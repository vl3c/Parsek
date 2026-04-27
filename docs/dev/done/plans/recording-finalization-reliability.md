# Recording Finalization Reliability Implementation Plan

**Branch:** `doc-recording-finalization-design`
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-doc-recording-finalization`
**Design doc:** `docs/parsek-recording-finalization-design.md`
**Related:** `docs/dev/done/plans/incomplete-ballistic-extrapolation.md`, `docs/parsek-rewind-to-separation-design.md`

## Goal

Make recording finalization reliable when KSP ends or unloads the live vessel before Parsek's current scene-exit finalizer can inspect it. The implementation should keep a per-live-recording, in-memory finalization cache and consume it at every premature-end path before falling back to trajectory inference.

This is not a TODO bucket. It is the phase plan for separate implementation worktrees and PRs after the design doc is accepted.

## Non-Goals

- No save-format change for the cache.
- No cross-scene background simulation.
- No full atmospheric drag simulation for unloaded vessels.
- No Rewind-to-Separation UI changes in this plan.
- No committed-recording mutation after commit.

## Current Source Map

- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` - current scene-exit predicted-tail applier.
- `Source/Parsek/PatchedConicSnapshot.cs` - current patched-conic snapshot; currently treats maneuver-node transitions as truncating.
- `Source/Parsek/BallisticExtrapolator.cs` - current ballistic/conic extrapolator and `ShouldExtrapolate` policy.
- `Source/Parsek/FlightRecorder.cs` - active recorder sampling, on-rails boundaries, vessel switch decision helper, active destruction hook.
- `Source/Parsek/BackgroundRecorder.cs` - background loaded/on-rails sampling, background destroy/unload/TTL paths.
- `Source/Parsek/ParsekFlight.cs` - tree finalization, scene exit, post-destruction merge, vessel event routing.
- `Source/Parsek/Recording.cs` / `RecordingStore.cs` - existing persisted recording fields and sidecar dirty/persistence behavior.
- `Source/Parsek/TerminalKindClassifier.cs` - Rewind-to-Separation terminal-kind consumer.

## Phase 0 - Documentation PR

**Scope:** this worktree only.

Tasks:

1. Add `docs/parsek-recording-finalization-design.md`.
2. Add this implementation plan under `docs/dev/plans/`.
3. Cross-reference the new design from the flight-recorder, Rewind-to-Separation, architecture, and old incomplete-ballistic docs.
4. Add a changelog documentation note.

Validation:

- `git diff --check`
- Manual doc read-through for stale maneuver-node or "scene-exit only" contradictions.

Done when:

- The docs clearly distinguish shipped scene-exit finalization from the proposed cache-based reliability work.

## Phase 1 - Cache Primitives and Pure Apply Logic

**PR shape:** one implementation worktree.

Files likely to change:

- Add `Source/Parsek/RecordingFinalizationCache.cs`
- Add `Source/Parsek/RecordingFinalizationCacheApplier.cs`
- Add tests, likely `Source/Parsek.Tests/RecordingFinalizationCacheTests.cs`
- Touch `Recording.cs` only if a helper is needed to compute last authored UT.

Tasks:

1. Define the in-memory cache payload from the design doc.
2. Implement pure validation and apply/reject logic:
   - match recording id and vessel pid
   - reject terminal UT before last authored sample
   - trim/clamp predicted segments to last authored UT
   - append predicted segments and set existing terminal fields
   - mark files dirty when applying to a recording that may already have flushed data
3. Add a small result enum for diagnostics, for example `Applied`, `RejectedStale`, `RejectedMismatchedRecording`, `RejectedTerminalBeforeLastSample`, `RejectedEmpty`.
4. Add log lines under `[FinalizerCache]`.

Tests:

- apply tail after last sample
- reject stale/terminal-before-last-sample
- reject mismatched recording id or PID
- preserve existing authored points and non-predicted orbit segments
- preserve surface metadata when a surface cache has no position, and stamp orbit metadata for each orbital terminal state
- mark dirty on successful apply
- log assertion for each rejection class

