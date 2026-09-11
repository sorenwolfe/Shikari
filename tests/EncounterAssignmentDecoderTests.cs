using System;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Services.Replay;

namespace Shikari.Tests;

public static class EncounterAssignmentDecoderTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    // Synthetic source observations verify decoding safeguards, not actual M12S pull coverage.
    static ReplayAttempt Recording(uint number = 3004, uint bonds = 4752)
    {
        var attempt = new ReplayAttempt { Duration = 100 };
        attempt.Mechanics.Add(new ReplayMechanic { ActionId = 48830, Occurrence = 1, Time = 10, ExpectedResolve = 13 });
        attempt.Evidence.Actors.Add(new EvidenceActor { Id = 7 });
        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = number, Time = 12, Duration = 30 });
        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = bonds, Time = 12.1f, Duration = 30 });
        return attempt;
    }

    static EncounterAssignment? Decode(ReplayAttempt attempt, float time = 14, long actor = 7) =>
        EncounterAssignmentDecoder.Decode(attempt, attempt.Mechanics[0], actor, time,
            new EvidenceTimeline(attempt.Evidence).StatusesAt(actor, time));

    public static void Run()
    {
        var numbers = new uint[] { 3004, 3005, 3006, 3451 };
        for (var i = 0; i < numbers.Length; i++)
        foreach (var letter in new[] { (4752u, "A"), (4754u, "B") })
        {
            var attempt = Recording(numbers[i], letter.Item1);
            var before = JsonConvert.SerializeObject(attempt);
            var result = Decode(attempt);
            Check(result is { IsResolved: true } && result.Number == i + 1 && result.Letter == letter.Item2,
                "Each distinct In Line status and Bonds A/B pair must retain its actual number and letter.");
            Check(result!.Label.Contains("Bonds " + letter.Item2), "The passive label must identify the observed Bonds status.");
            Check(JsonConvert.SerializeObject(attempt) == before, "Decoding must not edit plans, recordings or navigation.");
        }

        var unknownDuration = Recording(3451, 4754);
        foreach (var status in unknownDuration.Evidence.Statuses) { status.Duration = null; status.Parameter = null; status.Stacks = 99; }
        Check(Decode(unknownDuration) is { IsResolved: true, Number: 4, Letter: "B" },
            "Status IDs determine order; unknown duration/parameter and unrelated stack counts must not change it.");

        foreach (var baseline in new[] { false, true })
        {
            var attempt = Recording();
            if (baseline) attempt.Evidence.Statuses[0].Baseline = true;
            else attempt.Evidence.Statuses.RemoveAt(0);
            Check(Decode(attempt) is { IsResolved: false }, "Missing or baseline-only number evidence must stay unresolved.");
        }
        var delayed = Recording(); delayed.Evidence.Statuses[1].Time = 15;
        Check(Decode(delayed) is { IsResolved: false } && Decode(delayed, 15) is { IsResolved: true },
            "The pair must be concurrently present at the selected review time.");
        var stale = Recording(); stale.Evidence.Statuses[0].Time = 9;
        Check(Decode(stale) is { IsResolved: false }, "A generic number already present before Act 2 is not a fresh assignment.");
        var conflict = Recording(); conflict.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = 3005, Time = 12 });
        Check(Decode(conflict) is { IsResolved: false }, "Two active number statuses must not choose an arbitrary number.");
        conflict = Recording(); conflict.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = 4754, Time = 12 });
        Check(Decode(conflict) is { IsResolved: false }, "Both Bonds statuses must remain conflicting.");
        var unverified = Recording(); unverified.Evidence.Statuses[0].AbilityId = 1003004; unverified.Evidence.Statuses[0].StatusId = 0;
        Check(Decode(unverified) is { IsResolved: false }, "Raw aura ability IDs do not establish a verified Status sheet row.");
        var possibleConflict = Recording(); possibleConflict.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = 0, AbilityId = 1004754, Time = 12 });
        Check(Decode(possibleConflict) is { IsResolved: false }, "An unverified alternate Bonds observation must not disappear behind a verified pair.");
        Check(Decode(Recording(3004, 4753)) is { IsResolved: false }, "Unbreakable A is not Bonds A.");

        foreach (var change in new[] { "remove", "unavailable" })
        {
            var attempt = Recording();
            attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 7, StatusId = 4752, Time = 13.5f, Change = change });
            Check(Decode(attempt) is { IsResolved: false }, "Removal or unavailable status snapshots must invalidate the assignment.");
        }
        Check(Decode(Recording(), 42) is { IsResolved: false }, "Expired number evidence must not be used.");
        var partial = Recording(); partial.Evidence.Complete = false;
        Check(Decode(partial) is { IsResolved: false }, "Incomplete recordings cannot establish a unique assignment.");
        Check(Decode(Recording(), actor: 8) is { IsResolved: false }, "Another actor must not inherit the selected player's statuses.");
        var wrongActor = Recording(); wrongActor.Evidence.Statuses[1].ActorId = 8;
        Check(Decode(wrongActor) is { IsResolved: false }, "The two statuses must belong to the same source actor.");

        var wrongCast = Recording(); wrongCast.Mechanics[0].ActionId = 48829;
        Check(Decode(wrongCast) == null, "Generic In Line statuses do not identify Act 2 outside its action anchor.");
        var foreign = Recording(); var another = Recording();
        Check(EncounterAssignmentDecoder.Decode(foreign, another.Mechanics[0], 7, 14,
            new EvidenceTimeline(foreign.Evidence).StatusesAt(7, 14)) == null,
            "A cast object from another recording must not establish context.");
        Check(Decode(Recording(), 9) == null && Decode(Recording(), float.NaN) == null && Decode(Recording(), 101) == null,
            "Review time must be within the recording and at or after the actual anchor.");
        var repeated = Recording(); repeated.Mechanics.Add(new ReplayMechanic { ActionId = 48830, Occurrence = 2, Time = 13 });
        Check(Decode(repeated) == null, "An earlier anchor cannot classify statuses after a later Act 2 occurrence.");
        var nextAct = Recording(); nextAct.Mechanics.Add(new ReplayMechanic { ActionId = 48831, Occurrence = 1, Time = 13 });
        Check(Decode(nextAct) == null, "A stale Act 2 selection cannot label generic statuses in Act 3.");
        Console.WriteLine("Encounter assignment decoder checks passed (synthetic observations; no real-pull validation claim).");
    }
}
