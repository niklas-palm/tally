# Troubleshooting

Fixes for installing Tally, its permissions and sign-in: the Mac first, then Windows. Below, `<bundle id>` is the deployment's `bundleId` setting, which whoever invited you can tell you.

If the install fails, the last lines of its log say why:

```sh
tail -n 50 ~/Library/Logs/Tally/install.log
```

## “xcrun: error” or the Command Line Tools won't install

Install them yourself, then run the install command again:

```sh
xcode-select --install
```

If macOS says they're installed but Swift is missing, it may point to a deleted Xcode. Reset it with `sudo xcode-select --reset`. If that doesn't help, reinstall them:

```sh
sudo rm -rf /Library/Developer/CommandLineTools
xcode-select --install
```

Xcode works too, once its licence is accepted (`sudo xcodebuild -license accept`).

## “Tally needs Swift 6.2 or later”

Your Command Line Tools are older than Xcode 26's. Reinstall them with the two commands above, click Install in the dialog, and run the install command again. If you use Xcode instead, update it to Xcode 26.

## “Building failed”

The log has the compiler's errors. Most often the Command Line Tools are older than macOS: update them first. If it still fails, send the log to the person who invited you.

A `codesign` error such as "no identity found" or "errSecInternalComponent" means the signing certificate isn't in the keychain. Check:

```sh
security find-identity -p codesigning | grep "Tally Local"
```

"CSSMERR_TP_NOT_TRUSTED" next to it is expected. If it's missing, unlock your login keychain and run the install command again:

```sh
security unlock-keychain ~/Library/Keychains/login.keychain-db
```

To start over, delete it with `security delete-identity -c "Tally Local"` (or, if there are two, `-Z <hash>` with each hash `find-identity` prints) and run the install command again.

## I can't find Tally after closing it

Tally lives in the menu bar, not the Dock. Open it from Spotlight (⌘-Space, then "Tally"), or with `open -a Tally`. If `/Applications` wasn't writable, it's in `~/Applications`. A menu bar crowded by the notch can hide its icon: hold ⌘ and drag icons to make room.

## Sign-in says I don't have access

Your account hasn't been given Tally yet. Ask the person who invited you to add you, then sign in again; with some providers it takes a few minutes.

## The recording has the other people silent, or no microphone

While recording, Tally's menu shows two levels, **Microphone** and **System audio**. If one stays flat while there's sound, macOS isn't giving Tally that audio:

- In System Settings → Privacy & Security, turn Tally on under **Microphone** and **Screen & System Audio Recording** (System Audio Recording Only).
- If it isn't listed, or that doesn't help, reset its permissions, quit and reopen Tally, and allow both on the next recording:

```sh
tccutil reset All <bundle id>
```

The mic is whichever input macOS uses (System Settings → Sound → Input).

## macOS keeps asking for my keychain password

Each new build is a new app to the keychain, so it asks once per install. Choose **Always Allow**. If it asks at every launch, choose Account → Sign Out in Tally's menu, and sign in again.

## A meeting failed, or the summary never comes

A summary takes a few minutes, or about 10 when several meetings are on their way. A failed meeting has **Send Again**, since the recording stays on your computer until its summary is ready. *No Speech Heard* and *Too Long to Summarise* (over 4 hours) have only Delete, because sending again would fail the same way. If it fails twice, tell the person who invited you, with the meeting's time.

## Tally crashed or froze

Open it again: a recording that was running is recovered, and Tally offers to send the crash report.

## Start completely fresh

This removes the app, its settings and its saved sign-in. Recordings not yet sent are in `~/Library/Application Support/Tally/recordings`: move them first to keep them.

```sh
osascript -e 'quit app "Tally"'
tccutil reset All <bundle id>
rm -rf /Applications/Tally.app ~/Applications/Tally.app
rm -rf ~/Library/Application\ Support/Tally ~/Library/Caches/Tally
defaults delete <bundle id>
security delete-generic-password -s <bundle id>
```

Then run the install command again. To uninstall for good, also delete the signing certificate (`security delete-identity -c "Tally Local"`) and Tally's entry in System Settings → General → Login Items & Extensions. Your meetings stay in your account until you delete them in Tally.

## On Windows

If the install fails, the last lines of its log say why:

```powershell
Get-Content "$env:LOCALAPPDATA\Tally\Logs\install.log" -Tail 50
```

### Smart App Control or app control blocks Tally

Tally is built on your PC, so it isn't signed, and Windows can block it.

**Smart App Control** runs only apps that are signed or that Microsoft knows. It's in Settings → Privacy & security → Windows Security → App & browser control → Smart App Control settings. When it's on, the installer stops with "Smart App Control is on". When it's evaluating, the installer carries on, but Windows blocks Tally if it turns on later. Turning it off is your call, and recent Windows updates let you turn it back on.

**An organisation's app control** (AppLocker, or App Control for Business) runs on some managed PCs. When its rules cover scripts, the installer stops with "This PC's organisation runs app control". When they cover apps alone, the install stops at the .NET SDK or the build, or Tally won't open. Whoever manages the PC decides whether Tally may run.

A signed build of Tally, which doesn't exist yet, would fix both.

### “The build failed” or “Installing the .NET SDK failed”

The log has the errors. Usually a download was cut short, so run the install command again. A proxy that blocks `dot.net`, `builds.dotnet.microsoft.com` or `nuget.org` stops the install: ask whoever runs your network.

### I can't find Tally

It's the **T.** by the clock. Windows puts a new app's icon in the overflow: click **^** by the clock, and drag the **T.** onto the taskbar. It's also in the Start menu.

### The microphone level stays flat

Turn on Settings → Privacy & security → Microphone → **Let desktop apps access your microphone**. The mic is whichever input Windows uses (Settings → System → Sound → Input). System audio needs no permission.

### Notifications don't show

Turn them on in Settings → System → Notifications → Tally.

### Start completely fresh on Windows

Uninstall Tally in Settings → Apps. Recordings not yet sent are in `%LOCALAPPDATA%\Tally\recordings`: move them first to keep them. Then remove the saved sign-in and Tally's folder, and run the install command again:

```powershell
cmdkey /delete:Tally/refresh-token
Remove-Item "$env:LOCALAPPDATA\Tally" -Recurse -Force
```
