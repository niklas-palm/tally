using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tally;

/// <summary><c>results/meeting.json</c> as the API returns it (docs/data.md): the summary, the speakers and the transcript.</summary>
/// <param name="SummaryWithout">The background voices the summary was last written without.</param>
public sealed record Results(
    string? Language, double Duration, Summary Summary, IReadOnlyDictionary<string, Speaker> Speakers,
    IReadOnlyList<Segment> Segments, IReadOnlyList<string>? SummaryWithout = null)
{
    /// <summary>Consecutive lines of one speaker as one paragraph: how the transcript reads.</summary>
    public IReadOnlyList<Paragraph> Paragraphs
    {
        get
        {
            var paragraphs = new List<Paragraph>();
            foreach (var segment in Segments)
            {
                if (paragraphs.Count > 0 && paragraphs[^1].Speaker == segment.Speaker)
                    paragraphs[^1] = paragraphs[^1] with { Text = paragraphs[^1].Text + " " + segment.Text };
                else
                    paragraphs.Add(new(segment.Speaker, segment.Start, segment.Text));
            }
            return paragraphs;
        }
    }

    /// <summary>Speaker ids in the order they first speak.</summary>
    public IReadOnlyList<string> SpeakerOrder
    {
        get
        {
            var first = new Dictionary<string, double>();
            foreach (var s in Segments) first.TryAdd(s.Speaker, s.Start);
            return Speakers.Keys.OrderBy(id => first.GetValueOrDefault(id, double.PositiveInfinity)).ThenBy(id => id, StringComparer.Ordinal).ToList();
        }
    }
}

public sealed record Paragraph(string Speaker, double Start, string Text);

/// <summary>
/// The agent's summary, or the owner's copy of it: the same shape, edited in place. Two summaries are
/// equal when they'd be sent the same: a record would compare its lists by reference.
/// </summary>
/// <param name="Points">Its points, "summary" in JSON: a member can't share its record's name.</param>
public sealed record Summary(
    string Title, string Overview, [property: JsonPropertyName("summary")] IReadOnlyList<string> Points, IReadOnlyList<Decision> Decisions,
    IReadOnlyList<ActionItem> ActionItems, IReadOnlyList<OpenQuestion> OpenQuestions, IReadOnlyList<Topic> Topics,
    IReadOnlyList<Attendee> Attendees)
{
    /// <summary>What's sent: points the owner emptied are gone. While they're on the page they stay, as places to type.</summary>
    [JsonIgnore] public Summary Cleaned => this with
    {
        Points = Points.Where(p => !IsBlank(p)).ToList(),
        Decisions = Decisions.Where(d => !IsBlank(d.Text)).ToList(),
        ActionItems = ActionItems.Where(a => !IsBlank(a.Text)).ToList(),
        OpenQuestions = OpenQuestions.Where(q => !IsBlank(q.Text)).ToList(),
        Topics = Topics.Select(t => t with { Notes = t.Notes.Where(n => !IsBlank(n)).ToList() })
            .Where(t => !IsBlank(t.Title) || t.Notes.Count > 0 || t.Outcome is { } outcome && !IsBlank(outcome))  // one emptied of everything
            .ToList(),
    };

    public bool Equals(Summary? other) => other is not null && Text(this) == Text(other);

    public override int GetHashCode() => Text(this).GetHashCode();

    private static string Text(Summary summary) => JsonSerializer.Serialize(summary, Api.Json);

    private static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);
}

public sealed record Decision(string Text, IReadOnlyList<string> By);

public sealed record ActionItem(string? Owner, string Text, string? Due = null, bool? Done = null);

public sealed record OpenQuestion(string Text, IReadOnlyList<string> RaisedBy);

public sealed record Topic(string Title, double Start, double End, IReadOnlyList<string> Notes, string? Outcome = null);

public sealed record Attendee(string Speaker, string Role, string Contribution);

/// <param name="Candidates">The owner's people whose voices come closest, closest first.</param>
public sealed record Speaker(double TalkTime, IReadOnlyList<Uri>? ClipUrls = null, Identity? Identity = null, IReadOnlyList<Candidate>? Candidates = null);

