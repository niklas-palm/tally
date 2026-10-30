import SwiftUI

/// One meeting. Until it's ready: where it is and what happens next. Once ready: who was there (with anyone to
/// confirm first), then the summary, the transcript, or the files kept with it.
struct MeetingView: View {
    @Environment(AppModel.self) private var model
    let item: AppModel.Item
    private let tab = State(initialValue: Tab.summary)
    private let copied = State(initialValue: false)  // "Copied" for a moment after Copy Summary
    private let restoring = State(initialValue: false)  // Restore Original's confirmation

    @Environment(\.undoManager) private var undo

    enum Tab: String, CaseIterable { case summary = "Summary", transcript = "Transcript", files = "Files" }

    var body: some View {
        content.onDisappear {
            model.player.stop()  // leaving a meeting stops its clips
            Task { await model.saveSummary(item.id, leaving: true) }  // and saves what was typed, without waiting the second
        }
    }

    @ViewBuilder private var content: some View {
        if item.status == .ready, let results = model.results[item.id] {
            let names = Names(results: results, people: model.peopleById)
            let summary = model.summary(of: item.id) ?? results.summary  // the owner's copy, once they've edited
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    header(results, summary, names)
                    BackgroundNote(meeting: item.id, results: results, names: names)
                    ToConfirm(meeting: item.id, results: results, names: names)
                    switch tab.wrappedValue {
                    case .summary:
                        // Being rewritten: what's typed now would be lost to the new one, so it waits
                        let rewriting = model.meetings.first { $0.meetingId == item.id }?.isRewriting == true
                        SummaryPage(meeting: item.id, summary: summary, names: names, speakers: results.speakerOrder)
                            .allowsHitTesting(!rewriting).opacity(rewriting ? 0.6 : 1)
                    case .transcript: TranscriptPage(meeting: item.id, results: results, names: names)
                    case .files: FilesPage(meeting: item.id)
                    }
                }
                .frame(maxWidth: 780, alignment: .leading)
                .padding(32)
                .frame(maxWidth: .infinity)
            }
            .task { await model.loadFiles(item.id) }  // for the tab's count
            .toolbar {
                // The page's views over it, as Notes and Mail keep theirs: they don't scroll away, and ⌘1 ⌘2 ⌘3 reach them
                ToolbarItem(placement: .principal) {
                    let count = model.files[item.id]?.count ?? 0
                    Picker("View", selection: tab.projectedValue) {
                        ForEach(Tab.allCases, id: \.self) { Text($0 == .files && count > 0 ? "Files (\(count))" : $0.rawValue) }
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                ToolbarItem { copyButton(results, summary, names) }
            }
            .background {  // the views' shortcuts, as View menu items would have them
                ForEach(Array(Tab.allCases.enumerated()), id: \.offset) { index, view in
                    Button(view.rawValue) { tab.wrappedValue = view }.keyboardShortcut(KeyEquivalent(Character("\(index + 1)")), modifiers: .command).hidden()
                }
            }
        } else if item.status == .ready, let error = model.loadErrors[item.id] {
            ContentUnavailableView {
                Label("This Summary Couldn't Be Opened", systemImage: "exclamationmark.triangle")
            } description: { Text(error) } actions: {
                Button("Try Again") { Task { await model.reload(item.id) } }.buttonStyle(.borderedProminent)
            }
        } else if item.status == .ready {
            ProgressView("Opening…").task { await model.load(item.id) }
        } else {
            Waiting(item: item)
        }
    }

    /// The title across the page, when it was, and who was there; then where it's filed and whether it's the owner's copy.
    private func header(_ results: Results, _ summary: Results.Summary, _ names: Names) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Editable(summary.title, names: names, placeholder: "Title", opensEmpty: false, keepsSpeakers: false) { title in model.edit(item.id, undo: undo) { $0.title = title } }
                .font(.title.bold())
            Text([longDate(item.startedAt), duration(results.duration), results.language.flatMap { Locale.current.localizedString(forLanguageCode: $0) }]
                .compactMap { $0 }.joined(separator: " · "))
                .foregroundStyle(.secondary)
            Attendees(meeting: item.id, results: results, names: names).padding(.vertical, 4)
            Flow(spacing: 14) {  // wraps in a narrow window rather than cutting off
                Menu { FolderChoices(item: item) } label: {
                    let paths = model.paths(of: item.folderIds)
                    Label(paths.isEmpty ? "Add to a Folder" : paths.joined(separator: ", "), systemImage: "folder").lineLimit(1).truncationMode(.middle)
                }
                .menuStyle(.borderlessButton).fixedSize().help("The folders this meeting is in")
                if model.isEdited(item.id) {
                    HStack(spacing: 6) {
                        Text(model.savingSummary.contains(item.id) ? "Saving…" : "Edited by you")
                        Button("Restore Original…") { restoring.wrappedValue = true }.buttonStyle(.link).foregroundStyle(.tint)  // a link looks like one
                    }
                    .confirmationDialog("Restore the original summary?", isPresented: restoring.projectedValue) {
                        Button("Restore Original", role: .destructive) { Task { await model.restoreSummary(item.id) } }
                    } message: {
                        Text("Your changes to this summary are deleted, and Tally's own summary is shown again.")
                    }
                }
            }
            .font(.callout).foregroundStyle(.secondary)
            if let suggestion = model.suggestion(for: item.id) { FilingNote(meeting: item.id, suggestion: suggestion) }
        }
    }

    /// Copy, in the toolbar: what the page shows, with names, to paste anywhere (⇧⌘C). On the Transcript tab, the
    /// transcript: text selection on a Mac stops at each paragraph.
    private func copyButton(_ results: Results, _ summary: Results.Summary, _ names: Names) -> some View {
        let transcript = tab.wrappedValue == .transcript
        return Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(transcript ? Markdown.transcript(results, names) : Markdown.summary(summary, names), forType: .string)
            copied.wrappedValue = true
            Task { try? await Task.sleep(for: .seconds(2)); copied.wrappedValue = false }
        } label: {
            Label(transcript ? "Copy Transcript" : "Copy Summary", systemImage: copied.wrappedValue ? "checkmark" : "doc.on.doc")
        }
        .keyboardShortcut("c", modifiers: [.command, .shift])
        .help(copied.wrappedValue ? "Copied" : transcript ? "Copy the transcript, with names (⇧⌘C)" : "Copy the summary, with names (⇧⌘C)")
    }
}

