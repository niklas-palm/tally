"""People and their voiceprints: matching a speaker's embedding against them, and the DynamoDB tables they live in.

A few hundred vectors need no vector database: the job loads the owner's people and voiceprints into people.json
before prepare, and matching is a cosine similarity in memory. Voiceprints are only comparable within one embedding model,
so each records its model and only those of the current one are matched.
"""

import contextlib
import unicodedata
from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal

import boto3
import numpy as np
from boto3.dynamodb.conditions import Key

EMBEDDING_MODEL = "pyannote/speaker-diarization-community-1"
DIMENSION = 256  # the model's embeddings
TOP = 5  # people listed per speaker: the agent may pick a lower one when the meeting supports it
NO_MATCH = 0.5  # below it, a voice isn't a match at all (the prompt says so; measured: different people scored at most 0.49)
LINE = 100  # a name, role or company: one printable line of at most this many characters, as the API allows
SHARED = "shared:"  # a person from the organisation's pool (config.json's sharedVoices), by their id there


def line(text: str) -> str | None:
    """A name, role or company as it may be kept: one line, its spaces made single, at most LINE characters; None when
    it can't be. They reach prompts, other people's with shared voices, so the agent is held to what the
    API holds the owner to."""
    joined = " ".join(text.split())
    if not 0 < len(joined) <= LINE or any(map(invisible, text)):
        return None
    return joined


def invisible(c: str) -> bool:
    """A character that's no part of a visible name: a control, a line break, unassigned or private, or a format
    character (invisible tags a model reads, direction overrides), save the joiners some scripts need."""
    category = unicodedata.category(c)
    return category in ("Cc", "Cs", "Co", "Cn", "Zl", "Zp") or category == "Cf" and c not in "\u200c\u200d"


def voiceless(people: list[dict]) -> list[dict]:
    """People without their voiceprints, as the agent's folder holds them: a voiceprint's one place is the tables,
    where deleting a person deletes it."""
    return [p | {"voiceprints": []} for p in people]


def match(embedding: np.ndarray, people: list[dict]) -> list[dict]:
    """The people whose voiceprints come closest to the embedding: each person's best cosine similarity, and the
    sources of their voiceprints (a match resting only on `auto` ones is a suggestion until the owner confirms). A
    colleague's person from the shared pool only on a real match, at least NO_MATCH: they aren't the owner's to list."""
    found = []
    for person in people:
        prints = [v for v in person["voiceprints"] if v["model"] == EMBEDDING_MODEL]
        if prints:
            score = max(float(np.dot(embedding, np.asarray(v["embedding"], dtype=np.float32))) for v in prints)
            if person.get("shared") and score < NO_MATCH:
                continue
            found.append({"person_id": person["person_id"], "name": person["name"], "score": round(score, 3),
                          "sources": sorted({v["source"] for v in prints})})
    return sorted(found, key=lambda m: -m["score"])[:TOP]


def sound(embedding: np.ndarray) -> bool:
    """A centroid fit to be a voiceprint: the model's size, finite, L2-normalised. embeddings.npz is in the agent's
    working directory (merge rewrites it), so publish and the API check before they keep one."""
    return embedding.shape == (DIMENSION,) and bool(np.isfinite(embedding).all()) and abs(float(np.linalg.norm(embedding)) - 1) < 0.01


def now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


