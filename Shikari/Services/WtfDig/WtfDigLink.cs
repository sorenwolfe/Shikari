using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace Shikari.Services.WtfDig;

public sealed class WtfDigLink
{
    public string Route { get; private init; } = "";
    public string Url { get; private init; } = "";
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public string DataUrl => "https://raw.githubusercontent.com/mczub/wtfdig/main/src/routes/" + Route + "/data.ts";
    public static WtfDigLink Parse(string input)
    {
        if (input.Length > 4096 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "wtfdig.info" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new FormatException("Use an HTTPS fight link from wtfdig.info.");
        var route = uri.AbsolutePath.Trim('/');
        if (!Regex.IsMatch(route, @"^(?:[0-9]{2}/[a-z0-9-]+|ultimates/[a-z0-9-]+)$", RegexOptions.CultureInvariant))
            throw new FormatException("Use a fight page, such as wtfdig.info/74/m9s. Board bundles and helpers are not fight guides.");
        var result = new WtfDigLink { Route = route, Url = uri.AbsoluteUri };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result.Options[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return result;
    }
}
