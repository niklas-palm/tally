#!/bin/bash
# Creates the self-signed "Tally Local" code-signing certificate in the login keychain, once per Mac. A stable
# signature is what lets macOS keep Tally's microphone and system-audio permissions from one build to the next.
set -euo pipefail
if security find-identity -p codesigning | grep "Tally Local" >/dev/null; then  # not -q: an early exit breaks the pipe
  echo "The Tally Local certificate is there already."
  exit 0
fi
dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT
# macOS's own LibreSSL: an OpenSSL earlier in PATH may write a .p12 that `security` can't read.
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -keyout "$dir/key.pem" -out "$dir/cert.pem" -days 3650 -subj "/CN=Tally Local" \
  -addext "basicConstraints=critical,CA:false" -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=critical,codeSigning"
/usr/bin/openssl pkcs12 -export -inkey "$dir/key.pem" -in "$dir/cert.pem" -out "$dir/cert.p12" -passout pass:tally
security import "$dir/cert.p12" -k ~/Library/Keychains/login.keychain-db -P tally -T /usr/bin/codesign >/dev/null
echo "Created the Tally Local certificate."
