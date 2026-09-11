# Shikari: replay and observation foundation

This implements the first development milestone after the MMO Minion research. Work is local on `codex/launch-readiness`, based on the earlier readiness fixes at `be01295`. The advertised version remains **0.9.5.0**; no GitHub release has been published for this work.

## What changed

**Completed pulls process in the background.** Ending capture hands its recording to the ordered worker, which validates the data, prepares strategy enrichment, links the replay, serializes it once, and saves it. The framework then publishes the recording and applies the prepared strategy fields only if the active strategy still matches. Edits, replacement plans, a new pull, deletion, and clearing the library prevent stale application. Explicit plan saving remains synchronous and retains rollback on failure.

**Replay recovery stays ordered.** Metadata edits retain the recording's original retention priority, including edits queued while a newer recording is being processed. A failed replacement cannot evict the last durable recording. Disk failures remain visible independently of delayed successful analysis, and a successful retry or removal clears the corresponding failure. Shutdown drains pending recording work with a bounded wait and does not apply a late strategy update.

**Startup publishes complete data and cleans up acquired resources.** Action lookups see either the established loading fallback or a complete index, never collections being filled by another thread. Cancelled/failed construction cannot publish partial data. Plugin startup tracks acquired hooks, windows, commands, and disposable services; failure releases those acquisitions without running the normal shutdown saves. Normal disposal is idempotent, continues through individual cleanup failures, and preserves commands belonging to another plugin.

**Live casts and imported casts now share a richer recorded format.** The optional bounded cast collection preserves provenance, IDs, timing, and context. Older recordings still load with an empty collection. Invalid or omitted cast data marks the reference incomplete instead of silently claiming complete observations.

| Field | Live capture | FF Logs import |
| --- | --- | --- |
| Source identity | Game object IDs | Report-scoped actor IDs |
| Start time | Reconstructed from the visible bar | Explicit start, when supplied |
| Observation time | First polling observation | Supplied event time |
| Predicted end | Visible bar prediction, including casts already underway at pull start | Unknown |
| Observed completion | Unknown; disappearance is not completion | Explicit paired or completion-only cast event |
| Caster/target world position and heading | Snapshot when readable | Unknown in these fields |
| Actual damage/effect timing | Unknown | Not inferred from a cast event |

Capture samples, status observations, decisions, and recording duration now account for the encounter clock before startup snapshot work, preventing near-end cast observations from being clipped by a later recorder clock origin.

The existing local status collector uses entity IDs, while the new live context uses game object IDs. Their relationship, and correspondence with FF Logs report actors, still needs an explicit identity mapping before cross-source encounter resolution. This milestone does not claim that a dragon emoji or an arbitrary drawing has acquired a verified mechanic meaning.

## Performance evidence

The isolated benchmark compares the actual `ReplayStore` from `be01295` with the new implementation. Both compile against the same captured production helpers and game-service substitutes. It uses 6,000 board frames, 48,000 world positions, eight actors, and 20 authored mechanic occurrences: a synthetic ten-minute recording. An explicit worker gate excludes worker processing from the ending callback measurement.

| Median of three measured runs after warmup | Earlier store | New store |
| --- | ---: | ---: |
| End-of-pull callback | 933.1405 ms | 0.0568 ms |
| Allocation on the ending thread | 90.984 MiB | 680 bytes |
| Following settled framework update | 0.0020 ms | 0.3333 ms |

Every saved recording passed production validation and contained the expected data and one strategy attachment. Files were about 6.24 MiB. The worker still performs the expensive processing; it is no longer part of the ending callback. Baseline timings varied substantially between runs.

These are **synthetic measurements, not FFXIV frame timings**. The framework update uses a plan-save substitute and excludes the real synchronous plan-file write. Large-plan fingerprinting and durable plan saves still need a separate performance pass. Raw measurements and exact source snapshots are preserved in the task workspace under `work/replay-foundation`; the later cast-completeness checks do not change the measured end-callback path.

## Verification

The full Release build passed with zero warnings/errors, and all **38 CI regression scripts** passed. After the final review corrections, the Release build and all seven affected suites passed again: cast observations, replay, replay storage integration, FF Logs, evidence import, strategy merging, and strategy evidence. The three new scripts also run in the reusable build workflow required before tagged releases.

Coverage includes concurrent index reads, cancellation, 36 actual plugin-constructor fault boundaries, replay disk failures, blocked-worker ownership, retry/clear/delete ordering, retention races, changed-plan rejection, a new pull during analysis, and unload with pending work. Tests compile production code with controlled game/API boundaries; native ImGui and embedded-art suites also passed.

No live duty or authenticated live FF Logs report was used to validate this milestone. The production monitor and recorder have separate harnesses, with explicit populated cast-context coverage in the recorder. Full API parser hardening and a combined encounter-to-recorder fixture remain useful follow-ups.

## Next work

1. Move plan autosaves and durable enriched-plan writes off the framework thread with immutable snapshots, revision acknowledgements, and retry state. Reduce duplicate pull-start snapshots and bound loaded replay memory.
2. Add an explicit actor identity map and reviewed encounter profiles that connect actions/statuses to mechanic instances and branches. Start with M12S Caro, preserving authored strategy choices and source provenance.
3. Run live observations and imported recordings through the same resolver, then validate decisions against independent pulls, conflicting evidence, missing observations, and timing drift.
4. Feed one resolved personal instruction to the mini window, world marker, and Ember. Add attached/frozen mechanic geometry only when the profile supplies verified semantics.

This foundation supports those steps; it does not yet provide a complete automatic M12S solver or a 1.0 release candidate.
