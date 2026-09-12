using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Shikari.Model;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;
using Shikari.Services.Storage;

namespace Shikari.Services { internal static class CallTemplate { public static string FormatTime(float time) => time.ToString(); } }

namespace Shikari
{
    internal static class Plugin
    {
        public static TestPlanSaves Plans = new();
        public static TestReplaySaves Replays = new();
        public static TestCombat Encounter = new();
    }
    internal sealed class TestCombat { public bool InCombat; }
    internal sealed class TestPlanSaves
    {
        public PlanDocument Active = PlanDocument.CreateDefault();
        public PlanSaveTicket? Ticket;
        public long Revision;
        public PlanSaveTicket RequestSave(PlanDocument plan) => Ticket = new(plan.Id, ++Revision);
        public PlanSaveState GetSaveState(string id) => new(Ticket?.Completion.IsCompleted == false, null, Revision, 0);
        public void Finish(PlanSaveOutcome outcome) => Ticket!.Source.SetResult(new(Active.Id, Ticket.Revision, outcome, "fixture", DateTime.UtcNow));
    }
    internal sealed class TestReplaySaves
    {
        public List<ReplayAttempt> Attempts = new();
        public long EvidenceRevision;
        public bool RejectAdmission;
        public ReplayAttempt? GetLoaded(string id) => Attempts.FirstOrDefault(a => a.Id == id);
        public void AddImported(ReplayAttempt attempt)
        {
            if (RejectAdmission) throw new InvalidOperationException("fixture admission failure");
            Attempts.RemoveAll(a => a.Id == attempt.Id); Attempts.Add(attempt); EvidenceRevision++;
        }
        public void SaveEvidence(ReplayAttempt attempt) => EvidenceRevision++;
    }
}
namespace Shikari.UI
{
    public sealed partial class MainWindow
    {
        private PlanDocument? Plan => Plugin.Plans.Active;
        private long validationEditRevision;
        private object? pendingLogReference;
        private string importStatusLine = "", importDetail = "", evidenceMessage = "";
        private bool importFailed;
        private void AdvancePendingLogReference() { }
        private void InvalidateAssignmentCoverage() { }
        private void InvalidatePullValidation() => validationEditRevision++;
        private void SelectReviewAttempt(ReplayAttempt attempt) => InvalidatePullValidation();
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static MainWindow Fixture(out StrategyMergeSession session, out ReplayAttempt attempt)
        {
            Plugin.Plans = new(); Plugin.Replays = new(); Plugin.Encounter = new();
            var plan = Plugin.Plans.Active;
            attempt = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
            attempt.Duration = 10;
            attempt.Evidence.Source = "FF Logs"; attempt.Evidence.ReportCode = "Sanitized"; attempt.Evidence.FightId = 2;
            session = new(plan);
            return new();
        }
        private void DrainPreparation()
        {
            Check(SpinWait.SpinUntil(() => { AdvanceEvidenceWork(); return !evidencePreparation.Pending; }, 5000), "Preparation must finish.");
        }
        public static void RunEvidenceWorkUiTests()
        {
            var window = Fixture(out var session, out var attempt);
            var originalReferences = Plugin.Plans.Active.StrategyEvidence;
            Plugin.Replays.RejectAdmission = true;
            window.StartEvidenceWork(session, () => new(session, attempt, session.Prepare(attempt)), null, true);
            window.DrainPreparation();
            Check(ReferenceEquals(originalReferences, Plugin.Plans.Active.StrategyEvidence) && Plugin.Plans.Ticket == null && window.importFailed,
                "Replay admission failure must happen before live plan mutation or any strategy save request.");
            window.DisposeEvidenceWork();

            window = Fixture(out session, out attempt);
            originalReferences = Plugin.Plans.Active.StrategyEvidence;
            window.StartEvidenceWork(session, () => new(session, attempt, session.Prepare(attempt)), null, true);
            window.DrainPreparation();
            Check(window.EvidenceWorkPending && Plugin.Plans.Ticket != null && Plugin.Plans.Active.StrategyEvidence.Count == 1,
                "Prepared reference must await the durable save acknowledgment.");
            Plugin.Plans.Finish(PlanSaveOutcome.Failed); window.AdvanceEvidenceWork();
            Check(!window.EvidenceWorkPending && ReferenceEquals(originalReferences, Plugin.Plans.Active.StrategyEvidence) && window.importFailed,
                "A failed current strategy save must restore the original fields and show failure.");
            Check(Plugin.Replays.Attempts.Count == 1, "The independently retained reference remains reviewable after strategy rollback.");
            window.DisposeEvidenceWork();

            window = Fixture(out session, out attempt);
            window.StartEvidenceWork(session, () => new(session, attempt, session.Prepare(attempt)), null, true);
            window.DrainPreparation();
            var changed = Plugin.Plans.Active.StrategyEvidence;
            Plugin.Plans.Active.Notes = "A newer user edit"; window.validationEditRevision++;
            Plugin.Plans.Finish(PlanSaveOutcome.Failed); window.AdvanceEvidenceWork();
            Check(ReferenceEquals(changed, Plugin.Plans.Active.StrategyEvidence) && Plugin.Plans.Active.Notes == "A newer user edit" && window.importFailed,
                "A late save failure cannot replace a newer authored state.");
            window.DisposeEvidenceWork();

            foreach (var change in new[] { "combat", "plan", "replay" })
            {
                window = Fixture(out session, out attempt);
                using var gate = new ManualResetEventSlim(false);
                var capturedSession = session; var capturedAttempt = attempt;
                window.StartEvidenceWork(session, () => { gate.Wait(); return new(capturedSession, capturedAttempt, capturedSession.Prepare(capturedAttempt)); }, null, true);
                if (change == "combat") Plugin.Encounter.InCombat = true;
                if (change == "plan") Plugin.Plans.Active = PlanDocument.CreateDefault();
                if (change == "replay") Plugin.Replays.EvidenceRevision++;
                window.AdvanceEvidenceWork();
                Plugin.Encounter.InCombat = false;
                gate.Set(); window.DrainPreparation();
                Check(Plugin.Replays.Attempts.Count == 0 && Plugin.Plans.Ticket == null && window.importFailed,
                    "A " + change + " change cancels publication even if combat ends before the worker does.");
                window.DisposeEvidenceWork();
            }

            window = Fixture(out session, out attempt);
            window.StartEvidenceWork(session, () => new(session, attempt, session.Prepare(attempt)), null, true);
            window.DrainPreparation();
            Plugin.Plans.Finish(PlanSaveOutcome.Saved); window.AdvanceEvidenceWork();
            Check(!window.EvidenceWorkPending && !window.importFailed && Plugin.Plans.Active.StrategyEvidence.Count == 1,
                "A successfully acknowledged reference is published once and releases its work slot.");
            window.DisposeEvidenceWork();
            MetadataTests();
            Console.WriteLine("Evidence UI preparation, admission, save acknowledgment, rollback and stale/combat guards passed.");
        }

