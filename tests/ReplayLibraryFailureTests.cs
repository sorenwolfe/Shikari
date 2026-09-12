using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static partial class ReplayIntegration
{
    public static void RunLibraryFailures(string directory)
    {
        DetachedReplaySnapshot();
        foreach (var operation in new[] { "import", "save" }) ReservedRecordingCapacity(Path.Combine(directory, operation), operation);
        StaleSaveReferences(Path.Combine(directory, "stale"));
        foreach (var operation in new[] { "save", "active-save", "delete", "clear" }) ReloadAfterTimeout(Path.Combine(directory, "reload-" + operation), operation);
        TrailingPayload(Path.Combine(directory, "trailing"));
        foreach (var corruption in new[] { "null-statuses", "zero-version" })
            InvalidPayloadRetention(Path.Combine(directory, corruption), corruption);
        foreach (var corruption in new[] { "null", "missing", "text", "nonfinite", "short-array", "long-array", "duplicate" })
            InvalidVectorPayload(Path.Combine(directory, "vector-" + corruption), corruption);
        ValidVectorPayload(Path.Combine(directory, "valid-vectors"));
        Console.WriteLine("PASS: complete detached replay snapshot, capacity and retry, stale save rejection, reload active/queued save/delete/clear ordering, malformed JSON recovery preservation");
    }

    // Exercise every writable field, including unknown/null observation values and nested plan
    // metadata. Newly added model fields must be preserved before a graph can move to a worker.
    private static void DetachedReplaySnapshot()
    {
        var source = (ReplayAttempt)FilledReplayMember(typeof(ReplayAttempt))!;
        source.Casts.Add(new RecordedCast());
        source.Evidence.Effects.Add(new EvidenceEffect());
        var copy = ReplaySnapshot.Copy(source);
        EqualReplayMembers(source, copy, "replay");
        var expected = JsonConvert.SerializeObject(copy);
        MutateReplayMembers(source);
        Check(JsonConvert.SerializeObject(copy) == expected, "Editing nested source data changed the detached worker snapshot.");
    }

    private static object? FilledReplayMember(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return FilledReplayMember(underlying);
        if (type == typeof(string)) return Guid.NewGuid().ToString("N");
        if (type.IsEnum) return Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1);
        if (type == typeof(bool)) return true;
        if (type == typeof(DateTime)) return new DateTime(2024, 4, 3, 2, 1, 0, DateTimeKind.Utc);
        if (type == typeof(Vector2)) return new Vector2(.321f, .765f);
        if (type == typeof(Vector3)) return new Vector3(12.3f, 4.5f, 67.8f);
        if (type.IsPrimitive) return Convert.ChangeType(17, type);
        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            dictionary.Add(3, "seat three"); dictionary.Add(5, "seat five"); return dictionary;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(FilledReplayMember(type.GetGenericArguments()[0]));
            list.Add(FilledReplayMember(type.GetGenericArguments()[0])); return list;
        }
        var value = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties().Where(p => p.CanWrite))
            property.SetValue(value, property.PropertyType == typeof(bool)
                ? !(bool)property.GetValue(value)! : FilledReplayMember(property.PropertyType));
        return value;
    }

    private static void EqualReplayMembers(object? source, object? copy, string path)
    {
        if (source == null) { Check(copy == null, "Invented observation at " + path); return; }
        Check(copy != null, "Lost value at " + path);
        var type = source.GetType();
        if (type.IsValueType || source is string) { Check(source.Equals(copy), "Lost value at " + path); return; }
        Check(!ReferenceEquals(source, copy), "Shared mutable reference at " + path);
        if (source is IDictionary dictionary)
        {
            var other = (IDictionary)copy!;
            Check(dictionary.Count == other.Count, "Lost dictionary entry at " + path);
            foreach (DictionaryEntry entry in dictionary) EqualReplayMembers(entry.Value, other[entry.Key], path + "[" + entry.Key + "]");
        }
        else if (source is IList list)
        {
            var other = (IList)copy!;
            Check(list.Count == other.Count, "Lost list entry at " + path);
            for (var i = 0; i < list.Count; i++) EqualReplayMembers(list[i], other[i], path + "[" + i + "]");
        }
        else foreach (var property in type.GetProperties().Where(p => p.CanWrite))
            EqualReplayMembers(property.GetValue(source), property.GetValue(copy), path + "." + property.Name);
    }

    private static void MutateReplayMembers(object value)
    {
        if (value is IDictionary dictionary) { dictionary.Clear(); return; }
        if (value is IList list)
        {
            foreach (var item in list) if (item != null) MutateReplayMembers(item);
            list.Clear(); return;
        }
        foreach (var property in value.GetType().GetProperties().Where(p => p.CanWrite))
        {
            if (property.PropertyType == typeof(string)) property.SetValue(value, "changed");
            else if (!property.PropertyType.IsValueType && property.GetValue(value) is { } child) MutateReplayMembers(child);
        }
    }

    private static void ReservedRecordingCapacity(string directory, string operation)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        Plugin.Plans.Active = PlanDocument.CreateDefault();
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        var attempts = Enumerable.Range(0, 3).Select(_ => StoredAttempt(DateTime.UtcNow)).ToArray();
        if (operation == "save")
            foreach (var attempt in attempts) { store.AddImported(attempt); WaitForStorage(store, "writes"); Plugin.Framework.Tick(); }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        typeof(ReplayStore).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store,
            new object[] { (Action)(() => { entered.Set(); Check(release.Wait(5000), "Blocked worker was not released."); }) });
        Check(entered.Wait(5000), "Worker did not block.");
        var locks = new List<FileStream>(); ReplayAttempt active = null!;
        try
        {
            Plugin.Encounter.Begin(); Plugin.Framework.Tick();
            Check(store.Recording, "The initial pull should have available recording capacity.");
            active = ((ReplayBuffer)typeof(ReplayStore).GetField("buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!).Attempt;
            Directory.CreateDirectory(ReplayPath(directory, active));
            for (var i = 0; i < 2; i++)
            {
                if (operation == "import") { Directory.CreateDirectory(ReplayPath(directory, attempts[i])); store.AddImported(attempts[i]); }
                else { locks.Add(new(ReplayPath(directory, attempts[i]), FileMode.Open, FileAccess.Read, FileShare.Read)); store.SaveEvidence(attempts[i]); }
            }
            var rejected = false;
            try { if (operation == "import") store.AddImported(attempts[2]); else store.SaveEvidence(attempts[2]); }
            catch (IOException) { rejected = true; }
            Check(rejected && store.UnsavedCount == 2, "An active pull must reserve the third protected cache slot before imports or metadata saves consume it.");
            Plugin.Encounter.End();
        }
        finally { release.Set(); }
        try
        {
            WaitForStorage(store, "writes"); Plugin.Framework.Tick();
            Check(store.UnsavedCount == 3 && store.GetLoaded(active.Id) != null &&
                attempts.Take(2).All(a => store.GetLoaded(a.Id) != null), "A failed completed pull must retain its only copy alongside failed imports or metadata saves.");
            Plugin.Encounter.Begin(); Check(!store.Recording, "Additional pulls must pause while failed-only-copy recordings occupy capacity.");
        }
        finally
        {
            foreach (var file in locks) file.Dispose();
            foreach (var attempt in attempts.Take(operation == "import" ? 2 : 0).Append(active))
                if (Directory.Exists(ReplayPath(directory, attempt))) Directory.Delete(ReplayPath(directory, attempt));
        }
        var retry = typeof(ReplayStore).GetMethod("RetrySaves");
        Check(retry != null, "Review needs a way to retry all retained unsaved recordings.");
        retry!.Invoke(store, null); WaitForStorage(store, "writes"); Plugin.Framework.Tick();
        Check(store.UnsavedCount == 0 && File.Exists(ReplayPath(directory, active)) && attempts.Take(2).All(a => File.Exists(ReplayPath(directory, a))),
            "Retry must drain existing unsaved recordings despite the reserved-capacity guard.");
        Plugin.Encounter.Begin(); Check(store.Recording, "Recording should resume once protected copies are durable."); Plugin.Encounter.End();
    }

    private static void StaleSaveReferences(string directory)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        ReplayAttempt Add()
        {
            var attempt = StoredAttempt(DateTime.UtcNow); store.AddImported(attempt); WaitForStorage(store, "writes"); Plugin.Framework.Tick(); return attempt;
        }
        void Reject(ReplayAttempt attempt, string reason)
        {
            var rejected = false; try { store.SaveEvidence(attempt); } catch (IOException) { rejected = true; }
            Check(rejected, reason);
        }
        var deleted = Add(); store.Delete(deleted.Id); WaitForStorage(store, "writes"); Plugin.Framework.Tick();
        Reject(deleted, "A stale reference cannot recreate a deleted recording through SaveEvidence.");
        Check(!File.Exists(ReplayPath(directory, deleted)), "Deleted recording reappeared on disk.");
        var cleared = Add(); store.Clear(); WaitForStorage(store, "writes"); Plugin.Framework.Tick();
        Reject(cleared, "A stale reference cannot recreate a cleared recording through SaveEvidence.");
        var evicted = Add(); Add(); Add(); Add(); Check(store.GetLoaded(evicted.Id) == null, "Fixture failed to evict the recording.");
        Reject(evicted, "An evicted reference must reopen its current payload before saving.");
        var original = Add();
        var replacement = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(original))!;
        replacement.EndReason = "Replacement"; store.AddImported(replacement); WaitForStorage(store, "writes"); Plugin.Framework.Tick();
        Reject(original, "A replaced reference cannot overwrite the newer recording.");
        Check(store.GetLoaded(original.Id)?.EndReason == "Replacement", "Stale save changed the replacement graph.");
        store.Dispose(saveRecording: false);
        using var current = new ReplayStore(); WaitForStorage(current, "loading"); Plugin.Framework.Tick();
        store.Delete(replacement.Id); WaitForStorage(store, "writes");
        Check(File.Exists(ReplayPath(directory, replacement)), "An unloaded store must not delete a new instance's durable recording.");
        store.Clear(); WaitForStorage(store, "writes");
        Check(File.Exists(ReplayPath(directory, replacement)) && current.Catalog.Any(e => e.Id == replacement.Id),
            "An unloaded store must not clear a new instance's library.");
    }

    private static void ReloadAfterTimeout(string directory, string operation)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        var old = new ReplayStore(); WaitForStorage(old, "loading"); Plugin.Framework.Tick();
        var original = StoredAttempt(DateTime.UtcNow); old.AddImported(original); WaitForStorage(old, "writes"); Plugin.Framework.Tick();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var oldBytes = JsonConvert.SerializeObject(original);
        typeof(ReplayStore).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(old,
            new object[] { (Action)(() =>
            {
                entered.Set(); Check(release.Wait(10000), "Old storage operation was not released.");
                if (operation == "active-save") Shikari.Services.Storage.AtomicFile.WriteAllText(ReplayPath(directory, original), oldBytes);
            }) });
        Check(entered.Wait(5000), "Old worker did not enter its operation.");
        try
        {
            if (operation == "save") { original.EndReason = "Old generation"; old.SaveEvidence(original); }
            else if (operation == "delete") old.Delete(original.Id);
            else if (operation == "clear") old.Clear();
            old.Dispose(saveRecording: false);
            Check(old.Status.Contains("shutdown", StringComparison.OrdinalIgnoreCase), "A timed-out storage drain must report its unfinished work.");
            using var current = new ReplayStore();
            var replacement = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(original))!;
            replacement.EndReason = "New generation"; current.AddImported(replacement);
            // Before the fix the independent newer queue can finish while the old worker is
            // blocked, making the stale write/delete deterministic. A lease makes it wait.
            var currentLoading = (System.Threading.Tasks.Task)typeof(ReplayStore).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
            if (SpinWait.SpinUntil(() => currentLoading.IsCompleted, 100)) WaitForStorage(current, "writes");
            release.Set(); WaitForStorage(old, "writes"); WaitForStorage(current, "writes"); Plugin.Framework.Tick();
            Check(File.Exists(ReplayPath(directory, replacement)) &&
                JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(ReplayPath(directory, replacement)))!.EndReason == "New generation",
                "Timed-out old " + operation + " must not overwrite or delete a newer plugin generation's recording.");
        }
        finally { release.Set(); old.Dispose(saveRecording: false); }
    }

    private static void TrailingPayload(string directory)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        var replay = StoredAttempt(DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        var corrupted = JsonConvert.SerializeObject(replay) + "\n{\"unexpected\":true}";
        File.WriteAllText(ReplayPath(directory, replay), corrupted);
        using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        Check(store.Catalog.Count == 0 && store.Status.Contains("could not be loaded") &&
            File.ReadAllText(ReplayPath(directory, replay)) == corrupted,
            "Trailing JSON must not become trusted catalog evidence or be deleted during retention.");
    }

    private static void InvalidPayloadRetention(string directory, string corruption)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 1;
        var old = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
        var current = StoredAttempt(DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        var damaged = JObject.Parse(JsonConvert.SerializeObject(old));
        if (corruption == "null-statuses") damaged["Evidence"]!["Statuses"] = JValue.CreateNull();
        else damaged["Version"] = 0;
        var raw = damaged.ToString(Formatting.None);
        File.WriteAllText(ReplayPath(directory, old), raw);
        File.SetLastWriteTimeUtc(ReplayPath(directory, old), DateTime.UtcNow.AddMinutes(-1));
        File.WriteAllText(ReplayPath(directory, current), JsonConvert.SerializeObject(current));
        using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        Check(File.Exists(ReplayPath(directory, old)) && File.ReadAllText(ReplayPath(directory, old)) == raw,
            "Read settings must not normalize " + corruption + " into valid evidence and delete its recovery file during retention.");
        Check(store.Catalog.Count == 1 && store.Catalog[0].Id == current.Id && store.Status.Contains("could not be loaded"),
            "Malformed evidence must report a load error without consuming a usable retention slot.");
    }

    private static JToken MalformedVector(string corruption) => corruption switch
    {
        "null" => JValue.CreateNull(),
        "missing" => new JObject { ["X"] = 1 },
        "text" => new JArray("not a coordinate", 2),
        "nonfinite" => new JArray(double.MaxValue, 2),
        "short-array" => new JArray(1),
        "long-array" => new JArray(1, 2, 3),
        "duplicate" => new JObject { ["X"] = 1, ["x"] = 3, ["Y"] = 2 },
        _ => throw new Exception("Unknown fixture corruption"),
    };

    private static void InvalidVectorPayload(string directory, string corruption)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 1;
        var old = StoredAttempt(DateTime.UtcNow.AddMinutes(-1));
        var current = StoredAttempt(DateTime.UtcNow);
        old.Evidence.Positions.Add(new EvidencePosition { ActorId = 1, Time = 0, Position = new Vector2(1, 2) });
        var damaged = JObject.Parse(JsonConvert.SerializeObject(old));
        damaged["Evidence"]!["Positions"]![0]!["Position"] = MalformedVector(corruption);
        var raw = damaged.ToString(Formatting.None);
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        File.WriteAllText(ReplayPath(directory, old), raw);
        File.SetLastWriteTimeUtc(ReplayPath(directory, old), DateTime.UtcNow.AddMinutes(-1));
        File.WriteAllText(ReplayPath(directory, current), JsonConvert.SerializeObject(current));
        using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick();
        Check(File.Exists(ReplayPath(directory, old)) && File.ReadAllText(ReplayPath(directory, old)) == raw &&
            store.Catalog.Count == 1 && store.Catalog[0].Id == current.Id && store.Status.Contains("could not be loaded"),
            "Malformed " + corruption + " vector must remain a recovery file, not fabricated position evidence.");
        // A file can change after catalog scanning. The same validation must protect lazy reads.
        damaged["Id"] = current.Id;
        var lazyRaw = damaged.ToString(Formatting.None);
        File.WriteAllText(ReplayPath(directory, current), lazyRaw);
        store.RequestLoad(current.Id);
        Check(SpinWait.SpinUntil(() => { Plugin.Framework.Tick(); return store.RequestLoad(current.Id) == ReplayLoadState.Failed; }, 5000),
            "Malformed " + corruption + " vector must fail an explicit payload read.");
        Check(store.GetLoaded(current.Id) == null && File.ReadAllText(ReplayPath(directory, current)) == lazyRaw,
            "Failed lazy decoding must preserve the original file without publishing invented evidence.");
    }

    private static void ValidVectorPayload(string directory)
    {
        Plugin.PluginInterface.Directory = directory; Plugin.Config.ReplayRetention = 10;
        Directory.CreateDirectory(Path.Combine(directory, "replays"));
        var replay = StoredAttempt(DateTime.UtcNow);
        replay.Evidence.Actors.Add(new EvidenceActor { Id = 1, Name = "Example Player", SlotIndex = 0 });
        replay.Evidence.Positions.Add(new EvidencePosition { ActorId = 1, Time = 0, Position = new Vector2(0, 2.5f) });
        replay.Evidence.Effects.Add(new EvidenceEffect { ActionId = 1, SourceId = 2, TargetId = 1, Type = "calculateddamage", Time = 0 });
        foreach (var compact in new[] { false, true })
        {
            var json = compact ? JsonConvert.SerializeObject(replay, Shikari.Services.PlanJson.Compact()) : JsonConvert.SerializeObject(replay);
            File.WriteAllText(ReplayPath(directory, replay), json);
            using var store = new ReplayStore(); WaitForStorage(store, "loading"); Plugin.Framework.Tick(); store.RequestLoad(replay.Id);
            Check(SpinWait.SpinUntil(() => { Plugin.Framework.Tick(); return store.GetLoaded(replay.Id) != null; }, 5000),
                "Valid legacy object and compact array coordinates must remain readable.");
            var loaded = store.GetLoaded(replay.Id)!;
            Check(loaded.Evidence.Positions[0].Position == new Vector2(0, 2.5f) &&
                loaded.Evidence.Effects[0].SourcePosition == null && loaded.Evidence.Effects[0].TargetPosition == null,
                "A real zero coordinate must survive, while absent optional effect positions stay unknown.");
        }
    }
}
