using System.Globalization;

namespace Tally.Tests;

/// <summary>Small pieces: updates, crash reports, search snippets, times, ULIDs.</summary>
public class SmallTests
{
    [Fact]
    public void Only_the_last_installs_failure_is_reported()
    {
        const string log = "--- Mon installing Tally 1\r\nFAILED: The download is damaged (its checksum is wrong). Try again.\r\n"
                           + "--- Tue installing Tally 2\r\n==> Downloading Tally 2\r\nFAILED: Tally is recording. Stop the recording, then run this command again.\r\n";
        Assert.Equal("Tally is recording. Stop the recording, then run this command again.", Updates.LastFailure(log));
        Assert.Null(Updates.LastFailure("--- Mon installing Tally 1\nFAILED: old\n--- Tue installing Tally 2\n==> Building"));
    }

    /// <summary>Any other version on the install page is an update: it only ever serves the latest.</summary>
    [Fact]
    public async Task Another_version_in_windows_json_is_an_update()
    {
        var http = new FakeHttp();
        using var client = new HttpClient(http);
        Task<string?> Available() => Updates.Available(client, new Uri("https://site.test"), "2026.10.26-43661a4");
        http.On("GET https://site.test/windows.json", 200, """{"version": "2026.10.27-abc1234"}""");
        Assert.Equal("2026.10.27-abc1234", await Available());
        http.On("GET https://site.test/windows.json", 200, """{"version": "2026.10.26-43661a4"}""");
        Assert.Null(await Available());
        http.On("GET https://site.test/windows.json", 200, """{"version": ""}""");
        Assert.Null(await Available());
        http.Offline = true;
        Assert.Null(await Available());
    }

    /// <summary>Only the newest report since the last look is offered, once; those from before the first look are left alone.</summary>
    [Fact]
    public void Only_the_newest_unhandled_crash_report_is_offered()
    {
        using var temp = new TempFolder();
        var now = DateTimeOffset.UtcNow;
        var settings = Settings.Load(Path.Combine(temp.Path, "settings.json"));
        CrashReport[] reports = [new("before", now.AddDays(-1))];
        Assert.Null(Crashes.Pending(settings, reports));  // the first look
        CrashReport[] later = [.. reports, new("old", now.AddMinutes(1)), new("new", now.AddMinutes(2))];
        var again = Settings.Load(Path.Combine(temp.Path, "settings.json"));  // remembered
        Assert.Equal("new", Crashes.Pending(again, later)?.Path);
        Crashes.Handled(again, later[^1]);
        Assert.Null(Crashes.Pending(again, later));  // and every older one with it
    }

    /// <summary>Search results mark each word searched for, whatever its case or accents.</summary>
    [Fact]
    public void Search_results_mark_the_words_searched_for()
    {
        var runs = Snippets.Marked("Budget for Acme, and Café plans", "acme cafe");
        Assert.Equal(["Acme", "Café"], runs.Where(r => r.Bold).Select(r => r.Text));
        Assert.Equal("Budget for Acme, and Café plans", string.Concat(runs.Select(r => r.Text)));
    }

    /// <summary>The menu's time of a meeting from before today: the date, and the time itself, not "27 Oct, P".</summary>
    [Fact]
    public void A_meeting_from_another_day_shows_its_date_and_time()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var time = new DateTime(2026, 10, 27, 14, 30, 0).ToString("t");  // "2:30 PM", its space as the PC's ICU has it
            Assert.Equal("27 Oct, " + time, Format.When(new DateTime(2026, 10, 27, 14, 30, 0), new DateTime(2026, 10, 29)));
            Assert.Equal(time, Format.When(new DateTime(2026, 10, 29, 14, 30, 0), new DateTime(2026, 10, 29)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Times_read_as_a_clock()
    {
        Assert.Equal("4:05", Format.Clock(245.9));
        Assert.Equal("1:04:05", Format.Clock(3845));
        Assert.Equal("1 hr, 5 min", Format.Duration(3900));
        Assert.Equal("1 min", Format.Duration(59.7));
        Assert.Equal("45 sec", Format.Duration(45));
    }

    /// <summary>A meeting the job said it can't summarise: sending it again would fail the same way, so it isn't offered.</summary>
    [Fact]
    public void A_meeting_that_cant_be_summarised_isnt_offered_again()
    {
        Item Failed(string error) => new("m", null, DateTimeOffset.UtcNow, null, Status.Failed(error), HasAudio: true, InCloud: true);
        Assert.True(Failed("Something went wrong while summarising it.").CanRetry);
        Assert.False(Failed("No speech was heard in this recording, so there's nothing to summarise.").CanRetry);
        Assert.False(Failed("This recording is longer than Tally summarises (4 hours).").CanRetry);
    }

    [Fact]
    public void Ulids_sort_by_time()
    {
        var earlier = Ulid.Make(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var later = Ulid.Make(DateTimeOffset.FromUnixTimeMilliseconds(2_000));
        Assert.Equal(26, earlier.Length);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void A_ulid_says_when_it_was_made()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_123);
        Assert.Equal(at, Ulid.Date(Ulid.Make(at)));
    }
}
