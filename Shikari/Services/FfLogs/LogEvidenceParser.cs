using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Shikari.Services.FfLogs;

/// <summary>Normalizes FF Logs JSON without turning absent fields into measured zero values.</summary>
public sealed class LogEvidenceParser
{
    private const int MaxEffects = 32768;
    private readonly LogFight fight;
    private readonly IReadOnlyDictionary<uint, string> abilityNames;
    private readonly HashSet<int>? playerTargets;
    private readonly HashSet<(float Time, int Actor, float X, float Y)> positions = new();
    public LogEvidence Result { get; }

    public LogEvidenceParser(LogFight fight, IReadOnlyDictionary<uint, string>? abilityNames = null,
        IReadOnlySet<int>? playerTargetIds = null)
    {
        this.fight = fight;
        this.abilityNames = abilityNames ?? new Dictionary<uint, string>();
        if (playerTargetIds?.Any(id => id <= 0) == true) throw new ArgumentException("Invalid player target identity.", nameof(playerTargetIds));
        playerTargets = playerTargetIds == null ? null : new HashSet<int>(playerTargetIds);
        Result = new LogEvidence { EffectsComplete = true,
            EffectTargetActorIds = playerTargets == null ? null : Array.AsReadOnly(playerTargets.OrderBy(id => id).ToArray()) };
    }

    public void Warn(string warning, bool incomplete = false)
    {
        if (!Result.Warnings.Contains(warning)) Result.Warnings.Add(warning);
        if (incomplete) { Result.Complete = false; Result.EffectsComplete = false; }
    }

    public void AddPage(JArray rows, CancellationToken cancel = default)
    {
        foreach (var token in rows)
        {
            cancel.ThrowIfCancellationRequested();
            var effectRow = token as JObject;
            var eventType = effectRow?["type"]?.Type == JTokenType.String ? effectRow.Value<string>("type") : null;
            var effectTarget = ExactInteger(effectRow?["targetID"], 1, int.MaxValue);
            var irrelevantEffect = eventType is "calculateddamage" or "damage" && playerTargets != null &&
                effectTarget.HasValue && !playerTargets.Contains((int)effectTarget.Value);
            if (token is not JObject row || !Number(row["timestamp"], out var timestamp))
            {
                if (irrelevantEffect) Warn("Some outgoing damage positions had invalid timestamps and were omitted.");
                else Warn("Some events had invalid timestamps and were omitted.", true);
                continue;
            }
            if (timestamp < fight.StartTime || timestamp > fight.EndTime) continue;
            var time = (float)((timestamp - fight.StartTime) / 1000d);
            AddPosition(row, "source", time);
            AddPosition(row, "target", time);
            var type = row["type"]?.Type == JTokenType.String ? row.Value<string>("type") : null;
            if (type is "calculateddamage" or "damage" && !irrelevantEffect) AddEffect(row, time, type);
            if (type == "combatantinfo")
            {
                var actor = Integer(row["sourceID"], 1, int.MaxValue);
                if (actor == null || row["auras"] is not JArray auras) continue;
                foreach (var aura in auras)
                    if (aura is JObject status)
                        AddStatus(status, time, LogStatusChange.Baseline, (int)actor.Value, true);
                continue;
            }
            LogStatusChange? change = type switch
            {
                "applybuff" or "applydebuff" => LogStatusChange.Apply,
                "refreshbuff" or "refreshdebuff" => LogStatusChange.Refresh,
                "removebuff" or "removedebuff" => LogStatusChange.Remove,
                "applybuffstack" or "applydebuffstack" or "removebuffstack" or "removedebuffstack" => LogStatusChange.Stacks,
                _ => null,
            };
            if (change.HasValue) AddStatus(row, time, change.Value, null, false);
        }
    }

    private void AddEffect(JObject row, float time, string type)
    {
        var target = ExactInteger(row["targetID"], 1, int.MaxValue);
        if (target == null)
        {
            EffectWarning("Some typed damage observations had invalid target IDs and could not be scoped to players.");
            return;
        }
        if (Result.Effects.Count >= MaxEffects)
        {
            EffectWarning("Typed damage observations exceeded the recording limit; the effect channel is incomplete.");
            return;
        }
        var action = ExactInteger(row["abilityGameID"], 1, uint.MaxValue);
        var source = ExactInteger(row["sourceID"], 1, long.MaxValue);
        if (action == null || source == null || target == null)
        {
            EffectWarning("Some typed damage observations had invalid ability or actor IDs and were omitted.");
            return;
        }
        var actionId = (uint)action.Value;
        var name = row["name"]?.Type == JTokenType.String ? row.Value<string>("name") : null;
        if (string.IsNullOrEmpty(name)) abilityNames.TryGetValue(actionId, out name);
        Result.Effects.Add(new LogEffectEvent
        {
            Time = time, ActionId = actionId, Name = name ?? "", Type = type,
            SourceId = source.Value, TargetId = target.Value,
            SourceInstance = (int?)OptionalInteger(row["sourceInstance"], 1, int.MaxValue),
            TargetInstance = (int?)OptionalInteger(row["targetInstance"], 1, int.MaxValue),
            PacketId = OptionalInteger(row["packetID"], 0, long.MaxValue),
            SourcePosition = EffectPosition(row["sourceResources"]),
            TargetPosition = EffectPosition(row["targetResources"]),
        });
    }

