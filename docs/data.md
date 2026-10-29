# Data

Every stored shape lives here. Change a shape when implementation shows something better, and update this file in the same change.

## S3 data bucket (created by CDK)

```
users/<owner>/meetings/<meeting_id>/
  meta.json              written by the API when the app registers the recording (below)
  audio.m4a              uploaded by the app to a presigned URL; its upload starts the job
  prepared/              the working directory as `tally prepare` left it, before the agent, without audio16k.wav and people.json (for evals and reruns)
  work/                  the working files the job ended with, those the table below names (not audio16k.wav or people.json); nothing else the agent wrote, and no clips of its own
  files/
    <file_id>-<name>     a file the owner added: file_id is a_<ulid>, the name has no `/`
  notes/
    summary.json         the owner's own copy of the summary, once they've edited it: shown instead of the agent's
  results/
    meeting.json         the results (below), which the app gets through the API
    summary.md           the summary as a page to read, with names (below)
    clips/<speaker>/<n>.m4a
  run/
    conversation.json    the agent's conversation, saved however the run ended (for evals and debugging); a rewrite's isn't kept
    metrics.jsonl        one line per step: prepare (seconds per stage), agent (seconds, turns, tool calls, tokens), publish
crashes/<owner>/<ulid>.ips   a crash report from someone's Mac or PC (macOS's report, or the Windows app's own), sent from the app
```

No voiceprint is ever in the agent's folder after prepare, so none is in what the job keeps from it: their one place is the tables, where deleting a person deletes them.

`meeting_id` is a ULID made by the app, so ids sort by time. A job runs only for a meeting with a row in the meetings table, that is one registered through the API (`PUT /meetings/{id}`, [api.md](api.md)). An audio file put in the bucket any other way is ignored.

## S3 site bucket (created by CDK)

The install page, behind CloudFront (`SiteBucket`), written only by `make publish-site`:

```
index.html  admin.html  install.sh  install.ps1  icon.svg  apple-touch-icon.png
version.json                   {"version": "<date>-<commit>-<settings>"}: installed Mac apps compare their own with it
tally-<version>.tar.gz         apps/mac/ at that commit, with its tally.json ([macos.md](macos.md)); never changed once written
windows.json                   {"version": …}, the same for the Windows app ([windows.md](windows.md))
tally-windows-<version>.tar.gz apps/windows/ at that commit, with its tally.json; never changed once written
```

The version's last part is a hash of the settings, so a changed setting (a new client, API or site) is a new version.

`meta.json`:
```json
{"started_at": "2026-10-22T09:00:00Z", "ended_at": "2026-10-22T09:47:12Z", "app_version": "0.1.0"}
```
The times are ISO 8601 to the second with their offset, this century (`2026-10-28T09:00:00Z`), as the API checks ([api.md](api.md)).

## Working directory (`work/`)

Every `tally` command runs in it and reads and writes these files.

