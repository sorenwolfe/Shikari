using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Replay;
using Shikari.Services.Storage;

namespace Shikari.Tests;

public static class PullValidationComparisonTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        AssignmentAgreementKeepsNavigationSeparate();
        ExactOutcomesAndKeys();
        MissingUnexpectedAndAmbiguous();
        RecordedScopeIsRequired();
        FullRuleAndDestinationCompatibility();
        ReviewedExpectationsAreIndependent();
        InvalidAndIncompleteEvidenceStaysUnknown();
        NoInputMutation();
        Console.WriteLine("PASS: exact assignment comparisons, timing deltas, recorded navigation separation, local actor and frozen-plan scope, reviewed expectations, ambiguity and incomplete evidence");
    }

    private static (PullValidationResult Result, ReplayAttempt Attempt) Fixture()
    {
        var plan = PlanDocument.CreateDefault("Original", 2);
        plan.Id = "plan";
        plan.Slides[0].Id = "north";
        plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new Vector2(.3f, .7f) });
        plan.Slides.Add(new Slide { Id = "south" });
        plan.AdaptiveMechanics.Add(new AdaptiveMechanic
        {
            Id = "rule", Label = "Choose side", Enabled = true, TerritoryId = 777, AnchorActionId = 100,
            Occurrence = 0, WindowSeconds = 10,
            Branches = new()
            {
                new() { Label = "North", StatusId = 10, SlideId = "north", MaximumSeconds = 3600,
                    AdditionalStatuses = new() { new() { StatusId = 12, MaximumSeconds = 3600 } } },
                new() { Label = "South", StatusId = 20, SlideId = "south", MaximumSeconds = 3600 },
            },
        });
        return (new PullValidationResult
        {
            Plan = PlanSnapshot.Copy(plan), AttemptId = "attempt", ActorId = 11, Duration = 30,
            TerritoryId = 777, Complete = true, ScopeVerified = true, ActiveRules = 1,
            Decisions = { Decision() },
        }, new ReplayAttempt
        {
            Id = "attempt", Plan = plan, TerritoryId = 777, Duration = 30, LocalSlot = 0,
            AdaptiveDecisions = new() { Decision(2.25f) },
            Evidence = new()
            {
                Source = "Local recording", Complete = true,
                Actors = new() { new() { Id = 11, IsLocal = true, SlotIndex = 0 }, new() { Id = 22, SlotIndex = 1 } },
            },
        });
    }

    private static AdaptiveDecision Decision(float time = 2) => new()
    {
        RuleId = "rule", AnchorActionId = 100, Occurrence = 1, Time = time,
        BranchIndex = 0, SlideId = "north", Mechanic = "Choose side", Reason = "Positive observation",
    };

    private static PullExpectedAssignment Expected(int branch = 0, bool conflict = false, int occurrence = 1) => new()
    {
        RuleId = "rule", AnchorActionId = 100, Occurrence = occurrence, BranchIndex = branch, Conflict = conflict,
    };

    private static PullValidationResult With(PullValidationResult source, long? actor = null, string? attempt = null,
        uint? territory = null, float? duration = null, bool? scope = null)
    {
        var copy = new PullValidationResult
        {
            Plan = source.Plan, ActorId = actor ?? source.ActorId, AttemptId = attempt ?? source.AttemptId,
            TerritoryId = territory ?? source.TerritoryId, Duration = duration ?? source.Duration,
            ScopeVerified = scope ?? source.ScopeVerified, Complete = source.Complete, ActiveRules = source.ActiveRules,
        };
        copy.Decisions.AddRange(source.Decisions);
        return copy;
    }

    private static PullComparisonRow Recorded((PullValidationResult Result, ReplayAttempt Attempt) f)
    {
        var rows = PullValidationComparison.CompareRecorded(f.Result, f.Attempt);
        Check(rows.Count == 1, "A single recorded assignment must produce one comparison row");
        return rows[0];
    }

    private static void AssignmentAgreementKeepsNavigationSeparate()
    {
        var f = Fixture();
        f.Result.Decisions[0].Applied = true;
        f.Attempt.AdaptiveDecisions[0].Applied = false;
        f.Attempt.AdaptiveDecisions[0].Navigation = "Editor visible; navigation withheld";
        var row = Recorded(f);
        Check(row.Outcome == PullComparisonOutcome.Matched, "Matching assignments must reproduce even when live navigation was withheld");
        Check(row.TimeDelta == -.25f && row.SimulatedTime == 2 && row.ReferenceTime == 2.25f,
            "Timing must be reported as simulation minus recorded time without claiming exact timing parity");
        Check(row.RecordedApplied == false && row.RecordedNavigation == "Editor visible; navigation withheld",
            "Actual recorded navigation must remain separate from the simulated assignment");
        Check(row.Reason.Length > 0, "Comparison rows need an explanation");
    }

    private static void ExactOutcomesAndKeys()
    {
        var f = Fixture();
        f.Result.Decisions[0].BranchIndex = 1; f.Result.Decisions[0].SlideId = "south";
        Check(Recorded(f).Outcome == PullComparisonOutcome.Different, "Another branch and destination must differ");
        f = Fixture();
        f.Result.Decisions[0].BranchIndex = -1; f.Result.Decisions[0].SlideId = ""; f.Result.Decisions[0].Conflict = true;
        Check(Recorded(f).Outcome == PullComparisonOutcome.Different, "Conflict and assigned destination are different outcomes");
        f.Attempt.AdaptiveDecisions[0].BranchIndex = -1; f.Attempt.AdaptiveDecisions[0].SlideId = "";
        Check(Recorded(f).Outcome == PullComparisonOutcome.Different, "No match must not be confused with conflict");
        f.Result.Decisions[0].Conflict = false;
        Check(Recorded(f).Outcome == PullComparisonOutcome.Matched, "Explicit recorded and simulated no-destination outcomes can reproduce");
        f = Fixture(); f.Result.Decisions[0].Occurrence = 2;
        var rows = PullValidationComparison.CompareRecorded(f.Result, f.Attempt);
        Check(rows.Count == 2 && rows.Any(r => r.Occurrence == 1 && r.Outcome == PullComparisonOutcome.Missing) &&
            rows.Any(r => r.Occurrence == 2 && r.Outcome == PullComparisonOutcome.Unexpected),
            "Action occurrence identities must not be aligned by nearby time or label");
        f = Fixture(); f.Result.Decisions[0].AnchorActionId = 101;
        Check(PullValidationComparison.CompareRecorded(f.Result, f.Attempt).All(r => r.Outcome != PullComparisonOutcome.Matched),
            "Another action must never match the recorded action");
        f = Fixture(); f.Result.Decisions[0].RuleId = "other-rule";
        Check(PullValidationComparison.CompareRecorded(f.Result, f.Attempt).All(r => r.Outcome != PullComparisonOutcome.Matched),
            "Another rule must never match by action, occurrence, label or destination alone");
    }

    private static void MissingUnexpectedAndAmbiguous()
    {
        var f = Fixture(); f.Result.Decisions.Clear();
        Check(Recorded(f).Outcome == PullComparisonOutcome.Missing, "A recorded decision absent from the simulation must be missing");
        f = Fixture(); f.Attempt.AdaptiveDecisions.Clear();
        Check(Recorded(f).Outcome == PullComparisonOutcome.Unknown, "An empty recorded trace is not proof that no live decision happened");
        f = Fixture(); f.Attempt.AdaptiveDecisions.Add(Decision());
        Check(Recorded(f).Outcome == PullComparisonOutcome.Unknown, "Duplicate recorded identities must not silently choose a decision");
        f = Fixture(); f.Result.Decisions.Add(Decision());
        Check(Recorded(f).Outcome == PullComparisonOutcome.Unknown, "Duplicate simulated identities must not claim reproduction");
    }

    private static void RecordedScopeIsRequired()
    {
        Action<ReplayAttempt>[] changes =
        {
            a => a.Evidence.Source = "FF Logs",
            a => a.TerritoryId = 0,
            a => a.Evidence.Actors[0].IsLocal = false,
            a => a.Evidence.Actors[1].IsLocal = true,
            a => a.Evidence.Actors.Add(new EvidenceActor { Id = 11 }),
            a => a.Evidence.Complete = false,
        };
        foreach (var change in changes)
        {
            var f = Fixture(); change(f.Attempt);
            Check(Recorded(f).Outcome == PullComparisonOutcome.Unknown, "Incompatible actor, source, pull, territory or recording coverage must stay unknown");
        }
        var original = Fixture();
        foreach (var changed in new[] { With(original.Result, actor: 22), With(original.Result, attempt: "another pull"),
                     With(original.Result, territory: 778), With(original.Result, territory: 0), With(original.Result, duration: 29) })
            Check(PullValidationComparison.CompareRecorded(changed, original.Attempt).Single().Outcome == PullComparisonOutcome.Unknown,
                "Another actor, pull, territory or recording duration must not establish recorded agreement");
    }

    private static void FullRuleAndDestinationCompatibility()
    {
        Action<PlanDocument>[] changes =
        {
            p => p.Id = "other plan",
            p => p.AdaptiveMechanics[0].Enabled = false,
            p => p.AdaptiveMechanics[0].TerritoryId = 778,
            p => p.AdaptiveMechanics[0].AnchorActionId = 101,
            p => p.AdaptiveMechanics[0].Occurrence = 1,
            p => p.AdaptiveMechanics[0].WindowSeconds = 11,
            p => p.AdaptiveMechanics[0].Branches[0].StatusId = 11,
            p => p.AdaptiveMechanics[0].Branches[0].Parameter = 2,
            p => p.AdaptiveMechanics[0].Branches[0].MinimumSeconds = 1,
            p => p.AdaptiveMechanics[0].Branches[0].MaximumSeconds = 20,
            p => p.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].StatusId = 13,
            p => p.AdaptiveMechanics[0].Branches[0].SlideId = "south",
            p => p.AdaptiveMechanics.Add(new AdaptiveMechanic { Id = "competitor", Enabled = false }),
            p => p.Slides.RemoveAt(0),
            p => p.Slides[0].Items[0].Position = Vector2.Zero,
            p => p.Slides[0].ArenaOverride = new ArenaSettings { Shape = ArenaShape.Rectangle },
            p => p.Arena.AspectRatio = 2,
            p => p.Roster[0].Placeholder = "Changed seat",
        };
        foreach (var change in changes)
        {
            var f = Fixture(); change(f.Result.Plan);
            var row = Recorded(f);
            Check(row.Outcome == PullComparisonOutcome.Unknown && row.Reason.Contains("plan", StringComparison.OrdinalIgnoreCase),
                "Changed frozen rules, competing rules or destination boards must report an incompatible plan");
        }
        var same = Fixture(); same.Result.Plan.Name = "Renamed"; same.Result.Plan.ModifiedUtc = DateTime.UtcNow.AddDays(1);
        Check(Recorded(same).Outcome == PullComparisonOutcome.Matched, "Unrelated plan metadata must not invalidate assignment semantics");
        same = Fixture();
        foreach (var plan in new[] { same.Result.Plan, same.Attempt.Plan })
            plan.AdaptiveMechanics.Add(new AdaptiveMechanic { Id = "disabled draft", Branches = new() { new StatusBranch() } });
        Check(Recorded(same).Outcome == PullComparisonOutcome.Matched,
            "An unchanged disabled draft rule without a destination must not block reproduction of an eligible rule");
    }

    private static void ReviewedExpectationsAreIndependent()
    {
        var f = Fixture();
        var expected = new List<PullExpectedAssignment> { Expected() };
        var row = PullValidationComparison.CompareExpected(f.Result, expected).Single();
        Check(row.Outcome == PullComparisonOutcome.Matched && row.RecordedApplied == null && row.TimeDelta == null,
            "Reviewed expectations establish assignment agreement, not a recorded navigation or timing claim");
        expected[0].BranchIndex = 1;
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Different,
            "An independently reviewed different branch must fail even when the simulator chose a plausible branch");
        expected[0].BranchIndex = -1;
        f.Result.Decisions[0].BranchIndex = -1; f.Result.Decisions[0].SlideId = "";
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Matched,
            "An explicit no-match expectation must compare meaningfully");
        expected[0].Conflict = true;
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Different,
            "Expected conflict must differ from no match");
        f.Result.Decisions[0].Conflict = true;
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Matched,
            "An explicit conflict expectation can reproduce a conflict without a destination");
        f = Fixture(); expected = new() { Expected(occurrence: 2) };
        var rows = PullValidationComparison.CompareExpected(f.Result, expected);
        Check(rows.Count == 2 && rows.Any(r => r.Outcome == PullComparisonOutcome.Missing) && rows.Any(r => r.Outcome == PullComparisonOutcome.Unknown),
            "A missing reviewed decision differs from an unreviewed occurrence; partial expectations must not assert exhaustive pull coverage");
        expected = new() { Expected(), Expected() };
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Unknown,
            "Duplicate expectations must stay ambiguous");
        Check(PullValidationComparison.CompareExpected(f.Result, Array.Empty<PullExpectedAssignment>()).Single().Outcome == PullComparisonOutcome.Unknown,
            "No supplied expectations must never create self-confirming expectations from the simulation");
        expected = new() { Expected(99) };
        Check(PullValidationComparison.CompareExpected(f.Result, expected).Single().Outcome == PullComparisonOutcome.Unknown,
            "A nonexistent expected branch must stay unknown");
    }

    private static void InvalidAndIncompleteEvidenceStaysUnknown()
    {
        foreach (var verified in new[] { true, false })
        {
            var f = Fixture(); f.Result.Complete = verified; f.Result = With(f.Result, scope: !verified);
            Check(Recorded(f).Outcome == PullComparisonOutcome.Unknown &&
                PullValidationComparison.CompareExpected(f.Result, new[] { Expected() }).Single().Outcome == PullComparisonOutcome.Unknown,
                "Incomplete or unverified validation must never report assignment success");
        }
        var capped = Fixture(); capped.Attempt.StatusObservations = Enumerable.Range(0, 4096).Select(_ => new StatusObservation()).ToList();
        Check(Recorded(capped).Outcome == PullComparisonOutcome.Unknown, "A status observation trace at its silent recording cap cannot establish complete coverage");
        capped = Fixture(); capped.Attempt.AdaptiveDecisions = Enumerable.Range(0, 1024).Select(_ => Decision()).ToList();
        Check(Recorded(capped).Outcome == PullComparisonOutcome.Unknown, "A decision trace at its silent recording cap cannot establish complete coverage");
        var invalid = Fixture(); invalid.Result.Decisions[0].Time = float.NaN;
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown, "Nonfinite timing must not produce a matched comparison");
        invalid = Fixture(); invalid.Result.Decisions[0].SlideId = "south";
        Check(Recorded(invalid).Outcome != PullComparisonOutcome.Matched, "A branch with a different slide must not match by branch alone");
        invalid = Fixture(); invalid.Attempt.AdaptiveDecisions[0].RuleId = "";
        Check(PullValidationComparison.CompareRecorded(invalid.Result, invalid.Attempt).All(r => r.Outcome != PullComparisonOutcome.Matched),
            "Legacy decisions without exact rule identity cannot establish reproduction");
        invalid = Fixture(); invalid.Attempt.AdaptiveDecisions[0].SlideId = "south";
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown,
            "A recorded destination that contradicts its own branch is malformed evidence, not a genuine assignment disagreement");
        invalid.Result.Decisions.Clear();
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown,
            "A malformed recorded destination cannot be classified as a missing valid simulated assignment");
        invalid = Fixture(); invalid.Attempt.Plan.Slides[0].Items = null!;
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown,
            "A malformed recorded destination must return unknown without crashing the comparison");
        invalid = Fixture(); invalid.Result.Plan.Slides[0].Items[0].Points = null!;
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown,
            "A malformed tested destination must return unknown without crashing the comparison");
        invalid = Fixture(); invalid.Result.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses.Clear();
        invalid.Attempt.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses = null!;
        Check(Recorded(invalid).Outcome == PullComparisonOutcome.Unknown,
            "Serialization's omission of both empty and null conditions must not disguise an invalid recorded rule as an equivalent valid rule");
    }

    private static void NoInputMutation()
    {
        var f = Fixture(); var expected = new List<PullExpectedAssignment> { Expected(1) };
        var before = JsonConvert.SerializeObject(new { f.Result, f.Attempt, expected });
        var recordedRows = PullValidationComparison.CompareRecorded(f.Result, f.Attempt);
        var expectedRows = PullValidationComparison.CompareExpected(f.Result, expected);
        Check(before == JsonConvert.SerializeObject(new { f.Result, f.Attempt, expected }), "Comparison must not change validation, recording, plan or expectations");
        recordedRows[0].Reason = "UI state"; expectedRows[0].SimulatedSlideId = "UI state";
        Check(before == JsonConvert.SerializeObject(new { f.Result, f.Attempt, expected }), "Returned rows must not expose mutable model references");
    }
}
