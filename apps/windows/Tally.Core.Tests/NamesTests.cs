namespace Tally.Tests;

/// <summary>Results and Names: what to call each speaker, and who the owner is asked about.</summary>
public class NamesTests
{
    private static readonly Results Meeting = Fixtures.Results("""
        {"meeting_id": "01M", "language": "en", "duration": 120.5, "summary": @SUMMARY,
         "speakers": {"S1": {"talk_time": 60, "turns": 3, "clip_urls": ["https://x/1.m4a"],
                             "identity": {"name": "Alex", "person_id": "p_1", "by": "agent"}},
                      "S2": {"talk_time": 30, "turns": 2, "identity": {"unknown": true, "by": "agent"}},
                      "S3": {"talk_time": 30, "turns": 2, "identity": {"name": "Sam", "person_id": "p_2", "by": "owner"}}},
         "segments": [{"start": 1.0, "end": 2.0, "speaker": "S2", "text": "Hi."}, {"start": 2.0, "end": 3.0, "speaker": "S1", "text": "Hello."}]}
        """.Replace("@SUMMARY", Fixtures.Summary));

    private static readonly Person Renamed = Fixtures.Person("p_1", "Alex Kim");
    private static readonly Person Sam = Fixtures.Person("p_2", "Sam");

    /// <summary>An attendee's role as the meeting described it, a speaker in it as a token: shown with the name.</summary>
    [Fact]
    public void An_attendees_role_reads_with_names()
    {
        var names = new Names(Meeting, new Dictionary<string, Person> { ["p_1"] = Renamed });
        Assert.Equal("Deputy to Alex Kim", names.Role(new Attendee("S2", "Deputy to {S1}", "Took notes.")));
    }

    [Fact]
    public void Names_follow_the_people_and_who_confirmed()
    {
        var names = new Names(Meeting, new Dictionary<string, Person> { ["p_1"] = Renamed, ["p_2"] = Sam });
        Assert.Equal("Alex Kim and Speaker 2 agreed with Sam.", names.Render(Meeting.Summary.Overview));
        Assert.Equal(["S2", "S1", "S3"], Meeting.SpeakerOrder);
        Assert.Equal(Review.Suggested, names.ReviewOf("S1"));
        Assert.Equal(Review.Unknown, names.ReviewOf("S2"));
        Assert.Equal(Review.Confirmed, names.ReviewOf("S3"));
        // Sam deleted in People: unknown again, wherever they were confirmed
        var withoutSam = new Names(Meeting, new Dictionary<string, Person> { ["p_1"] = Renamed });
        Assert.Equal("Speaker 3", withoutSam.Name("S3"));
        Assert.Equal(Review.Unknown, withoutSam.ReviewOf("S3"));
        // Asked about: S1 (a guess) and S2 (unknown), who both said enough; never someone settled
        Assert.True(names.Asks("S1") && names.Asks("S2") && !names.Asks("S3"));
    }

    [Fact]
    public void A_voice_the_owner_confirmed_before_is_confirmed_here_too()
    {
        var names = new Names(Meeting, new Dictionary<string, Person> { ["p_1"] = Fixtures.Person("p_1", "Alex", sources: "user") });
        Assert.Equal(Review.Confirmed, names.ReviewOf("S1"));
    }

    /// <summary>Someone who said only a few words (a remark across the room) isn't asked about: too little to stop for.</summary>
    [Fact]
    public void Someone_who_said_little_isnt_asked_about()
    {
        var results = Fixtures.Results("""
            {"language": "en", "duration": 180, "summary": @SUMMARY,
             "speakers": {"S1": {"talk_time": 150, "turns": 9, "identity": {"unknown": true, "by": "agent"}},
                          "S3": {"talk_time": 12, "turns": 2, "identity": {"unknown": true, "by": "agent"}}},
             "segments": []}
            """.Replace("@SUMMARY", Fixtures.Summary));
        var names = new Names(results, new Dictionary<string, Person>());
        Assert.True(names.Asks("S1"));
        Assert.False(names.Asks("S3"));
    }

