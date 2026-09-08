using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Symbols;

namespace Shikari.UI;

/// <summary>Text and emoji use the same advances for drawing, centering and caption placement.</summary>
public static class CanvasText
{
    private readonly record struct Run(string Text, string? Asset);

    public static Vector2 Measure(string text)
    {
        if (text.All(c => c < 128)) return ImGui.CalcTextSize(text, false, -1);
        var font = ImGui.GetFontSize();
        var width = 0f;
        var height = 0f;
        foreach (var line in text.Split('\n'))
        {
            var advance = 0f;
            foreach (var run in Runs(line)) advance += run.Asset != null ? font : ImGui.CalcTextSize(run.Text, false, -1).X;
            width = MathF.Max(width, advance); height += font;
        }
        return new(width, height);
    }

    public static void Draw(ImDrawListPtr draw, Vector2 position, string text, uint color, float scale = 1, bool drawEmoji = true)
    {
        var fontSize = ImGui.GetFontSize() * scale;
        var y = position.Y;
        foreach (var line in text.Split('\n'))
        {
            var x = position.X;
            foreach (var run in Runs(line))
            {
                if (run.Asset != null)
                {
                    if (!drawEmoji) { x += fontSize; continue; }
                    if (EmojiArtwork.TryHandle(run.Asset, out var handle))
                        draw.AddImage(handle, new Vector2(x, y), new Vector2(x + fontSize, y + fontSize), Vector2.Zero, Vector2.One, (color & 0xFF000000) | 0xFFFFFF);
                    else
                        draw.AddText(ImGui.GetFont(), fontSize, new Vector2(x, y), color, "?", 0);
                    x += fontSize;
                }
                else
                {
                    draw.AddText(ImGui.GetFont(), fontSize, new Vector2(x, y), color, run.Text, 0);
                    x += ImGui.CalcTextSize(run.Text, false, -1).X * scale;
                }
            }
            y += fontSize;
        }
    }

    private static IEnumerable<Run> Runs(string text)
    {
        if (text.All(c => c < 128)) { if (text.Length > 0) yield return new(text, null); yield break; }
        var plain = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (EmojiCatalog.TryResolve(element, out var key))
            {
                if (plain.Length > 0) { yield return new(plain.ToString(), null); plain.Clear(); }
                yield return new(element, key);
            }
            else
            {
                // This ImGui ABI has 16-bit glyph indices. Preserve unsupported cluster data
                // in the document, but never draw broken surrogate halves as separate labels.
                var unsupported = element.EnumerateRunes().Any(r => r.Value > 0xFFFF) || element.Contains('\u200D') || element.Contains('\u20E3');
                plain.Append(unsupported ? "[symbol]" : element);
            }
        }
        if (plain.Length > 0) yield return new(plain.ToString(), null);
    }
}
