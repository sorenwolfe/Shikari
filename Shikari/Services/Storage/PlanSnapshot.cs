using System;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Storage;

/// <summary>A privately owned graph captured on the editor thread; only encoding reads it later.</summary>
internal sealed class PlanSnapshot
{
    private readonly PlanDocument document;
    private PlanSnapshot(PlanDocument document) => this.document = document;
    public string PlanId => document.Id;
    public string Name => document.Name;
    public DateTime ModifiedUtc => document.ModifiedUtc;

    public static PlanSnapshot Capture(PlanDocument source, DateTime modifiedUtc)
    {
        var copy = Copy(source);
        copy.ModifiedUtc = modifiedUtc;
        return new PlanSnapshot(copy);
    }

    public string Encode() => JsonConvert.SerializeObject(document, PlanJson.Readable());

    /// <summary>Typed graph copy without JSON encoding, normalization, or changing any identity.</summary>
    internal static PlanDocument Copy(PlanDocument source) => new()
    {
        FormatVersion = source.FormatVersion, Id = source.Id, Name = source.Name,
        Encounter = source.Encounter, Author = source.Author, ModifiedUtc = source.ModifiedUtc,
        Notes = source.Notes, Arena = Copy(source.Arena),
        Roster = source.Roster.Select(p => new PlayerSlot
        {
            Id = p.Id, Name = p.Name, Nickname = p.Nickname, JobId = p.JobId,
            Role = p.Role, Color = p.Color, Placeholder = p.Placeholder,
        }).ToList(),
        Slides = source.Slides.Select(s => new Slide
        {
            Id = s.Id, Title = s.Title, Notes = s.Notes, SourceUrl = s.SourceUrl,
            GuideUrl = s.GuideUrl, SourceLabel = s.SourceLabel, SourceStep = s.SourceStep,
            ArenaOverride = s.ArenaOverride == null ? null : Copy(s.ArenaOverride),
            BackdropId = s.BackdropId, BackdropOpacity = s.BackdropOpacity,
            Items = s.Items.Select(Copy).ToList(),
        }).ToList(),
        Timeline = source.Timeline.Select(Copy).ToList(),
        AdaptiveMechanics = source.AdaptiveMechanics.Select(r => new AdaptiveMechanic
        {
            Id = r.Id, Label = r.Label, Enabled = r.Enabled, TerritoryId = r.TerritoryId,
            AnchorActionId = r.AnchorActionId, Occurrence = r.Occurrence, WindowSeconds = r.WindowSeconds,
            Branches = r.Branches.Select(b => new StatusBranch
            {
                Label = b.Label, StatusId = b.StatusId, Parameter = b.Parameter,
                MinimumSeconds = b.MinimumSeconds, MaximumSeconds = b.MaximumSeconds, SlideId = b.SlideId,
                AdditionalStatuses = b.AdditionalStatuses.Select(c => new StatusCondition
                {
                    StatusId = c.StatusId, Parameter = c.Parameter,
                    MinimumSeconds = c.MinimumSeconds, MaximumSeconds = c.MaximumSeconds,
                }).ToList(),
            }).ToList(),
        }).ToList(),
        StrategyEvidence = source.StrategyEvidence.Select(a => new StrategyEvidenceAttachment
        {
            Key = a.Key, Source = a.Source, ReportCode = a.ReportCode, FightId = a.FightId,
            EncounterId = a.EncounterId, TerritoryId = a.TerritoryId, EncounterVerified = a.EncounterVerified,
            Complete = a.Complete, OmittedMechanics = a.OmittedMechanics,
            Mechanics = a.Mechanics.Select(m => new StrategyMechanicEvidence
            {
                EntryId = m.EntryId, SlideId = m.SlideId, Match = m.Match, ActionId = m.ActionId,
                Occurrence = m.Occurrence, CastTime = m.CastTime, ResolveTime = m.ResolveTime,
                ExpectedResolveTime = m.ExpectedResolveTime, ResolveObserved = m.ResolveObserved,
                Actors = m.Actors.Select(p => new StrategyActorEvidence
                {
                    Actor = p.Actor, SlotIndex = p.SlotIndex, JobId = p.JobId, DestinationDistance = p.DestinationDistance,
                    Statuses = p.Statuses.Select(s => new StrategyStatusEvidence
                    {
                        StatusId = s.StatusId, AbilityId = s.AbilityId, Time = s.Time,
                        Baseline = s.Baseline, Parameter = s.Parameter, Duration = s.Duration,
                    }).ToList(),
                    Positions = p.Positions.Select(s => new StrategyPositionEvidence
                    {
                        Time = s.Time, Position = s.Position, Calibrated = s.Calibrated,
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        }).ToList(),
    };

    private static ArenaSettings Copy(ArenaSettings a) => new()
    {
        Shape = a.Shape, AspectRatio = a.AspectRatio, ShowGrid = a.ShowGrid,
        GridDivisions = a.GridDivisions, ShowCardinals = a.ShowCardinals,
        ShowWaymarkGuides = a.ShowWaymarkGuides, BackgroundColor = a.BackgroundColor,
        LineColor = a.LineColor, GridColor = a.GridColor,
    };

    private static CanvasItem Copy(CanvasItem source)
    {
        var copy = source.Clone();
        copy.Id = source.Id;
        return copy;
    }

    private static TimelineEntry Copy(TimelineEntry source)
    {
        var copy = source.Clone();
        copy.Id = source.Id;
        copy.Label = source.Label;
        return copy;
    }
}