    /// <summary>What the owner types keeps speakers as speakers, so a rename still reaches their copy.</summary>
    [Fact]
    public void What_the_owner_types_keeps_speakers_as_speakers()
    {
        var results = Fixtures.Results("""
            {"language": "en", "duration": 60, "segments": [], "summary": @SUMMARY,
             "speakers": {"S1": {"talk_time": 30, "identity": {"name": "Anna", "person_id": "p_1"}},
                          "S2": {"talk_time": 30, "identity": {"name": "Ann", "person_id": "p_2"}}, "S3": {"talk_time": 5}}}
            """.Replace("@SUMMARY", Fixtures.Summary));
        var names = new Names(results, new Dictionary<string, Person> { ["p_1"] = Fixtures.Person("p_1", "Anna"), ["p_2"] = Fixtures.Person("p_2", "Ann") });
        Assert.Equal("{S1} told {S2}, and {S3} agreed. Annual review.", names.Tokenize("Anna told Ann, and Speaker 3 agreed. Annual review."));
        Assert.Equal("Ann and Anna", names.Render(names.Tokenize("Ann and Anna")));  // what's shown is what was typed
    }

    [Fact]
    public void Emptied_points_arent_saved()
    {
        var summary = new Summary("T", "", ["Kept", "  "], [new("", [])], [new(null, "Do it", Done: true)], [],
            [new("A", 0, 1, ["", "Note"]), new(" ", 1, 2, [" "])], []).Cleaned;
        Assert.Equal(["Kept"], summary.Points);
        Assert.Empty(summary.Decisions);
        Assert.Equal(["Note"], Assert.Single(summary.Topics).Notes);  // and one emptied of everything goes
        Assert.True(summary.ActionItems[0].Done);
    }

    [Fact]
    public void Summaries_are_equal_by_what_they_say()
    {
        var a = new Summary("T", "O", ["P"], [], [new("S1", "Do it")], [], [], []);
        var b = new Summary("T", "O", ["P"], [], [new("S1", "Do it")], [], [], []);
        Assert.True(a == b);
        Assert.False(a == b with { Points = ["Q"] });
    }

    /// <summary>A background voice isn't a participant, isn't asked about, and its lines fold together.</summary>
    [Fact]
    public void A_background_voice_is_set_aside()
    {
        var results = Fixtures.Results("""
            {"language": "en", "duration": 300, "summary": @SUMMARY,
             "speakers": {"S1": {"talk_time": 150, "identity": {"name": "Ada", "person_id": "p_1", "by": "owner"}},
                          "S2": {"talk_time": 120, "identity": {"unknown": true, "by": "agent"}},
                          "S3": {"talk_time": 30, "identity": {"unknown": true, "background": true, "by": "owner"}}},
             "segments": [{"start": 0, "end": 4, "speaker": "S1", "text": "Shall we?"}, {"start": 4, "end": 6, "speaker": "S3", "text": "Lunch?"},
                          {"start": 6, "end": 8, "speaker": "S3", "text": "Upstairs."}, {"start": 8, "end": 12, "speaker": "S2", "text": "Yes."}]}
            """.Replace("@SUMMARY", Fixtures.Summary.Replace(" agreed with {S3}", " met")));
        var names = new Names(results, new Dictionary<string, Person> { ["p_1"] = Fixtures.Person("p_1", "Ada Lovelace", "CFO", "Acme", owner: true) });
        Assert.Equal(Review.Background, names.ReviewOf("S3"));
        Assert.False(names.Asks("S3"));
        Assert.Equal(["S1", "S2"], names.Participants);
        Assert.Equal(new Paragraph("S3", 4, "Lunch? Upstairs."), results.Paragraphs[1]);
        var summary = Markdown.Summary(results.Summary, names);
        Assert.Contains("**Participants:** Ada Lovelace (CFO, Acme), Speaker 2", summary);
        Assert.DoesNotContain("Speaker 3", summary);
        Assert.DoesNotContain("Lunch?", Markdown.Transcript(results, names));
    }

    [Fact]
    public void A_candidate_says_how_close_its_voice_is()
    {
        Assert.Equal("Very close", new Candidate("p", 0.7).Closeness);
        Assert.Equal("A little alike", new Candidate("p", 0.4).Closeness);
        Assert.Null(new Candidate("p", 0.2).Closeness);
    }
}
