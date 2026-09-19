using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;

namespace Shikari.Services { public static class CallTemplate { public static string FormatTime(float value) => value.ToString("0.0"); } }
namespace Shikari.Tests
{
    public static class EffectEvidenceTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static void Run()
        {
            var parser = new LogEvidenceParser(new LogFight { StartTime = 10000, EndTime = 20000 });
            parser.AddPage(JArray.Parse("""
                [{"timestamp":12000,"type":"calculateddamage","sourceID":99,"targetID":1,"abilityGameID":123,"sourceInstance":2,"packetID":17,"sourceResources":{"x":9000,"y":11000},"targetResources":{"x":10000,"y":10001}},
                 {"timestamp":12200,"type":"damage","sourceID":99,"targetID":1,"abilityGameID":123,"sourceInstance":2,"packetID":17,"targetResources":{"x":10100,"y":10101}}]
                """));
            var json = JObject.FromObject(parser.Result);
            Check(json["Effects"] is JArray effects && effects.Count == 2,
                "Calculated damage and later damage must survive parsing as distinct typed effect observations");
            Check(parser.Result.EffectsComplete && parser.Result.Effects[0].Time == 2 && parser.Result.Effects[1].Time == 2.2f &&
                parser.Result.Effects[0].Type == "calculateddamage" && parser.Result.Effects[1].Type == "damage" &&
                parser.Result.Effects[0].TargetPosition == new Vector2(10000, 10001) &&
                parser.Result.Effects[1].TargetPosition == new Vector2(10100, 10101),
                "Snapshot and delayed damage positions must remain attached to their own timestamps");
            ParserIdentityAndMissingValues();
            ParserMalformedDataAndBounds();
            ScopedEffectTargets();
            BuilderAndSerialization();
            ScopedReplay();
            Validation();
            Console.WriteLine("PASS: typed damage identity, source instances, snapshot positions, channel bounds, replay import and backward serialization");
        }

        private static LogFight Fight() => new() { Id = 1, StartTime = 10000, EndTime = 20000 };
        private static JObject Effect(long timestamp = 12000, long target = 1, long source = 99) => new()
        {
            ["timestamp"] = timestamp, ["type"] = "calculateddamage", ["sourceID"] = source,
            ["targetID"] = target, ["abilityGameID"] = 123,
        };
        private static LogFightData Data()
        {
            var data = new LogFightData { Fight = Fight(), ReportCode = "Fixture" };
            data.Actors.AddRange(new[] { new LogActor { Id = 1, Type = "Player", Name = "One" },
                new LogActor { Id = 2, Type = "Player", Name = "Unused report player" },
                new LogActor { Id = 99, Type = "NPC", Name = "Boss" } });
            data.PlayerCasts.Add(new LogCast { SourceId = 1, AbilityId = 456, TimeSeconds = 1 });
            return data;
        }

        private static void ParserIdentityAndMissingValues()
        {
            var parser = new LogEvidenceParser(Fight(), new System.Collections.Generic.Dictionary<uint, string> { [123] = "Resolution" });
            var first = Effect(); first["sourceInstance"] = 2; first["targetInstance"] = 1; first["packetID"] = long.MaxValue;
            var second = (JObject)first.DeepClone(); second["sourceInstance"] = 3;
            var third = (JObject)first.DeepClone(); third["packetID"] = long.MaxValue - 1;
            parser.AddPage(new JArray(first, second, third, first.DeepClone(), Effect()));
            Check(parser.Result.Effects.Count == 5 && parser.Result.Effects[0].SourceInstance == 2 &&
                parser.Result.Effects[1].SourceInstance == 3 && parser.Result.Effects[0].PacketId == long.MaxValue &&
                parser.Result.Effects[2].PacketId == long.MaxValue - 1,
                "Duplicate-looking events must retain source instances and exact Int64 packet identity without merging");
            var missing = parser.Result.Effects.Last();
            Check(missing.SourceInstance == null && missing.TargetInstance == null && missing.PacketId == null &&
                missing.SourcePosition == null && missing.TargetPosition == null && missing.Name == "Resolution",
                "Missing optional evidence stays unknown and names use ability metadata");
            var zero = Effect(); zero["packetID"] = 0; zero["targetResources"] = new JObject { ["x"] = 0, ["y"] = 0 };
            parser.AddPage(new JArray(zero));
            Check(parser.Result.Effects.Last().PacketId == 0 && parser.Result.Effects.Last().TargetPosition == Vector2.Zero,
                "Explicit zero packet and coordinates remain observations");
            parser.AddPage(new JArray(new JObject { ["timestamp"] = 12000, ["type"] = "begincast", ["abilityGameID"] = 123 },
                new JObject { ["timestamp"] = 12000, ["type"] = "heal", ["abilityGameID"] = 123 }, Effect(9000), Effect(21000)));
            Check(parser.Result.Effects.Count == 6 && parser.Result.EffectsComplete, "Casts, healing and out-of-fight events never become resolution evidence");
        }

