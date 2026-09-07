using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Model;

/// <summary>A cooldown (or any other action) handed to one seat at one moment of the fight.</summary>
public sealed class Assignment
{
    /// <summary>Index into <see cref="PlanDocument.Roster"/>.</summary>
    [DefaultValue(0)]
    public int SlotIndex { get; set; }

    /// <summary>Action sheet row id.</summary>
    [DefaultValue(0u)]
    public uint ActionId { get; set; }

    /// <summary>Cached name so a plan still reads correctly if the sheet lookup fails.</summary>
    [DefaultValue("")]
    public string ActionName { get; set; } = string.Empty;

    /// <summary>Optional qualifier, e.g. "on the group", "delay 3s".</summary>
    [DefaultValue("")]
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// One row of the fight timeline: when it happens, which slide explains it, who presses what,
/// and what each player is told.
/// </summary>
public sealed class TimelineEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Human name for the step, e.g. "Akh Morn 1".</summary>
    [DefaultValue("New step")]
    public string Label { get; set; } = "New step";

    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DefaultValue(TriggerKind.BossCast)]
    public TriggerKind Trigger { get; set; } = TriggerKind.BossCast;

    /// <summary>Action row id of the boss cast this step is anchored to.</summary>
    [DefaultValue(0u)]
    public uint CastActionId { get; set; }

    /// <summary>Service-inferred anchor; name matching remains authoritative until its action is edited.</summary>
    [DefaultValue(0u)]
    public uint InferredCastActionId { get; set; }

    /// <summary>This disabled row was created by matching an otherwise untimed imported board.</summary>
    [DefaultValue(false)]
    public bool EvidenceCreated { get; set; }

    /// <summary>Cached cast name, shown when the sheet lookup is unavailable.</summary>
    [DefaultValue("")]
    public string CastName { get; set; } = string.Empty;

    /// <summary>
    /// Which use of that cast within the pull, counting from 1. Zero or below means
    /// "every time the boss casts it".
    /// </summary>
    [DefaultValue(1)]
    public int Occurrence { get; set; } = 1;

    /// <summary>For <see cref="TriggerKind.CombatTime"/>: seconds after the pull.</summary>
    [DefaultValue(0f)]
    public float TimeSeconds { get; set; }

    /// <summary>For <see cref="TriggerKind.AfterCast"/>: seconds after the anchor cast begins.</summary>
    [DefaultValue(10f)]
    public float OffsetSeconds { get; set; } = 10f;

    /// <summary>How far ahead of the moment the call should be delivered.</summary>
    [DefaultValue(5f)]
    public float LeadSeconds { get; set; } = 5f;

    /// <summary>Slide that illustrates this step, or empty for none.</summary>
    [DefaultValue("")]
    public string SlideId { get; set; } = string.Empty;

    public List<Assignment> Assignments { get; set; } = new();

    /// <summary>Default call text, used for any seat without an override.</summary>
    [DefaultValue("")]
    public string CallText { get; set; } = string.Empty;

    /// <summary>Per-seat call text, keyed by roster index.</summary>
    public Dictionary<int, string> SlotCallText { get; set; } = new();

    [DefaultValue(CallAudience.Everyone)]
    public CallAudience Audience { get; set; } = CallAudience.Everyone;

    /// <summary>Display time used for ordering the list, in seconds. Editable for cast triggers too.</summary>
    [DefaultValue(0f)]
    public float SortTime { get; set; }

    /// <summary>Opt-in review checkpoint. A plan position alone is not a mechanic verdict.</summary>
    [DefaultValue(false)]
    public bool ReviewCheckpointEnabled { get; set; }

    /// <summary>Seconds relative to the expected cast end (or authored clock anchor).</summary>
    [DefaultValue(0f)]
    public float ReviewOffsetSeconds { get; set; }

    [DefaultValue(2f)]
    public float ReviewRadiusYalms { get; set; } = 2f;

    public bool ShouldSerializeAssignments() => Assignments.Count > 0;

    public bool ShouldSerializeSlotCallText() => SlotCallText.Count > 0;

    public IEnumerable<Assignment> ForSlot(int slotIndex) =>
        Assignments.Where(a => a.SlotIndex == slotIndex);

    public bool HasAnythingFor(int slotIndex) =>
        Assignments.Any(a => a.SlotIndex == slotIndex) ||
        (SlotCallText.TryGetValue(slotIndex, out var t) && !string.IsNullOrWhiteSpace(t));

    public TimelineEntry Clone()
    {
        return new TimelineEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = Label + " (copy)",
            Enabled = Enabled,
            Trigger = Trigger,
            CastActionId = CastActionId,
            InferredCastActionId = InferredCastActionId,
            EvidenceCreated = EvidenceCreated,
            CastName = CastName,
            Occurrence = Occurrence,
            TimeSeconds = TimeSeconds,
            OffsetSeconds = OffsetSeconds,
            LeadSeconds = LeadSeconds,
            SlideId = SlideId,
            Assignments = Assignments
                .Select(a => new Assignment { SlotIndex = a.SlotIndex, ActionId = a.ActionId, ActionName = a.ActionName, Note = a.Note })
                .ToList(),
            CallText = CallText,
            SlotCallText = new Dictionary<int, string>(SlotCallText),
            Audience = Audience,
            SortTime = SortTime,
            ReviewCheckpointEnabled = ReviewCheckpointEnabled,
            ReviewOffsetSeconds = ReviewOffsetSeconds,
            ReviewRadiusYalms = ReviewRadiusYalms,
        };
    }
}
