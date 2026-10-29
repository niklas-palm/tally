"""download_models.py, the image's model download: what it writes beside the models."""

import json
from pathlib import Path


def test_kb_whisper_gets_a_ctranslate2_config(tmp_path, monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).parent.parent))
    from download_models import ctranslate2_config

    (tmp_path / "generation_config.json").write_text(json.dumps({
        "alignment_heads": [[7, 0]], "lang_to_id": {"<|sv|>": 50273, "<|en|>": 50259}, "suppress_tokens": [1, 2], "begin_suppress_tokens": [220, 50257]}))
    ctranslate2_config(tmp_path)
    assert json.loads((tmp_path / "config.json").read_text()) == {
        "alignment_heads": [[7, 0]], "lang_ids": [50259, 50273], "suppress_ids": [1, 2], "suppress_ids_begin": [220, 50257]}
