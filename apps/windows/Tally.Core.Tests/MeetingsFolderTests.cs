using System.Text.Json;

namespace Tally.Tests;

/// <summary>The meetings folder: a meeting written whole, named by when and what, and only Tally's own
/// folders ever removed, never the owner's.</summary>
public class MeetingsFolderTests : IDisposable
{
    private readonly TempFolder temp = new();
    private readonly FakeHttp http = new();
    private readonly MeetingsFolder folder;
    private readonly Results results = Fixtures.Results("""
        {"language": "en", "duration": 1800,
         "summary": {"title": "Budget / Q3: plan", "overview": "{S1} and {S2} set the budget.", "summary": ["A point."],
                     "decisions": [{"text": "Go ahead.", "by": ["S1"]}], "action_items": [{"owner": "S2", "text": "Send it.", "due": "2026-11-03"}],
                     "open_questions": [], "topics": [], "attendees": []},
         "speakers": {"S1": {"talk_time": 900, "turns": 3, "identity": {"name": "Ada", "person_id": "p_1", "by": "owner"}},
                      "S2": {"talk_time": 600, "turns": 2, "identity": {"unknown": true, "by": "agent"}}},
         "segments": [{"start": 0, "end": 4, "speaker": "S1", "text": "Shall we?"}, {"start": 4, "end": 8, "speaker": "S2", "text": "Yes."}]}
        """);
    private readonly Meeting meeting = new("01M5W8DAHM7HCDPH26DE2PRR31", "ready", "Budget / Q3: plan", DateTimeOffset.FromUnixTimeSeconds(1_793_000_000),
        Duration: 1800, People: ["p_1"]);
    private readonly MeetingsFolder.Source source;

    public MeetingsFolderTests()
    {
        folder = new MeetingsFolder(Path.Combine(temp.Path, "Meetings"), temp.Path, "test", new HttpClient(http));
        var ada = Fixtures.Person("p_1", "Ada Lovelace", "CFO", "Acme", owner: true);
        source = new(meeting, results, results.Summary, false, new Names(results, new Dictionary<string, Person> { ["p_1"] = ada }), ["Customers › Acme"], []);
    }

    public void Dispose() => temp.Dispose();

