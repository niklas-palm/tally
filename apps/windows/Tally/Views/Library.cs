using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Tally;

/// <summary>
/// The window's left, as Windows' own navigation pane: Meetings, every meeting, with a count of those with someone to
/// confirm (fixed: it can't be renamed or deleted); People; and the owner's folders, nested as they like.
/// A meeting dropped on a folder is added to it; a folder dropped on another goes inside, and on Meetings to the top.
/// </summary>
internal sealed class Sidebar
{
    private readonly AppModel model;
    private readonly NavigationViewItem meetings, people, newFolder;
    private readonly InfoBadge badge = new() { Background = new SolidColorBrush(Ui.Orange), Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, NavigationViewItem> folders = [];
    private bool drawing;  // the pane's own events, raised as it's filled, aren't the owner's

    public NavigationView View { get; }

    public Sidebar(AppModel model, UIElement content)
    {
        this.model = model;
        View = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            ExpandedModeThresholdWidth = 900,
            CompactModeThresholdWidth = 600,
            OpenPaneLength = 240,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,  // the title bar has it, as Settings does
            IsTitleBarAutoPaddingEnabled = false,
            Content = content,
        };
        meetings = new NavigationViewItem { Content = "Meetings", Icon = new FontIcon { Glyph = Ui.MeetingsGlyph }, Tag = Place.Meetings, InfoBadge = badge };
        people = new NavigationViewItem { Content = "People", Icon = new FontIcon { Glyph = Ui.PeopleGlyph }, Tag = Place.People };
        // As Outlook's Create new folder: a row of its own at the folders' end, not a place
        newFolder = new NavigationViewItem { Content = "New Folder", Icon = new FontIcon { Glyph = Ui.NewFolderGlyph }, SelectsOnInvoked = false, Foreground = Ui.Secondary };
        ToolTipService.SetToolTip(newFolder, "New folder (Ctrl+Shift+N)");
        View.ItemInvoked += (view, e) => { if (e.InvokedItemContainer == newFolder) _ = model.NewFolder(); };
        Target(meetings, null);
        View.SelectionChanged += (_, e) => { if (!drawing && e.SelectedItemContainer?.Tag is Place place) model.Place = place; };
        View.Expanding += (_, e) => { if (!drawing && e.ExpandingItemContainer?.Tag is Place { FolderId: { } id }) model.Expanded = model.Expanded.Add(id); };
        View.Collapsed += (_, e) => { if (!drawing && e.CollapsedItemContainer?.Tag is Place { FolderId: { } id }) model.Expanded = model.Expanded.Remove(id); };
        View.KeyDown += (_, e) =>
        {
            // Only a folder's own row, the one with focus (not the one selected): the list and the meeting are inside the
            // pane's content too
            if (e.OriginalSource is not NavigationViewItem { Tag: Place { FolderId: { } id } } || Folder(id) is not { } folder) return;
            if (e.Key == VirtualKey.Delete) model.DeletingFolder = folder;
            else if (e.Key == VirtualKey.F2) model.RenamingFolder = id;
            else return;
            e.Handled = true;
        };
        Draw();
        Count();
    }

    public void Changed(string? name)
    {
        switch (name)
        {
            case nameof(AppModel.Folders) or nameof(AppModel.RenamingFolder):
                Draw();
                break;
            case nameof(AppModel.Expanded):
                drawing = true;
                foreach (var (id, item) in folders) item.IsExpanded = model.Expanded.Contains(id);
                drawing = false;
                break;
            case nameof(AppModel.Place):
                Select();
                break;
            case nameof(AppModel.Items) or nameof(AppModel.Results) or nameof(AppModel.People):
                Count();
                break;
        }
    }

    private Folder? Folder(string id) => model.Folders.FirstOrDefault(f => f.FolderId == id);

    private void Draw()
    {
        // A folder's row with focus (or its name being typed) has it again once drawn
        var again = View.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused ? FolderAt(focused) : null;
        drawing = true;
        View.MenuItems.Clear();
        folders.Clear();
        View.MenuItems.Add(meetings);
        View.MenuItems.Add(people);
        View.MenuItems.Add(new NavigationViewItemHeader { Content = "Folders" });
        foreach (var folder in model.Children(null)) View.MenuItems.Add(Item(folder));
        View.MenuItems.Add(newFolder);
        drawing = false;
        Select();
        if (again is not null && model.RenamingFolder != again)  // the row as it is then: a rename draws it twice
            View.DispatcherQueue.TryEnqueue(() => folders.GetValueOrDefault(again)?.Focus(FocusState.Programmatic));
    }

