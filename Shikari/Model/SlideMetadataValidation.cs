using System;
using System.Linq;

namespace Shikari.Model;

/// <summary>Bounds imported arena rendering and source metadata without changing authored geometry.</summary>
public static class SlideMetadataValidation
{
    public static bool IsValid(Slide slide) => ValidSource(slide.SourceUrl) && ValidSource(slide.GuideUrl) &&
        slide.SourceLabel != null && slide.SourceLabel.Length <= 256 && !slide.SourceLabel.Any(char.IsControl) &&
        slide.SourceStep is >= -1 and <= 4096 && (slide.ArenaOverride == null || ValidArena(slide.ArenaOverride));

    public static bool ValidArena(ArenaSettings arena) => Enum.IsDefined(arena.Shape) &&
        float.IsFinite(arena.AspectRatio) && arena.AspectRatio is >= 0.2f and <= 100 && arena.GridDivisions is >= 2 and <= 64;

    public static void Normalise(Slide slide, ArenaSettings fallback)
    {
        if (!ValidSource(slide.SourceUrl) || !ValidSource(slide.GuideUrl) && string.IsNullOrEmpty(slide.SourceUrl))
        {
            slide.SourceUrl = string.Empty;
            // Do not turn a damaged source identifier into a legacy shared alignment domain.
            // An unidentified override intentionally cannot borrow another slide's waymarks.
            slide.ArenaOverride ??= new ArenaSettings
            {
                Shape = fallback.Shape, AspectRatio = fallback.AspectRatio, ShowGrid = fallback.ShowGrid,
                GridDivisions = fallback.GridDivisions, ShowCardinals = fallback.ShowCardinals,
                ShowWaymarkGuides = fallback.ShowWaymarkGuides, BackgroundColor = fallback.BackgroundColor,
                LineColor = fallback.LineColor, GridColor = fallback.GridColor,
            };
        }
        if (!ValidSource(slide.GuideUrl)) slide.GuideUrl = string.Empty;
        slide.SourceLabel = new string((slide.SourceLabel ?? string.Empty).Where(c => !char.IsControl(c)).Take(256).ToArray());
        slide.SourceStep = Math.Clamp(slide.SourceStep, -1, 4096);
        if (slide.ArenaOverride != null) NormaliseArena(slide.ArenaOverride);
    }

    public static void NormaliseArena(ArenaSettings arena)
    {
        if (!Enum.IsDefined(arena.Shape)) arena.Shape = ArenaShape.Circle;
        arena.AspectRatio = float.IsFinite(arena.AspectRatio) ? Math.Clamp(arena.AspectRatio, 0.2f, 100) : 1;
        arena.GridDivisions = Math.Clamp(arena.GridDivisions, 2, 64);
    }

    private static bool ValidSource(string? source) => source != null && (source.Length == 0 ||
        source.Length <= 2048 && !source.Any(char.IsControl) &&
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0);
}
