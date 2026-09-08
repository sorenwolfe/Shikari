using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Shikari.Model;
using Shikari.Services;
using Shikari.UI.Theme;

namespace Shikari.UI;

/// <summary>The planner: slides, timeline, roster and sharing, all in one window.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly ArenaCanvas canvas = new();

    private int slideIndex;

    /// <summary>Slide the planner is on. The mini window mirrors this so the two never disagree.</summary>
    public int SlideIndex => slideIndex;

    private bool dirty;
    private DateTime lastSaveUtc = DateTime.UtcNow;

    private SlideChangeReason lastAutoChange;
    private DateTime lastAutoChangeUtc = DateTime.MinValue;

    private int cachedCodeLength;
    private string codeLengthPlanId = string.Empty;
    private DateTime codeLengthCheckedUtc = DateTime.MinValue;

    private string importBuffer = string.Empty;
    private string importStatus = string.Empty;
    private bool importStatusIsError;

    public MainWindow()
        : base("Shikari###shikari-main", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 560),
            MaximumSize = new Vector2(4000, 3000),
        };

        Size = new Vector2(1100, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        Plugin.Encounter.CombatStarted += CancelAssignmentCheck;
    }

    private ThemeScope theme;

    public override void PreDraw() => theme = ThemeScope.Push();

    public override void PostDraw() => theme.Dispose();

    private PlanDocument? Plan => Plugin.Plans.Active;

    private Slide? CurrentSlide
    {
        get
        {
            var plan = Plan;
            if (plan == null || plan.Slides.Count == 0)
                return null;

            slideIndex = Math.Clamp(slideIndex, 0, plan.Slides.Count - 1);
            return plan.Slides[slideIndex];
        }
    }

    /// <summary>Jumps the editor to a slide by id.</summary>
    public void ShowSlide(string slideId)
    {
        var plan = Plan;
        if (plan == null)
            return;

        var index = plan.IndexOfSlide(slideId);
        if (index >= 0)
            slideIndex = index;
    }

    /// <summary>The slide director asking for a particular slide during a pull.</summary>
    public void OnDirectedSlide(string slideId, SlideChangeReason reason)
    {
        var plan = Plan;
        if (plan == null)
            return;

        var index = plan.IndexOfSlide(slideId);
        if (index < 0 || index == slideIndex)
            return;

        slideIndex = index;
        canvas.Select(null);
        lastAutoChange = reason;
        lastAutoChangeUtc = DateTime.UtcNow;
    }

    /// <summary>The slide director asking to go back to the top — a fresh pull, or a wipe.</summary>
    public void OnDirectedReset(SlideChangeReason reason)
    {
        if (slideIndex == 0)
        {
            lastAutoChange = reason;
            lastAutoChangeUtc = DateTime.UtcNow;
            return;
        }

        slideIndex = 0;
        canvas.Select(null);
        lastAutoChange = reason;
        lastAutoChangeUtc = DateTime.UtcNow;
    }

    public void ResetToFirstSlide()
    {
        slideIndex = 0;
        canvas.Select(null);
    }

    /// <summary>
    /// Moves the slide because a person asked for it, which also parks the automation briefly
    /// so it does not pull them straight back.
    /// </summary>
    public void StepSlide(int delta)
    {
        var plan = Plan;
        if (plan == null || plan.Slides.Count == 0)
            return;

        var target = Math.Clamp(slideIndex + delta, 0, plan.Slides.Count - 1);
        if (target == slideIndex)
            return;

        slideIndex = target;
        Plugin.Director.NotifyManualChange();
    }

    /// <summary>Selecting a slide by hand in the planner counts as taking the wheel.</summary>
    private void SelectSlideManually(int index)
    {
        if (index == slideIndex)
            return;

        slideIndex = index;
        canvas.Select(null);

        if (Plugin.Encounter.InCombat)
            Plugin.Director.NotifyManualChange();
    }

    private void MarkDirty() { dirty = true; editOccurred = true; InvalidateAssignmentCoverage(); }

    public override void Update()
    {
        AdvanceAssignmentCheck(Plan);
        // Autosave a couple of seconds after the last edit so a crash never costs much.
        if (!dirty)
            return;

        if ((DateTime.UtcNow - lastSaveUtc).TotalSeconds < 2)
            return;

        lastSaveUtc = DateTime.UtcNow;
        if (Plugin.Plans.SaveActive())
        {
            Plugin.SaveConfig();
            dirty = false;
        }
    }

    public override void OnClose()
    {
        CancelAssignmentCheck();
        if (!dirty)
            return;

        if (Plugin.Plans.SaveActive())
        {
            Plugin.SaveConfig();
            dirty = false;
        }
    }

    public override void Draw()
    {
        PollWtfDig();
        PollImport();
        // Sits on the background list so it lands under the window's own fill.
        if (Plugin.Config.ThemeEnabled && Plugin.Config.ThemeShadows)
        {
            var pos = ImGui.GetWindowPos();
            Sprites.Shadow(ImGui.GetBackgroundDrawList(), pos, pos + ImGui.GetWindowSize(),
                18f * UiHelpers.Scale, 0.45f);
        }

        var plan = Plan;
        if (plan == null)
        {
            ImGui.TextUnformatted("No plan is loaded.");
            if (ImGui.Button("Create one", Vector2.Zero))
                Plugin.Plans.CreateNew("New plan");
            return;
        }

        BeginPlanEditFrame(plan);
        DrawWorkspace(plan);
        EndPlanEditFrame(plan);
    }

    /// <summary>
    /// A tab with its icon in front of the label. The icon and the label are separate glyph runs
    /// because they come from different fonts, so they are drawn as one string only when the icon
    /// font is actually there — otherwise the label would be prefixed with a stray box.
    /// </summary>
    private static bool Tab(FontAwesomeIcon icon, string label)
    {
        if (!Plugin.Config.ThemeToolIcons)
            return ImGui.BeginTabItem(label, ImGuiTabItemFlags.None);

        if (!Plugin.Fonts.MixedAvailable)
            return ImGui.BeginTabItem(label, ImGuiTabItemFlags.None);

        // The id stays put whichever way the tab is drawn, so switching the setting doesn't
        // reset which tab is open.
        var pushed = Plugin.Fonts.PushMixed();
        try
        {
            return ImGui.BeginTabItem(
                $"{ThemeFonts.Glyph(icon)}  {label}###tab-{label}", ImGuiTabItemFlags.None);
        }
        finally
        {
            pushed?.Dispose();
        }
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader(PlanDocument plan)
    {
        // Two rows: the plan's identity, then the controls. One row overflowed at the window's
        // own minimum width.
        var avail = ImGui.GetContentRegionAvail().X;
        var nameWidth = Math.Clamp(avail * 0.34f, 140 * UiHelpers.Scale, 320 * UiHelpers.Scale);
        var fightWidth = Math.Clamp(avail * 0.34f, 140 * UiHelpers.Scale, 320 * UiHelpers.Scale);

        ImGui.SetNextItemWidth(nameWidth);
        var name = plan.Name;
        if (UiHelpers.InputTextHint("##plan-name", "Plan name", ref name, 128))
        {
            plan.Name = name;
            MarkDirty();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(fightWidth);
        var encounter = plan.Encounter;
        if (UiHelpers.InputTextHint("##plan-encounter", "Fight", ref encounter, 128))
        {
            plan.Encounter = encounter;
            MarkDirty();
        }

        if (ImGui.Button("Plans", Vector2.Zero))
            ImGui.OpenPopup("##plan-list", ImGuiPopupFlags.None);

        DrawPlanListPopup(plan);

        ImGui.SameLine();
        var team = Plugin.Config.GetActiveTeam();
        ImGui.SetNextItemWidth(Math.Clamp(avail * 0.2f, 110 * UiHelpers.Scale, 200 * UiHelpers.Scale));
        if (ImGui.BeginCombo("##team", team.Name, ImGuiComboFlags.None))
        {
            foreach (var profile in Plugin.Config.Teams)
            {
                if (ImGui.Selectable(profile.Name + "##" + profile.Id, profile.Id == team.Id, ImGuiSelectableFlags.None, Vector2.Zero))
                {
                    Plugin.Config.ActiveTeamId = profile.Id;
                    Plugin.SaveConfig();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            UiHelpers.Tooltip("Active team profile. Each team keeps its own call wording and delivery settings.");

        ImGui.SameLine();
        var remindersOn = Plugin.Config.RemindersEnabled;
        if (ImGui.Checkbox("Calls", ref remindersOn))
        {
            Plugin.Config.RemindersEnabled = remindersOn;
            Plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Live shotcalls on or off.");

        ImGui.SameLine();
        if (ImGui.Button("Settings", Vector2.Zero))
            ConfigWindow.RequestOpen = true;
    }

    private void DrawPlanListPopup(PlanDocument current)
    {
        if (!ImGui.BeginPopup("##plan-list", ImGuiWindowFlags.None))
            return;

        ImGui.TextDisabled("Saved plans");
        ImGui.Separator();

        foreach (var doc in Plugin.Plans.Ordered.ToList())
        {
            var label = string.IsNullOrWhiteSpace(doc.Encounter)
                ? doc.Name
                : $"{doc.Name}  —  {doc.Encounter}";

            if (ImGui.Selectable(label + "##" + doc.Id, doc.Id == current.Id, ImGuiSelectableFlags.None, Vector2.Zero))
            {
                if (Plugin.Plans.SaveActive())
                {
                    Plugin.Plans.SetActive(doc);
                    slideIndex = 0;
                    canvas.Select(null);
                }
            }

            if (!ImGui.IsItemHovered())
                continue;

            ImGui.SetTooltip($"Last edited {doc.ModifiedUtc.ToLocalTime():g}\n{doc.Slides.Count} slides, {doc.Timeline.Count} timeline steps");
        }

        ImGui.Separator();

        if (ImGui.Selectable("New plan…", false, ImGuiSelectableFlags.None, Vector2.Zero))
        {
            if (Plugin.Plans.SaveActive())
            {
                Plugin.Plans.CreateNew("New plan");
                slideIndex = 0;
                MarkDirty();
            }
        }

        if (ImGui.Selectable("Duplicate this plan", false, ImGuiSelectableFlags.None, Vector2.Zero))
        {
            if (Plugin.Plans.SaveActive())
            {
                var copy = Plugin.Plans.Duplicate(current);
                Plugin.Plans.SetActive(copy);
                slideIndex = 0;
                MarkDirty();
            }
        }

        if (Plugin.Plans.All.Count > 1 && ImGui.Selectable("Delete this plan", false, ImGuiSelectableFlags.None, Vector2.Zero))
        {
            Plugin.Plans.Delete(current);
            slideIndex = 0;
        }

        ImGui.EndPopup();
    }

    public void Dispose()
    {
        Plugin.Encounter.CombatStarted -= CancelAssignmentCheck;
        CancelAssignmentCheck();
        DisposeWtfDig();
        DisposeImport();
    }
}
