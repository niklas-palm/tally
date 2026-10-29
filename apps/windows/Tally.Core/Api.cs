using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tally;

/// <summary>
/// Tally's API (docs/api.md), with the signed-in person's access token on every call: everything it returns is theirs.
/// </summary>
public sealed class Api(Uri baseUrl, string token, string? version, HttpClient http)
{
    /// <summary>The API's JSON: snake_case keys, and nothing written for a field with nothing in it.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<IReadOnlyList<Meeting>> Meetings() => List<Meeting>(await Send(HttpMethod.Get, "meetings"), "meetings");

    public async Task<Detail> Detail(string id) => Decode<Detail>(await DetailData(id));

    /// <summary>The meeting as the API sends it, for the meetings folder to keep and render again.</summary>
    public Task<byte[]> DetailData(string id) => Send(HttpMethod.Get, $"meetings/{id}");

    public async Task<IReadOnlyList<Person>> People() => List<Person>(await Send(HttpMethod.Get, "people"), "people");

    /// <summary>The API has this meeting already (a send that got further than the app knew): nothing more to upload.</summary>
    public sealed class AlreadySent : Exception;

    /// <summary>Registers a recording and uploads its audio, which starts the pipeline. Throws <see cref="AlreadySent"/>
    /// when it's sent.</summary>
    public async Task Upload(Recording recording, string appVersion)
    {
        var body = new Dictionary<string, object?> { ["started_at"] = Iso(recording.StartedAt), ["app_version"] = appVersion };
        if (recording.EndedAt is { } ended) body["ended_at"] = Iso(ended);
        Upload upload;
        try
        {
            upload = Decode<Upload>(await Send(HttpMethod.Put, $"meetings/{recording.Id}", body));
        }
        catch (Failure e) when (e.Code == 409)
        {
            throw new AlreadySent();
        }
        await Http.Upload(http, upload, recording.Audio);
    }

    /// <summary>The audio is up: the meeting is queued.</summary>
    public Task Uploaded(string id) => Send(HttpMethod.Post, $"meetings/{id}/uploaded");

    public Task DeleteMeeting(string id) => Send(HttpMethod.Delete, $"meetings/{id}");

    /// <summary>Who a speaker is: <c>{person_id}</c>, <c>{name, role, company}</c> for someone new, <c>{unknown: true}</c>, or
    /// <c>{background: true}</c>.</summary>
    public Task Assign(string meeting, string speaker, IReadOnlyDictionary<string, object> body) =>
        Send(HttpMethod.Put, $"meetings/{meeting}/speakers/{speaker}", body);

    /// <summary>A crash report from this PC, sent with the owner's say-so.</summary>
    public Task ReportCrash(string report, string appVersion) =>
        Send(HttpMethod.Post, "crashes", new Dictionary<string, object> { ["report"] = report, ["app_version"] = appVersion });

    /// <summary>The owner's copy of a meeting's summary, saved over their last; <see cref="RestoreSummary"/> goes back to the agent's.</summary>
    public Task SaveSummary(Summary summary, string meeting) =>
        Send(HttpMethod.Put, $"meetings/{meeting}/summary", new Dictionary<string, object> { ["summary"] = summary });

    public Task RewriteSummary(string meeting, bool replace) =>
        Send(HttpMethod.Post, $"meetings/{meeting}/summary/rewrite", new Dictionary<string, object> { ["replace"] = replace });

    public Task RestoreSummary(string meeting) => Send(HttpMethod.Delete, $"meetings/{meeting}/summary");

    public async Task<IReadOnlyList<Folder>> Folders() => List<Folder>(await Send(HttpMethod.Get, "folders"), "folders");

    public Task Save(Folder folder) =>
        Send(HttpMethod.Put, $"folders/{folder.FolderId}", new Dictionary<string, object?> { ["name"] = folder.Name, ["parent"] = folder.Parent });

    public Task DeleteFolder(string id) => Send(HttpMethod.Delete, $"folders/{id}");

    /// <summary>A meeting into a folder (<paramref name="inside"/>), or out of it.</summary>
    public Task File(string meeting, string folder, bool inside) =>
        Send(inside ? HttpMethod.Put : HttpMethod.Delete, $"meetings/{meeting}/folders/{folder}");

    public async Task<IReadOnlyList<MeetingFile>> Files(string meeting) =>
        List<MeetingFile>(await Send(HttpMethod.Get, $"meetings/{meeting}/files"), "files");

    /// <summary>A file from this PC into a meeting: where to put it, then the upload, signed for exactly its size.</summary>
    public async Task Add(string path, string type, string meeting)
    {
        var body = new Dictionary<string, object> { ["name"] = Path.GetFileName(path), ["size"] = new FileInfo(path).Length, ["type"] = type };
        var upload = Decode<Upload>(await Send(HttpMethod.Post, $"meetings/{meeting}/files", body));
        await Http.Upload(http, upload, path);
    }

    public Task DeleteFile(string file, string meeting) => Send(HttpMethod.Delete, $"meetings/{meeting}/files/{file}");

    public async Task<IReadOnlyList<SearchHit>> Search(string query) =>
        List<SearchHit>(await Send(HttpMethod.Get, "search?q=" + Uri.EscapeDataString(query)), "results");

