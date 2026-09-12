using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>Owns a detached analysis job. Only the UI thread publishes or invalidates its result.</summary>
public sealed class PullValidationSession : IDisposable
{
    private sealed record Stamp(PlanDocument Plan, ReplayAttempt Attempt, string PlanId, string AttemptId,
        long EditRevision, long EvidenceRevision);
    private sealed record Output(PullValidationResult Result, List<PullComparisonRow> RecordedRows, string CaseFingerprint);
    private sealed record Job(CancellationTokenSource Cancel, Task<Output> Work);
    private readonly Func<PlanDocument, ReplayAttempt, long, PullValidationOptions, CancellationToken, PullValidationResult> run;
    private Stamp? stamp;
    private Job? job;
    private bool disposed;
    public bool Running => job != null;
    public PullValidationResult? Result { get; private set; }
    public ReplayAttempt? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public string CaseFingerprint { get; private set; } = "";
    public IReadOnlyList<PullComparisonRow> RecordedRows { get; private set; } = Array.Empty<PullComparisonRow>();

    public PullValidationSession() : this(PullValidationRunner.Run) { }
    internal PullValidationSession(Func<PlanDocument, ReplayAttempt, long, PullValidationOptions, CancellationToken, PullValidationResult> run)
        => this.run = run;

    public void Start(PlanDocument plan, ReplayAttempt attempt, long actorId, PullValidationOptions options,
        long editRevision, long evidenceRevision)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Cancel();
        try
        {
            var frozen = PlanSnapshot.Copy(plan);
            var snapshot = Capture(attempt, ReferenceEquals(plan, attempt.Plan) ? frozen : PlanSnapshot.Copy(attempt.Plan));
            var cancel = new CancellationTokenSource();
            stamp = new(plan, attempt, plan.Id, attempt.Id, editRevision, evidenceRevision);
            Snapshot = snapshot;
            job = new(cancel, Task.Run(() =>
            {
                var result = run(frozen, snapshot, actorId, options, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                var rows = PullValidationComparison.CompareRecorded(result, snapshot);
                var binding = PullValidationCases.Fingerprint(result.Plan, snapshot, actorId, options.TerritoryId);
                cancel.Token.ThrowIfCancellationRequested();
                return new Output(result, rows, binding);
            }, cancel.Token));
        }
        catch (Exception ex) { Cancel(); Error = "Could not prepare validation: " + ex.Message; }
    }

    public void Poll(PlanDocument? plan, ReplayAttempt? attempt, long editRevision, long evidenceRevision, bool allowed)
    {
        if (stamp == null) return;
        if (!allowed || !ReferenceEquals(stamp.Plan, plan) || !ReferenceEquals(stamp.Attempt, attempt) ||
            stamp.PlanId != plan?.Id || stamp.AttemptId != attempt?.Id ||
            stamp.EditRevision != editRevision || stamp.EvidenceRevision != evidenceRevision)
        { Cancel(); return; }
        if (job == null || !job.Work.IsCompleted) return;
        var completed = job;
        job = null;
        try
        {
            var output = completed.Work.GetAwaiter().GetResult();
            Result = output.Result; RecordedRows = output.RecordedRows; CaseFingerprint = output.CaseFingerprint;
        }
        catch (OperationCanceledException) { Result = null; }
        catch (Exception ex) { Error = "Validation could not finish: " + ex.Message; }
        finally { completed.Cancel.Dispose(); }
    }

    public void Cancel()
    {
        var abandoned = job;
        job = null; stamp = null; Result = null; Snapshot = null; Error = null; CaseFingerprint = "";
        RecordedRows = Array.Empty<PullComparisonRow>();
        if (abandoned == null) return;
        abandoned.Cancel.Cancel();
        // Cancellation never waits for analysis. Observe faults even if its consumer is gone.
        _ = abandoned.Work.ContinueWith(task => { _ = task.Exception; abandoned.Cancel.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() { if (disposed) return; disposed = true; Cancel(); }

    private static ReplayAttempt Capture(ReplayAttempt source, PlanDocument plan)
    {
        if (source.Casts.Count > ReplayBuffer.MaxCasts || source.Mechanics.Count > ReplayBuffer.MaxMechanics ||
            source.Evidence.Actors.Count > 32 || source.Evidence.Statuses.Count > ReplayEvidence.MaxStatuses ||
            source.StatusObservations.Count > 4096 || source.AdaptiveDecisions.Count > 1024)
            throw new InvalidOperationException("The recording exceeds validation limits.");
        // The assignment evaluator needs no position or frame history. The ordinary Review map
        // owns those; copying them here would waste memory without changing a decision.
        return new ReplayAttempt
        {
            Version = source.Version, Id = source.Id, StartedUtc = source.StartedUtc, Plan = plan,
            LocalSlot = source.LocalSlot, TerritoryId = source.TerritoryId, Duration = source.Duration, EndReason = source.EndReason,
            Casts = source.Casts.Select(c => c.Snapshot()).ToList(),
            Mechanics = source.Mechanics.Select(m => new ReplayMechanic { EntryId = m.EntryId, SlideId = m.SlideId,
                Label = m.Label, ActionId = m.ActionId, Occurrence = m.Occurrence, Time = m.Time, ExpectedResolve = m.ExpectedResolve }).ToList(),
            StatusObservations = source.StatusObservations.Select(s => new StatusObservation { Time = s.Time, StatusId = s.StatusId,
                Duration = s.Duration, Parameter = s.Parameter, SourceId = s.SourceId, Removed = s.Removed, Baseline = s.Baseline,
                ParameterKnown = s.ParameterKnown, DurationKnown = s.DurationKnown }).ToList(),
            AdaptiveDecisions = source.AdaptiveDecisions.Select(d => new AdaptiveDecision { RuleId = d.RuleId, BranchIndex = d.BranchIndex,
                Conflict = d.Conflict, AnchorActionId = d.AnchorActionId, Occurrence = d.Occurrence, Time = d.Time,
                Mechanic = d.Mechanic, SlideId = d.SlideId, Reason = d.Reason, Applied = d.Applied, Navigation = d.Navigation }).ToList(),
            Evidence = new ReplayEvidence
            {
                Source = source.Evidence.Source, Url = source.Evidence.Url, ReportCode = source.Evidence.ReportCode,
                FightId = source.Evidence.FightId, EncounterId = source.Evidence.EncounterId, Complete = source.Evidence.Complete,
                Warnings = source.Evidence.Warnings.ToList(),
                Actors = source.Evidence.Actors.Select(a => new EvidenceActor { Id = a.Id, GameObjectId = a.GameObjectId,
                    Name = a.Name, JobId = a.JobId, Job = a.Job, SlotIndex = a.SlotIndex, IsLocal = a.IsLocal }).ToList(),
                Statuses = source.Evidence.Statuses.Select(s => new EvidenceStatus { ActorId = s.ActorId, SourceId = s.SourceId,
                    Time = s.Time, StatusId = s.StatusId, AbilityId = s.AbilityId, Name = s.Name, Change = s.Change,
                    Duration = s.Duration, Parameter = s.Parameter, Stacks = s.Stacks, Baseline = s.Baseline }).ToList(),
            },
        };
    }
}
