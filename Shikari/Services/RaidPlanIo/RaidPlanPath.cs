using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Shikari.Model;

namespace Shikari.Services.RaidPlanIo;

/// <summary>Rebuilds raidplan's packed PencilBrush points as an editable sampled stroke.</summary>
internal static class RaidPlanPath
{
    public static CanvasItem? Translate(JObject source, PlanFrame frame)
    {
        var attr = source["attr"] as JObject;
        var meta = source["meta"] as JObject;
        // The site's brush stores at most 512 points. Leave room for other exports, but bound
        // the work and reject malformed paths rather than manufacturing a line through them.
        if (attr?["points"] is not JArray packed || packed.Count < 4 || packed.Count > 8192 || packed.Count % 2 != 0)
            return null;
        var points = new List<Vector2>();
        foreach (var value in packed)
            if (value.Type is not (JTokenType.Integer or JTokenType.Float) || !float.IsFinite(value.Value<float>())) return null;
        for (var i = 0; i < packed.Count; i += 2)
            points.Add(new Vector2(packed[i].Value<float>(), packed[i + 1].Value<float>()) / 10f);

        var width = attr.Value<float?>("strokeWidth") ?? 4f;
        if (!float.IsFinite(width) || width < 0) return null;
        var epsilon = width / 1000f;
        var direction = points.Count > 2 ? Direction(points[2] - points[1]) : Vector2.UnitX;
        var start = points[0] - direction * epsilon;
        var sampled = new List<Vector2> { start };
        var min = start;
        var max = start;
        for (var i = 0; i < points.Count - 1; i++)
        {
            var control = points[i];
            if (control == points[i + 1]) continue;
            var end = (control + points[i + 1]) * 0.5f;
            // Bound the actual quadratic, independent of how many samples we render.
            Include(end, ref min, ref max);
            for (var axis = 0; axis < 2; axis++)
            {
                var a = axis == 0 ? start.X : start.Y;
                var b = axis == 0 ? control.X : control.Y;
                var c = axis == 0 ? end.X : end.Y;
                var denominator = a - 2 * b + c;
                var t = denominator == 0 ? -1 : (a - b) / denominator;
                if (t > 0 && t < 1) Include(Quadratic(start, control, end, t), ref min, ref max);
            }
            for (var sample = 1; sample <= 8; sample++)
                sampled.Add(Quadratic(start, control, end, sample / 8f));
            start = end;
        }
        direction = points.Count > 2 ? Direction(points[^1] - points[^2]) : Vector2.UnitX;
        var last = points[^1] + direction * epsilon;
        sampled.Add(last);
        Include(last, ref min, ref max);
        var center = (min + max) * 0.5f;
        var scale = new Vector2(meta?["scale"]?.Value<float?>("x") ?? 1f, meta?["scale"]?.Value<float?>("y") ?? 1f);
        var angle = meta?.Value<float?>("angle") ?? 0f;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(angle)) return null;
        var anchor = new Vector2(meta?["pos"]?.Value<float?>("x") ?? 0f, meta?["pos"]?.Value<float?>("y") ?? 0f);
        var origin = new Vector2(Origin(meta?["origin"]?["x"], "left", "right"), Origin(meta?["origin"]?["y"], "top", "bottom"));
        // Fabric's left/top origin includes the stroke's bounding box. Flip changes the
        // drawing within that box, while the anchor-to-center offset remains unchanged.
        var dimensions = (max - min + new Vector2(width)) * Vector2.Abs(scale);
        var position = anchor + Rotate(origin * dimensions, angle);
        if (meta?["flip"]?["x"]?.ToString() is "True" or "true" or "1") scale.X *= -1;
        if (meta?["flip"]?["y"]?.ToString() is "True" or "true" or "1") scale.Y *= -1;
        var item = new CanvasItem
        {
            Kind = CanvasItemKind.Freehand,
            Thickness = frame.Length(width * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y))),
            Color = RaidPlanIoImporter.ParseColour(attr.Value<string>("stroke"), 0xFFFFFFFF),
        };
        var opacity = attr.Value<float?>("opacity") ?? 1f;
        if (float.IsFinite(opacity)) item.Color = (item.Color & 0xFFFFFF) | ((uint)MathF.Round((item.Color >> 24) * Math.Clamp(opacity, 0f, 1f)) << 24);
        foreach (var point in sampled)
        {
            var transformed = position + Rotate((point - center) * scale, angle);
            item.Points.Add(frame.Normalise(transformed.X, transformed.Y));
        }
        return item;
    }

    private static Vector2 Quadratic(Vector2 a, Vector2 b, Vector2 c, float t) => a * ((1 - t) * (1 - t)) + b * (2 * t * (1 - t)) + c * (t * t);
    private static void Include(Vector2 point, ref Vector2 min, ref Vector2 max) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
    private static Vector2 Direction(Vector2 delta) => new(MathF.Sign(delta.X), MathF.Sign(delta.Y));
    private static float Origin(JToken? value, string low, string high) => value?.Type is JTokenType.Float or JTokenType.Integer ? 0.5f - value.Value<float>() : value?.Value<string>() == low ? 0.5f : value?.Value<string>() == high ? -0.5f : 0f;
    private static Vector2 Rotate(Vector2 point, float angle)
    {
        var radians = angle * (MathF.PI / 180f);
        return new Vector2(point.X * MathF.Cos(radians) - point.Y * MathF.Sin(radians), point.X * MathF.Sin(radians) + point.Y * MathF.Cos(radians));
    }
}
