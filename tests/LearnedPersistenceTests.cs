using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Storage;

namespace Shikari.Tests;

public static class LearnedPersistenceTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static FightMemory Memory(uint id = 1, string name = "Original")
    {
        var memory = new FightMemory { TerritoryId = id, Name = name, PullCount = 4, ClearCount = 2,
            LongestPullSeconds = 120, LastSeenUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) };
        memory.Casts.Add(new LearnedCast { ActionId = 50, Name = "Cast", Occurrence = 2, CastBarSeconds = 3,
            Median = 12, Deviation = 1, PullsSeen = 4, Samples = new() { 10, 12, 14 } });
        return memory;
    }
    private static FightMemory Read(string path) => JsonConvert.DeserializeObject<FightMemory>(File.ReadAllText(path), PlanJson.Readable())!;
    private static void Flush(LearnedPersistenceQueue queue) => Check(queue.FlushAsync(Limit).GetAwaiter().GetResult(), "Queue did not drain successfully.");

    public static void Run(string root)
    {
        Snapshot();
        Coalescing(Path.Combine(root, "coalescing"));
        foreach (var readd in new[] { false, true }) Ordering(Path.Combine(root, "ordering" + readd), readd);
        FailedWrite(Path.Combine(root, "failure"));
        Shutdown(Path.Combine(root, "shutdown"));
        ReloadOrdering(Path.Combine(root, "reload"));
        ShutdownBeforeDirectoryOwnership(Path.Combine(root, "waiting-shutdown"));
        Console.WriteLine("PASS: detached learned history, coalesced ordered writes, deletion barriers, atomic failure/retry, bounded shutdown and cancellation");
    }
    private static void Snapshot()
    {
        var source = Memory(); var before = JsonConvert.SerializeObject(source, PlanJson.Readable());
        var copy = LearnedPersistenceQueue.Capture(source);
        source.Name = "Changed"; source.Casts[0].Samples[0] = 999; source.Casts.Clear();
        Check(JsonConvert.SerializeObject(copy, PlanJson.Readable()) == before, "Snapshot lost fields or retained caller-owned timing data.");
        foreach (var corrupt in new Action<FightMemory>[] { m => m.FormatVersion++, m => m.TerritoryId = 0,
            m => m.Casts = null!, m => m.Casts[0].Samples = null!, m => m.Casts[0].Samples.AddRange(Enumerable.Repeat(1f, 24)),
            m => m.Casts[0].Samples[0] = float.NaN, m => m.LongestPullSeconds = float.PositiveInfinity })
        {
            source = Memory(); corrupt(source); var rejected = false;
            try { LearnedPersistenceQueue.Capture(source); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unsupported or malformed learned history must not overwrite durable history.");
        }
    }
    private static void Coalescing(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var written = new List<string>(); var owner = Environment.CurrentManagedThreadId;
        var queue = new LearnedPersistenceQueue(directory, (path, json) => {
            Check(Environment.CurrentManagedThreadId != owner, "JSON persistence must run off the caller thread.");
            if (written.Count == 0) { entered.Set(); Check(release.Wait(Limit), "Writer not released."); }
            written.Add(json); AtomicFile.WriteAllText(path, json);
        });
        queue.Save(Memory()); Check(entered.Wait(Limit), "Writer did not start.");
        try
        {
            queue.Save(Memory(name: "Superseded"));
            var latest = Memory(name: "Newest"); queue.Save(latest); latest.Name = "Mutation"; latest.Casts[0].Samples[0] = 999;
            Check(queue.IsSaving && written.Count == 0, "Caller waited for a blocked disk write.");
        }
        finally { release.Set(); }
        Flush(queue);
        Check(written.Count == 2 && Read(Path.Combine(directory, "1.json")).Name == "Newest" &&
            Read(Path.Combine(directory, "1.json")).Casts[0].Samples[0] == 10, "Pending saves failed to coalesce or leaked mutations.");
        var results = new List<LearnedSaveResult>(); while (queue.TryTakeCompletion(out var result)) results.Add(result);
        Check(results.Count(r => r.Outcome == LearnedSaveOutcome.Saved) == 2 &&
            results.Single(r => r.Outcome == LearnedSaveOutcome.Superseded).Revision == 2, "Revision acknowledgements lost the superseded request.");
    }
    private static void Ordering(string directory, bool readd)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var operations = new List<string>();
        var queue = new LearnedPersistenceQueue(directory, (path, json) => {
            if (operations.Count == 0) { entered.Set(); Check(release.Wait(Limit), "Writer not released."); }
            operations.Add(JsonConvert.DeserializeObject<FightMemory>(json)!.Name); AtomicFile.WriteAllText(path, json);
        }, path => { operations.Add("delete"); File.Delete(path); });
        queue.Save(Memory()); Check(entered.Wait(Limit), "Writer did not start.");
        try { queue.Save(Memory(name: "Before delete")); queue.Delete(1); if (readd) queue.Save(Memory(name: "After delete")); }
        finally { release.Set(); }
        Flush(queue);
        Check(operations.SequenceEqual(readd ? new[] { "Original", "Before delete", "delete", "After delete" } :
            new[] { "Original", "Before delete", "delete" }), "Delete did not separate save generations.");
        Check(readd ? Read(Path.Combine(directory, "1.json")).Name == "After delete" : !File.Exists(Path.Combine(directory, "1.json")),
            "A stale save resurrected forgotten learned history.");
    }
    private static void FailedWrite(string directory)
    {
        var queue = new LearnedPersistenceQueue(directory); queue.Save(Memory()); Flush(queue);
        var path = Path.Combine(directory, "1.json"); var before = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            queue.Save(Memory(name: "Replacement"));
            Check(!queue.FlushAsync(Limit).GetAwaiter().GetResult() && queue.LastError != null, "Failed atomic replacement must stay visible.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Failed atomic replacement destroyed the last readable history.");
        }
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed replacement left a temporary file.");
        queue.Save(Memory(2, "Unrelated")); queue.FlushAsync(Limit).GetAwaiter().GetResult();
        Check(queue.LastError != null, "An unrelated territory save hid the failure.");
        queue.Save(Memory(name: "Retry")); Flush(queue);
        Check(queue.LastError == null && Read(path).Name == "Retry", "Successful retry did not recover failed storage.");
        var deleteFailure = true;
        var deleting = new LearnedPersistenceQueue(directory, delete: p => { if (deleteFailure) throw new IOException("Injected delete failure"); File.Delete(p); });
        deleting.Delete(1); Check(!deleting.FlushAsync(Limit).GetAwaiter().GetResult() && File.Exists(path), "Failed delete was hidden.");
        deleteFailure = false; deleting.Delete(1); Flush(deleting); Check(!File.Exists(path), "Delete retry did not recover.");
    }
    private static void Shutdown(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writes = 0;
        var queue = new LearnedPersistenceQueue(directory, (path, json) => { entered.Set(); Check(release.Wait(Limit), "Writer not released."); writes++; AtomicFile.WriteAllText(path, json); });
        queue.Save(Memory()); Check(entered.Wait(Limit), "Writer did not start."); queue.Save(Memory(2));
        var watch = Stopwatch.StartNew();
        try
        {
            Check(!queue.FlushAsync(TimeSpan.FromMilliseconds(20), stopAccepting: true, discardPending: true).GetAwaiter().GetResult() &&
                watch.Elapsed < TimeSpan.FromSeconds(1), "Shutdown did not respect the bounded drain timeout.");
            var rejected = false; try { queue.Save(Memory()); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Shutdown accepted another write.");
        }
        finally { release.Set(); }
        Flush(queue); Check(writes == 1 && !File.Exists(Path.Combine(directory, "2.json")), "Startup rollback persisted pending history.");
    }

    private static void ReloadOrdering(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var oldWrites = new List<string>(); using var newerEntered = new ManualResetEventSlim();
        var old = new LearnedPersistenceQueue(directory, (path,json) => {
            oldWrites.Add(JsonConvert.DeserializeObject<FightMemory>(json)!.Name);
            entered.Set(); Check(release.Wait(Limit), "Old-generation writer not released."); AtomicFile.WriteAllText(path,json);
        });
        old.Save(Memory(name:"Old active")); Check(entered.Wait(Limit), "Old-generation writer did not start.");
        old.Save(Memory(name:"Old queued"));
        Check(!old.FlushAsync(TimeSpan.FromMilliseconds(20),stopAccepting:true).GetAwaiter().GetResult(), "Blocked shutdown must return within its deadline.");
        var newer = new LearnedPersistenceQueue(directory, (path,json) => { newerEntered.Set(); AtomicFile.WriteAllText(path,json); });
        try
        {
            newer.Save(Memory(name:"New generation"));
            Check(!newerEntered.Wait(100), "A reloaded writer must not bypass the old in-flight directory operation.");
        }
        finally { release.Set(); }
        Flush(newer); old.FlushAsync(Limit).GetAwaiter().GetResult();
        Check(oldWrites.SequenceEqual(new[] { "Old active" }) && Read(Path.Combine(directory,"1.json")).Name=="New generation",
            "Timed-out queued old snapshots must not run after a new generation writes.");
        Check(old.LastError != null, "Discarded shutdown snapshots must retain a visible storage failure.");
    }

    private static void ShutdownBeforeDirectoryOwnership(string directory)
    {
        using var ownerEntered = new ManualResetEventSlim(); using var releaseOwner = new ManualResetEventSlim();
        var owner = new LearnedPersistenceQueue(directory,(path,json)=> {
            ownerEntered.Set(); Check(releaseOwner.Wait(Limit),"Directory owner not released."); AtomicFile.WriteAllText(path,json);
        });
        owner.Save(Memory(name:"Directory owner")); Check(ownerEntered.Wait(Limit),"Directory owner did not start.");
        var obsoleteWrites=0;
        var obsolete = new LearnedPersistenceQueue(directory,(path,json)=> { obsoleteWrites++; AtomicFile.WriteAllText(path,json); });
        var latest = new LearnedPersistenceQueue(directory);
        try
        {
            obsolete.Save(Memory(name:"Obsolete waiting generation"));
            Check(!obsolete.FlushAsync(TimeSpan.FromMilliseconds(50),stopAccepting:true).GetAwaiter().GetResult(),
                "A writer waiting for directory ownership must obey its shutdown deadline.");
            latest.Save(Memory(name:"Latest generation"));
        }
        finally { releaseOwner.Set(); }
        Flush(owner); obsolete.FlushAsync(Limit).GetAwaiter().GetResult(); Flush(latest);
        Check(obsoleteWrites==0 && obsolete.LastError!=null && Read(Path.Combine(directory,"1.json")).Name=="Latest generation",
            "An old operation that never owned the directory must be canceled even if it already left its queue.");
    }
}
