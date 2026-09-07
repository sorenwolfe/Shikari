using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private void DrawStrategyEvidence(PlanDocument plan)
    {
        if (plan.StrategyEvidence.Count == 0) return;
        if (!ImGui.CollapsingHeader("Strategy references##strategy-evidence")) return;
        ImGui.TextWrapped("These are observations from pulls. A matching destination does not establish that a mechanic was resolved correctly. Assignment drafts remain disabled until reviewed in Adaptive.");
        if (ImGui.BeginChild("##strategy-references", new Vector2(0, 230 * UiHelpers.Scale), true, ImGuiWindowFlags.None))
        {
            foreach (var reference in plan.StrategyEvidence.AsEnumerable().Reverse())
            {
                ImGui.PushID(reference.Key);
                ImGui.TextWrapped(reference.Source + (reference.FightId > 0 ? $" / fight {reference.FightId}" : "") +
                    $" — {reference.Mechanics.Count(m => m.EntryId.Length > 0)} linked, {reference.Mechanics.Count(m => m.EntryId.Length == 0)} unresolved");
                if (!reference.Complete) ImGui.TextDisabled("Partial recording");
                if (!reference.EncounterVerified) ImGui.TextDisabled("Encounter identity has not been independently verified.");
                foreach (var mechanic in reference.Mechanics)
                {
                    var label = plan.Timeline.FirstOrDefault(e => e.Id == mechanic.EntryId)?.Label ?? "Cast #" + mechanic.ActionId;
                    if (!ImGui.TreeNode($"{mechanic.CastTime:0.0}s  {label} / {mechanic.Match}##{mechanic.ActionId}-{mechanic.Occurrence}-{mechanic.CastTime}")) continue;
                    var slide = plan.FindSlide(mechanic.SlideId);
                    if (slide != null) ImGui.TextWrapped("Board: " + slide.Title);
                    foreach (var actor in mechanic.Actors)
                    {
                        var seat = actor.SlotIndex >= 0 && actor.SlotIndex < plan.Roster.Count
                            ? plan.Roster[actor.SlotIndex].DisplayName : "Unassigned player " + (actor.Actor + 1);
                        var statuses = string.Join(", ", actor.Statuses.Select(s => EvidenceStatusName(new EvidenceStatus {
                            StatusId = s.StatusId, AbilityId = s.AbilityId }) + (s.Baseline ? " (baseline)" : "")));
                        ImGui.TextWrapped(seat + ": " + (statuses.Length > 0 ? statuses : "No usable status observation") +
                            (actor.DestinationDistance.HasValue ? $"; observed distance to plan spot: {actor.DestinationDistance.Value * 100:0.0}% of board width" : "; destination comparison unresolved"));
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }
}
