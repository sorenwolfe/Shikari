using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using Shikari.Model;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Shikari.Services;

/// <summary>A single searchable action pulled out of the game's Action sheet.</summary>
public sealed class ActionEntry
{
    public uint RowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SearchName { get; init; } = string.Empty;
    public ushort IconId { get; init; }
    public uint ClassJobId { get; init; }
    public uint CategoryId { get; init; }
    public bool IsRoleAction { get; init; }
    public bool IsPlayerAction { get; init; }
    public byte Level { get; init; }
    public float RecastSeconds { get; init; }
    public string JobAbbreviation { get; init; } = string.Empty;

    /// <summary>
    /// Worth putting on a raid plan. Anything on a real cooldown, plus the role actions, which is
    /// mitigation, utility and burst windows — and excludes the rotation, which nobody assigns.
    /// </summary>
    public bool IsCooldown { get; init; }
}

/// <summary>Minimal job record used to colour and filter the spell picker.</summary>
public sealed class JobEntry
{
    public uint RowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Abbreviation { get; init; } = string.Empty;
    public RaidRole Role { get; init; }
    public bool IsCombatJob { get; init; }
}

/// <summary>
/// Builds and searches an in-memory index of the game's actions. Two views are kept: the
/// player-usable actions offered by the assignment picker, and every named action, which is
/// what the timeline needs when you anchor a step to a boss cast.
/// </summary>
public sealed class ActionIndex
{
    // Construction owns its mutable collections exclusively. Readers only ever see one
    // completed snapshot, published with the same memory barrier that makes Ready true.
    private sealed record Snapshot(
        IReadOnlyList<ActionEntry> PlayerActions,
        IReadOnlyList<ActionEntry> PlayerCooldowns,
        IReadOnlyList<ActionEntry> AllActions,
        FrozenDictionary<uint, ActionEntry> ById,
        FrozenDictionary<uint, JobEntry> JobsById,
        IReadOnlyList<JobEntry> Jobs,
        FrozenDictionary<uint, FrozenSet<uint>> CategoryJobs);

    private Snapshot? snapshot;

    public bool Ready => Volatile.Read(ref snapshot) != null;

    public IReadOnlyList<JobEntry> Jobs => Volatile.Read(ref snapshot)?.Jobs ?? Array.Empty<JobEntry>();

