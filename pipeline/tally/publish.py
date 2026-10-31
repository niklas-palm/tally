"""Publishing a finished meeting: the people and voiceprints it adds, results/meeting.json (the results, which the
app gets through the API), results/summary.md, the summary as a page to read, and the speakers' clips, cut from
root's own copy of the audio.

The harness publishes, never the agent. Here the hard rule on voiceprints applies: a speaker the agent named
gives its person an `auto` voiceprint only with high confidence, two independent kinds of evidence, and 30 s of
clean speech. Each meeting adds at most one voiceprint per speaker, its centroid over the whole meeting, so a
person collects one more with every meeting they're named in.
"""

import json
import os
import re
import time
from pathlib import Path

from . import prepare, summary, voiceprints, work
from .cli import EVIDENCE
from .voiceprints import Store, sound

CLEAN_SPEECH = 30.0  # seconds a speaker needs for an auto-saved voiceprint
CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
RESULTS_BYTES = 4_000_000  # a meeting.json the API can answer with (Lambda's 6 MB), with room for links; an hour's is about 200 KB


def ulid() -> str:
    """A ULID: sortable by time, like meeting ids."""
    value = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10))
    return "".join(CROCKFORD[(value >> shift) & 31] for shift in range(125, -1, -5))


def may_save_voiceprint(speaker: dict) -> bool:
    """The agent may write speakers.json itself: only kinds the CLI knows count, since a voiceprint is matched in later
    meetings, and with shared voices in colleagues' too."""
    identity = speaker["identity"]
    evidence = identity.get("evidence")
    kinds = {e["kind"] for e in evidence if isinstance(e, dict) and e.get("kind") in EVIDENCE} if isinstance(evidence, list) else set()
    return identity.get("confidence") == "high" and len(kinds) >= 2 and speaker["clean_speech"] >= CLEAN_SPEECH


def suggestion(person_id: str, people: list[dict]) -> bool:
    """A known person the owner has never confirmed (only `auto` voiceprints) is shown as a suggestion."""
    person = next((p for p in people if p["person_id"] == person_id), None)
    return bool(person and person["voiceprints"] and all(v["source"] == "auto" for v in person["voiceprints"]))


def refuse_too_big(meeting: dict) -> None:
    """A meeting.json the API can answer with: the agent wrote the files it's made of, as big as it likes."""
    if len(json.dumps(meeting)) > RESULTS_BYTES:
        raise ValueError("results/meeting.json would be more than the API can answer with, so no app could open it")


def publish(root: Path, meeting_id: str, meta: dict, store: Store) -> dict:
    path = root / "work"
    speakers = work.read("speakers.json", path)["speakers"]
    if any(not re.fullmatch(r"S\d+", speaker) for speaker in speakers):  # they become S3 keys and paths, and rows
        raise ValueError("speakers.json has a speaker id that prepare and the CLI don't make")
    embeddings = work.read_embeddings(path)
    people = work.people(path)
    # The owner's people as the tables hold them now, with their voiceprints: a meeting processed at the same time
    # may have created a person since people.json was written, and only the tables say whose voice is confirmed.
    known = store.load()
    now = {name.casefold(): p["person_id"] for p in known for name in [p["name"], *p["aliases"]]}
    turns = work.read("diarization.json", path)["turns"]
    store.drop_voiceprints(meeting_id)  # a Batch retry of this job publishes again: its first try's go
    created: dict[str, str] = {}
    saved = []
    for speaker, s in speakers.items():
        identity = s["identity"]
        if identity.get("unknown"):  # no person_id: the people a meeting's row names are held on to in People
            s["identity"] = {"unknown": True, "reason": identity.get("reason"), "by": "agent"}
            continue
        pooled = next((p for p in people if p["person_id"] == identity["person_id"] and p.get("shared")), None)
        if pooled:  # someone from the shared pool: the owner's own person now, found or made by name
            identity |= {"person_id": None, "name": pooled["name"]}
        elif identity["person_id"] is not None and identity["person_id"] not in now.values():
            identity["person_id"] = None  # deleted in People while the job ran: found or made again by name
        # The agent may write speakers.json and people.json itself: what's named is held to the CLI's rule here too
        if not (name := voiceprints.line(identity.get("name") or "")):
            s["identity"] = {"unknown": True, "reason": "The name the agent gave isn't one line", "by": "agent"}
            continue
        # As the agent named them, only what a named speaker is: set aside or unknown is the owner's word, never the agent's
        s["identity"] = identity = {k: v for k, v in identity.items() if k in ("person_id", "role", "company", "confidence", "evidence")} | {
            "name": name, "by": "agent"}
        for field in ("role", "company"):
            if value := voiceprints.line(identity.get(field) or ""):
                identity[field] = value
            else:
                identity.pop(field, None)
        new = False
        if identity["person_id"] is None:
            key = identity["name"].casefold()
            if key in now:
                identity["person_id"] = now[key]
            else:
                if key not in created:
                    created[key] = f"p_{ulid()}"
                    store.put_person(created[key], identity["name"], meeting_id, identity.get("role"), identity.get("company"))
                identity["person_id"] = created[key]
                new = True
        if not new:  # someone known: what this meeting says they do fills only what's still empty
            store.fill_person(identity["person_id"], identity.get("role"), identity.get("company"))
        identity["suggestion"] = bool(pooled) or suggestion(identity["person_id"], known)  # a pooled voice: theirs to confirm
        if pooled:
            identity["shared"] = True  # a colleague's naming: the apps say so beside "Is this …?"
        # A colleague's naming is the owner's to confirm: an `auto` voiceprint from it would spread a mistake
        if not pooled and speaker in embeddings and sound(embeddings[speaker]) and may_save_voiceprint(s):
            end = max(t["end"] for t in turns if t["speaker"] == speaker)
            store.put_voiceprint(identity["person_id"], f"v_{ulid()}", embeddings[speaker], "auto", meeting_id, speaker, s["first_seen"], end, s["clean_speech"])
            saved.append(speaker)
    work.write("speakers.json", {"speakers": speakers}, path)

    # Clips cut again from root's own audio, only for the checked speakers (a turn's speaker becomes a path), never taken from the agent's folder
    prepare.clips(prepare.read_wav(root / "audio16k.wav"), [t for t in turns if t["speaker"] in speakers], root / "results")
    clips = {speaker: [f"results/clips/{speaker}/{c.name}" for c in sorted((root / "results" / "clips" / speaker).glob("*.m4a"))]
             for speaker in speakers}
    manifest = work.read("manifest.json", path)
    meeting = {
        # the meeting's row takes the language, and every meeting list shows it
        "meeting_id": meeting_id, "meta": meta, "language": manifest.get("language") if manifest.get("language") in prepare.ASR else None,
        "duration": manifest.get("duration"), "layout": manifest.get("layout"), "summary": work.read("summary.json", path),
        "speakers": {k: {"talk_time": v.get("talk_time"), "turns": v.get("turns"), "mic_ratio_db": v.get("mic_ratio_db"),
                         "identity": v["identity"], "clips": clips[k]} for k, v in speakers.items()},
        "segments": work.read("transcript.json", path)["segments"],
    }
    refuse_too_big(meeting)
    work.write("results/meeting.json", meeting, root)
    (root / "results" / "summary.md").write_text(summary.markdown(meeting["summary"], speakers, meta, meeting["duration"]))
    work.update_manifest(path, step="published")
    return {"people_created": len(created), "voiceprints_saved": saved}
