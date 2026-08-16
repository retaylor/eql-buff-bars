using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuffBars.Core;

public sealed class OverlayRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 330;
    public double Height { get; set; } = 480;
}

public sealed class AppConfig
{
    public string GameDir { get; set; } =
        @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends";
    public int BackfillMinutes { get; set; } = 90;
    /// <summary>Hide timers whose total duration is shorter than this (bard pulse spam).</summary>
    public int MinDurationSeconds { get; set; } = 18;
    public double Opacity { get; set; } = 0.92;
    public bool ShowDotPanel { get; set; } = true;
    /// <summary>Twisted bard songs churn every few seconds - hidden from the buff panel by default.</summary>
    public bool ShowBardSongs { get; set; } = false;
    /// <summary>
    /// Extends every beneficial duration by this percent - the knob for buff-extension AAs
    /// (e.g. 15 for a +15% Spell Casting Reinforcement-style focus). 0 = client base durations.
    /// </summary>
    public int ExtendBeneficialPercent { get; set; } = 0;
    /// <summary>Per-spell absolute duration overrides in seconds, e.g. {"Chloroplast": 420}.</summary>
    public Dictionary<string, int> DurationOverridesSeconds { get; set; } = new();
    /// <summary>Collapse long package buffs (the Quick Buff AA blast) into one aggregate row.</summary>
    public bool GroupQuickBuffs { get; set; } = true;
    /// <summary>Buffs at least this long (base) are considered part of the Quick Buff package.
    /// 20 minutes keeps mid-length buffs visible individually - the aggregate is for the
    /// long-term stat package.</summary>
    public int QuickBuffMinDurationSeconds { get; set; } = 1200;
    /// <summary>Per-character Spell Casting Reinforcement overrides, e.g. {"Doofus": 50}.</summary>
    public Dictionary<string, int> ExtendBeneficialPercentByCharacter { get; set; } = new();
    /// <summary>Quick Buff AA cooldown on this server.</summary>
    public int QuickBuffCooldownSeconds { get; set; } = 300;
    /// <summary>Separate window for BENEFICIAL effects on enemies (dispel intel). Off by default.</summary>
    public bool ShowEnemyBuffPanel { get; set; } = false;
    public OverlayRect EnemyBuffWindow { get; set; } = new() { Left = 1860, Top = 210, Height = 360 };
    /// <summary>Dedicated crowd-control panel: mez/charm/root/fear timers on mobs, flat,
    /// soonest-break-first. The crowd-control view a chanter actually plays from.</summary>
    public bool ShowCcPanel { get; set; } = true;
    public OverlayRect CcWindow { get; set; } = new() { Left = 1115, Top = 60, Width = 330, Height = 230 };
    /// <summary>CC timers get their own panel; keep them out of the DoT panel to avoid double rows.</summary>
    public bool HideCcFromDotPanel { get; set; } = true;
    public OverlayRect BuffWindow { get; set; } = new() { Left = 2205, Top = 210 };
    public OverlayRect DotWindow { get; set; } = new() { Left = 2205, Top = 710, Height = 360 };

    [JsonIgnore]
    public string LogsDir => Path.Combine(GameDir, "Logs");

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqlBuffBars", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { /* corrupted config - fall through to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
