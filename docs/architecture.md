# Architecture

Tally turns a meeting recording into a transcript with real speaker names, a summary and action items. A Mac or Windows app records, and AWS does the rest. The owner confirms who spoke, and confirmed voices are recognised in later meetings. Everything belongs to the signed-in person.

## Overview

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

Everything runs in config's one region, except the site's certificate in us-east-1. The contract between the apps and the cloud is [api.md](api.md), and every stored shape is in [data.md](data.md).

## Recording

The apps are described in [macos.md](macos.md) and [windows.md](windows.md).
- **Capture:** all system audio except Tally's own, alongside the default mic. The Mac uses a Core Audio tap in a private aggregate device; Windows uses WASAPI process loopback. Both write on one clock.
- **The file:** one 2-channel 48 kHz `.m4a`, with the mic on channel 1 and system audio on channel 2. An in-person meeting's system channel is simply silent.
- **There is no "mic = me".** The owner's voice is learned like anyone's. A speaker's mic/system energy ratio is only a clue to who is in the room.

The pipeline also accepts 1-channel files, and 2-channel files whose channels are identical (one microphone in the room), as `.m4a`, `.mp3` or `.wav`.

## Pipeline

### 1. `tally prepare`

Deterministic code that runs before the agent:
1. **Decode** to 16 kHz mono, keeping the channels for stats, and bring the mix to a normal level (a Mac's built-in mic records quiet).
2. **Diarize** with pyannote.audio 4 (`pyannote/speaker-diarization-community-1`). Its exclusive diarization assigns words, and its embeddings give one centroid per speaker.
3. **Language ID:** Whisper's own detection over several speech segments, comparing English with Swedish only.
4. **Transcribe and align** with WhisperX: `large-v3-turbo` for English, `KBLab/kb-whisper-large` for Swedish, and wav2vec2 for word times.
5. **Speaker stats:** talk time, turns, the mic/system ratio, and the best voiceprint matches among the owner's people.
6. **Clips:** a few short, clean clips per speaker, so the owner can hear someone before naming them.

### 2. The agent

A Strands agent with config's `modelId` on Bedrock. It has four tools (`bash`, `read_file`, `write_file`, `edit_file`) and works by running the `tally` CLI. Its shell has no AWS credentials and no network. The design is in [agent.md](agent.md).

The run has two parts:
1. **Identify the speakers** from voiceprint matches, introductions, direct address followed by a reply ("thanks, Priya"), the meeting's structure and elimination. It merges or splits clusters diarization got wrong, and says "unknown" rather than guess.
2. **Write the summary,** with everyone named, in layers: a 30-second read, what to act on, the detail of each topic, and who was there. The standard is `pipeline/tally/prompts/summary.md`.

The summary refers to speakers by id (`{S2}`), never by name, and the app fills in the current names. Renaming someone later reaches every summary without reprocessing.

### 3. The harness and the job

**The harness decides when the run is done, not the model.** After each agent turn it runs `tally check`, with code loaded before the agent started. Done means every speaker is named or marked unknown and the summary validates. If not, it prompts the agent again; three turns in a row without progress fail the run.

**The job holds a claim on the meeting.** A meeting deleted in the app, or sent again, stops it without anything changing. The job publishes, not the agent.

## People and voiceprints

- **A person** belongs to one owner, with a name, aliases (old names), a role, a company, and an `is_owner` flag ("This is me" in the app).
- **A voiceprint** is one person's voice averaged over one meeting. Its `source` is `user` (the owner confirmed it) or `auto` (the pipeline was confident). Every meeting a person is identified in can add one.
- **Matching** compares each speaker's centroid with the owner's voiceprints by cosine similarity, in memory, and scores each person by their best voiceprint.
- **Naming a speaker in the app** saves a `user` voiceprint from that speaker's centroid.
- **The pipeline saves an `auto` voiceprint** only at `high` confidence, on at least two kinds of evidence, with at least 30 s of clean speech, and never from a colleague's naming (shared voices, below).
- **The app asks "Is this Alex?"** until the owner confirms, unless the person's voice is already confirmed.
- **Shared voices** (`sharedVoices`) add colleagues' people to the matching as a pool, grouped by name: one entry a name, with the voiceprints their owners confirmed (never `auto` ones). Names the owner already knows by voice are left out, so their own person always wins. Only a matched entry's name reaches the agent, never a colleague's role or company, and naming it finds or makes the owner's own person by that name. So two colleagues' different "Johan"s share an entry and come out as "Johan", right for either, and one person under two spellings shows as two candidates for one voice.

## Cloud

- **S3:** one data bucket, KMS-encrypted, no public access, TLS only. Meetings live under `users/<sub>/meetings/<id>/`.
- **DynamoDB:** five tables (`meetings`, `people`, `voiceprints`, `folders`, `users`), each keyed by `owner` first. A meeting's state is its row in `meetings`.
- **The API:** an HTTP API with a JWT authorizer and one Python Lambda.
- **Sign-in:** any OIDC provider in config's `auth`, or else a Cognito user pool whose people an admin adds.
- **The site:** a private S3 bucket that only CloudFront reads.
- **AWS Batch:** one on-demand compute environment across two AZs, single-GPU instance types (config's `gpu.instanceTypes`), and an x86 image with the models baked in. Config's `gpu.warm` keeps none, one, or one in set hours.
- **Network:** public subnets, no ingress, IMDSv2, and a firewall rule that gives the agent's shell no network.

## Why it's built this way

The choices that shape Tally, so they aren't undone by mistake.

- **Everything belongs to the signed-in person.** S3 keys live under `users/<sub>/` and every table is keyed by `owner` first. The API takes the owner from the token and the job from the upload's key, so another person's data is simply not found.
- **Checks guard what reaches beyond one meeting, and nothing less.** An owner can break their own meeting and delete it, so that isn't guarded. Anything that reaches another person, the owner's meeting list, People, search or the agent's sandbox is.
- **GPU work runs on AWS Batch, on demand, not spot.** In eu-north-1, GPU spot had a placement score of 1 out of 10 and every launch failed. Batch won't move a job on from a spot environment without capacity, so jobs would wait forever.
- **A GPU, not a CPU.** Measured on an L4 (g6.xlarge), an hour-long meeting takes about 3 minutes to prepare and the agent about a minute. A cold start adds 6–7 minutes (boot and the 14 GB image), and the whole costs about $0.16 of GPU. A 16-vCPU instance was about 11 times slower and cost more.
- **Code does what it can, the agent only what needs judgement.** Prepare runs first, the agent works through the CLI, and the harness decides when it's done.
- **The agent's shell has no network and no AWS credentials.** The job's role reaches every owner's meetings, and anyone in a meeting can say anything to the agent. So transcript text is data, never instructions, and the job trusts nothing in the agent's folder.
- **Voiceprints never reach the agent.** Prepare matches the voices before the agent runs, and the agent's folder holds names and scores only. A voiceprint's one place is the tables, and deleting a person deletes theirs.
- **0.65 is a strong voiceprint match.** Measured across meetings, the same person scored 0.81 to 0.96, and different people at most 0.49. Below 0.5 is no match.
- **The owner has the last word on who someone is.** The agent suggests and the owner confirms. Only a voice the owner confirmed is trusted from then on, and the agent fills in a role or company only where it's empty.
- **Shared voices are off by default.** Voiceprints are biometric data, and `sharedVoices` shares the voices of people who never used Tally.
- **The apps are built on the person's own computer, with no developer account.** macOS blocks a downloaded app without a paid Developer ID, but not one built locally. The cost: Smart App Control and organisations' app control block an app built on a PC until it's signed.
- **The Windows app mirrors the Mac's.** Each is native, and `Tally.Core` ports the Mac's model with the same names, so the two compare file by file.
- **Nothing deployment-specific is in the repository.** Settings live in the local `config.json`.
- **The licence is PolyForm Shield 1.0.0.** Anyone may use, change and share Tally, at work too, but not to offer a product that competes with it.
