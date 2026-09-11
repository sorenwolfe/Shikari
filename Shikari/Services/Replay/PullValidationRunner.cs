using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Shikari.Model;
using Shikari.Services.Adaptive;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

public enum PullValidationScenario { RecordedTiming, DelayedPolling, ObservationGap }

public sealed record PullValidationOptions(uint TerritoryId, bool IncludeDisabledRules = false,
    PullValidationScenario Scenario = PullValidationScenario.RecordedTiming, float GapStart = 0, float GapDuration = 1);

public sealed class PullValidationResult
{
    public PlanDocument Plan { get; init; } = new();
    public string AttemptId { get; init; } = "";
    public long ActorId { get; init; }
    public float Duration { get; init; }
    public uint TerritoryId { get; init; }
    public bool ScopeVerified { get; init; }
    public bool Complete { get; set; }
    public int ActiveRules { get; set; }
    public int ExcludedRules { get; set; }
    public List<AdaptiveDecision> Decisions { get; } = new();
    public List<string> Notices { get; } = new();
}

/// <summary>Runs a detached plan against one actor's recorded evidence without reading live services.</summary>
public static class PullValidationRunner
{
    public const int MaxDecisions = 1024;
    private const int MaxNotices = 64;
    private sealed record InputEvent(float Time, int Priority, int Order, RecordedCast? Cast = null,
        EvidenceStatus? Status = null, bool Gap = false);

