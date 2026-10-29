using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Tally;

/// <summary>
/// One meeting. Until it's ready: where it is and what happens next. Once ready: its title across the page, when it
/// was, who was there (with anyone to confirm first), then the summary, the transcript, or the files kept with it.
/// Over it, as Outlook keeps a message's commands over it: its views (Ctrl+1, 2, 3), Copy (Ctrl+Shift+C) and Delete.
///
/// Each part is drawn again only when what it shows changes; the whole page only when the meeting moves on (sent,
/// ready, opened).
/// </summary>
internal sealed class MeetingView : Grid
{
    public enum Tab { Summary, Transcript, Files }

    private readonly AppModel model;
    private readonly Window window;
    private readonly Grid bar = Ui.Grid("*,Auto");
    private readonly Border tabsSlot = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border page = new();
    private readonly AppBarButton copy, delete;
    private string phase = "";
    private Tab tab = Tab.Summary;
    private SelectorBar? tabs;
    private SelectorBarItem? filesTab;
    private TextBlock? elapsed, hint;
    private Editable? title;
    private TextBlock? when;
    private Section chips = new(), foldersLine = new(), filing = new(), background = new(), confirm = new(), transcript = new();
    private readonly Border body = new();
    private SummaryPage? summary;
    private FilesView? files;
    private bool showingBackground;  // the background voices' lines, folded away until asked for
    private int copies;

    public string Id { get; }

    public MeetingView(AppModel model, Window window, string id)
    {
        this.model = model;
        this.window = window;
        Id = id;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        copy = new AppBarButton { Label = "Copy Summary", Icon = new FontIcon { Glyph = Ui.CopyGlyph } };
        copy.Click += (_, _) => Copy();
        delete = new AppBarButton { Label = "Delete", Icon = new FontIcon { Glyph = Ui.DeleteGlyph } };
        delete.Click += (_, _) => { if (Item is { CanDelete: true } item) model.Deleting = item; };
        ToolTipService.SetToolTip(delete, "Delete this meeting (Delete)");
        var commands = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
            IsDynamicOverflowEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        commands.PrimaryCommands.Add(copy);
        commands.PrimaryCommands.Add(delete);
        // Narrow, the views keep their names and the commands their icons, as Outlook's reading pane does
        SizeChanged += (_, e) => commands.DefaultLabelPosition = e.NewSize.Width < 600 ? CommandBarDefaultLabelPosition.Collapsed : CommandBarDefaultLabelPosition.Right;
        bar.Add(tabsSlot, commands.At(1));
        bar.Padding = new Thickness(20, 4, 8, 4);
        bar.MinHeight = 52;
        Children.Add(bar);
        Children.Add(page.At(0, 1));
        Draw();
    }

    private Item? Item => model.Items.FirstOrDefault(i => i.Id == Id);
    private Names Names(Results results) => new(results, model.PeopleById);
    public bool ShowsSummary => phase == "ready" && tab == Tab.Summary;
    public bool ShowsFiles => phase == "ready" && tab == Tab.Files;

    /// <summary>What decides which page this is: the whole page is drawn again when it changes.</summary>
    private string Phase()
    {
        if (Item is not { } item) return "gone";
        if (item.Status.Kind == StatusKind.Ready)
            return model.Results.ContainsKey(Id) ? "ready" : model.LoadErrors.TryGetValue(Id, out var error) ? "error:" + error : "opening";
        return string.Join("|", item.Status.Kind, item.Status.Error, item.HasAudio, item.InCloud, item.SendError, item.CanDelete,
            model.Auth.State == AuthState.SignedIn, model.StoppedByCall != null);
    }

    public void Changed(string? name)
    {
        if (Phase() != phase)
        {
            Draw();
            return;
        }
        if (Item is { } item) delete.IsEnabled = item.CanDelete;
        if (name == nameof(AppModel.Elapsed))
        {
            if (elapsed is not null) elapsed.Text = Elapsed;
            if (hint is not null) hint.Text = Hint;
            return;
        }
        if (phase != "ready" || model.Results.GetValueOrDefault(Id) is not { } results || Item is not { } shown) return;
        var names = Names(results);
        title?.Refresh(model.SummaryOf(Id) ?? results.Summary, names, force: false);
        if (when is not null) when.Text = When(shown, results);
        summary?.Refresh(names);
        if (tab == Tab.Transcript) ShowTranscript(results, names);
        if (name is nameof(AppModel.Files) or nameof(AppModel.AddingFiles) or nameof(AppModel.Opening) or nameof(AppModel.Offline) or "Auth") files?.Changed();
        FilesLabel();
        Sections();
    }

