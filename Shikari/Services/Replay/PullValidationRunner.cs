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

/// <summary>Evidence coverage for one actual eligible arm, including arms with no emitted decision.</summary>
public sealed class PullValidationOccurrence
{
    public string RuleId { get; init; } = "";
    public uint AnchorActionId { get; init; }
    public int Occurrence { get; init; }
    public float StartTime { get; init; }
    public float Deadline { get; init; }
    public float EndTime { get; set; }
    public bool Complete { get; set; }
    public List<string> Reasons { get; } = new();
}

public sealed class PullValidationResult
{
    public PlanDocument Plan { get; init; } = new();
    public string AttemptId { get; init; } = "";
    public long ActorId { get; init; }
    public float Duration { get; init; }
    public uint TerritoryId { get; init; }
    public bool ScopeVerified { get; init; }
    public bool Complete { get; set; }
    /// <summary>False preserves conservative whole-run handling of older, manually constructed results.</summary>
    public bool HasOccurrenceReadiness { get; init; }
    /// <summary>Global source integrity, distinct from missing evidence in an individual assignment window.</summary>
    public bool EvidenceUsable { get; set; }
    public int ActiveRules { get; set; }
    public int ExcludedRules { get; set; }
    public List<AdaptiveDecision> Decisions { get; } = new();
    public List<string> Notices { get; } = new();
    public List<PullValidationOccurrence> Occurrences { get; } = new();
}

