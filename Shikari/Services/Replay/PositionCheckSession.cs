using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>Owner-thread publication of a passive check over detached geometry and assignment inputs.</summary>
public sealed class PositionCheckSession : IDisposable
{
    private sealed record Stamp(PullValidationResult Assignments, AdaptiveDecision Decision, ReplayAttempt Attempt,
        string AttemptId, PositionCheckOptions Options, long EditRevision, long EvidenceRevision);
    private sealed record Job(CancellationTokenSource Cancel, Task<PositionCheckResult> Work);
    private readonly Func<PullValidationResult, AdaptiveDecision, ReplayAttempt, PositionCheckOptions, CancellationToken, PositionCheckResult> evaluate;
    private Stamp? stamp;
    private Job? job;
    private bool disposed;
    public bool Running => job != null;
    public PositionCheckResult? Result { get; private set; }
    public string? Error { get; private set; }

    public PositionCheckSession() : this(PositionCheck.Evaluate) { }
    internal PositionCheckSession(Func<PullValidationResult, AdaptiveDecision, ReplayAttempt, PositionCheckOptions, CancellationToken, PositionCheckResult> evaluate)
        => this.evaluate = evaluate;

    public void Start(PullValidationResult assignments, AdaptiveDecision decision, ReplayAttempt attempt,
        PositionCheckOptions options, long editRevision, long evidenceRevision)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Cancel();
        try
        {
            var index = assignments.Decisions.IndexOf(decision);
            if (index < 0) throw new InvalidOperationException("Select a decision from this validation result.");
            CheckLimits(assignments, attempt);
            var frozen = CopyAssignments(assignments);
            var recording = CopyRecording(attempt);
            var cancel = new CancellationTokenSource();
            stamp = new(assignments, decision, attempt, attempt.Id, options, editRevision, evidenceRevision);
            job = new(cancel, Task.Run(() => evaluate(frozen, frozen.Decisions[index], recording, options, cancel.Token), cancel.Token));
        }
        catch (Exception ex) { Cancel(); Error = "Could not prepare position check: " + ex.Message; }
    }

    public void Poll(PullValidationResult? assignments, AdaptiveDecision? decision, ReplayAttempt? attempt,
        PositionCheckOptions? options, long editRevision, long evidenceRevision, bool allowed)
    {
        if (stamp == null) return;
        if (!allowed || !ReferenceEquals(stamp.Assignments, assignments) || !ReferenceEquals(stamp.Decision, decision) ||
            !ReferenceEquals(stamp.Attempt, attempt) || stamp.AttemptId != attempt?.Id || stamp.Options != options ||
            stamp.EditRevision != editRevision || stamp.EvidenceRevision != evidenceRevision)
        { Cancel(); return; }
        if (job == null || !job.Work.IsCompleted) return;
        var completed = job; job = null;
        try { Result = completed.Work.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { Result = null; }
        catch (Exception ex) { Error = "Position check could not finish: " + ex.Message; }
        finally { completed.Cancel.Dispose(); }
    }

    public void Cancel()
    {
        var abandoned = job;
        job = null; stamp = null; Result = null; Error = null;
        if (abandoned == null) return;
        abandoned.Cancel.Cancel();
        _ = abandoned.Work.ContinueWith(task => { _ = task.Exception; abandoned.Cancel.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void Dispose() { if (disposed) return; disposed = true; Cancel(); }

    private static void CheckLimits(PullValidationResult result, ReplayAttempt attempt)
    {
        if (result.Decisions.Count > PullValidationRunner.MaxDecisions || result.Occurrences.Count > ReplayBuffer.MaxCasts ||
            attempt.Evidence.Actors.Count > 32 || attempt.Evidence.References.Count > 8 ||
            attempt.Evidence.Effects.Count > ReplayEvidence.MaxEffects || attempt.Evidence.Positions.Count > ReplayEvidence.MaxPositions ||
            attempt.Evidence.Statuses.Count > ReplayEvidence.MaxStatuses || attempt.Frames.Count > ReplayBuffer.MaxFrames ||
            attempt.Frames.Any(f => f.Players.Count > 8) || attempt.Casts.Count > ReplayBuffer.MaxCasts)
            throw new InvalidOperationException("The recording exceeds position-check limits.");
    }
    private static PullValidationResult CopyAssignments(PullValidationResult source)
    {
        var result = new PullValidationResult { Plan = PlanSnapshot.Copy(source.Plan), AttemptId = source.AttemptId,
            ActorId = source.ActorId, Duration = source.Duration, TerritoryId = source.TerritoryId,
            ScopeVerified = source.ScopeVerified, Complete = source.Complete, HasOccurrenceReadiness = source.HasOccurrenceReadiness,
            EvidenceUsable = source.EvidenceUsable, ActiveRules = source.ActiveRules, ExcludedRules = source.ExcludedRules };
        result.Decisions.AddRange(source.Decisions.Select(d => new AdaptiveDecision { RuleId = d.RuleId, BranchIndex = d.BranchIndex,
            Conflict = d.Conflict, AnchorActionId = d.AnchorActionId, Occurrence = d.Occurrence, Time = d.Time,
            Mechanic = d.Mechanic, SlideId = d.SlideId, Reason = d.Reason, Applied = d.Applied, Navigation = d.Navigation }));
        foreach (var o in source.Occurrences)
        {
            var copy = new PullValidationOccurrence { RuleId = o.RuleId, AnchorActionId = o.AnchorActionId,
                Occurrence = o.Occurrence, StartTime = o.StartTime, Deadline = o.Deadline, EndTime = o.EndTime, Complete = o.Complete };
            copy.Reasons.AddRange(o.Reasons); result.Occurrences.Add(copy);
        }
        return result;
    }
    private static ReplayAttempt CopyRecording(ReplayAttempt source) => new()
    {
        Id = source.Id, Version = source.Version, Plan = PlanSnapshot.Copy(source.Plan), StartedUtc = source.StartedUtc,
        TerritoryId = source.TerritoryId, LocalSlot = source.LocalSlot, Duration = source.Duration,
        Casts = source.Casts.Select(c => c.Snapshot()).ToList(),
        Frames = source.Frames.Select(f => new ReplayFrame { Time = f.Time, SlideId = f.SlideId, Valid = f.Valid,
            BoardPerYalm = f.BoardPerYalm, Players = f.Players.Select(p => new ReplayPlayer { Name = p.Name,
                JobId = p.JobId, SlotIndex = p.SlotIndex, Board = p.Board, IsLocal = p.IsLocal }).ToList() }).ToList(),
        // Statuses already produced the detached assignment result. Only geometry is needed here.
        Evidence = new ReplayEvidence { Source = source.Evidence.Source, ReportCode = source.Evidence.ReportCode,
            Url = source.Evidence.Url, FightId = source.Evidence.FightId, EncounterId = source.Evidence.EncounterId,
            Complete = source.Evidence.Complete, EffectsComplete = source.Evidence.EffectsComplete,
            CalibrationSlideId = source.Evidence.CalibrationSlideId,
            Actors = source.Evidence.Actors.Select(a => new EvidenceActor { Id = a.Id, GameObjectId = a.GameObjectId,
                Name = a.Name, Job = a.Job, JobId = a.JobId, SlotIndex = a.SlotIndex, IsLocal = a.IsLocal }).ToList(),
            Positions = source.Evidence.Positions.Select(p => new EvidencePosition { ActorId = p.ActorId, Time = p.Time, Position = p.Position }).ToList(),
            References = source.Evidence.References.Select(r => new EvidenceReference { Source = r.Source, Board = r.Board }).ToList(),
            Effects = source.Evidence.Effects.Select(e => new EvidenceEffect { Time = e.Time, ActionId = e.ActionId, Name = e.Name,
                Type = e.Type, SourceId = e.SourceId, TargetId = e.TargetId, SourceInstance = e.SourceInstance,
                TargetInstance = e.TargetInstance, PacketId = e.PacketId, SourcePosition = e.SourcePosition, TargetPosition = e.TargetPosition }).ToList() },
    };
}
