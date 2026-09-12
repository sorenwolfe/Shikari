using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Adaptive;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class PullValidationTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static (PlanDocument Plan, ReplayAttempt Pull) Fixture(bool logs = false)
    {
        var plan = PlanDocument.CreateDefault();
        plan.Slides.Add(new Slide { Title = "Alternate" });
        plan.AdaptiveMechanics.Add(Rule(plan, 100, 10));
        var pull = new ReplayAttempt { Plan = plan, Duration = 6, TerritoryId = logs ? 0u : 1u };
        pull.Evidence.Source = logs ? "FF Logs" : "Local recording";
        pull.Evidence.Actors.Add(new EvidenceActor { Id = 20, Name = "Player", IsLocal = !logs });
        pull.Casts.Add(Cast(100, 1, 0, 0, logs));
        return (plan, pull);
    }
    private static AdaptiveMechanic Rule(PlanDocument plan, uint action, uint status, int slide = 0) => new()
    {
        Enabled = true, TerritoryId = 1, AnchorActionId = action, Occurrence = 0, WindowSeconds = 3,
        Branches = new() { new StatusBranch { StatusId = status, SlideId = plan.Slides[slide].Id, MaximumSeconds = 3600 } },
    };
    private static RecordedCast Cast(uint action, int occurrence, float start, float observed, bool logs = false) => new()
    { Source = logs ? "FF Logs" : "Live", ActionId = action, Occurrence = occurrence, StartTime = start, ObservedTime = observed };
    private static EvidenceStatus Status(float time, uint id = 10, string change = "apply", bool baseline = false) => new()
    { ActorId = 20, SourceId = 9, StatusId = id, Time = time, Change = change, Duration = 30, Parameter = 0, Baseline = baseline };
    private static PullValidationResult Run(PlanDocument plan, ReplayAttempt pull, PullValidationOptions? options = null) =>
        PullValidationRunner.Run(plan, pull, 20, options ?? new(1));

    public static void Run()
    {
        ObservationOrdering(); FullPlan(); ArrivalAndRearming(); EvidenceBoundaries(); ScopeAndProvenance(); ImmutabilityAndBounds();
        OccurrenceReadiness(); ConcurrentReadiness();
        Console.WriteLine("PASS: full-plan chronological rules, conflicts, rearming, availability, gaps, unknown log fields, scope, cancellation and bounded immutable validation");
    }

    private static void ObservationOrdering()
    {
        var (plan, _) = Fixture();
        var engine = new AdaptiveEngine(plan, 1);
        engine.Arm(100, 1, 0);
        engine.Observe(new[] { new StatusObservation { StatusId = 10, Time = .05f, Duration = 30 } }, .05f);
        engine.Arm(100, 2, .1f);
        Check(engine.Update(Array.Empty<StatusObservation>(), 1).Count == 0 &&
            engine.Update(Array.Empty<StatusObservation>(), 4).Single().SlideId == "",
            "Ingestion before a later arm must not leak into the replacement occurrence");
        engine.Arm(100, 3, 5);
        engine.Observe(new[] { new StatusObservation { StatusId = 10, Time = 5.1f, Duration = 30 } }, 5.1f);
        Check(engine.Update(Array.Empty<StatusObservation>(), 5.5f).Count == 0 &&
            engine.Update(Array.Empty<StatusObservation>(), 5.9f).Single().SlideId.Length > 0,
            "Ingestion alone must not settle or emit; evaluation remains a full-plan polling step");
    }

    private static void FullPlan()
    {
        var (plan, pull) = Fixture();
        pull.Evidence.Statuses.Add(Status(.2f));
        var result = Run(plan, pull);
        Check(result.Complete && result.ScopeVerified && result.ActiveRules == 1 && result.Decisions.Count == 1 &&
            result.Decisions[0].SlideId == plan.Slides[0].Id && !result.Decisions[0].Applied,
            "A complete scoped stream must produce a passive decision from the real engine");
        plan.AdaptiveMechanics.Add(Rule(plan, 100, 10, 1));
        result = Run(plan, pull);
        Check(result.ActiveRules == 0 && result.ExcludedRules == 2 && result.Decisions.Count == 0,
            "Whole-plan overlap suppression must exclude competing rules");
        plan.AdaptiveMechanics[1].AnchorActionId = 200;
        plan.AdaptiveMechanics[1].Branches[0].StatusId = 11;
        pull.Casts.Add(Cast(200, 1, 0, 0));
        pull.Evidence.Statuses.Add(Status(.2f, 11));
        result = Run(plan, pull);
        Check(result.Decisions.Count == 2 && result.Decisions.All(d => d.Conflict && d.SlideId == ""),
            "Different simultaneous mechanics must retain the live engine's conflict suppression");
        plan.AdaptiveMechanics[1].Enabled = false;
        Check(Run(plan, pull).ActiveRules == 1 && Run(plan, pull, new(1, true)).ActiveRules == 2,
            "Disabled rules need explicit testing opt-in");
    }

    private static void ArrivalAndRearming()
    {
        var (plan, pull) = Fixture();
        pull.Casts[0].ObservedTime = 1;
        pull.Evidence.Statuses.Add(Status(.2f));
        Check(Run(plan, pull).Decisions.Single().SlideId == "", "Late cast observation cannot retroactively acquire an earlier status");
        pull.Evidence.Statuses.Add(Status(1.2f));
        Check(Run(plan, pull).Decisions.Single().Time >= 1.5f, "A cast must not arm before observed availability");
        (plan, pull) = Fixture();
        pull.Casts.Add(Cast(100, 2, .1f, .1f));
        pull.Evidence.Statuses.Add(Status(.05f));
        var result = Run(plan, pull);
        Check(result.Decisions.Count == 1 && result.Decisions[0].Occurrence == 2 && result.Decisions[0].SlideId == "",
            "Rearming inside a virtual tick must not transfer an earlier arm's pending status");
        pull.Evidence.Statuses.Add(Status(.15f));
        result = Run(plan, pull);
        Check(result.Decisions.Count == 1 && result.Decisions[0].Occurrence == 2 && result.Decisions[0].SlideId.Length > 0,
            "A later cast replaces the earlier pending arm and accepts only subsequent observations");
        (plan, pull) = Fixture();
        pull.Evidence.Statuses.Add(Status(.2f));
        pull.Casts.Add(Cast(100, 2, 2, 2));
        pull.Evidence.Statuses.Add(Status(2.2f));
        Check(Run(plan, pull).Decisions.Select(d => d.Occurrence).SequenceEqual(new[] { 1, 2 }),
            "Later occurrences must rearm after an earlier decision was delivered");
        var normal = Run(plan, pull);
        var delayed = Run(plan, pull, new(1, Scenario: PullValidationScenario.DelayedPolling));
        Check(delayed.Decisions.Count == normal.Decisions.Count && delayed.Decisions[0].Time > normal.Decisions[0].Time &&
            delayed.Notices.Any(n => n.Contains("poll", StringComparison.OrdinalIgnoreCase)),
            "Delayed polling must alter actual evaluation timing and label the synthetic scenario");
    }

    private static void EvidenceBoundaries()
    {
        var (plan, pull) = Fixture();
        Check(!Run(plan, pull).Complete, "A player with no status events cannot establish a validated no-assignment outcome");
        pull.Evidence.Statuses.Add(Status(.1f, 999));
        Check(!Run(plan, pull).Complete, "Unrelated statuses alone do not establish the tested assignment's coverage");
        pull.Evidence.Statuses.Clear();
        pull.Evidence.Statuses.Add(Status(.1f, baseline: true));
        Check(!Run(plan, pull).Complete && Run(plan, pull).Decisions.Single().SlideId == "", "Baseline statuses are not fresh assignment evidence");
        pull.Evidence.Statuses.Clear();
        pull.Evidence.Statuses.Add(Status(.1f));
        pull.Evidence.Statuses.Add(Status(.2f, change: "remove"));
        Check(Run(plan, pull).Decisions.Single().SlideId == "", "Removal before settling must invalidate the assignment");
        pull.Evidence.Statuses[1] = Status(.2f, 0, "unavailable");
        var result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Single().SlideId == "", "Unreadable evidence must clear pending state and mark incomplete coverage");
        pull.Evidence.Statuses.RemoveAt(1);
        result = Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1));
        Check(!result.Complete && result.Decisions.Single().SlideId == "", "Injected gaps must clear pre-gap evidence instead of reviving it after resuming");
        pull.Evidence.Statuses.Add(Status(.4f));
        Check(Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1)).Decisions.Single().SlideId == "",
            "Status applications hidden by an injected gap cannot become fresh on resume");
        pull.Evidence.Statuses.Add(Status(1.4f, change: "refresh"));
        Check(Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1)).Decisions.Single().SlideId == "",
            "A refresh of a status carried through the synthetic gap is not a post-gap baseline or new application");
        pull.Evidence.Statuses.Add(Status(1.5f, change: "remove"));
        pull.Evidence.Statuses.Add(Status(1.6f));
        Check(Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1)).Decisions.Single().SlideId.Length > 0,
            "A positively observed new application after the gap may resolve while the report remains partial");
        (plan, pull) = Fixture();
        pull.Evidence.Statuses.Add(Status(.1f));
        var otherSource = Status(.1f); otherSource.SourceId = 10;
        pull.Evidence.Statuses.Add(otherSource);
        pull.Evidence.Statuses.Add(Status(1.2f, change: "remove"));
        var carriedRefresh = Status(1.3f, change: "refresh"); carriedRefresh.SourceId = 10;
        pull.Evidence.Statuses.Add(carriedRefresh);
        Check(Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1)).Decisions.Single().SlideId == "",
            "Removing one source after a gap cannot revive a carried status from a different source");
        (plan, pull) = Fixture();
        var knownSource = Status(.1f); knownSource.SourceId = 10;
        pull.Evidence.Statuses.Add(knownSource);
        var unknownRefresh = Status(1.3f, change: "refresh"); unknownRefresh.SourceId = 0;
        pull.Evidence.Statuses.Add(unknownRefresh);
        Check(Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .15f, GapDuration: 1)).Decisions.Single().SlideId == "",
            "A source-less post-gap refresh cannot turn a carried known-source status into a fresh assignment");
        (plan, pull) = Fixture(true);
        pull.Evidence.EncounterId = 99;
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { EncounterId = 99, EncounterVerified = true, TerritoryId = 1 });
        var status = Status(.1f); status.Duration = .05f; status.Parameter = 7;
        pull.Evidence.Statuses.Add(status);
        result = Run(plan, pull);
        Check(result.Decisions.Single().SlideId.Length > 0,
            "A legacy log duration potentially derived from a later removal must not expire a status in advance");
        plan.AdaptiveMechanics[0].Branches[0].Parameter = 7;
        result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Single().SlideId == "", "Unproven log parameter fields must always be masked");
        plan.AdaptiveMechanics[0].Branches[0].Parameter = -1;
        plan.AdaptiveMechanics[0].Branches[0].MaximumSeconds = 20;
        result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Single().SlideId == "", "Unproven log initial duration must not select a duration branch");
        plan.AdaptiveMechanics[0].Branches[0].MaximumSeconds = 3600;
        pull.Evidence.Statuses.Add(Status(.2f, change: "remove"));
        Check(Run(plan, pull).Decisions.Single().SlideId == "", "Explicit log removal is usable only when its event arrives");
        (plan, pull) = Fixture();
        pull.Evidence.Statuses.Add(Status(.1f));
        pull.Casts.Add(Cast(100, 2, 2, 2));
        var incomplete = Run(plan, pull);
        Check(incomplete.Decisions.Count == 2 && !incomplete.Complete,
            "A fresh observation in one occurrence cannot establish status coverage for a later empty assignment window");
        (plan, pull) = Fixture();
        plan.AdaptiveMechanics[0].Branches[0].Parameter = 9;
        pull.Evidence.Statuses.Add(Status(.1f));
        var mismatch = Run(plan, pull);
        Check(mismatch.Complete && mismatch.Decisions.Single().SlideId == "",
            "A fresh relevant status with a known nonmatching parameter is a conclusive engine no-match");
    }

    private static void ScopeAndProvenance()
    {
        var (plan, pull) = Fixture();
        pull.Evidence.Statuses.Add(Status(.1f));
        var result = Run(plan, pull, new(2));
        Check(!result.ScopeVerified && !result.Complete && result.Decisions.Count == 0, "Known territory mismatch must reject the run");
        pull.TerritoryId = 0;
        result = Run(plan, pull);
        Check(!result.ScopeVerified && !result.Complete && result.Decisions.Count == 1, "Explicit unknown territory can simulate without verifying scope");
        pull.Evidence.EncounterId = 99;
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { EncounterId = 99, EncounterVerified = true, TerritoryId = 1 });
        Check(Run(plan, pull).ScopeVerified, "Verified matching encounter evidence can establish imported encounter scope");
        pull.Evidence.EncounterId = 100;
        Check(Run(plan, pull).Decisions.Count == 0, "A verified conflicting encounter must reject simulation");
        (plan, pull) = Fixture();
        pull.Evidence.Actors.Add(new EvidenceActor { Id = 20 });
        Check(Run(plan, pull).Decisions.Count == 0 && !Run(plan, pull).Complete, "Duplicate selected actor identity cannot be resolved by name or slot");
        pull.Evidence.Actors.RemoveAt(1);
        pull.Casts.Add(Cast(100, 1, 1, 1));
        Check(!Run(plan, pull).Complete && Run(plan, pull).Decisions.Count == 0, "Duplicate action/occurrence identities must not produce success");
        pull.Casts.Clear();
        pull.Mechanics.Add(new ReplayMechanic { ActionId = 100, Occurrence = 1, Time = 0 });
        result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Count == 1 && result.Notices.Any(n => n.Contains("legacy", StringComparison.OrdinalIgnoreCase)),
            "Legacy mechanics fallback must be explicit and cannot claim original observation coverage");
        pull.Casts.Add(Cast(100, 1, 0, 0, true));
        Check(Run(plan, pull).Decisions.Count == 0, "A cast from incompatible provenance cannot be replaced by a more convenient legacy anchor");
        (plan, pull) = Fixture(true);
        pull.Evidence.EncounterId = 99;
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { EncounterId = 99, EncounterVerified = true, TerritoryId = 1 });
        pull.Evidence.Statuses.Add(Status(.1f));
        pull.Casts.Add(new RecordedCast { Source = "FF Logs", ActionId = 999, CompletionTime = .2f });
        pull.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 20, Time = .2f, AbilityId = 1000777 });
        Check(Run(plan, pull).Complete,
            "An unrelated instant cast and unknown unrelated log aura must not invalidate a fully observed tested assignment");
        pull.Evidence.Statuses[1].AbilityId = 1000010;
        Check(!Run(plan, pull).Complete, "An unverified log aura corresponding to a tested condition must remain unknown");
    }

    private static void ImmutabilityAndBounds()
    {
        var (plan, pull) = Fixture();
        plan.AdaptiveMechanics[0].Enabled = false;
        pull.Evidence.Statuses.Add(Status(.2f));
        var beforePlan = JsonConvert.SerializeObject(plan); var beforePull = JsonConvert.SerializeObject(pull);
        Run(plan, pull, new(1, true));
        Check(beforePlan == JsonConvert.SerializeObject(plan) && beforePull == JsonConvert.SerializeObject(pull),
            "Simulation and opt-in rule enabling must not mutate either input graph");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var threw = false;
        try { PullValidationRunner.Run(plan, pull, 20, new(1), canceled.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check(threw, "Cancellation must be observed before work begins");
        pull.Duration = 1801;
        var result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Count == 0, "Oversize pull duration must not create unbounded virtual ticks");
        pull.Duration = 6;
        pull.Evidence.Statuses.AddRange(Enumerable.Repeat(Status(.2f), 65536));
        result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Count == 0, "Oversize event streams must be rejected before sorting");
        (plan, pull) = Fixture();
        pull.Duration = .25f;
        pull.Evidence.Statuses.Add(Status(.1f));
        result = Run(plan, pull);
        Check(!result.Complete && result.Decisions.Count == 0,
            "A recording that ends with an unsettled armed assignment cannot claim complete validation");
        (plan, pull) = Fixture();
        pull.Casts.AddRange(Enumerable.Repeat(Cast(200, 1, 0, 0), 1000));
        Check(!Run(plan, pull).Complete && Run(plan, pull).Decisions.Count == 0, "The cast-event limit is enforced before stream sorting");
        pull.Casts.RemoveRange(1, 1000);
        pull.Evidence.Statuses.AddRange(Enumerable.Repeat(Status(.2f), 65536));
        using var midRun = new CancellationTokenSource();
        midRun.CancelAfter(TimeSpan.FromMilliseconds(1));
        threw = false;
        try { PullValidationRunner.Run(plan, pull, 20, new(1), midRun.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check(threw, "A long valid stream must observe cancellation while preparing or running");
    }

    private static void OccurrenceReadiness()
    {
        var (plan, pull) = Fixture();
        pull.Duration = 8;
        pull.Casts.Add(Cast(100, 2, 4, 4));
        pull.Evidence.Statuses.Add(Status(.1f));
        var result = Run(plan, pull);
        Check(result.HasOccurrenceReadiness && result.EvidenceUsable && !result.Complete && result.Occurrences.Count == 2 &&
            result.Occurrences.Single(o => o.Occurrence == 1).Complete && !result.Occurrences.Single(o => o.Occurrence == 2).Complete,
            "A missing later window must be explicit without invalidating the source or earlier window");
        pull.Evidence.Statuses[0].Time = 4.1f;
        result = Run(plan, pull);
        Check(!result.Occurrences.Single(o => o.Occurrence == 1).Complete && result.Occurrences.Single(o => o.Occurrence == 2).Complete,
            "A missing early window must not poison a later fully observed assignment");
        pull.Evidence.Statuses.Add(Status(3.5f, baseline: true));
        Check(Run(plan, pull).Occurrences.Single(o => o.Occurrence == 2).Complete,
            "A baseline between closed and newly armed acquisition windows cannot invalidate the new window");

        (plan, pull) = Fixture(); pull.Duration = 8;
        pull.Casts.Add(Cast(100, 2, 4, 4));
        pull.Evidence.Statuses.Add(Status(.1f)); pull.Evidence.Statuses.Add(Status(4.1f));
        plan.AdaptiveMechanics[0].Branches[0].MinimumSeconds = 20;
        pull.Evidence.Statuses[0].Duration = null;
        result = Run(plan, pull);
        Check(!result.Occurrences[0].Complete && result.Occurrences[1].Complete && result.EvidenceUsable,
            "An unknown required initial duration must remain local to the occurrence that observed it");
        pull.Evidence.Statuses[0].Duration = 30;
        plan.AdaptiveMechanics[0].Branches[0].Parameter = 0;
        pull.Evidence.Statuses[1].Parameter = null;
        result = Run(plan, pull);
        Check(result.Occurrences[0].Complete && !result.Occurrences[1].Complete,
            "An unknown later status parameter cannot retroactively change early readiness");
        pull.Evidence.Statuses.Add(Status(float.NaN));
        Check(!Run(plan, pull).EvidenceUsable, "An event without a usable time must remain a global source-integrity failure");

        (plan, pull) = Fixture(); pull.Duration = .25f;
        pull.Evidence.Statuses.Add(Status(.1f));
        result = Run(plan, pull);
        Check(result.Decisions.Count == 0 && result.Occurrences.Count == 1 && !result.Occurrences[0].Complete &&
            result.Occurrences[0].EndTime == .25f && result.Occurrences[0].Reasons.Count > 0,
            "An unfinished arm needs an explicit incomplete window even when the engine emitted no decision");
        (plan, pull) = Fixture();
        pull.Casts.Add(Cast(100, 2, .2f, .2f));
        pull.Evidence.Statuses.Add(Status(.1f)); pull.Evidence.Statuses.Add(Status(.3f));
        result = Run(plan, pull);
        Check(result.Occurrences.Count == 2 && !result.Occurrences[0].Complete && result.Occurrences[0].EndTime == .2f &&
            result.Occurrences[1].Complete && result.Decisions.Single().Occurrence == 2,
            "Rearming must preserve the abandoned occurrence as unknown while accepting the later fresh assignment");

        (plan, pull) = Fixture(); pull.Duration = 8;
        pull.Casts.Add(Cast(100, 2, 4, 4));
        pull.Evidence.Statuses.Add(Status(.1f)); pull.Evidence.Statuses.Add(Status(1.5f)); pull.Evidence.Statuses.Add(Status(4.1f));
        result = Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: .2f, GapDuration: 1));
        Check(result.EvidenceUsable && !result.Occurrences[0].Complete && result.Occurrences[1].Complete &&
            result.Decisions.Single(d => d.Occurrence == 1).SlideId.Length > 0,
            "Positive evidence after a settling gap may produce a candidate but cannot repair that window; later fresh windows remain usable");
        result = Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: 4.2f, GapDuration: 1));
        Check(result.Occurrences[0].Complete && !result.Occurrences[1].Complete,
            "A gap after an emitted decision cannot be used to retroactively invalidate it");
        result = Run(plan, pull, new(1, Scenario: PullValidationScenario.ObservationGap, GapStart: 3.2f, GapDuration: .4f));
        Check(result.Complete, "A gap entirely between finished and fresh future assignment windows must not make those windows unknown");

        (plan, pull) = Fixture();
        plan.AdaptiveMechanics[0].WindowSeconds = 3.05f;
        plan.AdaptiveMechanics[0].Branches[0].Parameter = 7;
        pull.Evidence.Statuses.Add(Status(.1f)); // Known parameter mismatch establishes a no-match window.
        var lateBaseline = Status(3.07f, baseline: true); lateBaseline.SourceId = 10;
        pull.Evidence.Statuses.Add(lateBaseline);
        Check(Run(plan, pull).Complete,
            "A baseline from an unseen source after acquisition closed must not invalidate a no-match while it awaits the next evaluation poll");
    }

    private static void ConcurrentReadiness()
    {
        var (plan, pull) = Fixture();
        plan.AdaptiveMechanics.Add(Rule(plan, 200, 11, 1));
        pull.Casts.Add(Cast(200, 1, 0, 0));
        pull.Evidence.Statuses.Add(Status(.1f));
        var result = Run(plan, pull);
        Check(!result.Occurrences.Single(o => o.AnchorActionId == 100).Complete,
            "A seemingly complete assignment cannot establish conflict parity while another armed mechanic lacks evidence");
        pull.Evidence.Statuses.Add(Status(.1f, 11));
        result = Run(plan, pull);
        Check(result.Complete && result.Decisions.All(d => d.Conflict),
            "Both fully observed concurrent mechanics can establish the engine's conflict result");
        plan.AdaptiveMechanics[1].Branches[0].Parameter = 0;
        pull.Evidence.Statuses[1].Parameter = null;
        result = Run(plan, pull);
        Check(!result.Occurrences.Single(o => o.AnchorActionId == 100).Complete,
            "An unknown field in a simultaneous competing rule must also invalidate apparent positive output in another rule");
        pull.Evidence.Statuses[1].Parameter = 0;
        pull.Casts[1].StartTime = pull.Casts[1].ObservedTime = 2;
        pull.Evidence.Statuses[1].Time = 2.1f; pull.Evidence.Statuses[1].Parameter = null;
        result = Run(plan, pull);
        Check(result.Occurrences.Single(o => o.AnchorActionId == 100).Complete,
            "A later separately armed competitor must not retroactively invalidate a completed decision");
        pull.Casts.Add(Cast(100, 1, 1, 1));
        Check(!Run(plan, pull).EvidenceUsable, "Ambiguous cast identity must still gate the whole run despite occurrence readiness");

        (plan, pull) = Fixture();
        plan.AdaptiveMechanics[0].Branches.Add(new StatusBranch
        { StatusId = 11, MinimumSeconds = .15f, MaximumSeconds = .25f, SlideId = plan.Slides[1].Id });
        plan.AdaptiveMechanics.Add(Rule(plan, 200, 12, 1));
        pull.Casts.Add(Cast(200, 1, .3f, .3f));
        pull.Evidence.Statuses.Add(Status(.1f));
        var uncertainDuration = Status(.1f, 11); uncertainDuration.Duration = null;
        pull.Evidence.Statuses.Add(uncertainDuration); pull.Evidence.Statuses.Add(Status(.3f, 12));
        result = Run(plan, pull);
        Check(result.Decisions.First().AnchorActionId == 100 && !result.Occurrences.Single(o => o.AnchorActionId == 200).Complete,
            "Unknown duration could delay an earlier candidate's settling and change a subsequent mechanic's conflict outcome before that earlier window expires");
        uncertainDuration.Duration = .2f;
        Check(Run(plan, pull).Decisions.All(d => d.Conflict),
            "A compatible concrete value for that unknown duration must actually change the later conflict outcome in the real engine");
        uncertainDuration.Duration = null;
        pull.Casts[1].StartTime = pull.Casts[1].ObservedTime = 4;
        pull.Evidence.Statuses[2].Time = 4.1f;
        Check(Run(plan, pull).Occurrences.Single(o => o.AnchorActionId == 200).Complete,
            "An uncertain completed candidate cannot influence unrelated mechanics after its original assignment window expires");
        pull.Casts[1].StartTime = pull.Casts[1].ObservedTime = .8f;
        pull.Evidence.Statuses[2].Time = .9f;
        pull.Casts.Add(Cast(100, 2, .7f, .7f));
        pull.Evidence.Statuses.Add(Status(.8f));
        Check(Run(plan, pull).Occurrences.Single(o => o.AnchorActionId == 200).Complete,
            "An observed rearm must end the uncertainty carried from a prior occurrence without poisoning the fresh new arm");
    }
}
