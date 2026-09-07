using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Model;

/// <summary>One step of a strategy: a drawn arena plus the notes that go with it.</summary>
public sealed class Slide
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [DefaultValue("New slide")]
    public string Title { get; set; } = "New slide";

    /// <summary>Free-form notes shown beside the arena.</summary>
    [DefaultValue("")]
    public string Notes { get; set; } = string.Empty;

    public List<CanvasItem> Items { get; set; } = new();

    /// <summary>Imported boards may use different arena settings within one guide.</summary>
    public ArenaSettings? ArenaOverride { get; set; }
    [DefaultValue("")]
    public string SourceUrl { get; set; } = "";
    /// <summary>Original guide provenance, retained when SourceUrl identifies an editable board step.</summary>
    [DefaultValue("")]
    public string GuideUrl { get; set; } = "";
    [DefaultValue("")]
    public string SourceLabel { get; set; } = "";
    [DefaultValue(-1)]
    public int SourceStep { get; set; } = -1;
    public bool ShouldSerializeArenaOverride() => ArenaOverride != null;

    /// <summary>
    /// A reference image drawn faintly under the arena, for tracing a plan from somewhere else.
    /// Empty for most slides. The id names a file in the plugin's own backdrop folder.
    /// </summary>
    [DefaultValue("")]
    public string BackdropId { get; set; } = string.Empty;

    [DefaultValue(0.45f)]
    public float BackdropOpacity { get; set; } = 0.45f;

    public bool HasBackdrop => !string.IsNullOrEmpty(BackdropId);

    public bool ShouldSerializeItems() => Items.Count > 0;

    public bool ShouldSerializeBackdropOpacity() => HasBackdrop;

    public bool ShouldSerializeHasBackdrop() => false;

    public Slide Clone(string? newTitle = null)
    {
        return new Slide
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = newTitle ?? (Title + " (copy)"),
            Notes = Notes,
            SourceUrl = SourceUrl,
            GuideUrl = GuideUrl,
            SourceLabel = SourceLabel,
            SourceStep = SourceStep,
            ArenaOverride = ArenaOverride == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<ArenaSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(ArenaOverride)),
            BackdropId = BackdropId,
            BackdropOpacity = BackdropOpacity,
            Items = Items.Select(i => i.Clone()).ToList(),
        };
    }
}
