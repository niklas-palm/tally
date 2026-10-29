using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Tally;

/// <summary>
/// Everyone Tally knows, as People lists contacts: each name in full with their initials, what they do under it, a
/// seal when their voice is confirmed, and how many meetings they were named in. Sort By, over the list, orders them
/// by name, company, meetings or voice, either way. The first is chosen at once, so the card beside it is never empty.
/// </summary>
internal sealed class PeopleList : Grid
{
    private readonly AppModel model;
    private readonly TextBlock count = Ui.Caption("");
    private readonly Border body = new();
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 0, 0, 12) };
    private string drawn = "";
    private bool selecting;

    public PeopleList(AppModel model)
    {
        this.model = model;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = Ui.Text("People", "SubtitleTextBlockStyle", wrap: false);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var header = Ui.Grid("*,Auto", spacing: 8).Add(Ui.Column(0, title, count), Sort().At(1));
        header.Padding = new Thickness(16, 10, 12, 10);
        Children.Add(header);
        Children.Add(body.At(0, 1));
        list.SelectionChanged += (_, _) => { if (!selecting && list.SelectedItem is ListViewItem { Tag: string id }) model.PeopleSelection = id; };
        list.DoubleTapped += (_, _) => Edit();
        list.PreviewKeyDown += (_, e) =>  // before the list, which takes Enter for itself
        {
            if (e.Key == VirtualKey.Enter) Edit();
            else if (e.Key == VirtualKey.Delete && model.SelectedPerson is { } person) model.DeletingPerson = person;
            else return;
            e.Handled = true;
        };
        Draw();
    }

    private void Edit()
    {
        if (model.SelectedPerson is { } person) model.Editing = new PersonForm(person);
    }

    public void Changed(string? name)
    {
        if (name == nameof(AppModel.PeopleSelection)) Select();
        else if (name is nameof(AppModel.People) or nameof(AppModel.Meetings) or nameof(AppModel.PeopleSort) or nameof(AppModel.PeopleAscending) or nameof(AppModel.Offline) or "Auth") Draw();
    }

    /// <summary>Sort, as File Explorer has it: by what, then which way.</summary>
    private DropDownButton Sort()
    {
        var menu = Ui.Menu(items =>
        {
            foreach (var sort in Enum.GetValues<PeopleSort>())
            {
                var choice = new RadioMenuFlyoutItem { Text = sort.ToString(), GroupName = "sort", IsChecked = model.PeopleSort == sort };
                choice.Click += (_, _) => model.PeopleSort = sort;
                items.Add(choice);
            }
            items.Add(new MenuFlyoutSeparator());
            var alphabetical = model.PeopleSort is PeopleSort.Name or PeopleSort.Company;
            foreach (var ascending in new[] { true, false })
            {
                var choice = new RadioMenuFlyoutItem
                {
                    Text = alphabetical ? ascending ? "A to Z" : "Z to A" : ascending ? "Fewest First" : "Most First",
                    GroupName = "order", IsChecked = model.PeopleAscending == ascending,
                };
                choice.Click += (_, _) => model.PeopleAscending = ascending;
                items.Add(choice);
            }
        });
        var button = new DropDownButton { Content = Ui.Row(6, Ui.Icon(Ui.SortGlyph, 14), new TextBlock { Text = "Sort" }), Flyout = menu };
        ToolTipService.SetToolTip(button, "Sort people by name, company, meetings or voice");
        Ui.Name(button, "Sort By");
        return button;
    }

    private List<(Person Person, int Meetings)> Sorted()
    {
        static int Compare(string a, string b) => string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        static int Voice(Person p) => p.Confirmed ? 0 : p.Voiceprints.Count == 0 ? 2 : 1;  // confirmed, not confirmed yet, not heard yet
        var rows = model.People.Select(p => (Person: p, Meetings: model.MeetingsWith(p.PersonId).Count)).ToList();
        rows.Sort((a, b) =>
        {
            var byName = Compare(a.Person.Name, b.Person.Name);
            var (ca, cb) = (a.Person.Company ?? "", b.Person.Company ?? "");
            return model.PeopleSort switch
            {
                PeopleSort.Company when ca.Length == 0 != (cb.Length == 0) => ca.Length == 0 ? 1 : -1,  // the unknown after the known
                PeopleSort.Company => ca == cb ? byName : Compare(ca, cb),
                PeopleSort.Meetings => a.Meetings == b.Meetings ? byName : a.Meetings.CompareTo(b.Meetings),
                PeopleSort.Voice => Voice(a.Person) == Voice(b.Person) ? byName : Voice(b.Person).CompareTo(Voice(a.Person)),  // confirmed last, so Most First puts them on top
                _ => byName,
            };
        });
        if (!model.PeopleAscending) rows.Reverse();
        return rows;
    }

    private void Draw()
    {
        count.Text = model.People.Count == 1 ? "1 person" : $"{model.People.Count} people";
        var people = Sorted();
        var signature = string.Join(";", people.Select(r => Signature(r.Person) + r.Meetings)) + model.Offline + model.Auth.State;
        if (signature == drawn) return;
        drawn = signature;
        if (people.Count == 0)
        {
            body.Child = Ui.Placeholder(Ui.PeopleGlyph, model.Auth.State != AuthState.SignedIn ? "Sign In to See Your People"
                : model.Offline ? "Your People Show Here Once You're Back Online" : "No People Yet", "People appear here as Tally hears them in your meetings.");
            return;
        }
        selecting = true;
        list.Items.Clear();
        foreach (var (person, meetings) in people) list.Items.Add(Row(person, meetings));
        selecting = false;
        body.Child = list;
        if (model.SelectedPerson is null) model.PeopleSelection = people[0].Person.PersonId;
        Select();
    }

    /// <summary>What a row and a card show of a person: a record's own text wouldn't say what its lists hold.</summary>
    public static string Signature(Person p) =>
        string.Join("|", p.PersonId, p.Name, p.Role, p.Company, p.IsOwner, p.Confirmed, p.Voiceprints.Count, string.Join(",", p.Aliases));

    private void Select()
    {
        selecting = true;
        list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == model.PeopleSelection);
        selecting = false;
    }

    private ListViewItem Row(Person person, int meetings)
    {
        var grid = Ui.Grid("Auto,*,Auto", spacing: 10).Add(
            Ui.Initials(person.Name, 32),
            Ui.Column(1,
                Ui.Row(6, Ui.Text(person.Name, wrap: false), person.IsOwner ? Ui.YouBadge() : null),
                Ui.Caption(person.Detail ?? "No role or company yet", person.Detail is null ? Ui.Theme("TextFillColorTertiaryBrush") : null)
                    .With(c => { c.TextWrapping = TextWrapping.NoWrap; c.TextTrimming = TextTrimming.CharacterEllipsis; })).At(1),
            Ui.Row(6, person.Confirmed ? Ui.Seal() : null, Ui.Caption(meetings.ToString(CultureInfo.CurrentCulture)).With(c =>
            {
                c.VerticalAlignment = VerticalAlignment.Center;
                ToolTipService.SetToolTip(c, meetings == 1 ? "Named in 1 meeting" : $"Named in {meetings} meetings");
            })).With(r => r.VerticalAlignment = VerticalAlignment.Center).At(2));
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item("Edit…", () => model.Editing = new PersonForm(person), glyph: Ui.EditGlyph));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item($"Delete {person.Name}…", () => model.DeletingPerson = person, glyph: Ui.DeleteGlyph));
        var row = new ListViewItem { Content = grid, Tag = person.PersonId, Padding = new Thickness(16, 6, 12, 6), ContextFlyout = menu };
        Ui.Name(row, string.Join(", ", new[] { person.Name, person.IsOwner ? "you" : null, person.Detail, person.Confirmed ? "voice confirmed" : null,
            meetings == 1 ? "1 meeting" : $"{meetings} meetings" }.OfType<string>()));
        return row;
    }
}

