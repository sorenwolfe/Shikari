using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.Live;

namespace Shikari.Services.Replay;

public sealed record PositionCheckOptions(string SlideId, float Time, float Radius = .03f,
    bool CheckpointReviewed = false, bool AlignmentReviewed = false, int EffectIndex = -1);

public enum PositionCheckOutcome { Unknown, Near, Away }

/// <summary>Observed proximity to one authored token, never a mechanic success or safe-route verdict.</summary>
public sealed class PositionCheckResult
{
    public PositionCheckOutcome Outcome { get; internal set; }
    public string Reason { get; internal set; } = "";
    public List<string> Notes { get; } = new();
    public float CheckTime { get; internal set; }
    public float? SampleTime { get; internal set; }
    public float? SampleAge { get; internal set; }
    public float? NextSampleTime { get; internal set; }
    public Vector2? ExpectedPosition { get; internal set; }
    public Vector2? ObservedPosition { get; internal set; }
    public Vector2? SourcePosition { get; internal set; }
    public float? Distance { get; internal set; }
    /// <summary>Largest landmark residual, an empirical diagnostic rather than statistical confidence.</summary>
    public float? AlignmentError { get; internal set; }
    public string SampleSource { get; internal set; } = "";
}

/// <summary>Pure evidence comparison over detached inputs. Never reads or changes live services.</summary>
public static class PositionCheck
{
    public const float MaximumSampleAge = .25f;

