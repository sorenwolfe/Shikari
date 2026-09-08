using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;

namespace Shikari.Services.Replay;

public enum AssignmentOutcome { Reproduced, Conflict, NoMatch, Unknown }
public sealed record AssignmentExample(string SourceKey, string AttemptId, long ActorId, int SlotIndex,
    int Occurrence, float Time, int BranchIndex, AssignmentOutcome Outcome, bool ScopeVerified, string Reason,
    float? Distance, string PositionReason);
public sealed record AssignmentBranchCoverage(int Index, string Label, int Recordings, int Examples);
public sealed class AdaptiveCoverage
{
    public int Recordings { get; set; }
    public int Duplicates { get; set; }
    public int ExcludedRecordings { get; set; }
    public List<AssignmentBranchCoverage> Branches { get; } = new();
    public List<AssignmentExample> Examples { get; } = new();
    public Dictionary<string, int> Exclusions { get; } = new();
    public string Message { get; set; } = "";
}
public static class AdaptiveEvidenceAudit
{
    private const int MaxRecordings = 30;
    private const int MaxExamples = 512;

    /// <summary>Re-evaluates raw, source-scoped observations. No counters or rule changes are persisted.</summary>
    public static AdaptiveCoverage Analyse(PlanDocument plan, AdaptiveMechanic rule, IReadOnlyList<ReplayAttempt> recordings) =>
        AnalyseIncrementally(plan, rule, recordings).Last();

