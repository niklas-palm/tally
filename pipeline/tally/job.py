"""`tally-job <s3 key>`: one meeting on AWS Batch, from its uploaded audio to its published results.

EventBridge starts it when `users/<owner>/meetings/<id>/audio.*` is uploaded; the owner is the signed-in person whose
meeting it is, and only their people are matched and changed. The job claims the meeting's row in the meetings
table (docs/api.md) and writes its progress there: a meeting deleted meanwhile, or sent again, isn't this job's any
more, and the job stops without changing anything. It loads the owner's people, runs prepare, the agent and
publish, and copies the meeting's directory back to S3. The log holds steps and timings only: no audio, transcript
or voiceprint ever goes to it.
"""

import json
import logging
import os
import re
import shutil
import sys
import time
from decimal import Decimal
from pathlib import Path

import boto3

from . import harness, prepare, publish, rewrite, voiceprints, work
from .config import settings
from .voiceprints import Store, now, query

ROOT = Path("/tmp/meeting")
# Left out of prepared/, the copy evals start from: the decoded audio (large, and the upload is there already), and
# people.json: the owner's people live in the tables, and an eval gives its own.
NOT_PREPARED = {"audio16k.wav", "people.json"}
KEY = re.compile(r"^users/([A-Za-z0-9_-]{1,128})/meetings/([0-9A-HJKMNP-TV-Z]{26})/audio\.(m4a|mp3|wav)$")
REWRITE = re.compile(r"^users/([A-Za-z0-9_-]{1,128})/meetings/([0-9A-HJKMNP-TV-Z]{26})/rewrite$")  # the API's: a summary again
FAILED = "Something went wrong while summarising it."  # the app offers what to do next
NO_SPEECH = "No speech was heard in this recording, so there's nothing to summarise."  # sending it again won't change that
TOO_LONG = "This recording is longer than Tally summarises (4 hours)."  # nor that: the apps know both by their first words

log = logging.getLogger("tally")


class Stopped(Exception):
    """The meeting isn't this job's any more: deleted in the app, or sent again (another job has it)."""


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    logging.getLogger("strands").setLevel(logging.WARNING)
    key = sys.argv[1]
    if again := REWRITE.match(key):
        return rewrite.run(again.group(1), again.group(2))
    match = KEY.match(key)
    if not match:  # the rule's wildcard also matches deeper keys: only a meeting's own audio starts a run
        log.info("not a meeting's audio, ignored: %s", key)
        return
    owner, meeting_id = match.group(1), match.group(2)
    prefix = f"users/{owner}/meetings/{meeting_id}"
    job = os.environ.get("AWS_BATCH_JOB_ID", "local")
    s3 = boto3.client("s3", region_name=settings.region)
    table = boto3.resource("dynamodb", region_name=settings.region).Table(settings.meetings_table)
    row = {"owner": owner, "meeting_id": meeting_id}
    store = Store(owner, settings.people_table, settings.voiceprints_table, settings.region)
    checked = table.meta.client.exceptions.ConditionalCheckFailedException

    def claim() -> bool:
        """Takes a meeting waiting for its run (uploading or queued) for this job, or takes it back on a Batch retry of
        this same job (a lost host). A repeated upload event or a second job finds it taken, and a deleted meeting has
        no row: both leave it alone."""
        try:
            table.update_item(Key=row, UpdateExpression="SET #s = :processing, job_id = :job, updated_at = :now, #e = :none",
                              ConditionExpression="#s IN (:uploading, :queued) OR (#s = :processing AND job_id = :job)",
                              ExpressionAttributeNames={"#s": "state", "#e": "error"},
                              ExpressionAttributeValues={":processing": "processing", ":job": job, ":now": now(), ":none": None,
                                                         ":uploading": "uploading", ":queued": "queued"})
            return True
        except checked:
            return False

    def status(state: str, **fields) -> None:
        """This job's progress on the meeting; Stopped when the meeting isn't its own any more."""
        values = {k: (Decimal(str(v)) if isinstance(v, float) else v) for k, v in ({"state": state, "updated_at": now()} | fields).items()}
        try:
            table.update_item(Key=row, UpdateExpression="SET " + ", ".join(f"#{k} = :{k}" for k in values), ConditionExpression="job_id = :job",
                              ExpressionAttributeNames={f"#{k}": k for k in values},
                              ExpressionAttributeValues={f":{k}": v for k, v in values.items()} | {":job": job})
        except checked:
            raise Stopped from None

    def forget() -> None:
        """A meeting deleted while this job wrote to it: take back what the job wrote after the app's delete. A
        meeting that's still there is left as it is."""
        if not gone():
            return
        for page in s3.get_paginator("list_objects_v2").paginate(Bucket=settings.bucket, Prefix=f"{prefix}/"):
            if keys := [{"Key": o["Key"]} for o in page.get("Contents", [])]:
                s3.delete_objects(Bucket=settings.bucket, Delete={"Objects": keys, "Quiet": True})
        # the people the owner's other meetings name hold on to them, voiceprint or not
        store.forget(meeting_id, {p for m in query(table, owner) if m["meeting_id"] != meeting_id for p in m.get("people") or []})

    def gone() -> bool:
        return "Item" not in table.get_item(Key=row)

    if not claim():
        log.info("meeting %s was deleted before its job started, or is another job's, or done", meeting_id)
        forget()  # deleted: the upload landed after the delete
        return
    path = ROOT / "work"
    path.mkdir(parents=True, exist_ok=True)
    metrics = []
    try:
        audio = ROOT / Path(key).name
        s3.download_file(settings.bucket, key, str(audio))
        meta = json.loads(s3.get_object(Bucket=settings.bucket, Key=f"{prefix}/meta.json")["Body"].read())
        mine = store.load()
        pool = store.shared(mine) if settings.shared_voices else []
        work.write("people.json", {"people": mine}, path)

        started = time.monotonic()
        prepared = prepare.run(audio, path)
        # prepare has matched the owner's people, and the pool is matched here: from now on the folder holds people,
        # never voiceprints
        matched, heard = match_pool(path, pool)
        work.write("people.json", {"people": voiceprints.voiceless(mine + matched)}, path)
        metrics.append({"step": "prepare", "seconds": round(time.monotonic() - started, 1), "timings": prepared["timings"],
                        "duration": prepared["duration"], "speakers": prepared["speakers"], "language": prepared["language"]})
        log.info("prepared: %s", metrics[-1])
        shutil.copytree(path, ROOT / "prepared", symlinks=True, ignore=shutil.ignore_patterns(*NOT_PREPARED))  # evals rerun the agent from here
        shutil.copyfile(path / "audio16k.wav", ROOT / "audio16k.wav")  # root's own copy, which publish cuts the clips from
        status("processing")

        run = harness.run(path, meta, ROOT / "run" / "conversation.json")
        metrics.append({"step": "agent", "seconds": run.seconds, "turns": run.turns, "tool_calls": run.tool_calls, "tokens": run.tokens})
        log.info("agent done: %s", metrics[-1])
        status("processing")  # still this job's, just before it changes anything outside the meeting

        published = publish.publish(ROOT, meeting_id, meta, store, heard)
        metrics.append({"step": "publish", **published, "voiceprints_saved": len(published["voiceprints_saved"])})
        log.info("published: %s", metrics[-1])
        upload(s3, prefix, metrics)
        speakers = work.read("speakers.json", path)["speakers"]
        # the language as publish checked it: the row shows in every list
        status("ready", duration=prepared["duration"], language=work.read("results/meeting.json", ROOT)["language"],
               title=work.read("summary.json", path)["title"], error=None, agent_runs=[run.record("summary", now())],
               people=sorted({s["identity"]["person_id"] for s in speakers.values() if s["identity"].get("person_id")}))
    except Stopped:
        log.info("meeting %s isn't this job's any more: stopping", meeting_id)
        forget()
    except (prepare.NoSpeech, prepare.TooLong) as e:
        log.info("meeting %s: %s", meeting_id, type(e).__name__)
        try:
            status("failed", error=NO_SPEECH if isinstance(e, prepare.NoSpeech) else TOO_LONG)
        except Stopped:
            forget()
    except Exception:
        log.exception("meeting %s failed", meeting_id)
        try:  # for debugging: what the run got to, before the app can see it failed; never in the way of saying so
            upload(s3, prefix, metrics)
        except Exception:
            log.exception("uploading meeting %s's files failed", meeting_id)
        try:
            status("failed", error=FAILED)
        except Stopped:
            forget()
            return
        sys.exit(1)


