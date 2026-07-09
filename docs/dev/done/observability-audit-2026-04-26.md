# Observability Audit - 2026-04-26

## Scope

This audit reviews Parsek's current logging coverage after the recent growth in
recording, rewind, ghost playback, map presence, KSC playback, UI, diagnostics,
and game-action systems. The goal is not more logs everywhere. The goal is that
each user-visible decision path can be reconstructed from `KSP.log` without
burying the useful lines under per-frame noise.

The audit was performed on branch `observability-audit` at `ee893e38` plus
read-only parallel subsystem reviews. No production code changes are included in
this report.

Implementation plan: `docs/dev/plan-observability-logging-visibility.md`.

## Existing Logging Tools

Parsek already has the right primitives in `Source/Parsek/ParsekLog.cs`:

- `Info`, `Warn`, `Error`: durable state transitions, player-visible outcomes,
  unexpected conditions, and failures that change behavior.
- `Verbose`: detailed diagnostics for one-shot operations.
- `VerboseRateLimited`: per-frame, per-ghost, per-loop-cycle, or repeated
  diagnostics. Default window is 5 seconds.
- `WarnRateLimited`: repeated abnormal conditions that still need WARN
  visibility.
- `VerboseOnChange`: best fit for hot-path gates whose reason stays stable for
  many frames. It logs the first state, stays quiet while unchanged, and reports
  suppressed count on the next change.
- `RecState` and `RecStateRateLimited`: structured recorder/scenario state
  snapshots for lifecycle edges and hot-path summaries.
- `TestSinkForTesting` / `TestObserverForTesting`: log-capture seams for tests.

`ParsekSettings.verboseLogging` defaults to true (`Source/Parsek/ParsekSettings.cs:50`),
so verbose diagnostics are currently part of the normal support workflow. That
makes rate limiting and batching mandatory.

## Logging Policy For The Follow-Up Work

Use this split when implementing the audit findings:

- One-shot lifecycle edge: `Info` or `Warn`, include IDs and numeric context.
- Expected hot-path skip: `VerboseOnChange` keyed by stable identity and reason.
- Per-frame aggregate: counters plus one `VerboseRateLimited` summary.
- Repeated abnormal hot-path state: `WarnRateLimited` keyed by stable identity.
- Exceptions that alter control flow: `Warn` or `Error` with exception type,
  message, path/pid/recording id, then preserve existing flow or rethrow.
- Collection loops: count decisions and log one summary. Do not add unbounded
  per-item logs.
- Rate-limit keys: use stable recording id, vessel pid, rewind-point id, or an
  aggregate key. Avoid bare dense list indexes when the list can shift.

## Current Strengths

- Recorder start/stop, sample summaries, and many `RecState` lifecycle snapshots
  are strong.
- `ParsekScenario.OnSave` / `OnLoad` log broad save/load counts and timings.
- Ghost map source decisions have improved substantially, especially around
  `Segment`, `TerminalOrbit`, `StateVector`, endpoint conflicts, and already
  materialized vessels.
- Recent spam fixes moved several formerly chatty paths to `VerboseRateLimited`
  or `VerboseOnChange`.
- In-game `LogContractTests` validate the synthetic `ParsekLog` primitive
  format, levels, rate-limit suppressed counts, and several resource
  invariants. Production-line warning-prefix coverage is still incomplete; see
  P3.5.

The remaining problem is concentrated in skip gates, negative decisions, and
places where repeated diagnostics either spam or hide different identities under
one shared rate-limit key.

## P1 Findings

### P1.1 Save/load exceptions lack top-level Parsek context

`ParsekScenario.OnSave` (`Source/Parsek/ParsekScenario.cs:435`) and
`ParsekScenario.OnLoad` (`Source/Parsek/ParsekScenario.cs:990`) use `finally`
timing logs, but they do not wrap the full phase in a top-level catch that logs
Parsek-specific context before the exception leaves the method.

