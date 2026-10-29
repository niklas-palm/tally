import SwiftUI
import UniformTypeIdentifiers

/// The window's left: Meetings, which holds every meeting and can't be renamed or deleted; People; and the owner's
/// folders, nested as they like. A meeting dropped on a folder is added to it; a folder dropped on
/// another goes inside, and on Meetings to the top.
struct Sidebar: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        List(selection: $model.place) {
            // As Mail counts unread: the meetings with someone to confirm
            Label("Meetings", systemImage: "tray.full")
                .badge(model.items.filter { model.toConfirm($0.id) > 0 }.count)
                .tag(AppModel.Place.meetings)  // after the badge: the row the list selects is the tagged one
                .dropDestination(for: Dragged.self) { items, _ in Sidebar.drop(items.map(\.id), into: nil, model) }
            Label("People", systemImage: "person.2").tag(AppModel.Place.people)
            Section {
                ForEach(model.children(of: nil)) { FolderTree(folder: $0) }
            } header: {
                HStack {
                    Text("Folders")
                    Spacer()
                    Button { Task { await model.newFolder() } } label: { Image(systemName: "plus") }
                        .buttonStyle(.borderless).help("New Folder").accessibilityLabel("New Folder")
                }
            }
        }
        .onDeleteCommand { if case .folder(let id) = model.place { model.deletingFolder = model.folders.first { $0.folderId == id } } }
        .confirmationDialog("Delete “\(model.deletingFolder?.name ?? "this folder")”?",
                            isPresented: Binding(get: { model.deletingFolder != nil }, set: { if !$0 { model.deletingFolder = nil } }),
                            presenting: model.deletingFolder) { folder in
            Button("Delete Folder", role: .destructive) { Task { await model.delete(folder: folder) } }
        } message: { folder in
            Text(model.children(of: folder.folderId).isEmpty
                 ? "Its meetings aren't deleted: they stay in Meetings, and in any other folders they're in."
                 : "The folders inside it go too. Their meetings aren't deleted: they stay in Meetings, and in any other folders they're in.")
        }
    }

    /// A meeting dropped is added to the folder; a folder dropped goes inside (or, onto Meetings, to the top).
    /// Meetings has every meeting already: one dropped there stays as it is.
    static func drop(_ ids: [String], into folder: String?, _ model: AppModel) -> Bool {
        // Only Tally's own: a meeting that's been sent, or a folder (text dragged in from elsewhere isn't either)
        guard ids.allSatisfy({ id in model.meetings.contains { $0.meetingId == id } || model.folders.contains { $0.folderId == id } }) else { return false }
        for id in ids {
            if id.hasPrefix("f_") {
                if id != folder { Task { await model.nest(folder: id, in: folder) } }
            } else if let folder {
                Task { await model.file(id, in: folder, true) }
            }
        }
        return true
    }
}

/// A folder and, unfolding, the folders inside it.
private struct FolderTree: View {
    @Environment(AppModel.self) private var model
    let folder: API.Folder

    var body: some View {
        let children = model.children(of: folder.folderId)
        if children.isEmpty {
            FolderRow(folder: folder)
        } else {
            DisclosureGroup(isExpanded: Binding(get: { model.expanded.contains(folder.folderId) },
                                                set: { if $0 { model.expanded.insert(folder.folderId) } else { model.expanded.remove(folder.folderId) } })) {
                ForEach(children) { FolderTree(folder: $0) }
            } label: {
                FolderRow(folder: folder)
            }
        }
    }
}

/// A folder in the sidebar: its name, typed in place when it's new or renamed; its context menu; and a target for
/// what's dropped on it.
private struct FolderRow: View {
    @Environment(AppModel.self) private var model
    let folder: API.Folder
    private let name = State(initialValue: "")
    private let focus = FocusState<Bool>()

    var body: some View {
        Group {
            if model.renamingFolder == folder.folderId {
                TextField("Name", text: name.projectedValue)
                    .focused(focus.projectedValue)
                    .onAppear { name.wrappedValue = folder.name; focus.wrappedValue = true }
                    .onSubmit { Task { await model.rename(folder: folder.folderId, to: name.wrappedValue) } }
                    .onExitCommand { model.renamingFolder = nil }
                    .onChange(of: focus.wrappedValue) { _, focused in
                        if !focused, model.renamingFolder == folder.folderId { Task { await model.rename(folder: folder.folderId, to: name.wrappedValue) } }
                    }
            } else {
                Label(folder.name, systemImage: "folder")
            }
        }
        .tag(AppModel.Place.folder(folder.folderId))
        .draggable(Dragged(id: folder.folderId, text: model.path(folder.folderId)))
        .dropDestination(for: Dragged.self) { items, _ in Sidebar.drop(items.map(\.id), into: folder.folderId, model) }
        .contextMenu {
            Button("Rename") { model.renamingFolder = folder.folderId }
            Button("New Folder Inside") { Task { await model.newFolder(in: folder.folderId) } }
            if folder.parent != nil { Button("Move to the Top") { Task { await model.nest(folder: folder.folderId, in: nil) } } }
            Divider()
            Button("Delete Folder…", role: .destructive) { model.deletingFolder = folder }
        }
    }
}

