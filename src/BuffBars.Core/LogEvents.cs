namespace BuffBars.Core;

/// <summary>
/// A parsed, tracker-relevant log event. Ts = log timestamp; Observer = the character whose
/// log produced the line (from the eqlog filename), i.e. "You" in the message text.
/// </summary>
public abstract record LogEvent
{
    public DateTime Ts { get; init; }
    public string Observer { get; init; } = "";
}

/// <summary>"You begin casting X." / "&lt;Caster&gt; begins casting X." (also singing).</summary>
public sealed record CastStartEvent(string Caster, string SpellName, bool IsSelf) : LogEvent;

public sealed record CastFizzleEvent(string SpellName) : LogEvent;

/// <summary>Caster empty = your own cast interrupted.</summary>
public sealed record CastInterruptEvent(string Caster, string SpellName) : LogEvent;

/// <summary>A lands-on-you text matched the whole line; candidates share that text.</summary>
public sealed record LandSelfEvent(IReadOnlyList<Spell> Candidates) : LogEvent;

/// <summary>A lands-on-other suffix matched; Target is the residual prefix name.</summary>
public sealed record LandOtherEvent(string Target, IReadOnlyList<Spell> Candidates) : LogEvent;

/// <summary>"Your &lt;Spell&gt; spell has worn off of &lt;Target&gt;." - names the spell explicitly.</summary>
public sealed record WearOffOtherEvent(string SpellName, string Target) : LogEvent;

/// <summary>"Your pet's &lt;Spell&gt; spell has worn off."</summary>
public sealed record WearOffPetEvent(string SpellName) : LogEvent;

/// <summary>A per-spell wear-off emote matched the whole line (self buff expired).</summary>
public sealed record WearOffSelfEvent(IReadOnlyList<Spell> Candidates) : LogEvent;

/// <summary>DoT tick. Caster empty when unknown ("damage by Spell." casterless form).</summary>
public sealed record DotTickEvent(string Target, int Damage, string SpellName, string Caster, bool Critical) : LogEvent;

/// <summary>Heal line; IsHot = "over time" tick (a HoT heartbeat).</summary>
public sealed record HealEvent(string Caster, string Target, int Amount, string SpellName, bool IsHot) : LogEvent;

/// <summary>"Your X spell did not take hold (on T). (Blocked by B.)" - stacking block, no state change.</summary>
public sealed record BlockedEvent(string SpellName, string Target, string Blocker) : LogEvent;

public sealed record ResistEvent(string Caster, string Target, string SpellName) : LogEvent;

/// <summary>Killer empty when unknown. IsObserverDeath = the log's own character died.</summary>
public sealed record DeathEvent(string Victim, string Killer, bool IsObserverDeath) : LogEvent;

public sealed record ZoneEvent(string ZoneName) : LogEvent;

public sealed record GroupEvent(string Name, bool Joined, bool IsSelf) : LogEvent;

public sealed record LevelEvent(int Level) : LogEvent;

/// <summary>"Your &lt;Item&gt; (&lt;Spell&gt;) feels alive with power." - item proc buff on self.</summary>
public sealed record ItemProcEvent(string ItemName, string SpellName) : LogEvent;

public sealed record MezBreakEvent(string Target, string Waker) : LogEvent;

/// <summary>"You activate X." / "&lt;Name&gt; activates X." - AA / discipline activations.</summary>
public sealed record ActivateEvent(string Actor, string AbilityName, bool IsSelf) : LogEvent;
