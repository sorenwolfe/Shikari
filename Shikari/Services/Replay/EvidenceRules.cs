using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Adaptive;

namespace Shikari.Services.Replay;

public static class EvidenceRules
{
    /// <summary>Source IDs and cosmetic labels do not make the same authored predicate a new rule.</summary>
    public static bool ContainsDraft(PlanDocument plan, AdaptiveMechanic draft) => plan.AdaptiveMechanics.Any(existing =>
        existing.TerritoryId == draft.TerritoryId && existing.AnchorActionId == draft.AnchorActionId &&
        existing.Occurrence == draft.Occurrence && existing.WindowSeconds == draft.WindowSeconds &&
        draft.Branches.All(branch => existing.Branches.Any(candidate => SameBranch(candidate, branch))));

    public static bool SameBranch(StatusBranch left, StatusBranch right)
    {
        if (left.SlideId != right.SlideId) return false;
        static IEnumerable<(uint, int, float, float)> Conditions(StatusBranch branch) => branch.AdditionalStatuses
            .Select(c => (c.StatusId, c.Parameter, c.MinimumSeconds, c.MaximumSeconds))
            .Append((branch.StatusId, branch.Parameter, branch.MinimumSeconds, branch.MaximumSeconds))
            .OrderBy(c => c.Item1).ThenBy(c => c.Item2).ThenBy(c => c.Item3).ThenBy(c => c.Item4);
        return Conditions(left).SequenceEqual(Conditions(right));
    }
    public static AdaptiveMechanic Draft(ReplayAttempt attempt, ReplayMechanic anchor,
        IReadOnlyList<EvidenceStatus> statuses, string slideId, uint territory)
    {
        if (!attempt.Evidence.Complete) throw new InvalidOperationException("Choose a complete recording before drafting a live rule.");
        if (anchor.ActionId == 0 || territory == 0 || attempt.Plan.FindSlide(slideId) == null ||
            statuses.Count is < 1 or > 4 || statuses.Select(s => s.ActorId).Distinct().Count() != 1 ||
            statuses.Any(s => s.StatusId == 0 || s.Baseline || s.Change is "remove" or "unavailable" ||
                s.Time < anchor.Time || s.Time > anchor.Time + 59))
            throw new InvalidOperationException("Choose 1–4 fresh, verified statuses from one player after this cast, a territory, and a destination slide.");
        if (statuses.Select(s => s.StatusId).Distinct().Count() != statuses.Count)
            throw new InvalidOperationException("Choose each status once.");
        var rule = new AdaptiveMechanic { Label = anchor.Label, AnchorActionId = anchor.ActionId,
            TerritoryId = territory, Occurrence = anchor.Occurrence, Enabled = false,
            WindowSeconds = Math.Clamp(statuses.Max(s => s.Time) - anchor.Time + 2, 1, 60) };
        StatusCondition Condition(EvidenceStatus s) => new() { StatusId = s.StatusId,
            Parameter = s.Parameter ?? -1,
            MinimumSeconds = s.Duration is > 0 and < 3598 ? Math.Max(0, s.Duration.Value - 2) : 0,
            MaximumSeconds = s.Duration is > 0 and < 3598 ? s.Duration.Value + 2 : 3600 };
        var first = Condition(statuses[0]);
        var branch = new StatusBranch { Label = string.Join(" + ", statuses.Select(s => s.Name.Length > 0 ? s.Name : "#" + s.StatusId)),
            StatusId = first.StatusId, Parameter = first.Parameter, MinimumSeconds = first.MinimumSeconds,
            MaximumSeconds = first.MaximumSeconds, SlideId = slideId };
        branch.AdditionalStatuses.AddRange(statuses.Skip(1).Select(Condition));
        rule.Branches.Add(branch);
        return rule;
    }

    public static IReadOnlyList<AdaptiveDecision> Simulate(ReplayAttempt attempt, long actorId, AdaptiveMechanic draft)
    {
        var rule = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(draft))!;
        rule.Enabled = true;
        var plan = new PlanDocument { Slides = attempt.Plan.Slides, AdaptiveMechanics = new() { rule } };
        var engine = new AdaptiveEngine(plan, rule.TerritoryId);
        var result = new List<AdaptiveDecision>();
        var relevant = rule.Branches.SelectMany(b => b.AdditionalStatuses.Select(c => c.StatusId).Append(b.StatusId)).ToHashSet();
        var events = attempt.Evidence.Statuses.Where(s => s.ActorId == actorId && (s.Change == "unavailable" || relevant.Contains(s.StatusId)))
            .OrderBy(s => s.Time).ToArray();
        foreach (var anchor in attempt.Mechanics.Where(m => m.ActionId == rule.AnchorActionId &&
                     (rule.Occurrence == 0 || m.Occurrence == rule.Occurrence)))
        {
            engine.Arm(anchor.ActionId, anchor.Occurrence, anchor.Time);
            var index = Array.FindIndex(events, s => s.Time >= anchor.Time);
            if (index < 0) index = events.Length;
            for (var tick = 0; tick <= (int)Math.Ceiling(rule.WindowSeconds * 10) + 1; tick++)
            {
                var time = anchor.Time + tick / 10f;
                if (time > attempt.Duration) break;
                var observations = new List<StatusObservation>();
                while (index < events.Length && events[index].Time <= time)
                {
                    var e = events[index++];
                    if (e.Change == "unavailable") { engine.InvalidateEvidence(); observations.Clear(); continue; }
                    if (e.StatusId == 0 || e.Change == "stacks") continue;
                    observations.Add(new StatusObservation { Time = e.Time, StatusId = e.StatusId,
                        SourceId = (uint)Math.Clamp(e.SourceId, 0, uint.MaxValue), Duration = e.Duration ?? 0,
                        DurationKnown = e.Duration.HasValue, Parameter = (ushort)(e.Parameter ?? 0),
                        ParameterKnown = e.Parameter.HasValue, Baseline = e.Baseline, Removed = e.Change == "remove" });
                }
                var decisions = engine.Update(observations, time);
                result.AddRange(decisions);
                // This simulation has one rule and one armed occurrence. Its state is removed
                // on delivery, so later ticks cannot produce another decision for this anchor.
                if (decisions.Count > 0) break;
            }
        }
        return result;
    }
}
