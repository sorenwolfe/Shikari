using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;
using Shikari.Services.FfLogs;

namespace Shikari.Services.Replay;

public static class LogReplayBuilder
{
    public static ReplayAttempt Build(PlanDocument plan, LogFightData data, LogEvidence source,
        Func<uint, bool> validStatus, Func<string, uint> jobId)
    {
        if (data.Fight.DurationSeconds <= 0 || data.Fight.DurationSeconds > ReplayBuffer.MaxDuration)
            throw new InvalidOperationException("Choose a pull between 0 and 30 minutes long.");
        var attempt = new ReplayBuffer(plan, -1, DateTime.UtcNow).Attempt;
        attempt.Duration = data.Fight.DurationSeconds;
        attempt.EndReason = data.Fight.Kill ? "FF Logs kill" : "FF Logs pull";
        var evidence = attempt.Evidence;
        evidence.Source = "FF Logs";
        evidence.Url = $"https://www.fflogs.com/reports/{data.ReportCode}?fight={data.Fight.Id}";
        evidence.ReportCode = data.ReportCode;
        evidence.FightId = data.Fight.Id;
        evidence.EncounterId = data.Fight.EncounterId;
        evidence.Complete = source.Complete;
        evidence.Warnings.AddRange(source.Warnings.Take(90));
        var participating = source.StatusEvents.Select(e => e.TargetId).Concat(source.Positions.Select(e => e.ActorId))
            .Concat(data.PlayerCasts.Select(c => c.SourceId)).ToHashSet();
        foreach (var actor in data.Actors.Where(a => a.IsPlayer && participating.Contains(a.Id)).Take(32))
        {
            var job = jobId(actor.Job);
            // Duplicate jobs stay unassigned until the user chooses a seat in Review.
            var seats = plan.Roster.Select((s, i) => (s, i)).Where(p =>
                string.Equals(p.s.Name, actor.Name, StringComparison.OrdinalIgnoreCase) || job != 0 && p.s.JobId == job).ToArray();
            evidence.Actors.Add(new EvidenceActor { Id = actor.Id, Name = actor.Name, Job = actor.Job, JobId = job,
                SlotIndex = seats.Length == 1 ? seats[0].i : -1 });
        }
        foreach (var group in evidence.Actors.Where(a => a.SlotIndex >= 0).GroupBy(a => a.SlotIndex).Where(g => g.Count() > 1))
            foreach (var actor in group) actor.SlotIndex = -1;
        var actorIds = evidence.Actors.Select(a => a.Id).ToHashSet();
        foreach (var item in source.StatusEvents.Where(s => actorIds.Contains(s.TargetId)).OrderBy(s => s.Time))
        {
            if (evidence.Statuses.Count == ReplayEvidence.MaxStatuses) { evidence.Complete = false; break; }
            evidence.Statuses.Add(new EvidenceStatus { ActorId = item.TargetId, SourceId = item.SourceId,
                Time = item.Time, StatusId = item.StatusId != 0 && validStatus(item.StatusId) ? item.StatusId : 0,
                AbilityId = item.AbilityId, Name = item.Name, Duration = item.Duration, Parameter = item.Parameter,
                Stacks = item.Stacks, Baseline = item.Change == LogStatusChange.Baseline,
                Change = item.Change switch { LogStatusChange.Remove => "remove", LogStatusChange.Refresh => "refresh",
                    LogStatusChange.Stacks => "stacks", _ => "apply" } });
        }
        // Logs can record several resource snapshots for one actor in a single timestamp. Keep
        // the last observed value; never fill the time between events with synthetic movement.
        foreach (var item in source.Positions.Where(p => actorIds.Contains(p.ActorId)).OrderBy(p => p.Time)
                     .GroupBy(p => (p.ActorId, p.Time)).Select(g => g.Last()))
        {
            if (evidence.Positions.Count == ReplayEvidence.MaxPositions) { evidence.Complete = false; break; }
            evidence.Positions.Add(new EvidencePosition { ActorId = item.ActorId, Time = item.Time, Position = new(item.X, item.Y) });
        }
        var counts = new Dictionary<uint, int>();
        foreach (var cast in data.EnemyCasts.Where(c => c.IsCastStart || c.CastSeconds > 0).OrderBy(c => c.TimeSeconds))
        {
            var occurrence = counts.GetValueOrDefault(cast.AbilityId) + 1;
            counts[cast.AbilityId] = occurrence;
            var entries = plan.Timeline.Where(e => e.CastActionId == cast.AbilityId &&
                (e.Occurrence == 0 || e.Occurrence == occurrence)).ToArray();
            var entry = entries.Length == 1 ? entries[0] : null;
            if (attempt.Mechanics.Count >= ReplayBuffer.MaxMechanics) { evidence.Complete = false; break; }
            attempt.Mechanics.Add(new ReplayMechanic { ActionId = cast.AbilityId, Occurrence = occurrence,
                Label = string.IsNullOrEmpty(cast.AbilityName) ? "Cast #" + cast.AbilityId : cast.AbilityName,
                Time = cast.TimeSeconds, ExpectedResolve = cast.TimeSeconds + cast.CastSeconds,
                EntryId = entry?.Id ?? "", SlideId = entry?.SlideId ?? "" });
        }
        if (!evidence.Complete) evidence.Warnings.Add("This reference is incomplete. Missing observations cannot establish an assignment.");
        evidence.Warnings.Add("Positions are sparse FF Logs centicoordinates. Identify matching landmarks before overlaying them on the plan.");
        if (evidence.Statuses.Any(s => s.StatusId == 0))
            evidence.Warnings.Add("Some status IDs could not be verified against the game data; they cannot create live rules.");
        return attempt;
    }
}
