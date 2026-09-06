using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.RaidPlanIo;
using Shikari.Services.WtfDig;
using Shikari.UI.Theme;
namespace Shikari.UI;

public sealed partial class MainWindow
{
    private readonly WtfDigClient wtfClient = new();
    private string wtfUrl = "";
    private string wtfStatus = "";
    private bool wtfError;
    private bool wtfImages = true;
    private WtfDigGuide? wtfGuide;
    private WtfDigPreview? wtfPreview;
    private WtfDigSelection wtfSelection = new();
    private CancellationTokenSource? wtfCancel;
    private Task<WtfDigGuide>? wtfLoad;
    private Task<PreparedWtfDig>? wtfPrepared;
    private bool WtfBusy => wtfLoad != null || wtfPrepared != null;

    private void DrawWtfDigImport()
    {
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("WTFDIG");
        ImGui.TextWrapped("Bring a selected fight guide into your plan, or import one of its linked editable raidplans.");
        ImGui.BeginDisabled(WtfBusy);
        ImGui.SetNextItemWidth(-130 * UiHelpers.Scale);
        UiHelpers.InputTextHint("##wtfdig-link", "https://wtfdig.info/74/m9s", ref wtfUrl, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Load guide", new Vector2(-1, 0)))
        {
            try
            {
                var link = WtfDigLink.Parse(wtfUrl);
                wtfGuide = null; wtfPreview = null;
                wtfCancel = CancellationTokenSource.CreateLinkedTokenSource(Plugin.Shutdown);
                var token = wtfCancel.Token;
                wtfLoad = Task.Run(() => wtfClient.LoadAsync(link, token), token);
                wtfStatus = "Loading guide data…"; wtfError = false;
            }
            catch (Exception ex) { WtfFail(ex); }
        }
        ImGui.EndDisabled();
        if (WtfBusy && ImGui.SmallButton("Cancel WTFDIG import")) wtfCancel?.Cancel();
        if (wtfStatus.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Vec(wtfError ? Palette.Attention : Palette.Text));
            ImGui.TextWrapped(wtfStatus); ImGui.PopStyleColor();
        }
        if (wtfGuide == null) return;
        ImGui.BeginDisabled(WtfBusy);
        ImGui.TextUnformatted(wtfGuide.Title);
        ImGui.TextDisabled("Verify selections below. Link options without a matching control are not applied.");
        if (ImGui.BeginCombo("Strategy##wtfdig", wtfSelection.Strategy.Length == 0 ? "Choose a strategy" : wtfSelection.Strategy))
        {
            foreach (var strat in wtfGuide.Strategies)
            {
                var id = (string)strat["stratName"]!;
                var label = wtfGuide.Config["strats"] is JObject definitions ? (string?)definitions[id]?["label"] ?? id : id;
                if (ImGui.Selectable(label + "##" + id, id == wtfSelection.Strategy))
                { wtfSelection.Strategy = id; wtfSelection.Variants.Clear(); RebuildWtfPreview(); }
            }
            ImGui.EndCombo();
        }
        if (ImGui.BeginCombo("Role##wtfdig", wtfSelection.Role))
        {
            foreach (var role in new[] { "Tank", "Healer", "Melee", "Ranged" })
                if (ImGui.Selectable(role, role == wtfSelection.Role)) { wtfSelection.Role = role; RebuildWtfPreview(); }
            ImGui.EndCombo();
        }
        var group = wtfSelection.Party;
        if (ImGui.SliderInt("Group##wtfdig", ref group, 1, 2)) { wtfSelection.Party = group; RebuildWtfPreview(); }
        if (wtfPreview != null)
        {
            foreach (var variant in wtfPreview.VariantChoices.ToArray())
            {
                wtfSelection.Variants.TryGetValue(variant.Key, out var selected);
                if (ImGui.BeginCombo(variant.Key + "##wtfdig-variant", selected ?? "Choose variant"))
                {
                    foreach (var choice in variant.Choices)
                        if (ImGui.Selectable(choice, choice == selected)) { wtfSelection.Variants[variant.Key] = choice; RebuildWtfPreview(); }
                    ImGui.EndCombo();
                }
            }
            var preview = wtfPreview;
            if (preview == null) { ImGui.EndDisabled(); return; }
            ImGui.TextUnformatted($"{preview.Plan.Slides.Count} guide slides / {preview.Plan.Timeline.Count} disabled timings / {preview.Links.Count} linked raidplans");
            if (preview.MissingVariants.Count > 0) ImGui.TextColored(Palette.Vec(Palette.Attention), "Choose the remaining variants before importing.");
            if (ImGui.TreeNode("Preview guide and conversion notes##wtfdig"))
            {
                foreach (var warning in preview.Warnings) ImGui.TextWrapped(warning);
                foreach (var slide in preview.Plan.Slides)
                    if (ImGui.TreeNode(slide.Title + "##" + slide.Id)) { ImGui.TextWrapped(slide.Notes); ImGui.TreePop(); }
                ImGui.TreePop();
            }
            ImGui.Checkbox("Download reference diagrams when available", ref wtfImages);
            ImGui.BeginDisabled(preview.Plan.Slides.Count == 0 || preview.MissingVariants.Count > 0);
            if (ImGui.Button("Import guide as a new plan"))
            {
                wtfCancel = CancellationTokenSource.CreateLinkedTokenSource(Plugin.Shutdown);
                var token = wtfCancel.Token;
                var folder = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "backdrops");
                var images = wtfImages;
                wtfPrepared = Task.Run(() => wtfClient.PrepareAsync(preview, folder, images, token), token);
                wtfStatus = "Preparing guide and reference diagrams…"; wtfError = false;
            }
            ImGui.EndDisabled();
            if (preview.Links.Count > 0 && ImGui.TreeNode("Import an editable raidplan instead##wtfdig"))
            {
                ImGui.TextWrapped("This imports the complete linked board as its own plan. A link's step number remains in source notes; it does not crop the board.");
                foreach (var link in preview.Links)
                {
                    ImGui.PushID(link.Code);
                    ImGui.TextWrapped(link.Label);
                    if (ImGui.Button("Import editable board")) StartWtfBoard(link, preview.Plan.Notes);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Copy source link")) ImGui.SetClipboardText(link.Url);
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
        }
        ImGui.EndDisabled();
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
    }

    private void RebuildWtfPreview()
    {
        if (wtfGuide == null || wtfSelection.Strategy.Length == 0) { wtfPreview = null; return; }
        try { wtfPreview = WtfDigMapper.Convert(wtfGuide, wtfSelection); wtfError = false; }
        catch (Exception ex) { wtfPreview = null; WtfFail(ex); }
    }
    private void StartWtfBoard(WtfDigBoardLink link, string sourceNotes)
    {
        if (WtfBusy) return;
        wtfCancel = CancellationTokenSource.CreateLinkedTokenSource(Plugin.Shutdown);
        var token = wtfCancel.Token;
        var fetcher = Plugin.PlanFetcher;
        wtfPrepared = Task.Run(async () =>
        {
            var json = await fetcher.GetAsync(link.Code, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!RaidPlanIoImporter.TryImport(json, out var plan, out var report, out var error) || plan == null)
                throw new InvalidDataException(error);
            plan.Name = link.Label + " / " + link.Code;
            plan.Notes = sourceNotes + "\nEditable board source: " + link.Url + "\n" + plan.Notes;
            foreach (var step in plan.Timeline) step.Enabled = false;
            var result = new PreparedWtfDig { Plan = plan };
            result.Warnings.Add(report.Summary()); result.Warnings.AddRange(report.Notes);
            return result;
        }, token);
        wtfStatus = "Loading editable raidplan…"; wtfError = false;
    }
    private void PollWtfDig()
    {
        if (wtfLoad?.IsCompleted == true)
        {
            try
            {
                var guide = wtfLoad.GetAwaiter().GetResult();
                wtfCancel?.Token.ThrowIfCancellationRequested();
                wtfGuide = guide; wtfSelection = new WtfDigSelection();
                var opts = guide.Link.Options;
                if (opts.TryGetValue("role", out var role) && role is "Tank" or "Healer" or "Melee" or "Ranged") wtfSelection.Role = role;
                if (opts.TryGetValue("party", out var group) && int.TryParse(group, out var g) && g is 1 or 2) wtfSelection.Party = g;
                var strat = opts.GetValueOrDefault("strat", opts.GetValueOrDefault("stratName", ""));
                if (guide.Strategies.Any(s => (string?)s["stratName"] == strat)) wtfSelection.Strategy = strat;
                foreach (var option in opts) wtfSelection.Variants["toggle/" + option.Key] = option.Value;
                RebuildWtfPreview();
                if (!wtfError) wtfStatus = "Guide loaded. Choose and inspect the strategy before importing.";
            }
            catch (Exception ex) { WtfFail(ex); }
            finally { wtfLoad = null; wtfCancel?.Dispose(); wtfCancel = null; }
        }
        if (wtfPrepared?.IsCompleted == true)
        {
            try
            {
                using var prepared = wtfPrepared.GetAwaiter().GetResult();
                wtfCancel?.Token.ThrowIfCancellationRequested();
                if (Plugin.Encounter.InCombat) throw new InvalidOperationException("Import finished during combat. Retry after the pull; the active plan was not changed.");
                if (!Plugin.Plans.SaveActive()) throw new IOException(Plugin.Plans.LastSaveError ?? "Could not save the current plan.");
                // Retain assets once the plan enters the store, even if a subsequent save reports an error.
                prepared.Retained = true;
                Plugin.Plans.Import(prepared.Plan, replaceExisting: false);
                slideIndex = 0; selectedEntryId = null; canvas.Select(null); MarkDirty();
                wtfStatus = "Imported " + prepared.Plan.Name + ".\n" + string.Join("\n", prepared.Warnings);
                if (Plugin.Plans.LastSaveError != null) wtfStatus += "\nSave pending: " + Plugin.Plans.LastSaveError;
            }
            catch (Exception ex) { WtfFail(ex); }
            finally { wtfPrepared = null; wtfCancel?.Dispose(); wtfCancel = null; }
        }
    }
    private void WtfFail(Exception ex) { wtfError = true; wtfStatus = ex is OperationCanceledException ? "WTFDIG import cancelled." : "WTFDIG import: " + ex.Message; }
    private void DisposeWtfDig()
    {
        wtfCancel?.Cancel();
        if (wtfPrepared != null) _ = wtfPrepared.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
        if (wtfLoad != null) _ = wtfLoad.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        wtfClient.Dispose(); wtfCancel?.Dispose();
    }
}
