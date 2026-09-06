using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Live;

namespace Shikari.UI;

/// <summary>What a click on the arena does.</summary>
public enum CanvasTool
{
    Select,
    PlayerToken,
    EnemyToken,
    Waymark,
    Label,
    Zone,
    Arrow,
    Tether,
    Pen,
}

/// <summary>
/// The arena widget: draws a slide and lets you move things around on it.
/// Everything is stored in normalised 0-1 coordinates, so the same plan renders identically in a
/// small docked window and a maximised one.
/// </summary>
public sealed partial class ArenaCanvas
{
    private const string CanvasId = "##shikari-arena";

    /// <summary>Past this many points a selected path gets an outline instead of per-point dots.</summary>
    public const int MaxPointHandles = 8;

    public const float MinZoom = ArenaView.MinZoom;
    public const float MaxZoom = ArenaView.MaxZoom;

    /// <summary>
    /// Top-left of the whole board in screen space, and its side. At zoom 1 this is the widget
    /// itself; zoomed in it is bigger than the widget and slides around under it, which is what
    /// lets every drawing call below stay in board coordinates and know nothing about zoom.
    /// </summary>
    private Vector2 origin;
    private float side;

    private ArenaView view = new();
    private bool panning;

    /// <summary>Alpha multiplier for the item being drawn. 1 unless it belongs to someone else.</summary>
    private float dim = 1f;

    private string? draggingId;
    private CanvasItem? drawingStroke;
    private Vector2 pendingStart;
    private bool hasPendingStart;

    public CanvasTool Tool { get; set; } = CanvasTool.Select;

    /// <summary>Colour applied to newly created items.</summary>
    private uint drawingBrushColor = 0xFF4FA3FF;
    private uint aoeBrushColor = CanvasItem.DefaultAoeColor;
    public uint BrushColor
    {
        get => Tool == CanvasTool.Zone ? aoeBrushColor : drawingBrushColor;
        set
        {
            if (Tool == CanvasTool.Zone) aoeBrushColor = value;
            else drawingBrushColor = value;
        }
    }

    /// <summary>Zone shape used when the Zone tool places something.</summary>
    public ZoneShape BrushZone { get; set; } = ZoneShape.Circle;

    /// <summary>Waymark letter placed by the Waymark tool.</summary>
    public string BrushWaymark { get; set; } = "A";

    /// <summary>Roster seat bound to newly placed player tokens.</summary>
    public int BrushSlot { get; set; }

    /// <summary>
    /// Roster seat to ring as "this is you", or -1 for none. Set from the resolved local seat so
    /// a player can find themselves on a board they didn't draw.
    /// </summary>
    public int HighlightSlot { get; set; } = -1;

    /// <summary>
    /// Where the party actually is, on the board. Null or empty draws nothing — which is the
    /// normal state out of a duty and whenever the arena cannot be lined up.
    /// </summary>
    public IReadOnlyList<ArenaTracker.LivePlayer>? LivePlayers { get; set; }

    /// <summary>Draw a line from each player to the token the plan has for their seat.</summary>
    public bool LiveGuides { get; set; } = true;

    /// <summary>
    /// Play everyone but <see cref="HighlightSlot"/> down, so one instruction stands out.
    /// </summary>
    /// <remarks>
    /// Only the players fade. Every shape that describes the mechanic — zones, waymarks, text —
    /// stays exactly as it was, because that is the thing being dodged and hiding any of it to
    /// reduce clutter would be trading the wrong way.
    /// </remarks>
    public bool FocusOnMe { get; set; }

    /// <summary>How far the local player is from their spot, as a fraction of the board. -1 unknown.</summary>
    public float SettleDistance { get; set; } = -1f;

    /// <summary>Inside this and they count as standing on their spot.</summary>
    public float SettleTolerance { get; set; } = 0.03f;

    /// <summary>True once the local player is standing where the plan wants them.</summary>
    public bool Settled => MiniPresentation ? miniSettled : SettleDistance >= 0f && SettleDistance <= SettleTolerance;

    /// <summary>Gold. The one colour on the board that only ever means "this is your spot".</summary>
    public const uint TargetGold = 0xFF4FC8FF;

    public string? SelectedId { get; private set; }

    /// <summary>How far in the view is. 1 is the whole arena.</summary>
    public float ViewZoom => view.Zoom;

    public bool IsZoomedIn => view.IsZoomedIn;

    /// <summary>Zooms about the middle of what is on screen, so the view does not jump.</summary>
    public void SetViewZoom(float value) => view.SetZoom(value);

    public void ResetView() => view.Reset();

    public CanvasItem? GetSelected(Slide slide) =>
        SelectedId == null ? null : slide.Items.FirstOrDefault(i => i.Id == SelectedId);

    public void Select(string? id) => SelectedId = id;

