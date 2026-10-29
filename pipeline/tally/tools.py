"""The agent's four tools, and that's the whole list: bash, read_file, write_file, edit_file.

Every capability lives in the `tally` CLI, which the agent runs through bash. The file tools shape output for the
context window and refuse to change a file the agent hasn't read. Tools never raise: a failure comes back as an
error result that says what to do. Output is capped, because one huge result poisons every later call. Every path
stays inside the meeting's working directory. On Batch the shell runs as the unprivileged `agent` user, in its own
group, with no AWS credentials and no network: the agent reads what anyone said in a meeting. The file
tools work with the shell's rights too (`shell_rights`), and whatever the shell leaves running is killed after each
call, so the root process never follows a path the agent controls.
"""

import functools
import os
import pwd
import re
import shutil
import signal
import subprocess
import sys
from pathlib import Path

from strands import tool

LINES = 2000
LINE_CHARACTERS = 2000
READ_CHARACTERS = 100_000
OUTPUT_CHARACTERS = 20_000  # per stream
TIMEOUT, MAX_TIMEOUT = 120, 900
AGENT = "agent"  # the image's unprivileged user, for the shell (Dockerfile)

root = Path.cwd()  # the meeting's working directory: set by use()
seen: set[Path] = set()  # files read or written in this run


def use(path: Path) -> None:
    global root
    root = path.resolve()
    seen.clear()
    if shell_user():  # the shell's user works in the meeting's directory, and nowhere else
        shutil.chown(root, AGENT)
        for file in root.rglob("*"):
            shutil.chown(file, AGENT)


def shell_user() -> str | None:
    """The image's unprivileged user, when the job runs as root (on Batch): the agent's shell runs as it, so it can't
    read the job's credentials from the main process. Locally (tests, evals) the shell is the current user."""
    return AGENT if os.getuid() == 0 else None


# Exits 0 if the shell reaches anything, the instance's metadata service or DNS (a way out too), and 2 if it reaches
# nothing: anything else (a crash) counts as reaching.
PROBE = """
import socket, sys
for reach in (lambda: socket.create_connection(("169.254.169.254", 80), timeout=3),
              lambda: socket.getaddrinfo("amazonaws.com", 443),
              lambda: socket.create_connection(("127.0.0.53", 53), timeout=3)):
    try:
        reach()
        sys.exit(0)
    except OSError:
        pass
sys.exit(2)
"""


def shell_is_offline() -> bool:
    """Whether the agent's shell reaches nothing on the network: on Batch, the instance's firewall rejects the agent's
    uid (Batch runs jobs with host networking, so the metadata service and the DNS stub would be in reach)."""
    return subprocess.run([sys.executable, "-c", PROBE], capture_output=True, timeout=30, **as_shell()).returncode == 2


def as_shell() -> dict:
    """What a subprocess needs to run as the shell's user, its group too (not root's): nothing locally."""
    return {"user": AGENT, "group": AGENT, "extra_groups": []} if shell_user() else {}


def shell_rights(fn):
    """Runs a file tool with the shell's rights. The agent owns work/, and a process it leaves running could swap a
    directory for a link at any moment: the root process must never be the one following its paths."""
    @functools.wraps(fn)
    def wrapped(*args, **kwargs):
        if not shell_user():
            return fn(*args, **kwargs)
        user, groups = pwd.getpwnam(AGENT), os.getgroups()
        os.setgroups([])
        os.setegid(user.pw_gid)
        os.seteuid(user.pw_uid)
        try:
            return fn(*args, **kwargs)
        finally:
            os.seteuid(0)
            os.setegid(0)
            os.setgroups(groups)
    return wrapped


def stop_shell_processes() -> None:
    """Kills whatever the agent's shell left running (`cmd &`): nothing of the agent's may outlive its call and race
    what the root process does next, its other tools or publishing."""
    uid = pwd.getpwnam(AGENT).pw_uid
    for _ in range(100):  # again until none is left: one may fork while the last pass ran, a killed one can't
        found = False
        for pid in filter(str.isdigit, os.listdir("/proc")):
            try:
                if re.search(rf"^Uid:\s+{uid}\s", Path(f"/proc/{pid}/status").read_text(), re.M):
                    os.kill(int(pid), signal.SIGKILL)
                    found = True
            except OSError:
                pass  # gone already
        if not found:
            return
    raise RuntimeError("the agent's shell keeps starting processes")


def result(text: str, status: str = "success") -> dict:
    return {"status": status, "content": [{"text": text}]}


def error(text: str) -> dict:
    return result(text, "error")


def inside(path: str) -> Path:
    found = (root / path).resolve()
    if found != root and root not in found.parents:
        raise ValueError(f"{path} is outside the meeting's directory; stay inside {root}")
    return found


def cap(text: str) -> str:
    return text if len(text) <= OUTPUT_CHARACTERS else text[:OUTPUT_CHARACTERS] + "\n... [cut: redirect to a file and read it with read_file]"