Impact: failures in tree persistence, sidecar loading, game-state persistence,
rewind separation, merge finishing, or pending tree restore can appear as a raw
Unity/KSP exception without the save folder, counts, active tree state, rewind
state, or pending marker state.

Recommendation: add top-level `ParsekLog.Error("Scenario", ...)` with phase,
scene, save folder, committed/pending counts, active tree id/name, rewind marker
state, and exception type/message. Emit `RecState("OnSave:exception", ...)` or
`RecState("OnLoad:exception", ...)`, then rethrow.

### P1.2 KSC playback has current spam risks

`ParsekKSC.Update` calls `RebuildAutoLoopLaunchScheduleCache` every frame
(`Source/Parsek/ParsekKSC.cs:209`), and that rebuild logs unconditionally at
`Source/Parsek/ParsekKSC.cs:348` while auto-loop candidates exist.

Playback-disabled past-end KSC spawn attempts log before the one-shot spawn
dedupe (`Source/Parsek/ParsekKSC.cs:249`, with dedupe later around
`Source/Parsek/ParsekKSC.cs:1238`). Looping skip logs also happen before dedupe
around `Source/Parsek/ParsekKSC.cs:1230`.

Impact: KSC can flood `KSP.log` in a normal steady state, making the real
decision lines hard to find.

Recommendation: use `VerboseOnChange` with a queue fingerprint for rebuilds, or
a `VerboseRateLimited` aggregate. Move past-end spawn attempt logging after the
dedupe, or make the pre-dedupe line `VerboseOnChange` keyed by
`RecordingId + reason`.

## P2 Findings

### P2.1 Missing flight ghost skip reasons

`ComputePlaybackFlags` collapses `!hasData`, `!rec.PlaybackEnabled`, and
external-vessel suppression into one `skipGhost` bit
(`Source/Parsek/ParsekFlight.cs:12218`). `GhostPlaybackEngine.UpdatePlayback`
then skips at `Source/Parsek/GhostPlaybackEngine.cs:388`, and the
`!HasRenderableGhostData` guard skips at `Source/Parsek/GhostPlaybackEngine.cs:414`.

Impact: "the ghost is missing" often has no reason trail in `KSP.log`.

Recommendation: carry a compact skip reason through `TrajectoryPlaybackFlags`,
then log `VerboseOnChange` per recording id and include aggregate per-frame
counts in the existing engine frame summary.

### P2.2 Additional playback skips are silent or under-summarized

Important skip paths currently have no summary when the ghost was never active:

- Loop anchor configured but not loaded:
  `Source/Parsek/GhostPlaybackEngine.cs:459`.
- Parent loop sync failure and parent pause/warp skip:
  `Source/Parsek/GhostPlaybackEngine.cs:492`.
- Warp mesh suppression hides/destroys without a reason count:
  `Source/Parsek/GhostPlaybackEngine.cs:549`.
- `EnsureGhostVisualsLoaded` returns failed for null state or debris without a
  snapshot (`Source/Parsek/GhostPlaybackEngine.cs:3362`).
- `TryPopulateGhostVisuals` returns failed for null state or null ghost
  (`Source/Parsek/GhostPlaybackEngine.cs:3106`).

Recommendation: add skip counters such as `beforeActivation`, `anchorMissing`,
`loopSyncFailed`, `warpHidden`, `visualLoadFailed`, and `noRenderableData` to
the frame summary. Use `VerboseOnChange` for per-recording reason flips.

### P2.3 Background recorder can stop sampling silently

`BackgroundRecorder.OnBackgroundPhysicsFrame` returns without logging when the
tree is null, the vessel is not in `BackgroundMap`, the vessel is packed, the
loaded state is missing, or the tree recording is missing
(`Source/Parsek/BackgroundRecorder.cs:1163`,
`Source/Parsek/BackgroundRecorder.cs:1194`,
`Source/Parsek/BackgroundRecorder.cs:1198`).