/// <summary>
/// One person, as a contact card: who they are, their voice, the names they're also known by, and every meeting they
/// were named in, newest first, each opening with a click. Edit and Delete are over it.
/// </summary>
internal sealed class PersonPanel : Grid
{
    private readonly AppModel model;
    private readonly Border page = new();
    private string drawn = "";

    public string Id { get; }

    public PersonPanel(AppModel model, string id)
    {
        this.model = model;
        Id = id;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var edit = new AppBarButton { Label = "Edit", Icon = new FontIcon { Glyph = Ui.EditGlyph } };
        edit.Click += (_, _) => { if (Person is { } person) model.Editing = new PersonForm(person); };
        ToolTipService.SetToolTip(edit, "Edit this person (Enter)");
        var delete = new AppBarButton { Label = "Delete", Icon = new FontIcon { Glyph = Ui.DeleteGlyph } };
        delete.Click += (_, _) => model.DeletingPerson = Person;
        ToolTipService.SetToolTip(delete, "Delete this person (Delete)");
        var commands = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        commands.PrimaryCommands.Add(edit);
        commands.PrimaryCommands.Add(delete);
        Children.Add(new Border { Child = commands, Padding = new Thickness(20, 4, 8, 4), MinHeight = 52 });
        Children.Add(page.At(0, 1));
        Draw();
    }

