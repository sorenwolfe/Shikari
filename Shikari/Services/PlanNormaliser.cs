using System.Collections.Generic;
using Shikari.Model;

namespace Shikari.Services;

/// <summary>
/// Fills in anything a plan might be missing before the UI touches it. Plans arrive from disk,
/// from share codes and from hand editing, and the drawing code should not have to null-check
/// every list it walks.
/// </summary>
public static class PlanNormaliser
{
    public static PlanDocument Normalise(PlanDocument doc)
    {
        doc.Arena ??= new ArenaSettings();
        SlideMetadataValidation.NormaliseArena(doc.Arena);
        doc.Roster ??= new List<PlayerSlot>();
        doc.Slides ??= new List<Slide>();
        doc.Timeline ??= new List<TimelineEntry>();
        doc.AdaptiveMechanics ??= new List<AdaptiveMechanic>();
        doc.AdaptiveMechanics.RemoveAll(r => r == null);
        foreach (var rule in doc.AdaptiveMechanics)
        {
            // Disable before repairing: truncating an AND expression would otherwise broaden an enabled rule.
            if (!rule.IsValid(doc)) rule.Enabled = false;
            rule.Branches ??= new List<StatusBranch>();
            rule.Branches.RemoveAll(b => b == null);
            if (rule.Branches.Count > 16) rule.Branches.RemoveRange(16, rule.Branches.Count - 16);
            foreach (var branch in rule.Branches)
            {
                branch.AdditionalStatuses ??= new List<StatusCondition>();
                branch.AdditionalStatuses.RemoveAll(c => c == null);
                if (branch.AdditionalStatuses.Count > 3)
                    branch.AdditionalStatuses.RemoveRange(3, branch.AdditionalStatuses.Count - 3);
            }
        }
        if (doc.AdaptiveMechanics.Count > 128) doc.AdaptiveMechanics.RemoveRange(128, doc.AdaptiveMechanics.Count - 128);

        if (doc.Slides.Count == 0)
            doc.Slides.Add(new Slide { Title = "Slide 1" });

        if (doc.Roster.Count == 0)
            doc.Roster = PlanDocument.CreateDefault().Roster;

        foreach (var slide in doc.Slides)
        {
            SlideMetadataValidation.Normalise(slide, doc.Arena);
            slide.Items ??= new List<CanvasItem>();
            foreach (var item in slide.Items)
                item.Points ??= new List<System.Numerics.Vector2>();
        }

        foreach (var entry in doc.Timeline)
        {
            entry.Assignments ??= new List<Assignment>();
            entry.SlotCallText ??= new Dictionary<int, string>();
        }

        StrategyEvidenceValidation.Normalise(doc);

        return doc;
    }
}
