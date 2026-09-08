using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Shikari.Model;
using Shikari.Services.Symbols;

namespace Shikari.UI;

public static class CanvasSymbols
{
    public static void Draw(ImDrawListPtr draw, CanvasItem item, Vector2 center, float boardSide, bool mini)
    {
        var bounds = SymbolGeometry.Corners(item, center, boardSide, mini, UiHelpers.Scale);
        if (item.SymbolAsset == SymbolAsset.Cut4)
        {
            DrawCutFour(draw, item, center, boardSide, mini);
            return;
        }
        var loaded = false;
        ImTextureID handle = default;
        if (item.Emoji.Length > 0 && EmojiCatalog.TryResolve(item.Emoji, out var key)) loaded = EmojiArtwork.TryHandle(key, out handle);
        else if (item.Emoji.Length == 0 && item.IconId > 0)
        {
            try
            {
                var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId));
                if (texture.TryGetWrap(out var wrap, out _) && wrap != null) { handle = wrap.Handle; loaded = true; }
            }
            catch { /* Fallback is still useful when the game lacks this artwork. */ }
        }
        if (loaded)
        {
            var uv = SymbolGeometry.Uvs(item);
            // Source colour is used only for opacity: tinting changes the meaning of status art.
            draw.AddImageQuad(handle, bounds.A, bounds.B, bounds.C, bounds.D, uv.A, uv.B, uv.C, uv.D, (item.Color & 0xFF000000) | 0xFFFFFF);
        }
        else
        {
            var text = !string.IsNullOrWhiteSpace(item.Text) ? EmojiCatalog.Truncate(item.Text, 18)
                : item.Emoji.Length > 0 ? EmojiCatalog.Fallback(item.Emoji) : "Symbol";
            draw.AddQuadFilled(bounds.A, bounds.B, bounds.C, bounds.D, 0xD91A1614);
            draw.AddQuad(bounds.A, bounds.B, bounds.C, bounds.D, 0xFFB9CDD8, UiHelpers.Scale);
            var measured = CanvasText.Measure(text);
            var fit = SymbolGeometry.FallbackScale(measured, SymbolGeometry.HalfSize(item, boardSide, mini, UiHelpers.Scale));
            // Fallback captions stay inside their board object; original text remains editable.
            if (fit * ImGui.GetFontSize() >= 5 * UiHelpers.Scale)
                CanvasText.Draw(draw, center - measured * fit / 2, text, 0xFFFFFFFF, fit);
        }
    }

    private static void DrawCutFour(ImDrawListPtr draw, CanvasItem item, Vector2 center, float side, bool mini)
    {
        // Exact authored SVG geometry: four r=9.5 circles in an 88x44 viewport. Keeping the
        // empty margins matters when the source object is scaled, mirrored or rotated.
        var half = SymbolGeometry.HalfSize(item, side, mini, UiHelpers.Scale);
        var matrix = Matrix3x2.CreateRotation(SymbolGeometry.Radians(item.Rotation));
        Vector2 Point(float x, float y)
        {
            var local = new Vector2((x / 88f - .5f) * 2 * half.X, (y / 44f - .5f) * 2 * half.Y);
            if (item.FlipX) local.X = -local.X;
            if (item.FlipY) local.Y = -local.Y;
            return center + Vector2.Transform(local, matrix);
        }
        var opacity = item.Color & 0xFF000000;
        foreach (var y in new[] { 11f, 33f })
        foreach (var x in new[] { 33f, 55f })
        {
            var points = new Vector2[32];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = i * 2 * MathF.PI / points.Length;
                points[i] = Point(x + MathF.Cos(angle) * 9.5f, y + MathF.Sin(angle) * 9.5f);
            }
            // ImGui's convex fill requires clockwise screen-space winding, including after a mirror.
            if (item.FlipX ^ item.FlipY) Array.Reverse(points);
            draw.AddConvexPolyFilled(ref points[0], points.Length, opacity | 0x00EAE9FE);
            var stroke = MathF.Max(.5f, MathF.Min(half.X * 2 / 88f, half.Y * 2 / 44f));
            draw.AddPolyline(ref points[0], points.Length, opacity | 0x002C1FF8, ImDrawFlags.Closed, stroke);
        }
    }

    public static void Outline(ImDrawListPtr draw, CanvasItem item, Vector2 center, float side, bool mini)
    {
        var q = SymbolGeometry.Corners(item, center, side, mini, UiHelpers.Scale);
        draw.AddQuad(q.A, q.B, q.C, q.D, 0xFFFFFFFF, 2 * UiHelpers.Scale);
    }
}
