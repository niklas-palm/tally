"""`tally prepare`: everything deterministic that happens to a recording before the agent sees it.

Decode, language ID (English or Swedish only), transcription and word alignment (WhisperX), diarization of the mono
mix with per-speaker embeddings (pyannote community-1), speaker stats, voiceprint matches and clips. The outputs
are files in the working directory (docs/data.md). The models are baked into the image under settings.models, and
nothing is downloaded at run time.
"""

import gc
import json
import subprocess
import time
import wave
from pathlib import Path

import numpy as np

from . import transcript, work
from .config import settings

RATE = 16_000
IDENTICAL = 1e-3  # largest sample difference between two channels that are one channel twice
TURBO = "faster-whisper-large-v3-turbo"
KB_WHISPER = "kb-whisper-large"  # KBLab/kb-whisper-large, revision strict
ASR = {"en": TURBO, "sv": KB_WHISPER}
DIARIZATION = "pyannote-community-1"
CLIPS = 3  # per speaker
CLIP_SECONDS = (3.0, 8.0)  # the shortest clean turn used for a clip, and the longest clip
SHORTEST_CLIP = 1.0  # for someone with no clean turn that long: their longest turns, from this up
SPEECH_PEAK = 0.5  # the level (-6 dBFS) a recording's loud speech is brought to
MAX_GAIN_DB = 30.0  # so a near-silent recording isn't turned into loud noise
LONGEST = 4 * 3600 + 300  # seconds: the apps stop at 4 hours; a few minutes over for an encoder's rounding


class NoSpeech(Exception):
    """The recording has nobody speaking in it: nothing to summarise, which isn't a fault."""


class TooLong(Exception):
    """Longer than the apps record (4 hours): prepare's memory grows with length, and an all-night recording would run
    out every time it's sent."""


def run(audio: Path, path: Path) -> dict:
    timings: dict[str, float] = {}
    started = time.monotonic()

    def lap(step: str) -> None:
        nonlocal started
        timings[step] = round(time.monotonic() - started, 1)
        started = time.monotonic()

    channels, layout = decode(audio)
    mono, gain_db = level(channels.mean(axis=0) if layout == "stereo-split" else channels[0])
    write_wav(path / "audio16k.wav", mono)
    duration = len(mono) / RATE
    work.write("channels.json", {"layout": layout, "channels": len(channels), "loudness_db": [loudness(c) for c in channels],
                                 "gain_db": gain_db}, path)
    lap("decode")

    turns, embeddings = diarize(mono, channels if layout == "stereo-split" else None)
    if not turns:
        raise NoSpeech()
    work.write("diarization.json", {"turns": turns}, path)
    work.write_embeddings(embeddings, path)
    lap("diarize")

    language, probabilities = detect_language(mono)
    lap("language")
    words = transcribe(mono, language)
    lap("transcribe")
    work.write("transcript.json", transcript.rebuild({"language": language, "model": ASR[language], "words": words}, turns), path)

    speakers = transcript.refresh(path)
    clips(mono, turns, path)
    lap("clips")
    work.update_manifest(path, step="prepared", duration=round(duration, 1), layout=layout, language=language,
                         language_probabilities=probabilities, models={"asr": ASR[language], "diarization": DIARIZATION},
                         timings=timings)
    return {"duration": round(duration, 1), "layout": layout, "language": language, "language_probabilities": probabilities,
            "speakers": len(speakers), "words": len(words), "timings": timings}


def decode(audio: Path) -> tuple[np.ndarray, str]:
    """The recording at 16 kHz, one row per channel, and its layout: `mono`, `identical` (two channels that are one,
    as in the samples) or `stereo-split` (mic and system audio)."""
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries",
                                       "stream=channels:format=duration", "-of", "json", str(audio)], capture_output=True, check=True).stdout)
    if float(probe.get("format", {}).get("duration") or 0) > LONGEST:
        raise TooLong()
    count = min(int(probe["streams"][0]["channels"]), 2)
    raw = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", str(audio), "-ac", str(count), "-ar", str(RATE), "-f", "f32le", "-"],
                         capture_output=True, check=True).stdout
    channels = np.frombuffer(raw, dtype=np.float32).reshape(-1, count).T.copy()
    if count == 1:
        return channels, "mono"
    if np.max(np.abs(channels[0] - channels[1])) < IDENTICAL:
        return channels[:1], "identical"
    return channels, "stereo-split"


