# macOS app

The Mac app is in `apps/mac/`: a menu bar app with one window, for macOS Tahoe (26). It talks only to Tally's API ([api.md](api.md)) and the sign-in provider, and holds no AWS credentials.

## Building

It builds with the Command Line Tools alone.

```
make signing-cert  # once per Mac: the "Tally Local" certificate
make app           # apps/mac/.build/Tally.app, with its settings from the deployed stack
make install       # into /Applications; refuses while Tally is recording
make app-test      # the tests
```

`make app` needs AWS credentials: `settings.sh` writes the app's settings, `tally.json`, from the stack's outputs, and `build.sh` builds and signs the app.

- **`--build-system native`:** SwiftPM's default build system fails with the Command Line Tools. The Makefile passes it.
- **No `@State`:** its macro needs Xcode. View state lives in the `@Observable` `AppModel`, and small local state uses `State(initialValue:)`.
- **The "Tally Local" certificate:** macOS ties the mic and system-audio permissions to the signature, so a stable self-signed certificate keeps them across rebuilds. `signing-cert.sh` makes it.

## Installing and updating

People install it from the deployment's install page:

```
curl -fsSL https://<site>/install.sh | bash
```

`site/install.sh` installs the Command Line Tools if needed, downloads and checks the source, builds it, and installs it in `/Applications`. It refuses while Tally is recording, and logs to `~/Library/Logs/Tally/install.log`.

`make publish-site` publishes it, versioned by the commit that last changed `apps/mac/` or `site/`. An installed app checks for a new version every 30 minutes and offers **Update**, which runs the installer. A build from the repository never does.

## Source

In `apps/mac/Sources/Tally/`:

| File | What |
|---|---|
| `TallyApp.swift` | The menu bar extra and the window |
| `AppModel.swift` | Recordings, meetings, people and the sync |
| `Recorder.swift`, `Recording.swift` | The audio, and a recording's folder |
| `Auth.swift`, `API.swift` | Sign-in, and the API's routes |
| `Calls.swift`, `Drawer.swift` | Noticing calls, and offering to record one |
| `Results.swift`, `Filing.swift`, `MeetingsFolder.swift` | Speakers' names, suggested folders, the meetings folder |
| `Updates.swift`, `Crashes.swift` | Updates and crash reports |
| `*View.swift`, `MainWindow.swift`, `Library.swift`, `Files.swift` | The views |

## Recording

Tally records the mic and everything the Mac plays into one 2-channel file: channel 1 is the mic, channel 2 the system. A Core Audio process tap takes the system audio, leaving Tally out. A private aggregate device puts the tap and the default input on one clock. The tap doesn't auto-start, or a quiet room would record nothing. When the input changes, the device is rebuilt and the file carries on.

Tally writes a CAF while recording, which a crash doesn't ruin, and encodes it to AAC (`audio.m4a`) on stop. A tap without permission records silence, so the menu shows both levels. A recording stops at 4 hours, the longest the pipeline summarises.

## Signing in

Sign-in is OIDC with PKCE, through `ASWebAuthenticationSession` and `tally://callback`. The refresh token is kept in the login keychain; the access token stays in memory. A refresh the provider refuses (`invalid_grant`) asks you to sign in again, and being offline just waits.

## Recordings on the Mac

Recording never needs a sign-in. Each recording has a folder in `~/Library/Application Support/Tally/recordings/`, with its audio and `local.json` ([data.md](data.md)). It goes `recording → saved → uploading → uploaded → sent`, and the folder is deleted once its meeting is ready. Each step survives a failure and is tried again at the next sync: every 15 s while anything is on its way, otherwise every 60 s. After a crash or a quit, a recording's CAF is encoded and it's sent as usual.

## Calls

Once a second, `AudioUsers` reads which processes use audio, with no permission. `CallRules`, tested without Core Audio, turns that into calls.

When another app has been on the mic for 10 s, a drawer under the menu bar icon offers to record. Tally records by itself only in auto mode, and drops what it recorded if that's under 2 minutes. A recording started during a call stops 30 s after the call's apps leave the mic and the speakers, and **Continue Recording** carries it on. The drawer and the icon are left out of screen shares.
