using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tally;

public enum RecordingState
{
    Recording,
    /// <summary>Waiting to be sent: not signed in, offline, or the last upload failed.</summary>
    Saved,
    Uploading,
    /// <summary>The audio is up; the API hasn't heard yet that it's done.</summary>
    Uploaded,
    /// <summary>The API has it.</summary>
    Sent,
    /// <summary>The encode failed; Try Again encodes the WAV again, if it's there.</summary>
    Broken,
}

/// <summary>
/// A recording on this PC, from the moment it starts until the API has its summary. Each lives in its own folder under
/// the recordings folder (<c>%LOCALAPPDATA%\Tally\recordings</c>):
/// <code>
/// recordings\&lt;id&gt;\local.json      this record
///                  recording.wav   while recording (a crash leaves it readable)
///                  audio.m4a       from stop until the summary is ready, then the folder goes: S3 has it
/// </code>
/// After upload the API owns the meeting; this only keeps the audio so a failed meeting can be sent again.
/// </summary>
/// <param name="Auto">Started by itself, in auto mode.</param>
public sealed record Recording(string Id, DateTimeOffset StartedAt, RecordingState State, DateTimeOffset? EndedAt = null, string? Error = null, bool? Auto = null)
{
    /// <summary>An auto-started recording shorter than this is discarded, not sent.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The recordings folder it's in: the platform's, given when it's made or loaded.</summary>
    [JsonIgnore] public string Root { get; init; } = "";

    /// <summary>Started by itself and too short to be a meeting: a lobby, a mic test, dictation.</summary>
    [JsonIgnore] public bool TooShortToKeep => Auto == true && (EndedAt ?? StartedAt) - StartedAt < Shortest;

    [JsonIgnore] public string Folder => Path.Combine(Root, Id);
    [JsonIgnore] public string Wav => Path.Combine(Folder, "recording.wav");
    [JsonIgnore] public string Audio => Path.Combine(Folder, "audio.m4a");

    public static List<Recording> LoadAll(string root)
    {
        var folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        var recordings = new List<Recording>();
        foreach (var folder in folders)
        {
            try
            {
                if (JsonSerializer.Deserialize<Recording>(File.ReadAllBytes(Path.Combine(folder, "local.json")), Json) is { } found)
                    recordings.Add(found with { Root = root });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Rebuilt only when the record is there but unreadable (a power cut as it was written). A folder without
                // one was removed (deleted, sent or discarded), even if the delete left its audio behind.
                if (File.Exists(Path.Combine(folder, "local.json")) && Rebuilt(root, folder) is { } rebuilt) recordings.Add(rebuilt);
            }
        }
        return recordings.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>A recording rebuilt from its folder, so its audio is never left unlisted. It started when its id says,
    /// and is taken as cut short, so Recover finishes it as far as its files go. A folder with no audio isn't a
    /// recording.</summary>
    private static Recording? Rebuilt(string root, string folder)
    {
        var id = Path.GetFileName(folder);
        if (!Ulid.Is(id)) return null;  // not one Tally made (by hand, say): passed over, as reading it would stop every launch
        var found = new Recording(id, Ulid.Date(id), RecordingState.Recording) { Root = root };
        return File.Exists(found.Wav) || File.Exists(found.Audio) ? found : null;
    }

    public void Save()
    {
        try
        {
            Disk.Write(Path.Combine(Folder, "local.json"), JsonSerializer.SerializeToUtf8Bytes(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Remove()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The frames a recording's WAV holds, counted from its data chunk to the end of the file, since a crash can
    /// leave the header's sizes unwritten; null when it isn't a WAV that can be read.</summary>
    public static long? Frames(string wav)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(wav));
            if (Tag(reader) != "RIFF") return null;
            reader.ReadUInt32();
            if (Tag(reader) != "WAVE") return null;
            var blockAlign = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = Tag(reader);
                var size = reader.ReadUInt32();
                var start = reader.BaseStream.Position;
                if (id == "fmt ")
                {
                    reader.ReadBytes(12);
                    blockAlign = reader.ReadUInt16();
                }
                else if (id == "data" && blockAlign > 0)
                {
                    var remaining = reader.BaseStream.Length - start;
                    return (size == 0 || size > remaining ? remaining : size) / blockAlign;
                }
                reader.BaseStream.Position = start + size + size % 2;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Tag(BinaryReader reader) => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
}
