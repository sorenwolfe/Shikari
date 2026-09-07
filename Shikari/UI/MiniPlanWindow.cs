using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Shikari.Model;
using Shikari.Services;
using Shikari.UI.Theme;

namespace Shikari.UI;

/// <summary>
/// A minimap-sized copy of the current slide, for reading mid-pull. It puts itself on screen in
/// raid content and gets out of the way everywhere else.
///
/// During a pull the arena ignores the mouse unless explicitly unlocked. A separate footer
/// receives input for personal-view controls and scrolling notes, including during combat.
/// </summary>
public sealed class MiniPlanWindow : Window, IDisposable
{
    /// <summary>
    /// Flags shared by both states.
    /// </summary>
    /// <remarks>
    /// <c>NoMove</c> matters more than it looks. This window drags and resizes by hand, because it
    /// has no title bar and ImGui's drag-anywhere fallback is something a player can switch off in
    /// Dalamud's settings. But leaving that fallback on meant ImGui claimed the click first, inside
    /// Begin, before any of the hit-testing below ran — so the resize corner picked the window up
    /// and moved it like everywhere else, and the grip could never win an argument it was never
    /// allowed to have.
    /// </remarks>
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse;

    private readonly ArenaCanvas canvas = new() { MiniPresentation = true };
    private readonly ZoneClassifier zone = new();

    private bool ignoringMouse = true;
    private bool anchorPendingSave;
    private bool dragging;
    private Vector2 dragGrab;
    private bool resizing;
    private bool overGrip;
    private bool resizePendingSave;
    private float resizeGrab;
    private string noteText = string.Empty;
    private string miniStatus = string.Empty;
    private string miniHint = string.Empty;
    private float noteHeight;
    private string notesSlideId = string.Empty;
    private ThemeScope theme;

    public MiniPlanWindow()
        : base("##shikari-mini", BaseFlags)
    {
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        ForceMainWindow = true;
        IsOpen = true;
    }

    /// <summary>Set by /shikari mini. Lets the window be summoned outside raid content.</summary>
    public bool ManuallyOpen { get; private set; }

    /// <summary>
    /// Not named Toggle: the base Window.Toggle flips IsOpen, which this window ignores in
    /// favour of DrawConditions. Hiding it would be a quiet trap for whoever calls it next.
    /// </summary>
    public void ToggleShown()
    {
        // Closing while it is showing on its own should actually close it, so the toggle
        // reflects what is on screen rather than only the manual flag.
        if (!ManuallyOpen && Visible())
        {
            Plugin.Config.MiniPlanMode = MiniPlanVisibility.Never;
            ManuallyOpen = false;
        }
        else
        {
            ManuallyOpen = !ManuallyOpen;
        }

        Plugin.SaveConfig();
    }

    public void Close()
    {
        ManuallyOpen = false;
        Plugin.Config.MiniPlanMode = MiniPlanVisibility.Never;
        Plugin.SaveConfig();
    }

