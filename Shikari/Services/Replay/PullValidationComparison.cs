using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Shikari.Model;

namespace Shikari.Services.Replay;

public enum PullComparisonOutcome { Matched, Different, Missing, Unexpected, Unknown }

/// <summary>A user-reviewed assignment, bound by the review session to one plan, actor and recording.</summary>
public sealed class PullExpectedAssignment
{
    public string RuleId { get; set; } = "";
    public uint AnchorActionId { get; set; }
    public int Occurrence { get; set; }
    public int BranchIndex { get; set; } = -1;
    public bool Conflict { get; set; }
}

/// <summary>Assignment agreement and recorded navigation are deliberately separate observations.</summary>
public sealed class PullComparisonRow
{
    public PullComparisonOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";
    public string RuleId { get; set; } = "";
    public uint AnchorActionId { get; set; }
    public int Occurrence { get; set; }
    public float? SimulatedTime { get; set; }
    public float? ReferenceTime { get; set; }
    /// <summary>Simulation minus recording, in seconds; not an assertion of timing equivalence.</summary>
    public float? TimeDelta { get; set; }
    public int? SimulatedBranchIndex { get; set; }
    public int? ReferenceBranchIndex { get; set; }
    public string SimulatedSlideId { get; set; } = "";
    public string ReferenceSlideId { get; set; } = "";
    public bool? SimulatedConflict { get; set; }
    public bool? ReferenceConflict { get; set; }
    public bool? RecordedApplied { get; set; }
    public string RecordedNavigation { get; set; } = "";
}

public static class PullValidationComparison
{
    private const int MaxDecisions = 1024;
    private readonly record struct Key(string RuleId, uint Action, int Occurrence);

    /// <summary>Compares assignment reproduction only. Navigation is reported exactly as recorded.</summary>
    public static List<PullComparisonRow> CompareRecorded(PullValidationResult result, ReplayAttempt attempt)
    {
        if (Readiness(result) is { } unavailable) return Unknown(unavailable);
        if (attempt == null || string.IsNullOrEmpty(attempt.Id) || result.AttemptId != attempt.Id)
            return Unknown("The validation and recorded decisions belong to different or unidentified pulls.");
        if (attempt.TerritoryId == 0 || result.TerritoryId != attempt.TerritoryId ||
            !float.IsFinite(attempt.Duration) || result.Duration != attempt.Duration)
            return Unknown("The validation and recording do not cover the same verified territory and duration.");
        var evidence = attempt.Evidence;
        if (evidence?.Source != "Local recording")
            return Unknown("Recorded local-player decisions can only be compared with their original local recording.");
        if (!evidence.Complete)
            return Unknown("The recorded evidence is incomplete; assignment reproduction cannot be established.");
        var actors = evidence.Actors;
        if (actors == null || actors.Count > 32 || actors.Any(a => a == null) ||
            actors.Count(a => a.IsLocal) != 1 || actors.Count(a => a.Id == result.ActorId) != 1 ||
            !actors.Any(a => a.Id == result.ActorId && a.IsLocal))
            return Unknown("Only the recording's uniquely identified local actor has recorded decisions to compare.");
        if (attempt.AdaptiveDecisions == null || attempt.AdaptiveDecisions.Count == 0)
            return Unknown("No recorded decisions are available; an empty trace does not prove a live no-match outcome.");
        // These recorder limits historically discard additional entries without marking Evidence.Complete.
        if (attempt.AdaptiveDecisions.Count >= MaxDecisions || attempt.StatusObservations == null ||
            attempt.StatusObservations.Count >= 4096)
            return Unknown("The recorded status or decision trace may have reached its recording limit; coverage is unknown.");
        if (!SamePlan(result.Plan, attempt.Plan, result.TerritoryId))
            return Unknown("The tested plan is incompatible with the frozen recorded plan: rules, enabled flags or destination boards changed.");
        return Compare(result, attempt.AdaptiveDecisions, recorded: true);
    }

