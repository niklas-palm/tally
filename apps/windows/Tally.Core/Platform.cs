namespace Tally;

/// <summary>
/// What Tally needs from Windows: the app implements it, the tests fake it. Everything is called on the UI thread
/// except Encode and Audio.Users.
/// </summary>
/// <param name="DataFolder">Tally's own folder (%LOCALAPPDATA%\Tally): recordings, settings, the meetings folder's state.</param>
/// <param name="CacheFolder">A folder the system may clear (%LOCALAPPDATA%\Tally\Cache): copies of meetings' files.</param>
public sealed record Platform(
    IAudio Audio,
    IBrowser Browser,
    ISecrets Secrets,
    INotifications Notifications,
    IClipPlayer Player,
    ILoginItem LoginItem,
    IShell Shell,
    IInstaller Installer,
    ICrashReports Crashes,
    ILog Log,
    string DataFolder,
    string CacheFolder);

/// <summary>The microphone, the recorder, the encoder and who else is using audio.</summary>
public interface IAudio
{
    /// <summary>Starts recording the default microphone (channel 1) and everything the PC plays except Tally (channel 2) into a
    /// 16-bit PCM, 2-channel, 48 kHz WAV at <paramref name="path"/>, written so a crash leaves it readable. Throws
    /// UnauthorizedAccessException when Windows refuses the microphone, or another error with a message a person can
    /// read, having let go of whatever it started.</summary>
    Task<IRecorder> Start(string path);

    /// <summary>Encodes a recording's WAV to the 2-channel AAC m4a (128 kbps) that is uploaded. Throws NotSupportedException
    /// when Windows has no AAC encoder (an N edition without its Media Feature Pack), and another error if it can't
    /// otherwise. Called on a background thread: an hour takes a while.</summary>
    void Encode(string wav, string m4a);

    /// <summary>Every other process using audio now: Tally's own never counts. Called on a background thread, once a
    /// second.</summary>
    IReadOnlyList<AudioUser> Users();
}

/// <summary>A recording in progress.</summary>
public interface IRecorder
{
    /// <summary>The live levels, read once a second for the menu and the window.</summary>
    RecorderLevels Levels { get; }

    /// <summary>Stops listening without closing the file: a recording stopped by a call's end can still carry on in it.</summary>
    void Pause();

    /// <summary>Listens again, into the same file; throws, with a message a person can read, if it can't.</summary>
    Task Resume();

    /// <summary>Stops and closes the file. Called once; also on quitting, so the file is whole at the next launch.</summary>
    void Stop();
}

/// <summary>The levels of the last buffer, in dBFS (-120 is silence).</summary>
/// <param name="Lost">The microphone went, and no other is there to record with.</param>
/// <param name="Failed">Why the recorder stopped by itself (its file couldn't be written, or it reached its longest), a
/// sentence for the owner: the recording ends, and the app says what became of it.</param>
/// <param name="SystemLost">What the PC plays isn't being recorded: its capture stopped or couldn't be opened, and is
/// being tried again.</param>
public sealed record RecorderLevels(float Mic = -120, float System = -120, bool Lost = false, string? Failed = null, bool SystemLost = false);

/// <summary>A process using audio: whether it takes the microphone's input, whether it's playing, and what to call it
/// ("Zoom", "Teams": its app's name, also for a helper process), or null when there's no telling.</summary>
public sealed record AudioUser(int Pid, bool Input, bool Output, string? Name = null);

/// <summary>Signing in in the system's browser.</summary>
public interface IBrowser
{
    /// <summary>Opens the authorize URL and returns the <c>tally://callback?...</c> URL it comes back with, or null when the
    /// owner cancelled. Throws, with a message a person can read, if the browser couldn't open.</summary>
    Task<Uri?> Authorize(Uri url);
}

/// <summary>A few bytes kept safe between launches (the Credential Manager): the refresh token.</summary>
public interface ISecrets
{
    /// <summary>What was saved under the name, or null.</summary>
    byte[]? Load(string name);

    /// <summary>Saves the bytes under the name, over anything there.</summary>
    void Save(string name, byte[] data);

    /// <summary>Forgets what's under the name, if anything.</summary>
    void Delete(string name);
}

/// <summary>The system's notifications.</summary>
public interface INotifications
{
    /// <summary>Shows a notification, replacing one with the same id. Clicking one with a meeting opens it: the app calls
    /// <see cref="AppModel.Open"/> with it.</summary>
    void Post(string id, string title, string body, string? meeting);
}

/// <summary>Plays a speaker's clips back to back, streamed from their links.</summary>
public interface IClipPlayer
{
    /// <summary>Plays these clips; the same ones again pause or resume; others stop what's playing first. It stops by itself
    /// after the last. Throws NotSupportedException when Windows has no media player (an N edition without its Media
    /// Feature Pack).</summary>
    void Toggle(IReadOnlyList<Uri> clips);

    /// <summary>Whether these clips are the ones playing.</summary>
    bool IsPlaying(IReadOnlyList<Uri> clips);

    /// <summary>Stops, and lets go of the clips.</summary>
    void Stop();

    /// <summary>Raised on the UI thread when playing starts, pauses or ends, for Listen's label.</summary>
    event Action? Changed;
}

/// <summary>Tally as a startup app, so the tray has it from the start.</summary>
public interface ILoginItem
{
    /// <summary>Asked of the system each time: the owner can change it in Settings too.</summary>
    bool Enabled { get; }

    /// <summary>Opens Tally at sign-in from now on; throws if it can't.</summary>
    void Register();

    /// <summary>Stops opening Tally at sign-in; throws if it can't.</summary>
    void Unregister();
}

/// <summary>The shell: the folder picker, a question, the clipboard, opening things.</summary>
public interface IShell
{
    /// <summary>A folder picker, its button saying <paramref name="prompt"/>; the chosen folder's path, or null when cancelled.</summary>
    Task<string?> ChooseFolder(string prompt);

    /// <summary>Asks a question with two buttons; true for the first.</summary>
    Task<bool> Confirm(string title, string message, string confirm, string cancel);

    /// <summary>The image on the clipboard, as PNG bytes, or null when there's none; throws when the clipboard can't be read (another program has it open).</summary>
    Task<byte[]?> ClipboardImage();

    /// <summary>Opens a file in its app, a folder in Explorer, or a URI (ms-settings:…, https:…).</summary>
    void Open(string target);
}

/// <summary>Updating Tally from inside it.</summary>
public interface IInstaller
{
    /// <summary>Where the installer writes how it went: the last line starting "FAILED: " is why it stopped.</summary>
    string LogPath { get; }

    /// <summary>Runs the install page's installer, which quits Tally and opens the new one; returns its exit code if it
    /// stops first (0 for success). Throws Api.Offline if the install page can't be reached, or another if it couldn't start.</summary>
    Task<int> Install(Uri site);
}

/// <summary>Crash reports the system (or Tally itself) wrote for Tally.</summary>
public interface ICrashReports
{
    /// <summary>Every report there is, each a text file; the core works out which is news.</summary>
    IReadOnlyList<CrashReport> Find();
}

/// <summary>A crash report: its file, and when it was written.</summary>
public sealed record CrashReport(string Path, DateTimeOffset Written);

/// <summary>Tally's log, for the owner to read when something went wrong: never audio, transcripts or voiceprints.</summary>
public interface ILog
{
    /// <summary>One line, in a category ("calls", "meetings-folder").</summary>
    void Write(string category, string message);
}
