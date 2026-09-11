using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
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
        public CastStartContext? Context { get; set; }
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
        public float CombatElapsed { get; set; }
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
    public static partial class ReplayIntegration
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
                CompleteQueued(store);
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
                CompleteQueued(store);
                Check(!store.Recording && store.Attempts.Count == 2, "Zone change closes recording");
                Check(plan.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount, "Zone changes must not auto-enrich the strategy");

                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast();
                var replacement = PlanDocument.CreateDefault(); Plugin.Plans.Active = replacement;
                Plugin.Encounter.End();
                CompleteQueued(store);
                Check(plan.StrategyEvidence.Count == evidenceCount && replacement.StrategyEvidence.Count == 0 && Plugin.Plans.Saves == saveCount,
                    "Changing active strategy during a pull must prevent enrichment of either plan");

                // An unconfigured cast reaches the real action-name lookup and timeline inference.
                replacement.Slides[0].Title = "Observed cast";
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
                CompleteQueued(store);
                Check(replacement.StrategyEvidence.Count == 1 && replacement.Timeline.Count == 1 && replacement.Timeline[0].CastActionId == 123,
                    "Completed local pull must infer a matching untimed board from its observed cast name");
                Check(!replacement.Timeline[0].Enabled, "Inferred local timeline instructions must stay disabled for review");
                evidenceCount = replacement.StrategyEvidence.Count; saveCount = Plugin.Plans.Saves;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); replacement.Notes = "Edited during pull"; Plugin.Encounter.End();
                CompleteQueued(store);
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount,
                    "Editing the active strategy during a pull must reject stale automatic evidence");
                Plugin.Plans.SaveSucceeds = false;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
                CompleteQueued(store);
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount + 1,
                    "A failed strategy save must roll back automatic enrichment without discarding the replay");
                Plugin.Plans.SaveSucceeds = true; saveCount = Plugin.Plans.Saves;
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); store.Dispose();
                Check(replacement.StrategyEvidence.Count == evidenceCount && Plugin.Plans.Saves == saveCount,
                    "Plugin unload must save the recording without automatic strategy enrichment");
                store.Clear();
                WaitForStorage(store, "writes");
                Check(store.Attempts.Count == 0, "Clear removes attempts");
            }
            Check(System.IO.Directory.GetFiles(System.IO.Path.Combine(directory, "replays"), "*.json").Length == 0, "Clear persists");
            Console.WriteLine("PASS: recording lifecycle, duplicate end, cast anchoring, persistence, reload, zone change, clear");
            Console.WriteLine("PASS: automatic completed-pull evidence, action-name inference, persistence, changed-plan rejection, save rollback and zone/unload skips");
            RunStorage(Path.Combine(directory, "storage"));
            RunBackground(Path.Combine(directory, "background"));
        }

        private static void CompleteQueued(ReplayStore store) { WaitForStorage(store, "writes"); Plugin.Framework.Tick(); }

        private static void WaitForStorage(ReplayStore store, string field)
        {
            var task = (Task)typeof(ReplayStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            Check(task.Wait(TimeSpan.FromSeconds(10)), "Replay storage did not settle");
        }

        private static ReplayAttempt StoredAttempt(DateTime started) => new()
            { Plan = PlanDocument.CreateDefault(), StartedUtc = started, Duration = 1 };

        private static string ReplayPath(string directory, ReplayAttempt attempt) =>
            Path.Combine(directory, "replays", attempt.Id + ".json");

        private static void RunStorage(string directory)
        {
            var failures = new List<Exception>();
            void RunCase(Action test) { try { test(); } catch (Exception ex) { failures.Add(ex); } }
            RunCase(() => FailedSavePreservesDurableReplay(Path.Combine(directory, "failed-save")));
            RunCase(() => InvalidNewestPreservesValidReplay(Path.Combine(directory, "corrupt-load"), false));
            RunCase(() => InvalidNewestPreservesValidReplay(Path.Combine(directory, "future-load"), true));
            RunCase(() => ExplicitDeletionStaysOrdered(Path.Combine(directory, "ordered")));
            RunCase(() => ReducedRetentionTrimsDurableFiles(Path.Combine(directory, "reduced")));
            RunCase(() => DeletedLoadingReplayStaysDeleted(Path.Combine(directory, "delete-during-load")));
            RunCase(() => EvidenceEditsPreserveRetentionOrder(Path.Combine(directory, "edit-order")));
            RunCase(() => ReloadedEvidenceEditsPreserveRetentionOrder(Path.Combine(directory, "reload-edit-order")));
            RunCase(() => ImportsDuringLoadPreserveRetentionOrder(Path.Combine(directory, "import-during-load")));
            Plugin.Config.ReplayRetention = 10;
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("PASS: failed replay saves preserve durable retention, retry cleans up, invalid/unsupported startup files preserve usable evidence, explicit deletion remains ordered");
        }

        private static void FailedSavePreservesDurableReplay(string directory)
        {
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 1;
            var older = StoredAttempt(DateTime.UtcNow.AddHours(-1));
            var newer = StoredAttempt(DateTime.UtcNow);
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            store.AddImported(older); WaitForStorage(store, "writes");
            Check(File.Exists(ReplayPath(directory, older)), "Baseline replay was not saved");
            // Force the real atomic replacement to fail while old replay deletion is allowed.
            Directory.CreateDirectory(ReplayPath(directory, newer));
            store.AddImported(newer); WaitForStorage(store, "writes");
            Check(!string.IsNullOrEmpty(store.Status), "A failed replay save must report its storage error");
            Check(File.Exists(ReplayPath(directory, older)), "Failed replacement must preserve the last durable replay");
            Check(!File.Exists(ReplayPath(directory, newer)), "The replacement should still be unsaved");
            Directory.Delete(ReplayPath(directory, newer));
            store.SaveEvidence(newer); WaitForStorage(store, "writes");
            Check(File.Exists(ReplayPath(directory, newer)), "Retry must persist the replacement replay");
            Check(!File.Exists(ReplayPath(directory, older)), "Successful retry must enforce retention on the older fallback");
        }

        private static void InvalidNewestPreservesValidReplay(string directory, bool futureVersion)
        {
            Directory.CreateDirectory(Path.Combine(directory, "replays"));
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 1;
            var older = StoredAttempt(DateTime.UtcNow.AddHours(-1));
            var newer = StoredAttempt(DateTime.UtcNow);
            var excess = StoredAttempt(DateTime.UtcNow.AddHours(-2));
            var futureOlder = StoredAttempt(DateTime.UtcNow.AddHours(-3));
            futureOlder.Version = 99;
            File.WriteAllText(ReplayPath(directory, excess), JsonConvert.SerializeObject(excess, PlanJson.Compact()));
            File.SetLastWriteTimeUtc(ReplayPath(directory, excess), DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(ReplayPath(directory, futureOlder), JsonConvert.SerializeObject(futureOlder, PlanJson.Compact()));
            File.SetLastWriteTimeUtc(ReplayPath(directory, futureOlder), DateTime.UtcNow.AddHours(-3));
            File.WriteAllText(ReplayPath(directory, older), JsonConvert.SerializeObject(older, PlanJson.Compact()));
            File.SetLastWriteTimeUtc(ReplayPath(directory, older), DateTime.UtcNow.AddHours(-1));
            newer.Version = 99;
            File.WriteAllText(ReplayPath(directory, newer), futureVersion
                ? JsonConvert.SerializeObject(newer, PlanJson.Compact()) : "{ damaged-json");
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            Check(File.Exists(ReplayPath(directory, older)), "Invalid or unsupported newest replay must not erase an older usable replay");
            Check(store.Attempts.Count == 1 && store.Attempts[0].Id == older.Id, "Retention must count successfully loaded replays");
            Check(File.Exists(ReplayPath(directory, newer)), "Rejected newest replay must remain available for recovery or a newer reader");
            Check(!File.Exists(ReplayPath(directory, excess)), "Startup must still trim excess validated replay files");
            Check(File.Exists(ReplayPath(directory, futureOlder)), "Unsupported older replay files must also remain available for a newer reader");
        }

        private static void ExplicitDeletionStaysOrdered(string directory)
        {
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 1;
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            var removed = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
            var retained = StoredAttempt(DateTime.UtcNow);
            store.AddImported(removed);
            store.Delete(removed.Id);
            WaitForStorage(store, "writes");
            Check(!File.Exists(ReplayPath(directory, removed)), "Explicit delete must remove even the only durable replay after its pending write");
            store.AddImported(removed);
            store.Clear();
            store.AddImported(retained);
            WaitForStorage(store, "writes");
            Check(!File.Exists(ReplayPath(directory, removed)) && File.Exists(ReplayPath(directory, retained)),
                "Clear must run after earlier writes and before later writes");
            Check(store.Attempts.Count == 1 && store.Attempts[0].Id == retained.Id, "Clear must preserve only later in-memory imports");
        }

        private static void ReducedRetentionTrimsDurableFiles(string directory)
        {
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 3;
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            var first = StoredAttempt(DateTime.UtcNow.AddMinutes(-2));
            var second = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
            var newest = StoredAttempt(DateTime.UtcNow);
            store.AddImported(first); store.AddImported(second); store.AddImported(newest);
            WaitForStorage(store, "writes");
            Check(Directory.GetFiles(Path.Combine(directory, "replays"), "*.json").Length == 3, "Initial retention must keep all three durable replays");
            Plugin.Config.ReplayRetention = 1;
            Plugin.Framework.Tick(); WaitForStorage(store, "writes");
            Check(store.Attempts.Count == 1 && store.Attempts[0].Id == newest.Id &&
                File.Exists(ReplayPath(directory, newest)) && !File.Exists(ReplayPath(directory, first)) && !File.Exists(ReplayPath(directory, second)),
                "Lower retention must trim both memory and durable storage without waiting for another pull");
        }

        private static void DeletedLoadingReplayStaysDeleted(string directory)
        {
            Directory.CreateDirectory(Path.Combine(directory, "replays"));
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 1;
            var attempt = StoredAttempt(DateTime.UtcNow);
            File.WriteAllText(ReplayPath(directory, attempt), JsonConvert.SerializeObject(attempt, PlanJson.Compact()));
            using var store = new ReplayStore();
            store.Delete(attempt.Id);
            WaitForStorage(store, "loading"); Plugin.Framework.Tick(); WaitForStorage(store, "writes");
            Check(store.Attempts.Count == 0 && !File.Exists(ReplayPath(directory, attempt)),
                "An explicit deletion before load results merge must not resurrect the replay in memory");
        }

        private static void EvidenceEditsPreserveRetentionOrder(string directory)
        {
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 2;
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            var oldest = StoredAttempt(DateTime.UtcNow.AddMinutes(-2));
            var recent = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
            var newest = StoredAttempt(DateTime.UtcNow);
            store.AddImported(oldest); store.AddImported(recent);
            store.SaveEvidence(oldest);
            store.AddImported(newest); WaitForStorage(store, "writes");
            Check(store.Attempts.Count == 2 && store.Attempts[0].Id == newest.Id && store.Attempts[1].Id == recent.Id &&
                !File.Exists(ReplayPath(directory, oldest)) && File.Exists(ReplayPath(directory, recent)) && File.Exists(ReplayPath(directory, newest)),
                "Editing old evidence must not make disk retention evict a different replay than memory retention");
        }

        private static void ReloadedEvidenceEditsPreserveRetentionOrder(string directory)
        {
            Directory.CreateDirectory(Path.Combine(directory, "replays"));
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 2;
            var oldest = StoredAttempt(DateTime.UtcNow.AddMinutes(-2));
            var recent = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
            var newest = StoredAttempt(DateTime.UtcNow);
            File.WriteAllText(ReplayPath(directory, recent), JsonConvert.SerializeObject(recent, PlanJson.Compact()));
            File.SetLastWriteTimeUtc(ReplayPath(directory, recent), DateTime.UtcNow.AddMinutes(-1));
            File.WriteAllText(ReplayPath(directory, oldest), JsonConvert.SerializeObject(oldest, PlanJson.Compact()));
            using var store = new ReplayStore();
            WaitForStorage(store, "loading"); Plugin.Framework.Tick();
            store.AddImported(newest); WaitForStorage(store, "writes");
            Check(store.Attempts.Count == 2 && store.Attempts[0].Id == newest.Id && store.Attempts[1].Id == recent.Id &&
                !File.Exists(ReplayPath(directory, oldest)) && File.Exists(ReplayPath(directory, recent)) && File.Exists(ReplayPath(directory, newest)),
                "Reloading an edited older replay must preserve the same retention order in memory and storage");
        }

        private static void ImportsDuringLoadPreserveRetentionOrder(string directory)
        {
            Directory.CreateDirectory(Path.Combine(directory, "replays"));
            Plugin.PluginInterface.Directory = directory;
            Plugin.Config.ReplayRetention = 1;
            var previous = StoredAttempt(DateTime.UtcNow);
            var imported = StoredAttempt(DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(ReplayPath(directory, previous), JsonConvert.SerializeObject(previous, PlanJson.Compact()));
            using var store = new ReplayStore();
            store.AddImported(imported);
            WaitForStorage(store, "loading"); Plugin.Framework.Tick(); WaitForStorage(store, "writes");
            Check(store.Attempts.Count == 1 && store.Attempts[0].Id == imported.Id &&
                File.Exists(ReplayPath(directory, imported)) && !File.Exists(ReplayPath(directory, previous)),
                "An import before startup results merge must use the same retention order as an import after loading");
        }
    }
}

namespace Shikari.Services.Replay { public sealed class LocalEvidenceCapture { public void Capture(ReplayAttempt a, float t) { } public void Invalidate(ReplayAttempt a, float t) { } } }
