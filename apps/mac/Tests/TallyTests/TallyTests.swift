import Foundation
import Testing
@testable import Tally

/// The S256 challenge (RFC 7636): unpadded base64url of the verifier's SHA-256, as Python's hashlib computes it.
@Test func pkceChallenge() {
    #expect(Auth.challenge("dBjftJeZ4CVP-mJ92K9-cdEwQUBOTfmMt4o5aH2Z1Gw") == "6zNL96ixlMmIwLrFpFfwC_r94rmpboqWEbu-UNx0Ya4")
}

private func person(_ id: String, _ name: String, role: String? = nil, company: String? = nil, isOwner: Bool = false) -> API.Person {
    API.Person(personId: id, name: name, aliases: [], role: role, company: company, isOwner: isOwner, voiceprints: [])
}

@Test func ulidsSortByTime() {
    let earlier = ULID.make(Date(timeIntervalSince1970: 1_700_000_000))
    let later = ULID.make(Date(timeIntervalSince1970: 1_700_000_001))
    #expect(earlier.count == 26)
    #expect(earlier < later)
}

@Test func namesFollowThePeopleAndWhoConfirmed() throws {
    let json = """
    {"meeting_id": "01M", "language": "en", "duration": 120.5,
     "summary": {"title": "Budget", "overview": "{S1} and {S2} agreed with {S3}.", "summary": [], "decisions": [],
                 "action_items": [], "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 60, "turns": 3, "clip_urls": ["https://x/1.m4a"],
                         "identity": {"name": "Alex", "person_id": "p_1", "by": "agent"}},
                  "S2": {"talk_time": 30, "turns": 2, "identity": {"unknown": true, "by": "agent"}},
                  "S3": {"talk_time": 30, "turns": 2, "identity": {"name": "Sam", "person_id": "p_2", "by": "owner"}}},
     "segments": [{"start": 1.0, "end": 2.0, "speaker": "S2", "text": "Hi."}, {"start": 2.0, "end": 3.0, "speaker": "S1", "text": "Hello."}]}
    """
    let results = try API.decoder.decode(Results.self, from: Data(json.utf8))
    let renamed = person("p_1", "Alex Kim")
    let sam = person("p_2", "Sam")
    let names = Names(results: results, people: ["p_1": renamed, "p_2": sam])
    #expect(names.render(results.summary.overview) == "Alex Kim and Speaker 2 agreed with Sam.")
    #expect(results.speakerOrder == ["S2", "S1", "S3"])
    #expect(names.review("S1") == .suggested && names.review("S2") == .unknown && names.review("S3") == .confirmed)
    // Sam deleted in People: unknown again, wherever they were confirmed
    let withoutSam = Names(results: results, people: ["p_1": renamed])
    #expect(withoutSam.name("S3") == "Speaker 3" && withoutSam.review("S3") == .unknown)
    // Asked about: S1 (a guess) and S2 (unknown), who both said enough; never someone settled
    #expect(names.asks("S1") && names.asks("S2") && !names.asks("S3"))
}

/// A guess from the shared voices says so, until the owner settles it.
@Test func aGuessFromAColleaguesNamingSaysSo() throws {
    let json = """
    {"language": "en", "duration": 60,
     "summary": {"title": "Chat", "overview": "", "summary": [], "decisions": [], "action_items": [], "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 60, "turns": 3, "identity": {"name": "Maja", "person_id": "p_1", "by": "agent", "shared": true}},
                  "S2": {"talk_time": 30, "turns": 2, "identity": {"name": "Sam", "person_id": "p_2", "by": "agent"}},
                  "S3": {"talk_time": 30, "turns": 2, "identity": {"name": "Bo", "person_id": "p_3", "by": "owner", "shared": true}}},
     "segments": []}
    """
    let results = try API.decoder.decode(Results.self, from: Data(json.utf8))
    let names = Names(results: results, people: ["p_1": person("p_1", "Maja"), "p_2": person("p_2", "Sam"), "p_3": person("p_3", "Bo")])
    #expect(names.byColleague("S1") && !names.byColleague("S2") && !names.byColleague("S3"))
}

/// Someone who said only a few words (a remark across the room) isn't asked about: too little to stop for.
@Test func someoneWhoSaidLittleIsntAskedAbout() throws {
    let json = """
    {"language": "en", "duration": 180,
     "summary": {"title": "Chat", "overview": "", "summary": [], "decisions": [], "action_items": [], "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 150, "turns": 9, "identity": {"unknown": true, "by": "agent"}},
                  "S3": {"talk_time": 12, "turns": 2, "identity": {"unknown": true, "by": "agent"}}},
     "segments": []}
    """
    let names = Names(results: try API.decoder.decode(Results.self, from: Data(json.utf8)), people: [:])
    #expect(names.asks("S1") && !names.asks("S3"))
}

