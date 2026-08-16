using BuffBars.Core;
using Xunit;

namespace BuffBars.Tests;

public class TrackerTests
{
    private static SpellDb? Db()
    {
        if (!File.Exists(Path.Combine(SpellDbTests.GameDir, "spells_us.txt"))) return null;
        return SpellDb.LoadFromGameDir(SpellDbTests.GameDir);
    }

    private static readonly DateTime T0 = new(2026, 8, 15, 12, 0, 0);

    [Fact]
    public void Sow_cast_and_land_creates_capped_timer_then_expires()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        t.OnEvent(new CastStartEvent("", "Spirit of Wolf", true) { Ts = T0, Observer = "Doofus" });
        t.OnEvent(new LandOtherEvent("Symmetry", db.MatchLandsOnOther("Symmetry is surrounded by a brief lupine aura.")!.Value.Candidates)
            { Ts = T0.AddSeconds(3), Observer = "Doofus" });

        var snap = t.GetSnapshot(T0.AddSeconds(5));
        var sym = Assert.Single(snap.Characters, a => a.Display == "Symmetry");
        var buff = Assert.Single(sym.Timers);
        Assert.Equal("Spirit of Wolf", buff.Spell.Name);          // resolved via recent cast
        Assert.Equal("Doofus", buff.CasterDisplay);
        Assert.InRange(buff.RemainingSeconds(T0.AddSeconds(5)), 2100, 2160);

