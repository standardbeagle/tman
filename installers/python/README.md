# tman (python wrapper)

Python/uv distribution of the [tman](https://github.com/standardbeagle/tman) native binary.

```sh
uv tool install tman
tman run --max-time 10m -- npm test
```

The native binary is downloaded from GitHub Releases on first run and cached under `~/.cache/tman`.
