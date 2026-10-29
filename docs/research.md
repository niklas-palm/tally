# Research notes

These findings back the choices in [architecture.md](architecture.md#why-its-built-this-way). They were checked on 2026-10-22 against PyPI, Hugging Face, AWS docs and the EC2 pricing APIs.

## Versions (2026-10-22)

| Package | Version | Note |
|---|---|---|
| `whisperx` | 3.8.6 | Requires Python <3.14 and **pins `torch~=2.8.0`**, torchaudio 2.8 and torchcodec <0.8 |
| `pyannote.audio` | 4.0.7 | |
| `faster-whisper` | 1.2.1 | `"turbo"` alias → `mobiuslabsgmbh/faster-whisper-large-v3-turbo` |
| `strands-agents` | 1.62.0 | |
| `ctranslate2` | 4.8.3 | Built for CUDA 12.8 with cuDNN off and cuBLAS loaded at run time: torch 2.8's `nvidia-cublas-cu12` wheel serves it (the image's `LD_LIBRARY_PATH`) |

The ML packages are the `ml` extra in `pipeline/pyproject.toml`: only the x86 image installs them, so the CLI, the agent and the tests run anywhere, arm64 included.

**Gotchas met while building:**
- pyannote 4 sends usage telemetry unless `PYANNOTE_METRICS_ENABLED=0` (set in the image).
- Strands' default conversation manager is a sliding window of 40 messages, which would drop the transcript an agent read early: the harness uses `NullConversationManager`.
- Strands prints the model's output to stdout unless `callback_handler=None`; on Batch that's the log, which must never hold a transcript.
- CDK's jsii refuses its package cache when `~/.cache` is group-writable, so the Makefile sets `JSII_RUNTIME_PACKAGE_CACHE=disabled`.

## Transcription

**WhisperX** bundles three things: batched faster-whisper (CTranslate2), wav2vec2 forced alignment for word timestamps, and pyannote diarization with `assign_word_speakers`.
- English alignment uses `WAV2VEC2_ASR_BASE_960H`.
- Swedish alignment uses `KBLab/wav2vec2-large-voxrex-swedish`.

**English: `large-v3-turbo`** (~809M parameters, decoder cut from 32 layers to 4).
- Much faster than large-v3 with only minor quality loss. MIT licence.
- Use `large-v3` as the quality fallback, if evals show a need.

**Swedish: `KBLab/kb-whisper-large`** (Apache-2.0).
- The repo root ships a CTranslate2 `model.bin`, but its `config.json` is Hugging Face's.
  - WhisperX's batched decoding then fails with "type must be number, but is number". Reproduced on 2026-10-22 with kb-whisper-small on CPU.
  - Plain faster-whisper copes.
  - `download_models.py` writes CTranslate2's keys from the model's `generation_config.json`.
- Revisions: `standard`, `strict` (closer to verbatim) and `subtitle`. **Use `strict`.**
- WER, FLEURS / CommonVoice / NST:

| Model | FLEURS | CommonVoice | NST |
|---|---|---|---|
| kb-whisper-large | 5.4 | 4.1 | 5.2 |
| OpenAI large-v3 | 7.8 | 9.5 | 11.3 |

- Even kb-whisper-small beats OpenAI large-v3.
- Source: https://huggingface.co/KBLab/kb-whisper-large

**Language ID:**
- Use the OpenAI turbo/large-v3 model, not KB-Whisper, which is biased towards Swedish.
- In faster-whisper, set `vad_filter=True` and `language_detection_segments` to about 3–5, then compare P(sv) with P(en) only.
- If the margin is low, the fallback is `speechbrain/lang-id-voxlingua107-ecapa` (cheap on CPU). Optional.

## Diarization and embeddings

