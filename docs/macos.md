# macOS app

Tally's Mac app is in `apps/mac/`. It's a menu bar app with one window, for macOS Tahoe (26). It records meetings, sends them to be summarised once you're signed in, brings the results back, and lets you confirm who spoke. It talks only to Tally's API ([api.md](api.md)) and the sign-in provider, and holds no AWS credentials.

## Building

The app builds with the Command Line Tools alone, without Xcode.

```
make signing-cert  # once per Mac: the "Tally Local" code-signing certificate in the login keychain
make app           # apps/mac/.build/Tally.app, signed, with its settings from the deployed stack
make install       # build, and put it in /Applications; refuses while Tally is recording
make run           # install, and open it
make app-test      # unit tests
```

`make app` needs AWS credentials for the stack's account. `apps/mac/settings.sh` writes the app's settings, `tally.json`, from the stack's outputs and the sign-in provider's discovery document:

```json
{"issuer": "…", "clientId": "tally", "authorization": "…/auth", "token": "…/token", "signInName": "Example",
 "api": "https://….execute-api.<region>.amazonaws.com", "site": "https://<site>", "bundleId": "com.example.tally", "region": "<region>"}
```

Builds from the install page add `version`. A build from the repository has none, and never offers updates.

- **SwiftPM's default build system fails with the CLT** ("Unknown error parsing property list"). Every command uses `--build-system native`, and the tests also need the CLT's `Testing.framework` path. The Makefile passes both.
- **No `@State`.** SwiftUI's `@State` is a macro whose plugin ships only with Xcode. View state lives in the `@Observable` `AppModel`, since Observation's macros come with the toolchain. Small view-local state (a tab, a hover, a name being typed) uses plain `State(initialValue:)`, which needs no macro.
- **Bundling:** `make app` wraps the SwiftPM binary as `Tally.app`, with `apps/mac/Info.plist` (the usage strings macOS needs before it asks for the mic and system audio) and `tally.json` in `Contents/Resources`. Config's `bundleId` fills in `Info.plist`. The Keychain service, UserDefaults and the log's subsystem follow it.
- **Signing:** the app is signed with a self-signed "Tally Local" certificate in the login keychain. macOS ties the microphone and system-audio permissions to the bundle id and certificate, so a stable certificate keeps them across rebuilds. An ad-hoc signature would make macOS ask again after every build.

`apps/mac/signing-cert.sh` makes the certificate, and `apps/mac/build.sh` builds and signs the app. By hand, the certificate is:

```
openssl req -x509 -newkey rsa:2048 -nodes -keyout k.pem -out c.pem -days 3650 -subj "/CN=Tally Local" \
  -addext "basicConstraints=critical,CA:false" -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=critical,codeSigning"
openssl pkcs12 -export -legacy -inkey k.pem -in c.pem -out c.p12 -passout pass:tally
security import c.p12 -k ~/Library/Keychains/login.keychain-db -P tally -T /usr/bin/codesign
rm k.pem c.pem c.p12
```

`security find-identity` lists it as `CSSMERR_TP_NOT_TRUSTED`. That's expected, and `codesign` still uses it.

## Installing and updating

People install Tally from the deployment's install page, `site/`, served from the stack's own bucket behind CloudFront:

```
curl -fsSL https://<site>/install.sh | bash
```

`install.sh` checks macOS and the Command Line Tools, and installs them if they're missing. It downloads the source, checks its sha256, runs `signing-cert.sh` and `build.sh`, and installs to `/Applications`, or `~/Applications` when that isn't writable. It refuses to update while Tally is recording. It logs to `~/Library/Logs/Tally/install.log`, and every failure says what failed and where the log is. An install or update takes about a minute, almost all of it the release build (measured).

The page also has `install.ps1` for Windows ([windows.md](windows.md)) and the admin dashboard, `admin.html`.

`make site` assembles `site/.build/` from the templates and the app's settings:
- the page and the dashboard
- `install.sh`, with the version and checksum filled in
- `tally-<date>-<commit>-<settings hash>.tar.gz`, which is `apps/mac/` as committed plus `tally.json`
- `version.json`, which installed apps check for updates

`make publish-site` uploads it and clears CloudFront's cache. It refuses uncommitted or untracked changes to `apps/mac/`, `apps/windows/` or `site/`, because the version is the commit. Since the version ends with a hash of the settings, a changed setting is a new version too. Tarballs never change and are cached for a year; the pages and the script are never cached.

**Updates:** a build from the install page checks `version.json` at launch, every 30 minutes and when the menu opens. When there's a newer version, the menu bar icon gets a dot and the menu offers **Update**. That runs the installer, which quits Tally and opens the new version. It's off while recording. If it fails, the menu says why, from the install log. `Updates.swift` does this.

## Files

In `apps/mac/Sources/Tally/`:

