"""The summary written again without background voices: the owner's word on who's who stands, the
background voices' lines never reach the agent, and nothing is published if the agent changes someone."""

import io
import json
from datetime import UTC, datetime
from pathlib import Path

import pytest

from tally import harness, rewrite, work

OWNER, MEETING = "0b6e4f2a-5c1d-4e8b-9a3f-7d2c6e1b4a90", "01M5W8DAHM7HCDPH26DE2PRR31"
PREFIX = f"users/{OWNER}/meetings/{MEETING}"
SUMMARY = {"title": "New", "overview": "{S1} met {S2}.", "summary": [], "decisions": [], "action_items": [], "open_questions": [],
           "topics": [], "attendees": []}


class Checked(Exception):
    pass


class Table:
    meta = type("M", (), {"client": type("C", (), {"exceptions": type("E", (), {"ConditionalCheckFailedException": Checked})})})

    def __init__(self, row):
        self.row = row

    def get_item(self, Key):
        return {"Item": self.row} if self.row is not None else {}

    def update_item(self, Key, UpdateExpression, ConditionExpression, ExpressionAttributeValues, ExpressionAttributeNames=None, ReturnValues=None):
        if "attribute_exists(rewriting)" in ConditionExpression and "rewriting" not in self.row:
            raise Checked()
        if self.row is None or ("rewrite_job = :job" in ConditionExpression and not UpdateExpression.startswith("SET rewrite_job")
                                and self.row.get("rewrite_job") != ExpressionAttributeValues[":job"]):
            raise Checked()
        if UpdateExpression.startswith("SET rewrite_job"):
            self.row["rewrite_job"] = ExpressionAttributeValues[":job"]
            return {"Attributes": dict(self.row)}
        else:
            for k in ("rewriting", "rewrite_job"):
                self.row.pop(k, None)
            self.runs = ExpressionAttributeValues.get(":run")
            self.row |= {k.lstrip(":"): v for k, v in ExpressionAttributeValues.items() if k not in (":job", ":run", ":none")}


class S3:
    exceptions = type("E", (), {"ClientError": type("ClientError", (Exception,), {})})

    def __init__(self, objects):
        self.objects = objects
        self.saved = {}  # when each object was last written, for head_object

    def head_object(self, Bucket, Key):
        if Key not in self.objects:
            raise self.exceptions.ClientError("404")
        return {"LastModified": self.saved.get(Key, datetime(2026, 10, 28, 9, 0, tzinfo=UTC))}

    def get_paginator(self, _):
        objects = self.objects
        return type("P", (), {"paginate": lambda _s, Bucket, Prefix: [{"Contents": [{"Key": k} for k in objects if k.startswith(Prefix)]}]})()

    def download_file(self, bucket, key, path):
        Path(path).write_bytes(self.objects[key])

    def get_object(self, Bucket, Key):
        return {"Body": io.BytesIO(self.objects[Key]), "ETag": "e"}

    def put_object(self, Bucket, Key, Body, ContentType=None, IfMatch=None):
        self.objects[Key] = Body

    def delete_object(self, Bucket, Key):
        self.objects.pop(Key, None)

    def delete_objects(self, Bucket, Delete):
        for o in Delete["Objects"]:
            self.objects.pop(o["Key"], None)


class Store:
    def __init__(self, *a):
        pass

    def load(self):
        return [{"person_id": "p_ada", "name": "Ada Lovelace", "aliases": [], "is_owner": True, "voiceprints": []}]


@pytest.fixture
def meeting(monkeypatch, tmp_path):
    speakers = {k: {"talk_time": 60, "turns": 2, "first_seen": 0.0, "clean_speech": 30.0, "mic_ratio_db": None, "matches": [], "identity": None}
                for k in ("S1", "S2", "S3")}
    results = {"meta": {"started_at": "2026-10-28T09:00:00Z"}, "duration": 600, "summary": SUMMARY | {"title": "Old"},
               "speakers": {"S1": {"identity": {"name": "Ada Lovelace", "person_id": "p_ada", "by": "owner"}},
                            "S2": {"identity": {"unknown": True, "by": "owner"}},
                            "S3": {"identity": {"unknown": True, "background": True, "by": "owner"}}}}
    segments = [{"start": 0, "end": 2, "speaker": "S1", "text": "Hi."}, {"start": 2, "end": 4, "speaker": "S3", "text": "Free lunch upstairs!"},
                {"start": 4, "end": 6, "speaker": "S2", "text": "Hello."}]
    objects = {f"{PREFIX}/work/speakers.json": json.dumps({"speakers": speakers}).encode(),
               f"{PREFIX}/work/transcript.json": json.dumps({"segments": segments}).encode(),
               f"{PREFIX}/work/manifest.json": json.dumps({"duration": 600, "language": "en", "layout": "mono"}).encode(),
               f"{PREFIX}/work/summary.json": json.dumps(SUMMARY | {"title": "Old"}).encode(),
               f"{PREFIX}/results/meeting.json": json.dumps(results).encode(),
               f"{PREFIX}/notes/summary.json": json.dumps(SUMMARY | {"title": "Mine"}).encode()}
    s3, table = S3(objects), Table({"state": "ready", "rewriting": "2026-10-28T10:00:00+00:00"})
    monkeypatch.setattr(rewrite, "ROOT", tmp_path / "rewrite")
    monkeypatch.setattr(rewrite.boto3, "client", lambda *a, **k: s3)
    monkeypatch.setattr(rewrite.boto3, "resource", lambda *a, **k: type("R", (), {"Table": lambda _s, name: table})())
    monkeypatch.setattr(rewrite, "Store", Store)
    monkeypatch.setattr(rewrite.summary, "markdown", lambda *a: "# page")
    return s3, table


