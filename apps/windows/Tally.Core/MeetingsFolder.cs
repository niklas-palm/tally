using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tally;

/// <summary>
/// A copy of the owner's meetings in a folder they choose, for agents and tools on their PC: each meeting's summary and
/// transcript as Markdown, and everything as JSON, kept up to date from Tally (one way: the folder is a copy). An agent
/// needs no sign-in to read files, and the README tells it what's where.
///
/// Only what Tally made is ever changed: a meeting's folder is known by the meeting.json inside it. Every file is
/// written whole (to a temporary file, then moved into place), so nothing is read half-written.
/// </summary>
/// <param name="root">The folder the owner chose.</param>
/// <param name="dataFolder">Tally's own, where <see cref="Store"/> is.</param>
/// <param name="account">Whose meetings: the token's subject.</param>
public sealed class MeetingsFolder(string root, string dataFolder, string account, HttpClient http)
{
    public string Root { get; } = root;

    /// <summary>What this PC wrote, and from what, for this account: in Tally's data folder, not the folder.</summary>
    public string Store { get; } = Path.Combine(dataFolder, "meetings-folder", account);

    /// <summary>What's remembered of each meeting: the stamp it was fetched at, the folder it's in, what it was rendered with.</summary>
    public sealed record FolderState(Dictionary<string, Entry> Meetings);

    public sealed record Entry(string Stamp, string Directory, string Rendered);

    /// <summary>Everything one meeting's files are made of.</summary>
    /// <param name="Edited">The owner's copy, not the agent's.</param>
    /// <param name="Folders">The Tally folders it's in, by path.</param>
    /// <param name="Files">Names in its files folder.</param>
    public sealed record Source(Meeting Meeting, Results Results, Summary Summary, bool Edited, Names Names, IReadOnlyList<string> Folders, IReadOnlyList<string> Files);

    // — What's remembered

