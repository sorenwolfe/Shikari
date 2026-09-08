using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Replay;

public sealed record StrategyEnrichmentResult(bool Accepted, bool Changed, int MatchedMechanics,
    int UnassignedMechanics, int DraftsAdded, string Summary);

public static class StrategyEnrichment
{
    public static StrategyEnrichmentResult Apply(PlanDocument plan, ReplayAttempt attempt)
    {
        if (!ReplayValidation.IsValid(attempt) || attempt.Mechanics.Any(m => m.ExpectedResolve < m.Time || m.ExpectedResolve > 1920))
            return Reject("This recording contains invalid or oversized evidence.");
        var evidence = attempt.Evidence;
        var prior = plan.StrategyEvidence ?? new();
        var knownEncounters = prior.Where(a => a.EncounterVerified).Select(a => a.EncounterId).Where(id => id != 0).Distinct().ToArray();
        var knownTerritories = prior.Where(a => a.EncounterVerified).Select(a => a.TerritoryId)
            .Concat(plan.AdaptiveMechanics.Where(a => a.Enabled && a.IsValid(plan)).Select(a => a.TerritoryId))
            .Where(id => id != 0).Distinct().ToArray();
        if (evidence.EncounterId != 0 && knownEncounters.Any(id => id != evidence.EncounterId) ||
            attempt.TerritoryId != 0 && knownTerritories.Any(id => id != attempt.TerritoryId))
            return Reject("This recording belongs to a different encounter or territory.");

        var isLog = evidence.Source == "FF Logs";
        if (isLog && (string.IsNullOrEmpty(evidence.ReportCode) || evidence.ReportCode.Length > 32 ||
            !evidence.ReportCode.All(char.IsAsciiLetterOrDigit) || evidence.FightId <= 0))
            return Reject("This FF Logs recording has no valid report and fight reference.");
        var key = isLog ? $"fflogs:{evidence.ReportCode}:{evidence.FightId}" : "local:" + attempt.Id;
        var previous = prior.FirstOrDefault(a => a.Key == key);
        var attachment = new StrategyEvidenceAttachment { Key = key, Source = isLog ? "FF Logs" : "Local recording",
            ReportCode = isLog ? evidence.ReportCode : "", FightId = isLog ? evidence.FightId : 0,
            EncounterId = evidence.EncounterId, TerritoryId = attempt.TerritoryId, Complete = evidence.Complete,
            EncounterVerified = evidence.EncounterId != 0 && knownEncounters.Contains(evidence.EncounterId) ||
                attempt.TerritoryId != 0 && knownTerritories.Contains(attempt.TerritoryId) };
        // A reference is not its own independent encounter validation on the next analysis.
        if (previous != null) attachment.EncounterVerified = previous.EncounterVerified;
        var timeline = new EvidenceTimeline(evidence);
        var unchangedGeometry = new Dictionary<string, bool>(StringComparer.Ordinal);
        var aligned = EvidenceProjection.TryAlign(evidence, out var alignment);
        var actors = evidence.Actors.Take(StrategyEvidenceValidation.MaxActors).ToArray();
        var slots = actors.ToDictionary(a => a.Id, a => MatchActor(plan, attempt, a));
        foreach (var group in slots.Where(p => p.Value >= 0).GroupBy(p => p.Value).Where(g => g.Count() > 1).ToArray())
            foreach (var pair in group) slots[pair.Key] = -1;
        var changed = false; var matched = 0; var unassigned = 0; var drafts = 0;
        var mechanics = attempt.Mechanics.OrderBy(m => m.Time).ToArray();
        changed |= InferTimeline(plan, mechanics);
        // Resolve all claims before enriching any row. Inferred anchors are still name evidence,
        // so neither an earlier cast in this pull nor a prior analysis can hide an ambiguity.
        var matches = mechanics.Select(m => Match(plan, m)).ToArray();
        for (var i = 0; i < mechanics.Length; i++)
            if (matches[i].Reason == "name" && mechanics.Any(m => m.ActionId != mechanics[i].ActionId &&
                Normalise(m.Label) == Normalise(mechanics[i].Label))) matches[i] = (null, "ambiguous");
        foreach (var claim in matches.Select((match, index) => (match, index)).Where(p => p.match.Entry != null)
                     .GroupBy(p => p.match.Entry!.Id).Where(g => g.Select(p => mechanics[p.index].ActionId).Distinct().Count() > 1))
            foreach (var item in claim) matches[item.index] = (null, "ambiguous");
        attachment.OmittedMechanics = Math.Max(0, mechanics.Length - StrategyEvidenceValidation.MaxMechanics);
        for (var index = 0; index < Math.Min(mechanics.Length, StrategyEvidenceValidation.MaxMechanics); index++)
        {
            var mechanic = mechanics[index];
            var (entry, reason) = matches[index];
            var resolve = Math.Clamp(mechanic.ExpectedResolve, mechanic.Time, attempt.Duration);
            var row = new StrategyMechanicEvidence { EntryId = entry?.Id ?? "", SlideId = entry?.SlideId ?? "",
                Match = reason, ActionId = mechanic.ActionId, Occurrence = Math.Max(0, mechanic.Occurrence),
                CastTime = mechanic.Time, ResolveTime = resolve, ExpectedResolveTime = mechanic.ExpectedResolve,
                ResolveObserved = mechanic.ExpectedResolve <= attempt.Duration };
            var earlier = previous?.Mechanics.FirstOrDefault(m => m.ActionId == row.ActionId && m.Occurrence == row.Occurrence &&
                m.CastTime == row.CastTime && m.EntryId == row.EntryId);
            if (earlier != null && entry != null) row.Match = earlier.Match;
            if (entry == null) unassigned++;
            else
            {
                matched++;
                if (entry.CastActionId == 0 && mechanic.ActionId != 0)
                {
                    entry.CastActionId = mechanic.ActionId;
                    entry.InferredCastActionId = mechanic.ActionId;
                    if (string.IsNullOrEmpty(entry.CastName))
                        entry.CastName = mechanic.Label.Length > 160 ? mechanic.Label[..160] : mechanic.Label;
                    if (entry.Trigger == TriggerKind.CombatTime && entry.TimeSeconds == 0)
                        entry.Trigger = TriggerKind.BossCast;
                    changed = true;
                }
                if (entry.SortTime == 0 && mechanic.Time > 0) { entry.SortTime = mechanic.Time; changed = true; }
            }
            var slide = plan.FindSlide(row.SlideId);
            if (!unchangedGeometry.TryGetValue(row.SlideId, out var geometryMatches))
            {
                geometryMatches = SameGeometry(plan, attempt.Plan, row.SlideId);
                unchangedGeometry[row.SlideId] = geometryMatches;
            }
            var frame = attempt.Frames.LastOrDefault(f => f.Valid && f.SlideId == row.SlideId && f.Time <= resolve &&
                resolve - f.Time <= EvidenceTimeline.MaxPositionAge);
            for (var actorIndex = 0; actorIndex < actors.Length; actorIndex++)
            {
                var actor = actors[actorIndex];
                var actorRow = new StrategyActorEvidence { Actor = actorIndex, SlotIndex = slots[actor.Id], JobId = actor.JobId };
                var active = timeline.StatusesAt(actor.Id, resolve);
                var fresh = active.Where(s => s.StatusId > 0 && !s.Baseline && s.Change is "apply" or "refresh" &&
                    s.Time >= mechanic.Time && s.Time <= mechanic.Time + 59).ToArray();
                actorRow.Statuses = fresh.Concat(active.Except(fresh)).Take(StrategyEvidenceValidation.MaxStatuses).Select(s => new StrategyStatusEvidence {
                    StatusId = s.StatusId, AbilityId = s.AbilityId, Time = s.Time, Baseline = s.Baseline,
                    Parameter = s.Parameter, Duration = s.Duration }).ToList();
                var calibrated = geometryMatches && aligned && row.SlideId.Length > 0 && evidence.CalibrationSlideId == row.SlideId;
                foreach (var time in new[] { mechanic.Time, (mechanic.Time + resolve) / 2, resolve }.Distinct())
                {
                    var position = timeline.PositionAt(actor.Id, time);
                    if (position == null || actorRow.Positions.Any(p => p.Time == position.Time)) continue;
                    actorRow.Positions.Add(new StrategyPositionEvidence { Time = position.Time,
                        Position = calibrated ? alignment.ToPlan(position.Position) : position.Position, Calibrated = calibrated });
                }
                Vector2? arrival = null;
                if (row.ResolveObserved && calibrated && timeline.PositionAt(actor.Id, resolve) is { } last) arrival = alignment.ToPlan(last.Position);
                // Locally recorded valid frames already carry an established per-slide projection.
                if (geometryMatches && row.ResolveObserved && !arrival.HasValue && frame != null && actorRow.SlotIndex >= 0 &&
                    SameSeat(plan, attempt.Plan, actorRow.SlotIndex))
                {
                    var players = frame.Players.Where(p => p.SlotIndex == actorRow.SlotIndex).ToArray();
                    if (players.Length == 1)
                    {
                        arrival = players[0].Board;
                        if (actorRow.Positions.Count == StrategyEvidenceValidation.MaxPositions) actorRow.Positions.RemoveAt(0);
                        actorRow.Positions.Add(new StrategyPositionEvidence { Time = frame.Time, Position = arrival.Value, Calibrated = true });
                    }
                }
                var destinations = slide?.Items.Where(i => i.Kind == CanvasItemKind.PlayerToken && actorRow.SlotIndex >= 0 &&
                    i.SlotIndex == actorRow.SlotIndex).ToArray() ?? Array.Empty<CanvasItem>();
                if (arrival.HasValue && destinations.Length == 1 && float.IsFinite(destinations[0].Position.X) && float.IsFinite(destinations[0].Position.Y))
                {
                    var distance = Vector2.Distance(arrival.Value, destinations[0].Position);
                    if (float.IsFinite(distance)) actorRow.DestinationDistance = distance;
                }
                row.Actors.Add(actorRow);
                var hasGap = evidence.Statuses.Any(s => s.ActorId == actor.Id && s.Change == "unavailable" &&
                    s.Time >= mechanic.Time && s.Time <= resolve);
                if (entry == null || mechanic.ActionId == 0 || mechanic.Occurrence <= 0 || !evidence.Complete ||
                    attempt.TerritoryId == 0 || hasGap || fresh.Length is < 1 or > 4 ||
                    fresh.Select(s => s.StatusId).Distinct().Count() != fresh.Length ||
                    actorRow.DestinationDistance is not <= .03f || plan.AdaptiveMechanics.Count >= 128 ||
                    attempt.Plan.FindSlide(row.SlideId) == null) continue;
                var draft = EvidenceRules.Draft(attempt, mechanic, fresh, row.SlideId, attempt.TerritoryId);
                draft.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}:{mechanic.ActionId}:{mechanic.Occurrence}:{actorRow.SlotIndex}")))[..32].ToLowerInvariant();
                // Keep deliberate edits to earlier drafts and never enable an observed rule.
                if (!draft.IsValid(plan) || plan.AdaptiveMechanics.Any(r => r.Id == draft.Id) || EvidenceRules.ContainsDraft(plan, draft)) continue;
                plan.AdaptiveMechanics.Add(draft); drafts++; changed = true;
            }
            attachment.Mechanics.Add(row);
        }
        if (previous == null || JsonConvert.SerializeObject(previous) != JsonConvert.SerializeObject(attachment))
        {
            plan.StrategyEvidence ??= new();
            if (previous != null) plan.StrategyEvidence[plan.StrategyEvidence.IndexOf(previous)] = attachment;
            else plan.StrategyEvidence.Add(attachment);
            if (plan.StrategyEvidence.Count > StrategyEvidenceValidation.MaxAttachments)
                plan.StrategyEvidence.RemoveRange(0, plan.StrategyEvidence.Count - StrategyEvidenceValidation.MaxAttachments);
            changed = true;
        }
        var summary = $"{matched} mechanics linked; {unassigned} unassigned; {drafts} disabled assignment drafts added.";
        if (!attachment.EncounterVerified) summary += " Encounter identity is unverified.";
        if (unchangedGeometry.Any(p => p.Key.Length > 0 && !p.Value)) summary += " Edited board geometry needs a new calibrated recording before destination comparison.";
        if (attachment.Mechanics.Any(m => !m.ResolveObserved)) summary += " Some expected cast endings are outside this recording; arrival is unobserved.";
        if (!aligned && !attempt.Frames.Any(f => f.Valid)) summary += " Position alignment is unresolved.";
        if (attachment.OmittedMechanics > 0) summary += $" {attachment.OmittedMechanics} further casts omitted from the compact summary.";
        return new(true, changed, matched, unassigned, drafts, summary);
    }

    static StrategyEnrichmentResult Reject(string summary) => new(false, false, 0, 0, 0, summary);
    static string Normalise(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    static (TimelineEntry? Entry, string Reason) Match(PlanDocument plan, ReplayMechanic mechanic)
    {
        var byAction = mechanic.ActionId == 0 ? Array.Empty<TimelineEntry>() : plan.Timeline.Where(e => e.CastActionId != e.InferredCastActionId && e.CastActionId == mechanic.ActionId &&
            e.Occurrence == mechanic.Occurrence).ToArray();
        if (byAction.Length == 0 && mechanic.ActionId != 0)
            byAction = plan.Timeline.Where(e => e.CastActionId != e.InferredCastActionId && e.CastActionId == mechanic.ActionId && e.Occurrence <= 0).ToArray();
        if (byAction.Length > 0) return byAction.Length == 1 ? (byAction[0], "action") : (null, "ambiguous");
        var name = Normalise(mechanic.Label);
        if (name.Length == 0) return (null, "unmatched");
        bool SameName(string value) => Normalise(value) == name;
        var byName = plan.Timeline.Where(e => (e.CastActionId == 0 || e.CastActionId == e.InferredCastActionId) && (e.Occurrence <= 0 || e.Occurrence == mechanic.Occurrence) &&
            (SameName(e.CastName) || SameName(e.Label) || SameName(plan.FindSlide(e.SlideId)?.Title ?? ""))).ToArray();
        if (byName.Any(e => e.CastActionId != 0 && e.CastActionId != mechanic.ActionId)) return (null, "ambiguous");
        if (byName.Length == 0 && (plan.Slides.Count(s => SameName(s.Title) || SameName(s.SourceLabel)) > 1 ||
            PhaseGroups(plan, name).Length > 1))
            return (null, "ambiguous");
        return byName.Length == 1 ? (byName[0], "name") : (null, byName.Length > 1 ? "ambiguous" : "unmatched");
    }

    static bool InferTimeline(PlanDocument plan, IReadOnlyList<ReplayMechanic> mechanics)
    {
        var changed = false;
        var inferred = plan.Timeline.Count(e => e.EvidenceCreated);
        foreach (var mechanic in mechanics)
        {
            if (inferred >= StrategyEvidenceValidation.MaxMechanics || plan.Timeline.Count >= 4096) break;
            if (mechanic.ActionId == 0 || mechanic.Occurrence <= 0 || Match(plan, mechanic).Reason != "unmatched") continue;
            var name = Normalise(mechanic.Label);
            if (name.Length == 0 || mechanics.Any(m => Normalise(m.Label) == name && m.ActionId != mechanic.ActionId)) continue;
            // An explicit row may deliberately reject this action for the same named occurrence.
            // Do not bypass that authored association by manufacturing a second row.
            if (plan.Timeline.Any(e => (e.Occurrence <= 0 || e.Occurrence == mechanic.Occurrence) &&
                (e.CastActionId == mechanic.ActionId || Normalise(e.Label) == name || Normalise(e.CastName) == name))) continue;
            var slides = plan.Slides.Where(s => Normalise(s.Title) == name || Normalise(s.SourceLabel) == name).ToArray();
            // A phase name may explain several authored boards. Attach its cast timing without
            // implying which board, or which player destination, was active at the cast.
            if (slides.Length != 1 && PhaseGroups(plan, name).Length != 1) continue;
            var slideId = slides.Length == 1 ? slides[0].Id : "";
            if (plan.Timeline.Any(e => e.SlideId == slideId && e.Occurrence == mechanic.Occurrence &&
                (slideId.Length > 0 || Normalise(e.Label) == name))) continue;
            plan.Timeline.Add(new TimelineEntry { Label = mechanic.Label.Length > 160 ? mechanic.Label[..160] : mechanic.Label,
                SlideId = slideId, Occurrence = mechanic.Occurrence, Trigger = TriggerKind.BossCast, Enabled = false,
                CastActionId = mechanic.ActionId, InferredCastActionId = mechanic.ActionId, EvidenceCreated = true,
                SortTime = mechanic.Time });
            inferred++;
            changed = true;
        }
        return changed;
    }

    static string[] PhaseGroups(PlanDocument plan, string name)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slide in plan.Slides)
        {
            if (!Uri.TryCreate(string.IsNullOrEmpty(slide.GuideUrl) ? slide.SourceUrl : slide.GuideUrl, UriKind.Absolute, out var source) || source.Scheme != "https" ||
                source.Host != "wtfdig.info" || !source.IsDefaultPort || source.UserInfo.Length > 0) continue;
            foreach (var label in new[] { slide.Title, slide.SourceLabel })
            {
                var split = label.IndexOf(" / ", StringComparison.Ordinal);
                if (split > 0 && Normalise(label[..split]) == name) groups.Add(source.AbsoluteUri + "\n" + name);
            }
        }
        return groups.ToArray();
    }

    internal static bool SameGeometry(PlanDocument current, PlanDocument snapshot, string slideId)
    {
        var currentSlide = current.FindSlide(slideId);
        var recordedSlide = snapshot.FindSlide(slideId);
        if (currentSlide == null || recordedSlide == null) return false;
        string Fingerprint(PlanDocument plan, Slide slide)
        {
            var arena = slide.ArenaOverride ?? plan.Arena;
            // Use the recording's persisted precision and per-item serialization rules (which
            // omit IDs and inapplicable geometry fields). Slide titles and notes are excluded.
            return JsonConvert.SerializeObject(new { arena.Shape, arena.AspectRatio,
                slide.Items }, PlanJson.Compact());
        }
        return Fingerprint(current, currentSlide) == Fingerprint(snapshot, recordedSlide);
    }

    internal static bool SameSeat(PlanDocument plan, PlanDocument snapshot, int slot) => plan.Id == snapshot.Id && slot >= 0 &&
        slot < plan.Roster.Count && slot < snapshot.Roster.Count && plan.Roster[slot].Name == snapshot.Roster[slot].Name &&
        plan.Roster[slot].JobId == snapshot.Roster[slot].JobId && plan.Roster[slot].Placeholder == snapshot.Roster[slot].Placeholder;

    static int MatchActor(PlanDocument plan, ReplayAttempt attempt, EvidenceActor actor)
    {
        if (actor.SlotIndex >= 0 && SameSeat(plan, attempt.Plan, actor.SlotIndex)) return actor.SlotIndex;
        var names = plan.Roster.Select((seat, index) => (seat, index)).Where(p => !string.IsNullOrWhiteSpace(actor.Name) &&
            string.Equals(p.seat.Name, actor.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (names.Length > 0) return names.Length == 1 ? names[0].index : -1;
        var jobs = plan.Roster.Select((seat, index) => (seat, index)).Where(p => actor.JobId != 0 && p.seat.JobId == actor.JobId).ToArray();
        return jobs.Length == 1 ? jobs[0].index : -1;
    }
}
