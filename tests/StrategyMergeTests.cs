using System;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Replay;
namespace Shikari.Tests;
public static class StrategyMergeTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        var plan = PlanDocument.CreateDefault();
        plan.Slides[0].Title = "Alpha";
        plan.Timeline.Clear();
        var session = new StrategyMergeSession(plan);
        var attempt = new ReplayBuffer(plan, -1, DateTime.UtcNow).Attempt;
        attempt.Duration = 30;
        attempt.Mechanics.Add(new ReplayMechanic { ActionId = 42, Occurrence = 1, Label = "Alpha", Time = 10, ExpectedResolve = 12 });
        plan.ModifiedUtc = DateTime.UtcNow.AddSeconds(3);
        Check(session.Matches(plan), "Autosave timestamps must not invalidate a pending source");
        var before = JsonConvert.SerializeObject(plan);
        var failed = false;
        try { session.Apply(plan, attempt, () => false); } catch (InvalidOperationException) { failed = true; }
        Check(failed && before == JsonConvert.SerializeObject(plan), "Failed saving rolls back all enrichment fields");
        var result = session.Apply(plan, attempt, () => true);
        Check(result.Accepted && result.Changed && plan.Timeline.Count == 1, "An unchanged plan adopts a staged result");
        StrategyMergeSession.LinkReplay(plan, attempt);
        Check(attempt.Mechanics[0].SlideId == plan.Slides[0].Id, "Review resolves linked geometry from its frozen snapshot");
        var second = new StrategyMergeSession(plan).Apply(plan, attempt, () => throw new Exception("Unchanged evidence must not save"));
        Check(second.Accepted && !second.Changed, "Reattaching identical evidence is idempotent");
        var stale = new StrategyMergeSession(plan);
        plan.Slides[0].Notes += "An edit while downloading";
        var rejected = stale.Apply(plan, attempt, () => throw new Exception("Stale save"));
        Check(!rejected.Accepted && !rejected.Changed, "Edits while downloading reject the late result");
        var switched = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan))!;
        switched.Id = Guid.NewGuid().ToString("N");
        Check(!new StrategyMergeSession(plan).Matches(switched), "Switching plans rejects a pending source");
        var backgroundPlan = PlanDocument.CreateDefault();
        backgroundPlan.Slides[0].Title = "Alpha";
        var backgroundSession = new StrategyMergeSession(backgroundPlan);
        var backgroundAttempt = new ReplayBuffer(backgroundPlan, -1, DateTime.UtcNow).Attempt;
        backgroundAttempt.Duration = 30;
        backgroundAttempt.Mechanics.Add(new ReplayMechanic { ActionId = 42, Occurrence = 1, Label = "Alpha", Time = 10, ExpectedResolve = 12 });
        var untouched = JsonConvert.SerializeObject(backgroundPlan);
        var prepared = backgroundSession.Prepare(backgroundAttempt);
        Check(untouched == JsonConvert.SerializeObject(backgroundPlan), "Preparation must not mutate the live plan");
        Check(prepared.Result.Accepted && prepared.Result.Changed, "Preparation computes the complete result before commit");
        backgroundPlan.Notes = "Changed after preparation";
        Check(!backgroundSession.Commit(backgroundPlan, prepared, () => throw new Exception("Stale commit")).Accepted,
            "Commit rechecks edits after background preparation");
        backgroundPlan.Notes = "";
        failed = false;
        try { backgroundSession.Commit(backgroundPlan, prepared, () => false); } catch (InvalidOperationException) { failed = true; }
        Check(failed && untouched == JsonConvert.SerializeObject(backgroundPlan), "Prepared result rolls back after a failed save");
        var owner = new StrategyMergeSession(backgroundPlan);
        Check(!owner.Commit(backgroundPlan, prepared, () => throw new Exception("Foreign commit")).Accepted,
            "A prepared result cannot be committed by another source session");
        Check(backgroundSession.Commit(backgroundPlan, prepared, () => true).Accepted && backgroundPlan.Timeline.Count == 1,
            "The unchanged plan can commit a prepared result");
        Console.WriteLine("PASS: staged enrichment, rollback, autosave, stale edits, switched plans, replay links and idempotence");
    }
}
