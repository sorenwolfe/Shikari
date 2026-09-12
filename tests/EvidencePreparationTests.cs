using System;
using System.Threading;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.Tests;
public static class EvidencePreparationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        using var work = new EvidencePreparationSession();
        var plan = PlanDocument.CreateDefault();
        var session = new StrategyMergeSession(plan);
        var attempt = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
        attempt.Duration = 10;
        Check(work.Start(() => { started.Set(); gate.Wait(); return new(session, attempt, session.Prepare(attempt)); }), "First work must start.");
        Check(started.Wait(5000), "Worker must start away from caller.");
        Check(!work.Poll(true, out _, out _) && work.Pending, "Polling cannot wait for preparation.");
        work.Cancel();
        Check(!work.Start(() => throw new Exception("Should not start")), "Cancelled work still occupies its single slot until drained.");
        gate.Set();
        EvidencePreparationResult? result = null;
        string error = "";
        Check(SpinWait.SpinUntil(() => work.Poll(true, out result, out error), 5000) && result == null && error == "",
            "Cancelled preparation cannot publish even when its worker finishes.");
        Check(work.Start(() => new(session, attempt, session.Prepare(attempt))), "Completed work releases its slot.");
        Check(SpinWait.SpinUntil(() => work.Poll(false, out result, out error), 5000) && result == null,
            "A changed plan, combat state or replay revision discards the prepared result.");
        Check(work.Start(() => throw new InvalidOperationException("fixture failure")), "Fault test must start.");
        Check(SpinWait.SpinUntil(() => work.Poll(true, out result, out error), 5000) && result == null && error == "fixture failure",
            "Preparation failure reaches the owner without mutating a plan.");
        Console.WriteLine("Evidence preparation lifecycle checks passed.");
    }
}
