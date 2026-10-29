using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Tally;

/// <summary>
/// A panel that stands on the notification area, as Windows 11's own flyouts do (Quick Settings, the volume mixer):
/// the flyout under Tally's icon, and the drawer that offers to record a call. A borderless, always-on-top
/// window in acrylic with rounded corners, sized to its content, never in Alt-Tab or the taskbar, left out of screen
/// captures, and gone when it loses focus (the flyout) or when it's told to (the drawer, which never takes focus
/// from the call app).
/// </summary>
internal sealed class Panel : Window
{
    private const int Margin = 12;  // from the taskbar and the screen's edge, as Windows' own flyouts stand
    private readonly bool takesFocus;
    private readonly FrameworkElement content;
    private bool open, focused;

    public Panel(FrameworkElement content, double width, bool takesFocus)
    {
        this.content = content;
        this.takesFocus = takesFocus;
        content.Width = width;
        content.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) };
        content.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
        Content = content;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var hwnd = WindowNative.GetWindowHandle(this);
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // a screen share shows no sign of Tally: Windows leaves the window out of every capture, except
        // for screenshots of Tally itself, with TALLY_SHOW_IN_CAPTURES=1
        if (Environment.GetEnvironmentVariable("TALLY_SHOW_IN_CAPTURES") != "1") Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);

        Activated += (_, e) =>
        {
            // it goes when it loses the focus it had; Windows may not give it focus at all (a launch it didn't start
            // from a click), and then it stays until it's closed another way
            if (e.WindowActivationState != WindowActivationState.Deactivated) focused = open;
            else if (this.takesFocus && focused) Hide();
        };
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); };  // Alt+F4 closes it as a flyout closes: hidden, kept
    }

    public bool IsOpen => open;

    /// <summary>Shows it standing on the taskbar by the icon at <paramref name="anchor"/> (screen pixels), sliding in.</summary>
    public void Show(Native.RECT anchor)
    {
        var center = new Native.POINT { X = (anchor.Left + anchor.Right) / 2, Y = (anchor.Top + anchor.Bottom) / 2 };
        var monitor = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpi, out _);
        var scale = dpi / 96.0;

        var width = (int)Math.Ceiling(content.Width * scale);
        var work = info.rcWork;
        var margin = (int)(Margin * scale);

        // Text can only be measured once the window is up: it's shown first where nobody sees it, invisible, then
        // fitted to its content once that's laid out, put in its place, and faded in
        content.Opacity = 0;
        content.Translation = new System.Numerics.Vector3(0, 12, 0);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, width, 400));
        if (takesFocus) Activate();
        else AppWindow.Show(false);  // the drawer: shown without taking focus from the call
        open = true;
        if (content.XamlRoot is null) content.Loaded += Place;  // the first time: it isn't laid out yet
        else Place(null, null);

        void Place(object? sender, RoutedEventArgs? e)
        {
            content.Loaded -= Place;
            content.UpdateLayout();
            content.Measure(new Windows.Foundation.Size(content.Width, double.PositiveInfinity));
            var (outerWidth, outerHeight) = Outer(scale);
            // Windows 11's taskbar is always at the bottom: the panel stands on it, by the icon, kept on screen
            var x = Math.Clamp(center.X - outerWidth / 2, work.Left + margin, work.Right - outerWidth - margin);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, work.Bottom - outerHeight - margin, outerWidth, outerHeight));
            content.Opacity = 1;
            content.Translation = System.Numerics.Vector3.Zero;
        }
    }

    /// <summary>Fits the window to the content again, after it changed (a notice came, recording started).</summary>
    public void Refit()
    {
        if (!open || content.XamlRoot is null) return;
        var scale = content.XamlRoot.RasterizationScale;
        content.Measure(new Windows.Foundation.Size(content.Width, double.PositiveInfinity));
        var bottom = AppWindow.Position.Y + AppWindow.Size.Height;  // it grows upwards from the taskbar, as it stands on it
        var (outerWidth, outerHeight) = Outer(scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(AppWindow.Position.X, bottom - outerHeight, outerWidth, outerHeight));
    }

    /// <summary>The window size that fits the content: the content plus the window's frame, measured rather than assumed.</summary>
    private (int Width, int Height) Outer(double scale)
    {
        var inside = content.XamlRoot.Size;
        var frameWidth = AppWindow.Size.Width - (int)Math.Round(inside.Width * scale);
        var frameHeight = AppWindow.Size.Height - (int)Math.Round(inside.Height * scale);
        return ((int)Math.Ceiling(content.Width * scale) + frameWidth, (int)Math.Ceiling(content.DesiredSize.Height * scale) + frameHeight);
    }

    public void Hide()
    {
        if (!open) return;
        open = focused = false;
        AppWindow.Hide();
    }
}
