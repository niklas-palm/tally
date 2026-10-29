"""`tally-job users/<owner>/meetings/<id>/rewrite`: the summary written again, without the voices the owner set aside
as background. Nothing is transcribed or identified again: the meeting's working files come back from
S3, everyone the owner settled stays as they said, and the background voices' lines are taken out before the agent
reads anything, so a loud bystander can't steer the summary. Only the summary and the meeting's title change.

The API asks for it (`POST /meetings/{id}/summary/rewrite` sets `rewriting` and submits this job); the job claims the
request, so a second job for it does nothing, and clears it however it ends.
"""

import json
import logging
import os
import shutil
from datetime import datetime
from pathlib import Path

import boto3

from . import harness, summary, voiceprints, work
from .config import settings
from .publish import refuse_too_big
from .voiceprints import Store, now

log = logging.getLogger("tally")
ROOT = Path("/tmp/rewrite")
START = ("Everyone in this meeting is already identified, by the owner, and voices that weren't in the meeting (people talking "
         "nearby) have been taken out of the transcript, if any. Don't change who anyone is: no `tally name`, `unknown`, `merge` or "
         "`split`. Read the meeting again as a whole and write its summary, as your instructions describe.")
FAILED = "The summary couldn't be rewritten. Try again in a moment."


def run(owner: str, meeting_id: str) -> None:
    prefix = f"users/{owner}/meetings/{meeting_id}"
    job = os.environ.get("AWS_BATCH_JOB_ID", "local")
    s3 = boto3.client("s3", region_name=settings.region)
    table = boto3.resource("dynamodb", region_name=settings.region).Table(settings.meetings_table)
    row = {"owner": owner, "meeting_id": meeting_id}
    checked = table.meta.client.exceptions.ConditionalCheckFailedException
    try:  # the request is this job's: asked for, ready, and no other job on it
        asked = table.update_item(Key=row, UpdateExpression="SET rewrite_job = :job", ReturnValues="ALL_NEW",
                                  ConditionExpression="attribute_exists(rewriting) AND #s = :ready AND (attribute_not_exists(rewrite_job) OR rewrite_job = :job)",
                                  ExpressionAttributeNames={"#s": "state"}, ExpressionAttributeValues={":job": job, ":ready": "ready"})["Attributes"]["rewriting"]
    except checked:
        log.info("meeting %s: no rewrite to do", meeting_id)
        return

    def done(agent_run: harness.Run | None, **fields) -> None:
        """Clears the request, with the new title or the error, and adds the agent's run to `agent_runs`, since its tokens were spent either way."""
        values = {f":{k}": v for k, v in fields.items()}
        sets = [f"{k} = :{k}" for k in fields]
        if agent_run:
            sets.append("agent_runs = list_append(if_not_exists(agent_runs, :none), :run)")
            values |= {":none": [], ":run": [agent_run.record("rewrite", now())]}
        update = "REMOVE rewriting, rewrite_job SET " + ", ".join(sets)
        try:
            table.update_item(Key=row, UpdateExpression=update, ConditionExpression="rewrite_job = :job",
                              ExpressionAttributeValues={":job": job} | values)
        except checked:
            if "Item" not in table.get_item(Key=row):  # deleted while this ran: take back what it wrote after the delete
                for page in s3.get_paginator("list_objects_v2").paginate(Bucket=settings.bucket, Prefix=f"{prefix}/"):
                    if keys := [{"Key": o["Key"]} for o in page.get("Contents", [])]:
                        s3.delete_objects(Bucket=settings.bucket, Delete={"Objects": keys, "Quiet": True})
            log.info("meeting %s changed meanwhile", meeting_id)

    agent_run = None
    try:
        path = ROOT / "work"
        shutil.rmtree(ROOT, ignore_errors=True)
        path.mkdir(parents=True)
        for page in s3.get_paginator("list_objects_v2").paginate(Bucket=settings.bucket, Prefix=f"{prefix}/work/"):
            for item in page.get("Contents", []):
                name = item["Key"].removeprefix(f"{prefix}/work/")
                if name in work.FILES:  # the working files, no clips or audio, and nothing else an earlier run left there
                    s3.download_file(settings.bucket, item["Key"], str(path / name))
        results = json.loads(s3.get_object(Bucket=settings.bucket, Key=f"{prefix}/results/meeting.json")["Body"].read())
        background = settle(path, results, Store(owner, settings.people_table, settings.voiceprints_table, settings.region))
        agent_run = harness.run(path, results["meta"], ROOT / "run" / "conversation.json", start=START)
        log.info("rewrite: %s turns, %s tool calls, %s s", agent_run.turns, agent_run.tool_calls, agent_run.seconds)
        settled = {k: v["identity"] for k, v in work.read("speakers.json", path)["speakers"].items()}
        if settled != {k: v["identity"] for k, v in results["speakers"].items() if k not in background}:
            raise RuntimeError("the agent changed who someone is")  # the owner's word stands: nothing is published
        written = work.read("summary.json", path)
        if publish(s3, prefix, written, background, results, datetime.fromisoformat(asked)):
            done(agent_run, title=written["title"], edited_at=now())
        else:
            done(agent_run, edited_at=now())  # the owner's own copy, edited since they asked, keeps its title
        log.info("meeting %s: summary rewritten without %s", meeting_id, len(background))
    except Exception:
        log.exception("meeting %s: rewrite failed", meeting_id)
        done(agent_run, rewrite_error=FAILED)


