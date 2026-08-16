# Security & Privacy

EQL Buff Bars is a Windows overlay that shows buff and DoT timers for EverQuest Legends.
It works entirely by reading the game's own text files. This document states exactly what
the app does and does not do, in terms you can verify against the source. The codebase is
small (about 15 C# files); every claim below cites the file that backs it.

## What the app does

- **Reads the client spell database** (`spells_us.txt`, `spells_us_str.txt`) from the game
  directory at startup, read-only (`src/BuffBars.Core/SpellDb.cs`).
- **Tails the game's chat log files** (`Logs\eqlog_*.txt`), read-only, using
  `FileShare.ReadWrite | FileShare.Delete` so the game keeps exclusive control of its own
  files (`src/BuffBars.Core/LogTailer.cs`). This is the same technique used by established
  community tools such as EQLogParser and GINA. Logging is a built-in game feature that the
  player enables with `/log`.
- **Parses log lines into buff/DoT state** (`src/BuffBars.Core/LineParser.cs`,
  `Tracker.cs`) and renders timers in transparent, click-through WPF windows
  (`src/BuffBars.App/OverlayWindow.xaml.cs`).
- **Writes one file**: its own settings at `%AppData%\EqlBuffBars\config.json`
  (`src/BuffBars.Core/Config.cs`, `AppConfig.Save()`). That is the only write the shipped
  application performs, anywhere.

## What the app does not do

Each of these is verifiable by searching the source; the entire runtime code is in
`src/BuffBars.Core` and `src/BuffBars.App`.

- **No network I/O of any kind.** There is no `HttpClient`, `WebRequest`, `Socket`,
  `TcpClient`, `UdpClient`, `WebSocket`, or any other networking API in the source. The
  app has no update checker, no telemetry, no crash reporting, no uploads. Your log data
  never leaves your machine. (The only `http://` strings in the repository are the
  standard XML-namespace identifiers in the two `.xaml` files; WPF resolves these locally
  and never contacts them.)
- **No game memory access and no injection.** There is no `OpenProcess`,
  `ReadProcessMemory`, `WriteProcessMemory`, `CreateRemoteThread`, `LoadLibrary`, or
  process enumeration anywhere. The app never even locates the game's process or window;
  it does not know or care whether the game is running.
- **No input hooks and no input synthesis.** No `SetWindowsHookEx`, no
  `GetAsyncKeyState`, no `SendInput`/`keybd_event`/`mouse_event`. The app cannot see your
  keystrokes and cannot act in the game on your behalf.
- **No screen capture.** No `BitBlt`, `PrintWindow`, or `CopyFromScreen`. The overlay
  draws its own pixels; it reads nothing from the screen.
- **No persistence mechanisms.** No registry access, no Windows service, no scheduled
  task, no startup-folder entry. The app runs only when you start it and exits from the
  tray menu (`src/BuffBars.App/App.xaml.cs`).
- **No third-party runtime dependencies.** `BuffBars.Core` and `BuffBars.App` reference
  only the .NET 8 runtime and the WPF/WinForms frameworks that ship with it (see the two
  `.csproj` files). There are no NuGet packages in the shipped application, so there is no
  third-party supply chain to audit. (The test project uses xunit and the standard
  Microsoft test SDK; tests are not part of the shipped binary.)

## Complete P/Invoke inventory

The application makes exactly three native calls, all declared in
`src/BuffBars.App/Win32.cs`, all into `user32.dll`, and all operating **only on the app's
own window handles** (obtained from its own WPF windows via `WindowInteropHelper`):

| Function | Why it is needed |
|---|---|
| `GetWindowLongPtr` | Read the overlay window's current extended style before modifying it. |
| `SetWindowLongPtr` | Add `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE` to the overlay: clicks pass through to the game, the overlay never takes focus, and it stays out of Alt-Tab. This is the standard recipe for a non-interactive game overlay. |
| `SetWindowPos` | Re-assert `HWND_TOPMOST` every ~2 seconds (with `SWP_NOSIZE \| SWP_NOMOVE \| SWP_NOACTIVATE`) because the game constantly reorders window z-order. Position and size are never changed and focus is never taken. |

There are no other `DllImport` or `LibraryImport` declarations in the repository.

## Data flow

```
game directory (read-only)                      your machine only
  spells_us.txt, spells_us_str.txt  --> SpellDb --+
  Logs\eqlog_<Char>_<server>.txt    --> LogTailer -+-> LineParser -> Tracker
                                                        |
                                                        v
                                        OverlayWindow (pixels on screen)

%AppData%\EqlBuffBars\config.json  <-> AppConfig (window positions, options)
```

Inputs: game files, read-only. Outputs: pixels in an overlay window, plus one JSON
settings file. Nothing else is read, written, or transmitted.

## Antivirus false positives

Unsigned, low-distribution tools that create transparent topmost overlay windows are a
classic heuristic false-positive profile, and some scanners may flag `EqlBuffBars.exe`.
The specific reasons:

- **The binary is not code-signed.** Code-signing certificates cost money this hobby
  project does not spend; unsigned + rarely-downloaded is the biggest reputation penalty
  with SmartScreen and AV heuristics.
- **Layered/transparent/topmost window APIs** (`SetWindowLongPtr` with `WS_EX_LAYERED |
  WS_EX_TRANSPARENT`, `SetWindowPos` with `HWND_TOPMOST`) are used by both legitimate
  overlays and by screen-covering malware, so heuristics weight them.
- **Continuous tailing of another program's log files** with permissive file sharing can
  resemble data-harvesting behavior to a scanner, even though the files are plain-text
  game logs the player asked the game to write.
- **Self-contained single-file publish** bundles the .NET runtime into one large exe that
  self-extracts at startup, another pattern heuristics dislike.

If you do not want to take a stranger's word for it: **build from source**. The repository
builds with the free .NET 8 SDK (`dotnet publish`, or `tools/publish.ps1`, which prints
the SHA-256 of the exe it produces). For prebuilt downloads, compare the file's SHA-256
against the hash published alongside each GitHub release before running it. If a release
file's hash does not match, do not run it, and report it.

## Scope and honest limitations

- The app trusts its own config file. `config.json` lives in your `%AppData%` and is
  read with the standard .NET JSON deserializer; a corrupted file falls back to defaults
  (`Config.cs`). The `GameDir` setting controls which directory is read for spell data and
  logs; it only ever affects what is read, never what is written.
- Log files are untrusted input by design. The parser is plain string slicing with no
  code execution, no SQL, and no shell invocation; malformed lines are ignored. The worst
  a hostile log line can do is display a wrong label in the overlay.
- This is a log reader, not an anti-cheat-proof statement about the game's rules. It
  does not touch game memory or automate play, but whether overlay/log tools are permitted
  is defined by the game's terms of service; that call is yours.

## Reporting a vulnerability

Open an issue on the GitHub repository. For anything sensitive (something exploitable
rather than a hardening suggestion), say so in the issue without the details, and a
maintainer will arrange a private channel. Please include the app version (or commit),
what you observed, and reproduction steps. There is no bug bounty; fixes and credit are
what we can offer.
