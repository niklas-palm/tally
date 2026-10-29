# Windows app

The Windows app is in `apps/windows/`: a notification-area app with one window, for Windows 11. It does what the Mac app does ([macos.md](macos.md)), and its model is the Mac's, ported with the same names. It talks only to Tally's API ([api.md](api.md)) and the sign-in provider, and holds no AWS credentials.

## Building

C# on .NET 10 with WinUI 3, unpackaged and self-contained. `apps/windows/Tally.slnx` has three projects:

| Project | What | Builds on |
|---|---|---|
| `Tally.Core` | Everything that isn't Windows, and `Platform.cs`, what it needs from Windows | Any OS |
| `Tally.Core.Tests` | Its tests | Any OS |
| `Tally` | The app, with `Platform/` implementing `Platform.cs` | Windows |

```
make windows-settings   # apps/windows/.build/tally.json, from the deployed stack
make windows-test       # the Core's tests, with the .NET 10 SDK
```

On Windows:

```
powershell -ExecutionPolicy Bypass -File apps\windows\settings.ps1     # tally.json, without make
dotnet publish apps\windows\Tally\Tally.csproj -c Release -r win-x64 -p:Platform=x64 -o build
```

- **Settings:** the build needs `tally.json`, the Mac app's settings.
- **ARM64:** `-r win-arm64 -p:Platform=ARM64`.
- **Warnings are errors** in every project (`Directory.Build.props`).
- **The views are code,** built from the model as SwiftUI's are. The app builds only on Windows, where the XAML compiler runs.

## Installing, updating and uninstalling

```
irm https://<site>/install.ps1 | iex
```

`site/install.ps1` builds Tally on the PC without administrator rights. It refuses while Tally is recording, installs the .NET 10 SDK for this user if it's missing, and installs in `%LOCALAPPDATA%\Programs\Tally`. It logs to `%LOCALAPPDATA%\Tally\Logs\install.log`.

No step runs a script file, since a Group Policy execution policy applies to script files whatever `-ExecutionPolicy` says.

Smart App Control and an organisation's app control block apps built on the PC, so the installer stops when it can tell ([troubleshooting.md](troubleshooting.md#smart-app-control-or-app-control-blocks-tally)). A signed build would fix it.

An installed app checks for a new version every 30 minutes and offers **Update**, which runs the installer. Uninstalling, in Settings → Apps, refuses while recording, and leaves `%LOCALAPPDATA%\Tally` and the sign-in ([troubleshooting.md](troubleshooting.md#start-completely-fresh-on-windows)).

## Source

In `apps/windows/Tally/`:

| File | What |
|---|---|
| `Program.cs` | One Tally at a time |
| `App.xaml.cs` | The icon, flyout, drawer and window, wired to the model |
| `Audio/Recorder.cs` | The mic and the system audio, in a 2-channel WAV |
| `Platform/` | What the Core needs from Windows |
| `Tray/` | The icon, and the panels that stand on the taskbar |
| `MainWindow.xaml(.cs)`, `Views/` | The window, the flyout and the drawer |

## Recording

The mic is the default capture device. The system audio is WASAPI process loopback of everything but Tally's own process tree, which needs no permission. So that a browser Tally starts isn't left out too, Tally opens links and files through Explorer.

Loopback delivers nothing while nothing plays, so a writer thread keeps time by the wall clock, writing silence where a capture fell short. Channel 1 is the mic, channel 2 the system. When the default mic changes, the capture follows it.

Tally writes `recording.wav` while recording, its header updated every second so a crash leaves a file that plays, and encodes AAC (`audio.m4a`) on stop. Without an AAC encoder (Windows 11 N), it keeps the WAV and says to add the Media Feature Pack. A recording stops at 4 hours or on a full disk, and what was recorded is sent.

## Signing in

Sign-in opens the provider in the default browser, with PKCE and `tally://callback`. Windows starts a second Tally with the callback, which hands it to the running one. The refresh token is in Credential Manager as `Tally/refresh-token`.

## Recordings on the PC

Recordings follow the Mac's rules ([macos.md](macos.md#recordings-on-the-mac)), under `%LOCALAPPDATA%\Tally\` ([data.md](data.md#on-windows)): `recordings\`, `settings.json`, `Cache\files\`, `Logs\` and `Crashes\`. Tally writes its own crash reports. Files are written whole before they replace the old, and a recording whose `local.json` can't be read is rebuilt from its folder.

## Calls

Calls follow the Mac's rules, from the same `CallRules`. Once a second, `WindowsAudio.Users` reads the active audio sessions: a capture session is on the mic, a render session is playing. The flyout and the drawer are left out of screen captures; the icon can't be.
