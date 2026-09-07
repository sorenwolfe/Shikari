using System.Collections.Generic;

namespace Shikari.Services.FfLogs;

/// <summary>One fight in a report.</summary>
public sealed class LogFight
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long StartTime { get; init; }
    public long EndTime { get; init; }
    public bool Kill { get; init; }
    public float FightPercentage { get; init; }

    public float DurationSeconds => (EndTime - StartTime) / 1000f;

    public string Describe()
    {
        var outcome = Kill ? "kill" : $"{FightPercentage:0.#}%";
        return $"#{Id}  {Name}  ({CallTemplate.FormatTime(DurationSeconds)}, {outcome})";
    }
}

/// <summary>A participant in the report. For players, <see cref="Job"/> is their job.</summary>
public sealed class LogActor
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Job { get; init; } = string.Empty;

    public bool IsPlayer => Type.Equals("Player", System.StringComparison.OrdinalIgnoreCase);
}

/// <summary>One cast from the log, with times already made relative to the start of the fight.</summary>
public sealed class LogCast
{
    public int SourceId { get; init; }
    public uint AbilityId { get; init; }
    public string AbilityName { get; set; } = string.Empty;
    public float TimeSeconds { get; init; }

    /// <summary>Cast bar length, when the log had a begincast and a cast to pair up.</summary>
    public float CastSeconds { get; init; }

    /// <summary>A begincast was observed, including interrupted casts with no measured duration.</summary>
    public bool IsCastStart { get; init; }

    public bool FromEnemy { get; init; }
}

/// <summary>Everything pulled out of one fight, ready to be turned into a plan.</summary>
public sealed class LogFightData
{
    public string ReportCode { get; init; } = string.Empty;
    public LogFight Fight { get; init; } = new();
    public List<LogActor> Actors { get; init; } = new();
    public List<LogCast> EnemyCasts { get; init; } = new();
    public List<LogCast> PlayerCasts { get; init; } = new();
    public Dictionary<uint, string> AbilityNames { get; init; } = new();
}
