using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tally;

/// <summary>
/// Tally's settings (docs/data.md, "On Windows"): what the Mac keeps in UserDefaults, here in one small JSON file in
/// Tally's data folder, written whole at each change.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    private string path = "";

    /// <summary>What Tally does when a call starts; null until the owner chooses.</summary>
    public CallMode? CallMode { get; set; }

    /// <summary>True once the window has opened by itself the first time.</summary>
    public bool Welcomed { get; set; }

    /// <summary>The meetings folder's path, by the provider's id of the account it belongs to.</summary>
    public Dictionary<string, string> MeetingsFolders { get; set; } = [];

    /// <summary>Meetings whose suggested folder the owner dismissed.</summary>
    public List<string> NotFiled { get; set; } = [];

    /// <summary>When the newest crash report handled was written.</summary>
    public DateTimeOffset? CrashesSeenUntil { get; set; }

    /// <summary>The settings, or the defaults when there are none or they can't be read: losing a setting beats not
    /// starting.</summary>
    public static Settings Load(string path)
    {
        Settings settings;
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), Json) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { settings = new(); }
        settings.path = path;
        return settings;
    }

    /// <summary>Kept on disk; a disk that's full or a file another program holds loses the setting, not Tally.</summary>
    public void Save()
    {
        try { Disk.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
