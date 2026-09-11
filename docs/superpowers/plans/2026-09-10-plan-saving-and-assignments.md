# Asynchronous plan saving and assignment evidence

> **For agentic workers:** Use superpowers:subagent-driven-development and production-source regression tests for each task.

**Goal:** Keep plan writes off routine framework updates and make recorded cast targets and verified assignment statuses usable in Review.

**Architecture:** Capture detached plan graphs on the caller and encode/write them on one ordered worker. Save tickets distinguish durable success, failure and supersession. Live plan mutation and acknowledgment remain on the framework thread. Cast actor resolution is scoped to one recording, and an Act 2 decoder interprets only verified positive status evidence.

**Tech stack:** C#, Dalamud API 15, Newtonsoft.Json, PowerShell regression harnesses.

**Spec:** `2026-09-10-foundation-results.md`, following the user's approval to continue the mapped plan.

## Constraints

- Preserve version 0.9.5.0 and keep development local; no release/tag/push requested.
- Never serialize mutable live plan graphs on a worker. Snapshot IDs, nested collections, symbols, source metadata and rule/evidence fields without loss.
- Retain explicit synchronous Save/SaveActive durability barriers for existing import/library actions. Autosaves and automatic evidence writes use the new asynchronous path.
- A superseded save never acknowledges newer edits. Failure preserves the previous durable file and exposes retry state for that plan.
- Save, delete, replacement and shutdown must share ordered ownership; no deleted plan resurrection.
- Async enrichment may roll back only if the plan still matches the applied proposal. Preserve newer edits after failure.
- Resolve live game object IDs through an explicit captured entity mapping; FF Logs IDs remain report-scoped. Never guess by job/name or reuse another recording's cast.
- The first encounter interpretation is passive Act 2 buff assignment evidence. It does not enable live routing, assign a world destination, or claim mechanic success.

## Tasks

1. **Plan persistence core** — `PlanStore`, typed `PlanSnapshot`, ticket/state types, production storage tests. Confirm nested snapshot isolation, coalescing, acknowledgment ordering, durable failures/retries, delete/reimport, and bounded drain.
2. **Editor saving** — `EditorAutosave`, MainWindow update/close/history/status integration and focused state-machine tests. Track user edit revisions separately from save revisions; show pending/saving/failure accurately and allow retry.
3. **Async enrichment** — `StrategyMergeSession`, ReplayStore completion handling, Plugin teardown and integration harnesses. Queue detached snapshots, poll completion, and roll back only unchanged failed proposals.
4. **Actor identity** — `ReplayEvidence`, local capture, `EvidenceActorIdentity`, validation and production capture tests. Link actual stored casts to unique actors and preserve legacy unknown fields.
5. **Caro evidence interpretation** — primary-source research, pure `EncounterAssignmentDecoder`, conflict/gap/occurrence tests, and passive Review presentation with cast target navigation. Cite reviewed source revisions and retain the distinction between assignment identification and strategy-specific positioning.
6. **Verification** — add new suites to CI, Release build and complete regression suite, independent review of storage ordering/rollback and decoder scope, targeted rechecks after fixes, measured snapshot overhead, and a local checkpoint/report.

## Acceptance

- Slow storage cannot block periodic autosave or completed-pull plan persistence.
- Edits made during saving remain dirty until their own successful acknowledgment; failed retries remain visible.
- Concurrent or delayed writes cannot restore a deleted or replaced plan.
- Selecting a cast target in Review shows that recorded actor's evidence at the observation time.
- Act 2 assignment labels require the actual anchor and matching fresh statuses in the same recording; ambiguity produces an unresolved result.
- Existing imports, adaptive routing, mini, Ember, sharing, and persisted formats remain compatible.
