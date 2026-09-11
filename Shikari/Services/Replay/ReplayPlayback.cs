using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;

namespace Shikari.Services.Replay;

/// <summary>Conservative playback: at most 250ms of sample hold, never interpolate through gaps.</summary>
public static class ReplayPlayback
{
    public const float MaxSampleAge = 0.25f;

    private static int IndexAt(ReplayAttempt attempt, float time)
    {
        int low = 0, high = attempt.Frames.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (attempt.Frames[mid].Time <= time) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found;
    }

    public static ReplayFrame? FrameAt(ReplayAttempt attempt, float time)
    {
        if (!float.IsFinite(time) || time < 0 || time > attempt.Duration) return null;
        var index = IndexAt(attempt, time);
        if (index < 0) return null;
        var frame = attempt.Frames[index];
        return frame.Valid && time - frame.Time <= MaxSampleAge ? frame : null;
    }

    public static float? DistanceAt(ReplayAttempt attempt, ReplayMechanic mechanic, int slot)
    {
        var entry = attempt.Plan.Timeline.FirstOrDefault(e => e.Id == mechanic.EntryId);
        if (entry == null || !entry.ReviewCheckpointEnabled || slot < 0) return null;
        var checkpointTime = mechanic.ExpectedResolve + entry.ReviewOffsetSeconds;
        var branch = attempt.AdaptiveDecisions.LastOrDefault(d => d.Applied && d.AnchorActionId != 0 &&
            d.AnchorActionId == mechanic.ActionId && d.Occurrence == mechanic.Occurrence &&
            d.Time >= mechanic.Time && d.Time <= checkpointTime && !string.IsNullOrEmpty(d.SlideId));
        var adaptiveCheckpoint = attempt.Plan.AdaptiveMechanics?.Any(r => r.Enabled && r.TerritoryId == attempt.TerritoryId &&
            r.AnchorActionId == mechanic.ActionId && (r.Occurrence == 0 || r.Occurrence == mechanic.Occurrence)) == true;
        if (adaptiveCheckpoint && branch == null) return null;
        var targetSlide = branch?.SlideId ?? mechanic.SlideId;
        var frame = FrameAt(attempt, checkpointTime);
        if (frame == null || !float.IsFinite(frame.BoardPerYalm) || frame.BoardPerYalm <= 0 || frame.SlideId != targetSlide)
            return null;
        var players = frame.Players.Where(p => p.SlotIndex == slot).ToArray();
        var targets = attempt.Plan.FindSlide(targetSlide)?.Items
            .Where(i => i.Kind == CanvasItemKind.PlayerToken && i.SlotIndex == slot).ToArray();
        if (players.Length != 1 || targets?.Length != 1) return null;
        var distance = Vector2.Distance(players[0].Board, targets[0].Position) / frame.BoardPerYalm;
        return float.IsFinite(distance) ? distance : null;
    }

    public static IReadOnlyList<Vector2> Trail(ReplayAttempt attempt, float time, int slot, float seconds = 5f)
    {
        var result = new List<Vector2>();
        var current = FrameAt(attempt, time);
        if (current == null || slot < 0) return result;
        var player = current.Players.FirstOrDefault(p => p.SlotIndex == slot);
        if (player == null) return result;
        var previous = current.Time;
        for (var i = IndexAt(attempt, time); i >= 0; i--)
        {
            var frame = attempt.Frames[i];
            if (!frame.Valid || frame.SlideId != current.SlideId || time - frame.Time > Math.Clamp(seconds, 0, 30) ||
                previous - frame.Time > MaxSampleAge) break;
            ReplayPlayer? match = null;
            foreach (var candidate in frame.Players)
            {
                if (candidate.SlotIndex != slot || candidate.Name != player.Name || candidate.JobId != player.JobId) continue;
                if (match != null) { match = null; break; }
                match = candidate;
            }
            if (match == null) break;
            result.Add(match.Board);
            previous = frame.Time;
        }
        result.Reverse();
        return result;
    }
}