/// A meeting on its way, said plainly.
private struct Waiting: View {
    @Environment(AppModel.self) private var model
    let item: AppModel.Item

    var body: some View {
        switch item.status {
        case .failed(let error):
            ContentUnavailableView {
                Label(error.hasPrefix("No speech") ? "No Speech Heard" : error.hasPrefix("This recording is longer") ? "Too Long to Summarise"
                      : item.inCloud ? "This Meeting Couldn't Be Summarised" : "This Recording Couldn't Be Saved",
                      systemImage: error.hasPrefix("No speech") ? "waveform.slash" : "exclamationmark.triangle")
            } description: {
                Text(!item.inCloud && item.hasAudio ? "Its audio is still on this Mac."
                     : AppModel.Item.final(error) ? error + " Delete it, unless you'd like to keep it."  // sending it again won't change that
                     : item.hasAudio ? error + " Send it again to try once more."
                     : error + " The recording is no longer on this Mac, so it can't be sent again.")
            } actions: {
                if item.canRetry { Button(item.inCloud ? "Send Again" : "Try Again") { model.retry(item) }.buttonStyle(.borderedProminent) }
                Button("Delete…", role: .destructive) { model.deleting = item }
            }
        case .waiting where model.auth.state != .signedIn:
            ContentUnavailableView {
                Label("Waiting for You to Sign In", systemImage: "icloud.slash")
            } description: { Text("The recording is safe on this Mac. Tally summarises it once you're signed in.") } actions: { SignInPrompt() }
        case .recording where model.stoppedByCall != nil:
            ContentUnavailableView {
                Label("Stopped When the Call Ended", systemImage: "stop.circle")
            } description: {
                Text("Still in the call? Continue, and it stays one recording. Otherwise it's sent in 30 seconds.")
            } actions: {
                Button("Continue Recording") { model.continueRecording() }.buttonStyle(.borderedProminent)
                Button("Send Now") { Task { await model.stopRecording() } }
            }
        case .recording:
            // As Voice Memos shows one: the time, large, then a word on what to do, and Stop
            let elapsed = model.elapsed  // read every time: the levels aren't observed, the once-a-second time is
            VStack(spacing: 14) {
                Label("Recording", systemImage: "record.circle.fill").font(.headline).foregroundStyle(.red)
                Text(elapsed.isEmpty ? "0:00" : elapsed).font(.system(size: 56, weight: .light)).monospacedDigit()
                Text(model.recorder?.levels.lost == true
                     ? "No microphone is connected, so nothing is being recorded. Connect one and recording carries on."
                     : "Stop the recording when the meeting ends: here, or from the menu bar.")
                    .foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 360)
                Button("Stop Recording") { Task { await model.stopRecording() } }.buttonStyle(.borderedProminent).tint(.red).controlSize(.large)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        default:
            ContentUnavailableView {
                Label(statusText(item.status), systemImage: "waveform")
            } description: {
                Text(item.sendError ?? explanation)
            }
        }
    }

    private var explanation: String {
        switch item.status {
        case .processing: "Transcribing, working out who's who, and writing the summary. A few minutes more."
        case .uploading: "Sending the recording to be summarised."
        case .waiting: "Tally sends it as soon as it can."
        case .saving: "Saving the recording on this Mac."
        default: "Usually ready within a few minutes."
        }
    }
}

/// Who was there, under the date: a chip per speaker not waiting to be asked about (confirmed, not someone the
/// owner knows, or someone who said too little to ask about), in the order they first spoke, as Calendar lists attendees. A click shows them, with Listen and what can change.
private struct Attendees: View {
    let meeting: String
    let results: Results
    let names: Names

    var body: some View {
        let settled = names.participants.filter { !names.asks($0) }
        if !settled.isEmpty {
            Flow(spacing: 6) {
                ForEach(settled, id: \.self) { SpeakerChip(meeting: meeting, speaker: $0, results: results, names: names) }
            }
        }
    }
}

