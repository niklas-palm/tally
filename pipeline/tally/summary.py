"""The summary: its shape, the rules `tally write-summary` holds it to, and the page it renders as (results/summary.md).
Whether it's a good summary is for the evals.
"""

import re
from datetime import date

from . import work

TOKEN = re.compile(r"\{(S\d+)\}")
TITLE_CHARACTERS = 80
OVERVIEW_WORDS = 60
SUMMARY_POINTS = (1, 7)  # a short or informal meeting needs only one or two
POINT_WORDS = 35
TOPIC_NOTES = 10  # a detailed technical topic needs its numbers and specifics
TOTAL_WORDS = 1500
TOKENS = 20  # speakers one text names ({S2})
CHARACTERS = 2000  # in any one text: words are counted, and a "word" of millions of characters is one


LIST = {"decisions": {"text": str, "by": list}, "action_items": {"owner": (str, type(None)), "text": str, "due": (str, type(None))},
        "open_questions": {"text": str, "raised_by": list},
        "topics": {"title": str, "start": (int, float), "end": (int, float), "notes": list, "outcome": (str, type(None))},
        "attendees": {"speaker": str, "role": str, "contribution": str}}


def a_day(text: str) -> bool:
    """YYYY-MM-DD this century, all the apps' calendars read (`fromisoformat` alone also takes 20261107 and 2026-W45-1)."""
    try:
        return bool(re.fullmatch(r"\d{4}-\d{2}-\d{2}", text)) and 2000 <= date.fromisoformat(text).year <= 2100
    except ValueError:
        return False


def texts(found: dict) -> list[str]:
    topics = [t for topic in found["topics"] for t in [topic["title"], *topic["notes"], topic["outcome"] or ""]]
    return ([found["title"], found["overview"], *found["summary"]] + [d["text"] for d in found["decisions"]]
            + [i["text"] for i in found["action_items"]] + [q["text"] for q in found["open_questions"]] + topics
            + [t for a in found["attendees"] for t in (a["role"], a["contribution"])])


def words(found: dict) -> int:
    return sum(len(t.split()) for t in texts(found))


def validate(found: dict, speakers: dict, people: list[dict]) -> None:
    """Raises ValueError listing every rule broken. `people` gives the aliases, which mustn't be written out either."""
    problems = shape(found)
    if problems:
        raise ValueError("; ".join(problems))
    every = texts(found)
    problems += [f"a text of {len(t)} characters: at most {CHARACTERS}" for t in every if len(t) > CHARACTERS]
    # search fills in a name of up to 100 characters for each {S2}
    problems += [f"a text names speakers {n} times: at most {TOKENS}" for t in every if (n := len(TOKEN.findall(t))) > TOKENS]
    ids = set(speakers)
    for token in sorted({t for text in every for t in TOKEN.findall(text)} - ids):
        problems.append(f"{{{token}}} isn't a speaker")
    for key, field in (("decisions", "by"), ("open_questions", "raised_by")):
        for item in found[key]:
            problems += [f"{key}: {s} isn't a speaker" for s in item[field] if s not in ids]
    for item in found["action_items"]:
        if item["owner"] is not None and item["owner"] not in ids:
            problems.append(f"action item owner {item['owner']} isn't a speaker")
        if item["due"] is not None and not a_day(item["due"]):
            problems.append(f"due {item['due']!r} isn't a date (YYYY-MM-DD) this century, or null when none was said")
    listed = [a["speaker"] for a in found["attendees"]]
    problems += [f"attendee {s} isn't a speaker" for s in listed if s not in ids]
    problems += [f"{s} is listed twice in attendees" for s in sorted({s for s in listed if listed.count(s) > 1})]
    named = [k for k, s in speakers.items() if (s["identity"] or {}).get("name")]
    problems += [f"{s} is named but missing from attendees" for s in named if s not in listed]
    aliases = {p["person_id"]: p.get("aliases", []) for p in people}
    for speaker, s in speakers.items():
        identity = s["identity"] or {}
        names = [n for n in [identity.get("name"), *aliases.get(identity.get("person_id"), [])] if n]
        # the surname too: "Warren voted for" goes stale when Warren is renamed, just as the full name does
        for part in {part for name in names for part in (name, name.split()[-1])}:
            if len(part) >= 3 and any(re.search(rf"\b{re.escape(part)}\b", text) for text in every):
                problems.append(f"{part!r} is written out: write {{{speaker}}} instead, so a rename needs no reprocessing")
    if len(found["title"]) > TITLE_CHARACTERS:
        problems.append(f"the title is over {TITLE_CHARACTERS} characters")
    if TOKEN.search(found["title"]):  # the meeting list, notifications and folder names show a title as it is
        problems.append("the title names no speaker, not even as a {S2}: it says what the meeting was about")
    if len(found["overview"].split()) > OVERVIEW_WORDS:
        problems.append(f"`overview` is {len(found['overview'].split())} words: at most {OVERVIEW_WORDS}")
    if not SUMMARY_POINTS[0] <= len(found["summary"]) <= SUMMARY_POINTS[1]:
        problems.append(f"`summary` has {len(found['summary'])} points: {SUMMARY_POINTS[0]} to {SUMMARY_POINTS[1]}")
    problems += [f"summary point {n + 1} is over {POINT_WORDS} words" for n, p in enumerate(found["summary"]) if len(p.split()) > POINT_WORDS]
    problems += [f"topic {t['title']!r} has {len(t['notes'])} notes: at most {TOPIC_NOTES}" for t in found["topics"] if len(t["notes"]) > TOPIC_NOTES]
    if (total := words(found)) > TOTAL_WORDS:
        problems.append(f"the whole summary is {total} words: at most {TOTAL_WORDS}; cut what doesn't matter")
    if problems:
        raise ValueError("; ".join(problems))


