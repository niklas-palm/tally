# Security

Tally keeps people's recordings, transcripts and voiceprints, so security reports matter.

## Reporting

Report a vulnerability privately: the repository's **Security** tab, then **Report a vulnerability**. Don't open a public issue or pull request for it.

Say what's affected, how to reproduce it, and what someone could do with it. You'll get an answer within a week, and credit when it's fixed if you'd like it.

## In scope

- **Isolation:** one signed-in person reading or changing another's meetings, people, voiceprints or files.
- **The API:** requests accepted without a valid token, or with one from the wrong issuer or audience.
- **The agent:** transcript text that makes it reach beyond its own meeting, or reach AWS credentials or the network.
- **The apps:** the saved sign-in, the install scripts, or code run from a recording or summary.
- **Leaks:** audio, transcripts or voiceprints in logs, or anything public that shouldn't be.

## Out of scope

- A deployment's own configuration: who its OIDC provider lets in, and its AWS account's settings.
- `sharedVoices`, which by design matches against everyone's named people when turned on.
- An owner breaking only their own meeting, by a hand-made request or by what's said in it ([why](docs/architecture.md#why-its-built-this-way)).

## Versions

Only the latest `main` gets fixes. Installed apps offer each new version their site publishes.
