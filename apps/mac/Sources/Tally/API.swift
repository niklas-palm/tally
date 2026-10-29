import Foundation

/// Tally's API (docs/api.md), with the signed-in person's access token on every call: everything it returns is theirs.
struct API {
    let base: URL
    let token: String

    struct Meeting: Decodable, Equatable {
        let meetingId: String
        let state: String  // uploading, queued, processing, ready, failed
        var title: String?
        let startedAt: Date?
        let endedAt: Date?
        let duration: Double?
        let error: String?
        var people: [String]?  // who spoke: their person ids
        var folderIds: [String]?  // the owner's folders it's in, any number; Meetings has every meeting
        var updatedAt: Date? = nil  // the job's last change
        var editedAt: Date? = nil  // the owner's last change: with updatedAt, when the meetings folder fetches it again
        var rewriting: Date? = nil  // a rewrite of the summary without background voices asked for, not done yet
        var rewriteError: String? = nil  // why the last one didn't happen
        /// On its way, for the 30 minutes the API gives it: a job that died without saying so doesn't hold it for good.
        var isRewriting: Bool { rewriting.map { Date().timeIntervalSince($0) < 30 * 60 } == true }
    }

    /// One of the owner's folders; `parent` none is at the top.
    struct Folder: Codable, Equatable, Identifiable {
        let folderId: String
        var name: String
        var parent: String?
        var id: String { folderId }
    }

    /// A meeting search found: where it matched (title, summary, people, transcript), a snippet, and for the
    /// transcript the time.
    struct SearchHit: Decodable, Identifiable {
        let meetingId: String
        let title: String?
        let startedAt: Date?
        let `where`: String
        let snippet: String
        let at: Double?
        var id: String { meetingId }
    }

    struct Detail: Decodable {
        let results: Results?
        let notes: Notes?

        struct Notes: Decodable { let summary: Results.Summary? }  // the owner's copy
    }

    struct Person: Decodable, Identifiable, Equatable {
        let personId: String
        let name: String
        let aliases: [String]
        let role: String?
        let company: String?
        let isOwner: Bool
        let voiceprints: [Voiceprint]
        var id: String { personId }
        /// The owner has confirmed this voice at least once, so matches to it are trusted.
        var confirmed: Bool { voiceprints.contains { $0.source == "user" } }
        /// "Head of finance, Acme", or nil when neither is known.
        var detail: String? {
            let detail = [role, company].compactMap { $0 }.joined(separator: ", ")
            return detail.isEmpty ? nil : detail
        }
    }

    struct Voiceprint: Decodable, Equatable {
        let source: String
    }

    /// A file the owner added to a meeting, with a link to download it, valid for an hour.
    struct File: Decodable, Identifiable, Equatable {
        let fileId: String
        let name: String
        let size: Int
        let addedAt: Date
        let url: URL
        var id: String { fileId }
    }

    struct Upload: Decodable {
        let uploadUrl: URL
        let uploadHeaders: [String: String]
    }

    func meetings() async throws -> [Meeting] { try await list("meetings", at: "meetings") }

    func detail(_ id: String) async throws -> Detail { try decode(Detail.self, try await detailData(id)) }

    /// The meeting as the API sends it, for the meetings folder to keep and render again.
    func detailData(_ id: String) async throws -> Data { try await send("GET", "meetings/\(id)") }

    func people() async throws -> [Person] { try await list("people", at: "people") }

    /// The API has this meeting already (a send that got further than the app knew): nothing more to upload.
    struct AlreadySent: Error {}

    /// Registers a recording and uploads its audio, which starts the pipeline. Throws `AlreadySent` when it's sent.
    func upload(_ recording: Recording, appVersion: String) async throws {
        var body = ["started_at": recording.startedAt.ISO8601Format(), "app_version": appVersion]
        body["ended_at"] = recording.endedAt?.ISO8601Format()
        let upload: Upload
        do {
            upload = try decode(Upload.self, try await send("PUT", "meetings/\(recording.id)", body))
        } catch let error as Failure where error.code == 409 {
            throw AlreadySent()
        }
        try await put(recording.audio, upload)
    }

    /// The audio is up: the meeting is queued.
    func uploaded(_ id: String) async throws { _ = try await send("POST", "meetings/\(id)/uploaded") }

    func delete(meeting id: String) async throws { _ = try await send("DELETE", "meetings/\(id)") }

    /// Who a speaker is: `["person_id": ...]`, `["name": ..., "role": ..., "company": ...]` for someone new, `["unknown": true]`, or `["background": true]`.
    func assign(_ meeting: String, _ speaker: String, _ body: [String: Any]) async throws {
        _ = try await send("PUT", "meetings/\(meeting)/speakers/\(speaker)", body)
    }

