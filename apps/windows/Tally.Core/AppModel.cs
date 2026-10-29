using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tally;

/// <summary>
/// The app's state: recordings on this PC, the meetings and people the API knows (the signed-in person's), the
/// recording in progress, the sync between them, and what the window shows.
///
/// Recording never waits for anything. A recording is sent as soon as the owner is signed in; the API owns the
/// meeting from then on, and once its summary is ready the recording's folder on this PC is deleted (S3 has it).
///
/// Every member is called on the UI thread, and every await comes back to it. Collections are immutable, replaced
/// on each change, with PropertyChanged raised for them and for what's worked out from them.
/// </summary>
public sealed class AppModel : ObservableObject
{
    public const string MicrophoneSettings = "ms-settings:privacy-microphone";
    public const string LoginItemsSettings = "ms-settings:startupapps";
    public const string OptionalFeaturesSettings = "ms-settings:optionalfeatures";

    private readonly Config config;
    private readonly HttpClient http;
    private readonly Settings settings;
    private readonly Notifier notifier;
    private readonly CallRules calls = new();
    private string? callApp;  // the call's app, as the offer named it

    public AppModel(Config config, Platform platform, HttpMessageHandler? handler = null)
    {
        this.config = config;
        Platform = platform;
        http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        settings = Settings.Load(System.IO.Path.Combine(platform.DataFolder, "settings.json"));
        notifier = new Notifier(platform.Notifications);
        Auth = new Auth(config, platform.Secrets, platform.Browser, http);
        Auth.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Auth.State)) Notify(nameof(WaitingForSignIn));
            if (e.PropertyName == nameof(Auth.Subject)) Notify(nameof(MeetingsFolder));
        };
        Recordings = [.. Recording.LoadAll(RecordingsFolder)];
        Crash = Crashes.Pending(settings, platform.Crashes.Find());
        calls.Offering = CallMode != CallMode.Off;
        NotFiled = [.. settings.NotFiled];
    }

    /// <summary>After a crash or a quit, picks up where it was, and starts the loops: the sync, calls and updates. Once,
    /// when the app has started.</summary>
    public void Start()
    {
        _ = Recover();
        _ = SyncLoop();
        _ = WatchCalls();
        _ = WatchUpdates();
    }

    public Platform Platform { get; }
    public Auth Auth { get; }
    public IClipPlayer Player => Platform.Player;

    /// <summary>A speaker's clips played, or paused. Windows N without its Media Feature Pack has no player: said, not a crash.</summary>
    public void Listen(IReadOnlyList<Uri> clips)
    {
        try { Platform.Player.Toggle(clips); }
        catch (NotSupportedException)
        {
            Problem = new("Tally couldn't play the clips: Windows has no media player. Add the Media Feature Pack in Optional features, in Settings.",
                OptionalFeaturesSettings);
        }
    }

    /// <summary>What people know the sign-in by, as config's auth.name says: "Sign In to &lt;name&gt;".</summary>
    public string SignInName => config.SignInName;
    private string RecordingsFolder => System.IO.Path.Combine(Platform.DataFolder, "recordings");

    public ImmutableList<Recording> Recordings
    {
        get;
        private set { if (SetProperty(ref field, value)) Notify(nameof(Items), nameof(Recording), nameof(WaitingForSignIn)); }
    } = [];

    public ImmutableList<Meeting> Meetings { get; private set { if (SetProperty(ref field, value)) Notify(nameof(Items)); } } = [];

    public ImmutableList<Person> People
    {
        get;
        private set { if (SetProperty(ref field, value)) Notify(nameof(PeopleById), nameof(SelectedPerson)); }
    } = [];

    /// <summary>Ready meetings' results, by id.</summary>
    public ImmutableDictionary<string, Results> Results { get; private set => SetProperty(ref field, value); } = ImmutableDictionary<string, Results>.Empty;

    /// <summary>Ready meetings whose results couldn't be read, and why.</summary>
    public ImmutableDictionary<string, string> LoadErrors { get; private set => SetProperty(ref field, value); } = ImmutableDictionary<string, string>.Empty;

    /// <summary>The owner's own copies, as saved.</summary>
    public ImmutableDictionary<string, Summary> Summaries { get; private set => SetProperty(ref field, value); } = ImmutableDictionary<string, Summary>.Empty;

    /// <summary>The owner's edits, before they're saved.</summary>
    public ImmutableDictionary<string, Summary> Drafts { get; private set => SetProperty(ref field, value); } = ImmutableDictionary<string, Summary>.Empty;

    public ImmutableHashSet<string> SavingSummary { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>What the owner added to each meeting.</summary>
    public ImmutableDictionary<string, ImmutableList<MeetingFile>> Files { get; private set => SetProperty(ref field, value); } =
        ImmutableDictionary<string, ImmutableList<MeetingFile>>.Empty;

    /// <summary>How many files are on their way to each meeting.</summary>
    public ImmutableDictionary<string, int> AddingFiles { get; private set { if (SetProperty(ref field, value)) Notify(nameof(Items)); } } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>Files being downloaded to show.</summary>
    public ImmutableHashSet<string> Opening { get; private set => SetProperty(ref field, value); } = [];

    private readonly Dictionary<string, int> saves = [];  // saves and restores of each meeting's copy: a load from before one doesn't undo it
    private readonly Dictionary<string, int> editCount = [];  // counts edits per meeting: a save waits for a second without one

    public IRecorder? Recorder { get; private set => SetProperty(ref field, value); }

    /// <summary>The recording in progress; a broken one being encoded again is not it.</summary>
    public string? LiveId { get; private set { if (SetProperty(ref field, value)) Notify(nameof(Items), nameof(Recording)); } }

    public bool Starting { get; private set => SetProperty(ref field, value); }
    public bool Offline { get; private set => SetProperty(ref field, value); }

    /// <summary>A crash report from last time, for the owner to send or not.</summary>
    public CrashReport? Crash { get; private set => SetProperty(ref field, value); }

    /// <summary>A newer version on the install page.</summary>
    public string? Update { get; private set => SetProperty(ref field, value); }

    public bool Updating { get; private set => SetProperty(ref field, value); }

    /// <summary>The call ended and the recording paused: it can still go on.</summary>
    public CallStop? StoppedByCall { get; private set => SetProperty(ref field, value); }

    /// <summary>What the recording in progress follows: the call's app ("Zoom"), "" for a call without a name, null in the room.</summary>
    public string? RecordingCall => calls.Recording == CallRules.State.Following ? callApp ?? "" : null;

    /// <summary>The drawer under the tray icon that offers to record a call; null when hidden.</summary>
    public CallPrompt? Drawer { get; private set => SetProperty(ref field, value); }

    public bool SendingCrash { get; private set => SetProperty(ref field, value); }

    /// <summary>The last sync failed for a reason other than being offline; cleared by the next that works.</summary>
    public string? SyncError { get; private set => SetProperty(ref field, value); }

    /// <summary>The recording's time, for the tray: updated once a second, which is also when to read the levels.</summary>
    public string Elapsed { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>The last thing that went wrong, for the owner; set to null to dismiss it.</summary>
    public Problem? Problem { get; set => SetProperty(ref field, value); }

    // What the window shows.

    /// <summary>The meeting shown, by id.</summary>
    public string? Selection { get; set => SetProperty(ref field, value); }

    public AssignForm? Assigning { get; set => SetProperty(ref field, value); }
    public PersonForm? Editing { get; set => SetProperty(ref field, value); }
    public Item? Deleting { get; set => SetProperty(ref field, value); }
    public Person? DeletingPerson { get; set => SetProperty(ref field, value); }

    /// <summary>One at a time: every action on a person is on one.</summary>
    public string? PeopleSelection { get; set { if (SetProperty(ref field, value)) Notify(nameof(SelectedPerson)); } }

    /// <summary>People's order, kept while elsewhere.</summary>
    public PeopleSort PeopleSort { get; set => SetProperty(ref field, value); } = PeopleSort.Name;

    public bool PeopleAscending { get; set => SetProperty(ref field, value); } = true;
    public Person? SelectedPerson => People.FirstOrDefault(p => p.PersonId == PeopleSelection);
    public bool DiscardArmed { get; private set => SetProperty(ref field, value); }

    /// <summary>"meeting/speaker" being saved: their cards show it at once.</summary>
    public ImmutableHashSet<string> Pending { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>A person's edit on its way.</summary>
    public bool Saving { get; private set => SetProperty(ref field, value); }

    /// <summary>Where the window is, in its sidebar: Meetings (every meeting), one of the owner's folders (its meetings
    /// and its folders'), or People.</summary>
    public Place Place
    {
        get;
        set
        {
            SetProperty(ref field, value);
            KeepSelectionInPlace();
        }
    } = Place.Meetings;

    /// <summary>Keeps the shown meeting in the list; when it isn't there, shows the newest instead.</summary>
    private void KeepSelectionInPlace()
    {
        if (Place == Place.People || SearchResults != null || Selection is not { } id || ItemsIn(Place).Any(i => i.Id == id)) return;
        Selection = ItemsIn(Place).MaxBy(i => i.StartedAt)?.Id;
    }

    public ImmutableList<Folder> Folders { get; private set { if (SetProperty(ref field, value)) Notify(nameof(FolderChoices)); } } = [];

    /// <summary>A folder's name being typed, in the sidebar.</summary>
    public string? RenamingFolder { get; set => SetProperty(ref field, value); }

    /// <summary>Folders open in the sidebar, showing the folders inside.</summary>
    public ImmutableHashSet<string> Expanded { get; set => SetProperty(ref field, value); } = [];

    public Folder? DeletingFolder { get; set => SetProperty(ref field, value); }

    /// <summary>The search field's.</summary>
    public string Query
    {
        get;
        set
        {
            SetProperty(ref field, value);
            SearchSoon();
        }
    } = "";

    /// <summary>Null: not searching.</summary>
    public ImmutableList<SearchHit>? SearchResults { get; private set => SetProperty(ref field, value); }

    /// <summary>No results because it couldn't search, not because nothing matched.</summary>
    public bool SearchFailed { get; private set => SetProperty(ref field, value); }

    private int searches;  // counts keystrokes: a search waits for a pause, and an older one's answer is dropped

    private bool syncing;
    private bool syncAgain;
    private readonly HashSet<string> deletingIds = [];  // a sync never sends a meeting while it's being deleted
    private int discardArming;
    private readonly HashSet<string> loading = [];
    private readonly HashSet<string> reloadNeeded = [];  // changed by the owner while a load of it was already on its way
    private int changes;  // the owner's changes: a sync that read the lists before one drops what it read
    private readonly Dictionary<string, DateTimeOffset> loadedAt = [];  // clip links last an hour

    /// <summary>The first launch opens the window, with a welcome; after that Tally starts in the tray only.</summary>
    public bool Welcomed
    {
        get => settings.Welcomed;
        set
        {
            settings.Welcomed = value;
            settings.Save();
            OnPropertyChanged();
        }
    }

    // — What the lists show

    /// <summary>A meeting as the window and the menu show it, wherever it is: still on this PC, or with the API.</summary>
    public IReadOnlyList<Item> Items
    {
        get
        {
            var remote = Meetings.Select(m => m.MeetingId).ToHashSet();
            var items = Meetings.Select(m =>
            {
                var local = Recordings.FirstOrDefault(r => r.Id == m.MeetingId);
                var duration = m.Duration ?? (m.EndedAt is { } end && m.StartedAt is { } start ? (end - start).TotalSeconds : null);
                return new Item(m.MeetingId, m.Title, m.StartedAt ?? Ulid.Date(m.MeetingId), duration, StatusOf(m.State, m.Error, local?.State),
                    local is not null && CanSend(local), InCloud: true)
                {
                    SendError = local?.Error,
                    FolderIds = m.FolderIds ?? [],
                    AddingFiles = AddingFiles.GetValueOrDefault(m.MeetingId) > 0,
                };
            }).ToList();
            foreach (var r in Recordings.Where(r => !remote.Contains(r.Id)))
            {
                var status = r.State switch
                {
                    RecordingState.Recording => r.Id == LiveId ? Status.Recording : Status.Saving,
                    RecordingState.Saved => Status.Waiting,
                    RecordingState.Uploading => Status.Uploading,
                    RecordingState.Uploaded or RecordingState.Sent => Status.Queued,
                    _ => Status.Failed(r.Error ?? "Nothing was recorded."),
                };
                items.Add(new Item(r.Id, null, r.StartedAt, r.EndedAt is { } ended ? (ended - r.StartedAt).TotalSeconds : null, status, CanSend(r),
                    InCloud: r.State is RecordingState.Uploaded or RecordingState.Sent) { SendError = r.Error });
            }
            return items.OrderByDescending(i => i.StartedAt).ToList();
        }
    }

    /// <summary>Where a meeting is, from what the API says and what this PC is doing with it: sending it (again) wins over
    /// whatever the cloud said last.</summary>
    public static Status StatusOf(string remote, string? error, RecordingState? local) => (remote, local) switch
    {
        (_, RecordingState.Uploading) => Status.Uploading,
        (not "ready", RecordingState.Saved) => Status.Waiting,
        ("ready", _) => Status.Ready,
        ("failed", _) => Status.Failed(error ?? "Something went wrong while summarising it."),
        ("processing", _) => Status.Processing,
        ("uploading", null) => Status.Waiting,
        _ => Status.Queued,
    };

    /// <summary>A recording that can be sent (again): its m4a is here, or the WAV an encode that failed left behind.</summary>
    private static bool CanSend(Recording r) => r.State != RecordingState.Broken || System.IO.File.Exists(r.Wav);

    public Recording? Recording => Recordings.FirstOrDefault(r => r.Id == LiveId);

    /// <summary>Tally as a startup app, so the tray has it from the start. Asked of the system each time: the owner can
    /// also change it in Settings → Apps → Startup.</summary>
    public bool OpensAtLogin
    {
        get => Platform.LoginItem.Enabled;
        set
        {
            try
            {
                if (value) Platform.LoginItem.Register(); else Platform.LoginItem.Unregister();
            }
            catch (Exception)
            {
                Problem = new("Tally couldn't change Open at login. Change it in Startup apps, in Settings.", LoginItemsSettings);
            }
            OnPropertyChanged();
        }
    }

    public int WaitingForSignIn => Auth.State == AuthState.SignedIn ? 0 : Recordings.Count(r => r.State == RecordingState.Saved);

    public IReadOnlyDictionary<string, Person> PeopleById => People.ToDictionary(p => p.PersonId);

    public static string ToConfirmText(int count) => count == 1 ? "1 person to confirm" : $"{count} people to confirm";

    /// <summary>How many of a ready meeting's speakers wait for the owner: the agent's guesses and the unknown.</summary>
    public int ToConfirm(string id)
    {
        if (Results.GetValueOrDefault(id) is not { } results) return 0;
        var names = new Names(results, PeopleById);
        return results.Speakers.Keys.Count(names.Asks);
    }

    // — Recording

    public Task ToggleRecording() => Recorder != null ? StopRecording() : StartRecording();

    public async Task StartRecording(bool auto = false)
    {
        if (Recorder != null || Starting) return;
        Starting = true;
        try
        {
            var recording = new Recording(Ulid.Make(), DateTimeOffset.UtcNow, RecordingState.Recording, Auto: auto ? true : null) { Root = RecordingsFolder };
            recording.Save();
            Player.Stop();  // a clip playing on would go into the recording
            IRecorder recorder;
            try { recorder = await Platform.Audio.Start(recording.Wav); }
            catch (Exception e)  // only the start: once it records, nothing here may remove the recording
            {
                recording.Remove();
                // Windows' privacy settings refused the microphone
                Problem = e is UnauthorizedAccessException ? new("Tally needs your microphone to record.", MicrophoneSettings) : new(e.Message);
                return;
            }
            Recorder = recorder;
            LiveId = recording.Id;
            Recordings = Recordings.Insert(0, recording);
            Problem = null;
            calls.Started();
            Notify(nameof(RecordingCall));
            HideDrawer();
            _ = Tick(recorder, recording.StartedAt);
        }
        finally
        {
            Starting = false;
        }
    }

    /// <summary>Ends and saves the recording; <paramref name="failed"/> is why it stopped by itself, if it did.</summary>
    public async Task StopRecording(string? failed = null)
    {
        if (Stopped() is not { } recording) return;
        await Finish(recording, failed);
        _ = Sync();
    }

    /// <summary>Ends the recording and returns it to be saved, ending when its call ended if a call stopped it.</summary>
    private Recording? Stopped()
    {
        var ended = StoppedByCall?.At ?? DateTimeOffset.UtcNow;  // a pause after the call isn't part of the meeting
        if (Recording is not { } recording)
        {
            StoppedByCall = null;  // nothing left to continue or send
            return null;
        }
        Platform.Log.Write("calls", "recording stopped");
        EndRecording();
        return recording with { EndedAt = ended };
    }

    /// <summary>Throws the recording in progress away, on a second click within four seconds.</summary>
    public void DiscardRecording()
    {
        if (!DiscardArmed)
        {
            DiscardArmed = true;
            var arming = ++discardArming;
            _ = Disarm();
            return;

            async Task Disarm()
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (arming == discardArming) DiscardArmed = false;  // an older arming doesn't disarm a newer one
            }
        }
        if (Recording is not { } recording) return;
        Platform.Log.Write("calls", "recording discarded by the owner");  // the logs then say why a call's recording ended
        EndRecording();
        Forget(recording.Id);
    }

    /// <summary>A recording off this PC: its folder and its row, with the selection moved to the next one.</summary>
    private void Forget(string id)
    {
        var neighbour = Neighbour(id);
        Recordings.FirstOrDefault(r => r.Id == id)?.Remove();
        Recordings = Recordings.RemoveAll(r => r.Id == id);
        if (Selection == id) Selection = neighbour;
    }

    /// <summary>Ends the recording in progress, keeping what was recorded: also on quitting, when the file is closed at
    /// once and the next launch saves and sends it, as after a crash.</summary>
    public void EndRecording()
    {
        Recorder?.Stop();
        Recorder = null;
        LiveId = null;
        DiscardArmed = false;
        StoppedByCall = null;
        calls.Ended();
        HideDrawer();  // nothing it asked about this recording, or the call after it, can be answered now
        Notify(nameof(RecordingCall));
    }

    // — Updates

    /// <summary>At launch, every 30 minutes, and when the menu opens: is there a newer Tally on the install page? A few
    /// bytes each time (windows.json). A repository build never asks.</summary>
    private async Task WatchUpdates()
    {
        while (true)
        {
            await CheckForUpdate();
            await Task.Delay(TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>Recording, or saving what was recorded: what the installer refuses to update over (a local.json that
    /// still says recording), so Update waits for it too.</summary>
    public bool RecordingOrSaving => Recordings.Any(r => r.State == RecordingState.Recording);

    public async Task CheckForUpdate()
    {
        if (config.Site is not { } site || config.Version is not { } version || Update != null) return;
        if (await Updates.Available(http, site, version) is { } latest) Update = latest;
    }

    /// <summary>Runs the installer, which quits Tally and opens the new one. If it fails, this Tally is still here to say why.</summary>
    public async Task InstallUpdate()
    {
        if (config.Site is not { } site || Update == null || RecordingOrSaving || Updating) return;
        Updating = true;
        try
        {
            var status = await Platform.Installer.Install(site);
            if (status == 0) return;
            var log = Platform.Installer.LogPath;
            var why = System.IO.File.Exists(log) ? Updates.LastFailure(await System.IO.File.ReadAllTextAsync(log)) : null;
            Problem = new("Tally couldn't update: " + (why ?? $"the installer stopped. Run the install command from {site.Host} to see why."));
        }
        catch (Exception e)
        {
            Problem = new(e is Api.Offline ? $"Tally couldn't update: it couldn't reach {site.Host}. Try again once you're online." : $"Tally couldn't update: {e.Message}");
        }
        finally
        {
            Updating = false;
        }
    }

    // — Calls

    /// <summary>What Tally does when a call starts (the menu's choice): nothing, offer to record (the default), or record
    /// by itself.</summary>
    public CallMode CallMode
    {
        get => settings.CallMode ?? CallMode.Offer;
        set
        {
            settings.CallMode = value;
            settings.Save();
            calls.Offering = value != CallMode.Off;
            if (value == CallMode.Off)
            {
                // Same Meeting? asked of a stopped recording: with calls left alone, it's sent, as unanswered would
                var asking = Drawer is { Kind: CallPromptKind.SameMeeting };
                HideDrawer();
                if (asking) _ = Unanswered();
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Once a second: what's using audio, through the rules. The rules need a clock anyway (10 s on the
    /// microphone, 30 s of quiet, 30 s to continue).</summary>
    private async Task WatchCalls()
    {
        while (true)
        {
            await ObserveCalls(DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>One look at what's using audio. A look that throws (Windows' audio APIs can) is skipped, so watching never
    /// stops and a paused recording is always sent.</summary>
    internal async Task ObserveCalls(DateTimeOffset now)
    {
        IReadOnlyList<AudioUser> users;
        try { users = await Task.Run(Platform.Audio.Users); }  // Core Audio's sessions, off the UI thread
        catch (Exception e)
        {
            Platform.Log.Write("calls", $"a look failed ({e.GetType().Name}): looking again in a second");
            return;
        }
        var events = calls.Observe(users, now);
        foreach (var e in events)
        {
            switch (e)
            {
                case CallRules.Event.Offer offer:
                    callApp = offer.App;
                    var started = $"call started: {callApp ?? "an unnamed process"} (pid {offer.Pid})";
                    if (CallMode == CallMode.Auto)
                    {
                        Platform.Log.Write("calls", started + ", recording");
                        await StartRecording(auto: true);
                        // Unless it couldn't start: the problem says why
                        if (Recorder != null) ShowDrawer(new(CallPromptKind.Recording, callApp, "Tally is recording this call."), TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        Platform.Log.Write("calls", started + ", offering");
                        ShowDrawer(new(CallPromptKind.Offer, callApp, $"Record this call with Tally? {callApp ?? "An app"} is using your microphone."),
                            TimeSpan.FromSeconds(20));
                    }
                    break;
                case CallRules.Event.Again again:
                    callApp = again.App;
                    Platform.Log.Write("calls", $"a call started while stopped: {callApp ?? "an unnamed process"}, asking");
                    ShowDrawer(new(CallPromptKind.SameMeeting, callApp, "A new call started. Is it the same meeting?"), SameMeetingWaits,
                        unanswered: () => _ = Unanswered());
                    break;
                case CallRules.Event.Stop:
                    Platform.Log.Write("calls", "call ended: recording stopped, continuable for 30 s");
                    Recorder?.Pause();
                    StoppedByCall = new(callApp, now);
                    notifier.CallEnded(callApp);
                    break;
                case CallRules.Event.Finish:
                    Platform.Log.Write("calls", "not continued: sending");
                    // Not awaited: the encode takes minutes for a long meeting, and the next call is looked for meanwhile
                    _ = StopRecording();
                    break;
            }
        }
        if (events.Count > 0) Notify(nameof(RecordingCall));
    }

    /// <summary>How long "Same meeting?" waits for an answer: the tests' to shorten.</summary>
    internal TimeSpan SameMeetingWaits { get; set; } = TimeSpan.FromSeconds(30);

    private int showing;  // counts showings: a fold scheduled for an older one leaves a newer one be
    private bool resuming;  // a Continue is opening the captures: the question it answers waits for how that goes

    /// <summary>Shows the drawer and folds it after <paramref name="folds"/>; <paramref name="unanswered"/> runs only when it
    /// folds by itself.</summary>
    private void ShowDrawer(CallPrompt prompt, TimeSpan folds, Action? unanswered = null)
    {
        Drawer = prompt;
        var shown = ++showing;
        _ = Fold();

        async Task Fold()
        {
            await Task.Delay(folds);
            // Answered as the time ran out: it folds only if the answer couldn't be carried out, as if never given
            while (resuming) await Task.Delay(TimeSpan.FromMilliseconds(200));
            if (shown != showing || Drawer == null) return;
            HideDrawer();
            unanswered?.Invoke();
        }
    }

    private void HideDrawer() => Drawer = null;

    /// <summary>Record, in the drawer's offer.</summary>
    public async Task RecordCall()
    {
        HideDrawer();
        await StartRecording();
    }

    /// <summary>Not Now, in the drawer's offer: no more offers for this call.</summary>
    public void NotNow()
    {
        calls.NotNow();
        HideDrawer();
    }

    /// <summary>Don't Record, on an auto-started recording: it goes, and this call isn't recorded again by itself.</summary>
    public void DontRecord()
    {
        if (Recording is not { Auto: true } recording) return;
        Platform.Log.Write("calls", "auto recording discarded from the drawer");
        EndRecording();
        Forget(recording.Id);
        calls.NotNow();
    }

    /// <summary>The Same Meeting, to a call that started while the last one's recording was stopped: one recording.</summary>
    public async Task SameMeeting()
    {
        if (StoppedByCall == null) return;
        Platform.Log.Write("calls", "the same meeting: continuing");
        if (await ContinueRecording()) return;
        // It couldn't carry on: as if unanswered, or nothing would ever send the stopped recording or offer the next call
        var why = Problem;
        await Unanswered();
        Problem ??= why;  // a new recording clears it
    }

    /// <summary>A New Meeting: the stopped recording is sent, and this call gets one of its own, started first so its
    /// opening isn't lost to the last one's encoding.</summary>
    public async Task NewMeeting(bool auto = false)
    {
        if (StoppedByCall == null) return;
        Platform.Log.Write("calls", "a new meeting: sending the last, recording this");
        HideDrawer();
        var last = Stopped();
        await StartRecording(auto);
        if (last is null) return;
        await Finish(last);
        _ = Sync();
    }

    /// <summary>No answer: in auto mode it's a new meeting, recorded as any call is; otherwise the last one is sent, and
    /// this call isn't recorded, as after an offer left unanswered.</summary>
    private async Task Unanswered()
    {
        if (StoppedByCall == null) return;
        if (CallMode == CallMode.Auto) await NewMeeting(auto: true); else await StopRecording();
    }

    /// <summary>Carries on in the same recording after the call seemed to end: one meeting, one summary. False when it
    /// couldn't (the owner is told why), or the recording was sent while it tried.</summary>
    public async Task<bool> ContinueRecording()
    {
        if (Recorder is not { } recorder || StoppedByCall == null) return false;
        resuming = true;
        try
        {
            await recorder.Resume();
        }
        catch (Exception e)
        {
            if (Recorder == recorder) Problem = new(e.Message);
            return false;
        }
        finally
        {
            resuming = false;
        }
        if (Recorder != recorder) return false;  // sent meanwhile: its 30 s ran out, or Send Now
        // Only now: a question about a new call that's still showing folds into its unanswered way if this fails
        HideDrawer();
        StoppedByCall = null;
        calls.Continued();
        Notify(nameof(RecordingCall));
        return true;
    }

    /// <summary>The pipeline summarises no more than 4 hours.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromHours(4);

    /// <summary>Once a second: the elapsed time, and the stop at 4 hours or when the recorder stopped by itself (a full
    /// disk).</summary>
    private async Task Tick(IRecorder recorder, DateTimeOffset start)
    {
        while (Recorder == recorder)
        {
            if (recorder.Levels.Failed is { } why)
            {
                Platform.Log.Write("calls", "the recorder stopped by itself: recording stopped");
                await StopRecording(failed: why);
                return;
            }
            var elapsed = DateTimeOffset.UtcNow - start;
            if (elapsed >= Longest)  // by the clock, so a PC asleep overnight stops when it wakes
            {
                Platform.Log.Write("calls", "the recording reached its longest: stopped");
                // Already paused by its call's end: it ends as the call ended it, without the 4-hour message
                await StopRecording(failed: StoppedByCall == null ? "Tally stopped the recording at 4 hours, the longest it records." : null);
                return;
            }
            Elapsed = Format.Clock(elapsed.TotalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Encodes the WAV to the m4a that's uploaded, deleting the WAV once the m4a is saved. If encoding fails, the
    /// WAV stays for Try Again. <paramref name="failed"/> is why the recording stopped by itself, if it did.</summary>
    private async Task Finish(Recording recording, string? failed = null)
    {
        if (recording.TooShortToKeep)  // a lobby, a mic test, dictation: not a meeting
        {
            Platform.Log.Write("calls", $"auto recording under {(int)Recording.Shortest.TotalSeconds} s discarded");
            Forget(recording.Id);
            Problem = new(failed is null
                ? "Tally didn't keep the recording it started: under 2 minutes, it wasn't a meeting."
                : failed + " Under 2 minutes, the recording wasn't kept.");
            return;
        }
        if (failed is not null) Problem = new(failed + " What it recorded is kept.");
        var (source, destination) = (recording.Wav, recording.Audio);
        try
        {
            await Task.Run(() => Platform.Audio.Encode(source, destination));
            if (Deleted()) return;
            Store(recording with { State = RecordingState.Saved, Error = null });
            TryDelete(source);
        }
        catch (Exception e)
        {
            if (Deleted()) return;
            if (Recording.Frames(source) == 0)  // stopped at once: nothing to keep or send
            {
                Forget(recording.Id);
                Problem = new("Nothing was recorded, so there's nothing to keep.");
                return;
            }
            if (e is NotSupportedException)  // Windows N without its Media Feature Pack: no recording can be saved until it's added
            {
                Store(recording with { State = RecordingState.Broken, Error = NoEncoder });
                Problem = new(NoEncoder, OptionalFeaturesSettings);
                return;
            }
            Store(recording with { State = RecordingState.Broken, Error = "Tally couldn't save the recording." });  // the system's own words are no help here
        }

        // Deleted while it saved: the delete couldn't take the files the encode held open, so they go now
        bool Deleted()
        {
            if (Recordings.Any(r => r.Id == recording.Id)) return false;
            recording.Remove();
            return true;
        }
    }

    private const string NoEncoder = "Tally couldn't save the recording: Windows has no AAC encoder. Add the Media Feature Pack in "
                                     + "Optional features, in Settings, then Try Again.";

    private async Task FinishAndSync(Recording recording)
    {
        await Finish(recording);
        await Sync();
    }

    /// <summary>After a crash or a quit: a recording still in progress keeps what was recorded, and a send that was cut
    /// off starts again. Done once it has saved what was recorded and synced.</summary>
    internal Task Recover()
    {
        var finishing = new List<Task>();
        foreach (var recording in Recordings)
        {
            switch (recording.State)
            {
                case RecordingState.Recording when System.IO.File.Exists(recording.Wav):
                    finishing.Add(FinishAndSync(recording with { EndedAt = System.IO.File.GetLastWriteTimeUtc(recording.Wav) }));
                    break;
                case RecordingState.Recording when System.IO.File.Exists(recording.Audio):
                    Store(recording with { State = RecordingState.Saved });  // encoded, but the crash came before it was marked
                    break;
                case RecordingState.Recording:
                    recording.Remove();  // the crash came before anything was recorded
                    Recordings = Recordings.RemoveAll(r => r.Id == recording.Id);
                    break;
                case RecordingState.Uploading:
                    Store(recording with { State = RecordingState.Saved });
                    break;
            }
        }
        return Task.WhenAll(finishing);
    }

    /// <summary>Saves a recording's state, unless it has been deleted meanwhile (an upload finishing after a delete).</summary>
    private void Store(Recording recording)
    {
        var index = Recordings.FindIndex(r => r.Id == recording.Id);
        if (index < 0) return;
        Recordings = Recordings.SetItem(index, recording);
        recording.Save();
    }

    // — Meetings and people

    public void Open(string id)
    {
        if (Place == Place.People || !ItemsIn(Place).Any(i => i.Id == id)) Place = Place.Meetings;  // so the list beside it shows it
        Selection = id;  // the window loads what it selects
    }

    /// <summary>A ready meeting's results, fetched again once their clip links are about to expire.</summary>
    public async Task Load(string id)
    {
        var stale = loadedAt.GetValueOrDefault(id, DateTimeOffset.MinValue) < DateTimeOffset.UtcNow.AddMinutes(-50);
        if (Items.FirstOrDefault(i => i.Id == id)?.Status != Status.Ready || !stale) return;
        if (!loading.Add(id)) return;
        if (await Connect() is not { } api)
        {
            loading.Remove(id);
            if (Auth.State == AuthState.SignedIn) LoadErrors = LoadErrors.SetItem(id, "You're offline. Try again when you're back online.");
            return;
        }
        var generation = this.generation;
        var edits = (editCount.GetValueOrDefault(id), saves.GetValueOrDefault(id));
        Detail? detail = null;
        Exception? failure = null;
        try
        {
            detail = await api.Detail(id);
        }
        catch (Exception e)
        {
            failure = e;
        }
        loading.Remove(id);
        if (generation != this.generation) return;  // signed out meanwhile
        if (detail is { Results: { } found })
        {
            Results = Results.SetItem(id, found);
            // Not over a copy saved or typed while this was on its way
            if (edits == (editCount.GetValueOrDefault(id), saves.GetValueOrDefault(id)) && !SavingSummary.Contains(id))
                Summaries = detail.Notes?.Summary is { } summary ? Summaries.SetItem(id, summary) : Summaries.Remove(id);
            loadedAt[id] = DateTimeOffset.UtcNow;
            LoadErrors = LoadErrors.Remove(id);
        }
        else if (failure is Api.Offline offline)
        {
            Offline = true;
            LoadErrors = LoadErrors.SetItem(id, offline.Message + " Tally opens it when you're back online.");  // the next sync tries again
        }
        else if (failure != null)
        {
            // The API's own words, or plain ones for a summary this version of Tally can't read
            LoadErrors = LoadErrors.SetItem(id, failure is Api.Failure ? failure.Message : "Tally couldn't read this summary. Updating Tally may help.");
            loadedAt[id] = DateTimeOffset.UtcNow;  // don't ask again at every sync; Try Again does
        }
        if (reloadNeeded.Remove(id))
        {
            loadedAt.Remove(id);
            await Load(id);
        }
    }

    public async Task Reload(string id)
    {
        loadedAt.Remove(id);
        LoadErrors = LoadErrors.Remove(id);
        await Load(id);
    }

    public async Task Delete(Item item)
    {
        // The item as it is now: a dialog may have been open while a sync started sending it.
        if (Items.FirstOrDefault(i => i.Id == item.Id) is not { CanDelete: true } now || !deletingIds.Add(now.Id)) return;
        try
        {
            var generation = this.generation;
            if (now.InCloud)
            {
                if (await Connect("delete this meeting") is not { } api) return;
                try
                {
                    await api.DeleteMeeting(now.Id);
                    changes++;
                }
                catch (Exception e)
                {
                    Report(e, "delete this meeting");
                    return;
                }
                await RefreshPeople(api, generation);  // people it alone brought in are gone too
            }
            Forget(now.Id);
            Meetings = Meetings.RemoveAll(m => m.MeetingId == now.Id);
            Results = Results.Remove(now.Id);
        }
        finally
        {
            deletingIds.Remove(now.Id);
        }
    }

    private string? Neighbour(string id)
    {
        var ids = Items.Select(i => i.Id).ToList();
        var index = ids.IndexOf(id);
        if (index < 0) return null;
        return index + 1 < ids.Count ? ids[index + 1] : index > 0 ? ids[index - 1] : null;
    }

    /// <summary>Sends a meeting again: uploading its audio again starts the pipeline again. A recording that couldn't be
    /// saved is encoded again first.</summary>
    public void Retry(Item item)
    {
        if (Recordings.FirstOrDefault(r => r.Id == item.Id) is not { State: RecordingState.Sent or RecordingState.Broken } recording) return;
        recording = recording with { Error = null };
        if (recording.State == RecordingState.Broken)
        {
            if (!System.IO.File.Exists(recording.Wav)) return;
            recording = recording with { State = RecordingState.Recording };  // "Saving…" while it's encoded again, so a second click does nothing
            Store(recording);
            _ = FinishAndSync(recording);
            return;
        }
        Store(recording with { State = RecordingState.Saved });
        _ = Sync();
    }

    /// <summary>Who a speaker is: <c>{person_id}</c>, <c>{name, role, company}</c> for someone new, <c>{unknown: true}</c>,
    /// or <c>{background: true}</c>.</summary>
    public async Task Assign(string meeting, string speaker, IReadOnlyDictionary<string, object> body)
    {
        if (await Connect("save who this is") is not { } api) return;
        var key = $"{meeting}/{speaker}";
        Pending = Pending.Add(key);
        try
        {
            var generation = this.generation;
            try
            {
                await api.Assign(meeting, speaker, body);
                changes++;
            }
            catch (Exception e)
            {
                if (generation == this.generation) Report(e, "save who this is");
                return;
            }
            loadedAt.Remove(meeting);
            if (loading.Contains(meeting)) reloadNeeded.Add(meeting);  // a load on its way may have read it before this change
            // People first: someone new must be among them to show as named. If that fails, the change is still saved,
            // and the next sync brings both.
            IReadOnlyList<Person> fresh;
            try
            {
                fresh = await api.People();
            }
            catch (Exception)
            {
                _ = Sync();
                return;
            }
            if (generation != this.generation) return;
            People = [.. fresh];
            if (!loading.Contains(meeting)) await Load(meeting);
        }
        finally
        {
            Pending = Pending.Remove(key);
        }
    }

    /// <summary>The summary written again without the background voices, replacing the owner's copy when they said so
    /// (<paramref name="replace"/>). Shown as on its way at once; the sync that sees it done reads it again.</summary>
    public async Task RewriteSummary(string meeting, bool replace)
    {
        if (await Connect("rewrite the summary") is not { } api || !Meetings.Any(m => m.MeetingId == meeting)) return;
        // At once: the button gives way to "Rewriting…", and can't be pressed twice
        ChangeMeeting(meeting, m => m with { Rewriting = DateTimeOffset.UtcNow, RewriteError = null });
        changes++;  // a sync that read the list before this landed mustn't take it for done
        try
        {
            await api.RewriteSummary(meeting, replace);
            changes++;
            _ = Sync();
        }
        catch (Exception e)
        {
            ChangeMeeting(meeting, m => m with { Rewriting = null });
            Report(e, "rewrite the summary");
        }
    }

    /// <summary>Yes, with their details: someone new is named in full and confirmed in one step, not confirmed and then
    /// edited. One the agent added (<paramref name="person"/>) gets the details, and then the voice; one it only heard of
    /// is made.</summary>
    public async Task Confirm(string meeting, string speaker, string? person, string name, string role, string company)
    {
        (name, role, company) = (name.Trim(), role.Trim(), company.Trim());
        if (name.Length == 0) return;
        if (person == null)
        {
            var body = new Dictionary<string, object> { ["name"] = name };
            if (role.Length > 0) body["role"] = role;
            if (company.Length > 0) body["company"] = company;
            await Assign(meeting, speaker, body);
            return;
        }
        if (People.FirstOrDefault(p => p.PersonId == person) is { } known && (known.Name != name || (known.Role ?? "") != role || (known.Company ?? "") != company))
        {
            if (await Connect("save who this is") is not { } api) return;
            try
            {
                await api.UpdatePerson(person, new Dictionary<string, object> { ["name"] = name, ["role"] = role, ["company"] = company });
                changes++;
            }
            catch (Exception e)
            {
                Report(e, "save who this is");
                return;
            }
        }
        await Assign(meeting, speaker, new Dictionary<string, object> { ["person_id"] = person });
    }

    /// <summary>The crash report from last time, to the API, which keeps it and emails the deployment's owner.</summary>
    public async Task SendCrash()
    {
        if (Crash is not { } crash || SendingCrash || await Connect("send the crash report") is not { } api) return;
        SendingCrash = true;
        try
        {
            await api.ReportCrash(await System.IO.File.ReadAllTextAsync(crash.Path), AppVersion);
            Crashes.Handled(settings, crash);
            Crash = null;
        }
        catch (Exception e)
        {
            Report(e, "send the crash report");
        }
        finally
        {
            SendingCrash = false;
        }
    }

    public void DismissCrash()
    {
        if (Crash is { } crash) Crashes.Handled(settings, crash);
        Crash = null;
    }

    // — Folders and search

    /// <summary>The meetings a place lists: Meetings, every one; a folder, its own and its folders'.</summary>
    public IReadOnlyList<Item> ItemsIn(Place place)
    {
        switch (place.Kind)
        {
            case PlaceKind.Meetings: return Items;
            case PlaceKind.Folder:
                var inside = Within(place.FolderId!);
                return Items.Where(i => i.FolderIds.Any(inside.Contains)).ToList();
            default: return [];
        }
    }

    /// <summary>A folder and every folder inside it.</summary>
    public IReadOnlySet<string> Within(string id)
    {
        var found = new HashSet<string> { id };
        while (true)
        {
            var more = Folders.Where(f => f.Parent is { } parent && found.Contains(parent) && !found.Contains(f.FolderId)).Select(f => f.FolderId).ToList();
            if (more.Count == 0) return found;
            found.UnionWith(more);
        }
    }

    /// <summary>A folder's folders, or the top's (null), in the order Explorer would list them: "Folder 2" before
    /// "Folder 10", whatever their case.</summary>
    public IReadOnlyList<Folder> Children(string? parent) =>
        Folders.Where(f => f.Parent == parent)
            .OrderBy(f => f.Name, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering)).ToList();

    /// <summary>"Customers › Acme", for menus and labels.</summary>
    public string FolderPath(string id)
    {
        var names = new List<string>();
        for (var at = id; at != null && Folders.FirstOrDefault(f => f.FolderId == at) is { } folder; at = folder.Parent) names.Insert(0, folder.Name);
        return string.Join(" › ", names);
    }

    /// <summary>Every folder, in the order the sidebar shows them: what the Folders menu offers.</summary>
    public IReadOnlyList<Folder> FolderChoices
    {
        get
        {
            IEnumerable<Folder> Walk(string? parent) => Children(parent).SelectMany(f => Walk(f.FolderId).Prepend(f));
            return Walk(null).ToList();
        }
    }

    /// <summary>A new folder, at the top or inside one, named for typing over at once.</summary>
    public async Task NewFolder(string? parent = null)
    {
        if (await Connect("make the folder") is not { } api) return;
        var taken = Children(parent).Select(f => f.Name).ToHashSet();
        var name = "New Folder";
        for (var n = 2; taken.Contains(name); n++) name = $"New Folder {n}";
        var folder = new Folder("f_" + Ulid.Make(), name, parent);
        Folders = Folders.Add(folder);
        if (parent != null) Expanded = Expanded.Add(parent);  // so the new one, and its name being typed, show
        RenamingFolder = folder.FolderId;  // named where it is, as in Explorer; the list and the meeting shown stay
        try
        {
            await api.Save(folder);
            changes++;
        }
        catch (Exception e)
        {
            Folders = Folders.RemoveAll(f => f.FolderId == folder.FolderId);
            if (RenamingFolder == folder.FolderId) RenamingFolder = null;  // else no sync would bring the folders again
            Report(e, "make the folder");
        }
    }

    public async Task RenameFolder(string id, string name)
    {
        RenamingFolder = null;
        name = name.Trim();
        if (Folders.FirstOrDefault(f => f.FolderId == id) is not { } folder || name.Length == 0 || name == folder.Name) return;
        await Change(id, f => f with { Name = name });
    }

    /// <summary>A folder into another, or to the top (null). Never inside itself, or inside one inside it.</summary>
    public async Task NestFolder(string id, string? parent)
    {
        if (parent != null && Within(id).Contains(parent) || Folders.FirstOrDefault(f => f.FolderId == id)?.Parent == parent) return;
        await Change(id, f => f with { Parent = parent });
        if (parent != null && Folders.FirstOrDefault(f => f.FolderId == id)?.Parent == parent) Expanded = Expanded.Add(parent);  // where it went shows
    }

    private async Task Change(string id, Func<Folder, Folder> edit)
    {
        if (Folders.FindIndex(f => f.FolderId == id) < 0 || await Connect("change the folder") is not { } api) return;
        var i = Folders.FindIndex(f => f.FolderId == id);
        if (i < 0) return;
        var before = Folders[i];
        Folders = Folders.SetItem(i, edit(before));
        try
        {
            await api.Save(Folders[i]);
            changes++;  // a sync that read the folders before this was saved mustn't put the old one back
        }
        catch (Exception e)
        {
            if (Folders.FindIndex(f => f.FolderId == id) is var j && j >= 0) Folders = Folders.SetItem(j, before);
            Report(e, "change the folder");
        }
    }

    /// <summary>A folder and the folders inside it; their meetings stay, in Meetings and any other folders.</summary>
    public async Task Delete(Folder folder)
    {
        if (await Connect("delete the folder") is not { } api) return;
        try
        {
            await api.DeleteFolder(folder.FolderId);
            changes++;
            var gone = Within(folder.FolderId);
            Folders = Folders.RemoveAll(f => gone.Contains(f.FolderId));
            Meetings = [.. Meetings.Select(m => m.FolderIds is { } ids ? m with { FolderIds = ids.Where(id => !gone.Contains(id)).ToList() } : m)];
            if (Place.Kind == PlaceKind.Folder && gone.Contains(Place.FolderId!)) Place = Place.Meetings;
        }
        catch (Exception e)
        {
            Report(e, "delete the folder");
        }
    }

    /// <summary>A meeting into a folder (<paramref name="inside"/>) or out of it: at once, and back if the API says no.
    /// It's the same meeting in each folder it's in, so a change to it anywhere is everywhere.</summary>
    public async Task FileMeeting(string meeting, string folder, bool inside)
    {
        var action = inside ? "add the meeting to the folder" : "take the meeting out of the folder";
        if (Meetings.FirstOrDefault(m => m.MeetingId == meeting) is not { } found || (found.FolderIds ?? []).Contains(folder) == inside
            || await Connect(action) is not { } api) return;
        Refile(meeting, folder, inside);
        changes++;
        try
        {
            await api.File(meeting, folder, inside);
            changes++;  // and after: a sync that read the list before this landed drops what it read
            KeepSelectionInPlace();
        }
        catch (Exception e)
        {
            Refile(meeting, folder, !inside);
            Report(e, action);
        }
    }

    private void Refile(string meeting, string folder, bool inside) =>
        ChangeMeeting(meeting, m => m with { FolderIds = inside ? [.. m.FolderIds ?? [], folder] : m.FolderIds?.Where(id => id != folder).ToList() });

    /// <summary>Where a meeting in no folder probably belongs, unless the owner dismissed it.</summary>
    public Filing.Suggestion? Suggestion(string id)
    {
        if (NotFiled.Contains(id) || Meetings.FirstOrDefault(m => m.MeetingId == id) is not { } meeting) return null;
        return Filing.Suggest(meeting, Meetings, People.FirstOrDefault(p => p.IsOwner)?.PersonId);
    }

    /// <summary>Dismiss, on a suggested folder: it isn't suggested for that meeting again.</summary>
    public ImmutableHashSet<string> NotFiled
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            settings.NotFiled = [.. value];
            settings.Save();
        }
    } = [];

    // — The meetings folder

    /// <summary>Where this account's meetings are copied, or null: the signed-in person's own setting, so someone else
    /// signing in on this PC never has their meetings written into it.</summary>
    public string? MeetingsFolder
    {
        get => Auth.Subject is { } account ? settings.MeetingsFolders.GetValueOrDefault(account) : null;
        set
        {
            if (Auth.Subject is not { } account) return;
            if (value is null) settings.MeetingsFolders.Remove(account); else settings.MeetingsFolders[account] = value;
            settings.Save();
            OnPropertyChanged();
            FolderProblem = null;
            if (value != null) _ = Sync();  // filled in now, not at the next tick
        }
    }

    /// <summary>The meetings folder couldn't be written: said in the menu, not just logged.</summary>
    public string? FolderProblem { get; private set => SetProperty(ref field, value); }

    private bool keepingFolder;
    private readonly Dictionary<string, Detail> copied = [];  // each meeting as the folder has it, at the stamp it remembers

    /// <summary>Keep a Copy in a Folder…: a folder picker, with a word of warning for one that syncs elsewhere.</summary>
    public async Task ChooseMeetingsFolder()
    {
        var path = await Platform.Shell.ChooseFolder("Keep Meetings Here");
        if (path is null) return;
        // OneDrive, Dropbox, iCloud Drive, Google Drive: the copy would leave this PC too
        if (Tally.MeetingsFolder.SyncsToCloud(path) && !await Platform.Shell.Confirm("This folder syncs to the cloud",
                "Your meetings' summaries, transcripts and files would be copied to that service too, not only kept on this PC.", "Use It Anyway", "Cancel"))
            return;
        MeetingsFolder = path;
    }

    /// <summary>One pass after each sync. A meeting is fetched again only when its stamps changed; a rename in People or
    /// of a folder re-renders from the cached copy. Each request gets a fresh token: a first fill can outlast one.</summary>
    private async Task KeepMeetingsFolder()
    {
        if (MeetingsFolder is not { } root || Auth.Subject is not { } account || keepingFolder) return;
        keepingFolder = true;
        try
        {
            var copy = new MeetingsFolder(root, Platform.DataFolder, account, http);
            var state = copy.LoadState();
            var people = PeopleById;
            var generation = this.generation;  // signed out meanwhile: nothing more is written or removed
            // What renders a meeting besides itself: people's names, roles and confirmed voices, folders' paths, and this code's format
            var shared = "1\n" + string.Join("\n", people.Values.OrderBy(p => p.PersonId, StringComparer.Ordinal)
                    .Select(p => $"{p.PersonId}\t{p.Name}\t{p.Role}\t{p.Company}\t{(p.IsOwner ? "true" : "false")}\t{(p.Confirmed ? "true" : "false")}"))
                + "\n" + string.Join("\n", Folders.Select(f => $"{f.FolderId}\t{FolderPath(f.FolderId)}").Order(StringComparer.Ordinal));
            var rendered = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shared)));
            var entries = new List<MeetingsFolder.IndexEntry>();
            string? failed = null;
            foreach (var meeting in Meetings.Where(m => m.State == "ready"))
            {
                var id = meeting.MeetingId;
                var stamp = $"{Seconds(meeting.UpdatedAt)}|{Seconds(meeting.EditedAt)}";
                var known = state.Meetings.GetValueOrDefault(id);
                // Read from what's kept once a launch (a transcript is big), and fetched again only when it changed
                var kept = known?.Stamp == stamp ? copied.GetValueOrDefault(id) ?? copy.Cached(id) : null;
                var changed = kept is null;
                if (changed)
                {
                    try
                    {
                        if (await Connect() is not { } api) return;  // signed out, or offline: the next pass
                        var data = await api.DetailData(id);
                        copy.Cache(data, id);
                        kept = Api.Decode<Detail>(data);
                    }
                    catch (Exception)
                    {
                        continue;  // offline, or gone: the next pass
                    }
                    if (generation != this.generation) return;
                }
                copied[id] = kept!;
                if (kept is not { Results: { } results } detail) continue;
                var summary = detail.Notes?.Summary ?? results.Summary;
                var directory = Tally.MeetingsFolder.DirectoryName(meeting, summary.Title);
                if (copy.Taken(directory, id)) directory += $" ({id[^6..]})";  // its own name, never into another's
                try
                {
                    var source = Source(meeting, results, detail, people);
                    // Also when its folder isn't there: a new folder chosen, or this one emptied in Explorer
                    var holds = copy.Holds(directory, id);
                    if (changed || known?.Rendered != rendered || known?.Directory != directory || !holds)
                    {
                        copy.Place(id, known?.Directory, directory);
                        // Written before anything downloads into it: a fill cut short is still its own, finished here next time
                        copy.Write(source with { Files = copy.Present(directory) }, directory);
                        if (changed || !holds)
                        {
                            // Signed out, or offline: this meeting's files come next pass, as any failure here does (the catch)
                            var api = await Connect() ?? throw new Api.Offline();
                            copy.Write(source with { Files = await copy.Files(await api.Files(id), directory) }, directory);
                        }
                        state.Meetings[id] = new(stamp, directory, rendered);
                    }
                    entries.Add(Tally.MeetingsFolder.EntryOf(source, directory));
                }
                catch (Exception e)
                {
                    Platform.Log.Write("meetings-folder", $"meeting {id} not written: {e.Message}");
                    failed = e.Message;
                    // Its meeting.json may be written, so the next pass would take it for whole: fetched again then
                    if (known is not null) state.Meetings[id] = known with { Stamp = "" };
                }
            }
            if (generation != this.generation) return;
            // A meeting deleted in Tally: its folder goes too, if it's the one Tally made
            foreach (var (id, entry) in state.Meetings.Where(p => !Meetings.Any(m => m.MeetingId == p.Key)).ToList())
            {
                copy.Remove(entry.Directory, id);
                copy.Forget(id);
                copied.Remove(id);
                state.Meetings.Remove(id);
            }
            try
            {
                copy.WriteIndex(entries, Auth.Account);
            }
            catch (Exception e)
            {
                failed = e.Message;  // the folder itself: gone, or a drive ejected
            }
            try { copy.Save(state); } catch (Exception) { }
            if (MeetingsFolder != root) return;  // stopped keeping it, or another chosen, meanwhile
            FolderProblem = failed is null ? null : $"Tally couldn't update your meetings folder ({System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root))}): {failed}";
        }
        finally
        {
            keepingFolder = false;
        }
    }

    /// <summary>A meeting as the folder writes it; its files are filled in where they're written (the index reads none).</summary>
    private MeetingsFolder.Source Source(Meeting meeting, Results results, Detail detail, IReadOnlyDictionary<string, Person> people) =>
        new(meeting, results, detail.Notes?.Summary ?? results.Summary, detail.Notes?.Summary != null, new Names(results, people),
            FolderChoices.Where(f => (meeting.FolderIds ?? []).Contains(f.FolderId)).Select(f => FolderPath(f.FolderId)).ToList(), []);

    private static string Seconds(DateTimeOffset? date) => ((date?.ToUnixTimeMilliseconds() ?? 0) / 1000.0).ToString(CultureInfo.InvariantCulture);

    /// <summary>Marks a meeting to be fetched again at the next pass: files were just added to it.</summary>
    private void RefetchCopy(string meeting)
    {
        if (MeetingsFolder is not { } root || Auth.Subject is not { } account) return;
        var copy = new MeetingsFolder(root, Platform.DataFolder, account, http);
        var state = copy.LoadState();
        if (state.Meetings.TryGetValue(meeting, out var entry)) state.Meetings[meeting] = entry with { Stamp = "" };
        try { copy.Save(state); } catch (Exception) { }
    }

    // — Files

    /// <summary>Copies of meetings' files, downloaded to show them: a cache, so the system may clear it.</summary>
    public string FileCache => System.IO.Path.Combine(Platform.CacheFolder, "files");

    public async Task LoadFiles(string meeting)
    {
        if (await Connect() is not { } api) return;  // the page says it's offline or signed out already
        var generation = this.generation;
        IReadOnlyList<MeetingFile>? found = null;  // none: the page says they show once Tally can reach them
        try { found = await api.Files(meeting); } catch (Exception) { }
        if (generation != this.generation) return;
        Files = found is null ? Files.Remove(meeting) : Files.SetItem(meeting, [.. found]);
    }

    private string Cached(MeetingFile file, string meeting) => System.IO.Path.Combine(FileCache, meeting, file.FileId, Tally.MeetingsFolder.FileName(file));

    /// <summary>Files from this PC (dropped, pasted or chosen) into a meeting, one after another.</summary>
    public async Task AddFiles(IReadOnlyList<string> paths, string meeting)
    {
        var files = paths.Where(System.IO.File.Exists).ToList();  // not a folder
        if (files.Count == 0 || await Connect("add the files") is null) return;
        AddingFiles = AddingFiles.SetItem(meeting, AddingFiles.GetValueOrDefault(meeting) + files.Count);
        foreach (var path in files)
        {
            try
            {
                // A fresh token for each: a big file takes longer to go up than one may have left
                var api = await Connect() ?? throw new Api.Offline();
                await api.Add(path, MimeType(path), meeting);
            }
            catch (Exception e)
            {
                Report(e, $"add “{System.IO.Path.GetFileName(path)}”");
            }
            AddingFiles = AddingFiles.SetItem(meeting, AddingFiles.GetValueOrDefault(meeting, 1) - 1);
        }
        await LoadFiles(meeting);
        RefetchCopy(meeting);  // its files are up now: the meetings folder fetches them at the next sync
    }

    /// <summary>A meeting as text, dragged out to another app: its title, when, and its overview once it's been opened.</summary>
    public string DragText(Item item)
    {
        var lines = new List<string> { item.Title ?? "Recording", Format.LongDate(item.StartedAt) };
        if (Results.GetValueOrDefault(item.Id) is { } results)
            lines.AddRange(["", new Names(results, PeopleById).Render((SummaryOf(item.Id) ?? results.Summary).Overview)]);
        return string.Join("\n", lines);
    }

    /// <summary>The meetings someone was named in: their meetings, in People.</summary>
    public IReadOnlyList<Meeting> MeetingsWith(string person) => Meetings.Where(m => m.People?.Contains(person) == true).ToList();

    /// <summary>An image on the clipboard, as a PNG file added to the meeting; the copy made to add it goes once it's up,
    /// since it's the meeting's content outside Tally's own folders.</summary>
    public async Task PasteImage(string meeting)
    {
        byte[]? png;
        try { png = await Platform.Shell.ClipboardImage(); }
        catch (Exception)  // the clipboard held by another program, or an image whose app has since closed
        {
            Problem = new("Tally couldn't read what's on the clipboard. Copy it again, then paste.");
            return;
        }
        if (png is null) return;
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = System.IO.Path.Combine(folder, DateTime.Now.ToString("'Pasted' yyyy-MM-dd 'at' HH.mm.ss'.png'", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(folder);
            await System.IO.File.WriteAllBytesAsync(path, png);
            await AddFiles([path], meeting);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A file's copy on this PC, downloaded the first time, for previewing and opening.</summary>
    public async Task<string?> Local(MeetingFile file, string meeting)
    {
        var path = Cached(file, meeting);
        if (System.IO.File.Exists(path)) return path;
        Opening = Opening.Add(file.FileId);
        try
        {
            var downloaded = await Http.Download(http, file.Url);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.Move(downloaded, path);
            }
            catch (IOException) when (System.IO.File.Exists(path))  // a second click got there first
            {
            }
            finally
            {
                TryDelete(downloaded);  // nothing there once it's moved
            }
            return path;
        }
        catch (Exception e)
        {
            if (e is Api.Offline) Offline = true;
            Problem = new(e is Api.Offline ? OfflineMessage($"open “{file.Name}”") : $"Tally couldn't open “{file.Name}”. Try again.", Offline: e is Api.Offline);
            await LoadFiles(meeting);  // perhaps its link expired: a fresh one for next time
            return null;
        }
        finally
        {
            Opening = Opening.Remove(file.FileId);
        }
    }

    public async Task Delete(MeetingFile file, string meeting)
    {
        if (await Connect("delete the file") is not { } api) return;
        try
        {
            await api.DeleteFile(file.FileId, meeting);
            if (Files.GetValueOrDefault(meeting) is { } list) Files = Files.SetItem(meeting, list.RemoveAll(f => f.FileId == file.FileId));
            try { Directory.Delete(System.IO.Path.GetDirectoryName(Cached(file, meeting))!, recursive: true); } catch (Exception) { }
        }
        catch (Exception e)
        {
            Report(e, "delete the file");
        }
    }

    /// <summary>Search, a moment after the last keystroke; the field emptied, the list again.</summary>
    private void SearchSoon()
    {
        var count = ++searches;
        var query = Query.Trim();
        if (query.Length == 0)
        {
            SearchResults = null;
            KeepSelectionInPlace();  // a result opened from elsewhere gives way to the list's own
            return;
        }
        if (SearchFailed)  // Searching…, not the last failure
        {
            SearchFailed = false;
            SearchResults = null;
        }
        _ = Search();

        async Task Search()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            if (count != searches) return;
            if (await Connect("search") is not { } api)
            {
                if (count == searches) (SearchFailed, SearchResults) = (true, []);  // not if signed out or typed meanwhile
                return;
            }
            try
            {
                var found = await api.Search(query);
                if (count == searches) (SearchFailed, SearchResults) = (false, [.. found]);
            }
            catch (Exception e)
            {
                if (count != searches) return;
                (SearchFailed, SearchResults) = (true, []);  // not the list, as if it found everything
                Report(e, "search");
            }
        }
    }

    // — The owner's summary

    /// <summary>The summary the page shows: what's being edited, else the owner's saved copy, else the agent's.</summary>
    public Summary? SummaryOf(string meeting) => Drafts.GetValueOrDefault(meeting) ?? Summaries.GetValueOrDefault(meeting) ?? Results.GetValueOrDefault(meeting)?.Summary;

    public bool IsEdited(string meeting) =>
        Summaries.ContainsKey(meeting) || Drafts.GetValueOrDefault(meeting) is { } draft && draft.Cleaned != Results.GetValueOrDefault(meeting)?.Summary;

    private readonly Dictionary<string, DateTimeOffset> lastEdit = [];  // when each meeting's summary was last edited: a burst is one undo
    private readonly Dictionary<string, Stack<Summary>> undos = [];
    private readonly Dictionary<string, Stack<Summary>> redos = [];

    /// <summary>An edit, as the owner types: shown at once, saved a second after the last one. The first makes their copy.
    /// Undoable (<see cref="Undo"/>), as in Notes: a burst of typing is one step.</summary>
    public void Edit(string meeting, Func<Summary, Summary> change) => Edit(meeting, change, undoable: true);

    private void Edit(string meeting, Func<Summary, Summary> change, bool undoable)
    {
        if (SummaryOf(meeting) is not { } shown) return;
        var found = change(shown);
        if (found == shown) return;  // a field writing back what it shows, as it first draws, isn't an edit
        if (undoable && DateTimeOffset.UtcNow - lastEdit.GetValueOrDefault(meeting, DateTimeOffset.MinValue) > TimeSpan.FromSeconds(1.5))
        {
            Stack(undos, meeting).Push(shown);
            Stack(redos, meeting).Clear();
        }
        lastEdit[meeting] = DateTimeOffset.UtcNow;
        Drafts = Drafts.SetItem(meeting, found);
        var count = editCount[meeting] = editCount.GetValueOrDefault(meeting) + 1;
        _ = SaveSoon();

        async Task SaveSoon()
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (count == editCount.GetValueOrDefault(meeting)) await SaveSummary(meeting);
        }
    }

    /// <summary>Ctrl+Z: the summary as it was before the last burst of edits.</summary>
    public void Undo(string meeting) => Step(meeting, undos, redos);

    /// <summary>Ctrl+Y: the summary as it was before the last undo.</summary>
    public void Redo(string meeting) => Step(meeting, redos, undos);

    private void Step(string meeting, Dictionary<string, Stack<Summary>> from, Dictionary<string, Stack<Summary>> to)
    {
        if (SummaryOf(meeting) is not { } now || !Stack(from, meeting).TryPop(out var back)) return;
        lastEdit.Remove(meeting);
        Edit(meeting, _ => back, undoable: false);
        Stack(to, meeting).Push(now);
    }

    private static Stack<Summary> Stack(Dictionary<string, Stack<Summary>> stacks, string meeting) =>
        stacks.TryGetValue(meeting, out var stack) ? stack : stacks[meeting] = new();

    /// <summary>Saves the edits now, and on leaving the meeting (<paramref name="leaving"/>) lets go of points added but
    /// never typed in.</summary>
    public async Task SaveSummary(string meeting, bool leaving = false)
    {
        if (Drafts.GetValueOrDefault(meeting) is not { } draft || SavingSummary.Contains(meeting)) return;
        var cleaned = draft.Cleaned;
        // A point just added and not typed in yet isn't a change: nothing to save, and it stays to be typed in.
        if (cleaned == (Summaries.GetValueOrDefault(meeting) ?? Results.GetValueOrDefault(meeting)?.Summary))
        {
            if (leaving) Drafts = Drafts.Remove(meeting);
            return;
        }
        SavingSummary = SavingSummary.Add(meeting);  // before any wait: one save at a time, so an older one never lands last
        int count;
        try
        {
            if (await Connect("save your changes") is not { } api) return;
            count = editCount.GetValueOrDefault(meeting);
            var generation = this.generation;
            try
            {
                await api.SaveSummary(cleaned, meeting);
                if (generation != this.generation) return;
                Summaries = Summaries.SetItem(meeting, cleaned);
                saves[meeting] = saves.GetValueOrDefault(meeting) + 1;
                if (count == editCount.GetValueOrDefault(meeting) && (draft == cleaned || leaving)) Drafts = Drafts.Remove(meeting);  // nothing typed since: the saved copy is it
                if (Meetings.FirstOrDefault(m => m.MeetingId == meeting)?.Title != cleaned.Title)
                {
                    ChangeMeeting(meeting, m => m with { Title = cleaned.Title });
                    changes++;  // a sync that read the list's old title before this mustn't put it back
                }
            }
            catch (Exception e)
            {
                if (generation == this.generation) Report(e, "save your changes");
            }
        }
        finally
        {
            SavingSummary = SavingSummary.Remove(meeting);  // before saving again, which waits for it
        }
        if (Drafts.ContainsKey(meeting) && count != editCount.GetValueOrDefault(meeting)) await SaveSummary(meeting, leaving);  // typed while it saved
    }

    /// <summary>Back to the agent's summary: the owner's copy goes.</summary>
    public async Task RestoreSummary(string meeting)
    {
        if (await Connect("restore the summary") is not { } api) return;
        try
        {
            await api.RestoreSummary(meeting);
            saves[meeting] = saves.GetValueOrDefault(meeting) + 1;
            editCount[meeting] = editCount.GetValueOrDefault(meeting) + 1;  // a save scheduled before this doesn't bring the copy back
            Drafts = Drafts.Remove(meeting);
            Summaries = Summaries.Remove(meeting);
            if (Results.GetValueOrDefault(meeting)?.Summary.Title is { } title) ChangeMeeting(meeting, m => m with { Title = title });
        }
        catch (Exception e)
        {
            Report(e, "restore the summary");
        }
    }

    /// <summary>A change to a meeting in the list, if it's still there.</summary>
    private void ChangeMeeting(string id, Func<Meeting, Meeting> change)
    {
        if (Meetings.FindIndex(m => m.MeetingId == id) is var i && i >= 0) Meetings = Meetings.SetItem(i, change(Meetings[i]));
    }

    public bool IsPending(string meeting, string speaker) => Pending.Contains($"{meeting}/{speaker}");

    public async Task Save(PersonForm form)
    {
        if (Saving) return;
        if (await Connect() is not { } api)  // the sheet covers the banner: say it there
        {
            NoApi();
            form.Error = Auth.State == AuthState.SignedIn ? OfflineMessage("save this person") : "Sign in to save this person.";
            return;
        }
        Saving = true;
        try
        {
            var generation = this.generation;
            await api.UpdatePerson(form.PersonId, new Dictionary<string, object>
            {
                ["name"] = form.Name, ["role"] = form.Role, ["company"] = form.Company, ["is_owner"] = form.IsOwner,
            });
            changes++;
            if (ReferenceEquals(Editing, form)) Editing = null;  // the sheet this save was for, not one opened since
            await RefreshPeople(api, generation);
        }
        catch (Exception e)
        {
            if (ReferenceEquals(Editing, form))  // the sheet covers the banner: say it there
            {
                if (e is Api.Offline) Offline = true;
                form.Error = e is Api.Offline ? OfflineMessage("save this person") : e.Message;
            }
            else
            {
                Report(e, "save this person");
            }
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>People as the API has them now, unless signed out since <paramref name="generation"/>; if it can't say,
    /// the next sync brings them.</summary>
    private async Task RefreshPeople(Api api, int generation)
    {
        try
        {
            var fresh = await api.People();
            if (generation == this.generation) People = [.. fresh];
        }
        catch (Exception) { }
    }

    public async Task Delete(Person person)
    {
        if (await Connect("delete this person") is not { } api) return;
        try
        {
            await api.DeletePerson(person.PersonId);
            changes++;
            People = People.RemoveAll(p => p.PersonId == person.PersonId);
            if (Editing?.PersonId == person.PersonId) Editing = null;
        }
        catch (Exception e)
        {
            Report(e, "delete this person");
        }
    }

    public async Task SignIn()
    {
        try
        {
            await Auth.SignIn();
            Problem = null;
            await Sync();
        }
        catch (OperationCanceledException)
        {
            // closed the browser: nothing to say
        }
        catch (Exception e)
        {
            Problem = new(e.Message);
        }
    }

    public void SignOut()
    {
        Auth.SignOut();
        ForgetAccount();
    }

    /// <summary>Clears the signed-in person's data. Recordings their account already has go from this PC; ones not yet
    /// sent stay for the next sign-in.</summary>
    private void ForgetAccount()
    {
        foreach (var recording in Recordings.Where(r => r.State is RecordingState.Uploaded or RecordingState.Sent)) Forget(recording.Id);
        foreach (var recording in Recordings.Where(r => r.Error != null && r.State == RecordingState.Saved))
            Store(recording with { Error = null });  // nothing tries again until someone signs in
        generation++;  // a sync on its way drops what it brings
        FolderProblem = null;
        Meetings = [];
        People = [];
        Results = Results.Clear();
        loadedAt.Clear();
        copied.Clear();
        LoadErrors = LoadErrors.Clear();
        Summaries = Summaries.Clear();
        Drafts = Drafts.Clear();
        Files = Files.Clear();
        NotFiled = [];
        try { Directory.Delete(FileCache, recursive: true); } catch (Exception) { }
        if (Selection is { } id && !Recordings.Any(r => r.Id == id)) Selection = null;  // a recording's page stays
        Folders = [];
        Place = Place.Meetings;
        Query = "";
        Player.Stop();
    }

    private int generation;

    /// <summary>The API for something the owner just asked for, or null with a word on why not: offline, or signed out.</summary>
    private async Task<Api?> Connect(string action)
    {
        if (await Connect() is { } api) return api;
        NoApi();
        Problem = Auth.State == AuthState.SignedIn ? new(OfflineMessage(action), Offline: true) : new($"Sign in to {action}.");
        return null;
    }

    /// <summary>Something the owner asked for that didn't happen, said plainly. Offline, it isn't kept for later: they ask again.</summary>
    private void Report(Exception error, string action)
    {
        if (error is Api.Offline) Offline = true;
        Problem = error is Api.Offline ? new(OfflineMessage(action), Offline: true) : new(error.Message);
    }

    private static string OfflineMessage(string action) => $"You're offline, so Tally couldn't {action}. Try again when you're back online.";

    private async Task<Api?> Connect()
    {
        if (await Auth.Token() is not { } token)
        {
            // The session ended: what's left of that account goes, also when it ended before this launch's first sync.
            if (Auth.State == AuthState.SignedOut
                && (Meetings.Count > 0 || People.Count > 0 || Recordings.Any(r => r.State is RecordingState.Uploaded or RecordingState.Sent)))
                ForgetAccount();
            return null;
        }
        return new Api(config.Api, token, config.Version, http);
    }

    // — Sync

    private async Task SyncLoop()
    {
        while (true)
        {
            await Sync();
            var busy = Recordings.Any(r => r.State is RecordingState.Saved or RecordingState.Uploaded)
                       || Meetings.Any(m => m.State is "uploading" or "queued" or "processing" || m.IsRewriting);
            await Task.Delay(TimeSpan.FromSeconds(busy ? 15 : 60));
        }
    }

    public async Task Sync()
    {
        if (syncing)  // claimed before the first await, so two syncs never send one recording twice
        {
            syncAgain = true;  // a sign-in, a stop: worth one more as soon as this one ends
            return;
        }
        syncing = true;
        try
        {
            await SyncOnce();
        }
        finally
        {
            syncing = false;
            if (syncAgain)
            {
                syncAgain = false;
                _ = Sync();
            }
        }
    }

    private async Task SyncOnce()
    {
        var generation = this.generation;
        foreach (var recording in Recordings.Where(r => r.State is RecordingState.Saved or RecordingState.Uploaded))
        {
            if (await Connect() is not { } next)  // a fresh token for each: uploads take time
            {
                NoApi();
                return;
            }
            if (generation != this.generation) return;  // signed out meanwhile
            await Send(recording.Id, next);
        }

        if (await Connect() is not { } api)
        {
            NoApi();
            return;
        }
        var changes = this.changes;
        var raised = Problem;  // an offline problem raised while this sync is on its way is news: it stays
        try
        {
            var fresh = await api.Meetings();
            var freshPeople = await api.People();
            var freshFolders = await api.Folders();
            // Signed out meanwhile, or the owner changed something after these were read: the next sync reads again.
            if (generation != this.generation || changes != this.changes) return;
            Offline = false;
            SyncError = null;
            if (Problem == raised && raised?.Offline == true) Problem = null;
            var ready = fresh.Where(m => m.State == "ready" && Meetings.Any(old => old.MeetingId == m.MeetingId && old.State != "ready")).ToList();
            // A summary rewritten without background voices since the last sync: read it again
            var rewritten = fresh.Where(m => m.Rewriting == null && Meetings.Any(old => old.MeetingId == m.MeetingId && old.Rewriting != null)).ToList();
            Meetings = [.. fresh];
            People = [.. freshPeople];
            if (RenamingFolder == null) Folders = [.. freshFolders];  // not under a name being typed
            foreach (var meeting in ready)
            {
                await Load(meeting.MeetingId);
                notifier.Ready(meeting, ToConfirm(meeting.MeetingId));
            }
            foreach (var meeting in rewritten)
            {
                Summaries = Summaries.Remove(meeting.MeetingId);  // the owner's copy went with the rewrite, if there was one
                await Reload(meeting.MeetingId);
                notifier.Rewritten(meeting);
            }
            // Done with once its meeting isn't on its way: ready, or not listed because it was deleted on another device
            // (its row is made before the upload starts)
            foreach (var recording in Recordings.Where(r => r.State is RecordingState.Sent or RecordingState.Uploaded
                         && !fresh.Any(m => m.MeetingId == r.Id && m.State != "ready")))
            {
                recording.Remove();
                Recordings = Recordings.RemoveAll(r => r.Id == recording.Id);
            }
            if (Selection is { } id) await Load(id);
            // The newest ready meetings' results, once: their counts of people to confirm show in the menu and the list.
            foreach (var meeting in fresh.Take(10).Where(m => m.State == "ready" && !Results.ContainsKey(m.MeetingId))) await Load(meeting.MeetingId);
            _ = KeepMeetingsFolder();  // its first fill can take minutes: never in the way of sending recordings
        }
        catch (Exception e)
        {
            Offline = e is Api.Offline;  // the next sync tries again
            SyncError = Offline ? null : (e as Api.Failure)?.Reason ?? "It tries again shortly.";
        }
    }

    /// <summary>No token: signed out, or a refresh that couldn't reach the provider, which means offline.</summary>
    private void NoApi()
    {
        if (Auth.State == AuthState.SignedIn) Offline = true;
    }

    /// <summary>One recording to the API, in steps that each survive a failure: the audio (which starts the pipeline),
    /// then "uploaded". A step that fails is tried again at the next sync, from where it stopped.</summary>
    private async Task Send(string id, Api api)
    {
        // The recording as it is now, before each step: deleted (or being deleted) meanwhile, it isn't sent.
        if (deletingIds.Contains(id) || Recordings.FirstOrDefault(r => r.Id == id) is not { State: RecordingState.Saved or RecordingState.Uploaded } recording) return;
        var generation = this.generation;
        // Signed out while a step was on its way: that account has the recording now, so it goes from this PC too.
        bool SignedOutMeanwhile()
        {
            if (generation == this.generation) return false;
            Forget(id);
            return true;
        }
        try
        {
            if (recording.State == RecordingState.Saved)
            {
                recording = recording with { State = RecordingState.Uploading, Error = null };  // trying again: the last attempt's error isn't news any more
                Store(recording);
                try
                {
                    await api.Upload(recording, AppVersion);
                }
                catch (Api.AlreadySent)
                {
                    if (SignedOutMeanwhile()) return;
                    Store(recording with { State = RecordingState.Sent, Error = null });
                    return;
                }
                if (SignedOutMeanwhile()) return;
                recording = recording with { State = RecordingState.Uploaded };
                Store(recording);
            }
            // A fresh token: an hour's audio takes longer to go up than one may have left
            await (await Connect() ?? throw new Api.Offline()).Uploaded(recording.Id);
            if (SignedOutMeanwhile()) return;
            recording = recording with { State = RecordingState.Sent, Error = null };
        }
        catch (Exception)
        {
            if (recording.State == RecordingState.Uploading) recording = recording with { State = RecordingState.Saved };
            // Signed in still: it tries again. Signed out meanwhile, nothing does until someone signs in.
            recording = recording with { Error = generation == this.generation ? "Couldn't send it yet. Tally tries again shortly." : null };
        }
        Store(recording);
    }

    private static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    // — Helpers

    private void Notify(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The content type a file's extension says, for its upload: Windows' own registry isn't plain .NET, so the
    /// common ones are here, and anything else is just bytes.</summary>
    private static string MimeType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream",
    };
}

// — The shapes the window works with

/// <summary>A meeting as the window and the menu show it, wherever it is: still on this PC, or with the API.</summary>
/// <param name="HasAudio">A failed one can be sent again.</param>
/// <param name="InCloud">Deleting it deletes it in the cloud too.</param>
public sealed record Item(string Id, string? Title, DateTimeOffset StartedAt, double? Duration, Status Status, bool HasAudio, bool InCloud)
{
    /// <summary>The last send failed: tried again at the next sync.</summary>
    public string? SendError { get; init; }

    /// <summary>The folders it's in; every meeting is in Meetings.</summary>
    public IReadOnlyList<string> FolderIds { get; init; } = [];

    /// <summary>A file on its way would land after the delete, and stay.</summary>
    public bool AddingFiles { get; init; }

    public bool CanDelete => Status.Kind is not (StatusKind.Recording or StatusKind.Uploading) && !AddingFiles;

    /// <summary>Failed, with its audio here, for a reason a resend could fix: not "No speech…" or "This recording is
    /// longer…", which can never be summarised.</summary>
    public bool CanRetry => Status.Kind == StatusKind.Failed && HasAudio && !Final(Status.Error);

    public static bool Final(string? error) =>
        error is { } e && (e.StartsWith("No speech", StringComparison.Ordinal) || e.StartsWith("This recording is longer", StringComparison.Ordinal));
}

public enum StatusKind { Recording, Saving, Waiting, Uploading, Queued, Processing, Ready, Failed }

/// <summary>Where a meeting is; a failed one says why.</summary>
public sealed record Status(StatusKind Kind, string? Error = null)
{
    public static readonly Status Recording = new(StatusKind.Recording);
    public static readonly Status Saving = new(StatusKind.Saving);
    public static readonly Status Waiting = new(StatusKind.Waiting);
    public static readonly Status Uploading = new(StatusKind.Uploading);
    public static readonly Status Queued = new(StatusKind.Queued);
    public static readonly Status Processing = new(StatusKind.Processing);
    public static readonly Status Ready = new(StatusKind.Ready);
    public static Status Failed(string error) => new(StatusKind.Failed, error);
}

public enum PlaceKind { Meetings, Folder, People }

/// <summary>Where the window is, in its sidebar: Meetings, one of the owner's folders, or People.</summary>
public sealed record Place(PlaceKind Kind, string? FolderId = null)
{
    public static readonly Place Meetings = new(PlaceKind.Meetings);
    public static readonly Place People = new(PlaceKind.People);
    public static Place Folder(string id) => new(PlaceKind.Folder, id);
}

/// <summary>Something to tell the owner, and where to fix it when it's a setting.</summary>
/// <param name="Settings">The settings page that fixes it (an ms-settings: URI), for Open Settings.</param>
/// <param name="Offline">It happened for want of a connection: it goes once a sync works.</param>
public sealed record Problem(string Message, string? Settings = null, bool Offline = false);

public enum PeopleSort { Name, Company, Meetings, Voice }

/// <summary>What Tally does when a call starts: nothing, offer to record (the default), or record by itself.</summary>
public enum CallMode { Off, Offer, Auto }

public static class CallModes
{
    public static string Title(this CallMode mode) => mode switch
    {
        CallMode.Off => "Do Nothing",
        CallMode.Offer => "Ask to Record",
        _ => "Record Automatically",
    };

    /// <summary>What the choice does, in a sentence under it.</summary>
    public static string Explanation(this CallMode mode) => mode switch
    {
        CallMode.Off => "You start recordings yourself.",
        CallMode.Offer => "Tally automatically detects a call, and offers to record.",
        _ => "Tally automatically detects and records calls.",
    };
}

/// <summary>A recording the end of its call stopped: the call's app, and when.</summary>
public sealed record CallStop(string? App, DateTimeOffset At);

public enum CallPromptKind
{
    /// <summary>"Record this call with Tally?": Record and Not Now.</summary>
    Offer,
    /// <summary>"Tally is recording this call" (auto mode): Don't Record.</summary>
    Recording,
    /// <summary>"A new call started": Same Meeting and New Meeting.</summary>
    SameMeeting,
}

/// <summary>What the drawer says: which kind, the call's app (null when there's no telling), and the sentence a screen
/// reader announces as it shows.</summary>
public sealed record CallPrompt(CallPromptKind Kind, string? App, string Announcement);

/// <summary>Who a speaker is, as the owner is about to say it.</summary>
public sealed class AssignForm(string meeting, string speaker, string? rejected = null) : ObservableObject
{
    public string Meeting { get; } = meeting;
    public string Speaker { get; } = speaker;

    public string Query { get; set { if (SetProperty(ref field, value)) Highlight = 0; } } = "";
    public string Role { get; set => SetProperty(ref field, value); } = "";
    public string Company { get; set => SetProperty(ref field, value); } = "";

    /// <summary>The row ↑ and ↓ have reached, which Enter picks: the people listed, then Add.</summary>
    public int Highlight { get; set => SetProperty(ref field, value); }

    /// <summary>Once: Enter in a field and the default button can both answer the same press.</summary>
    public bool Sent { get; set => SetProperty(ref field, value); }

    /// <summary>The person the owner just said No to: not offered again.</summary>
    public string? Rejected { get; } = rejected;
}

/// <summary>A person as the owner edits them.</summary>
public sealed class PersonForm(Person person) : ObservableObject
{
    public string PersonId { get; } = person.PersonId;
    public string Name { get; set => SetProperty(ref field, value); } = person.Name;
    public string Role { get; set => SetProperty(ref field, value); } = person.Role ?? "";
    public string Company { get; set => SetProperty(ref field, value); } = person.Company ?? "";
    public bool IsOwner { get; set => SetProperty(ref field, value); } = person.IsOwner;
    public string? Error { get; set => SetProperty(ref field, value); }
}

/// <summary>"Summary ready" notifications, through the platform's; clicking one opens that meeting.</summary>
internal sealed class Notifier(INotifications notifications)
{
    /// <summary>The summary written again without background voices: said, since the owner may have moved on.</summary>
    public void Rewritten(Meeting meeting) => notifications.Post("rewritten-" + meeting.MeetingId, meeting.Title ?? "Your meeting",
        meeting.RewriteError == null ? "The summary is rewritten, without the background voices." : "The summary couldn't be rewritten. Try again from the meeting.",
        meeting.MeetingId);

    public void Ready(Meeting meeting, int toConfirm) => notifications.Post(meeting.MeetingId, meeting.Title ?? "Your meeting is summarised",
        toConfirm == 0 ? "The summary is ready." : $"The summary is ready. {AppModel.ToConfirmText(toConfirm)}.", meeting.MeetingId);

    /// <summary>The call a recording followed has ended, and Tally stopped it; the menu can still continue it.</summary>
    public void CallEnded(string? app) => notifications.Post("call-ended", "Recording stopped",
        (app is null ? "The call ended." : $"The {app} call ended.") + " Tally sends it in 30 seconds; Continue Recording in its menu, if it hasn't.",
        null);
}
