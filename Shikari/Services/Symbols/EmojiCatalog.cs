using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Shikari.Services.Symbols;

/// <summary>Whole-grapheme lookup in the pinned offline artwork set; no font shaping or HTTP.</summary>
public static class EmojiCatalog
{
    private static readonly Lazy<Dictionary<string, string>> Assets = new(Load);
    public static int Count => Assets.Value.Values.Distinct(StringComparer.Ordinal).Count();

    public static bool TryResolve(string? emoji, out string key)
    {
        key = "";
        if (string.IsNullOrEmpty(emoji) || emoji.Length > 64 || emoji.Contains('\uFE0E')) return false;
        var starts = StringInfo.ParseCombiningCharacters(emoji);
        if (starts.Length != 1) return false;
        var candidate = string.Join("-", emoji.EnumerateRunes().Select(r => r.Value.ToString("x", CultureInfo.InvariantCulture)));
        return Assets.Value.TryGetValue(candidate, out key!) || Assets.Value.TryGetValue(Normalize(candidate), out key!);
    }

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shikari.Resources.Emoji.index.txt");
        if (stream == null) return result;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var key = line.Trim();
            if (key.Length == 0) continue;
            result[key] = key;
            result.TryAdd(Normalize(key), key);
        }
        return result;
    }

    private static string Normalize(string key) => string.Join("-", key.Split('-').Where(p => p != "fe0f"));

    public static string Truncate(string? text, int elements)
    {
        text ??= "";
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= elements) return text;
        if (elements <= 1) return "…";
        return text[..starts[elements - 1]] + "…";
    }

    public static string Fallback(string? emoji)
    {
        if (!TryResolve(emoji, out var key)) return "Symbol";
        return key switch
        {
            "1f432" => "Dragon", "1f409" => "Dragon", "1f916" => "Robot",
            "1f642" or "1f603" => "Smile", "1f621" => "Angry", "1f92f" => "Surprised",
            "1faf2" => "Left hand", "1faf1" => "Right hand",
            _ when key.EndsWith("-20e3", StringComparison.Ordinal) => char.ConvertFromUtf32(int.Parse(key.Split('-')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            _ => "Symbol",
        };
    }
}
