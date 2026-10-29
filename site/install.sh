#!/bin/bash
# Installs Tally: downloads the app's source, builds it on this Mac with Apple's Command Line Tools, and puts it in
# Applications. Building here, rather than downloading a finished app, is what lets an app without an Apple Developer
# ID open without Gatekeeper's warnings. Run it again to update.
#   curl -fsSL @SITE@/install.sh | bash
# Everything it does goes to ~/Library/Logs/Tally/install.log.
#
# `make site` fills in VERSION and SHA256 (the source tarball's checksum).
set -euo pipefail

VERSION="@VERSION@"
SHA256="@SHA256@"
SITE="@SITE@"
LOG="$HOME/Library/Logs/Tally/install.log"

say() { printf '\033[1m==>\033[0m %s\n' "$*"; echo "==> $*" >>"$LOG"; }
fail() {
  printf '\033[31mTally was not installed:\033[0m %s\n' "$1" >&2
  echo "FAILED: $1" >>"$LOG"
  printf 'Log: %s\n' "$LOG" >&2
  exit 1
}
# A step's output goes to the log, and on failure its last lines to the screen too.
run() {
  local what=$1; shift
  if ! "$@" >>"$LOG" 2>&1 </dev/null; then
    tail -n 20 "$LOG" >&2
    fail "$what failed."
  fi
}

check_mac() {
  [ "$(uname -s)" = Darwin ] || fail "Tally is a Mac app."
  local major
  major=$(sw_vers -productVersion | cut -d. -f1)
  [ "$major" -ge 26 ] || fail "Tally needs macOS Tahoe (26) or later; this Mac has $(sw_vers -productVersion)."
}

TOOLS=/Library/Developer/CommandLineTools

swift_version() {
  (xcrun swift --version 2>/dev/null || true) | sed -n 's/.*Swift version \([0-9]*\.[0-9]*\).*/\1/p' | head -1
}

new_enough() {  # Swift 6.2 or later: the version Package.swift asks for, and the macOS 26 SDK
  local major=${1%%.*} minor=${1#*.}
  [ -n "$1" ] && { [ "$major" -gt 6 ] || { [ "$major" -eq 6 ] && [ "$minor" -ge 2 ]; }; }
}

# The Command Line Tools bring Swift. Their installer is a dialog of its own, so this waits until it's done.
check_tools() {
  if ! xcrun --find swift >/dev/null 2>&1; then
    say "Installing Apple's Command Line Tools: click Install in the dialog (it takes a few minutes)"
    # It fails when macOS thinks they're installed already: a selected Xcode that was deleted, or half-removed tools.
    xcode-select --install >>"$LOG" 2>&1 \
      || fail "The Command Line Tools seem installed but Swift isn't there. Run 'sudo xcode-select --reset', then this command again."
    echo "    Waiting for it to finish. If you cancelled the dialog, press Control-C and run this command again."
    local waited=0
    until xcrun --find swift >/dev/null 2>&1; do
      sleep 10
      waited=$((waited + 10))
      [ "$waited" -lt 3600 ] || fail "The Command Line Tools didn't finish installing within an hour. Install them from the dialog, then run this command again."
    done
  fi
  local swift
  swift=$(swift_version)
  echo "Swift ${swift:-none} at $(xcode-select -p)" >>"$LOG"
  # An older Xcode may be selected while the Command Line Tools beside it are new enough: build with those, no sudo.
  if ! new_enough "$swift" && [ -d "$TOOLS" ]; then
    local tools
    tools=$(DEVELOPER_DIR="$TOOLS" swift_version)
    echo "Swift ${tools:-none} at $TOOLS" >>"$LOG"
    if new_enough "$tools"; then
      export DEVELOPER_DIR="$TOOLS"
      swift=$tools
    fi
  fi
  new_enough "$swift" || fail "Tally needs Swift 6.2 or later (the Command Line Tools for Xcode 26), and this Mac has ${swift:-no working Swift}. Install the newest: run 'sudo rm -rf $TOOLS' and then 'xcode-select --install', click Install, and run this command again. With Xcode, accept its licence first: sudo xcodebuild -license accept"
}

# Quitting Tally ends a recording in progress: an update waits until it has stopped.
check_not_recording() {
  pgrep -x Tally >/dev/null || return 0
  if grep -qs '"state" *: *"recording"' "$HOME/Library/Application Support/Tally/recordings/"*/local.json; then
    fail "Tally is recording. Stop the recording, then run this command again."
  fi
}

main() {
  mkdir -p "$(dirname "$LOG")"
  echo "--- $(date) installing Tally $VERSION on macOS $(sw_vers -productVersion 2>/dev/null) $(uname -m)" >>"$LOG"
  check_mac
  check_tools
  check_not_recording

  work=$(mktemp -d)  # global: the EXIT trap runs after main has returned
  trap 'rm -rf "$work"' EXIT

  say "Downloading Tally $VERSION"
  curl -fsSL --retry 3 -o "$work/tally.tar.gz" "$SITE/tally-$VERSION.tar.gz" >>"$LOG" 2>&1 \
    || fail "Downloading failed. Check your internet connection, then run this command again."
  echo "$SHA256  $work/tally.tar.gz" | shasum -a 256 -c >>"$LOG" 2>&1 || fail "The download is damaged (its checksum is wrong). Try again."
  mkdir "$work/src" && run "Unpacking" tar -xzf "$work/tally.tar.gz" -C "$work/src" --strip-components 2

  say "Making sure the Tally Local signing certificate is in your keychain"
  run "Creating the signing certificate" "$work/src/signing-cert.sh"

  say "Building Tally (a minute or two)"
  run "Building" "$work/src/build.sh" "$work/src/tally.json"

  # /Applications if this account may write there (admins can), otherwise the account's own Applications folder.
  apps=/Applications  # global, as work is: the EXIT trap clears a new app left beside the old by a failure
  [ -w "$apps" ] || { apps="$HOME/Applications"; mkdir -p "$apps"; }
  trap 'rm -rf "$work" "$apps/Tally.app.new"' EXIT
  say "Installing it in $apps"
  # The new app goes in beside the old first, so a failed copy leaves the old one as it was
  rm -rf "$apps/Tally.app.new"
  run "Copying the app" cp -R "$work/src/.build/Tally.app" "$apps/Tally.app.new"
  check_not_recording  # again: a recording may have started during the build, and quitting would cut it short
  osascript -e 'quit app id "@BUNDLE@"' >/dev/null 2>&1 || true
  sleep 1
  pkill -x Tally 2>/dev/null || true
  # The old app moves aside and comes back if the new one can't go in, and opens again: the Mac keeps a working Tally
  rm -rf "$apps/Tally.app.old"
  [ ! -d "$apps/Tally.app" ] || mv "$apps/Tally.app" "$apps/Tally.app.old" || { open "$apps/Tally.app"; fail "The old app couldn't be moved aside."; }
  if ! mv "$apps/Tally.app.new" "$apps/Tally.app" >>"$LOG" 2>&1; then
    [ ! -d "$apps/Tally.app.old" ] || mv "$apps/Tally.app.old" "$apps/Tally.app"
    open "$apps/Tally.app" 2>/dev/null || true
    fail "The new app couldn't be put in place. The version you had is still there."
  fi
  rm -rf "$apps/Tally.app.old"
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$apps/Tally.app" >>"$LOG" 2>&1 || true

  run "Opening Tally" open "$apps/Tally.app"
  say "Done. Tally $VERSION is in your menu bar (the T. icon), and in $apps."
  echo "--- installed" >>"$LOG"
}

main "$@"
