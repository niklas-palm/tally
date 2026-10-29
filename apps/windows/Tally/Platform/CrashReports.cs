using System.Globalization;

namespace Tally;

/// <summary>
/// Crash reports (ICrashReports). Windows writes none a person could send for a .NET app, so Tally writes its own when
/// it fails unexpectedly: the exception and the versions, never meeting content.
/// </summary>
internal sealed class CrashReports(string folder) : ICrashReports
{
    public IReadOnlyList<CrashReport> Find() =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.txt").Select(path => new CrashReport(path, File.GetLastWriteTimeUtc(path))).ToList()
            : [];

    public void Write(Exception error)
    {
        Directory.CreateDirectory(folder);
        var report = $"""
            Tally {typeof(App).Assembly.GetName().Version} on Windows {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})
            {DateTimeOffset.Now:O}

            {error}
            """;
        File.WriteAllText(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"), report);
    }
}
