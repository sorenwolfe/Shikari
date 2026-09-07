# Strategy import intelligence implementation plan

**Goal:** A WTFDIG import produces editable strategy boards in one plan; a subsequent FFLogs import enriches that plan with traceable cast/status/movement evidence; mini view supports personal filtering and readable scrollable notes.

**Architecture:** Preserve authored board geometry and identity. Compose linked boards with guide slides through explicit source links. Add a deterministic evidence analysis layer using verified action IDs or unambiguous normalized cast names, occurrence, actor seat identity, and calibrated coordinate transforms. Keep uncertain observations visible, without enabling speculative live instructions.

**Tech stack:** C#/.NET 10, Dalamud ImGui, existing JSON importers, PowerShell regression harnesses.

## Requirements and decisions

- Work from release v0.9.0 in the existing isolated checkout. Preserve D:/Shikari's uncommitted files. No release or remote push is requested this turn.
- Screenshot source: https://wtfdig.info/74/m12s#caro, linked Replication board 9ncP6UIDURcWuRuO. Verify the source sprite geometry before translating ff-ring/ff-circle; aggregate unsupported-type diagnostics.
- Automatically fetch distinct linked boards, bounded and cancellable, after the selected strategy is determined. Keep optional role/variant choices, remove per-board import/copy buttons. Preserve per-board arena settings and guide context. Failed board fetches retain guide content and concise diagnostics.
- Your view removes other player markers/labels and other observed players, retaining hazards, waymarks and local destinations. Notes wrap in a capped scrollable panel; controls must actually receive input, including during pulls without swallowing clicks over the arena.
- Log attachment preserves existing slides and active-plan identity, enriches matching timeline entries and stores evidence once. Wrong-fight, ambiguous matches and stale async results must not overwrite plans.
- Sparse replay positions cannot establish spatial alignment. Use known calibration; show unresolved coordinate mapping explicitly. Correlation is evidence, not proof of a correct strategy. Draft adaptive branches only from complete, verified status observations and traceable destinations.

## Tasks

- [x] 1. Reproduce missing area imports from the real board; add regression tests for shape, size, rotation and coordinate alignment; implement supported aliases/geometry and concise diagnostics.
- [x] 2. Add persistent mini personal-view policy, bounded notes layout and practical mouse interaction. Test rendering/filtering and layout, then build against Dalamud.
- [x] 3. Add tested guide/board composition and bounded fetching; replace manual board UI with automatic preparation and one combined import. Preserve source provenance, arena overrides, roster mappings and guide-to-slide links.
- [x] 4. Add tested strategy evidence enrichment: match casts to guide/board mechanics, attach timing and actor/status/movement summaries, draft conservative observed branches where justified; integrate FFLogs and local replay UI with stale-plan checks and idempotency.
- [x] 5. Run relevant and full regression suites, full Release build, live-source conversion check, independent review; fix findings. Deliver reviewable patch/build and user-facing validation notes.

## Progress

Design authorized by the user's explicit build request. Existing v0.9.0 observations are manual-only; extend this flow rather than replacing the evidence recorder.

Validation: focused regressions and full Release build pass. Full Caro source composition retains 144 slides / 130 editable slides / 3148 objects. Review findings fixed. In-game and authenticated FF Logs checks remain user-client validation. Development build and patch prepared in outputs; no remote push.
