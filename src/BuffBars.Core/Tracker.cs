namespace BuffBars.Core;

/// <summary>A live buff/DoT timer on some actor.</summary>
public sealed class BuffInstance
{
    public required Spell Spell { get; init; }
    public required string TargetKey { get; init; }
    public required string TargetDisplay { get; init; }
    public string CasterDisplay { get; set; } = "";
    public string CasterKey { get; set; } = "";
    /// <summary>Which log character's events created this instance (for zone-scoped cleanup).</summary>
    public string CreatorObserver { get; set; } = "";
    public DateTime Start { get; set; }
    /// <summary>Expected end (DB duration); null = effectively permanent.</summary>
    public DateTime? End { get; set; }
    public DateTime LastHeartbeat { get; set; }

    public double RemainingSeconds(DateTime now) =>
        End is null ? double.PositiveInfinity : Math.Max(0, (End.Value - now).TotalSeconds);
}

public sealed record ActorView(string Display, IReadOnlyList<BuffInstance> Timers, DateTime? QuickBuffAt = null);
public sealed record TrackerSnapshot(IReadOnlyList<ActorView> Characters, IReadOnlyList<ActorView> Mobs);

/// <summary>
/// Event -> state. Duration timers are primary (there is NO generic self wear-off line in this
/// client); wear-off texts, deaths and zoning are corrections. Multi-log friendly: instances
/// are keyed so the same buff observed from two logs merges instead of duplicating.
/// </summary>
public sealed class Tracker
{
    private const int RecentCastWindowSeconds = 12;
    private const int MobStaleMinutes = 5;

