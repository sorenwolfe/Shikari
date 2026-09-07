using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
namespace Dalamud.Game.ClientState.Conditions { public enum ConditionFlag { BoundByDuty } }
namespace Dalamud.Interface.Windowing
{
    public class Window
    {
        public Window(string name, ImGuiWindowFlags flags) { Flags = flags; }
        public ImGuiWindowFlags Flags;
        public bool RespectCloseHotkey, DisableWindowSounds, ShowCloseButton, AllowPinning, AllowClickthrough, ForceMainWindow, IsOpen;
        public virtual bool DrawConditions() => true;
        public virtual void PreDraw() { }
        public virtual void PostDraw() { }
        public virtual void Draw() { }
    }
}
namespace Dalamud.Interface.Utility
{
    public static class ImGuiHelpers { public static FakeViewport MainViewport = new(); }
    public sealed class FakeViewport { public Vector2 Pos = new(40, 50), Size = new(800, 600); }
}
namespace Dalamud.Bindings.ImGui
{
    [Flags] public enum ImGuiWindowFlags { None = 0, NoMove = 1, NoDecoration = 2, NoSavedSettings = 4, NoFocusOnAppearing = 8, NoBackground = 16, NoNav = 32, NoScrollbar = 64, NoScrollWithMouse = 128, NoInputs = 256 }
    public enum ImGuiCond { Always }
    public enum ImGuiStyleVar { WindowPadding }
    public enum ImGuiHoveredFlags { AllowWhenBlockedByActiveItem }
    public enum ImGuiMouseButton { Left }
    public enum ImDrawFlags { None }
    public sealed class ImDrawListPtr
    {
        public void AddRectFilled(Vector2 a, Vector2 b, uint c, float r) { }
        public void AddRect(Vector2 a, Vector2 b, uint c, float r, ImDrawFlags f, float t) { }
        public void AddLine(Vector2 a, Vector2 b, uint c, float t) { }
        public void AddCircleFilled(Vector2 p, float r, uint c, int n) { }
    }
    // Substitute only the game UI boundary. Production MiniPlanWindow owns sizing, flags,
    // content submission and config changes; the recorder exposes those decisions to tests.
    public static class ImGui
    {
        private sealed record Frame(Vector2 Pos, Vector2 Size, ImGuiWindowFlags Flags, Vector2 Padding)
        { public Vector2 Cursor = Padding; }
        private static readonly Stack<Frame> frames = new();
        private static readonly Stack<Vector2> padding = new();
        private static Vector2 nextPos, nextSize;
        private static Frame Current => frames.Peek();
        public static readonly List<(string Id, ImGuiWindowFlags Flags, Vector2 Pos, Vector2 Size)> Windows = new();
        public static readonly List<(string Text, bool Scrollable, bool Wrapped)> Texts = new();
        public static bool TogglePersonal;
        private static bool wrap;
        public static int ScrollResets;
        public static void Reset() { frames.Clear(); padding.Clear(); Windows.Clear(); Texts.Clear(); wrap = false; }
        public static void SetNextWindowSize(Vector2 value, ImGuiCond c) => nextSize = value;
        public static void SetNextWindowPos(Vector2 value, ImGuiCond c) => nextPos = value;
        public static bool Begin(string id, ImGuiWindowFlags flags)
        { frames.Push(new(nextPos, nextSize, flags, padding.Count > 0 ? padding.Peek() : Vector2.Zero)); Windows.Add((id, flags, nextPos, nextSize)); return true; }
        public static void End() => frames.Pop();
        public static bool BeginChild(string id, Vector2 size, bool border, ImGuiWindowFlags flags)
        { nextPos = Current.Pos + Current.Cursor; nextSize = size; return Begin(id, flags | (Current.Flags & ImGuiWindowFlags.NoInputs)); }
        public static void EndChild() => End();
        public static void PushStyleVar(ImGuiStyleVar v, Vector2 value) => padding.Push(value);
        public static void PopStyleVar() => padding.Pop();
        public static Vector2 GetWindowPos() => Current.Pos;
        public static Vector2 GetWindowSize() => Current.Size;
        public static Vector2 GetContentRegionAvail() => Current.Size - Current.Cursor - Current.Padding;
        public static ImDrawListPtr GetWindowDrawList() => new();
        public static void SetCursorPos(Vector2 v) => Current.Cursor = v;
        public static float GetCursorPosX() => Current.Cursor.X;
        public static float GetTextLineHeight() => 16;
        public static float GetFrameHeightWithSpacing() => 24;
        public static Vector2 CalcTextSize(string text, bool hide, float width) => new(width, MathF.Ceiling(text.Length * 8 / MathF.Max(1, width)) * 16);
        public static (float ScrollbarSize, int Unused) GetStyle() => (14, 0);
        public static void TextUnformatted(string text)
        { Texts.Add((text, (Current.Flags & (ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoInputs)) == 0, wrap)); Current.Cursor += new Vector2(0, 16); }
        public static void TextColored(Vector4 color, string text) => TextUnformatted(text);
        public static void PushTextWrapPos(float x) => wrap = true;
        public static void PopTextWrapPos() => wrap = false;
        public static void SetScrollY(float y) => ScrollResets++;
        public static void Separator() { }
        public static bool Checkbox(string label, ref bool value)
        { Current.Cursor += new Vector2(0, 24); if (!TogglePersonal || (Current.Flags & ImGuiWindowFlags.NoInputs) != 0) return false; TogglePersonal = false; value = !value; return true; }
        public static bool IsWindowHovered(ImGuiHoveredFlags f) => false;
        public static bool IsItemHovered() => false;
        public static Vector2 GetMousePos() => Vector2.Zero;
        public static bool IsMouseClicked(ImGuiMouseButton b) => false;
        public static bool IsMouseDown(ImGuiMouseButton b) => false;
        public static bool IsMouseReleased(ImGuiMouseButton b) => false;
        public static void SetWindowPos(Vector2 v, ImGuiCond c) { }
    }
}
namespace Shikari.Services { public sealed class ZoneClassifier { public void Refresh() { } public uint ContentTypeId => 5; public bool HighEndDuty => true; } }
namespace Shikari
{
    public static class Plugin
    {
        public static Configuration Config = new();
        public static FakeEncounter Encounter = new();
        public static FakePlans Plans = new();
        public static FakeMain Main = new();
        public static FakeTracker Tracker = new();
        public static FakeRoster Roster = new();
        public static FakeCondition Condition = new();
        public static int Saves;
        public static void SaveConfig() => Saves++;
    }
    public sealed class FakeEncounter { public bool InCombat = true; }
    public sealed class FakePlans { public PlanDocument? Active = PlanDocument.CreateDefault(); }
    public sealed class FakeMain { public int SlideIndex; }
    public sealed class FakeRoster { public int Slot = -1; public int ResolveLocalSlot(PlanDocument? p) => Slot; }
    public sealed class FakeCondition { public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag f] => true; }
    public sealed class FakeTracker
    {
        public bool Aligned => false; public float BoardPerYalm => 0.01f; public string Status => "No alignment";
        public bool TryAlign(PlanDocument p, Slide? s, out object? result) { result = null; return false; }
        public object? Read(PlanDocument p, Slide s) => null;
    }
}
namespace Shikari.UI.Theme
{
    public struct ThemeScope : IDisposable { public static ThemeScope Push() => new(); public void Dispose() { } }
    public static class Palette { public const uint Window = 0; public static uint Pack(uint c, float a) => c; public static uint Line(float a) => 0; public static Vector4 Vec(uint c) => Vector4.One; }
    public static class Sprites { public static void Shadow(ImDrawListPtr d, Vector2 a, Vector2 b, float r, float o) { } }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale => 1; public static uint WithAlpha(uint c, float a) => c; public static int Tooltips; public static void Tooltip(string s) => Tooltips++; }
    public sealed class ArenaCanvas
    {
        public bool MiniPresentation, LiveGuides, FocusOnMe, MiniYourView;
        public bool Settled => false;
        public int HighlightSlot;
        public object? LivePlayers;
        public float SettleTolerance;
        public static bool LastPersonal;
        public static int LastSlot;
        public bool Draw(PlanDocument plan, Slide slide, Vector2 available, bool editable) { LastPersonal = MiniYourView; LastSlot = HighlightSlot; return false; }
    }
}
namespace Shikari.Tests
{
    public static class MiniWindowTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void Frame(Shikari.UI.MiniPlanWindow window)
        { ImGui.Reset(); window.PreDraw(); ImGui.Begin("arena", window.Flags); window.Draw(); window.PostDraw(); ImGui.End(); }
        public static void Run()
        {
            var notes = string.Join("\n", Enumerable.Repeat("Long mechanic note with all details retained.", 120));
            Plugin.Plans.Active!.Slides[0].Notes = notes;
            Plugin.Config.MiniPlanSize = 640;
            Plugin.Config.MiniPlanAnchor = new Vector2(1.5f, -1);
            var window = new Shikari.UI.MiniPlanWindow();
            ImGui.TogglePersonal = true;
            Frame(window);
            Check(Plugin.Config.MiniPlanYourView && Plugin.Saves == 1, "Combat checkbox must receive input and persist the choice");
            Check(Shikari.UI.UiHelpers.Tooltips == 0, "An unhovered mini control must not display a tooltip over the game");
            var arena = ImGui.Windows.Single(w => w.Id == "arena");
            var footer = ImGui.Windows.Single(w => w.Id == "##shikari-mini-controls");
            Check((arena.Flags & ImGuiWindowFlags.NoInputs) != 0, "Combat arena must pass clicks through");
            Check((footer.Flags & ImGuiWindowFlags.NoInputs) == 0 && footer.Pos.Y >= arena.Pos.Y + arena.Size.X, "Interactive footer must not cover arena clicks");
            Check(ImGui.Texts.Any(t => t.Text == notes && t.Scrollable && t.Wrapped), "Entire notes must be submitted to wrapped scrollable content");
            Check(footer.Pos.Y + footer.Size.Y <= 650 && footer.Pos.X >= 40, "Footer must stay within the viewport despite an off-screen saved anchor");
            var reset = ImGui.ScrollResets;
            Frame(window);
            Check(ImGui.ScrollResets == reset, "Scrolling must survive subsequent frames of the same slide");
            Check(ImGui.Texts.Any(t => t.Text.Contains("Choose your seat")), "Unresolved seat must be explained in personal view");
            Plugin.Config.MiniPlanHighlightMe = false;
            Plugin.Roster.Slot = 2;
            Frame(window);
            Check(Shikari.UI.ArenaCanvas.LastPersonal && Shikari.UI.ArenaCanvas.LastSlot == 2, "Your view must retain own destinations even with highlighting disabled");
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Plugin.Config);
            Check(Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!.MiniPlanYourView, "Your view must survive config serialization");
            Plugin.Plans.Active.Slides.Add(new Slide { Notes = "Next mechanic" }); Plugin.Main.SlideIndex = 1;
            Frame(window);
            Check(ImGui.ScrollResets == reset + 1, "A new slide must start notes at the top");
            Console.WriteLine("PASS: production mini window combat input separation, checkbox persistence, viewport bounds, complete wrapped scrolling notes and slide scroll reset");
        }
    }
}
