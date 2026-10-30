import SwiftUI

/// The window, as Mail and Notes are: where you are (Meetings, your folders, People), what's there, and the one
/// chosen.
struct MainWindow: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            Sidebar()
                .navigationSplitViewColumnWidth(min: 200, ideal: 230)
        } content: {
            if model.place == .people {
                PeopleView().navigationSplitViewColumnWidth(min: 260, ideal: 300)
            } else {
                MeetingList().navigationSplitViewColumnWidth(min: 260, ideal: 300)
            }
        } detail: {
            Group {
                if model.place == .people {
                    if let person = model.selectedPerson {
                        PersonPanel(person: person)
                    } else {
                        ContentUnavailableView("No Person Selected", systemImage: "person.2", description: Text("Their meetings show here."))
                    }
                } else if let item = selectedItem {
                    MeetingView(item: item).id(item.id)
                } else if model.items.isEmpty {
                    ScrollView { welcome }  // so its text, wrapped narrow, can never make the window taller than the screen
                } else {
                    ContentUnavailableView("No Meeting Selected", systemImage: "doc.text")
                }
            }
            .navigationSplitViewColumnWidth(min: 380, ideal: 600)
            // In the detail column, not over the whole split view: the columns run up under the toolbar, so an inset on
            // the split view would cover the sidebar's and the list's first rows rather than move them down
            .safeAreaInset(edge: .top) { banner }
            // Over what's shown, and only about it: as in Mail, the toolbar above each column acts on that column
            .toolbar {
                ToolbarSpacer(.flexible)  // at the far end, by the search field, apart from Record over the list
                if model.place == .people {
                    ToolbarItem {
                        Button { if let person = model.selectedPerson { model.editing = PersonForm(person) } } label: { Label("Edit", systemImage: "pencil") }
                            .disabled(model.selectedPerson == nil).help("Edit this person")
                    }
                }
                ToolbarItem {
                    let inPeople = model.place == .people
                    Button {
                        if inPeople { model.deletingPerson = model.selectedPerson } else if let item = selectedItem { model.deleting = item }
                    } label: { Label("Delete", systemImage: "trash") }
                        .disabled(inPeople ? model.selectedPerson == nil : !(selectedItem?.canDelete ?? false))
                        .help(inPeople ? "Delete this person" : "Delete this meeting")  // no ⌘⌫: in a text field it deletes the line
                }
            }
        }
        .navigationTitle(placeTitle)
        .navigationSubtitle(placeCount)
        .confirmationDialog(deleteTitle, isPresented: Binding(get: { model.deleting != nil }, set: { if !$0 { model.deleting = nil } }),
                            presenting: model.deleting) { item in
            Button("Delete", role: .destructive) { Task { await model.delete(item) } }
        } message: { item in
            Text(item.inCloud
                 ? "Its recording, transcript and summary are deleted, and so is everything Tally learned about voices from it. This can't be undone."
                 + (item.status == .queued || item.status == .processing ? " Tally stops summarising it." : "")
                 : "It's only on this Mac, so it can't be recovered.")
        }
        .confirmationDialog("Delete \(model.deletingPerson?.name ?? "this person")?",
                            isPresented: Binding(get: { model.deletingPerson != nil }, set: { if !$0 { model.deletingPerson = nil } }),
                            presenting: model.deletingPerson) { person in
            Button("Delete", role: .destructive) { Task { await model.delete(person: person) } }
        } message: { _ in
            Text("Tally forgets their voice and stops recognising them. Meetings they were in show them as an unknown speaker.")
        }
        .sheet(item: $model.assigning) { AssignSheet(form: $0) }
        .sheet(item: $model.editing) { PersonSheet(form: $0) }
        .onAppear {
            UserDefaults.standard.set(true, forKey: "welcomed")
            NSApp.setActivationPolicy(.regular)  // in the Dock and ⌘-Tab while the window is open
            if model.selection == nil, let first = model.items.first { model.open(first.id) }
            Task { await model.sync() }  // up to date when looked at, not at the next tick
        }
        .onDisappear { NSApp.setActivationPolicy(.accessory) }
        .onChange(of: model.selection) { if case .meeting(let id) = model.selection { Task { await model.load(id) } } }
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 22) {
            Text("Welcome to Tally").font(.largeTitle.bold())
            Text("Tally records your meetings and gives you back a summary, with who said what.").font(.title3).foregroundStyle(.secondary)
            step(1, "Record", "Click the **T.** in the menu bar, or press ⌘R here. Tally records your microphone and what your Mac plays, so it works in any call app and in the room. macOS asks you to allow each, once.") {
                Button(model.recorder == nil ? "Start Recording" : "Stop Recording") { Task { await model.toggleRecording() } }
            }
            step(2, "Sign in", "Summaries are made in the cloud. Recordings wait safely on this Mac until you're signed in.") {
                if model.auth.state == .signedIn { Label("Signed in", systemImage: "checkmark.circle.fill").foregroundStyle(.green) } else { SignInPrompt(explains: false) }
            }
            step(3, "Confirm who spoke", "Each summary lists the people in it. Confirm a voice once, and Tally recognises it in later meetings.") { EmptyView() }
        }
        .frame(maxWidth: 560, alignment: .leading)
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func step(_ number: Int, _ title: String, _ text: LocalizedStringKey, @ViewBuilder action: () -> some View) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Text("\(number)").font(.headline).frame(width: 28, height: 28).background(.tint.opacity(0.15), in: .circle)
            VStack(alignment: .leading, spacing: 8) {
                Text(title).font(.headline)
                Text(text).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                action()
            }
        }
    }

    private var selectedItem: AppModel.Item? {
        guard case .meeting(let id) = model.selection else { return nil }
        return model.items.first { $0.id == id }
    }

    /// Where you are, as Mail titles a mailbox: the meeting's own title is large on its page already.
    private var placeTitle: String {
        switch model.place {
        case .meetings: "Meetings"
        case .folder(let id): model.folders.first { $0.folderId == id }?.name ?? "Folder"
        case .people: "People"
        }
    }

    /// How many are there: "12 meetings", "1 person".
    private var placeCount: String {
        if model.place == .people { return model.people.count == 1 ? "1 person" : "\(model.people.count) people" }
        let count = model.items(in: model.place).count
        return count == 1 ? "1 meeting" : "\(count) meetings"
    }

    private var deleteTitle: String {
        "Delete \(model.deleting?.title.map { "“\($0)”" } ?? "this meeting")?"
    }

    /// What needs saying, a line each under the toolbar, as the menu says it too (Notices).
    @ViewBuilder private var banner: some View {
        // Shown when Notices has something to say: asked of Notices itself, so the two can't drift apart
        Group(subviews: Notices(inWindow: true, asksToSignIn: !detailAsksToSignIn)) { notices in
            if !notices.isEmpty {
                VStack(spacing: 0) {
                    notices
                    Divider()
                }
                .background(.bar)
            }
        }
    }

    /// The welcome and a recording waiting for sign-in ask to sign in themselves: the banner doesn't ask twice.
    private var detailAsksToSignIn: Bool { model.place != .people && (selectedItem.map { $0.status == .waiting } ?? model.items.isEmpty) }
}
