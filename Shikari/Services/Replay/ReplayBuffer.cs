using System;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Replay;

/// <summary>Pure, bounded recorder. A sample is copied so callers cannot rewrite history.</summary>
public sealed class ReplayBuffer
{
    public const float SampleInterval = 0.1f;
    public const float MaxDuration = 1800f;
    public const int MaxFrames = 18001;
    public const int MaxMechanics = 1000;
    public const int MaxCasts = 1000;
    private bool finished;
    public ReplayAttempt Attempt { get; }

    public ReplayBuffer(PlanDocument plan, int localSlot, DateTime startedUtc)
    {
        var settings = PlanJson.Readable();
        var copy = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan, settings), settings)
            ?? throw new InvalidOperationException("Could not snapshot the plan.");
        Attempt = new ReplayAttempt { Plan = copy, LocalSlot = localSlot, StartedUtc = startedUtc };
    }

    public bool TryAdd(ReplayFrame frame)
    {
        if (finished || !float.IsFinite(frame.Time) || frame.Time < 0 || frame.Time > MaxDuration ||
            Attempt.Frames.Count >= MaxFrames ||
            (Attempt.Frames.Count > 0 && frame.Time - Attempt.Frames[^1].Time < SampleInterval - 0.00001f))
            return false;
        var valid = frame.Valid && float.IsFinite(frame.BoardPerYalm) && frame.BoardPerYalm > 0;
        Attempt.Frames.Add(new ReplayFrame
        {
            Time = frame.Time, SlideId = frame.SlideId, Valid = valid,
            BoardPerYalm = valid ? frame.BoardPerYalm : 0,
            Players = valid ? frame.Players.Take(8)
                .Where(p => float.IsFinite(p.Board.X) && float.IsFinite(p.Board.Y))
                .Select(p => new ReplayPlayer { Name = p.Name, JobId = p.JobId, SlotIndex = p.SlotIndex,
                    Board = p.Board, IsLocal = p.IsLocal }).ToList() : new(),
        });
        Attempt.Duration = frame.Time;
        return true;
    }

    public void AddMechanic(ReplayMechanic mechanic)
    {
        if (finished || Attempt.Mechanics.Count >= MaxMechanics || !float.IsFinite(mechanic.Time) ||
            !float.IsFinite(mechanic.ExpectedResolve) || mechanic.Time < 0 || mechanic.Time > MaxDuration)
            return;
        if (Attempt.Mechanics.Any(m => m.EntryId == mechanic.EntryId && m.ActionId == mechanic.ActionId &&
                m.Occurrence == mechanic.Occurrence && Math.Abs(m.Time - mechanic.Time) < 0.05f))
            return;
        Attempt.Mechanics.Add(new ReplayMechanic { EntryId = mechanic.EntryId, SlideId = mechanic.SlideId,
            Label = mechanic.Label, ActionId = mechanic.ActionId, Occurrence = mechanic.Occurrence,
            Time = mechanic.Time, ExpectedResolve = mechanic.ExpectedResolve });
    }

    public void AddCast(RecordedCast cast)
    {
        if (finished) return;
        if (Attempt.Casts.Count >= MaxCasts || !cast.IsValid(MaxDuration))
        {
            Attempt.Evidence.Complete = false;
            const string warning = "Some live cast observations were invalid or exceeded the recording limit.";
            if (!Attempt.Evidence.Warnings.Contains(warning)) Attempt.Evidence.Warnings.Add(warning);
            return;
        }
        Attempt.Casts.Add(cast.Snapshot());
    }

    public ReplayAttempt? Finish(string reason, float duration)
    {
        if (finished) return null;
        finished = true;
        if (Attempt.Frames.Count == 0) return null;
        Attempt.Duration = Math.Clamp(float.IsFinite(duration) ? Math.Max(duration, Attempt.Duration) : Attempt.Duration, 0, MaxDuration);
        Attempt.EndReason = reason;
        Attempt.Casts.RemoveAll(c => !c.IsValid(Attempt.Duration));
        Attempt.Mechanics.Sort((a, b) => a.Time.CompareTo(b.Time));
        return Attempt;
    }
}
