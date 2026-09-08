using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Shikari.Services.WtfDig;

namespace Shikari.Tests;

public static class WtfDigLiteralTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string FirstMechanic(JToken? value) => value is JArray { Count: > 0 } array ? array[0].Value<string>("mechanic") ?? "" : "";
    public static void Run(string sourcePath)
    {
        var data = LiteralData.Read("""
            const caro = {overview:[{mechanic:'Overview'}], phases:{first:[{mechanic:'Clones'}]}};
            const alias = caro;
            const selected = caro.phases;
            const variants = {caro:caro.overview, alternate:alias.phases.first};
            const first = selected.first;
            const missing = caro.absent;
            const unsafeCall = caro.overview.map(run);
            const computed = caro[key];
            const quoted = caro.'overview';
            const unknown = globalThis.process;
            const loop = loop.overview;
            const base = {overview:[{mechanic:'Inherited'}]};
            const spreadFirst = { ...base, overview:[{mechanic:'Own'}] };
            const spreadLast = { overview:[{mechanic:'Own'}], ...base };
            const own = spreadFirst.overview;
            const inherited = spreadLast.overview;
            const cyclic = { first: 'Usable', second: cyclic.second };
            const usable = cyclic.first;
            const broken = cyclic.second;
            const unknownSpread = runtime();
            const uncertain = { value:'Authored', ...unknownSpread };
            const uncertainSelection = uncertain.value;
            const overrideAfterUnknown = { ...unknownSpread, value:'Certain' };
            const certainSelection = overrideAfterUnknown.value;
            const repeated = { overview:[{mechanic:'First'}], ...base, overview:[{mechanic:'Final'}] };
            const finalSelection = repeated.overview;
            const known = {value:'Restored'};
            const nestedUnknown = {value:'Initial', ...unknownSpread, ...known};
            const nestedCopy = {...nestedUnknown};
            const nestedSelection = nestedCopy.value;
            const nestedUncertain = {...{value:'Initial', ...unknownSpread}};
            const nestedUncertainSelection = nestedUncertain.value;
            """);
        Check(FirstMechanic(data["variants"]["caro"]) == "Overview", "A literal member reference must preserve the selected mechanic array");
        Check(FirstMechanic(data["first"]) == "Clones" && FirstMechanic(data["variants"]["alternate"]) == "Clones", "Aliases and nested member chains must resolve");
        foreach (var name in new[] { "missing", "unsafeCall", "computed", "quoted", "unknown", "loop", "broken" })
            Check(LiteralData.IsUnsupported(data[name]), name + " must remain unsupported without executing guide code");
        Check(FirstMechanic(data["own"]) == "Own" && FirstMechanic(data["inherited"]) == "Inherited", "Object spread ordering must be preserved during member selection");
        Check(data["usable"].ToString() == "Usable", "An unrelated unsupported member must not destroy a usable selection");
        Check(LiteralData.IsUnsupported(data["uncertainSelection"]), "An unknown later spread might override a selected field and must remain unsupported");
        Check(data["certainSelection"].ToString() == "Certain", "An explicit field after an unknown spread still has a certain value");
        Check(FirstMechanic(data["finalSelection"]) == "Final", "A repeated property after a spread overrides its earlier occurrence in source order");
        Check(data["nestedSelection"].ToString() == "Restored", "A later known override retains certainty through another object spread");
        Check(LiteralData.IsUnsupported(data["nestedUncertainSelection"]), "An unknown spread propagates uncertainty through another object spread");
        if (sourcePath.Length > 0)
        {
            var actual = LiteralData.Read(File.ReadAllText(sourcePath));
            foreach (var name in new[] { "idyllicOverviewMechs", "idyllicClones1Mechs", "idyllicPlatforms1Mechs", "idyllicClones2Mechs", "idyllicPlatforms2Mechs", "idyllicBlackholeMechs" })
                Check(actual[name]["caro"] is JArray items && items.Count > 0 && items.All(i => !LiteralData.IsUnsupported(i)), name + " must retain the real selected Caro data");
            Console.WriteLine("PASS: all six real Caro Idyllic mechanic tables resolve.");
        }
        Console.WriteLine("PASS: bounded literal member lookup, aliases, spread precedence, unresolved paths, cycles and rejected executable expressions.");
    }
}
