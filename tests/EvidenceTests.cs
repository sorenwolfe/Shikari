using System;
using System.Linq;
using System.Numerics;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class EvidenceTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        var evidence = new ReplayEvidence();
        evidence.Statuses.AddRange(new[] {
            new EvidenceStatus { Time=0, ActorId=1, StatusId=10, Duration=4, Baseline=true },
            new EvidenceStatus { Time=1, ActorId=2, StatusId=20, Duration=8 },
            new EvidenceStatus { Time=2, ActorId=1, StatusId=30, Duration=10 },
            new EvidenceStatus { Time=3, ActorId=1, StatusId=30, Change="remove" },
            new EvidenceStatus { Time=5, ActorId=2, Change="unavailable" },
        });
        var timeline = new EvidenceTimeline(evidence);
        Check(timeline.StatusesAt(1, 2.5f).Count == 2, "Actor-specific baseline plus fresh assignment");
        Check(timeline.StatusesAt(1, 3).All(s => s.StatusId != 30), "Removal takes effect at event time");
        Check(timeline.StatusesAt(1, 4).Count == 0, "Status expiry without removal event");
        Check(timeline.StatusesAt(2, 5).Count == 0, "Unreadable actor clears stale status evidence");
        Check(timeline.StatusesAt(2, 2).Single().StatusId == 20, "Backward seek reconstructs prior status");

        evidence.Positions.Add(new EvidencePosition { Time=1, ActorId=1, Position=new(100,100) });
        evidence.Positions.Add(new EvidencePosition { Time=2, ActorId=2, Position=new(110,100) });
        timeline = new EvidenceTimeline(evidence);
        Check(timeline.PositionAt(1, 1.2f) != null, "Recent position available");
        Check(timeline.PositionAt(1, 1.6f) == null, "No holding sparse log positions across gaps");
        Check(timeline.PositionAt(2, 1) == null, "No looking ahead to a future position");
        Check(!EvidenceProjection.TryAlign(evidence, out _), "No guessed alignment");
        evidence.References.AddRange(new[] {
            new EvidenceReference { Source=new(80,80), Board=new(.1f,.1f) },
            new EvidenceReference { Source=new(120,80), Board=new(.9f,.1f) },
            new EvidenceReference { Source=new(80,120), Board=new(.1f,.9f) },
        });
        Check(EvidenceProjection.TryAlign(evidence, out var alignment), "Three known references align");
        Check(Vector2.Distance(alignment.ToPlan(new(100,100)), new(.5f,.5f)) < .001f, "Calibrated mapping");
        evidence.References[2].Board = new(.9f,.9f);
        Check(!EvidenceProjection.TryAlign(evidence, out _), "Inconsistent references rejected");
        var captured = new ReplayEvidence();
        var recorder = new EvidenceRecorder();
        recorder.Observe(captured, 1, 0, new[] { new EvidenceStatusSample(20, 10, 3, 9) });
        Check(captured.Statuses.Single().Baseline, "First snapshot is baseline, not a fresh assignment");
        recorder.Observe(captured, 1, 1, new[] { new EvidenceStatusSample(20, 9, 3, 9), new EvidenceStatusSample(30, 10, 2, 9) });
        Check(captured.Statuses.Count == 2 && !captured.Statuses[1].Baseline, "Countdown does not generate repeated refreshes");
        recorder.Observe(captured, 1, 2, Array.Empty<EvidenceStatusSample>());
        Check(new EvidenceTimeline(captured).StatusesAt(1, 2).Count == 0, "Snapshot removals close both statuses");
        recorder.Observe(captured, 1, 3, null);
        recorder.Observe(captured, 1, 4, new[] { new EvidenceStatusSample(40, 0, 0, 9) });
        Check(captured.Statuses.Last().Baseline && captured.Statuses.Last().Duration == null, "Post-gap snapshot and permanent duration stay distinct");
        var stacks = new ReplayEvidence();
        stacks.Statuses.Add(new EvidenceStatus { ActorId=1, StatusId=10, Time=1, Duration=5, Parameter=2 });
        stacks.Statuses.Add(new EvidenceStatus { ActorId=1, StatusId=10, Time=3, Change="stacks", Stacks=4 });
        var stacked = new EvidenceTimeline(stacks);
        Check(stacked.StatusesAt(1, 4).Single().Parameter == 2, "Stack count never overwrites a verified parameter");
        Check(stacked.StatusesAt(1, 6).Count == 0, "Stack updates do not erase expiry time");
        Console.WriteLine("PASS: evidence status lifecycle, actor isolation, backward seeking, sparse positions and calibration");
    }
}
