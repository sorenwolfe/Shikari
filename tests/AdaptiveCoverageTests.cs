using System;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Adaptive;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class AdaptiveCoverageTests
{
    public static void Benchmark()
    {
        foreach (var sample in new[] { ("Typical mapped", 10, 300, 1, false), ("Upper retained mapped", 30, 1800, 1, false),
            ("Unknown assignment properties", 30, 1800, 1, true), ("Maximum actor/cast examples", 1, 1800, 64, false) })
        {
            var (plan, rule) = Strategy(); rule.WindowSeconds = 60; rule.Occurrence = 0;
            for (var actor = 1; actor <= 8; actor++)
            {
                plan.Roster[actor - 1].Name = "Player " + actor; plan.Roster[actor - 1].JobId = 24;
                if (actor > 1) plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken,
                    SlotIndex = actor - 1, Position = new(.5f, .5f) });
            }
            var recordings = new System.Collections.Generic.List<ReplayAttempt>();
            for (var index = 0; index < sample.Item2; index++)
            {
                var attempt = Recording(plan, sample.Item3);
                attempt.Evidence.Actors.Clear(); attempt.Evidence.Statuses.Clear(); attempt.Evidence.Positions.Clear();
                attempt.Mechanics.Clear();
                for (var occurrence = 1; occurrence <= sample.Item4; occurrence++)
                    attempt.Mechanics.Add(new ReplayMechanic { ActionId = 800, Occurrence = occurrence,
                        Time = 1 + (occurrence - 1) * 20, ExpectedResolve = 4 + (occurrence - 1) * 20 });
                attempt.Evidence.CalibrationSlideId = plan.Slides[0].Id;
                attempt.Evidence.References.AddRange(new[] {
                    new EvidenceReference { Source = new(0,0), Board = new(0,0) },
                    new EvidenceReference { Source = new(1,0), Board = new(1,0) },
                    new EvidenceReference { Source = new(0,1), Board = new(0,1) } });
                for (var actor = 1; actor <= 8; actor++)
                {
                    attempt.Evidence.Actors.Add(new EvidenceActor { Id = actor, SlotIndex = actor - 1, Name = "Player " + actor, JobId = 24 });
                    foreach (var anchor in attempt.Mechanics)
                        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = actor, StatusId = 10, Time = anchor.Time + 1,
                            Duration = sample.Item5 ? null : 30 });
                    for (var second = 0; second < sample.Item3; second++)
                    {
                        if (second % 2 == 0) attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = actor, StatusId = 999, Time = second, Duration = 1 });
                        for (var tenth = 0; tenth < 10; tenth++)
                            attempt.Evidence.Positions.Add(new EvidencePosition { ActorId = actor, Time = second + tenth / 10f, Position = new(.5f,.5f) });
                    }
                }
                recordings.Add(attempt);
            }
            var steps = new System.Collections.Generic.List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var iterator = AdaptiveEvidenceAudit.AnalyseIncrementally(plan, rule, recordings).GetEnumerator();
            AdaptiveCoverage report = new();
            while (true)
            {
                var step = System.Diagnostics.Stopwatch.StartNew();
                var moved = iterator.MoveNext(); steps.Add(step.Elapsed.TotalMilliseconds);
                if (!moved) break;
                report = iterator.Current;
            }
            Console.WriteLine($"{sample.Item1}: {clock.Elapsed.TotalMilliseconds:0.0} ms total, {steps.Max():0.0} ms slowest step; {report.Recordings} recordings, {report.Examples.Count} examples, {recordings.Sum(r => r.Evidence.Positions.Count)} position samples");
        }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static (PlanDocument Plan, AdaptiveMechanic Rule) Strategy()
    {
        var plan = PlanDocument.CreateDefault();
        plan.Slides[0].Items.Clear();
        plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(.5f, .5f) });
        plan.Roster[0].Name = "Local Player"; plan.Roster[0].JobId = 24;
        var rule = new AdaptiveMechanic { TerritoryId = 100, AnchorActionId = 800, Occurrence = 1, WindowSeconds = 5 };
        rule.Branches.Add(new StatusBranch { Label = "Short", StatusId = 10, MinimumSeconds = 0, MaximumSeconds = 20, SlideId = plan.Slides[0].Id });
        rule.Branches.Add(new StatusBranch { Label = "Long", StatusId = 10, MinimumSeconds = 20, MaximumSeconds = 60, SlideId = plan.Slides[0].Id });
        plan.AdaptiveMechanics.Add(rule);
        return (plan, rule);
    }
    static ReplayAttempt Recording(PlanDocument plan, float duration = 10)
    {
        var attempt = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
        attempt.Duration = duration; attempt.TerritoryId = 100;
        attempt.Mechanics.Add(new ReplayMechanic { ActionId = 800, Occurrence = 1, Time = 1, ExpectedResolve = 4, SlideId = plan.Slides[0].Id });
        attempt.Evidence.Actors.Add(new EvidenceActor { Id = 42, Name = "Local Player", SlotIndex = 0, JobId = 24, IsLocal = true });
        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 42, SourceId = 77, StatusId = 10, Time = 2, Duration = 30 });
        attempt.Frames.Add(new ReplayFrame { Time = 4, SlideId = plan.Slides[0].Id, Valid = true, BoardPerYalm = .01f,
            Players = new() { new ReplayPlayer { SlotIndex = 0, Board = new(.5f,.5f), IsLocal = true } } });
        return attempt;
    }
    public static void Run()
    {
        var (plan, rule) = Strategy();
        Check(JsonConvert.DeserializeObject<AdaptiveDecision>("{\"Time\":1}")!.BranchIndex == -1,
            "Old recorded decisions without branch attribution must remain unknown, never branch zero.");
        var first = Recording(plan);
        var invalidAttribution = Recording(plan);
        invalidAttribution.AdaptiveDecisions.Add(new AdaptiveDecision { Time = 2, BranchIndex = 16 });
        Check(!ReplayValidation.IsValid(invalidAttribution), "Out-of-range recorded branch attribution must fail replay validation.");
        var decisions = EvidenceRules.Simulate(first, 42, rule);
        Check(decisions.Count == 1 && decisions[0].BranchIndex == 1 && decisions[0].RuleId == rule.Id,
            "The real evaluator must identify a branch even when alternatives share a destination.");
        var repeated = Recording(plan, 20);
        repeated.Mechanics.Add(new ReplayMechanic { ActionId = 800, Occurrence = 2, Time = 11, ExpectedResolve = 14 });
        repeated.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 42, StatusId = 10, Time = 12, Duration = 10 });
        var eachCast = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!; eachCast.Occurrence = 0;
        var repeatedDecisions = EvidenceRules.Simulate(repeated, 42, eachCast);
        Check(repeatedDecisions.Count == 2 && repeatedDecisions[0].BranchIndex == 1 && repeatedDecisions[1].BranchIndex == 0,
            "Completing one simulated occurrence must leave later casts able to produce their own assignment.");
        var beforePlan = JsonConvert.SerializeObject(plan); var beforeAttempt = JsonConvert.SerializeObject(first);
        var result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { first });
        Check(result.Branches.Count == 2 && result.Branches[1].Recordings == 1 && result.Branches[0].Recordings == 0 && result.Examples.Single().Outcome == AssignmentOutcome.Reproduced,
            "Branch coverage counts reproduced conditions and missing alternatives separately.");
        Check(result.Examples.Single().Distance == 0, "A fresh calibrated local frame can report observed proximity separately.");
        Check(JsonConvert.SerializeObject(plan) == beforePlan && JsonConvert.SerializeObject(first) == beforeAttempt && !rule.Enabled,
            "Auditing must not modify authored rules or recordings.");
        first.Evidence.Actors.Add(new EvidenceActor { Id = 43, SlotIndex = -1, JobId = 24 });
        first.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 43, StatusId = 10, Time = 2, Duration = 30 });
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { first });
        Check(result.Branches[1].Recordings == 1 && result.Branches[1].Examples == 2,
            "Several actors in one recording do not become independent pull evidence.");

        var log = Recording(plan); log.Evidence.Source = "FF Logs"; log.Evidence.ReportCode = "ABC123"; log.Evidence.FightId = 9;
        var duplicate = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(log))!; duplicate.Id = Guid.NewGuid().ToString("N");
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { log, duplicate });
        Check(result.Recordings == 1 && result.Duplicates == 1 && result.Branches[1].Recordings == 1,
            "Reimported FF Logs report/fight pairs must count once.");
        duplicate.Evidence.FightId = 10; duplicate.Evidence.Statuses[0].Duration = 10;
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { log, duplicate });
        Check(result.Branches[0].Recordings == 1 && result.Branches[1].Recordings == 1 && result.Examples.Count == 2,
            "Actor IDs are scoped to each recording, including when different fights reuse the same ID.");

        foreach (var mode in new[] { "baseline", "gap", "partial", "short", "unknown-status" })
        {
            var sample = Recording(plan, mode == "short" ? 3 : 10);
            if (mode == "short") sample.Frames.Clear();
            if (mode == "baseline") sample.Evidence.Statuses[0].Baseline = true;
            if (mode == "gap") sample.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 42, Time = 3, Change = "unavailable" });
            if (mode == "partial") sample.Evidence.Complete = false;
            if (mode == "unknown-status") sample.Evidence.Statuses[0].StatusId = 0;
            result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { sample });
            Check(result.Branches.Sum(b => b.Recordings) == 0 && result.Examples.All(e => e.Outcome == AssignmentOutcome.Unknown),
                mode + " recording must remain unknown, not successful or contradictory.");
        }
        var conflict = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!;
        var unknownDuration = Recording(plan); unknownDuration.Evidence.Statuses[0].Duration = null;
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { unknownDuration });
        Check(result.Examples.Single().Outcome == AssignmentOutcome.Unknown, "Missing initial duration cannot count as a rejected duration-dependent assignment.");
        var unknownParameter = Recording(plan);
        var requiresParameter = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!;
        requiresParameter.Branches[1].Parameter = 2;
        result = AdaptiveEvidenceAudit.Analyse(plan, requiresParameter, new[] { unknownParameter });
        Check(result.Examples.Single().Outcome == AssignmentOutcome.Unknown, "Missing status parameter cannot count as a rejected parameter-dependent assignment.");
        var acceptsUnknown = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!;
        acceptsUnknown.Branches.RemoveAt(0); acceptsUnknown.Branches[0].MinimumSeconds = 0; acceptsUnknown.Branches[0].MaximumSeconds = 3600;
        result = AdaptiveEvidenceAudit.Analyse(plan, acceptsUnknown, new[] { unknownDuration });
        Check(result.Examples.Single().Outcome == AssignmentOutcome.Reproduced, "Explicit any-duration and any-parameter conditions can reproduce with unknown fields.");
        conflict.Branches[0].MaximumSeconds = 60;
        result = AdaptiveEvidenceAudit.Analyse(plan, conflict, new[] { Recording(plan) });
        Check(result.Examples.Single().Outcome == AssignmentOutcome.Conflict && result.Branches.Sum(b => b.Recordings) == 0,
            "Two matching branches conflict even if their destination is identical.");
        var edited = Recording(plan); plan.Slides[0].Items[0].Position = new(.1f, .1f);
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { edited });
        Check(result.Examples.Single().Outcome == AssignmentOutcome.Reproduced && result.Examples.Single().Distance == null,
            "Edited geometry invalidates proximity without erasing a recorded status condition.");
        plan.Slides[0].Items[0].Position = new(.5f, .5f);
        plan.Roster[0].Name = "Replacement player";
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { edited });
        Check(result.Examples.Single().Distance == null, "Changed seat identity cannot inherit another actor's position comparison.");
        plan.Roster[0].Name = "Local Player";
        var unaligned = Recording(plan); unaligned.Frames.Clear();
        unaligned.Evidence.Positions.Add(new EvidencePosition { ActorId = 42, Time = 4, Position = new(50, 50) });
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { unaligned });
        Check(result.Examples.Single().Distance == null, "Raw uncalibrated coordinates cannot become board distances.");
        var calibrated = Recording(plan); calibrated.Frames.Clear();
        calibrated.Evidence.CalibrationSlideId = plan.Slides[0].Id;
        calibrated.Evidence.References.AddRange(new[] {
            new EvidenceReference { Source = new(0,0), Board = new(0,0) },
            new EvidenceReference { Source = new(1,0), Board = new(1,0) },
            new EvidenceReference { Source = new(0,1), Board = new(0,1) } });
        calibrated.Evidence.Positions.AddRange(new[] {
            new EvidencePosition { ActorId = 42, Time = 4.1f, Position = new(1,1) },
            new EvidencePosition { ActorId = 43, Time = 4, Position = new(1,1) },
            new EvidencePosition { ActorId = 42, Time = 3.8f, Position = new(.5f,.5f) },
            new EvidencePosition { ActorId = 42, Time = 2, Position = new(1,1) } });
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { calibrated });
        Check(result.Examples.Single().Distance == 0, "Proximity chooses the latest fresh sample at or before resolve, scoped to the exact actor even with unsorted input.");
        calibrated.Evidence.Positions.RemoveAt(2);
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { calibrated });
        Check(result.Examples.Single().Distance == null, "A future or stale position cannot stand in for an observation at resolve.");
        unaligned.TerritoryId = 101;
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { unaligned });
        Check(result.ExcludedRecordings == 1 && result.Examples.Count == 0, "Wrong encounter territory must be excluded.");
        unaligned.TerritoryId = 0; unaligned.Evidence.Source = "FF Logs"; unaligned.Evidence.ReportCode = "UNKNOWN"; unaligned.Evidence.FightId = 1;
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { unaligned });
        Check(result.Branches.Sum(b => b.Recordings) == 0 && result.Examples.Single().ScopeVerified == false,
            "Unverified encounter observations cannot count as confirmed coverage.");
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, Array.Empty<ReplayAttempt>());
        Check(result.Recordings == 0 && result.Branches.Sum(b => b.Recordings) == 0, "Deleted recordings must not leave accumulated support behind.");
        var changedRule = JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!;
        changedRule.Branches[1].MaximumSeconds = 25;
        result = AdaptiveEvidenceAudit.Analyse(plan, changedRule, new[] { Recording(plan) });
        Check(result.Branches.Sum(b => b.Recordings) == 0, "Rule changes must be evaluated against raw evidence again.");
        var wrongEncounter = Recording(plan); wrongEncounter.Evidence.Source = "FF Logs";
        wrongEncounter.Evidence.ReportCode = "WRONG"; wrongEncounter.Evidence.FightId = 1; wrongEncounter.Evidence.EncounterId = 901;
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { Key = "known", EncounterVerified = true, EncounterId = 900, TerritoryId = 100 });
        result = AdaptiveEvidenceAudit.Analyse(plan, rule, new[] { wrongEncounter });
        Check(result.ExcludedRecordings == 1 && result.Examples.Count == 0,
            "A matching territory tag cannot overrule a contradictory verified FF Logs encounter ID.");
        Check(EvidenceRules.ContainsDraft(plan, JsonConvert.DeserializeObject<AdaptiveMechanic>(JsonConvert.SerializeObject(rule))!),
            "Different IDs must not hide an exact duplicate draft.");
        changedRule.Branches[1].SlideId = "different";
        Check(!EvidenceRules.ContainsDraft(plan, changedRule), "Different predicates or destinations must remain distinct candidates.");
        Console.WriteLine("Adaptive assignment coverage checks passed.");
    }
}