| File | Written by | Content |
|---|---|---|
| `manifest.json` | prepare, retranscribe, write-summary, publish | `step` (`prepared` → `summarised` → `published`), `duration`, `layout`, `language`, `language_probabilities`, `models`, `timings` |
| `status.jsonl` | every command | one line per call: `{ts, command, ok, detail}` |
| `people.json` | the job, before prepare and again after it; the rewrite job | the meeting owner's people (below): with their voiceprints for prepare to match against, then, before the agent runs, without them; never uploaded |
| `audio16k.wav` | prepare | 16 kHz mono mix (not uploaded) |
| `channels.json` | prepare | `layout`: `stereo-split` \| `mono` \| `identical`, `channels`, per-channel `loudness_db` (as recorded), and `gain_db`, what the level step applied to the mono mix |
| `diarization.json` | prepare, merge, split | the exclusive turns (below) |
| `transcript.json` | prepare, merge, split, retranscribe | words and lines (below) |
| `speakers.json` | prepare, name, unknown, merge, split, publish | speakers (below) |
| `embeddings.npz` | prepare, merge | one centroid per speaker, L2-normalised float32, keyed by speaker id: the meeting's own, kept as its audio is, until the meeting is deleted (a voiceprint is saved from one) |
| `clips/<speaker>/<n>.m4a` | prepare | (kept only in `prepared/`: publish cuts `results/clips/` again from root's copy of the audio by the final turns) up to three clips per speaker, 3–8 s each, from their longest clean turns; for someone with no clean turn of 3 s, their longest turns from 1 s up, overlapped or not |
| `summary.json` | write-summary | summary (below) |

`diarization.json`: one speaker at a time, as pyannote's exclusive diarization gives it. `clean` is the turn's seconds without overlapping speech; `mic` and `system` are the turn's energy on each channel, for `stereo-split` recordings only.
```json
{"turns": [{"start": 12.3, "end": 15.0, "speaker": "S2", "clean": 2.7, "mic": 41.2, "system": 0.3}]}
```

`transcript.json`: `words` are what Whisper said and wav2vec2 aligned, each with its ASR segment (`seg`) and the speaker whose turn it falls in; `segments` are the lines, consecutive words of one speaker within one ASR segment.
```json
{
  "language": "en",
  "model": "faster-whisper-large-v3-turbo",
  "words": [{"w": "Thanks,", "start": 12.31, "end": 12.6, "score": 0.93, "seg": 4, "speaker": "S2"}],
  "segments": [{"start": 12.31, "end": 15.02, "speaker": "S2", "text": "Thanks, Alex."}]
}
```

`speakers.json`, ordered by when each speaker first speaks:
```json
{
  "speakers": {
    "S2": {
      "talk_time": 412.5, "turns": 38, "clean_speech": 96.0, "first_seen": 12.3, "mic_ratio_db": null,
      "matches": [{"person_id": "p_01H...", "name": "Alex", "score": 0.71, "sources": ["user"]}],
      "identity": {"name": "Alex", "person_id": "p_01H...", "confidence": "high", "by": "agent",
                   "evidence": [{"kind": "address", "text": "00:12:31 'Thanks Alex', S2 replies"},
                                {"kind": "voiceprint", "text": "0.71 to Alex"}]}
    }
  }
}
```
- `identity` is `null` until named, or `{"unknown": true, "reason": "...", "by": "agent"}`. The agent may add `role` and `company`, as the meeting says them.
- In `results/meeting.json`, an identity keeps only these fields:
  - An unknown speaker the agent marked has only those three, with its reason as the agent wrote it. It has no `person_id`, since the people a meeting's row names are held on to in People.
  - A named one has only `name`, `person_id`, `role`, `company`, `confidence`, `evidence`, `suggestion` and `by: agent`. Being set aside, or unknown, is the owner's word.
  - `confidence` and `evidence` are as the agent wrote them. For an `auto` voiceprint, only evidence of the kinds below counts.
  - An identity the owner set in the app has `by: owner`. One they marked a background voice, not in the meeting, is `{"unknown": true, "background": true, "reason": "...", "by": "owner"}`.
- `person_id` is `null` for a new person until publish creates them; publish also adds `suggestion` (below).
- Evidence kinds: `voiceprint`, `introduction`, `address`, `role`, `elimination`, `other`.
- `mic_ratio_db` is `null` unless the recording is `stereo-split`.
- `matches`: the three people whose voiceprints come closest, each with their best cosine similarity and the sources of their voiceprints; with `sharedVoices`, someone from the pool only at 0.5 or more. They're prepare's: a merge keeps each person's best of the two clusters', and a split's new speaker has none.

`people.json`:
```json
{"people": [{"person_id": "p_01H...", "name": "Alex", "aliases": [], "role": null, "company": null, "is_owner": false,
             "voiceprints": [{"voiceprint_id": "v_01H...", "source": "user", "model": "pyannote/speaker-diarization-community-1",
                              "meeting_id": "01H...", "embedding": [0.01, ...]}]}]}
```
With `sharedVoices` on, prepare matches against everyone else's named people with voiceprints too:
- There's one entry per name, and none for a name the owner already knows someone by.
- `person_id` is `shared:<their id>`, with `"shared": true`, and their voiceprints' `meeting_id` is `null`.
- Before the agent runs, only those among a speaker's three closest matches (each at 0.5 or more) stay, with their name alone: `voiceprints` empty, `role` and `company` `null`.
- Publishing turns a speaker named as one into the owner's own person, by name, as a suggestion.

It's the job's own copy, from the tables, and isn't uploaded. Once prepare has matched the voices, the job writes it again with `voiceprints` empty for everyone. The matches prepare found stay in `speakers.json`, kept through `tally merge` (each person's best) and `split`. Whatever the agent can read, it could copy into what's kept, and the tables are where a person's voiceprints live and where deleting them deletes them.

