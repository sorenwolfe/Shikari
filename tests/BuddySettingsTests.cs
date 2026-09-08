using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
namespace Dalamud.Game.ClientState.Conditions { public enum ConditionFlag { InCombat } }
namespace Dalamud.Bindings.ImGui
{
    public static class ImGui
    {
        public static readonly List<string> Visible = new();
        public static string Action = "";
        public static float NewScale;
        public static bool Disabled;
        public static void TextDisabled(string value) { }
        public static void TextWrapped(string value) { }
        public static void Separator() { }
        public static void Spacing() { }
        public static void SetNextItemWidth(float value) { }
        public static void BeginDisabled(bool disabled) => Disabled = disabled;
        public static void EndDisabled() => Disabled = false;
        public static bool Checkbox(string label, ref bool value)
        { Visible.Add(label); if (Action != label || Disabled) return false; value = !value; Action = ""; return true; }
        public static bool SmallButton(string label)
        { Visible.Add(label); if (Action != label || Disabled) return false; Action = ""; return true; }
        public static bool SliderFloat(string label, ref float value, float min, float max, string format)
        { Visible.Add(label); if (Action != label) return false; value = Math.Clamp(NewScale, min, max); Action = ""; return true; }
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static FakeEncounter Encounter = new();
        public static FakeCondition Condition = new();
        public static int Saves;
        public static void SaveConfig() => Saves++;
    }
    public sealed class FakeEncounter { public bool InCombat; }
    public sealed class FakeCondition { public bool Combat; public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag flag] => Combat; }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale => 1; }
    public sealed partial class ConfigWindow
    {
        public static void TestSettings(Configuration config) => DrawBuddySettings(config);
    }
}
namespace Shikari.Tests
{
    public static class BuddySettingsTests
    {
        static void Check(bool value,string message) { if(!value) throw new Exception(message); }
        public static void Run()
        {
            var old=JsonConvert.DeserializeObject<Configuration>("{\"Version\":1,\"MiniPlanYourView\":true}")!;
            Check(!old.BuddyEnabled && !old.BuddyUnlocked,"Existing installations must not gain an enabled or draggable HUD");
            Check(old.MiniPlanYourView,"Old settings must remain intact");
            old.BuddyEnabled=true; old.BuddyAnchor=new Vector2(.42f,.66f); old.BuddyScale=1.25f; old.BuddyReducedMotion=true;
            var restored=JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(old))!;
            Check(restored.BuddyEnabled && restored.BuddyReducedMotion && restored.BuddyScale==1.25f && restored.BuddyAnchor==old.BuddyAnchor,"Buddy preferences must survive saving and reload");
            restored.BuddyEnabled=false;
            Check(!JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(restored))!.BuddyEnabled,"Disabled buddy stays disabled on reload");
            var settings = new Configuration();
            Dalamud.Bindings.ImGui.ImGui.Action = "Enable dragon buddy";
            UI.ConfigWindow.TestSettings(settings);
            Check(settings.BuddyEnabled && Plugin.Saves == 1, "Enable control must save the optional companion preference");
            Check(Array.TrueForAll(new[] { "Move Ember", "Buddy size", "Reduce buddy motion", "Reset buddy position" }, label => Dalamud.Bindings.ImGui.ImGui.Visible.Contains(label)), "Movement, size, motion and reset must be immediately visible without a disclosure tree");
            Dalamud.Bindings.ImGui.ImGui.Action = "Move Ember"; UI.ConfigWindow.TestSettings(settings);
            Check(settings.BuddyUnlocked && Plugin.Saves == 2, "Movement control must unlock and save outside combat");
            Plugin.Encounter.InCombat = true; Dalamud.Bindings.ImGui.ImGui.Action = "Move Ember"; UI.ConfigWindow.TestSettings(settings);
            Check(settings.BuddyUnlocked && Plugin.Saves == 2, "Move control must be unavailable in combat");
            Plugin.Encounter.InCombat = false; Plugin.Condition.Combat = true; UI.ConfigWindow.TestSettings(settings);
            Check(settings.BuddyUnlocked && Plugin.Saves == 2, "Move control must respect the native combat condition too");
            Plugin.Condition.Combat = false; Dalamud.Bindings.ImGui.ImGui.Action = "Buddy size"; Dalamud.Bindings.ImGui.ImGui.NewScale = 2;
            UI.ConfigWindow.TestSettings(settings); Check(settings.BuddyScale == 2, "Size slider must permit the larger two-times size");
            settings.BuddyAnchor = Vector2.Zero; Dalamud.Bindings.ImGui.ImGui.Action = "Reset buddy position"; UI.ConfigWindow.TestSettings(settings);
            Check(settings.BuddyAnchor == new Vector2(.76f, .70f), "Reset must restore the default creature anchor");
            Dalamud.Bindings.ImGui.ImGui.Action = "Enable dragon buddy"; UI.ConfigWindow.TestSettings(settings);
            Check(!settings.BuddyEnabled && !settings.BuddyUnlocked, "Disabling the companion also exits move mode");
            Console.WriteLine("PASS: real configuration legacy defaults, buddy opt-in, placement, scale, reduced motion and disabling round trip");
        }
    }
}