**`pyannote/speaker-diarization-community-1`** with pyannote.audio 4:
- **Licence:** the code is MIT, the weights CC-BY-4.0, and the repo is **gated on Hugging Face**, so the account whose token builds the image must accept its terms. Download the weights at image build time with an HF token, so the runtime is offline and has no token.
- **DER:** AMI-IHM 17.0, VoxConverse 11.2, DIHARD3 20.2. Better speaker counting than 3.1.
- **Speaker count:** `num_speakers` or `min_speakers` / `max_speakers`, with no hard cap.
- **Output** (`DiarizeOutput`): `speaker_diarization`, `exclusive_speaker_diarization` (one speaker at a time, used for word assignment) and `speaker_embeddings`. The s-th embedding is the centroid of the s-th label.
- **Runtime:** 31 s per hour of audio on an H100 (README). On an L4 and on CPU it's measured below, under Compute.
- **Gotcha:** pyannote 4 needs `torchcodec`, which has **no Linux aarch64 wheels**. Use x86 images and instances (g6 is x86).

**Alternatives,** used only if measurements call for them:
- sherpa-onnx 1.13.8: fast on CPU, weaker on overlap.
- ReDimNet or WeSpeaker ResNet293: stronger standalone embedders for matching, computed on the exclusive segments.
- pyannoteAI's hosted "precision" tier: out, because data would leave AWS.

**Voiceprint matching:** cosine similarity on L2-normalised centroids, with 0.65 as the strong-match line (measured below, under Compute). Expect real-meeting DER around 15–20%, so the agent needs `merge` and `split`.

## Mic and system audio

- The meeting app doesn't play the owner's own voice back. So the system channel holds remote speakers, and the mic channel holds the owner, people in the room, and bleed from the speakers.
- Diarize the mono mix once, and compute a per-cluster mic/system energy ratio from the separate channels as a clue (high means local).
- No echo cancellation in v1. Add AVAudioEngine voice processing only if bleed measurably hurts WER.

## Compute

**AWS Batch, managed EC2 compute environment, GPU, min 0 vCPU,** or `gpu.warm`'s vCPUs while a GPU is kept warm. The CDK Python L2 constructs are stable:
- `ManagedEc2EcsComputeEnvironment(instance_types=[...], minv_cpus=0, images=[EcsMachineImage(image_type=ECS_AL2023_NVIDIA)], launch_template=...)`; the launch template gives the instances a 100 GB root volume, because the image is 14 GB compressed.
- Pair it with a `JobQueue`, an `EcsJobDefinition` with `EcsEc2ContainerDefinition(gpu=1)`, and an EventBridge rule targeting the queue.
- **Spot is out of reach for GPUs in eu-north-1** (2026-10-22): placement score 1/10 in both AZs for every listed type (eu-central-1: 9), and every spot launch failed with `UnfulfillableCapacity`. Batch only moves a job to a queue's next compute environment when the first is at its vCPU limit, so a spot-then-on-demand queue waits forever. Hence on-demand only.

**Single-GPU types in eu-north-1** (both AZs, checked 2026-10-22): `g6.xlarge`, `g6.2xlarge`, `g6.4xlarge`, `g6.8xlarge`, `gr6.4xlarge` (L4 24 GB); `g6e.xlarge`, `g6e.2xlarge` (L40S 48 GB). The `g6f` types are fractional GPUs, so they're out.

**Prices in eu-north-1** (2026-10-22, EC2 price list):

| Instance | Spot $/h | On-demand $/h |
|---|---|---|
| g6.xlarge | ~0.18 | 0.854 |
| gr6.4xlarge | | 1.633 |

With several jobs at once, or when g6.xlarge has no capacity, Batch launches a bigger type (gr6.4xlarge, 16 vCPUs, still one GPU): about twice the price for the same GPU. On-demand g6.xlarge did run out in both AZs for at least 12 minutes on 2026-10-22, so all seven types stay listed.

**Measured on the three samples** (2026-10-22, L4, large-v3-turbo, all English):

| Meeting | Length | Decode | Diarize | Language ID | Transcribe + align | Clips | Prepare | Agent |
|---|---|---|---|---|---|---|---|---|
| 2026-03-03 | 46:54 | 4.9 s | 97 s | 5.9 s | 51 s | 2.4 s | 162 s | 30 s, 6 tool calls |
| 2026-10-06 | 55:15 | 4.8 s | 100 s | 6.6 s | 61 s | 2.5 s | 175 s | 37 s, 8 tool calls |
| 2026-10-20 | 28:48 | 2.7 s | 55 s | 4.1 s | 32 s | 2.0 s | 96 s | 29 s, 5 tool calls |

