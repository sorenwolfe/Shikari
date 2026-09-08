using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Shikari.Model;

/// <summary>Bounded persisted symbol payloads, shared by import, disk and replay readers.</summary>
public static class SymbolValidation
{
    public const int MaximumEmojiLength = 64;
    public const int MaximumCaptionLength = 256;
    public const uint MaximumIconId = 999999;
    public const float MaximumExtent = 16;
    public const float MaximumCoordinate = 16;

    public static bool IsValid(CanvasItem item)
    {
        if (item.Kind != CanvasItemKind.Symbol) return true;
        return item.Emoji != null && item.Text != null && item.Text.Length <= MaximumCaptionLength &&
            item.IconId <= MaximumIconId && item.SlotIndex == -1 && Enum.IsDefined(item.SymbolAsset) &&
            (item.Emoji.Length == 0 || IsSingleGrapheme(item.Emoji) && item.IconId == 0 && item.SymbolAsset == SymbolAsset.None) &&
            (item.SymbolAsset == SymbolAsset.None || item.IconId == 0) &&
            (item.Emoji.Length > 0 || item.IconId > 0 || item.SymbolAsset != SymbolAsset.None || !string.IsNullOrWhiteSpace(item.Text)) &&
            Finite(item.Position) && MathF.Abs(item.Position.X) <= MaximumCoordinate && MathF.Abs(item.Position.Y) <= MaximumCoordinate &&
            float.IsFinite(item.Rotation) && Finite(item.Extent) &&
            item.Extent.X > 0 && item.Extent.Y > 0 && item.Extent.X <= MaximumExtent && item.Extent.Y <= MaximumExtent;
    }

    /// <summary>Unknown graphemes are retained; only malformed/oversized payloads become captions.</summary>
    public static void Normalise(CanvasItem item)
    {
        if (item.Kind != CanvasItemKind.Symbol) return;
        item.SlotIndex = -1;
        item.Text = Caption(item.Text);
        item.Emoji ??= "";
        if (item.Emoji.Length > 0 && !IsSingleGrapheme(item.Emoji))
        {
            if (item.Text.Length == 0) item.Text = Caption(item.Emoji);
            item.Emoji = "";
        }
        if (!Enum.IsDefined(item.SymbolAsset)) item.SymbolAsset = SymbolAsset.None;
        if (item.Emoji.Length > 0) { item.IconId = 0; item.SymbolAsset = SymbolAsset.None; }
        if (item.IconId > MaximumIconId) item.IconId = 0;
        if (item.IconId > 0) item.SymbolAsset = SymbolAsset.None;
        if (item.Emoji.Length == 0 && item.IconId == 0 && item.SymbolAsset == SymbolAsset.None && string.IsNullOrWhiteSpace(item.Text)) item.Text = "Unknown symbol";
        item.Position = new Vector2(Coordinate(item.Position.X), Coordinate(item.Position.Y));
        if (!float.IsFinite(item.Rotation)) item.Rotation = 0;
        item.Extent = new Vector2(Extent(item.Extent.X), Extent(item.Extent.Y));
    }

    public static bool IsSingleGrapheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEmojiLength) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (char.IsControl(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return StringInfo.ParseCombiningCharacters(value).Length == 1;
    }

    public static string Caption(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var buffer = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // Emoji ZWJ and subdivision-flag tags are meaningful parts of a grapheme.
            // Other formatting characters (including bidi controls) are not display labels.
            var emojiJoiner = rune.Value == 0x200D || rune.Value is >= 0xE0020 and <= 0xE007F;
            if (category == UnicodeCategory.Control || category == UnicodeCategory.Format && !emojiJoiner)
            {
                if (buffer.Length > 0 && buffer[^1] != ' ') buffer.Append(' ');
            }
            else buffer.Append(rune.ToString());
        }
        var cleaned = buffer.ToString().Trim();
        if (cleaned.Length <= MaximumCaptionLength) return cleaned;
        var end = 0;
        foreach (var start in StringInfo.ParseCombiningCharacters(cleaned))
        {
            if (start > MaximumCaptionLength - 1) break;
            end = start;
        }
        return cleaned[..end] + "…";
    }

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    private static float Coordinate(float v) => float.IsFinite(v) ? Math.Clamp(v, -MaximumCoordinate, MaximumCoordinate) : .5f;
    private static float Extent(float v) => float.IsFinite(v) && v > 0 ? MathF.Min(v, MaximumExtent) : .025f;
}
