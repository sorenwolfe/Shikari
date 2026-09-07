using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.UI.Theme;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private int workspace;
    private int planTool;
    private readonly ArenaCanvas liveCanvas = new();
    private readonly NavigationMotion navigationMotion = new();
    private readonly Vector2[] navigationRanges = new Vector2[3];
    private readonly List<string> undoEdits = new();
    private readonly List<string> redoEdits = new();
    private string historyPlanId = string.Empty;
    private PlanDocument? historyPlan;
    private string frameBefore = string.Empty;
    private string? pendingBefore;
    private bool editOccurred;

    public void OpenReview() { workspace = 2; IsOpen = true; }

    private void DrawWorkspace(PlanDocument plan)
    {
        DrawStudioIdentity(plan);
        ImGui.Separator();
        ImGui.Spacing();
        if (workspace == 2) { DrawReviewWorkspace(); return; }
        if (workspace == 1) { DrawLiveWorkspace(plan); return; }

        DrawHeader(plan);
        ImGui.Spacing();
        var tools = new[] { "Slides", "Timeline", "Roster", "Learned", "Import", "Share", "Adaptive" };
        for (var i = 0; i < tools.Length; i++)
        {
            if (i > 0) UiHelpers.SameLineIfRoom(UiHelpers.ButtonWidth(tools[i]));
            var activeTool = planTool == i;
            if (activeTool) ImGui.PushStyleColor(ImGuiCol.Button, Palette.Vec(Palette.Accent, 0.2f));
            if (ImGui.Button(tools[i], Vector2.Zero)) planTool = i;
            if (activeTool) ImGui.PopStyleColor();
        }
        ImGui.Spacing();
        ImGui.BeginDisabled(undoEdits.Count == 0);
        if (ImGui.SmallButton("Undo edit")) RestoreEdit(plan, undoEdits, redoEdits);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(redoEdits.Count == 0);
        if (ImGui.SmallButton("Redo edit")) RestoreEdit(plan, redoEdits, undoEdits);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(dirty ? "Changes pending save" : "Saved locally");
        if (!string.IsNullOrEmpty(Plugin.Plans.LastSaveError))
            ImGui.TextWrapped("Save needs attention: " + Plugin.Plans.LastSaveError);
        ImGui.Separator();
        DrawMechanicContext(plan);
        if (ImGui.BeginChild("##workspace-content", Vector2.Zero, false, ImGuiWindowFlags.None))
        {
            switch (planTool)
            {
                case 0: DrawSlidesTab(plan); break;
                case 1: DrawTimelineTab(plan); break;
                case 2: DrawRosterTab(plan); break;
                case 3: DrawLearnedTab(plan); break;
                case 4: DrawImportTab(plan); break;
                case 5: DrawShareTab(plan); break;
                case 6: DrawAdaptiveTab(plan); break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawMechanicContext(PlanDocument plan)
    {
        var entry = plan.Timeline.FirstOrDefault(e => e.Id == selectedEntryId);
        ImGui.SetNextItemWidth(MathF.Max(150, ImGui.GetContentRegionAvail().X * 0.55f));
        if (ImGui.BeginCombo("Mechanic", entry?.Label ?? "Select a timeline step", ImGuiComboFlags.None))
        {
            foreach (var step in plan.Timeline.OrderBy(e => e.SortTime))
            {
                if (!ImGui.Selectable(step.Label + "##context-" + step.Id, step.Id == selectedEntryId, ImGuiSelectableFlags.None, Vector2.Zero)) continue;
                selectedEntryId = step.Id;
                var linked = plan.IndexOfSlide(step.SlideId);
                if (linked >= 0) SelectSlideManually(linked);
            }
            ImGui.EndCombo();
        }
        if (entry != null && planTool == 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit timing / checkpoint")) planTool = 1;
        }
        ImGui.Spacing();
    }

    private void DrawLiveWorkspace(PlanDocument plan)
    {
        ImGui.TextUnformatted(plan.Name);
        ImGui.TextDisabled(Plugin.Encounter.InCombat ? "PULL IN PROGRESS" : "READY FOR THE NEXT PULL");
        DrawFollowIndicator();
        ImGui.TextWrapped(Plugin.Adaptive.Status);
        var slide = CurrentSlide;
        var aligned = Plugin.Tracker.TryAlign(plan, slide, out var fit);
        ImGui.TextColored(Palette.Vec(aligned ? Palette.Good : Palette.Attention),
            aligned ? $"Arena aligned  |  residual {fit.Residual * 100:0.0}%" : "Positions unavailable: arena is not aligned");
        if (!aligned && !string.IsNullOrEmpty(Plugin.Tracker.Status)) ImGui.TextWrapped(Plugin.Tracker.Status);
        if (ImGui.BeginTabBar("##live-workspace", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Current mechanic", ImGuiTabItemFlags.None))
            {
                if (slide != null)
                {
                    ImGui.TextUnformatted($"{slideIndex + 1} / {plan.Slides.Count}   {slide.Title}");
                    if (!string.IsNullOrWhiteSpace(slide.Notes)) ImGui.TextWrapped(slide.Notes);
                    liveCanvas.LivePlayers = Plugin.Config.ShowLivePositions ? Plugin.Tracker.Read(plan, slide) : null;
                    liveCanvas.HighlightSlot = Plugin.Roster.ResolveLocalSlot(plan);
                    liveCanvas.LiveGuides = Plugin.Config.LivePositionGuides;
                    liveCanvas.Draw(plan, slide, ImGui.GetContentRegionAvail(), editable: false);
                }
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Calls and controls", ImGuiTabItemFlags.None)) { DrawLiveTab(plan); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawReviewCheckpointEditor(TimelineEntry entry)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Review checkpoint");
        ImGui.TextWrapped("Compare recorded seats with planned tokens at this moment. Distance is an observation, not a mechanic success verdict.");
        var enabled = entry.ReviewCheckpointEnabled;
        if (ImGui.Checkbox("Enable position checkpoint", ref enabled)) { entry.ReviewCheckpointEnabled = enabled; MarkDirty(); }
        if (!enabled) return;
        var offset = entry.ReviewOffsetSeconds;
        ImGui.SetNextItemWidth(180 * UiHelpers.Scale);
        if (ImGui.SliderFloat("Seconds after expected cast end", ref offset, -15, 30, "%.1f s", ImGuiSliderFlags.None)) { entry.ReviewOffsetSeconds = offset; MarkDirty(); }
        var radius = entry.ReviewRadiusYalms;
        ImGui.SetNextItemWidth(180 * UiHelpers.Scale);
        if (ImGui.SliderFloat("Target radius", ref radius, 0.5f, 20, "%.1f yalms", ImGuiSliderFlags.None)) { entry.ReviewRadiusYalms = radius; MarkDirty(); }
        if (string.IsNullOrEmpty(entry.SlideId)) ImGui.TextWrapped("Link a slide with player tokens above to make this checkpoint usable.");
    }

    // Coalesce an ImGui drag or text edit into one undo step. Keep memory bounded.
    private void BeginPlanEditFrame(PlanDocument plan)
    {
        if (historyPlanId != plan.Id || !ReferenceEquals(historyPlan, plan))
        {
            historyPlan = plan;
            historyPlanId = plan.Id; undoEdits.Clear(); redoEdits.Clear(); pendingBefore = null;
            frameBefore = JsonConvert.SerializeObject(plan);
        }
        editOccurred = false;
    }

    private void EndPlanEditFrame(PlanDocument plan)
    {
        if (Plan != plan) { pendingBefore = null; return; }
        if (editOccurred && pendingBefore == null && frameBefore.Length > 0) pendingBefore = frameBefore;
        if (pendingBefore == null || ImGui.IsAnyItemActive()) return;
        var after = JsonConvert.SerializeObject(plan);
        if (pendingBefore != after)
        {
            undoEdits.Add(pendingBefore);
            if (undoEdits.Count > 24) undoEdits.RemoveAt(0);
            redoEdits.Clear();
        }
        pendingBefore = null;
        frameBefore = after;
    }

    private void RestoreEdit(PlanDocument plan, List<string> source, List<string> destination)
    {
        if (source.Count == 0) return;
        var restored = JsonConvert.DeserializeObject<PlanDocument>(source[^1]);
        if (restored == null) return;
        destination.Add(JsonConvert.SerializeObject(plan));
        source.RemoveAt(source.Count - 1);
        plan.Name = restored.Name; plan.Encounter = restored.Encounter; plan.Author = restored.Author;
        plan.Notes = restored.Notes; plan.Arena = restored.Arena; plan.Roster = restored.Roster;
        plan.Slides = restored.Slides; plan.Timeline = restored.Timeline;
        plan.AdaptiveMechanics = restored.AdaptiveMechanics;
        plan.StrategyEvidence = restored.StrategyEvidence;
        plan.FormatVersion = restored.FormatVersion;
        canvas.Select(null); dirty = true; pendingBefore = null;
        frameBefore = JsonConvert.SerializeObject(plan);
    }

    private void DrawStudioIdentity(PlanDocument plan)
    {
        var scale = UiHelpers.Scale;
        var startY = ImGui.GetCursorPosY();
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var headerHeight = MathF.Max(40 * scale, ImGui.GetTextLineHeightWithSpacing() * 2);
        ImGui.BeginGroup();
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var centre = origin + new Vector2(19, 20) * scale;
        var ink = Palette.Pack(Palette.Accent);
        var radius = 13f * scale;
        draw.AddLine(centre - new Vector2(radius, 0), centre - new Vector2(0, radius), ink, 1.5f * scale);
        draw.AddLine(centre - new Vector2(0, radius), centre + new Vector2(radius, 0), ink, 1.5f * scale);
        draw.AddLine(centre + new Vector2(radius, 0), centre + new Vector2(0, radius), ink, 1.5f * scale);
        draw.AddLine(centre + new Vector2(0, radius), centre - new Vector2(radius, 0), ink, 1.5f * scale);
        draw.AddCircleFilled(centre, 3f * scale, ink, 12);
        ImGui.Dummy(new Vector2(45 * scale, headerHeight));
        ImGui.SameLine();
        ImGui.BeginGroup();
        using (Plugin.Fonts.PushTitle()) ImGui.TextUnformatted("S H I K A R I");
        ImGui.TextColored(Palette.Vec(Palette.TextMuted), "RAID STRATEGY STUDIO");
        ImGui.EndGroup();

        // Navigation shares the identity row. Its width follows the actual font metrics,
        // while the optional session readout yields first when the window gets narrow.
        ImGui.SameLine(0, 24 * scale);
        var buttonHeight = MathF.Max(32 * scale, ImGui.GetFrameHeight());
        var navigationY = startY + MathF.Max(0, (headerHeight - buttonHeight) * 0.5f);
        var navigationX = ImGui.GetCursorPosX();
        var position = navigationMotion.Position;
        var labels = new[] { "Plan", "Live", "Review" };
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 6 * scale);
            // SameLine restores the previous line's Y. Set every item to the shared baseline,
            // not just Plan, or the following buttons inherit the logo's taller line.
            ImGui.SetCursorPosY(navigationY);
            var selected = workspace == i;
            var styled = Plugin.Config.ThemeEnabled;
            if (styled)
            {
                var weight = Math.Clamp(1f - MathF.Abs(position - i), 0, 1);
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Lerp(Palette.Vec(Palette.PanelRaised),
                    Palette.Vec(Palette.Accent, 0.20f), weight));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Palette.Vec(Palette.Accent, 0.30f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Palette.Vec(Palette.Accent, 0.42f));
                ImGui.PushStyleColor(ImGuiCol.Text, Palette.Vec(selected ? Palette.Text : Palette.TextMuted));
            }
            var width = MathF.Max(76 * scale, ImGui.CalcTextSize(labels[i]).X + 28 * scale);
            if (ImGui.Button(labels[i] + "##studio-navigation", new Vector2(width, buttonHeight))) workspace = i;
            if (styled) ImGui.PopStyleColor(4);
            navigationRanges[i] = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().X)
                - new Vector2(ImGui.GetWindowPos().X + navigationX);
        }
        // Animate in local coordinates so moving/resizing the window never leaves a trail.
        position = navigationMotion.Update(workspace, ImGui.GetIO().DeltaTime);
        var lower = Math.Clamp((int)MathF.Floor(position), 0, 2);
        var upper = Math.Min(lower + 1, 2);
        var range = Vector2.Lerp(navigationRanges[lower], navigationRanges[upper], position - lower)
            + new Vector2(ImGui.GetWindowPos().X + navigationX);
        var underlineY = ImGui.GetItemRectMax().Y - 1;
        var underlineColor = Plugin.Config.ThemeEnabled ? Palette.Pack(Palette.Accent) : ImGui.GetColorU32(ImGuiCol.Text);
        draw.AddLine(new Vector2(range.X + 10 * scale, underlineY), new Vector2(range.Y - 10 * scale, underlineY),
            underlineColor, 2 * scale);
        var navigationRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        var statusWidth = MathF.Max(180 * scale, ImGui.CalcTextSize($"{plan.Slides.Count} slides  /  {plan.Timeline.Count} mechanics").X);
        if (rightEdge - navigationRight > statusWidth + 24 * scale)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(rightEdge - statusWidth);
            ImGui.SetCursorPosY(startY);
            ImGui.BeginGroup();
            ImGui.TextColored(Palette.Vec(Plugin.Encounter.InCombat ? Palette.Good : Palette.TextMuted),
                Plugin.Encounter.InCombat ? "LIVE SESSION" : "BETWEEN PULLS");
            ImGui.TextDisabled($"{plan.Slides.Count} slides  /  {plan.Timeline.Count} mechanics");
            ImGui.EndGroup();
        }
        ImGui.EndGroup();
    }
}
