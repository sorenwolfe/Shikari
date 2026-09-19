namespace Shikari.Services.Replay;

/// <summary>Connects a recorded cast to this recording's status/position actor, without roster guesses.</summary>
public static class EvidenceActorIdentity
{
    /// <summary>Finds one stored cast with the mechanic's exact action, occurrence and observed start.</summary>
    public static RecordedCast? FindCast(ReplayAttempt? attempt, ReplayMechanic? mechanic)
    {
        if (attempt?.Mechanics == null || mechanic == null || attempt.Mechanics.Count > ReplayBuffer.MaxMechanics ||
            !attempt.Mechanics.Contains(mechanic) || attempt.Casts == null || attempt.Casts.Count > ReplayBuffer.MaxCasts ||
            mechanic.ActionId == 0 || mechanic.Occurrence <= 0 || !float.IsFinite(mechanic.Time) || mechanic.Time < 0)
            return null;
        RecordedCast? found = null;
        foreach (var cast in attempt.Casts)
        {
            if (cast == null) return null;
            if (cast.ActionId != mechanic.ActionId || cast.Occurrence != mechanic.Occurrence || cast.StartTime != mechanic.Time)
                continue;
            if (found != null) return null;
            found = cast;
        }
        return found;
    }

    /// <summary>
    /// Returns the unique caster (or target) actor, or null for absent, ambiguous or incompatible identity.
    /// The cast must be an item in attempt.Casts; IDs from another pull or report are never searched.
    /// The returned Id is the key used by EvidenceTimeline, not a game object ID.
    /// </summary>
    public static EvidenceActor? Resolve(ReplayAttempt? attempt, RecordedCast? cast, bool target = false, bool completion = false)
    {
        if (attempt?.Casts == null || cast == null || attempt.Casts.Count > ReplayBuffer.MaxCasts ||
            !attempt.Casts.Contains(cast) || attempt.Evidence?.Actors == null || attempt.Evidence.Actors.Count > 32)
            return null;
        var evidence = attempt.Evidence;
        var live = cast.Source == "Live" && evidence.Source == "Local recording";
        var logs = cast.Source == "FF Logs" && evidence.Source == "FF Logs";
        if (completion && (!logs || !target || cast.CompletionTime == null)) return null;
        var id = completion ? cast.CompletionTargetId : target ? cast.TargetId : cast.CasterId;
        if ((!live && !logs) || id == null || (live ? !IsLiveGameObjectId(id) : id is 0 or > long.MaxValue))
            return null;

        EvidenceActor? found = null;
        foreach (var actor in evidence.Actors)
        {
            if (actor == null) return null;
            if (live ? actor.GameObjectId != id : actor.Id <= 0 || (ulong)actor.Id != id) continue;
            if (found != null || (live ? !IsLiveEntityId(actor.Id) : actor.GameObjectId != null)) return null;
            found = actor;
        }
        if (found == null) return null;
        // Even a unique game object match is unsafe if its status key names multiple actors.
        var matchingKeys = 0;
        foreach (var actor in evidence.Actors)
            if (actor.Id == found.Id && ++matchingKeys > 1) return null;
        return found;
    }

    internal static bool IsLiveGameObjectId(ulong? id) => id is not (null or 0 or 0xE0000000 or uint.MaxValue or ulong.MaxValue);
    internal static bool IsLiveEntityId(long id) => id is > 0 and < uint.MaxValue and not 0xE0000000;
}
