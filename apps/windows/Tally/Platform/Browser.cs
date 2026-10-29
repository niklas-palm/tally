using Microsoft.Win32;

namespace Tally;

/// <summary>
/// Signing in in the owner's own browser (IBrowser), where they're probably signed in to the provider already. It
/// sends the browser back to <c>tally://callback</c>; Windows starts Tally with that URL, the new process hands it to
/// the running one (Program), and <see cref="Callback"/> finishes the sign-in. Registering the scheme is per user and
/// done at every launch, so it always points at this copy of Tally.
/// </summary>
internal sealed class Browser : IBrowser
{
    private TaskCompletionSource<Uri?>? waiting;

    public static void RegisterScheme()
    {
        var exe = Environment.ProcessPath!;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\tally");
        key.SetValue("", "URL:Tally");
        key.SetValue("URL Protocol", "");
        using var icon = key.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{exe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
    }

    public Task<Uri?> Authorize(Uri url)
    {
        waiting?.TrySetResult(null);  // a second Sign In replaces the first, as a new browser tab would
        waiting = new TaskCompletionSource<Uri?>();
        Shell.Launch(url.AbsoluteUri);  // by Explorer, so a browser it starts isn't left out of the recording
        return waiting.Task;
    }

    /// <summary>The browser came back: called with every tally://callback Windows hands Tally.</summary>
    public void Callback(Uri url) => waiting?.TrySetResult(url);
}
