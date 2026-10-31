# Data

Every stored shape.

## S3 data bucket

```
users/<owner>/meetings/<meeting_id>/
  meta.json              written by the API when the app registers the recording
  audio.m4a              uploaded by the app; its upload starts the job
  prepared/              the working directory as prepare left it, without audio16k.wav and people.json (for evals and reruns)
  work/                  the working files the job ended with (below), likewise
  files/<file_id>-<name> a file the owner added (file_id a_<ulid>)
  notes/summary.json     the owner's own copy of the summary, shown instead of the agent's
  results/
    meeting.json         the results, which the app gets through the API
    summary.md           the summary as a page, with names
    clips/<speaker>/<n>.m4a
  run/
    conversation.json    the agent's conversation, however the run ended (not a rewrite's)
    metrics.jsonl        one line per step, with timings and tokens
crashes/<owner>/<ulid>.ips   a crash report sent from an app
```

`<owner>` is the token's `sub`, `meeting_id` a ULID. A job runs only for a meeting registered through the API. No voiceprint is kept in S3. The evals' samples are under an old `meetings/<meeting_id>/` prefix.

`meta.json`:
```json
{"started_at": "2026-10-22T09:00:00Z", "ended_at": "2026-10-22T09:47:12Z", "app_version": "0.1.0"}
```

## S3 site bucket

Written only by `make publish-site`:

```
index.html  admin.html  install.sh  install.ps1  icon.svg  apple-touch-icon.png
version.json                   {"version": "<date>-<commit>-<settings>"}, which installed Mac apps compare with their own
tally-<version>.tar.gz         apps/mac/ at that commit, with its tally.json ([macos.md](macos.md))
windows.json                   the same for the Windows app ([windows.md](windows.md))
tally-windows-<version>.tar.gz apps/windows/ at that commit, with its tally.json
```

The version ends in a hash of the settings. An archive is never changed once written.

## Working directory (`work/`)

| File | Written by | Content |
|---|---|---|
| `manifest.json` | prepare, retranscribe, write-summary, publish | `step` (`prepared` → `summarised` → `published`), `duration`, `layout`, `language`, `language_probabilities`, `models`, `timings` |
| `status.jsonl` | every command | `{ts, command, ok, detail}` per call |
| `people.json` | the job | the owner's people (below); never uploaded |
| `audio16k.wav` | prepare | 16 kHz mono mix; never uploaded |
| `channels.json` | prepare | `layout` (`stereo-split` \| `mono` \| `identical`), `channels`, per-channel `loudness_db`, and `gain_db` applied to the mix |
| `diarization.json` | prepare, merge, split | the turns (below) |
| `transcript.json` | prepare, merge, split, retranscribe | words and lines (below) |
| `speakers.json` | prepare, name, unknown, merge, split, publish | speakers (below) |
| `embeddings.npz` | prepare, merge | one L2-normalised float32 centroid per speaker; voiceprints are saved from these |
| `clips/<speaker>/<n>.m4a` | prepare | up to three clips per speaker, 3–8 s; kept only in `prepared/` |
| `summary.json` | write-summary | the summary (below) |

`diarization.json`: one speaker at a time. `clean` is seconds without overlap; `mic` and `system`, energy per channel (`stereo-split` only).
```json
{"turns": [{"start": 12.3, "end": 15.0, "speaker": "S2", "clean": 2.7, "mic": 41.2, "system": 0.3}]}
```

`transcript.json`: `words`, each with its ASR segment (`seg`) and speaker, and `segments`, the lines.
```json
{
  "language": "en",
  "model": "faster-whisper-large-v3-turbo",
  "words": [{"w": "Thanks,", "start": 12.31, "end": 12.6, "score": 0.93, "seg": 4, "speaker": "S2"}],
  "segments": [{"start": 12.31, "end": 15.02, "speaker": "S2", "text": "Thanks, Alex."}]
}
```

`speakers.json`, in order of first speech:
```json
{
  "speakers": {
    "S2": {
      "talk_time": 412.5, "turns": 38, "clean_speech": 96.0, "first_seen": 12.3, "mic_ratio_db": null,
      "matches": [{"person_id": "p_01H...", "name": "Alex", "score": 0.71, "sources": ["user"]}],
      "identity": {"name": "Alex", "person_id": "p_01H...", "confidence": "high", "by": "agent",
                   "evidence": [{"kind": "voiceprint", "text": "0.71 to Alex"}]}
    }
  }
}
```
- `identity` is `null` until named, or `{"unknown": true, "reason": "...", "by": "agent"}`. It may have `role` and `company`. Its `person_id` is `null` for a new person until publish.
- Evidence kinds: `voiceprint`, `introduction`, `address`, `role`, `elimination`, `other`.
- `mic_ratio_db` is `null` unless the recording is `stereo-split`.
- `matches`: the five people closest in voice, with their best score and voiceprints' sources.

