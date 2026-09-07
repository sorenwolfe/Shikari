using System;
using Shikari.Services;
using Shikari.Services.WtfDig;

namespace Shikari.Tests;

public static class ImportRoutingTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static void Run()
    {
        foreach (var (input, expected) in new[]
        {
            ("https://wtfdig.info/74/m12s#caro", ImportKind.WtfDig),
            ("wtfdig.info/74/m12s#caro", ImportKind.WtfDig),
            ("https://www.raidplan.io/plan/44JJjqZ6Mcgaxnnn#11", ImportKind.RaidPlan),
            ("<https://raidplan.io/plan/44JJjqZ6Mcgaxnnn#11>", ImportKind.RaidPlan),
            ("[Guide](https://wtfdig.info/74/m12s#caro)", ImportKind.WtfDig),
            ("https://www.fflogs.com/reports/PcyCvGAFRqzrXJBj?fight=30&type=damage-done", ImportKind.FfLogs),
            ("fflogs.com/reports/PcyCvGAFRqzrXJBj#fight=last", ImportKind.FfLogs),
            ("RPLAN1:abc", ImportKind.ShareCode),
            ("```\nRPLAN2:abc\n```", ImportKind.ShareCode),
        })
            Check(ImportSource.Parse(input).Kind == expected, "Wrong route for " + input);

        foreach (var invalid in new[]
        {
            "https://fflogs.com.evil.test/reports/PcyCvGAFRqzrXJBj",
            "https://evil.test/raidplan.io/plan/44JJjqZ6Mcgaxnnn",
            "https://evil.test/?guide=https://wtfdig.info/74/m12s",
            "https://wtfdig.info@evil.test/74/m12s",
            "https://user@wtfdig.info/74/m12s",
            "https://wtfdig.info:8443/74/m12s",
            "https://wtfdig.info/", "https://wtfdig.info/tools/idyllic",
            "https://raidplan.io/plan/44JJjqZ6Mcgaxnnn/extra",
            "https://fflogs.com/reports/PcyCvGAFRqzrXJBjEXTRA",
            "https://shikari.io/plan/44JJjqZ6Mcgaxnnn",
            "file:///D:/plan.json", "PcyCvGAFRqzrXJBj", "44JJjqZ6Mcgaxnnn",
            "http://wtfdig.info/74/m12s", "https://fflogs.com/reports/short",
        })
            Check(ImportSource.Parse(invalid).Kind == ImportKind.Unknown, "Unexpected route for " + invalid);

        Check(ImportSource.Parse("  ").Kind == ImportKind.Unknown, "Empty input");
        Check(ImportSource.Parse(new string('x', 524289)).Kind == ImportKind.Unknown, "Input limit");
        var link = WtfDigLink.Parse(ImportSource.Parse("wtfdig.info/74/m12s#caro").Value);
        Check(link.Options["strat"] == "caro", "Hash strategy preserved for exact user link");
        Check(WtfDigLink.Parse("https://wtfdig.info/74/m12s?strat=modified#caro").Options["strat"] == "modified", "Explicit query takes priority");
        Check(WtfDigLink.Parse("https://wtfdig.info/74/m12s?stratName=modified#caro").Options["stratName"] == "modified", "Alternate query takes priority");
        Console.WriteLine("PASS: unified import routing, URL boundaries, ambiguous codes, input limits, and strategy selection");
    }
}
