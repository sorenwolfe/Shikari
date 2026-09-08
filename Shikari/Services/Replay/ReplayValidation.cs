using System;
using System.Linq;
using Shikari.Model;

namespace Shikari.Services.Replay;

public static class ReplayValidation
{
    public static bool IsValid(ReplayAttempt attempt)
    {
        if (attempt.Version is not (1 or 2) || !Guid.TryParseExact(attempt.Id, "N", out _) ||
            !float.IsFinite(attempt.Duration) || attempt.Duration < 0 || attempt.Duration > ReplayBuffer.MaxDuration ||
            attempt.Plan?.Slides == null || attempt.Plan.Roster == null || attempt.Plan.Timeline == null ||
            attempt.Plan.Arena == null || attempt.Frames == null || attempt.Mechanics == null ||
            attempt.Frames.Count > ReplayBuffer.MaxFrames || attempt.Mechanics.Count > ReplayBuffer.MaxMechanics ||
            attempt.Plan.Slides.Any(s => s == null || s.Items == null || s.Items.Any(i => i == null || i.Points == null)) ||
            attempt.Plan.Roster.Any(r => r == null) || attempt.Plan.Timeline.Any(e => e == null)) return false;
        if (attempt.Plan.FormatVersion > PlanDocument.CurrentFormatVersion ||
            !StrategyEvidenceValidation.IsValid(attempt.Plan) || !SlideMetadataValidation.ValidArena(attempt.Plan.Arena) ||
            attempt.Plan.Slides.Any(s => !SlideMetadataValidation.IsValid(s) || s.Items.Any(i => !SymbolValidation.IsValid(i)))) return false;
        var last = -1f;
        var evidence = attempt.Evidence;
        if (evidence == null || evidence.Actors == null || evidence.Statuses == null || evidence.Positions == null ||
            evidence.References == null || evidence.Warnings == null || evidence.Actors.Count > 32 ||
            evidence.Statuses.Count > ReplayEvidence.MaxStatuses || evidence.Positions.Count > ReplayEvidence.MaxPositions ||
            evidence.References.Count > 8 || evidence.Warnings.Count > 100 ||
            evidence.Actors.Any(a => a == null || a.Id <= 0 || a.Name == null || a.Name.Length > 256 || a.SlotIndex < -1 || a.SlotIndex >= attempt.Plan.Roster.Count) ||
            evidence.Actors.Select(a => a.Id).Distinct().Count() != evidence.Actors.Count ||
            evidence.Statuses.Any(s => s == null || !float.IsFinite(s.Time) || s.Time < 0 || s.Time > attempt.Duration ||
                s.ActorId <= 0 || s.Duration is { } duration && (!float.IsFinite(duration) || duration < 0 || duration > 86400) ||
                s.Parameter is < 0 or > 65535 || s.Change is not ("apply" or "refresh" or "remove" or "unavailable" or "stacks")) ||
            evidence.Positions.Any(p => p == null || !float.IsFinite(p.Time) || p.Time < 0 || p.Time > attempt.Duration ||
                p.ActorId <= 0 || !float.IsFinite(p.Position.X) || !float.IsFinite(p.Position.Y)) ||
            evidence.References.Any(r => r == null || !float.IsFinite(r.Source.X) || !float.IsFinite(r.Source.Y) ||
                !float.IsFinite(r.Board.X) || !float.IsFinite(r.Board.Y))) return false;
        if (attempt.StatusObservations == null || attempt.AdaptiveDecisions == null ||
            attempt.StatusObservations.Count > 4096 || attempt.AdaptiveDecisions.Count > 1024 ||
            attempt.StatusObservations.Any(s => s == null || !float.IsFinite(s.Time) || s.Time < 0 || s.Time > attempt.Duration ||
                !float.IsFinite(s.Duration) || s.Duration < 0) ||
            attempt.AdaptiveDecisions.Any(d => d == null || !float.IsFinite(d.Time) || d.Time < 0 || d.Time > attempt.Duration ||
                d.RuleId == null || d.RuleId.Length > 128 || d.BranchIndex is < -1 or > 15)) return false;
        foreach (var frame in attempt.Frames)
        {
            if (frame == null || !float.IsFinite(frame.Time) || frame.Time < 0 || frame.Time <= last || frame.Time > attempt.Duration ||
                frame.Players == null || frame.Players.Count > 8 ||
                (frame.Valid && (!float.IsFinite(frame.BoardPerYalm) || frame.BoardPerYalm <= 0)) ||
                frame.Players.Any(p => p == null || !float.IsFinite(p.Board.X) || !float.IsFinite(p.Board.Y))) return false;
            last = frame.Time;
        }
        return attempt.Mechanics.All(m => m != null && float.IsFinite(m.Time) && m.Time >= 0 && m.Time <= attempt.Duration && float.IsFinite(m.ExpectedResolve));
    }
}
