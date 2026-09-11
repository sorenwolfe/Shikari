using System;
using System.Collections.Generic;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Buddy;

namespace Shikari.Tests
{
    public static class BuddyReminderTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Run()
        {
            AutomaticCallsWaitForCombat();
            DisabledPendingCallsAreDiscarded();
            var plan = Plugin.Plans.Active;
            var team = Plugin.Config.GetActiveTeam();
            team.Channels = 0;
            var entry = new TimelineEntry { CallText = "Default call" };
            entry.SlotCallText[0] = "Use your mitigation";
            plan.Timeline.Add(entry);
            var cues = new BuddyCueEngine();
            cues.Update(new BuddyContext(true, true, 100, 42, false, true, true, plan, plan.Id), DateTime.UtcNow);
            using var reminders = new ReminderEngine();
            ActiveCall? delivered = null;
            reminders.CallDelivered += call =>
            {
                delivered = call;
                cues.Reminder(call.EntryId + call.FiredAtUtc.Ticks, call.Headline, call.ForLocalPlayer,
                    call.ScheduledAtUtc, call.ExpiresAtUtc, DateTime.UtcNow, call.FiredAtUtc);
            };
            reminders.FireNow(plan, entry);
            Check(delivered != null && delivered.PlanId == plan.Id && delivered.Headline == "Use your mitigation" &&
                cues.Presentation.Body == "Use your mitigation", "A real local delivery must reach the buddy without an overlay channel.");
            Check(reminders.ActiveCalls.Count == 0, "Enabling the buddy must not enable ordinary overlay banners.");
            cues.Clear(DateTime.UtcNow);
            var other = new TimelineEntry { Audience = CallAudience.AssignedOnly, CallText = "Other player's task" };
            reminders.FireNow(plan, other);
            Check(delivered != null && !delivered.ForLocalPlayer && !cues.Presentation.HasCue,
                "Test-forced other-player calls must retain audience identity and stay out of personal guidance.");
            reminders.Dispose();
            plan.Timeline.Clear();
            Plugin.Encounter.Begin();
            plan.Timeline.Add(new TimelineEntry { Trigger = TriggerKind.CombatTime, TimeSeconds = 10, LeadSeconds = 2, CallText = "Old mechanic" });
            using var lateReminders = new ReminderEngine();
            ActiveCall? lateCall = null;
            lateReminders.CallDelivered += call =>
            {
                lateCall = call;
                cues.Reminder(call.EntryId + call.FiredAtUtc.Ticks, call.Headline, call.ForLocalPlayer,
                    call.ScheduledAtUtc, call.ExpiresAtUtc, DateTime.UtcNow, call.FiredAtUtc);
            };
            Plugin.Config.RemindersEnabled = false;
            Plugin.Encounter.CombatElapsed = 9; Plugin.Framework.Tick();
            Plugin.Config.RemindersEnabled = true;
            Plugin.Encounter.CombatElapsed = 14; Plugin.Framework.Tick();
            Check(lateCall != null && (lateCall.FiredAtUtc - lateCall.ScheduledAtUtc).TotalSeconds >= 5,
                "Calls enabled after a due time must retain that actual due timestamp.");
            Check(!cues.Presentation.HasCue, "Overdue calls must not become fresh buddy instructions after Calls re-enables.");

