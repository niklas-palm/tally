using System.Text;
using System.Text.Json;

namespace Tally.Tests;

/// <summary>The API's shapes (docs/api.md, docs/data.md) as they come and go, and its errors as the app tells them.</summary>
public class ApiTests
{
    private readonly FakeHttp http = new();
    private readonly Api api;

    public ApiTests() => api = new Api(new Uri(FakeHttp.Api), "token-1", null, new HttpClient(http));

    [Fact]
    public async Task Meetings_read_every_field_the_list_has()
    {
        http.On("GET https://api.test/meetings", 200, """
            {"meetings": [{"meeting_id": "01M5W8DAHM7HCDPH26DE2PRR31", "state": "ready", "title": "Budget", "started_at": "2026-10-22T09:00:00Z",
                           "ended_at": "2026-10-22T09:47:12.123456+00:00", "duration": 2832.1, "language": "en", "error": null,
                           "updated_at": "2026-10-22T10:00:00Z", "edited_at": null, "rewriting": "2026-10-22T10:05:00Z", "rewrite_error": null,
                           "people": ["p_1", "p_2"], "folder_ids": ["f_1"]},
                          {"meeting_id": "01M5W8DAHM7HCDPH26DE2PRR32", "state": "uploading"}]}
            """);
        var meetings = await api.Meetings();
        var budget = meetings[0];
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero), budget.StartedAt);
        Assert.Equal(2832.1, budget.Duration);
        Assert.Equal(["p_1", "p_2"], budget.People);
        Assert.Equal(["f_1"], budget.FolderIds);
        Assert.NotNull(budget.Rewriting);
        Assert.False(budget.IsRewriting);  // asked for long ago: a job that died doesn't hold it for good
        Assert.Null(meetings[1].Title);
        var request = Assert.Single(http.Requests).Message;
        Assert.Equal("Bearer token-1", request.Headers.Authorization?.ToString());
        Assert.Equal(["repository build"], request.Headers.GetValues("X-Tally-Version"));
    }

    [Fact]
    public async Task A_ready_meeting_brings_its_results_and_the_owners_copy()
    {
        http.On("GET https://api.test/meetings/01M", 200, """
            {"meeting_id": "01M", "state": "ready",
             "results": {"meeting_id": "01M", "meta": {}, "language": "sv", "duration": 60, "layout": "identical", "summary_without": ["S3"],
                         "summary": @SUMMARY,
                         "speakers": {"S1": {"talk_time": 40, "turns": 3, "mic_ratio_db": null, "clips": ["results/clips/S1/1.m4a"],
                                             "clip_urls": ["https://s3.test/1.m4a"], "candidates": [{"person_id": "p_1", "name": "Ada", "score": 0.71}],
                                             "identity": {"name": "Ada", "person_id": "p_1", "confidence": "high", "by": "agent", "evidence": [], "suggestion": false}}},
                         "segments": [{"start": 1, "end": 2, "speaker": "S1", "text": "Hi."}]},
             "notes": {"summary": {"title": "Mine", "overview": "", "summary": ["A point."], "decisions": [], "action_items": [{"owner": null, "text": "Do it", "due": null, "done": true}],
                                   "open_questions": [], "topics": [], "attendees": []}}}
            """.Replace("@SUMMARY", Fixtures.Summary));
        var detail = await api.Detail("01M");
        var speaker = detail.Results!.Speakers["S1"];
        Assert.Equal("sv", detail.Results.Language);
        Assert.Equal(["S3"], detail.Results.SummaryWithout);
        Assert.Equal(new Uri("https://s3.test/1.m4a"), Assert.Single(speaker.ClipUrls!));
        Assert.Equal("Very close", Assert.Single(speaker.Candidates!).Closeness);
        Assert.Equal("agent", speaker.Identity!.By);
        Assert.Equal(["A point."], detail.Notes!.Summary!.Points);
        Assert.True(detail.Notes.Summary.ActionItems[0].Done);
    }

    [Fact]
    public async Task People_files_folders_and_search_read_as_the_api_sends_them()
    {
        http.On("GET https://api.test/people", 200, """
            {"people": [{"person_id": "p_1", "name": "Ada", "aliases": ["A"], "role": "CFO", "company": null, "is_owner": true,
                         "voiceprints": [{"voiceprint_id": "v_1", "source": "user", "meeting_id": "01M", "created_at": "2026-10-22T09:00:00Z"}]}]}
            """);
        http.On("GET https://api.test/meetings/01M/files", 200, """
            {"files": [{"file_id": "a_1", "name": "slides.pdf", "size": 1200, "added_at": "2026-10-22T09:00:00Z", "url": "https://s3.test/f"}]}
            """);
        http.On("GET https://api.test/folders", 200, """{"folders": [{"folder_id": "f_1", "name": "Customers", "parent": null}]}""");
        http.On("GET https://api.test/search", 200, """
            {"results": [{"meeting_id": "01M", "title": "Budget", "started_at": "2026-10-22T09:00:00Z", "where": "transcript", "snippet": "the budget", "at": 12.5}]}
            """);
        var ada = Assert.Single(await api.People());
        Assert.True(ada.Confirmed && ada.IsOwner);
        Assert.Equal("CFO", ada.Detail);
        Assert.Equal(1200, Assert.Single(await api.Files("01M")).Size);
        Assert.Null(Assert.Single(await api.Folders()).Parent);
        var hit = Assert.Single(await api.Search("budget & plan"));
        Assert.Equal(("transcript", 12.5), (hit.Where, hit.At));
        Assert.EndsWith("/search?q=budget%20%26%20plan", http.Requests.Last().Message.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task The_owners_summary_goes_in_snake_case_without_empty_fields()
    {
        http.On("PUT https://api.test/meetings/01M/summary", 200);
        var summary = new Summary("T", "O", ["P"], [new("Go.", ["S1"])], [new(null, "Do it", "2026-11-03")], [new("Why?", ["S2"])],
            [new("Topic", 0, 60, ["N"])], [new("S1", "Chairs", "Led it.")]);
        await api.SaveSummary(summary, "01M");
        using var body = JsonDocument.Parse(http.Requests.Single().Body);
        var sent = body.RootElement.GetProperty("summary");
        Assert.Equal("P", sent.GetProperty("summary")[0].GetString());
        Assert.Equal("S2", sent.GetProperty("open_questions")[0].GetProperty("raised_by")[0].GetString());
        var item = sent.GetProperty("action_items")[0];
        Assert.False(item.TryGetProperty("owner", out _) || item.TryGetProperty("done", out _));
        Assert.Equal("2026-11-03", item.GetProperty("due").GetString());
        Assert.False(sent.GetProperty("topics")[0].TryGetProperty("outcome", out _));
        Assert.Equal(summary, Api.Decode<Summary>(Encoding.UTF8.GetBytes(sent.GetRawText())));  // and back
    }

    [Fact]
    public async Task A_folder_at_the_top_says_so()
    {
        http.On("PUT https://api.test/folders/f_1", 200);
        await api.Save(new Folder("f_1", "Customers"));
        Assert.Equal("""{"name":"Customers","parent":null}""", http.Requests.Single().Body);
    }

    [Fact]
    public async Task A_recording_is_registered_then_its_audio_put_where_the_api_says()
    {
        using var temp = new TempFolder();
        var recording = new Recording("01M", new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero), RecordingState.Saved,
            EndedAt: new DateTimeOffset(2026, 10, 22, 9, 47, 12, TimeSpan.Zero)) { Root = temp.Path };
        Directory.CreateDirectory(recording.Folder);
        File.WriteAllBytes(recording.Audio, [1, 2, 3]);
        http.On("PUT https://api.test/meetings/01M", 200, """{"upload_url": "https://s3.test/audio", "upload_headers": {"Content-Type": "audio/mp4", "x-amz-meta-owner": "u"}}""");
        http.On("PUT https://s3.test/audio", 200, "");
        await api.Upload(recording, "0.1.0");
        var registered = http.Requests.First();
        Assert.Equal("""{"started_at":"2026-10-22T09:00:00Z","app_version":"0.1.0","ended_at":"2026-10-22T09:47:12Z"}""", registered.Body);
        var put = http.Requests.Last().Message;
        Assert.Equal("audio/mp4", put.Content!.Headers.ContentType!.ToString());
        Assert.Equal(3, put.Content.Headers.ContentLength);
        Assert.Equal(["u"], put.Headers.GetValues("x-amz-meta-owner"));

        http.On("PUT https://api.test/meetings/01M", 409, """{"error": "meeting already sent"}""");
        await Assert.ThrowsAsync<Api.AlreadySent>(() => api.Upload(recording, "0.1.0"));
    }

    [Fact]
    public async Task Errors_say_what_a_person_can_act_on()
    {
        http.On("PUT https://api.test/meetings/01M/speakers/S1", 409, """{"error": "meeting isn't ready yet"}""");
        var refused = await Assert.ThrowsAsync<Api.Failure>(() => api.Assign("01M", "S1", new Dictionary<string, object> { ["unknown"] = true }));
        Assert.Equal("Meeting isn't ready yet.", refused.Message);

        http.On("DELETE https://api.test/people/p_1", 500, """{"error": "Traceback..."}""");
        var broken = await Assert.ThrowsAsync<Api.Failure>(() => api.DeletePerson("p_1"));
        Assert.Null(broken.Reason);
        Assert.Equal("Something went wrong on Tally's side. Try again in a moment.", broken.Message);

        http.On("GET https://api.test/meetings", 401, """{"message": "Unauthorized"}""");
        Assert.StartsWith("Your sign-in wasn't accepted.", (await Assert.ThrowsAsync<Api.Failure>(api.Meetings)).Message);

        http.Offline = true;
        Assert.Equal("You're offline.", (await Assert.ThrowsAsync<Api.Offline>(api.Meetings)).Message);
    }
}