@dataclass
class Store:
    """One owner's rows in the `people` and `voiceprints` tables, which are keyed by owner first (docs/data.md): a
    meeting is matched only against the people of whoever recorded it."""

    owner: str
    people_table: str
    voiceprints_table: str
    region: str

    def tables(self):
        dynamodb = boto3.resource("dynamodb", region_name=self.region)
        return dynamodb.Table(self.people_table), dynamodb.Table(self.voiceprints_table)

    def load(self) -> list[dict]:
        """Every person, each with their voiceprints: what prepare matches against (people.json before prepare), and what
        publish asks whose voice is confirmed."""
        people_table, voiceprints_table = self.tables()
        people = {p["person_id"]: {"person_id": p["person_id"], "name": p["name"], "aliases": list(p.get("aliases", [])),
                                   "role": p.get("role"), "company": p.get("company"),
                                   "is_owner": bool(p.get("is_owner", False)), "voiceprints": []}
                  for p in query(people_table, self.owner)}
        for v in query(voiceprints_table, self.owner):
            if v["person_id"] in people:
                people[v["person_id"]]["voiceprints"].append(voiceprint(v))
        return list(people.values())

    def shared(self, mine: list[dict]) -> list[dict]:
        """Everyone else's named people with voiceprints their owner confirmed, for a deployment that shares voices
        (sharedVoices): one entry a name, with those voiceprints, leaving out names this owner already knows by voice
        (`mine`, their own people). A meeting that names one makes them this owner's person when it's published."""
        people_table, voiceprints_table = self.tables()
        others = {p["person_id"]: p for p in scan(people_table) if p["owner"] != self.owner}
        # a person of the owner's without a voice doesn't hide a colleague's: Tally couldn't recognise them otherwise
        known = {name.casefold() for p in mine if p.get("voiceprints") for name in [p["name"], *p["aliases"]]}
        pool: dict[str, dict] = {}
        for v in scan(voiceprints_table):
            person = others.get(v["person_id"])
            # only a voice its owner confirmed: an `auto` one may be the agent's mistake, and it would spread
            if not person or person["owner"] != v["owner"] or v["source"] != "user":
                continue
            # a colleague's name reaches other people's prompts: one that isn't one line is left out, whatever stored it
            name = line(person["name"])
            if not name or name.casefold() in known:
                continue
            entry = pool.setdefault(name.casefold(), {
                "person_id": SHARED + person["person_id"], "name": name, "aliases": [], "role": person.get("role"),
                "company": person.get("company"), "is_owner": False, "shared": True, "voiceprints": []})
            entry["voiceprints"].append(voiceprint(v) | {"meeting_id": None})
        return list(pool.values())

    def put_person(self, person_id: str, name: str, meeting_id: str, role: str | None = None, company: str | None = None) -> None:
        item = {"owner": self.owner, "person_id": person_id, "name": name, "aliases": [], "is_owner": False, "first_meeting_id": meeting_id,
                "created_at": now(), "role": role, "company": company}
        self.tables()[0].put_item(Item={k: v for k, v in item.items() if v is not None})

    def fill_person(self, person_id: str, role: str | None, company: str | None) -> None:
        """Fills in a known person's role and company from a meeting, only where they're still empty: what the
        owner wrote in the app always wins."""
        given = {k: v for k, v in {"role": role, "company": company}.items() if v}
        if not given:
            return
        people = self.tables()[0]
        # Only an existing person: one deleted in the app meanwhile stays deleted.
        with contextlib.suppress(people.meta.client.exceptions.ConditionalCheckFailedException):
            people.update_item(
                Key={"owner": self.owner, "person_id": person_id}, ConditionExpression="attribute_exists(person_id)",
                UpdateExpression="SET " + ", ".join(f"#{k} = if_not_exists(#{k}, :{k})" for k in given),
                ExpressionAttributeNames={f"#{k}": k for k in given}, ExpressionAttributeValues={f":{k}": v for k, v in given.items()})

    def drop_voiceprints(self, meeting_id: str) -> list[dict]:
        """Deletes the voiceprints one meeting gave anyone, and returns the owner's others."""
        voiceprints_table = self.tables()[1]
        kept = []
        for v in query(voiceprints_table, self.owner):
            if v["meeting_id"] == meeting_id:
                voiceprints_table.delete_item(Key={"owner": self.owner, "voiceprint_id": v["voiceprint_id"]})
            else:
                kept.append(v)
        return kept

    def forget(self, meeting_id: str, named: set[str]) -> None:
        """What one meeting added, for a meeting deleted while its job ran: its voiceprints, and the people it alone
        brought in that the owner never touched, nor names in another meeting (`named`; the API's delete does the
        same, docs/api.md)."""
        people_table = self.tables()[0]
        held = {v["person_id"] for v in self.drop_voiceprints(meeting_id)} | named
        for p in query(people_table, self.owner):
            if p.get("first_meeting_id") == meeting_id and p["person_id"] not in held and not p.get("is_owner") and not p.get("edited"):
                people_table.delete_item(Key={"owner": self.owner, "person_id": p["person_id"]})

    def put_voiceprint(self, person_id: str, voiceprint_id: str, embedding: np.ndarray, source: str, meeting_id: str,
                       speaker: str, start: float, end: float, quality: float) -> None:
        self.tables()[1].put_item(Item={
            "owner": self.owner, "voiceprint_id": voiceprint_id, "person_id": person_id, "embedding": embedding.astype(np.float32).tobytes(),
            "dimension": len(embedding), "model": EMBEDDING_MODEL, "source": source, "meeting_id": meeting_id, "speaker": speaker,
            "start": Decimal(str(round(start, 2))), "end": Decimal(str(round(end, 2))), "quality": Decimal(str(round(quality, 1))),
            "created_at": now()})


def voiceprint(row: dict) -> dict:
    """A voiceprint as people.json holds it, from its row."""
    return {"voiceprint_id": row["voiceprint_id"], "source": row["source"], "model": row["model"], "meeting_id": row["meeting_id"],
            "embedding": np.frombuffer(row["embedding"].value, dtype=np.float32).tolist()}


def scan(table) -> list[dict]:
    """Every row, everyone's: only for the shared pool."""
    return pages(table.scan)


def query(table, owner: str) -> list[dict]:
    return pages(table.query, KeyConditionExpression=Key("owner").eq(owner))


def pages(read, **kwargs) -> list[dict]:
    """All of a scan or a query, which DynamoDB answers a page at a time."""
    items = []
    while True:
        page = read(**kwargs)
        items += page["Items"]
        if "LastEvaluatedKey" not in page:
            return items
        kwargs["ExclusiveStartKey"] = page["LastEvaluatedKey"]
