import Foundation

/// A recording on this Mac, from the moment it starts until the API has its summary. Each lives in its own folder
/// under Application Support:
///
///     recordings/<id>/local.json      this struct
///                     recording.caf   while recording (survives a crash)
///                     audio.m4a       from stop until the summary is ready, then the folder goes: S3 has it
///
/// After upload the API owns the meeting; this only keeps the audio so a failed meeting can be sent again.
struct Recording: Codable, Identifiable, Equatable {
    enum State: String, Codable {
        case recording
        case saved      // waiting to be sent: not signed in, offline, or the last upload failed
        case uploading
        case uploaded   // the audio is up; the API hasn't heard yet that it's done
        case sent       // the API has it
        case broken     // the encode failed; Try Again encodes the CAF again, if it's there
    }

    let id: String
    let startedAt: Date
    var endedAt: Date?
    var state: State
    var error: String?
    var auto: Bool?  // started by itself, in auto mode

    static let shortest: TimeInterval = 120  // an auto-started recording shorter than this is discarded, not sent

    /// Started by itself and too short to be a meeting: a lobby, a mic test, dictation.
    var tooShortToKeep: Bool {
        auto == true && (endedAt ?? startedAt).timeIntervalSince(startedAt) < Self.shortest
    }

    var folder: URL { Self.root.appending(path: id, directoryHint: .isDirectory) }
    var caf: URL { folder.appending(path: "recording.caf") }
    var audio: URL { folder.appending(path: "audio.m4a") }

    static let root = URL.applicationSupportDirectory.appending(path: "Tally/recordings", directoryHint: .isDirectory)

    static func loadAll() -> [Recording] {
        let folders = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return folders
            .compactMap { try? decoder.decode(Recording.self, from: Data(contentsOf: $0.appending(path: "local.json"))) }
            .sorted { $0.startedAt > $1.startedAt }
    }

    func save() {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? Self.encoder.encode(self).write(to: folder.appending(path: "local.json"), options: .atomic)
    }

    func remove() { try? FileManager.default.removeItem(at: folder) }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }()
}