        // expired + grace -> gone
        var later = t.GetSnapshot(T0.AddSeconds(2170));
        Assert.DoesNotContain(later.Characters, a => a.Display == "Symmetry");
    }

    [Fact]
    public void Wear_off_of_target_ends_early()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        t.OnEvent(new CastStartEvent("", "Spirit of Wolf", true) { Ts = T0, Observer = "Doofus" });
        t.OnEvent(new LandOtherEvent("Symmetry", db.MatchLandsOnOther("Symmetry is surrounded by a brief lupine aura.")!.Value.Candidates)
            { Ts = T0.AddSeconds(3), Observer = "Doofus" });
        t.OnEvent(new WearOffOtherEvent("Spirit of Wolf", "Symmetry") { Ts = T0.AddSeconds(60), Observer = "Doofus" });

        var snap = t.GetSnapshot(T0.AddSeconds(61));
        Assert.DoesNotContain(snap.Characters, a => a.Display == "Symmetry");
    }

    [Fact]
    public void Dot_ticks_create_and_death_clears_mob_timers()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        t.OnEvent(new DotTickEvent("A Teir`Dal ranger", 25, "Denon's Disruptive Discord", "", false)
            { Ts = T0, Observer = "Doofus" });

        var snap = t.GetSnapshot(T0.AddSeconds(1));
        var mob = Assert.Single(snap.Mobs);
        Assert.Equal("Teir`Dal ranger", mob.Display);              // article stripped for display
        var dot = Assert.Single(mob.Timers);
        Assert.Equal("Doofus", dot.CasterDisplay);                 // your-dot attributed to observer

        t.OnEvent(new DeathEvent("a Teir`Dal ranger", "Doofus", false) { Ts = T0.AddSeconds(10), Observer = "Doofus" });
        Assert.Empty(t.GetSnapshot(T0.AddSeconds(11)).Mobs);       // case-insensitive article-stripped key match
    }

    [Fact]
    public void Zoning_drops_songs_keeps_long_buffs()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        // long buff on self
        t.OnEvent(new CastStartEvent("", "Spirit of Wolf", true) { Ts = T0, Observer = "Doofus" });
        t.OnEvent(new LandSelfEvent(db.GetByLandsOnYou("You feel the spirit of wolf enter you."))
            { Ts = T0.AddSeconds(3), Observer = "Doofus" });
        var before = t.GetSnapshot(T0.AddSeconds(4));
        Assert.Contains(before.Characters, a => a.Display == "Doofus");

        t.OnEvent(new ZoneEvent("The Feerrott") { Ts = T0.AddSeconds(30), Observer = "Doofus" });
        var after = t.GetSnapshot(T0.AddSeconds(31));
        var doofus = Assert.Single(after.Characters, a => a.Display == "Doofus");
        Assert.Contains(doofus.Timers, i => i.Spell.Name == "Spirit of Wolf");
    }

    [Fact]
    public void Npc_self_buffs_go_to_mob_panel_and_clear_on_zone()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        // a mob receives a beneficial buff (lupine aura = SoW-family) - must NOT hit the party panel
        t.OnEvent(new LandOtherEvent("a tal ghoul wizard",
                db.MatchLandsOnOther("a tal ghoul wizard is surrounded by a brief lupine aura.")!.Value.Candidates)
            { Ts = T0, Observer = "Doofus" });

        var snap = t.GetSnapshot(T0.AddSeconds(1));
        Assert.Empty(snap.Characters);
        Assert.Single(snap.Mobs);

        // observer zones (death respawn included) -> their mob view clears
        t.OnEvent(new ZoneEvent("The Hole") { Ts = T0.AddSeconds(30), Observer = "Doofus" });
        Assert.Empty(t.GetSnapshot(T0.AddSeconds(31)).Mobs);
    }

    [Fact]
    public void Caster_death_purges_its_debuffs_from_players()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db);
        // a mob DoTs the player (player-target tick -> char-store debuff with mob as caster)
        t.OnEvent(new DotTickEvent("", 33, "Engulfing Darkness", "a ghoul ritualist", false)
            { Ts = T0, Observer = "Bezerkher" });
        var snap = t.GetSnapshot(T0.AddSeconds(1));
        Assert.Contains(snap.Characters, a => a.Display == "Bezerkher" &&
            a.Timers.Any(i => i.Spell.Name == "Engulfing Darkness"));

        // the mob dies -> its debuff fades from the player (server rule)
        t.OnEvent(new DeathEvent("a ghoul ritualist", "Bezerkher", false) { Ts = T0.AddSeconds(5), Observer = "Bezerkher" });
        var after = t.GetSnapshot(T0.AddSeconds(6));
        Assert.DoesNotContain(after.Characters, a =>
            a.Timers.Any(i => i.Spell.Name == "Engulfing Darkness"));
    }

    [Fact]
    public void Beneficial_extension_percent_lengthens_buffs()
    {
        var db = Db(); if (db is null) return;
        var t = new Tracker(db) { ExtendBeneficialPercent = 20 };
        t.OnEvent(new CastStartEvent("", "Spirit of Wolf", true) { Ts = T0, Observer = "Doofus" });
        t.OnEvent(new LandSelfEvent(db.GetByLandsOnYou("You feel the spirit of wolf enter you."))
            { Ts = T0, Observer = "Doofus" });
        var buff = t.GetSnapshot(T0.AddSeconds(1)).Characters.Single(a => a.Display == "Doofus").Timers.Single();
        Assert.InRange(buff.RemainingSeconds(T0.AddSeconds(1)), 2160 * 1.2 - 5, 2160 * 1.2);
    }

    [Fact]
    public void Vital_buff_classification_from_effect_slots()
    {
        var db = Db(); if (db is null) return;
        var sow = db.GetById(278)!;
        Assert.False(sow.IsVitalBuff);                      // movement speed - not vital
        var npcHaste = db.GetById(998);
        if (npcHaste is not null) Assert.True(npcHaste.HasHaste);
        // the game has plenty of regen/HoT buffs - classification must find a healthy number
        var vitals = db.All.Count(s => s.IsVitalBuff && s.PlayerCastable);
        Assert.True(vitals > 50, $"only {vitals} vital buffs classified");

        // damage shields (SPA 59) are vital - user report: Shield of Spikes must leave the aggregate
        var spikes = db.GetBestByName("Shield of Spikes");
        if (spikes is not null)
        {
            Assert.True(spikes.HasDamageShield, "Shield of Spikes not detected as a damage shield");
            Assert.True(spikes.IsVitalBuff);
        }
        var dsCount = db.All.Count(s => s.HasDamageShield && s.PlayerCastable);
        Assert.True(dsCount > 10, $"only {dsCount} damage shields classified");
    }

    [Fact]
    public void Full_fixture_replay_produces_state_without_errors()
    {
        var db = Db(); if (db is null) return;
        var (fixture, isLocal) = LineParserTests.ResolveReplayFixture();
        if (fixture is null) return;
        var observer = isLocal ? "Doofus" : "Tanko";   // log-owner identity comes from the filename
        var snapshotEvery = isLocal ? 2000 : 10;       // sample is ~250 lines - sample state often

        var parser = new LineParser(db);
        var tracker = new Tracker(db);
        var reader = new LogLineReader();
        var maxCharTimers = 0; var maxMobTimers = 0;
        DateTime last = default;
        var n = 0;
        foreach (var raw in File.ReadLines(fixture))
        {
            if (!reader.TryParse(raw, out var ts, out var action)) continue;
            last = ts;
            var e = parser.Parse(action);
            if (e is null) continue;
            tracker.OnEvent(e with { Ts = ts, Observer = observer });
            if (++n % snapshotEvery == 0)
            {
                var snap = tracker.GetSnapshot(ts);
                maxCharTimers = Math.Max(maxCharTimers, snap.Characters.Sum(c => c.Timers.Count));
                maxMobTimers = Math.Max(maxMobTimers, snap.Mobs.Sum(m => m.Timers.Count));
            }
        }
        var final = tracker.GetSnapshot(last);
        Assert.True(maxCharTimers > 0, "no character buffs ever tracked");
        Assert.True(maxMobTimers > 0, "no mob timers ever tracked");
    }
}
