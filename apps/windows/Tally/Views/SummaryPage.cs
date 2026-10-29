using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Tally;

/// <summary>
/// The summary, edited where it stands: click any text and type, as in OneNote. The first change makes
/// the owner's own copy, saved a second after the last keystroke; the agent's is kept, and Restore Original brings it
/// back. Points are added under each list (or with Enter at the end of one) and deleted with their button or
/// Backspace in an empty one; an action item has a box to tick, an owner and a due date.
///
/// It's drawn again only when its shape changes (a point added or deleted, a name changed): otherwise each text is
/// brought up to date where it is, so the one being typed in keeps its caret.
/// </summary>
internal sealed class SummaryPage : UserControl
{
    private readonly AppModel model;
    private readonly string meeting;
    private readonly IReadOnlyList<string> speakers;
    private readonly StackPanel stack = new() { Spacing = 26 };
    private readonly List<Editable> editors = [];
    private readonly List<ActionItemRow> actions = [];
    private readonly Dictionary<string, Control> focusable = [];
    private Names names;
    private string shape = "";
    private string? focusNext;  // a point just added: ready to type in once it's drawn

    public SummaryPage(AppModel model, string meeting, Names names, IReadOnlyList<string> speakers)
    {
        this.model = model;
        this.meeting = meeting;
        this.names = names;
        this.speakers = speakers;
        Content = stack;
        IsTabStop = false;
        Draw();
    }

    private Summary? Shown => model.SummaryOf(meeting);

    /// <summary>After a change to the summary or to who's who. <paramref name="force"/>: the one being typed in too, after
    /// an undo.</summary>
    public void Refresh(Names names, bool force = false)
    {
        this.names = names;
        if (Shown is not { } summary) return;
        // Being rewritten: what's typed now would be lost to the new one, so it waits
        var rewriting = model.Meetings.FirstOrDefault(m => m.MeetingId == meeting)?.IsRewriting == true;
        IsEnabled = !rewriting;
        Opacity = rewriting ? 0.6 : 1;
        if (Shape(summary) != shape)
        {
            Draw();
            return;
        }
        foreach (var editor in editors) editor.Refresh(summary, names, force);
        foreach (var row in actions) row.Refresh(summary, names);
    }

    /// <summary>What can't change in place: how many of everything there are, and the names shown beside them.</summary>
    private string Shape(Summary s) => string.Join("|",
        s.Points.Count, s.Decisions.Count, s.ActionItems.Count, s.OpenQuestions.Count, s.Attendees.Count,
        string.Join(",", s.Topics.Select(t => $"{t.Notes.Count}{(t.Outcome is null ? "" : "+")}")),
        string.Join(",", s.Decisions.Select(d => string.Join(" ", d.By.Select(names.Name)))),
        string.Join(",", s.OpenQuestions.Select(q => string.Join(" ", q.RaisedBy.Select(names.Name)))),
        string.Join(",", s.Attendees.Select(a => names.Name(a.Speaker) + " " + names.Role(a))),
        string.Join(",", speakers.Select(names.Name)));

