using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Shikari.Model;

namespace Shikari.Services.RaidPlanIo;

/// <summary>What an import produced, so the result can be reported rather than guessed at.</summary>
public sealed class RaidPlanIoReport
{
    public int Slides { get; set; }

    public int Items { get; set; }

    public int TimelineSteps { get; set; }

    public int SeatsBound { get; set; }

    /// <summary>Text boxes that became slide notes rather than objects.</summary>
    public int NotesMoved { get; set; }

    /// <summary>Seats that took a role, and sometimes a job, from the plan's own token artwork.</summary>
    public int RolesRecognised { get; set; }

    /// <summary>How many of those named an actual job rather than just a role.</summary>
    public int JobsRecognised { get; set; }

    public Dictionary<string, int> ByType { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Notes { get; } = new();

    internal Dictionary<string, int> Unsupported { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Summary()
    {
        var text = $"{Slides} slide(s), {Items} object(s), {TimelineSteps} timeline step(s).";

        if (RolesRecognised > 0)
        {
            text += $" {RolesRecognised} seat(s) got their role";
            text += JobsRecognised > 0 ? $", {JobsRecognised} of them a job." : ".";
        }

        if (NotesMoved > 0)
            text += $" {NotesMoved} text box(es) became slide notes.";

        var dropped = Skipped.Where(p => p.Key.ToLowerInvariant() is not ("arena" or "itext" or "emoji"))
            .Sum(p => p.Value);
        if (dropped > 0)
            text += $" {dropped} object(s) had no equivalent and were left out.";

        return text;
    }
}

/// <summary>
/// Reads a plan exported from raidplan.io and rebuilds it as one of ours.
/// </summary>
/// <remarks>
/// Their model is a flat list of nodes, each stamped with the step it belongs to; ours is a list
/// of slides that own their contents. Everything else is a shape-by-shape translation, with the
/// coordinates refitted by <see cref="PlanFrame"/> because the source canvas size is not in the
/// file.
/// </remarks>
public static class RaidPlanIoImporter
{
    /// <summary>
    /// Sweep used for the cone-shaped area sprites. Their geometry lives in artwork we cannot
    /// read, so these are sensible defaults meant to be nudged on the slider afterwards.
    /// </summary>
    public const float PieSweepDegrees = 90f;
    public const float WedgeSweepDegrees = 45f;

    /// <summary>Margin left around the plan's contents, as a fraction of its own size.</summary>
    public const float Padding = 0.04f;

    /// <summary>Where our own arena outline is drawn, as a fraction of the square.</summary>
    public const float ArenaEdge = 0.47f;

    /// <summary>
    /// How much of the source canvas's half-height the platform itself actually covers.
    /// </summary>
    /// <remarks>
    /// Their arena art is a square the height of the canvas with the platform drawn inside a
    /// margin, so the platform is meaningfully smaller than the canvas. Measured off a rendered
    /// plan: a platform of radius 297px on a canvas 714px tall, which is 0.83. Treating the
    /// canvas half-height as the radius — which is what this used to do — makes the frame a fifth
    /// too big and everything on it a fifth too small.
    /// </remarks>
    public const float ArenaFraction = 0.83f;

    /// <summary>
    /// How far the frame may be widened past the arena to keep stray objects on the board.
    /// </summary>
    /// <remarks>
    /// Deliberately small. Widening is what was shrinking these plans: one wide mechanic drawn
    /// off the platform — and most fights have one — pushed the frame out to its old 1.6 limit
    /// and took the whole plan down with it. Objects outside the arena are still drawn, just
    /// outside the outline, which is where the original plan had them; an arena too small to read
    /// is the worse outcome. What little widening is left is measured against the bulk of the
    /// plan rather than its furthest object, so one stray shape cannot trigger it at all.
    /// </remarks>
    public const float MaxWidening = 1.12f;

    /// <summary>
    /// Share of the plan's objects the frame is sized to hold. The rest may hang over the edge.
    /// </summary>
    public const float ReachPercentile = 0.98f;

    /// <summary>Types that describe the board rather than sit on it, so they never size the frame.</summary>
    private static readonly HashSet<string> NotOnTheBoard =
        new(StringComparer.OrdinalIgnoreCase) { "arena", "itext", "emoji" };

    public static bool TryImport(string? json, out PlanDocument? document, out RaidPlanIoReport report, out string error)
    {
        document = null;
        report = new RaidPlanIoReport();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Nothing to import.";
            return false;
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            error = "That is not readable JSON: " + ex.Message;
            return false;
        }

        if (root["nodes"] is not JArray nodes)
        {
            error = "No 'nodes' in that file. Is it the plan JSON rather than the page?";
            return false;
        }

        var parsed = nodes.OfType<JObject>().Select(Node.From).Where(n => n != null).Select(n => n!).ToList();
        if (parsed.Count == 0)
        {
            error = "That plan has no objects in it.";
            return false;
        }

        // Notes are pinned in the margins beside the arena, sometimes a long way out. Sizing the
        // frame to include them shrinks the arena itself into the middle of the square, which is
        // exactly what it looked like before this was excluded.
        var onBoard = parsed
            .Where(n => n.HasPosition && !NotOnTheBoard.Contains(n.Type))
            .Select(n => n.Position)
            .ToList();

        var frame = BuildFrame(parsed, onBoard);

        var doc = PlanDocument.CreateDefault("Imported plan");
        doc.Slides.Clear();

        var steps = parsed.Select(n => n.Step).Distinct().OrderBy(s => s).ToList();
        var slideByStep = new Dictionary<int, Slide>();

        foreach (var step in steps)
        {
            var slide = new Slide { Title = $"Step {step + 1}", SourceStep = step };
            doc.Slides.Add(slide);
            slideByStep[step] = slide;
        }

        var seatLookup = BuildSeatLookup(doc);
        var bound = new HashSet<int>();

        foreach (var node in parsed)
        {
            if (!slideByStep.TryGetValue(node.Step, out var slide))
                continue;

            var marker = AbilityMarker(node, frame, report);
            if (marker != null)
            {
                slide.Items.AddRange(marker);
                Bump(report.ByType, node.Type);
                report.Items += marker.Count;
                continue;
            }

            var item = Translate(node, frame, doc, seatLookup, bound, report);
            if (item == null)
            {
                Bump(report.Skipped, node.Type);
                continue;
            }

            slide.Items.Add(item);
            Bump(report.ByType, node.Type);
            report.Items++;
        }

        ApplySlideNotes(parsed, slideByStep, report);

        foreach (var pair in report.Unsupported)
            report.Notes.Add($"Unsupported {pair.Key}: {pair.Value} object(s) left out.");

        report.Slides = doc.Slides.Count;
        report.SeatsBound = bound.Count;
        report.RolesRecognised = doc.Roster.Count(seat => seat.Role != RaidRole.Unknown);
        report.JobsRecognised = doc.Roster.Count(seat => seat.JobId != 0);

        ApplyNotes(root, doc, report);

        if (parsed.Any(n => n.Type == "arena" && !string.IsNullOrEmpty(n.ArenaImageUrl)))
        {
            var url = parsed.First(n => n.Type == "arena" && !string.IsNullOrEmpty(n.ArenaImageUrl)).ArenaImageUrl;
            doc.Arena.Shape = ArenaShape.None;
            report.Notes.Add(
                "This plan uses a custom arena picture. Save it and add it as a tracing picture: " + url);
        }

        document = PlanNormaliser.Normalise(doc);
        return true;
    }

    /// <summary>
    /// Works out how the source coordinates map onto our arena.
    /// </summary>
    /// <remarks>
    /// Their canvas is centred on the arena and the arena is as tall as the canvas, so the
    /// distance from the waymark ring's centre up to the top of the canvas is the arena's radius.
    /// That gives a scale that depends on the arena rather than on how far the drawing happens to
    /// spread, which is what stops a busy plan importing smaller than a quiet one.
    /// </remarks>
    private static PlanFrame BuildFrame(IReadOnlyList<Node> parsed, IReadOnlyList<Vector2> onBoard)
    {
        if (!TryWaymarkCentre(parsed, out var centre) || centre.Y <= 1f)
            return PlanFrame.Fit(onBoard, Padding);

        var frame = PlanFrame.FromArena(centre, centre.Y * ArenaFraction, ArenaEdge);

        // Widen only if the bulk of the plan would otherwise fall off the board, and only so far.
        var reach = frame.Reach(onBoard, ReachPercentile);
        if (reach <= 0.5f)
            return frame;

        return frame.Widened(MathF.Min(reach / 0.5f * (1f + Padding), MaxWidening));
    }

    /// <summary>
    /// The middle of the waymark ring, which is the one landmark whose position means something.
    /// The eight marks sit symmetrically about the arena centre, so their average is that centre.
    /// </summary>
    public static bool TryWaymarkCentre(IReadOnlyList<Node> nodes, out Vector2 centre)
    {
        centre = Vector2.Zero;

        var marks = nodes
            .Where(n => n.Type == "waypoint" && n.HasPosition)
            .GroupBy(n => (n.WayId ?? string.Empty).ToLowerInvariant())
            .Select(g => g.First().Position)
            .ToList();

        // Fewer than the full set and the average is pulled off centre by whichever are missing.
        if (marks.Count < 8)
            return false;

        foreach (var mark in marks)
            centre += mark;

        centre /= marks.Count;
        return true;
    }

    // ---------------------------------------------------------------- translation

    private static List<CanvasItem>? AbilityMarker(Node node, PlanFrame frame, RaidPlanIoReport report)
    {
        if (node.Type != "ability") return null;
        var id = node.AbilityId?.ToLowerInvariant();
        if (id is not ("ff-area-prox" or "ff-knock")) return null;
        var items = new List<CanvasItem>();
        string note;
        if (id == "ff-area-prox")
        {
            // Source SVG: an r50/r37.5 gradient ring and an r8 dot in a 100px viewbox.
            // The editor has solid zones, so retain the boundary and dot without claiming
            // that the transparent part of the source proximity marker is a safe zone.
            var ring = Zone(node, frame, ZoneShape.Donut);
            ring.InnerRadius = ring.Radius * 0.75f;
            ring.Color = node.OpacityColour("colorB", node.Colour("colorA", 0x80FFFFFF));
            items.Add(ring);
            var dot = Zone(node, frame, ZoneShape.Circle);
            dot.Radius = frame.Length(node.Width * 0.08f);
            items.Add(dot);
            note = "Proximity markers (Prox) simplified to an editable boundary ring and center dot; the source gradient is omitted. The ring's interior does not indicate safety.";
        }
        else
        {
            // Source SVG: eight pairs of outward chevrons. Keep the outer tips at r45
            // and shorten each arrow into the original chevron band rather than implying
            // a measured travel path starting at the origin.
            for (var direction = 0; direction < 8; direction++)
            {
                var radians = direction * MathF.PI / 4f;
                var vector = new Vector2(MathF.Sin(radians) * node.Width, -MathF.Cos(radians) * node.Height);
                var tail = node.Position + Rotate(vector * 0.28f, node.Angle);
                var head = node.Position + Rotate(vector * 0.45f, node.Angle);
                var arrow = Base(node, frame, CanvasItemKind.Arrow);
                arrow.Color = node.AreaColour();
                arrow.Points = new List<Vector2> { frame.Normalise(tail.X, tail.Y), frame.Normalise(head.X, head.Y) };
                items.Add(arrow);
            }
            note = "Knockback markers (KB) simplified to eight editable outward arrows; paired chevrons and the second color are omitted. Arrow length is marker artwork, not a measured knockback distance.";
        }
        var label = Base(node, frame, CanvasItemKind.Label);
        label.Text = id == "ff-area-prox" ? "Prox" : "KB";
        if (id == "ff-area-prox")
        {
            var position = node.Position + new Vector2(0, node.Height * 0.18f);
            label.Position = frame.Normalise(position.X, position.Y);
        }
        items.Add(label);
        if (!report.Notes.Contains(note)) report.Notes.Add(note);
        return items;
    }

    private static CanvasItem? Translate(
        Node node, PlanFrame frame, PlanDocument doc, Dictionary<string, int> seats,
        HashSet<int> bound, RaidPlanIoReport report)
    {
        switch (node.Type)
        {
            case "arena":
                return null;

            case "marker":
                return Marker(node, frame, doc, seats, bound);

            case "waypoint":
                return Waymark(node, frame);

            // Handled as notes, not as things on the board.
            case "itext":
            case "emoji":
                return null;

            case "circle":
                return Zone(node, frame, ZoneShape.Circle);

            case "rect":
                return Zone(node, frame, ZoneShape.Rectangle);

            case "triangle":
                return Cone(node, frame, TriangleSweep(node));

            case "arrow":
                return Arrow(node, frame);

            case "path":
                var path = RaidPlanPath.Translate(node.Source, frame);
                if (path == null) Bump(report.Unsupported, "path geometry");
                return path;

            case "ability":
                return Ability(node, frame, report);

            default:
                Bump(report.Unsupported, "object type '" + node.Type + "'");
                return null;
        }
    }

    private static CanvasItem Marker(
        Node node, PlanFrame frame, PlanDocument doc, Dictionary<string, int> seats, HashSet<int> bound)
    {
        var item = Base(node, frame, CanvasItemKind.PlayerToken);
        item.Text = node.Text ?? string.Empty;
        item.Radius = frame.Length(node.Width * 0.5f);
        item.Color = node.Colour("bgColor", 0);

        // The artwork says which role, and often which job, is standing there.
        var who = JobAssets.Read(node.Asset);

        // Their label is the seat name, and ours are named the same way out of the box, so a
        // token can land on the seat it was drawn for rather than floating unbound.
        var key = (node.Text ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length > 0 && seats.TryGetValue(key, out var index))
        {
            item.SlotIndex = index;
            item.Text = string.Empty;
            bound.Add(index);

            // The plan knows what everyone was playing; the blank roster does not.
            var seat = doc.Roster[index];
            if (who.KnowsJob)
                seat.JobId = who.JobId;
            if (who.Role != RaidRole.Unknown)
            {
                seat.Role = who.Role;
                seat.Color = RoleColors.Default(who.Role);
            }
        }
        else if (who.Role != RaidRole.Unknown && item.Color == 0)
        {
            // Unbound, so it keeps its caption — but it can still be the right colour.
            item.Color = RoleColors.Default(who.Role);
        }

        return item;
    }

    private static CanvasItem Waymark(Node node, PlanFrame frame)
    {
        var item = Base(node, frame, CanvasItemKind.Waymark);
        item.Text = (node.WayId ?? "A").ToUpperInvariant();
        item.Radius = frame.Length(node.Width * 0.5f);
        return item;
    }

    private static CanvasItem Zone(Node node, PlanFrame frame, ZoneShape shape)
    {
        var item = Base(node, frame, CanvasItemKind.Zone);
        item.Zone = shape;
        item.Color = node.AreaColour();
        item.Rotation = node.Angle;
        item.Radius = frame.Length(node.Width * 0.5f);
        item.Extent = new Vector2(frame.Length(node.Width * 0.5f), frame.Length(node.Height * 0.5f));
        return item;
    }

    private static CanvasItem Cone(Node node, PlanFrame frame, float sweep)
    {
        // Their triangle is drawn around its centre with the apex at the top; our cone opens out
        // from its origin. So the origin moves to where the apex is, and the cone faces the
        // opposite way to the triangle's own rotation.
        var item = Base(node, frame, CanvasItemKind.Zone);
        item.Zone = ZoneShape.Cone;
        item.Color = node.AreaColour();
        item.ConeAngle = sweep;
        item.Radius = frame.Length(node.Height);

        var apex = node.Position + Rotate(new Vector2(0f, -node.Height * 0.5f), node.Angle);
        item.Position = frame.Normalise(apex.X, apex.Y);
        item.Rotation = (node.Angle + 180f) % 360f;

        return item;
    }

    private static CanvasItem Arrow(Node node, PlanFrame frame)
    {
        // One rotated sprite on their side, a two-point path on ours.
        var half = Rotate(new Vector2(0f, -node.Height * 0.5f), node.Angle);
        var tail = node.Position - half;
        var head = node.Position + half;

        var item = Base(node, frame, CanvasItemKind.Arrow);
        item.Color = node.Colour("fill", 0xFFFFFFFF);
        item.Points = new List<Vector2>
        {
            frame.Normalise(tail.X, tail.Y),
            frame.Normalise(head.X, head.Y),
        };

        return item;
    }

    private static CanvasItem? Ability(Node node, PlanFrame frame, RaidPlanIoReport report)
    {
        switch ((node.AbilityId ?? string.Empty).ToLowerInvariant())
        {
            case "ff-boss":
            {
                var item = Base(node, frame, CanvasItemKind.EnemyToken);
                item.Radius = frame.Length(node.Width * 0.5f);
                item.Color = node.Colour("colorA", 0xFF4444EE);
                item.Rotation = node.Angle;
                return item;
            }

            // Source 100x100 SVGs: ff-ring has outer radius 50 and inner 47.5;
            // ff-donut has outer 50 and inner 25. See tests/raidplan-source-notes.md.
            case "ff-ring":
            case "ff-donut":
            {
                var item = Zone(node, frame, ZoneShape.Donut);
                item.InnerRadius = item.Radius * (node.AbilityId?.Equals("ff-ring", StringComparison.OrdinalIgnoreCase) == true ? 0.95f : 0.5f);
                return item;
            }

            case "ff-square":
                return Zone(node, frame, ZoneShape.Rectangle);

            // A stack marker is a circle you gather in. The zone shape carries that; the caption
            // does not, because zones never draw one.
            case "ff-stack":
            case "ff-circle":
                return Zone(node, frame, ZoneShape.Circle);

            case "ff-pie":
                return Cone(node, frame, PieSweepDegrees);

            case "ff-wedge":
                return Cone(node, frame, WedgeSweepDegrees);

            case "ff-push":
                return Arrow(node, frame);

            default:
                Bump(report.Unsupported, "area type '" + node.AbilityId + "'");
                return null;
        }
    }

    private static CanvasItem Base(Node node, PlanFrame frame, CanvasItemKind kind) => new()
    {
        Kind = kind,
        Position = frame.Normalise(node.Position.X, node.Position.Y),
        Rotation = node.Angle,
        Color = 0xFFFFFFFF,
    };

    /// <summary>Sweep of a triangle, from how wide its base is against how long it is.</summary>
    public static float TriangleSweep(Node node) => TriangleSweep(node.Width, node.Height);

    public static float TriangleSweep(float width, float height)
    {
        if (height <= 0f)
            return 60f;

        var half = MathF.Atan2(width * 0.5f, height) * (180f / MathF.PI);
        return Math.Clamp(half * 2f, 5f, 355f);
    }

    private static Vector2 Rotate(Vector2 point, float degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(r);
        var sin = MathF.Sin(r);

        return new Vector2((point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos));
    }

    /// <summary>
    /// Text pinned beside the arena is the strategy written out, so it becomes the slide's notes
    /// rather than labels floating on the board.
    /// </summary>
    private static void ApplySlideNotes(
        IReadOnlyList<Node> nodes, Dictionary<int, Slide> slideByStep, RaidPlanIoReport report)
    {
        foreach (var group in nodes.Where(n => n.Type is "itext" or "emoji").GroupBy(n => n.Step))
        {
            if (!slideByStep.TryGetValue(group.Key, out var slide))
                continue;

            // Down the board then across, so the notes come out in the order they were read.
            var lines = group
                .OrderBy(n => n.Position.Y)
                .ThenBy(n => n.Position.X)
                .Select(n => (n.Type == "emoji" ? n.Emoji : n.Text) ?? string.Empty)
                .Select(t => t.Replace("\r\n", "\n").Trim())
                .Where(t => t.Length > 0)
                .ToList();

            if (lines.Count == 0)
                continue;

            var text = string.Join("\n", lines);
            slide.Notes = slide.Notes.Length == 0 ? text : slide.Notes + "\n" + text;
            report.NotesMoved += lines.Count;
        }
    }

    // ---------------------------------------------------------------- notes

    private static void ApplyNotes(JObject root, PlanDocument doc, RaidPlanIoReport report)
    {
        var notes = root.Value<string>("header_notes_raw") ?? string.Empty;
        if (notes.Length == 0)
            return;

        doc.Notes = notes;

        foreach (var line in NoteTimeline.Parse(notes))
        {
            var entry = new TimelineEntry
            {
                Label = line.Label,
                Trigger = TriggerKind.CombatTime,
                TimeSeconds = line.Seconds,
            };

            // The note says which slide the mechanic is drawn on, so the step can carry the
            // player there instead of just naming it.
            if (line.HasSlide && line.FirstSlide <= doc.Slides.Count)
                entry.SlideId = doc.Slides[line.FirstSlide - 1].Id;

            doc.Timeline.Add(entry);
            report.TimelineSteps++;
        }

        // A slide named by the timeline is better titled than "Step 7".
        foreach (var line in NoteTimeline.Parse(notes).Where(l => l.HasSlide))
        {
            for (var slide = line.FirstSlide; slide <= line.LastSlide && slide <= doc.Slides.Count; slide++)
            {
                if (slide < 1)
                    continue;

                var target = doc.Slides[slide - 1];
                if (target.Title.StartsWith("Step ", StringComparison.Ordinal))
                {
                    target.Title = line.FirstSlide == line.LastSlide
                        ? line.Label
                        : $"{line.Label} ({slide - line.FirstSlide + 1}/{line.LastSlide - line.FirstSlide + 1})";
                }
            }
        }
    }

    private static Dictionary<string, int> BuildSeatLookup(PlanDocument doc)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < doc.Roster.Count; i++)
        {
            var key = doc.Roster[i].Placeholder;
            if (!string.IsNullOrWhiteSpace(key))
                map[key.Trim().ToUpperInvariant()] = i;
        }

