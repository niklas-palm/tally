namespace Tally.Tests;

/// <summary>Calls: what the rules do with what's using audio, second by second.</summary>
public class CallsTests
{
    private const int Zoom = 501;
    private static readonly CallRules.Event Offered = new CallRules.Event.Offer(Zoom, null);
    private static readonly CallRules.Event Stopped = new CallRules.Event.Stop();
    private static readonly CallRules.Event Finished = new CallRules.Event.Finish();

    private static List<AudioUser> OnMic() => [new(Zoom, Input: true, Output: true)];
    private static List<AudioUser> Playing() => [new(Zoom, Input: false, Output: true)];
    private static List<AudioUser> Nobody() => [];

    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    private readonly CallRules rules = new();

    /// <summary>Feeds the same users for this many seconds, one second at a time, and gathers what happened.</summary>
    private List<CallRules.Event> Run(List<AudioUser> users, int seconds)
    {
        var events = new List<CallRules.Event>();
        for (var i = 0; i < seconds; i++)
        {
            events.AddRange(rules.Observe(users, now));
            now += TimeSpan.FromSeconds(1);
        }
        return events;
    }

    private List<CallRules.Event> Now(List<AudioUser> users) => rules.Observe(users, now);

    [Fact]
    public void A_call_is_offered_after_ten_seconds_on_the_mic()
    {
        Assert.Empty(Run(OnMic(), 10));  // 0 to 9 s
        Assert.Equal([Offered], Now(OnMic()));  // 10 s
        Assert.Empty(Run(OnMic(), 60));  // offered once
    }

    [Fact]
    public void The_offer_names_the_app_the_platform_named()
    {
        Run([new(Zoom, true, true, "Zoom")], 10);
        Assert.Equal([new CallRules.Event.Offer(Zoom, "Zoom")], Now([new(Zoom, true, true, "Zoom")]));
    }

    [Fact]
    public void Not_now_holds_for_the_call_and_the_next_call_offers_again()
    {
        Run(OnMic(), 11);
        rules.NotNow();
        Assert.Empty(Run(Nobody(), 31));  // the call ends, without a recording to stop
        Assert.Equal([Offered], Run(OnMic(), 11));
    }

    [Fact]
    public void Not_now_holds_through_a_short_gap()
    {
        Run(OnMic(), 11);
        rules.NotNow();
        Assert.Empty(Run(Nobody(), 10));
        Assert.Empty(Run(OnMic(), 20));
    }

    [Fact]
    public void No_offer_while_recording_or_with_the_setting_off()
    {
        rules.Started();  // nobody on the mic: a recording of its own
        Assert.Equal(CallRules.State.Plain, rules.Recording);
        Assert.Empty(Run(OnMic(), 20));

        var off = new CallRules { Offering = false };
        for (var i = 0; i < 20; i++) Assert.Empty(off.Observe(OnMic(), now.AddSeconds(i)));
    }

    [Fact]
    public void Muted_never_ends_a_call_but_silence_for_thirty_seconds_does()
    {
        Run(OnMic(), 11);
        rules.Started();
        Assert.Equal(CallRules.State.Following, rules.Recording);
        Assert.Empty(Run(Playing(), 120));  // off the mic, still playing the others
        Assert.Empty(Run(Nobody(), 30));  // 0 to 29 s of silence
        Assert.Equal([Stopped], Now(Nobody()));  // 30 s
        Assert.Equal([new CallRules.Event.Again(Zoom, null)], Run(OnMic(), 11));  // the call is over: the next is a new one
    }

    [Fact]
    public void A_recording_started_early_in_a_call_follows_it()
    {
        Run(OnMic(), 3);  // Start Recording before any offer
        rules.Started();
        Assert.Equal(CallRules.State.Following, rules.Recording);
        Assert.Equal([Stopped], Run(Nobody(), 31));
    }

    [Fact]
    public void A_recording_with_nobody_else_on_the_mic_never_stops()
    {
        rules.Started();
        Assert.Empty(Run(Nobody(), 600));
        Assert.Equal(CallRules.State.Plain, rules.Recording);
    }

    /// <summary>30 s to continue, not 5 minutes.</summary>
    [Fact]
    public void A_stopped_recording_can_be_continued_for_thirty_seconds_then_finishes()
    {
        Run(OnMic(), 11);
        rules.Started();
        Run(Nobody(), 31);
        rules.Continued();  // a mistake: the call goes on
        Assert.Empty(Run(OnMic(), 11));  // the call is back, and no offer while recording
        Assert.Equal([Stopped], Run(Nobody(), 31));  // it follows the call again
        Assert.Empty(Run(Nobody(), 29));
        Assert.Equal([Finished], Now(Nobody()));
        Assert.Equal(CallRules.State.None, rules.Recording);
    }

    /// <summary>A call starting while a stopped recording can still go on asks whether it's the same meeting.</summary>
    [Fact]
    public void A_call_while_stopped_asks_whether_its_the_same_meeting()
    {
        Run(OnMic(), 11);
        rules.Started();
        Assert.Equal([Stopped], Run(Nobody(), 31));
        Assert.Empty(Run(Nobody(), 20));
        // On the mic from 20 s after the stop: at 30 s it's not a call yet, so it waits; at 10 s on the mic, it asks.
        Assert.Equal([new CallRules.Event.Again(Zoom, null)], Run(OnMic(), 11));
        Assert.Equal(CallRules.State.Asking, rules.Recording);
        Assert.Empty(Run(OnMic(), 60));  // no finish, no offer, while the owner decides
        rules.Continued();  // The Same Meeting
        Assert.Equal(CallRules.State.Following, rules.Recording);
        Assert.Equal([Stopped], Run(Nobody(), 31));
    }

    [Fact]
    public void A_mic_that_goes_before_its_a_call_lets_the_stopped_recording_finish()
    {
        Run(OnMic(), 11);
        rules.Started();
        Run(Nobody(), 31);
        Assert.Empty(Run(Nobody(), 25));
        Assert.Empty(Run(OnMic(), 8));  // voice typing, say: past 30 s, but on the mic
        Assert.Equal([Finished], Now(Nobody()));  // gone, before it was a call
    }

    /// <summary>Measured on the Mac: a Slack huddle's helper kept its speakers open after the call, so a Not Now never reset.</summary>
    [Fact]
    public void After_not_now_an_app_that_keeps_its_speakers_open_still_gets_offered_the_next_call()
    {
        Run(OnMic(), 11);
        rules.NotNow();
        Assert.Empty(Run(Playing(), 31));  // off the mic, speakers still open: the call is over
        Assert.Equal([Offered], Run(OnMic(), 11));  // the next call
    }

    /// <summary>A recording discarded mid-call, and the next call, even 10 s later, is a call of its own.</summary>
    [Fact]
    public void A_new_call_soon_after_a_discard_is_offered_again()
    {
        Run(OnMic(), 11);
        rules.Started();
        rules.Ended();  // Discard, still in the huddle
        Assert.Empty(Run(OnMic(), 5));
        Assert.Empty(Run(Nobody(), 6));  // the huddle closed: over after 5 s, nothing to stop
        Assert.Equal([Offered], Run(OnMic(), 11));  // the next one, 10 s on the mic
    }

    [Fact]
    public void A_calls_audio_moving_to_another_of_its_processes_is_the_same_call()
    {
        Run(OnMic(), 11);
        rules.Started();
        Run([new(Zoom, true, true), new(502, true, false)], 5);
        Assert.Empty(Run([new(502, true, false)], 60));  // Zoom left, its other process carries the call
        Assert.Equal([Stopped], Run(Nobody(), 31));
    }
}
