using System.Diagnostics;

namespace Tally;

/// <summary>
/// Updates Tally from inside it: fetches the install page's script and runs it as a person would. The script builds the
/// new version, closes this one and opens the new one; its log's last line starts "FAILED: " when it stops.
/// </summary>
internal sealed class Installer(string logs) : IInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public string LogPath => Path.Combine(logs, "install.log");

    public async Task<int> Install(Uri site)
    {
        string script;
        try { script = await Http.GetStringAsync(new Uri(site, "install.ps1")); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new Api.Offline(); }
        // Through iex, not a script file: Group Policy's execution policy applies to files. Handed over in the
        // environment (it's about 10 KB, of the 32 KB one may hold)
        var start = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command \"iex $env:TALLY_INSTALLER\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["TALLY_INSTALLER"] = script;
        using var process = Process.Start(start) ?? throw new AppError("Tally couldn't start the installer.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