Impact: background sampling can disappear because the tree maps and loaded
states drifted, without a diagnostic that explains the lost samples.

Recommendation: keep expected null/tree skips quiet, but add `WarnRateLimited`
for map/state disagreements keyed by vessel pid and recording id.

### P2.4 Transition to background can miss vessel context

`FlightRecorder` transitions active recordings to background around
`Source/Parsek/FlightRecorder.cs:6368`. If live vessel lookup fails, boundary
sample/orbit/finalization-cache work can be skipped before the generic
transition log near `Source/Parsek/FlightRecorder.cs:6413`.

Recommendation: add a `Warn` with pid, recording id, vessel name, points,
orbit segments, track sections, and finalization-cache state when the lookup is
null during this transition.

### P2.5 Post-switch auto-record no-trigger decisions are invisible

`OnPostSwitchAutoRecordPhysicsFrame` logs the watch state once per second
(`Source/Parsek/ParsekFlight.cs:5834`) and logs settle waits, but when
`EvaluatePostSwitchAutoRecordTrigger` returns `None` it returns silently.

Impact: "why did Parsek not start recording after I modified the switched
vessel?" cannot be reconstructed. The trigger inputs are precisely the useful
data: engine, RCS, attitude, crew, resource, part, motion, orbit, manifest
evaluation cadence, and thresholds.

Recommendation: add a `VerboseOnChange` summary keyed by watched vessel pid with
suppression, baseline, settle, and trigger state. Add a rate-limited manifest
delta summary at the existing manifest-evaluation cadence.

### P2.6 Crew board/EVA split gates have silent early returns

`OnCrewBoardVessel` logs entry and some null/no-tree paths, but
`pendingSplitInProgress` returns silently (`Source/Parsek/ParsekFlight.cs:4791`).
`OnCrewOnEva` returns silently for `pendingSplitInProgress` and for source
vessel mismatch during an active recording (`Source/Parsek/ParsekFlight.cs:4817`).

Recommendation: use `VerboseRateLimited` or `VerboseOnChange` keyed by active
recording id/source pid so real crew transitions explain why no branch or
auto-record was started.

### P2.7 Rewind button precondition failures are not logged

`RewindInvoker.CanInvoke` returns false for null RP, wrong scene, pending invoke,
corrupt RP, missing quicksave, active re-fly marker, and deep-parse failure
without a Parsek log line (`Source/Parsek/RewindInvoker.cs:63`).

Spam risk: this can be called from UI draw loops.

Recommendation: use `VerboseOnChange` keyed by RP id and reason, or a
`VerboseRateLimited` cache summary. Keep the returned user-facing reason intact.

### P2.8 Fast-forward watch handoff can disappear silently

`pendingWatchAfterFFId` is cleared after engine playback
(`Source/Parsek/ParsekFlight.cs:12292`). If the recording exists but no active
ghost is available, the watch request can vanish with no log.

Recommendation: emit `Warn` when the recording exists but no active ghost is
watchable, and `Verbose` when the recording id no longer exists.

### P2.9 Watch camera infrastructure failures are silent

`WatchModeController` returns if `FlightCamera.fetch`, its transform, or its
parent is null (`Source/Parsek/WatchModeController.cs:2477`).

Impact: the user sees broken watch/camera behavior without a Parsek context.

Recommendation: `WarnRateLimited` with watched recording id/name, cycle, active
scene, target state, and which camera object was null.

### P2.10 Sidecar and path failures need richer context

