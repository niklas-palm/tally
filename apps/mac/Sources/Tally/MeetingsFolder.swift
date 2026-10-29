import Foundation

/// A copy of the owner's meetings in a folder they choose, for agents and tools on their Mac: each
/// meeting's summary and transcript as Markdown, and everything as JSON, kept up to date from Tally (one way: the
/// folder is a copy). An agent needs no sign-in to read files, and the README tells it what's where.
///
/// Only what Tally made is ever changed: a meeting's folder is known by the meeting.json inside it. Every file is
/// written whole (to a temporary file, then moved into place), so nothing is read half-written.
struct MeetingsFolder {
    let root: URL
    /// What this Mac wrote, and from what, for this account: Application Support, not the folder.
    let store: URL

    /// What's remembered of each meeting: the stamp it was fetched at, the folder it's in, what it was rendered with.
    struct State: Codable {
        var meetings: [String: Entry] = [:]
        struct Entry: Codable { var stamp: String; var directory: String; var rendered: String }
    }

    /// Everything one meeting's files are made of.
    struct Source {
        let meeting: API.Meeting
        let results: Results
        let summary: Results.Summary
        let edited: Bool  // the owner's copy, not the agent's
        let names: Names
        let folders: [String]  // the Tally folders it's in, by path
        var files: [String]  // names in its files/ folder
    }

    init(root: URL, account: String) {
        self.root = root
        store = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Tally/meetings-folder/\(account)")
    }

    // MARK: - What's remembered

    func state() -> State {
        (try? JSONDecoder().decode(State.self, from: Data(contentsOf: store.appending(path: "state.json")))) ?? State()
    }

    func save(_ state: State) throws {
        try FileManager.default.createDirectory(at: store, withIntermediateDirectories: true)
        try JSONEncoder().encode(state).write(to: store.appending(path: "state.json"), options: .atomic)
    }

    /// A meeting as the API gave it (its results and the owner's copy), kept so a rename in People re-renders it
    /// without fetching it again.
    func cache(_ detail: Data, of meeting: String) throws {
        try FileManager.default.createDirectory(at: store.appending(path: "cache"), withIntermediateDirectories: true)
        try detail.write(to: store.appending(path: "cache/\(meeting).json"), options: .atomic)
    }

    func cached(_ meeting: String) -> API.Detail? {
        (try? Data(contentsOf: store.appending(path: "cache/\(meeting).json"))).flatMap { try? API.decoder.decode(API.Detail.self, from: $0) }
    }

    func forget(_ meeting: String) { try? FileManager.default.removeItem(at: store.appending(path: "cache/\(meeting).json")) }

    // MARK: - The folder

    /// "2026-10-27 1306 Budget review": by when, then what, so a listing reads in order.
    static func directory(for meeting: API.Meeting, title: String) -> String {
        let when = DateFormatter()
        when.locale = Locale(identifier: "en_US_POSIX")
        when.dateFormat = "yyyy-MM-dd HHmm"
        // "Budget: Q3 / plan" reads "Budget - Q3 - plan": no character a path, or Finder, gives meaning to
        let safe = title.replacing(": ", with: " - ").replacing(" / ", with: " - ")
            .map { "/:\\\n\r\t".contains($0) ? "-" : $0 }.reduce(into: "") { $0.append($1) }
            .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        let name = "\(when.string(from: meeting.startedAt ?? ULID.date(meeting.meetingId))) \(safe.isEmpty ? "Meeting" : String(safe.prefix(80)))"
        return name.trimmingCharacters(in: .whitespaces)
    }

    func url(_ directory: String) -> URL { root.appending(path: directory, directoryHint: .isDirectory) }

    /// Whether a name is another's: another meeting's (the same minute and title) or a folder of the owner's own.
    func taken(_ directory: String, by meeting: String) -> Bool {
        !holds(directory, meeting: meeting) && (try? FileManager.default.contentsOfDirectory(atPath: url(directory).path))?.isEmpty == false
    }