| File | What |
|---|---|
| `TallyApp.swift` | The menu bar extra, its icon (`Mark`), and the window |
| `AppModel.swift` | Recordings, meetings, people, the sync, and what the window shows |
| `Recording.swift` | A recording on the Mac and its folder |
| `Recorder.swift` | The Core Audio tap plus the mic, written to a 2-channel file |
| `Auth.swift` | OIDC sign-in (PKCE) and the refresh token |
| `API.swift` | The API's routes ([api.md](api.md)) |
| `Results.swift` | `results/meeting.json`, and `Names`: what to call each speaker, and whether to confirm them |
| `ClipPlayer.swift` | Plays speaker clips from their links |
| `MenuView.swift`, `MainWindow.swift`, `Library.swift`, `MeetingView.swift`, `Files.swift`, `PeopleView.swift` | The views: the menu, the window, its sidebar and list, a meeting, its files, People |
| `Filing.swift` | Which folder a meeting probably belongs in, from who was in it |
| `MeetingsFolder.swift` | The meetings folder, and the Markdown that Copy Summary shares with it |
| `Calls.swift` | `CallRules`, when a call starts and ends, and `AudioUsers`, the processes using audio |
| `Drawer.swift` | The drawer under the menu bar icon that offers to record a call |
| `Updates.swift` | Checks the install page for a newer version, and runs the installer |
| `Crashes.swift` | Finds a crash report macOS wrote for Tally since the last one handled |
| `Keychain.swift`, `ULID.swift`, `Config.swift` | Small helpers |

`apps/mac/icon.svg` is the icon's source, and `make icon` draws the app's icon and the install page's from it.

## Recording

- **The tap:** a global Core Audio process tap of everything the Mac plays, except Tally itself (`CATapDescription(stereoGlobalTapButExcludeProcesses:)`).
- **The aggregate device:** one private aggregate device holds the default input and the tap, with drift compensation on, so one IOProc gets both on one clock. Each cycle is mixed down to channel 1 for the mic and channel 2 for the system.
- **The tap doesn't auto-start.** With auto-start, the device runs only while something plays, and a quiet room records nothing.
- **Sample rate:** 48 kHz. A mic that can't run at 48 kHz, such as a Bluetooth one, keeps its own rate, and its buffers are converted.
- **Files:** while recording, Tally writes 16-bit PCM CAF, which a crash doesn't ruin. On stop it encodes 2-channel AAC at 128 kbps as `audio.m4a`, about 55 MB an hour, and deletes the CAF.
- **Device changes:** when the default input changes, the aggregate device is rebuilt around the new mic, and the same file carries on.
- **Levels:** the menu shows live mic and system levels. A tap without the system-audio permission records silence without an error, so the moving system level is how you see it works. After a silent minute, the menu hints that the permission may be missing.
- **Permissions:** the mic is asked for before recording starts. macOS asks for system audio the first time the tap runs.
- **Limit:** every recording stops at 4 hours, which is the longest the pipeline summarises.

## Signing in

Sign-in uses the OIDC provider in config's `auth`, or the stack's Cognito user pool ([api.md](api.md)):
1. The app opens `ASWebAuthenticationSession` on the provider's authorization endpoint, with PKCE S256 and the redirect `tally://callback`.
2. It exchanges the code for tokens. The refresh token goes in the keychain, under the app's bundle id as the service and `refresh-token` as the account. The access token stays in memory and is renewed 30 s before it expires.
3. A refresh the provider refuses (`invalid_grant`) means the session has ended, and the app asks you to sign in again. Being offline isn't that, and the app just tries later.

After a rebuild, macOS may ask once whether Tally may use its keychain item. Choose **Always Allow**.

## Recordings on the Mac

Each recording has a folder, `~/Library/Application Support/Tally/recordings/<ulid>/`, with `local.json` (its state, [data.md](data.md)) and the audio.

```
recording → saved → uploading → uploaded → sent ──(ready, or no longer listed)──→ folder deleted
              ↑                                └─(failed: Send Again)─→ saved
  broken (couldn't be saved; Try Again encodes the CAF again)
```

- **Recording never needs a sign-in.** Saved recordings wait, and are sent at the first sync after you sign in.
- **Sending is in steps that each survive a failure.** The audio goes to its presigned URL (`uploaded`), then the app tells the API (`sent`). A failed step is tried again at the next sync. A 409 means the API has it already.
- **After a crash or a quit,** a recording with a CAF is encoded and becomes `saved`, as is one with only its m4a. An `uploading` one goes back to `saved`. One with no audio is removed.
- **Sync** runs every 15 s while anything is on its way, otherwise every 60 s, and at once after a stop, a sign-in, or opening the menu or window. A sent recording is deleted once its meeting is ready, or once it isn't listed any more because it was deleted on another device.
- **Little else is kept on disk.** Summaries and transcripts are saved only for the meetings folder, if you keep one. A meeting's files are downloaded when first opened, into `~/Library/Caches/Tally/files/`.
- **Sign Out** forgets the account's meetings in the window, and removes the recordings whose audio it already has and the copies of its files. Recordings not yet sent stay, and go to whoever signs in next.

