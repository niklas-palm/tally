namespace Tally;

/// <summary>Meeting ids are ULIDs made by the app (docs/api.md), so they sort by time.</summary>
public static class Ulid
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Make(DateTimeOffset? at = null)
    {
        var ms = (ulong)(at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var chars = new char[26];
        for (var i = 9; i >= 0; i--, ms >>= 5) chars[i] = Crockford[(int)(ms & 31)];
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        for (var i = 10; i < 26; i++) chars[i] = Crockford[random[i - 10] & 31];
        return new string(chars);
    }

    public static bool Is(string text) => text.Length == 26 && text.All(Crockford.Contains);

    /// <summary>When a ULID was made: its first ten characters are the milliseconds.</summary>
    public static DateTimeOffset Date(string ulid)
    {
        var ms = 0L;
        foreach (var c in ulid[..10]) ms = ms << 5 | (long)Crockford.IndexOf(c);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
