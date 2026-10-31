"""The harness: one agent per meeting, prompted until `tally check` says the meeting is done.

The harness, not the model, decides when the run is done. After each turn it calls check with code loaded before
the agent started, which nothing the agent edits can reach. Work left means another prompt in the same
conversation. Turns in a row that change nothing fail the run as stalled, and there's a hard cap on turns and on
tool calls. A model call that fails for a passing reason (throttling, a dropped stream) is retried at the turn
level, the conversation carrying on with a note.
"""

import json
import logging
import time
from dataclasses import dataclass
from pathlib import Path

from botocore.config import Config
from strands import Agent
from strands.agent.conversation_manager import NullConversationManager
from strands.hooks import BeforeToolCallEvent
from strands.models import BedrockModel
from strands.models.bedrock import CacheConfig
from strands.tools.executors import SequentialToolExecutor
from strands.types.exceptions import MaxTokensReachedException

from . import check, tools, work
from .config import settings

PROMPTS = Path(__file__).parent / "prompts"
SYSTEM = (PROMPTS / "system.md").read_text() + "\n\n" + (PROMPTS / "summary.md").read_text()
START = "Identify every speaker in this meeting and write its summary, as your instructions describe."
UNANSWERED = "(No reply: the model call failed.)"
SHARED_NOTE = " (from the organisation's shared voices: someone a colleague named, not yet the owner's; name them on the same evidence as anyone)"
LAYOUTS = {"mono": "one channel", "identical": "two identical channels: treat it as one microphone in the room",
           "stereo-split": "channel 1 the owner's microphone, channel 2 the computer's audio"}
RETRY_WAITS = (10, 30, 90)
PASSING = ("ServiceUnavailable", "InternalServer", "ModelStreamError", "ModelNotReady", "Throttl", "ReadTimeout",
           "EndpointConnection", "ConnectionClosed", "ConnectTimeout", "ProtocolError", "IncompleteRead")

log = logging.getLogger("tally")


class RunFailed(Exception):
    pass


@dataclass
class Run:
    turns: int
    tool_calls: int
    seconds: float
    tokens: dict  # the model's, summed over every call: input (uncached), output, cache_read, cache_write

    def record(self, kind: str, at: str) -> dict:
        """The run as the meeting's row keeps it (`agent_runs`), for the admin dashboard's model use and cost."""
        return {"kind": kind, "at": at, "seconds": round(self.seconds), "turns": self.turns} | self.tokens


def tokens(agent: Agent) -> dict:
    """What Strands counted over the whole run: one agent across every turn, so its total is the run's."""
    used = agent.event_loop_metrics.accumulated_usage
    return {"input": used.get("inputTokens", 0), "output": used.get("outputTokens", 0),
            "cache_read": used.get("cacheReadInputTokens", 0), "cache_write": used.get("cacheWriteInputTokens", 0)}


def build_model() -> BedrockModel:
    return BedrockModel(
        model_id=settings.model_id,
        region_name=settings.region,
        max_tokens=32_000,
        additional_request_fields={"thinking": {"type": "adaptive", "display": "summarized"}, "output_config": {"effort": "high"}},
        boto_client_config=Config(connect_timeout=10, read_timeout=600, retries={"max_attempts": 3, "mode": "adaptive"}),
        cache_config=CacheConfig(strategy="auto"),
    )


def system_prompt(path: Path, meta: dict) -> str:
    """The fixed prompt, then this meeting's facts as labelled sections."""
    manifest = work.read("manifest.json", path)
    people = work.people(path)
    speakers = work.read("speakers.json", path)["speakers"]
    meeting = [f"- Date: {meta.get('started_at', 'unknown')}",
               f"- Duration: {work.clock(manifest['duration'])}", f"- Language: {manifest['language']}",
               f"- Channels: {manifest['layout']} ({LAYOUTS[manifest['layout']]})"]
    known = [f"- {quoted(p['name'])}{' (the owner)' if p['is_owner'] else ''}{SHARED_NOTE if p.get('shared') else ''}: "
             + "".join(f"{field} {quoted(p[field])}; " for field in ("role", "company") if p.get(field))
             + f"aliases {', '.join(map(quoted, p['aliases'])) or 'none'}" for p in people] or ["- None yet."]
    found = [f"- {k}: {v['talk_time']} s talk time, {v['turns']} turns, first at {work.clock(v['first_seen'])}, "
             f"best voiceprint matches: {', '.join(f'{quoted(m['name'])} {m['score']}' for m in v['matches']) or 'none'}" for k, v in speakers.items()]
    return "\n\n".join([SYSTEM, "# This meeting\n\n" + "\n".join(meeting), "# Known people\n\n" + "\n".join(known),
                        "# Speakers\n\n" + "\n".join(found)])


