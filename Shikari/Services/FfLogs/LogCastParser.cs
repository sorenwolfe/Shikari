using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Shikari.Services.FfLogs;

/// <summary>Pairs explicit observations without treating absent NPC instances as instance one.</summary>
internal sealed class LogCastParser
{
    private sealed record Observation(double Timestamp, bool Start, int Source, int? Instance,
        int? Target, int? TargetInstance, uint Ability);
    private readonly List<Observation> observations = new();
    private readonly LogFight fight;
    private readonly bool enemy;
    private readonly HashSet<int> singleInstances;

    public LogCastParser(LogFight fight, bool enemy, IReadOnlySet<int> singleInstances)
    { this.fight = fight; this.enemy = enemy; this.singleInstances = new(singleInstances); }

    public void Add(JArray rows)
    {
        foreach (var token in rows)
        {
            if (token is not JObject row) throw Invalid();
            var type = row["type"]?.Type == JTokenType.String ? row.Value<string>("type") : null;
            if (type is not ("begincast" or "cast")) continue;
            if (!LogEvidenceParser.Number(row["timestamp"], out var timestamp)) throw Invalid();
            if (timestamp < fight.StartTime || timestamp > fight.EndTime) continue;
            var source = Required(row["sourceID"], int.MaxValue);
            var ability = Required(row["abilityGameID"], uint.MaxValue);
            var instance = Optional(row["sourceInstance"]);
            var target = Target(row["targetID"]);
            var targetInstance = Optional(row["targetInstance"]);
            if (target == null && targetInstance != null) throw Invalid();
            observations.Add(new(timestamp, type == "begincast", (int)source, instance, target, targetInstance, (uint)ability));
        }
    }

    public List<LogCast> Finish()
    {
        // Contradictory event identities override supposedly single-instance metadata.
        foreach (var actor in observations.Where(o => o.Instance != null).GroupBy(o => o.Source))
            if (actor.Select(o => o.Instance).Distinct().Take(2).Count() > 1) singleInstances.Remove(actor.Key);
        var results = new List<LogCast>();
        var pending = new Dictionary<(int Source, uint Ability), List<(Observation Observation, int Index)>>();
        var ambiguous = new HashSet<(int Source, uint Ability)>();
        foreach (var row in observations.OrderBy(o => o.Timestamp))
        {
            var key = (row.Source, row.Ability);
            if (row.Start)
            {
                if (ambiguous.Contains(key)) { results.Add(Capture(row)); continue; }
                if (!pending.TryGetValue(key, out var starts)) pending[key] = starts = new();
                starts.Add((row, results.Count));
                results.Add(Capture(row));
                continue;
            }
            var paired = false;
            if (pending.TryGetValue(key, out var candidates))
            {
                // Unknown instances are possible matches, never evidence that two NPCs are equal.
                var possible = candidates.Where(c => c.Observation.Instance == null || row.Instance == null ||
                    c.Observation.Instance == row.Instance).ToArray();
                if (possible.Length == 1 && (row.Instance != null && possible[0].Observation.Instance == row.Instance ||
                    singleInstances.Contains(row.Source)))
                {
                    var match = possible[0];
                    results[match.Index] = Capture(match.Observation, row);
                    paired = true;
                }
                // The log cannot prove whether an unresolved older start was interrupted or will
                // complete later. Do not let a new start turn that uncertainty into a guessed pair.
                if (!paired && possible.Length != 0)
                {
                    ambiguous.Add(key);
                    candidates.Clear();
                }
                else candidates.RemoveAll(c => c.Observation.Instance == null || row.Instance == null || c.Observation.Instance == row.Instance);
                if (candidates.Count == 0) pending.Remove(key);
            }
            if (!paired) results.Add(Capture(row));
        }
        return results.OrderBy(c => c.TimeSeconds).ToList();
    }

    private LogCast Capture(Observation row, Observation? completion = null)
    {
        completion ??= row.Start ? null : row;
        return new LogCast {
            SourceId = row.Source, SourceInstance = row.Instance, TargetId = row.Target, TargetInstance = row.TargetInstance,
            CompletionSourceInstance = completion?.Instance, CompletionTargetId = completion?.Target,
            CompletionTargetInstance = completion?.TargetInstance, AbilityId = row.Ability, FromEnemy = enemy,
            TimeSeconds = (float)((row.Timestamp - fight.StartTime) / 1000), IsCastStart = row.Start,
            CompletionTimeSeconds = completion == null ? null : (float)((completion.Timestamp - fight.StartTime) / 1000),
            CastSeconds = completion == null || !row.Start ? 0 : (float)((completion.Timestamp - row.Timestamp) / 1000),
        };
    }

    private static long Required(JToken? token, long maximum)
    {
        if (token?.Type != JTokenType.Integer || !long.TryParse(token.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) || value <= 0 || value > maximum) throw Invalid();
        return value;
    }
    private static int? Optional(JToken? token) => token == null || token.Type == JTokenType.Null ? null : (int)Required(token, int.MaxValue);
    private static int? Target(JToken? token) => token?.Type == JTokenType.Integer &&
        long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is 0 or -1 ? null : Optional(token);
    private static FfLogsException Invalid() => new("FF Logs returned malformed cast identity or timing. Retry the import; incomplete cast history cannot establish occurrences.");
}
