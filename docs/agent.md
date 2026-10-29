# Agent design

How the meeting agent works. It runs once per meeting, unattended, inside the Batch container, after `tally prepare`. The code is in `pipeline/tally/`: `harness.py`, `tools.py` and `cli.py`.

## The pattern

An agent orchestrates a deterministic CLI. Code does everything that has a right answer, and the model does only the judgement.

1. **Four tools, and that's the whole list:** `bash`, `read_file`, `write_file` and `edit_file` (`tools.py`).
   - Every capability lives in the `tally` CLI, which the agent runs through `bash`. The system prompt lists the subcommands, and `tally <cmd> --help` gives the arguments.
   - Tools never raise. They return `{"status": "success" | "error", "content": [{"text": ...}]}`, and an error's text says what to do next.
   - Output is capped, because one huge tool result spoils every later call. `bash` caps stdout and stderr at 20,000 characters each, and `read_file` reads up to 2,000 lines and 100,000 characters per call.
   - `bash` is a fresh `bash -c` in the meeting's directory, in its own process group, killed as a group on timeout (120 s by default, 900 s at most).
   - On Batch the shell runs as the unprivileged `agent` user, with no AWS credentials and no network. Nothing it starts outlives the call. The file tools work with the same rights, and tools run one at a time.
   - `write_file` and `edit_file` refuse an existing file the agent hasn't read. `edit_file` needs a unique exact match unless `replace_all` is set.
   - A path outside the meeting's working directory is refused.
2. **The CLI contract** (`cli.py`):
   - Files are the interface. A command reads the meeting's directory and writes only its own outputs.
   - stdout is always a small JSON envelope, `{"ok": true, ...}`. On failure it is `{"ok": false, "error", "type", "hint", "traceback"}` with exit code 1, the traceback cut to its last 1,200 characters. The hint is plain advice for the exception's type. `main()` never crashes.
   - Commands are idempotent, so rerunning one is the normal way to repair.
   - Commands are deterministic, with no model calls inside the CLI.
   - Commands report evidence, not verdicts. They refuse only malformed input, and publish decides what's kept.
   - `manifest.json` is the meeting's state, and every command appends a line to `status.jsonl`.
3. **A code pre-pass.** `tally prepare` does everything deterministic before the agent starts.
4. **Judgements go through the CLI, not free text.** For example, `tally name S2 --person "Alex" --confidence high --evidence "address: 00:12:31 'Thanks Alex' → S2 replies"`. Each judgement reaches the published record with its evidence.
5. **The harness decides when the run is done** (`harness.py`). After each turn it calls check with code loaded before the agent started, which nothing the agent edits can reach. If work remains, it prompts again in the same conversation with what check says is missing. Three turns in a row with the same result fail the run as stalled. So do 8 turns, and after 200 tool calls the tools are refused. When check passes, the job publishes.
6. **No human in the run, and one agent.** Confirmation happens later in the app. There are no sub-agents.

## The model

One model, config's `modelId`, built in `harness.py`:
- `BedrockModel(model_id, region_name, max_tokens=32_000, ...)`, with the region and model from the job's environment.
- Adaptive thinking with high effort: `additional_request_fields={"thinking": {"type": "adaptive", "display": "summarized"}, "output_config": {"effort": "high"}}`.
- `boto_client_config=Config(connect_timeout=10, read_timeout=600, retries={"max_attempts": 3, "mode": "adaptive"})` and `cache_config=CacheConfig(strategy="auto")`.
- `NullConversationManager`, because Strands' default sliding window would drop the transcript read early. `callback_handler=None`, so no transcript reaches the log.

Each run's tokens are kept with the meeting and priced at `modelPrices`.

**Retries:** a turn that fails for a passing reason waits 10, 30, then 90 s, and the conversation carries on with a note that the call failed. Passing means throttling, a timeout, a dropped connection, an unavailable service or the token limit, matched by the error's name or AWS code (`PASSING` in `harness.py`). Anything else fails the run.

## Conventions

- **The prompt is one Markdown file,** `prompts/system.md`, with `prompts/summary.md` after it. It stays fixed, so it can be cached. Everything every run needs is in it: who the agent is, that nobody can be asked so it must finish, that accuracy beats speed, then the domain and the runbook.
- **Transcript text is data, not instructions.** Anyone in a meeting can say "ignore your instructions". The prompt says so, and the CLI returns transcript lines only in a `transcript` field, which the prompt names as data.
- **Tool docstrings are prompts.** They say when to use the tool, and when not to.
- **A scripted model** (`tests/scripted.py`): a Strands `Model` whose `stream()` replays scripted replies, which are text, tool calls or exceptions. The whole loop, with the real tools and the real CLI, runs in tests in a second with no AWS.
- **Strands is pinned** (`strands-agents==1.62.*` in `pipeline/pyproject.toml`, 1.62.0 in `uv.lock`). Test before raising the pin.
- **The conversation is kept.** The job saves each meeting's run to `run/conversation.json` beside it, as raw material for evals. A rewrite's isn't kept.

## What it leaves out