`people.json`:
```json
{"people": [{"person_id": "p_01H...", "name": "Alex", "aliases": [], "role": null, "company": null, "is_owner": false,
             "voiceprints": [{"voiceprint_id": "v_01H...", "source": "user", "model": "pyannote/speaker-diarization-community-1",
                              "meeting_id": "01H...", "embedding": [0.01, ...]}]}]}
```
- The job writes it from the tables for prepare, then with `voiceprints` empty before the agent runs.
- With `sharedVoices`, the job matches colleagues' people in memory after prepare, never in this file, and adds those a speaker matched to it, by name alone (`person_id` `shared:<id>`, `"shared": true`).

`summary.json`: the standard is `pipeline/tally/prompts/summary.md`, the rules `pipeline/tally/summary.py`.
```json
{
  "title": "Q3 pricing",
  "overview": "What the meeting was and its outcomes. {S2} …",
  "summary": ["One sentence a point, most important first."],
  "decisions": [{"text": "Enterprise tier at $45.", "by": ["S1", "S2"]}],
  "action_items": [{"owner": "S3", "text": "Send Acme the renewal.", "due": "2026-10-31"}],
  "open_questions": [{"text": "Whether support needs a hire.", "raised_by": ["S4"]}],
  "topics": [{"title": "Enterprise tier", "start": 60.0, "end": 1320.0, "notes": ["{S2} proposed $45."], "outcome": "Approved."}],
  "attendees": [{"speaker": "S1", "role": "Head of product", "contribution": "Made the call on price."}]
}
```
- Speakers appear only as `{S2}` tokens, never by name.
- `due` is `null` or a YYYY-MM-DD day; `owner` and `outcome` may be `null`. `done` (true, false or null) is only in the owner's copy.
- A topic's `start` and `end` are in seconds. Every named speaker is in `attendees`.
- Limits: `title` 80 characters with no `{S2}`, `overview` 60 words, 1 to 7 `summary` points of at most 35 words, 10 `notes` per topic, 1,500 words in all, 2,000 characters and 20 speaker tokens per text. The owner's copy's title may be 200 characters.

`results/summary.md`: the summary rendered with names at publish and after a rewrite.

`results/meeting.json`. `GET /meetings/{id}` adds each speaker's `clip_urls` and `candidates` ([api.md](api.md)):
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
- Clip paths are relative to the meeting. `language` is `en`, `sv` or null.
- A named identity keeps only `name`, `person_id`, `role`, `company`, `confidence`, `evidence`, `suggestion`, `shared` and `by`. One the owner set has `by: owner`; a background voice is `{"unknown": true, "background": true, "reason": "...", "by": "owner"}`.
- `suggestion`: a shared voice, or a person with only `auto` voiceprints.
- `shared`: `true` when the name came from the shared voices, a colleague's naming; the apps say "Named by a colleague" until the owner settles it.
- `summary_without`: the background voices the summary was last written without; absent until a rewrite.
- A `meeting.json` over 4 MB fails the meeting.

## DynamoDB