    private void Draw()
    {
        if (Shown is not { } s) return;
        // What has focus now, to have it again: the same field (and caret), or the same button
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        var again = focusNext ?? (focused?.Tag as string is { } key && focusable.ContainsKey(key) ? key : null);
        var caret = focused is TextBox box && focusNext is null ? box.SelectionStart : int.MaxValue;
        focusNext = null;
        stack.Children.Clear();
        editors.Clear();
        actions.Clear();
        focusable.Clear();
        shape = Shape(s);

        stack.Children.Add(Edit("overview", "Overview", x => x.Overview, (x, t) => x with { Overview = t }, size: 18));
        List("Summary", "points", s.Points.Count, i => Edit($"points/{i}", "A point", x => At(x.Points, i) ?? "", (x, t) => x with { Points = Set(x.Points, i, t) }),
            x => x with { Points = [.. x.Points, ""] }, (x, i) => x with { Points = Without(x.Points, i) }, (x, i) => x with { Points = Insert(x.Points, i, "") });
        List("Decisions", "decisions", s.Decisions.Count, i => Ui.Column(2,
                Edit($"decisions/{i}", "A decision", x => At(x.Decisions, i)?.Text ?? "", (x, t) => x with { Decisions = Set(x.Decisions, i, d => d with { Text = t }) }),
                People(s.Decisions[i].By)),
            x => x with { Decisions = [.. x.Decisions, new("", [])] }, (x, i) => x with { Decisions = Without(x.Decisions, i) },
            (x, i) => x with { Decisions = Insert(x.Decisions, i, new("", [])) });
        List("Action items", "actions", s.ActionItems.Count, ActionRow,
            x => x with { ActionItems = [.. x.ActionItems, new(null, "")] }, (x, i) => x with { ActionItems = Without(x.ActionItems, i) },
            (x, i) => x with { ActionItems = Insert(x.ActionItems, i, new(null, "")) }, bulleted: false);
        List("Open questions", "questions", s.OpenQuestions.Count, i => Ui.Column(2,
                Edit($"questions/{i}", "A question", x => At(x.OpenQuestions, i)?.Text ?? "", (x, t) => x with { OpenQuestions = Set(x.OpenQuestions, i, q => q with { Text = t }) }),
                People(s.OpenQuestions[i].RaisedBy)),
            x => x with { OpenQuestions = [.. x.OpenQuestions, new("", [])] }, (x, i) => x with { OpenQuestions = Without(x.OpenQuestions, i) },
            (x, i) => x with { OpenQuestions = Insert(x.OpenQuestions, i, new("", [])) });
        if (s.Topics.Count > 0) stack.Children.Add(Topics(s));
        if (s.Attendees.Count > 0) stack.Children.Add(WhoSaidWhat(s));
        if (AddMenu(s) is { } add) stack.Children.Add(add);

        if (again is not null && focusable.GetValueOrDefault(again) is { } target)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                target.Focus(FocusState.Programmatic);
                if (target is TextBox field) field.Select(Math.Min(caret, field.Text.Length), 0);
            });
        }
    }

    private void Change(Func<Summary, Summary> change) => model.Edit(meeting, change);

    private TextBox Edit(string key, string placeholder, Func<Summary, string> read, Func<Summary, string, Summary> write, double size = 14,
        bool strong = false)
    {
        var editor = new Editable(model, meeting, names, key, placeholder, read, write, keepsSpeakers: true, size, strong);
        if (Shown is { } s) editor.Refresh(s, names, force: true);
        editors.Add(editor);
        focusable[key] = editor.Box;
        return editor.Box;
    }

    /// <summary>A list under its heading, with Add at its end; Enter at the end of a point starts the next, and
    /// Backspace in an empty one deletes it. An empty list shows nothing: it's added from the menu at the page's end.</summary>
    private void List(string? title, string prefix, int count, Func<int, UIElement> row, Func<Summary, Summary> add, Func<Summary, int, Summary> delete,
        Func<Summary, int, Summary> insert, bool bulleted = true, StackPanel? into = null)
    {
        if (count == 0) return;
        var list = new StackPanel { Spacing = 8 };
        if (title is not null) list.Children.Add(Ui.Heading(title));
        for (var i = 0; i < count; i++)
        {
            var at = i;
            var content = row(at);
            // Enter and Backspace work on the row's own field, the first in it
            if (focusable.GetValueOrDefault($"{prefix}/{at}") is TextBox field && editors.FirstOrDefault(e => e.Box == field) is { } editor)
            {
                editor.Enter = () => { focusNext = $"{prefix}/{at + 1}"; Change(s => insert(s, at + 1)); };
                editor.RemoveEmpty = () => { focusNext = at > 0 ? $"{prefix}/{at - 1}" : null; Change(s => delete(s, at)); };
            }
            list.Children.Add(Row(content, bulleted, $"{prefix}/{at}", () => Change(s => delete(s, at))));
        }
        var addButton = Ui.Subtle("Add", Ui.AddGlyph, () => { focusNext = $"{prefix}/{count}"; Change(add); }, $"Add to {title ?? "the topic"}");
        addButton.Tag = $"{prefix}/add";
        addButton.Margin = new Thickness(bulleted ? 10 : 22, -2, 0, 0);
        focusable[$"{prefix}/add"] = addButton;
        list.Children.Add(addButton);
        (into ?? stack).Children.Add(list);
    }

    /// <summary>A point, with a bullet, and its Delete button under the pointer or with focus.</summary>
    private Grid Row(UIElement content, bool bulleted, string key, Action delete)
    {
        var remove = new Button { Content = Ui.Icon(Ui.CloseGlyph, 10), Style = Ui.Style("SubtleButtonStyle"), Padding = new Thickness(6), Opacity = 0, VerticalAlignment = VerticalAlignment.Top, Tag = key + "/delete" };
        remove.Click += (_, _) => delete();
        ToolTipService.SetToolTip(remove, "Delete");
        Ui.Name(remove, "Delete this point");
        focusable[key + "/delete"] = remove;
        var grid = Ui.Grid(bulleted ? "Auto,*,Auto" : "*,Auto", spacing: 8);
        if (bulleted) grid.Children.Add(Ui.Text("•", brush: Ui.Secondary));
        grid.Add(((FrameworkElement)content).At(bulleted ? 1 : 0), remove.At(bulleted ? 2 : 1));
        grid.Background = new SolidColorBrush(Colors.Transparent);  // so the pointer over its gaps counts as over it
        void Reveal(bool on) => remove.Opacity = on || remove.FocusState != FocusState.Unfocused ? 1 : 0;
        grid.PointerEntered += (_, _) => Reveal(true);
        grid.PointerExited += (_, _) => Reveal(false);
        remove.GotFocus += (_, _) => Reveal(true);
        remove.LostFocus += (_, _) => Reveal(false);
        return grid;
    }

    /// <summary>Who decided or asked, under it.</summary>
    private TextBlock? People(IReadOnlyList<string> who) => who.Count == 0 ? null : Ui.Caption(And(who.Select(names.Name).ToList()));

    private UIElement ActionRow(int i)
    {
        var row = new ActionItemRow(this, i);
        actions.Add(row);
        return row.View;
    }

    private UIElement Topics(Summary s)
    {
        var topics = new StackPanel { Spacing = 18 };
        topics.Children.Add(Ui.Heading("Topics"));
        for (var t = 0; t < s.Topics.Count; t++)
        {
            var at = t;
            var topic = new StackPanel { Spacing = 6 };
            var title = Edit($"topics/{at}", "Topic", x => At(x.Topics, at)?.Title ?? "", (x, text) => x with { Topics = Set(x.Topics, at, y => y with { Title = text }) }, strong: true);
            topic.Children.Add(Ui.Grid("*,Auto", spacing: 12).Add(title,
                Ui.Caption($"{Format.Clock(s.Topics[at].Start)}–{Format.Clock(s.Topics[at].End)}").With(c => c.VerticalAlignment = VerticalAlignment.Center).At(1)));
            List(null, $"topics/{at}/notes", s.Topics[at].Notes.Count,
                n => Edit($"topics/{at}/notes/{n}", "A note", x => At(At(x.Topics, at)?.Notes ?? [], n) ?? "",
                    (x, text) => x with { Topics = Set(x.Topics, at, y => y with { Notes = Set(y.Notes, n, text) }) }),
                x => x with { Topics = Set(x.Topics, at, y => y with { Notes = [.. y.Notes, ""] }) },
                (x, n) => x with { Topics = Set(x.Topics, at, y => y with { Notes = Without(y.Notes, n) }) },
                (x, n) => x with { Topics = Set(x.Topics, at, y => y with { Notes = Insert(y.Notes, n, "") }) }, into: topic);
            if (s.Topics[at].Outcome is not null)  // its label above, so a long outcome wraps the full width
                topic.Children.Add(Ui.Column(2,
                    Ui.Caption("Outcome").With(c => c.FontWeight = FontWeights.SemiBold),
                    Edit($"topics/{at}/outcome", "Outcome", x => At(x.Topics, at)?.Outcome ?? "", (x, text) => x with { Topics = Set(x.Topics, at, y => y with { Outcome = text }) })));
            topics.Children.Add(topic);
        }
        return topics;
    }

    private UIElement WhoSaidWhat(Summary s)
    {
        var list = new StackPanel { Spacing = 10 };
        list.Children.Add(Ui.Heading("Who said what"));
        for (var i = 0; i < s.Attendees.Count; i++)
        {
            var at = i;
            var who = new TextBlock { TextWrapping = TextWrapping.Wrap };
            who.Inlines.Add(new Run { Text = names.Name(s.Attendees[at].Speaker), FontWeight = FontWeights.SemiBold });
            who.Inlines.Add(new Run { Text = "  " + names.Role(s.Attendees[at]), Foreground = Ui.Secondary });
            list.Children.Add(Ui.Grid("Auto,*", spacing: 8).Add(Ui.Text("•", brush: Ui.Secondary),
                Ui.Column(2, who, Edit($"attendees/{at}", "Their part", x => At(x.Attendees, at)?.Contribution ?? "",
                    (x, text) => x with { Attendees = Set(x.Attendees, at, a => a with { Contribution = text }) })).At(1)));
        }
        return list;
    }

    /// <summary>For the lists that are empty: a point, a decision, an action item or an open question where the agent
    /// wrote none.</summary>
    private UIElement? AddMenu(Summary s)
    {
        var missing = new (string Name, string Key, bool Empty, Func<Summary, Summary> Add)[]
        {
            ("Summary Point", "points/0", s.Points.Count == 0, x => x with { Points = [""] }),
            ("Decision", "decisions/0", s.Decisions.Count == 0, x => x with { Decisions = [new("", [])] }),
            ("Action Item", "actions/0", s.ActionItems.Count == 0, x => x with { ActionItems = [new(null, "")] }),
            ("Open Question", "questions/0", s.OpenQuestions.Count == 0, x => x with { OpenQuestions = [new("", [])] }),
        }.Where(m => m.Empty).ToList();
        if (missing.Count == 0) return null;
        var menu = new MenuFlyout();
        foreach (var (name, key, _, add) in missing) menu.Items.Add(Ui.Item(name, () => { focusNext = key; Change(add); }));
        var button = Ui.Subtle("Add", Ui.AddGlyph, () => { }, "Add a section");
        button.Flyout = menu;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(-6, -10, 0, 0);
        return button;
    }

    /// <summary>"Anna, Erik and Sara".</summary>
    public static string And(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static T? At<T>(IReadOnlyList<T> list, int i) => i < list.Count ? list[i] : default;
    private static IReadOnlyList<T> Set<T>(IReadOnlyList<T> list, int i, T value) => i < list.Count ? [.. list.Take(i), value, .. list.Skip(i + 1)] : list;
    private static IReadOnlyList<T> Set<T>(IReadOnlyList<T> list, int i, Func<T, T> change) => i < list.Count ? Set(list, i, change(list[i])) : list;
    private static IReadOnlyList<T> Without<T>(IReadOnlyList<T> list, int i) => i < list.Count ? [.. list.Take(i), .. list.Skip(i + 1)] : list;
    private static IReadOnlyList<T> Insert<T>(IReadOnlyList<T> list, int i, T value) => [.. list.Take(i), value, .. list.Skip(i)];

    /// <summary>An action item: tick it done, edit what it says, and under it who owns it and when it's due, as To Do
    /// shows them. One not set is offered under the pointer, or with focus in the row.</summary>
    private sealed class ActionItemRow
    {
        private readonly SummaryPage page;
        private readonly int at;
        private readonly CheckBox done;
        private readonly TextBox text;
        private readonly Button owner, due;
        private readonly TextBlock ownerText = new(), dueText = new();
        private bool hovering, open;

        public Grid View { get; }

        public ActionItemRow(SummaryPage page, int at)
        {
            this.page = page;
            this.at = at;
            var key = $"actions/{at}";
            done = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, 0, 0), Tag = key + "/done" };
            done.Click += (_, _) => Change(a => a with { Done = done.IsChecked == true });
            Ui.Name(done, "Done");
            page.focusable[key + "/done"] = done;
            text = page.Edit(key, "What's to be done", x => At(x.ActionItems, at)?.Text ?? "", (x, t) => x with { ActionItems = Set(x.ActionItems, at, a => a with { Text = t }) });

            owner = Ui.Subtle("", Ui.PersonGlyph, () => { }, label: ownerText);
            owner.Tag = key + "/owner";
            ToolTipService.SetToolTip(owner, "Who owns it");
            var owners = Ui.Menu(items =>
            {
                items.Add(Ui.Item("No Owner", () => Change(a => a with { Owner = null })));
                items.Add(new MenuFlyoutSeparator());
                foreach (var speaker in page.speakers) items.Add(Ui.Item(page.names.Name(speaker), () => Change(a => a with { Owner = speaker })));
            });
            Track(owners);
            owner.Flyout = owners;
            page.focusable[key + "/owner"] = owner;

            due = Ui.Subtle("", Ui.CalendarGlyph, () => { }, label: dueText);
            due.Tag = key + "/due";
            var calendar = new Flyout();
            calendar.Opening += (_, _) => calendar.Content = Calendar(calendar);
            Track(calendar);
            due.Flyout = calendar;
            page.focusable[key + "/due"] = due;

            var details = Ui.Row(4, owner, due).With(r => r.Margin = new Thickness(-8, 0, 0, 0));
            View = Ui.Grid("Auto,*", spacing: 4).Add(done, Ui.Column(0, text, details).At(1));
            View.Background = new SolidColorBrush(Colors.Transparent);
            View.PointerEntered += (_, _) => { hovering = true; Show(); };
            View.PointerExited += (_, _) => { hovering = false; Show(); };
            View.GotFocus += (_, _) => Show();
            View.LostFocus += (_, _) => Show();
            if (page.Shown is { } s) Refresh(s, page.names);
        }

        private ActionItem? Item => page.Shown is { } s ? At(s.ActionItems, at) : null;

        private void Change(Func<ActionItem, ActionItem> change) =>
            page.Change(x => x with { ActionItems = Set(x.ActionItems, at, change) });

        private void Track(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
        {
            flyout.Opening += (_, _) => { open = true; Show(); };  // before it takes the pointer and focus, which would fold its button away
            flyout.Closed += (_, _) => { open = false; Show(); };
        }

        public void Refresh(Summary s, Names names)
        {
            if (At(s.ActionItems, at) is not { } item) return;
            done.IsChecked = item.Done == true;
            text.Foreground = item.Done == true ? Ui.Secondary : Ui.Theme("TextControlForeground");
            ownerText.Text = item.Owner is { } who ? names.Name(who) : "Add owner";
            var date = Date(item.Due);
            dueText.Text = date is { } d ? "Due " + d.ToString("d MMM", CultureInfo.CurrentCulture) : "Add due date";
            // A done one is never late
            due.Foreground = item.Done != true && date < DateOnly.FromDateTime(DateTime.Today) ? new SolidColorBrush(Ui.Red) : Ui.Secondary;
            ToolTipService.SetToolTip(due, date is null ? "Give it a due date" : "Change the due date");
            Ui.Name(owner, item.Owner is { } o ? $"Owner: {names.Name(o)}" : "Add owner");
            Ui.Name(due, date is { } dd ? $"Due {dd:D}" : "Add due date");
            Show();
        }

        private void Show()
        {
            var within = View.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject f && Ui.Within(f, View);
            var offered = hovering || open || within;
            owner.Visibility = Item?.Owner != null || offered ? Visibility.Visible : Visibility.Collapsed;
            due.Visibility = Item?.Due != null || offered ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>One click opens a calendar, as To Do's due date does; Remove Due Date is under it.</summary>
        private UIElement Calendar(Flyout flyout)
        {
            var date = Date(Item?.Due);
            var view = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, IsTodayHighlighted = true };
            if (date is { } d)
            {
                var day = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue));
                view.SelectedDates.Add(day);
                view.SetDisplayDate(day);
            }
            view.SelectedDatesChanged += (_, e) =>
            {
                if (e.AddedDates.Count == 0) return;
                var picked = DateOnly.FromDateTime(e.AddedDates[0].Date);
                if (picked == date) return;  // the day it had, selected as the calendar opens
                Change(a => a with { Due = picked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
                flyout.Hide();
            };
            var stack = Ui.Column(8, view);
            if (date is not null)
            {
                var remove = Ui.Button("Remove Due Date", () => { Change(a => a with { Due = null }); flyout.Hide(); });
                remove.HorizontalAlignment = HorizontalAlignment.Stretch;
                stack.Children.Add(remove);
            }
            return stack;
        }

        // this century, as the pipeline holds them: a calendar east of UTC can't show the year 1
        private static DateOnly? Date(string? text) =>
            DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 2000 and <= 2100 ? d : null;
    }
}

