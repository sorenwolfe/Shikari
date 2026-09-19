using System.Collections.Generic;
using System.Numerics;

namespace Shikari.Services.FfLogs;

public enum LogStatusChange { Apply, Refresh, Remove, Stacks, Baseline }

/// <summary>Optional, sparse evidence. Complete means the requested event range was fully read,
/// not that every actor has positions, durations, parameters, or an initial status snapshot.</summary>
public sealed class LogEvidence
{
    public List<LogStatusEvent> StatusEvents { get; init; } = new();
    public List<LogPosition> Positions { get; init; } = new();
    public List<LogEffectEvent> Effects { get; init; } = new();
    /// <summary>The optional typed-effect channel was read without omissions. Legacy evidence is unknown.</summary>
    public bool EffectsComplete { get; set; }
    /// <summary>Validated selected-fight player IDs used before the effect cap; null is unscoped legacy evidence.</summary>
    public IReadOnlyList<int>? EffectTargetActorIds { get; init; }
    public List<string> Warnings { get; init; } = new();
    public bool Complete { get; set; } = true;
}

/// <summary>A typed damage observation; calculateddamage and damage are separate timestamps,
/// not inferred mechanic resolutions. Positions remain raw FF Logs centicoordinates.</summary>
public sealed class LogEffectEvent
{
    public float Time { get; init; }
    public uint ActionId { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public long SourceId { get; init; }
    public long TargetId { get; init; }
    public int? SourceInstance { get; init; }
    public int? TargetInstance { get; init; }
    public long? PacketId { get; init; }
    public Vector2? SourcePosition { get; init; }
    public Vector2? TargetPosition { get; init; }
}

public sealed class LogStatusEvent
{
    public float Time { get; init; }
    public int SourceId { get; init; }
    public int TargetId { get; init; }
    /// <summary>Candidate decoded from FF Logs' aura ID namespace. Consumers must validate against
    /// the game's Status sheet before using it in live rules. Zero means no candidate.</summary>
    public uint StatusId { get; init; }
    public bool StatusIdVerified { get; init; }
    public uint AbilityId { get; init; }
    public string Name { get; init; } = string.Empty;
    public LogStatusChange Change { get; init; }
    /// <summary>Seconds; null is unknown, including baseline auras.</summary>
    public float? Duration { get; init; }
    public int? Stacks { get; init; }
    /// <summary>Live Status.Param, when verified. FF Logs stacks/extraInfo do not establish it.</summary>
    public int? Parameter { get; init; }
    public int? ExtraInfo { get; init; }
}

/// <summary>One observation in FF Logs source centicoordinates. No alignment or interpolation.</summary>
public sealed class LogPosition
{
    public float Time { get; init; }
    public int ActorId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
}
