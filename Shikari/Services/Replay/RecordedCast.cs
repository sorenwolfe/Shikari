using System;
using System.Numerics;

namespace Shikari.Services.Replay;

/// <summary>Cast observations independent of authored mechanics and board alignment.</summary>
public sealed class RecordedCast
{
    public string Source { get; set; } = string.Empty;
    public uint ActionId { get; set; }
    public int? Occurrence { get; set; }
    /// <summary>Seconds into the pull when the source supplied this observation.</summary>
    public float? ObservedTime { get; set; }
    /// <summary>Live: reconstructed from the visible bar. FF Logs: an explicit begincast.</summary>
    public float? StartTime { get; set; }
    /// <summary>Live display-bar prediction only; never evidence of an effect resolving.</summary>
    public float? ExpectedEndTime { get; set; }
    /// <summary>Explicit FF Logs cast event; does not establish damage or effect timing.</summary>
    public float? CompletionTime { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    /// <summary>Live game object ID, or FF Logs report actor ID according to Source.</summary>
    public ulong? CasterId { get; set; }
    public ulong? TargetId { get; set; }
    /// <summary>Live world XYZ in yalms at ObservedTime. Log centicoordinates are not world coordinates.</summary>
    public Vector3? CasterWorldPosition { get; set; }
    public Vector3? TargetWorldPosition { get; set; }
    /// <summary>Live actor rotation in radians at ObservedTime, not a measured action direction.</summary>
    public float? CasterHeading { get; set; }
    public float? TargetHeading { get; set; }

    public static RecordedCast FromLive(uint actionId, int occurrence, float startTime, float totalCastTime,
        CastStartContext? context = null) => new()
    {
        Source = "Live", ActionId = actionId, Occurrence = occurrence, StartTime = startTime,
        ExpectedEndTime = ExpectedBarEnd(startTime, totalCastTime, context),
        ObservedTime = context?.ObservedTime,
        ObservedAtUtc = context?.ObservedAtUtc is { } observed && observed != default ? observed : null,
        StartedAtUtc = context?.StartedAtUtc is { } started && started != default ? started : null,
        CasterId = LiveId(context?.CasterId), TargetId = LiveId(context?.TargetId),
        CasterWorldPosition = FinitePosition(context?.CasterWorldPosition), TargetWorldPosition = FinitePosition(context?.TargetWorldPosition),
        CasterHeading = FiniteHeading(context?.CasterHeading), TargetHeading = FiniteHeading(context?.TargetHeading),
    };

    private static float? ExpectedBarEnd(float startTime, float totalCastTime, CastStartContext? context)
    {
        if (!float.IsFinite(totalCastTime) || totalCastTime < 0) return null;
        if (context != null && float.IsFinite(context.ObservedTime) &&
            context.ObservedAtUtc != default && context.StartedAtUtc != default &&
            context.StartedAtUtc <= context.ObservedAtUtc)
        {
            // The relative start is clamped to zero if the bar predates combat. Its observed
            // remaining duration still predicts the original end, without shifting it later.
            var elapsed = Math.Clamp((context.ObservedAtUtc - context.StartedAtUtc).TotalSeconds, 0, totalCastTime);
            return context.ObservedTime + (float)(totalCastTime - elapsed);
        }
        return startTime + totalCastTime;
    }

    internal RecordedCast Snapshot() => (RecordedCast)MemberwiseClone();

    public bool IsValid(float duration) => Source is "Live" or "FF Logs" && ActionId != 0 &&
        (Occurrence == null || Occurrence is > 0 and <= ReplayBuffer.MaxCasts) &&
        (StartTime != null || CompletionTime != null) &&
        ValidTime(ObservedTime, duration) && ValidTime(StartTime, duration) && ValidTime(CompletionTime, duration) &&
        ValidTime(ExpectedEndTime, ReplayBuffer.MaxDuration * 2) &&
        !(ObservedTime < StartTime) && !(CompletionTime < StartTime) && !(ExpectedEndTime < StartTime) &&
        CasterId != 0 && TargetId != 0 &&
        (CasterWorldPosition == null || FinitePosition(CasterWorldPosition) != null) &&
        (TargetWorldPosition == null || FinitePosition(TargetWorldPosition) != null) &&
        (CasterHeading == null || float.IsFinite(CasterHeading.Value)) &&
        (TargetHeading == null || float.IsFinite(TargetHeading.Value));

    private static bool ValidTime(float? value, float maximum) => value == null ||
        float.IsFinite(value.Value) && value >= 0 && value <= maximum;
    private static ulong? LiveId(ulong? value) => value is null or 0 or 0xE0000000 or ulong.MaxValue ? null : value;
    private static float? FiniteHeading(float? value) => value is { } v && float.IsFinite(v) ? v : null;
    private static Vector3? FinitePosition(Vector3? value) => value is { } v &&
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : null;
}

/// <summary>Optional actor snapshot taken when polling first detects a live cast.</summary>
public sealed class CastStartContext
{
    public float ObservedTime { get; init; }
    public DateTime ObservedAtUtc { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public ulong? CasterId { get; init; }
    public ulong? TargetId { get; init; }
    public Vector3? CasterWorldPosition { get; init; }
    public Vector3? TargetWorldPosition { get; init; }
    public float? CasterHeading { get; init; }
    public float? TargetHeading { get; init; }
}
