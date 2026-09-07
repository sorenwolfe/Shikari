using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.WtfDig;
namespace Shikari.Services { public static class JobRoles { public static RaidRole RoleFor(string name)=>RaidRole.Unknown; } }
namespace Shikari.Tests {
public static class WtfDigBoardTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        const string source = """
        const config={fightKey:'m12s',title:'Example',strats:{caro:{defaults:{replication1:'caro'}}},toggles:[{key:'replication1',defaultValue:'dn',options:[{value:'caro'},{value:'dn'}]}]};
        const caro={stratName:'caro',stratUrl:'https://raidplan.io/plan/abcdefghijklmnop',strats:[{phaseName:'Replication 1',mechs:{caro:[{mechanic:'First',description:'Your first instructions',url:'https://raidplan.io/plan/abcdefghijklmnop#2'},{mechanic:'Second',description:'Your next instructions',url:'https://raidplan.io/plan/abcdefghijklmnop#1'}],dn:[{mechanic:'Wrong variant'}]}}]};
        """;
        var guide=WtfDigGuide.Read(WtfDigLink.Parse("https://wtfdig.info/74/m12s#caro"),source);
        var selection=new WtfDigSelection { Strategy="caro" };
        var preview=WtfDigMapper.Convert(guide,selection);
        Check(preview.MissingVariants.Count==0 && preview.Plan.Slides.Count==2,"Strategy defaults select the actual variant automatically");
        Check(preview.BoardReferences.Count==2 && preview.Links.Count==1,"Keep per-step source references while deduplicating board downloads");
        const string json="""
        {"steps":3,"nodes":[{"type":"marker","meta":{"step":0,"pos":{"x":100,"y":100},"size":{"w":20,"h":20}},"attr":{"text":"MT"}},{"type":"circle","meta":{"step":1,"pos":{"x":200,"y":200},"size":{"w":60,"h":60}},"attr":{}},{"type":"rect","meta":{"step":2,"pos":{"x":300,"y":200},"size":{"w":60,"h":30}},"attr":{}}]}
        """;
        var raw=Newtonsoft.Json.Linq.JObject.Parse(json);
        using var client=new WtfDigClient(); var requests=0;
        using var prepared=await client.PrepareWithBoardsAsync(preview,"",false,(code,token)=>{requests++;return Task.FromResult(raw.ToString());},CancellationToken.None);
        Check(requests==1,"Fetch each board once");
        Check(prepared.Plan.Slides.Any(s=>s.Items.Count>0),"Imported board has editable items");
        Check(prepared.Plan.Slides[0].Id==preview.Plan.Slides[0].Id && prepared.Plan.Slides[0].Notes.Contains("Your first instructions"),"Preserve guide identity and instructions");
        Check(prepared.Plan.Slides[0].SourceUrl.EndsWith("#2"),"Exact source step retained");
        Check(prepared.Plan.Slides[0].Items.Any(i=>i.Kind==CanvasItemKind.Zone),"Explicit step 2 maps to correct geometry");
        Check(prepared.Plan.Slides.Count==3,"Keep full board without duplicating explicitly linked steps");
        Check(prepared.Plan.Slides.All(s=>s.ArenaOverride!=null),"Each board retains its own arena settings");
        Check(preview.Plan.Slides.All(s=>s.Items.Count==0),"Composition cannot mutate the preview");
        var geometry=Newtonsoft.Json.JsonConvert.SerializeObject(prepared.Plan.Slides);
        prepared.Plan.Timeline.Add(new TimelineEntry { Label="Unrelated authored timing", TimeSeconds=5 });
        var replay=new Shikari.Services.Replay.ReplayBuffer(prepared.Plan,-1,DateTime.UtcNow).Attempt;
        replay.Duration=30;
        replay.Mechanics.Add(new Shikari.Services.Replay.ReplayMechanic { Label="Replication 1", ActionId=42, Occurrence=1, Time=10, ExpectedResolve=12 });
        var enriched=Shikari.Services.Replay.StrategyEnrichment.Apply(prepared.Plan,replay);
        Check(enriched.MatchedMechanics==1 && prepared.Plan.Timeline.Any(e=>e.Label=="Replication 1" && e.SlideId=="" && !e.Enabled),"Composed editable guide absorbs phase timing without guessing a destination");
        Check(geometry==Newtonsoft.Json.JsonConvert.SerializeObject(prepared.Plan.Slides),"Evidence must preserve all composed boards and instructions");
        Check(StrategyEvidenceValidation.IsValid(prepared.Plan),"Combined guide and evidence remain valid to share");
        using var failed=await client.PrepareWithBoardsAsync(preview,"",false,(_,_)=>throw new System.Net.Http.HttpRequestException("offline"),CancellationToken.None);
        Check(failed.Plan.Slides.Count==2 && failed.Warnings.Any(w=>w.Contains("offline")),"Partial download failure retains guide and reports source failure");
        using var malformed=await client.PrepareWithBoardsAsync(preview,"",false,(_,_)=>Task.FromResult("{\"nodes\":[{\"type\":\"circle\",\"meta\":{\"pos\":{\"x\":\"bad\",\"y\":0},\"size\":{\"w\":20,\"h\":20}}}]}"),CancellationToken.None);
        Check(malformed.Plan.Slides.Count==2 && malformed.Warnings.Any(w=>w.Contains("board unavailable")),"Malformed board is isolated without discarding the guide");
        using var cancel=new CancellationTokenSource();cancel.Cancel();var cancelled=false;
        try { await client.PrepareWithBoardsAsync(preview,"",false,(_,_)=>Task.FromResult(json),cancel.Token); } catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"Cancelled automatic import never produces an adoptable plan");
        Console.WriteLine("PASS: strategy defaults, deduplicated automatic boards, exact step mapping, full board retention, arena overrides, isolation and failures");
    }
}

}