## Calls

Tally notices calls, so one isn't forgotten, or recorded for an hour after it ended.
- **The offer:** when another app has been on the microphone for 10 s, a drawer drops from the menu bar icon: *Record this call with Tally? Zoom is using your microphone.* It has **Record** and **Not Now**, and folds back after 20 s. Tally never starts recording on its own in this mode, because only the person recording knows whether the others agreed.
- **The end:** a recording that started during a call follows it. When the call's apps have left the microphone and the speakers for 30 s, the recording stops. **Continue Recording** carries on in the same recording. **Send Now** finishes it, and so does waiting 30 s. If a new call starts in those 30 s, the drawer asks whether it's the same meeting.
- **Auto mode** (When a call starts: Record Automatically): Tally starts recording when a call starts, and the drawer offers **Don't Record**. A recording it started that lasted under 2 minutes, such as a lobby or a mic test, isn't kept.
- **Detecting:** `AudioUsers` reads Core Audio's list of processes using audio once a second. It needs no permission, and Tally's own process never counts. `CallRules` turns that into events, and is tested without Core Audio.
- **The app's name** comes from the process, or for a helper (such as "Google Chrome Helper") from the app it belongs to.
- **The drawer** is a non-activating panel at the menu bar's level. It never takes focus from the call app, and shows over a full-screen call.
- **Screen sharing:** the drawer and the menu bar icon set `sharingType = .none`. With `screencapture` on macOS 26, both were left out. It's still to be checked in a real Zoom, Teams or Meet share.
- **Its log:** call events go to the unified log, with no meeting content: `/usr/bin/log show --predicate 'subsystem == "<bundle id>"'`.

## The menu bar

- **The icon** is Tally's "T.". Its full stop is red while recording and a ring in auto mode, and a dot shows when an update is ready.
- **The menu** has Start Recording (⌘R), notices (problems, an update, a crash report, a sign-in prompt), the four latest meetings, Open at login, the meetings folder, When a call starts, Open Tally, Account and Quit.
- **While recording** it shows the time, what's being recorded, the two levels, Stop Recording and Discard.
- **Right-click** gives a plain menu (Start or Stop Recording, Open Tally, Quit).
- **Quit** while recording ends the recording and keeps it. Tally saves and sends it at the next launch.
- **The meetings folder** keeps a copy of every summarised meeting as Markdown and JSON in a folder you choose, with a README for tools and agents ([data.md](data.md)). It's one way: edits in Tally reach it, and edits there are overwritten.

## The window

The window opens by itself on the first launch, with a short welcome. After that Tally starts in the menu bar only, and is in the Dock only while the window is open. It's laid out in three columns, as Mail is:
- **The sidebar:** Meetings (all of them, with a count of those with someone to confirm), People, and the owner's own folders, which nest.
- **The list:** the meetings of the place chosen, grouped by day, each with its state and a count of people to confirm. A meeting can be in any number of folders. Search in the toolbar finds meetings by title, summary, people or transcript.
- **The page:** the meeting or person chosen.

A meeting on its way shows where it is: recording, waiting for sign-in, sending, summarising, or failed with Send Again or Delete.

A ready meeting has three tabs, Summary, Transcript and Files (⌘1 to ⌘3):
- **The header:** title, date, length and language, a chip for each settled speaker, and its folders.
- **To confirm:** speakers Tally isn't sure of, each a card with **Listen**. The agent's guess asks *Is this Alex?*, and an unknown speaker asks *Who's This?*. The picker lists your people closest in voice, finds anyone by name, or adds someone new. Someone who spoke for under 20 s isn't asked about.
- **Background voices:** a voice nearby that wasn't in the meeting can be set aside, and the summary rewritten without it.
- **The summary** edits in place, with undo. The first change makes your own copy, and **Restore Original…** goes back to the agent's.
- **Files:** drop, paste or add files and screenshots. Space opens one in Quick Look, as in Finder.
- **A suggested folder,** when you've usually filed meetings with these people in one folder.

**People** lists everyone, with a card for the one chosen: their role and company, their voice, other names, and their meetings. Deleting a person forgets their voice.

## Crashes and notifications

- **After a crash,** the next launch offers **Send Report** and **Don't Send**. The newest report goes to the API (`POST /crashes`), which keeps it and emails config's `crashEmail` when one is set.
- **Notifications** are asked for at the first Stop. When a meeting is ready, the notification says how many people are to confirm, and clicking it opens the meeting (`tally://meeting/<id>`).

## Next

- **Voiceprints over time:** flag one that disagrees with the person's others, and keep each person's best twenty or so.
- **A global start/stop hotkey.**
- **Not yet checked on a real call or with AirPods:** that the tap's channels follow the mic's in every device combination.
