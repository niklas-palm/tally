"""`tally`: one CLI with a subcommand per capability, run in the meeting's working directory.

The agent works through it with bash. stdout is always a small JSON envelope: `{"ok": true, ...}`, or on failure
`{"ok": false, "error", "type", "hint", "traceback"}` with exit code 1, and `main()` never crashes. Commands read the working
directory and write only their own outputs, so rerunning one is the way to repair. They report evidence, not
verdicts, and make no model calls. Every call appends a line to status.jsonl.
"""

import argparse
import json
import re
import sys
import traceback
from pathlib import Path

from . import check as checks
from . import summary as summaries
from . import transcript, voiceprints, work

EVIDENCE = ("voiceprint", "introduction", "address", "role", "elimination", "other")
CONFIDENCE = ("low", "medium", "high")
SHOW_LIMIT = 15_000  # characters of transcript per `show`: under the bash tool's 20k cap
GREP_LIMIT = 40
HINTS = {
    "FileNotFoundError": "a file the command needs is missing: is this the meeting's working directory, and has `tally prepare` run?",
    "KeyError": "a speaker or field doesn't exist: `tally speakers` lists the speakers",
    "ValueError": "an argument isn't what the command expects: read the error and `tally <command> --help`",
}


def speakers_file() -> dict:
    return work.read("speakers.json")["speakers"]


def known(speaker: str, speakers: dict) -> None:
    if speaker not in speakers:
        raise KeyError(f"there's no speaker {speaker}; the speakers are {', '.join(speakers)}")


def cmd_prepare(a):
    from . import prepare

    return prepare.run(Path(a.audio).resolve(), Path.cwd())


def cmd_speakers(a):
    """Each speaker's stats and voiceprint matches, and which other clusters in this meeting their voice is like:
    a tiny cluster that sounds like another is usually a fragment of that speaker."""
    speakers, embeddings = speakers_file(), work.read_embeddings()

    def alike(speaker: str) -> list[dict]:
        if speaker not in embeddings:
            return []
        scores = [(other, float(embeddings[speaker] @ vector)) for other, vector in embeddings.items() if other != speaker]
        return [{"speaker": other, "score": round(score, 2)} for other, score in sorted(scores, key=lambda s: -s[1]) if score >= voiceprints.NO_MATCH]

    return {"speakers": [{"speaker": k, **v, "sounds_like_here": alike(k)} for k, v in speakers.items()]}


def cmd_show(a):
    found = work.read("transcript.json")["segments"]
    names = {k: v["identity"]["name"] for k, v in speakers_file().items() if v["identity"] and "name" in v["identity"]}
    start = work.seconds(a.start) if a.start else 0.0
    end = work.seconds(a.end) if a.end else float("inf")
    lines, size, more = [], 0, None
    for s in found:
        if s["end"] < start or s["start"] > end or (a.speaker and s["speaker"] != a.speaker):
            continue
        who = f"{s['speaker']} ({names[s['speaker']]})" if s["speaker"] in names else s["speaker"]
        line = f"[{work.clock(s['start'])}] {who}: {s['text']}"
        if size + len(line) > SHOW_LIMIT:
            more = s["start"]
            break
        lines.append(line)
        size += len(line) + 1
    return {"transcript": lines, **({"more_from": work.clock(more)} if more is not None else {})}


def cmd_grep(a):
    pattern = re.compile(a.pattern, re.IGNORECASE)
    hits = [s for s in work.read("transcript.json")["segments"] if pattern.search(s["text"])]
    return {"count": len(hits), "transcript": [f"[{work.clock(s['start'])}] {s['speaker']}: {s['text']}" for s in hits[:GREP_LIMIT]],
            **({"note": f"showing the first {GREP_LIMIT}; narrow the pattern"} if len(hits) > GREP_LIMIT else {})}


def evidence(items: list[str]) -> list[dict]:
    found = []
    for item in items:
        kind, _, text = item.partition(":")
        if kind.strip() not in EVIDENCE or not text.strip():
            raise ValueError(f"evidence {item!r} must be '<kind>: <what and where>', with kind one of {', '.join(EVIDENCE)}")
        found.append({"kind": kind.strip(), "text": text.strip()})
    return found


