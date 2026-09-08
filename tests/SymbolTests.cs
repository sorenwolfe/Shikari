using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Shikari.Model;
using Shikari.Services.Symbols;
using Shikari.UI;

namespace Shikari.Tests;

public static class SymbolTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        Check(EmojiCatalog.Count == 4009, "All pinned Twemoji assets must be catalogued");
        foreach (var (value, expected) in new[]
        {
            ("🐲", "1f432"), ("6️⃣", "36-20e3"), ("6⃣", "36-20e3"), ("7️⃣", "37-20e3"),
            ("🇺🇸", "1f1fa-1f1f8"), ("👩🏽‍🚀", "1f469-1f3fd-200d-1f680"), ("🫲", "1faf2"),
            ("❤️", "2764"), ("❤", "2764"), ("👩‍❤️‍💋‍👩", "1f469-200d-2764-fe0f-200d-1f48b-200d-1f469"),
            ("👩‍❤‍💋‍👩", "1f469-200d-2764-fe0f-200d-1f48b-200d-1f469")
        })
        {
            Check(EmojiCatalog.TryResolve(value, out var key) && key == expected, "Whole-cluster lookup failed: " + value);
            Check(EmojiCatalog.Truncate(value + "123456789", 2) == value + "…", "Truncation broke an emoji grapheme: " + value);
        }
        foreach (var invalid in new[] { "", "🐲🐲", "../1f432", "\ud800", "❤\uFE0E", "A", "\u200D", "🐲\u200D🫲" })
            Check(!EmojiCatalog.TryResolve(invalid, out _), "Unknown/invalid/multiple clusters must not guess a supported emoji: " + invalid);
        Check(EmojiCatalog.Fallback("🐲") == "Dragon" && EmojiCatalog.Fallback("6️⃣") == "6", "Important unavailable artwork needs a readable fallback");
        var item = new CanvasItem { Kind = CanvasItemKind.Symbol, Position = new(.4f, .6f), Extent = new(.1f, .025f), Rotation = 90, FlipX = true, FlipY = false };
        var quad = SymbolGeometry.Corners(item, item.Position * 1000, 1000, false, 1);
        Check(Vector2.Distance(quad.Min, new(375, 500)) < .01f && Vector2.Distance(quad.Max, new(425, 700)) < .01f, "Rotation must preserve nonuniform source dimensions");
        Check(SymbolGeometry.Contains(item, new(.4f, .69f)) && !SymbolGeometry.Contains(item, new(.49f, .6f)), "Selection must use the same rotated rectangle as the drawing");
        foreach (var extreme in new[] { float.MaxValue, float.MinValue })
        {
            item.Rotation = extreme;
            var extremeQuad = SymbolGeometry.Corners(item, item.Position * 1000, 1000, false, 1);
            Check(float.IsFinite(extremeQuad.Min.X) && float.IsFinite(extremeQuad.Max.Y) && SymbolGeometry.Contains(item, item.Position),
                "Every finite rotation must yield finite drawn and selectable geometry");
        }
        item.Rotation = 90;
        var uv = SymbolGeometry.Uvs(item);
        Check(uv.A == new Vector2(1, 0) && uv.B == Vector2.Zero && uv.C == new Vector2(0, 1), "Horizontal flips must affect texture UVs");
        item.FlipY = true; Check(SymbolGeometry.Uvs(item).A == Vector2.One, "Vertical flip must combine with horizontal flip");
        item.Extent = new(.012f, .016f);
        var half = SymbolGeometry.HalfSize(item, 220, true, 1);
        Check(half.X >= 9 && MathF.Abs(half.X / half.Y - .75f) < .0001f, "Mini status symbols must remain legible without distorting aspect ratio");
        item.Extent = new(.00001f, 4);
        var thin = SymbolGeometry.HalfSize(item, 220, true, 1);
        Check(thin.Y <= 880.01f && MathF.Abs(thin.X / thin.Y - item.Extent.X / item.Extent.Y) < .000001f,
            "A narrow symbol must not inflate its already-large long axis to reach the mini minimum");
        Check(SymbolGeometry.FallbackScale(new(100, 16), thin) == 0, "Unreadably thin fallback text must not escape its object");
        item.Extent = new(.0001f, .01f);
        var shortThin = SymbolGeometry.HalfSize(item, 220, true, 1);
        Check(shortThin.Y <= 48.01f, "Enlargement of small thin symbols must have a bounded long axis");
        var plan = PlanDocument.CreateDefault();
        item.Text = "12345👩🏽‍🚀78";
        Check(MiniMapLayout.Label(plan, item) == "12345👩🏽‍🚀…", "Mini label truncation must retain a complete compound emoji");
        Check(MiniMapLayout.Layer(item) > MiniMapLayout.Layer(new CanvasItem { Kind = CanvasItemKind.Zone }), "Mechanic symbols must remain above hazards in the mini");
        Console.WriteLine("Symbol tests: 4009-asset catalog, whole clusters, fallback, rotation, selection and mini sizing passed.");
    }
}
