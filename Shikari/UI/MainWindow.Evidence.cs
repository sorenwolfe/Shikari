using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.FfLogs;
using Shikari.Services.Replay;
using Shikari.UI.Theme;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private EvidenceTimeline? evidenceTimeline;
    private readonly Dictionary<string, EvidenceTimeline> evidenceTimelines = new();
    private string evidenceAttempt = "";
    private long evidenceActor;
    private readonly HashSet<uint> evidenceSelection = new();
    private string evidenceSlide = "";
    private int evidenceTerritory;
    private string evidenceMessage = "";
    private AdaptiveMechanic? evidenceDraft;
    private float evidenceDraftAt = -1;
    private readonly Dictionary<uint, string> evidenceStatusNames = new();
    private Vector2 referenceSource;
    private Vector2 referenceBoard = new(.5f, .5f);

    private void LoadLogReview(PlanDocument plan, LogFightData data)
    {
        var session = new StrategyMergeSession(plan);
        Run(async cancel =>
        {
            var evidence = await Plugin.FfLogs.GetEvidenceAsync(Plugin.Config.FfLogsClientId,
                Plugin.Config.FfLogsClientSecret, data.ReportCode, data.Fight, cancel);
            return () =>
            {
                ApplyLogReference(session, data, evidence);
            };
        });
        importStatusLine = "Reading this pull's statuses and movement…";
    }

    private void ApplyLogReference(StrategyMergeSession session, LogFightData data, LogEvidence evidence)
    {
        if (Plan == null || !session.Matches(Plan))
            throw new InvalidOperationException("The strategy changed while the log was loading. Import again against the current plan.");
        var statuses = Plugin.DataManager.GetExcelSheet<Status>();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var attempt = LogReplayBuilder.Build(session.Snapshot, data, evidence,
            id => statuses.GetRowOrDefault(id) is { } row && !string.IsNullOrEmpty(row.Name.ToString()),
            name => jobs.FirstOrDefault(j => LogImporter.SameJob(name, j.Name.ToString(), j.Abbreviation.ToString())).RowId);
        // Reattaching a pull updates its reference and retains calibration against unchanged geometry.
        var previous = Plugin.Replays.Attempts.FirstOrDefault(a => a.Plan.Id == Plan.Id &&
            a.Evidence.Source == "FF Logs" && a.Evidence.ReportCode == data.ReportCode && a.Evidence.FightId == data.Fight.Id);
        if (previous != null)
        {
            attempt.Id = previous.Id;
            if (Newtonsoft.Json.JsonConvert.SerializeObject(previous.Plan.Roster) ==
                Newtonsoft.Json.JsonConvert.SerializeObject(attempt.Plan.Roster))
            {
                foreach (var actor in attempt.Evidence.Actors)
                {
                    var old = previous.Evidence.Actors.FirstOrDefault(a => a.Id == actor.Id && a.JobId == actor.JobId);
                    if (old != null) actor.SlotIndex = old.SlotIndex;
                }
                foreach (var duplicate in attempt.Evidence.Actors.Where(a => a.SlotIndex >= 0).GroupBy(a => a.SlotIndex).Where(g => g.Count() > 1))
                    foreach (var actor in duplicate) actor.SlotIndex = -1;
            }
            var calibratedSlide = previous.Evidence.CalibrationSlideId;
            if (calibratedSlide.Length > 0 && Newtonsoft.Json.JsonConvert.SerializeObject(previous.Plan.FindSlide(calibratedSlide)) ==
                Newtonsoft.Json.JsonConvert.SerializeObject(attempt.Plan.FindSlide(calibratedSlide)))
            {
                attempt.Evidence.CalibrationSlideId = calibratedSlide;
                attempt.Evidence.References = previous.Evidence.References;
            }
        }
        var result = session.Apply(Plan, attempt, Plugin.Plans.SaveActive);
        if (!result.Accepted) { Fail(result.Summary); return; }
        StrategyMergeSession.LinkReplay(Plan, attempt);
        Plugin.Replays.AddImported(attempt);
        evidenceTimelines.Remove(attempt.Id); evidenceTimeline = null;
        SelectReviewAttempt(attempt);
        if (result.Changed) MarkDirty();
        importStatusLine = result.Summary;
        importDetail = string.Join("\n", evidence.Warnings);
        importFailed = false;
    }

    private EvidenceTimeline TimelineFor(ReplayAttempt attempt)
    {
        if (evidenceAttempt != attempt.Id || evidenceTimeline == null)
        {
            evidenceAttempt = attempt.Id;
            evidenceTimeline = CachedEvidenceTimeline(attempt);
            evidenceActor = attempt.Evidence.Actors.FirstOrDefault(a => a.IsLocal)?.Id ?? attempt.Evidence.Actors.FirstOrDefault()?.Id ?? 0;
            evidenceSelection.Clear();
            evidenceDraft = null;
            evidenceSlide = "";
            evidenceTerritory = (int)attempt.TerritoryId;
            evidenceMessage = "";
        }
        return evidenceTimeline;
    }

    private EvidenceTimeline CachedEvidenceTimeline(ReplayAttempt attempt)
    {
        if (evidenceTimelines.TryGetValue(attempt.Id, out var timeline)) return timeline;
        if (evidenceTimelines.Count >= 30) evidenceTimelines.Clear();
        return evidenceTimelines[attempt.Id] = new EvidenceTimeline(attempt.Evidence);
    }

    private string EvidenceStatusName(EvidenceStatus status)
    {
        if (!string.IsNullOrEmpty(status.Name)) return status.Name;
        if (status.StatusId == 0) return "Unverified status / log ability " + status.AbilityId;
        if (!evidenceStatusNames.TryGetValue(status.StatusId, out var name))
        {
            name = Plugin.DataManager.GetExcelSheet<Status>().GetRowOrDefault(status.StatusId)?.Name.ToString() ?? "";
            evidenceStatusNames[status.StatusId] = name;
        }
        return string.IsNullOrEmpty(name) ? "Status #" + status.StatusId : name;
    }

    private void DrawEvidencePanel(ReplayAttempt attempt)
    {
        ImGui.BeginDisabled(Plan?.Id != attempt.Plan.Id || Plugin.Encounter.InCombat);
        if (ImGui.Button("Update strategy from this pull")) SaveEvidenceEdits(attempt);
        ImGui.EndDisabled();
        if (Plan?.Id == attempt.Plan.Id) DrawStrategyEvidence(Plan);
        var timeline = TimelineFor(attempt);
        if (evidenceDraft != null && evidenceDraftAt != reviewTime) evidenceDraft = null;
        var evidence = attempt.Evidence;
        if (evidence.Actors.Count == 0) return;
        if (!ImGui.CollapsingHeader($"Statuses & strategy / {evidence.Source}##evidence")) return;
        if (!evidence.Complete) ImGui.TextColored(Palette.Vec(Palette.Attention), "Incomplete reference — rule drafting is unavailable.");
        ImGui.SetNextItemWidth(260 * UiHelpers.Scale);
        if (ImGui.BeginCombo("Player##evidence", evidence.Actors.FirstOrDefault(a => a.Id == evidenceActor)?.Name ?? "Choose player"))
        {
            foreach (var actor in evidence.Actors)
                if (ImGui.Selectable(actor.Name + " / " + actor.Job + "##actor-" + actor.Id, actor.Id == evidenceActor))
                { evidenceActor = actor.Id; reviewSeat = actor.SlotIndex; evidenceSelection.Clear(); evidenceDraft = null; }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Next status change"))
        {
            var next = evidence.Statuses.FirstOrDefault(s => s.ActorId == evidenceActor && s.Time > reviewTime + .01f);
            if (next != null) { reviewTime = next.Time; reviewPlaying = false; }
        }
        var actorEntry = evidence.Actors.FirstOrDefault(a => a.Id == evidenceActor);
        if (actorEntry != null)
        {
            ImGui.SetNextItemWidth(210 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Plan seat", actorEntry.SlotIndex >= 0 ? attempt.Plan.Roster[actorEntry.SlotIndex].DisplayName : "Unassigned"))
            {
                for (var i = -1; i < attempt.Plan.Roster.Count; i++)
                    if (ImGui.Selectable(i < 0 ? "Unassigned" : attempt.Plan.Roster[i].DisplayName, actorEntry.SlotIndex == i))
                    {
                        foreach (var other in evidence.Actors.Where(a => a != actorEntry && a.SlotIndex == i)) other.SlotIndex = -1;
                        actorEntry.SlotIndex = i; reviewSeat = i; SaveEvidenceEdits(attempt);
                    }
                ImGui.EndCombo();
            }
        }
        var active = timeline.StatusesAt(evidenceActor, reviewTime);
        ImGui.TextDisabled($"{active.Count} active statuses at {reviewTime:0.1}s. Select up to four assignment statuses.");
        if (ImGui.BeginChild("##evidence-status-list", new Vector2(0, 125 * UiHelpers.Scale), true, ImGuiWindowFlags.None))
        {
            foreach (var status in active)
            {
                ImGui.PushID(status.StatusId + "/" + status.AbilityId + "/" + status.SourceId);
                var selected = evidenceSelection.Contains(status.StatusId);
                ImGui.BeginDisabled(status.StatusId == 0 || status.Baseline || !selected && evidenceSelection.Count >= 4);
                if (ImGui.Checkbox(EvidenceStatusName(status), ref selected))
                { if (selected) evidenceSelection.Add(status.StatusId); else evidenceSelection.Remove(status.StatusId); evidenceDraft = null; }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled((status.Duration.HasValue ? $"{Math.Max(0, status.Time + status.Duration.Value - reviewTime):0.0}s" : "duration unknown") +
                    (status.Parameter.HasValue ? $" / parameter {status.Parameter}" : " / parameter unknown") + (status.Baseline ? " / baseline" : ""));
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        var anchor = SelectedReviewMechanic(attempt);
        if (anchor != null)
        {
            ImGui.TextWrapped($"Cast anchor: {anchor.Label} / #{anchor.ActionId}, occurrence {anchor.Occurrence}");
            ImGui.SetNextItemWidth(300 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Strategy slide", attempt.Plan.FindSlide(evidenceSlide)?.Title ?? "Choose from the imported plan"))
            {
                foreach (var candidate in attempt.Plan.Slides)
                    if (ImGui.Selectable(candidate.Title + "##evidence-" + candidate.Id, candidate.Id == evidenceSlide))
                    { evidenceSlide = candidate.Id; evidenceDraft = null; }
                ImGui.EndCombo();
            }
            var slide = attempt.Plan.FindSlide(evidenceSlide);
            if (slide != null && ImGui.TreeNode("Read strategy and source notes"))
            { ImGui.TextWrapped(slide.Notes); ImGui.TextWrapped(attempt.Plan.Notes); ImGui.TreePop(); }
            ImGui.SetNextItemWidth(130 * UiHelpers.Scale);
            if (ImGui.InputInt("Duty territory ID", ref evidenceTerritory)) { evidenceTerritory = Math.Max(0, evidenceTerritory); evidenceDraft = null; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Use current duty")) { evidenceTerritory = (int)Plugin.ClientState.TerritoryType; evidenceDraft = null; }
            ImGui.BeginDisabled(Plugin.Encounter.InCombat || slide == null);
            if (ImGui.Button("Test selected assignment"))
            {
                try
                {
                    evidenceDraft = EvidenceRules.Draft(attempt, anchor, active.Where(s => evidenceSelection.Contains(s.StatusId)).ToArray(), evidenceSlide, (uint)evidenceTerritory);
                    evidenceDraft.Branches[0].Label = string.Join(" + ", active.Where(s => evidenceSelection.Contains(s.StatusId)).Select(EvidenceStatusName));
                    evidenceDraftAt = reviewTime;
                    reviewPlaying = false;
                    var results = EvidenceRules.Simulate(attempt, evidenceActor, evidenceDraft);
                    evidenceMessage = results.Count == 0 ? "No completed decision in this recording window." :
                        string.Join("\n", results.Select(d => $"{d.Time:0.0}s: " + d.Reason));
                }
                catch (Exception ex) { evidenceDraft = null; evidenceMessage = ex.Message; }
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(evidenceDraft == null || Plan?.Id != attempt.Plan.Id);
            if (ImGui.Button("Add disabled rule to plan")) AddEvidenceDraft(attempt);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(Plan?.Id != attempt.Plan.Id || anchor.ActionId == 0);
            if (ImGui.Button("Link cast to this slide")) LinkEvidenceCast(attempt, anchor);
            ImGui.EndDisabled();
            ImGui.EndDisabled();
            ImGui.TextWrapped("Drafts use observed durations with a two-second margin when known. Verify duration ranges and alternate assignments in Adaptive before enabling.");
        }
        if (evidenceMessage.Length > 0) ImGui.TextWrapped(evidenceMessage);
        if (evidence.Warnings.Count > 0 && ImGui.TreeNode("Source coverage"))
        { foreach (var warning in evidence.Warnings) ImGui.TextWrapped(warning); ImGui.TreePop(); }
    }

    private void AddEvidenceDraft(ReplayAttempt attempt)
    {
        if (evidenceDraft == null || Plan?.Id != attempt.Plan.Id || Plugin.Encounter.InCombat) return;
        var plan = Plan;
        if (plan.AdaptiveMechanics.Count >= 128 || !evidenceDraft.IsValid(plan)) { evidenceMessage = "The plan no longer supports this draft."; return; }
        if (EvidenceRules.ContainsDraft(plan, evidenceDraft))
        { evidenceDraft = null; evidenceMessage = "These exact conditions and destinations are already in the plan. Check their recording coverage in Adaptive."; return; }
        var existing = plan.AdaptiveMechanics.FirstOrDefault(r => !r.Enabled && r.TerritoryId == evidenceDraft.TerritoryId &&
            r.AnchorActionId == evidenceDraft.AnchorActionId && r.Occurrence == evidenceDraft.Occurrence &&
            r.WindowSeconds == evidenceDraft.WindowSeconds && r.Branches.Count < 16);
        if (existing == null) plan.AdaptiveMechanics.Add(evidenceDraft);
        else
        {
            existing.Branches.AddRange(evidenceDraft.Branches.Where(branch => !existing.Branches.Any(candidate => EvidenceRules.SameBranch(candidate, branch))));
        }
        evidenceDraft = null;
        MarkDirty();
        evidenceMessage = "Disabled assignment added. Open Plan → Adaptive to review it and add the other outcomes.";
    }

    private void LinkEvidenceCast(ReplayAttempt attempt, ReplayMechanic anchor)
    {
        if (Plan?.Id != attempt.Plan.Id || Plugin.Encounter.InCombat || Plan.FindSlide(evidenceSlide) == null) return;
        var existing = Plan.Timeline.Where(e => e.CastActionId == anchor.ActionId && e.Occurrence == anchor.Occurrence).ToArray();
        if (existing.Length > 1) { evidenceMessage = "Several timeline steps match. Choose the link in Timeline."; return; }
        var entry = existing.SingleOrDefault();
        if (entry == null)
        {
            entry = new TimelineEntry { Label = anchor.Label, CastActionId = anchor.ActionId, CastName = anchor.Label,
                Occurrence = anchor.Occurrence, SortTime = anchor.Time, Enabled = false };
            Plan.Timeline.Add(entry);
        }
        entry.SlideId = evidenceSlide;
        entry.Enabled = false;
        anchor.SlideId = evidenceSlide;
        MarkDirty();
        SaveEvidenceEdits(attempt);
        evidenceMessage = "Cast linked to the chosen slide. Review its timeline settings before enabling.";
    }

    private void SaveEvidenceEdits(ReplayAttempt attempt)
    {
        // Metadata has already changed in memory, even if validation or disk save later fails.
        InvalidateAssignmentCoverage();
        try
        {
            if (Plan?.Id == attempt.Plan.Id && !Plugin.Encounter.InCombat)
            {
                var result = new StrategyMergeSession(Plan).Apply(Plan, attempt, Plugin.Plans.SaveActive);
                evidenceMessage = result.Summary;
                if (result.Changed) MarkDirty();
                StrategyMergeSession.LinkReplay(Plan, attempt);
            }
            Plugin.Replays.SaveEvidence(attempt);
        }
        catch (Exception ex) { evidenceMessage = "Could not save replay changes: " + ex.Message; }
    }
}
