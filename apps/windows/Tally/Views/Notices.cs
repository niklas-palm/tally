using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Tally;

/// <summary>
/// What needs saying, a notice each, in the flyout and under the window's title bar alike: an icon, one
/// sentence, and the buttons that answer it (under the sentence in the narrow flyout; at the end of the line in the
/// window). A page that asks to sign in itself isn't asked for twice.
/// </summary>
internal static class Notices
{
    /// <summary>Said while recording, in the flyout and on the recording's page.</summary>
    public const string LostMic = "No microphone is connected, so nothing is being recorded. Connect one and recording carries on.";

    public static IEnumerable<UIElement> For(AppModel model, bool inWindow, bool asksToSignIn = true)
    {
        var shell = model.Platform.Shell;
        if (model.Problem is { } problem)
            yield return Notice(inWindow, Ui.ErrorGlyph, Ui.Orange, problem.Message,
                problem.Settings is { } settings ? Ui.Button("Open Settings", () => shell.Open(settings)) : null,
                Ui.Button("Dismiss", () => model.Problem = null));
        if (model.FolderProblem is { } folderProblem)
            yield return Notice(inWindow, Ui.FolderGlyph, Ui.Orange, folderProblem, Ui.Button("Change Folder…", () => _ = model.ChooseMeetingsFolder()));
        if (model.Update is not null)
        {
            var text = model.Updating ? "Updating Tally: it reopens as the new version in a minute or two."
                : model.RecordingOrSaving ? "A new version of Tally is ready. Update after this recording." : "A new version of Tally is ready.";
            UIElement? action = model.Updating ? new ProgressRing { Width = 16, Height = 16, IsActive = true }
                : !model.RecordingOrSaving ? Ui.Button("Update", () => _ = model.InstallUpdate(), accent: true) : null;
            yield return Notice(inWindow, Ui.DownloadGlyph, null, text, action);
        }
        if (model.Crash is not null)
        {
            var signedIn = model.Auth.State == AuthState.SignedIn;
            var send = Ui.Button(model.SendingCrash ? "Sending…" : "Send Report", () => _ = model.SendCrash(), accent: true);
            send.IsEnabled = signedIn && !model.SendingCrash;
            yield return Notice(inWindow, Ui.WarningGlyph, Ui.Orange, signedIn
                    ? "Tally quit unexpectedly last time. Send the report so it can be fixed: it holds no meeting content."
                    : "Tally quit unexpectedly last time. Sign in to send the report so it can be fixed.",
                Ui.Button("Don't Send", model.DismissCrash), send);
        }
        if (model.Auth.State != AuthState.SignedIn)
        {
            if (asksToSignIn) yield return Notice(inWindow, Ui.PersonGlyph, null, SignInExplanation(model), SignInButton(model));
        }
        else if (model.Offline) yield return Notice(inWindow, Ui.OfflineGlyph, null, "You're offline. Tally carries on when you're back online.");
        else if (model.SyncError is { } error) yield return Notice(inWindow, Ui.WarningGlyph, null, $"Tally couldn't update your meetings. {error}");
    }

    /// <summary>Why to sign in, now: shared with the window's pages.</summary>
    public static string SignInExplanation(AppModel model)
    {
        var waiting = model.WaitingForSignIn;
        // A sign-in abandoned in the browser never comes back: Sign In again starts afresh
        if (model.Auth.State == AuthState.SigningIn) return "Your browser is open to sign in. Closed it? Sign in again.";
        if (model.Auth.Expired) return "Your sign-in has expired. Sign in again so Tally can keep summarising your meetings.";
        if (waiting > 0) return waiting == 1 ? "1 recording is waiting to be sent. Sign in to have it summarised." : $"{waiting} recordings are waiting to be sent. Sign in to have them summarised.";
        return "Recording works without signing in. Sign in to have your meetings summarised.";
    }

    /// <summary>Sign In to …, here and on a page that asks itself: the welcome, a recording waiting to be sent.</summary>
    public static Button SignInButton(AppModel model) => Ui.Button($"Sign In to {model.SignInName}", () => _ = model.SignIn(), accent: true);

    private static UIElement Notice(bool inWindow, string glyph, Windows.UI.Color? tint, string text, params UIElement?[] actions)
    {
        var icon = Ui.Icon(glyph, 16, tint is { } t ? new SolidColorBrush(t) : Ui.Secondary);
        var buttons = Ui.Row(8, actions);
        if (inWindow)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            return Ui.Grid("Auto,*,Auto", spacing: 12).With(g => g.Padding = new Thickness(16, 8, 16, 8)).Add(icon,
                Ui.Text(text).With(b => { b.MaxLines = 2; b.VerticalAlignment = VerticalAlignment.Center; }).At(1), buttons.At(2));
        }
        icon.Margin = new Thickness(0, 2, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        return Ui.Grid("Auto,*", spacing: 10).Add(icon, Ui.Column(8, Ui.Text(text), buttons.Children.Count > 0 ? buttons : null).At(1));
    }
}
