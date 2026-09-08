using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Shikari.Model;
using Shikari.Services.Speech;

namespace Shikari.Services;

/// <summary>A call that is currently on screen.</summary>
public sealed class ActiveCall
{
    public PlanDocument? SourcePlan { get; init; }
    public string PlanId { get; init; } = string.Empty;
    public string EntryId { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public string SubLine { get; init; } = string.Empty;
    public bool ForLocalPlayer { get; init; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime FiredAtUtc { get; init; }
    /// <summary>Intended delivery time; consumers can reject overdue calls after a pause.</summary>
    public DateTime ScheduledAtUtc { get; init; }
    public uint AccentColor { get; init; }
}

/// <summary>
/// Fires timeline steps at the right moment. Cast-anchored steps are scheduled the instant the
/// boss starts casting, so lead time is measured against the real cast bar.
/// </summary>
public sealed class ReminderEngine : IDisposable
{
    private sealed class PendingCall
    {
        public required PlanDocument Plan;
        public required string PlanId;
        public required TimelineEntry Entry;
        public DateTime FireAtUtc;
        public int Occurrence;
    }

    private readonly List<PendingCall> pending = new();
    private readonly HashSet<string> fired = new();
    private readonly List<ActiveCall> active = new();

    public ReminderEngine()
    {
        Plugin.Encounter.CastStarted += OnCastStarted;
        Plugin.Encounter.CombatStarted += OnCombatStarted;
        Plugin.Encounter.CombatEnded += OnCombatEnded;
        Plugin.Framework.Update += OnUpdate;
    }

    /// <summary>Calls that should currently be shown by the overlay, newest first.</summary>
    public IReadOnlyList<ActiveCall> ActiveCalls => active;

    /// <summary>Raised when a step delivers its call. Test calls raise it too.</summary>
    public event Action<TimelineEntry>? StepFired;

    /// <summary>A newly formatted delivery, including when the ordinary overlay is disabled.</summary>
    public event Action<ActiveCall>? CallDelivered;

    public void ClearActive() => active.Clear();

    private void OnCombatStarted()
    {
        pending.Clear();
        fired.Clear();
        active.Clear();
    }

    private void OnCombatEnded()
    {
        pending.Clear();

        // Anything still queued to be said belongs to a pull that is over.
        Plugin.Speech.Clear();
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var now = DateTime.UtcNow;

            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].ExpiresAtUtc <= now)
                    active.RemoveAt(i);
            }

            if (!ShouldRun())
                return;

            var plan = Plugin.Plans.Active;
            if (plan == null)
                return;

            // Steps measured from the pull.
            var elapsed = Plugin.Encounter.CombatElapsed;
            var lead = Plugin.Config.GetActiveTeam().LeadTimeAdjust;
            foreach (var entry in plan.Timeline)
            {
                if (!entry.Enabled || entry.Trigger != TriggerKind.CombatTime)
                    continue;

                var key = Key(entry, 1);
                if (fired.Contains(key))
                    continue;

                if (elapsed >= entry.TimeSeconds - entry.LeadSeconds - lead)
                {
                    fired.Add(key);
                    Fire(plan, entry, scheduledAtUtc: now.AddSeconds(Math.Max(0, entry.TimeSeconds - entry.LeadSeconds - lead) - elapsed));
                }
            }

            // Steps that fire on a learned timing, corrected for how this pull is running.
            foreach (var entry in plan.Timeline)
            {
                if (!entry.Enabled || entry.Trigger != TriggerKind.Predicted)
                    continue;
                if (entry.CastActionId == 0 || entry.Occurrence <= 0)
                    continue;

                var key = Key(entry, entry.Occurrence);
                if (fired.Contains(key))
                    continue;

                // If the cast has already happened, the cast-anchored path below owns this step.
                if (Plugin.Encounter.OccurrenceOf(entry.CastActionId) >= entry.Occurrence)
                    continue;

                if (!Plugin.Learner.TryPredict(entry.CastActionId, entry.Occurrence, out var expected, out var confidence))
                    continue;

                if (confidence < Plugin.Config.MinimumPredictionConfidence)
                    continue;

                if (TimelinePrediction.IsDue(elapsed, expected, entry.LeadSeconds + lead))
                {
                    fired.Add(key);
                    Fire(plan, entry, scheduledAtUtc: now.AddSeconds(Math.Max(0, expected - entry.LeadSeconds - lead) - elapsed));
                }
            }