        private static void ParserMalformedDataAndBounds()
        {
            var parser = new LogEvidenceParser(Fight());
            foreach (var field in new[] { "sourceID", "targetID", "abilityGameID" })
                foreach (var value in new JToken[] { -1, 0, 1.5, "2", JValue.CreateNull(), (JToken)decimal.MaxValue })
                {
                    var malformed = Effect(); malformed[field] = value; parser.AddPage(new JArray(malformed));
                }
            Check(parser.Result.Effects.Count == 0 && !parser.Result.EffectsComplete && parser.Result.Complete,
                "Invalid required effect identity omits the event without invalidating independently usable status observations");
            var optional = Effect(); optional["sourceInstance"] = 0; optional["targetInstance"] = -1;
            optional["packetID"] = -1; optional["targetResources"] = new JObject { ["x"] = 1 };
            optional["sourceResources"] = new JObject { ["x"] = double.NaN, ["y"] = 3 };
            parser.AddPage(new JArray(optional));
            var effect = parser.Result.Effects.Single();
            Check(effect.SourceInstance == null && effect.TargetInstance == null && effect.PacketId == null &&
                effect.SourcePosition == null && effect.TargetPosition == null, "Malformed optional fields remain unknown");
            parser = new LogEvidenceParser(Fight());
            parser.AddPage(new JArray(Enumerable.Range(0, ReplayEvidence.MaxEffects + 1).Select(_ => Effect())));
            Check(parser.Result.Effects.Count == ReplayEvidence.MaxEffects && !parser.Result.EffectsComplete &&
                parser.Result.Complete && parser.Result.Warnings.Count == 1, "Effect cap is bounded and independent of the status channel");
            var status = new JObject { ["timestamp"] = 13000, ["type"] = "applybuff", ["targetID"] = 1, ["abilityGameID"] = 1000010 };
            parser.AddPage(new JArray(status));
            Check(parser.Result.StatusEvents.Count == 1, "Status ingestion continues after the optional effect cap");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var canceled = false;
            try { parser.AddPage(new JArray(Effect()), cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && parser.Result.Effects.Count == ReplayEvidence.MaxEffects, "Canceled pages stop before mutation");
            parser = new LogEvidenceParser(Fight()); parser.Warn("Partial event stream", true);
            Check(!parser.Result.EffectsComplete, "An interrupted whole stream cannot claim a complete effect channel");
            parser = new LogEvidenceParser(Fight()); var invalidTimestamp = Effect(); invalidTimestamp["timestamp"] = "unknown";
            parser.AddPage(new JArray(invalidTimestamp));
            Check(!parser.Result.EffectsComplete, "An unreadable event timestamp cannot establish effect completeness");
        }

        private static LogEvidenceParser ScopedParser(IReadOnlySet<int>? targets)
        {
            var constructor = typeof(LogEvidenceParser).GetConstructor(new[] { typeof(LogFight),
                typeof(IReadOnlyDictionary<uint, string>), typeof(IReadOnlySet<int>) });
            return constructor == null ? new LogEvidenceParser(Fight()) :
                (LogEvidenceParser)constructor.Invoke(new object?[] { Fight(), null, targets });
        }

        private static void ScopedEffectTargets()
        {
            var targets = new HashSet<int> { 2, 1 };
            var parser = ScopedParser(targets);
            targets.Clear(); targets.Add(99);
            parser.AddPage(new JArray(Enumerable.Range(0, ReplayEvidence.MaxEffects + 5).Select(i => Effect(target: i % 2 == 0 ? 99 : 500, source: 1))));
            var outgoing = Effect(target: 99, source: 1);
            outgoing["sourceResources"] = new JObject { ["x"] = 0, ["y"] = 42 };
            outgoing["abilityGameID"] = "bad ability"; outgoing["packetID"] = -1; outgoing["sourceInstance"] = 0;
            outgoing["targetResources"] = new JObject { ["x"] = "not a coordinate" };
            var untimed = (JObject)outgoing.DeepClone(); untimed["timestamp"] = "unknown";
            parser.AddPage(new JArray(outgoing, untimed, Effect(target: 1), Effect(target: 2)));
            Check(parser.Result.Effects.Count == 2 && parser.Result.Effects.All(e => e.TargetId is 1 or 2) &&
                parser.Result.EffectsComplete && parser.Result.Complete,
                "Outgoing, pet and NPC-target damage must not exhaust the player-target cap or poison scoped completeness.");
            Check(parser.Result.Positions.Any(p => p.ActorId == 1 && p.X == 0 && p.Y == 42),
                "Ignoring outgoing effects must preserve their useful player position resources.");
            Check(parser.Result.Warnings.Count == 1 && parser.Result.Warnings[0].Contains("position", StringComparison.OrdinalIgnoreCase),
                "Untimed irrelevant damage must explain omitted positions without failing the independent player-effect channel.");
            var provenance = JObject.FromObject(parser.Result)["EffectTargetActorIds"] as JArray;
            Check(provenance != null && provenance.Select(t => t.Value<int>()).SequenceEqual(new[] { 1, 2 }),
                "The authoritative target scope must survive as a detached, deterministic evidence snapshot.");

            parser = ScopedParser(new HashSet<int> { 1 });
            parser.AddPage(new JArray(Enumerable.Range(0, ReplayEvidence.MaxEffects).Select(_ => Effect())));
            parser.AddPage(new JArray(Effect(target: 99)));
            Check(parser.Result.EffectsComplete, "An irrelevant event after the exact cap does not imply relevant observations were omitted.");
            parser.AddPage(new JArray(Effect()));
            Check(!parser.Result.EffectsComplete && parser.Result.Complete && parser.Result.Effects.Count == ReplayEvidence.MaxEffects,
                "Relevant overflow remains bounded and partial without damaging status completeness.");

            foreach (var invalid in new JToken[] { 0, -1, 1.5, "99", JValue.CreateNull(), (long)int.MaxValue + 1 })
            {
                parser = ScopedParser(new HashSet<int> { 1 });
                var row = Effect(); row["targetID"] = invalid; parser.AddPage(new JArray(row));
                Check(parser.Result.Effects.Count == 0 && !parser.Result.EffectsComplete,
                    "An invalid target cannot be silently classified outside the authoritative player scope.");
            }
            parser = ScopedParser(new HashSet<int>());
            parser.AddPage(new JArray(Effect(), Effect(target: 99)));
            Check(parser.Result.Effects.Count == 0 && parser.Result.EffectsComplete &&
                JObject.FromObject(parser.Result)["EffectTargetActorIds"] is JArray { Count: 0 },
                "An authoritative empty target set must capture no effects and never infer players from damage.");
            var missing = Effect(); missing.Remove("targetID"); parser.AddPage(new JArray(missing));
            Check(!parser.Result.EffectsComplete, "Missing target identity remains an unresolved omission even with an empty scope.");
            parser = ScopedParser(null); parser.AddPage(new JArray(Effect(target: 99)));
            Check(parser.Result.Effects.Count == 1 && JObject.FromObject(parser.Result)["EffectTargetActorIds"]?.Type is null or JTokenType.Null,
                "A null scope preserves the legacy unfiltered parser and must not claim authoritative target provenance.");
        }

        private static void BuilderAndSerialization()
        {
            var plan = PlanDocument.CreateDefault();
            var parser = new LogEvidenceParser(Fight());
            var first = Effect(); first["sourceInstance"] = 2; first["packetID"] = 0;
            first["sourceResources"] = new JObject { ["x"] = 9000, ["y"] = 11000 };
            first["targetResources"] = new JObject { ["x"] = 10000, ["y"] = 10001 };
            parser.AddPage(new JArray(first, Effect(target: 2), Effect(target: 99), Effect(target: 888), Effect(timestamp: 12500)));
            var attempt = LogReplayBuilder.Build(plan, Data(), parser.Result, _ => true, _ => 0);
            Check(attempt.Evidence.Effects.Count == 2 && attempt.Evidence.Actors.Count == 1 &&
                attempt.Evidence.Effects.All(e => e.TargetId == 1) && attempt.Evidence.Effects[0].SourceId == 99 &&
                attempt.Evidence.Effects[0].SourceInstance == 2 && attempt.Evidence.Effects[0].TargetPosition == new Vector2(10000, 10001),
                "Replay retains known participant targets and NPC identities without inventing party members from damage targets");
            Check(attempt.Evidence.EffectsComplete && ReplayValidation.IsValid(attempt), "Imported typed effects validate independently of live alignment");
            foreach (var settings in new[] { PlanJson.Compact(), PlanJson.Readable() })
            {
                var json = JsonConvert.SerializeObject(attempt, settings);
                var restored = JsonConvert.DeserializeObject<ReplayAttempt>(json, settings)!;
                Check(ReplayValidation.IsValid(restored) && restored.Evidence.EffectsComplete &&
                    restored.Evidence.Effects[0].PacketId == 0 && restored.Evidence.Effects[1].PacketId == null &&
                    restored.Evidence.Effects[1].TargetPosition == null && restored.Evidence.Effects[0].SourcePosition == new Vector2(9000, 11000),
                    "Typed effect snapshots and unknowns survive replay storage serialization");
                var legacy = JObject.Parse(json); ((JObject)legacy["Evidence"]!).Remove("Effects"); ((JObject)legacy["Evidence"]!).Remove("EffectsComplete");
                restored = JsonConvert.DeserializeObject<ReplayAttempt>(legacy.ToString(), settings)!;
                Check(ReplayValidation.IsValid(restored) && !restored.Evidence.EffectsComplete && restored.Evidence.Effects.Count == 0,
                    "Legacy recordings remain valid with an explicitly unavailable effect channel");
            }
            parser.Result.Complete = false;
            Check(!LogReplayBuilder.Build(plan, Data(), parser.Result, _ => true, _ => 0).Evidence.EffectsComplete,
                "An incomplete source stream never becomes complete effect evidence");
            Check(!LogReplayBuilder.Build(plan, Data(), new LogEvidence(), _ => true, _ => 0).Evidence.EffectsComplete,
                "Legacy/manual sources do not claim typed-effect coverage");
            var oversized = new LogEvidence { EffectsComplete = true };
            oversized.Effects.AddRange(Enumerable.Range(0, ReplayEvidence.MaxEffects + 1).Select(_ => new LogEffectEvent
                { Time = 2, ActionId = 123, Type = "calculateddamage", SourceId = 99, TargetId = 1 }));
            attempt = LogReplayBuilder.Build(plan, Data(), oversized, _ => true, _ => 0);
            Check(attempt.Evidence.Effects.Count == ReplayEvidence.MaxEffects && !attempt.Evidence.EffectsComplete && attempt.Evidence.Complete,
                "Replay builder independently bounds manually supplied effects without spoiling status completeness");
        }

        private static void ScopedReplay()
        {
            var parser = ScopedParser(new HashSet<int> { 1, 2 });
            parser.AddPage(new JArray(Effect(target: 2)));
            var plan = PlanDocument.CreateDefault();
            var attempt = LogReplayBuilder.Build(plan, Data(), parser.Result, _ => true, _ => 0);
            Check(attempt.Evidence.Actors.Count == 2 && attempt.Evidence.Effects.Count == 1 && attempt.Evidence.EffectsComplete,
                "Verified fight participants include damage-only players without relying on their outgoing casts or positions.");
            var restored = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt, PlanJson.Compact()), PlanJson.Compact())!;
            Check(ReplayValidation.IsValid(restored) && JObject.FromObject(restored.Evidence)["EffectTargetActorIds"] is JArray { Count: 2 },
                "Player-target scope must survive compact replay storage.");
            var data = Data(); data.Actors[1] = new LogActor { Id = 2, Type = "NPC" };
            var partial = LogReplayBuilder.Build(plan, data, parser.Result, _ => true, _ => 0);
            Check(!partial.Evidence.EffectsComplete && partial.Evidence.Effects.Count == 0 && partial.Evidence.Actors.Count == 1,
                "Conflicting actor metadata cannot silently remove a scoped target while claiming complete effects.");
            data = Data(); data.Actors.Add(data.Actors[1]);
            partial = LogReplayBuilder.Build(plan, data, parser.Result, _ => true, _ => 0);
            Check(!partial.Evidence.EffectsComplete && ReplayValidation.IsValid(partial), "Duplicate actor metadata must remain partial and loadable.");
            foreach (var scope in new[] { new[] { 0 }, new[] { 1, 1 }, new[] { 99 } })
            {
                var json = JObject.FromObject(attempt); json["Evidence"]!["EffectTargetActorIds"] = new JArray(scope);
                Check(!ReplayValidation.IsValid(json.ToObject<ReplayAttempt>()!), "Malformed or inconsistent stored scope is rejected.");
            }
        }

        private static void Validation()
        {
            ReplayAttempt Fresh()
            {
                var attempt = new ReplayBuffer(PlanDocument.CreateDefault(), -1, DateTime.UtcNow).Attempt;
                attempt.Duration = 10;
                attempt.Evidence.Source = "FF Logs";
                attempt.Evidence.Actors.Add(new EvidenceActor { Id = 1 });
                attempt.Evidence.Effects.Add(new EvidenceEffect { Time = 2, ActionId = 123, Type = "calculateddamage", SourceId = 99, TargetId = 1 });
                return attempt;
            }
            Check(ReplayValidation.IsValid(Fresh()), "Optional unknown fields are valid");
            foreach (var change in new Action<EvidenceEffect>[] {
                e => e.Time = float.NaN, e => e.Time = -1, e => e.Time = 11, e => e.ActionId = 0,
                e => e.SourceId = 0, e => e.TargetId = -1, e => e.TargetId = 88, e => e.Type = "cast",
                e => e.SourceInstance = 0, e => e.TargetInstance = -1, e => e.PacketId = -1,
                e => e.SourcePosition = new Vector2(float.PositiveInfinity, 0),
                e => e.TargetPosition = new Vector2(0, float.NaN), e => e.Name = null!, e => e.Name = new string('x', 257) })
            {
                var attempt = Fresh(); change(attempt.Evidence.Effects[0]);
                Check(!ReplayValidation.IsValid(attempt), "Malformed stored effect evidence must be rejected");
            }
            var invalid = Fresh(); invalid.Evidence.Effects = null!;
            Check(!ReplayValidation.IsValid(invalid), "Null effect collection rejected");
            invalid = Fresh(); invalid.Evidence.Effects[0] = null!;
            Check(!ReplayValidation.IsValid(invalid), "Null effect entry rejected");
            invalid = Fresh(); invalid.Evidence.Effects.AddRange(Enumerable.Repeat(invalid.Evidence.Effects[0], ReplayEvidence.MaxEffects));
            Check(!ReplayValidation.IsValid(invalid), "Stored effect cap enforced");
        }
    }
}
