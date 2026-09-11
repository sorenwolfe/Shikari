using System;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Services.Replay;

/// <summary>A passive interpretation of recorded statuses, never a navigation or success decision.</summary>
public sealed record EncounterAssignment(bool IsResolved, int Number, string Letter, string Label, string Reason);

public static class EncounterAssignmentDecoder
{
    public const uint ActTwoActionId = 48830;

    // Caro strategy: mczub/wtfdig 2262bfea410539c9d2465a03b67094bc4c1f7761,
    // src/routes/74/m12s/data.ts, caroStrat / Grotesquerie: Act 2.
    // ID/number semantics corroborated by awgil/ffxiv_bossmod
    // ecf7a1d2d4a694bf177fc5a4b7966e0f6524f2ad, RM12S1TheLindwurmEnums.cs
    // and GrotesquerieAct2.cs. This mapping has not been validated against real Shikari pulls.

    /// <summary>Supply StatusesAt(actorId, time) from this recording's EvidenceTimeline.
    /// Null means this selected cast/time does not establish Act 2 context.
    /// A non-null unresolved result retains an explanation for insufficient evidence.</summary>
    public static EncounterAssignment? Decode(ReplayAttempt attempt, ReplayMechanic? anchor, long actorId,
        float time, IReadOnlyList<EvidenceStatus> activeStatuses)
    {
        if (anchor == null || anchor.ActionId != ActTwoActionId || anchor.Occurrence <= 0 ||
            !attempt.Mechanics.Contains(anchor) || !float.IsFinite(anchor.Time) || anchor.Time < 0 ||
            !float.IsFinite(time) || !float.IsFinite(attempt.Duration) || time < anchor.Time || time > attempt.Duration)
            return null;
        // A generic In Line status must not acquire meaning from an older selected phase.
        if (attempt.Mechanics.Any(m => m.Time > anchor.Time && m.Time <= time &&
            m.ActionId is 48829 or 48830 or 48831 or 48832)) return null;
        if (!attempt.Evidence.Complete) return Unknown("This recording is incomplete.");
        if (attempt.Evidence.Actors.Count(a => a.Id == actorId) != 1)
            return Unknown("The selected actor is not uniquely identified in this recording.");

        var relevant = activeStatuses.Where(s => s.ActorId == actorId && Relevant(s)).ToArray();
        if (relevant.Any(s => s.StatusId == 0))
            return Unknown("An assignment status could not be verified against the game data.");
        if (relevant.Any(s => s.Baseline || s.Time < anchor.Time))
            return Unknown("Existing status baselines do not establish a fresh Act 2 assignment.");
        if (relevant.Any(s => !float.IsFinite(s.Time) || s.Time > time ||
            s.Change is not ("apply" or "refresh") || s.Duration is { } d &&
            (!float.IsFinite(d) || d < 0 || s.Time + d <= time)))
            return Unknown("The assignment statuses are not active observations at this review time.");

        var numbers = relevant.Select(s => Number(s.StatusId)).Where(n => n > 0).Distinct().ToArray();
        var letters = relevant.Where(s => s.StatusId is 4752 or 4754)
            .Select(s => s.StatusId == 4752 ? "A" : "B").Distinct().ToArray();
        if (numbers.Length > 1 || letters.Length > 1)
            return Unknown("Conflicting number or Bonds statuses were observed.");
        if (numbers.Length != 1 || letters.Length != 1)
            return Unknown("A fresh number status and Bonds A or B must both be active.");

        var roman = numbers[0] switch { 1 => "I", 2 => "II", 3 => "III", _ => "IV" };
        return new(true, numbers[0], letters[0], $"Grotesquerie: Act 2 — {roman} / Bonds {letters[0]}",
            "Number and Bonds statuses were observed after this cast; this labels the assignment only.");
    }

    private static EncounterAssignment Unknown(string reason) =>
        new(false, 0, "", "Grotesquerie: Act 2 — assignment unresolved", reason);

    private static int Number(uint statusId) => statusId switch
    {
        3004 => 1, // First in Line
        3005 => 2, // Second in Line
        3006 => 3, // Third in Line
        3451 => 4, // Fourth in Line
        _ => 0,
    };

    private static bool KnownStatus(uint statusId) => Number(statusId) != 0 || statusId is 4752 or 4754;
    private static bool Relevant(EvidenceStatus status) => KnownStatus(status.StatusId) ||
        // Raw aura IDs can disclose missing verification, but never positively identify a branch.
        status.StatusId == 0 && status.AbilityId > 1000000 && KnownStatus(status.AbilityId - 1000000);
}
