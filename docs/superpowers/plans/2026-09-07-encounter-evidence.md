# Encounter evidence implementation plan

> For agentic workers: use superpowers:subagent-driven-development for independently owned tasks, with integration review by the primary agent.

Goal: Review FF Logs pulls and local recordings against an imported plan, inspect each player's statuses and positions, and create disabled, editable adaptive rules from observed assignments.

Architecture: Keep source data and a frozen plan together in ReplayAttempt. Import log evidence through the existing unified import flow. Live capture retains statuses for each readable party member independently of map alignment. The pure adaptive evaluator supports AND conditions; Review can test an assignment against a selected recorded actor before it is enabled for live use.

Tech stack: C#, .NET 10, Dalamud 15, ImGui, Newtonsoft.Json; existing PowerShell pure-code test harnesses. No new runtime packages or hosted services.

## Constraints

- Preserve the unified import work and backwards compatibility with existing plans/replays.
- Do not push, tag, or publish to GitHub in this task.
- Never infer live status IDs from icon IDs or cast names alone. Unknown log parameters remain unknown.
- Log positions are sparse observations. Never interpolate across missing data or use unverified coordinate conversion.
- Imported log coordinates need explicit reference-point calibration before overlaying plan coordinates.
- Keep history and raw evidence local; share codes contain only authored rule definitions, not replay data or credentials.
- Plan/rule changes are explicit UI actions and imports/default drafts remain disabled.
- Log evidence is cached per imported attempt; no API requests during live rule evaluation.

## Task 1: FF Logs evidence acquisition (agent)

Files: Services/FfLogs/FfLogsClient.cs, LogModels.cs, new LogEvidence.cs / parser, tests/fflogs-evidence.ps1 and associated tests.

Produce an optional GetEvidenceAsync(clientId, secret, code, LogFight, cancellationToken) method and a LogEvidence DTO with StatusEvents, Positions, Warnings, Complete. Each status includes Time, SourceId, TargetId, StatusId (0 if not verified), AbilityId, Name, Change, nullable Duration/Stacks/Parameter. Each position includes Time, ActorId, X, Y in source coordinates. Preserve exact numeric IDs and report-relative to pull-relative conversion. Reuse current authentication; fetch paginated bounded events with resources and do not silently truncate.

- [x] Inspect a small real response from the configured report without printing credentials, token responses, or player names.
- [x] Write parser tests for applies/removals, stacks vs parameters, missing resources, timestamp conversion, numeric limits, and pagination.
- [x] Implement acquisition and normalization with explicit warnings for unavailable information.
- [x] Run tests and provide the precise DTO contract and sanitized shape evidence for integration.

## Task 2: Compound adaptive rules (agent)

Files: Model/AdaptiveMechanic.cs, Services/Adaptive/AdaptiveEngine.cs, UI/MainWindow.Adaptive.cs, Services/ShareCode.cs and normalizer only if necessary, tests/adaptive*.cs/ps1.

Produce StatusCondition with StatusId, Parameter=-1, MinimumSeconds, MaximumSeconds=60. StatusBranch.AdditionalStatuses holds up to three AND conditions in addition to its existing primary condition. StatusObservation gains Removed=false, Baseline=false, ParameterKnown=true, DurationKnown=true so unknown evidence is not mistaken for exact zero or a fresh assignment.

- [x] Tests: two asynchronous status assignments select one branch only after all conditions match; missing, removed, expired, baseline and conflicting evidence withhold; old single-status rules still work.
- [x] Keep compound conditions active concurrently, restart settling on relevant evidence changes, and invalidate removed/expired matches.
- [x] Expose extra conditions in existing Adaptive editor; keep invalid/shared rules disabled and old format compatible.
- [x] Run adaptive and share-code suites; report exact behavior and interfaces.

## Task 3: Shared replay evidence and plan comparison (primary)

Files: new Services/Replay/ReplayEvidence.cs, ReplayAttempt.cs, ReplayStore.cs, ReplayValidation.cs, ReplayBuffer.cs, new Services/Replay/LogReplayBuilder.cs and EvidenceAnalysis.cs, UI/MainWindow.Review.cs and new MainWindow.Evidence.cs.

- [x] Add actor-identified status events, sparse source-position samples, raw cast anchors, source provenance and calibration metadata to attempts.
- [x] Capture readable party status snapshots and changes independent of alignment; clear state when actor unreadable, dead, or identity changes. Record all cast anchors even if the plan has no linked timeline.
- [x] Build a log attempt against a frozen plan; map actors to seats explicitly, align mechanics using action ID + occurrence, retain unmatched casts.
- [x] Review shows active statuses at cursor and selected actor, source notes, and imported source positions on their own map. Add explicit three-point calibration and mapped overlay with stale-position gaps.
- [x] Compare another pull at matching cast occurrence. Propose disabled conjunction rules only from known IDs and fresh changes after the anchor, with explicit destination and territory selection; simulate candidate rules against evidence without moving live slides.
- [x] Verify serialization, old replay compatibility, status expiry/removal/gaps, calibration, sparse playback, and rule draft safeguards.

## Task 4: Unified flow and verification (primary)

Files: UI/MainWindow.Import.cs, README.md, integration tests and user-facing validation notes.

- [x] Extend the FF Logs preview with one action to attach the selected pull to Review, fetch evidence asynchronously and save via ReplayStore on the UI thread.
- [x] Show progress and useful warnings in that existing flow, not a new import window.
- [x] Run real-API checks using configured report, pure tests, existing replay/adaptive/sharing/import suites, Release build, and archive checks.
- [x] Review integrated changes, resolve findings, synchronize verified changes to D:/Shikari without overwriting unrelated edits, deliver local test build. Do not publish.

## Completion notes

Implemented and verified on 2026-09-07. The integrated review findings were resolved, including a regression fixture for legacy omitted first-seat fields. Source was synchronized to D:/Shikari after checking its working files against the previous unified-import baseline. Release builds passed with zero warnings/errors. The local archive and in-game walkthrough are in the task outputs folder. No push, tag, or publication was performed. In-game visual/encounter validation remains a user test; the automated and real-API coverage does not establish a complete mechanic solver.
