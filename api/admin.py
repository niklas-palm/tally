"""The admin dashboard's figures, counted from the rows tally_api reads across everyone: counts and timings, never
what a meeting says."""

import statistics
from datetime import datetime, timedelta

FAILURES = 20  # the newest failures listed
PERIODS = {"three_days": 3, "week": 7, "month": 30}  # rolling, back from now; "today" is since local midnight
TOKENS = ("input", "output", "cache_read", "cache_write")


def when(text: str | None) -> datetime | None:
    return datetime.fromisoformat(text) if text else None


def model_use(meetings: list[dict], since: datetime, prices: dict) -> dict:
    """The agent's runs since `since` (each meeting's `agent_runs`: its summary, and any rewrites): how many, the
    tokens Strands counted, and what they cost at config.json's `modelPrices` (dollars per million tokens). Bedrock
    bills Anthropic's models through AWS Marketplace, untagged, so this is the model's cost, not Cost Explorer's."""
    runs = [r for m in meetings for r in m.get("agent_runs") or [] if when(r["at"]) >= since]
    tokens = {k: sum(int(r.get(k, 0)) for r in runs) for k in TOKENS}
    return {"runs": len(runs), "rewrites": sum(r["kind"] == "rewrite" for r in runs), **tokens,
            "usd": round(sum(tokens[k] * prices[k] for k in TOKENS) / 1e6, 2)}


def stats(meetings: list[dict], users: list[dict], owners: dict[str, str], now: datetime, today: datetime) -> dict:
    """`meetings` as the app sees them (with `owner`), `users` as the API keeps them, `owners` each person's own name
    in their People (for someone who hasn't synced since `users` began), `today` local midnight."""
    starts = {"today": today} | {name: now - timedelta(days=days) for name, days in PERIODS.items()}
    week, month = starts["week"], starts["month"]
    known = {u["owner"]: u for u in users}
    everyone = sorted(set(known) | {m["owner"] for m in meetings})
    names = {o: (known.get(o, {}).get("name") or known.get(o, {}).get("email") or owners.get(o) or f"Someone ({o[:8]})") for o in everyone}

    def counts(rows: list[dict]) -> dict:
        found = {name: sum((when(m["started_at"]) or now) >= start for m in rows) for name, start in starts.items()}
        return found | {"all": len(rows)}

    usage = {"recorded": counts(meetings), "summarised": counts([m for m in meetings if m["state"] == "ready"]),
             "hours": round(sum(float(m["duration"] or 0) for m in meetings) / 3600, 1)}

    people = []
    for owner in everyone:
        user = known.get(owner, {})
        theirs = [m for m in meetings if m["owner"] == owner]
        # last_seen is their app's last sync, about every minute while it runs: whether Tally is open, not activity
        people.append({"name": names[owner], "email": user.get("email") or None, "meetings": counts(theirs),
                       "last_recorded": max((m["started_at"] for m in theirs if m["started_at"]), default=None),
                       "last_seen": user.get("last_seen"), "app_version": user.get("app_version") or None})
    people.sort(key=lambda p: (-p["meetings"]["week"], -p["meetings"]["all"], p["name"]))

    failed = sorted((m for m in meetings if m["state"] == "failed" and (when(m["updated_at"]) or now) >= week),
                    key=lambda m: m["started_at"] or "", reverse=True)
    failures = [{"name": names[m["owner"]], "started_at": m["started_at"], "error": m["error"]} for m in failed[:FAILURES]]

    # From the end of the recording to its summary: the row's last update, for a ready one, is when it turned ready
    minutes = sorted((when(m["updated_at"]) - when(m["ended_at"])).total_seconds() / 60 for m in meetings
                     if m["state"] == "ready" and m["ended_at"] and m["updated_at"] and when(m["ended_at"]) >= month)
    turnaround = {"meetings": len(minutes), "median": round(statistics.median(minutes), 1) if minutes else None,
                  "slowest": round(minutes[-1], 1) if minutes else None}
    return {"usage": usage, "people": people, "failures": failures, "failed_week": len(failed), "turnaround": turnaround,
            "at": now.isoformat()}
