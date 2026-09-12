using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private int generateMinPulls = 3;
    private float generateMinConfidence = 0.45f;
    private bool generateOnlyLongCasts = true;
    private string learnStatus = string.Empty;

    private void DrawLearnedTab(PlanDocument plan)
    {
        var learner = Plugin.Learner;
        var memory = learner.Current;

        if (learner.IsLoading)
        {
            ImGui.TextWrapped("Loading learned timings… Learning resumes with the next pull.");
            if (learner.StorageError is { Length: > 0 } loadingError) ImGui.TextWrapped(loadingError);
            return;
        }

        if (learner.StorageError is { Length: > 0 } error)
        {
            ImGui.TextWrapped(error);
            ImGui.BeginDisabled(Plugin.Encounter.InCombat || learner.IsSaving);
            if (ImGui.Button("Retry saving learned timings")) learner.SaveAll();
            ImGui.EndDisabled();
        }
        else if (learner.IsSaving) ImGui.TextDisabled("Saving learned timings…");

        ImGui.TextWrapped(
            "Shikari watches your pulls and remembers when each cast happens. Once it has seen a " +
            "fight a few times it can call a mechanic before the boss starts casting it — which is " +
            "the only way to get more warning than a cast bar is long.");

        ImGui.Spacing();

        var learning = Plugin.Config.LearningEnabled;
        if (ImGui.Checkbox("Learn from my pulls", ref learning))
        {
            Plugin.Config.LearningEnabled = learning;
            Plugin.SaveConfig();
        }

        ImGui.SameLine();
        UiHelpers.HelpMarker(
            "Timings are stored per zone in the plugin's own folder and never leave your machine.\n\n" +
            "Pulls shorter than 15 seconds, or with fewer than three casts, are ignored — that " +
            "keeps striking dummies and instant wipes out of the data.");

        ImGui.Separator();

        if (memory == null || memory.Casts.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Nothing learned about {EncounterLearner.DescribeTerritory(Plugin.ClientState.TerritoryType)} yet.");
            ImGui.Spacing();
            ImGui.TextWrapped("Do a pull with learning on and this fills in. One pull is enough to be useful; three or four makes the timings trustworthy.");

            if (learner.PullSoFar.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Recording now: {learner.PullSoFar.Count} casts this pull.");
            }

            DrawOtherFights();
            return;
        }

        DrawMemoryHeader(memory, learner);
        ImGui.Separator();
        DrawGenerator(plan, memory);
        ImGui.Separator();
        DrawLearnedTable(plan, memory, learner);
        DrawOtherFights();
    }

    private void DrawMemoryHeader(FightMemory memory, EncounterLearner learner)
    {
        ImGui.TextUnformatted(memory.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(
            $"— {memory.PullCount} pull{(memory.PullCount == 1 ? "" : "s")}, " +
            $"{memory.ClearCount} clear{(memory.ClearCount == 1 ? "" : "s")}, " +
            $"{memory.Casts.Count} timings, longest {CallTemplate.FormatTime(memory.LongestPullSeconds)}");

        if (Plugin.Encounter.InCombat)
        {
            ImGui.Spacing();
            if (learner.DriftConfirmed)
            {
                var drift = learner.Drift;
                var word = drift >= 0 ? "behind" : "ahead of";
                ImGui.TextColored(
                    UiHelpers.Pack(new Vector4(0.5f, 0.9f, 0.5f, 1f)),
                    $"This pull is running {MathF.Abs(drift):0.0}s {word} the usual, measured at \"{learner.DriftAnchor}\". Predictions are shifted to match.");
            }
            else
            {
                ImGui.TextDisabled("Waiting for a cast it recognises before it can tell how this pull is running.");
            }

            var upcoming = learner.Upcoming(Plugin.Encounter.CombatElapsed, 4);
            if (upcoming.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Expected next:");
                var line = string.Join("   ·   ", upcoming.Select(u =>
                    $"{u.Cast.Name} in {MathF.Max(0f, u.ExpectedTime - Plugin.Encounter.CombatElapsed):0}s"));
                ImGui.TextWrapped(line);
            }
        }
    }

    private void DrawGenerator(PlanDocument plan, FightMemory memory)
    {
        ImGui.TextDisabled("Build a timeline from what it knows");
        ImGui.Separator();

        ImGui.SetNextItemWidth(140 * UiHelpers.Scale);
        ImGui.SliderInt("Seen in at least", ref generateMinPulls, 1, 10, "%d pulls", ImGuiSliderFlags.None);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * UiHelpers.Scale);
        ImGui.SliderFloat("Confidence", ref generateMinConfidence, 0f, 1f, "%.2f", ImGuiSliderFlags.None);

        ImGui.SameLine();
        ImGui.Checkbox("Only casts with a bar", ref generateOnlyLongCasts);
        if (ImGui.IsItemHovered())
        {
            UiHelpers.Tooltip(
                "Skips instant casts, which are usually auto-attacks and filler rather than " +
                "mechanics worth a slide.");
        }

        var candidates = SelectCandidates(memory);

        if (ImGui.Button($"Add {candidates.Count} step(s) to this plan", Vector2.Zero))
            GenerateSteps(plan, candidates);

        ImGui.SameLine();
        if (ImGui.Button("Add steps and a slide each", Vector2.Zero))
            GenerateSteps(plan, candidates, withSlides: true);

        ImGui.SameLine();
        UiHelpers.HelpMarker(
            "Generated steps use the Predicted trigger, so they warn ahead of the cast once the " +
            "timing is trusted and fall back to the cast bar when it is not.\n\n" +
            "Casts already on your timeline are skipped, so this is safe to run again after more " +
            "pulls have taught it more.");

        if (!string.IsNullOrEmpty(learnStatus))
        {
            ImGui.TextColored(UiHelpers.Pack(new Vector4(0.5f, 0.9f, 0.5f, 1f)), learnStatus);
        }
    }

    private List<LearnedCast> SelectCandidates(FightMemory memory)
    {
        return memory.InOrder()
            .Where(c => c.PullsSeen >= generateMinPulls)
            .Where(c => c.Confidence >= generateMinConfidence)
            .Where(c => !generateOnlyLongCasts || c.CastBarSeconds >= 0.5f)
            .ToList();
    }

    private void GenerateSteps(PlanDocument plan, List<LearnedCast> candidates, bool withSlides = false)
    {
        var added = 0;
        var skipped = 0;

        foreach (var cast in candidates)
        {
            var alreadyThere = plan.Timeline.Any(e =>
                e.CastActionId == cast.ActionId &&
                (e.Occurrence == cast.Occurrence || e.Occurrence <= 0));

            if (alreadyThere)
            {
                skipped++;
                continue;
            }

            var slideId = string.Empty;
            if (withSlides)
            {
                var slide = new Slide { Title = OccurrenceTitle(cast) };
                plan.Slides.Add(slide);
                slideId = slide.Id;
            }

            plan.Timeline.Add(new TimelineEntry
            {
                Label = OccurrenceTitle(cast),
                Trigger = TriggerKind.Predicted,
                CastActionId = cast.ActionId,
                CastName = cast.Name,
                Occurrence = cast.Occurrence,
                SortTime = cast.Median,
                SlideId = slideId,

                // A cast with a long bar needs less extra warning than an instant one; either way
                // keep it inside a range a person can actually react to.
                LeadSeconds = Math.Clamp(MathF.Max(cast.CastBarSeconds, 3f), 3f, 8f),
            });

            added++;
        }

        MarkDirty();
        learnStatus = skipped > 0
            ? $"Added {added} step(s); skipped {skipped} already on the timeline."
            : $"Added {added} step(s).";
    }

    private static string OccurrenceTitle(LearnedCast cast)
    {
        var name = string.IsNullOrWhiteSpace(cast.Name) ? "Action #" + cast.ActionId : cast.Name;
        return cast.Occurrence <= 1 ? name : $"{name} {cast.Occurrence}";
    }

    private void DrawLearnedTable(PlanDocument plan, FightMemory memory, EncounterLearner learner)
    {
        ImGui.TextDisabled("What it knows");
        ImGui.Separator();

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("##learned", 7, flags, new Vector2(0, 300 * UiHelpers.Scale), 0f))
        {
            ImGui.TableSetupColumn("Usually at", ImGuiTableColumnFlags.WidthFixed, 80 * UiHelpers.Scale, 0);
            ImGui.TableSetupColumn("Cast", ImGuiTableColumnFlags.WidthStretch, 0f, 0);
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 45 * UiHelpers.Scale, 0);
            ImGui.TableSetupColumn("Wander", ImGuiTableColumnFlags.WidthFixed, 70 * UiHelpers.Scale, 0);
            ImGui.TableSetupColumn("Pulls", ImGuiTableColumnFlags.WidthFixed, 50 * UiHelpers.Scale, 0);
            ImGui.TableSetupColumn("Trust", ImGuiTableColumnFlags.WidthFixed, 90 * UiHelpers.Scale, 0);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90 * UiHelpers.Scale, 0);
            ImGui.TableHeadersRow();

            foreach (var cast in memory.InOrder().ToList())
            {
                ImGui.TableNextRow();
                ImGui.PushID($"learn{cast.ActionId}-{cast.Occurrence}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(CallTemplate.FormatTime(cast.Median));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(cast.Name) ? "Action #" + cast.ActionId : cast.Name);
                if (ImGui.IsItemHovered())
                {
                    var samples = string.Join(", ", cast.Samples.Select(s => CallTemplate.FormatTime(s)));
                    UiHelpers.Tooltip(
                        $"Action #{cast.ActionId}\n" +
                        $"Cast bar about {cast.CastBarSeconds:0.0}s\n" +
                        $"Seen at: {samples}");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(cast.Occurrence.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"±{cast.Deviation:0.0}s");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(cast.PullsSeen.ToString());

                ImGui.TableNextColumn();
                var confidence = cast.Confidence;
                var colour = confidence switch
                {
                    >= 0.75f => new Vector4(0.5f, 0.9f, 0.5f, 1f),
                    >= 0.45f => new Vector4(0.9f, 0.85f, 0.45f, 1f),
                    _ => new Vector4(0.85f, 0.55f, 0.45f, 1f),
                };
                ImGui.TextColored(UiHelpers.Pack(colour), cast.ConfidenceLabel);

                ImGui.TableNextColumn();
                var onTimeline = plan.Timeline.Any(e => e.CastActionId == cast.ActionId && e.Occurrence == cast.Occurrence);
                if (onTimeline)
                {
                    ImGui.TextDisabled("on plan");
                }
                else if (ImGui.SmallButton("Add step"))
                {
                    GenerateSteps(plan, new List<LearnedCast> { cast });
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (ImGui.Button("Forget these timings", Vector2.Zero))
        {
            learner.ForgetTimings(memory);
            learnStatus = "Cleared the learned timings for this fight.";
        }

        ImGui.SameLine();
        UiHelpers.HelpMarker(
            "Worth doing after a patch retunes an encounter, or if a run of odd pulls has skewed " +
            "the numbers. The fight stays in the list and starts learning again from the next pull.");
    }

    private void DrawOtherFights()
    {
        var others = Plugin.Learner.All.Where(m => m.TerritoryId != Plugin.ClientState.TerritoryType).ToList();
        if (others.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Other fights it remembers");

        foreach (var memory in others)
        {
            ImGui.PushID("mem" + memory.TerritoryId);
            ImGui.TextUnformatted(memory.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"— {memory.PullCount} pulls, {memory.Casts.Count} timings, last seen {memory.LastSeenUtc.ToLocalTime():d}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Forget"))
                Plugin.Learner.Forget(memory);
            ImGui.PopID();
        }
    }
}