`RecordingStore` sidecar load/save catches at
`Source/Parsek/RecordingStore.cs:5796`,
`Source/Parsek/RecordingStore.cs:5920`, and
`Source/Parsek/RecordingStore.cs:6636` log recording id plus message, but not
enough path/save-folder/epoch/file-kind context. `SnapshotSidecarCodec.TryProbe`
can fail before codec-level details are surfaced
(`Source/Parsek/SnapshotSidecarCodec.cs:67`). `RecordingPaths` directory
creation calls can throw without a Parsek path-context log
(`Source/Parsek/RecordingPaths.cs:74`,
`Source/Parsek/RecordingPaths.cs:131`,
`Source/Parsek/RecordingPaths.cs:152`,
`Source/Parsek/RecordingPaths.cs:184`).

There is also a severity mapping problem: `RecordingStore.Log` maps messages
without `WARNING:` or `WARN:` to `Info`
(`Source/Parsek/RecordingStore.cs:182`), while failed save/load sidecar
operations return false but currently call it with plain "Failed to ..."
messages (`Source/Parsek/RecordingStore.cs:5796`,
`Source/Parsek/RecordingStore.cs:5920`,
`Source/Parsek/RecordingStore.cs:6636`). These are control-flow-changing
failures and should not be INFO.

Recommendation: log sidecar path, file kind, save name/folder, sidecar epoch,
encoding/probe version, staged-file counts, exception type, and message at
`Warn` or `Error` as appropriate. Prefer structured failure reasons from codecs
to duplicate direct logging.

### P2.11 Scenario OnLoad timing can duplicate and miscount

`ParsekScenario.OnLoad` calls `WriteLoadTiming` on several successful-return
paths (`Source/Parsek/ParsekScenario.cs:1044`,
`Source/Parsek/ParsekScenario.cs:1595`,
`Source/Parsek/ParsekScenario.cs:1822`) and again in `finally`
(`Source/Parsek/ParsekScenario.cs:1869`). The final call can use stale
`loadedRecordingCount`.

Impact: cold-start or early-return load logs can contain duplicate timing lines
or a misleading recording count, which weakens the save/load timeline when
debugging persistence bugs.

Recommendation: centralize load timing emission so each `OnLoad` invocation logs
one timing line with the final count and clear phase/status fields.

### P2.12 Kerbal assignment and finalization cache decisions need batch summaries

`KerbalsModule` skips tourist actions, empty recording ids, missing metadata,
loop recordings, and empty kerbal names in assignment processing around
`Source/Parsek/KerbalsModule.cs:156`.

`RecordingFinalizationCacheProducer` accepts and declines cache decisions at
`VerboseRateLimited` around
`Source/Parsek/RecordingFinalizationCacheProducer.cs:667`; unexpected declines
that affect rewind/resource reconciliation may disappear when verbose is off.

Recommendation: add one batch summary for kerbal pre-pass/post-walk decisions.
Keep cache accepts verbose, but promote unexpected or terminal-state-impacting
declines to `WarnRateLimited`.

### P2.13 Game-action capture has silent null and baseline gates

`GameStateRecorder` returns silently for several event shapes:

- Tech researched null host or unsuccessful operation:
  `Source/Parsek/GameStateRecorder.cs:561`.
- Part purchase null part:
  `Source/Parsek/GameStateRecorder.cs:609`.
- Kerbal type change suppression or irrelevant transition:
  `Source/Parsek/GameStateRecorder.cs:846`.
- Resource event baseline not seeded:
  `Source/Parsek/GameStateRecorder.cs:894`,
  `Source/Parsek/GameStateRecorder.cs:954`,
  `Source/Parsek/GameStateRecorder.cs:1004`.

`GameStateEventConverter.ConvertEvents` logs converted/skipped totals
(`Source/Parsek/GameActions/GameStateEventConverter.cs:31`) but not skip
reason counts by event type.

Recommendation: log one-shot event rejects at `Verbose` or `VerboseRateLimited`
with event type and reason. Add per-type skip counters to converter summaries.

### P2.14 Map/UI marker skips lack summaries

