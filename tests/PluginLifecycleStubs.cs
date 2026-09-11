using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shikari.Tests
{
    public static class Probe
    {
        public static string? FailAt;
        public static Exception StartupFailure = new InvalidOperationException("startup fault");
        public static bool ThrowDuringCleanup;
        public static readonly List<Resource> Resources = new();
        public static readonly List<string> Saves = new();
        public static readonly List<string> PlanEvents = new();
        public static bool ThrowOnPlanRequest;
        public static CancellationToken? BuildToken;
        public static TaskCompletionSource? BuildCompletion;
        public static void Reset()
        {
            FailAt = null; ThrowDuringCleanup = false; Resources.Clear(); Saves.Clear();
            PlanEvents.Clear(); ThrowOnPlanRequest = false;
            BuildToken = null; BuildCompletion = null;
            StartupFailure = new InvalidOperationException("startup fault");
        }
        public static void Hit(string point) { if (point == FailAt) throw StartupFailure; }
        public static void Cleanup() { if (ThrowDuringCleanup) throw new IOException("cleanup fault"); }
        public static void Save(string name) { Saves.Add(name); Cleanup(); }
    }
    public class Resource : IDisposable
    {
        public string Name => GetType().Name;
        public int DisposeCount;
        public Resource() { Probe.Hit("construct." + Name); Probe.Resources.Add(this); }
        public virtual void Dispose() { DisposeCount++; Probe.Cleanup(); }
    }
    public sealed class Hook
    {
        private readonly string name;
        private readonly List<Delegate> handlers = new();
        public int Count => handlers.Count;
        public Hook(string name) => this.name = name;
        public void Add(Delegate value) { Probe.Hit(name + ".add"); handlers.Add(value); }
        public void Remove(Delegate value) { handlers.Remove(value); Probe.Cleanup(); }
    }
    public sealed class FakeUiBuilder
    {
        private readonly Hook draw = new("ui.draw"), config = new("ui.config"), main = new("ui.main");
        public int Count => draw.Count + config.Count + main.Count;
        public event Action Draw { add => draw.Add(value); remove => draw.Remove(value); }
        public event Action OpenConfigUi { add => config.Add(value); remove => config.Remove(value); }
        public event Action OpenMainUi { add => main.Add(value); remove => main.Remove(value); }
    }
    public sealed class FakeHost : Dalamud.Plugin.IDalamudPluginInterface,
        Dalamud.Plugin.Services.ICommandManager, Dalamud.Plugin.Services.IDutyState,
        Dalamud.Plugin.Services.IChatGui, Dalamud.Plugin.Services.IPluginLog
    {
        public FakeUiBuilder UiBuilder { get; } = new();
        public FileInfo ConfigFile => new("unused.json");
        public Dictionary<string, Dalamud.Game.Command.CommandInfo> Commands { get; } = new();
        private readonly Hook start = new("duty.start"), complete = new("duty.complete");
        public int DutyHookCount => start.Count + complete.Count;
        public event Action<Dalamud.Game.DutyState.IDutyStateEventArgs> DutyStarted { add => start.Add(value); remove => start.Remove(value); }
        public event Action<Dalamud.Game.DutyState.IDutyStateEventArgs> DutyCompleted { add => complete.Add(value); remove => complete.Remove(value); }
        public string GetPluginConfigDirectory() => "unused";
        public object GetPluginConfig() { Probe.Hit("config.load"); return new Configuration(); }
        public void SavePluginConfig(object value) => Probe.Save("config");
        public bool AddHandler(string name, Dalamud.Game.Command.CommandInfo info)
        { Probe.Hit("command.add." + name); return Commands.TryAdd(name, info); }
        public bool RemoveHandler(string name) { var result = Commands.Remove(name); Probe.Cleanup(); return result; }
        public void Print(string text, string name, object? unused) { }
        public void PrintError(string text, string name, object? unused) { }
        public void Information(string message, params object[] args) { if (message == "Shikari loaded.") Probe.Hit("log.loaded"); }
        public void Error(Exception ex, string message, params object[] args) => Probe.Cleanup();
        public void Warning(string message, params object[] args) { }
    }
}
namespace Dalamud.IoC { public sealed class PluginServiceAttribute : Attribute { } }
namespace Dalamud.Game.DutyState { public interface IDutyStateEventArgs { } }
namespace Dalamud.Game.Command
{
    public sealed class CommandInfo
    {
        public CommandInfo(Action<string, string> handler) { }
        public string HelpMessage { get; set; } = "";
    }
}
namespace Dalamud.Plugin
{
    public interface IDalamudPlugin : IDisposable { }
    public interface IDalamudPluginInterface
    {
        Shikari.Tests.FakeUiBuilder UiBuilder { get; }
        FileInfo ConfigFile { get; }
        string GetPluginConfigDirectory();
        object GetPluginConfig();
        void SavePluginConfig(object value);
    }
}
namespace Dalamud.Plugin.Services
{
    public interface ICommandManager
    {
        bool AddHandler(string name, Dalamud.Game.Command.CommandInfo command);
        bool RemoveHandler(string name);
    }
    public interface IDataManager { }
    public interface IClientState { }
    public interface IObjectTable { }
    public interface IPartyList { }
    public interface ICondition { }
    public interface IFramework { }
    public interface INotificationManager { }
    public interface ITextureProvider { }
    public interface IGameGui { }
    public interface IDutyState
    {
        event Action<Dalamud.Game.DutyState.IDutyStateEventArgs> DutyStarted;
        event Action<Dalamud.Game.DutyState.IDutyStateEventArgs> DutyCompleted;
    }
    public interface IChatGui
    {
        void Print(string text, string name, object? unused);
        void PrintError(string text, string name, object? unused);
    }
    public interface IPluginLog
    {
        void Information(string message, params object[] args);
        void Error(Exception ex, string message, params object[] args);
        void Warning(string message, params object[] args);
    }
}
namespace Dalamud.Interface.Windowing
{
    public sealed class WindowSystem
    {
        public static WindowSystem? Latest;
        private readonly List<object> windows = new();
        public int Count => windows.Count;
        public WindowSystem(string name) => Latest = this;
        public void AddWindow(object window) { Shikari.Tests.Probe.Hit("window.add." + window.GetType().Name); windows.Add(window); }
        public void RemoveAllWindows() { windows.Clear(); Shikari.Tests.Probe.Cleanup(); }
        public void Draw() { }
    }
}
namespace Shikari
{
    public sealed class TeamProfile { public string Id { get; } = "team"; }
    public sealed class Configuration
    {
        public List<TeamProfile> Teams { get; } = new();
        public string ActiveTeamId { get; set; } = "";
        public string FfLogsClientId => "";
        public string FfLogsClientSecret => "";
        public bool OpenOnDutyStart, RemindersEnabled, AutoAdvanceSlides;
    }
}
namespace Shikari.Services
{
    public static class ConfigMigration
    { public static int Run(string directory, string file) { Tests.Probe.Hit("migration"); return 0; } }
    public sealed class ActionIndex
    {
        public ActionIndex() => Tests.Probe.Hit("construct.ActionIndex");
        public Task BuildAsync(CancellationToken token)
        {
            Tests.Probe.Hit("actions.build"); Tests.Probe.BuildToken = token;
            return Tests.Probe.BuildCompletion?.Task ?? Task.CompletedTask;
        }
    }
    public sealed class PlanStore : Tests.Resource
    {
        public IEnumerable<object> All => new[] { new object() };
        public string? LastSaveError => null;
        public void SaveAll() => Tests.Probe.Save("plans");
        public object RequestSave(object document)
        {
            if (DisposeCount != 0) throw new ObjectDisposedException(nameof(PlanStore));
            Tests.Probe.PlanEvents.Add("request");
            Tests.Probe.Save("plans");
            if (Tests.Probe.ThrowOnPlanRequest) throw new IOException("final plan request fault");
            return new object();
        }
        public override void Dispose()
        {
            Tests.Probe.PlanEvents.Add("dispose");
            base.Dispose();
        }
    }
    public sealed class BackdropStore : Tests.Resource { }
    public sealed class RosterResolver { }
    public sealed class EncounterLearner : Tests.Resource
    {
        public void NoteClear() { }
        public void SaveAll() => Tests.Probe.Save("learner");
        public override void Dispose() => Dispose(true);
        public void Dispose(bool saveChanges)
        {
            DisposeCount++;
            if (saveChanges) SaveAll();
            Tests.Probe.Cleanup();
        }
    }
    public sealed class ReminderEngine : Tests.Resource { }
    public sealed class SlideDirector : Tests.Resource
    {
        private readonly Tests.Hook slide = new("director.slide"), reset = new("director.reset");
        public event Action SlideRequested { add => slide.Add(value); remove => slide.Remove(value); }
        public event Action ResetRequested { add => reset.Add(value); remove => reset.Remove(value); }
        public void ClearSuppression() { }
        public override void Dispose()
        {
            if (slide.Count != 0 || reset.Count != 0) throw new Exception("Director callbacks survived teardown");
            base.Dispose();
        }
    }
}
namespace Shikari.Services.Live
{
    public sealed class ArenaTracker { }
    public sealed class EncounterMonitor : Tests.Resource { }
}
namespace Shikari.Services.Speech
{
    public sealed class SapiSpeechEngine { }
    public sealed class SpeechChannel : Tests.Resource { public SpeechChannel(SapiSpeechEngine engine) { } }
}
namespace Shikari.Services.FfLogs
{
    public sealed class FfLogsClient : Tests.Resource { }
    public sealed class FfLogsAuth
    { public void Forget(string id, string secret) => Tests.Probe.Hit("auth.forget"); }
}
namespace Shikari.Services.RaidPlanIo { public sealed class PlanFetcher : Tests.Resource { } }
namespace Shikari.Services.Replay
{
    public sealed class ReplayStore : Tests.Resource
    {
        public override void Dispose() => Dispose(true);
        public void Dispose(bool saveRecording)
        {
            DisposeCount++;
            if (saveRecording) Tests.Probe.Save("replay");
            Tests.Probe.Cleanup();
        }
    }
}
namespace Shikari.Services.Adaptive { public sealed class AdaptiveService : Tests.Resource { } }
namespace Shikari.Services.Buddy { public sealed class BuddyService : Tests.Resource { } }
namespace Shikari.UI.World { public sealed class ArenaOverlay : Tests.Resource { } }
namespace Shikari.UI.Theme
{
    public sealed class ThemeFonts : Tests.Resource { }
    public static class Sprites { public static void Forget() => Tests.Probe.Cleanup(); }
    public static class EmojiArtwork { public static void Forget() => Tests.Probe.Cleanup(); }
}
namespace Shikari.UI
{
    public class TestWindow : Tests.Resource
    {
        public bool IsOpen { get; set; }
        public void Toggle() => IsOpen = !IsOpen;
    }
    public sealed class MainWindow : TestWindow
    {
        public void OnDirectedSlide() { }
        public void OnDirectedReset() { }
        public void OpenReview() { }
        public void StepSlide(int direction) { }
        public void ResetToFirstSlide() { }
    }
    public sealed class ConfigWindow : TestWindow { }
    public sealed class OverlayWindow : TestWindow { }
    public sealed class MiniPlanWindow : TestWindow { public void ToggleShown() { } }
    public sealed class BuddyWindow : TestWindow { }
}
