using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Storage;

namespace Shikari.Tests;

public static class PlanPersistenceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static PlanSaveResult Result(PlanSaveTicket ticket) => ticket.Completion.WaitAsync(Limit).GetAwaiter().GetResult();
    private static void Finish(PlanStore store)
    {
        Check(store.FlushAsync(Limit).GetAwaiter().GetResult(), "Storage did not finish successfully");
        store.Poll();
    }
    private static PlanDocument Read(string path) => JsonConvert.DeserializeObject<PlanDocument>(File.ReadAllText(path), PlanJson.Readable())!;

    public static void Run(string root)
    {
        DetachedGraph();
        CoalescedSnapshots(Path.Combine(root, "coalescing"));
        foreach (var operation in new[] { "save", "delete", "replace", "reimport" })
            ExplicitBarrier(Path.Combine(root, operation), operation);
        FailedWrite(Path.Combine(root, "failure"));
        InvalidId(Path.Combine(root, "invalid"));
        Shutdown(Path.Combine(root, "shutdown"));
        Console.WriteLine("PASS: detached complete plan graph, coalesced acknowledgements, explicit barriers, deletion/reimport, durable failure/retry and bounded shutdown");
    }

    // Omitting a newly added model member or sharing any nested object fails this fixture.
    private static void DetachedGraph()
    {
        var source = (PlanDocument)Filled(typeof(PlanDocument))!;
        var copy = PlanSnapshot.Copy(source);
        EqualDetached(source, copy, "plan");
        var expected = JsonConvert.SerializeObject(source, PlanJson.Readable());
        var snapshot = PlanSnapshot.Capture(source, source.ModifiedUtc);
        Mutate(source);
        Check(snapshot.Encode() == expected, "Nested source mutations leaked into the immutable snapshot");
        Check(JsonConvert.SerializeObject(copy, PlanJson.Readable()) == expected, "Copy changed after source mutation");
    }

    private static object? Filled(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) return Filled(nullable);
        if (type == typeof(string)) return Guid.NewGuid().ToString("N");
        if (type.IsEnum) return Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1);
        if (type == typeof(bool)) return true;
        if (type == typeof(DateTime)) return new DateTime(2024, 4, 3, 2, 1, 0, DateTimeKind.Utc);
        if (type == typeof(Vector2)) return new Vector2(.321f, .765f);
        if (type.IsPrimitive) return Convert.ChangeType(17, type);
        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            dictionary.Add(3, "seat three"); dictionary.Add(5, "seat five");
            return dictionary;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(Filled(type.GetGenericArguments()[0]));
            list.Add(Filled(type.GetGenericArguments()[0]));
            return list;
        }
        var result = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties().Where(p => p.CanWrite))
            property.SetValue(result, property.PropertyType == typeof(bool)
                ? !(bool)property.GetValue(result)! : Filled(property.PropertyType));
        return result;
    }

    private static void EqualDetached(object source, object copy, string path)
    {
        var type = source.GetType();
        if (type.IsValueType || source is string) { Check(source.Equals(copy), "Lost value at " + path); return; }
        Check(!ReferenceEquals(source, copy), "Shared mutable reference at " + path);
        if (source is IDictionary dictionary)
        {
            var other = (IDictionary)copy;
            Check(dictionary.Count == other.Count, "Lost dictionary entry at " + path);
            foreach (DictionaryEntry entry in dictionary) Check(Equals(entry.Value, other[entry.Key]), "Lost call text at " + path);
        }
        else if (source is IList list)
        {
            var other = (IList)copy;
            Check(list.Count == other.Count, "Lost list item at " + path);
            for (var i = 0; i < list.Count; i++) EqualDetached(list[i]!, other[i]!, path + "[" + i + "]");
        }
        else foreach (var property in type.GetProperties().Where(p => p.CanWrite))
            EqualDetached(property.GetValue(source)!, property.GetValue(copy)!, path + "." + property.Name);
    }

    private static void Mutate(object value)
    {
        if (value is IDictionary dictionary) { dictionary.Clear(); return; }
        if (value is IList list)
        {
            foreach (var item in list) if (item != null) Mutate(item);
            list.Clear(); return;
        }
        foreach (var property in value.GetType().GetProperties().Where(p => p.CanWrite))
        {
            if (property.PropertyType == typeof(string)) property.SetValue(value, "changed");
            else if (!property.PropertyType.IsValueType && property.GetValue(value) is { } child) Mutate(child);
        }
    }

    private static void CoalescedSnapshots(string root)
    {
        using var disk = new ControlledDisk();
        using var store = MakeStore(root, disk);
        var doc = store.Active!;
        var path = Path.Combine(root, doc.Id + ".json");
        var previousModified = doc.ModifiedUtc;
        disk.BlockNext();
        doc.Name = "in progress";
        var first = store.RequestSave(doc);
        disk.WaitEntered();
        try
        {
            Check(!first.Completion.IsCompleted, "A save reported success before the durable write");
            doc.Name = "outdated queued";
            var outdated = store.RequestSave(doc);
            doc.Name = "latest captured";
            doc.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.Freehand, Points = new() { new(.25f, .75f) } });
            doc.Timeline.Add(new TimelineEntry { SlotCallText = new() { [2] = "captured call" } });
            var latest = store.RequestSave(doc);
            Check(Result(outdated).Outcome == PlanSaveOutcome.Superseded, "A replaced queued save claimed durability");
            var pending = store.GetSaveState(doc.Id);
            Check(pending.IsSaving && pending.RequestedRevision == latest.Revision && pending.SavedRevision < first.Revision,
                "Superseded acknowledgements advanced the durable revision");
            Check(doc.ModifiedUtc == previousModified, "Request changed live metadata before successful persistence");
            doc.Name = "new unsaved edit";
            doc.Slides[0].Items[0].Points[0] = Vector2.Zero;
            doc.Timeline[0].SlotCallText[2] = "new edit";
            disk.Release();
            Check(Result(first).Outcome == PlanSaveOutcome.Saved && Result(latest).Outcome == PlanSaveOutcome.Saved,
                "Ordered saves did not finish");
            Check(doc.ModifiedUtc == previousModified, "Worker mutated live document metadata");
            Finish(store);
            var saved = Read(path);
            Check(saved.Name == "latest captured" && saved.Slides[0].Items[0].Points[0] == new Vector2(.25f, .75f) &&
                saved.Timeline[0].SlotCallText[2] == "captured call", "Worker persisted later live edits or stale autosave");
            Check(store.GetSaveState(doc.Id).SavedRevision == latest.Revision && !store.GetSaveState(doc.Id).IsSaving,
                "Latest durable revision was not acknowledged");
            Check(disk.Writes == 3, "Not-yet-started autosave was written instead of coalesced");
            Check(disk.Threads.All(t => t != Environment.CurrentManagedThreadId), "Durable writes ran on caller thread");
        }
        finally { disk.Release(); }
    }

    private static void ExplicitBarrier(string root, string action)
    {
        using var disk = new ControlledDisk();
        using var store = MakeStore(root, disk);
        var doc = store.Active!;
        var id = doc.Id;
        var path = Path.Combine(root, id + ".json");
        disk.BlockNext();
        doc.Name = "old in progress";
        var first = store.RequestSave(doc);
        disk.WaitEntered();
        doc.Name = "old queued";
        var queued = store.RequestSave(doc);
        // The releasing observer does not touch owner-thread documents or collections.
        var release = Task.Run(() =>
        {
            Check(SpinWait.SpinUntil(() => store.GetSaveState(id).RequestedRevision > queued.Revision, Limit), "Explicit operation did not queue a barrier");
            Check(!first.Completion.IsCompleted, "Worker gate released prematurely");
            disk.Release();
        });
        try
        {
            if (action == "save")
            {
                doc.Name = "explicit saved";
                Check(store.Save(doc), "Synchronous save did not return durable success");
            }
            else if (action == "replace")
                store.Import(new PlanDocument { Id = id, Name = "replacement", Slides = new() { new Slide() } }, true);
            else
            {
                store.Delete(doc);
                Check(!File.Exists(path), "Delete returned before older queued writes and deletion finished");
                if (action == "reimport")
                    store.Import(new PlanDocument { Id = id, Name = "reimported", Slides = new() { new Slide() } }, true);
            }
            release.WaitAsync(Limit).GetAwaiter().GetResult();
            Finish(store);
            Check(Result(first).Outcome == PlanSaveOutcome.Saved && Result(queued).Outcome == PlanSaveOutcome.Saved,
                "An explicit barrier silently discarded a pre-barrier save");
            if (action == "delete") Check(!File.Exists(path), "Old autosave resurrected a deleted plan");
            else Check(Read(path).Name == (action == "save" ? "explicit saved" : action == "replace" ? "replacement" : "reimported"),
                "Old autosave overwrote an explicit saved/imported plan");
            if (action != "save")
            {
                Check(Result(store.RequestSave(doc)).Outcome == PlanSaveOutcome.Superseded, "A stale deleted/replaced editor object could resurrect its contents");
                Finish(store);
            }
        }
        finally { disk.Release(); }
    }

    private static void FailedWrite(string root)
    {
        using var disk = new ControlledDisk();
        using var store = MakeStore(root, disk);
        var doc = store.Active!;
        var path = Path.Combine(root, doc.Id + ".json");
        var original = File.ReadAllText(path);
        var modified = doc.ModifiedUtc;
        PlanSaveTicket failed;
        using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            doc.Notes = "retry this edit";
            failed = store.RequestSave(doc);
            Check(Result(failed).Outcome == PlanSaveOutcome.Failed, "Locked destination did not fail atomic replacement");
            Check(!store.FlushAsync(Limit).GetAwaiter().GetResult(), "Flush claimed success despite unresolved durable failure");
            store.Poll();
            Check(File.ReadAllText(path) == original && doc.ModifiedUtc == modified, "Failed write changed durable file or live modification timestamp");
            Check(store.GetSaveState(doc.Id).Error != null && store.LastSaveError != null && !store.GetSaveState(doc.Id).IsSaving,
                "Failed write did not leave retry/error state");
        }
        var unrelated = PlanDocument.CreateDefault("unrelated");
        Check(store.Save(unrelated), "Unrelated plan should still save");
        Check(store.GetSaveState(doc.Id).Error != null && store.LastSaveError != null, "Unrelated successful save hid an unresolved failure");
        var retry = store.RequestSave(doc);
        Check(Result(retry).Outcome == PlanSaveOutcome.Saved, "Retry failed");
        Finish(store);
        Check(Read(path).Notes == "retry this edit" && store.LastSaveError == null &&
            store.GetSaveState(doc.Id).SavedRevision == retry.Revision, "Successful retry did not persist and clear its failure");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Failed atomic write leaked a temporary file");
    }

    private static void InvalidId(string root)
    {
        using var disk = new ControlledDisk();
        using var store = MakeStore(root, disk);
        var doc = store.Active!;
        var modified = doc.ModifiedUtc;
        doc.Id = "../escape";
        Check(!store.Save(doc) && store.LastSaveError != null && doc.ModifiedUtc == modified,
            "Unsafe id must return failure/error and restore ModifiedUtc");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.json")), "Save escaped the plan directory");
        doc.Id = null!;
        Check(!store.Save(doc) && store.LastSaveError != null && doc.ModifiedUtc == modified,
            "Null id must return failure/error and restore ModifiedUtc");
    }

    private static void Shutdown(string root)
    {
        using var disk = new ControlledDisk();
        using var store = MakeStore(root, disk);
        var doc = store.Active!;
        var modified = doc.ModifiedUtc;
        disk.BlockNext();
        var pending = store.RequestSave(doc);
        disk.WaitEntered();
        try
        {
            Check(!store.DrainAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult(), "Drain exceeded its bound or falsely succeeded");
            Check(!pending.Completion.IsCompleted && store.GetSaveState(doc.Id).IsSaving, "Timed-out write falsely completed");
            var rejected = store.RequestSave(doc);
            Check(Result(rejected).Outcome == PlanSaveOutcome.Failed, "Shutdown accepted a new request");
            disk.Release();
            Check(Result(pending).Outcome == PlanSaveOutcome.Saved, "Already queued save was lost during shutdown");
            store.Dispose();
            Check(doc.ModifiedUtc == modified, "Teardown published late metadata into the live editor");
        }
        finally { disk.Release(); }
    }

    private static PlanStore MakeStore(string root, ControlledDisk disk)
    {
        Plugin.Config.ActivePlanId = "";
        return new PlanStore(root, disk.Write);
    }

    private sealed class ControlledDisk : IDisposable
    {
        private readonly ManualResetEventSlim entered = new();
        private readonly ManualResetEventSlim released = new();
        private int block;
        public int Writes;
        public readonly List<int> Threads = new();
        public void BlockNext() { entered.Reset(); released.Reset(); Interlocked.Exchange(ref block, 1); }
        public void WaitEntered() => Check(entered.Wait(Limit), "Worker did not enter controlled durable write");
        public void Release() => released.Set();
        public void Write(string path, string text)
        {
            if (Interlocked.Exchange(ref block, 0) == 1)
            {
                entered.Set();
                Check(released.Wait(Limit), "Controlled durable write timed out");
            }
            AtomicFile.WriteAllText(path, text);
            Writes++;
            Threads.Add(Environment.CurrentManagedThreadId);
        }
        public void Dispose() { released.Set(); entered.Dispose(); released.Dispose(); }
    }
}