/// What the Mac is doing with a meeting wins over what the cloud said last (a Send again, an interrupted send).
@Test func statusFollowsTheMacWhileItSends() {
    #expect(AppModel.status("failed", error: "x", local: .uploading) == .uploading)
    #expect(AppModel.status("failed", error: "x", local: .saved) == .waiting)
    #expect(AppModel.status("failed", error: "x", local: .sent) == .failed("x"))
    #expect(AppModel.status("uploading", error: nil, local: .uploaded) == .queued)
    #expect(AppModel.status("uploading", error: nil, local: nil) == .waiting)
    #expect(AppModel.status("ready", error: nil, local: .saved) == .ready)
    #expect(AppModel.status("processing", error: nil, local: .sent) == .processing)
}

@Test func theAccountIsNamedFromItsToken() {
    let claims = Data(#"{"sub":"x","name":"Ada Lovelace","email":"ada@example.com"}"#.utf8).base64EncodedString()
        .replacing("+", with: "-").replacing("/", with: "_").replacing("=", with: "")
    #expect(Auth.name(in: "e30.\(claims).sig") == "Ada Lovelace")
    #expect(Auth.name(in: "not a token") == nil)
}

@Test func onlyTheNewestUnhandledCrashReportIsOffered() throws {
    let folder = FileManager.default.temporaryDirectory.appending(path: "crashes-\(UUID())")
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: folder) }
    func write(_ name: String, at date: Date) throws {
        let file = folder.appending(path: name)
        try Data("{}".utf8).write(to: file)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: file.path)
    }
    let now = Date()
    try write("Tally-old.ips", at: now.addingTimeInterval(-3600))
    try write("Tally-new.ips", at: now.addingTimeInterval(-60))
    try write("Other-app.ips", at: now)
    try write("Tally-hang.spin", at: now)
    #expect(Crashes.newest(in: folder, after: now.addingTimeInterval(-7200))?.lastPathComponent == "Tally-new.ips")
    #expect(Crashes.newest(in: folder, after: now.addingTimeInterval(-30)) == nil)
}

// Calls: what the rules do with what's using audio, second by second.

private let zoom: pid_t = 501
private func onMic(_ pid: pid_t = zoom, output: Bool = true) -> [AudioUser] { [AudioUser(pid: pid, input: true, output: output)] }
private func playing(_ pid: pid_t = zoom) -> [AudioUser] { [AudioUser(pid: pid, input: false, output: true)] }

/// Feeds the same users for `seconds`, one second at a time, and gathers what happened.
private func run(_ rules: inout CallRules, _ users: [AudioUser], for seconds: Int, from start: inout Date) -> [CallRules.Event] {
    var events: [CallRules.Event] = []
    for _ in 0..<seconds {
        events += rules.observe(users, at: start)
        start += 1
    }
    return events
}

@Test func aCallIsOfferedAfterTenSecondsOnTheMic() {
    var rules = CallRules(), now = Date()
    #expect(run(&rules, onMic(), for: 10, from: &now).isEmpty)  // 0 to 9 s
    #expect(rules.observe(onMic(), at: now) == [.offer(zoom, bundleID: "")])  // 10 s
    #expect(run(&rules, onMic(), for: 60, from: &now).isEmpty)  // offered once
}

@Test func notNowHoldsForTheCallAndTheNextCallOffersAgain() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.notNow()
    #expect(run(&rules, [], for: 31, from: &now).isEmpty)  // the call ends, without a recording to stop
    #expect(run(&rules, onMic(), for: 11, from: &now) == [.offer(zoom, bundleID: "")])
}

@Test func noOfferWhileRecordingOrWithTheSettingOff() {
    var rules = CallRules(), now = Date()
    rules.started()  // nobody on the mic: a recording of its own
    #expect(rules.recording == .plain)
    #expect(run(&rules, onMic(), for: 20, from: &now).isEmpty)
    var off = CallRules()
    off.offering = false
    #expect(run(&off, onMic(), for: 20, from: &now).isEmpty)
}