- Diarization is the slowest step: about 1.8 minutes per hour of audio, transcription about 1.1.
- A job ran 2.2 to 3.6 minutes in all, model loading included.
- Cold start, from the instance launching to the job running, was about 7 minutes: boot, ECS registration and the 14 GB image pull.
- So a one-hour meeting is about 11 minutes of instance time, $0.16 on a g6.xlarge or $0.30 on a gr6.4xlarge. Batch then scales back to the one instance kept on, or to zero without `gpu.warm`.
- A Batch compute environment can't change its allocation strategy in place, and one with `BEST_FIT` can't be updated at all later, so the environment stays on `BEST_FIT_PROGRESSIVE` (tried 2026-10-22; CloudFormation rolled back).

**End to end through S3** (2026-10-22, the three samples uploaded one after another with `meta.json`, as the app will):

| Meeting | Upload → `processing` | → `ready` | Why |
|---|---|---|---|
| 2026-03-03 (46:54) | 7.0 min | 10.0 min | cold start |
| 2026-10-06 (55:15) | 1.0 min | 3.9 min | the instance was still warm |
| 2026-10-20 (28:48) | 17.6 min | 19.8 min | 11 min without g6.xlarge capacity (and a redeploy), then a 6 min cold start on a gr6.4xlarge |

**Voiceprint test** (`evals/voiceprints.py`, 2026-10-22; three meetings of one council, one room microphone, speakers with 10 s or more):
- 14 pairs of the same person across meetings scored 0.812 to 0.958 (median 0.91); 105 pairs of different people scored −0.07 to 0.488 (median 0.13).
- With every other meeting's centroids as the store: any threshold from 0.5 to 0.85 matched 86% of talk time to the right person, 0% to a wrong one, and left 14% unmatched (the delegates who appear in one meeting only). At 0.45 a wrong match appears (4%); at 0.90 right matches drop to 70%.
- A person's best voiceprint and their average did the same.
- End to end, the third meeting matched five of its six speakers at 0.86 to 0.97 against the voiceprints the first two had saved; the sixth (0.49, below the line) had none yet and was named from context.

**Agent evals** (`evals/run.py`, 2026-10-22, Sonnet 5.5, context alone, three runs per meeting): 9 of 9 runs named 100% of talk time correctly, with no wrong names, and left the one never-named speaker unknown. Every summary had all of its required facts, at 269 to 381 words. A run took 26 to 41 s and 5 to 10 tool calls, always one turn.

