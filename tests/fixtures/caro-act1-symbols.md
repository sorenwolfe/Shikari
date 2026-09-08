# Act 1 symbol fixture

This is a reduced data fixture from the publicly linked [Toxic Friends P1 board](https://raidplan.io/plan/44JJjqZ6Mcgaxnnn#6), retrieved 2026-09-08 from [its JSON source](https://userdata.raidplan.io/44JJjqZ6Mcgaxnnn.json), revision 2. The source attributes the strategy to Cute Animal @ Omega and Toxic Friends. WTFDIG's [M12S Caro guide](https://wtfdig.info/74/m12s#caro) explicitly links steps #6–#10 to Grotesquerie: Act 1.

The fixture retains only source steps 5–9 (zero-based) and their arena, waypoint, marker and emoji nodes. No artwork files are included. Source positions, scale, origins and asset names are unchanged. It contains three dragon-face emoji and 22 game-art markers: 17 using artwork 214336 and five using 214337. These numbers identify images, not Status sheet rows or live assignment conditions.

Run `pwsh -NoProfile -File tests/raidplan-symbols.ps1`. The tests verify source-step retention, native icon/emoji classification, the absence of anonymous status-player tokens, and composition with the corresponding guide steps. Optional full-board corpus checks take a directory containing the six named source JSON files. That corpus currently contains 195 emoji, 328 native game-art markers and 18 custom target/cut markers retained as readable fallbacks.

Text boxes still become notes. Custom backgrounds, geometric approximations, missing alternate outcomes, and unsupported guide expressions are outside this fixture's completeness claim.
