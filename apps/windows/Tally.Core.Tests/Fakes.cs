using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Tally.Tests;

/// <summary>The API, the sign-in provider and S3 as the tests answer them: a route ("GET https://api.test/meetings") to a reply, every
/// request kept to look at, and a switch for being offline.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public const string Api = "https://api.test";
    public const string Token = "https://auth.test/token";

    public sealed record Request(string Route, string Body, HttpRequestMessage Message);

    private readonly ConcurrentDictionary<string, Func<Request, (int, string)>> routes = new();
    public ConcurrentQueue<Request> Requests { get; } = new();
    public bool Offline { get; set; }

    public void On(string route, int status, string body = "{}") => routes[route] = _ => (status, body);
    public void On(string route, Func<Request, (int, string)> reply) => routes[route] = reply;

    /// <summary>The provider's token endpoint, handing out an access token for this person.</summary>
    public void SignedIn(string sub = "user-1", string name = "Ada Lovelace", int expiresIn = 300)
    {
        var token = Jwt($$"""{"sub": "{{sub}}", "name": "{{name}}"}""");
        On($"POST {Token}", 200, $$"""{"access_token": "{{token}}", "expires_in": {{expiresIn}}, "refresh_token": "refresh-2"}""");
    }

    public IEnumerable<string> Routes => Requests.Select(r => r.Route);

    /// <summary>An access token carrying these claims (JSON), unsigned: the app only reads them.</summary>
    public static string Jwt(string claims) => $"{Part("{}")}.{Part(claims)}.sig";

    private static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    {
        var body = message.Content is null ? "" : await message.Content.ReadAsStringAsync(token);
        var request = new Request($"{message.Method} {message.RequestUri!.GetLeftPart(UriPartial.Path)}", body, message);
        Requests.Enqueue(request);
        if (Offline) throw new HttpRequestException("No network");
        var (status, reply) = routes.TryGetValue(request.Route, out var answer) ? answer(request) : (404, """{"error": "not found"}""");
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}

/// <summary>The operating system as the tests have it: nothing real, everything recorded.</summary>
internal sealed class FakePlatform : IAudio, IBrowser, ISecrets, INotifications, IClipPlayer, ILoginItem, IShell, IInstaller, ICrashReports, ILog
{
    public Dictionary<string, byte[]> Secrets { get; } = [];
    public List<(string Id, string Title, string Body, string? Meeting)> Posted { get; } = [];
    public List<string> Logged { get; } = [];
    public List<AudioUser> AudioUsers { get; } = [];
    public List<CrashReport> CrashReports { get; } = [];
    public Func<Uri, Uri?> Authorize { get; set; } = _ => null;

    /// <summary>Windows N without its Media Feature Pack: nothing encodes AAC.</summary>
    public bool NoEncoder { get; set; }

    public Platform Bundle(string data) => new(this, this, this, this, this, this, this, this, this, this, data, Path.Combine(data, "Cache"));

    public Exception? Refusal { get; set; }

    public Task<IRecorder> Start(string path) => Refusal is null ? Task.FromResult<IRecorder>(new FakeRecorder(path)) : Task.FromException<IRecorder>(Refusal);

    /// <summary>Encoding waits while it's reset: a long recording's encode, still going.</summary>
    public ManualResetEventSlim EncodeWaits { get; } = new(true);

    /// <summary>An m4a for any WAV with something in it, as the real encoder would make.</summary>
    public void Encode(string wav, string m4a)
    {
        EncodeWaits.Wait();
        if (Recording.Frames(wav) is not > 0) throw new InvalidDataException("Nothing was recorded.");
        if (NoEncoder) throw new NotSupportedException("Windows has no AAC encoder.");
        File.WriteAllBytes(m4a, [1, 2, 3]);
    }

    public Exception? UsersFail { get; set; }  // thrown by the next look, once
    public IReadOnlyList<AudioUser> Users()
    {
        if (UsersFail is { } failure) { UsersFail = null; throw failure; }
        return AudioUsers;
    }

