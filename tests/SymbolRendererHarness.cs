using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Symbols;

namespace Dalamud.Interface.Textures
{
    public readonly record struct GameIconLookup(uint IconId);
    public interface ISharedImmediateTexture { bool TryGetWrap(out FakeWrap? wrap, out object? error); }
    public sealed class FakeWrap { public ImTextureID Handle; }
    public sealed class FakeTexture : ISharedImmediateTexture
    {
        public string Id = "";
        public bool TryGetWrap(out FakeWrap? wrap, out object? error) { wrap = new() { Handle = new(Id) }; error = null; return true; }
    }
}
namespace Dalamud.Bindings.ImGui
{
    public readonly record struct ImTextureID(string Id);
    public struct ImFontPtr { }
    public enum ImDrawFlags { None, Closed }
    public static class ImGui
    {
        public static float GetFontSize() => 16;
        public static ImFontPtr GetFont() => new();
        public static Vector2 CalcTextSize(string text, bool hide, float wrap) => new(text.Length * 7f, text.Length == 0 ? 0 : 16f);
    }
    public sealed class ImDrawListPtr : IDisposable
    {
        public Graphics? G;
        public string EmojiDirectory = "", IconDirectory = "";
        public readonly List<(string Id, Vector2 A, Vector2 B, Vector2 C, Vector2 D, Vector2 Uv, uint Tint)> Images = new();
        public readonly List<(Vector2 At, string Text, float Size)> Texts = new();
        public readonly List<Vector2[]> Polygons = new();
        public int Circles;
        private readonly Dictionary<string, Image> images = new();
        private static Color C(uint c) => Color.FromArgb((int)(c >> 24), (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255));
        private static PointF P(Vector2 p) => new(p.X, p.Y);
        public void Clear() { Images.Clear(); Texts.Clear(); Polygons.Clear(); Circles = 0; }
        public void AddImageQuad(ImTextureID id, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, uint tint)
        {
            Images.Add((id.Id, a, b, c, d, uvA, tint));
            if (G == null) return;
            if (!images.TryGetValue(id.Id, out var image))
            {
                var path = id.Id.StartsWith("game/") ? Path.Combine(IconDirectory, id.Id[5..] + ".png") : Path.Combine(EmojiDirectory, id.Id + ".png");
                if (!File.Exists(path)) return;
                images[id.Id] = image = Image.FromFile(path);
            }
            Vector2 Destination(float x, float y)
            {
                if (uvA.X > uvB.X) x = 1 - x;
                if (uvA.Y > uvD.Y) y = 1 - y;
                return a + (b - a) * x + (d - a) * y;
            }
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = (tint >> 24) / 255f }; attributes.SetColorMatrix(matrix);
            G.DrawImage(image, new[] { P(Destination(0, 0)), P(Destination(1, 0)), P(Destination(0, 1)) }, new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
        }
        public void AddImage(ImTextureID id, Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1, uint tint) =>
            AddImageQuad(id, min, new(max.X, min.Y), max, new(min.X, max.Y), uv0, new(uv1.X, uv0.Y), uv1, new(uv0.X, uv1.Y), tint);
        public void AddText(ImFontPtr font, float size, Vector2 at, uint color, string text, float wrap)
        {
            Texts.Add((at, text, size)); if (G == null) return;
            using var brush = new SolidBrush(C(color)); using var f = new Font("Segoe UI", size * .75f, FontStyle.Regular, GraphicsUnit.Pixel);
            G.DrawString(text, f, brush, P(at), StringFormat.GenericTypographic);
        }
        public void AddQuadFilled(Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint color) { if (G == null) return; using var brush = new SolidBrush(C(color)); G.FillPolygon(brush, new[] { P(a), P(b), P(c), P(d) }); }
        public void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint color, float width) { if (G == null) return; using var pen = new Pen(C(color), width); G.DrawPolygon(pen, new[] { P(a), P(b), P(c), P(d) }); }
        public void AddLine(Vector2 a, Vector2 b, uint color, float width) { if (G == null) return; using var pen = new Pen(C(color), width); G.DrawLine(pen, P(a), P(b)); }
        public void AddCircleFilled(Vector2 at, float radius, uint color, int count) { Circles++; if (G == null) return; using var brush = new SolidBrush(C(color)); G.FillEllipse(brush, at.X - radius, at.Y - radius, radius * 2, radius * 2); }
        public void AddCircle(Vector2 at, float radius, uint color, int count, float width) { if (G == null) return; using var pen = new Pen(C(color), width); G.DrawEllipse(pen, at.X - radius, at.Y - radius, radius * 2, radius * 2); }
        public void AddTriangleFilled(Vector2 a, Vector2 b, Vector2 c, uint color) { if (G == null) return; using var brush = new SolidBrush(C(color)); G.FillPolygon(brush, new[] { P(a), P(b), P(c) }); }
        public void AddRectFilled(Vector2 a, Vector2 b, uint color, float rounding) { if (G == null) return; using var brush = new SolidBrush(C(color)); G.FillRectangle(brush, a.X, a.Y, b.X - a.X, b.Y - a.Y); }
        public void AddRect(Vector2 a, Vector2 b, uint color, float rounding, ImDrawFlags flags, float width) => AddQuad(a, new(b.X, a.Y), b, new(a.X, b.Y), color, width);
        public void AddConvexPolyFilled(ref Vector2 first, int count, uint color)
        {
            var points = new Vector2[count]; for (var i = 0; i < count; i++) points[i] = Unsafe.Add(ref first, i);
            Polygons.Add(points); if (G == null) return;
            using var brush = new SolidBrush(C(color)); G.FillPolygon(brush, points.Select(P).ToArray());
        }
        public void AddPolyline(ref Vector2 first, int count, uint color, ImDrawFlags flags, float width)
        {
            if (G == null) return; var points = new PointF[count]; for (var i = 0; i < count; i++) points[i] = P(Unsafe.Add(ref first, i));
            using var pen = new Pen(C(color), width); if (flags == ImDrawFlags.Closed) G.DrawPolygon(pen, points); else G.DrawLines(pen, points);
        }
        public void Dispose() { foreach (var image in images.Values) image.Dispose(); images.Clear(); }
    }
}
namespace Shikari
{
    public static class Plugin { public static FakeProvider TextureProvider = new(); }
    public sealed class FakeProvider
    {
        public int Requests;
        public bool Fail;
        public Dalamud.Interface.Textures.ISharedImmediateTexture GetFromManifestResource(Assembly assembly, string name)
        { Requests++; if (Fail) throw new InvalidOperationException("Unavailable"); return new Dalamud.Interface.Textures.FakeTexture { Id = name["Shikari.Resources.Emoji.".Length..^4] }; }
        public Dalamud.Interface.Textures.ISharedImmediateTexture GetFromGameIcon(Dalamud.Interface.Textures.GameIconLookup lookup)
        { if (Fail) throw new InvalidOperationException("Unavailable"); return new Dalamud.Interface.Textures.FakeTexture { Id = "game/" + lookup.IconId }; }
    }
}
namespace Shikari.Services.Live
{
    public sealed class ArenaTracker { public readonly record struct LivePlayer(string Name, uint JobId, int SlotIndex, Vector2 Board, bool IsLocal); }
}
namespace Shikari.UI
{
    public static class UiHelpers
    {
        public static float Scale => 1;
        public static Vector2 TextSize(string text) => CanvasText.Measure(text);
        public static void CenteredShadowText(ImDrawListPtr draw, Vector2 center, string text, uint color) => CanvasText.Draw(draw, center - TextSize(text) / 2, text, color);
    }
    public sealed partial class ArenaCanvas
    {
        private Vector2 origin;
        private float side;
        public int HighlightSlot;
        public bool FocusOnMe, LiveGuides;
        public bool Settled => miniSettled;
        public float SettleDistance, SettleTolerance;
        public IReadOnlyList<Shikari.Services.Live.ArenaTracker.LivePlayer>? LivePlayers;
        private Vector2 ToScreen(Vector2 p) => origin + p * side;
        public void CheckCaptionBounds(ImDrawListPtr draw, string text)
        {
            origin = Vector2.Zero; side = 220; miniLabels.Clear();
            miniLabels.Add((new(110), text, 0xFFFFFFFF, 2));
            DrawMiniLabels(draw);
            if (miniPlaced.Count != 1 || miniPlaced.Any(p => CanvasText.Measure(p.Text).X > p.Box.Max.X - p.Box.Min.X - 9.99f))
                throw new Exception("Rendered mini captions must fit their placed box after unknown emoji expansion");
        }
        public void RenderSource(ImDrawListPtr draw, PlanDocument plan, Slide slide, Vector2 min, float boardSide, bool yourView)
        {
            origin = min; side = boardSide; MiniYourView = yourView; HighlightSlot = plan.Roster.FindIndex(s => s.Placeholder == "M2");
            miniLabels.Clear();
            foreach (var item in slide.Items.OrderBy(MiniMapLayout.Layer))
            {
                var at = ToScreen(item.Position);
                if (item.Kind == CanvasItemKind.PlayerToken) DrawMiniToken(draw, plan, item, at);
                if (item.Kind == CanvasItemKind.Symbol) CanvasSymbols.Draw(draw, item, at, side, true);
                if (item.Kind == CanvasItemKind.Waymark) { draw.AddCircleFilled(at, 8, item.Color, 24); UiHelpers.CenteredShadowText(draw, at, item.Text, 0xFFFFFFFF); }
            }
            DrawMiniDestination(draw, slide);
            DrawMiniLabels(draw);
        }
    }
}
namespace Shikari.Tests
{
    using Shikari.UI;
    public static class SymbolRendererTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static void Run()
        {
            EmojiArtwork.Forget(); Plugin.TextureProvider = new(); using var draw = new ImDrawListPtr();
            var item = new CanvasItem { Kind = CanvasItemKind.Symbol, Emoji = "🐲", Extent = new(.05f, .03f), Rotation = 90, FlipX = true, Color = 0x8055AA22 };
            CanvasSymbols.Draw(draw, item, new(100, 100), 400, false);
            var image = draw.Images.Single();
            Check(image.Id == "1f432" && image.Uv == new Vector2(1, 0) && image.Tint == 0x80FFFFFF, "Emoji must use its complete offline texture, source flip and opacity without recolouring art");
            Check(Vector2.Distance(image.A, new(112, 80)) < .01f, "Texture corner must follow the authored nonuniform extent and rotation");
            item.Emoji = ""; item.IconId = 214336;
            CanvasSymbols.Draw(draw, item, new(100, 100), 400, true);
            Check(draw.Images.Last().Id == "game/214336", "Imported status art must resolve as a native game icon, not a text glyph");
            var plan = PlanDocument.CreateDefault(); var slide = plan.Slides[0]; slide.Items.Clear();
            item.Position = new(.4f); slide.Items.Add(item);
            slide.Items.Add(new CanvasItem { Kind = CanvasItemKind.Symbol, Emoji = "🐲", Position = new(.8f,.5f), Extent = new(.05f), Color = 0xFFFFFFFF });
            for (var slot = 0; slot < 8; slot++) slide.Items.Add(new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = slot, Position = new(.4f + slot * .03f,.6f) });
            var canvas = new ArenaCanvas(); draw.Clear(); canvas.RenderSource(draw, plan, slide, Vector2.Zero, 220, false); var allCircles = draw.Circles;
            draw.Clear(); canvas.RenderSource(draw, plan, slide, Vector2.Zero, 220, true);
            Check(draw.Images.Count == 2 && draw.Circles < allCircles, "Your view must remove other players while keeping dragon and status art");
            draw.Clear(); var text = "A🐲6️⃣👩🏽‍🚀B"; var measured = CanvasText.Measure(text); CanvasText.Draw(draw, Vector2.Zero, text, 0xFFFFFFFF);
            Check(draw.Images.Count == 3 && draw.Texts.Last().Text == "B" && MathF.Abs(draw.Texts.Last().At.X + 7 - measured.X) < .01f,
                "Inline emoji drawing and measured caption advances must agree without splitting joined clusters");
            draw.Clear(); CanvasText.Draw(draw, Vector2.Zero, "x🐲\u200D🫲y", 0xFFFFFFFF);
            Check(draw.Images.Count == 0 && draw.Texts.Any(t => t.Text.Contains("[symbol]")), "Unsupported combined clusters must get readable fallback rather than partial emoji");
            draw.Clear(); canvas.CheckCaptionBounds(draw, string.Concat(Enumerable.Repeat("🐲\u200D🫲", 7)));
            EmojiArtwork.Forget(); Plugin.TextureProvider.Fail = true; Plugin.TextureProvider.Requests = 0;
            item.Emoji = "🐲"; item.IconId = 0; item.Extent = new(.15f); item.Rotation = 0; draw.Clear();
            CanvasSymbols.Draw(draw, item, new(100), 400, false); CanvasSymbols.Draw(draw, item, new(100), 400, false);
            Check(Plugin.TextureProvider.Requests == 1 && draw.Texts.All(t => t.Text == "Dragon"), "Missing artwork must retain a readable caption and avoid repeated resource lookups");
            EmojiArtwork.Forget(); Plugin.TextureProvider.Fail = false; var before = Plugin.TextureProvider.Requests;
            Check(EmojiArtwork.TryHandle("1f432", out _) && Plugin.TextureProvider.Requests == before + 1, "Unload cleanup must release cached failure/texture references");
            item.Emoji = ""; item.SymbolAsset = SymbolAsset.Cut4; item.Extent = new(.11f,.055f); item.Rotation = 31; item.FlipY = true; draw.Clear();
            CanvasSymbols.Draw(draw, item, new(100), 400, false);
            Check(draw.Polygons.Count == 4 && draw.Images.Count == 0, "Four-hit marker must render all source circles as one transformed symbol");
            var q = SymbolGeometry.Corners(item, new(100), 400, false, 1);
            Check(draw.Polygons.SelectMany(p => p).All(p => p.X >= q.Min.X && p.Y >= q.Min.Y && p.X <= q.Max.X && p.Y <= q.Max.Y), "Procedural marker must preserve the source viewport and transforms");
            item.Rotation = float.MaxValue; draw.Clear(); CanvasSymbols.Draw(draw, item, new(100), 400, false);
            Check(draw.Polygons.SelectMany(p => p).All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)), "Procedural symbols must handle every finite authored rotation");
            foreach (var flipX in new[] { false, true })
            foreach (var flipY in new[] { false, true })
            {
                item.FlipX = flipX; item.FlipY = flipY; draw.Clear(); CanvasSymbols.Draw(draw, item, new(100), 400, false);
                Check(draw.Polygons.All(points => Enumerable.Range(0, points.Length).Sum(i =>
                    points[i].X * points[(i + 1) % points.Length].Y - points[(i + 1) % points.Length].X * points[i].Y) > 0),
                    "Every mirrored procedural polygon must retain clockwise screen-space winding for ImGui antialiasing");
            }
            Console.WriteLine("Symbol renderer: native/emoji art, transforms, Your view, whole-cluster text, fallback, cache lifetime and Cut4 passed.");
        }

        public static void Render(string output, string emojiDirectory, string iconDirectory, string source)
        {
            if (!Shikari.Services.RaidPlanIo.RaidPlanIoImporter.TryImport(source, out var plan, out _, out var error)) throw new Exception(error);
            var slide = plan!.Slides.Single(s => s.SourceStep == 7);
            EmojiArtwork.Forget(); Plugin.TextureProvider = new();
            using var bitmap = new Bitmap(1170, 760); using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.FromArgb(9, 9, 13));
            using var title = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
            using var body = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var caption = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(240, 240, 245)); using var muted = new SolidBrush(Color.FromArgb(157, 157, 168));
            using var red = new SolidBrush(Color.FromArgb(239, 64, 84)); using var grid = new Pen(Color.FromArgb(32, 32, 38));
            g.DrawString("GROTESQUERIE: ACT 1", title, red, new PointF(40, 26));
            g.DrawString("Editable dragon and status symbols, retained at their source positions", body, white, new PointF(42, 66));
            using var draw = new ImDrawListPtr { G = g, EmojiDirectory = emojiDirectory, IconDirectory = iconDirectory };
            for (var panel = 0; panel < 2; panel++)
            {
                var min = new Vector2(40 + panel * 565, 158); const float size = 520;
                g.DrawString(panel == 0 ? "Imported board" : "Your view · M2", body, white, new PointF(min.X, 120));
                draw.AddRectFilled(min, min + new Vector2(size), 0xFF131216, 6);
                var state = g.Save(); g.SetClip(new RectangleF(min.X, min.Y, size, size));
                for (var i = 0; i <= 10; i++) { g.DrawLine(grid, min.X + i * size / 10, min.Y, min.X + i * size / 10, min.Y + size); g.DrawLine(grid, min.X, min.Y + i * size / 10, min.X + size, min.Y + i * size / 10); }
                draw.AddCircle(min + new Vector2(size / 2), size * .47f, 0xFF565057, 96, 1.5f);
                new ArenaCanvas().RenderSource(draw, plan, slide, min, size, panel == 1);
                g.Restore(state);
            }
            g.DrawString("Source step 8 · Caro linked board 44JJjqZ6Mcgaxnnn. Symbols and player captions use production drawing helpers.", caption, muted, new PointF(42, 704));
            g.DrawString("Drawing API substitute, not an in-game screenshot. Reference background and hazard geometry omitted to inspect symbols.", caption, muted, new PointF(42, 726));
            bitmap.Save(output, ImageFormat.Png);
        }
    }
}
