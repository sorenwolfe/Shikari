using System;
using System.Collections.Generic;
using System.Linq;

namespace Shikari.Services.Replay;

public enum ReplayLoadState { Missing, Loading, Ready, Failed }
public sealed record ReplayCatalogMechanic(string EntryId, uint ActionId, int Occurrence);

/// <summary>List/filter metadata only. A catalog entry can never supply mechanic evidence.</summary>
public sealed class ReplayCatalogEntry
{
    public string Id { get; init; } = "";
    public string PlanId { get; init; } = "";
    public string PlanName { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public float Duration { get; init; }
    public string EndReason { get; init; } = "";
    public string Source { get; init; } = "";
    public string ReportCode { get; init; } = "";
    public int FightId { get; init; }
    public IReadOnlyList<ReplayCatalogMechanic> Mechanics { get; init; } = Array.Empty<ReplayCatalogMechanic>();

    public static ReplayCatalogEntry From(ReplayAttempt attempt) => new()
    {
        Id = attempt.Id, PlanId = attempt.Plan.Id, PlanName = attempt.Plan.Name, StartedUtc = attempt.StartedUtc,
        Duration = attempt.Duration, EndReason = attempt.EndReason, Source = attempt.Evidence.Source,
        ReportCode = attempt.Evidence.ReportCode, FightId = attempt.Evidence.FightId,
        Mechanics = Array.AsReadOnly(attempt.Mechanics.Select(m => new ReplayCatalogMechanic(m.EntryId, m.ActionId, m.Occurrence)).ToArray()),
    };
}

/// <summary>Conservative accounting for resident managed graphs, not a process-wide heap measurement.</summary>
internal static class ReplayMemory
{
    public static long Estimate(ReplayAttempt a)
    {
        static long Text(string? value) => 32L + 2L * (value?.Length ?? 0);
        long bytes = 65536 + Text(a.Plan.Name) + Text(a.Plan.Notes) + Text(a.EndReason);
        foreach (var s in a.Plan.Slides)
        {
            bytes += 512 + Text(s.Title) + Text(s.Notes) + Text(s.SourceUrl) + Text(s.GuideUrl) + Text(s.SourceLabel);
            foreach (var i in s.Items) bytes += 512 + Text(i.Text) + Text(i.Emoji) + i.Points.Count * 16L;
        }
        foreach (var t in a.Plan.Timeline) bytes += 2048 + Text(t.Label);
        bytes += a.Plan.Roster.Count * 4096L + a.Plan.AdaptiveMechanics.Count * 16384L;
        foreach (var e in a.Plan.StrategyEvidence)
            foreach (var m in e.Mechanics) bytes += 1024 + m.Actors.Sum(p => 512L + p.Statuses.Count * 128L + p.Positions.Count * 64L);
        foreach (var f in a.Frames) bytes += 128 + f.Players.Sum(p => 192L + Text(p.Name));
        foreach (var s in a.Evidence.Statuses) bytes += 160 + Text(s.Name);
        foreach (var e in a.Evidence.Effects) bytes += 224 + Text(e.Name);
        bytes += a.Evidence.Positions.Count * 64L + a.Evidence.Actors.Sum(p => 256L + Text(p.Name) + Text(p.Job));
        bytes += a.Casts.Count * 512L + a.Mechanics.Sum(m => 256L + Text(m.Label));
        bytes += a.AdaptiveDecisions.Sum(d => 256L + Text(d.Reason) + Text(d.Mechanic) + Text(d.Navigation));
        return bytes + a.StatusObservations.Count * 128L;
    }
}
