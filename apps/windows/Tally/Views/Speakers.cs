using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Tally;

/// <summary>
/// Who was there, and who's still to confirm. Each settled speaker is a chip under the date, in the order they first
/// spoke, as Outlook lists attendees; a click shows them, with Listen and what can change. The ones still to confirm
/// are together in one orange box above the summary, a card each: confirming each once is what makes Tally know their
/// voice. A voice set aside as background is in neither, but named in a line of its own.
/// </summary>
internal static class Speakers
{
    /// <summary>Everything the chips, the cards and the line of background voices show: they're drawn again only when it changes.</summary>
    public static string Signature(AppModel model, string meeting, Results results, Names names) =>
        string.Join(";", results.SpeakerOrder.Select(s =>
        {
            var person = names.Person(s);
            var speaker = results.Speakers[s];
            return string.Join("|", s, names.Name(s), names.ReviewOf(s), names.Asks(s), person?.Role, person?.Company, person?.IsOwner, person?.Confirmed,
                model.IsPending(meeting, s), speaker.ClipUrls?.FirstOrDefault(), speaker.ClipUrls?.Count, speaker.TalkTime, speaker.Identity?.PersonId);
        })) + $"|{model.Recorder != null}|{model.Starting}";

    public static UIElement? Chips(AppModel model, string meeting, Results results, Names names)
    {
        var settled = names.Participants.Where(s => !names.Asks(s)).ToList();
        if (settled.Count == 0) return null;
        var flow = new Flow { Spacing = 6 };
        foreach (var speaker in settled) flow.Children.Add(Chip(model, meeting, speaker, results, names));
        return flow;
    }

