# Pull validation: completed milestone

Implemented on `codex/launch-readiness` after `d480caa`. Version remains 0.9.5.0. This is a local development checkpoint; no release or tag was created.

## Using it

Open **Review**, select a retained local recording or imported FF Logs pull, and expand **Validate pull**. Choose a player, the recording's plan or the current edited plan, and the observation scenario. Disabled rules can be included explicitly for testing without enabling them during combat.

The simulator runs all eligible assignment rules together. Select a decision to seek the recording and preview its authored board and assignment cue. Decisions at the same timestamp remain individually selectable. The existing replay map remains available for inspecting recorded movement.

**Compare recorded decisions** compares the original local player's Shikari decisions when the recording and tested rule/board definitions are compatible. **Reviewed expectations** accepts independently reviewed outcomes for specific rule occurrences. It never derives the expected answer from the simulator's own output. Expectations remain session-local and clear when the inputs change; unreviewed occurrences remain unknown.

## Implementation and evidence boundaries

- A virtual clock drives the production `AdaptiveEngine`, including overlap exclusion, cross-rule conflicts, status settling, timeouts and cast rearming. Its new ingest-only method separates chronological observation delivery from evaluation; the existing live `Update` path retains its behavior.
- Casts become available at their recorded observation time while retaining their start time as the assignment anchor. FF Logs duration and parameter fields are masked when their live-time provenance is unverified, so a duration reconstructed from a later removal cannot influence an earlier decision.
- Normal evaluation uses a 0.1-second clock. The slower-polling scenario evaluates every 0.25 seconds while retaining observation arrival times. A synthetic one-second observation gap invalidates carried evidence. Refreshes cannot silently turn pre-gap statuses into fresh evidence, including when a refresh omits the original source ID.
- Missing relevant status history, baseline-only observations, incomplete acquisition windows, ambiguous actors/anchors and unverified encounter scope prevent a successful comparison. Choosing a test territory filters rules; it does not verify the recording's encounter. Unrelated instant casts and unknown unrelated auras do not invalidate otherwise usable evidence.
- Comparison validates rule/action/occurrence identity and branch/board correspondence. Outcome agreement and timing differences are separate. Recorded navigation and whether a decision was applied are historical information, not claims that navigation was replayed.
- Detached typed snapshots feed a background worker that performs both simulation and recorded comparison. Plan edits, evidence revisions, object replacement, player/pull changes, combat, cancellation and disposal discard stale work. Validation does not edit the source plan or call live navigation or reminder services.

## Verification

The Release build passed with **zero warnings and zero errors**. All **47 CI regression scripts** passed, including four new suites for chronological validation, comparison, session lifecycle and Review UI integration. The first broad run encountered a PowerShell compiler `OutOfMemoryException` in the unchanged buddy-window harness; that test passed unchanged in isolation, and all 16 remaining scripts then passed. No production change was made for the compiler failure.

The new tests compile production sources with controlled service/UI boundaries. They exercise full-rule conflicts, rearming, event availability, unknown log values, evidence gaps, independent expectations, malformed references, detached inputs, cancellation, stale-result rejection and the actual Review controls. The UI checks include exact selection of simultaneous decisions and a read-only board preview with a positive drawing width. Existing adaptive runtime, persistence, imports, symbol rendering and native buddy input checks also passed.

Independent review closed with no remaining material findings after fixes for incomplete evidence being treated as agreement, unrelated log events blocking validation, invalid branch references, simultaneous-decision selection and source-less refreshes across gaps. Whitespace checks passed. The packaged archive contains the DLL and manifest at its root and agrees with repository metadata: **0.9.5.0, Dalamud API 15**.

## Practical limits and next validation

This milestone validates assignment decisions for one selected actor per run. It does not reproduce the complete game, manual slide holds, timeline reminder delivery or Ember animation. The preview assumes following is enabled. Matching player movement or an assignment does not establish that a mechanic succeeded.

Readiness is conservative across the whole run: an incomplete relevant window can leave comparisons unknown even when other candidate decisions are visible. Expectations are not yet persisted as reusable fixtures. No authenticated FF Logs pull or live duty was used to establish real encounter accuracy in this milestone.

The next evidence step is to review known assignments from retained real pulls, compare them with the simulator, and preserve those reviewed cases as regression fixtures. Per-occurrence readiness can then separate a well-observed mechanic from incomplete portions elsewhere in the pull without weakening evidence requirements.
