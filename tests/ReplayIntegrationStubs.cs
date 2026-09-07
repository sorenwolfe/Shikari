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
        public static FakeActions Actions { get; } = new();
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
    public sealed class FakePlans
    {
        public PlanDocument? Active = PlanDocument.CreateDefault();
        public int Saves;
        public bool SaveSucceeds = true;
        public bool SaveActive() { Saves++; return SaveSucceeds; }
    }
    public sealed class FakeActions { public FakeAction Get(uint id) => new(); }
    public sealed class FakeAction { public string Name => "Observed cast"; }
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
                Check(plan.StrategyEvidence.Count == 1 && plan.StrategyEvidence[0].Key == "local:" + attempt.Id,
                    "A completed pull must attach its evidence automatically to the active strategy");
                Check(plan.StrategyEvidence[0].Mechanics.Count == 1 && plan.StrategyEvidence[0].Mechanics[0].EntryId == plan.Timeline[0].Id,
                    "Automatic evidence must reference the authored timeline entry");
                Check(Plugin.Plans.Saves == 1, "Completed-pull enrichment must persist once despite duplicate wipe signals");
                id = attempt.Id;
            }
            Check(System.IO.File.Exists(System.IO.Path.Combine(directory, "replays", id + ".json")), "Replay persisted");
            using (var store = new ReplayStore())
            {
                for (int i = 0; i < 100 && store.Attempts.Count == 0; i++) { Thread.Sleep(10); Plugin.Framework.Tick(); }
                Check(store.Attempts.Count == 1, "Persisted replay loads");
                Check(store.Attempts[0].StatusObservations.Count == 1 && store.Attempts[0].AdaptiveDecisions[0].Reason == "Long duration", "Adaptive evidence survives disk reload");
                var evidenceCount = plan.StrategyEvidence.Count;
                var saveCount = Plugin.Plans.Saves;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.ClientState.Change();
                Check(!store.Recording && store.Attempts.Count == 2, "Zone change closes recording");
                Check(plan.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount, "Zone changes must not auto-enrich the strategy");

                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast();
                var replacement = PlanDocument.CreateDefault(); Plugin.Plans.Active = replacement;
                Plugin.Encounter.End();
                Check(plan.StrategyEvidence.Count == evidenceCount && replacement.StrategyEvidence.Count == 0 && Plugin.Plans.Saves == saveCount,
                    "Changing active strategy during a pull must prevent enrichment of either plan");

                // An unconfigured cast reaches the real action-name lookup and timeline inference.
                replacement.Slides[0].Title = "Observed cast";
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
                Check(replacement.StrategyEvidence.Count == 1 && replacement.Timeline.Count == 1 && replacement.Timeline[0].CastActionId == 123,
                    "Completed local pull must infer a matching untimed board from its observed cast name");
                Check(!replacement.Timeline[0].Enabled, "Inferred local timeline instructions must stay disabled for review");
                evidenceCount = replacement.StrategyEvidence.Count; saveCount = Plugin.Plans.Saves;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); replacement.Notes = "Edited during pull"; Plugin.Encounter.End();
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount,
                    "Editing the active strategy during a pull must reject stale automatic evidence");
                Plugin.Plans.SaveSucceeds = false;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount + 1,
                    "A failed strategy save must roll back automatic enrichment without discarding the replay");
                Plugin.Plans.SaveSucceeds = true; saveCount = Plugin.Plans.Saves;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); store.Dispose();
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount,
                    "Plugin unload must save the recording without automatic strategy enrichment");
                store.Clear();
                Check(store.Attempts.Count == 0, "Clear removes attempts");
            }
            Check(System.IO.Directory.GetFiles(System.IO.Path.Combine(directory, "replays"), "*.json").Length == 0, "Clear persists");
            Console.WriteLine("PASS: recording lifecycle, duplicate end, cast anchoring, persistence, reload, zone change, clear");
            Console.WriteLine("PASS: automatic completed-pull evidence, action-name inference, persistence, changed-plan rejection, save rollback and zone/unload skips");
        }
    }
}

namespace Shikari.Services.Replay { public sealed class LocalEvidenceCapture { public void Capture(ReplayAttempt a, float t) { } public void Invalidate(ReplayAttempt a, float t) { } } }
