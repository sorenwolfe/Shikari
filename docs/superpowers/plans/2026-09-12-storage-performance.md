# Storage and pull-start performance implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development. Complete and verify each independent task before integration.

**Goal:** Keep the plugin responsive and its retained data recoverable with large strategies and replay libraries.

**Architecture:** Preserve the existing ordered recording worker and atomic file replacement. Separate a lightweight replay catalog from a small resident payload cache, load selected recordings asynchronously, and stream coverage inputs. Use detached typed snapshots for mutable data crossing threads. Keep logical evidence revisions independent of cache activity. Continue on the existing isolated launch-readiness branch; do not publish or change version 0.9.5.0.

The user approved the performance/storage pass after the release-readiness review. This plan refines that approved scope without another approval stop.

## Tasks

- [x] Add immutable replay catalog metadata, load states and bounded cache admission. Keep at most three ordinary loaded recordings under a documented estimated-memory budget. Protect the selected pair and unsaved payloads. Reject additional admission explicitly when protected data fills the budget rather than silently losing a failed save.
- [x] Build the startup catalog by validating files sequentially and immediately releasing full graphs. Preserve corrupt/future files for recovery and previous durable retention on failed saves. Retain metadata only in completed startup task results. Disk scan acceleration through indexed sidecars is deferred until it can preserve these validation guarantees.
- [x] Load recordings on demand with generation checks around replacement/deletion/clear. Catalog presence never substitutes for fully validated evidence. Cache loads and evictions do not invalidate unchanged logical evidence.
- [x] Change Review menus and import lookup to catalog metadata; load primary/comparison payloads explicitly and expose waiting/retry. Restrict evidence timeline indexes to the selected pair. Stream assignment coverage one recording at a time without losing deduplication or example limits.
- [x] Replace caller-side replay JSON encoding for metadata/import saves with detached typed capture and ordered/coalesced worker serialization. Preserve original retention ordering, failed-save retry and durable acknowledgements. Bound queued snapshots and preserve only-copy unsaved data.
- [x] Replace learned-history direct writes with typed snapshots and ordered/coalesced atomic persistence, visible errors, deletion barriers and bounded shutdown.
- [x] Replace whole-plan JSON copies in adaptive/Ember startup with small independent rule snapshots. Use typed historical plan copies for replay/enrichment; preserve stale-plan fingerprints and immutable ownership.
- [x] Add targeted regressions before implementation for lazy loading, cache bounds/pins, stale results, failure/retry, mutation isolation, ordered deletion, recovery, streaming coverage and unchanged assignment behavior. Measure representative snapshot/storage paths.
- [x] Integrate, independently review, run the full workflow suite and Release build, inspect the package, document measured results and remaining in-game checks, and commit locally.

## Acceptance boundaries

No fake replay payloads, silent cache-only coverage reports, eviction of unsaved data, mutation shared across a worker and UI, or stale load resurrection. A saved-file catalog may describe availability, but only a loaded and validated recording can supply mechanic evidence. Budget figures describe retained cache estimates, not a guarantee about process-wide GC memory. Existing live guidance and unknown-evidence behavior must remain consistent.
