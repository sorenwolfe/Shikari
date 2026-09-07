using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;

namespace Shikari.UI;

/// <summary>Mini-map presentation rules; never changes saved geometry or colours.</summary>
public static class MiniMapLayout
{
    /// <summary>Keep the arena square and leave a bounded scrollable footer within the viewport.</summary>
    public static Vector2 FitWindow(float boardSide, float footerHeight, Vector2 viewport)
    {
        var available = Vector2.Max(Vector2.One, viewport);
        var footer = Math.Clamp(footerHeight, 0, available.Y * 0.45f);
        var side = Math.Clamp(boardSide, 1, MathF.Max(1, MathF.Min(available.X, available.Y - footer)));
        return new Vector2(side, side + footer);
    }

    public static Vector2 ClampPosition(Vector2 position, Vector2 size, Vector2 viewportPosition, Vector2 viewportSize) =>
        Vector2.Clamp(position, viewportPosition, viewportPosition + Vector2.Max(Vector2.Zero, viewportSize - size));

    public static int Layer(CanvasItem item) => item.Kind switch
    {
        CanvasItemKind.Zone => 0,
        CanvasItemKind.EnemyToken => 1,
        CanvasItemKind.PlayerToken => 4,
        CanvasItemKind.Waymark => 3,
        _ => 2,
    };

    public static uint HazardFill(uint color) => (color & 0x00FFFFFF) | (Math.Min(color >> 24, 56u) << 24);

    // Multiple tokens for one seat may represent alternative positions. Never choose one arbitrarily.
    public static CanvasItem? Target(Slide slide, int slot)
    {
        if (slot < 0) return null;
        CanvasItem? found = null;
        foreach (var item in slide.Items)
        {
            if (item.Kind != CanvasItemKind.PlayerToken || item.SlotIndex != slot) continue;
            if (found != null) return null;
            found = item;
        }
        return found;
    }

    public static string Label(PlanDocument plan, CanvasItem item)
    {
        var slot = item.SlotIndex >= 0 && item.SlotIndex < plan.Roster.Count ? plan.Roster[item.SlotIndex] : null;
        var text = !string.IsNullOrWhiteSpace(item.Text) ? item.Text : slot?.Placeholder;
        if (string.IsNullOrWhiteSpace(text)) text = slot?.DisplayName;
        if (string.IsNullOrWhiteSpace(text) || text == "?") text = slot != null ? $"P{item.SlotIndex + 1}" : "—";
        return text.Length > 7 ? text[..6] + "…" : text;
    }

    public readonly record struct Box(Vector2 Min, Vector2 Max)
    {
        public Vector2 Center => (Min + Max) * 0.5f;
        public bool Overlaps(Box other) => Min.X < other.Max.X && Max.X > other.Min.X && Min.Y < other.Max.Y && Max.Y > other.Min.Y;
    }

    /// <summary>Move captions, never tokens. Search deterministically around the real board position.</summary>
    public static Box Place(Vector2 anchor, Vector2 size, Vector2 min, Vector2 max, IReadOnlyList<Box> occupied, float gap)
    {
        size = Vector2.Min(size, max - min);
        Box best = default;
        var bestScore = float.MaxValue;
        for (var ring = 0; ring < 6; ring++)
        {
            for (var direction = 0; direction < 8; direction++)
            {
                var angle = -MathF.PI / 2 + direction * MathF.PI / 4;
                var offset = new Vector2(MathF.Cos(angle) * (size.X / 2 + gap + ring * size.Y), MathF.Sin(angle) * (size.Y / 2 + gap + ring * (size.Y + gap)));
                var top = Vector2.Clamp(anchor + offset - size / 2, min, max - size);
                var box = new Box(top, top + size);
                var collisions = occupied.Count(other => box.Overlaps(other));
                var score = collisions * 100000f + Vector2.DistanceSquared(anchor, box.Center);
                if (score < bestScore) { best = box; bestScore = score; }
            }
            if (bestScore < 100000f) break;
        }
        return best;
    }
}
