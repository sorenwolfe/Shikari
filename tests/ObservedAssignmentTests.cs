using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;

namespace Shikari.Services
{
    // Only presentation formatting is replaced; import, timeline, decoder and engine are production sources.
    public static class CallTemplate { public static string FormatTime(float seconds) => seconds.ToString("0.0"); }
}

namespace Shikari.Tests
{
    public static class ObservedAssignmentTests
    {
        // Synthetic evaluator scope only. FF Logs gameZone 1327 is not treated as a verified Dalamud TerritoryType mapping.
        private const uint Territory = 1;
        private static readonly HashSet<uint> CorroboratedStatuses = new() { 3004, 3005, 3006, 3451, 4752, 4754 };
        private sealed class Expected
        {
            public int ActorId { get; set; }
            public int Number { get; set; }
            public string Letter { get; set; } = "";
            public string Label { get; set; } = "";
        }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        public static void Run(string path)
        {
            var fixture = JObject.Parse(File.ReadAllText(path));
            Check(fixture.Value<int>("SchemaVersion") == 1 && fixture["Provenance"]?.Value<int>("IndependentPullCount") == 1,
                "Observed fixture must retain its single-pull provenance.");
            var data = fixture["Data"]!.ToObject<LogFightData>()!;
            var source = fixture["Evidence"]!.ToObject<LogEvidence>()!;
            var expected = fixture["Expected"]!.ToObject<List<Expected>>()!;
            Check(data.ReportCode == "yaP6A3hwN7b8KnQJ" && data.Fight.Id == 2 && data.Fight.EncounterId == 104 && data.Fight.Kill,
                "This source is the observed P1 kill, not the requested P2 fight 9.");
            Check(data.Actors.Select(a => a.Id).Order().SequenceEqual(Enumerable.Range(1, 8)) &&
                data.Actors.All(a => a.Name == $"Player {a.Id}") && expected.Count == 8,
                "Party identities must be replaced by fixture-local actors 1 through 8.");
            Check(source.Complete && source.StatusEvents.Count == 32 && source.Positions.Count == 0 &&
                source.StatusEvents.All(s => s.SourceId == -1 && s.Parameter == null),
                "The bounded observed slice retains unknown status sources/parameters and no invented positions.");
            var anchor = data.EnemyCasts.Single();
            Check(anchor.AbilityId == 48830 && Math.Abs(anchor.TimeSeconds - 117.258f) < .001f &&
                Math.Abs(anchor.CompletionTimeSeconds!.Value - 120.244f) < .001f,
                "Preserve the actual cast start and separately observed completion.");
            Check(source.StatusEvents.Where(s => s.Change == LogStatusChange.Apply).All(s => Math.Abs(s.Time - 121.403f) < .001f),
                "Status availability must retain its measured 4.145-second delay from the cast start.");
            var serializedSource = JsonConvert.SerializeObject(source);
            var serializedData = JsonConvert.SerializeObject(data);

            foreach (var row in expected)
            {
                var plan = TestPlan(6);
                var pull = Build(plan, data, source);
                Check(pull.Evidence.Actors.Count == 8 && pull.Casts.Single().ObservedTime == anchor.TimeSeconds &&
                    pull.Evidence.Statuses.All(s => CorroboratedStatuses.Contains(s.StatusId) && s.SourceId == -1 && s.Parameter == null),
                    "The actual importer must preserve scoped actor observations, cast availability and unknown fields.");
                var timeline = new EvidenceTimeline(pull.Evidence);
                var mechanic = pull.Mechanics.Single();
                var earlier = EncounterAssignmentDecoder.Decode(pull, mechanic, row.ActorId, 121.402f,
                    timeline.StatusesAt(row.ActorId, 121.402f));
                Check(earlier is { IsResolved: false }, "The passive decoder must not see assignment statuses before availability.");
                var decoded = EncounterAssignmentDecoder.Decode(pull, mechanic, row.ActorId, 121.404f,
                    timeline.StatusesAt(row.ActorId, 121.404f));
                Check(decoded is { IsResolved: true } && decoded.Number == row.Number && decoded.Letter == row.Letter &&
                    decoded.Label.EndsWith(row.Label, StringComparison.Ordinal),
                    $"Observed assignment differs from independently reviewed expectation for fixture actor {row.ActorId}.");
                var removedAt = source.StatusEvents.Where(s => s.TargetId == row.ActorId && s.Change == LogStatusChange.Remove).Max(s => s.Time);
                Check(EncounterAssignmentDecoder.Decode(pull, mechanic, row.ActorId, removedAt + .001f,
                    timeline.StatusesAt(row.ActorId, removedAt + .001f)) is { IsResolved: false },
                    "An observed removal must end the actor's active assignment label.");

                var result = PullValidationRunner.Run(plan, pull, row.ActorId, new(Territory));
                var decision = result.Decisions.Single();
                Check(result.Complete && result.ScopeVerified && decision.Time >= 121.403f && decision.Time < 123.258f &&
                    !decision.Conflict && !decision.Applied && plan.FindSlide(decision.SlideId)?.Title == row.Label,
                    $"Chronological production rules failed the observed pairing for fixture actor {row.ActorId}.");
                Check(plan.Slides.All(s => s.Items.Count == 0), "Label-only test boards must not claim a reviewed route.");

                var shortPlan = TestPlan(4);
                var shortResult = PullValidationRunner.Run(shortPlan, Build(shortPlan, data, source), row.ActorId, new(Territory));
                Check(!shortResult.Complete && shortResult.Decisions.All(d => d.SlideId == ""),
                    "A four-second window must miss the observed 4.145-second status delay, not use future evidence.");
            }

            var fieldsPlan = TestPlan(6);
            var fieldsPull = Build(fieldsPlan, data, source);
            foreach (var status in fieldsPull.Evidence.Statuses.Where(s => s.Change == "apply"))
            {
                // Deliberately fabricated stress values: logs cannot establish either live field.
                status.Duration = .01f;
                status.Parameter = 7;
            }
            var masked = PullValidationRunner.Run(fieldsPlan, fieldsPull, 1, new(Territory));
            Check(masked.Complete && fieldsPlan.FindSlide(masked.Decisions.Single().SlideId)?.Title == "I / Bonds A",
                "Imported duration/parameter fields cannot expire or alter the observed status-only pairing.");
            fieldsPlan.AdaptiveMechanics[0].Branches[0].Parameter = 7;
            var unknownParameter = PullValidationRunner.Run(fieldsPlan, fieldsPull, 1, new(Territory));
            Check(!unknownParameter.Complete && unknownParameter.Decisions.All(d => d.SlideId == ""),
                "A parameter-dependent rule must remain unknown despite a fabricated matching log parameter.");
            fieldsPlan.AdaptiveMechanics[0].Branches[0].Parameter = -1;
            fieldsPlan.AdaptiveMechanics[0].Branches[0].MaximumSeconds = 30;
            var unknownDuration = PullValidationRunner.Run(fieldsPlan, fieldsPull, 1, new(Territory));
            Check(!unknownDuration.Complete && unknownDuration.Decisions.All(d => d.SlideId == ""),
                "A duration-dependent rule must remain unknown despite a fabricated matching log duration.");
            var unverified = LogReplayBuilder.Build(TestPlan(6), data, source, _ => false, _ => 0);
            var unverifiedResult = PullValidationRunner.Run(unverified.Plan, unverified, 1, new(Territory));
            Check(!unverifiedResult.Complete && unverifiedResult.Decisions.All(d => d.SlideId == ""),
                "Raw aura IDs cannot replace canonical-status verification.");
            Check(serializedSource == JsonConvert.SerializeObject(source) && serializedData == JsonConvert.SerializeObject(data),
                "Importer and validation must leave the retained observed source unchanged.");
            var unscopedPlan = TestPlan(6);
            unscopedPlan.StrategyEvidence[0].EncounterVerified = false;
            var unscoped = PullValidationRunner.Run(unscopedPlan, Build(unscopedPlan, data, source), 1, new(Territory));
            Check(!unscoped.ScopeVerified && !unscoped.Complete && unscoped.Decisions.Single().SlideId != "",
                "Selecting a test territory may reproduce a candidate but cannot establish an unverified imported encounter scope.");
            Console.WriteLine("PASS: one observed M12S P1 pull, eight reviewed assignment identities, measured arrival/removal timing, four-second miss and unknown log fields; no route or mechanic-success claim");
        }

