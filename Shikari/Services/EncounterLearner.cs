using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services;

/// <summary>
/// Remembers when things happen across pulls, so mechanics can be called before the boss starts
/// casting. Each cast is keyed by action id plus which use of it this is; the median of the times
/// we've seen is the expectation. Pulls run fast or slow, so a recognised cast re-anchors
/// everything after it.
/// </summary>
public sealed class EncounterLearner : IDisposable
{
    /// <summary>A pull shorter than this, or with fewer casts, is not worth learning from.</summary>
    private const float MinimumPullSeconds = 15f;
    private const int MinimumPullCasts = 3;

    private readonly string directory;
    private readonly Dictionary<uint, FightMemory> memories = new();
    private readonly List<CastEvent> pullBuffer = new();
    private readonly LearnedPersistenceQueue persistence;
    private readonly TimeSpan shutdownTimeout;
    private readonly Dictionary<uint, string> preparationErrors = new();
    private readonly Dictionary<uint, (long Revision, bool Failed)> pendingDeletes = new();
    private readonly HashSet<uint> unreadableTerritories = new();

    private uint currentTerritory;
    private bool pullCommitted;
    private bool disposed;
    private bool initialized;
    private bool skipCurrentPull = true;
    private DateTime nextInitializationUtc;
    private string? initializationError;

    public bool IsLoading => !initialized && !disposed;
    public bool IsSaving => persistence.IsSaving;
    public string? StorageError => initializationError ?? preparationErrors.Values.FirstOrDefault() ?? persistence.LastError;