    public static PositionCheckResult Evaluate(PullValidationResult assignments, AdaptiveDecision selectedDecision,
        ReplayAttempt recording, PositionCheckOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new PositionCheckResult { CheckTime = options?.Time ?? 0 };
        if (assignments == null || selectedDecision == null || recording == null || options == null)
            return Unknown(result, "A validation result, selected decision, recording and checkpoint are required.");
        if (!float.IsFinite(options.Radius) || options.Radius <= 0 || options.Radius > .5f || options.EffectIndex < -1)
            return Unknown(result, "The comparison radius or event selection is invalid.");
        if (!assignments.ScopeVerified || assignments.TerritoryId == 0 ||
            (assignments.HasOccurrenceReadiness ? !assignments.EvidenceUsable : !assignments.Complete))
            return Unknown(result, "Assignment evidence is incomplete or its encounter scope is unverified.");
        if (string.IsNullOrEmpty(recording.Id) || assignments.AttemptId != recording.Id ||
            !ValidTime(recording.Duration, ReplayBuffer.MaxDuration) || assignments.Duration != recording.Duration ||
            recording.TerritoryId != 0 && recording.TerritoryId != assignments.TerritoryId)
            return Unknown(result, "The validation and position recording do not identify the same bounded pull.");
        var evidence = recording.Evidence;
        if (evidence == null || !evidence.Complete || evidence.Source is not ("FF Logs" or "Local recording") ||
            evidence.Actors == null || evidence.Actors.Count > 32 || evidence.Actors.Any(a => a == null) ||
            evidence.Positions == null || evidence.Positions.Count > ReplayEvidence.MaxPositions ||
            evidence.Statuses == null || evidence.Statuses.Count > ReplayEvidence.MaxStatuses ||
            evidence.Effects == null || evidence.Effects.Count > ReplayEvidence.MaxEffects ||
            recording.Frames == null || recording.Frames.Count > ReplayBuffer.MaxFrames ||
            recording.Casts == null || recording.Casts.Count > ReplayBuffer.MaxCasts)
            return Unknown(result, "The source evidence is incomplete, unavailable or outside recording limits.");
        if (assignments.Decisions.Count > PullValidationRunner.MaxDecisions || assignments.Decisions.Any(d => d == null) ||
            assignments.Occurrences.Count > ReplayBuffer.MaxCasts || assignments.Occurrences.Any(o => o == null) ||
            assignments.Decisions.Count(d => ReferenceEquals(d, selectedDecision)) != 1 ||
            assignments.Decisions.Count(d => SameDecisionKey(d, selectedDecision)) != 1 ||
            selectedDecision.Conflict || selectedDecision.BranchIndex < 0 ||
            !ValidTime(selectedDecision.Time, recording.Duration) || selectedDecision.Occurrence <= 0)
            return Unknown(result, "Select one unique, resolved assignment from this validation result.");
        var plan = assignments.Plan;
        if (!AvailablePlan(plan, token) || !AvailablePlan(recording.Plan, token) || string.IsNullOrEmpty(plan.Id) || plan.Id != recording.Plan.Id)
            return Unknown(result, "The tested plan and recorded plan are unavailable or incompatible.");
        var rules = plan.AdaptiveMechanics.Where(r => r.Id == selectedDecision.RuleId).ToArray();
        if (rules.Length != 1 || !ValidRule(rules[0], plan) || !rules[0].Enabled ||
            rules[0].TerritoryId != assignments.TerritoryId || rules[0].AnchorActionId != selectedDecision.AnchorActionId ||
            rules[0].Occurrence != 0 && rules[0].Occurrence != selectedDecision.Occurrence ||
            selectedDecision.BranchIndex >= rules[0].Branches.Count ||
            rules[0].Branches[selectedDecision.BranchIndex].SlideId != selectedDecision.SlideId)
            return Unknown(result, "The selected decision no longer identifies a valid tested assignment branch.");
        if (string.IsNullOrEmpty(options.SlideId) || options.SlideId != selectedDecision.SlideId ||
            plan.Slides.Count(s => s.Id == options.SlideId) != 1 || recording.Plan.Slides.Count(s => s.Id == options.SlideId) != 1)
            return Unknown(result, "This check requires the exact board assigned by the selected decision.");
        if (assignments.HasOccurrenceReadiness)
        {
            var arms = assignments.Occurrences.Where(o => o.RuleId == selectedDecision.RuleId &&
                o.AnchorActionId == selectedDecision.AnchorActionId && o.Occurrence == selectedDecision.Occurrence).ToArray();
            if (arms.Length != 1 || !arms[0].Complete || arms[0].Reasons.Count != 0 ||
                !ValidTime(arms[0].StartTime, recording.Duration) || !ValidTime(arms[0].EndTime, recording.Duration) ||
                !float.IsFinite(arms[0].Deadline) || arms[0].Deadline < arms[0].StartTime ||
                selectedDecision.Time < arms[0].StartTime || selectedDecision.Time > arms[0].EndTime)
                return Unknown(result, "The selected assignment window does not have complete, unique evidence.");
        }
        if (assignments.ActorId <= 0 || evidence.Actors.Count(a => a.Id == assignments.ActorId) != 1)
            return Unknown(result, "The selected player's recorded identity is missing or ambiguous.");
        var actor = evidence.Actors.Single(a => a.Id == assignments.ActorId);
        if (actor.SlotIndex < 0 || actor.SlotIndex >= plan.Roster.Count ||
            evidence.Actors.Count(a => a.SlotIndex == actor.SlotIndex) != 1 ||
            !JToken.DeepEquals(JToken.FromObject(plan.Roster), JToken.FromObject(recording.Plan.Roster)))
            return Unknown(result, "The selected player does not have one unchanged, uniquely mapped roster seat.");
        var board = plan.Slides.Single(s => s.Id == options.SlideId);
        if (!SameGeometry(plan, recording.Plan, board))
            return Unknown(result, "The recorded board geometry changed; its positions cannot be checked against this board.");
        var destinations = board.Items.Where(i => i.Kind == CanvasItemKind.PlayerToken && i.SlotIndex == actor.SlotIndex).ToArray();
        if (destinations.Length != 1 || !BoardPoint(destinations[0].Position))
            return Unknown(result, "The assigned board needs exactly one authored destination token for this player.");
        result.ExpectedPosition = destinations[0].Position;
        EvidenceEffect? effect = null;
        if (options.EffectIndex >= 0)
        {
            if (evidence.Source != "FF Logs" || !evidence.EffectsComplete || options.EffectIndex >= evidence.Effects.Count ||
                evidence.Effects.Any(e => e == null))
                return Unknown(result, "A complete FF Logs effect stream and a valid exact event selection are required.");
            effect = evidence.Effects[options.EffectIndex];
            result.CheckTime = effect.Time;
            if (effect.Type != "calculateddamage" || effect.TargetId != assignments.ActorId || effect.ActionId == 0 ||
                effect.SourceId <= 0 || effect.SourceInstance is <= 0 || effect.TargetInstance is <= 0 || effect.PacketId is < 0 ||
                !ValidTime(effect.Time, recording.Duration) || effect.TargetPosition is not { } position || !Finite(position))
                return Unknown(result, "Select this player's calculated-damage event with an explicit target position; later damage events are not snapshots.");
            if (evidence.Effects.Count(e => SameEffectKey(e, effect)) != 1)
                return Unknown(result, "This exact effect identity is duplicated; its recorded position is ambiguous.");
            if (effect.PacketId.HasValue && evidence.Effects.Any(other =>
                other.PacketId == effect.PacketId && other.Type == effect.Type && other.ActionId == effect.ActionId &&
                other.TargetId == effect.TargetId && other.TargetInstance == effect.TargetInstance &&
                (other.SourceId != effect.SourceId || other.SourceInstance != effect.SourceInstance ||
                 other.Time != effect.Time || other.SourcePosition != effect.SourcePosition || other.TargetPosition != effect.TargetPosition)))
                return Unknown(result, "The same typed effect packet and target contain conflicting observations; its snapshot cannot be established.");
        }
        if (!ValidTime(result.CheckTime, recording.Duration) || result.CheckTime < selectedDecision.Time)
            return Unknown(result, "The checkpoint must be within the pull and after the selected assignment became available.");
        if (!options.CheckpointReviewed)
            return Unknown(result, "Review which mechanic checkpoint and authored destination this observation should represent.");
        if (Superseded(assignments, selectedDecision, recording, rules[0], result.CheckTime, token))
            return Unknown(result, "A later assignment or cast arm supersedes this decision before the selected checkpoint.");
        if (!options.AlignmentReviewed)
            return Unknown(result, "Review the position alignment before comparing it with an authored destination.");

        float margin;
        if (evidence.Source == "Local recording")
        {
            result.SampleSource = "Recorded local board frame";
            var reason = LocalSample(recording, actor, options.SlideId, selectedDecision.Time, result, token);
            if (reason != null) return Unknown(result, reason);
            // Older frame records have only a valid flag and scale, not their landmark residual.
            // Do not report a fabricated fit quality or silently treat that fit as exact.
            margin = WorldAlignment.MaxResidual;
            result.Notes.Add("Per-frame alignment error was not recorded. A 0.02 board-unit comparison margin is applied; it is not a measured error bound.");
        }
        else
        {
            result.SampleSource = effect == null ? "FF Logs position sample" : "Exact FF Logs calculated-damage target position";
            if (effect != null)
            {
                result.SampleTime = effect.Time; result.SampleAge = 0; result.SourcePosition = effect.TargetPosition;
                result.Notes.Add("This is the selected calculated event's target position, not a later damage event or an interpolated replay point.");
            }
            else
            {
                var reason = SourceSample(evidence.Positions, assignments.ActorId, selectedDecision.Time, recording.Duration, result, token);
                if (reason != null) return Unknown(result, reason);
            }
            if (evidence.CalibrationSlideId != options.SlideId || !TryAlignment(evidence.References, out var alignment, out margin))
                return Unknown(result, "This board needs 3–8 distinct, well-spread, reviewed landmarks with a trustworthy position fit.");
            result.AlignmentError = margin;
            result.ObservedPosition = alignment.ToPlan(result.SourcePosition!.Value);
            result.Notes.Add("Alignment error is the largest observed landmark residual, not statistical confidence or a guaranteed bound.");
            if (!WithinLandmarkReach(evidence.References, result.ExpectedPosition.Value, result.ObservedPosition.Value))
                return Unknown(result, "The destination or observation is far beyond the landmark coverage. Add reviewed landmarks across this part of the board.");
        }
        token.ThrowIfCancellationRequested();
        if (result.ObservedPosition is not { } observed || !Finite(observed))
            return Unknown(result, "The recorded observation could not be mapped to a finite board position.");
        var distance = Vector2.Distance(observed, result.ExpectedPosition!.Value);
        if (!float.IsFinite(distance)) return Unknown(result, "The position distance is invalid.");
        result.Distance = distance;
        result.Notes.Add("This compares observed proximity only. It does not establish mechanic success, a safe location or a movement route.");
        // Exact equality is intentionally inconclusive, including with an apparently perfect fit.
        if (MathF.Abs(distance - options.Radius) <= margin + .0001f)
            return Unknown(result, "The observation is too close to the chosen radius to distinguish near from away with this alignment.");
        result.Outcome = distance < options.Radius ? PositionCheckOutcome.Near : PositionCheckOutcome.Away;
        result.Reason = result.Outcome == PositionCheckOutcome.Near
            ? "The recorded observation is near the authored destination at this reviewed checkpoint."
            : "The recorded observation is away from the authored destination at this reviewed checkpoint.";
        return result;
    }