def level(samples: np.ndarray) -> tuple[np.ndarray, float]:
    """The recording at a normal level, and the gain applied. A Mac's built-in mic records raw and quiet (measured: a
    room meeting averaging -46 dBFS, where speech is usually about -20), and Whisper's input scaling and the speech
    detection in front of it expect normal levels. The loud speech (the 99.9th percentile, so a click doesn't decide)
    is brought to SPEECH_PEAK; a hot recording is turned down. The clips the owner hears come from this too."""
    loud = float(np.percentile(np.abs(samples), 99.9)) if samples.size else 0.0
    if loud <= 0:
        return samples, 0.0
    gain_db = round(min(20 * np.log10(SPEECH_PEAK / loud), MAX_GAIN_DB), 1)
    return np.clip(samples * 10 ** (gain_db / 20), -1, 1).astype(np.float32), gain_db


def write_wav(path: Path, samples: np.ndarray) -> None:
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(pcm16(samples))


def pcm16(samples: np.ndarray) -> bytes:
    return (np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes()


def read_wav(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as f:
        return np.frombuffer(f.readframes(f.getnframes()), dtype=np.int16).astype(np.float32) / 32767


def loudness(samples: np.ndarray) -> float:
    return round(float(10 * np.log10(np.mean(samples.astype(np.float64) ** 2) + 1e-12)), 1)


def free() -> None:
    import torch

    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def detect_language(mono: np.ndarray) -> tuple[str, dict]:
    """English or Swedish, by Whisper's own detection over several speech segments. Other languages are left out on
    purpose: Swedish is often mistaken for Norwegian or Danish."""
    from faster_whisper import WhisperModel

    model = WhisperModel(str(settings.models / TURBO), device=settings.device, compute_type=compute_type())
    _, _, probabilities = model.detect_language(mono, vad_filter=True, language_detection_segments=4)
    found = dict(probabilities)
    en, sv = found.get("en", 0.0), found.get("sv", 0.0)
    del model
    free()
    return ("sv" if sv > en else "en"), {"en": round(en, 3), "sv": round(sv, 3)}


def compute_type() -> str:
    return "float16" if settings.device == "cuda" else "int8"


def transcribe(mono: np.ndarray, language: str) -> list[dict]:
    """Words with timestamps: Whisper (turbo for English, KB-Whisper for Swedish), then wav2vec2 alignment. Each word
    keeps the index of its ASR segment, so the transcript's lines break where Whisper's did."""
    import whisperx

    asr = whisperx.load_model(str(settings.models / ASR[language]), settings.device, compute_type=compute_type(), language=language)
    result = asr.transcribe(mono, batch_size=16, language=language)
    del asr
    free()
    aligner, metadata = whisperx.load_align_model(language_code=language, device=settings.device, model_dir=str(settings.models / "align"))
    aligned = whisperx.align(result["segments"], aligner, metadata, mono, settings.device, return_char_alignments=False)
    del aligner
    free()
    words = []
    for i, segment in enumerate(aligned["segments"]):
        last = segment["start"]
        for w in segment.get("words", []):
            # numbers and symbols wav2vec2 can't align get no times: they take the previous word's end
            start, end = w.get("start", last), w.get("end", last)
            words.append({"w": w["word"], "start": round(start, 2), "end": round(end, 2), "score": round(w.get("score", 0.0), 2), "seg": i})
            last = end
    return words


def diarize(mono: np.ndarray, channels: np.ndarray | None) -> tuple[list[dict], dict[str, np.ndarray]]:
    """Exclusive turns (one speaker at a time, for word assignment), each with its clean, non-overlapping seconds and,
    for a stereo-split recording, its mic and system energy; and one L2-normalised centroid per speaker. Speakers are
    named S1, S2, ... in the order they first speak."""
    import torch
    from pyannote.audio import Pipeline

    pipeline = Pipeline.from_pretrained(str(settings.models / DIARIZATION)).to(torch.device(settings.device))
    output = pipeline({"waveform": torch.from_numpy(mono[None, :]), "sample_rate": RATE})
    del pipeline
    free()
    overlap = [(s.start, s.end) for s in output.speaker_diarization.get_overlap()]
    exclusive = sorted(output.exclusive_speaker_diarization.itertracks(yield_label=True), key=lambda t: t[0].start)
    names: dict[str, str] = {}
    for _, _, label in exclusive:
        names.setdefault(label, f"S{len(names) + 1}")
    turns = []
    for segment, _, label in exclusive:
        start, end = segment.start, segment.end
        clean = (end - start) - sum(max(0.0, min(end, e) - max(start, s)) for s, e in overlap)
        turn = {"start": round(start, 2), "end": round(end, 2), "speaker": names[label], "clean": round(max(clean, 0.0), 2)}
        if channels is not None:
            a, b = int(start * RATE), int(end * RATE)
            turn |= {"mic": float(np.sum(channels[0, a:b].astype(np.float64) ** 2)), "system": float(np.sum(channels[1, a:b].astype(np.float64) ** 2))}
        turns.append(turn)
    embeddings = {}
    for label, vector in zip(output.speaker_diarization.labels(), output.speaker_embeddings, strict=True):
        if label in names and np.all(np.isfinite(vector)):  # a speaker with too little speech gets no usable centroid
            embeddings[names[label]] = transcript.normalised(vector)
    return turns, embeddings


def clips(mono: np.ndarray, turns: list[dict], path: Path) -> None:
    """A few short clips per speaker from their longest clean turns, so the owner can hear "Speaker 3" before naming
    them. Someone who only said a little, or always over others, has no clean turn that long: their longest turns
    all the same, from a second up, since a short or overlapped clip says more than none."""
    for speaker in sorted({t["speaker"] for t in turns}):
        theirs = [t for t in turns if t["speaker"] == speaker]
        found = sorted((t for t in theirs if t["clean"] >= CLIP_SECONDS[0]), key=lambda t: -t["clean"])
        if not found:
            found = sorted((t for t in theirs if t["end"] - t["start"] >= SHORTEST_CLIP), key=lambda t: t["start"] - t["end"])
        if not found:
            continue
        folder = path / "clips" / speaker
        folder.mkdir(parents=True, exist_ok=True)
        for n, turn in enumerate(sorted(found[:CLIPS], key=lambda t: t["start"]), 1):
            a = int(turn["start"] * RATE)
            b = int(min(turn["end"], turn["start"] + CLIP_SECONDS[1]) * RATE)
            encode(mono[a:b], folder / f"{n}.m4a")


def encode(samples: np.ndarray, target: Path) -> None:
    subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", "-f", "s16le", "-ar", str(RATE), "-ac", "1", "-i", "-",
                    "-c:a", "aac", "-b:a", "48k", str(target)], input=pcm16(samples), check=True)


def retranscribe(language: str, path: Path) -> dict:
    """Transcription again in the other language, keeping the diarization and everything decided about speakers."""
    mono = read_wav(path / "audio16k.wav")
    turns = work.read("diarization.json", path)["turns"]
    words = transcribe(mono, language)
    work.write("transcript.json", transcript.rebuild({"language": language, "model": ASR[language], "words": words}, turns), path)
    work.update_manifest(path, language=language, models=work.read("manifest.json", path)["models"] | {"asr": ASR[language]})
    return {"language": language, "model": ASR[language], "words": len(words)}
