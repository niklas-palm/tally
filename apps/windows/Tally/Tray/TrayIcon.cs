using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tally;

/// <summary>
/// Tally's icon in the notification area, the Windows counterpart of the Mac's menu bar extra: a click opens the
/// flyout, a right-click a plain menu (start or stop, open the window, quit), as Control-click does on the Mac.
/// It draws the mark itself in the taskbar's colour and at its DPI, and redraws when either changes or Explorer
/// restarts (which forgets every icon).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private readonly Native.WndProc proc;  // kept: the window calls it for as long as it lives
    private readonly IntPtr window;
    private readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private IntPtr icon;
    private bool added;
    private State state = new(MarkIcon.Dot.Solid, false, false, "Tally");

    public readonly record struct State(MarkIcon.Dot Dot, bool Red, bool Badge, string Tip);

    /// <summary>A click (or Enter on it), with where the icon is on the screen, in pixels.</summary>
    public event Action<Native.RECT>? Clicked;

    /// <summary>A right-click: the app shows its menu at the pointer.</summary>
    public event Action<Native.POINT>? MenuRequested;

    public TrayIcon()
    {
        proc = Handle;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "TallyTrayIcon",
        };
        Native.RegisterClassEx(ref wc);
        // a hidden top-level window, not a message-only one: only top-level windows hear the broadcasts above
        window = Native.CreateWindowEx(0, wc.lpszClassName, "Tally", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Add();
    }

    public void Show(State next)
    {
        if (next == state) return;
        state = next;
        Redraw();
    }

    /// <summary>The icon's place on the screen, for the flyout and the drawer to stand on. A new app's icon waits in the
    /// taskbar's overflow (^), where the shell can't say: then the corner by the clock, where the overflow opens.</summary>
    public Native.RECT Bounds
    {
        get
        {
            var id = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = window, uID = 1 };
            if (Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Right > rect.Left) return rect;
            var monitor = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);  // the primary display, where the taskbar's clock is
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(monitor, ref info);
            return new Native.RECT { Left = info.rcWork.Right - 1, Right = info.rcWork.Right, Top = info.rcWork.Bottom, Bottom = info.rcWork.Bottom + 1 };
        }
    }

    /// <summary>Shows a native menu at the point and returns the chosen item's id, or 0.</summary>
    public int ShowMenu(Native.POINT at, IEnumerable<(int Id, string? Text, bool Enabled)> items)
    {
        var menu = Native.CreatePopupMenu();
        foreach (var (id, text, enabled) in items)
            Native.AppendMenu(menu, text is null ? Native.MF_SEPARATOR : Native.MF_STRING | (enabled ? 0 : Native.MF_GRAYED), (UIntPtr)id, text);
        Native.SetForegroundWindow(window);  // without it, the menu doesn't close when you click elsewhere
        var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, at.X, at.Y, window, IntPtr.Zero);
        Native.DestroyMenu(menu);
        return chosen;
    }

    private Native.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = state.Tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        Draw();
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private void Redraw()
    {
        Draw();
        var data = Data(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (added) Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    private void Draw()
    {
        var old = icon;
        var size = (int)Math.Round(16 * Native.GetDpiForSystem() / 96.0);
        icon = MarkIcon.Create(size, LightTaskbar(), state.Dot, state.Red, state.Badge, Accent());
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    private static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    private static uint Accent()
    {
        // the accent as Explorer draws it, ABGR in the registry
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is not int abgr) return 0xFF0067C0;
        var v = unchecked((uint)abgr);
        return 0xFF000000 | ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF);
    }

    private IntPtr Handle(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch (Native.LowWord(lParam))
            {
                case Native.NIN_SELECT or Native.NIN_KEYSELECT:
                    Clicked?.Invoke(Bounds);
                    break;
                case Native.WM_CONTEXTMENU:
                    MenuRequested?.Invoke(new Native.POINT { X = Native.LowWord(wParam), Y = Native.HighWord(wParam) });
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated) Add();
        else if (msg is Native.WM_SETTINGCHANGE or Native.WM_DPICHANGED) Redraw();  // light or dark taskbar, accent, scale
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        var data = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        Native.DestroyWindow(window);
    }
}