/// <summary>Runs a detached plan against one actor's recorded evidence without reading live services.</summary>
public static class PullValidationRunner
{
    public const int MaxDecisions = 1024;
    private const int MaxNotices = 64;
    private sealed record InputEvent(float Time, int Priority, int Order, RecordedCast? Cast = null,
        EvidenceStatus? Status = null, bool Gap = false, uint IssueStatus = 0, string? Issue = null);
    private sealed class Window
    {
        public required PullValidationOccurrence Coverage;
        public required Dictionary<uint, StatusCondition[]> Conditions;
        public readonly HashSet<(uint Id, uint Source)> ObservedKeys = new();
        public bool Fresh;
        public bool HasDirectIssue;
    }

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
            HasOccurrenceReadiness = true, EvidenceUsable = evidence?.Complete == true,
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
            GlobalIssue(result, "This plan exceeds the engine's 128-rule limit; later rules were not evaluated.");
        }
        var candidates = plan.AdaptiveMechanics.Take(128).Where(r => r.Enabled && r.TerritoryId == options.TerritoryId && r.IsValid(plan)).ToArray();
        var activeRules = candidates.Where(r => candidates.Count(other => r.Overlaps(other)) == 1).ToArray();
        if (activeRules.Any(r => string.IsNullOrEmpty(r.Id)) || activeRules.GroupBy(r => r.Id).Any(g => g.Count() > 1))
        {
            GlobalIssue(result, "Tested rule identities are missing or duplicated; decisions cannot establish identity agreement.");
        }
        var conditions = activeRules.SelectMany(Conditions).ToArray();
        var relevantStatuses = conditions.Select(c => c.StatusId).ToHashSet();
        var events = CastEvents(frozenAttempt, result, activeRules.Select(r => r.AnchorActionId).ToHashSet(), cancel);
        var logs = evidence.Source == "FF Logs";
        if (logs) Notice(result, "FF Logs initial durations and live status parameters are unknown here; stored duration or stack fields cannot prove them.");
        for (var index = 0; index < evidence.Statuses.Count; index++)
        {
            if ((index & 127) == 0) cancel.ThrowIfCancellationRequested();
            var status = evidence.Statuses[index];
            if (status == null) { GlobalIssue(result, "Invalid status events were excluded without a usable actor or time."); continue; }
            if (status.ActorId != actorId) continue;
            var candidateStatus = status.StatusId == 0 && logs && status.AbilityId is > 1000000 and <= 1065535
                ? status.AbilityId - 1000000 : status.StatusId;
            if (status.Change != "unavailable" && !relevantStatuses.Contains(candidateStatus)) continue;
            if (!float.IsFinite(status.Time) || status.Time < 0 || status.Time > frozenAttempt.Duration)
            {
                GlobalIssue(result, "A relevant status event has no usable time; its affected assignment window is unknown.");
                continue;
            }
            if (status.Change != "unavailable" && status.StatusId == 0)
            {
                events.Add(new(status.Time, 2, index, IssueStatus: candidateStatus,
                    Issue: "A log aura corresponding to this condition has not been verified as a game status."));
                continue;
            }
            if (!ValidStatus(status, frozenAttempt.Duration))
            {
                events.Add(new(status.Time, 2, index, IssueStatus: candidateStatus, Issue: "A relevant status event was invalid and excluded."));
                continue;
            }
            events.Add(new(status.Time, 2, index, Status: status));
        }
        if (options.Scenario == PullValidationScenario.ObservationGap)
        {
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
        var windows = new Dictionary<string, Window>();
        var uncertainClosed = new List<Window>();
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
                    foreach (var window in windows.Values)
                    {
                        WindowIssue(window, "Status observations were unavailable during this assignment window.");
                        window.ObservedKeys.Clear();
                    }
                    engine.InvalidateEvidence();
                    carriedAcrossGap.UnionWith(activeStatuses);
                    activeStatuses.Clear();
                    if (!input.Gap)
                    {
                        Notice(result, "The player's statuses became unavailable; pending evidence was invalidated.");
                    }
                    continue;
                }
                if (input.Cast is { } cast)
                {
                    foreach (var rule in activeRules.Where(r => r.AnchorActionId == cast.ActionId &&
                                 (r.Occurrence == 0 || r.Occurrence == cast.Occurrence)))
                    {
                        uncertainClosed.RemoveAll(w => w.Coverage.RuleId == rule.Id);
                        if (windows.Remove(rule.Id, out var replaced))
                            FinishWindow(result, replaced, input.Time, "A later cast rearmed this rule before its decision was emitted.");
                        var coverage = new PullValidationOccurrence { RuleId = rule.Id, AnchorActionId = cast.ActionId,
                            Occurrence = cast.Occurrence!.Value, StartTime = input.Time,
                            Deadline = cast.StartTime!.Value + rule.WindowSeconds, EndTime = input.Time };
                        var window = new Window { Coverage = coverage,
                            Conditions = Conditions(rule).GroupBy(c => c.StatusId).ToDictionary(g => g.Key, g => g.ToArray()) };
                        if (options.Scenario == PullValidationScenario.ObservationGap && input.Time >= options.GapStart &&
                            input.Time < options.GapStart + options.GapDuration)
                            WindowIssue(window, "This assignment armed while status observations were unavailable.");
                        windows[rule.Id] = window;
                        result.Occurrences.Add(coverage);
                    }
                    engine.Arm(cast.ActionId, cast.Occurrence!.Value, cast.StartTime!.Value);
                    continue;
                }
                if (input.Issue != null)
                {
                    foreach (var window in windows.Values.Where(w => w.Conditions.ContainsKey(input.IssueStatus)))
                        WindowIssue(window, input.Issue);
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
                foreach (var window in windows.Values)
                {
                    if (!window.Conditions.TryGetValue(status.StatusId, out var relevant)) continue;
                    // As in Observe, a later event can invalidate an existing source, but
                    // cannot introduce a new source after the acquisition deadline.
                    if (input.Time > window.Coverage.Deadline && !window.ObservedKeys.Contains((status.StatusId, source))) continue;
                    if (!observation[0].Removed || source != 0) window.ObservedKeys.Add((status.StatusId, source));
                    if (observation[0].Baseline && !observation[0].Removed)
                        WindowIssue(window, "A relevant status was already present in a baseline; its original acquisition was not observed.");
                    if (!observation[0].Removed && relevant.Any(c =>
                        !observation[0].DurationKnown && (c.MinimumSeconds != 0 || c.MaximumSeconds != 3600) ||
                        !observation[0].ParameterKnown && c.Parameter >= 0))
                        WindowIssue(window, "A required initial duration or parameter was unknown when its status was observed.");
                    if (!observation[0].Baseline && !observation[0].Removed && input.Time <= window.Coverage.Deadline)
                        window.Fresh = true;
                }
            }
            var decisions = engine.Update(Array.Empty<StatusObservation>(), time);
            // A missing observation in another still-armed mechanic could have changed this
            // poll's conflict suppression. Inspect only evidence available now: later gaps
            // must not retroactively taint an already completed earlier assignment.
            // Unknown values may also have changed when an early candidate settled. Such
            // a candidate can still compete until the original deadline (plus its final
            // evaluation poll), unless an observed cast explicitly rearms that rule.
            uncertainClosed.RemoveAll(w => time > w.Coverage.Deadline + interval);
            var uncertainCompetitors = windows.Values.Where(w => !w.Fresh || w.HasDirectIssue).Concat(uncertainClosed).ToArray();
            foreach (var decision in decisions)
            {
                if (!windows.TryGetValue(decision.RuleId, out var window))
                { GlobalIssue(result, "An emitted decision could not be bound to its armed assignment window."); continue; }
                if (uncertainCompetitors.Any(other => other != window))
                    WindowIssue(window, "Another potentially concurrent mechanic had incomplete evidence and could affect this decision's conflict outcome.", direct: false);
            }
            foreach (var decision in decisions)
                if (windows.Remove(decision.RuleId, out var window))
                {
                    FinishWindow(result, window, time);
                    if (window.HasDirectIssue && time < window.Coverage.Deadline + interval) uncertainClosed.Add(window);
                }
            if (result.Decisions.Count + decisions.Count > MaxDecisions)
            {
                result.Decisions.AddRange(decisions.Take(MaxDecisions - result.Decisions.Count));
                return Reject(result, "The 1,024-decision limit was reached; later decisions were not evaluated.");
            }
            result.Decisions.AddRange(decisions);
        }
        foreach (var window in windows.Values)
            FinishWindow(result, window, frozenAttempt.Duration, "The recording ended before this assignment emitted a decision.");
        result.Complete = result.ScopeVerified && result.EvidenceUsable && result.Occurrences.Count > 0 && result.Occurrences.All(o => o.Complete);
        return result;
    }

    private static List<InputEvent> CastEvents(ReplayAttempt attempt, PullValidationResult result, HashSet<uint> anchorActions, CancellationToken cancel)
    {
        var casts = new List<RecordedCast>();
        if (attempt.Casts.Count == 0)
        {
            GlobalIssue(result, "Legacy recording: authored mechanic anchors are used because cast observations were not recorded; arrival timing is unverified.");
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
                    GlobalIssue(result, "Casts without a compatible source, unique occurrence or usable start/arrival were excluded.");
                    continue;
                }
                if (cast.ObservedTime == null && cast.Source == "Live")
                {
                    GlobalIssue(result, "Some live casts lack their observation time; reconstructed starts are used with unverified arrival timing.");
                }
                casts.Add(cast);
            }
        var ambiguous = casts.GroupBy(c => (c.ActionId, c.Occurrence)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (ambiguous.Count > 0)
        {
            GlobalIssue(result, "Duplicate cast action/occurrence identities were excluded instead of selecting an arbitrary anchor.");
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

    private static StatusCondition[] Conditions(AdaptiveMechanic rule) => rule.Branches.SelectMany(b => b.AdditionalStatuses
        .Append(new StatusCondition { StatusId = b.StatusId, Parameter = b.Parameter,
            MinimumSeconds = b.MinimumSeconds, MaximumSeconds = b.MaximumSeconds })).ToArray();

    private static void WindowIssue(Window window, string reason, bool direct = true)
    {
        window.HasDirectIssue |= direct;
        if (window.Coverage.Reasons.Count < MaxNotices && !window.Coverage.Reasons.Contains(reason))
            window.Coverage.Reasons.Add(reason);
    }

    private static void FinishWindow(PullValidationResult result, Window window, float time, string? reason = null)
    {
        if (reason != null) WindowIssue(window, reason);
        if (!window.Fresh)
            WindowIssue(window, "No fresh relevant status was observed in this acquisition window; a timeout cannot prove no assignment.");
        window.Coverage.EndTime = time;
        window.Coverage.Complete = window.Coverage.Reasons.Count == 0;
        foreach (var issue in window.Coverage.Reasons) Notice(result, issue);
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
            Duration = source.Duration, TerritoryId = source.TerritoryId, ScopeVerified = source.ScopeVerified, Complete = source.Complete,
            HasOccurrenceReadiness = source.HasOccurrenceReadiness, EvidenceUsable = source.EvidenceUsable };
        copy.Notices.AddRange(source.Notices);
        return copy;
    }

    private static PullValidationResult Reject(PullValidationResult result, string reason)
    { GlobalIssue(result, reason); return result; }

    private static void GlobalIssue(PullValidationResult result, string reason)
    { result.Complete = false; result.EvidenceUsable = false; Notice(result, reason); }

    private static void Notice(PullValidationResult result, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason) && result.Notices.Count < MaxNotices && !result.Notices.Contains(reason))
            result.Notices.Add(reason);
    }
}