@Test func mutedNeverEndsACallButSilenceForThirtySecondsDoes() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.started()
    #expect(rules.recording == .following)
    #expect(run(&rules, playing(), for: 120, from: &now).isEmpty)  // off the mic, still playing the others
    #expect(run(&rules, [], for: 30, from: &now).isEmpty)  // 0 to 29 s of silence
    #expect(rules.observe([], at: now) == [.stop])  // 30 s
    #expect(rules.call == nil)
}

@Test func aRecordingStartedEarlyInACallFollowsIt() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 3, from: &now)  // Start Recording before any offer
    rules.started()
    #expect(rules.recording == .following)
    #expect(run(&rules, [], for: 31, from: &now) == [.stop])
}

@Test func aRecordingWithNobodyElseOnTheMicNeverStops() {
    var rules = CallRules(), now = Date()
    rules.started()
    #expect(run(&rules, [], for: 600, from: &now).isEmpty)
    #expect(rules.recording == .plain)
}

@Test func aStoppedRecordingCanBeContinuedForThirtySecondsThenFinishes() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.started()
    _ = run(&rules, [], for: 31, from: &now)
    rules.continued()  // a mistake: the call goes on
    #expect(run(&rules, onMic(), for: 11, from: &now).isEmpty)  // the call is back, and no offer while recording
    #expect(run(&rules, [], for: 31, from: &now) == [.stop])  // it follows the call again
    #expect(run(&rules, [], for: 29, from: &now).isEmpty)
    #expect(rules.observe([], at: now) == [.finish])
    #expect(rules.recording == .none)
}

/// A call starting while a stopped recording can still go on: the owner says whether it's the same meeting.
@Test func aCallWhileStoppedAsksWhetherItsTheSameMeeting() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.started()
    #expect(run(&rules, [], for: 31, from: &now) == [.stop])
    #expect(run(&rules, [], for: 20, from: &now).isEmpty)
    // On the mic from 20 s after the stop: at 30 s it's not a call yet, so it waits; at 10 s on the mic, it asks.
    #expect(run(&rules, onMic(), for: 11, from: &now) == [.again(zoom, bundleID: "")])
    #expect(rules.recording == .asking)
    #expect(run(&rules, onMic(), for: 60, from: &now).isEmpty)  // no finish, no offer, while the owner decides
    rules.continued()  // The Same Meeting
    #expect(rules.recording == .following)
    #expect(run(&rules, [], for: 31, from: &now) == [.stop])
}

@Test func aMicThatGoesBeforeItsACallLetsTheStoppedRecordingFinish() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.started()
    _ = run(&rules, [], for: 31, from: &now)
    #expect(run(&rules, [], for: 25, from: &now).isEmpty)
    #expect(run(&rules, onMic(), for: 8, from: &now).isEmpty)  // Siri, say: past 30 s, but on the mic
    #expect(rules.observe([], at: now) == [.finish])  // gone, before it was a call
}

@Test func tallysOwnProcessIsNeverInTheList() {
    #expect(!AudioUsers.now().contains { $0.pid == getpid() })
}

/// Measured: a Slack huddle's helper kept its speakers open after the call, so a Not Now never reset.
@Test func afterNotNowAnAppThatKeepsItsSpeakersOpenStillGetsOfferedTheNextCall() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.notNow()
    #expect(run(&rules, playing(), for: 31, from: &now).isEmpty)  // off the mic, speakers still open: the call is over
    #expect(rules.call == nil)
    #expect(run(&rules, onMic(), for: 11, from: &now) == [.offer(zoom, bundleID: "")])  // the next call
}

@Test func aHelperIsNamedAfterTheAppItsBundleIDExtends() {
    // Finder is always running: a made-up helper of it is named after it.
    #expect(AudioUsers.name(of: -1, bundleID: "com.apple.finder.helper") == "Finder")
    #expect(AudioUsers.name(of: -1, bundleID: "com.example.nothing") == nil)
}

/// Auto mode: what it starts by itself is kept only if it lasted 2 minutes.
@Test func anAutoRecordingUnderTwoMinutesIsntKept() {
    let start = Date()
    var lobby = Recording(id: "a", startedAt: start, endedAt: start + 90, state: .recording, auto: true)
    #expect(lobby.tooShortToKeep)
    lobby.endedAt = start + 120
    #expect(!lobby.tooShortToKeep)
    #expect(!Recording(id: "b", startedAt: start, endedAt: start + 30, state: .recording).tooShortToKeep)  // started by the owner
}

// Updates

