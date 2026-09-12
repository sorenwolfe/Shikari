using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;
using Shikari.Services.Storage;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private sealed record ReviewedReference(string Id, PlanDocument Plan,
        (long Id, uint Job, int Seat)[] Seats, string SlideId, List<EvidenceReference> References)
    {
        public static ReviewedReference Capture(ReplayAttempt attempt) => new(attempt.Id, PlanSnapshot.Copy(attempt.Plan),
            attempt.Evidence.Actors.Select(a => (a.Id, a.JobId, a.SlotIndex)).ToArray(),
            attempt.Evidence.CalibrationSlideId, attempt.Evidence.References.Select(r =>
                new EvidenceReference { Source = r.Source, Board = r.Board }).ToList());
    }
    private sealed record EvidenceWorkContext(PlanDocument Plan, StrategyMergeSession Session, long EditRevision,
        long ReplayRevision, ReplayAttempt? Original, bool Imported, string Warnings);
    private sealed record EvidenceSaveContext(PlanDocument Plan, StrategyMergeSession.PendingCommit Commit,
        long EditRevision, bool Imported);
    private readonly EvidencePreparationSession evidencePreparation = new();
    private EvidenceWorkContext? evidenceWorkContext;
    private EvidenceSaveContext? evidenceSaveContext;
    private bool EvidenceWorkPending => pendingLogReference != null || evidencePreparation.Pending || evidenceSaveContext != null;

    private static EvidencePreparationResult PrepareImportedReference(StrategyMergeSession session, LogFightData data,
        LogEvidence evidence, HashSet<uint> validStatuses, Dictionary<string, uint> jobs, ReviewedReference? previous)
    {
        var attempt = LogReplayBuilder.Build(session.Snapshot, data, evidence,
            validStatuses.Contains, name => jobs.GetValueOrDefault(name));
        if (previous != null)
        {
            attempt.Id = previous.Id;
            if (JsonConvert.SerializeObject(previous.Plan.Roster) == JsonConvert.SerializeObject(attempt.Plan.Roster))
            {
                foreach (var actor in attempt.Evidence.Actors)
                {
                    var old = previous.Seats.Where(a => a.Id == actor.Id && a.Job == actor.JobId).ToArray();
                    if (old.Length == 1) actor.SlotIndex = old[0].Seat;
                }
                foreach (var duplicate in attempt.Evidence.Actors.Where(a => a.SlotIndex >= 0).GroupBy(a => a.SlotIndex).Where(g => g.Count() > 1))
                    foreach (var actor in duplicate) actor.SlotIndex = -1;
            }
            if (previous.SlideId.Length > 0 && previous.Plan.FindSlide(previous.SlideId) != null &&
                JsonConvert.SerializeObject(previous.Plan.Arena) == JsonConvert.SerializeObject(attempt.Plan.Arena) &&
                JsonConvert.SerializeObject(previous.Plan.FindSlide(previous.SlideId)) == JsonConvert.SerializeObject(attempt.Plan.FindSlide(previous.SlideId)))
            {
                attempt.Evidence.CalibrationSlideId = previous.SlideId;
                attempt.Evidence.References = previous.References;
            }
        }
        return new(session, attempt, session.Prepare(attempt));
    }

    private void StartEvidenceWork(StrategyMergeSession session, Func<EvidencePreparationResult> prepare,
        ReplayAttempt? original, bool imported, string warnings = "")
    {
        if (evidencePreparation.Pending || evidenceSaveContext != null)
            throw new InvalidOperationException("The previous reference is still processing. Wait for it to finish.");
        var plan = Plan;
        if (plan == null || Plugin.Encounter.InCombat || !session.Matches(plan))
            throw new InvalidOperationException("The strategy changed or combat started. Retry after the pull with the current plan.");
        evidenceWorkContext = new(plan, session, validationEditRevision, Plugin.Replays.EvidenceRevision, original, imported, warnings);
        evidencePreparation.Start(prepare);
        SetEvidenceWorkMessage(imported, "Studying this pull's casts, assignments and movement…");
    }

    private void AdvanceEvidenceWork()
    {
        AdvancePendingLogReference();
        if (evidenceWorkContext is { } context)
        {
            var compatible = !Plugin.Encounter.InCombat && ReferenceEquals(Plan, context.Plan) &&
                validationEditRevision == context.EditRevision && Plugin.Replays.EvidenceRevision == context.ReplayRevision &&
                (context.Original == null || ReferenceEquals(Plugin.Replays.GetLoaded(context.Original.Id), context.Original));
            if (evidencePreparation.Ready && compatible) compatible = context.Session.Matches(Plan);
            if (evidencePreparation.Poll(compatible, out var prepared, out var error))
            {
                evidenceWorkContext = null;
                if (error.Length > 0) SetEvidenceWorkMessage(context.Imported, "Reference preparation failed: " + error, true);
                else if (prepared == null)
                    SetEvidenceWorkMessage(context.Imported, "The strategy, recording or combat state changed while processing. Retry with the current plan.", true);
                else CommitEvidenceWork(context, prepared);
            }
        }
        if (evidenceSaveContext is not { } saving) return;
        var ticket = saving.Commit.Ticket;
        var allowRollback = !Plugin.Encounter.InCombat && ReferenceEquals(Plan, saving.Plan) &&
            validationEditRevision == saving.EditRevision && (ticket == null ||
                Plugin.Plans.GetSaveState(saving.Plan.Id).RequestedRevision == ticket.Revision);
        if (!saving.Commit.TryComplete(allowRollback, out var result)) return;
        evidenceSaveContext = null;
        InvalidateAssignmentCoverage(); InvalidatePullValidation();
        SetEvidenceWorkMessage(saving.Imported, result.Summary, !result.Accepted ||
            ticket?.Completion.GetAwaiter().GetResult().Outcome == PlanSaveOutcome.Failed);
    }

    private void CommitEvidenceWork(EvidenceWorkContext context, EvidencePreparationResult prepared)
    {
        if (!prepared.Prepared.Result.Accepted)
        { SetEvidenceWorkMessage(context.Imported, prepared.Prepared.Result.Summary, true); return; }
        try
        {
            // Admission can fail under memory/storage backpressure. Complete it before
            // CommitAsync is allowed to change the active strategy.
            StrategyMergeSession.LinkReplay(prepared.Prepared.Plan, prepared.Attempt);
            if (context.Imported) Plugin.Replays.AddImported(prepared.Attempt);
            else if (context.Original is { } original)
            {
                var mechanics = original.Mechanics;
                original.Mechanics = prepared.Attempt.Mechanics;
                try { Plugin.Replays.SaveEvidence(original); }
                catch { original.Mechanics = mechanics; throw; }
            }
            var commit = prepared.Session.CommitAsync(context.Plan, prepared.Prepared, Plugin.Plans.RequestSave);
            InvalidateAssignmentCoverage(); InvalidatePullValidation();
            if (context.Imported)
            {
                SelectReviewAttempt(prepared.Attempt);
                importDetail = context.Warnings;
            }
            evidenceSaveContext = new(context.Plan, commit, validationEditRevision, context.Imported);
            SetEvidenceWorkMessage(context.Imported, commit.Ticket == null ? commit.Result.Summary : "Reference retained. Saving the strategy update…");
        }
        catch (Exception ex)
        { SetEvidenceWorkMessage(context.Imported, "The strategy update could not finish: " + ex.Message, true); }
    }

    private void SetEvidenceWorkMessage(bool imported, string message, bool failed = false)
    {
        if (imported) { importStatusLine = message; importFailed = failed; }
        else evidenceMessage = message;
    }

    private void DisposeEvidenceWork()
    {
        pendingLogReference = null;
        evidencePreparation.Dispose();
        evidenceWorkContext = null; evidenceSaveContext = null;
    }
}