/// A speaker as a chip: their initials and name, a seal for a confirmed voice, You. Its popover and context menu
/// have Listen, how long they spoke, and Change Who This Is…, Edit, Not Someone I Know.
private struct SpeakerChip: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let speaker: String
    let results: Results
    let names: Names
    private let showing = State(initialValue: false)

    var body: some View {
        let person = names.person(speaker)
        let known = names.review(speaker) != .notKnown
        Button { showing.wrappedValue = true } label: {
            HStack(spacing: 6) {
                Initials(name: names.name(speaker), size: 20)
                Text(names.name(speaker)).foregroundStyle(known ? .primary : .secondary)
                if names.review(speaker) == .confirmed { Image(systemName: "checkmark.seal.fill").foregroundStyle(.green).imageScale(.small) }
                if person?.isOwner == true { YouBadge() }
            }
            .padding(.leading, 3).padding(.trailing, 9).padding(.vertical, 3)
            .background(Color.primary.opacity(0.06), in: .capsule)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .help(person?.detail ?? (known ? names.name(speaker) : "Not someone you know"))
        .accessibilityLabel("\(names.name(speaker))\(names.review(speaker) == .confirmed ? ", voice confirmed" : "")")
        .contextMenu { actions(person) }
        .popover(isPresented: showing.projectedValue, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    Initials(name: names.name(speaker), size: 36)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(names.name(speaker)).font(.headline)
                        Text(person?.detail ?? (known ? "No role or company yet" : "Not someone you know")).font(.callout).foregroundStyle(.secondary)
                    }
                }
                HStack {
                    Listen(clips: results.speakers[speaker]?.clipUrls ?? [], name: names.name(speaker))
                    Text("Spoke for \(duration(results.speakers[speaker]?.talkTime ?? 0))").font(.caption).foregroundStyle(.secondary)
                }
                Divider()
                actions(person).buttonStyle(.borderless)
            }
            .padding(14)
            .frame(width: 280, alignment: .leading)
        }
    }

    @ViewBuilder private func actions(_ person: API.Person?) -> some View {
        // Someone brief, not asked about, can still be named from here
        if names.review(speaker) == .suggested, let id = results.speakers[speaker]?.identity?.personId {
            Button("Yes, This Is \(names.name(speaker))") { showing.wrappedValue = false; Task { await model.assign(meeting, speaker, ["person_id": id]) } }
        }
        Button(names.review(speaker) == .unknown ? "Who's This?" : "Change Who This Is…") { showing.wrappedValue = false; model.assigning = AssignForm(meeting: meeting, speaker: speaker) }
        if let person { Button("Edit \(person.name)…") { showing.wrappedValue = false; model.editing = PersonForm(person) } }
        if names.review(speaker) != .notKnown {
            Button("Not Someone I Know") { showing.wrappedValue = false; Task { await model.assign(meeting, speaker, ["unknown": true]) } }
        }
        Button("Mark as Background Voice") { showing.wrappedValue = false; Task { await model.assign(meeting, speaker, ["background": true]) } }
            .help("Someone nearby, not in the meeting: their lines are set aside")
    }
}

/// The voices set aside as background, and the summary written without them: once the owner has marked
/// whoever they will, one press writes it again. Their own edited copy would be replaced, so that asks first.
private struct BackgroundNote: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let results: Results
    let names: Names
    private let confirming = State(initialValue: false)

    var body: some View {
        let background = results.speakerOrder.filter { names.review($0) == .background }
        let row = model.meetings.first { $0.meetingId == meeting }
        // Written without other voices than are set aside now: some marked since, or one taken back
        let stale = Set(background) != Set(results.summaryWithout ?? [])
        if let row, stale || row.rewriting != nil || !background.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                if !background.isEmpty {
                    Flow(spacing: 6) {
                        Text("Set aside:").foregroundStyle(.secondary)
                        ForEach(background, id: \.self) { speaker in
                            Menu(names.name(speaker)) {  // who it was, to hear again, and the way back
                                Button("Listen") { model.player.toggle(results.speakers[speaker]?.clipUrls ?? []) }
                                    .disabled((results.speakers[speaker]?.clipUrls ?? []).isEmpty || model.recorder != nil)
                                Button("Not a Background Voice…") { model.assigning = AssignForm(meeting: meeting, speaker: speaker) }
                            }
                            .menuStyle(.borderlessButton).fixedSize()
                        }
                    }
                }
                if row.isRewriting {
                    HStack(spacing: 6) {
                        ProgressView().controlSize(.small)
                        Text("Rewriting the summary… It can't be edited until it's done.").foregroundStyle(.secondary)
                    }
                } else if stale {
                    HStack(spacing: 6) {
                        if let error = row.rewriteError { Text(error).foregroundStyle(.orange) }
                        Button(background.isEmpty ? "Rewrite the Summary With Everyone" : "Rewrite the Summary Without Them") {
                            if model.isEdited(meeting) { confirming.wrappedValue = true } else { Task { await model.rewriteSummary(meeting, replace: false) } }
                        }
                        .buttonStyle(.link)
                    }
                }
            }
            .font(.callout)
            .confirmationDialog("Rewrite the summary?", isPresented: confirming.projectedValue) {
                Button("Rewrite") { Task { await model.rewriteSummary(meeting, replace: true) } }
            } message: {
                Text("Tally writes it again for the voices that were in the meeting. Your edits to this summary are replaced.")
            }
        }
    }
}

/// The speakers still to confirm, together above the summary: confirming each once is what makes Tally know their
/// voice. Gone when there are none; they join the chips as they're confirmed.
private struct ToConfirm: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let results: Results
    let names: Names

    var body: some View {
        let open = results.speakerOrder.filter(names.asks)
        if !open.isEmpty {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline) {
                    Label(AppModel.toConfirmText(open.count), systemImage: "person.crop.circle.badge.questionmark")
                        .font(.headline).foregroundStyle(.orange)
                    Spacer()
                    if model.recorder != nil {  // said, not just greyed out: why Listen is off
                        Label("Listen is off while recording", systemImage: "speaker.slash").font(.callout).foregroundStyle(.secondary)
                    }
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 260), spacing: 10)], alignment: .leading, spacing: 10) {
                    ForEach(open, id: \.self) { SpeakerCard(meeting: meeting, speaker: $0, results: results, names: names) }
                }
            }
            .padding(14)
            .background(Color.orange.opacity(0.07), in: .rect(cornerRadius: 12))
        }
    }
}

/// Views side by side, wrapping onto the next line as words do.
struct Flow: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        return CGSize(width: rows.map(\.width).max() ?? 0, height: rows.last.map { $0.y + $0.height } ?? 0)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.items {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: bounds.minY + row.y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
        }
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [(items: [Int], y: CGFloat, width: CGFloat, height: CGFloat)] {
        var rows: [(items: [Int], y: CGFloat, width: CGFloat, height: CGFloat)] = []
        var current: (items: [Int], y: CGFloat, width: CGFloat, height: CGFloat) = ([], 0, 0, 0)
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if !current.items.isEmpty, current.width + spacing + size.width > width {
                rows.append(current)
                current = ([], current.y + current.height + spacing, 0, 0)
            }
            current.width += (current.items.isEmpty ? 0 : spacing) + size.width
            current.height = max(current.height, size.height)
            current.items.append(index)
        }
        if !current.items.isEmpty { rows.append(current) }
        return rows
    }
}

