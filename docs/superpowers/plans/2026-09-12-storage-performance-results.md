# Storage and pull-start performance: results

Implemented after `c5fe0fa` on `codex/launch-readiness`. Version remains 0.9.5.0. This milestone is local; it does not publish or tag a release.

## Replay library

Startup now produces recording metadata instead of retaining every deserialized replay. It validates files sequentially, discards each full graph after extracting metadata, and preserves malformed or unsupported files. Opening a recording reads and validates that recording asynchronously. Deletion, clear, replacement and retention changes invalidate late results; catalog presence alone cannot supply evidence.

Replay decoding rejects trailing JSON, explicit null collections, invalid format versions and supplied malformed coordinate arrays/objects. It accepts legacy `{X,Y}` coordinates, compact arrays, legitimate zero values and optional null effect positions. The stricter coordinate reader is limited to replay files; shared strategy import formats are unchanged.

The resident cache normally holds at most three payloads under a 192 MiB estimated graph budget. The selected comparison pair is protected from ordinary eviction. Failed saves retain their only copies, with priority over durable cached recordings, and can exceed the byte target. Active and pending recordings reserve admission capacity; further imports, edits or recordings pause visibly before consuming the protected slots. Retry controls resume persistence without discarding that data.

The byte estimate describes resident payloads, not the entire process heap. Active capture, transient worker snapshots, deserialization, JSON strings, validation results and UI indexes have separate lifetimes. Requests and coalesced snapshots are bounded; this is not a claim that total process memory stays below 192 MiB.

Review uses metadata for its lists and exposes loading, failure and retry states. Timeline indexes retain at most two recordings and release evicted references. Assignment coverage loads recordings in sequence, preserving duplicate detection and the shared example cap; unavailable recordings are reported as exclusions. Cache activity does not increment logical evidence revisions.

## Persistence and analysis

Imported recordings and replay metadata edits use typed detached snapshots. Encoding and atomic writes run on the ordered storage worker, with pending edits to the same recording coalesced. Save acknowledgments clear only the corresponding unsaved revision. Old references cannot overwrite an evicted, deleted or replaced recording.

Learned timing history now uses detached snapshots and ordered, coalesced atomic persistence. Save and delete failures remain visible and can be retried. Unsupported or malformed history is protected from autosaves and shutdown until the user explicitly removes it. Parsing validates explicit null collections and invalid numeric samples before recomputation can normalize them.

Replay and learned-history storage use directory-specific OS ownership across plugin load contexts. An old in-flight operation finishes before the new instance can mutate those files; a bounded shutdown cancels old operations that have not started. The replay startup scan shares this ordering because its retention cleanup can delete files. Learned-history initialization also waits for ownership without blocking the constructor on an older writer. It does not save stale pre-reload history or learn a partial pull while initialization is pending.

FF Logs replay construction, metadata comparison and strategy preparation run on a worker. Game-sheet lookups are captured on the owner thread first. Reimport preserves reviewed seat mappings and arena landmarks only when their relevant strategy data still matches. Publication rechecks plan identity, revisions, replay identity and combat state. Replay admission occurs before live strategy mutation; strategy completion waits for the durable plan acknowledgment, and a failed current save restores the previous state without rolling back later edits.

## Pull-start snapshots

Adaptive guidance and Ember now independently capture only eligible rules and their destination identities. They do not copy drawings, collected evidence, the roster or the timeline. Both retain the previous first-128 candidate limit and conflict behavior. Replay still owns a full historical plan, copied directly without a JSON round trip. Captured coordinate precision and nested ownership are tested.

## Measured workloads

These warmed synthetic measurements use production classes with game-service substitutes. Allocation counters measure the calling thread. They do not measure FFXIV frames, whole-plugin memory or the later worker's encoding cost.

| Workload | Previous capture | Current capture |
| --- | ---: | ---: |
| Full historical plan with 2,400 drawing objects | 7,195,206 B | 1,019,832 B |
| Replay metadata capture: 6,000 eight-player frames and 48,000 positions, including validation | 44,126,784 B | 6,294,250 B |

The adaptive-only capture was 2,360 B with both the small and drawing-heavy fixtures. The full historical capture and long replay capture each reduced measured caller allocations by about 86%. The final replay run measured 203.76 ms for validation plus caller JSON encoding and 20.19 ms for validation plus typed capture. Timing varies with machine load; worker serialization still takes work and memory.

Reproduce the measurements with `tests/pull-snapshots.ps1` and `tests/replay-library.ps1`. The library test also confirms eight retained files start with zero resident payloads and that repeated opening keeps only three payloads while preserving the selected pair.

## Verification

- All 59 workflow regression scripts passed. Six new suites cover pull snapshots, learned persistence, replay loading, replay recovery, evidence preparation and the actual evidence UI workflow.
- After the final recovery fixes, affected persistence, lifecycle, replay, evidence UI and native rendering suites passed again. The final learned initialization cleanup was also rechecked independently.
- Release build: zero warnings and zero errors against the installed .NET/Dalamud SDK.
- Package: root DLL and manifest present, version 0.9.5.0 / API 15. Packaged and built DLL SHA-256 both `08BDC2697A4E910EFF9506A69E67988B888DEFC3FAA780768C08879D453F5BC5`.
- Independent review reproduced and corrected active-recording capacity exhaustion, stale replay saves, cross-reload write/delete ordering, stale learned-history initialization, explicit-null normalization and malformed coordinate handling.
- Slow/failed storage tests preserve prior durable data, coalesce queued edits, respect deletion barriers and expose retry state. New replay snapshots have a complete property-level deep-copy regression, including nested evidence and plan fields.
- No in-game soak, rendering session or real-duty frame-time measurement was performed. Native input/render checks and controlled service/UI substitutes passed; they do not establish actual raid performance.

## Remaining launch checks

- Measure actual update/draw and pull-transition timings in FFXIV with large boards, maximum retained recordings, repeated wipes and all optional windows.
- Exercise real addon reloads, upgrade recovery and disk failures. Bounded shutdown can report unsaved work; it cannot make unavailable storage durable.
- Startup still validates all candidate files. An indexed catalog could accelerate scanning, but must preserve malformed-file recovery and reliable retention ordering.
- Learned-history file parsing still uses the existing synchronous path after acquiring directory ownership without waiting. Moving a very large timing library's initial read off the owner thread remains a profiling-driven follow-up.
- Strategy revision fingerprints, detached graph capture and per-recording coverage indexing still have owner-thread costs. The coverage scheduling budget is not a guaranteed frame-time ceiling.
- Prove the full reviewed M12S Caro import-to-personal-cue workflow with independent arena landmarks and additional pulls. Storage improvements do not establish mechanic correctness on unseen data.

Cast source-instance/completion-target identity and player-target filtering before the FF Logs effect cap are covered by the [September 19 follow-up](2026-09-19-cast-effect-identity.md). The next intelligence work is a reviewed encounter workflow that records the cues actually delivered during a pull.
