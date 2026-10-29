namespace Tally.Tests;

/// <summary>AppModel against a fake API: sending in steps, tidying up, picking up after a crash, the account's own,
/// and the owner's summary.</summary>
public class AppModelTests : IDisposable
{
    private const string Id = "01M5W8DAHM7HCDPH26DE2PRR31";
    private const string Meetings = $"GET {FakeHttp.Api}/meetings";
    private const string Register = $"PUT {FakeHttp.Api}/meetings/{Id}";
    private const string S3 = "PUT https://s3.test/audio";
    private const string Uploaded = $"POST {FakeHttp.Api}/meetings/{Id}/uploaded";
    private static readonly Config Config = new(new Uri("https://auth.test/authorize"), new Uri(FakeHttp.Token), "tally", "Example", new Uri(FakeHttp.Api));

    private const int Zoom = 501;

    private readonly TempFolder temp = new();
    private readonly FakeHttp http = new();
    private readonly FakePlatform platform = new();
    private DateTimeOffset now = DateTimeOffset.UtcNow;  // the call rules' clock

    public void Dispose() => temp.Dispose();

    private AppModel Model() => new(Config, platform.Bundle(temp.Path), http);
    private string Root => Path.Combine(temp.Path, "recordings");

    private void SignIn()
    {
        platform.Secrets["refresh-token"] = "refresh-1"u8.ToArray();
        http.SignedIn();
    }

    private Recording Saved(string id = Id, RecordingState state = RecordingState.Saved)
    {
        var recording = new Recording(id, Ulid.Date(id), state, Ulid.Date(id).AddMinutes(30)) { Root = Root };
        recording.Save();
        File.WriteAllBytes(recording.Audio, [1, 2, 3]);
        return recording;
    }

    /// <summary>What GET /meetings, /people and /folders answer: this one meeting, in this state.</summary>
    private void Listing(string state)
    {
        http.On(Meetings, 200, """{"meetings": [{"meeting_id": "@ID", "state": "@STATE", "title": "Budget", "started_at": "2026-10-22T09:00:00Z"}]}""".Replace("@ID", Id).Replace("@STATE", state));
        http.On($"GET {FakeHttp.Api}/people", 200, """{"people": []}""");
        http.On($"GET {FakeHttp.Api}/folders", 200, """{"folders": []}""");
    }

    private void Ready()
    {
        Listing("ready");
        http.On($"GET {FakeHttp.Api}/meetings/{Id}", 200, Detail());
    }

    /// <summary>What GET /meetings/&lt;id&gt; answers for the ready meeting: one speaker, of this identity.</summary>
    private static string Detail(string identity = """{"unknown": true, "by": "agent"}""") => """
        {"meeting_id": "@ID", "state": "ready",
         "results": {"language": "en", "duration": 60, "segments": [], "summary": @SUMMARY,
                     "speakers": {"S1": {"talk_time": 60, "identity": @WHO}}}}
        """.Replace("@ID", Id).Replace("@SUMMARY", Fixtures.Summary).Replace("@WHO", identity);

    /// <summary>The meeting has slides.pdf among its files, which S3 serves.</summary>
    private void WithSlides()
    {
        http.On($"GET {FakeHttp.Api}/meetings/{Id}/files", 200,
            """{"files": [{"file_id": "f_1", "name": "slides.pdf", "size": 6, "added_at": "2026-10-22T09:00:00Z", "url": "https://s3.test/f"}]}""");
        http.On("GET https://s3.test/f", 200, "slides");
    }

    /// <summary>Where slides.pdf is in a meetings folder, or "" while it isn't there.</summary>
    private static string Slides(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "slides.pdf", SearchOption.AllDirectories).SingleOrDefault() ?? "" : "";

    /// <summary>A build from the install page, which offers a newer one.</summary>
    private async Task<AppModel> UpdateOffered()
    {
        var model = new AppModel(Config with { Site = new Uri("https://tally.test/"), Version = "2026.10.01-abc" }, platform.Bundle(temp.Path), http);
        http.On("GET https://tally.test/windows.json", 200, """{"version": "2026.10.29-def"}""");
        await model.CheckForUpdate();
        return model;
    }

    private void Accepting()
    {
        http.On(Register, 200, """{"upload_url": "https://s3.test/audio", "upload_headers": {"Content-Type": "audio/mp4"}}""");
        http.On(S3, 200, "");
        http.On(Uploaded, 200);
    }

