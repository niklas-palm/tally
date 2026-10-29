using System.Globalization;

namespace Tally;

/// <summary>Times, dates, lengths and states as Tally writes them: shared by the window, the menu and the Markdown.</summary>
public static class Format
{
    /// <summary>"4:05", or "1:04:05" from an hour on.</summary>
    public static string Clock(double seconds)
    {
        var s = (int)seconds;
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"Tuesday 20 October 2026, 20:00": a fixed order that reads well, with the names in the PC's language, and
    /// the time on its own clock.</summary>
    public static string LongDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) + ", " + local.ToString("t", CultureInfo.CurrentCulture);
    }

    /// <summary>When a meeting was, as the menu lists it: "14:30" today, else "27 Oct, 14:30", the time on the PC's own
    /// clock. Built in two parts: inside a longer format, "t" is only the first letter of AM or PM.</summary>
    public static string When(DateTime local, DateTime today) =>
        local.Date == today ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d MMM", CultureInfo.CurrentCulture) + ", " + local.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"45 sec" under a minute; otherwise hours and minutes, "1 hr, 5 min".</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 59.5) return $"{Math.Round(seconds, MidpointRounding.AwayFromZero)} sec";  // by what it rounds to: never "60 sec"
        var minutes = (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        return minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr, {minutes % 60} min";
    }

    public static string StatusText(Status status) => status.Kind switch
    {
        StatusKind.Recording => "Recording",
        StatusKind.Saving => "Saving…",
        StatusKind.Waiting => "Waiting to be sent",
        StatusKind.Uploading => "Sending…",
        StatusKind.Queued => "Getting ready to summarise…",
        StatusKind.Processing => "Summarising…",
        StatusKind.Ready => "Ready",
        _ => status.Error ?? "",
    };
}
