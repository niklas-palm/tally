# Evals

The eval cases point at the maintainer's meetings, whose prepared data isn't public, so you can read the method here but can't run the cases as they are. To evaluate your own deployment, use recordings of your own, and write cases for them the same way.

The maintainer's samples are public recordings of a municipal council's meetings. They aren't in git (`.gitignore` keeps them out) and aren't published with Tally.

## Where

Keep your recordings in a private samples bucket of your own, outside the stack, in the stack's region, under `council/` or a prefix of your choice. Turn Block Public Access on, use SSE-S3 (AES256, with a bucket key), add a TLS-only bucket policy, and tag it `project=tally`. The hashes below say whether a copy is the same as the maintainer's.

```
aws s3 cp s3://<your samples bucket>/council/ samples/ --recursive --region <region>
```

## Files

| S3 key | Original filename | Duration | Size | SHA-256 |
|---|---|---|---|---|
| `council/2026-03-03-council.mp3` | `Council Meeting Audio 2026-3-3.mp3` | 46:54 (2813.7 s) | 45.0 MB | `bb18673376483b835d60485073fec1c2232ac33541cd40aabe0659b8656ee9e1` |
| `council/2026-10-06-council.mp3` | `10-06-2026-Council-Meeting.mp3` | 55:15 (3314.5 s) | 53.0 MB | `82923c802f5138b88102f719f01e69197275448127f0a9ce552ad7d0d711f47c` |
| `council/2026-10-20-council.mp3` | `10-20-2026-Council-Meeting.mp3` | 28:48 (1727.8 s) | 27.6 MB | `acf70c9ca0045ad3f564f1dc2642427a7cdc4f20367028440221ea6f986c8357` |

All three are MP3, 44.1 kHz, 128 kbps, stereo. The two channels are bit-identical, so in practice they are mono.

## What they are

Three in-person meetings of the same council, each one room recording of several speakers, mostly in English. Formal naming cues are common ("Councilmember …", "Mr. Mayor", roll calls, people introducing themselves at public comment). The same people recur, which tests the voiceprint loop: name someone in one meeting, and match them in the others.

They don't cover call audio (two channels, codec-compressed), Swedish, the recording person's own voice, or mic bleed. The maintainer will add recordings of those now that the apps record.

## Ground truth

Nobody involved knew who the speakers were, so:
1. **Names.** For each meeting, record each real speaker by the name the meeting gives them ("Councilmember Ortiz"), or by a role where it gives none ("Chair", "Clerk", "Public-1").
2. **Identity across meetings.** The same person keeps the same name in every meeting. A self-introduction or a roll call links them; otherwise listen to the clips.
3. **The voiceprint test.** Match each meeting's speakers against the voiceprints of the others. Measure the share of talk time matched correctly, the false matches, and how the threshold trades one against the other.
4. **The agent test.** With no known people, measure how many speakers the agent names correctly from context alone, and check that it says "unknown" rather than guess.

The names live in `evals/cases/`, and the agent never sees them.

## The cases, and running the evals

Each sample went through the pipeline once, as a meeting in the maintainer's data bucket. The evals run on its `prepared/` directory, which is prepare's output before any agent. To run them in your own deployment, put a recording through it from an app or the API. Then copy its meeting's `prepared/` to `meetings/<id>/prepared/` in your data bucket, and write a case for it with its ground truth.

| Case | Meeting id | Speakers (clusters) |
|---|---|---|
| `evals/cases/2026-03-03-council.json` | `01M5J4E97NMTFCSYR365RF1V1K` | 7, all named in the meeting |
| `evals/cases/2026-10-06-council.json` | `01M5J4E9BA52BZ13CTNZ98WKGP` | 7, all named |
| `evals/cases/2026-10-20-council.json` | `01M5J4E9F13K783X1XQ3D655FQ` | 6, five named; the CAO is never named |

- The same council recurs. Mayor Tarrian, Councillors Miller, Kaczynski and Warren, Deputy Mayor Fuel and the CAO (Colleen) appear in two or three meetings each, and three delegates appear once. Whisper spells several names more than one way, and each case's `match` lists the variants.
- The ground truth came from a careful pass by Claude over each prepared transcript on 2026-10-22, with timestamped evidence for each speaker. Rerunning `tally prepare` with a new image or new models may number the clusters differently, so check the cases against the new `prepared/` before trusting a result.
- The commit ids in `evals/results.jsonl` are from before publication, and aren't in this repository.

```
cd pipeline
AWS_REGION=<region> TALLY_MODEL_ID=<modelId> uv run python ../evals/run.py --runs 3 --bucket <data bucket>   # the agent, context alone; appends to evals/results.jsonl
AWS_REGION=<region> uv run python ../evals/voiceprints.py --bucket <data bucket>                              # the voiceprint test
```

The data bucket's name is the stack's `BucketName` output. The agent eval calls Bedrock from wherever it runs, so it needs AWS credentials there.

The eval meetings live under the legacy `meetings/<id>/` prefix, which nothing else uses. The pipeline itself runs only for meetings sent through the API, under `users/<sub>/` ([data.md](../docs/data.md)).
