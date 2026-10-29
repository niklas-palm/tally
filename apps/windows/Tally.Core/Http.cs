namespace Tally;

/// <summary>
/// HTTP for the API, sign-in and S3. Getting no answer at all (no network, DNS, a captive portal, a minute's wait) is
/// <see cref="Api.Offline"/>. HttpClient has no idle timeout, so a transfer without progress for a minute fails instead
/// of holding the sync.
/// </summary>
internal static class Http
{
    /// <summary>How long an API or sign-in request has for its whole answer.</summary>
    public static readonly TimeSpan Answer = TimeSpan.FromSeconds(60);

    /// <summary>How long an upload or a download may go without progress.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);

    /// <summary>A request and its whole answer, within <paramref name="timeout"/>.</summary>
    public static async Task<(int Code, byte[] Data)> Send(HttpClient http, HttpRequestMessage request, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception e) when (NoAnswer(e))
        {
            throw new Api.Offline();
        }
    }

    /// <summary>A file PUT to a presigned URL, with the headers it was signed with.</summary>
    public static async Task Upload(HttpClient http, Upload upload, string path)
    {
        using var cts = new CancellationTokenSource(Idle);
        await using var file = File.OpenRead(path);
        using var content = new StreamContent(new Watched(file, cts));
        content.Headers.ContentLength = file.Length;
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl) { Content = content };
        foreach (var (name, value) in upload.UploadHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            Api.Check((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(cts.Token));
        }
        catch (Exception e) when (NoAnswer(e))
        {
            throw new Api.Offline();
        }
    }

    /// <summary>A link's file, downloaded to a temporary file of its own for the caller to move into place. Anything
    /// but a 200 is an <see cref="Api.Failure"/>.</summary>
    public static async Task<string> Download(HttpClient http, Uri url)
    {
        using var cts = new CancellationTokenSource(Idle);
        var temporary = Path.Combine(Path.GetTempPath(), $"tally-{Guid.NewGuid():N}");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode != 200) throw new Api.Failure((int)response.StatusCode, null);
            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var target = File.Create(temporary);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
            {
                cts.CancelAfter(Idle);
                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            }
            target.Flush(flushToDisk: true);  // on the disk before it's moved into place, as Disk.Write does
            return temporary;
        }
        catch (Exception e) when (NoAnswer(e))
        {
            File.Delete(temporary);
            throw new Api.Offline();
        }
    }

    private static bool NoAnswer(Exception e) => e is HttpRequestException or OperationCanceledException or IOException;

    /// <summary>A file being uploaded: each read is progress, and puts the idle timeout off again.</summary>
    private sealed class Watched(Stream inner, CancellationTokenSource cts) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cts.CancelAfter(Idle);
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            cts.CancelAfter(Idle);
            return await inner.ReadAsync(buffer, token);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
