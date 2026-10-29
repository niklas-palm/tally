namespace Tally.Tests;

/// <summary>A recording on this PC: its folder, its local.json, and what its WAV holds.</summary>
public class RecordingTests
{
    /// <summary>Auto mode: what it starts by itself is kept only if it lasted 2 minutes.</summary>
    [Fact]
    public void An_auto_recording_under_two_minutes_isnt_kept()
    {
        var start = DateTimeOffset.UtcNow;
        var lobby = new Recording("a", start, RecordingState.Recording, start.AddSeconds(90), Auto: true);
        Assert.True(lobby.TooShortToKeep);
        Assert.False((lobby with { EndedAt = start.AddSeconds(120) }).TooShortToKeep);
        Assert.False(new Recording("b", start, RecordingState.Recording, start.AddSeconds(30)).TooShortToKeep);  // started by the owner
    }

    /// <summary>A local.json a power cut left as zeros: the recording is rebuilt from its folder, its audio never unlisted.</summary>
    [Fact]
    public void A_recording_whose_record_cant_be_read_is_rebuilt_from_its_audio()
    {
        using var temp = new TempFolder();
        var id = Ulid.Make(DateTimeOffset.Parse("2026-10-28T09:00:00Z"));
        var unreadable = new Recording(id, DateTimeOffset.UtcNow, RecordingState.Saved) { Root = temp.Path };
        Directory.CreateDirectory(unreadable.Folder);
        File.WriteAllBytes(unreadable.Audio, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(unreadable.Folder, "local.json"), new byte[200]);
        var found = Assert.Single(Recording.LoadAll(temp.Path));  // taken as cut short: Recover finishes it as far as its files go
        Assert.Equal((id, RecordingState.Recording, DateTimeOffset.Parse("2026-10-28T09:00:00Z")), (found.Id, found.State, found.StartedAt));
    }

    /// <summary>A folder Tally didn't make, with an unreadable record and audio in it: passed over, so Tally still starts.</summary>
    [Fact]
    public void A_folder_tally_didnt_make_is_passed_over()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "my recording");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "audio.m4a"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "local.json"), new byte[200]);
        Assert.Empty(Recording.LoadAll(temp.Path));
    }

    /// <summary>A folder whose local.json is gone was removed: the meeting deleted, sent or thrown away, and the audio
    /// a delete couldn't take with it (a file another program held) is never brought back and sent.</summary>
    [Fact]
    public void A_removed_recording_with_its_audio_left_behind_isnt_listed()
    {
        using var temp = new TempFolder();
        var gone = new Recording(Ulid.Make(), DateTimeOffset.UtcNow, RecordingState.Sent) { Root = temp.Path };
        Directory.CreateDirectory(gone.Folder);
        File.WriteAllBytes(gone.Audio, [1, 2, 3]);
        Assert.Empty(Recording.LoadAll(temp.Path));
    }

    /// <summary>A write that fails as it's moved into place (a file another program holds): no temporary copy is left beside it.</summary>
    [Fact]
    public void A_write_that_fails_leaves_no_temporary_file()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        Directory.CreateDirectory(path);  // in the way: the move over it fails
        var failed = Record.Exception(() => Disk.Write(path, [1, 2, 3]));
        Assert.True(failed is IOException or UnauthorizedAccessException, failed?.GetType().Name);  // Windows says access denied
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    /// <summary>A settings.json a power cut left as zeros: Tally starts with its defaults rather than not at all.</summary>
    [Fact]
    public void Settings_that_cant_be_read_are_the_defaults()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllBytes(path, new byte[200]);
        var settings = Settings.Load(path);
        Assert.Null(settings.CallMode);
        settings.CallMode = CallMode.Auto;
        settings.Save();
        Assert.Equal(CallMode.Auto, Settings.Load(path).CallMode);
    }

    [Fact]
    public void A_recording_is_kept_in_its_folder_and_read_back_newest_first()
    {
        using var temp = new TempFolder();
        var older = new Recording("01A", DateTimeOffset.UtcNow.AddHours(-1), RecordingState.Saved, Error: "Couldn't send it yet.") { Root = temp.Path };
        var newer = new Recording("01B", DateTimeOffset.UtcNow, RecordingState.Recording, Auto: true) { Root = temp.Path };
        older.Save();
        newer.Save();
        Directory.CreateDirectory(Path.Combine(temp.Path, "not-a-recording"));
        Assert.Equal([newer, older], Recording.LoadAll(temp.Path));
        Assert.Contains("\"state\": \"saved\"", File.ReadAllText(Path.Combine(older.Folder, "local.json")));
        older.Remove();
        Assert.Equal([newer], Recording.LoadAll(temp.Path));
    }

    /// <summary>A crash leaves the WAV's sizes unwritten: what's in it is counted from the file itself.</summary>
    [Fact]
    public void A_wav_left_by_a_crash_still_says_what_it_holds()
    {
        using var temp = new TempFolder();
        var wav = Path.Combine(temp.Path, "recording.wav");
        Fixtures.Wav(wav, frames: 4800);
        Assert.Equal(4800, Recording.Frames(wav));
        Fixtures.Wav(wav, frames: 0);
        Assert.Equal(0, Recording.Frames(wav));
        File.WriteAllText(wav, "not audio");
        Assert.Null(Recording.Frames(wav));
        Assert.Null(Recording.Frames(Path.Combine(temp.Path, "missing.wav")));
    }
}
