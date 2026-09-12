using System;
using System.Linq;
using Shikari.Model;

namespace Shikari.Services.Adaptive;

/// <summary>Privately owned pull rules and destination identities. No drawings or collected pull data.</summary>
internal static class AdaptiveRuleSnapshot
{
    public static PlanDocument Capture(PlanDocument source, uint territory)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Preserve the evaluator's original first-128 limit before applying eligibility.
        // Keep overlapping candidates: the evaluator and buddy still reject those together.
        var candidates = source.AdaptiveMechanics.Take(128)
            .Where(r => r != null && r.Enabled && r.TerritoryId == territory && r.IsValid(source)).ToArray();
        return new PlanDocument
        {
            Id = source.Id,
            AdaptiveMechanics = candidates.Select(r => new AdaptiveMechanic
            {
                Id = r.Id, Label = r.Label, Enabled = r.Enabled, TerritoryId = r.TerritoryId,
                AnchorActionId = r.AnchorActionId, Occurrence = r.Occurrence, WindowSeconds = r.WindowSeconds,
                Branches = r.Branches.Select(b => new StatusBranch
                {
                    Label = b.Label, StatusId = b.StatusId, Parameter = b.Parameter,
                    MinimumSeconds = b.MinimumSeconds, MaximumSeconds = b.MaximumSeconds, SlideId = b.SlideId,
                    AdditionalStatuses = b.AdditionalStatuses.Select(c => new StatusCondition
                    {
                        StatusId = c.StatusId, Parameter = c.Parameter,
                        MinimumSeconds = c.MinimumSeconds, MaximumSeconds = c.MaximumSeconds,
                    }).ToList(),
                }).ToList(),
            }).ToList(),
            // IsValid only needs destination existence; neither consumer reads board geometry.
            Slides = candidates.SelectMany(r => r.Branches).Select(b => b.SlideId).Distinct(StringComparer.Ordinal)
                .Select(id => new Slide { Id = id }).ToList(),
        };
    }
}
