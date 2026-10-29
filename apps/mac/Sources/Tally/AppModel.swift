import AVFoundation
import CryptoKit
import AppKit
import AuthenticationServices
import Foundation
import os
import ServiceManagement
import UniformTypeIdentifiers
import UserNotifications

/// The app's state: recordings on this Mac, the meetings and people the API knows (the signed-in person's), the
/// recording in progress, the sync between them, and what the window shows.
///
/// Recording never waits for anything. A recording is sent as soon as the owner is signed in; the API owns the
/// meeting from then on, and once its summary is ready the recording's folder on this Mac is deleted (S3 has it).
@MainActor @Observable final class AppModel {
    let auth = Auth()
    let player = ClipPlayer()
    private(set) var recordings: [Recording] = []
    private(set) var meetings: [API.Meeting] = []
    private(set) var people: [API.Person] = []
    private(set) var results: [String: Results] = [:]  // ready meetings, by id
    private(set) var loadErrors: [String: String] = [:]  // ready meetings whose results couldn't be read
    private(set) var summaries: [String: Results.Summary] = [:]  // the owner's own copies, as saved
    private(set) var drafts: [String: Results.Summary] = [:]  // the owner's edits, before they're saved
    private(set) var savingSummary: Set<String> = []
    private(set) var files: [String: [API.File]] = [:]  // what the owner added to each meeting
    private(set) var addingFiles: [String: Int] = [:]  // how many are on their way to each
    private(set) var opening: Set<String> = []  // files being downloaded to show
    private var saves: [String: Int] = [:]  // saves and restores of each meeting's copy: a load from before one doesn't undo it
    private var editCount: [String: Int] = [:]  // counts edits per meeting: a save waits for a second without one
    private(set) var recorder: Recorder?
    private(set) var liveId: String?  // the recording in progress; a broken one being encoded again is not it
    private(set) var starting = false
    private(set) var offline = false
    private(set) var crash: URL?  // a crash report from last time, for the owner to send or not
    private(set) var update: String?  // a newer version on the install page
    private(set) var updating = false
    private(set) var stoppedByCall: (app: String?, at: Date)?  // the call ended and the recording paused: it can still go on
    private var calls = CallRules()
    private var callApp: String?  // the call's app, as the offer named it

    /// What the recording in progress follows: the call's app ("Zoom"), "" for a call without a name, nil in the room.
    var recordingCall: String? { calls.recording == .following ? callApp ?? "" : nil }

    /// The window, opened from outside SwiftUI (the menu bar icon's right-click menu): an action the menu bar
    /// label hands over, since only views get it.
    var openMain: (() -> Void)?
    private let drawer = Drawer()
    private let callLog = Logger(subsystem: Bundle.main.bundleIdentifier ?? "tally", category: "calls")  // /usr/bin/log show --predicate 'subsystem == "<bundle id>"'
    private(set) var sendingCrash = false
    private(set) var syncError: String?  // the last sync failed for a reason other than being offline; cleared by the next that works
    private(set) var elapsed = ""  // the recording's time, for the menu bar: updated once a second
    var problem: Problem?  // the last thing that went wrong, for the owner

    /// Something to tell the owner, and where to fix it when it's a setting.
    struct Problem: Equatable {
        let message: String
        var settings: URL? = nil
        var offline = false  // it happened for want of a connection: it goes once a sync works
    }

    // What the window shows.
    var selection: Selection?
    var assigning: AssignForm?
    var editing: PersonForm?
    var deleting: Item?
    var deletingPerson: API.Person?
    var peopleSelection: String?  // one at a time: every action on a person is on one
    enum PeopleSort: String, CaseIterable { case name = "Name", company = "Company", meetings = "Meetings", voice = "Voice" }
    var peopleSort = PeopleSort.name  // People's order, kept while elsewhere
    var peopleAscending = true
    var selectedPerson: API.Person? { people.first { $0.personId == peopleSelection } }
    private(set) var discardArmed = false
    private(set) var pending: Set<String> = []  // "meeting/speaker" being saved: their cards show it at once
    private(set) var saving = false  // a person's edit on its way

    enum Selection: Hashable { case meeting(String) }

    /// Where the window is, in its sidebar: Meetings (every meeting), one of the owner's folders
    /// (its meetings and its folders'), or People.
    enum Place: Hashable { case meetings, folder(String), people }

    var place = Place.meetings { didSet { keepSelectionInPlace() } }

    /// The meeting shown is one of the list's, as in Mail: when it isn't (another place, or moved away), its newest.
    private func keepSelectionInPlace() {
        guard place != .people, searchResults == nil, case .meeting(let id) = selection, !items(in: place).contains(where: { $0.id == id }) else { return }
        selection = items(in: place).max { $0.startedAt < $1.startedAt }.map { .meeting($0.id) }
    }
    private(set) var folders: [API.Folder] = []
    var renamingFolder: String?  // a folder's name being typed, in the sidebar
    var expanded: Set<String> = []  // folders open in the sidebar, showing the folders inside
    var deletingFolder: API.Folder?
    var query = "" { didSet { searchSoon() } }  // the search field's
    private(set) var searchResults: [API.SearchHit]?  // nil: not searching
    private(set) var searchFailed = false  // no results because it couldn't search, not because nothing matched
    private var searches = 0  // counts keystrokes: a search waits for a pause, and an older one's answer is dropped

    private var syncing = false
    private var syncAgain = false
    private var deletingIds: Set<String> = []  // a sync never sends a meeting while it's being deleted
    private var discardArming = 0
    private var loading: Set<String> = []
    private var reloadNeeded: Set<String> = []  // changed by the owner while a load of it was already on its way
    private var changes = 0  // the owner's changes: a sync that read the lists before one drops what it read
    private var loadedAt: [String: Date] = [:]  // clip links last an hour
    private let config = Config.shared
    private let notifier = Notifier()

    static let microphoneSettings = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!
    static let loginItemsSettings = URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")!
    static let systemAudioSettings = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture")!

    init() {
        recordings = Recording.loadAll()
        crash = Crashes.pending()
        calls.offering = callMode != .off
        recover()
        Task { await syncLoop() }
        Task { await watchCalls() }
        Task { await watchUpdates() }
    }

    // MARK: - What the lists show

    /// A meeting as the window and the menu show it, wherever it is: still on this Mac, or with the API.
    struct Item: Identifiable, Hashable {
        let id: String
        let title: String?
        let startedAt: Date
        let duration: Double?
        let status: Status
        let hasAudio: Bool  // a failed one can be sent again
        let inCloud: Bool  // deleting it deletes it in the cloud too
        var sendError: String? = nil  // the last send failed: tried again at the next sync
        var folderIds: [String] = []  // the folders it's in; every meeting is in Meetings
        var addingFiles = false  // a file on its way would land after the delete, and stay
        var canDelete: Bool { status != .recording && status != .uploading && !addingFiles }
        /// Failed with its audio still here, for a reason sending again could fix: not "no speech" or "too long", known
        /// by how the job's message starts.
        var canRetry: Bool { if case .failed(let error) = status { hasAudio && !Self.final(error) } else { false } }
        static func final(_ error: String) -> Bool { error.hasPrefix("No speech") || error.hasPrefix("This recording is longer") }
    }

    enum Status: Hashable {
        case recording, saving, waiting, uploading, queued, processing, ready
        case failed(String)
    }

    var items: [Item] {
        let remote = Set(meetings.map(\.meetingId))
        var items = meetings.map { m in
            let local = recordings.first { $0.id == m.meetingId }
            let status = Self.status(m.state, error: m.error, local: local?.state)
            let duration = m.duration ?? m.endedAt.flatMap { end in m.startedAt.map { end.timeIntervalSince($0) } }
            return Item(id: m.meetingId, title: m.title, startedAt: m.startedAt ?? ULID.date(m.meetingId), duration: duration,
                        status: status, hasAudio: local.map(Self.canSend) ?? false, inCloud: true, sendError: local?.error,
                        folderIds: m.folderIds ?? [], addingFiles: (addingFiles[m.meetingId] ?? 0) > 0)
        }
        for r in recordings where !remote.contains(r.id) {
            let status: Status = switch r.state {
            case .recording: r.id == liveId ? .recording : .saving
            case .saved: .waiting
            case .uploading: .uploading
            case .uploaded, .sent: .queued
            case .broken: .failed(r.error ?? "Nothing was recorded.")
            }
            items.append(Item(id: r.id, title: nil, startedAt: r.startedAt, duration: r.endedAt.map { $0.timeIntervalSince(r.startedAt) },
                              status: status, hasAudio: Self.canSend(r), inCloud: [.uploaded, .sent].contains(r.state),
                              sendError: r.error))
        }
        return items.sorted { $0.startedAt > $1.startedAt }
    }

