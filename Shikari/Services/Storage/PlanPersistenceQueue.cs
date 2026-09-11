using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Shikari.Services.Storage;

public enum PlanSaveOutcome { Saved, Failed, Superseded }

public sealed record PlanSaveResult(string PlanId, long Revision, PlanSaveOutcome Outcome,
    string? Error, DateTime ModifiedUtc);

public sealed class PlanSaveTicket
{
    internal readonly TaskCompletionSource<PlanSaveResult> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal PlanSaveTicket(string planId, long revision) { PlanId = planId; Revision = revision; }
    public string PlanId { get; }
    public long Revision { get; }
    public Task<PlanSaveResult> Completion => Source.Task;
}

public readonly record struct PlanSaveState(bool IsSaving, string? Error, long RequestedRevision, long SavedRevision);

/// <summary>One ordered storage lane. Operations contain detached data and never access plugin services.</summary>
internal sealed class PlanPersistenceQueue
{
    private sealed record Operation(PlanSaveTicket Ticket, string Path, PlanSnapshot? Snapshot, bool Delete);
    private sealed class State
    {
        public int Pending;
        public long Requested;
        public long Saved;
        public long ErrorRevision;
        public string? Error;
    }

    private readonly object gate = new();
    private readonly LinkedList<Operation> pending = new();
    private readonly Dictionary<string, LinkedListNode<Operation>> autosaves = new();
    private readonly Dictionary<string, State> states = new();
    private readonly Queue<PlanSaveResult> completions = new();
    private readonly Dictionary<string, (long Revision, string Error)> errors = new();
    private readonly Action<string, string> write;
    private Task worker = Task.CompletedTask;
    private bool running;
    private bool accepting = true;
    private long revision;

    public PlanPersistenceQueue(Action<string, string> write) => this.write = write;

    public PlanSaveTicket Enqueue(string id, string path, PlanSnapshot? snapshot, bool coalesce, bool delete = false,
        string? failure = null, bool superseded = false)
    {
        lock (gate)
        {
            var ticket = new PlanSaveTicket(id, ++revision);
            var state = StateFor(id);
            state.Requested = ticket.Revision;
            state.Pending++;
            var operation = new Operation(ticket, path, snapshot, delete);
            if (!accepting || failure != null || superseded)
            {
                Complete(operation, superseded ? PlanSaveOutcome.Superseded : PlanSaveOutcome.Failed,
                    superseded ? null : failure ?? "Plan storage is shutting down.");
                return ticket;
            }

            if (!coalesce) autosaves.Clear(); // Explicit operations form a barrier for every plan.
            else if (autosaves.Remove(id, out var old))
            {
                pending.Remove(old);
                Complete(old.Value, PlanSaveOutcome.Superseded, null);
            }
            var node = pending.AddLast(operation);
            if (coalesce) autosaves[id] = node;
            if (!running)
            {
                running = true;
                worker = Task.Run(Run);
            }
            return ticket;
        }
    }

    public PlanSaveState GetState(string id)
    {
        lock (gate)
        {
            if (!states.TryGetValue(id, out var s)) return default;
            return new PlanSaveState(s.Pending > 0, s.Error, s.Requested, s.Saved);
        }
    }

    public bool TryDequeueCompletion(out PlanSaveResult result)
    {
        lock (gate) return completions.TryDequeue(out result!);
    }

    public string? LastError
    {
        get { lock (gate) return errors.Values.OrderByDescending(e => e.Revision).Select(e => e.Error).FirstOrDefault(); }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout, bool stopAccepting = false)
    {
        Task drain;
        lock (gate)
        {
            if (stopAccepting) accepting = false;
            // Do not let later autosaves remove a request covered by this flush.
            autosaves.Clear();
            drain = worker;
        }
        try { await drain.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        lock (gate) return !running && errors.Count == 0;
    }

    private State StateFor(string id)
    {
        if (!states.TryGetValue(id, out var value)) states[id] = value = new State();
        return value;
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
                if (autosaves.TryGetValue(operation.Ticket.PlanId, out var queued) && ReferenceEquals(queued, node))
                    autosaves.Remove(operation.Ticket.PlanId);
            }
            string? error = null;
            try
            {
                if (operation.Delete) File.Delete(operation.Path);
                else write(operation.Path, operation.Snapshot!.Encode());
            }
            catch (Exception ex)
            {
                error = operation.Delete ? "Could not delete plan: " + ex.Message
                    : "Could not save " + operation.Snapshot!.Name + ": " + ex.Message;
            }
            lock (gate) Complete(operation, error == null ? PlanSaveOutcome.Saved : PlanSaveOutcome.Failed, error);
        }
    }

    // Caller holds gate. Publishing a ticket does not invoke consumer code inline.
    private void Complete(Operation operation, PlanSaveOutcome outcome, string? error)
    {
        var ticket = operation.Ticket;
        var state = StateFor(ticket.PlanId);
        state.Pending--;
        if (outcome == PlanSaveOutcome.Saved)
        {
            if (!operation.Delete) state.Saved = ticket.Revision;
            if (ticket.Revision >= state.ErrorRevision)
            {
                state.Error = null;
                errors.Remove(ticket.PlanId);
            }
        }
        else if (outcome == PlanSaveOutcome.Failed && ticket.Revision >= state.ErrorRevision)
        {
            state.ErrorRevision = ticket.Revision;
            state.Error = error;
            errors[ticket.PlanId] = (ticket.Revision, error!);
        }
        var result = new PlanSaveResult(ticket.PlanId, ticket.Revision, outcome, error,
            operation.Snapshot?.ModifiedUtc ?? default);
        completions.Enqueue(result);
        ticket.Source.TrySetResult(result);
    }
}
