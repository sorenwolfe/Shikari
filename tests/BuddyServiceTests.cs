using System;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Buddy;
using Dalamud.Game.ClientState.Conditions;

namespace Shikari.Tests
{
    public static class BuddyServiceTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Run()
        {
            var plan = new PlanDocument();
            plan.Slides.Clear();
            var slide = new Slide { Title = "East spread" };
            plan.Slides.Add(slide);
            var entry = new TimelineEntry { Label = "Spread", SlideId = slide.Id, CastActionId = 800 };
            plan.Timeline.Add(entry);
            plan.AdaptiveMechanics.Add(new AdaptiveMechanic { Enabled = true, Label = "Assignment", TerritoryId = 100,
                AnchorActionId = 800, WindowSeconds = 10, Branches = new() { new() { StatusId = 900, SlideId = slide.Id } } });
            Plugin.Plans.Active = plan;
            Plugin.Config.BuddyEnabled = true;
            using var buddy = new BuddyService();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "The service must not invent an AFK return when enabled.");
            Plugin.Plans.Active = null;
            Plugin.ObjectTable.LocalPlayer!.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Sleeping && !buddy.Presentation.HasCue,
                "The real local AFK status should sleep even without a plan, independently of tactical guidance.");
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Welcoming, "A live AFK-to-online update should welcome the player.");
            var welcomeStarted = buddy.Ambient.StartedAtUtc;
            Plugin.Plans.Active = plan; Plugin.Framework.Tick();
            Plugin.Plans.Active = null; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Welcoming && buddy.Ambient.StartedAtUtc == welcomeStarted,
                "Switching or clearing plans must not restart a cosmetic welcome.");
            Plugin.Config.BuddyEnabled = false; Plugin.Framework.Tick();
            Plugin.Config.BuddyEnabled = true; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Disabling/re-enabling must not restore an old welcome.");
            foreach (var flag in new[] { ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95 })
            {
                Plugin.Condition[flag] = true; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Focused, "Each supported duty flag should produce the focused pose.");
                Plugin.Condition[flag] = false;
            }
            foreach (var flag in new[] { ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut,
                ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInCutSceneEvent })
            {
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
                Plugin.Condition[flag] = true;
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Hidden/loading states must suppress the return animation.");
                Plugin.Condition[flag] = false; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, "A suppressed welcome must not reappear after visibility returns.");
            }
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.ThrowOnRead = true; Plugin.Framework.Tick();
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47;
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.ThrowOnRead = false; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Unreadable online status must break the return transition baseline.");
            foreach (var unknown in new uint[] { 0, 5, 9, 10 })
            {
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = unknown; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Default, disconnected, not-found and offline statuses cannot mean returned from AFK.");
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, "An unknown status gap cannot queue a later welcome.");
            }
            foreach (var mode in new[] { "gpose", "death", "actor", "territory" })
            {
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
                if (mode == "gpose") Plugin.ClientState.IsGPosing = true;
                if (mode == "death") Plugin.ObjectTable.LocalPlayer.IsDead = true;
                if (mode == "actor") Plugin.ObjectTable.LocalPlayer.EntityId = 99;
                if (mode == "territory") Plugin.ClientState.Move(101);
                Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47; Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, mode + " must suppress any old AFK return.");
                Plugin.ClientState.IsGPosing = false; Plugin.ObjectTable.LocalPlayer.IsDead = false;
                Plugin.ObjectTable.LocalPlayer.EntityId = 42; Plugin.ClientState.Move(100); Plugin.Framework.Tick();
                Check(buddy.Ambient.State == BuddyAmbientState.Idle, mode + " recovery must not replay a suppressed return.");
            }
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
            Plugin.ClientState.IsLoggedIn = false; Plugin.Framework.Tick();
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47;
            Plugin.ClientState.IsLoggedIn = true; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Logout/login must not impersonate returning from AFK.");
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17;
            Plugin.Condition[ConditionFlag.InCombat] = true; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Focused, "The raw combat flag must prevent sleeping before EncounterMonitor catches up.");
            Plugin.Condition[ConditionFlag.InCombat] = false;
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Combat's AFK state must not turn into a later welcome.");
            Plugin.Plans.Active = plan;
            Plugin.Encounter.Begin();
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 17; Plugin.Framework.Tick();
            Check(buddy.Ambient.State == BuddyAmbientState.Focused, "Combat must prevent sleeping even if AFK remains set.");
            Plugin.Reminders.Deliver(new ActiveCall { SourcePlan = plan, ScheduledAtUtc = DateTime.UtcNow, PlanId = plan.Id, EntryId = entry.Id, ForLocalPlayer = true,
                Headline = "Use your mitigation", FiredAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddSeconds(20) });
            Check(buddy.Presentation.Body == "Use your mitigation", "The real service must consume fresh formatted delivery events.");
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.ThrowOnRead = true; Plugin.Framework.Tick();
            Check(buddy.Presentation.Body == "Use your mitigation", "A cosmetic online-status failure must preserve valid tactical calls.");
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.ThrowOnRead = false;
            Plugin.ObjectTable.LocalPlayer.OnlineStatus.RowId = 47;
            Plugin.Config.BuddyEnabled = false; Plugin.Framework.Tick();
            Check(!buddy.Presentation.HasCue, "Disabling the service must clear the bubble immediately on update.");
            Plugin.Config.BuddyEnabled = true; Plugin.Framework.Tick();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Check(!buddy.Presentation.HasCue, "Enabling during a pull must not assume its rule snapshot.");
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Check(buddy.Presentation.Mood == BuddyMood.Listening && buddy.Presentation.HasCue, "A validated matching cast starts a waiting cue.");
            Plugin.Adaptive.Decide(new AdaptiveDecision { AnchorActionId = 800, Occurrence = 1, Applied = true, SlideId = slide.Id });
            Check(buddy.Presentation.Body == "East spread", "An actual applied decision must use the authored board title.");
            Plugin.Adaptive.Invalidate();
            Check(!buddy.Presentation.HasCue, "Unreadable status data must immediately clear guidance.");
            Plugin.Reminders.Deliver(new ActiveCall { SourcePlan = plan, ScheduledAtUtc = DateTime.UtcNow, PlanId = plan.Id, EntryId = entry.Id, ForLocalPlayer = true,
                Headline = "Generic stack", FiredAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddSeconds(20) });
            Check(!buddy.Presentation.HasCue, "A delayed generic call must not replace the same cast's observed assignment after its bubble clears.");
            plan.AdaptiveMechanics[0].Branches[0].Label = "Long debuff - spread";
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Plugin.Adaptive.Decide(new AdaptiveDecision { AnchorActionId = 800, Occurrence = 1, Applied = true, SlideId = slide.Id });
            Check(buddy.Presentation.Body == "Long debuff - spread" && buddy.Presentation.Detail.Contains("East spread"),
                "A unique meaningful branch label should explain the assignment, with the selected board as context.");
            plan.AdaptiveMechanics[0].Branches.Add(new StatusBranch { Label = "Short debuff", StatusId = 901, SlideId = slide.Id });
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Plugin.Adaptive.Decide(new AdaptiveDecision { AnchorActionId = 800, Occurrence = 1, Applied = true, SlideId = slide.Id });
            Check(buddy.Presentation.Body == "East spread", "A shared destination does not identify which branch matched.");
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Plugin.Director.IsSuppressed = true; Plugin.Framework.Tick();
            Plugin.Adaptive.Decide(new AdaptiveDecision { AnchorActionId = 800, Occurrence = 1, Applied = true, SlideId = slide.Id });
            Check(!buddy.Presentation.HasCue, "A manual hold must silence adaptive board cues even with a late decision.");
            Plugin.Director.IsSuppressed = false;
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            var alternate = new PlanDocument { Id = plan.Id }; Plugin.Plans.Active = alternate; Plugin.Framework.Tick();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Check(!buddy.Presentation.HasCue, "Replacing the active plan object must invalidate frozen rules even when IDs match.");
            alternate.Timeline.Add(new TimelineEntry { Id = entry.Id });
            Plugin.Reminders.Deliver(new ActiveCall { SourcePlan = plan, ScheduledAtUtc = DateTime.UtcNow, PlanId = alternate.Id, EntryId = entry.Id, ForLocalPlayer = true,
                Headline = "Wrong source plan", FiredAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddSeconds(20) });
            Check(!buddy.Presentation.HasCue, "Matching plan and entry IDs cannot disguise a detached source plan reference.");
            Plugin.Plans.Active = plan;
            plan.AdaptiveMechanics.Add(new AdaptiveMechanic { Enabled = true, Label = "Overlap", TerritoryId = 100,
                AnchorActionId = 800, WindowSeconds = 10, Branches = new() { new() { StatusId = 901, SlideId = slide.Id } } });
            Plugin.Encounter.End(); Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, StartedAtUtc = DateTime.UtcNow });
            Check(!buddy.Presentation.HasCue, "Overlapping enabled rules must not advertise a detectable assignment.");
            buddy.Dispose();
            Check(buddy.Ambient.State == BuddyAmbientState.Idle, "Disposal must clear the last cosmetic presentation.");
            Check(Plugin.Framework.Count == 0 && Plugin.Reminders.Count == 0 && Plugin.Adaptive.Count == 0 && Plugin.Encounter.Count == 0 && Plugin.ClientState.Count == 0,
                "All event subscriptions must detach on dispose.");
            Console.WriteLine("Buddy service integration checks passed.");
        }
    }
}

namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag { BoundByDuty, BoundByDuty56, BoundByDuty95, InCombat, BetweenAreas, BetweenAreas51,
        LoggingOut, WatchingCutscene, WatchingCutscene78, OccupiedInCutSceneEvent }
}
namespace Shikari
{
    internal static class Plugin
    {
        public static FakeConfig Config { get; } = new(); public static FakePlans Plans { get; } = new();
        public static FakeFramework Framework { get; } = new(); public static FakeEncounter Encounter { get; } = new();
        public static FakeClient ClientState { get; } = new(); public static FakeObjects ObjectTable { get; } = new();
        public static FakeReminders Reminders { get; } = new(); public static FakeAdaptive Adaptive { get; } = new();
        public static FakeDirector Director { get; } = new(); public static FakeLog Log { get; } = new();
        public static FakeConditions Condition { get; } = new();
    }
    internal sealed class FakeConfig { public bool BuddyEnabled; public bool RemindersEnabled = true; public bool AutoAdvanceSlides = true; }
    internal sealed class FakePlans { public PlanDocument? Active; }
    internal sealed class FakeFramework : Dalamud.Plugin.Services.IFramework
    { public event Action<Dalamud.Plugin.Services.IFramework>? Update; public void Tick() => Update?.Invoke(this); public int Count => Update?.GetInvocationList().Length ?? 0; }
    internal sealed class FakeEncounter
    {
        public bool InCombat; public event Action? CombatStarted; public event Action? CombatEnded; public event Action? Wiped;
        public event Action<CastEvent>? CastStarted;
        public int Count => (CombatStarted?.GetInvocationList().Length ?? 0) + (CombatEnded?.GetInvocationList().Length ?? 0) + (Wiped?.GetInvocationList().Length ?? 0) + (CastStarted?.GetInvocationList().Length ?? 0);
        public void Begin() { InCombat = true; CombatStarted?.Invoke(); } public void End() { InCombat = false; CombatEnded?.Invoke(); }
        public void Cast(CastEvent value) => CastStarted?.Invoke(value); public void Wipe() => Wiped?.Invoke();
    }
    internal sealed class FakeClient { public bool IsLoggedIn = true; public bool IsGPosing; public uint TerritoryType = 100; public event Action<uint>? TerritoryChanged; public int Count => TerritoryChanged?.GetInvocationList().Length ?? 0; public void Move(uint territory) { TerritoryType = territory; TerritoryChanged?.Invoke(territory); } }
    internal sealed class FakeObjects { public FakePlayer? LocalPlayer = new(); }
    internal sealed class FakePlayer { public uint EntityId = 42; public bool IsDead; public FakeOnlineStatus OnlineStatus { get; } = new(); }
    internal sealed class FakeOnlineStatus { private uint rowId = 47; public bool ThrowOnRead; public uint RowId { get => ThrowOnRead ? throw new Exception("Unavailable status") : rowId; set => rowId = value; } }
    internal sealed class FakeConditions { private readonly System.Collections.Generic.HashSet<ConditionFlag> active = new();
        public bool this[ConditionFlag flag] { get => active.Contains(flag); set { if (value) active.Add(flag); else active.Remove(flag); } } }
    internal sealed class FakeReminders { public event Action<ActiveCall>? CallDelivered; public int Count => CallDelivered?.GetInvocationList().Length ?? 0; public void Deliver(ActiveCall value) => CallDelivered?.Invoke(value); }
    internal sealed class FakeAdaptive { public event Action<AdaptiveDecision>? Decided; public event Action? EvidenceInvalidated; public int Count => (Decided?.GetInvocationList().Length ?? 0) + (EvidenceInvalidated?.GetInvocationList().Length ?? 0); public void Decide(AdaptiveDecision value) => Decided?.Invoke(value); public void Invalidate() => EvidenceInvalidated?.Invoke(); }
    internal sealed class FakeDirector { public bool IsSuppressed; }
    internal sealed class FakeLog { public void Warning(Exception ex, string text) { throw new Exception(text, ex); } }
}
namespace Shikari.Services
{
    public sealed class ActiveCall { public PlanDocument? SourcePlan { get; init; } public DateTime ScheduledAtUtc { get; init; } public string PlanId { get; init; } = ""; public string EntryId { get; init; } = ""; public string Headline { get; init; } = ""; public bool ForLocalPlayer { get; init; } public DateTime FiredAtUtc { get; init; } public DateTime ExpiresAtUtc { get; set; } }
    public sealed class CastEvent { public uint ActionId { get; init; } public int Occurrence { get; init; } public DateTime StartedAtUtc { get; init; } }
}
