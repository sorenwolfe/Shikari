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
        public static void TextWrapped(string text) => Text.Add(text);
        public static void TextDisabled(string text) => Text.Add(text);
        public static bool SmallButton(string text) { Buttons++; return Click; }
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
            Console.WriteLine("PASS: cast target review selection, pause/seek, draft reset, timing distinction and unknown identity");
        }
    }
}