    public static PullValidationResult Run(PlanDocument frozenPlan, ReplayAttempt frozenAttempt, long actorId,
        PullValidationOptions options, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(frozenPlan);
        ArgumentNullException.ThrowIfNull(frozenAttempt);
        ArgumentNullException.ThrowIfNull(options);
        cancel.ThrowIfCancellationRequested();
        var evidence = frozenAttempt.Evidence;
        var scope = Scope(frozenPlan, frozenAttempt, options.TerritoryId);
        var result = new PullValidationResult
        {
            Plan = frozenPlan, AttemptId = frozenAttempt.Id, ActorId = actorId,
            Duration = frozenAttempt.Duration, TerritoryId = options.TerritoryId,
            ScopeVerified = scope.Verified, Complete = scope.Verified && evidence?.Complete == true,
        };
        if (!scope.Compatible) return Reject(result, "The selected territory or verified encounter does not match this recording.");
        if (!float.IsFinite(frozenAttempt.Duration) || frozenAttempt.Duration < 0 || frozenAttempt.Duration > ReplayBuffer.MaxDuration ||
            evidence?.Actors == null || evidence.Statuses == null || frozenAttempt.Casts == null || frozenAttempt.Mechanics == null ||
            evidence.Actors.Count > 32 || evidence.Statuses.Count > ReplayEvidence.MaxStatuses ||
            frozenAttempt.Casts.Count > ReplayBuffer.MaxCasts || frozenAttempt.Mechanics.Count > ReplayBuffer.MaxMechanics)
            return Reject(result, "Recording limits or required evidence are invalid; no validation was run.");
        if (actorId <= 0 || evidence.Actors.Count(a => a != null && a.Id == actorId) != 1)
            return Reject(result, "The selected player's identity is missing or ambiguous in this recording.");
        if (evidence.Source is not ("Local recording" or "FF Logs"))
            return Reject(result, "This recording's evidence source is not recognized.");
        if (!Enum.IsDefined(options.Scenario) || options.Scenario == PullValidationScenario.ObservationGap &&
            (!float.IsFinite(options.GapStart) || !float.IsFinite(options.GapDuration) || options.GapStart < 0 ||
                options.GapStart > frozenAttempt.Duration || options.GapDuration <= 0 || options.GapDuration > ReplayBuffer.MaxDuration))
            return Reject(result, "The requested observation-gap or polling scenario is invalid.");
        if (!scope.Verified) Notice(result, "Encounter scope is unverified; choosing a territory only selects rules for this simulation.");
        if (!evidence.Complete) Notice(result, "The recording contains incomplete evidence; positive results do not establish full coverage.");
        foreach (var warning in evidence.Warnings.Take(10)) Notice(result, warning);

        // Inputs already belong to the caller's detached session. Only the opt-in flag needs
        // a second graph: it must never enable rules on that caller's original snapshot.
        var plan = options.IncludeDisabledRules ? PlanSnapshot.Copy(frozenPlan) : frozenPlan;
        if (options.IncludeDisabledRules)
        {
            foreach (var rule in plan.AdaptiveMechanics) rule.Enabled = true;
            result = CopyWithPlan(result, plan);
            Notice(result, "Disabled rules are included for this test only.");
        }
        var engine = new AdaptiveEngine(plan, options.TerritoryId);
        result.ActiveRules = engine.ActiveRuleCount;
        result.ExcludedRules = Math.Max(0, plan.AdaptiveMechanics.Count - result.ActiveRules);
        if (result.ExcludedRules > 0)
            Notice(result, $"{result.ExcludedRules} rule(s) were excluded by enabled state, scope, validity, overlap or the 128-rule engine limit.");
        if (result.ActiveRules == 0) return Reject(result, "No eligible rules remain to validate for this territory.");
        if (plan.AdaptiveMechanics.Count > 128)
        {
            result.Complete = false;
            Notice(result, "This plan exceeds the engine's 128-rule limit; later rules were not evaluated.");
        }
        var candidates = plan.AdaptiveMechanics.Take(128).Where(r => r.Enabled && r.TerritoryId == options.TerritoryId && r.IsValid(plan)).ToArray();
        var activeRules = candidates.Where(r => candidates.Count(other => r.Overlaps(other)) == 1).ToArray();
        if (activeRules.Any(r => string.IsNullOrEmpty(r.Id)) || activeRules.GroupBy(r => r.Id).Any(g => g.Count() > 1))
        {
            result.Complete = false;
            Notice(result, "Tested rule identities are missing or duplicated; decisions cannot establish identity agreement.");
        }
        var conditions = activeRules.SelectMany(r => r.Branches).SelectMany(b => b.AdditionalStatuses
            .Append(new StatusCondition { StatusId = b.StatusId, Parameter = b.Parameter,
                MinimumSeconds = b.MinimumSeconds, MaximumSeconds = b.MaximumSeconds })).ToArray();
        var relevantStatuses = conditions.Select(c => c.StatusId).ToHashSet();
        var events = CastEvents(frozenAttempt, result, activeRules.Select(r => r.AnchorActionId).ToHashSet(), cancel);
        var logs = evidence.Source == "FF Logs";
        if (logs) Notice(result, "FF Logs initial durations and live status parameters are unknown here; stored duration or stack fields cannot prove them.");
        var selected = 0;
        for (var index = 0; index < evidence.Statuses.Count; index++)
        {
            if ((index & 127) == 0) cancel.ThrowIfCancellationRequested();
            var status = evidence.Statuses[index];
            if (status == null) { result.Complete = false; Notice(result, "Invalid status events were excluded."); continue; }
            if (status.ActorId != actorId) continue;
            selected++;
            var candidateStatus = status.StatusId == 0 && logs && status.AbilityId is > 1000000 and <= 1065535
                ? status.AbilityId - 1000000 : status.StatusId;
            if (status.Change != "unavailable" && !relevantStatuses.Contains(candidateStatus)) continue;
            if (status.Change != "unavailable" && status.StatusId == 0)
            {
                result.Complete = false;
                Notice(result, "A log aura corresponding to a tested condition has not been verified as a game status.");
                continue;
            }
            if (!ValidStatus(status, frozenAttempt.Duration))
            {
                result.Complete = false; Notice(result, "Invalid status events were excluded."); continue;
            }
            if (status.Baseline)
            {
                result.Complete = false;
                Notice(result, "A tested status was already present in a baseline; its original acquisition was not observed.");
            }
            if (status.Change is "apply" or "refresh" && !status.Baseline && conditions.Any(c => c.StatusId == status.StatusId &&
                ((logs || status.Duration == null) && (c.MinimumSeconds != 0 || c.MaximumSeconds != 3600) ||
                 (logs || status.Parameter == null) && c.Parameter >= 0)))
            {
                result.Complete = false;
                Notice(result, "A rule needs an initial duration or parameter that was not known when its status was observed.");
            }
            events.Add(new(status.Time, 2, index, Status: status));
        }
        if (selected == 0)
        {
            result.Complete = false;
            Notice(result, "This player has no recorded status events; no positive assignment evidence is available.");
        }
        if (options.Scenario == PullValidationScenario.ObservationGap)
        {
            result.Complete = false;
            events.Add(new(options.GapStart, 0, 0, Gap: true));
            Notice(result, $"Synthetic observation gap: {options.GapStart:0.0}s–{Math.Min(frozenAttempt.Duration, options.GapStart + options.GapDuration):0.0}s. Hidden statuses are not restored on resume.");
        }
        var interval = options.Scenario == PullValidationScenario.DelayedPolling ? .25f : .1f;
        if (options.Scenario == PullValidationScenario.DelayedPolling)
            Notice(result, "Synthetic delayed polling: decisions are evaluated every 0.25s; observations retain their recorded arrival times.");
        Notice(result, "Assignment cues are passive previews. Recorded party evidence is sampled separately from live adaptive decisions; this is not exact game playback.");
        events.Sort((left, right) =>
        {
            var order = left.Time.CompareTo(right.Time);
            if (order == 0) order = left.Priority.CompareTo(right.Priority);
            return order == 0 ? left.Order.CompareTo(right.Order) : order;
        });
        var next = 0;
        var activeStatuses = new HashSet<(uint Id, uint Source)>();
        var carriedAcrossGap = new HashSet<(uint Id, uint Source)>();
        var freshStatusTimes = new Dictionary<uint, List<float>>();
        var observation = new StatusObservation[1];
        for (var tick = 0; tick <= (int)Math.Ceiling(frozenAttempt.Duration / interval); tick++)
        {
            cancel.ThrowIfCancellationRequested();
            var time = Math.Min(frozenAttempt.Duration, tick * interval);
            while (next < events.Count && events[next].Time <= time)
            {
                if ((next & 127) == 0) cancel.ThrowIfCancellationRequested();
                var input = events[next++];
                if (input.Gap || input.Status?.Change == "unavailable")
                {
                    engine.InvalidateEvidence();
                    carriedAcrossGap.UnionWith(activeStatuses);
                    activeStatuses.Clear();
                    if (!input.Gap)
                    {
                        result.Complete = false;
                        Notice(result, "The player's statuses became unavailable; pending evidence was invalidated.");
                    }
                    continue;
                }
                if (input.Cast is { } cast)
                {
                    engine.Arm(cast.ActionId, cast.Occurrence!.Value, cast.StartTime!.Value);
                    continue;
                }
                var status = input.Status!;
                var source = (uint)Math.Max(0, status.SourceId);
                if (options.Scenario == PullValidationScenario.ObservationGap && input.Time >= options.GapStart &&
                    input.Time < options.GapStart + options.GapDuration)
                {
                    // Hidden removals cannot be used to infer that a carried status became
                    // a fresh application. Only an observed post-gap event can establish it.
                    if (status.Change != "remove") carriedAcrossGap.Add((status.StatusId, source));
                    continue;
                }
                if (status.Change == "stacks") continue; // Stack counts are not live Status.Param.
                if (status.Change == "remove")
                {
                    if (source == 0)
                    {
                        activeStatuses.RemoveWhere(k => k.Id == status.StatusId);
                        carriedAcrossGap.RemoveWhere(k => k.Id == status.StatusId);
                    }
                    else
                    {
                        activeStatuses.Remove((status.StatusId, source));
                        carriedAcrossGap.Remove((status.StatusId, source));
                    }
                }
                else
                {
                    activeStatuses.Add((status.StatusId, source));
                    if (status.Change == "apply" && !status.Baseline) carriedAcrossGap.Remove((status.StatusId, source));
                }
                observation[0] = new StatusObservation
                {
                    Time = status.Time, StatusId = status.StatusId, SourceId = source,
                    Duration = !logs ? status.Duration ?? 0 : 0, DurationKnown = !logs && status.Duration.HasValue,
                    Parameter = !logs ? (ushort)(status.Parameter ?? 0) : (ushort)0, ParameterKnown = !logs && status.Parameter.HasValue,
                    Baseline = status.Baseline || status.Change != "apply" &&
                        (source == 0 ? carriedAcrossGap.Any(k => k.Id == status.StatusId) :
                            carriedAcrossGap.Contains((status.StatusId, source)) || carriedAcrossGap.Contains((status.StatusId, 0))),
                    Removed = status.Change == "remove",
                };
                engine.Observe(observation, input.Time);
                if (!observation[0].Baseline && !observation[0].Removed)
                {
                    if (!freshStatusTimes.TryGetValue(status.StatusId, out var times))
                        freshStatusTimes[status.StatusId] = times = new();
                    times.Add(input.Time);
                }
            }
            var decisions = engine.Update(Array.Empty<StatusObservation>(), time);
            if (result.Decisions.Count + decisions.Count > MaxDecisions)
            {
                result.Decisions.AddRange(decisions.Take(MaxDecisions - result.Decisions.Count));
                return Reject(result, "The 1,024-decision limit was reached; later decisions were not evaluated.");
            }
            result.Decisions.AddRange(decisions);
        }
        if (engine.PendingRuleCount > 0)
        {
            result.Complete = false;
            Notice(result, "The recording ended while an assignment was still pending; its later outcome is unknown.");
        }
        var anchors = events.Where(e => e.Cast != null).ToDictionary(e => (e.Cast!.ActionId, e.Cast.Occurrence!.Value), e => e.Cast!);
        foreach (var decision in result.Decisions)
        {
            var rule = activeRules.FirstOrDefault(r => r.Id == decision.RuleId);
            if (rule == null || !anchors.TryGetValue((decision.AnchorActionId, decision.Occurrence), out var anchor) ||
                !rule.Branches.SelectMany(b => b.AdditionalStatuses.Select(c => c.StatusId).Append(b.StatusId)).Distinct()
                    .Any(id => HasFreshObservation(freshStatusTimes, id, anchor.ObservedTime ?? anchor.StartTime!.Value,
                        Math.Min(decision.Time, anchor.StartTime!.Value + rule.WindowSeconds))))
            {
                result.Complete = false;
                Notice(result, "An assignment window has no fresh relevant status observations; a timeout is unknown coverage, not proof of no assignment.");
            }
        }
        return result;
    }

