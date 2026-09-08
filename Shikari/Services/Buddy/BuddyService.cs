using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Buddy;

public sealed class BuddyService : IDisposable
{
    private readonly BuddyCueEngine cues = new();
    private readonly BuddyAmbientController ambient = new();
    private readonly List<AdaptiveMechanic> rules = new();
    private BuddyContext previous;
    private bool initialized;
    private bool combatSession;
    private bool disposed;
    private uint decidedAction;
    private int decidedOccurrence;

    public BuddyPresentation Presentation => cues.Presentation;
    public BuddyAmbientPresentation Ambient => ambient.Presentation;

    public BuddyService()
    {
        // Loading/enabling during a pull can consume new calls, but must not reconstruct an
        // assignment from decisions made before the buddy existed.
        combatSession = Plugin.Encounter.InCombat;
        Refresh();
        Plugin.Framework.Update += Update;
        Plugin.Encounter.CombatStarted += Begin;
        Plugin.Encounter.CombatEnded += End;
        Plugin.Encounter.Wiped += End;
        Plugin.Encounter.CastStarted += Cast;
        Plugin.ClientState.TerritoryChanged += Territory;
        Plugin.Reminders.CallDelivered += Call;
        Plugin.Adaptive.Decided += Decide;
        Plugin.Adaptive.EvidenceInvalidated += Invalidate;
    }