Flight map marker drawing silently skips camera unavailable, native-icon
suppression, chain non-tip rows, hidden ghost position failure, and missing body
around `Source/Parsek/ParsekUI.cs:982`,
`Source/Parsek/ParsekUI.cs:1044`, and `Source/Parsek/ParsekUI.cs:1069`.

Tracking Station atmospheric marker success is per-recording, while skip
reasons are silent around `Source/Parsek/ParsekTrackingStation.cs:443`.

Recommendation: add per-frame or per-refresh counters and one
`VerboseRateLimited` summary. Keep per-index logs only for state changes.

### P2.15 UI window transitions are mostly logged, but not all

Main window toggles log Timeline, Recordings, Kerbals, Gloops, Settings, Close,
and Real Spawn Control. The Career window toggle is missing the matching UI log
(`Source/Parsek/ParsekUI.cs:155`).

`SpawnControlUI.DrawIfOpen` auto-closes with no log when not in flight, flight is
null, or candidate count is zero (`Source/Parsek/UI/SpawnControlUI.cs:64`).

Recommendation: add `Verbose` for Career toggle and `VerboseOnChange` for Real
Spawn Control auto-close reasons.

### P2.16 Harmony/diagnostics/test infrastructure has observability gaps

- `PhysicsFramePatch.BackgroundRecorderInstance` has no attach/clear transition
  log, while active and Gloops recorders do (`Source/Parsek/Patches/PhysicsFramePatch.cs:35`).
- Ghost orbit Harmony suppression returns false or hides icons without summary
  (`Source/Parsek/Patches/GhostOrbitLinePatch.cs:17`,
  `Source/Parsek/Patches/GhostOrbitLinePatch.cs:152`).
- In-game test scene eligibility skips only show final counts; per-scene skip
  reasons are not visible enough (`Source/Parsek/InGameTests/InGameTestRunner.cs:754`).
- Diagnostics missing-sidecar warnings can repeat on every storage scan
  (`Source/Parsek/Diagnostics/DiagnosticsComputation.cs:193`).
- Post-hoc log validation skips malformed `[Parsek]` lines even though the
  parser records them (`Source/Parsek.Tests/LogValidation/ParsekLogContractChecker.cs:55`).

Recommendation: add attach/clear `Info` for background recorder, aggregate
Harmony suppression summaries, one `Info` scene-skip batch summary for tests,
`WarnRateLimited` or scan aggregates for diagnostics, and post-hoc checks for
unstructured Parsek lines, invalid levels, and redundant warning prefixes.

## P3 Findings

### P3.1 Shared rate-limit keys hide identities

Some `VerboseRateLimited` keys are shared across different recordings or cycles,
including loop/overlap events in `ParsekPlaybackPolicy` and `GhostPlaybackEngine`
around `Source/Parsek/ParsekPlaybackPolicy.cs:1388`,
`Source/Parsek/ParsekPlaybackPolicy.cs:1395`,
`Source/Parsek/GhostPlaybackEngine.cs:1390`, and
`Source/Parsek/GhostPlaybackEngine.cs:4185`.

Impact: the first identity in the window can suppress later identities.

Recommendation: either key by stable identity when identity matters, or log one
aggregate summary with counts and first sample.

### P3.2 Repeated warnings still need rate limits

Potential repeaters:

- Map state-vector/body/anchor failures in `GhostMapPresence`.
- Ballistic terrain/PQS resolver warnings in `BallisticExtrapolator`.
- Diagnostics `SafeGetFileSize` missing-sidecar warnings.
- Watch horizon-basis `Info` changes when the source is effectively steady.
- Terrain correction helper decisions.

Recommendation: keep first failure visible, then use `WarnRateLimited` keyed by
recording/body/path/reason. Demote helper-level steady decisions to
`VerboseRateLimited`.

### P3.3 Resource event logging can crowd out decision logs

Accepted funds/science/reputation events are logged individually through
`GameStateStore` and `GameStateRecorder` (`Source/Parsek/GameStateStore.cs:31`,
`Source/Parsek/GameStateRecorder.cs:883`). This is useful for irreversible
player-visible actions, but resource-heavy sessions can crowd out diagnostics.

