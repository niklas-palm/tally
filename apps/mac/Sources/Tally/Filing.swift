import Foundation

/// Where a meeting probably belongs: the folder the owner usually files their other meetings with the
/// same people into, at least twice and more than half the time. Only for a ready meeting in no folder yet, so a
/// colleague who's in everything suggests nothing. The owner themselves never counts: they're in every meeting.
enum Filing {
    struct Suggestion: Equatable {
        let folder: String
        let people: [String]  // the person ids it rests on
    }

    static func suggestion(for meeting: API.Meeting, among meetings: [API.Meeting], owner: String?) -> Suggestion? {
        guard meeting.folderIds?.isEmpty ?? true, meeting.state == "ready" else { return nil }
        let here = Set(meeting.people ?? []).subtracting([owner].compactMap { $0 })
        var votes: [String: Int] = [:], shared: [String: Set<String>] = [:]
        for other in meetings where other.meetingId != meeting.meetingId {
            let common = here.intersection(other.people ?? [])
            guard !common.isEmpty else { continue }
            for folder in other.folderIds ?? [] {
                votes[folder, default: 0] += 1
                shared[folder, default: []].formUnion(common)
            }
        }
        guard let best = votes.max(by: { $0.value < $1.value }), best.value >= 2, best.value * 2 > votes.values.reduce(0, +) else { return nil }
        return Suggestion(folder: best.key, people: shared[best.key, default: []].sorted())
    }
}
