using System.Globalization;

namespace Tally;

/// <summary>Tally's log (ILog), a file the owner can read: steps and timings, never audio, transcripts or voiceprints.
/// Written on the UI thread. A line it can't write is dropped: logging must never break what it logs.</summary>
internal sealed class Log(string folder) : ILog
{
    public void Write(string category, string message)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "tally.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
            // the same on every PC: a Danish one's time separator is a dot
            File.AppendAllText(path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{category}] {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
