using System;
using System.Collections.Generic;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Adaptive;
namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Shikari.Services
{
    public sealed class CastEvent
    {
        public uint ActionId { get; set; }
        public int Occurrence { get; set; }
        public float CombatTime { get; set; }
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static FakeEncounter Encounter = new();
        public static FakePlans Plans = new();
        public static FakeClient ClientState = new();
        public static FakeObjects ObjectTable = new();
        public static FakeFramework Framework = new();
        public static FakeConfig Config = new();
        public static FakeReminders Reminders = new();
        public static FakeLog Log = new();
        public static SlideDirector Director = null!;
    }
    public sealed class FakeConfig { public bool AutoAdvanceSlides = true, AutoAdvanceOnCast = true, ResetSlidesOnWipe = true; public float ManualOverrideSeconds = 30; }
    public sealed class FakeLog { public void Warning(Exception e, string message) { } }
    public sealed class FakePlans { public PlanDocument Active = PlanDocument.CreateDefault(); }
    public sealed class FakeClient
    {
        public uint TerritoryType = 1;
        public event Action<uint>? TerritoryChanged;
        public void Change() { TerritoryType++; TerritoryChanged?.Invoke(TerritoryType); }
    }
    public sealed class FakeObjects { public FakePlayer? LocalPlayer = new(); }
    public sealed class FakePlayer { public uint EntityId = 20; public bool IsDead; public List<FakeStatus> StatusList = new(); }
    public sealed class FakeStatus { public uint StatusId = 10, SourceId = 99; public float RemainingTime = 30; public ushort Param = 0; }
    public sealed class FakeFramework : Dalamud.Plugin.Services.IFramework
    {
        public event Action<Dalamud.Plugin.Services.IFramework>? Update;
        public void Tick(float time) { Plugin.Encounter.CombatElapsed = time; Update?.Invoke(this); }
    }
    public sealed class FakeReminders
    {
        public event Action<TimelineEntry>? StepFired;
        public void Fire(TimelineEntry entry) => StepFired?.Invoke(entry);
    }
    public sealed class FakeEncounter
    {
        public bool InCombat;
        public float CombatElapsed;
        public event Action? CombatStarted;
        public event Action? CombatEnded;
        public event Action? Wiped;
        public event Action<CastEvent>? CastStarted;
        public void Begin() { InCombat = true; CombatElapsed = 0; CombatStarted?.Invoke(); }
        public void End() { InCombat = false; CombatEnded?.Invoke(); Wiped?.Invoke(); }
        public void WipeOnly() => Wiped?.Invoke();
        public void Cast(int occurrence = 1) => CastStarted?.Invoke(new CastEvent { ActionId = 100, Occurrence = occurrence, CombatTime = CombatElapsed });
    }
}
namespace Shikari.Tests
{
    public static class AdaptiveRuntimeTests
    {
        private static void Check(bool c, string message) { if (!c) throw new Exception(message); }
        public static void Run()
        {
            var plan = Plugin.Plans.Active;
            var rule = new AdaptiveMechanic { Enabled = true, TerritoryId = 1, AnchorActionId = 100 };
            rule.Branches.Add(new StatusBranch { StatusId = 10, MinimumSeconds = 20, MaximumSeconds = 40, SlideId = plan.Slides[0].Id });
            plan.AdaptiveMechanics.Add(rule);
            using var director = Plugin.Director = new SlideDirector();
            var service = new AdaptiveService();
            var navigations = 0;
            director.SlideRequested += (_, _) => navigations++;
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(0); Plugin.Framework.Tick(1); Plugin.Encounter.Cast();
            rule.Enabled = false; // Editing cannot change the captured rule mid-pull.
            Plugin.ObjectTable.LocalPlayer!.StatusList.Add(new FakeStatus());
            Plugin.Framework.Tick(2); Plugin.Framework.Tick(2.4f);
            Check(navigations == 1 && service.Decisions.Count == 1 && service.Decisions[0].Applied, "Frozen rule dispatches observed branch");
            Plugin.Reminders.Fire(new TimelineEntry { CastActionId = 100, Occurrence = 1, SlideId = plan.Slides[0].Id });
            Check(navigations == 1, "Generic delayed call cannot overwrite adaptive result");
            Plugin.Encounter.Cast(2);
            Plugin.Reminders.Fire(new TimelineEntry { CastActionId = 100, Occurrence = 0, SlideId = plan.Slides[0].Id });
            Check(navigations == 2, "Later cast occurrence can use a generic wildcard timeline step");
            navigations = 1;
            Plugin.Encounter.End(); rule.Enabled = true; Plugin.ObjectTable.LocalPlayer.StatusList.Clear();
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(0); Plugin.Framework.Tick(1); Plugin.Encounter.Cast();
            director.NotifyManualChange();
            Plugin.ObjectTable.LocalPlayer.StatusList.Add(new FakeStatus()); Plugin.Framework.Tick(2); Plugin.Framework.Tick(2.4f);
            Check(service.Decisions.Count == 1 && !service.Decisions[0].Applied && navigations == 1, "Manual hold recorded without navigation");
            Plugin.ClientState.Change();
            Check(service.Decisions.Count == 0 && service.Recent.Count == 0, "Zone change clears stale state");
            Plugin.ClientState.TerritoryType = 1;
            Plugin.Encounter.End(); Plugin.ObjectTable.LocalPlayer.StatusList.Clear();
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(0); Plugin.Framework.Tick(1); Plugin.Encounter.Cast();
            Plugin.ObjectTable.LocalPlayer.StatusList.Add(new FakeStatus()); Plugin.Framework.Tick(2);
            Plugin.Encounter.WipeOnly(); Plugin.Framework.Tick(2.4f);
            Check(service.Decisions.Count == 0, "Wipe without CombatEnded cancels pending evaluation");
            foreach (var gap in new[] { "missing", "replacement", "dead" })
            {
                Plugin.Encounter.End(); Plugin.ObjectTable.LocalPlayer = new FakePlayer();
                Plugin.Encounter.Begin(); Plugin.Framework.Tick(0); Plugin.Framework.Tick(1); Plugin.Encounter.Cast();
                Plugin.ObjectTable.LocalPlayer.StatusList.Add(new FakeStatus()); Plugin.Framework.Tick(2);
                if (gap == "missing") Plugin.ObjectTable.LocalPlayer = null;
                if (gap == "replacement") Plugin.ObjectTable.LocalPlayer = new FakePlayer { EntityId = 21 };
                if (gap == "dead") Plugin.ObjectTable.LocalPlayer!.IsDead = true;
                Plugin.Framework.Tick(2.4f);
                Check(service.Decisions.Count == 0, "A " + gap + " actor must invalidate pending assignment evidence");
                Plugin.ObjectTable.LocalPlayer = new FakePlayer(); Plugin.Framework.Tick(3);
                Plugin.ObjectTable.LocalPlayer.StatusList.Add(new FakeStatus()); Plugin.Framework.Tick(4); Plugin.Framework.Tick(4.4f);
                Check(service.Decisions.Count == 1 && service.Decisions[0].SlideId.Length > 0, "Fresh evidence after actor gap may complete the armed rule");
            }
            Plugin.Encounter.End(); Plugin.ObjectTable.LocalPlayer.StatusList.Clear();
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(0); Plugin.Framework.Tick(1); Plugin.Encounter.Cast();
            Plugin.Plans.Active = PlanDocument.CreateDefault(); Plugin.Framework.Tick(2);
            Check(service.Status.Contains("Plan changed"), "Plan replacement ends evaluation");
            service.Dispose();
            Plugin.Encounter.Begin(); Plugin.Framework.Tick(0);
            Check(service.Status.Contains("Plan changed"), "Dispose removes callbacks");
            Console.WriteLine("PASS: actual adaptive service and director with game stubs: frozen rules, routing, delayed calls, manual hold, territory reset, plan replacement, disposal");
        }
    }
}
