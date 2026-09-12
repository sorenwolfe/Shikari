# Resolution-linked position checks

Continue the approved launch-readiness trajectory with passive Review comparisons. Keep version 0.9.5.0; no release or push in this milestone.

## Behavior

After validating a player's assignment, select its decision and a recorded calculated-damage effect (or an explicitly reviewed time). Compare the observed position with the unique authored destination only when player identity, seat, board geometry, occurrence evidence and landmark alignment are trustworthy. Use Near, Away and Unknown, never success/failure. Preserve missing data and sample age; do not interpolate or derive expectations from the observed movement.

Existing imports already request All events with resources. Retain typed damage/effect observations from that response, including packet and NPC instance identity. Calculated damage and later damage remain distinct. Older recordings without this additive channel remain readable and can be reimported.

For independently documented M12S Act 2 tower actions, also show same-event distance to the recorded tower center in yalms. This observation does not establish the strategy assignment, board orientation, safe travel or overall mechanic success. Chain helper positions are not tower centers.

## Implementation

1. Add bounded typed effect import, replay persistence and validation; exercise parsing, partial coverage, identities and round trips.
2. Add a pure destination evaluator with explicit checkpoint/alignment review, exact effect target sampling or a preceding sample no older than 0.25 seconds, nondegenerate landmark fitting and conservative ambiguity gates.
3. Add a detached cancellable position-check session. Publish only against the same assignment result, selected decision, attempt, options and plan/evidence revisions. Keep movement out of assignment case fingerprints.
4. Integrate into Review's existing validation panel: decision selector, effect search, manual checkpoint, calibration controls, source observations, proximity result and read-only board preview. Cancel on combat, edits, replacement, closure and disposal. Position checks remain session-local.
5. Add the anonymous real-pull resolution fixture and regression covering all eight observed tower effects; document independent sources and limitations.
6. Independently review, run all CI regression scripts and Release build, inspect packaged artifacts, document results and commit locally.

## Validation boundaries

- Reviewed occurrence evidence must be complete; a later rearm supersedes the earlier assignment.
- A selected effect must target the selected actor; delayed damage cannot substitute for the calculated snapshot.
- A source actor instance is retained separately from its actor ID.
- Calibration uses three to eight actual arena landmarks on the assigned board. No player destination may calibrate itself.
- Sample gaps, duplicate identities, ambiguous seats/tokens, edited geometry, degenerate fits and positions close to the error margin stay Unknown.
- Local frames require the latest frame to be valid; an older valid frame cannot hide a newer invalid one.
- Raw report responses and account credentials stay outside Git; commit only anonymized evidence.
