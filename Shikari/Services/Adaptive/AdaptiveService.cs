using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.Adaptive;

/// <summary>Owns game observations on the framework thread. Rules are frozen per pull.</summary>
public sealed class AdaptiveService : IDisposable
{
    private readonly StatusTracker tracker = new();
    private AdaptiveEngine? engine;
    private PlanDocument? sourcePlan;
    private float nextScan;
    private uint actorId;
    private readonly List<StatusObservation> recent = new();
    private readonly List<AdaptiveDecision> decisions = new();
    public IReadOnlyList<StatusObservation> Recent => recent;
    public IReadOnlyList<AdaptiveDecision> Decisions => decisions;
    public string Status { get; private set; } = "Adaptive mechanics ready. Rules are captured at pull start.";
    public event Action<StatusObservation>? Observed;
    public event Action<AdaptiveDecision>? Decided;

    public AdaptiveService()
    {
        Plugin.Encounter.CombatStarted += Begin;
        Plugin.Encounter.CombatEnded += End;
        Plugin.Encounter.Wiped += End;
        Plugin.Encounter.CastStarted += Cast;
        Plugin.ClientState.TerritoryChanged += Territory;
        Plugin.Framework.Update += Update;
    }
    private void Begin()
    {
        tracker.Invalidate(); actorId = 0; nextScan = 0;
        recent.Clear(); decisions.Clear();
        sourcePlan = Plugin.Plans.Active;
        try
        {
            var frozen = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(sourcePlan, PlanJson.Compact()), PlanJson.Compact());
            engine = frozen == null ? null : new AdaptiveEngine(frozen, Plugin.ClientState.TerritoryType);
            Status = engine?.ActiveRuleCount > 0 ? "Waiting for a matching cast and a new status assignment." :
                "No valid, non-overlapping enabled rules for this territory. Capturing statuses for discovery.";
        }
        catch (Exception ex)
        {
            engine = null; Status = "Adaptive rules could not start for this pull.";
            Plugin.Log.Warning(ex, "Could not snapshot adaptive rules.");
        }
    }
    private void End() { engine = null; tracker.Invalidate(); }
    private void Territory(uint _) { End(); recent.Clear(); decisions.Clear(); Status = "Territory changed. Waiting for the next pull."; }
    private void Cast(CastEvent cast)
    {
        if (ReferenceEquals(sourcePlan, Plugin.Plans.Active))
            engine?.Arm(cast.ActionId, cast.Occurrence, cast.CombatTime);
    }
    private void Update(IFramework framework)
    {
        if (!Plugin.Encounter.InCombat || engine == null) return;
        if (!ReferenceEquals(sourcePlan, Plugin.Plans.Active))
        {
            End(); Status = "Plan changed. Adaptive rules resume next pull."; return;
        }
        var time = Plugin.Encounter.CombatElapsed;
        if (time < nextScan) return;
        nextScan = time + .1f;
        var observations = new List<StatusObservation>();
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null || player.IsDead) { tracker.Invalidate(); engine.InvalidateEvidence(); actorId = 0; }
            else
            {
                if (actorId != player.EntityId) { tracker.Invalidate(); engine.InvalidateEvidence(); actorId = player.EntityId; }
                var samples = player.StatusList.Select(s => new StatusSample(s.StatusId, s.RemainingTime, s.Param, s.SourceId)).ToArray();
                observations = tracker.Observe(samples, time);
            }
        }
        catch
        {
            tracker.Invalidate();
            engine.InvalidateEvidence();
            Status = "Status data unavailable; waiting for a fresh observation.";
        }
        foreach (var observation in observations)
        {
            recent.Add(observation);
            Observed?.Invoke(observation);
        }
        if (recent.Count > 256) recent.RemoveRange(0, recent.Count - 256);
        foreach (var decision in engine.Update(observations, time))
        {
            decision.Applied = decision.SlideId.Length > 0 && Plugin.Director.RequestAdaptive(decision.SlideId, decision.AnchorActionId, decision.Occurrence);
            decision.Navigation = decision.SlideId.Length == 0 ? "No destination" : decision.Applied ? "Slide selected" : "Navigation held by follow settings or manual override";
            decisions.Add(decision);
            if (decisions.Count > 256) decisions.RemoveAt(0);
            Status = decision.Mechanic + ": " + decision.Reason + " " + decision.Navigation;
            Decided?.Invoke(decision);
        }
    }
    public void Dispose()
    {
        Plugin.Encounter.CombatStarted -= Begin;
        Plugin.Encounter.CombatEnded -= End;
        Plugin.Encounter.Wiped -= End;
        Plugin.Encounter.CastStarted -= Cast;
        Plugin.ClientState.TerritoryChanged -= Territory;
        Plugin.Framework.Update -= Update;
        End();
    }
}
