# Plan: Crowd Control panel + compact raid mode

Status: PLANNED (not started). Resume here. Prereq reading: CLAUDE.md (ground truth +
checklists), plans/buff-bars-plan.md. Origin: raid feedback 2026-08-16 - mez/charm timers
in the DoT panel proved high-value (Dazzle break countdowns during a Hate raid); they
deserve a dedicated, always-visible home. Priority order below.

## P1 - Crowd Control panel (the priority)

A dedicated overlay window for CC timers on mobs: mez, charm, root, fear.

### Classification (VERIFIED against this client's spells_us.txt, 2026-08-16)
Detrimental spells with these SPA ids in the effect slots (parse alongside the existing
regen/haste/DS/invuln flags in SpellDb.Load):
- SPA 31 = mez        (verified: Dazzle id 190 `1|31|2|...`, Mesmerize id 292)
- SPA 22 = charm      (verified: Beguile id 182 `1|22|4|...`, Charm id 300)
- SPA 99 = root       (verified: Root id 230 slot2 `2|99|-10000|...`)
- SPA 23 = fear       (verified: Fear id 229 `1|23|1|...`)
Spell flags: HasMez/HasCharm/HasRoot/HasFear + `CcKind` enum helper; `IsCc` =
!Beneficial && DurationTicks > 0 && any flag. Stuns (SPA 21/64) are near-instant
(durF 0) - excluded. Slows (SPA 11 base<100) are NOT CC for this panel (debated; skip v1).
Regression tests: GetById(190).HasMez, 182 charm, 230 root, 229 fear; plus a count
sanity check (player-castable CC > 20).

### Panel behavior
- New OverlayWindow "Crowd Control": config rect (`CcWindow`, default near top-center),
  tray toggle `Show CC panel` (default ON), config `ShowCcPanel`.
- Content: mob-store instances where Spell.IsCc, rendered FLAT (not grouped per mob),
  sorted soonest-break-first: label `Dazzle - Cleric of Innoruuk` (+ caster in parens if
  not the observer), time right-aligned. Flat rendering: either add a flat mode flag to
  OverlayWindow.Render or synthesize one PanelView in App.RenderTick from snap.Mobs.
- Colors by kind (freeze brushes): mez #9333EA, charm #D946EF, root #CA8A04, fear #10B981.
  Keep the amber<30s / red<10s urgency override (tighter than buff thresholds - CC
  reactions are faster).
- The DoT panel keeps showing CC too (it is a debuff timer); optionally add config
  `HideCcFromDotPanel` default true to avoid double-listing.

### Correction events (accuracy)
- MezBreakEvent ("X has been awakened by Y.") is ALREADY parsed but is a tracker no-op:
  add handler - remove mez-flagged instances on target X (any caster). Test with a real
  line: "Doofus has been awakened by a wan ghoul knight." (target may be a PC - mez on
  players lives in the char store; sweep both stores).
- Charm/root/fear early ends already covered by "Your X spell has worn off of Y." and
  death/zone sweeps. Charm break on YOURSELF ("You are no longer charmed.") - v1 no-op.
- Recast refresh already works (instance replace by (target, spellId, caster)).

## P2 - Compact raid mode

Tray toggle `Compact raid mode` (config `CompactRaidMode`, default off):
- Typography/density: font 11.5 -> 9.5, bar height 3 -> 2, row margins halved, panel
  header margins halved (parameterize the DataTemplate via bound sizes or a second
  ItemsControl style - simplest: bind sizes off the PanelView/RowView).
- Density rule change: in compact mode, ALL non-vital beneficial buffs group into the
  Quick Buffs aggregate regardless of duration (drop the 20-min threshold); panel shows:
  debuff alarms -> vitals -> aggregate. That is the real density win for 24-person raids.
- Consider max-panel height + "+N more" overflow indicator instead of silent clipping
  (the raid screenshot clipped Evilgrin's list mid-row).

## P3 - README raid screenshot
Swap docs screenshot for a raid shot (maintainer supplies, or capture live via the
PrintWindow composite used before). IMPORTANT: use a NEW filename (camo CDN caches by
URL - the rename trick, see commit 5da83fd).

## Ship checklist per feature (from CLAUDE.md)
dotnet test green (all new classification/behavior tests) -> rebuild Release -> restart
overlay live -> commit on master -> cherry-pick to public-main -> push origin
public-main:main. Never push master (private history).
