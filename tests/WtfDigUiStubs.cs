using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.WtfDig;
namespace Shikari.Services { public static class JobRoles { public static RaidRole RoleFor(string text) => RaidRole.Unknown; } }
namespace Dalamud.Bindings.ImGui
{
    public enum ImGuiCol { Text }
    public enum ImGuiWindowFlags { None }
    public static class ImGui
    {
        public static void TextUnformatted(string s) { }
        public static void TextWrapped(string s) { }
        public static void TextDisabled(string s) { }
        public static void TextColored(Vector4 c, string s) { }
        public static void PushStyleColor(ImGuiCol c, Vector4 v) { }
        public static void PopStyleColor() { }
        public static void BeginDisabled(bool b) { }
        public static void EndDisabled() { }
        public static void SetNextItemWidth(float f) { }
        public static void SameLine() { }
        public static bool Button(string s, Vector2 v = default) => false;
        public static bool SmallButton(string s) => false;
        public static bool BeginCombo(string s, string p) => false;
        public static void EndCombo() { }
        public static bool Selectable(string s, bool selected) => false;
        public static bool SliderInt(string s, ref int v, int min, int max) => false;
        public static bool TreeNode(string s) => false;
        public static void TreePop() { }
        public static bool BeginChild(string id, Vector2 size, bool border, ImGuiWindowFlags flags) => true;
        public static void EndChild() { }
        public static bool Checkbox(string s, ref bool b) => false;
        public static void PushID(string s) { }
        public static void PopID() { }
        public static void SetClipboardText(string s) { }
        public static void Spacing() { }
        public static void Separator() { }
    }
}
namespace Shikari.UI.Theme
{
    public static class Palette { public const uint Attention = 1, Text = 2; public static Vector4 Vec(uint c) => Vector4.One; }
}
namespace Shikari
{
    public static class Plugin
    {
        public static CancellationToken Shutdown => CancellationToken.None;
        public static FakeFonts Fonts = new();
        public static FakeInterface PluginInterface = new();
        public static FakeFetcher PlanFetcher = new();
        public static FakeEncounter Encounter = new();
        public static FakePlans Plans = new();
        public static FakeActions Actions = new();
        public static FakeObjects ObjectTable = new();
    }
    public sealed class FakeActions { public FakeJob Job(uint id) => new(); }
    public sealed class FakeJob { public RaidRole Role => RaidRole.PhysicalRanged; public uint RowId => 0; }
    public sealed class FakeObjects { public FakePlayer? LocalPlayer => new(); }
    public sealed class FakePlayer { public FakeJob ClassJob => new(); }
    public sealed class FakeFonts : IDisposable { public IDisposable PushHeading() => this; public void Dispose() { } }
    public sealed class FakeInterface { public string GetPluginConfigDirectory() => System.IO.Path.GetTempPath(); }
    public sealed class FakeFetcher
    {
        public TaskCompletionSource<string> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public int Requests;
        public Task<string> GetAsync(string code, CancellationToken token) { Token = token; Interlocked.Increment(ref Requests); return Response.Task; }
    }
    public sealed class FakeEncounter { public bool InCombat; }
    public sealed class FakePlans
    {
        public bool SaveSucceeds = true;
        public string? LastSaveError => SaveSucceeds ? null : "Disk unavailable";
        public int Imports;
        public int ImportThread;
        public bool RejectImport;
        public System.Collections.Generic.List<PlanDocument> Ordered = new();
        public bool SaveActive() => SaveSucceeds;
        public PlanDocument Import(PlanDocument plan, bool replaceExisting) { if (RejectImport) throw new InvalidOperationException("Rejected before acceptance"); Ordered.Add(plan); Imports++; ImportThread = Environment.CurrentManagedThreadId; return plan; }
    }
}
namespace Shikari.UI
{
    public static class UiHelpers { public static float Scale => 1; public static bool InputTextHint(string id, string hint, ref string text, int max) => false; }
    public sealed class FakeCanvas { public void Select(object? o) { } }
    public sealed partial class MainWindow
    {
        private int slideIndex;
        private int planTool;
        private string? selectedEntryId;
        private FakeCanvas canvas = new();
        private void MarkDirty() { }
        private static void Check(bool b, string message) { if (!b) throw new Exception(message); }
        public static void RunImportChecks()
        {
            var window = new MainWindow();
            window.wtfImages = false;
            var guide = WtfDigGuide.Read(WtfDigLink.Parse("https://wtfdig.info/74/m9s"),
                "const config={fightKey:'m9s',title:'Example',defaultStratName:'example',strats:{example:{defaults:{direction:'south'}}},toggles:[{key:'direction',defaultValue:'north'}]}; const example={stratName:'example',stratUrl:'https://raidplan.io/plan/abcdefghijklmnop',strats:[{phaseName:'Phase',description:'Guide notes',strats:[{role:'Tank',party:1,toggleKey:'direction',toggleValue:'south',description:'South'},{role:'Tank',party:1,toggleKey:'direction',toggleValue:'north',description:'North'}]}]};");
            window.wtfLoad = Task.FromResult(guide); window.wtfCancel = new();
            window.PollWtfDig();
            Check(window.wtfPreview != null && window.wtfSelection.Strategy == "example", "Guide task consumed on UI thread");
            Check(window.wtfPrepared != null && window.wtfCancel != null && window.wtfCancel.Token.CanBeCanceled, "Resolved guide automatically prepares boards with a live replacement cancellation source");
            Check(window.wtfSelection.Variants["toggle/direction"] == "south", "Strategy defaults override general toggle defaults");
            Check(SpinWait.SpinUntil(() => Plugin.PlanFetcher.Requests == 1, 3000), "Automatic preparation fetches linked board");
            Plugin.PlanFetcher.Response.SetResult("{\"nodes\":[{\"type\":\"circle\",\"meta\":{\"pos\":{\"x\":500,\"y\":400},\"size\":{\"w\":100,\"h\":100}}}]}");
            window.wtfPrepared!.GetAwaiter().GetResult(); window.PollWtfDig();
            Check(Plugin.Plans.Imports == 1 && window.wtfCancel == null, "Automatic result imported once and cancellation source released");
            window.PollWtfDig(); Check(Plugin.Plans.Imports == 1, "Polling cannot repeat completed automatic import");
            Plugin.Plans.Imports = 0;
            var prepared = new PreparedWtfDig { Plan = window.wtfPreview!.Plan };
            var staged = System.IO.Path.GetTempFileName(); prepared.CreatedFiles.Add(staged);
            window.wtfPrepared = Task.FromResult(prepared); window.wtfCancel = new(); window.wtfCancel.Cancel();
            window.PollWtfDig();
            Check(Plugin.Plans.Imports == 0 && !prepared.Retained, "Cancelled completed result is not committed");
            Check(!System.IO.File.Exists(staged), "Cancelled completed preparation releases staged files");
            window.wtfPrepared = Task.FromResult(prepared); window.wtfCancel = new(); Plugin.Encounter.InCombat = true;
            window.PollWtfDig();
            Check(Plugin.Plans.Imports == 0, "Combat prevents plan replacement");
            Plugin.Encounter.InCombat = false; Plugin.Plans.SaveSucceeds = false;
            window.wtfPrepared = Task.FromResult(prepared); window.wtfCancel = new(); window.PollWtfDig();
            Check(Plugin.Plans.Imports == 0, "Failed active-plan save prevents replacement");
            Plugin.Plans.SaveSucceeds = true;
            staged = System.IO.Path.GetTempFileName(); prepared.CreatedFiles.Add(staged);
            Plugin.Plans.RejectImport = true;
            window.wtfPrepared = Task.FromResult(prepared); window.wtfCancel = new(); window.PollWtfDig();
            Check(!prepared.Retained && !System.IO.File.Exists(staged), "Store rejection cleans up staged assets");
            Plugin.Plans.RejectImport = false;
            window.wtfPrepared = Task.FromResult(prepared); window.wtfCancel = new(); window.PollWtfDig();
            Check(Plugin.Plans.Imports == 1 && Plugin.Plans.ImportThread == Environment.CurrentManagedThreadId && prepared.Retained, "Completed result committed and assets retained on consumer thread");
            var unresolved = WtfDigGuide.Read(WtfDigLink.Parse("https://wtfdig.info/74/m9s"),
                "const config={fightKey:'m9s',title:'Variants'}; const only={stratName:'only',strats:[{phaseName:'Phase',description:{north:'North',south:'South'}}]};");
            window.wtfLoad = Task.FromResult(unresolved); window.wtfCancel = new(); window.PollWtfDig();
            Check(window.wtfSelection.Strategy == "only" && window.wtfPrepared == null && window.wtfAutoImport && window.wtfPreview!.MissingVariants.Count == 1, "Single strategy selects automatically but unresolved variant prevents preparation");
            window.wtfSelection.Variants[window.wtfPreview!.MissingVariants[0].Key] = "south";
            window.RebuildWtfPreview(); window.PollWtfDig();
            window.wtfPrepared?.GetAwaiter().GetResult(); window.PollWtfDig();
            Check(Plugin.Plans.Imports == 2 && !window.wtfAutoImport, "Choosing final variant automatically completes import without an extra import click");
            window.DisposeWtfDig();
            Console.WriteLine("PASS: real WTFDIG UI partial compiled with API stubs; load, cancel, combat/save guards and consumer-thread commit");
        }
    }
}