`summary.json`: the one summary the owner comes back to, in layers: the 30-second read (`title`, `overview`, `summary`), what to act on (`decisions`, `action_items`, `open_questions`), the detail (`topics`) and who was there (`attendees`). The standard is `pipeline/tally/prompts/summary.md`; the rules `tally write-summary` enforces are in `pipeline/tally/summary.py`.
```json
{
  "title": "Q3 pricing: enterprise tier and the Acme renewal",
  "overview": "Two or three sentences, at most 60 words: what the meeting was and its headline outcomes. {S2} …",
  "summary": ["1 to 7 points, one sentence each, most important first."],
  "decisions": [{"text": "Enterprise tier at $45 per seat, from 1 January.", "by": ["S1", "S2"]}],
  "action_items": [{"owner": "S3", "text": "Send Acme the renewal at $120,000.", "due": "2026-10-31"}],
  "open_questions": [{"text": "Whether support can carry the SLA without a hire.", "raised_by": ["S4"]}],
  "topics": [{"title": "Enterprise tier", "start": 60.0, "end": 1320.0, "notes": ["{S2} proposed $45 against $39 last quarter."],
              "outcome": "Approved at $45 from 1 January."}],
  "attendees": [{"speaker": "S1", "role": "Head of product, chairs", "contribution": "Made the call on the enterprise price."}]
}
```
- Speakers appear only as `{S2}` tokens, never by name, so a rename never needs reprocessing.
- `due` is `null` unless a date was said, and then a YYYY-MM-DD day this century: `tally write-summary` and the API refuse anything else. `owner` is `null` when no speaker took the item on.
- `outcome` is `null` for a topic that was only informational.
- `done`, on an action item: absent in the agent's summary; true or false once the owner ticks it, in their copy (`notes/summary.json`). The API refuses anything but true, false or null there.
- A topic's `start` and `end` are seconds into the meeting.
- A summary text is at most 2,000 characters, naming speakers at most 20 times. `tally write-summary` and the API both hold to that. The owner's copy's title is kept to 200 characters.
- A `meeting.json` that would be over 4 MB isn't published. The meeting fails, and the people it brought in go when it's deleted.
- The summary, the transcript's lines and the speakers' stats are published as the agent's files have them, since what the agent got wrong there breaks only its own meeting.
- `language`, which the meeting's row takes, is `en`, `sv` or null.
- Every named speaker is in `attendees`; an unnamed one is there if they took real part.
- Limits: `title` 80 characters, naming no speaker (no `{S2}`: the meeting list, notifications and folder names show it as it is), `overview` 60 words, 1 to 7 `summary` points of at most 35 words, 10 `notes` per topic, 1,500 words in all.

`results/summary.md`: the summary as a page to read, rendered at publish with the speakers' names (unnamed ones are "Speaker 3"): the title, a line with the date, length and attendees, then Overview, Summary, Decisions, Action items, Open questions, Topics and Attendees. The app renders `summary` from `meeting.json` itself, so a rename shows at once; this file is as of publishing, and written again when the summary is rewritten.

`results/meeting.json`, the meeting's results. The app gets it through `GET /meetings/{id}`, which adds each speaker's `clip_urls` and `candidates` ([api.md](api.md)):
```json
{
  "meeting_id": "01H...", "meta": {}, "language": "en", "duration": 2813.7, "layout": "identical",
  "summary": {"title": "…", "overview": "…", "summary": [], "decisions": [], "action_items": [], "open_questions": [], "topics": [], "attendees": []},
  "speakers": {"S2": {"talk_time": 412.5, "turns": 38, "mic_ratio_db": null, "clips": ["results/clips/S2/1.m4a"],
                      "identity": {"name": "Alex", "person_id": "p_01H...", "confidence": "high", "by": "agent",
                                   "evidence": [], "suggestion": false}}},
  "segments": [{"start": 12.31, "end": 15.02, "speaker": "S2", "text": "Thanks, Alex."}]
}
```
- Clip paths are relative to the meeting's prefix.
- `summary_without`: the background voices the summary was last written without, set by the rewrite job; absent until a rewrite.
- `suggestion` is true when the speaker was matched to a known person who has only `auto` voiceprints. The app doesn't use it: it works out who needs confirming itself, from the person's voiceprints as they are now.

`<owner>` is the user id (`sub`, letters, digits, `-` and `_`) of whoever recorded the meeting, from their token. The evals' samples are under the old `meetings/<meeting_id>/` prefix, which nothing else uses.

## DynamoDB