    public FolderState LoadState()
    {
        try
        {
            return JsonSerializer.Deserialize<FolderState>(File.ReadAllBytes(Path.Combine(Store, "state.json")), StateJson) ?? new([]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([]);
        }
    }

    public void Save(FolderState state) => Disk.Write(Path.Combine(Store, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state, StateJson));

    /// <summary>state.json in the Mac's shape (docs/data.md).</summary>
    private static readonly JsonSerializerOptions StateJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A meeting as the API gave it (its results and the owner's copy), kept so a rename in People re-renders it
    /// without fetching it again.</summary>
    public void Cache(byte[] detail, string meeting) => Disk.Write(Path.Combine(Store, "cache", $"{meeting}.json"), detail);

    public Detail? Cached(string meeting)
    {
        try
        {
            return Api.Decode<Detail>(File.ReadAllBytes(Path.Combine(Store, "cache", $"{meeting}.json")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Forget(string meeting) => File.Delete(Path.Combine(Store, "cache", $"{meeting}.json"));

    // — The folder

    /// <summary>"2026-10-27 1306 Budget review": by when, then what, so a listing reads in order.</summary>
    public static string DirectoryName(Meeting meeting, string title)
    {
        var when = (meeting.StartedAt ?? Ulid.Date(meeting.MeetingId)).ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        // "Budget: Q3 / plan" reads "Budget - Q3 - plan"
        var what = new StringInfo(Trim(SafeName(title.Replace(": ", " - ").Replace(" / ", " - "))));
        var name = what.LengthInTextElements > 80 ? what.SubstringByTextElements(0, 80) : what.String;
        return Trim($"{when} {(name.Length == 0 ? "Meeting" : name)}");
    }

    /// <summary>A name Windows takes for a file or folder: no characters a path or Explorer gives meaning to (a Mac's
    /// names can have them), no trailing dot or space (Windows drops them), and no device's name.</summary>
    public static string SafeName(string name)
    {
        var safe = new string(name.Select(c => "/:\\<>\"|?*".Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).TrimEnd(' ', '.');
        // "con.txt" opens the console, not a file: a device's name is one with any extension
        return Devices.Contains(safe.Split('.')[0]) ? "_" + safe : safe;
    }

    // COM¹–³ and LPT¹–³ are devices to Windows too
    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL", .. "123456789¹²³".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })], StringComparer.OrdinalIgnoreCase);

    /// <summary>A meeting's file as this PC keeps it: its name, made safe, or its id when nothing's left of it.</summary>
    public static string FileName(MeetingFile file) => SafeName(file.Name) is { Length: > 0 } name ? name : file.FileId;

    public string PathOf(string directory) => Path.Combine(Root, directory);

    /// <summary>Whether a name is another's: another meeting's (the same minute and title) or a folder of the owner's own.</summary>
    public bool Taken(string directory, string meeting) =>
        Directory.Exists(PathOf(directory)) && !Holds(directory, meeting) && Directory.EnumerateFileSystemEntries(PathOf(directory)).Any();

    /// <summary>Whether a folder is the copy of this meeting: Tally only touches its own.</summary>
    public bool Holds(string directory, string meeting)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PathOf(directory), "meeting.json")));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String && id.GetString() == meeting;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>A meeting's folder where its name says, moved there from its old name when the title changed.</summary>
    public void Place(string meeting, string? previous, string directory)
    {
        var target = PathOf(directory);
        if (previous != null && previous != directory && Holds(previous, meeting) && !Path.Exists(target))
            Directory.Move(PathOf(previous), target);
        Directory.CreateDirectory(target);
    }

    /// <summary>The names in a meeting's files folder, as they are.</summary>
    public IReadOnlyList<string> Present(string directory)
    {
        var folder = Path.Combine(PathOf(directory), "files");
        return Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>Writes a meeting's three files where they differ from what's there.</summary>
    public void Write(Source source, string directory)
    {
        var target = PathOf(directory);
        var files = new Dictionary<string, byte[]>
        {
            ["summary.md"] = Encoding.UTF8.GetBytes(SummaryPage(source)),
            ["transcript.md"] = Encoding.UTF8.GetBytes(TranscriptPage(source)),
            ["meeting.json"] = JsonSerializer.SerializeToUtf8Bytes(RecordOf(source), Json),
        };
        foreach (var (name, data) in files) WriteIfChanged(Path.Combine(target, name), data);
    }

    /// <summary>A meeting's files folder as the API lists them: the missing downloaded, the deleted gone.</summary>
    public async Task<IReadOnlyList<string>> Files(IReadOnlyList<MeetingFile> listed, string directory)
    {
        var folder = Path.Combine(PathOf(directory), "files");
        // Windows' names ignore case: "Report.pdf" and "report.pdf" are one file there
        var names = new Dictionary<string, MeetingFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in listed)  // two files of one name: the later one keeps its id, so neither is lost
        {
            var name = FileName(file);
            names[names.ContainsKey(name) ? $"{file.FileId}-{name}" : name] = file;
        }
        var present = Present(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in present.Except(names.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, gone);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (names.Count > 0) Directory.CreateDirectory(folder);
        // A file there is current only if its modified time is the file's AddedAt (set on download); a newer upload of the
        // same name is fetched again
        bool Current(string name, MeetingFile file) =>
            present.TryGetValue(name, out var there) && File.GetLastWriteTimeUtc(Path.Combine(folder, there)) == file.AddedAt.UtcDateTime;
        foreach (var (name, file) in names.Where(n => !Current(n.Key, n.Value)))
        {
            // The one it replaces goes first: one open in another program (Acrobat, Word) can't, and then nothing's
            // downloaded for it, not the whole file again at every pass
            if (present.TryGetValue(name, out var old)) File.Delete(Path.Combine(folder, old));
            var downloaded = await Http.Download(http, file.Url);  // a failure: the meeting's tried again next pass
            try
            {
                var target = Path.Combine(folder, name);
                File.Move(downloaded, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, file.AddedAt.UtcDateTime);
            }
            finally
            {
                File.Delete(downloaded);  // not left in the temporary folder when the move failed
            }
        }
        return names.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public void Remove(string directory, string meeting)
    {
        if (!Holds(directory, meeting)) return;
        try
        {
            Directory.Delete(PathOf(directory), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>index.json, every meeting newest first, and the README: rewritten when they'd say something new.</summary>
    public void WriteIndex(IReadOnlyList<IndexEntry> entries, string? account)
    {
        Directory.CreateDirectory(Root);
        var index = new Index(account, entries.OrderByDescending(e => e.StartedAt ?? "", StringComparer.Ordinal).ToList());
        WriteIfChanged(Path.Combine(Root, "index.json"), JsonSerializer.SerializeToUtf8Bytes(index, Json));
        WriteIfChanged(Path.Combine(Root, "README.md"), Encoding.UTF8.GetBytes(Readme));
    }

    /// <summary>A folder that syncs to a cloud (OneDrive, Dropbox, iCloud Drive, Google Drive): the copy would leave this PC too.</summary>
    public static bool SyncsToCloud(string path)
    {
        var full = Path.GetFullPath(path);
        var oneDrive = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable)
            .OfType<string>().Where(r => r.Length > 0).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)));
        if (oneDrive.Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase)
                              || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
        string[] services = ["OneDrive", "Dropbox", "iCloudDrive", "Google Drive", "My Drive"];
        return full.Split('/', '\\').Any(part => services.Any(s => part.Equals(s, StringComparison.OrdinalIgnoreCase)
                                                                   || part.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)));
    }

    private static void WriteIfChanged(string path, byte[] data)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return;
        Disk.Write(path, data);
    }

    /// <summary>Without spaces or dots at either end, which Windows drops from a name.</summary>
    private static string Trim(string text)
    {
        static bool Dropped(char c) => char.IsWhiteSpace(c) || c == '.';
        var (start, end) = (0, text.Length);
        while (start < end && Dropped(text[start])) start++;
        while (end > start && Dropped(text[end - 1])) end--;
        return text[start..end];
    }

    // — What's written

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public sealed record Index(string? Account, IReadOnlyList<IndexEntry> Meetings);

    public sealed record IndexEntry(
        string Id, string Title, string? StartedAt, int DurationMinutes, IReadOnlyList<string> Folders, IReadOnlyList<string> People,
        int ActionItemsOpen, string Path);

    public static IndexEntry EntryOf(Source source, string directory) => new(
        source.Meeting.MeetingId, source.Summary.Title, source.Meeting.StartedAt is { } started ? Iso(started) : null,
        Minutes(source.Results.Duration), source.Folders, source.Names.Participants.Select(source.Names.Name).ToList(),
        source.Summary.ActionItems.Count(a => a.Done != true), directory);

    /// <summary>A time as agents read it: ISO 8601, with the PC's offset.</summary>
    private static string Iso(DateTimeOffset date) => date.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <param name="BackgroundVoices">Speakers the owner set aside as not in the meeting: left out of people and the transcript.</param>
    public sealed record Record(
        string Id, string Title, string? StartedAt, string? EndedAt, int DurationMinutes, string? Language, IReadOnlyList<string> Folders,
        bool EditedByYou, string Overview, IReadOnlyList<string> KeyPoints, IReadOnlyList<Record.Said> Decisions,
        IReadOnlyList<Record.Action> ActionItems, IReadOnlyList<Record.Said> OpenQuestions, IReadOnlyList<Record.Part> Topics,
        IReadOnlyList<Record.Who> People, IReadOnlyList<string> Files, IReadOnlyList<string> BackgroundVoices, string OpenInTally)
    {
        public string SummaryFile => "summary.md";
        public string TranscriptFile => "transcript.md";

        public sealed record Said(string Text, IReadOnlyList<string> By);
        public sealed record Action(string Text, string? Owner, string? Due, bool Done);
        public sealed record Part(string Title, string Start, string End, IReadOnlyList<string> Notes, string? Outcome);
        public sealed record Who(
            string Speaker, string Name, string? PersonId, string? Role, string? Company, bool IsYou, bool Confirmed, double TalkMinutes,
            string? Contribution);
    }

    private static Record RecordOf(Source source)
    {
        var (s, names, results) = (source.Summary, source.Names, source.Results);
        var attendees = new Dictionary<string, Attendee>();
        foreach (var attendee in s.Attendees) attendees.TryAdd(attendee.Speaker, attendee);
        return new(
            source.Meeting.MeetingId, s.Title, source.Meeting.StartedAt is { } started ? Iso(started) : null,
            source.Meeting.EndedAt is { } ended ? Iso(ended) : null, Minutes(results.Duration), results.Language, source.Folders,
            source.Edited, names.Render(s.Overview), s.Points.Select(names.Render).ToList(),
            s.Decisions.Select(d => new Record.Said(names.Render(d.Text), d.By.Select(names.Name).ToList())).ToList(),
            s.ActionItems.Select(a => new Record.Action(names.Render(a.Text), a.Owner is { } owner ? names.Name(owner) : null, a.Due, a.Done == true)).ToList(),
            s.OpenQuestions.Select(q => new Record.Said(names.Render(q.Text), q.RaisedBy.Select(names.Name).ToList())).ToList(),
            s.Topics.Select(t => new Record.Part(names.Render(t.Title), Format.Clock(t.Start), Format.Clock(t.End), t.Notes.Select(names.Render).ToList(),
                t.Outcome is { } outcome ? names.Render(outcome) : null)).ToList(),
            names.Participants.Select(speaker =>
            {
                var person = names.Person(speaker);
                var attendee = attendees.GetValueOrDefault(speaker);
                return new Record.Who(speaker, names.Name(speaker), person?.PersonId, person?.Role ?? (attendee is null ? null : names.Render(attendee.Role)), person?.Company,
                    person?.IsOwner == true, names.ReviewOf(speaker) == Review.Confirmed,
                    Math.Round((results.Speakers.GetValueOrDefault(speaker)?.TalkTime ?? 0) / 6, MidpointRounding.AwayFromZero) / 10,
                    attendee is null ? null : names.Render(attendee.Contribution));
            }).ToList(),
            source.Files.Select(f => $"files/{f}").ToList(), results.SpeakerOrder.Where(sp => names.ReviewOf(sp) == Review.Background).ToList(),
            $"tally://meeting/{source.Meeting.MeetingId}");
    }

    /// <summary>summary.md: when and who, then the summary as Copy Summary gives it.</summary>
    private static string SummaryPage(Source source)
    {
        var when = source.Meeting.StartedAt is { } started ? Format.LongDate(started) : "";
        var facts = new[]
        {
            $"**When:** {when}, {Format.Duration(source.Results.Duration)}",
            source.Folders.Count == 0 ? null : "**Folders:** " + string.Join(", ", source.Folders),
            source.Edited ? "**Edited by you** in Tally" : null,
        }.OfType<string>();
        var summary = Markdown.Summary(source.Summary, source.Names).Split('\n', 2);
        return string.Join("\n", [summary[0], "", .. facts.Select(f => f + "  "), summary[1]]) + "\n";
    }

    private static string TranscriptPage(Source source) =>
        $"# {source.Summary.Title}: transcript\n\n" + Markdown.Transcript(source.Results, source.Names) + "\n";

    private static int Minutes(double seconds) => (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);

    // — The README

    private const string Readme = """
        # Tally meetings

        This folder is a copy of the owner's meetings, recorded and summarised by Tally, a meeting recorder for the PC. Tally
        keeps it up to date while it runs on this PC: a new meeting appears once it's summarised, and edits made in Tally
        (the summary, who spoke, folders, files) reach it within a minute or so.

        **It's a copy, one way.** Don't edit these files: Tally overwrites them at its next sync, and deletes a meeting's
        folder when the meeting is deleted in Tally. To change a meeting, change it in Tally. Files you add here yourself,
        outside the meetings' folders, are left alone.

        ## Layout

        ```
        README.md                         this file
        index.json                        every meeting, newest first: start here
        <date> <time> <title>/            one folder per meeting, e.g. "2026-10-27 1306 Budget review"
          meeting.json                    everything about the meeting, structured (below)
          summary.md                      the summary to read: overview, points, decisions, action items, topics
          transcript.md                   who said what, paragraph by paragraph, with times from the start
          files/                          files the owner kept with the meeting (slides, screenshots, documents)
        ```

        ## Finding things

        - **Go by id, not by path.** A meeting's `id` never changes. Its folder's name does, when its title is changed in
          Tally: `index.json` maps each `id` to its current `path`.
        - **People are the same person across meetings** when they share a `person_id`. `name` is how the owner calls
          them now. `is_you` marks the owner. `confirmed` is true when the owner has confirmed that voice.
        - **Times:** `started_at` and `ended_at` are ISO 8601 with the PC's UTC offset. Topic `start` and `end` are times
          from the meeting's start (`mm:ss`, or `h:mm:ss`). `due` is a date, `YYYY-MM-DD`.
        - **A field with nothing in it is left out** (an action item without an owner or a due date, a person without a
          role): read a missing field as null.
        - **Folders** are the owner's Tally folders, by path (`Customers › Acme`). A meeting can be in several, or none.
        - **Language:** `language` is the language spoken (`en`, `sv`, ...). Summaries are in that language.

        ## index.json

        ```json
        {"account": "Ada Lovelace",
         "meetings": [{"id": "01M5W8...", "title": "...", "started_at": "2026-10-27T13:06:00+01:00", "duration_minutes": 30,
                       "folders": ["Customers › Acme"], "people": ["Ada Lovelace", "Anna Berg"], "action_items_open": 2,
                       "path": "2026-10-27 1306 ..."}]}
        ```

        ## meeting.json

        ```json
        {"id": "01M5W8...", "title": "...", "started_at": "...", "ended_at": "...", "duration_minutes": 30, "language": "en",
         "folders": ["Customers › Acme"], "edited_by_you": false,
         "overview": "What the meeting was about, in a few sentences.",
         "key_points": ["..."],
         "decisions": [{"text": "...", "by": ["Anna Berg"]}],
         "action_items": [{"text": "...", "owner": "Anna Berg", "due": "2026-11-03", "done": false}],
         "open_questions": [{"text": "...", "by": ["..."]}],
         "topics": [{"title": "...", "start": "0:00", "end": "12:30", "notes": ["..."], "outcome": "..."}],
         "people": [{"speaker": "S1", "name": "Anna Berg", "person_id": "p_...", "role": "Head of finance", "company": "Acme",
                     "is_you": false, "confirmed": true, "talk_minutes": 12.5, "contribution": "What they brought."}],
         "files": ["files/slides.pdf"], "background_voices": ["S4"],
         "summary_file": "summary.md", "transcript_file": "transcript.md",
         "open_in_tally": "tally://meeting/01M5W8..."}
        ```

        `edited_by_you` is true when the owner edited the summary in Tally; this copy is then theirs. A speaker nobody has
        named yet is "Speaker 3", with no `person_id`. `background_voices` are speakers the owner marked as not in the
        meeting (someone talking nearby): they're not among `people`, and their lines aren't in transcript.md. `open_in_tally` opens the meeting in the Tally app.

        ## Examples

        ```sh
        # The ten newest meetings
        jq -r '.meetings[:10][] | "\(.started_at)  \(.title)"' index.json
        # Open action items, with their owner and meeting
        jq -r '.title as $t | .action_items[] | select(.done | not) | "\(.owner // "no owner"): \(.text) (\($t))"' */meeting.json
        # Meetings with someone
        jq -r '.meetings[] | select(.people | index("Anna Berg")) | .path' index.json
        # Every mention of a word, in the transcripts
        grep -il "budget" */transcript.md
        ```
        """;
}

/// <summary>
/// Markdown for a meeting, with names: what Copy Summary and Copy Transcript put on the clipboard, and what the
/// meetings folder writes. One source, so the two never differ.
/// </summary>
public static class Markdown
{
    /// <summary>The whole summary, everything the page shows, with who took part under the title: each by name, with what
    /// they do when it's known. A background voice wasn't in the meeting, so isn't among them.</summary>
    public static string Summary(Summary s, Names names)
    {
        var present = names.Participants.Select(speaker => names.Person(speaker)?.Detail is { } detail ? $"{names.Name(speaker)} ({detail})" : names.Name(speaker));
        var lines = new List<string> { $"# {s.Title}", "", "**Participants:** " + string.Join(", ", present), "", names.Render(s.Overview) };
        void Section(string title, IEnumerable<string> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            lines.AddRange(["", $"## {title}", .. list.Select(row => $"- {row}")]);
        }
        string Who(IReadOnlyList<string> speakers) => speakers.Count == 0 ? "" : $" ({string.Join(", ", speakers.Select(names.Name))})";

        Section("Summary", s.Points.Select(names.Render));
        Section("Decisions", s.Decisions.Select(d => names.Render(d.Text) + Who(d.By)));
        Section("Action items", s.ActionItems.Select(item =>
            (item.Done == true ? "[x] " : "[ ] ") + (item.Owner is { } owner ? $"{names.Name(owner)}: " : "") + names.Render(item.Text)
            + (item.Due is { } due ? $" (by {due})" : "")));
        Section("Open questions", s.OpenQuestions.Select(q => names.Render(q.Text) + Who(q.RaisedBy)));
        if (s.Topics.Count > 0)
        {
            lines.AddRange(["", "## Topics"]);
            foreach (var topic in s.Topics)
            {
                lines.AddRange(["", $"### {names.Render(topic.Title)} ({Format.Clock(topic.Start)}–{Format.Clock(topic.End)})", .. topic.Notes.Select(n => $"- {names.Render(n)}")]);
                if (topic.Outcome is { } outcome) lines.AddRange(["", $"**Outcome:** {names.Render(outcome)}"]);
            }
        }
        Section("Who said what", s.Attendees.Select(a => $"**{names.Name(a.Speaker)}**, {names.Role(a)}. {names.Render(a.Contribution)}"));
        return string.Join("\n", lines);
    }

    /// <summary>The transcript as the page shows it: each paragraph under its speaker's name and time.</summary>
    public static string Transcript(Results results, Names names) =>
        string.Join("\n\n", results.Paragraphs.Where(p => names.ReviewOf(p.Speaker) != Review.Background)
            .Select(p => $"**{names.Name(p.Speaker)}** ({Format.Clock(p.Start)})\n{p.Text}"));
}
