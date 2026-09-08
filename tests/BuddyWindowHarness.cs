using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Buddy;

namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag { WatchingCutscene, WatchingCutscene78, OccupiedInCutSceneEvent }
}
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
    public sealed class FakeViewport { public Vector2 Pos = new(40, 50), Size = new(1920, 1080); }
}
namespace Dalamud.Interface.Textures
{
    public interface ISharedImmediateTexture { bool TryGetWrap(out FakeWrap? wrap, out object? error); }
    public sealed class FakeWrap { public ImTextureID Handle => new(); }
    public sealed class FakeTexture : ISharedImmediateTexture
    {
        public bool TryGetWrap(out FakeWrap? wrap, out object? error) { wrap = new(); error = null; return true; }
    }
}
namespace Dalamud.Bindings.ImGui
{
    [Flags] public enum ImGuiWindowFlags { None = 0, NoDecoration = 1, NoMove = 2, NoSavedSettings = 4, NoBackground = 8, NoFocusOnAppearing = 16, NoNav = 32, NoScrollbar = 64, NoScrollWithMouse = 128, NoInputs = 256 }
    public enum ImGuiCond { Always }
    public enum ImGuiStyleVar { WindowPadding }
    public enum ImGuiHoveredFlags { AllowWhenBlockedByActiveItem }
    public enum ImGuiMouseButton { Left }
    public enum ImDrawFlags { None }
    public struct ImTextureID { }
    public struct ImFontPtr { }
    public sealed class ImDrawListPtr
    {
        public Graphics? G;
        public Image? Sprite;
        public readonly List<(string Text, Vector2 Position, Vector2 Size, uint Color)> Texts = new();
        public readonly List<(Vector2 Min, Vector2 Max, Vector2 Uv)> Images = new();
        private static Color C(uint c) => Color.FromArgb((int)(c >> 24), (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255));
        private static PointF P(Vector2 p) => new(p.X, p.Y);
        public void Clear() { Texts.Clear(); Images.Clear(); }
        private static GraphicsPath Rounded(Vector2 min, Vector2 max, float r)
        {
            var p = new GraphicsPath(); var d = Math.Min(r * 2, Math.Min(max.X - min.X, max.Y - min.Y));
            if (d < .1f) { p.AddRectangle(new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y)); return p; }
            p.AddArc(min.X, min.Y, d, d, 180, 90); p.AddArc(max.X - d, min.Y, d, d, 270, 90);
            p.AddArc(max.X - d, max.Y - d, d, d, 0, 90); p.AddArc(min.X, max.Y - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public void AddRectFilled(Vector2 min, Vector2 max, uint c, float r)
        { if (G == null) return; using var b = new SolidBrush(C(c)); using var p = Rounded(min, max, r); G.FillPath(b, p); }
        public void AddRect(Vector2 min, Vector2 max, uint c, float r, ImDrawFlags f, float width)
        { if (G == null) return; using var pen = new Pen(C(c), width); using var p = Rounded(min, max, r); G.DrawPath(pen, p); }
        public void AddCircleFilled(Vector2 p, float r, uint c, int segments)
        { if (G == null) return; using var b = new SolidBrush(C(c)); G.FillEllipse(b, p.X - r, p.Y - r, r * 2, r * 2); }
        public void AddLine(Vector2 a, Vector2 b, uint c, float width)
        { if (G == null) return; using var pen = new Pen(C(c), width); G.DrawLine(pen, P(a), P(b)); }
        public void AddTriangleFilled(Vector2 a, Vector2 b, Vector2 d, uint c)
        { if (G == null) return; using var brush = new SolidBrush(C(c)); G.FillPolygon(brush, new[] { P(a), P(b), P(d) }); }
        public void AddImageRounded(ImTextureID id, Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1, uint tint, float rounding)
        {
            Images.Add((min, max, uv0));
            if (G == null || Sprite == null) return;
            var state = G.Save(); using var clip = Rounded(min, max, rounding); G.SetClip(clip);
            var destination = uv0.X > uv1.X
                ? new[] { new PointF(max.X, min.Y), new PointF(min.X, min.Y), new PointF(max.X, max.Y) }
                : new[] { new PointF(min.X, min.Y), new PointF(max.X, min.Y), new PointF(min.X, max.Y) };
            G.DrawImage(Sprite, destination); G.Restore(state);
        }
        public void AddText(ImFontPtr font, float size, Vector2 at, uint c, string text, float wrapWidth)
        {
            var measured = ImGui.Measure(text, wrapWidth, size);
            Texts.Add((text, at, measured, c));
            if (G == null) return;
            using var brush = new SolidBrush(C(c)); using var drawingFont = ImGui.Font(size);
            G.DrawString(text, drawingFont, brush, new RectangleF(at.X, at.Y, wrapWidth, measured.Y + 2), StringFormat.GenericTypographic);
        }
    }
    // Only the game UI boundary is substituted. The actual BuddyWindow controls visibility,
    // geometry, text submission, animation state, texture lifetime and config writes.
    public static class ImGui
    {
        private static readonly Bitmap measureBitmap = new(4, 4);
        private static readonly Graphics measureGraphics = Graphics.FromImage(measureBitmap);
        public static readonly ImDrawListPtr DrawList = new();
        public static Vector2 WindowPosition, WindowSize, Mouse;
        public static bool MouseDown, MouseClicked, Hovered;
        public static double Time;
        public static float BaseFontSize = 16;
        public static int PaddingDepth;
        public static float GetFontSize() => BaseFontSize;
        public static ImFontPtr GetFont() => new();
        public static double GetTime() => Time;
        public static void SetNextWindowPos(Vector2 p, ImGuiCond c) => WindowPosition = p;
        public static void SetNextWindowSize(Vector2 s, ImGuiCond c) => WindowSize = s;
        public static void SetWindowPos(Vector2 p, ImGuiCond c) => WindowPosition = p;
        public static Vector2 GetWindowPos() => WindowPosition;
        public static Vector2 GetWindowSize() => WindowSize;
        public static ImDrawListPtr GetWindowDrawList() => DrawList;
        public static void PushStyleVar(ImGuiStyleVar v, Vector2 padding) => PaddingDepth++;
        public static void PopStyleVar() => PaddingDepth--;
        public static Vector2 GetMousePos() => Mouse;
        public static bool IsMouseDown(ImGuiMouseButton b) => MouseDown;
        public static bool IsMouseClicked(ImGuiMouseButton b) => MouseClicked;
        public static bool IsWindowHovered(ImGuiHoveredFlags f) => Hovered;
        public static Font Font(float size) => new("Segoe UI", size * .75f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static Vector2 Measure(string text, float width, float size)
        {
            if (text.Length == 0) return Vector2.Zero;
            using var font = Font(size);
            var measured = measureGraphics.MeasureString(text, font, Math.Max(1, (int)MathF.Round(width)), StringFormat.GenericTypographic);
            return new(measured.Width, measured.Height);
        }
        public static Vector2 CalcTextSize(string text, bool hide, float wrap) => Measure(text, wrap, BaseFontSize);
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static FakeConfig Config = new();
        public static FakeClient ClientState = new();
        public static FakeCondition Condition = new();
        public static FakePlans Plans = new();
        public static FakeEncounter Encounter = new();
        public static FakeBuddy Buddy = new();
        public static FakeProvider TextureProvider = new();
        public static FakeLog Log = new();
        public static int Saves;
        public static void SaveConfig() => Saves++;
    }
    public sealed class FakeConfig
    {
        public bool BuddyEnabled, BuddyUnlocked, BuddyReducedMotion;
        public float BuddyScale = 1;
        public Vector2 BuddyAnchor = new(.76f, .70f);
        public uint ThemeAccent;
    }
    public sealed class FakeClient { public bool IsLoggedIn = true, IsGPosing; }
    public sealed class FakeCondition
    {
        public HashSet<Dalamud.Game.ClientState.Conditions.ConditionFlag> Active = new();
        public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag flag] => Active.Contains(flag);
    }
    public sealed class FakePlans { public FakePlan? Active = new(); }
    public sealed class FakePlan { public List<int> Slides = new() { 1 }; }
    public sealed class FakeEncounter { public bool InCombat; }
    public sealed class FakeBuddy { public BuddyPresentation Presentation = new("", "", "", "", BuddyMood.Resting, false); }
    public sealed class FakeProvider
    {
        public int Loads;
        public bool Fail;
        public Dalamud.Interface.Textures.ISharedImmediateTexture GetFromManifestResource(Assembly a, string name)
        { Loads++; if (Fail) throw new InvalidOperationException("Missing resource"); return new Dalamud.Interface.Textures.FakeTexture(); }
    }
    public sealed class FakeLog { public int Warnings; public void Warning(Exception e, string message) => Warnings++; }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale = 1; public static void Tooltip(string text) { } }
}
namespace Shikari.Tests
{
    using Dalamud.Game.ClientState.Conditions;
    using Dalamud.Interface.Utility;
    using Shikari.UI;
    public static class BuddyWindowTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void Frame(BuddyWindow window)
        {
            ImGui.DrawList.Clear();
            if (!window.DrawConditions()) return;
            window.PreDraw(); window.Draw(); window.PostDraw();
            Check(ImGui.PaddingDepth == 0, "Every window must restore its style stack");
            ImGui.MouseClicked = false;
        }
        private static void Reset()
        {
            Plugin.Config = new() { BuddyEnabled = true }; Plugin.ClientState = new(); Plugin.Condition = new(); Plugin.Plans = new();
            Plugin.Encounter = new(); Plugin.Buddy = new(); Plugin.TextureProvider = new(); Plugin.Log = new(); Plugin.Saves = 0;
            ImGuiHelpers.MainViewport.Pos = new(40, 50); ImGuiHelpers.MainViewport.Size = new(1920, 1080);
            ImGui.Hovered = ImGui.MouseDown = ImGui.MouseClicked = false; ImGui.BaseFontSize = 16; ImGui.Time = 10;
        }
        public static void Run()
        {
            Reset(); using var window = new BuddyWindow();
            Check(window.DrawConditions(), "Enabled buddy must be available with an active plan in game");
            Plugin.Config.BuddyEnabled = false; Check(!window.DrawConditions(), "Disabled buddy must disappear immediately"); Plugin.Config.BuddyEnabled = true;
            Plugin.ClientState.IsLoggedIn = false; Check(!window.DrawConditions(), "Buddy must hide at character selection"); Plugin.ClientState.IsLoggedIn = true;
            Plugin.ClientState.IsGPosing = true; Check(!window.DrawConditions(), "Buddy must hide in group pose"); Plugin.ClientState.IsGPosing = false;
            foreach (var flag in Enum.GetValues<ConditionFlag>())
            { Plugin.Condition.Active.Add(flag); Check(!window.DrawConditions(), "Buddy must hide for every cutscene flag"); Plugin.Condition.Active.Clear(); }
            Plugin.Plans.Active = null; Check(!window.DrawConditions(), "Buddy must hide with no plan"); Plugin.Plans.Active = new();
            Frame(window); Check(ImGui.DrawList.Texts.Count == 0, "Idle creature must not generate a permanent instruction bubble");
            Plugin.Config.BuddyUnlocked = true; Plugin.Encounter.InCombat = true;
            Frame(window); Check((window.Flags & ImGuiWindowFlags.NoInputs) != 0, "Combat must always override unlocked settings");
            Plugin.Encounter.InCombat = false; Frame(window);
            Check((window.Flags & ImGuiWindowFlags.NoInputs) == 0, "Unlocked buddy must be draggable outside combat");
            var firstAnchor = Plugin.Config.BuddyAnchor;
            var image = ImGui.DrawList.Images.Single(); ImGui.Mouse = (image.Min + image.Max) / 2; ImGui.Hovered = true;
            ImGui.MouseDown = ImGui.MouseClicked = true; Frame(window); ImGui.Mouse += new Vector2(160, 50); Frame(window);
            Check(Plugin.Saves == 0 && Plugin.Config.BuddyAnchor == firstAnchor, "Dragging must not write config per frame");
            ImGui.MouseDown = false; Frame(window);
            Check(Plugin.Saves == 1 && Plugin.Config.BuddyAnchor != firstAnchor, "Drag release must save the creature anchor exactly once"); Frame(window);
            Check(Plugin.Saves == 1, "Idle frames must not repeat drag saves");
            Plugin.Config.BuddyUnlocked = false; ImGui.Hovered = false;
            Plugin.Buddy.Presentation = new("one", "Your call", "Use your mitigation.", "From your configured timeline", BuddyMood.Guiding, true);
            Frame(window); ImGui.Time += .2; Frame(window);
            Check(ImGui.DrawList.Texts.All(t => t.Color >> 24 == 255), "A stable cue must finish its fade rather than restart every frame");
            Plugin.Buddy.Presentation = new("two", "", "", "", BuddyMood.Uncertain, true); Frame(window);
            Plugin.Config.BuddyAnchor = new(8, -3); Plugin.Config.BuddyScale = 1.6f;
            ImGuiHelpers.MainViewport.Size = new(240, 160); ImGui.BaseFontSize = 60;
            Plugin.Buddy.Presentation = new("long", new string('H', 2000), string.Join(" ", Enumerable.Repeat("Long authored instruction", 200)), new string('D', 3000), BuddyMood.Guiding, true);
            Frame(window); ImGui.Time += .2; Frame(window);
            var min = ImGui.GetWindowPos(); var max = min + ImGui.GetWindowSize();
            Check(min.X >= 40 && min.Y >= 50 && max.X <= 280.1f && max.Y <= 210.1f, "Complete HUD must fit after resolution and large-font changes");
            Check(ImGui.DrawList.Texts.All(t => t.Position.X >= min.X && t.Position.Y >= min.Y && t.Position.Y + t.Size.Y <= max.Y + 1), "Wrapped text must fit the measured surface");
            Check(ImGui.DrawList.Texts.Any(t => t.Text.EndsWith("…")), "Extreme authored cues must have a visible truncation marker");
            Check(Plugin.TextureProvider.Loads == 1, "The shared texture reference must be cached per instance");
            Reset(); Plugin.TextureProvider.Fail = true; using var missing = new BuddyWindow(); Frame(missing); Frame(missing); Frame(missing);
            Check(Plugin.TextureProvider.Loads == 1 && Plugin.Log.Warnings == 1, "Missing artwork must fall back once without repeating warnings");
            missing.Dispose(); Frame(missing);
            Check(Plugin.TextureProvider.Loads == 1, "Disposed windows must not reload a released texture");
            Console.WriteLine("Buddy window: visibility, combat input, drag persistence, text bounds, cue fade and resource lifecycle passed.");
        }