    /// <summary>Expectations must be independently reviewed and bound to this result by the calling session.</summary>
    public static List<PullComparisonRow> CompareExpected(PullValidationResult result, IReadOnlyList<PullExpectedAssignment> expected)
    {
        if (Readiness(result) is { } unavailable) return Unknown(unavailable);
        if (expected == null || expected.Count == 0)
            return Unknown("No independently reviewed expected assignments were supplied.");
        if (expected.Count > MaxDecisions || expected.Any(e => e == null))
            return Unknown("The reviewed expectations are unavailable or exceed the comparison limit.");
        var reference = new List<AdaptiveDecision>();
        foreach (var e in expected)
        {
            var rules = result.Plan.AdaptiveMechanics.Where(r => r.Id == e.RuleId).ToArray();
            var slide = rules.Length == 1 && e.BranchIndex >= 0 && e.BranchIndex < rules[0].Branches.Count
                ? rules[0].Branches[e.BranchIndex].SlideId : "";
            reference.Add(new AdaptiveDecision
            {
                RuleId = e.RuleId, AnchorActionId = e.AnchorActionId, Occurrence = e.Occurrence,
                BranchIndex = e.BranchIndex, Conflict = e.Conflict, SlideId = slide,
            });
        }
        return Compare(result, reference, recorded: false);
    }

    private static string? Readiness(PullValidationResult? result)
    {
        if (result == null || !result.ScopeVerified ||
            (result.HasOccurrenceReadiness ? !result.EvidenceUsable : !result.Complete))
            return "The validation is incomplete or its evidence scope is unverified; agreement remains unknown.";
        if (string.IsNullOrEmpty(result.AttemptId) || result.ActorId <= 0 || result.TerritoryId == 0 ||
            !float.IsFinite(result.Duration) || result.Duration < 0 || result.Decisions.Count > MaxDecisions)
            return "The validation lacks a bounded, identified actor, territory or recording duration.";
        if (result.Plan == null || !RulesAvailable(result.Plan) ||
            result.Plan.Slides == null || result.Plan.Slides.Any(s => s == null || !BoardAvailable(s)))
            return "The tested plan's rule or destination snapshot is unavailable or malformed.";
        if (result.HasOccurrenceReadiness && (result.Occurrences.Count > ReplayBuffer.MaxCasts || result.Occurrences.Any(o => o == null)))
            return "The occurrence coverage is unavailable or exceeds its recording limit.";
        return null;
    }