    /// <summary>This many seconds of what's using audio now, a look a second, on the test's clock.</summary>
    private async Task Seconds(AppModel model, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            await model.ObserveCalls(now);
            now += TimeSpan.FromSeconds(1);
        }
    }

    private void OnCall(bool on)
    {
        platform.AudioUsers.Clear();
        if (on) platform.AudioUsers.Add(new(Zoom, Input: true, Output: true, "Zoom"));
    }

    /// <summary>A three-minute call, recorded from its start (offered, or by itself in auto mode), that has just ended:
    /// the recording is paused, and can still be continued.</summary>
    private async Task<AppModel> CallEnded(bool auto = false)
    {
        var model = Model();
        if (auto) model.CallMode = CallMode.Auto;
        OnCall(true);
        await Seconds(model, 11);
        if (!auto) await model.RecordCall();
        Fixtures.Wav(((FakeRecorder)model.Recorder!).Path, frames: 48_000);
        await Seconds(model, 180);
        OnCall(false);
        await Seconds(model, 31);
        Assert.NotNull(model.StoppedByCall);
        return model;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 250 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    /// <summary>A sent recording whose meeting was deleted on another device: no longer listed, so it's let go, not shown
    /// on its way for good.</summary>
    [Fact]
    public void A_sent_recording_whose_meeting_was_deleted_elsewhere_goes() => UiThread.Run(async () =>
    {
        SignIn();
        var recording = Saved();
        Accepting();
        Listing("queued");
        var model = Model();
        await model.Sync();
        Assert.Equal(RecordingState.Sent, Assert.Single(model.Recordings).State);
        http.On(Meetings, 200, """{"meetings": []}""");  // deleted on the other device
        await model.Sync();
        Assert.Empty(model.Recordings);
        Assert.False(Directory.Exists(recording.Folder));
        Assert.Empty(model.Items);
    });

    /// <summary>What this PC is doing with a meeting wins over what the cloud said last (a Send Again, an interrupted send).</summary>
    [Fact]
    public void Status_follows_this_pc_while_it_sends()
    {
        Assert.Equal(Status.Uploading, AppModel.StatusOf("failed", "x", RecordingState.Uploading));
        Assert.Equal(Status.Waiting, AppModel.StatusOf("failed", "x", RecordingState.Saved));
        Assert.Equal(Status.Failed("x"), AppModel.StatusOf("failed", "x", RecordingState.Sent));
        Assert.Equal(Status.Queued, AppModel.StatusOf("uploading", null, RecordingState.Uploaded));
        Assert.Equal(Status.Waiting, AppModel.StatusOf("uploading", null, null));
        Assert.Equal(Status.Ready, AppModel.StatusOf("ready", null, RecordingState.Saved));
        Assert.Equal(Status.Processing, AppModel.StatusOf("processing", null, RecordingState.Sent));
    }

    [Fact]
    public void A_recording_is_sent_in_steps_and_goes_from_this_pc_once_its_summary_is_ready() => UiThread.Run(async () =>
    {
        SignIn();
        var recording = Saved();
        Accepting();
        Listing("queued");
        var model = Model();

        await model.Sync();
        Assert.Equal([$"POST {FakeHttp.Token}", Register, S3, Uploaded, Meetings, $"GET {FakeHttp.Api}/people", $"GET {FakeHttp.Api}/folders"], http.Routes);
        Assert.Equal(RecordingState.Sent, Assert.Single(model.Recordings).State);
        Assert.Equal(RecordingState.Sent, Assert.Single(Recording.LoadAll(Root)).State);  // kept as it goes
        Assert.Equal(Status.Queued, Assert.Single(model.Items).Status);

        Ready();
        await model.Sync();
        Assert.Empty(model.Recordings);
        Assert.False(Directory.Exists(recording.Folder));  // S3 has it
        Assert.Equal(Status.Ready, Assert.Single(model.Items).Status);
        Assert.Equal(1, model.ToConfirm(Id));
        Assert.Equal((Id, "Budget", "The summary is ready. 1 person to confirm.", Id), Assert.Single(platform.Posted));
    });

    [Fact]
    public void A_meeting_the_api_has_already_isnt_uploaded_again() => UiThread.Run(async () =>
    {
        SignIn();
        Saved();
        Accepting();
        http.On(Register, 409, """{"error": "meeting already sent"}""");
        Listing("processing");
        var model = Model();
        await model.Sync();
        Assert.Equal(RecordingState.Sent, Assert.Single(model.Recordings).State);
        Assert.DoesNotContain(S3, http.Routes);
        Assert.DoesNotContain(Uploaded, http.Routes);
    });

    [Fact]
    public void A_step_that_fails_is_tried_again_from_where_it_stopped() => UiThread.Run(async () =>
    {
        SignIn();
        Saved();
        Accepting();
        http.On(Uploaded, 500, """{"error": "boom"}""");
        Listing("uploading");
        var model = Model();
        await model.Sync();
        var stopped = Assert.Single(model.Recordings);
        Assert.Equal((RecordingState.Uploaded, "Couldn't send it yet. Tally tries again shortly."), (stopped.State, stopped.Error));

        http.On(Uploaded, 200);
        await model.Sync();
        Assert.Equal((RecordingState.Sent, null), (model.Recordings[0].State, model.Recordings[0].Error));
        Assert.Single(http.Routes, r => r == S3);  // the audio went once
    });

    [Fact]
    public void An_upload_that_fails_waits_to_be_sent_again() => UiThread.Run(async () =>
    {
        SignIn();
        Saved();
        Accepting();
        http.On(S3, 503, "");
        Listing("uploading");
        var model = Model();
        await model.Sync();
        Assert.Equal((RecordingState.Saved, "Couldn't send it yet. Tally tries again shortly."), (model.Recordings[0].State, model.Recordings[0].Error));
        Assert.Equal(Status.Waiting, model.Items[0].Status);
    });

    [Fact]
    public void Recordings_wait_for_a_sign_in() => UiThread.Run(async () =>
    {
        Saved();
        var model = Model();
        await model.Sync();
        Assert.Equal(1, model.WaitingForSignIn);
        Assert.Empty(http.Requests);
        Assert.False(model.Offline);
    });

    [Fact]
    public void Offline_nothing_is_lost_and_the_next_sync_sends_it() => UiThread.Run(async () =>
    {
        SignIn();
        Saved();
        Accepting();
        Listing("queued");
        var model = Model();
        http.Offline = true;
        await model.Sync();
        Assert.True(model.Offline);
        Assert.Equal((RecordingState.Saved, null), (model.Recordings[0].State, model.Recordings[0].Error));

        http.Offline = false;
        await model.Sync();
        Assert.False(model.Offline);
        Assert.Equal(RecordingState.Sent, model.Recordings[0].State);
    });

    /// <summary>docs/macos.md, "After a crash or a quit".</summary>
    [Fact]
    public void After_a_crash_what_was_recorded_is_kept_and_a_cut_off_send_starts_again() => UiThread.Run(async () =>
    {
        var started = DateTimeOffset.UtcNow.AddHours(-1);
        Recording Make(string id, RecordingState state) => new(id, started, state) { Root = Root };
        var recording = Make("01A", RecordingState.Recording);
        recording.Save();
        Fixtures.Wav(recording.Wav, frames: 48_000);
        var stopped = started.AddMinutes(40);
        File.SetLastWriteTimeUtc(recording.Wav, stopped.UtcDateTime);
        var encoded = Make("01B", RecordingState.Recording);
        encoded.Save();
        File.WriteAllBytes(encoded.Audio, [1]);
        var nothing = Make("01C", RecordingState.Recording);
        nothing.Save();
        var uploading = Make("01D", RecordingState.Uploading);
        uploading.Save();
        File.WriteAllBytes(uploading.Audio, [1]);

        var model = Model();
        await model.Recover();
        var byId = model.Recordings.ToDictionary(r => r.Id);
        Assert.Equal(RecordingState.Saved, byId["01A"].State);  // encoded from its WAV, which goes
        Assert.Equal(stopped, byId["01A"].EndedAt);
        Assert.True(File.Exists(recording.Audio));
        Assert.False(File.Exists(recording.Wav));
        Assert.Equal(RecordingState.Saved, byId["01B"].State);  // encoded, but the crash came before it was marked
        Assert.False(byId.ContainsKey("01C"));  // the crash came before anything was recorded
        Assert.False(Directory.Exists(nothing.Folder));
        Assert.Equal(RecordingState.Saved, byId["01D"].State);
        Assert.Equal(RecordingState.Saved, Recording.LoadAll(Root).Single(r => r.Id == "01D").State);
    });

    [Fact]
    public void After_a_crash_an_empty_recording_or_a_short_auto_one_isnt_kept() => UiThread.Run(async () =>
    {
        var empty = new Recording("01A", DateTimeOffset.UtcNow.AddHours(-1), RecordingState.Recording) { Root = Root };
        empty.Save();
        Fixtures.Wav(empty.Wav, frames: 0);
        var lobby = new Recording("01B", DateTimeOffset.UtcNow.AddSeconds(-30), RecordingState.Recording, Auto: true) { Root = Root };
        lobby.Save();
        Fixtures.Wav(lobby.Wav, frames: 48_000);

        var model = Model();
        await model.Recover();
        Assert.Empty(model.Recordings);
        Assert.Empty(Directory.GetDirectories(Root));
        Assert.NotNull(model.Problem);
    });

    /// <summary>An encode that fails leaves the WAV, and Try Again encodes it once more.</summary>
    [Fact]
    public void A_recording_that_couldnt_be_saved_is_tried_again() => UiThread.Run(async () =>
    {
        var broken = new Recording("01A", DateTimeOffset.UtcNow.AddHours(-1), RecordingState.Broken, Error: "Tally couldn't save the recording.") { Root = Root };
        broken.Save();
        Fixtures.Wav(broken.Wav, frames: 48_000);
        var model = Model();
        var item = Assert.Single(model.Items);
        Assert.True(item.HasAudio);
        platform.EncodeWaits.Reset();  // held, so it's seen saving, however quick the machine
        model.Retry(item);
        Assert.Equal(Status.Saving, Assert.Single(model.Items).Status);  // a second click does nothing
        platform.EncodeWaits.Set();
        await Until(() => model.Recordings[0].State != RecordingState.Recording);
        Assert.Equal(RecordingState.Saved, model.Recordings[0].State);
    });

    /// <summary>Sign Out: what the account has goes from this PC; what's still to be sent stays, for whoever signs in next.</summary>
    [Fact]
    public void Signing_out_forgets_the_account_but_keeps_what_isnt_sent() => UiThread.Run(async () =>
    {
        SignIn();
        var sent = Saved("01M5W8DAHM7HCDPH26DE2PRR31", RecordingState.Sent);
        var waiting = Saved("01M5W8DAHM7HCDPH26DE2PRR32") with { Error = "Couldn't send it yet. Tally tries again shortly." };
        waiting.Save();
        Listing("processing");
        var model = Model();
        await model.Auth.Token();
        await model.Sync();
        Assert.NotEmpty(model.Meetings);

        model.SignOut();
        Assert.Empty(model.Meetings);
        Assert.Empty(model.Results);
        Assert.False(Directory.Exists(sent.Folder));
        var kept = Assert.Single(model.Recordings);
        Assert.Equal((waiting.Id, null), (kept.Id, kept.Error));
        Assert.Empty(platform.Secrets);
    });

    [Fact]
    public void A_microphone_windows_refuses_says_where_to_allow_it() => UiThread.Run(async () =>
    {
        platform.Refusal = new UnauthorizedAccessException();
        var model = Model();
        await model.StartRecording();
        Assert.Equal(new Problem("Tally needs your microphone to record.", "ms-settings:privacy-microphone"), model.Problem);
        Assert.Null(model.Recorder);
        Assert.Empty(model.Recordings);
        Assert.False(Directory.Exists(Root) && Directory.GetDirectories(Root).Length > 0);
    });

    [Fact]
    public void A_recording_stopped_at_once_isnt_kept() => UiThread.Run(async () =>
    {
        var model = Model();
        await model.StartRecording();
        var recording = Assert.Single(model.Recordings);
        Assert.Equal(Status.Recording, model.Items[0].Status);
        Fixtures.Wav(recording.Wav, frames: 0);
        await model.StopRecording();
        Assert.Empty(model.Recordings);
        Assert.Equal("Nothing was recorded, so there's nothing to keep.", model.Problem?.Message);
    });

    /// <summary>The owner's copy, saved without what they emptied, titled as they typed, and undone a burst at a time.</summary>
    [Fact]
    public void The_owners_edits_are_saved_as_their_copy_and_can_be_undone() => UiThread.Run(async () =>
    {
        SignIn();
        Ready();
        var put = $"PUT {FakeHttp.Api}/meetings/{Id}/summary";
        http.On(put, 200);
        var model = Model();
        await model.Sync();
        var agents = model.SummaryOf(Id)!;

        model.Edit(Id, s => s with { Title = "Budget, agreed", Points = ["A point.", " "] });
        Assert.True(model.IsEdited(Id));
        await model.SaveSummary(Id, leaving: true);
        var saved = Api.Decode<Dictionary<string, Summary>>(System.Text.Encoding.UTF8.GetBytes(http.Requests.Last(r => r.Route == put).Body))["summary"];
        Assert.Equal(["A point."], saved.Points);  // the emptied point isn't sent
        Assert.Equal(saved, model.Summaries[Id]);
        Assert.Empty(model.Drafts);
        Assert.Equal("Budget, agreed", model.Meetings[0].Title);

        model.Undo(Id);
        Assert.Equal(agents, model.SummaryOf(Id));
        model.Edit(Id, s => s with { Title = s.Title });  // writing back what it shows isn't an edit, so Redo still has its step
        model.Redo(Id);
        Assert.Equal("Budget, agreed", model.SummaryOf(Id)!.Title);
    });

    /// <summary>A full disk, or the longest a recording can be: the recording ends as Stop ends it, keeping what it has.</summary>
    [Fact]
    public void A_recorder_that_stops_by_itself_ends_the_recording_and_keeps_it() => UiThread.Run(async () =>
    {
        var model = Model();
        await model.StartRecording();
        var recorder = (FakeRecorder)model.Recorder!;
        Fixtures.Wav(recorder.Path, frames: 48_000);
        recorder.Levels = new(Failed: "Tally stopped the recording at 4 hours, the longest it records.");
        await Until(() => model.Recordings[0].State != RecordingState.Recording);
        Assert.Null(model.Recorder);
        Assert.Equal(RecordingState.Saved, Assert.Single(model.Recordings).State);
        Assert.Equal("Tally stopped the recording at 4 hours, the longest it records. What it recorded is kept.", model.Problem?.Message);
    });

    [Fact]
    public void Without_an_aac_encoder_the_recording_waits_and_says_what_windows_needs() => UiThread.Run(async () =>
    {
        platform.NoEncoder = true;
        var recording = new Recording("01A", DateTimeOffset.UtcNow.AddHours(-1), RecordingState.Recording) { Root = Root };
        recording.Save();
        Fixtures.Wav(recording.Wav, frames: 48_000);
        var model = Model();
        await model.Recover();
        var broken = Assert.Single(model.Recordings);
        Assert.Equal(RecordingState.Broken, broken.State);
        Assert.Contains("Media Feature Pack", broken.Error);
        Assert.Equal(AppModel.OptionalFeaturesSettings, model.Problem?.Settings);
        Assert.True(File.Exists(recording.Wav));  // Try Again, once it's added
    });

    /// <summary>A Mac's file names may hold what Windows' can't: the copy here gets a name it takes.</summary>
    [Fact]
    public void A_file_is_opened_under_a_name_windows_takes() => UiThread.Run(async () =>
    {
        http.On("GET https://s3.test/a", 200, "slides");
        var model = Model();
        var path = await model.Local(new MeetingFile("a_1", "Plan: v2? <draft>.", 6, DateTimeOffset.UtcNow, new Uri("https://s3.test/a")), Id);
        Assert.Equal("Plan- v2- -draft-", Path.GetFileName(path));
        Assert.Equal("slides", File.ReadAllText(path!));
    });

    /// <summary>A move that fails isn't taken for a second click that got there first.</summary>
    [Fact]
    public void A_file_that_cant_be_put_in_place_isnt_opened() => UiThread.Run(async () =>
    {
        http.On("GET https://s3.test/a", 200, "slides");
        var model = Model();
        Directory.CreateDirectory(Path.Combine(model.FileCache, Id, "a_1", "slides.pdf"));  // in the way
        Assert.Null(await model.Local(new MeetingFile("a_1", "slides.pdf", 6, DateTimeOffset.UtcNow, new Uri("https://s3.test/a")), Id));
        Assert.NotNull(model.Problem);
    });

    /// <summary>The 30 s running out while Continue Recording waits for the microphone: the recording is sent, and
    /// nothing goes on as if it followed the call.</summary>
    [Fact]
    public void A_recording_sent_while_it_resumes_stays_sent() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        var resumed = new TaskCompletionSource();
        ((FakeRecorder)model.Recorder!).Resuming = () => resumed.Task;
        var continuing = model.ContinueRecording();
        await Seconds(model, 30);  // not continued in time
        Assert.Null(model.Recorder);
        resumed.SetResult();
        Assert.False(await continuing);
        Assert.Null(model.StoppedByCall);
        Assert.Null(model.RecordingCall);
        await Until(() => Assert.Single(model.Recordings).State == RecordingState.Saved);
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.Offer, model.Drawer?.Kind);  // the next call is offered as any is
    });

    /// <summary>New Meeting records the new call at once: the last one's encode (minutes, for an hour) waits for nothing.</summary>
    [Fact]
    public void A_new_meeting_records_before_the_last_one_is_saved() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        var last = model.LiveId;
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        platform.EncodeWaits.Reset();
        var newMeeting = model.NewMeeting();
        Assert.NotNull(model.Recorder);
        Assert.NotEqual(last, model.LiveId);
        Assert.Equal(RecordingState.Recording, model.Recordings.Single(r => r.Id == last).State);  // still encoding
        platform.EncodeWaits.Set();
        await newMeeting;
        Assert.Equal(RecordingState.Saved, model.Recordings.Single(r => r.Id == last).State);
    });

    /// <summary>The Same Meeting when the microphone can't be opened again: what was recorded is sent, as if the question
    /// went unanswered (in auto mode, the new call is recorded), and the owner hears why.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_same_meeting_that_cant_carry_on_goes_as_if_unanswered(bool auto) => UiThread.Run(async () =>
    {
        var model = await CallEnded(auto);
        var last = model.LiveId;
        ((FakeRecorder)model.Recorder!).Resuming = () => Task.FromException(new AppError("Tally couldn't carry on recording (no microphone)."));
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        await model.SameMeeting();
        Assert.Null(model.StoppedByCall);
        Assert.Equal(RecordingState.Saved, model.Recordings.Single(r => r.Id == last).State);
        Assert.Equal(auto, model.Recorder != null);
        Assert.NotEqual(last, model.LiveId);
        Assert.Equal("Tally couldn't carry on recording (no microphone).", model.Problem?.Message);
    });

    /// <summary>A full disk two seconds into a call recorded by itself: it isn't kept, and the owner hears about the disk
    /// and that it wasn't kept.</summary>
    [Fact]
    public void A_short_auto_recording_stopped_by_a_full_disk_says_so() => UiThread.Run(async () =>
    {
        var model = Model();
        await model.StartRecording(auto: true);
        var recorder = (FakeRecorder)model.Recorder!;
        Fixtures.Wav(recorder.Path, frames: 48_000);
        recorder.Levels = new(Failed: "Tally stopped recording: the disk is full.");
        await Until(() => model.Recorder == null);
        Assert.Empty(model.Recordings);
        Assert.Equal("Tally stopped recording: the disk is full. Under 2 minutes, the recording wasn't kept.", model.Problem?.Message);
    });

    /// <summary>The 30 s after a call run out while an hour's encode takes minutes: the next call is offered as it
    /// starts, not once the encode is done.</summary>
    [Fact]
    public void A_call_is_offered_while_the_last_recording_encodes() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        var last = model.LiveId;
        platform.EncodeWaits.Reset();
        var looking = Seconds(model, 30);  // not continued: sent
        await Until(() => looking.IsCompleted);
        Assert.Null(model.StoppedByCall);
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.Offer, model.Drawer?.Kind);
        Assert.Equal(RecordingState.Recording, model.Recordings.Single(r => r.Id == last).State);  // still encoding
        platform.EncodeWaits.Set();
        await Until(() => model.Recordings.Single(r => r.Id == last).State == RecordingState.Saved);
    });

    /// <summary>The Same Meeting just before the question's 30 s run out, while the microphone takes a moment to open
    /// again: the answer holds, and the recording carries on rather than being sent as if unanswered.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_same_meeting_answered_as_the_question_folds_carries_on(bool auto) => UiThread.Run(async () =>
    {
        var model = await CallEnded(auto);
        model.SameMeetingWaits = TimeSpan.FromMilliseconds(100);
        var last = model.LiveId;
        var recorder = model.Recorder;
        var resumed = new TaskCompletionSource();
        ((FakeRecorder)recorder!).Resuming = () => resumed.Task;
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        var answering = model.SameMeeting();
        await Task.Delay(500);  // the question's time runs out while the microphone opens
        resumed.SetResult();
        await answering;
        await Task.Delay(500);  // and nothing folds it afterwards
        Assert.Same(recorder, model.Recorder);
        Assert.Equal(last, model.LiveId);
        Assert.Null(model.StoppedByCall);
        Assert.Null(model.Drawer);
        Assert.Equal(RecordingState.Recording, Assert.Single(model.Recordings).State);
    });

    /// <summary>A screenshot pasted into a meeting: added, and the copy made to add it gone, not left in the temp folder.</summary>
    [Fact]
    public void A_pasted_image_is_added_and_its_copy_goes() => UiThread.Run(async () =>
    {
        SignIn();
        http.On($"POST {FakeHttp.Api}/meetings/{Id}/files", 200, """{"file_id": "a_1", "upload_url": "https://s3.test/up", "upload_headers": {}}""");
        http.On("PUT https://s3.test/up", 200);
        http.On($"GET {FakeHttp.Api}/meetings/{Id}/files", 200, """{"files": []}""");
        var model = Model();
        platform.Clipboard = [0x89, 0x50, 0x4E, 0x47];
        await model.PasteImage(Id);
        var sent = http.Requests.Single(r => r.Route == $"POST {FakeHttp.Api}/meetings/{Id}/files");
        Assert.Contains("Pasted", sent.Body);
        Assert.Contains("PUT https://s3.test/up", http.Routes);
        var name = System.Text.Json.JsonDocument.Parse(sent.Body).RootElement.GetProperty("name").GetString()!;
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }));
    });

    /// <summary>Paste while another program holds the clipboard: said, rather than nothing happening.</summary>
    [Fact]
    public void A_paste_the_clipboard_wont_give_says_so() => UiThread.Run(async () =>
    {
        var model = Model();
        platform.ClipboardBusy = true;
        await model.PasteImage(Id);
        Assert.Contains("clipboard", model.Problem?.Message);
    });

    /// <summary>A recording still saving after Stop: Update waits for it, as the installer would refuse it then.</summary>
    [Fact]
    public void Update_waits_for_a_recording_still_saving() => UiThread.Run(async () =>
    {
        var model = await UpdateOffered();
        await model.StartRecording();
        platform.EncodeWaits.Reset();
        var stop = model.StopRecording();
        Assert.Null(model.Recorder);
        Assert.True(model.RecordingOrSaving);  // saving: the installer would say it's recording
        platform.EncodeWaits.Set();
        await stop;
        Assert.False(model.RecordingOrSaving);
    });

    /// <summary>Update while the install page can't be reached: said so, not a flash of Updating and nothing.</summary>
    [Fact]
    public void An_update_that_cant_reach_the_install_page_says_so() => UiThread.Run(async () =>
    {
        var model = await UpdateOffered();
        Assert.NotNull(model.Update);
        platform.InstallerUnreachable = true;
        await model.InstallUpdate();
        Assert.Contains("couldn't reach tally.test", model.Problem?.Message);
        Assert.False(model.Updating);
    });

    /// <summary>Listen on a Windows N without its Media Feature Pack: said, with where to add it, rather than a crash.</summary>
    [Fact]
    public void Listen_without_a_media_player_says_what_to_add()
    {
        var model = Model();
        platform.NoPlayer = true;
        model.Listen([new Uri("https://s3.test/clip.m4a")]);
        Assert.Contains("Media Feature Pack", model.Problem?.Message);
    }

    /// <summary>A look at what's using audio that fails (Windows can throw): the next one carries on, and the call is offered.</summary>
    [Fact]
    public void A_look_that_fails_doesnt_stop_calls_being_seen() => UiThread.Run(async () =>
    {
        var model = Model();
        platform.UsersFail = new System.Runtime.InteropServices.COMException("the audio service restarted");
        OnCall(true);
        await Seconds(model, 12);
        Assert.Equal(CallPromptKind.Offer, model.Drawer?.Kind);
    });

    /// <summary>Do Nothing chosen while "Same meeting?" is asked of a stopped recording: calls are left alone, and
    /// the recording is sent, as if the question went unanswered, not left waiting.</summary>
    [Fact]
    public void Do_nothing_during_same_meeting_sends_the_stopped_recording() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        var last = model.LiveId;
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        model.CallMode = CallMode.Off;
        await Until(() => model.Recordings.Single(r => r.Id == last).State == RecordingState.Saved);
        Assert.Null(model.Recorder);
        Assert.Null(model.Drawer);
    });

    /// <summary>Continue Recording from the flyout as "Same meeting?" runs out, when the microphone then can't be opened
    /// again: the question folds into its unanswered way once that's known, so the recording is still sent.</summary>
    [Fact]
    public void A_continue_that_fails_as_the_question_folds_still_sends() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        model.SameMeetingWaits = TimeSpan.FromMilliseconds(100);
        var last = model.LiveId;
        var resumed = new TaskCompletionSource();
        ((FakeRecorder)model.Recorder!).Resuming = () => resumed.Task;
        OnCall(true);
        await Seconds(model, 11);
        var continuing = model.ContinueRecording();
        await Task.Delay(500);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);  // waiting for how the Continue goes
        resumed.SetException(new AppError("Tally couldn't carry on recording (no microphone)."));
        Assert.False(await continuing);
        await Until(() => model.Recordings.Single(r => r.Id == last).State == RecordingState.Saved);
        Assert.Null(model.Recorder);
        Assert.Null(model.StoppedByCall);
        Assert.Null(model.Drawer);
    });

    /// <summary>Continue Recording from the flyout while "Same meeting?" shows, when the microphone can't be opened
    /// again: the question stays, to be answered or to fold into its unanswered way, so the recording is still sent.</summary>
    [Fact]
    public void A_continue_that_fails_leaves_the_same_meeting_question() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        var last = model.LiveId;
        ((FakeRecorder)model.Recorder!).Resuming = () => Task.FromException(new AppError("Tally couldn't carry on recording (no microphone)."));
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        Assert.False(await model.ContinueRecording());
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        Assert.Equal("Tally couldn't carry on recording (no microphone).", model.Problem?.Message);
        await model.NewMeeting();
        Assert.Equal(RecordingState.Saved, model.Recordings.Single(r => r.Id == last).State);
        Assert.NotEqual(last, model.LiveId);
    });

    /// <summary>Send Now while "Same meeting?" shows: the question goes with the recording it was about.</summary>
    [Fact]
    public void Send_now_puts_the_same_meeting_question_away() => UiThread.Run(async () =>
    {
        var model = await CallEnded();
        OnCall(true);
        await Seconds(model, 11);
        Assert.Equal(CallPromptKind.SameMeeting, model.Drawer?.Kind);
        await model.StopRecording();
        Assert.Null(model.Drawer);
        Assert.Equal(RecordingState.Saved, Assert.Single(model.Recordings).State);
    });

    /// <summary>A voice confirmed in another meeting: this meeting's copy in the folder says so too, as its page does.</summary>
    [Fact]
    public void A_voice_confirmed_elsewhere_reaches_the_meetings_folder() => UiThread.Run(async () =>
    {
        SignIn();
        Listing("ready");
        http.On($"GET {FakeHttp.Api}/meetings/{Id}", 200, Detail("""{"name": "Ada", "person_id": "p_ada", "by": "agent"}"""));
        string People(string source) =>
            $$"""{"people": [{"person_id": "p_ada", "name": "Ada", "aliases": [], "is_owner": false, "voiceprints": [{"source": "{{source}}", "meeting_id": "m_other"}]}]}""";
        http.On($"GET {FakeHttp.Api}/people", 200, People("auto"));
        var model = Model();
        await model.Auth.Token();
        await model.Sync();  // Ada among the owner's people before the folder is filled
        Assert.Single(model.People);
        var root = Path.Combine(temp.Path, "Meetings");
        model.MeetingsFolder = root;
        string Copy() => Directory.Exists(root) ? Directory.GetFiles(root, "meeting.json", SearchOption.AllDirectories).Select(File.ReadAllText).SingleOrDefault() ?? "" : "";
        await Until(() => File.Exists(Path.Combine(root, "index.json")));
        Assert.Contains("\"confirmed\": false", Copy());
        http.On($"GET {FakeHttp.Api}/people", 200, People("user"));  // the owner confirmed Ada in another meeting
        await model.Sync();
        await Until(() => Copy().Contains("\"confirmed\": true"));
    });

    /// <summary>Change Folder…: what's remembered is the account's, not the folder's, so the new folder gets every
    /// meeting's files too.</summary>
    [Fact]
    public void A_new_meetings_folder_gets_the_files_too() => UiThread.Run(async () =>
    {
        SignIn();
        Ready();
        WithSlides();
        var model = Model();
        await model.Auth.Token();
        var first = Path.Combine(temp.Path, "Meetings");
        model.MeetingsFolder = first;
        await Until(() => File.Exists(Path.Combine(first, "index.json")));  // the pass is done
        Assert.Equal("slides", File.ReadAllText(Slides(first)));

        var second = Path.Combine(temp.Path, "Elsewhere");
        model.MeetingsFolder = second;
        await Until(() => File.Exists(Path.Combine(second, "index.json")));
        Assert.Equal("slides", File.ReadAllText(Slides(second)));
    });

    /// <summary>A meetings folder pass that outlasts the access token (a first fill takes minutes): it asks for a fresh one
    /// as it goes, rather than taking the expired one's refusal for a sign-in to redo.</summary>
    [Fact]
    public void A_meetings_folder_pass_longer_than_a_token_renews_it_as_it_goes() => UiThread.Run(async () =>
    {
        SignIn();
        http.SignedIn(expiresIn: 31);  // a second past the 30 s it's renewed before
        Ready();
        http.On($"GET {FakeHttp.Api}/meetings/{Id}/files", 200, """{"files": []}""");
        var model = Model();
        await model.Auth.Token();
        var tokens = http.Routes.Count(r => r == $"POST {FakeHttp.Token}");
        http.On($"GET {FakeHttp.Api}/meetings/{Id}", request =>
        {
            Thread.Sleep(1500);  // the detail outlasts the token
            return (200, Detail());
        });
        model.MeetingsFolder = Path.Combine(temp.Path, "Meetings");
        await Until(() => File.Exists(Path.Combine(temp.Path, "Meetings", "index.json")));
        Assert.True(http.Routes.Count(r => r == $"POST {FakeHttp.Token}") > tokens);  // renewed for the files, after the detail
        Assert.Null(model.FolderProblem);
    });

    /// <summary>A new meetings folder filled while the token can't be renewed (offline, as it ran out): the meeting's
    /// files aren't taken for fetched, and come once it can be.</summary>
    [Fact]
    public void A_folder_filled_while_the_token_cant_be_renewed_gets_its_files_later() => UiThread.Run(async () =>
    {
        SignIn();
        http.SignedIn(expiresIn: 32);  // two seconds past the 30 s it's renewed before
        Ready();
        WithSlides();
        var model = Model();
        await model.Auth.Token();
        var first = Path.Combine(temp.Path, "Meetings");
        model.MeetingsFolder = first;
        await Until(() => File.Exists(Path.Combine(first, "index.json")));
        http.On($"POST {FakeHttp.Token}", 503);  // the provider can't be reached
        http.On($"GET {FakeHttp.Api}/people", _ =>
        {
            Thread.Sleep(2500);  // and the token comes within its last 30 s while the sync runs
            return (200, """{"people": []}""");
        });
        var second = Path.Combine(temp.Path, "Elsewhere");
        model.MeetingsFolder = second;
        await Until(() => Directory.Exists(second) && Directory.GetFiles(second, "meeting.json", SearchOption.AllDirectories).Length == 1);
        await Task.Delay(500);
        Assert.Equal("", Slides(second));
        http.SignedIn();  // back
        http.On($"GET {FakeHttp.Api}/people", 200, """{"people": []}""");
        await model.Sync();
        await Until(() => Slides(second) != "");
        Assert.Equal("slides", File.ReadAllText(Slides(second)));
    });

    /// <summary>A file that fails to download into a new meetings folder, after its meeting.json is written there: the
    /// next pass doesn't take the meeting for whole, and fetches it.</summary>
    [Fact]
    public void A_file_that_failed_to_download_into_a_new_folder_comes_next_time() => UiThread.Run(async () =>
    {
        SignIn();
        Ready();
        WithSlides();
        var model = Model();
        await model.Auth.Token();
        var first = Path.Combine(temp.Path, "Meetings");
        model.MeetingsFolder = first;
        await Until(() => File.Exists(Path.Combine(first, "index.json")));

        http.On("GET https://s3.test/f", 503, "");
        var second = Path.Combine(temp.Path, "Elsewhere");
        model.MeetingsFolder = second;
        await Until(() => model.FolderProblem != null);
        Assert.Empty(Directory.GetFiles(second, "slides.pdf", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(second, "meeting.json", SearchOption.AllDirectories));

        http.On("GET https://s3.test/f", 200, "slides");
        await model.Sync();
        await Until(() => model.FolderProblem == null && Directory.GetFiles(second, "slides.pdf", SearchOption.AllDirectories).Length == 1);
        Assert.Equal("slides", File.ReadAllText(Directory.GetFiles(second, "slides.pdf", SearchOption.AllDirectories)[0]));
    });
}