@tool
def bash(command: str, timeout: int = TIMEOUT) -> dict:
    """Run a bash command in the meeting's working directory: above all the `tally` CLI.

    Each call is a fresh shell, so cd and variables don't carry over. stdout and stderr are capped at 20,000
    characters each; redirect longer output to a file and read it with read_file. Use read_file, write_file and
    edit_file for files, not cat, echo or sed.

    Args:
        command: The command to run.
        timeout: Seconds before it's killed (default 120, at most 900).
    """
    # No AWS credentials in the shell: the agent reads what anyone said in a meeting, and its tools need only the
    # meeting's files. The harness, not the shell, calls the model.
    env = {k: v for k, v in os.environ.items() if not k.startswith(("AWS_", "ECS_"))}
    env |= {"PATH": f"{Path(sys.executable).parent}:{os.environ.get('PATH', '')}", "HOME": str(root)}
    process = subprocess.Popen(["bash", "-c", command], cwd=root, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, start_new_session=True, **as_shell())  # a group of its own: a timeout kills all of it
    try:
        stdout, stderr = process.communicate(timeout=min(max(timeout, 1), MAX_TIMEOUT))
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        if shell_user():
            stop_shell_processes()  # something in a session of its own may still hold the pipes
        stdout, stderr = process.communicate(timeout=10)
        return error(f"killed after {timeout} s: do less per call\n{cap(stdout)}\n{cap(stderr)}")
    finally:
        if shell_user():
            stop_shell_processes()
    text = cap(stdout) + (f"\n[stderr]\n{cap(stderr)}" if stderr.strip() else "") + (f"\n[exit code {process.returncode}]" if process.returncode else "")
    return result(text.strip() or "(no output)", "success" if process.returncode == 0 else "error")


@tool
@shell_rights
def read_file(path: str, offset: int = 1, limit: int = LINES) -> dict:
    """Read a text file in the meeting's directory, with line numbers (the number, a tab, then the line).

    Up to 2,000 lines from offset, and at most 100,000 characters per call: the result says where to continue.
    Lines over 2,000 characters are cut. Don't read a file again to check an edit: edit_file fails loudly.

    Args:
        path: The file, relative to the meeting's directory.
        offset: The first line, counting from 1.
        limit: How many lines.
    """
    try:
        found = inside(path)
        data = found.read_bytes()
    except (OSError, ValueError) as e:
        return error(f"{e}; list files with bash (ls)")
    if b"\0" in data[:8000]:
        return error(f"{path} is a binary file")
    seen.add(found)
    lines = data.decode(errors="replace").split("\n")
    out, size = [], 0
    for n, line in enumerate(lines[offset - 1:offset - 1 + limit], offset):
        line = f"{n}\t{line[:LINE_CHARACTERS]}"
        if size + len(line) > READ_CHARACTERS and out:
            out.append(f"[stopped at {READ_CHARACTERS} characters: continue with offset {n}]")
            break
        out.append(line)
        size += len(line) + 1
    return result("\n".join(out) or f"{path} has no lines from {offset} on ({len(lines)} lines)")


@tool
@shell_rights
def write_file(path: str, content: str) -> dict:
    """Write a file in the meeting's directory: a new one, or one you have read (it's replaced whole). For a
    draft summary, say, before `tally write-summary`.

    Args:
        path: The file, relative to the meeting's directory.
        content: The whole content.
    """
    try:
        found = inside(path)
    except ValueError as e:
        return error(str(e))
    if found.exists() and found not in seen:
        return error(f"{path} exists and you haven't read it: read_file it first, or use edit_file")
    try:
        found.parent.mkdir(parents=True, exist_ok=True)
        found.write_text(content)
    except (OSError, ValueError) as e:
        return error(f"couldn't write {path}: {e}")
    seen.add(found)
    return result(f"wrote {path} ({len(content)} characters)")


@tool
@shell_rights
def edit_file(path: str, old_text: str, new_text: str, replace_all: bool = False) -> dict:
    """Replace exact text in a file you have read. old_text must appear exactly once, unless replace_all.

    Args:
        path: The file, relative to the meeting's directory.
        old_text: The exact text to replace, without read_file's line numbers.
        new_text: What replaces it.
        replace_all: Replace every occurrence.
    """
    try:
        found = inside(path)
    except ValueError as e:
        return error(str(e))
    if found not in seen:
        return error(f"you haven't read {path}: read_file it first")
    try:
        text = found.read_text()
    except (OSError, ValueError) as e:  # not text (read_file showed it with replacement characters), or gone
        return error(f"couldn't edit {path} as text: {e}")
    count = text.count(old_text)
    if not old_text or count == 0:
        return error("old_text wasn't found: copy it exactly, without line numbers")
    if count > 1 and not replace_all:
        return error(f"old_text appears {count} times: add surrounding text, or set replace_all")
    try:
        found.write_text(text.replace(old_text, new_text) if replace_all else text.replace(old_text, new_text, 1))
    except OSError as e:
        return error(f"couldn't write {path}: {e}")
    return result(f"replaced {count if replace_all else 1} in {path}")


TOOLS = [bash, read_file, write_file, edit_file]