With the layered summary (2026-10-22, 9 more runs across the prompt's revisions): still 100% of talk time named correctly, and every summary had all of its required facts. They ran 850 to 1,185 words, the dense 47- and 55-minute meetings near the 1,200 ceiling: the first draft often overshoots, and `write-summary`'s refusal makes the agent trim. A run took 39 to 61 s, always one turn. Reading the pages showed two faults the grades didn't: bare surnames in vote lines (now refused by `write-summary`) and procedure listed as decisions (now ruled out in the standard).

With the summary standard for any workplace (2026-10-28, one run per meeting): 100% of talk time named correctly, no wrong names, and every summary had all of its required facts, at 1,161 to 1,470 words. A run took 47 to 68 s.

**The image build** (`make image`, CodeBuild, 2026-10-28): 14 minutes, from 17:40 to 17:54.

**Rejected:**
- Fargate: no GPU.
- Lambda: 15 min and 10 GB limits, no GPU.
- SageMaker async: no spot, higher prices, a serving contract.
- ECS on EC2 alone: you'd rebuild Batch's queue and retries.
- AgentCore Runtime: GA in eu-north-1, but it adds invoke and session machinery that a batch step doesn't need. A possible fit for a chat about meetings, should it come back.
- Lambda MicroVMs (checked 2026-10-22 against the Lambda docs):
  - ARM64 (Graviton) only, at most 16 vCPU and 32 GB at peak, 32 GB of disk, 8 hours a MicroVM
  - pyannote 4 needs torchcodec, which has no Linux aarch64 wheels, and a CPU-only pipeline would be several times slower than an L4
  - so the GPU job stays; MicroVMs were the chat agent's design, now paused

**CPU instead of a GPU** (2026-10-28, c7i.4xlarge: 16 vCPUs, Xeon Platinum 8488C, $0.76 an hour; `TALLY_DEVICE=cpu`, int8): the 28:48 sample prepared in 1,049 s, with diarization 663 s and transcription 345 s, against 55 s and 32 s on the L4: about 11 to 12 times slower, so about 35 minutes and $0.45 for an hour-long meeting, against about 3 minutes and $0.16 on a g6.xlarge. Pulling the 14.2 GB image took 251 s, which a Fargate task would pay on every job (it keeps no image). A CPU fallback costs more and takes ten times as long, so more GPU families are the better answer to GPU capacity.

**GPU capacity, seen the same day:** launching one of each to test the image, g5.xlarge had no capacity in eu-north-1b or 1c, and g7e.2xlarge none in 1a or 1c; g4dn.xlarge and g7.2xlarge launched in 1a. The image hasn't been run on g4dn (T4), g5 (A10G), g7 or g7e (Blackwell) yet.

## Bedrock (eu-north-1, checked 2026-10-22)

Active inference profiles:
- `eu.anthropic.claude-sonnet-5-5`
- `eu.anthropic.claude-opus-5-5`
- `eu.anthropic.claude-haiku-5-5`
- `global.` variants of each

Notes for 5.5 models:
- Opus 5.5 always thinks; set the effort explicitly.
- 5.5 models support only `tool_choice` `auto` and `none`. Strands uses `auto`, so that's fine. Avoid forced tool use, e.g. for structured output, and check how Strands implements it.

## Trigger

- S3 sends events to EventBridge only when the bucket has EventBridge notifications turned on (`event_bridge_enabled=True` in CDK; CDK sets it with a deploy-time custom resource).
- An EventBridge rule targets the Batch job queue directly. An input transformer with the template `{"Parameters": {"key": <$.detail.object.key>}}` becomes the job's parameters, and the job definition's command `["Ref::key"]` receives the key (verified 2026-10-22: the three sample uploads each started their job).
- The rule matches keys with the wildcard `users/*/meetings/*/audio.*`. EventBridge's `*` also crosses `/`, so the job checks the key exactly (`users/<sub>/meetings/<ulid>/audio.m4a|mp3|wav`) and ignores anything else.

## Mac app gotchas (2026-10-22)

- **`SMAppService.mainApp.status` takes half a second** (520–630 ms, measured 2026-10-29). It's an XPC call to the system's login-item service, not a property. The menu's "Open at login" switch read it each time the menu drew. The menu bar panel stays alive while it's hidden and redraws whenever something it shows changes, such as a meeting's results loading when one is selected. So both opening the menu and clicking a folder in the window waited on it. Now it's asked off the main thread, at launch and when the menu opens, and the switch shows the stored answer. A 75-second sample of the main thread afterwards: call detection 591 ms (also moved off the main thread), a sync 92 ms, the meetings-folder pass 32 ms, the rest idle.

- **SwiftPM with the Command Line Tools only:** the default build system fails ("Unknown error parsing property list"); `--build-system native` works. Tests also need the CLT's `Testing.framework` on the framework path.
- **SwiftUI's `@State` is a macro whose plugin ships only with Xcode.** Observation's `@Observable` does build. Plain `State(initialValue:)` stored values work for view-local state.
- **Core Audio aggregate devices with a tap:** with `kAudioAggregateDeviceTapAutoStartKey` on, the device runs only while something plays, so a quiet room records nothing. Off, it runs from start to stop.
- **`Text(date, style: .timer)` in a MenuBarExtra label** makes the status item redraw without pause, which pegs the main thread. Update a plain string once a second instead.
- **Menu bar images are template images:** a red dot needs `isTemplate = false`.
- **The login keychain ties an item to the exact build that saved it** when the app has no team ID, so each rebuild asks once.
