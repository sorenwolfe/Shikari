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
    public List<string> Queries { get; } = new();
    public bool RequireEvidenceQuery { get; init; } = true;
    public bool RequireEncounterIdentity { get; init; }
    public Responses(params string[] pages) => this.pages = new(pages);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) {
        cancel.ThrowIfCancellationRequested();
        string body;
        if (request.RequestUri!.AbsolutePath.EndsWith("/token")) body="{\"access_token\":\"fixture\",\"expires_in\":3600}";
        else {
            var query = JObject.Parse(await request.Content!.ReadAsStringAsync(cancel)).Value<string>("query")!;
            Queries.Add(query);
            if (RequireEvidenceQuery) Check(query.Contains("includeResources: true") && query.Contains("dataType: All"), "Evidence request must include resources and all event types");
            if (RequireEncounterIdentity) Check(query.Contains("encounterID"), "Fight query must request source encounter identity");
            body=pages.Dequeue();
        }
        return new(HttpStatusCode.OK) { Content=new StringContent(body) };
    }
}
public static async Task Run() {
    await CastIdentity();
    await PlayerEffectScope();
    using (var client = new FfLogsClient(new Responses("""
        {"data":{"reportData":{"report":{"fights":[{"id":30,"name":"Fixture","encounterID":1234,"startTime":10000,"endTime":20000}]}}}}
        """) { RequireEvidenceQuery = false, RequireEncounterIdentity = true })) {
        var fights = await client.GetFightsAsync("id", "secret", "code");
        Check((uint?)typeof(LogFight).GetProperty("EncounterId")?.GetValue(fights.Single()) == 1234,
            "Selected fight retains verified source encounter ID");
    }
    using(var client=new FfLogsClient(new Responses(
        "{\"data\":{\"reportData\":{\"report\":{\"masterData\":{\"actors\":[],\"abilities\":[]}}}}}",
        Page("""
        [{"timestamp":11000,"type":"begincast","sourceID":1,"sourceInstance":1,"abilityGameID":100},
         {"timestamp":12000,"type":"begincast","sourceID":1,"sourceInstance":2,"targetID":2,"abilityGameID":100},
         {"timestamp":14000,"type":"cast","sourceID":1,"sourceInstance":2,"targetID":3,"abilityGameID":100},
         {"timestamp":15000,"type":"cast","sourceID":1,"targetID":3,"abilityGameID":200}]
        """), Page("[]")) { RequireEvidenceQuery=false })) {
        var data=await client.GetFightDataAsync("id","secret","code",Fight);
        var castStart=typeof(LogCast).GetProperty("IsCastStart");
        Check(castStart!=null, "Cast evidence must identify observed cast-bar starts");
        Check(data.EnemyCasts.Count==3, "Interrupted, completed, and instant actions each retain one cast entry");
        Check((bool)castStart!.GetValue(data.EnemyCasts[0])! && data.EnemyCasts[0].CastSeconds==0, "Interrupted begincast remains a cast start without inventing duration");
        Check((bool)castStart.GetValue(data.EnemyCasts[1])! && data.EnemyCasts[1].CastSeconds==2, "Paired completed cast retains its observed start");
        Check(!(bool)castStart.GetValue(data.EnemyCasts[2])!, "Instant action must not become a cast-bar occurrence");
        var serialized = JArray.FromObject(data.EnemyCasts);
        Check(serialized[0]["CompletionTimeSeconds"]?.Type == JTokenType.Null &&
            serialized[1].Value<float?>("CompletionTimeSeconds") == 4 && serialized[2].Value<float?>("CompletionTimeSeconds") == 5,
            "Explicit cast completion timestamps survive parsing while unpaired starts remain unknown");
        Check(serialized[0]["TargetId"]?.Type == JTokenType.Null && serialized[1].Value<int?>("TargetId") == 2 &&
            serialized[2].Value<int?>("TargetId") == 3, "Cast-start target must not be replaced by a later completion target");
    }
    await CastPagination();
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
private static string MetadataPage(string rows, string players = "[1,2]", string actors = "[{\"id\":1,\"type\":\"Player\"},{\"id\":2,\"type\":\"Player\"},{\"id\":99,\"type\":\"NPC\"}]", string next = "null") {
    var page=JObject.Parse(Page(rows,next)); var report=(JObject)page.SelectToken("data.reportData.report")!;
    report["fights"]=JArray.Parse("[{\"id\":30,\"startTime\":10000,\"endTime\":20000,\"friendlyPlayers\":"+players+",\"enemyNPCs\":[{\"id\":99,\"instanceCount\":1}]}]");
    report["masterData"]=new JObject { ["actors"]=JArray.Parse(actors), ["abilities"]=new JArray() }; return page.ToString();
}
private static async Task CastIdentity() {
    async Task<JArray> Read(string rows, string? master=null) {
        using var client=new FfLogsClient(new Responses(master??MetadataPage("[]"),Page(rows),Page("[]")){RequireEvidenceQuery=false});
        return JArray.FromObject((await client.GetFightDataAsync("id","secret","code",Fight)).EnemyCasts);
    }
    var casts=await Read("""
    [{"timestamp":11000.25,"type":"begincast","sourceID":99,"sourceInstance":1,"targetID":1,"targetInstance":1,"abilityGameID":100},
     {"timestamp":12000,"type":"begincast","sourceID":99,"sourceInstance":2,"targetID":2,"abilityGameID":100},
     {"timestamp":14000.75,"type":"cast","sourceID":99,"sourceInstance":1,"targetID":2,"targetInstance":1,"abilityGameID":100},
     {"timestamp":15000,"type":"cast","sourceID":99,"sourceInstance":2,"abilityGameID":100}]
    """);
    Check(casts.Count==2 && casts[0].Value<float>("CastSeconds")==3.0005f && casts[1].Value<float>("CastSeconds")==3,
        "Overlapping NPC instances must complete their own exact start, preserving fractional timestamps.");
    Check(casts[0].Value<int?>("SourceInstance")==1 && casts[1].Value<int?>("SourceInstance")==2 &&
        casts[0].Value<int?>("TargetId")==1 && casts[0].Value<int?>("CompletionTargetId")==2 &&
        casts[0].Value<int?>("CompletionTargetInstance")==1 && casts[1]["CompletionTargetId"]?.Type==JTokenType.Null,
        "Start and completion identities remain distinct, including absent completion targets.");
    casts=await Read("""
    [{"timestamp":11000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":12000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":14000,"type":"cast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":15000,"type":"cast","sourceID":99,"sourceInstance":1,"abilityGameID":100}]
    """);
    Check(casts.Count==4 && casts.Take(2).All(c=>c["CompletionTimeSeconds"]?.Type==JTokenType.Null),
        "Ambiguous overlapping starts cannot be assigned a guessed completion, including a later second completion.");
    casts = await Read("""
    [{"timestamp":11000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":12000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":14000,"type":"cast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":15000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":16000,"type":"cast","sourceID":99,"sourceInstance":1,"abilityGameID":100}]
    """);
    Check(casts.Count == 5 && casts.Where(c => c.Value<bool>("IsCastStart")).All(c => c["CompletionTimeSeconds"]?.Type == JTokenType.Null),
        "Ambiguous older starts cannot be forgotten to justify pairing a later completion to a new start.");
    const string absent="[{\"timestamp\":11000,\"type\":\"begincast\",\"sourceID\":99,\"abilityGameID\":100},{\"timestamp\":14000,\"type\":\"cast\",\"sourceID\":99,\"abilityGameID\":100}]";
    casts=await Read(absent,Page("[]"));
    Check(casts.Count==2 && casts[0]["CompletionTimeSeconds"]?.Type==JTokenType.Null,
        "Unknown NPC instance without independent single-instance metadata cannot be paired.");
    casts=await Read(absent);
    Check(casts.Count==1 && casts[0].Value<float>("CastSeconds")==3 && casts[0]["SourceInstance"]?.Type==JTokenType.Null,
        "Independent single-instance fight metadata permits pairing without inventing an instance number.");
    foreach(var invalid in new[]{"\"1\"","0","-1","1.5","2147483648"}) {
        var failed=false;
        try { await Read("[{\"timestamp\":11000,\"type\":\"begincast\",\"sourceID\":99,\"sourceInstance\":"+invalid+",\"abilityGameID\":100}]"); }
        catch(FfLogsException){failed=true;}
        Check(failed,"Malformed cast identity must not masquerade as missing identity or complete cast history.");
    }
    var oversizedTarget = false;
    try { await Read("[{\"timestamp\":11000,\"type\":\"cast\",\"sourceID\":99,\"targetID\":999999999999999999999999999999,\"abilityGameID\":100}]"); }
    catch(FfLogsException) { oversizedTarget = true; }
    Check(oversizedTarget, "Oversized target IDs must report a malformed import rather than escape as numeric conversion errors.");
    // The independent actor metadata must lose precedence when observations prove several instances exist.
    casts = await Read("""
    [{"timestamp":11000,"type":"begincast","sourceID":99,"sourceInstance":1,"abilityGameID":100},
     {"timestamp":12000,"type":"cast","sourceID":99,"abilityGameID":100},
     {"timestamp":15000,"type":"cast","sourceID":99,"sourceInstance":2,"abilityGameID":200}]
    """);
    Check(casts.Count == 3 && casts[0]["CompletionTimeSeconds"]?.Type == JTokenType.Null,
        "A later observed second NPC instance invalidates pairing based on contradictory single-instance metadata.");
}
private static async Task PlayerEffectScope() {
    var outgoing=new JObject { ["timestamp"]=11000,["type"]="damage",["sourceID"]=1,["targetID"]=99,["abilityGameID"]=50 };
    var rows=new JArray(Enumerable.Range(0,32769).Select(_=>outgoing.DeepClone()));
    rows.Add(new JObject { ["timestamp"]=15000,["type"]="calculateddamage",["sourceID"]=99,["targetID"]=2,["abilityGameID"]=100 });
    var responses=new Responses(MetadataPage(rows.ToString()));
    using(var client=new FfLogsClient(responses)) {
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(result.Effects.Count==1 && result.Effects[0].TargetId==2 && result.EffectsComplete,
            "Acquisition must establish player scope before outgoing damage consumes the effect cap.");
        Check(responses.Queries.Single().Contains("friendlyPlayers") && responses.Queries.Single().Contains("actors"),
            "Player scope must come from selected-fight metadata and report actor types, not damage targets.");
    }
    foreach(var mutate in new Action<JObject>[] {
        p=>((JObject)p.SelectToken("data.reportData.report")!).Remove("fights"),
        p=>p.SelectToken("data.reportData.report.fights[0]")!["id"]=31,
        p=>p.SelectToken("data.reportData.report.fights[0]")!["startTime"]=9999,
        p=>p.SelectToken("data.reportData.report.fights[0]")!["friendlyPlayers"]=new JArray(1,1),
        p=>p.SelectToken("data.reportData.report.fights[0]")!["friendlyPlayers"]=new JArray(1,99),
        p=>p.SelectToken("data.reportData.report.fights[0]")!["friendlyPlayers"]=new JArray(1,88),
        p=>p.SelectToken("data.reportData.report.masterData")!["actors"]=new JArray(),
        p=>p.SelectToken("data.reportData.report")!["masterData"]=42,
        p=>p.SelectToken("data.reportData.report")!["masterData"]=JValue.CreateNull(),
        p=>p.SelectToken("data.reportData.report")!["masterData"]=new JArray()
    }) {
        var page=JObject.Parse(MetadataPage("[{\"timestamp\":12000,\"type\":\"applydebuff\",\"targetID\":1,\"abilityGameID\":1000010}]")); mutate(page);
        using var client=new FfLogsClient(new Responses(page.ToString()));
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(result.Complete && result.StatusEvents.Count == 1 && !result.EffectsComplete && result.Warnings.Any(w=>w.Contains("player",StringComparison.OrdinalIgnoreCase)),
            "Unverified player scope must be disclosed without throwing away independent status evidence.");
    }
    using(var client=new FfLogsClient(new Responses(MetadataPage("[]","[]","[]")))) {
        var result=await client.GetEvidenceAsync("id","secret","code",Fight);
        Check(result.EffectsComplete && result.Effects.Count==0,"An authoritative empty fight roster is distinct from missing metadata.");
    }
}
private static async Task CastPagination() {
    const string master="{\"data\":{\"reportData\":{\"report\":{\"masterData\":{\"actors\":[],\"abilities\":[]}}}}}";
    var missingCursor=Page("[]").Replace(",\"nextPageTimestamp\":null", "");
    foreach(var fixture in new[] {
        ("unreadable cast page", new[] { "{\"data\":{\"reportData\":{\"report\":null}}}" }),
        ("missing cast cursor", new[] { missingCursor }),
        ("nonadvancing cast cursor", new[] { Page("[]", "10000") }),
        ("out-of-range cast cursor", new[] { Page("[]", "20001") }),
        ("nonnumeric cast cursor", new[] { Page("[]", "\"12000\"") }),
        ("cast page budget", Enumerable.Range(0,20).Select(i=>Page("[]",(11000+i).ToString())).ToArray()),
        ("cast event budget", new[] { Page("["+string.Join(",",Enumerable.Repeat("{\"timestamp\":11000,\"type\":\"damage\"}",200001))+"]") }),
    }) {
        using var client=new FfLogsClient(new Responses(new[] { master }.Concat(fixture.Item2).Concat(new[] { Page("[]"), Page("[]") }).ToArray()) { RequireEvidenceQuery=false });
        var rejected=false;
        try { await client.GetFightDataAsync("id","secret","code",Fight); }
        catch(FfLogsException) { rejected=true; }
        Check(rejected, fixture.Item1+" must reject the import instead of returning apparently complete cast occurrences");
    }
    var pages=new Responses(master,
        Page("[{\"timestamp\":11000,\"type\":\"begincast\",\"sourceID\":1,\"sourceInstance\":1,\"abilityGameID\":100}]", "12000.5"),
        Page("[{\"timestamp\":14000,\"type\":\"cast\",\"sourceID\":1,\"sourceInstance\":1,\"abilityGameID\":100}]"), Page("[]")) { RequireEvidenceQuery=false };
    using(var client=new FfLogsClient(pages)) {
        var data=await client.GetFightDataAsync("id","secret","code",Fight);
        Check(data.EnemyCasts.Single().CastSeconds==3, "Cast pairing survives pagination");
        Check(pages.Queries.Any(q=>q.Contains("startTime: 12000.5")), "Cast pagination preserves fractional millisecond cursors without rounding");
    }
    using(var client=new FfLogsClient(new Responses(new[] { master }.Concat(Enumerable.Range(0,19).Select(i=>Page("[]",(11000+i).ToString())))
        .Concat(new[] { Page("[]"), Page("[]") }).ToArray()) { RequireEvidenceQuery=false })) {
        Check((await client.GetFightDataAsync("id","secret","code",Fight)).EnemyCasts.Count==0,
            "A completed twentieth cast page remains a valid empty history");
    }
}
}}
