"""Locate (downloading if needed) and exec the native tman binary."""

import io
import os
import platform
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from importlib.metadata import version as pkg_version
from pathlib import Path

ASSETS = {
    ("Linux", "x86_64"): "tman-linux-x64.tar.gz",
    ("Linux", "aarch64"): "tman-linux-arm64.tar.gz",
    ("Darwin", "x86_64"): "tman-osx-x64.tar.gz",
    ("Darwin", "arm64"): "tman-osx-arm64.tar.gz",
    ("Windows", "AMD64"): "tman-win-x64.zip",
}

BIN_NAME = "tman.exe" if platform.system() == "Windows" else "tman"


def cache_dir() -> Path:
    root = os.environ.get("XDG_CACHE_HOME") or Path.home() / ".cache"
    return Path(root) / "tman"


def ensure_binary() -> Path:
    # One directory per package version: an upgraded package must not keep exec'ing the binary
    # an older one downloaded, which an unversioned path did forever.
    ver = pkg_version("tman")
    exe = cache_dir() / ver / BIN_NAME
    if exe.exists():
        return exe

    key = (platform.system(), platform.machine())
    asset = ASSETS.get(key)
    if asset is None:
        sys.exit(f"tman: unsupported platform {key[0]}/{key[1]}")

    url = f"https://github.com/standardbeagle/tman/releases/download/v{ver}/{asset}"
    print(f"tman: downloading {url}", file=sys.stderr)
    with urllib.request.urlopen(url) as res:
        binary = _binary_from(asset, res.read())

    # Published by rename, so a launcher that finds `exe` always finds all of it — two first
    # launches racing each write a private temp file and the second rename replaces the first
    # with identical bytes.
    exe.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=exe.parent, prefix=f".{BIN_NAME}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(binary)
        os.chmod(tmp, 0o755)
        try:
            os.replace(tmp, exe)
        except PermissionError:
            # Windows refuses to replace a binary that is running: the racer that published it
            # first is already exec'ing this same version, so its copy is the one to use
            if not exe.exists():
                raise
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)
    return exe


def _binary_from(asset: str, archive: bytes) -> bytes:
    """The binary's bytes, read by name: nothing else in the archive is written anywhere."""
    try:
        if asset.endswith(".zip"):
            with zipfile.ZipFile(io.BytesIO(archive)) as z:
                return z.read(BIN_NAME)
        with tarfile.open(fileobj=io.BytesIO(archive)) as t:
            member = t.extractfile(BIN_NAME)
            if member is None:
                raise KeyError(BIN_NAME)
            return member.read()
    except KeyError:
        sys.exit(f"tman: {asset} did not contain {BIN_NAME}")


def main() -> None:
    exe = ensure_binary()
    if platform.system() == "Windows":
        import subprocess

        sys.exit(subprocess.call([str(exe), *sys.argv[1:]]))
    os.execv(str(exe), [str(exe), *sys.argv[1:]])


if __name__ == "__main__":
    main()
