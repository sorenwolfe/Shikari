using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Shikari.Model;

namespace Shikari.Services.Live;

/// <summary>
/// Puts the party's real positions onto the plan's board.
/// </summary>
/// <remarks>
/// The point of this is the gap between a diagram and following one. A plan can say "north-east
/// tower" perfectly clearly and still leave a newer raider translating that into a direction on
/// screen while something is casting at them. Showing where they are next to where the plan wants
/// them removes the translation step.
///
/// It shows information, and only information the game already puts on screen: your own position,
/// your party's, and the waymarks. It never moves anyone and never places a marker.
/// </remarks>
public sealed class ArenaTracker
{
    /// <summary>A party member, placed on the board.</summary>
    public readonly record struct LivePlayer(
        string Name,
        uint JobId,
        int SlotIndex,
        Vector2 Board,
        bool IsLocal);

    /// <summary>
    /// How long a solved alignment is reused before the waymarks are read again.
    /// </summary>
    /// <remarks>
    /// Waymarks move when somebody places them, which is rare; player positions move constantly.
    /// So the fit is cached and the positions are not — the dots stay smooth at frame rate while
    /// the expensive half runs four times a second. Two windows drawing the same board also share
    /// the one fit rather than each solving it.
    /// </remarks>
    private static readonly TimeSpan AlignmentLifetime = TimeSpan.FromMilliseconds(250);

    private WorldAlignment cached;
    private DateTime cachedAtUtc = DateTime.MinValue;
    private string cachedSlideId = string.Empty;
    private PlanDocument? cachedPlan;
    private string cachedSourceUrl = string.Empty;
    private bool cachedLegacyBoard;
    private bool cachedResult;

    /// <summary>Why there is nothing to draw, in words a player can act on.</summary>
    public string Status { get; private set; } = string.Empty;

    public bool Aligned { get; private set; }

    /// <summary>Mean error of the last fit, as a fraction of the board.</summary>
    public float Residual { get; private set; }

    /// <summary>
    /// Board units to a yalm under the last fit, so a real distance can be turned into a
    /// distance on the board. Zero when nothing is lined up.
    /// </summary>
    public float BoardPerYalm { get; private set; }

    /// <summary>
    /// Lines the board up with the arena using the waymarks as the common reference.
    /// </summary>
    /// <remarks>
    /// The current slide first, then other steps of the same source board. Legacy authored
    /// plans can share waymarks, but independently imported boards cannot share coordinates.
    /// </remarks>
    public bool TryAlign(PlanDocument plan, Slide? slide, out WorldAlignment alignment)
    {
        var slideId = slide?.Id ?? string.Empty;

        if (ReferenceEquals(plan, cachedPlan) && slideId == cachedSlideId &&
            (slide?.SourceUrl ?? string.Empty) == cachedSourceUrl && LegacyBoard(slide) == cachedLegacyBoard &&
            DateTime.UtcNow - cachedAtUtc < AlignmentLifetime)
        {
            alignment = cached;
            return cachedResult;
        }

        var result = Solve(plan, slide, out alignment);

        cached = alignment;
        cachedResult = result;
        cachedSlideId = slideId;
        cachedPlan = plan;
        cachedSourceUrl = slide?.SourceUrl ?? string.Empty;
        cachedLegacyBoard = LegacyBoard(slide);
        cachedAtUtc = DateTime.UtcNow;

        return result;
    }

    private bool Solve(PlanDocument plan, Slide? slide, out WorldAlignment alignment)
    {
        alignment = default;
        Aligned = false;
        Residual = 0f;
        BoardPerYalm = 0f;

        var placed = FieldMarkers.Read();
        if (placed.Count < WorldAlignment.MinimumPairs)
        {
            Status = "Place your waymarks in the duty to line the plan up with the arena.";
            return false;
        }

        var drawn = DrawnWaymarks(plan, slide);
        if (drawn == null)
        {
            Status = "This board has no usable waymarks. Add matching waymarks to this board to align live positions.";
            return false;
        }

        var pairs = new List<AlignmentPair>(placed.Count);
        foreach (var marker in placed)
        {
            if (drawn.TryGetValue(marker.Letter, out var board))
                pairs.Add(new AlignmentPair(marker.World, board));
        }

        if (pairs.Count < WorldAlignment.MinimumPairs)
        {
            Status = $"Only {pairs.Count} waymark(s) match between the plan and the arena; " +
                     $"{WorldAlignment.MinimumPairs} are needed.";
            return false;
        }

        if (!WorldAlignment.TrySolve(pairs, out var solved))
        {
            Status = "The waymarks are all in the same place, so there is no way to tell which way round the plan goes.";
            return false;
        }

        Residual = solved.Residual;

        if (!solved.IsTrustworthy)
        {
            // Drawing a bad fit is worse than drawing nothing: someone walks to the wrong dot.
            Status = "The plan's waymarks do not match the ones in the arena closely enough to be trusted.";
            return false;
        }

        alignment = solved;
        Aligned = true;
        BoardPerYalm = solved.Scale;
        Status = string.Empty;
        return true;
    }

