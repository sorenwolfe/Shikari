using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Replay;

namespace Shikari.Tests
{
    public static class ActorIdentityTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static void Run()
        {
            ExactMechanicCast();
            CaptureMapsActualObjectIdentity();
            LiveIdentityReachesTargetEvidence();
            LegacyAndJson();
            SourceAndOwnership();
            AmbiguityAndInvalidIds();
            Validation();
            CaptureIdentityConflicts();
            Console.WriteLine("PASS: actor identity capture, provenance, scoped status/position resolution, ambiguity, validation and legacy JSON");
        }
        private static EvidenceActor Actor(long id, ulong? gameObjectId = null) => new()
            { Id = id, GameObjectId = gameObjectId, Name = "Same name", JobId = 25 };
        private static ulong? GameId(EvidenceActor actor) => actor.GameObjectId;
        private static ReplayAttempt Attempt(string source = "Live", ulong? caster = 0x100000007, ulong? target = 0x100000009)
        {
            var attempt = new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 10 };
            attempt.Casts.Add(new RecordedCast { Source = source, CasterId = caster, TargetId = target, ActionId = 100,
                ObservedTime = 2, StartTime = 1 });
            return attempt;
        }
        private static EvidenceActor? Resolve(ReplayAttempt attempt, RecordedCast cast, bool target = false)
            => EvidenceActorIdentity.Resolve(attempt, cast, target);
        private static RecordedCast? FindCast(ReplayAttempt attempt, ReplayMechanic? mechanic)
            => EvidenceActorIdentity.FindCast(attempt, mechanic);
        private static void ExactMechanicCast()
        {
            var attempt = Attempt(); var cast = attempt.Casts[0]; cast.Occurrence = 2;
            var mechanic = new ReplayMechanic { ActionId = 100, Occurrence = 2, Time = 1 };
            attempt.Mechanics.Add(mechanic);
            Check(ReferenceEquals(FindCast(attempt, mechanic), cast), "Exact action, occurrence and start return the actual stored cast");
            Check(FindCast(attempt, null) == null && FindCast(attempt,
                new ReplayMechanic { ActionId = 100, Occurrence = 2, Time = 1 }) == null,
                "Absent and foreign mechanics cannot borrow this recording's cast");
            cast.StartTime = 1.01f;
            Check(FindCast(attempt, mechanic) == null, "Nearby cast timestamps cannot be treated as the same observation");
            cast.StartTime = 1; cast.Occurrence = 3;
            Check(FindCast(attempt, mechanic) == null, "Another occurrence cannot supply review identity");
            cast.Occurrence = 2; cast.ActionId = 101;
            Check(FindCast(attempt, mechanic) == null, "Another action cannot supply review identity");
            cast.ActionId = 100; cast.StartTime = null; cast.CompletionTime = 1;
            Check(FindCast(attempt, mechanic) == null, "Completion-only observations cannot invent a matching cast start");
            cast.StartTime = 1; cast.Occurrence = null;
            Check(FindCast(attempt, mechanic) == null, "Unknown legacy occurrences cannot supply a match");
            cast.Occurrence = 2;
            attempt.Casts.Add(new RecordedCast { ActionId = 100, Occurrence = 2, StartTime = 1 });
            Check(FindCast(attempt, mechanic) == null, "Multiple exact observations remain ambiguous");
            attempt.Casts.Clear();
            Check(FindCast(attempt, mechanic) == null, "A legacy recording without casts retains unknown identity");
        }
        private static void CaptureMapsActualObjectIdentity()
        {
            var player = new IdentityPlayer { EntityId = 9, GameObjectId = 0x100000009, Position = new(110, 3, 90) };
            player.StatusList.Add(new IdentityStatus { StatusId = 42, RemainingTime = 5, SourceId = 7 });
            Plugin.ObjectTable.LocalPlayer = player;
            Plugin.PartyList.Clear(); Plugin.PartyList.Add(new IdentityPartyMember { GameObject = player });
            var attempt = Attempt();
            new LocalEvidenceCapture().Capture(attempt, 2);
            Check(attempt.Evidence.Actors.Count == 1 && attempt.Evidence.Actors[0].Id == 9 &&
                GameId(attempt.Evidence.Actors[0]) == 0x100000009,
                "Production party capture must explicitly map the observed GameObjectId to the status EntityId");
            Check(attempt.Evidence.Statuses.Single().ActorId == 9 && attempt.Evidence.Positions.Single().ActorId == 9,
                "Adding cast identity must preserve existing status/position entity keys");
            var actor = Resolve(attempt, attempt.Casts[0], true);
            Check(actor?.Id == 9 && new EvidenceTimeline(attempt.Evidence).StatusesAt(actor.Id, 2).Single().StatusId == 42,
                "A captured cast target must reach that target's captured status");
        }
        private static void LiveIdentityReachesTargetEvidence()
        {
            var attempt = Attempt();
            attempt.Evidence.Actors.AddRange(new[] { Actor(7, 0x100000007), Actor(9, 0x100000009), Actor(11, 9) });
            attempt.Evidence.Statuses.AddRange(new[] { new EvidenceStatus { ActorId = 9, StatusId = 42, Time = 1, Duration = 5 },
                new EvidenceStatus { ActorId = 7, StatusId = 77, Time = 1 }, new EvidenceStatus { ActorId = 11, StatusId = 88, Time = 1 } });
            attempt.Evidence.Positions.Add(new EvidencePosition { ActorId = 9, Time = 2, Position = new(110, 90) });
            var caster = Resolve(attempt, attempt.Casts[0]);
            var target = Resolve(attempt, attempt.Casts[0], true);
            Check(caster?.Id == 7 && target?.Id == 9, "Caster and target use explicit GameObjectIds, including high 32 bits");
            var timeline = new EvidenceTimeline(attempt.Evidence);
            Check(timeline.StatusesAt(target!.Id, 2).Single().StatusId == 42 && timeline.PositionAt(target.Id, 2)?.Position == new Vector2(110, 90),
                "Resolved target reaches only its observed statuses and fresh position");
            Check(timeline.PositionAt(target.Id, 3) == null, "Actor identity must not hold stale movement across evidence gaps");
            attempt.Casts[0].TargetId = 9;
            Check(Resolve(attempt, attempt.Casts[0], true)?.Id == 11, "A low numeric game object ID cannot fall back to a matching entity ID");
            attempt.Casts[0].TargetId = 12;
            Check(Resolve(attempt, attempt.Casts[0], true) == null, "Matching names and jobs cannot invent a target");
        }
        private static void LegacyAndJson()
        {
            foreach (var settings in new[] { PlanJson.Readable(), PlanJson.Compact() })
            {
                var attempt = Attempt();
                attempt.Evidence.Actors.Add(Actor(9, 0x100000009));
                var copy = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(attempt, settings), settings)!;
                Check(GameId(copy.Evidence.Actors[0]) == 0x100000009 && Resolve(copy, copy.Casts[0], true)?.Id == 9 && ReplayValidation.IsValid(copy),
                    "Production readable and compact JSON retain an exact optional actor mapping");
                var legacy = JObject.Parse(JsonConvert.SerializeObject(attempt, settings));
                ((JObject)legacy["Evidence"]!["Actors"]![0]!).Remove("GameObjectId");
                var old = JsonConvert.DeserializeObject<ReplayAttempt>(legacy.ToString(), settings)!;
                old.Casts[0].TargetId = 9;
                Check(GameId(old.Evidence.Actors[0]) == null && Resolve(old, old.Casts[0], true) == null && ReplayValidation.IsValid(old),
                    "Legacy entity IDs remain valid but cannot masquerade as live game object IDs");
            }
        }
        private static void SourceAndOwnership()
        {
            var logs = Attempt("FF Logs", 7, 9);
            logs.Evidence.Source = "FF Logs"; logs.Evidence.ReportCode = "report-A"; logs.Evidence.FightId = 1;
            logs.Evidence.Actors.AddRange(new[] { Actor(7), Actor(9) });
            Check(Resolve(logs, logs.Casts[0])?.Id == 7 && Resolve(logs, logs.Casts[0], true)?.Id == 9,
                "FF Logs actor IDs resolve directly within their owning recording");
            var other = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(logs))!;
            other.Evidence.ReportCode = "report-B";
            Check(Resolve(other, logs.Casts[0], true) == null, "Another report cannot borrow a cast despite identical numeric actor IDs");
            other.Evidence.ReportCode = "report-A"; other.Evidence.FightId = 2;
            Check(Resolve(other, logs.Casts[0], true) == null, "Another pull cannot borrow a cast even within the same report");
            foreach (var pair in new[] { ("Live", "FF Logs"), ("FF Logs", "Local recording"), ("guessed", "Local recording"), ("Live", "unknown") })
            {
                var mixed = Attempt(pair.Item1, 7, 9); mixed.Evidence.Source = pair.Item2;
                mixed.Evidence.Actors.Add(Actor(9, 9));
                Check(Resolve(mixed, mixed.Casts[0], true) == null, "Mismatched or unknown source provenance cannot resolve identity");
            }
        }
        private static void AmbiguityAndInvalidIds()
        {
            var attempt = Attempt();
            attempt.Evidence.Actors.AddRange(new[] { Actor(9, 0x100000009), Actor(10, 0x100000009) });
            Check(Resolve(attempt, attempt.Casts[0], true) == null, "One game object mapped to two entity IDs is ambiguous");
            attempt.Evidence.Actors[1] = Actor(9, 0x100000010);
            Check(Resolve(attempt, attempt.Casts[0], true) == null, "Duplicate status entity IDs cannot select one actor");
            foreach (var id in new ulong?[] { null, 0, 0xE0000000, uint.MaxValue, ulong.MaxValue })
            {
                attempt.Casts[0].TargetId = id;
                attempt.Evidence.Actors = new() { Actor(9, id) };
                Check(Resolve(attempt, attempt.Casts[0], true) == null, "Missing and sentinel game object IDs stay unknown");
            }
            foreach (var id in new long[] { 0, -1, 0xE0000000, uint.MaxValue, (long)uint.MaxValue + 1 })
            {
                attempt.Casts[0].TargetId = 0x100000009; attempt.Evidence.Actors = new() { Actor(id, 0x100000009) };
                Check(Resolve(attempt, attempt.Casts[0], true) == null, "A live mapped entity must belong to the entity-ID domain");
            }
            attempt.Evidence.Actors = Enumerable.Range(1, 33).Select(i => Actor(i, (ulong)i)).ToList(); attempt.Casts[0].TargetId = 1;
            Check(Resolve(attempt, attempt.Casts[0], true) == null, "Oversized actor input cannot trigger an unbounded lookup");
            attempt.Evidence.Actors = new() { null! };
            Check(Resolve(attempt, attempt.Casts[0], true) == null, "Malformed actors cannot crash resolution");
        }
        private static void Validation()
        {
            foreach (var id in new ulong[] { 0, 0xE0000000, uint.MaxValue, ulong.MaxValue })
            {
                var attempt = Attempt(); attempt.Evidence.Actors.Add(Actor(9, id));
                Check(!ReplayValidation.IsValid(attempt), "Explicit invalid live mappings must not pass the replay reader");
            }
            var logs = Attempt("FF Logs", 7, 9); logs.Evidence.Source = "FF Logs"; logs.Evidence.Actors.Add(Actor(9, 0x100000009));
            Check(!ReplayValidation.IsValid(logs), "FF Logs evidence cannot contain a live-only game object mapping");
        }
        private static void CaptureIdentityConflicts()
        {
            var player = new IdentityPlayer { EntityId = 9, GameObjectId = 0x100000009 };
            Plugin.ObjectTable.LocalPlayer = player; Plugin.PartyList.Clear();
            var attempt = Attempt(); var capture = new LocalEvidenceCapture(); capture.Capture(attempt, 1);
            player.GameObjectId = 0x200000009; capture.Capture(attempt, 2);
            player.GameObjectId = 0x100000009; capture.Capture(attempt, 3);
            Check(GameId(attempt.Evidence.Actors.Single()) == null && Resolve(attempt, attempt.Casts[0], true) == null,
                "Entity reuse permanently clears a conflicting mapping within this recording");
            var next = Attempt(); new LocalEvidenceCapture().Capture(next, 1);
            Check(Resolve(next, next.Casts[0], true)?.Id == 9, "Identity conflict state cannot leak into the next recording");
            Plugin.PartyList.Add(new IdentityPartyMember { GameObject = player });
            Plugin.PartyList.Add(new IdentityPartyMember { GameObject = new IdentityPlayer { EntityId = 9, GameObjectId = 0x200000009 } });
            var duplicate = Attempt(); new LocalEvidenceCapture().Capture(duplicate, 1);
            Check(GameId(duplicate.Evidence.Actors.Single()) == null, "Duplicate entity observations cannot conceal conflicting object IDs");
            Plugin.PartyList.Clear(); player.EntityId = 0xE0000000;
            var invalid = Attempt(); new LocalEvidenceCapture().Capture(invalid, 1);
            Check(invalid.Evidence.Actors.Count == 0 && invalid.Evidence.Statuses.Count == 0 && invalid.Evidence.Positions.Count == 0,
                "Invalid entity sentinels cannot create actors or observations");
        }
    }
    public sealed class IdentityPlayer : Dalamud.Game.ClientState.Objects.Types.IBattleChara
    {
        public uint EntityId { get; set; }
        public ulong GameObjectId { get; set; }
        public IdentityName Name { get; } = new();
        public IdentityJob ClassJob { get; } = new();
        public Vector3 Position { get; set; }
        public uint CurrentHp { get; set; } = 100;
        public List<IdentityStatus> StatusList { get; } = new();
    }
    public sealed class IdentityName { public string TextValue => "Same name"; }
    public sealed class IdentityJob { public uint RowId => 25; }
    public sealed class IdentityStatus { public uint StatusId; public float RemainingTime; public int Param; public uint SourceId; }
    public sealed class IdentityPartyMember { public object? GameObject; }
    public sealed class IdentityObjects { public IdentityPlayer? LocalPlayer; }
}
namespace Dalamud.Game.ClientState.Objects.Types
{
    public interface IBattleChara
    {
        uint EntityId { get; }
        ulong GameObjectId { get; }
        Shikari.Tests.IdentityName Name { get; }
        Shikari.Tests.IdentityJob ClassJob { get; }
        Vector3 Position { get; }
        uint CurrentHp { get; }
        List<Shikari.Tests.IdentityStatus> StatusList { get; }
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static Tests.IdentityObjects ObjectTable { get; } = new();
        public static List<Tests.IdentityPartyMember> PartyList { get; } = new();
    }
}
namespace Shikari.Services
{
    public static class RosterResolver { public static int MatchSeat(IReadOnlyList<PlayerSlot> roster, string name, uint job, int pinned) => -1; }
    public static class CallTemplate { public static string FormatTime(float value) => value.ToString("0.0"); }
}
