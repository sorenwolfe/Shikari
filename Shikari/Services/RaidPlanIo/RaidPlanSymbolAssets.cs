using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Shikari.Model;

namespace Shikari.Services.RaidPlanIo;

/// <summary>Reads known artwork identifiers without downloading or interpreting arbitrary URLs.</summary>
public static class RaidPlanSymbolAssets
{
    private static string PathOf(string? asset)
    {
        var path = (asset ?? "").Trim().Replace('\\', '/');
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return uri.Scheme == "https" && uri.Host.Equals("cdn.raidplan.io", StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
                ? uri.AbsolutePath.TrimStart('/') : "";
        return path.TrimStart('/');
    }

    public static bool IsJobOrRole(string? asset)
    {
        var path = PathOf(asset);
        return path.StartsWith("game/ffxiv/job/", StringComparison.OrdinalIgnoreCase) && JobAssets.Read(path).KnowsAnything;
    }

    public static bool TryGameIcon(string? asset, out uint iconId)
    {
        var path = PathOf(asset);
        // Exact catalog artwork verified against the game's native textures. Do not infer
        // other numbered markers by arithmetic or treat these as Status row identifiers.
        iconId = path.ToLowerInvariant() switch
        {
            "game/ffxiv/mark/mark_link1.png" => 61211,
            "game/ffxiv/mark/mark_stop1.png" => 61221,
            _ => 0,
        };
        if (iconId != 0) return true;
        var match = Regex.Match(path, @"^ffxiv/legacy/icon(?:_hd)?/([0-9]{6})\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return match.Success && uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out iconId) &&
               iconId > 0 && iconId <= SymbolValidation.MaximumIconId;
    }

    public static bool TryDiagram(string? asset, out SymbolAsset diagram)
    {
        diagram = PathOf(asset).Equals("game/ffxiv/cut/4.svg", StringComparison.OrdinalIgnoreCase) ? SymbolAsset.Cut4 : SymbolAsset.None;
        return diagram != SymbolAsset.None;
    }

    public static string Fallback(string? asset)
    {
        var path = PathOf(asset);
        var target = Regex.Match(path, @"^game/ffxiv/mark/mark_(link|stop)([1-9])\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (target.Success) return (target.Groups[1].Value.Equals("link", StringComparison.OrdinalIgnoreCase) ? "Link " : "Stop ") + target.Groups[2].Value;
        var cut = Regex.Match(path, @"^game/ffxiv/cut/([1-9])\.svg$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (cut.Success) return "Cut " + cut.Groups[1].Value;
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? "Unknown artwork" : "Artwork: " + SymbolValidation.Caption(name);
    }
}
