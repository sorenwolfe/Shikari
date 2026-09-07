using System;
using System.Text.RegularExpressions;
using Shikari.Services.WtfDig;

namespace Shikari.Services;

public enum ImportKind { Unknown, RaidPlan, WtfDig, FfLogs, ShareCode }

public readonly record struct ImportSource(ImportKind Kind, string Value, string Hint)
{
    public const int MaxLength = 512 * 1024;

    // Decide locally before invoking a provider. The older individual parsers also accept bare
    // codes, whose formats overlap; a shared entry point must not guess which service owns one.
    public static ImportSource Parse(string? input)
    {
        var text = (input ?? "").Trim();
        ImportSource Unknown(string hint) => new(ImportKind.Unknown, text, hint);
        if (text.Length == 0) return Unknown("WTFDIG · raidplan.io · FF Logs · Shikari share codes");
        if (text.Length > MaxLength) return Unknown("That input is too long. Import a saved file instead.");
        text = text.Trim('`').Trim();
        if (text.StartsWith("RPLAN1:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("RPLAN2:", StringComparison.OrdinalIgnoreCase))
            return new(ImportKind.ShareCode, text, "Shikari share code · import a new plan");
        if (text.StartsWith('<') && text.EndsWith('>')) text = text[1..^1].Trim();
        if (text.StartsWith('[') && text.EndsWith(')') && text.IndexOf("](", StringComparison.Ordinal) is var divider && divider > 0)
            text = text[(divider + 2)..^1].Trim();
        if (text.Length > 4096) return Unknown("Paste a supported link or a Shikari share code.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return Unknown("Use an HTTPS link from WTFDIG, raidplan.io, or FF Logs.");

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var canonical = new UriBuilder(uri) { Host = host }.Uri.AbsoluteUri;
        switch (host)
        {
            case "wtfdig.info":
                try
                {
                    WtfDigLink.Parse(canonical);
                    return new(ImportKind.WtfDig, canonical, "WTFDIG guide · choose your strategy and role");
                }
                catch (FormatException ex) { return Unknown(ex.Message); }
            case "raidplan.io" when Regex.IsMatch(uri.AbsolutePath, @"^/plan/[A-Za-z0-9_-]{10,32}/?$", RegexOptions.CultureInvariant):
                return new(ImportKind.RaidPlan, canonical, "raidplan.io · import an editable plan");
            case "fflogs.com" when Regex.IsMatch(uri.AbsolutePath, @"^/reports/[A-Za-z0-9]{16}/?$", RegexOptions.CultureInvariant):
                return new(ImportKind.FfLogs, canonical, "FF Logs report · add a pull's timeline and cooldowns to this plan");
            default:
                return Unknown("Paste the full fight or plan link, or a Shikari share code. Bare report and plan IDs can look identical.");
        }
    }
}
