# Contributing to EQL Buff Bars

This guide is addressed to both humans and their coding agents. **If you use a coding agent
(Claude Code, Copilot, Cursor, ...), point it at `CLAUDE.md` first** - that file carries the
verified ground truth (spell-file field maps, log line grammar, known traps) and the invariants
that must not break. Everything below assumes you (or your agent) have read it.

## Setup

1. Windows 10/11 x64, .NET 8 SDK on PATH (`dotnet --version` >= 8.0).
2. Clone and build:

   ```powershell
   git clone <repo-url>
   cd eql-buff-bars
   dotnet build BuffBars.sln
   dotnet test
   ```

3. **Optional but strongly recommended**: an EverQuest Legends install at
   `C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends`. The spell-DB,
   parser, and tracker tests load the game's real `spells_us.txt` - without the game they
   silently no-op (they pass while asserting nothing). Fixture-replay tests likewise skip
   unless log fixtures exist under `tests/fixtures/`.

## The verify loop

- **`dotnet test` MUST be green before you open a PR.** ~30 tests, ~5 seconds.
- **Every parser change REQUIRES a regression test quoting a REAL log line verbatim** in
  `tests/BuffBars.Tests/LineParserTests.cs`. No invented lines - the whole value of the parser
  is that its grammar was mined from real logs. If you don't play, ask for a sample (below).
- If your machine has no game install, say so in the PR: your green run only exercised the
  machine-independent tests, and a maintainer run on a game machine is the real verification.
- Tracker behavior changes get a `TrackerTests` case; spell-classification changes get a
  known-spell assertion plus a population sanity count (copy
  `Vital_buff_classification_from_effect_slots`).

## Contributing log samples

The most valuable non-code contribution is a log sample for a message family the parser does
not cover yet (see the family catalog in `CLAUDE.md`). Logs live at
`<game>\Logs\eqlog_<Char>_<server>.txt` (enable with `/log on` in game).

**Sanitize before sharing.** Player chat is the privacy risk, and every chat line contains
comma-space-quote (`, '`) - the same marker the parser uses to discard chat. Strip those lines:

```powershell
Get-Content "eqlog_Char_server.txt" | Where-Object { -not $_.Contains(", '") } | Set-Content "sample_sanitized.txt" -Encoding ascii
```

Then skim the result once for anything you still don't want public (character names remain).
Attach the sanitized sample to an issue, note which server/format it came from, and quote the
specific lines you think are unhandled.

## PR checklist

- [ ] `dotnet test` green (state whether the game files were present for the run).
- [ ] Parser change -> regression test quoting a real log line verbatim.
- [ ] No new P/Invoke without a SECURITY.md update in the same PR
      (current surface is exactly `src/BuffBars.App/Win32.cs`).
- [ ] No network I/O. None. A PR that adds any networking will not be merged without a
      SECURITY.md rewrite and explicit maintainer sign-off.
- [ ] `config.json` stays backward compatible: additive keys with defaults only; no renames,
      no re-typing, no removed keys.
- [ ] New log line family -> catalog table in `CLAUDE.md` updated.

## Code style

Match the existing code; do not introduce a formatter pass or restyle files you touch.

- File-scoped namespaces; `Nullable` enabled; `sealed` classes by default.
- Events are `record` types in `src/BuffBars.Core/LogEvents.cs` - additive changes only,
  they are the parser<->tracker contract.
- **No regex in hot paths** (parser, tail loop): `StartsWith`/`IndexOf`/`EndsWith` with
  explicit `StringComparison.Ordinal`.
- Core logic goes in `BuffBars.Core` (no WPF references) so it stays testable; the App project
  is wiring and rendering only.
- Keep source files ASCII-clean.

## Wanted contributions

- **Melee discipline / stance line families** - activation and drop lines for warrior/monk/etc.
  disciplines so they can get timers (samples first, then parsing).
- **Spell icons** - render the client's gemicons (`spells_us.txt` field 75 is the icon id)
  instead of text-only rows.
- **Other-server format variants** - samples from other EQ emulator servers whose log grammar
  diverges, so parsing can be made server-aware.
- **AA duration tables** - real data for buff-extension AAs to replace the single global
  `ExtendBeneficialPercent` approximation.
