using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Replay;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private sealed record CoverageCache(PlanDocument Plan, object Slides, object Roster, object References,
        string Rule, long Revision, AdaptiveCoverage Result);
    private sealed record CoverageRun(string RuleId, CoverageCache Stamp, IEnumerator<AdaptiveCoverage> Steps)
    {
        public AdaptiveCoverage Report { get; set; } = new();
    }
    private readonly Dictionary<string, CoverageCache> assignmentCoverage = new();
    private CoverageRun? assignmentCheck;
    private void CancelAssignmentCheck() { assignmentCheck?.Steps.Dispose(); assignmentCheck = null; }
    private void InvalidateAssignmentCoverage() { CancelAssignmentCheck(); assignmentCoverage.Clear(); }
    private static bool CoverageInputsMatch(CoverageCache cached, PlanDocument plan, string rule, long revision) =>
        ReferenceEquals(cached.Plan, plan) && ReferenceEquals(cached.Slides, plan.Slides) &&
        ReferenceEquals(cached.Roster, plan.Roster) && ReferenceEquals(cached.References, plan.StrategyEvidence) &&
        cached.Rule == rule && cached.Revision == revision;

    private void AdvanceAssignmentCheck(PlanDocument? plan)
    {
        var job = assignmentCheck;
        if (job == null) return;
        var rule = plan?.AdaptiveMechanics.FirstOrDefault(r => r.Id == job.RuleId);
        if (Plugin.Encounter.InCombat || plan == null || rule == null ||
            !CoverageInputsMatch(job.Stamp, plan, JsonConvert.SerializeObject(rule), Plugin.Replays.EvidenceRevision))
        { CancelAssignmentCheck(); return; }
        // Scheduling budget, not a hard frame-time promise: validating or indexing one
        // recording may exceed it. Yielding per actor/cast avoids a whole-corpus draw stall.
        var clock = Stopwatch.StartNew();
        try
        {
            for (var step = 0; step < 16 && clock.Elapsed.TotalMilliseconds < 8; step++)
            {
                if (!job.Steps.MoveNext())
                {
                    assignmentCoverage[job.RuleId] = job.Stamp with { Result = job.Report };
                    CancelAssignmentCheck();
                    return;
                }
                job.Report = job.Steps.Current;
            }
        }
        catch (Exception ex)
        {
            assignmentCoverage[job.RuleId] = job.Stamp with {
                Result = new AdaptiveCoverage { Message = "Recording analysis could not finish: " + ex.Message } };
            CancelAssignmentCheck();
        }
    }

    private void DrawAssignmentCoverage(PlanDocument plan, AdaptiveMechanic rule)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("ASSIGNMENT COVERAGE");
        var fingerprint = JsonConvert.SerializeObject(rule);
        assignmentCoverage.TryGetValue(rule.Id, out var cached);
        if (cached != null && !CoverageInputsMatch(cached, plan, fingerprint, Plugin.Replays.EvidenceRevision))
        { assignmentCoverage.Remove(rule.Id); cached = null; }
        if (assignmentCheck?.RuleId == rule.Id)
        {
            ImGui.TextWrapped($"Checking recordings… {assignmentCheck.Report.Recordings} recordings / {assignmentCheck.Report.Examples.Count} examples processed");
            if (ImGui.SmallButton("Cancel check")) CancelAssignmentCheck();
        }
        ImGui.BeginDisabled(Plugin.Encounter.InCombat || !rule.IsValid(plan));
        if (ImGui.Button("Check recordings"))
        {
            CancelAssignmentCheck();
            assignmentCoverage.Remove(rule.Id); cached = null;
            var stamp = new CoverageCache(plan, plan.Slides, plan.Roster, plan.StrategyEvidence, fingerprint,
                Plugin.Replays.EvidenceRevision, new AdaptiveCoverage());
            assignmentCheck = new(rule.Id, stamp, AdaptiveEvidenceAudit.AnalyseIncrementally(plan, rule, Plugin.Replays.Attempts.ToArray()).GetEnumerator());
        }
        ImGui.EndDisabled();
        if (cached == null)
        {
            ImGui.TextWrapped("Check this mechanic against your retained local recordings and FF Logs references. Conditions, missing assignments and conflicts are tested using the live evaluator.");
            return;
        }
        var result = cached.Result;
        ImGui.TextWrapped($"{result.Recordings} recordings checked · {result.Duplicates} duplicate references ignored");
        ImGui.TextWrapped("Counts describe recordings, not independent pulls: a local recording and FF Logs may capture the same pull. Reproducing a condition does not verify the strategy or prove a successful mechanic.");
        foreach (var branch in result.Branches)
            ImGui.TextWrapped((string.IsNullOrWhiteSpace(branch.Label) ? "Branch " + (branch.Index + 1) : branch.Label) +
                $": {branch.Recordings} encounter-matched recordings / {branch.Examples} actor examples");
        var missing = result.Branches.Where(b => b.Recordings == 0).Select(b => string.IsNullOrWhiteSpace(b.Label) ? "Branch " + (b.Index + 1) : b.Label).ToArray();
        if (missing.Length > 0) ImGui.TextWrapped("Still needs an encounter-matched example: " + string.Join(", ", missing));
        var conflicts = result.Examples.Count(e => e.Outcome == AssignmentOutcome.Conflict);
        var unknown = result.Examples.Count(e => e.Outcome == AssignmentOutcome.Unknown);
        var unverified = result.Examples.Where(e => !e.ScopeVerified).Select(e => e.SourceKey).Distinct().Count();
        ImGui.TextWrapped($"{conflicts} conflicting actor examples · {unknown} unknown examples · {unverified} recordings with unverified encounter identity");
        if (result.Message.Length > 0) ImGui.TextWrapped(result.Message);
        foreach (var exclusion in result.Exclusions) ImGui.TextDisabled($"Excluded {exclusion.Value}: {exclusion.Key}");
        if (!ImGui.TreeNode("Review examples##assignment-coverage-examples")) return;
        if (ImGui.BeginChild("##assignment-coverage-rows", new Vector2(0, 230 * UiHelpers.Scale), true, ImGuiWindowFlags.None))
        {
            var index = 0;
            foreach (var example in result.Examples)
            {
                ImGui.PushID((index++).ToString());
                var source = example.SourceKey.StartsWith("fflogs:", StringComparison.Ordinal)
                    ? "FF Logs " + example.SourceKey[7..] : "Local recording";
                var seat = example.SlotIndex >= 0 && example.SlotIndex < plan.Roster.Count
                    ? plan.Roster[example.SlotIndex].DisplayName : "Unassigned actor";
                ImGui.TextWrapped($"{source} · {seat} · use {example.Occurrence} · {example.Time:0.0}s");
                ImGui.TextWrapped(example.Reason + (example.ScopeVerified ? "" : " — encounter identity unverified"));
                if (example.Distance.HasValue)
                    ImGui.TextWrapped($"Observed distance to the authored spot: {example.Distance.Value * 100:0.0}% of board width. {example.PositionReason}. This is proximity, not a success check.");
                else ImGui.TextDisabled(example.PositionReason);
                ImGui.BeginDisabled(Plugin.Encounter.InCombat || !Plugin.Replays.Attempts.Any(a => a.Id == example.AttemptId));
                if (ImGui.SmallButton("Review this example")) OpenAssignmentExample(rule, example);
                ImGui.EndDisabled();
                ImGui.Separator();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.TreePop();
    }

    private void OpenAssignmentExample(AdaptiveMechanic rule, AssignmentExample example)
    {
        if (Plugin.Encounter.InCombat) return;
        var attempt = Plugin.Replays.Attempts.FirstOrDefault(a => a.Id == example.AttemptId);
        if (attempt == null) return;
        SelectReviewAttempt(attempt);
        TimelineFor(attempt);
        evidenceActor = example.ActorId;
        reviewSeat = attempt.Evidence.Actors.FirstOrDefault(a => a.Id == example.ActorId)?.SlotIndex ?? -1;
        reviewMechanicIndex = Math.Max(0, attempt.Mechanics.FindIndex(m => m.ActionId == rule.AnchorActionId && m.Occurrence == example.Occurrence));
        reviewTime = Math.Clamp(example.Time, 0, attempt.Duration);
        reviewPlaying = false;
        evidenceSelection.Clear(); evidenceDraft = null;
        evidenceSlide = example.BranchIndex >= 0 && example.BranchIndex < rule.Branches.Count ? rule.Branches[example.BranchIndex].SlideId : "";
        workspace = 2;
        IsOpen = true;
    }
}
