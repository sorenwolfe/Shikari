using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Shikari.Services;
using Shikari.Services.Replay;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;

namespace Shikari.Tests
{
    public static class EncounterCastTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static void Run()
        {
            Plugin.ObjectTable.Clear(); Plugin.Condition.InCombat = true;
            var caster = new FakeActor { EntityId = 7, GameObjectId = 0x100000007, ObjectKind = ObjectKind.BattleNpc,
                IsCasting = true, CastActionId = 100, CurrentCastTime = .25f, TotalCastTime = 3,
                CastTargetObjectId = 0x100000009, Position = new(100, 3, 90), Rotation = 1.2f };
            var target = new FakeActor { EntityId = 9, GameObjectId = 0x100000009, Position = new(110, 4, 90), Rotation = -2 };
            Plugin.ObjectTable.Add(caster); Plugin.ObjectTable.Add(target);
            using var monitor = new EncounterMonitor();
            var events = new List<CastEvent>(); monitor.CastStarted += events.Add;
            Plugin.Framework.Tick();
            Check(events.Count == 1, "Production live polling still emits one cast start");
            var context = typeof(CastEvent).GetProperty("Context")?.GetValue(events[0]) as CastStartContext;
            Check(context != null, "Production live polling must attach an explicit cast-start actor snapshot");
            Check(context!.CasterId == 0x100000007 && context.TargetId == 0x100000009 &&
                context.CasterWorldPosition == new Vector3(100,3,90) && context.TargetWorldPosition == new Vector3(110,4,90) &&
                context.CasterHeading == 1.2f && context.TargetHeading == -2,
                "Snapshot preserves actual game object IDs, target geometry and radians without conflating entity IDs");
            Check(context.ObservedAtUtc > context.StartedAtUtc && context.StartedAtUtc == events[0].StartedAtUtc &&
                context.ObservedTime >= events[0].CombatTime, "Polling delay is explicit; geometry is never backdated to the bar start");
            caster.Position = new(999,999,999); target.Position = new(888,888,888);
            Check(context.CasterWorldPosition == new Vector3(100,3,90) && context.TargetWorldPosition == new Vector3(110,4,90),
                "Actor movement cannot mutate recorded cast-start context");
            var first = RecordedCast.FromLive(events[0].ActionId, events[0].Occurrence, events[0].CombatTime, events[0].TotalCastTime, context);
            caster.IsCasting = false;
            Scan(monitor);
            Check(events.Count == 1 && first.CompletionTime == null,
                "A cast bar disappearing never establishes a completion or effect event");
            caster.IsCasting = true; caster.CastTargetObjectId = 123456;
            Scan(monitor);
            var absent = (CastStartContext)typeof(CastEvent).GetProperty("Context")!.GetValue(events[1])!;
            Check(absent.TargetId == 123456 && absent.TargetWorldPosition == null && absent.TargetHeading == null,
                "An unresolved cast target retains its identity with unknown geometry");
            caster.IsCasting = false; Scan(monitor);
            caster.IsCasting = true; caster.CastTargetObjectId = 0xE0000000;
            Scan(monitor);
            var noTarget = (CastStartContext)typeof(CastEvent).GetProperty("Context")!.GetValue(events[2])!;
            var recording = RecordedCast.FromLive(100, 3, 0, 3, noTarget);
            Check(recording.TargetId == null && recording.TargetWorldPosition == null, "No-target sentinel remains unknown");
            caster.IsCasting = false; Scan(monitor);
            caster.IsCasting = true; caster.CurrentCastTime = 0;
            Scan(monitor);
            var zero = events[3];
            var zeroContext = (CastStartContext)typeof(CastEvent).GetProperty("Context")!.GetValue(zero)!;
            Check(RecordedCast.FromLive(100, 4, zero.CombatTime, zero.TotalCastTime, zeroContext).IsValid(10),
                "A cast observed at zero bar elapsed must not place its reconstructed start after the geometry snapshot");
            Console.WriteLine("PASS: production live polling preserves cast geometry, timing and unknown completion");
        }
        private static void Scan(EncounterMonitor monitor) =>
            typeof(EncounterMonitor).GetMethod("ScanCasts", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(monitor, new object[] { DateTime.UtcNow });
    }
    public sealed class FakeActor : IBattleNpc
    {
        public uint EntityId { get; set; }
        public ulong GameObjectId { get; set; }
        public ObjectKind ObjectKind { get; set; }
        public BattleNpcSubKind BattleNpcKind => BattleNpcSubKind.Combatant;
        public bool IsCasting { get; set; }
        public uint CastActionId { get; set; }
        public ulong CastTargetObjectId { get; set; }
        public float CurrentCastTime { get; set; }
        public float TotalCastTime { get; set; }
        public Vector3 Position { get; set; }
        public float Rotation { get; set; }
        public FakeName Name => new();
        public bool IsDead => false;
        public bool IsTargetable => true;
        public uint CurrentHp => 100;
    }
    public sealed class FakeName { public string TextValue => "Fixture"; }
    public sealed class FakeFramework : Dalamud.Plugin.Services.IFramework
    {
        public event Action<Dalamud.Plugin.Services.IFramework>? Update;
        public void Tick() => Update?.Invoke(this);
    }
    public sealed class FakeDuty
    {
        public event Action<Dalamud.Game.DutyState.IDutyStateEventArgs>? DutyWiped;
        public event Action<Dalamud.Game.DutyState.IDutyStateEventArgs>? DutyRecommenced;
        public void Emit(Dalamud.Game.DutyState.IDutyStateEventArgs args) { DutyWiped?.Invoke(args); DutyRecommenced?.Invoke(args); }
    }
    public sealed class FakeObjects : List<IGameObject>
    {
        public IBattleChara? LocalPlayer => null;
        public IGameObject? SearchById(ulong id) => this.FirstOrDefault(a => a.GameObjectId == id);
    }
    public sealed class FakeConditions
    {
        public bool InCombat;
        public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag flag] => InCombat;
    }
    public sealed class FakeLog
    {
        public void Warning(Exception ex, string message) { }
        public void Error(Exception ex, string message) => throw ex;
        public void Information(string text, params object[] args) { }
    }
    public sealed class FakeConfig { public bool LogDetectedCasts => false; }
    public sealed class FakeActions { public string NameOf(uint id) => "Fixture cast"; }
}
namespace Shikari
{
    public static class Plugin
    {
        public static Tests.FakeFramework Framework { get; } = new();
        public static Tests.FakeDuty DutyState { get; } = new();
        public static Tests.FakeObjects ObjectTable { get; } = new();
        public static Tests.FakeConditions Condition { get; } = new();
        public static Tests.FakeLog Log { get; } = new();
        public static Tests.FakeConfig Config { get; } = new();
        public static Tests.FakeActions Actions { get; } = new();
    }
}
namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Dalamud.Game.DutyState { public interface IDutyStateEventArgs { } }
namespace Dalamud.Game.ClientState.Conditions { public enum ConditionFlag { InCombat } }
namespace Dalamud.Game.ClientState.Objects.Enums
{
    public enum ObjectKind { Player, BattleNpc }
    public enum BattleNpcSubKind { Combatant, BNpcPart }
}
namespace Dalamud.Game.ClientState.Objects.Types
{
    public interface IGameObject
    {
        uint EntityId { get; }
        ulong GameObjectId { get; }
        ObjectKind ObjectKind { get; }
        Vector3 Position { get; }
        float Rotation { get; }
    }
    public interface IBattleChara : IGameObject
    {
        bool IsCasting { get; }
        uint CastActionId { get; }
        ulong CastTargetObjectId { get; }
        float CurrentCastTime { get; }
        float TotalCastTime { get; }
        Shikari.Tests.FakeName Name { get; }
        bool IsDead { get; }
        bool IsTargetable { get; }
        uint CurrentHp { get; }
    }
    public interface IBattleNpc : IBattleChara { BattleNpcSubKind BattleNpcKind { get; } }
}