    Task<Uri?> IBrowser.Authorize(Uri url) => Task.FromResult(Authorize(url));

    public byte[]? Load(string name) => Secrets.GetValueOrDefault(name);
    public void Save(string name, byte[] data) => Secrets[name] = data;
    public void Delete(string name) => Secrets.Remove(name);

    public void Post(string id, string title, string body, string? meeting) => Posted.Add((id, title, body, meeting));

    public bool NoPlayer { get; set; }  // Windows N without its Media Feature Pack
    public void Toggle(IReadOnlyList<Uri> clips) { if (NoPlayer) throw new NotSupportedException("Windows has no media player."); }
    public bool IsPlaying(IReadOnlyList<Uri> clips) => false;
    public void Stop() { }
    public event Action? Changed { add { } remove { } }

    public bool Enabled { get; private set; }
    public void Register() => Enabled = true;
    public void Unregister() => Enabled = false;

    public Task<string?> ChooseFolder(string prompt) => Task.FromResult<string?>(null);
    public Task<bool> Confirm(string title, string message, string confirm, string cancel) => Task.FromResult(false);
    public byte[]? Clipboard { get; set; }
    public bool ClipboardBusy { get; set; }  // another program has it open
    public Task<byte[]?> ClipboardImage() => ClipboardBusy ? throw new System.Runtime.InteropServices.COMException("OpenClipboard failed") : Task.FromResult(Clipboard);
    public void Open(string target) { }

    public string LogPath => "";
    public bool InstallerUnreachable { get; set; }  // the install page can't be reached: offline, or the site down
    public Task<int> Install(Uri site) => InstallerUnreachable ? throw new Api.Offline() : Task.FromResult(0);

    public IReadOnlyList<CrashReport> Find() => CrashReports;

    public void Write(string category, string message) => Logged.Add($"{category}: {message}");
}

internal sealed class FakeRecorder(string path) : IRecorder
{
    public string Path { get; } = path;
    public RecorderLevels Levels { get; set; } = new();

    /// <summary>What Resume does: at once, by default.</summary>
    public Func<Task> Resuming { get; set; } = () => Task.CompletedTask;

    public void Pause() { }
    public Task Resume() => Resuming();
    public void Stop() { }
}

/// <summary>A folder of the test's own, gone when it's done.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tally-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>
/// A UI thread for the tests: AppModel's awaits come back to the thread that started them, one continuation at a time,
/// as they do in the app. Work still queued when the test's own task ends (a sync started by the way) is dropped.
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new Context();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Pump();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class Context : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> queue = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            try
            {
                queue.Add((callback, state));
            }
            catch (InvalidOperationException) { }  // the test is over
        }

        public void Pump()
        {
            while (queue.TryTake(out var work, Timeout.Infinite))
            {
                work.Item1(work.Item2);
                if (queue.IsAddingCompleted) return;
            }
        }

        public void Complete() => Post(_ => queue.CompleteAdding(), null);
    }
}

/// <summary>Meetings as the API sends them, for the tests to build on.</summary>
internal static class Fixtures
{
    public const string Summary = """
        {"title": "Budget", "overview": "{S1} and {S2} agreed with {S3}.", "summary": [], "decisions": [],
         "action_items": [], "open_questions": [], "topics": [], "attendees": []}
        """;

    public static Results Results(string json) => Api.Decode<Results>(Encoding.UTF8.GetBytes(json));

    public static Person Person(string id, string name, string? role = null, string? company = null, bool owner = false, params string[] sources) =>
        new(id, name, [], role, company, owner, sources.Select(s => new Voiceprint(s)).ToList());

    /// <summary>A WAV as the recorder leaves it after a crash: its sizes never written, and this many frames after them.</summary>
    public static void Wav(string path, int frames)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);  // PCM
        writer.Write((ushort)2);
        writer.Write(48_000u);
        writer.Write(48_000u * 4);
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(0u);
        writer.Write(new byte[frames * 4]);
    }
}
