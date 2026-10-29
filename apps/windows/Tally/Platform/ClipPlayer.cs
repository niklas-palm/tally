using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Tally;

/// <summary>A speaker's clips, played back to back from their links (IClipPlayer), with Windows' own media player.
/// Made and used on the UI thread; the player raises its events on threads of its own, so they're passed back to it.</summary>
internal sealed class ClipPlayer : IClipPlayer
{
    private readonly DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
    private (MediaPlayer Player, IReadOnlyList<Uri> Clips)? current;

    public event Action? Changed;

    public void Toggle(IReadOnlyList<Uri> clips)
    {
        if (current is { } same && same.Clips.SequenceEqual(clips))
        {
            if (IsPlaying(clips)) same.Player.Pause(); else same.Player.Play();
            Changed?.Invoke();
            return;
        }
        Stop();
        MediaPlayer player;
        try
        {
            var list = new MediaPlaybackList();
            foreach (var clip in clips) list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromUri(clip)));
            player = new MediaPlayer { Source = list };
        }
        catch (Exception e) when (e is COMException or TypeLoadException or FileNotFoundException)
        {
            throw new NotSupportedException("Windows has no media player.", e);  // an N edition without its Media Feature Pack
        }
        player.MediaEnded += (_, _) => queue.TryEnqueue(() => { if (ReferenceEquals(current?.Player, player)) Stop(); });
        player.PlaybackSession.PlaybackStateChanged += (_, _) =>  // opened, buffering, playing
            queue.TryEnqueue(() => { if (ReferenceEquals(current?.Player, player)) Changed?.Invoke(); });
        current = (player, clips);
        player.Play();
        Changed?.Invoke();
    }

    /// <summary>Still opening or buffering counts: Listen says Pause from the click on, and a click then pauses.</summary>
    public bool IsPlaying(IReadOnlyList<Uri> clips) =>
        current is { } playing && playing.Clips.SequenceEqual(clips)
        && playing.Player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;

    public void Stop()
    {
        if (current is not { } playing) return;
        playing.Player.Pause();
        playing.Player.Dispose();
        current = null;
        Changed?.Invoke();
    }
}
