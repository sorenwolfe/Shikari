using System;
using Shikari.Model;

namespace Shikari.Services.Storage;

/// <summary>Tracks editor revisions independently of asynchronously persisted snapshots.</summary>
public sealed class EditorAutosave
{
    private PlanDocument? document;
    private long revision;
    private long requestedRevision;
    private bool dirty;
    private PlanSaveTicket? ticket;
    private DateTime requestedAt = DateTime.MinValue;

    public bool IsDirty(PlanDocument? plan) => ReferenceEquals(document, plan) && dirty;

    public void MarkDirty(PlanDocument plan)
    {
        if (!ReferenceEquals(document, plan))
        {
            document = plan;
            ticket = null;
            requestedAt = DateTime.MinValue;
        }
        revision++;
        dirty = true;
    }

    public bool Update(PlanDocument? plan, DateTime now, Func<PlanDocument, PlanSaveTicket> request)
    {
        if (plan == null || !ReferenceEquals(document, plan)) return false;
        var acknowledged = false;
        if (ticket?.Completion.IsCompleted == true)
        {
            if (ticket.Completion.IsCompletedSuccessfully && ticket.Completion.Result.Outcome == PlanSaveOutcome.Saved &&
                requestedRevision == revision)
            { dirty = false; acknowledged = true; }
            ticket = null;
        }
        if (dirty && (now - requestedAt).TotalSeconds >= 2)
            RequestNow(plan, now, request);
        return acknowledged;
    }

    public void RequestNow(PlanDocument plan, DateTime now, Func<PlanDocument, PlanSaveTicket> request)
    {
        if (!ReferenceEquals(document, plan)) MarkDirty(plan);
        if (ticket?.Completion.IsCompleted == false && requestedRevision == revision) return;
        dirty = true;
        ticket = request(plan);
        requestedRevision = revision;
        requestedAt = now;
    }
}
