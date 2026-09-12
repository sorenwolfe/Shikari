using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;

namespace Shikari.Services
{
    public static class CallTemplate { public static string FormatTime(float seconds) => seconds.ToString("0.0"); }
}

namespace Shikari.Tests
{
    public static class ObservedResolutionTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static ReplayAttempt Copy(ReplayAttempt attempt) => JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt))!;
        public static void Run(string path)
        {
            var fixture = JObject.Parse(File.ReadAllText(path));
            Check(fixture.Value<int>("SchemaVersion") == 1 && fixture["Provenance"]!.Value<int>("IndependentPullCount") == 1,
                "The fixture represents one independent observed pull, not eight independent successes.");
            var events = (JArray)fixture["Events"]!;
            var expected = (JArray)fixture["ExpectedTowerAssignments"]!;
            var fight = new LogFight { Id = 2, EncounterId = 104, Name = "Lindwurm", StartTime = 0, EndTime = 425672, Kill = true };
            var parser = new LogEvidenceParser(fight);
            parser.AddPage(events);
            Check(parser.Result.EffectsComplete && parser.Result.Effects.Count == 32,
                "Production parser should preserve all 16 calculated and 16 later damage observations from this slice.");
            var jobs = new[] { "Gunbreaker", "Samurai", "Warrior", "RedMage", "Scholar", "Dancer", "WhiteMage", "Dragoon" };
            var data = new LogFightData { ReportCode = "yaP6A3hwN7b8KnQJ", Fight = fight,
                Actors = jobs.Select((job, i) => new LogActor { Id = i + 1, Name = $"Player {i + 1}", Job = job, Type = "Player" }).ToList() };
            var plan = new PlanDocument();
            var pull = LogReplayBuilder.Build(plan, data, parser.Result, _ => false, _ => 0);
            Check(pull.Evidence.EffectsComplete && pull.Evidence.Effects.Count == 32 && pull.Evidence.Actors.Count == 8,
                "Production replay builder must keep typed effect identity and independently mapped player actors.");
            var serialized = JsonConvert.SerializeObject(pull);
            Check(pull.Evidence.References.Count == 0, "This observation never invents board landmarks or arena orientation.");
            var towers = pull.Evidence.Effects.Select((effect, index) => (effect, index))
                .Where(row => row.effect.Type == "calculateddamage" && row.effect.ActionId is 46259 or 46263).ToArray();
            Check(towers.Length == 8 && expected.Count == 8, "All eight observed tower effects must be retained.");
            foreach (var literal in expected)
            {
                var action = literal.Value<uint>("Action");
                var occurrence = literal.Value<int>("Occurrence");
                var row = towers.Where(t => t.effect.ActionId == action).OrderBy(t => t.effect.Time).ElementAt(occurrence - 1);
                Check(row.effect.TargetId == literal.Value<long>("Actor"), "Observed tower target differs from independently reviewed number/Bonds assignment.");
                var result = TowerEffectObservation.Evaluate(pull, row.index);
                Check(result.Available && result.Inside == true && result.RadiusYalms == 3 && result.DistanceYalms is >= .269f and <= 2.726f,
                    "Observed same-event tower-to-player distance should lie inside the independently declared three-yalm radius.");
                Check(result.Note.Contains("board", StringComparison.OrdinalIgnoreCase) && !result.Note.Contains("success", StringComparison.OrdinalIgnoreCase),
                    "The output must describe observational proximity without asserting assignment success or board correspondence.");
                var later = pull.Evidence.Effects.Single(e => e.Type == "damage" && e.PacketId == row.effect.PacketId && e.TargetId == row.effect.TargetId);
                Check(later.Time - row.effect.Time is >= .487f and <= .492f,
                    "Retain the observed 488–491ms separation of calculated snapshot and actual damage event.");
                Check(!TowerEffectObservation.Evaluate(pull, pull.Evidence.Effects.IndexOf(later)).Available,
                    "Later damage must not silently substitute for a reviewed calculated snapshot.");
            }
            Check(JsonConvert.SerializeObject(pull) == serialized, "Evaluation must leave recorded observations and authored boards unchanged.");
            var first = towers[0].index;
            void Reject(Action<ReplayAttempt> change, string reason)
            {
                var copy = Copy(pull); change(copy);
                var result = TowerEffectObservation.Evaluate(copy, first);
                Check(!result.Available && result.Inside == null && result.DistanceYalms == null, reason);
            }
            Reject(p => p.Evidence.Source = "Local recording", "Local predicted casts cannot become imported action effects.");
            Reject(p => p.Evidence.EncounterId = 105, "A different encounter cannot use the Act 2 tower profile.");
            Reject(p => p.Evidence.EffectsComplete = false, "Missing effect history must remain unknown.");
            Reject(p => p.Evidence.Effects[first].ActionId = 46260, "Chain helper positions are not valid circle centers.");
            Reject(p => p.Evidence.Effects[first].ActionId = 48830, "The assignment cast is not a tower effect.");
            Reject(p => p.Evidence.Effects[first].SourceInstance = null, "Different NPC instances cannot be treated as one known tower source.");
            Reject(p => p.Evidence.Effects[first].SourcePosition = null, "Absent source position must stay unknown.");
            Reject(p => p.Evidence.Effects[first].TargetPosition = null, "Absent target position must stay unknown.");
            Reject(p => p.Evidence.Effects[first].TargetPosition = new Vector2(float.NaN, 0), "Non-finite positions cannot yield a distance.");
            Reject(p => p.Evidence.Effects[first].Time = p.Duration + 1, "Out-of-recording observations cannot be checked.");
            Reject(p => p.Evidence.Effects[first].Time = float.NaN, "Invalid observation times cannot be checked.");
            Reject(p => p.Evidence.Actors.RemoveAll(a => a.Id == p.Evidence.Effects[first].TargetId), "Unknown targets cannot become player observations.");
            Reject(p => p.Evidence.Actors.Add(p.Evidence.Actors.Single(a => a.Id == p.Evidence.Effects[first].TargetId)), "Ambiguous target identity must remain unknown.");
            Reject(p =>
            {
                var conflict = JsonConvert.DeserializeObject<EvidenceEffect>(JsonConvert.SerializeObject(p.Evidence.Effects[first]))!;
                conflict.TargetPosition += new Vector2(400, 0);
                p.Evidence.Effects.Add(conflict);
            }, "Contradictory geometry for the same packet and target cannot establish one proximity result.");
            Check(!TowerEffectObservation.Evaluate(pull, -1).Available && !TowerEffectObservation.Evaluate(pull, pull.Evidence.Effects.Count).Available,
                "Invalid selection indices must remain unknown.");
            var outside = Copy(pull);
            outside.Evidence.Effects[first].TargetPosition = outside.Evidence.Effects[first].SourcePosition!.Value + new Vector2(400, 0);
            var observedOutside = TowerEffectObservation.Evaluate(outside, first);
            Check(observedOutside.Available && observedOutside.Inside == false && observedOutside.DistanceYalms == 4,
                "An explicitly synthetic outside-radius observation should report measured proximity, not pass every position.");
            var missingOther = Copy(pull); missingOther.Evidence.Positions.Clear();
            Check(TowerEffectObservation.Evaluate(missingOther, first).Available,
                "A missing unrelated actor sample must not obscure geometry attached to this exact effect.");
            var zeroPacket = Copy(pull); zeroPacket.Evidence.Effects[first].PacketId = 0;
            Check(TowerEffectObservation.Evaluate(zeroPacket, first).Available,
                "An explicit packet ID zero is valid observed identity, consistent with parser and replay persistence.");
            Console.WriteLine("Observed resolution checks passed: eight independently reviewed tower targets, typed timing, same-event proximity and unknown guards.");
        }
    }
}
