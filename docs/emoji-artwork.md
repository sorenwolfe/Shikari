# Emoji artwork

Shikari includes the complete 72×72 PNG collection from **Twemoji 17.0.3**, by Twitter, Inc. and the Twemoji contributors, licensed under **Creative Commons Attribution 4.0 International (CC BY 4.0)**.

- Project: https://github.com/jdecked/twemoji
- Pinned source: https://github.com/jdecked/twemoji/tree/b6b55fef1e8636b540a6d016a4729ca8cdf2e60b
- License: https://creativecommons.org/licenses/by/4.0/
- Full license text: `Shikari/Resources/Emoji/LICENSE-GRAPHICS.txt`

All 4,009 PNGs are copied without modification. Shikari scales, rotates and mirrors them at draw time to match the authored board; it does not claim the artwork as its own. The lookup and rendering code are original Shikari code. No Twemoji JavaScript is bundled.

`tools/update_emoji_assets.py` reproduces the asset set, sorted catalog and SHA-256 checksums from that exact upstream commit. `--archive` accepts an existing upstream ZIP. Symbols work offline and never trigger downloads during a pull. Unknown sequences retain the original plan data and receive a visible text fallback.

Game buff, debuff and marker pictures come from the player's installed FFXIV game through Dalamud's game-icon provider. An artwork number is never treated as a status identifier or an assignment rule.

This artwork path covers positioned board symbols and canvas/mini captions. Notes and other ordinary UI text still use the game's configured ImGui font. Sequences newer than the pinned collection or unsupported combinations use a text fallback on the board; this does not add universal emoji support to every text surface.
