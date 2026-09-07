using System;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Adaptive;
namespace Shikari.Tests;
public static class AdaptiveTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        CompoundRules();
        PermanentStatuses();
        var tracker = new StatusTracker();
        Check(tracker.Observe(new[] { new StatusSample(10, 30, 0, 99) }, 0).Count == 0, "First snapshot establishes baseline");
        var lost = tracker.Observe(Array.Empty<StatusSample>(), 1);
        Check(lost.Count == 1 && JsonConvert.SerializeObject(lost[0]).Contains("\"Removed\":true"), "Loss emits an explicit removal, never a gain");
        var gained = tracker.Observe(new[] { new StatusSample(10, 30, 0, 99) }, 2);
        Check(gained.Count == 1 && gained[0].Duration == 30, "Capture initial observed duration");
        Check(tracker.Observe(new[] { new StatusSample(10, 10, 0, 99) }, 22).Count == 0, "Countdown cannot reclassify long as short");
        Check(tracker.Observe(new[] { new StatusSample(10, 30, 0, 99) }, 23).Count == 1, "Refresh creates a new observation");
        tracker.Invalidate();
        Check(tracker.Observe(new[] { new StatusSample(10, 30, 0, 99) }, 24).Count == 0, "Missing actor requires a fresh baseline");
        var plan = PlanDocument.CreateDefault();
        var alternate = new Slide { Title = "Long" }; plan.Slides.Add(alternate);
        var rule = new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100, Occurrence = 2 };
        rule.Branches.Add(new StatusBranch { StatusId = 10, MinimumSeconds = 0, MaximumSeconds = 20, SlideId = plan.Slides[0].Id });
        rule.Branches.Add(new StatusBranch { StatusId = 10, MinimumSeconds = 20, MaximumSeconds = 60, SlideId = alternate.Id });
        plan.AdaptiveMechanics.Add(rule);
        var engine = new AdaptiveEngine(plan, 1);
        engine.Arm(100, 1, 0);
        Check(engine.Update(gained, 2).Count == 0, "Wrong occurrence cannot arm");
        engine.Arm(100, 2, 1);
        Check(engine.Update(gained, 2).Count == 0, "Wait briefly for competing observations");
        var decisions = engine.Update(Array.Empty<StatusObservation>(), 2.4f);
        Check(decisions.Count == 1 && decisions[0].SlideId == alternate.Id, "Long branch selected");
        Check(engine.Update(gained, 3).Count == 0, "Decision emitted once per arm");
        engine.Arm(100, 2, 4);
        Check(engine.Update(gained, 4.5f).Count == 0, "Pre-anchor observations ignored");
        decisions = engine.Update(Array.Empty<StatusObservation>(), 20);
        Check(decisions.Count == 1 && decisions[0].SlideId == "", "Timeout has no destination");
        rule.Branches.Add(new StatusBranch { StatusId = 10, MaximumSeconds = 60, SlideId = plan.Slides[0].Id });
        engine = new AdaptiveEngine(plan, 1); engine.Arm(100, 2, 1);
        engine.Update(gained, 2);
        decisions = engine.Update(Array.Empty<StatusObservation>(), 2.4f);
        Check(decisions.Count == 1 && decisions[0].SlideId == "" && decisions[0].Reason.Contains("Conflicting"), "Conflicts do not guess");
        engine = new AdaptiveEngine(plan, 2); engine.Arm(100, 2, 1);
        Check(engine.Update(gained, 2).Count == 0 && engine.Update(gained, 20).Count == 0, "Wrong territory cannot arm");
        rule.Branches.RemoveAt(2);
        rule.Branches[1].Parameter = 5;
        engine = new AdaptiveEngine(plan, 1); engine.Arm(100, 2, 1); engine.Update(gained, 2);
        Check(engine.Update(Array.Empty<StatusObservation>(), 20)[0].SlideId == "", "Parameter mismatch cannot select a branch");
        rule.Branches[1].Parameter = -1;
        rule.Enabled = false;
        engine = new AdaptiveEngine(plan, 1); engine.Arm(100, 2, 1);
        Check(engine.Update(gained, 2).Count == 0 && engine.Update(gained, 20).Count == 0, "Disabled rule cannot arm");
        rule.Enabled = true;
        var duplicate = new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100, Occurrence = 0 };
        duplicate.Branches.Add(new StatusBranch { StatusId = 10, SlideId = alternate.Id });
        plan.AdaptiveMechanics.Add(duplicate);
        engine = new AdaptiveEngine(plan, 1);
        Check(engine.ActiveRuleCount == 0, "Overlapping wildcard/specific rules cannot independently overwrite assignments");
        Console.WriteLine("PASS: status baseline, initial duration, countdown, refresh, gaps, occurrence, scope, settling, conflicts, timeout, one decision per arm");
    }

    private static void PermanentStatuses()
    {
        var tracker = new StatusTracker();
        tracker.Observe(Array.Empty<StatusSample>(), 0);
        var gained = tracker.Observe(new[] { new StatusSample(10, 0, 0, 99) }, 1);
        Check(gained.Count == 1 && !gained[0].DurationKnown && !gained[0].Removed,
            "A permanent status gain must have unknown duration instead of expiring immediately");
        var plan = PlanDocument.CreateDefault();
        var rule = new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100 };
        var branch = new StatusBranch { StatusId = 10, MaximumSeconds = 3600, SlideId = plan.Slides[0].Id };
        rule.Branches.Add(branch); plan.AdaptiveMechanics.Add(rule);
        var engine = new AdaptiveEngine(plan, 1); engine.Arm(100, 1, 0);
        engine.Update(gained, 1);
        Check(engine.Update(Array.Empty<StatusObservation>(), 1.4f)[0].SlideId == branch.SlideId,
            "A permanent status can select an explicitly unrestricted duration branch");
        branch.MaximumSeconds = 60;
        engine = new AdaptiveEngine(plan, 1); engine.Arm(100, 1, 0);
        engine.Update(gained, 1);
        Check(engine.Update(Array.Empty<StatusObservation>(), 15)[0].SlideId == "",
            "Permanent status unknown duration cannot satisfy a bounded duration branch");
    }

    private static void CompoundRules()
    {
        var plan = PlanDocument.CreateDefault();
        var branch = JsonConvert.DeserializeObject<StatusBranch>("{\"StatusId\":10,\"AdditionalStatuses\":[{\"StatusId\":11,\"Parameter\":2}]}")!;
        branch.SlideId = plan.Slides[0].Id;
        var rule = new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100 };
        rule.Branches.Add(branch); plan.AdaptiveMechanics.Add(rule);
        AdaptiveEngine NewEngine() { var e = new AdaptiveEngine(plan, 1); e.Arm(100, 1, 0); return e; }
        StatusObservation Status(uint id, float time, float duration = 30, string flags = "") =>
            JsonConvert.DeserializeObject<StatusObservation>($"{{\"StatusId\":{id},\"Time\":{time.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"Duration\":{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"Parameter\":2{flags}}}")!;
        var empty = Array.Empty<StatusObservation>();
        var engine = NewEngine();
        engine.Update(new[] { Status(10, 1) }, 1);
        Check(engine.Update(empty, 1.4f).Count == 0, "Partial conjunction must not start settling or select a slide");
        Check(engine.Update(new[] { Status(11, 3) }, 3).Count == 0, "Second asynchronous assignment starts settling only now");
        Check(engine.Update(empty, 3.2f).Count == 0, "Full conjunction must settle for 300ms");
        Check(engine.Update(empty, 3.4f)[0].SlideId == branch.SlideId, "Concurrent asynchronous assignments select compound branch");

        foreach (var invalid in new[] {
            Status(10, 1, 30, ",\"Baseline\":true"),
            Status(10, -1), Status(10, 1, .2f),
            Status(10, 1, 30, ",\"DurationKnown\":false") })
        {
            engine = NewEngine(); engine.Update(new[] { invalid }, 1);
            engine.Update(new[] { Status(11, 3) }, 3);
            Check(engine.Update(empty, 3.4f).Count == 0, "Baseline, stale, expired and unknown constrained duration cannot complete conjunction");
            Check(engine.Update(empty, 15)[0].SlideId == "", "Missing condition times out without destination");
        }
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        engine.Update(new[] { Status(10, 1.1f, 0, ",\"Removed\":true") }, 1.1f);
        Check(engine.Update(empty, 1.5f).Count == 0, "Removal invalidates pending compound match");
        engine.Update(new[] { Status(10, 2) }, 2);
        Check(engine.Update(empty, 2.4f)[0].SlideId == branch.SlideId, "Fresh reapplication restores conjunction");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1, .2f), Status(11, 1) }, 1);
        Check(engine.Update(empty, 1.4f).Count == 0, "Expiry during settling invalidates pending match");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1, 30, ",\"ParameterKnown\":false") }, 1);
        Check(engine.Update(empty, 1.4f).Count == 0, "Unknown exact parameter cannot satisfy additional condition");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        engine.Update(new[] { Status(11, 1.2f) }, 1.2f);
        Check(engine.Update(empty, 1.4f).Count == 0, "Refresh restarts settling");
        Check(engine.Update(empty, 1.6f)[0].SlideId == branch.SlideId, "Refreshed conjunction eventually settles");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        var changed = Status(11, 1.2f); changed.Parameter = 3;
        engine.Update(new[] { changed }, 1.2f);
        Check(engine.Update(empty, 1.6f).Count == 0, "Changed parameter invalidates previously matching evidence");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        engine.Update(new[] { Status(10, 1.1f, 0, ",\"Removed\":true") }, 1.1f);
        engine.Update(new[] { Status(10, 1) }, 1.2f);
        Check(engine.Update(empty, 1.6f).Count == 0, "Out-of-order old apply cannot resurrect a removed status");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 2), Status(11, 2) }, 1);
        Check(engine.Update(empty, 1.4f).Count == 0, "Future observations cannot complete a branch before their timestamp");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1.2f);
        Check(engine.Update(empty, 1.4f)[0].SlideId == branch.SlideId, "Repeated identical evidence does not restart settling");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 14.9f), Status(11, 14.9f) }, 14.9f);
        Check(engine.Update(empty, 15)[0].SlideId == "", "Window end cannot bypass the settling interval");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 14.9f), Status(11, 14.9f) }, 14.9f);
        Check(engine.Update(empty, 15.4f)[0].SlideId == "", "Delayed evaluation cannot settle an assignment beyond its acquisition window");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 14.6f), Status(11, 14.6f) }, 14.6f);
        Check(engine.Update(new[] { Status(10, 15.1f, 0, ",\"Removed\":true") }, 15.4f)[0].SlideId == "",
            "Post-window removal must invalidate a pending assignment before delayed evaluation");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 14.6f), Status(11, 14.6f) }, 14.6f);
        changed = Status(11, 15.1f); changed.Parameter = 3;
        Check(engine.Update(new[] { changed }, 15.4f)[0].SlideId == "",
            "Post-window parameter change must invalidate a pending assignment before delayed evaluation");
        engine = NewEngine();
        engine.Update(new[] { Status(10, 14.6f), Status(11, 14.6f) }, 14.6f);
        Check(engine.Update(empty, 15.4f)[0].SlideId == branch.SlideId,
            "Delayed evaluation may select a still-active assignment that settled before the deadline");
        var alternative = JsonConvert.DeserializeObject<StatusBranch>(JsonConvert.SerializeObject(branch))!;
        rule.Branches.Add(alternative); engine = NewEngine();
        engine.Update(new[] { Status(10, 1), Status(11, 1) }, 1);
        Check(engine.Update(empty, 1.4f)[0].SlideId == "", "Competing complete compound branches withhold navigation");
        rule.Branches.Remove(alternative);
        branch.MaximumSeconds = 3600;
        engine = NewEngine();
        engine.Update(new[] { Status(10, 1, 0, ",\"DurationKnown\":false"), Status(11, 1) }, 1);
        Check(engine.Update(empty, 1.4f)[0].SlideId == branch.SlideId, "Explicit unrestricted duration accepts unknown evidence duration");
        var old = JsonConvert.DeserializeObject<StatusObservation>("{\"StatusId\":10,\"Duration\":30}")!;
        Check(JsonConvert.SerializeObject(old).Contains("\"DurationKnown\":true"), "Legacy status JSON retains known duration default");
        Console.WriteLine("PASS: compound arrival, full settling, baseline, stale, removal, expiry, refresh, parameters, unknown duration and conflicts");
    }
}