    /// <summary>Draws the slide and processes interaction. Returns true if the plan was modified.</summary>
    public bool Draw(PlanDocument plan, Slide slide, Vector2 available, bool editable)
    {
        var changed = false;

        var floor = (MiniPresentation ? 96f : 120f) * UiHelpers.Scale;
        if (available.X < floor || available.Y < floor)
        {
            ImGui.TextDisabled("Not enough room to draw the arena.");
            return false;
        }

        var viewSide = MathF.Min(available.X, available.Y);
        var canvasSize = new Vector2(viewSide, viewSide);

        // Centre the square inside whatever room we were given.
        var pad = MathF.Max(0f, (available.X - viewSide) * 0.5f);
        if (pad > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);

        var viewOrigin = ImGui.GetCursorScreenPos();

        // The board is the widget scaled up by the zoom and shifted so the focus point sits in
        // the middle. Everything below then draws in board coordinates as it always did.
        if (!editable)
            view.Reset();

        side = view.BoardSide(viewSide);
        origin = view.BoardOrigin(viewOrigin, viewSide);
        var boardSize = new Vector2(side, side);

        // Before anything is drawn: the target ring is painted with the tokens, and it needs to
        // know whether you have arrived before it decides whether to pulse.
        MeasureSettle(slide);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(viewOrigin, viewOrigin + canvasSize, true);

        miniLabels.Clear();
        DrawBackdrop(drawList, slide, boardSize);
        DrawArenaBackground(drawList, plan.Arena, boardSize);

        var ordered = MiniPresentation
            ? slide.Items.OrderBy(MiniMapLayout.Layer).ThenBy(i => i.Layer)
            : slide.Items.OrderBy(i => i.Layer).ThenBy(i => (int)i.Kind);
        foreach (var item in ordered)
            DrawItem(drawList, plan, slide, item);

        dim = 1f;

        if (MiniPresentation)
        {
            DrawMiniLivePlayers(drawList, plan, slide);
            DrawMiniDestination(drawList, slide);
            DrawMiniLabels(drawList);
        }
        else DrawLivePlayers(drawList, plan, slide);

        drawList.PopClipRect();

        if (editable)
        {
            ImGui.InvisibleButton(CanvasId, canvasSize,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            changed |= HandleInteraction(plan, slide);

            // After the interaction, so a click and the thing it hit agree on where they are.
            HandleView(viewSide);
        }
        else
        {
            // A read-only arena takes no input. An InvisibleButton here would still claim the
            // hovered id for the whole area, and ImGui will not start a window drag, or let
            // anything drawn on top take the mouse, while an item is hovered. In the mini window
            // that is the entire window, which is how its move and close stopped working.
            ImGui.Dummy(canvasSize);
        }

        // Outline last so it sits above everything, including the hit region. On the widget, not
        // the board — zoomed in the board's own edge is off screen.
        drawList.AddRect(viewOrigin, viewOrigin + canvasSize, 0x40FFFFFF, 4f, ImDrawFlags.None, 1f);

        if (editable && IsZoomedIn)
            DrawZoomHint(drawList, viewOrigin, canvasSize);

        return changed;
    }

    /// <summary>
    /// The wheel zooms about the cursor, the middle button drags the board around. Left is busy
    /// drawing and right opens the menu, so the middle button is the one left for panning.
    /// </summary>
    private void HandleView(float viewSide)
    {
        var hovered = ImGui.IsItemHovered();
        var busy = drawingStroke != null || draggingId != null || hasPendingStart;

        var wheel = ImGui.GetIO().MouseWheel;
        if (hovered && !busy && wheel != 0f)
            view.ZoomAbout(view.Zoom * MathF.Pow(1.25f, wheel), ToNormalised(ImGui.GetMousePos()));

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            panning = true;

        if (!panning)
            return;

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            panning = false;
            return;
        }

        view.Pan(ImGui.GetIO().MouseDelta, viewSide);
    }

    /// <summary>
    /// Where everyone actually is, over the top of where the plan says they should be.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike a planned token: a hollow ring, smaller, no icon. The board has to
    /// stay readable as a plan, and someone glancing at it during a mechanic must be able to tell
    /// "this is me now" from "this is where I go" without thinking about it. The line between the
    /// two is the whole point — it is the instruction, in a form that needs no reading.
    /// </remarks>
    private void DrawLivePlayers(ImDrawListPtr drawList, PlanDocument plan, Slide slide)
    {
        var players = LivePlayers;
        if (players == null || players.Count == 0)
            return;

        var radius = Len(0.022f);
        if (radius < 2f)
            return;

        foreach (var player in players)
        {
            var mine = player.IsLocal || (HighlightSlot >= 0 && player.SlotIndex == HighlightSlot);

            // In focus mode everyone else stays visible, just quiet enough not to compete.
            dim = FocusOnMe && HighlightSlot >= 0 && !mine ? OtherPlayerDim : 1f;

            var at = ToScreen(player.Board);

            var colour = player.SlotIndex >= 0 && player.SlotIndex < plan.Roster.Count
                ? RoleColors.Default(plan.Roster[player.SlotIndex].Role)
                : 0xFFCCCCCC;

            if (LiveGuides && TryPlannedSpot(slide, player.SlotIndex, out var target))
            {
                var to = ToScreen(target);

                // Only worth drawing while they are actually somewhere else. A line to a token
                // you are standing on is noise, and every player standing right adds one.
                if ((to - at).Length() > radius * 1.6f)
                    DrawGuide(drawList, at, to, player.IsLocal ? TargetGold : colour, player.IsLocal);
            }

            drawList.AddCircleFilled(at, radius, Fade(UiHelpers.WithAlpha(colour, 0.28f)), 20);
            drawList.AddCircle(at, radius, Fade(UiHelpers.WithAlpha(colour, 0.95f)), 20, player.IsLocal ? 2.5f : 1.5f);

            if (player.IsLocal)
                drawList.AddCircle(at, radius + (2.5f * UiHelpers.Scale), 0xCCFFFFFF, 24, 1.5f);
        }

        dim = 1f;
    }

