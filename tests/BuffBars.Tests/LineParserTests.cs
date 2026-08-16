using BuffBars.Core;
using Xunit;

namespace BuffBars.Tests;

/// <summary>Every example here is a VERBATIM line mined from the user's real EQL logs.</summary>
public class LineParserTests
{
    private static LineParser? _parser;
    private static LineParser? P()
    {
        if (!File.Exists(Path.Combine(SpellDbTests.GameDir, "spells_us.txt"))) return null;
        return _parser ??= new LineParser(SpellDb.LoadFromGameDir(SpellDbTests.GameDir));
    }

    [Fact]
    public void Timestamp_parses_zero_padded_asctime()
    {
        var r = new LogLineReader();
        Assert.True(r.TryParse("[Sat Aug 01 00:00:00 2026] You feel much faster.", out var ts, out var action));
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0), ts);
        Assert.Equal("You feel much faster.", action);
        Assert.True(r.TryParse("[Thu Aug 13 23:04:43 2026] You begin casting Walking Sleep.", out ts, out _));
        Assert.Equal(new DateTime(2026, 8, 13, 23, 4, 43), ts);
    }

    [Fact]
    public void Cast_lines_self_and_other()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<CastStartEvent>(p.Parse("You begin casting Walking Sleep."));
        Assert.True(e1.IsSelf);
        Assert.Equal("Walking Sleep", e1.SpellName);

        var e2 = Assert.IsType<CastStartEvent>(p.Parse("A Teir`Dal ranger begins casting Light Healing."));
        Assert.False(e2.IsSelf);
        Assert.Equal("A Teir`Dal ranger", e2.Caster);
        Assert.Equal("Light Healing", e2.SpellName);

        var e3 = Assert.IsType<CastStartEvent>(p.Parse("Grimloc begins casting Juli's Animation."));
        Assert.Equal("Juli's Animation", e3.SpellName);
    }

    [Fact]
    public void Interrupt_and_fizzle()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<CastInterruptEvent>(p.Parse("Your Snails Healing spell is interrupted."));
        Assert.Equal("Snails Healing", e1.SpellName);
        var e2 = Assert.IsType<CastInterruptEvent>(p.Parse("a Teir`Dal ranger's Light Healing spell is interrupted."));
        Assert.Equal("a Teir`Dal ranger", e2.Caster);
        var e3 = Assert.IsType<CastFizzleEvent>(p.Parse("Your Healing spell fizzles!"));
        Assert.Equal("Healing", e3.SpellName);
    }

    [Fact]
    public void Dot_ticks_all_three_forms()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<DotTickEvent>(p.Parse("A Teir`Dal ranger has taken 25 damage from your Denon's Disruptive Discord."));
        Assert.Equal("A Teir`Dal ranger", e1.Target);
        Assert.Equal(25, e1.Damage);
        Assert.Equal("Denon's Disruptive Discord", e1.SpellName);
        Assert.Equal("", e1.Caster);
        Assert.False(e1.Critical);

        var e2 = Assert.IsType<DotTickEvent>(p.Parse("A Teir`Dal shadowknight has taken 57 damage from your Chords of Dissonance. (Critical)"));
        Assert.True(e2.Critical);
        Assert.Equal("Chords of Dissonance", e2.SpellName);

        var e3 = Assert.IsType<DotTickEvent>(p.Parse("A large plague rat has taken 8 damage from Suffocating Sphere by Grimloc."));
        Assert.Equal("Suffocating Sphere", e3.SpellName);
        Assert.Equal("Grimloc", e3.Caster);

        var e4 = Assert.IsType<DotTickEvent>(p.Parse("Dovhesi has taken 173674 damage by Wisp Explosion."));
        Assert.Equal("Wisp Explosion", e4.SpellName);
        Assert.Equal("", e4.Caster);
    }

    [Fact]
    public void Wear_off_forms()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<WearOffOtherEvent>(p.Parse("Your Denon's Disruptive Discord spell has worn off of Baron Telyx V`Zher."));
        Assert.Equal("Denon's Disruptive Discord", e1.SpellName);
        Assert.Equal("Baron Telyx V`Zher", e1.Target);

        var e2 = Assert.IsType<WearOffPetEvent>(p.Parse("Your pet's Hymn of Restoration spell has worn off."));
        Assert.Equal("Hymn of Restoration", e2.SpellName);
    }

    [Fact]
    public void Landing_texts_resolve_via_spell_db()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<LandSelfEvent>(p.Parse("You feel the spirit of wolf enter you."));
        Assert.Contains(e1.Candidates, s => s.Id == 278);

        var e2 = Assert.IsType<LandOtherEvent>(p.Parse("Symmetry is surrounded by a brief lupine aura."));
        Assert.Equal("Symmetry", e2.Target);

        var e3 = Assert.IsType<WearOffSelfEvent>(p.Parse("The spirit of wolf leaves you."));
        Assert.Contains(e3.Candidates, s => s.Id == 278);
    }

    [Fact]
    public void Heals_including_overheal_and_hot()
    {
        var p = P(); if (p is null) return;
        var e1 = Assert.IsType<HealEvent>(p.Parse("You healed Doofus over time for 61 hit points by Snails Healing."));
        Assert.True(e1.IsHot);
        Assert.Equal(61, e1.Amount);
        Assert.Equal("Doofus", e1.Target);

        var e2 = Assert.IsType<HealEvent>(p.Parse("You healed Doofus over time for 37 (61) hit points by Snails Healing."));
        Assert.Equal(37, e2.Amount);

        var e3 = Assert.IsType<HealEvent>(p.Parse("a Teir`Dal priest healed Korven Nisere over time for 55 hit points by Echoing Light."));
        Assert.Equal("a Teir`Dal priest", e3.Caster);
        Assert.Equal("Korven Nisere", e3.Target);
    }

    [Fact]
    public void Deaths_zones_groups_levels()
    {
        var p = P(); if (p is null) return;
        var d1 = Assert.IsType<DeathEvent>(p.Parse("A cracked skeleton has been slain by Greasy!"));
        Assert.Equal("A cracked skeleton", d1.Victim);
        var d2 = Assert.IsType<DeathEvent>(p.Parse("You have slain Korven Nisere!"));
        Assert.Equal("Korven Nisere", d2.Victim);
        var d3 = Assert.IsType<DeathEvent>(p.Parse("You have been slain by Baron Telyx V`Zher!"));
        Assert.True(d3.IsObserverDeath);

        var z = Assert.IsType<ZoneEvent>(p.Parse("You have entered Befallen 4 (Refined)."));
        Assert.Equal("Befallen 4 (Refined)", z.ZoneName);

        var g1 = Assert.IsType<GroupEvent>(p.Parse("Nuddle has joined the group."));
        Assert.True(g1.Joined);
        var g2 = Assert.IsType<GroupEvent>(p.Parse("Nuddle has left the group."));
        Assert.False(g2.Joined);
    }

    [Fact]
    public void Stacking_block_and_item_proc()
    {
        var p = P(); if (p is null) return;
        var b = Assert.IsType<BlockedEvent>(p.Parse("Your Chloroplast spell did not take hold on Bezerkher. (Blocked by Regrowth.)"));
        Assert.Equal("Chloroplast", b.SpellName);
        Assert.Equal("Bezerkher", b.Target);
        Assert.Equal("Regrowth", b.Blocker);

        var i = Assert.IsType<ItemProcEvent>(p.Parse("Your Polished Mithril Mask (Exaltation) feels alive with power."));
        Assert.Equal("Exaltation", i.SpellName);
    }

    [Fact]
    public void Chat_lines_are_never_parsed_as_combat()
    {
        var p = P(); if (p is null) return;
        Assert.Null(p.Parse("Wretch tells the group, 'a skeleton has taken 500 damage from your Fire.'"));
        Assert.Null(p.Parse("You say, 'You have entered The Feerrott.'"));
        Assert.Null(p.Parse("Voidling says, 'Your hubris risks our very reality itself.'"));
    }

    [Fact]
    public void Fixture_replay_parses_without_exceptions_and_finds_events()
    {
        var p = P(); if (p is null) return;
        var (fixture, isLocal) = ResolveReplayFixture();
        if (fixture is null) return;
        var reader = new LogLineReader();
        int lines = 0, events = 0, casts = 0, dots = 0, lands = 0;
        foreach (var raw in File.ReadLines(fixture))
        {
            if (!reader.TryParse(raw, out _, out var action)) continue;
            lines++;
            var e = p.Parse(action);
            if (e is null) continue;
            events++;
            if (e is CastStartEvent) casts++;
            if (e is DotTickEvent) dots++;
            if (e is LandSelfEvent or LandOtherEvent) lands++;
        }
        if (isLocal)
        {
            Assert.True(lines > 20_000, $"only {lines} lines parsed");
            Assert.True(casts > 100, $"only {casts} casts");
            Assert.True(dots > 100, $"only {dots} dot ticks");
            Assert.True(lands > 50, $"only {lands} landing events");
        }
        else
        {
            Assert.True(lines > 150, $"only {lines} lines parsed");
            Assert.True(casts >= 5, $"only {casts} casts");
            Assert.True(dots >= 5, $"only {dots} dot ticks");
            Assert.True(lands >= 3, $"only {lands} landing events");
        }
    }

    /// <summary>
    /// Replay fixture resolution: prefer the maintainer's real log tail under
    /// tests/fixtures/local/ (gitignored - real logs contain other players' chat), else fall
    /// back to the committed synthetic tests/fixtures/sample.txt (fictional names, every
    /// message family). IsLocal picks the matching assertion thresholds.
    /// </summary>
    internal static (string? Path, bool IsLocal) ResolveReplayFixture()
    {
        var root = FindRepoRoot();
        var local = Path.Combine(root, "tests", "fixtures", "local", "doofus_recent.txt");
        if (File.Exists(local)) return (local, true);
        var sample = Path.Combine(root, "tests", "fixtures", "sample.txt");
        return File.Exists(sample) ? (sample, false) : (null, false);
    }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
