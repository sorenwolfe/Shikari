using System;
using System.Numerics;
using Shikari.Model;

namespace Shikari.UI;

public static class SymbolGeometry
{
    public readonly record struct Quad(Vector2 A, Vector2 B, Vector2 C, Vector2 D)
    {
        public Vector2 Min => Vector2.Min(Vector2.Min(A, B), Vector2.Min(C, D));
        public Vector2 Max => Vector2.Max(Vector2.Max(A, B), Vector2.Max(C, D));
    }

    public static Vector2 HalfSize(CanvasItem item, float boardSide, bool mini, float uiScale)
    {
        var size = Vector2.Abs(item.Extent) * boardSide;
        if (mini)
        {
            // Preserve the authored aspect ratio while keeping small status pictures legible.
            var shortest = MathF.Max(.000001f, MathF.Min(size.X, size.Y));
            var longest = MathF.Max(.000001f, MathF.Max(size.X, size.Y));
            var desired = MathF.Max(1, 9f * uiScale / shortest);
            var limited = MathF.Max(1, 48f * uiScale / longest);
            size *= MathF.Min(desired, limited);
        }
        return size;
    }

    public static float FallbackScale(Vector2 measured, Vector2 halfSize)
    {
        // A square inscribed in the narrow dimension stays inside the source rectangle at
        // every rotation. No forced minimum scale may spill text beyond tiny/thin objects.
        var available = MathF.Max(0, MathF.Min(halfSize.X, halfSize.Y) * 1.41421356f - 4);
        return MathF.Min(1, available / MathF.Max(1, MathF.Max(measured.X, measured.Y)));
    }

    public static Quad Corners(CanvasItem item, Vector2 center, float boardSide, bool mini, float uiScale)
    {
        var half = HalfSize(item, boardSide, mini, uiScale);
        var radians = Radians(item.Rotation);
        Vector2 Point(float x, float y) => center + Vector2.Transform(new Vector2(x, y), Matrix3x2.CreateRotation(radians));
        return new(Point(-half.X, -half.Y), Point(half.X, -half.Y), Point(half.X, half.Y), Point(-half.X, half.Y));
    }

    public static bool Contains(CanvasItem item, Vector2 normalizedPoint)
    {
        var local = Vector2.Transform(normalizedPoint - item.Position, Matrix3x2.CreateRotation(-Radians(item.Rotation)));
        var half = Vector2.Abs(item.Extent);
        return MathF.Abs(local.X) <= half.X && MathF.Abs(local.Y) <= half.Y;
    }

    public static float Radians(float degrees) => (degrees % 360f) * (MathF.PI / 180f);

    public static Quad Uvs(CanvasItem item)
    {
        var left = item.FlipX ? 1f : 0f; var right = 1 - left;
        var top = item.FlipY ? 1f : 0f; var bottom = 1 - top;
        return new(new(left, top), new(right, top), new(right, bottom), new(left, bottom));
    }
}
