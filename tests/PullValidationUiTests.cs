using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Replay;
using Dalamud.Bindings.ImGui;
namespace Dalamud.Bindings.ImGui
{
    public enum ImGuiWindowFlags { None }
    public static class ImGui
    {
        private static int disabled;
        private static readonly Stack<bool> disabledScopes = new();
        public static readonly HashSet<string> Clicks = new();
        public static readonly List<string> Text = new();
        public static bool CollapsingHeader(string s) => true;
        public static bool TreeNode(string s) => true;
        public static void TreePop() { }
        public static Vector2 GetContentRegionAvail() => new(600,600);
        public static bool BeginChild(string s, Vector2 size, bool border, ImGuiWindowFlags flags) => true;
        public static void EndChild() { }
        public static void SetNextItemWidth(float v) { }
        public static bool BeginCombo(string label, string preview) => false;
        public static void EndCombo() { }
        public static bool Selectable(string s, bool selected = false) => Button(s);
        public static bool Checkbox(string s, ref bool value) { if (!Button(s)) return false; value = !value; return true; }
        public static bool InputInt(string s, ref int v) => false;
        public static bool SliderFloat(string s, ref float v, float min, float max, string format) => false;
        public static void SameLine() { }
        public static void Separator() { }
        public static void TextWrapped(string s) => Text.Add(s);
        public static void TextUnformatted(string s) => Text.Add(s);
        public static void TextDisabled(string s) => Text.Add(s);
        public static void TextColored(Vector4 c, string s) => Text.Add(s);
        public static bool Button(string s) => disabled == 0 && Clicks.Remove(s);
        public static bool SmallButton(string s) => Button(s);
        public static void BeginDisabled(bool v = true) { disabledScopes.Push(v); if(v) disabled++; }
        // Tests use a stack so nested disabled scopes retain their actual input state.
        public static void EndDisabled() { if(disabledScopes.Pop()) disabled--; }
        public static void PushID(string s) { }
        public static void PopID() { }
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static TestEncounter Encounter = new();
        public static TestReplays Replays = new();
        public static TestConfig Config = new();
    }
    public sealed class TestConfig { public uint ThemeAccent; }
    public sealed class TestEncounter { public bool InCombat; }
    public sealed class TestReplays { public long EvidenceRevision; public List<ReplayAttempt> Attempts = new(); }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale => 1; }
    public sealed class ArenaCanvas
    {
        public static int Draws;
        public int HighlightSlot { get; set; }
        public bool FocusOnMe { get; set; }
        public void Draw(PlanDocument p, Slide s, Vector2 size, bool editable) { if(editable || size.X <= 0 || size.Y <= 0) throw new Exception("Validation canvas is editable or has no size"); Draws++; }
    }
    public sealed partial class MainWindow
    {
        private PlanDocument? Plan;
        private string reviewAttemptId = "";
        private float reviewTime;
        private bool reviewPlaying;
        private int reviewSeat = -1;
        private long evidenceActor;
        private void TimelineFor(ReplayAttempt a) { if(evidenceActor == 0) evidenceActor = a.Evidence.Actors[0].Id; }
        public static void RunValidationUiTests()
        {
            static void Check(bool c, string message) { if(!c) throw new Exception(message); }
            var p = PlanDocument.CreateDefault();
            var rule = new AdaptiveMechanic { Enabled=true, TerritoryId=1, AnchorActionId=123, WindowSeconds=4,
                Branches=new() { new StatusBranch { StatusId=10, MaximumSeconds=3600, SlideId=p.Slides[0].Id, Label="North tower" } } };
            p.AdaptiveMechanics.Add(rule);
            var a = new ReplayAttempt { Plan=p, Duration=6, TerritoryId=1, LocalSlot=0 };
            a.Evidence.Actors.Add(new EvidenceActor { Id=7, IsLocal=true, SlotIndex=0, Name="Player" });
            a.Casts.Add(new RecordedCast { Source="Live", ActionId=123, Occurrence=1, StartTime=1, ObservedTime=1 });
            a.Mechanics.Add(new ReplayMechanic { ActionId=123, Occurrence=1, Time=1, ExpectedResolve=2 });
            a.Evidence.Statuses.Add(new EvidenceStatus { ActorId=7, StatusId=10, Time=2, Duration=20 });
            Plugin.Replays.Attempts.Add(a);
            var w = new MainWindow { Plan=p, reviewAttemptId=a.Id };
            var original = JsonConvert.SerializeObject(a);
            ImGui.Clicks.Add("Validate pull");
            w.DrawPullValidation(a);
            Check(SpinWait.SpinUntil(()=>{ w.AdvancePullValidation(); return !w.pullValidation.Running; },5000), "UI validation did not finish");
            Check(w.pullValidation.Result?.Decisions.Count == 1, "Validate pull must run production engine for selected actor");
            w.reviewTime = 3; w.validationPreview = true;
            w.DrawPullValidation(a);
            Check(ArenaCanvas.Draws > 0 && ImGui.Text.Exists(t=>t.Contains("North tower")), "Review must preview tested assignment board and cue");
            Check(JsonConvert.SerializeObject(a)==original && !w.reviewPlaying && w.reviewSeat == -1, "Validation changed source plan, playback or active seat");
            var decision = w.pullValidation.Result!.Decisions[0];
            w.pullValidation.Result.Decisions.Add(new AdaptiveDecision { Time=decision.Time, Mechanic="Second rule", Occurrence=1, Reason="Timed out" });
            ImGui.Clicks.Add($"{decision.Time:0.0}s · {decision.Mechanic} · use {decision.Occurrence}");
            w.DrawPullValidation(a);
            ImGui.Text.Clear(); w.DrawPullValidation(a);
            Check(ImGui.Text.Contains("North tower") && !ImGui.Text.Contains("Assignment unclear — check the mechanic"),
                "Selecting one decision must preview that exact row when another decision shares its timestamp");
            Check(w.validationExpected.Count == 0, "Simulation must never create its own expected answer");
            w.validationExpectedRule = rule.Id; w.validationExpectedOutcome = 0;
            ImGui.Clicks.Add("Add reviewed expectation"); w.DrawPullValidation(a);
            Check(w.validationExpected.Count == 1 && w.validationExpectedRows.Exists(r=>r.Outcome == PullComparisonOutcome.Matched),
                "A reviewed user expectation must reach the comparison service");
            w.InvalidatePullValidation();
            Check(w.pullValidation.Result == null && w.validationExpected.Count == 0, "Edits must clear validation and its bound expectations");
            Plugin.Encounter.InCombat=true;
            ImGui.Clicks.Add("Validate pull"); w.DrawPullValidation(a);
            Check(!w.pullValidation.Running && w.pullValidation.Result == null, "Combat must disable validation start");
            Plugin.Encounter.InCombat=false; ImGui.Clicks.Clear();
            w.pullValidation.Dispose();
            Console.WriteLine("PASS: real Review validation controls, detached engine run, board/cue preview, read-only source and edit/combat guards");
        }
    }
}
