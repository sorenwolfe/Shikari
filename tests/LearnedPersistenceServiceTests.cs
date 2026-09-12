using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Storage;
using Dalamud.Plugin.Services;

namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Lumina.Excel.Sheets
{
    public struct FakeText { public string ExtractText() => ""; }
    public struct FakePlace { public FakeText Name { get; set; } }
    public struct FakeLink { public FakePlace? ValueNullable { get; set; } }
    public struct TerritoryType { public FakeLink ContentFinderCondition { get; set; } public FakeLink PlaceName { get; set; } }
}
namespace Shikari
{
    public static class Plugin
    {
        public static Tests.LearnerInterface PluginInterface = new();
        public static Tests.LearnerClient ClientState = new();
        public static Tests.LearnerEncounter Encounter = new();
        public static Tests.LearnerFramework Framework = new();
        public static Tests.LearnerConfig Config = new();
        public static Tests.LearnerLog Log = new();
        public static Tests.LearnerData DataManager = new();
    }
}
namespace Shikari.Services
{
    public sealed class CastEvent { public uint ActionId { get; init; } public int Occurrence { get; init; }
        public string ActionName { get; init; } = "Cast"; public float CombatTime { get; init; } public float TotalCastTime { get; init; } }
}
namespace Shikari.Tests
{
    public static class LearnerProbe
    {
        public static int Added, FailAt;
        public static void Add() { if (++Added == FailAt) throw new IOException("Injected subscription failure"); }
    }
    public sealed class LearnerInterface { public string Directory = ""; public string GetPluginConfigDirectory() => Directory; }
    public sealed class LearnerClient
    {
        private Action<uint>? changed; public uint TerritoryType = 777;
        public event Action<uint> TerritoryChanged { add { changed += value; LearnerProbe.Add(); } remove { changed -= value; } }
        public int Hooks => changed?.GetInvocationList().Length ?? 0;
    }
    public sealed class LearnerEncounter
    {
        private Action? started, ended; private Action<CastEvent>? cast;
        public event Action CombatStarted { add { started += value; LearnerProbe.Add(); } remove { started -= value; } }
        public event Action CombatEnded { add { ended += value; LearnerProbe.Add(); } remove { ended -= value; } }
        public event Action<CastEvent> CastStarted { add { cast += value; LearnerProbe.Add(); } remove { cast -= value; } }
        public int Hooks => (started?.GetInvocationList().Length ?? 0) + (ended?.GetInvocationList().Length ?? 0) + (cast?.GetInvocationList().Length ?? 0);
        public void Begin() => started?.Invoke(); public void End() => ended?.Invoke(); public void Cast(float time) => cast?.Invoke(new() { ActionId = 50, Occurrence = 1, CombatTime = time });
    }
    public sealed class LearnerFramework : IFramework
    {
        private Action<IFramework>? update;
        public event Action<IFramework> Update { add { update += value; LearnerProbe.Add(); } remove { update -= value; } }
        public int Hooks => update?.GetInvocationList().Length ?? 0;
        public void Poll() => update?.Invoke(this);
    }
    public sealed class LearnerConfig { public bool LearningEnabled = true; }
    public sealed class LearnerLog
    {
        public List<string> Errors = new();
        public void Error(Exception ex, string text, params object[] args) => Errors.Add(text + ex.Message);
        public void Information(string text, params object[] args) { }
        public void Warning(Exception ex, string text, params object[] args) => Errors.Add(text + ex.Message);
        public void Warning(string text, params object[] args) => Errors.Add(text);
    }
    public sealed class LearnerData { public LearnerSheet<T> GetExcelSheet<T>() where T : struct => new(); }
    public sealed class LearnerSheet<T> where T : struct { public bool TryGetRow(uint id, out T row) { row = default; return false; } }
    public static class LearnedPersistenceServiceTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Setup(string directory)
        {
            Directory.CreateDirectory(directory);
            Plugin.PluginInterface = new() { Directory = directory }; Plugin.ClientState = new(); Plugin.Encounter = new();
            Plugin.Framework = new(); Plugin.Config = new(); Plugin.Log = new(); LearnerProbe.Added = LearnerProbe.FailAt = 0;
            var memory = new FightMemory { TerritoryId = 777, Name = "Original" };
            memory.GetOrAdd(50, 1, "Cast").AddSample(20, 3);
            AtomicFile.WriteAllText(Path.Combine(directory, "777.json"), JsonConvert.SerializeObject(memory, PlanJson.Readable()));
        }
        private static void Settled(EncounterLearner learner)
        {
            Check(SpinWait.SpinUntil(() => !learner.IsSaving, 5000), "Learner persistence did not settle.");
            Plugin.Framework.Poll();
        }
        private static int Hooks => Plugin.Encounter.Hooks + Plugin.ClientState.Hooks + Plugin.Framework.Hooks;
        private static string Name(string directory) => JsonConvert.DeserializeObject<FightMemory>(File.ReadAllText(Path.Combine(directory, "777.json")))!.Name;
        public static void Run(string directory)
        {
            SaveAndForget(Path.Combine(directory, "save"));
            FailAndRetry(Path.Combine(directory, "retry"));
            FailedForgetRetry(Path.Combine(directory, "forget-retry"));
            UnreadableRecovery(Path.Combine(directory, "unreadable"));
            MalformedHistoryRecovery(Path.Combine(directory, "malformed"));
            ReloadInitialization(Path.Combine(directory, "initialization"));
            ShutdownAndRollback(Path.Combine(directory, "shutdown"));
            foreach (var boundary in Enumerable.Range(1, 5))
            {
                var path = Path.Combine(directory, "startup" + boundary); Setup(path); LearnerProbe.FailAt = boundary;
                var failed = false;
                try { _ = new EncounterLearner(path); } catch (IOException) { failed = true; }
                Check(failed && Hooks == 0 && Name(path) == "Original", "Partial learner startup leaked a subscription or wrote history.");
            }
            Console.WriteLine("PASS: actual learner asynchronous saves, visible errors/retry, stale-forget guards, bounded shutdown and five constructor fault boundaries");
        }
        private static void SaveAndForget(string directory)
        {
            Setup(directory); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var first = true;
            using var learner = new EncounterLearner(directory, write: (path, json) => {
                if (first) { first = false; entered.Set(); Check(release.Wait(5000), "Writer not released."); }
                AtomicFile.WriteAllText(path, json);
            });
            Check(Hooks == 5, "Learner lifecycle did not install expected subscriptions.");
            learner.SaveAll(); Check(entered.Wait(5000), "Actual learner did not queue background persistence.");
            var memory = learner.Current!;
            try
            {
                memory.Name = "Newest"; learner.SaveAll(); memory.Name = "Mutation";
                Check(learner.IsSaving && Name(directory) == "Original", "SaveAll blocked on disk or exposed a torn save.");
            }
            finally { release.Set(); }
            Settled(learner); Check(Name(directory) == "Newest", "Caller mutations leaked into queued learned history.");
            learner.Forget(memory); learner.ForgetTimings(memory); learner.SaveAll(); Settled(learner);
            Check(learner.Current == null && !File.Exists(Path.Combine(directory, "777.json")), "A stale memory reference resurrected forgotten history.");
        }
        private static void FailAndRetry(string directory)
        {
            Setup(directory); var fail = true;
            using var learner = new EncounterLearner(directory, write: (path, json) => { if (fail) throw new IOException("Injected disk failure"); AtomicFile.WriteAllText(path, json); });
            learner.Current!.Name = "Replacement"; learner.SaveAll(); Settled(learner);
            Check(learner.StorageError != null && Plugin.Log.Errors.Count == 1 && Name(directory) == "Original", "A failed learned save was hidden or damaged history.");
            fail = false; learner.SaveAll(); Settled(learner);
            Check(learner.StorageError == null && Name(directory) == "Replacement", "SaveAll did not retry failed history.");
        }
        private static void ShutdownAndRollback(string directory)
        {
            Setup(directory);
            var rollback = new EncounterLearner(directory); rollback.Current!.Name = "Uncommitted"; rollback.Dispose(saveChanges: false); rollback.Dispose();
            Check(Hooks == 0 && Name(directory) == "Original", "Startup rollback or repeated dispose wrote history.");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var learner = new EncounterLearner(directory, write: (path, json) => { entered.Set(); Check(release.Wait(5000), "Writer not released."); AtomicFile.WriteAllText(path, json); },
                shutdownTimeout: TimeSpan.FromMilliseconds(20));
            learner.Current!.Name = "Last snapshot"; learner.SaveAll(); Check(entered.Wait(5000), "Writer did not start.");
            var watch = Stopwatch.StartNew();
            try { learner.Dispose(); Check(watch.Elapsed < TimeSpan.FromSeconds(1) && Hooks == 0, "Slow disk blocked shutdown or kept event subscriptions."); }
            finally { release.Set(); }
            Settled(learner); learner.SaveAll(); learner.Dispose();
            Check(Name(directory) == "Last snapshot", "Final detached history was lost after a bounded shutdown.");
        }
        private static void FailedForgetRetry(string directory)
        {
            Setup(directory); var fail = true;
            using var learner = new EncounterLearner(directory, delete: path => { if (fail) throw new IOException("Injected delete failure"); File.Delete(path); });
            learner.Forget(learner.Current!); Settled(learner);
            Check(learner.StorageError != null && learner.Current == null && File.Exists(Path.Combine(directory, "777.json")), "Failed forget should retain durable data and expose its failure.");
            fail = false; learner.SaveAll(); Settled(learner);
            Check(learner.StorageError == null && !File.Exists(Path.Combine(directory, "777.json")), "Retrying persistence must retry a failed deletion after its memory left the visible library.");
        }
        private static void UnreadableRecovery(string directory)
        {
            Setup(directory); var path = Path.Combine(directory, "777.json");
            var json = File.ReadAllText(path).Replace("\"TerritoryId\": 777", "\"FormatVersion\": 99, \"TerritoryId\": 777");
            File.WriteAllText(path, json);
            using var learner = new EncounterLearner(directory);
            Plugin.Encounter.Begin(); Plugin.Encounter.Cast(0); Plugin.Encounter.Cast(10); Plugin.Encounter.Cast(20); Plugin.Encounter.End();
            Settled(learner);
            Check(learner.StorageError != null && File.ReadAllText(path) == json, "A new pull must not overwrite an unreadable or future-format history retained for recovery.");
            learner.Forget(learner.Current!); Settled(learner);
            Check(!File.Exists(path), "Explicitly forgetting the territory should still remove its retained unreadable history.");
        }
        private static void MalformedHistoryRecovery(string root)
        {
            var examples = new[] {
                "{\"TerritoryId\":777,\"Casts\":null}",
                "{\"TerritoryId\":777,\"Casts\":[{\"ActionId\":50,\"Occurrence\":1,\"Samples\":null}]}",
                "{\"TerritoryId\":777,\"Casts\":[{\"ActionId\":50,\"Occurrence\":1,\"Samples\":[\"NaN\"]}]}"
            };
            for (var index=0;index<examples.Length;index++)
            {
                var directory=Path.Combine(root,index.ToString()); Setup(directory);
                var path=Path.Combine(directory,"777.json"); File.WriteAllText(path,examples[index]);
                var learner=new EncounterLearner(directory);
                Check(learner.Current==null && learner.StorageError!=null, "Malformed loaded history must fail before null-list normalization or recomputation.");
                Plugin.Encounter.Begin(); Plugin.Encounter.Cast(0); Plugin.Encounter.Cast(10); Plugin.Encounter.Cast(20); Plugin.Encounter.End();
                learner.SaveAll(); learner.Dispose();
                Check(File.ReadAllText(path)==examples[index], "Autosave or shutdown replaced malformed history retained for recovery.");
            }
        }