    /// <summary>A dashed run from where you are to where the plan wants you.</summary>
    private void DrawGuide(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint colour, bool local)
    {
        var span = to - from;
        var length = span.Length();
        if (length < 1f)
            return;

        var step = 7f * UiHelpers.Scale;
        var direction = span / length;
        var shade = Fade(UiHelpers.WithAlpha(colour, local ? 0.85f : 0.35f));
        var thickness = local ? 2f : 1.2f;

        // Dashes rather than a solid line: eight solid lines across the arena reads as a mess,
        // and the dashes still show the direction at a glance.
        for (var travelled = 0f; travelled < length; travelled += step * 2f)
        {
            var a = from + (direction * travelled);
            var b = from + (direction * MathF.Min(travelled + step, length));
            drawList.AddLine(a, b, shade, thickness);
        }
    }

    /// <summary>Where this seat's token sits on the slide, if it has one.</summary>
    private static bool TryPlannedSpot(Slide slide, int slotIndex, out Vector2 position)
    {
        position = Vector2.Zero;
        if (slotIndex < 0)
            return false;

        foreach (var item in slide.Items)
        {
            if (item.Kind == CanvasItemKind.PlayerToken && item.SlotIndex == slotIndex)
            {
                position = item.Position;
                return true;
            }
        }

        return false;
    }

    /// <summary>A small badge while zoomed, so nobody wonders why the arena is cropped.</summary>
    private void DrawZoomHint(ImDrawListPtr drawList, Vector2 viewOrigin, Vector2 size)
    {
        var text = view.Zoom.ToString("0.#") + "x";
        var pad = new Vector2(6f, 3f) * UiHelpers.Scale;
        var extent = UiHelpers.TextSize(text);
        var max = viewOrigin + new Vector2(size.X - (8f * UiHelpers.Scale), 8f * UiHelpers.Scale + extent.Y + (pad.Y * 2));
        var min = max - extent - (pad * 2);

        drawList.AddRectFilled(min, max, 0x99000000, 3f);
        UiHelpers.CenteredShadowText(drawList, (min + max) * 0.5f, text, 0xCCFFFFFF);
    }

    // ---------------------------------------------------------------- coordinates

    public Vector2 ToScreen(Vector2 normalised) => origin + (normalised * side);

    public Vector2 ToNormalised(Vector2 screen) => (screen - origin) / side;

    private float Len(float normalisedLength) => normalisedLength * side;

    /// <summary>
    /// How far down to play an item that belongs to somebody else.
    /// </summary>
    /// <remarks>
    /// Faded, never hidden. A plan you cannot see the rest of is no longer a plan — you lose the
    /// ability to check whether the person next to you is where they should be, and to pick your
    /// own spot up again after a death shuffles the party round.
    /// </remarks>
    public const float OtherPlayerDim = 0.3f;

    /// <summary>Scales a packed colour's alpha by the current dim, leaving its hue alone.</summary>
    private uint Fade(uint colour)
    {
        if (dim >= 0.999f)
            return colour;

        var alpha = (colour >> 24) & 0xFF;
        return (colour & 0x00FFFFFF) | ((uint)(alpha * dim) << 24);
    }

    /// <summary>
    /// Whether this item is somebody else's business.
    /// </summary>
    /// <remarks>
    /// Only players and the movement drawn from them. A line is somebody's if it starts on their
    /// token; one starting nowhere near a token belongs to the mechanic and stays bright. The
    /// default in every uncertain case is to leave it alone — dimming something the player needed
    /// is a far worse failure than leaving one line too many at full strength.
    /// </remarks>
    private float DimFor(Slide slide, CanvasItem item)
    {
        // Nothing to focus on. Without knowing which seat is yours there is no "everyone else".
        if (HighlightSlot < 0)
            return 1f;

        switch (item.Kind)
        {
            case CanvasItemKind.PlayerToken:
                return item.SlotIndex < 0 || item.SlotIndex == HighlightSlot ? 1f : OtherPlayerDim;

            case CanvasItemKind.Arrow:
            case CanvasItemKind.Tether:
            case CanvasItemKind.Freehand:
            {
                if (item.Points.Count == 0)
                    return 1f;

                var owner = TokenAt(slide, item.Points[0]);
                return owner >= 0 && owner != HighlightSlot ? OtherPlayerDim : 1f;
            }

            default:
                return 1f;
        }
    }

    /// <summary>
    /// How far the local player is from the spot the plan gives them, this frame.
    /// </summary>
    /// <remarks>
    /// -1 whenever that cannot be answered — no live positions, no seat resolved, or no token for
    /// that seat on this slide. That is not the same as being out of position, and the target ring
    /// treats it differently: it keeps pulsing rather than claiming an arrival it cannot see.
    /// </remarks>
    private void MeasureSettle(Slide slide)
    {
        SettleDistance = -1f;
        if (MiniPresentation) { MeasureMiniSettle(slide); return; }

        var players = LivePlayers;
        if (players == null || HighlightSlot < 0)
            return;

        foreach (var player in players)
        {
            if (!player.IsLocal || player.SlotIndex != HighlightSlot)
                continue;

            if (TryPlannedSpot(slide, HighlightSlot, out var target))
                SettleDistance = (player.Board - target).Length();

            return;
        }
    }

    /// <summary>The seat whose token sits under this point, or -1 if none does.</summary>
    private static int TokenAt(Slide slide, Vector2 point)
    {
        foreach (var item in slide.Items)
        {
            if (item.Kind != CanvasItemKind.PlayerToken || item.SlotIndex < 0)
                continue;

            if ((item.Position - point).Length() <= MathF.Max(item.Radius, 0.02f) * 1.4f)
                return item.SlotIndex;
        }

        return -1;
    }

