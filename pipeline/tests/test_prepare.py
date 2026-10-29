"""Bringing a recording to a normal level before transcription (quiet mics up, hot ones down, silence left alone),
its clips, and refusing one longer than the apps record."""

import json
from pathlib import Path

import numpy as np
import pytest

from tally import prepare


def tone(amplitude: float, seconds: float = 2.0) -> np.ndarray:
    t = np.arange(int(seconds * prepare.RATE)) / prepare.RATE
    return (amplitude * np.sin(2 * np.pi * 220 * t)).astype(np.float32)


def test_a_quiet_recording_is_brought_up_to_speech_level():
    out, gain = prepare.level(tone(0.02))  # loud speech at -34 dBFS, like a Mac's built-in mic across a room
    assert 25 < gain < 30 and abs(np.abs(out).max() - prepare.SPEECH_PEAK) < 0.01


def test_a_hot_recording_is_turned_down_and_never_clips():
    out, gain = prepare.level(tone(1.0))
    assert gain < 0 and np.abs(out).max() <= 1.0


def test_near_silence_isnt_turned_into_loud_noise():
    out, gain = prepare.level(tone(1e-5))
    assert gain == prepare.MAX_GAIN_DB and np.abs(out).max() < 0.01
    assert prepare.level(np.zeros(100, dtype=np.float32))[1] == 0.0


def test_someone_who_only_said_a_little_still_gets_a_clip(tmp_path):
    mono = np.zeros(prepare.RATE * 30, dtype=np.float32)
    turns = [{"speaker": "S1", "start": 0.0, "end": 9.0, "clean": 9.0},  # a long clean turn: the usual clip
             {"speaker": "S3", "start": 10.0, "end": 12.5, "clean": 0.4},  # only short remarks, talked over
             {"speaker": "S3", "start": 20.0, "end": 21.5, "clean": 1.5},
             {"speaker": "S4", "start": 25.0, "end": 25.6, "clean": 0.6}]  # under a second: nothing worth hearing
    prepare.clips(mono, turns, tmp_path)
    assert [p.name for p in (tmp_path / "clips" / "S1").iterdir()] == ["1.m4a"]
    assert sorted(p.name for p in (tmp_path / "clips" / "S3").iterdir()) == ["1.m4a", "2.m4a"]
    assert not (tmp_path / "clips" / "S4").exists()


def test_a_recording_longer_than_the_apps_record_isnt_decoded(monkeypatch):
    probe = type("Run", (), {"stdout": json.dumps({"streams": [{"channels": 2}], "format": {"duration": "36000.0"}}).encode()})
    monkeypatch.setattr(prepare.subprocess, "run", lambda *a, **k: probe)  # a room recording left on all night
    with pytest.raises(prepare.TooLong):
        prepare.decode(Path("audio.m4a"))
