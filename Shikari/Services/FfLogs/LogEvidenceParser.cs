using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Shikari.Services.FfLogs;

/// <summary>Normalizes FF Logs JSON without turning absent fields into measured zero values.</summary>
public sealed class LogEvidenceParser
{
    private readonly LogFight fight;
    private readonly IReadOnlyDictionary<uint, string> abilityNames;
    private readonly HashSet<(float Time, int Actor, float X, float Y)> positions = new();
    public LogEvidence Result { get; } = new();

    public LogEvidenceParser(LogFight fight, IReadOnlyDictionary<uint, string>? abilityNames = null)
    {
        this.fight = fight;
        this.abilityNames = abilityNames ?? new Dictionary<uint, string>();
    }

    public void Warn(string warning, bool incomplete = false)
    {
        if (!Result.Warnings.Contains(warning)) Result.Warnings.Add(warning);
        if (incomplete) Result.Complete = false;
    }

    public void AddPage(JArray rows, CancellationToken cancel = default)
    {
        foreach (var token in rows)
        {
            cancel.ThrowIfCancellationRequested();
            if (token is not JObject row || !Number(row["timestamp"], out var timestamp))
            {
                Warn("Some events had invalid timestamps and were omitted.", true);
                continue;
            }
            if (timestamp < fight.StartTime || timestamp > fight.EndTime) continue;
            var time = (float)((timestamp - fight.StartTime) / 1000d);
            AddPosition(row, "source", time);
            AddPosition(row, "target", time);
            var type = row["type"]?.Type == JTokenType.String ? row.Value<string>("type") : null;
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
