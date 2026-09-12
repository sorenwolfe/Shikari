using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static partial class ReplayIntegration
{
    public static void RunLibrary(string directory)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        var saved = Enumerable.Range(0, 8).Select(i => StoredAttempt(DateTime.UtcNow.AddMinutes(-i))).ToArray();
        foreach (var item in saved) File.WriteAllText(ReplayPath(directory, item), JsonConvert.SerializeObject(item, PlanJson.Compact()));
        using var store = new ReplayStore();
        WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        Check(store.Catalog.Count == 8 && store.Attempts.Count == 0, "Startup must retain catalog metadata without retaining full replay graphs.");
        Check(store.GetLoaded(saved[0].Id) == null, "Catalog entries must not become fake replay payloads.");
        var revision = store.EvidenceRevision;
        ReplayAttempt Load(string id)
        {
            store.RequestLoad(id);
            Check(SpinWait.SpinUntil(() => { Plugin.Framework.Tick(); return store.GetLoaded(id) != null || store.RequestLoad(id) == ReplayLoadState.Failed; }, 5000), "Load did not finish.");
            return store.GetLoaded(id) ?? throw new Exception(store.LoadError(id));
        }
        Load(saved[0].Id); Load(saved[1].Id);
        store.SetReviewSelection(saved[0].Id, saved[1].Id);
        for (var i = 2; i < saved.Length; i++)
        {
            Load(saved[i].Id);
            Check(store.Attempts.Count <= 3 && store.GetLoaded(saved[0].Id) != null && store.GetLoaded(saved[1].Id) != null,
                "A rolling coverage read must retain the selected pair and bound other loaded payloads.");
        }
        Check(store.Catalog.Count == 8 && store.EvidenceRevision == revision, "Cache activity must not alter retention or logical evidence revision.");
        store.SetReviewSelection(null, null);
        var reloaded = Load(saved[2].Id);
        Check(reloaded.Id == saved[2].Id && reloaded.Plan.Id == saved[2].Plan.Id, "An evicted recording must remain loadable with its actual plan.");
        var pending = saved[3].Id;
        store.RequestLoad(pending); store.Delete(pending);
        WaitForStorage(store, "writes");
        for (var i = 0; i < 20; i++) { Plugin.Framework.Tick(); Thread.Sleep(5); }
        Check(!store.Catalog.Any(e => e.Id == pending) && store.GetLoaded(pending) == null, "A late load must not resurrect a deleted recording.");
        var bad = saved[4].Id;
        for (var i = 5; i < 8; i++) Load(saved[i].Id);
        File.WriteAllText(ReplayPath(directory, saved[4]), "{ damaged"); store.RequestLoad(bad);
        Check(SpinWait.SpinUntil(() => { Plugin.Framework.Tick(); return store.RequestLoad(bad) == ReplayLoadState.Failed; }, 5000), "A damaged lazy payload must fail visibly.");
        Check(store.GetLoaded(bad) == null && store.LoadError(bad).Length > 0 && File.Exists(ReplayPath(directory, saved[4])), "Unreadable recording must stay on disk with an explicit load error.");
        CheckCoalescedSaves(store, Load(saved[7].Id), directory);
        MeasureReplayCapture();
        Console.WriteLine("PASS: catalog-only startup, explicit validated loading, rolling bounded cache, selected-pair pins, eviction/reload, stable evidence revisions and late-load guards");
    }

    private static void CheckCoalescedSaves(ReplayStore store, ReplayAttempt replay, string directory)
    {
        void Block(Action edit)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            typeof(ReplayStore).GetMethod("Queue", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(store,
                new object[] { (Action)(() => { entered.Set(); Check(release.Wait(5000), "Save barrier was not released."); }) });
            Check(entered.Wait(5000), "Save barrier did not start.");
            try { edit(); } finally { release.Set(); }
            WaitForStorage(store, "writes"); Plugin.Framework.Tick();
        }
        Block(() =>
        {
            replay.EndReason = "First edit"; store.SaveEvidence(replay);
            replay.EndReason = "Latest captured edit"; store.SaveEvidence(replay);
            var queued = (System.Collections.IDictionary)typeof(ReplayStore)
                .GetField("queuedSaves", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
            Check(queued.Count == 1, "Successive pending metadata edits must retain one queued snapshot.");
            replay.EndReason = "Later unsubmitted mutation";
        });
        var durable = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(ReplayPath(directory, replay)))!;
        Check(durable.EndReason == "Latest captured edit" && store.UnsavedCount == 0,
            "Coalesced save must persist the latest detached capture and acknowledge its revision.");
        Block(() =>
        {
            replay.EndReason = "Obsolete before delete"; store.SaveEvidence(replay);
            store.Delete(replay.Id);
            var replacement = ReplaySnapshot.Copy(replay);
            replacement.EndReason = "Explicit new import after delete";
            store.AddImported(replacement);
        });
        durable = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(ReplayPath(directory, replay)))!;
        Check(durable.EndReason == "Explicit new import after delete" && store.UnsavedCount == 0,
            "A queued pre-delete save must not consume a post-delete generation's snapshot before its deletion barrier.");
    }

    private static void MeasureReplayCapture()
    {
        var replay = new ReplayAttempt { Plan = PlanDocument.CreateDefault(), Duration = 600 };
        for (var frameIndex = 0; frameIndex < 6000; frameIndex++)
        {
            var frame = new ReplayFrame { Time = frameIndex / 10f, Valid = true, BoardPerYalm = .025f };
            for (var seat = 0; seat < 8; seat++)
            {
                frame.Players.Add(new ReplayPlayer { Name = "Player " + seat, SlotIndex = seat, JobId = 24,
                    Board = new Vector2(.25f + seat * .01f, .75f), IsLocal = seat == 0 });
                replay.Evidence.Positions.Add(new EvidencePosition { Time = frame.Time, ActorId = seat + 1,
                    Position = new Vector2(90 + seat, 110) });
            }
            replay.Frames.Add(frame);
        }
        Check(ReplayValidation.IsValid(replay), "Performance fixture must be a usable replay.");
        (long Bytes, double Ms) Measure(Func<object> capture)
        {
            GC.KeepAlive(capture()); GC.KeepAlive(capture());
            var start = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 3; i++)
            {
                Check(ReplayValidation.IsValid(replay), "Validation changed during measurement.");
                GC.KeepAlive(capture());
            }
            return ((GC.GetAllocatedBytesForCurrentThread() - start) / 3, watch.Elapsed.TotalMilliseconds / 3);
        }
        var legacy = Measure(() => JsonConvert.SerializeObject(replay, PlanJson.Compact()));
        var detached = Measure(() => ReplaySnapshot.Copy(replay));
        Check(detached.Bytes < legacy.Bytes, "Typed caller capture should allocate less than caller JSON encoding for a long replay.");
        Console.WriteLine($"MEASURE: 6000 frames / 48000 positions; validation + caller JSON {legacy.Bytes:N0} B / {legacy.Ms:0.00} ms; validation + typed capture {detached.Bytes:N0} B / {detached.Ms:0.00} ms. Worker encoding is excluded.");
    }
}
