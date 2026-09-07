using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;
using Shikari.UI;
namespace Shikari.Tests;
public static class MiniMapTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        // Dropping the viewport cap used to let long notes cover controls below the screen.
        var compact = MiniMapLayout.FitWindow(640, 900, new Vector2(800, 600));
        Check(compact.X <= 800 && compact.Y <= 600 && compact.Y > compact.X, "Board plus scrollable footer must fit the viewport");
        var normal = MiniMapLayout.FitWindow(220, 100, new Vector2(1920, 1080));
        Check(normal == new Vector2(220, 320), "Ordinary mini dimensions should stay unchanged");
        foreach (var viewport in new[] { new Vector2(320, 240), new Vector2(160, 160), new Vector2(800, 300) })
        {
            var fit = MiniMapLayout.FitWindow(640, 2000, viewport);
            Check(fit.X > 0 && fit.Y > fit.X && fit.X <= viewport.X && fit.Y <= viewport.Y, "Oversized mini must fit even at high UI scale");
            var pos = MiniMapLayout.ClampPosition(new Vector2(3000, -100), fit, new Vector2(40, 50), viewport);
            Check(pos.X >= 40 && pos.Y >= 50 && pos.X + fit.X <= 40 + viewport.X && pos.Y + fit.Y <= 50 + viewport.Y, "Off-screen anchors must leave the entire panel reachable");
        }
        var plan = PlanDocument.CreateDefault();
        var slide = plan.Slides[0];
        slide.Items.Clear();
        var target = new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(0.2f, 0.3f), Color = 0xFFCCBB99 };
        var hazard = new CanvasItem { Kind = CanvasItemKind.Zone, Layer = 100, Color = 0xFF0000EE };
        slide.Items.Add(target); slide.Items.Add(hazard);
        Check(slide.Items.OrderBy(MiniMapLayout.Layer).Last() == target, "Hazards must stay below tokens even with a higher saved layer");
        Check(MiniMapLayout.HazardFill(hazard.Color) == 0x380000EE && hazard.Color == 0xFF0000EE, "Presentation caps alpha without changing saved color");
        Check(MiniMapLayout.HazardFill(0x120000EE) == 0x120000EE, "Low opacity is preserved");
        Check(MiniMapLayout.Target(slide, -1) == null && MiniMapLayout.Target(slide, 0) == target, "Unresolved seats cannot become destinations");
        slide.Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0 });
        Check(MiniMapLayout.Target(slide, 0) == null, "Multiple alternatives must not select an arbitrary destination");
        target.Text = "Long player label";
        Check(MiniMapLayout.Label(plan, target).Length <= 7, "Mini captions are bounded");
        foreach (var side in new[] { 114f, 214f, 324f, 634f })
        {
            var occupied = new List<MiniMapLayout.Box>();
            var at = new Vector2(side * 0.2f, side * 0.3f);
            for (var i = 0; i < 8; i++)
            {
                var box = MiniMapLayout.Place(at, new Vector2(28, 20), Vector2.Zero, new Vector2(side), occupied, 9);
                Check(box.Min.X >= 0 && box.Min.Y >= 0 && box.Max.X <= side && box.Max.Y <= side, "Captions must stay inside the mini board");
                Check(!occupied.Any(box.Overlaps), "Eight stacked seat captions should separate at side " + side);
                occupied.Add(box);
            }
        }
        foreach (var at in new[] { Vector2.Zero, Vector2.One * 220, new Vector2(0, 110), new Vector2(220, 110) })
        {
            var box = MiniMapLayout.Place(at, new Vector2(90, 22), Vector2.Zero, new Vector2(220), Array.Empty<MiniMapLayout.Box>(), 21);
            Check(box.Min.X >= 0 && box.Min.Y >= 0 && box.Max.X <= 220 && box.Max.Y <= 220, "Edge destination caption remains readable");
        }
        Console.WriteLine("PASS: mini layers, non-mutating danger opacity, ambiguous seats, bounded labels, crowded captions and board edges");
    }
}
