using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Shikari.Services.FfLogs;
namespace Shikari.Services { public static class CallTemplate { public static string FormatTime(float time) => time.ToString(); } }
namespace Shikari.Tests {
public static class FfLogsEvidenceTests {
private static int checks;
private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
private static readonly LogFight Fight = new() { Id=30, StartTime=10000, EndTime=20000 };
private static string Page(string rows, string next="null") => "{\"data\":{\"reportData\":{\"report\":{\"events\":{\"data\":" + rows + ",\"nextPageTimestamp\":" + next + "}}}}}";
private sealed class Responses : HttpMessageHandler {
    private readonly Queue<string> pages;
    public Responses(params string[] pages) => this.pages = new(pages);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) {
        cancel.ThrowIfCancellationRequested();
        string body;
        if (request.RequestUri!.AbsolutePath.EndsWith("/token")) body="{\"access_token\":\"fixture\",\"expires_in\":3600}";
        else {
            var query = JObject.Parse(await request.Content!.ReadAsStringAsync(cancel)).Value<string>("query")!;
            Check(query.Contains("includeResources: true") && query.Contains("dataType: All"), "Evidence request must include resources and all event types");
            body=pages.Dequeue();
        }
        return new(HttpStatusCode.OK) { Content=new StringContent(body) };
    }
}
public static async Task Run() {
    var parser=new LogEvidenceParser(Fight, new Dictionary<uint,string>{{1000048,"Well Fed"}});
    parser.AddPage(JArray.Parse("""
    [
      {"timestamp":11000,"type":"applybuff","sourceID":1,"targetID":2,"abilityGameID":1000048,"duration":30000,"extraInfo":123,"stack":2},
      {"timestamp":12000,"type":"refreshdebuff","sourceID":1,"targetID":2,"abilityGameID":1000048},
      {"timestamp":13000,"type":"removebuff","targetID":2,"abilityGameID":1000048},
      {"timestamp":14000,"type":"applybuffstack","sourceID":1,"targetID":2,"abilityGameID":1000048,"stack":3},
      {"timestamp":15000,"type":"cast","sourceID":1,"targetID":2,"sourceResources":{"x":10008,"y":10551},"targetResources":{"x":10000,"y":9250}},
      {"timestamp":15000,"type":"damage","sourceID":1,"sourceResources":{"x":10008,"y":10551}},
      {"timestamp":16000,"type":"cast","sourceID":3,"sourceResources":{"x":1}},
      {"timestamp":10000,"type":"combatantinfo","sourceID":2,"auras":[{"source":1,"ability":1000048,"stacks":1,"name":"Well Fed"}]}
    ]
    """));
    var evidence=parser.Result;
    Check(evidence.StatusEvents.Count==5, "Applications, refreshes, removals, stacks, and baseline retained");
    var apply=evidence.StatusEvents[0];
    Check(apply.Time==1 && apply.Duration==30 && apply.AbilityId==1000048 && apply.StatusId==48, "IDs and millisecond times normalized");
    Check(apply.Name=="Well Fed" && apply.SourceId==1 && apply.TargetId==2, "Names and actor identity retained");
    Check(apply.Stacks==2 && apply.Parameter==null && apply.ExtraInfo==123, "Stack and raw extraInfo must never become live parameters");
    Check(evidence.StatusEvents[1].Duration==null && evidence.StatusEvents[1].Stacks==null, "Absent values remain unknown");
    Check(evidence.StatusEvents[2].Change==LogStatusChange.Remove && evidence.StatusEvents[3].Change==LogStatusChange.Stacks, "Removal and stack changes remain distinct");
    Check(evidence.StatusEvents[4].Change==LogStatusChange.Baseline && evidence.StatusEvents[4].Duration==null, "Initial auras must not become fresh applications");
    Check(evidence.Positions.Count==2 && evidence.Positions[0].Time==5 && evidence.Positions[0].X==10008 && evidence.Positions[1].ActorId==2, "Sparse source positions preserved and exact duplicates discarded");
    var malformed=new LogEvidenceParser(Fight);
    malformed.AddPage(JArray.Parse("""
    [{"timestamp":11000,"type":"applybuff","targetID":2,"abilityGameID":4294967295,"duration":"","stack":-1},
    {"timestamp":12000,"type":"applybuff","targetID":2,"abilityGameID":4294967296},
    {"timestamp":13000,"type":"applybuff","targetID":2147483648,"abilityGameID":1000048},
    {"timestamp":14000.5,"type":"applybuff","targetID":2,"abilityGameID":1000048,"duration":-1},
    {"timestamp":9999,"type":"applybuff","targetID":2,"abilityGameID":1000048},
    {"timestamp":20001,"type":"applybuff","targetID":2,"abilityGameID":1000048},
    {"type":"applybuff","targetID":2,"abilityGameID":1000048}]
    """));
    Check(malformed.Result.StatusEvents.Count==2 && malformed.Result.StatusEvents[0].AbilityId==uint.MaxValue && malformed.Result.StatusEvents[0].StatusId==0, "Unknown numeric IDs preserved without wrapping, invalid identities/times skipped");
    Check(malformed.Result.StatusEvents.All(x=>x.Duration==null && x.Stacks==null), "Invalid optional numeric values remain unknown");
    Check(malformed.Result.Warnings.Count>0 && !malformed.Result.Complete, "Discarded malformed evidence is disclosed");
    using(var client=new FfLogsClient(new Responses(Page("[{\"timestamp\":11000,\"type\":\"applybuff\",\"targetID\":2,\"abilityGameID\":1000048}]","12000"), Page("[{\"timestamp\":13000,\"type\":\"removebuff\",\"targetID\":2,\"abilityGameID\":1000048}]")))) {
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(result.Complete && result.StatusEvents.Count==2, "Acquisition consumes all pages");
    }
    using(var client=new FfLogsClient(new Responses(Page("[]","10000")))) {
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(!result.Complete && result.Warnings.Count>0, "Nonadvancing cursor stops with warning");
    }
    using(var client=new FfLogsClient(new Responses("{\"data\":{\"reportData\":{\"report\":null}}}"))) {
        Check(!(await client.GetEvidenceAsync("id","secret","code",Fight)).Complete, "Missing data is not a complete empty fight");
    }
    using(var client=new FfLogsClient(new Responses(Page("[]","12000"), "{\"errors\":[{\"message\":\"unavailable\"}]}"))) {
        Check(!(await client.GetEvidenceAsync("id","secret","code",Fight)).Complete, "Later request failure is explicit incomplete evidence");
    }
    using(var client=new FfLogsClient(new Responses(Enumerable.Range(0,20).Select(i=>Page("[]",(11000+i).ToString())).ToArray()))) {
        Check(!(await client.GetEvidenceAsync("id","secret","code",Fight)).Complete, "Page budget cannot silently truncate");
    }
    using(var client=new FfLogsClient(new Responses(Page("["+string.Join(",",Enumerable.Repeat("{\"timestamp\":11000,\"type\":\"damage\"}",10001))+"]")))) {
        Check((await client.GetEvidenceAsync("id","secret","code",Fight)).Complete, "API may extend a page beyond limit to finish a timestamp group");
    }
    var sameTimeRows=new JArray(Enumerable.Range(0,24).Select(i=>new JObject {
        ["timestamp"]=11000, ["type"]="applybuff", ["targetID"]=2, ["abilityGameID"]=1000001+i }));
    using(var client=new FfLogsClient(new Responses(Page(sameTimeRows.ToString())))) {
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(result.StatusEvents.Select(x=>x.AbilityId).SequenceEqual(Enumerable.Range(1000001,24).Select(x=>(uint)x)), "Same-timestamp evidence must retain server order");
    }
    using(var client=new FfLogsClient(new Responses(Page("["+string.Join(",",Enumerable.Repeat("{\"timestamp\":11000}",200001))+"]")))) {
        Check(!(await client.GetEvidenceAsync("id","secret","code",Fight)).Complete, "Event budget cannot silently truncate");
    }
    var unknownSource=new LogEvidenceParser(Fight);
    unknownSource.AddPage(JArray.Parse("[{\"timestamp\":11000,\"type\":\"applydebuff\",\"sourceID\":-1,\"targetID\":2,\"abilityGameID\":1000048}]"));
    Check(unknownSource.Result.Complete && unknownSource.Result.StatusEvents[0].SourceId==-1, "FF Logs negative source sentinel preserved without labeling valid data corrupt");
    using(var client=new FfLogsClient(new Responses())) {
        using var cts=new CancellationTokenSource(); cts.Cancel();
        try { await client.GetEvidenceAsync("id","secret","code",Fight,cts.Token); throw new Exception("Cancellation swallowed"); }
        catch(OperationCanceledException) { checks++; }
    }
    Console.WriteLine($"PASS: {checks} FF Logs evidence checks");
}
}}
