using System;
using System.Numerics;
using Shikari.Services.Buddy;

namespace Shikari.UI;

/// <summary>Small deterministic movements within the companion's fixed drag surface.</summary>
public static class BuddyMotion
{
    public readonly record struct Particle(Vector2 Offset, float Size, float Alpha, bool Sleep);
    public readonly record struct Frame(BuddyAmbientState Pose, Vector2 Offset, Vector2 Scale, float Rotation, Particle[] Particles);
    public readonly record struct Quad(Vector2 A, Vector2 B, Vector2 C, Vector2 D);
    private const float CellSize = 627;
    // Nonzero alpha bounds of the bundled four-pose atlas, plus a transparent sampling margin.
    // A common source-pixel scale preserves Ember's proportions as he lies down or stands up.
    private static readonly (Vector2 Min, Vector2 Max)[] PoseBounds =
    {
        (new(73, 98), new(497, 612)), (new(71, 250), new(534, 550)),
        (new(104, 73), new(535, 533)), (new(108, 39), new(518, 535)),
    };

    public static Frame Sample(BuddyAmbientState state, double seconds, double age, bool reduced, bool tactical, bool recovering)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        if (!double.IsFinite(age)) age = 0;
        if (recovering) return new(BuddyAmbientState.Idle, Vector2.Zero, Vector2.One, 0, Array.Empty<Particle>());
        if (tactical) state = BuddyAmbientState.Focused;
        if (reduced) return new(state, Vector2.Zero, Vector2.One, 0, Array.Empty<Particle>());
        var wave = (float)Math.Sin(seconds * (state == BuddyAmbientState.Sleeping ? 1.35 : 2.05));
        var offset = new Vector2(0, wave * .008f);
        var scale = new Vector2(1 + wave * .008f, 1 - wave * .006f);
        var rotation = (float)Math.Sin(seconds * 1.1) * .012f;
        var particles = Array.Empty<Particle>();
        if (state == BuddyAmbientState.Sleeping)
        {
            scale = new(1 + wave * .014f, 1 - wave * .01f);
            particles = new Particle[2];
            for (var i = 0; i < particles.Length; i++)
            {
                var phase = (float)((seconds / 3.6 + i * .5) % 1); if (phase < 0) phase++;
                particles[i] = new(new(.15f + phase * .19f, -.25f - phase * .29f), 10 + phase * 5,
                    MathF.Sin(phase * MathF.PI) * .68f, true);
            }
        }
        else if (state == BuddyAmbientState.Welcoming && age is >= 0 and < 2.5)
        {
            var envelope = 1 - (float)age / 2.5f;
            var bounce = MathF.Abs(MathF.Sin((float)age * 7.5f)) * envelope;
            offset.Y -= bounce * .073f;
            rotation += MathF.Sin((float)age * 8) * .065f * envelope;
            particles = new Particle[3];
            for (var i = 0; i < particles.Length; i++)
            {
                var phase = (float)((age * .7 + i / 3f) % 1);
                particles[i] = new(new((i == 1 ? -.38f : .32f + i * .04f), -.1f - phase * .34f), 3 + phase * 2,
                    MathF.Sin(phase * MathF.PI) * envelope * .85f, false);
            }
        }
        else if (state == BuddyAmbientState.Focused)
        {
            offset = new(.014f, wave * .003f);
            rotation *= .25f;
            scale = new(1 + wave * .003f, 1 - wave * .004f);
        }
        return new(state, offset, scale, rotation, particles);
    }

    public static Quad Corners(Vector2 min, float size, Frame frame)
    {
        var center = min + new Vector2(size / 2) + frame.Offset * size;
        var transform = Matrix3x2.CreateScale(frame.Scale) * Matrix3x2.CreateRotation(frame.Rotation);
        var rect = SpriteRect(frame.Pose);
        Vector2 Point(float x, float y) => center + Vector2.Transform((new Vector2(x, y) - new Vector2(.5f)) * size, transform);
        return new(Point(rect.Min.X, rect.Min.Y), Point(rect.Max.X, rect.Min.Y), Point(rect.Max.X, rect.Max.Y), Point(rect.Min.X, rect.Max.Y));
    }

    public static (Vector2 Min, Vector2 Max) SpriteRect(BuddyAmbientState pose)
    {
        var bounds = Crop(pose);
        var size = (bounds.Max - bounds.Min) / 550f;
        return (new(.5f - size.X / 2, .95f - size.Y), new(.5f + size.X / 2, .95f));
    }

    public static (Vector2 Min, Vector2 Max) Uvs(BuddyAmbientState pose, bool mirrored)
    {
        var index = Index(pose);
        var cell = new Vector2(index % 2, index / 2) * CellSize;
        var crop = Crop(pose);
        var min = (cell + crop.Min) / (CellSize * 2);
        var max = (cell + crop.Max) / (CellSize * 2);
        if (mirrored) (min.X, max.X) = (max.X, min.X);
        return (min, max);
    }

    private static int Index(BuddyAmbientState pose) => (int)pose is >= 0 and < 4 ? (int)pose : 0;
    private static (Vector2 Min, Vector2 Max) Crop(BuddyAmbientState pose)
    {
        var bounds = PoseBounds[Index(pose)];
        return (bounds.Min - new Vector2(3), bounds.Max + new Vector2(3));
    }
}
