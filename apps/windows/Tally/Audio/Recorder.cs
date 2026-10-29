using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Tally;

/// <summary>
/// Records a meeting: the default microphone, and everything the PC plays except Tally itself (process loopback,
/// excluding Tally's own process tree), together in one 2-channel file: channel 1 the mic, channel 2 the system
/// audio (docs/architecture.md), as the Mac's Recorder does.
///
/// The two captures run on their own device clocks, and the loopback delivers nothing while nothing plays. So a
/// writer thread keeps time by the wall clock instead: every 20 ms it writes the frames that are due, taking each
/// channel's from what its capture delivered and padding with silence when there isn't enough, and dropping a
/// backlog that grows (drift, or a capture that bursts). A quiet PC, a lost mic and a pause need nothing special.
/// A capture that isn't recording (no microphone, one that couldn't be opened, one that stopped with an error) is
/// opened again a second later, as is the microphone when the default one changes, and the same file carries on.
///
/// It writes 16-bit PCM WAV and updates the header every second, so a crash leaves a file that plays up to then;
/// <see cref="Encode"/> repairs the header anyway before making the m4a. If the file can't be written (a full disk),
/// or the recording reaches its longest, it stops writing and says why in its levels, and the app ends the recording.
/// </summary>
internal sealed class Recorder : IRecorder
{
    private const int SampleRate = 48_000;
    private const int MaxBacklog = SampleRate / 5;  // 200 ms queued: drift or a burst, trimmed back to Target
    private const int Target = SampleRate / 20;  // 50 ms kept in hand, so a late buffer doesn't become a gap
    // A WAV's sizes are 32-bit: 4 GB is 6 h 12 min of this file. The app stops a recording at 4 hours by the clock first;
    // this is the file's own guard, at the same length, should that ever not happen
    private const long MaxFrames = SampleRate * 3600L * 4;

    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
    private readonly object gate = new();  // the channels, the file, and what's said in the levels
    private readonly SemaphoreSlim opening = new(1, 1);  // the captures open and close one at a time
    private readonly Channel mic = new(), system = new();
    private readonly MMDeviceEnumerator devices = new();
    private MMDeviceNotificationClient? watcher;
    private Timer? keeper;
    private WasapiRecorder? micCapture, systemCapture;
    // Opened again at the next look: the mic when the default one changed (a headset), either when it stopped with an error
    private volatile bool reopenMic, reopenSystem;
    private WaveFileWriter? file;
    private Thread? writer;
    private volatile bool running, paused;
    private long written, skipped;  // frames in the file, and the time it left out (the PC asleep)
    private readonly Stopwatch clock = new();
    private bool lost, systemLost;
    private string? failed;

    public RecorderLevels Levels
    {
        get { lock (gate) return new(mic.Level, system.Level, lost, failed, systemLost); }
    }

