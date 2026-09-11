# Plan saving and assignment evidence: completed milestone

Implemented on `codex/launch-readiness` after `b80712c`. The advertised version remains 0.9.5.0. This is a local development checkpoint, with no release or tag.

## Behavior

- Periodic editor saves and automatic completed-pull strategy saves capture detached typed snapshots. One ordered worker handles JSON encoding and disk writes. Explicit library/import saves retain their existing durable barriers.
- Save tickets distinguish success, failure and supersession. The editor tracks edit revisions separately, so an older completion cannot acknowledge a newer edit. The header shows pending/saving/error states and exposes retry.
- Save/delete/replacement operations share ordering. Only the owning thread publishes timestamps or changes live plans. A failed automatic enrichment rolls back only if the plan still matches the proposal and no newer save, active plan or pull superseded it.
- Normal unload snapshots the final library and bounds its storage drain to two seconds. Startup failure disposes the acquired queue without saving incomplete state. A timeout remains an unsaved-changes error; it does not claim that pending work finished.
- New local recordings retain explicit game-object to entity-ID relationships. Imported FF Logs cast IDs use actors from the same report recording. Missing, conflicting, invalid and legacy identity stays unknown.
- Review can select the actual recorded cast target and seek to its observation time. Expected cast-bar end and observed completion remain separate. The selected player's status and position evidence use the same actor key.
- A passive Grotesquerie: Act 2 decoder labels all eight number/Bonds combinations. It requires an actual selected Act 2 anchor, fresh active statuses and complete evidence for the same actor. Baselines, gaps, expiry, missing/conflicting statuses and later Grotesquerie phases suppress resolution. It does not choose a destination, advance a slide or judge mechanic success.

## Source basis

The [WTFDIG Caro guide source](https://github.com/mczub/wtfdig/blob/2262bfea410539c9d2465a03b67094bc4c1f7761/src/routes/74/m12s/data.ts#L4555) describes numbered A/B assignments. The action/status associations were corroborated against pinned public [BossMod ID declarations](https://github.com/awgil/ffxiv_bossmod/blob/ecf7a1d2d4a694bf177fc5a4b7966e0f6524f2ad/BossMod.Modules/Dawntrail/Savage/RM12S1TheLindwurm/RM12S1TheLindwurmEnums.cs) and its [Act 2 handler](https://github.com/awgil/ffxiv_bossmod/blob/ecf7a1d2d4a694bf177fc5a4b7966e0f6524f2ad/BossMod.Modules/Dawntrail/Savage/RM12S1TheLindwurm/GrotesquerieAct2.cs). No implementation was copied.

| Meaning | Decimal IDs |
| --- | --- |
| Act 2 assignment cast | Action 48830 |
| Bonds A / B | Status 4752 / 4754 |
| I / II / III / IV | Status 3004 / 3005 / 3006 / 3451 |

Number is derived from the distinct status IDs, never stacks, duration or parameter. These are source-corroborated mappings with synthetic regression coverage; actual Shikari pull validation remains outstanding. Territory identity and coil/tower geometry have not been inferred from these IDs.

## Verification

The Release build passed with zero warnings and errors. All **43 CI regression scripts** passed, including five new suites: plan persistence, editor autosave, actor identity, cast evidence UI and encounter assignment. Existing reliability checks also passed independently.

Tests compile production sources with controlled service/API substitutes. Coverage includes recursive equality and reference isolation throughout the plan graph; blocked writes; coalescing; explicit barriers; locked-file preservation/retry; stale same-ID objects; delete/reimport; bounded drain; delayed acknowledgments; newer edits and saves during automatic enrichment; all 36 startup fault boundaries; actual target-selection UI behavior; and eight synthetic Act 2 combinations with negative cases. Independent reviews found no outstanding issues in persistence or actor/profile integration.

### Synthetic snapshot measurement

Measured in the local PowerShell runtime with the actual model, snapshot and JSON settings, after five warmups and over 30 samples. Input: 40 slides, 4,000 objects, four references, 400 mechanic records and 3,200 actor observations, each with four statuses and three positions.

| Caller-side operation | Median | p95 | Median allocation |
| --- | ---: | ---: | ---: |
| Typed snapshot | 5.82 ms | 26.83 ms | 3,826 KiB |
| Previous JSON encoding | 54.62 ms | 125.17 ms | 18,370 KiB |

This excludes disk IO and does not measure FFXIV frame rates. JSON encoding still runs on the worker. Pull-start fingerprints, explicit import saves and other large-plan copies remain separate optimization opportunities.

## Next milestone

Validate the Act 2 mapping against retained real pulls, measure status acquisition/removal timing, and report coverage per assignment before adding a reviewed disabled encounter profile. Shared live/replay resolution should preserve unknown states and authored strategy choices. Precise destinations require verified arena orientation and later mechanic events; the initial assignment cast's predicted end is not the tower or chain resolution time. Reduce duplicate pull-start snapshots and bound loaded replay memory alongside that work.
