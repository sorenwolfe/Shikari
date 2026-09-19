# FF Logs cast identity and player-effect coverage

The approved next step after the storage/performance pass was to preserve cast source instances and completion targets, and filter player damage before the effect cap. This work stays on `codex/launch-readiness`, at version 0.9.5.0, without a release or remote push.

## Behavior

- Cast starts and completions retain their own target IDs and optional source/target instances. Fractional event timestamps survive pairing. A completion is an observed cast event, not proof that damage resolved at that instant.
- Explicit matching NPC instances can pair. An absent instance can pair only when independent report/fight metadata establishes one possible actor instance. Later event evidence of multiple instances overrides that metadata. Once a completion has unresolved candidate starts, pairing stops for that actor/action for the rest of the import; later starts cannot erase the ambiguity. All observations are retained separately.
- Malformed cast identities reject the cast import, preserving the existing rule that partial cast histories cannot establish reliable occurrences. Existing 20-page/200,000-event bounds remain.
- The first evidence request obtains the selected fight's `friendlyPlayers` and report actor types. Damage to other actors is filtered before the 32,768-effect limit, while useful player resource positions from outgoing damage remain available.
- Unverified player membership makes effect coverage explicitly incomplete. An independently verified empty player set is different from absent metadata. Invalid target IDs cannot silently count as irrelevant damage. Status observations remain independently usable.
- Replay construction retains independently verified participants even when they appear only as damage recipients. Conflicting or duplicate actor metadata cannot silently create a player or claim full effect coverage. The saved scope is detached, validated and included in asynchronous replay snapshots.
- Review can inspect the cast-start target and completion target separately. Each action selects that actor, pauses at the appropriate observed timestamp and clears the previous actor's draft. Missing completion targets remain unknown rather than falling back to the start target.
- Older recordings still load with unknown optional fields. Reimport is required to acquire the new identities and player-scope provenance.

## Source verification

The [FF Logs ReportFight schema](https://www.fflogs.com/v2-api-docs/ff/reportfight.doc.html) defines participating player IDs and fight NPC lists. The [ReportActor schema](https://www.fflogs.com/v2-api-docs/ff/reportactor.doc.html) supplies report-scoped identity and Player/Pet/NPC types. The [Report schema](https://www.fflogs.com/v2-api-docs/ff/report.doc.html) supports querying selected fight metadata beside events. Authenticated FFXIV schema introspection also confirmed `ReportFightNPC.id` and `instanceCount`; no assumption was made that an omitted raw `sourceInstance` means instance one.

## Real report check

The production client was run against the user's report, fight 2, using the locally configured credentials. No credentials, raw player names or event payloads were added to the repository.

| Observation | Result |
| --- | ---: |
| Encounter ID / duration | 104 / 425.672 seconds |
| Enemy cast records | 317 |
| Paired enemy cast starts | 163 |
| Unpaired enemy starts | 0 |
| Paired enemy casts with different known start/completion targets | 17 |
| Independently verified report player IDs | 10 |
| Retained player-targeted damage observations | 519 |
| Parsed status observations | 5,089 |
| Parsed sparse position samples | 10,336 |

The event acquisition and effect channel both completed without reaching their limits. These are parser/acquisition counts, before replay conversion, status-sheet checks or position calibration. Ten report player IDs does not mean ten assigned party seats: seat matching remains independently reviewed and duplicate matches remain unassigned. This check does not establish the correctness of an adaptive strategy or a mechanic outcome.

## Verification and remaining work

Targeted regression tests cover overlapping NPC instances, independent completion targets, fractional timestamps, cross-page casts, malformed identity, contradictory metadata, player-scope validation, irrelevant-damage overflow, damage-only participants, replay persistence, backward compatibility and target-inspection behavior.

- All 59 workflow scripts passed, including native input/rendering and asynchronous storage recovery.
- After independent review, the FF Logs suite passed 107 checks and the effect suite passed again. Cast observation tests passed 59 checks. The effect and cast-target UI suites passed their targeted regressions. Newly introduced replay properties passed the existing exhaustive detached-snapshot test.
- The Release build completed with zero warnings and zero errors.
- The final package contains the root DLL and manifest, still version 0.9.5.0 / Dalamud API 15. Packaged and built DLL SHA-256 matched: `5A0248A6D71FB12C47B6E016DC2FDF0A388EFC98907BDC429DA2E3B3808FEC07`.
- Independent review identified two corrected regressions: ambiguity could disappear before a later cast start, and a non-object `masterData` value could abort optional evidence acquisition. Both were reproduced with failing regressions before the fixes. The final build and live-report check passed again with the same acquisition counts.
- The production-client live check above verifies that the expanded GraphQL queries and parsers accept the real report. It does not replace an in-game UI or duty test.

The next encounter workflow should record the personal cues actually delivered during a pull, then compare those decisions with reviewed status, cast and position evidence from additional pulls. An observed destination alone must not become an automatic instruction for a variable mechanic. Actual in-game UI checks, calibration and duty soak testing remain necessary before 1.0.

The overall event stream is still capped at 200,000 observations/20 pages; filtering the effect channel does not remove that acquisition limit. Sparse FF Logs coordinates still need independent arena landmarks, and cast completion remains distinct from damage timing.
