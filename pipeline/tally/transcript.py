"""Who said which word, and how much each speaker spoke, derived from the diarization's turns (diarization.json).

prepare, merge, split and retranscribe all go through here, so a speaker's words, segments and stats always agree
with its turns.
"""

import bisect
import math
from pathlib import Path

import numpy as np

from . import voiceprints, work


def assign(words: list[dict], turns: list[dict]) -> None:
    """Each word goes to the turn its middle falls in, or else the nearest turn (words between turns, or in speech
    the diarization missed)."""
    turns = sorted(turns, key=lambda t: t["start"])
    starts = [t["start"] for t in turns]
    for word in words:
        middle = (word["start"] + word["end"]) / 2
        i = bisect.bisect_right(starts, middle) - 1
        near = turns[max(0, i - 1):i + 2]
        word["speaker"] = min(near, key=lambda t: 0 if t["start"] <= middle <= t["end"] else min(abs(middle - t["start"]), abs(middle - t["end"])))["speaker"]


def segments(words: list[dict]) -> list[dict]:
    """Lines of the transcript: consecutive words of one speaker within one ASR segment."""
    found: list[dict] = []
    for word in words:
        last = found[-1] if found else None
        if last and last["speaker"] == word["speaker"] and last["_seg"] == word["seg"]:
            last["end"] = word["end"]
            last["text"] += " " + word["w"]
        else:
            found.append({"start": word["start"], "end": word["end"], "speaker": word["speaker"], "text": word["w"], "_seg": word["seg"]})
    return [{k: v for k, v in s.items() if k != "_seg"} for s in found]


def stats(turns: list[dict]) -> dict[str, dict]:
    """Talk time, turns (runs of a speaker's speech between others'), clean (non-overlapping) speech, and the
    mic/system energy ratio when the recording has the two apart."""
    found: dict[str, dict] = {}
    previous = None
    for turn in sorted(turns, key=lambda t: t["start"]):
        s = found.setdefault(turn["speaker"], {"talk_time": 0.0, "turns": 0, "clean_speech": 0.0, "first_seen": turn["start"], "_mic": 0.0, "_system": 0.0})
        s["talk_time"] += turn["end"] - turn["start"]
        s["clean_speech"] += turn["clean"]
        s["turns"] += turn["speaker"] != previous
        if turn.get("mic") is not None:
            s["_mic"] += turn["mic"]
            s["_system"] += turn["system"]
        previous = turn["speaker"]
    stereo = any(t.get("mic") is not None for t in turns)
    for s in found.values():
        mic, system = s.pop("_mic"), s.pop("_system")
        s["mic_ratio_db"] = round(10 * math.log10((mic + 1e-12) / (system + 1e-12)), 1) if stereo else None
        s["talk_time"], s["clean_speech"], s["first_seen"] = round(s["talk_time"], 1), round(s["clean_speech"], 1), round(s["first_seen"], 2)
    return found


def refresh(path: Path | None = None) -> dict:
    """speakers.json from the turns, the embeddings and the known people, keeping each speaker's identity."""
    turns = work.read("diarization.json", path)["turns"]
    embeddings = work.read_embeddings(path)
    people = work.people(path)
    before = work.read("speakers.json", path)["speakers"] if work.exists("speakers.json", path) else {}
    found = {}
    for speaker, s in sorted(stats(turns).items(), key=lambda item: item[1]["first_seen"]):
        embedding = embeddings.get(speaker)
        old = before.get(speaker, {})
        # Keep each person's best match, from before too: after prepare the folder has no voiceprints
        best = {}
        for m in (voiceprints.match(embedding, people) if embedding is not None else []) + old.get("matches", []):
            if m["person_id"] not in best or m["score"] > best[m["person_id"]]["score"]:
                best[m["person_id"]] = m
        found[speaker] = s | {
            "matches": sorted(best.values(), key=lambda m: -m["score"])[:voiceprints.TOP],
            "identity": old.get("identity"),
        }
    work.write("speakers.json", {"speakers": found}, path)
    return found


def rebuild(transcript: dict, turns: list[dict]) -> dict:
    assign(transcript["words"], turns)
    transcript["segments"] = segments(transcript["words"])
    return transcript


def normalised(vector: np.ndarray) -> np.ndarray:
    return (vector / np.linalg.norm(vector)).astype(np.float32)
