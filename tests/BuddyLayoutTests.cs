using System;
using System.Numerics;
using Shikari.UI;

namespace Shikari.Tests;

public static class BuddyLayoutTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        foreach (var viewport in new[] { new Vector2(1920, 1080), new Vector2(800, 600), new Vector2(320, 240), new Vector2(160, 110) })
        foreach (var anchor in new[] { Vector2.Zero, Vector2.One, new Vector2(.5f), new Vector2(-5, 3), new Vector2(float.NaN) })
        foreach (var requested in new[] { .65f, 1f, 3.2f, float.NaN })
        {
            var scale = BuddyLayout.FitScale(viewport, requested);
            var layout = BuddyLayout.Place(new Vector2(40, 50), viewport, anchor, scale, new Vector2(240, 225) * scale);
            Check(float.IsFinite(scale) && scale > 0, "Scale must recover from corrupt settings");
            Check(layout.Position.X >= 40 && layout.Position.Y >= 50 && layout.Position.X + layout.Size.X <= viewport.X + 40.01f && layout.Position.Y + layout.Size.Y <= viewport.Y + 50.01f,
                "Complete companion and bubble must stay on screen at every scale and anchor");
            Check(layout.SpriteMin.X >= 0 && layout.SpriteMin.Y >= 0 && layout.SpriteMin.X + layout.SpriteSize <= layout.Size.X + .01f && layout.SpriteMin.Y + layout.SpriteSize <= layout.Size.Y + .01f,
                "Sprite must fit its input surface");
            Check(layout.BubbleMin.X >= 0 && layout.BubbleMin.Y >= 0 && layout.BubbleMin.X + layout.BubbleSize.X <= layout.Size.X + .01f && layout.BubbleMin.Y + layout.BubbleSize.Y <= layout.Size.Y + .01f,
                "Speech bubble must fit its surface without clipping");
        }
        var right = BuddyLayout.Place(Vector2.Zero, new Vector2(1920, 1080), new Vector2(.8f, .7f), 1, new Vector2(240, 120));
        var left = BuddyLayout.Place(Vector2.Zero, new Vector2(1920, 1080), new Vector2(.2f, .7f), 1, new Vector2(240, 120));
        Check(right.BubbleOnLeft && !left.BubbleOnLeft, "Speech should open toward the available screen space");
        var quiet = BuddyLayout.Place(Vector2.Zero, new Vector2(1920, 1080), new Vector2(.8f, .7f), 1, Vector2.Zero);
        Check(Vector2.Distance(right.Position + right.SpriteMin, quiet.Position + quiet.SpriteMin) < .01f, "Appearing speech must not move the creature when there is room");
        var anchorAgain = BuddyLayout.Anchor(right.Position + right.SpriteMin + new Vector2(right.SpriteSize / 2), Vector2.Zero, new Vector2(1920, 1080));
        Check(Vector2.Distance(anchorAgain, new Vector2(.8f, .7f)) < .001f, "Saved anchor must describe the creature, not the changing speech bubble");
        Check(BuddyLayout.Motion(1.3, false).OffsetY != BuddyLayout.Motion(2.3, false).OffsetY, "Idle motion should advance gently");
        Check(BuddyLayout.Motion(2.3, true) == (0f, 1f) && BuddyLayout.Fade(.01, true) == 1, "Reduced motion disables bob, breathing and transitions");
        Check(BuddyLayout.Fade(0, false) == 0 && BuddyLayout.Fade(1, false) == 1, "Cue animation must settle promptly");
        Console.WriteLine("Buddy layout: viewport fitting, stable anchors and reduced motion passed.");
    }
}
