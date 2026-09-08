using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services.Symbols;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private void DrawSymbolProperties(CanvasItem item)
    {
        var emoji = item.Emoji;
        ImGui.SetNextItemWidth(-1);
        if (UiHelpers.InputTextHint("##symbol-emoji", "Paste an emoji", ref emoji, 64))
        {
            if (emoji.Length == 0 || SymbolValidation.IsSingleGrapheme(emoji))
            {
                item.Emoji = emoji;
                if (emoji.Length > 0) { item.IconId = 0; item.SymbolAsset = SymbolAsset.None; }
                if (emoji.Length == 0 && item.IconId == 0 && item.SymbolAsset == SymbolAsset.None && string.IsNullOrWhiteSpace(item.Text)) item.Text = "Symbol";
                MarkDirty();
            }
        }
        if (item.Emoji.Length > 0 && !EmojiCatalog.TryResolve(item.Emoji, out _))
            ImGui.TextWrapped("This symbol uses its caption because its artwork is unavailable.");
        if (item.IconId > 0)
            ImGui.TextDisabled("Game artwork: " + item.IconId);
        else if (item.SymbolAsset == SymbolAsset.Cut4)
            ImGui.TextDisabled("Four-hit marker");

        var width = item.Extent.X * 2;
        var height = item.Extent.Y * 2;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("Width##symbol", ref width, .01f, 1f, "%.3f", ImGuiSliderFlags.None))
        { item.Extent = new(width / 2, item.Extent.Y); MarkDirty(); }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("Height##symbol", ref height, .01f, 1f, "%.3f", ImGuiSliderFlags.None))
        { item.Extent = new(item.Extent.X, height / 2); MarkDirty(); }
        var horizontal = item.FlipX;
        if (ImGui.Checkbox("Flip horizontally", ref horizontal)) { item.FlipX = horizontal; MarkDirty(); }
        var vertical = item.FlipY;
        if (ImGui.Checkbox("Flip vertically", ref vertical)) { item.FlipY = vertical; MarkDirty(); }
    }
}
