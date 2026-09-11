using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Shikari.Services.Replay;

namespace Shikari.Services;

/// <summary>A boss cast that has just begun.</summary>
public sealed class CastEvent
{
    public uint ActionId { get; init; }
    public string ActionName { get; init; } = string.Empty;
    public string CasterName { get; init; } = string.Empty;
    public uint CasterEntityId { get; init; }

    /// <summary>How many times this action has been cast since the pull, counting from 1.</summary>
    public int Occurrence { get; init; }

    /// <summary>Full cast bar length in seconds.</summary>
    public float TotalCastTime { get; init; }

    /// <summary>Seconds since the pull at the moment the cast began.</summary>
    public float CombatTime { get; init; }

    public DateTime StartedAtUtc { get; init; }

    /// <summary>Actor geometry at first detection, which can be later than the reconstructed bar start.</summary>
    public CastStartContext? Context { get; init; }

    /// <summary>Expected end of the display bar, not an observed completion or effect.</summary>
    public DateTime ResolvesAtUtc => StartedAtUtc.AddSeconds(TotalCastTime);
}

/// <summary>
/// Watches the pull: when combat starts and ends, and every cast a hostile actor begins.
/// Occurrences are counted per action per pull, which is what lets a plan say
/// "the second Akh Morn" rather than a wall-clock time that drifts.
/// </summary>
public sealed class EncounterMonitor : IDisposable
{
    private sealed class TrackedCaster
    {
        public uint ActionId;
        public bool WasCasting;
    }

    // Not every frame: walking the object table allocates. Accuracy is unaffected because the
    // real start time comes from CurrentCastTime, not from when we happened to look.
    private const double ScanIntervalSeconds = 0.1;

    private readonly Dictionary<uint, TrackedCaster> tracked = new();
    private readonly Dictionary<uint, int> occurrences = new();
    private readonly List<CastEvent> recentCasts = new();

    /// <summary>Entity ids of everything that cast at us this pull — our candidates for "the boss".</summary>
    private readonly HashSet<uint> pullCasters = new();

    private DateTime lastScanUtc = DateTime.MinValue;
    private DateTime combatStartUtc;
    private bool inCombat;

    public EncounterMonitor()
    {
        Plugin.Framework.Update += OnUpdate;
        Plugin.DutyState.DutyWiped += OnDutyWiped;
        Plugin.DutyState.DutyRecommenced += OnDutyRecommenced;
    }

    /// <summary>True while the local player is flagged in combat.</summary>
    public bool InCombat => inCombat;

    /// <summary>Seconds since the pull started, or 0 when out of combat.</summary>
    public float CombatElapsed =>
        inCombat ? (float)(DateTime.UtcNow - combatStartUtc).TotalSeconds : 0f;

    /// <summary>Casts seen during this pull, newest last, capped to a readable number.</summary>
    public IReadOnlyList<CastEvent> RecentCasts => recentCasts;

    public event Action? CombatStarted;
    public event Action? CombatEnded;
    public event Action<CastEvent>? CastStarted;

    /// <summary>Raised after CombatEnded when the pull looked like a wipe.</summary>
    public event Action? Wiped;

    /// <summary>True when the most recent pull looked like a wipe rather than a kill.</summary>
    public bool LastPullWasWipe { get; private set; }

    /// <summary>How many times an action has been cast so far this pull.</summary>
    public int OccurrenceOf(uint actionId) => occurrences.GetValueOrDefault(actionId);

    /// <summary>Clears cast history and occurrence counters without touching combat state.</summary>
    public void ResetPull()
    {
        tracked.Clear();
        occurrences.Clear();
        recentCasts.Clear();
        pullCasters.Clear();
    }

    private void OnDutyWiped(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        LastPullWasWipe = true;
        ResetPull();
        Wiped?.Invoke();
    }

    private void OnDutyRecommenced(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        ResetPull();
        Wiped?.Invoke();
    }

    /// <summary>
    /// The game only reports wipes in duties that have them, so fall back on what we can see:
    /// we're dead, or something that was casting at us is still standing.
    /// </summary>
    private bool LooksLikeWipe()
    {
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local is { IsDead: true })
                return true;

