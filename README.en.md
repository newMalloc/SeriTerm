# SeriTerm

[![CI](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/newMalloc/SeriTerm?label=release)](https://github.com/newMalloc/SeriTerm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[简体中文](README.md) | **English**

A serial port debugging assistant for Windows (C# / WPF / .NET 8) for receiving data, sending commands and capturing
logs: no stutter over long sessions, a 200,000-line log cap, automatic reconnect, single-file build with no installer.
It also ships a built-in MCP server, so an AI can read your device output directly.

> The UI is currently Chinese only, so the screenshot shows Chinese labels.

![Main window](docs/images/main.png)

> Real full-screen capture: one MCP client connected; `Ctrl+F` search for `error` with 4 hits and character-level
> highlighting; log traffic sent and received over a COM5 loopback (paired `Tx`/`Rx`); `[AI] 发送 8 字节：STATUS` is a
> command issued by the AI through MCP. Permission tier: full.

https://github.com/user-attachments/assets/60625d5f-2d7c-470b-8355-47cf9d8c60c3

[web version](https://newmalloc.github.io/SeriTerm/video/)

## Download

Download from [Releases](https://github.com/newMalloc/SeriTerm/releases). Both files behave the same and are win-x64
single-file builds, no installer required:

| File | Size | Runtime | Use case |
|---|---|---|---|
| `SeriTerm-<version>-win-x64.exe` | ~3.8 MB | Requires the .NET 8 desktop runtime (auto-installed from Microsoft if missing, ~56 MB) | General distribution (recommended) |
| `SeriTerm-<version>-win-x64-full.exe` | ~67 MB | Bundled in the exe, no network access | Offline / air-gapped machines |

## Features

**Connection**

- Port list shows friendly device names (`COM5 (USB-SERIAL CH340)`)
- Baud rate / data bits / parity / stop bits / flow control; named presets can be saved and reused
- Reopens the last used port at startup; reconnects automatically with exponential backoff

**Receiving**

- Text / HEX modes; UTF-8, GB2312 and other encodings, decoded across chunk boundaries without corruption
- Framing by idle gap / delimiter / none, with a configurable buffer cap; output is per frame, never glued or partial
- Virtualized log view capped at 200,000 lines, evicting in blocks; pause, clear, save
- Optional timestamps, word wrap and font size; auto-scroll pauses while you scroll up and shows the new line count

**Sending**

- Text / HEX with CR / LF / CRLF line endings
- Timed sending; chunked file sending (cancellable, with progress)
- Terminal mode: send as you type, Enter / Backspace / `Ctrl+C` pass-through, local echo

**Search and copy**

- `Ctrl+F` live search: match counter, next / previous, character-level highlighting
- Bookmarked keywords in an overlay at the log's top-right corner; click to refill the search box and jump to the first match
- Character-level selection within and across lines; `Ctrl+C` copies the selection

**Log to disk**

- Text log and a raw byte log (replayable into the log view), rolled over by size
- Background asynchronous write queue; writing never blocks receiving

**AI access (MCP)**

- Built-in [MCP](https://modelcontextprotocol.io) server; Claude Desktop / Cursor / VS Code can read the device output
  directly — left panel "AI 接入" → "复制配置" to connect
- `serial_wait_for_pattern` blocks until the requested text appears (first line after a reboot, `OK`, `panic`) instead of polling
- Reads return frames already split by your framing settings, with arrival time, direction and a cursor; incremental
  reads neither duplicate nor miss
- Read-only by default: write tools are absent from the tool list. Tick "允许 AI 发送数据（完全权限）" to enable sending;
  the status bar shows the current tier
- Sends are capped at 4096 bytes per call and 3 per second, and leave an `[AI]` audit line in the log
- Local named pipe only, current user only, no network port; see [docs/mcp.md](docs/mcp.md) (Chinese)

**UI**

- Dark / light themes, switchable at runtime; custom-drawn title bar; window background blur (can be disabled)
- About in the title bar (or <kbd>F1</kbd>): version, commit, runtime and repository links

## Requirements

Windows 10 1809 or later (64-bit). The full build needs no runtime; the launcher requires the .NET 8 desktop runtime
(installed automatically if missing). Building from source requires .NET SDK 8.0 or later.

## Build and test

```powershell
dotnet build SeriTerm.sln
dotnet run   --project src/SeriTerm.App
dotnet test  SeriTerm.sln
```

**354 automated tests** (346 pure logic unit tests + 8 loopback integration tests); `dotnet test` runs them all.
The loopback group needs a USB-TTL adapter with TX and RX shorted and uses `COM5` by default; the port can be
overridden without touching the source (`$env:SERITERM_LOOPBACK_PORT = 'COM3'`). On a machine without loopback
hardware the group is skipped, not failed.

The MCP path spans three processes (AI client → `--mcp-stdio` bridge → UI process). Unit tests cover the protocol and
its guard rails; before a release, run `pwsh -File tools/mcp-smoke.ps1 -Exe <exe>` for an end-to-end pass.

## Packaging

```powershell
pwsh -File tools/publish.ps1                     # launcher, ~3.8 MB (default, for distribution)
pwsh -File tools/publish.ps1 -All                # launcher + self-contained full build (~67 MB)
pwsh -File tools/publish.ps1 -SelfContained      # self-contained full build only
pwsh -File tools/publish.ps1 -FrameworkDependent # framework-dependent single file, ~1.7 MB (local dev)
```

The launcher is a native exe built with
[NativeAOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/): it carries no .NET runtime, so it starts on
a machine with nothing installed. The application itself (a framework-dependent single file) is Brotli compressed,
embedded inside it and unpacked to `%LocalAppData%\SeriTerm\app\<version>\` on start; older versions are removed
automatically.

Pushing a `v*` tag makes [release.yml](.github/workflows/release.yml) build with `-All` and attach both exes to the
matching Release. The release body is taken from the corresponding section of [CHANGELOG.md](CHANGELOG.md); if that
section is missing, the workflow fails instead of publishing.

## Configuration

- Settings: `%AppData%\SeriTerm\settings.json`
- Diagnostic log: `%AppData%\SeriTerm\ui-errors.log` (written only on failure)

## Known limitations

- **"Port in use" and "device unplugged" are indistinguishable at the Win32 level**: both surface as
  `ERROR_ACCESS_DENIED`. The app reports "in use by another program" only when the port is still enumerated but cannot
  be opened; otherwise it treats the device as unplugged.
- **The background blur uses a blurred copy of the desktop wallpaper**, not live desktop content: when the window covers
  only part of the desktop, desktop icons and other windows do not appear in it.
- **The search overlay covers a small part of the log's top-right corner**: the trade-off for not spending a log row on
  it. With no search open it collapses and the log uses the full width.
- In a short window the left settings column needs scrolling; "Open port" stays on the first screen.
- **MCP requires the UI process**: SeriTerm must keep running for reads, otherwise tools return `[not_connected]`.
  "复制配置" contains the path of the currently running exe, which changes on upgrade — copy it again.
- The single-file build extracts itself to a temporary directory on first launch, so that launch is slightly slower.

## License

[MIT](LICENSE)

Design decisions, measured numbers and development scripts: [development spec](docs/development-plan.md) and
[verification log](docs/verification.md). AI access (MCP) usage and tool list: [docs/mcp.md](docs/mcp.md). All three
documents are in Chinese.
