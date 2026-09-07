using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Model;

/// <summary>Look of the arena that every slide is drawn on.</summary>
public sealed class ArenaSettings
{
    [DefaultValue(ArenaShape.Circle)]
    public ArenaShape Shape { get; set; } = ArenaShape.Circle;

    /// <summary>Width divided by height. Only meaningful for <see cref="ArenaShape.Rectangle"/>.</summary>
    [DefaultValue(1.0f)]
    public float AspectRatio { get; set; } = 1.0f;

    [DefaultValue(true)]
    public bool ShowGrid { get; set; } = true;

    [DefaultValue(8)]
    public int GridDivisions { get; set; } = 8;

    [DefaultValue(true)]
    public bool ShowCardinals { get; set; } = true;

    /// <summary>Draws the eight standard waymark seats around the edge as faint guides.</summary>
    [DefaultValue(false)]
    public bool ShowWaymarkGuides { get; set; }

    [DefaultValue(0xFF1A1A1Eu)]
    public uint BackgroundColor { get; set; } = 0xFF1A1A1E;

    [DefaultValue(0xFF4A4A55u)]
    public uint LineColor { get; set; } = 0xFF4A4A55;

    [DefaultValue(0x22FFFFFFu)]
    public uint GridColor { get; set; } = 0x22FFFFFF;
}

/// <summary>A complete strategy sheet: roster, slides and timeline.</summary>
public sealed class PlanDocument
{
    public const int CurrentFormatVersion = 4;

    private int formatVersion = 1;
    [DefaultValue(1)]
    public int FormatVersion
    {
        get => StrategyEvidence is { Count: > 0 } || Timeline?.Any(e => e != null && (e.InferredCastActionId != 0 || e.EvidenceCreated)) == true || Slides?.Any(s => s != null &&
                   (s.ArenaOverride != null || !string.IsNullOrEmpty(s.SourceUrl) || !string.IsNullOrEmpty(s.GuideUrl) || !string.IsNullOrEmpty(s.SourceLabel) || s.SourceStep >= 0)) == true
            ? Math.Max(4, formatVersion)
            : AdaptiveMechanics?.Any(r => r?.Branches?.Any(b => b?.AdditionalStatuses is { Count: > 0 }) == true) == true
            ? Math.Max(3, formatVersion) : AdaptiveMechanics is { Count: > 0 } ? Math.Max(2, formatVersion) : formatVersion;
        set => formatVersion = value;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [DefaultValue("Untitled plan")]
    public string Name { get; set; } = "Untitled plan";

    /// <summary>Free text describing the fight, e.g. "AAC Cruiserweight M4S".</summary>
    [DefaultValue("")]
    public string Encounter { get; set; } = string.Empty;

    [DefaultValue("")]
    public string Author { get; set; } = string.Empty;

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    [DefaultValue("")]
    public string Notes { get; set; } = string.Empty;

    public ArenaSettings Arena { get; set; } = new();

    public List<PlayerSlot> Roster { get; set; } = new();

    public List<Slide> Slides { get; set; } = new();

    public List<TimelineEntry> Timeline { get; set; } = new();

    public List<AdaptiveMechanic> AdaptiveMechanics { get; set; } = new();
    public bool ShouldSerializeAdaptiveMechanics() => AdaptiveMechanics.Count > 0;

    public List<StrategyEvidenceAttachment> StrategyEvidence { get; set; } = new();
    public bool ShouldSerializeStrategyEvidence() => StrategyEvidence is { Count: > 0 };

    public bool ShouldSerializeTimeline() => Timeline.Count > 0;

    public Slide? FindSlide(string id) =>
        string.IsNullOrEmpty(id) ? null : Slides.FirstOrDefault(s => s.Id == id);

    public int IndexOfSlide(string id)
    {
        for (var i = 0; i < Slides.Count; i++)
        {
            if (Slides[i].Id == id)
                return i;
        }

        return -1;
    }

    /// <summary>A blank light-party or full-party plan with sensible seat placeholders.</summary>
    public static PlanDocument CreateDefault(string name = "New plan", int seats = 8)
    {
        var doc = new PlanDocument { Name = name };
        doc.Slides.Add(new Slide { Title = "Slide 1" });

        var layout = DefaultSeats(seats);
        for (var i = 0; i < seats; i++)
        {
            var (placeholder, role) = layout[i];
            doc.Roster.Add(new PlayerSlot
            {
                Placeholder = placeholder,
                Role = role,
                Color = RoleColors.Default(role),
            });
        }

        return doc;
    }

    private static (string, RaidRole)[] DefaultSeats(int seats)
    {
        var full = new (string, RaidRole)[]
        {
            ("MT", RaidRole.Tank),
            ("OT", RaidRole.Tank),
            ("H1", RaidRole.Healer),
            ("H2", RaidRole.Healer),
            ("M1", RaidRole.Melee),
            ("M2", RaidRole.Melee),
            ("R1", RaidRole.PhysicalRanged),
            ("R2", RaidRole.MagicalRanged),
        };

        if (seats <= full.Length)
            return full.Take(seats).ToArray();

        var result = new List<(string, RaidRole)>(full);
        for (var i = full.Length; i < seats; i++)
            result.Add(($"P{i + 1}", RaidRole.Unknown));
        return result.ToArray();
    }
}

/// <summary>Default token colours per role, in ImGui's packed ABGR order.</summary>
public static class RoleColors
{
    public static uint Default(RaidRole role) => role switch
    {
        RaidRole.Tank => 0xFFD98A4A,           // steel blue, ABGR
        RaidRole.Healer => 0xFF6FCB6F,         // green
        RaidRole.Melee => 0xFF5B5BE0,          // red
        RaidRole.PhysicalRanged => 0xFF6FD0E8, // amber
        RaidRole.MagicalRanged => 0xFFE07BC8,  // violet
        _ => 0xFFAAAAAA,
    };

    public static string Label(RaidRole role) => role switch
    {
        RaidRole.Tank => "Tank",
        RaidRole.Healer => "Healer",
        RaidRole.Melee => "Melee",
        RaidRole.PhysicalRanged => "Phys. ranged",
        RaidRole.MagicalRanged => "Magic ranged",
        _ => "Unassigned",
    };
}
