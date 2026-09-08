using System;
using Shikari.Services.Buddy;

namespace Shikari.Tests;

public static class BuddyAmbientTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var active = new BuddyAmbientContext(true, 42, 100, true, false, false, false, false, false);
        var controller = new BuddyAmbientController();
        controller.Update(active, now);
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "A new enabled companion must not invent a return from AFK.");
        controller.Update(active with { Away = true }, now.AddSeconds(1));
        Check(controller.Presentation.State == BuddyAmbientState.Sleeping, "The game's readable AFK status should make the companion sleep.");
        var sleepStart = controller.Presentation.StartedAtUtc;
        controller.Update(active with { Away = true }, now.AddSeconds(2));
        Check(controller.Presentation.StartedAtUtc == sleepStart, "Continuing AFK must not restart the sleeping animation every frame.");
        controller.Update(active, now.AddSeconds(3));
        Check(controller.Presentation.State == BuddyAmbientState.Welcoming, "A fresh AFK-to-active transition should briefly welcome the player back.");
        controller.Update(active, now.AddSeconds(5.4));
        Check(controller.Presentation.State == BuddyAmbientState.Welcoming, "A welcome should survive ordinary updates for its short duration.");
        controller.Update(active, now.AddSeconds(5.5));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "A welcome must expire instead of becoming constant chatter.");
        controller.Update(active with { InDuty = true }, now.AddSeconds(6));
        Check(controller.Presentation.State == BuddyAmbientState.Focused, "An active player in duty should get the determined pose without needing a plan.");
        controller.Update(active with { InDuty = true, Away = true }, now.AddSeconds(7));
        Check(controller.Presentation.State == BuddyAmbientState.Sleeping, "An AFK break between pulls can sleep even in duty.");
        controller.Update(active with { InDuty = true }, now.AddSeconds(8));
        controller.Update(active with { InDuty = true }, now.AddSeconds(10.5));
        Check(controller.Presentation.State == BuddyAmbientState.Focused, "Returning in duty should settle back into its determined pose after welcoming.");

        foreach (var interrupted in new[] {
            active with { Enabled = false }, active with { PlayerId = 0 }, active with { PlayerId = 99 },
            active with { TerritoryId = 101 }, active with { Suppressed = true }, active with { Dead = true },
            active with { StatusKnown = false }, active with { InCombat = true } })
        {
            controller = new BuddyAmbientController();
            controller.Update(active with { Away = true }, now);
            controller.Update(interrupted, now.AddSeconds(1));
            Check(controller.Presentation.State != BuddyAmbientState.Welcoming && controller.Presentation.State != BuddyAmbientState.Sleeping,
                "Interrupted actor/visibility/combat/status context must not welcome or remain asleep.");
            controller.Update(active, now.AddSeconds(2));
            Check(controller.Presentation.State == BuddyAmbientState.Idle, "A suppressed or replaced AFK baseline must not replay its welcome later.");
        }
        controller = new BuddyAmbientController();
        controller.Update(active with { Away = true, InCombat = true }, now);
        Check(controller.Presentation.State == BuddyAmbientState.Focused, "Combat must override the AFK flag.");
        controller.Update(active, now.AddSeconds(1));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "AFK observed during combat cannot queue a post-combat welcome.");
        controller.Update(active with { Away = true }, now.AddSeconds(2));
        controller.Update(active, now.AddSeconds(20));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "A stale gap between snapshots must not reconstruct a return animation.");
        controller.Update(active with { Away = true }, now.AddSeconds(21));
        controller.Update(active, now.AddSeconds(19));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "Clock rollback must invalidate the AFK transition history.");
        controller.Update(active with { Away = true }, now.AddSeconds(20));
        controller.Update(active, now.AddSeconds(21));
        controller.Reset(now.AddSeconds(21));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "Disposal/reset clears cosmetic state and transition history.");
        controller.Update(active, now.AddSeconds(22));
        Check(controller.Presentation.State == BuddyAmbientState.Idle, "A reset cannot replay an old welcome.");
        foreach (var carried in new[] { active with { TerritoryId = 101 }, active with { PlayerId = 99 }, active })
        {
            controller = new BuddyAmbientController();
            controller.Update(active, now);
            controller.Update(active with { Away = true }, now.AddSeconds(1));
            if (carried == active) controller.Reset(now.AddSeconds(2));
            controller.Update(carried with { Away = true }, now.AddSeconds(2));
            controller.Update(carried, now.AddSeconds(3));
            Check(controller.Presentation.State == BuddyAmbientState.Idle,
                "An AFK flag carried through a new territory, actor or generation cannot arm a welcome when it clears.");
        }
        Console.WriteLine("Buddy ambient transition checks passed.");
    }
}