private struct SpeakerCard: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let speaker: String
    let results: Results
    let names: Names
    private let confirming = State(initialValue: false)  // Yes… to someone new: their details, then confirmed

    var body: some View {
        let review = names.review(speaker)
        let person = names.person(speaker)
        let saving = model.isPending(meeting, speaker)
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Text(names.name(speaker)).font(.headline).lineLimit(1)
                if review == .confirmed {
                    Image(systemName: "checkmark.seal.fill").foregroundStyle(.green).accessibilityLabel("Voice confirmed").help("Voice confirmed")
                }
                if person?.isOwner == true { YouBadge() }
                Spacer(minLength: 0)
                if saving { ProgressView().controlSize(.small).accessibilityLabel("Saving who this is") }
                Menu {
                    Button("Change Who This Is…") { model.assigning = AssignForm(meeting: meeting, speaker: speaker) }
                    if let person { Button("Edit \(person.name)…") { model.editing = PersonForm(person) } }
                    if review != .notKnown { Button("Not Someone I Know") { Task { await model.assign(meeting, speaker, ["unknown": true]) } } }
                    Button("Mark as Background Voice") { Task { await model.assign(meeting, speaker, ["background": true]) } }
                } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().disabled(saving)
                    .accessibilityLabel("More for \(names.name(speaker))")
            }
            if let detail = person?.detail { Text(detail).font(.callout).foregroundStyle(.secondary).lineLimit(1) }
            HStack {
                Listen(clips: results.speakers[speaker]?.clipUrls ?? [], name: names.name(speaker))
                Text("Spoke for \(duration(results.speakers[speaker]?.talkTime ?? 0))").font(.caption).foregroundStyle(.secondary)
            }
            actions(review).disabled(saving)
        }
        .padding(12)
        .background(.background.opacity(0.6), in: .rect(cornerRadius: 10))  // on the to-confirm box's tint
        .animation(.default, value: review)
    }

    @ViewBuilder private func actions(_ review: Names.Review) -> some View {
        switch review {
        case .suggested:
            HStack {
                Text("Is this \(names.name(speaker))?").font(.callout)
                Spacer()
                // A suggestion is someone whose voice the owner hasn't confirmed: Yes asks for their details too
                Button("Yes…") { confirming.wrappedValue = true }
                    .buttonStyle(.borderedProminent).controlSize(.small)
                    .accessibilityLabel("Yes, this is \(names.name(speaker))").accessibilityInputLabels(["Yes"])
                    .popover(isPresented: confirming.projectedValue, arrowEdge: .bottom) {
                        let person = names.person(speaker)
                        ConfirmPerson(name: person?.name ?? names.name(speaker), role: person?.role ?? "", company: person?.company ?? "") { name, role, company in
                            confirming.wrappedValue = false
                            Task {
                                await model.confirm(meeting, speaker, person: results.speakers[speaker]?.identity?.personId,
                                                    name: name, role: role, company: company)
                            }
                        }
                    }
                Button("No…") {
                    model.assigning = AssignForm(meeting: meeting, speaker: speaker, rejected: results.speakers[speaker]?.identity?.personId)
                }.controlSize(.small)
                    .accessibilityLabel("No, it isn't \(names.name(speaker))").accessibilityInputLabels(["No"])
            }
        case .unknown:
            Button("Who's This?") { model.assigning = AssignForm(meeting: meeting, speaker: speaker) }.controlSize(.small)
                .accessibilityLabel("Who's \(names.name(speaker))?").accessibilityInputLabels(["Who's This"])
        case .notKnown:
            Text("Not someone you know").font(.callout).foregroundStyle(.secondary)
        case .background:
            Text("A background voice: not in the meeting").font(.callout).foregroundStyle(.secondary)
        case .confirmed:
            EmptyView()
        }
    }
}

/// Yes, to someone new: who they are in full, filled in with what the agent heard, the cursor at the name's end
/// (to add a surname), and Return confirms. Nothing changed is a plain yes.
private struct ConfirmPerson: View {
    private let name: State<String>
    private let role: State<String>
    private let company: State<String>
    private let focus = FocusState<Bool>()
    let confirm: (String, String, String) -> Void

