using System;
using System.Collections.Generic;
using System.Threading;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Shikari.Services
{
    public sealed class CastEvent
    {
        public uint ActionId { get; set; }
        public int Occurrence { get; set; }
        public float CombatTime { get; set; }
        public float TotalCastTime { get; set; }
    }
}
namespace Shikari.Services.Live
{
    public sealed class ArenaTracker
    {
        public readonly record struct LivePlayer(string Name, uint JobId, int SlotIndex, System.Numerics.Vector2 Board, bool IsLocal);
        public bool Aligned => true;
        public float BoardPerYalm => .025f;
        public IReadOnlyList<LivePlayer> Read(PlanDocument plan, Slide slide, int? localSlotOverride = null) => new[] { new LivePlayer("Player", 25, 0, new(.5f,.5f), true) };
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static FakeInterface PluginInterface { get; } = new();
        public static FakeConfig Config { get; } = new();
        public static FakeEncounter Encounter { get; } = new();
        public static FakeClient ClientState { get; } = new();
        public static FakeFramework Framework { get; } = new();
        public static FakePlans Plans { get; } = new();
        public static FakeRoster Roster { get; } = new();
        public static FakeMain Main { get; } = new();
        public static FakeLog Log { get; } = new();
        public static FakeAdaptive Adaptive { get; } = new();
    }
    public sealed class FakeAdaptive
    {
        public event Action<StatusObservation>? Observed;
        public event Action<AdaptiveDecision>? Decided;
        public void Emit()
        {
            Observed?.Invoke(new StatusObservation { StatusId = 10, Duration = 30 });
            Decided?.Invoke(new AdaptiveDecision { Mechanic = "Assignment", Reason = "Long duration", Applied = true });
        }
    }
    public sealed class FakeInterface { public string Directory = ""; public string GetPluginConfigDirectory() => Directory; }
    public sealed class FakeConfig { public bool ReplayEnabled = true; public int ReplayRetention = 10; }
    public sealed class FakePlans { public PlanDocument? Active = PlanDocument.CreateDefault(); }
    public sealed class FakeRoster { public int ResolveLocalSlot(PlanDocument p) => 0; }
    public sealed class FakeMain { public int SlideIndex = 0; }
    public sealed class FakeLog { public void Warning(Exception e, string message) { } }
    public sealed class FakeClient
    {
        public uint TerritoryType = 1;
        public event Action<uint>? TerritoryChanged;
        public void Change() => TerritoryChanged?.Invoke(++TerritoryType);
    }
    public sealed class FakeFramework : Dalamud.Plugin.Services.IFramework
    {
        public event Action<Dalamud.Plugin.Services.IFramework>? Update;
        public void Tick() => Update?.Invoke(this);
    }
    public sealed class FakeEncounter
    {
        public bool LastPullWasWipe;
        public event Action? CombatStarted;
        public event Action? CombatEnded;
        public event Action? Wiped;
        public event Action<Services.CastEvent>? CastStarted;
        public void Begin() => CombatStarted?.Invoke();
        public void End() { LastPullWasWipe = true; CombatEnded?.Invoke(); Wiped?.Invoke(); }
        public void Cast() => CastStarted?.Invoke(new Services.CastEvent { ActionId = 123, Occurrence = 1, CombatTime = 0, TotalCastTime = .1f });
    }
}
namespace Shikari.Tests
{
    public static class ReplayIntegration
    {
        private static void Check(bool c, string message) { if (!c) throw new Exception(message); }
        public static void Run(string directory)
        {
            Plugin.PluginInterface.Directory = directory;
            var plan = Plugin.Plans.Active!;
            plan.Timeline.Add(new TimelineEntry { CastActionId = 123, SlideId = plan.Slides[0].Id });
            string id;
            using (var store = new ReplayStore())
            {
                Plugin.Encounter.Begin();
                Check(store.Recording, "Recording starts without drawing a window");
                Plugin.Framework.Tick();
                Plugin.Encounter.Cast();
                Plugin.Adaptive.Emit();
                Thread.Sleep(120);
                Plugin.Framework.Tick();
                Plugin.Encounter.End();
                Check(!store.Recording && store.Attempts.Count == 1, "End + wipe only persists one attempt");
                var attempt = store.Attempts[0];
                Check(attempt.Frames.Count == 2, "Framework records positions without UI");
                Check(attempt.Mechanics.Count == 1, "Cast anchor recorded");
                Check(attempt.StatusObservations.Count == 1 && attempt.AdaptiveDecisions.Count == 1, "Adaptive evidence recorded");
                id = attempt.Id;
            }
            Check(System.IO.File.Exists(System.IO.Path.Combine(directory, "replays", id + ".json")), "Replay persisted");
            using (var store = new ReplayStore())
            {
                for (int i = 0; i < 100 && store.Attempts.Count == 0; i++) { Thread.Sleep(10); Plugin.Framework.Tick(); }
                Check(store.Attempts.Count == 1, "Persisted replay loads");
                Check(store.Attempts[0].StatusObservations.Count == 1 && store.Attempts[0].AdaptiveDecisions[0].Reason == "Long duration", "Adaptive evidence survives disk reload");
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.ClientState.Change();
                Check(!store.Recording && store.Attempts.Count == 2, "Zone change closes recording");
                store.Clear();
                Check(store.Attempts.Count == 0, "Clear removes attempts");
            }
            Check(System.IO.Directory.GetFiles(System.IO.Path.Combine(directory, "replays"), "*.json").Length == 0, "Clear persists");
            Console.WriteLine("PASS: recording lifecycle, duplicate end, cast anchoring, persistence, reload, zone change, clear");
        }
    }
}

namespace Shikari.Services.Replay { public sealed class LocalEvidenceCapture { public void Capture(ReplayAttempt a, float t) { } public void Invalidate(ReplayAttempt a, float t) { } } }