            if (pullCasters.Count == 0)
                return false;

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not IBattleChara chara)
                    continue;
                if (!pullCasters.Contains(chara.EntityId))
                    continue;
                if (!chara.IsDead && chara.IsTargetable && chara.CurrentHp > 0)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not work out whether that pull was a wipe.");
            return false;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            UpdateCombatState();

            if (!inCombat)
                return;

            var now = DateTime.UtcNow;
            if ((now - lastScanUtc).TotalSeconds < ScanIntervalSeconds)
                return;
            lastScanUtc = now;

            ScanCasts(now);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EncounterMonitor update failed.");
        }
    }

    private void UpdateCombatState()
    {
        var nowInCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (nowInCombat == inCombat)
            return;

        inCombat = nowInCombat;
        if (inCombat)
        {
            combatStartUtc = DateTime.UtcNow;
            LastPullWasWipe = false;
            ResetPull();
            CombatStarted?.Invoke();
        }
        else
        {
            var wiped = LooksLikeWipe();
            LastPullWasWipe = wiped;

            CombatEnded?.Invoke();

            if (wiped)
                Wiped?.Invoke();
        }
    }

    private void ScanCasts(DateTime now)
    {
        var seen = new HashSet<uint>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleChara chara)
                continue;
            if (obj.ObjectKind != ObjectKind.BattleNpc)
                continue;
            if (obj is IBattleNpc npc && npc.BattleNpcKind is not (BattleNpcSubKind.Combatant or BattleNpcSubKind.BNpcPart))
                continue;

            var entityId = chara.EntityId;
            seen.Add(entityId);

            if (!tracked.TryGetValue(entityId, out var state))
            {
                state = new TrackedCaster();
                tracked[entityId] = state;
            }

            var casting = chara.IsCasting;
            var actionId = chara.CastActionId;

            var isNewCast = casting && (!state.WasCasting || state.ActionId != actionId);

            state.WasCasting = casting;
            state.ActionId = casting ? actionId : 0;

            if (!isNewCast || actionId == 0)
                continue;

            var occurrence = occurrences.GetValueOrDefault(actionId) + 1;
            occurrences[actionId] = occurrence;

            // CurrentCastTime is already non-zero by the time we notice, so wind the
            // start time back to when the bar actually began.
            var elapsed = Math.Clamp(chara.CurrentCastTime, 0f, chara.TotalCastTime);

            var evt = new CastEvent
            {
                ActionId = actionId,
                ActionName = Plugin.Actions.NameOf(actionId),
                CasterName = chara.Name.TextValue,
                CasterEntityId = entityId,
                Occurrence = occurrence,
                TotalCastTime = chara.TotalCastTime,
                CombatTime = Math.Max(0f, (float)(now - combatStartUtc).TotalSeconds - elapsed),
                StartedAtUtc = now.AddSeconds(-elapsed),
                Context = CaptureContext(chara, now, elapsed),
            };

            pullCasters.Add(entityId);
            recentCasts.Add(evt);
            if (recentCasts.Count > 200)
                recentCasts.RemoveAt(0);

            if (Plugin.Config.LogDetectedCasts)
            {
                Plugin.Log.Information(
                    "[cast] {Time:0.0}s  {Name} (#{Id}) x{Occurrence} by {Caster}, {Cast:0.0}s bar",
                    evt.CombatTime, evt.ActionName, evt.ActionId, evt.Occurrence, evt.CasterName, evt.TotalCastTime);
            }

            CastStarted?.Invoke(evt);
        }

        if (tracked.Count > seen.Count)
        {
            foreach (var stale in tracked.Keys.Where(k => !seen.Contains(k)).ToList())
                tracked.Remove(stale);
        }
    }

    private CastStartContext? CaptureContext(IBattleChara caster, DateTime now, float elapsed)
    {
        try
        {
            var targetId = caster.CastTargetObjectId;
            var hasTarget = targetId is not (0 or 0xE0000000 or ulong.MaxValue);
            var target = hasTarget ? Plugin.ObjectTable.SearchById(targetId) : null;
            return new CastStartContext
            {
                ObservedTime = Math.Max(0f, (float)(now - combatStartUtc).TotalSeconds),
                ObservedAtUtc = now, StartedAtUtc = now.AddSeconds(-elapsed),
                CasterId = caster.GameObjectId, TargetId = hasTarget ? targetId : null,
                CasterWorldPosition = caster.Position, CasterHeading = caster.Rotation,
                TargetWorldPosition = target?.Position, TargetHeading = target?.Rotation,
            };
        }
        catch (Exception ex)
        {
            // Optional context must not prevent the existing cast reminder from firing.
            Plugin.Log.Warning(ex, "Cast actor context was unavailable.");
            return null;
        }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.DutyState.DutyWiped -= OnDutyWiped;
        Plugin.DutyState.DutyRecommenced -= OnDutyRecommenced;
    }
}
