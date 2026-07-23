#!/usr/bin/env sh
# tman installer: curl -fsSL https://raw.githubusercontent.com/standardbeagle/tman/main/install.sh | sh
set -eu

REPO="standardbeagle/tman"
DEST="${TMAN_INSTALL_DIR:-$HOME/.local/bin}"

OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS-$ARCH" in
  Linux-x86_64)  ASSET="tman-linux-x64.tar.gz" ;;
  Linux-aarch64) ASSET="tman-linux-arm64.tar.gz" ;;
  Darwin-x86_64) ASSET="tman-osx-x64.tar.gz" ;;
  Darwin-arm64)  ASSET="tman-osx-arm64.tar.gz" ;;
  *) echo "tman: unsupported platform $OS-$ARCH" >&2; exit 1 ;;
esac

VERSION="${TMAN_VERSION:-}"
if [ -z "$VERSION" ]; then
  VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | sed 's/.*"tag_name": *"//;s/".*//')"
fi
case "$VERSION" in v*) ;; *) VERSION="v$VERSION" ;; esac

URL="https://github.com/$REPO/releases/download/$VERSION/$ASSET"
echo "tman: downloading $URL"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$URL" -o "$TMP/$ASSET"
tar -xzf "$TMP/$ASSET" -C "$TMP"

mkdir -p "$DEST"
install -m 755 "$TMP/tman" "$DEST/tman"
echo "tman: installed $VERSION to $DEST/tman"

case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo "tman: note - $DEST is not on your PATH" >&2 ;;
esac
