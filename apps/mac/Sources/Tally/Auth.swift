import AppKit
import AuthenticationServices
import CryptoKit
import Foundation

/// Signing in to the OIDC provider, once: authorization code with PKCE in the system's web sign-in sheet, which
/// comes back at once when the browser is already signed in there. The refresh token is kept in the keychain and
/// renews the short-lived access tokens for as long as the provider's session lasts.
@MainActor @Observable final class Auth: NSObject {
    enum State: Equatable { case signedOut, signingIn, signedIn }

    private(set) var state: State = .signedOut
    private(set) var expired = false  // signed out because the session ended, not by choice
    private(set) var account: String?  // who is signed in, as their token names them: known once a token has come
    private(set) var subject: String?  // their id with the provider: what the meetings folder setting belongs to
    private var refreshToken: String?
    private var accessToken: (value: String, expires: Date)?
    private var refreshing: Task<String?, Never>?  // one renewal at a time: every caller awaits the same one
    private var session = 0  // counts sign-outs: an answer that comes back after one is dropped
    private var sheet: ASWebAuthenticationSession?  // held while the sheet is up, or it's cancelled
    private var anchor: NSWindow?
    private let config = Config.shared

    override init() {
        super.init()
        refreshToken = Keychain.load("refresh-token").map { String(decoding: $0, as: UTF8.self) }
        if refreshToken != nil { state = .signedIn }
    }

    func signIn() async throws {
        guard state != .signingIn else { return }
        state = .signingIn
        defer { if state == .signingIn { state = .signedOut } }
        let verifier = Self.randomURLSafe(32)
        let nonce = Self.randomURLSafe(16)
        var components = URLComponents(url: config.authorization, resolvingAgainstBaseURL: false)!
        components.queryItems = [
            .init(name: "client_id", value: config.clientId), .init(name: "response_type", value: "code"),
            .init(name: "redirect_uri", value: "tally://callback"), .init(name: "scope", value: "openid"),
            .init(name: "code_challenge", value: Self.challenge(verifier)), .init(name: "code_challenge_method", value: "S256"),
            .init(name: "state", value: nonce),
        ]
        let callback = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
            // The completion handler runs on a thread of the system's choosing, not the main one.
            let session = ASWebAuthenticationSession(url: components.url!, callback: .customScheme("tally")) { [weak self] url, error in
                Task { @MainActor in self?.sheet = nil }
                if let url { continuation.resume(returning: url) } else { continuation.resume(throwing: error ?? AppError("Sign-in was cancelled.")) }
            }
            session.presentationContextProvider = self
            self.sheet = session
            if !session.start() {
                self.sheet = nil
                continuation.resume(throwing: AppError("The sign-in sheet couldn't open. Try again."))
            }
        }
        let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard items.first(where: { $0.name == "state" })?.value == nonce,
              let code = items.first(where: { $0.name == "code" })?.value
        else { throw AppError("Sign-in didn't complete. Try again.") }
        do {
            try await requestTokens(["grant_type": "authorization_code", "code": code, "redirect_uri": "tally://callback", "code_verifier": verifier])
        } catch is SessionEnded {  // the code expired or was used already
            throw AppError("Sign-in didn't complete. Try again.")
        }
        expired = false
        state = .signedIn
    }

    func signOut() {
        session += 1
        refreshing = nil
        refreshToken = nil
        accessToken = nil
        account = nil
        subject = nil
        Keychain.delete("refresh-token")
        state = .signedOut
    }

    /// A valid access token, renewed when it's about to expire. Nil when signed out, offline, or when the session
    /// has ended (then the state turns to signed out).
    func token() async -> String? {
        if let accessToken, accessToken.expires > Date().addingTimeInterval(30) { return accessToken.value }
        if let refreshing { return await refreshing.value }
        guard let refreshToken else { return nil }
        let task = Task { () -> String? in
            do {
                try await requestTokens(["grant_type": "refresh_token", "refresh_token": refreshToken])
                return accessToken?.value
            } catch is SessionEnded {
                guard self.refreshToken == refreshToken else { return nil }  // only the session that ended
                signOut()
                expired = true
                return nil
            } catch {
                return nil  // offline: try again later
            }
        }
        refreshing = task
        defer { refreshing = nil }
        return await task.value
    }

    private struct SessionEnded: Error {}

    private func requestTokens(_ form: [String: String]) async throws {
        let session = session
        var request = URLRequest(url: config.token)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let fields = form.merging(["client_id": config.clientId]) { a, _ in a }
        request.httpBody = Data(fields.map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: .alphanumerics)!)" }
            .joined(separator: "&").utf8)
        let (data, response) = try await URLSession.shared.data(for: request)
        let json = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
        guard (response as? HTTPURLResponse)?.statusCode == 200, let access = json["access_token"] as? String else {
            if json["error"] as? String == "invalid_grant" { throw SessionEnded() }
            throw AppError("Couldn't sign in to \(config.signInName). Try again in a moment.")
        }
        guard session == self.session else { return }  // signed out while this was on its way: keep nothing
        accessToken = (access, Date().addingTimeInterval(json["expires_in"] as? Double ?? 300))
        account = Self.name(in: access)
        subject = Self.claims(in: access)?["sub"] as? String
        if let refresh = json["refresh_token"] as? String {
            refreshToken = refresh
            Keychain.save(Data(refresh.utf8), as: "refresh-token")
        }
    }

    /// The person a token is for: its name, or its email. Read for showing only; the API checks the token itself.
    nonisolated static func name(in token: String) -> String? {
        guard let claims = claims(in: token) else { return nil }
        return (claims["name"] ?? claims["email"] ?? claims["preferred_username"] ?? claims["username"]) as? String  // username: Cognito's
    }

    nonisolated static func claims(in token: String) -> [String: Any]? {
        let parts = token.split(separator: ".")
        guard parts.count == 3 else { return nil }
        var payload = parts[1].replacing("-", with: "+").replacing("_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: String(payload)) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    nonisolated static func challenge(_ verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    private static func randomURLSafe(_ bytes: Int) -> String {
        base64URL(Data((0..<bytes).map { _ in UInt8.random(in: 0...255) }))
    }

    nonisolated private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacing("+", with: "-").replacing("/", with: "_").replacing("=", with: "")
    }
}

extension Auth: ASWebAuthenticationPresentationContextProviding {
    /// The sheet's window: Tally's own when it's open; a menu bar app may have none, so then a small one of its own.
    nonisolated func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            if let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeKey }) { return window }
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1, height: 1), styleMask: [], backing: .buffered, defer: false)
            window.center()
            anchor = window
            return window
        }
    }
}

/// An error with a message a person can read.
struct AppError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
