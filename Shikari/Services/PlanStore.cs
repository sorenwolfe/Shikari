using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services;

/// <summary>
/// Owns the on-disk library of plans and tracks which one the planner is editing.
/// Plans live as individual JSON files under the plugin's config directory so they can be
/// backed up, diffed, or hand-edited.
/// </summary>
public sealed class PlanStore : IDisposable
{
    private static readonly JsonSerializerSettings Settings = PlanJson.Readable();

    private readonly string directory;
    private readonly Dictionary<string, PlanDocument> plans = new();
    private readonly PlanPersistenceQueue persistence;
    private readonly Dictionary<long, (PlanDocument Document, PlanSaveTicket Ticket)> acknowledgements = new();
    private readonly HashSet<string> deletedIds = new();
    private bool disposed;

    public PlanStore() : this(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "plans"), AtomicFile.WriteAllText)
    {
    }

    internal PlanStore(string directory, Action<string, string> write)
    {
        this.directory = directory;
        persistence = new PlanPersistenceQueue(write);
        Directory.CreateDirectory(directory);
        LoadAll();
    }

    public PlanDocument? Active { get; private set; }
    public string? LastSaveError { get; private set; }

    public IReadOnlyCollection<PlanDocument> All => plans.Values;

    public IEnumerable<PlanDocument> Ordered =>
        plans.Values.OrderByDescending(p => p.ModifiedUtc);

    private void LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var doc = JsonConvert.DeserializeObject<PlanDocument>(json, Settings);
                if (doc == null)
                    continue;

                PlanNormaliser.Normalise(doc);
                plans[doc.Id] = doc;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Could not read plan file {File}.", file);
            }
        }

        var wanted = Plugin.Config.ActivePlanId;
        if (!string.IsNullOrEmpty(wanted) && plans.TryGetValue(wanted, out var active))
        {
            Active = active;
        }
        else
        {
            Active = Ordered.FirstOrDefault();
        }

        if (Active == null)
        {
            Active = PlanDocument.CreateDefault("My first plan");
            plans[Active.Id] = Active;
            Save(Active);
        }

        Plugin.Config.ActivePlanId = Active.Id;
    }

    public void SetActive(PlanDocument plan)
    {
        if (!plans.ContainsKey(plan.Id))
            plans[plan.Id] = plan;

        Active = plan;
        Plugin.Config.ActivePlanId = plan.Id;
        Plugin.PluginInterface.SavePluginConfig(Plugin.Config);
    }

    public PlanDocument CreateNew(string name)
    {
        var doc = PlanDocument.CreateDefault(name);
        plans[doc.Id] = doc;
        deletedIds.Remove(doc.Id);
        Save(doc);
        SetActive(doc);
        return doc;
    }

    /// <summary>Adds an imported plan, giving it a fresh id if one with that id already exists.</summary>
    public PlanDocument Import(PlanDocument doc, bool replaceExisting)
    {
        foreach (var rule in doc.AdaptiveMechanics)
            rule.Enabled = false;
        // Imports enter the editor with the same danger palette as newly drawn AoEs.
        // Do this here rather than on load, so subsequent colour edits survive saving.
        foreach (var slide in doc.Slides)
            foreach (var item in slide.Items)
                if (item.Kind == CanvasItemKind.Zone)
                    item.Color = CanvasItem.DefaultAoeColor;

        if (!replaceExisting && plans.ContainsKey(doc.Id))
        {
            doc.Id = Guid.NewGuid().ToString("N");
            doc.Name += " (imported)";
        }

        plans[doc.Id] = doc;
        deletedIds.Remove(doc.Id);
        Save(doc);
        SetActive(doc);
        return doc;
    }

    public PlanDocument Duplicate(PlanDocument source)
    {
        var json = JsonConvert.SerializeObject(source, Settings);
        var copy = PlanNormaliser.Normalise(JsonConvert.DeserializeObject<PlanDocument>(json, Settings)!);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = source.Name + " (copy)";
        plans[copy.Id] = copy;
        Save(copy);
        return copy;
    }

    public void Delete(PlanDocument doc)
    {
        plans.Remove(doc.Id);
        deletedIds.Add(doc.Id);
        try
        {
            var path = PathFor(doc);
            var result = persistence.Enqueue(doc.Id, path, null, coalesce: false, delete: true)
                .Completion.GetAwaiter().GetResult();
            if (result.Outcome == PlanSaveOutcome.Failed)
                Plugin.Log.Error(new IOException(result.Error), "Could not delete plan {Name}.", doc.Name);
            Poll();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not delete plan {Name}.", doc.Name);
        }

        if (Active?.Id == doc.Id)
        {
            Active = Ordered.FirstOrDefault() ?? CreateNew("New plan");
            Plugin.Config.ActivePlanId = Active.Id;
        }
    }

    public bool Save(PlanDocument doc)
    {
        var previousModified = doc.ModifiedUtc;
        var ticket = Request(doc, coalesce: false);
        var result = ticket.Completion.GetAwaiter().GetResult();
        Poll();
        doc.ModifiedUtc = result.Outcome == PlanSaveOutcome.Saved ? result.ModifiedUtc : previousModified;
        return result.Outcome == PlanSaveOutcome.Saved;
    }

    /// <summary>Captures on the caller thread. Completion means the captured revision reached durable storage.</summary>
    public PlanSaveTicket RequestSave(PlanDocument doc) => Request(doc, coalesce: true);

    public PlanSaveState GetSaveState(string planId) => persistence.GetState(planId);

    private PlanSaveTicket Request(PlanDocument doc, bool coalesce)
    {
        PlanSaveTicket ticket;
        var id = doc.Id ?? "";
        try
        {
            var path = PathFor(doc);
            if (deletedIds.Contains(id) || plans.TryGetValue(id, out var current) && !ReferenceEquals(doc, current))
                ticket = persistence.Enqueue(id, path, null, coalesce, superseded: true);
            else
                ticket = persistence.Enqueue(id, path, PlanSnapshot.Capture(doc, DateTime.UtcNow), coalesce);
        }
        catch (Exception ex)
        {
            ticket = persistence.Enqueue(id, "", null, coalesce, failure: "Could not save " + doc.Name + ": " + ex.Message);
        }
        acknowledgements[ticket.Revision] = (doc, ticket);
        return ticket;
    }

    /// <summary>Publish completed metadata on the owner thread; the worker never reads live documents or Plugin.</summary>
    public void Poll()
    {
        while (persistence.TryDequeueCompletion(out var result))
        {
            if (!acknowledgements.Remove(result.Revision, out var pending)) continue;
            var doc = pending.Document;
            if (result.Outcome == PlanSaveOutcome.Saved && !disposed &&
                !deletedIds.Contains(result.PlanId) && doc.Id == result.PlanId &&
                (!plans.TryGetValue(result.PlanId, out var current) || ReferenceEquals(doc, current)) &&
                persistence.GetState(result.PlanId).RequestedRevision == result.Revision)
                doc.ModifiedUtc = result.ModifiedUtc;
            if (result.Outcome == PlanSaveOutcome.Failed)
                Plugin.Log.Error(new IOException(result.Error), "Could not save plan {Name}.", doc.Name);
        }
        LastSaveError = persistence.LastError;
    }

    /// <summary>True only if queued writes finish within the bound and no plan has an unresolved storage failure.</summary>
    public Task<bool> FlushAsync(TimeSpan timeout) => persistence.FlushAsync(timeout);

    /// <summary>Stop new requests, then wait at most timeout. Timed-out tickets remain pending, never falsely saved.</summary>
    public Task<bool> DrainAsync(TimeSpan timeout) => persistence.FlushAsync(timeout, stopAccepting: true);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var drained = DrainAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Poll();
        if (!drained && LastSaveError == null)
            LastSaveError = "Plan storage did not finish before shutdown; queued saves remain pending.";
    }

    public bool SaveActive() => Active == null || Save(Active);

    public void SaveAll()
    {
        foreach (var doc in plans.Values)
            Save(doc);
    }

    private string PathFor(PlanDocument doc)
    {
        if (!Guid.TryParseExact(doc.Id, "N", out _))
            throw new InvalidDataException("A plan id must be a 32-character hexadecimal identifier.");
        return Path.Combine(directory, doc.Id + ".json");
    }
}
