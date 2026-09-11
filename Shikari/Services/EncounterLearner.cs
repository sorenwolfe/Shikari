using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Shikari.Model;

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
    private readonly JsonSerializerSettings settings = PlanJson.Readable();

    private uint currentTerritory;
    private bool pullCommitted;

    public EncounterLearner()
    {
        directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "learned");
        Directory.CreateDirectory(directory);
        LoadAll();

        currentTerritory = Plugin.ClientState.TerritoryType;

        Plugin.Encounter.CombatStarted += OnCombatStarted;
        Plugin.Encounter.CombatEnded += OnCombatEnded;
        Plugin.Encounter.CastStarted += OnCastStarted;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
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
    public FightMemory? Current => memories.GetValueOrDefault(currentTerritory);

    public IEnumerable<FightMemory> All => memories.Values.OrderByDescending(m => m.LastSeenUtc);

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
        pullBuffer.Clear();
        pullCommitted = false;
        Drift = 0f;
        DriftConfirmed = false;
        DriftAnchor = string.Empty;
    }

    private void OnCastStarted(CastEvent evt)
    {
        if (!Plugin.Config.LearningEnabled)
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
        Drift = 0f;
        DriftConfirmed = false;
    }

    /// <summary>Called when the duty is completed, so a clear can be counted as one.</summary>
    public void NoteClear() => CommitPull(cleared: true);

    private void CommitPull(bool cleared)
    {
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
        memories.Remove(memory.TerritoryId);
        try
        {
            var path = PathFor(memory.TerritoryId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not delete learned data for territory {Id}.", memory.TerritoryId);
        }
    }

    /// <summary>Clears the timings but keeps the fight, for when a patch retunes it.</summary>
    public void ForgetTimings(FightMemory memory)
    {
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

    private void LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var memory = JsonConvert.DeserializeObject<FightMemory>(json, settings);
                if (memory == null || memory.TerritoryId == 0)
                    continue;

                memory.Casts ??= new List<LearnedCast>();
                foreach (var cast in memory.Casts)
                {
                    cast.Samples ??= new List<float>();
                    cast.Recompute();
                }

                memories[memory.TerritoryId] = memory;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Could not read learned data from {File}.", file);
            }
        }

        if (memories.Count > 0)
            Plugin.Log.Information("Loaded learned timings for {Count} fight(s).", memories.Count);
    }

    private void Save(FightMemory memory)
    {
        try
        {
            var json = JsonConvert.SerializeObject(memory, settings);
            File.WriteAllText(PathFor(memory.TerritoryId), json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not save learned data for {Name}.", memory.Name);
        }
    }

    public void SaveAll()
    {
        foreach (var memory in memories.Values)
            Save(memory);
    }

    private string PathFor(uint territory) => Path.Combine(directory, territory + ".json");

    public void Dispose() => Dispose(saveChanges: true);

    internal void Dispose(bool saveChanges)
    {
        // Unhook first. Committing the pull touches disk, and a failure there must not leave us
        // subscribed to events that will fire into an unloaded assembly.
        Plugin.Encounter.CombatStarted -= OnCombatStarted;
        Plugin.Encounter.CombatEnded -= OnCombatEnded;
        Plugin.Encounter.CastStarted -= OnCastStarted;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;

        if (saveChanges)
        {
            CommitPull(cleared: false);
            SaveAll();
        }
    }
}