    private void Draw()
    {
        phase = Phase();
        tabs = null;
        filesTab = null;
        elapsed = hint = null;
        title = null;
        when = null;
        summary = null;
        files = null;
        chips = new();
        foldersLine = new();
        filing = new();
        background = new();
        confirm = new();
        tabsSlot.Child = null;
        copy.Visibility = phase == "ready" ? Visibility.Visible : Visibility.Collapsed;
        if (Item is not { } item)
        {
            page.Child = null;
            delete.IsEnabled = false;
            return;
        }
        delete.IsEnabled = item.CanDelete;
        if (phase == "ready") page.Child = Ready(item);
        else if (phase == "opening")
        {
            page.Child = Ui.Column(12, new ProgressRing { IsActive = true }, Ui.Text("Opening…", brush: Ui.Secondary))
                .With(s => { s.HorizontalAlignment = HorizontalAlignment.Center; s.VerticalAlignment = VerticalAlignment.Center; });
            _ = model.Load(Id);
        }
        else if (phase.StartsWith("error:", StringComparison.Ordinal))
            page.Child = Ui.Placeholder(Ui.WarningGlyph, "This Summary Couldn't Be Opened", phase["error:".Length..], Ui.Button("Try Again", () => _ = model.Reload(Id), accent: true));
        else page.Child = Waiting(item);
    }

