using System.Linq;
using Shikari.Model;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>Detached owner-thread capture. JSON validation/encoding and disk IO run on the worker.</summary>
internal static class ReplaySnapshot
{
    public static ReplayAttempt Copy(ReplayAttempt a) => new()
    {
        Version = a.Version, Id = a.Id, StartedUtc = a.StartedUtc, Plan = PlanSnapshot.Copy(a.Plan),
        LocalSlot = a.LocalSlot, TerritoryId = a.TerritoryId, Duration = a.Duration, EndReason = a.EndReason,
        Frames = a.Frames.Select(f => new ReplayFrame { Time = f.Time, SlideId = f.SlideId, Valid = f.Valid,
            BoardPerYalm = f.BoardPerYalm, Players = f.Players.Select(p => new ReplayPlayer { Name = p.Name,
                JobId = p.JobId, SlotIndex = p.SlotIndex, Board = p.Board, IsLocal = p.IsLocal }).ToList() }).ToList(),
        Mechanics = a.Mechanics.Select(m => new ReplayMechanic { EntryId = m.EntryId, SlideId = m.SlideId,
            Label = m.Label, ActionId = m.ActionId, Occurrence = m.Occurrence, Time = m.Time, ExpectedResolve = m.ExpectedResolve }).ToList(),
        Casts = a.Casts.Select(c => c.Snapshot()).ToList(),
        StatusObservations = a.StatusObservations.Select(s => new StatusObservation { Time = s.Time, StatusId = s.StatusId,
            Duration = s.Duration, Parameter = s.Parameter, SourceId = s.SourceId, Removed = s.Removed, Baseline = s.Baseline,
            ParameterKnown = s.ParameterKnown, DurationKnown = s.DurationKnown }).ToList(),
        AdaptiveDecisions = a.AdaptiveDecisions.Select(d => new AdaptiveDecision { RuleId = d.RuleId, BranchIndex = d.BranchIndex,
            Conflict = d.Conflict, AnchorActionId = d.AnchorActionId, Occurrence = d.Occurrence, Time = d.Time,
            Mechanic = d.Mechanic, SlideId = d.SlideId, Reason = d.Reason, Applied = d.Applied, Navigation = d.Navigation }).ToList(),
        Evidence = new ReplayEvidence { Source = a.Evidence.Source, Url = a.Evidence.Url, ReportCode = a.Evidence.ReportCode,
            FightId = a.Evidence.FightId, EncounterId = a.Evidence.EncounterId, Complete = a.Evidence.Complete,
            EffectsComplete = a.Evidence.EffectsComplete, CalibrationSlideId = a.Evidence.CalibrationSlideId,
            EffectTargetActorIds = a.Evidence.EffectTargetActorIds?.ToList(),
            Warnings = a.Evidence.Warnings.ToList(),
            Actors = a.Evidence.Actors.Select(p => new EvidenceActor { Id = p.Id, GameObjectId = p.GameObjectId,
                Name = p.Name, Job = p.Job, JobId = p.JobId, SlotIndex = p.SlotIndex, IsLocal = p.IsLocal }).ToList(),
            Statuses = a.Evidence.Statuses.Select(s => new EvidenceStatus { Time = s.Time, ActorId = s.ActorId, SourceId = s.SourceId,
                StatusId = s.StatusId, AbilityId = s.AbilityId, Name = s.Name, Change = s.Change, Duration = s.Duration,
                Parameter = s.Parameter, Stacks = s.Stacks, Baseline = s.Baseline }).ToList(),
            Positions = a.Evidence.Positions.Select(p => new EvidencePosition { Time = p.Time, ActorId = p.ActorId, Position = p.Position }).ToList(),
            Effects = a.Evidence.Effects.Select(e => new EvidenceEffect { Time = e.Time, ActionId = e.ActionId, Name = e.Name,
                Type = e.Type, SourceId = e.SourceId, TargetId = e.TargetId, SourceInstance = e.SourceInstance,
                TargetInstance = e.TargetInstance, PacketId = e.PacketId, SourcePosition = e.SourcePosition, TargetPosition = e.TargetPosition }).ToList(),
            References = a.Evidence.References.Select(r => new EvidenceReference { Source = r.Source, Board = r.Board }).ToList() },
    };
}
