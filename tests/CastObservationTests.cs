using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Replay;
using Shikari.Services.FfLogs;

namespace Shikari.Services { public static class CallTemplate { public static string FormatTime(float value) => value.ToString(); } }
namespace Shikari.Tests
{
    public static class CastObservationTests
    {
        private static int checks;
        private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
        public static void Run()
        {
            var failures = new List<string>();
            foreach (var test in new Action[] { Roundtrip, Legacy, Validation, LiveAdapter, PrePullPrediction, BoundedCopy, LogAdapter })
                try { test(); } catch (Exception ex) { failures.Add(test.Method.Name + ": " + ex.Message); }
            if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
            Console.WriteLine($"PASS: {checks} cast observation checks");
        }
        private static void Roundtrip()
        {
            var json = JObject.FromObject(new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10 });
            json["Casts"] = JArray.Parse("""
                [{"Source":"Live","ActionId":100,"Occurrence":1,"ObservedTime":1.1,"StartTime":1,
                  "ExpectedEndTime":4,"CasterId":42,"TargetId":43,"CasterWorldPosition":{"X":100,"Y":2,"Z":90}}]
                """);
            var copy = JsonConvert.DeserializeObject<ReplayAttempt>(json.ToString(), PlanJson.Readable())!;
            if (JObject.FromObject(copy)["Casts"] is not JArray casts || casts.Count != 1)
                throw new Exception("Persisted cast-start context must survive the production replay reader and writer.");
            Check(copy.Casts[0].CasterWorldPosition == new Vector3(100, 2, 90) && copy.Casts[0].TargetId == 43,
                "Roundtrip retains world XYZ and source-scoped identities");
            Check(copy.Casts[0].CompletionTime == null && copy.Casts[0].TargetWorldPosition == null,
                "Roundtrip never converts unknown completion or geometry to zero");
            Check(ReplayValidation.IsValid(copy), "Valid optional cast data loads");
        }
        private static void Legacy()
        {
            var json = JObject.FromObject(new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10 });
            json.Remove("Casts");
            var copy = json.ToObject<ReplayAttempt>()!;
            Check(copy.Casts.Count == 0 && ReplayValidation.IsValid(copy), "Legacy replay without casts remains readable with no invented observations");
        }
        private static RecordedCast ValidCast() => new() { Source = "Live", ActionId = 100, Occurrence = 1,
            ObservedTime = 1.1f, StartTime = 1, ExpectedEndTime = 4, CasterId = 42, CasterWorldPosition = new(100, 2, 90) };
        private static void Validation()
        {
            foreach (var corrupt in new Action<RecordedCast>[] {
                c => c.ObservedTime = float.NaN, c => c.StartTime = float.PositiveInfinity,
                c => c.ExpectedEndTime = float.NaN, c => c.CompletionTime = float.NaN,
                c => c.CasterWorldPosition = new(0, float.NaN, 0), c => c.TargetWorldPosition = new(float.PositiveInfinity, 0, 0),
                c => c.CasterHeading = float.NaN, c => c.TargetHeading = float.PositiveInfinity,
                c => c.StartTime = 3, c => c.CompletionTime = 0, c => c.ExpectedEndTime = 0,
                c => c.ObservedTime = 11, c => c.CompletionTime = 11, c => c.CasterId = 0,
                c => c.TargetId = 0, c => c.Source = "guessed", c => c.ActionId = 0,
                c => c.Occurrence = 0,
            })
            {
                var cast = ValidCast(); corrupt(cast);
                var attempt = new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10, Casts = new() { cast } };
                Check(!ReplayValidation.IsValid(attempt), "Malformed cast data must be rejected: " + corrupt.Method.Name);
            }
            var missing = new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Casts = null! };
            Check(!ReplayValidation.IsValid(missing), "Explicit null cast lists are malformed, unlike absent legacy fields");
            var lastBar = ValidCast(); lastBar.ExpectedEndTime = 30;
            Check(ReplayValidation.IsValid(new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10, Casts = new() { lastBar } }),
                "Expected bar end after wipe remains a prediction, not a completion");
        }
        private static void LiveAdapter()
        {
            var factory = typeof(RecordedCast).GetMethod("FromLive");
            Check(factory != null, "Live casts require one shared adapter");
            var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var context = new CastStartContext { ObservedTime = 2.1f, ObservedAtUtc = now, StartedAtUtc = now.AddSeconds(-.1),
                CasterId = 42, TargetId = 43, CasterWorldPosition = new(100, 2, 90), TargetWorldPosition = new(110, 3, 90),
                CasterHeading = 1.2f, TargetHeading = -2 };
            var cast = (RecordedCast)factory!.Invoke(null, new object?[] { 100u, 2, 2f, 3f, context })!;
            Check(cast.Source == "Live" && cast.StartTime == 2 && cast.ObservedTime == 2.1f && cast.ExpectedEndTime == 5 && cast.CompletionTime == null,
                "Observed snapshot, reconstructed start and predicted end retain separate meanings");
            Check(cast.CasterId == 42 && cast.TargetId == 43 && cast.TargetWorldPosition == new Vector3(110,3,90) &&
                cast.CasterHeading == 1.2f && cast.TargetHeading == -2 && cast.ObservedAtUtc == now,
                "Live adapter copies observed identity, geometry and timestamps");
            var absent = (RecordedCast)factory.Invoke(null, new object?[] { 100u, 1, 2f, 3f, null })!;
            Check(absent.CasterId == null && absent.TargetId == null && absent.CasterWorldPosition == null && absent.CasterHeading == null,
                "Older live events cannot invent absent actor context");
            var invalid = new CastStartContext { ObservedTime = 2.1f, CasterId = 0, TargetId = 0xE0000000,
                CasterWorldPosition = new(1, float.NaN, 3), TargetHeading = float.PositiveInfinity };
            var sanitized = (RecordedCast)factory.Invoke(null, new object?[] { 100u, 1, 2f, 3f, invalid })!;
            Check(sanitized.CasterId == null && sanitized.TargetId == null && sanitized.CasterWorldPosition == null && sanitized.TargetHeading == null,
                "Invalid live geometry and no-target sentinels remain unknown");
        }
        private static void PrePullPrediction()
        {
            var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var context = new CastStartContext { ObservedTime = .1f, ObservedAtUtc = now,
                StartedAtUtc = now.AddSeconds(-.25) };
            var cast = RecordedCast.FromLive(100, 1, 0, 3, context);
            Check(cast.ExpectedEndTime is { } end && Math.Abs(end - 2.85f) < .00001f,
                "A bar already underway at pull start ends after its observed remaining time, not a fresh full duration");
            Check(cast.StartTime == 0 && cast.ObservedTime == .1f && cast.CompletionTime == null && cast.IsValid(10),
                "Correcting the bar prediction preserves the clamped start and unknown completion");
            var legacy = RecordedCast.FromLive(100, 1, 2, 3);
            var partial = RecordedCast.FromLive(100, 1, 2, 3, new CastStartContext { ObservedTime = 2.1f });
            Check(legacy.ExpectedEndTime == 5 && partial.ExpectedEndTime == 5,
                "Events without absolute timing context keep their existing bar prediction");
            Check(RecordedCast.FromLive(100, 1, 0, float.NaN, context).ExpectedEndTime == null,
                "Unknown bar duration cannot acquire a prediction from actor context");
        }
        private static void BoundedCopy()
        {
            var method = typeof(ReplayBuffer).GetMethod("AddCast");
            Check(method != null, "Recorder must accept bounded cast evidence");
            var buffer = new ReplayBuffer(PlanDocument.CreateDefault(), -1, DateTime.UtcNow);
            buffer.TryAdd(new ReplayFrame { Time = 0 });
            var cast = ValidCast(); method!.Invoke(buffer, new object[] { cast });
            cast.CasterId = 99;
            Check(buffer.Attempt.Casts.Single().CasterId == 42, "Recorder copies incoming evidence so callers cannot rewrite history");
            var invalid = ValidCast(); invalid.CasterHeading = float.NaN;
            method.Invoke(buffer, new object[] { invalid });
            Check(buffer.Attempt.Casts.Count == 1, "Recorder rejects invalid evidence before it poisons replay validation");
            Check(!buffer.Attempt.Evidence.Complete, "Rejected live observations must mark partial evidence");
            for (var i = 0; i < 1100; i++) method.Invoke(buffer, new object[] { ValidCast() });
            Check(buffer.Attempt.Casts.Count <= 1000, "Live cast retention is bounded independently of mechanic matches");
            Check(!ReplayValidation.IsValid(new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10,
                Casts = Enumerable.Range(0, 1001).Select(_ => ValidCast()).ToList() }), "Loaded cast collections enforce the same cap");
            buffer.Finish("Wipe", 1);
            Check(buffer.Attempt.Casts.Count == 0, "Observations sampled after the final pull boundary are removed");
            method.Invoke(buffer, new object[] { ValidCast() });
            Check(buffer.Attempt.Casts.Count == 0, "Finished recordings cannot receive later casts");
        }
        private static void LogAdapter()
        {
            var data = new LogFightData { Fight = new LogFight { Id = 1, EndTime = 10000 } };
            data.EnemyCasts.Add(new LogCast { SourceId = 7, AbilityId = 100, IsCastStart = true, TimeSeconds = 1 });
            data.EnemyCasts.Add(JsonConvert.DeserializeObject<LogCast>("""
                {"SourceId":7,"SourceInstance":2,"TargetId":9,"TargetInstance":1,"CompletionSourceInstance":2,"CompletionTargetId":10,"CompletionTargetInstance":1,"AbilityId":100,"IsCastStart":true,"TimeSeconds":2,"CastSeconds":3,"CompletionTimeSeconds":5}
                """)!);
            data.EnemyCasts.Add(JsonConvert.DeserializeObject<LogCast>("""
                {"SourceId":7,"TargetId":9,"AbilityId":200,"TimeSeconds":6,"CompletionTimeSeconds":6}
                """)!);
            var attempt = LogReplayBuilder.Build(PlanDocument.CreateDefault(), data, new LogEvidence(), _ => true, _ => 0);
            Check(attempt.Casts.Count == 3, "Log replay retains unpaired starts, paired casts and completion-only observations");
            var first = attempt.Casts[0]; var paired = attempt.Casts[1]; var instant = attempt.Casts[2];
            Check(first.Source == "FF Logs" && first.StartTime == 1 && first.ExpectedEndTime == null && first.CompletionTime == null,
                "Unpaired log starts have neither predicted nor observed end");
            Check(paired.Source == "FF Logs" && paired.CasterId == 7 && paired.TargetId == 9 && paired.StartTime == 2 &&
                paired.CompletionTime == 5 && paired.ExpectedEndTime == null && paired.Occurrence == 2,
                "Explicit log completion stays separate from bar prediction");
            Check(instant.StartTime == null && instant.CompletionTime == 6 && instant.Occurrence == null,
                "Completion-only events do not invent a cast start or bar occurrence");
            var stored = JObject.FromObject(paired);
            Check(stored.Value<int?>("CasterInstance") == 2 && stored.Value<int?>("TargetInstance") == 1 &&
                stored.Value<int?>("CompletionCasterInstance") == 2 && stored.Value<int?>("CompletionTargetId") == 10 &&
                stored.Value<int?>("CompletionTargetInstance") == 1, "Replay preserves independent cast-start and completion identities.");
            var restored = JsonConvert.DeserializeObject<RecordedCast>(JsonConvert.SerializeObject(paired, PlanJson.Compact()), PlanJson.Compact())!;
            Check(restored.IsValid(10) && JObject.FromObject(restored).Value<int?>("CompletionTargetId") == 10,
                "Independent completion identity survives compact serialization.");
            foreach (var change in new Action<JObject>[] {
                c => c["CasterInstance"] = 0, c => c["TargetInstance"] = -1,
                c => c["CompletionCasterInstance"] = 0, c => c["CompletionTargetInstance"] = 0,
                c => c["CompletionTargetId"] = 0, c => c["TargetId"] = null,
                c => c["CasterId"] = null, c => c["CompletionTargetId"] = null, c => c["CompletionTime"] = null })
            {
                var corrupt = (JObject)stored.DeepClone(); change(corrupt);
                Check(!corrupt.ToObject<RecordedCast>()!.IsValid(10), "Invalid or orphaned cast instances/completion targets are rejected.");
            }
            Check(attempt.Casts.All(c => c.CasterWorldPosition == null && c.TargetWorldPosition == null && c.CasterHeading == null &&
                c.TargetHeading == null && c.StartedAtUtc == null && c.ObservedAtUtc == null), "Logs do not invent world geometry or absolute event times");
            Check(ReplayValidation.IsValid(attempt), "Both live and log records use the same validated replay representation");
            data.EnemyCasts.Add(new LogCast { SourceId = 7, AbilityId = 300, IsCastStart = true,
                TimeSeconds = 7, CastSeconds = 1, CompletionTimeSeconds = -1 });
            var partial = LogReplayBuilder.Build(PlanDocument.CreateDefault(), data, new LogEvidence(), _ => true, _ => 0);
            Check(!partial.Evidence.Complete && partial.Evidence.Warnings.Any(w => w.Contains("cast", StringComparison.OrdinalIgnoreCase)),
                "Omitted invalid cast context must report incomplete evidence");
        }
    }
}
