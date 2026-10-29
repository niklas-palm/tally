# Notices

Tally's own code is under the PolyForm Shield License 1.0.0 ([LICENSE.md](LICENSE.md)). The pipeline's image downloads
these models when it's built (`pipeline/download_models.py`), each under its own licence. They aren't part of this
repository.

| Model | Used for | Licence | Source |
|---|---|---|---|
| pyannote speaker-diarization-community-1 | Who spoke when, and voiceprints | CC BY 4.0 | [pyannote/speaker-diarization-community-1](https://huggingface.co/pyannote/speaker-diarization-community-1), by pyannoteAI |
| Whisper large-v3-turbo, as CTranslate2 | Transcribing English, and telling English from Swedish | MIT | [mobiuslabsgmbh/faster-whisper-large-v3-turbo](https://huggingface.co/mobiuslabsgmbh/faster-whisper-large-v3-turbo), a conversion of [openai/whisper-large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) by OpenAI |
| KB-Whisper large | Transcribing Swedish | Apache 2.0 | [KBLab/kb-whisper-large](https://huggingface.co/KBLab/kb-whisper-large), by the National Library of Sweden (KBLab) |
| wav2vec 2.0 Base, 960 h | Aligning English words in time | MIT | `WAV2VEC2_ASR_BASE_960H` in torchaudio, from [fairseq](https://github.com/facebookresearch/fairseq) by Meta AI |
| wav2vec2 large VoxRex Swedish | Aligning Swedish words in time | CC0 1.0 | [KBLab/wav2vec2-large-voxrex-swedish](https://huggingface.co/KBLab/wav2vec2-large-voxrex-swedish), by KBLab |


Two voice-activity models come inside Python packages the image installs, not downloaded separately: [Silero VAD](https://github.com/snakers4/silero-vad) (MIT), in faster-whisper, and WhisperX's own, from [pyannote/segmentation](https://huggingface.co/pyannote/segmentation) (MIT).

**Attribution for pyannote's model** (CC BY 4.0): speaker diarization and speaker embeddings by
[pyannote.audio](https://github.com/pyannote/pyannote-audio) and pyannoteAI's `speaker-diarization-community-1`
pipeline, used unchanged. Licence: https://creativecommons.org/licenses/by/4.0/. The model is gated: using it needs a
Hugging Face account that has accepted its terms (README, "What you need").

The meeting agent runs on a model in Amazon Bedrock (`modelId`), under the terms of the model's provider and AWS.