    private readonly SpellDb _db;
    private readonly object _lock = new();
    /// <summary>Global beneficial-duration extension in percent (Spell Casting Reinforcement AA).</summary>
    public int ExtendBeneficialPercent { get; set; }
    /// <summary>Per-caster extension overrides (boxes with different AA ranks).</summary>
    public IReadOnlyDictionary<string, int> ExtensionOverridesByCaster { get; set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-spell absolute duration overrides in seconds (config-driven).</summary>
    public IReadOnlyDictionary<string, int> DurationOverridesSeconds { get; set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // (targetKey, spellId) -> instance for character buffs; mobs additionally key by caster
    private readonly Dictionary<(string Target, int SpellId), BuffInstance> _charBuffs = new();
    private readonly Dictionary<(string Target, int SpellId, string Caster), BuffInstance> _mobTimers = new();
    private readonly Dictionary<string, string> _displayNames = new();     // key -> best display
    private readonly Dictionary<string, int> _observerLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecentCast> _recentCasts = new();
    private readonly Dictionary<string, DateTime> _quickBuffAt = new();   // actor key -> last activation
    private readonly HashSet<string> _observers = new();                  // keys of our own log characters
    // long-term memory: every spell an actor has been SEEN casting (disambiguates Quick Buff
    // blasts, whose landings arrive with no cast lines - e.g. the shared regen-line text)
    private readonly Dictionary<string, HashSet<string>> _knownSpells = new();

    private readonly record struct RecentCast(DateTime Ts, string CasterKey, string CasterDisplay, string SpellNameLower);

    public Tracker(SpellDb db) => _db = db;

    public void OnEvent(LogEvent e)
    {
        lock (_lock)
        {
            if (e.Observer.Length > 0) _observers.Add(Names.Key(e.Observer));
            switch (e)
            {
                case CastStartEvent c: HandleCast(c); break;
                case CastInterruptEvent i: RemoveRecentCast(i.Caster, i.SpellName, i.Observer); break;
                case CastFizzleEvent f: RemoveRecentCast("", f.SpellName, f.Observer); break;
                case LandSelfEvent l: HandleLand(l.Observer, l.Candidates, l); break;
                case LandOtherEvent l: HandleLand(l.Target, l.Candidates, l); break;
                case ItemProcEvent p: HandleItemProc(p); break;
                case WearOffOtherEvent w: EndByName(w.SpellName, w.Target, w.Ts); break;
                case WearOffSelfEvent w: EndSelfByCandidates(w); break;
                case DotTickEvent d: HandleDotTick(d); break;
                case HealEvent h when h.IsHot: HandleHotTick(h); break;
                case DeathEvent d: HandleDeath(d); break;
                case ZoneEvent z: HandleZone(z); break;
                case LevelEvent lv: _observerLevels[lv.Observer] = lv.Level; break;
                case GroupEvent g: HandleGroup(g); break;
                case ActivateEvent a when a.AbilityName.Equals("Quick Buff", StringComparison.OrdinalIgnoreCase):
                    _quickBuffAt[Names.Key(a.IsSelf ? a.Observer : a.Actor)] = a.Ts;
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- handlers

    private void HandleCast(CastStartEvent c)
    {
        var display = c.IsSelf ? c.Observer : c.Caster;
        var key = Names.Key(display);
        var nameLower = c.SpellName.ToLowerInvariant();
        PruneRecentCasts(c.Ts);
        _recentCasts.Add(new RecentCast(c.Ts, key, display, nameLower));
        if (!_knownSpells.TryGetValue(key, out var known)) _knownSpells[key] = known = new HashSet<string>();
        known.Add(nameLower);
    }

    private void RemoveRecentCast(string caster, string spellName, string observer)
    {
        var display = caster.Length == 0 ? observer : caster;
        var key = Names.Key(display);
        var nameLower = spellName.ToLowerInvariant();
        for (var i = _recentCasts.Count - 1; i >= 0; i--)
        {
            if (_recentCasts[i].CasterKey == key && _recentCasts[i].SpellNameLower == nameLower)
            {
                _recentCasts.RemoveAt(i);
                return;
            }
        }
    }

    private void HandleLand(string targetDisplay, IReadOnlyList<Spell> candidates, LogEvent e)
    {
        var (spell, casterDisplay) = Resolve(candidates, e.Ts);
        if (spell is null || spell.DurationTicks <= 0) return;

        // route by TARGET, not by spell polarity: NPCs buff themselves constantly (Skin like
        // Rock etc.) and those belong with the mob timers, which zone-clear - not in the party panel
        if (Names.IsLikelyPlayer(targetDisplay))
            ApplyCharBuff(targetDisplay, spell, casterDisplay, e);
        else
            ApplyMobTimer(targetDisplay, spell, casterDisplay, e);
    }

    private void HandleItemProc(ItemProcEvent p)
    {
        var spell = _db.GetBestByName(p.SpellName);
        if (spell is null || spell.DurationTicks <= 0) return;
        ApplyCharBuff(p.Observer, spell, p.Observer, p);
    }

    private void HandleDotTick(DotTickEvent d)
    {
        var spell = _db.GetBestByName(d.SpellName);
        if (spell is null) return;
        var targetDisplay = d.Target.Length == 0 ? d.Observer : d.Target;
        var casterDisplay = d.Caster.Length == 0 ? d.Observer : d.Caster;

        // a DoT ticking on a PLAYER is a character debuff, not a mob timer
        if (d.Target.Length == 0 || Names.IsLikelyPlayer(targetDisplay))
        {
            var charKey = (Names.Key(targetDisplay), spell.Id);
            if (_charBuffs.TryGetValue(charKey, out var charInst))
            {
                charInst.LastHeartbeat = d.Ts;
                if (charInst.End is { } cend && cend <= d.Ts) charInst.End = d.Ts.AddSeconds(18);
            }
            else
            {
                _charBuffs[charKey] = NewInstance(targetDisplay, spell, casterDisplay, d.Ts, d.Observer);
            }
            RememberDisplay(targetDisplay);
            return;
        }

        var key = (Names.Key(targetDisplay), spell.Id, Names.Key(casterDisplay));
        if (_mobTimers.TryGetValue(key, out var inst))
        {
            inst.LastHeartbeat = d.Ts;
            // still ticking past its expected end: duration was underestimated - extend a little
            if (inst.End is { } end && end <= d.Ts) inst.End = d.Ts.AddSeconds(18);
        }
        else
        {
            _mobTimers[key] = NewInstance(targetDisplay, spell, casterDisplay, d.Ts, d.Observer);
        }
        RememberDisplay(targetDisplay);
    }

    private void HandleHotTick(HealEvent h)
    {
        var spell = _db.GetBestByName(h.SpellName);
        if (spell is null || spell.DurationTicks <= 0) return;
        var caster = h.Caster.Length == 0 ? h.Observer : h.Caster;
        var targetKey = Names.Key(h.Target);
        if (_charBuffs.TryGetValue((targetKey, spell.Id), out var inst))
        {
            inst.LastHeartbeat = h.Ts;
            if (inst.End is { } end && end <= h.Ts) inst.End = h.Ts.AddSeconds(18);
        }
        else
        {
            _charBuffs[(targetKey, spell.Id)] = NewInstance(h.Target, spell, caster, h.Ts, h.Observer);
        }
        ReconcileLandingFamily(targetKey, spell);
        RememberDisplay(h.Target);
    }

    /// <summary>
    /// A tick line names its spell EXACTLY, unlike landing emotes which are shared across a
    /// spell line ("You begin to regenerate." = Regeneration/Chloroplast/Pack variants).
    /// When the exact spell is known, evict same-family instances the ambiguous landing
    /// resolver may have guessed wrong (the Pack Regeneration phantom).
    /// </summary>
    private void ReconcileLandingFamily(string targetKey, Spell known)
    {
        if (known.LandsOnYou.Length == 0 && known.LandsOnOther.Length == 0) return;
        foreach (var kv in _charBuffs.Where(kv =>
                     kv.Key.Target == targetKey &&
                     kv.Key.SpellId != known.Id &&
                     SameLandingFamily(kv.Value.Spell, known)).ToList())
            _charBuffs.Remove(kv.Key);
    }

    private static bool SameLandingFamily(Spell a, Spell b) =>
        (a.LandsOnYou.Length > 0 && a.LandsOnYou == b.LandsOnYou) ||
        (a.LandsOnOther.Length > 0 && a.LandsOnOther == b.LandsOnOther);

    private void HandleDeath(DeathEvent d)
    {
        var victimKey = d.IsObserverDeath ? Names.Key(d.Observer) : Names.Key(d.Victim);
        if (victimKey.Length == 0) return;
        ClearActor(victimKey);

        // server rule: detrimental effects fade when their caster dies - purge the dead
        // actor's debuffs from every player and mob it had afflicted
        foreach (var kv in _charBuffs.Where(kv =>
                     kv.Value.CasterKey == victimKey && !kv.Value.Spell.Beneficial).ToList())
            _charBuffs.Remove(kv.Key);
        foreach (var kv in _mobTimers.Where(kv => kv.Key.Caster == victimKey).ToList())
            _mobTimers.Remove(kv.Key);
    }

    private void HandleGroup(GroupEvent g)
    {
        // every log character we tail is an "observer" - their own buffs are always real
        _observers.Add(Names.Key(g.Observer));
        if (g.IsSelf)
        {
            // our own group changed (joined new / left / removed): stale strangers and old
            // groupmates no longer belong on the panel; our own characters' buffs persist
            foreach (var kv in _charBuffs.Where(kv => !_observers.Contains(kv.Key.Target)).ToList())
                _charBuffs.Remove(kv.Key);
        }
        else if (!g.Joined && g.Name.Length > 0)
        {
            // a named member left: drop their panel right away
            var key = Names.Key(g.Name);
            if (!_observers.Contains(key))
            {
                foreach (var kv in _charBuffs.Where(kv => kv.Key.Target == key).ToList())
                    _charBuffs.Remove(kv.Key);
            }
        }
    }

    /// <summary>Manual wipe: all buff and DoT panels. Learned spell sets, levels and Quick
    /// Buff cooldowns are kept - they stay true regardless of who is nearby.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _charBuffs.Clear();
            _mobTimers.Clear();
            _recentCasts.Clear();
        }
    }

    private void HandleZone(ZoneEvent z)
    {
        var observerKey = Names.Key(z.Observer);
        // bard-song-style short buffs drop on zoning; long buffs persist
        foreach (var kv in _charBuffs.Where(kv =>
                     kv.Key.Target == observerKey && IsSongWindow(kv.Value.Spell)).ToList())
            _charBuffs.Remove(kv.Key);
        // this observer's view of mobs is gone
        foreach (var kv in _mobTimers.Where(kv => kv.Value.CreatorObserver == z.Observer).ToList())
            _mobTimers.Remove(kv.Key);
    }

    private static bool IsSongWindow(Spell s) =>
        s.ClassLevels[7] < 255 && s.BaseDurationSeconds <= 120;   // index 7 = BRD

    // ---------------------------------------------------------------- helpers

    /// <summary>Pick the spell that was most recently cast among the candidates; fall back sanely.</summary>
    private (Spell?, string) Resolve(IReadOnlyList<Spell> candidates, DateTime ts)
    {
        PruneRecentCasts(ts);
        for (var i = _recentCasts.Count - 1; i >= 0; i--)
        {
            var rc = _recentCasts[i];
            foreach (var s in candidates)
            {
                if (string.Equals(s.Name, rc.SpellNameLower, StringComparison.OrdinalIgnoreCase))
                    return (s, rc.CasterDisplay);
            }
        }

        // Quick Buff blasts land without cast lines: prefer the candidate the recent
        // ACTIVATOR has been seen casting at any point (their spell set)
        var qbCutoff = ts.AddSeconds(-RecentCastWindowSeconds);
        foreach (var (actorKey, at) in _quickBuffAt)
        {
            if (at < qbCutoff || at > ts) continue;
            if (!_knownSpells.TryGetValue(actorKey, out var known)) continue;
            foreach (var s in candidates)
            {
                if (known.Contains(s.Name.ToLowerInvariant()))
                    return (s, _displayNames.GetValueOrDefault(actorKey, ""));
            }
        }

        if (candidates.Count == 1) return (candidates[0], "");
        var best = candidates.FirstOrDefault(s => s.PlayerCastable && s.DurationTicks > 0)
                   ?? candidates.FirstOrDefault(s => s.DurationTicks > 0);
        return (best, "");
    }

    private void ApplyCharBuff(string targetDisplay, Spell spell, string casterDisplay, LogEvent e)
    {
        var targetKey = Names.Key(targetDisplay);
        var inst = NewInstance(targetDisplay, spell, casterDisplay, e.Ts, e.Observer);
        _charBuffs[(targetKey, spell.Id)] = inst;      // recast = refresh/replace
        ReconcileLandingFamily(targetKey, spell);      // evict mis-resolved same-family cousins
        RememberDisplay(targetDisplay);
    }

    private void ApplyMobTimer(string targetDisplay, Spell spell, string casterDisplay, LogEvent e)
    {
        var key = (Names.Key(targetDisplay), spell.Id, Names.Key(casterDisplay.Length == 0 ? e.Observer : casterDisplay));
        _mobTimers[key] = NewInstance(targetDisplay, spell, casterDisplay, e.Ts, e.Observer);
        RememberDisplay(targetDisplay);
    }

    private BuffInstance NewInstance(string targetDisplay, Spell spell, string casterDisplay, DateTime ts, string observer)
    {
        var level = _observerLevels.TryGetValue(casterDisplay, out var lvl) ? lvl : 0;
        var durSecs = spell.DurationSeconds(level);
        if (DurationOverridesSeconds.TryGetValue(spell.Name, out var overrideSecs))
        {
            durSecs = overrideSecs;
        }
        else if (spell.Beneficial && !spell.HasInvulnerability)   // SCR AA exempts invulnerability
        {
            var pct = ExtensionOverridesByCaster.TryGetValue(casterDisplay, out var o)
                ? o
                : ExtendBeneficialPercent;
            if (pct > 0) durSecs = (int)((long)durSecs * (100 + pct) / 100);
        }
        DateTime? end = durSecs >= int.MaxValue / 2 || spell.DurationFormula == 50
            ? null
            : ts.AddSeconds(durSecs);
        return new BuffInstance
        {
            Spell = spell,
            TargetKey = Names.Key(targetDisplay),
            TargetDisplay = Names.StripArticleForDisplay(targetDisplay),
            CasterDisplay = casterDisplay,
            CasterKey = Names.Key(casterDisplay),
            CreatorObserver = observer,
            Start = ts,
            End = end,
            LastHeartbeat = ts,
        };
    }

    private void EndByName(string spellName, string target, DateTime ts)
    {
        var targetKey = Names.Key(target);
        foreach (var kv in _charBuffs.Where(kv =>
                     kv.Key.Target == targetKey &&
                     string.Equals(kv.Value.Spell.Name, spellName, StringComparison.OrdinalIgnoreCase)).ToList())
            _charBuffs.Remove(kv.Key);
        foreach (var kv in _mobTimers.Where(kv =>
                     kv.Key.Target == targetKey &&
                     string.Equals(kv.Value.Spell.Name, spellName, StringComparison.OrdinalIgnoreCase)).ToList())
            _mobTimers.Remove(kv.Key);
    }

    private void EndSelfByCandidates(WearOffSelfEvent w)
    {
        var targetKey = Names.Key(w.Observer);
        foreach (var s in w.Candidates)
            _charBuffs.Remove((targetKey, s.Id));
    }

    private void ClearActor(string actorKey)
    {
        foreach (var kv in _charBuffs.Where(kv => kv.Key.Target == actorKey).ToList())
            _charBuffs.Remove(kv.Key);
        foreach (var kv in _mobTimers.Where(kv => kv.Key.Target == actorKey).ToList())
            _mobTimers.Remove(kv.Key);
    }

    private void PruneRecentCasts(DateTime now)
    {
        var cutoff = now.AddSeconds(-RecentCastWindowSeconds);
        _recentCasts.RemoveAll(rc => rc.Ts < cutoff);
    }

    private void RememberDisplay(string display) =>
        _displayNames[Names.Key(display)] = Names.StripArticleForDisplay(display);

    // ---------------------------------------------------------------- snapshot

    public TrackerSnapshot GetSnapshot(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            var chars = _charBuffs.Values
                .GroupBy(i => i.TargetKey)
                .Select(g => new ActorView(
                    _displayNames.GetValueOrDefault(g.Key, g.First().TargetDisplay),
                    g.OrderBy(i => i.RemainingSeconds(now)).ToList(),
                    _quickBuffAt.TryGetValue(g.Key, out var qb) ? qb : null))
                .OrderBy(a => a.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var mobs = _mobTimers.Values
                .GroupBy(i => i.TargetKey)
                .Select(g => new ActorView(
                    _displayNames.GetValueOrDefault(g.Key, g.First().TargetDisplay),
                    g.OrderBy(i => i.RemainingSeconds(now)).ToList()))
                .OrderBy(a => a.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new TrackerSnapshot(chars, mobs);
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var kv in _charBuffs.Where(kv => Expired(kv.Value, now)).ToList())
            _charBuffs.Remove(kv.Key);
        var mobCutoff = now.AddMinutes(-MobStaleMinutes);
        foreach (var kv in _mobTimers.Where(kv =>
                     Expired(kv.Value, now) || kv.Value.LastHeartbeat < mobCutoff).ToList())
            _mobTimers.Remove(kv.Key);
    }

    private static bool Expired(BuffInstance i, DateTime now) =>
        i.End is { } end && end.AddSeconds(2) < now;
}