        private static ReplayAttempt Build(PlanDocument plan, LogFightData data, LogEvidence source) =>
            // Source-corroborated fixture whitelist only. Production uses the installed Status sheet.
            LogReplayBuilder.Build(plan, data, source, CorroboratedStatuses.Contains, _ => 0);

        private static PlanDocument TestPlan(float window)
        {
            // Deliberately test-only declaration: no live profile, real board geometry or default window is changed.
            var plan = PlanDocument.CreateDefault();
            plan.Slides.Clear();
            var rule = new AdaptiveMechanic { Id = "test-observed-act2", Label = "Test-only Act 2 identities", Enabled = true,
                TerritoryId = Territory, AnchorActionId = 48830, Occurrence = 1, WindowSeconds = window };
            var branches = new (uint NumberStatus, uint Bonds, string Label)[]
            {
                (3004, 4752, "I / Bonds A"), (3004, 4754, "I / Bonds B"),
                (3005, 4752, "II / Bonds A"), (3005, 4754, "II / Bonds B"),
                (3006, 4752, "III / Bonds A"), (3006, 4754, "III / Bonds B"),
                (3451, 4752, "IV / Bonds A"), (3451, 4754, "IV / Bonds B"),
            };
            foreach (var item in branches)
            {
                var slide = new Slide { Title = item.Label };
                plan.Slides.Add(slide);
                rule.Branches.Add(new StatusBranch { Label = item.Label, StatusId = item.NumberStatus,
                    MaximumSeconds = 3600, SlideId = slide.Id,
                    AdditionalStatuses = new() { new StatusCondition { StatusId = item.Bonds, MaximumSeconds = 3600 } } });
            }
            plan.AdaptiveMechanics.Add(rule);
            // Explicit synthetic test binding, not evidence of the FF Logs gameZone-to-Dalamud-territory mapping.
            plan.StrategyEvidence.Add(new StrategyEvidenceAttachment { EncounterId = 104, TerritoryId = Territory, EncounterVerified = true });
            return plan;
        }
    }
}
