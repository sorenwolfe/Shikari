using System;
using System.Numerics;
using System.Threading;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class PositionCheckSessionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        foreach (var change in new[] { "none", "edit", "evidence", "result", "decision", "attempt", "options", "combat", "cancel" }) Test(change);
        Console.WriteLine("PASS: detached position jobs, effect/frame/reference capture, result/decision/options/revision guards and cancellation");
    }
    private static void Test(string change)
    {
        var plan = PlanDocument.CreateDefault();
        var decision = new AdaptiveDecision { SlideId = plan.Slides[0].Id, Time = 2 };
        var assignments = new PullValidationResult { Plan = plan, AttemptId = "pull", ActorId = 7, Decisions = { decision } };
        var attempt = new ReplayAttempt { Id = "pull", Plan = plan, Duration = 20,
            Evidence = new() { Actors = { new() { Id = 7, SlotIndex = 0 } },
                Effects = { new() { Time = 5, TargetId = 7, TargetPosition = new(10, 20), Type = "calculateddamage" } },
                Positions = { new() { ActorId = 7, Time = 5, Position = new(10, 20) } },
                References = { new() { Source = new(1, 2), Board = new(.1f, .2f) } } },
            Frames = { new() { Time = 5, Valid = true, Players = { new() { SlotIndex = 0, Board = new(.5f) } } } } };
        var options = new PositionCheckOptions(decision.SlideId, 5, EffectIndex: 0);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
        PullValidationResult? capturedResult = null; ReplayAttempt? capturedAttempt = null;
        using var session = new PositionCheckSession((r,d,a,o,c) => {
            capturedResult = r; capturedAttempt = a;
            Check(r.Decisions.Contains(d) && !ReferenceEquals(d, decision), "Selected decision must belong to the frozen result.");
            entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); ended.Set();
            return new PositionCheckResult { Outcome = PositionCheckOutcome.Near, CheckTime = 5 };
        });
        try
        {
            session.Start(assignments, decision, attempt, options, 4, 8);
            Check(entered.Wait(5000) && session.Running, "Start must return while evaluation runs.");
            attempt.Evidence.Effects[0].TargetPosition = Vector2.Zero;
            attempt.Evidence.Positions[0].Position = Vector2.Zero;
            attempt.Evidence.References[0].Source = Vector2.Zero;
            attempt.Frames[0].Players[0].Board = Vector2.Zero;
            plan.Name = "Edited";
            Check(capturedAttempt!.Evidence.Effects[0].TargetPosition == new Vector2(10,20) &&
                capturedAttempt.Evidence.Positions[0].Position == new Vector2(10,20) &&
                capturedAttempt.Evidence.References[0].Source == new Vector2(1,2) &&
                capturedAttempt.Frames[0].Players[0].Board == new Vector2(.5f) && capturedResult!.Plan.Name != "Edited", "Mutable geometry leaked into the worker.");
            if (change == "cancel") session.Cancel();
            else session.Poll(change == "result" ? new() : assignments, change == "decision" ? new() : decision,
                change == "attempt" ? new() : attempt, change == "options" ? options with { Time = 6 } : options,
                change == "edit" ? 5 : 4, change == "evidence" ? 9 : 8, change != "combat");
        }
        finally { release.Set(); }
        Check(ended.Wait(5000), "Worker did not settle.");
        Check(SpinWait.SpinUntil(() => { session.Poll(assignments, decision, attempt, options, 4, 8, true); return !session.Running; }, 5000), "Publication did not settle.");
        Check((session.Result != null) == (change == "none"), "Stale result was published: " + change);
        if (change == "none") { session.Poll(assignments, decision, attempt, options, 5, 8, true); Check(session.Result == null, "Completed result survived an edit."); }
    }
}