        private static void ReloadInitialization(string root)
        {
            foreach(var closeBeforeReady in new[] { false, true })
            {
                var directory=Path.Combine(root,closeBeforeReady.ToString()); Setup(directory);
                using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
                var old=new EncounterLearner(directory,write:(path,json)=> {
                    entered.Set(); Check(release.Wait(5000),"Old initialization writer was not released."); AtomicFile.WriteAllText(path,json);
                },shutdownTimeout:TimeSpan.FromMilliseconds(20));
                old.Current!.Name="Latest completed history"; old.Current.PullCount=7; old.Current.ClearCount=3;
                old.SaveAll(); Check(entered.Wait(5000),"Old writer did not start."); old.Dispose();
                var watch=Stopwatch.StartNew(); var newer=new EncounterLearner(directory);
                try
                {
                    Check(watch.Elapsed<TimeSpan.FromSeconds(1) && newer.IsLoading && newer.Current==null && !newer.All.Any(),
                        "Reload must defer reading an owned directory without blocking the caller or exposing stale history.");
                    newer.SaveAll(); newer.NoteClear(); Plugin.Encounter.Begin(); Plugin.Encounter.Cast(0);
                    if(closeBeforeReady) newer.Dispose();
                    release.Set(); Settled(old);
                    if(!closeBeforeReady)
                    {
                        Check(SpinWait.SpinUntil(()=> { Plugin.Framework.Poll(); return !newer.IsLoading; },5000),"Reload did not initialize after prior ownership ended.");
                        Check(newer.Current?.Name=="Latest completed history" && newer.Current.PullCount==7,
                            "Reload must read the old writer's final durable state, not its earlier snapshot.");
                        Plugin.Encounter.Cast(10); Plugin.Encounter.Cast(20); Plugin.Encounter.Cast(30); Plugin.Encounter.End(); newer.NoteClear();
                        Check(newer.Current!.PullCount==7 && newer.Current.ClearCount==3,
                            "A pull begun before initialization must not train a partial timeline or count as a clear.");
                        Plugin.Encounter.Begin(); Plugin.Encounter.Cast(0); Plugin.Encounter.Cast(10); Plugin.Encounter.Cast(20); Plugin.Encounter.End();
                        Check(newer.Current.PullCount==8,"Learning should resume normally on the next fully observed pull.");
                        newer.Dispose();
                    }
                    Check(Name(directory)=="Latest completed history",
                        "Disposing the replacement learner must not overwrite the old writer's newer history with stale initialization data.");
                }
                finally { release.Set(); old.Dispose(); newer.Dispose(); }
            }
        }
    }
}
