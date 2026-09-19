using System;
using System.Collections.Generic;
using Shikari.Model;
using Shikari.Services.Replay;
using Dalamud.Bindings.ImGui;

namespace Dalamud.Bindings.ImGui
{
    public static class ImGui
    {
        public static readonly List<string> Text = new();
        public static int Buttons;
        public static bool Click;
        public static string? ClickLabel;
        public static void TextWrapped(string text) => Text.Add(text);
        public static void TextDisabled(string text) => Text.Add(text);
        public static bool SmallButton(string text) { Buttons++; return ClickLabel == null ? Click : ClickLabel == text; }
    }
}
namespace Shikari.UI
{
    public sealed partial class MainWindow
    {
        private long evidenceActor;
        private int reviewSeat;
        private float reviewTime;
        private bool reviewPlaying = true;
        private readonly HashSet<uint> evidenceSelection = new() { 10 };
        private AdaptiveMechanic? evidenceDraft = new();
        public static void Run()
        {
            static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
            var attempt = new ReplayAttempt { Duration = 10, Plan = PlanDocument.CreateDefault() };
            var anchor = new ReplayMechanic { ActionId = 123, Occurrence = 1, Time = 1 };
            attempt.Mechanics.Add(anchor);
            var cast = new RecordedCast { ActionId = 123, Occurrence = 1, StartTime = 1, ObservedTime = 2,
                ExpectedEndTime = 5, Source = "Live", TargetId = 0x100000009 };
            attempt.Casts.Add(cast);
            attempt.Evidence.Actors.Add(new EvidenceActor { Id = 9, GameObjectId = 0x100000009, Name = "Target", SlotIndex = 3 });
            var window = new MainWindow();
            ImGui.Click = true;
            window.DrawCastEvidenceContext(attempt, anchor);
            Check(window.evidenceActor == 9 && window.reviewSeat == 3 && window.reviewTime == 2 && !window.reviewPlaying,
                "Target action must select its status/position actor and pause at observed time");
            Check(window.evidenceSelection.Count == 0 && window.evidenceDraft == null, "Target change clears the previous actor's draft");
            Check(ImGui.Text.Exists(t => t.StartsWith("Expected cast-bar end")) &&
                !ImGui.Text.Exists(t => t.StartsWith("Observed cast completion")), "Expected bar end cannot be displayed as observed completion");
            attempt.Evidence.Actors[0].GameObjectId = null;
            ImGui.Buttons = 0; ImGui.Text.Clear();
            window.DrawCastEvidenceContext(attempt, anchor);
            Check(ImGui.Buttons == 0 && ImGui.Text.Exists(t => t.Contains("unavailable")), "Legacy missing identity cannot offer a guessed target");
            ImGui.Text.Clear();
            window.DrawCastEvidenceContext(attempt, new ReplayMechanic { ActionId = 123, Occurrence = 1, Time = 1 });
            Check(ImGui.Text.Count == 0, "A foreign mechanic must not supply cast context");
            cast.Source = "FF Logs"; cast.CasterId = 99; cast.TargetId = 9;
            cast.CompletionTime = 5; cast.ExpectedEndTime = null;
            // JSON keeps this test executable against the old model for its red phase.
            var json = Newtonsoft.Json.Linq.JObject.FromObject(cast);
            json["CompletionTargetId"] = 10; json["CasterInstance"] = 2;
            cast = json.ToObject<RecordedCast>()!; attempt.Casts[0] = cast;
            attempt.Evidence.Source = "FF Logs";
            attempt.Evidence.Actors.Add(new EvidenceActor { Id = 10, Name = "Other target", SlotIndex = 4 });
            ImGui.Text.Clear(); ImGui.ClickLabel = "Inspect completion target";
            window.DrawCastEvidenceContext(attempt, anchor);
            Check(window.evidenceActor == 10 && window.reviewSeat == 4 && window.reviewTime == 5,
                "Completion inspection selects its own target and exact completion time, not the cast-start target.");
            Check(ImGui.Text.Exists(t => t == "Cast target: Target") && ImGui.Text.Exists(t => t == "Completion target: Other target"),
                "Start and completion targets must have distinct labels.");
            cast.CompletionTargetId = null; ImGui.Buttons = 0; ImGui.Text.Clear();
            window.DrawCastEvidenceContext(attempt, anchor);
            Check(ImGui.Buttons == 1 && ImGui.Text.Exists(t => t.Contains("Completion target unavailable")),
                "An absent completion target must not fall back to the known start target.");
            Console.WriteLine("PASS: cast target review selection, pause/seek, draft reset, timing distinction and unknown identity");
        }
    }
}