        return map;
    }

    private static void Bump(Dictionary<string, int> counter, string key) =>
        counter[key] = counter.GetValueOrDefault(key) + 1;

    // ---------------------------------------------------------------- source node

    /// <summary>One object out of the source file, with the bits we care about pulled forward.</summary>
    public sealed class Node
    {
        public string Type { get; init; } = string.Empty;

        public int Step { get; init; }

        public Vector2 Position { get; init; }

        public bool HasPosition { get; init; }

        /// <summary>Size with the node's scale already applied.</summary>
        public float Width { get; init; }

        public float Height { get; init; }

        public float Angle { get; init; }

        public string? Text { get; init; }

        public string? WayId { get; init; }

        public string? AbilityId { get; init; }

        public string? Emoji { get; init; }

        public string? ArenaImageUrl { get; init; }

        /// <summary>Image path on a marker, which names the role or the job.</summary>
        public string? Asset { get; init; }

        private JObject Attr { get; init; } = new();

        internal JObject Source { get; init; } = new();

        public uint Colour(string key, uint fallback) => ParseColour(Attr.Value<string>(key), fallback);

        public uint AreaColour() => OpacityColour(Type == "ability" ? "colorA" : "fill", 0x80FFFFFF);

        public uint OpacityColour(string key, uint fallback)
        {
            var color = Colour(key, fallback);
            var opacity = Attr.Value<float?>("opacity") ?? 1f;
            var alpha = (uint)MathF.Round((color >> 24) * (float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : 1f));
            return (color & 0x00FFFFFF) | (alpha << 24);
        }

        public static Node? From(JObject node)
        {
            var type = node.Value<string>("type");
            if (string.IsNullOrEmpty(type))
                return null;

            var attr = node["attr"] as JObject ?? new JObject();
            var meta = node["meta"] as JObject ?? new JObject();

            var pos = meta["pos"] as JObject;
            var size = meta["size"] as JObject;
            var scale = meta["scale"] as JObject;

            var scaleX = scale?.Value<float?>("x") ?? 1f;
            var scaleY = scale?.Value<float?>("y") ?? 1f;
            var width = (size?.Value<float?>("w") ?? (type == "ability" ? 100f : 0f)) * MathF.Abs(scaleX);
            var height = (size?.Value<float?>("h") ?? (type == "ability" ? 100f : 0f)) * MathF.Abs(scaleY);
            var angle = meta.Value<float?>("angle") ?? 0f;
            var position = new Vector2(pos?.Value<float?>("x") ?? 0f, pos?.Value<float?>("y") ?? 0f);
            var origin = meta["origin"] as JObject;
            // Fabric saves the anchor, not necessarily the center. Its screen rotation is
            // clockwise, just like our rectangles; rotate the anchor-to-center offset too.
            var offset = new Vector2(OriginOffset(origin?["x"], "left", "right") * width,
                OriginOffset(origin?["y"], "top", "bottom") * height);
            position += Rotate(offset, angle);

            return new Node
            {
                Type = type,
                Step = meta.Value<int?>("step") ?? 0,
                HasPosition = pos != null,
                Position = position,
                Width = width,
                Height = height,
                Angle = angle,
                Text = attr.Value<string>("text"),
                WayId = attr.Value<string>("wayId"),
                AbilityId = attr.Value<string>("abilityId"),
                Emoji = attr.Value<string>("emoji"),
                ArenaImageUrl = attr.Value<string>("imageUrl"),
                Asset = attr.Value<string>("asset"),
                Attr = attr,
                Source = node,
            };
        }

        private static float OriginOffset(JToken? origin, string start, string end) => origin?.Type is JTokenType.Float or JTokenType.Integer
            ? 0.5f - origin.Value<float>()
            : origin?.Value<string>() == start ? 0.5f : origin?.Value<string>() == end ? -0.5f : 0f;
    }

    /// <summary>
    /// Turns a CSS colour into the packed ABGR ImGui wants. Accepts #RGB, #RRGGBB and #RRGGBBAA.
    /// </summary>
    public static uint ParseColour(string? css, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(css))
            return fallback;

        var text = css.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return 0u;

        if (text.StartsWith('#'))
            text = text[1..];

        if (text.Length == 3)
            text = string.Concat(text.Select(c => new string(c, 2)));

        if (text.Length is not (6 or 8))
            return fallback;

        if (!uint.TryParse(text[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return fallback;

        uint alpha = 0xFF;
        if (text.Length == 8 &&
            uint.TryParse(text[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedAlpha))
        {
            alpha = parsedAlpha;
        }

        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;

        return (alpha << 24) | (b << 16) | (g << 8) | r;
    }
}
