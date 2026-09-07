using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Shikari.Model;

/// <summary>A local player's positive status assignment, scoped to a cast and territory.</summary>
public sealed class AdaptiveMechanic
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Status assignment";
    public bool Enabled { get; set; }
    public uint TerritoryId { get; set; }
    public uint AnchorActionId { get; set; }
    [DefaultValue(1)]
    public int Occurrence { get; set; } = 1;
    public float WindowSeconds { get; set; } = 15;
    public List<StatusBranch> Branches { get; set; } = new();

    public bool Overlaps(AdaptiveMechanic other) => TerritoryId == other.TerritoryId &&
        AnchorActionId == other.AnchorActionId && (Occurrence == 0 || other.Occurrence == 0 || Occurrence == other.Occurrence);

    public bool IsValid(PlanDocument plan) => TerritoryId > 0 && AnchorActionId > 0 && Occurrence >= 0 &&
        float.IsFinite(WindowSeconds) && WindowSeconds >= 1 && WindowSeconds <= 60 &&
        Branches is { Count: > 0 and <= 16 } && Branches.All(b => b != null &&
            StatusCondition.IsValid(b.StatusId, b.Parameter, b.MinimumSeconds, b.MaximumSeconds) &&
            b.AdditionalStatuses is { Count: <= 3 } && b.AdditionalStatuses.All(c => c != null &&
                StatusCondition.IsValid(c.StatusId, c.Parameter, c.MinimumSeconds, c.MaximumSeconds)) &&
            plan.FindSlide(b.SlideId) != null);
}

/// <summary>An AND condition. The full [0, 3600) range accepts an unknown initial duration.</summary>
public sealed class StatusCondition
{
    public uint StatusId { get; set; }
    [DefaultValue(-1)]
    public int Parameter { get; set; } = -1;
    public float MinimumSeconds { get; set; }
    public float MaximumSeconds { get; set; } = 60;

    internal static bool IsValid(uint statusId, int parameter, float minimum, float maximum) =>
        statusId > 0 && parameter >= -1 && parameter <= ushort.MaxValue && float.IsFinite(minimum) &&
        float.IsFinite(maximum) && minimum >= 0 && maximum > minimum && maximum <= 3600;
}

public sealed class StatusBranch
{
    public string Label { get; set; } = "New branch";
    public uint StatusId { get; set; }
    [DefaultValue(-1)]
    public int Parameter { get; set; } = -1;
    public float MinimumSeconds { get; set; }
    public float MaximumSeconds { get; set; } = 60;
    public string SlideId { get; set; } = string.Empty;
    public List<StatusCondition> AdditionalStatuses { get; set; } = new();
    public bool ShouldSerializeAdditionalStatuses() => AdditionalStatuses is { Count: > 0 };
}

public sealed class AdaptiveDecision
{
    public uint AnchorActionId { get; set; }
    public int Occurrence { get; set; }
    public float Time { get; set; }
    public string Mechanic { get; set; } = string.Empty;
    public string SlideId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public string Navigation { get; set; } = string.Empty;
}

public sealed class StatusObservation
{
    public float Time { get; set; }
    public uint StatusId { get; set; }
    public float Duration { get; set; }
    public ushort Parameter { get; set; }
    public uint SourceId { get; set; }
    public bool Removed { get; set; }
    public bool Baseline { get; set; }
    [DefaultValue(true)]
    public bool ParameterKnown { get; set; } = true;
    [DefaultValue(true)]
    public bool DurationKnown { get; set; } = true;
}