    public async Task Start(string path)
    {
        file = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 2));
        running = true;
        // Off the UI thread: opening may wait for it, and it may be waiting for this
        await Task.Run(() => Open(asked: true));
        watcher = devices.CreateNotificationClient(false);
        // Raised on Core Audio's thread, which mustn't block: the next look reopens the mic
        watcher.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Capture && e.Role == Role.Console) reopenMic = true; };
        keeper = new Timer(state => _ = Open(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        clock.Start();
        writer = new Thread(Write) { IsBackground = true, Name = "Tally recorder", Priority = ThreadPriority.AboveNormal };
        writer.Start();
    }

    /// <summary>Stops listening but keeps the file open: a recording that followed a call can still go on.</summary>
    public void Pause()
    {
        lock (gate) { paused = true; clock.Stop(); }
        CloseCaptures();
        lock (gate) { mic.Clear(); system.Clear(); }
    }

    public async Task Resume()
    {
        try
        {
            await Task.Run(() => Open(asked: true));
        }
        catch (Exception e)
        {
            CloseCaptures();  // still paused: nothing it opened may listen meanwhile
            throw new AppError($"Tally couldn't carry on recording ({e.Message}).");
        }
        lock (gate) { paused = false; clock.Start(); }
    }

    /// <summary>Stops and closes the file; also after a start that failed part way, so nothing it opened stays open.</summary>
    public void Stop()
    {
        running = false;
        keeper?.Dispose();
        watcher?.Dispose();
        writer?.Join();
        CloseCaptures();
        lock (gate)
        {
            try { file?.Dispose(); }
            catch (IOException) { }  // a full disk: Encode puts the header right from the file's length
            file = null;
        }
        devices.Dispose();
    }

    /// <summary>
    /// Opens the captures that aren't recording, and the default microphone in place of the old one when it changed.
    /// Asked for (a start, a resume), a failure is thrown, to be said; otherwise it's tried again a second later. Never
    /// while stopped or paused.
    /// </summary>
    private async Task Open(bool asked = false)
    {
        await opening.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!running || (paused && !asked)) return;
            if (reopenMic || micCapture is null)
            {
                Close(ref micCapture);
                lock (gate) mic.Clear();
                reopenMic = false;
                try { OpenMic(); }
                catch (Exception) when (!asked) { lock (gate) lost = true; }  // whatever it was, the next look tries again
            }
            if (reopenSystem || systemCapture is null)
            {
                Close(ref systemCapture);
                reopenSystem = false;
                try { await OpenSystem().ConfigureAwait(false); }
                catch (Exception) when (!asked) { lock (gate) systemLost = true; }  // said, and the next look tries again
            }
        }
        finally
        {
            opening.Release();
        }
    }

    private void OpenMic()
    {
        MMDevice device;
        try { device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            lock (gate) lost = true;  // no microphone: the menu says nothing is recorded until one is connected
            return;
        }
        var capture = new WasapiRecorderBuilder().WithDevice(device).WithFormat(CaptureFormat).Build();
        micCapture = Started(capture, mic, () =>
        {
            // The audio service itself may have gone (restarted): the loopback never says so, but records nothing
            // after (measured), so it's opened again with the mic
            reopenMic = reopenSystem = true;
            lock (gate) lost = true;
        });
        lock (gate) lost = false;
    }

    private async Task OpenSystem()
    {
        var capture = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithFormat(CaptureFormat)
            .BuildAsync().ConfigureAwait(false);
        systemCapture = Started(capture, system, () =>
        {
            reopenSystem = true;
            lock (gate) systemLost = true;
        });
        lock (gate) systemLost = false;
    }

    /// <summary>Starts a capture into its channel, calling <paramref name="failed"/> if it stops with an error; one
    /// that can't start is disposed.</summary>
    private static WasapiRecorder Started(WasapiRecorder capture, Channel channel, Action failed)
    {
        var format = capture.WaveFormat;
        // WASAPI's silent packets may hold anything: they're zeros by definition
        capture.DataAvailable += (buffer, flags, _, _) => channel.Add(buffer, flags.HasFlag(AudioClientBufferFlags.Silent), format);
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) failed(); };
        try { capture.StartRecording(); }
        catch
        {
            capture.Dispose();
            throw;
        }
        return capture;
    }

    private void CloseCaptures()
    {
        opening.Wait();
        try
        {
            Close(ref micCapture);
            Close(ref systemCapture);
        }
        finally
        {
            opening.Release();
        }
    }

    /// <summary>Disposing stops the capture and waits for its thread.</summary>
    private static void Close(ref WasapiRecorder? capture)
    {
        capture?.Dispose();
        capture = null;
    }

    private void Write()
    {
        var file = this.file!;  // set before this thread starts, and closed only once it has ended
        var buffer = new byte[SampleRate / 10 * 4];
        var micFrames = new float[SampleRate / 10];
        var systemFrames = new float[SampleRate / 10];
        var lastFlush = clock.Elapsed;
        try
        {
            while (running)
            {
                Thread.Sleep(20);
                lock (gate)
                {
                    if (paused) continue;
                    if (written >= MaxFrames)
                    {
                        failed = "Tally stopped the recording at 4 hours, the longest it records.";
                        return;
                    }
                    var due = (long)(clock.Elapsed.TotalSeconds * SampleRate) - skipped - written;
                    // More than a second due means this thread didn't run (the PC slept): that's a gap, not silence to write
                    if (due > SampleRate)
                    {
                        skipped += due - Target;
                        due = Target;
                    }
                    while (due > 0)
                    {
                        var frames = (int)Math.Min(due, micFrames.Length);
                        mic.Take(micFrames.AsSpan(0, frames));
                        system.Take(systemFrames.AsSpan(0, frames));
                        for (var i = 0; i < frames; i++)
                        {
                            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4), Pcm(micFrames[i]));
                            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4 + 2), Pcm(systemFrames[i]));
                        }
                        file.Write(buffer, 0, frames * 4);
                        written += frames;
                        due -= frames;
                    }
                    if (clock.Elapsed - lastFlush > TimeSpan.FromSeconds(1))
                    {
                        file.Flush();  // the header's sizes too, so a crash leaves a playable file
                        lastFlush = clock.Elapsed;
                    }
                }
            }
        }
        catch (Exception)  // on this thread, anything uncaught would end Tally: a full disk, a drive gone
        {
            lock (gate) failed = "Tally stopped recording because it couldn't save any more of it. Make room on your disk if it's full.";
        }
    }

    private static short Pcm(float sample) => (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

    /// <summary>Makes the 2-channel AAC 128 kbps m4a that is uploaded, from a WAV a crash may have left with stale sizes.
    /// NotSupportedException when Windows can't: an N edition without its Media Feature Pack.</summary>
    public static void Encode(string wav, string m4a)
    {
        RepairHeader(wav);
        using var reader = new WaveFileReader(wav);
        // Without the pack there's no Media Foundation, or no AAC encoder in it
        try
        {
            using var aac = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, reader.WaveFormat, 128_000)
                            ?? throw new NotSupportedException("Windows has no AAC encoder.");
        }
        catch (DllNotFoundException e)
        {
            throw new NotSupportedException("Windows has no Media Foundation.", e);
        }
        MediaFoundationEncoder.EncodeToAac(reader, m4a, 128_000);
        // On the disk before the WAV is deleted: a power cut after can't leave the recording only as a file of zeros
        using var encoded = new FileStream(m4a, FileMode.Open, FileAccess.ReadWrite);
        encoded.Flush(flushToDisk: true);
    }

    /// <summary>The RIFF and data sizes from the file's length: what a crash before the last header update leaves wrong.</summary>
    private static void RepairHeader(string wav)
    {
        using var stream = new FileStream(wav, FileMode.Open, FileAccess.ReadWrite);
        if (stream.Length < 44) return;
        var header = new byte[44];
        stream.ReadExactly(header);
        // WaveFileWriter writes a plain 44-byte header for PCM: RIFF size at 4, "data" at 36, its size at 40
        if (System.Text.Encoding.ASCII.GetString(header, 36, 4) != "data") return;
        var data = (stream.Length - 44) / 4 * 4;
        stream.Position = 4;
        stream.Write(BitConverter.GetBytes((uint)(data + 36)));
        stream.Position = 40;
        stream.Write(BitConverter.GetBytes((uint)data));
    }

    /// <summary>One capture's samples, mixed down to mono, waiting for the writer; and its level, which is silence once
    /// it has delivered nothing for a while (the loopback sends nothing while nothing plays).</summary>
    private sealed class Channel
    {
        private readonly ConcurrentQueue<float[]> chunks = new();
        private float[]? current;
        private int offset, queued;
        private float level = -120;
        private long heard;  // when it last delivered, in ms

        public float Level => Environment.TickCount64 - Volatile.Read(ref heard) > 200 ? -120 : level;

        public void Add(ReadOnlySpan<byte> bytes, bool silent, WaveFormat format)
        {
            var channels = format.Channels;
            var frames = bytes.Length / (4 * channels);
            var mono = new float[frames];
            double sum = 0;
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
            for (var i = 0; i < frames && !silent; i++)
            {
                float s = 0;
                for (var c = 0; c < channels; c++) s += samples[i * channels + c];
                mono[i] = s / channels;
                sum += mono[i] * mono[i];
            }
            level = frames == 0 ? -120 : (float)Math.Max(-120, 10 * Math.Log10(sum / frames + 1e-12));
            Volatile.Write(ref heard, Environment.TickCount64);
            chunks.Enqueue(mono);
            Interlocked.Add(ref queued, frames);
        }

        /// <summary>Fills <paramref name="into"/> with the next frames, silence where there aren't enough; a backlog over
        /// <see cref="MaxBacklog"/> is dropped first, down to <see cref="Target"/>.</summary>
        public void Take(Span<float> into)
        {
            if (Volatile.Read(ref queued) > MaxBacklog) Move(Volatile.Read(ref queued) - Target, []);
            var filled = Move(into.Length, into);
            into[filled..].Clear();
        }

        /// <summary>Takes up to <paramref name="frames"/> frames off the queue, into <paramref name="into"/> unless it's
        /// empty (dropped); how many there were.</summary>
        private int Move(int frames, Span<float> into)
        {
            var moved = 0;
            while (moved < frames)
            {
                if (current is null || offset == current.Length)
                {
                    if (!chunks.TryDequeue(out current)) break;
                    offset = 0;
                }
                var n = Math.Min(frames - moved, current.Length - offset);
                if (!into.IsEmpty) current.AsSpan(offset, n).CopyTo(into[moved..]);
                offset += n;
                moved += n;
                Interlocked.Add(ref queued, -n);
            }
            return moved;
        }

        public void Clear()
        {
            while (chunks.TryDequeue(out _)) { }
            current = null;
            offset = 0;
            Volatile.Write(ref queued, 0);
            level = -120;
        }
    }
}
