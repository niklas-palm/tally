import SwiftUI

/// The menu bar panel. Idle: one button to start, and what's on its way. Recording: the time, the two levels,
/// stop or discard.
struct MenuView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let stopped = model.stoppedByCall {
                StoppedByCall(app: stopped.app)
            } else if let recorder = model.recorder, let recording = model.recording {
                RecordingPanel(recorder: recorder, since: recording.startedAt)
            } else {
                Button { Task { await model.startRecording() } } label: {
                    Label("Start Recording", systemImage: "record.circle").frame(maxWidth: .infinity)
                }
                .controlSize(.extraLarge).buttonStyle(.borderedProminent).tint(.red)
                .keyboardShortcut("r").disabled(model.starting)
            }
            Notices(inWindow: false)

            let recent = model.items.filter { $0.status != .recording }.prefix(4)
            if !recent.isEmpty {
                Divider()
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(recent)) { item in
                        Button { show(item.id) } label: { MeetingRow(item: item, toConfirm: model.toConfirm(item.id)) }
                            .buttonStyle(.menuRow)
                    }
                }
                .padding(.horizontal, -6)  // the rows' highlight reaches past their text, which lines up with the rest
            }
            Divider()
            Toggle("Open at login", isOn: Binding(get: { model.opensAtLogin }, set: { on in Task { await model.setOpensAtLogin(on) } }))
                .toggleStyle(.checkbox)
            // A copy of the meetings in a folder, for agents on this Mac: the signed-in person's own
            if model.auth.state == .signedIn {
                if let folder = model.meetingsFolder {
                    Menu {
                        Button("Show in Finder") { NSWorkspace.shared.open(folder); dismiss() }
                        Button("Change Folder…") { dismiss(); model.chooseMeetingsFolder() }
                        Divider()
                        Button("Stop Keeping a Copy") { model.meetingsFolder = nil }  // what's there stays: it's the owner's
                    } label: {
                        Label("Meetings copied to \(folder.lastPathComponent)", systemImage: "folder").lineLimit(1).truncationMode(.middle)
                    }
                    .menuStyle(.borderlessButton).help(folder.path)
                } else {
                    Button("Keep a Copy of My Meetings in a Folder…") { dismiss(); model.chooseMeetingsFolder() }
                        .buttonStyle(.borderless)
                        .help("Summaries, transcripts and files as Markdown and JSON, kept up to date: for you and the agents on your Mac")
                }
            }
            // The question as the label, what happens as the options, and a sentence for the one chosen.
            VStack(alignment: .leading, spacing: 4) {
                Picker("When a call starts", selection: Bindable(model).callMode) {
                    ForEach(AppModel.CallMode.allCases, id: \.self) { Text($0.title).tag($0) }
                }
                .pickerStyle(.menu).fixedSize()
                Text(model.callMode.explanation)
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Button("Open Tally") { show(nil) }.keyboardShortcut("o")
                Spacer()
                if model.auth.state == .signedIn {
                    Menu("Account") {
                        if let account = model.auth.account { Text(account) }
                        Button("Sign Out") { model.signOut() }
                    }.menuStyle(.borderlessButton).fixedSize()
                }
                Button(model.recorder == nil ? "Quit Tally" : "Stop and Quit") { NSApp.terminate(nil) }.keyboardShortcut("q")
            }
            .buttonStyle(.borderless)
        }
        .padding(16)
        .frame(width: 340)
        .onAppear {  // up to date when looked at, not at the next tick: meetings, and whether there's a new Tally
            Task { await model.sync() }
            Task { await model.checkForUpdate() }
            Task { await model.checkLoginItem() }
        }
    }

    private func show(_ meeting: String?) {
        if let meeting { model.open(meeting) }
        openWindow(id: "main")
        NSApp.activate()
        dismiss()
    }
}

private struct RecordingPanel: View {
    @Environment(AppModel.self) private var model
    let recorder: Recorder
    let since: Date

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline) {
                    Image(systemName: "record.circle.fill").foregroundStyle(.red).symbolEffect(.pulse)
                    Text(since, style: .timer).font(.system(size: 30, weight: .semibold, design: .rounded).monospacedDigit())
                    Spacer()
                }
                // What it's recording, so auto mode's catch is plain at a glance
                Text(model.recordingCall.map { $0.isEmpty ? "Recording the call" : "Recording the \($0) call" } ?? "Recording in the room")
                    .font(.callout).foregroundStyle(.secondary)
            }
            TimelineView(.periodic(from: .now, by: 0.1)) { context in
                let levels = recorder.levels
                VStack(alignment: .leading, spacing: 6) {
                    LevelBar(label: "Microphone", dB: levels.mic)
                    LevelBar(label: "System audio", dB: levels.system)
                    if levels.lost {
                        Text("No microphone is connected, so nothing is being recorded. Connect one and recording carries on.")
                            .font(.caption).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                    // A tap without the system-audio permission records silence, and says nothing.
                    } else if context.date.timeIntervalSince(since) > 60,
                       context.date.timeIntervalSince(levels.lastSystemSound ?? since) > 60 {
                        QuietSystemAudio()
                    }
                }
            }
            HStack {
                Button { Task { await model.stopRecording() } } label: {
                    Label("Stop Recording", systemImage: "stop.fill").frame(maxWidth: .infinity)
                }
                .controlSize(.large).buttonStyle(.borderedProminent).keyboardShortcut("r")
                Button(model.discardArmed ? "Click Again to Discard" : "Discard", role: .destructive) { model.discardRecording() }
                    .controlSize(.large)
            }
        }
    }
}

