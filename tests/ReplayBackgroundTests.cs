using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static partial class ReplayIntegration
{
    private static void RunBackground(string directory)
    {
        foreach (var scenario in new[] { "normal", "edit", "switch", "clear", "delete", "new-pull", "import", "disk-failure", "unload" })
            BackgroundCompletion(Path.Combine(directory, scenario), scenario);
        BackgroundRetentionEdit(Path.Combine(directory, "pending-retention"));
        BackgroundTimeOrigin(Path.Combine(directory, "time-origin"));
        BackgroundStorageError(Path.Combine(directory, "later-error"));
        Console.WriteLine("PASS: detached finalization, framework-only publication, stale edits/switches/new pulls, ordered clear/delete/import, failed disk recovery and unload");
    }

    private static void BackgroundTimeOrigin(string directory)
    {
        Plugin.PluginInterface.Directory = directory;
        Plugin.Plans.Active = PlanDocument.CreateDefault();
        using var store = new ReplayStore();
        WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        // Simulate earlier CombatStarted subscribers taking time before replay capture starts.
        Plugin.Encounter.CombatElapsed = 5;
        try
        {
            Plugin.Encounter.Begin(); Plugin.Framework.Tick();
            var buffer = (ReplayBuffer)typeof(ReplayStore).GetField("buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            buffer.AddCast(RecordedCast.FromLive(123, 1, 4, 2, new CastStartContext { ObservedTime = 5 }));
            Plugin.Adaptive.Emit();
            Plugin.Encounter.End(); CompleteQueued(store);
            var attempt = store.Attempts[0];
            Check(attempt.Casts.Count == 1, "Startup delay must not clip casts observed before recording ended");
            Check(attempt.Frames[0].Time >= 5 && attempt.StatusObservations[0].Time >= 5 &&
                attempt.AdaptiveDecisions[0].Time >= 5 && attempt.Duration >= 5,
                "Frames, statuses, decisions and duration must share the encounter cast time origin");
        }
        finally { Plugin.Encounter.CombatElapsed = 0; }
    }

    private static void BackgroundRetentionEdit(string directory)
    {
        Plugin.PluginInterface.Directory = directory;
        Plugin.Config.ReplayRetention = 1;
        Plugin.Plans.Active = PlanDocument.CreateDefault();
        using var store = new ReplayStore();
        WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        var older = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
        store.AddImported(older); WaitForStorage(store, "writes");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        typeof(ReplayStore).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store,
            new object[] { (Action)(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }) });
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Worker did not block for retention test");
        try
        {
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
            Check(store.Attempts.Count == 1 && store.Attempts[0].Id == older.Id, "Older replay remains available during processing");
            older.Evidence.CalibrationSlideId = older.Plan.Slides[0].Id;
            store.SaveEvidence(older);
        }
        finally { release.Set(); }
        CompleteQueued(store);
        Check(store.Attempts.Count == 1 && store.Attempts[0].Id != older.Id, "Newest recording should remain visible at retention one");
        Check(File.Exists(ReplayPath(directory, store.Attempts[0])), "Editing an older visible replay must not delete the pending newer recording");
        Check(!File.Exists(ReplayPath(directory, older)), "The older metadata edit must retain its original retention priority");
        Plugin.Config.ReplayRetention = 10;
    }

    private static void BackgroundStorageError(string directory)
    {
        Plugin.PluginInterface.Directory = directory;
        Plugin.Plans.Active = PlanDocument.CreateDefault();
        using var store = new ReplayStore();
        WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast(); Plugin.Encounter.End();
        WaitForStorage(store, "writes");
        var newer = StoredAttempt(DateTime.UtcNow);
        Directory.CreateDirectory(ReplayPath(directory, newer));
        store.AddImported(newer); WaitForStorage(store, "writes");
        Check(store.Status.Contains("storage", StringComparison.OrdinalIgnoreCase), "Failed import must report its storage error");
        Plugin.Framework.Tick();
        Check(store.Status.Contains("storage", StringComparison.OrdinalIgnoreCase), "Older completion must not hide a newer save failure");
        var unrelated = StoredAttempt(DateTime.UtcNow);
        store.AddImported(unrelated); WaitForStorage(store, "writes");
        Check(store.Status.Contains("storage", StringComparison.OrdinalIgnoreCase), "An unrelated successful save cannot clear the failed import warning");
        Directory.Delete(ReplayPath(directory, newer));
        store.SaveEvidence(newer); WaitForStorage(store, "writes");
        Check(!store.Status.Contains("storage", StringComparison.OrdinalIgnoreCase), "Successful retry must clear its resolved storage warning");
        var retry = StoredAttempt(DateTime.UtcNow);
        Directory.CreateDirectory(ReplayPath(directory, retry));
        store.AddImported(retry); WaitForStorage(store, "writes");
        Directory.Delete(ReplayPath(directory, retry));
        store.SaveEvidence(retry); WaitForStorage(store, "writes");
        Check(!store.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase), "A direct retry must clear the fallback error without an intervening completion");
        var cleared = StoredAttempt(DateTime.UtcNow);
        Directory.CreateDirectory(ReplayPath(directory, cleared));
        store.AddImported(cleared); WaitForStorage(store, "writes");
        Directory.Delete(ReplayPath(directory, cleared));
        store.Clear(); WaitForStorage(store, "writes");
        Check(!store.Status.Contains("needs attention", StringComparison.OrdinalIgnoreCase), "Clearing the library resolves its old save warnings");
    }

    private static void BackgroundCompletion(string directory, string scenario)
    {
        Plugin.PluginInterface.Directory = directory;
        Plugin.Config.ReplayRetention = 10;
        Plugin.Plans.SaveSucceeds = true;
        Plugin.Plans.Saves = 0;
        var plan = PlanDocument.CreateDefault();
        plan.Slides[0].Title = "Observed cast";
        Plugin.Plans.Active = plan;
        using var store = new ReplayStore();
        WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        typeof(ReplayStore).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store,
            new object[] { (Action)(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Blocked worker timed out"); }) });
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Worker did not reach controlled block");
        ReplayAttempt? imported = null;
        string id;
        try
        {
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(); Plugin.Encounter.Cast();
            var buffer = (ReplayBuffer)typeof(ReplayStore).GetField("buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            id = buffer.Attempt.Id;
            Plugin.Encounter.End();
            Check(!store.Recording, "Ending a pull must immediately release capture ownership");
            Check(store.Attempts.Count == 0 && Plugin.Plans.Saves == 0,
                "Finishing must defer validation, enrichment and publication while its worker is blocked");
            if (scenario == "edit") plan.Notes = "User edited while processing";
            if (scenario == "switch") Plugin.Plans.Active = PlanDocument.CreateDefault();
            if (scenario == "clear") store.Clear();
            if (scenario == "delete") store.Delete(id);
            if (scenario == "new-pull") { Plugin.Encounter.Begin(); Plugin.Framework.Tick(); }
            if (scenario == "import") { imported = StoredAttempt(DateTime.UtcNow); store.AddImported(imported); }
            if (scenario == "disk-failure") Directory.CreateDirectory(Path.Combine(directory, "replays", id + ".json"));
            if (scenario == "unload")
            {
                // Keep finalization blocked until real teardown starts, then allow its bounded
                // drain to persist the capture. No framework update may apply the late result.
                var disposing = Task.Run(() =>
                {
                    var field = typeof(ReplayStore).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Check(SpinWait.SpinUntil(() => (bool)field.GetValue(store)!, TimeSpan.FromSeconds(10)), "Teardown did not begin");
                    release.Set();
                });
                store.Dispose();
                disposing.GetAwaiter().GetResult();
            }
        }
        finally { release.Set(); }
        WaitForStorage(store, "writes");
        Check(Plugin.Plans.Saves == 0, "Worker must not call mutable plan services");
        if (scenario == "unload") store.Dispose();
        Plugin.Framework.Tick();
        if (scenario is "clear" or "delete")
        {
            Check(store.Attempts.Count == 0, "Deleted pending results must not return to the library");
            Check(!File.Exists(Path.Combine(directory, "replays", id + ".json")), "Pending storage must respect ordered deletion");
        }
        else if (scenario != "unload")
        {
            Check(store.Attempts.Count >= 1, "A completed recording should become reviewable on the next framework update");
            if (imported != null) Check(store.Attempts[0].Id == imported.Id, "A late result must not jump ahead of a newer import");
        }
        if (scenario is "normal" or "import")
            Check(plan.StrategyEvidence.Count == 1 && Plugin.Plans.Saves == 1, "Unchanged plan must adopt its prepared evidence once");
        else
            Check(plan.StrategyEvidence.Count == 0 && Plugin.Plans.Saves == 0, "A stale, deleted, failed or unloading result must not edit the strategy");
        if (scenario == "disk-failure")
        {
            Check(store.Status.Contains("storage", StringComparison.OrdinalIgnoreCase), "A failed disk write must remain visible");
            Directory.Delete(Path.Combine(directory, "replays", id + ".json"));
            store.SaveEvidence(store.Attempts[0]); WaitForStorage(store, "writes");
        }
        if (scenario is not ("clear" or "delete"))
        {
            var saved = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(Path.Combine(directory, "replays", id + ".json")), Services.PlanJson.Compact())!;
            Check(saved.Id == id && saved.Mechanics.Count == 1, "Detached recording must reach durable storage");
            Check(saved.Mechanics[0].SlideId == plan.Slides[0].Id, "The persisted recording must contain its captured strategy links");
        }
    }
}
