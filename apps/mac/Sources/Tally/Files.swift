import QuickLook
import SwiftUI
import UniformTypeIdentifiers

/// A meeting's Files tab: what the owner keeps with the meeting, such as slides, a document, or a
/// screenshot of what was shared. Files are dropped here, pasted (⌘V, a screenshot taken with Control too), or
/// chosen with Add Files…. As in Finder: a click selects one, Space shows it in Quick Look, a double-click opens it,
/// ← and → move, and Delete asks to delete it.
struct FilesPage: View {
    @Environment(AppModel.self) private var model
    let meeting: String
    private let choosing = State(initialValue: false)
    private let targeted = State(initialValue: false)  // something's being dragged over the page
    private let preview = State<URL?>(initialValue: nil)  // what Quick Look shows
    private let deleting = State<API.File?>(initialValue: nil)
    private let selected = State<String?>(initialValue: nil)
    private let focus = FocusState<Bool>()

    var body: some View {
        let loaded = model.files[meeting]
        let files = loaded ?? []
        let adding = model.addingFiles[meeting] ?? 0
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                if adding > 0 {
                    ProgressView().controlSize(.small)
                    Text(adding == 1 ? "Adding 1 file…" : "Adding \(adding) files…").foregroundStyle(.secondary)
                }
                Spacer()
                Button("Add Files…", systemImage: "plus") { choosing.wrappedValue = true }
            }
            if files.isEmpty {
                ContentUnavailableView {
                    Label("No Files", systemImage: "doc.on.doc")
                } description: {
                    Text(loaded == nil && (model.offline || model.auth.state != .signedIn)
                         ? (model.offline ? "Files already here show once you're back online." : "Sign in to see the files already here.")
                         : "Keep slides, documents and screenshots with this meeting: drop them here, or paste them with ⌘V.")
                }
                .frame(minHeight: 200)
                // the tint only while something's dragged over, as Finder shows a drop
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.tint, lineWidth: 2).opacity(targeted.wrappedValue ? 1 : 0))
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 130), spacing: 16)], alignment: .leading, spacing: 16) {
                    ForEach(files) { file in
                        FileTile(file: file, opening: model.opening.contains(file.fileId), selected: selected.wrappedValue == file.fileId)
                            .onTapGesture(count: 2) { open(file) }
                            .onTapGesture { selected.wrappedValue = file.fileId; focus.wrappedValue = true }
                            .accessibilityAction { lookAt(file) }
                            .contextMenu {
                                Button("Quick Look") { lookAt(file) }
                                Button("Open") { open(file) }
                                Divider()
                                Button("Delete…", role: .destructive) { deleting.wrappedValue = file }
                            }
                    }
                }
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 10).strokeBorder(.tint, lineWidth: 2).opacity(targeted.wrappedValue ? 1 : 0))
            }
        }
        .contentShape(.rect)
        .focusable().focusEffectDisabled().focused(focus.projectedValue)
        .onAppear { focus.wrappedValue = true }  // so ⌘V reaches it
        .task { await model.loadFiles(meeting) }  // fresh links, valid for an hour
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(AppModel.isFile)  // a link from a browser, or a folder, isn't one: the drop says no
            Task { await model.add(files, to: meeting) }
            return !files.isEmpty
        } isTargeted: { targeted.wrappedValue = $0 }
        .onPasteCommand(of: [.fileURL, .image]) { _ in paste() }
        .onKeyPress(.space) {  // Quick Look, and Space again closes it
            if preview.wrappedValue != nil { preview.wrappedValue = nil } else if let file = choice(files) { lookAt(file) }
            return .handled
        }
        .onKeyPress(.leftArrow) { step(-1, files) }
        .onKeyPress(.rightArrow) { step(1, files) }
        .onDeleteCommand { deleting.wrappedValue = choice(files) }
        .fileImporter(isPresented: choosing.projectedValue, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            guard let urls = try? result.get() else { return }
            Task {
                let open = urls.filter { $0.startAccessingSecurityScopedResource() }
                await model.add(urls, to: meeting)
                open.forEach { $0.stopAccessingSecurityScopedResource() }
            }
        }
        .quickLookPreview(preview.projectedValue)
        .confirmationDialog("Delete “\(deleting.wrappedValue?.name ?? "this file")”?",
                            isPresented: Binding(get: { deleting.wrappedValue != nil }, set: { if !$0 { deleting.wrappedValue = nil } }),
                            presenting: deleting.wrappedValue) { file in
            Button("Delete", role: .destructive) { Task { await model.delete(file, of: meeting) } }
        } message: { _ in Text("It's deleted from this meeting. This can't be undone.") }
    }

    private func choice(_ files: [API.File]) -> API.File? { files.first { $0.fileId == selected.wrappedValue } }

    private func step(_ by: Int, _ files: [API.File]) -> KeyPress.Result {
        guard !files.isEmpty else { return .ignored }
        let at = files.firstIndex { $0.fileId == selected.wrappedValue }.map { $0 + by } ?? 0
        selected.wrappedValue = files[min(max(at, 0), files.count - 1)].fileId
        return .handled
    }

    private func lookAt(_ file: API.File) {
        selected.wrappedValue = file.fileId
        Task { preview.wrappedValue = await model.local(file, of: meeting) }
    }

    private func open(_ file: API.File) {
        Task { if let url = await model.local(file, of: meeting) { NSWorkspace.shared.open(url) } }
    }

    /// Files copied in Finder, or an image (a screenshot copied with Control) as a PNG.
    private func paste() {
        let urls = NSPasteboard.general.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        if !urls.isEmpty {
            Task { await model.add(urls, to: meeting) }
        } else {
            Task { await model.pasteImage(to: meeting) }
        }
    }
}

/// A file: a picture of it (the image itself, or its kind's icon), its name, and its size and when it was added.
private struct FileTile: View {
    let file: API.File
    let opening: Bool  // downloading, to show: the first time, it takes a moment
    let selected: Bool

    var body: some View {
        let type = UTType(filenameExtension: (file.name as NSString).pathExtension) ?? .data
        VStack(spacing: 6) {
            Group {
                if type.conforms(to: .image) {
                    AsyncImage(url: file.url) { image in
                        image.resizable().scaledToFill()
                    } placeholder: {
                        Image(nsImage: NSWorkspace.shared.icon(for: type)).resizable().scaledToFit().padding(24)
                    }
                } else {
                    Image(nsImage: NSWorkspace.shared.icon(for: type)).resizable().scaledToFit().padding(16)
                }
            }
            .frame(width: 130, height: 96)
            .background(selected ? Color.primary.opacity(0.1) : .clear, in: .rect(cornerRadius: 8).inset(by: -4))
            .overlay { if opening { ProgressView().controlSize(.small).padding(8).background(.regularMaterial, in: .circle) } }
            .clipShape(.rect(cornerRadius: 6))
            .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 6))
            Text(file.name).font(.callout).lineLimit(2).truncationMode(.middle).multilineTextAlignment(.center)
                .padding(.horizontal, 4).padding(.vertical, 1)
                .background(selected ? Color.accentColor : .clear, in: .rect(cornerRadius: 4))  // as Finder marks a selected icon's name
                .foregroundStyle(selected ? .white : .primary)
            Text("\(file.size.formatted(.byteCount(style: .file))) · \(file.addedAt.formatted(date: .abbreviated, time: .omitted))")
                .font(.caption).foregroundStyle(.secondary)
        }
        .frame(width: 130)
        .contentShape(.rect)
        .help(file.name)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}
