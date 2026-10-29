namespace Tally;

/// <summary>
/// Notices calls, so a meeting isn't left unrecorded. Another app on the microphone for 10 s is a call, and Tally offers
/// to record it. The call ends 30 s after its apps leave the microphone, or, while Tally records it, the speakers too,
/// since a muted call still plays the others. The recording then pauses for 30 s in case the call goes on; a new call in
/// that time asks whether it's the same meeting. Without a recording the speakers don't count: many apps keep them open
/// between calls.
///
/// Pure rules over what's using audio and the time, so tests drive it with their own clock.
/// </summary>
public sealed class CallRules
{
    public static readonly TimeSpan StartsAfter = TimeSpan.FromSeconds(10);  // keeps out voice typing and quick voice notes
    public static readonly TimeSpan EndsAfter = TimeSpan.FromSeconds(30);
    // With nothing recording and nothing declined, nothing needs the 30 s: the call is over after this, so the next
    // call (after a recording discarded mid-call, say) counts as one of its own
    public static readonly TimeSpan EndsSoonAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Continuable = TimeSpan.FromSeconds(30);  // the owner's call: 5 minutes was too long to wait for the send

    public enum State
    {
        None,
        /// <summary>Started with no call on: it never stops by itself.</summary>
        Plain,
        /// <summary>Started during a call: it stops when the call ends.</summary>
        Following,
        /// <summary>Stopped by the call's end, and can still be continued.</summary>
        Stopped,
        /// <summary>Stopped, and a new call started: is it the same meeting?</summary>
        Asking,
    }

    public abstract record Event
    {
        /// <summary>A call has started: offer to record it, naming this app.</summary>
        public sealed record Offer(int Pid, string? App) : Event;

        /// <summary>The call a recording followed has ended.</summary>
        public sealed record Stop : Event;

        /// <summary>A call started while stopped: ask whether it's the same meeting.</summary>
        public sealed record Again(int Pid, string? App) : Event;

        /// <summary>A stopped recording wasn't continued in time.</summary>
        public sealed record Finish : Event;
    }

    /// <summary>The owner's setting.</summary>
    public bool Offering { get; set; } = true;

    public State Recording { get; private set; } = State.None;

    private HashSet<int>? call;  // the call's apps, once a call has started
    private DateTimeOffset stoppedSince;
    private Dictionary<int, DateTimeOffset> micSince = [];
    private DateTimeOffset? quietSince;
    private bool declined;  // Not Now, until the call ends

    /// <summary>What's using audio now (other apps only), and what that means.</summary>
    public List<Event> Observe(IReadOnlyList<AudioUser> users, DateTimeOffset now)
    {
        var events = new List<Event>();
        var onMic = users.Where(u => u.Input).Select(u => u.Pid).ToHashSet();
        micSince = onMic.ToDictionary(pid => pid, pid => micSince.GetValueOrDefault(pid, now));

        if (call is { } apps)
        {
            apps.UnionWith(onMic);  // a call's audio can move to another of its processes
            var following = Recording == State.Following;
            if (users.Any(u => apps.Contains(u.Pid) && (u.Input || following && u.Output)))
            {
                quietSince = null;  // recording, muted still counts: the call app keeps playing the others' voices
            }
            else if (now - (quietSince ?? now) >= (Recording == State.None && !declined ? EndsSoonAfter : EndsAfter))
            {
                call = null;
                quietSince = null;
                declined = false;
                if (following)
                {
                    Recording = State.Stopped;
                    stoppedSince = now;
                    events.Add(new Event.Stop());
                }
            }
            else if (quietSince == null)
            {
                quietSince = now;
            }
        }
        else if (micSince.Where(p => now - p.Value >= StartsAfter).OrderBy(p => p.Value).Select(p => (int?)p.Key).FirstOrDefault() is { } first)
        {
            call = onMic;
            var app = users.FirstOrDefault(u => u.Pid == first)?.Name;
            if (Recording == State.Stopped)
            {
                Recording = State.Asking;
                events.Add(new Event.Again(first, app));
            }
            else if (Offering && Recording == State.None && !declined)
            {
                events.Add(new Event.Offer(first, app));
            }
        }

        // Not while something's on the mic: it may be a call that hasn't had its 10 s yet
        if (Recording == State.Stopped && now - stoppedSince >= Continuable && micSince.Count == 0)
        {
            Recording = State.None;
            events.Add(new Event.Finish());
        }
        return events;
    }

    /// <summary>A recording started, from the offer or Start Recording: it follows a call when another app was on the
    /// microphone as it started, even in the call's first 10 s, before any offer.</summary>
    public void Started()
    {
        if (call == null && micSince.Count > 0) call = [.. micSince.Keys];
        Recording = call == null ? State.Plain : State.Following;
    }

    /// <summary>Continue Recording after the call's end, or The Same Meeting: it follows the call again, when there's one.</summary>
    public void Continued() => Recording = State.Following;

    /// <summary>Stopped, sent or discarded.</summary>
    public void Ended() => Recording = State.None;

    /// <summary>Not Now: no more offers for this call.</summary>
    public void NotNow() => declined = true;
}