    /// Where a meeting is, from what the API says and what this Mac is doing with it: sending it (again) wins over
    /// whatever the cloud said last.
    nonisolated static func status(_ remote: String, error: String?, local: Recording.State?) -> Status {
        switch (remote, local) {
        case (_, .uploading): .uploading
        case (_, .saved) where remote != "ready": .waiting
        case ("ready", _): .ready
        case ("failed", _): .failed(error ?? "Something went wrong while summarising it.")
        case ("processing", _): .processing
        case ("uploading", nil): .waiting
        default: .queued
        }
    }

    /// A recording that can be sent (again): its m4a is here, or the CAF an encode that failed left behind.
    private static func canSend(_ r: Recording) -> Bool {
        r.state != .broken || FileManager.default.fileExists(atPath: r.caf.path)
    }

    var recording: Recording? { recordings.first { $0.id == liveId } }

    /// Tally as a login item, so the menu bar has it from the start, as the system last said. Asking takes half a
    /// second (measured: 520–630 ms), so it's asked off the main thread when the menu opens, never while drawing; the
    /// owner can also change it in System Settings → General → Login Items & Extensions.
    private(set) var opensAtLogin = false

    func checkLoginItem() async {
        opensAtLogin = await Task.detached { SMAppService.mainApp.status == .enabled }.value
    }

    func setOpensAtLogin(_ on: Bool) async {
        opensAtLogin = on  // at once: the switch follows the click
        let failed = await Task.detached { () -> Bool in
            do {
                if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                return false
            } catch { return true }
        }.value
        if failed {
            problem = Problem(message: "Tally couldn't change Open at login. Change it in Login Items in System Settings.", settings: Self.loginItemsSettings)
        }
        let status = await Task.detached { SMAppService.mainApp.status }.value
        if on, status == .requiresApproval {  // turned off in System Settings before
            problem = Problem(message: "Allow Tally in Login Items to open it at login.", settings: Self.loginItemsSettings)
        }
        opensAtLogin = status == .enabled
    }
    var waitingForSignIn: Int { auth.state == .signedIn ? 0 : recordings.filter { $0.state == .saved }.count }
    var peopleById: [String: API.Person] { Dictionary(people.map { ($0.personId, $0) }, uniquingKeysWith: { a, _ in a }) }

    nonisolated static func toConfirmText(_ count: Int) -> String {
        count == 1 ? "1 person to confirm" : "\(count) people to confirm"
    }

    /// How many of a ready meeting's speakers wait for the owner: the agent's guesses and the unknown.
    func toConfirm(_ id: String) -> Int {
        guard let results = results[id] else { return 0 }
        let names = Names(results: results, people: peopleById)
        return results.speakers.keys.filter(names.asks).count
    }

    // MARK: - Recording

    func toggleRecording() async {
        if recorder != nil { await stopRecording() } else { await startRecording() }
    }