            plan.Timeline.Clear();
            var castEntry = new TimelineEntry { CastActionId = 800, Trigger = TriggerKind.BossCast, LeadSeconds = 0, CallText = "Queued old board" };
            plan.Timeline.Add(castEntry);
            lateReminders.Dispose();
            using var queuedReminders = new ReminderEngine();
            var queuedDeliveries = 0;
            queuedReminders.CallDelivered += _ => queuedDeliveries++;
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
            Plugin.Plans.Active = new PlanDocument();
            Plugin.Plans.Active.Timeline.Add(new TimelineEntry { Id = castEntry.Id, CastActionId = 800 });
            Plugin.Framework.Tick();
            Check(queuedDeliveries == 0, "A queued cast must retain source identity and never relabel itself as a duplicate active plan.");
            queuedReminders.Dispose();
            Plugin.Plans.Active = plan;
            castEntry.Trigger = TriggerKind.AfterCast; castEntry.OffsetSeconds = 5;
            using var stalledReminders = new ReminderEngine();
            ActiveCall? stalled = null;
            stalledReminders.CallDelivered += call =>
            {
                stalled = call;
                cues.Reminder(call.EntryId + call.FiredAtUtc.Ticks, call.Headline, call.ForLocalPlayer,
                    call.ScheduledAtUtc, call.ExpiresAtUtc, DateTime.UtcNow, call.FiredAtUtc);
            };
            Plugin.Encounter.Cast(new CastEvent { ActionId = 800, Occurrence = 1, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-9) });
            Plugin.Framework.Tick();
            Check(stalled != null && (stalled.FiredAtUtc - stalled.ScheduledAtUtc).TotalSeconds >= 3 && !cues.Presentation.HasCue,
                "A stalled pending cast retains its old due time and cannot masquerade as a fresh cue.");
            stalledReminders.Dispose();
            plan.Timeline.Clear();
            plan.Timeline.Add(new TimelineEntry { Trigger = TriggerKind.CombatTime, TimeSeconds = 0, LeadSeconds = 5, CallText = "Opening mitigation" });
            using var openingReminders = new ReminderEngine();
            openingReminders.CallDelivered += call => cues.Reminder(call.EntryId + call.FiredAtUtc.Ticks, call.Headline, call.ForLocalPlayer,
                call.ScheduledAtUtc, call.ExpiresAtUtc, DateTime.UtcNow, call.FiredAtUtc);
            cues.Clear(DateTime.UtcNow);
            Plugin.Encounter.CombatElapsed = .05f; Plugin.Framework.Tick();
            Check(cues.Presentation.Body == "Opening mitigation", "A newly delivered time-zero call must survive the milliseconds between combat start and buddy initialization.");
            Console.WriteLine("Buddy real reminder delivery checks passed.");
        }

        private static void AutomaticCallsWaitForCombat()
        {
            var plan = Plugin.Plans.Active;
            var team = Plugin.Config.GetActiveTeam();
            team.Channels = ReminderChannel.Overlay;
            plan.Timeline.Clear();
            var opener = new TimelineEntry { Trigger = TriggerKind.CombatTime, TimeSeconds = 0, LeadSeconds = 5 };
            plan.Timeline.Add(opener);
            using var reminders = new ReminderEngine();
            var delivered = 0;
            reminders.CallDelivered += _ => delivered++;
            Plugin.Framework.Tick();
            Check(delivered == 0, "Automatic opening calls must wait for combat instead of firing while idle in a duty.");
            Plugin.Encounter.Begin();
            Plugin.Framework.Tick();
            Check(delivered == 1, "A time-zero opening call must still fire when the pull begins.");
            Plugin.Encounter.End();
            var addedAfterPull = new TimelineEntry { Trigger = TriggerKind.CombatTime, TimeSeconds = 0 };
            plan.Timeline.Add(addedAfterPull);
            Plugin.Framework.Tick();
            Check(delivered == 1, "Adding a time-zero entry after combat ends must not trigger a live call.");
            reminders.FireNow(plan, addedAfterPull);
            Check(delivered == 2 && reminders.ActiveCalls.Count > 0, "Manual Test calls must remain available outside combat.");
            foreach (var call in reminders.ActiveCalls) call.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            Plugin.Framework.Tick();
            Check(reminders.ActiveCalls.Count == 0, "Overlay calls must still expire outside combat.");
            Plugin.Encounter.Begin();
            Plugin.Framework.Tick();
            Check(delivered == 4, "A new pull must reset delivery history and fire both opening calls once.");
            Plugin.Encounter.End();
            plan.Timeline.Clear();
            team.Channels = 0;
        }

        private static void DisabledPendingCallsAreDiscarded()
        {
            var plan = Plugin.Plans.Active;
            plan.Timeline.Clear();
            var entry = new TimelineEntry { Trigger = TriggerKind.BossCast, CastActionId = 801, Occurrence = 0, LeadSeconds = 0 };
            plan.Timeline.Add(entry);
            using var reminders = new ReminderEngine();
            var delivered = 0;
            reminders.CallDelivered += _ => delivered++;
            Plugin.Encounter.Begin();
            Plugin.Encounter.Cast(new CastEvent { ActionId = 801, Occurrence = 1, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
            entry.Enabled = false;
            Plugin.Framework.Tick();
            Check(delivered == 0, "Disabling an entry after its cast is queued must prevent delivery.");
            entry.Enabled = true;
            Plugin.Framework.Tick();
            Check(delivered == 0, "Re-enabling a discarded entry must not revive the old pending call.");
            Plugin.Encounter.Cast(new CastEvent { ActionId = 801, Occurrence = 2, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
            Plugin.Framework.Tick();
            Check(delivered == 1, "A re-enabled entry must still deliver a new cast occurrence.");
            Plugin.Encounter.Cast(new CastEvent { ActionId = 801, Occurrence = 2, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
            Plugin.Framework.Tick();
            Check(delivered == 1, "Repeated scheduling of the same entry and occurrence must deliver only once.");
            Plugin.Encounter.Cast(new CastEvent { ActionId = 801, Occurrence = 3, TotalCastTime = 1, StartedAtUtc = DateTime.UtcNow.AddSeconds(-2) });
            Plugin.Framework.Tick();
            Check(delivered == 2, "Occurrence identity must allow later casts of the same entry to deliver.");
            Plugin.Encounter.End();
            plan.Timeline.Clear();
        }
    }
}
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
namespace Dalamud.Plugin.Services { public interface IFramework { } }
namespace Dalamud.Game.ClientState.Conditions { public enum ConditionFlag { BoundByDuty } }
namespace Dalamud.Interface.ImGuiNotification
{
    public enum NotificationType { Info }
    public sealed class Notification { public string Title { get; set; } = ""; public string Content { get; set; } = ""; public NotificationType Type { get; set; } public TimeSpan InitialDuration { get; set; } }
}
namespace FFXIVClientStructs.FFXIV.Client.UI { public static class UIGlobals { public static void PlayChatSoundEffect(uint id) { } } }
namespace Shikari
{
    internal static class Plugin
    {
        public static Configuration Config = new(); public static FakePlans Plans = new();
        public static FakeFramework Framework = new(); public static FakeEncounter Encounter = new();
        public static FakeCondition Condition = new(); public static FakeSpeech Speech = new(); public static FakeLearner Learner = new();
        public static FakeRoster Roster = new(); public static FakeActions Actions = new(); public static FakeChat ChatGui = new();
        public static FakeNotifications Notifications = new(); public static FakeLog Log = new();
    }
    internal sealed class FakePlans { public PlanDocument Active = PlanDocument.CreateDefault(); }
    internal sealed class FakeFramework : Dalamud.Plugin.Services.IFramework { public event Action<Dalamud.Plugin.Services.IFramework>? Update; public void Tick() => Update?.Invoke(this); }
    internal sealed class FakeEncounter
    {
        public bool InCombat;
        public float CombatElapsed; public event Action? CombatStarted; public event Action? CombatEnded; public event Action<CastEvent>? CastStarted;
        public void Begin() { InCombat = true; CombatElapsed = 0; CombatStarted?.Invoke(); }
        public void End() { InCombat = false; CombatElapsed = 0; CombatEnded?.Invoke(); }
        public void Cast(CastEvent value) => CastStarted?.Invoke(value); public int OccurrenceOf(uint action) => 0;
    }
    internal sealed class FakeCondition { public bool this[Dalamud.Game.ClientState.Conditions.ConditionFlag flag] => true; }
    internal sealed class FakeSpeech { public void Clear() { } public void Start() { } public void Say(string text) { } }
    internal sealed class FakeLearner { public bool TryPredict(uint action, int occurrence, out float time, out float confidence) { time = 0; confidence = 0; return false; } }
    internal sealed class FakeRoster { public int ResolveLocalSlot(PlanDocument plan) => 0; }
    internal sealed class FakeActions { public string NameOf(uint action, string fallback = "") => fallback; public string JobAbbreviation(uint job) => "JOB"; }
    internal sealed class FakeChat { public void Print(string line, string prefix, object? empty) { } }
    internal sealed class FakeNotifications { public void AddNotification(Dalamud.Interface.ImGuiNotification.Notification notification) { } }
    internal sealed class FakeLog { public void Error(Exception ex, string text) { throw new Exception(text, ex); } public void Warning(Exception ex, string text) { throw new Exception(text, ex); } }
}
namespace Shikari.Services
{
    public sealed class CastEvent { public uint ActionId { get; init; } public int Occurrence { get; init; } public float TotalCastTime { get; init; } public DateTime StartedAtUtc { get; init; } }
}