    private Person? Person => model.People.FirstOrDefault(p => p.PersonId == Id);

    public void Changed(string? name)
    {
        if (name is nameof(AppModel.People) or nameof(AppModel.Meetings)) Draw();
    }

    private void Draw()
    {
        if (Person is not { } person) return;
        var meetings = model.MeetingsWith(person.PersonId).OrderByDescending(m => m.StartedAt ?? DateTimeOffset.MinValue).ToList();
        var signature = PeopleList.Signature(person) + string.Join(",", meetings.Select(m => m.MeetingId + m.Title));
        if (signature == drawn) return;
        drawn = signature;

        var name = Ui.Text(person.Name, "TitleTextBlockStyle");
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level1);
        var who = Ui.Row(16, Ui.Initials(person.Name, 64), Ui.Column(2, Ui.Row(8, name, person.IsOwner ? Ui.YouBadge() : null),
            person.Detail is { } detail ? Ui.Text(detail, "BodyLargeTextBlockStyle", Ui.Secondary) : null).With(c => c.VerticalAlignment = VerticalAlignment.Center));

        // Labels on the left, grey and right-aligned, as a contact card has them
        var card = Ui.Grid("Auto,*", spacing: 12).With(g => g.RowSpacing = 8);
        var row = 0;
        void Add(string label, UIElement value)
        {
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Add(Ui.Text(label, brush: Ui.Secondary).With(t => t.HorizontalAlignment = HorizontalAlignment.Right).At(0, row), ((FrameworkElement)value).At(1, row));
            row++;
        }
        Add("Role", Ui.Text(person.Role ?? "—", brush: person.Role is null ? Ui.Theme("TextFillColorTertiaryBrush") : null));
        Add("Company", Ui.Text(person.Company ?? "—", brush: person.Company is null ? Ui.Theme("TextFillColorTertiaryBrush") : null));
        Add("Voice", person.Confirmed
            ? Ui.Row(6, Ui.Seal(), Ui.Text("Confirmed", brush: Ui.Success))
            : Ui.Row(6, Ui.Icon(Ui.WaveGlyph, 14, Ui.Secondary), Ui.Text(person.Voiceprints.Count == 0 ? "Not heard yet" : "Not confirmed yet", brush: Ui.Secondary)));
        if (person.Aliases.Count > 0) Add("Also known as", Ui.Text(SummaryPage.And(person.Aliases)));

