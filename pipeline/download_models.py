"""Download every model the pipeline uses into one folder, at image build time, so a run is offline and holds no
token. pyannote's community-1 is gated: HF_TOKEN (from Secrets Manager, through a build secret) unlocks it.

    HF_TOKEN=... python download_models.py /models
"""

import json
import os
import sys
from pathlib import Path

CT2 = ["model.bin", "config.json", "tokenizer.json", "vocabulary*", "preprocessor_config.json"]


def ctranslate2_config(model: Path) -> None:
    """KB-Whisper's repository keeps Hugging Face's config.json beside its CTranslate2 model.bin, but CTranslate2
    reads its own keys from that file, and WhisperX's batched decoding then fails ("type must be number, but is
    number"). Write the keys CTranslate2's converter would, from the model's generation config."""
    generation = json.loads((model / "generation_config.json").read_text())
    (model / "config.json").write_text(json.dumps({
        "alignment_heads": generation["alignment_heads"], "lang_ids": sorted(generation["lang_to_id"].values()),
        "suppress_ids": generation["suppress_tokens"], "suppress_ids_begin": generation["begin_suppress_tokens"]}))


def main(target: Path) -> None:
    from huggingface_hub import snapshot_download  # only in the image, with the models' dependencies

    snapshot_download("mobiuslabsgmbh/faster-whisper-large-v3-turbo", local_dir=target / "faster-whisper-large-v3-turbo", allow_patterns=CT2)
    snapshot_download("KBLab/kb-whisper-large", revision="strict", local_dir=target / "kb-whisper-large",
                      allow_patterns=[*CT2, "generation_config.json"])
    ctranslate2_config(target / "kb-whisper-large")
    snapshot_download("pyannote/speaker-diarization-community-1", local_dir=target / "pyannote-community-1", token=os.environ["HF_TOKEN"])

    import whisperx  # the aligners, through WhisperX itself, so they land where it looks for them at run time

    for language in ("en", "sv"):
        whisperx.load_align_model(language_code=language, device="cpu", model_dir=str(target / "align"))


if __name__ == "__main__":
    main(Path(sys.argv[1]))
