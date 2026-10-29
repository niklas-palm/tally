using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Tally;

/// <summary>
/// Tally's one window, as Outlook and File Explorer are: where you are (Meetings, your folders, People) in the
/// navigation pane, what's there in the middle, and the one chosen; search in the title bar, and what
/// needs saying in a banner over the columns. Closing it keeps Tally running in the notification area, as closing the
/// Mac's window keeps it in the menu bar. It opens centred at a friendly size, never at last time's.
///
/// Each part draws itself from the model and again on the changes that matter to it, and only what changed.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppModel model;
    private readonly Sidebar sidebar;
    private MeetingList meetings;
    private PeopleList people;
    private readonly Border listColumn = new(), detailColumn = new();
    private readonly StackPanel banner = new();
    private readonly AutoSuggestBox search;
    private MeetingView? meeting;
    private PersonPanel? person;
    private string detail = "", notices = "";
    private AssignDialog? assigning;
    private PersonDialog? editing;
    private bool pickFirst;  // just opened: the newest meeting shows once there are any

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tally.ico"));
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Tally.png"))) };
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            meeting?.Leave();  // its clips stop, and what was typed is saved
            AppWindow.Hide();
        };

        search = new AutoSuggestBox { PlaceholderText = "Search meetings", QueryIcon = new SymbolIcon(Symbol.Find), Width = 400, VerticalAlignment = VerticalAlignment.Center };
        Ui.Name(search, "Search meetings");
        search.TextChanged += (box, e) => { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) model.Query = box.Text; };
        // Esc empties it, as in Explorer: the box takes Esc for itself, so it's heard even then
        search.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != VirtualKey.Escape || search.Text.Length == 0) return;
            search.Text = "";
            model.Query = "";  // the list again
            e.Handled = true;
        }), handledEventsToo: true);
        AppTitleBar.Content = search;

        meetings = new MeetingList(model);
        people = new PeopleList(model);
        var divider = new Border { Width = 1, Background = Ui.Theme("DividerStrokeColorDefaultBrush") };
        var columns = Ui.Grid("290,Auto,*").Add(listColumn, divider.At(1), detailColumn.At(2));
        var content = Ui.Grid("*", "Auto,*").Add(banner, columns.At(0, 1));
        sidebar = new Sidebar(model, content);
        Body.Children.Add(sidebar.View);
        AppTitleBar.PaneToggleRequested += (_, _) => sidebar.View.IsPaneOpen = !sidebar.View.IsPaneOpen;
        var loaded = new TaskCompletionSource();
        Root.Loaded += (_, _) => loaded.TrySetResult();
        Dialogs.Root = async () =>
        {
            await loaded.Task;  // a window just made has no XamlRoot until it's on screen
            return Root.XamlRoot;
        };

        model.PropertyChanged += (_, e) => Changed(e.PropertyName);
        model.Auth.PropertyChanged += (_, _) => Changed("Auth");
        Root.ActualThemeChanged += (_, _) => Redraw();  // the brushes are the theme's, taken as each part is drawn
        Keys();
        Columns();
        Detail(null);
        Banner();
        ShowTitle();
    }

    /// <summary>Shows it, in front: centred at 980 × 640, within 85% × 80% of the screen.</summary>
    public void Present()
    {
        if (!AppWindow.IsVisible)
        {
            Size();
            pickFirst = true;
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        model.Welcomed = true;
        PickFirst();
        _ = model.Sync();  // up to date when looked at, not at the next tick
    }

    private void PickFirst()
    {
        if (!pickFirst || model.Place == Place.People || model.Selection != null) return;
        if (model.ItemsIn(model.Place).FirstOrDefault() is not { } first) return;
        pickFirst = false;
        model.Selection = first.Id;
    }

    private void Size()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Native.GetDpiForSystem() / 96.0;
        var width = (int)Math.Min(980 * scale, area.Width * 0.85);
        var height = (int)Math.Min(640 * scale, area.Height * 0.8);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void Changed(string? name)
    {
        switch (name)
        {
            case nameof(AppModel.Assigning):
                Assign();
                return;
            case nameof(AppModel.Editing):
                Edit();
                return;
            case nameof(AppModel.Saving):
                editing?.Enable();
                return;
            case nameof(AppModel.Deleting):
                _ = DeleteMeeting();
                return;
            case nameof(AppModel.DeletingPerson):
                _ = DeletePerson();
                return;
            case nameof(AppModel.DeletingFolder):
                _ = DeleteFolder();
                return;
            case nameof(AppModel.Query):
                if (search.Text != model.Query) search.Text = model.Query;
                break;
            case nameof(AppModel.Items):
                PickFirst();
                break;
        }
        sidebar.Changed(name);
        meetings.Changed(name);
        people.Changed(name);
        if (name == nameof(AppModel.Place)) Columns();
        Detail(name);
        if (name == nameof(AppModel.Elapsed)) return;
        Banner();
        ShowTitle();
    }

    /// <summary>After the theme changes: everything drawn again, in its colours.</summary>
    private void Redraw()
    {
        meetings = new MeetingList(model);
        people = new PeopleList(model);
        meeting?.Leave();
        detail = notices = "";
        Columns();
        Detail(null);
        Banner();
    }

    /// <summary>Where you are, in the taskbar and Alt+Tab: "Meetings - Tally".</summary>
    private void ShowTitle() => AppWindow.Title = $"{MeetingList.Title(model)} - Tally";

    private void Columns() => listColumn.Child = model.Place == Place.People ? people : meetings;

    /// <summary>The right: the person or meeting chosen, the welcome before there are any meetings, or nothing chosen.</summary>
    private void Detail(string? name)
    {
        string key;
        if (model.Place == Place.People) key = model.SelectedPerson is { } p ? "person:" + p.PersonId : "no person";
        else if (model.Selection is { } id && model.Items.Any(i => i.Id == id)) key = "meeting:" + id;
        else if (model.Items.Count == 0) key = $"welcome|{model.Recorder != null}|{model.Starting}|{model.Auth.State}|{Notices.SignInExplanation(model)}";
        else key = "no meeting";
        if (key == detail)
        {
            meeting?.Changed(name);
            person?.Changed(name);
            return;
        }
        meeting?.Leave();
        meeting = null;
        person = null;
        detail = key;
        if (key.StartsWith("person:", StringComparison.Ordinal)) detailColumn.Child = person = new PersonPanel(model, key["person:".Length..]);
        else if (key.StartsWith("meeting:", StringComparison.Ordinal))
        {
            detailColumn.Child = meeting = new MeetingView(model, this, model.Selection!);
            _ = model.Load(model.Selection!);  // its clip links, fresh once they're about to expire
        }
        else if (key.StartsWith("welcome", StringComparison.Ordinal)) detailColumn.Child = Welcome();
        else detailColumn.Child = key == "no person"
            ? Ui.Placeholder(Ui.PeopleGlyph, "No Person Selected", "Their meetings show here.")
            : Ui.Placeholder(Ui.NoteGlyph, "No Meeting Selected", null);
    }

    /// <summary>The notices over the columns, a line each; the sign-in prompt is left out when the page shows its own.</summary>
    private void Banner()
    {
        var item = model.Items.FirstOrDefault(i => i.Id == model.Selection);
        var pageAsks = model.Place != Place.People && (item is null ? model.Items.Count == 0 : item.Status.Kind == StatusKind.Waiting);
        var signature = string.Join("|", model.Problem, model.FolderProblem, model.Update, model.Updating, model.RecordingOrSaving, model.Crash, model.SendingCrash,
            model.Auth.State, model.Auth.Expired, model.WaitingForSignIn, model.Offline, model.SyncError, pageAsks, model.SignInName);
        if (signature == notices) return;
        notices = signature;
        banner.Children.Clear();
        var lines = Notices.For(model, inWindow: true, asksToSignIn: !pageAsks).ToList();
        if (lines.Count == 0) return;
        var stack = new StackPanel();
        foreach (var line in lines) stack.Children.Add(line);
        banner.Children.Add(new Border
        {
            Child = stack,
            Background = Ui.Theme("LayerOnMicaBaseAltFillColorDefaultBrush"),
            BorderBrush = Ui.Theme("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        });
    }

    /// <summary>The first launch's welcome, in three steps: record, sign in, confirm who spoke.</summary>
    private UIElement Welcome()
    {
        var record = Ui.Button(model.Recorder == null ? "Start Recording" : "Stop Recording", () => _ = model.ToggleRecording());
        record.IsEnabled = !model.Starting;
        UIElement signIn = model.Auth.State == AuthState.SignedIn
            ? Ui.Row(6, Ui.Seal(16), Ui.Text("Signed in", brush: Ui.Success))
            : model.Auth.State == AuthState.SigningIn  // otherwise the step says why itself
                ? (UIElement)Ui.Column(8, Ui.Caption(Notices.SignInExplanation(model)), Notices.SignInButton(model).With(b => b.HorizontalAlignment = HorizontalAlignment.Left))
                : Notices.SignInButton(model).With(b => b.HorizontalAlignment = HorizontalAlignment.Left);
        var title = Ui.Text("Welcome to Tally", "TitleLargeTextBlockStyle");
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var stack = Ui.Column(24,
            title,
            Ui.Text("Tally records your meetings and gives you back a summary, with who said what.", "BodyLargeTextBlockStyle", Ui.Secondary).With(t => t.Margin = new Thickness(0, -14, 0, 4)),
            Step(1, "Record", "Click Tally's T. by the clock (under ^ until you pin it), or press Ctrl+R here. Tally records your microphone and what your PC plays, so it works in any call app and in the room.", record),
            Step(2, "Sign in", "Summaries are made in the cloud. Recordings wait safely on this PC until you're signed in.", signIn),
            Step(3, "Confirm who spoke", "Each summary lists the people in it. Confirm a voice once, and Tally recognises it in later meetings.", null));
        stack.MaxWidth = 560;
        stack.Padding = new Thickness(40);
        stack.VerticalAlignment = VerticalAlignment.Center;
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        static UIElement Step(int number, string title, string text, UIElement? action)
        {
            var circle = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top,
                Background = Ui.Tint(Ui.Accent, 0x30),
                Child = new TextBlock { Text = number.ToString(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var heading = Ui.Text(title, "BodyStrongTextBlockStyle");
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            return Ui.Grid("Auto,*", spacing: 14).Add(circle, Ui.Column(8, heading, Ui.Text(text, brush: Ui.Secondary), action).At(1));
        }
    }

    private void Assign()
    {
        if (model.Assigning is not { } form)
        {
            assigning?.Dialog.Hide();
            return;
        }
        if (assigning?.Form == form) return;
        var dialog = assigning = new AssignDialog(model, form);
        dialog.Dialog.Closed += (_, _) =>
        {
            if (model.Assigning == form) model.Assigning = null;
            if (assigning == dialog) assigning = null;
        };
        _ = Dialogs.Show(dialog.Dialog);
    }

    private void Edit()
    {
        if (model.Editing is not { } form)
        {
            editing?.Dialog.Hide();  // saved
            return;
        }
        if (editing?.Form == form) return;
        var dialog = editing = new PersonDialog(model, form);
        dialog.Dialog.Closed += (_, _) =>
        {
            if (model.Editing == form) model.Editing = null;
            if (editing == dialog) editing = null;
        };
        _ = Dialogs.Show(dialog.Dialog);
    }

    private async Task DeleteMeeting()
    {
        if (model.Deleting is not { } item) return;
        var yes = await Dialogs.Confirm($"Delete {(item.Title is { } title ? $"“{title}”" : "this meeting")}?", item.InCloud
            ? "Its recording, transcript and summary are deleted, and so is everything Tally learned about voices from it. This can't be undone."
              + (item.Status.Kind is StatusKind.Queued or StatusKind.Processing ? " Tally stops summarising it." : "")
            : "It's only on this PC, so it can't be recovered.", "Delete");
        if (model.Deleting == item) model.Deleting = null;
        if (yes) await model.Delete(item);
    }

    private async Task DeletePerson()
    {
        if (model.DeletingPerson is not { } who) return;
        var yes = await Dialogs.Confirm($"Delete {who.Name}?", "Tally forgets their voice and stops recognising them. Meetings they were in show them as an unknown speaker.", "Delete");
        if (model.DeletingPerson == who) model.DeletingPerson = null;
        if (yes) await model.Delete(who);
    }

    private async Task DeleteFolder()
    {
        if (model.DeletingFolder is not { } folder) return;
        var yes = await Dialogs.Confirm($"Delete “{folder.Name}”?", model.Children(folder.FolderId).Count == 0
            ? "Its meetings aren't deleted: they stay in Meetings, and in any other folders they're in."
            : "The folders inside it go too. Their meetings aren't deleted: they stay in Meetings, and in any other folders they're in.", "Delete Folder");
        if (model.DeletingFolder == folder) model.DeletingFolder = null;
        if (yes) await model.Delete(folder);
    }

    /// <summary>Windows' keys for what the Mac does with ⌘: Ctrl+R records, Ctrl+1 2 3 the meeting's views, Ctrl+Shift+C
    /// copies, Ctrl+F searches, Ctrl+Shift+N makes a folder; Ctrl+Z and Ctrl+Y undo the summary's edits.</summary>
    private void Keys()
    {
        void Key(VirtualKey key, VirtualKeyModifiers modifiers, Action act)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                act();
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control, shift = VirtualKeyModifiers.Shift;
        Key(VirtualKey.R, ctrl, () => { if (!model.Starting) _ = model.ToggleRecording(); });
        Key(VirtualKey.F, ctrl, () => search.Focus(FocusState.Keyboard));
        Key(VirtualKey.Number1, ctrl, () => meeting?.Show(MeetingView.Tab.Summary));
        Key(VirtualKey.Number2, ctrl, () => meeting?.Show(MeetingView.Tab.Transcript));
        Key(VirtualKey.Number3, ctrl, () => meeting?.Show(MeetingView.Tab.Files));
        Key(VirtualKey.C, ctrl | shift, () => meeting?.Copy());
        Key(VirtualKey.N, ctrl | shift, () => _ = model.NewFolder());
        // Before any field takes them: the summary's undo is the model's, a burst of typing at a time, and a paste
        // onto the Files tab is files
        Root.PreviewKeyDown += (_, e) =>
        {
            if (meeting is null || !Down(VirtualKey.Control)) return;
            var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            var inField = focused is TextBox || Ui.Within(focused, search);
            var onPage = Ui.Within(focused, meeting);
            if (e.Key is VirtualKey.Z or VirtualKey.Y && meeting.ShowsSummary && (onPage || !inField))
            {
                meeting.Undo(redo: e.Key == VirtualKey.Y || Down(VirtualKey.Shift));
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.V && meeting.ShowsFiles && !inField)
            {
                _ = meeting.Paste();
                e.Handled = true;
            }
        };
    }

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}

/// <summary>The window's dialogs, one at a time as Windows allows: a second waits for the first to close.</summary>
internal static class Dialogs
{
    public static Func<Task<XamlRoot>>? Root { get; set; }
    private static Task last = Task.CompletedTask;

    public static Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        var shown = After(last, dialog);
        last = shown;
        return shown;

        static async Task<ContentDialogResult> After(Task before, ContentDialog dialog)
        {
            try { await before; } catch (Exception) { }
            dialog.XamlRoot = await Root!();
            return await dialog.ShowAsync();
        }
    }

    /// <summary>A question with the action and Cancel, Cancel the default: what's asked here can't be undone.</summary>
    public static async Task<bool> Confirm(string title, string message, string confirm, string cancel = "Cancel") => await Show(new ContentDialog
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        PrimaryButtonText = confirm,
        CloseButtonText = cancel,
        DefaultButton = ContentDialogButton.Close,
        Style = Ui.Style("DefaultContentDialogStyle"),
    }) == ContentDialogResult.Primary;
}
