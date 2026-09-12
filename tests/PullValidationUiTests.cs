using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
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
        public static readonly Dictionary<string, string> Inputs = new();
        public static readonly HashSet<string> OpenCombos = new();
        public static readonly Dictionary<string, int> ComboChoices = new();
        public static readonly List<string> Options = new();
        private static string activeCombo = "";
        private static int comboIndex;
        public static bool CollapsingHeader(string s) => true;
        public static bool TreeNode(string s) => true;
        public static void TreePop() { }
        public static Vector2 GetContentRegionAvail() => new(600,600);
        public static bool BeginChild(string s, Vector2 size, bool border, ImGuiWindowFlags flags) => true;
        public static void EndChild() { }
        public static void SetNextItemWidth(float v) { }
        public static bool BeginCombo(string label, string preview)
        { if (!OpenCombos.Remove(label)) return false; activeCombo = label; comboIndex = 0; return true; }
        public static void EndCombo() { activeCombo = ""; }
        public static bool Selectable(string s, bool selected = false)
        {
            Options.Add(s);
            if (activeCombo != "" && ComboChoices.TryGetValue(activeCombo, out var pick) && comboIndex++ == pick && disabled == 0)
            { ComboChoices.Remove(activeCombo); return true; }
            return Button(s);
        }
        public static bool Checkbox(string s, ref bool value) { if (!Button(s)) return false; value = !value; return true; }
        public static bool InputInt(string s, ref int v) => false;
        public static bool InputFloat(string s, ref float v, float step = 0, float stepFast = 0, string format = "%.3f")
        { if (disabled != 0 || !Inputs.Remove(s, out var value)) return false; v = float.Parse(value, CultureInfo.InvariantCulture); return true; }
        public static bool InputFloat2(string s, ref Vector2 v, string format = "%.3f")
        { if (disabled != 0 || !Inputs.Remove(s, out var value)) return false; var parts = value.Split(','); v = new(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture)); return true; }
        public static bool InputTextWithHint(string label, string hint, ref string value, uint length)
        { if (!Inputs.Remove(label, out var entered)) return false; value = entered; return true; }
        public static bool InputTextMultiline(string label, ref string value, uint length, Vector2 size)
            => InputTextWithHint(label, "", ref value, length);
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
        public static TestInterface PluginInterface = new();
    }
    public sealed class TestInterface
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "Shikari-validation-ui-" + Guid.NewGuid().ToString("N"));
        public string GetPluginConfigDirectory() => Directory;
    }
    public sealed class TestConfig { public uint ThemeAccent; }
    public sealed class TestEncounter { public bool InCombat; }
    public sealed class TestReplays { public long EvidenceRevision; public List<ReplayAttempt> Attempts = new(); }
}
namespace Shikari.Services.Live
{
    public static class ArenaTracker
    {
        public readonly record struct LivePlayer(string Name, uint JobId, int SlotIndex, Vector2 Board, bool IsLocal);
    }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale => 1; }
    public sealed class ArenaCanvas
    {
        public static int Draws;
        public static int ObservedDraws;
        public int HighlightSlot { get; set; }
        public bool FocusOnMe { get; set; }
        public bool LiveGuides { get; set; }
        public IReadOnlyList<Shikari.Services.Live.ArenaTracker.LivePlayer>? LivePlayers { get; set; }
        public void Draw(PlanDocument p, Slide s, Vector2 size, bool editable)
        {
            if(editable || size.X <= 0 || size.Y <= 0) throw new Exception("Validation canvas is editable or has no size");
            Draws++;
            if(LivePlayers?.Count > 0)
            {
                if(LiveGuides || LivePlayers.Any(player=>player.IsLocal))
                    throw new Exception("Recorded position preview must not enable live guides or settled-player feedback.");
                ObservedDraws++;
            }
        }
    }
    public sealed partial class MainWindow
    {
        private PlanDocument? Plan;
        private string reviewAttemptId = "";
        private float reviewTime;
        private bool reviewPlaying;
        private int reviewSeat = -1;
        private long evidenceActor;
        private void SaveEvidenceEdits(ReplayAttempt attempt, bool enrichStrategy = true)
        {
            if(enrichStrategy) throw new Exception("Position calibration must save evidence without enriching the live strategy.");
            InvalidatePullValidation(); Plugin.Replays.EvidenceRevision++;
        }
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
            Check(ImGui.Text.Contains("1 of 1 mechanic occurrences have usable evidence."),
                "Review must show occurrence coverage instead of only a whole-pull flag");
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
            ImGui.Inputs["Case name##validation"] = "Reviewed north assignment";
            ImGui.Inputs["Review note##validation"] = "Checked the strategy and this player's observed buff pair.";
            ImGui.Clicks.Add("Save reviewed case");
            var caseFile = Path.Combine(Plugin.PluginInterface.Directory, "validation-cases", "pull-validation-cases.json");
            Check(SpinWait.SpinUntil(() => { w.DrawPullValidation(a); return File.Exists(caseFile); }, 5000),
                "The explicit Save reviewed case control must persist reviewed expectations locally");
            ImGui.Clicks.Add("Clear expectations"); w.DrawPullValidation(a);
            Check(w.validationExpected.Count == 0, "Clearing working expectations must not silently reload a saved answer");
            ImGui.Clicks.Add("Load reviewed expectations");
            Check(SpinWait.SpinUntil(() => { w.DrawPullValidation(a); return w.validationExpected.Count == 1; }, 5000),
                "The saved case must explicitly reload its compatible reviewed expectations");
            w.InvalidatePullValidation();
            Check(w.pullValidation.Result == null && w.validationExpected.Count == 0, "Edits must clear validation and its bound expectations");
            Plugin.Encounter.InCombat=true;
            ImGui.Clicks.Add("Validate pull"); w.DrawPullValidation(a);
            Check(!w.pullValidation.Running && w.pullValidation.Result == null, "Combat must disable validation start");
            Plugin.Encounter.InCombat=false; ImGui.Clicks.Clear();
            w.pullValidation.Dispose();
            w.validationCaseStore?.Dispose();
            // ReplayStore uses compact persistence; session-only canvas and roster IDs regenerate on restart.
            a = JsonConvert.DeserializeObject<ReplayAttempt>(JsonConvert.SerializeObject(a, Shikari.Services.PlanJson.Compact()),
                Shikari.Services.PlanJson.Compact())!;
            p = a.Plan; Plugin.Replays.Attempts[0] = a;
            var reopened = new MainWindow { Plan=p, reviewAttemptId=a.Id };
            ImGui.Clicks.Add("Validate pull"); reopened.DrawPullValidation(a);
            Check(SpinWait.SpinUntil(() => {
                reopened.AdvancePullValidation(); reopened.DrawPullValidation(a);
                return reopened.pullValidation.Result != null && reopened.validationCaseStore?.Items.Count == 1;
            }, 5000), "Reopening Review must retain the durable case and rerun its recording");
            Check(reopened.validationExpected.Count == 0, "Reopening must not accept saved expectations automatically");
            reopened.validationCaseSelection = reopened.validationCaseStore!.Items[0].Id;
            ImGui.Clicks.Add("Load reviewed expectations"); reopened.DrawPullValidation(a);
            Check(reopened.validationExpected.Count == 1 && reopened.validationExpectedRows.Exists(r=>r.Outcome == PullComparisonOutcome.Matched),
                "A compatible case must load after a new Review instance is created");
            a.Evidence.Statuses[0].Time += .1f;
            reopened.InvalidatePullValidation(); ImGui.Clicks.Add("Validate pull"); reopened.DrawPullValidation(a);
            Check(SpinWait.SpinUntil(() => { reopened.AdvancePullValidation(); return !reopened.pullValidation.Running; }, 5000),
                "The edited evidence run did not settle");
            ImGui.Clicks.Add("Load reviewed expectations"); ImGui.Text.Clear(); reopened.DrawPullValidation(a);
            Check(reopened.validationExpected.Count == 0 && ImGui.Text.Exists(t=>t.Contains("different inputs")),
                "A case from before an evidence edit must be visibly incompatible and cannot load");
            ImGui.Clicks.Clear(); reopened.pullValidation.Dispose(); reopened.validationCaseStore?.Dispose();
            File.Delete(caseFile); Directory.Delete(Path.GetDirectoryName(caseFile)!); Directory.Delete(Plugin.PluginInterface.Directory);
            RunPositionUiTests();
            Console.WriteLine("PASS: actual Review controls, occurrence coverage, board/cue preview, durable cases, exact position-check selection and publication, input incompatibility and combat guards");
        }

        private static void RunPositionUiTests()
        {
            static void Check(bool c, string message) { if (!c) throw new Exception(message); }
            ImGui.Clicks.Clear(); ImGui.Inputs.Clear(); ImGui.Text.Clear(); ImGui.Options.Clear();
            var plan = PlanDocument.CreateDefault("Reviewed board", 2);
            plan.Slides[0].Id = "assigned";
            plan.Slides[0].Items.Add(new CanvasItem { Kind=CanvasItemKind.PlayerToken, SlotIndex=0, Position=new(.5f,.5f) });
            plan.AdaptiveMechanics.Add(new() { Id="position-rule", Label="Destination assignment", Enabled=true, TerritoryId=1, AnchorActionId=123, WindowSeconds=4,
                Branches=new() { new() { StatusId=10, MaximumSeconds=3600, SlideId="assigned", Label="Reviewed north spot" } } });
            var attempt = new ReplayAttempt { Id="position-pull", Plan=plan, Duration=8, TerritoryId=1,
                Casts=new() { new() { Source="FF Logs", ActionId=123, Occurrence=1, StartTime=1, ObservedTime=1 } },
                Evidence=new() { Source="FF Logs", Complete=true, EffectsComplete=true, CalibrationSlideId="assigned",
                    Actors=new() { new() { Id=7, SlotIndex=0, Name="Selected player" }, new() { Id=8, SlotIndex=1, Name="Other player" } },
                    Statuses=new() { new() { ActorId=7, StatusId=10, Time=2 } },
                    Positions=new() { new() { ActorId=7, Time=3, Position=new(30,-30) } },
                    References=new() { new() { Source=new(0,0), Board=new(.2f,.2f) }, new() { Source=new(40,0), Board=new(.2f,.6f) }, new() { Source=new(0,-40), Board=new(.6f,.2f) } },
                    Effects=new() {
                        new() { Time=3, ActionId=200, SourceId=99, TargetId=8, Type="calculateddamage", Name="Other player's event", TargetPosition=new(0,0) },
                        new() { Time=3, ActionId=200, SourceId=99, TargetId=7, Type="calculateddamage", Name="Selected calculated event", TargetPosition=new(30,-30) },
                        new() { Time=3.3f, ActionId=200, SourceId=99, TargetId=7, Type="damage", Name="Delayed damage event", TargetPosition=new(0,0) },
                    } } };
            Plugin.Replays.Attempts.Clear(); Plugin.Replays.Attempts.Add(attempt);
            var window = new MainWindow { Plan=plan, reviewAttemptId=attempt.Id, evidenceActor=7 };
            ImGui.Clicks.Add("Validate pull"); window.DrawPullValidation(attempt);
            Check(SpinWait.SpinUntil(() => { window.AdvancePullValidation(); return !window.pullValidation.Running; },5000), "Position UI assignment validation did not complete.");
            var assignments = window.pullValidation.Result!;
            Check(assignments?.Decisions.Count==1, "Position UI needs a production-engine assignment.");
            var decision = assignments!.Decisions[0];
            window.SelectPositionDecision(assignments,decision); window.DrawPositionCheck(assignments,attempt);
            Check(window.positionEffectIndex == -1 && window.positionCheck.Result == null, "Position UI must not silently choose the first effect or start a check.");
            ImGui.OpenCombos.Add("Effect##position"); window.DrawPositionCheck(assignments,attempt);
            Check(ImGui.Options.Any(o => o.Contains("Selected calculated event")) &&
                !ImGui.Options.Any(o => o.Contains("Other player's event") || o.Contains("Delayed damage event")),
                "Effect options must offer only the chosen player's exact calculated events.");
            ImGui.OpenCombos.Add("Effect##position"); ImGui.ComboChoices["Effect##position"] = 0; window.DrawPositionCheck(assignments,attempt);
            Check(window.positionEffectIndex == 1, "Choosing the offered event must preserve its exact source index.");
            ImGui.Clicks.Add("I verified this mechanic checkpoint"); ImGui.Clicks.Add("I reviewed this board’s alignment"); window.DrawPositionCheck(assignments,attempt);
            var before = JsonConvert.SerializeObject(attempt); var observedBefore = ArenaCanvas.ObservedDraws;
            ImGui.Clicks.Add("Check position"); window.DrawPositionCheck(assignments,attempt);
            Check(SpinWait.SpinUntil(() => { window.AdvancePullValidation(); window.DrawPositionCheck(assignments,attempt); return !window.positionCheck.Running; },5000), "Check position did not publish.");
            Check(window.positionCheck.Result?.Outcome==PositionCheckOutcome.Near && window.positionCheck.Result.SampleTime==3,
                "Actual Check position control must publish the production evaluator's exact-event observation.");
            Check(ArenaCanvas.ObservedDraws > observedBefore && JsonConvert.SerializeObject(attempt)==before,
                "Position preview must draw the observed marker read-only without changing the recording.");
            ImGui.Clicks.Add("Use a recorded effect checkpoint"); window.DrawPositionCheck(assignments,attempt);
            Check(window.positionCheck.Result == null, "Switching checkpoint source must invalidate an old verdict.");
            ImGui.Inputs["Checkpoint time (seconds)"] = "3.1"; window.DrawPositionCheck(assignments,attempt);
            Check(window.positionTime==3.1f && window.positionCheck.Result==null, "Editing checkpoint time must not retain previous results.");
            window.positionCheckpointReviewed = true; window.positionAlignmentReviewed = true;
            Plugin.Encounter.InCombat = true; ImGui.Clicks.Add("Check position"); window.DrawPositionCheck(assignments,attempt);
            Check(!window.positionCheck.Running && window.positionCheck.Result==null, "Combat must disable position checks.");
            Plugin.Encounter.InCombat = false; ImGui.Clicks.Clear();
            ImGui.Clicks.Add("Check position"); window.DrawPositionCheck(assignments,attempt);
            Check(SpinWait.SpinUntil(() => { window.AdvancePullValidation(); window.DrawPositionCheck(assignments,attempt); return !window.positionCheck.Running; },5000) && window.positionCheck.Result != null,
                "Reviewed manual checkpoints must reach the passive evaluator.");
            ImGui.Inputs["Checkpoint time (seconds)"] = "3.15"; window.DrawPositionCheck(assignments,attempt);
            Check(window.positionCheck.Result==null && !window.positionCheckpointReviewed,
                "Editing a completed manual checkpoint must clear both its result and its review acknowledgement.");
            window.positionCheckpointReviewed=true; ImGui.Clicks.Add("Check position"); window.DrawPositionCheck(assignments,attempt);
            Check(SpinWait.SpinUntil(() => { window.AdvancePullValidation(); window.DrawPositionCheck(assignments,attempt); return !window.positionCheck.Running; },5000) && window.positionCheck.Result != null,
                "The replacement reviewed checkpoint must publish before testing calibration invalidation.");
            var previousRevision = Plugin.Replays.EvidenceRevision;
            ImGui.Inputs["Landmark source X / Y"] = "40,-40"; ImGui.Inputs["Landmark board X / Y"] = "0.6,0.6";
            ImGui.Clicks.Add("Add alignment landmark"); window.DrawPositionCalibration(attempt);
            Check(window.positionCheck.Result==null && window.positionDecision==null && !window.positionAlignmentReviewed,
                "Calibration/evidence edits must invalidate position results and review acknowledgements.");
            Check(attempt.Evidence.References.Count==4 && attempt.Evidence.References[3].Source==new Vector2(40,-40) &&
                attempt.Evidence.References[3].Board==new Vector2(.6f,.6f) && Plugin.Replays.EvidenceRevision>previousRevision,
                "Alignment controls must save the exact user-entered landmark and invalidate evidence revision.");
            Plugin.Encounter.InCombat=true; ImGui.Clicks.Add("Clear position alignment"); window.DrawPositionCalibration(attempt);
            Check(attempt.Evidence.References.Count==4, "Combat must disable alignment edits.");
            Plugin.Encounter.InCombat=false; ImGui.Clicks.Clear();
            window.positionCheck.Dispose(); window.pullValidation.Dispose(); window.validationCaseStore?.Dispose();
            ImGui.Clicks.Clear(); ImGui.Inputs.Clear(); ImGui.OpenCombos.Clear(); ImGui.ComboChoices.Clear();
        }
    }
}
