using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Live;
using Shikari.Services.Replay;
using Shikari.Services.Storage;

namespace Shikari.Tests;

public static class PositionCheckTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static (PullValidationResult Result, ReplayAttempt Attempt, PositionCheckOptions Options) Fixture(bool verifiedScope = true)
    {
        var plan = PlanDocument.CreateDefault("Position evidence", 2);
        plan.Id = "plan"; plan.Slides[0].Id = "assigned";
        plan.Slides.Add(new Slide { Id = "next-board" });
        plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(.5f, .5f) });
        plan.AdaptiveMechanics.Add(new() { Id = "rule", Enabled = true, TerritoryId = 777, AnchorActionId = 100, Occurrence = 0,
            Branches = new() { new() { StatusId = 10, SlideId = "assigned", MaximumSeconds = 3600 } } });
        var attempt = new ReplayAttempt { Id = "pull", Duration = 30, TerritoryId = 777, Plan = plan,
            Evidence = new() { Source = "FF Logs", Complete = true, CalibrationSlideId = "assigned", EffectsComplete = true,
                Actors = new() { new() { Id = 11, Name = "First Player", JobId = 25, SlotIndex = 0 }, new() { Id = 22, Name = "Second Player", JobId = 24, SlotIndex = 1 } },
                // 90 degree rotation, scale .01 and translation. Independent arena landmarks.
                References = new() { new() { Source = new(0,0), Board = new(.2f,.2f) }, new() { Source = new(40,0), Board = new(.2f,.6f) }, new() { Source = new(0,-40), Board = new(.6f,.2f) } },
                Positions = new() { new() { ActorId = 11, Time = 5, Position = new(30,-30) } },
                Effects = new() { new() { Time = 5, TargetId = 11, SourceId = 99, ActionId = 200, Type = "calculateddamage", TargetPosition = new(30,-30), PacketId = 123 } },
            } };
        var result = new PullValidationResult { Plan = PlanSnapshot.Copy(plan), AttemptId = "pull", ActorId = 11, Duration = 30,
            TerritoryId = 777, ScopeVerified = verifiedScope, Complete = true, ActiveRules = 1,
            Decisions = { new() { RuleId = "rule", AnchorActionId = 100, Occurrence = 1, BranchIndex = 0, SlideId = "assigned", Time = 2 } } };
        return (result, attempt, new("assigned", 5, .03f, true, true));
    }
    private static PositionCheckResult Evaluate((PullValidationResult Result, ReplayAttempt Attempt, PositionCheckOptions Options) f) =>
        PositionCheck.Evaluate(f.Result, f.Result.Decisions[0], f.Attempt, f.Options);

    public static void Run()
    {
        var f = Fixture(); var r = Evaluate(f);
        Check(r.Outcome == PositionCheckOutcome.Near && r.Distance < .0001f && r.ObservedPosition.HasValue, "Rotated scaled independently calibrated point should be near.");
        f.Attempt.Evidence.Positions[0].Position = new(40,-40); Check(Evaluate(f).Outcome == PositionCheckOutcome.Away, "Far observed point should be away, not success/failure.");
        f = Fixture(); f.Options = f.Options with { CheckpointReviewed = false }; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Unreviewed checkpoint cannot establish a comparison.");
        f = Fixture(); f.Options = f.Options with { AlignmentReviewed = false }; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Unreviewed landmarks cannot establish alignment.");
        f = Fixture(); f.Options = f.Options with { SlideId = "other" }; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Only the assigned board is allowed.");
        f = Fixture(); f.Attempt.Evidence.CalibrationSlideId = "other"; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Calibration cannot leak between boards.");
        f = Fixture(); f.Result.Plan.Slides[0].Items.Add(f.Result.Plan.Slides[0].Items[0].Clone()); f.Attempt.Plan.Slides[0].Items.Add(f.Attempt.Plan.Slides[0].Items[0].Clone());
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown && Evaluate(f).Reason.Contains("exactly one"), "Duplicate tokens are ambiguous even in unchanged geometry.");
        f = Fixture(); f.Result.Plan.Slides[0].Items.Clear(); f.Attempt.Plan.Slides[0].Items.Clear();
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown && Evaluate(f).Reason.Contains("exactly one"), "Missing authored destination cannot come from observations.");
        f = Fixture(); f.Attempt.Evidence.Actors[1].SlotIndex = 0; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Two actors cannot own one roster seat.");
        f = Fixture(); f.Attempt.Evidence.Actors[0].SlotIndex = -1; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Missing actor-seat mapping cannot be guessed.");
        f = Fixture(); f.Result.Plan.Slides[0].Items[0].Position = new(.51f,.5f); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Edited board invalidates old alignment.");
        f = Fixture(); f.Result.Plan.Roster[0].Name = "Changed"; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Edited roster invalidates actor-seat correspondence.");
        f = Fixture(); f.Attempt.Evidence.Positions[0].Time = 4.7f; f.Attempt.Evidence.Positions.Add(new() { Time = 5.1f, ActorId = 11, Position = new(30,-30) }); r = Evaluate(f);
        Check(r.Outcome == PositionCheckOutcome.Unknown && r.SampleAge > .25f && r.NextSampleTime == 5.1f, "Stale predecessor must report future neighbor without interpolation.");
        f = Fixture(); f.Attempt.Evidence.Positions[0].Time = 5.1f; r = Evaluate(f); Check(r.Outcome == PositionCheckOutcome.Unknown && r.SampleTime == null && r.NextSampleTime == 5.1f, "Future-only sample cannot be read.");
        f = Fixture(); f.Attempt.Evidence.Positions.Clear(); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Absent positions stay unknown.");
        f = Fixture(); f.Attempt.Evidence.Positions.Add(new() { Time = 5, ActorId = 11, Position = new(40,-40) }); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Duplicate sample timestamps are ambiguous.");
        f = Fixture(); f.Attempt.Evidence.Positions.Add(new() { Time = 5.01f, ActorId = 11, Position = new(float.NaN,0) }); f.Options = f.Options with { Time = 5.01f }; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Cannot skip invalid newer sample.");
        ExactEffectEvidence(); ReadinessAndRearm(); AlignmentGeometry(); LocalFrames(); CancellationAndNoMutation();
        Console.WriteLine("PASS: passive destination proximity, calibrated rotated/scaled maps, exact calculated effects, bounded prior samples, actor/board/readiness guards, cancellation and no mutation");
    }

    private static void ExactEffectEvidence()
    {
        var f = Fixture(); f.Options = f.Options with { EffectIndex = 0, Time = 20 }; f.Attempt.Evidence.Positions[0].Position = new(0,0);
        var r = Evaluate(f); Check(r.Outcome == PositionCheckOutcome.Near && r.CheckTime == 5 && r.SampleTime == 5 && r.SampleAge == 0, "Exact selected effect must use target event position and time.");
        f.Attempt.Evidence.Effects[0].Type = "damage"; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Delayed damage must never substitute for calculated snapshot.");
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.Effects[0].TargetId = 22; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Effect must target the selected actor.");
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.Effects[0].TargetPosition = null; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Missing target position is unknown, not origin.");
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.EffectsComplete = false; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Incomplete effect stream cannot establish event comparison.");
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.Effects.Add(f.Attempt.Evidence.Effects[0]); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Duplicate exact effect identity is ambiguous.");
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.Effects.Add(new() { Time = 5, Type = "damage", TargetId = 11, SourceId = 99, ActionId = 200, PacketId = 123, TargetPosition = new(0,0) });
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "Calculated and damage records remain separate even when packet and time agree.");
        foreach (var corrupt in new Action<EvidenceEffect>[] { e => e.SourceId = 0, e => e.SourceId = -1,
            e => e.SourceInstance = 0, e => e.TargetInstance = -1, e => e.PacketId = -1 })
        {
            f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; corrupt(f.Attempt.Evidence.Effects[0]);
            Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Malformed source, instance or packet identity cannot establish an exact effect comparison.");
        }
        f = Fixture(); f.Options = f.Options with { EffectIndex = 0 }; f.Attempt.Evidence.Effects[0].PacketId = null;
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "An absent optional packet identity remains unknown without inventing an invalid zero.");
        foreach (var conflict in new Action<EvidenceEffect>[] { e => e.Time = 6, e => e.SourceId = 100,
            e => e.SourceInstance = 2, e => e.SourcePosition = new(0,0), e => e.TargetPosition = new(40,-40) })
        {
            f = Fixture(); f.Options = f.Options with { EffectIndex = 0 };
            var duplicate = JsonConvert.DeserializeObject<EvidenceEffect>(JsonConvert.SerializeObject(f.Attempt.Evidence.Effects[0]))!;
            conflict(duplicate); f.Attempt.Evidence.Effects.Add(duplicate);
            Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown,
                "Contradictory observations for one typed packet and target cannot be selected as trustworthy exact snapshots.");
        }
        foreach (long? packet in new long?[] { 0, null })
        {
            f = Fixture(); f.Options = f.Options with { EffectIndex = 0 };
            f.Attempt.Evidence.Effects[0].PacketId = packet;
            f.Attempt.Evidence.Effects.Add(new() { Time = 6, Type = "calculateddamage", ActionId = 200,
                SourceId = 99, TargetId = 11, PacketId = packet, TargetPosition = new(40,-40) });
            Check(Evaluate(f).Outcome == (packet.HasValue ? PositionCheckOutcome.Unknown : PositionCheckOutcome.Near),
                "Packet zero is a real correlation ID; absent packet IDs cannot correlate distinct observations.");
        }
        foreach (var different in new Action<EvidenceEffect>[] { e => e.PacketId = 124, e => e.ActionId = 201,
            e => e.TargetId = 22, e => e.TargetInstance = 2 })
        {
            f = Fixture(); f.Options = f.Options with { EffectIndex = 0 };
            var other = JsonConvert.DeserializeObject<EvidenceEffect>(JsonConvert.SerializeObject(f.Attempt.Evidence.Effects[0]))!;
            other.Time = 6; different(other); f.Attempt.Evidence.Effects.Add(other);
            Check(Evaluate(f).Outcome == PositionCheckOutcome.Near,
                "Different packet, action, target or target-instance observations do not conflict with the selected effect.");
        }
    }

    private static void ReadinessAndRearm()
    {
        var f = Fixture(); f.Result.Decisions[0].Conflict = true; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Conflicting assignment cannot be compared.");
        f = Fixture(false); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Choosing a territory cannot establish verified encounter scope.");
        f = Fixture(); f.Attempt.Id = "other-pull"; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "An unrelated pull cannot supply position evidence.");
        f = Fixture(); f.Result.Complete = false; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Legacy incomplete run remains unknown.");
        f = Fixture(); f.Options = f.Options with { Time = 1 }; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Cannot compare before assignment exists.");
        f = Fixture(); f.Result.Decisions.Add(new() { RuleId = "rule", AnchorActionId = 100, Occurrence = 2, BranchIndex = 0, SlideId = "assigned", Time = 4 }); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Later same-rule decision supersedes old assignment.");
        f = Fixture(); f.Attempt.Casts.Add(new() { Source = "FF Logs", ActionId = 100, Occurrence = 2, StartTime = 4, ObservedTime = 4 }); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Later cast rearm supersedes even with no later decision.");
        f = Fixture(); var decision = f.Result.Decisions[0]; var clone = JsonConvert.DeserializeObject<AdaptiveDecision>(JsonConvert.SerializeObject(decision))!;
        Check(PositionCheck.Evaluate(f.Result, clone, f.Attempt, f.Options).Outcome == PositionCheckOutcome.Unknown, "Caller must select an actual result decision.");
        var old = f.Result; f.Result = new PullValidationResult { Plan = old.Plan, AttemptId = old.AttemptId, ActorId = old.ActorId, Duration = old.Duration,
            TerritoryId = old.TerritoryId, ScopeVerified = true, HasOccurrenceReadiness = true, EvidenceUsable = true, Complete = false,
            Decisions = { decision }, Occurrences = { new() { RuleId = "rule", AnchorActionId = 100, Occurrence = 1, StartTime = 0, Deadline = 10, EndTime = 10, Complete = true } } };
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "Independent complete occurrence may be checked when another occurrence is incomplete.");
        f.Result.Occurrences[0].Complete = false; Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Selected incomplete occurrence stays unknown.");
        f = Fixture(); f.Attempt.Plan.AdaptiveMechanics[0].Enabled = false;
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "A disabled rule explicitly enabled in the detached tested plan can still validate unchanged recorded geometry.");
        foreach (var sameTime in new[] { false, true })
        {
            f = Fixture();
            var otherBoard = f.Result.Plan.Slides[1].Id;
            f.Result.Plan.AdaptiveMechanics.Add(new() { Id = "other-rule", Enabled = true, TerritoryId = 777, AnchorActionId = 101,
                Branches = new() { new() { StatusId = 11, SlideId = otherBoard } } });
            f.Result.Decisions.Add(new() { RuleId = "other-rule", AnchorActionId = 101, Occurrence = 1, BranchIndex = 0,
                SlideId = otherBoard, Time = sameTime ? 2 : 4 });
            Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown,
                "Another rule assigning a different board at or after the selected decision supersedes or conflicts with that destination.");
            f.Result.Decisions[^1].SlideId = "assigned";
            f.Result.Plan.AdaptiveMechanics[^1].Branches[0].SlideId = "assigned";
            Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "An independent rule agreeing on the same board is not a conflicting destination.");
        }
    }

    private static void AlignmentGeometry()
    {
        var f = Fixture(); f.Attempt.Evidence.References[2].Source = new(20,0); f.Attempt.Evidence.References[2].Board = new(.2f,.4f);
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Collinear landmarks cannot validate a 2D arena.");
        f = Fixture(); foreach (var reference in f.Attempt.Evidence.References) reference.Board = new(.5f,.5f); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Collapsed board references cannot align.");
        f = Fixture(); f.Attempt.Evidence.References[0].Source = new(float.PositiveInfinity,0); Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Nonfinite references cannot align.");
        f = Fixture(); f.Attempt.Evidence.References[2].Board = new(.59f,.2f);
        WorldAlignment.TrySolve(f.Attempt.Evidence.References.Select(p => new AlignmentPair(p.Source,p.Board)).ToArray(), out var fit);
        f.Attempt.Evidence.Positions[0].Position = fit.ToWorld(new(.53f,.5f));
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Distance near radius inside landmark residual margin is inconclusive.");
        f = Fixture();
        f.Attempt.Evidence.References[0].Board = new(.499f,.499f);
        f.Attempt.Evidence.References[1].Board = new(.499f,.501f);
        f.Attempt.Evidence.References[2].Board = new(.501f,.499f);
        f.Attempt.Evidence.Positions[0].Position = new(3020,-3020);
        f.Result.Plan.Slides[0].Items[0].Position = f.Attempt.Plan.Slides[0].Items[0].Position = new(.65f,.65f);
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "A tiny but perfectly shaped landmark triangle cannot establish board-wide proximity.");
        f = Fixture();
        f.Attempt.Evidence.References[0].Board = new(.1f,.1f);
        f.Attempt.Evidence.References[1].Board = new(.1f,.4f);
        f.Attempt.Evidence.References[2].Board = new(.1225f,.1f);
        f.Attempt.Evidence.References[2].Source = new(0,-3);
        f.Attempt.Evidence.Positions[0].Position = new(20f / 3,-20f / 3);
        f.Result.Plan.Slides[0].Items[0].Position = f.Attempt.Plan.Slides[0].Items[0].Position = new(.15f,.15f);
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "A long narrow landmark triangle needs meaningful board area as well as span.");
        f = Fixture();
        f.Attempt.Evidence.References[0].Board = new(.1f,.1f);
        f.Attempt.Evidence.References[1].Board = new(.1f,.3f);
        f.Attempt.Evidence.References[2].Board = new(.3f,.1f);
        f.Attempt.Evidence.Positions[0].Position = new(160,-160);
        f.Result.Plan.Slides[0].Items[0].Position = f.Attempt.Plan.Slides[0].Items[0].Position = new(.9f,.9f);
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "A position far beyond the landmark extent cannot inherit a near-zero local fit residual.");
    }

    private static void LocalFrames()
    {
        var f = Fixture(); f.Attempt.Evidence.Source = "Local recording"; f.Attempt.Frames.Add(new() { Time = 5, SlideId = "assigned", Valid = true, BoardPerYalm = .02f,
            Players = new() { new() { Name = "First Player", JobId = 25, SlotIndex = 0, Board = new(.5f,.5f) } } });
        var r = Evaluate(f); Check(r.Outcome == PositionCheckOutcome.Near && r.AlignmentError == null && r.Notes.Any(n => n.Contains("not recorded")), "Local frame quality remains explicitly unmeasured.");
        f.Attempt.Frames[0].Players[0].Name = "Different Player";
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "A remapped seat cannot borrow a different player's historical position.");
        f.Attempt.Frames[0].Players[0].Name = "First Player"; f.Attempt.Frames[0].Players[0].JobId = 24;
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Local legacy identity requires the recorded job as well as the name.");
        f.Attempt.Frames[0].Players[0].JobId = 25; f.Attempt.Frames[0].Players[0].IsLocal = true;
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Local legacy identity cannot disagree on the locally recorded player.");
        f.Attempt.Frames[0].Players[0].IsLocal = false;
        f.Attempt.Evidence.Actors[1].Name = "First Player";
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Ambiguous same-name actor identities cannot establish a local position.");
        f.Attempt.Evidence.Actors[1].Name = "Second Player";
        f.Attempt.Frames[0].Players[0].SlotIndex = 1;
        f.Attempt.Frames[0].Players.Add(new() { Name = "Second Player", JobId = 24, SlotIndex = 0, Board = new(.9f,.9f) });
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Near, "Seat remapping can compare the selected actor's own captured position, never the new seat occupant.");
        f.Attempt.Frames[0].Players.RemoveAt(1); f.Attempt.Frames[0].Players[0].SlotIndex = 0;
        f.Attempt.Frames.Add(new() { Time = 5.1f, SlideId = "assigned", Valid = false }); f.Options = f.Options with { Time = 5.1f };
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Invalid newest local frame must not fall back to an earlier valid one.");
        f.Attempt.Frames.RemoveAt(1); f.Attempt.Frames[0].SlideId = "other"; f.Options = f.Options with { Time = 5 };
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Local frames are only valid on their recorded alignment board.");
        f.Attempt.Frames[0].SlideId = "assigned"; f.Attempt.Frames[0].Players.Add(new() { Name = "First Player", JobId = 25, SlotIndex = 1, Board = new(.5f,.5f) });
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Duplicate local-frame identities are ambiguous even when their seats differ.");
    }

    private static void CancellationAndNoMutation()
    {
        var f = Fixture(); var before = JsonConvert.SerializeObject(new { f.Result, f.Attempt, f.Options }); Evaluate(f);
        Check(before == JsonConvert.SerializeObject(new { f.Result, f.Attempt, f.Options }), "Passive evaluation must not mutate caller data.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { PositionCheck.Evaluate(f.Result, f.Result.Decisions[0], f.Attempt, f.Options, cancellation.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        f = Fixture(); f.Attempt.Evidence.Positions.AddRange(Enumerable.Repeat(f.Attempt.Evidence.Positions[0], ReplayEvidence.MaxPositions));
        Check(Evaluate(f).Outcome == PositionCheckOutcome.Unknown, "Oversized position streams must be rejected before scanning.");
    }
}
