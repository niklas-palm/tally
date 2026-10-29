using System.Text.Json;

namespace Tally;

/// <summary>
/// Updating Tally from inside it. The install page publishes <c>windows.json</c> next to its installer, and a build from
/// it knows its own version (<c>tally.json</c>). When they differ, the menu offers Update, which runs the installer (the
/// platform's <see cref="IInstaller"/>): it installs the new version, quits Tally and opens the new one. A build from the
/// repository has no version, so it's never offered one.
/// </summary>
public static class Updates
{
    /// <summary>The version on the install page, when it isn't this one: it only ever serves the latest.</summary>
    public static async Task<string?> Available(HttpClient http, Uri site, string mine)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site, "windows.json"));
        request.Headers.CacheControl = new() { NoCache = true };
        try
        {
            var (_, data) = await Http.Send(http, request, TimeSpan.FromSeconds(20));
            return JsonSerializer.Deserialize<Published>(data, Api.Json)?.Version is { Length: > 0 } latest && latest != mine ? latest : null;
        }
        catch (Exception e) when (e is Api.Offline or JsonException)  // a captive portal answers with a page of its own
        {
            return null;
        }
    }

    private sealed record Published(string? Version);

    /// <summary>Why the last install failed, as the installer wrote it ("FAILED: …"): only from the last run in the log (each
    /// starts with "--- "), so an old failure isn't this one's.</summary>
    public static string? LastFailure(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.FindLastIndex(lines, l => l.StartsWith("--- ", StringComparison.Ordinal));
        return lines[Math.Max(start, 0)..].LastOrDefault(l => l.StartsWith("FAILED: ", StringComparison.Ordinal))?["FAILED: ".Length..];
    }
}
