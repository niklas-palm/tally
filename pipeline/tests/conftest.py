"""A small meeting as `tally prepare` leaves it, built without the models: three speakers, a few lines each."""

import numpy as np
import pytest

from tally import transcript, work

LINES = [  # (start, end, diarization label, ASR segment text)
    (0.0, 6.0, "S1", "Good evening everyone, I'm Mayor Alice Brown and I call this meeting to order."),
    (6.5, 9.0, "S2", "Thank you, Mayor Brown."),
    (9.5, 20.0, "S1", "First item is the park budget. Councilmember Ortiz, would you present?"),
    (20.5, 40.0, "S3", "Thanks Alice. The park budget is two hundred thousand and we propose to approve it today."),
    (40.5, 44.0, "S2", "I move to approve the park budget."),
    (44.5, 47.0, "S1", "All in favour? The motion carries."),
]


def words_of(start: float, end: float, text: str, seg: int) -> list[dict]:
    parts = text.split()
    step = (end - start) / len(parts)
    return [{"w": w, "start": round(start + i * step, 2), "end": round(start + (i + 1) * step, 2), "score": 0.9, "seg": seg}
            for i, w in enumerate(parts)]


def unit(seed: int) -> np.ndarray:
    return transcript.normalised(np.random.default_rng(seed).normal(size=256))


@pytest.fixture
def meeting(tmp_path, monkeypatch):
    turns = [{"start": s, "end": e, "speaker": spk, "clean": e - s - 0.2} for s, e, spk, _ in LINES]
    words = [w for i, (s, e, _, text) in enumerate(LINES) for w in words_of(s, e, text, i)]
    alice = unit(1)
    people = [{"person_id": "p_alice", "name": "Alice Brown", "aliases": ["Mayor Brown"], "is_owner": False,
               "voiceprints": [{"voiceprint_id": "v1", "source": "user", "model": "pyannote/speaker-diarization-community-1",
                                "meeting_id": "m0", "embedding": alice.tolist()}]}]
    work.write("diarization.json", {"turns": turns}, tmp_path)
    work.write("people.json", {"people": people}, tmp_path)
    work.write_embeddings({"S1": transcript.normalised(alice + 0.3 * unit(2)), "S2": unit(3), "S3": unit(4)}, tmp_path)
    work.write("transcript.json", transcript.rebuild({"language": "en", "model": "faster-whisper-large-v3-turbo", "words": words}, turns), tmp_path)
    transcript.refresh(tmp_path)
    work.update_manifest(tmp_path, step="prepared", duration=47.0, layout="identical", language="en")
    monkeypatch.chdir(tmp_path)
    return tmp_path
