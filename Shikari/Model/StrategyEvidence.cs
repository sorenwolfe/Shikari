using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;

namespace Shikari.Model;

/// <summary>Compact shareable observations. Actor identity is a seat or anonymous ordinal, never a character name.</summary>
public sealed class StrategyEvidenceAttachment
{
    public string Key { get; set; } = "";
    public string Source { get; set; } = "Local recording";
    public string ReportCode { get; set; } = "";
    public int FightId { get; set; }
    public uint EncounterId { get; set; }
    public uint TerritoryId { get; set; }
    public bool EncounterVerified { get; set; }
    public bool Complete { get; set; }
    public int OmittedMechanics { get; set; }
    public List<StrategyMechanicEvidence> Mechanics { get; set; } = new();
}

public sealed class StrategyMechanicEvidence
{
    public string EntryId { get; set; } = "";
    public string SlideId { get; set; } = "";
    // action / name / ambiguous / unmatched
    public string Match { get; set; } = "unmatched";
    public uint ActionId { get; set; }
    public int Occurrence { get; set; }
    public float CastTime { get; set; }
    public float ResolveTime { get; set; }
    /// <summary>Expected cast end, which can fall beyond the recorded pull.</summary>
    public float? ExpectedResolveTime { get; set; }
    public bool ResolveObserved { get; set; }
    public List<StrategyActorEvidence> Actors { get; set; } = new();
}

public sealed class StrategyActorEvidence
{
    public int Actor { get; set; }
    [DefaultValue(-1)] public int SlotIndex { get; set; } = -1;
    public uint JobId { get; set; }
    public List<StrategyStatusEvidence> Statuses { get; set; } = new();
    public List<StrategyPositionEvidence> Positions { get; set; } = new();
    /// <summary>Board units at resolve, only for a calibrated fresh observation and unique authored destination.</summary>
    public float? DestinationDistance { get; set; }
}

public sealed class StrategyStatusEvidence
{
    public uint StatusId { get; set; }
    public uint AbilityId { get; set; }
    public float Time { get; set; }
    public bool Baseline { get; set; }
    public int? Parameter { get; set; }
    public float? Duration { get; set; }
}

public sealed class StrategyPositionEvidence
{
    public float Time { get; set; }
    public Vector2 Position { get; set; }
    public bool Calibrated { get; set; }
}

/// <summary>Model-only bounds for disk/share import. Malformed evidence is discarded without touching authored content.</summary>
public static class StrategyEvidenceValidation
{
    public const int MaxAttachments = 4;
    public const int MaxMechanics = 128;
    public const int MaxActors = 8;
    public const int MaxStatuses = 4;
    public const int MaxPositions = 3;
    public static bool IsValid(PlanDocument plan) => plan.StrategyEvidence == null ||
        plan.StrategyEvidence.Count <= MaxAttachments && plan.StrategyEvidence.All(a => Valid(a, plan));

    public static void Normalise(PlanDocument plan)
    {
        plan.StrategyEvidence ??= new();
        plan.StrategyEvidence = plan.StrategyEvidence.Where(a => Valid(a, plan))
            .GroupBy(a => a.Key).Select(g => g.Last()).TakeLast(MaxAttachments).ToList();
    }

    static bool Text(string? s, int max) => s != null && s.Length <= max && !s.Any(char.IsControl);
    static bool Time(float time) => float.IsFinite(time) && time >= 0 && time <= 1800;
    static bool Valid(StrategyEvidenceAttachment? a, PlanDocument plan) => a != null && Text(a.Key, 100) &&
        a.Key.Length > 0 && a.Source is "Local recording" or "FF Logs" && Text(a.ReportCode, 32) &&
        a.ReportCode.All(char.IsAsciiLetterOrDigit) && a.FightId >= 0 && a.OmittedMechanics >= 0 &&
        a.Mechanics is { Count: <= MaxMechanics } && a.Mechanics.All(m => m != null &&
            Text(m.EntryId, 128) && Text(m.SlideId, 128) && m.Match is "action" or "name" or "ambiguous" or "unmatched" &&
            m.Occurrence >= 0 && Time(m.CastTime) && Time(m.ResolveTime) && m.ResolveTime >= m.CastTime &&
            (m.ExpectedResolveTime == null || float.IsFinite(m.ExpectedResolveTime.Value) &&
                m.ExpectedResolveTime >= m.CastTime && m.ExpectedResolveTime <= 1920) &&
            m.Actors is { Count: <= MaxActors } && m.Actors.All(p => p != null && p.Actor is >= 0 and < 32 &&
                p.SlotIndex >= -1 && p.SlotIndex < (plan.Roster?.Count ?? 0) &&
                (p.DestinationDistance == null || float.IsFinite(p.DestinationDistance.Value) && p.DestinationDistance >= 0) &&
                p.Statuses is { Count: <= MaxStatuses } && p.Statuses.All(s => s != null && Time(s.Time) &&
                    (s.Parameter == null || s.Parameter is >= 0 and <= 65535) &&
                    (s.Duration == null || float.IsFinite(s.Duration.Value) && s.Duration is >= 0 and <= 86400)) &&
                p.Positions is { Count: <= MaxPositions } && p.Positions.All(s => s != null && Time(s.Time) &&
                    float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y))));
}
