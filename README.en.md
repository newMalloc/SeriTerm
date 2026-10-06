# SeriTerm

[![CI](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/newMalloc/SeriTerm?label=release)](https://github.com/newMalloc/SeriTerm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[简体中文](README.md) | **English**

A serial port debugging assistant for Windows (C# / WPF / .NET 8). It does the three daily jobs of hardware
debugging — watching incoming data, sending commands, capturing logs — and is built to survive the boring parts:
long captures without stutter, a 200,000-line display cap, automatic reconnect, and a single-file portable build.

> The UI is currently Chinese only, so the screenshot shows Chinese labels.

![Main window](docs/images/main.png)

## Download

Pick either file from [Releases](https://github.com/newMalloc/SeriTerm/releases) — they behave identically:

| File | Size | Notes |
|---|---|---|
| `SeriTerm-<version>-win-x64.exe` | ~3.8 MB | **Recommended.** Contains no .NET runtime. Machines that already have the .NET 8/9/10 desktop runtime download only these few MB; if it is missing, the launcher asks once and then installs Microsoft's official runtime (~56 MB, once per machine, shared by every .NET app) before starting. |
| `SeriTerm-<version>-win-x64-full.exe` | ~67 MB | The runtime is bundled inside. No network, no installs — for offline or locked-down machines. |

Both are win-x64 single files: double-click to run, no installer.

## Features

**Connection**

- Port enumeration with friendly device names (`COM5 (USB-SERIAL CH340)`), so you do not have to guess;
- Baud rate / data bits / parity / stop bits / flow control, with named configuration presets;
- Opens the last used port at startup; **automatic reconnect** with exponential backoff, so unplugging no longer
  reports a bogus "port is in use by another program".

**Receiving**

- Text / HEX modes, multiple encodings (UTF-8, GB2312, ...) decoded statefully across chunk boundaries;
- Framing by idle gap / delimiter / none, with a buffer cap, so frames are neither glued together nor half-displayed;
- Virtualized log view capped at 200,000 lines, evicting the oldest in blocks; pause, clear, save;
- Toggleable timestamps, word wrap, font size; **smart auto-scroll** pauses while you scroll up and offers a
  "▸ N new lines" bar at the bottom.

**Sending**

- Text / HEX with CR / LF / CRLF line endings;
- Timed sending; chunked file sending (cancellable, with progress);
- Terminal mode: send as you type, Enter / Backspace / `Ctrl+C` pass-through, local echo.

**Search and copy**

- `Ctrl+F` live search with a match counter, next / previous navigation and **character-level highlighting**;
- Bookmark keywords in an overlay over the log's top-right corner; one click refills the search box and jumps to the
  first match;
- Select log text **character by character**, within and across lines — `Ctrl+C` copies exactly what is highlighted.

**Log to disk**

- Text log plus a **raw byte log** that can be replayed back into the log view, rolled over by size;
- Background asynchronous write queue, so writing never blocks receiving.

**AI access (MCP)**

- A built-in [MCP](https://modelcontextprotocol.io) server: models in Claude Desktop / Cursor / VS Code
  **read your device's output directly** instead of you copy-pasting the log. Left panel → "AI 接入" → "复制配置";
- **It can wait**: `serial_wait_for_pattern` blocks until the device prints the text you asked for
  (the first line after a reboot, `OK`, `panic`);
- **Frames, not raw bytes**: data arrives already split by your framing settings, with arrival time, direction and a
  cursor that makes incremental reads neither duplicate nor miss;
- **Read-only by default**: write tools are not even listed. Sending requires ticking
  "允许 AI 发送数据（完全权限）" in the UI; the status bar always shows the current tier, sends are size- and
  rate-limited, every send leaves an `[AI]` audit line in the log, and the permission can be revoked in one click;
- Local named pipe only, current user only, no network port. See [docs/mcp.md](docs/mcp.md) (Chinese).

**UI**

- Dark / light themes, switchable at runtime; custom-drawn title bar; window background blur (can be turned off);
- About button in the title bar (or <kbd>F1</kbd>): version, commit, runtime and repository links.

## Requirements

Windows 10 1809 or later (64-bit). The full build needs no runtime at all; the launcher needs the .NET 8 desktop
runtime (installed automatically if missing). Building from source needs .NET SDK 8.0 or later.

## Build and test

```powershell
dotnet build SeriTerm.sln
dotnet run   --project src/SeriTerm.App
dotnet test  SeriTerm.sln
```

**354 automated tests** (346 pure logic unit tests + 8 loopback integration tests), green out of the box.
The loopback group needs a USB-TTL adapter with **TX and RX shorted**; it uses `COM5` by default and the port can be
overridden without touching the source (`$env:SERITERM_LOOPBACK_PORT = 'COM3'`). On a machine without loopback
hardware the group is skipped rather than failed.

The MCP path spans three processes (AI client → `--mcp-stdio` bridge → UI process). Unit tests cover the protocol and
the guard rails; before a release, run `pwsh -File tools/mcp-smoke.ps1 -Exe <exe>` for an end-to-end pass.

## Packaging

```powershell
pwsh -File tools/publish.ps1                     # launcher, ~3.8 MB (default, for distribution)
pwsh -File tools/publish.ps1 -All                # launcher + self-contained full build (~67 MB)
pwsh -File tools/publish.ps1 -SelfContained      # self-contained full build only
pwsh -File tools/publish.ps1 -FrameworkDependent # framework-dependent single file, ~1.7 MB (local dev)
```

The launcher is a **native exe** compiled with
[NativeAOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/): it carries no .NET runtime itself, so it
starts on a machine with nothing installed. The actual application (a framework-dependent single file) is Brotli
compressed, embedded inside it, unpacked to `%LocalAppData%\SeriTerm\app\<version>\` on start, and older versions are
cleaned up automatically.

Pushing a `v*` tag makes [release.yml](.github/workflows/release.yml) build with `-All` and attach both exes to the
matching Release. The release body is taken from the corresponding version section in [CHANGELOG.md](CHANGELOG.md) —
write it first, or the workflow fails instead of publishing.

## Configuration

- Settings: `%AppData%\SeriTerm\settings.json`
- Diagnostic log: `%AppData%\SeriTerm\ui-errors.log` (written only on failure)

## Known limitations

- **"Port in use" and "device unplugged" cannot be told apart at the Win32 level** (both are `ERROR_ACCESS_DENIED`):
  the app reports "in use by another program" only when the port is still enumerated but cannot be opened.
- **The background blur uses a blurred copy of the desktop wallpaper**, not live desktop content: when the window
  covers only part of the desktop, desktop icons and other windows do not appear in it.
- **The search overlay covers a small part of the log's top-right corner** — the price of not spending a log row on it;
  with no search open it collapses and the log uses the full width.
- The left settings column needs scrolling in a short window; the primary "Open port" action is always on the first
  screen.
- The self-contained single-file build extracts itself to a temporary directory on first launch, so that launch is
  slightly slower; the launcher unpacks its 1.7 MB payload to `%LocalAppData%\SeriTerm\app\` once per version.

## License

[MIT](LICENSE)

Design decisions, measured numbers and development scripts are documented in
[the development spec](docs/development-plan.md) and [the verification log](docs/verification.md) (both in Chinese).
