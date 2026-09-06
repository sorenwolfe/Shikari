using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
namespace Shikari.Services.WtfDig;

public sealed class WtfDigGuide
{
    public required WtfDigLink Link { get; init; }
    public required JObject Config { get; init; }
    public required IReadOnlyList<JObject> Strategies { get; init; }
    public string Title => (string?)Config["title"] ?? Link.Route;
    public required string SourceHash { get; init; }
    public DateTime RetrievedUtc { get; init; } = DateTime.UtcNow;
    public static WtfDigGuide Read(WtfDigLink link, string source)
    {
        var data = LiteralData.Read(source);
        var config = data.Values.OfType<JObject>().FirstOrDefault(o => o["fightKey"]?.Type == JTokenType.String);
        var strategies = data.Values.OfType<JObject>().Where(o => o["stratName"]?.Type == JTokenType.String && o["strats"] is JArray).ToArray();
        if (config == null || strategies.Length == 0)
            throw new InvalidDataException("This guide's source structure is not supported yet. No plan was imported.");
        return new WtfDigGuide { Link = link, Config = config, Strategies = strategies,
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant() };
    }
}

public sealed class WtfDigSelection
{
    public string Strategy { get; set; } = "";
    public string Role { get; set; } = "Tank";
    public int Party { get; set; } = 1;
    public Dictionary<string, string> Variants { get; set; } = new(StringComparer.Ordinal);
}

public sealed record WtfDigBoardLink(string Label, string Url, string Code);
public sealed record WtfDigVariant(string Key, IReadOnlyList<string> Choices);
