using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using Shikari.Model;

namespace Shikari.Services.Replay;

/// <summary>A self-contained local recording; never rendered against a subsequently edited plan.</summary>
public sealed class ReplayAttempt
{
    private int version = 1;
    /// <summary>Version 2 protects per-board geometry and evidence from older replay readers.</summary>
    public int Version
    {
        get => Plan?.FormatVersion >= 4 ? Math.Max(2, version) : version;
        set => version = value;
    }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedUtc { get; set; }
    public PlanDocument Plan { get; set; } = new();
    [DefaultValue(-1)]
    public int LocalSlot { get; set; } = -1;
    public uint TerritoryId { get; set; }
    public List<ReplayFrame> Frames { get; set; } = new();
    public List<ReplayMechanic> Mechanics { get; set; } = new();
    public List<StatusObservation> StatusObservations { get; set; } = new();
    public List<AdaptiveDecision> AdaptiveDecisions { get; set; } = new();
    public float Duration { get; set; }
    public string EndReason { get; set; } = "Ended";
    public ReplayEvidence Evidence { get; set; } = new();
}

public sealed class ReplayFrame
{
    public float Time { get; set; }
    public string SlideId { get; set; } = string.Empty;
    public bool Valid { get; set; }
    public float BoardPerYalm { get; set; }
    public List<ReplayPlayer> Players { get; set; } = new();
}

public sealed class ReplayPlayer
{
    public string Name { get; set; } = string.Empty;
    public uint JobId { get; set; }
    public int SlotIndex { get; set; }
    public Vector2 Board { get; set; }
    public bool IsLocal { get; set; }
}

public sealed class ReplayMechanic
{
    public string EntryId { get; set; } = string.Empty;
    public string SlideId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public uint ActionId { get; set; }
    public int Occurrence { get; set; }
    public float Time { get; set; }
    /// <summary>Expected timing from the observed cast bar or authored clock, not a damage snapshot.</summary>
    public float ExpectedResolve { get; set; }
}