    init(name: String, role: String, company: String, confirm: @escaping (String, String, String) -> Void) {
        self.name = State(initialValue: name)
        self.role = State(initialValue: role)
        self.company = State(initialValue: company)
        self.confirm = confirm
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Confirm \(name.wrappedValue.isEmpty ? "who this is" : name.wrappedValue)").font(.headline)
            Text("Tally remembers their voice. Add what you know of them.").font(.callout).foregroundStyle(.secondary)
            Form {
                TextField("Name", text: name.projectedValue).focused(focus.projectedValue)
                TextField("Role", text: role.projectedValue, prompt: Text("e.g. Head of finance"))
                TextField("Company", text: company.projectedValue, prompt: Text("e.g. Acme"))
            }
            HStack {
                Spacer()
                Button("Confirm") { confirm(name.wrappedValue, role.wrappedValue, company.wrappedValue) }
                    .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(name.wrappedValue.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(16)
        .frame(width: 320)
        .onAppear {
            focus.wrappedValue = true
            moveCursorToEnd()  // to type a surname
        }
    }
}

/// Hear a speaker: their clips (a few seconds each from their clearest turns) one after the other.
private struct Listen: View {
    @Environment(AppModel.self) private var model
    let clips: [URL]
    var name = "this speaker"

    @ViewBuilder var body: some View {
        if clips.isEmpty {  // said, not a button that can never work
            Text("No clip: too little clear speech").font(.caption).foregroundStyle(.tertiary)
        } else {
            button
        }
    }

    private var button: some View {
        let playing = model.player.isPlaying(clips)
        return Button { model.player.toggle(clips) } label: {
            Label(playing ? "Pause" : "Listen", systemImage: playing ? "pause.fill" : "play.fill")
        }
        .controlSize(.small)
        .disabled(model.recorder != nil || model.starting)  // Tally's own sound would go into the recording
        .help(model.recorder != nil ? "Listening is off while recording" : clips.count == 1 ? "Hear a short clip of \(name)" : "Hear \(clips.count) short clips of \(name)")
        .accessibilityLabel(playing ? "Pause \(name)" : "Listen to \(name)")
    }
}

/// The owner says who a speaker is: one of the voices that sound like theirs, anyone they know (type to find them),
/// someone new, or nobody they know.
struct AssignSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Bindable var form: AssignForm

    var body: some View {
        let speaker = model.results[form.meeting]?.speakers[form.speaker]
        let query = form.query.trimmingCharacters(in: .whitespaces)
        let close = (speaker?.candidates ?? []).filter { $0.closeness != nil && model.peopleById[$0.personId] != nil && $0.personId != form.rejected }
        let matches = query.isEmpty ? [] : model.people.filter { $0.personId != form.rejected && ($0.name.localizedCaseInsensitiveContains(query)
            || $0.aliases.contains { $0.localizedCaseInsensitiveContains(query) }) }
        let exact = model.people.contains { ([$0.name] + $0.aliases).contains { $0.caseInsensitiveCompare(query) == .orderedSame } }
        // What ↑ ↓ and Return go through, as in Spotlight: the voices alike (or the names typed for), then Add
        let listed = query.isEmpty ? close.map(\.personId) : matches.map(\.personId)
        let adding = !query.isEmpty && !exact
        let addHighlighted = adding && form.highlight == listed.count
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Who's speaking?").font(.title2.bold())
                Spacer()
                Listen(clips: speaker?.clipUrls ?? [])
            }
            if let speaker { Text("Spoke for \(duration(speaker.talkTime)) in this meeting").font(.callout).foregroundStyle(.secondary) }

            if query.isEmpty, !close.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Sounds like").font(.headline)
                    ForEach(Array(close.enumerated()), id: \.element.id) { index, candidate in
                        PersonRow(person: model.peopleById[candidate.personId]!, note: candidate.closeness, highlighted: form.highlight == index) {
                            choose(candidate.personId)
                        }
                    }
                }
            }
            TextField(close.isEmpty ? "Name" : "Someone else? Type a name", text: $form.query).textFieldStyle(.roundedBorder).font(.title3)
                .onKeyPress(.downArrow) { form.highlight = max(0, min(form.highlight + 1, listed.count - (adding ? 0 : 1))); return .handled }
                .onKeyPress(.upArrow) { form.highlight = max(form.highlight - 1, 0); return .handled }
                .onSubmit {
                    // Return picks the row highlighted: a person, or Add, which shows the name it makes before it does
                    if listed.indices.contains(form.highlight) { choose(listed[form.highlight]) } else if addHighlighted { add(query) }
                }
            if !matches.isEmpty {
                ScrollViewReader { scroller in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(matches.enumerated()), id: \.element.id) { index, person in
                                PersonRow(person: person, note: nil, highlighted: form.highlight == index) { choose(person.personId) }.id(index)
                            }
                        }
                    }
                    .frame(maxHeight: 180)
                    .onChange(of: form.highlight) { _, row in withAnimation { scroller.scrollTo(row) } }  // the highlight stays in sight
                }
            }
            // Someone new: an option among the matches when there are some, and then, chosen, their details with
            // Add Person where a sheet's main button is, at the bottom right; Return from any field adds them too
            if adding, !matches.isEmpty {
                Button { form.highlight = listed.count } label: {
                    Label("Someone new: “\(query)”", systemImage: "person.badge.plus").frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 5).padding(.horizontal, 8)
                        .background(addHighlighted ? Color.accentColor.opacity(0.2) : .clear, in: .rect(cornerRadius: 6))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(addHighlighted ? .isSelected : [])
            }
            let making = adding && (addHighlighted || matches.isEmpty)
            if making {
                VStack(alignment: .leading, spacing: 8) {
                    Text("New person: \(query)").font(.headline)
                    TextField("Role", text: $form.role, prompt: Text("Role, e.g. Head of finance")).onSubmit { add(query) }
                    TextField("Company", text: $form.company, prompt: Text("Company, e.g. Acme")).onSubmit { add(query) }
                }
                .textFieldStyle(.roundedBorder)
            }
            HStack {
                Button("Not Someone I Know") { send(["unknown": true]) }
                Button("Mark as Background Voice") { send(["background": true]) }.help("Someone nearby, not in the meeting: their lines are set aside")
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                if making { Button("Add Person") { add(query) }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction) }
            }
        }
        .padding(20)
        .frame(width: 440)
    }

    private func choose(_ id: String) { send(["person_id": id]) }
    private func add(_ name: String) { send(["name": name, "role": form.role, "company": form.company]) }

    private func send(_ body: [String: Any]) {
        guard !form.sent else { return }
        form.sent = true
        let (meeting, speaker) = (form.meeting, form.speaker)
        dismiss()
        Task { await model.assign(meeting, speaker, body) }
    }
}

/// A person to pick, the whole row clickable.
private struct PersonRow: View {
    let person: API.Person
    let note: String?
    var highlighted = false  // reached with ↑ ↓: Return picks it
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text(person.name)
                    if let detail = person.detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
                }
                Spacer()
                if let note { Text(note).font(.caption).foregroundStyle(.secondary) }
                if person.confirmed { Image(systemName: "checkmark.seal.fill").foregroundStyle(.green).font(.caption) }
            }
            .padding(.vertical, 5).padding(.horizontal, 8)
            .background(highlighted ? Color.accentColor.opacity(0.2) : .clear, in: .rect(cornerRadius: 6))
            .contentShape(.rect)
        }
        .buttonStyle(.menuRow)
        .accessibilityAddTraits(highlighted ? .isSelected : [])
    }
}