@Test func anotherVersionOnTheInstallPageIsAnUpdate() {
    #expect(Updates.newer("2026.10.27-abc1234", than: "2026.10.26-43661a4") == "2026.10.27-abc1234")
    #expect(Updates.newer("2026.10.26-43661a4", than: "2026.10.26-43661a4") == nil)
    #expect(Updates.newer("", than: "2026.10.26-43661a4") == nil)
}

@Test func onlyTheLastInstallsFailureIsReported() {
    let log = """
    --- Mon installing Tally 1
    FAILED: The download is damaged (its checksum is wrong). Try again.
    --- Tue installing Tally 2
    ==> Downloading Tally 2
    FAILED: Tally is recording. Stop the recording, then run this command again.
    """
    #expect(Updates.lastFailure(in: log) == "Tally is recording. Stop the recording, then run this command again.")
    #expect(Updates.lastFailure(in: "--- Mon installing Tally 1\nFAILED: old\n--- Tue installing Tally 2\n==> Building") == nil)
}

// The owner's summary

@Test func whatTheOwnerTypesKeepsSpeakersAsSpeakers() throws {
    let json = """
    {"language": "en", "duration": 60, "segments": [],
     "summary": {"title": "T", "overview": "", "summary": [], "decisions": [], "action_items": [], "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 30, "identity": {"name": "Anna", "person_id": "p_1"}},
                  "S2": {"talk_time": 30, "identity": {"name": "Ann", "person_id": "p_2"}}, "S3": {"talk_time": 5}}}
    """
    let results = try API.decoder.decode(Results.self, from: Data(json.utf8))
    let people = ["p_1": person("p_1", "Anna"), "p_2": person("p_2", "Ann")]
    let names = Names(results: results, people: people)
    #expect(names.tokenize("Anna told Ann, and Speaker 3 agreed. Annual review.") == "{S1} told {S2}, and {S3} agreed. Annual review.")
    #expect(names.render(names.tokenize("Ann and Anna")) == "Ann and Anna")  // what's shown is what was typed
}

@Test func emptiedPointsArentSaved() {
    var summary = Results.Summary(title: "T", overview: "", summary: ["Kept", "  "], decisions: [.init(text: "", by: [])],
                                  actionItems: [.init(owner: nil, text: "Do it", due: nil, done: true)], openQuestions: [],
                                  topics: [.init(title: "A", start: 0, end: 1, notes: ["", "Note"], outcome: nil)], attendees: [])
    summary = summary.cleaned
    #expect(summary.summary == ["Kept"] && summary.decisions.isEmpty && summary.topics[0].notes == ["Note"])
    #expect(summary.actionItems[0].done == true)
}

/// Filing: the folder the owner usually files meetings with these people in: twice or more, over half the time.
@Test func aMeetingIsSuggestedTheFolderItsPeopleAreUsuallyIn() {
    func meeting(_ id: String, _ people: [String], in folders: [String] = [], state: String = "ready") -> API.Meeting {
        API.Meeting(meetingId: id, state: state, title: nil, startedAt: nil, endedAt: nil, duration: nil, error: nil, people: people,
                    folderIds: folders)
    }
    let new = meeting("new", ["me", "anna", "erik"])
    let filed = [meeting("1", ["me", "anna"], in: ["acme"]), meeting("2", ["erik"], in: ["acme"]), meeting("3", ["anna"], in: ["team"]),
                 meeting("4", ["me"], in: ["team"])]
    #expect(Filing.suggestion(for: new, among: filed, owner: "me") == .init(folder: "acme", people: ["anna", "erik"]))
    // The owner is in every meeting: they suggest nothing.
    #expect(Filing.suggestion(for: meeting("new", ["me"]), among: filed, owner: "me") == nil)
    // A tie suggests nothing; nor does a meeting filed already, or one not ready.
    #expect(Filing.suggestion(for: meeting("new", ["anna"]), among: filed, owner: "me") == nil)
    #expect(Filing.suggestion(for: meeting("new", ["erik"], in: ["team"]), among: filed, owner: "me") == nil)
    #expect(Filing.suggestion(for: meeting("new", ["erik"], state: "processing"), among: filed, owner: "me") == nil)
    // A meeting in two folders counts for both: Anna's in Acme and Team, so with Acme's two others, Acme leads.
    let both = filed + [meeting("5", ["anna"], in: ["acme"]), meeting("6", ["anna"], in: ["acme", "team"])]
    #expect(Filing.suggestion(for: meeting("new", ["anna"]), among: both, owner: "me")?.folder == "acme")
    // Once isn't usually: one meeting with Erik in Acme suggests nothing yet.
    #expect(Filing.suggestion(for: meeting("new", ["erik"]), among: filed, owner: "me") == nil)
}