        public static void Render(string output, string spritePath)
        {
            Reset(); ImGuiHelpers.MainViewport.Pos = Vector2.Zero; ImGuiHelpers.MainViewport.Size = new(1100, 790);
            Plugin.Config.BuddyScale = 1.45f; Plugin.Config.BuddyReducedMotion = true;
            using var bitmap = new Bitmap(1100, 790); using var graphics = Graphics.FromImage(bitmap); using var sprite = Image.FromFile(spritePath);
            graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.FromArgb(9, 9, 13)); ImGui.DrawList.G = graphics; ImGui.DrawList.Sprite = sprite;
            using var title = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
            using var label = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(243, 243, 247)); using var muted = new SolidBrush(Color.FromArgb(150, 150, 162));
            using var red = new SolidBrush(Color.FromArgb(239, 64, 84)); using var line = new Pen(Color.FromArgb(28, 28, 35));
            graphics.DrawString("EMBER", title, red, new PointF(64, 42));
            graphics.DrawString("Your optional raid buddy", label, white, new PointF(66, 85));
            var exampleTime = DateTime.UtcNow;
            var exampleEngine = new BuddyCueEngine();
            exampleEngine.Update(new BuddyContext(true, true, 1, 1, false, true, true, new object(), "preview"), exampleTime);
            exampleEngine.Arm("example-mechanic", "Grotesquerie: Act 2", exampleTime.AddSeconds(6), exampleTime);
            exampleEngine.Decide("example-mechanic", "", false, exampleTime.AddSeconds(1));
            var states = new[]
            {
                (Y: 222f, Label: "Quiet between mechanics", Note: "A little company, without the chatter.", Cue: new BuddyPresentation("", "", "", "", BuddyMood.Listening, false)),
                (Y: 420f, Label: "One useful cue", Note: "A personal call from your configured plan.", Cue: new BuddyPresentation("example-call", "Your call", "Use your mitigation.", "From your configured timeline", BuddyMood.Guiding, true)),
                (Y: 620f, Label: "Clear about uncertainty", Note: "No guessed destination.", Cue: exampleEngine.Presentation)
            };
            foreach (var state in states)
            {
                graphics.DrawLine(line, 64, state.Y - 96, 1036, state.Y - 96);
                graphics.DrawString(state.Label, label, white, new PointF(65, state.Y - 14));
                graphics.DrawString(state.Note, small, muted, new PointF(65, state.Y + 15));
                Plugin.Config.BuddyAnchor = new(.85f, state.Y / 790);
                Plugin.Buddy.Presentation = state.Cue;
                using var window = new BuddyWindow(); Frame(window);
            }
            graphics.DrawString("Illustrative cues rendered by the plugin's window through a drawing API substitute; not an in-game screenshot.", small, muted, new PointF(64, 742));
            bitmap.Save(output, ImageFormat.Png); ImGui.DrawList.G = null; ImGui.DrawList.Sprite = null;
        }
    }
}
