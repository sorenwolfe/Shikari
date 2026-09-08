using System;
using System.Collections.Generic;
using System.IO;
using Shikari.Model;

namespace Shikari.Services.RaidPlanIo;

/// <summary>What a token's artwork says about who is standing there.</summary>
public readonly record struct TokenIdentity(RaidRole Role, uint JobId)
{
    public bool KnowsJob => JobId != 0;

    public bool KnowsAnything => Role != RaidRole.Unknown || JobId != 0;
}

/// <summary>
/// Reads the role or job out of a raidplan.io token's image path, which looks like
/// <c>game/ffxiv/job/role_healer.png</c> for a role and <c>game/ffxiv/job/whm.png</c> for a job.
/// </summary>
/// <remarks>
/// Without this an imported seat keeps whatever the blank roster had, which is why every job came
/// through as unset even though the plan plainly knew what everyone was playing.
/// </remarks>
public static class JobAssets
{
    /// <summary>
    /// ClassJob sheet ids. Fixed by the game and stable across patches, so a static table beats
    /// reaching into the sheets — it also means this works with no game attached, in tests.
    /// </summary>
    private static readonly Dictionary<string, uint> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23,
        ["WHM"] = 24, ["BLM"] = 25, ["SMN"] = 27, ["SCH"] = 28, ["NIN"] = 30,
        ["MCH"] = 31, ["DRK"] = 32, ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35,
        ["BLU"] = 36, ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39, ["SGE"] = 40,
        ["VPR"] = 41, ["PCT"] = 42,
    };

    /// <summary>Their role artwork, and the odd name they use for a caster.</summary>
    private static readonly Dictionary<string, RaidRole> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["role_tank"] = RaidRole.Tank,
        ["role_healer"] = RaidRole.Healer,
        ["role_melee"] = RaidRole.Melee,
        ["role_ranged"] = RaidRole.PhysicalRanged,
        ["role_phys_ranged"] = RaidRole.PhysicalRanged,
        ["role_caster"] = RaidRole.MagicalRanged,
        ["rmage"] = RaidRole.MagicalRanged,
        ["role_dps"] = RaidRole.Melee,
    };

    public static TokenIdentity Read(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return default;

        var stem = Path.GetFileNameWithoutExtension(assetPath.Replace('\\', '/'));
        if (string.IsNullOrEmpty(stem))
            return default;

        // The site's own job catalog uses bard.png, while the ClassJob abbreviation is BRD.
        if (stem.Equals("bard", StringComparison.OrdinalIgnoreCase)) stem = "BRD";

        if (Roles.TryGetValue(stem, out var role))
            return new TokenIdentity(role, 0);

        if (Jobs.TryGetValue(stem, out var jobId))
            return new TokenIdentity(JobRoles.RoleFor(stem), jobId);

        // Numbered artwork is a game icon id we cannot resolve to a job from the name alone.
        return default;
    }
}
