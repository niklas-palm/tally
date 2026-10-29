import AppKit
import CoreAudio

/// Calls, so a recording isn't forgotten:
/// - A call is another app on the microphone for 10 s: Tally offers to record it, or in auto mode records it.
/// - A call ends when its apps have been off the microphone for 30 s.
/// - A recording that followed the call also waits for them to leave the speakers (a muted call still plays the
///   others), then stops, and can be continued for 30 s.
/// - A call starting in those 30 s asks whether it's the same meeting or a new one.
/// - Without a recording the speakers don't count: many apps keep theirs open between calls.
///
/// `CallRules` decides from what's using audio and the time, so it's tested without Core Audio. `AudioUsers` reads
/// Core Audio's list of processes using audio (macOS 14.2 and later, no permission needed).
struct CallRules {
    static let startsAfter: TimeInterval = 10  // keeps out Siri, dictation and quick voice notes
    static let endsAfter: TimeInterval = 30
    // With nothing recording and nothing declined, the 30 s has nothing to guard: a call is over after this, so the
    // next call (after a recording discarded mid-call, say) counts as one of its own
    static let endsSoonAfter: TimeInterval = 5
    static let continuable: TimeInterval = 30  // how long Continue Recording is offered after a call ends

    enum Recording: Equatable {
        case none
        case plain  // started with no call on: it never stops by itself
        case following  // started during a call: it stops when the call ends
        case stopped(since: Date)  // stopped by the call's end, and can still be continued
        case asking  // stopped, and a new call started: is it the same meeting?
    }

    enum Event: Equatable {
        case offer(pid_t, bundleID: String)  // a call has started: offer to record it, naming this app
        case stop  // the call a recording followed has ended
        case again(pid_t, bundleID: String)  // a call started while stopped: ask whether it's the same meeting
        case finish  // a stopped recording wasn't continued in time
    }

    var offering = true  // the owner's setting
    private(set) var recording = Recording.none
    private(set) var call: Set<pid_t>?  // the call's apps, once a call has started
    private var micSince: [pid_t: Date] = [:]
    private var quietSince: Date?
    private var declined = false  // Not Now, until the call ends

    /// What's using audio now (other apps only), and what that means.
    mutating func observe(_ users: [AudioUser], at now: Date) -> [Event] {
        var events: [Event] = []
        let onMic = Set(users.filter(\.input).map(\.pid))
        micSince = onMic.reduce(into: [:]) { since, pid in since[pid] = micSince[pid] ?? now }

        if var apps = call {
            apps.formUnion(onMic)  // a call's audio can move to another of its processes
            call = apps
            let following = recording == .following
            if users.contains(where: { apps.contains($0.pid) && ($0.input || (following && $0.output)) }) {
                quietSince = nil  // recording, muted still counts: the call app keeps playing the others' voices
            } else if now.timeIntervalSince(quietSince ?? now) >= (recording == .none && !declined ? Self.endsSoonAfter : Self.endsAfter) {
                call = nil
                quietSince = nil
                declined = false
                if following {
                    recording = .stopped(since: now)
                    events.append(.stop)
                }
            } else if quietSince == nil {
                quietSince = now
            }
        } else if let first = micSince.filter({ now.timeIntervalSince($0.value) >= Self.startsAfter }).min(by: { $0.value < $1.value }) {
            call = onMic
            let bundleID = users.first { $0.pid == first.key }?.bundleID ?? ""
            if case .stopped = recording {
                recording = .asking
                events.append(.again(first.key, bundleID: bundleID))
            } else if offering, recording == .none, !declined {
                events.append(.offer(first.key, bundleID: bundleID))
            }
        }

        // Not while something's on the mic: it may be a call that hasn't had its 10 s yet
        if case .stopped(let since) = recording, now.timeIntervalSince(since) >= Self.continuable, micSince.isEmpty {
            recording = .none
            events.append(.finish)
        }
        return events
    }

    /// A recording started, from the offer or Start Recording: it follows a call when another app was on the
    /// microphone as it started, even in the call's first 10 s, before any offer.
    mutating func started() {
        if call == nil, !micSince.isEmpty { call = Set(micSince.keys) }
        recording = call == nil ? .plain : .following
    }

    /// Continue Recording after the call's end, or The Same Meeting: it follows the call again, when there's one.
    mutating func continued() { recording = .following }

    /// Stopped, sent or discarded.
    mutating func ended() { recording = .none }

    /// Not Now: no more offers for this call.
    mutating func notNow() { declined = true }
}

/// A process using audio: whether it's taking the microphone's input, and whether it's playing.
struct AudioUser: Equatable {
    let pid: pid_t
    let input: Bool
    let output: Bool
    var bundleID = ""
}

enum AudioUsers {
    /// Every other process using audio now: Tally's own (its tap and its microphone) never counts.
    static func now() -> [AudioUser] {
        let ownPID = getpid()
        return processes().compactMap { process -> AudioUser? in
            guard let pid: pid_t = value(process, kAudioProcessPropertyPID), pid != ownPID else { return nil }
            let input: UInt32 = value(process, kAudioProcessPropertyIsRunningInput) ?? 0
            let output: UInt32 = value(process, kAudioProcessPropertyIsRunningOutput) ?? 0
            return input == 0 && output == 0 ? nil : AudioUser(pid: pid, input: input != 0, output: output != 0, bundleID: bundleID(process))
        }
    }

    /// What to call the app, for the offer and the banner. Audio often comes from a helper process: its parent
    /// app if it has one ("Google Chrome Helper" is Chrome's child), the app whose bundle id it extends
    /// (`com.tinyspeck.slackmacgap.helper` is Slack's, measured), or else its own name without the helper's suffix
    /// (WebKit's "Microsoft Outlook Graphics and Media" is an XPC service, not a child). Only a label.
    static func name(of pid: pid_t, bundleID: String) -> String? {
        var parts = bundleID.split(separator: ".")
        while parts.count > 1 {
            if let owner = NSRunningApplication.runningApplications(withBundleIdentifier: parts.joined(separator: "."))
                .first(where: { $0.activationPolicy == .regular }) { return owner.localizedName }
            parts.removeLast()
        }
        guard let app = NSRunningApplication(processIdentifier: pid) else { return nil }
        if app.activationPolicy == .regular { return app.localizedName }
        if let parent = parent(of: pid), parent > 1, let owner = NSRunningApplication(processIdentifier: parent),
           owner.activationPolicy == .regular {
            return owner.localizedName
        }
        guard var name = app.localizedName else { return nil }
        for suffix in [" Graphics and Media", " Web Content", " Networking", " Helper (Renderer)", " Helper (GPU)", " Helper"] {
            name = name.replacingOccurrences(of: suffix, with: "")
        }
        return name
    }

    private static func bundleID(_ process: AudioObjectID) -> String {
        var address = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyBundleID, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<CFString?>.size)
        var value: Unmanaged<CFString>?
        guard AudioObjectGetPropertyData(process, &address, 0, nil, &size, &value) == noErr, let value else { return "" }
        return value.takeRetainedValue() as String
    }

    private static func parent(of pid: pid_t) -> pid_t? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0, size > 0 else { return nil }
        return info.kp_eproc.e_ppid
    }

    private static func processes() -> [AudioObjectID] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let system = AudioObjectID(kAudioObjectSystemObject)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func value<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> T? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<T>.size)
        let pointer = UnsafeMutablePointer<T>.allocate(capacity: 1)
        defer { pointer.deallocate() }
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer) == noErr else { return nil }
        return pointer.pointee
    }
}
