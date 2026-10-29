"""The voiceprint test: how well one meeting's speaker centroids pick out the same people in the others, and where
the cosine threshold should be (evals/README.md, step 3).

    cd pipeline && uv run python ../evals/voiceprints.py [--bucket <data bucket>]

It uses the cases' ground truth (`person`, the same name for the same person in every meeting) and each
meeting's prepared embeddings. People the meeting never names (names starting with "?") are left out, since
nobody knows whether they recur. For every threshold it reports, by talk time, what share of speakers is matched
to the right person, matched to a wrong one, or not matched, when the store holds every other meeting's
voiceprints. It does this twice: matching a person by their best voiceprint, and by the average of theirs.
"""

import argparse
import json
from pathlib import Path

import numpy as np
from run import prepared

from tally import work
from tally.config import settings

EVALS = Path(__file__).parent
THRESHOLDS = [round(t, 2) for t in np.arange(0.30, 0.91, 0.05)]
MIN_TALK = 10.0  # seconds: shorter clusters are fragments, left out of the test


def speakers(case: dict, bucket: str) -> list[dict]:
    path = prepared(bucket, case["meeting_id"])
    embeddings = work.read_embeddings(path)
    stats = work.read("speakers.json", path)["speakers"]
    return [{"meeting": case["name"], "speaker": k, "person": v.get("same_as", v["person"]), "talk": stats[k]["talk_time"], "embedding": embeddings[k]}
            for k, v in case["speakers"].items() if not v.get("same_as", v["person"]).startswith("?") and k in embeddings and stats[k]["talk_time"] >= MIN_TALK]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bucket", default=settings.bucket)
    a = parser.parse_args()
    cases = [json.loads(p.read_text()) | {"name": p.stem} for p in sorted((EVALS / "cases").glob("*.json"))]
    everyone = [s for c in cases for s in speakers(c, a.bucket)]

    same, different = [], []
    for i, x in enumerate(everyone):
        for y in everyone[i + 1:]:
            if x["meeting"] != y["meeting"]:
                (same if x["person"] == y["person"] else different).append(float(np.dot(x["embedding"], y["embedding"])))
    print(f"pairs across meetings: {len(same)} same person, {len(different)} different")
    print(f"  same person:      min {min(same):.3f}  median {np.median(same):.3f}  max {max(same):.3f}")
    print(f"  different people: min {min(different):.3f}  median {np.median(different):.3f}  max {max(different):.3f}")

    for how, score in (("best voiceprint", lambda prints, e: max(float(np.dot(p, e)) for p in prints)),
                       ("average of voiceprints", lambda prints, e: float(np.dot(np.mean(prints, axis=0) / np.linalg.norm(np.mean(prints, axis=0)), e)))):
        print(f"\nmatching by {how}: share of talk time (right / wrong / not matched)")
        for t in THRESHOLDS:
            right = wrong = none = total = 0.0
            for s in everyone:
                store: dict[str, list] = {}
                for o in everyone:
                    if o["meeting"] != s["meeting"]:
                        store.setdefault(o["person"], []).append(o["embedding"])
                scored = sorted(((score(prints, s["embedding"]), person) for person, prints in store.items()), reverse=True)
                best, person = scored[0]
                total += s["talk"]
                if best < t:
                    none += s["talk"]
                elif person == s["person"]:
                    right += s["talk"]
                else:
                    wrong += s["talk"]
            print(f"  {t:.2f}: {right / total:.2f} / {wrong / total:.2f} / {none / total:.2f}")


if __name__ == "__main__":
    main()