            // Steps scheduled against a real cast bar.
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].FireAtUtc > now)
                    continue;

                var call = pending[i];
                pending.RemoveAt(i);

                if (!ReferenceEquals(call.Plan, plan) || call.PlanId != plan.Id || !plan.Timeline.Contains(call.Entry))
                    continue;

                var key = Key(call.Entry, call.Occurrence);
                if (fired.Contains(key))
                    continue;

                fired.Add(key);
                Fire(call.Plan, call.Entry, scheduledAtUtc: call.FireAtUtc);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ReminderEngine update failed.");
        }
    }

    private void OnCastStarted(CastEvent evt)
    {
        if (!ShouldRun())
            return;

        var plan = Plugin.Plans.Active;
        if (plan == null)
            return;

        var team = Plugin.Config.GetActiveTeam();

        foreach (var entry in plan.Timeline)
        {
            if (!entry.Enabled)
                continue;
            if (entry.CastActionId != evt.ActionId)
                continue;
            if (entry.Occurrence > 0 && entry.Occurrence != evt.Occurrence)
                continue;

            var lead = entry.LeadSeconds + team.LeadTimeAdjust;

            double delay = entry.Trigger switch
            {
                // Warn during the cast bar itself.
                TriggerKind.BossCast => evt.TotalCastTime - lead,

                // Warn ahead of something that happens a while after this cast begins.
                TriggerKind.AfterCast => entry.OffsetSeconds - lead,

                // Prediction never fired, so fall back to the bar. Never worse than a cast step.
                TriggerKind.Predicted => evt.TotalCastTime - lead,

                _ => double.NaN,
            };

            if (double.IsNaN(delay))
                continue;

            if (delay < 0)
                delay = 0;

            pending.Add(new PendingCall
            {
                Plan = plan,
                PlanId = plan.Id,
                Entry = entry,
                Occurrence = evt.Occurrence,
                FireAtUtc = evt.StartedAtUtc.AddSeconds(delay),
            });
        }
    }

    private bool ShouldRun()
    {
        if (!Plugin.Config.RemindersEnabled)
            return false;

        var team = Plugin.Config.GetActiveTeam();
        if (team.OnlyInDuty && !Plugin.Condition[ConditionFlag.BoundByDuty])
            return false;

        // Speech has to be brought up by whoever is going to use it. It used to be started by the
        // settings panel alone, which meant it worked while you were setting it up and was silent
        // in every session afterwards where you never opened settings — the queue was perfect and
        // nothing was ever put in it. Starting is idempotent and returns immediately.
        if ((team.Channels & ReminderChannel.Speech) != 0)
            Plugin.Speech.Start();

        return true;
    }

    /// <summary>Delivers a step's call now, ignoring triggers. Used by the Test button.</summary>
    public void FireNow(PlanDocument plan, TimelineEntry entry) => Fire(plan, entry, test: true);

    private void Fire(PlanDocument plan, TimelineEntry entry, bool test = false, DateTime? scheduledAtUtc = null)
    {
        var team = Plugin.Config.GetActiveTeam();
        var slot = Plugin.Roster.ResolveLocalSlot(plan);

        var addressesMe = entry.Audience == CallAudience.Everyone || (slot >= 0 && entry.HasAnythingFor(slot));

        // A test always shows, otherwise pressing it and seeing nothing reads as a bug.
        if (!addressesMe && !team.ShowOtherPlayersCalls && !test)
            return;

        var headline = CallTemplate.Resolve(plan, entry, slot, team);
        if (string.IsNullOrWhiteSpace(headline))
            headline = entry.Label;

        var subline = BuildSubLine(plan, entry, slot);

        var accent = 0xFFFFFFFFu;
        if (slot >= 0 && slot < plan.Roster.Count)
        {
            var s = plan.Roster[slot];
            accent = s.Color != 0 ? s.Color : RoleColors.Default(s.Role);
        }

        var now = DateTime.UtcNow;

        var delivery = new ActiveCall
        {
            SourcePlan = plan,
            PlanId = plan.Id,
            EntryId = entry.Id,
            Headline = headline,
            SubLine = subline,
            ForLocalPlayer = addressesMe,
            FiredAtUtc = now,
            ScheduledAtUtc = scheduledAtUtc ?? now,
            ExpiresAtUtc = now.AddSeconds(Math.Max(0.5f, team.OverlayHoldSeconds)),
            AccentColor = accent,
        };

        if ((team.Channels & ReminderChannel.Overlay) != 0)
        {
            active.Insert(0, delivery);

            while (active.Count > 4)
                active.RemoveAt(active.Count - 1);
        }

        if ((team.Channels & ReminderChannel.Chat) != 0)
        {
            var line = string.IsNullOrWhiteSpace(subline) ? headline : $"{headline}  —  {subline}";
            Plugin.ChatGui.Print(line, string.IsNullOrWhiteSpace(team.ChatPrefix) ? "Shikari" : team.ChatPrefix, null);
        }

        if ((team.Channels & ReminderChannel.Toast) != 0)
        {
            Plugin.Notifications.AddNotification(new Notification
            {
                Title = entry.Label,
                Content = headline,
                Type = NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(Math.Max(1f, team.OverlayHoldSeconds)),
            });
        }

        if ((team.Channels & ReminderChannel.Sound) != 0)
        {
            PlaySound(team.SoundEffectId);
        }

        if ((team.Channels & ReminderChannel.Speech) != 0)
        {
            Plugin.Speech.Say(SpokenText.For(headline, subline, team.SpeakOtherPlayersCalls));
        }

        StepFired?.Invoke(entry);
        CallDelivered?.Invoke(delivery);
    }

    private static string BuildSubLine(PlanDocument plan, TimelineEntry entry, int slot)
    {
        var parts = new List<string>();

        foreach (var assignment in entry.Assignments.OrderBy(a => a.SlotIndex))
        {
            if (assignment.SlotIndex < 0 || assignment.SlotIndex >= plan.Roster.Count)
                continue;
            if (slot >= 0 && assignment.SlotIndex == slot)
                continue;

            var seat = plan.Roster[assignment.SlotIndex].DisplayName;
            var name = Plugin.Actions.NameOf(assignment.ActionId, assignment.ActionName);
            parts.Add($"{seat}: {name}");
        }

        return string.Join("   ", parts.Take(6));
    }

    private static void PlaySound(uint id)
    {
        try
        {
            // Chat sound effects are 1-16 in the game's own numbering.
            UIGlobals.PlayChatSoundEffect(Math.Clamp(id, 1u, 16u));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not play the reminder sound.");
        }
    }

    private static string Key(TimelineEntry entry, int occurrence) => entry.Id + "#" + occurrence;

    public void Dispose()
    {
        Plugin.Encounter.CastStarted -= OnCastStarted;
        Plugin.Encounter.CombatStarted -= OnCombatStarted;
        Plugin.Encounter.CombatEnded -= OnCombatEnded;
        Plugin.Framework.Update -= OnUpdate;
    }
}
