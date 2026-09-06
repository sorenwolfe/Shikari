using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.WtfDig;
namespace Shikari.Tests;
public static class WtfDigTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        var link = WtfDigLink.Parse("https://wtfdig.info/74/m9s?role=Tank&party=2&strat=toxic");
        Check(link.Route == "74/m9s" && link.Options["party"] == "2", "Fight route and selections preserved");
        foreach (var bad in new[] { "https://wtfdig.info.evil.test/74/m9s", "https://evil.test/wtfdig.info/74/m9s", "file:///74/m9s", "https://wtfdig.info/tools/idyllic", "https://wtfdig.info/", "https://wtfdig.info:8443/74/m9s" })
        {
            var rejected = false; try { WtfDigLink.Parse(bad); } catch (FormatException) { rejected = true; }
            Check(rejected, "Unexpected accepted URL: " + bad);
        }
        const string source = """
        import type { Strat } from '$lib/types';
        const mechanics = [{ mechanic: 'Assignment', description: 'Watch status', url: 'https://raidplan.io/plan/abcdefghijklmnop#2',
          strats: [{role:'Tank',party:1,description:'North'}, {role:'Tank',party:2,description:'South',mask:getCircleMaskUrl(50,75,10)}] }];
        export const config = {fightKey:'m9s',title:'Example encounter',strats:{toxic:{label:'Example strategy'}},timeline:[{mechName:'Assignment',startTimeMs:30000}]};
        export const toxic: Strat = {stratName:'toxic',stratUrl:'https://raidplan.io/plan/abcdefghijklmnop',description:'Guide',strats:[{phaseName:'Phase 1',mechs:mechanics}]};
        """;
        var guide = WtfDigGuide.Read(link, source);
        Check(guide.Strategies.Count == 1 && guide.Title == "Example encounter", "Read fight configuration and strategy");
        var options = new WtfDigSelection { Strategy = "toxic", Role = "Tank", Party = 2 };
        var preview = WtfDigMapper.Convert(guide, options);
        Check(preview.Plan.Slides.Any(s => s.Notes.Contains("South")) && !preview.Plan.Slides.Any(s => s.Notes.Contains("North")), "Role/group isolation");
        Check(preview.Plan.Timeline.Count == 1 && !preview.Plan.Timeline[0].Enabled, "Imported timing stays disabled");
        Check(preview.Plan.AdaptiveMechanics.Count == 0, "Guide prose does not invent live rules");
        Check(preview.Links.Any(l => l.Code == "abcdefghijklmnop"), "Selected guide links exposed");
        Check(preview.Warnings.Any(), "Unsupported mask disclosed");
        var literals = LiteralData.Read("const a = {text: 'one' + 'two'}; const b = a; const c = danger();");
        Check(literals["b"]["text"]!.ToString() == "onetwo", "References and concatenation resolved");
        Check(LiteralData.IsUnsupported(literals["c"]), "Calls not executed or mistaken for data");
        var variantSource = source.Replace("mechs:mechanics", "mechs:{north:mechanics,south:mechanics}");
        var variants = WtfDigGuide.Read(link, variantSource);
        var missing = WtfDigMapper.Convert(variants, options);
        Check(missing.MissingVariants.Count == 1, "Unresolved variants require a choice");
        options.Variants[missing.MissingVariants[0].Key] = "south";
        Check(WtfDigMapper.Convert(variants, options).MissingVariants.Count == 0, "Explicit variant selected");
        Check(LiteralData.IsUnsupported(LiteralData.Read("const a = b; const b = a;")["a"]), "Circular references marked unsupported");
        Check(LiteralData.Read("const a = 'outside'; function f() { const a = 'inside'; }")["a"].ToString() == "outside", "Nested local does not shadow source data");
        var spread = LiteralData.Read("const a = [{x:1}]; const b = [...a, {x:2}]; const c = { ...b, x:3 };");
        Check(spread["b"].Count() == 2, "Array spreads resolved");
        var invalid = false; try { LiteralData.Read(new string(' ', LiteralData.MaxSourceCharacters + 1)); } catch (InvalidDataException) { invalid = true; }
        Check(invalid, "Oversized source rejected before parsing");
        var unsupported = WtfDigGuide.Read(link, source.Replace("description: 'Watch status'", "description: {a:makeText(),b:'West'}"));
        options.Variants["Phase 1/0/description"] = "a";
        Check(WtfDigMapper.Convert(unsupported, options).Warnings.Any(w => w.Contains("description: unsupported")), "Selected unsupported expression is disclosed");
        var singleUnsupported = WtfDigGuide.Read(link, source.Replace("description: 'Watch status'", "description: {a:makeText()}"));
        options.Variants.Clear();
        Check(WtfDigMapper.Convert(singleUnsupported, options).Warnings.Any(w => w.Contains("description: unsupported")), "Single unsupported variant is disclosed");
        var toggles = WtfDigGuide.Read(link, source.Replace("{role:'Tank',party:1,description:'North'}, {role:'Tank',party:2,description:'South',mask:getCircleMaskUrl(50,75,10)}",
            "{role:'Tank',party:2,toggleKey:'direction',toggleValue:'north',description:'North'}, {role:'Tank',party:2,toggleKey:'direction',toggleValue:'south',description:'South'}, {role:'Healer',party:2,toggleKey:'direction',toggleValue:'east',description:'East'}"));
        foreach (var invalidChoice in new[] { "typo", "east" })
        {
            options.Variants["toggle/direction"] = invalidChoice;
            Check(WtfDigMapper.Convert(toggles, options).MissingVariants.Any(v => v.Key == "toggle/direction"), "Invalid or inapplicable toggle requires selection");
        }
        options.Variants["toggle/direction"] = "south";
        var chosenToggle = WtfDigMapper.Convert(toggles, options);
        Check(chosenToggle.MissingVariants.Count == 0 && chosenToggle.Plan.Slides[0].Notes.Contains("South") && !chosenToggle.Plan.Slides[0].Notes.Contains("North"), "Valid toggle includes only selected instructions");
        NetworkChecks(source, link, preview).GetAwaiter().GetResult();
        Console.WriteLine("PASS: WTFDIG URL validation, literal data, role/group mapping, disabled timing, links and unsupported reporting");
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Respond(request)); }
    }
    private static async Task NetworkChecks(string source, WtfDigLink link, WtfDigPreview preview)
    {
        var handler = new FakeHttp { Respond = request =>
        {
            Check(request.RequestUri!.Host == "raw.githubusercontent.com", "Source fetch host fixed");
            Check(request.RequestUri.AbsolutePath.EndsWith("/74/m9s/data.ts"), "Route resolves to data source");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(source) };
        }};
        using var client = new WtfDigClient(handler);
        Check((await client.LoadAsync(link, CancellationToken.None)).Strategies.Count == 1, "Source fetch and parser integrated");
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/") } };
        var rejected = false; try { await client.LoadAsync(link, CancellationToken.None); } catch (HttpRequestException) { rejected = true; }
        Check(rejected, "Redirect response rejected");
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[LiteralData.MaxSourceCharacters + 1]) };
        rejected = false; try { await client.LoadAsync(link, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Oversized HTTP body rejected");
        var directory = Path.Combine(Path.GetTempPath(), "shikari-wtfdig-" + Guid.NewGuid().ToString("N"));
        try
        {
            preview.ImageReferences[preview.Plan.Slides[0].Id] = "https://wtfdig.info/74/m9s/test.png";
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x89,0x50,0x4e,0x47,13,10,26,10,0,0,0,0 }) };
            using (var prepared = await client.PrepareAsync(preview, directory, true, CancellationToken.None))
            {
                Check(prepared.Plan.Slides[0].BackdropId.EndsWith(".png"), "Reference image attached to isolated draft");
                Check(preview.Plan.Slides[0].BackdropId == "", "Preparing does not mutate preview");
                Check(Directory.GetFiles(directory).Length == 1, "Image staged");
            }
            Check(Directory.GetFiles(directory).Length == 0, "Uncommitted images cleaned up");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            rejected = false; try { await client.PrepareAsync(preview, directory, true, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && Directory.GetFiles(directory).Length == 0, "Cancellation leaves no staged assets");
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
            using var missing = await client.PrepareAsync(preview, directory, true, CancellationToken.None);
            Check(missing.Plan.Slides[0].BackdropId == "" && missing.Warnings.Any(w => w.Contains("unavailable")), "Missing image produces a visible warning and retains notes");
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
        Console.WriteLine("PASS: fake HTTP source fetch, redirects, size limits, draft isolation, image cleanup, cancellation and missing assets");
    }
}