/// The middle: the meetings where the sidebar is, by day, newest first; or, while searching, what was found.
struct MeetingList: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Group {
            if let results = model.searchResults {
                SearchResults(results: results)
            } else if !model.query.trimmingCharacters(in: .whitespaces).isEmpty {
                ProgressView("Searching…").frame(maxWidth: .infinity, maxHeight: .infinity)  // not the list, as if it matched
            } else {
                list
            }
        }
        .searchable(text: $model.query, placement: .toolbar, prompt: "Search meetings")
        .toolbar { ToolbarItem { RecordButton() } }  // over the list, as New Note is in Notes: it makes a meeting, it isn't one
    }

    @ViewBuilder private var list: some View {
        @Bindable var model = model
        let items = model.items(in: model.place)
        if items.isEmpty, model.place == .meetings, model.meetings.isEmpty, model.auth.state != .signedIn || model.offline {
            ContentUnavailableView(model.auth.state != .signedIn ? "Sign In to See Your Meetings" : "Your Meetings Show Here Once You're Back Online",
                                   systemImage: "tray", description: Text("Recordings you make wait safely on this Mac."))
        } else if items.isEmpty {
            ContentUnavailableView(model.place == .meetings ? "No Meetings Yet" : "No Meetings Here", systemImage: "tray",
                                   description: Text(model.place == .meetings ? "Your recordings appear here." : "Drag meetings onto this folder, or right-click one and choose Folders."))
        } else {
            List(selection: $model.selection) {
                ForEach(Self.days(items), id: \.day) { day in
                    Section(day.title) {
                        ForEach(day.items) { item in
                            ListRow(item: item, toConfirm: model.toConfirm(item.id), folders: folders(of: item))
                            .tag(AppModel.Selection.meeting(item.id))
                            .draggable(Dragged(id: item.id, text: model.dragText(item)))  // one still on this Mac has nowhere to go yet: Sidebar.drop says no
                            .contextMenu { MeetingMenu(item: item) }
                        }
                    }
                }
            }
            .onDeleteCommand { if case .meeting(let id) = model.selection, let item = items.first(where: { $0.id == id }), item.canDelete { model.deleting = item } }
        }
    }

    /// The folders a meeting is in, but the one being looked at: "Acme, Team".
    private func folders(of item: AppModel.Item) -> String? {
        let here: String? = if case .folder(let id) = model.place { id } else { nil }
        let names = model.folderChoices.filter { item.folderIds.contains($0.folderId) && $0.folderId != here }.map(\.name)
        return names.isEmpty ? nil : names.joined(separator: ", ")
    }

    /// Meetings under Today, Yesterday, or their date.
    static func days(_ items: [AppModel.Item]) -> [(day: Date, title: String, items: [AppModel.Item])] {
        let calendar = Calendar.current
        let grouped = Dictionary(grouping: items) { calendar.startOfDay(for: $0.startedAt) }
        return grouped.keys.sorted(by: >).map { day in
            let thisYear = calendar.isDate(day, equalTo: .now, toGranularity: .year)
            let title = calendar.isDateInToday(day) ? "Today" : calendar.isDateInYesterday(day) ? "Yesterday"
                : day.formatted(thisYear ? dayStyle : dayStyle.year())
            return (day, title, grouped[day]!)
        }
    }

    // "Wednesday 21 October", in the Gregorian order even when the Mac's calendar is ISO 8601 (which puts the year first)
    private static let dayStyle: Date.FormatStyle = {
        var style = Date.FormatStyle.dateTime.weekday(.wide).day().month(.wide)
        style.calendar = Calendar(identifier: .gregorian)
        return style
    }()
}

/// A meeting's context menu: Send Again for a failed one, Folders, Delete.
struct MeetingMenu: View {
    @Environment(AppModel.self) private var model
    let item: AppModel.Item

    var body: some View {
        if item.canRetry { Button(item.inCloud ? "Send Again" : "Try Again") { model.retry(item) } }
        // Only a meeting that's been sent: one still on this Mac can't be filed yet.
        Menu("Folders") { FolderChoices(item: item) }.disabled(!item.inCloud)
        Divider()
        Button("Delete…", role: .destructive) { model.deleting = item }.disabled(!item.canDelete)
    }
}

/// The owner's folders, by path, each ticked when the meeting is in it: a click adds it or takes it out, as
/// Mail's labels do. The Folders menu, and the header's.
struct FolderChoices: View {
    @Environment(AppModel.self) private var model
    let item: AppModel.Item

    var body: some View {
        if model.folders.isEmpty { Text("No folders yet: make one with + in the sidebar") }
        ForEach(model.folderChoices) { folder in
            Toggle(model.path(folder.folderId), isOn: Binding(get: { item.folderIds.contains(folder.folderId) },
                                                             set: { inside in Task { await model.file(item.id, in: folder.folderId, inside) } }))
        }
    }
}

/// What a search found: each meeting, newest first, with where it matched and a snippet.
private struct SearchResults: View {
    @Environment(AppModel.self) private var model
    let results: [API.SearchHit]

