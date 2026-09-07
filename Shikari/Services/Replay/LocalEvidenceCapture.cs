using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace Shikari.Services.Replay;

/// <summary>Evidence collection does not depend on having a calibrated board or a linked slide.</summary>
public sealed class LocalEvidenceCapture
{
    private readonly EvidenceRecorder recorder = new();
    public void Capture(ReplayAttempt attempt, float time)
    {
        var evidence = attempt.Evidence;
        var seen = new HashSet<long>();
        var local = Plugin.ObjectTable.LocalPlayer;
        var members = new List<IBattleChara>();
        foreach (var member in Plugin.PartyList.Take(8))
            if (member.GameObject is IBattleChara actor) members.Add(actor);
        if (local != null && members.All(p => p.EntityId != local.EntityId)) members.Add(local);
        foreach (var actor in members.Take(8))
        {
            var id = (long)actor.EntityId;
            if (!seen.Add(id)) continue;
            var entry = evidence.Actors.FirstOrDefault(p => p.Id == id);
            if (entry == null)
            {
                if (evidence.Actors.Count >= 32) continue;
                var isLocal = actor.EntityId == local?.EntityId;
                entry = new EvidenceActor { Id = id, Name = actor.Name.TextValue, JobId = actor.ClassJob.RowId,
                    IsLocal = isLocal, SlotIndex = RosterResolver.MatchSeat(attempt.Plan.Roster, actor.Name.TextValue,
                        actor.ClassJob.RowId, isLocal ? attempt.LocalSlot : -1) };
                evidence.Actors.Add(entry);
            }
            var pos = actor.Position;
            if (evidence.Positions.Count < ReplayEvidence.MaxPositions && float.IsFinite(pos.X) && float.IsFinite(pos.Z))
                evidence.Positions.Add(new EvidencePosition { ActorId = id, Time = time, Position = new Vector2(pos.X, pos.Z) });
            recorder.Observe(evidence, id, time, actor.CurrentHp == 0 ? null : actor.StatusList
                .Select(s => new EvidenceStatusSample(s.StatusId, s.RemainingTime, s.Param, s.SourceId)).ToArray());
        }
        foreach (var actor in evidence.Actors.Where(a => !seen.Contains(a.Id))) recorder.Observe(evidence, actor.Id, time, null);
        foreach (var duplicate in evidence.Actors.Where(a => a.SlotIndex >= 0).GroupBy(a => a.SlotIndex).Where(g => g.Count() > 1))
            foreach (var actor in duplicate.Where(a => !a.IsLocal)) actor.SlotIndex = -1;
    }
    public void Invalidate(ReplayAttempt attempt, float time)
    {
        foreach (var actor in attempt.Evidence.Actors) recorder.Observe(attempt.Evidence, actor.Id, time, null);
    }
}
