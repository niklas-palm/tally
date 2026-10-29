"""A stand-in for Bedrock: a Strands model that answers from a script, so the whole agent loop runs in a test in a
second, free. Each reply is text, a list of tool calls ({"name", "input"}), or an exception to raise."""

import json
import uuid

from strands.models.model import Model


class Scripted(Model):
    def __init__(self, replies: list):
        self.replies = list(replies)
        self.seen: list[list[dict]] = []  # the messages each call was sent

    def update_config(self, **model_config) -> None:
        pass

    def get_config(self) -> dict:
        return {}

    async def structured_output(self, *args, **kwargs):
        raise NotImplementedError

    async def stream(self, messages, tool_specs=None, system_prompt=None, **kwargs):
        self.seen.append([dict(m) for m in messages])
        reply = self.replies.pop(0) if self.replies else "done"
        if isinstance(reply, BaseException):
            raise reply
        yield {"messageStart": {"role": "assistant"}}
        if isinstance(reply, str):
            yield {"contentBlockDelta": {"delta": {"text": reply}}}
            yield {"contentBlockStop": {}}
            stop = "end_turn"
        else:
            for call in reply:
                yield {"contentBlockStart": {"start": {"toolUse": {"toolUseId": f"t{uuid.uuid4().hex[:8]}", "name": call["name"]}}}}
                yield {"contentBlockDelta": {"delta": {"toolUse": {"input": json.dumps(call["input"])}}}}
                yield {"contentBlockStop": {}}
            stop = "tool_use"
        yield {"messageStop": {"stopReason": stop}}
        yield {"metadata": {"usage": {"inputTokens": 10, "outputTokens": 5, "totalTokens": 15}, "metrics": {"latencyMs": 1}}}


def bash(command: str) -> list[dict]:
    return [{"name": "bash", "input": {"command": command}}]


def results(messages: list[dict]) -> list[str]:
    """The text of every tool result in a conversation."""
    return [c["text"] for m in messages for b in m["content"] if "toolResult" in b for c in b["toolResult"]["content"] if "text" in c]