Validation:

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter RecordingFinalizationCache`

## Phase 2 - Refresh Producers

**PR shape:** one implementation worktree after Phase 1.

Files likely to change:

- `Source/Parsek/FlightRecorder.cs`
- `Source/Parsek/BackgroundRecorder.cs`
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs`
- `Source/Parsek/PatchedConicSnapshot.cs`
- `Source/Parsek/BallisticExtrapolator.cs` only if small API seams are needed
- Tests for active/background refresh helpers

Tasks:

1. Add active recorder cache ownership and refresh API:
   - `RefreshFinalizationCache(reason, force)`
   - 5-second cadence
   - digest early-out for stable coast
2. Add background cache ownership keyed by recording id and vessel pid.
3. Trigger refresh on:
   - recording start/promotion
   - active -> background transition
   - throttle/engine/RCS state changes
   - atmosphere entry/exit
   - SOI change
   - go-on-rails/go-off-rails
   - situation change
   - joint break / part death
   - warp-rate change
4. Keep background refresh solver-light:
   - active vessel may use node-free stock conic data
   - background loaded/on-rails paths may use current orbit and `BallisticExtrapolator`
5. Add aggregate/rate-limited refresh logs.
6. Retire `PatchedConicSnapshotResult.StoppedBeforeManeuver` after node-free capture lands, then update all callers and tests so a UI maneuver node can never become a terminal boundary. Phase 2 renames the flag to `EncounteredManeuverNode` and makes the finalizer discard node-contaminated stock tails before falling back to current-state propagation.

Maneuver-node rule:

- Do not preserve the current "stop before maneuver node and call it orbiting" behavior as final.
- If node-free patched-conic capture is easy and safe, implement here.
- If not, explicitly detect node-contaminated chains and fall back to Parsek-owned propagation from the current state vector.

Tests:

- stable coast digest early-out
- forced refresh bypasses cadence
- active -> background transfers cache ownership
- background on-rails stable orbit cache
- suborbital/atmo background cache marks destroyed
- maneuver-node presence does not truncate the cache

Validation:

- targeted xUnit filter for cache refresh tests
- full `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` if touched helpers are broad

## Phase 3 - Scene-Exit Fallback Consumption

**PR shape:** one implementation worktree after Phases 1-2.

Files likely to change:

- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs`
- `Source/Parsek/BackgroundRecorder.cs`
- tests around existing scene-exit finalization

Tasks:

1. Preserve the current precedence:
   - fresh live scene-exit finalizer first
   - finalization cache second
   - trajectory inference last
2. Teach `FinalizeIndividualRecording` and `EnsureActiveRecordingTerminalState` to consume cache when:
   - vessel lookup returns null
   - default finalizer declines due to missing runtime/vessel/orbit
   - `FlightGlobals` probe is unavailable
   - non-scene-exit finalization sees a missing background/leaf vessel but a cache exists
3. Teach background finalization at scene exit to apply each background cache before `FinalizeIndividualRecording` falls back to inference.
4. Log one summary per finalized recording, including which source won.

Tests:

- live finalizer wins over cache
- cache wins when vessel missing
- cache wins when `FlightGlobals` unavailable
- cache wins during manual/revert commit when a background vessel is already missing
- inference logs degraded path when no cache exists
- background cache applies during tree scene-exit finalization

Validation:

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter Finalize`

## Phase 4 - Premature-End Consumers

**PR shape:** one or two implementation worktrees depending on coupling.

Files likely to change:

- `Source/Parsek/BackgroundRecorder.cs`
- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/FlightRecorder.cs`
- crash/coalescer-adjacent tests

Tasks:

1. Consume cache in `BackgroundRecorder.OnBackgroundVesselWillDestroy`.
2. Consume cache in debris TTL and out-of-bubble endings before `BackgroundMap` removal.
3. Consume cache when a vessel is missing/despawned and the background path currently stamps `Destroyed`.
4. Consume active recorder cache after crash coalescing resolves and before post-destruction tree finalization.
5. Consume cache on vessel-unloaded paths that remove or strand a background recording; if a specific unload path already funnels through TTL/out-of-bubble handling, add an explicit comment and test at that funnel.
6. Audit future stop/abandon seams that remove a recording from `BackgroundMap` or end a live recorder without a fresh vessel, and route them through the shared cache applier or log why they are not terminal consumers.
7. Ensure focus switch does not consume cache unless it truly ends the recording; normal active -> background transition transfers ownership.
8. Persist sidecar updates immediately in background end paths that already bypass `OnSave`.
9. For stock atmospheric deletion before predicted impact, set terminal UT to `min(deletionUT, predictedImpactUT)` and trim the cached tail to that UT. If the cache has no predicted impact UT, use deletion UT.

Tests:

- background destroy path appends cached tail and persists
- TTL expiry with live vessel refreshes/applies before ending
- missing vessel path applies cache before destroyed fallback
- active crash path applies cache after coalescer
- normal focus switch transfers cache without finalizing

Validation:

- targeted xUnit filters for background recorder and post-destruction tests
- full xUnit if `ParsekFlight` finalization flow changed broadly

## Phase 5 - Runtime Coverage and Calibration

**PR shape:** one implementation/test worktree after core behavior lands.

Files likely to change:

- `Source/Parsek/InGameTests/RuntimeTests.cs`
- test generators under `Source/Parsek.Tests/Generators/` if synthetic fixtures are needed
- `docs/dev/manual-testing/test-auto-record.md` or a new finalization manual checklist

Runtime canaries:

1. Controllable atmospheric booster unload/delete finalizes `Destroyed` with predicted tail.
2. Stable background orbiter finalizes `Orbiting`.
3. Scene exit mid-burn continues ghost playback past last authored point.
4. Planned maneuver node is ignored during finalization.
5. Focused crash commits destroyed terminal and cache-extended endpoint.

Manual validation:

- Run isolated runtime tests in FLIGHT via `Ctrl+Shift+T`.
- Collect logs with `python scripts/collect-logs.py recording-finalization-cache`.
- Check `KSP.log` for `[FinalizerCache]`, `[PatchedSnapshot]`, `[Extrapolator]`, `[BgRecorder]`, and degraded fallback lines.

Calibration:

- Confirm 5-second cadence is cheap with several background vessels.
- Adjust cache stale threshold only if runtime logs show stale cache application is common.

## Review Checklist

Each implementation PR must be reviewed against:

- Does it follow the source precedence: fresh live -> cache -> inference?
- Does it preserve actual sampled history?
- Does it avoid save-format changes for the cache?
- Does it ignore UI maneuver nodes?
- Are all non-obvious branches logged?
- Are background sidecar writes still durable after cache application?
- Does every new helper with logic have xUnit coverage?
- Is Rewind-to-Separation terminal classification made more reliable without changing its UI semantics?

## Risks

- **False confidence from stale cache:** mitigate with age/digest logging, forced refresh on event seams, and rejection when terminal UT precedes real data.
- **Maneuver-node contamination:** explicitly test planned-node scenarios; do not accept "stopped before maneuver" as a terminal state.
- **Background cost explosion:** use per-recording cadence, digest early-outs, and aggregate logs.
- **Double finalization:** centralize apply/reject and refuse to overwrite already-finalized recordings unless explicitly repairing a degraded path.
- **Sidecar loss after background finalize:** consume cache before existing immediate persistence calls and test `FilesDirty`/save behavior.

## Open Implementation Questions

These should be answered by the Phase 2 code exploration, not by expanding scope in the design doc:

1. Which stock API path gives the safest node-free patched-conic chain without mutating player UI state?
2. Should cache stale threshold be time-based only, or also state-digest based?
3. Is one shared `RecordingFinalizationCacheApplier` enough for active and background paths, or does background persistence need a thin wrapper?
4. Can crash-coalescer cache consumption reuse an existing terminal-state seam, or does it need a new explicit call after coalescing?