    private static List<PullComparisonRow> Compare(PullValidationResult result,
        IReadOnlyList<AdaptiveDecision> reference, bool recorded)
    {
        if (result.Decisions.Any(d => d == null) || reference.Any(d => d == null))
            return Unknown("A decision is unavailable; exact assignment identity cannot be compared.");
        var simulated = result.Decisions.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToArray());
        var references = reference.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToArray());
        var rows = new List<PullComparisonRow>();
        var occurrences = result.HasOccurrenceReadiness
            ? result.Occurrences.GroupBy(o => new Key(o.RuleId, o.AnchorActionId, o.Occurrence)).ToDictionary(g => g.Key, g => g.ToArray())
            : new Dictionary<Key, PullValidationOccurrence[]>();
        foreach (var key in simulated.Keys.Union(references.Keys).Union(occurrences.Keys).OrderBy(k => k.Action)
                     .ThenBy(k => k.Occurrence).ThenBy(k => k.RuleId, StringComparer.Ordinal))
        {
            var simulations = simulated.GetValueOrDefault(key) ?? Array.Empty<AdaptiveDecision>();
            var originals = references.GetValueOrDefault(key) ?? Array.Empty<AdaptiveDecision>();
            var s = simulations.Length == 1 ? simulations[0] : null;
            var r = originals.Length == 1 ? originals[0] : null;
            var row = Row(key, s, r, recorded);
            rows.Add(row);
            if (result.HasOccurrenceReadiness && OccurrenceReadiness(result, occurrences.GetValueOrDefault(key)) is { } unavailable)
                Set(row, PullComparisonOutcome.Unknown, unavailable);
            else if (simulations.Length > 1 || originals.Length > 1)
                Set(row, PullComparisonOutcome.Unknown, "Duplicate rule/action/occurrence decisions make this comparison ambiguous.");
            else if (s != null && !ValidDecision(result, s) || r != null && !ValidDecision(result, r))
                Set(row, PullComparisonOutcome.Unknown, "The rule, action, occurrence, branch or decision time is missing or incompatible with the tested plan.");
            else if (s != null && !ValidDestination(result.Plan, s) || r != null && !ValidDestination(result.Plan, r))
                Set(row, PullComparisonOutcome.Unknown, "A decision's destination is inconsistent with its frozen plan branch.");
            else if (s == null)
                Set(row, PullComparisonOutcome.Missing, recorded
                    ? "The recorded assignment outcome is missing from the simulation."
                    : "The reviewed expected assignment outcome is missing from the simulation.");
            else if (r == null)
                Set(row, recorded ? PullComparisonOutcome.Unexpected : PullComparisonOutcome.Unknown, recorded
                    ? "The simulation produced an assignment outcome without a corresponding recorded decision."
                    : "This occurrence has no reviewed expectation; partial expectations do not establish coverage of the whole pull.");
            else if (s.BranchIndex != r.BranchIndex || s.Conflict != r.Conflict || s.SlideId != r.SlideId)
                Set(row, PullComparisonOutcome.Different, Difference(s, r));
            else
                Set(row, PullComparisonOutcome.Matched, recorded
                    ? "Assignment outcomes agree. The timing delta and recorded navigation are separate observations; live UI equivalence is not established."
                    : "The simulated assignment outcome agrees with the independently reviewed expectation.");
        }
        return rows.Count > 0 ? rows : Unknown("No assignment outcomes are available to compare.");
    }

    private static string? OccurrenceReadiness(PullValidationResult result, PullValidationOccurrence[]? entries)
    {
        if (entries?.Length != 1)
            return "This rule/action/occurrence has no unique armed assignment window; coverage remains unknown.";
        var occurrence = entries[0];
        if (!float.IsFinite(occurrence.StartTime) || occurrence.StartTime < 0 ||
            !float.IsFinite(occurrence.Deadline) || occurrence.Deadline < 0 ||
            !float.IsFinite(occurrence.EndTime) || occurrence.EndTime < occurrence.StartTime || occurrence.EndTime > result.Duration)
            return "This assignment window has invalid timing; coverage remains unknown.";
        if (!occurrence.Complete || occurrence.Reasons.Count > 0)
            return occurrence.Reasons.Count > 0 ? string.Join(" ", occurrence.Reasons) : "This assignment window has incomplete evidence.";
        return null;
    }

    private static bool ValidDecision(PullValidationResult result, AdaptiveDecision d)
    {
        if (string.IsNullOrEmpty(d.RuleId) || d.AnchorActionId == 0 || d.Occurrence <= 0 ||
            !float.IsFinite(d.Time) || d.Time < 0 || d.Time > result.Duration || d.BranchIndex < -1)
            return false;
        var rules = result.Plan.AdaptiveMechanics.Where(r => r.Id == d.RuleId).ToArray();
        if (rules.Length != 1) return false;
        var rule = rules[0];
        return rule.Enabled && rule.TerritoryId == result.TerritoryId && rule.AnchorActionId == d.AnchorActionId &&
            (rule.Occurrence == 0 || rule.Occurrence == d.Occurrence) && rule.IsValid(result.Plan) &&
            d.BranchIndex < rule.Branches.Count && (d.BranchIndex < 0 ? string.IsNullOrEmpty(d.SlideId) : !d.Conflict);
    }

    private static bool ValidDestination(PlanDocument plan, AdaptiveDecision d) => d.BranchIndex < 0 ||
        plan.AdaptiveMechanics.Single(r => r.Id == d.RuleId).Branches[d.BranchIndex].SlideId == d.SlideId &&
        plan.Slides.Count(s => s.Id == d.SlideId) == 1;

    private static bool SamePlan(PlanDocument tested, PlanDocument recorded, uint territory)
    {
        if (recorded == null || !RulesAvailable(recorded) || recorded.Slides == null || recorded.Arena == null ||
            recorded.Roster == null || string.IsNullOrEmpty(tested.Id) || tested.Id != recorded.Id ||
            tested.AdaptiveMechanics.Count != recorded.AdaptiveMechanics.Count ||
            tested.AdaptiveMechanics.Any(r => string.IsNullOrEmpty(r.Id)) ||
            tested.AdaptiveMechanics.Select(r => r.Id).Distinct().Count() != tested.AdaptiveMechanics.Count)
            return false;
        // Compare serialized model semantics, including future rule/board fields. Session-only canvas and
        // roster IDs intentionally do not serialize; unrelated plan metadata and timeline edits are excluded.
        if (!JToken.DeepEquals(JToken.FromObject(tested.AdaptiveMechanics), JToken.FromObject(recorded.AdaptiveMechanics)))
            return false;
        var destinations = tested.AdaptiveMechanics.Where(r => r.Enabled && r.TerritoryId == territory)
            .SelectMany(r => r.Branches).Select(b => b.SlideId)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (var id in destinations)
        {
            var current = tested.Slides.Where(s => s.Id == id).ToArray();
            var original = recorded.Slides.Where(s => s != null && s.Id == id).ToArray();
            if (current.Length != 1 || original.Length != 1 || !BoardAvailable(original[0]) ||
                !JToken.DeepEquals(JToken.FromObject(current[0]), JToken.FromObject(original[0]))) return false;
        }
        return tested.Arena != null && tested.Roster != null &&
            JToken.DeepEquals(JToken.FromObject(tested.Arena), JToken.FromObject(recorded.Arena)) &&
            JToken.DeepEquals(JToken.FromObject(tested.Roster), JToken.FromObject(recorded.Roster));
    }

    private static bool BoardAvailable(Slide slide) => slide.Items != null &&
        slide.Items.All(i => i != null && i.Points != null);

    private static bool RulesAvailable(PlanDocument plan) => plan.AdaptiveMechanics != null &&
        plan.AdaptiveMechanics.Count <= 128 && plan.AdaptiveMechanics.All(r => r != null && r.Branches != null &&
            r.Branches.Count <= 16 && r.Branches.All(b => b != null && b.AdditionalStatuses != null &&
                b.AdditionalStatuses.Count <= 3 && b.AdditionalStatuses.All(c => c != null)));

    private static Key Identity(AdaptiveDecision decision) => new(decision.RuleId ?? "", decision.AnchorActionId, decision.Occurrence);

    private static PullComparisonRow Row(Key key, AdaptiveDecision? simulated, AdaptiveDecision? reference, bool recorded) => new()
    {
        RuleId = key.RuleId, AnchorActionId = key.Action, Occurrence = key.Occurrence,
        SimulatedTime = simulated?.Time, ReferenceTime = recorded ? reference?.Time : null,
        TimeDelta = recorded && simulated != null && reference != null && float.IsFinite(simulated.Time) && float.IsFinite(reference.Time)
            ? simulated.Time - reference.Time : null,
        SimulatedBranchIndex = simulated?.BranchIndex, ReferenceBranchIndex = reference?.BranchIndex,
        SimulatedSlideId = simulated?.SlideId ?? "", ReferenceSlideId = reference?.SlideId ?? "",
        SimulatedConflict = simulated?.Conflict, ReferenceConflict = reference?.Conflict,
        RecordedApplied = recorded ? reference?.Applied : null,
        RecordedNavigation = recorded ? reference?.Navigation ?? "" : "",
    };

    private static string Difference(AdaptiveDecision simulated, AdaptiveDecision reference)
    {
        var differences = new List<string>();
        if (simulated.BranchIndex != reference.BranchIndex) differences.Add("branch selection");
        if (simulated.Conflict != reference.Conflict) differences.Add("conflict outcome");
        if (simulated.SlideId != reference.SlideId) differences.Add("destination board");
        return "Assignment outcomes differ: " + string.Join(", ", differences) + ".";
    }

    private static void Set(PullComparisonRow row, PullComparisonOutcome outcome, string reason)
    { row.Outcome = outcome; row.Reason = reason; }

    private static List<PullComparisonRow> Unknown(string reason) => new()
    { new PullComparisonRow { Outcome = PullComparisonOutcome.Unknown, Reason = reason } };
}