    private bool Visible()
    {
        zone.Refresh();

        return ContentPolicy.ShouldShow(
            Plugin.Config.MiniPlanMode,
            ManuallyOpen,
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty],
            zone.ContentTypeId,
            zone.HighEndDuty,
            Plugin.Encounter.InCombat,
            Plugin.Plans.Active is { Slides.Count: > 0 });
    }

    public override bool DrawConditions() => Visible();

    public override void PreDraw()
    {
        ignoringMouse = ContentPolicy.ShouldIgnoreMouse(
            Plugin.Encounter.InCombat, Plugin.Config.MiniPlanUnlocked);

        Flags = ignoringMouse ? BaseFlags | ImGuiWindowFlags.NoInputs : BaseFlags;

        if (ignoringMouse)
        {
            dragging = false;
            resizing = false;
            overGrip = false;
        }

        var side = Math.Clamp(Plugin.Config.MiniPlanSize, Configuration.MiniPlanMinSize, Configuration.MiniPlanMaxSize) * UiHelpers.Scale;
        var activePlan = Plugin.Plans.Active;
        var activeSlide = activePlan != null && activePlan.Slides.Count > 0
            ? activePlan.Slides[Math.Clamp(Plugin.Main.SlideIndex, 0, activePlan.Slides.Count - 1)] : null;
        var aligned = activePlan != null && Plugin.Tracker.TryAlign(activePlan, activeSlide, out _);
        var slot = Plugin.Roster.ResolveLocalSlot(activePlan);
        var target = activeSlide != null ? MiniMapLayout.Target(activeSlide, slot) : null;
        miniStatus = Plugin.Config.ShowLivePositions && aligned ? "LIVE TRACKING" : "PLAN VIEW";
        if (!Plugin.Config.MiniPlanHighlightMe && !Plugin.Config.MiniPlanYourView)
            miniHint = "Personal highlight is off in Settings.";
        else if (slot < 0)
            miniHint = Plugin.Config.MiniPlanYourView
                ? "Choose your seat in Plan > Roster to show your planned spots. Other players are hidden."
                : "Choose your seat in Plan > Roster.";
        else if (target == null)
            miniHint = "No single destination for your seat on this slide.";
        else if (!Plugin.Config.ShowLivePositions)
            miniHint = "YOUR SPOT = planned destination. Live positions are off.";
        else if (!aligned)
            miniHint = "YOUR SPOT = planned destination. " + Plugin.Tracker.Status;
        else
            miniHint = "White diamond = you. Cyan ring = your spot.";
        noteText = CurrentNotes();
        var statusHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeight() + 24 * UiHelpers.Scale;
        statusHeight += MathF.Min(ImGui.CalcTextSize(miniHint, false, MathF.Max(1, side - 40 * UiHelpers.Scale)).Y, ImGui.GetTextLineHeight() * 3);
        var viewport = ImGuiHelpers.MainViewport;
        var windowSize = MiniMapLayout.FitWindow(side, statusHeight + MeasureNotes(noteText, side), viewport.Size);
        noteHeight = windowSize.Y - windowSize.X;
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);

        // Anchor the whole panel and clamp it after resolution, size or note-length changes.
        // While dragging, DrawIdleChrome updates it directly instead.
        if (!dragging)
        {
            var position = viewport.Pos + viewport.Size * Plugin.Config.MiniPlanAnchor - windowSize * 0.5f;
            ImGui.SetNextWindowPos(MiniMapLayout.ClampPosition(position, windowSize, viewport.Pos, viewport.Size), ImGuiCond.Always);
        }

        // Theme first, then the padding, so the pops below unwind in the reverse order.
        theme = ThemeScope.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        theme.Dispose();

        if (ignoringMouse)
            return;

        RememberPosition();
    }

    public override void Draw()
    {
        var plan = Plugin.Plans.Active;
        if (plan == null || plan.Slides.Count == 0)
            return;

        // Mirrors the planner rather than tracking its own position, so auto-advance, /shikari
        // next and a wipe reset all move both at once.
        var index = Math.Clamp(Plugin.Main.SlideIndex, 0, plan.Slides.Count - 1);
        var slide = plan.Slides[index];

        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var drawList = ImGui.GetWindowDrawList();

        // The arena keeps its square; the notes strip is extra height under it.
        var board = new Vector2(size.X, size.Y - noteHeight);

        DrawPanel(drawList, min, size);

        canvas.HighlightSlot = Plugin.Config.MiniPlanHighlightMe || Plugin.Config.MiniPlanYourView
            ? Plugin.Roster.ResolveLocalSlot(plan)
            : -1;

        canvas.LiveGuides = Plugin.Config.LivePositionGuides;
        canvas.FocusOnMe = Plugin.Config.MiniPlanOnlyMe;
        canvas.MiniYourView = Plugin.Config.MiniPlanYourView;
        canvas.LivePlayers = Plugin.Config.ShowLivePositions
            ? Plugin.Tracker.Read(plan, slide)
            : null;

        // "Close enough" is a distance in the arena, not on the screen, so it is set in yalms and
        // converted through the fit. Otherwise the same two steps would count as arriving in a
        // small arena and being out of position in a large one.
        canvas.SettleTolerance = Plugin.Tracker.Aligned
            ? MathF.Max(0.005f, Plugin.Config.MiniPlanSettleYalms * Plugin.Tracker.BoardPerYalm)
            : 0.03f;

        // The arena is square and the panel has a hairline inset, so give the canvas the inner box.
        var inset = 3f * UiHelpers.Scale;
        ImGui.SetCursorPos(new Vector2(inset, inset));
        canvas.Draw(plan, slide, new Vector2(board.X - (inset * 2), board.Y - (inset * 2)), editable: false);

        DrawSlideDots(drawList, min, board, plan.Slides.Count, index);
        if (!ignoringMouse)
            DrawIdleChrome(drawList, min, board);

        // Separate top-level input window: a child of the click-through arena would inherit NoInputs.
        // Read position again because the manual drag may have moved the board this frame.
        DrawNotes(ImGui.GetWindowPos(), size, board, slide.Id);
    }

    /// <summary>The panel behind the arena: rounded, translucent, one hairline border.</summary>
    private static void DrawPanel(ImDrawListPtr drawList, Vector2 min, Vector2 size)
    {
        var max = min + size;
        var alpha = Math.Clamp(Plugin.Config.MiniPlanOpacity, 0.1f, 1f);
        var rounding = 8f * UiHelpers.Scale;

        if (Plugin.Config.ThemeShadows)
            Sprites.Shadow(drawList, min, max, 14f * UiHelpers.Scale, 0.55f * alpha);

        drawList.AddRectFilled(min, max, Palette.Pack(Palette.Window, alpha), rounding);
        drawList.AddRect(min, max, Palette.Line(0.14f), rounding, ImDrawFlags.None, 1f);
    }

    /// <summary>
    /// One dot per slide along the bottom edge, so you can see the plan moving under you without
    /// spending room on a title.
    /// </summary>
    private static void DrawSlideDots(ImDrawListPtr drawList, Vector2 min, Vector2 size, int count, int current)
    {
        if (count < 2 || count > 24)
            return;

        var radius = 1.9f * UiHelpers.Scale;
        var gap = radius * 3.2f;
        var y = min.Y + size.Y - (7f * UiHelpers.Scale);
        var startX = min.X + (size.X * 0.5f) - ((count - 1) * gap * 0.5f);

        for (var i = 0; i < count; i++)
        {
            var here = i == current;
            drawList.AddCircleFilled(
                new Vector2(startX + (i * gap), y),
                here ? radius * 1.5f : radius,
                UiHelpers.WithAlpha(0xFFFFFFFF, here ? 0.85f : 0.25f),
                12);
        }
    }

    /// <summary>The notes on the slide being shown, trimmed of blank lines.</summary>
    private static string CurrentNotes()
    {
        if (!Plugin.Config.MiniPlanShowNotes)
            return string.Empty;

        var plan = Plugin.Plans.Active;
        if (plan == null || plan.Slides.Count == 0)
            return string.Empty;

        var index = Math.Clamp(Plugin.Main.SlideIndex, 0, plan.Slides.Count - 1);
        return plan.Slides[index].Notes.Trim();
    }

    /// <summary>
    /// How much height the notes need, capped so a slide with an essay on it cannot grow the
    /// window down over the hotbars.
    /// </summary>
    private static float MeasureNotes(string text, float width)
    {
        if (text.Length == 0)
            return 0f;

        var pad = 10f * UiHelpers.Scale;
        var lines = Math.Clamp(Plugin.Config.MiniPlanNoteLines, 1, 12);
        var wrapped = ImGui.CalcTextSize(text, false, MathF.Max(1, width - (pad * 2) - ImGui.GetStyle().ScrollbarSize)).Y;

        return MathF.Min(wrapped, ImGui.GetTextLineHeight() * lines) + (pad * 2);
    }

    private void DrawNotes(Vector2 min, Vector2 size, Vector2 board, string slideId)
    {
        var pad = 10f * UiHelpers.Scale;
        ImGui.SetNextWindowPos(min + new Vector2(0, board.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(size.X, noteHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, 7 * UiHelpers.Scale));
        if (ImGui.Begin("##shikari-mini-controls", BaseFlags))
        {
            var footerMin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(footerMin, footerMin + ImGui.GetWindowSize(), Palette.Pack(0x101014, 0.97f), 6 * UiHelpers.Scale);
            var yourView = Plugin.Config.MiniPlanYourView;
            if (ImGui.Checkbox("Your view", ref yourView))
            {
                Plugin.Config.MiniPlanYourView = yourView;
                Plugin.SaveConfig();
            }
            if (ImGui.IsItemHovered())
                UiHelpers.Tooltip("Show your planned and live positions, keeping mechanics and waymarks. Scroll below to read all notes.");

            // The footer owns input, but the text child owns vertical scrolling. It never grows
            // with the notes, and wrapping uses the actual content width after the scrollbar.
            var available = ImGui.GetContentRegionAvail();
            if (available.Y > 1 && ImGui.BeginChild("##mini-notes", available, false, ImGuiWindowFlags.None))
            {
                if (notesSlideId != slideId)
                {
                    ImGui.SetScrollY(0);
                    notesSlideId = slideId;
                }
                var status = canvas.Settled ? "IN POSITION" : miniStatus;
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(Palette.Vec(canvas.Settled ? 0x80EDA2u : 0x70DEFFu), status);
                ImGui.TextUnformatted(canvas.Settled ? "Green ring = in position." : miniHint);
                if (noteText.Length > 0)
                {
                    ImGui.Separator();
                    ImGui.TextUnformatted(noteText);
                }
                ImGui.PopTextWrapPos();
            }
            if (available.Y > 1) ImGui.EndChild();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }
    /// <summary>
    /// Shown only when the window is taking the mouse — that is, out of combat. The close button
    /// and the drag are hit-tested by hand rather than left to ImGui: this window has no title
    /// bar, and ImGui's drag-anywhere fallback is something the player can switch off in Dalamud's
    /// settings, which would silently leave the thing stuck where it is.
    /// </summary>
    private void DrawIdleChrome(ImDrawListPtr drawList, Vector2 min, Vector2 size)
    {
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var mouse = ImGui.GetMousePos();
        var max = min + size;

        var button = 15f * UiHelpers.Scale;
        var pad = 6f * UiHelpers.Scale;
        var closeMin = new Vector2(max.X - pad - button, min.Y + pad);
        var closeMax = closeMin + new Vector2(button, button);

        var overClose = hovered &&
                        mouse.X >= closeMin.X && mouse.X <= closeMax.X &&
                        mouse.Y >= closeMin.Y && mouse.Y <= closeMax.Y;

        // Before the move, so a click in the corner resizes rather than picking the window up.
        DrawResizeGrip(drawList, min, size, hovered, mouse);

        if (!dragging && !resizing && !overGrip && hovered && !overClose && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragging = true;
            dragGrab = mouse - min;
        }

        if (dragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                ImGui.SetWindowPos(mouse - dragGrab, ImGuiCond.Always);
            else
                dragging = false;
        }

        if (!hovered && !dragging && !resizing)
            return;

        var rounding = 8f * UiHelpers.Scale;
        drawList.AddRect(min, max, UiHelpers.WithAlpha(0xFFFFFFFF, 0.30f), rounding, ImDrawFlags.None, 1.5f);

        var centre = closeMin + new Vector2(button * 0.5f, button * 0.5f);
        drawList.AddCircleFilled(centre, button * 0.5f,
            UiHelpers.WithAlpha(overClose ? 0xFF3B4CE0 : 0xFF000000, overClose ? 0.9f : 0.45f), 16);

        var arm = button * 0.22f;
        var cross = UiHelpers.WithAlpha(0xFFFFFFFF, overClose ? 1f : 0.7f);
        drawList.AddLine(centre - new Vector2(arm, arm), centre + new Vector2(arm, arm), cross, 1.6f);
        drawList.AddLine(centre - new Vector2(arm, -arm), centre + new Vector2(arm, -arm), cross, 1.6f);

        // Released rather than clicked, so a click that started elsewhere and drifted over the
        // button on the way up doesn't close the window mid-drag.
        if (overClose && !dragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            Close();
            return;
        }

        if (dragging)
            return;

        if (overClose)
            UiHelpers.Tooltip("Hide the mini plan. Bring it back with /shikari mini.");
        else if (!overGrip)
            UiHelpers.Tooltip("Drag the arena to move, or its corner to resize. During pulls only the footer takes clicks unless unlocked.");
    }

    /// <summary>
    /// The corner grip. Dragging it sets the arena's size, which everything else follows from,
    /// since the whole board is drawn in fractions of it.
    /// </summary>
    /// <remarks>
    /// Hand-drawn and hand-dragged rather than ImGui's own, for the same reason the move and the
    /// close box are: this window has no decoration, and the height is the arena plus however
    /// many lines of notes the slide needs. Letting ImGui own the height would put those two in a
    /// fight it wins every frame.
    /// </remarks>
    private bool DrawResizeGrip(ImDrawListPtr drawList, Vector2 min, Vector2 size, bool hovered, Vector2 mouse)
    {
        var grip = 14f * UiHelpers.Scale;
        var corner = min + size;
        var gripMin = corner - new Vector2(grip, grip);

        overGrip = hovered && !dragging &&
                   mouse.X >= gripMin.X && mouse.Y >= gripMin.Y &&
                   mouse.X <= corner.X && mouse.Y <= corner.Y;

        if (overGrip && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            resizing = true;
            resizeGrab = Plugin.Config.MiniPlanSize - (MathF.Max(mouse.X - min.X, mouse.Y - min.Y) / UiHelpers.Scale);
        }

        if (resizing)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                // Sized off whichever axis the cursor has taken further, so a diagonal drag does
                // not feel like it is fighting the mouse.
                var reach = MathF.Max(mouse.X - min.X, mouse.Y - min.Y) / UiHelpers.Scale;
                var wanted = Math.Clamp(reach + resizeGrab, Configuration.MiniPlanMinSize, Configuration.MiniPlanMaxSize);

                if (MathF.Abs(wanted - Plugin.Config.MiniPlanSize) > 0.5f)
                {
                    Plugin.Config.MiniPlanSize = wanted;
                    resizePendingSave = true;
                }
            }
            else
            {
                resizing = false;
                if (resizePendingSave)
                {
                    // Saved on release, not per frame: a drag is hundreds of frames and every one
                    // of them would be a write to disk.
                    Plugin.SaveConfig();
                    resizePendingSave = false;
                }
            }
        }

        // Three diagonal strokes, the same shape ImGui uses, so it reads as a grip on sight.
        var shade = UiHelpers.WithAlpha(0xFFFFFFFF, overGrip || resizing ? 0.85f : 0.35f);
        for (var i = 1; i <= 3; i++)
        {
            var step = grip * (i / 3.4f);
            drawList.AddLine(
                new Vector2(corner.X - step, corner.Y - (2f * UiHelpers.Scale)),
                new Vector2(corner.X - (2f * UiHelpers.Scale), corner.Y - step),
                shade,
                1.4f);
        }

        if (overGrip && !resizing)
            UiHelpers.Tooltip($"Drag to resize. Currently {Plugin.Config.MiniPlanSize:0} px.");

        return resizing;
    }

    private void RememberPosition()
    {
        var viewport = ImGuiHelpers.MainViewport;
        if (viewport.Size.X <= 0 || viewport.Size.Y <= 0)
            return;

        var centre = ImGui.GetWindowPos() + (ImGui.GetWindowSize() * 0.5f);
        var anchor = (centre - viewport.Pos) / viewport.Size;

        if (Vector2.Distance(anchor, Plugin.Config.MiniPlanAnchor) > 0.001f)
        {
            Plugin.Config.MiniPlanAnchor = anchor;
            anchorPendingSave = true;
        }

        // Written once the drag ends, so moving it isn't a hundred config writes.
        if (anchorPendingSave && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            anchorPendingSave = false;
            Plugin.SaveConfig();
        }
    }

    public void Dispose()
    {
    }
}
