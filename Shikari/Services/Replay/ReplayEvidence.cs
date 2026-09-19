using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;

namespace Shikari.Services.Replay;

/// <summary>Source observations, kept separate from authored diagram positions.</summary>
public sealed class ReplayEvidence
{
    public const int MaxStatuses = 65536;
    public const int MaxPositions = 144008;
    public const int MaxEffects = 32768;
    public string Source { get; set; } = "Local recording";
    public string Url { get; set; } = "";
    public string ReportCode { get; set; } = "";
    public int FightId { get; set; }
    public uint EncounterId { get; set; }
    [DefaultValue(true)]
    public bool Complete { get; set; } = true;
    public List<string> Warnings { get; set; } = new();
    public List<EvidenceActor> Actors { get; set; } = new();
    public List<EvidenceStatus> Statuses { get; set; } = new();
    public List<EvidencePosition> Positions { get; set; } = new();
    public List<EvidenceEffect> Effects { get; set; } = new();
    /// <summary>Optional effect-channel completeness, independent of status evidence. False for older recordings.</summary>
    public bool EffectsComplete { get; set; }
    /// <summary>Validated selected-fight player scope; null for older/unscoped recordings.</summary>
    public List<int>? EffectTargetActorIds { get; set; }
    public List<EvidenceReference> References { get; set; } = new();
    public string CalibrationSlideId { get; set; } = "";
}

/// <summary>One explicit FF Logs damage observation with report-scoped actor identity and
/// nullable resource snapshots. It does not establish tower semantics or live world units.</summary>
public sealed class EvidenceEffect
{
    public float Time { get; set; }
    public uint ActionId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public long SourceId { get; set; }
    public long TargetId { get; set; }
    public int? SourceInstance { get; set; }
    public int? TargetInstance { get; set; }
    public long? PacketId { get; set; }
    public Vector2? SourcePosition { get; set; }
    public Vector2? TargetPosition { get; set; }
}

public sealed class EvidenceActor
{
    /// <summary>Local entity ID for statuses/positions, or an actor ID scoped to this FF Logs report.</summary>
    public long Id { get; set; }
    /// <summary>Explicit local game object ID observed with Id. Missing or conflicting identity stays unknown.</summary>
    public ulong? GameObjectId { get; set; }
    public string Name { get; set; } = "";
    public uint JobId { get; set; }
    public string Job { get; set; } = "";
    [DefaultValue(-1)]
    public int SlotIndex { get; set; } = -1;
    public bool IsLocal { get; set; }
}

public sealed class EvidenceStatus
{
    public float Time { get; set; }
    public long ActorId { get; set; }
    public long SourceId { get; set; }
    public uint StatusId { get; set; }
    public uint AbilityId { get; set; }
    public string Name { get; set; } = "";
    // apply / refresh / remove / unavailable. Unavailable clears a readable baseline.
    public string Change { get; set; } = "apply";
    public float? Duration { get; set; }
    public int? Parameter { get; set; }
    public int? Stacks { get; set; }
    public bool Baseline { get; set; }
}

public sealed class EvidencePosition
{
    public float Time { get; set; }
    public long ActorId { get; set; }
    public Vector2 Position { get; set; }
}

/// <summary>User-identified matching landmarks, not guesses from player destinations.</summary>
public sealed class EvidenceReference
{
    public Vector2 Source { get; set; }
    public Vector2 Board { get; set; }
}