    // ---------------------------------------------------------------- arena

    /// <summary>
    /// The tracing reference, drawn under everything. Fitted to the square without stretching, so
    /// a wide screenshot keeps its proportions rather than being squashed onto the arena.
    /// </summary>
    private void DrawBackdrop(ImDrawListPtr drawList, Slide slide, Vector2 size)
    {
        if (!slide.HasBackdrop)
            return;

        var texture = Plugin.Backdrops.Get(slide.BackdropId);
        if (texture == null || !texture.TryGetWrap(out var wrap, out _) || wrap == null)
            return;

        if (wrap.Width <= 0 || wrap.Height <= 0)
            return;

        var scale = MathF.Min(size.X / wrap.Width, size.Y / wrap.Height);
        var drawn = new Vector2(wrap.Width * scale, wrap.Height * scale);
        var offset = (size - drawn) * 0.5f;

        var alpha = Math.Clamp(slide.BackdropOpacity, 0.05f, 1f);
        drawList.AddImage(wrap.Handle, origin + offset, origin + offset + drawn,
            Vector2.Zero, Vector2.One, UiHelpers.WithAlpha(0xFFFFFFFF, alpha));
    }

    private void DrawArenaBackground(ImDrawListPtr drawList, ArenaSettings arena, Vector2 size)
    {
        var min = origin;
        var max = origin + size;
        var centre = origin + (size * 0.5f);
        var half = size * 0.5f;

        drawList.AddRectFilled(min, max, arena.BackgroundColor, 4f);

        switch (arena.Shape)
        {
            case ArenaShape.Circle:
                drawList.AddCircleFilled(centre, half.X * 0.94f, UiHelpers.Darken(arena.BackgroundColor, -0.0f), 96);
                drawList.AddCircle(centre, half.X * 0.94f, arena.LineColor, 96, 2f);
                break;

            case ArenaShape.Square:
                drawList.AddRect(min + (size * 0.03f), max - (size * 0.03f), arena.LineColor, 0f, ImDrawFlags.None, 2f);
                break;

            case ArenaShape.Rectangle:
            {
                var ratio = MathF.Max(0.2f, arena.AspectRatio);
                var w = half.X * 0.94f;
                var h = w / ratio;
                if (h > half.Y * 0.94f)
                {
                    h = half.Y * 0.94f;
                    w = h * ratio;
                }

                drawList.AddRect(centre - new Vector2(w, h), centre + new Vector2(w, h), arena.LineColor, 0f, ImDrawFlags.None, 2f);
                break;
            }

            case ArenaShape.Octagon:
                DrawRegularPolygon(drawList, centre, half.X * 0.94f, 8, 22.5f, arena.LineColor);
                break;

            case ArenaShape.Hexagon:
                DrawRegularPolygon(drawList, centre, half.X * 0.94f, 6, 0f, arena.LineColor);
                break;
        }

        if (arena.ShowGrid && arena.GridDivisions > 1)
        {
            var step = size.X / arena.GridDivisions;
            for (var i = 1; i < arena.GridDivisions; i++)
            {
                var x = min.X + (step * i);
                var y = min.Y + (step * i);
                drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), MiniPresentation ? 0x16FFFFFF : arena.GridColor, 1f);
                drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), MiniPresentation ? 0x16FFFFFF : arena.GridColor, 1f);
            }
        }

        if (arena.ShowCardinals)
        {
            var inset = size.X * 0.035f;
            UiHelpers.CenteredShadowText(drawList, new Vector2(centre.X, min.Y + inset), "N", 0xB0FFFFFF);
            UiHelpers.CenteredShadowText(drawList, new Vector2(centre.X, max.Y - inset), "S", 0x80FFFFFF);
            UiHelpers.CenteredShadowText(drawList, new Vector2(max.X - inset, centre.Y), "E", 0x80FFFFFF);
            UiHelpers.CenteredShadowText(drawList, new Vector2(min.X + inset, centre.Y), "W", 0x80FFFFFF);
        }

        if (arena.ShowWaymarkGuides)
        {
            var r = half.X * 0.78f;
            var labels = new[] { "A", "1", "B", "2", "C", "3", "D", "4" };
            for (var i = 0; i < 8; i++)
            {
                var angle = (-90f + (i * 45f)) * (MathF.PI / 180f);
                var p = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
                drawList.AddCircle(p, size.X * 0.022f, 0x40FFFFFF, 20, 1f);
                UiHelpers.CenteredShadowText(drawList, p, labels[i], 0x60FFFFFF);
            }
        }
    }

    private static void DrawRegularPolygon(ImDrawListPtr drawList, Vector2 centre, float radius, int sides, float rotationDegrees, uint colour)
    {
        drawList.PathClear();
        for (var i = 0; i < sides; i++)
        {
            var angle = ((360f / sides * i) + rotationDegrees - 90f) * (MathF.PI / 180f);
            drawList.PathLineTo(centre + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius));
        }

        drawList.PathStroke(colour, ImDrawFlags.Closed, 2f);
    }

    // ---------------------------------------------------------------- items

    private void DrawItem(ImDrawListPtr drawList, PlanDocument plan, Slide slide, CanvasItem item)
    {
        var selected = item.Id == SelectedId;
        var centre = ToScreen(item.Position);

        dim = FocusOnMe ? DimFor(slide, item) : 1f;

        switch (item.Kind)
        {
            case CanvasItemKind.Zone:
                DrawZone(drawList, item);
                break;

            case CanvasItemKind.PlayerToken:
                DrawPlayerToken(drawList, plan, item, centre);
                break;

            case CanvasItemKind.EnemyToken:
            {
                if (MiniPresentation) { DrawMiniEnemy(drawList, item, centre); break; }
                var r = Len(item.Radius);
                drawList.AddCircleFilled(centre, r, UiHelpers.WithAlpha(item.Color, 0.85f), 32);
                drawList.AddCircle(centre, r, 0xFF101010, 32, 2f);
                UiHelpers.CenteredShadowText(drawList, centre,
                    string.IsNullOrEmpty(item.Text) ? "B" : item.Text,
                    UiHelpers.ReadableTextOn(item.Color));
                break;
            }

            case CanvasItemKind.Waymark:
                DrawWaymark(drawList, item, centre);
                break;

            case CanvasItemKind.Label:
            {
                var text = string.IsNullOrEmpty(item.Text) ? "text" : item.Text;
                var size = UiHelpers.TextSize(text);
                var pos = centre - (size * 0.5f);
                drawList.AddRectFilled(pos - new Vector2(4, 2), pos + size + new Vector2(4, 2), 0x90000000, 3f);
                drawList.AddText(pos, item.Color, text);
                break;
            }

            case CanvasItemKind.Arrow:
                DrawArrow(drawList, item);
                break;

            case CanvasItemKind.Tether:
                DrawPath(drawList, item, closed: false);
                break;

            case CanvasItemKind.Freehand:
                DrawPath(drawList, item, closed: false);
                break;
        }

        if (selected)
            DrawSelectionHint(drawList, item, centre);
    }

    private void DrawPlayerToken(ImDrawListPtr drawList, PlanDocument plan, CanvasItem item, Vector2 centre)
    {
        if (MiniPresentation) { DrawMiniToken(drawList, plan, item, centre); return; }
        var radius = Len(item.Radius);

        var colour = item.Color;
        var label = item.Text;
        uint jobId = 0;

        if (item.SlotIndex >= 0 && item.SlotIndex < plan.Roster.Count)
        {
            var slot = plan.Roster[item.SlotIndex];
            colour = slot.Color != 0 ? slot.Color : RoleColors.Default(slot.Role);
            jobId = slot.JobId;
            if (string.IsNullOrEmpty(label))
                label = slot.DisplayName;
        }

        if (string.IsNullOrEmpty(label))
            label = "?";

        var isMe = HighlightSlot >= 0 && item.SlotIndex == HighlightSlot;
        if (isMe)
            DrawYouRing(drawList, centre, radius, colour);

        drawList.AddCircleFilled(centre, radius, Fade(UiHelpers.WithAlpha(colour, 0.92f)), 32);
        drawList.AddCircle(centre, radius, Fade(isMe ? 0xFFFFFFFF : 0xFF101010), 32, isMe ? 2.5f : 2f);

        // An explicit icon on the token wins, then the seat's job, then just the seat label.
        var iconId = item.IconId != 0 ? item.IconId : JobIcons.For(jobId);

        var half = new Vector2(radius * 0.78f, radius * 0.78f);
        if (iconId == 0 || !UiHelpers.DrawIconAt(drawList, iconId, centre - half, centre + half, Fade(0xFFFFFFFF)))
            UiHelpers.CenteredShadowText(drawList, centre, label, Fade(UiHelpers.ReadableTextOn(colour)));
        else if (radius > 18f * UiHelpers.Scale)
            UiHelpers.CenteredShadowText(drawList, centre + new Vector2(0, radius + 7f), label, Fade(0xFFFFFFFF));
    }

    /// <summary>
    /// The "you are here" marker. A soft halo plus a slowly breathing ring — enough to find at a
    /// glance on a busy board, calm enough not to pull the eye during a mechanic.
    /// </summary>
    /// <summary>
    /// Marks the spot the plan gives you. It pulses gold while you are somewhere else and goes
    /// solid the moment you are standing on it.
    /// </summary>
    /// <remarks>
    /// The pulse is the point. Something moving catches the eye in peripheral vision, which is
    /// all the attention going spare mid-mechanic, and it stopping is a clearer "yes, there" than
    /// any wording — you get the confirmation without reading anything or looking away from the
    /// boss. One colour, used for nothing else on the board, so it never has to be interpreted.
    /// </remarks>
    private void DrawYouRing(ImDrawListPtr drawList, Vector2 centre, float radius, uint colour)
    {
        // Without live positions there is nothing to settle against, so it keeps pulsing rather
        // than claiming you have arrived.
        var settled = Settled;
        var pulse = settled ? 1f : 0.5f + (0.5f * MathF.Sin((float)ImGui.GetTime() * 3.4f));

        var glowAlpha = settled ? 0.42f : 0.24f + (0.20f * pulse);
        var ringAlpha = settled ? 0.95f : 0.45f + (0.40f * pulse);
        var ringScale = settled ? 1.30f : 1.26f + (0.13f * pulse);

        // The sprite gives a real falloff. Without it, fall back to a flat disc, which is the
        // best a draw list can manage on its own.
        var halo = UiHelpers.WithAlpha(TargetGold, glowAlpha);
        if (!Theme.Sprites.Glow(drawList, centre, radius * (settled ? 2.4f : 2.6f), halo))
            drawList.AddCircleFilled(centre, radius * 1.62f, UiHelpers.WithAlpha(TargetGold, glowAlpha * 0.35f), 40);

        drawList.AddCircle(centre, radius * ringScale, UiHelpers.WithAlpha(TargetGold, ringAlpha), 40,
            settled ? 3f : 2f);

        // A second, still ring underneath once you have arrived: unmistakably "done" next to the
        // same mark moving a moment ago.
        if (settled)
            drawList.AddCircle(centre, radius * 1.52f, UiHelpers.WithAlpha(TargetGold, 0.35f), 40, 1.5f);
    }

    private void DrawWaymark(ImDrawListPtr drawList, CanvasItem item, Vector2 centre)
    {
        var text = string.IsNullOrEmpty(item.Text) ? "A" : item.Text;
        var colour = WaymarkColor(text);
        var radius = MiniPresentation ? MathF.Max(9f * UiHelpers.Scale, Len(item.Radius * 0.8f)) : Len(item.Radius * 0.8f);
        if (MiniPresentation) drawList.AddCircleFilled(centre, radius + 2 * UiHelpers.Scale, 0xFF171310, 24);

        if (text is "1" or "2" or "3" or "4")
        {
            drawList.AddRectFilled(centre - new Vector2(radius, radius), centre + new Vector2(radius, radius),
                UiHelpers.WithAlpha(colour, 0.35f), 2f);
            drawList.AddRect(centre - new Vector2(radius, radius), centre + new Vector2(radius, radius),
                colour, 2f, ImDrawFlags.None, 2f);
        }
        else
        {
            drawList.AddCircleFilled(centre, radius, UiHelpers.WithAlpha(colour, 0.35f), 24);
            drawList.AddCircle(centre, radius, colour, 24, 2f);
        }

        UiHelpers.CenteredShadowText(drawList, centre, text, 0xFFFFFFFF);
    }

    private static uint WaymarkColor(string mark) => mark switch
    {
        "A" or "1" => 0xFF4444EE, // red
        "B" or "2" => 0xFF44DDEE, // yellow
        "C" or "3" => 0xFFEE9944, // blue
        "D" or "4" => 0xFFEE66CC, // purple
        _ => 0xFFFFFFFF,
    };

    private void DrawZone(ImDrawListPtr drawList, CanvasItem item)
    {
        var centre = ToScreen(item.Position);
        var fill = MiniPresentation ? MiniMapLayout.HazardFill(item.Color) : item.Color;
        var edge = UiHelpers.WithAlpha(item.Color, 0.9f);

        switch (item.Zone)
        {
            case ZoneShape.Circle:
            {
                var r = Len(item.Radius);
                drawList.AddCircleFilled(centre, r, fill, 64);
                drawList.AddCircle(centre, r, edge, 64, 2f);
                break;
            }

            case ZoneShape.Donut:
            {
                var outer = Len(item.Radius);
                var inner = Len(MathF.Min(item.InnerRadius, item.Radius * 0.95f));
                const int segments = 64;
                for (var i = 0; i < segments; i++)
                {
                    var a0 = MathF.Tau * i / segments;
                    var a1 = MathF.Tau * (i + 1) / segments;
                    var d0 = new Vector2(MathF.Cos(a0), MathF.Sin(a0));
                    var d1 = new Vector2(MathF.Cos(a1), MathF.Sin(a1));
                    var quad = new[]
                    {
                        centre + (d0 * inner),
                        centre + (d0 * outer),
                        centre + (d1 * outer),
                        centre + (d1 * inner),
                    };
                    drawList.AddConvexPolyFilled(ref quad[0], 4, fill);
                }

                drawList.AddCircle(centre, outer, edge, 64, 2f);
                drawList.AddCircle(centre, inner, edge, 64, 2f);
                break;
            }

            case ZoneShape.Rectangle:
            {
                var e = new Vector2(Len(item.Extent.X), Len(item.Extent.Y));
                var corners = new[]
                {
                    centre + UiHelpers.Rotate(new Vector2(-e.X, -e.Y), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(e.X, -e.Y), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(e.X, e.Y), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(-e.X, e.Y), item.Rotation),
                };
                drawList.AddConvexPolyFilled(ref corners[0], 4, fill);
                drawList.AddPolyline(ref corners[0], 4, edge, ImDrawFlags.Closed, 2f);
                break;
            }

            case ZoneShape.Cone:
            {
                var r = Len(item.Radius);
                var halfSweep = MathF.Max(1f, item.ConeAngle) * 0.5f;
                var start = (item.Rotation - 90f - halfSweep) * (MathF.PI / 180f);
                var end = (item.Rotation - 90f + halfSweep) * (MathF.PI / 180f);

                drawList.PathClear();
                drawList.PathLineTo(centre);
                drawList.PathArcTo(centre, r, start, end, 48);
                drawList.PathFillConvex(fill);

                drawList.PathClear();
                drawList.PathLineTo(centre);
                drawList.PathArcTo(centre, r, start, end, 48);
                drawList.PathStroke(edge, ImDrawFlags.Closed, 2f);
                break;
            }

            case ZoneShape.Line:
            {
                var halfWidth = Len(item.Extent.X);
                var length = Len(item.Radius * 2f);
                var corners = new[]
                {
                    centre + UiHelpers.Rotate(new Vector2(-halfWidth, 0), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(halfWidth, 0), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(halfWidth, -length), item.Rotation),
                    centre + UiHelpers.Rotate(new Vector2(-halfWidth, -length), item.Rotation),
                };
                drawList.AddConvexPolyFilled(ref corners[0], 4, fill);
                drawList.AddPolyline(ref corners[0], 4, edge, ImDrawFlags.Closed, 2f);
                break;
            }

            case ZoneShape.Cross:
            {
                var arm = Len(item.Radius);
                var thickness = Len(item.Extent.X);
                DrawRotatedBar(drawList, centre, arm, thickness, item.Rotation, fill, edge);
                DrawRotatedBar(drawList, centre, arm, thickness, item.Rotation + 90f, fill, edge);
                break;
            }
        }
    }

    private static void DrawRotatedBar(ImDrawListPtr drawList, Vector2 centre, float halfLength, float halfWidth, float rotation, uint fill, uint edge)
    {
        var corners = new[]
        {
            centre + UiHelpers.Rotate(new Vector2(-halfLength, -halfWidth), rotation),
            centre + UiHelpers.Rotate(new Vector2(halfLength, -halfWidth), rotation),
            centre + UiHelpers.Rotate(new Vector2(halfLength, halfWidth), rotation),
            centre + UiHelpers.Rotate(new Vector2(-halfLength, halfWidth), rotation),
        };
        drawList.AddConvexPolyFilled(ref corners[0], 4, fill);
        drawList.AddPolyline(ref corners[0], 4, edge, ImDrawFlags.Closed, 2f);
    }

    private void DrawArrow(ImDrawListPtr drawList, CanvasItem item)
    {
        if (item.Points.Count < 2)
            return;

        var thickness = MathF.Max(1.5f, Len(item.Thickness));
        var points = item.Points.Select(ToScreen).ToArray();
        drawList.AddPolyline(ref points[0], points.Length, Fade(item.Color), ImDrawFlags.None, thickness);

        var tip = points[^1];
        var prev = points[^2];
        var dir = tip - prev;
        var length = dir.Length();
        if (length < 0.001f)
            return;

        dir /= length;
        var normal = new Vector2(-dir.Y, dir.X);
        var head = MathF.Max(8f, thickness * 3.2f);

        drawList.AddTriangleFilled(
            tip,
            tip - (dir * head) + (normal * head * 0.55f),
            tip - (dir * head) - (normal * head * 0.55f),
            Fade(item.Color));
    }

    private void DrawPath(ImDrawListPtr drawList, CanvasItem item, bool closed)
    {
        if (item.Points.Count < 2)
            return;

        var thickness = MathF.Max(1.5f, Len(item.Thickness));
        var points = item.Points.Select(ToScreen).ToArray();
        drawList.AddPolyline(ref points[0], points.Length, Fade(item.Color),
            closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);
    }

    private void DrawSelectionHint(ImDrawListPtr drawList, CanvasItem item, Vector2 centre)
    {
        var r = Len(MathF.Max(item.Radius, 0.03f)) + 5f;

        if (item.Kind is CanvasItemKind.Arrow or CanvasItemKind.Tether or CanvasItemKind.Freehand && item.Points.Count > 0)
        {
            var screen = item.Points.Select(ToScreen).ToArray();

            // A handle per point works for a two-ended tether. A pen stroke has dozens, and
            // beading every one of them buries the line you actually drew.
            if (screen.Length > MaxPointHandles)
            {
                var min = screen[0];
                var max = screen[0];
                foreach (var p in screen)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }

                var pad = new Vector2(5f, 5f);
                drawList.AddRect(min - pad, max + pad, 0x90FFFFFF, 3f, ImDrawFlags.None, 1.5f);
                return;
            }

            foreach (var p in screen)
                drawList.AddCircle(p, 4f, 0xFFFFFFFF, 12, 1.5f);
            return;
        }

        drawList.AddCircle(centre, r, 0xFFFFFFFF, 40, 1.5f);
        drawList.AddCircle(centre, r, 0x60000000, 40, 3f);
    }

    // ---------------------------------------------------------------- interaction

    private bool HandleInteraction(PlanDocument plan, Slide slide)
    {
        var changed = false;
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        var normalised = ToNormalised(mouse);

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (Tool == CanvasTool.Select)
            {
                var hit = HitTest(slide, normalised);
                SelectedId = hit?.Id;
                draggingId = hit is { Locked: false } ? hit.Id : null;
            }
            else if (Tool == CanvasTool.Pen)
            {
                drawingStroke = new CanvasItem
                {
                    Kind = CanvasItemKind.Freehand,
                    Color = BrushColor,
                    Points = new List<Vector2> { normalised },
                    Position = normalised,
                    Thickness = 0.006f,
                };
                slide.Items.Add(drawingStroke);
                SelectedId = drawingStroke.Id;
                changed = true;
            }
            else if (Tool is CanvasTool.Arrow or CanvasTool.Tether)
            {
                if (!hasPendingStart)
                {
                    pendingStart = normalised;
                    hasPendingStart = true;
                }
                else
                {
                    var item = new CanvasItem
                    {
                        Kind = Tool == CanvasTool.Arrow ? CanvasItemKind.Arrow : CanvasItemKind.Tether,
                        Color = BrushColor,
                        Points = new List<Vector2> { pendingStart, normalised },
                        Position = (pendingStart + normalised) * 0.5f,
                        Thickness = Tool == CanvasTool.Arrow ? 0.008f : 0.005f,
                    };
                    slide.Items.Add(item);
                    SelectedId = item.Id;
                    hasPendingStart = false;
                    changed = true;
                }
            }
            else
            {
                var item = CreateForTool(normalised);
                if (item != null)
                {
                    slide.Items.Add(item);
                    SelectedId = item.Id;
                    changed = true;
                }
            }
        }

        // Freehand: keep collecting while the button is down.
        if (drawingStroke != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var last = drawingStroke.Points.Count > 0 ? drawingStroke.Points[^1] : normalised;
                if (Vector2.Distance(last, normalised) > 0.004f)
                {
                    drawingStroke.Points.Add(normalised);
                    changed = true;
                }
            }
            else
            {
                if (drawingStroke.Points.Count < 2)
                    slide.Items.Remove(drawingStroke);
                drawingStroke = null;
                changed = true;
            }
        }

        // Dragging an existing item.
        if (draggingId != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetIO().MouseDelta / side;
                if (delta != Vector2.Zero)
                {
                    var item = slide.Items.FirstOrDefault(i => i.Id == draggingId);
                    if (item != null)
                    {
                        item.Position += delta;
                        for (var i = 0; i < item.Points.Count; i++)
                            item.Points[i] += delta;
                        changed = true;
                    }
                }
            }
            else
            {
                draggingId = null;
            }
        }

        // Right click clears the pending arrow anchor, or deletes what is under the cursor.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            if (hasPendingStart)
            {
                hasPendingStart = false;
            }
            else
            {
                var hit = HitTest(slide, normalised);
                if (hit is { Locked: false })
                {
                    slide.Items.Remove(hit);
                    if (SelectedId == hit.Id)
                        SelectedId = null;
                    changed = true;
                }
            }
        }

        if (hovered && ImGui.IsKeyPressed(ImGuiKey.Delete, false) && SelectedId != null)
        {
            var item = slide.Items.FirstOrDefault(i => i.Id == SelectedId);
            if (item is { Locked: false })
            {
                slide.Items.Remove(item);
                SelectedId = null;
                changed = true;
            }
        }

        // Preview line for a half-placed arrow.
        if (hasPendingStart && hovered)
        {
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddLine(ToScreen(pendingStart), mouse, UiHelpers.WithAlpha(BrushColor, 0.6f), 2f);
        }

        return changed;
    }

    private CanvasItem? CreateForTool(Vector2 position)
    {
        return Tool switch
        {
            CanvasTool.PlayerToken => new CanvasItem
            {
                Kind = CanvasItemKind.PlayerToken,
                Position = position,
                Radius = 0.045f,
                SlotIndex = BrushSlot,
                Color = BrushColor,
            },

            CanvasTool.EnemyToken => new CanvasItem
            {
                Kind = CanvasItemKind.EnemyToken,
                Position = position,
                Radius = 0.06f,
                Color = 0xFF2222AA,
                Text = "B",
            },

            CanvasTool.Waymark => new CanvasItem
            {
                Kind = CanvasItemKind.Waymark,
                Position = position,
                Radius = 0.045f,
                Text = BrushWaymark,
            },

            CanvasTool.Label => new CanvasItem
            {
                Kind = CanvasItemKind.Label,
                Position = position,
                Text = "New label",
                Color = 0xFFFFFFFF,
            },

            CanvasTool.Zone => new CanvasItem
            {
                Kind = CanvasItemKind.Zone,
                Zone = BrushZone,
                Position = position,
                Radius = BrushZone == ZoneShape.Line ? 0.25f : 0.18f,
                InnerRadius = 0.09f,
                Extent = BrushZone is ZoneShape.Line or ZoneShape.Cross ? new Vector2(0.05f, 0.05f) : new Vector2(0.15f, 0.1f),
                Color = BrushColor,
                ConeAngle = 90f,
                Layer = -1,
            },

            _ => null,
        };
    }

    private CanvasItem? HitTest(Slide slide, Vector2 position)
    {
        CanvasItem? best = null;
        var bestLayer = int.MinValue;

        foreach (var item in slide.Items)
        {
            if (item.Locked)
                continue;

            var hit = item.Kind switch
            {
                CanvasItemKind.PlayerToken or CanvasItemKind.EnemyToken or CanvasItemKind.Waymark =>
                    Vector2.Distance(item.Position, position) <= MathF.Max(item.Radius, 0.03f),

                CanvasItemKind.Label =>
                    Vector2.Distance(item.Position, position) <= 0.05f,

                CanvasItemKind.Zone =>
                    HitZone(item, position),

                CanvasItemKind.Arrow or CanvasItemKind.Tether or CanvasItemKind.Freehand =>
                    HitPath(item, position),

                _ => false,
            };

            if (!hit)
                continue;

            // Tokens should win over the big zones they sit on top of.
            var layer = item.Layer * 10 + (item.Kind == CanvasItemKind.Zone ? 0 : 5);
            if (layer >= bestLayer)
            {
                bestLayer = layer;
                best = item;
            }
        }

        return best;
    }

    private static bool HitZone(CanvasItem item, Vector2 position)
    {
        var d = Vector2.Distance(item.Position, position);
        return item.Zone switch
        {
            ZoneShape.Circle or ZoneShape.Cone => d <= item.Radius,
            ZoneShape.Donut => d <= item.Radius,
            ZoneShape.Rectangle => MathF.Abs(position.X - item.Position.X) <= item.Extent.X + 0.02f &&
                                   MathF.Abs(position.Y - item.Position.Y) <= item.Extent.Y + 0.02f,
            ZoneShape.Line or ZoneShape.Cross => d <= MathF.Max(item.Radius, 0.06f),
            _ => d <= 0.08f,
        };
    }

    private static bool HitPath(CanvasItem item, Vector2 position)
    {
        const float tolerance = 0.02f;
        for (var i = 0; i < item.Points.Count - 1; i++)
        {
            if (DistanceToSegment(position, item.Points[i], item.Points[i + 1]) <= tolerance)
                return true;
        }

        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-6f)
            return Vector2.Distance(p, a);

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, a + (ab * t));
    }
}
