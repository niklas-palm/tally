using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Tally;

/// <summary>
/// Tally lives in the notification area (record, stop, what's on its way) with one window for meetings and people,
/// as it lives in the Mac's menu bar. Closing the window keeps Tally running there; Quit, in its flyout or its
/// menu, ends it, keeping any recording.
/// </summary>
public partial class App : Application
{
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tally");
    // Made in OnLaunched, before anything uses them
    private AppModel model = null!;
    private TrayIcon tray = null!;
    private Panel flyout = null!, drawer = null!;
    private DrawerPanel drawerPanel = null!;
    private MainWindow? window;
    private readonly Browser browser = new();
    private readonly Notifications notifications = new();
    private readonly CrashReports crashes = new(Path.Combine(DataFolder, "Crashes"));

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => crashes.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) crashes.Write(error); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Browser.RegisterScheme();
        notifications.MeetingClicked += OpenWindow;
        notifications.Register();
        var logs = Path.Combine(DataFolder, "Logs");
        var platform = new Platform(new WindowsAudio(), browser, new Secrets(), notifications, new ClipPlayer(), new LoginItem(),
            new Shell(() => Window), new Installer(logs), crashes, new Log(logs), DataFolder, Path.Combine(DataFolder, "Cache"));
        model = new AppModel(Config.Load(Path.Combine(AppContext.BaseDirectory, "tally.json")), platform);

        tray = new TrayIcon();
        var menu = new MenuPanel(model, OpenWindow, Quit);
        flyout = new Panel(menu, 360, takesFocus: true);
        menu.Redrawn += () => flyout.Refit();
        menu.Done += () => flyout.Hide();
        tray.Clicked += anchor =>
        {
            if (flyout.IsOpen) { flyout.Hide(); return; }
            drawer.Hide();
            flyout.Show(anchor);
            _ = model.Sync();  // up to date when looked at, not at the next tick
            _ = model.CheckForUpdate();
        };
        tray.MenuRequested += ShowMenu;

        drawerPanel = new DrawerPanel(model);
        drawer = new Panel(drawerPanel, 340, takesFocus: false);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.Drawer)) ShowDrawer();
            if (e.PropertyName is nameof(AppModel.Recorder) or nameof(AppModel.CallMode) or nameof(AppModel.Update) or nameof(AppModel.StoppedByCall)) ShowState();
        };
        ShowState();
        Program.Activated += Activated;
        model.Start();

        var started = Environment.GetCommandLineArgs();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs clicked
            && clicked.Arguments.TryGetValue("meeting", out var meeting))
            OpenWindow(meeting);
        // For screenshots (docs/windows.md): the flyout, open, as a click leaves it
        else if (started.Contains("--menu")) flyout.Show(tray.Bounds);
        // Opened at login, Tally waits in the notification area; opened from Start, it shows its window
        else if (!started.Contains("--background") || !model.Welcomed) OpenWindow(null);
    }

    /// <summary>A later launch: the sign-in coming back, a meeting to open, or Tally opened again.</summary>
    private void Activated(AppActivationArguments activation)
    {
        if (activation.Kind == ExtendedActivationKind.AppNotification) return;  // NotificationInvoked has it
        switch (Program.TallyUrl(activation))
        {
            case { Host: "callback" } url: browser.Callback(url); break;
            case { Host: "meeting" } url: OpenWindow(url.AbsolutePath.Trim('/')); break;
            default: OpenWindow(null); break;
        }
    }

    private void ShowState()
    {
        var recording = model.Recorder is not null && model.StoppedByCall is null;
        var tip = recording ? "Tally is recording" : model.CallMode == CallMode.Auto ? "Tally, recording calls automatically"
            : model.Update is not null ? "Tally: an update is ready" : "Tally";
        tray.Show(new TrayIcon.State(model.CallMode == CallMode.Auto ? MarkIcon.Dot.Ring : MarkIcon.Dot.Solid, recording, model.Update is not null, tip));
    }

    private void ShowDrawer()
    {
        if (model.Drawer is not { } prompt) { drawer.Hide(); return; }
        if (flyout.IsOpen) return;  // the owner is in the flyout already, which says the same
        drawerPanel.Draw(prompt);
        drawer.Show(tray.Bounds);
    }

    private void ShowMenu(Native.POINT at)
    {
        var recording = model.Recorder is not null;
        var chosen = tray.ShowMenu(at, [
            (1, recording ? "Stop Recording" : "Start Recording", !model.Starting),
            (0, null, true),
            (2, "Open Tally", true),
            (3, recording ? "Stop and Quit" : "Quit Tally", true),
        ]);
        switch (chosen)
        {
            case 1: _ = model.ToggleRecording(); break;
            case 2: OpenWindow(null); break;
            case 3: Quit(); break;
        }
    }

    private MainWindow Window => window ??= new MainWindow(model);  // made the first time it's needed, then kept

    private void OpenWindow(string? meeting)
    {
        if (meeting is not null) model.Open(meeting);
        Window.Present();
    }

    private void Quit()
    {
        model.EndRecording();  // the file is closed at once; the next launch saves and sends it, as after a crash
        tray.Dispose();
        notifications.Unregister();
        Exit();
    }
}
