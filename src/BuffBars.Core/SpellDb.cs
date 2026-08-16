using System.Text;

namespace BuffBars.Core;

/// <summary>
/// Parses the EverQuest Legends client spell database (spells_us.txt, 173 caret fields, no
/// header + spells_us_str.txt, 7 fields with a #SPELLINDEX header) and builds the lookup
/// indexes the log parser needs. Field indices cross-validated against rumstil/eqspellparser
/// and empirical probing of the EQL client files (see plans/buff-bars-plan.md).
/// </summary>
public sealed class SpellDb
{
    public const int ExpectedFieldCount = 173;

    private readonly Dictionary<int, Spell> _byId = new();
    private readonly Dictionary<string, List<Spell>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Spell>> _byLandsOnYou = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Spell>> _byWearOff = new(StringComparer.Ordinal);
    // lands-on-other suffixes indexed by their last word for cheap candidate lookup
    private readonly Dictionary<string, List<Spell>> _otherByLastWord = new(StringComparer.Ordinal);

    public int Count => _byId.Count;
    public IReadOnlyCollection<Spell> All => _byId.Values;

    public static SpellDb LoadFromGameDir(string gameDir)
    {
        var spells = Path.Combine(gameDir, "spells_us.txt");
        var strings = Path.Combine(gameDir, "spells_us_str.txt");
        return Load(spells, strings);
    }