def agent(change=None):
    def run(path, meta, conversation, start):
        seen = work.read("transcript.json", path)["segments"]
        assert [s["speaker"] for s in seen] == ["S1", "S2"]  # the background voice's lines never reach the agent
        assert "Don't change who anyone is" in start
        if change:
            change(path)
        work.write("summary.json", SUMMARY, path)
        return harness.Run(1, 2, 3.0, {"input": 10, "output": 20, "cache_read": 30, "cache_write": 40})
    return run


def test_the_summary_is_written_again_without_background_voices(meeting, monkeypatch):
    s3, table = meeting
    monkeypatch.setattr(rewrite.harness, "run", agent())
    rewrite.run(OWNER, MEETING)
    results = json.loads(s3.objects[f"{PREFIX}/results/meeting.json"])
    assert results["summary"]["title"] == "New" and results["summary_without"] == ["S3"]
    assert results["speakers"]["S3"]["identity"]["background"] is True  # still there, set aside
    assert table.row["title"] == "New" and "rewriting" not in table.row and "edited_at" in table.row
    assert f"{PREFIX}/notes/summary.json" not in s3.objects  # the owner's copy goes once the new summary is written
    assert table.runs == [{"kind": "rewrite", "at": table.runs[0]["at"], "seconds": 3, "turns": 1,
                           "input": 10, "output": 20, "cache_read": 30, "cache_write": 40}]  # its tokens, with the meeting's others


def test_an_agent_that_changes_someone_publishes_nothing(meeting, monkeypatch):
    s3, table = meeting

    def rename(path):
        speakers = work.read("speakers.json", path)
        speakers["speakers"]["S2"]["identity"] = {"name": "Mallory", "person_id": None, "by": "agent"}
        work.write("speakers.json", speakers, path)

    monkeypatch.setattr(rewrite.harness, "run", agent(rename))
    rewrite.run(OWNER, MEETING)
    assert json.loads(s3.objects[f"{PREFIX}/results/meeting.json"])["summary"]["title"] == "Old"
    assert f"{PREFIX}/notes/summary.json" in s3.objects  # a failed rewrite loses nothing of the owner's
    assert table.row["rewrite_error"] == rewrite.FAILED and "rewriting" not in table.row


def test_a_rewrite_nobody_asked_for_does_nothing(meeting, monkeypatch):
    s3, table = meeting
    del table.row["rewriting"]
    monkeypatch.setattr(rewrite.harness, "run", lambda *a, **k: pytest.fail("ran the agent"))
    rewrite.run(OWNER, MEETING)


def test_an_edit_the_owner_saved_after_asking_is_kept(meeting, monkeypatch):
    s3, table = meeting
    s3.saved[f"{PREFIX}/notes/summary.json"] = datetime(2026, 10, 28, 10, 40, tzinfo=UTC)  # after the request at 10:00
    monkeypatch.setattr(rewrite.harness, "run", agent())
    rewrite.run(OWNER, MEETING)
    assert json.loads(s3.objects[f"{PREFIX}/results/meeting.json"])["summary"]["title"] == "New"  # the agent's, rewritten
    assert json.loads(s3.objects[f"{PREFIX}/notes/summary.json"])["title"] == "Mine"  # and theirs, newer, stays
    assert "title" not in table.row and "rewriting" not in table.row  # the list keeps their title


def test_a_meeting_deleted_while_its_summary_was_rewritten_leaves_nothing_behind(meeting, monkeypatch):
    s3, table = meeting

    def deleted(path):  # the owner deletes the meeting while the agent writes
        s3.objects.clear()
        table.row = None

    monkeypatch.setattr(rewrite.harness, "run", agent(deleted))
    monkeypatch.setattr(rewrite, "publish", lambda s3_, prefix, *a: s3_.put_object(Bucket="b", Key=f"{prefix}/results/summary.md", Body=b"#") or True)
    rewrite.run(OWNER, MEETING)
    assert not [k for k in s3.objects if k.startswith(PREFIX)]