Recommendation: keep `Info` for durable ledger milestones and user-visible
resource commitments. Use rate-limited aggregate summaries for noisy recalcs and
high-frequency resource deltas.

### P3.4 Cleanup errors are swallowed in low-risk paths

`SidecarFileCommitBatch` swallows transient artifact cleanup failures around
`Source/Parsek/SidecarFileCommitBatch.cs:197`. `PartStateSeeder` has a silent
catch at `Source/Parsek/PartStateSeeder.cs:82`. Several reflection helpers in
`GhostMapPresence` catch and return null/false.

Recommendation: do not promote these to noisy WARNs by default. Add
`VerboseRateLimited` cleanup/reflection summaries, and promote only when the
failure changes runtime behavior.

### P3.5 Production WARN messages still include redundant WARNING prefixes

`LogContractTests.WarnLinesNoRedundantPrefix` validates synthetic
`ParsekLog.Warn` output, but it does not scan production call sites. Current
production messages still include redundant `WARNING:` text before being emitted
at WARN level, for example `Source/Parsek/TimeJumpManager.cs:440` and
`Source/Parsek/TimeJumpManager.cs:568`.

Recommendation: remove redundant prefixes from production messages and add a
post-hoc or source-level check that catches real emitted `[Parsek][WARN]`
messages whose payload starts with `WARNING:` or `WARN:`.

## Suggested Implementation Order

1. Fix current spam first: KSC auto-loop rebuild logging, playback-disabled
   KSC spawn-attempt logging, diagnostics missing-sidecar scan warnings, and
   shared rate-limit keys that hide identities.
2. Add flight playback decision visibility: explicit ghost skip reason, frame
   skip counters, loop anchor/warp/visual-load summaries, and fast-forward watch
   miss logs.
3. Add persistence/rewind context: scenario top-level exception logs,
   `CanInvoke` reason logging, sidecar/path context, and active-tree restore
   no-op summaries.
4. Add recorder/game-action negative-path logs: background recorder state drift,
   active-to-background missing-vessel warning, post-switch auto-record
   no-trigger summaries, EVA/boarding split skips, and game-action skip counts.
5. Add UI/map/diagnostic/test contract coverage: map marker summaries, Real
   Spawn Control auto-close reasons, background recorder attach/clear logs,
   ghost orbit Harmony summaries, and post-hoc malformed-log validation.

## Test Strategy

Add log assertions with `ParsekLog.TestSinkForTesting` for each new decision
helper or state transition. Prefer pure helpers that return reason enums or
summary structs, then test both behavior and emitted log shape.

Minimum regression set:

- Flight playback: each `TrajectoryPlaybackFlags` skip reason emits once and
  stable repeats are suppressed.
- Engine frame summary: no-renderable-data, anchor-missing, warp-hidden,
  visual-load-failed, and session-suppressed counters appear in one summary.
- KSC playback: auto-loop queue rebuild logs once per fingerprint change, not
  every `Update`.
- Rewind: `CanInvoke` logs reason changes without per-frame UI spam.
- Scenario: injected `OnSave`/`OnLoad` exception logs phase context and rethrows.
- Game actions: converter summaries include per-type skip reason counts.
- Log validation: malformed `[Parsek]` lines fail post-hoc validation.

## Definition Of Done For The Follow-Up

- A user-visible skip or state change has a grep target in `KSP.log`.
- Hot-path logs are either batched, rate-limited, or on-change.
- Repeated abnormal conditions use stable keys so one identity does not hide
  another unless the log is intentionally aggregate.
- The log line includes enough context to reproduce the decision: recording id,
  vessel pid/name, rewind point id, tree id, UT, body, path, and reason as
  applicable.
- Tests assert both the behavior and the log contract for the new coverage.
