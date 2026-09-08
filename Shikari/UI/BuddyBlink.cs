using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Buddy;

namespace Shikari.UI;

/// <summary>A brief idle blink drawn over the bundled artwork without changing its pixels.</summary>
public static class BuddyBlink
{
    private static readonly double[] Starts = { 4.8, 11, 16.4, 22, 22.32 };
    // Source atlas pixels, tightly following the two idle eye openings beneath the brows.
    // Keep the artist's surrounding face and dark upper lashes visible.
    private static readonly Vector2[] NearEye =
    {
        new(299, 223), new(306, 213), new(315, 207), new(324, 205), new(333, 208),
        new(341, 215), new(347, 228), new(352, 246), new(354, 264),
        new(339, 265), new(325, 261), new(314, 254), new(305, 243), new(300, 232),
    };
    private static readonly Vector2[] FarEye =
    {
        new(403, 224), new(408, 215), new(411, 214), new(415, 224), new(417, 235),
        new(417, 245), new(414, 254), new(408, 261), new(402, 264), new(400, 250), new(401, 237),
    };

    public static float Sample(BuddyAmbientState state, double seconds, bool reduced, bool tactical, bool recovering)
    {
        if (state != BuddyAmbientState.Idle || reduced || tactical || recovering || !double.IsFinite(seconds) || seconds < 0) return 0;
        var phase = seconds % 24;
        foreach (var start in Starts)
        {
            var elapsed = phase - start;
            if (elapsed is < 0 or >= .19) continue;
            if (elapsed < .06) return Ease((float)(elapsed / .06));
            if (elapsed < .09) return 1;
            return 1 - Ease((float)((elapsed - .09) / .1));
        }
        return 0;
    }

    public static void Draw(ImDrawListPtr draw, BuddyMotion.Quad quad, (Vector2 Min, Vector2 Max) uv, float closure)
    {
        if (!float.IsFinite(closure) || closure <= 0) return;
        closure = MathF.Min(1, closure);
        Eye(draw, quad, uv, NearEye, closure, .18f, 145, 205, 192, 0xFF1639E8, 0xFF1533E0);
        Eye(draw, quad, uv, FarEye, closure, -.5f, 415, 466, 449, 0xFF1331DE, 0xFF1433DE);
    }

    private static float Ease(float value) => value * value * (3 - 2 * value);

    private static void Eye(ImDrawListPtr draw, BuddyMotion.Quad quad, (Vector2 Min, Vector2 Max) uv,
        Vector2[] outline, float closure, float slope, float top, float bottom, float seam, uint upperColor, uint lowerColor)
    {
        // Both halves are clipped by a moving line in source coordinates. Intersecting
        // a convex eye with a half-plane keeps every fill valid for ImGui's convex API.
        var upperEdge = top + (seam - top) * closure;
        var lowerEdge = bottom + (seam - bottom) * closure;
        Span<Vector2> points = stackalloc Vector2[20];
        Fill(draw, quad, uv, points[..Clip(outline, points, slope, upperEdge, true)], upperColor);
        Fill(draw, quad, uv, points[..Clip(outline, points, slope, lowerEdge, false)], lowerColor);
        // A few faint nested tints echo the artwork's darker brow and warmer lower lid.
        // The opaque base already covers the eye, so antialiased tint edges cannot leak iris pixels.
        for (var band = 1; band <= 8; band++)
        {
            var edge = MathF.Min(upperEdge, top + (seam - top) * band / 8);
            Fill(draw, quad, uv, points[..Clip(outline, points, slope, edge, true)], 0x09091039);
        }

        // The lash travels with the upper lid. Keep the line inside the original eye;
        // it is only visible once there is enough skin above it to read as an eyelid.
        if (closure < .15f) return;
        Span<Vector2> intersections = stackalloc Vector2[2];
        var count = 0;
        for (var i = 0; i < outline.Length && count < 2; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Length];
            var da = a.Y - slope * a.X - upperEdge;
            var db = b.Y - slope * b.X - upperEdge;
            if ((da < 0) == (db < 0)) continue;
            intersections[count++] = Vector2.Lerp(a, b, da / (da - db));
        }
        if (count != 2) return;
        var left = intersections[0].X < intersections[1].X ? intersections[0] : intersections[1];
        var right = intersections[0].X < intersections[1].X ? intersections[1] : intersections[0];
        var width = Vector2.Distance(Map(left, quad, uv), Map(left + new Vector2(0, 2.4f), quad, uv));
        var previous = Map(left, quad, uv);
        for (var step = 1; step <= 8; step++)
        {
            var amount = step / 8f;
            var pixel = Vector2.Lerp(left, right, amount);
            pixel.Y += MathF.Sin(amount * MathF.PI) * 2.3f * closure;
            var next = Map(pixel, quad, uv);
            draw.AddLine(previous, next, 0xFF10122C, width);
            previous = next;
        }
    }

    private static int Clip(Vector2[] outline, Span<Vector2> result, float slope, float edge, bool upper)
    {
        var count = 0;
        for (var i = 0; i < outline.Length; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Length];
            var da = a.Y - slope * a.X - edge;
            var db = b.Y - slope * b.X - edge;
            var aInside = upper ? da <= 0 : da >= 0;
            var bInside = upper ? db <= 0 : db >= 0;
            if (aInside) result[count++] = a;
            if (aInside != bInside) result[count++] = Vector2.Lerp(a, b, da / (da - db));
        }
        return count;
    }

    private static void Fill(ImDrawListPtr draw, BuddyMotion.Quad quad, (Vector2 Min, Vector2 Max) uv, ReadOnlySpan<Vector2> points, uint color)
    {
        if (points.Length < 3) return;
        draw.PathClear();
        if (uv.Min.X > uv.Max.X)
            for (var i = points.Length - 1; i >= 0; i--) draw.PathLineTo(Map(points[i], quad, uv));
        else
            foreach (var p in points) draw.PathLineTo(Map(p, quad, uv));
        draw.PathFillConvex(color);
    }

    private static Vector2 Map(Vector2 pixel, BuddyMotion.Quad quad, (Vector2 Min, Vector2 Max) uv)
    {
        var unit = (pixel / 1254f - uv.Min) / (uv.Max - uv.Min);
        return Vector2.Lerp(Vector2.Lerp(quad.A, quad.B, unit.X), Vector2.Lerp(quad.D, quad.C, unit.X), unit.Y);
    }
}