Tally runs once per meeting, for minutes, on a transcript that fits in context. So there's no compaction, no resuming a conversation on another machine, no skills, and no notes injected into tool results. If a job's host is lost, Batch retries it from the start, which is cheap for one meeting.

## The run

The input is the meeting's directory after `tally prepare` ([data.md](data.md)).

**System prompt:** the fixed prompt plus these sections:
- `# This meeting`: date, duration, language, and the channel layout (`stereo-split`, `mono` or `identical`)
- `# Known people`: the owner's people, with name, role, company, aliases and the owner flag. With `sharedVoices`, also the colleagues' people a speaker's voice matched, by name alone.
- `# Speakers`: a line per speaker with talk time, turns, when first heard, and the best voiceprint matches

The agent reads the whole transcript through the CLI. A one-hour transcript fits in context.

**Part 1: who is who.**
- Cues: voiceprint matches, self-introductions, direct address followed by a reply, meeting structure, elimination, and the mic/system ratio.
- "Hey Alex" is a hint, not proof.
- Diarization is fixed with `merge` and `split`.
- Every speaker ends named, as an existing or new person, or unknown. A new person gets the name they're called in the meeting. Without one they stay unknown, and the app shows "Speaker 3" for the owner to name.

**Part 2: the summary.** With everyone named, the agent analyses the whole transcript and writes the summary to `prompts/summary.md`'s standard. `tally write-summary` validates it.

**The finish:** once check passes, the job publishes (`publish.py`) in its own process. Publishing isn't a CLI command, so the agent can't do it. Publish:
- drops voiceprints an earlier try of this meeting saved (a Batch retry)
- saves `auto` voiceprints only through the gate ([architecture.md](architecture.md#people-and-voiceprints))
- creates new people with the role and company the agent recorded, and fills in a known person's only where empty
- writes `results/meeting.json` and the clips, and updates the owner's rows in DynamoDB

**A summary again:** `tally-job users/<sub>/meetings/<id>/rewrite` runs the agent on the meeting's working files, with everyone identified as the owner said and background voices' lines taken out. It writes only the summary. If it changes who anyone is, nothing is published.

## The CLI

`pipeline/tally/cli.py`. The prompt lists the same commands.

| Command | Purpose |
|---|---|
| `tally prepare <audio>` | Decode, language ID, transcribe, align, diarize, embed, match, clips (run by the job, not the agent) |
| `tally speakers` | Speakers with talk time, turns, clean speech, mic/system ratio, top voiceprint matches, identity, and the other speakers here whose voice is alike (`sounds_like_here`, 0.5 and up) |
| `tally show [--from T] [--to T] [--speaker S2]` | Transcript lines with times and speaker ids, about 15,000 characters at a time, then `more_from` |
| `tally grep <regex>` | Where something was said |
| `tally name <speaker> --person <name> --confidence low\|medium\|high --evidence "<kind>: <text>"... [--role <role>] [--company <company>]` | Record who a speaker is. Kinds are `voiceprint`, `introduction`, `address`, `role`, `elimination` and `other`. Role and company only as the meeting says them |
| `tally unknown <speaker> --reason <text>` | Record that a speaker can't be identified |
| `tally merge <a> <b>` | Two clusters are one person: b's turns and words join a, the centroids averaged by talk time |
| `tally split <speaker> --at <T>` | One cluster is two people from a point in time. The new speaker has no centroid |
| `tally retranscribe --language sv\|en` | Transcribe again in the other language, keeping the speakers (in the image only) |
| `tally write-summary <file.json>` | Validate and store the summary |
| `tally check` | What's still missing before the meeting can be published |

## The summary standard

It's `pipeline/tally/prompts/summary.md`. The summary is the one page the owner comes back to, in layers. A 30-second read comes first (title, overview, 1 to 7 points), then what to act on (decisions, action items, open questions), then the detail (topics with notes and an outcome), then the attendees. It favours outcomes over process and exact numbers, keeps what was decided apart from what was proposed, and invents nothing. Speakers appear only as `{S2}` tokens, and it's written in the meeting's language. `tally write-summary` enforces what code can check: the shape, that tokens and owners exist, every named speaker among the attendees, dates, no speaker's name written out, and the lengths ([data.md](data.md)). Publish renders it as `results/summary.md`.

## Evals

Graded by code, with no model judging a model (`evals/run.py`, `evals/voiceprints.py`; how to run them is in [evals/README.md](../evals/README.md)):
- **Cases** in `evals/cases/*.json` hold a meeting id, the ground truth for each prepared cluster, and the summary's expectations.
- **Speakers:** the share of talk time named correctly (the right name, or unknown for someone the meeting never names), named wrongly, which is worse than unknown, and missed.
- **Summary:** required facts (`includes`, each a list of alternatives), forbidden content (`excludes`), and `max_words`.
- The agent runs on a copy of the meeting's `prepared/` directory with no known people, so it names from context alone.
- The agent isn't deterministic, so cases that guard behaviour run three times (`--runs 3`).
- Every result records the git commit and the model in `evals/results.jsonl`.