/// <summary>A voice that sounds like this speaker's: cosine similarity, 0.65 or more is very close (measured in docs/research.md).</summary>
public sealed record Candidate(string PersonId, double Score)
{
    public string? Closeness => Score >= 0.65 ? "Very close" : Score >= 0.5 ? "Close" : Score >= 0.35 ? "A little alike" : null;
}

/// <param name="By">agent or owner.</param>
/// <param name="Background">The owner said it wasn't in the meeting: a voice nearby.</param>
public sealed record Identity(string? Name = null, string? PersonId = null, string? By = null, bool? Unknown = null, bool? Background = null);

public sealed record Segment(double Start, double End, string Speaker, string Text);

public enum Review
{
    /// <summary>The owner said so, or the voice is one they've confirmed before.</summary>
    Confirmed,
    /// <summary>The agent's call: "Is this Alex?"</summary>
    Suggested,
    /// <summary>Nobody knows yet: "Who's this?"</summary>
    Unknown,
    /// <summary>The owner said it's nobody they know.</summary>
    NotKnown,
    /// <summary>The owner said it wasn't in the meeting at all: set aside.</summary>
    Background,
}

/// <summary>
/// What to call each speaker in a meeting, with the people as they are now: a person renamed since the meeting shows
/// under their new name, and the summary's <c>{S2}</c> tokens are filled in the same way.
/// </summary>
public sealed partial class Names(Results results, IReadOnlyDictionary<string, Person> people)
{
    /// <summary>Someone who said less than this isn't asked about (a hello, a remark across the room): too little to be
    /// worth stopping for, and to learn a voice from. They're still among who was there, to name if the owner likes.</summary>
    public const double Brief = 20;

    public string Name(string speaker)
    {
        var identity = results.Speakers.GetValueOrDefault(speaker)?.Identity;
        var name = identity?.PersonId is { } id ? people.GetValueOrDefault(id)?.Name : identity?.Name;  // a person deleted: unknown again
        return name ?? $"Speaker {speaker[1..]}";
    }

    /// <summary>What an attendee does: the role and company the owner keeps for them in People, when there are any, so a
    /// correction there reaches every meeting; otherwise how the meeting described them, with speakers' names in.</summary>
    public string Role(Attendee attendee) => Person(attendee.Speaker)?.Detail ?? Render(attendee.Role);

    public Person? Person(string speaker) =>
        results.Speakers.GetValueOrDefault(speaker)?.Identity?.PersonId is { } id ? people.GetValueOrDefault(id) : null;

    /// <summary>Whether to ask the owner who this is: not settled yet, and said enough.</summary>
    public bool Asks(string speaker) =>
        ReviewOf(speaker) is Review.Suggested or Review.Unknown && (results.Speakers.GetValueOrDefault(speaker)?.TalkTime ?? 0) >= Brief;

    /// <summary>Who was in the meeting, in the order they first spoke: everyone but a voice set aside as background.</summary>
    public IReadOnlyList<string> Participants => results.SpeakerOrder.Where(s => ReviewOf(s) != Review.Background).ToList();

    public Review ReviewOf(string speaker)
    {
        if (results.Speakers.GetValueOrDefault(speaker)?.Identity is not { } identity) return Review.Unknown;
        if (identity.Background == true) return Review.Background;
        if (identity.Unknown == true) return identity.By == "owner" ? Review.NotKnown : Review.Unknown;
        if (identity.PersonId is { } id && !people.ContainsKey(id)) return Review.Unknown;  // a person the owner deleted since
        if (identity.By == "owner" || Person(speaker)?.Confirmed == true) return Review.Confirmed;
        return Review.Suggested;
    }

    public string Render(string text) => Token().Replace(text, m => Name(m.Groups[1].Value));

    /// <summary>The other way, for what the owner types: a speaker's name, as shown, back to its token, so a later rename
    /// still reaches the owner's copy. Longest names first, whole words only ("Ann" isn't in "Anna").</summary>
    public string Tokenize(string text)
    {
        var found = text;
        foreach (var speaker in results.Speakers.Keys.OrderByDescending(s => Name(s).Length))
        {
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(Name(speaker)) + @"(?![\p{L}\p{N}])";
            found = Regex.Replace(found, pattern, _ => $"{{{speaker}}}");
        }
        return found;
    }

    [GeneratedRegex(@"\{(S\d+)\}")]
    private static partial Regex Token();
}
