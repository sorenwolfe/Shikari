using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Storage;

internal enum LearnedSaveOutcome { Saved, Failed, Superseded }
internal sealed record LearnedSaveResult(uint TerritoryId, long Revision, LearnedSaveOutcome Outcome, string? Error);

/// <summary>One ordered storage lane over privately owned timing snapshots; never calls game services.</summary>
internal sealed class LearnedPersistenceQueue
{
    private sealed record Operation(uint TerritoryId, long Revision, FightMemory? Memory);
    private readonly object gate = new();
    private readonly LinkedList<Operation> pending = new();
    private readonly Dictionary<uint, LinkedListNode<Operation>> saves = new();
    private readonly Dictionary<uint, (long Revision, string Error)> errors = new();
    private readonly Queue<LearnedSaveResult> completions = new();
    private readonly string directory;
    private readonly string mutexName;
    private readonly Action<string, string> write;
    private readonly Action<string> delete;
    private Task worker = Task.CompletedTask;
    private bool running;
    private bool accepting = true;
    private bool discardNotStarted;
    private bool discardedOperationsAreFailures;
    private long revision;

    public LearnedPersistenceQueue(string directory, Action<string, string>? write = null, Action<string>? delete = null)
    {
        this.directory = Path.GetFullPath(directory);
        var key = Path.TrimEndingDirectorySeparator(this.directory);
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        // OS ownership survives a different plugin assembly/load context after reload.
        mutexName = (OperatingSystem.IsWindows() ? "Local\\" : "") + "Shikari.Learned." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        this.write = write ?? AtomicFile.WriteAllText;
        this.delete = delete ?? File.Delete;
    }

    public bool IsSaving { get { lock (gate) return running; } }
    public string? LastError { get { lock (gate) return errors.Values.OrderByDescending(e => e.Revision).Select(e => e.Error).FirstOrDefault(); } }
    public bool TryTakeCompletion(out LearnedSaveResult result) { lock (gate) return completions.TryDequeue(out result!); }

    /// <summary>Never wait on the caller thread for a previous plugin instance's writer.
    /// Parsing inside read remains synchronous, but starts only after exclusive ownership is available.</summary>
    internal bool TryReadUnderOwnership(Action read)
    {
        lock (gate) if (!accepting) return false;
        using var mutex = new Mutex(false, mutexName);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) return false;
            read(); return true;
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    public long Save(FightMemory memory) => Enqueue(Capture(memory), memory.TerritoryId);
    public long Delete(uint territory) => Enqueue(null, territory);

    private long Enqueue(FightMemory? memory, uint territory)
    {
        if (territory == 0) throw new ArgumentException("Learned history needs a valid territory.", nameof(territory));
        lock (gate)
        {
            if (!accepting) throw new InvalidOperationException("Learned history storage is shutting down.");
            var operation = new Operation(territory, ++revision, memory);
            if (memory == null) saves.Remove(territory); // Deletion separates earlier and later save generations.
            else if (saves.Remove(territory, out var previous))
            {
                pending.Remove(previous);
                Complete(previous.Value, LearnedSaveOutcome.Superseded, null);
            }
            var node = pending.AddLast(operation);
            if (memory != null) saves[territory] = node;
            if (!running) { running = true; worker = Task.Run(Run); }
            return operation.Revision;
        }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout, bool stopAccepting = false, bool discardPending = false)
    {
        Task drain;
        lock (gate)
        {
            if (stopAccepting) accepting = false;
            saves.Clear(); // Requests included in this drain cannot be coalesced by a later caller.
            if (discardPending)
            {
                discardNotStarted = true;
                foreach (var operation in pending) Complete(operation, LearnedSaveOutcome.Superseded, null);
                pending.Clear();
            }
            drain = worker;
        }
        try { await drain.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (stopAccepting)
            {
                lock (gate)
                {
                    // Only an operation already inside the directory lock may finish. A new
                    // plugin instance waits behind it; queued/lock-waiting old writes must not
                    // overwrite that instance's newer generation afterward.
                    discardNotStarted = true;
                    discardedOperationsAreFailures = !discardPending;
                    foreach (var operation in pending)
                        Complete(operation, discardPending ? LearnedSaveOutcome.Superseded : LearnedSaveOutcome.Failed,
                            discardPending ? null : ShutdownError(operation));
                    pending.Clear(); saves.Clear();
                }
            }
            return false;
        }
        lock (gate) return !running && errors.Count == 0;
    }

