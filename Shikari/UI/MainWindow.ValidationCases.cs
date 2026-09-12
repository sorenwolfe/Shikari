using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Shikari.Services.Replay;

namespace Shikari.UI;

public sealed partial class MainWindow
{
    private PullValidationCaseStore? validationCaseStore;
    private string validationCaseName = "";
    private string validationCaseNote = "";
    private string validationCaseSelection = "";
    private string validationCasePending = "";
    private string validationCaseMessage = "";

    private void PollValidationCases()
    {
        if (validationCaseStore == null) return;
        validationCaseStore.Poll();
        if (validationCasePending.Length == 0 || validationCaseStore.Busy) return;
        validationCaseMessage = validationCaseStore.Items.Any(c => c.Id == validationCasePending)
            ? "Reviewed case saved locally." : "The reviewed case could not be saved. See the storage error below.";
        validationCasePending = "";
    }

    private void DrawSavedValidationCases(PullValidationResult? result)
    {
        if (!ImGui.TreeNode("Saved reviewed cases")) return;
        try
        {
            validationCaseStore ??= new PullValidationCaseStore(Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(), "validation-cases"));
            PollValidationCases();
            var store = validationCaseStore;
            ImGui.TextWrapped("Save assignments you checked against the strategy and pull. Saved cases require the original recording; keep a copy before replay retention removes it.");
            var canUseResult = result != null && pullValidation.Snapshot != null &&
                pullValidation.CaseFingerprint.Length > 0 && !pullValidation.Running && !Plugin.Encounter.InCombat;
            ImGui.InputTextWithHint("Case name##validation", "e.g. Act 2 assignment check", ref validationCaseName, PullValidationCases.MaxName);
            ImGui.InputTextMultiline("Review note##validation", ref validationCaseNote, PullValidationCases.MaxReviewNote,
                new Vector2(-1, 60 * UiHelpers.Scale));
            ImGui.TextDisabled("Describe what you checked and the source of the expected assignments.");
            ImGui.BeginDisabled(!canUseResult || !store.WritesEnabled || store.Busy ||
                validationExpected.Count == 0 || string.IsNullOrWhiteSpace(validationCaseName) ||
                string.IsNullOrWhiteSpace(validationCaseNote) || store.Items.Count >= PullValidationCases.MaxCases);
            var saveClicked = ImGui.SmallButton("Save reviewed case");
            ImGui.EndDisabled();
            if (saveClicked)
            {
                var reviewed = PullValidationCases.Create(validationCaseName, validationCaseNote, result!.Plan,
                    pullValidation.Snapshot!, result.ActorId, result.TerritoryId, pullValidation.CaseFingerprint, validationExpected);
                if (store.Save(reviewed))
                {
                    validationCasePending = reviewed.Id; validationCaseSelection = reviewed.Id;
                    validationCaseMessage = "Saving reviewed case…";
                }
            }
            if (!canUseResult) ImGui.TextWrapped("Validate the original pull and player to save or load its expected assignments.");
            if (store.Items.Count >= PullValidationCases.MaxCases) ImGui.TextWrapped("The library has 64 cases. Remove an older case before saving another.");
            if (store.Busy) ImGui.TextDisabled("Reading or saving reviewed cases…");
            if (validationCaseMessage.Length > 0) ImGui.TextWrapped(validationCaseMessage);
            if (store.Error != null) ImGui.TextWrapped(store.Error);
            if (store.Items.Count == 0) return;

            var selected = store.Items.FirstOrDefault(c => c.Id == validationCaseSelection);
            ImGui.SetNextItemWidth(280 * UiHelpers.Scale);
            if (ImGui.BeginCombo("Reviewed case", selected?.Name ?? "Choose saved case"))
            {
                foreach (var candidate in store.Items.OrderByDescending(c => c.UpdatedUtc))
                    if (ImGui.Selectable(candidate.Name + "##case-" + candidate.Id, candidate == selected))
                    { validationCaseSelection = candidate.Id; selected = candidate; }
                ImGui.EndCombo();
            }
            if (selected == null) return;
            ImGui.TextWrapped(selected.ReviewNote);
            ImGui.TextDisabled($"{selected.Expected.Count} reviewed assignments · saved {selected.UpdatedUtc.ToLocalTime():g}");
            var compatible = canUseResult && PullValidationCases.IsCompatible(selected, pullValidation.CaseFingerprint);
            if (canUseResult && !compatible)
                ImGui.TextWrapped("This case belongs to different inputs. Select its original pull, player and tested plan; changed evidence or boards need a new review.");
            ImGui.BeginDisabled(!compatible || store.Busy);
            var loadClicked = ImGui.SmallButton("Load reviewed expectations");
            ImGui.EndDisabled();
            if (loadClicked)
            {
                var copy = PullValidationCases.Copy(selected);
                validationExpected.Clear(); validationExpected.AddRange(copy.Expected);
                validationExpectedRows = PullValidationComparison.CompareExpected(result!, validationExpected);
                validationCaseMessage = "Reviewed expectations loaded. Compare the results above.";
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(!store.WritesEnabled || store.Busy || Plugin.Encounter.InCombat);
            if (ImGui.SmallButton("Remove saved case") && store.Delete(selected.Id))
            { validationCaseSelection = ""; validationCaseMessage = "Removing saved case…"; }
            ImGui.EndDisabled();
        }
        catch (Exception ex) { ImGui.TextWrapped("Could not use reviewed cases: " + ex.Message); }
        finally { ImGui.TreePop(); }
    }
}
