using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.RaidPlanIo;
namespace Shikari.Tests;
public static class RaidPlanAreaTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(float actual, float expected, string message) => Check(MathF.Abs(actual - expected) < 0.0001f, $"{message}: expected {expected}, got {actual}");
    private static JObject Area(string id) => JObject.Parse("""
        {"type":"ability","attr":{"abilityId":"ff-ring","colorA":"#108210","opacity":0.5},
        "meta":{"pos":{"x":500,"y":400},"size":{"w":100,"h":100},"scale":{"x":1.73,"y":1.73},"angle":90,"origin":{"x":"center","y":"center"}}}
        """).WithId(id);
    private static JObject WithId(this JObject node, string id) { node["attr"]!["abilityId"] = id; return node; }
    private static PlanDocument Import(JArray objects, out RaidPlanIoReport report)
    {
        // Eight symmetric waymarks make the frame side exactly 1000 (470 / 0.47).
        var cy = 470f / 0.83f;
        foreach (var pair in new[] { ("A",-100,-100), ("B",100,-100), ("C",100,100), ("D",-100,100), ("1",0,-100), ("2",100,0), ("3",0,100), ("4",-100,0) })
            objects.Add(new JObject { ["type"] = "waypoint", ["attr"] = new JObject { ["wayId"] = pair.Item1 }, ["meta"] = new JObject { ["pos"] = new JObject { ["x"] = 500 + pair.Item2, ["y"] = cy + pair.Item3 } } });
        Check(RaidPlanIoImporter.TryImport(new JObject { ["nodes"] = objects }.ToString(), out var doc, out report, out var error), error);
        return doc!;
    }
    public static void Run()
    {
        var doc = Import(new JArray(Area("ff-ring"), Area("ff-circle"), Area("ff-donut")), out var report);
        var zones = doc.Slides[0].Items.Where(i => i.Kind == CanvasItemKind.Zone).ToArray();
        Check(zones.Length == 3, "Ring, circle and donut must all become editable areas");
        Check(zones[0].Zone == ZoneShape.Donut && zones[1].Zone == ZoneShape.Circle, "SVG ring and circle shape mapping");
        Near(zones[0].Radius, 0.0865f, "100px sprite diameter scaled by 1.73");
        Near(zones[0].InnerRadius, 0.082175f, "Thin ring hole must be 95%, not the donut's 50%");
        Near(zones[2].InnerRadius, 0.04325f, "Donut hole is 50%");
        Near(zones[0].Position.X, 0.5f, "Centered sprite x");
        Near(zones[0].Position.Y, 0.33373494f, "Centered sprite y");
        Near(zones[0].Rotation, 90f, "Clockwise source rotation");
        Check(zones[0].Color == 0x80108210, "Source ability colorA and opacity preserved before editor palette policy");
        var rect = Area("ff-square");
        rect["meta"]!["origin"] = new JObject { ["x"] = "left", ["y"] = "top" };
        rect["meta"]!["size"] = new JObject { ["w"] = 100, ["h"] = 50 };
        rect["meta"]!["scale"] = new JObject { ["x"] = 2, ["y"] = 2 };
        var square = Import(new JArray(rect), out _).Slides[0].Items[0];
        Near(square.Position.X, 0.45f, "Rotated top-left origin shifts center left by half height");
        Near(square.Position.Y, 0.43373494f, "Rotated top-left origin shifts center down by half width");
        Near(square.Extent.X, 0.1f, "Rectangle half width");
        Near(square.Extent.Y, 0.05f, "Rectangle half height");
        Import(new JArray(Area("unknown"), Area("unknown"), Area("other")), out report);
        Check(report.Notes.Count(n => n.Contains("unknown")) == 1 && report.Notes.Any(n => n.Contains("unknown") && n.Contains("2")), "Repeated unsupported areas aggregate with count");
        var path = JObject.Parse("""
          {"type":"path","attr":{"points":[0,0,100,100,200,0],"stroke":"#D0021B","strokeWidth":8},
           "meta":{"pos":{"x":500,"y":400},"size":{"w":20.016,"h":7.508},"origin":{"x":"left","y":"top"}}}
          """);
        var stroke = Import(new JArray(path), out _).Slides[0].Items[0];
        Check(stroke.Kind == CanvasItemKind.Freehand && stroke.Points.Count > 3, "Packed pencil points become editable sampled quadratic curves");
        Near(stroke.Points[0].X, 0.504f, "Pencil stroke starts half stroke-width inside its left anchor");
        Near(stroke.Points[^1].X, 0.524016f, "Pencil point packing divides by ten and retains brush end cap offset");
        Near(stroke.Points[^1].Y, 0.33773494f, "Pencil curve top aligned inside its top anchor");
        Near(stroke.Thickness, 0.008f, "Pencil stroke width uses source pixels");
        Check(stroke.Color == 0xFF1B02D0, "Pencil source stroke color");
        path["meta"]!["angle"] = 90;
        path["meta"]!["flip"] = new JObject { ["x"] = true };
        stroke = Import(new JArray(path), out _).Slides[0].Items[0];
        Near(stroke.Points[0].Y - stroke.Points[^1].Y, 0.020016f, "Pencil flip applied before clockwise rotation");
        Import(new JArray(new JObject { ["type"] = "itext", ["attr"] = new JObject { ["text"] = "Retained note" } }), out report);
        Check(!report.Summary().Contains("left out"), "Notes moved to slides are not reported as dropped objects");
        var proximity = Area("ff-area-prox");
        proximity["attr"]!["colorB"] = "#deff00";
        doc = Import(new JArray(proximity, Area("ff-knock")), out report);
        var markerParts = doc.Slides[0].Items.Where(i => i.Kind != CanvasItemKind.Waymark).ToArray();
        Check(markerParts.Length == 12, "Proximity and radial knockback markers retain their editable component geometry and labels");
        var ring = markerParts.Single(i => i.Kind == CanvasItemKind.Zone && i.Zone == ZoneShape.Donut);
        Near(ring.Radius, 0.0865f, "Proximity outer boundary follows source radius50");
        Near(ring.InnerRadius, 0.064875f, "Proximity gradient ring starts at source radius37.5");
        Check(ring.Color == 0x8000FFDE, "Proximity gradient boundary uses colorB and opacity");
        Near(markerParts.Single(i => i.Kind == CanvasItemKind.Zone && i.Zone == ZoneShape.Circle).Radius, 0.01384f, "Proximity source dot radius8");
        var arrows = markerParts.Where(i => i.Kind == CanvasItemKind.Arrow).ToArray();
        Check(arrows.Length == 8, "Knockback remains radial with all eight outward directions");
        Near(arrows[0].Points[^1].X, 0.57785f, "Rotated north knockback chevron points east at45% width");
        Near(arrows[0].Points[^1].Y, 0.33373494f, "Rotated knockback marker remains centered vertically");
        Check(arrows.All(a => System.Numerics.Vector2.DistanceSquared(a.Points[0], new(0.5f, 0.33373494f)) < System.Numerics.Vector2.DistanceSquared(a.Points[^1], new(0.5f, 0.33373494f))), "Knockback arrows point outward, never inward");
        Check(markerParts.Any(i => i.Kind == CanvasItemKind.Label && i.Text == "Prox") && markerParts.Any(i => i.Kind == CanvasItemKind.Label && i.Text == "KB"), "Compact semantic marker labels stay editable");
        Check(report.Notes.Count(n => n.Contains("simplified")) == 2 && !report.Summary().Contains("left out"), "Marker approximations disclosed without reporting them dropped");
        proximity["attr"]!["colorB"] = null;
        var fallbackRing = Import(new JArray(proximity, proximity.DeepClone()), out report).Slides[0].Items.First(i => i.Kind == CanvasItemKind.Zone && i.Zone == ZoneShape.Donut);
        Check(fallbackRing.Color == 0x80108210, "Missing secondary marker color applies opacity once");
        Check(report.Notes.Count(n => n.Contains("simplified")) == 1, "Repeated proximity markers share one simplification note");
        Console.WriteLine("Raidplan area geometry tests passed.");
    }
    public static void Replication(string json)
    {
        var root = JObject.Parse(json);
        Check(root.Value<string>("code") == "9ncP6UIDURcWuRuO", "Exact replication board identity");
        Check(RaidPlanIoImporter.TryImport(json, out var doc, out var report, out var error), error);
        Check(doc!.Slides.Count == 21 && report.ByType.GetValueOrDefault("ability") == 55, "All 55 real board abilities must import across 21 slides");
        Check(report.ByType.GetValueOrDefault("path") == 10, "All 10 real board pencil paths retained");
        Check(doc.Slides.SelectMany(s => s.Items).Count(i => i.Kind == CanvasItemKind.Zone && i.Zone == ZoneShape.Donut && MathF.Abs(i.InnerRadius / i.Radius - 0.95f) < 0.0001f) == 16, "All 16 authored thin rings retained");
        Console.WriteLine(report.Summary());
        foreach (var note in report.Notes) Console.WriteLine(note);
    }
    public static void CachedMarkers(string directory)
    {
        var proximity = 0; var knockback = 0;
        foreach (var file in System.IO.Directory.GetFiles(directory, "*.json"))
        {
            var json = System.IO.File.ReadAllText(file);
            var root = JObject.Parse(json);
            if (root["nodes"] is not JArray nodes) continue;
            var proxCount = nodes.Count(n => (string?)n["attr"]?["abilityId"] == "ff-area-prox");
            var knockCount = nodes.Count(n => (string?)n["attr"]?["abilityId"] == "ff-knock");
            if (proxCount + knockCount == 0) continue;
            Check(RaidPlanIoImporter.TryImport(json, out var doc, out var report, out var error), error);
            Check(!report.Skipped.ContainsKey("ability"), "All cached board abilities convert: " + file);
            var labels = doc!.Slides.SelectMany(s => s.Items).Where(i => i.Kind == CanvasItemKind.Label).ToArray();
            Check(labels.Count(i => i.Text == "Prox") == proxCount && labels.Count(i => i.Text == "KB") == knockCount, "Every cached marker retained: " + file);
            proximity += proxCount; knockback += knockCount;
        }
        Check(proximity == 80 && knockback == 3, "Cached Caro boards must retain all 80 proximity and 3 knockback markers");
        Console.WriteLine($"PASS: {proximity} proximity and {knockback} knockback markers retained across cached boards.");
    }
}
