# The agent

The meeting agent runs once per meeting, unattended, in the Batch container after `tally prepare`. The code is in `pipeline/tally/`: `harness.py`, `tools.py` and `cli.py`.

## Design

An agent orchestrates a deterministic CLI: code does everything that has a right answer, the model only the judgement.

- **Four tools:** `bash`, `read_file`, `write_file` and `edit_file` (`tools.py`). Everything else is the `tally` CLI, run through `bash`.
  - Tools never raise. They return a status and a text, and an error's text says what to do next.
  - Output is capped: 20,000 characters per stream from `bash`, and 2,000 lines or 100,000 characters per `read_file`.
  - `bash` runs in the meeting's directory, killed on timeout (120 s by default, 900 s at most). On Batch it runs as the unprivileged `agent` user, with no AWS credentials and no network.
  - The file tools refuse paths outside the meeting's directory, and won't overwrite a file the agent hasn't read.
- **The CLI** (`cli.py`): files are the interface, and stdout is a small JSON envelope, `{"ok": true, ...}` or `{"ok": false, "error", "type", "hint", "traceback"}`. Commands are idempotent and deterministic, with no model calls. They report evidence, not verdicts.
- **Judgements go through the CLI,** with their evidence: `tally name S2 --person "Alex" --confidence high --evidence "address: 00:12:31 'Thanks Alex' → S2 replies"`.
- **The harness decides when the run is done** (`harness.py`). After each turn it runs check, with code loaded before the agent started. If work remains, it prompts again with what's missing. Three turns in a row without progress fail the run, as do 8 turns; after 200 tool calls the tools are refused.
- **One agent, no human in the run.** The owner confirms later, in the app.

## The model

Config's `modelId` on Bedrock, with adaptive thinking at high effort and prompt caching. Strands' `NullConversationManager` keeps the whole conversation, so the transcript read early isn't dropped, and `callback_handler=None` keeps transcripts out of the log. A turn that fails for a passing reason (throttling, a timeout, a dropped connection) is retried after 10, 30, then 90 s. Each run's tokens are kept with the meeting and priced at `modelPrices`.

## The prompt

- **One fixed Markdown file,** `prompts/system.md`, followed by `prompts/summary.md`, so it can be cached. Each run adds the meeting's details, the owner's known people, and a line per speaker with their stats and voiceprint matches. Names, roles and companies are quoted there: they are text someone typed, never instructions.
- **Transcript text is data, not instructions.** Anyone in a meeting can say "ignore your instructions". The prompt says so, and the CLI returns transcript lines only in a `transcript` field.
- **Tool docstrings are prompts:** when to use a tool, and when not.

## The run

1. **Who is who:** voiceprints, introductions, direct address followed by a reply, the meeting's structure, elimination and the mic/system ratio. Diarization is fixed with `merge` and `split`. Every speaker ends named, as a known or new person, or unknown.
2. **The summary:** with everyone named, the agent reads the whole transcript and writes the summary, which `tally write-summary` validates.
3. **Publish:** once check passes, the job publishes in its own process (`publish.py`). It saves `auto` voiceprints only through the gate ([architecture.md](architecture.md#people-and-voiceprints)), creates new people, and writes `results/meeting.json`, the clips and the owner's rows.

**A summary again:** `tally-job users/<sub>/meetings/<id>/rewrite` runs the agent on the meeting's working files, with everyone identified as the owner said and background voices taken out. It writes only the summary.

The job saves each run's conversation to `run/conversation.json`. A transcript fits in context, so there's no compaction; a lost host means Batch retries the job.

## The CLI

| Command | Purpose |
|---|---|
| `tally prepare <audio>` | Decode, language ID, transcribe, align, diarize, embed, match, clips (run by the job, not the agent) |
| `tally speakers` | Each speaker's stats, voiceprint matches, identity, and the speakers here whose voice is alike |
| `tally show [--from T] [--to T] [--speaker S2]` | Transcript lines with times and speaker ids, about 15,000 characters at a time |
| `tally grep <regex>` | Where something was said |
| `tally name <speaker> --person <name> --confidence low\|medium\|high --evidence "<kind>: <text>"... [--role <role>] [--company <company>]` | Record who a speaker is, with evidence |
| `tally unknown <speaker> --reason <text>` | Record that a speaker can't be identified |
| `tally merge <a> <b>` | Two clusters are one person |
| `tally split <speaker> --at <T>` | One cluster is two people from a point in time |
| `tally retranscribe --language sv\|en` | Transcribe again in the other language, keeping the speakers |
| `tally write-summary <file.json>` | Validate and store the summary |
| `tally check` | What's still missing before the meeting can be published |

## The summary standard

`pipeline/tally/prompts/summary.md`. A 30-second read comes first (title, overview, 1 to 7 points), then what to act on (decisions, action items, open questions), the topics with notes and an outcome, and the attendees. It favours outcomes and exact numbers over process, and invents nothing. Speakers appear only as `{S2}` tokens. `tally write-summary` checks the shape, the tokens, the dates and the lengths ([data.md](data.md)).

## Tests and evals

`tests/scripted.py` is a Strands model that replays scripted replies, so the whole loop runs in tests with the real tools and CLI, with no AWS. Strands is pinned in `pipeline/pyproject.toml`: test before raising it. The evals, graded by code, are in [evals/README.md](../evals/README.md).
