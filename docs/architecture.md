# Architecture

Tally works like this: a macOS menu bar app or a Windows notification-area app records a meeting, and AWS turns the recording into three things:
- a transcript with real speaker names
- a summary, in layers: a quick read, what to act on, the detail
- action items

The app shows the result, and the owner confirms who spoke. Confirmed voices are recognised in every later meeting. Everyone signs in with their own account, and everything is theirs alone.

The choices that shape it, and why, are in [Why it's built this way](#why-its-built-this-way).

## Architecture

```
Mac or Windows app ──record──► audio.m4a (ch1 mic, ch2 system audio), kept on the computer until its summary is ready
   │
   ├──sign in (OIDC, PKCE)──► config's auth provider, or the stack's Cognito user pool
   │
   └──HTTP API (JWT authorizer) ──► Lambda (api/tally_api.py) ──► meetings, people, voiceprints, folders, users tables
          │  PUT /meetings/{id} → presigned PUT                       (all keyed by owner = the token's sub)
          ▼
   S3 users/<sub>/meetings/<id>/audio.m4a ──EventBridge──► AWS Batch (GPU, on demand, warm as config says)
                                                             one container, the job claims the meeting:
                                                               1. tally prepare       (deterministic code)
                                                               2. agent + tally CLI   (judgement)
                                                               3. tally check → publish
   The app polls GET /meetings, and reads a ready one with GET /meetings/{id}: results, clip links, voices that sound alike.

Browser ──► CloudFront (config's domain) ──► S3 site bucket: the install page, install.sh and install.ps1, the apps' source, admin.html
```

Everything runs in config's one region (eu-north-1 in the example config), except the site's certificate, which CloudFront needs in us-east-1 (the `<stackName>Certificate` stack). The contract between the app and the cloud is [api.md](api.md).

## Recording

The apps are described in [macos.md](macos.md) and [windows.md](windows.md), and compared in [parity.md](parity.md).
- **Capture:** all system audio except Tally's own, alongside the default mic. On the Mac, one global Core Audio tap in a private aggregate device on one clock; on Windows, WASAPI process loopback excluding Tally, with the mic, written on one wall clock.
- **The file:** one 2-channel 48 kHz `.m4a`, with the mic on channel 1 and system audio on channel 2.
- **In-person meetings** are recorded the same way. Their system channel is simply silent.
- **There is no "mic = me".** The owner's voice is learned like everyone else's. The mic/system energy ratio per speaker is only a clue: someone loud on the mic and quiet in system audio is in the room.

The pipeline accepts 2-channel files (mic and system), 1-channel files, and 2-channel files whose channels are identical (treated as one microphone in the room), as `.m4a`, `.mp3` or `.wav`.

## Pipeline

### 1. `tally prepare`

Deterministic code that always runs, before the agent starts:

1. **Decode** to 16 kHz mono for ASR and diarization, and keep the channels separate for stats. The mono mix is brought to a normal level (its loud speech at -6 dBFS, at most +30 dB), because a Mac's built-in mic records quiet.
2. **Diarize** with pyannote.audio 4, `pyannote/speaker-diarization-community-1`, on the mono mix. The exclusive diarization assigns words. `speaker_embeddings` gives one centroid per speaker.
3. **Language ID:** Whisper's own detection over several VAD speech segments, comparing P(en) with P(sv) only. English is the primary language, with the occasional Swedish meeting.
4. **Transcribe and align** with WhisperX:
   - English: `large-v3-turbo`.
   - Swedish: `KBLab/kb-whisper-large`, revision `strict`, with a CTranslate2 config written at image build.
   - Word alignment uses wav2vec2 (WhisperX picks the model per language).
5. **Speaker stats:** talk time, number of turns, mic/system energy ratio, and the best voiceprint matches against the owner's people.
6. **Clips:** a few short, clean clips per speaker, so the owner can hear a speaker before naming them.

Outputs go to the meeting's working directory. The shapes are in [data.md](data.md).

### 2. The agent

Strands Agents in Python, with config's `modelId` on Bedrock (`eu.anthropic.claude-sonnet-5-5` in the example config). It has four tools: `bash`, `read_file`, `write_file`, `edit_file`. It works by running the `tally` CLI, and its shell has no AWS credentials and no network. The full design is in [agent.md](agent.md).

The run has two parts:
1. **Identify the speakers.** The agent uses:
   - voiceprint matches
   - self-introductions ("this is Sam from finance")
   - direct address followed by a reply ("thanks, Priya")
   - meeting structure: the chair opens, a presenter talks most
   - elimination

   It records role and company when they're said. It merges or splits clusters when diarization got it wrong, and says "unknown" rather than guess.
2. **Analyse the whole transcript, with everyone named, and write the summary.** It's the one page the owner comes back to after the meeting, in layers:
   - the 30-second read: a title, an overview, and the few points that matter most
   - what to act on: decisions and who made them, action items with owners and due dates when said, and what is still open
   - the detail: each topic of the meeting with its notes and outcome
   - who was there: each attendee's role and part in the meeting

   No filler, and no chronological retelling. The standard is `pipeline/tally/prompts/summary.md`, and the evals grade it. Publish also renders it as a page, `results/summary.md`.

The summary refers to speakers by id (`{S2}`), never by a name in any form, and the app fills in the current names, with roles and companies from People. Renaming or correcting someone later reaches every summary without reprocessing.

### 3. The harness and the job

**The harness decides when the run is done, not the model.** After each agent turn it runs `tally check`, with code loaded before the agent started, which nothing the agent edits can reach.
- Done means every speaker is named or marked unknown and the summary validates.
- If not, it prompts the agent again. Three turns in a row without progress fail the run.

**The job holds a claim on the meeting:** a meeting deleted in the app, or sent again, stops it without anything changing.

## People and voiceprints

- **A person** belongs to one owner. They have a name, aliases (old names they had), a role, a company, and an `is_owner` flag ("This is me" in the app).
- **A voiceprint** is one person's voice averaged over one meeting. It records its `source`: `user` (the owner confirmed it) or `auto` (the pipeline was confident). Every meeting a person is identified in can add one.
- **Matching** compares each speaker's centroid with every one of the owner's voiceprints by cosine similarity, in memory, and scores each person by their best voiceprint.
- **Confirming or naming a speaker in the app** saves a `user` voiceprint from the speaker's centroid in that meeting (none when the speaker has no sound centroid, such as a split's new speaker; [api.md](api.md)).
- **The pipeline auto-saves a voiceprint** only when all of these hold:
  - confidence is `high`
  - there are at least two independent kinds of evidence
  - there are at least 30 s of clean speech
