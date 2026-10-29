namespace Tally;

/// <summary>
/// The crash reports written for Tally (found by the platform's <see cref="ICrashReports"/>), looked at at launch so the
/// owner can send the newest with a click. Reports from before Tally first looked are left alone, and a report is
/// handled, sent or not, only once: Tally remembers when the newest it has seen was written.
/// </summary>
public static class Crashes
{
    /// <summary>The newest report not handled yet, if any.</summary>
    public static CrashReport? Pending(Settings settings, IReadOnlyList<CrashReport> reports)
    {
        if (settings.CrashesSeenUntil is not { } seen)
        {
            settings.CrashesSeenUntil = DateTimeOffset.UtcNow;  // the first look: older crashes aren't news
            settings.Save();
            return null;
        }
        return reports.Where(r => r.Written > seen).MaxBy(r => r.Written);
    }

    /// <summary>This report and every older one are handled.</summary>
    public static void Handled(Settings settings, CrashReport report)
    {
        settings.CrashesSeenUntil = report.Written;
        settings.Save();
    }
}