    private static string? FolderAt(DependencyObject element)
    {
        for (var at = element; at != null; at = VisualTreeHelper.GetParent(at))
            if (at is NavigationViewItem { Tag: Place { FolderId: { } id } }) return id;
        return null;
    }

    private void Select()
    {
        drawing = true;
        View.SelectedItem = model.Place.Kind switch
        {
            PlaceKind.People => people,
            PlaceKind.Folder => folders.GetValueOrDefault(model.Place.FolderId!),
            _ => meetings,
        };
        drawing = false;
    }

    /// <summary>As Mail counts unread: the meetings with someone to confirm.</summary>
    private void Count()
    {
        var count = model.Items.Count(i => model.ToConfirm(i.Id) > 0);
        badge.Value = count;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Ui.Name(meetings, count > 0 ? $"Meetings, {count} with someone to confirm" : "Meetings");
    }

    /// <summary>A folder, and inside it the folders it holds: its name typed in place when it's new or renamed.</summary>
    private NavigationViewItem Item(Folder folder)
    {
        var id = folder.FolderId;
        var renaming = model.RenamingFolder == id;
        var item = new NavigationViewItem
        {
            Content = renaming ? Rename(folder) : folder.Name,
            Icon = new FontIcon { Glyph = Ui.FolderGlyph },
            Tag = Place.Folder(id),
            ContextFlyout = Menu(folder),
        };
        foreach (var child in model.Children(id)) item.MenuItems.Add(Item(child));
        item.IsExpanded = model.Expanded.Contains(id);
        item.DragStarting += (_, e) =>
        {
            Dragging.Id = id;
            e.Data.SetText(model.FolderPath(id));
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        item.DropCompleted += (_, _) => Dragging.Id = null;
        if (!renaming) Draggable(item, id);
        Target(item, id);
        folders[id] = item;
        return item;
    }

    /// <summary>Starts a folder's drag by hand: the pane takes the pointer, so its rows don't drag by themselves. It
    /// starts once the pointer has moved a few pixels with the button down on this row, not on a folder inside it.</summary>
    private static void Draggable(NavigationViewItem item, string id)
    {
        Windows.Foundation.Point? pressed = null;
        bool Own(RoutedEventArgs e) => e.OriginalSource is DependencyObject source && FolderAt(source) == id;
        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(item);
            pressed = Own(e) && point.Properties.IsLeftButtonPressed ? point.Position : null;
        }), handledEventsToo: true);
        item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, e) =>
        {
            var point = e.GetCurrentPoint(item);
            if (pressed is not { } start || !point.Properties.IsLeftButtonPressed) return;
            if (Math.Abs(point.Position.X - start.X) + Math.Abs(point.Position.Y - start.Y) < 8) return;
            pressed = null;
            try { await item.StartDragAsync(point); }
            catch (COMException) { }  // an elevated Tally can't drag (WinUI): no drag, rather than no Tally
        }), handledEventsToo: true);
        item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => pressed = null), handledEventsToo: true);
    }

    private MenuFlyout Menu(Folder folder)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item("Rename", () => model.RenamingFolder = folder.FolderId, glyph: Ui.EditGlyph).With(i => i.KeyboardAcceleratorTextOverride = "F2"));
        menu.Items.Add(Ui.Item("New Folder Inside", () => _ = model.NewFolder(folder.FolderId), glyph: Ui.NewFolderGlyph));
        if (folder.Parent != null) menu.Items.Add(Ui.Item("Move to the Top", () => _ = model.NestFolder(folder.FolderId, null)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item("Delete Folder…", () => model.DeletingFolder = folder, glyph: Ui.DeleteGlyph).With(i => i.KeyboardAcceleratorTextOverride = "Delete"));
        return menu;
    }

    /// <summary>The name, typed where it shows, as in File Explorer: Enter or leaving keeps it, Esc doesn't.</summary>
    private TextBox Rename(Folder folder)
    {
        var box = new TextBox { Text = folder.Name, MinWidth = 0, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(-6, 0, 0, 0) };
        Ui.Name(box, "Folder name");
        bool done = false, focused = false;
        void Finish(bool keep)
        {
            if (done) return;
            done = true;
            if (keep) _ = model.RenameFolder(folder.FolderId, box.Text);
            else model.RenamingFolder = null;
        }
        // After the menu that asked for it has closed and given focus back to the row
        box.Loaded += (_, _) => box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        });
        box.GotFocus += (_, _) => focused = true;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) Finish(true);
            else if (e.Key == VirtualKey.Escape) Finish(false);
            // The row would take these as its own: selecting, folding, moving to the next folder
            else if (e.Key is not (VirtualKey.Space or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
            e.Handled = true;
        };
        box.LostFocus += (_, _) => { if (focused && model.RenamingFolder == folder.FolderId) Finish(true); };
        return box;
    }

    /// <summary>Takes a meeting (into a folder) or a folder (inside it, or to the top on Meetings).</summary>
    private void Target(NavigationViewItem item, string? folder)
    {
        item.AllowDrop = true;
        item.DragOver += (_, e) =>
        {
            if (Dragging.Id is not { } dragged || !Accepts(dragged, folder)) return;
            var isFolder = dragged.StartsWith("f_", StringComparison.Ordinal);
            e.AcceptedOperation = isFolder ? DataPackageOperation.Move : DataPackageOperation.Copy;
            var name = folder is null ? "the top" : Folder(folder)?.Name;
            e.DragUIOverride.Caption = isFolder ? folder is null ? "Move to the top" : $"Move into {name}" : $"Add to {name}";
            e.Handled = true;
        };
        item.Drop += (_, e) =>
        {
            if (Dragging.Id is not { } dragged || !Accepts(dragged, folder)) return;
            if (dragged.StartsWith("f_", StringComparison.Ordinal)) _ = model.NestFolder(dragged, folder);
            else _ = model.FileMeeting(dragged, folder!, true);
            e.Handled = true;
        };
    }

    /// <summary>Only Tally's own: a meeting that's been sent, into a folder it isn't in; a folder, never inside itself.
    /// Meetings has every meeting already.</summary>
    private bool Accepts(string dragged, string? folder)
    {
        if (dragged.StartsWith("f_", StringComparison.Ordinal))
            return folder is null ? Folder(dragged)?.Parent != null : dragged != folder && !model.Within(dragged).Contains(folder) && Folder(dragged)?.Parent != folder;
        return folder != null && model.Meetings.FirstOrDefault(m => m.MeetingId == dragged) is { } meeting && meeting.FolderIds?.Contains(folder) != true;
    }
}

