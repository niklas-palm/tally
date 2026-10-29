"""Tally's API: one Lambda behind API Gateway, whose JWT authorizer has already checked the caller (docs/api.md).

Everything belongs to the token's `sub`: S3 keys under `users/<sub>/`, every table keyed by `owner` first. Besides the
pipeline it's the only writer, so the rules for an owner's changes live here. Only boto3 and the standard library.
"""

import base64
import contextlib
import io
import json
import os
import re
import struct
import time
import unicodedata
import zipfile
from array import array
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from zoneinfo import ZoneInfo

import admin
import boto3
from boto3.dynamodb.conditions import Key

BUCKET = os.environ["TALLY_BUCKET"]
MODEL = "pyannote/speaker-diarization-community-1"  # voiceprints compare only within one model
DIMENSION = 256  # its embeddings
# A meeting uploading, queued or processing for longer has stopped without saying so: the job's limit is 2 hours,
# and a GPU can take a while to come free.
STALE = timedelta(hours=3)
MEETING_ID = re.compile(r"^[0-9A-HJKMNP-TV-Z]{26}$")
OWNER = re.compile(r"^[A-Za-z0-9_-]{1,128}$")  # the provider's user id (a UUID in Keycloak and Cognito): it becomes part of S3 keys
CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
CANDIDATES = 5  # people listed under "Sounds like" for each speaker
LINE_CHARACTERS = 100  # a person's name, role or company, and the app's version: one short line each

REPORT_BYTES = 2_000_000  # a macOS crash report is 50 to 200 KB; a Windows one, a few KB

s3 = boto3.client("s3", config=boto3.session.Config(signature_version="s3v4", s3={"addressing_style": "virtual"}))
dynamodb = boto3.resource("dynamodb")
meetings = dynamodb.Table(os.environ["TALLY_MEETINGS_TABLE"])
people = dynamodb.Table(os.environ["TALLY_PEOPLE_TABLE"])
folders = dynamodb.Table(os.environ["TALLY_FOLDERS_TABLE"])
users = dynamodb.Table(os.environ["TALLY_USERS_TABLE"])
batch = boto3.client("batch")
ce = boto3.client("ce", region_name="us-east-1")  # Cost Explorer has one endpoint
voiceprints = dynamodb.Table(os.environ["TALLY_VOICEPRINTS_TABLE"])
sns = boto3.client("sns")


