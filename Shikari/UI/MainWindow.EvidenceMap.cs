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
    private bool evidenceOverlay;
    private string evidenceBoundsId = "";
    private Vector2 evidenceBoundsCenter;
    private float evidenceBoundsSpan;

    private void DrawEvidenceMap(ReplayAttempt attempt, Slide? slide)
    {
        var timeline = TimelineFor(attempt);
        var evidence = attempt.Evidence;
        var fit = EvidenceProjection.TryAlign(evidence, out var alignment);
        var aligned = fit && slide != null && evidence.CalibrationSlideId == slide.Id;
        ImGui.BeginDisabled(!aligned || slide == null);
        ImGui.Checkbox("Overlay on strategy", ref evidenceOverlay);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(evidence.Source == "FF Logs" ? "Source coordinates / FF Logs samples" : "World coordinates / local samples");
        if (ImGui.TreeNode("Align source positions to the strategy"))
        {
            ImGui.TextWrapped("Enter three known landmarks in both coordinate systems. Use actual arena landmarks; a player's example destination is not a calibration point. Board coordinates run from 0 to 1. This alignment applies only to the displayed slide.");
            ImGui.InputFloat2("Source X / Y", ref referenceSource);
            ImGui.InputFloat2("Board X / Y", ref referenceBoard);
            ImGui.BeginDisabled(slide == null || (evidence.CalibrationSlideId == slide.Id && evidence.References.Count >= 8) || !float.IsFinite(referenceSource.X) || !float.IsFinite(referenceSource.Y) ||
                !float.IsFinite(referenceBoard.X) || !float.IsFinite(referenceBoard.Y));
            if (ImGui.Button("Add reference point"))
            {
                if (evidence.CalibrationSlideId != slide!.Id) evidence.References.Clear();
                evidence.CalibrationSlideId = slide.Id;
                evidence.References.Add(new EvidenceReference { Source = referenceSource, Board = referenceBoard });
                SaveEvidenceEdits(attempt);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Clear alignment")) { evidence.References.Clear(); evidenceOverlay = false; SaveEvidenceEdits(attempt); }
            ImGui.TextWrapped($"{evidence.References.Count} reference points / " + (aligned ? $"fit error {alignment.Residual:P1}" : "alignment not confirmed"));
            ImGui.TreePop();
        }
        var samples = evidence.Actors.Select(actor => (Actor: actor, Sample: timeline.PositionAt(actor.Id, reviewTime)))
            .Where(p => p.Sample != null).ToArray();
        ImGui.TextDisabled($"{samples.Length} players with a sample in the last 0.25s. Missing samples remain gaps.");
        if (evidenceOverlay && aligned && slide != null)
        {
            reviewCanvas.LivePlayers = samples.Select(p => new ArenaTracker.LivePlayer(p.Actor.Name, p.Actor.JobId,
                p.Actor.SlotIndex, alignment.ToPlan(p.Sample!.Position), p.Actor.Id == evidenceActor)).ToArray();
            reviewCanvas.HighlightSlot = reviewSeat;
            reviewCanvas.FocusOnMe = reviewFocus;
            reviewCanvas.Draw(attempt.Plan, slide, ImGui.GetContentRegionAvail(), false);
            DrawEvidenceComparisonOverlay(attempt, slide);
            return;
        }
        if (evidenceBoundsId != attempt.Id)
        {
            evidenceBoundsId = attempt.Id;
            // Keep the viewport fixed throughout playback. No arena outline is inferred from it.
            var xs = evidence.Positions.Select(p => p.Position.X).OrderBy(x => x).ToArray();
            var ys = evidence.Positions.Select(p => p.Position.Y).OrderBy(y => y).ToArray();
            if (xs.Length > 0)
            {
                var lo = xs.Length / 100; var hi = Math.Min(xs.Length - 1, xs.Length - 1 - lo);
                evidenceBoundsCenter = new((xs[lo] + xs[hi]) / 2, (ys[lo] + ys[hi]) / 2);
                evidenceBoundsSpan = MathF.Max(1, MathF.Max(xs[hi] - xs[lo], ys[hi] - ys[lo]) * 1.2f);
            }
            else { evidenceBoundsCenter = Vector2.Zero; evidenceBoundsSpan = 1; }
        }
        var available = ImGui.GetContentRegionAvail();
        var side = MathF.Min(available.X, available.Y);
        if (side < 40) return;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##source-position-map", new Vector2(side));
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + new Vector2(side), true);
        draw.AddRectFilled(origin, origin + new Vector2(side), 0xFF111114);
        for (var i = 0; i <= 8; i++)
        {
            var offset = side * i / 8;
            draw.AddLine(origin + new Vector2(offset, 0), origin + new Vector2(offset, side), 0xFF29292F);
            draw.AddLine(origin + new Vector2(0, offset), origin + new Vector2(side, offset), 0xFF29292F);
        }
        foreach (var pair in samples)
        {
            var point = origin + ((pair.Sample!.Position - evidenceBoundsCenter) / evidenceBoundsSpan + new Vector2(.5f)) * side;
            var selected = pair.Actor.Id == evidenceActor;
            if (reviewFocus && !selected) continue;
            var color = selected ? 0xFFFFFFFFu : Palette.Pack(Palette.Accent);
            draw.AddCircleFilled(point, (selected ? 7 : 5) * UiHelpers.Scale, color);
            draw.AddCircle(point, 9 * UiHelpers.Scale, 0xFF000000, 20, 2);
            var label = pair.Actor.SlotIndex >= 0 ? attempt.Plan.Roster[pair.Actor.SlotIndex].DisplayName : pair.Actor.Name;
            draw.AddText(point + new Vector2(11, -7) * UiHelpers.Scale, 0xFFFFFFFF, label);
        }
        draw.PopClipRect();
    }

    private void DrawEvidenceComparisonOverlay(ReplayAttempt attempt, Slide slide)
    {
        var comparison = Plugin.Replays.Attempts.FirstOrDefault(a => a.Id == reviewCompareId && a.Plan.Id == attempt.Plan.Id);
        var anchor = SelectedReviewMechanic(attempt);
        if (comparison == null || anchor == null || !CompatibleReplayBoards(attempt, comparison)) return;
        var other = MatchingReviewMechanic(comparison, anchor);
        if (other == null) return;
        var time = other.Time + reviewTime - anchor.Time;
        if (time < 0 || time > comparison.Duration) return;
        var frame = ReplayPlayback.FrameAt(comparison, time);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), true);
        void Marker(Vector2 board)
        {
            var point = reviewCanvas.ToScreen(board);
            var size = new Vector2(6 * UiHelpers.Scale);
            draw.AddRect(point - size, point + size, Palette.Pack(0xB49AFF), 1, ImDrawFlags.None, 2 * UiHelpers.Scale);
        }
        if (frame?.SlideId == slide.Id)
        {
            foreach (var player in frame.Players.Where(p => reviewSeat >= 0 && p.SlotIndex == reviewSeat)) Marker(player.Board);
        }
        else if (comparison.Evidence.CalibrationSlideId == slide.Id && EvidenceProjection.TryAlign(comparison.Evidence, out var alignment))
        {
            var timeline = CachedEvidenceTimeline(comparison);
            foreach (var actor in comparison.Evidence.Actors.Where(a => reviewSeat >= 0 && a.SlotIndex == reviewSeat))
            {
                var sample = timeline.PositionAt(actor.Id, time);
                if (sample != null) Marker(alignment.ToPlan(sample.Position));
            }
        }
        draw.PopClipRect();
    }
}
