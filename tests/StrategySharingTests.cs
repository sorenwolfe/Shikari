using System;
using System.Numerics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
namespace Shikari.Tests;
public static class StrategySharingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var plan = PlanDocument.CreateDefault();
        Check(plan.FormatVersion == 1, "Plain plans must remain readable by legacy clients");
        plan.Slides[0].ArenaOverride = new ArenaSettings { Shape = ArenaShape.Rectangle, AspectRatio = 2.5f, ShowGrid = false };
        Check(plan.FormatVersion >= 4, "Per-slide arenas must require a client that understands their geometry");
        plan.Slides[0].SourceUrl = "https://raidplan.io/plan/first#2";
        plan.Slides[0].GuideUrl = "https://wtfdig.info/74/m12s#caro";
        plan.Slides[0].SourceLabel = "First board"; plan.Slides[0].SourceStep = 1;
        plan.Slides.Add(new Slide { SourceUrl = "https://raidplan.io/plan/second#1", SourceStep = 0, ArenaOverride = new ArenaSettings { Shape = ArenaShape.Square } });
        var entry = new TimelineEntry { SlideId = plan.Slides[0].Id }; plan.Timeline.Add(entry);
        plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { Key = "local-test", Mechanics = new()
        {
            new() { EntryId = entry.Id, SlideId = plan.Slides[0].Id, Match = "action", ActionId = 123, CastTime = 10, ResolveTime = 12,
                Actors = new() { new() { SlotIndex = 0, Positions = new() { new() { Time = 12, Position = new Vector2(.25f, .75f), Calibrated = true } } } } }
        } });
        Check(ShareCode.TryDecode(ShareCode.Encode(plan), out var decoded, out var error), "Composite evidence round trip failed: " + error);
        Check(decoded!.Slides[0].ArenaOverride!.AspectRatio == 2.5f && !decoded.Slides[0].ArenaOverride!.ShowGrid && decoded.Slides[1].ArenaOverride!.Shape == ArenaShape.Square, "Per-board arena properties changed in sharing");
        Check(decoded.Slides[0].SourceUrl == plan.Slides[0].SourceUrl && decoded.Slides[0].SourceStep == 1 && decoded.Slides[1].SourceStep == 0, "Source links and exact zero source step must survive compact serialization");
        Check(decoded.Slides[0].GuideUrl == plan.Slides[0].GuideUrl, "Guide provenance must survive the separate source-board link");
        Check(decoded.StrategyEvidence[0].Mechanics[0].EntryId == decoded.Timeline[0].Id && decoded.StrategyEvidence[0].Mechanics[0].SlideId == decoded.Slides[0].Id, "Evidence links must still reference authored slides and timeline entries");
        Check(decoded.StrategyEvidence[0].Mechanics[0].Actors[0].Positions[0].Position == new Vector2(.25f, .75f), "Evidence position changed during sharing");
        Check(JsonConvert.SerializeObject(plan, PlanJson.Compact()).Contains("\"FormatVersion\":4"), "Version 4 must be explicit on the wire so older clients reject it");
        var evidenceOnly = PlanDocument.CreateDefault(); evidenceOnly.StrategyEvidence.Add(new StrategyEvidenceAttachment { Key = "evidence" });
        Check(evidenceOnly.FormatVersion >= 4, "Evidence must not silently disappear on older clients");
        var provenanceOnly = PlanDocument.CreateDefault(); provenanceOnly.Slides[0].SourceUrl = "https://raidplan.io/plan/source";
        Check(provenanceOnly.FormatVersion >= 4, "Source alignment domains must not be silently dropped by old clients");
        provenanceOnly.Slides[0].SourceUrl = ""; provenanceOnly.Slides[0].GuideUrl = "https://wtfdig.info/74/m12s#caro";
        Check(provenanceOnly.FormatVersion >= 4, "Guide provenance must require a compatible client");
        var inferredOnly = PlanDocument.CreateDefault(); inferredOnly.Timeline.Add(new TimelineEntry { InferredCastActionId = 123 });
        Check(inferredOnly.FormatVersion >= 4, "Inferred-anchor semantics must survive after old evidence attachments are pruned");
        plan.StrategyEvidence[0].Mechanics[0].Actors[0].Positions[0].Position = new Vector2(float.NaN, 0);
        Check(!ShareCode.TryDecode(ShareCode.Encode(plan), out _, out _), "Nonfinite strategy evidence was accepted from sharing");
        PlanNormaliser.Normalise(plan);
        Check(plan.StrategyEvidence.Count == 0 && plan.Slides.Count == 2 && plan.Timeline[0].Id == entry.Id, "Disk repair must discard malformed evidence and preserve authored content");
        plan.Slides[0].ArenaOverride!.AspectRatio = float.NaN;
        Check(!ShareCode.TryDecode(ShareCode.Encode(plan), out _, out _), "Nonfinite slide arena was accepted from sharing");
        plan.Slides[0].ArenaOverride!.GridDivisions = int.MaxValue;
        plan.Slides[0].SourceUrl = new string('x', 5000); plan.Slides[0].SourceLabel = new string('x', 1000); plan.Slides[0].SourceStep = int.MaxValue;
        PlanNormaliser.Normalise(plan);
        Check(float.IsFinite(plan.Slides[0].ArenaOverride!.AspectRatio) && plan.Slides[0].ArenaOverride!.GridDivisions <= 64, "Disk arena repair must bound rendering work and coordinates");
        Check(plan.Slides[0].SourceUrl.Length <= 2048 && plan.Slides[0].SourceLabel.Length <= 256 && plan.Slides[0].SourceStep <= 4096, "Disk metadata must be bounded");
        Check(ShareCode.TryDecode(ShareCode.Encode(plan), out _, out error), "Normalized plan must remain shareable: " + error);
        foreach (var source in new[] { "javascript:alert(1)", "https://user:pass@raidplan.io/plan/id", "https://raidplan.io/\nplan/id", new string('x', 2049) })
        {
            plan.Slides[1].SourceUrl = source;
            Check(!ShareCode.TryDecode(ShareCode.Encode(plan), out _, out _), "Invalid source metadata was accepted from sharing");
        }
        plan.Slides[1].SourceUrl = ""; plan.Slides[1].GuideUrl = new string('x', 2049);
        Check(!ShareCode.TryDecode(ShareCode.Encode(plan), out _, out _), "Oversized guide provenance was accepted from sharing");
        Console.WriteLine("PASS: composite/evidence sharing, version 4 compatibility, exact source steps, references, malformed-data rejection and disk repair");
    }
}
