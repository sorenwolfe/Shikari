using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Shikari.Services.Replay;

public sealed partial class ReplayStore
{
    public const long CacheBudgetBytes = 192L * 1024 * 1024;
    public const int MaxLoadedAttempts = 3;
    private readonly List<ReplayCatalogEntry> catalog = new();
    private readonly Dictionary<string, long> cacheBytes = new();
    private readonly Dictionary<string, long> cacheUse = new();
    private readonly HashSet<string> reviewPins = new();
    private readonly HashSet<string> unsaved = new();
    private readonly Dictionary<string, long> generations = new();
    private readonly HashSet<string> requestedLoads = new();
    private readonly Dictionary<string, string> loadErrors = new();
    private readonly ConcurrentQueue<ReadResult> readResults = new();
    private sealed record ReadResult(string Id, long Generation, long Epoch, ReplayAttempt? Attempt, string Error);
    private Task reads = Task.CompletedTask;
    private long cacheClock;
    private long libraryEpoch;
    private readonly string storageMutexName;
    private volatile bool discardQueuedStorage;
    private readonly object saveGate = new();
    private readonly Dictionary<string, SaveRequest> queuedSaves = new();
    private readonly Dictionary<string, long> saveRevisions = new();
    private readonly ConcurrentQueue<SaveResult> saveResults = new();
    private long nextSaveBatch;
    private sealed record SaveRequest(ReplayAttempt Snapshot, long Revision, long Batch, bool Promote, long Order, int Keep);
    private sealed record SaveResult(string Id, long Revision, bool Success);