/// The owner's own person, wherever people are listed.
struct YouBadge: View {
    var body: some View {
        Text("You").font(.caption2.bold()).padding(.horizontal, 5).background(.blue.opacity(0.15), in: .capsule)
    }
}

private struct Heading: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View { Text(text).font(.title3.weight(.semibold)).accessibilityAddTraits(.isHeader) }
}

/// The summary, edited where it stands: click any text and type, as in Notes. The first change makes
/// the owner's own copy, saved a second after the last keystroke; the agent's is kept, and Restore Original brings it
/// back. Points can be added under each list and deleted from their context menu; an action item has an owner, a due
/// date and a box to tick.
private struct SummaryPage: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let summary: Results.Summary
    let names: Names
    let speakers: [String]

    var body: some View {
        VStack(alignment: .leading, spacing: 26) {
            Editable(summary.overview, names: names, placeholder: "Overview", opensEmpty: false) { text in edit { $0.overview = text } }.font(.title3)
            list("Summary", summary.summary.indices, add: { $0.summary.append("") }, delete: { $0.summary.remove(at: $1) }) { i in
                Editable(summary.summary[i], names: names, placeholder: "A point") { text in edit { $0.summary[i] = text } }
            }
            list("Decisions", summary.decisions.indices, add: { $0.decisions.append(.init(text: "", by: [])) },
                 delete: { $0.decisions.remove(at: $1) }) { i in
                VStack(alignment: .leading, spacing: 2) {
                    Editable(summary.decisions[i].text, names: names, placeholder: "A decision") { text in edit { $0.decisions[i].text = text } }
                    people(summary.decisions[i].by)
                }
            }
            list("Action items", summary.actionItems.indices, bulleted: false,
                 add: { $0.actionItems.append(.init(owner: nil, text: "", due: nil, done: nil)) }, delete: { $0.actionItems.remove(at: $1) }) { i in
                ActionItemRow(item: summary.actionItems[i], names: names, speakers: speakers) { change in edit { change(&$0.actionItems[i]) } }
            }
            list("Open questions", summary.openQuestions.indices, add: { $0.openQuestions.append(.init(text: "", raisedBy: [])) },
                 delete: { $0.openQuestions.remove(at: $1) }) { i in
                VStack(alignment: .leading, spacing: 2) {
                    Editable(summary.openQuestions[i].text, names: names, placeholder: "A question") { text in edit { $0.openQuestions[i].text = text } }
                    people(summary.openQuestions[i].raisedBy)
                }
            }
            if !summary.topics.isEmpty {
                VStack(alignment: .leading, spacing: 16) {
                    Heading("Topics")
                    ForEach(summary.topics.indices, id: \.self) { t in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack(alignment: .firstTextBaseline, spacing: 12) {
                                Editable(summary.topics[t].title, names: names, placeholder: "Topic") { text in edit { $0.topics[t].title = text } }
                                    .font(.headline)
                                Text("\(clock(summary.topics[t].start))–\(clock(summary.topics[t].end))").font(.caption).monospacedDigit()
                                    .foregroundStyle(.secondary).fixedSize()
                            }
                            list(nil, summary.topics[t].notes.indices, add: { $0.topics[t].notes.append("") },
                                 delete: { $0.topics[t].notes.remove(at: $1) }) { n in
                                Editable(summary.topics[t].notes[n], names: names, placeholder: "A note") { text in edit { $0.topics[t].notes[n] = text } }
                            }
                            if let outcome = summary.topics[t].outcome {
                                VStack(alignment: .leading, spacing: 2) {  // its label above, so a long outcome wraps the full width
                                    Text("Outcome").font(.caption.bold()).foregroundStyle(.secondary)
                                    Editable(outcome, names: names, placeholder: "Outcome", opensEmpty: false) { text in edit { $0.topics[t].outcome = text } }
                                }
                                .padding(.top, 2)
                            }
                        }
                    }
                }
            }
            if !summary.attendees.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Heading("Who said what")
                    ForEach(summary.attendees.indices, id: \.self) { i in
                        bullet(VStack(alignment: .leading, spacing: 2) {  // who they are, then their part, each the full width
                            Text("\(Text(names.name(summary.attendees[i].speaker)).bold())  \(Text(names.role(summary.attendees[i])).foregroundStyle(.secondary))")
                                .fixedSize(horizontal: false, vertical: true)
                            Editable(summary.attendees[i].contribution, names: names, placeholder: "Their part", opensEmpty: false) { text in
                                edit { $0.attendees[i].contribution = text }
                            }
                        })
                    }
                }
            }
            addSection
        }
    }

    @Environment(\.undoManager) private var undo
    private func edit(_ change: (inout Results.Summary) -> Void) { model.edit(meeting, undo: undo, change) }

    /// A list under its heading, with Add at the end and Delete in each row's context menu. An empty list shows
    /// nothing: it's added from the menu at the end of the page.
    @ViewBuilder private func list<Row: View>(_ title: String?, _ indices: Range<Int>, bulleted: Bool = true,
                                              add: @escaping (inout Results.Summary) -> Void,
                                              delete: @escaping (inout Results.Summary, Int) -> Void,
                                              @ViewBuilder row: @escaping (Int) -> Row) -> some View {
        if !indices.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                if let title { Heading(title) }
                ForEach(indices, id: \.self) { i in
                    Group { if bulleted { bullet(row(i)) } else { row(i) } }
                        .contextMenu { Button("Delete", role: .destructive) { edit { delete(&$0, i) } } }
                }
                Button { edit(add) } label: { Label("Add", systemImage: "plus") }
                    .buttonStyle(.borderless).font(.callout).foregroundStyle(.secondary).accessibilityLabel("Add to \(title ?? "the summary")")
            }
        }
    }

    /// For the lists that are empty: a summary point, a decision, an action item or an open question where the agent
    /// wrote none.
    @ViewBuilder private var addSection: some View {
        let sections: [(String, Bool, (inout Results.Summary) -> Void)] = [  // its name, whether it's empty, and adding one
            ("Summary Point", summary.summary.isEmpty, { $0.summary.append("") }),
            ("Decision", summary.decisions.isEmpty, { $0.decisions.append(.init(text: "", by: [])) }),
            ("Action Item", summary.actionItems.isEmpty, { $0.actionItems.append(.init(owner: nil, text: "", due: nil, done: nil)) }),
            ("Open Question", summary.openQuestions.isEmpty, { $0.openQuestions.append(.init(text: "", raisedBy: [])) }),
        ]
        let missing = sections.filter(\.1)
        if !missing.isEmpty {
            Menu {
                ForEach(missing, id: \.0) { name, _, add in Button(name) { edit(add) } }
            } label: { Label("Add", systemImage: "plus") }
                .menuStyle(.borderlessButton).fixedSize().foregroundStyle(.secondary).accessibilityLabel("Add a section")
        }
    }

    /// Who decided or asked, under it.
    @ViewBuilder private func people(_ speakers: [String]) -> some View {
        if !speakers.isEmpty {
            Text(speakers.map(names.name).formatted(.list(type: .and))).font(.callout).foregroundStyle(.secondary)
        }
    }
}