    var body: some View {
        @Bindable var model = model
        if results.isEmpty, model.searchFailed {
            ContentUnavailableView("Couldn't Search", systemImage: "magnifyingglass",
                                   description: Text(model.auth.state != .signedIn ? "Sign in to search your meetings."
                                                     : model.offline ? "You're offline. Try again when you're back online." : "Try again in a moment."))
        } else if results.isEmpty {
            ContentUnavailableView.search(text: model.query)
        } else {
            List(selection: $model.selection) {
                Section(results.count == 1 ? "1 meeting" : "\(results.count) meetings") {
                    ForEach(results) { hit in
                        VStack(alignment: .leading, spacing: 3) {
                            Text(hit.title ?? "Meeting").font(.headline).lineLimit(1)
                            Text([hit.startedAt?.formatted(date: .abbreviated, time: .shortened), Self.where(hit)].compactMap { $0 }.joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                            Text(marked(hit.snippet, model.query)).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                        }
                        .padding(.vertical, 2)
                        .tag(AppModel.Selection.meeting(hit.meetingId))
                        .draggable(Dragged(id: hit.meetingId, text: model.items.first { $0.id == hit.meetingId }.map(model.dragText) ?? hit.title ?? ""))
                        .contextMenu { if let item = model.items.first(where: { $0.id == hit.meetingId }) { MeetingMenu(item: item) } }
                    }
                }
            }
        }
    }

    private static func `where`(_ hit: API.SearchHit) -> String {
        switch hit.where {
        case "title": "In the title"
        case "summary": "In the summary"
        case "people": "Who was there"
        default: hit.at.map { "Said at \(clock($0))" } ?? "In the transcript"
        }
    }
}

/// Record, over the list of meetings; while recording, Stop with the time, in red, as in Voice Memos. ⌘R does
/// the same, from the app's menu, and so does the menu bar.
struct RecordButton: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if model.recorder != nil {
            Button { Task { await model.stopRecording() } } label: {
                Label("Stop Recording \(model.elapsed)", systemImage: "stop.circle.fill").labelStyle(.titleAndIcon).monospacedDigit()
            }
            .tint(.red).foregroundStyle(.red).help("Stop recording (⌘R)")
        } else {
            Button { Task { await model.startRecording() } } label: { Label("Record", systemImage: "record.circle").labelStyle(.titleAndIcon) }
                .disabled(model.starting).help("Start a new recording (⌘R)")
        }
    }
}

/// A meeting in the window's list, as Mail shows a message: its title (two lines if it needs them) with the time
/// beside it, and under it how long it was and the folders it's in, or where it is when it isn't ready. A status
/// icon only for one that isn't ready, and an orange count of people to confirm.
private struct ListRow: View {
    let item: AppModel.Item
    let toConfirm: Int
    let folders: String?

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            if item.status != .ready { StatusIcon(status: item.status).alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 } }
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(item.title ?? "Recording at \(item.startedAt.formatted(date: .omitted, time: .shortened))")
                        .fontWeight(.semibold).lineLimit(2)
                    Spacer(minLength: 4)
                    Text(item.startedAt.formatted(date: .omitted, time: .shortened)).font(.caption).monospacedDigit().foregroundStyle(.secondary)
                }
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(detail).font(.callout).foregroundStyle(item.sendError == nil ? .secondary : Color.orange).lineLimit(1)
                    Spacer(minLength: 4)
                    if toConfirm > 0 { ToConfirmBadge(count: toConfirm) }
                }
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
        .accessibilityValue(toConfirm > 0 ? AppModel.toConfirmText(toConfirm) : "")
    }

    private var detail: String {
        if let error = item.sendError { return error }
        let length = item.duration.map(duration)
        switch item.status {
        case .ready: return [length, folders].compactMap { $0 }.joined(separator: " · ")
        case .failed(let error): return error
        default: return [statusText(item.status), length].compactMap { $0 }.joined(separator: " · ")
        }
    }
}

/// The snippet with the words searched for in bold, as Mail marks them.
func marked(_ snippet: String, _ query: String) -> AttributedString {
    var text = AttributedString(snippet)
    for word in query.split(separator: " ") {
        var from = text.startIndex
        while let found = text[from...].range(of: String(word), options: [.caseInsensitive, .diacriticInsensitive]) {
            text[found].inlinePresentationIntent = .stronglyEmphasized
            text[found].foregroundColor = .primary
            from = found.upperBound
        }
    }
    return text
}

extension UTType { static let tallyItem = UTType(exportedAs: "\(Bundle.main.bundleIdentifier ?? "tally").item") }

/// What's dragged, a meeting or a folder: its id for Tally's own drops (onto a folder), and as text for anywhere
/// else, so a meeting dropped in Mail or Notes is its title, when it was and what it was about, not an id.
struct Dragged: Codable, Transferable {
    let id: String
    let text: String

    static var transferRepresentation: some TransferRepresentation {
        CodableRepresentation(contentType: .tallyItem)
        ProxyRepresentation(exporting: \.text)
    }
}
