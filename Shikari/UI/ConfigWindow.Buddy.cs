using System.Numerics;
using Dalamud.Bindings.ImGui;

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
        ImGui.TextWrapped("Meet Ember: a quiet little companion for your personal calls and detected assignments. Appears while a plan is active.");
        if (enabled)
        {
            ImGui.TextWrapped("Uses your enabled timeline and Adaptive rules. Unknown assignments stay unknown; Ember does not solve an imported diagram on its own.");
            if (ImGui.TreeNode("Appearance and placement##buddy-settings"))
            {
                var unlocked = config.BuddyUnlocked;
                ImGui.BeginDisabled(Plugin.Encounter.InCombat);
                if (ImGui.Checkbox("Unlock buddy to move", ref unlocked))
                { config.BuddyUnlocked = unlocked; Plugin.SaveConfig(); }
                ImGui.EndDisabled();
                ImGui.TextDisabled("Drag outside combat. During a pull, clicks pass through.");
                var scale = config.BuddyScale;
                ImGui.SetNextItemWidth(190 * UiHelpers.Scale);
                if (ImGui.SliderFloat("Buddy size", ref scale, .65f, 1.6f, "%.2fx"))
                { config.BuddyScale = scale; Plugin.SaveConfig(); }
                var reduced = config.BuddyReducedMotion;
                if (ImGui.Checkbox("Reduce buddy motion", ref reduced))
                { config.BuddyReducedMotion = reduced; Plugin.SaveConfig(); }
                if (ImGui.SmallButton("Reset buddy position"))
                { config.BuddyAnchor = new Vector2(.76f, .70f); Plugin.SaveConfig(); }
                ImGui.TreePop();
            }
        }
        ImGui.Spacing();
    }
}
