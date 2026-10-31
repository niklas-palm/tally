import Foundation

/// `results/meeting.json` as the API returns it (docs/data.md): the summary, the speakers and the transcript.
struct Results: Decodable {
    let language: String?
    let duration: Double
    let summary: Summary
    let speakers: [String: Speaker]
    let segments: [Segment]
    let summaryWithout: [String]?  // the background voices the summary was last written without

    /// The agent's summary, or the owner's copy of it: the same shape, edited in place.
    struct Summary: Codable, Equatable {
        var title: String
        var overview: String
        var summary: [String]
        var decisions: [Decision]
        var actionItems: [ActionItem]
        var openQuestions: [OpenQuestion]
        var topics: [Topic]
        var attendees: [Attendee]

        /// What's sent: points the owner emptied are gone. While they're on the page they stay, as places to type.
        var cleaned: Summary {
            var found = self
            found.summary = summary.filter { !$0.isBlank }
            found.decisions = decisions.filter { !$0.text.isBlank }
            found.actionItems = actionItems.filter { !$0.text.isBlank }
            found.openQuestions = openQuestions.filter { !$0.text.isBlank }
            found.topics = topics.map { var t = $0; t.notes = t.notes.filter { !$0.isBlank }; return t }
                .filter { !$0.title.isBlank || !$0.notes.isEmpty || $0.outcome?.isBlank == false }  // one emptied of everything
            return found
        }
    }

    struct Decision: Codable, Equatable { var text: String; var by: [String] }
    struct ActionItem: Codable, Equatable { var owner: String?; var text: String; var due: String?; var done: Bool? }
    struct OpenQuestion: Codable, Equatable { var text: String; var raisedBy: [String] }
    struct Topic: Codable, Equatable { var title: String; var start: Double; var end: Double; var notes: [String]; var outcome: String? }
    struct Attendee: Codable, Equatable { var speaker: String; var role: String; var contribution: String }

    struct Speaker: Decodable {
        let talkTime: Double
        let clipUrls: [URL]?
        let identity: Identity?
        let candidates: [Candidate]?  // the owner's people whose voices come closest, closest first
    }

    /// A voice that sounds like this speaker's: cosine similarity, 0.65 or more is very close (measured: the same person scored 0.81 to 0.96, different people at most 0.49).
    struct Candidate: Decodable, Identifiable {
        let personId: String
        let score: Double
        var id: String { personId }
        var closeness: String? { score >= 0.65 ? "Very close" : score >= 0.5 ? "Close" : score >= 0.35 ? "A little alike" : nil }
    }

    struct Identity: Decodable {
        let name: String?
        let personId: String?
        let by: String?  // agent or owner
        let unknown: Bool?
        let background: Bool?  // the owner said it wasn't in the meeting: a voice nearby
        let shared: Bool?  // named from the shared voices: a colleague's naming, the owner's to confirm
    }

    struct Segment: Decodable { let start: Double; let end: Double; let speaker: String; let text: String }

    typealias Paragraph = (speaker: String, start: Double, text: String)

    /// Consecutive lines of one speaker as one paragraph: how the transcript reads.
    var paragraphs: [Paragraph] {
        segments.reduce(into: []) { paragraphs, segment in
            if let last = paragraphs.last, last.speaker == segment.speaker {
                paragraphs[paragraphs.count - 1].text += " " + segment.text
            } else {
                paragraphs.append((segment.speaker, segment.start, segment.text))
            }
        }
    }

    /// Speaker ids in the order they first speak.
    var speakerOrder: [String] {
        let first = segments.reduce(into: [String: Double]()) { first, s in first[s.speaker] = first[s.speaker] ?? s.start }
        return speakers.keys.sorted { (first[$0] ?? .infinity, $0) < (first[$1] ?? .infinity, $1) }
    }
}

/// What to call each speaker in a meeting, with the people as they are now: a person renamed since the meeting
/// shows under their new name, and the summary's `{S2}` tokens are filled in the same way.
struct Names {
    let results: Results
    let people: [String: API.Person]

    enum Review: Equatable {
        case confirmed      // the owner said so, or the voice is one they've confirmed before
        case suggested      // the agent's call: "Is this Alex?"
        case unknown        // nobody knows yet: "Who's this?"
        case notKnown       // the owner said it's nobody they know
        case background     // the owner said it wasn't in the meeting at all: set aside
    }

    func name(_ speaker: String) -> String {
        let identity = results.speakers[speaker]?.identity
        if let id = identity?.personId { return people[id]?.name ?? "Speaker \(speaker.dropFirst())" }  // deleted: unknown again
        return identity?.name ?? "Speaker \(speaker.dropFirst())"
    }

    /// What an attendee does: the role and company the owner keeps for them in People, when there are any, so a
    /// correction there reaches every meeting; otherwise how the meeting described them, with speakers' names in.
    func role(_ attendee: Results.Attendee) -> String { person(attendee.speaker)?.detail ?? render(attendee.role) }

    func person(_ speaker: String) -> API.Person? {
        results.speakers[speaker]?.identity?.personId.flatMap { people[$0] }
    }

    /// Someone who said less than this isn't asked about (a hello, a remark across the room): too little to be worth
    /// stopping for, and to learn a voice from. They're still among who was there, to name if the owner likes.
    static let brief: Double = 20

    /// Whether to ask the owner who this is: not settled yet, and said enough.
    func asks(_ speaker: String) -> Bool {
        [.suggested, .unknown].contains(review(speaker)) && (results.speakers[speaker]?.talkTime ?? 0) >= Self.brief
    }

    /// Who was in the meeting, in the order they first spoke: everyone but a voice set aside as background.
    var participants: [String] { results.speakerOrder.filter { review($0) != .background } }

    /// A guess that came from a colleague's naming (shared voices), not from this meeting alone.
    func byColleague(_ speaker: String) -> Bool {
        review(speaker) == .suggested && results.speakers[speaker]?.identity?.shared == true
    }

    func review(_ speaker: String) -> Review {
        guard let identity = results.speakers[speaker]?.identity else { return .unknown }
        if identity.background == true { return .background }
        if identity.unknown == true { return identity.by == "owner" ? .notKnown : .unknown }
        if let id = identity.personId, people[id] == nil { return .unknown }  // a person the owner deleted since
        if identity.by == "owner" || person(speaker)?.confirmed == true { return .confirmed }
        return .suggested
    }

    func render(_ text: String) -> String {
        text.replacing(#/\{(S\d+)\}/#) { name(String($0.1)) }
    }

    /// The other way, for what the owner types: a speaker's name, as shown, back to its token, so a later rename
    /// still reaches the owner's copy. Longest names first, whole words only ("Ann" isn't in "Anna").
    func tokenize(_ text: String) -> String {
        var found = text
        for speaker in results.speakers.keys.sorted(by: { name($0).count > name($1).count }) {
            let pattern = "(?<![\\p{L}\\p{N}])" + NSRegularExpression.escapedPattern(for: name(speaker)) + "(?![\\p{L}\\p{N}])"
            found = found.replacingOccurrences(of: pattern, with: "{\(speaker)}", options: .regularExpression)
        }
        return found
    }
}

private extension String {
    var isBlank: Bool { trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
}
