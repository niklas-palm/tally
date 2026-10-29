# Contributing to Tally

Thanks for wanting to make Tally better. This page says how to work on it, and what a change needs before it's merged.

## Before you start

- **Read [AGENTS.md](AGENTS.md).** It's written for coding agents, and it's the clearest statement of how this code is
  written: the rules (simplicity and readability first, docs kept true, saying why, measurements over
  guesses), the safety rules for a deployment people use, and the map of the repository.
- **For anything bigger than a fix, open an issue first** and say what you'd like to change and why. Tally is
  deliberately small, and a feature that adds settings, layers or moving parts may not fit. Talking first saves you work.
- **Security problems** go privately, never in an issue: see [SECURITY.md](SECURITY.md).

## Setting up

You need your own deployment to run Tally end to end ([README.md](README.md): what you need, configure, deploy). Most
work doesn't: the tests need no AWS.

| | |
|---|---|
| `make test` | The pipeline's and the API's tests, and that the shell scripts parse (Python 3.12 with `uv`) |
| `make app-test` | The Mac app's tests (a Mac with the Command Line Tools) |
| `make windows-test` | The Windows app's Core tests (the .NET 10 SDK, on any OS) |
| `cd cdk && npx -y aws-cdk@latest diff --all` | What a deploy would change (your own AWS account) |

The Mac app is in `apps/mac/` ([docs/macos.md](docs/macos.md)) and the Windows app in `apps/windows/`
([docs/windows.md](docs/windows.md)). They do the same things, and [docs/parity.md](docs/parity.md) keeps track of
where they stand: a change to what one of them does says what it means for the other.

## A change that's ready

- **It's simple and reads well.** Short modules, each starting with a sentence or two on why it exists; plain names;
  comments that say why, not what. No code for cases that can't happen, no layers for later.
- **It has tests.** Every bug fixed gets a test that fails without the fix; every new rule gets a test. The API's tests
  cover isolation between people: everything is the signed-in person's, and nothing may read or change one person's
  data on another's behalf.
- **The docs are still true.** A change that makes the README, AGENTS.md, anything in `docs/`, a prompt or a tool's
  docstring wrong updates them in the same pull request. Every stored shape is in [docs/data.md](docs/data.md).
- **It says why** in its pull request. A change to one of the choices in
  [Why it's built this way](docs/architecture.md#why-its-built-this-way) updates that section.
- **Measured, not assumed,** for models, thresholds, compute and cost: say what you measured and how in
  [docs/research.md](docs/research.md).
- **Nothing deployment-specific** goes in tracked files (account ids, domains, issuers, emails): those live in
  `config.json`, which is gitignored. No secrets, tokens or recordings in commits, ever.
- **Checks pass:** `make test`, plus `make app-test` or `make windows-test` for app changes, and `ruff` is clean.

Open the pull request against `main` with a description of what changed and why, and how you checked it.

## Licence of contributions

Tally is under the PolyForm Shield License 1.0.0 ([LICENSE.md](LICENSE.md)). By opening a pull request, you agree that
your contribution is licensed under the same terms, and that you have the right to contribute it.