def match_pool(path: Path, pool: list[dict]) -> tuple[list[dict], dict[str, set[str]]]:
    """The shared pool matched against this meeting's voices in memory, and added to each speaker's matches: the pool
    can hold every colleague's voiceprints, too many for a file the agent's tools read. Returns the people a speaker's
    matches still list, with their names alone (nothing else of a colleague's reaches the agent's folder), and, for
    publish, each speaker's matched names: the job's own record, which the agent can't change."""
    if not pool:
        return [], {}
    speakers = work.read("speakers.json", path)
    embeddings = work.read_embeddings(path)
    for speaker, s in speakers["speakers"].items():
        if speaker in embeddings:  # a pooled person is listed only at NO_MATCH or more (voiceprints.match)
            s["matches"] = sorted(s["matches"] + voiceprints.match(embeddings[speaker], pool), key=lambda m: -m["score"])[:voiceprints.TOP]
    work.write("speakers.json", speakers, path)
    names = {p["person_id"]: p["name"].casefold() for p in pool}
    heard = {speaker: {names[m["person_id"]] for m in s["matches"] if m["person_id"] in names} for speaker, s in speakers["speakers"].items()}
    listed = {m["person_id"] for s in speakers["speakers"].values() for m in s["matches"]}
    return [p | {"role": None, "company": None} for p in pool if p["person_id"] in listed], {k: v for k, v in heard.items() if v}


def upload(s3, prefix: str, metrics: list[dict]) -> None:
    (ROOT / "run").mkdir(exist_ok=True)
    (ROOT / "run" / "metrics.jsonl").write_text("".join(json.dumps(m) + "\n" for m in metrics))
    for file in sorted(ROOT.rglob("*")):
        relative = file.relative_to(ROOT)
        # Not a symlink: the agent owns work/, and root mustn't follow a link it planted to some other file.
        # root's own top-level files (the upload, and its decoded copy) aren't kept
        if file.is_file() and not file.is_symlink() and len(relative.parts) > 1 and kept(relative):
            s3.upload_file(str(file), settings.bucket, f"{prefix}/{relative}")


def kept(relative: Path) -> bool:
    """Of the folder the agent writes in, the working files docs/data.md names: not whatever else it made, nor its
    clips (publish cuts the meeting's own). prepared/, results/ and run/ are root's own."""
    if relative.parts[0] != "work":
        return True
    return len(relative.parts) == 2 and relative.name in work.FILES


if __name__ == "__main__":
    main()