    private void Begin()
    {
        combatSession = true;
        Refresh();
        cues.Clear(DateTime.UtcNow);
        decidedAction = 0;
        rules.Clear();
        if (!Plugin.Config.BuddyEnabled || Plugin.Plans.Active is not { } plan) return;
        try
        {
            // Match AdaptiveService's pull snapshot. Mid-pull edits cannot advertise a rule
            // which the actual evaluator never armed.
            var frozen = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(plan, PlanJson.Compact()), PlanJson.Compact());
            if (frozen == null) return;
            var candidates = frozen.AdaptiveMechanics.Take(128)
                .Where(r => r != null && r.Enabled && r.TerritoryId == Plugin.ClientState.TerritoryType && r.IsValid(frozen)).ToList();
            rules.AddRange(candidates.Where(r => candidates.Count(other => r.Overlaps(other)) == 1));
        }
        catch (Exception ex)
        {
            rules.Clear();
            Plugin.Log.Warning(ex, "Buddy could not snapshot adaptive rules; assignment cues are paused for this pull.");
        }
    }

    private void End()
    {
        combatSession = false;
        decidedAction = 0;
        rules.Clear();
        Refresh();
        cues.Clear(DateTime.UtcNow);
    }

    private void Territory(uint _) => End();
    private void Invalidate() => cues.Clear(DateTime.UtcNow);
    private void Update(IFramework _) => Refresh();

    private void Refresh()
    {
        if (disposed) return;
        var now = DateTime.UtcNow;
        uint playerId = 0;
        var dead = false;
        var statusKnown = false;
        var away = false;
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            playerId = player?.EntityId ?? 0;
            dead = player?.IsDead ?? false;
            try
            {
                var status = player?.OnlineStatus.RowId ?? 0;
                // OnlineStatus row 17 is Away from Keyboard, verified against the local
                // 2026.08.11 game sheet. Zero/disconnected/offline rows provide no return signal.
                statusKnown = player != null && status > 0 && status is not (5 or 9 or 10);
                away = status == 17;
            }
            catch { /* Cosmetic status failure must not alter otherwise valid tactical cues. */ }
        }
        catch { /* Unreadable game objects invalidate every cue, not merely the status baseline. */ }
        var plan = Plugin.Plans.Active;
        var next = new BuddyContext(Plugin.Config.BuddyEnabled, combatSession && Plugin.Encounter.InCombat,
            Plugin.ClientState.TerritoryType, playerId, dead, Plugin.Config.RemindersEnabled,
            Plugin.Config.AutoAdvanceSlides && !Plugin.Director.IsSuppressed, plan, plan?.Id ?? "");
        if (initialized && (previous.Enabled != next.Enabled || previous.TerritoryId != next.TerritoryId ||
            !ReferenceEquals(previous.Plan, next.Plan) || previous.PlanId != next.PlanId || previous.PlayerId != next.PlayerId))
        {
            rules.Clear();
            decidedAction = 0;
        }
        previous = next;
        initialized = true;
        cues.Update(next, now);
        RefreshAmbient(now, playerId, dead, statusKnown, away);
    }

    private void RefreshAmbient(DateTime now, uint playerId, bool dead, bool statusKnown, bool away)
    {
        try
        {
            var suppressed = Plugin.ClientState.IsGPosing || Plugin.Condition[ConditionFlag.BetweenAreas] ||
                Plugin.Condition[ConditionFlag.BetweenAreas51] || Plugin.Condition[ConditionFlag.LoggingOut] ||
                Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];
            var duty = Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] ||
                Plugin.Condition[ConditionFlag.BoundByDuty95];
            ambient.Update(new BuddyAmbientContext(Plugin.Config.BuddyEnabled,
                Plugin.ClientState.IsLoggedIn ? playerId : 0, Plugin.ClientState.TerritoryType, statusKnown, away, duty,
                Plugin.Encounter.InCombat || Plugin.Condition[ConditionFlag.InCombat], dead, suppressed), now);
        }
        catch { ambient.Reset(now); }
    }

    private void Call(ActiveCall call)
    {
        Refresh();
        var plan = Plugin.Plans.Active;
        if (plan == null || !ReferenceEquals(call.SourcePlan, plan) || call.PlanId != plan.Id) return;
        var entry = plan.Timeline.FirstOrDefault(e => e.Id == call.EntryId && e.Enabled);
        if (entry == null) return;
        if (decidedAction != 0 && entry.CastActionId == decidedAction &&
            (entry.Occurrence == 0 || entry.Occurrence == decidedOccurrence)) return;
        cues.Reminder(call.EntryId + "/" + call.FiredAtUtc.Ticks, call.Headline, call.ForLocalPlayer,
            call.ScheduledAtUtc, call.ExpiresAtUtc, DateTime.UtcNow, call.FiredAtUtc);
    }

    private void Cast(CastEvent cast)
    {
        Refresh();
        if (cast.ActionId == decidedAction && cast.Occurrence != decidedOccurrence) decidedAction = 0;
        if (cast.ActionId == 0 || cast.Occurrence <= 0 || rules.Count == 0) return;
        var rule = rules.SingleOrDefault(r => r.AnchorActionId == cast.ActionId &&
            (r.Occurrence == 0 || r.Occurrence == cast.Occurrence));
        if (rule == null) return;
        // The evaluator polls at 10Hz; a small deadline grace accepts its genuine timeout
        // decision without keeping a waiting cue alive indefinitely.
        cues.Arm(Key(cast.ActionId, cast.Occurrence), rule.Label, cast.StartedAtUtc.AddSeconds(rule.WindowSeconds + .5), DateTime.UtcNow);
    }

    private void Decide(AdaptiveDecision decision)
    {
        Refresh();
        var plan = Plugin.Plans.Active;
        var slide = plan?.FindSlide(decision.SlideId);
        // Never turn Reason/guide prose into a made-up movement instruction. The destination
        // is the authored title or the unique frozen branch which selected that actual slide.
        var title = slide?.Title ?? "";
        string? detail = null;
        if (slide != null)
        {
            var branches = rules.Where(r => r.AnchorActionId == decision.AnchorActionId &&
                    (r.Occurrence == 0 || r.Occurrence == decision.Occurrence))
                .SelectMany(r => r.Branches).Where(b => b.SlideId == decision.SlideId).ToArray();
            if (branches.Length == 1 && !string.IsNullOrWhiteSpace(branches[0].Label) &&
                !branches[0].Label.Trim().Equals("New branch", StringComparison.OrdinalIgnoreCase))
            {
                title = branches[0].Label;
                detail = "Board: " + slide.Title;
            }
        }
        cues.Decide(Key(decision.AnchorActionId, decision.Occurrence),
            title, decision.Applied && slide != null, DateTime.UtcNow, detail);
        var key = Key(decision.AnchorActionId, decision.Occurrence);
        if (cues.Presentation.HasCue && (cues.Presentation.Key == "assignment/" + key || cues.Presentation.Key == "unknown/" + key))
        {
            decidedAction = decision.AnchorActionId;
            decidedOccurrence = decision.Occurrence;
        }
    }

    private static string Key(uint action, int occurrence) => action + "/" + occurrence;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.Framework.Update -= Update;
        Plugin.Encounter.CombatStarted -= Begin;
        Plugin.Encounter.CombatEnded -= End;
        Plugin.Encounter.Wiped -= End;
        Plugin.Encounter.CastStarted -= Cast;
        Plugin.ClientState.TerritoryChanged -= Territory;
        Plugin.Reminders.CallDelivered -= Call;
        Plugin.Adaptive.Decided -= Decide;
        Plugin.Adaptive.EvidenceInvalidated -= Invalidate;
        rules.Clear();
        cues.Clear(DateTime.UtcNow);
        ambient.Reset(DateTime.UtcNow);
    }
}