    [Fact]
    public void A_meeting_is_written_whole_and_only_its_own_folder_is_ever_removed()
    {
        var directory = MeetingsFolder.DirectoryName(meeting, results.Summary.Title);
        Assert.DoesNotContain("/", directory);
        Assert.DoesNotContain(":", directory);
        Assert.EndsWith("Budget - Q3 - plan", directory);

        folder.Place(meeting.MeetingId, null, directory);
        folder.Write(source, directory);
        Assert.False(folder.Taken(directory, meeting.MeetingId));
        Assert.True(folder.Taken(directory, "01M5W8DAHM7HCDPH26DE2PRR99"));
        using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder.PathOf(directory), "meeting.json")));
        var root = record.RootElement;
        Assert.Equal(meeting.MeetingId, root.GetProperty("id").GetString());
        Assert.Equal("Customers › Acme", root.GetProperty("folders")[0].GetString());
        var item = root.GetProperty("action_items")[0];
        Assert.Equal(("Speaker 2", "2026-11-03", false), (item.GetProperty("owner").GetString(), item.GetProperty("due").GetString(), item.GetProperty("done").GetBoolean()));
        var ada = root.GetProperty("people")[0];
        Assert.Equal(("Ada Lovelace", true, "Acme", 15.0), (ada.GetProperty("name").GetString(), ada.GetProperty("is_you").GetBoolean(),
            ada.GetProperty("company").GetString(), ada.GetProperty("talk_minutes").GetDouble()));
        Assert.False(root.GetProperty("people")[1].TryGetProperty("person_id", out _));  // nothing in it: left out
        Assert.Equal("summary.md", root.GetProperty("summary_file").GetString());
        var summary = File.ReadAllText(Path.Combine(folder.PathOf(directory), "summary.md"));
        Assert.StartsWith("# Budget / Q3: plan\n\n**When:** ", summary);
        Assert.Contains("**Folders:** Customers › Acme  \n\n**Participants:**", summary);
        Assert.Contains("Ada Lovelace and Speaker 2 set the budget.", summary);
        Assert.StartsWith("# Budget / Q3: plan: transcript\n\n**Ada Lovelace** (0:00)\nShall we?", File.ReadAllText(Path.Combine(folder.PathOf(directory), "transcript.md")));

        // The owner's own folder (no meeting.json of this meeting's) is never removed, nor another meeting's
        Directory.CreateDirectory(folder.PathOf("My notes"));
        folder.Remove("My notes", meeting.MeetingId);
        folder.Remove(directory, "01M5W8DAHM7HCDPH26DE2PRR99");
        Assert.True(Directory.Exists(folder.PathOf("My notes")));
        Assert.True(Directory.Exists(folder.PathOf(directory)));
        folder.Remove(directory, meeting.MeetingId);
        Assert.False(Directory.Exists(folder.PathOf(directory)));

        folder.WriteIndex([MeetingsFolder.EntryOf(source, directory)], "Ada Lovelace");
        var readme = File.ReadAllText(Path.Combine(folder.Root, "README.md"));
        Assert.Contains("Go by id, not by path", readme);
        Assert.Contains("""jq -r '.meetings[:10][] | "\(.started_at)""", readme);
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder.Root, "index.json")));
        Assert.Equal(1, index.RootElement.GetProperty("meetings")[0].GetProperty("action_items_open").GetInt32());
    }

    [Fact]
    public void A_retitled_meeting_moves_its_folder()
    {
        folder.Place(meeting.MeetingId, null, "old name");
        folder.Write(source, "old name");
        File.WriteAllText(Path.Combine(folder.PathOf("old name"), "extra.txt"), "kept");
        folder.Place(meeting.MeetingId, "old name", "new name");
        Assert.False(Directory.Exists(folder.PathOf("old name")));
        Assert.True(File.Exists(Path.Combine(folder.PathOf("new name"), "extra.txt")));
    }

    [Fact]
    public void Names_are_safe_for_windows_paths()
    {
        var name = MeetingsFolder.DirectoryName(meeting with { StartedAt = null }, "What? <Draft> \"Plan\" | v2...");
        Assert.Equal(Ulid.Date(meeting.MeetingId).ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture) + " What- -Draft- -Plan- - v2", name);
        Assert.EndsWith(" Meeting", MeetingsFolder.DirectoryName(meeting, " ... "));
    }

    [Fact]
    public async Task Files_follow_the_api_downloaded_when_missing_and_gone_when_deleted()
    {
        http.On("GET https://s3.test/a", 200, "slides");
        http.On("GET https://s3.test/b", 200, "notes");
        Directory.CreateDirectory(Path.Combine(folder.PathOf("m"), "files"));
        File.WriteAllText(Path.Combine(folder.PathOf("m"), "files", "deleted.txt"), "old");
        var now = DateTimeOffset.UtcNow;
        var names = await folder.Files([new("a_1", "slides.pdf", 6, now, new Uri("https://s3.test/a")), new("a_2", "slides.pdf", 5, now, new Uri("https://s3.test/b"))], "m");
        Assert.Equal(["a_2-slides.pdf", "slides.pdf"], names);  // two of one name: neither is lost
        Assert.Equal(names, folder.Present("m"));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(folder.PathOf("m"), "files", "a_2-slides.pdf")));
    }

    /// <summary>A file replaced by another of its name (an updated agenda, the old one deleted): the folder gets the new
    /// one, not the deleted one's content for good.</summary>
    [Fact]
    public async Task A_file_replaced_by_one_of_its_name_is_fetched_again()
    {
        http.On("GET https://s3.test/old", 200, "old agenda");
        http.On("GET https://s3.test/new", 200, "new agenda");
        var first = new MeetingFile("a_1", "Agenda.docx", 10, DateTimeOffset.Parse("2026-10-28T09:00:00Z"), new Uri("https://s3.test/old"));
        var second = new MeetingFile("a_2", "Agenda.docx", 10, DateTimeOffset.Parse("2026-10-28T10:00:00Z"), new Uri("https://s3.test/new"));
        await folder.Files([first], "m");
        await folder.Files([first, second], "m");  // the new one beside the old, as a_2-Agenda.docx
        await folder.Files([second], "m");  // the old one deleted
        Assert.Equal("new agenda", File.ReadAllText(Path.Combine(folder.PathOf("m"), "files", "Agenda.docx")));
        Assert.Equal(["Agenda.docx"], folder.Present("m"));
    }

    /// <summary>A Mac's file names may hold what Windows' can't (a colon, a question mark, a trailing dot): each is
    /// kept under a name Windows takes, so the folder's state can be saved.</summary>
    [Fact]
    public async Task Files_windows_wont_name_are_kept_under_names_it_takes()
    {
        http.On("GET https://s3.test/a", 200, "slides");
        http.On("GET https://s3.test/b", 200, "notes");
        var now = DateTimeOffset.UtcNow;
        var names = await folder.Files([new("a_1", "Q3: plan?.pdf", 6, now, new Uri("https://s3.test/a")), new("a_2", "...", 5, now, new Uri("https://s3.test/b"))], "m");
        Assert.Equal(["Q3- plan-.pdf", "a_2"], names);
        Assert.Equal(names, folder.Present("m"));
    }

    /// <summary>Windows' names ignore case: two files whose names differ only in it are both kept, and one already there
    /// under another case isn't fetched again.</summary>
    [Fact]
    public async Task Names_that_differ_only_in_case_are_two_files()
    {
        http.On("GET https://s3.test/a", 200, "slides");
        http.On("GET https://s3.test/b", 200, "notes");
        var now = DateTimeOffset.UtcNow;
        List<MeetingFile> listed = [new("a_1", "Report.pdf", 6, now, new Uri("https://s3.test/a")), new("a_2", "report.pdf", 5, now, new Uri("https://s3.test/b"))];
        Assert.Equal(["Report.pdf", "a_2-report.pdf"], await folder.Files(listed, "m"));
        Assert.Equal(["Report.pdf", "a_2-report.pdf"], await folder.Files(listed, "m"));
        Assert.Equal(2, http.Requests.Count);  // each once

        File.Move(Path.Combine(folder.PathOf("m"), "files", "Report.pdf"), Path.Combine(folder.PathOf("m"), "files", "REPORT.PDF"));
        await folder.Files(listed, "m");
        Assert.Equal(2, http.Requests.Count);
        Assert.True(File.Exists(Path.Combine(folder.PathOf("m"), "files", "REPORT.PDF")));  // the same file to Windows: kept
    }

    /// <summary>CON, NUL, COM1 and the rest are devices to Windows, with any extension: such a name is kept as a file.</summary>
    [Fact]
    public void Device_names_are_kept_as_files()
    {
        Assert.Equal("_CON", MeetingsFolder.SafeName("CON"));
        Assert.Equal("_nul.txt", MeetingsFolder.SafeName("nul.txt"));
        Assert.Equal("_com1.tar.gz", MeetingsFolder.SafeName("com1.tar.gz"));
        Assert.Equal("_LPT9", MeetingsFolder.SafeName("LPT9."));
        Assert.Equal("CONSOLE.txt", MeetingsFolder.SafeName("CONSOLE.txt"));
        Assert.Equal("COM10", MeetingsFolder.SafeName("COM10"));
        Assert.Equal("_COM¹.txt", MeetingsFolder.SafeName("COM¹.txt"));  // the superscripts are devices too
    }

    /// <summary>What was written, per account, in Tally's data folder; state.json in the Mac's shape, so the two apps'
    /// files read alike (docs/data.md).</summary>
    [Fact]
    public void What_was_written_is_remembered_per_account_in_the_macs_shape()
    {
        var state = folder.LoadState();
        state.Meetings["01M"] = new("1|2", "2026-10-28 0900 Budget", "abc");
        folder.Save(state);
        folder.Cache("""{"results": null}"""u8.ToArray(), "01M");
        var saved = File.ReadAllText(Path.Combine(folder.Store, "state.json"));
        Assert.Contains("\"meetings\"", saved);
        Assert.Contains("\"directory\"", saved);
        Assert.Equal(new MeetingsFolder.Entry("1|2", "2026-10-28 0900 Budget", "abc"), folder.LoadState().Meetings["01M"]);
        Assert.NotNull(folder.Cached("01M"));
        Assert.StartsWith(Path.Combine(temp.Path, "meetings-folder", "test"), folder.Store);
    }

    [Fact]
    public void A_folder_in_a_cloud_drive_is_noticed()
    {
        Assert.True(MeetingsFolder.SyncsToCloud(Path.Combine(temp.Path, "Dropbox", "Meetings")));
        Assert.True(MeetingsFolder.SyncsToCloud(Path.Combine(temp.Path, "OneDrive - Acme", "Meetings")));
        Assert.False(MeetingsFolder.SyncsToCloud(Path.Combine(temp.Path, "Documents", "Meetings")));
    }
}