def cmd_name(a):
    speakers = speakers_file()
    known(a.speaker, speakers)
    name, role, company = one_line(a.person), a.role and one_line(a.role), a.company and one_line(a.company)  # an empty role is none
    people = work.people()
    wanted = name.casefold()
    person = next((p for p in people if wanted in [p["name"].casefold(), *(x.casefold() for x in p["aliases"])]), None)
    speakers[a.speaker]["identity"] = {
        "name": person["name"] if person else name, "person_id": person["person_id"] if person else None,
        "confidence": a.confidence, "evidence": evidence(a.evidence), "by": "agent",
        **{field: value for field, value in (("role", role), ("company", company)) if value}}
    work.write("speakers.json", {"speakers": speakers})
    named = speakers[a.speaker]["identity"]["name"].casefold()
    same = [k for k, v in speakers.items() if k != a.speaker and v["identity"] and v["identity"].get("name", "").casefold() == named]
    return {"speaker": a.speaker, "identity": speakers[a.speaker]["identity"], "known_person": person is not None,
            **({"note": f"{', '.join(same)} also named {name}: if they're one person, `tally merge`"} if same else {})}


def one_line(value: str) -> str:
    """What the agent names someone, or their role or company: the rule the API holds the owner to (voiceprints.line)."""
    if not (kept := voiceprints.line(value)):
        raise ValueError(f"a name, role or company is one line of at most {voiceprints.LINE} characters")
    return kept


def cmd_unknown(a):
    speakers = speakers_file()
    known(a.speaker, speakers)
    speakers[a.speaker]["identity"] = {"unknown": True, "reason": a.reason, "by": "agent"}
    work.write("speakers.json", {"speakers": speakers})
    return {"speaker": a.speaker, "identity": speakers[a.speaker]["identity"]}


def cmd_merge(a):
    """`other` joins `into`: its turns, words and talk time; their centroids are averaged by talk time."""
    speakers = speakers_file()
    known(a.into, speakers)
    known(a.other, speakers)
    if a.into == a.other:
        raise ValueError("a speaker can't merge with itself")
    diarization = work.read("diarization.json")
    for turn in diarization["turns"]:
        if turn["speaker"] == a.other:
            turn["speaker"] = a.into
    work.write("diarization.json", diarization)
    embeddings = work.read_embeddings()
    if a.into in embeddings and a.other in embeddings:
        weights = speakers[a.into]["talk_time"], speakers[a.other]["talk_time"]
        embeddings[a.into] = transcript.normalised(weights[0] * embeddings[a.into] + weights[1] * embeddings[a.other])
    elif a.other in embeddings:
        embeddings[a.into] = embeddings[a.other]
    embeddings.pop(a.other, None)
    work.write_embeddings(embeddings)
    if speakers[a.into]["identity"] is None:
        speakers[a.into]["identity"] = speakers[a.other]["identity"]
    speakers[a.into]["matches"] += speakers[a.other]["matches"]  # one voice now: each person's best of either, in refresh
    del speakers[a.other]
    work.write("speakers.json", {"speakers": speakers})
    work.write("transcript.json", transcript.rebuild(work.read("transcript.json"), diarization["turns"]))
    found = transcript.refresh()
    return {"merged": a.other, "into": a.into, "speaker": {"speaker": a.into, **found[a.into]}}


def cmd_split(a):
    """From --at on, the speaker's turns belong to a new speaker, with no embedding of its own."""
    speakers = speakers_file()
    known(a.speaker, speakers)
    at = work.seconds(a.at)
    diarization = work.read("diarization.json")
    new = f"S{max(int(k[1:]) for k in speakers) + 1}"
    moved = 0
    for turn in diarization["turns"]:
        if turn["speaker"] == a.speaker and turn["start"] >= at:
            turn["speaker"] = new
            moved += 1
    if not moved or all(t["speaker"] != a.speaker for t in diarization["turns"]):
        raise ValueError(f"{a.speaker} has no turns on both sides of {a.at}: nothing to split")
    work.write("diarization.json", diarization)
    work.write("transcript.json", transcript.rebuild(work.read("transcript.json"), diarization["turns"]))
    found = transcript.refresh()
    return {"split": a.speaker, "new": new, "speakers": {k: found[k] for k in (a.speaker, new)}}


