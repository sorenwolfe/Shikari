using System;
using System.Collections.Generic;
using System.Linq;
using Shikari.Model;

namespace Shikari.Services.Adaptive;

public readonly record struct StatusSample(uint Id, float Remaining, ushort Parameter, uint Source);

/// <summary>Absence of a readable actor invalidates the baseline; it never means status loss.</summary>
public sealed class StatusTracker
{
    private Dictionary<(uint, uint), StatusSample> previous = new();
    private bool ready;
    public void Invalidate() { previous.Clear(); ready = false; }
    public List<StatusObservation> Observe(IEnumerable<StatusSample> snapshot, float time)
    {
        var current = new Dictionary<(uint, uint), StatusSample>();
        var result = new List<StatusObservation>();
        foreach (var sample in snapshot.Take(128))
        {
            if (sample.Id == 0 || !float.IsFinite(sample.Remaining) || sample.Remaining < 0) continue;
            var key = (sample.Id, sample.Source);
            current[key] = sample;
            if (ready && (!previous.TryGetValue(key, out var old) || old.Parameter != sample.Parameter ||
                sample.Remaining > old.Remaining + 1))
                result.Add(new StatusObservation { Time = time, StatusId = sample.Id, Duration = sample.Remaining,
                    DurationKnown = sample.Remaining > 0, Parameter = sample.Parameter, SourceId = sample.Source });
        }
        if (ready)
            foreach (var (key, sample) in previous)
                if (!current.ContainsKey(key))
                    result.Add(new StatusObservation { Time = time, StatusId = sample.Id,
                        Parameter = sample.Parameter, SourceId = sample.Source, Removed = true, DurationKnown = false });
        previous = current;
        ready = true;
        return result;
    }
}

/// <summary>Caller supplies a frozen plan. This evaluator never reads game state or changes slides.</summary>
public sealed class AdaptiveEngine
{
    private sealed class Armed
    {
        public required AdaptiveMechanic Rule;
        public float Start;
        public int Occurrence;
        public float SettledSince = -1;
        public readonly Dictionary<(uint, uint), StatusObservation> Statuses = new();
        public HashSet<int> Matches = new();
    }
    private readonly List<AdaptiveMechanic> rules;
    private readonly List<Armed> armed = new();
    public int ActiveRuleCount => rules.Count;
    /// <summary>Unreadable or replaced actors cannot carry pending assignments across a gap.</summary>
    public void InvalidateEvidence()
    {
        foreach (var state in armed)
        {
            state.Statuses.Clear();
            state.Matches.Clear();
            state.SettledSince = -1;
        }
    }

    public AdaptiveEngine(PlanDocument plan, uint territory)
    {
        var candidates = plan.AdaptiveMechanics.Take(128)
            .Where(r => r != null && r.Enabled && r.TerritoryId == territory && r.IsValid(plan)).ToList();
        // Alternatives belong to a single rule; overlapping rules cannot make independent
        // decisions at different times and silently replace one another's assignment.
        rules = candidates.Where(r => candidates.Count(other => r.Overlaps(other)) == 1).ToList();
    }

    public void Arm(uint action, int occurrence, float time)
    {
        if (!float.IsFinite(time) || time < 0) return;
        foreach (var rule in rules.Where(r => r.AnchorActionId == action && (r.Occurrence == 0 || r.Occurrence == occurrence)))
        {
            armed.RemoveAll(a => a.Rule == rule);
            armed.Add(new Armed { Rule = rule, Start = time, Occurrence = occurrence });
        }
    }

