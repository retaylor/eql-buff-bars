# EQL Buff Bars — implementation plan

Real-time buff/DoT overlay for EverQuest Legends, driven by live log parsing (EQLogParser-style,
but multi-log-native and focused on party buffs + mob DoTs).

## Ground truth (from research 2026-08-15)

### Game paths
- Game: `C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends`
- Logs: `<game>\Logs\eqlog_<Char>_<server>.txt` (ANSI/ASCII no BOM; char/server identity comes
  from the FILENAME). Live files are held open by EQ — open with `FileShare.ReadWrite | Delete`.
- Spell DB: `<game>\spells_us.txt` (73,963 lines × exactly 173 caret fields, no header) +
  `<game>\spells_us_str.txt` (1:1 rows, HAS header `#SPELLINDEX^...`, 7 fields).

### spells_us.txt field map (0-based, cross-validated vs rumstil/eqspellparser)
| field | meaning |
|---|---|
| 0 / 1 | id / name (apostrophes are normal `'`, log names match) |
| 8 / 9 / 10 | cast / recovery / recast (ms) |
| 11 / 12 | duration formula / base duration in ticks (tick = 6s). NOT [107]/[108] (trap: near-duplicate pair, diverges on short formulas) |
| 14 | mana |
| 28 | beneficial: 0 det, 1 ben, 2 ben-variant → treat `>0` |
| 29 | resist type (1 magic, 2 fire, 3 cold, 4 poison, 5 disease…) |
| 30 | target type (4 PBAE, 5 single, 6 self, 13 lifetap, 41 group-v2, **51 single-friendly (this client's legacy buff target — treat as single)**, 3 group-v1, 14 pet) |
| 36..51 | 16 class levels WAR..BER, 255 = unusable (all-255 + 0 mana = NPC-only, filter from name lookup) |
| 75 | spell icon |
| 85 | DescID → dbstr_us.txt `id^6^text` (tooltips, phase 2) |
| 172 (last) | effect slots `slot|spa|base|base2|calc|max` joined by `$` (address from end) |

Duration formulas (classic table, result capped at base ticks): 0=instant, 1=ceil(lvl/2),
2=ceil(lvl*0.6), 3=lvl*30, 4=50, 5=2, 6=ceil(lvl/2), 7=lvl, 8=lvl+10, 9=2*lvl+10, 10=3*lvl+10,
11/12=base, 50=permanent, 3600=6h/base. Without caster level, assume base cap (document as
overestimate); own-character level tracked from `You have gained a level!` + /who lines.

### spells_us_str.txt columns
`[0]` id · `[3]` lands-on-you · `[4]` lands-on-other (SUFFIX — target name prepended, often starts
with space or `'s`) · `[5]` wear-off text. `[1]/[2]` cast-by texts, mostly empty. Skip header row.

### Log line formats (mined from 162MB of real logs — see plans/research notes + tests/fixtures)
- Line: `[Www Mmm DD HH:MM:SS YYYY] msg` — zero-padded asctime, action starts at col 27.
- **Casting always names the spell** for everyone: `X begins casting <Spell>.` / `begins singing` —
  0 occurrences of classic `begins to cast a spell.` → other-caster buff timers are possible.
- Landing: self = str[3] exact line (`You feel the spirit of wolf enter you.`); other =
  `<Name>` + str[4] suffix (`Symmetry is surrounded by a brief lupine aura.`).
- **Wear-off: NO generic self form exists** (0 hits in 162MB). Only:
  `Your <Spell> spell has worn off of <Name>.` (your casts on others — names the spell!),
  `Your pet's <Spell> spell has worn off.`, and per-spell self emotes (str[5], e.g.
  `Your speed returns to normal.`). → self-buff expiry is TIMER-driven with emote corrections.
- DoT ticks: `<Mob> has taken <N> damage from your <Spell>.` / `from <Spell> by <Caster>.` /
  casterless `damage by <Spell>.` — modern order, NOT classic possessive. `(Critical)` appended
  AFTER the period. Ticks are liveness heartbeats.
- Stacking block: `Your <Spell> spell did not take hold( on <Name>)?. (Blocked by <Other>.)`
- Deaths: `<X> has been slain by <Y>!` / `You have slain <X>!` / `You have been slain by <Y>!`
  → clear target's timers; own death clears all self buffs.
- Zoning: `LOADING, PLEASE WAIT...` then `You have entered <Zone>.` → drop song-window buffs,
  clear that character's mob/DoT view.
- Heals/HoTs: `You healed <Name> over time for N (M) hit points by <Spell>.` — HoT heartbeat.
- Group roster: `<Name> has joined/left the group.`, `You have joined the group.`
- Mez break: `<Name> has been awakened by <X>.`; charm: `You lose control of yourself!` /
  `You are no longer charmed.`; invis: `<Name> fades away.` / `You appear.`
- Item procs: `Your <Item> (<SpellName>) feels alive with power.` — parenthesized = spell name.
- Names: PCs single capitalized word; NPCs `a/an/the` + phrase (article case inconsistent —
  compare case-insensitively); backticks in names; `/who` = `[47 WAR/MNK/BST] Name (Race) <Guild>
  ZONE: ...` with TRAILING SPACES. Chat must be filtered before combat parsing.

### EQLogParser techniques adopted
- Tail: FileStream(Read, Share=ReadWrite|Delete, 128KB buffer, SequentialScan), drain-to-EOF then
  200ms delay; truncation = `fs.Length < pos` OR FileSystemWatcher delete/rename → reopen loop.
- Timestamp cache: reparse only when chars [1..24] change. Lines ≤ 28 chars dropped.
- No regex in the hot path: prefix/token matching; chat classified first.
- Landing-text matching: reverse-word index (last word → candidates → suffix compare); residual
  prefix = target name. Ambiguous texts resolved by recent-cast correlation (last ~10s).
- Overlay: WPF `WindowStyle=None AllowsTransparency Background=#01000000 ShowActivated=false`,
  click-through via `WS_EX_LAYERED|WS_EX_TRANSPARENT` (+`TOOLWINDOW|NOACTIVATE`), HWND_TOPMOST
  re-asserted every 2s, separate non-click-through instantiation for edit mode (never toggle live),
  timer-driven render (~250ms) with change-only updates.

## Architecture

```
src/BuffBars.Core          net8.0 class lib (no WPF)
  SpellDb.cs               parse spells_us(.str) → Spell records + name/landing/wear indexes
  DurationFormula.cs       classic formula table
  LogLine.cs               timestamp parse (cached), action slicing
  LogTailer.cs             one live tail per eqlog file (async, truncation-safe)
  LogWatcher.cs            directory watcher: attach/detach tailers as eqlog files appear
  Parser/                  ordered matchers → LogEvent (Cast, Landed, WearOff, DotTick, Heal,
                           Death, Zone, Group, Block, LevelUp, CharmMez…)
  Tracker.cs               event → state: per-character BuffInstance set, per-mob DotInstance set,
                           multi-log dedupe (target+spell within 2s), ambiguity resolution via
                           recent casts, corrections (wear emote/death/zone/dispel)
  Config.cs                JSON config (%AppData%\EqlBuffBars)
src/BuffBars.App           net8.0-windows WPF
  OverlayWindow            per-panel: character buff bars / mob DoT bars (legends_dark palette:
                           #14161B glass, #C9A86A gold, gauge colors; time bar + mm:ss)
  EditMode                 drag/resize instantiation, save rects
  TrayIcon                 enable/disable panels, edit mode, exit
tests/BuffBars.Tests       xunit; fixtures = real log tails (tests/fixtures/*.txt)
```

## Build order
1. **P1 SpellDb** — load client files, indexes, duration calc. Test: SoW=2160s cap, Chords
   detrimental beneficial>0=false, str landing/wear joins, NPC-only filtered.
2. **P2 Parser** — all families above from fixtures; measure: 30k-line fixture parses < 1s,
   zero mis-parses of chat.
3. **P3 Tracker** — state machine + dedupe + corrections; replay fixture produces plausible
   buff/DoT sets (assert known sequences from the fixture).
4. **P4 Tailer/Watcher** — live follow with truncation tests (write temp file, append, truncate).
5. **P5 Overlay** — panels, edit mode, config, tray. Verify live on the user's machine in game.
6. **P6 polish** — spell icons (gemicons dds), per-buff colors, filters UI, dispel/charm alerts,
   backfill window (default 90 min) on startup.

## Decisions
- Parse client spell files at startup (~74k lines, <1s) — no offline generation step, always
  patch-current. Cache parsed form with file-mtime key if startup cost matters.
- Field-count guard: assert 173 fields on load; if it changes after a patch, warn and fall back
  to last-known-good cache (indices above ~85 may shift).
- Multi-log native: every eqlog in the folder is tailed; per-target state prefers the target's
  own log, then caster logs, then observers.
- No Syncfusion/commercial deps. LiteDB unnecessary — JSON config only.
- .NET 8 self-contained single-file publish for distribution later; dev runs framework-dependent.