    /// <summary>Where everyone actually is, on the board. Empty when it cannot be worked out.</summary>
    public IReadOnlyList<LivePlayer> Read(PlanDocument plan, Slide? slide, int? localSlotOverride = null)
    {
        if (!TryAlign(plan, slide, out var alignment))
            return Array.Empty<LivePlayer>();

        var localName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        var pinned = localSlotOverride ?? Plugin.Config.GetActiveTeam().PinnedSlotIndex;
        var players = new List<LivePlayer>(8);

        foreach (var (name, jobId, world) in ReadPositions())
        {
            var isLocal = !string.IsNullOrEmpty(localName) && name.Equals(localName, StringComparison.OrdinalIgnoreCase);
            var seat = RosterResolver.MatchSeat(plan.Roster, name, jobId, isLocal ? pinned : -1);
            players.Add(new LivePlayer(
                name,
                jobId,
                seat,
                alignment.ToPlan(world),
                isLocal));
        }

        // A seat represents one person. Preserve the local resolution used by callouts;
        // other collisions remain unassigned instead of guessing from party-list order.
        var collisions = players.Where(p => p.SlotIndex >= 0)
            .GroupBy(p => p.SlotIndex).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        for (var i = 0; i < players.Count; i++)
        {
            if (collisions.Contains(players[i].SlotIndex) && !players[i].IsLocal)
                players[i] = players[i] with { SlotIndex = -1 };
        }

        return players;
    }

    /// <summary>Names, jobs and ground positions for the party, or just you when solo.</summary>
    private static List<(string Name, uint JobId, Vector2 World)> ReadPositions()
    {
        var found = new List<(string, uint, Vector2)>(8);

        try
        {
            if (Plugin.PartyList.Length > 0)
            {
                for (var i = 0; i < Plugin.PartyList.Length; i++)
                {
                    var member = Plugin.PartyList[i];
                    if (member == null)
                        continue;

                    found.Add((
                        member.Name.TextValue,
                        member.ClassJob.RowId,
                        WorldAlignment.Ground(member.Position)));
                }

                return found;
            }

            var local = Plugin.ObjectTable.LocalPlayer;
            if (local != null)
            {
                found.Add((
                    local.Name.TextValue,
                    local.ClassJob.RowId,
                    WorldAlignment.Ground(local.Position)));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read party positions.");
            found.Clear();
        }

        return found;
    }

    /// <summary>The waymarks the plan draws, by letter. Null when no slide has enough of them.</summary>
    private static Dictionary<string, Vector2>? DrawnWaymarks(PlanDocument plan, Slide? slide)
    {
        if (slide != null && TryCollect(slide, out var here))
            return here;

        foreach (var other in plan.Slides)
        {
            if (!ReferenceEquals(other, slide) && SameBoard(slide, other) && TryCollect(other, out var found))
                return found;
        }

        return null;
    }

    private static bool SameBoard(Slide? current, Slide other)
    {
        var source = BoardSource(current);
        if (source.Length > 0) return source == BoardSource(other);
        return LegacyBoard(current) && LegacyBoard(other);
    }

    private static string BoardSource(Slide? slide) => slide != null &&
        Uri.TryCreate(slide.SourceUrl, UriKind.Absolute, out var source) && source.Scheme is "https" or "http"
            ? source.GetLeftPart(UriPartial.Query) : string.Empty;

    private static bool LegacyBoard(Slide? slide) => slide == null ||
        string.IsNullOrEmpty(slide.SourceUrl) && string.IsNullOrEmpty(slide.GuideUrl) && string.IsNullOrEmpty(slide.SourceLabel) &&
        slide.SourceStep < 0 && slide.ArenaOverride == null;

    private static bool TryCollect(Slide slide, out Dictionary<string, Vector2> marks)
    {
        marks = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in slide.Items)
        {
            if (item.Kind != CanvasItemKind.Waymark || string.IsNullOrEmpty(item.Text))
                continue;

            // First wins. A plan with two "A"s is already ambiguous; picking one beats guessing
            // an average that sits between them and is right for neither.
            marks.TryAdd(item.Text.Trim(), item.Position);
        }

        return marks.Count >= WorldAlignment.MinimumPairs;
    }
}