    /// Whether a folder is the copy of this meeting: Tally only touches its own.
    func holds(_ directory: String, meeting: String) -> Bool {
        guard let data = try? Data(contentsOf: url(directory).appending(path: "meeting.json")),
              let found = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
        return found["id"] as? String == meeting
    }

    /// A meeting's folder where its name says, moved there from its old name when the title changed.
    func place(_ meeting: String, from previous: String?, to directory: String) throws {
        let target = url(directory)
        if let previous, previous != directory, holds(previous, meeting: meeting), !FileManager.default.fileExists(atPath: target.path) {
            try FileManager.default.moveItem(at: url(previous), to: target)
        }
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
    }

    /// The names in a meeting's files/ folder, as they are.
    func present(in directory: String) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: url(directory).appending(path: "files").path)) ?? []).sorted()
    }

    /// Writes a meeting's three files where they differ from what's there.
    func write(_ source: Source, to directory: String) throws {
        let target = url(directory)
        let files = ["summary.md": Data(Self.summaryPage(source).utf8), "transcript.md": Data(Self.transcriptPage(source).utf8),
                     "meeting.json": try Self.encoder.encode(Self.record(source))]
        for (name, data) in files where (try? Data(contentsOf: target.appending(path: name))) != data {
            try data.write(to: target.appending(path: name), options: .atomic)
        }
    }

    /// A meeting's files/ folder as the API lists them: the missing downloaded, the deleted gone.
    func files(_ listed: [API.File], in directory: String) async throws -> [String] {
        let folder = url(directory).appending(path: "files", directoryHint: .isDirectory)
        // Names as the Mac's disk takes them, without regard to case: Report.pdf and report.pdf are one name to APFS
        var names: [String: API.File] = [:]
        for file in listed {  // two files of one name: the later one keeps its id, so neither is lost
            let taken = names.keys.contains { $0.lowercased() == file.name.lowercased() }
            names[taken ? "\(file.fileId)-\(file.name)" : file.name] = file
        }
        let here = present(in: directory)
        let wanted = Set(names.keys.map { $0.lowercased() })
        for gone in here where !wanted.contains(gone.lowercased()) { try? FileManager.default.removeItem(at: folder.appending(path: gone)) }
        if !names.isEmpty { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        // A name that's there is that file only when its time is the file's, set as it was downloaded: one replaced by
        // another of its name (an updated deck) is fetched again
        func current(_ name: String, _ file: API.File) -> Bool {
            guard let there = here.first(where: { $0.lowercased() == name.lowercased() }),
                  let time = try? FileManager.default.attributesOfItem(atPath: folder.appending(path: there).path)[.modificationDate] as? Date
            else { return false }
            return time == file.addedAt
        }
        for (name, file) in names where !current(name, file) {
            let (downloaded, response) = try await URLSession.shared.download(from: file.url)
            defer { try? FileManager.default.removeItem(at: downloaded) }  // the download's own copy, whether it was moved or not
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }  // the meeting's tried again next pass
            let target = folder.appending(path: name)
            try? FileManager.default.removeItem(at: target)  // the one it replaces
            try FileManager.default.moveItem(at: downloaded, to: target)
            try? FileManager.default.setAttributes([.modificationDate: file.addedAt], ofItemAtPath: target.path)
        }
        return names.keys.sorted()
    }

    func remove(_ directory: String, meeting: String) {
        if holds(directory, meeting: meeting) { try? FileManager.default.removeItem(at: url(directory)) }
    }

    /// index.json, every meeting newest first, and the README: rewritten when they'd say something new.
    func writeIndex(_ entries: [IndexEntry], account: String?) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let index = try Self.encoder.encode(Index(account: account, meetings: entries.sorted { $0.startedAt ?? "" > $1.startedAt ?? "" }))
        for (name, data) in ["index.json": index, "README.md": Data(Self.readme.utf8)]
        where (try? Data(contentsOf: root.appending(path: name))) != data {
            try data.write(to: root.appending(path: name), options: .atomic)
        }
    }

    // MARK: - What's written

    struct Index: Encodable {
        let account: String?
        let meetings: [IndexEntry]
    }

    struct IndexEntry: Encodable {
        let id: String
        let title: String
        let startedAt: String?
        let durationMinutes: Int
        let folders: [String]
        let people: [String]
        let actionItemsOpen: Int
        let path: String
    }

    static func entry(_ source: Source, directory: String) -> IndexEntry {
        IndexEntry(id: source.meeting.meetingId, title: source.summary.title, startedAt: source.meeting.startedAt.map(iso),
                   durationMinutes: Int((source.results.duration / 60).rounded()), folders: source.folders,
                   people: source.names.participants.map(source.names.name),
                   actionItemsOpen: source.summary.actionItems.filter { $0.done != true }.count, path: directory)
    }

    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.keyEncodingStrategy = .convertToSnakeCase
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }()

    /// A time as agents read it: ISO 8601, with the Mac's offset.
    static func iso(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = .current
        return formatter.string(from: date)
    }

    struct Record: Encodable {
        let id: String
        let title: String
        let startedAt: String?
        let endedAt: String?
        let durationMinutes: Int
        let language: String?
        let folders: [String]
        let editedByYou: Bool
        let overview: String
        let keyPoints: [String]
        let decisions: [Said]
        let actionItems: [ActionItem]
        let openQuestions: [Said]
        let topics: [Topic]
        let people: [Person]
        let files: [String]
        let backgroundVoices: [String]  // speakers the owner set aside as not in the meeting: left out of people and the transcript
        let summaryFile = "summary.md"
        let transcriptFile = "transcript.md"
        let openInTally: String

        struct Said: Encodable { let text: String; let by: [String] }
        struct ActionItem: Encodable { let text: String; let owner: String?; let due: String?; let done: Bool }
        struct Topic: Encodable { let title: String; let start: String; let end: String; let notes: [String]; let outcome: String? }
        struct Person: Encodable {
            let speaker: String
            let name: String
            let personId: String?
            let role: String?
            let company: String?
            let isYou: Bool
            let confirmed: Bool
            let talkMinutes: Double
            let contribution: String?
        }
    }

    static func record(_ source: Source) -> Record {
        let (s, names, results) = (source.summary, source.names, source.results)
        let attendees = Dictionary(s.attendees.map { ($0.speaker, $0) }, uniquingKeysWith: { a, _ in a })
        return Record(
            id: source.meeting.meetingId, title: s.title, startedAt: source.meeting.startedAt.map(iso), endedAt: source.meeting.endedAt.map(iso),
            durationMinutes: Int((results.duration / 60).rounded()), language: results.language, folders: source.folders, editedByYou: source.edited,
            overview: names.render(s.overview), keyPoints: s.summary.map(names.render),
            decisions: s.decisions.map { .init(text: names.render($0.text), by: $0.by.map(names.name)) },
            actionItems: s.actionItems.map { .init(text: names.render($0.text), owner: $0.owner.map(names.name), due: $0.due, done: $0.done == true) },
            openQuestions: s.openQuestions.map { .init(text: names.render($0.text), by: $0.raisedBy.map(names.name)) },
            topics: s.topics.map { .init(title: names.render($0.title), start: clock($0.start), end: clock($0.end), notes: $0.notes.map(names.render),
                                         outcome: $0.outcome.map(names.render)) },
            people: names.participants.map { speaker in
                let person = names.person(speaker)
                return .init(speaker: speaker, name: names.name(speaker), personId: person?.personId, role: person?.role ?? attendees[speaker].map { names.render($0.role) },
                             company: person?.company, isYou: person?.isOwner == true, confirmed: names.review(speaker) == .confirmed,
                             talkMinutes: ((results.speakers[speaker]?.talkTime ?? 0) / 6).rounded() / 10,
                             contribution: attendees[speaker].map { names.render($0.contribution) })
            },
            files: source.files.map { "files/\($0)" }, backgroundVoices: results.speakerOrder.filter { names.review($0) == .background }, openInTally: "tally://meeting/\(source.meeting.meetingId)")
    }

    /// summary.md: when and who, then the summary as Copy Summary gives it.
    static func summaryPage(_ source: Source) -> String {
        let when = source.meeting.startedAt.map { longDate($0) } ?? ""
        let facts = ["**When:** \(when), \(duration(source.results.duration))",
                     source.folders.isEmpty ? nil : "**Folders:** " + source.folders.joined(separator: ", "),
                     source.edited ? "**Edited by you** in Tally" : nil].compactMap { $0 }
        let summary = Markdown.summary(source.summary, source.names)
        let title = summary.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        return ([String(title[0]), ""] + facts.map { $0 + "  " } + [String(title.dropFirst().joined(separator: "\n"))]).joined(separator: "\n") + "\n"
    }

    static func transcriptPage(_ source: Source) -> String {
        "# \(source.summary.title): transcript\n\n" + Markdown.transcript(source.results, source.names) + "\n"
    }

    // MARK: - The README

    static let readme = """
    # Tally meetings

    This folder is a copy of the owner's meetings, recorded and summarised by Tally, a meeting recorder for the Mac. Tally
    keeps it up to date while it runs on this Mac: a new meeting appears once it's summarised, and edits made in Tally
    (the summary, who spoke, folders, files) reach it within a minute or so.

    **It's a copy, one way.** Don't edit these files: Tally overwrites them at its next sync, and deletes a meeting's
    folder when the meeting is deleted in Tally. To change a meeting, change it in Tally. Files you add here yourself,
    outside the meetings' folders, are left alone.

    ## Layout

    ```
    README.md                         this file
    index.json                        every meeting, newest first: start here
    <date> <time> <title>/            one folder per meeting, e.g. "2026-10-27 1306 Budget review"
      meeting.json                    everything about the meeting, structured (below)
      summary.md                      the summary to read: overview, points, decisions, action items, topics
      transcript.md                   who said what, paragraph by paragraph, with times from the start
      files/                          files the owner kept with the meeting (slides, screenshots, documents)
    ```

    ## Finding things

    - **Go by id, not by path.** A meeting's `id` never changes. Its folder's name does, when its title is changed in
      Tally: `index.json` maps each `id` to its current `path`.
    - **People are the same person across meetings** when they share a `person_id`. `name` is how the owner calls
      them now. `is_you` marks the owner. `confirmed` is true when the owner has confirmed that voice.
    - **Times:** `started_at` and `ended_at` are ISO 8601 with the Mac's UTC offset. Topic `start` and `end` are times
      from the meeting's start (`mm:ss`, or `h:mm:ss`). `due` is a date, `YYYY-MM-DD`.
    - **A field with nothing in it is left out** (an action item without an owner or a due date, a person without a
      role): read a missing field as null.
    - **Folders** are the owner's Tally folders, by path (`Customers › Acme`). A meeting can be in several, or none.
    - **Language:** `language` is the language spoken (`en`, `sv`, ...). Summaries are in that language.

    ## index.json

    ```json
    {"account": "Ada Lovelace",
     "meetings": [{"id": "01M5W8...", "title": "...", "started_at": "2026-10-27T13:06:00+01:00", "duration_minutes": 30,
                   "folders": ["Customers › Acme"], "people": ["Ada Lovelace", "Anna Berg"], "action_items_open": 2,
                   "path": "2026-10-27 1306 ..."}]}
    ```

    ## meeting.json

    ```json
    {"id": "01M5W8...", "title": "...", "started_at": "...", "ended_at": "...", "duration_minutes": 30, "language": "en",
     "folders": ["Customers › Acme"], "edited_by_you": false,
     "overview": "What the meeting was about, in a few sentences.",
     "key_points": ["..."],
     "decisions": [{"text": "...", "by": ["Anna Berg"]}],
     "action_items": [{"text": "...", "owner": "Anna Berg", "due": "2026-11-03", "done": false}],
     "open_questions": [{"text": "...", "by": ["..."]}],
     "topics": [{"title": "...", "start": "0:00", "end": "12:30", "notes": ["..."], "outcome": "..."}],
     "people": [{"speaker": "S1", "name": "Anna Berg", "person_id": "p_...", "role": "Head of finance", "company": "Acme",
                 "is_you": false, "confirmed": true, "talk_minutes": 12.5, "contribution": "What they brought."}],
     "files": ["files/slides.pdf"], "background_voices": ["S4"],
     "summary_file": "summary.md", "transcript_file": "transcript.md",
     "open_in_tally": "tally://meeting/01M5W8..."}
    ```

    `edited_by_you` is true when the owner edited the summary in Tally; this copy is then theirs. A speaker nobody has
    named yet is "Speaker 3", with no `person_id`. `background_voices` are speakers the owner marked as not in the
    meeting (someone talking nearby): they're not among `people`, and their lines aren't in transcript.md. `open_in_tally` opens the meeting in the Tally app.

    ## Examples

    ```sh
    # The ten newest meetings
    jq -r '.meetings[:10][] | "\\(.started_at)  \\(.title)"' index.json
    # Open action items, with their owner and meeting
    jq -r '.title as $t | .action_items[] | select(.done | not) | "\\(.owner // "no owner"): \\(.text) (\\($t))"' */meeting.json
    # Meetings with someone
    jq -r '.meetings[] | select(.people | index("Anna Berg")) | .path' index.json
    # Every mention of a word, in the transcripts
    grep -il "budget" */transcript.md
    ```
    """
}

