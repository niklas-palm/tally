import SwiftUI

/// Everyone Tally knows, as Contacts lists them: each name in full, what they do under it, and whether their voice
/// is confirmed. The one chosen shows beside it, the first at once, so the page is never empty. Sort By, in the
/// toolbar, orders them by name, company, meetings or voice, either way.
struct PeopleView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        let people = sorted()
        Group {
            if people.isEmpty {
                ContentUnavailableView(model.auth.state != .signedIn ? "Sign In to See Your People" : model.offline ? "Your People Show Here Once You're Back Online" : "No People Yet",
                                       systemImage: "person.2", description: Text("People appear here as Tally hears them in your meetings."))
            } else {
                List(selection: $model.peopleSelection) {
                    ForEach(people) { row in PersonRow(row: row).tag(row.id) }
                }
                .contextMenu(forSelectionType: String.self) { ids in
                    if let person = model.people.first(where: { ids.contains($0.personId) }) {
                        Button("Edit…") { model.editing = PersonForm(person) }
                        Button("Delete \(person.name)…", role: .destructive) { model.deletingPerson = person }
                    }
                } primaryAction: { ids in
                    if let person = model.people.first(where: { ids.contains($0.personId) }) { model.editing = PersonForm(person) }
                }
                .onDeleteCommand { if let person = model.selectedPerson { model.deletingPerson = person } }
                .onAppear { if model.selectedPerson == nil { model.peopleSelection = people.first?.id } }
            }
        }
        .toolbar {
            ToolbarItem {
                Menu {
                    Picker("Sort By", selection: $model.peopleSort) {
                        ForEach(AppModel.PeopleSort.allCases, id: \.self) { Text($0.rawValue) }
                    }
                    .pickerStyle(.inline)
                    Picker("Order", selection: $model.peopleAscending) {
                        Text(model.peopleSort == .name || model.peopleSort == .company ? "A to Z" : "Fewest First").tag(true)
                        Text(model.peopleSort == .name || model.peopleSort == .company ? "Z to A" : "Most First").tag(false)
                    }
                    .pickerStyle(.inline)
                } label: { Label("Sort By", systemImage: "arrow.up.arrow.down") }
                    .help("Sort people by name, company, meetings or voice")
            }
        }
    }

    private func sorted() -> [PeopleRow] {
        let rows = model.people.map { PeopleRow(person: $0, meetings: model.meetings(with: $0.personId).count) }
        let ordered = rows.sorted { a, b in
            switch model.peopleSort {
            case .name: a.name.localizedStandardCompare(b.name) == .orderedAscending
            case .company where a.company.isEmpty != b.company.isEmpty: !a.company.isEmpty  // the unknown after the known
            case .company: a.company == b.company ? a.name.localizedStandardCompare(b.name) == .orderedAscending
                : a.company.localizedStandardCompare(b.company) == .orderedAscending
            case .meetings: a.meetings == b.meetings ? a.name.localizedStandardCompare(b.name) == .orderedAscending : a.meetings < b.meetings
            case .voice: a.voice == b.voice ? a.name.localizedStandardCompare(b.name) == .orderedAscending : a.voice > b.voice  // confirmed last, so Most First puts them on top
            }
        }
        return model.peopleAscending ? ordered : ordered.reversed()
    }
}

/// A person in the list: their name in full (with You), what they do, and a seal when their voice is confirmed.
private struct PersonRow: View {
    let row: PeopleRow

    var body: some View {
        HStack(spacing: 10) {
            Initials(name: row.name, size: 30)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    Text(row.name).lineLimit(1)
                    if row.person.isOwner { YouBadge() }
                }
                Text(row.person.detail ?? "No role or company yet").font(.caption)
                    .foregroundStyle(row.person.detail == nil ? .tertiary : .secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            if row.person.confirmed {
                Image(systemName: "checkmark.seal.fill").foregroundStyle(.green).help("Voice confirmed").accessibilityLabel("Voice confirmed")
            }
        }
        .padding(.vertical, 2)
    }
}

/// Someone's initials in a circle, as Contacts shows a person without a photo.
struct Initials: View {
    let name: String
    let size: CGFloat

    var body: some View {
        let letters = name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
        Text(letters.uppercased())
            .font(.system(size: size * 0.4, weight: .semibold)).foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Color.gray.gradient, in: .circle)
            .accessibilityHidden(true)
    }
}

