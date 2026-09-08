using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.IO;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Buddy;

namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag { WatchingCutscene, WatchingCutscene78, OccupiedInCutSceneEvent, BetweenAreas, BetweenAreas51, LoggingOut, InCombat }
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
        public readonly List<(Vector2 A, Vector2 B, Vector2 C, Vector2 D)> Quads = new();
        public readonly List<object> Commands = new();
        public int RectFills;
        private static Color C(uint c) => Color.FromArgb((int)(c >> 24), (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255));
        private static PointF P(Vector2 p) => new(p.X, p.Y);
        public void Clear() { Texts.Clear(); Images.Clear(); Quads.Clear(); Commands.Clear(); RectFills = 0; }
        private static GraphicsPath Rounded(Vector2 min, Vector2 max, float r)
        {
            var p = new GraphicsPath(); var d = Math.Min(r * 2, Math.Min(max.X - min.X, max.Y - min.Y));
            if (d < .1f) { p.AddRectangle(new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y)); return p; }
            p.AddArc(min.X, min.Y, d, d, 180, 90); p.AddArc(max.X - d, min.Y, d, d, 270, 90);
            p.AddArc(max.X - d, max.Y - d, d, d, 0, 90); p.AddArc(min.X, max.Y - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        public void AddRectFilled(Vector2 min, Vector2 max, uint c, float r)
        { RectFills++; if (G == null) return; using var b = new SolidBrush(C(c)); using var p = Rounded(min, max, r); G.FillPath(b, p); }
        public void AddRect(Vector2 min, Vector2 max, uint c, float r, ImDrawFlags f, float width)
        { if (G == null) return; using var pen = new Pen(C(c), width); using var p = Rounded(min, max, r); G.DrawPath(pen, p); }
        public void AddCircleFilled(Vector2 p, float r, uint c, int segments)
        { if (G == null) return; using var b = new SolidBrush(C(c)); G.FillEllipse(b, p.X - r, p.Y - r, r * 2, r * 2); }
        public void AddLine(Vector2 a, Vector2 b, uint c, float width)
        { Commands.Add(new { kind = "line", a = new[] { a.X, a.Y }, b = new[] { b.X, b.Y }, color = c, width }); if (G == null) return; using var pen = new Pen(C(c), width); G.DrawLine(pen, P(a), P(b)); }
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
        public void AddImageQuad(ImTextureID id, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, uint tint)
        {
            var min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)); var max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            Images.Add((min, max, uvA)); Quads.Add((a, b, c, d));
            Commands.Add(new { kind = "image", a = new[] { a.X, a.Y }, b = new[] { b.X, b.Y }, d = new[] { d.X, d.Y }, uvA = new[] { uvA.X, uvA.Y }, uvC = new[] { uvC.X, uvC.Y } });
            if (G == null || Sprite == null) return;
            var sourceMin = Vector2.Min(uvA, uvC); var sourceMax = Vector2.Max(uvA, uvC);
            var destination = uvA.X > uvB.X ? new[] { P(b), P(a), P(c) } : new[] { P(a), P(b), P(d) };
            G.DrawImage(Sprite, destination, new RectangleF(sourceMin.X * Sprite.Width, sourceMin.Y * Sprite.Height,
                (sourceMax.X - sourceMin.X) * Sprite.Width, (sourceMax.Y - sourceMin.Y) * Sprite.Height), GraphicsUnit.Pixel);
        }
        public void AddText(ImFontPtr font, float size, Vector2 at, uint c, string text, float wrapWidth)
        {
            var measured = ImGui.Measure(text, wrapWidth, size);
            Texts.Add((text, at, measured, c));
            Commands.Add(new { kind = "text", text, x = at.X, y = at.Y, size, color = c });
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
    public sealed class FakeBuddy
    {
        public BuddyPresentation Presentation = new("", "", "", "", BuddyMood.Resting, false);
        public BuddyAmbientPresentation Ambient = new(BuddyAmbientState.Idle, DateTime.UtcNow);
    }
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
            foreach (var flag in Enum.GetValues<ConditionFlag>().Where(flag => flag != ConditionFlag.InCombat))
            { Plugin.Condition.Active.Add(flag); Check(!window.DrawConditions(), "Buddy must hide for every cutscene flag"); Plugin.Condition.Active.Clear(); }
            Plugin.Plans.Active = null; Check(window.DrawConditions(), "Companion presence remains available with no active plan"); Plugin.Plans.Active = new();
            Frame(window); Check(ImGui.DrawList.Texts.Count == 0, "Idle creature must not generate a permanent instruction bubble");
            Check(ImGui.DrawList.RectFills == 0 && ImGui.DrawList.Images.Count == 1, "Idle HUD must draw the alpha sprite without any portrait fill or pod");
            Check(ImGui.DrawList.Images[0].Max.Y - ImGui.DrawList.Images[0].Min.Y > 117, "Existing saved scale should show the larger companion");
            Plugin.Config.BuddyReducedMotion = true;
            foreach (var state in Enum.GetValues<BuddyAmbientState>())
            {
                Plugin.Buddy.Ambient = new(state, DateTime.UtcNow.AddSeconds(-.5)); Frame(window);
                var expected = BuddyMotion.Uvs(state, true).Min;
                Check(ImGui.DrawList.Images.Single().Uv == expected && ImGui.DrawList.Texts.Count == 0 && ImGui.DrawList.Commands.Count == 1,
                    "Reduced motion selects each static pose without particles or an ambient speech bubble");
            }
            Plugin.Config.BuddyReducedMotion = false;
            Plugin.Buddy.Ambient = new(BuddyAmbientState.Sleeping, DateTime.UtcNow.AddSeconds(-2)); Frame(window);
            Check(ImGui.DrawList.Texts.All(t => t.Text == "z") && ImGui.DrawList.Texts.Count == 2, "Sleep adds only quiet Zs, not tactical instructions");
            Plugin.Buddy.Presentation = new("priority", "Your call", "Use your mitigation.", "", BuddyMood.Guiding, true); Frame(window);
            Check(ImGui.DrawList.Images.Single().Uv == BuddyMotion.Uvs(BuddyAmbientState.Focused, true).Min && ImGui.DrawList.Texts.All(t => t.Text != "z"), "Tactical cues suppress sleeping and welcome particles");
            Plugin.Buddy.Presentation = new("", "", "", "", BuddyMood.Resting, false); Plugin.Buddy.Ambient = new(BuddyAmbientState.Idle, DateTime.UtcNow);
            Plugin.Config.BuddyUnlocked = true; Plugin.Encounter.InCombat = true;
            Frame(window); Check((window.Flags & ImGuiWindowFlags.NoInputs) != 0, "Combat must always override unlocked settings");
            Plugin.Encounter.InCombat = false; Frame(window);
            Check((window.Flags & ImGuiWindowFlags.NoInputs) == 0, "Unlocked buddy must be draggable outside combat");
            Plugin.Condition.Active.Add(ConditionFlag.InCombat); Frame(window);
            Check((window.Flags & ImGuiWindowFlags.NoInputs) != 0, "Native combat condition must lock input before encounter tracking catches up");
            Plugin.Condition.Active.Clear(); Frame(window);
            var firstAnchor = Plugin.Config.BuddyAnchor;
            var image = ImGui.DrawList.Images.Single(); ImGui.Mouse = (image.Min + image.Max) / 2; ImGui.Hovered = true;
            ImGui.MouseDown = ImGui.MouseClicked = true; Frame(window); ImGui.Mouse += new Vector2(160, 50); Frame(window);
            Check(Plugin.Saves == 0 && Plugin.Config.BuddyAnchor == firstAnchor, "Dragging must not write config per frame");
            ImGui.MouseDown = false; Frame(window);
            Check(Plugin.Saves == 1 && Plugin.Config.BuddyAnchor != firstAnchor, "Drag release must save the creature anchor exactly once"); Frame(window);
            Check(Plugin.Saves == 1, "Idle frames must not repeat drag saves");
            var placed = (BuddyLayout.Placement)typeof(BuddyWindow).GetField("placement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var beforeMotion = placed.Position + placed.SpriteMin; ImGui.Time += 1.37; Frame(window);
            var afterMotion = (BuddyLayout.Placement)typeof(BuddyWindow).GetField("placement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(Vector2.Distance(beforeMotion, afterMotion.Position + afterMotion.SpriteMin) < .001f, "Animated poses must not move the fixed drag target");
            ImGui.Mouse = afterMotion.Position + afterMotion.SpriteMin + new Vector2(afterMotion.SpriteSize / 2, afterMotion.SpriteSize + 9);
            ImGui.MouseClicked = ImGui.MouseDown = true; Frame(window); ImGui.Mouse += new Vector2(-60, 20); Frame(window);
            ImGui.MouseDown = false; Frame(window); Check(Plugin.Saves == 2, "The visible movement grip itself must be draggable");
            Plugin.Config.BuddyUnlocked = false; ImGui.Hovered = false;
            Plugin.Buddy.Presentation = new("one", "Your call", "Use your mitigation.", "From your configured timeline", BuddyMood.Guiding, true);
            Frame(window); ImGui.Time += .2; Frame(window);
            Check(ImGui.DrawList.Texts.All(t => t.Color >> 24 == 255), "A stable cue must finish its fade rather than restart every frame");
            Plugin.Buddy.Presentation = new("two", "", "", "", BuddyMood.Uncertain, true); Frame(window);
            Plugin.Config.BuddyAnchor = new(8, -3); Plugin.Config.BuddyScale = 2f;
            ImGuiHelpers.MainViewport.Size = new(240, 160); ImGui.BaseFontSize = 60;
            Plugin.Buddy.Presentation = new("long", new string('H', 2000), string.Join(" ", Enumerable.Repeat("Long authored instruction", 200)), new string('D', 3000), BuddyMood.Guiding, true);
            Frame(window); ImGui.Time += .2; Frame(window);
            var min = ImGui.GetWindowPos(); var max = min + ImGui.GetWindowSize();
            Check(min.X >= 40 && min.Y >= 50 && max.X <= 280.1f && max.Y <= 210.1f, "Complete HUD must fit after resolution and large-font changes");
            Check(ImGui.DrawList.Texts.All(t => t.Position.X >= min.X && t.Position.Y >= min.Y && t.Position.Y + t.Size.Y <= max.Y + 1), "Wrapped text must fit the measured surface");
            Check(ImGui.DrawList.Texts.Any(t => t.Text.EndsWith("…")), "Extreme authored cues must have a visible truncation marker");
            Check(ImGui.DrawList.Quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }).All(p => p.X >= 40 && p.X <= 280 && p.Y >= 50 && p.Y <= 210), "All animated sprite corners must remain inside the resized viewport");
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
            Plugin.Config.BuddyScale = 1.4f;
            using var bitmap = new Bitmap(1100, 790); using var graphics = Graphics.FromImage(bitmap); using var sprite = Image.FromFile(spritePath);
            graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var backdrop = new LinearGradientBrush(new Rectangle(0, 0, 1100, 790), Color.FromArgb(17, 33, 42), Color.FromArgb(25, 17, 29), 20);
            graphics.FillRectangle(backdrop, 0, 0, 1100, 790); ImGui.DrawList.G = graphics; ImGui.DrawList.Sprite = sprite;
            using var title = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
            using var label = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(243, 243, 247)); using var muted = new SolidBrush(Color.FromArgb(150, 150, 162));
            using var red = new SolidBrush(Color.FromArgb(239, 64, 84)); using var line = new Pen(Color.FromArgb(28, 28, 35));
            graphics.DrawString("EMBER", title, red, new PointF(64, 42));
            graphics.DrawString("A little more life. Room to breathe.", label, white, new PointF(66, 85));
            var moods = new[] { BuddyAmbientState.Idle, BuddyAmbientState.Sleeping, BuddyAmbientState.Welcoming, BuddyAmbientState.Focused };
            var names = new[] { "Keeping company", "Away for a moment", "Welcome back", "Ready for the pull" };
            for (var i = 0; i < moods.Length; i++)
            {
                var x = 148 + i * 270;
                graphics.DrawString(names[i], label, white, new PointF(x - 82, 170));
                Plugin.Config.BuddyAnchor = new(x / 1100f, .365f);
                Plugin.Buddy.Ambient = new(moods[i], DateTime.UtcNow.AddSeconds(-.7));
                ImGui.Time = 13.5;
                using var ambientWindow = new BuddyWindow(); Frame(ambientWindow);
            }
            graphics.DrawLine(line, 64, 425, 1036, 425);
            var exampleTime = DateTime.UtcNow;
            var exampleEngine = new BuddyCueEngine();
            exampleEngine.Update(new BuddyContext(true, true, 1, 1, false, true, true, new object(), "preview"), exampleTime);
            exampleEngine.Arm("example-mechanic", "Grotesquerie: Act 2", exampleTime.AddSeconds(6), exampleTime);
            exampleEngine.Decide("example-mechanic", "", false, exampleTime.AddSeconds(1));
            graphics.DrawString("Your mechanic comes first", label, white, new PointF(65, 494));
            graphics.DrawString("No guessed destination. No ambient chatter in your call.", small, muted, new PointF(65, 524));
            graphics.DrawString("Move Ember, adjust size, or reduce motion in Settings.", small, muted, new PointF(65, 567));
            graphics.DrawString("Transparent artwork: no portrait box or background.", small, muted, new PointF(65, 596));
            Plugin.Config.BuddyAnchor = new(.87f, .72f);
            Plugin.Config.BuddyReducedMotion = true;
            Plugin.Buddy.Presentation = exampleEngine.Presentation;
            using var cueWindow = new BuddyWindow(); Frame(cueWindow);
            graphics.DrawString("Actual companion drawing methods through a drawing API substitute; illustrative scene, not an in-game screenshot.", small, muted, new PointF(64, 742));
            bitmap.Save(output, ImageFormat.Png); ImGui.DrawList.G = null; ImGui.DrawList.Sprite = null;
        }

        public static void RenderAnimation(string output, string spritePath)
        {
            var variants = new List<object>();
            foreach (var reduced in new[] { false, true })
            {
                Reset(); ImGuiHelpers.MainViewport.Pos = Vector2.Zero; ImGuiHelpers.MainViewport.Size = new(1100, 620);
                Plugin.Config.BuddyScale = 2; Plugin.Config.BuddyAnchor = new(.5f, .52f); Plugin.Config.BuddyReducedMotion = reduced;
                using var window = new BuddyWindow(); var frames = new List<object>();
                for (var i = 0; i < 240; i++)
                {
                    var seconds = i / 20.0;
                    var state = seconds < 3 ? BuddyAmbientState.Idle : seconds < 6 ? BuddyAmbientState.Sleeping : seconds < 8.5 ? BuddyAmbientState.Welcoming : BuddyAmbientState.Focused;
                    var age = seconds - (state == BuddyAmbientState.Idle ? 0 : state == BuddyAmbientState.Sleeping ? 3 : state == BuddyAmbientState.Welcoming ? 6 : 8.5);
                    Plugin.Buddy.Ambient = new(state, DateTime.UtcNow.AddSeconds(-age)); ImGui.Time = seconds;
                    Frame(window); frames.Add(new { state = state.ToString(), commands = ImGui.DrawList.Commands.ToArray() });
                }
                variants.Add(frames);
            }
            var template = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Ember · Presence preview</title>
<style>*{box-sizing:border-box}body{margin:0;background:#0d1119;color:#edf1f9;font:16px system-ui,sans-serif}main{max-width:1100px;margin:auto;padding:32px}h1{letter-spacing:.16em;font-size:24px;color:#fc536a}p{color:#a6b1c1;line-height:1.6}canvas{display:block;width:100%;border-radius:18px;background:radial-gradient(ellipse at 48% 58%,#2f475166,transparent 50%),linear-gradient(120deg,#162c39,#261725);border:1px solid #ffffff0d}nav{display:flex;gap:24px;align-items:center;margin:16px 0}button{border:1px solid #ffffff2b;color:#eef2fa;background:#252b37;padding:8px 16px;border-radius:8px;cursor:pointer}input{accent-color:#ee4560}#state{color:#f6cda6}small{color:#8796aa}</style>
<main><h1>EMBER</h1><p>A companion with a little personality. The real window's drawing commands drive this preview.</p><canvas width="1100" height="620" aria-label="Animated Ember companion preview"></canvas><nav><button id="pause">Pause</button><label><input type="checkbox" id="reduced"> Reduce motion</label><strong id="state"></strong></nav><small>Idle → away → welcome back → focused. Demonstration timings only; the plugin uses detected AFK and duty state. This is a drawing API substitute, not an in-game capture.</small></main>
<script>const variants=__FRAMES__,atlas=new Image();atlas.src='data:image/png;base64,__ATLAS__';const canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d'),reduced=document.querySelector('#reduced'),state=document.querySelector('#state');let playing=true,clock=0,last=performance.now();const rgba=c=>`rgba(${c&255},${(c>>>8)&255},${(c>>>16)&255},${(c>>>24)/255})`;function paint(now){if(playing)clock+=(now-last)/1000;last=now;const f=variants[reduced.checked?1:0][Math.floor(clock*20)%240];ctx.clearRect(0,0,1100,620);state.textContent=f.state;for(const c of f.commands){ctx.save();if(c.kind==='image'){let a=c.a,b=c.b,d=c.d;if(c.uvA[0]>c.uvC[0]){d=[b[0]+d[0]-a[0],b[1]+d[1]-a[1]];[a,b]=[b,a]}ctx.transform(b[0]-a[0],b[1]-a[1],d[0]-a[0],d[1]-a[1],a[0],a[1]);ctx.drawImage(atlas,Math.min(c.uvA[0],c.uvC[0])*atlas.width,c.uvA[1]*atlas.height,Math.abs(c.uvC[0]-c.uvA[0])*atlas.width,(c.uvC[1]-c.uvA[1])*atlas.height,0,0,1,1)}else if(c.kind==='line'){ctx.strokeStyle=rgba(c.color);ctx.lineWidth=c.width;ctx.beginPath();ctx.moveTo(...c.a);ctx.lineTo(...c.b);ctx.stroke()}else if(c.kind==='text'){ctx.fillStyle=rgba(c.color);ctx.font=`${c.size*.75}px Segoe UI,sans-serif`;ctx.textBaseline='top';ctx.fillText(c.text,c.x,c.y)}ctx.restore()}requestAnimationFrame(paint)}atlas.onload=()=>requestAnimationFrame(paint);document.querySelector('#pause').onclick=e=>{playing=!playing;e.target.textContent=playing?'Pause':'Play'};</script></html>
""";
            File.WriteAllText(output, template.Replace("__FRAMES__", JsonSerializer.Serialize(variants)).Replace("__ATLAS__", Convert.ToBase64String(File.ReadAllBytes(spritePath))));
        }
    }
}