    private void Run()
    {
        while (true)
        {
            Operation operation;
            lock (gate)
            {
                var node = pending.First;
                if (node == null) { running = false; return; }
                operation = node.Value;
                pending.RemoveFirst();
                if (saves.TryGetValue(operation.TerritoryId, out var queued) && ReferenceEquals(queued, node))
                    saves.Remove(operation.TerritoryId);
            }
            string? error = null;
            var outcome = LearnedSaveOutcome.Saved;
            try
            {
                using var mutex = new Mutex(false, mutexName);
                var entered = false;
                try
                {
                    while (!entered)
                    {
                        lock (gate)
                            if (discardNotStarted)
                            {
                                outcome = discardedOperationsAreFailures ? LearnedSaveOutcome.Failed : LearnedSaveOutcome.Superseded;
                                error = discardedOperationsAreFailures ? ShutdownError(operation) : null;
                                break;
                            }
                        try { entered = mutex.WaitOne(100); }
                        catch (AbandonedMutexException) { entered = true; }
                    }
                    if (entered)
                    {
                        lock (gate)
                            if (discardNotStarted)
                            {
                                outcome = discardedOperationsAreFailures ? LearnedSaveOutcome.Failed : LearnedSaveOutcome.Superseded;
                                error = discardedOperationsAreFailures ? ShutdownError(operation) : null;
                            }
                        if (outcome == LearnedSaveOutcome.Saved)
                        {
                            var path = Path.Combine(directory, operation.TerritoryId + ".json");
                            if (operation.Memory == null) delete(path);
                            else
                            {
                                var json = JsonConvert.SerializeObject(operation.Memory, PlanJson.Readable());
                                Directory.CreateDirectory(directory);
                                write(path, json);
                            }
                        }
                    }
                }
                finally { if (entered) mutex.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                error = (operation.Memory == null ? "Could not delete learned history" : "Could not save learned history") +
                    " for territory " + operation.TerritoryId + ": " + ex.Message;
                outcome = LearnedSaveOutcome.Failed;
            }
            lock (gate) Complete(operation, outcome, error);
        }
    }

    private static string ShutdownError(Operation operation) => "Learned history for territory " + operation.TerritoryId +
        " was not saved because storage remained busy during shutdown.";

    private void Complete(Operation operation, LearnedSaveOutcome outcome, string? error)
    {
        if (outcome == LearnedSaveOutcome.Failed) errors[operation.TerritoryId] = (operation.Revision, error!);
        else if (outcome == LearnedSaveOutcome.Saved &&
                 (!errors.TryGetValue(operation.TerritoryId, out var failure) || failure.Revision <= operation.Revision))
            errors.Remove(operation.TerritoryId);
        completions.Enqueue(new(operation.TerritoryId, operation.Revision, outcome, error));
    }

    internal static FightMemory Capture(FightMemory source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TerritoryId == 0 || source.FormatVersion != FightMemory.CurrentFormatVersion ||
            source.PullCount < 0 || source.ClearCount < 0 || !Nonnegative(source.LongestPullSeconds) ||
            source.Casts == null || source.Casts.Count > 8192 || source.Casts.Any(c => c == null ||
                c.ActionId == 0 || c.Occurrence <= 0 || c.PullsSeen < 0 || !Nonnegative(c.CastBarSeconds) ||
                !Nonnegative(c.Median) || !Nonnegative(c.Deviation) ||
                c.Samples == null || c.Samples.Count > LearnedCast.MaxSamples || c.Samples.Any(s => !Nonnegative(s))))
            throw new InvalidOperationException("Learned history has unsupported or oversized timing data; the existing file was preserved.");
        return new FightMemory
        {
            FormatVersion = source.FormatVersion, TerritoryId = source.TerritoryId, Name = source.Name,
            PullCount = source.PullCount, ClearCount = source.ClearCount, LongestPullSeconds = source.LongestPullSeconds,
            LastSeenUtc = source.LastSeenUtc,
            Casts = source.Casts.Select(c => new LearnedCast { ActionId = c.ActionId, Name = c.Name,
                Occurrence = c.Occurrence, CastBarSeconds = c.CastBarSeconds, Median = c.Median,
                Deviation = c.Deviation, PullsSeen = c.PullsSeen, Samples = new(c.Samples) }).ToList(),
        };
    }

    private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
}
