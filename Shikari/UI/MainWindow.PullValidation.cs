using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Replay;
using Shikari.UI.Theme;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private readonly PullValidationSession pullValidation = new();
    private readonly ArenaCanvas validationCanvas = new();
    private readonly List<PullExpectedAssignment> validationExpected = new();
    private List<PullComparisonRow> validationRecordedRows = new();
    private List<PullComparisonRow> validationExpectedRows = new();
    private PullValidationResult? validationCompared;
    private long validationEditRevision;
    private long validationActorId;
    private string validationAttempt = "";
    private bool validationCurrentPlan;
    private bool validationIncludeDisabled;
    private bool validationPreview;
    private AdaptiveDecision? validationSelectedDecision;
    private float validationSelectedTime;
    private PullValidationScenario validationScenario;
    private int validationTerritory;
    private float validationGapStart;
    private string validationExpectedRule = "";
    private int validationExpectedOccurrence = 1;
    private int validationExpectedOutcome = -3;

    private void InvalidatePullValidation()
    {
        validationEditRevision++;
        pullValidation.Cancel();
        validationActorId = 0;
        ClearValidationComparisons();
    }

    private void ClearValidationComparisons()
    {
        validationExpected.Clear(); validationCompared = null;
        validationRecordedRows.Clear(); validationExpectedRows.Clear();
        validationExpectedRule = ""; validationExpectedOutcome = -3;
        validationSelectedDecision = null;
    }

    private PlanDocument? ValidationPlan(ReplayAttempt attempt) => validationCurrentPlan
        ? Plan?.Id == attempt.Plan.Id ? Plan : null : attempt.Plan;

    private void AdvancePullValidation()
    {
        var attempt = Plugin.Replays.Attempts.FirstOrDefault(a => a.Id == reviewAttemptId);
        var hadInput = pullValidation.Snapshot != null;
        if (validationActorId != 0 && validationActorId != evidenceActor) { InvalidatePullValidation(); return; }
        pullValidation.Poll(attempt == null ? null : ValidationPlan(attempt), attempt, validationEditRevision,
            Plugin.Replays.EvidenceRevision, !Plugin.Encounter.InCombat);
        if (hadInput && pullValidation.Snapshot == null) ClearValidationComparisons();
    }

    private void DrawPullValidation(ReplayAttempt attempt)
    {
        if (validationAttempt != attempt.Id)
        {
            InvalidatePullValidation(); validationAttempt = attempt.Id;
            validationTerritory = 0; validationGapStart = 0;
        }
        if (!ImGui.CollapsingHeader("Validate pull##pull-validation")) return;
        TimelineFor(attempt);
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y * .5f, 140 * UiHelpers.Scale, 350 * UiHelpers.Scale);
        if (ImGui.BeginChild("##pull-validation-panel", new Vector2(0, height), true, ImGuiWindowFlags.None))
        {
            ImGui.TextWrapped("Replay the full assignment rule set. Results stay in Review; your live plan and calls are unaffected.");
            var actor = attempt.Evidence.Actors.FirstOrDefault(a => a.Id == evidenceActor);
            ImGui.SetNextItemWidth(240 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Player##validate", actor?.Name ?? "Choose player"))
            {
                foreach (var candidate in attempt.Evidence.Actors)
                    if (ImGui.Selectable(candidate.Name + "##validate-" + candidate.Id, candidate.Id == evidenceActor))
                    { InvalidatePullValidation(); evidenceActor = candidate.Id; reviewSeat = candidate.SlotIndex; }
                ImGui.EndCombo();
            }
            if (validationActorId != 0 && validationActorId != evidenceActor)
                InvalidatePullValidation();
            ImGui.BeginDisabled(Plan?.Id != attempt.Plan.Id && !validationCurrentPlan);
            if (ImGui.Checkbox("Use current plan instead of recorded plan", ref validationCurrentPlan)) InvalidatePullValidation();
            ImGui.EndDisabled();
            if (ImGui.Checkbox("Include disabled rules for testing", ref validationIncludeDisabled)) InvalidatePullValidation();
            ImGui.SetNextItemWidth(240 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Conditions##validate", ValidationScenarioName(validationScenario)))
            {
                foreach (var scenario in Enum.GetValues<PullValidationScenario>())
                    if (ImGui.Selectable(ValidationScenarioName(scenario), scenario == validationScenario))
                    { InvalidatePullValidation(); validationScenario = scenario; }
                ImGui.EndCombo();
            }
            if (validationScenario == PullValidationScenario.ObservationGap)
            {
                ImGui.SetNextItemWidth(230 * UiHelpers.Scale);
                if (ImGui.SliderFloat("One-second gap starts", ref validationGapStart, 0, Math.Max(0, attempt.Duration - 1), "%.1f s"))
                    InvalidatePullValidation();
                ImGui.TextDisabled("Synthetic missing observations; later evidence must establish a fresh assignment.");
            }
            var plan = ValidationPlan(attempt);
            if (plan?.AdaptiveMechanics.Count == 0) ImGui.TextWrapped(!validationCurrentPlan && Plan?.Id == attempt.Plan.Id && Plan.AdaptiveMechanics.Count > 0
                ? "The recorded plan has no assignment rules. Use the current plan to test rules added after this pull."
                : "This plan has no assignment rules yet. Draft one from recorded statuses or create one in Plan → Adaptive.");
            else if (plan != null && !validationIncludeDisabled && plan.AdaptiveMechanics.All(r => !r.Enabled))
                ImGui.TextWrapped("All rules are disabled. Include disabled rules above to test these drafts without enabling them live.");
            var territory = attempt.TerritoryId;
            if (territory == 0)
            {
                var choices = plan?.AdaptiveMechanics.Where(r => r.Enabled || validationIncludeDisabled)
                    .Select(r => r.TerritoryId).Where(t => t > 0).Distinct().ToArray() ?? Array.Empty<uint>();
                if (validationTerritory > 0) territory = (uint)validationTerritory;
                else if (choices.Length == 1) territory = choices[0];
                ImGui.SetNextItemWidth(150 * UiHelpers.Scale);
                if (ImGui.InputInt("Test territory (0 = plan)", ref validationTerritory))
                { validationTerritory = Math.Max(0, validationTerritory); InvalidatePullValidation(); }
                ImGui.TextDisabled("Choosing a test territory does not verify the log's encounter.");
                if (territory == 0) ImGui.TextWrapped("Set a test territory or select rules for a single duty before running validation.");
            }
            ImGui.BeginDisabled(Plugin.Encounter.InCombat || plan == null || actor == null || territory == 0 || pullValidation.Running);
            if (ImGui.Button("Validate pull"))
            {
                validationCompared = null;
                validationActorId = evidenceActor;
                pullValidation.Start(plan!, attempt, evidenceActor,
                    new(territory, validationIncludeDisabled, validationScenario, validationGapStart),
                    validationEditRevision, Plugin.Replays.EvidenceRevision);
            }
            ImGui.EndDisabled();
            if (pullValidation.Running)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel validation")) InvalidatePullValidation();
                ImGui.TextDisabled("Replaying observations…");
            }
            if (pullValidation.Error != null) ImGui.TextWrapped(pullValidation.Error);
            if (pullValidation.Result is { } result)
            {
                if (!ReferenceEquals(validationCompared, result))
                {
                    validationCompared = result;
                    validationRecordedRows = pullValidation.RecordedRows.ToList();
                    validationExpectedRows = PullValidationComparison.CompareExpected(result, validationExpected);
                }
                DrawPullValidationResult(result);
            }
        }
        ImGui.EndChild();
    }

    private static string ValidationScenarioName(PullValidationScenario scenario) => scenario switch
    {
        PullValidationScenario.DelayedPolling => "Slower polling (stress test)",
        PullValidationScenario.ObservationGap => "Missing observations (stress test)",
        _ => "Recorded timing",
    };

    private void DrawPullValidationResult(PullValidationResult result)
    {
        ImGui.Separator();
        ImGui.TextWrapped($"{result.ActiveRules} eligible rules · {result.ExcludedRules} excluded · {result.Decisions.Count} decisions");
        ImGui.TextColored(Palette.Vec(result.Complete && result.ScopeVerified ? Palette.Good : Palette.Attention),
            result.Complete && result.ScopeVerified ? "Recording processed; compare assignments below." : "Evidence or encounter scope is incomplete.");
        foreach (var notice in result.Notices) ImGui.TextWrapped(notice);
        ImGui.Checkbox("Preview assignment board and cue", ref validationPreview);
        if (validationPreview) DrawValidationPreview(result);
        if (ImGui.TreeNode("Simulated decisions"))
        {
            for (var i = 0; i < result.Decisions.Count; i++)
            {
                var decision = result.Decisions[i];
                ImGui.PushID("validation-decision-" + i);
                if (ImGui.Selectable($"{decision.Time:0.0}s · {decision.Mechanic} · use {decision.Occurrence}"))
                    SelectValidationDecision(result, decision);
                ImGui.TextWrapped(decision.Reason);
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
        if (ImGui.TreeNode("Compare recorded decisions"))
        {
            ImGui.TextWrapped("Checks the recorded local player's assignments. Timing differences are reported separately; recorded navigation is historical evidence.");
            DrawValidationComparisons(result, validationRecordedRows);
            ImGui.TreePop();
        }
        if (ImGui.TreeNode("Reviewed expectations"))
        {
            ImGui.TextWrapped("Choose the outcome you verified from the strategy and pull. These expectations belong to this player and tested plan for this session; edits or switching players clear them.");
            var rule = result.Plan.AdaptiveMechanics.FirstOrDefault(r => r.Id == validationExpectedRule);
            ImGui.SetNextItemWidth(240 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Expected mechanic", rule?.Label ?? "Choose mechanic"))
            {
                foreach (var candidate in result.Plan.AdaptiveMechanics)
                    if (ImGui.Selectable(candidate.Label + "##expected-" + candidate.Id, candidate == rule))
                    { validationExpectedRule = candidate.Id; validationExpectedOutcome = -3; }
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(110 * UiHelpers.Scale);
            if (ImGui.InputInt("Cast occurrence", ref validationExpectedOccurrence)) validationExpectedOccurrence = Math.Clamp(validationExpectedOccurrence, 1, 1000);
            ImGui.SetNextItemWidth(240 * UiHelpers.Scale);
            var label = validationExpectedOutcome == -1 ? "No assignment" : validationExpectedOutcome == -2 ? "Conflicting assignments" :
                rule != null && validationExpectedOutcome >= 0 && validationExpectedOutcome < rule.Branches.Count
                    ? rule.Branches[validationExpectedOutcome].Label : "Choose reviewed outcome";
            if (ImGui.BeginCombo("Expected outcome", label))
            {
                if (ImGui.Selectable("No assignment")) validationExpectedOutcome = -1;
                if (ImGui.Selectable("Conflicting assignments")) validationExpectedOutcome = -2;
                if (rule != null)
                    for (var i = 0; i < rule.Branches.Count; i++)
                        if (ImGui.Selectable(rule.Branches[i].Label + "##expected-branch-" + i)) validationExpectedOutcome = i;
                ImGui.EndCombo();
            }
            ImGui.BeginDisabled(rule == null || validationExpectedOutcome < -2 || validationExpected.Count >= 1024);
            if (ImGui.SmallButton("Add reviewed expectation"))
            {
                validationExpected.RemoveAll(e => e.RuleId == rule!.Id && e.Occurrence == validationExpectedOccurrence);
                validationExpected.Add(new PullExpectedAssignment { RuleId = rule!.Id, AnchorActionId = rule.AnchorActionId,
                    Occurrence = validationExpectedOccurrence, BranchIndex = Math.Max(-1, validationExpectedOutcome),
                    Conflict = validationExpectedOutcome == -2 });
                validationExpectedRows = PullValidationComparison.CompareExpected(result, validationExpected);
            }
            ImGui.EndDisabled();
            if (validationExpected.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear expectations"))
                { validationExpected.Clear(); validationExpectedRows = PullValidationComparison.CompareExpected(result, validationExpected); }
            }
            DrawValidationComparisons(result, validationExpectedRows);
            ImGui.TreePop();
        }
    }

    private void DrawValidationComparisons(PullValidationResult result, IReadOnlyList<PullComparisonRow> rows)
    {
        var index = 0;
        foreach (var row in rows)
        {
            ImGui.PushID("validation-comparison-" + index++);
            var rule = result.Plan.AdaptiveMechanics.FirstOrDefault(r => r.Id == row.RuleId);
            if (rule != null) ImGui.TextWrapped(rule.Label);
            ImGui.TextColored(Palette.Vec(row.Outcome == PullComparisonOutcome.Matched ? Palette.Good : Palette.Attention),
                row.Outcome + (row.Occurrence > 0 ? $" · use {row.Occurrence}" : ""));
            ImGui.TextWrapped(row.Reason);
            if (row.SimulatedBranchIndex.HasValue) ImGui.TextWrapped("Simulated: " + ValidationOutcomeLabel(rule, row.SimulatedBranchIndex.Value, row.SimulatedConflict == true));
            if (row.ReferenceBranchIndex.HasValue) ImGui.TextWrapped((row.RecordedApplied.HasValue ? "Recorded: " : "Reviewed: ") +
                ValidationOutcomeLabel(rule, row.ReferenceBranchIndex.Value, row.ReferenceConflict == true));
            if (row.TimeDelta is { } delta) ImGui.TextDisabled($"Simulation minus recording: {delta:+0.00;-0.00;0.00}s");
            if (row.RecordedApplied is { } applied)
                ImGui.TextWrapped("Recorded navigation: " + (applied ? "applied. " : "not applied. ") + row.RecordedNavigation);
            if ((row.SimulatedTime ?? row.ReferenceTime) is { } time && ImGui.SmallButton("View decision"))
            {
                var candidates = result.Decisions.Where(d => d.RuleId == row.RuleId && d.AnchorActionId == row.AnchorActionId &&
                    d.Occurrence == row.Occurrence && d.Time == time).ToArray();
                if (candidates.Length == 1) SelectValidationDecision(result, candidates[0]);
                else { validationSelectedDecision = null; reviewTime = Math.Clamp(time, 0, result.Duration); reviewPlaying = false; validationPreview = true; }
            }
            ImGui.PopID();
        }
    }

    private static string ValidationOutcomeLabel(AdaptiveMechanic? rule, int branch, bool conflict) => conflict ? "Conflicting assignments" :
        branch >= 0 && rule != null && branch < rule.Branches.Count ? rule.Branches[branch].Label : "No assignment";

    private void SelectValidationDecision(PullValidationResult result, AdaptiveDecision decision)
    {
        reviewTime = Math.Clamp(decision.Time, 0, result.Duration);
        reviewPlaying = false; validationPreview = true;
        validationSelectedDecision = decision; validationSelectedTime = reviewTime;
    }

    private void DrawValidationPreview(PullValidationResult result)
    {
        if (reviewTime != validationSelectedTime || validationSelectedDecision != null && !result.Decisions.Contains(validationSelectedDecision))
            validationSelectedDecision = null;
        var decision = validationSelectedDecision ?? result.Decisions.LastOrDefault(d => d.Time <= reviewTime);
        if (decision == null) { ImGui.TextDisabled("No simulated assignment at this replay time. Select a decision or scrub forward."); return; }
        if (reviewTime - decision.Time > 6) { ImGui.TextDisabled("The assignment cue has expired. Select its decision to review it."); return; }
        var slide = result.Plan.FindSlide(decision.SlideId);
        var rule = result.Plan.AdaptiveMechanics.FirstOrDefault(r => r.Id == decision.RuleId);
        var branch = rule != null && decision.BranchIndex >= 0 && decision.BranchIndex < rule.Branches.Count ? rule.Branches[decision.BranchIndex] : null;
        var cue = slide == null ? "Assignment unclear — check the mechanic" : !string.IsNullOrWhiteSpace(branch?.Label) && branch.Label != "New branch" ? branch.Label : slide.Title;
        ImGui.TextColored(Palette.Vec(Palette.Accent), $"Assignment decision preview · {decision.Time:0.0}s");
        ImGui.TextWrapped(cue);
        ImGui.TextDisabled("Preview assumes following is enabled. Actual follow settings, manual holds and Ember delivery are not replayed.");
        if (slide == null) return;
        ImGui.TextWrapped("Board: " + slide.Title);
        validationCanvas.HighlightSlot = -1;
        validationCanvas.FocusOnMe = false;
        validationCanvas.Draw(result.Plan, slide, new Vector2(ImGui.GetContentRegionAvail().X, 230 * UiHelpers.Scale), false);
        ImGui.TextWrapped("Recorded movement remains on the replay map. A player's position alone does not prove this assignment was correct.");
    }
}
