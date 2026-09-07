using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Live;
using Shikari.Services.Replay;
using Shikari.UI.Theme;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    // Review owns its canvas and clock. Scrubbing never changes the active plan or director.
    private readonly ArenaCanvas reviewCanvas = new();
    private string reviewAttemptId = string.Empty;
    private string reviewCompareId = string.Empty;
    private int reviewMechanicIndex;
    private int reviewSeat = -1;
    private float reviewTime;
    private float reviewSpeed = 1f;
    private bool reviewPlaying;
    private bool reviewTrails = true;
    private bool reviewFocus;

    private void DrawReviewWorkspace()
    {
        var store = Plugin.Replays;
        var attempts = store.Attempts;
        var attempt = attempts.FirstOrDefault(a => a.Id == reviewAttemptId);
        if (attempt == null && attempts.Count > 0)
        {
            attempt = attempts.OrderByDescending(a => a.StartedUtc).First();
            SelectReviewAttempt(attempt);
        }

        using (Plugin.Fonts.PushTitle())
            ImGui.TextUnformatted("MECHANIC REPLAY");
        ImGui.SameLine();
        ImGui.TextColored(Palette.Vec(store.Recording ? Palette.Good : Palette.TextMuted),
            store.Recording ? "  RECORDING PULL" : "  SAVED PULLS");
        ImGui.TextDisabled("Study the movement. Return to the plan with one clear adjustment.");
        if (!string.IsNullOrEmpty(store.Status))
            ImGui.TextWrapped(store.Status);

        if (ImGui.CollapsingHeader("Recording & storage"))
        {
            var enabled = Plugin.Config.ReplayEnabled;
            if (ImGui.Checkbox("Record mechanic replays locally", ref enabled))
            {
                Plugin.Config.ReplayEnabled = enabled;
                Plugin.SaveConfig();
            }
            var retention = Plugin.Config.ReplayRetention;
            ImGui.SetNextItemWidth(160f * UiHelpers.Scale);
            if (ImGui.SliderInt("Retained attempts", ref retention, 1, 30))
            {
                Plugin.Config.ReplayRetention = retention;
                Plugin.SaveConfig();
            }
            ImGui.TextDisabled("Replays include party names, positions and a snapshot of the plan. Stored on this device.");
            if (ImGui.Button("Delete all replays..."))
                ImGui.OpenPopup("Clear replays");
            if (ImGui.BeginPopup("Clear replays"))
            {
                ImGui.TextWrapped("Permanently remove every saved attempt?");
                if (ImGui.Button("Delete all"))
                {
                    store.Clear();
                    reviewAttemptId = string.Empty;
                    reviewPlaying = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        ImGui.Separator();
        if (attempt == null || !store.Attempts.Any(a => a.Id == attempt.Id))
        {
            ImGui.Spacing();
            using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("Your next pull starts the story.");
            ImGui.TextWrapped("Enable recording, open a plan and enter combat. After the pull, choose an attempt here to replay its mechanics against the plan captured at pull start.");
            ImGui.TextWrapped("Player positions need an aligned arena. Missing alignment is retained as a gap, so review never invents movement.");
            return;
        }

        ImGui.SetNextItemWidth(MathF.Max(240f * UiHelpers.Scale, ImGui.GetContentRegionAvail().X - 135f * UiHelpers.Scale));
        if (ImGui.BeginCombo("##review-attempt", ReviewAttemptLabel(attempt)))
        {
            foreach (var item in attempts.OrderByDescending(a => a.StartedUtc))
                if (ImGui.Selectable(ReviewAttemptLabel(item) + "###attempt-" + item.Id, item.Id == attempt.Id))
                {
                    SelectReviewAttempt(item);
                    attempt = item;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete attempt")) ImGui.OpenPopup("Delete replay");
        if (ImGui.BeginPopup("Delete replay"))
        {
            ImGui.TextUnformatted("Permanently remove this attempt?");
            if (ImGui.Button("Delete"))
            {
                store.Delete(attempt.Id);
                reviewAttemptId = string.Empty;
                reviewPlaying = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (reviewPlaying)
        {
            reviewTime = MathF.Min(attempt.Duration, reviewTime + MathF.Min(ImGui.GetIO().DeltaTime, 0.1f) * reviewSpeed);
            if (reviewTime >= attempt.Duration) reviewPlaying = false;
        }
        DrawReviewTransport(attempt);
        DrawAdaptiveEvidence(attempt);
        DrawEvidencePanel(attempt);
        if (attempt.Mechanics.Count > 0 && SelectedReviewMechanic(attempt)?.Time != reviewTime)
            reviewMechanicIndex = Math.Max(0, attempt.Mechanics.FindLastIndex(m => m.Time <= reviewTime));

        var available = ImGui.GetContentRegionAvail();
        var sidebar = Math.Clamp(available.X * 0.29f, 220f * UiHelpers.Scale, 310f * UiHelpers.Scale);
        if (ImGui.BeginChild("##review-mechanics", new Vector2(sidebar, available.Y), true, ImGuiWindowFlags.None))
            DrawReviewMechanics(attempt);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##review-stage", new Vector2(0, available.Y), false, ImGuiWindowFlags.None))
            DrawReviewStage(attempt);
        ImGui.EndChild();
    }

    private static string ReviewAttemptLabel(ReplayAttempt attempt) =>
        $"{attempt.StartedUtc.ToLocalTime():MMM d, HH:mm:ss}  /  {attempt.Plan.Name}  /  {attempt.Duration:0}s  /  {attempt.EndReason}";

    private void SelectReviewAttempt(ReplayAttempt attempt)
    {
        reviewAttemptId = attempt.Id;
        reviewCompareId = string.Empty;
        reviewTime = 0;
        reviewMechanicIndex = 0;
        reviewSeat = attempt.LocalSlot;
        reviewPlaying = false;
    }

    private void SeekReviewMechanic(ReplayAttempt attempt, int index)
    {
        if (attempt.Mechanics.Count == 0) return;
        reviewMechanicIndex = Math.Clamp(index, 0, attempt.Mechanics.Count - 1);
        reviewTime = Math.Clamp(attempt.Mechanics[reviewMechanicIndex].Time, 0, attempt.Duration);
        reviewPlaying = false;
    }

    private void DrawReviewTransport(ReplayAttempt attempt)
    {
        if (ImGui.Button(reviewPlaying ? "Pause" : "Play", new Vector2(65, 0) * UiHelpers.Scale))
        {
            if (reviewTime >= attempt.Duration) reviewTime = 0;
            reviewPlaying = !reviewPlaying;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(attempt.Mechanics.Count == 0 || reviewMechanicIndex == 0);
        if (ImGui.Button("Previous")) SeekReviewMechanic(attempt, reviewMechanicIndex - 1);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(attempt.Mechanics.Count == 0 || reviewMechanicIndex >= attempt.Mechanics.Count - 1);
        if (ImGui.Button("Next")) SeekReviewMechanic(attempt, reviewMechanicIndex + 1);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(85f * UiHelpers.Scale);
        if (ImGui.BeginCombo("##review-speed", $"{reviewSpeed:0.##}x"))
        {
            foreach (var speed in new[] { 0.25f, 0.5f, 1f, 2f })
                if (ImGui.Selectable($"{speed:0.##}x", speed == reviewSpeed)) reviewSpeed = speed;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{reviewTime:0.0}s / {attempt.Duration:0.0}s");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##review-time", ref reviewTime, 0, MathF.Max(0.01f, attempt.Duration), "%.1f s"))
            reviewPlaying = false;
    }

    private ReplayMechanic? SelectedReviewMechanic(ReplayAttempt attempt) =>
        attempt.Mechanics.Count == 0 ? null : attempt.Mechanics[Math.Clamp(reviewMechanicIndex, 0, attempt.Mechanics.Count - 1)];

    private void DrawReviewMechanics(ReplayAttempt attempt)
    {
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("MECHANICS");
        ImGui.TextDisabled($"{attempt.Mechanics.Count} recorded anchors");
        ImGui.Separator();
        if (attempt.Mechanics.Count == 0)
            ImGui.TextWrapped("No mechanic anchors were captured. Scrub the pull to inspect the recorded movement.");
        for (var i = 0; i < attempt.Mechanics.Count; i++)
        {
            var mechanic = attempt.Mechanics[i];
            ImGui.PushID(i);
            if (ImGui.Selectable($"{mechanic.Time:0.0}s   {mechanic.Label}", i == reviewMechanicIndex))
                SeekReviewMechanic(attempt, i);
            ImGui.TextDisabled(mechanic.ActionId == 0 ? "  Authored timeline anchor" : $"  Observed cast / occurrence {mechanic.Occurrence}");
            ImGui.PopID();
        }
        ImGui.Spacing();
        ImGui.Separator();
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("OBSERVATION");
        var selected = SelectedReviewMechanic(attempt);
        if (selected == null) return;
        var entry = attempt.Plan.Timeline.FirstOrDefault(e => e.Id == selected.EntryId);
        if (entry == null || !entry.ReviewCheckpointEnabled)
            ImGui.TextWrapped("No authored checkpoint for this mechanic. Movement is available; distance assessment is not.");
        else
        {
            ImGui.TextWrapped($"Checkpoint: {selected.ExpectedResolve + entry.ReviewOffsetSeconds:0.0}s into pull");
            ImGui.TextDisabled($"Authored radius: {entry.ReviewRadiusYalms:0.0} yalms");
            var distance = ReplayPlayback.DistanceAt(attempt, selected, reviewSeat);
            using (Plugin.Fonts.PushTitle())
                ImGui.TextUnformatted(distance.HasValue ? $"{distance.Value:0.0} yalms" : "Unknown");
            ImGui.TextWrapped(distance.HasValue ? "Distance from this seat to its planned token at the authored checkpoint." : "Choose a recorded seat with a planned token and valid alignment at the checkpoint.");
            ImGui.TextDisabled("Position observation, not a success verdict.");
        }
        DrawReviewComparison(attempt, selected);
        ImGui.Spacing();
        var canEdit = Plan?.Id == attempt.Plan.Id && Plan.FindSlide(selected.SlideId) != null;
        ImGui.BeginDisabled(!canEdit);
        if (ImGui.Button("Edit this mechanic's slide"))
        {
            reviewPlaying = false;
            ShowSlide(selected.SlideId);
            workspace = 0;
            planTool = 0;
            selectedEntryId = selected.EntryId;
        }
        ImGui.EndDisabled();
        if (!canEdit) ImGui.TextWrapped("Open the original plan to edit. Its slide must still exist.");
    }

    private static ReplayMechanic? MatchingReviewMechanic(ReplayAttempt attempt, ReplayMechanic mechanic) =>
        attempt.Mechanics.FirstOrDefault(m => m.ActionId == mechanic.ActionId && m.Occurrence == mechanic.Occurrence &&
            (mechanic.ActionId != 0 || m.EntryId == mechanic.EntryId));

    private void DrawReviewComparison(ReplayAttempt attempt, ReplayMechanic mechanic)
    {
        ImGui.Spacing();
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("COMPARE ATTEMPTS");
        var candidates = Plugin.Replays.Attempts.Where(a => a.Id != attempt.Id && a.Plan.Id == attempt.Plan.Id && MatchingReviewMechanic(a, mechanic) != null).ToList();
        var comparison = candidates.FirstOrDefault(a => a.Id == reviewCompareId);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##review-compare", comparison == null ? "Choose another attempt" : comparison.StartedUtc.ToLocalTime().ToString("MMM d, HH:mm:ss")))
        {
            if (ImGui.Selectable("None", comparison == null)) reviewCompareId = string.Empty;
            foreach (var candidate in candidates)
                if (ImGui.Selectable(ReviewAttemptLabel(candidate) + "###compare-" + candidate.Id, candidate.Id == reviewCompareId)) reviewCompareId = candidate.Id;
            ImGui.EndCombo();
        }
        if (candidates.Count == 0) ImGui.TextWrapped("Another attempt of this plan and mechanic occurrence will appear here.");
        if (comparison == null) return;
        var other = MatchingReviewMechanic(comparison, mechanic)!;
        var comparisonTime = other.Time + reviewTime - mechanic.Time;
        if (comparisonTime < 0 || comparisonTime > comparison.Duration)
        {
            ImGui.TextWrapped("The comparison pull has no recording at this synchronized time.");
            return;
        }
        var actors = comparison.Evidence.Actors.Where(a => reviewSeat >= 0 && a.SlotIndex == reviewSeat).ToArray();
        if (actors.Length == 1)
        {
            var statuses = CachedEvidenceTimeline(comparison).StatusesAt(actors[0].Id, comparisonTime);
            ImGui.TextWrapped($"Comparison at {comparisonTime:0.1}s: " +
                (statuses.Count == 0 ? "No active status evidence." : string.Join(", ", statuses.Take(12).Select(EvidenceStatusName))));
        }
        else ImGui.TextWrapped("Assign the comparison player to the same plan seat to compare their statuses.");
        var distance = ReplayPlayback.DistanceAt(comparison, other, reviewSeat);
        ImGui.TextWrapped(distance.HasValue ? $"Comparison checkpoint: {distance.Value:0.0} yalms" : "Comparison checkpoint: unknown");
        ImGui.TextWrapped(CompatibleReplayBoards(attempt, comparison)
            ? "Purple squares show the same seat in the other attempt, synchronized to this cast's start. Each checkpoint uses its own saved plan."
            : "The saved diagrams differ. Overlay hidden; checkpoint distances use each attempt's own saved plan.");
    }

    private void DrawReviewStage(ReplayAttempt attempt)
    {
        var mechanic = SelectedReviewMechanic(attempt);
        var frame = ReplayPlayback.FrameAt(attempt, reviewTime);
        var slide = frame == null ? null : attempt.Plan.FindSlide(frame.SlideId);
        slide ??= mechanic == null ? attempt.Plan.Slides.FirstOrDefault() : attempt.Plan.FindSlide(mechanic.SlideId);
        ImGui.SetNextItemWidth(190f * UiHelpers.Scale);
        var seatLabel = reviewSeat >= 0 && reviewSeat < attempt.Plan.Roster.Count ? attempt.Plan.Roster[reviewSeat].DisplayName : "All seats";
        if (ImGui.BeginCombo("##review-seat", seatLabel))
        {
            if (ImGui.Selectable("All seats", reviewSeat < 0)) reviewSeat = -1;
            for (var i = 0; i < attempt.Plan.Roster.Count; i++)
                if (ImGui.Selectable(attempt.Plan.Roster[i].DisplayName + "##seat-" + i, reviewSeat == i)) reviewSeat = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Focus", ref reviewFocus);
        ImGui.SameLine();
        ImGui.Checkbox("Trails", ref reviewTrails);
        ImGui.TextDisabled("Filled tokens: plan   /   Rings: recorded positions   /   Trails: last 5 seconds");
        if (attempt.Evidence.Positions.Count > 0 && (attempt.Evidence.Source == "FF Logs" || frame == null))
        {
            DrawEvidenceMap(attempt, slide);
            return;
        }
        if (frame == null)
            ImGui.TextColored(Palette.Vec(Palette.Attention), "No aligned sample at this time. Player positions are unavailable.");
        if (slide == null)
        {
            ImGui.TextWrapped("This attempt has no saved slide to display.");
            return;
        }
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted(slide.Title);
        reviewCanvas.HighlightSlot = reviewSeat;
        reviewCanvas.FocusOnMe = reviewFocus;
        reviewCanvas.LivePlayers = frame?.Players.Select(p => new ArenaTracker.LivePlayer(p.Name, p.JobId, p.SlotIndex, p.Board, p.SlotIndex == reviewSeat)).ToArray();
        var size = ImGui.GetContentRegionAvail();
        reviewCanvas.Draw(attempt.Plan, slide, size, false);
        if (size.X < 120f * UiHelpers.Scale || size.Y < 120f * UiHelpers.Scale || frame == null) return;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), true);
        if (reviewTrails)
        {
            foreach (var player in frame.Players)
            {
                if (reviewFocus && reviewSeat >= 0 && player.SlotIndex != reviewSeat) continue;
                var trail = ReplayPlayback.Trail(attempt, reviewTime, player.SlotIndex);
                var color = player.SlotIndex == reviewSeat ? ArenaCanvas.TargetGold : Palette.Pack(Palette.Good, 0.45f);
                for (var i = 1; i < trail.Count; i++)
                    draw.AddLine(reviewCanvas.ToScreen(trail[i - 1]), reviewCanvas.ToScreen(trail[i]), color, 2f * UiHelpers.Scale);
            }
        }
        var comparison = Plugin.Replays.Attempts.FirstOrDefault(a => a.Id == reviewCompareId && a.Plan.Id == attempt.Plan.Id);
        var other = comparison == null || mechanic == null ? null : MatchingReviewMechanic(comparison, mechanic);
        if (comparison != null && other != null && mechanic != null && CompatibleReplayBoards(attempt, comparison))
        {
            var otherFrame = ReplayPlayback.FrameAt(comparison, other.Time + reviewTime - mechanic.Time);
            if (otherFrame != null && otherFrame.SlideId == slide.Id)
                foreach (var player in otherFrame.Players)
                {
                    if (reviewSeat >= 0 && player.SlotIndex != reviewSeat) continue;
                    var point = reviewCanvas.ToScreen(player.Board);
                    var extent = new Vector2(5f * UiHelpers.Scale);
                    draw.AddRect(point - extent, point + extent, Palette.Pack(0xB49AFF), 1f, ImDrawFlags.None, 2f * UiHelpers.Scale);
                }
        }
        draw.PopClipRect();
        if (comparison?.Evidence.Positions.Count > 0 && comparison.Evidence.Source == "FF Logs")
            DrawEvidenceComparisonOverlay(attempt, slide);
    }

    // Cache by immutable attempt identity: comparison must not serialize large plans every frame.
    private string comparedBoardsKey = string.Empty;
    private bool comparedBoardsCompatible;
    private bool CompatibleReplayBoards(ReplayAttempt left, ReplayAttempt right)
    {
        var key = left.Id + right.Id;
        if (key == comparedBoardsKey) return comparedBoardsCompatible;
        comparedBoardsKey = key;
        comparedBoardsCompatible =
            Newtonsoft.Json.JsonConvert.SerializeObject(left.Plan.Arena) == Newtonsoft.Json.JsonConvert.SerializeObject(right.Plan.Arena) &&
            Newtonsoft.Json.JsonConvert.SerializeObject(left.Plan.Slides) == Newtonsoft.Json.JsonConvert.SerializeObject(right.Plan.Slides);
        return comparedBoardsCompatible;
    }
}
