using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Shikari.Services;
using Shikari.Services.FfLogs;
using Shikari.Services.Live;
using Shikari.Services.Speech;
using Shikari.Services.RaidPlanIo;
using Shikari.Services.Replay;
using Shikari.Services.Adaptive;
using Shikari.Services.Buddy;
using Shikari.UI;
using Shikari.UI.World;
using Shikari.UI.Theme;

namespace Shikari;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/shikari";
    private const string CommandAlias = "/rp";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    internal static Configuration Config { get; private set; } = null!;
    internal static ActionIndex Actions { get; private set; } = null!;
    internal static PlanStore Plans { get; private set; } = null!;
    internal static BackdropStore Backdrops { get; private set; } = null!;
    internal static EncounterMonitor Encounter { get; private set; } = null!;
    internal static ReminderEngine Reminders { get; private set; } = null!;
    internal static RosterResolver Roster { get; private set; } = null!;
    internal static ArenaTracker Tracker { get; private set; } = null!;
    internal static ArenaOverlay ArenaSpot { get; private set; } = null!;
    internal static SpeechChannel Speech { get; private set; } = null!;
    internal static EncounterLearner Learner { get; private set; } = null!;
    internal static SlideDirector Director { get; private set; } = null!;
    internal static FfLogsClient FfLogs { get; private set; } = null!;
    internal static FfLogsAuth FfLogsAuth { get; private set; } = null!;
    internal static PlanFetcher PlanFetcher { get; private set; } = null!;
    internal static ThemeFonts Fonts { get; private set; } = null!;
    internal static ReplayStore Replays { get; private set; } = null!;
    internal static AdaptiveService Adaptive { get; private set; } = null!;
    internal static BuddyService Buddy { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("Shikari");

    /// <summary>
    /// The source belongs to this instance. Keeping the published token separately also makes
    /// it safe for workers to read Shutdown after the source has been disposed.
    /// </summary>
    private readonly CancellationTokenSource shutdown = new();
    private static CancellationToken shutdownToken;

    internal static CancellationToken Shutdown => shutdownToken;

    private readonly List<(Action Release, string Description)> hooks = new();
    private readonly List<(Action Release, string Description)> resources = new();
    private readonly List<(Action Release, string Description)> windows = new();
    private readonly List<(Action Release, string Description)> saves = new();
    private Task actionBuild = Task.CompletedTask;
    private PlanStore? planStore;
    private bool initialized;
    private int disposed;

    /// <summary>The planner. The mini window reads which slide it is on.</summary>
    internal static MainWindow Main { get; private set; } = null!;

    private MainWindow mainWindow = null!;
    private ConfigWindow configWindow = null!;
    private OverlayWindow overlayWindow = null!;
    private MiniPlanWindow miniWindow = null!;
    private BuddyWindow buddyWindow = null!;

    public Plugin()
    {
        shutdownToken = shutdown.Token;
        try
        {
            Initialize();
            initialized = true;
        }
        catch
        {
            // Dalamud cannot Dispose an instance whose constructor did not return.
            Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        // Before the settings are read: reading them creates the new file, and an existing new
        // file is how the migration decides it has already run.
        var broughtOver = ConfigMigration.Run(
            PluginInterface.GetPluginConfigDirectory(),
            PluginInterface.ConfigFile.FullName);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (broughtOver > 0)
            Log.Information($"Brought {broughtOver} plan(s) across from Shikari.");
        if (Config.Teams.Count == 0)
            Config.Teams.Add(new TeamProfile());
        if (string.IsNullOrEmpty(Config.ActiveTeamId))
            Config.ActiveTeamId = Config.Teams[0].Id;

        Actions = new ActionIndex();
        actionBuild = Actions.BuildAsync(Shutdown);

        Plans = planStore = new PlanStore();
        var backdrops = new BackdropStore();
        Backdrops = backdrops;
        resources.Add((backdrops.Dispose, "dispose the backdrops"));
        Roster = new RosterResolver();
        Tracker = new ArenaTracker();
        ArenaSpot = Own(new ArenaOverlay(), hooks);
        Speech = Own(new SpeechChannel(new SapiSpeechEngine()));

        // Order matters here: the monitor produces the events, the learner and the reminder
        // engine consume them, and the director consumes both.
        Encounter = Own(new EncounterMonitor());
        var learner = new EncounterLearner();
        Learner = learner;
        resources.Add((() => learner.Dispose(initialized), "dispose the learner"));
        Reminders = Own(new ReminderEngine());
        Director = Own(new SlideDirector());
        Adaptive = Own(new AdaptiveService());
        FfLogs = Own(new FfLogsClient());
        FfLogsAuth = new FfLogsAuth();
        PlanFetcher = Own(new PlanFetcher());
        FfLogsAuth.Forget(Config.FfLogsClientId, Config.FfLogsClientSecret);
        Fonts = Own(new ThemeFonts());

        mainWindow = Own(new MainWindow(), windows);
        Main = mainWindow;

        configWindow = Own(new ConfigWindow(), windows);
        overlayWindow = Own(new OverlayWindow(), windows);
        miniWindow = Own(new MiniPlanWindow(), windows);

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(overlayWindow);
        WindowSystem.AddWindow(miniWindow);

        overlayWindow.IsOpen = true;

        var director = Director;
        director.SlideRequested += mainWindow.OnDirectedSlide;
        hooks.Add((() => director.SlideRequested -= mainWindow.OnDirectedSlide, "detach the slide director"));
        director.ResetRequested += mainWindow.OnDirectedReset;
        hooks.Add((() => director.ResetRequested -= mainWindow.OnDirectedReset, "detach the slide reset"));
        var replays = new ReplayStore();
        Replays = replays;
        resources.Add((() => replays.Dispose(initialized), "dispose the replay store"));
        Buddy = Own(new BuddyService());
        buddyWindow = Own(new BuddyWindow(), windows);
        buddyWindow.IsOpen = true;
        WindowSystem.AddWindow(buddyWindow);

        var commands = CommandManager;
        if (commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the raid strategy planner.\n" +
                "        /shikari config  →  open settings\n" +
                "        /shikari review  →  open mechanic replay\n" +
                "        /shikari calls   →  toggle live shotcalls\n" +
                "        /shikari next    →  show the next slide\n" +
                "        /shikari prev    →  show the previous slide\n" +
                "        /shikari follow  →  toggle slides following the fight\n" +
                "        /shikari mini    →  toggle the small in-fight window\n" +
                "        /shikari reset   →  jump back to the first slide",
        }))
            hooks.Add((() => commands.RemoveHandler(CommandName), "remove " + CommandName));

        if (commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shorthand for /shikari.",
        }))
            hooks.Add((() => commands.RemoveHandler(CommandAlias), "remove " + CommandAlias));

        var ui = PluginInterface.UiBuilder;
        ui.Draw += WindowSystem.Draw;
        hooks.Add((() => ui.Draw -= WindowSystem.Draw, "detach the draw hook"));
        ui.OpenConfigUi += ToggleConfig;
        hooks.Add((() => ui.OpenConfigUi -= ToggleConfig, "detach the config button"));
        ui.OpenMainUi += ToggleMain;
        hooks.Add((() => ui.OpenMainUi -= ToggleMain, "detach the main button"));
        var duty = DutyState;
        duty.DutyStarted += OnDutyStarted;
        hooks.Add((() => duty.DutyStarted -= OnDutyStarted, "detach duty start"));
        duty.DutyCompleted += OnDutyCompleted;
        hooks.Add((() => duty.DutyCompleted -= OnDutyCompleted, "detach duty completion"));

        var pluginInterface = PluginInterface;
        var config = Config;
        saves.Add((() => pluginInterface.SavePluginConfig(config), "save the settings"));

        Log.Information("Shikari loaded.");
    }

    private void OnDutyStarted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        if (Config.OpenOnDutyStart)
            mainWindow.IsOpen = true;
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        // Lets the learner mark the pull it just recorded as a clear.
        Learner.NoteClear();
    }

    private void OnCommand(string command, string args)
    {
        var argument = args.Trim().ToLowerInvariant();

        switch (argument)
        {
            case "":
                mainWindow.Toggle();
                break;

            case "config":
            case "settings":
                configWindow.Toggle();
                break;

            case "review":
            case "replay":
                mainWindow.OpenReview();
                break;

            case "calls":
            case "reminders":
                Config.RemindersEnabled = !Config.RemindersEnabled;
                PluginInterface.SavePluginConfig(Config);
                ChatGui.Print(
                    Config.RemindersEnabled ? "Shotcalls are on." : "Shotcalls are off.",
                    "Shikari",
                    null);
                break;

            case "next":
                mainWindow.StepSlide(1);
                mainWindow.IsOpen = true;
                break;

            case "follow":
            case "auto":
                Config.AutoAdvanceSlides = !Config.AutoAdvanceSlides;
                Director.ClearSuppression();
                PluginInterface.SavePluginConfig(Config);
                ChatGui.Print(
                    Config.AutoAdvanceSlides
                        ? "Slides will follow the fight."
                        : "Slides will stay where you put them.",
                    "Shikari",
                    null);
                break;

            case "reset":
                mainWindow.ResetToFirstSlide();
                Director.ClearSuppression();
                break;

            case "mini":
            case "small":
                miniWindow.ToggleShown();
                break;

            case "prev":
            case "previous":
                mainWindow.StepSlide(-1);
                mainWindow.IsOpen = true;
                break;

            default:
                ChatGui.PrintError(
                    $"Unknown option '{argument}'. Try: config, review, calls, follow, mini, reset, next, prev.",
                    "Shikari",
                    null);
                break;
        }
    }

    public void ToggleConfig() => configWindow.Toggle();

    public void ToggleMain() => mainWindow.Toggle();

    public static void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    /// <summary>
    /// Teardown has to finish even when a step of it throws. A half-unloaded plugin leaves its
    /// commands registered and its callbacks pointing at an assembly that is going away, and the
    /// next version then fails to load — which looks to the player like the update is broken.
    /// </summary>
    private static void Safely(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            // Diagnostics are best effort too: a logger failure must not strand later hooks.
            try { Log?.Error(ex, "Shikari could not {What} while unloading.", what); }
            catch { }
        }
    }

    private T Own<T>(T resource, List<(Action Release, string Description)>? releases = null)
        where T : IDisposable
    {
        // Capture only successfully acquired instances, never a mutable static service slot.
        (releases ?? resources).Add((resource.Dispose, "dispose " + typeof(T).Name));
        return resource;
    }

    private static void ReleaseAll(List<(Action Release, string Description)> releases)
    {
        for (var i = releases.Count - 1; i >= 0; --i)
            Safely(releases[i].Release, releases[i].Description);
        releases.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Safely(shutdown.Cancel, "stop background work");
        ReleaseAll(hooks);
        Safely(WindowSystem.RemoveAllWindows, "remove the windows");

        // The index owns its unpublished build data. Allow cancellation to finish without
        // holding the game thread indefinitely if a sheet provider stops responding.
        Safely(() => actionBuild.Wait(TimeSpan.FromSeconds(1)), "wait for the action index");

        ReleaseAll(resources);
        ReleaseAll(windows);
        Safely(Sprites.Forget, "drop the sprite handles");
        Safely(EmojiArtwork.Forget, "drop the emoji handles");

        // Keep the queue alive until windows and recording services have finished changing
        // plans. Final snapshots join its ordered worker; Dispose bounds the drain instead
        // of waiting indefinitely on synchronous SaveAll behind a stalled disk write.
        if (planStore is { } plans)
        {
            Safely(() =>
            {
                try
                {
                    if (initialized)
                        foreach (var plan in plans.All)
                            plans.RequestSave(plan);
                }
                finally
                {
                    // Constructor rollback still owns the queue, but must not save its
                    // incompletely initialized library.
                    plans.Dispose();
                }
                if (plans.LastSaveError is { } error)
                    Log.Warning("Shikari plan storage closed with unsaved changes: {Error}", error);
            }, "close plan storage");
            planStore = null;
        }

        // Failed startup must not overwrite settings or plans with incompletely loaded state.
        if (initialized)
            foreach (var save in saves)
                Safely(save.Release, save.Description);
        saves.Clear();

        if (actionBuild.IsCompleted)
        {
            _ = actionBuild.Exception;
            Safely(shutdown.Dispose, "dispose the shutdown token");
        }
        else
            _ = actionBuild.ContinueWith(completed =>
            {
                _ = completed.Exception;
                Safely(shutdown.Dispose, "dispose the shutdown token");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
