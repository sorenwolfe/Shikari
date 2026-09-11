using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Shikari.Tests;

// Plugin.cs itself is compiled into this harness. Only its game, UI and service boundaries
// are substituted, so a missing constructor rollback or cleanup registration fails here.
public static class PluginLifecycleTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static FakeHost Setup(string? failAt = null)
    {
        Probe.Reset();
        Probe.FailAt = failAt;
        var host = new FakeHost();
        foreach (var name in new[] { "PluginInterface", "CommandManager", "DutyState", "ChatGui", "Log" })
            typeof(Plugin).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, host);
        return host;
    }

    private static void Released(FakeHost host, string scenario)
    {
        Check(host.UiBuilder.Count == 0 && host.DutyHookCount == 0, scenario + ": host callbacks survived teardown");
        Check(Dalamud.Interface.Windowing.WindowSystem.Latest!.Count == 0, scenario + ": windows remained registered");
        var unreleased = Probe.Resources.Where(r => r.DisposeCount != 1).Select(r => r.Name + "=" + r.DisposeCount);
        Check(!unreleased.Any(), scenario + ": acquired resources must be released exactly once: " + string.Join(", ", unreleased));
    }

    public static void Run()
    {
        // The breaks caught here are missing rollback, missing resource/hook registration,
        // saving incomplete state, and replacing the original exception during cleanup.
        var startupFailures = new[]
        {
            "migration", "config.load", "construct.ActionIndex", "actions.build", "construct.PlanStore",
            "construct.BackdropStore", "construct.ArenaOverlay", "construct.EncounterMonitor",
            "construct.EncounterLearner", "construct.ReminderEngine", "construct.SlideDirector",
            "construct.AdaptiveService", "construct.FfLogsClient", "construct.PlanFetcher", "auth.forget",
            "construct.ThemeFonts", "construct.MainWindow", "construct.ConfigWindow", "construct.OverlayWindow",
            "construct.MiniPlanWindow", "window.add.MainWindow", "window.add.MiniPlanWindow",
            "director.slide.add", "director.reset.add", "construct.ReplayStore", "construct.BuddyService",
            "construct.BuddyWindow", "window.add.BuddyWindow", "command.add./shikari", "command.add./rp",
            "ui.draw.add", "ui.config.add", "ui.main.add", "duty.start.add", "duty.complete.add", "log.loaded",
        };
        foreach (var failure in startupFailures)
        {
            var host = Setup(failure);
            var expected = Probe.StartupFailure;
            Exception? caught = null;
            try { _ = new Plugin(); }
            catch (Exception ex) { caught = ex; }
            Check(ReferenceEquals(caught, expected), failure + ": constructor must preserve the startup exception");
            Released(host, failure);
            Check(host.Commands.Count == 0, failure + ": owned command survived rollback");
            Check(Probe.Saves.Count == 0, failure + ": rollback persisted incomplete state");
            if (Probe.BuildToken.HasValue)
                Check(Probe.BuildToken.Value.IsCancellationRequested, failure + ": action build was not cancelled");
        }

        // Rejecting acquisition must never grant permission to remove somebody else's command.
        foreach (var collision in new[] { "/shikari", "/rp" })
        {
            var host = Setup();
            var foreign = new Dalamud.Game.Command.CommandInfo((_, _) => { });
            host.Commands.Add(collision, foreign);
            var plugin = new Plugin();
            plugin.Dispose();
            plugin.Dispose();
            Released(host, "collision " + collision);
            Check(host.Commands.Count == 1 && ReferenceEquals(host.Commands[collision], foreign),
                "A failed " + collision + " acquisition must preserve the existing handler");
            Check(Probe.Saves.SequenceEqual(new[] { "replay", "learner", "plans", "config" }), "Normal unload should save once");
        }

        // Rollback must preserve foreign registrations and the original startup error even
        // when every cleanup diagnostic fails too.
        {
            var host = Setup("log.loaded");
            var foreign = new Dalamud.Game.Command.CommandInfo((_, _) => { });
            host.Commands.Add("/rp", foreign);
            Probe.ThrowDuringCleanup = true;
            Exception? caught = null;
            try { _ = new Plugin(); }
            catch (Exception ex) { caught = ex; }
            Check(ReferenceEquals(caught, Probe.StartupFailure), "Throwing rollback masked the startup error");
            Released(host, "throwing rollback with command collision");
            Check(host.Commands.Count == 1 && ReferenceEquals(host.Commands["/rp"], foreign),
                "Rollback removed a command owned by someone else");
            Check(Probe.Saves.Count == 0, "Throwing rollback persisted incomplete state");
        }

        // One bad cleanup, including diagnostic logging, cannot prevent subsequent releases.
        {
            var host = Setup();
            var plugin = new Plugin();
            Probe.ThrowDuringCleanup = true;
            plugin.Dispose();
            plugin.Dispose();
            Released(host, "throwing cleanup");
            Check(host.Commands.Count == 0, "Throwing cleanup must still remove both commands");
            Check(Probe.Saves.SequenceEqual(new[] { "replay", "learner", "plans", "config" }), "Save failure must not prevent later saves");
        }

        // A stalled background operation must receive cancellation without blocking unload forever.
        {
            var host = Setup();
            Probe.BuildCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var plugin = new Plugin();
            var token = Probe.BuildToken!.Value;
            var elapsed = Stopwatch.StartNew();
            plugin.Dispose();
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(4), "Action build teardown must have a bounded wait");
            Check(token.IsCancellationRequested, "A pending action build must be cancelled");
            Check(Plugin.Shutdown.IsCancellationRequested, "Workers must be able to read cancellation during teardown");
            Probe.BuildCompletion.SetResult();
            Released(host, "pending action build");
        }

        // An old disposed instance must not cancel the next startup's worker token.
        {
            _ = Setup();
            var previous = new Plugin();
            previous.Dispose();
            var host = Setup();
            var current = new Plugin();
            var token = Probe.BuildToken!.Value;
            previous.Dispose();
            Check(!token.IsCancellationRequested, "Repeated stale Dispose cancelled a reloaded instance");
            current.Dispose();
            Released(host, "reload");

            var oldResources = Probe.Resources.ToArray();
            host = Setup("construct.PlanStore");
            try { _ = new Plugin(); }
            catch (Exception ex) when (ReferenceEquals(ex, Probe.StartupFailure)) { }
            Released(host, "failed reload");
            Check(oldResources.All(r => r.DisposeCount == 1), "Failed reload disposed old static services a second time");
            Check(Plugin.Shutdown.IsCancellationRequested, "Shutdown must remain readable after disposing its source");
        }
        Console.WriteLine("PASS: real Plugin constructor rollback at " + startupFailures.Length +
            " fault boundaries, owned commands, idempotent resilient cleanup, normal saves, cancellation and reload");
    }
}