/// Markdown for a meeting, with names: what Copy Summary and Copy Transcript put on the clipboard, and what the
/// meetings folder writes. One source, so the two never differ.
enum Markdown {
    /// The whole summary, everything the page shows, with who took part under the title: each by name, with what
    /// they do when it's known. A background voice wasn't in the meeting, so isn't among them.
    static func summary(_ s: Results.Summary, _ names: Names) -> String {
        let present = names.participants.map { speaker in names.person(speaker)?.detail.map { "\(names.name(speaker)) (\($0))" } ?? names.name(speaker) }
        var lines = ["# \(s.title)", "", "**Participants:** " + present.joined(separator: ", "), "", names.render(s.overview)]
        func section(_ title: String, _ rows: [String]) {
            if !rows.isEmpty { lines += ["", "## \(title)"] + rows.map { "- \($0)" } }
        }
        func who(_ speakers: [String]) -> String {
            speakers.isEmpty ? "" : " (\(speakers.map(names.name).joined(separator: ", ")))"
        }
        section("Summary", s.summary.map(names.render))
        section("Decisions", s.decisions.map { names.render($0.text) + who($0.by) })
        section("Action items", s.actionItems.map { item in
            (item.done == true ? "[x] " : "[ ] ") + (item.owner.map { "\(names.name($0)): " } ?? "") + names.render(item.text)
                + (item.due.map { " (by \($0))" } ?? "")
        })
        section("Open questions", s.openQuestions.map { names.render($0.text) + who($0.raisedBy) })
        if !s.topics.isEmpty {
            lines += ["", "## Topics"]
            for topic in s.topics {
                lines += ["", "### \(names.render(topic.title)) (\(clock(topic.start))–\(clock(topic.end)))"] + topic.notes.map { "- \(names.render($0))" }
                if let outcome = topic.outcome { lines += ["", "**Outcome:** \(names.render(outcome))"] }
            }
        }
        section("Who said what", s.attendees.map { "**\(names.name($0.speaker))**, \(names.role($0)). \(names.render($0.contribution))" })
        return lines.joined(separator: "\n")
    }

    /// The transcript as the page shows it: each paragraph under its speaker's name and time.
    static func transcript(_ results: Results, _ names: Names) -> String {
        results.paragraphs.filter { names.review($0.speaker) != .background }.map { "**\(names.name($0.speaker))** (\(clock($0.start)))\n\($0.text)" }.joined(separator: "\n\n")
    }
}
