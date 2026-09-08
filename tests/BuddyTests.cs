using System;
using Shikari.Services.Buddy;

namespace Shikari.Tests;

public static class BuddyTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var plan = new object();
        var state = new BuddyContext(true, true, 100, 42, false, true, true, plan, "one");
        var engine = new BuddyCueEngine();
        engine.Update(state, Start);
        Check(!engine.Presentation.HasCue, "Idle must not chatter.");
        engine.Reminder("other", "Another player's spread", false, Start.AddSeconds(1), Start.AddSeconds(20), Start.AddSeconds(1));
        Check(!engine.Presentation.HasCue, "Other players' calls must never be personalized.");
        engine.Reminder("mine", "Use your mitigation", true, Start.AddSeconds(1), Start.AddSeconds(20), Start.AddSeconds(1));
        Check(engine.Presentation.Body == "Use your mitigation", "A newly delivered local call must be shown.");
        engine.Update(state, Start.AddSeconds(9));
        Check(!engine.Presentation.HasCue, "Calls must expire after at most eight seconds.");
        engine.Reminder("mine", "Use your mitigation", true, Start.AddSeconds(1), Start.AddSeconds(20), Start.AddSeconds(9));
        Check(!engine.Presentation.HasCue, "An expired call must never replay.");

        engine.Update(state with { Enabled = false }, Start.AddSeconds(10));
        engine.Reminder("disabled", "Spread", true, Start.AddSeconds(11), Start.AddSeconds(20), Start.AddSeconds(11));
        engine.Update(state, Start.AddSeconds(12));
        Check(!engine.Presentation.HasCue, "Re-enabling must not replay disabled calls.");
        engine.Reminder("stale", "Spread", true, Start.AddSeconds(11.9), Start.AddSeconds(20), Start.AddSeconds(12));
        Check(!engine.Presentation.HasCue, "A previous enable generation cannot deliver an old call.");
        engine.Reminder("fresh", "Spread", true, Start.AddSeconds(13), Start.AddSeconds(20), Start.AddSeconds(13));
        Check(engine.Presentation.HasCue, "Fresh events after re-enabling must work.");
        engine.Update(state with { CallsEnabled = false }, Start.AddSeconds(14));
        Check(!engine.Presentation.HasCue, "Calls master off clears its current call.");

        engine.Update(state, Start.AddSeconds(15));
        engine.Arm("cast-1", "Assignments", Start.AddSeconds(25), Start.AddSeconds(16));
        Check(engine.Presentation.Mood == BuddyMood.Listening && engine.Presentation.Body == "Waiting for your assignment", "Only an armed assignment may produce a waiting cue.");
        engine.Decide("unrelated", "Wrong slide", true, Start.AddSeconds(17));
        Check(engine.Presentation.Mood == BuddyMood.Listening, "Unrelated or stale adaptive decisions must be ignored.");
        engine.Decide("cast-1", "East spread", true, Start.AddSeconds(17));
        Check(engine.Presentation.Body == "East spread" && engine.Presentation.Mood == BuddyMood.Guiding, "An applied matching decision uses its authored destination.");
        engine.Reminder("generic", "Generic stack", true, Start.AddSeconds(18), Start.AddSeconds(25), Start.AddSeconds(18));
        Check(engine.Presentation.Body == "East spread", "A generic reminder cannot overwrite an observed assignment.");
        engine.Update(state with { Following = false }, Start.AddSeconds(19));
        Check(!engine.Presentation.HasCue, "Holding navigation clears adaptive guidance.");
        engine.Update(state, Start.AddSeconds(20));
        Check(!engine.Presentation.HasCue, "Releasing a hold cannot replay the previous assignment.");
        engine.Arm("cast-2", "Assignments", Start.AddSeconds(25), Start.AddSeconds(21));
        engine.Decide("cast-2", "", false, Start.AddSeconds(22));
        Check(engine.Presentation.Mood == BuddyMood.Uncertain && engine.Presentation.Body == "Check the mechanic", "Unresolved assignments must not invent movement.");
        engine.Update(state, Start.AddSeconds(28));
        Check(!engine.Presentation.HasCue, "An uncertain assignment must expire.");

        engine.Arm("held", "Assignments", Start.AddSeconds(40), Start.AddSeconds(29));
        engine.Decide("held", "West", false, Start.AddSeconds(30));
        Check(!engine.Presentation.HasCue, "A known but held destination must not be presented as current.");
        engine.Arm("timeout", "Assignments", Start.AddSeconds(35), Start.AddSeconds(31));
        engine.Update(state, Start.AddSeconds(37));
        engine.Decide("timeout", "West", true, Start.AddSeconds(37));
        Check(!engine.Presentation.HasCue, "A late decision after its window must never restore action cues.");

        foreach (var replacement in new[] {
            state with { Dead = true }, state with { PlayerId = 99 }, state with { TerritoryId = 101 },
            state with { Plan = new object() }, state with { PlanId = "two" }, state with { InCombat = false },
            state with { PlayerId = 0 }, state with { Plan = null } })
        {
            engine.Update(state, Start.AddSeconds(40));
            engine.Reminder(Guid.NewGuid().ToString(), "Move now", true, Start.AddSeconds(41), Start.AddSeconds(45), Start.AddSeconds(41));
            engine.Update(replacement, Start.AddSeconds(42));
            Check(!engine.Presentation.HasCue, "Player, plan, territory, death and pull changes must invalidate cues.");
        }
        engine.Update(state, Start.AddSeconds(50));
        engine.Reminder("markup", "\u202e  Go\n  east\0 \t" + new string('x', 300), true, Start.AddSeconds(51), Start.AddSeconds(59), Start.AddSeconds(51));
        Check(engine.Presentation.Body.Length <= 140 && !engine.Presentation.Body.Contains('\n') && !engine.Presentation.Body.Contains('\0') && !engine.Presentation.Body.Contains('\u202e'), "Imported text must be short, single-line and free of control formatting.");
        engine.Clear(Start.AddSeconds(52));
        Check(!engine.Presentation.HasCue, "Explicit end/wipe/dispose reset clears presentation.");
        engine.Reminder("opening", "Opening mitigation", true, Start.AddSeconds(51.95), Start.AddSeconds(56), Start.AddSeconds(52.02), Start.AddSeconds(52.02));
        Check(engine.Presentation.Body == "Opening mitigation", "A fresh opening delivery may be due a few milliseconds before pull-start initialization.");
        engine.Clear(Start.AddSeconds(52.1));
        engine.Reminder("previous-delivery", "Old event", true, Start.AddSeconds(51.95), Start.AddSeconds(56), Start.AddSeconds(52.2), Start.AddSeconds(52.02));
        Check(!engine.Presentation.HasCue, "Separating due time must not allow a delivery from a previous enable generation.");
        Console.WriteLine("Buddy behavior checks passed.");
    }
}