    public EncounterLearner() : this(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "learned")) { }

    internal EncounterLearner(string directory, Action<string, string>? write = null, Action<string>? delete = null,
        TimeSpan? shutdownTimeout = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(3);
        Directory.CreateDirectory(directory);
        persistence = new LearnedPersistenceQueue(directory, write, delete);
        try
        {
            currentTerritory = Plugin.ClientState.TerritoryType;
            TryInitialize();
            Plugin.Encounter.CombatStarted += OnCombatStarted;
            Plugin.Encounter.CombatEnded += OnCombatEnded;
            Plugin.Encounter.CastStarted += OnCastStarted;
            Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
            Plugin.Framework.Update += PollStorage;
        }
        catch { Dispose(saveChanges: false); throw; }
    }

    /// <summary>
    /// How far ahead (negative) or behind (positive) the learned timeline this pull is running,
    /// in seconds. Zero until a recognised cast has confirmed it.
    /// </summary>
    public float Drift { get; private set; }

    public bool DriftConfirmed { get; private set; }

    /// <summary>The cast that last re-anchored the prediction, for display.</summary>
    public string DriftAnchor { get; private set; } = string.Empty;

    /// <summary>What is known about the fight in the current zone, if anything.</summary>
    public FightMemory? Current => initialized ? memories.GetValueOrDefault(currentTerritory) : null;

    public IEnumerable<FightMemory> All => initialized ? memories.Values.OrderByDescending(m => m.LastSeenUtc) : Enumerable.Empty<FightMemory>();

    /// <summary>Casts recorded so far in the pull that is running now.</summary>
    public IReadOnlyList<CastEvent> PullSoFar => pullBuffer;

    // ---------------------------------------------------------------- prediction

    /// <summary>
    /// Best guess at when a cast will happen in the current pull, in seconds from the pull start.
    /// </summary>
    /// <returns>False when there is nothing learned about this cast yet.</returns>
    public bool TryPredict(uint actionId, int occurrence, out float expectedCombatTime, out float confidence)
    {
        expectedCombatTime = 0f;
        confidence = 0f;

        var memory = Current;
        var learned = memory?.Find(actionId, occurrence);
        if (learned == null || learned.Samples.Count == 0)
            return false;

        expectedCombatTime = TimelinePrediction.Expected(learned.Median, Drift);
        confidence = learned.Confidence;

        // An unconfirmed pull is a pull we have no anchor for, so trust it a little less.
        if (!DriftConfirmed)
            confidence *= 0.8f;

        return true;
    }

    /// <summary>
    /// Learned casts still ahead of the given point in the pull, soonest first, with the times
    /// they are expected at.
    /// </summary>
    public List<(LearnedCast Cast, float ExpectedTime)> Upcoming(float combatElapsed, int max = 8)
    {
        var memory = Current;
        if (memory == null)
            return new List<(LearnedCast, float)>();

        return memory.InOrder()
            .Select(c => (Cast: c, ExpectedTime: TimelinePrediction.Expected(c.Median, Drift)))
            .Where(x => x.ExpectedTime > combatElapsed)
            .Take(max)
            .ToList();
    }

    // ---------------------------------------------------------------- recording

    private void OnCombatStarted()
    {
        skipCurrentPull = !initialized;
        pullBuffer.Clear();
        pullCommitted = false;
        Drift = 0f;
        DriftConfirmed = false;
        DriftAnchor = string.Empty;
    }

    private void OnCastStarted(CastEvent evt)
    {
        if (!initialized || skipCurrentPull || !Plugin.Config.LearningEnabled)
            return;

        pullBuffer.Add(evt);

        // Only re-anchor on a cast we know well; this shifts everything after it.
        var learned = Current?.Find(evt.ActionId, evt.Occurrence);
        if (learned == null || learned.Samples.Count == 0)
            return;
        if (learned.Confidence < TimelinePrediction.MinimumAnchorConfidence)
            return;

        Drift = TimelinePrediction.MeasureDrift(evt.CombatTime, learned.Median);
        DriftConfirmed = true;
        DriftAnchor = evt.ActionName;
    }

    private void OnCombatEnded() => CommitPull(cleared: false);

    private void OnTerritoryChanged(uint territory)
    {
        // Leaving the zone mid-pull still leaves us with usable data.
        CommitPull(cleared: false);
        currentTerritory = territory;
        skipCurrentPull = true;
        Drift = 0f;
        DriftConfirmed = false;
    }

    /// <summary>Called when the duty is completed, so a clear can be counted as one.</summary>
    public void NoteClear() { if (!disposed) CommitPull(cleared: true); }

    private void CommitPull(bool cleared)
    {
        if (!initialized || skipCurrentPull)
        { pullBuffer.Clear(); pullCommitted = true; return; }
        if (pullCommitted)
        {
            // A clear arriving after combat already ended should still bump the counter.
            if (cleared && Current != null)
            {
                Current.ClearCount++;
                Save(Current);
            }

            return;
        }

        pullCommitted = true;

        if (!Plugin.Config.LearningEnabled || pullBuffer.Count == 0)
            return;

        var length = pullBuffer.Max(c => c.CombatTime);
        if (length < MinimumPullSeconds || pullBuffer.Count < MinimumPullCasts)
        {
            // Dummies, stray adds, pulls that died on contact.
            pullBuffer.Clear();
            return;
        }

        var memory = GetOrCreateMemory(currentTerritory);
        memory.PullCount++;
        if (cleared)
            memory.ClearCount++;
        memory.LongestPullSeconds = MathF.Max(memory.LongestPullSeconds, length);
        memory.LastSeenUtc = DateTime.UtcNow;

        foreach (var cast in pullBuffer)
        {
            var learned = memory.GetOrAdd(cast.ActionId, cast.Occurrence, cast.ActionName);
            learned.AddSample(cast.CombatTime, cast.TotalCastTime);
        }

        Plugin.Log.Information(
            "Learned from a {Length:0}s pull of {Name}: {Casts} casts, {Total} known timings over {Pulls} pulls.",
            length, memory.Name, pullBuffer.Count, memory.Casts.Count, memory.PullCount);

        pullBuffer.Clear();
        Save(memory);
    }

    // ---------------------------------------------------------------- housekeeping

    public void Forget(FightMemory memory)
    {
        if (disposed || !initialized || !memories.TryGetValue(memory.TerritoryId, out var current) || !ReferenceEquals(current, memory)) return;
        memories.Remove(memory.TerritoryId);
        try
        {
            pendingDeletes[memory.TerritoryId] = (persistence.Delete(memory.TerritoryId), false);
            unreadableTerritories.Remove(memory.TerritoryId); // Explicit forgetting authorizes replacing this generation.
            preparationErrors.Remove(memory.TerritoryId);
        }
        catch (Exception ex)
        {
            preparationErrors[memory.TerritoryId] = "Could not queue learned-history deletion: " + ex.Message;
            Plugin.Log.Error(ex, "Could not delete learned data for territory {Id}.", memory.TerritoryId);
        }
    }

    /// <summary>Clears the timings but keeps the fight, for when a patch retunes it.</summary>
    public void ForgetTimings(FightMemory memory)
    {
        if (disposed || !initialized || !memories.TryGetValue(memory.TerritoryId, out var current) || !ReferenceEquals(current, memory)) return;
        memory.Casts.Clear();
        memory.PullCount = 0;
        memory.ClearCount = 0;
        memory.LongestPullSeconds = 0f;
        Save(memory);
    }

    private FightMemory GetOrCreateMemory(uint territory)
    {
        if (memories.TryGetValue(territory, out var existing))
        {
            if (string.IsNullOrEmpty(existing.Name))
                existing.Name = DescribeTerritory(territory);
            return existing;
        }

        var created = new FightMemory
        {
            TerritoryId = territory,
            Name = DescribeTerritory(territory),
        };

        memories[territory] = created;
        return created;
    }

    /// <summary>Duty name where there is one, otherwise the place name.</summary>
    public static string DescribeTerritory(uint territory)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territory, out var row))
            {
                var duty = row.ContentFinderCondition.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(duty))
                    return duty;

                var place = row.PlaceName.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(place))
                    return place;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not name territory {Id}.", territory);
        }

        return "Zone " + territory;
    }

    private void TryInitialize()
    {
        if (initialized || disposed || DateTime.UtcNow < nextInitializationUtc) return;
        nextInitializationUtc = DateTime.UtcNow.AddMilliseconds(100);
        try
        {
            if (persistence.TryReadUnderOwnership(LoadAll))
            { initialized = true; initializationError = null; }
        }
        catch (Exception ex)
        {
            var message = "Learned history could not be loaded: " + ex.Message;
            if (initializationError != message) Plugin.Log.Error(ex, "Could not load learned timing history.");
            initializationError = message;
        }
    }

    private void LoadAll()
    {
        // Initialization retries must replace any unpublished partial directory scan.
        memories.Clear();
        unreadableTerritories.Clear();
        preparationErrors.Clear();
        var settings = PlanJson.Readable();
        settings.NullValueHandling = NullValueHandling.Include; // Explicit null must reach validation, not become an empty default list.
        settings.DefaultValueHandling = DefaultValueHandling.Include;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var memory = JsonConvert.DeserializeObject<FightMemory>(json, settings);
                if (memory == null || memory.TerritoryId == 0 || memory.FormatVersion != FightMemory.CurrentFormatVersion ||
                    Path.GetFileNameWithoutExtension(file) != memory.TerritoryId.ToString())
                    throw new IOException("Learned history has an unsupported format or mismatched territory; its file was preserved.");

                // Validate before normalization or recomputation: explicit null/invalid lists
                // are malformed history to preserve, not evidence that the fight was empty.
                memory = LearnedPersistenceQueue.Capture(memory);
                foreach (var cast in memory.Casts)
                {
                    cast.Recompute();
                }

                memories[memory.TerritoryId] = memory;
            }
            catch (Exception ex)
            {
                if (uint.TryParse(Path.GetFileNameWithoutExtension(file), out var territory) && territory != 0)
                {
                    unreadableTerritories.Add(territory);
                    preparationErrors[territory] = "Learned history for territory " + territory + " could not be read; its file is preserved for recovery.";
                }
                Plugin.Log.Error(ex, "Could not read learned data from {File}.", file);
            }
        }

        if (memories.Count > 0)
            Plugin.Log.Information("Loaded learned timings for {Count} fight(s).", memories.Count);
    }

    private void Save(FightMemory memory)
    {
        if (!initialized) return;
        try
        {
            if (unreadableTerritories.Contains(memory.TerritoryId))
                throw new IOException("Existing learned history is unreadable and was preserved. Recover it or explicitly forget this territory before replacing it.");
            persistence.Save(memory);
            pendingDeletes.Remove(memory.TerritoryId); // A new saved generation supersedes any older delete retry.
            preparationErrors.Remove(memory.TerritoryId);
        }
        catch (Exception ex)
        {
            preparationErrors[memory.TerritoryId] = "Could not prepare learned history: " + ex.Message;
            Plugin.Log.Error(ex, "Could not save learned data for {Name}.", memory.Name);
        }
    }

    public void SaveAll()
    {
        if (disposed || !initialized) return;
        SaveAllCore();
    }

    private void SaveAllCore()
    {
        if (!initialized) return;
        PollStorage(Plugin.Framework);
        foreach (var memory in memories.Values)
            Save(memory);
        foreach (var territory in pendingDeletes.Where(p => p.Value.Failed).Select(p => p.Key).ToArray())
            pendingDeletes[territory] = (persistence.Delete(territory), false);
    }

    private void PollStorage(IFramework _)
    {
        TryInitialize();
        while (persistence.TryTakeCompletion(out var result))
        {
            if (pendingDeletes.TryGetValue(result.TerritoryId, out var deletion) && deletion.Revision == result.Revision)
            {
                if (result.Outcome == LearnedSaveOutcome.Failed) pendingDeletes[result.TerritoryId] = (result.Revision, true);
                else if (result.Outcome == LearnedSaveOutcome.Saved) pendingDeletes.Remove(result.TerritoryId);
            }
            if (result.Outcome == LearnedSaveOutcome.Failed)
                Plugin.Log.Error(new IOException(result.Error), "Learned-history storage failed for territory {Territory}.", result.TerritoryId);
        }
    }

    public void Dispose() => Dispose(saveChanges: true);

    internal void Dispose(bool saveChanges)
    {
        if (disposed) return;
        disposed = true;
        // Unhook first, including partial construction. Workers only own detached data and IO.
        Plugin.Encounter.CombatStarted -= OnCombatStarted;
        Plugin.Encounter.CombatEnded -= OnCombatEnded;
        Plugin.Encounter.CastStarted -= OnCastStarted;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.Framework.Update -= PollStorage;

        try
        {
            if (saveChanges) { CommitPull(cleared: false); SaveAllCore(); }
        }
        finally
        {
            if (!persistence.FlushAsync(shutdownTimeout, stopAccepting: true, discardPending: !saveChanges).GetAwaiter().GetResult())
                Plugin.Log.Warning("Learned history did not finish saving before shutdown: {Error}", StorageError ?? "storage is still busy");
            PollStorage(Plugin.Framework);
        }
    }
}