    /// <summary>Kick off the index build on a worker thread; the UI stays responsive meanwhile.</summary>
    public Task BuildAsync(CancellationToken cancel = default)
    {
        return Task.Run(() =>
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                var built = Build(cancel);

                // Unloaded while we were reading sheets. Say nothing and touch nothing.
                cancel.ThrowIfCancellationRequested();

                Volatile.Write(ref snapshot, built);
                Plugin.Log.Information(
                    "Action index ready: {Cooldowns} cooldowns of {Player} player actions, {All} total, {Jobs} jobs.",
                    built.PlayerCooldowns.Count, built.PlayerActions.Count, built.AllActions.Count, built.JobsById.Count);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Cancellation is normal during unload; no partial snapshot is published.
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to build the action index.");
            }
        });
    }

    private static Snapshot Build(CancellationToken cancel)
    {
        var playerActions = new List<ActionEntry>();
        var playerCooldowns = new List<ActionEntry>();
        var allActions = new List<ActionEntry>();
        var byId = new Dictionary<uint, ActionEntry>();
        var jobsById = new Dictionary<uint, JobEntry>();
        var jobs = BuildJobs(jobsById, cancel);

        cancel.ThrowIfCancellationRequested();
        var actionSheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
        if (actionSheet != null)
        {
            foreach (var row in actionSheet)
            {
                cancel.ThrowIfCancellationRequested();
                if (row.RowId == 0)
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var jobId = row.ClassJob.RowId;
                // ClassJob is stored as -1 (0xFFFFFFFF) for actions with no owning job.
                if (jobId == uint.MaxValue)
                    jobId = 0;

                var isPlayerAction = !row.IsPvP && (row.ClassJobLevel > 0 || row.IsRoleAction);

                var recast = row.Recast100ms / 10f;
                var isCooldown = ActionFilter.IsCooldown(recast, row.IsRoleAction, isPlayerAction);

                var entry = new ActionEntry
                {
                    RowId = row.RowId,
                    Name = name,
                    SearchName = name.ToLowerInvariant(),
                    IconId = row.Icon,
                    ClassJobId = jobId,
                    CategoryId = row.ClassJobCategory.RowId,
                    IsRoleAction = row.IsRoleAction,
                    IsPlayerAction = isPlayerAction,
                    Level = row.ClassJobLevel,
                    RecastSeconds = recast,
                    IsCooldown = isCooldown,
                    JobAbbreviation = jobsById.TryGetValue(jobId, out var job) ? job.Abbreviation : string.Empty,
                };

                allActions.Add(entry);
                byId[entry.RowId] = entry;

                if (isPlayerAction)
                    playerActions.Add(entry);

                if (isCooldown)
                    playerCooldowns.Add(entry);
            }
        }

        cancel.ThrowIfCancellationRequested();
        allActions.Sort(static (a, b) => string.CompareOrdinal(a.SearchName, b.SearchName));
        playerActions.Sort(static (a, b) => string.CompareOrdinal(a.SearchName, b.SearchName));
        playerCooldowns.Sort(static (a, b) => string.CompareOrdinal(a.SearchName, b.SearchName));

        var categoryJobs = BuildCategoryMap(jobsById, cancel);
        cancel.ThrowIfCancellationRequested();
        return new Snapshot(
            playerActions.AsReadOnly(), playerCooldowns.AsReadOnly(), allActions.AsReadOnly(),
            byId.ToFrozenDictionary(), jobsById.ToFrozenDictionary(), jobs,
            categoryJobs.ToFrozenDictionary());
    }

    private static IReadOnlyList<JobEntry> BuildJobs(Dictionary<uint, JobEntry> jobsById, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (sheet == null)
            return Array.Empty<JobEntry>();

        var list = new List<JobEntry>();
        foreach (var row in sheet)
        {
            cancel.ThrowIfCancellationRequested();
            if (row.RowId == 0)
                continue;

            var abbr = row.Abbreviation.ExtractText();
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(abbr) || string.IsNullOrWhiteSpace(name))
                continue;

            var entry = new JobEntry
            {
                RowId = row.RowId,
                Name = Capitalise(name),
                Abbreviation = abbr.ToUpperInvariant(),
                Role = JobRoles.RoleFor(abbr.ToUpperInvariant()),
                IsCombatJob = JobRoles.RoleFor(abbr.ToUpperInvariant()) != RaidRole.Unknown,
            };

            jobsById[entry.RowId] = entry;
            list.Add(entry);
        }

        cancel.ThrowIfCancellationRequested();
        return list
            .OrderByDescending(j => j.IsCombatJob)
            .ThenBy(j => (int)j.Role)
            .ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToList().AsReadOnly();
    }

    /// <summary>
    /// ClassJobCategory exposes one boolean column per job abbreviation. Reading them once at
    /// startup gives a cheap "can job X use category Y" lookup for the rest of the session.
    /// </summary>
    private static Dictionary<uint, FrozenSet<uint>> BuildCategoryMap(
        Dictionary<uint, JobEntry> jobsById, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var categoryJobs = new Dictionary<uint, FrozenSet<uint>>();
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJobCategory>();
        if (sheet == null)
            return categoryJobs;

        var accessors = new List<(uint JobId, PropertyInfo Property)>();
        foreach (var job in jobsById.Values)
        {
            cancel.ThrowIfCancellationRequested();
            var prop = typeof(ClassJobCategory).GetProperty(
                job.Abbreviation,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(bool))
                accessors.Add((job.RowId, prop));
        }

        foreach (var row in sheet)
        {
            cancel.ThrowIfCancellationRequested();
            var set = new HashSet<uint>();
            object boxed = row;
            foreach (var (jobId, prop) in accessors)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (prop.GetValue(boxed) is true)
                        set.Add(jobId);
                }
                catch
                {
                    // A malformed row should not take the whole index down.
                }
            }

            categoryJobs[row.RowId] = set.ToFrozenSet();
        }
        return categoryJobs;
    }

    public ActionEntry? Get(uint actionId) => Volatile.Read(ref snapshot)?.ById.GetValueOrDefault(actionId);

    public string NameOf(uint actionId, string fallback = "")
    {
        var entry = Get(actionId);
        if (entry != null)
            return entry.Name;
        return string.IsNullOrEmpty(fallback) ? $"Action #{actionId}" : fallback;
    }

    public JobEntry? Job(uint jobId) => Volatile.Read(ref snapshot)?.JobsById.GetValueOrDefault(jobId);

    public string JobAbbreviation(uint jobId) => Job(jobId)?.Abbreviation ?? "???";

    public bool CanJobUse(ActionEntry entry, uint jobId) => CanJobUse(Volatile.Read(ref snapshot), entry, jobId);

    private static bool CanJobUse(Snapshot? current, ActionEntry entry, uint jobId)
    {
        if (jobId == 0)
            return true;
        if (entry.ClassJobId == jobId)
            return true;
        if (current != null && current.CategoryJobs.TryGetValue(entry.CategoryId, out var jobs))
            return jobs.Contains(jobId);
        return false;
    }

    /// <summary>
    /// Searches player-usable actions, optionally narrowed to one job. Exact and prefix matches
    /// are ranked above matches buried in the middle of a name.
    /// </summary>
    public List<ActionEntry> SearchPlayerActions(string query, uint jobId, bool cooldownsOnly, int limit = 60)
    {
        var current = Volatile.Read(ref snapshot);
        if (current == null)
            return new List<ActionEntry>();
        var source = cooldownsOnly ? current.PlayerCooldowns : current.PlayerActions;
        return Search(current, source, query, jobId, limit);
    }

    /// <summary>How many of a job's actions survive the cooldown filter, for the UI to show.</summary>
    public int CooldownCount(uint jobId)
    {
        var current = Volatile.Read(ref snapshot);
        return current?.PlayerCooldowns.Count(e => jobId == 0 || CanJobUse(current, e, jobId)) ?? 0;
    }

    /// <summary>Searches every named action, which is what boss casts live in.</summary>
    public List<ActionEntry> SearchAllActions(string query, int limit = 60)
    {
        var current = Volatile.Read(ref snapshot);
        return current == null ? new List<ActionEntry>() : Search(current, current.AllActions, query, 0, limit);
    }

    private static List<ActionEntry> Search(
        Snapshot current, IReadOnlyList<ActionEntry> source, string query, uint jobId, int limit)
    {
        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        var results = new List<(int Rank, ActionEntry Entry)>();

        foreach (var entry in source)
        {
            if (jobId != 0 && !CanJobUse(current, entry, jobId))
                continue;

            int rank;
            if (q.Length == 0)
            {
                rank = 3;
            }
            else if (entry.SearchName == q)
            {
                rank = 0;
            }
            else if (entry.SearchName.StartsWith(q, StringComparison.Ordinal))
            {
                rank = 1;
            }
            else if (entry.SearchName.Contains(q, StringComparison.Ordinal))
            {
                rank = 2;
            }
            else
            {
                continue;
            }

            results.Add((rank, entry));
            if (q.Length == 0 && results.Count >= limit * 4)
                break;
        }

        return results
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Entry.Name.Length)
            .ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => r.Entry)
            .ToList();
    }

    private static string Capitalise(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}

/// <summary>Maps job abbreviations onto the roles a raid plan cares about.</summary>
public static class JobRoles
{
    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase)
        { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };

    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
        { "WHM", "SCH", "AST", "SGE", "CNJ" };

    private static readonly HashSet<string> Melee = new(StringComparer.OrdinalIgnoreCase)
        { "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "PGL", "LNC", "ROG" };

    private static readonly HashSet<string> PhysRanged = new(StringComparer.OrdinalIgnoreCase)
        { "BRD", "MCH", "DNC", "ARC" };

    private static readonly HashSet<string> MagRanged = new(StringComparer.OrdinalIgnoreCase)
        { "BLM", "SMN", "RDM", "PCT", "BLU", "THM", "ACN" };

    public static RaidRole RoleFor(string abbreviation)
    {
        if (Tanks.Contains(abbreviation)) return RaidRole.Tank;
        if (Healers.Contains(abbreviation)) return RaidRole.Healer;
        if (Melee.Contains(abbreviation)) return RaidRole.Melee;
        if (PhysRanged.Contains(abbreviation)) return RaidRole.PhysicalRanged;
        if (MagRanged.Contains(abbreviation)) return RaidRole.MagicalRanged;
        return RaidRole.Unknown;
    }
}
