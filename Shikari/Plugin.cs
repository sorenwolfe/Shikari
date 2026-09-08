using System;
using System.Threading;
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
    /// Cancelled on unload so worker threads stop before the assembly goes away. Replaced in the
    /// constructor rather than only initialised there, in case a reload reuses the load context.
    /// </summary>
    private static CancellationTokenSource shutdown = new();

    internal static CancellationToken Shutdown => shutdown.Token;

    /// <summary>The planner. The mini window reads which slide it is on.</summary>
    internal static MainWindow Main { get; private set; } = null!;

    private MainWindow mainWindow = null!;
    private ConfigWindow configWindow = null!;
    private OverlayWindow overlayWindow = null!;
    private MiniPlanWindow miniWindow = null!;
    private BuddyWindow buddyWindow = null!;

    public Plugin()
    {
        shutdown = new CancellationTokenSource();

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
        Actions.BuildAsync(Shutdown);

        Plans = new PlanStore();
        Backdrops = new BackdropStore();
        Roster = new RosterResolver();
        Tracker = new ArenaTracker();
        ArenaSpot = new ArenaOverlay();
        Speech = new SpeechChannel(new SapiSpeechEngine());

        // Order matters here: the monitor produces the events, the learner and the reminder
        // engine consume them, and the director consumes both.
        Encounter = new EncounterMonitor();
        Learner = new EncounterLearner();
        Reminders = new ReminderEngine();
        Director = new SlideDirector();
        Adaptive = new AdaptiveService();
        FfLogs = new FfLogsClient();
        FfLogsAuth = new FfLogsAuth();
        PlanFetcher = new PlanFetcher();
        FfLogsAuth.Forget(Config.FfLogsClientId, Config.FfLogsClientSecret);
        Fonts = new ThemeFonts();

        mainWindow = new MainWindow();
        Main = mainWindow;

        configWindow = new ConfigWindow();
        overlayWindow = new OverlayWindow();
        miniWindow = new MiniPlanWindow();

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(overlayWindow);
        WindowSystem.AddWindow(miniWindow);

        overlayWindow.IsOpen = true;

        Director.SlideRequested += mainWindow.OnDirectedSlide;
        Director.ResetRequested += mainWindow.OnDirectedReset;
        Replays = new ReplayStore();
        Buddy = new BuddyService();
        buddyWindow = new BuddyWindow { IsOpen = true };
        WindowSystem.AddWindow(buddyWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
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
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shorthand for /shikari.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyCompleted += OnDutyCompleted;

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
            Log.Error(ex, "Shikari could not {What} while unloading.", what);
        }
    }

    public void Dispose()
    {
        // Nothing running in the background should still be touching us by the time the rest of
        // this method starts pulling things apart.
        Safely(shutdown.Cancel, "stop background work");

        // Detach from the game before anything else. Everything below is ours and can be leaked
        // without consequence; these five are Dalamud's and must come off no matter what.
        Safely(() => CommandManager.RemoveHandler(CommandName), "remove " + CommandName);
        Safely(() => CommandManager.RemoveHandler(CommandAlias), "remove " + CommandAlias);
        Safely(() => ArenaSpot?.Dispose(), "detach the arena spot");
        Safely(() => PluginInterface.UiBuilder.Draw -= WindowSystem.Draw, "detach the draw hook");
        Safely(() => PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig, "detach the config button");
        Safely(() => PluginInterface.UiBuilder.OpenMainUi -= ToggleMain, "detach the main button");
        Safely(() => DutyState.DutyStarted -= OnDutyStarted, "detach duty start");
        Safely(() => DutyState.DutyCompleted -= OnDutyCompleted, "detach duty completion");

        Safely(() => Director.SlideRequested -= mainWindow.OnDirectedSlide, "detach the slide director");
        Safely(() => Director.ResetRequested -= mainWindow.OnDirectedReset, "detach the slide reset");

        Safely(() => Buddy?.Dispose(), "stop the raid buddy");
        Safely(Director.Dispose, "shut down the slide director");
        Safely(() => Replays?.Dispose(), "finish mechanic recording");
        Safely(() => Adaptive?.Dispose(), "stop adaptive mechanics");
        Safely(Reminders.Dispose, "shut down the reminder engine");

        // Before the windows, so a line still being spoken is cut off rather than left talking
        // over a game the plugin has already let go of.
        Safely(Speech.Dispose, "shut down speech");

        Safely(Learner.Dispose, "shut down the learner");
        Safely(Encounter.Dispose, "shut down the encounter monitor");
        Safely(FfLogs.Dispose, "close the FF Logs client");
        Safely(PlanFetcher.Dispose, "close the plan fetcher");
        Safely(Backdrops.Dispose, "drop the backdrop textures");
        Safely(Fonts.Dispose, "release the font handles");
        Safely(Sprites.Forget, "drop the sprite handles");
        Safely(EmojiArtwork.Forget, "drop the emoji handles");

        Safely(WindowSystem.RemoveAllWindows, "remove the windows");
        Safely(mainWindow.Dispose, "dispose the planner window");
        Safely(configWindow.Dispose, "dispose the settings window");
        Safely(overlayWindow.Dispose, "dispose the overlay");
        Safely(miniWindow.Dispose, "dispose the mini plan");
        Safely(() => buddyWindow?.Dispose(), "dispose the raid buddy window");

        // Saving comes last. A disk error here used to abandon the rest of the teardown.
        Safely(Plans.SaveAll, "save the plans");
        Safely(Learner.SaveAll, "save the learned timings");
        Safely(() => PluginInterface.SavePluginConfig(Config), "save the settings");

        Safely(shutdown.Dispose, "dispose the shutdown token");
    }
}
