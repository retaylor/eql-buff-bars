# CLAUDE.md

Agent-facing project brief. Read this file completely before changing any code.

## What this is

**EqlBuffBars** - a C#/.NET 8 WPF click-through overlay for the EverQuest Legends MMO that shows
live buff/DoT timers for the player's party by tailing the game's own text log files (read-only)
and parsing the client's spell-data files. It never touches game memory, never injects, has zero
network I/O, and writes only its own `%AppData%\EqlBuffBars\config.json`.

## Solution map

```
BuffBars.sln
src/BuffBars.Core/               net8.0 class lib, no WPF - all logic + all tests target this
  SpellDb.cs                     spells_us.txt + spells_us_str.txt -> Spell records + lookup indexes
  Spell.cs                       one spell: duration formulas, SPA-derived flags, IsBuff/IsVitalBuff
  LogLine.cs                     LogLineReader: timestamp slice + cached parse (STATEFUL, one per character)
  LogTailer.cs                   LogTailer (one live eqlog follow) + LogWatcher (directory rescan)
  LineParser.cs                  action text -> LogEvent; chat filter first; ordered prefix matching
  LogEvents.cs                   the LogEvent record hierarchy - the parser<->tracker contract
  Tracker.cs                     events -> BuffInstance state; snapshots for rendering
  Names.cs                       actor-name canonicalization (article strip, player heuristic)
  Config.cs                      AppConfig: %AppData%\EqlBuffBars\config.json
src/BuffBars.App/                net8.0-windows WPF (assembly name EqlBuffBars)
  App.xaml.cs                    startup wiring: tail -> channel -> parser -> tracker -> 250ms render
  OverlayWindow.xaml(.cs)        one panel window: render rows, edit mode, Quick Buff aggregate
  Win32.cs                       the ONLY P/Invoke: click-through + topmost (user32)
tests/BuffBars.Tests/            xunit: SpellDbTests, LineParserTests, TrackerTests, LogTailerTests
tests/fixtures/                  log fixtures for replay tests (machine-local; tests skip if absent)
plans/buff-bars-plan.md          research ground truth (log mining, field maps) - keep in sync
plans/ship-plan.md               public-release checklist
```

## Build / test / verify

`dotnet` is on PATH or at `C:\Users\retaylor\.dotnet\dotnet.exe`.

```powershell
dotnet build BuffBars.sln                      # build everything
dotnet test                                    # 30 tests, ~5s (~10s cold with build)
dotnet run --project src\BuffBars.App          # run the overlay (needs the game's spell files)
```

**A change is NOT verified unless `dotnet test` ran green.** Do not claim a change works
because "the code looks right".

**Silent no-op trap**: most tests begin with a skip-if-missing guard (`if (db is null) return;` /
`if (!File.Exists(...)) return;`):

- `SpellDbTests`, `LineParserTests` (except the timestamp test), `TrackerTests` all need the game
  installed at `C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends`
  (`spells_us.txt` present). Without it they PASS while asserting nothing.
- The fixture-replay tests additionally need `tests/fixtures/*.txt` (local, not in the public repo).
- Only `LogTailerTests` (temp files) and the timestamp test run everywhere.

So on a machine without the game, "Passed: 30" is weak evidence for a parser or spell-DB change.
Say so explicitly when reporting verification from such a machine.

## VERIFIED GROUND TRUTH

Cross-validated against rumstil/eqspellparser and 162MB of real EQL logs (research:
`plans/buff-bars-plan.md`). Field indices are load-bearing - do not "fix" them from memory.

### spells_us.txt

`<game>\spells_us.txt`: ~74k lines, **exactly 173 caret-separated fields, no header**.
`SpellDb.ExpectedFieldCount = 173` is asserted per line; a mismatch throws `InvalidDataException`
(a game patch changed the format - indices must be re-verified, especially above ~85).
Read with `Encoding.Latin1` (ANSI, no BOM) - same for logs and the str file.

Field map (0-based):

