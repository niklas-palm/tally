using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Tally;

/// <summary>
/// One Tally at a time, as on the Mac. A second launch (the provider's tally://callback, a notification clicked, Tally
/// opened again from Start) hands what it was started with to the running one and goes.
/// </summary>
public static class Program
{
    /// <summary>Raised on the UI thread with what a later launch was started with.</summary>
    public static event Action<AppActivationArguments>? Activated;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var key = AppInstance.FindOrRegisterForKey("Tally");
        if (!key.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // off this thread: the STA thread mustn't block on the redirect it starts
            Task.Run(() => key.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }
        Application.Start(started =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            key.Activated += (_, e) => queue.TryEnqueue(() => Activated?.Invoke(e));
            _ = new App();
        });
        return 0;
    }

    /// <summary>A tally:// URL among what Tally was started with: the sign-in's callback, or a meeting to open.</summary>
    public static Uri? TallyUrl(AppActivationArguments activation) => activation.Data switch
    {
        Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri,
        Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => Parse(launch.Arguments),
        _ => null,
    };

    /// <summary>The first tally:// argument, if it's a valid URL: a web page's link can put anything after tally://.</summary>
    private static Uri? Parse(string? arguments) =>
        arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim('"'))
            .FirstOrDefault(a => a.StartsWith("tally://", StringComparison.OrdinalIgnoreCase)) is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
}