def shape(found: dict) -> list[str]:
    expected = {"title": str, "overview": str, "summary": list, **{k: list for k in LIST}}
    problems = [f"`{k}` must be a {t.__name__}" for k, t in expected.items() if not isinstance(found.get(k), t)]
    if problems:
        return problems
    problems += [f"summary[{n}] must be a string" for n, p in enumerate(found["summary"]) if not isinstance(p, str)]
    for key, fields in LIST.items():
        for n, item in enumerate(found[key]):
            for field, kind in fields.items():
                if not isinstance(item, dict) or field not in item:
                    problems.append(f"{key}[{n}].{field} is missing")
                elif not isinstance(item[field], kind):
                    problems.append(f"{key}[{n}].{field} is the wrong type")
    problems += [f"{key}[{n}].{field} must be strings" for key, field in (("decisions", "by"), ("open_questions", "raised_by"), ("topics", "notes"))
                 for n, i in enumerate(found[key]) if isinstance(i, dict) and isinstance(i.get(field), list) and not all(isinstance(x, str) for x in i[field])]
    return problems


def markdown(found: dict, speakers: dict, meta: dict, duration: float) -> str:
    """The summary as the owner reads it, with the speakers' current names; unnamed speakers are "Speaker 3"."""
    def name(speaker: str) -> str:
        identity = speakers.get(speaker, {}).get("identity") or {}
        return identity.get("name") or f"Speaker {speaker[1:]}"

    def fill(text: str) -> str:
        return TOKEN.sub(lambda m: name(m[1]), text)

    def names(ids: list[str]) -> str:
        return ", ".join(name(s) for s in ids)

    hours, minutes = divmod(max(1, round(duration / 60)), 60)
    length = f"{hours} h {minutes} min" if hours else f"{minutes} min"
    when = " · ".join(x for x in [(meta.get("started_at") or "")[:10], length] if x)
    lines = [f"# {fill(found['title'])}", "", f"{when} · with {names([a['speaker'] for a in found['attendees']])}", "",
             f"**Overview.** {fill(found['overview'])}", "", "## Summary", "", *[f"- {fill(p)}" for p in found["summary"]]]
    if found["decisions"]:
        lines += ["", "## Decisions", "", *[f"- {fill(d['text'])}" + (f" *({names(d['by'])})*" if d["by"] else "") for d in found["decisions"]]]
    if found["action_items"]:
        lines += ["", "## Action items", "", *[f"- [ ] **{name(i['owner']) if i['owner'] else 'Unassigned'}**: {fill(i['text'])}"
                                               + (f" (due {i['due']})" if i["due"] else "") for i in found["action_items"]]]
    if found["open_questions"]:
        lines += ["", "## Open questions", "", *[f"- {fill(q['text'])}" + (f" *({names(q['raised_by'])})*" if q["raised_by"] else "")
                                                 for q in found["open_questions"]]]
    if found["topics"]:
        lines += ["", "## Topics"]
    for t in found["topics"]:
        lines += ["", f"### {fill(t['title'])} ({work.clock(t['start'])}–{work.clock(t['end'])})", "", *[f"- {fill(n)}" for n in t["notes"]]]
        if t["outcome"]:
            lines += ["", f"**Outcome:** {fill(t['outcome'])}"]
    lines += ["", "## Attendees", "", *[f"- **{name(a['speaker'])}**, {fill(a['role'])}: {fill(a['contribution'])}" for a in found["attendees"]]]
    return "\n".join(lines) + "\n"