/// <summary>What's being dragged inside Tally, a meeting or a folder: its id. Other apps get its text.</summary>
internal static class Dragging
{
    public static string? Id { get; set; }
}

/// <summary>
/// The middle: the meetings where the sidebar is, under Today, Yesterday or their date, newest first, as Mail lists
/// messages; or, while searching, what was found. Over it, where you are with a count, and Record, as New mail is over
/// Mail's list: it makes a meeting, it isn't one.
/// </summary>
internal sealed class MeetingList : Grid
{
    private readonly AppModel model;
    private readonly TextBlock title = Ui.Text("", "SubtitleTextBlockStyle", wrap: false);
    private readonly TextBlock count = Ui.Caption("");
    private readonly Border record = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border body = new();
    private readonly ListView list = NewList(), found = NewList();
    private TextBlock? stopTime;
    private bool selecting;

    public MeetingList(AppModel model)
    {
        this.model = model;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var header = Ui.Grid("*,Auto", spacing: 8).Add(Ui.Column(0, title, count), record.At(1));
        header.Padding = new Thickness(16, 10, 12, 10);
        Children.Add(header);
        Children.Add(body.At(0, 1));
        foreach (var view in new[] { list, found })
        {
            view.SelectionChanged += (_, _) =>
            {
                if (!selecting && view.SelectedItem is ListViewItem { Tag: string id } && !id.StartsWith('#')) model.Selection = id;
            };
            view.DragItemsStarting += (_, e) =>
            {
                // The item dragged may be the row or its content
                var dragged = e.Items.FirstOrDefault();
                if (view.Items.OfType<ListViewItem>().FirstOrDefault(r => ReferenceEquals(r, dragged) || ReferenceEquals(r.Content, dragged)) is not { Tag: string id }
                    || model.Items.FirstOrDefault(i => i.Id == id) is not { } item)
                {
                    e.Cancel = true;
                    return;
                }
                Dragging.Id = id;  // a meeting not yet sent is refused by every folder (Accepts)
                e.Data.SetText(model.DragText(item));
                e.Data.RequestedOperation = DataPackageOperation.Copy;
            };
            view.DragItemsCompleted += (_, _) => Dragging.Id = null;
            view.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Delete || model.Items.FirstOrDefault(i => i.Id == model.Selection) is not { CanDelete: true } item) return;
                model.Deleting = item;
                e.Handled = true;
            };
        }
        Header();
        Record();
        Draw();
    }

    /// <summary>Where you are, as Mail titles a mailbox: the meeting's own title is large on its page already.</summary>
    public static string Title(AppModel model) => model.Place.Kind switch
    {
        PlaceKind.Meetings => "Meetings",
        PlaceKind.People => "People",
        _ => model.Folders.FirstOrDefault(f => f.FolderId == model.Place.FolderId)?.Name ?? "Folder",
    };

    public void Changed(string? name)
    {
        switch (name)
        {
            case nameof(AppModel.Elapsed):
                if (stopTime is not null) stopTime.Text = $"Stop {model.Elapsed}";
                return;
            case nameof(AppModel.Recorder) or nameof(AppModel.Starting):
                Record();
                return;
            case nameof(AppModel.Selection):
                Select();
                return;
            case nameof(AppModel.Place) or nameof(AppModel.Folders) or nameof(AppModel.Items) or nameof(AppModel.Results) or nameof(AppModel.People)
                or nameof(AppModel.SearchResults) or nameof(AppModel.SearchFailed) or nameof(AppModel.Query) or nameof(AppModel.Offline) or "Auth":
                Header();
                Draw();
                return;
        }
    }

    private void Header()
    {
        title.Text = Title(model);
        var n = model.ItemsIn(model.Place).Count;
        count.Text = n == 1 ? "1 meeting" : $"{n} meetings";
    }

    /// <summary>Record; while recording, Stop with the time, in red, as Sound Recorder shows it. Ctrl+R does the same,
    /// and so does the tray.</summary>
    private void Record()
    {
        stopTime = null;
        if (model.Recorder != null)
        {
            stopTime = new TextBlock { Text = $"Stop {model.Elapsed}" };
            var stop = Ui.RedButton("Stop Recording", Ui.StopGlyph, () => _ = model.StopRecording(), label: stopTime);
            ToolTipService.SetToolTip(stop, "Stop recording (Ctrl+R)");
            record.Child = stop;
            return;
        }
        var start = new Button { Content = Ui.Row(8, Ui.Icon(Ui.RecordGlyph, 14, new SolidColorBrush(Ui.Red)), new TextBlock { Text = "Record" }), IsEnabled = !model.Starting };
        start.Click += (_, _) => _ = model.StartRecording();
        ToolTipService.SetToolTip(start, "Start a new recording (Ctrl+R)");
        Ui.Name(start, "Record");
        record.Child = start;
    }

    private static ListView NewList() => new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        CanDragItems = true,
        Padding = new Thickness(0, 0, 0, 12),
    };

    private void Draw()
    {
        if (model.SearchResults is { } results) body.Child = Search(results);
        else if (model.Query.Trim().Length > 0)  // not the list, as if it matched
            body.Child = Ui.Column(12, new ProgressRing { IsActive = true }, Ui.Text("Searching…", brush: Ui.Secondary))
                .With(s => { s.HorizontalAlignment = HorizontalAlignment.Center; s.VerticalAlignment = VerticalAlignment.Center; });
        else body.Child = Meetings();
    }

    private UIElement Meetings()
    {
        var items = model.ItemsIn(model.Place);
        var signedOut = model.Auth.State != AuthState.SignedIn;
        if (items.Count == 0 && model.Place == Place.Meetings && model.Meetings.Count == 0 && (signedOut || model.Offline))
            return Ui.Placeholder(Ui.MeetingsGlyph, signedOut ? "Sign In to See Your Meetings" : "Your Meetings Show Here Once You're Back Online",
                "Recordings you make wait safely on this PC.");
        if (items.Count == 0)
            return Ui.Placeholder(Ui.MeetingsGlyph, model.Place == Place.Meetings ? "No Meetings Yet" : "No Meetings Here",
                model.Place == Place.Meetings ? "Your recordings appear here." : "Drag meetings onto this folder, or right-click one and choose Folders.");
        var rows = new List<ListViewItem>();
        foreach (var day in items.GroupBy(i => i.StartedAt.ToLocalTime().Date))
        {
            rows.Add(Heading(DayTitle(day.Key)));
            rows.AddRange(day.Select(item => Row(item)));
        }
        Fill(list, rows);
        return list;
    }

    /// <summary>"Today", "Yesterday", "Wednesday 21 October", with the year when it isn't this one.</summary>
    private static string DayTitle(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday"
            : day.ToString(day.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>A day's heading in the list: not a row to select.</summary>
    private static ListViewItem Heading(string text) => new ListViewItem
    {
        Content = Ui.Text(text, "CaptionTextBlockStyle", Ui.Secondary).With(b => b.FontWeight = FontWeights.SemiBold),
        IsEnabled = false,
        Tag = "#" + text,
        MinHeight = 0,
        Padding = new Thickness(16, 12, 12, 2),
    }.With(i => i.Resources["ListViewItemDisabledThemeOpacity"] = 1.0);

    /// <summary>A meeting, as Mail shows a message: its title (two lines if it needs them) with the time beside it, and
    /// under it its length and the folders it's in, or where it is when it isn't ready. A status icon only for one that
    /// isn't ready, and an orange count of people to confirm.</summary>
    private ListViewItem Row(Item item)
    {
        var toConfirm = model.ToConfirm(item.Id);
        var local = item.StartedAt.ToLocalTime();
        var grid = Ui.Grid("Auto,*,Auto", "Auto,Auto", 8).With(g => g.RowSpacing = 2);
        if (item.Status.Kind != StatusKind.Ready)
            grid.Children.Add(MeetingRow.StatusIcon(item.Status).With(i => { i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 2, 0, 0); }).At(0));
        grid.Add(
            Ui.Text(item.Title ?? $"Recording at {local:t}", "BodyStrongTextBlockStyle").With(b => { b.MaxLines = 2; b.TextTrimming = TextTrimming.CharacterEllipsis; }).At(1),
            Ui.Caption(local.ToString("t", CultureInfo.CurrentCulture)).With(b => { b.VerticalAlignment = VerticalAlignment.Top; b.Margin = new Thickness(0, 1, 0, 0); }).At(2),
            Ui.Text(Detail(item), brush: item.SendError is null ? Ui.Secondary : new SolidColorBrush(Ui.Orange), wrap: false).At(1, 1),
            toConfirm > 0 ? Ui.Badge(toConfirm, AppModel.ToConfirmText(toConfirm)).With(b => b.HorizontalAlignment = HorizontalAlignment.Right).At(2, 1) : null);
        var row = new ListViewItem { Content = grid, Tag = item.Id, Padding = new Thickness(16, 8, 12, 8), ContextFlyout = Menu(model, item.Id) };
        Ui.Name(row, string.Join(", ", new[] { item.Title ?? "Recording", local.ToString("t"), Detail(item), toConfirm > 0 ? AppModel.ToConfirmText(toConfirm) : null }.OfType<string>()));
        return row;
    }

    /// <summary>The time is beside the title: under it, how long, and the folders it's in but the one being looked at.</summary>
    private string Detail(Item item) => MeetingRow.Detail(item, null,
        string.Join(", ", model.FolderChoices.Where(f => item.FolderIds.Contains(f.FolderId) && f.FolderId != model.Place.FolderId).Select(f => f.Name)));

    /// <summary>What a search found: each meeting, newest first, with where it matched and a snippet, the words searched
    /// for in bold.</summary>
    private UIElement Search(IReadOnlyList<SearchHit> results)
    {
        if (results.Count == 0 && model.SearchFailed)
            return Ui.Placeholder(Ui.SearchGlyph, "Couldn't Search", model.Auth.State != AuthState.SignedIn ? "Sign in to search your meetings."
                : model.Offline ? "You're offline. Try again when you're back online." : "Try again in a moment.");
        if (results.Count == 0) return Ui.Placeholder(Ui.SearchGlyph, $"No Results for “{model.Query.Trim()}”", "Check the spelling or try a new search.");
        var rows = new List<ListViewItem> { Heading(results.Count == 1 ? "1 meeting" : $"{results.Count} meetings") };
        foreach (var hit in results)
        {
            var snippet = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Secondary };
            foreach (var run in Snippets.Marked(hit.Snippet, model.Query))
                snippet.Inlines.Add(run.Bold ? new Run { Text = run.Text, FontWeight = FontWeights.SemiBold, Foreground = Ui.Theme("TextFillColorPrimaryBrush") } : new Run { Text = run.Text });
            var when = hit.StartedAt?.ToLocalTime() is { } at ? at.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + ", " + at.ToString("t", CultureInfo.CurrentCulture) : null;
            var content = Ui.Column(2,
                Ui.Text(hit.Title ?? "Meeting", "BodyStrongTextBlockStyle", wrap: false),
                Ui.Caption(string.Join(" · ", new[] { when, Where(hit) }.OfType<string>())),
                snippet);
            var row = new ListViewItem { Content = content, Tag = hit.MeetingId, Padding = new Thickness(16, 8, 12, 8), ContextFlyout = Menu(model, hit.MeetingId) };
            Ui.Name(row, $"{hit.Title ?? "Meeting"}, {Where(hit)}: {hit.Snippet}");
            rows.Add(row);
        }
        Fill(found, rows);
        return found;
    }

    private static string Where(SearchHit hit) => hit.Where switch
    {
        "title" => "In the title",
        "summary" => "In the summary",
        "people" => "Who was there",
        _ => hit.At is { } at ? $"Said at {Format.Clock(at)}" : "In the transcript",
    };

    /// <summary>The rows, in place when they're the same ones, so the list keeps where it's scrolled to and what has
    /// focus; anew when they've changed.</summary>
    private void Fill(ListView view, List<ListViewItem> rows)
    {
        selecting = true;
        var current = view.Items.Cast<ListViewItem>().ToList();
        if (current.Select(r => r.Tag).SequenceEqual(rows.Select(r => r.Tag)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var content = rows[i].Content;
                rows[i].Content = null;
                current[i].Content = content;
                current[i].ContextFlyout = rows[i].ContextFlyout;
                Ui.Name(current[i], AutomationProperties.GetName(rows[i]));
            }
        }
        else
        {
            view.Items.Clear();
            foreach (var row in rows) view.Items.Add(row);
        }
        selecting = false;
        Select();
    }

    private void Select()
    {
        selecting = true;
        foreach (var view in new[] { list, found })
            view.SelectedItem = view.Items.Cast<ListViewItem>().FirstOrDefault(r => r.Tag as string == model.Selection);
        selecting = false;
    }

    /// <summary>A meeting's context menu, as it is when it opens: Send Again for a failed one, Folders, Delete.</summary>
    public static MenuFlyout Menu(AppModel model, string id)
    {
        return Ui.Menu(items =>
        {
            if (model.Items.FirstOrDefault(i => i.Id == id) is not { } item) return;
            if (item.CanRetry) items.Add(Ui.Item(item.InCloud ? "Send Again" : "Try Again", () => model.Retry(item), glyph: Ui.SyncGlyph));
            // Only a meeting that's been sent: one still on this PC can't be filed yet
            var folders = new MenuFlyoutSubItem { Text = "Folders", IsEnabled = item.InCloud, Icon = new FontIcon { Glyph = Ui.FolderGlyph } };
            FolderChoices(model, item, folders.Items);
            items.Add(folders);
            items.Add(new MenuFlyoutSeparator());
            items.Add(Ui.Item("Delete…", () => model.Deleting = item, item.CanDelete, Ui.DeleteGlyph).With(i => i.KeyboardAcceleratorTextOverride = "Delete"));
        });
    }

    /// <summary>The owner's folders, by path, each ticked when the meeting is in it: a click adds it or takes it out, as
    /// Outlook's categories do. The Folders menu, and the meeting's header.</summary>
    public static void FolderChoices(AppModel model, Item item, IList<MenuFlyoutItemBase> into)
    {
        if (model.Folders.Count == 0) into.Add(new MenuFlyoutItem { Text = "No folders yet: make one with New Folder in the sidebar", IsEnabled = false });
        foreach (var folder in model.FolderChoices)
        {
            var choice = new ToggleMenuFlyoutItem { Text = model.FolderPath(folder.FolderId), IsChecked = item.FolderIds.Contains(folder.FolderId) };
            choice.Click += (_, _) => _ = model.FileMeeting(item.Id, folder.FolderId, choice.IsChecked);
            into.Add(choice);
        }
    }
}
