#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROPS_FILE="$REPO_ROOT/src/Directory.Build.props"
PKG_DIR="$REPO_ROOT/packages"

VERSION="$(sed -n 's/.*<RustWebTransportVersion>\([^<]*\)<\/RustWebTransportVersion>.*/\1/p' "$PROPS_FILE")"
[ -n "$VERSION" ] || { echo "RustWebTransportVersion not found in $PROPS_FILE" >&2; exit 1; }

NUPKG="Decentraland.RustWebTransport.${VERSION}.nupkg"
URL="https://github.com/decentraland/rust-web-transport/releases/download/v${VERSION}/${NUPKG}"

mkdir -p "$PKG_DIR"
DEST="$PKG_DIR/$NUPKG"

if [ -f "$DEST" ]; then
  echo "$NUPKG already present at $DEST"
  exit 0
fi

TMP="$DEST.tmp.$$"
echo "Fetching $NUPKG from $URL"
curl -sSL --fail -o "$TMP" "$URL"

if mv -n "$TMP" "$DEST" 2>/dev/null && [ ! -f "$TMP" ]; then
  echo "Saved to $DEST"
else
  rm -f "$TMP"
  if [ -f "$DEST" ]; then
    echo "$NUPKG already present at $DEST (concurrent fetch)"
  else
    echo "Failed to publish $NUPKG to $DEST" >&2
    exit 1
  fi
fi