Five tables, each keyed by `owner` (the token's `sub`) first.

**`meetings`**: PK `owner`, SK `meeting_id`
- `state`: `uploading` | `queued` (the API) | `processing` | `ready` | `failed` (the job)
- `job_id`: the Batch job that claimed it; every write of the job checks it
- `started_at`, `ended_at`, `title`, `duration`, `language`, `updated_at`
- `error`: why it failed. The apps offer no Send Again for one starting "No speech" or "This recording is longer".
- `people`: the ids of everyone named in it
- `rewriting`, `rewrite_job`, `rewrite_error`: a summary rewrite on its way, its job, and why the last didn't happen
- `edited_at`: the owner's last change, for the meetings folder to fetch it again
- `file_ids`: its files' ids as the API last listed them
- `folder_ids`: a string set of its folders; absent when none
- `agent_runs`: a list of `{kind, at, seconds, turns, input, output, cache_read, cache_write}`, `kind` `summary` or `rewrite`, for the dashboard's model cost; `input` is uncached

**`users`**: PK `owner`
- `name`, `email`, `last_seen`, `app_version`

**`folders`**: PK `owner`, SK `folder_id` (`f_<ulid>`, made by the app)
- `name`; `parent`, absent at the top

**`people`**: PK `owner`, SK `person_id` (`p_<ulid>`)
- `name`, `aliases[]`, `is_owner`, `created_at`
- `role`, `company`: absent when unknown
- `first_meeting_id`: the meeting that brought them in
- `edited`: true once the owner has created or changed them; such a person is never pruned

**`voiceprints`**: PK `owner`, SK `voiceprint_id` (`v_<ulid>`)
- `person_id`
- `embedding`: L2-normalised float32 bytes, and its `dimension`
- `model`: the embedding model; voiceprints compare within one
- `source`: `user` (the owner named the speaker) | `auto` (publish's gate held)
- `meeting_id`, `speaker`, `start`, `end` (auto only), `quality` (clean speech, in seconds), `created_at`

## On the Mac

- **Recordings** waiting to be sent: `~/Library/Application Support/Tally/recordings/<id>/` ([macos.md](macos.md)).
- **Files' copies:** `~/Library/Caches/Tally/files/<meeting_id>/<file_id>/<name>`, removed at Sign Out.
- **The meetings folder,** where the owner chose: `<yyyy-MM-dd HHmm> <title>/` per meeting, with `meeting.json`, `summary.md`, `transcript.md` and `files/`, and `index.json` and `README.md` at the top.
- **The meetings folder's state:** `~/Library/Application Support/Tally/meetings-folder/<sub>/`: `cache/<id>.json`, the meetings as sent, and `state.json`, `{"meetings": {"<meeting_id>": {"stamp": "...", "directory": "...", "rendered": "..."}}}`.
- **Settings** (UserDefaults): `callMode` (`off` | `offer` | `auto`), `welcomed`, `meetingsFolder.<sub>`, `notFiled` (dismissed folder suggestions), `crashesSeenUntil`.
- **Sign-in:** the refresh token, in the Keychain, under the app's bundle id.
- **App settings:** `Tally.app/Contents/Resources/tally.json`.

`local.json`, in both apps, for a recording on the computer:
```json
{"id": "01H...", "startedAt": "2026-10-22T09:00:00Z", "endedAt": "2026-10-22T09:47:12Z", "state": "saved", "auto": true}
```
`state` is `recording`, `saved`, `uploading`, `uploaded`, `sent` or `broken`; `error`, why a send failed; `auto`, started by auto mode.

## On Windows

As on the Mac ([windows.md](windows.md)), under `%LOCALAPPDATA%\Tally\`:
- **Recordings:** `recordings\<id>\` (`local.json`, `recording.wav` while recording, then `audio.m4a`).
- **Settings:** `settings.json`: `callMode` (`null` until chosen), `welcomed`, `meetingsFolders` (`{<sub>: <path>}`), `notFiled`, `crashesSeenUntil`.
- **Files' copies:** `Cache\files\<meeting_id>\<file_id>\<name>`, removed at Sign Out; `Cache\kinds\`, file-type icons.
- **The meetings folder:** the Mac's layout, with names Windows accepts; its state in `meetings-folder\<sub>\`.
- **The installer's work:** `install-<pid>\`, `dotnet.new\` and `%LOCALAPPDATA%\Programs\Tally.old\`, removed when it finishes.
- **Logs:** `Logs\tally.log` (no meeting content; `tally.log.1` past 2 MB) and `Logs\install.log`.
- **Crash reports:** `Crashes\<yyyyMMdd-HHmmss>.txt`, sent as `crashes/<owner>/<ulid>.ips`.
- **The .NET SDK,** if the installer added it: `dotnet\`.
- **Sign-in:** the refresh token, in Credential Manager (`Tally/refresh-token`).
- **The registry,** under `HKCU\Software\`: `Microsoft\Windows\CurrentVersion\Uninstall\Tally`, `…\CurrentVersion\Run` → `Tally` (Open at login, with `…\Explorer\StartupApproved\Run`), `Classes\tally` (the `tally://` scheme), and the Windows App SDK's notification entries, which stay after uninstalling.
- **The app:** `%LOCALAPPDATA%\Programs\Tally\`, with `tally.json` beside it.
