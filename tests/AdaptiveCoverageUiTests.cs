using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;
using Shikari.Services.Replay;
using Shikari.UI;

namespace Shikari.Tests
{
    public static class AdaptiveCoverageUiTests
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Run()
        {
            var plan = PlanDocument.CreateDefault();
            var rule = new AdaptiveMechanic { Label = "Assignments", TerritoryId = 100, AnchorActionId = 800, WindowSeconds = 5 };
            rule.Branches.Add(new StatusBranch { StatusId = 10, MaximumSeconds = 60, SlideId = plan.Slides[0].Id, Label = "Spread" });
            plan.AdaptiveMechanics.Add(rule);
            var attempt = new ReplayBuffer(plan, 0, DateTime.UtcNow).Attempt;
            attempt.Duration = 10; attempt.TerritoryId = 100;
            attempt.Mechanics.Add(new ReplayMechanic { ActionId = 800, Occurrence = 1, Time = 1, ExpectedResolve = 4 });
            attempt.Evidence.Actors.Add(new EvidenceActor { Id = 42, SlotIndex = 0 });
            attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 42, StatusId = 10, Time = 2, Duration = 30 });
            Plugin.Replays.Attempts.Add(attempt);
            var window = new MainWindow();
            window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "Drawing Adaptive must not scan all recordings every frame.");
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "Starting a check should schedule bounded work, not scan the retained corpus inside the button handler.");
            for (var frame = 0; frame < 100 && window.CachedCount == 0; frame++) window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 1, "An explicit check should eventually cache its complete analysis.");
            Plugin.Replays.EvidenceRevision++;
            window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "Updated or deleted recording evidence must invalidate cached coverage.");
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            window.EditPlan();
            Check(window.CachedCount == 0, "An authored board or seat edit must invalidate cached coverage.");
            window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "Editing a plan must also cancel an unfinished check.");
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            Plugin.Encounter.InCombat = true;
            window.DrawCoverage(plan, rule);
            Plugin.Encounter.InCombat = false;
            window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "A check interrupted by combat must not silently resume with stale data.");
            Plugin.Encounter.InCombat = true;
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            Check(window.CachedCount == 0, "Coverage analysis cannot start during combat.");
            Plugin.Encounter.InCombat = false;
            for (var i = 0; i < 29; i++) Plugin.Replays.Attempts.Add(attempt);
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            window.DrawCoverage(plan, rule);
            Check(window.CheckRunning && window.CachedCount == 0, "Retained-corpus work should yield across frames before publishing its report.");
            rule.WindowSeconds = 6;
            window.DrawCoverage(plan, rule);
            Check(!window.CheckRunning && window.CachedCount == 0, "Editing the rule during analysis must cancel its old interpretation.");
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            window.DrawCoverage(plan, rule);
            Plugin.Replays.EvidenceRevision++;
            attempt.Evidence.Statuses.Clear();
            window.DrawCoverage(plan, rule);
            Check(!window.CheckRunning && window.CachedCount == 0, "An in-place replay edit plus its revision must cancel unfinished enumeration before it can publish mixed data.");
            attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 42, StatusId = 10, Time = 2, Duration = 30 });
            Dalamud.Bindings.ImGui.ImGui.Press = "Check recordings"; window.DrawCoverage(plan, rule);
            var replacement = new PlanDocument { Id = plan.Id, Slides = plan.Slides, Roster = plan.Roster,
                AdaptiveMechanics = plan.AdaptiveMechanics };
            window.DrawCoverage(replacement, rule);
            Check(!window.CheckRunning && window.CachedCount == 0, "A replacement plan with the same ID cannot inherit a running check.");
            Plugin.Replays.Attempts.RemoveRange(1, 29);
            var example = AdaptiveEvidenceAudit.Analyse(plan, rule, Plugin.Replays.Attempts).Examples.Single();
            window.OpenExample(rule, example);
            Check(window.SelectedAttempt == attempt.Id && window.SelectedActor == 42 && window.SelectedWorkspace == 2 &&
                window.SelectedTime == example.Time && window.SelectedSeat == 0,
                "A coverage example must open the exact recording, source actor, mechanic time and plan seat in Review.");
            Console.WriteLine("Adaptive coverage UI lifecycle checks passed.");
        }
    }
}
namespace Shikari
{
    internal static class Plugin { public static FakeReplays Replays { get; } = new(); public static FakeEncounter Encounter { get; } = new(); }
    internal sealed class FakeReplays { public List<ReplayAttempt> Attempts { get; } = new(); public long EvidenceRevision { get; set; } }
    internal sealed class FakeEncounter { public bool InCombat; }
}
namespace Shikari.UI
{
    public sealed partial class MainWindow
    {
        private int workspace, reviewSeat, reviewMechanicIndex;
        private long evidenceActor;
        private float reviewTime;
        private bool reviewPlaying;
        private string selectedAttempt = "", evidenceSlide = "";
        private AdaptiveMechanic? evidenceDraft;
        private readonly HashSet<uint> evidenceSelection = new();
        public bool IsOpen { get; set; }
        private void SelectReviewAttempt(ReplayAttempt attempt) { selectedAttempt = attempt.Id; reviewTime = 0; }
        private EvidenceTimeline TimelineFor(ReplayAttempt attempt) => new(attempt.Evidence);
        public void DrawCoverage(PlanDocument plan, AdaptiveMechanic rule) { AdvanceAssignmentCheck(plan); DrawAssignmentCoverage(plan, rule); }
        public void EditPlan() => InvalidateAssignmentCoverage();
        public void OpenExample(AdaptiveMechanic rule, AssignmentExample example) => OpenAssignmentExample(rule, example);
        public int CachedCount => assignmentCoverage.Count;
        public bool CheckRunning => assignmentCheck != null;
        public string SelectedAttempt => selectedAttempt;
        public long SelectedActor => evidenceActor;
        public int SelectedWorkspace => workspace;
        public float SelectedTime => reviewTime;
        public int SelectedSeat => reviewSeat;
    }
    internal static class UiHelpers { public static float Scale => 1; }
}
namespace Dalamud.Bindings.ImGui
{
    public enum ImGuiWindowFlags { None }
    public static class ImGui
    {
        public static string Press = "";
        private static readonly Stack<bool> disabled = new();
        public static void BeginDisabled(bool value = true) => disabled.Push(value);
        public static void EndDisabled() => disabled.Pop();
        public static bool Button(string label) { var match = label == Press; if (match) Press = ""; return match && !disabled.Any(v => v); }
        public static bool SmallButton(string label) => Button(label);
        public static void TextWrapped(string text) { } public static void TextDisabled(string text) { } public static void TextUnformatted(string text) { }
        public static void Separator() { } public static void SameLine() { } public static void Spacing() { }
        public static bool BeginChild(string id, Vector2 size, bool border, ImGuiWindowFlags flags) => true;
        public static void EndChild() { } public static void PushID(string value) { } public static void PopID() { }
        public static bool TreeNode(string text) => false; public static void TreePop() { }
    }
}