    public Task UpdatePerson(string id, IReadOnlyDictionary<string, object> body) => Send(HttpMethod.Patch, $"people/{id}", body);

    public Task DeletePerson(string id) => Send(HttpMethod.Delete, $"people/{id}");

    private async Task<byte[]> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUrl.ToString().TrimEnd('/') + "/" + path));
        request.Headers.Authorization = new("Bearer", token);
        // Which version each person runs: the admin dashboard shows it
        request.Headers.TryAddWithoutValidation("X-Tally-Version", version ?? "repository build");
        if (body != null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
            request.Content.Headers.ContentType = new("application/json");
        }
        var (code, data) = await Http.Send(http, request, Http.Answer);
        Check(code, data);
        return data;
    }

    /// <summary>No network: the sync tries again by itself; something the owner just asked for has to be asked again.</summary>
    public sealed class Offline() : Exception("You're offline.");

    /// <summary>An answer that wasn't a success, with what the API said about it.</summary>
    public sealed class Failure(int code, string? message) : Exception
    {
        public int Code { get; } = code;
        private string? ApiMessage { get; } = message;

        public override string Message => Reason ?? "Something went wrong on Tally's side. Try again in a moment.";

        /// <summary>What the API said, when it's something a person can act on: a refused sign-in, or a rule of its own.</summary>
        public string? Reason
        {
            get
            {
                if (Code == 401) return "Your sign-in wasn't accepted. Sign out (Account, in Tally's menu in the tray), then sign in again.";
                if (ApiMessage is not { Length: > 0 } said || Code >= 500) return null;
                return char.ToUpperInvariant(said[0]) + said[1..] + ".";
            }
        }
    }

    internal static void Check(int code, byte[] data)
    {
        if (code is >= 200 and < 300) return;
        string? message;
        try
        {
            message = JsonSerializer.Deserialize<ErrorBody>(data, Json)?.Error;
        }
        catch (JsonException)  // not the API's own answer: S3's XML, a proxy's page
        {
            message = null;
        }
        throw new Failure(code, message);
    }

    private sealed record ErrorBody(string? Error);

    internal static T Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Json) ?? throw new JsonException("null");

    private static IReadOnlyList<T> List<T>(byte[] data, string key)
    {
        using var json = JsonDocument.Parse(data);
        return json.RootElement.GetProperty(key).Deserialize<List<T>>(Json) ?? [];
    }

    private static string Iso(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>A meeting, as <c>GET /meetings</c> lists it.</summary>
/// <param name="State">uploading, queued, processing, ready, failed.</param>
/// <param name="People">Who spoke: their person ids.</param>
/// <param name="FolderIds">The owner's folders it's in, any number; Meetings has every meeting.</param>
/// <param name="UpdatedAt">The job's last change.</param>
/// <param name="EditedAt">The owner's last change: with UpdatedAt, when the meetings folder fetches it again.</param>
/// <param name="Rewriting">A rewrite of the summary without background voices asked for, not done yet.</param>
/// <param name="RewriteError">Why the last one didn't happen.</param>
public sealed record Meeting(
    string MeetingId, string State, string? Title = null, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null,
    double? Duration = null, string? Error = null, IReadOnlyList<string>? People = null, IReadOnlyList<string>? FolderIds = null,
    DateTimeOffset? UpdatedAt = null, DateTimeOffset? EditedAt = null, DateTimeOffset? Rewriting = null, string? RewriteError = null)
{
    /// <summary>On its way, for the 30 minutes the API gives it: a job that died without saying so doesn't hold it for good.</summary>
    public bool IsRewriting => Rewriting is { } asked && DateTimeOffset.UtcNow - asked < TimeSpan.FromMinutes(30);
}

/// <summary>One of the owner's folders; a null parent is at the top.</summary>
public sealed record Folder(string FolderId, string Name, string? Parent = null);

/// <summary>A meeting search found: where it matched (title, summary, people, transcript), a snippet, and for the
/// transcript the time.</summary>
public sealed record SearchHit(string MeetingId, string? Title, DateTimeOffset? StartedAt, string Where, string Snippet, double? At = null);

/// <summary>A meeting with what's ready of it: its results, and the owner's copy of the summary.</summary>
public sealed record Detail(Results? Results = null, Notes? Notes = null);

public sealed record Notes(Summary? Summary = null);

public sealed record Person(
    string PersonId, string Name, IReadOnlyList<string> Aliases, string? Role, string? Company, bool IsOwner,
    IReadOnlyList<Voiceprint> Voiceprints)
{
    /// <summary>The owner has confirmed this voice at least once, so matches to it are trusted.</summary>
    public bool Confirmed => Voiceprints.Any(v => v.Source == "user");

    /// <summary>"Head of finance, Acme", or null when neither is known.</summary>
    public string? Detail => string.Join(", ", new[] { Role, Company }.OfType<string>()) is { Length: > 0 } detail ? detail : null;
}

public sealed record Voiceprint(string Source);

/// <summary>A file the owner added to a meeting, with a link to download it, valid for an hour.</summary>
public sealed record MeetingFile(string FileId, string Name, long Size, DateTimeOffset AddedAt, Uri Url);

/// <summary>Where to PUT a file: a presigned URL, and the headers it was signed with.</summary>
public sealed record Upload(Uri UploadUrl, IReadOnlyDictionary<string, string> UploadHeaders);
