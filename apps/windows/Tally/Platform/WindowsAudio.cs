using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Tally;

/// <summary>
/// The microphone, the recorder, the encoder, and who else is using audio, from Windows' Core Audio (IAudio).
/// Windows asks nobody for the microphone: a desktop app may use it unless the owner turned that off in Settings,
/// and then opening it fails with access denied, which <see cref="Start"/> reports as such.
/// </summary>
internal sealed class WindowsAudio : IAudio
{
    private const int AccessDenied = unchecked((int)0x80070005);

    public async Task<IRecorder> Start(string path)
    {
        var recorder = new Recorder();
        try
        {
            await recorder.Start(path);
            return recorder;
        }
        catch (Exception e)
        {
            recorder.Stop();  // whatever it opened: the file, the microphone
            // Access denied comes as either, from Core Audio or from .NET
            if (e is UnauthorizedAccessException || e.HResult == AccessDenied) throw new UnauthorizedAccessException("Tally needs your microphone to record.", e);
            throw new AppError($"Tally couldn't start recording ({e.Message}).");
        }
    }

    public void Encode(string wav, string m4a) => Recorder.Encode(wav, m4a);

    /// <summary>
    /// Every process with an audio session that is active now, on any microphone (input) or any output device:
    /// what Core Audio's process list gives the Mac. A session only counts while it streams, so an app that has
    /// merely opened a device, or one that's paused, isn't on it.
    /// </summary>
    public IReadOnlyList<AudioUser> Users()
    {
        using var devices = new MMDeviceEnumerator();  // made on this thread: one from the UI thread would call back to it
        var input = Active(devices, DataFlow.Capture);
        var output = Active(devices, DataFlow.Render);
        return input.Union(output)
            .Where(pid => pid != Environment.ProcessId && pid != 0)
            .Select(pid => new AudioUser(pid, input.Contains(pid), output.Contains(pid), AppNames.Of(pid)))
            .ToList();
    }

    private static HashSet<int> Active(MMDeviceEnumerator devices, DataFlow flow)
    {
        var pids = new HashSet<int>();
        // Failures here (the audio service restarting) reach ObserveCalls, which skips the look: counting it as quiet
        // would end a call
        foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateActive && !session.IsSystemSoundsSession)
                            pids.Add((int)session.GetProcessID);
                    }
                }
                catch (COMException) { }  // a device going away mid-look: it's gone next second
            }
        }
        return pids;
    }
}

/// <summary>
/// What to call a process using audio, as the drawer says it ("Zoom is using your microphone"): its product's name,
/// or for a helper process (a browser's audio service, Teams' WebView2) the app that started it.
/// </summary>
internal static class AppNames
{
    // By id and start time: Windows gives a gone process's id to a new one
    private static readonly Dictionary<(int, DateTime), string?> Known = [];
    private static readonly string[] Helpers = ["msedgewebview2", "webview2", "crashpad", "helper"];

    public static string? Of(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var key = (pid, process.StartTime);
            if (!Known.TryGetValue(key, out var name)) Known[key] = name = Lookup(pid, 0);
            return name;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;  // gone, or not ours to look at (a service): unnamed
        }
    }

    private static string? Lookup(int pid, int depth)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var file = process.MainModule?.FileName;
            var info = file is null ? null : FileVersionInfo.GetVersionInfo(file);
            var exe = Path.GetFileNameWithoutExtension(file ?? process.ProcessName).ToLowerInvariant();
            if (depth < 3 && Helpers.Any(exe.Contains) && Parent(pid) is { } parent and > 0) return Lookup(parent, depth + 1) ?? Name(info, process);
            return Name(info, process);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;  // gone, not ours to look at (a service), or its file unreadable (moved, a share gone): unnamed
        }
    }

    private static string? Name(FileVersionInfo? info, Process process)
    {
        var name = info?.ProductName?.Trim();
        if (string.IsNullOrEmpty(name) || name.StartsWith("Microsoft® Windows®", StringComparison.Ordinal)) name = info?.FileDescription?.Trim();
        if (string.IsNullOrEmpty(name)) name = process.ProcessName;
        return name switch
        {
            "Zoom Workplace" or "Zoom Meetings" => "Zoom",
            "Microsoft Teams" or "Microsoft Teams (work or school)" => "Teams",
            _ => name,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref BasicInformation info, int size, out int returned);

    private static int? Parent(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var info = new BasicInformation();
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf(info), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
