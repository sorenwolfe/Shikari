# Ember presence

The companion's appearance is independent of tactical assignments. `BuddyAmbientController` consumes fresh game snapshots and exposes one pose and its start time; `BuddyCueEngine` still owns personal calls and assignment wording. The renderer gives tactical cues priority and never interprets a pose as evidence for a mechanic.

## State and placement

The default drag surface is 132 pixels at the existing saved scale. The atlas has four 627-pixel cells in reading order: Idle, Sleeping, Welcoming, Focused. Draw the alpha cutouts without a panel behind the creature. Keep a stable drag surface and common ground line across poses, with enough viewport padding for motion and particles. Speech bubbles retain their readable background.

FFXIV's `OnlineStatus` row 17 marks Away from Keyboard. This was verified directly from the installed 2026.08.11 game sheet through Lumina; the runtime reads the row ID without relying on a localized label. An observed active → Away → active cycle triggers a 2.5-second welcome. Unknown status, changed actor/territory, a long update gap, loading, logout, cutscenes, GPose, combat, and death discard the welcome history. Duty and combat select Focused; an AFK break outside combat can still select Sleeping in a duty.

The visible companion does not require an active plan. Personal calls continue to require their usual plan and configuration. Reduced motion keeps the selected pose while disabling movement, blinking, particles, and bubble fades.

Idle eyes use small procedural eyelids registered to landmarks in the original atlas. The supplied image quad and UV crop keep them aligned through breathing, rotation, resizing, and mirroring. Each blink lasts 190 ms, with varying pauses and an occasional double blink. Other poses, tactical cues, recovery, and missing artwork suppress the overlay. No image pixels are modified and no extra texture is loaded.

Move Ember is directly visible in settings. Dragging keeps its temporary anchor in memory and writes the configuration once on release. The HUD and settings use both encounter state and the game's combat flag to prevent movement during combat. Hidden or locked windows cancel an unfinished drag.

Mouse interaction must test `NoMouseInputs`, not any bit in `NoInputs`: the latter is a composite that also contains `NoNav`, which the HUD always sets. Testing the composite for zero caused v0.9.4 to skip dragging and its grip. The harness now models the real composite values.

## Validation

`buddy-ambient.ps1` and `buddy-service.ps1` cover state transitions, stale status handling, and existing tactical delivery. `buddy-layout.ps1`, `buddy-settings.ps1`, and `buddy-window.ps1` cover actual settings controls, drag persistence, visibility, viewport bounds, pose selection, particle limits, and reduced motion. The window harness can export a PNG or animated HTML preview from captured drawing commands.

`buddy-blink.ps1` checks blink timing, state suppression, eye coverage, transforms, and polygon winding. `buddy-native-input.ps1` runs the production window through native ImGui frames with the real Dalamud binding. It covers initial mouse capture, the visible grip, release persistence, combat cancellation, occlusion, viewport bounds, and locked click-through. This test runs after CI fetches the matching native and managed Dalamud libraries. Game services and the window host shell are still substituted; input, frame lifecycle, flags, and drawing submissions are real.

`buddy-art.ps1`, run after building, checks real RGBA pixels, clear cell edges, a visible creature in every cell, and the exact resource embedded in the DLL. See [art provenance](buddy-art.md) for the image prompts and the user's authorized local background extraction.

These harnesses use substitute game/graphics interfaces. They do not replace an in-game check of texture loading, input pass-through, AFK transitions, UI scale, and screen-edge placement.
