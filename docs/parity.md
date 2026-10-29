# The Mac and Windows apps, side by side

The two apps do the same things for the same people, against the same API ([api.md](api.md)). Each is written for its platform: SwiftUI and Core Audio on the Mac ([macos.md](macos.md)), WinUI 3 and WASAPI on Windows ([windows.md](windows.md)). This page compares them, so neither falls behind unnoticed.

**Keep it true.** A change to what one app does updates this page in the same change. Either the other app does it too, or it's a gap here, with a reason.

## How they're built

| | Mac | Windows |
|---|---|---|
| Code | Swift, `apps/mac/` | C#, `apps/windows/` |
| UI | SwiftUI, a menu bar extra and one window | WinUI 3, a notification-area icon with a flyout, and one window |
| Logic | `AppModel.swift` and its neighbours | `Tally.Core`, ported from them with the same names, tested on any OS |
| Settings | `tally.json` from `apps/mac/settings.sh` | The same file, from `make windows-settings` or `settings.ps1` |
| Tests | `make app-test` | `make windows-test` (the Core), and CI builds the app |
| Installing | `install.sh` builds it with the Command Line Tools | `install.ps1` builds it with the .NET SDK, installed for the user if missing |

## What they do

| Feature | Mac | Windows | Notes |
|---|---|---|---|
| **Recording** | | | |
| Mic and system audio, one 2-channel file | Core Audio tap + aggregate device | WASAPI mic + process loopback | Both leave Tally's own sound out |
| A quiet room still records | The tap doesn't auto-start | Wall-clock writer | |
| Crash-safe file, recovered at launch | CAF | WAV, header updated every second | |
| Device changes mid-recording | Rebuilds the aggregate | Moves the mic capture | Neither checked with real headsets yet |
| Levels, lost mic | ✓ | ✓ | |
| A recording's longest | 4 hours | 4 hours | The longest the pipeline summarises |
| The disk fills while recording | Writes are dropped unnoticed | Stops, keeps what it has, says why | See Gaps |
| No system sound for a minute | A hint that the permission may be missing | — | Windows asks no permission for loopback |
| System audio can't be recorded | — | Said in the flyout, and tried again every second | A Windows capture can fail on its own |
| Permission prompts | Mic and system audio | None; Tally says when Settings turns the mic off | |
| **Sending and sync** | | | |
| Offline queue, steps that survive a failure | ✓ | ✓ | The same rules, ported |
| Sync every 15 s or 60 s, and on opening | ✓ | ✓ | |
| Recording deleted once its meeting is ready or gone | ✓ | ✓ | |
| **Sign-in** | | | |
| OIDC with PKCE | Web sign-in sheet | Default browser, back through `tally://` | |
| Refresh token kept | Login keychain | Credential Manager | |
| An abandoned sign-in | The sheet says it was cancelled | Sign In stays, and a new one replaces it | The browser never says |
| **Menu bar and notification area** | | | |
| Icon: the mark, red while recording, ring in auto mode, update dot | ✓ | ✓ | Drawn in the taskbar's colour on Windows |
| Panel: record, notices, recent meetings, settings, account, quit | ✓ | ✓ | |
| Right-click menu | ✓ | ✓ | |
| Recording panel: time, levels, stop, discard | ✓ | ✓ | |
| **Calls** | | | |
| Detecting a call | Core Audio's process list | WASAPI's audio sessions | Both once a second, no permission |
| The drawer: offer, auto mode, same or new meeting | ✓ | ✓ | `CallRules`, the same timings |
| Stop when the call ends, Continue Recording, Send Now | ✓ | ✓ | |
| Left out of screen captures | Drawer and icon | Drawer and flyout | Windows can't hide the taskbar's icon |
| **The window** | | | |
| Three columns: places, the list, the page | Sidebar | Navigation pane, folding below 900 px | |
| Folders: nested, renamed in place, dropped onto | ✓ | ✓ | New Folder is a row at the end on Windows, as in Outlook |
| Search, with the words in bold | Toolbar | Title bar | |
| Where you are | The window's title | The list's heading, and the taskbar's title | As Outlook shows it |
| The banner | ✓ | ✓ | |
| A meeting on its way, each state's page | ✓ | ✓ | |
| Header, chips, folders, Edited by you, Restore Original | ✓ | ✓ | |
| To confirm: cards, Listen, Yes… / No…, Who's This? | ✓ | ✓ | |
| The picker: Sounds like, type to find, someone new | A sheet | A dialog | Buttons in Windows' order |
| Background voices, rewriting without them | ✓ | ✓ | |
| The summary edited in place, with undo | ✓ | ✓ | On Windows, Enter adds a point and × deletes one |
| Action items: tick, owner, due date | ✓ | ✓ | |
| Transcript, Copy | ✓ | ✓ | |
| Files | Quick Look | Opens in the file's app | Windows has no Quick Look |
| Files dragged from new Outlook | — | Tally says to save them first | New Outlook hands attachments over only after the drop |
| A suggested folder | ✓ | ✓ | |
| People: the list, Sort By, the card, Edit, Delete | ✓ | ✓ | |
| Keys | ⌘ | Ctrl, F2, Delete | |
| **Elsewhere** | | | |
| Notifications that open the meeting | ✓ | ✓ | |
| Updates from the install page | `version.json` | `windows.json` | |
| Crash reports | macOS's reports | Tally's own | .NET apps get no OS report worth sending |
| The meetings folder | ✓ | ✓ | The same layout and README |
| Open at login | Login items | Startup apps | |
| Uninstall | Drag to the Bin | Settings → Apps | |

## Gaps

**The Mac should follow Windows:**
- **A full disk while recording.** The Mac drops writes unnoticed, the clock runs on, and Stop then can't save. Windows stops, keeps what it has and says why. This gap matters most, because a meeting is cut short without anyone seeing it.
- **A `local.json` that can't be read** (a power cut as it was written). The Mac leaves the recording out of the list, though its audio stays. Windows rebuilds it from its folder.
- **New Meeting after a call's end.** The Mac starts recording the new call only once the stopped one is saved. Windows starts at once, so the new meeting's opening isn't lost while the old one encodes.
- **A Same Meeting that can't carry on.** The Mac says why and waits for Send Now. Windows goes on as if unanswered: it sends the stopped one, or starts a new meeting in auto mode.
- **Continue Recording that fails while *Same meeting?* shows.** The Mac drops the question; Windows keeps it, to answer.
- **Do Nothing chosen while *Same meeting?* shows.** The Mac drops the question, and the stopped recording waits for Send Now. Windows sends it, as if unanswered.

**Differences by platform:**
- **Smart App Control and an organisation's app control** block a Tally built on a Windows PC, and the installer stops and says so ([troubleshooting.md](troubleshooting.md#smart-app-control-or-app-control-blocks-tally)).
- **The taskbar's icon in a screen share:** Windows can't leave it out, since Explorer draws it. Its flyout and drawer are left out.
- **No-sound hint:** Windows has no system-audio permission, so it needs no hint.

**Still to check on Windows:**
- The Mac app is used every day. The Windows app has run only on Windows Server 2025, with a virtual display and audio device, and a stand-in backend and call app.
- On a Windows 11 PC: a real microphone and headset, Zoom, Teams and Meet calls and screen shares, sign-in with a real account, Narrator, and switching the theme while it runs.
- Not exercised yet: pasting an image, Add Files…, dropping files from Explorer, dragging a meeting out to another app, the folded background-voice lines, a rewrite, and redo.