| field | meaning |
|---|---|
| 0 / 1 | id / name (apostrophes are normal `'`; log spell names match exactly) |
| 8 / 9 / 10 | cast / recovery / recast (ms) |
| 11 / 12 | **duration formula / base duration in ticks** (tick = 6s). **TRAP: NOT [107]/[108]** - that pair is a near-duplicate of 11/12 and diverges on short formulas. Using it ships wrong timers that look right in spot checks. |
| 14 | mana |
| 28 | beneficial: 0 detrimental, 1 beneficial, 2 beneficial-variant -> **treat `>0` as beneficial** |
| 29 | resist type (1 magic, 2 fire, 3 cold, 4 poison, 5 disease...) |
| 30 | target type (4 PBAE, 5 single, 6 self, 13 lifetap, 41 group-v2, **51 single-friendly - this client's legacy buff target, treat as single**, 3 group-v1, 14 pet) |
| 36..51 | 16 class levels WAR..BER; 255 = unusable. All-255 = NPC-only (`PlayerCastable = false`, deprioritized in name lookup) |
| 75 | spell icon id |
| 85 | DescID -> dbstr_us.txt `id^6^text` (tooltips; not yet consumed) |
| 172 (last, address as `f[^1]`) | effect slots: `slot\|spa\|base\|base2\|calc\|max` entries joined by `$` |

Duration: `seconds = ticks * 6`. Formula table lives in `Spell.DurationSeconds(level)` (classic:
0=instant, 1/6=ceil(lvl/2), 2=ceil(lvl*0.6), 3=lvl*30, 4=50, 5=2, 7=lvl, 8=lvl+10, 9=2lvl+10,
10=3lvl+10, 11/12/15=base, 50=permanent, 3600=6h/base; result capped at base ticks). Without a
known caster level we assume the base cap - a documented **overestimate**. Own-character levels
come from `LevelEvent` (`You have gained a level! Welcome to level N!`).

### SPA classifiers (SpellDb.Load, effect-slot loop)

| SPA | condition | flag | notes |
|---|---|---|---|
| 0 | `base > 0` on a timed spell | `HasRegen` | +HP per tick = regen/HoT |
| 11 | `base >= 100` | `HasHaste` | melee haste (values are percent-of-normal) |
| 59 | `base != 0` | `HasDamageShield` | **SIGN TRAP: base is NEGATIVE in this client** (damage dealt to the attacker). A `base > 0` check classifies zero damage shields - this already bit once (Shield of Spikes regression, see `TrackerTests.Vital_buff_classification_from_effect_slots`). |

`IsVitalBuff` = beneficial + timed + (HasRegen | HasHaste | HasDamageShield); vital buffs are
pinned/tinted in the overlay and excluded from the Quick Buff aggregate.

### spells_us_str.txt

1:1 rows with spells_us.txt, 7 caret fields, **HAS a header row** starting `#SPELLINDEX^...`
(skip lines starting `#`).

| col | meaning |
|---|---|
| 0 | spell id |
| 1 / 2 | cast-by texts, mostly empty (unused) |
| 3 | lands-on-you (full line, e.g. `You feel the spirit of wolf enter you.`) |
| 4 | lands-on-other (**SUFFIX** - client prepends the target name; often starts with a space or `'s`) |
| 5 | wear-off text (per-spell self emote, e.g. `Your speed returns to normal.`) |

### Log files and line families

Logs: `<game>\Logs\eqlog_<Char>_<server>.txt` - ANSI/ASCII, no BOM; **character identity comes
from the FILENAME** (`LogTailer.CharacterFromFileName`). Line shape:
`[Www Mmm DD HH:MM:SS YYYY] action` - zero-padded asctime, action starts at column 27
(`LogLineReader.PrefixLength`).

Verbatim examples mined from real logs (every parser change must quote one like these in a test):

| family | verbatim example | event |
|---|---|---|
| cast self | `You begin casting Walking Sleep.` (also `You begin singing ...`) | `CastStartEvent` |
| cast other | ``A Teir`Dal ranger begins casting Light Healing.`` - **casting always names the spell for everyone**; the classic `begins to cast a spell.` form had 0 occurrences in 162MB, so other-caster buff timers are possible | `CastStartEvent` |
| fizzle | `Your Healing spell fizzles!` | `CastFizzleEvent` |
| interrupt | `Your Snails Healing spell is interrupted.` / ``a Teir`Dal ranger's Light Healing spell is interrupted.`` | `CastInterruptEvent` |
| land self | `You feel the spirit of wolf enter you.` (= str[3], whole-line DB match) | `LandSelfEvent` |
| land other | `Symmetry is surrounded by a brief lupine aura.` (str[4] suffix match; residual prefix = target name) | `LandOtherEvent` |
| wear-off yours-on-other | ``Your Denon's Disruptive Discord spell has worn off of Baron Telyx V`Zher.`` (names the spell!) | `WearOffOtherEvent` |
| wear-off pet | `Your pet's Hymn of Restoration spell has worn off.` | `WearOffPetEvent` |
| wear-off self emote | `The spirit of wolf leaves you.` (= str[5], whole-line DB match) | `WearOffSelfEvent` |
| DoT tick, your | ``A Teir`Dal ranger has taken 25 damage from your Denon's Disruptive Discord.`` | `DotTickEvent` |
| DoT tick, other-caster | `A large plague rat has taken 8 damage from Suffocating Sphere by Grimloc.` | `DotTickEvent` |
| DoT tick, casterless | `Dovhesi has taken 173674 damage by Wisp Explosion.` | `DotTickEvent` |
| crit suffix | `... from your Chords of Dissonance. (Critical)` - modifier appended AFTER the period; stripped up front (also `Lucky Critical`, `Twincast`) | flag on event |
| heal / HoT | `You healed Doofus over time for 61 hit points by Snails Healing.`; overheal renders `37 (61)` - take the first number | `HealEvent` (IsHot = "over time") |
| stacking block | `Your Chloroplast spell did not take hold on Bezerkher. (Blocked by Regrowth.)` | `BlockedEvent` |
| death | `A cracked skeleton has been slain by Greasy!` / `You have slain Korven Nisere!` / ``You have been slain by Baron Telyx V`Zher!`` / `... died.` | `DeathEvent` |
| zone | `LOADING, PLEASE WAIT...` then `You have entered Befallen 4 (Refined).` (parser keys on the latter) | `ZoneEvent` |
| group | `Nuddle has joined the group.` / `... has left the group.` / `You have joined the group.` / `You have been removed from the group.` | `GroupEvent` |
| level | `You have gained a level! Welcome to level 12!` | `LevelEvent` |
| item proc | `Your Polished Mithril Mask (Exaltation) feels alive with power.` (parenthesized = spell name) | `ItemProcEvent` |
| activate (AA/disc) | `You activate Quick Buff.` / `<Name> activates Quick Buff.` | `ActivateEvent` |
| mez break | `<Name> has been awakened by <X>.` | `MezBreakEvent` |
| resist | `<Target> resisted your <Spell>!` / `You resist <Caster>'s <Spell>!` | `ResistEvent` |

**The NO-generic-self-wear-off rule (duration-primary model).** There is NO generic
"your spell has worn off" line for your own buffs - 0 hits in 162MB. The only wear-off lines are:
your casts on OTHERS (`Your <Spell> spell has worn off of <Name>.`), your pet's, and per-spell
self emotes (str[5]). Therefore **self-buff expiry is TIMER-driven** (DB duration), with emotes,
deaths, zoning, and dispels acting as corrections. Never build display logic that requires a
wear-off line to remove a bar.

**Quick Buff grammar**: the Quick Buff AA fires `You activate Quick Buff.` /
`<Name> activates Quick Buff.` The tracker timestamps it per actor; the overlay collapses long
(>= `QuickBuffMinDurationSeconds`, default 600s) non-vital beneficial buffs into one
`Quick Buffs (N)` row with cooldown countdown (`QuickBuffCooldownSeconds`, default 300s).

**Names**: PCs are a single capitalized word; NPCs have `a/an/the` + phrase, article case is
inconsistent -> `Names.Key` strips the article and lowercases; compare keys, never displays.
Backticks appear inside names (`` Teir`Dal ``). `/who` lines have TRAILING SPACES. Heuristic
`Names.IsLikelyPlayer` = single word starting uppercase.

## INVARIANTS - do not break

1. **`FileShare.ReadWrite | FileShare.Delete` on every log FileStream** (`LogTailer.RunAsync`).
   EQ holds the file open for append; narrower sharing fails or interferes with the game.
   Truncation (`length < position`) or delete/rename triggers the reopen loop - keep it.
2. **Chat lines are filtered BEFORE combat parsing.** First check in `LineParser.Parse`:
   any action containing `, '` (comma-space-quote) is chat and is dropped. Chat can quote combat
   lines verbatim (`Wretch tells the group, 'a skeleton has taken 500 damage from your Fire.'`) -
   any matcher placed before this filter is a mis-parse factory.
3. **Timestamp cache pattern**: `LogLineReader` re-parses the stamp only when the 24-char
   substring changes, so it is STATEFUL and must stay **one instance per character** (see the
   consumer dictionary in `App.OnStartup`). Never share one reader across logs.
4. **`LogEvents.cs` records are the public contract between parser and tracker.** Additive
   changes only: new record types or new properties with defaults. Never rename, repurpose, or
   change the meaning of an existing member.
5. **config.json must stay backward compatible.** Additive keys with sensible C# defaults only;
   never rename or re-type existing keys. `AppConfig.Load()` must keep falling back to defaults
   on corrupt/missing files.
6. **Tests skip-if-missing** for machine-dependent inputs (game install, `tests/fixtures/`):
   guard at the top of the test, silent `return`. The suite must pass on a clean CI machine.
7. **Zero network I/O.** Adding any network call - or any new P/Invoke - requires updating
   SECURITY.md in the same change. The current P/Invoke surface is exactly `Win32.cs`
   (user32 `GetWindowLongPtr`/`SetWindowLongPtr`/`SetWindowPos` for click-through + topmost).
8. **No regex in hot paths** (parser, tail loop). Use `StartsWith`/`IndexOf`/`EndsWith` with
   explicit `StringComparison.Ordinal`, ordered by real-world frequency.
9. **Never toggle click-through on a live window.** Edit mode is a separate instantiation
   (`App.OpenOverlays(editMode)` closes and recreates); `Win32.MakeClickThrough` is applied once
   at `OnSourceInitialized`. Topmost is re-asserted on a ~2s cadence because the game reorders z.

## CHECKLISTS

### Adding a log line family

1. Obtain a REAL log line, verbatim (own logs or a contributor sample; see CONTRIBUTING.md).
2. Add an event record in `src/BuffBars.Core/LogEvents.cs` (additive - invariant 4).
3. Route it in `src/BuffBars.Core/LineParser.cs`: after the chat filter, Ordinal string ops,
   no regex, positioned by expected frequency.
4. Handle it in `src/BuffBars.Core/Tracker.cs` (`OnEvent` switch + handler) if it changes state.
5. Regression test in `tests/BuffBars.Tests/LineParserTests.cs` quoting the real line VERBATIM
   (plus a `TrackerTests` case if state changes). Include the chat-quoted version of the line as
   a must-not-parse case if plausible.
6. Update the line-family catalog table in this file.
7. `dotnet test` green.

### Adding a SPA-based buff class

1. Identify the SPA number and its base-value semantics - **check the sign against real spells
   in this client's spells_us.txt** (remember SPA 59's negative base).
2. Parse it in the effect-slot loop in `SpellDb.Load` (`f[^1]`, `$`-joined entries,
   `|`-separated `slot|spa|base|base2|calc|max`).
3. Add an init-only flag on `Spell` (`src/BuffBars.Core/Spell.cs`); fold into `IsVitalBuff`
   or a new classification property as appropriate.
4. Classifier test with a KNOWN spell (by id or name) plus a population sanity count - copy the
   pattern of `TrackerTests.Vital_buff_classification_from_effect_slots`.
5. UI treatment in `OverlayWindow.Render` (brush, rank, filter) if it should look different.
6. `dotnet test` green.

### Adding an overlay panel

1. Config: an `OverlayRect` property + a `Show...` bool in `src/BuffBars.Core/Config.cs`
   (additive, defaulted - invariant 5).
2. Instantiate in `App.OpenOverlays` (follow the `_enemyBuffWindow` pattern: close old, null,
   conditionally create).
3. Feed it in `App.RenderTick` with the right snapshot slice + filters
   (`beneficialFilter`, `isDotPanel`, `barBaseOverride`).
4. Tray toggle in `App.SetupTray`: `Checked` from config, save + `OpenOverlays` on change.
5. Verify by hand: config round-trips, edit mode drag/resize saves the rect, normal mode is
   click-through, panel survives a re-open.

## Release process

1. Bump `<Version>` in `src/BuffBars.App/BuffBars.App.csproj`.
2. `dotnet test` green.
3. `tools/publish.ps1` - self-contained single-file win-x64 publish to `dist/EqlBuffBars.exe`;
   run the exe once to verify it boots.
4. Commit, tag `v<version>`.
5. Cross-check the current release checklist in `plans/ship-plan.md`.
