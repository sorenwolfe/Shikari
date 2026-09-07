using System;
using System.Linq;
using System.Numerics;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class ReplayTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static ReplayFrame Frame(float time, bool valid = true, string slide = "slide") => new()
    {
        Time = time, Valid = valid, SlideId = slide, BoardPerYalm = 0.025f,
        Players = new() { new() { Name = "Player", SlotIndex = 0, IsLocal = true, Board = new Vector2(0.5f, 0.6f) } },
    };

    public static void Run()
    {
        var plan = PlanDocument.CreateDefault();
        plan.Slides[0].Id = "slide";
        plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(0.5f, 0.5f) });
        plan.Timeline.Add(new TimelineEntry { Id = "entry", SlideId = "slide", ReviewCheckpointEnabled = true, ReviewRadiusYalms = 2 });
        var buffer = new ReplayBuffer(plan, 0, DateTime.UtcNow);
        var anchor = new ReplayMechanic { Label = "Original", Time = 0.2f, ExpectedResolve = 0.4f };
        buffer.AddMechanic(anchor);
        anchor.Label = "Changed";
        Check(buffer.Attempt.Mechanics[0].Label == "Original", "Mechanic anchors own their data");
        plan.Name = "Edited after pull began";
        Check(buffer.Attempt.Plan.Name != plan.Name, "Plan snapshot must be independent of edits");
        Check(buffer.TryAdd(Frame(0)), "First sample accepted");
        Check(!buffer.TryAdd(Frame(0.01f)), "Sample rate is bounded");
        Check(!buffer.TryAdd(Frame(float.NaN)), "Invalid time rejected");
        var original = Frame(0.2f);
        Check(buffer.TryAdd(original), "Later sample accepted");
        original.Players[0].Board = Vector2.Zero;
        Check(buffer.Attempt.Frames[1].Players[0].Board.X == 0.5f, "Samples own their data");
        Check(buffer.TryAdd(Frame(0.4f, false)), "Missing alignment is recorded as a gap");
        Check(buffer.TryAdd(Frame(0.6f)), "Tracking can recover");
        var attempt = buffer.Finish("Wipe", 0.65f)!;
        Check(buffer.Finish("Wipe", 0.7f) == null, "Duplicate finish does not duplicate attempts");
        Check(!buffer.TryAdd(Frame(0.8f)), "Finished attempt is sealed");
        Check(ReplayPlayback.FrameAt(attempt, 0.1f) != null, "Nearby sample available");
        Check(ReplayPlayback.FrameAt(attempt, 0.5f) == null, "No invented positions during gaps");
        Check(ReplayPlayback.FrameAt(attempt, 5) == null, "No stale positions beyond pull");
        Check(ReplayPlayback.Trail(attempt, 0.6f, 0).Count == 1, "Trails never bridge alignment gaps");
        var mechanic = new ReplayMechanic { EntryId = "entry", SlideId = "slide", ExpectedResolve = 0.2f };
        Check(Math.Abs(ReplayPlayback.DistanceAt(attempt, mechanic, 0)!.Value - 4) < 0.001f, "Checkpoint distance uses yalms");
        attempt.Plan.Timeline[0].ReviewCheckpointEnabled = false;
        Check(ReplayPlayback.DistanceAt(attempt, mechanic, 0) == null, "Unconfigured checkpoint is unavailable");
        attempt.Plan.Timeline[0].ReviewCheckpointEnabled = true;
        attempt.Plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0 });
        Check(ReplayPlayback.DistanceAt(attempt, mechanic, 0) == null, "Ambiguous planned positions are unavailable");
        var bounded = new ReplayBuffer(plan, 0, DateTime.UtcNow);
        Check(!bounded.TryAdd(Frame(ReplayBuffer.MaxDuration + 1)), "Duration bounded");
        var change = new ReplayAttempt { Duration = 1, Frames = new() { Frame(0), Frame(0.2f, true, "other") } };
        Check(ReplayPlayback.Trail(change, 0.2f, 0).Count == 1, "Trails do not connect slides");
        change.Frames[1].Players[0].Name = "Replacement";
        change.Frames[1].SlideId = "slide";
        Check(ReplayPlayback.Trail(change, 0.2f, 0).Count == 1, "Trails do not connect different people");
        Check(ReplayPlayback.FrameAt(change, float.NaN) == null, "Invalid playback time unavailable");
        Check(ReplayValidation.IsValid(attempt), "Valid recording accepted");
        attempt.Plan.Slides[0].ArenaOverride = new ArenaSettings { Shape = ArenaShape.Rectangle, AspectRatio = 2 };
        Check(attempt.Version == 2, "Per-board replay snapshots must reject readers that silently ignore arena overrides");
        Check(ReplayValidation.IsValid(attempt), "Current reader must accept the new replay envelope");
        attempt.Plan.Slides[0].ArenaOverride!.GridDivisions = int.MaxValue;
        Check(!ReplayValidation.IsValid(attempt), "Replay must reject unbounded per-slide arena rendering");
        attempt.Plan.Slides[0].ArenaOverride!.GridDivisions = 8;
        attempt.Plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { Key = "damaged", Mechanics = new() { new() { CastTime = float.NaN } } });
        Check(!ReplayValidation.IsValid(attempt), "Replay must validate attached strategy evidence");
        attempt.Plan.StrategyEvidence.Clear();
        attempt.Plan.FormatVersion = PlanDocument.CurrentFormatVersion + 1;
        Check(!ReplayValidation.IsValid(attempt), "Replay must reject unsupported embedded plan versions");
        attempt.Plan.FormatVersion = 1;
        attempt.Frames[1].Time = -1;
        Check(!ReplayValidation.IsValid(attempt), "Negative stored time rejected");
        attempt.Frames[1].Time = 0;
        Check(!ReplayValidation.IsValid(attempt), "Duplicate stored time rejected");
        attempt.Frames[1].Time = .2f;
        attempt.Frames[1].BoardPerYalm = float.NaN;
        Check(!ReplayValidation.IsValid(attempt), "Non-finite alignment rejected");
        attempt.Frames[1].BoardPerYalm = .025f;
        attempt.Version = 999;
        Check(!ReplayValidation.IsValid(attempt), "Future replay format rejected");
        var adaptive = new ReplayAttempt { Duration = 1, TerritoryId = 1 };
        var adaptiveSlide = new Slide { Id = "branch" };
        adaptiveSlide.Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(.6f, .5f) });
        adaptive.Plan.Slides.Add(adaptiveSlide);
        adaptive.Plan.Timeline.Add(new TimelineEntry { Id = "adaptive-entry", ReviewCheckpointEnabled = true });
        adaptive.Plan.AdaptiveMechanics.Add(new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100 });
        adaptive.Frames.Add(Frame(.2f, true, "branch"));
        var checkpoint = new ReplayMechanic { EntryId = "adaptive-entry", ActionId = 100, Occurrence = 1, SlideId = "static", ExpectedResolve = .2f };
        Check(ReplayPlayback.DistanceAt(adaptive, checkpoint, 0) == null, "Missing adaptive decision cannot score a static fallback");
        adaptive.AdaptiveDecisions.Add(new AdaptiveDecision { Time = .1f, AnchorActionId = 100, Occurrence = 1, SlideId = "branch", Applied = true });
        Check(ReplayPlayback.DistanceAt(adaptive, checkpoint, 0).HasValue, "Checkpoint resolves recorded branch instead of static slide");
        adaptive.AdaptiveDecisions[0].Applied = false;
        Check(ReplayPlayback.DistanceAt(adaptive, checkpoint, 0) == null, "Suppressed branch is not scored as applied");
        Console.WriteLine($"PASS: {checks} replay assertions");
    }
}
