# Optional red-dragon buddy

Goal: an original small red dragon provides restrained, personal mechanic cues as an optional Shikari HUD. The user approved the creature, an enable/disable checkbox, and implementation discretion over behavior.

Base: v0.9.1, branch `codex/red-dragon-buddy` in the existing isolated workspace. Preserve D:/Shikari. Build and present a reviewable development artifact; publishing remains a separate operation with the release-tag workflow established by the user.

Design: off by default; an independent repositionable HUD with one compact bubble at a time. The dragon breathes/bobs subtly, with a reduced-motion option. Locked HUD passes clicks through; combat always locks it. Visibility requires an in-game player and active plan, with cutscenes hidden. Local configured shotcalls and actual adaptive decisions are the only sources of instructions. No prose-to-position guessing, silent automatic rule activation, or implied safety from an observed position. Wait only for an actually armed supported mechanic. Reset stale advice when disabled, changing plan/territory/player, dying, or ending a pull. Respect follow/manual holds and bounded cue lifetimes.

Implementation tasks:

- [x] Create the original mascot with built-in image generation; inspect silhouette/alpha, embed asset, document prompt/method. Generated alpha requests produced RGB, so the selected clean black-background artwork is intentionally presented in a rounded dark portrait. Asset ownership: buddy_art.
- [x] Build presentation model, pure cue engine and framework service; add a formatted local-call event if needed. Verify expiry, priority, reset and unresolved data behavior. Ownership: buddy_behavior.
- [x] Implement BuddyWindow and pure layout/motion helper: viewport-safe placement, wrapping, restrained animation, drag persistence, texture lifecycle and graceful fallback. Verify bounds, disabled behavior and mouse policy. Ownership: buddy_ui.
- [x] Integrate default-off persistent settings, configuration UI, Plugin construction/disposal and embedded-resource packaging; document user flow. Ownership: root.
- [x] Run focused regressions, existing CI checks, full Release build, independent review and asset/package inspection. Produce build/patch/testing notes in outputs. Ownership: root with review assistance.

Settings: BuddyEnabled=false; BuddyAnchor=(.76,.70); BuddyScale=1; BuddyReducedMotion=false; BuddyUnlocked=false. Controls live in a small Raid buddy section with optional appearance controls collapsed.

Presentation contract: BuddyService.Presentation exposes Key, Heading, Body, Detail, Mood and HasCue. Moods are Resting, Listening, Guiding, Uncertain and Recovering. Window renders this snapshot without deriving encounter logic itself.

Validation limits: substituted ImGui/game tests cannot establish actual in-game layering or input behavior; user-client check remains required. This feature presents existing configured guidance; it does not independently solve unsupported mechanics.

Verified September 8: all 25 regression scripts pass; full Release build has zero warnings/errors; workflow YAML parses; ZIP contains the current DLL and exact embedded artwork; binary patch applies cleanly to v0.9.1. Independent review found and verified fixes for overdue reminders, queued calls from replaced plans, and fresh opening calls at combat time zero. Development package and illustrative production-window preview are in the parent task's outputs.
