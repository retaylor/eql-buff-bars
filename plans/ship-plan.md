# Ship plan â€” public release + contribution readiness

Goal: MIT-licensed public repo, shippable single-file build, human README, AI-native
CLAUDE.md/CONTRIBUTING.md, security/privacy audited. Resume from the checkboxes below if a
session is interrupted.

## Checklist

- [x] **P1 Privacy: fixtures** â€” real logs out of git history scope for the public release:
      move `tests/fixtures/doofus_recent.txt` + `sira_recent.txt` â†’ `tests/fixtures/local/`
      (gitignored). Commit a synthetic `tests/fixtures/sample.txt` (~200 lines, fictional
      names, every message family from plans/buff-bars-plan.md catalog). Replay tests use
      local/ when present, else skip silently (same pattern as the game-files skip).
      NOTE: repo history contains the old fixtures â€” public release must be a FRESH repo /
      squashed history or history rewrite. Plan: squash-init a clean history before pushing.
- [x] **P2 Security audit** â€” verify + document: zero network I/O, no input hooks, no process
      injection; P/Invoke inventory (user32 SetWindowLongPtr/SetWindowPos = click-through +
      topmost only); file writes limited to %AppData%\EqlBuffBars; no secrets/personal paths
      in sources. Output: SECURITY.md + README AV-false-positive note.
- [x] **P3 License** â€” LICENSE (MIT, "2026 EQL Buff Bars contributors").
- [x] **P4 README.md** â€” human-readable: what/why, screenshot (docs/), requirements
      (Windows x64, EQ Legends, /log on per character, windowed/borderless mode - exclusive
      fullscreen hides overlays), quickstart, tray menu, config.json reference, how it works
      (client spell DB + log parsing; no game memory access, no injection), troubleshooting,
      building from source, license + attribution (EQLogParser inspiration).
- [x] **P5 CLAUDE.md** â€” AI-native: architecture map with file paths; VERIFIED ground truth
      (spells_us 173 fields, field map, SPA classifiers incl. 59-negative-base trap,
      [107]/[108] trap, str-file columns, log line families with real examples, no-self-wear-off
      rule); invariants that must not break (FileShare.ReadWrite|Delete, chat-first parse,
      duration-primary model, event record contracts, config back-compat); build/test/verify
      commands; feature checklists (new line family, new SPA class, new panel).
- [x] **P6 CONTRIBUTING.md** â€” AI-native workflow: read CLAUDE.md first; every parser change
      ships a regression test built from a REAL log line; how to contribute log samples for
      new message families/servers (sanitize chat lines first); PR checklist; style rules.
- [x] **P7 Repo hygiene** â€” .gitattributes (eol=lf for sources, kill CRLF warnings),
      .editorconfig, remove tools_smoke/, move docs_overlay_buffs.png â†’ docs/, csproj
      metadata (Version 0.9.0, Authors, RepositoryUrl placeholder, Description).
- [x] **P8 Publish path** â€” tools/publish.ps1: dotnet publish win-x64 self-contained
      single-file â†’ dist/EqlBuffBars.exe; README instructions; verify the exe runs.
- [x] **P9 CI** â€” .github/workflows/build.yml: windows-latest, .NET 8, build + test
      (tests skip gracefully without game files - documented limitation).
- [x] **P10 Verify + ship** â€” dotnet test green; publish exe boots; docs reviewed; commit;
      tag v0.9.0; squash-init clean public history when pushing to GitHub.

## Facts agents need

- Repo: C:\Users\retaylor\work\eql-buff-bars (solution BuffBars.sln; src/BuffBars.Core,
  src/BuffBars.App WPF, tests/BuffBars.Tests xunit; dotnet at C:\Users\retaylor\.dotnet).
- Ground truth research: plans/buff-bars-plan.md. Game paths inside it are the maintainer's
  install; they are PUBLIC paths (C:\Users\Public\...) - fine to keep in docs.
- Tests skip-if-missing pattern: `if (!File.Exists(...)) return;` at test start.
- The app: reads game spell files + log files (read-only), writes only its own config.
- Known AV-sensitive but legitimate: WS_EX_LAYERED|WS_EX_TRANSPARENT click-through overlay,
  HWND_TOPMOST reassert, log tailing with ReadWrite|Delete sharing.

## Ship status 2026-08-15

All ten phases complete locally. v0.9.0 tagged. dist/EqlBuffBars.exe published
(self-contained single file, SHA256 51C6B75AD6C251CDC4308893759022B109ECC24CE93480B3D37DCB6ACCCADF95)
and verified running live.

REMAINING BEFORE PUBLIC PUSH (one step): git history still contains real player logs
from early commits. When creating the GitHub repo: init a FRESH repository from the
current working tree (or git checkout --orphan + single initial commit), do NOT push
this local history. Then: create repo, push, attach dist exe + SHA256 to a v0.9.0
release, update README repository links if desired.
