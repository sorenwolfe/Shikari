using System;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Shikari.Services.Buddy;
using Shikari.UI.Theme;

namespace Shikari.UI;

/// <summary>A quiet, optional companion HUD. All assignment decisions belong to BuddyService.</summary>
public sealed class BuddyWindow : Window, IDisposable
{
    private const string SpriteResource = "Shikari.Resources.buddy-dragon.png";
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private ISharedImmediateTexture? sprite;
    private bool triedSprite;
    private bool spriteFailed;
    private bool dragging;
    private Vector2 dragGrab;
    private Vector2 dragAnchor;
    private BuddyLayout.Placement placement;
    private BuddyPresentation presentation = new("", "", "", "", BuddyMood.Resting, false);
    private string cueKey = "";
    private double cueStarted;
    private float scale = 1;
    private float fontSize;
    private float textWidth;
    private string heading = "", body = "", detail = "";
    private float headingHeight, bodyHeight, detailHeight;

    public BuddyWindow() : base("##shikari-buddy", BaseFlags | ImGuiWindowFlags.NoInputs)
    {
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        ForceMainWindow = true;
        IsOpen = true;
    }

    public override bool DrawConditions()
    {
        var visible = Plugin.Config.BuddyEnabled && Plugin.ClientState.IsLoggedIn && !Plugin.ClientState.IsGPosing &&
            !Plugin.Condition[ConditionFlag.WatchingCutscene] && !Plugin.Condition[ConditionFlag.WatchingCutscene78] &&
            !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] && Plugin.Plans.Active is { Slides.Count: > 0 };
        if (!visible)
        {
            dragging = false;
            cueKey = "";
        }
        return visible;
    }

    public override void PreDraw()
    {
        var unlocked = Plugin.Config.BuddyUnlocked && !Plugin.Encounter.InCombat;
        Flags = unlocked ? BaseFlags : BaseFlags | ImGuiWindowFlags.NoInputs;
        if (!unlocked) dragging = false;
        presentation = Plugin.Buddy.Presentation;
        var viewport = ImGuiHelpers.MainViewport;
        var globalScale = float.IsFinite(UiHelpers.Scale) && UiHelpers.Scale > 0 ? UiHelpers.Scale : 1;
        var requestedScale = float.IsFinite(Plugin.Config.BuddyScale) ? Math.Clamp(Plugin.Config.BuddyScale, .65f, 1.6f) : 1;
        scale = BuddyLayout.FitScale(viewport.Size, requestedScale * globalScale);
        fontSize = ImGui.GetFontSize() * scale / globalScale;
        textWidth = 212f * scale;
        MeasureCue();
        var bubble = presentation.HasCue ? new Vector2(240 * scale, MathF.Max(40 * scale,
            24 * scale + headingHeight + bodyHeight + detailHeight +
            (body.Length > 0 ? 7 * scale : 0) + (detail.Length > 0 ? 8 * scale : 0))) : Vector2.Zero;
        var fit = MathF.Min(1, viewport.Size.Y / (MathF.Max(94 * scale, bubble.Y) + 18 * scale));
        if (fit > 0 && fit < 1)
        {
            // Respect unusual user fonts as well as the normal 9-line layout estimate.
            scale *= fit;
            fontSize *= fit;
            textWidth *= fit;
            headingHeight *= fit;
            bodyHeight *= fit;
            detailHeight *= fit;
            bubble *= fit;
        }
        placement = BuddyLayout.Place(viewport.Pos, viewport.Size, dragging ? dragAnchor : Plugin.Config.BuddyAnchor, scale, bubble);
        ImGui.SetNextWindowPos(placement.Position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(placement.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        // Stable cue keys prevent a continuous status observation restarting the animation.
        var key = presentation.HasCue ? presentation.Key : "";
        if (cueKey != key)
        {
            cueKey = key;
            cueStarted = ImGui.GetTime();
        }
    }

    public override void PostDraw() => ImGui.PopStyleVar();

    public override void Draw()
    {
        var draw = ImGui.GetWindowDrawList();
        if ((Flags & ImGuiWindowFlags.NoInputs) == 0) HandleDrag();
        var origin = ImGui.GetWindowPos();
        DrawPet(draw, origin + placement.SpriteMin);
        if (presentation.HasCue)
            DrawSpeech(draw, origin + placement.BubbleMin);
    }

    private void MeasureCue()
    {
        heading = body = detail = "";
        headingHeight = bodyHeight = detailHeight = 0;
        if (!presentation.HasCue) return;
        // Service cues are already concise. These final bounds also protect the overlay from
        // extremely long user-authored labels and unusual font sizes without a clipped sentence.
        heading = FitText(presentation.Heading, 2);
        body = FitText(presentation.Body, 5);
        detail = FitText(presentation.Detail, 2);
        headingHeight = TextHeight(heading);
        bodyHeight = TextHeight(body);
        detailHeight = TextHeight(detail);
    }

    private string FitText(string? value, int maxLines)
    {
        var text = (value ?? "").Trim();
        var maxHeight = fontSize * maxLines + .5f;
        if (TextHeight(text) <= maxHeight) return text;
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (TextHeight(Prefix(text, middle) + "…") <= maxHeight) low = middle;
            else high = middle - 1;
        }
        return Prefix(text, low).TrimEnd() + "…";
    }

    private static string Prefix(string text, int count)
    {
        if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1])) count--;
        return text[..count];
    }

    private float TextHeight(string text)
    {
        if (text.Length == 0) return 0;
        var ratio = fontSize / MathF.Max(1, ImGui.GetFontSize());
        return ImGui.CalcTextSize(text, false, textWidth / ratio).Y * ratio;
    }

    private void DrawPet(ImDrawListPtr draw, Vector2 min)
    {
        var motion = BuddyLayout.Motion(ImGui.GetTime(), Plugin.Config.BuddyReducedMotion);
        var size = placement.SpriteSize;
        var accent = presentation.Mood == BuddyMood.Uncertain ? 0xD6AB79u : Palette.DefaultAccent;
        var max = min + new Vector2(size);
        // A small portrait pod intentionally contains the original artwork's black background.
        // Its rounded silhouette stays quiet when no actionable cue is available.
        draw.AddRectFilled(min + new Vector2(-3, 4) * scale, max + new Vector2(3, 7) * scale,
            Palette.Pack(0x000000, .13f), 22 * scale);
        var breathSize = size * motion.Breath;
        var spriteMin = min + new Vector2((size - breathSize) / 2, size - breathSize + motion.OffsetY * scale);
        var spriteMax = spriteMin + new Vector2(breathSize);
        draw.AddRectFilled(spriteMin, spriteMax, Palette.Pack(0x000000, .90f), 18 * scale);
        if (TrySprite(out var handle))
            draw.AddImageRounded(handle, spriteMin, spriteMax,
                placement.BubbleOnLeft ? new Vector2(1, 0) : Vector2.Zero,
                placement.BubbleOnLeft ? new Vector2(0, 1) : Vector2.One, 0xFFFFFFFF, 18 * scale);
        else
            DrawFallback(draw, spriteMin, breathSize);
        draw.AddRect(spriteMin, spriteMax, Palette.Pack(accent, presentation.HasCue ? .28f : .12f),
            18 * scale, ImDrawFlags.None, MathF.Max(1, scale));

        if ((Flags & ImGuiWindowFlags.NoInputs) == 0)
        {
            var chrome = Palette.Pack(0xFFFFFF, .5f);
            draw.AddLine(min + new Vector2(size - 18 * scale, 10 * scale), min + new Vector2(size - 9 * scale, 10 * scale), chrome, scale);
            draw.AddLine(min + new Vector2(size - 18 * scale, 14 * scale), min + new Vector2(size - 9 * scale, 14 * scale), chrome, scale);
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && Inside(ImGui.GetMousePos(), min, max))
                UiHelpers.Tooltip("Drag Ember to move. Lock position in Settings when you're ready.");
        }
    }

    private void DrawSpeech(ImDrawListPtr draw, Vector2 min)
    {
        var alpha = BuddyLayout.Fade(ImGui.GetTime() - cueStarted, Plugin.Config.BuddyReducedMotion);
        var size = placement.BubbleSize;
        var max = min + size;
        var accent = presentation.Mood == BuddyMood.Uncertain ? 0xD6AB79u : Palette.DefaultAccent;
        draw.AddRectFilled(min + new Vector2(-3, 3) * scale, max + new Vector2(3, 6) * scale,
            Palette.Pack(0x000000, .20f * alpha), 14 * scale);
        draw.AddRectFilled(min, max, Palette.Pack(0x101014, .98f * alpha), 12 * scale);
        draw.AddRect(min, max, Palette.Pack(accent, .28f * alpha), 12 * scale, ImDrawFlags.None, MathF.Max(1, scale));
        var tailY = Math.Clamp(ImGui.GetWindowPos().Y + placement.SpriteMin.Y + placement.SpriteSize * .45f,
            min.Y + 14 * scale, max.Y - 14 * scale);
        var edge = placement.BubbleOnLeft ? max.X - 1 * scale : min.X + 1 * scale;
        var direction = placement.BubbleOnLeft ? 1 : -1;
        draw.AddTriangleFilled(new Vector2(edge, tailY - 5 * scale), new Vector2(edge + direction * 7 * scale, tailY),
            new Vector2(edge, tailY + 5 * scale), Palette.Pack(0x101014, .98f * alpha));
        var textPosition = min + new Vector2(14, 12) * scale;
        DrawText(draw, textPosition, Palette.Pack(accent, alpha), heading);
        textPosition.Y += headingHeight;
        if (body.Length > 0)
        {
            textPosition.Y += 7 * scale;
            DrawText(draw, textPosition, Palette.Pack(Palette.Text, alpha), body);
            textPosition.Y += bodyHeight;
        }
        if (detail.Length > 0)
        {
            textPosition.Y += 8 * scale;
            DrawText(draw, textPosition, Palette.Pack(0xBDBDC7, alpha), detail);
        }
    }

    private void DrawText(ImDrawListPtr draw, Vector2 at, uint color, string text)
    {
        if (text.Length > 0) draw.AddText(ImGui.GetFont(), fontSize, at, color, text, textWidth);
    }

    private bool TrySprite(out ImTextureID handle)
    {
        handle = default;
        if (spriteFailed) return false;
        try
        {
            if (!triedSprite)
            {
                triedSprite = true;
                sprite = Plugin.TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), SpriteResource);
            }
            if (sprite == null || !sprite.TryGetWrap(out var wrap, out _) || wrap == null) return false;
            handle = wrap.Handle;
            return true;
        }
        catch (Exception error)
        {
            spriteFailed = true;
            Plugin.Log.Warning(error, "Shikari could not display Ember's artwork.");
            return false;
        }
    }

    private void DrawFallback(ImDrawListPtr draw, Vector2 min, float size)
    {
        var center = min + new Vector2(size / 2);
        var ink = Palette.Pack(Palette.DefaultAccent, .9f);
        draw.AddCircleFilled(center, size * .2f, ink, 24);
        draw.AddTriangleFilled(center + new Vector2(-.2f, -.06f) * size, center + new Vector2(-.2f, -.3f) * size,
            center + new Vector2(-.04f, -.17f) * size, ink);
        draw.AddTriangleFilled(center + new Vector2(.04f, -.17f) * size, center + new Vector2(.2f, -.3f) * size,
            center + new Vector2(.2f, -.06f) * size, ink);
        draw.AddCircleFilled(center + new Vector2(.07f, -.03f) * size, size * .023f, 0xFFFFFFFF, 12);
    }

    private void HandleDrag()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var min = ImGui.GetWindowPos() + placement.SpriteMin;
        var center = min + new Vector2(placement.SpriteSize / 2);
        var mouse = ImGui.GetMousePos();
        if (!dragging && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) &&
            Inside(mouse, min, min + new Vector2(placement.SpriteSize)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            dragging = true;
            dragGrab = mouse - center;
            dragAnchor = BuddyLayout.Anchor(center, viewport.Pos, viewport.Size);
        }
        if (!dragging) return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var wantedAnchor = BuddyLayout.Anchor(mouse - dragGrab, viewport.Pos, viewport.Size);
            var moved = BuddyLayout.Place(viewport.Pos, viewport.Size, wantedAnchor, scale, placement.BubbleSize);
            dragAnchor = BuddyLayout.Anchor(moved.Position + moved.SpriteMin + new Vector2(moved.SpriteSize / 2), viewport.Pos, viewport.Size);
            placement = moved;
            ImGui.SetWindowPos(moved.Position, ImGuiCond.Always);
        }
        else
        {
            dragging = false;
            if (Vector2.Distance(Plugin.Config.BuddyAnchor, dragAnchor) > .0001f)
            {
                Plugin.Config.BuddyAnchor = dragAnchor;
                Plugin.SaveConfig();
            }
        }
    }

    private static bool Inside(Vector2 point, Vector2 min, Vector2 max) =>
        point.X >= min.X && point.Y >= min.Y && point.X <= max.X && point.Y <= max.Y;

    public void Dispose()
    {
        // Shared immediate textures are owned by Dalamud; release only this instance's reference.
        sprite = null;
        triedSprite = true;
        spriteFailed = true;
        dragging = false;
    }
}