    /// <summary>A speaker as a chip: their initials and name, a seal for a confirmed voice, You.</summary>
    private static Button Chip(AppModel model, string meeting, string speaker, Results results, Names names)
    {
        var person = names.Person(speaker);
        var review = names.ReviewOf(speaker);
        var known = review != Review.NotKnown;
        var name = names.Name(speaker);
        var chip = new Button
        {
            Content = Ui.Row(6, Ui.Initials(name, 22), Ui.Text(name, brush: known ? null : Ui.Secondary, wrap: false).With(t => t.VerticalAlignment = VerticalAlignment.Center),
                review == Review.Confirmed ? Ui.Seal(12) : null, person?.IsOwner == true ? Ui.YouBadge() : null),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(3, 3, 11, 3),
            Background = Ui.Theme("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
        };
        chip.Resources["ButtonBackgroundPointerOver"] = Ui.Theme("SubtleFillColorTertiaryBrush");
        chip.Resources["ButtonBackgroundPressed"] = Ui.Theme("SubtleFillColorTertiaryBrush");
        ToolTipService.SetToolTip(chip, person?.Detail ?? (known ? name : "Not someone you know"));
        Ui.Name(chip, name + (review == Review.Confirmed ? ", voice confirmed" : ""));
        var flyout = new Flyout();
        flyout.Opening += (_, _) =>
        {
            var actions = new StackPanel { Spacing = 2, Margin = new Thickness(-8, 0, -8, 0) };
            foreach (var (text, act, tip) in Actions(model, meeting, speaker, results, names))
            {
                var button = new Button { Content = text, Style = Ui.Style("SubtleButtonStyle"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) => { flyout.Hide(); act(); };
                if (tip is not null) ToolTipService.SetToolTip(button, tip);
                actions.Children.Add(button);
            }
            flyout.Content = Ui.Column(10,
                Ui.Row(10, Ui.Initials(name, 40), Ui.Column(2, Ui.Text(name, "BodyStrongTextBlockStyle"),
                    Ui.Caption(person?.Detail ?? (known ? "No role or company yet" : "Not someone you know"))).With(c => c.VerticalAlignment = VerticalAlignment.Center)),
                Heard(model, results.Speakers[speaker], name),
                Ui.Divider(),
                actions).With(c => c.Width = 280);
        };
        chip.Flyout = flyout;
        chip.ContextFlyout = Menu(model, meeting, speaker, results, names);
        return chip;
    }

    /// <summary>What can be said about a speaker, with a word on what it means where it needs one: from a chip's flyout,
    /// its context menu, and a card's ⋯.</summary>
    private static List<(string Text, Action Act, string? Tip)> Actions(AppModel model, string meeting, string speaker, Results results, Names names, bool card = false)
    {
        var review = names.ReviewOf(speaker);
        var person = names.Person(speaker);
        var actions = new List<(string, Action, string?)>();
        // Someone brief, not asked about, can still be named from a chip
        if (!card && review == Review.Suggested && results.Speakers[speaker].Identity?.PersonId is { } id)
            actions.Add(($"Yes, This Is {names.Name(speaker)}", () => _ = model.Assign(meeting, speaker, new Dictionary<string, object> { ["person_id"] = id }), null));
        actions.Add((review == Review.Unknown && !card ? "Who's This?" : "Change Who This Is…", () => model.Assigning = new AssignForm(meeting, speaker), null));
        if (person is not null) actions.Add(($"Edit {person.Name}…", () => model.Editing = new PersonForm(person), null));
        if (review != Review.NotKnown) actions.Add(("Not Someone I Know", () => _ = model.Assign(meeting, speaker, new Dictionary<string, object> { ["unknown"] = true }), null));
        actions.Add(("Mark as Background Voice", () => _ = model.Assign(meeting, speaker, new Dictionary<string, object> { ["background"] = true }),
            "Someone nearby, not in the meeting: their lines are set aside"));
        return actions;
    }

    private static MenuFlyout Menu(AppModel model, string meeting, string speaker, Results results, Names names, bool card = false)
    {
        var menu = new MenuFlyout();
        if (!card)
        {
            var clips = results.Speakers[speaker].ClipUrls ?? [];
            menu.Items.Add(Ui.Item("Listen", () => model.Listen(clips), clips.Count > 0 && model.Recorder == null, Ui.PlayGlyph));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var (text, act, tip) in Actions(model, meeting, speaker, results, names, card))
        {
            var item = Ui.Item(text, act);
            if (tip is not null) ToolTipService.SetToolTip(item, tip);
            menu.Items.Add(item);
        }
        return menu;
    }

    public static UIElement? ToConfirm(AppModel model, string meeting, Results results, Names names)
    {
        var open = results.SpeakerOrder.Where(names.Asks).ToList();
        if (open.Count == 0) return null;
        var heading = Ui.Grid("*,Auto", spacing: 12).Add(
            Ui.Row(8, Ui.Icon(Ui.PersonGlyph, 16, Ui.OrangeText), Ui.Text(AppModel.ToConfirmText(open.Count), "BodyStrongTextBlockStyle", Ui.OrangeText))
                .With(r => AutomationProperties.SetHeadingLevel(r, AutomationHeadingLevel.Level2)),
            // Said, not just greyed out: why Listen is off
            model.Recorder != null ? Ui.Row(6, Ui.Icon(Ui.VolumeOffGlyph, 12, Ui.Secondary), Ui.Caption("Listen is off while recording")).At(1) : null);
        var cards = new Columns();
        foreach (var speaker in open) cards.Children.Add(Card(model, meeting, speaker, results, names));
        return new Border
        {
            Child = Ui.Column(12, heading, cards),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = Ui.Tint(Ui.Orange, 0x1A),
            BorderBrush = Ui.Tint(Ui.Orange, 0x40),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>A speaker to confirm (the agent's guess, or nobody yet): name, what they do, Listen and how long they
    /// spoke, and the question.</summary>
    private static UIElement Card(AppModel model, string meeting, string speaker, Results results, Names names)
    {
        var person = names.Person(speaker);
        var saving = model.IsPending(meeting, speaker);
        var name = names.Name(speaker);
        var more = new Button { Content = Ui.Icon(Ui.MoreGlyph, 14), Style = Ui.Style("SubtleButtonStyle"), Padding = new Thickness(6), IsEnabled = !saving, VerticalAlignment = VerticalAlignment.Center };
        more.Flyout = Menu(model, meeting, speaker, results, names, card: true);
        Ui.Name(more, $"More for {name}");
        ToolTipService.SetToolTip(more, "More");
        var top = Ui.Grid("*,Auto,Auto", spacing: 6).Add(
            Ui.Row(6, Ui.Text(name, "BodyStrongTextBlockStyle", wrap: false).With(t => t.VerticalAlignment = VerticalAlignment.Center),
                person?.IsOwner == true ? Ui.YouBadge() : null),
            saving ? new ProgressRing { IsActive = true, Width = 16, Height = 16 }.With(r => Ui.Name(r, "Saving who this is")).At(1) : null,
            more.At(2));
        var card = Ui.Column(8,
            top,
            person?.Detail is { } detail ? Ui.Caption(detail).With(c => { c.TextWrapping = TextWrapping.NoWrap; c.TextTrimming = TextTrimming.CharacterEllipsis; c.Margin = new Thickness(0, -6, 0, 0); }) : null,
            Heard(model, results.Speakers[speaker], name),
            Question(model, meeting, speaker, results, names, saving));
        return Ui.Card(card);
    }

    /// <summary>"Is this Alex?" for the agent's guess, "Who's this?" for nobody yet: the only two a card is for.</summary>
    private static UIElement Question(AppModel model, string meeting, string speaker, Results results, Names names, bool saving)
    {
        var name = names.Name(speaker);
        if (names.ReviewOf(speaker) == Review.Unknown)
        {
            var who = Ui.Button("Who's This?", () => model.Assigning = new AssignForm(meeting, speaker));
            who.IsEnabled = !saving;
            Ui.Name(who, $"Who's {name}?");
            return who;
        }
        // A guess is never someone whose voice the owner confirmed (that makes it settled), so Yes asks for their details too
        var identity = results.Speakers[speaker].Identity;
        var person = names.Person(speaker);
        var yes = new Button
        {
            Content = "Yes…",
            Style = Ui.Style("AccentButtonStyle"),
            Flyout = ConfirmPerson(person?.Name ?? name, person?.Role ?? "", person?.Company ?? "",
                (n, role, company) => _ = model.Confirm(meeting, speaker, identity?.PersonId, n, role, company)),
        };
        Ui.Name(yes, $"Yes, this is {name}");
        var no = Ui.Button("No…", () => model.Assigning = new AssignForm(meeting, speaker, rejected: identity?.PersonId));
        Ui.Name(no, $"No, it isn't {name}");
        yes.IsEnabled = no.IsEnabled = !saving;
        UIElement question = Ui.Text($"Is this {name}?").With(t => t.VerticalAlignment = VerticalAlignment.Center);
        if (names.ByColleague(speaker)) question = Ui.Column(2, question, Ui.Caption("Named by a colleague"));
        return Ui.Grid("*,Auto", spacing: 8).Add(question, Ui.Row(6, yes, no).At(1));
    }

    /// <summary>Listen, and how long they spoke: on a chip's flyout and a card.</summary>
    private static UIElement Heard(AppModel model, Speaker speaker, string name) =>
        Ui.Row(10, Listen(model, speaker.ClipUrls ?? [], name), Ui.Caption($"Spoke for {Format.Duration(speaker.TalkTime)}").With(c => c.VerticalAlignment = VerticalAlignment.Center));

    /// <summary>Yes, to someone new: who they are in full, filled in with what the agent heard, the caret at the name's
    /// end (to add a surname), and Enter confirms. Nothing changed is a plain yes.</summary>
    private static Flyout ConfirmPerson(string name, string role, string company, Action<string, string, string> confirm)
    {
        var flyout = new Flyout();
        var heading = Ui.Text($"Confirm {name}", "BodyStrongTextBlockStyle");
        var nameBox = new TextBox { Header = "Name", Text = name };
        var roleBox = new TextBox { Header = "Role", Text = role, PlaceholderText = "e.g. Head of finance" };
        var companyBox = new TextBox { Header = "Company", Text = company, PlaceholderText = "e.g. Acme" };
        var go = Ui.Button("Confirm", Go, accent: true);
        go.HorizontalAlignment = HorizontalAlignment.Right;
        void Go()
        {
            if (nameBox.Text.Trim().Length == 0) return;
            flyout.Hide();
            confirm(nameBox.Text, roleBox.Text, companyBox.Text);
        }
        nameBox.TextChanged += (_, _) =>
        {
            heading.Text = nameBox.Text.Trim().Length == 0 ? "Confirm who this is" : $"Confirm {nameBox.Text.Trim()}";
            go.IsEnabled = nameBox.Text.Trim().Length > 0;
        };
        foreach (var box in new[] { nameBox, roleBox, companyBox })
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Go(); } };
        flyout.Opening += (_, _) =>  // as it was offered, each time: what was typed and dismissed isn't kept
        {
            (nameBox.Text, roleBox.Text, companyBox.Text) = (name, role, company);
        };
        flyout.Opened += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            nameBox.Select(nameBox.Text.Length, 0);  // the caret at the end, not all of it selected to be typed over
        };
        flyout.Content = Ui.Column(12, heading, Ui.Caption("Tally remembers their voice. Add what you know of them."), nameBox, roleBox, companyBox, go).With(c => c.Width = 300);
        return flyout;
    }

    /// <summary>Hear a speaker: their clips (a few seconds each from their clearest turns) one after the other; a second
    /// click pauses. Off while recording: Tally's own sound would go into the recording.</summary>
    public static FrameworkElement Listen(AppModel model, IReadOnlyList<Uri> clips, string name = "this speaker")
    {
        if (clips.Count == 0) return Ui.Caption("No clip: too little clear speech", Ui.Theme("TextFillColorTertiaryBrush")).With(c => c.VerticalAlignment = VerticalAlignment.Center);
        var icon = Ui.Icon(Ui.PlayGlyph, 12);
        var label = new TextBlock { Text = "Listen" };
        var button = new Button { Content = Ui.Row(6, icon, label), Padding = new Thickness(10, 4, 11, 5) };
        void Show()
        {
            var playing = model.Player.IsPlaying(clips);
            icon.Glyph = playing ? Ui.PauseGlyph : Ui.PlayGlyph;
            label.Text = playing ? "Pause" : "Listen";
            button.IsEnabled = model.Recorder == null && !model.Starting;
            ToolTipService.SetToolTip(button, model.Recorder != null ? "Listening is off while recording" : clips.Count == 1 ? $"Hear a short clip of {name}" : $"Hear {clips.Count} short clips of {name}");
            Ui.Name(button, playing ? $"Pause {name}" : $"Listen to {name}");
        }
        button.Click += (_, _) =>
        {
            model.Listen(clips);
            Show();
        };
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppModel.Recorder) or nameof(AppModel.Starting)) Show();
        }
        button.Loaded += (_, _) =>
        {
            model.Player.Changed += Show;
            model.PropertyChanged += Changed;
            Show();
        };
        button.Unloaded += (_, _) =>
        {
            model.Player.Changed -= Show;
            model.PropertyChanged -= Changed;
        };
        Show();
        return button;
    }

    /// <summary>The voices set aside as background, each with Listen and the way back, and the summary written again
    /// without them: once the owner has marked whoever they will, one press. Their own edited copy would
    /// be replaced, so that asks first.</summary>
    public static UIElement? Background(AppModel model, string meeting, Results results, Names names)
    {
        var background = results.SpeakerOrder.Where(s => names.ReviewOf(s) == Review.Background).ToList();
        var row = model.Meetings.FirstOrDefault(m => m.MeetingId == meeting);
        // Written without other voices than are set aside now: some marked since, or one taken back
        var stale = !background.ToHashSet().SetEquals(results.SummaryWithout ?? []);
        if (row is null || !(stale || row.Rewriting != null || background.Count > 0)) return null;
        var stack = new StackPanel { Spacing = 6 };
        if (background.Count > 0)
        {
            var flow = new Flow { Spacing = 4 };
            flow.Children.Add(Ui.Text("Set aside:", brush: Ui.Secondary).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 4, 0); }));
            foreach (var speaker in background)
            {
                var clips = results.Speakers[speaker].ClipUrls ?? [];
                var menu = new MenuFlyout();
                menu.Items.Add(Ui.Item("Listen", () => model.Listen(clips), clips.Count > 0 && model.Recorder == null, Ui.PlayGlyph));
                menu.Items.Add(Ui.Item("Not a Background Voice…", () => model.Assigning = new AssignForm(meeting, speaker)));
                var name = new DropDownButton { Content = names.Name(speaker), Flyout = menu, Padding = new Thickness(8, 3, 8, 4) };
                Ui.Name(name, $"{names.Name(speaker)}, a background voice");
                flow.Children.Add(name);
            }
            stack.Children.Add(flow);
        }
        if (row.IsRewriting)
        {
            stack.Children.Add(Ui.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                Ui.Text("Rewriting the summary… It can't be edited until it's done.", brush: Ui.Secondary)));
        }
        else if (stale)
        {
            var rewrite = Ui.Link(background.Count == 0 ? "Rewrite the Summary With Everyone" : "Rewrite the Summary Without Them", async () =>
            {
                if (!model.IsEdited(meeting)) await model.RewriteSummary(meeting, replace: false);
                else if (await Dialogs.Confirm("Rewrite the summary?", "Tally writes it again for the voices that were in the meeting. Your edits to this summary are replaced.", "Rewrite"))
                    await model.RewriteSummary(meeting, replace: true);
            });
            rewrite.Margin = new Thickness(-4, 0, 0, 0);
            stack.Children.Add(Ui.Row(8, row.RewriteError is { } error ? Ui.Text(error, brush: Ui.OrangeText).With(t => t.VerticalAlignment = VerticalAlignment.Center) : null, rewrite));
        }
        return stack;
    }
}