        var list = new StackPanel();
        list.Children.Add(Ui.Heading(meetings.Count == 0 ? "No Meetings Yet" : meetings.Count == 1 ? "1 Meeting" : $"{meetings.Count} Meetings").With(h => h.Margin = new Thickness(0, 0, 0, 8)));
        foreach (var meeting in meetings)
        {
            list.Children.Add(Ui.Divider().With(d => d.Margin = new Thickness(0)));
            var open = new Button
            {
                Content = Ui.Grid("*,Auto", spacing: 12).Add(Ui.Text(meeting.Title ?? "Meeting", wrap: false),
                    meeting.StartedAt is { } date ? Ui.Text(date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture), brush: Ui.Secondary).At(1) : null),
                Style = Ui.Style("SubtleButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 9, 8, 9),
                Margin = new Thickness(-8, 1, -8, 1),
            };
            open.Click += (_, _) => model.Open(meeting.MeetingId);
            ToolTipService.SetToolTip(open, "Open this meeting");
            list.Children.Add(open);
        }

        var column = Ui.Column(28, who, card, list);
        column.MaxWidth = 704;
        column.Padding = new Thickness(32, 12, 32, 40);
        page.Child = new ScrollViewer { Content = column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };
    }
}

/// <summary>Editing a person: what Tally calls them, what they do, and whether it's the owner. Saving keeps it open
/// until the API has it, and says there why not if it couldn't.</summary>
internal sealed class PersonDialog
{
    private readonly AppModel model;
    private readonly TextBlock error = Ui.Text("", brush: new SolidColorBrush(Ui.Red));

    public PersonForm Form { get; }
    public ContentDialog Dialog { get; }

    public PersonDialog(AppModel model, PersonForm form)
    {
        this.model = model;
        Form = form;
        var person = model.People.FirstOrDefault(p => p.PersonId == form.PersonId);
        var name = new TextBox { Header = "Name", Text = form.Name };
        var role = new TextBox { Header = "Role", Text = form.Role, PlaceholderText = "e.g. Head of finance" };
        var company = new TextBox { Header = "Company", Text = form.Company, PlaceholderText = "e.g. Acme" };
        var me = new CheckBox { Content = "This is me", IsChecked = form.IsOwner };
        name.TextChanged += (_, _) => { form.Name = name.Text; Enable(); };
        role.TextChanged += (_, _) => form.Role = role.Text;
        company.TextChanged += (_, _) => form.Company = company.Text;
        me.Click += (_, _) => form.IsOwner = me.IsChecked == true;
        foreach (var box in new[] { name, role, company })
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Save(); } };
        var delete = Ui.Link("Delete Person…", () =>
        {
            Dialog!.Hide();
            model.DeletingPerson = person;
        });
        delete.Foreground = new SolidColorBrush(Ui.Red);
        delete.Margin = new Thickness(-4, 0, 0, 0);
        error.Visibility = Visibility.Collapsed;
        Dialog = new ContentDialog
        {
            Title = person?.Name ?? "Edit Person",
            Content = Ui.Column(12, name, role, company, Ui.Column(0, me, Ui.Caption("Tally marks you with a You badge.").With(c => c.Margin = new Thickness(28, -6, 0, 0))),
                error, person is null ? null : delete).With(c => c.Width = 380),
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Style = Ui.Style("DefaultContentDialogStyle"),
        };
        Dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true;  // it closes once saved
            Save();
        };
        Dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        form.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PersonForm.Error)) return;
            error.Text = form.Error ?? "";
            error.Visibility = form.Error is null ? Visibility.Collapsed : Visibility.Visible;
        };
        Enable();
    }

    /// <summary>Save, unless it's saving or there's no name.</summary>
    public void Enable()
    {
        Dialog.IsPrimaryButtonEnabled = !model.Saving && Form.Name.Trim().Length > 0;
        Dialog.PrimaryButtonText = model.Saving ? "Saving…" : "Save";
    }

    private void Save()
    {
        if (!Dialog.IsPrimaryButtonEnabled) return;
        Form.Error = null;
        _ = model.Save(Form);
    }
}
