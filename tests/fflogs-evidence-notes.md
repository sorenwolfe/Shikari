# FF Logs evidence verification

Run `pwsh -NoProfile -File tests/fflogs-evidence.ps1` for the isolated parser and HTTP acquisition checks. The suite uses a controlled HTTP handler at the network boundary and the production client/parser. No credentials or remote report are needed.

Verified against the configured report, fight 30, on 2026-09-07: the complete read yielded 6,484 status events, including 35 initial auras; 11,700 deduplicated position observations; 4,104 known durations; and zero verified live status parameters. This check requested all event types with resources. Neither player names nor authentication responses are stored in test fixtures.

Observed v2 shapes:

- Status events have `timestamp`, `type`, `sourceID`, `targetID`, `abilityGameID`; optional `duration` (milliseconds), `stack` (singular), and `extraInfo`.
- Initial `combatantinfo.auras` entries have `ability`, `source`, `stacks` (plural), and `name`; they do not establish duration or a fresh application.
- `sourceID: -1` is an unknown-source sentinel and is retained.
- `sourceResources` and `targetResources` may contain `x` and `y`. Status events commonly have neither; cast/damage/heal events supply sparse position observations.
- Pagination can return slightly more events than the requested limit to finish a timestamp group. The client enforces a total 200,000-event budget and 20-page budget, checks advancing cursors, and discloses incomplete reads.

The DTO's `Time` and `Duration` use seconds relative to pull start. `AbilityId` preserves the raw uint ID. `StatusId` is a candidate decoded only from the 1,000,001–1,065,535 aura namespace; `StatusIdVerified` remains false. The importer must validate the candidate against the installed game's Status sheet before using it in live rules. Observed examples include 1,000,048 (Well Fed) and 1,000,079 (Iron Will). Icons and name matching are never used to invent an ID.

`Parameter` remains null. `Stacks` and raw `ExtraInfo` are kept separately: neither proves equivalence to the live client's Status.Param. Missing or invalid durations and stacks remain null. `Complete` means all requested event pages were read; it does not imply continuous position/status coverage. Positions remain source centicoordinates and require calibration before plan overlay.

Primary references:

- [Report events schema](https://www.fflogs.com/v2-api-docs/ff/report.doc.html): event pagination and `includeResources`; the events schema is explicitly not frozen.
- [ApplyOrRefreshEvent](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.ApplyOrRefreshEvent.html): report-relative millisecond timestamps, duration, and extraInfo as extra numeric information.
- [Position](https://www.fflogs.com/scripting-api-docs/ff/types/RpgLogs.StaticMap.Position.html): event resource positions use in-game centicoordinates.

The scripting API documentation corroborates semantics, but its field names are not substituted for the observed v2 JSON names (`stack` versus scripting `stacks`, for example).
