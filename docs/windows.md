# Windows app

Tally's Windows app is in `apps/windows/`. It's a notification-area app with one window, for Windows 11. It does what the Mac app does ([macos.md](macos.md)), the Windows way, and [parity.md](parity.md) compares the two. It talks only to Tally's API ([api.md](api.md)) and the sign-in provider, and holds no AWS credentials.

## Building

C# on .NET 10 with WinUI 3 (the Windows App SDK). It's unpackaged and self-contained, so it installs per user with nothing else first. There are three projects, in `apps/windows/Tally.slnx`:

| Project | What | Builds on |
|---|---|---|
| `Tally.Core` | Everything that isn't Windows: the API, sign-in, recordings, the sync, calls, the meetings folder, and `AppModel`. `Platform.cs` is what it needs from Windows | Any OS |
| `Tally.Core.Tests` | Its tests (xUnit v3, Microsoft.Testing.Platform) | Any OS |
| `Tally` | The app: the icon and its flyout, the call drawer, the window, the recorder, and `Platform/`, which implements `Platform.cs` | Windows |

```
make windows-settings   # apps/windows/.build/tally.json, from the deployed stack
make windows-test       # the Core's tests, on any OS with the .NET 10 SDK
```

On Windows, with the .NET 10 SDK:
```
powershell -ExecutionPolicy Bypass -File apps\windows\settings.ps1     # tally.json, without make or bash
dotnet publish apps\windows\Tally\Tally.csproj -c Release -r win-x64 -p:Platform=x64 -o build
build\Tally.exe
```

- **Settings:** `tally.json` beside `Tally.exe`, the same file the Mac app has. The build refuses to start without it, and `-p:TallySettings=<path>` builds with another.
- **ARM64:** `-r win-arm64 -p:Platform=ARM64`.
- **Warnings are errors** in every project (`Directory.Build.props`). The installer builds with them as warnings, so that a newly published security advisory for a package can't stop every install until a new version is out.
- **The views are code,** built from the model as SwiftUI's are, with small builders in `Views/Ui.cs`. Only `App.xaml` and `MainWindow.xaml` are XAML. The app itself builds only on Windows, because the XAML compiler doesn't run elsewhere (measured on Linux, 2026-10-28).

## Installing and updating

The install page has a Windows command beside the Mac's:
```
irm https://<site>/install.ps1 | iex
```
`site/install.ps1` builds Tally on the PC, as `install.sh` does on the Mac:
1. It checks for Windows 11, Smart App Control and an organisation's app control, and refuses to update while Tally is recording.
2. It downloads `tally-windows-<version>.tar.gz` (the `apps/windows` source and `tally.json`) and checks its sha256.
3. It uses the .NET 10 SDK if there is one. Otherwise it installs it for this user alone, in `%LOCALAPPDATA%\Tally\dotnet`, with Microsoft's `dotnet-install.ps1`. It needs no administrator rights.
4. It builds Tally for the PC's architecture, closes the running one, and installs it in `%LOCALAPPDATA%\Programs\Tally`.
5. It adds a Start menu entry and an entry in Settings → Apps, and opens Tally through Explorer. That way Tally runs at the user's own level even when installed from an admin terminal, since an elevated WinUI app can't take drops or use its pickers.

It logs to `%LOCALAPPDATA%\Tally\Logs\install.log`, and every failure says what failed. Files PowerShell downloads carry no mark of the web, so SmartScreen has nothing to warn about.

**Execution policy:** one that Group Policy sets applies to script files, and no `-ExecutionPolicy` overrides it. So installing, updating and uninstalling run no script file: the install command, an update from inside Tally and the uninstaller run their script through `iex`, and the .NET SDK's installer runs as a script block.