    private static string StorageMutexName(string directory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        return (OperatingSystem.IsWindows() ? "Local\\" : "") + "Shikari.Replays." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    // A plugin reload creates a different assembly/load context. OS directory ownership
    // lets an old active atomic operation finish before the next instance can mutate disk.
    private T WithStorageOwnership<T>(Func<T> operation, T cancelled)
    {
        if (discardQueuedStorage) return cancelled;
        using var mutex = new Mutex(false, storageMutexName);
        var entered = false;
        try
        {
            while (!entered)
            {
                if (discardQueuedStorage) return cancelled;
                try { entered = mutex.WaitOne(100); }
                catch (AbandonedMutexException) { entered = true; }
            }
            return discardQueuedStorage ? cancelled : operation();
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    public IReadOnlyList<ReplayCatalogEntry> Catalog => catalog;
    public bool CatalogLoading => !loaded;
    public long CachedBytes => cacheBytes.Values.Sum();
    public int UnsavedCount => unsaved.Count;
    public ReplayAttempt? GetLoaded(string id)
    {
        var attempt = attempts.FirstOrDefault(a => a.Id == id);
        if (attempt != null) cacheUse[id] = ++cacheClock;
        return attempt;
    }
    public string LoadError(string id) => loadErrors.GetValueOrDefault(id, "");
    public void SetReviewSelection(string? primaryId, string? comparisonId)
    {
        reviewPins.Clear();
        if (!string.IsNullOrEmpty(primaryId)) reviewPins.Add(primaryId);
        if (!string.IsNullOrEmpty(comparisonId)) reviewPins.Add(comparisonId);
    }
    public ReplayLoadState RequestLoad(string id)
    {
        if (disposed || !catalog.Any(e => e.Id == id)) return !disposed && !loaded ? ReplayLoadState.Loading : ReplayLoadState.Missing;
        if (GetLoaded(id) != null) return ReplayLoadState.Ready;
        if (loadErrors.ContainsKey(id)) return ReplayLoadState.Failed;
        if (requestedLoads.Contains(id) || requestedLoads.Count >= MaxLoadedAttempts) return ReplayLoadState.Loading;
        requestedLoads.Add(id);
        var generation = generations.GetValueOrDefault(id);
        var epoch = libraryEpoch;
        var path = PathFor(id);
        // Read one payload at a time. Atomic replacements guarantee readers see one complete file.
        reads = reads.ContinueWith(_ =>
        {
            if (disposed) return;
            ReplayAttempt? attempt = null; var error = "";
            try { attempt = ReadPayload(path, id); }
            catch (Exception ex) { error = "Recording could not be opened: " + ex.Message; }
            if (!disposed) readResults.Enqueue(new(id, generation, epoch, attempt, error));
        }, TaskScheduler.Default);
        return ReplayLoadState.Loading;
    }
    public ReplayLoadState RetryLoad(string id) { loadErrors.Remove(id); return RequestLoad(id); }

    private void PublishReads()
    {
        while (readResults.TryDequeue(out var result))
        {
            if (result.Epoch != libraryEpoch || result.Generation != generations.GetValueOrDefault(result.Id)) continue;
            requestedLoads.Remove(result.Id);
            if (!catalog.Any(e => e.Id == result.Id) || GetLoaded(result.Id) != null) continue;
            if (result.Attempt == null) { loadErrors[result.Id] = result.Error; continue; }
            if (!Admit(result.Attempt)) loadErrors[result.Id] = "Replay memory is occupied by selected or unsaved recordings. Close the comparison or retry failed saves, then retry opening this recording.";
        }
    }
    private void InvalidateLoad(string id)
    {
        generations[id] = generations.GetValueOrDefault(id) + 1;
        requestedLoads.Remove(id); loadErrors.Remove(id);
    }
    private void Evict(string id)
    {
        attempts.RemoveAll(a => a.Id == id); cacheBytes.Remove(id); cacheUse.Remove(id);
    }
    private bool Admit(ReplayAttempt attempt, bool preserveUnsaved = false)
    {
        var bytes = ReplayMemory.Estimate(attempt);
        var replaced = attempts.Any(a => a.Id == attempt.Id);
        var count = attempts.Count - (replaced ? 1 : 0);
        var total = CachedBytes - cacheBytes.GetValueOrDefault(attempt.Id);
        var evictions = new List<string>();
        foreach (var candidate in attempts.Where(a => a.Id != attempt.Id && !unsaved.Contains(a.Id) &&
                     (preserveUnsaved || !reviewPins.Contains(a.Id))).OrderBy(a => cacheUse.GetValueOrDefault(a.Id)))
        {
            if (count < MaxLoadedAttempts && total + bytes <= CacheBudgetBytes) break;
            evictions.Add(candidate.Id); total -= cacheBytes.GetValueOrDefault(candidate.Id); count--;
        }
        if (count >= MaxLoadedAttempts || total + bytes > CacheBudgetBytes)
        {
            // A failed completed pull is the only copy. Keep it and stop new admission until
            // it is saved/deleted; the estimate target is never a reason to destroy that data.
            if (!preserveUnsaved || count >= MaxLoadedAttempts) return false;
            status = "Replay memory needs attention. Save or remove unsaved recordings before recording another pull.";
        }
        foreach (var id in evictions) Evict(id);
        Evict(attempt.Id);
        cacheBytes[attempt.Id] = bytes; cacheUse[attempt.Id] = ++cacheClock;
        var order = visibleOrder.GetValueOrDefault(attempt.Id);
        var index = attempts.FindIndex(a => visibleOrder.GetValueOrDefault(a.Id) < order);
        attempts.Insert(index < 0 ? attempts.Count : index, attempt);
        return true;
    }

    private void QueueSnapshot(ReplayAttempt attempt, bool promote, long order)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ReplayStore));
        if (!unsaved.Contains(attempt.Id) && unsaved.Count + pending.Count + (buffer != null ? 1 : 0) >= MaxLoadedAttempts)
            throw new IOException("Replay memory is reserved for active or unsaved recordings. Retry pending saves before editing another recording.");
        var snapshot = ReplaySnapshot.Copy(attempt);
        var id = snapshot.Id;
        var revision = saveRevisions.GetValueOrDefault(id) + 1;
        saveRevisions[id] = revision;
        unsaved.Add(id); InvalidateLoad(id);
        var request = new SaveRequest(snapshot, revision, ++nextSaveBatch, promote, order, Math.Clamp(Plugin.Config.ReplayRetention, 1, 30));
        bool schedule;
        lock (saveGate)
        {
            schedule = !queuedSaves.TryGetValue(id, out var prior);
            request = request with { Batch = prior?.Batch ?? request.Batch, Promote = promote || prior?.Promote == true };
            queuedSaves[id] = request;
        }
        var batch = request.Batch;
        if (schedule) Queue(() => DrainSnapshot(id, batch));
    }
    private void DrainSnapshot(string id, long batch)
    {
            SaveRequest request;
            lock (saveGate)
            {
                if (!queuedSaves.TryGetValue(id, out request!) || request.Batch != batch) return;
                queuedSaves.Remove(id);
            }
            var success = false;
            try
            {
                if (!ReplayValidation.IsValid(request.Snapshot)) throw new IOException("Replay evidence failed validation.");
                var json = Serialize(request.Snapshot);
                Persist(id, json, request.Keep, request.Promote, request.Order); success = true;
            }
            catch (Exception ex)
            {
                // Encoding failures need the same retry visibility as disk failures.
                storageErrors[id] = "Replay storage needs attention: " + ex.Message;
                storageError = storageErrors.Values.First();
            }
            saveResults.Enqueue(new(id, request.Revision, success));
    }
    private void PublishSnapshotSaves()
    {
        while (saveResults.TryDequeue(out var result))
        {
            if (saveRevisions.GetValueOrDefault(result.Id) != result.Revision) continue;
            if (result.Success) unsaved.Remove(result.Id);
        }
    }
    private void ForgetSnapshots(string id)
    {
        lock (saveGate) queuedSaves.Remove(id);
        saveRevisions[id] = saveRevisions.GetValueOrDefault(id) + 1;
        unsaved.Remove(id);
    }