    private static List<InputEvent> CastEvents(ReplayAttempt attempt, PullValidationResult result, HashSet<uint> anchorActions, CancellationToken cancel)
    {
        var casts = new List<RecordedCast>();
        if (attempt.Casts.Count == 0)
        {
            result.Complete = false;
            Notice(result, "Legacy recording: authored mechanic anchors are used because cast observations were not recorded; arrival timing is unverified.");
            foreach (var mechanic in attempt.Mechanics)
            {
                cancel.ThrowIfCancellationRequested();
                if (mechanic != null && !anchorActions.Contains(mechanic.ActionId)) continue;
                if (mechanic == null || mechanic.ActionId == 0 || mechanic.Occurrence <= 0 ||
                    !float.IsFinite(mechanic.Time) || mechanic.Time < 0 || mechanic.Time > attempt.Duration)
                { Notice(result, "Invalid legacy anchors were excluded."); continue; }
                casts.Add(new RecordedCast { Source = attempt.Evidence.Source == "FF Logs" ? "FF Logs" : "Live",
                    ActionId = mechanic.ActionId, Occurrence = mechanic.Occurrence, StartTime = mechanic.Time, ObservedTime = mechanic.Time });
            }
        }
        else
            foreach (var cast in attempt.Casts)
            {
                cancel.ThrowIfCancellationRequested();
                if (cast != null && !anchorActions.Contains(cast.ActionId)) continue;
                if (cast == null || cast.ActionId == 0 || cast.Occurrence is not (> 0 and <= ReplayBuffer.MaxCasts) ||
                    cast.StartTime is not { } start || !float.IsFinite(start) || start < 0 || start > attempt.Duration ||
                    cast.Source != (attempt.Evidence.Source == "FF Logs" ? "FF Logs" : "Live") ||
                    cast.ObservedTime is { } observed && (!float.IsFinite(observed) || observed < start || observed > attempt.Duration))
                {
                    result.Complete = false;
                    Notice(result, "Casts without a compatible source, unique occurrence or usable start/arrival were excluded.");
                    continue;
                }
                if (cast.ObservedTime == null && cast.Source == "Live")
                {
                    result.Complete = false;
                    Notice(result, "Some live casts lack their observation time; reconstructed starts are used with unverified arrival timing.");
                }
                casts.Add(cast);
            }
        var ambiguous = casts.GroupBy(c => (c.ActionId, c.Occurrence)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (ambiguous.Count > 0)
        {
            result.Complete = false;
            Notice(result, "Duplicate cast action/occurrence identities were excluded instead of selecting an arbitrary anchor.");
        }
        return casts.Where(c => !ambiguous.Contains((c.ActionId, c.Occurrence)))
            .Select((cast, index) => new InputEvent(cast.ObservedTime ?? cast.StartTime!.Value, 1, index, Cast: cast)).ToList();
    }

    private static bool ValidStatus(EvidenceStatus status, float duration)
    {
        if (!float.IsFinite(status.Time) || status.Time < 0 || status.Time > duration) return false;
        if (status.Change == "unavailable") return true;
        return status.SourceId <= uint.MaxValue &&
            (status.Duration is not { } remaining || float.IsFinite(remaining) && remaining >= 0 && remaining <= 86400) &&
            status.Parameter is not (< 0 or > 65535) &&
            status.Change is "apply" or "refresh" or "remove" or "stacks" && status.StatusId > 0;
    }

    private static bool HasFreshObservation(Dictionary<uint, List<float>> observations, uint status, float start, float end)
    {
        if (!observations.TryGetValue(status, out var times)) return false;
        var index = times.BinarySearch(start);
        if (index < 0) index = ~index;
        return index < times.Count && times[index] <= end;
    }

    private static (bool Compatible, bool Verified) Scope(PlanDocument plan, ReplayAttempt attempt, uint territory)
    {
        if (territory == 0 || attempt.TerritoryId != 0 && attempt.TerritoryId != territory) return (false, false);
        var known = plan.StrategyEvidence.Where(e => e.EncounterVerified && e.EncounterId != 0 &&
            (e.TerritoryId == 0 || e.TerritoryId == territory)).Select(e => e.EncounterId).Distinct().ToArray();
        var encounter = attempt.Evidence?.EncounterId ?? 0;
        if (encounter != 0 && known.Any(id => id != encounter)) return (false, false);
        return (true, attempt.TerritoryId == territory || encounter != 0 && known.Length == 1 && known[0] == encounter);
    }

    private static PullValidationResult CopyWithPlan(PullValidationResult source, PlanDocument plan)
    {
        var copy = new PullValidationResult { Plan = plan, AttemptId = source.AttemptId, ActorId = source.ActorId,
            Duration = source.Duration, TerritoryId = source.TerritoryId, ScopeVerified = source.ScopeVerified, Complete = source.Complete };
        copy.Notices.AddRange(source.Notices);
        return copy;
    }

    private static PullValidationResult Reject(PullValidationResult result, string reason)
    { result.Complete = false; Notice(result, reason); return result; }

    private static void Notice(PullValidationResult result, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason) && result.Notices.Count < MaxNotices && !result.Notices.Contains(reason))
            result.Notices.Add(reason);
    }
}
