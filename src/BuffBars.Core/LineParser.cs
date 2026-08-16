namespace BuffBars.Core;

/// <summary>
/// Turns a log action (text after the timestamp) into a LogEvent. Routing is done with cheap
/// prefix/contains checks ordered by real-world frequency (measured on 162MB of EQL logs);
/// spell-database landing/wear-off lookups run only when nothing structural matched.
/// All message shapes verified against real logs - see plans/buff-bars-plan.md.
/// </summary>
public sealed class LineParser
{
    private readonly SpellDb _db;

    public LineParser(SpellDb db) => _db = db;

    public LogEvent? Parse(string action)
    {
        if (action.Length < 8) return null;

        // chat filter first: every player chat line quotes text as `, '...` - combat lines never do.
        if (action.Contains(", '", StringComparison.Ordinal)) return null;

        // strip trailing modifier "(Critical)" etc. appended AFTER the period
        var critical = false;
        if (action.EndsWith(")", StringComparison.Ordinal))
        {
            var open = action.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0)
            {
                var mod = action[(open + 2)..^1];
                if (mod is "Critical" or "Lucky Critical" or "Twincast")
                {
                    critical = true;
                    action = action[..open];
                }
            }
        }

        return ParseCasting(action)
            ?? ParseDamageTaken(action, critical)
            ?? ParseWearOffExplicit(action)
            ?? ParseHeal(action)
            ?? ParseDeath(action)
            ?? ParseMisc(action)
            ?? ParseSpellText(action);
    }

    private LogEvent? ParseCasting(string a)
    {
        if (a.StartsWith("You begin casting ", StringComparison.Ordinal))
            return new CastStartEvent("", TrimDot(a[18..]), IsSelf: true);
        if (a.StartsWith("You begin singing ", StringComparison.Ordinal))
            return new CastStartEvent("", TrimDot(a[18..]), IsSelf: true);

        var idx = a.IndexOf(" begins casting ", StringComparison.Ordinal);
        if (idx < 0) idx = a.IndexOf(" begins singing ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var caster = a[..idx];
            var spell = TrimDot(a[(idx + 16)..]);
            return new CastStartEvent(caster, spell, IsSelf: false);
        }

        if (a.StartsWith("Your ", StringComparison.Ordinal))
        {
            if (a.EndsWith(" spell fizzles!", StringComparison.Ordinal))
                return new CastFizzleEvent(a[5..^15]);
            if (a.EndsWith(" spell is interrupted.", StringComparison.Ordinal))
                return new CastInterruptEvent("", a[5..^22]);
        }
        else if (a.EndsWith(" spell is interrupted.", StringComparison.Ordinal))
        {
            // "<caster>'s <spell> spell is interrupted."
            var body = a[..^22];
            var poss = body.IndexOf("'s ", StringComparison.Ordinal);
            if (poss > 0)
                return new CastInterruptEvent(body[..poss], body[(poss + 3)..]);
        }
        return null;
    }

    private LogEvent? ParseDamageTaken(string a, bool critical)
    {
        // DoT ticks: "<T> has taken <N> damage from your <S>." / "from <S> by <C>." / "damage by <S>."
        var taken = a.IndexOf(" has taken ", StringComparison.Ordinal);
        string target;
        int rest;
        if (taken > 0) { target = a[..taken]; rest = taken + 11; }
        else if (a.StartsWith("You have taken ", StringComparison.Ordinal)) { target = ""; rest = 15; }
        else return null;

        if (a.AsSpan(rest).StartsWith("an extra ", StringComparison.Ordinal)) return null; // bane, v1 skip

        var span = a.AsSpan(rest);
        var sp = span.IndexOf(' ');
        if (sp <= 0 || !int.TryParse(span[..sp], out var dmg)) return null;
        var tail = a[(rest + sp + 1)..];

        if (tail.StartsWith("damage from your ", StringComparison.Ordinal))
            return new DotTickEvent(target, dmg, TrimDot(tail[17..]), Caster: "", Critical: critical) { };
        if (tail.StartsWith("damage from ", StringComparison.Ordinal))
        {
            var body = TrimDot(tail[12..]);
            var by = body.LastIndexOf(" by ", StringComparison.Ordinal);
            if (by > 0)
                return new DotTickEvent(target, dmg, body[..by], body[(by + 4)..], critical);
            return new DotTickEvent(target, dmg, body, "", critical);
        }
        if (tail.StartsWith("damage by ", StringComparison.Ordinal))
            return new DotTickEvent(target, dmg, TrimDot(tail[10..]), "", critical);
        return null;
    }

    private LogEvent? ParseWearOffExplicit(string a)
    {
        if (a.StartsWith("Your pet's ", StringComparison.Ordinal) &&
            a.EndsWith(" spell has worn off.", StringComparison.Ordinal))
            return new WearOffPetEvent(a[11..^20]);

        if (a.StartsWith("Your ", StringComparison.Ordinal))
        {
            var idx = a.IndexOf(" spell has worn off of ", StringComparison.Ordinal);
            if (idx > 5)
                return new WearOffOtherEvent(a[5..idx], TrimDot(a[(idx + 23)..]));

            // stacking block: "Your X spell did not take hold( on T). (Blocked by B.)"
            var hold = a.IndexOf(" spell did not take hold", StringComparison.Ordinal);
            if (hold > 5)
            {
                var spell = a[5..hold];
                var target = "";
                var onIdx = a.IndexOf(" on ", hold, StringComparison.Ordinal);
                if (onIdx > 0)
                {
                    var end = a.IndexOf('.', onIdx);
                    if (end > onIdx) target = a[(onIdx + 4)..end];
                }
                var blocker = "";
                var blk = a.IndexOf("(Blocked by ", StringComparison.Ordinal);
                if (blk > 0)
                {
                    var end = a.IndexOf('.', blk);
                    if (end > blk) blocker = a[(blk + 12)..end];
                }
                return new BlockedEvent(spell, target, blocker);
            }
        }
        return null;
    }

    private LogEvent? ParseHeal(string a)
    {
        var by = a.LastIndexOf(" hit points by ", StringComparison.Ordinal);
        if (by < 0) return null;
        var spell = TrimDot(a[(by + 15)..]);

        var healed = a.IndexOf(" healed ", StringComparison.Ordinal);
        string caster, rest;
        if (a.StartsWith("You healed ", StringComparison.Ordinal)) { caster = ""; rest = a[11..by]; }
        else if (healed > 0) { caster = a[..healed]; rest = a[(healed + 8)..by]; }
        else return null;

        var isHot = false;
        var forIdx = rest.IndexOf(" over time for ", StringComparison.Ordinal);
        if (forIdx >= 0) isHot = true;
        else forIdx = rest.IndexOf(" for ", StringComparison.Ordinal);
        if (forIdx < 0) return null;
        var target = rest[..forIdx];
        var amountPart = rest[(forIdx + (isHot ? 15 : 5))..];
        // overheal renders "37 (61)" - take the first number (actual)
        var sp = amountPart.IndexOf(' ');
        var numText = sp > 0 ? amountPart[..sp] : amountPart;
        if (!int.TryParse(numText, out var amount)) return null;
        if (target is "himself" or "herself" or "itself") target = caster;
        return new HealEvent(caster, target, amount, spell, isHot);
    }

    private LogEvent? ParseDeath(string a)
    {
        if (a.EndsWith("!", StringComparison.Ordinal))
        {
            var slain = a.IndexOf(" has been slain by ", StringComparison.Ordinal);
            if (slain > 0)
                return new DeathEvent(a[..slain], a[(slain + 19)..^1], IsObserverDeath: false);
            if (a.StartsWith("You have slain ", StringComparison.Ordinal))
                return new DeathEvent(a[15..^1], "", IsObserverDeath: false);
            if (a.StartsWith("You have been slain by ", StringComparison.Ordinal))
                return new DeathEvent("", a[23..^1], IsObserverDeath: true);
        }
        else if (a.EndsWith(" died.", StringComparison.Ordinal))
        {
            return new DeathEvent(a[..^6], "", IsObserverDeath: false);
        }
        return null;
    }

    private LogEvent? ParseMisc(string a)
    {
        if (a.StartsWith("You have entered ", StringComparison.Ordinal) && a.EndsWith(".", StringComparison.Ordinal))
            return new ZoneEvent(a[17..^1]);

        if (a.EndsWith(" has joined the group.", StringComparison.Ordinal))
            return new GroupEvent(a[..^22], Joined: true, IsSelf: false);
        if (a.EndsWith(" has left the group.", StringComparison.Ordinal))
            return new GroupEvent(a[..^20], Joined: false, IsSelf: false);
        if (a is "You have joined the group.")
            return new GroupEvent("", Joined: true, IsSelf: true);
        if (a is "You have been removed from the group.")
            return new GroupEvent("", Joined: false, IsSelf: true);

        if (a.StartsWith("You have gained a level! Welcome to level ", StringComparison.Ordinal))
        {
            var numText = TrimDot(a[42..]).TrimEnd('!');
            if (int.TryParse(numText, out var lvl)) return new LevelEvent(lvl);
        }

        if (a.StartsWith("Your ", StringComparison.Ordinal) &&
            a.EndsWith(" feels alive with power.", StringComparison.Ordinal))
        {
            var open = a.LastIndexOf(" (", StringComparison.Ordinal);
            var close = a.LastIndexOf(')');
            if (open > 0 && close > open)
                return new ItemProcEvent(a[5..open], a[(open + 2)..close]);
        }

        if (a.StartsWith("You activate ", StringComparison.Ordinal) && a.EndsWith(".", StringComparison.Ordinal))
            return new ActivateEvent("", a[13..^1], IsSelf: true);
        var act = a.IndexOf(" activates ", StringComparison.Ordinal);
        if (act > 0 && a.EndsWith(".", StringComparison.Ordinal))
            return new ActivateEvent(a[..act], a[(act + 11)..^1], IsSelf: false);

        var awake = a.IndexOf(" has been awakened by ", StringComparison.Ordinal);
        if (awake > 0 && a.EndsWith(".", StringComparison.Ordinal))
            return new MezBreakEvent(a[..awake], a[(awake + 22)..^1]);

        var resisted = a.IndexOf(" resisted your ", StringComparison.Ordinal);
        if (resisted > 0 && a.EndsWith("!", StringComparison.Ordinal))
            return new ResistEvent("", a[..resisted], a[(resisted + 15)..^1]);
        if (a.StartsWith("You resist ", StringComparison.Ordinal) && a.EndsWith("!", StringComparison.Ordinal))
        {
            var body = a[11..^1];
            var poss = body.IndexOf("'s ", StringComparison.Ordinal);
            if (poss > 0) return new ResistEvent(body[..poss], "", body[(poss + 3)..]);
        }
        return null;
    }

    /// <summary>Landing/wear-off emote lookups against the spell database (whole-line matches).</summary>
    private LogEvent? ParseSpellText(string a)
    {
        var landYou = _db.GetByLandsOnYou(a);
        if (landYou.Count > 0) return new LandSelfEvent(landYou);

        var wearSelf = _db.GetByWearOff(a);
        if (wearSelf.Count > 0) return new WearOffSelfEvent(wearSelf);

        var other = _db.MatchLandsOnOther(a);
        if (other is { } m) return new LandOtherEvent(m.Target, m.Candidates);

        return null;
    }

    private static string TrimDot(string s) =>
        s.EndsWith(".", StringComparison.Ordinal) ? s[..^1] : s;
}
