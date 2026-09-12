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
    private readonly PositionCheckSession positionCheck = new();
    private readonly ArenaCanvas positionCanvas = new() { LiveGuides = false };
    private PullValidationResult? positionAssignments;
    private AdaptiveDecision? positionDecision;
    private bool positionUseEffect = true;
    private int positionEffectIndex = -1;
    private string positionEffectSearch = "";
    private float positionTime;
    private float positionRadiusPercent = 3;
    private bool positionCheckpointReviewed;
    private bool positionAlignmentReviewed;
    private string positionCalibrationAttempt = "";
    private string positionCalibrationSlide = "";
    private Vector2 positionReferenceSource;
    private Vector2 positionReferenceBoard;

    private PositionCheckOptions PositionOptions() => new(positionDecision?.SlideId ?? "", positionTime,
        positionRadiusPercent / 100, positionCheckpointReviewed, positionAlignmentReviewed, positionUseEffect ? positionEffectIndex : -1);

    private void ResetPositionCheck()
    {
        positionCheck.Cancel(); positionAssignments = null; positionDecision = null;
        positionEffectIndex = -1; positionTime = 0;
        positionCheckpointReviewed = false; positionAlignmentReviewed = false;
    }

    private void SelectPositionDecision(PullValidationResult result, AdaptiveDecision decision)
    {
        ResetPositionCheck(); positionAssignments = result; positionDecision = decision;
        positionTime = decision.Time; positionCalibrationSlide = decision.SlideId;
    }

    private void DrawPositionCheck(PullValidationResult result, ReplayAttempt attempt)
    {
        if (!ReferenceEquals(positionAssignments, result)) { ResetPositionCheck(); positionAssignments = result; }
        if (!ImGui.TreeNode("Check destination at a mechanic checkpoint")) return;
        ImGui.TextWrapped("Compare a recorded position with the spot authored for this assignment. Choose the mechanic's actual checkpoint; a cast bar ending is not necessarily its damage snapshot.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("Assignment##position", positionDecision == null ? "Choose an assignment" :
            $"{positionDecision.Time:0.000}s · {positionDecision.Mechanic} · use {positionDecision.Occurrence}"))
        {
            for (var i = 0; i < result.Decisions.Count; i++)
            {
                var candidate = result.Decisions[i];
                if (ImGui.Selectable($"{candidate.Time:0.000}s · {candidate.Mechanic} · use {candidate.Occurrence}##position-{i}", candidate == positionDecision))
                    SelectPositionDecision(result, candidate);
            }
            ImGui.EndCombo();
        }
        if (positionDecision == null) { ImGui.TreePop(); return; }
        var slide = result.Plan.FindSlide(positionDecision.SlideId);
        ImGui.TextWrapped("Assigned board: " + (slide?.Title ?? "No destination"));
        if (ImGui.Checkbox("Use a recorded effect checkpoint", ref positionUseEffect))
        { positionCheck.Cancel(); positionEffectIndex = -1; positionCheckpointReviewed = false; }
        if (positionUseEffect)
        {
            if (!attempt.Evidence.EffectsComplete)
                ImGui.TextWrapped(attempt.Evidence.Effects.Count == 0
                    ? "This recording has no retained effect stream. Import the FF Logs fight again to add it."
                    : "The effect stream is incomplete. A precise effect comparison remains unknown.");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##position-effect-search", "Find an effect by name or action ID", ref positionEffectSearch, 128);
            var selected = positionEffectIndex >= 0 && positionEffectIndex < attempt.Evidence.Effects.Count
                ? attempt.Evidence.Effects[positionEffectIndex] : null;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("Effect##position", selected == null ? "Choose this player's calculated effect" : PositionEffectLabel(selected)))
            {
                var shown = 0;
                for (var i = 0; i < attempt.Evidence.Effects.Count; i++)
                {
                    var candidate = attempt.Evidence.Effects[i];
                    if (candidate.Type != "calculateddamage" || candidate.TargetId != result.ActorId || candidate.Time < positionDecision.Time ||
                        (!candidate.Name.Contains(positionEffectSearch, StringComparison.OrdinalIgnoreCase) &&
                         !candidate.ActionId.ToString().Contains(positionEffectSearch, StringComparison.Ordinal))) continue;
                    if (++shown > 200) { ImGui.TextDisabled("More events available — narrow the search."); break; }
                    if (ImGui.Selectable(PositionEffectLabel(candidate) + "##position-effect-" + i, i == positionEffectIndex))
                    {
                        positionCheck.Cancel(); positionEffectIndex = i; positionTime = candidate.Time; positionCheckpointReviewed = false;
                        reviewTime = candidate.Time; reviewPlaying = false;
                    }
                }
                if (shown == 0) ImGui.TextDisabled("No matching calculated effects for this player after the assignment.");
                ImGui.EndCombo();
            }
            if (selected != null)
            {
                ImGui.TextDisabled($"Action {selected.ActionId} · source {selected.SourceId}, instance {selected.SourceInstance?.ToString() ?? "unknown"}");
                if (selected.TargetPosition is { } point) ImGui.TextDisabled($"Recorded target coordinates: {point.X:0.##}, {point.Y:0.##}");
                else ImGui.TextWrapped("The selected effect has no target position.");
                var tower = TowerEffectObservation.Evaluate(attempt, positionEffectIndex);
                if (tower.Available)
                {
                    ImGui.TextWrapped($"Observed tower proximity: {tower.DistanceYalms:0.00} yalms from its recorded center / {tower.RadiusYalms:0.#}-yalm radius.");
                    ImGui.TextWrapped(tower.Note);
                }
            }
        }
        else
        {
            ImGui.SetNextItemWidth(180 * UiHelpers.Scale);
            if (ImGui.InputFloat("Checkpoint time (seconds)", ref positionTime))
            {
                positionCheck.Cancel(); positionCheckpointReviewed = false;
                positionTime = float.IsFinite(positionTime) ? Math.Clamp(positionTime, 0, attempt.Duration) : 0;
            }
            if (ImGui.SmallButton("Use replay cursor"))
            { positionCheck.Cancel(); positionTime = reviewTime; positionCheckpointReviewed = false; }
            ImGui.TextDisabled("Uses a recorded sample at or before this time, no older than 0.25 seconds.");
        }
        if (ImGui.Checkbox("I verified this mechanic checkpoint", ref positionCheckpointReviewed)) positionCheck.Cancel();
        if (ImGui.Checkbox("I reviewed this board’s alignment", ref positionAlignmentReviewed)) positionCheck.Cancel();
        ImGui.SetNextItemWidth(180 * UiHelpers.Scale);
        if (ImGui.SliderFloat("Near radius (% of board)", ref positionRadiusPercent, .5f, 10, "%.1f%%")) positionCheck.Cancel();
        ImGui.BeginDisabled(Plugin.Encounter.InCombat || positionCheck.Running || positionUseEffect && positionEffectIndex < 0);
        if (ImGui.Button("Check position"))
        {
            positionCheck.Start(result, positionDecision, attempt, PositionOptions(), validationEditRevision, Plugin.Replays.EvidenceRevision);
            reviewTime = positionTime; reviewPlaying = false;
        }
        ImGui.EndDisabled();
        if (positionCheck.Running)
        {
            ImGui.SameLine(); if (ImGui.SmallButton("Cancel position check")) positionCheck.Cancel();
            ImGui.TextDisabled("Comparing recorded position…");
        }
        if (positionCheck.Error != null) ImGui.TextWrapped(positionCheck.Error);
        if (positionCheck.Result is { } check)
        {
            ImGui.TextColored(Palette.Vec(check.Outcome == PositionCheckOutcome.Near ? Palette.Good : Palette.Attention),
                $"{check.Outcome} · checkpoint {check.CheckTime:0.000}s");
            ImGui.TextWrapped(check.Reason);
            if (check.SampleTime is { } sample) ImGui.TextDisabled($"{check.SampleSource} · {sample:0.000}s · age {check.SampleAge:0.000}s");
            if (check.NextSampleTime is { } next) ImGui.TextDisabled($"Next sample: {next:0.000}s; gaps are not interpolated.");
            if (check.Distance is { } distance) ImGui.TextWrapped($"Distance: {distance * 100:0.00}% of board / near radius {positionRadiusPercent:0.0}%.");
            if (check.AlignmentError is { } residual) ImGui.TextDisabled($"Largest landmark residual: {residual * 100:0.00}% of board.");
            foreach (var note in check.Notes) ImGui.TextWrapped(note);
            if (slide != null && check.ObservedPosition is { } observed && check.ExpectedPosition.HasValue)
            {
                var actor = attempt.Evidence.Actors.FirstOrDefault(a => a.Id == result.ActorId);
                positionCanvas.HighlightSlot = actor?.SlotIndex ?? -1; positionCanvas.FocusOnMe = true;
                positionCanvas.LivePlayers = new[] { new ArenaTracker.LivePlayer("Recorded position", actor?.JobId ?? 0,
                    actor?.SlotIndex ?? -1, observed, false) };
                positionCanvas.Draw(result.Plan, slide, new Vector2(ImGui.GetContentRegionAvail().X, 240 * UiHelpers.Scale), false);
                ImGui.TextDisabled("Hollow circle = recorded position; filled role token = authored destination.");
            }
        }
        ImGui.TextWrapped("For FF Logs, use Position alignment below to enter known arena landmarks on this board, then validate again. Position checks are kept for this Review session.");
        ImGui.TreePop();
    }

    private static string PositionEffectLabel(EvidenceEffect effect) =>
        $"{effect.Time:0.000}s · {(string.IsNullOrWhiteSpace(effect.Name) ? "Action " + effect.ActionId : effect.Name)}";

    // Available even after evidence edits invalidate the assignment result, so entering three
    // landmarks never requires three validation runs. Points belong to one recorded board.
    private void DrawPositionCalibration(ReplayAttempt attempt)
    {
        if (positionCalibrationAttempt != attempt.Id)
        { positionCalibrationAttempt = attempt.Id; positionCalibrationSlide = attempt.Evidence.CalibrationSlideId; }
        if (attempt.Evidence.Source != "FF Logs" || !ImGui.TreeNode("Position alignment")) return;
        ImGui.TextWrapped("Choose the assigned board and enter at least three well-spread arena landmarks in both coordinate systems. Use arena features, never player destinations. Board coordinates run from 0 to 1. Validate again after changing alignment.");
        var board = attempt.Plan.FindSlide(positionCalibrationSlide);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("Alignment board", board?.Title ?? "Choose a board"))
        {
            foreach (var candidate in attempt.Plan.Slides)
                if (ImGui.Selectable(candidate.Title + "##position-align-" + candidate.Id, candidate == board))
                { positionCalibrationSlide = candidate.Id; board = candidate; }
            ImGui.EndCombo();
        }
        ImGui.InputFloat2("Landmark source X / Y", ref positionReferenceSource);
        ImGui.InputFloat2("Landmark board X / Y", ref positionReferenceBoard);
        var evidence = attempt.Evidence;
        ImGui.BeginDisabled(Plugin.Encounter.InCombat || board == null || evidence.CalibrationSlideId == board.Id && evidence.References.Count >= 8 ||
            !float.IsFinite(positionReferenceSource.X) || !float.IsFinite(positionReferenceSource.Y) ||
            !float.IsFinite(positionReferenceBoard.X) || !float.IsFinite(positionReferenceBoard.Y) ||
            positionReferenceBoard.X < 0 || positionReferenceBoard.X > 1 || positionReferenceBoard.Y < 0 || positionReferenceBoard.Y > 1);
        if (ImGui.Button("Add alignment landmark"))
        {
            if (evidence.CalibrationSlideId != board!.Id) evidence.References.Clear();
            evidence.CalibrationSlideId = board.Id;
            evidence.References.Add(new EvidenceReference { Source = positionReferenceSource, Board = positionReferenceBoard });
            SaveEvidenceEdits(attempt, enrichStrategy: false);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(Plugin.Encounter.InCombat || evidence.References.Count == 0);
        if (ImGui.SmallButton("Clear position alignment")) { evidence.References.Clear(); SaveEvidenceEdits(attempt, enrichStrategy: false); }
        ImGui.EndDisabled();
        ImGui.TextDisabled($"{(board?.Id == evidence.CalibrationSlideId ? evidence.References.Count : 0)} landmarks on this board.");
        if (board?.Id == evidence.CalibrationSlideId)
        {
            for (var i = 0; i < evidence.References.Count; i++)
            {
                var point = evidence.References[i];
                ImGui.PushID("position-landmark-" + i);
                ImGui.TextWrapped($"{i + 1}. Source ({point.Source.X:0.##}, {point.Source.Y:0.##}) → board ({point.Board.X:0.###}, {point.Board.Y:0.###})");
                ImGui.SameLine();
                ImGui.BeginDisabled(Plugin.Encounter.InCombat);
                var remove = ImGui.SmallButton("Remove landmark");
                ImGui.EndDisabled(); ImGui.PopID();
                if (!remove) continue;
                evidence.References.RemoveAt(i); SaveEvidenceEdits(attempt, enrichStrategy: false); break;
            }
        }
        ImGui.TreePop();
    }
}