    /// <summary>Cooperative work, yielding after validation and each actor/cast example. The caller
    /// must cancel if the inputs change; the final yielded report is complete only when enumeration ends.</summary>
    public static IEnumerable<AdaptiveCoverage> AnalyseIncrementally(PlanDocument plan, AdaptiveMechanic rule, IReadOnlyList<ReplayAttempt> recordings)
    {
        var result = new AdaptiveCoverage();
        if (!rule.IsValid(plan)) { result.Message = "Set valid conditions and destination boards before checking recordings."; yield return result; yield break; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var relevantStatuses = rule.Branches.SelectMany(b => b.AdditionalStatuses.Select(c => c.StatusId).Append(b.StatusId)).ToHashSet();
        var geometry = new Dictionary<(string, string), bool>();
        foreach (var attempt in recordings.Take(MaxRecordings))
        {
            yield return result;
            if (result.Examples.Count >= MaxExamples) { result.Message = "Example limit reached; this report covers the first 512 actor/mechanic examples."; break; }
            if (!ReplayValidation.IsValid(attempt)) { Exclude(result, "Invalid or unsupported recording"); continue; }
            if (attempt.Plan.Id != plan.Id) { Exclude(result, "Different strategy"); continue; }
            var key = SourceKey(attempt);
            if (key == null) { Exclude(result, "Missing source identity"); continue; }
            if (!seen.Add(key)) { result.Duplicates++; continue; }
            var scope = Scope(plan, rule, attempt);
            if (!scope.Compatible) { Exclude(result, "Different encounter or territory"); continue; }
            var anchors = attempt.Mechanics.Where(m => m.ActionId == rule.AnchorActionId && m.Occurrence > 0 &&
                (rule.Occurrence == 0 || m.Occurrence == rule.Occurrence)).OrderBy(m => m.Time).Take(64).ToArray();
            if (anchors.Length == 0) { Exclude(result, "No matching cast occurrence"); continue; }
            result.Recordings++;
            var resolveTimes = anchors.Select(a => a.ExpectedResolve).Distinct().OrderBy(t => t).ToArray();
            // Proximity only needs fresh samples at these cast ends. Indexing a complete
            // 30-minute position history wastes memory and can stall the drawing thread.
            var timeline = new Lazy<EvidenceTimeline>(() => new EvidenceTimeline(new ReplayEvidence {
                Positions = attempt.Evidence.Positions.Where(p => NearResolve(p.Time, resolveTimes)).ToList() }));
            var actors = attempt.Evidence.Actors.Take(8).ToArray();
            var simulation = new ReplayAttempt { Plan = attempt.Plan, Evidence = attempt.Evidence, Duration = attempt.Duration,
                Mechanics = new() };
            yield return result;
            if (actors.Length == 0)
                result.Examples.Add(new(key, attempt.Id, 0, -1, anchors[0].Occurrence, anchors[0].Time, -1,
                    AssignmentOutcome.Unknown, scope.Verified, "No actor observations", null, "No position evidence"));
            foreach (var actor in actors)
            {
                if (result.Examples.Count >= MaxExamples) break;
                // A simulation is always scoped to one source actor. Numerically equal IDs in
                // another report or local pull never enter this status stream.
                var actorStatuses = attempt.Evidence.Statuses.Where(s => s.ActorId == actor.Id).ToArray();
                var seat = actor.SlotIndex >= 0 && StrategyEnrichment.SameSeat(plan, attempt.Plan, actor.SlotIndex) &&
                    attempt.Evidence.Actors.Count(a => a.SlotIndex == actor.SlotIndex) == 1 ? actor.SlotIndex : -1;
                foreach (var anchor in anchors)
                {
                    if (result.Examples.Count >= MaxExamples) break;
                    simulation.Mechanics.Clear(); simulation.Mechanics.Add(anchor);
                    var decisions = attempt.Evidence.Complete ? EvidenceRules.Simulate(simulation, actor.Id, rule) : Array.Empty<AdaptiveDecision>();
                    var end = anchor.Time + rule.WindowSeconds;
                    var observed = actorStatuses.Where(s => s.Time >= anchor.Time && s.Time <= end).ToArray();
                    var fresh = observed.Where(s => relevantStatuses.Contains(s.StatusId) && !s.Baseline && s.Change is "apply" or "refresh").ToArray();
                    var matches = decisions.Where(d => d.Occurrence == anchor.Occurrence && d.Time >= anchor.Time && d.Time <= end + .2f).ToArray();
                    var outcome = AssignmentOutcome.Unknown;
                    var reason = "Assignment statuses were not observed";
                    var branch = -1;
                    var time = anchor.Time;
                    if (!attempt.Evidence.Complete) reason = "Partial recording";
                    else if (attempt.Duration < end) reason = "Recording ended before the assignment window closed";
                    else if (observed.Any(s => s.Change == "unavailable")) reason = "Status evidence was unavailable inside the assignment window";
                    else if (anchors.Count(m => m.Occurrence == anchor.Occurrence) > 1) reason = "Several casts claim this occurrence";
                    else if (rule.Branches.Any(b => attempt.Plan.FindSlide(b.SlideId) == null)) reason = "A destination board was not present in this recording";
                    else if (fresh.Length == 0) reason = observed.Any(s => s.Baseline && relevantStatuses.Contains(s.StatusId))
                        ? "Only an existing status baseline was available" : "Assignment statuses were not observed";
                    else if (matches.Length != 1) reason = "No complete, unique decision in the recorded window";
                    else
                    {
                        var decision = matches[0]; time = decision.Time;
                        if (decision.Conflict) { outcome = AssignmentOutcome.Conflict; reason = "Multiple branches matched the same observations"; }
                        else if (UnknownProperty(rule, fresh.Where(s => s.Time <= time)) is { } missing) reason = missing;
                        else if (decision.RuleId == rule.Id && decision.BranchIndex >= 0 && decision.BranchIndex < rule.Branches.Count && decision.SlideId.Length > 0)
                        { outcome = AssignmentOutcome.Reproduced; branch = decision.BranchIndex; reason = "Recorded conditions reproduce this branch"; }
                        else { outcome = AssignmentOutcome.NoMatch; reason = "Fresh statuses did not satisfy a settled assignment"; }
                    }
                    float? distance = null;
                    var positionReason = "No assignment destination to compare";
                    if (branch >= 0)
                    {
                        if (!scope.Verified) positionReason = "Encounter identity is unverified";
                        else (distance, positionReason) = Proximity(plan, attempt, actor, seat, rule.Branches[branch].SlideId, anchor, time, timeline, geometry);
                    }
                    result.Examples.Add(new(key, attempt.Id, actor.Id, seat, anchor.Occurrence, time, branch,
                        outcome, scope.Verified, reason, distance, positionReason));
                    yield return result;
                }
            }
        }
        for (var i = 0; i < rule.Branches.Count; i++)
        {
            var examples = result.Examples.Where(e => e.ScopeVerified && e.Outcome == AssignmentOutcome.Reproduced && e.BranchIndex == i).ToArray();
            result.Branches.Add(new(i, rule.Branches[i].Label, examples.Select(e => e.SourceKey).Distinct().Count(), examples.Length));
        }
        if (result.Examples.Count >= MaxExamples) result.Message = "Example limit reached; this report covers the first 512 actor/mechanic examples.";
        if (recordings.Count > MaxRecordings) result.Message += " Only the 30 retained recording slots are analyzed.";
        yield return result;
    }

    private static string? SourceKey(ReplayAttempt attempt)
    {
        if (attempt.Evidence.Source == "Local recording") return "local:" + attempt.Id;
        if (attempt.Evidence.Source != "FF Logs") return null;
        var report = attempt.Evidence.ReportCode;
        return report.Length is > 0 and <= 32 && report.All(char.IsAsciiLetterOrDigit) && attempt.Evidence.FightId > 0
            ? $"fflogs:{report}:{attempt.Evidence.FightId}" : null;
    }

    private static bool NearResolve(float time, float[] resolves)
    {
        var next = Array.BinarySearch(resolves, time);
        if (next >= 0) return true;
        next = ~next;
        return next < resolves.Length && resolves[next] - time <= EvidenceTimeline.MaxPositionAge;
    }

    private static (bool Compatible, bool Verified) Scope(PlanDocument plan, AdaptiveMechanic rule, ReplayAttempt attempt)
    {
        if (attempt.TerritoryId != 0 && attempt.TerritoryId != rule.TerritoryId) return (false, false);
        var known = plan.StrategyEvidence.Where(e => e.EncounterVerified && e.EncounterId != 0 &&
            (e.TerritoryId == 0 || e.TerritoryId == rule.TerritoryId)).Select(e => e.EncounterId).Distinct().ToArray();
        var encounter = attempt.Evidence.EncounterId;
        if (encounter != 0 && known.Any(id => id != encounter)) return (false, false);
        if (attempt.TerritoryId == rule.TerritoryId) return (true, true);
        if (encounter != 0 && known.Length > 0) return (known.All(id => id == encounter), known.Length == 1 && known[0] == encounter);
        return (true, false);
    }

    private static (float? Distance, string Reason) Proximity(PlanDocument plan, ReplayAttempt attempt, EvidenceActor actor, int seat,
        string slideId, ReplayMechanic anchor, float decisionTime, Lazy<EvidenceTimeline> timeline, Dictionary<(string, string), bool> geometry)
    {
        if (seat < 0) return (null, "Plan seat is unresolved or changed");
        if (!geometry.TryGetValue((attempt.Id, slideId), out var same))
            geometry[(attempt.Id, slideId)] = same = StrategyEnrichment.SameGeometry(plan, attempt.Plan, slideId);
        if (!same) return (null, "Recorded board geometry differs");
        var destinations = plan.FindSlide(slideId)!.Items.Where(i => i.Kind == CanvasItemKind.PlayerToken && i.SlotIndex == seat).ToArray();
        if (destinations.Length != 1) return (null, "No unique authored destination for this seat");
        if (anchor.ExpectedResolve < decisionTime || anchor.ExpectedResolve > attempt.Duration)
            return (null, "Expected cast end is not a recorded point after this assignment");
        var at = anchor.ExpectedResolve;
        Vector2? position = null;
        if (attempt.Evidence.CalibrationSlideId == slideId && EvidenceProjection.TryAlign(attempt.Evidence, out var alignment) &&
            timeline.Value.PositionAt(actor.Id, at) is { } sample)
            position = alignment.ToPlan(sample.Position);
        else if (attempt.Evidence.Source != "FF Logs")
        {
            var frame = attempt.Frames.LastOrDefault(f => f.Valid && f.SlideId == slideId && f.Time <= at && at - f.Time <= EvidenceTimeline.MaxPositionAge);
            var players = frame?.Players.Where(p => p.SlotIndex == seat).ToArray();
            if (players is { Length: 1 }) position = players[0].Board;
        }
        if (position == null) return (null, "No fresh position aligned to this board at expected cast end");
        var distance = Vector2.Distance(position.Value, destinations[0].Position);
        return float.IsFinite(distance) ? (distance, $"Observed at expected cast end ({at:0.0}s)") : (null, "Position is invalid");
    }

    private static void Exclude(AdaptiveCoverage report, string reason)
    {
        report.ExcludedRecordings++;
        report.Exclusions[reason] = report.Exclusions.GetValueOrDefault(reason) + 1;
    }

    private static string? UnknownProperty(AdaptiveMechanic rule, IEnumerable<EvidenceStatus> observations)
    {
        var conditions = rule.Branches.SelectMany(b => b.AdditionalStatuses.Append(new StatusCondition {
            StatusId = b.StatusId, Parameter = b.Parameter, MinimumSeconds = b.MinimumSeconds, MaximumSeconds = b.MaximumSeconds })).ToArray();
        foreach (var status in observations.GroupBy(s => (s.StatusId, s.SourceId)).Select(g => g.OrderBy(s => s.Time).Last()))
        {
            var relevant = conditions.Where(c => c.StatusId == status.StatusId);
            if (status.Parameter == null && relevant.Any(c => c.Parameter >= 0)) return "A status parameter required by this rule is unknown";
            if (status.Duration == null && relevant.Any(c => c.MinimumSeconds != 0 || c.MaximumSeconds != 3600)) return "An initial status duration required by this rule is unknown";
        }
        return null;
    }
}
