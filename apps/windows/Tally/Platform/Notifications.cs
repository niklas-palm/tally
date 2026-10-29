using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Tally;

/// <summary>
/// Windows' notifications, through the Windows App SDK, which works for an app that isn't packaged
/// (INotifications). Clicking one about a meeting opens it: Windows hands the click to Tally, running or not. There's
/// no permission to ask for: the owner turns them off in Settings, if they want them off.
/// </summary>
internal sealed class Notifications : INotifications
{
    private bool registered;

    /// <summary>Raised on the UI thread when a notification about a meeting is clicked.</summary>
    public event Action<string>? MeetingClicked;

    /// <summary>Called on the UI thread: Windows raises clicks on a thread of its own, and they're passed back to it.</summary>
    public void Register()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        AppNotificationManager.Default.NotificationInvoked += (_, e) =>
        {
            if (e.Arguments.TryGetValue("meeting", out var meeting)) queue.TryEnqueue(() => MeetingClicked?.Invoke(meeting));
        };
        try { AppNotificationManager.Default.Register(); registered = true; }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { }  // no notifications: everything still shows in the app
    }

    public void Post(string id, string title, string body, string? meeting)
    {
        if (!registered) return;
        var builder = new AppNotificationBuilder().AddText(title).AddText(body).SetTag(id);
        if (meeting is not null) builder.AddArgument("meeting", meeting);
        // The notification service can be off (some managed PCs): then nothing shows, and the caller carries on
        try { AppNotificationManager.Default.Show(builder.BuildNotification()); }
        catch (COMException) { }
    }

    public void Unregister()
    {
        if (registered) AppNotificationManager.Default.Unregister();
    }
}