    public List<AdaptiveDecision> Update(IReadOnlyList<StatusObservation> observations, float time)
    {
        var decisions = new List<AdaptiveDecision>();
        if (!float.IsFinite(time) || time < 0) return decisions;
        foreach (var state in armed.ToArray())
        {
            var deadline = state.Start + state.Rule.WindowSeconds;
            var changed = false;
            foreach (var observed in observations)
            {
                if (observed == null || !float.IsFinite(observed.Time) ||
                    (observed.DurationKnown && (!float.IsFinite(observed.Duration) || observed.Duration < 0))) continue;
                if (observed.Time < state.Start || observed.Time > time) continue;
                if (!state.Rule.Branches.Any(b => b.StatusId == observed.StatusId ||
                    b.AdditionalStatuses.Any(c => c.StatusId == observed.StatusId))) continue;
                var key = (observed.StatusId, observed.SourceId);
                // A late removal/refresh/change can invalidate an existing assignment, but cannot
                // acquire a new condition after the window. Retain it as a tombstone for old deltas.
                if (observed.Time > deadline && !state.Statuses.ContainsKey(key)) continue;
                if (state.Statuses.TryGetValue(key, out var prior) &&
                    (prior.Time > observed.Time || SameObservation(prior, observed))) continue;
                state.Statuses[key] = observed;
                changed = true;
            }
            var eligible = state.Statuses.Values.Where(o => o.Time <= deadline).ToArray();
            var matches = Enumerable.Range(0, state.Rule.Branches.Count)
                .Where(i => Matches(state.Rule.Branches[i], eligible, time)).ToHashSet();
            changed |= !matches.SetEquals(state.Matches);
            state.Matches = matches;
            if (matches.Count == 0) state.SettledSince = -1;
            else if (changed || state.SettledSince < 0) state.SettledSince = time;
            var expired = time >= deadline;
            var settled = state.SettledSince >= 0 && time - state.SettledSince >= .3f && state.SettledSince + .3f <= deadline;
            if (!expired && !settled) continue;
            var decision = new AdaptiveDecision { Time = time, Mechanic = state.Rule.Label,
                AnchorActionId = state.Rule.AnchorActionId, Occurrence = state.Occurrence,
                RuleId = state.Rule.Id, Conflict = state.Matches.Count > 1 };
            if (state.Matches.Count == 1 && settled)
            {
                var match = state.Matches.First();
                var b = state.Rule.Branches[match];
                decision.BranchIndex = match;
                decision.SlideId = b.SlideId;
                var evidence = eligible.First(o => Matches(b.StatusId, b.Parameter, b.MinimumSeconds, b.MaximumSeconds, o, time));
                decision.Reason = $"{b.Label}: status #{evidence.StatusId}, initial observed duration " +
                    (evidence.DurationKnown ? $"{evidence.Duration:0.0}s" : "unknown") + ", parameter " +
                    (evidence.ParameterKnown ? evidence.Parameter.ToString() : "unknown") + $", source #{evidence.SourceId}.";
                if (b.AdditionalStatuses.Count > 0)
                    decision.Reason += $" All {b.AdditionalStatuses.Count + 1} required statuses are active concurrently.";
            }
            else decision.Reason = state.Matches.Count == 0 ? "No matching status observed within the assignment window." :
                state.Matches.Count > 1 ? "Conflicting branches matched; no destination selected." :
                "The complete assignment did not settle before the window closed; no destination selected.";
            decisions.Add(decision);
            armed.Remove(state);
        }
        if (decisions.Where(d => d.SlideId.Length > 0).Select(d => d.SlideId).Distinct().Count() > 1)
            foreach (var d in decisions) { d.SlideId = ""; d.BranchIndex = -1; d.Conflict = true; d.Reason += " Conflicting mechanics; navigation withheld."; }
        return decisions;
    }

    private static bool Matches(StatusBranch branch, IEnumerable<StatusObservation> statuses, float time) =>
        statuses.Any(o => Matches(branch.StatusId, branch.Parameter, branch.MinimumSeconds, branch.MaximumSeconds, o, time)) &&
        branch.AdditionalStatuses.All(c => statuses.Any(o => Matches(c.StatusId, c.Parameter, c.MinimumSeconds, c.MaximumSeconds, o, time)));

    private static bool Matches(uint id, int parameter, float minimum, float maximum, StatusObservation observed, float time) =>
        observed.StatusId == id && !observed.Removed && !observed.Baseline &&
        (!observed.DurationKnown || observed.Time + observed.Duration > time) &&
        (parameter < 0 || observed.ParameterKnown && observed.Parameter == parameter) &&
        (observed.DurationKnown ? observed.Duration >= minimum && observed.Duration < maximum : minimum == 0 && maximum == 3600);

    private static bool SameObservation(StatusObservation a, StatusObservation b) =>
        a.Time == b.Time && a.Duration == b.Duration && a.Parameter == b.Parameter && a.Removed == b.Removed &&
        a.Baseline == b.Baseline && a.DurationKnown == b.DurationKnown && a.ParameterKnown == b.ParameterKnown;
}
