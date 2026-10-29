# Troubleshooting

Fixes for what can go wrong on a Mac or a PC when installing Tally, with its permissions and with sign-in. The Mac comes first, and Windows has its own section at the end. Below, `<bundle id>` is the deployment's `bundleId` setting, which the person who invited you can tell you.

The installer writes everything it does to a log. If the install fails, its last lines say why:

```sh
tail -n 50 ~/Library/Logs/Tally/install.log
```

## “xcrun: error” or the Command Line Tools won't install

Install them yourself, then run the install command again:

```sh
xcode-select --install
```

If macOS says they're installed already but Swift is missing or broken, it may still point to an Xcode that was deleted. Reset it:

```sh
sudo xcode-select --reset
```

If that doesn't help, reinstall them:

```sh
sudo rm -rf /Library/Developer/CommandLineTools
xcode-select --install
```

If you have Xcode, that works too, once its licence is accepted (`sudo xcodebuild -license accept`). Check that `xcode-select -p` points to it, or to `/Library/Developer/CommandLineTools`.

## “Tally needs Swift 6.2 or later”

Tally needs the Command Line Tools for Xcode 26, and your Mac has older ones, from Xcode 16 (Swift 6.1). See which are in use:

```sh
xcode-select -p
swift --version
```

If the first line is an Xcode in Applications, the installer uses the Command Line Tools instead when those are new enough. Otherwise install the newest Command Line Tools, then run the install command again:

```sh
sudo rm -rf /Library/Developer/CommandLineTools
xcode-select --install
```

Click Install in the dialog and wait for it to finish. If you use Xcode and want to keep it selected, update it to Xcode 26 from the App Store instead.

## “Building failed”

The log has the compiler's errors. Most often the Command Line Tools are older than macOS, so update them first. If it still fails, send the log to the person who invited you.

A `codesign` error such as "no identity found" or "errSecInternalComponent" means the signing certificate didn't make it into the keychain. Check that it's there:

```sh
security find-identity -p codesigning | grep "Tally Local"
```

"CSSMERR_TP_NOT_TRUSTED" next to it is expected and harmless. If it's missing, unlock your login keychain and run the install command again:

```sh
security unlock-keychain ~/Library/Keychains/login.keychain-db
```

To start over, delete it and run the install command again. If there are two, delete each by the hash `find-identity` prints before its name:

```sh
security delete-identity -c "Tally Local"
security delete-identity -Z <hash>
```

## I can't find Tally after closing it

Tally lives in the menu bar, not the Dock. After Quit, open it like any app, from Spotlight (⌘-Space, then "Tally"), from Apps, or with this command. A new install can take a few minutes to show up in Spotlight.

```sh
open -a Tally
```

If the installer couldn't write to /Applications, it put Tally in your own Applications folder, `~/Applications`. A menu bar crowded by the notch can hide its icon. Hold ⌘ and drag icons to make room.

## Sign-in says I don't have access

Your sign-in account works, but it hasn't been given Tally yet. Ask the person who invited you to add you. Once they have, sign in again. With some sign-in providers, it takes a few minutes to reach you.

## The recording has the other people silent, or no microphone

While recording, Tally's menu shows two moving levels, **Microphone** and **System audio**. If one stays flat while there's sound, macOS isn't giving Tally that audio:

- Look in System Settings → Privacy & Security → **Microphone**, and **Screen & System Audio Recording** (under System Audio Recording Only), and turn Tally on.
- If it isn't listed, or turning it on doesn't help, reset Tally's permissions, quit and reopen it, and allow both again on the next recording:

```sh
tccutil reset All <bundle id>
```

The mic is whichever input macOS uses (System Settings → Sound → Input). Plugging in headphones switches it during a recording, and that's fine.

## macOS keeps asking for my keychain password

Each new build of Tally is a new app as far as the keychain is concerned, so it asks once after each install. Choose **Always Allow** rather than Allow. If it asks every launch, choose Account → Sign Out in Tally's menu, and sign in again.

## A meeting failed, or the summary never comes

