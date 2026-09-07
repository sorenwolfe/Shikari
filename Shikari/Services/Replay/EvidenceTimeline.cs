using System;
using System.Collections.Generic;
using System.Linq;
using Shikari.Services.Live;

namespace Shikari.Services.Replay;

public sealed class EvidenceTimeline
{
    public const float MaxPositionAge = .25f;
    private readonly Dictionary<long, EvidenceStatus[]> statuses;
    private readonly Dictionary<long, EvidencePosition[]> positions;
    public EvidenceTimeline(ReplayEvidence evidence)
    {
        statuses = evidence.Statuses.GroupBy(s => s.ActorId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Time).ToArray());
        positions = evidence.Positions.GroupBy(s => s.ActorId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Time).ToArray());
    }
    public IReadOnlyList<EvidenceStatus> StatusesAt(long actorId, float time)
    {
        var active = new Dictionary<(uint, uint, long), EvidenceStatus>();
        if (!float.IsFinite(time) || !statuses.TryGetValue(actorId, out var events)) return Array.Empty<EvidenceStatus>();
        foreach (var e in events)
        {
            if (e.Time > time) break;
            if (e.Change == "unavailable") { active.Clear(); continue; }
            var key = (e.StatusId, e.AbilityId, e.SourceId);
            if (e.Change == "remove") active.Remove(key);
            else if (e.Change == "stacks" && active.TryGetValue(key, out var prior))
                active[key] = new EvidenceStatus { ActorId = prior.ActorId, SourceId = prior.SourceId,
                    StatusId = prior.StatusId, AbilityId = prior.AbilityId, Name = prior.Name,
                    Time = prior.Time, Duration = prior.Duration, Parameter = prior.Parameter,
                    Baseline = prior.Baseline, Change = prior.Change, Stacks = e.Stacks };
            else if (e.Change == "stacks")
                active[key] = new EvidenceStatus { ActorId = e.ActorId, SourceId = e.SourceId, StatusId = e.StatusId,
                    AbilityId = e.AbilityId, Name = e.Name, Time = e.Time, Stacks = e.Stacks, Baseline = true };
            else active[key] = e;
        }
        return active.Values.Where(e => !e.Duration.HasValue || time < e.Time + e.Duration.Value)
            .OrderBy(e => e.StatusId).ThenBy(e => e.AbilityId).ToArray();
    }
    public EvidencePosition? PositionAt(long actorId, float time)
    {
        if (!float.IsFinite(time) || !positions.TryGetValue(actorId, out var samples)) return null;
        var low = 0; var high = samples.Length - 1; var found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].Time <= time) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        return found >= 0 && time - samples[found].Time <= MaxPositionAge ? samples[found] : null;
    }
}

public static class EvidenceProjection
{
    public static bool TryAlign(ReplayEvidence evidence, out WorldAlignment alignment)
    {
        alignment = default;
        if (evidence.References.Count < 3 || evidence.References.Count > 8 ||
            evidence.References.Any(p => !float.IsFinite(p.Source.X) || !float.IsFinite(p.Source.Y) ||
                !float.IsFinite(p.Board.X) || !float.IsFinite(p.Board.Y))) return false;
        return WorldAlignment.TrySolve(evidence.References.Select(p => new AlignmentPair(p.Source, p.Board)).ToArray(), out alignment)
            && alignment.IsTrustworthy;
    }
}