/// Search results mark each word searched for, whatever its case or accents.
@Test func searchResultsMarkTheWordsSearchedFor() {
    let text = marked("Budget for Acme, and Café plans", "acme cafe")
    let bold = text.runs.filter { $0.inlinePresentationIntent == .stronglyEmphasized }.map { String(text[$0.range].characters) }
    #expect(bold == ["Acme", "Café"])
}

/// A recording discarded mid-call: the next call, even 10 s later, is a call of its own, offered (or recorded).
@Test func aNewCallSoonAfterADiscardIsOfferedAgain() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.started()
    rules.ended()  // Discard, still in the huddle
    #expect(run(&rules, onMic(), for: 5, from: &now).isEmpty)
    #expect(run(&rules, [], for: 6, from: &now).isEmpty)  // the huddle closed: over after 5 s, nothing to stop
    #expect(rules.call == nil)
    #expect(run(&rules, onMic(), for: 11, from: &now) == [.offer(zoom, bundleID: "")])  // the next one, 10 s on the mic
}

/// Not Now still holds through a short gap: the 30 s guard is for that.
@Test func notNowHoldsThroughAShortGap() {
    var rules = CallRules(), now = Date()
    _ = run(&rules, onMic(), for: 11, from: &now)
    rules.notNow()
    #expect(run(&rules, [], for: 10, from: &now).isEmpty)
    #expect(run(&rules, onMic(), for: 20, from: &now).isEmpty)
}

/// The meetings folder: a meeting written whole, named by when and what, and only Tally's own
/// folders ever removed, never the owner's.
@Test func theMeetingsFolderWritesAMeetingAndOnlyEverRemovesItsOwn() throws {
    let json = """
    {"language": "en", "duration": 1800,
     "summary": {"title": "Budget / Q3: plan", "overview": "{S1} and {S2} set the budget.", "summary": ["A point."],
                 "decisions": [{"text": "Go ahead.", "by": ["S1"]}], "action_items": [{"owner": "S2", "text": "Send it.", "due": "2026-11-03"}],
                 "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 900, "turns": 3, "identity": {"name": "Ada", "person_id": "p_1", "by": "owner"}},
                  "S2": {"talk_time": 600, "turns": 2, "identity": {"unknown": true, "by": "agent"}}},
     "segments": [{"start": 0, "end": 4, "speaker": "S1", "text": "Shall we?"}, {"start": 4, "end": 8, "speaker": "S2", "text": "Yes."}]}
    """
    let results = try API.decoder.decode(Results.self, from: Data(json.utf8))
    let ada = person("p_1", "Ada Lovelace", role: "CFO", company: "Acme", isOwner: true)
    let meeting = API.Meeting(meetingId: "01M5W8DAHM7HCDPH26DE2PRR31", state: "ready", title: "Budget / Q3: plan",
                              startedAt: Date(timeIntervalSince1970: 1_793_000_000), endedAt: nil, duration: 1800, error: nil, people: ["p_1"], folderIds: nil)
    let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let folder = MeetingsFolder(root: root, account: "test")
    let directory = MeetingsFolder.directory(for: meeting, title: results.summary.title)
    #expect(!directory.contains("/") && !directory.contains(":") && directory.hasSuffix("Budget - Q3 - plan"))

    let source = MeetingsFolder.Source(meeting: meeting, results: results, summary: results.summary, edited: false,
                                       names: Names(results: results, people: ["p_1": ada]), folders: ["Customers › Acme"], files: [])
    try folder.place(meeting.meetingId, from: nil, to: directory)
    try folder.write(source, to: directory)
    #expect(!folder.taken(directory, by: meeting.meetingId) && folder.taken(directory, by: "01M5W8DAHM7HCDPH26DE2PRR99"))
    let record = try JSONSerialization.jsonObject(with: Data(contentsOf: folder.url(directory).appending(path: "meeting.json"))) as! [String: Any]
    #expect(record["id"] as? String == meeting.meetingId && record["folders"] as? [String] == ["Customers › Acme"])
    let items = record["action_items"] as! [[String: Any]]
    #expect(items.first?["owner"] as? String == "Speaker 2" && items.first?["due"] as? String == "2026-11-03" && items.first?["done"] as? Bool == false)
    let people = record["people"] as! [[String: Any]]
    #expect(people.first?["name"] as? String == "Ada Lovelace" && people.first?["is_you"] as? Bool == true && people.first?["company"] as? String == "Acme")
    let summary = try String(contentsOf: folder.url(directory).appending(path: "summary.md"), encoding: .utf8)
    #expect(summary.hasPrefix("# Budget / Q3: plan") && summary.contains("Ada Lovelace and Speaker 2 set the budget."))

    // The owner's own folder (no meeting.json of this meeting's) is never removed, nor another meeting's
    try FileManager.default.createDirectory(at: root.appending(path: "My notes"), withIntermediateDirectories: true)
    folder.remove("My notes", meeting: meeting.meetingId)
    folder.remove(directory, meeting: "01M5W8DAHM7HCDPH26DE2PRR99")
    #expect(FileManager.default.fileExists(atPath: root.appending(path: "My notes").path))
    #expect(FileManager.default.fileExists(atPath: folder.url(directory).path))
    folder.remove(directory, meeting: meeting.meetingId)
    #expect(!FileManager.default.fileExists(atPath: folder.url(directory).path))

    try folder.writeIndex([MeetingsFolder.entry(source, directory: directory)], account: "Ada Lovelace")
    let readme = try String(contentsOf: root.appending(path: "README.md"), encoding: .utf8)
    #expect(readme.contains("Go by id, not by path") && readme.contains(#"jq -r '.meetings[:10][] | "\(.started_at)"#))
}

