using System;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;

namespace Shikari.Services { public static class CallTemplate { public static string FormatTime(float value) => value.ToString("0.0"); } }
namespace Shikari.Tests
{
    public static class EvidenceIntegrationTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static void Run()
        {
            var plan = PlanDocument.CreateDefault();
            plan.Name = "Caro reference";
            plan.Timeline.Add(new TimelineEntry { CastActionId = 123, Occurrence = 2, SlideId = plan.Slides[0].Id });
            var data = new LogFightData { ReportCode = "abcdefghijklmnop", Fight = new LogFight { Id = 30, StartTime = 10000, EndTime = 30000 } };
            data.Actors.Add(new LogActor { Id = 1, Name = "First", Type = "Player", Job = "BlackMage" });
            data.Actors.Add(new LogActor { Id = 2, Name = "Second", Type = "Player", Job = "BlackMage" });
            data.EnemyCasts.Add(new LogCast { AbilityId = 123, AbilityName = "Assignment", TimeSeconds = 1, IsCastStart = true });
            data.EnemyCasts.Add(new LogCast { AbilityId = 123, AbilityName = "Assignment", TimeSeconds = 10, CastSeconds = 1 });
            var source = new LogEvidence();
            source.StatusEvents.Add(new LogStatusEvent { Time=1.2f, TargetId=1, StatusId=10, AbilityId=1000010, Duration=10 });
            source.StatusEvents.Add(new LogStatusEvent { Time=1.6f, TargetId=1, StatusId=20, AbilityId=1000020, Duration=10 });
            source.StatusEvents.Add(new LogStatusEvent { Time=1.2f, TargetId=2, StatusId=30, AbilityId=1000030, Duration=10 });
            source.Positions.Add(new LogPosition { ActorId=1, Time=1, X=10000, Y=10000 });
            source.Positions.Add(new LogPosition { ActorId=1, Time=1, X=10001, Y=10000 });
            var attempt = LogReplayBuilder.Build(plan, data, source, id => id is 10 or 20, _ => 25);
            Check(ReplayValidation.IsValid(attempt), "Imported evidence validates with no fabricated aligned frames");
            Check(attempt.Frames.Count == 0 && attempt.Evidence.Positions.Single().Position.X == 10001, "Raw coordinates retained, duplicates resolved");
            Check(attempt.Evidence.Statuses.Single(s => s.ActorId == 2).StatusId == 0, "Unknown game status row cannot become a live condition");
            Check(attempt.Mechanics[0].SlideId == "" && attempt.Mechanics[1].SlideId == plan.Slides[0].Id, "Exact action and occurrence binding");
            plan.Name = "Edited after import";
            Check(attempt.Plan.Name == "Caro reference", "Evidence keeps frozen plan");
            var selected = attempt.Evidence.Statuses.Where(s => s.ActorId == 1).ToArray();
            var draft = EvidenceRules.Draft(attempt, attempt.Mechanics[0], selected, attempt.Plan.Slides[0].Id, 1);
            Check(!draft.Enabled && draft.Branches[0].AdditionalStatuses.Count == 1 && draft.Branches[0].Parameter == -1,
                "Draft is disabled conjunction, never assumes unknown parameter zero");
            Check(EvidenceRules.Simulate(attempt, 1, draft).Single().SlideId == attempt.Plan.Slides[0].Id, "Compound rule replays from recorded evidence");
            Check(EvidenceRules.Simulate(attempt, 2, draft).All(d => d.SlideId == ""), "Other player cannot borrow assignment");
            Check(attempt.Plan.AdaptiveMechanics.Count == 0 && plan.AdaptiveMechanics.Count == 0, "Simulation never edits live or recorded rules");
            attempt.Evidence.Complete = false;
            attempt.Evidence.Actors[0].SlotIndex = 0;
            var incompleteReload = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt, PlanJson.Compact()), PlanJson.Compact())!;
            Check(!incompleteReload.Evidence.Complete && incompleteReload.Evidence.Actors[0].SlotIndex == 0, "Incomplete flag and first seat survive compact JSON");
            var blocked = false;
            try { EvidenceRules.Draft(attempt, attempt.Mechanics[0], selected, attempt.Plan.Slides[0].Id, 1); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Incomplete source cannot draft live rules");
            attempt.Evidence.Complete = true;
            var roundtrip = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt, PlanJson.Compact()), PlanJson.Compact())!;
            Check(ReplayValidation.IsValid(roundtrip) && roundtrip.Evidence.Statuses[0].Parameter == null, "Evidence survives compact serialization without inventing zero");
            var old = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
            var json = JsonConvert.SerializeObject(old, PlanJson.Compact());
            var objectForm = Newtonsoft.Json.Linq.JObject.Parse(json); objectForm.Remove("Evidence");
            Check(ReplayValidation.IsValid(JsonConvert.DeserializeObject<ReplayAttempt>(objectForm.ToString(), PlanJson.Compact())!), "Old replay without evidence remains valid");
            Check(JsonConvert.DeserializeObject<ReplayAttempt>(json, PlanJson.Compact())!.LocalSlot == 0, "Local first seat survives compact JSON");
            Check(JsonConvert.DeserializeObject<ReplayPlayer>("{\"Name\":\"Legacy first seat\"}", PlanJson.Compact())!.SlotIndex == 0,
                "Legacy compact replay players with omitted seat retain seat zero");
            Console.WriteLine("PASS: log-to-replay import, exact anchor matching, rule drafting/simulation, ID validation and backwards serialization");
        }
    }
}
