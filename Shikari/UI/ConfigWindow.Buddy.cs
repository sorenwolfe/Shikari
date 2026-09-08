using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;

namespace Shikari.UI;

public sealed partial class ConfigWindow
{
    private static void DrawBuddySettings(Configuration config)
    {
        ImGui.TextDisabled("Raid buddy");
        ImGui.Separator();
        var enabled = config.BuddyEnabled;
        if (ImGui.Checkbox("Enable dragon buddy", ref enabled))
        {
            config.BuddyEnabled = enabled;
            if (!enabled) config.BuddyUnlocked = false;
            Plugin.SaveConfig();
        }
        ImGui.TextWrapped("Meet Ember: a little companion who rests when you're away and perks up when you return. Your personal calls take priority during a pull.");
        if (enabled)
        {
            ImGui.TextWrapped("Uses your enabled timeline and Adaptive rules. Unknown assignments stay unknown; Ember does not solve an imported diagram on its own.");
            var unlocked = config.BuddyUnlocked;
            ImGui.BeginDisabled(Plugin.Encounter.InCombat || Plugin.Condition[ConditionFlag.InCombat]);
            if (ImGui.Checkbox("Move Ember", ref unlocked))
            { config.BuddyUnlocked = unlocked; Plugin.SaveConfig(); }
            ImGui.EndDisabled();
            ImGui.TextDisabled("Drag Ember, then turn off Move Ember to lock. Locked during combat.");
            var scale = config.BuddyScale;
            ImGui.SetNextItemWidth(190 * UiHelpers.Scale);
            if (ImGui.SliderFloat("Buddy size", ref scale, .65f, 2f, "%.2fx"))
            { config.BuddyScale = scale; Plugin.SaveConfig(); }
            var reduced = config.BuddyReducedMotion;
            if (ImGui.Checkbox("Reduce buddy motion", ref reduced))
            { config.BuddyReducedMotion = reduced; Plugin.SaveConfig(); }
            if (ImGui.SmallButton("Reset buddy position"))
            { config.BuddyAnchor = new Vector2(.76f, .70f); Plugin.SaveConfig(); }
        }
        ImGui.Spacing();
    }
}
