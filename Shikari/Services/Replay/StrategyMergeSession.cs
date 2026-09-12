using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>A source request owns a snapshot; late results cannot edit a different or changed strategy.</summary>
public sealed class StrategyMergeSession
{
    /// <summary>A framework-owned proposal whose disk acknowledgment may arrive after more edits.</summary>
    public sealed class PendingCommit
    {
        private readonly PlanDocument? plan;
        private readonly string fingerprint;
        private readonly Action? rollback;
        private StrategyEnrichmentResult result;
        private bool finished;
        public PlanSaveTicket? Ticket { get; }
        public StrategyEnrichmentResult Result => result;
        internal PendingCommit(StrategyEnrichmentResult result, PlanSaveTicket? ticket = null,
            PlanDocument? plan = null, string fingerprint = "", Action? rollback = null)
        { this.result = result; Ticket = ticket; this.plan = plan; this.fingerprint = fingerprint; this.rollback = rollback; }

        public bool TryComplete(bool allowRollback, out StrategyEnrichmentResult completed)
        {
            completed = result;
            if (finished || Ticket == null) return true;
            if (!Ticket.Completion.IsCompleted) return false;
            finished = true;
            var saved = Ticket.Completion.GetAwaiter().GetResult();
            if (saved.Outcome == PlanSaveOutcome.Superseded)
                result = result with { Summary = "A newer plan operation replaced this save request." };
            else if (saved.Outcome == PlanSaveOutcome.Failed)
            {
                var restore = allowRollback && plan != null && Fingerprint(plan) == fingerprint;
                if (restore) rollback?.Invoke();
                result = result with { Accepted = !restore, Changed = !restore,
                    Summary = restore ? "The strategy update could not be saved; its previous state was restored."
                        : "The strategy update could not be saved. Newer changes were kept; retry saving from the plan header." };
            }
            completed = result;
            return true;
        }
    }
    /// <summary>Detached worker result. Only its originating session can commit it.</summary>
    public sealed class Prepared
    {
        internal StrategyMergeSession Owner { get; }
        internal PlanDocument Plan { get; }
        public StrategyEnrichmentResult Result { get; }
        internal Prepared(StrategyMergeSession owner, PlanDocument plan, StrategyEnrichmentResult result)
        { Owner = owner; Plan = plan; Result = result; }
    }

    public PlanDocument Snapshot { get; }
    private readonly string fingerprint;
    public StrategyMergeSession(PlanDocument plan)
    {
        Snapshot = PlanSnapshot.Copy(plan);
        fingerprint = Fingerprint(plan);
    }

    public bool Matches(PlanDocument? plan) => plan != null && plan.Id == Snapshot.Id && Fingerprint(plan) == fingerprint;

    public StrategyEnrichmentResult Apply(PlanDocument plan, ReplayAttempt attempt, Func<bool> save)
    {
        if (!Matches(plan)) return Stale();
        return Commit(plan, Prepare(attempt), save);
    }

    /// <summary>Uses only captured data; safe on a worker with exclusive ownership of the recording.</summary>
    public Prepared Prepare(ReplayAttempt attempt)
    {
        var staged = PlanSnapshot.Copy(Snapshot);
        var result = StrategyEnrichment.Apply(staged, attempt);
        return new Prepared(this, staged, result);
    }

    /// <summary>Runs on the framework thread. A failed durable save restores the original fields.</summary>
    public StrategyEnrichmentResult Commit(PlanDocument plan, Prepared prepared, Func<bool> save)
    {
        if (prepared.Owner != this || !Matches(plan)) return Stale();
        var result = prepared.Result;
        if (!result.Accepted || !result.Changed) return result;
        var staged = prepared.Plan;
        var timeline = plan.Timeline; var rules = plan.AdaptiveMechanics; var evidence = plan.StrategyEvidence;
        plan.Timeline = staged.Timeline; plan.AdaptiveMechanics = staged.AdaptiveMechanics; plan.StrategyEvidence = staged.StrategyEvidence;
        try
        {
            if (!save()) throw new InvalidOperationException("Could not save the enriched strategy; its previous state was restored.");
        }
        catch
        {
            plan.Timeline = timeline; plan.AdaptiveMechanics = rules; plan.StrategyEvidence = evidence;
            throw;
        }
        return result;
    }

    public PendingCommit CommitAsync(PlanDocument plan, Prepared prepared, Func<PlanDocument, PlanSaveTicket> save)
    {
        if (prepared.Owner != this || !Matches(plan)) return new PendingCommit(Stale());
        var result = prepared.Result;
        if (!result.Accepted || !result.Changed) return new PendingCommit(result);
        var timeline = plan.Timeline; var rules = plan.AdaptiveMechanics; var evidence = plan.StrategyEvidence;
        void Restore() { plan.Timeline = timeline; plan.AdaptiveMechanics = rules; plan.StrategyEvidence = evidence; }
        plan.Timeline = prepared.Plan.Timeline;
        plan.AdaptiveMechanics = prepared.Plan.AdaptiveMechanics;
        plan.StrategyEvidence = prepared.Plan.StrategyEvidence;
        try
        {
            var applied = Fingerprint(plan);
            return new PendingCommit(result, save(plan), plan, applied, Restore);
        }
        catch { Restore(); throw; }
    }

    private static StrategyEnrichmentResult Stale() => new(false, false, 0, 0, 0,
        "The strategy changed while this reference was processing. Import it again against the current plan.");

    public static void LinkReplay(PlanDocument plan, ReplayAttempt attempt)
    {
        var key = attempt.Evidence.Source == "FF Logs"
            ? $"fflogs:{attempt.Evidence.ReportCode}:{attempt.Evidence.FightId}" : "local:" + attempt.Id;
        var attachment = plan.StrategyEvidence.FirstOrDefault(a => a.Key == key);
        if (attachment == null) return;
        foreach (var mechanic in attempt.Mechanics)
        {
            var rows = attachment.Mechanics.Where(m => m.ActionId == mechanic.ActionId &&
                m.Occurrence == mechanic.Occurrence && m.CastTime == mechanic.Time && m.EntryId.Length > 0).ToArray();
            if (rows.Length != 1 || attempt.Plan.FindSlide(rows[0].SlideId) == null) continue;
            mechanic.EntryId = rows[0].EntryId; mechanic.SlideId = rows[0].SlideId;
        }
    }

    private static string Fingerprint(PlanDocument plan)
    {
        var value = JObject.FromObject(plan);
        value.Remove(nameof(PlanDocument.ModifiedUtc));
        return value.ToString(Formatting.None);
    }
}