        private static void MetadataTests()
        {
            var plan = PlanDocument.CreateDefault();
            plan.Roster[0].JobId = 19;
            var previous = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
            previous.Evidence.Actors.Add(new EvidenceActor { Id = 12, JobId = 19, SlotIndex = 3 });
            previous.Evidence.CalibrationSlideId = plan.Slides[0].Id;
            previous.Evidence.References.Add(new EvidenceReference { Source = new(80, 80), Board = new(.1f, .1f) });
            var metadata = ReviewedReference.Capture(previous);
            previous.Evidence.Actors[0].SlotIndex = 7;
            previous.Evidence.References[0].Board = new(.9f, .9f);
            var data = new LogFightData { ReportCode = "Sanitized", Fight = new LogFight { Id = 2, EndTime = 10000 },
                Actors = new() { new LogActor { Id = 12, Job = "Paladin", Name = "Actor", Type = "Player" } } };
            var source = new LogEvidence { StatusEvents = new() { new LogStatusEvent { TargetId = 12, StatusId = 77, Time = 1, Duration = 4 } } };
            var jobs = new Dictionary<string, uint> { ["Paladin"] = 19 };
            var retained = PrepareImportedReference(new(plan), data, source, new() { 77 }, jobs, metadata).Attempt;
            Check(retained.Id == previous.Id && retained.Evidence.Actors.Single().SlotIndex == 3 &&
                retained.Evidence.References.Single().Board.X == .1f && retained.Evidence.Statuses.Single().StatusId == 77,
                "Reimport preserves captured reviewed seats and alignment without borrowing later mutable metadata or losing verified status IDs.");
            plan.Arena.Shape = ArenaShape.Rectangle;
            var changed = PrepareImportedReference(new(plan), data, source, new() { 77 }, jobs, metadata).Attempt;
            Check(changed.Evidence.References.Count == 0 && changed.Evidence.CalibrationSlideId == "",
                "Changed arena geometry cannot silently reuse old calibration.");
            plan.Roster[0].Name = "A different roster";
            changed = PrepareImportedReference(new(plan), data, source, new() { 77 }, jobs, metadata).Attempt;
            Check(changed.Evidence.Actors.Single().SlotIndex == 0,
                "Changed roster metadata cannot inherit a previously hand-assigned seat.");
        }
    }
}