    private static bool Superseded(PullValidationResult result, AdaptiveDecision decision, ReplayAttempt recording,
        AdaptiveMechanic rule, float time, CancellationToken token)
    {
        foreach (var other in result.Decisions)
        {
            if (!ValidTime(other.Time, recording.Duration)) return true;
            if (ReferenceEquals(other, decision) || other.Time < decision.Time || other.Time > time) continue;
            if (other.RuleId == decision.RuleId) return true;
            if (other.Conflict || other.BranchIndex < 0 || string.IsNullOrEmpty(other.SlideId))
            {
                // No order can choose between an assignment and conflicting simultaneous output.
                if (other.Time == decision.Time) return true;
                continue;
            }
            // Different rules can agree on the same board. A later or simultaneous destination
            // on another board makes this earlier assignment inappropriate for the checkpoint.
            if (other.SlideId != decision.SlideId) return true;
        }
        if (result.Occurrences.Any(o => o.RuleId == decision.RuleId &&
            (!ValidTime(o.StartTime, recording.Duration) || o.Occurrence != decision.Occurrence &&
                o.StartTime >= decision.Time && o.StartTime <= time))) return true;
        for (var i = 0; i < recording.Casts.Count; i++)
        {
            if ((i & 127) == 0) token.ThrowIfCancellationRequested();
            var cast = recording.Casts[i];
            if (cast == null) return true;
            if (cast.ActionId != decision.AnchorActionId || cast.Occurrence == decision.Occurrence ||
                rule.Occurrence != 0 && cast.Occurrence != rule.Occurrence) continue;
            var available = cast.ObservedTime ?? cast.StartTime;
            if (available == null || !ValidTime(available.Value, recording.Duration)) return true;
            if (available > decision.Time && available <= time) return true;
        }
        return false;
    }

