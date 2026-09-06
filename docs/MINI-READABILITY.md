# Mini-window readability

The mini window now draws hazards and boss footprints beneath player markers, independently of saved item layers. Fills are limited to 22% opacity for readability; saved colours and the planner's rendering are unchanged. Grid lines are subdued. Enlarged waymarks, outlined position dots, and readable seat captions provide a foreground above the mechanic.

Captions move apart when seats stack. Thin leaders connect each caption to the exact planned position; the underlying token coordinates never move. At extremely small sizes, lower-priority captions that cannot fit without overlapping another caption are omitted while their position dots remain visible.

- **YOUR SPOT / cyan crosshair:** your unique planned destination.
- **YOU / white diamond:** your tracked current position.
- **IN POSITION / green ring:** within the configured tolerance, with a small exit margin to prevent boundary flicker.
- **OPTION:** your seat has multiple planned tokens; no unique destination or arrival is inferred.

The connection between YOU and YOUR SPOT indicates their relationship, not a computed safe path around hazards. Other tracked players use hollow circles. Focus mode subdues their live markers and annotated movement while keeping planned seat labels readable.

A solid footer separates status and instructions from game scenery. PLAN VIEW means live positioning is disabled or alignment is unavailable. If no seat resolves, it directs you to Plan > Roster. Alignment still requires matching waymarks; a planned crosshair alone does not establish a live position.

Validation: Release build against installed Dalamud SDK; layout tests in tests/mini.ps1; production mini marker/arrival code with a System.Drawing substitute in tests/mini-render.ps1; sharing, adaptive runtime, replay integration, and syntax regressions. The render preview uses synthetic positions and is not an in-game screenshot. Small-size label layout, live arrival, missing alignment, changed destinations, and ambiguous seats are covered.
