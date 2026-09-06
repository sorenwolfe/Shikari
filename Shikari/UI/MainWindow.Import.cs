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
    private string reportInput = string.Empty;
    private string importStatusLine = string.Empty;
    private bool importFailed;
    private string importDetail = string.Empty;

    private List<LogFight>? fights;
    private int selectedFight = -1;
    private LogFightData? loadedFight;
    private bool importBusy;

    private readonly ImportOptions importOptions = new();
    private bool showCredentials;

    private string planLink = string.Empty;
    private bool planBusy;
    private string planFilePath = string.Empty;
    private string planFileStatus = string.Empty;
    private bool planFileFailed;

    private void DrawImportTab(PlanDocument plan)
    {
        DrawWtfDigImport();
        DrawPlanFileImport();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("From FF Logs");
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Paste an FF Logs link and pull the fight's timeline straight in, along with the " +
            "cooldowns each player pressed. Seats are matched to log players by job.");

        ImGui.Spacing();

        if (!HasCredentials())
        {
            ImGui.Separator();
            FfLogsCredentialsPanel.Draw(showInstructions: true);
            return;
        }

        // Set up and working: one line, with a way back to the boxes.
        FfLogsCredentialsPanel.DrawSummary();
        ImGui.SameLine();
        if (ImGui.SmallButton(showCredentials ? "Hide" : "Change"))
            showCredentials = !showCredentials;

        if (showCredentials)
        {
            ImGui.Spacing();
            FfLogsCredentialsPanel.Draw(showInstructions: false);
            ImGui.Separator();
        }

        ImGui.Spacing();

        if (string.IsNullOrEmpty(reportInput) && !string.IsNullOrEmpty(Plugin.Config.LastReportUrl))
            reportInput = Plugin.Config.LastReportUrl;

        ImGui.SetNextItemWidth(-120 * UiHelpers.Scale);
        UiHelpers.InputTextHint("##report", "https://www.fflogs.com/reports/…", ref reportInput, 256);

        ImGui.SameLine();
        ImGui.BeginDisabled(importBusy);
        if (ImGui.Button("Load fights", new Vector2(-1, 0)))
            LoadFights();
        ImGui.EndDisabled();

        var parsed = ReportUrl.Parse(reportInput);
        if (!parsed.IsValid && reportInput.Length > 0)
            ImGui.TextDisabled("No report code in that. A code is 16 letters and digits.");

        if (importBusy)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Talking to FF Logs…");
        }

        DrawImportStatus();

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
    }

    /// <summary>
    /// Rebuilds a plan from raidplan.io. A link fetches the plan's own data file — the same one
    /// the site's page loads — and a saved file is read straight off disk.
    /// </summary>
    private void DrawPlanFileImport()
    {
        ImGui.TextDisabled("From raidplan.io");
        ImGui.Spacing();

        ImGui.TextWrapped("Paste a link to a plan and it comes across as a plan of its own.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-120 * UiHelpers.Scale);
        UiHelpers.InputTextHint("##plan-link", "https://shikari.io/plan/…", ref planLink, 512);

        ImGui.SameLine();
        ImGui.BeginDisabled(planBusy);
        if (ImGui.Button("Import", new Vector2(-1, 0)))
            ImportFromLink();
        ImGui.EndDisabled();

        var parsedLink = PlanUrlParser.Parse(planLink);
        if (!parsedLink.IsValid && planLink.Trim().Length > 0)
            ImGui.TextDisabled("No plan code in that. A link looks like raidplan.io/plan/<code>.");

        if (planBusy)
            ImGui.TextDisabled("Fetching…");

        if (ImGui.TreeNode("Or import a file you saved###plan-file-node"))
        {
            ImGui.SetNextItemWidth(-1);
            UiHelpers.InputTextHint("##plan-file", "Path to a saved .json file", ref planFilePath, 512);

            if (ImGui.Button("Import that file", Vector2.Zero))
                ImportPlanFile();

            ImGui.SameLine();
            UiHelpers.HelpMarker(
                "For a plan a link cannot reach. Open it in a browser, press F12, and on the " +
                "Network tab copy the response of the .json request from userdata.raidplan.io.");

            ImGui.TreePop();
        }

        if (planFileStatus.Length == 0)
            return;

        ImGui.Spacing();
        ImGui.TextColored(
            UiHelpers.Pack(planFileFailed ? Palette.Vec(Palette.Danger) : Palette.Vec(Palette.Good)),
            planFileStatus);
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

        planBusy = true;
        planFileStatus = string.Empty;

        var cancel = Plugin.Shutdown;

        Task.Run(async () =>
        {
            try
            {
                var json = await Plugin.PlanFetcher.GetAsync(parsed.Code, cancel).ConfigureAwait(false);
                Adopt(json, parsed.Code);
            }
            catch (OperationCanceledException)
            {
                // Unloading.
            }
            catch (PlanFetchException ex)
            {
                Fail(ex.Message);
            }
            catch (Exception ex)
            {
                Fail("Could not reach raidplan.io: " + ex.Message);
                Plugin.Log.Error(ex, "Fetching a raidplan.io plan failed.");
            }
            finally
            {
                planBusy = false;
            }
        }, cancel);

        void Fail(string message)
        {
            planFileFailed = true;
            planFileStatus = message;
        }
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
            Adopt(System.IO.File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path));
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

        if (!ImGui.TreeNode("What FF Logs sent back###import-detail"))
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
        });
    }

    private void LoadFightData()
    {
        var parsed = ReportUrl.Parse(reportInput);
        if (!parsed.IsValid || fights == null || selectedFight < 0)
            return;

        var fight = fights[selectedFight];

        Run(async cancel =>
        {
            loadedFight = await Plugin.FfLogs.GetFightDataAsync(
                Plugin.Config.FfLogsClientId, Plugin.Config.FfLogsClientSecret, parsed.Code, fight, cancel);

            importStatusLine = $"Loaded {fight.Name}.";
            importFailed = false;
        });
    }

    private void Run(Func<CancellationToken, Task> work)
    {
        importBusy = true;
        importStatusLine = string.Empty;
        importDetail = string.Empty;

        var cancel = Plugin.Shutdown;

        Task.Run(async () =>
        {
            try
            {
                await work(cancel);
            }
            catch (OperationCanceledException)
            {
                // Plugin is unloading mid-request. Nothing left to report to.
                return;
            }
            catch (FfLogsException ex)
            {
                if (cancel.IsCancellationRequested)
                    return;

                Fail(ex.Message, ex.Detail);
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested)
                    return;

                Fail("Import failed: " + ex.Message);
                Plugin.Log.Error(ex, "FF Logs import failed.");
            }
            finally
            {
                importBusy = false;
            }
        }, cancel);
    }

    private void Fail(string message, string? detail = null)
    {
        importStatusLine = message;
        importFailed = true;
        importDetail = detail ?? string.Empty;
    }
}