    private static string? SourceSample(List<EvidencePosition> positions, long actor, float decisionTime,
        float duration, PositionCheckResult result, CancellationToken token)
    {
        EvidencePosition? latest = null; var duplicates = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            if ((i & 127) == 0) token.ThrowIfCancellationRequested();
            var point = positions[i];
            if (point == null) return "The position stream contains an unidentified sample.";
            if (point.ActorId != actor) continue;
            if (!ValidTime(point.Time, duration)) return "A selected-player position has no valid time; ordering is unknown.";
            if (point.Time > result.CheckTime)
            {
                result.NextSampleTime = result.NextSampleTime is { } next ? MathF.Min(next, point.Time) : point.Time;
                continue;
            }
            if (latest == null || point.Time > latest.Time) { latest = point; duplicates = 1; }
            else if (point.Time == latest.Time) duplicates++;
        }
        if (latest == null) return "There is no recorded position at or before this checkpoint.";
        result.SampleTime = latest.Time; result.SampleAge = result.CheckTime - latest.Time;
        if (duplicates != 1) return "Multiple player positions share the latest sample time; the observation is ambiguous.";
        if (!Finite(latest.Position)) return "The latest selected-player sample is invalid; an earlier sample cannot replace it.";
        result.SourcePosition = latest.Position;
        if (result.SampleAge > MaximumSampleAge || latest.Time < decisionTime)
            return "The prior position is stale or predates the assignment; a future sample cannot fill this gap.";
        return null;
    }

    private static string? LocalSample(ReplayAttempt recording, EvidenceActor actor, string slide, float decisionTime,
        PositionCheckResult result, CancellationToken token)
    {
        ReplayFrame? latest = null; var duplicates = 0;
        for (var i = 0; i < recording.Frames.Count; i++)
        {
            if ((i & 127) == 0) token.ThrowIfCancellationRequested();
            var frame = recording.Frames[i];
            if (frame == null || !ValidTime(frame.Time, recording.Duration)) return "A local frame has no valid time; ordering is unknown.";
            if (frame.Time > result.CheckTime)
            {
                result.NextSampleTime = result.NextSampleTime is { } next ? MathF.Min(next, frame.Time) : frame.Time;
                continue;
            }
            if (latest == null || frame.Time > latest.Time) { latest = frame; duplicates = 1; }
            else if (frame.Time == latest.Time) duplicates++;
        }
        if (latest == null) return "There is no recorded local frame at or before this checkpoint.";
        result.SampleTime = latest.Time; result.SampleAge = result.CheckTime - latest.Time;
        if (duplicates != 1 || !latest.Valid || !float.IsFinite(latest.BoardPerYalm) || latest.BoardPerYalm <= 0 ||
            latest.SlideId != slide || latest.Players == null || latest.Players.Count > 8 || latest.Players.Any(p => p == null))
            return "The latest local frame is invalid, ambiguous or aligned to another board; an older frame cannot replace it.";
        // Plan-seat mappings can be edited after recording. They select the authored token,
        // but cannot identify a historical actor. Legacy frames retain name, job and IsLocal;
        // all three must agree uniquely before their captured position can be reused.
        if (string.IsNullOrWhiteSpace(actor.Name) || actor.JobId == 0 ||
            recording.Evidence.Actors.Count(a => string.Equals(a.Name, actor.Name, StringComparison.OrdinalIgnoreCase)) != 1)
            return "The selected player has no unique captured identity for this legacy local frame.";
        var players = latest.Players.Where(p => string.Equals(p.Name, actor.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (players.Length != 1 || players[0].JobId != actor.JobId || players[0].IsLocal != actor.IsLocal || !Finite(players[0].Board))
            return "The selected player's captured name, job and local-player identity do not uniquely match the latest frame.";
        result.ObservedPosition = players[0].Board;
        if (result.SampleAge > MaximumSampleAge || latest.Time < decisionTime)
            return "The prior local frame is stale or predates the assignment; a future frame cannot fill this gap.";
        return null;
    }

    private static bool TryAlignment(List<EvidenceReference> references, out WorldAlignment alignment, out float margin)
    {
        alignment = default; margin = 0;
        if (references == null || references.Count is < 3 or > 8 || references.Any(r => r == null || !Finite(r.Source) || !BoardPoint(r.Board)) ||
            !Spread(references.Select(r => r.Source).ToArray(), false) || !Spread(references.Select(r => r.Board).ToArray(), true)) return false;
        var pairs = references.Select(r => new AlignmentPair(r.Source, r.Board)).ToArray();
        if (!WorldAlignment.TrySolve(pairs, out alignment) || !alignment.IsTrustworthy || !float.IsFinite(alignment.Scale) ||
            alignment.Scale <= 0 || !float.IsFinite(alignment.Residual)) return false;
        foreach (var pair in pairs) margin = MathF.Max(margin, Vector2.Distance(alignment.ToPlan(pair.World), pair.Plan));
        return float.IsFinite(margin) && margin <= WorldAlignment.MaxResidual;
    }

    private static bool Spread(Vector2[] points, bool board)
    {
        var maxDistanceSquared = 0f; var area = 0f;
        for (var i = 0; i < points.Length; i++)
        for (var j = i + 1; j < points.Length; j++)
        {
            var distance = Vector2.DistanceSquared(points[i], points[j]);
            if (!float.IsFinite(distance) || distance <= .00000001f) return false;
            maxDistanceSquared = MathF.Max(maxDistanceSquared, distance);
            for (var k = j + 1; k < points.Length; k++)
            {
                var a = points[j] - points[i]; var b = points[k] - points[i];
                area = MathF.Max(area, MathF.Abs(a.X * b.Y - a.Y * b.X));
            }
        }
        // Shape alone is scale-invariant: a tiny, perfect triangle can still amplify a small
        // placement error across the arena. Board references must span 20% of its width and
        // contain a triangle covering at least 1% of its area.
        return maxDistanceSquared > .000001f && float.IsFinite(area) && area / maxDistanceSquared >= .05f &&
            (!board || maxDistanceSquared >= .04f && area >= .02f);
    }

    private static bool WithinLandmarkReach(List<EvidenceReference> references, Vector2 expected, Vector2 observed)
    {
        if (!Finite(expected) || !Finite(observed)) return false;
        var spanSquared = 0f;
        foreach (var first in references)
            foreach (var second in references)
                spanSquared = MathF.Max(spanSquared, Vector2.DistanceSquared(first.Board, second.Board));
        // Allow nearby extrapolation, but do not transfer a small local fit residual across a
        // distant uncalibrated region. This is a coverage guard, not an estimated error bound.
        return references.Any(r => Vector2.DistanceSquared(r.Board, expected) <= spanSquared) &&
            references.Any(r => Vector2.DistanceSquared(r.Board, observed) <= spanSquared);
    }

    private static bool SameGeometry(PlanDocument current, PlanDocument recorded, Slide currentBoard)
    {
        var recordedBoard = recorded.Slides.Single(s => s.Id == currentBoard.Id);
        object Geometry(PlanDocument plan, Slide board)
        {
            var arena = board.ArenaOverride ?? plan.Arena;
            return new { arena.Shape, arena.AspectRatio, board.Items };
        }
        return JToken.DeepEquals(JToken.FromObject(Geometry(current, currentBoard)), JToken.FromObject(Geometry(recorded, recordedBoard)));
    }

    private static bool AvailablePlan(PlanDocument? plan, CancellationToken token)
    {
        if (plan?.Arena == null || !ArenaAvailable(plan.Arena) || plan.Roster is not { Count: <= 32 } ||
            plan.Roster.Any(r => r == null) || plan.Slides is not { Count: <= 4096 } ||
            plan.AdaptiveMechanics is not { Count: <= 128 } || plan.AdaptiveMechanics.Any(r => r == null)) return false;
        var items = 0; var points = 0;
        foreach (var board in plan.Slides)
        {
            token.ThrowIfCancellationRequested();
            if (board == null || board.ArenaOverride != null && !ArenaAvailable(board.ArenaOverride) ||
                board.Items is not { Count: <= 4096 } || (items += board.Items.Count) > 65536) return false;
            foreach (var item in board.Items)
                if (item == null || item.Points is not { Count: <= 16384 } || (points += item.Points.Count) > 262144) return false;
        }
        return true;
    }

    private static bool ArenaAvailable(ArenaSettings arena) => Enum.IsDefined(arena.Shape) &&
        float.IsFinite(arena.AspectRatio) && arena.AspectRatio > 0;

    private static bool ValidRule(AdaptiveMechanic rule, PlanDocument plan) => rule.Branches is { Count: > 0 and <= 16 } &&
        rule.Branches.All(b => b != null && b.AdditionalStatuses is { Count: <= 3 } && b.AdditionalStatuses.All(s => s != null)) && rule.IsValid(plan);
    private static bool SameDecisionKey(AdaptiveDecision a, AdaptiveDecision b) => a.RuleId == b.RuleId && a.AnchorActionId == b.AnchorActionId && a.Occurrence == b.Occurrence;
    private static bool SameEffectKey(EvidenceEffect a, EvidenceEffect b) => a.Type == b.Type && a.Time == b.Time && a.ActionId == b.ActionId &&
        a.SourceId == b.SourceId && a.TargetId == b.TargetId && a.SourceInstance == b.SourceInstance && a.TargetInstance == b.TargetInstance && a.PacketId == b.PacketId;
    private static bool ValidTime(float time, float maximum) => float.IsFinite(time) && time >= 0 && time <= maximum;
    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
    private static bool BoardPoint(Vector2 point) => Finite(point) && point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1;
    private static PositionCheckResult Unknown(PositionCheckResult result, string reason)
    { result.Outcome = PositionCheckOutcome.Unknown; result.Reason = reason; return result; }
}
