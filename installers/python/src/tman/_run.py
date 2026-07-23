"""Locate (downloading if needed) and exec the native tman binary."""

import os
import platform
import stat
import sys
import tarfile
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
    exe = cache_dir() / BIN_NAME
    if exe.exists():
        return exe

    key = (platform.system(), platform.machine())
    asset = ASSETS.get(key)
    if asset is None:
        sys.exit(f"tman: unsupported platform {key[0]}/{key[1]}")

    ver = pkg_version("tman")
    url = f"https://github.com/standardbeagle/tman/releases/download/v{ver}/{asset}"
    print(f"tman: downloading {url}", file=sys.stderr)

    archive = cache_dir() / asset
    cache_dir().mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url) as res:
        archive.write_bytes(res.read())

    if asset.endswith(".zip"):
        with zipfile.ZipFile(archive) as z:
            z.extractall(cache_dir())
    else:
        with tarfile.open(archive) as t:
            t.extractall(cache_dir())
    archive.unlink()

    if not exe.exists():
        sys.exit("tman: archive did not contain the binary")
    exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return exe


def main() -> None:
    exe = ensure_binary()
    if platform.system() == "Windows":
        import subprocess

        sys.exit(subprocess.call([str(exe), *sys.argv[1:]]))
    os.execv(str(exe), [str(exe), *sys.argv[1:]])


if __name__ == "__main__":
    main()
