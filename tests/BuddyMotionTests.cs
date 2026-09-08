using System;
using System.Linq;
using System.Numerics;
using Shikari.Services.Buddy;
using Shikari.UI;

namespace Shikari.Tests;

public static class BuddyMotionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        foreach (var state in Enum.GetValues<BuddyAmbientState>())
        foreach (var scale in new[] { .2f, 1f, 2f, 4f })
        for (var step = 0; step < 100; step++)
        {
            var motion = BuddyMotion.Sample(state, step * .11, step * .025, false, false, false);
            var size = BuddyLayout.BaseSpriteSize * scale;
            var quad = BuddyMotion.Corners(Vector2.Zero, size, motion);
            var extent = BuddyLayout.MotionPadding * scale;
            Check(new[] { quad.A, quad.B, quad.C, quad.D }.All(p => p.X >= -extent && p.Y >= -extent && p.X <= size + extent && p.Y <= size + extent), "Animated quad must fit reserved viewport insets");
            Check(motion.Particles.All(p => p.Offset.X * size - p.Size * scale >= -extent - size / 2 && p.Offset.X * size + p.Size * scale <= size / 2 + extent &&
                p.Offset.Y * size - p.Size * scale >= -extent - size / 2 && p.Offset.Y * size + p.Size * scale <= size / 2 + extent), "Particles must fit reserved viewport insets");
            var reduced = BuddyMotion.Sample(state, step, step, true, false, false);
            Check(reduced.Pose == state && reduced.Offset == Vector2.Zero && reduced.Scale == Vector2.One && reduced.Rotation == 0 && reduced.Particles.Length == 0,
                "Reduced motion retains the meaningful pose without movement or particles");
        }
        Check(BuddyLayout.BaseSpriteSize == 132, "The companion should be comfortably larger at existing saved scale");
        Check(BuddyMotion.Sample(BuddyAmbientState.Idle, 1, 1, false, false, false).Offset != BuddyMotion.Sample(BuddyAmbientState.Idle, 2, 2, false, false, false).Offset,
            "Breathing should advance gently between frames");
        Check(BuddyMotion.Sample(BuddyAmbientState.Welcoming, 3, 3, false, false, false).Particles.Length == 0, "Welcome celebration must finish after its brief response");
        var sleepy = BuddyMotion.Sample(BuddyAmbientState.Sleeping, 1, 1, false, false, false);
        Check(sleepy.Particles.Length > 0 && sleepy.Particles.All(p => p.Sleep), "Sleeping uses quiet rising Z particles");
        var tactical = BuddyMotion.Sample(BuddyAmbientState.Welcoming, 1, 1, false, true, false);
        Check(tactical.Pose == BuddyAmbientState.Focused && tactical.Particles.Length == 0, "Tactical instructions override welcome celebration");
        var recovery = BuddyMotion.Sample(BuddyAmbientState.Sleeping, 1, 1, false, false, true);
        Check(recovery.Pose == BuddyAmbientState.Idle && recovery.Offset == Vector2.Zero && recovery.Particles.Length == 0, "Recovery is quiet and static");
        foreach (var state in Enum.GetValues<BuddyAmbientState>())
        {
            var uv = BuddyMotion.Uvs(state, false); var mirrored = BuddyMotion.Uvs(state, true);
            var cell = new Vector2((int)state % 2, (int)state / 2) * .5f;
            Check(uv.Min.X >= cell.X && uv.Min.Y >= cell.Y && uv.Max.X <= cell.X + .5f && uv.Max.Y <= cell.Y + .5f, "Cropped pose stays within its atlas cell");
            Check(mirrored.Min.X == uv.Max.X && mirrored.Max.X == uv.Min.X && mirrored.Min.Y == uv.Min.Y, "Mirroring stays within the selected pose");
            var rect = BuddyMotion.SpriteRect(state);
            Check(rect.Max.Y == .95f && MathF.Abs((rect.Max.X - rect.Min.X) / (rect.Max.Y - rect.Min.Y) - (uv.Max.X - uv.Min.X) / (uv.Max.Y - uv.Min.Y)) < .001f,
                "Cropped poses must share the baseline and retain their original aspect ratio");
        }
        Console.WriteLine("Buddy motion: all ambient poses, tactical/recovery priority, reduced motion, UVs and animation envelopes passed.");
    }
}
