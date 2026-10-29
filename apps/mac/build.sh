#!/bin/bash
# Builds Tally.app from this folder with the Command Line Tools alone (no Xcode), signed with the "Tally Local"
# certificate (signing-cert.sh). `make app` and the installer (site/install.sh) both build with this.
#
#   apps/mac/build.sh <tally.json>     the app's settings (apps/mac/settings.sh): sign-in, API, site, bundle id
set -euo pipefail
settings=$(realpath "$1")  # before the cd: the path may be relative to the caller
cd "$(dirname "$0")"

# SwiftPM's default build system fails with the Command Line Tools; the native one works.
swift build -c release --build-system native
app=.build/Tally.app
rm -rf "$app" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp .build/release/Tally "$app/Contents/MacOS/"
sed "s/@BUNDLE@/$(plutil -extract bundleId raw "$settings")/g" Info.plist > "$app/Contents/Info.plist"
cp Tally.icns "$app/Contents/Resources/"  # the "T." (make icon draws it from icon.svg)
cp "$settings" "$app/Contents/Resources/tally.json"
codesign --force --sign "Tally Local" "$app"
