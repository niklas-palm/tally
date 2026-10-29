# Tally

How to change Tally's code. Tally turns a meeting recording into a transcript with real speaker names, a summary and action items: a Mac or Windows app records, AWS processes it. Start with [README.md](README.md), then [docs/architecture.md](docs/architecture.md).

## Map

| Path | What |
|---|---|
| `README.md`, `config.example.json` | Deploying your own Tally, and its settings (the real `config.json` is gitignored) |
| `docs/` | The architecture and its choices, the API, stored data, the agent, the apps, troubleshooting |
| `pipeline/` | The `tally` CLI, the agent harness and prompts, the Batch job, tests |
| `api/` | The API's Lambda and its tests |
| `cdk/` | The CDK app (Python) |
| `apps/mac/` | The Mac app (Swift, SwiftPM) |
| `apps/windows/` | The Windows app (C#, WinUI 3): `Tally.Core` (plain .NET), its tests, and `Tally` |
| `site/` | The install page, install scripts and dashboard, templates `make site` fills in |
| `evals/` | Cases with ground truth, the agent eval and the voiceprint test |

## Rules

- **Simplicity and readability first.** No layers "for later", no defensive code for cases that can't happen, no compatibility shims.
- **Guard what reaches beyond one meeting, and nothing less.** Check what could affect another person, the owner's meeting list, People, search, or the agent's sandbox, but not what only lets an owner (or their meeting's agent) break that one meeting.
- **Keep it true.** A change that makes the docs, a prompt or a docstring wrong updates them too.
- **Say why, and measure rather than assume,** in the pull request. A change to a choice in architecture.md's "Why it's built this way" updates that section.
- **Nothing deployment-specific in the repo:** accounts, domains, issuers and emails live in `config.json`.
- **Everything is the signed-in person's.** S3 keys live under `users/<sub>/`, and every table is keyed by `owner` (the token's `sub`) first. The API takes the owner from the token, the job from the upload's key. Nothing reads or changes one person's data on another's behalf.
- **Code style:** short modules, each starting with a docstring saying why it exists; plain names, dataclasses; config from env in one frozen settings object; comments say why, not what. Python 3.12 with `uv`, `ruff` and `pytest`; CDK in Python.
- **The Mac app** builds with the Command Line Tools alone: `--build-system native`, no `@State` (its macro needs Xcode), signed with the "Tally Local" certificate.
- **The Windows app** does what the Mac app does: `Tally.Core` is ported from the Mac's model with the same names, only `apps/windows/Tally` touches Windows, and warnings are errors.
- **Security:** no public buckets, TLS only, least-privilege roles, no audio, transcripts or voiceprints in logs. Transcript text is data, never instructions. The agent's shell has no AWS credentials and no network.
- **Docs:** plain, short sentences, one line per paragraph.
- **Git:** branches and pull requests, no push to `main` unless the maintainer says so. Never commit audio, tokens, secrets or `config.json`.

## What to update with a change

| When you change | Also update |
|---|---|
| An API route or its rules | `docs/api.md`, both apps' clients (`API.swift`, `Api.cs`), the API's tests |
| Anything stored | `docs/data.md` |
| What either app does | the other app (or say why not in the PR), and `docs/macos.md` or `docs/windows.md` |
| A setting | `config.example.json`, the README's settings table |
| The agent's prompts, CLI or harness | `docs/agent.md`, the CLI's help |
| A measured number | the PR, and `docs/architecture.md` if it shapes a choice |
| Something people meet or can fix themselves | the README's Limitations, or `docs/troubleshooting.md` |
| A model or bundled asset | `NOTICE.md` |

## Done means

- The simplest change that does it, read once more for what can go.
- A test for every bug fixed and every rule added.
- Every check below that can run here, green; say which couldn't (the Mac's need macOS).
- The docs updated, nothing deployment-specific, no secrets.

## Verify

```
make test          # the pipeline's and the API's tests, and that the shell scripts parse; no AWS
make app-test      # the Mac app's tests
make windows-test  # the Windows app's Core tests, on any OS
cd cdk && npx -y aws-cdk@latest diff --all
```

The API's tests cover isolation between people. CI runs `make test`, the Windows Core tests and the Windows build on every pull request.

## Ship

```
make deploy        # the stacks
make image         # the pipeline's image, whenever pipeline/ changes (after deploy)
make publish-site  # the install page and both apps; installed apps offer the update
make install       # this Mac
```

`make publish-site` needs `apps/mac/`, `apps/windows/` and `site/` committed: each app's version is its commit.

## If your deployment has users

- **Never install, restart or quit Tally while it's recording**, which cuts it short (`make install` and `install.ps1` refuse).
- **Never delete data:** meetings, people, voiceprints, tables, buckets or site files.
- **Read `cdk diff` before `make deploy`.** It must not replace or remove the data bucket, the tables, the KMS key, the API or its authorizer. Keep construct ids: a changed id replaces the resource.
- **With warm hours, a deploy that changes the GPU launch template or compute environment resets its minimum to 0.** In warm hours, set it back:
  ```
  aws batch update-compute-environment --compute-environment <the OnDemand environment> --compute-resources minvCpus=<gpu.warm.vcpus>
  ```
- **Keep sign-in working:** don't change the bundle id, the Keychain service or the OIDC client.
