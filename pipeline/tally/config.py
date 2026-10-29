"""Settings, from the environment: the Batch job definition sets them on AWS; tests and local runs use the defaults."""

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    region: str
    bucket: str  # the data bucket; empty: no AWS (tests, local evals)
    people_table: str
    voiceprints_table: str
    meetings_table: str  # each meeting's state, for the app's list
    models: Path  # where the image keeps the model weights
    device: str  # cuda on Batch; cpu for a local try
    model_id: str  # the agent's model: config's modelId, a Bedrock inference profile
    max_turns: int  # agent turns (harness prompts) before the run fails
    stalled_turns: int  # turns in a row without progress before the run fails
    max_tool_calls: int  # tool calls in the whole run before tools are refused
    shared_voices: bool  # match against everyone's named people too (config.json's sharedVoices)


def load() -> Settings:
    env = os.environ.get
    return Settings(
        region=env("AWS_REGION", "eu-north-1"),
        bucket=env("TALLY_BUCKET", ""),
        people_table=env("TALLY_PEOPLE_TABLE", ""),
        voiceprints_table=env("TALLY_VOICEPRINTS_TABLE", ""),
        meetings_table=env("TALLY_MEETINGS_TABLE", ""),
        models=Path(env("TALLY_MODELS", "/models")),
        device=env("TALLY_DEVICE", "cuda"),
        model_id=env("TALLY_MODEL_ID", "eu.anthropic.claude-sonnet-5-5"),
        max_turns=int(env("TALLY_MAX_TURNS", "8")),
        stalled_turns=int(env("TALLY_STALLED_TURNS", "3")),
        max_tool_calls=int(env("TALLY_MAX_TOOL_CALLS", "200")),
        shared_voices=env("TALLY_SHARED_VOICES") == "true",
    )


settings = load()
