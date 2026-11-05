"""The GPU queue's watch (watch.py): how long a meeting has waited, and which failures count."""

import os

os.environ.update(AWS_DEFAULT_REGION="eu-north-1", TALLY_JOB_QUEUE="q", TALLY_WATCH_MINUTES="5")

import watch  # noqa: E402

NOW = 1_800_000_000_000  # ms


class Queue:
    def __init__(self, jobs):
        self.jobs = jobs
        self.sent = []

    def list_jobs(self, jobQueue, jobStatus, nextToken=None):
        found = [j for j in self.jobs if j["status"] == jobStatus]
        if nextToken is None and len(found) > 1:  # a page at a time
            return {"jobSummaryList": found[:1], "nextToken": "next"}
        return {"jobSummaryList": found[1:] if nextToken else found}

    def put_metric_data(self, Namespace, MetricData):
        self.sent.append({m["MetricName"]: m["Value"] for m in MetricData})


def test_the_longest_wait_and_the_failures_since_the_last_look_but_never_a_deleted_meeting(monkeypatch):
    queue = Queue([{"status": "STARTING", "createdAt": NOW - 3 * 60_000}, {"status": "RUNNABLE", "createdAt": NOW - 2 * 60_000},
                   {"status": "RUNNABLE", "createdAt": NOW - 25 * 60_000},  # on the second page
                   {"status": "RUNNABLE", "createdAt": NOW - 90 * 60_000, "stoppedAt": NOW - 60_000},  # retried: waiting a minute
                   {"status": "FAILED", "stoppedAt": NOW - 60_000, "statusReason": "Essential container in task exited"},
                   {"status": "FAILED", "stoppedAt": NOW - 5.5 * 60_000, "statusReason": "between two looks, the schedule late"},
                   {"status": "FAILED", "stoppedAt": NOW - 60_000, "statusReason": "The meeting was deleted"},
                   {"status": "FAILED", "stoppedAt": NOW - 60 * 60_000, "statusReason": "an hour ago: told already"}])
    monkeypatch.setattr(watch, "batch", queue)
    monkeypatch.setattr(watch, "cloudwatch", queue)
    monkeypatch.setattr(watch.time, "time", lambda: NOW / 1000)
    assert watch.handler({}, None) == {"waiting_minutes": 25.0, "failed": 2}
    assert queue.sent == [{"WaitingMinutes": 25.0, "FailedMeetings": 2}]
    queue.jobs = []
    assert watch.handler({}, None) == {"waiting_minutes": 0, "failed": 0}  # nothing waiting: 0, so the alarm ends
