"""`tally check`: what is still missing before a meeting can be published. The harness calls it after every agent
turn in its own process, with code loaded before the agent started, so nothing the agent edits can change what
"done" means."""

from pathlib import Path

from . import summary, work


def missing(path: Path) -> list[str]:
    speakers = work.read("speakers.json", path)["speakers"]
    found = [f"{k} has no identity: `tally name` or `tally unknown`" for k, v in speakers.items() if v["identity"] is None]
    if not work.exists("summary.json", path):
        found.append("there's no summary: write one and store it with `tally write-summary`")
    else:
        try:
            summary.validate(work.read("summary.json", path), speakers, work.people(path))
        except ValueError as e:  # a merge or split since it was written
            found.append(f"the summary no longer fits the speakers ({e}): write it again")
    return found
