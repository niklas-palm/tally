using System.Runtime.InteropServices;

namespace Tally;

/// <summary>
/// The refresh token, kept in Windows' Credential Manager as a generic credential of the signed-in Windows user,
/// as the Mac keeps it in the login keychain (ISecrets). Windows protects it with the user's own key.
/// </summary>
internal sealed class Secrets : ISecrets
{
    private const int Generic = 1, LocalMachine = 2;

    public byte[]? Load(string name)
    {
        if (!CredRead(Target(name), Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var data = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, data, 0, data.Length);
            return data;
        }
        finally { CredFree(pointer); }
    }

    public void Save(string name, byte[] data)
    {
        var blob = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, blob, data.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = Target(name), CredentialBlobSize = data.Length, CredentialBlob = blob,
                Persist = LocalMachine, UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0)) throw new AppError("Tally couldn't keep your sign-in in Credential Manager.");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public void Delete(string name) => CredDelete(Target(name), Generic, 0);

    private static string Target(string name) => $"Tally/{name}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