    public static SpellDb Load(string spellsPath, string stringsPath)
    {
        // pass 1: landing/wear-off strings by id
        var landYou = new Dictionary<int, string>();
        var landOther = new Dictionary<int, string>();
        var wearOff = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(stringsPath, Encoding.Latin1))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('^');
            if (f.Length < 6 || !int.TryParse(f[0], out var sid)) continue;
            if (f[3].Length > 0) landYou[sid] = f[3];
            if (f[4].Length > 0) landOther[sid] = f[4];
            if (f[5].Length > 0) wearOff[sid] = f[5];
        }

        var db = new SpellDb();
        foreach (var line in File.ReadLines(spellsPath, Encoding.Latin1))
        {
            if (line.Length == 0) continue;
            var f = line.Split('^');
            if (f.Length != ExpectedFieldCount)
                throw new InvalidDataException(
                    $"spells_us.txt field count changed: expected {ExpectedFieldCount}, got {f.Length}. " +
                    "A game patch likely altered the format - field indices must be re-verified.");
            if (!int.TryParse(f[0], out var id)) continue;

            var levels = new byte[16];
            for (var i = 0; i < 16; i++)
                levels[i] = byte.TryParse(f[36 + i], out var b) ? b : (byte)255;

            // effect slots live in the LAST field: "slot|spa|base|base2|calc|max" joined by '$'
            var hasRegen = false;
            var hasHaste = false;
            var hasDs = false;
            var hasInvuln = false;
            var hasMez = false;
            var hasCharm = false;
            var hasRoot = false;
            var hasFear = false;
            foreach (var slot in f[^1].Split('$'))
            {
                var parts = slot.Split('|');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[1], out var spa) || !int.TryParse(parts[2], out var baseVal)) continue;
                var maxVal = parts.Length > 5 && int.TryParse(parts[5], out var mv) ? mv : 0;
                // timed +HP = regen/HoT; some regens are level-scaled with base=0 but max>0 (Pack Regeneration)
                if (spa == 0 && baseVal >= 0 && (baseVal > 0 || maxVal > 0)) hasRegen = true;
                if (spa == 11 && baseVal >= 100) hasHaste = true;    // melee haste
                if (spa == 59 && baseVal != 0) hasDs = true;         // damage shield (base is NEGATIVE in this client: damage dealt to attacker)
                if (spa == 40) hasInvuln = true;                     // invulnerability - exempt from AA duration extension
                if (spa == 31) hasMez = true;                        // mesmerize (Dazzle 190)
                if (spa == 22) hasCharm = true;                      // charm (Beguile 182)
                if (spa == 99) hasRoot = true;                       // root (Root 230)
                if (spa == 23) hasFear = true;                       // fear (Fear 229)
            }

            var spell = new Spell
            {
                Id = id,
                Name = f[1],
                CastMs = ParseInt(f[8]),
                DurationFormula = ParseInt(f[11]),
                DurationTicks = ParseInt(f[12]),
                Beneficial = ParseInt(f[28]) > 0,
                ResistType = (byte)ParseInt(f[29]),
                TargetType = (byte)ParseInt(f[30]),
                Icon = ParseInt(f[75]),
                ClassLevels = levels,
                LandsOnYou = landYou.GetValueOrDefault(id, ""),
                LandsOnOther = landOther.GetValueOrDefault(id, ""),
                WearOff = wearOff.GetValueOrDefault(id, ""),
                HasRegen = hasRegen,
                HasHaste = hasHaste,
                HasDamageShield = hasDs,
                HasMez = hasMez,
                HasCharm = hasCharm,
                HasRoot = hasRoot,
                HasFear = hasFear,
                HasInvulnerability = hasInvuln,
            };
            spell.ComputePlayerCastable();
            db.Add(spell);
        }
        db.ComputeAmbiguity();
        return db;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    private void Add(Spell s)
    {
        _byId[s.Id] = s;
        AddTo(_byName, s.Name, s);
        if (s.LandsOnYou.Length > 0) AddTo(_byLandsOnYou, s.LandsOnYou, s);
        if (s.WearOff.Length > 0) AddTo(_byWearOff, s.WearOff, s);
        if (s.LandsOnOther.Length > 0)
        {
            var lastWord = LastWord(s.LandsOnOther);
            if (lastWord.Length > 0) AddTo(_otherByLastWord, lastWord, s);
        }
    }

    private static void AddTo(Dictionary<string, List<Spell>> map, string key, Spell s)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Spell>(1);
        list.Add(s);
    }

    private static string LastWord(string text)
    {
        var t = text.TrimEnd();
        var idx = t.LastIndexOf(' ');
        return idx < 0 ? t : t[(idx + 1)..];
    }

    private void ComputeAmbiguity()
    {
        foreach (var list in _byLandsOnYou.Values)
        {
            if (list.Count > 1) foreach (var s in list) s.AmbiguousYou = true;
        }
        var bySuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _byId.Values)
        {
            if (s.LandsOnOther.Length == 0) continue;
            bySuffix[s.LandsOnOther] = bySuffix.TryGetValue(s.LandsOnOther, out var n) ? n + 1 : 1;
        }
        foreach (var s in _byId.Values)
        {
            if (s.LandsOnOther.Length > 0 && bySuffix[s.LandsOnOther] > 1) s.AmbiguousOther = true;
        }
    }

    public Spell? GetById(int id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Spells by exact name. Multiple spells share names (NPC versions, ranks) - callers should
    /// prefer player-castable entries; this returns them first.
    /// </summary>
    public IReadOnlyList<Spell> GetByName(string name)
    {
        if (!_byName.TryGetValue(name, out var list)) return Array.Empty<Spell>();
        if (list.Count <= 1) return list;
        return list.OrderByDescending(s => s.PlayerCastable).ThenBy(s => s.Id).ToList();
    }

    /// <summary>Best single candidate by name: player-castable preferred, then lowest id.</summary>
    public Spell? GetBestByName(string name)
    {
        var list = GetByName(name);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Spells whose lands-on-you text equals the whole action line.</summary>
    public IReadOnlyList<Spell> GetByLandsOnYou(string action) =>
        _byLandsOnYou.TryGetValue(action, out var list) ? list : Array.Empty<Spell>();

    /// <summary>Spells whose wear-off text equals the whole action line.</summary>
    public IReadOnlyList<Spell> GetByWearOff(string action) =>
        _byWearOff.TryGetValue(action, out var list) ? list : Array.Empty<Spell>();

    /// <summary>
    /// Match a third-person landing line: finds spells whose LandsOnOther text is a suffix of
    /// the action; the residual prefix is the target's name. Longest suffix wins.
    /// </summary>
    public LandsOnOtherMatch? MatchLandsOnOther(string action)
    {
        var lastWord = LastWord(action);
        if (lastWord.Length == 0 || !_otherByLastWord.TryGetValue(lastWord, out var candidates))
            return null;

        List<Spell>? best = null;
        var bestLen = -1;
        foreach (var s in candidates)
        {
            var suffix = s.LandsOnOther;
            if (suffix.Length >= action.Length) continue;         // must leave room for a name
            if (!action.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (suffix.Length > bestLen)
            {
                bestLen = suffix.Length;
                best = new List<Spell> { s };
            }
            else if (suffix.Length == bestLen)
            {
                best!.Add(s);
            }
        }
        if (best is null) return null;

        var target = action[..(action.Length - bestLen)].TrimEnd();
        // suffixes that start with "'s" leave the bare name; ones starting with a space already do
        if (target.EndsWith("'s", StringComparison.Ordinal)) target = target[..^2];
        if (target.Length == 0) return null;
        return new LandsOnOtherMatch(target, best);
    }
}

public readonly record struct LandsOnOtherMatch(string Target, IReadOnlyList<Spell> Candidates);