    func startRecording(auto: Bool = false) async {
        guard recorder == nil, !starting else { return }
        starting = true
        defer { starting = false }
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            problem = Problem(message: "Tally needs your microphone to record.", settings: Self.microphoneSettings)
            return
        }
        let recording = Recording(id: ULID.make(), startedAt: Date(), state: .recording, auto: auto ? true : nil)
        recording.save()
        let recorder = Recorder()
        player.stop()  // a clip playing on would go into the recording
        do {
            try recorder.start(writingTo: recording.caf)
            self.recorder = recorder
            liveId = recording.id
            recordings.insert(recording, at: 0)
            problem = nil
            calls.started()
            drawer.hide()
            Task { await tick(recorder, since: recording.startedAt) }
        } catch {
            recorder.stop()  // whatever start got to: the tap and the device go
            recording.remove()
            problem = Problem(message: error.localizedDescription)
        }
    }

    func stopRecording() async {
        guard let recording else { return }
        callLog.notice("recording stopped")
        let ended = stoppedByCall?.at ?? Date()  // a pause after the call isn't part of the meeting
        endRecording()
        var stopped = recording
        stopped.endedAt = ended
        notifier.askForPermission()  // now it means something: "you'll hear when it's ready"
        await finish(stopped)
        Task { await sync() }
    }

    /// Throws the recording in progress away, on a second click within four seconds.
    func discardRecording() {
        guard discardArmed else {
            discardArmed = true
            discardArming += 1
            let arming = discardArming
            Task {
                try? await Task.sleep(for: .seconds(4))
                if arming == discardArming { discardArmed = false }  // an older arming doesn't disarm a newer one
            }
            return
        }
        guard let recording else { return }
        callLog.notice("recording discarded by the owner")  // the logs then say why a call's recording ended
        endRecording()
        forget(recording.id)
    }

    /// A recording off this Mac: its folder and its row, with the selection moved to the next one.
    private func forget(_ id: String) {
        let neighbour = neighbour(of: id)
        recordings.first { $0.id == id }?.remove()
        recordings.removeAll { $0.id == id }
        if selection == .meeting(id) { selection = neighbour.map { .meeting($0) } }
    }

    func endRecording() {
        recorder?.stop()
        recorder = nil
        liveId = nil
        discardArmed = false
        stoppedByCall = nil
        calls.ended()
        drawer.hide()  // a question about the recording that's ended (Same Meeting?) has nothing left to answer
    }

    // MARK: - Updates

    /// At launch, every 30 minutes, and when the menu opens: is there a newer Tally on the install page? A build from
    /// the repository never asks.
    private func watchUpdates() async {
        while true {
            await checkForUpdate()
            try? await Task.sleep(for: .seconds(30 * 60))
        }
    }

    func checkForUpdate() async {
        guard let site = config.site, let version = config.version, update == nil else { return }
        if let latest = await Updates.available(site: site, mine: version) { update = latest }
    }

    /// Recording, or still encoding one: the installer refuses to update then, so Update waits too.
    var recordingOrSaving: Bool { recorder != nil || recordings.contains { $0.state == .recording } }

    /// Runs the installer, which quits Tally and opens the new one. If it fails, this Tally is still here to say why.
    func installUpdate() {
        guard let site = config.site, update != nil, !recordingOrSaving, !updating else { return }
        updating = true
        Task {
            defer { updating = false }
            do {
                guard try await Updates.install(from: site) != 0 else { return }
                let why = (try? String(contentsOf: Updates.log, encoding: .utf8)).flatMap(Updates.lastFailure)
                problem = Problem(message: "Tally couldn't update: " + (why ?? "the installer stopped. Run the install command from \(site.host() ?? "Tally's install page") to see why."))
            } catch {
                problem = Problem(message: error is URLError ? "Tally couldn't update: it couldn't reach \(site.host() ?? "its install page"). Try again once you're online."
                                                             : "Tally couldn't update: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Calls

    /// What Tally does when a call starts (the menu's Calls choice): nothing, offer to record (the default), or
    /// record by itself.
    enum CallMode: String, CaseIterable {
        case off, offer, auto

        var title: String {
            switch self {
            case .off: "Do Nothing"
            case .offer: "Ask to Record"
            case .auto: "Record Automatically"
            }
        }

        /// What the choice does, in a sentence under it.
        var explanation: String {
            switch self {
            case .off: "You start recordings yourself."
            case .offer: "Tally automatically detects a call, and offers to record."
            case .auto: "Tally automatically detects and records calls."
            }
        }
    }

    var callMode: CallMode = UserDefaults.standard.string(forKey: "callMode").flatMap(CallMode.init) ?? .offer {
        didSet {
            UserDefaults.standard.set(callMode.rawValue, forKey: "callMode")
            calls.offering = callMode != .off
            if callMode == .off { drawer.hide() }
        }
    }

    /// Once a second: what's using audio, through the rules. Reading Core Audio's list is cheap, and the rules need
    /// a clock anyway (10 s on the microphone, 30 s of quiet, 30 s to continue).
    private func watchCalls() async {
        while true {
            let users = await Task.detached { AudioUsers.now() }.value  // Core Audio's process list: not on the main thread
            for event in calls.observe(users, at: Date()) {
                switch event {
                case .offer(let pid, let bundleID):
                    callApp = AudioUsers.name(of: pid, bundleID: bundleID)
                    let auto = callMode == .auto
                    callLog.notice("call started: \(self.callApp ?? "an unnamed process", privacy: .public) (pid \(pid)), \(auto ? "recording" : "offering", privacy: .public)")
                    if auto { await startRecording(auto: true) }
                    if !auto || recorder != nil {  // said either way: the owner knows it's recording, or may
                        drawer.show(CallOffer(model: self, app: callApp, recording: auto),
                                    announcing: auto ? "Tally is recording this call." : "Record this call with Tally? \(callApp ?? "An app") is using your microphone.",
                                    folds: .seconds(auto ? 5 : 20))
                    }
                case .again(let pid, let bundleID):
                    callApp = AudioUsers.name(of: pid, bundleID: bundleID)
                    callLog.notice("a call started while stopped: \(self.callApp ?? "an unnamed process", privacy: .public), asking")
                    drawer.show(SameMeeting(model: self, app: callApp),
                                announcing: "A new call started. Is it the same meeting?", folds: .seconds(30)) { [weak self] in
                        Task { await self?.unanswered() }
                    }
                case .stop:
                    callLog.notice("call ended: recording stopped, continuable for 30 s")
                    recorder?.pause()
                    stoppedByCall = (callApp, Date())
                    notifier.callEnded(callApp)
                case .finish:
                    callLog.notice("not continued: sending")
                    Task { await stopRecording() }  // not awaited: its encode mustn't keep the next call from being seen
                }
            }
            try? await Task.sleep(for: .seconds(1))
        }
    }

    func recordCall() async {
        drawer.hide()
        await startRecording()
    }

    func notNow() {
        calls.notNow()
        drawer.hide()
    }

    /// Don't Record, on an auto-started recording: it goes, and this call isn't recorded again by itself.
    func dontRecord() {
        guard let recording, recording.auto == true else { return }
        callLog.notice("auto recording discarded from the drawer")
        endRecording()
        forget(recording.id)
        calls.notNow()
    }

    /// The Same Meeting, to a call that started while the last one's recording was stopped: one recording.
    func sameMeeting() {
        guard stoppedByCall != nil else { return }
        callLog.notice("the same meeting: continuing")
        continueRecording()
    }

    /// A New Meeting: the stopped recording is sent, and this call gets one of its own.
    func newMeeting(auto: Bool = false) async {
        guard stoppedByCall != nil else { return }
        callLog.notice("a new meeting: sending the last, recording this")
        await stopRecording()
        await startRecording(auto: auto)
    }

    /// No answer: in auto mode it's a new meeting, recorded as any call is; otherwise the last one is sent, and
    /// this call isn't recorded, as after an offer left unanswered.
    private func unanswered() async {
        guard stoppedByCall != nil else { return }
        if callMode == .auto { await newMeeting(auto: true) } else { await stopRecording() }
    }

    /// Carries on in the same recording after the call seemed to end: one meeting, one summary.
    func continueRecording() {
        drawer.hide()  // a question about a new call is answered too
        do {
            try recorder?.resume()
            stoppedByCall = nil
            calls.continued()
        } catch {
            problem = Problem(message: error.localizedDescription)
        }
    }

    private func tick(_ recorder: Recorder, since start: Date) async {
        while self.recorder === recorder {
            let seconds = Date().timeIntervalSince(start)
            if seconds >= Self.longest {
                callLog.notice("the recording reached its longest: stopped")
                if stoppedByCall == nil {  // one its call's end already stopped (and then a night's sleep) ends without the reason
                    problem = Problem(message: "Tally stopped the recording at 4 hours, the longest it records. What it recorded is kept.")
                }
                await stopRecording()
                return
            }
            elapsed = clock(seconds)
            try? await Task.sleep(for: .seconds(1))
        }
    }

    /// The longest a recording runs: the pipeline summarises no more, and a forgotten room recording would otherwise
    /// run all night.
    static let longest: TimeInterval = 4 * 3600

    /// Encodes the recording to the m4a that's uploaded. The CAF goes only once the m4a is safely recorded as saved;
    /// if encoding fails, the CAF stays and Send Again tries once more.
    private func finish(_ recording: Recording) async {
        if recording.tooShortToKeep {  // a lobby, a mic test, dictation: not a meeting
            callLog.notice("auto recording under \(Int(Recording.shortest)) s discarded")
            forget(recording.id)
            problem = Problem(message: "Tally didn't keep the recording it started: under 2 minutes, it wasn't a meeting.")
            return
        }
        var recording = recording
        let (source, destination) = (recording.caf, recording.audio)
        if (try? AVAudioFile(forReading: source).length) == 0 {  // stopped at once: nothing to keep or send
            forget(recording.id)
            problem = Problem(message: "Nothing was recorded, so there's nothing to keep.")
            return
        }
        do {
            try await Task.detached { try Recorder.encode(source, to: destination) }.value
            recording.state = .saved
            recording.error = nil
            update(recording)
            try? FileManager.default.removeItem(at: source)
        } catch {
            recording.state = .broken
            recording.error = "Tally couldn't save the recording."  // the system's own words are no help here
            update(recording)
        }
    }

    /// After a crash or a quit: a recording still in progress keeps what was recorded, and a send that was cut off
    /// starts again.
    private func recover() {
        for var recording in recordings {
            switch recording.state {
            case .recording:
                if FileManager.default.fileExists(atPath: recording.caf.path) {
                    recording.endedAt = (try? recording.caf.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
                    Task { await finish(recording); await sync() }
                } else if FileManager.default.fileExists(atPath: recording.audio.path) {
                    recording.state = .saved  // encoded, but the crash came before it was marked
                    update(recording)
                } else {
                    recording.remove()  // the crash came before anything was recorded
                    recordings.removeAll { $0.id == recording.id }
                }
            case .uploading:
                recording.state = .saved
                update(recording)
            default:
                break
            }
        }
    }

    /// Saves a recording's state, unless it has been deleted meanwhile (an upload finishing after a delete).
    private func update(_ recording: Recording) {
        guard let index = recordings.firstIndex(where: { $0.id == recording.id }) else { return }
        recordings[index] = recording
        recording.save()
    }

    // MARK: - Meetings and people

    func open(_ id: String) {
        if place == .people || !items(in: place).contains(where: { $0.id == id }) { place = .meetings }  // so the list beside it shows it
        selection = .meeting(id)  // the window loads what it selects
    }

    /// A ready meeting's results, fetched again once their clip links are about to expire.
    func load(_ id: String) async {
        let stale = (loadedAt[id] ?? .distantPast) < Date().addingTimeInterval(-50 * 60)
        guard items.first(where: { $0.id == id })?.status == .ready, stale else { return }
        guard !loading.contains(id) else { return }
        loading.insert(id)
        guard let api = await api() else {
            loading.remove(id)
            if auth.state == .signedIn { loadErrors[id] = "You're offline. Try again when you're back online." }
            return
        }
        let generation = generation
        let edits = (editCount[id], saves[id])
        let fetched: Result<API.Detail, Error>
        do { fetched = .success(try await api.detail(id)) } catch { fetched = .failure(error) }
        loading.remove(id)
        guard generation == self.generation else { return }  // signed out meanwhile
        switch fetched {
        case .success(let detail):
            if let found = detail.results {
                results[id] = found
                // Not over a copy saved or typed while this was on its way
                if edits == (editCount[id], saves[id]), !savingSummary.contains(id) { summaries[id] = detail.notes?.summary }
                loadedAt[id] = Date()
                loadErrors[id] = nil
            }
        case .failure(let error as API.Offline):
            offline = true
            loadErrors[id] = error.localizedDescription + " Tally opens it when you're back online."  // the next sync tries again
        case .failure(let error):
            // The API's own words, or plain ones for a summary this version of Tally can't read
            loadErrors[id] = error is API.Failure ? error.localizedDescription : "Tally couldn't read this summary. Updating Tally may help."
            loadedAt[id] = Date()  // don't ask again at every sync; Try again does
        }
        if reloadNeeded.remove(id) != nil {
            loadedAt[id] = nil
            await load(id)
        }
    }

    func reload(_ id: String) async {
        loadedAt[id] = nil
        loadErrors[id] = nil
        await load(id)
    }

    func delete(_ item: Item) async {
        // The item as it is now: a dialog may have been open while a sync started sending it.
        guard let item = items.first(where: { $0.id == item.id }), item.canDelete, !deletingIds.contains(item.id) else { return }
        deletingIds.insert(item.id)
        defer { deletingIds.remove(item.id) }
        let generation = generation
        if item.inCloud {
            guard let api = await api(for: "delete this meeting") else { return }
            do {
                try await api.delete(meeting: item.id)
                changes += 1
            } catch {
                report(error, "delete this meeting")
                return
            }
            let fresh = try? await api.people()  // people it alone brought in are gone too
            if generation == self.generation, let fresh { people = fresh }
        }
        forget(item.id)
        meetings.removeAll { $0.meetingId == item.id }
        results[item.id] = nil
    }

    private func neighbour(of id: String) -> String? {
        let ids = items.map(\.id)
        guard let index = ids.firstIndex(of: id) else { return nil }
        return index + 1 < ids.count ? ids[index + 1] : index > 0 ? ids[index - 1] : nil
    }

    /// Sends a meeting again: uploading its audio again starts the pipeline again. A recording that couldn't be
    /// saved is encoded again first.
    func retry(_ item: Item) {
        guard var recording = recordings.first(where: { $0.id == item.id }), [.sent, .broken].contains(recording.state) else { return }
        recording.error = nil
        if recording.state == .broken {
            guard FileManager.default.fileExists(atPath: recording.caf.path) else { return }
            recording.state = .recording  // "Saving…" while it's encoded again, so a second click does nothing
            update(recording)
            Task { await finish(recording); await sync() }
            return
        }
        recording.state = .saved
        update(recording)
        Task { await sync() }
    }

    func assign(_ meeting: String, _ speaker: String, _ body: [String: Any]) async {
        guard let api = await api(for: "save who this is") else { return }
        let key = "\(meeting)/\(speaker)"
        pending.insert(key)
        defer { pending.remove(key) }
        let generation = generation
        do {
            try await api.assign(meeting, speaker, body)
            changes += 1
            // People first: someone new must be among them to show as named. If that fails, the change is still
            // saved, and the next sync brings both.
            let fresh = try? await api.people()
            guard generation == self.generation else { return }
            loadedAt[meeting] = nil
            if let fresh { people = fresh } else { Task { await sync() } }
            if loading.contains(meeting) {
                reloadNeeded.insert(meeting)  // the load on its way may have read it before this change
            } else if fresh != nil {
                await load(meeting)
            }
        } catch {
            if generation == self.generation { report(error, "save who this is") }
        }
    }

    /// The summary written again without the background voices, replacing the owner's copy when they
    /// said so (`replace`). Shown as on its way at once; the sync that sees it done reads it again.
    func rewriteSummary(_ meeting: String, replace: Bool) async {
        guard let api = await api(for: "rewrite the summary"), let i = meetings.firstIndex(where: { $0.meetingId == meeting }) else { return }
        meetings[i].rewriting = Date()  // at once: the button gives way to "Rewriting…", and can't be pressed twice
        meetings[i].rewriteError = nil
        changes += 1  // a sync that read the list before this landed mustn't take it for done
        do {
            try await api.rewriteSummary(of: meeting, replace: replace)
            changes += 1
            Task { await sync() }
        } catch {
            if let j = meetings.firstIndex(where: { $0.meetingId == meeting }) { meetings[j].rewriting = nil }
            report(error, "rewrite the summary")
        }
    }

    /// Yes, with their details: someone new is named in full and confirmed in one step, not confirmed and then
    /// edited. One the agent added (`person`) gets the details, and then the voice; one it only heard of is made.
    func confirm(_ meeting: String, _ speaker: String, person: String?, name: String, role: String, company: String) async {
        let (name, role, company) = (name.trimmingCharacters(in: .whitespaces), role.trimmingCharacters(in: .whitespaces),
                                     company.trimmingCharacters(in: .whitespaces))
        guard !name.isEmpty else { return }
        guard let person else {
            return await assign(meeting, speaker, ["name": name, "role": role, "company": company].filter { !$0.value.isEmpty })
        }
        if let known = people.first(where: { $0.personId == person }), known.name != name || (known.role ?? "") != role || (known.company ?? "") != company {
            guard let api = await api(for: "save who this is") else { return }
            do {
                try await api.update(person: person, ["name": name, "role": role, "company": company])
                changes += 1
            } catch {
                return report(error, "save who this is")
            }
        }
        await assign(meeting, speaker, ["person_id": person])
    }

    /// The crash report from last time, to the API, which keeps it and emails the deployment's owner.
    func sendCrash() async {
        guard let crash, !sendingCrash, let api = await api(for: "send the crash report") else { return }
        sendingCrash = true
        defer { sendingCrash = false }
        do {
            try await api.reportCrash(try String(contentsOf: crash, encoding: .utf8), appVersion: appVersion)
            Crashes.handled(crash)
            self.crash = nil
        } catch {
            report(error, "send the crash report")
        }
    }

    func dismissCrash() {
        if let crash { Crashes.handled(crash) }
        crash = nil
    }

    // MARK: - Folders and search

    /// The meetings a place lists: Meetings, every one; a folder, its own and its folders'.
    func items(in place: Place) -> [Item] {
        switch place {
        case .meetings: return items
        case .folder(let id): let inside = within(id); return items.filter { !inside.isDisjoint(with: $0.folderIds) }
        case .people: return []
        }
    }

    /// A folder and every folder inside it.
    func within(_ id: String) -> Set<String> {
        var found: Set<String> = [id]
        while true {
            let more = folders.filter { $0.parent.map(found.contains) == true && !found.contains($0.folderId) }.map(\.folderId)
            if more.isEmpty { return found }
            found.formUnion(more)
        }
    }

    func children(of parent: String?) -> [API.Folder] {
        folders.filter { $0.parent == parent }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// "Customers › Acme", for menus and labels.
    func path(_ id: String) -> String {
        var names: [String] = [], at: String? = id
        while let current = at, let folder = folders.first(where: { $0.folderId == current }) {
            names.insert(folder.name, at: 0)
            at = folder.parent
        }
        return names.joined(separator: " › ")
    }

    /// Every folder, in the order the sidebar shows them: what the Folders menu offers.
    var folderChoices: [API.Folder] {
        func walk(_ parent: String?) -> [API.Folder] { children(of: parent).flatMap { [$0] + walk($0.folderId) } }
        return walk(nil)
    }

    /// The paths of these folders, in the sidebar's order.
    func paths(of folderIds: [String]) -> [String] {
        folderChoices.filter { folderIds.contains($0.folderId) }.map { path($0.folderId) }
    }

    /// A new folder, at the top or inside one, named for typing over at once.
    func newFolder(in parent: String? = nil) async {
        guard let api = await api(for: "make the folder") else { return }
        let taken = Set(children(of: parent).map(\.name))
        let name = ["New Folder"] + (2...99).map { "New Folder \($0)" }
        let folder = API.Folder(folderId: "f_" + ULID.make(), name: name.first { !taken.contains($0) } ?? "New Folder", parent: parent)
        folders.append(folder)
        if let parent { expanded.insert(parent) }  // so the new one, and its name being typed, show
        renamingFolder = folder.folderId  // named where it is, as in Finder; the list and the meeting shown stay
        do {
            try await api.save(folder)
            changes += 1
        } catch {
            folders.removeAll { $0.folderId == folder.folderId }
            if renamingFolder == folder.folderId { renamingFolder = nil }  // else no sync would bring the folders again
            report(error, "make the folder")
        }
    }

    func rename(folder id: String, to name: String) async {
        renamingFolder = nil
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let i = folders.firstIndex(where: { $0.folderId == id }), !name.isEmpty, name != folders[i].name else { return }
        await change(folder: id) { $0.name = name }
    }

    /// A folder into another, or to the top (nil). Never inside itself, or inside one inside it.
    func nest(folder id: String, in parent: String?) async {
        guard parent.map({ !within(id).contains($0) }) ?? true, folders.first(where: { $0.folderId == id })?.parent != parent else { return }
        await change(folder: id) { $0.parent = parent }
        if let parent, folders.first(where: { $0.folderId == id })?.parent == parent { expanded.insert(parent) }  // where it went shows
    }

    private func change(folder id: String, _ edit: (inout API.Folder) -> Void) async {
        // Found again after the wait for the API: a sync may have changed the list meanwhile
        guard folders.contains(where: { $0.folderId == id }), let api = await api(for: "change the folder"),
              let i = folders.firstIndex(where: { $0.folderId == id }) else { return }
        let before = folders[i]
        edit(&folders[i])
        do {
            try await api.save(folders[i])
            changes += 1  // a sync that read the folders before this was saved mustn't put the old one back
        } catch {
            if let j = folders.firstIndex(where: { $0.folderId == id }) { folders[j] = before }
            report(error, "change the folder")
        }
    }

    /// A folder and the folders inside it; their meetings stay, in Meetings and any other folders.
    func delete(folder: API.Folder) async {
        guard let api = await api(for: "delete the folder") else { return }
        do {
            try await api.delete(folder: folder.folderId)
            changes += 1
            let gone = within(folder.folderId)
            folders.removeAll { gone.contains($0.folderId) }
            for i in meetings.indices { meetings[i].folderIds?.removeAll(where: gone.contains) }
            if case .folder(let id) = place, gone.contains(id) { place = .meetings }
        } catch {
            report(error, "delete the folder")
        }
    }

    /// A meeting into a folder (`inside`) or out of it: at once, and back if the API says no. It's the same
    /// meeting in each folder it's in, so a change to it anywhere is everywhere.
    func file(_ meeting: String, in folder: String, _ inside: Bool) async {
        let action = inside ? "add the meeting to the folder" : "take the meeting out of the folder"
        // Found again after the wait for the API: a sync may have changed the list meanwhile
        func index() -> Int? { meetings.firstIndex { $0.meetingId == meeting && ($0.folderIds ?? []).contains(folder) != inside } }
        func move(_ into: Bool, _ ids: inout [String]?) { if into { ids = (ids ?? []) + [folder] } else { ids?.removeAll { $0 == folder } } }
        guard index() != nil, let api = await api(for: action), let i = index() else { return }
        move(inside, &meetings[i].folderIds)
        changes += 1
        do {
            try await api.file(meeting, in: folder, inside)
            changes += 1  // and after: a sync that read the list before this landed drops what it read
            keepSelectionInPlace()
        } catch {
            if let j = meetings.firstIndex(where: { $0.meetingId == meeting }) { move(!inside, &meetings[j].folderIds) }
            report(error, action)
        }
    }

    /// Where a meeting in no folder probably belongs, unless the owner dismissed it.
    func suggestion(for id: String) -> Filing.Suggestion? {
        guard !notFiled.contains(id), let meeting = meetings.first(where: { $0.meetingId == id }) else { return nil }
        return Filing.suggestion(for: meeting, among: meetings, owner: people.first(where: \.isOwner)?.personId)
    }

    /// Dismiss, on a suggested folder: it isn't suggested for that meeting again.
    var notFiled: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "notFiled") ?? []) {
        didSet { UserDefaults.standard.set(Array(notFiled), forKey: "notFiled") }
    }

    // MARK: - The meetings folder

    /// Where this account's meetings are copied, or nil: the signed-in person's own setting, so someone else signing in
    /// on this Mac never has their meetings written into it.
    var meetingsFolder: URL? {
        get {
            _ = folderChanges  // read it so the menu redraws after a change
            return auth.subject.flatMap { UserDefaults.standard.string(forKey: "meetingsFolder.\($0)") }.map { URL(filePath: $0, directoryHint: .isDirectory) }
        }
        set {
            guard let account = auth.subject else { return }
            UserDefaults.standard.set(newValue?.path, forKey: "meetingsFolder.\(account)")
            folderChanges += 1
            folderProblem = nil
            if newValue != nil { Task { await sync() } }  // filled in now, not at the next tick
        }
    }
    private var folderChanges = 0
    private(set) var folderProblem: String?  // the meetings folder couldn't be written: said in the menu, not just logged
    private let folderLog = Logger(subsystem: Bundle.main.bundleIdentifier ?? "tally", category: "meetings-folder")
    private var keepingFolder = false

    /// Keep a Copy in a Folder…: an open panel for a folder, with a word of warning for one that syncs elsewhere.
    func chooseMeetingsFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Keep Meetings Here"
        panel.message = "Tally keeps a copy of your meetings here, for you and the agents on your Mac: summaries, transcripts and files."
        panel.directoryURL = meetingsFolder ?? FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        // iCloud Drive, Dropbox, OneDrive, Google Drive: the copy would leave this Mac too
        if url.path.contains("/Library/Mobile Documents/") || url.path.contains("/Library/CloudStorage/") {
            let alert = NSAlert()
            alert.messageText = "This folder syncs to the cloud"
            alert.informativeText = "Your meetings' summaries, transcripts and files would be copied to that service too, not only kept on this Mac."
            alert.addButton(withTitle: "Use It Anyway")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        meetingsFolder = url
    }

    /// One pass, after each sync: every ready meeting written as it is now. A meeting is fetched again only when
    /// it changed (the job's stamp, or the owner's); a rename in People or of a folder re-renders from what's kept. A
    /// fresh token for each call it makes (`api()`): a first fill takes longer than one lasts, and a token that ran
    /// out would read as a sign-in to redo.
    private func keepMeetingsFolder() async {
        guard let root = meetingsFolder, let account = auth.subject, !keepingFolder else { return }
        keepingFolder = true
        defer { keepingFolder = false }
        let copy = MeetingsFolder(root: root, account: account)
        var state = copy.state()
        let people = peopleById
        let generation = generation  // signed out meanwhile: nothing more is written or removed
        // What renders a meeting besides itself: people's names, roles and confirmed voices, folders' paths, and this code's format
        let shared = "1\n" + people.values.sorted { $0.personId < $1.personId }.map { "\($0.personId)\t\($0.name)\t\($0.role ?? "")\t\($0.company ?? "")\t\($0.isOwner)\t\($0.confirmed)" }
            .joined(separator: "\n") + folders.map { "\($0.folderId)\t\(path($0.folderId))" }.sorted().joined(separator: "\n")
        let rendered = SHA256.hash(data: Data(shared.utf8)).map { String(format: "%02x", $0) }.joined()
        var entries: [MeetingsFolder.IndexEntry] = []
        var failed: String?
        for meeting in meetings where meeting.state == "ready" {
            let id = meeting.meetingId
            let stamp = "\(meeting.updatedAt?.timeIntervalSince1970 ?? 0)|\(meeting.editedAt?.timeIntervalSince1970 ?? 0)"
            let known = state.meetings[id]
            let changed = known?.stamp != stamp || copy.cached(id) == nil
            if changed {
                guard let api = await self.api() else { return }  // signed out, or offline: the next pass
                guard let data = try? await api.detailData(id) else { continue }  // offline, or gone: the next pass
                try? copy.cache(data, of: id)
            }
            guard let detail = copy.cached(id), let results = detail.results else { continue }
            let summary = detail.notes?.summary ?? results.summary
            var directory = MeetingsFolder.directory(for: meeting, title: summary.title)
            if copy.taken(directory, by: id) { directory += " (\(id.suffix(6)))" }  // its own name, never into another's
            do {
                var page = MeetingsFolder.Source(meeting: meeting, results: results, summary: summary, edited: detail.notes?.summary != nil,
                                                 names: Names(results: results, people: people), folders: paths(of: meeting.folderIds ?? []),
                                                 files: [])  // filled in where they're written: the index reads no files
                // Also when its folder isn't there: a new folder chosen, or this one emptied in Finder
                let holds = copy.holds(directory, meeting: id)
                if changed || known?.rendered != rendered || known?.directory != directory || !holds {
                    try copy.place(id, from: known?.directory, to: directory)
                    // Written before anything downloads into it: a fill cut short is still its own, finished here next time
                    page.files = copy.present(in: directory)
                    try copy.write(page, to: directory)
                    if changed || !holds {
                        // Signed out, or offline: this meeting's files come next pass, as any failure here does (the catch)
                        guard let api = await self.api() else { throw API.Offline() }
                        page.files = try await copy.files(try await api.files(of: id), in: directory)
                        try copy.write(page, to: directory)
                    }
                    state.meetings[id] = .init(stamp: stamp, directory: directory, rendered: rendered)
                }
                entries.append(MeetingsFolder.entry(page, directory: directory))
            } catch {
                folderLog.error("meeting \(id, privacy: .public) not written: \(error.localizedDescription, privacy: .public)")
                failed = error.localizedDescription
                state.meetings[id]?.stamp = ""  // a fill cut short (a file that didn't download): written again next pass
            }
        }
        guard generation == self.generation else { return }
        // A meeting deleted in Tally: its folder goes too, if it's the one Tally made
        for (id, entry) in state.meetings where !meetings.contains(where: { $0.meetingId == id }) {
            copy.remove(entry.directory, meeting: id)
            copy.forget(id)
            state.meetings[id] = nil
        }
        do {
            try copy.writeIndex(entries, account: auth.account)
        } catch {
            failed = error.localizedDescription  // the folder itself: gone, or a drive ejected
        }
        try? copy.save(state)
        guard meetingsFolder == root else { return }  // stopped keeping it, or another chosen, meanwhile
        folderProblem = failed.map { "Tally couldn't update your meetings folder (\(root.lastPathComponent)): \($0)" }
    }

    /// Fetched again at the next pass, after this Mac adds files: the listing after their upload moves the API's stamp,
    /// and this covers that listing failing.
    private func refetchCopy(_ meeting: String) {
        guard let root = meetingsFolder, let account = auth.subject else { return }
        let copy = MeetingsFolder(root: root, account: account)
        var state = copy.state()
        state.meetings[meeting]?.stamp = ""
        try? copy.save(state)
    }

    // MARK: - Files

    /// Copies of meetings' files, downloaded to show them: a cache, so macOS may clear it.
    static let fileCache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appending(path: "Tally/files")

    func loadFiles(_ meeting: String) async {
        guard let api = await api() else { return }  // the page says it's offline or signed out already
        let generation = generation
        let found = try? await api.files(of: meeting)  // none: the page says they show once Tally can reach them
        guard generation == self.generation else { return }
        files[meeting] = found
    }

    private func cached(_ file: API.File, of meeting: String) -> URL {
        Self.fileCache.appending(path: meeting).appending(path: file.fileId).appending(path: file.name)
    }

    /// Files from this Mac (dropped, pasted or chosen) into a meeting, one after another.
    func add(_ urls: [URL], to meeting: String) async {
        let urls = urls.filter(Self.isFile)
        guard !urls.isEmpty, await api(for: "add the files") != nil else { return }
        addingFiles[meeting, default: 0] += urls.count
        for url in urls {
            let type = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
            do {
                // A fresh token for each: a big file takes longer to go up than one may have left
                guard let api = await self.api() else { throw API.Offline() }
                try await api.add(file: url, type: type, to: meeting)
            } catch { report(error, "add “\(url.lastPathComponent)”") }
            addingFiles[meeting, default: 1] -= 1
        }
        await loadFiles(meeting)
        refetchCopy(meeting)  // its files are up now: the meetings folder fetches them at the next sync
    }

    /// A file to keep: not a link, and not a folder.
    static func isFile(_ url: URL) -> Bool {
        url.isFileURL && (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) != true
    }

    /// A meeting as text, dragged out to another app: its title, when, and its overview once it's been opened.
    func dragText(_ item: Item) -> String {
        var lines = [item.title ?? "Recording", item.startedAt.formatted(date: .long, time: .shortened)]
        if let results = results[item.id] {
            lines += ["", Names(results: results, people: peopleById).render((summary(of: item.id) ?? results.summary).overview)]
        }
        return lines.joined(separator: "\n")
    }

    /// The meetings someone was named in: their meetings, in People.
    func meetings(with person: String) -> [API.Meeting] { meetings.filter { $0.people?.contains(person) == true } }

    /// An image on the clipboard (a screenshot taken with Control), as a PNG file added to the meeting; the copy made to
    /// add it goes once it's up, since it's the meeting's content outside Tally's own folders.
    func pasteImage(to meeting: String) async {
        guard let image = NSImage(pasteboard: .general), let tiff = image.tiffRepresentation,
              let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) else { return }
        let folder = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appending(path: Self.pastedName.string(from: Date()))
        guard (try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)) != nil,
              (try? png.write(to: url)) != nil else { return }
        await add([url], to: meeting)
    }

    private static let pastedName: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "'Pasted' yyyy-MM-dd 'at' HH.mm.ss'.png'"
        return formatter
    }()

    /// A file's copy on this Mac, downloaded the first time, for Quick Look and opening.
    func local(_ file: API.File, of meeting: String) async -> URL? {
        let url = cached(file, of: meeting)
        if FileManager.default.fileExists(atPath: url.path) { return url }
        opening.insert(file.fileId)
        defer { opening.remove(file.fileId) }
        do {
            let (downloaded, response) = try await URLSession.shared.download(from: file.url)
            defer { try? FileManager.default.removeItem(at: downloaded) }  // the download's own copy, whether it was moved or not
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw CocoaError(.fileReadUnknown) }
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? FileManager.default.moveItem(at: downloaded, to: url)  // fails only when a second click got there first
            return url
        } catch {
            if error is URLError { offline = true }
            problem = Problem(message: error is URLError ? offlineMessage("open “\(file.name)”") : "Tally couldn't open “\(file.name)”. Try again.",
                              offline: error is URLError)
            await loadFiles(meeting)  // perhaps its link expired: a fresh one for next time
            return nil
        }
    }

    func delete(_ file: API.File, of meeting: String) async {
        guard let api = await api(for: "delete the file") else { return }
        do {
            try await api.delete(file: file.fileId, of: meeting)
            files[meeting]?.removeAll { $0.fileId == file.fileId }
            try? FileManager.default.removeItem(at: cached(file, of: meeting).deletingLastPathComponent())
        } catch {
            report(error, "delete the file")
        }
    }

    /// Search, a moment after the last keystroke; the field emptied, the list again.
    private func searchSoon() {
        searches += 1
        let count = searches
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else {
            searchResults = nil
            return keepSelectionInPlace()  // a result opened from elsewhere gives way to the list's own
        }
        if searchFailed { searchFailed = false; searchResults = nil }  // Searching…, not the last failure
        Task {
            try? await Task.sleep(for: .milliseconds(300))
            guard count == searches else { return }
            guard let api = await api(for: "search") else {
                if count == searches { searchFailed = true; searchResults = [] }  // not if signed out or typed meanwhile
                return
            }
            do {
                let found = try await api.search(query)
                if count == searches { searchFailed = false; searchResults = found }
            } catch {
                if count == searches { searchFailed = true; searchResults = []; report(error, "search") }  // not the list, as if it found everything
            }
        }
    }

    // MARK: - The owner's summary

    /// The summary the page shows: what's being edited, else the owner's saved copy, else the agent's.
    func summary(of meeting: String) -> Results.Summary? { drafts[meeting] ?? summaries[meeting] ?? results[meeting]?.summary }

    func isEdited(_ meeting: String) -> Bool { summaries[meeting] != nil || drafts[meeting].map { $0.cleaned != results[meeting]?.summary } == true }

    /// An edit, as the owner types: shown at once, saved a second after the last one. The first makes their copy.
    /// With the window's `undo`, ⌘Z takes it back, as in Notes: a burst of typing is one step.
    func edit(_ meeting: String, undo: UndoManager? = nil, _ change: (inout Results.Summary) -> Void) {
        guard let shown = summary(of: meeting) else { return }
        var found = shown
        change(&found)
        guard found != shown else { return }  // a field writing back what it shows, as it first draws, isn't an edit
        if let undo, Date().timeIntervalSince(lastEdit[meeting] ?? .distantPast) > 1.5 { undoable(meeting, back: shown, undo) }
        lastEdit[meeting] = Date()
        drafts[meeting] = found
        editCount[meeting, default: 0] += 1
        let count = editCount[meeting]
        Task {
            try? await Task.sleep(for: .seconds(1))
            if count == editCount[meeting] { await saveSummary(meeting) }
        }
    }

    private var lastEdit: [String: Date] = [:]  // when each meeting's summary was last edited: a burst is one undo

    /// ⌘Z: the summary as it was; and ⇧⌘Z, registered as it undoes, the summary as it is now.
    private func undoable(_ meeting: String, back summary: Results.Summary, _ undo: UndoManager) {
        undo.registerUndo(withTarget: self) { model in
            guard let now = model.summary(of: meeting) else { return }
            model.lastEdit[meeting] = nil
            model.edit(meeting) { $0 = summary }
            model.undoable(meeting, back: now, undo)
        }
        undo.setActionName("Edit Summary")
    }

    /// Saves the edits now, and on leaving the meeting (`leaving`) lets go of points added but never typed in.
    func saveSummary(_ meeting: String, leaving: Bool = false) async {
        guard let draft = drafts[meeting], !savingSummary.contains(meeting) else { return }
        let cleaned = draft.cleaned
        // A point just added and not typed in yet isn't a change: nothing to save, and it stays to be typed in.
        guard cleaned != (summaries[meeting] ?? results[meeting]?.summary) else {
            if leaving { drafts[meeting] = nil }
            return
        }
        savingSummary.insert(meeting)  // before any wait: one save at a time, so an older one never lands last
        defer { savingSummary.remove(meeting) }
        guard let api = await api(for: "save your changes") else { return }
        let count = editCount[meeting]
        let generation = generation
        do {
            try await api.save(summary: cleaned, of: meeting)
            guard generation == self.generation else { return }
            summaries[meeting] = cleaned
            saves[meeting, default: 0] += 1
            if count == editCount[meeting], draft == cleaned || leaving { drafts[meeting] = nil }  // nothing typed since: the saved copy is it
            if meetings.first(where: { $0.meetingId == meeting })?.title != cleaned.title {
                retitle(meeting, cleaned.title)
                changes += 1  // a sync that read the list's old title before this mustn't put it back
            }
        } catch {
            if generation == self.generation { report(error, "save your changes") }
        }
        savingSummary.remove(meeting)  // before saving again, which waits for it
        if drafts[meeting] != nil, count != editCount[meeting] { await saveSummary(meeting, leaving: leaving) }  // typed while it saved
    }

    /// Back to the agent's summary: the owner's copy goes.
    func restoreSummary(_ meeting: String) async {
        guard let api = await api(for: "restore the summary") else { return }
        do {
            try await api.restoreSummary(of: meeting)
            saves[meeting, default: 0] += 1
            editCount[meeting, default: 0] += 1  // a save scheduled before this doesn't bring the copy back
            drafts[meeting] = nil
            summaries[meeting] = nil
            if let title = results[meeting]?.summary.title { retitle(meeting, title) }
        } catch {
            report(error, "restore the summary")
        }
    }

    private func retitle(_ meeting: String, _ title: String) {
        if let i = meetings.firstIndex(where: { $0.meetingId == meeting }) { meetings[i].title = title }
    }

    func isPending(_ meeting: String, _ speaker: String) -> Bool { pending.contains("\(meeting)/\(speaker)") }

    func save(_ form: PersonForm) async {
        guard !saving else { return }
        guard let api = await api() else {  // the sheet covers the banner: say it there
            noAPI()
            form.error = auth.state == .signedIn ? offlineMessage("save this person") : "Sign in to save this person."
            return
        }
        saving = true
        defer { saving = false }
        let generation = generation
        do {
            try await api.update(person: form.personId, ["name": form.name, "role": form.role, "company": form.company, "is_owner": form.isOwner])
            changes += 1
            if editing === form { editing = nil }  // the sheet this save was for, not one opened since
            if let fresh = try? await api.people(), generation == self.generation { people = fresh }
        } catch {
            if editing === form {  // the sheet covers the banner: say it there
                if error is API.Offline { offline = true }
                form.error = error is API.Offline ? offlineMessage("save this person") : error.localizedDescription
            } else {
                report(error, "save this person")
            }
        }
    }

    func delete(person: API.Person) async {
        guard let api = await api(for: "delete this person") else { return }
        do {
            try await api.delete(person: person.personId)
            changes += 1
            people.removeAll { $0.personId == person.personId }
            if editing?.personId == person.personId { editing = nil }
        } catch {
            report(error, "delete this person")
        }
    }

    func signIn() async {
        do {
            try await auth.signIn()
            problem = nil
            await sync()
        } catch let error as ASWebAuthenticationSessionError where error.code == .canceledLogin {
            // closed the sheet: nothing to say
        } catch {
            problem = Problem(message: error.localizedDescription)
        }
    }

    func signOut() {
        auth.signOut()
        forgetAccount()
    }

    /// Everything of the signed-in person's, gone from the window, after Sign Out or when the session ends. A sync
    /// still on its way finds the generation changed and drops what it brings back. Recordings that account has
    /// already (its cloud has their audio) go from this Mac too; ones still waiting to be sent stay, and go to
    /// whoever signs in next: on a Mac of one's own, the same person.
    private func forgetAccount() {
        for recording in recordings where [.uploaded, .sent].contains(recording.state) { forget(recording.id) }
        for var recording in recordings where recording.error != nil && recording.state == .saved {
            recording.error = nil  // nothing tries again until someone signs in
            update(recording)
        }
        generation += 1
        folderProblem = nil
        meetings = []
        people = []
        results = [:]
        loadedAt = [:]
        loadErrors = [:]
        summaries = [:]
        drafts = [:]
        files = [:]
        notFiled = []
        try? FileManager.default.removeItem(at: Self.fileCache)
        if case .meeting(let id) = selection, !recordings.contains(where: { $0.id == id }) { selection = nil }  // a recording's page stays
        folders = []
        place = .meetings
        query = ""
        player.stop()
    }

    private var generation = 0

    /// The API for something the owner just asked for, or nil with a word on why not: offline, or signed out.
    private func api(for action: String) async -> API? {
        if let api = await api() { return api }
        noAPI()
        problem = auth.state == .signedIn ? Problem(message: offlineMessage(action), offline: true) : Problem(message: "Sign in to \(action).")
        return nil
    }

    /// Something the owner asked for that didn't happen, said plainly. Offline, it isn't kept for later: they ask again.
    private func report(_ error: Error, _ action: String) {
        if error is API.Offline { offline = true }
        problem = error is API.Offline ? Problem(message: offlineMessage(action), offline: true) : Problem(message: error.localizedDescription)
    }

    private func offlineMessage(_ action: String) -> String {
        "You're offline, so Tally couldn't \(action). Try again when you're back online."
    }

    private func api() async -> API? {
        guard let token = await auth.token() else {
            // The session ended: what's left of that account goes, also when it ended before this launch's first sync.
            if auth.state == .signedOut, !meetings.isEmpty || !people.isEmpty || recordings.contains(where: { [.uploaded, .sent].contains($0.state) }) {
                forgetAccount()
            }
            return nil
        }
        return API(base: config.api, token: token)
    }

    // MARK: - Sync

    private func syncLoop() async {
        while true {
            await sync()
            let busy = recordings.contains { [.saved, .uploaded].contains($0.state) }
                || meetings.contains { ["uploading", "queued", "processing"].contains($0.state) || $0.isRewriting }
            try? await Task.sleep(for: .seconds(busy ? 15 : 60))
        }
    }

    func sync() async {
        guard !syncing else {  // claimed before the first await, so two syncs never send one recording twice
            syncAgain = true  // a sign-in, a stop: worth one more as soon as this one ends
            return
        }
        syncing = true
        defer {
            syncing = false
            if syncAgain {
                syncAgain = false
                Task { await sync() }
            }
        }
        let generation = generation

        for recording in recordings where recording.state == .saved || recording.state == .uploaded {
            guard let api = await api() else { return noAPI() }  // a fresh token for each: uploads take time
            guard generation == self.generation else { return }  // signed out meanwhile
            await send(recording.id, api)
        }

        guard let api = await api() else { return noAPI() }
        let changes = changes
        let raised = problem  // an offline problem raised while this sync is on its way is news: it stays
        do {
            let fresh = try await api.meetings()
            let freshPeople = try await api.people()
            let freshFolders = try await api.folders()
            // Signed out meanwhile, or the owner changed something after these were read: the next sync reads again.
            guard generation == self.generation, changes == self.changes else { return }
            offline = false
            syncError = nil
            if problem == raised, raised?.offline == true { problem = nil }
            let ready = fresh.filter { m in m.state == "ready" && meetings.contains { $0.meetingId == m.meetingId && $0.state != "ready" } }
            // A summary rewritten without background voices since the last sync: read it again
            let rewritten = fresh.filter { m in m.rewriting == nil && meetings.contains { $0.meetingId == m.meetingId && $0.rewriting != nil } }
            meetings = fresh
            people = freshPeople
            if renamingFolder == nil { folders = freshFolders }  // not under a name being typed
            for meeting in ready {
                await load(meeting.meetingId)
                notifier.ready(meeting, toConfirm: toConfirm(meeting.meetingId))
            }
            for meeting in rewritten {
                summaries[meeting.meetingId] = nil  // the owner's copy went with the rewrite, if there was one
                await reload(meeting.meetingId)
                notifier.rewritten(meeting)
            }
            // Done with once its meeting isn't on its way: ready, or not listed because it was deleted on another device
            // (its row is made before the upload starts)
            for recording in recordings where [.sent, .uploaded].contains(recording.state)
                && !fresh.contains(where: { $0.meetingId == recording.id && $0.state != "ready" }) {
                recording.remove()
                recordings.removeAll { $0.id == recording.id }
            }
            if case .meeting(let id) = selection { await load(id) }
            // The newest ready meetings' results, once: their counts of people to confirm show in the menu and the list.
            for meeting in fresh.prefix(10) where meeting.state == "ready" && results[meeting.meetingId] == nil { await load(meeting.meetingId) }
            Task { await keepMeetingsFolder() }  // its first fill can take minutes: never in the way of sending recordings
        } catch {
            offline = error is API.Offline  // the next sync tries again
            syncError = offline ? nil : (error as? API.Failure).flatMap(\.reason) ?? "It tries again shortly."
        }
    }

    /// No token: signed out, or a refresh that couldn't reach the provider, which means offline.
    private func noAPI() {
        if auth.state == .signedIn { offline = true }
    }

    /// One recording to the API, in steps that each survive a failure: the audio (which starts the pipeline), then
    /// "uploaded". A step that fails is tried again at the next sync, from where it stopped.
    private func send(_ id: String, _ api: API) async {
        // The recording as it is now: deleted (or being deleted) meanwhile, it isn't sent.
        guard !deletingIds.contains(id), var recording = recordings.first(where: { $0.id == id }),
              [.saved, .uploaded].contains(recording.state) else { return }
        let generation = generation
        // Signed out while a step was on its way: that account has the recording now, so it goes from this Mac too.
        func signedOutMeanwhile() -> Bool {
            guard generation != self.generation else { return false }
            forget(id)
            return true
        }
        do {
            if recording.state == .saved {
                recording.state = .uploading
                recording.error = nil  // trying again: the last attempt's error isn't news any more
                update(recording)
                do {
                    try await api.upload(recording, appVersion: appVersion)
                } catch is API.AlreadySent {
                    if signedOutMeanwhile() { return }
                    recording.state = .sent
                    update(recording)
                    return
                }
                if signedOutMeanwhile() { return }  // not deleted meanwhile: an item that's uploading can't be
                recording.state = .uploaded
                update(recording)
            }
            // A fresh token: an hour's audio takes longer to go up than one may have left
            guard let fresh = await self.api() else { throw API.Offline() }
            try await fresh.uploaded(recording.id)
            if signedOutMeanwhile() { return }
            recording.state = .sent
            recording.error = nil
        } catch {
            if recording.state == .uploading { recording.state = .saved }
            // Signed in still: it tries again. Signed out meanwhile, nothing does until someone signs in.
            recording.error = generation == self.generation ? "Couldn't send it yet. Tally tries again shortly." : nil
        }
        update(recording)
    }

    private var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }
}

