using BuffBars.Core;
using Xunit;

namespace BuffBars.Tests;

/// <summary>
/// Loads the REAL EverQuest Legends client spell files and validates the parse against
/// lore-known values (research: plans/buff-bars-plan.md). Tests no-op on machines
/// without the game installed.
/// </summary>
public class SpellDbTests
{
    public const string GameDir = @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends";

    private static SpellDb? _db;
    private static SpellDb? TryLoad()
    {
        if (!File.Exists(Path.Combine(GameDir, "spells_us.txt"))) return null;
        return _db ??= SpellDb.LoadFromGameDir(GameDir);
    }

    [Fact]
    public void Loads_full_database()
    {
        var db = TryLoad(); if (db is null) return;
        Assert.InRange(db.Count, 70_000, 80_000);
    }

    [Fact]
    public void Spirit_of_wolf_decodes_correctly()
    {
        var db = TryLoad(); if (db is null) return;
        var sow = db.GetById(278);
        Assert.NotNull(sow);
        Assert.Equal("Spirit of Wolf", sow!.Name);
        Assert.True(sow.Beneficial);
        Assert.True(sow.IsBuff);
        Assert.True(sow.PlayerCastable);
        Assert.Equal(3, sow.DurationFormula);
        Assert.Equal(360, sow.DurationTicks);
        Assert.Equal(2160, sow.BaseDurationSeconds);          // 36 minutes at cap
        Assert.Equal(1620, sow.DurationSeconds(9));           // 9*30 ticks * 6s below cap
        Assert.Equal(2160, sow.DurationSeconds(60));          // capped
        Assert.Equal("You feel the spirit of wolf enter you.", sow.LandsOnYou);
        Assert.Equal("The spirit of wolf leaves you.", sow.WearOff);
    }

    [Fact]
    public void Lands_on_other_matches_target_and_spell()
    {
        var db = TryLoad(); if (db is null) return;
        var m = db.MatchLandsOnOther("Symmetry is surrounded by a brief lupine aura.");
        Assert.NotNull(m);
        Assert.Equal("Symmetry", m!.Value.Target);
        Assert.Contains(m.Value.Candidates, s => s.Id == 278);
    }

    [Fact]
    public void Lands_on_other_handles_possessive_suffixes()
    {
        var db = TryLoad(); if (db is null) return;
        // id 6's suffix is "'s blood ignites." per research
        var spell = db.GetById(6);
        if (spell is null || !spell.LandsOnOther.StartsWith("'s")) return;
        var m = db.MatchLandsOnOther("Doofus's blood ignites.");
        Assert.NotNull(m);
        Assert.Equal("Doofus", m!.Value.Target);
    }

    [Fact]
    public void Detrimentals_and_instants_classified()
    {
        var db = TryLoad(); if (db is null) return;
        var chords = db.GetById(703);                          // Chords of Dissonance
        Assert.NotNull(chords);
        Assert.False(chords!.Beneficial);
        Assert.True(chords.IsDetrimentalTimed);
        var minorHealing = db.GetById(200);
        Assert.NotNull(minorHealing);
        Assert.True(minorHealing!.Beneficial);
        Assert.False(minorHealing.IsBuff);                     // instant - no duration
    }

    [Fact]
    public void Npc_only_spells_flagged()
    {
        var db = TryLoad(); if (db is null) return;
        var npcHaste = db.GetById(998);
        if (npcHaste is null) return;
        Assert.False(npcHaste.PlayerCastable);
    }

    [Fact]
    public void Lookup_by_name_prefers_player_castable()
    {
        var db = TryLoad(); if (db is null) return;
        var best = db.GetBestByName("Spirit of Wolf");
        Assert.NotNull(best);
        Assert.True(best!.PlayerCastable);
    }

    [Fact]
    public void Ambiguity_flags_shared_landing_texts()
    {
        var db = TryLoad(); if (db is null) return;
        // "You feel much faster." is shared by many haste spells - must be ambiguous
        var fast = db.GetByLandsOnYou("You feel much faster.");
        if (fast.Count > 1) Assert.All(fast, s => Assert.True(s.AmbiguousYou));
        Assert.True(fast.Count >= 1);
    }
}
