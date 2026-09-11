# Pull validation implementation plan

> **For agentic workers:** Use subagent-driven development and production-source regression harnesses. The user approved the preceding Validate pull proposal; proceed through implementation and verification locally.

**Goal:** Replay a complete plan's adaptive assignment rules chronologically against an imported or local pull, inspect its decisions, and compare them with recorded behavior and reviewed expectations.

**Architecture:** A detached input and virtual clock drive the existing AdaptiveEngine on a background worker. Results remain in Review and never call live services. A separate comparison layer evaluates rule/outcome identity and timing; missing evidence is explicit. Existing Review playback previews the decision's saved board and a clearly labeled assignment cue.

**Tech stack:** C#, Dalamud ImGui, Newtonsoft.Json, PowerShell production-source regression harnesses.

**Spec:** User-approved proposal in the conversation immediately before this plan, building on `2026-09-10-plan-saving-results.md`.

## Constraints

- Continue in the existing isolated `codex/launch-readiness` worktree. Keep version 0.9.5.0; no push or tag requested.
- Freeze inputs before worker execution; cancellation and revision checks discard stale results. No live navigation, reminders, sound, plan edits or API calls from validation.
- Run all eligible rules together; disabled rules remain disabled unless explicitly included for testing. Preserve overlap suppression, simultaneous conflicts and later-cast rearming.
- Casts arrive at observed time when available, retaining reconstructed start only as the anchor clock. Never use an event before its availability time. Mask FF Logs status duration/parameter fields whose live-time provenance is not established.
- Baselines, unavailable periods, incomplete imports, unknown territory and duplicate actor/anchor identity cannot produce a validation success claim. Synthetic delayed-polling and observation-gap scenarios are labeled.
- Recorded decisions exist only for the recorded local player. Comparing another actor, edited rules, or a different pull cannot establish live equivalence.
- Expected assignments must be user-reviewed inputs, never generated from the candidate's own decision. Expectations remain session-local, bound to the selected recording, actor and tested plan.
- Preview assignment boards and cue text separately from actual recorded navigation. Full reminder, director and Ember animation replay is outside this first validator; do not call it a full game simulation.

## Tasks

1. **Chronological runner:** create `Services/Replay/PullValidationRunner.cs`, options/result types and `tests/pull-validation.ps1`. Test competing rules, rearming, delayed casts, status baselines/gaps/removals, unknown FF Logs fields, no mutation, cancellation and bounded input before implementing. Run focused harness to green.
2. **Comparison:** create `Services/Replay/PullValidationComparison.cs` and tests. Compare exact rule/action/occurrence/outcome identities, expose timing deltas, missing/unexpected/ambiguous decisions and incompatible rule snapshots. Compare independently supplied expectations separately from live decision reproduction.
3. **Review session and UI:** create detached capture/cancellable session helper and `UI/MainWindow.PullValidation.cs`. Add Validate pull, actor/plan/scenario selection, disabled-rule opt-in, readable bounded results, seek/preview controls and session-local expectation editing. Invalidate on edits, metadata revision, attempt removal/switch, combat, close or disposal. Test real helper/UI partial against controlled worker/ImGui boundaries.
4. **Preview:** draw the tested decision board with an assignment cue in a separate bounded child. Existing recorded map stays available alongside it. State explicitly that displayed cue is a preview, not the exact recorded director or Ember output.
5. **Verification:** add new scripts to CI, build Release, run every workflow regression, obtain independent review, check whitespace/archive and document limitations. Commit the verified local milestone without publishing.

## Acceptance

- An imported pull or retained recording can be validated through Review without starting a live fight.
- The full-plan engine receives one chronological stream; tests demonstrate behavior that differs from the old isolated-rule simulation.
- Failure modes remain unresolved, and the evaluator never observes future-derived log fields as live values.
- A selected simulated decision seeks the recording and exposes its board and readable cue without changing the active strategy or live plugin state.
- Users can distinguish recorded-decision agreement, reviewed-expectation agreement, stress-test behavior and missing coverage.
