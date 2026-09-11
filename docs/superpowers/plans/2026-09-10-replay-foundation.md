# Replay and observation foundation implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development to implement and review the independent tasks below.

**Goal:** Remove expensive completed-recording processing from the game thread, make startup failure recoverable, and retain richer cast context for future encounter interpretation.

**Architecture:** A finished recording transfers exclusively to the existing ordered storage worker. Analysis stages changes against the captured strategy, then the framework commits the prepared result only against an unchanged active plan. Live and imported casts retain provenance and optional observed context without treating expected cast endings as verified effects.

**Tech stack:** C#, Dalamud API 15, Newtonsoft.Json, PowerShell production-source regression harnesses.

**Spec:** `2026-09-10-launch-readiness.md`, together with the user-approved MMO Minion research trajectory.

## Constraints

- Keep version 0.9.5.0 during local development; no release or push in this task.
- Preserve existing saves, authored strategy choices, disabled imported drafts, and incomplete-data safeguards.
- Game services and mutable live plans stay on the framework thread. Workers exclusively own detached recordings and staged plans.
- Disk writes, deletion, clear, retention, and finalization share one ordered queue. A failed replacement must preserve older durable evidence.
- A deleted recording, cleared library, changed strategy, or new active pull must not receive stale background results.
- Keep synchronous explicit plan-save success semantics; editor autosave coalescing and full asynchronous plan persistence are a separate follow-up.

## Task 1: Atomic action index publication

Files: `Services/ActionIndex.cs`, `tests/action-index-tests.cs`, `tests/action-index.ps1`.

- [x] Hold actual production construction mid-sheet and exercise every public read; confirm the partial publication failure.
- [x] Build privately, check cancellation within enumeration, and atomically publish one complete snapshot.
- [x] Verify loading fallbacks, cancellation, search ranking, job membership, and post-publication reads.

## Task 2: Background completed-recording processing

Files: `Services/Replay/ReplayStore.cs`, `StrategyMergeSession.cs`, replay integration and strategy-merge harnesses.

Interfaces: `StrategyMergeSession.Prepare(ReplayAttempt)` produces a detached prepared result; `Commit(PlanDocument, prepared, Func<bool>)` rechecks the captured strategy and rolls back on save failure. Existing `Apply` remains the synchronous import wrapper.

- [x] Add controlled queue-blocking regressions: finishing a pull must return while the worker is blocked, and no unfinished recording is visible.
- [x] Transfer ownership at finish; trim and validate evidence, prepare enrichment, link the recording, serialize once, and persist on the worker.
- [x] Publish completed results on framework updates; prevent stale application after a plan edit, plan switch, delete, clear, or new pull.
- [x] Preserve ordered storage and failure recovery tests; verify shutdown persists a final capture without applying a plan edit.

## Task 3: Cast provenance and observed context

Files: live cast event/monitor, replay model/buffer/validation, log replay adapter, focused observation harnesses.

- [x] Add actual-production tests for old recordings, nullable context, invalid numeric data, and distinct live/log provenance.
- [x] Capture source/target identity and available world transforms at the event observation; keep observation time separate from reconstructed start time.
- [x] Preserve log completion only when explicitly paired with a completion event; never invent action-effect timestamps.
- [x] Bound persisted event context, wire both adapters, and exercise round trips and legacy files.

## Task 4: Recover failed startup

Files: `Plugin.cs`, focused lifecycle helper if needed, startup regression harness.

- [x] Inject failures at actual production construction stages and command registration.
- [x] Track acquired resources and subscriptions; reverse cleanup on failure without saving partial initialization.
- [x] Keep normal unload idempotent and resilient to individual cleanup exceptions; do not remove commands owned by another plugin.

## Integration and review

- [x] Add new regression scripts to the reusable verification workflow.
- [x] Build Release against installed Dalamud SDK and run every CI regression script.
- [x] Independently review ownership, asynchronous cancellation/deletion, backward compatibility, and lifecycle cleanup.
- [x] Document measured callback behavior and remaining work: asynchronous plan persistence, bounded replay memory, encounter profiles, shared resolver, and in-game validation.
