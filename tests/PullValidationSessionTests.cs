using System;
using System.Diagnostics;
using System.Threading;
using Shikari.Model;
using Shikari.Services.Replay;
namespace Shikari.Tests;
public static class PullValidationSessionTests
{
    private static void Check(bool c, string message) { if (!c) throw new Exception(message); }
    public static void Run()
    {
        foreach (var change in new[] { "none", "edit", "evidence", "replacement", "combat", "cancel" }) Test(change);
        using var failure = new PullValidationSession((p,a,id,o,c) => throw new InvalidOperationException("test failure"));
        var plan = PlanDocument.CreateDefault(); var attempt = Attempt(plan);
        failure.Start(plan, attempt, 7, new(1), 0, 0);
        Check(SpinWait.SpinUntil(() => { failure.Poll(plan, attempt, 0, 0, true); return !failure.Running; }, 5000), "Failure did not settle");
        Check(failure.Result == null && failure.Error!.Contains("test failure"), "Background failures must be observed and shown");
        Console.WriteLine("PASS: detached validation inputs, nonblocking start, revision/reference guards, combat/cancel invalidation and observed failures");
    }
    private static ReplayAttempt Attempt(PlanDocument p)
    {
        var a = new ReplayAttempt { Plan = p, Duration = 10, TerritoryId = 1 };
        a.Evidence.Actors.Add(new EvidenceActor { Id = 7, IsLocal = true });
        a.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, Time = 1, StatusId = 10, Duration = 15 });
        a.Casts.Add(new RecordedCast { Source = "Live", ActionId = 10, Occurrence = 1, StartTime = 1 });
        a.AdaptiveDecisions.Add(new AdaptiveDecision { RuleId = "original", Time = 3 });
        return a;
    }
    private static void Test(string change)
    {
        var plan = PlanDocument.CreateDefault(); plan.Name = "Captured plan";
        var attempt = Attempt(plan);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var ended = new ManualResetEventSlim();
        PlanDocument? capturedPlan = null; ReplayAttempt? capturedAttempt = null;
        using var session = new PullValidationSession((p,a,id,o,c) => {
            capturedPlan = p; capturedAttempt = a; entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            ended.Set(); return new PullValidationResult { Plan = p, AttemptId = a.Id, ActorId = id, Complete = true };
        });
        try
        {
            session.Start(plan, attempt, 7, new(1), 4, 8);
            Check(entered.Wait(5000) && session.Running && session.Result == null, "Start must return with analysis running separately");
            Check(!ReferenceEquals(plan, capturedPlan) && !ReferenceEquals(attempt, capturedAttempt) &&
                !ReferenceEquals(attempt.Evidence.Statuses[0], capturedAttempt!.Evidence.Statuses[0]), "Worker data must be detached");
            if (change == "none")
            {
                // Mutating after capture never leaks to the worker. A caller revision invalidates this in normal UI use.
                plan.Name = "New edit"; attempt.Evidence.Statuses[0].Duration = 99;
                attempt.AdaptiveDecisions[0].RuleId = "new";
                Check(capturedPlan!.Name == "Captured plan" && capturedAttempt!.Evidence.Statuses[0].Duration == 15 &&
                    capturedAttempt.AdaptiveDecisions[0].RuleId == "original", "Nested mutation leaked into analysis");
            }
            var clock = Stopwatch.StartNew();
            if (change == "cancel") session.Cancel();
            else session.Poll(change == "replacement" ? PlanDocument.CreateDefault() : plan, attempt,
                change == "edit" ? 5 : 4, change == "evidence" ? 9 : 8, change != "combat");
            Check(clock.ElapsedMilliseconds < 500, "Polling or cancellation blocked on the worker");
        }
        finally { release.Set(); }
        Check(ended.Wait(5000), "Worker did not release");
        Check(SpinWait.SpinUntil(() => { session.Poll(plan, attempt, 4, 8, true); return !session.Running; }, 5000), "Publication did not settle");
        Check((session.Result != null) == (change == "none"), "A stale or cancelled result was published: " + change);
        if (change == "none")
        {
            var binding = session.GetType().GetProperty("CaseFingerprint")?.GetValue(session) as string;
            Check(binding == PullValidationCases.Fingerprint(capturedPlan!, capturedAttempt!, 7, 1),
                "Saved cases must bind to the detached inputs used by the worker, not newer edits");
            session.Poll(plan, attempt, 5, 8, true);
            Check(session.Result == null, "Editing after completion must clear the old result");
            Check(session.GetType().GetProperty("CaseFingerprint")?.GetValue(session) as string == "",
                "A stale result must discard its saved-case binding");
        }
    }
}
