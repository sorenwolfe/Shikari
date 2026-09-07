using System;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Services.Replay;

public readonly record struct EvidenceStatusSample(uint Id, float Remaining, int Parameter, uint Source);

/// <summary>Snapshot differences preserve baselines, removals, and gaps for every readable actor.</summary>
public sealed class EvidenceRecorder
{
    private readonly Dictionary<long, Dictionary<(uint, uint), EvidenceStatusSample>> previous = new();
    public void Observe(ReplayEvidence evidence, long actor, float time, IEnumerable<EvidenceStatusSample>? samples)
    {
        var ready = previous.TryGetValue(actor, out var old);
        if (samples == null)
        {
            if (ready) Add(evidence, new EvidenceStatus { ActorId = actor, Time = time, Change = "unavailable" });
            previous.Remove(actor);
            return;
        }
        var current = new Dictionary<(uint, uint), EvidenceStatusSample>();
        foreach (var s in samples.Take(128))
        {
            if (s.Id == 0 || !float.IsFinite(s.Remaining) || s.Remaining < 0) continue;
            var key = (s.Id, s.Source);
            current[key] = s;
            if (!ready || !old!.TryGetValue(key, out var before) || before.Parameter != s.Parameter || s.Remaining > before.Remaining + 1)
                Add(evidence, new EvidenceStatus { ActorId = actor, SourceId = s.Source, Time = time, StatusId = s.Id,
                    Duration = s.Remaining > 0 ? s.Remaining : null, Parameter = s.Parameter,
                    Baseline = !ready, Change = ready && old!.ContainsKey(key) ? "refresh" : "apply" });
        }
        if (ready)
            foreach (var s in old!.Values.Where(s => !current.ContainsKey((s.Id, s.Source))))
                Add(evidence, new EvidenceStatus { ActorId = actor, SourceId = s.Source, Time = time, StatusId = s.Id, Change = "remove" });
        previous[actor] = current;
    }
    private static void Add(ReplayEvidence evidence, EvidenceStatus item)
    {
        if (evidence.Statuses.Count < ReplayEvidence.MaxStatuses) evidence.Statuses.Add(item);
        else if (evidence.Complete)
        {
            evidence.Complete = false;
            evidence.Warnings.Add("Status recording reached its limit. Later assignments may be missing.");
        }
    }
}
