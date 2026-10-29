# Security

Tally keeps people's meeting recordings, transcripts and voiceprints, so we take reports seriously.

## Reporting a problem

Please report a vulnerability **privately**, through GitHub: the repository's **Security** tab → **Report a
vulnerability**. Only the maintainers see it. Don't open a public issue or pull request for it.

Say what's affected, how to reproduce it, and what someone could do with it. We'll answer within a week, keep you
updated on what we find, and credit you when it's fixed, unless you'd rather we didn't.

## What's in scope

- **Isolation between people:** anything that lets one signed-in person read or change another's meetings, people,
  voiceprints or files (the API, the pipeline's job, the stored keys).
- **The API and its authorizer:** requests accepted without a valid token, or tokens from the wrong issuer or audience.
- **The agent:** transcript text that makes the meeting agent reach beyond its own meeting (another person's data, or
  the owner's other meetings), or that reaches AWS credentials or the network from its shell. Transcript text is data,
  never instructions.
- **The apps:** the saved sign-in, the install scripts, and anything that runs code from a recording or a summary.
- **Leaks:** audio, transcripts or voiceprints in logs, or anything public that shouldn't be.

## What's out of scope

- A deployment's own configuration: who its OIDC provider lets sign in, and its AWS account's settings.
- `sharedVoices`, which by design matches meetings against everyone's named people when a deployment turns it on (the
  README says what that means).
- An owner breaking only their own meeting, which they can delete: by a hand-made request to the API, or by what's said
  in the meeting steering its agent. What reaches another person, or more than that meeting, is in scope ([why](docs/architecture.md#why-its-built-this-way)).

## Supported versions

Only the latest version on `main` gets fixes. Installed apps offer each new version their deployment's site publishes.
