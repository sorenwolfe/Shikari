using System;
using System.Linq;
using System.Numerics;

namespace Shikari.Services.Replay;

public sealed record TowerEffectResult(bool Available, string Reason, float? DistanceYalms = null,
    float? RadiusYalms = null, bool? Inside = null, string Note = "");

/// <summary>Read-only proximity for two source-reviewed M12S P1 tower actions. This does not
/// establish a strategy assignment, safe route, or correspondence with an authored board.</summary>
public static class TowerEffectObservation
{
    // WTFDIG/BossMod source review and the observed fixture establish only these tower semantics.
    // See tests/fixtures/m12s-act2-resolutions.json for pinned source provenance.
    public static TowerEffectResult Evaluate(ReplayAttempt attempt, int effectIndex)
    {
        var evidence = attempt?.Evidence;
        if (evidence?.Source != "FF Logs" || evidence.EncounterId != 104)
            return Unknown("Tower proximity is available only for reviewed M12S P1 FF Logs observations.");
        if (!evidence.EffectsComplete)
            return Unknown("Typed effect evidence is incomplete or was not retained by this import.");
        if (evidence.Effects == null || evidence.Effects.Count > ReplayEvidence.MaxEffects ||
            effectIndex < 0 || effectIndex >= evidence.Effects.Count ||
            !float.IsFinite(attempt!.Duration) || attempt.Duration <= 0 || attempt.Duration > ReplayBuffer.MaxDuration)
            return Unknown("The selected effect or recording range is invalid.");
        var effect = evidence.Effects[effectIndex];
        if (effect == null || effect.Type != "calculateddamage" || effect.ActionId is not (46259 or 46263))
            return Unknown("This event has no reviewed tower-center geometry. Select a Roiling Mass calculated snapshot.");
        if (!float.IsFinite(effect.Time) || effect.Time < 0 || effect.Time > attempt.Duration ||
            effect.SourceId <= 0 || effect.TargetId <= 0 || effect.SourceId == effect.TargetId ||
            effect.SourceInstance is null or <= 0 || effect.TargetInstance is <= 0 || effect.PacketId is < 0)
            return Unknown("The tower observation has invalid timing or actor identity.");
        if (evidence.Actors == null || evidence.Actors.Count > 32 ||
            evidence.Actors.Count(a => a != null && a.Id == effect.TargetId) != 1)
            return Unknown("The effect target does not identify exactly one recorded player.");
        if (!ValidPosition(effect.SourcePosition) || !ValidPosition(effect.TargetPosition))
            return Unknown("This exact effect has no readable tower and player coordinates.");
        if (effect.PacketId.HasValue && evidence.Effects.Any(other => other != null &&
            other.PacketId == effect.PacketId && other.Type == effect.Type && other.ActionId == effect.ActionId &&
            other.TargetId == effect.TargetId && other.TargetInstance == effect.TargetInstance &&
            (other.SourceId != effect.SourceId || other.SourceInstance != effect.SourceInstance ||
             other.Time != effect.Time || other.SourcePosition != effect.SourcePosition || other.TargetPosition != effect.TargetPosition)))
            return Unknown("The same effect packet and target contain conflicting observations.");

        // Resource positions from the SAME event share FF Logs' documented centicoordinate
        // space. Euclidean distance needs no board calibration or inferred arena orientation.
        var source = effect.SourcePosition!.Value;
        var target = effect.TargetPosition!.Value;
        var dx = ((double)source.X - target.X) / 100;
        var dy = ((double)source.Y - target.Y) / 100;
        var distance = (float)Math.Sqrt(dx * dx + dy * dy);
        if (!float.IsFinite(distance)) return Unknown("The measured distance is invalid.");
        return new(true, "Tower and target positions were observed on the same calculated effect.", distance, 3, distance <= 3,
            "Same-event proximity only. No board alignment, assignment judgment, or movement route is inferred.");
    }

    private static bool ValidPosition(Vector2? position) => position is { } p && float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static TowerEffectResult Unknown(string reason) => new(false, reason);
}
