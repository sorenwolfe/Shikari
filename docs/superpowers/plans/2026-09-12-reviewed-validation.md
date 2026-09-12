# Reviewed validation cases implementation plan

> **For agentic workers:** Use subagent-driven development and production-source tests. Continue in the existing isolated worktree; the user approved continuing the validation trajectory.

**Goal:** Preserve independently reviewed expectations and distinguish evidence readiness for each mechanic occurrence, then inspect the user's nominated FF Logs pull as a candidate real case.

**Architecture:** Extend the chronological runner with occurrence coverage records, keeping unknown encounter scope and unusable recordings as global gates. A bounded asynchronous local library stores expectations and fingerprints of the exact tested inputs. Review loads compatible expectations only by explicit user action; no new live call or destination behavior is introduced.

**Tech stack:** C#, Dalamud ImGui, Newtonsoft.Json, atomic local file replacement, PowerShell regression harnesses.

**Spec:** Continuation of `2026-09-11-pull-validation-results.md` and the user's instruction to continue this path. User nominated report `yaP6A3hwN7b8KnQJ`, fight 9, for verification; its encounter and strategy must be checked separately.

## Constraints

- Version remains 0.9.5.0; do not push, tag or release.
- A missing or uncertain occurrence cannot establish agreement. Unrelated incomplete windows must not invalidate an independently complete occurrence. Competing rules that can affect a decision remain part of its evidence requirements.
- Global source, identity, scope, malformed timing and recording limits still prevent successful comparisons. Retain unknown log durations/parameters and chronological event availability.
- Reviewed expectations are independently entered. Saving, loading and removing a saved case are explicit actions. The library does not enable rules or produce expectations from simulated outcomes.
- Store at most 64 cases in a versioned file of at most 4 MiB. Retain the original recording separately; the library must not duplicate player names, report URLs, frames or positions. A saved case is useful only with its original recording and matching tested plan/evidence.
- Bind cases by SHA-256 of exact tested inputs and actor/territory. Evaluate the fingerprint on the existing detached worker. Synthetic polling/gap selection is intentionally excluded so the same reviewed assignment can be stress-tested.
- Corrupt or newer-version libraries are not silently replaced. Publish items only after durable writes; errors remain visible and failed writes preserve the last good library.

## Tasks

- [x] **Occurrence coverage:** Extend `PullValidationRunner.cs` with `HasOccurrenceReadiness`, `EvidenceUsable` and occurrence records (rule, action, occurrence, start/deadline/end, completeness and reasons). Adapt `PullValidationComparison.cs` to gate each row. First test separate complete/incomplete windows, incomplete competitors, gaps during settling, unfinished/rearmed windows, and legacy/global guards.
- [x] **Saved expectations:** Add `PullValidationCases.cs` and `PullValidationCaseStore.cs`. Implement detached validated case creation, compatibility hashes and bounded ordered asynchronous load/save/delete. First test reload, changed plan/evidence/actor, malformed/versioned data, failed writes and nonblocking publication.
- [x] **Review integration:** Compute and publish `CaseFingerprint` with the existing validation session. Add occurrence evidence summaries and saved-case controls in a separate UI partial. Require a case name and review note, show exact-input incompatibility, and allow explicit compatible expectation loading. Test the actual UI save/reload/load path and edit/combat guards.
- [x] **Real source check:** Verify the supplied fight via the existing FF Logs client where public access is unavailable. Inspect casts/statuses before judging suitability. Read existing Shikari configuration only for using its configured credentials; never print credentials or token responses. Keep report data out of Git and do not manufacture expected outcomes or claim Caro strategy from encounter identity alone.
- [x] **Finish:** Register tests in CI, update user guidance, run focused and appropriate broad regressions, build Release, inspect archive, obtain independent review, and save a local commit with a results report.

## Acceptance

A reviewed expectation survives restart and can be applied only to a matching selected recording/actor/plan. A gap in a later mechanic leaves an earlier complete mechanic comparable; incomplete competing evidence cannot falsely validate a conflict outcome. Review explains missing evidence per occurrence. No saved case or report inspection silently promotes live rules or validates an assignment against its own generated answer.
