using System;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Replay;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private void DrawCastEvidenceContext(ReplayAttempt attempt, ReplayMechanic? anchor)
    {
        var cast = EvidenceActorIdentity.FindCast(attempt, anchor);
        if (cast == null) return;
        var caster = EvidenceActorIdentity.Resolve(attempt, cast);
        var target = EvidenceActorIdentity.Resolve(attempt, cast, target: true);
        if (caster != null) ImGui.TextWrapped("Cast by " + caster.Name);
        ImGui.TextWrapped(target == null ? "Cast target unavailable in this recording." : "Cast target: " + target.Name);
        if (cast.ExpectedEndTime is { } expected) ImGui.TextDisabled($"Expected cast-bar end: {expected:0.0}s");
        if (cast.CompletionTime is { } completed) ImGui.TextDisabled($"Observed cast completion: {completed:0.0}s");
        if (target == null || !ImGui.SmallButton("Inspect cast target")) return;
        evidenceActor = target.Id;
        reviewSeat = target.SlotIndex;
        reviewTime = Math.Clamp(cast.ObservedTime ?? cast.StartTime ?? reviewTime, 0, attempt.Duration);
        reviewPlaying = false;
        evidenceSelection.Clear();
        evidenceDraft = null;
    }
}