    /// <summary>Disk evidence must never repair malformed coordinates into measured zeroes.</summary>
    private sealed class ReplayVectorReader : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type objectType) => objectType == typeof(Vector2) || objectType == typeof(Vector2?);
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null && objectType == typeof(Vector2?)) return null;
            if (reader.TokenType == JsonToken.StartArray)
            {
                if (!reader.Read()) throw Invalid();
                var x = Component(reader);
                if (!reader.Read()) throw Invalid();
                var y = Component(reader);
                if (!reader.Read() || reader.TokenType != JsonToken.EndArray) throw Invalid();
                return new Vector2(x, y);
            }
            if (reader.TokenType == JsonToken.StartObject)
            {
                float x = 0, y = 0;
                bool seenX = false, seenY = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject)
                    {
                        if (!seenX || !seenY) throw Invalid();
                        return new Vector2(x, y);
                    }
                    if (reader.TokenType != JsonToken.PropertyName) throw Invalid();
                    var name = (string?)reader.Value;
                    if (!reader.Read()) throw Invalid();
                    if (string.Equals(name, "X", StringComparison.OrdinalIgnoreCase))
                    {
                        if (seenX) throw Invalid();
                        x = Component(reader); seenX = true;
                    }
                    else if (string.Equals(name, "Y", StringComparison.OrdinalIgnoreCase))
                    {
                        if (seenY) throw Invalid();
                        y = Component(reader); seenY = true;
                    }
                    else reader.Skip();
                }
            }
            throw Invalid();
        }

        private static float Component(JsonReader reader)
        {
            if (reader.TokenType is not (JsonToken.Integer or JsonToken.Float)) throw Invalid();
            float value;
            try { value = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException) { throw Invalid(); }
            if (!float.IsFinite(value)) throw Invalid();
            return value;
        }

        private static JsonSerializationException Invalid() => new("Replay coordinates require exactly two finite numeric components.");
    }
}
