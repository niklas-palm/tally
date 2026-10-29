using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Tally;

/// <summary>
/// The owner says who a speaker is (from No…, Who's This? or Change): one of the voices that sound like theirs, anyone
/// they know (type to find them: ↑ and ↓ move through the matches, as in Windows search, and Enter picks the one
/// highlighted), someone new with their role and company, nobody they know, or a voice in the background.
/// Typing redraws the lists only, never the fields.
/// </summary>
internal sealed class AssignDialog
{
    private readonly AppModel model;
    private readonly TextBox query, role, company;
    private readonly StackPanel soundsLike, close = new() { Spacing = 2 }, matches = new() { Spacing = 2 }, making;
    private readonly ScrollViewer found;
    private readonly Border someoneNew = new();
    private readonly TextBlock makingTitle = Ui.Text("", "BodyStrongTextBlockStyle");
    private readonly Dictionary<int, Button> rows = [];

    public AssignForm Form { get; }
    public ContentDialog Dialog { get; }

    public AssignDialog(AppModel model, AssignForm form)
    {
        this.model = model;
        Form = form;
        var speaker = Speaker;
        query = new TextBox { PlaceholderText = Close.Count == 0 ? "Name" : "Someone else? Type a name", FontSize = 16 };
        Ui.Name(query, "Name");
        query.TextChanged += (_, _) =>
        {
            Form.Query = query.Text;
            Draw();
        };
        query.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    Form.Highlight = Math.Max(0, Math.Min(Form.Highlight + 1, Listed.Count - (Adding ? 0 : 1)));
                    break;
                case VirtualKey.Up:
                    Form.Highlight = Math.Max(Form.Highlight - 1, 0);
                    break;
                case VirtualKey.Enter:
                    // Enter picks the row highlighted: a person, or Add, which shows the name it makes before it does
                    var listed = Listed;
                    if (Form.Highlight >= 0 && Form.Highlight < listed.Count) Choose(listed[Form.Highlight]);
                    else if (AddHighlighted) Add();
                    break;
                default:
                    return;
            }
            e.Handled = true;
            Draw();
        };
        role = new TextBox { Header = "Role", PlaceholderText = "e.g. Head of finance" };
        company = new TextBox { Header = "Company", PlaceholderText = "e.g. Acme" };
        role.TextChanged += (_, _) => Form.Role = role.Text;
        company.TextChanged += (_, _) => Form.Company = company.Text;
        foreach (var box in new[] { role, company })
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Add(); } };

        soundsLike = Ui.Column(4, Ui.Text("Sounds like", "BodyStrongTextBlockStyle"), close);
        found = new ScrollViewer { Content = matches, MaxHeight = 180 };
        making = Ui.Column(8, makingTitle, role, company);
        var unknown = Ui.Button("Not Someone I Know", () => Send(new Dictionary<string, object> { ["unknown"] = true }));
        var background = Ui.Button("Mark as Background Voice", () => Send(new Dictionary<string, object> { ["background"] = true }));
        ToolTipService.SetToolTip(background, "Someone nearby, not in the meeting: their lines are set aside");
        var content = Ui.Column(14,
            Ui.Grid("*,Auto", spacing: 12).Add(Ui.Text("Who's speaking?", "SubtitleTextBlockStyle"), Speakers.Listen(model, speaker?.ClipUrls ?? []).At(1)),
            speaker is null ? null : Ui.Caption($"Spoke for {Format.Duration(speaker.TalkTime)} in this meeting").With(c => c.Margin = new Thickness(0, -10, 0, 0)),
            soundsLike, query, found, someoneNew, making,
            Ui.Row(8, unknown, background).With(r => r.Margin = new Thickness(0, 4, 0, 0)));
        content.Width = 440;
        Dialog = new ContentDialog
        {
            Content = content,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Style = Ui.Style("DefaultContentDialogStyle"),
        };
        Dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true;  // Add closes it, once
            Add();
        };
        Dialog.Opened += (_, _) => Dialog.DispatcherQueue.TryEnqueue(() => query.Focus(FocusState.Programmatic));  // after the dialog's own first focus, on Listen
        Draw();
    }

    private Speaker? Speaker => model.Results.GetValueOrDefault(Form.Meeting)?.Speakers.GetValueOrDefault(Form.Speaker);
    private string Typed => Form.Query.Trim();

    /// <summary>The owner's people closest in voice, but the one just said No to.</summary>
    private List<Candidate> Close => (Speaker?.Candidates ?? [])
        .Where(c => c.Closeness != null && model.PeopleById.ContainsKey(c.PersonId) && c.PersonId != Form.Rejected).ToList();

    private List<Person> Matches => Typed.Length == 0 ? [] : model.People
        .Where(p => p.PersonId != Form.Rejected && p.Aliases.Prepend(p.Name).Any(n => CultureInfo.CurrentCulture.CompareInfo.IndexOf(n, Typed, CompareOptions.IgnoreCase) >= 0))
        .ToList();

    /// <summary>What ↑ ↓ and Enter go through: the voices alike (or the names typed for), then Add.</summary>
    private List<string> Listed => Typed.Length == 0 ? Close.Select(c => c.PersonId).ToList() : Matches.Select(p => p.PersonId).ToList();

    private bool Adding => Typed.Length > 0 && !model.People.Any(p => p.Aliases.Prepend(p.Name).Any(n => string.Compare(n, Typed, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase) == 0));
    private bool AddHighlighted => Adding && Form.Highlight == Listed.Count;

    private void Draw()
    {
        var closest = Close;
        var matched = Matches;
        rows.Clear();
        soundsLike.Visibility = Typed.Length == 0 && closest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        close.Children.Clear();
        for (var i = 0; i < closest.Count; i++)
        {
            var id = closest[i].PersonId;
            close.Children.Add(rows[i] = PersonRow(model.PeopleById[id], closest[i].Closeness, Typed.Length == 0 && Form.Highlight == i, () => Choose(id)));
        }
        matches.Children.Clear();
        for (var i = 0; i < matched.Count; i++)
        {
            var id = matched[i].PersonId;
            matches.Children.Add(rows[i] = PersonRow(matched[i], null, Form.Highlight == i, () => Choose(id)));
        }
        found.Visibility = matched.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Someone new: a row among the matches when there are some, and then, chosen, their details with Add Person
        someoneNew.Child = Adding && matched.Count > 0
            ? Row(Ui.Row(8, Ui.Icon(Ui.AddGlyph, 14), new TextBlock { Text = $"Someone new: “{Typed}”" }), AddHighlighted, () =>
            {
                Form.Highlight = Listed.Count;
                Draw();
            }).With(b => Ui.Name(b, $"Someone new: {Typed}"))
            : null;
        var shows = Adding && (AddHighlighted || matched.Count == 0);
        making.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
        makingTitle.Text = $"New person: {Typed}";
        Dialog.PrimaryButtonText = shows ? "Add Person" : "";  // where a dialog's main button is; none until there's someone to add
        if (rows.GetValueOrDefault(Form.Highlight) is { } highlighted) highlighted.StartBringIntoView();  // the highlight stays in sight
        else if (AddHighlighted) someoneNew.StartBringIntoView();
    }

    /// <summary>A person to pick, the whole row clickable.</summary>
    private static Button PersonRow(Person person, string? note, bool highlighted, Action pick)
    {
        var content = Ui.Grid("*,Auto,Auto", spacing: 8).Add(
            Ui.Column(1, new TextBlock { Text = person.Name }, person.Detail is { } detail ? Ui.Caption(detail) : null),
            note is null ? null : Ui.Caption(note).With(c => c.VerticalAlignment = VerticalAlignment.Center).At(1),
            person.Confirmed ? Ui.Seal(12).At(2) : null);
        var row = Row(content, highlighted, pick);
        Ui.Name(row, string.Join(", ", new[] { person.Name, person.Detail, note, highlighted ? "highlighted" : null }.OfType<string>()));
        return row;
    }

    private static Button Row(UIElement content, bool highlighted, Action click)
    {
        var row = new Button
        {
            Content = content,
            Style = Ui.Style("SubtleButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
            IsTabStop = false,  // reached with ↑ ↓ from the name, as search results are
        };
        if (highlighted) row.Background = Ui.Tint(Ui.Accent, 0x33);
        row.Click += (_, _) => click();
        return row;
    }

    private void Choose(string person) => Send(new Dictionary<string, object> { ["person_id"] = person });

    private void Add()
    {
        if (!Adding) return;
        Send(new Dictionary<string, object> { ["name"] = Typed, ["role"] = Form.Role, ["company"] = Form.Company });
    }

    private void Send(Dictionary<string, object> body)
    {
        if (Form.Sent) return;  // Enter in a field and the main button can both answer one press
        Form.Sent = true;
        Dialog.Hide();
        _ = model.Assign(Form.Meeting, Form.Speaker, body);
    }
}