- **The app asks "Is this Alex?"** for the agent's call until the owner confirms, unless the person's voice is already confirmed. Its picker leads with **Sounds like**: the owner's people closest in voice.

## Cloud

- **S3:** one data bucket, KMS-encrypted, no public access, TLS only. Meetings live under `users/<sub>/meetings/<id>/`.
- **DynamoDB:** five tables, `meetings`, `people`, `voiceprints`, `folders` and `users`, each keyed by `owner` first. A meeting's state is its row in `meetings`.
- **The API:** an HTTP API whose JWT authorizer accepts the provider's tokens for the app's client, with one Python Lambda. Who may sign in is the provider's business; the admin role is read from config's `auth.adminClaim`.
- **Sign-in:** config's `auth`, any OIDC provider; without it, a Cognito user pool with an `admin` group, its people added by an admin.
- **The site:** a private S3 bucket that only CloudFront reads, at config's `domain` with an ACM certificate and Route 53 records, or at CloudFront's own name.
- **AWS Batch** across two AZs:
  - one on-demand environment, because spot had no GPU capacity in eu-north-1 ([why](#why-its-built-this-way)). Config's `gpu.warm` sets how many GPUs stay warm: none, always one, or one in set hours. The maintainer's deployment keeps one from 07:00 to 21:00.
  - single-GPU types, because GPUs are scarce. The example config lists seven L4 and L40S types in `gpu.instanceTypes`.
  - the image is x86 with the models baked in
- **Network:** public subnets (one per AZ), a security group with no ingress, IMDSv2, and a firewall rule giving the agent's shell no network at all.
- **Trigger:** S3 "Object Created" on `users/*/meetings/*/audio.*` goes through EventBridge to a Batch job.
- **Measured:** a one-hour meeting takes about 11 minutes of instance time, 7 of them a cold start when no instance is running. On a warm instance a job starts within seconds, and the agent takes about a minute ([research.md](research.md)).

## Why it's built this way

The choices that shape Tally, so they aren't undone by mistake.

- **Everything belongs to the signed-in person.** S3 keys live under `users/<sub>/` and every table is keyed by `owner` first; the API takes the owner from the token, and the job from the upload's key, so another person's data is simply not found.
- **Checks guard what reaches beyond one meeting, and nothing less.** An owner can break their own meeting (by a hand-made request, or by what's said in it steering its agent) and can delete it, so that isn't guarded; anything that reaches another person, the owner's meeting list, People, search or the agent's sandbox is.
- **GPU work runs on AWS Batch, on demand, not spot.** In eu-north-1 GPU spot had a placement score of 1 out of 10 and every launch failed, and Batch won't move a job on from a spot environment that has no capacity, so jobs would wait forever. It scales to zero; `gpu.warm` keeps one GPU on, always or in set hours, to skip the 6–7 minute cold start.
- **Code does what it can, the agent only what needs judgement.** `tally prepare` runs before the agent; the meeting agent is a Strands agent that works through the `tally` CLI, and the harness, not the model, decides when it's done.
- **The agent's shell has no network and no AWS credentials.** The job's role reaches every owner's meetings and anyone in a meeting can say anything to the agent, so transcript text is data, never instructions, and root trusts nothing in the agent's folder.
- **Voiceprints never reach the agent.** Prepare, as root, matches the voices before the agent runs, and the agent's folder holds names and scores only. A voiceprint's one place is the tables, and deleting a person deletes theirs.
- **The owner has the last word on who someone is.** The agent suggests, the owner confirms, and only a voice the owner confirmed is trusted from then on; the agent fills in a role or a company only where it's empty.
- **Shared voices are off by default.** Voiceprints are biometric data, and `sharedVoices` shares the voices of people who never used Tally, so a deployment turns it on knowingly (the README says what it means).
- **The apps are built on the person's own computer, with no developer account.** macOS blocks a downloaded app without a paid Developer ID, but not one built locally, which is signed with a local certificate so macOS keeps its permissions; the Windows app installs the same way. The cost: Windows' Smart App Control and organisations' app control block an app built on the PC, until it's signed.
- **The Windows app mirrors the Mac's.** Each is native to its system, and `Tally.Core` is a port of the Mac's model with the same names, so the two can be compared file by file.
- **Nothing deployment-specific is in the repository.** Anyone can deploy their own Tally: settings live in the local `config.json`, and the code reads them from config or the stack's outputs.
- **The licence is PolyForm Shield 1.0.0.** Anyone may use, change and share Tally, at work too, but not to offer a product that competes with it.

## Next

1. **The voiceprint experiment,** to run once there's real data: roughly 20 or more confirmed people with 5 or more meetings each. Today every meeting adds one voiceprint per person, and a person scores by their best one. That may stop working as people and voiceprints grow:
   - each new voiceprint differs a little (a room or a call, a headset or the laptop, a cold), so a person's prints spread out over time
   - with more people in the space, someone's spread-out prints can come close to someone else's voice
   - the best of many prints gives an impostor more chances to come close: the false matches grow with the number of prints, not just of people

   **Questions:**
   - **How to represent a person:** every print scored by its best (today), one normalised mean, a mean weighted by clean speech, a few centroids per person (one per condition, such as in the room or on a call), or every print with outliers dropped. Which separates people best, and is it the same at 5 people and at 100?
   - **How many prints per person:** where adding more stops helping, and which to keep (the newest, the cleanest, the most typical).
   - **Scores:** whether a fixed cosine threshold (0.65, [research.md](research.md)) holds as the space fills, or whether it needs per-person thresholds or cohort score normalisation (AS-norm, standard in speaker verification). Calibrating scores so high, medium and low confidence mean measured error rates.
   - **The gate for `auto` voiceprints** (high confidence, two kinds of evidence, 30 s of clean speech): whether it keeps bad prints out, and what one bad print does to a person's matches.
   - **The embedding:** whether 256 numbers from community-1 are enough, against another speaker model, if the data says so.

   **Method:** confirmed (`user`) voiceprints are the ground truth. Leave one meeting out: match its speakers against stores built from the other meetings, for each representation, at people counts subsampled from small to all. Measure:
   - top-1 accuracy, by talk time
   - false-accept and false-reject rates against the threshold, and the equal error rate
   - the margin between the best and second-best person

   Split everything by condition (in the room, on a call), and by how old the prints are. `evals/voiceprints.py` already compares best against mean on the samples, so it's the place to start.

   **Out of it:** the representation, a cap per person, the thresholds, and what certain and uncertain mean. Until then, flag a voiceprint that disagrees with the person's others.
2. **Backlog, for notes:**
   - **To Do:** one list of every action item across meetings, open ones first (overdue, then by due date), each with its box, owner and due date, and its meeting a click away; Mine or Everyone. Later, not now.
   - **Screenshots while recording,** placed at their moment in the transcript.
3. **Background speakers** (open, 2026-10-27): in an open office, the microphone also picks up people talking nearby, and they're transcribed as speakers. This was measured on one colleague's five meetings. The people on a call came through at about −23 to −26 dB, and the colleague on their own mic at −34 to −40 dB. A background voice came through at −40 dB, only 6 dB under the colleague in that meeting. So a level threshold alone isn't safe, as it would drop the person recording in some meetings, and someone across the table in a room meeting. Considered and not taken:
   - Apple's Voice Isolation while following a call (it'd also cut the room in a hybrid meeting)
   - the agent marking quiet, unanswered, off-topic voices as background (the maintainer decided against it)

   So far the owner can mark a speaker as a background voice. A way that's safe for room meetings too, without the owner marking it, is still to find.
4. **Questions about a meeting:** paused, and taken out for now: it added many moving parts, and the app already shows the whole transcript beside the summary.
5. **Later:**
   - calendar attendees (EventKit)
   - a global start/stop hotkey
   - live captions