/// An action item: tick it done, edit what it says, and under it who owns it and when it's due, as in Reminders.
private struct ActionItemRow: View {
    let item: Results.ActionItem
    let names: Names
    let speakers: [String]
    let change: ((inout Results.ActionItem) -> Void) -> Void
    private let hovering = State(initialValue: false)

    var body: some View {
        // Owner and due date show when set; one not set is offered under the pointer, as Reminders does
        let due = item.due.flatMap(Self.date)
        let details = item.owner != nil || due != nil || hovering.wrappedValue
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Toggle("Done", isOn: Binding(get: { item.done == true }, set: { done in change { $0.done = done } }))
                .toggleStyle(.checkbox).labelsHidden()
            VStack(alignment: .leading, spacing: 2) {
                Editable(item.text, names: names, placeholder: "What's to be done") { text in change { $0.text = text } }
                    .strikethrough(item.done == true).foregroundStyle(item.done == true ? .secondary : .primary)
                if details {
                    HStack(spacing: 10) {
                        if item.owner != nil || hovering.wrappedValue {
                            Menu {
                                Button("No Owner") { change { $0.owner = nil } }
                                Divider()
                                ForEach(speakers, id: \.self) { speaker in Button(names.name(speaker)) { change { $0.owner = speaker } } }
                            } label: {
                                Label(item.owner.map(names.name) ?? "Add owner", systemImage: "person")
                            }
                            .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().help("Who owns it")
                        }
                        if due != nil || hovering.wrappedValue {
                            DueDate(due: due, done: item.done == true) { date in change { $0.due = date.map(Self.string) } }
                        }
                    }
                    .font(.callout).foregroundStyle(.secondary)
                }
            }
        }
        .contentShape(.rect)
        .onHover { hovering.wrappedValue = $0 }
    }

    /// A due date is a day on the owner's calendar: ISO 8601 is read and written in GMT unless told otherwise, which put
    /// "today" a day out east of UTC, and every date a day early west of it.
    private static let day = Date.ISO8601FormatStyle(timeZone: .current).year().month().day()
    private static func date(_ text: String) -> Date? { try? Date(text, strategy: day) }
    private static func string(_ date: Date) -> String { date.formatted(day) }
}

/// When an action item is due: its date, or "Add due date", and one click opens a calendar. A plain date picker
/// field needs a click into it and has a stepper, which isn't how anyone picks a day.
private struct DueDate: View {
    let due: Date?
    let done: Bool  // a done one is never late
    let change: (Date?) -> Void
    private let picking = State(initialValue: false)

    var body: some View {
        Button {
            if due == nil { change(Calendar.current.startOfDay(for: .now)) }  // today, to change: so it stays while the calendar's open
            picking.wrappedValue = true
        } label: {
            Label(due.map { "Due \($0.formatted(.dateTime.day().month(.abbreviated)))" } ?? "Add due date", systemImage: "calendar")
                .foregroundStyle(!done && due.map { $0 < Calendar.current.startOfDay(for: .now) } == true ? AnyShapeStyle(.red) : AnyShapeStyle(.secondary))
        }
        .buttonStyle(.borderless)
        .help(due == nil ? "Give it a due date" : "Change the due date")
        .popover(isPresented: picking.projectedValue) {
            VStack(alignment: .leading, spacing: 10) {
                DatePicker("Due", selection: Binding(get: { due ?? .now }, set: { change($0) }), displayedComponents: .date)
                    .datePickerStyle(.graphical).labelsHidden()
                if due != nil {
                    Button("Remove Due Date", role: .destructive) { change(nil); picking.wrappedValue = false }.buttonStyle(.borderless)
                }
            }
            .padding(12)
        }
    }
}

/// Text that edits where it stands: it reads as text (a faint highlight under the pointer says it can be edited), and
/// becomes a field when clicked, with the cursor at its end; leaving it, or Return, makes it text again. A field
/// isn't simply left on the page, because in a scroll view SwiftUI draws a multi-line field's first line only.
/// What's shown has speakers' names; what's kept has their tokens, so renaming someone in People still reaches it.
struct Editable: View {
    let text: String
    let names: Names
    let placeholder: String
    let change: (String) -> Void
    private let editing: State<Bool>
    private let hovering = State(initialValue: false)
    private let focus = FocusState<Bool>()

    /// `opensEmpty`: an empty one is a point just added, so it's ready to type in, as a new line in Notes is.
    /// `keepsSpeakers`: names typed stay the speakers (`{S2}`), except in the title, which the meeting list shows as is.
    init(_ text: String, names: Names, placeholder: String, opensEmpty: Bool = true, keepsSpeakers: Bool = true,
         change: @escaping (String) -> Void) {
        self.text = text
        self.names = names
        self.placeholder = placeholder
        self.change = keepsSpeakers ? { change(names.tokenize($0)) } : change
        editing = State(initialValue: opensEmpty && text.isEmpty)
    }

