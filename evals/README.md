# Evals

Two evals check the parts of Tally that need judgement: the agent naming speakers from what is said, and voiceprints matching people across meetings. The cases here point at the maintainer's meetings, whose prepared data isn't public, so to run them you write cases for recordings of your own the same way.

## The samples

The maintainer's samples are three public recordings of a municipal council's meetings, kept out of git. Each is one room recording of several speakers, mostly in English, with plenty of naming cues ("Councilmember …", roll calls, public comment), and the same people recur.

| File | Duration | SHA-256 |
|---|---|---|
| `council/2026-03-03-council.mp3` | 46:54 | `bb18673376483b835d60485073fec1c2232ac33541cd40aabe0659b8656ee9e1` |
| `council/2026-10-06-council.mp3` | 55:15 | `82923c802f5138b88102f719f01e69197275448127f0a9ce552ad7d0d711f47c` |
| `council/2026-10-20-council.mp3` | 28:48 | `acf70c9ca0045ad3f564f1dc2642427a7cdc4f20367028440221ea6f986c8357` |

They don't cover call audio, Swedish, the recording person's own voice, or mic bleed.

## Ground truth

Each case in `evals/cases/` names a meeting and its speakers, and the agent never sees it:
1. **Names:** each speaker by the name the meeting gives them, or a role where it gives none ("Chair", "Public-1"). `match` lists the spellings Whisper uses.
2. **The same person** keeps the same name in every meeting.
3. **The voiceprint test** matches each meeting's speakers against the others' voiceprints, and reports correct and false matches by talk time at each threshold.
4. **The agent test** starts with no known people, and grades by talk time the speakers it names from context alone (a wrong name is worse than "unknown"), and whether the summary says what the case's `summary` requires.

The maintainer's ground truth came from a careful pass by Claude over each prepared transcript, with timestamped evidence. A new image or new models may number the clusters differently, so check the cases against a new `prepared/` before trusting a result.

## Running them

Put a recording through your deployment, from an app or the API. Copy its meeting's `prepared/` to `meetings/<id>/prepared/` in the data bucket (the stack's `BucketName` output), a prefix only the evals use, and write a case for it. Then:

```
cd pipeline
AWS_REGION=<region> TALLY_MODEL_ID=<modelId> uv run python ../evals/run.py --runs 3 --bucket <data bucket>   # the agent; appends to evals/results.jsonl
AWS_REGION=<region> uv run python ../evals/voiceprints.py --bucket <data bucket>                              # the voiceprint test
```

The agent eval calls Bedrock, so it needs AWS credentials. The commit ids in `evals/results.jsonl` are from before publication.