/// Editing a person: what Tally calls them, what they do, and whether it's the owner.
struct PersonSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Bindable var form: PersonForm

    var body: some View {
        let person = model.people.first { $0.personId == form.personId }
        VStack(alignment: .leading, spacing: 16) {
            Text(person?.name ?? "Edit person").font(.title2.bold())
            Form {
                TextField("Name", text: $form.name)
                TextField("Role", text: $form.role, prompt: Text("e.g. Head of finance"))
                TextField("Company", text: $form.company, prompt: Text("e.g. Acme"))
                Toggle(isOn: $form.isOwner) {
                    Text("This is me")
                    Text("Tally marks you with a You badge.")
                }
            }
            .formStyle(.grouped)
            if let error = form.error { Text(error).font(.callout).foregroundStyle(.red) }
            HStack {
                if let person { Button("Delete Person…", role: .destructive) { model.deletingPerson = person; dismiss() } }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button { Task { await model.save(form) } } label: {
                    if model.saving { ProgressView().controlSize(.small) } else { Text("Save") }
                }
                .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                .disabled(model.saving || form.name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 440)
    }
}

/// One person, as Contacts shows a card: who they are, their voice, and every meeting they were named in, newest
/// first, each opening with a click. Edit and Delete are in the toolbar above it.
struct PersonPanel: View {
    @Environment(AppModel.self) private var model
    let person: API.Person

    var body: some View {
        let meetings = model.meetings(with: person.personId)
            .sorted { ($0.startedAt ?? .distantPast) > ($1.startedAt ?? .distantPast) }
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                HStack(spacing: 16) {
                    Initials(name: person.name, size: 64)
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 8) {
                            Text(person.name).font(.title.bold())
                            if person.isOwner { YouBadge() }
                        }
                        if let detail = person.detail { Text(detail).font(.title3).foregroundStyle(.secondary) }
                    }
                }
                // Labels on the left, grey and right-aligned, as a Contacts card has them
                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 12, verticalSpacing: 8) {
                    row("role") { Text(person.role ?? "—").foregroundStyle(person.role == nil ? .tertiary : .primary) }
                    row("company") { Text(person.company ?? "—").foregroundStyle(person.company == nil ? .tertiary : .primary) }
                    row("voice") {
                        Label(person.confirmed ? "Confirmed" : person.voiceprints.isEmpty ? "Not heard yet" : "Not confirmed yet",
                              systemImage: person.confirmed ? "checkmark.seal.fill" : "waveform")
                            .foregroundStyle(person.confirmed ? AnyShapeStyle(.green) : AnyShapeStyle(.secondary))
                    }
                    if !person.aliases.isEmpty { row("also known as") { Text(person.aliases.formatted(.list(type: .and))) } }
                }
                VStack(alignment: .leading, spacing: 0) {
                    Text(meetings.isEmpty ? "No Meetings Yet" : meetings.count == 1 ? "1 Meeting" : "\(meetings.count) Meetings")
                        .font(.headline).padding(.bottom, 8)
                    ForEach(meetings, id: \.meetingId) { meeting in
                        Divider()
                        Button { model.open(meeting.meetingId) } label: {
                            HStack(alignment: .firstTextBaseline) {
                                Text(meeting.title ?? "Meeting").lineLimit(1)
                                Spacer()
                                if let date = meeting.startedAt {
                                    Text(date.formatted(date: .abbreviated, time: .omitted)).font(.callout).foregroundStyle(.secondary)
                                }
                            }
                            .padding(.vertical, 8)
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .help("Open this meeting")
                    }
                }
            }
            .frame(maxWidth: 640, alignment: .leading)
            .padding(32)
            .frame(maxWidth: .infinity)
        }
    }

    private func row(_ label: String, @ViewBuilder value: () -> some View) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary).gridColumnAlignment(.trailing)
            value()
        }
    }
}

/// A person with what the list sorts them by.
struct PeopleRow: Identifiable {
    let person: API.Person
    let meetings: Int  // the meetings they were named in
    var id: String { person.personId }
    var name: String { person.name }
    var company: String { person.company ?? "" }
    var voice: Int { person.confirmed ? 0 : person.voiceprints.isEmpty ? 2 : 1 }  // confirmed, not confirmed yet, not heard yet
}
