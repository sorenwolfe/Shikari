using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Buddy;
using Shikari.UI;

namespace Dalamud.Bindings.ImGui
{
    // Records only the external drawing boundary. Blink timing, landmarks and transforms
    // are all evaluated by the real production class.
    public sealed class ImDrawListPtr
    {
        public readonly List<(Vector2[] Points, uint Color)> Polygons = new();
        public readonly List<(Vector2 A, Vector2 B, uint Color, float Width)> Lines = new();
        private readonly List<Vector2> path = new();
        public void PathClear() => path.Clear();
        public void PathLineTo(Vector2 p) => path.Add(p);
        public void PathFillConvex(uint color) { Polygons.Add((path.ToArray(), color)); path.Clear(); }
        public void AddLine(Vector2 a, Vector2 b, uint color, float width) => Lines.Add((a, b, color, width));
    }
}

namespace Shikari.Tests
{
    public static class BuddyBlinkTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static float At(double seconds, BuddyAmbientState state = BuddyAmbientState.Idle,
            bool reduced = false, bool tactical = false, bool recovering = false) =>
            BuddyBlink.Sample(state, seconds, reduced, tactical, recovering);

        public static void Run()
        {
            // Removing the animation or stretching it into a sustained expression fails this scan.
            var episodes = new List<(double Start, double End, float Peak)>();
            var start = -1d; var peak = 0f;
            for (var tick = 0; tick <= 120000; tick++)
            {
                var seconds = tick / 1000d;
                var amount = At(seconds);
                Check(float.IsFinite(amount) && amount is >= 0 and <= 1, "Closure must stay finite and bounded");
                if (amount > 0)
                {
                    if (start < 0) start = seconds;
                    peak = MathF.Max(peak, amount);
                }
                else if (start >= 0) { episodes.Add((start, seconds, peak)); start = -1; peak = 0; }
            }
            Check(episodes.Count >= 16 && episodes.Count <= 32, "Idle Ember must blink naturally several times per minute");
            Check(episodes.All(e => e.End - e.Start is >= .15 and <= .22 && e.Peak == 1),
                "Each blink must briefly close both eyes completely and finish within 150–220 ms");
            var gaps = episodes.Zip(episodes.Skip(1), (a, b) => b.Start - a.Start).ToArray();
            Check(gaps.All(g => g is >= 4 and <= 7 || g is >= .25 and <= .5), "Blink spacing must remain quiet, with only brief double blinks");
            Check(gaps.Count(g => g < 1) <= episodes.Count / 3, "Double blinks must remain occasional");
            Check(gaps.Where(g => g > 1).Max() - gaps.Where(g => g > 1).Min() > .5,
                "Vary the pause between blinks so idle does not feel mechanical");

            var blinkTime = episodes[0].Start + .085;
            foreach (var state in new[] { BuddyAmbientState.Sleeping, BuddyAmbientState.Welcoming, BuddyAmbientState.Focused })
                Check(At(blinkTime, state) == 0, "The idle eye overlay must never appear on a different pose");
            Check(At(blinkTime, reduced: true) == 0 && At(blinkTime, tactical: true) == 0 && At(blinkTime, recovering: true) == 0,
                "Reduced motion, tactical cues and recovery must suppress blinking");
            foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
                Check(At(time) == 0, "Invalid clocks must safely leave the eyes open");

            var uv = BuddyMotion.Uvs(BuddyAmbientState.Idle, false);
            // This quad makes screen coordinates equal the original atlas pixel coordinates.
            var source = new BuddyMotion.Quad(new(70, 95), new(500, 95), new(500, 615), new(70, 615));
            var closed = new ImDrawListPtr();
            BuddyBlink.Draw(closed, source, uv, 1);
            Check(closed.Polygons.Count >= 4 && closed.Lines.Count >= 16, "A closed blink must submit upper/lower eyelids and an eyelash line for both eyes");
            foreach (var p in new[] { new Vector2(321, 226), new Vector2(338, 255), new Vector2(352, 262),
                new Vector2(407, 231), new Vector2(405, 249), new Vector2(416, 241), new Vector2(411, 254), new Vector2(409, 258) })
                Check(closed.Polygons.Any(polygon => Contains(polygon.Points, p)), "Closed eyelids must hide both eye glints and lower iris: " + p);
            foreach (var polygon in closed.Polygons)
            {
                Check(SignedArea(polygon.Points) > 0, "ImGui fill paths must keep clockwise winding");
                Check(polygon.Points.All(p => p.X is >= 297 and <= 419 && p.Y is >= 202 and <= 266),
                    "Lids must stay tightly within the existing eye landmarks");
            }
            foreach (var amount in new[] { .05f, .25f, .5f, .75f, 1f })
            foreach (var mirror in new[] { false, true })
            {
                var partial = new ImDrawListPtr();
                BuddyBlink.Draw(partial, source, BuddyMotion.Uvs(BuddyAmbientState.Idle, mirror), amount);
                foreach (var polygon in partial.Polygons)
                {
                    Check(SignedArea(polygon.Points) > 0, "Partial and mirrored lids must retain clockwise winding");
                    for (var i = 0; i < polygon.Points.Length; i++)
                    {
                        var a = polygon.Points[i]; var b = polygon.Points[(i + 1) % polygon.Points.Length];
                        var c = polygon.Points[(i + 2) % polygon.Points.Length];
                        var ab = b - a; var bc = c - b;
                        Check(ab.X * bc.Y - ab.Y * bc.X >= -.002f,
                            "Every animated fill must remain convex for ImGui antialiasing");
                    }
                }
            }
            foreach (var amount in new[] { 0f, float.NaN, float.PositiveInfinity, -1f })
            {
                var absent = new ImDrawListPtr(); BuddyBlink.Draw(absent, source, uv, amount);
                Check(absent.Polygons.Count == 0 && absent.Lines.Count == 0, "An open or invalid blink must submit no overlay");
            }
            // Literal affine transform catches detached lids under translation, rotation and nonuniform scale.
            Vector2 Transform(Vector2 p) => new(900 - p.Y * .5f, 40 + p.X * 2);
            var transformed = new BuddyMotion.Quad(Transform(source.A), Transform(source.B), Transform(source.C), Transform(source.D));
            var moved = new ImDrawListPtr(); BuddyBlink.Draw(moved, transformed, uv, 1);
            for (var i = 0; i < closed.Polygons.Count; i++)
            for (var j = 0; j < closed.Polygons[i].Points.Length; j++)
                Check(Vector2.Distance(moved.Polygons[i].Points[j], Transform(closed.Polygons[i].Points[j])) < .002,
                    "Every eyelid vertex must use the exact sprite quad transform");

            var mirrored = new ImDrawListPtr(); BuddyBlink.Draw(mirrored, source, BuddyMotion.Uvs(BuddyAmbientState.Idle, true), 1);
            for (var i = 0; i < closed.Polygons.Count; i++)
            {
                var originalPoints = closed.Polygons[i].Points;
                var mirrorPoints = mirrored.Polygons[i].Points;
                Check(SignedArea(mirrorPoints) > 0, "Mirroring must preserve ImGui clockwise fill winding");
                foreach (var p in originalPoints)
                    Check(mirrorPoints.Any(m => Vector2.Distance(m, new(570 - p.X, p.Y)) < .002),
                        "Mirrored eyes must follow the flipped source image without swapping crop margins");
            }
            Console.WriteLine("Buddy blink: natural timing, full eye coverage, state suppression, bounded landmarks and quad/mirror transforms passed.");
        }

        private static float SignedArea(Vector2[] points)
        {
            var twice = 0f;
            for (var i = 0; i < points.Length; i++) { var a = points[i]; var b = points[(i + 1) % points.Length]; twice += a.X * b.Y - b.X * a.Y; }
            return twice * .5f;
        }
        private static bool Contains(Vector2[] polygon, Vector2 point)
        {
            var inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i]; var b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
    }
}