**Smart App Control** blocks an app built on the PC, so the installer stops before anything when it's on. **An organisation's app control** (AppLocker, App Control for Business) does the same, and the installer stops when it can tell. A signed build would run with Smart App Control on, and an organisation could allow it by its publisher. [troubleshooting.md](troubleshooting.md#smart-app-control-or-app-control-blocks-tally) explains both in full.

`make site` makes the tarball from the commit that last changed `apps/windows/` or `site/`, and writes `windows.json` (the version) and `install.ps1` with the version and checksum filled in. `make publish-site` uploads them with the Mac's.

On a fresh Windows Server 2025 machine with 4 vCPUs (2026-10-28), a first install took 170 s and an update 60 s. Installed, Tally is 219 MB, since it carries .NET and the Windows App SDK.

**Updates:** a build from the install page checks `windows.json` at launch, every 30 minutes and when the flyout opens. When there's a newer version, the icon gets a dot and the flyout offers **Update**. That fetches the installer and runs it, which builds the new version, closes this one and opens the new one. It's off while recording. If it fails, the flyout says why.

**Uninstalling** (Settings → Apps → Tally → Uninstall) removes the app, its Start menu entry, opening at sign-in and the `tally://` links. It refuses while recording. `%LOCALAPPDATA%\Tally` and the saved sign-in stay, and [troubleshooting.md](troubleshooting.md#start-completely-fresh-1) says how to remove them.

## Files

In `apps/windows/Tally/`:

| File | What |
|---|---|
| `Program.cs` | One Tally at a time: a second launch (the sign-in's callback, a notification, Start) hands what it got to the running one |
| `App.xaml.cs` | The notification-area icon, its flyout and menu, the drawer, and the window, wired to the model |
| `Audio/Recorder.cs` | WASAPI: the microphone and process loopback (everything but Tally), written to a 2-channel WAV |
| `Platform/WindowsAudio.cs` | `IAudio`: the recorder, the encoder, and the apps using audio, for calls |
| `Platform/Browser.cs` | Sign-in in the browser, and the `tally://` scheme |
| `Platform/Secrets.cs` | The refresh token in Credential Manager |
| `Platform/Notifications.cs`, `ClipPlayer.cs`, `LoginItem.cs`, `Shell.cs`, `Installer.cs`, `CrashReports.cs`, `Log.cs` | Notifications, clips, Startup apps, dialogs and pickers, the installer, crash reports and the log |
| `Tray/TrayIcon.cs`, `MarkIcon.cs`, `Panel.cs`, `Native.cs` | The icon, Tally's mark drawn for the taskbar, the flyout panel that stands on the taskbar, and the Win32 calls |
| `Views/MenuPanel.cs`, `Notices.cs`, `DrawerPanel.cs` | The flyout, the notices it shares with the window, the call drawer |
| `MainWindow.xaml(.cs)`, `Views/*` | The window |
| `settings.ps1`, `uninstall.ps1` | The settings without bash, and the uninstaller (in `apps/windows/`) |

## Recording

- **The microphone:** the default capture device, in shared mode, as 32-bit float at 48 kHz.
- **The system audio:** WASAPI process loopback of every process except Tally's own process tree. It's the Windows counterpart of the Mac's tap, and Windows asks no permission for it.
- **One clock:** the two captures run on their own device clocks, and loopback delivers nothing while nothing plays. So a writer thread keeps time by the wall clock. Every 20 ms it writes the frames due, with silence where a capture delivered too little, and drops a backlog over 200 ms. Channel 1 is the mic and channel 2 the system.
- **Files:** `recording.wav` (16-bit PCM, 2 channels, 48 kHz) while recording. Its header is updated every second, so a crash leaves a file that plays up to then. On stop, Media Foundation encodes 2-channel AAC at 128 kbps as `audio.m4a`.
- **Device changes:** when the default microphone changes, the capture moves to it within a second, and the same file carries on. A capture that stops or can't be opened is tried again every second.
- **What Tally opens isn't left out.** Loopback leaves out Tally's whole process tree, so a browser Tally started would be left out too, with its call unheard. Tally therefore opens links, files and its sign-in through Explorer.
- **Power cuts:** files Tally writes whole reach the disk before they replace the old ones. A recording whose `local.json` can't be read is rebuilt from its folder.
- **Sleep:** time the PC spent asleep isn't written as silence.
- **Limits:** a recording stops at 4 hours, as on the Mac, and when the disk is full. Either way, what was recorded is kept and sent, and Tally says why it stopped.
- **No AAC encoder** (Windows 11 N without the Media Feature Pack): the recording is kept as its WAV, and Tally says to add the Media Feature Pack.
- **Permissions:** none to ask for. If microphone access is turned off in Settings → Privacy & security → Microphone, Tally says so, with **Open Settings**.
- **Measured** on Windows Server 2025 with a virtual audio device (2026-10-28): a tone another process played came out in channel 2 at its level; a pause and a frozen process were left out of the file's length; a full disk stopped the recording cleanly; and restarting the audio service brought the mic back. The mic channel was checked only for silence.

## Signing in

Sign-in uses the deployment's OIDC provider ([api.md](api.md)):
1. The app opens the provider's authorization endpoint in the default browser, with PKCE S256 and the redirect `tally://callback`.
2. Windows starts Tally with the callback. The new process hands it to the running one, which exchanges the code for tokens. Tally registers `tally://` for the current user at every launch, so it always points at this copy.
3. The refresh token is kept in Credential Manager, as the generic credential `Tally/refresh-token`. The access token stays in memory and is renewed 30 s before it expires.
4. A refresh the provider refuses (`invalid_grant`) means the session has ended, and the app asks you to sign in again.
5. A sign-in abandoned in the browser doesn't block the next one. Sign In stays there, and a new attempt replaces the old.

## Recordings on the PC

Recordings follow the Mac's rules ([macos.md](macos.md#recordings-on-the-mac)): the same states, sending in steps, recovery after a crash, and the same sync. The places are Windows', all under `%LOCALAPPDATA%\Tally\` ([data.md](data.md#on-windows)):
- `recordings\<ulid>\`: `local.json`, and `recording.wav`, then `audio.m4a`
- `settings.json`: what the Mac keeps in UserDefaults
- `Cache\files\`: meetings' files, downloaded when first opened
- `Logs\`: `tally.log` (steps, calls and the meetings folder, with no meeting content) and `install.log`
- `Crashes\`: crash reports Tally writes itself

## The notification area and calls

- **The icon** is Tally's "T.", drawn in the taskbar's own colour, with the same red full stop, ring and update dot as the Mac's. Windows 11 puts a new app's icon in the overflow (**^**) until it's dragged beside the clock.
- **A click** opens the flyout, which stands on the taskbar as Windows' own flyouts do. It holds what the Mac's menu holds. **A right-click** shows a plain menu.
- **Calls** follow the Mac's rules, decided by the same `CallRules` in the Core. [parity.md](parity.md) lists where they differ.
- **Detecting:** `WindowsAudio.Users` reads every active audio session once a second. A process with an active capture session is on the microphone, and one with an active render session is playing. It needs no permission.
- **The drawer** stands on the taskbar like the flyout. It never takes focus from the call app, and shows over a full-screen call.
- **Screen sharing:** the flyout and the drawer are left out of screen captures (`WDA_EXCLUDEFROMCAPTURE`). The icon can't be, because Explorer draws the taskbar. To screenshot Tally's own panels, start it with `TALLY_SHOW_IN_CAPTURES=1`, and with `--menu` to open the flyout at once.
- **Measured** with a stand-in call app (2026-10-28): the offer came 10 s into the call, and in auto mode the recording ran from the call's start to its end. Not yet with Zoom, Teams or Meet.

## The window

The window holds what the Mac's does ([macos.md](macos.md#the-window)), drawn from the same model, the way a Windows 11 app shows it. [parity.md](parity.md) says where it differs on purpose.
- **The navigation pane:** Meetings, People, and the owner's folders, nested. It narrows to icons below 900 px wide, and becomes a button below 600 px.
- **The list:** the meetings of the place chosen, grouped by day.
- **Search** is in the middle of the title bar (Ctrl+F).
- **The page:** a meeting with Summary, Transcript and Files (Ctrl+1 to Ctrl+3), or a person's card. The summary edits in place, with Ctrl+Z and Ctrl+Y.
- **The banner** under the title bar shows the same notices as the flyout.
- **Keys:** Ctrl+R records or stops, Ctrl+Shift+N makes a folder, F2 renames one, and Delete deletes what's selected.

It was checked in light and dark on Windows Server 2025, against a stand-in backend with meetings in every state (2026-10-28). It's not yet checked with Narrator, or with a real account.

## Crashes and notifications

- **Crash reports:** Windows writes no report a person could send for a .NET app, so Tally writes its own when it fails unexpectedly. It holds the exception and the versions, never meeting content. At the next launch Tally offers **Send Report** and **Don't Send**, as on the Mac.
- **Notifications** are Windows' own, through the Windows App SDK. When a meeting is ready, the notification says how many people are to confirm, and clicking it opens the meeting. There's no permission to ask for.