    /// <summary>A meeting on its way, said plainly, with what can be done.</summary>
    private UIElement Waiting(Item item)
    {
        switch (item.Status.Kind)
        {
            case StatusKind.Failed:
                var error = item.Status.Error ?? "";
                var silent = error.StartsWith("No speech", StringComparison.Ordinal);
                var final = Item.Final(error);  // sending it again won't change that
                return Ui.Placeholder(silent ? Ui.VolumeOffGlyph : Ui.WarningGlyph,
                    silent ? "No Speech Heard" : error.StartsWith("This recording is longer", StringComparison.Ordinal) ? "Too Long to Summarise" : item.InCloud ? "This Meeting Couldn't Be Summarised" : "This Recording Couldn't Be Saved",
                    !item.InCloud && item.HasAudio ? "Its audio is still on this PC."
                        : final ? error + " Delete it, unless you'd like to keep it."
                        : item.HasAudio ? error + " Send it again to try once more."
                        : error + " The recording is no longer on this PC, so it can't be sent again.",
                    item.CanRetry ? Ui.Button(item.InCloud ? "Send Again" : "Try Again", () => model.Retry(item), accent: true) : null,
                    Ui.Button("Delete…", () => model.Deleting = item).With(b => b.IsEnabled = item.CanDelete));
            case StatusKind.Waiting when model.Auth.State != AuthState.SignedIn:
                return Ui.Placeholder(Ui.CloudGlyph, "Waiting for You to Sign In", "The recording is safe on this PC. Tally summarises it once you're signed in.",
                    Notices.SignInButton(model));
            case StatusKind.Recording when model.StoppedByCall != null:
                return Ui.Placeholder(Ui.StopGlyph, "Stopped When the Call Ended", "Still in the call? Continue, and it stays one recording. Otherwise it's sent in 30 seconds.",
                    Ui.Button("Continue Recording", () => _ = model.ContinueRecording(), accent: true), Ui.Button("Send Now", () => _ = model.StopRecording()));
            case StatusKind.Recording:
                // As Sound Recorder shows one: the time, large, then a word on what to do, and Stop
                elapsed = new TextBlock { Text = Elapsed, FontSize = 56, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
                hint = Ui.Text(Hint, brush: Ui.Secondary).With(t => { t.TextAlignment = TextAlignment.Center; t.MaxWidth = 360; t.HorizontalAlignment = HorizontalAlignment.Center; });
                var stop = Ui.RedButton("Stop Recording", Ui.StopGlyph, () => _ = model.StopRecording());
                stop.HorizontalAlignment = HorizontalAlignment.Center;
                stop.Padding = new Thickness(16, 8, 16, 9);
                return Ui.Column(14,
                    Ui.Row(8, Ui.Icon(Ui.DotGlyph, 12, new SolidColorBrush(Ui.Red)), Ui.Text("Recording", "BodyStrongTextBlockStyle", new SolidColorBrush(Ui.Red))).With(r => r.HorizontalAlignment = HorizontalAlignment.Center),
                    elapsed, hint, stop).With(s => { s.HorizontalAlignment = HorizontalAlignment.Center; s.VerticalAlignment = VerticalAlignment.Center; });
            default:
                return Ui.Placeholder(Ui.WaveGlyph, Format.StatusText(item.Status), item.SendError ?? item.Status.Kind switch
                {
                    StatusKind.Processing => "Transcribing, working out who's who, and writing the summary. A few minutes more.",
                    StatusKind.Uploading => "Sending the recording to be summarised.",
                    StatusKind.Waiting => "Tally sends it as soon as it can.",
                    StatusKind.Saving => "Saving the recording on this PC.",
                    _ => "Usually ready within a few minutes.",
                });
        }
    }

    private string Elapsed => model.Elapsed.Length == 0 ? "0:00" : model.Elapsed;  // before the first tick

    private string Hint => model.Recorder?.Levels.Lost == true
        ? Notices.LostMic
        : "Stop the recording when the meeting ends: here, or from Tally's icon by the clock.";

    private UIElement Ready(Item item)
    {
        var results = model.Results[Id];
        var names = Names(results);
        title = new Editable(model, Id, names, "title", "Title", s => s.Title, (s, t) => s with { Title = t }, keepsSpeakers: false, size: 28, strong: true);
        title.Refresh(model.SummaryOf(Id) ?? results.Summary, names, force: true);
        AutomationProperties.SetHeadingLevel(title.Box, AutomationHeadingLevel.Level1);
        when = Ui.Text(When(item, results), brush: Ui.Secondary);
        chips.Margin = new Thickness(0, 4, 0, 2);
        var column = Ui.Column(22, Ui.Column(8, title.Box, when, chips, foldersLine, filing), background, confirm, body);
        column.MaxWidth = 844;
        column.Padding = new Thickness(32, 12, 32, 48);

        tabs = new SelectorBar();
        foreach (var view in Enum.GetValues<Tab>())
        {
            var it = new SelectorBarItem { Text = view.ToString(), Tag = view };
            ToolTipService.SetToolTip(it, $"{view} (Ctrl+{(int)view + 1})");
            tabs.Items.Add(it);
            if (view == Tab.Files) filesTab = it;
        }
        tabs.SelectedItem = tabs.Items[(int)tab];
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem?.Tag is not Tab chosen || chosen == tab) return;
            if (tab == Tab.Summary) _ = model.SaveSummary(Id);  // what was typed, without waiting the second
            tab = chosen;
            ShowTab();
        };
        tabsSlot.Child = tabs;
        Sections();
        ShowTab();
        FilesLabel();
        _ = model.LoadFiles(Id);  // for the tab's count
        return new ScrollViewer { Content = column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = true };  // arrows scroll it
    }

    /// <summary>"Tuesday 20 October 2026, 20:00 · 47 min · English".</summary>
    private static string When(Item item, Results results) =>
        string.Join(" · ", new[] { Format.LongDate(item.StartedAt), Format.Duration(results.Duration), LanguageName(results.Language) }.OfType<string>());

    private static string? LanguageName(string? code)
    {
        if (code is null) return null;
        try { return CultureInfo.GetCultureInfo(code).DisplayName; }
        catch (CultureNotFoundException) { return null; }
    }

    /// <summary>Ctrl+1, 2 and 3.</summary>
    public void Show(Tab view)
    {
        if (tabs is not null) tabs.SelectedItem = tabs.Items[(int)view];
    }

    private void ShowTab()
    {
        summary = null;
        files = null;
        if (model.Results.GetValueOrDefault(Id) is not { } results) return;
        var names = Names(results);
        if (tab == Tab.Summary)
        {
            summary = new SummaryPage(model, Id, names, results.SpeakerOrder);
            summary.Refresh(names);
            body.Child = summary;
        }
        else if (tab == Tab.Transcript)
        {
            transcript = new Section();
            ShowTranscript(results, names);
            body.Child = transcript;
        }
        else
        {
            files = new FilesView(model, window, Id);
            body.Child = files;
        }
        copy.Label = CopyLabel;
        ToolTipService.SetToolTip(copy, tab == Tab.Transcript ? "Copy the transcript, with names (Ctrl+Shift+C)" : "Copy the summary, with names (Ctrl+Shift+C)");
    }

    private string CopyLabel => tab == Tab.Transcript ? "Copy Transcript" : "Copy Summary";

    private void ShowTranscript(Results results, Names names) =>
        transcript.Show(Speakers.Signature(model, Id, results, names) + showingBackground, () => Transcript(results, names));

    private void FilesLabel()
    {
        if (filesTab is null) return;
        var count = model.Files.GetValueOrDefault(Id)?.Count ?? 0;
        filesTab.Text = count > 0 ? $"Files ({count})" : "Files";
    }

    /// <summary>The parts under the title, each drawn again only when what it shows has changed.</summary>
    private void Sections()
    {
        if (Item is not { } item || model.Results.GetValueOrDefault(Id) is not { } results) return;
        var names = Names(results);
        var people = Speakers.Signature(model, Id, results, names);
        chips.Show(people, () => Speakers.Chips(model, Id, results, names));
        confirm.Show(people, () => Speakers.ToConfirm(model, Id, results, names));
        var row = model.Meetings.FirstOrDefault(m => m.MeetingId == Id);
        background.Show(string.Join("|", people, row?.Rewriting, row?.IsRewriting, row?.RewriteError, string.Join(",", results.SummaryWithout ?? []), model.IsEdited(Id)),
            () => Speakers.Background(model, Id, results, names));
        var paths = string.Join(", ", model.FolderChoices.Where(f => item.FolderIds.Contains(f.FolderId)).Select(f => model.FolderPath(f.FolderId)));
        foldersLine.Show(string.Join("|", paths, model.IsEdited(Id), model.SavingSummary.Contains(Id)), () => FoldersLine(paths));
        var suggestion = model.Suggestion(Id);
        filing.Show(suggestion is null ? "" : string.Join("|", model.FolderPath(suggestion.Folder), string.Join(",", suggestion.People.Select(p => model.PeopleById.GetValueOrDefault(p)?.Name))),
            () => suggestion is null ? null : Filing(suggestion));
    }

    /// <summary>Where it's filed (the Folders menu; Add to a Folder when it's in none), and whether it's the owner's
    /// copy, with Restore Original…, on one line.</summary>
    private UIElement FoldersLine(string paths)
    {
        var menu = Ui.Menu(items =>
        {
            if (Item is { } now) MeetingList.FolderChoices(model, now, items);
        });
        var folders = new Button
        {
            Content = Ui.Row(6, Ui.Icon(Ui.FolderGlyph, 14),
                new TextBlock { Text = paths.Length == 0 ? "Add to a Folder" : paths, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 },
                Ui.Icon(Ui.ChevronGlyph, 10)),
            Style = Ui.Style("SubtleButtonStyle"),
            Foreground = Ui.Secondary,
            Flyout = menu,
            Padding = new Thickness(6, 4, 8, 5),
            Margin = new Thickness(-6, 0, 0, 0),
        };
        ToolTipService.SetToolTip(folders, "The folders this meeting is in");
        Ui.Name(folders, paths.Length == 0 ? "Add to a Folder" : $"Folders: {paths}");
        var line = new Flow { Spacing = 14 };
        line.Children.Add(folders);
        if (model.IsEdited(Id))
        {
            var restore = Ui.Link("Restore Original…", async () =>
            {
                if (await Dialogs.Confirm("Restore the original summary?", "Your changes to this summary are deleted, and Tally's own summary is shown again.", "Restore Original"))
                    await model.RestoreSummary(Id);
            });
            line.Children.Add(Ui.Row(4, Ui.Icon(Ui.EditGlyph, 12, Ui.Secondary), Ui.Text(model.SavingSummary.Contains(Id) ? "Saving…" : "Edited by you", brush: Ui.Secondary), restore)
                .With(r => { foreach (var c in r.Children) ((FrameworkElement)c).VerticalAlignment = VerticalAlignment.Center; r.Margin = new Thickness(0, 1, 0, 0); }));
        }
        return line;
    }

    /// <summary>"You usually file meetings with Anna in Customers › Acme", with File There; closing it dismisses it for
    /// this meeting.</summary>
    private InfoBar Filing(Filing.Suggestion suggestion)
    {
        var people = suggestion.People.Select(p => model.PeopleById.GetValueOrDefault(p)?.Name).OfType<string>().ToList();
        var who = people.Count > 2 ? $"{people[0]}, {people[1]} and others" : SummaryPage.And(people);
        var path = model.FolderPath(suggestion.Folder);
        var file = Ui.Button("File There", () => _ = model.FileMeeting(Id, suggestion.Folder, true), accent: true);
        var bar = new InfoBar
        {
            IsOpen = true,
            Severity = InfoBarSeverity.Informational,
            IconSource = new FontIconSource { Glyph = Ui.FolderGlyph },
            Message = people.Count == 0 ? $"You usually file meetings like this in {path}." : $"You usually file meetings with {who} in {path}.",
            ActionButton = file,
            IsClosable = true,
            Margin = new Thickness(0, 6, 0, 0),
        };
        bar.CloseButtonClick += (_, _) => model.NotFiled = model.NotFiled.Add(Id);
        return bar;
    }

    /// <summary>Each paragraph under its speaker's name and time. A background voice's lines fold into one row where
    /// they come; shown, they're grey, with the way back in their context menu.</summary>
    private UIElement Transcript(Results results, Names names)
    {
        var stack = new StackPanel { Spacing = 16 };
        foreach (var (isBackground, lines) in Parts(results.Paragraphs, s => names.ReviewOf(s) == Review.Background))
        {
            if (isBackground && !showingBackground)
            {
                var show = Ui.Subtle(lines.Count == 1 ? "1 line of a background voice · Show" : $"{lines.Count} lines of background voices · Show", Ui.VolumeGlyph, () =>
                {
                    showingBackground = true;
                    ShowTranscript(results, names);
                });
                show.HorizontalAlignment = HorizontalAlignment.Left;
                show.Margin = new Thickness(-6, -4, 0, -4);
                stack.Children.Add(show);
                continue;
            }
            foreach (var paragraph in lines)
            {
                var header = new TextBlock();
                header.Inlines.Add(new Run { Text = names.Name(paragraph.Speaker), FontWeight = FontWeights.SemiBold });
                if (isBackground) header.Inlines.Add(new Run { Text = " · background" });
                header.Inlines.Add(new Run { Text = "  " + Format.Clock(paragraph.Start), FontSize = 12, Foreground = Ui.Secondary });
                var text = new TextBlock { Text = paragraph.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (isBackground)
                {
                    header.Foreground = text.Foreground = Ui.Secondary;
                    var menu = new MenuFlyout();
                    menu.Items.Add(Ui.Item("Not a Background Voice…", () => model.Assigning = new AssignForm(Id, paragraph.Speaker)));
                    text.ContextFlyout = menu;
                    header.ContextFlyout = menu;
                }
                stack.Children.Add(Ui.Column(3, header, text));
            }
        }
        return stack;
    }

    /// <summary>The paragraphs in runs: a background voice's consecutive lines together, everyone else's one by one.</summary>
    private static List<(bool Background, List<Paragraph> Lines)> Parts(IReadOnlyList<Paragraph> paragraphs, Func<string, bool> background)
    {
        var parts = new List<(bool Background, List<Paragraph> Lines)>();
        foreach (var paragraph in paragraphs)
        {
            var set = background(paragraph.Speaker);
            if (set && parts.Count > 0 && parts[^1].Background) parts[^1].Lines.Add(paragraph);
            else parts.Add((set, [paragraph]));
        }
        return parts;
    }

    /// <summary>Copy: what the page shows, with names, to paste anywhere. On the Transcript tab, the transcript.</summary>
    public void Copy()
    {
        if (phase != "ready" || model.Results.GetValueOrDefault(Id) is not { } results) return;
        var names = Names(results);
        var package = new DataPackage();
        package.SetText(tab == Tab.Transcript ? Markdown.Transcript(results, names) : Markdown.Summary(model.SummaryOf(Id) ?? results.Summary, names));
        try
        {
            Clipboard.SetContent(package);
            copy.Icon = new FontIcon { Glyph = Ui.CheckGlyph };
            copy.Label = "Copied";
        }
        catch (COMException)  // another program has the clipboard open (a clipboard manager, Remote Desktop): the owner copies again
        {
            copy.Label = "Couldn't Copy";
        }
        var count = ++copies;
        _ = Restore();

        async Task Restore()
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (count != copies) return;
            copy.Icon = new FontIcon { Glyph = Ui.CopyGlyph };
            copy.Label = CopyLabel;
        }
    }

    /// <summary>Ctrl+Z and Ctrl+Y on the summary: the model's undo, a burst of typing at a time, not the field's own.</summary>
    public void Undo(bool redo)
    {
        if (!ShowsSummary || model.Results.GetValueOrDefault(Id) is not { } results) return;
        if (redo) model.Redo(Id); else model.Undo(Id);
        var names = Names(results);
        title?.Refresh(model.SummaryOf(Id) ?? results.Summary, names, force: true);
        summary?.Refresh(names, force: true);
    }

    public Task Paste() => files?.Paste() ?? Task.CompletedTask;

    /// <summary>Leaving a meeting stops its clips, and saves what was typed without waiting the second.</summary>
    public void Leave()
    {
        model.Player.Stop();
        _ = model.SaveSummary(Id, leaving: true);
    }
}