def quoted(text: str) -> str:
    """What someone typed (a name, a role, a company), in quotes: set apart from the prompt's own words."""
    return json.dumps(text, ensure_ascii=False)


def passing(e: BaseException | None) -> bool:
    for _ in range(10):
        if e is None:
            return False
        code = (getattr(e, "response", None) or {}).get("Error", {}).get("Code", "")
        # Not case-sensitive: Bedrock's stream says "serviceUnavailableException" (seen 2026-10-26, a meeting lost to it)
        found = f"{type(e).__name__} {code}".lower()
        if isinstance(e, MaxTokensReachedException) or any(p.lower() in found for p in PASSING):
            return True
        e = e.__cause__ or e.__context__
    return False


def ask(agent: Agent, prompt: str, sleep=time.sleep) -> None:
    """One turn, retried after a passing failure. A failed call leaves the user's message last; a placeholder reply
    closes it, because turns must alternate."""
    for attempt in range(len(RETRY_WAITS) + 1):
        try:
            agent(prompt)
            return
        except Exception as e:
            if attempt == len(RETRY_WAITS) or not passing(e):
                raise
            log.warning("model call failed (%s), retrying in %s s", type(e).__name__, RETRY_WAITS[attempt])
            if agent.messages and agent.messages[-1]["role"] == "user":
                agent.messages.append({"role": "assistant", "content": [{"text": UNANSWERED}]})
            sleep(RETRY_WAITS[attempt])
            prompt = f"(The model call failed: {type(e).__name__}. Carry on where you were.)"


def run(path: Path, meta: dict, conversation: Path | None = None, model=None, sleep=time.sleep, start: str = START) -> Run:
    """The agent on a prepared meeting until check passes. Raises RunFailed when it stalls or runs out of turns.
    The conversation is saved to `conversation` however the run ends: raw material for evals and debugging."""
    started = time.monotonic()
    if tools.shell_user() and not tools.shell_is_offline():
        raise RuntimeError("the agent's shell can reach the network: the instance's firewall rule is missing")
    tools.use(path)
    # One tool at a time: the file tools switch the process to the shell's rights while they work (tools.shell_rights).
    agent = Agent(model=model or build_model(), tools=tools.TOOLS, system_prompt=system_prompt(path, meta),
                  conversation_manager=NullConversationManager(), callback_handler=None,  # no printing: transcripts stay out of logs
                  tool_executor=SequentialToolExecutor())
    calls = 0

    def budget(event: BeforeToolCallEvent) -> None:
        nonlocal calls
        calls += 1
        if calls > settings.max_tool_calls:
            event.cancel_tool = "The run's tool calls are used up: end your turn now."

    agent.hooks.add_callback(BeforeToolCallEvent, budget)
    try:
        prompt, before, stalled = start, None, 0
        for turn in range(1, settings.max_turns + 1):
            ask(agent, prompt, sleep)
            missing = check.missing(path)
            log.info("turn %s: %s tool calls so far, %s things missing", turn, calls, len(missing))
            if not missing:
                return Run(turn, calls, round(time.monotonic() - started, 1), tokens(agent))
            stalled = stalled + 1 if missing == before else 0
            if stalled >= settings.stalled_turns:
                raise RunFailed(f"stalled: {stalled} turns in a row changed nothing ({len(missing)} things missing)")
            before = missing
            prompt = "Your turn ended, but the meeting isn't done. `tally check` reports:\n" + "\n".join(f"- {m}" for m in missing)
        raise RunFailed(f"not done after {settings.max_turns} turns")
    finally:
        if tools.shell_user():
            tools.stop_shell_processes()  # nothing of the agent's runs on while root checks and publishes
        if conversation:
            conversation.parent.mkdir(parents=True, exist_ok=True)
            conversation.write_text(json.dumps(agent.messages, indent=1, ensure_ascii=False, default=str))

