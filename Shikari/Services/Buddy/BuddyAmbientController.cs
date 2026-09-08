using System;

namespace Shikari.Services.Buddy;

public readonly record struct BuddyAmbientContext(bool Enabled, uint PlayerId, uint TerritoryId, bool StatusKnown,
    bool Away, bool InDuty, bool InCombat, bool Dead, bool Suppressed);

public sealed class BuddyAmbientController
{
    private static readonly TimeSpan WelcomeDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ContinuityLimit = TimeSpan.FromSeconds(5);
    private BuddyAmbientContext previous;
    private DateTime lastUpdate;
    private bool initialized;
    private bool awayArmed;

    public BuddyAmbientPresentation Presentation { get; private set; } = new(BuddyAmbientState.Idle, DateTime.MinValue);

    /// <summary>Consumes fresh game snapshots. Cosmetic state never changes tactical calls or assignments.</summary>
    public void Update(BuddyAmbientContext next, DateTime now)
    {
        var continuous = initialized && now >= lastUpdate && now - lastUpdate <= ContinuityLimit &&
            previous.Enabled == next.Enabled && previous.PlayerId == next.PlayerId && previous.TerritoryId == next.TerritoryId;
        var canWelcome = continuous && CanReact(previous) && CanReact(next);
        // An Away bit can linger through a zone/player replacement or enable. Sleeping is
        // harmless, but greeting needs a complete active→Away→active cycle in this generation.
        if (!canWelcome) awayArmed = false;
        else if (next.Away && !previous.Away) awayArmed = true;
        var returned = canWelcome && awayArmed && previous.Away && !next.Away;
        if (!next.Away) awayArmed = false;
        var welcoming = canWelcome && !next.Away && Presentation.State == BuddyAmbientState.Welcoming &&
            now - Presentation.StartedAtUtc < WelcomeDuration;

        var state = !next.Enabled || next.PlayerId == 0 || next.Suppressed || next.Dead ? BuddyAmbientState.Idle :
            next.InCombat ? BuddyAmbientState.Focused :
            next.StatusKnown && next.Away ? BuddyAmbientState.Sleeping :
            returned || welcoming ? BuddyAmbientState.Welcoming :
            next.InDuty ? BuddyAmbientState.Focused : BuddyAmbientState.Idle;
        if (!continuous || state != Presentation.State) Presentation = new(state, now);

        // Suppressed transitions are consumed here; they are never queued for a later greeting.
        previous = next;
        lastUpdate = now;
        initialized = true;
    }

    public void Reset(DateTime now)
    {
        initialized = false;
        awayArmed = false;
        previous = default;
        lastUpdate = now;
        Presentation = new(BuddyAmbientState.Idle, now);
    }

    private static bool CanReact(BuddyAmbientContext value) => value.Enabled && value.PlayerId != 0 &&
        value.StatusKnown && !value.Suppressed && !value.Dead && !value.InCombat;
}
