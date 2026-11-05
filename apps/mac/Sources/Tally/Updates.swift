import Foundation

/// Updating Tally from inside it. The install page publishes `version.json` next to `install.sh`, and
/// a build from it knows its own version (`tally.json`). When they differ, the menu offers Update, which runs the
/// same installer the install command does: it builds the new version, quits Tally, swaps the app and opens it.
/// A build from the repository has no version, so it's never offered one.
enum Updates {
    static let log = FileManager.default.homeDirectoryForCurrentUser.appending(path: "Library/Logs/Tally/install.log")

    /// The version on the install page, when it isn't this one.
    static func available(site: URL, mine: String) async -> String? {
        var request = URLRequest(url: site.appending(path: "version.json"), cachePolicy: .reloadIgnoringLocalCacheData)
        request.timeoutInterval = 20
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let latest = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["version"] as? String
        else { return nil }
        return newer(latest, than: mine)
    }

    /// Any version the install page serves that isn't this one is the one to have: it only ever serves the latest.
    static func newer(_ latest: String, than mine: String) -> String? { latest.isEmpty || latest == mine ? nil : latest }

    /// The installer, fetched first (so one that can't be reached is said, where `curl | bash` would run nothing and
    /// succeed), then run as the install command runs it, and its exit status. Its output goes nowhere: it outlives
    /// Tally, which it quits, and writing into a pipe Tally had held would stop it before it opened the new Tally. Its
    /// log says how it went.
    static func install(from site: URL) async throws -> Int32 {
        let (script, response) = try await URLSession.shared.data(from: site.appending(path: "install.sh"))
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        let file = FileManager.default.temporaryDirectory.appending(path: "tally-install-\(UUID().uuidString).sh")
        try script.write(to: file)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [file.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        return try await withCheckedThrowingContinuation { done in
            process.terminationHandler = {
                try? FileManager.default.removeItem(at: file)
                done.resume(returning: $0.terminationStatus)
            }
            do { try process.run() } catch { done.resume(throwing: error) }
        }
    }

    /// Why the last install failed, as the installer wrote it ("FAILED: Tally needs Swift 6.2 or later…"): only
    /// from the last run in the log (each starts with "--- "), so an old failure isn't this one's.
    static func lastFailure(in text: String) -> String? {
        let lines = text.split(separator: "\n")
        let run = lines.lastIndex { $0.hasPrefix("--- ") }.map { lines[$0...] } ?? lines[...]
        return run.last { $0.hasPrefix("FAILED: ") }.map { String($0.dropFirst("FAILED: ".count)) }
    }
}
