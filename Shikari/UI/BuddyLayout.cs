using System;
using System.Numerics;

namespace Shikari.UI;

/// <summary>Screen-space placement independent of live game or ImGui state.</summary>
public static class BuddyLayout
{
    public readonly record struct Placement(Vector2 Position, Vector2 Size, Vector2 SpriteMin,
        float SpriteSize, Vector2 BubbleMin, Vector2 BubbleSize, bool BubbleOnLeft);

    public static float FitScale(Vector2 viewport, float requested)
    {
        if (!float.IsFinite(requested)) requested = 1;
        var available = Vector2.Max(Vector2.One, Finite(viewport, Vector2.One));
        // Reserve room for the longest permitted cue and all shadow/motion insets. Text and
        // sprite shrink together only when a small viewport cannot hold the requested size.
        return MathF.Min(Math.Clamp(requested, .25f, 6f), MathF.Min(available.X / 370f, available.Y / 330f));
    }

    public static Placement Place(Vector2 viewportPosition, Vector2 viewportSize, Vector2 anchor,
        float scale, Vector2 bubbleSize)
    {
        var viewport = Vector2.Max(Vector2.One, Finite(viewportSize, Vector2.One));
        anchor = Vector2.Clamp(Finite(anchor, new Vector2(.76f, .70f)), Vector2.Zero, Vector2.One);
        scale = float.IsFinite(scale) && scale > 0 ? MathF.Min(scale, FitScale(viewport, scale)) : FitScale(viewport, 1);
        var spriteSize = 94f * scale;
        var padding = 9f * scale;
        var spriteMin = viewportPosition + viewport * anchor - new Vector2(spriteSize / 2);
        var hasBubble = bubbleSize.X > 0 && bubbleSize.Y > 0;
        var onLeft = anchor.X >= .5f;
        var bubbleMin = spriteMin + new Vector2(onLeft ? -bubbleSize.X - 9f * scale : spriteSize + 9f * scale,
            spriteSize * .47f - bubbleSize.Y / 2f);
        var min = hasBubble ? Vector2.Min(spriteMin, bubbleMin) : spriteMin;
        var max = hasBubble ? Vector2.Max(spriteMin + new Vector2(spriteSize), bubbleMin + bubbleSize) : spriteMin + new Vector2(spriteSize);
        min -= new Vector2(padding);
        max += new Vector2(padding);
        var size = max - min;
        var position = Vector2.Clamp(min, viewportPosition, viewportPosition + Vector2.Max(Vector2.Zero, viewport - size));
        return new Placement(position, size, spriteMin - min, spriteSize,
            hasBubble ? bubbleMin - min : Vector2.Zero, hasBubble ? bubbleSize : Vector2.Zero, onLeft);
    }

    public static Vector2 Anchor(Vector2 spriteCenter, Vector2 viewportPosition, Vector2 viewportSize) =>
        Vector2.Clamp((spriteCenter - viewportPosition) / Vector2.Max(Vector2.One, viewportSize), Vector2.Zero, Vector2.One);

    public static (float OffsetY, float Breath) Motion(double seconds, bool reducedMotion) => reducedMotion
        ? (0, 1)
        : ((float)Math.Sin(seconds * 1.7) * 1.35f, 1 + (float)Math.Sin(seconds * 2.1) * .006f);

    public static float Fade(double age, bool reducedMotion)
    {
        if (reducedMotion) return 1;
        var t = Math.Clamp((float)(age / .16), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static Vector2 Finite(Vector2 value, Vector2 fallback) => new(
        float.IsFinite(value.X) ? value.X : fallback.X,
        float.IsFinite(value.Y) ? value.Y : fallback.Y);
}
