"""Tally: a meeting recording in, a transcript with real speaker names, a summary and action items out.

`tally prepare` does the deterministic work (decode, language ID, transcription, diarization, voiceprint matches,
clips); an agent then works out who is who and writes the summary through the rest of the `tally` CLI; the job
(`tally-job`) runs both for one uploaded meeting on AWS Batch and publishes the results.
"""