private struct QuietSystemAudio: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("No sound from your Mac in the last minute. In a call? Tally may not be allowed to record system audio.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Button("Open Settings") { NSWorkspace.shared.open(AppModel.systemAudioSettings) }
                .font(.caption).buttonStyle(.link)
        }
    }
}

private struct LevelBar: View {
    let label: String
    let dB: Float

    var body: some View {
        HStack(spacing: 8) {
            Text(label).font(.caption).foregroundStyle(.secondary).frame(width: 84, alignment: .leading)
            Gauge(value: Double(max(0, min(1, (dB + 60) / 60)))) { Text(label) }
                .gaugeStyle(.accessoryLinearCapacity).tint(.green).labelsHidden()
                .accessibilityLabel("\(label) level")
        }
    }
}

struct SignInPrompt: View {
    @Environment(AppModel.self) private var model
    var explains = true  // the welcome's step says why itself

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if explains {
                Text(Self.explanation(model)).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Button(model.auth.state == .signingIn ? "Signing In…" : "Sign In to \(Config.shared.signInName)") { Task { await model.signIn() } }
                .disabled(model.auth.state == .signingIn)
        }
    }

    /// Why to sign in, now: shared with the window's banner.
    static func explanation(_ model: AppModel) -> String {
        let waiting = model.waitingForSignIn
        if model.auth.expired { return "Your sign-in has expired. Sign in again so Tally can keep summarising your meetings." }
        if waiting > 0 { return waiting == 1 ? "1 recording is waiting to be sent. Sign in to have it summarised." : "\(waiting) recordings are waiting to be sent. Sign in to have them summarised." }
        return "Recording works without signing in. Sign in to have your meetings summarised."
    }
}

/// One meeting in a list: its title or when it was recorded, and where it is.
struct MeetingRow: View {
    let item: AppModel.Item
    var toConfirm = 0

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            StatusIcon(status: item.status)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title ?? "Recording at \(item.startedAt.formatted(date: .omitted, time: .shortened))").lineLimit(1)
                Text(subtitle).font(.caption).foregroundStyle(item.sendError == nil ? .secondary : Color.orange).lineLimit(1)
            }
            Spacer(minLength: 0)
            if toConfirm > 0 { ToConfirmBadge(count: toConfirm).accessibilityLabel(AppModel.toConfirmText(toConfirm)) }
        }
        .padding(.vertical, 3)
        .accessibilityElement(children: .combine)
    }

    private var subtitle: String {
        let when = Calendar.current.isDateInToday(item.startedAt)
            ? item.startedAt.formatted(date: .omitted, time: .shortened)
            : item.startedAt.formatted(.dateTime.day().month().hour().minute())
        let length = item.duration.map { " · " + duration($0) } ?? ""
        if let error = item.sendError { return error }
        return switch item.status {
        case .ready: when + length
        case .failed(let error): error
        default: "\(statusText(item.status))\(length)"
        }
    }
}

/// The orange count of people to confirm, beside a meeting.
struct ToConfirmBadge: View {
    let count: Int

    var body: some View {
        Text("\(count)").font(.caption2.bold()).foregroundStyle(.white)
            .padding(.horizontal, 6).padding(.vertical, 1).background(.orange, in: .capsule)
            .help(AppModel.toConfirmText(count))
    }
}

struct StatusIcon: View {
    let status: AppModel.Status

    var body: some View {
        Group {
            switch status {
            case .ready: Image(systemName: "text.document").foregroundStyle(.secondary)
            case .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            case .recording: Image(systemName: "record.circle.fill").foregroundStyle(.red)
            case .waiting: Image(systemName: "icloud.slash").foregroundStyle(.secondary)
            default: ProgressView().controlSize(.small)
            }
        }
        .frame(width: 18)
        .accessibilityLabel(statusText(status))
    }
}

func statusText(_ status: AppModel.Status) -> String {
    switch status {
    case .recording: "Recording"
    case .saving: "Saving…"
    case .waiting: "Waiting to be sent"
    case .uploading: "Sending…"
    case .queued: "Getting ready to summarise…"
    case .processing: "Summarising…"
    case .ready: "Ready"
    case .failed(let error): error
    }
}

func duration(_ seconds: Double) -> String {
    let units: Set<Duration.UnitsFormatStyle.Unit> = seconds < 59.5 ? [.seconds] : [.hours, .minutes]  // by what it rounds to: never "60 sec"
    return Duration.seconds(seconds).formatted(.units(allowed: units, width: .abbreviated, fractionalPart: .hide(rounded: .toNearestOrAwayFromZero)))
}