    var body: some View {
        let shown = names.render(text)
        if editing.wrappedValue {
            TextField(placeholder, text: Binding(get: { shown }, set: { if $0 != shown { change($0) } }), axis: .vertical)
                .textFieldStyle(.plain)
                .focused(focus.projectedValue)
                .onSubmit { editing.wrappedValue = false }
                .onChange(of: focus.wrappedValue) { _, focused in if !focused { editing.wrappedValue = false } }
                .onAppear {
                    focus.wrappedValue = true
                    moveCursorToEnd()
                }
        } else {
            Text(shown.isEmpty ? placeholder : shown)
                .foregroundStyle(shown.isEmpty ? AnyShapeStyle(.tertiary) : AnyShapeStyle(.foreground))  // inherited: a ticked item is greyed
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
                .background(hovering.wrappedValue ? Color.primary.opacity(0.05) : .clear, in: .rect(cornerRadius: 4))
                .contentShape(.rect)
                .onHover { hovering.wrappedValue = $0 }
                .onTapGesture { editing.wrappedValue = true }
                .accessibilityAddTraits(.isButton)
                .accessibilityAction { editing.wrappedValue = true }
        }
    }
}

struct TranscriptPage: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let results: Results
    let names: Names
    private let showing = State(initialValue: false)  // the background voices' lines, folded away until asked for

    var body: some View {
        // A background voice's lines fold into one row where they come; shown, they're there in grey. One view a
        // row, with a stable id: a lazy stack whose rows yield a varying number of views can't measure them, and
        // draws only the first, its scroll bar guessing and jumping back.
        let rows = Self.rows(results.paragraphs, background: { names.review($0) == .background }, folded: !showing.wrappedValue)
        LazyVStack(alignment: .leading, spacing: 16) {
            ForEach(rows) { row in
                if row.folded {
                    Button { showing.wrappedValue = true } label: {
                        Label(row.lines.count == 1 ? "1 line of a background voice · Show" : "\(row.lines.count) lines of background voices · Show",
                              systemImage: "speaker.wave.1")
                    }
                    .buttonStyle(.borderless).font(.callout).foregroundStyle(.secondary)
                } else {
                    let paragraph = row.lines[0]
                    VStack(alignment: .leading, spacing: 3) {
                        Text("\(Text(names.name(paragraph.speaker)).bold())\(row.background ? " · background" : "")  \(Text(clock(paragraph.start)).font(.caption).foregroundStyle(.secondary))")
                        Text(paragraph.text).fixedSize(horizontal: false, vertical: true)
                    }
                    .foregroundStyle(row.background ? .secondary : .primary)
                    .contextMenu {
                        if row.background {
                            Button("Not a Background Voice…") { model.assigning = AssignForm(meeting: meeting, speaker: paragraph.speaker) }
                        }
                    }
                }
            }
        }
        .textSelection(.enabled)
    }

    /// One row of the transcript: a paragraph, or (folded) a background voice's consecutive lines. Its id is its
    /// first paragraph's place in the meeting.
    struct Row: Identifiable {
        let id: Int
        var lines: [Results.Paragraph]
        let background: Bool
        let folded: Bool
    }

    static func rows(_ paragraphs: [Results.Paragraph], background: (String) -> Bool, folded: Bool) -> [Row] {
        var rows: [Row] = []
        for (index, paragraph) in paragraphs.enumerated() {
            let set = background(paragraph.speaker)
            if set, folded, rows.last?.folded == true {
                rows[rows.count - 1].lines.append(paragraph)
            } else {
                rows.append(Row(id: index, lines: [paragraph], background: set, folded: set && folded))
            }
        }
        return rows
    }
}

/// The cursor at the end of the field just focused, rather than all of its text selected to be typed over.
@MainActor func moveCursorToEnd() {
    DispatchQueue.main.async {
        if let editor = NSApp.keyWindow?.firstResponder as? NSTextView {
            editor.setSelectedRange(NSRange(location: editor.string.utf16.count, length: 0))
        }
    }
}

func bullet(_ content: some View) -> some View {
    HStack(alignment: .firstTextBaseline, spacing: 8) {
        Text("•").foregroundStyle(.secondary)
        content.fixedSize(horizontal: false, vertical: true)
    }
}

func clock(_ seconds: Double) -> String {
    let s = Int(seconds)
    return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
}

/// "Tuesday 20 October 2026, 20:00": a fixed order that reads well, with the names in the Mac's language (the
/// locale's own long style can come out as "2026 October 20, Tuesday"), and the time on the Mac's own clock.
func longDate(_ date: Date) -> String {
    let formatter = DateFormatter()
    formatter.dateFormat = "EEEE d MMMM yyyy"
    return formatter.string(from: date) + ", " + date.formatted(date: .omitted, time: .shortened)
}

/// "You usually file meetings with Anna in Customers › Acme", with File There and Dismiss.
private struct FilingNote: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    let suggestion: Filing.Suggestion

    var body: some View {
        let people = model.peopleById
        let names = suggestion.people.compactMap { people[$0]?.name }
        let who = names.count > 2 ? "\(names[0]), \(names[1]) and others" : names.formatted(.list(type: .and))
        HStack(spacing: 10) {
            Image(systemName: "folder").foregroundStyle(.tint)
            Text(names.isEmpty ? "You usually file meetings like this in **\(model.path(suggestion.folder))**."
                 : "You usually file meetings with \(who) in **\(model.path(suggestion.folder))**.")
            Spacer(minLength: 8)
            Button("File There") { Task { await model.file(meeting, in: suggestion.folder, true) } }.buttonStyle(.borderedProminent).controlSize(.small)
            Button("Dismiss") { model.notFiled.insert(meeting) }.controlSize(.small)
        }
        .font(.callout)
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(.tint.opacity(0.08), in: .rect(cornerRadius: 8))
    }
}
