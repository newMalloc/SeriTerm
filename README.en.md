# SeriTerm

[![CI](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/newMalloc/SeriTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/newMalloc/SeriTerm?label=release)](https://github.com/newMalloc/SeriTerm/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[简体中文](README.md) | **English**

A serial port debugging assistant for Windows (C# / WPF / .NET 8). It covers the three daily jobs in embedded,
microcontroller and hardware debugging — watching incoming data, sending commands, and capturing logs — reproducing the
core serial capabilities of lingguang's "Serial Debug Assistant" (Chinese), with sturdier implementations for long
captures, large amounts of displayed data, and failure reporting.

> Note: the application UI is currently **Chinese only**, so the screenshots below show Chinese labels. The source
> layout, architecture and commands documented here are language-neutral.

- **Download and run**: grab the single-file portable build from [Releases](https://github.com/newMalloc/SeriTerm/releases)
  (win-x64, no .NET runtime installation required);
- **Logic separated from UI**: the receive pipeline, framing, codecs, log writing and reconnect policy all live in
  `SeriTerm.Core` with zero WPF dependencies, so they are fully unit-testable;
- **No stutter on long captures**: virtualized log view plus batched refresh, with a 200,000-line cap that evicts the
  oldest data in blocks.

Docs: [development spec and milestones](docs/development-plan.md) · [verification log](docs/verification.md) (both in Chinese)

## Screenshot

![SeriTerm main window](docs/images/main.png)

(Captured over a COM5 loopback: every line in the log is real traffic, so Tx and Rx come in pairs.)

## Features

### Connection and parameters

- Port enumeration with **friendly device names** (e.g. `USB-SERIAL CH340`), so you do not have to guess which COM
  port is the one you want;
- Baud rate / data bits / parity / stop bits / flow control, with **named configuration presets** you can save and
  re-apply in one click;
- Open / close the port, with live Rx / Tx byte counters;
- Optionally open the last used port automatically at startup;
- **Automatic reconnect**: unplugging no longer produces a bogus "port is in use by another program" message, and
  plugging the device back in recovers automatically with exponential backoff.

### Receiving and display

- Virtualized log view that stays responsive during long captures; the displayed-line cap is 200,000 and the oldest
  data is evicted in blocks;
- **Text / HEX modes**, with every already-displayed line re-rendered when you switch; multiple encodings
  (GB2312, UTF-8 and more) with stateful decoding across chunk boundaries;
- **Framing strategies**: idle gap / delimiter (CRLF etc.) / no framing, each with a buffer size cap, so you neither
  glue frames together nor display half a frame;
- Timestamp column toggle, word wrap, adjustable font size;
- **Smart auto-scroll**: scrolling up pauses it automatically, a "▸ N new lines" bar appears at the bottom, and
  returning to the bottom resumes it;
- Pause display (incoming data still goes into the buffer), clear, and save.

### Search, selection and copy

- `Ctrl+F` **live search**: a "match N of M" counter, next / previous navigation, and **character-level highlighting**
  of the matched text (the current match is drawn darker than ordinary matches);
- Keywords can be **bookmarked**; they appear in an overlay at the top-right of the log, and one click puts the
  keyword back into the search box and jumps to its first match. Bookmarks can be removed one by one;
- Log lines can be **selected and copied**: drag inside one line to select characters, drag across lines to select
  whole lines, then use `Ctrl+C` or the context menu.

### Sending

- Text / HEX sending, with selectable line ending (CR / LF / CRLF);
- **Timed sending**: resend automatically at a configured interval;
- **Chunked file sending**: cancellable, with progress.

### Terminal mode

- Send-as-you-type, pass-through for Enter / Backspace / `Ctrl+C`, local echo, multi-line paste, ANSI escape filtering.

### Writing logs to disk

- Text log plus a **raw byte log** (which can be replayed back into the log view), rolled over by size;
- Background asynchronous write queue, so writing never blocks receiving;
- Configurable log directory; a separate diagnostic log is written only when something fails, which is what makes
  problems in the released build debuggable.

### UI

- Dark / light themes, switchable at runtime;
- Custom-drawn title bar (follows the theme) and window background blur (can be turned off);
- An **About** button at the right end of the title bar (or press <kbd>F1</kbd>): version, commit, runtime, license and
  links to the GitHub repository / releases; the dialog is a solid-colour window, unaffected by the main window's
  translucent "blurred background" surfaces.

## Requirements

- Windows 10 1809 or later (WPF, target framework `net8.0-windows`);
- Building from source requires .NET SDK 8.0 or later;
- The published single-file portable build requires no .NET runtime on the target machine.

## Build and run

```powershell
dotnet build SeriTerm.sln
dotnet run --project src/SeriTerm.App
```

## Tests

```powershell
dotnet test SeriTerm.sln
```

- Almost all tests are **pure logic unit tests** (framing boundaries, codecs, search, send assembly, log writing and
  replay, reconnect backoff, failure classification, presets, ...) and need no hardware;
- There is also a group of **loopback integration tests** that require a USB-TTL adapter with **TX and RX shorted**.
  They use `COM5` by default; changing the port does not require touching the source:

  ```powershell
  $env:SERITERM_LOOPBACK_PORT = 'COM3'
  dotnet test SeriTerm.sln
  ```

- When the target port does not exist (CI runners, or a machine without a USB-TTL adapter), the group is marked
  **skipped** rather than failed by `LoopbackFactAttribute`, so `dotnet test` is still green; with loopback hardware
  it is **286 passed / 0 failed** (280 unit + 6 loopback);
- Serial ports are exclusive resources, so parallel execution is disabled for the test assembly.

### Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs `dotnet restore` → `dotnet build -c Release` →
`dotnet test -c Release` on `windows-latest`, and uploads the trx report as a build artifact.

- The app is WPF (`net8.0-windows`), so the runner must be Windows; `ubuntu-latest` does not compile it;
- Runners have no serial ports at all, so the loopback group is skipped and public CI is always green; on a machine
  with a shorted loopback adapter the same commands report **286 passed / 0 failed** (280 unit + 6 loopback).

## Repository layout

```
SeriTerm.sln
Directory.Build.props            # Solution-wide build conventions (including the <Version>)
CHANGELOG.md                     # Release notes, one section per version; used verbatim as the release body
.github/workflows/ci.yml         # Continuous integration: build + test
.github/workflows/release.yml    # Tag-triggered portable release
docs/development-plan.md         # Development spec: scope, modules, milestones, design decisions, implementation log
docs/verification.md             # Verification log: measured numbers from automated and loopback verification
tools/                           # Development and verification scripts (publish, icon, screenshots, probes, UI smoke)
src/
  SeriTerm.Core/                 # Pure logic layer, zero WPF dependencies, unit-testable
    Serial/                      # Settings model, transport, port enumeration, error translation, reconnect policy
    Framing/                     # Framing strategies (idle gap / delimiter)
    Text/                        # HEX codecs, stateful text decoding, byte-mode escaping, ANSI filtering
    Terminal/                    # Terminal mode key encoding
    Send/                        # Send assembly, timed sender, chunked file sending
    Logging/                     # Log writing (text + raw bytes), background write queue, raw log reading
    Presets/                     # Configuration preset model (add / edit / delete and default naming)
    Pipeline/                    # Receive processor, display lines, receive options
    Documents/                   # Log document (storage / eviction / search) and batched-notification collection
  SeriTerm.App/                  # WPF presentation layer
    Assets/                      # Application icon (design source PNG + the ICO generated from it)
    MainWindow.xaml              # Main window: custom title bar + settings left / log & send right
    AboutWindow.xaml             # About window (version, commit, runtime, GitHub links)
    Services/                    # Theming, settings store, user prompts, device friendly names, file dialogs, IME, version info
    ViewModels/                  # MainViewModel and friends
    Controls/                    # LogView: virtualized log + auto-scroll + search overlay
    Themes/                      # Shared.xaml + Dark.xaml + Light.xaml
  SeriTerm.Tests/                # xUnit: pure logic unit tests + loopback integration tests
```

## Design invariants

These are the preconditions for a tool like this not losing data and not freezing. Please do not violate them:

1. **Do not use the `SerialPort.DataReceived` event**: it fires on the thread pool and drops events, which loses data
   at high baud rates. The transport layer uses a dedicated background thread doing blocking reads.
2. **Read with a finite timeout (100 ms) plus a cancellation flag**: `SerialPort` cancellation tokens cannot reliably
   interrupt an already blocked read, and `Close()` can hang forever.
3. **The reader thread never touches the UI**: it only counts and enqueues, while the UI refreshes in batches at
   roughly 30 fps.
4. **One collection notification per batch of log lines**: notifying per line makes WPF run layout per line, and
   evicting old lines must rebuild a whole block rather than looping `RemoveAt(0)` (which is O(n²) at 200,000 lines).
5. **All theming goes through `DynamicResource`**: switching dark / light must not require a restart.
6. **On a serial failure, first ask whether the port still exists, then decide what to report**: a removed device and a
   port taken over by another program both surface as Win32 `ERROR_ACCESS_DENIED`, so classifying by exception type
   alone is guaranteed to misreport. The entry point is `SerialErrorTranslator.Classify(ex, portPresent)`, and
   `portPresent` must be queried by the caller at the moment of failure.
7. **A "default on" toggle cannot drive other components through property-change notifications alone**:
   `[ObservableProperty]` does not raise a notification when the value is unchanged, so "default true + config also
   true" silently disables the feature (automatic reconnect actually hit this; see spec 11.17).
8. **The custom title bar and the background blur are coupled**: `WindowStyle=None` plus `WindowChrome`
   (`CaptionHeight` must equal the real title bar height), with the three window buttons in a
   `LastChildFill="False"` `DockPanel`; `App.OnExit` must `DisposeAsync` the container, and
   `OnDispatcherUnhandledException` must not resolve services (both were causes of issue 11.22).
9. **A control appears in exactly one place**: appearance switches (blur, theme) live only in the title bar,
   connection parameters only in the left column, and the persistent log settings (display options, save / clear)
   only in the "Log display" section of the left column. The **transient search UI (search box + favorites) instead
   floats over the top-right corner of the log** rather than taking up layout, because permanently spending a log row
   on it is a bad trade; its entry point (the "Find" button) stays in the left column (see spec 11.29). Duplicating a
   switch in two places (auto-scroll used to exist both in the toolbar and the sidebar) looks convenient but leaves
   users unsure which one wins.
10. **The two-column information architecture is fixed**: the left column holds connection, receive and log settings;
    the right column holds the log plus the send area. `Send` is the most frequently used button, so it must stay
    pinned in the send area below the log and never move into a scrolling region (see spec 11.23–11.25).
11. **Selection granularity in the log is "characters within a line, whole lines across lines"**: each line's content
    column is a read-only text box (`TextBlock` cannot select characters), and a drag selects characters when it stays
    inside one line but switches to whole-line selection when it lands on another line; `Ctrl+C` / the context menu
    copy prefer the selected characters. Making cross-line character selection work would mean replacing the log host
    with a single rich-text control and losing both the 200,000-line virtualization and the 30 fps refresh
    (see spec 11.26).

## Packaging and releases

```powershell
# Single-file portable build (recommended for distribution; no .NET runtime needed on the target machine)
pwsh -File tools/publish.ps1

# Framework-dependent build (the target machine needs the .NET 8/10 desktop runtime)
pwsh -File tools/publish.ps1 -FrameworkDependent
```

The output lands in `artifacts/publish/` as a single file of about 64 MB (self-contained plus compression).
Its file name carries the version (`SeriTerm-1.0.2-win-x64.exe`, read from the exe's own version resource).

Official releases are produced automatically by [`.github/workflows/release.yml`](.github/workflows/release.yml):
pushing a `v*` tag runs the publish script above, computes the SHA256, and attaches the exe to the matching
[Release](https://github.com/newMalloc/SeriTerm/releases). **Write the section for that version in
[`CHANGELOG.md`](CHANGELOG.md) first** — the release body is taken from it verbatim (the workflow only appends the
file size, SHA256, requirements and doc links); if the section is missing the workflow fails instead of publishing.
The tag message follows the same section, and the workflow refuses to publish when the tag version and the built
version disagree (bump `<Version>` in `Directory.Build.props` first).

> ⚠️ Do not enable `PublishTrimmed`: WPF does not support trimming.
> Compression (`EnableCompressionInSingleFile`) makes the first launch slightly slower in exchange for half the size.

## Configuration file

- Location: `%AppData%\SeriTerm\settings.json` (UTF-8 without BOM)
- Contents: theme, last serial parameters, display / send / terminal settings, auto-reconnect toggle, background blur
  toggle, log directory, configuration presets
- There is also a `%AppData%\SeriTerm\ui-errors.log`, written only when something fails: the released build has no
  console, so without a log file an unhandled exception leaves nothing to go on.

## Development scripts

The scripts under `tools/` are used during development and verification. They must be run with **Windows PowerShell
5.1** (`UIAutomationClient` is only available in .NET Framework) and saved as **UTF-8 with BOM** (otherwise Chinese
control names are read as ANSI and turn into mojibake).

| Script | Purpose |
|---|---|
| `publish.ps1` | Produce the single-file portable build |
| `make-icon.ps1` | Build the multi-size ICO from `Assets/seriterm.png`: auto-crops to the device body, scales in premultiplied alpha, and emits a light/dark preview sheet (reproducible) |
| `capture-window.ps1` | Launch the app and take a screenshot (with DPI awareness handling) |
| `ui-review-capture.ps1` | Batch screenshots: light / dark theme × default / taller window, can feed loopback data (for UI review) |
| `probe-layout.ps1` | Print the real rectangles of key controls to judge numerically what got pushed out of view |
| `probe-log-drag.ps1` | Drag across the log with real mouse events, then read back UIA selected-row count, selected characters and clipboard contents |
| `probe-log-search.ps1` | Feed data → `Ctrl+F` search → read back match count and selected rows (verifies character-level highlighting and selection colors) |
| `probe-search-overlay.ps1` | Verify the search box / favorites overlay sits over the top-right of the log: the log list's top edge must not move when search opens |
| `ui-layout-check.ps1` | UI structure and interaction assertions (control presence, duplicate controls at zero, which column owns what, font size, presets, send-area bounds, narrow window) |
| `ui-smoke-test.ps1` | Smoke: loopback send / receive, auto-scroll, `Ctrl+F` search |
| `ui-m4-smoke.ps1` | Smoke: delimiter framing, HEX sending, timed sending |
| `ui-m5-smoke.ps1` | Smoke: terminal mode (real virtual key injection) |
| `ui-m7-smoke.ps1` | Smoke: log writing to disk, auto-open port at startup |
| `ui-m8-smoke.ps1` | Smoke: applying configuration presets |
| `ui-reconnect-smoke.ps1` | Smoke: unplugging and replugging the real device (failure diagnosis + no modal dialog + automatic recovery) |

## Verification

- **297 automated tests** (291 pure logic unit tests + 6 loopback integration tests), all green under `dotnet test`
  (the loopback group is skipped automatically on machines without a serial port);
- **End-to-end loopback verification** (UI automation driving real mouse / keyboard events against the published
  build itself): framing, timed sending, log writing and replay, terminal mode, smart auto-scroll, theme switching,
  title bar and window blur, clean shutdown, and automatic reconnect after unplugging the real device;
- **UI information-architecture assertions**: control ownership, duplicate controls at zero, font size taking effect,
  no clipping in a narrow window, and more — all passing.

Line-by-line numbers (control coordinates, byte counts, before-and-after comparisons) are in
[docs/verification.md](docs/verification.md) (Chinese).

## Known limitations

- **"Port in use" and "device unplugged" cannot be distinguished at the Win32 level**: both are
  `ERROR_ACCESS_DENIED`. Only when the port still shows up in the system port enumeration yet cannot be opened does
  the app report "in use by another program"; everything else is treated as an unplugged device. This is an inherent
  ambiguity of the Windows exclusive-open serial model.
- **The background blur uses a blurred copy of the desktop wallpaper**, not live desktop content: when the window
  covers only part of the desktop, desktop icons and other windows do not appear in the background; on multi-monitor
  setups or with centered / tiled wallpaper the alignment is approximate. On Windows 10 the legacy DWM blur interface
  does nothing for third-party windows, so the blur is done in-process.
- The blur radius and mask opacity are fixed values with no UI to tune them; turn the effect off from the title bar
  if you do not want it.
- **There is no Settings dialog and no Help menu**: an entry point that does nothing when clicked is worse than no
  entry point at all.
- The left settings column needs scrolling in short windows to reveal every group; the primary action
  "Open port" is always on the first screen.
- **The search overlay covers a small part of the log's top-right corner** — the price of not consuming a log row.
  With no search and no bookmarks it collapses entirely and the log uses the full width.
- **Selection granularity is "characters within a line, whole lines across lines"**: partial cross-line selection
  (the second half of line 1 plus the first half of line 2) is not possible; supporting it would require replacing the
  log host with a single rich-text control at the cost of the 200,000-line virtualization.
- The single-file portable build extracts itself to a temporary directory on first launch, so that launch is slightly
  slower than a regular build.

## License

[MIT](LICENSE)
