using System;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class StrategyEvidenceTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static (PlanDocument, ReplayAttempt) Fixture(bool calibrated = true)
    {
        var plan = PlanDocument.CreateDefault();
        plan.Roster[0].JobId = 19;
        plan.Slides[0].Title = "Akh Morn";
        plan.Slides[0].Items.Add(new CanvasItem { SlotIndex = 0, Position = new(.5f, .5f) });
        plan.Timeline.Add(new TimelineEntry { Label = "Akh Morn", SlideId = plan.Slides[0].Id,
            Trigger = TriggerKind.CombatTime, Enabled = false });
        var attempt = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
        attempt.Duration = 30; attempt.TerritoryId = 100;
        attempt.Mechanics.Add(new ReplayMechanic { Label = "Akh-Morn", ActionId = 42, Occurrence = 1, Time = 10, ExpectedResolve = 12 });
        attempt.Evidence.Actors.Add(new EvidenceActor { Id = 10, Name = "Private Player", JobId = 19, SlotIndex = 0 });
        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 10, Time = 11, StatusId = 77, Name = "Assigned", Duration = 10 });
        foreach (var time in new[] { 10f, 11f, 12f })
            attempt.Evidence.Positions.Add(new EvidencePosition { ActorId = 10, Time = time, Position = new(100, 100) });
        if (calibrated) Calibrate(attempt);
        return (plan, attempt);
    }
    static void Calibrate(ReplayAttempt attempt)
    {
        attempt.Evidence.CalibrationSlideId = attempt.Plan.Slides[0].Id;
        attempt.Evidence.References.AddRange(new[] {
            new EvidenceReference { Source = new(80,80), Board = new(.1f,.1f) },
            new EvidenceReference { Source = new(120,80), Board = new(.9f,.1f) },
            new EvidenceReference { Source = new(80,120), Board = new(.1f,.9f) },
        });
    }
    public static void Run()
    {
        var (plan, attempt) = Fixture();
        var slide = JsonConvert.SerializeObject(plan.Slides);
        var id = plan.Timeline[0].Id;
        var result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.Accepted && result.MatchedMechanics == 1, "Normalized name must attach observed mechanic");
        Check(plan.Timeline[0].Id == id && plan.Timeline[0].CastActionId == 42 && plan.Timeline[0].SortTime == 10 &&
            plan.Timeline[0].Trigger == TriggerKind.BossCast && !plan.Timeline[0].Enabled, "Missing anchor enriched without enabling navigation");
        Check(JsonConvert.SerializeObject(plan.Slides) == slide, "Authored geometry must survive attachment");
        Check(plan.StrategyEvidence.Single().Mechanics.Single().Actors.Single().SlotIndex == 0, "Actor resolved to seat");
        Check(!JsonConvert.SerializeObject(plan.StrategyEvidence).Contains("Private Player"), "Shared evidence cannot copy character names");
        Check(plan.AdaptiveMechanics.Count == 1 && !plan.AdaptiveMechanics[0].Enabled, "Reliable observed assignment yields disabled draft");
        var json = JsonConvert.SerializeObject(plan);
        Check(!StrategyEnrichment.Apply(plan, attempt).Changed && JsonConvert.SerializeObject(plan) == json, "Repeat attachment must be idempotent");
        var sameObservation = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt))!;
        sameObservation.Id = Guid.NewGuid().ToString("N");
        result = StrategyEnrichment.Apply(plan, sameObservation);
        Check(result.DraftsAdded == 0 && plan.AdaptiveMechanics.Count == 1 && plan.StrategyEvidence.Count == 2,
            "A repeated condition from a new recording adds evidence without duplicating an identical disabled candidate.");

        (plan, attempt) = Fixture();
        plan.Timeline[0].CastActionId = 42; plan.Timeline[0].SortTime = 9; plan.Timeline[0].Label = "Authored label";
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 1 && plan.Timeline[0].SortTime == 9, "Verified action ID wins and authored timing survives");

        (plan, attempt) = Fixture();
        plan.Timeline.Add(new TimelineEntry { Label = "Akh Morn", SlideId = plan.Slides[0].Id });
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.UnassignedMechanics == 1 && result.MatchedMechanics == 0 && plan.Timeline.All(e => e.CastActionId == 0), "Ambiguous authored rows cannot be guessed");

        (plan, attempt) = Fixture();
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { Key = "previous", EncounterId = 88, EncounterVerified = true });
        attempt.Evidence.EncounterId = 99;
        json = JsonConvert.SerializeObject(plan);
        Check(!StrategyEnrichment.Apply(plan, attempt).Accepted && JsonConvert.SerializeObject(plan) == json, "Known different encounters reject atomically");

        foreach (var condition in new[] { "baseline", "gap", "incomplete", "unknown-status", "expired" })
        {
            (plan, attempt) = Fixture();
            if (condition == "baseline") attempt.Evidence.Statuses[0].Baseline = true;
            if (condition == "gap") attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 10, Time = 11.5f, Change = "unavailable" });
            if (condition == "incomplete") attempt.Evidence.Complete = false;
            if (condition == "unknown-status") attempt.Evidence.Statuses[0].StatusId = 0;
            if (condition == "expired") attempt.Evidence.Statuses[0].Duration = .5f;
            StrategyEnrichment.Apply(plan, attempt);
            Check(plan.AdaptiveMechanics.Count == 0, condition + " must not become an adaptive assignment");
        }

        (plan, attempt) = Fixture(false);
        StrategyEnrichment.Apply(plan, attempt);
        var actor = plan.StrategyEvidence.Single().Mechanics.Single().Actors.Single();
        Check(actor.DestinationDistance == null && actor.Positions.All(p => !p.Calibrated) && plan.AdaptiveMechanics.Count == 0,
            "Uncalibrated coordinates cannot be compared with destinations");
        Calibrate(attempt);
        Check(StrategyEnrichment.Apply(plan, attempt).Changed && plan.StrategyEvidence.Count == 1 && plan.AdaptiveMechanics.Count == 1,
            "Calibration reanalysis replaces same reference and adds justified draft");

        (plan, attempt) = Fixture();
        attempt.Evidence.Actors[0].SlotIndex = -1; plan.Roster[1].JobId = 19;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.StrategyEvidence.Single().Mechanics.Single().Actors.Single().SlotIndex == -1 && plan.AdaptiveMechanics.Count == 0,
            "Duplicate jobs remain unassigned");

        (plan, attempt) = Fixture();
        attempt.Evidence.Actors.Add(new EvidenceActor { Id = 20, Name = "Other Private Player", SlotIndex = 0, JobId = 19 });
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.StrategyEvidence.Single().Mechanics.Single().Actors.All(a => a.SlotIndex == -1), "Colliding actor seats remain unassigned");

        (plan, attempt) = Fixture();
        attempt.Evidence.Positions[2].Time = 11.5f;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.AdaptiveMechanics.Count == 0, "Sparse positions cannot prove an arrival at resolve");

        (plan, attempt) = Fixture();
        plan.Timeline[0].Occurrence = 0;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.Timeline[0].Occurrence == 0, "Authored every-occurrence selector must remain intact");

        (plan, attempt) = Fixture();
        attempt.Mechanics.Add(new ReplayMechanic { Label = "Akh Morn", ActionId = 99, Occurrence = 1, Time = 15, ExpectedResolve = 17 });
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 0, "Two distinct actions sharing a name cannot establish an anchor");

        (plan, attempt) = Fixture();
        plan.Roster[1].JobId = 19; plan.Roster[1].Name = "Private Player"; attempt.Evidence.Actors[0].SlotIndex = -1;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.StrategyEvidence.Single().Mechanics.Single().Actors.Single().SlotIndex == 1, "Exact actor identity takes precedence over duplicate jobs");

        (plan, attempt) = Fixture();
        attempt.Evidence.CalibrationSlideId = "another-slide";
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.AdaptiveMechanics.Count == 0 && plan.StrategyEvidence[0].Mechanics[0].Actors[0].DestinationDistance == null,
            "Calibration belongs only to its identified board");

        (plan, attempt) = Fixture();
        plan.Slides[0].Items[0].Position = new(float.MaxValue, float.MaxValue);
        StrategyEnrichment.Apply(plan, attempt);
        Check(StrategyEvidenceValidation.IsValid(plan), "Overflowing destination distance must not poison stored evidence");

        (plan, attempt) = Fixture(false);
        for (var pull = 0; pull < 6; pull++)
        {
            attempt.Id = Guid.NewGuid().ToString("N");
            StrategyEnrichment.Apply(plan, attempt);
        }
        Check(plan.StrategyEvidence.Count == 4 && StrategyEvidenceValidation.IsValid(plan), "Evidence references remain bounded");
        var restored = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan))!;
        Check(restored.StrategyEvidence[0].Mechanics[0].Actors[0].Statuses[0].StatusId == 77, "Compact evidence survives JSON persistence");
        restored.StrategyEvidence[0].Mechanics[0].Actors[0].Positions[0].Position = new(float.NaN, 0);
        Check(!StrategyEvidenceValidation.IsValid(restored), "Nonfinite imported observations must be rejected");
        StrategyEvidenceValidation.Normalise(restored);
        Check(restored.StrategyEvidence.Count == 3 && StrategyEvidenceValidation.IsValid(restored), "Normalisation discards invalid evidence without altering boards");

        (plan, attempt) = Fixture();
        for (var cast = 0; cast < 140; cast++)
            attempt.Mechanics.Add(new ReplayMechanic { ActionId = 100, Occurrence = cast + 1, Time = 13 + cast / 10f, ExpectedResolve = 13 + cast / 10f });
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.StrategyEvidence[0].Mechanics.Count == 128 && plan.StrategyEvidence[0].OmittedMechanics == 13,
            "Excess observed mechanics are bounded and omissions reported");
        Check(ShareCode.TryDecode(ShareCode.Encode(plan), out var shared, out var shareError), "Enriched share-code round trip: " + shareError);
        Check(shared!.StrategyEvidence[0].Mechanics[0].Actors[0].Statuses[0].StatusId == 77 && !shared.AdaptiveMechanics[0].Enabled,
            "Sharing retains observations and disabled drafts");

        (plan, attempt) = Fixture(false);
        plan.Slides[0].Title = "Diagram";
        plan.Timeline[0].Label = "Alpha";
        plan.Timeline.Add(new TimelineEntry { Label = "Alpha", CastName = "Beta", SlideId = plan.Slides[0].Id });
        attempt.Mechanics[0].Label = "Alpha";
        attempt.Mechanics.Add(new ReplayMechanic { Label = "Beta", ActionId = 43, Occurrence = 1, Time = 20, ExpectedResolve = 22 });
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 1 && result.UnassignedMechanics == 1 && plan.Timeline[1].CastName == "Beta", "Distinct authored cast name survives enrichment");
        json = JsonConvert.SerializeObject(plan);
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 1 && !result.Changed && json == JsonConvert.SerializeObject(plan),
            "Inferred mutable anchors must not resolve prior name ambiguity on reanalysis");

        (plan, attempt) = Fixture();
        attempt.Evidence.EncounterId = 100;
        StrategyEnrichment.Apply(plan, attempt);
        attempt.Id = Guid.NewGuid().ToString("N"); attempt.TerritoryId = 200; attempt.Evidence.EncounterId = 200;
        Check(StrategyEnrichment.Apply(plan, attempt).Accepted && plan.StrategyEvidence.All(a => !a.EncounterVerified),
            "Unverified attachments and disabled drafts cannot establish encounter identity");

        (plan, attempt) = Fixture();
        attempt.Duration = 12; attempt.Mechanics[0].ExpectedResolve = 20;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.AdaptiveMechanics.Count == 0 && plan.StrategyEvidence[0].Mechanics[0].Actors[0].DestinationDistance == null,
            "A truncated cast cannot use the final pull position as its resolve destination");
        Check(plan.StrategyEvidence[0].Mechanics[0].ExpectedResolveTime == 20 && !plan.StrategyEvidence[0].Mechanics[0].ResolveObserved,
            "Expected cast timing is retained separately from the truncated observation boundary");

        (plan, attempt) = Fixture();
        for (uint status = 1; status <= 4; status++)
            attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 10, Time = 0, StatusId = status, Baseline = true });
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.AdaptiveMechanics.Count == 1 && plan.StrategyEvidence[0].Mechanics[0].Actors[0].Statuses.Any(s => s.StatusId == 77),
            "Bounded evidence must retain fresh draft predicates before baseline statuses");

        (plan, attempt) = Fixture();
        plan.Timeline.Clear(); attempt.Plan.Timeline.Clear();
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 1 && plan.Timeline.Count == 1 && !plan.Timeline[0].Enabled && plan.Timeline[0].CastActionId == 42,
            "A unique exact slide mechanic name creates a disabled cast timeline entry");
        json = JsonConvert.SerializeObject(plan);
        Check(!StrategyEnrichment.Apply(plan, attempt).Changed && json == JsonConvert.SerializeObject(plan), "Inferred timeline entries are idempotent");

        (plan, attempt) = Fixture();
        plan.Timeline.Clear(); attempt.Plan.Timeline.Clear(); plan.Slides[0].Title = "Board phase 1"; plan.Slides[0].SourceLabel = "Akh Morn";
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 1, "Unique exact source mechanic labels can anchor empty timelines");

        (plan, attempt) = Fixture();
        plan.Timeline.Clear(); attempt.Plan.Timeline.Clear(); plan.Slides[0].Title = "Phase 1"; plan.Slides[0].SourceLabel = "Akh Morn";
        plan.Slides.Add(new Slide { Title = "Phase 2", SourceLabel = "Akh Morn" });
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.UnassignedMechanics == 1 && plan.Timeline.Count == 0 && plan.AdaptiveMechanics.Count == 0,
            "Multiple phase boards with a shared mechanic name do not imply a destination");
        Check(plan.StrategyEvidence[0].Mechanics[0].Match == "ambiguous", "Repeated phase labels are explicitly ambiguous");

        (plan, attempt) = Fixture(false);
        plan.Timeline.Clear(); plan.Slides.Clear(); attempt.Mechanics.Clear();
        for (var i = 0; i < 140; i++)
        {
            plan.Slides.Add(new Slide { Title = "Mechanic " + i });
            attempt.Mechanics.Add(new ReplayMechanic { Label = "Mechanic " + i, ActionId = (uint)(i + 1), Occurrence = 1,
                Time = i / 10f, ExpectedResolve = i / 10f });
        }
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.Timeline.Count == 128 && plan.Timeline.All(e => !e.Enabled), "Empty-guide inference remains bounded and disabled");

        (plan, attempt) = Fixture();
        plan.Timeline.Clear(); attempt.Plan.Timeline.Clear();
        plan.Slides[0].Title = plan.Slides[0].SourceLabel = "Akh Morn / Setup";
        plan.Slides[0].SourceUrl = "https://wtfdig.info/74/m12s#caro";
        plan.Slides.Add(new Slide { Title = "Akh Morn / Resolve", SourceLabel = "Akh Morn / Resolve",
            SourceUrl = "https://wtfdig.info/74/m12s#caro" });
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 1 && plan.Timeline.Count == 1 && plan.Timeline[0].SlideId == "" &&
            !plan.Timeline[0].Enabled && plan.AdaptiveMechanics.Count == 0, "An exact WTFDIG phase group gains timing without choosing a phase board");
        Check(plan.StrategyEvidence[0].Mechanics[0].Match == "name" && plan.StrategyEvidence[0].Mechanics[0].Actors.All(a => a.DestinationDistance == null),
            "Phase-only cast evidence cannot imply a player destination");
        json = JsonConvert.SerializeObject(plan);
        Check(!StrategyEnrichment.Apply(plan, attempt).Changed && json == JsonConvert.SerializeObject(plan), "Phase-only timing inference is idempotent");

        (plan, attempt) = Fixture(); plan.Timeline.Clear();
        plan.Slides[0].Title = "Akh Morn / Setup"; plan.Slides[0].SourceUrl = "https://raidplan.io/plan/example";
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 0 && plan.Timeline.Count == 0,
            "Phase-prefix inference requires explicit WTFDIG provenance");

        (plan, attempt) = Fixture(); plan.Timeline.Clear();
        plan.Slides[0].Title = "Akh Morn / Setup"; plan.Slides[0].SourceUrl = "https://wtfdig.info/74/m12s#caro";
        plan.Slides.Add(new Slide { Title = "Akh Morn / Resolve", SourceUrl = "https://wtfdig.info/74/m12s#other" });
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 0 && plan.Timeline.Count == 0,
            "Two distinct source strategy phase groups remain ambiguous");

        foreach (var edit in new[] { "token", "arena", "override", "local-frame" })
        {
            (plan, attempt) = Fixture(edit != "local-frame");
            if (edit == "arena") plan.Arena.Shape = ArenaShape.Square;
            else if (edit == "override") plan.Slides[0].ArenaOverride = new ArenaSettings { AspectRatio = 2 };
            else plan.Slides[0].Items[0].Position = new(.51f, .5f);
            if (edit == "local-frame") attempt.Frames.Add(new ReplayFrame { Time = 12, Valid = true, BoardPerYalm = .02f,
                SlideId = plan.Slides[0].Id, Players = new() { new ReplayPlayer { SlotIndex = 0, Board = new(.5f, .5f) } } });
            StrategyEnrichment.Apply(plan, attempt);
            Check(plan.StrategyEvidence[0].Mechanics[0].Actors[0].DestinationDistance == null && plan.AdaptiveMechanics.Count == 0 &&
                plan.StrategyEvidence[0].Mechanics[0].Actors[0].Positions.All(p => !p.Calibrated),
                edit + " geometry edits must invalidate frozen replay coordinate comparisons");
        }
        (plan, attempt) = Fixture();
        plan.Slides[0].Title = "Edited title"; plan.Slides[0].Notes = "Edited notes";
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.AdaptiveMechanics.Count == 1, "Notes and title edits do not invalidate unchanged geometry");

        (plan, attempt) = Fixture(); plan.Timeline.Clear();
        plan.Slides[0].Title = plan.Slides[0].SourceLabel = "Akh Morn / Setup";
        plan.Slides[0].GuideUrl = "https://wtfdig.info/74/m12s#caro";
        plan.Slides[0].SourceUrl = "https://raidplan.io/plan/board#1";
        var laterBoard = plan.Slides[0].Clone("Akh Morn / Resolve");
        laterBoard.SourceLabel = laterBoard.Title; laterBoard.SourceUrl = "https://raidplan.io/plan/board#2";
        plan.Slides.Add(laterBoard);
        Check(StrategyEnrichment.Apply(plan, attempt).MatchedMechanics == 1 && plan.Timeline[0].SlideId == "",
            "Editable board links retain cloned guide provenance for phase timing inference");

        (plan, attempt) = Fixture();
        StrategyEnrichment.Apply(plan, attempt);
        attempt.Id = Guid.NewGuid().ToString("N"); attempt.Mechanics[0].ActionId = 99;
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 0 && result.DraftsAdded == 0 && plan.Timeline[0].CastActionId == 42 &&
            plan.StrategyEvidence.Last().Mechanics[0].Match == "ambiguous",
            "A later pull's different action ID cannot reuse a previous inferred name-to-board association");

        (plan, attempt) = Fixture();
        plan.Timeline[0].Label = "Authored other mechanic"; plan.Timeline[0].CastActionId = 900;
        plan.Timeline[0].SlideId = ""; plan.Timeline[0].TimeSeconds = 7;
        var authoredRow = JsonConvert.SerializeObject(plan.Timeline[0]);
        plan.Slides[0].Title = plan.Slides[0].SourceLabel = "Akh Morn / Setup";
        plan.Slides[0].GuideUrl = "https://wtfdig.info/74/m12s#caro";
        plan.Slides[0].SourceUrl = "https://raidplan.io/plan/board#1";
        var resolveBoard = plan.Slides[0].Clone("Akh Morn / Resolve"); resolveBoard.SourceLabel = resolveBoard.Title;
        plan.Slides.Add(resolveBoard);
        result = StrategyEnrichment.Apply(plan, attempt);
        Check(result.MatchedMechanics == 1 && plan.Timeline.Count == 2 && plan.Timeline[1].SlideId == "" &&
            !plan.Timeline[1].Enabled && JsonConvert.SerializeObject(plan.Timeline[0]) == authoredRow,
            "A partially timed composed guide can gain unmatched phase timing without altering its authored rows");
        Check(StrategyEvidenceValidation.IsValid(plan), "Phase-only evidence with an empty SlideId passes compact validation");
        json = JsonConvert.SerializeObject(plan);
        Check(!StrategyEnrichment.Apply(plan, attempt).Changed && json == JsonConvert.SerializeObject(plan),
            "Partial timeline inference cannot duplicate an existing inferred phase row");

        (plan, attempt) = Fixture();
        plan.Timeline[0].CastActionId = 900;
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.Timeline.Count == 1 && plan.Timeline[0].CastActionId == 900,
            "A conflicting authored name and action cannot be bypassed with a newly inferred row");

        (plan, attempt) = Fixture(false);
        plan.Timeline.Clear(); plan.Slides.Clear(); attempt.Mechanics.Clear();
        for (var i = 0; i < 150; i++) plan.Timeline.Add(new TimelineEntry { Label = "Authored " + i, CastActionId = 1000 });
        for (var i = 0; i < 140; i++)
        {
            plan.Slides.Add(new Slide { Title = "Mechanic " + i });
            attempt.Mechanics.Add(new ReplayMechanic { Label = "Mechanic " + i, ActionId = (uint)(i + 1), Occurrence = 1,
                Time = i / 10f, ExpectedResolve = i / 10f });
        }
        StrategyEnrichment.Apply(plan, attempt);
        Check(plan.Timeline.Count == 278 && plan.Timeline.Count(e => e.EvidenceCreated) == 128,
            "The inferred-row budget is independent of the authored timeline count");
        Console.WriteLine("PASS: strategy matching, authored geometry, idempotency, encounter guard, actor privacy, status gaps and calibration");
    }
}