    /// A crash report from this Mac, sent with the owner's say-so.
    func reportCrash(_ report: String, appVersion: String) async throws {
        _ = try await send("POST", "crashes", ["report": report, "app_version": appVersion])
    }

    /// The owner's copy of a meeting's summary, saved over their last; DELETE goes back to the agent's.
    func save(summary: Results.Summary, of meeting: String) async throws {
        let body = try JSONSerialization.jsonObject(with: Self.encoder.encode(summary))
        _ = try await send("PUT", "meetings/\(meeting)/summary", ["summary": body])
    }

    func rewriteSummary(of meeting: String, replace: Bool) async throws {
        _ = try await send("POST", "meetings/\(meeting)/summary/rewrite", ["replace": replace])
    }

    func restoreSummary(of meeting: String) async throws { _ = try await send("DELETE", "meetings/\(meeting)/summary") }

    func folders() async throws -> [Folder] { try await list("folders", at: "folders") }

    func save(_ folder: Folder) async throws {
        var body: [String: Any] = ["name": folder.name]
        body["parent"] = folder.parent ?? NSNull()
        _ = try await send("PUT", "folders/\(folder.folderId)", body)
    }

    func delete(folder id: String) async throws { _ = try await send("DELETE", "folders/\(id)") }

    /// A meeting into a folder (`inside`), or out of it.
    func file(_ meeting: String, in folder: String, _ inside: Bool) async throws {
        _ = try await send(inside ? "PUT" : "DELETE", "meetings/\(meeting)/folders/\(folder)")
    }

    func files(of meeting: String) async throws -> [File] { try await list("files", at: "meetings/\(meeting)/files") }

    /// A file from this Mac into a meeting: where to put it, then the upload, signed for exactly its size.
    func add(file: URL, type: String, to meeting: String) async throws {
        let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        let upload = try decode(Upload.self, try await send("POST", "meetings/\(meeting)/files",
                                                            ["name": file.lastPathComponent, "size": size, "type": type]))
        try await put(file, upload)
    }

    func delete(file: String, of meeting: String) async throws { _ = try await send("DELETE", "meetings/\(meeting)/files/\(file)") }

    func search(_ query: String) async throws -> [SearchHit] {
        try await list("results", at: "search", query: [URLQueryItem(name: "q", value: query)])
    }

    func update(person id: String, _ body: [String: Any]) async throws { _ = try await send("PATCH", "people/\(id)", body) }
    func delete(person id: String) async throws { _ = try await send("DELETE", "people/\(id)") }

    /// A GET whose answer is one list, under `key`.
    private func list<T: Decodable>(_ key: String, at path: String, query: [URLQueryItem] = []) async throws -> [T] {
        try decode([String: [T]].self, try await send("GET", path, query: query))[key] ?? []
    }

    /// A file up to where `upload` says, with the headers it was signed with.
    private func put(_ file: URL, _ upload: Upload) async throws {
        var request = URLRequest(url: upload.uploadUrl)
        request.httpMethod = "PUT"
        upload.uploadHeaders.forEach { request.setValue($1, forHTTPHeaderField: $0) }
        let (data, response) = try await URLSession.shared.upload(for: request, fromFile: file)
        try check(response, data)
    }

    private func send(_ method: String, _ path: String, _ body: [String: Any]? = nil, query: [URLQueryItem] = []) async throws -> Data {
        let url = base.appending(path: path)
        var request = URLRequest(url: query.isEmpty ? url : url.appending(queryItems: query))
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(Config.shared.version ?? "repository build", forHTTPHeaderField: "X-Tally-Version")  // which version each person runs: the admin dashboard shows it
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let data: Data, response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch is URLError {  // no answer at all, however it failed (no network, a captive Wi-Fi, DNS): offline
            throw Offline()
        }
        try check(response, data)
        return data
    }

    /// No network: the sync tries again by itself; something the owner just asked for has to be asked again.
    struct Offline: LocalizedError {
        var errorDescription: String? { "You're offline." }
    }

    /// An answer that wasn't a success, with what the API said about it.
    struct Failure: LocalizedError {
        let code: Int
        let message: String?
        var errorDescription: String? { reason ?? "Something went wrong on Tally's side. Try again in a moment." }

        /// What the API said, when it's something a person can act on: a refused sign-in, or a rule of its own.
        var reason: String? {
            if code == 401 { return "Your sign-in wasn't accepted. Sign out (Account, in Tally's menu in the menu bar), then sign in again." }
            guard let message, code < 500 else { return nil }
            return message.prefix(1).uppercased() + message.dropFirst() + "."
        }
    }

    private func check(_ response: URLResponse, _ data: Data) throws {
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else {
            throw Failure(code: code, message: (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String)
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        try Self.decoder.decode(type, from: data)
    }

    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        return encoder
    }()

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            guard let date = try? Date(text, strategy: .iso8601) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "not a date: \(text)"))
            }
            return date
        }
        return decoder
    }()
}
