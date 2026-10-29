"""Agent evals: the agent on each sample's prepared meeting, graded by code against the cases' ground truth.

    cd pipeline && uv run python ../evals/run.py [case ...] [--runs 3] [--bucket <data bucket>]

Each case (evals/cases/*.json) names a meeting in the data bucket. Its `prepared/` directory (prepare's output,
before any agent) is downloaded once into evals/.cache. Each run is the real harness on a fresh copy, with no known
people, so the agent names speakers from what is said alone. Results go to evals/results.jsonl with the commit and
the model. Never shown to the agent: the cases are only read here.

Speakers are graded by talk time, turn by turn: merge and split only relabel the prepared turns, so turn i before
and after the agent is the same stretch of speech. Correct is the right name (any of the case's `match` words
appears in the name the agent gave), or "unknown" for someone the meeting never names. A wrong name is worse than
an unknown, so it's counted apart. The summary must say each of `includes` (any of a list's alternatives, case
insensitive) and none of `excludes`, within `max_words`.
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import UTC, datetime
from pathlib import Path

import boto3

from tally import harness, summary, work
from tally.config import settings

EVALS = Path(__file__).parent
CACHE = EVALS / ".cache"


def prepared(bucket: str, meeting_id: str) -> Path:
    target = CACHE / meeting_id
    if not target.exists():
        s3 = boto3.client("s3", region_name=settings.region)
        for page in s3.get_paginator("list_objects_v2").paginate(Bucket=bucket, Prefix=f"meetings/{meeting_id}/prepared/"):
            for item in page.get("Contents", []):
                file = target / item["Key"].split("/prepared/", 1)[1]
                file.parent.mkdir(parents=True, exist_ok=True)
                s3.download_file(bucket, item["Key"], str(file))
    return target


def grade_speakers(case: dict, before: Path, after: Path) -> dict:
    truth = case["speakers"]
    speakers = work.read("speakers.json", after)["speakers"]
    totals = {"correct": 0.0, "wrong": 0.0, "missed": 0.0, "total": 0.0}
    named = {}
    for old, new in zip(work.read("diarization.json", before)["turns"], work.read("diarization.json", after)["turns"], strict=True):
        seconds = old["end"] - old["start"]
        expected = truth[old["speaker"]]
        identity = speakers[new["speaker"]]["identity"]
        given = None if identity.get("unknown") else identity["name"]
        named[old["speaker"]] = given
        if given is None:
            outcome = "correct" if "match" not in expected else "missed"
        else:
            outcome = "correct" if any(m in given.casefold() for m in expected.get("match", [])) else "wrong"
        totals[outcome] += seconds
        totals["total"] += seconds
    return {"correct": round(totals["correct"] / totals["total"], 3), "wrong": round(totals["wrong"] / totals["total"], 3),
            "missed": round(totals["missed"] / totals["total"], 3), "names": named}


def grade_summary(case: dict, after: Path) -> dict:
    found = work.read("summary.json", after)
    speakers = work.read("speakers.json", after)["speakers"]
    names = {k: (v["identity"].get("name") or k) for k, v in speakers.items()}
    text = re.sub(r"\{(S\d+)\}", lambda m: names.get(m[1], m[1]), " ".join(summary.texts(found))).casefold()
    expected = case["summary"]
    missing = [alternatives[0] for alternatives in expected["includes"] if not any(a.casefold() in text for a in alternatives)]
    forbidden = [e for e in expected.get("excludes", []) if e.casefold() in text]
    words = summary.words(found)
    return {"missing": missing, "forbidden": forbidden, "words": words,
            "passed": not missing and not forbidden and words <= expected["max_words"]}


def run_case(case: dict, bucket: str) -> dict:
    source = prepared(bucket, case["meeting_id"])
    with tempfile.TemporaryDirectory() as scratch:
        path = Path(scratch) / "work"
        shutil.copytree(source, path)
        work.write("people.json", {"people": []}, path)
        speakers = work.read("speakers.json", path)
        for s in speakers["speakers"].values():
            s["matches"] = []  # the cloud run matched against the people known then: context alone here
        work.write("speakers.json", speakers, path)
        started = datetime.now(UTC)
        name = f"{case['name']}-{started:%Y%m%dT%H%M%S}"
        try:
            run = harness.run(path, case.get("meta", {}), CACHE / "conversations" / f"{name}.json")
        except harness.RunFailed as e:
            return {"case": case["name"], "failed": str(e)}
        page = CACHE / "summaries" / f"{name}.md"  # to read: the grades can't say whether it's a good summary
        page.parent.mkdir(parents=True, exist_ok=True)
        page.write_text(summary.markdown(work.read("summary.json", path), work.read("speakers.json", path)["speakers"],
                                         case.get("meta", {}), work.read("manifest.json", path)["duration"]))
        return {"case": case["name"], "turns": run.turns, "tool_calls": run.tool_calls, "seconds": run.seconds,
                "speakers": grade_speakers(case, source, path), "summary": grade_summary(case, path), "page": str(page.relative_to(EVALS))}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("cases", nargs="*", help="case names (default: all)")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--bucket", default=settings.bucket)
    a = parser.parse_args()
    cases = [json.loads(p.read_text()) | {"name": p.stem} for p in sorted((EVALS / "cases").glob("*.json"))]
    cases = [c for c in cases if not a.cases or c["name"] in a.cases]
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    with (EVALS / "results.jsonl").open("a") as out:
        for case in cases:
            for n in range(a.runs):
                result = run_case(case, a.bucket) | {"run": n + 1, "commit": commit, "model": settings.model_id,
                                                     "at": datetime.now(UTC).isoformat(timespec="seconds")}
                out.write(json.dumps(result) + "\n")
                out.flush()
                speakers = {k: v for k, v in result.get("speakers", {}).items() if k != "names"}
                print(json.dumps(result | {"speakers": speakers}), file=sys.stderr)


if __name__ == "__main__":
    main()