def cmd_retranscribe(a):
    from . import prepare

    return prepare.retranscribe(a.language, Path.cwd())


def cmd_write_summary(a):
    found = json.loads(Path(a.file).read_text())
    summaries.validate(found, speakers_file(), work.people())
    work.write("summary.json", found)
    work.update_manifest(step="summarised")
    return {"stored": "summary.json", "words": summaries.words(found)}


def cmd_check(a):
    missing = checks.missing(Path.cwd())
    return {"done": not missing, "missing": missing}


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="tally", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    def command(name, fn, help):
        sp = sub.add_parser(name, help=help, description=help)
        sp.set_defaults(fn=fn)
        return sp

    sp = command("prepare", cmd_prepare, "Decode, detect the language, transcribe, diarize, match voiceprints and cut clips (run by the job before the agent).")
    sp.add_argument("audio")
    command("speakers", cmd_speakers, "Every speaker: talk time, turns, clean speech, mic/system ratio, voiceprint matches, identity, and which other speakers here sound alike (sounds_like_here).")
    sp = command("show", cmd_show, f"Transcript lines with times and speakers, at most {SHOW_LIMIT} characters; continue from `more_from`.")
    sp.add_argument("--from", dest="start", help="seconds or hh:mm:ss")
    sp.add_argument("--to", dest="end", help="seconds or hh:mm:ss")
    sp.add_argument("--speaker", help="only this speaker, e.g. S2")
    sp = command("grep", cmd_grep, "Where something was said: lines matching a case-insensitive regular expression.")
    sp.add_argument("pattern")
    sp = command("name", cmd_name, "Record who a speaker is, with your confidence and the evidence.")
    sp.add_argument("speaker")
    sp.add_argument("--person", required=True, help="the person's name, as known people list it or as they're called in the meeting")
    sp.add_argument("--confidence", required=True, choices=CONFIDENCE)
    sp.add_argument("--evidence", required=True, action="append", help=f"'<kind>: <what and where>', kind one of {', '.join(EVIDENCE)}; repeat for each")
    sp.add_argument("--role", help="what they do, only as the meeting says it, e.g. 'Head of finance'")
    sp.add_argument("--company", help="where they work, only as the meeting says it")
    sp = command("unknown", cmd_unknown, "Record that a speaker can't be identified, and why.")
    sp.add_argument("speaker")
    sp.add_argument("--reason", required=True)
    sp = command("merge", cmd_merge, "Two clusters are one person: the second joins the first.")
    sp.add_argument("into")
    sp.add_argument("other")
    sp = command("split", cmd_split, "One cluster is two people: from --at on, its turns become a new speaker.")
    sp.add_argument("speaker")
    sp.add_argument("--at", required=True, help="seconds or hh:mm:ss")
    sp = command("retranscribe", cmd_retranscribe, "Transcribe again in the other language, keeping the speakers.")
    sp.add_argument("--language", required=True, choices=["en", "sv"])
    sp = command("write-summary", cmd_write_summary, "Validate a summary JSON file and store it as summary.json.")
    sp.add_argument("file")
    command("check", cmd_check, "What is still missing before the meeting can be published.")
    return p


def main(argv=None) -> None:
    a = build_parser().parse_args(argv)
    try:
        result = {"ok": True, **a.fn(a)}
    except Exception as e:  # the whole point is to report, not crash
        result = {"ok": False, "error": str(e), "type": type(e).__name__,
                  "hint": HINTS.get(type(e).__name__, "read the error, fix the input, and run the command again"),
                  "traceback": traceback.format_exc()[-1200:]}
    if work.exists("manifest.json"):  # in a meeting's working directory
        work.log(a.cmd, result["ok"], result.get("error", ""))
    print(json.dumps(result, indent=1, ensure_ascii=False, default=float))
    if not result["ok"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
