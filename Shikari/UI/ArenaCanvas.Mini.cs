using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Live;

namespace Shikari.UI;

public sealed partial class ArenaCanvas
{
    /// <summary>Screen-sized markers and a clear foreground for the combat mini window.</summary>
    public bool MiniPresentation { get; set; }
    /// <summary>Remove other player markers and captions, preserving mechanic geometry.</summary>
    public bool MiniYourView { get; set; }
    private const uint MiniTarget = 0xFFFFDE70;
    private const uint MiniArrived = 0xFFA2ED80;
    private const uint MiniInk = 0xFF171310;
    private readonly List<(Vector2 Anchor, string Text, uint Color, int Priority)> miniLabels = new();
    private readonly List<MiniMapLayout.Box> miniOccupied = new();
    private readonly List<(MiniMapLayout.Box Box, Vector2 Anchor, string Text, uint Color, int Priority)> miniPlaced = new();

    private bool miniSettled;
    private string miniSettleKey = "";
    private Vector2 miniSettlePosition;

    private void MeasureMiniSettle(Slide slide)
    {
        var target = MiniMapLayout.Target(slide, HighlightSlot);
        var key = slide.Id + "/" + target?.Id + "/" + HighlightSlot;
        if (key != miniSettleKey || target?.Position != miniSettlePosition) miniSettled = false;
        miniSettleKey = key;
        miniSettlePosition = target?.Position ?? default;
        var local = LivePlayers?.FirstOrDefault(p => p.IsLocal && p.SlotIndex == HighlightSlot);
        if (target == null || local == null || !local.Value.IsLocal ||
            local.Value.Board.X < 0 || local.Value.Board.X > 1 || local.Value.Board.Y < 0 || local.Value.Board.Y > 1)
        { miniSettled = false; return; }
        SettleDistance = Vector2.Distance(local.Value.Board, target.Position);
        miniSettled = StandingSpot.IsSatisfied(miniSettled, SettleDistance, SettleTolerance);
    }
    private void DrawMiniEnemy(ImDrawListPtr draw, CanvasItem item, Vector2 at)
    {
        // Large imported boss footprints must not turn into opaque discs in the mini window.
        var radius = MathF.Max(7 * UiHelpers.Scale, item.Radius * side);
        draw.AddCircleFilled(at, radius, MiniMapLayout.HazardFill(item.Color), 64);
        draw.AddCircle(at, radius, item.Color | 0xCC000000, 64, 1.5f * UiHelpers.Scale);
        draw.AddCircleFilled(at, 9 * UiHelpers.Scale, MiniInk, 24);
        draw.AddCircle(at, 9 * UiHelpers.Scale, item.Color | 0xFF000000, 24, 1.5f * UiHelpers.Scale);
        var label = string.IsNullOrWhiteSpace(item.Text) ? "B" : item.Text;
        UiHelpers.CenteredShadowText(draw, at, label.Length <= 2 ? label : "B", 0xFFFFFFFF);
        if (label.Length > 2) miniLabels.Add((at, label.Length > 12 ? label[..11] + "…" : label, item.Color | 0xFF000000, 3));
    }
    private void DrawMiniToken(ImDrawListPtr draw, PlanDocument plan, CanvasItem item, Vector2 at)
    {
        if (MiniYourView && (HighlightSlot < 0 || item.SlotIndex != HighlightSlot)) return;
        var color = item.Color | 0xFF000000;
        if (item.SlotIndex >= 0 && item.SlotIndex < plan.Roster.Count)
        {
            var slot = plan.Roster[item.SlotIndex];
            color = (slot.Color != 0 ? slot.Color : RoleColors.Default(slot.Role)) | 0xFF000000;
        }
        var radius = 5f * UiHelpers.Scale;
        draw.AddCircleFilled(at, radius + 2 * UiHelpers.Scale, MiniInk, 24);
        draw.AddCircleFilled(at, radius, color, 24);
        draw.AddCircle(at, radius, 0xFFFFFFFF, 24, UiHelpers.Scale);
        // Labels are laid out separately so a stack remains a stack in board coordinates.
        if (item.SlotIndex != HighlightSlot || HighlightSlot < 0)
            miniLabels.Add((at, MiniMapLayout.Label(plan, item), color, 2));
    }

