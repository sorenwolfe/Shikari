# Raidplan geometry evidence

Inspected 2026-09-07. The downloaded source was parsed as data, never executed.

Reproduction: [WTFDIG M12S Caro](https://wtfdig.info/74/m12s#caro) links to
[Replication board 9ncP6UIDURcWuRuO](https://raidplan.io/plan/9ncP6UIDURcWuRuO).
Its native JSON identifies that exact code, version 2, 21 steps (meta.step 0–20),
55 abilities (34 bosses, 16 rings, 3 circles, 2 donuts), and 10 pencil paths.

Authoritative geometry, from the site's own CDN:

| Asset | Geometry in the 100×100 SVG view box |
| --- | --- |
| [ff-ring.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-ring.svg) | Center (50,50), outer radius 50, inner radius 47.5. Hole ratio 0.95. |
| [ff-circle.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-circle.svg) | Center (50,50), radius 50. |
| [ff-donut.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-donut.svg) | Center (50,50), outer radius 50, inner radius 25. Hole ratio 0.5. |
| [ff-square.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-square.svg) | Rectangle from (0,0) to (100,100). |
| [ff-area-prox.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-area-prox.svg) | Center (50,50), outer gradient ring radius50 / inner37.5, center dot radius8. Dot uses colorA; gradient uses colorB. |
| [ff-knock.svg](https://cdn.raidplan.io/game/ffxiv/ability/ff-knock.svg) | Eight pairs of outward chevrons centered about (50,50). Cardinal outer tips radius45, inner tips37.8; diagonals rotate those directions by45°. Outer and inner chevrons use colorA/colorB respectively. |

The native [drawing bundle](https://raidplan.io/_next/static/chunks/3b3g8h5xb-g44.js)
defines PlanAbility with centered origins and default dimensions 100×100. It colors
SVG objects whose identifier ends in `_1` with `colorA`, and `_2` with `colorB`.
Opacity is separate. Fabric angles rotate clockwise in screen coordinates. Node
positions represent `meta.origin`, so a rotated left/top anchor must first be
shifted to its rotated center. The importer retains source colors for inspection;
the existing PlanStore.Import policy still applies the requested orange danger
palette when the plan enters the editor.

The same bundle defines PlanPath.packPoints/unpackPoints: consecutive x/y values
are multiplied/divided by 10. createFromPoints passes decoded points to Fabric's
PencilBrush.convertPointsToSVGPath, generating quadratic segments through adjacent
midpoints, with endpoint adjustments of brush width / 1000. Fabric's path center
comes from curve extrema; a left/top origin includes half the stroke width.
RaidPlanPath samples those quadratics into editable freehand points and uses exact
quadratic bounds for anchoring. Flips apply within the bounds before rotation.

The native [viewer bundle](https://raidplan.io/_next/static/chunks/0ewa72m1wpnq9.js)
accepts `#N` and `#step=N`, validates 1 ≤ N ≤ steps, and selects N−1. SourceStep
therefore records the original zero-based meta.step, independent of slide list
indices when the source contains empty steps.

Run the offline synthetic geometry regression with `pwsh -NoProfile -File tests/raidplan-areas.ps1`.
Pass a downloaded JSON filename as its first argument for the optional exact-board
check: 21 slides, 458 objects, 49 note boxes retained, no unsupported drawable
objects. The role lookup is stubbed in this focused harness; full builds use the
production role lookup.

The additional cached Caro boards contain 80 proximity and 3 knockback markers.
Proximity becomes an editable solid boundary ring, center dot, and compact `Prox`
label. Knockback becomes eight short outward arrows and a `KB` label. The labels
remain ordinary editable text, and one note per marker type explains that the
gradient/paired chevrons are simplified. A proximity ring's hole does not assert
safety, and arrow length does not assert a measured knockback distance. Marker
components retain source scale, center, and rotation. The proximity label is moved
slightly below the dot to keep both readable. One source marker generates several
editor objects, so ByType counts source nodes while Items counts editor objects.
Pass the cached board directory as the second optional argument to
`tests/raidplan-areas.ps1` to verify all 83 markers are retained.

Remaining limits: the custom arena picture remains a tracing reference; its
platform boundary is not encoded in the JSON and the existing frame estimation
still uses the waymarks. Circles/donuts do not have an ellipse model, skew is not
represented, and sampled pencil curves approximate continuous Béziers. SVG command
paths without packed pencil points are reported once as unsupported path geometry.
Existing stack/boss/push icons and triangle/cone translations simplify their artwork.
