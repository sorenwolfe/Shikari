using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Shikari.UI;
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
    public static class ImGuiHelpers { public static ImGuiViewportPtr MainViewport => ImGui.GetMainViewport(); }
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
namespace Shikari
{
    public static class Plugin
    {
        public static FakeConfig Config = new();
        public static FakeClient ClientState = new();
        public static FakeCondition Condition = new();
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
    public sealed class FakeEncounter { public bool InCombat; }
    public sealed class FakeBuddy
    {
        public BuddyPresentation Presentation = new("", "", "", "", BuddyMood.Resting, false);
        public BuddyAmbientPresentation Ambient = new(BuddyAmbientState.Idle, DateTime.UtcNow);
    }
    public sealed class FakeProvider
    {
        public Dalamud.Interface.Textures.ISharedImmediateTexture GetFromManifestResource(Assembly a, string name)
            => new Dalamud.Interface.Textures.FakeTexture();
    }
    public sealed class FakeLog { public int Warnings; public void Warning(Exception e, string message) => Warnings++; }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale = 1; public static void Tooltip(string text) { } }
}

namespace Shikari.Tests
{
    /// <summary>Real Dalamud ImGui input and native frame lifecycle around production BuddyWindow.
    /// Only game services, shared textures and the Window host shell are substituted.</summary>
    public static unsafe class BuddyNativeInputTests
    {
        private static bool hovered, captured, clicked, down;
        private static int vertices;
        private static int checks;
        private static BuddyLayout.Placement Placement(BuddyWindow window) =>
            (BuddyLayout.Placement)typeof(BuddyWindow).GetField("placement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        private static bool Dragging(BuddyWindow window) =>
            (bool)typeof(BuddyWindow).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        private static Vector2 Center(BuddyWindow window)
        {
            var p = Placement(window);
            return p.Position + p.SpriteMin + new Vector2(p.SpriteSize / 2);
        }
        private static void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }
        private static void Frame(BuddyWindow window, Vector2 mouse, bool held = false, bool cover = false)
        {
            var io = ImGui.GetIO();
            io.AddMousePosEvent(mouse.X, mouse.Y);
            io.AddMouseButtonEvent(0, held);
            ImGui.NewFrame();
            hovered = false;
            captured = io.WantCaptureMouse;
            clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            if (window.DrawConditions())
            {
                window.PreDraw();
                if (ImGui.Begin("##shikari-buddy", window.Flags))
                {
                    hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                    window.Draw();
                    vertices = ImGui.GetWindowDrawList().VtxBuffer.Size;
                }
                ImGui.End();
                window.PostDraw();
            }
            if (cover)
            {
                ImGui.SetNextWindowPos(new Vector2(0, 0));
                ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("Cover", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                ImGui.SetWindowFocus();
                ImGui.End();
            }
            ImGui.Render();
        }
        private static void Warm(BuddyWindow window)
        {
            Frame(window, new Vector2(10));
            Frame(window, Center(window));
            Frame(window, Center(window));
        }
        public static void Run(string nativeLibrary)
        {
            var handle = NativeLibrary.Load(nativeLibrary);
            NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, (name, assembly, path) => handle);
            ImGui.CreateContext();
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(1920, 1080);
            io.DeltaTime = 1f / 60;
            io.IniFilename = null;
            io.Fonts.AddFontDefault();
            Check(io.Fonts.Build(), "Native font atlas builds");
            Console.WriteLine("Native ImGui version: " + ImGui.GetVersion());
            Console.WriteLine($"Native flags: NoInputs={(int)ImGuiWindowFlags.NoInputs}, NoNav={(int)ImGuiWindowFlags.NoNav}, NoMouseInputs={(int)ImGuiWindowFlags.NoMouseInputs}");
            try
            {
                Plugin.Config.BuddyEnabled = Plugin.Config.BuddyUnlocked = true;
                Plugin.Config.BuddyReducedMotion = true;
                using var window = new BuddyWindow();
                Warm(window);
                var unlockedVertices = vertices;
                Plugin.Config.BuddyUnlocked = false;
                Warm(window);
                Check(unlockedVertices > vertices, "Native draw list includes the movement grip only while unlocked");
                Plugin.Config.BuddyUnlocked = true;
                Warm(window);
                var start = Center(window);
                Frame(window, start, true);
                Check(hovered && clicked && down && Dragging(window), "First native mouse-down starts production drag");
                Check(captured, "Native ImGui captures mouse when clicking Ember");
                Frame(window, start + new Vector2(-300, -150), true);
                Check(Vector2.Distance(Center(window), start + new Vector2(-300, -150)) < 1, "Native drag follows cursor without requiring a second click");
                Check(captured && Dragging(window), "Native capture persists while mouse moves beyond previous window bounds");
                Check(Plugin.Saves == 0, "Dragging does not save each frame");
                Frame(window, start + new Vector2(-300, -150));
                Check(!Dragging(window) && Plugin.Saves == 1, "Native release commits exactly once");
                Frame(window, Center(window));
                Check(Plugin.Saves == 1, "Idle native frame does not repeat save");

                var gripPlacement = Placement(window);
                var grip = gripPlacement.Position + gripPlacement.SpriteMin + new Vector2(gripPlacement.SpriteSize / 2, gripPlacement.SpriteSize + 9);
                Frame(window, grip);
                Frame(window, grip, true);
                Check(Dragging(window), "Native click on movement grip starts drag");
                Frame(window, grip + new Vector2(-100, 50), true);
                Frame(window, grip + new Vector2(-100, 50));
                Check(Plugin.Saves == 2, "Native grip drag commits on release");

                Frame(window, Center(window));
                Frame(window, Center(window), true);
                Frame(window, Center(window) + new Vector2(100, 0), true);
                var committed = Plugin.Config.BuddyAnchor;
                Plugin.Condition.Active.Add(ConditionFlag.InCombat);
                Frame(window, Center(window), true);
                Check(!Dragging(window) && (window.Flags & ImGuiWindowFlags.NoMouseInputs) != 0, "Native combat condition cancels an active drag and locks mouse input");
                Frame(window, Center(window));
                Check(Plugin.Config.BuddyAnchor == committed && Plugin.Saves == 2, "Combat cancellation preserves last committed anchor without saving");
                Plugin.Condition.Active.Clear();
                Warm(window);

                Frame(window, Center(window), true);
                Frame(window, new Vector2(-5000, -5000), true);
                var bounds = Placement(window);
                Check(bounds.Position.X >= 0 && bounds.Position.Y >= 0, "Native off-screen drag clamps top and left bounds");
                Frame(window, new Vector2(5000, 5000), true);
                bounds = Placement(window);
                Check(bounds.Position.X + bounds.Size.X <= 1920.1f && bounds.Position.Y + bounds.Size.Y <= 1080.1f, "Native off-screen drag clamps right and bottom bounds");
                Frame(window, new Vector2(5000, 5000));
                Check(Plugin.Saves == 3 && Plugin.Config.BuddyAnchor.X is >= 0 and <= 1 && Plugin.Config.BuddyAnchor.Y is >= 0 and <= 1, "Native off-screen release saves a bounded normalized anchor");

                Warm(window);
                Frame(window, Center(window), false, true);
                Frame(window, Center(window), false, true);
                Frame(window, Center(window), true, true);
                Check(!hovered && !Dragging(window), "A covering native ImGui window prevents starting a drag through it");
                Frame(window, Center(window), false, true);
                Check(Plugin.Saves == 3, "Blocked click does not commit an anchor");
                Plugin.Config.BuddyUnlocked = false;
                Frame(window, Center(window));
                Frame(window, Center(window));
                Frame(window, Center(window), true);
                Check(!Dragging(window) && !hovered && !captured, "Locked Ember is mouse transparent in native ImGui");
                Console.WriteLine($"Native production-window input checks passed: {checks}");
            }
            finally { ImGui.DestroyContext(); }
        }
    }
}
