# Resolution-linked position checks: results

Implemented after `3abe07c` on `codex/launch-readiness`. Version remains 0.9.5.0. This is a local milestone with no publication or tag.

## Review behavior

The existing Validate pull panel now lets the player select an assignment and inspect a recorded calculated-damage effect or an explicitly reviewed checkpoint time. Effect selection preserves the exact original event index, filters to the selected player, and pauses the replay. Calculated damage and subsequent damage remain separate observations.

The position comparison runs over detached inputs in a cancellable background job. Results bind to the assignment result, selected decision, recording, options and edit/evidence revisions. Edits, combat, replacement, cancellation, window closure and disposal discard stale work. Position histories remain outside assignment-case fingerprints; position comparisons are session-local.

The result reports Near, Away or Unknown relative to one authored destination. It requires a resolved assignment, usable occurrence and encounter evidence, one mapped player and token, and unchanged board/roster semantics. Later assignments or cast rearms can supersede an earlier destination. Contradictory snapshots for a correlated effect remain unknown.

Manual checkpoints use only the latest preceding sample within 0.25 seconds. A newer invalid frame cannot be replaced with an older valid one. Local frame identity uses captured player name, job and local-player flag rather than a mutable plan-seat mapping. Missing identity or duplicate names remain unknown. The unrecorded local alignment residual is disclosed; the 0.02 board-unit comparison margin is not represented as a measured error bound.

FF Logs positions need three to eight actual arena landmarks on the assigned board. The evaluator rejects collinearity, clustered or narrow landmarks, excessive fit error and distant extrapolation. The largest observed landmark residual is a diagnostic margin, not statistical confidence. Borderline proximity remains unknown. No player's observed destination is used to calibrate itself.

Position alignment can be entered, inspected and corrected even after changing a landmark invalidates the previous assignment result. Alignment edits persist replay metadata without also invoking strategy enrichment. The comparison canvas is read-only, with movement guide lines and independent settled feedback disabled.

## Imported effect observations

The existing All-events request already includes resource snapshots. The parser now retains typed calculated-damage and damage events with action, source/target IDs, source/target instances, nullable packet identity and nullable source/target positions. Packet IDs are parsed as exact integers; packet zero is valid. Missing optional fields remain unknown.

The additive channel is capped at 32,768 events and has its own completeness flag. Replay storage retains only targets already established as participating players; effects alone do not create roster members. Old recordings remain readable with an absent effect channel. Reimporting an old FF Logs fight adds the new evidence.

The parser cap currently applies before player-target filtering. A dense, long pull may exhaust it on outgoing events and leave effect comparisons unknown. Filtering against independently established party actors before this cap is follow-up work; the available data did not establish a full-fight event count. The complete targeted real window contained 904 raw events, including 362 typed damage observations, and parsed without warnings.

## Observed M12S Act 2 evidence

The same report's [fight 2](https://www.fflogs.com/reports/yaP6A3hwN7b8KnQJ?fight=2), Lindwurm P1 / encounter 104, contains the relevant mechanic. The originally supplied fight 9 is Lindwurm II. This milestone adds a second anonymous fixture for the Act 2 resolution observations, with player aliases matching the earlier assignment fixture. Raw API responses and account credentials remain outside Git.

All eight observed tower recipients match the independently reviewed number/Bonds assignments and tower order III–IV–I–II. Each selected calculated-damage event includes the tower and target coordinates at the same timestamp. The matching later damage events arrive 488–491 milliseconds later, when the player may already have moved. Other party members have sparse samples; the event's own target resources avoid substituting those gaps.

The narrowly scoped `TowerEffectObservation` helper supports only documented tower actions 46259 and 46263 in encounter 104. The measured source-to-target distances in this single pull are 0.269–2.725 yalms, within the independently documented three-yalm tower radius. This is observed proximity, not proof that the correct assignment, movement route or entire mechanic succeeded. Chain helper coordinates for action 46260 are explicitly unsuitable as circle centers.

The fixture cites the pinned [WTFDIG guide](https://github.com/mczub/wtfdig/blob/2262bfea410539c9d2465a03b67094bc4c1f7761/src/routes/74/m12s/data.ts#L4555), [BossMod mechanic implementation](https://github.com/awgil/ffxiv_bossmod/blob/ecf7a1d2d4a694bf177fc5a4b7966e0f6524f2ad/BossMod.Modules/Dawntrail/Savage/RM12S1TheLindwurm/GrotesquerieAct2.cs), and FF Logs [calculated event](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html) and [coordinate](https://www.fflogs.com/scripting-api-docs/ff/types/RpgLogs.StaticMap.Position.html) documentation.

No real Caro board orientation, later coil route or independent arena calibration was established. Production decoding does not automatically turn these observations into live instructions.

## Verification

- All 53 workflow regression scripts passed, including four new effect/position suites and expanded tests of the actual Review UI controls.
- After the final correlated-packet fix, the destination evaluator, detached session, Review UI and observed-resolution suites passed again.
- Release build completed with zero warnings and zero errors.
- `latest.zip` contains the manifest and assembly at its root; version 0.9.5.0 / API 15. The packaged DLL's SHA-256 matches the final build.
- Independent review reproduced and corrected local identity remapping, different-rule supersession, clustered landmark geometry, invalid effect identity, valid zero packet IDs and contradictory packet snapshots.
- No in-game rendering or live-duty test was performed. UI tests exercise production controls with API substitutes; the existing Ember native input tests also passed.

## Next useful work

Collect more independent pulls and establish real arena landmarks before evaluating Caro destination adherence. Pinning recordings and replaying saved cases in a batch would make the validation library practical across future plugin updates. Cast pairing should also retain NPC source instances and completion target identity; this milestone's effect checks avoid depending on those older cast-pairing limitations.
