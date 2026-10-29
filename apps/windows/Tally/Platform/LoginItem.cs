using Microsoft.Win32;

namespace Tally;

/// <summary>
/// Tally as a startup app (ILoginItem): the current user's Run key, which Settings' Startup apps lists and can turn
/// off. Turned off there, it stays off until the owner turns it on again, here or there.
/// </summary>
internal sealed class LoginItem : ILoginItem
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Tally";

    public bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Run);
            if (run?.GetValue(Name) is null) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(Approved);
            // Startup apps keeps its switch here: an odd first byte means it's turned off
            return !(approved?.GetValue(Name) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1);
        }
    }

    public void Register()
    {
        using var run = Registry.CurrentUser.CreateSubKey(Run);
        run.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        using var approved = Registry.CurrentUser.CreateSubKey(Approved);
        approved.SetValue(Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }

    public void Unregister()
    {
        using var run = Registry.CurrentUser.CreateSubKey(Run);
        run.DeleteValue(Name, false);
    }
}