    private void DrawMiniLivePlayers(ImDrawListPtr draw, PlanDocument plan, Slide slide)
    {
        if (LivePlayers == null) return;
        foreach (var player in LivePlayers.OrderBy(p => p.IsLocal))
        {
            if (MiniYourView && !player.IsLocal) continue;
            var at = ToScreen(player.Board);
            // An off-board position must not masquerade as a clamped position inside the arena.
            if (player.Board.X < 0 || player.Board.X > 1 || player.Board.Y < 0 || player.Board.Y > 1) continue;
            var radius = (player.IsLocal ? 7f : 4f) * UiHelpers.Scale;
            if (!player.IsLocal)
            {
                draw.AddCircleFilled(at, radius + 2 * UiHelpers.Scale, MiniInk, 20);
                draw.AddCircle(at, radius, FocusOnMe ? 0xBBFFFFFF : 0xFFFFFFFF, 20, 1.5f * UiHelpers.Scale);
                continue;
            }
            var target = MiniMapLayout.Target(slide, HighlightSlot);
            if (LiveGuides && target != null && player.SlotIndex == HighlightSlot && !Settled)
            {
                var to = ToScreen(target.Position);
                var delta = to - at;
                var length = delta.Length();
                if (length > 24 * UiHelpers.Scale)
                {
                    var direction = delta / length;
                    var end = to - direction * 13 * UiHelpers.Scale;
                    draw.AddLine(at, end, MiniInk, 6 * UiHelpers.Scale);
                    draw.AddLine(at, end, MiniTarget, 2.5f * UiHelpers.Scale);
                    var normal = new Vector2(-direction.Y, direction.X);
                    draw.AddTriangleFilled(end, end - direction * 9 * UiHelpers.Scale + normal * 4 * UiHelpers.Scale,
                        end - direction * 9 * UiHelpers.Scale - normal * 4 * UiHelpers.Scale, MiniTarget);
                }
            }
            draw.AddCircleFilled(at, radius + 3 * UiHelpers.Scale, MiniInk, 24);
            draw.AddQuadFilled(at + new Vector2(0, -radius), at + new Vector2(radius, 0), at + new Vector2(0, radius), at + new Vector2(-radius, 0), 0xFFFFFFFF);
            if (!Settled) miniLabels.Add((at, "YOU", 0xFFFFFFFF, 1));
        }
    }

    private void DrawMiniDestination(ImDrawListPtr draw, Slide slide)
    {
        var target = MiniMapLayout.Target(slide, HighlightSlot);
        if (target == null)
        {
            // Alternative tokens stay labelled, but none is presented as a resolved instruction.
            foreach (var item in slide.Items.Where(i => i.Kind == CanvasItemKind.PlayerToken && HighlightSlot >= 0 && i.SlotIndex == HighlightSlot))
                miniLabels.Add((ToScreen(item.Position), "OPTION", MiniTarget, 0));
            return;
        }
        var at = ToScreen(target.Position);
        var radius = 12f * UiHelpers.Scale;
        var color = Settled ? MiniArrived : MiniTarget;
        draw.AddCircle(at, radius, MiniInk, 40, 6 * UiHelpers.Scale);
        draw.AddCircle(at, radius, color, 40, 2.5f * UiHelpers.Scale);
        // A steady crosshair marks the planned position even when there is no alignment.
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathF.PI / 2;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(at + direction * (radius + 3 * UiHelpers.Scale), at + direction * (radius + 7 * UiHelpers.Scale), MiniInk, 5 * UiHelpers.Scale);
            draw.AddLine(at + direction * (radius + 3 * UiHelpers.Scale), at + direction * (radius + 7 * UiHelpers.Scale), color, 2 * UiHelpers.Scale);
        }
        miniLabels.Add((at, Settled ? "IN POSITION" : "YOUR SPOT", color, 0));
    }

    private void DrawMiniLabels(ImDrawListPtr draw)
    {
        miniOccupied.Clear();
        miniPlaced.Clear();
        // Reserve exact marker centers before placing captions so labels do not hide another seat.
        foreach (var label in miniLabels)
        {
            var radius = (label.Priority == 0 ? 19 : 8) * UiHelpers.Scale;
            miniOccupied.Add(new MiniMapLayout.Box(label.Anchor - new Vector2(radius), label.Anchor + new Vector2(radius)));
        }
        foreach (var label in miniLabels.OrderBy(l => l.Priority))
        {
            var textSize = UiHelpers.TextSize(label.Text);
            var box = MiniMapLayout.Place(label.Anchor, textSize + new Vector2(10, 5) * UiHelpers.Scale,
                origin + new Vector2(4 * UiHelpers.Scale), origin + new Vector2(side - 4 * UiHelpers.Scale), miniOccupied, (label.Priority == 0 ? 21 : 9) * UiHelpers.Scale);
            if (label.Priority > 0 && miniPlaced.Any(p => box.Overlaps(p.Box))) continue;
            miniPlaced.Add((box, label.Anchor, label.Text, label.Color, label.Priority));
            miniOccupied.Add(new MiniMapLayout.Box(box.Min - new Vector2(2 * UiHelpers.Scale), box.Max + new Vector2(2 * UiHelpers.Scale)));
        }
        // All leader lines go under all captions. Draw the personal labels last if space is tight.
        foreach (var label in miniPlaced)
        {
            var attach = Vector2.Clamp(label.Anchor, label.Box.Min, label.Box.Max);
            draw.AddLine(label.Anchor, attach, MiniInk, 3 * UiHelpers.Scale);
            draw.AddLine(label.Anchor, attach, label.Color, UiHelpers.Scale);
        }
        foreach (var label in miniPlaced.OrderByDescending(l => l.Priority))
        {
            var box = label.Box;
            draw.AddRectFilled(box.Min, box.Max, MiniInk, 4 * UiHelpers.Scale);
            draw.AddRect(box.Min, box.Max, label.Color, 4 * UiHelpers.Scale, ImDrawFlags.None, UiHelpers.Scale);
            UiHelpers.CenteredShadowText(draw, box.Center, label.Text, 0xFFFFFFFF);
        }
    }
}