/// Who a speaker is, as the owner is about to say it.
@Observable final class AssignForm: Identifiable {
    let meeting: String
    let speaker: String
    var query = "" { didSet { highlight = 0 } }
    var role = ""
    var company = ""
    var highlight = 0  // the row ↑ and ↓ have reached, which Return picks: the people listed, then Add
    var sent = false  // once: Return in a field and the default button can both answer the same press
    let rejected: String?  // the person the owner just said No to: not offered again
    var id: String { meeting + speaker }

    init(meeting: String, speaker: String, rejected: String? = nil) {
        self.meeting = meeting
        self.speaker = speaker
        self.rejected = rejected
    }
}

/// A person as the owner edits them.
@Observable final class PersonForm: Identifiable {
    let personId: String
    var name: String
    var role: String
    var company: String
    var isOwner: Bool
    var error: String?
    var id: String { personId }

    init(_ person: API.Person) {
        personId = person.personId
        name = person.name
        role = person.role ?? ""
        company = person.company ?? ""
        isOwner = person.isOwner
    }
}

/// "Summary ready" notifications; clicking one opens that meeting.
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    override init() {
        super.init()
        UNUserNotificationCenter.current().delegate = self
    }

    /// Asks for permission when it first means something, at a recording's stop (macOS asks only once).
    func askForPermission() {
        Task { _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) }
    }

    /// The summary written again without background voices: said, since the owner may have moved on.
    func rewritten(_ meeting: API.Meeting) {
        let content = UNMutableNotificationContent()
        content.title = meeting.title ?? "Your meeting"
        content.body = meeting.rewriteError == nil ? "The summary is rewritten, without the background voices." : "The summary couldn't be rewritten. Try again from the meeting."
        content.userInfo = ["meeting": meeting.meetingId]
        Task { try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "rewritten-" + meeting.meetingId, content: content, trigger: nil)) }
    }

    func ready(_ meeting: API.Meeting, toConfirm: Int) {
        let content = UNMutableNotificationContent()
        content.title = meeting.title ?? "Your meeting is summarised"
        content.body = toConfirm == 0 ? "The summary is ready." : "The summary is ready. \(AppModel.toConfirmText(toConfirm))."
        content.userInfo = ["meeting": meeting.meetingId]
        Task { try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: meeting.meetingId, content: content, trigger: nil)) }
    }

    /// The call a recording followed has ended, and Tally stopped it; the menu can still continue it.
    func callEnded(_ app: String?) {
        let content = UNMutableNotificationContent()
        content.title = "Recording stopped"
        let call = app.map { "The \($0) call" } ?? "The call"
        content.body = "\(call) ended. Tally sends it in 30 seconds; Continue Recording in its menu, if it hasn't."
        Task { try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "call-ended", content: content, trigger: nil)) }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard let id = response.notification.request.content.userInfo["meeting"] as? String else { return }
        await MainActor.run { _ = NSWorkspace.shared.open(URL(string: "tally://meeting/\(id)")!) }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async
        -> UNNotificationPresentationOptions { [.banner, .sound] }
}