    private long? OptionalInteger(JToken? token, long min, long max)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var value = ExactInteger(token, min, max);
        if (value == null) EffectWarning("Some typed damage observations had invalid instance or packet IDs; those fields are unknown.");
        return value;
    }

    private Vector2? EffectPosition(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JObject resources)
        {
            // Resource snapshots may legitimately have no position component.
            if (resources["x"] == null && resources["y"] == null) return null;
            if (Number(resources["x"], out var x) && Number(resources["y"], out var y) &&
                Math.Abs(x) <= float.MaxValue && Math.Abs(y) <= float.MaxValue)
                return new Vector2((float)x, (float)y);
        }
        EffectWarning("Some typed damage observations had invalid position resources; those positions are unknown.");
        return null;
    }

    private void EffectWarning(string message)
    {
        Result.EffectsComplete = false;
        Warn(message);
    }

    // Int64 packet identity must never pass through double, which loses integers above 2^53.
    private static long? ExactInteger(JToken? token, long min, long max) =>
        token != null && token.Type is JTokenType.Integer or JTokenType.Float &&
        decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        value >= min && value <= max && value == decimal.Truncate(value) ? (long)value : null;

    private void AddStatus(JObject row, float time, LogStatusChange change, int? target, bool baseline)
    {
        var ability = Integer(row[baseline ? "ability" : "abilityGameID"], 1, uint.MaxValue);
        var targetId = target ?? Integer(row["targetID"], 1, int.MaxValue);
        if (ability == null || targetId == null)
        {
            Warn("Some status events had invalid ability or actor IDs and were omitted.", true);
            return;
        }
        var abilityId = (uint)ability.Value;
        // The report's aura namespace is 1,000,000 + the game status ID. This only decodes
        // a candidate; the importer must validate the row against the installed game data.
        var statusId = abilityId > 1000000 && abilityId <= 1000000 + ushort.MaxValue ? abilityId - 1000000 : 0;
        if (statusId == 0) Warn("Some log aura IDs cannot be decoded to game status IDs.");
        var sourceToken = row[baseline ? "source" : "sourceID"];
        var source = Integer(sourceToken, -1, int.MaxValue);
        if (source == null && sourceToken != null && sourceToken.Type != JTokenType.Null)
            Warn("Some status source IDs were invalid and are unknown.", true);
        float? duration = null;
        if (!baseline && Number(row["duration"], out var milliseconds) && milliseconds >= 0 && milliseconds / 1000 <= float.MaxValue)
            duration = (float)(milliseconds / 1000);
        var name = row["name"]?.Type == JTokenType.String ? row.Value<string>("name") : null;
        if (string.IsNullOrEmpty(name)) abilityNames.TryGetValue(abilityId, out name);
        Result.StatusEvents.Add(new LogStatusEvent
        {
            Time = time, SourceId = (int)(source ?? 0), TargetId = (int)targetId.Value,
            AbilityId = abilityId, StatusId = statusId, Name = name ?? string.Empty, Change = change,
            Duration = duration,
            Stacks = (int?)Integer(row[baseline ? "stacks" : "stack"], 0, int.MaxValue),
            ExtraInfo = (int?)Integer(row["extraInfo"], 0, int.MaxValue),
        });
    }

    private void AddPosition(JObject row, string side, float time)
    {
        var actor = Integer(row[side + "ID"], 1, int.MaxValue);
        if (actor == null || row[side + "Resources"] is not JObject resources) return;
        if (!Number(resources["x"], out var x) || !Number(resources["y"], out var y)
            || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue) return;
        var point = (time, (int)actor.Value, (float)x, (float)y);
        if (positions.Add(point)) Result.Positions.Add(new LogPosition { Time = time, ActorId = point.Item2, X = point.Item3, Y = point.Item4 });
    }

    internal static bool Number(JToken? token, out double value)
    {
        value = 0;
        return token != null && token.Type is JTokenType.Integer or JTokenType.Float
            && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    internal static long? Integer(JToken? token, long min, long max) =>
        Number(token, out var value) && value >= min && value <= max && value == Math.Truncate(value) ? (long)value : null;
}
