namespace BuffBars.Core;

/// <summary>One spell parsed from the client's spells_us.txt + spells_us_str.txt.</summary>
public sealed class Spell
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public int CastMs { get; init; }
    public int DurationFormula { get; init; }
    public int DurationTicks { get; init; }
    public bool Beneficial { get; init; }
    public byte ResistType { get; init; }
    public byte TargetType { get; init; }
    public int Icon { get; init; }
    /// <summary>Class levels WAR..BER (16 entries), 255 = unusable.</summary>
    public required byte[] ClassLevels { get; init; }
    public string LandsOnYou { get; init; } = "";
    /// <summary>Suffix form: target name is prepended by the client (may start with space or 's).</summary>
    public string LandsOnOther { get; init; } = "";
    public string WearOff { get; init; } = "";

    public bool PlayerCastable { get; private set; }
    /// <summary>True when another spell shares this one's LandsOnYou / LandsOnOther text.</summary>
    public bool AmbiguousYou { get; internal set; }
    public bool AmbiguousOther { get; internal set; }

    /// <summary>Base (max) duration in seconds: ticks * 6.</summary>
    public int BaseDurationSeconds => DurationTicks * 6;

    /// <summary>Duration in seconds at a given caster level (classic formula table, capped at base).</summary>
    public int DurationSeconds(int level = 0)
    {
        if (level <= 0) return BaseDurationSeconds;
        int ticks = DurationFormula switch
        {
            0 => 0,
            1 => Math.Max(1, (int)Math.Ceiling(level / 2.0)),
            2 => Math.Max(1, (int)Math.Ceiling(level * 0.6)),
            3 => level * 30,
            4 => 50,
            5 => 2,
            6 => Math.Max(1, (int)Math.Ceiling(level / 2.0)),
            7 => level,
            8 => level + 10,
            9 => 2 * level + 10,
            10 => 3 * level + 10,
            11 or 12 => DurationTicks,
            15 => DurationTicks,
            50 => int.MaxValue / 6,          // "permanent"
            3600 => DurationTicks > 0 ? DurationTicks : 3600,
            _ => DurationTicks,
        };
        if (DurationTicks > 0 && ticks > DurationTicks) ticks = DurationTicks;
        long secs = (long)ticks * 6;
        return secs > int.MaxValue ? int.MaxValue : (int)secs;
    }

    /// <summary>Buff-window candidate: beneficial with a real duration.</summary>
    public bool IsBuff => Beneficial && DurationTicks > 0;

    /// <summary>Bard-castable short-duration spell (twisted songs; index 7 = BRD).</summary>
    public bool IsBardSong => ClassLevels[7] < 255 && DurationTicks > 0 && BaseDurationSeconds <= 120;

    /// <summary>Timed positive HP-per-tick effect (regen / HoT) - from SPA 0 with positive base.</summary>
    public bool HasRegen { get; init; }
    /// <summary>Melee haste (SPA 11 with base >= 100).</summary>
    public bool HasHaste { get; init; }
    /// <summary>Damage shield (SPA 59; base is NEGATIVE in this client - damage dealt to the attacker).</summary>
    public bool HasDamageShield { get; init; }

    /// <summary>Critical buff (HoT/regen/haste/damage shield) - pinned + tinted in the overlay,
    /// and kept out of the Quick Buffs aggregate.</summary>
    public bool IsVitalBuff => Beneficial && DurationTicks > 0 && (HasRegen || HasHaste || HasDamageShield);

    /// <summary>DoT-window candidate: detrimental with a real duration.</summary>
    public bool IsDetrimentalTimed => !Beneficial && DurationTicks > 0;

    internal void ComputePlayerCastable()
    {
        foreach (var lvl in ClassLevels)
        {
            if (lvl < 255) { PlayerCastable = true; return; }
        }
        PlayerCastable = false;
    }
}
