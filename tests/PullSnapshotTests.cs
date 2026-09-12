using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Adaptive;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class PullSnapshotTests
{
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static PlanDocument Plan(bool drawingHeavy = false)
    {
        var plan = PlanDocument.CreateDefault("Snapshot fixture");
        plan.Slides[0].Id = "north";
        plan.Slides[0].Title = "North board";
        plan.AdaptiveMechanics.Add(new() { Id="rule", Label="Choose tower", Enabled=true, TerritoryId=7,
            AnchorActionId=123, Occurrence=0, WindowSeconds=5, Branches=new() { new() { StatusId=10, MaximumSeconds=3600,
                SlideId="north", Label="Take the north tower", AdditionalStatuses=new() { new() { StatusId=20, MaximumSeconds=3600 } } } } });
        if (drawingHeavy)
        {
            for (var i=0;i<2400;i++) plan.Slides[0].Items.Add(new() { Kind=CanvasItemKind.Freehand,
                Position=new(.12345678f,.45678912f), Points=new() { new(.1f,.2f), new(.2f,.3f), new(.3f,.4f) } });
            plan.Timeline.Add(new() { Label=new string('x',8192), SlideId="north" });
            plan.Notes = new string('n',32768);
        }
        return plan;
    }

    public static void Run()
    {
        DetachedRulesAndEligibility(); ReplayOwnsFullPrecision(); AllocationScale();
        Console.WriteLine("PASS: small detached adaptive snapshots, unchanged eligibility/overlaps/decisions, deep replay history, bounded rule allocations and reduced capture allocations");
    }

    private static void DetachedRulesAndEligibility()
    {
        var source = Plan(true);
        var legacy = Legacy(source);
        var first = AdaptiveRuleSnapshot.Capture(source,7);
        var second = AdaptiveRuleSnapshot.Capture(source,7);
        Check(first.Slides.Count==1 && first.Slides[0].Id=="north" && first.Slides[0].Items.Count==0 && first.Timeline.Count==0 &&
            first.Roster.Count==0 && first.StrategyEvidence.Count==0, "Adaptive capture must exclude drawings, roster, timeline and collected strategy data.");
        Check(JsonConvert.SerializeObject(Decisions(first))==JsonConvert.SerializeObject(Decisions(legacy)),
            "Slim snapshot must produce identical assignment identity, timing and cue labels to legacy capture.");
        source.AdaptiveMechanics[0].Label="Edited"; source.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].StatusId=99;
        source.Slides.Clear(); first.AdaptiveMechanics[0].Branches[0].Label="One consumer edited its own clone";
        Check(second.AdaptiveMechanics[0].Label=="Choose tower" && second.AdaptiveMechanics[0].Branches[0].Label=="Take the north tower" &&
            second.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].StatusId==20 && second.Slides[0].Id=="north",
            "Source edits and one consumer's edits cannot alter another consumer's frozen rules.");
        source=Plan(); source.AdaptiveMechanics.Add(new() { Id="disabled", AnchorActionId=123, TerritoryId=7, Enabled=false });
        source.AdaptiveMechanics.Add(new() { Id="invalid", AnchorActionId=123, TerritoryId=7, Enabled=true });
        source.AdaptiveMechanics.Add(new() { Id="wrong-duty", AnchorActionId=123, TerritoryId=8, Enabled=true });
        Check(new AdaptiveEngine(AdaptiveRuleSnapshot.Capture(source,7),7).ActiveRuleCount==new AdaptiveEngine(Legacy(source),7).ActiveRuleCount,
            "Invalid, disabled and other-duty rules keep existing eligibility.");
        source.AdaptiveMechanics.Add(new() { Id="overlap", Label="Conflict", Enabled=true, AnchorActionId=123, TerritoryId=7, Occurrence=1,
            Branches=new() { new() { StatusId=30, MaximumSeconds=3600, SlideId="north" } } });
        Check(new AdaptiveEngine(AdaptiveRuleSnapshot.Capture(source,7),7).ActiveRuleCount==0,
            "Snapshot must preserve overlapping candidate rules so the evaluator still excludes both.");
        source=Plan(); var eligible=source.AdaptiveMechanics[0]; source.AdaptiveMechanics.Clear();
        for(var i=0;i<128;i++) source.AdaptiveMechanics.Add(new() { Enabled=false }); source.AdaptiveMechanics.Add(eligible);
        Check(AdaptiveRuleSnapshot.Capture(source,7).AdaptiveMechanics.Count==0, "Taking eligible rules must not move a later rule through the first128 cap.");
    }

    private static List<AdaptiveDecision> Decisions(PlanDocument plan)
    {
        var engine = new AdaptiveEngine(plan,7); engine.Arm(123,1,0);
        var outcomes = engine.Update(new[] { new StatusObservation { StatusId=10, Time=1, Duration=20 }, new StatusObservation { StatusId=20, Time=1, Duration=20 } },1);
        outcomes.AddRange(engine.Update(Array.Empty<StatusObservation>(),1.2f)); return outcomes;
    }

    private static void ReplayOwnsFullPrecision()
    {
        var plan=Plan(true); var item=plan.Slides[0].Items[0]; var itemId=item.Id;
        plan.Slides[0].ArenaOverride=new() { Shape=ArenaShape.Rectangle, AspectRatio=1.3f };
        var buffer=new ReplayBuffer(plan,0,DateTime.UtcNow);
        Check(buffer.Attempt.Plan.Slides[0].Items[0].Id==itemId && buffer.Attempt.Plan.Slides[0].Items[0].Position==item.Position,
            "Historical capture must preserve session identities and full in-memory coordinate precision.");
        item.Points[0]=new(9,9); plan.Slides[0].ArenaOverride!.AspectRatio=2; plan.Timeline[0].Label="Changed";
        plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].StatusId=999;
        Check(buffer.Attempt.Plan.Slides[0].Items[0].Points[0]==new Vector2(.1f,.2f) && buffer.Attempt.Plan.Slides[0].ArenaOverride!.AspectRatio==1.3f &&
            buffer.Attempt.Plan.Timeline[0].Label!="Changed" && buffer.Attempt.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].StatusId==20,
            "Replay history must own nested drawings, arenas, timeline and adaptive conditions.");
    }

    private static PlanDocument Legacy(PlanDocument plan)
    {
        var settings=PlanJson.Readable(); return JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan,settings),settings)!;
    }
    private static long Allocated(Action action, int repetitions=8)
    {
        action(); action(); var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<repetitions;i++) action(); return (GC.GetAllocatedBytesForCurrentThread()-before)/repetitions;
    }
    private static void AllocationScale()
    {
        var small=Plan(); var heavy=Plan(true);
        var smallRules=Allocated(()=>GC.KeepAlive(AdaptiveRuleSnapshot.Capture(small,7)));
        var heavyRules=Allocated(()=>GC.KeepAlive(AdaptiveRuleSnapshot.Capture(heavy,7)));
        Check(heavyRules<=smallRules+2048, "Irrelevant drawing size must not inflate adaptive snapshot allocation.");
        var history=Allocated(()=>GC.KeepAlive(new ReplayBuffer(heavy,0,DateTime.UnixEpoch)));
        var previous=Allocated(()=>GC.KeepAlive(Legacy(heavy)));
        Check(history<previous/2, "Typed historical capture must allocate less than half of legacy JSON capture on the heavy fixture.");
        Console.WriteLine($"Snapshot allocations per capture: rules small={smallRules:N0} B, drawing-heavy={heavyRules:N0} B; history typed={history:N0} B, legacy JSON={previous:N0} B.");
    }
}
