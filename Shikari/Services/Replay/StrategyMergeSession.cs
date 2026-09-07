using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;

namespace Shikari.Services.Replay;

/// <summary>A source request owns a snapshot; late results cannot edit a different or changed strategy.</summary>
public sealed class StrategyMergeSession
{
    public PlanDocument Snapshot { get; }
    private readonly string fingerprint;
    public StrategyMergeSession(PlanDocument plan)
    {
        Snapshot = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan))!;
        fingerprint = Fingerprint(plan);
    }

    public bool Matches(PlanDocument? plan) => plan != null && plan.Id == Snapshot.Id && Fingerprint(plan) == fingerprint;

    public StrategyEnrichmentResult Apply(PlanDocument plan, ReplayAttempt attempt, Func<bool> save)
    {
        if (!Matches(plan)) return new(false, false, 0, 0, 0,
            "The strategy changed while this reference was loading. Import it again against the current plan.");
        var staged = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan))!;
        var result = StrategyEnrichment.Apply(staged, attempt);
        if (!result.Accepted || !result.Changed) return result;
        var timeline = plan.Timeline; var rules = plan.AdaptiveMechanics; var evidence = plan.StrategyEvidence;
        plan.Timeline = staged.Timeline; plan.AdaptiveMechanics = staged.AdaptiveMechanics; plan.StrategyEvidence = staged.StrategyEvidence;
        try
        {
            if (!save()) throw new InvalidOperationException("Could not save the enriched strategy; its previous state was restored.");
        }
        catch
        {
            plan.Timeline = timeline; plan.AdaptiveMechanics = rules; plan.StrategyEvidence = evidence;
            throw;
        }
        return result;
    }

    public static void LinkReplay(PlanDocument plan, ReplayAttempt attempt)
    {
        var key = attempt.Evidence.Source == "FF Logs"
            ? $"fflogs:{attempt.Evidence.ReportCode}:{attempt.Evidence.FightId}" : "local:" + attempt.Id;
        var attachment = plan.StrategyEvidence.FirstOrDefault(a => a.Key == key);
        if (attachment == null) return;
        foreach (var mechanic in attempt.Mechanics)
        {
            var rows = attachment.Mechanics.Where(m => m.ActionId == mechanic.ActionId &&
                m.Occurrence == mechanic.Occurrence && m.CastTime == mechanic.Time && m.EntryId.Length > 0).ToArray();
            if (rows.Length != 1 || attempt.Plan.FindSlide(rows[0].SlideId) == null) continue;
            mechanic.EntryId = rows[0].EntryId; mechanic.SlideId = rows[0].SlideId;
        }
    }

    private static string Fingerprint(PlanDocument plan)
    {
        var value = JObject.FromObject(plan);
        value.Remove(nameof(PlanDocument.ModifiedUtc));
        return value.ToString(Formatting.None);
    }
}
