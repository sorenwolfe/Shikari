using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shikari.Services.Buddy;

public enum BuddyMood { Resting, Listening, Guiding, Uncertain, Recovering }
public sealed record BuddyPresentation(string Key, string Heading, string Body, string Detail, BuddyMood Mood, bool HasCue);

public readonly record struct BuddyContext(bool Enabled, bool InCombat, uint TerritoryId, uint PlayerId,
    bool Dead, bool CallsEnabled, bool Following, object? Plan, string PlanId);

public sealed class BuddyCueEngine
{
    private BuddyContext context;
    private bool initialized;
    private DateTime generation;
    private DateTime lastUpdate;
    private DateTime expires;
    private DateTime pendingDeadline;
    private string pendingKey = "";
    private string pendingMechanic = "";
    private int priority;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();

    public BuddyPresentation Presentation { get; private set; } = new("", "", "", "", BuddyMood.Resting, false);

    private bool Ready => initialized && context.Enabled && context.InCombat && context.PlayerId != 0 &&
        !context.Dead && context.Plan != null && context.PlanId.Length > 0;

    public void Update(BuddyContext next, DateTime now)
    {
        var changed = !initialized || now < lastUpdate || context.Enabled != next.Enabled ||
            context.InCombat != next.InCombat || context.TerritoryId != next.TerritoryId ||
            context.PlayerId != next.PlayerId || context.Dead != next.Dead ||
            !ReferenceEquals(context.Plan, next.Plan) || context.PlanId != next.PlanId;
        context = next;
        initialized = true;
        lastUpdate = now;
        if (changed || !Ready) Clear(now);
        if (!context.CallsEnabled && priority == 20) ClearCue();
        if (!context.Following)
        {
            pendingKey = "";
            if (priority is 10 or 30) ClearCue();
        }
        if (pendingKey.Length > 0 && now > pendingDeadline)
        {
            pendingKey = "";
            if (priority == 10) ClearCue();
        }
        if (Presentation.HasCue && now >= expires) ClearCue();
        if (!Presentation.HasCue) Idle();
    }

    /// <summary>Only a new, local delivery may become a cue. There is deliberately no backlog.</summary>
    public void Reminder(string key, string text, bool forLocalPlayer, DateTime scheduled, DateTime expiresAt, DateTime now, DateTime? deliveredAt = null)
    {
        var delivered = deliveredAt ?? scheduled;
        if (!Ready || !context.CallsEnabled || !forLocalPlayer || priority > 20 ||
            delivered < generation || delivered > now.AddSeconds(.1) || now - delivered > TimeSpan.FromSeconds(1) ||
            scheduled > now.AddSeconds(.1) || now - scheduled > TimeSpan.FromSeconds(1) || expiresAt <= now)
            return;
        var body = Clean(text, 140);
        if (body.Length == 0 || !Remember("call/" + key)) return;
        var deadline = scheduled.AddSeconds(8);
        if (expiresAt < deadline) deadline = expiresAt;
        Show("call/" + key, "Your call", body, "From your configured timeline", BuddyMood.Guiding, 20, deadline);
    }

    /// <summary>The service arms only validated, non-overlapping rules for the observed cast.</summary>
    public void Arm(string key, string mechanic, DateTime deadline, DateTime now)
    {
        if (!Ready || !context.Following || deadline <= now || deadline > now.AddSeconds(61) ||
            !Remember("arm/" + key)) return;
        pendingKey = key;
        pendingMechanic = Clean(mechanic, 72);
        // A new mechanic must never retain the previous mechanic's destination.
        if (priority == 30) ClearCue();
        pendingDeadline = deadline;
        if (priority > 10) return;
        Show("wait/" + key, pendingMechanic, "Waiting for your assignment", "Watch for your status effect", BuddyMood.Listening,
            10, deadline < now.AddSeconds(6) ? deadline : now.AddSeconds(6));
    }

    public void Decide(string key, string title, bool applied, DateTime now, string? detail = null)
    {
        if (!Ready || !context.Following || key != pendingKey || now > pendingDeadline) return;
        pendingKey = "";
        if (!string.IsNullOrEmpty(title))
        {
            if (!applied) { ClearCue(); return; }
            var body = Clean(title, 140);
            if (body.Length == 0) { ClearCue(); return; }
            Show("assignment/" + key, "Your assignment", body, detail ?? "Assignment detected · See the selected board",
                BuddyMood.Guiding, 30, now.AddSeconds(6));
        }
        else
            Show("unknown/" + key, "Assignment unclear", "Check the mechanic", pendingMechanic,
                BuddyMood.Uncertain, 30, now.AddSeconds(6));
    }

    public void Clear(DateTime now)
    {
        generation = now;
        pendingKey = "";
        pendingMechanic = "";
        seen.Clear(); seenOrder.Clear();
        ClearCue();
    }

    private void ClearCue() { priority = 0; expires = default; Idle(); }
    private void Idle() => Presentation = new("", "", "", "",
        context.Enabled && context.Dead ? BuddyMood.Recovering : Ready ? BuddyMood.Listening : BuddyMood.Resting, false);
    private void Show(string key, string heading, string body, string detail, BuddyMood mood, int rank, DateTime until)
    {
        Presentation = new(key, Clean(heading, 72), Clean(body, 140), Clean(detail, 96), mood, true);
        priority = rank;
        expires = until;
    }

    private bool Remember(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256 || !seen.Add(key)) return false;
        seenOrder.Enqueue(key);
        while (seenOrder.Count > 128) seen.Remove(seenOrder.Dequeue());
        return true;
    }

    /// <summary>Display authored text safely without interpreting guide prose as instructions.</summary>
    private static string Clean(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = new StringBuilder(limit);
        var space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.Surrogate) continue;
            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.Control) { space = result.Length > 0; continue; }
            var needed = rune.Utf16SequenceLength + (space ? 1 : 0);
            if (result.Length + needed > limit - 1) { result.Append('…'); break; }
            if (space) { result.Append(' '); space = false; }
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
