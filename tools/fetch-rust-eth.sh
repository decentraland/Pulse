#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROPS_FILE="$REPO_ROOT/src/Directory.Build.props"
PKG_DIR="$REPO_ROOT/packages"

VERSION="$(sed -n 's/.*<RustEthereumVersion>\([^<]*\)<\/RustEthereumVersion>.*/\1/p' "$PROPS_FILE")"
[ -n "$VERSION" ] || { echo "RustEthereumVersion not found in $PROPS_FILE" >&2; exit 1; }

NUPKG="Decentraland.RustEthereum.${VERSION}.nupkg"
URL="https://github.com/decentraland/rust-ethereum/releases/download/v${VERSION}/${NUPKG}"

mkdir -p "$PKG_DIR"
DEST="$PKG_DIR/$NUPKG"

if [ -f "$DEST" ]; then
  echo "$NUPKG already present at $DEST"
  exit 0
fi

echo "Fetching $NUPKG from $URL"
curl -sSL --fail -o "$DEST" "$URL"
echo "Saved to $DEST"