def settle(path: Path, results: dict, store: Store) -> list[str]:
    """The working files as the owner left the meeting: each speaker as they said, the background voices and their
    lines gone, the people as they are now, and no summary yet. Returns the background voices."""
    background = sorted(k for k, v in results["speakers"].items() if (v.get("identity") or {}).get("background"))
    speakers = work.read("speakers.json", path)["speakers"]
    for speaker in list(speakers):
        if speaker in background:
            del speakers[speaker]
        else:
            speakers[speaker]["identity"] = results["speakers"][speaker]["identity"]
    work.write("speakers.json", {"speakers": speakers}, path)
    found = work.read("transcript.json", path)
    found["segments"] = [s for s in found["segments"] if s["speaker"] not in background]
    work.write("transcript.json", found, path)
    work.write("people.json", {"people": voiceprints.voiceless(store.load())}, path)
    (path / "summary.json").unlink(missing_ok=True)
    return background


def publish(s3, prefix: str, written: dict, background: list[str], results: dict, asked: datetime) -> bool:
    """The new summary into results/meeting.json, written only over the version that was read, so a speaker the owner
    named meanwhile isn't lost; and summary.md again. `summary_without` says which voices it was written without."""
    key = f"{prefix}/results/meeting.json"
    for _ in range(5):
        found = s3.get_object(Bucket=settings.bucket, Key=key)
        current = json.loads(found["Body"].read())
        current["summary"], current["summary_without"] = written, background
        refuse_too_big(current)
        try:
            s3.put_object(Bucket=settings.bucket, Key=key, Body=json.dumps(current).encode(), ContentType="application/json",
                          IfMatch=found["ETag"])
            break
        except s3.exceptions.ClientError as e:
            if e.response["Error"]["Code"] not in ("PreconditionFailed", "ConditionalRequestConflict"):
                raise
    else:
        raise RuntimeError("the meeting kept changing")
    speakers = {k: v for k, v in current["speakers"].items() if k not in background}
    page = summary.markdown(written, speakers, results["meta"], results["duration"])
    s3.put_object(Bucket=settings.bucket, Key=f"{prefix}/results/summary.md", Body=page.encode(), ContentType="text/markdown")
    # Asking for a rewrite replaces the owner's own copy; one they saved after asking is newer, and stays.
    try:
        saved = s3.head_object(Bucket=settings.bucket, Key=f"{prefix}/notes/summary.json")["LastModified"]
    except s3.exceptions.ClientError:
        return True  # no copy of their own
    if saved > asked:
        return False
    s3.delete_object(Bucket=settings.bucket, Key=f"{prefix}/notes/summary.json")
    return True