class Problem(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def line(value: str) -> str:
    """A person's name, role or company as the owner typed it, or the app's version: one short line. Names, roles and
    companies reach the meeting agent's prompt (and, with shared voices, colleagues'), so nothing longer or on several
    lines gets in."""
    joined = " ".join(value.split())  # a non-breaking or other space is a space
    if len(joined) > LINE_CHARACTERS or any(unicodedata.category(c) in ("Cc", "Zl", "Zp") for c in value):
        raise Problem(400, f"a name, role, company or version is one line of at most {LINE_CHARACTERS} characters")
    return joined


TIME = re.compile(r"20\d\d-\d\d-\d\dT\d\d:\d\d:\d\d(Z|[+-](0\d|1[0-3]):[0-5]\d|[+-]14:00)")


def instant(value: str | None) -> str | None:
    """A time as both apps write and read it: `2026-10-28T09:00:00Z`, or with an offset of at most 14 hours. One in
    any other form would stop the apps' meeting lists loading."""
    try:
        if value is None or (TIME.fullmatch(value) and datetime.fromisoformat(value)):
            return value
    except ValueError:
        pass
    raise Problem(400, "a time is ISO 8601 with its offset (2026-10-28T09:00:00Z), this century")


def handler(event, _context):
    route = ROUTES.get(event["routeKey"])
    if not route:
        return reply(404, {"error": "no such route"})
    try:
        claims = event["requestContext"]["authorizer"]["jwt"]["claims"]
        owner = claims["sub"]
        if not OWNER.fullmatch(owner):
            raise Problem(403, "unexpected user id")
        params = (event.get("queryStringParameters") or {}) | (event.get("pathParameters") or {})
        params["signed_in_as"] = claims.get("name") or claims.get("email") or claims.get("preferred_username") or claims.get("username") or owner
        params["email"] = claims.get("email")
        params["app_version"] = (event.get("headers") or {}).get("x-tally-version")
        params["admin"] = "admin" in roles(event)
        if "/meetings/{id}" in event["routeKey"] and not MEETING_ID.fullmatch(params["id"]):
            raise Problem(400, "a meeting id is a ULID")
        try:
            body = json.loads(event.get("body") or "{}")
        except json.JSONDecodeError:
            raise Problem(400, "the body isn't JSON") from None
        try:  # half an emoji's pair on its own (\ud800) is JSON, but no text either app can read back
            json.dumps(body, ensure_ascii=False).encode()
        except UnicodeEncodeError:
            raise Problem(400, "the body isn't valid Unicode") from None
        if not isinstance(body, dict):
            raise Problem(400, "the body is a JSON object")
        return reply(200, route(owner, params, body))
    except Problem as e:
        return reply(e.status, {"error": str(e)})
    except Exception as e:  # logged by Lambda; the app shows the message
        print(f"{event['routeKey']} failed: {type(e).__name__}: {e}")
        return reply(500, {"error": "Something went wrong on Tally's side. Try again in a moment."})


def roles(event: dict) -> list:
    """The roles in the caller's token at `auth.adminClaim`, a dotted path (`resource_access.tally.roles` in Keycloak,
    `cognito:groups` in Cognito). API Gateway checked the token but flattens nested claims, so the payload is read
    from the header, and only when it's that same token (same `sub`, `iat`, `exp`): a request can carry a second."""
    checked = event["requestContext"]["authorizer"]["jwt"]["claims"]
    header = (event.get("headers") or {}).get("authorization") or ""
    # One token, so the one API Gateway checked: two headers arrive joined with a comma
    if not re.fullmatch(r"Bearer [\w-]+\.[\w-]+\.[\w-]+", header):
        return []
    try:
        payload = header.split(".")[1]
        found = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
        # the sub always, and its times wherever API Gateway hands them on (as text)
        if (not isinstance(found, dict) or found.get("sub") != checked.get("sub")
                or any(k in checked and str(found.get(k)) != str(checked[k]) for k in ("iat", "exp"))):
            return []
        for name in os.environ["TALLY_ADMIN_CLAIM"].split("."):
            found = found.get(name, {}) if isinstance(found, dict) else {}
        return found if isinstance(found, list) else []
    except ValueError:  # not base64, or not JSON
        return []


def reply(status: int, body: dict) -> dict:
    return {"statusCode": status, "headers": {"content-type": "application/json"}, "body": json.dumps(body, default=plain)}


def plain(value):
    if isinstance(value, Decimal):
        return int(value) if value == value.to_integral_value() else float(value)
    raise TypeError(type(value))


def prefix(owner: str, meeting_id: str) -> str:
    return f"users/{owner}/meetings/{meeting_id}"


# Meetings


def list_meetings(owner, params, _body) -> dict:
    # Who uses Tally, when last, and on which version: the app syncs this every minute or so (the admin dashboard)
    users.update_item(Key={"owner": owner}, UpdateExpression="SET #n = :n, email = :e, last_seen = :t, app_version = :v",
                      ExpressionAttributeNames={"#n": "name"},
                      ExpressionAttributeValues={":n": params["signed_in_as"][:200], ":e": (params.get("email") or "")[:200], ":t": now(),
                                                 ":v": (params.get("app_version") or "")[:60]})
    return {"meetings": sorted((summary_of(m) for m in mine(meetings, owner)), key=lambda m: m["meeting_id"], reverse=True)}


def summary_of(item: dict) -> dict:
    listed = {k: item.get(k) for k in ("meeting_id", "state", "title", "started_at", "ended_at", "duration", "language", "error", "updated_at", "edited_at", "rewriting", "rewrite_error")}
    listed["people"] = list(item.get("people") or [])  # the person ids of who spoke: a person's meetings, in the app
    listed["folder_ids"] = sorted(item.get("folder_ids") or [])  # the owner's folders it's in: any number
    working = listed["state"] in ("uploading", "queued", "processing") and listed["updated_at"]
    if working and datetime.now(UTC) - datetime.fromisoformat(listed["updated_at"]) > STALE:
        listed |= {"state": "failed", "error": "Summarising stopped before it finished."}
    return listed


def meeting_of(owner: str, meeting_id: str) -> dict | None:
    """The owner's meeting as it is now (a consistent read: a write a moment ago, an upload asked for, is in it)."""
    return meetings.get_item(Key={"owner": owner, "meeting_id": meeting_id}, ConsistentRead=True).get("Item")


def meeting(owner: str, meeting_id: str) -> dict:
    """The owner's meeting, or 404."""
    if not (item := meeting_of(owner, meeting_id)):
        raise Problem(404, "this meeting is no longer there")
    return item


def ready_meeting(owner: str, meeting_id: str) -> dict:
    """The owner's finished meeting: 404 if it's gone, 409 if a run is still on its way (it would publish over a change)."""
    if (item := meeting(owner, meeting_id))["state"] != "ready":
        raise Problem(409, "the meeting isn't ready")
    return item


def rewriting(item: dict) -> bool:
    """A summary rewrite asked for and not yet answered: one asked longer ago than REWRITE_WAIT has stopped silently."""
    return bool(item.get("rewriting")) and datetime.now(UTC) - datetime.fromisoformat(item["rewriting"]) < REWRITE_WAIT


def create_meeting(owner, params, body) -> dict:
    """Registers a recording and hands back where to upload it. Calling it again for one still uploading, or one
    that failed (Send again), gives a fresh URL; for one already on its way or ready it refuses, so a repeated send
    can never start a second run or overwrite what the owner confirmed."""
    meeting_id = params["id"]
    existing = meeting_of(owner, meeting_id)
    if existing and summary_of(existing)["state"] not in ("uploading", "failed"):
        raise Problem(409, "this meeting has been sent already")
    meta = {k: instant(body.get(k)) for k in ("started_at", "ended_at")} | {"app_version": line(body.get("app_version") or "")}
    meta = {k: v for k, v in meta.items() if v}
    if "started_at" not in meta:
        raise Problem(400, "a meeting has started_at")
    put_json(f"{prefix(owner, meeting_id)}/meta.json", meta)
    # An update, not a new row: sending a failed one again keeps its folders and title
    meetings.update_item(Key={"owner": owner, "meeting_id": meeting_id},
                         UpdateExpression="SET #s = :uploading, updated_at = :t, #e = :none, started_at = :started, ended_at = :ended",
                         ExpressionAttributeNames={"#s": "state", "#e": "error"},
                         ExpressionAttributeValues={":uploading": "uploading", ":t": now(), ":none": None,
                                                    ":started": meta["started_at"], ":ended": meta.get("ended_at")})
    url = s3.generate_presigned_url("put_object", ExpiresIn=3600, Params={
        "Bucket": BUCKET, "Key": f"{prefix(owner, meeting_id)}/audio.m4a", "ContentType": "audio/mp4"})
    return {"upload_url": url, "upload_headers": {"Content-Type": "audio/mp4"}}


def uploaded(owner, params, _body) -> dict:
    """The upload finished: the pipeline has started (S3 told EventBridge), and is queued until a GPU is ready."""
    # Only from uploading: the job may have got there first, and a meeting never moves back.
    with contextlib.suppress(meetings.meta.client.exceptions.ConditionalCheckFailedException):
        meetings.update_item(Key={"owner": owner, "meeting_id": params["id"]}, UpdateExpression="SET #s = :queued, updated_at = :t",
                             ConditionExpression="#s = :uploading", ExpressionAttributeNames={"#s": "state"},
                             ExpressionAttributeValues={":queued": "queued", ":uploading": "uploading", ":t": now()})
    return {"state": meeting(owner, params["id"])["state"]}


def get_meeting(owner, params, _body) -> dict:
    """The meeting, and once it's ready its results: a link to each clip (valid for an hour), and each speaker's
    `candidates`, the owner's people whose voices come closest, as they are now."""
    found = summary_of(meeting(owner, params["id"]))
    if found["state"] == "ready":
        base = prefix(owner, params["id"])
        results = get_json(f"{base}/results/meeting.json")
        close = candidates(owner, params["id"], set(results["speakers"]))
        for name, speaker in results["speakers"].items():
            speaker["clip_urls"] = [s3.generate_presigned_url("get_object", ExpiresIn=3600, Params={"Bucket": BUCKET, "Key": f"{base}/{key}"})
                                    for key in speaker["clips"]]
            speaker["candidates"] = close.get(name, [])
        found["results"] = results
        notes = get_json(f"{base}/notes/summary.json", default={})
        if notes:
            found["notes"] = {"summary": notes}  # the owner's own copy of the summary, shown instead of the agent's
    return found


def delete_meeting(owner, params, _body) -> dict:
    """The meeting and everything Tally learned from it: its files, the voiceprints it gave anyone, and the people
    it alone brought in. A job still running for it notices and publishes nothing."""
    meeting_id = params["id"]
    prints = mine(voiceprints, owner)
    removed = [v for v in prints if v["meeting_id"] == meeting_id]
    for v in removed:
        voiceprints.delete_item(Key={"owner": owner, "voiceprint_id": v["voiceprint_id"]})
    held = {v["person_id"] for v in prints if v["meeting_id"] != meeting_id} | named_elsewhere(owner, meeting_id)
    gone = [p for p in mine(people, owner) if only_from(p, meeting_id) and p["person_id"] not in held]
    for p in gone:
        people.delete_item(Key={"owner": owner, "person_id": p["person_id"]})
    meetings.delete_item(Key={"owner": owner, "meeting_id": meeting_id})
    files = 0
    for page in s3.get_paginator("list_objects_v2").paginate(Bucket=BUCKET, Prefix=f"{prefix(owner, meeting_id)}/"):
        keys = [{"Key": o["Key"]} for o in page.get("Contents", [])]
        if keys:
            s3.delete_objects(Bucket=BUCKET, Delete={"Objects": keys, "Quiet": True})
            files += len(keys)
    return {"files": files, "voiceprints": len(removed), "people": len(gone)}


def assign_speaker(owner, params, body) -> dict:
    """The owner says who a speaker is: an existing person (`person_id`), a new one (`name`, `role`, `company`),
    nobody they know (`unknown`), or a voice that wasn't in the meeting at all (`background`: someone talking
    nearby, kept but set aside). Whoever it was before loses the voiceprint this meeting gave them; a person it
    goes to gets this speaker's voice as a `user` voiceprint, the kind future matches trust."""
    meeting_id, speaker = params["id"], params["speaker"]
    ready_meeting(owner, meeting_id)
    key = f"{prefix(owner, meeting_id)}/results/meeting.json"
    results, etag = get_json_tagged(key)
    if speaker not in results["speakers"]:
        raise Problem(404, f"no speaker {speaker}")
    previous = (results["speakers"][speaker].get("identity") or {}).get("person_id")

    # Who it is, before anything changes: a bad request leaves the meeting as it was.
    person = new = None
    if body.get("background"):  # a voice nearby, not in the meeting, whatever else the body says: no voiceprint
        identity = {"unknown": True, "background": True, "reason": "Not in the meeting: a voice nearby", "by": "owner"}
    elif body.get("person_id"):
        person = person_of(owner, body["person_id"])
    elif name := line(body.get("name") or ""):
        person = new = {"owner": owner, "person_id": f"p_{ulid()}", "name": name, "aliases": [], "is_owner": False,
                        "first_meeting_id": meeting_id, "created_at": now(), "edited": True,
                        **{k: v for k in ("role", "company") if (v := line(body.get(k) or ""))}}
    elif body.get("unknown"):
        identity = {"unknown": True, "reason": "The owner doesn't know who this is", "by": "owner"}
    else:
        raise Problem(400, "give person_id, name, unknown or background")
    if person:
        identity = {"name": person["name"], "person_id": person["person_id"], "confidence": "high", "by": "owner",
                    "evidence": [{"kind": "other", "text": "confirmed by the owner"}], "suggestion": False}

    # meeting.json first, and only over the version read, so two quick confirmations don't undo each other; voiceprints
    # and people change once it's saved.
    for _ in range(5):
        results["speakers"][speaker]["identity"] = identity
        try:
            put_json(key, results, if_match=etag)
            break
        except s3.exceptions.ClientError as e:
            if e.response["Error"]["Code"] not in ("PreconditionFailed", "ConditionalRequestConflict"):
                raise
            results, etag = get_json_tagged(key)
    else:
        raise Problem(409, "this meeting changed while saving. Try again")

    for v in mine(voiceprints, owner):
        if v["meeting_id"] == meeting_id and v.get("speaker") == speaker:
            voiceprints.delete_item(Key={"owner": owner, "voiceprint_id": v["voiceprint_id"]})
    if person:
        if new:
            people.put_item(Item=new)
        save_voiceprint(owner, person["person_id"], meeting_id, speaker)
    if previous and previous != identity.get("person_id"):
        prune(owner, previous, meeting_id, results)
    remember_people(owner, meeting_id, results)
    touched(owner, meeting_id)
    return {"speaker": speaker, "identity": identity}


def remember_people(owner: str, meeting_id: str, results: dict) -> None:
    """Who spoke, on the meeting's row: a person's meetings come straight from the list. From meeting.json as it is
    now, not the copy this request wrote: a confirmation that landed meanwhile counts too."""
    results = get_json(f"{prefix(owner, meeting_id)}/results/meeting.json", default=results)
    found = sorted({s["identity"]["person_id"] for s in results["speakers"].values() if (s.get("identity") or {}).get("person_id")})
    change(owner, meeting_id, UpdateExpression="SET people = :p", ExpressionAttributeValues={":p": found})


def candidates(owner: str, meeting_id: str, speakers: set[str]) -> dict[str, list]:
    """For each of the meeting's speakers, the people whose voiceprints come closest to their voice (cosine similarity
    of L2-normalised embeddings, each person's best), so the owner can pick the right one among voices that sound
    alike. Computed when asked, so people confirmed since the meeting count too."""
    embeddings = {k: v for k, v in read_embeddings(owner, meeting_id).items() if k in speakers}
    names = {p["person_id"]: p["name"] for p in mine(people, owner)}
    prints = [(v["person_id"], array("f", v["embedding"].value)) for v in mine(voiceprints, owner)
              if v.get("model") == MODEL and v.get("dimension") == DIMENSION and v["person_id"] in names]
    close = {}
    for speaker, voice in embeddings.items():
        best: dict[str, float] = {}
        for person_id, vector in prints:
            score = sum(a * b for a, b in zip(voice, vector, strict=True))
            best[person_id] = max(score, best.get(person_id, -1.0))
        ranked = sorted(best.items(), key=lambda item: -item[1])[:CANDIDATES]
        close[speaker] = [{"person_id": p, "name": names[p], "score": round(s, 3)} for p, s in ranked]
    return close


def save_voiceprint(owner: str, person_id: str, meeting_id: str, speaker: str) -> None:
    voice = read_embeddings(owner, meeting_id).get(speaker)
    if voice is None:
        return
    stats = get_json(f"{prefix(owner, meeting_id)}/work/speakers.json")["speakers"][speaker]
    voiceprints.put_item(Item={
        "owner": owner, "voiceprint_id": f"v_{ulid()}", "person_id": person_id, "embedding": voice.tobytes(),
        "dimension": DIMENSION, "model": MODEL, "source": "user", "meeting_id": meeting_id, "speaker": speaker,
        "start": Decimal(str(stats["first_seen"])), "quality": Decimal(str(stats["clean_speech"])), "created_at": now()})


def prune(owner: str, person_id: str, meeting_id: str, results: dict) -> None:
    """A person the agent brought in with this meeting, whom the owner has now said it wasn't: gone, unless anything
    else holds on to them."""
    person = people.get_item(Key={"owner": owner, "person_id": person_id}).get("Item")
    if not person or not only_from(person, meeting_id):
        return
    if any((s.get("identity") or {}).get("person_id") == person_id for s in results["speakers"].values()):
        return
    if any(v["person_id"] == person_id for v in mine(voiceprints, owner)) or person_id in named_elsewhere(owner, meeting_id):
        return
    people.delete_item(Key={"owner": owner, "person_id": person_id})


def only_from(person: dict, meeting_id: str) -> bool:
    """A person the agent brought in with this meeting, whom the owner hasn't made their own."""
    return person.get("first_meeting_id") == meeting_id and not person.get("is_owner") and not person.get("edited")


def named_elsewhere(owner: str, meeting_id: str) -> set[str]:
    """The people the owner's other meetings name a speaker as (their rows' `people`): someone named at medium
    confidence has no voiceprint there, but that meeting still holds on to them."""
    return {p for m in mine(meetings, owner) if m["meeting_id"] != meeting_id for p in m.get("people") or []}


# People


def person_of(owner: str, person_id: str) -> dict:
    person = people.get_item(Key={"owner": owner, "person_id": person_id}).get("Item")
    if not person:
        raise Problem(404, "this person is no longer in People")
    return person


def list_people(owner, _params, _body) -> dict:
    prints: dict[str, list] = {}
    for v in mine(voiceprints, owner):
        prints.setdefault(v["person_id"], []).append(
            {"voiceprint_id": v["voiceprint_id"], "source": v["source"], "meeting_id": v["meeting_id"], "created_at": v.get("created_at")})
    found = [{"person_id": p["person_id"], "name": p["name"], "aliases": list(p.get("aliases", [])), "role": p.get("role"),
              "company": p.get("company"), "is_owner": bool(p.get("is_owner")), "voiceprints": prints.get(p["person_id"], [])}
             for p in mine(people, owner)]
    return {"people": sorted(found, key=lambda p: p["name"].casefold())}


def update_person(owner, params, body) -> dict:
    """Name, role, company, and whether this is the owner. An empty role or company clears it. A new name
    keeps the old one as an alias, so the agent still recognises it in meetings."""
    person = person_of(owner, params["id"])
    name = line(body.get("name") or "") or person["name"]
    aliases = list(person.get("aliases", []))
    if name != person["name"] and person["name"] not in aliases:
        aliases.append(person["name"])
    person |= {"name": name, "aliases": [a for a in aliases if a.casefold() != name.casefold()], "edited": True}
    for field in ("role", "company"):
        if field in body:
            value = line(body[field] or "")
            if value:
                person[field] = value
            else:
                person.pop(field, None)
    if "is_owner" in body:
        person["is_owner"] = bool(body["is_owner"])
        if person["is_owner"]:  # one of an owner's people is them
            for other in mine(people, owner):
                if other.get("is_owner") and other["person_id"] != person["person_id"]:
                    people.update_item(Key={"owner": owner, "person_id": other["person_id"]}, UpdateExpression="SET is_owner = :f",
                                       ExpressionAttributeValues={":f": False})
    people.put_item(Item=person)
    return {"person_id": person["person_id"]}


def delete_person(owner, params, _body) -> dict:
    for v in mine(voiceprints, owner):
        if v["person_id"] == params["id"]:
            voiceprints.delete_item(Key={"owner": owner, "voiceprint_id": v["voiceprint_id"]})
    people.delete_item(Key={"owner": owner, "person_id": params["id"]})
    return {"person_id": params["id"]}


# The owner's copy of a summary


SUMMARY_BYTES = 200_000
SUMMARY_SECONDS = 172_800  # two days: a time every app can show
TEXT_CHARACTERS = 2000  # the pipeline's limits (pipeline/tally/summary.py)
TOKENS = 20  # {S2}s in one text: each becomes a name of up to 100 characters
TITLE = 200  # a summary's title, as the meeting's row keeps it
TOKEN = re.compile(r"\{(S\d+)\}")  # a speaker, in a summary's text
REWRITE_WAIT = timedelta(minutes=30)  # a rewrite asked for longer ago than this has stopped without saying: ask again


def save_summary(owner, params, body) -> dict:
    """The owner's edited summary: kept beside the agent's, never over it, and shown instead of it. Its title is the
    meeting's title in the list. Only the shape is checked (`summary_kept`): what it says is the owner's."""
    if rewriting(ready_meeting(owner, params["id"])):
        raise Problem(409, "the summary is being rewritten: edit it once that's done")
    found = summary_kept(body.get("summary"))
    if found is None or len(json.dumps(found)) > SUMMARY_BYTES:
        raise Problem(400, "a summary has a title, an overview and its lists, of the fields the apps read")
    put_json(f"{prefix(owner, params['id'])}/notes/summary.json", found)
    retitle(owner, params["id"], found["title"].strip() or "Untitled meeting")
    touched(owner, params["id"])
    return {"saved": True}


def text(value) -> bool:
    if not isinstance(value, str):
        return False
    if len(value) > TEXT_CHARACTERS or len(TOKEN.findall(value)) > TOKENS:  # said, so the owner knows what to cut
        raise Problem(400, f"a summary's text is at most {TEXT_CHARACTERS:,} characters, naming speakers at most {TOKENS} times")
    return True


def texts(value) -> bool:
    return isinstance(value, list) and all(text(v) for v in value)


def day(value) -> bool:  # YYYY-MM-DD this century: what the apps' calendars read
    try:
        return isinstance(value, str) and bool(re.fullmatch(r"20\d\d-\d\d-\d\d", value)) and bool(date.fromisoformat(value))
    except ValueError:
        return False


def seconds(value) -> bool:
    return type(value) in (int, float) and 0 <= value <= SUMMARY_SECONDS


def maybe(check):
    return lambda value: value is None or check(value)


SUMMARY_FIELDS = {  # the fields the apps read of each list's items
    "decisions": {"text": text, "by": texts},
    "action_items": {"owner": maybe(text), "text": text, "due": maybe(day), "done": maybe(lambda v: isinstance(v, bool))},
    "open_questions": {"text": text, "raised_by": texts},
    "topics": {"title": text, "start": seconds, "end": seconds, "notes": texts, "outcome": maybe(text)},
    "attendees": {"speaker": text, "role": text, "contribution": text},
}


def summary_kept(found) -> dict | None:
    """The owner's summary with only the fields the apps read, or None when it isn't in their shape. A text too long,
    or naming speakers too often, is refused with why."""
    if not (isinstance(found, dict) and isinstance(found.get("title"), str) and text(found.get("overview")) and texts(found.get("summary"))):
        return None
    kept = {"title": found["title"][:TITLE], "overview": found["overview"], "summary": found["summary"]}
    for key, fields in SUMMARY_FIELDS.items():
        items = found.get(key)
        if not isinstance(items, list) or not all(isinstance(i, dict) and all(ok(i.get(f)) for f, ok in fields.items()) for i in items):
            return None
        kept[key] = [{f: i[f] for f in fields if f in i} for i in items]
    return kept


def restore_summary(owner, params, _body) -> dict:
    """Back to the agent's summary: the owner's copy goes, and the title is the agent's again."""
    meeting(owner, params["id"])
    base = prefix(owner, params["id"])
    s3.delete_object(Bucket=BUCKET, Key=f"{base}/notes/summary.json")
    retitle(owner, params["id"], get_json(f"{base}/results/meeting.json")["summary"]["title"])
    touched(owner, params["id"])
    return {"restored": True}


def rewrite_summary(owner, params, body) -> dict:
    """The summary written again without the voices the owner set aside as background, or with everyone
    again when they've taken a voice back: a summary-only job, which leaves who's who as the owner said. One at a
    time. The owner's own copy of the summary would be replaced, so that takes their say-so (`replace`); the job
    removes it only once the new summary is written, so a rewrite that fails loses nothing."""
    meeting_id = params["id"]
    if rewriting(ready_meeting(owner, meeting_id)):
        raise Problem(409, "the summary is being rewritten already")
    base = prefix(owner, meeting_id)
    results = get_json(f"{base}/results/meeting.json")
    background = {k for k, s in results["speakers"].items() if (s.get("identity") or {}).get("background")}
    if background == set(results.get("summary_without") or []):
        raise Problem(400, "the summary was already written without the background voices")
    if get_json(f"{base}/notes/summary.json", default={}) and not body.get("replace"):
        raise Problem(409, "your edited summary would be replaced")
    change(owner, meeting_id, UpdateExpression="SET rewriting = :t REMOVE rewrite_error, rewrite_job", ExpressionAttributeValues={":t": now()})
    try:
        batch.submit_job(jobName="tally-rewrite", jobQueue=os.environ["TALLY_JOB_QUEUE"], jobDefinition=os.environ["TALLY_JOB_DEFINITION"],
                         parameters={"key": f"{base}/rewrite"})
    except Exception:  # no job will clear the request: it goes now, with why, so the owner can ask again
        change(owner, meeting_id, UpdateExpression="SET rewrite_error = :e REMOVE rewriting",
               ExpressionAttributeValues={":e": "The summary couldn't be rewritten. Try again in a moment."})
        raise
    touched(owner, meeting_id)
    return {"rewriting": True}


def retitle(owner: str, meeting_id: str, title: str) -> None:
    change(owner, meeting_id, UpdateExpression="SET title = :t", ExpressionAttributeValues={":t": title})


def touched(owner: str, meeting_id: str) -> None:
    """The owner changed this meeting (its summary, who spoke, its folders or files): `edited_at`, so a copy kept
    elsewhere (the app's meetings folder) knows to fetch it again. `updated_at` stays the job's."""
    change(owner, meeting_id, UpdateExpression="SET edited_at = :e", ExpressionAttributeValues={":e": now()})


def change(owner: str, meeting_id: str, **update) -> None:
    """An update to a meeting's row, if it's still there: one deleted meanwhile doesn't come back as an empty row."""
    with contextlib.suppress(meetings.meta.client.exceptions.ConditionalCheckFailedException):
        meetings.update_item(Key={"owner": owner, "meeting_id": meeting_id}, ConditionExpression="attribute_exists(meeting_id)", **update)


# Folders: the owner's own, nested; a meeting can be in any number of them, and is always in Meetings, which isn't a folder


FOLDER_ID = re.compile(r"^f_[0-9A-HJKMNP-TV-Z]{26}$")
NAME_CHARACTERS = 100


def list_folders(owner, _params, _body) -> dict:
    return {"folders": [{"folder_id": f["folder_id"], "name": f["name"], "parent": f.get("parent")} for f in mine(folders, owner)]}


def save_folder(owner, params, body) -> dict:
    """A folder made or changed: its name, and the folder it's in (none: at the top). Ids are the app's (f_<ulid>),
    so a repeated save is the same folder. A folder can't go inside itself, or inside one inside it."""
    folder_id = params["id"]
    name = (body.get("name") or "").strip()
    parent = body.get("parent")
    if not FOLDER_ID.fullmatch(folder_id) or not 0 < len(name) <= NAME_CHARACTERS:
        raise Problem(400, f"a folder has an id and a name of 1 to {NAME_CHARACTERS} characters")
    if parent is not None:
        tree = {f["folder_id"]: f.get("parent") for f in mine(folders, owner)}
        if parent not in tree:
            raise Problem(404, "that folder is no longer there")
        at, seen = parent, set()
        while at is not None:  # up from the new parent: this folder mustn't be on the way (nor any loop two saves made)
            if at == folder_id or at in seen:
                raise Problem(400, "a folder can't go inside itself")
            seen.add(at)
            at = tree.get(at)
    folders.put_item(Item={"owner": owner, "folder_id": folder_id, "name": name, **({"parent": parent} if parent else {})})
    return {"folder_id": folder_id}


def delete_folder(owner, params, _body) -> dict:
    """A folder, and the folders inside it. Their meetings aren't deleted: they're just no longer in them."""
    tree = {f["folder_id"]: f.get("parent") for f in mine(folders, owner)}
    gone = {params["id"]}
    while more := {f for f, parent in tree.items() if parent in gone} - gone:  # and the folders inside those
        gone |= more
    for item in mine(meetings, owner):
        if taken := set(item.get("folder_ids") or []) & gone:
            change(owner, item["meeting_id"], UpdateExpression="DELETE folder_ids :f", ExpressionAttributeValues={":f": taken})
            touched(owner, item["meeting_id"])
    for folder in gone & set(tree):
        folders.delete_item(Key={"owner": owner, "folder_id": folder})
    return {"folders": len(gone & set(tree))}


def file_meeting(owner, params, _body) -> dict:
    """A meeting into one of the owner's folders, besides any it's in already: a set, so adding twice is once."""
    meeting(owner, params["id"])
    folder_id = params["folder"]
    if not folders.get_item(Key={"owner": owner, "folder_id": folder_id}).get("Item"):
        raise Problem(404, "that folder is no longer there")
    change(owner, params["id"], UpdateExpression="ADD folder_ids :f", ExpressionAttributeValues={":f": {folder_id}})
    touched(owner, params["id"])
    return {"folder_id": folder_id}


def unfile_meeting(owner, params, _body) -> dict:
    """A meeting out of a folder; it's still in Meetings, and any other folders."""
    meeting(owner, params["id"])
    folder_id = params["folder"]
    change(owner, params["id"], UpdateExpression="DELETE folder_ids :f", ExpressionAttributeValues={":f": {folder_id}})
    touched(owner, params["id"])
    return {"folder_id": folder_id}


# Search


SEARCH_RESULTS = 50
SNIPPET = 60  # characters either side of a match


def search(owner, params, _body) -> dict:
    """The owner's meetings that mention every word of `q`, newest first: in the title, the summary (the owner's copy
    when there is one), the names of who was there, or the transcript. Each with where it matched, a snippet, and
    for the transcript the time. Read from each meeting's files when asked: a few hundred meetings take a second or
    two, with nothing to keep in step."""
    words = (params.get("q") or "").casefold().split()
    if not words:
        return {"results": []}
    names = {p["person_id"]: p["name"] for p in mine(people, owner)}
    ready = sorted((m for m in mine(meetings, owner) if m.get("state") == "ready"), key=lambda m: m["meeting_id"], reverse=True)

    def match(item: dict) -> dict | None:
        try:
            return matched(item)
        except (Problem, KeyError, TypeError, AttributeError, ValueError):  # a meeting that can't be read isn't found, nor stops search
            return None

    def matched(item: dict) -> dict | None:
        base = prefix(owner, item["meeting_id"])
        results = get_json(f"{base}/results/meeting.json")
        summary = get_json(f"{base}/notes/summary.json", default={}) or results["summary"]
        def name(speaker: str, identity: dict) -> str:  # as the apps name them: someone deleted since is unknown again
            found = names.get(identity["person_id"]) if identity.get("person_id") else identity.get("name")
            return found or f"Speaker {speaker[1:]}"

        speakers = {k: name(k, s.get("identity") or {}) for k, s in results["speakers"].items()}

        def say(text: str) -> str:
            return TOKEN.sub(lambda m: speakers.get(m.group(1), m.group(1)), text)

        def found(text: str) -> bool:
            folded = text.casefold()
            return all(w in folded for w in words)

        def snippet(text: str) -> str:
            folded = text.casefold()
            at = min((folded.find(w) for w in words if w in folded), default=0)
            start, end = max(0, at - SNIPPET), min(len(text), at + SNIPPET)
            return ("…" if start else "") + text[start:end].strip() + ("…" if end < len(text) else "")

        hit = {"meeting_id": item["meeting_id"], "title": item.get("title"), "started_at": item.get("started_at")}
        title = summary.get("title") or ""
        if found(title):
            return hit | {"where": "title", "snippet": snippet(title)}
        texts = [say(t) for t in [title, summary.get("overview", ""), *summary.get("summary", []),
                                  *(d["text"] for d in summary.get("decisions", [])), *(a["text"] for a in summary.get("action_items", [])),
                                  *(q["text"] for q in summary.get("open_questions", [])),
                                  *(n for t in summary.get("topics", []) for n in [t["title"], *t["notes"], t.get("outcome") or ""])]]
        if found(" ".join(texts)):
            best = next((t for t in texts if found(t)), next(t for t in texts if any(w in t.casefold() for w in words)))
            return hit | {"where": "summary", "snippet": snippet(best)}
        if found(" ".join(speakers.values())):
            return hit | {"where": "people", "snippet": ", ".join(speakers.values())}
        for segment in results["segments"]:
            if found(segment["text"]):
                return hit | {"where": "transcript", "snippet": f"{speakers.get(segment['speaker'], '')}: {snippet(segment['text'])}",
                              "at": segment["start"]}
        if found(" ".join(s["text"] for s in results["segments"])):  # the words said in different lines
            return hit | {"where": "transcript", "snippet": snippet(" ".join(s["text"] for s in results["segments"]))}
        return None

    with ThreadPoolExecutor(max_workers=16) as pool:
        hits = [h for h in pool.map(match, ready) if h]
    return {"results": hits[:SEARCH_RESULTS]}


# Files: what the owner adds to a meeting, kept beside it


FILE_BYTES = 200_000_000
FILE_NAME_CHARACTERS = 200


def file_name(name: str) -> str:
    """A file's name as it's kept: in its key, after its id. Never a `/`, so nothing under `files/` is ever
    `.../audio.*`, the key whose upload starts the pipeline."""
    name = name.strip()
    # Control characters out, not other unusual ones: macOS names a screenshot with a narrow no-break space.
    if (not 0 < len(name) <= FILE_NAME_CHARACTERS or any(c in name for c in "/\\") or name in (".", "..")
            or any(unicodedata.category(c) == "Cc" for c in name)):
        raise Problem(400, f"a file's name is 1 to {FILE_NAME_CHARACTERS} characters, without slashes")
    return name


def list_files(owner, params, _body) -> dict:
    """A meeting's files, oldest first, each with an hour's download link. New ones move `edited_at`, so another
    computer's meetings folder fetches them (the apps list a meeting's files once an upload is done)."""
    row = meeting(owner, params["id"])
    base = f"{prefix(owner, params['id'])}/files/"
    found = []
    for page in s3.get_paginator("list_objects_v2").paginate(Bucket=BUCKET, Prefix=base):
        for item in page.get("Contents", []):
            file_id, name = item["Key"].removeprefix(base).split("-", 1)
            found.append({"file_id": file_id, "name": name, "size": item["Size"], "added_at": item["LastModified"].strftime("%Y-%m-%dT%H:%M:%SZ"),
                          "url": s3.generate_presigned_url("get_object", ExpiresIn=3600, Params={"Bucket": BUCKET, "Key": item["Key"]})})
    found.sort(key=lambda f: f["file_id"])
    ids = [f["file_id"] for f in found]
    if ids != row.get("file_ids", []):
        # edited_at is to the second: a file added in the second another computer looked must still move it
        edited = max(now(), later(row.get("edited_at")))
        change(owner, params["id"], UpdateExpression="SET file_ids = :f, edited_at = :e", ExpressionAttributeValues={":f": ids, ":e": edited})
    return {"files": found}


def add_file(owner, params, body) -> dict:
    """Where to upload a file for a meeting: a presigned PUT for exactly the size the app says, up to 200 MB, so
    S3 itself refuses anything bigger."""
    meeting(owner, params["id"])
    name, size = file_name(body.get("name") or ""), body.get("size")
    if not isinstance(size, int) or not 0 < size <= FILE_BYTES:
        raise Problem(400, "a file is 1 byte to 200 MB")
    content_type = body.get("type") or "application/octet-stream"
    file_id = "a_" + ulid()
    url = s3.generate_presigned_url("put_object", ExpiresIn=3600, Params={
        "Bucket": BUCKET, "Key": f"{prefix(owner, params['id'])}/files/{file_id}-{name}", "ContentType": content_type, "ContentLength": size})
    touched(owner, params["id"])
    return {"file_id": file_id, "upload_url": url, "upload_headers": {"Content-Type": content_type}}


def delete_file(owner, params, _body) -> dict:
    listed = s3.list_objects_v2(Bucket=BUCKET, Prefix=f"{prefix(owner, params['id'])}/files/{params['file']}-")
    for item in listed.get("Contents", []):
        s3.delete_object(Bucket=BUCKET, Key=item["Key"])
    touched(owner, params["id"])
    return {"file_id": params["file"]}


# The admin dashboard


def admin_stats(owner, params, _body) -> dict:
    """For Tally's admins (`admin` in the token's claim at config's `auth.adminClaim`): usage, everyone's counts, failures, how long summaries
    take, the GPU queue, crash reports and the month's cost. Counts and timings only: never a title, a transcript
    or a name from inside a meeting."""
    if not params["admin"]:
        raise Problem(403, "this is for Tally's admins")
    rows = [summary_of(m) | {"owner": m["owner"], "agent_runs": m.get("agent_runs")} for m in scan(meetings)]
    owners = {p["owner"]: p["name"] for p in scan(people) if p.get("is_owner")}  # their own name, in their People
    here = datetime.now(ZoneInfo(os.environ["TALLY_TIMEZONE"]))
    today = here.replace(hour=0, minute=0, second=0, microsecond=0)  # local midnight: "today" as the owner means it
    found = admin.stats(rows, scan(users), owners, datetime.now(UTC), today)
    week = datetime.now(UTC) - timedelta(days=7)
    crashes = [o for page in s3.get_paginator("list_objects_v2").paginate(Bucket=BUCKET, Prefix="crashes/")
               for o in page.get("Contents", []) if o["LastModified"] >= week]
    queue = {s.lower(): len(batch.list_jobs(jobQueue=os.environ["TALLY_JOB_QUEUE"], jobStatus=s)["jobSummaryList"])
             for s in ("RUNNABLE", "STARTING", "RUNNING")}
    gpu = batch.describe_compute_environments(computeEnvironments=[os.environ["TALLY_COMPUTE_ENVIRONMENT"]])["computeEnvironments"][0]
    cost = month_cost()
    model = admin.model_use(rows, datetime.fromisoformat(cost["since"]).replace(tzinfo=UTC), json.loads(os.environ["TALLY_MODEL_PRICES"]))
    cost["services"] = sorted([*cost["services"], {"service": "The model (Bedrock, from its tokens)", "usd": model["usd"]}], key=lambda s: -s["usd"])
    cost["total"] = round(cost["total"] + model["usd"], 2)
    return found | {"crashes_week": len(crashes), "queue": queue | {"warm": gpu["computeResources"].get("minvCpus", 0) > 0,
                                                                     "vcpus": gpu["computeResources"].get("desiredvCpus", 0)},
                    "cost": cost, "model": model}


GPU_SERVICE = "Amazon Elastic Compute Cloud - Compute"


def month_cost() -> dict:
    """This month's AWS cost so far, from Cost Explorer (a day behind). The GPU by what it is: the instance types
    Tally's Batch environment launches, in its region (anything else running those types is counted too). The rest
    by its tag, project=tally, less EC2 compute, so the GPU isn't counted twice."""
    today = datetime.now(UTC).date()
    period = {"Start": today.replace(day=1).isoformat(), "End": (today + timedelta(days=1)).isoformat()}

    def cost(filter: dict, **group) -> dict:
        return ce.get_cost_and_usage(TimePeriod=period, Granularity="MONTHLY", Metrics=["UnblendedCost"], Filter=filter,
                                     **group)["ResultsByTime"][0]

    gpu = float(cost({"And": [{"Dimensions": {"Key": "REGION", "Values": [os.environ["AWS_REGION"]]}},
                        {"Dimensions": {"Key": "SERVICE", "Values": [GPU_SERVICE]}},
                        {"Dimensions": {"Key": "INSTANCE_TYPE", "Values": os.environ["TALLY_INSTANCE_TYPES"].split(",")}}]})["Total"]["UnblendedCost"]["Amount"])
    tagged = [(g["Keys"][0], float(g["Metrics"]["UnblendedCost"]["Amount"]))
              for g in cost({"And": [{"Tags": {"Key": "project", "Values": ["tally"]}},
                                     {"Not": {"Dimensions": {"Key": "SERVICE", "Values": [GPU_SERVICE]}}}]},
                            GroupBy=[{"Type": "DIMENSION", "Key": "SERVICE"}])["Groups"]]
    services = sorted([("GPU instances (AWS Batch)", gpu), *tagged], key=lambda s: -s[1])
    return {"since": period["Start"], "total": round(sum(c for _, c in services), 2),
            "services": [{"service": name, "usd": round(c, 2)} for name, c in services[:6] if c >= 0.01]}


def scan(table) -> list[dict]:
    """Every row, everyone's: for the admin dashboard only."""
    return every(table.scan)


# Crashes


def report_crash(owner, params, body) -> dict:
    """A crash report the app found on someone's Mac or PC, sent with their say-so: kept under `crashes/<sub>/`, and its
    gist emailed to the deployment's owner. Reports hold stack traces and versions, no meeting content. Who it's from is
    the token's, never the request's."""
    report = body.get("report") or ""
    if not report or len(report.encode()) > REPORT_BYTES:
        raise Problem(400, "a crash report is 1 byte to 2 MB of text")
    key = f"crashes/{owner}/{ulid()}.ips"
    s3.put_object(Bucket=BUCKET, Key=key, Body=report.encode(), ContentType="text/plain")
    who, version = plain_line(params["signed_in_as"]), plain_line(body.get("app_version") or "?")
    sns.publish(TopicArn=os.environ["TALLY_CRASH_TOPIC"], Subject=f"Tally crashed for {who}"[:99],  # SNS: under 100, one line
                Message="\n".join([f"Tally crashed on {who}'s computer (app {version}).", "", *gist(report), "",
                                    f"The whole report: s3://{BUCKET}/{key}"]))
    return {"key": key}


def plain_line(text: str) -> str:
    """Text for an email's subject or a line of it: control characters and line breaks out, and not too long."""
    return "".join(c for c in text if unicodedata.category(c) not in ("Cc", "Zl", "Zp"))[:200]


def gist(report: str) -> list[str]:
    """What crashed, from a macOS .ips report (a JSON header line, then a JSON body): when, the versions, the exception
    and the top of the crashed thread. The Windows app's own report is plain text already: its first lines. Whatever
    can't be read is said, not raised."""
    if not report.startswith("{"):
        return [plain_line(text) for text in report.splitlines()[:16]]
    try:
        header, rest = report.split("\n", 1)
        head, crash = json.loads(header), json.loads(rest)
        thread = next(t for t in crash["threads"] if t.get("triggered"))
        images = crash["usedImages"]
        frames = [plain_line(f"  {images[f['imageIndex']].get('name', '?')}  {f.get('symbol', '?')}") for f in thread["frames"][:12]]
        exception = crash.get("exception", {})
        return [f"When: {head.get('timestamp', '?')}", f"macOS: {crash.get('osVersion', {}).get('train', '?')}",
                f"Exception: {exception.get('type', '?')} {exception.get('signal', '')}".rstrip(), "Crashed thread:", *frames]
    except Exception as e:  # an unexpected shape: the report itself is kept all the same
        return [f"(The report couldn't be summarised: {type(e).__name__}.)"]


ROUTES = {
    "GET /meetings": list_meetings,
    "PUT /meetings/{id}": create_meeting,
    "POST /meetings/{id}/uploaded": uploaded,
    "GET /meetings/{id}": get_meeting,
    "DELETE /meetings/{id}": delete_meeting,
    "PUT /meetings/{id}/speakers/{speaker}": assign_speaker,
    "PUT /meetings/{id}/summary": save_summary,
    "POST /meetings/{id}/summary/rewrite": rewrite_summary,
    "DELETE /meetings/{id}/summary": restore_summary,
    "GET /people": list_people,
    "PATCH /people/{id}": update_person,
    "DELETE /people/{id}": delete_person,
    "PUT /meetings/{id}/folders/{folder}": file_meeting,
    "DELETE /meetings/{id}/folders/{folder}": unfile_meeting,
    "GET /folders": list_folders,
    "PUT /folders/{id}": save_folder,
    "DELETE /folders/{id}": delete_folder,
    "GET /search": search,
    "GET /meetings/{id}/files": list_files,
    "POST /meetings/{id}/files": add_file,
    "DELETE /meetings/{id}/files/{file}": delete_file,
    "POST /crashes": report_crash,
    "GET /admin/stats": admin_stats,
}


# Helpers


def read_embeddings(owner: str, meeting_id: str) -> dict[str, array]:
    """Each speaker's centroid from the meeting's embeddings.npz (a zip of .npy files), read by hand."""
    try:
        data = s3.get_object(Bucket=BUCKET, Key=f"{prefix(owner, meeting_id)}/work/embeddings.npz")["Body"].read()
        with zipfile.ZipFile(io.BytesIO(data)) as npz:
            found = {i.filename.removesuffix(".npy"): centroid_of(npz.read(i)) for i in npz.infolist()}
    except Exception:  # missing, or the agent wrote something that isn't a zip of .npy files
        return {}
    return {k: v for k, v in found.items() if v is not None}


def centroid_of(npy: bytes) -> array | None:
    """DIMENSION float32s of length 1, as the model makes them. The agent can write the file, and a voiceprint saved
    from anything else would spoil matching in every later meeting (a NaN fails the length check too)."""
    start = 10 + struct.unpack("<H", npy[8:10])[0]  # after the .npy header
    if not npy.startswith(b"\x93NUMPY\x01") or b"'descr': '<f4'" not in npy[10:start] or len(npy) - start != 4 * DIMENSION:
        return None
    centroid = array("f", npy[start:])
    return centroid if abs(sum(x * x for x in centroid) - 1) < 0.02 else None


def get_json(key: str, default=None):
    try:
        return json.loads(s3.get_object(Bucket=BUCKET, Key=key)["Body"].read())
    except s3.exceptions.NoSuchKey:
        if default is not None:
            return default
        raise Problem(404, "not found") from None


def get_json_tagged(key: str) -> tuple[dict, str]:
    """A JSON object and its ETag, for a write that mustn't overwrite someone else's (`put_json(if_match=)`)."""
    try:
        found = s3.get_object(Bucket=BUCKET, Key=key)
    except s3.exceptions.NoSuchKey:
        raise Problem(404, "not found") from None
    return json.loads(found["Body"].read()), found["ETag"]


def put_json(key: str, value, if_match: str | None = None) -> None:
    s3.put_object(Bucket=BUCKET, Key=key, Body=json.dumps(value, default=plain), ContentType="application/json",
                  **({"IfMatch": if_match} if if_match else {}))


def mine(table, owner: str) -> list[dict]:
    """Every row of one owner's: the tables are keyed by owner first."""
    return every(table.query, KeyConditionExpression=Key("owner").eq(owner))


def every(read, **kwargs) -> list[dict]:
    """All the pages of a query or a scan."""
    items = []
    while True:
        page = read(**kwargs)
        items += page["Items"]
        if "LastEvaluatedKey" not in page:
            return items
        kwargs["ExclusiveStartKey"] = page["LastEvaluatedKey"]


def later(stamp: str | None) -> str:
    """A second after a time `now()` wrote (or nothing, before any)."""
    return (datetime.fromisoformat(stamp) + timedelta(seconds=1)).isoformat(timespec="seconds") if stamp else ""


def now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


def ulid() -> str:
    value = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10))
    return "".join(CROCKFORD[(value >> shift) & 31] for shift in range(125, -1, -5))
