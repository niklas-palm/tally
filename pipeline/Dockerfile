# syntax=docker/dockerfile:1
# The pipeline's image: x86_64 only (pyannote 4's torchcodec has no aarch64 wheels), CUDA from torch's own wheels
# (the GPU driver comes from the Batch host), every model baked in. Built by CodeBuild (cdk/, `make image`).
FROM public.ecr.aws/docker/library/python:3.12-slim-bookworm

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
COPY --from=ghcr.io/astral-sh/uv:0.13.3 /uv /usr/local/bin/uv

ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never \
    PATH=/app/.venv/bin:$PATH \
    TALLY_MODELS=/models \
    PYANNOTE_METRICS_ENABLED=0 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
WORKDIR /app
# The agent's shell runs as this user, without the job's AWS credentials (tally/tools.py). Its uid is fixed: the
# instance's firewall gives it no network (cdk/tally_stack.py), and Batch's host networking shares the uid.
RUN useradd --system --uid 9999 --no-create-home --shell /usr/sbin/nologin agent

COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --extra ml --no-install-project

# ctranslate2 (faster-whisper) finds cuDNN and cuBLAS in the wheels torch brought along
ENV LD_LIBRARY_PATH=/app/.venv/lib/python3.12/site-packages/nvidia/cudnn/lib:/app/.venv/lib/python3.12/site-packages/nvidia/cublas/lib

COPY download_models.py ./
RUN --mount=type=secret,id=hf_token HF_HUB_OFFLINE=0 TRANSFORMERS_OFFLINE=0 HF_TOKEN=$(cat /run/secrets/hf_token) \
    python download_models.py /models

COPY tally ./tally
RUN uv sync --frozen --no-dev --extra ml

ENTRYPOINT ["tally-job"]
