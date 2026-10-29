using System.Globalization;

namespace Tally;

/// <summary>Search results' snippets with the words searched for in bold, as Mail marks them: whatever their case or accents.</summary>
public static class Snippets
{
    public sealed record Run(string Text, bool Bold);

    public static IReadOnlyList<Run> Marked(string snippet, string query)
    {
        var bold = new bool[snippet.Length];
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var from = 0;
            while (from < snippet.Length)
            {
                var at = compare.IndexOf(snippet.AsSpan(from), word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var length);
                if (at < 0 || length == 0) break;
                Array.Fill(bold, true, from + at, length);
                from += at + length;
            }
        }
        var runs = new List<Run>();
        for (var start = 0; start < snippet.Length;)
        {
            var end = start;
            while (end < snippet.Length && bold[end] == bold[start]) end++;
            runs.Add(new(snippet[start..end], bold[start]));
            start = end;
        }
        return runs;
    }
}
