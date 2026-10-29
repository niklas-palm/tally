# Contributing

Thanks for helping. Tally is small and kept that way by one maintainer, so for anything bigger than a fix, open an issue first and say what you'd like to change and why.

Security problems go privately, never in an issue: see [SECURITY.md](SECURITY.md). Everyone follows the [code of conduct](CODE_OF_CONDUCT.md).

## Build and test

The tests need no AWS. Running Tally end to end needs a deployment of your own ([README.md](README.md)).

```
make test          # the pipeline's and the API's tests (Python 3.12 with uv, and ffmpeg), and that the shell scripts parse
make app-test      # the Mac app's tests (macOS with the Command Line Tools)
make windows-test  # the Windows app's Core tests (the .NET 10 SDK, any OS)
```

## A good pull request

- **Small and simple,** and reads well.
- **Tested:** a bug fix comes with a test that fails without it. The API's tests cover isolation between people: keep them passing.
- **The docs stay true:** the README, `docs/`, prompts and docstrings change with the code.
- **Both apps:** a change to what one app does is made in the other too, or the PR says why not.
- **Says why,** and how you checked it. Measured, not assumed, for models, thresholds and cost.
- **Nothing deployment-specific,** and no secrets, tokens or recordings.

[AGENTS.md](AGENTS.md) has the full rules and a map of the code.

## Licence

By opening a pull request, you agree that your contribution is licensed under the [PolyForm Shield License 1.0.0](LICENSE.md), and that you have the right to contribute it.