Five tables, each keyed by `owner` (the token's `sub`) first: one owner's rows are read with one query, and nothing of one owner's reaches another's.

**`meetings`**: PK `owner`, SK `meeting_id`
- `state`: `uploading` | `queued` (written by the API) | `processing` | `ready` | `failed` (written by the job)
- `job_id`: the Batch job that claimed it; every write of the job's checks it
- `started_at`, `ended_at` (from the app), `title` (the summary's once ready, or the owner's copy's), `duration`, `language`, `error` (why it failed, shown as it is: the apps offer no Send Again for one starting "No speech" or "This recording is longer", the job's `NO_SPEECH` and `TOO_LONG`, so keep those first words), `updated_at`
- `people`: the person ids of everyone named in it, written by the job and whenever the owner names a speaker: a person's meetings, in the app
- `rewriting`: when a rewrite of the summary without background voices was asked for, until the job's done; `rewrite_job`, the job that has it; `rewrite_error`, why the last one didn't happen
- `edited_at`: the owner's last change to it (its summary, a speaker named, its folders, its files, also when a listing of its files finds others than it last saw), through the API or by a summary rewrite they asked for, when it finishes: the app's meetings folder fetches it again when it, or `updated_at`, moves
- `file_ids`: a list of its files' ids, oldest first, as the API last listed them; a listing that finds others moves `edited_at`, always to a later second. Absent until a listing finds one.
- `folder_ids`: a string set of the owner's folders it's in, any number; absent when none
- `agent_runs`: every run of the agent on it, for the admin dashboard's model use and cost. Absent on meetings summarised before 2026-10-28.
  - a list of `{kind, at, seconds, turns, input, output, cache_read, cache_write}`, with `kind` `summary` or `rewrite`
  - the four token counts are summed over the run's model calls as Strands counts them, and `input` is the uncached input
  - the job sets it to its run when the meeting turns ready; a rewrite adds its own (`list_append`), published or not

**`users`**: PK `owner`: who uses Tally, for the admin dashboard
- `name`, `email`: as their token has them; `last_seen`: their last sync; `app_version`: the version their app said it is

**`folders`**: PK `owner`, SK `folder_id` (`f_<ulid>`, made by the app)
- `name`; `parent`, the folder it's in, absent at the top

**`people`**: PK `owner`, SK `person_id` (`p_<ulid>`)
- `name`, `aliases[]`, `is_owner`, `created_at`
- `role`, `company`: absent when unknown. The agent fills them in from what's said (`tally name --role --company`), only where they're absent.
- `first_meeting_id`: the meeting that brought them in. Deleting that meeting deletes them too, if nothing else holds on to them ([api.md](api.md)).
- `edited`: true once the owner has created or changed them in the app. Such a person is never pruned.

**`voiceprints`**: PK `owner`, SK `voiceprint_id` (`v_<ulid>`); `person_id` is an attribute
- `embedding`: float32 bytes, L2-normalised, and its `dimension`
- `model`: the embedding model, because voiceprints are only comparable within one model
- `source`: `user` (the owner confirmed or named the speaker) | `auto` (publish's gate held)
- `meeting_id`, `speaker` (the cluster in that meeting), `start` (the speaker's first second), `end` (their last, auto only), `quality` (their clean speech, in seconds), `created_at`

A voiceprint is one person's centroid over one meeting. Every meeting a person is identified in can add one, so their set grows meeting by meeting, and matching takes their best one.

Publish first drops any voiceprints an earlier try of the same meeting saved (a Batch retry). Then it saves an `auto` one only when:
- the agent's identification is `high` confidence
- it rests on at least two kinds of evidence
- the speaker has at least 30 s of clean speech

Matching loads the owner's people and voiceprints into memory (a query each).

When the owner names a speaker in the app, the API saves a `user` voiceprint from the speaker's centroid in that meeting's `embeddings.npz`, when there's a sound one ([api.md](api.md)), and rewrites the speaker's identity in `results/meeting.json` (`by: owner`), so the change shows without reprocessing. `work/speakers.json` keeps the agent's version ([api.md](api.md)).

## On the Mac

- **Recordings** waiting to be sent: `~/Library/Application Support/Tally/recordings/<id>/` ([macos.md](macos.md)).
- **Files' copies:** `~/Library/Caches/Tally/files/<meeting_id>/<file_id>/<name>`, downloaded to show them; a cache, removed at Sign Out.
- **The meetings folder,** wherever the owner chose:
  - a folder per meeting, `<yyyy-MM-dd HHmm> <title>/`, with `meeting.json`, `summary.md`, `transcript.md` and `files/`
  - in `files/`, of two files with one name (in any case), the later one is `<file_id>-<name>`; each file's modified time is when it was added, so one replaced by another of its name is fetched again
  - `index.json`, and `README.md`, which documents them for agents
- **What the Mac wrote to the meetings folder,** and the meetings as the API sent them (to render again after a rename in People), are in `~/Library/Application Support/Tally/meetings-folder/<sub>/` (`state.json`, `cache/<id>.json`).
  `state.json` is `{"meetings": {"<meeting_id>": {"stamp": "...", "directory": "...", "rendered": "..."}}}`: for each meeting, the `updated_at` and `edited_at` it was fetched at, the folder it's in, and a hash of what it was rendered with.
- **Settings** (UserDefaults, the app's bundle id):
  - `callMode`: `off` | `offer` | `auto`, When a call starts
  - `welcomed`: true once the window has opened by itself the first time
  - `meetingsFolder.<sub>`: the meetings folder's path, the signed-in account's own
  - `notFiled`: meeting ids whose suggested folder the owner dismissed; cleared at Sign Out
  - `crashesSeenUntil`: when the newest crash report handled was written
- **Sign-in:** the refresh token, in the Keychain, under the app's bundle id.
- **The app's settings:** `Tally.app/Contents/Resources/tally.json` ([macos.md](macos.md)).

`local.json`, in both apps (a recording on the computer, from its start until its summary is ready or its meeting is deleted):
```json
{"id": "01H...", "startedAt": "2026-10-22T09:00:00Z", "endedAt": "2026-10-22T09:47:12Z", "state": "saved", "auto": true}
```
`state` is `recording`, `saved` (waiting to be sent), `uploading`, `uploaded` (the audio is up; the API hasn't heard), `sent`, or `broken` (the encode failed); `error` says why a send failed; `auto` is there for one auto mode started.

## On Windows

As on the Mac, in the Windows places ([windows.md](windows.md)), all under `%LOCALAPPDATA%\Tally\`:
- **Recordings** waiting to be sent: `recordings\<id>\` (`local.json`, `recording.wav` while recording, then `audio.m4a`).
- **Settings:** `settings.json`, what the Mac keeps in UserDefaults: `callMode` (`off` | `offer` | `auto`, `null` until chosen), `welcomed`, `meetingsFolders` (`{<sub>: <path>}`), `notFiled`, `crashesSeenUntil`.
- **Files' copies:** `Cache\files\<meeting_id>\<file_id>\<name>` (the name made one Windows takes, or the file id when nothing's left of it), downloaded to show them and removed at Sign Out, and `Cache\kinds\`, the icons the shell gives each kind of file.
- **The meetings folder:** the same layout as the Mac's, with files under names Windows takes (`/ : \ < > " | ? *` and control characters made `-`, trailing dots and spaces dropped, a device name such as `CON` given a `_` in front); what the PC wrote is in `meetings-folder\<sub>\` (`state.json`, `cache\<id>.json`).
- **The installer's work:** `install-<pid>\`, while the SDK installs `dotnet.new\`, and while it swaps versions the old one as `%LOCALAPPDATA%\Programs\Tally.old\`; removed when it finishes; one left by an install cut short goes at the next install (`dotnet.new\` at the next SDK install).
- **Logs:** `Logs\tally.log` (steps, calls, the meetings folder; no meeting content; moved to `tally.log.1` past 2 MB) and `Logs\install.log`.
- **Crash reports:** `Crashes\<yyyyMMdd-HHmmss>.txt`, written by Tally itself, sent as `crashes/<owner>/<ulid>.ips` all the same.
- **The .NET SDK,** when the installer put it there: `dotnet\`.
- **Sign-in:** the refresh token, in Credential Manager, as the generic credential `Tally/refresh-token`.
- **The registry,** under `HKCU\Software\`, all removed by uninstalling except the last:
  - `Microsoft\Windows\CurrentVersion\Uninstall\Tally`: the installer's entry in Settings → Apps, with name, version, icon, location, and an `UninstallString` that runs `uninstall.ps1` through `iex`
  - `…\CurrentVersion\Run` → `Tally` (`"<Tally.exe>" --background`, Open at login), with its switch in `…\Explorer\StartupApproved\Run`
  - `Classes\tally`, the `tally://` scheme, written at every launch
  - what the Windows App SDK registers for notifications, under `Classes\AppUserModelId\` and `Classes\CLSID\{…}\LocalServer32`, which stays
- **The app** in `%LOCALAPPDATA%\Programs\Tally\`, with its settings beside it in `tally.json`.
