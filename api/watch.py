"""Watching the GPU queue, for the alarms (cdk/tally_stack.py): every few minutes, how long the longest-waiting meeting
has waited for a GPU, and how many meetings' jobs failed since the last look, as two CloudWatch metrics their alarms
read. A job stopped because its meeting was deleted (tally_api.delete_meeting) is no failure. Only boto3 and the
standard library.
"""

import os
import time

import boto3

batch = boto3.client("batch")
cloudwatch = boto3.client("cloudwatch")
WAITING = ("SUBMITTED", "PENDING", "RUNNABLE", "STARTING")
DELETED = "The meeting was deleted"  # the API's reason, when it stops a deleted meeting's job


def jobs(status: str) -> list[dict]:
    found, kwargs = [], {"jobQueue": os.environ["TALLY_JOB_QUEUE"], "jobStatus": status}
    while True:
        page = batch.list_jobs(**kwargs)
        found += page["jobSummaryList"]
        if not page.get("nextToken"):
            return found
        kwargs["nextToken"] = page["nextToken"]


def handler(_event, _context) -> dict:
    now, every = time.time() * 1000, int(os.environ["TALLY_WATCH_MINUTES"]) * 60_000
    # A job Batch retries, its instance gone, waits again from when its last try stopped, not from when it was sent
    waiting = [j.get("stoppedAt") or j["createdAt"] for status in WAITING for j in jobs(status)]
    minutes = round((now - min(waiting)) / 60_000, 1) if waiting else 0
    # A minute more than the schedule's interval, which isn't exact: a failure counted twice rings as once, one missed never
    failed = sum(1 for j in jobs("FAILED") if j.get("stoppedAt", 0) > now - every - 60_000 and DELETED not in (j.get("statusReason") or ""))
    cloudwatch.put_metric_data(Namespace="Tally", MetricData=[
        {"MetricName": "WaitingMinutes", "Value": minutes, "Unit": "None"},
        {"MetricName": "FailedMeetings", "Value": failed, "Unit": "Count"}])
    return {"waiting_minutes": minutes, "failed": failed}