/// A background voice isn't a participant: not in Copy Summary's participants, nor Copy Transcript,
/// nor asked about; its lines fold together in the transcript.
@Test func aBackgroundVoiceIsSetAside() throws {
    let json = """
    {"language": "en", "duration": 300,
     "summary": {"title": "Sync", "overview": "{S1} and {S2} met.", "summary": [], "decisions": [], "action_items": [],
                 "open_questions": [], "topics": [], "attendees": []},
     "speakers": {"S1": {"talk_time": 150, "turns": 3, "identity": {"name": "Ada", "person_id": "p_1", "by": "owner"}},
                  "S2": {"talk_time": 120, "turns": 2, "identity": {"unknown": true, "by": "agent"}},
                  "S3": {"talk_time": 30, "turns": 2, "identity": {"unknown": true, "background": true, "by": "owner"}}},
     "segments": [{"start": 0, "end": 4, "speaker": "S1", "text": "Shall we?"}, {"start": 4, "end": 6, "speaker": "S3", "text": "Lunch?"},
                  {"start": 6, "end": 8, "speaker": "S3", "text": "Upstairs."}, {"start": 8, "end": 12, "speaker": "S2", "text": "Yes."}]}
    """
    let results = try API.decoder.decode(Results.self, from: Data(json.utf8))
    let ada = person("p_1", "Ada Lovelace", role: "CFO", company: "Acme", isOwner: true)
    let names = Names(results: results, people: ["p_1": ada])
    #expect(names.review("S3") == .background && !names.asks("S3") && names.participants == ["S1", "S2"])
    let summary = Markdown.summary(results.summary, names)
    #expect(summary.contains("**Participants:** Ada Lovelace (CFO, Acme), Speaker 2") && !summary.contains("Speaker 3"))
    #expect(!Markdown.transcript(results, names).contains("Lunch?"))
}

/// The transcript is one row a paragraph, each with its own id, a lazy stack's needs to draw and scroll it; a
/// background voice's lines fold into one row where they come, and are rows of their own once shown.
@Test func theTranscriptIsOneRowAParagraphWithBackgroundLinesFolded() {
    let paragraphs: [Results.Paragraph] = [("S1", 0, "Hi."), ("S3", 2, "Lunch!"), ("S3", 3, "Upstairs."), ("S2", 5, "Hello."), ("S1", 7, "Shall we?")]
    let folded = TranscriptPage.rows(paragraphs, background: { $0 == "S3" }, folded: true)
    #expect(folded.map(\.id) == [0, 1, 3, 4] && folded.map(\.lines.count) == [1, 2, 1, 1] && folded.map(\.folded) == [false, true, false, false])
    let shown = TranscriptPage.rows(paragraphs, background: { $0 == "S3" }, folded: false)
    #expect(shown.map(\.id) == [0, 1, 2, 3, 4] && shown.allSatisfy { $0.lines.count == 1 && !$0.folded } && shown[1].background)
    let long = TranscriptPage.rows((0..<500).map { ("S\($0 % 3)", Double($0), "line") }, background: { _ in false }, folded: true)
    #expect(Set(long.map(\.id)).count == 500)
}
