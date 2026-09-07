using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.FfLogs;
using Shikari.Services.RaidPlanIo;
using Shikari.UI.Theme;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private string unifiedImportInput = string.Empty;
    private ImportSource importSource = ImportSource.Parse("");
    private bool replaceSharedPlan;
    private bool ImportBusy => importBusy || WtfBusy;
    private string reportInput = string.Empty;
    private string importStatusLine = string.Empty;
    private bool importFailed;
    private string importDetail = string.Empty;

    private List<LogFight>? fights;
    private int selectedFight = -1;
    private LogFightData? loadedFight;
    private Task<Action>? importLoad;
    private bool importBusy => importLoad != null;

    private readonly ImportOptions importOptions = new();
    private bool showCredentials;

    private string planLink = string.Empty;
    private string planFilePath = string.Empty;
    private string planFileStatus = string.Empty;
    private bool planFileFailed;

    private void DrawImportTab(PlanDocument plan)
    {
        using (Plugin.Fonts.PushHeading()) ImGui.TextUnformatted("Bring in a strategy");
        ImGui.TextDisabled("Paste a link or share code. Shikari will handle the source.");
        ImGui.Spacing();
        ImGui.BeginDisabled(ImportBusy);
        ImGui.SetNextItemWidth(-112 * UiHelpers.Scale);
        if (UiHelpers.InputTextHint("##unified-import", "WTFDIG, raidplan.io, FF Logs, or a Shikari share code…",
                ref unifiedImportInput, ImportSource.MaxLength))
            ResetImportPreview();
        var submit = ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter, false);
        ImGui.SameLine();
        ImGui.BeginDisabled(importSource.Kind == ImportKind.Unknown || Plugin.Encounter.InCombat);
        var clicked = ImGui.Button(ImportBusy ? "Loading…" : "Import", new Vector2(-1, 0));
        ImGui.EndDisabled();
        if ((clicked || submit) && !ImportBusy && !Plugin.Encounter.InCombat)
            StartUnifiedImport();
        ImGui.EndDisabled();
        ImGui.TextWrapped(importSource.Hint);
        if (Plugin.Encounter.InCombat) ImGui.TextDisabled("Import after the pull finishes.");
        ImGui.Spacing();
        switch (importSource.Kind)
        {
            case ImportKind.WtfDig:
                DrawWtfDigImport();
                break;
            case ImportKind.FfLogs:
                DrawFfLogsImport(plan);
                break;
            case ImportKind.ShareCode:
                ImGui.BeginDisabled(ImportBusy || Plugin.Encounter.InCombat);
                ImGui.Checkbox("Update the saved plan with the same ID", ref replaceSharedPlan);
                ImGui.EndDisabled();
                ImGui.SameLine();
                UiHelpers.HelpMarker("Leave this off to import a new copy. Turn it on to replace the matching saved plan with your raid lead's update.");
                if (!string.IsNullOrEmpty(importStatus))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Palette.Vec(importStatusIsError ? Palette.Danger : Palette.Good));
                    ImGui.TextWrapped(importStatus);
                    ImGui.PopStyleColor();
                }
                break;
            case ImportKind.RaidPlan:
                if (importBusy) ImGui.TextDisabled("Fetching the editable plan…");
                break;
        }
        DrawImportStatus();
        DrawPlanFileImport();
    }

    private void ResetImportPreview()
    {
        importSource = ImportSource.Parse(unifiedImportInput);
        fights = null;
        selectedFight = -1;
        loadedFight = null;
        wtfGuide = null;
        wtfPreview = null;
        wtfStatus = importStatusLine = importDetail = planFileStatus = importStatus = string.Empty;
        importFailed = planFileFailed = importStatusIsError = wtfError = false;
        replaceSharedPlan = false;
    }

    private void StartUnifiedImport()
    {
        if (ImportBusy || Plugin.Encounter.InCombat) return;
        switch (importSource.Kind)
        {
            case ImportKind.WtfDig:
                StartWtfGuide(importSource.Value);
                break;
            case ImportKind.RaidPlan:
                planLink = importSource.Value;
                ImportFromLink();
                break;
            case ImportKind.FfLogs:
                reportInput = importSource.Value;
                if (HasCredentials()) LoadFights();
                else showCredentials = true;
                break;
            case ImportKind.ShareCode:
                importBuffer = importSource.Value;
                DoImport(replaceSharedPlan);
                break;
        }
    }

    private void DrawFfLogsImport(PlanDocument plan)
    {
        ImGui.BeginDisabled(importBusy);
        if (!HasCredentials())
        {
            FfLogsCredentialsPanel.Draw(showInstructions: true);
            ImGui.EndDisabled();
            return;
        }
        FfLogsCredentialsPanel.DrawSummary();
        ImGui.SameLine();
        if (ImGui.SmallButton(showCredentials ? "Hide" : "Change")) showCredentials = !showCredentials;
        if (showCredentials)
        {
            ImGui.Spacing();
            FfLogsCredentialsPanel.Draw(showInstructions: false);
        }
        ImGui.EndDisabled();
        if (importBusy) ImGui.TextDisabled("Talking to FF Logs…");
        ImGui.BeginDisabled(importBusy || Plugin.Encounter.InCombat);

        if (fights is { Count: > 0 })
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Fight");
            ImGui.Separator();

            ImGui.SetNextItemWidth(-1);
            var preview = selectedFight >= 0 && selectedFight < fights.Count
                ? fights[selectedFight].Describe()
                : "Pick a pull";

            if (ImGui.BeginCombo("##fight", preview, ImGuiComboFlags.HeightLarge))
            {
                for (var i = 0; i < fights.Count; i++)
                {
                    if (ImGui.Selectable(fights[i].Describe() + "##f" + i, i == selectedFight, ImGuiSelectableFlags.None, Vector2.Zero))
                    {
                        selectedFight = i;
                        loadedFight = null;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.BeginDisabled(importBusy || selectedFight < 0);
            if (ImGui.Button("Fetch this fight", Vector2.Zero))
                LoadFightData();
            ImGui.EndDisabled();
        }

        if (loadedFight != null)
            DrawPreviewAndApply(plan, loadedFight);
        ImGui.EndDisabled();
    }

    /// <summary>
    /// Rebuilds a plan from raidplan.io. A link fetches the plan's own data file — the same one
    /// the site's page loads — and a saved file is read straight off disk.
    /// </summary>
    private void DrawPlanFileImport()
    {
        ImGui.Spacing();
        ImGui.BeginDisabled(ImportBusy || Plugin.Encounter.InCombat);
        if (ImGui.TreeNode("Import a saved file###plan-file-node"))
        {
            ImGui.SetNextItemWidth(-1);
            UiHelpers.InputTextHint("##plan-file", "Path to a raidplan .json or Shikari .txt file", ref planFilePath, 512);

            if (ImGui.Button("Import that file", Vector2.Zero))
                ImportPlanFile();

            ImGui.SameLine();
            UiHelpers.HelpMarker(
                "Choose a saved raidplan.io JSON file or a Shikari share-code text file. Files are imported as new plans.");

            ImGui.TreePop();
        }

        ImGui.EndDisabled();
        if (planFileStatus.Length == 0)
            return;

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Vec(planFileFailed ? Palette.Danger : Palette.Good));
        ImGui.TextWrapped(planFileStatus);
        ImGui.PopStyleColor();
    }

    private void ImportFromLink()
    {
        var parsed = PlanUrlParser.Parse(planLink);
        if (!parsed.IsValid)
        {
            planFileFailed = true;
            planFileStatus = "No plan code in that link.";
            return;
        }

        planFileStatus = string.Empty;
        Run(async cancel =>
        {
            var json = await Plugin.PlanFetcher.GetAsync(parsed.Code, cancel).ConfigureAwait(false);
            return () => Adopt(json, parsed.Code);
        });
    }

    private void ImportPlanFile()
    {
        var path = planFilePath.Trim().Trim('"');

        if (path.Length == 0)
        {
            planFileFailed = true;
            planFileStatus = "Give the path to the saved plan file first.";
            return;
        }

        if (!System.IO.File.Exists(path))
        {
            planFileFailed = true;
            planFileStatus = "Nothing at that path.";
            return;
        }

        try
        {
            var file = new System.IO.FileInfo(path);
            if (file.Length > 16 * 1024 * 1024) throw new System.IO.InvalidDataException("The file exceeds the 16 MB import limit.");
            var content = System.IO.File.ReadAllText(path);
            var source = ImportSource.Parse(content);
            if (source.Kind == ImportKind.ShareCode)
            {
                importBuffer = source.Value;
                DoImport(replaceExisting: false);
                planFileStatus = importStatus;
                planFileFailed = importStatusIsError;
            }
            else Adopt(content, System.IO.Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            planFileFailed = true;
            planFileStatus = "Could not read that file: " + ex.Message;
        }
    }

    /// <summary>Turns fetched or loaded plan data into a plan of our own.</summary>
    private void Adopt(string json, string name)
    {
        if (!RaidPlanIoImporter.TryImport(json, out var imported, out var report, out var error))
        {
            planFileFailed = true;
            planFileStatus = error;
            return;
        }

        imported!.Name = name;
        if (!Plugin.Plans.SaveActive())
        {
            planFileFailed = true;
            planFileStatus = Plugin.Plans.LastSaveError ?? "Save the current plan before importing another.";
            return;
        }
        Plugin.Plans.Import(imported, replaceExisting: false);

        slideIndex = 0;
        canvas.Select(null);
        MarkDirty();

        planFileFailed = false;
        planLink = string.Empty;
        planFilePath = string.Empty;
        planFileStatus = report.Summary() +
                         (report.SeatsBound > 0 ? $" {report.SeatsBound} seat(s) matched." : string.Empty);

        foreach (var note in report.Notes.Take(2))
            planFileStatus += "\n" + note;
    }

    /// <summary>
    /// Credentials good enough to try an import with. Unchecked counts: someone who set them up
    /// before this check existed should not be stopped from importing.
    /// </summary>
    private static bool HasCredentials() => Plugin.FfLogsAuth.Usable;

    private void DrawImportStatus()
    {
        if (string.IsNullOrEmpty(importStatusLine))
            return;

        ImGui.Spacing();
        ImGui.TextColored(
            UiHelpers.Pack(importFailed
                ? new Vector4(1f, 0.45f, 0.4f, 1f)
                : new Vector4(0.5f, 0.9f, 0.5f, 1f)),
            importStatusLine);

        if (string.IsNullOrEmpty(importDetail))
            return;

        if (!ImGui.TreeNode("Import details###import-detail"))
            return;

        ImGui.TextWrapped(importDetail.Length > 2000 ? importDetail[..2000] + "…" : importDetail);
        if (ImGui.SmallButton("Copy"))
            ImGui.SetClipboardText(importDetail);
        ImGui.TreePop();
    }

    private void DrawPreviewAndApply(PlanDocument plan, LogFightData data)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("What's in this pull");
        ImGui.Separator();

        if (ImGui.Button("Review this pull with this plan")) LoadLogReview(plan, data);
        ImGui.TextDisabled("Compare recorded statuses and movement with this strategy in Review.");
        ImGui.Spacing();
        var players = data.Actors.Where(a => a.IsPlayer).ToList();
        ImGui.TextUnformatted(
            $"{data.EnemyCasts.Count} boss casts, {data.PlayerCasts.Count} player casts, {players.Count} players.");

        var seats = BuildSeatJobs(plan);
        var matches = LogImporter.MatchSeats(seats, data.Actors);

        ImGui.Spacing();
        if (matches.Count == 0)
        {
            ImGui.TextColored(
                UiHelpers.Pack(new Vector4(0.9f, 0.85f, 0.45f, 1f)),
                "No seats matched. Set each seat's job on the Roster tab and the log's players will line up.");
        }
        else
        {
            ImGui.TextDisabled($"{matches.Count} seat(s) matched by job:");
            foreach (var match in matches)
            {
                var seat = plan.Roster[match.SeatIndex];
                ImGui.TextUnformatted($"    {seat.DisplayName}  ←  {match.PlayerName} ({match.JobName})");
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Bring in");
        ImGui.Separator();

        var timeline = importOptions.ImportTimeline;
        if (ImGui.Checkbox("Timeline steps", ref timeline))
            importOptions.ImportTimeline = timeline;

        ImGui.SameLine();
        var assignments = importOptions.ImportAssignments;
        if (ImGui.Checkbox("Cooldown assignments", ref assignments))
            importOptions.ImportAssignments = assignments;

        ImGui.SameLine();
        var slides = importOptions.CreateSlides;
        if (ImGui.Checkbox("A slide per step", ref slides))
            importOptions.CreateSlides = slides;

        var onlyBar = importOptions.OnlyCastsWithBar;
        if (ImGui.Checkbox("Only casts with a bar", ref onlyBar))
            importOptions.OnlyCastsWithBar = onlyBar;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drops instants, which are mostly auto-attacks and filler.");

        ImGui.SameLine();
        var window = importOptions.WindowBefore;
        ImGui.SetNextItemWidth(180 * UiHelpers.Scale);
        if (ImGui.SliderFloat("Look back from a cast", ref window, 5f, 40f, "%.0f s", ImGuiSliderFlags.None))
            importOptions.WindowBefore = window;

        if (ImGui.IsItemHovered())
        {
            UiHelpers.Tooltip(
                "A cooldown pressed this long before a boss cast is taken as being for it. " +
                "Longer catches pre-planned mitigation; shorter avoids grabbing the wrong mechanic.");
        }

        var preview = LogImporter.BuildTimeline(data, importOptions);
        ImGui.Spacing();
        ImGui.TextDisabled($"That gives {preview.Count} step(s).");

        ImGui.Spacing();
        if (ImGui.Button("Import into this plan", Vector2.Zero))
        {
            var result = LogImporter.Apply(
                plan, data, importOptions, seats,
                id => Plugin.Actions.Get(id)?.IsCooldown ?? false);

            MarkDirty();
            importFailed = false;
            importDetail = string.Empty;
            importStatusLine = result.Summary();

            if (result.CooldownsUnattributed > 0)
                importStatusLine += $" {result.CooldownsUnattributed} cooldown(s) didn't line up with a step.";
            if (result.Unmatched.Count > 0)
                importStatusLine += " Unmatched: " + string.Join(", ", result.Unmatched.Take(4)) + ".";
        }

        ImGui.SameLine();
        UiHelpers.HelpMarker(
            "Steps already on your timeline are left alone, so importing a second pull only fills " +
            "in what's missing. Assignments are added, never removed.");
    }

    private static List<SeatJob> BuildSeatJobs(PlanDocument plan)
    {
        var seats = new List<SeatJob>();
        for (var i = 0; i < plan.Roster.Count; i++)
        {
            var job = Plugin.Actions.Job(plan.Roster[i].JobId);
            if (job != null)
                seats.Add(new SeatJob(i, job.Name, job.Abbreviation));
        }

        return seats;
    }

    private void LoadFights()
    {
        var parsed = ReportUrl.Parse(reportInput);
        if (!parsed.IsValid)
        {
            Fail("No report code in that link.");
            return;
        }

        Plugin.Config.LastReportUrl = reportInput.Trim();
        Plugin.SaveConfig();

        Run(async cancel =>
        {
            var list = await Plugin.FfLogs.GetFightsAsync(
                Plugin.Config.FfLogsClientId, Plugin.Config.FfLogsClientSecret, parsed.Code, cancel);

            return () =>
            {
                fights = list;
                loadedFight = null;
                selectedFight = parsed.FightId switch
                {
                    ReportUrl.LastFight => list.Count - 1,
                    { } id => list.FindIndex(f => f.Id == id),
                    _ => -1,
                };

                if (selectedFight < 0 && list.Count > 0)
                    selectedFight = list.FindIndex(f => f.Kill) is var kill && kill >= 0 ? kill : list.Count - 1;

                importStatusLine = $"Found {list.Count} fight(s).";
                importFailed = false;
            };
        });
    }

    private void LoadFightData()
    {
        var parsed = ReportUrl.Parse(reportInput);
        if (!parsed.IsValid || fights == null || selectedFight < 0 || selectedFight >= fights.Count)
            return;

        var fight = fights[selectedFight];

        Run(async cancel =>
        {
            var data = await Plugin.FfLogs.GetFightDataAsync(
                Plugin.Config.FfLogsClientId, Plugin.Config.FfLogsClientSecret, parsed.Code, fight, cancel);

            return () =>
            {
                loadedFight = data;
                importStatusLine = $"Loaded {fight.Name}.";
                importFailed = false;
            };
        });
    }

    // Workers fetch data only. Results enter the UI and plan store together on the draw thread.
    private void Run(Func<CancellationToken, Task<Action>> work)
    {
        if (ImportBusy) return;
        importStatusLine = importDetail = string.Empty;
        var cancel = Plugin.Shutdown;
        importLoad = Task.Run(() => work(cancel), cancel);
    }

    private void PollImport()
    {
        if (importLoad?.IsCompleted != true) return;
        try
        {
            var apply = importLoad.GetAwaiter().GetResult();
            Plugin.Shutdown.ThrowIfCancellationRequested();
            if (Plugin.Encounter.InCombat)
                throw new InvalidOperationException("Import finished during combat. Retry after the pull.");
            apply();
        }
        catch (OperationCanceledException) { }
        catch (FfLogsException ex) { Fail(ex.Message, ex.Detail); }
        catch (PlanFetchException ex) { Fail(ex.Message); }
        catch (Exception ex)
        {
            Fail("Import failed: " + ex.Message);
            Plugin.Log.Error(ex, "Import failed.");
        }
        finally { importLoad = null; }
    }

    private void DisposeImport()
    {
        if (importLoad != null)
            _ = importLoad.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
    }

    private void Fail(string message, string? detail = null)
    {
        importStatusLine = message;
        importFailed = true;
        importDetail = detail ?? string.Empty;
    }
}
