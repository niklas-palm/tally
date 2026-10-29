using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Tally;

/// <summary>
/// The flyout from Tally's icon in the notification area, the Mac's menu bar panel. Idle: one red button to start,
/// what needs saying, the latest meetings, and the settings at its foot. Recording: the time, what it's recording, the
/// two levels, stop or discard. Stopped by a call's end: carry on, or send now.
/// </summary>
internal sealed class MenuPanel : Grid
{
    private readonly AppModel model;
    private readonly Action<string?> openWindow;
    private readonly Action quit;
    private (TextBlock Timer, ProgressBar Mic, ProgressBar System, TextBlock LostMic, TextBlock LostSystem)? live;  // while recording

    /// <summary>Raised after it drew itself again, for the panel to fit its new height.</summary>
    public event Action? Redrawn;

    /// <summary>Raised when something in it means the flyout should go (a meeting opened, Quit).</summary>
    public event Action? Done;

    public MenuPanel(AppModel model, Action<string?> openWindow, Action quit)
    {
        this.model = model;
        this.openWindow = openWindow;
        this.quit = quit;
        Padding = new Thickness(16);
        model.PropertyChanged += Changed;
        model.Auth.PropertyChanged += (_, _) => Draw();
        Draw();
    }

    // The levels and the time change every second: they're updated in place, the rest is drawn again
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Elapsed)) Tick();
        else if (e.PropertyName is not (nameof(AppModel.Selection) or nameof(AppModel.Query) or nameof(AppModel.Drafts) or nameof(AppModel.Pending))) Draw();
    }

    private void Tick()
    {
        if (live is not { } shown || model.Recorder is not { } recorder) return;
        shown.Timer.Text = model.Elapsed;
        var levels = recorder.Levels;
        shown.Mic.Value = Meter(levels.Mic);
        shown.System.Value = Meter(levels.System);
        shown.LostMic.Visibility = levels.Lost ? Visibility.Visible : Visibility.Collapsed;
        // Nothing at all is recorded without the mic, which says so; otherwise the other side of a call may be missing
        shown.LostSystem.Visibility = levels.SystemLost && !levels.Lost ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double Meter(float dB) => Math.Clamp((dB + 60) / 60, 0, 1) * 100;

    private void Draw()
    {
        Children.Clear();
        live = null;
        var stack = new StackPanel { Spacing = 14 };

        if (model.StoppedByCall is { } stopped && model.Recorder is not null) stack.Children.Add(StoppedByCall(stopped.App));
        else if (model.Recorder is not null && model.Recording is not null) stack.Children.Add(RecordingPanel());
        else
        {
            var start = Ui.RedButton("Start Recording", Ui.RecordGlyph, () => _ = model.StartRecording());
            start.HorizontalAlignment = HorizontalAlignment.Stretch;
            start.Padding = new Thickness(12, 9, 12, 9);
            start.FontSize = 15;
            start.IsEnabled = !model.Starting;
            stack.Children.Add(start);
        }
        foreach (var notice in Notices.For(model, inWindow: false)) stack.Children.Add(notice);

        var recent = model.Items.Where(i => i.Status.Kind != StatusKind.Recording).Take(4).ToList();
        if (recent.Count > 0)
        {
            stack.Children.Add(Ui.Divider());
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(-8, 0, -8, 0) };
            foreach (var item in recent) list.Children.Add(MenuRow(MeetingRow.Build(item, model.ToConfirm(item.Id)), () => Show(item.Id)));
            stack.Children.Add(list);
        }
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(Settings());
        stack.Children.Add(Footer());
        Children.Add(stack);
        Redrawn?.Invoke();
    }

    private UIElement RecordingPanel()
    {
        var timer = new TextBlock { Text = model.Elapsed, FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display") };
        var dot = Ui.Icon(Ui.DotGlyph, 14, new SolidColorBrush(Ui.Red));
        dot.VerticalAlignment = VerticalAlignment.Center;
        var what = model.RecordingCall is { } app ? app.Length == 0 ? "Recording the call" : $"Recording the {app} call" : "Recording in the room";
        var micLevel = new ProgressBar { Maximum = 100, Foreground = new SolidColorBrush(Ui.Green) };
        var systemLevel = new ProgressBar { Maximum = 100, Foreground = new SolidColorBrush(Ui.Green) };
        var lostMic = Ui.Caption(Notices.LostMic, new SolidColorBrush(Ui.Orange));
        var lostSystem = Ui.Caption("Tally can't record what your PC plays, so the others in a call aren't being recorded. It keeps trying.", new SolidColorBrush(Ui.Orange));
        lostMic.Visibility = lostSystem.Visibility = Visibility.Collapsed;
        var stop = Ui.Button("Stop Recording", () => _ = model.StopRecording(), accent: true, glyph: Ui.StopGlyph);
        stop.HorizontalAlignment = HorizontalAlignment.Stretch;
        var discard = Ui.Button(model.DiscardArmed ? "Click Again to Discard" : "Discard", model.DiscardRecording);
        if (model.DiscardArmed) discard.Foreground = new SolidColorBrush(Ui.Red);
        var buttons = Ui.Grid("*,Auto", spacing: 8).Add(stop, discard.At(1));
        var view = Ui.Column(12,
            Ui.Column(2, Ui.Row(10, dot, timer), Ui.Text(what, brush: Ui.Secondary)),
            Ui.Column(6, Level("Microphone", micLevel), Level("System audio", systemLevel), lostMic, lostSystem),
            buttons);
        live = (timer, micLevel, systemLevel, lostMic, lostSystem);
        Tick();
        return view;
    }

    private static UIElement Level(string label, ProgressBar bar)
    {
        bar.VerticalAlignment = VerticalAlignment.Center;
        Ui.Name(bar, $"{label} level");
        return Ui.Grid("92,*", spacing: 8).Add(Ui.Caption(label), bar.At(1));
    }

    private UIElement StoppedByCall(string? app)
    {
        var resume = Ui.RedButton("Continue Recording", Ui.RecordGlyph, () => _ = model.ContinueRecording());
        return Ui.Column(10,
            Ui.Row(8, Ui.Icon(Ui.StopGlyph, 16), Ui.Text(app is null ? "Stopped when the call ended" : $"Stopped when the {app} call ended", "BodyStrongTextBlockStyle")),
            Ui.Text("Still in the call? Continue, and it stays one recording. Otherwise it's sent in 30 seconds.", brush: Ui.Secondary),
            Ui.Row(8, resume, Ui.Button("Send Now", () => _ = model.StopRecording())));
    }

    private UIElement Settings()
    {
        var stack = new StackPanel { Spacing = 10 };
        var login = new CheckBox { Content = "Open at login", IsChecked = model.OpensAtLogin, MinHeight = 0 };
        login.Click += (_, _) => model.OpensAtLogin = login.IsChecked == true;
        stack.Children.Add(login);

        // A copy of the meetings in a folder, for agents on this PC: the signed-in person's own
        if (model.Auth.State == AuthState.SignedIn)
        {
            if (model.MeetingsFolder is { } folder)
            {
                var menu = new MenuFlyout();
                menu.Items.Add(Ui.Item("Show in File Explorer", () => { model.Platform.Shell.Open(folder); Done?.Invoke(); }));
                menu.Items.Add(Ui.Item("Change Folder…", () => { Done?.Invoke(); _ = model.ChooseMeetingsFolder(); }));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Ui.Item("Stop Keeping a Copy", () => model.MeetingsFolder = null));  // what's there stays: it's the owner's
                var button = new DropDownButton
                {
                    Content = Ui.Row(8, Ui.Icon(Ui.FolderGlyph, 14), new TextBlock { Text = $"Meetings copied to {Path.GetFileName(folder.TrimEnd('\\'))}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 }),
                    Flyout = menu,
                };
                ToolTipService.SetToolTip(button, folder);
                stack.Children.Add(button);
            }
            else
            {
                var keep = Ui.Link("Keep a Copy of My Meetings in a Folder…", () => { Done?.Invoke(); _ = model.ChooseMeetingsFolder(); });
                keep.Margin = new Thickness(-4, 0, 0, 0);
                ToolTipService.SetToolTip(keep, "Summaries, transcripts and files as Markdown and JSON, kept up to date: for you and the agents on your PC");
                stack.Children.Add(keep);
            }
        }

        // The question as the label, what happens as the options, and a sentence for the one chosen
        var modes = new ComboBox { Header = "When a call starts", MinWidth = 200 };
        foreach (var mode in Enum.GetValues<CallMode>()) modes.Items.Add(new ComboBoxItem { Content = mode.Title(), Tag = mode });
        modes.SelectedIndex = (int)model.CallMode;
        modes.SelectionChanged += (_, _) => { if (modes.SelectedItem is ComboBoxItem { Tag: CallMode mode }) model.CallMode = mode; };
        stack.Children.Add(Ui.Column(4, modes, Ui.Caption(model.CallMode.Explanation())));
        return stack;
    }

    private UIElement Footer()
    {
        var grid = Ui.Grid("Auto,*,Auto,Auto", spacing: 4).With(g => g.Margin = new Thickness(-4, 0, -4, 0));
        grid.Children.Add(Ui.Link("Open Tally", () => Show(null)));
        if (model.Auth.State == AuthState.SignedIn)
        {
            var menu = new MenuFlyout();
            if (model.Auth.Account is { } account) menu.Items.Add(new MenuFlyoutItem { Text = account, IsEnabled = false });
            menu.Items.Add(Ui.Item("Sign Out", model.SignOut));
            var accountButton = new HyperlinkButton { Content = "Account", Padding = new Thickness(4, 2, 4, 2) };
            accountButton.Click += (_, _) => menu.ShowAt(accountButton);
            grid.Children.Add(accountButton.At(2));
        }
        grid.Children.Add(Ui.Link(model.Recorder is null ? "Quit Tally" : "Stop and Quit", () => { Done?.Invoke(); quit(); }).At(3));
        return grid;
    }

    /// <summary>A row that highlights under the pointer, as a menu item does.</summary>
    private static Button MenuRow(UIElement content, Action click)
    {
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        button.Resources["ButtonBackgroundPointerOver"] = Ui.Theme("SubtleFillColorSecondaryBrush");
        button.Resources["ButtonBackgroundPressed"] = Ui.Theme("SubtleFillColorTertiaryBrush");
        button.Click += (_, _) => click();
        return button;
    }

    private void Show(string? meeting)
    {
        Done?.Invoke();
        openWindow(meeting);
    }
}

/// <summary>One meeting in a list: its title or when it was recorded, and where it is.</summary>
internal static class MeetingRow
{
    /// <summary>The flyout's row: an icon for where it is, the title, when and how long, and the count to confirm.</summary>
    public static UIElement Build(Item item, int toConfirm)
    {
        var local = item.StartedAt.ToLocalTime();
        var when = Format.When(local.DateTime, DateTime.Today);
        var title = Ui.Text(item.Title ?? $"Recording at {local:t}", wrap: false);
        var subtitle = Ui.Caption(Detail(item, when, null), item.SendError is null ? null : new SolidColorBrush(Ui.Orange)).With(b => { b.TextWrapping = TextWrapping.NoWrap; b.TextTrimming = TextTrimming.CharacterEllipsis; });
        var status = StatusIcon(item.Status);
        status.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Grid("20,*,Auto", spacing: 10).Add(status, Ui.Column(1, title, subtitle).At(1),
            toConfirm > 0 ? Ui.Badge(toConfirm, AppModel.ToConfirmText(toConfirm)).At(2) : null);
    }

    /// <summary>A meeting's second line: once ready, when, how long and its folders (each if given); before, its status and
    /// length; or why it failed or couldn't be sent.</summary>
    public static string Detail(Item item, string? when, string? folders)
    {
        if (item.SendError is { } error) return error;
        var length = item.Duration is { } d ? Format.Duration(d) : null;
        return item.Status.Kind switch
        {
            StatusKind.Ready => string.Join(" · ", new[] { when, length, folders }.Where(s => !string.IsNullOrEmpty(s))),
            StatusKind.Failed => item.Status.Error ?? "",
            _ => string.Join(" · ", new[] { Format.StatusText(item.Status), length }.OfType<string>()),
        };
    }

    public static FrameworkElement StatusIcon(Status status)
    {
        FrameworkElement icon = status.Kind switch
        {
            StatusKind.Ready => Ui.Icon(Ui.DocumentGlyph, 16, Ui.Secondary),
            StatusKind.Failed => Ui.Icon(Ui.WarningGlyph, 16, new SolidColorBrush(Ui.Orange)),
            StatusKind.Recording => Ui.Icon(Ui.DotGlyph, 14, new SolidColorBrush(Ui.Red)),
            StatusKind.Waiting => Ui.Icon(Ui.CloudGlyph, 16, Ui.Secondary),  // waiting to go up
            _ => new ProgressRing { IsActive = true, Width = 16, Height = 16 },
        };
        Ui.Name(icon, Format.StatusText(status));
        return icon;
    }
}