/// A list row in the menu that highlights under the pointer, like a menu item.
struct MenuRowStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Hovering { hovering in
            configuration.label
                .padding(.horizontal, 6)
                .background(hovering || configuration.isPressed ? Color.primary.opacity(0.08) : .clear, in: .rect(cornerRadius: 6))
                .contentShape(.rect)
        }
    }
}

extension ButtonStyle where Self == MenuRowStyle {
    static var menuRow: MenuRowStyle { MenuRowStyle() }
}

/// Tracks the pointer.
private struct Hovering<Content: View>: View {
    private let inside = State(initialValue: false)
    let content: (Bool) -> Content

    init(@ViewBuilder content: @escaping (Bool) -> Content) { self.content = content }

    var body: some View {
        content(inside.wrappedValue).onHover { inside.wrappedValue = $0 }
    }
}

/// The call a recording followed has ended, and Tally stopped it: carry on in the same recording, or send it now.
/// 30 seconds later it's sent by itself.
struct StoppedByCall: View {
    @Environment(AppModel.self) private var model
    let app: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(app.map { "Stopped when the \($0) call ended" } ?? "Stopped when the call ended", systemImage: "stop.circle")
                .font(.headline)
            Text("Still in the call? Continue, and it stays one recording. Otherwise it's sent in 30 seconds.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Continue Recording") { model.continueRecording() }.buttonStyle(.borderedProminent).tint(.red)
                Button("Send Now") { Task { await model.stopRecording() } }
            }
            .controlSize(.large)
        }
    }
}

/// What needs saying, a notice each, in the menu and under the window's toolbar alike: an icon, one sentence, and
/// the buttons that answer it (at the end of the line in the window; under the sentence in the narrow menu). A page
/// that asks to sign in itself (`asksToSignIn` false) isn't asked for twice.
struct Notices: View {
    @Environment(AppModel.self) private var model
    let inWindow: Bool
    var asksToSignIn = true

    var body: some View {
        if let problem = model.problem {
            notice("exclamationmark.circle.fill", .orange, problem.message) {
                if let settings = problem.settings { Button("Open Settings") { NSWorkspace.shared.open(settings) } }
                Button("Dismiss") { model.problem = nil }
            }
        }
        if let problem = model.folderProblem {
            notice("folder.badge.questionmark", .orange, problem) {
                Button("Change Folder…") { model.chooseMeetingsFolder() }
            }
        }
        if model.update != nil {
            notice("arrow.down.circle.fill", .accentColor, model.updating ? "Updating Tally: it reopens as the new version in a minute or two."
                   : model.recordingOrSaving ? "A new version of Tally is ready. Update after this recording." : "A new version of Tally is ready.") {
                if model.updating {
                    ProgressView().controlSize(.small)  // a spinner, not a dimmed button: it's working
                } else if !model.recordingOrSaving {
                    Button("Update") { model.installUpdate() }.buttonStyle(.borderedProminent)
                }
            }
        }
        if model.crash != nil {
            let signedIn = model.auth.state == .signedIn
            notice("exclamationmark.triangle.fill", .orange, signedIn ? "Tally quit unexpectedly last time. Send the report so it can be fixed: it holds no meeting content."
                   : "Tally quit unexpectedly last time. Sign in to send the report so it can be fixed.") {
                Button("Don't Send") { model.dismissCrash() }
                Button(model.sendingCrash ? "Sending…" : "Send Report") { Task { await model.sendCrash() } }
                    .buttonStyle(.borderedProminent).disabled(!signedIn || model.sendingCrash)
            }
        }
        if model.auth.state != .signedIn {
            if asksToSignIn {
                notice("person.crop.circle.badge.exclamationmark", .secondary, SignInPrompt.explanation(model)) {
                    Button(model.auth.state == .signingIn ? "Signing In…" : "Sign In to \(Config.shared.signInName)") { Task { await model.signIn() } }
                        .buttonStyle(.borderedProminent).disabled(model.auth.state == .signingIn)
                }
            }
        } else if model.offline {
            notice("wifi.slash", .secondary, "You're offline. Tally carries on when you're back online.") { EmptyView() }
        } else if let error = model.syncError {  // it retries by itself: nothing to do but wait, or sign in again when it says so
            notice("exclamationmark.triangle", .secondary, "Tally couldn't update your meetings. \(error)") { EmptyView() }
        }
    }

    @ViewBuilder private func notice(_ icon: String, _ tint: Color, _ text: String, @ViewBuilder actions: () -> some View) -> some View {
        if inWindow {
            HStack(spacing: 10) {
                Image(systemName: icon).foregroundStyle(tint)
                Text(text).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 12)
                HStack { actions() }.controlSize(.small)
            }
            .font(.callout)
            .padding(.horizontal, 16).padding(.vertical, 8)
        } else {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: icon).foregroundStyle(tint)
                VStack(alignment: .leading, spacing: 6) {
                    Text(text).fixedSize(horizontal: false, vertical: true)
                    HStack { actions() }.controlSize(.small)
                }
            }
            .font(.callout)
        }
    }
}