A summary normally takes a few minutes, or 10 or so when several meetings are on their way at once. A meeting that failed has **Send Again** on its page, since the recording stays on your Mac until its summary is ready. A meeting with nobody speaking in it (*No Speech Heard*), or longer than 4 hours (*Too Long to Summarise*), has only Delete, because sending it again would fail the same way. Otherwise, if there's no Send Again, the recording is no longer on your Mac. If it fails twice, tell the person who invited you, with the meeting's time.

## Tally crashed or froze

Open it again. A recording that was running is recovered from its file. macOS keeps a crash report, which is worth sending:

```sh
ls -t ~/Library/Logs/DiagnosticReports/Tally* | head -1
```

## Start completely fresh

This removes the app, its settings and its saved sign-in. Recordings that haven't been sent yet are in `~/Library/Application Support/Tally/recordings`, so move them somewhere first if you want to keep them.

```sh
osascript -e 'quit app "Tally"'
tccutil reset All <bundle id>
rm -rf /Applications/Tally.app ~/Applications/Tally.app
rm -rf ~/Library/Application\ Support/Tally ~/Library/Caches/Tally
defaults delete <bundle id>
security delete-generic-password -s <bundle id>
```

Then run the install command again. To uninstall for good, also remove the signing certificate (`security delete-identity -c "Tally Local"`) and Tally's entry under System Settings → General → Login Items & Extensions. Your meetings stay in your account until you delete them in Tally.

## On Windows

The installer writes everything it does to a log. If the install fails, its last lines say why, or at which step it stopped. A download that fails says why on screen.

```powershell
Get-Content "$env:LOCALAPPDATA\Tally\Logs\install.log" -Tail 50
```

### Smart App Control or app control blocks Tally

Windows can block apps that aren't signed, and Tally is built on your PC, so it isn't. This comes up in two ways.

**Smart App Control** is a Windows 11 feature that runs only apps that are signed, or that Microsoft already knows. It's in Settings → Privacy & security → Windows Security → App & browser control → Smart App Control settings, and it's on, evaluating or off. When it's on, the installer stops with "Smart App Control is on", since Windows would block Tally from opening. When it's evaluating, the installer says so and carries on. If Smart App Control turns on later, Windows blocks Tally from opening, for the same reason. Turning it off is your call, and Tally doesn't ask you to. Recent Windows updates let you turn it on again in the same place, without resetting Windows.

**An organisation's app control** (AppLocker, or App Control for Business) runs on some PCs that an IT department manages, and it blocks apps built on the PC too. When its rules cover scripts, the installer stops before it changes anything, with "This PC's organisation runs app control". When its rules cover apps alone, the installer can't tell. Windows then blocks what runs from your own folders. Either the install stops partway, at the .NET SDK or the build, with Windows saying a program is blocked, or the install finishes and Tally won't open. Whoever manages the PC decides whether Tally may run there.

A signed build of Tally would fix both. It would run with Smart App Control on, and an organisation could allow it by its publisher. Tally isn't signed yet.

### “The build failed” or “Installing the .NET SDK failed”

The log has the errors. The usual cause is a download cut short, of the .NET SDK or a package the build fetches, so run the install command again. A proxy that blocks `dot.net`, `builds.dotnet.microsoft.com` (the SDK) or `nuget.org` (the build's packages) stops the install from fetching what it needs. Ask whoever runs your network.

### I can't find Tally

It's the **T.** by the clock. Windows puts a new app's icon in the overflow, so click **^** by the clock, and drag the **T.** onto the taskbar to keep it there. It's also in the Start menu.

### The microphone level stays flat

Windows lets desktop apps use the microphone unless that's turned off, in Settings → Privacy & security → Microphone → **Let desktop apps access your microphone**. The mic is whichever input Windows uses (Settings → System → Sound → Input). System audio needs no permission on Windows.

### Notifications don't show

Turn them on in Settings → System → Notifications → Tally.

### Start completely fresh

Uninstall Tally in Settings → Apps. Recordings that haven't been sent yet are in `%LOCALAPPDATA%\Tally\recordings`, so move them somewhere first if you want to keep them. Then remove Tally's saved sign-in and its folder, and run the install command again:

```powershell
cmdkey /delete:Tally/refresh-token
Remove-Item "$env:LOCALAPPDATA\Tally" -Recurse -Force
```
