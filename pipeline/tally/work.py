"""The meeting's working directory: every command reads and writes its files here (docs/data.md), so files are the
state and rerunning a command is the way to repair."""

import json
import os
import stat
from datetime import UTC, datetime
from pathlib import Path

import numpy as np

# The working files docs/data.md names: what's kept of a meeting's folder, and all a rewrite needs of it
FILES = {"manifest.json", "status.jsonl", "channels.json", "diarization.json", "transcript.json", "speakers.json",
         "embeddings.npz", "summary.json"}


def read(name: str, work: Path | None = None):
    path = (work or Path.cwd()) / name
    if not path.exists():
        raise FileNotFoundError(f"{name} is missing")
    with open_plain(path) as f:
        return json.loads(f.read())


MAX_READ = 64 * 1024 * 1024  # a meeting's JSON is a few MB at most


def open_plain(path: Path):
    """A file in the working directory, opened only if it's a plain file: the agent owns the directory, and root reads
    from it, so a link, a pipe or a device (which would block or never end) is refused."""
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    found = os.fstat(fd)
    if not stat.S_ISREG(found.st_mode) or found.st_size > MAX_READ:
        os.close(fd)
        raise ValueError(f"{path.name} isn't a plain file of a sensible size")
    return os.fdopen(fd, "rb")


def write(name: str, data, work: Path | None = None) -> None:
    path = (work or Path.cwd()) / name
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")  # renamed over it: a crash never leaves half a file
    temporary.unlink(missing_ok=True)  # never through a link someone left in its place
    # No NaN or infinity, which isn't JSON the apps can read: what Tally computes is finite, so one came from the agent
    temporary.write_text(json.dumps(data, indent=1, ensure_ascii=False, allow_nan=False))
    temporary.replace(path)


def people(work: Path | None = None) -> list[dict]:
    """The owner's people as people.json has them (none, when there's no file: an eval, a local run)."""
    return read("people.json", work)["people"] if exists("people.json", work) else []


def exists(name: str, work: Path | None = None) -> bool:
    return ((work or Path.cwd()) / name).exists()


def log(command: str, ok: bool, detail: str, work: Path | None = None) -> None:
    line = {"ts": datetime.now(UTC).isoformat(timespec="seconds"), "command": command, "ok": ok, "detail": detail}
    with ((work or Path.cwd()) / "status.jsonl").open("a") as f:
        f.write(json.dumps(line) + "\n")


def update_manifest(work: Path | None = None, **fields) -> None:
    manifest = read("manifest.json", work) if exists("manifest.json", work) else {}
    write("manifest.json", manifest | fields, work)


def read_embeddings(work: Path | None = None) -> dict[str, np.ndarray]:
    path = (work or Path.cwd()) / "embeddings.npz"
    if not path.exists():
        return {}
    with open_plain(path) as f, np.load(f) as found:
        return {key: found[key] for key in found.files}


def write_embeddings(embeddings: dict[str, np.ndarray], work: Path | None = None) -> None:
    path = (work or Path.cwd()) / "embeddings.npz"
    with path.open("wb") as f:
        np.savez(f, **embeddings)


def clock(seconds: float) -> str:
    seconds = int(seconds)
    return f"{seconds // 3600:02d}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


def seconds(text: str) -> float:
    """A time as the agent writes it: seconds (754.2), or a clock (00:12:34, 12:34)."""
    if ":" not in text:
        return float(text)
    total = 0.0
    for part in text.split(":"):
        total = total * 60 + float(part)
    return total
