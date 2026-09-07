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
    private string wtfStatus = "";
    private bool wtfError;
    private bool wtfImages = true;
    private WtfDigGuide? wtfGuide;
    private WtfDigPreview? wtfPreview;
    private WtfDigSelection wtfSelection = new();
    private CancellationTokenSource? wtfCancel;
    private Task<WtfDigGuide>? wtfLoad;
    private Task<PreparedWtfDig>? wtfPrepared;
    private bool wtfAutoImport;
    private readonly List<string> wtfDetails = new();
    private bool WtfBusy => wtfLoad != null || wtfPrepared != null;

    private void DrawWtfDigImport()
    {
        if (WtfBusy && ImGui.SmallButton("Cancel WTFDIG import")) wtfCancel?.Cancel();
        if (wtfStatus.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Vec(wtfError ? Palette.Attention : Palette.Text));
            ImGui.TextWrapped(wtfStatus); ImGui.PopStyleColor();
        }
        if (wtfDetails.Count > 0 && ImGui.TreeNode($"Import details ({wtfDetails.Count})##wtfdig-status"))
        {
            if (ImGui.BeginChild("wtfdig-status-details", new Vector2(0, 180 * UiHelpers.Scale), true, ImGuiWindowFlags.None))
                foreach (var detail in wtfDetails) ImGui.TextWrapped(detail);
            ImGui.EndChild();
            ImGui.TreePop();
        }
        if (wtfGuide == null) return;
        ImGui.BeginDisabled(WtfBusy || Plugin.Encounter.InCombat);
        ImGui.TextUnformatted(wtfGuide.Title);
        ImGui.TextDisabled("Linked editable boards are included automatically. Adjust options below when needed.");
        if (ImGui.BeginCombo("Strategy##wtfdig", wtfSelection.Strategy.Length == 0 ? "Choose a strategy" : wtfSelection.Strategy))
        {
            foreach (var strat in wtfGuide.Strategies)
            {
                var id = (string)strat["stratName"]!;
                var label = wtfGuide.Config["strats"] is JObject definitions ? (string?)definitions[id]?["label"] ?? id : id;
                if (ImGui.Selectable(label + "##" + id, id == wtfSelection.Strategy))
                { wtfSelection.Strategy = id; ApplyWtfDefaults(); RebuildWtfPreview(); }
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
            var choicesPreview = wtfPreview;
            foreach (var variant in choicesPreview.VariantChoices.ToArray())
            {
                wtfSelection.Variants.TryGetValue(variant.Key, out var selected);
                var unresolved = choicesPreview.MissingVariants.Any(v => v.Key == variant.Key);
                if (ImGui.BeginCombo(variant.Key + "##wtfdig-variant", selected ?? (unresolved ? "Choose variant" : "Strategy default")))
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
                if (ImGui.BeginChild("wtfdig-preview-details", new Vector2(0, 240 * UiHelpers.Scale), true, ImGuiWindowFlags.None))
                {
                    foreach (var warning in preview.Warnings) ImGui.TextWrapped(warning);
                    foreach (var slide in preview.Plan.Slides)
                        if (ImGui.TreeNode(slide.Title + "##" + slide.Id)) { ImGui.TextWrapped(slide.Notes); ImGui.TreePop(); }
                }
                ImGui.EndChild();
                ImGui.TreePop();
            }
            ImGui.Checkbox("Download reference diagrams when available", ref wtfImages);
            ImGui.BeginDisabled(preview.Plan.Slides.Count == 0 || preview.MissingVariants.Count > 0);
            if (!wtfAutoImport && ImGui.Button(wtfError ? "Retry combined import" : "Import selected options as a new plan")) StartWtfPreparation();
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
    }

    private void StartWtfGuide(string url)
    {
        if (WtfBusy || Plugin.Encounter.InCombat) return;
        try
        {
            var link = WtfDigLink.Parse(url);
            wtfGuide = null; wtfPreview = null; wtfAutoImport = false; wtfDetails.Clear();
            wtfCancel = CancellationTokenSource.CreateLinkedTokenSource(Plugin.Shutdown);
            var token = wtfCancel.Token;
            wtfLoad = Task.Run(() => wtfClient.LoadAsync(link, token), token);
            wtfStatus = "Loading guide data…"; wtfError = false;
        }
        catch (Exception ex) { WtfFail(ex); }
    }

    private void RebuildWtfPreview()
    {
        if (wtfGuide == null || wtfSelection.Strategy.Length == 0) { wtfPreview = null; return; }
        try { wtfPreview = WtfDigMapper.Convert(wtfGuide, wtfSelection); wtfError = false; }
        catch (Exception ex) { wtfPreview = null; WtfFail(ex); }
    }
    private void ApplyWtfDefaults()
    {
        if (wtfGuide == null) return;
        wtfSelection.Variants.Clear();
        foreach (var toggle in (wtfGuide.Config["toggles"] as JArray ?? new JArray()).OfType<JObject>())
            if (toggle["key"]?.Type == JTokenType.String && toggle["defaultValue"]?.Type == JTokenType.String)
                wtfSelection.Variants["toggle/" + toggle["key"]] = toggle["defaultValue"]!.ToString();
        if (wtfGuide.Config["strats"]?[wtfSelection.Strategy]?["defaults"] is JObject defaults)
            foreach (var option in defaults.Properties().Where(p => p.Value.Type == JTokenType.String))
                wtfSelection.Variants["toggle/" + option.Name] = option.Value.ToString();
        foreach (var option in wtfGuide.Link.Options)
            wtfSelection.Variants["toggle/" + option.Key] = option.Value;
    }
    private void StartWtfPreparation()
    {
        if (WtfBusy || Plugin.Encounter.InCombat || wtfPreview == null || wtfPreview.MissingVariants.Count > 0 || wtfPreview.Plan.Slides.Count == 0) return;
        wtfAutoImport = false;
        wtfCancel = CancellationTokenSource.CreateLinkedTokenSource(Plugin.Shutdown);
        var token = wtfCancel.Token;
        var fetcher = Plugin.PlanFetcher;
        var folder = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "backdrops");
        var preview = wtfPreview;
        var images = wtfImages;
        wtfPrepared = Task.Run(() => wtfClient.PrepareWithBoardsAsync(preview, folder, images, fetcher.GetAsync, token), token);
        wtfStatus = "Preparing guide, editable boards and reference diagrams…"; wtfError = false; wtfDetails.Clear();
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
                wtfSelection.Role = Plugin.Actions.Job(Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0)?.Role switch
                {
                    RaidRole.Healer => "Healer", RaidRole.Melee => "Melee",
                    RaidRole.PhysicalRanged or RaidRole.MagicalRanged => "Ranged", _ => "Tank",
                };
                var opts = guide.Link.Options;
                if (opts.TryGetValue("role", out var role) && role is "Tank" or "Healer" or "Melee" or "Ranged") wtfSelection.Role = role;
                if (opts.TryGetValue("party", out var group) && int.TryParse(group, out var g) && g is 1 or 2) wtfSelection.Party = g;
                var strat = opts.GetValueOrDefault("strat", opts.GetValueOrDefault("stratName", (string?)guide.Config["defaultStratName"] ?? ""));
                if (strat.Length == 0 && guide.Strategies.Count == 1) strat = (string?)guide.Strategies[0]["stratName"] ?? "";
                if (guide.Strategies.Any(s => (string?)s["stratName"] == strat)) wtfSelection.Strategy = strat;
                ApplyWtfDefaults();
                RebuildWtfPreview();
                if (!wtfError) { wtfAutoImport = true; wtfStatus = "Guide loaded. Choose any unresolved options to complete the import."; }
            }
            catch (Exception ex) { WtfFail(ex); }
            finally { wtfLoad = null; wtfCancel?.Dispose(); wtfCancel = null; }
        }
        // Start only after the load's finally releases its cancellation source. Replacing it
        // inside the try would dispose the new preparation's token source immediately.
        if (wtfAutoImport && !WtfBusy && wtfPreview?.MissingVariants.Count == 0)
        {
            if (Plugin.Encounter.InCombat) WtfFail(new InvalidOperationException("Guide loaded during combat. Retry after the pull."));
            else StartWtfPreparation();
        }
        if (wtfPrepared?.IsCompleted == true)
        {
            try
            {
                using var prepared = wtfPrepared.GetAwaiter().GetResult();
                wtfCancel?.Token.ThrowIfCancellationRequested();
                if (Plugin.Encounter.InCombat) throw new InvalidOperationException("Import finished during combat. Retry after the pull; the active plan was not changed.");
                if (!Plugin.Plans.SaveActive()) throw new IOException(Plugin.Plans.LastSaveError ?? "Could not save the current plan.");
                // Retain only after acceptance, including a store insertion followed by a
                // save/activation failure. Rejected imports must release their staged assets.
                try { Plugin.Plans.Import(prepared.Plan, replaceExisting: false); }
                finally { prepared.Retained = Plugin.Plans.Ordered.Any(p => ReferenceEquals(p, prepared.Plan)); }
                slideIndex = Math.Max(0, prepared.Plan.Slides.FindIndex(s => s.Items.Count > 0));
                planTool = 0; selectedEntryId = null; canvas.Select(null); MarkDirty();
                wtfStatus = "Imported " + prepared.Plan.Name + $" ({prepared.Plan.Slides.Count} slides; {wtfSelection.Role}, group {wtfSelection.Party}).";
                wtfError = false; wtfDetails.Clear(); wtfDetails.AddRange(prepared.Warnings.Distinct());
                if (Plugin.Plans.LastSaveError != null) { wtfStatus += " Save pending."; wtfDetails.Add(Plugin.Plans.LastSaveError); }
            }
            catch (Exception ex) { WtfFail(ex); }
            finally { wtfPrepared = null; wtfCancel?.Dispose(); wtfCancel = null; }
        }
    }
    private void WtfFail(Exception ex) { wtfAutoImport = false; wtfError = true; wtfStatus = ex is OperationCanceledException ? "WTFDIG import cancelled." : "WTFDIG import: " + ex.Message; }
    private void DisposeWtfDig()
    {
        wtfCancel?.Cancel();
        if (wtfPrepared != null) _ = wtfPrepared.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
        if (wtfLoad != null) _ = wtfLoad.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        wtfClient.Dispose(); wtfCancel?.Dispose();
    }
}
