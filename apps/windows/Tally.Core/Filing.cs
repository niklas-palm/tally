namespace Tally;

/// <summary>
/// Where a meeting probably belongs: the folder the owner usually files their other meetings with the
/// same people into, at least twice and more than half the time. Only for a ready meeting in no folder yet, so a
/// colleague who's in everything suggests nothing. The owner themselves never counts: they're in every meeting.
/// </summary>
public static class Filing
{
    /// <param name="People">The person ids it rests on.</param>
    public sealed record Suggestion(string Folder, IReadOnlyList<string> People);

    public static Suggestion? Suggest(Meeting meeting, IReadOnlyList<Meeting> meetings, string? owner)
    {
        if (meeting.FolderIds is { Count: > 0 } || meeting.State != "ready") return null;
        var here = (meeting.People ?? []).Where(p => p != owner).ToHashSet();
        var votes = new Dictionary<string, int>();
        var shared = new Dictionary<string, HashSet<string>>();
        foreach (var other in meetings.Where(m => m.MeetingId != meeting.MeetingId))
        {
            var common = here.Intersect(other.People ?? []).ToList();
            if (common.Count == 0) continue;
            foreach (var folder in other.FolderIds ?? [])
            {
                votes[folder] = votes.GetValueOrDefault(folder) + 1;
                if (!shared.TryGetValue(folder, out var people)) shared[folder] = people = [];
                people.UnionWith(common);
            }
        }
        if (votes.Count == 0) return null;
        var best = votes.MaxBy(v => v.Value);
        if (best.Value < 2 || best.Value * 2 <= votes.Values.Sum()) return null;
        return new(best.Key, shared[best.Key].Order(StringComparer.Ordinal).ToList());
    }
}