/// <summary>
/// A text of the summary that edits where it stands: it reads as text (a faint fill under the pointer says it can be
/// edited), and is a field as soon as it's clicked. What's shown has speakers' names; what's kept has their tokens, so
/// renaming someone in People still reaches it (but in the title, which the list shows as typed).
/// </summary>
internal sealed class Editable
{
    private readonly Func<Summary, string> read;
    private string shown = "";  // what it was last given: the field echoing it back isn't an edit
    private Names names;

    public TextBox Box { get; }
    public Action? Enter { get; set; }
    public Action? RemoveEmpty { get; set; }

    public Editable(AppModel model, string meeting, Names names, string key, string placeholder, Func<Summary, string> read, Func<Summary, string, Summary> write,
        bool keepsSpeakers, double size, bool strong)
    {
        this.read = read;
        this.names = names;
        Box = new TextBox
        {
            PlaceholderText = placeholder,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            FontSize = size,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Tag = key,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(6, 3, 6, 4),
            Margin = new Thickness(-7, -4, -7, -5),
        };
        Plain(Box);
        Ui.Name(Box, placeholder);
        // A wrapping field made wider keeps the height it had when narrower: its text set again, it's laid out anew
        // (the change events that follow find what it was given, so they aren't edits)
        var width = 0.0;
        Box.SizeChanged += (_, e) =>
        {
            if (width > 0 && e.NewSize.Width > width + 1 && Box.FocusState == FocusState.Unfocused)
            {
                var text = Box.Text;
                Box.Text = "";
                Box.Text = text;
            }
            width = e.NewSize.Width;
        };
        Box.TextChanged += (_, _) =>
        {
            if (Box.Text == shown) return;
            shown = Box.Text;
            var text = keepsSpeakers ? this.names.Tokenize(Box.Text) : Box.Text;
            model.Edit(meeting, s => write(s, text));
        };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && Enter is { } enter) enter();
            else if (e.Key == VirtualKey.Enter && Box.XamlRoot is { } root)  // done: on to the next, as Tab
                FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = root.Content });
            else if (e.Key == VirtualKey.Back && Box.Text.Length == 0 && RemoveEmpty is { } remove) remove();
            else return;
            e.Handled = true;
        };
    }

    public void Refresh(Summary summary, Names names, bool force)
    {
        this.names = names;
        var text = names.Render(read(summary));
        if (Box.Text == text || !force && Box.FocusState != FocusState.Unfocused) return;
        var caret = Box.SelectionStart;
        shown = text;
        Box.Text = text;
        Box.Select(Math.Min(caret, text.Length), 0);
    }

    /// <summary>A field that looks like the text it holds until it's in use.</summary>
    public static void Plain(TextBox box)
    {
        var clear = new SolidColorBrush(Colors.Transparent);
        box.Background = clear;
        box.BorderBrush = clear;
        box.Resources["TextControlBackgroundPointerOver"] = Ui.Theme("SubtleFillColorSecondaryBrush");
        box.Resources["TextControlBorderBrushPointerOver"] = clear;
        box.Resources["TextControlBackgroundDisabled"] = clear;
        box.Resources["TextControlBorderBrushDisabled"] = clear;
        box.Resources["TextControlForegroundDisabled"] = Ui.Theme("TextFillColorPrimaryBrush");
    }
}
