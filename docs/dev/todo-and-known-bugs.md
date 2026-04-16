# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.
Entries 272–303 (78 bugs, 6 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v2.md`.

---

## Post-review follow-ups on PR #307 (career-earnings-bundle)

After the initial 11-bug bundle landed on `fix/career-earnings-bundle`, an independent
Codex review found four follow-up bugs my orchestration pipeline had missed. All four
are fixed in the same PR branch with additional commits:

- **Tree-commit science duplication** (`LedgerOrchestrator.cs`). `NotifyLedgerTreeCommitted`
  re-read `PendingScienceSubjects` once per recording in the tree, so an N-recording tree
  credited each subject N times. Fix: snapshot once at the top, pass to exactly one
  recording via `PickScienceOwnerRecordingId` (highest `EndUT`, ties broken by
  `ActiveRecordingId`), sibling recordings receive an empty sentinel. See bug #397.
- **`DedupKey` not serialized** (`GameAction.cs`). KSC part-purchase dedup depended on
  `DedupKey`, but `SerializeFundsSpending`/`DeserializeFundsSpending` never persisted it,
  so reloads collapsed all KSC purchases to a single `""` key and
  `TryRecoverBrokenLedgerOnLoad` re-synthesized the debits. Fix: round-trip the field,
  2 regression tests. See bug #405.
- **Contract advance never captured** (`GameStateRecorder.cs`, `GameStateEventConverter.cs`).
  `OnContractAccepted` wrote title/deadline/fail-penalties into detail but skipped
  `contract.FundsAdvance`, and `ConvertContractAccepted` had no `funds=` parser. The
  downstream `GameAction.AdvanceFunds` + `FundsModule` consumption was already in place
  but always saw zero. Fix: detail v3 format adds `funds=`, converter parses it, backward
  compat preserved for v2 strings. See bug #405.
- **Milestone science dropped** (`GameAction.cs`, `GameStateEventConverter.cs`,
  `ScienceModule.cs`). `#400`'s fix wrote `sci=` into the milestone detail string but
  `GameAction` had no `MilestoneScienceAwarded` field and `ConvertMilestoneAchieved` read
  only funds/rep. Milestones with science rewards (Kerbin/Science, Kerbin/Landing, etc.)
  produced zero ledger science credit. Fix: schema field added, serialized,
  `ScienceModule.ProcessMilestoneScienceReward` consumes on effective-only actions,
  reconciliation diagnostic counts it, Actions window displays it. See bug #400.

---

## Gloops Flight Recorder

- **Gloops Flight Recorder window** — manual ghost-only recording controls moved from main UI to a dedicated window. Recordings marked `IsGhostOnly`, auto-commit on stop, loop by default, grouped under "Gloops Flight Recordings - Ghosts Only". Parallel FlightRecorder instance with `IsGloopsMode` flag for separate Harmony patch routing, skipped rewind saves, and auto-stop on vessel switch. X delete button in recordings table for ghost-only recordings (no confirmation). Needs in-game verification when KSP is available.

---

# Known Bugs

## ~~411. Playback engine and KSC dispatcher still compute loop duration from raw/hybrid ranges instead of the effective loop range~~

**Source:** `#409` fix pass `2026-04-15`. Surfaced while fixing the `WatchModeController` duration mismatch: three sibling sites in the playback engine and the KSC dispatcher also compute "loop duration" in ways that diverge from the new shared `GhostPlaybackEngine.EffectiveLoopDuration` helper. The `#409` fix was scoped to the watch-mode path and intentionally left these untouched because they predate `#381` and need in-game validation on a save with a real loop subrange before changing.

**Concern:** for recordings that use a custom loop subrange (`LoopStartUT` / `LoopEndUT` narrower than `StartUT` / `EndUT`), three dispatch sites disagree with each other and with `WatchModeController`:

| Site | Duration expression | Shape |
|---|---|---|
| `GhostPlaybackEngine.UpdateLoopingPlayback` (`GhostPlaybackEngine.cs:717-718`) | `traj.EndUT - EffectiveLoopStartUT(traj)` | **hybrid** — effective start, raw end |
| `GhostPlaybackEngine.UpdateOverlapPlayback` (`GhostPlaybackEngine.cs:907`) | `GetActiveCycles(..., loopStartUT, traj.EndUT, ...)` via the hybrid parent | **hybrid** (inherited from parent + raw `traj.EndUT` in the GetActiveCycles bounds) |
| `ParsekKSC` main dispatcher (`ParsekKSC.cs:175`) | `rec.EndUT - rec.StartUT` | **raw full range** |
| `ParsekKSC.TryComputeLoopUT` (`ParsekKSC.cs:699-703`) | `EffectiveLoopEndUT - EffectiveLoopStartUT` | effective (correct) |
| `WatchModeController.TryStartWatchSession` / `ResolveWatchPlaybackUT` (post-`#409`) | `EffectiveLoopDuration(rec)` | effective (correct) |

Concrete divergence: take a 300s recording with loop subrange `[100, 200]` (effective duration = 100s) and `LoopIntervalSeconds = 80`.

- `UpdateLoopingPlayback` sees `duration = 300 - 100 = 200`. `IsOverlapLoop(80, 200)` → **overlap** path.
- `TryComputeLoopUT` (KSC single-ghost path) sees `duration = 100`. Pause-window check uses `100`.
- `WatchModeController` (post-`#409`) sees `duration = 100`. `IsOverlapLoop(80, 100)` → **overlap** path.
- `ParsekKSC` main dispatcher sees `duration = 300 - 0 = 300`. `IsOverlapLoop(80, 300)` → **overlap** path.

The flight engine's overlap-vs-single decision and its pause-window/phase clamp are computed from a hybrid that silently lets playback run past the loop subrange's end into the post-loop portion of the recording. `UpdateOverlapPlayback` then calls `GetActiveCycles(..., loopStartUT, traj.EndUT, ...)` — which computes cycle bounds over the half-range — so the number of simultaneously active cycles is wrong whenever `EffectiveLoopEndUT < traj.EndUT`. `ParsekKSC`'s main dispatcher uses the raw full range, which flips to the overlap path even when the subrange makes `period >= effectiveDuration` and the single-ghost path would be correct.

**Why it didn't get caught under `#381`:** loop subranges are a niche feature. The `#381` fix pass touched every call site's formula but kept the "which start/end fields to use" semantics identical to pre-`#381` — only the arithmetic on those fields changed. The raw-vs-effective asymmetry predates both `#381` and `#409`.

**Proposed fix:**

1. Centralize "effective loop duration" via the existing `GhostPlaybackEngine.EffectiveLoopDuration` helper (already added for `#409`). No new API.
2. Rewrite `UpdateLoopingPlayback` line 717-718:
   ```csharp
   double loopStartUT = EffectiveLoopStartUT(traj);
   double duration = EffectiveLoopDuration(traj);
   ```
3. Rewrite `UpdateOverlapPlayback` line 907 to pass `EffectiveLoopEndUT(traj)` as the cycle bound (not `traj.EndUT`). Also verify the `primaryCycleStartUT = loopStartUT + lastCycle * cycleDuration` reference is correct after the fix — `loopStartUT` already comes from `EffectiveLoopStartUT`, so the reference frame is consistent.
4. Rewrite `ParsekKSC.cs:175` to use `EffectiveLoopDuration(rec)` for the overlap-vs-single decision, matching `TryComputeLoopUT` which already uses the effective range. This aligns the main KSC dispatcher with its own single-ghost helper.
5. Audit `GetActiveCycles` callers elsewhere in the engine for any other `(rec.StartUT, rec.EndUT)` vs `(loopStartUT, loopEndUT)` pair.
6. `UpdateExpireAndPositionOverlaps` takes `duration` and `loopStartUT` as parameters — verify the parameters are still correct after the parent rewrites (should just be the new effective duration + effective start, same variable names).

**Tests:**

- Unit: extend `GhostPlaybackEngineTests` with a `UpdateLoopingPlayback_LoopSubrange_UsesEffectiveDuration` integration case that asserts the overlap decision agrees with `EffectiveLoopDuration` and not `traj.EndUT - loopStartUT`. Can be pure-static if we lift the decision logic into a helper.
- Unit: extend `KscGhostPlaybackTests` with the same LoopSubrange overlap-decision guard for `ParsekKSC.cs:175`.
- In-game: create a synthetic recording with a real loop subrange (e.g. `WithLoop(period=80)` + `LoopStartUT=100, LoopEndUT=200` inside a `[0, 300]` range), enable loop playback, and verify:
  - The ghost restarts at `loopStartUT=100`, not `0`.
  - Playback ends at `loopEndUT=200`, not `300` (no playback past the subrange).
  - At period=80 (< effective duration 100), the user sees overlapping ghosts.
  - At period=110 (> effective duration 100), the user sees single-ghost with a pause window.
  - The same recording, opened from KSC (tracking station) instead of flight, shows the same number of active ghosts and the same pause behavior.

**Files to touch:**

- `Source/Parsek/GhostPlaybackEngine.cs` — `UpdateLoopingPlayback`, `UpdateOverlapPlayback`, `UpdateExpireAndPositionOverlaps` (param audit).
- `Source/Parsek/ParsekKSC.cs` — main dispatcher line 175.
- `Source/Parsek.Tests/GhostPlaybackEngineTests.cs` — loop-subrange dispatch tests.
- `Source/Parsek.Tests/KscGhostPlaybackTests.cs` — loop-subrange dispatch tests.
- `CHANGELOG.md` and this entry on completion.

**Status:** ~~Fixed~~. Size: S-M. Not a regression — the behavior predates `#381`. `#409` fixed the watch-mode half of the same drift; this closes out the remaining flight/KSC range math.

**Fix:** Initial landing in `eba44b1b` rewrote the three core dispatch sites (`GhostPlaybackEngine.UpdateLoopingPlayback` now branches on `EffectiveLoopDuration(traj)`; `UpdateOverlapPlayback` bounds `GetActiveCycles` on `EffectiveLoopEndUT(traj)`; `ParsekKSC` main dispatcher uses `EffectiveLoopDuration(rec)`) and completed the KSC audit by anchoring `UpdateOverlapKsc`'s activation, cycle starts, phase clamping, and active-cycle selection on `EffectiveLoopStartUT`/`EffectiveLoopEndUT` instead of raw `rec.StartUT`/`rec.EndUT`.

In-game and review-driven follow-ups then closed out the rest of the raw-vs-effective drift across the entire loop subsystem:

- **`14ae6ba3` Legacy loop migration + runtime epsilon.** Added `GhostPlaybackEngine.ConvertLegacyGapToLoopPeriodSeconds` (later renamed `TryConvertLegacyGapToLoopPeriodSeconds`) to upgrade pre-`#381` negative-gap saves to `effectiveDuration + legacyGap`. Fixed the load order in both `ParsekScenario.LoadRecordingMetadata` and `RecordingTree.LoadRecordingFrom` so `loopStartUT`/`loopEndUT` parse before `loopIntervalSeconds` (the migration needs the subrange). Extended `GhostPlaybackLogic.BoundaryEpsilon` to the live `GhostPlaybackEngine.TryComputeLoopPlaybackUT` (instance) and `ParsekKSC.TryComputeLoopUT` — the original `#410` fix only touched the static helpers, missing the runtime schedulers that the playback engine actually calls each frame.
- **`1df9d119` Loop-end teardown for subrange playback.** New `GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(traj) = EffectiveLoopEndUT(traj)` helper and new `PositionGhostAtLoopEndpoint` (flight + KSC versions). Every pause-window / cycle-change / overlap-expire / destroyed-explosion position path that used to position at the raw `rec.Points[last]` now positions at the effective loop end, so ghosts no longer play past the subrange into the post-loop portion of the recording.
- **`1c0c5339` Loop destruction timing.** New shared `GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(traj, playbackUT)` helper unifies the "should we fire the destroyed-vessel explosion at this UT?" decision across six duplicated call sites in the flight engine and KSC dispatcher, using `ResolveLoopPlaybackEndpointUT` instead of raw `traj.EndUT`. Fixes a timing bug where loop-subrange recordings with `TerminalState.Destroyed` were exploding at the wrong UT.
- **`33371d4b` Legacy tree migration + quiet overlap handoff.** Introduced `RecordingStore.LaunchToLaunchLoopIntervalFormatVersion = 4`; bumped `CurrentRecordingFormatVersion` from 3 → 4. `ConvertLegacy...` became `TryConvertLegacy...` (returns false when duration isn't hydrated). Deferred migration via new `RecordingStore.MigrateLegacyLoopIntervalAfterHydration` fires after sidecar load with the real duration. `UpdateExpireAndPositionOverlaps` now fires `ExplosionHoldEnd` instead of `ExplosionHoldStart` for non-destroyed overlap cycle expiries, so the watch camera bridge doesn't stall waiting for an explosion that never comes.
- **`6034853b` Deferred migration + bridge retry.** Dropped the `LoopIntervalSeconds >= 0` early-exit from `MigrateLegacyLoopIntervalAfterHydration` (was rejecting migrations that happened to land on a non-negative result). Added `NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec)` to bump the format version to 4 after a successful migration so the same recording doesn't re-migrate on the next load. Added the overlap camera-bridge retry state machine: `OverlapBridgeRetargetState.ExitWatch`, `MaxPendingOverlapBridgeFrames = 3`, Unity-safe object helpers, and a bounded retry budget so the watch camera exits cleanly if the primary ghost never returns.
- **`ba5f57ab` Sidecar loop version persistence.** `TrajectorySidecarBinary` now distinguishes `SparsePointBinaryVersion = 3` (actual binary layout) from `CurrentBinaryVersion = RecordingStore.CurrentRecordingFormatVersion = 4` (metadata semantic). Load accepts v2/v3/v4; save emits v4 for any recording at `RecordingFormatVersion >= 4`. Without this, the v4 migration couldn't persist through sidecar round-trips.
- **`9308c7cd` Overlap bridge retry accounting.** New `overlapBridgeLastRetryFrame` per-session field + `AdvanceOverlapBridgeWaitFrames` helper prevents the retry budget from being consumed twice per frame. Reset at all camera-event cleanup sites and in `ResetWatchState`.
- **`14bf03fb` Test/CHANGELOG alignment.** `FormatVersionTests` updated to assert v4; `RecordingStoreTests.RecordingMetadata_Load_MissingFields_*` updated for the "missing field = legacy v0" contract; CHANGELOG `#381` entry rewritten to reflect the full version-migration behavior instead of the original "clamped to 1 second" wording.
- **Final review fix (v4 sidecar demotion).** `TrajectorySidecarBinary.Read` was unconditionally assigning `rec.RecordingFormatVersion = probe.FormatVersion`, which demoted any just-stamped v4 (from the tree-load migration with explicit bounds) back to v3 — causing `MigrateLegacyLoopIntervalAfterHydration` to fire a SECOND time and double the period. The fix is a one-line promote-only guard at `TrajectorySidecarBinary.cs:195`. Regression test `LoopRange_Tree_Load_LegacyWithExplicitBoundsAndSidecar_MigratesOnceNotTwice` in `LoopAnchorTests` exercises the full path: tree node with `explicitStartUT`/`explicitEndUT` + v3 sidecar → migration fires at tree-load time → sidecar load must NOT re-migrate. Without the fix, `loaded.LoopIntervalSeconds` would jump from 110 to 210 on sidecar hydration.

Targeted verification across all follow-ups: `dotnet test Source\Parsek.Tests\Parsek.Tests.csproj` → 6241 passed / 0 failed / 1 skipped (the pre-existing Unity-runtime `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` skip tracked as `#380`).

---

## ~~410. `ComputeLoopPhaseFromUT` and `TryComputeLoopPlaybackUT` disagree at the exact `phase == duration` boundary~~

**Source:** surfaced during the `#381` (launch-to-launch loop semantics) review pass `2026-04-15`. Not introduced by `#381` — the inconsistency predates the refactor, but both helpers now share the same cycleDuration formula, so the divergence is easier to reason about.

**Concern:** the two helpers in `Source/Parsek/GhostPlaybackLogic.cs` describe the same playback model but disagree on what happens when a cycle's elapsed phase equals its recording duration exactly:

| Helper | Check | At `phase == duration` |
|---|---|---|
| `ComputeLoopPhaseFromUT` (~line 372) | `if (phaseInCycle < duration)` (strict) | falls through to pause branch → "in pause window" |
| `TryComputeLoopPlaybackUT` (~line 167) | `if (phase > duration + epsilon) return false` | stays in playback branch → "not in pause" |

An existing test `LoopPhaseTests.AtRecordingEnd_BoundaryEntersPause` locks in the strict-less-than behavior of `ComputeLoopPhaseFromUT`.

**Why it matters:** callers that query both helpers within the same frame (e.g. watch-mode that asks `ComputeLoopPhaseFromUT` for a display phase and `TryComputeLoopPlaybackUT` for a playback UT) can observe a "playing → paused → playing" flicker at the single physics frame where `currentUT - cycleStart` lands exactly on `duration`. In practice the window is narrow (floating-point UT arithmetic rarely produces exact equality for long), but when it does hit, the ghost visual blips.

**Proposed fix:** pick a consistent rule. The epsilon-tolerant version in `TryComputeLoopPlaybackUT` is safer under floating-point UT math, so the cleaner direction is:

1. Rewrite `ComputeLoopPhaseFromUT` to use `phaseInCycle < duration - epsilon` (or equivalently, the pause branch fires on `phaseInCycle >= duration - epsilon`).
2. Add the same `const double epsilon = 1e-6` that `TryComputeLoopPlaybackUT` uses, or lift it to a shared `MinCycleEpsilon` on `GhostPlaybackLogic`.
3. Rewrite `AtRecordingEnd_BoundaryEntersPause` to verify the epsilon-inclusive boundary — the test should still pass with `phaseInCycle = duration` mapping to pause under `phaseInCycle >= duration - epsilon`, so the rename is cosmetic; but add a new test `AtRecordingEnd_JustBeforeBoundary_StaysPlaying` with `phaseInCycle = duration - 2*epsilon` to lock in the tolerant-but-still-strict behavior.

**Files to touch:**

- `Source/Parsek/GhostPlaybackLogic.cs` (`ComputeLoopPhaseFromUT`, epsilon constant)
- `Source/Parsek.Tests/LoopPhaseTests.cs` (`AtRecordingEnd_BoundaryEntersPause` + new test)

**Status:** ~~Fixed~~. Size: XS.

**Fix:** Added a shared `GhostPlaybackLogic.BoundaryEpsilon = 1e-6` constant. Rewrote `ComputeLoopPhaseFromUT`'s playback/pause gate as `phaseInCycle <= duration + BoundaryEpsilon` (matching the epsilon-tolerant form already used by `TryComputeLoopPlaybackUT`). At the exact boundary, both helpers now report "still playing the final frame, loopUT=endUT" — the ghost visual lands at `recordingEndUT` either way, but the `isInPause` flag agrees across helpers, eliminating the one-frame flicker. `TryComputeLoopPlaybackUT` now references `BoundaryEpsilon` instead of its own inline `const double epsilon = 1e-6`. Rewrote `LoopPhaseTests.AtRecordingEnd_BoundaryEntersPause` → `_ExactBoundary_StaysInPlayback`, added `_JustPastBoundary_StaysInPlaybackWithinEpsilon` and `_BeyondEpsilon_EntersPause`, and added a new `BoundaryConsistency_WithTryComputeLoopPlaybackUT` cross-helper consistency assertion.

---

## ~~409. `ResolveWatchPlaybackUT` uses `rec.EndUT - rec.StartUT` while `TryStartWatchSession` uses `EffectiveLoopEndUT - EffectiveLoopStartUT` for the same overlap decision~~

**Source:** surfaced during the `#381` (launch-to-launch loop semantics) review pass `2026-04-15`. Not introduced by `#381` — both sites predate the refactor — but `#381` makes the mismatch matter because both dispatches now depend on `IsOverlapLoop(interval, duration)`, so the `duration` value they compute is load-bearing.

**Concern:** two sites in `Source/Parsek/WatchModeController.cs` ask the same question — "should I take the overlap / multi-cycle dispatch path?" — but compute `duration` differently:

| Site | Duration expression | Respects loop subrange? |
|---|---|---|
| `TryStartWatchSession` (~line 1344) | `watchRecDuration = GhostPlaybackEngine.EffectiveLoopEndUT(rec) - GhostPlaybackEngine.EffectiveLoopStartUT(rec)` | yes |
| `ResolveWatchPlaybackUT` (~line 2589) | `duration = rec.EndUT - rec.StartUT` | no |

Under pre-`#381` semantics both sites dispatched on `intervalSeconds < 0`, so `duration` did not participate in the check — the mismatch was dormant. Under `#381`, both dispatch on `GhostPlaybackLogic.IsOverlapLoop(interval, duration)`, and the two sites can disagree.

**Concrete reproducing case:** a 600s recording with a 60s loop subrange (`LoopStartUT`/`LoopEndUT` set to a 60-second window inside the recording) and `LoopIntervalSeconds = 80`:

- `TryStartWatchSession`: `IsOverlapLoop(80, 60) → 80 < 60 → false` → single-ghost dispatch.
- `ResolveWatchPlaybackUT`: `IsOverlapLoop(80, 600) → 80 < 600 → true` → multi-cycle overlap dispatch.

Watch mode enters via one code path, UT resolution uses the other — positioning drifts from the animation state. Expected symptom: the ghost visual shows one cycle playing while the `loopPhaseOffsets` / `loopCycleIndex` book-keeping thinks multiple cycles are live, producing stale `cycleStartUT` computations after the first cycle boundary.

**Proposed fix:**

1. Centralize the "effective loop duration" computation in a helper on `GhostPlaybackLogic` or `GhostPlaybackEngine` — e.g. `internal static double EffectiveLoopDuration(IPlaybackTrajectory rec) => EffectiveLoopEndUT(rec) - EffectiveLoopStartUT(rec);`. Reuse it at every site that asks "how long is one cycle?"
2. Switch `ResolveWatchPlaybackUT`'s `duration` local to use the new helper.
3. Audit `GhostPlaybackEngine`, `ParsekKSC`, and `WatchModeController` for any other site that computes `rec.EndUT - rec.StartUT` near a loop-dispatch check — the same drift can hide elsewhere.
4. Add an integration test: construct a mock trajectory with `LoopStartUT/LoopEndUT` narrower than `StartUT/EndUT`, call both `TryStartWatchSession`'s overlap check and `ResolveWatchPlaybackUT`'s overlap check with the same interval, assert they return the same decision.

**Files to touch:**

- `Source/Parsek/WatchModeController.cs` (`ResolveWatchPlaybackUT`, line ~2589)
- `Source/Parsek/GhostPlaybackLogic.cs` or `GhostPlaybackEngine.cs` (new helper)
- Possibly `Source/Parsek/ParsekKSC.cs` (audit for the same pattern)
- `Source/Parsek.Tests/` (new integration test covering the dispatch-parity case)

**Status:** ~~Fixed~~. Size: S.

**Fix:** Added a shared `GhostPlaybackEngine.EffectiveLoopDuration(traj) = EffectiveLoopEndUT(traj) - EffectiveLoopStartUT(traj)` helper. Added a pure-static `GhostPlaybackLogic.ComputeOverlapCycleLoopUT(currentUT, loopStartUT, duration, intervalSeconds, loopCycleIndex)` that encapsulates the overlap-cycle UT math with phase clamping, so the watch sites no longer duplicate it inline. Rewrote `WatchModeController.ResolveWatchPlaybackUT` to use `EffectiveLoopStartUT` + `EffectiveLoopDuration` as the cycle reference frame (was `rec.StartUT` + raw full-range duration) and to delegate the arithmetic to the new helper. Updated `WatchModeController.TryStartWatchSession` to call the shared `EffectiveLoopDuration` too, so both sites read the same formula. Added tests `EffectiveLoopDuration_NoSubrange_EqualsFullRange`, `_WithSubrange_EqualsSubrange`, `_SubrangeAndFullRange_OverlapDecisionDiffers` (the regression guard — asserts that a 50s subrange + 80s period would disagree between the raw-range and effective-range dispatch), and five `ComputeOverlapCycleLoopUT_*` tests covering cycle 0, higher cycles, phase clamping (both ends), and defensive negative-interval clamping.

**Out of scope (follow-up):** tracked as `#411`. The playback engine's `UpdateLoopingPlayback` / `UpdateOverlapPlayback` and `ParsekKSC`'s main dispatcher still compute "duration" from raw/hybrid ranges instead of `EffectiveLoopDuration`. Predates `#381`, closes out the drift engine-wide, needs in-game validation on a save with a real loop subrange.

---

## ~~406. CRITICAL: map-view framerate collapses with many looping showcase ghosts because every loop-cycle rebuild runs the full reentry FX build pipeline even for stationary part showcases~~

**Source:** `test career` playtest `2026-04-15` (logs `logs/2026-04-15_2034_showcase-loop-perf/`). Player opened flight map view with ~260 active showcase loops; FPS tanked. The `[Parsek][WARN][Diagnostics] Playback frame budget exceeded: 12.3ms (259 ghosts, warp: 1x) | suppressed=106` line proves the `GhostPlaybackEngine.UpdatePlayback` path alone was blowing the 8 ms budget on most frames, *before* any map OnGUI/icon render. 265 primary ghost audio sources paused on ESC (`PauseAllGhostAudio: paused 265 primary + 0 overlap`) confirms the active ghost count. `PauseAllGhostAudio`-scale log spam of `combined 7 meshes into emission shape (4346 verts) for ghost #N | suppressed=839` confirms the reentry FX mesh-combine was firing many hundreds of times per rate-limit window.

**Root cause:** `GhostPlaybackEngine.TryPopulateGhostVisuals` (call site was `GhostPlaybackEngine.cs:1949`) called `GhostVisualBuilder.TryBuildReentryFx` unconditionally on every ghost build. Ghost builds happen on loop-cycle boundaries because the engine destroys + respawns the ghost (`DestroyGhost(reason: "loop cycle boundary")` followed by `SpawnGhost`, triggered by `HasLoopCycleChanged`). `TryBuildReentryFx` per call allocates:

1. Combined emission `Mesh` (`CombineMeshes` on every MeshFilter in the ghost hierarchy),
2. A `ParticleSystem` (`ReentryFire`) with runtime-generated soft-circle texture + additive material,
3. Fire shell overlay `MaterialPropertyBlock` + material clone,
4. Cloned glow materials for every renderer on the ghost via `CollectReentryGlowMaterials`.

For stationary part showcases (lights, solar panels, antennae sitting on KSC ground), reentry is physically impossible — `ComputeReentryIntensity` requires Mach ≥ 2.5 and density ≥ 0.0015 kg/m³. The build work is pure waste, and it was being paid hundreds of times per second across the looping fleet.

**Fix (PR #309):**

1. New pure static helper `TrajectoryMath.HasReentryPotential(IPlaybackTrajectory)` — returns `true` iff the trajectory has any orbit segments OR any recorded trajectory point has velocity magnitude at or above `TrajectoryMath.ReentryPotentialSpeedFloor` (`400 m/s`). 400 m/s is well under Mach 1.5 on every stock body, so the gate cannot hide any real reentry heating. Orbit segments are always considered high-speed because de-orbit happens at ~2300 m/s.
2. `GhostPlaybackEngine.TryPopulateGhostVisuals` now gates the `TryBuildReentryFx` call behind `HasReentryPotential`. When skipped, `state.reentryFxInfo` stays `null`; all downstream paths already null-guard (`UpdateReentryFx` early-returns at `GhostPlaybackEngine.cs:1395`, `RebuildReentryMeshes` early-returns at `GhostVisualBuilder.cs:6311`, `DestroyReentryFxResources` early-returns at `GhostPlaybackEngine.cs:2577`, `ParsekKSC.cs:519` already documents this pattern).
3. Matching gate in the `ParsekFlight` preview path (`ParsekFlight.cs:7407`) so manual preview playback gets the same savings.
4. New session counters `DiagnosticsState.health.reentryFxBuildsThisSession` / `reentryFxSkippedThisSession` make the gate visible in the live diagnostics stream, and a rate-limited `ReentryFx` `Verbose` log line announces each skip with the ghost index, vessel name, and reason.
5. Unit tests `ReentryPotentialTests` (15 cases) cover: null trajectory, empty, stationary showcase, EVA walk, 200 m/s Flea hop, just-below-floor, at-floor, fast suborbital point, diagonal magnitude, orbit-segment-only recordings, speed floor constant, and the NaN / +Infinity / null-`Points` input edges added in the Opus review follow-up.

**Expected impact:** for the 259-ghost showcase scenario in the smoking-gun logs, ~250 of those ghosts are stationary KSC showcases whose `reentryFxInfo` now stays `null` permanently. The mesh-combine + ParticleSystem + cloned-material allocation is skipped on every loop-cycle rebuild, eliminating the dominant cost in `UpdatePlayback` and taking the playback frame cost back below the 8 ms budget threshold. Only genuine flight recordings (Suborbital Arc, Orbit-1, Kerbin Ascent, etc.) still pay the build cost, and they pay it correctly.

**Status:** fix landed on branch `investigate-showcase-loop-perf`. Follow-up candidates (not in this PR): reuse ghost GameObject across loop cycles instead of destroy/rebuild (would eliminate the remaining per-cycle material/audio clone cost); gate hidden-tier prewarm by reentry potential too.

---

## ~~405. CRITICAL: career-mode contract accept/complete/fail/cancel events and `PartPurchased` never reach the ledger — `PatchContracts` destroys active contracts on every recalc~~

**Source:** c1 career-mode playtest `2026-04-15` (logs `logs/2026-04-15_0005_c1-career-bugs/`). Player accepted 2 contracts at UT ≈ 412.9 (`Test RT-5 "Flea"` and `Test LV-T45 "Swivel"` at launch site), both captured by `GameStateRecorder.OnContractAccepted` and written to `events.pgse`, both unconditionally destroyed by the next `PatchContracts` pass. Ledger at end of session has **zero** `ContractAccept`, `ContractComplete`, `ContractFail`, `ContractCancel`, or `FundsSpending (PartPurchased)` actions.

**Concern:** five `GameStateRecorder` handlers write their events to `GameStateStore.AddEvent` but are missing the `LedgerOrchestrator.OnKscSpending(evt)` real-time ledger write that all the other KSC-happy handlers have:

| Handler | File:line | OnKscSpending call? |
|---|---|---|
| `OnContractOffered` | `GameStateRecorder.cs:142-166` | **missing** (arguably fine — offered is not a commitment) |
| `OnContractAccepted` | `GameStateRecorder.cs:168-210` | **missing** |
| `OnContractCompleted` | `GameStateRecorder.cs:212-225` | **missing** |
| `OnContractFailed` | `GameStateRecorder.cs:227-240` | **missing** |
| `OnContractCancelled` | `GameStateRecorder.cs:242-255` | **missing** |
| `OnPartPurchased` | `GameStateRecorder.cs:319-339` | **missing** |
| `OnKerbalAdded` (CrewHired) | `GameStateRecorder.cs:345-381` | line 380 ✓ |
| `OnResearched` (TechResearched) | `GameStateRecorder.cs:270-317` | line 316 ✓ |
| `OnProgressComplete` (MilestoneAchieved) | `GameStateRecorder.cs:667-713` | line 712 ✓ |
| `PollFacilityState` (FacilityUpgraded) | `GameStateRecorder.cs:814-863` | line 857 ✓ |

The consequence is that any contract state that happens at KSC (accept is *always* at KSC; complete/fail/cancel can happen at KSC) can only enter the ledger via a recording commit whose `[startUT, endUT]` window happens to include the event's UT. Contracts accepted between flights are in the "gap" between recordings — `GameStateEventConverter.ConvertEvents` treats them as `outOfRange` and they sit in `GameStateStore.Events` forever without ever producing a `ContractAccept` action. Same for `PartPurchased`: VAB part-purchase-on-build happens outside the flight window, so no `FundsSpending` is created — which directly contributes to the funds collapse in `#403`.

Log evidence (c1 session, the smoking gun):

```
11569 AddEvent: ContractAccepted key='6c23609d-3ff4-44ae-9bc4-57f57e23dda7' epoch=2 ut=412.9 (total=60)
11571 ContractAccepted 'Test RT-5 "Flea" Solid Fuel Booster at the Launch Site.' ... (snapshot saved)
11577 AddEvent: ContractAccepted key='480e5b44-6fbf-465b-8b08-f34036519db7' epoch=2 ut=412.9 (total=61)
      ... no OnKscSpending call, no ledger action written ...
11738 PatchContracts: ledger has 0 active contracts, KSP has 6 current contracts
11739 PatchContracts: cleared KSP contract lists (unregistered=2)       ← both accepted contracts destroyed
11740 PatchContracts: restored=0, registered=0, ... ledgerActive=0
```

c1 `events.pgse` has **47 ContractOffered + 2 ContractAccepted + 8 PartPurchased** events; c1 `ledger.pgld` has **zero** of the corresponding actions. This is the same "store says yes, ledger says no, patcher drags KSP to match ledger" anti-pattern as `#397` but for the KSC event pipeline instead of the `PendingScienceSubjects` pipeline.

**Fix:** Added `if (!IsFlightScene()) LedgerOrchestrator.OnKscSpending(evt);` to `OnContractAccepted`, `OnContractCompleted`, `OnContractFailed`, `OnContractCancelled`, and `OnPartPurchased` in `Source/Parsek/GameStateRecorder.cs`. Contract events now flow through the existing `GameStateEventConverter.ConvertContractAccepted/Completed/Failed/Cancelled` dispatch, and `OnPartPurchased` relies on the new `DedupKey` field (see §F of `career-earnings-bundle.md`) to avoid collisions between multiple part purchases at similar UTs. Regression tests in `Source/Parsek.Tests/GameStateRecorderLedgerTests.cs`.

**Status:** ~~Fixed~~

---

## ~~404. `PatchContracts` unconditionally clears KSP's `ContractsFinished` list and `Offered` contracts, neither of which the ledger tracks~~

**Source:** same c1 playtest as `#405`.

**Concern:** `KspStatePatcher.PatchContracts` lines 613-643 do this, every recalc cycle:

```csharp
// 1. Unregister all currently active contracts
for (int i = 0; i < currentContracts.Count; i++) {
    if (currentContracts[i].ContractState == Contract.State.Active) {
        currentContracts[i].Unregister();
    }
}
// 2. Clear both lists
currentContracts.Clear();                    // <-- wipes Offered too
ContractSystem.Instance.ContractsFinished.Clear();   // <-- wipes completed history
// 3. Rebuild active contracts from snapshots
...
```

Then step 3 only restores contracts whose IDs come from `ContractsModule.GetActiveContractIds()` — i.e., `Active`-state contracts only. Two categories of contract state are destroyed with no restoration:

- **Offered contracts** (the list the player sees in Mission Control when they walk in). KSP's `ContractSystem` re-generates them on the next cycle, but the destroy-then-regenerate loop explains the `ContractOffered` event flood in the c1 log (47 events in one session, all duplicates of the same underlying set of `RT-5 Flea`, `LV-T45 Swivel`, etc. test contracts).
- **`ContractsFinished`** (the player's completed/failed/cancelled contract history). Once cleared, no code path restores it because the `ContractsModule` only tracks active IDs, not the finished list. Any completed contract the player accomplished before Parsek's first `PatchContracts` is gone from the history UI.

Log evidence — same 7 offered contracts appear at 3 different UTs (lines 9369-9382 at ut=52.6; 9829-9842 at ut=204.0; 10974-10987 at ut=395.6; 11744-11761 at ut=420.5) — that's not 28 unique contracts, it's 7 unique contracts that `ContractSystem` kept regenerating after each `PatchContracts` wipe.

**Fix:** Replaced the destructive `currentContracts.Clear()` + `ContractsFinished.Clear()` in `Source/Parsek/GameActions/KspStatePatcher.cs` `PatchContracts` with a filtered partition. Only Active contracts whose id is NOT in the ledger's active set are removed; non-Active entries (Offered/Declined/Cancelled/Failed/Completed) stay untouched, and `ContractsFinished` is never mutated. Active contracts already in the ledger are preserved in place (no unregister/recreate cycle), which also closes the `ContractOffered` regeneration loop that caused #398. Filtering extracted into the testable `KspStatePatcher.PartitionContractsForPatch` helper. Tests in `Source/Parsek.Tests/PatchContractsPreservationTests.cs`.

**Status:** ~~Fixed~~

---

## ~~403. Career-mode `FundsEarning` and `ReputationEarning` actions are never emitted — running totals stuck at seed for the entire timeline~~

**Source:** same c1 playtest as `#405`.

**Concern:** `GameStateEventConverter.cs:121-130` deliberately drops `FundsChanged`, `ScienceChanged`, and `ReputationChanged` events with a "no GameAction equivalent" comment. Nothing replaces them. Looking at the entire c1 ledger, there are **zero** `FundsEarning` (type 2) or `ReputationEarning` (type 9) actions even though the player:

- Recovered 3 vessels (each captured as `ScienceChanged key='VesselRecovery'` and `FundsChanged key='Progression'` in `events.pgse`, both dropped at convert time)
- Earned milestone rewards: `FirstLaunch` normally grants 4000 funds + rep, `Kerbin/Landing` grants more — both milestones are in the ledger with `milestoneFundsAwarded = 0` and `milestoneRepAwarded = 0` because `OnProgressComplete` (line 687-698) explicitly hardcodes the reward to 0, per its own comment: *"Funds and rep rewards are not directly available on the ProgressNode ... we capture 0 here. See deferred items D17/D18"*
- Would have earned contract rewards on completion — but `#405` means no `ContractComplete` actions enter the ledger in the first place, so the reward capture path at `ConvertContractCompleted:372-404` never runs for KSC-completed contracts

The `FundsModule.totalEarnings` field (`FundsModule.cs:49`) stays at `0` for the entire timeline. `GetAvailableFunds` (line 481-485) returns `initialFunds + totalEarnings - totalCommittedSpendings`, clamped to 0. The c1 session gets `25000 + 0 - 434791 = -409791` → clamped to `0`. Every `PatchFunds` call from that point on drags KSP's funds down to `0`, which is `#402` below.

The `ScienceChanged` channel has a partial parallel path (`OnScienceReceived → PendingScienceSubjects → ConvertScienceSubjects → ScienceEarning`), but that path is broken by `#397`. The `FundsChanged` and `ReputationChanged` channels have no parallel path at all.

**Fix:** No code change — the missing earnings were a composite symptom of #405 (contract events not reaching the ledger), #404 (contracts destroyed on recalc), #400 (milestones hardcoded to zero rewards), and the existing `CreateVesselCostActions` recovery path not being misdiagnosed after all. The review (§1.3) verified `CreateVesselCostActions` correctly emits `FundsEarning(Recovery)` whenever `TerminalStateValue == Recovered`; the todo's "silently bails" claim was wrong. Regression guard added in `Source/Parsek.Tests/VesselCostRecoveryRegressionTests.cs` so future refactors can't drop the recovery emission without a red test. The drop block in `GameStateEventConverter.cs:121-130` stays as-is; re-emitting `FundsChanged` would double-count against recovery + contract + milestone channels (see `career-earnings-bundle-review.md` §5.1). The new `LedgerOrchestrator.ReconcileEarningsWindow` diagnostic (#394) surfaces any future regressions loudly at commit time.

**Status:** ~~Fixed~~

---

## ~~402. `PatchFunds` drags KSP's fund balance down to 0 every recalc (visible symptom of `#403`)~~

**Source:** same c1 playtest as `#405`. The player-facing symptom: **funds slider reads 0 within seconds of any scene transition or commit**.

**Concern:** `KspStatePatcher.PatchFunds` (lines 107-147) is the career-mode analogue of `PatchScience` in `#397` — it reads `Funding.Instance.Funds`, compares to `FundsModule.GetAvailableFunds()`, and issues `AddFunds(delta)` to close the gap. When the ledger has no `FundsEarning` actions (because of `#403`), `GetAvailableFunds` returns `max(seed - spendings, 0) = 0`, and `AddFunds(-currentFunds)` wipes the pool.

Log evidence:

```
11706 Funds: Reset: prevSeed=25000, prevBalance=-409791, prevSpendings=434791, prevEarnings=0
11713 Funds: FundsInitial: seed=25000, runningBalance=25000
11718-11724 KerbalHire: -62113 * 7 kerbals, all affordable=False, runningBalance=-409791
11733 PatchFunds: 3534.0 -> 0.0 (delta=-3534.0, target=0.0)
```

The `affordable=False` on every kerbal hire (lines 11718-11724) is a cascading diagnostic — the `KerbalsModule` / `FundsModule` reservation check thinks the player couldn't afford the seven kerbals they demonstrably did hire, because the walk starts from a running balance that's already negative due to missing `FundsEarning` actions.

**Fix:** Added a defensive WARN to `Source/Parsek/GameActions/KspStatePatcher.cs` `PatchFunds` and `PatchScience` that fires when a single recalc removes more than 10% of a non-trivial resource pool (>1000). This is log-only — legitimate revert walks can subtract large amounts — but the shape of a missing earning channel always produces a >10% drop, so the diagnostic is a cheap tripwire. The threshold check is extracted into `KspStatePatcher.IsSuspiciousDrawdown` for unit testing. The underlying symptom is resolved by the #405/#404/#400 fixes landing in this bundle. Tests in `Source/Parsek.Tests/PatchFundsSanityTests.cs`.

**Status:** ~~Fixed~~

---

## ~~401. c1 career save needs one-shot funds/contract recovery after `#403`/`#405` land~~

**Source:** same playtest as `#405`. The c1 save (`saves/c1/Parsek/GameState/{events.pgse, ledger.pgld}`) is in a broken state that fixing the code forward will not repair.

**Concern:** c1 `events.pgse` contains:

- 47 `ContractOffered` (useless)
- 2 `ContractAccepted` (ut=412.9, with full contract snapshots via `GameStateStore.AddContractSnapshot`)
- 14 `FundsChanged` (all dropped)
- 9 `ScienceChanged` (all dropped, `VesselRecovery` reason)
- 8 `PartPurchased` (never converted due to `#405`)
- 2 `TechResearched`, 4 `MilestoneAchieved`, 7 `CrewHired`

c1 `ledger.pgld` has only 14 actions: `FundsInitial(25000)`, `ScienceInitial(11.04)`, `ReputationInitial(2.0)`, 2 milestones (FirstLaunch + Kerbin/Landing with rewards = 0), 7 KerbalHire, 2 ScienceSpending (tech nodes). **Zero** FundsEarning, ContractAccept, FundsSpending(PartPurchased).

Once `#403`/`#405` are fixed forward, loading the c1 save will still see `FundsModule.totalEarnings = 0` and `Contract` list as empty, because `OnKspLoad` doesn't re-synthesize missing actions from the store. The funds pool will still be dragged to 0 on the first `PatchFunds` call and the player's accepted contracts will still be missing from Mission Control.

**Fix:** Added `LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad` which walks `GameStateStore.Events` and synthesizes missing `ContractAccept/Complete/Fail/Cancel` and `FundsSpending(PartPurchased)` actions for any event that has no matching ledger action. Called from `ParsekScenario.OnLoad` after the epoch is restored from the save file, and only when not rewinding. Respects `MilestoneStore.CurrentEpoch` (per `career-earnings-bundle-review.md` §5.6) so old-branch events don't leak into the new epoch. Idempotent via `LedgerOrchestrator.LedgerHasMatchingAction`. Automatic recalc runs after recovery so the derived state heals. Tests in `Source/Parsek.Tests/LedgerRecoveryMigrationTests.cs`.

**Follow-up / known limitation:** the ledger recovery migration in §K covers contract events and science subjects only; extend `IsRecoverableEventType` if a future broken save surfaces with stale `CrewHired`, `TechResearched`, or `FacilityUpgraded` actions. Those real-time `OnKscSpending` paths were never broken, so c1/sci1 did not need them synthesized.

**Status:** ~~Fixed~~

---

## ~~400. `OnProgressComplete` hardcodes milestone funds/rep rewards to 0, causing permanent career-mode funds drain via `PatchFunds`~~

**Source:** same c1 playtest as `#405`. Identified from in-code comment at `GameStateRecorder.cs:687-698` and confirmed in c1 `ledger.pgld`.

**Concern:** the handler writes the `MilestoneAchieved` event with no reward data:

```csharp
// OnProgressComplete fires AFTER KSP has already applied the reward via
// Funding.Instance.AddFunds / Reputation.Instance.AddReputation. In theory
// we could compute the delta by comparing pre/post values, but we don't have
// a pre-event snapshot (no prefix hook). The reward amounts come from
// GameVariables.Instance.GetProgressFunds/Rep/Science() which vary by
// body, milestone type, and difficulty settings — too fragile to replicate.
// See deferred items D17/D18 for earning-side capture plans.
```

The c1 ledger confirms the effect: every `MilestoneAchievement` action has `milestoneFundsAwarded = 0` and `milestoneRepAwarded = 0`, so `FundsModule` / `ReputationModule` never sees the reward even though KSP's pool received it in real time. Over a full career, this drains hundreds of thousands of funds silently — every `FirstLaunch`, `FirstOrbit`, `Kerbin/Flyby`, etc. contributes to the gap that `#403`/`#402` then close by dragging funds back down.

**Fix:** Two-phase capture. `OnProgressComplete` in `Source/Parsek/GameStateRecorder.cs` emits the `MilestoneAchieved` event with zero-reward detail and stores the `ProgressNode` reference in `PendingMilestoneEventByNode`. Then the new `Source/Parsek/Patches/ProgressRewardPatch.cs` (Harmony postfix on the protected `ProgressNode.AwardProgress(string, float, float, float, CelestialBody)`) reads the real funds/science/rep values from the method parameters and calls `GameStateRecorder.EnrichPendingMilestoneRewards`, which patches the stored event in place (`GameStateStore.UpdateEventDetail` helper) and updates any matching ledger `MilestoneAchievement` action. The plan's preferred `GameVariables.GetProgressFunds/Rep/Science` does NOT exist in stock KSP (verified via decompile) — the review's `AwardProgress`-postfix fallback was the only stable option. Tests in `Source/Parsek.Tests/MilestoneRewardCaptureTests.cs`.

**Status:** ~~Fixed~~

---

## ~~399. `ScienceModule.ComputeTotalSpendings` walks only one of two `ScienceSpending` actions for a save with two tech unlocks at the same UT~~

**Source:** same c1 playtest as `#405`. **Suspect — needs verification.**

**Concern:** c1 `ledger.pgld` contains two `ScienceSpending` (type 1) actions, both at `ut = 420.54265197752716`:

```
GAME_ACTION { ut = 420.54265197752716; type = 1; seq = 8; nodeId = basicRocketry; cost = 5; }
GAME_ACTION { ut = 420.54265197752716; type = 1; seq = 9; nodeId = engineering101; cost = 5; }
```

But every `ScienceModule.ComputeTotalSpendings` log line in the c1 session shows `spendingCount=1, totalCommittedSpendings=5` — not `2` / `10`. Example line 11710 and 11772. The walk claims to have processed only the first of the two spending actions.

Confirming log:

```
11725 ScienceModule Spending: nodeId=basicRocketry, cost=5, affordable=true, runningScience=6.04
      (no "engineering101" spending log line in the walk)
11762 Coalesced ScienceChanged event at ut=420.54
11763 Game state: ScienceChanged -5.0 (RnDTechResearch) → 1.0
```

Line 11762's `Coalesced ScienceChanged event at ut=420.54` hints that `GameStateStore.AddEvent` coalesces two same-UT `ScienceChanged` events (one per tech node) into one. But the `ScienceSpending` actions in the ledger are distinct — so the coalescing happens at the event-store level, not the action level. That rules out event-merging as the cause. The suspect is `ScienceModule.ProcessSpending` / `ComputeTotalSpendings` having a `seq` or `nodeId` dedup that incorrectly merges same-UT same-cost spendings.

**Fix direction:** read `Source/Parsek/GameActions/ScienceModule.cs` around `ComputeTotalSpendings` and `ProcessSpending`, look for a dedup keyed on `(ut, cost)` or `(ut, nodeId)` that drops one of the two actions. Add a unit test with two `ScienceSpending` actions at the same UT with different `nodeId`s, assert both are walked. Likely a one-line fix, but the cascade matters — if this drops one tech research, the running balance is off by the cost of that tech node, which compounds into the `#403` funds-drain chain.

**Status:** ~~Fixed~~. Verified — the code is correct; no dedup bug exists. The `spendingCount=1` log entries were from intermediate `RecalculateAndPatch` calls (triggered by `CanAffordScienceSpending`) before the second action was added to the ledger. Regression tests added in `ScienceModuleTests.cs` to lock in the correct behavior.

---

## ~~398. `GameStateStore` accumulates `ContractOffered` events forever — c1 session bloated to 78+ within 8 minutes of play~~

**Source:** same c1 playtest as `#405`. Distinct from `#390` (perf/size concern about `outOfRange` accumulation in general) — this is specifically about `ContractOffered` volume caused by the `#404` clear-and-regenerate loop.

**Concern:** c1 session shows `GameStateStore.Events.total` climbing monotonically past 78 within 8 minutes, mostly driven by duplicate `ContractOffered` events. The same 7 underlying contract types (`RT-5 Flea`, `LV-T45 Swivel`, `TD-12 Decoupler`, etc.) appear at 4+ distinct UTs (52.6, 204.0, 395.6, 410.8, 420.5) with **different GUIDs** each time. Each repetition is a fresh `ContractSystem` regeneration after `PatchContracts` wiped the offered pool — so this bug is downstream of `#404`.

Once `#404` is fixed, the clear-regenerate loop stops and this bloat stops accumulating. Until then, every few seconds adds another 5-9 events to the store, every save writes them to disk, every future `ConvertEvents` re-walks them as `outOfRange`.

**Fix:** `OnContractOffered` in `Source/Parsek/GameStateRecorder.cs` no longer calls `GameStateStore.AddEvent`. Offered contracts are transient advertisements generated on every ContractSystem tick; keeping the handler subscribed preserves the diagnostic log only. The converter already dropped `ContractOffered`, and no UI/module reads the events from the store, so the removal is safe. `#404`'s preservation of surviving Active contracts also closes the clear-and-regenerate loop upstream. Tests in `Source/Parsek.Tests/OnContractOfferedStoreTests.cs` (including an IL-scan regression guard against re-adding an `AddEvent` call).

**Status:** ~~Fixed~~

---

## ~~397. CRITICAL: science-career earnings silently dropped — `PatchScience` resets R&D back to seed after every commit~~

**Source:** sci1 science-career playtest `2026-04-14` (logs `logs/2026-04-14_2340_sci1-science-bug/`). Player collected ~16 science across multiple recovered flights; sci1 `ledger.pgld` contains zero `ScienceEarning` actions; in-game R&D pool stayed pinned at the starting seed value of `2.712`.

**Concern:** at recording-commit time two static consumers drain `GameStateRecorder.PendingScienceSubjects` in the wrong order. `RecordingStore.CommitRecordingDirect:348-349` and `RecordingStore.FinalizeTreeCommit` (`RecordingStore.cs:823-824`) both do:

```csharp
GameStateStore.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
GameStateRecorder.PendingScienceSubjects.Clear();
```

Later, `LedgerOrchestrator.NotifyLedgerTreeCommitted → OnRecordingCommitted` (`LedgerOrchestrator.cs:106-108`) reads from the same list that was just cleared:

```csharp
var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
    GameStateRecorder.PendingScienceSubjects, recordingId, endUT);
```

`ConvertScienceSubjects` always returns an empty list, so no `ScienceEarning` actions are ever added to the ledger. `ScienceModule.totalEffectiveEarnings` stays at the seed value forever. On every subsequent `RecalculateAndPatch` cycle, `KspStatePatcher.PatchScience` (`KspStatePatcher.cs:77-95`) computes `delta = targetScience(seed) - currentScience(real)` and calls `AddScience(delta)`, actively resetting R&D.Science back to the seed. Log excerpt (sci1 session):

```
11697 CommitScienceSubjects: 2 added, 0 updated (total=2)
11735 ConvertEvents: converted=1, outOfRange=11, total=12
11736 ConvertScienceSubjects: empty or null subjects list, returning 0 actions
11738 Committed recording '...': 1 actions ... science=0
11739 Seeded initial science: amount=2.71200013, total=10
12527 PatchScience: 3.3 -> 2.7  (delta=-0.6, target=2.7)
12674 PatchScience: 4.8 -> 2.7  (delta=-2.1, target=2.7)
13429 PatchScience: 6.0 -> 2.7  (delta=-3.3, target=2.7)
14482 PatchScience: 10.4 -> 2.7 (delta=-7.7, target=2.7)
```

The ledger loses 7.7 + 3.3 + 2.1 + 0.6 = **13.7 science points** across one play session, just from this save.

**Regression history:** `fd58a05e` (March 7 2026, "Fix infinite science duplication via recording merge/revert") introduced `CommitScienceSubjects + .Clear()` inside `RecordingStore`. At that point `LedgerOrchestrator` did not exist, so the clear was harmless. `b19c9de9` (March 31 2026, "Add LedgerOrchestrator: central coordination for ledger pipeline") added step 2 `ConvertScienceSubjects(PendingScienceSubjects, …)` as part of `OnRecordingCommitted`. Those two commits never reconciled the ordering — `fd58a05e`'s clear now always beats `b19c9de9`'s read. The existing unit tests (`GameStateEventTests.cs:1584+`, `QuickloadDiscardTests.cs:266+`) manually repopulate `PendingScienceSubjects` between `CommitScienceSubjects` and the downstream convert call, so they never exercise the real end-to-end order.

**Scope:** all science-mode and career-mode saves on `main` since `b19c9de9` (2026-03-31). The symptom only appears in career/science mode — sandbox saves are unaffected because `ResearchAndDevelopment.Instance` is null there. Funds and reputation flow through the same `OnRecordingCommitted → ConvertEvents` path but do **not** use a parallel `PendingX + CommitX + Clear` staging list, so they are unaffected.

**Fix:** Removed the premature `PendingScienceSubjects.Clear()` inside `RecordingStore.CommitRecordingDirect:348-349` and `RecordingStore.FinalizeTreeCommit:823-824`. Added `try/finally` clear points at the authoritative upstream sites that read the list — `LedgerOrchestrator.NotifyLedgerTreeCommitted` (after the foreach so every recording in a tree sees the same non-empty list), `ChainSegmentManager.CommitSegmentCore:527` (for chain segments), and `ParsekFlight.FallbackCommitSplitRecorder` (direct commit path). The `try/finally` guarantees the list is cleared even if `OnRecordingCommitted` throws. Discard paths (ClearCommitted, CommitTree duplicate guard, DiscardPendingTree, ResetForTesting, Quickload) keep their clears — they drop orphaned pending subjects. Tests in `Source/Parsek.Tests/PendingScienceSubjectsClearTests.cs`.

**Status:** ~~Fixed~~

---

## ~~396. sci1 save file needs one-shot science recovery after #397 fix~~

**Source:** same playtest as `#397`. The collected sci1 save (`saves/sci1/Parsek/GameState/events.pgse` + `ledger.pgld`) is in a broken state that simply fixing `#397` will not un-brick.

**Concern:** `events.pgse` has 9 SCIENCE_SUBJECTS persisted (mysteryGoo@pad 3.6, temperatureScan 1.2, telemetryReport 0.6, mysteryGoo@runway 1.512, temperatureScan 1.2, telemetryReport 0.6, telemetryReport@KerbinFlyingLow 1.4, temperatureScan@KerbinFlyingLowShores 2.8, mysteryGoo@KerbinFlyingLow 3.528 — total 16.44 science). `ledger.pgld` has:

- 8 `KerbalHire` (type 12)
- 2 `MilestoneAchievement` (type 4): `FirstLaunch`, `Kerbin/Science`
- 1 `ScienceInitial` (type 21): `initialScience=2.71200013`
- **0 `ScienceEarning`**

Even after `#397` is fixed, loading this save runs `OnKspLoad → RecalculateAndPatch → PatchScience`, which still sees `GetAvailableScience() = 2.712` (no earnings in ledger) and drags R&D back to 2.712. The science sitting in `events.pgse` is orphaned — nothing reads it back into the ledger.

**Fix:** Covered by the same `LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad` added for `#401` — the second half of the migration walks `GameStateStore.GetCommittedScienceSubjectIds()` and synthesizes a `ScienceEarning` action for any subject with a positive committed value but no matching ledger action (via `LedgerOrchestrator.LedgerHasMatchingScienceEarning`). Respects epoch isolation and is idempotent. Tests in `Source/Parsek.Tests/LedgerRecoveryMigrationTests.cs`.

**Status:** ~~Fixed~~

---

## ~~395. `ScienceSubjectPatch` Harmony postfix masks the broken ledger at the Science Archive display layer~~

**Source:** same investigation as `#397`. Made the main bug much harder to detect in-game.

**Concern:** `Source/Parsek/Patches/ScienceSubjectPatch.cs:33-48` is a Harmony postfix on `ResearchAndDevelopment.GetExperimentSubject` / `GetSubjectByID` that injects per-subject credited values into `ScienceSubject.science` by reading from `GameStateStore.TryGetCommittedSubjectScience(...)`. That source is `GameStateStore.committedScienceSubjects`, which is populated by `CommitScienceSubjects` **regardless of** whether `LedgerOrchestrator.ConvertScienceSubjects` runs.

In the `#397` bug scenario the store has the subjects (persisted to `events.pgse`) but the ledger has no `ScienceEarning` actions. That means:

- The Science Archive UI can show "correct" credited science per experiment (via this patch), while
- The actual R&D pool total is stuck at the seed value because the module-derived `ScienceModule.totalEffectiveEarnings == seed` drives `PatchScience`, and
- `KspStatePatcher.PatchPerSubjectScience` (`KspStatePatcher.cs:357-408`) writes `totalSubjects=0` in the same log line pair, silently disagreeing with the Harmony patch.

The contradiction makes the bug look like "the number at the top is wrong but my experiments are fine" instead of "the entire pipeline from OnScienceReceived to ledger is broken."

**Fix:** `Source/Parsek/Patches/ScienceSubjectPatch.cs` now resolves committed science via `LedgerOrchestrator.Science.GetSubjectCredited(subjectId)` (the same `ScienceModule` the R&D pool patcher reads from) instead of `GameStateStore.TryGetCommittedSubjectScience`. If the module has zero credited for a subject, the Archive no longer shows a stale store value — a broken ledger can't mask itself with a correct-looking Archive display. Falls back to the store path only when `LedgerOrchestrator` is uninitialized (sandbox / pre-init loads). Decision logic extracted into `ScienceSubjectPatch.TryResolveCommittedScience` for unit testing. Tests in `Source/Parsek.Tests/ScienceSubjectPatchHardeningTests.cs`.

**Status:** ~~Fixed~~

---

## ~~394. `GameStateEventConverter` silently drops `ScienceChanged` events while `GameStateRecorder` keeps capturing them~~

**Source:** same investigation as `#397`.

**Concern:** `GameStateRecorder.OnScienceChanged` (`GameStateRecorder.cs:557-590`) subscribes to `GameEvents.OnScienceChanged` and pushes a `ScienceChanged` `GameStateEvent` into `GameStateStore.Events` for every R&D delta, with reason tags like `VesselRecovery`. The converter then deliberately drops those events: `GameStateEventConverter.cs:121-130` lists `FundsChanged / ScienceChanged / ReputationChanged` in the "no GameAction equivalent" switch case and returns `null`.

Log evidence from the sci1 session (5 distinct `ScienceChanged` events captured and all dropped at convert time):

```
11402 Game state: ScienceChanged +1.2 (VesselRecovery) → 2.7
12541 Game state: ScienceChanged +2.1 (VesselRecovery) → 4.8
13026 Game state: ScienceChanged +1.2 (VesselRecovery) → 5.4
14063 Game state: ScienceChanged +1.4 (VesselRecovery) → 4.1     ← note the regression: from 5.4 to 4.1
14080 Game state: ScienceChanged +2.8 (VesselRecovery) → 6.9
```

The `5.4 → 4.1` regression on line `14063` is a direct visible symptom of `#397` — `PatchScience` dragged R&D down between two recovery events.

**Fix:** Added `LedgerOrchestrator.ReconcileEarningsWindow`, called from the tail of `OnRecordingCommitted`, which sums dropped `FundsChanged/ReputationChanged/ScienceChanged` event deltas against the effective emitted earning/spending actions in the same UT window and logs WARN when they disagree beyond tolerance. This is the log-level cross-check the review (§5.1) called out: it would have caught `#397`, `#400`, and `#405` on day one without needing to re-emit the Changed events (re-emitting would double-count against recovery + contract + milestone channels). The drop block in `GameStateEventConverter.cs:121-130` stays — now with an expanded comment explaining WHY so a future engineer can't "fix" it into double-counting. Tests in `Source/Parsek.Tests/EarningsReconciliationTests.cs`.

**Status:** ~~Fixed~~

---

## ~~393. `KspStatePatcher.PatchScience` log message says "sandbox/science mode" when only sandbox is affected~~

**Source:** same investigation as `#397`.

**Concern:** `Source/Parsek/GameActions/KspStatePatcher.cs:63-67`:

```csharp
if (ResearchAndDevelopment.Instance == null)
{
    ParsekLog.Verbose(Tag,
        "PatchScience: ResearchAndDevelopment.Instance is null (sandbox/science mode) — skipping");
    return;
}
```

`ResearchAndDevelopment.Instance` is non-null in **both** science and career modes — only sandbox mode has it null. The log message conflates sandbox and science, which is exactly what made this line a red herring during the `#397` investigation (it implied Parsek didn't support science mode, when in fact it's broken *within* science mode). Same confusion is **not** present in `PatchFunds` (`line 115-119`) and `PatchReputation` (`line 163-167`), which correctly say "sandbox mode" only. (Those cases are actually sandbox-only: `Funding.Instance` and `Reputation.Instance` are both null in science mode, which is correct.)

**Fix:** change the `PatchScience` message to `"ResearchAndDevelopment.Instance is null (sandbox mode) — skipping"`. One-line edit.

**Status:** ~~Fixed~~. Changed `"sandbox/science mode"` to `"sandbox mode"` in `KspStatePatcher.PatchScience` log message.

---

## ~~392. `ScienceModule.HasSeed` gate may silently skip patching on the first recalculate after a fresh load~~

**Source:** same investigation as `#397`. **Unverified — file as a diagnostic question, not a confirmed bug.**

**Concern:** `KspStatePatcher.PatchScience` lines 70-75 early-returns when `!science.HasSeed`:

```csharp
if (!science.HasSeed)
{
    ParsekLog.Verbose(Tag,
        "PatchScience: module has no ScienceInitial seed — skipping to preserve KSP values");
    return;
}
```

`ScienceInitial` is created lazily by `LedgerOrchestrator.RecalculateAndPatch` at line 451-456 the first time R&D.Science is non-zero:

```csharp
if (!scienceSeedDone && ResearchAndDevelopment.Instance != null
    && ResearchAndDevelopment.Instance.Science != 0f)
{
    Ledger.SeedInitialScience(ResearchAndDevelopment.Instance.Science);
    scienceSeedDone = true;
}
```

In the sci1 log the `HasSeed`-skip message fires 12 times between save load (`23:21:11`) and the first recording commit (`23:30:18`). Those 12 skips are all on `RecalculateAndPatch` calls (scene changes, quicksave hooks, etc.) before any `ScienceInitial` action exists. After the first commit seeds the ledger, the gate flips and patching starts working — and starts breaking science under `#397`.

The question: is there a window where (a) R&D.Science has already been mutated by gameplay but (b) the seed is still 0 / unset, and `PatchScience` should *capture* that mutation into a new action rather than silently skip? Specifically: a fresh save where the player collects science *before* the first recording commit — does the seed bake in those earnings, or are they lost too? Unverified but worth tracing.

**Fix direction:** verify the intended contract. If "seed captures baseline-at-first-recalc, earnings must arrive via `OnScienceReceived`" is correct and intentional, add a one-line comment explaining *why* the `HasSeed` skip is safe so the next person doesn't mistake it for the root cause. If there's a real gap, close it.

**Status:** ~~Fixed~~. Verified benign — the `DeferredSeedAndRecalculate` coroutine correctly handles the timing gap. The 12 HasSeed-skip log entries during early load are expected. Added clarifying comments to `PatchScience`/`PatchFunds`/`PatchReputation` in `KspStatePatcher.cs`.

---

## ~~391. Max-wins inside `CommitScienceSubjects` silently drops subjects whose value hasn't increased since last commit~~

**Source:** same investigation as `#397`. **Unverified — file as a suspect.**

**Concern:** `GameStateStore.cs:190-218` `CommitScienceSubjects` only promotes a subject's value when it strictly exceeds the previously stored max (`science > existing`). Equal-value re-earnings are silently dropped — no action added, store unchanged. That is the right shape for the original rewind-dedupe problem (re-earning the same experiment shouldn't award twice), **but** it also means: after `#397` is fixed, if a player rewinds, replays the exact same experiment to the exact same credited value, the converter gets nothing to convert. The ledger will not contain a `ScienceEarning` for the replayed recording even though the experiment is rightfully theirs — the max-wins already wrote the score on the first run, and the ledger was supposed to get the action then.

In the `#397`-broken state, where the store has the subjects but the ledger has no earnings, attempting to "recover" on load (per `#396`) needs to *not* rely on the max-wins path — it needs to create the action even when `new == existing`. Wire the recovery path carefully.

**Fix direction:** after `#397` is fixed, add a test for the rewind-same-value case, and confirm the intended behavior: the action *should* exist in the new recording's slice of the ledger, because otherwise the old recording's action disappears when the old recording is discarded. This may require splitting "store update" from "action emit" at the call-site level.

**Status:** ~~Fixed~~. The `>` guard itself is harmless (equal-value re-commits don't change the dictionary). The real bug was that `committedScienceSubjects` became stale after recording deletion — subjects from deleted recordings persisted and `TryRecoverBrokenLedgerOnLoad` could synthesize ghost actions for them. Fix: `RebuildCommittedScienceFromWalk()` at the end of `RecalculateAndPatch()` and `RebuildCommittedScienceFromLedger()` before `TryRecoverBrokenLedgerOnLoad` iterates subjects. Tests in `CommittedScienceDictTests.cs`.

---

## ~~390. `GameStateStore.Events` grows unboundedly with `outOfRange` events across recordings~~

**Source:** same playtest as `#397`. Low-severity perf/size concern — logged for completeness.

**Concern:** `GameStateEventConverter.ConvertEvents` only converts events whose UT falls inside `startUT..endUT` for the current recording. Everything outside that window is counted as `outOfRange` and left in `GameStateStore.Events`. The store grows over time because nothing prunes events after they've been processed by at least one commit. Log from the sci1 session shows the monotonic climb:

```
11735 ConvertEvents: converted=1, skipped=0, outOfRange=11, total=12
13396 ConvertEvents: converted=0, skipped=0, outOfRange=16, total=16
14449 ConvertEvents: converted=0, skipped=3, outOfRange=16, total=19
```

Every commit re-walks the full 16-19 event list to decide what's in range. In a long career (hundreds of recordings) the store will grow to thousands of events and the walk becomes O(n) per commit. Ledger dedupe (`LedgerOrchestrator.DeduplicateAgainstLedger`) still keeps ledger state correct, so this is correctness-safe — just growth-unbounded.

**Fix direction:** after a commit successfully converts events falling in its window, those specific events can be tombstoned (or moved to a separate "already-committed" list) so future commits walk a shorter candidate list. Separately, consider pruning events older than the oldest committed recording's startUT. Both require a migration for existing saves, so plan carefully.

**Status:** ~~Fixed~~. `GameStateStore.PruneProcessedEvents()` removes old-epoch events and events at or below the latest committed milestone's EndUT. Called after each commit in `NotifyLedgerTreeCommitted`, `CommitSegmentCore`, and `FallbackCommitSplitRecorder`. Tests in `EventPruningTests.cs`.

---

## ~~389. Time-range filter for Timeline window and Recordings table (scroll-limit mitigation for long careers)~~

**Source:** maintenance request `2026-04-15`. As a career progresses, both the Timeline window and the Recordings table accumulate entries linearly. A mature career (100+ missions) becomes painful to navigate — scrolling dozens of screenfuls to find a specific flight is not sustainable. The user wants a time-interval filter ("show entries from UT A to UT B") that limits both windows to a user-chosen slice.

**Concern:** Neither window has a time-range filter today. Current filter surfaces:

- `Source/Parsek/UI/TimelineWindowUI.cs` has `DrawFilterBar` at line `281`-`331` with only four toggles: Overview/Detail tier selector + Recordings/Actions/Events source toggles. `IsEntryVisible` at line `405`-`415` gates rows on `Tier` + `Source`. No UT check.
- `Source/Parsek/UI/RecordingsTableUI.cs` supports sort via `sortColumn` (line `85`) but has no filter bar at all — every committed recording draws every frame, gated only by chain/group expand state.

For a 200-mission career the Timeline window could have 1000+ entries and the Recordings table 200+ top-level rows plus debris children. Scroll performance and human navigation both degrade past a few hundred rows.

**Desired behavior:**

1. Add a time-range filter that both windows share (same state, so filtering in one window applies to the other — or at least they read the same underlying settings so "Year 2, Day 40 to Day 60" behaves consistently).
2. Default: no range filter (current behavior). Empty state = show everything.
3. When a range is set:
   - Timeline window: `IsEntryVisible` additionally checks `entry.UT` (or equivalent) is inside `[rangeStartUT, rangeEndUT]`.
   - Recordings table: skip any recording whose `[StartUT, EndUT]` window has zero overlap with `[rangeStartUT, rangeEndUT]`. Default to "overlap any" rather than "fully contained" so a mission spanning the boundary still shows.
   - Chain segments / tree children: keep visible if any descendant's window overlaps the range — otherwise a range that cuts a chain mid-flight would confusingly hide part of it.
   - Debris children: same rule as parents. If the parent recording is in range, its debris stays visible in the expand view.
4. Clear filter: single button / Clear-X in the filter UI that resets to unlimited.
5. Visual indicator: when a range is active, the window title bar (or a small label near the filter) shows the active range and total matched count, e.g. `"Year 2, Day 40 - Day 60 (14 matches)"`.

**UI surface — the actual "idk exactly how" question:**

Three options, ranked by recommended order:

1. **Two-slider range control with quick presets.** In the Timeline filter bar, add a second row (or an expand/collapse "Time range" disclosure): min-UT slider + max-UT slider over `[earliest committed UT, current UT + some future headroom]`, plus preset buttons: "Last day", "Last 7 days", "Last 30 days", "This year", "All time (clear)". The sliders are IMGUI `GUILayout.HorizontalSlider` — simple, matches existing visual language. Preset buttons cover 90% of real use. Full manual slider for the 10% edge case.
2. **Two text fields with KSP-date parsing.** Two `GUILayout.TextField`s accepting KSP date strings (`Y2 D40`, or UT seconds, or whatever `KSPUtil.PrintDateCompact` round-trips to). Parse + validate on commit. Harder to use (you have to know the format) but more precise for power users. Consider as a v2 addition if users ask for exact dates.
3. **Click-on-timeline drag-to-select.** Let the user draw a range directly in the timeline window by click-and-dragging across visible rows. Most intuitive but the most work to implement — the timeline window today is a plain vertical `GUILayout` of rows, not a horizontal time axis. Out of scope for v1.

Start with **option 1** (sliders + presets). Simplest path to a working filter, matches the existing filter-bar visual language.

**Shared state:**

- New singleton-ish struct `TimeRangeFilter` with `double? MinUT` / `double? MaxUT` fields (null = unbounded), exposed from `ParsekUI` or a new small class `TimeRangeFilterState`. Both windows read from it. Both update `filterDirty` flags (existing `TimelineWindowUI.filterDirty` pattern — `RecordingsTableUI` would need an equivalent if it doesn't have one).
- Optional: persist across sessions in `ParsekSettings` so the user's range survives save/load. Low stakes either way — the user may *want* it reset on load. Default: don't persist.

**Pure helper methods (testable without Unity):**

- `static bool IsEntryInRange(double entryUT, double? minUT, double? maxUT)` — core predicate.
- `static bool IsRecordingInRange(double startUT, double endUT, double? minUT, double? maxUT)` — overlap check.
- `static bool IsGroupInRange(HashSet<int> descendants, IReadOnlyList<Recording> committed, double? minUT, double? maxUT)` — "any descendant overlaps" check.
- Put in a new `UI/TimeRangeFilterLogic.cs` or extend an existing logic file. Unit tests cover empty range, one-sided (min only / max only), no-overlap, partial overlap, fully-contained.

**Wiring:**

- `TimelineWindowUI.IsEntryVisible` (line `405`) — add `if (!TimeRangeFilterLogic.IsEntryInRange(entry.UT, state.MinUT, state.MaxUT)) return false;`.
- `TimelineWindowUI.DrawFilterBar` (line `281`) — add second row with sliders + clear button.
- `RecordingsTableUI` (line `85`+) — add filter dirty flag + skip recordings/groups where the range predicate fails. Add the same filter UI (or a compact version) at the top of the table.
- Existing group expand/collapse state (`expandedGroups` HashSet in `ParsekUI`) is independent of filtering — don't force-expand filtered groups, just hide rows.

**Performance:**

- Both windows already rebuild filter state from scratch when `filterDirty` is set (Timeline: line `243`-`265`). Time-range filtering adds one float comparison per entry — O(n) same as existing path. No perf concern for realistic career sizes.
- For truly huge careers (thousands of entries) the bottleneck is row drawing, not filtering. Filtering *helps* here because hidden rows don't draw. This TODO is the right direction.

**Interaction with #385 (Kerbals Status window):** the Kerbals window will have its own summary of crew-end-state events per recording. Those should also respect the range filter so a "show me what happened in the last 30 days" slice of the Kerbals window makes sense. Out of scope for v1 — note as a followup.

**Open questions:**

- Should the range be UT-based (raw seconds) or calendar-based (Year/Day)? UT under the hood, UI displays via `KSPUtil.PrintDateCompact`. Sliders operate on UT, labels show formatted dates.
- Max-UT slider end: current UT, or current UT + some future window (for planned missions)? Use `max(currentUT, latest committed EndUT) + 10% headroom`. Future-dated entries are rare but exist.
- What happens if the user scrolls to an entry that's later filtered out? The scroll position should snap back to the top on filter change. Log the snap.

**Files to touch:**

- `Source/Parsek/UI/TimeRangeFilterLogic.cs` (new, pure static helpers)
- `Source/Parsek/UI/TimelineWindowUI.cs` — `DrawFilterBar` + `IsEntryVisible` + shared-state plumbing
- `Source/Parsek/UI/RecordingsTableUI.cs` — new filter bar + skip logic in the draw loop
- `Source/Parsek/ParsekUI.cs` — owns the shared `TimeRangeFilterState` singleton (or wherever the other cross-window state lives)
- `Source/Parsek.Tests/` — unit tests for `TimeRangeFilterLogic` predicates
- `CHANGELOG.md` under Unreleased: "Timeline and Recordings windows now support a time-range filter (UT interval) with quick presets (Last day / Last 7 days / Last 30 days / This year / Clear). Useful for navigating long careers with many missions."

**Status:** ~~Fixed~~. Implemented as a shared `TimeRangeFilterState` on `ParsekUI`, read by both `TimelineWindowUI` (filter bar with presets + collapsible dual-slider custom range) and `RecordingsTableUI` (filter gate on root items + compact indicator bar with Clear button). Presets: Last Day, Last 7d, Last 30d, This Year, All. Custom range via two `HorizontalSlider` controls with KSP-formatted labels (seconds omitted to mask float quantization). Chains shown as a unit if any segment overlaps the range. Groups hidden when no descendant overlaps. Pure predicates in `TimeRangeFilterLogic` with 22 unit tests. Not persisted across sessions.

**Fix:** New `Source/Parsek/UI/TimeRangeFilter.cs` (state + predicates), wired into `TimelineWindowUI.DrawTimeRangeFilterBar` / `IsEntryVisible` and `RecordingsTableUI.DrawRecordingsWindow` / `DrawTimeRangeFilterIndicator`. `Source/Parsek.Tests/TimeRangeFilterTests.cs` covers all predicate edge cases including in-progress recordings, boundary touches, null bounds, and slider bound computation.

---

## 388. Tracking station: add a Ghost visibility toggle alongside the stock type filters

**Source:** maintenance request `2026-04-14`. Stock KSP's tracking station has a row of show/hide toggles (asteroids, debris, probes, rovers, landers, ships, stations, bases, EVAs, planes, relays, flags). Parsek adds a *new* category of entries — "ghosts" — but there's no user control to hide them in bulk. Users need a single toggle that collapses all Parsek ghosts out of both the vessel list and the map view simultaneously.

**Concern:** Parsek ghost presence in the tracking station comes from two independent render paths:

1. **Proto-vessel ghosts.** `GhostMapPresence.CreateGhostVesselsFromCommittedRecordings` creates lightweight `ProtoVessel` entries that show up in the stock tracking station vessel list and have real orbit renderers. Behavior is patched in `Source/Parsek/Patches/GhostTrackingStationPatch.cs` (Fly/Delete/Recover/SetVessel all intercepted). The stock type filters DO hide these when the corresponding `VesselType` is toggled off — but that's the wrong UX: hiding "Ships" shouldn't hide ghost ships, and hiding ghosts shouldn't hide real ships.
2. **Atmospheric ghost markers.** `Source/Parsek/ParsekTrackingStation.cs` line `65`-`109` `OnGUI` iterates `RecordingStore.CommittedRecordings` and calls `MapMarkerRenderer.DrawMarker` directly for recordings in atmospheric phases (no proto-vessel exists for these). These entries bypass the stock vessel list entirely and do NOT respond to the stock type filters — they're always visible.

Both paths need the same toggle. Currently neither does.

**Desired behavior:**

- A single bool `ParsekSettings.Current.showGhostsInTrackingStation` (default `true`). Persisted across sessions.
- When `false`:
  - `ParsekTrackingStation.OnGUI` skips all atmospheric ghost marker draws (early-return inside the `for` loop over `committed`).
  - Proto-vessel ghosts are hidden from the stock vessel list. Options for implementing this:
    1. **Suppress creation:** `GhostMapPresence.CreateGhostVesselsFromCommittedRecordings` returns `0` when the flag is off. Simplest, but entering TS with ghosts disabled, then enabling the toggle, needs a live recreate pass. `Update` already has a count-based force-tick (`ParsekTrackingStation.cs:49`-`57`) — extend it to force a tick when the toggle flips.
    2. **Hide after creation:** keep creating them, then iterate `GhostMapPresence.ghostMapVesselPids` and set their `orbitRenderer` visibility off + remove from the vessel list on filter change. More flicker-prone.
  - Prefer option 1. The force-tick-on-toggle pattern already exists for commit detection, so reusing it is cheap.
- When `true` again, ghosts reappear on the next lifecycle tick (or immediately via force-tick on toggle flip).

**UI surface:**

Two reasonable placements — pick one:

1. **Parsek settings window.** `Source/Parsek/UI/SettingsWindowUI.cs` already has a Ghost section (see memory index). Add a `Show ghosts in Tracking Station` checkbox there. Minimal new UI, consistent with existing toggle patterns. Downside: user has to open the main Parsek window to change TS visibility, which is annoying if they're in TS and want to quickly hide ghosts.
2. **In-scene TS button.** Draw a small `GUI.Toggle` inside `ParsekTrackingStation.OnGUI` — bottom-right corner or attached to the existing stock filter bar position. Harder to position cleanly (stock filter bar is Unity UI, not IMGUI, so we can't piggy-back directly — would need to locate the stock bar's RectTransform and place our toggle adjacent via `GameObject.Find` + offset, fragile).

Start with **option 1** (SettingsWindowUI checkbox). It's enough for the feature request and avoids stock UI layout coupling. If users ask for an in-scene quick toggle later, add it as a followup.

**Settings plumbing:**

- `Source/Parsek/ParsekSettings.cs` — add `public bool showGhostsInTrackingStation = true;`. Ensure it's serialized in `ParsekScenario.OnSave`/`OnLoad` alongside the other settings fields.
- `Source/Parsek/UI/SettingsWindowUI.cs` — add the checkbox with label "Show ghosts in Tracking Station". On change, log `ParsekLog.Info("UI", $"showGhostsInTrackingStation -> {value}")`.
- `Source/Parsek/ParsekTrackingStation.cs` — inside `OnGUI` line `80`-`108`, add `if (!ParsekSettings.Current.showGhostsInTrackingStation) return;` before the loop. Inside `Update` line `41`-`63`, track `lastKnownShowGhosts` alongside `lastKnownCommittedCount` and force a lifecycle tick when the flag flips.
- `Source/Parsek/GhostMapPresence.cs` — `CreateGhostVesselsFromCommittedRecordings` reads `ParsekSettings.Current.showGhostsInTrackingStation` and short-circuits creation when `false`. `UpdateTrackingStationGhostLifecycle` calls `RemoveAllGhostVessels("type-filter-off")` when the flag flips to `false` with existing ghosts in play.

**Interaction with the custom atmospheric marker click-toggle (bug #386):** those sticky-label states belong to individual markers. When the flag turns off and all markers disappear, the sticky set should NOT be cleared (user flipping the toggle back on expects the same sticky marks to return). Sticky set lives in `MapMarkerRenderer` state keyed by `RecordingId`, not by `VesselType` or visibility, so this "just works" as long as the clearing paths don't over-reach. Worth a comment in the fix.

**Proto-vessel ghost interaction with stock type filters:** independent of this TODO — the stock filters still apply to ghost ProtoVessels (a ghost Ship will be hidden when "Ships" is filtered off, same as real ships). That's arguably a bug in itself — ghost entries should probably be categorized as "Ghost" globally, not inherit the vessel's type for filter purposes. Out of scope for this TODO, but note it as a follow-up candidate if users complain.

**Tests:**

- Pure unit test: a helper method `ParsekTrackingStation.ShouldDrawAnyAtmosphericMarkers(ParsekSettings)` or similar that returns the early-return decision, so the toggle semantics are testable without Unity.
- Runtime test: open TS with the flag off, assert no atmospheric markers drawn and ghost ProtoVessel count is zero. Flip flag on, force tick, assert markers + ghosts reappear.
- `ParsekSettings` save/load round-trip test — make sure the new field survives.

**Files to touch:**

- `Source/Parsek/ParsekSettings.cs` (add field)
- `Source/Parsek/ParsekScenario.cs` (if settings serialization is done there and not auto-reflected)
- `Source/Parsek/UI/SettingsWindowUI.cs` (checkbox)
- `Source/Parsek/ParsekTrackingStation.cs` (OnGUI early-return + Update force-tick on flag flip)
- `Source/Parsek/GhostMapPresence.cs` (short-circuit creation, remove all when flag drops)
- `Source/Parsek.Tests/` (toggle semantics test + settings round-trip test)
- `Source/Parsek/InGameTests/RuntimeTests.cs` (TS lifecycle test)
- `CHANGELOG.md` under Unreleased: "New Tracking Station filter toggle to show/hide Parsek ghosts (Settings > Ghost section)."

**Status:** TODO. Size: S-M. Pure plumbing, no novel systems. Main design choice is option-1 vs option-2 for the UI surface — recommended option 1 above.

---

## 387. Ghost map icons don't match stock ProtoVessel icons for the same VesselType

**Source:** maintenance request `2026-04-14`. Users report that Parsek's custom ghost icon in map view / tracking station sometimes looks different from the stock icon for the same vessel type. Root cause confirmed by decompiling `KSP.UI.Screens.Mapview.MapNode` — Parsek's atlas-indexing logic is wrong.

**Concern:** `Source/Parsek/MapMarkerRenderer.cs` line `153`-`193` builds `vesselIconUVs` by reading `MapNode.iconSprites` (an obtained-via-reflection `Sprite[]`) and assigning entries by sequential array index:

```csharp
var vtypes = new[] {
    VesselType.Ship, VesselType.Plane, VesselType.Probe,
    VesselType.Relay, VesselType.Rover, VesselType.Lander,
    VesselType.Station, VesselType.Base, VesselType.EVA,
    VesselType.Flag, VesselType.Debris, VesselType.SpaceObject,
    VesselType.DeployedScienceController, VesselType.DeployedSciencePart
};
for (int i = 0; i < vtypes.Length && i < sprites.Length; i++)
{
    Sprite s = sprites[i];
    if (s == null) continue;
    Rect r = s.textureRect;
    vesselIconUVs[vtypes[i]] = ...
}
```

This assumes `sprites[0]` is the Ship sprite, `sprites[1]` is the Plane sprite, etc. **That assumption is wrong.** Decompiling `KSP.UI.Screens.Mapview.MapNode` (lines `2309`-`2321` of the decompiled source) shows the actual lookup is a custom switch:

```csharp
// From stock KSP MapNode icon index resolution:
VesselType.Debris => 7
VesselType.SpaceObject => 21
VesselType.Probe => 18
VesselType.Rover => 19
VesselType.Lander => 14
VesselType.Ship => 20
VesselType.Station => 0
VesselType.Base => 5
VesselType.EVA => 13
VesselType.Flag => 11
VesselType.Plane => 23
VesselType.Relay => 24
VesselType.DeployedScienceController => 28
VesselType.DeployedGroundPart => 29
```

So `iconSprites[0]` is the Station sprite, `iconSprites[20]` is the Ship sprite, `iconSprites[7]` is the Debris sprite, etc. Parsek's current mapping ends up assigning:

- Ship → `sprites[0]` → actually the Station sprite
- Plane → `sprites[1]` → some unrelated sprite (no entry in the table → unused slot)
- Probe → `sprites[2]` → unused slot
- Relay → `sprites[3]` → unused slot
- ...

This produces the exact symptom users are reporting: ghosts of a given type show an icon that matches a *different* stock vessel type.

**Fix:** replace the sequential-index loop with the exact same mapping stock uses. Hard-code it from the decompiled table rather than trying to derive it:

```csharp
private static readonly Dictionary<VesselType, int> StockIconIndexByVesselType =
    new Dictionary<VesselType, int>
    {
        { VesselType.Station, 0 },
        { VesselType.Base, 5 },
        { VesselType.Debris, 7 },
        { VesselType.Flag, 11 },
        { VesselType.EVA, 13 },
        { VesselType.Lander, 14 },
        { VesselType.Probe, 18 },
        { VesselType.Rover, 19 },
        { VesselType.Ship, 20 },
        { VesselType.SpaceObject, 21 },
        { VesselType.Plane, 23 },
        { VesselType.Relay, 24 },
        { VesselType.DeployedScienceController, 28 },
        { VesselType.DeployedGroundPart, 29 },
    };

foreach (var kv in StockIconIndexByVesselType)
{
    int idx = kv.Value;
    if (idx < 0 || idx >= sprites.Length || sprites[idx] == null) continue;
    Rect r = sprites[idx].textureRect;
    vesselIconUVs[kv.Key] = new Rect(
        r.x / spriteAtlas.width, r.y / spriteAtlas.height,
        r.width / spriteAtlas.width, r.height / spriteAtlas.height);
}
```

Two things to verify before shipping:

1. **Is `sprites[idx].texture` the same atlas for every entry?** Today's code picks `spriteAtlas` from the first non-null sprite (line `165`-`173`), then normalizes every `textureRect` by that one atlas's width/height. If sprites at different indices live in different atlas textures, the UV computation for the "other" atlas sprites is wrong. Parsek's current code has the same latent issue — it just didn't surface because most stock sprites live in the same atlas. Before adding the indices above, loop through and confirm every index's `sprites[idx].texture` matches `spriteAtlas`, log a warning and skip any that don't.
2. **`DeployedSciencePart` absence.** The stock table has `DeployedScienceController` and `DeployedGroundPart` but no `DeployedSciencePart`. Parsek's current array does include it. Decide whether to leave `DeployedSciencePart` without a custom icon (falls back to `fallbackDiamond`, line `70`-`76`) or to reuse the `DeployedScienceController` sprite. Probably the former — stock doesn't give it one either.

**Test:** add an in-game runtime test (`Source/Parsek/InGameTests/RuntimeTests.cs`, category "MapView" or similar) that:

1. Gets a `MapView.UINodePrefab` and extracts its `iconSprites` via the same reflection path.
2. Spawns ghost markers for several vessel types via a controlled fixture (or reuses existing ghost test infrastructure).
3. For each spawned type, asserts that `MapMarkerRenderer.vesselIconUVs[vtype]` matches `sprites[StockIconIndexByVesselType[vtype]].textureRect` (normalized).
4. Logs the per-type icon assignment so future regressions are obvious in the test output.

A unit test is insufficient here — this needs the live `MapView` reflection target to be authoritative.

**Files to touch:**

- `Source/Parsek/MapMarkerRenderer.cs` — replace the `vtypes` array + sequential loop at lines `154`-`193` with the dictionary-driven lookup above.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — add the icon-consistency runtime test.
- `CHANGELOG.md` under Unreleased: "Ghost map icons now match the stock ProtoVessel icon for each vessel type (Ship, Plane, Probe, ...). Previous versions used an incorrect sequential atlas indexing that caused ships to appear as stations, etc."
- Consider updating `docs/ksp_mapnode_icon_indices.md` (new file) with the decompiled mapping for future reference, similar to `ksp_flightcamera_units.md` / `ksp_aerofx_physics.md` references already tracked in memory. Cross-link from the fix commit.

**Status:** TODO. Size: S. Straightforward replacement, main risk is the per-index atlas-texture consistency check (point 1 above). High user-visible impact — every Parsek user sees wrong icons today.

---

## 386. Map view / tracking station ghost icon: hide label by default, show on hover, sticky-toggle on click

**Source:** maintenance request `2026-04-14`. Parsek's custom ghost map icon currently always draws a `"Ghost: <name>"` text label directly below it. Stock KSP vessel icons don't — they only show the name on hover (and clicking enters/pins the target). The ghost icon should match that behavior so the map view isn't cluttered with permanent text for every ghost.

**Concern:** `Source/Parsek/MapMarkerRenderer.cs` line `80` draws the label unconditionally every frame:

```csharp
labelStyle.normal.textColor = color;
GUI.Label(new Rect(x - 75, y + iconSize / 2 + 2, 150, 20), "Ghost: " + label, labelStyle);
```

There is no hover test, no click handling, and no per-marker "sticky" state. Both call sites hit this same path: `ParsekUI.DrawMapMarkers` (line `626`) in the flight scene map view, and `ParsekTrackingStation.OnGUI` (line `102`) in the tracking station.

**Desired behavior:**

1. **Default:** icon visible, label hidden.
2. **Hover:** when the mouse cursor is over the icon rect (or a small padded version of it), draw the label for that one frame. Matches stock vessel-icon hover behavior.
3. **Click (toggle):** click the icon once — the label becomes sticky (drawn every frame regardless of hover). Click again — sticky clears. Clicking a different ghost toggles its own sticky independently. Multiple ghosts can be sticky simultaneously.
4. **Behavior parity:** left-click is the toggle. Don't interfere with KSP's own map view drag / camera controls — consume the click only when the cursor is actually over a ghost icon rect. Use `Event.current.Use()` inside the hit test, same pattern stock KSP uses for its own map nodes.

**Implementation sketch:**

- `MapMarkerRenderer.DrawMarker` takes a `label` string today but has no identity for the marker. Add a `string markerKey` parameter (use `rec.RecordingId` at the call sites — stable across index reuse, see the discussion in bug #279). The renderer uses this as the key for sticky-state.
- Add `MapMarkerRenderer` fields: `private static readonly HashSet<string> stickyMarkers = new HashSet<string>();` and `private static string hoveredMarkerThisFrame;`.
- In `DrawMarker`:
  1. Compute `Rect iconRect = new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize);` (already computed inline, just hoist it).
  2. `bool hover = iconRect.Contains(Event.current.mousePosition);`
  3. `bool sticky = stickyMarkers.Contains(markerKey);`
  4. Draw the icon (existing code path).
  5. If `hover || sticky` → draw the label (existing `GUI.Label` call).
  6. If `hover && Event.current.type == EventType.MouseDown && Event.current.button == 0`:
     - Toggle `markerKey` in `stickyMarkers`.
     - `Event.current.Use()` so map view doesn't drag.
     - `ParsekLog.Info("MapMarker", $"Ghost icon '{label}' sticky={(sticky ? "off" : "on")} key={markerKey}")`.
- Clear `stickyMarkers` on scene change (new method `MapMarkerRenderer.ResetForSceneChange()`) and call it from `ParsekScenario.OnGameSceneLoadRequested` or the existing scene-teardown hook. Optional: keep stickies across tracking-station ↔ flight-map transitions so a sticky set in tracking station persists when entering map view of the same scene. Start by clearing on every scene change — simpler, can relax if users ask.
- Handle `Event.current == null` gracefully (happens during non-repaint phases). Guard the hover test behind `Event.current != null`.

**Edge cases:**

- Multiple icons overlapping at the same screen position (two ghosts at the same orbit phase). Iterate in a stable order and let the first hit win. Document that stacked ghosts may need the player to zoom in. Not a regression — stock has the same issue with close vessels.
- Hit rect size: 20px icon is small. Pad the hit rect to `iconSize + 6` (30px) on each side so the user doesn't have to pixel-hunt. Label rect is NOT a hit target — only the icon.
- Icon drawn at `screenPos.z < 0` (behind camera) is early-returned at line `54` — sticky stays set but won't render. That's fine.
- The flight scene has its own click handlers inside `ParsekUI` for the main window — make sure the map marker click handler runs *only* when `MapView.MapIsEnabled` (or the tracking station is active). Otherwise a click on the main-window area that happens to overlap a projected marker rect could double-fire. Quick guard: check `MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION` at the top of the click branch.

**Files to touch:**

- `Source/Parsek/MapMarkerRenderer.cs` — add `markerKey` param, sticky set, hover/click handling. The only renderer.
- `Source/Parsek/ParsekUI.cs` — `DrawMapMarkers` (line `626`) passes `rec.RecordingId` to the new `DrawMarker` signature.
- `Source/Parsek/ParsekTrackingStation.cs` — line `102` passes `rec.RecordingId` too.
- `Source/Parsek/ParsekScenario.cs` — call `MapMarkerRenderer.ResetForSceneChange()` from the existing scene-teardown path.
- Unit test: pure-static helper method `MapMarkerRenderer.ShouldDrawLabel(bool hover, bool sticky) => hover || sticky` (or similar) so the decision is testable without Unity. Toggle logic can also be extracted into a pure method `ToggleSticky(string key, HashSet<string> set)` for the same reason.
- `CHANGELOG.md` under Unreleased: "Map view / tracking station ghost icons now match stock KSP: name label hidden by default, shows on hover, click to pin."

**Open questions:**

- Should the label style differ between hover (fading) and sticky (solid)? v1: identical. If users want visual distinction, bold the sticky one in a followup.
- Does right-click do something else (center camera on ghost)? Stock uses left-click for target / right-click for center-focus on map nodes — for parity, consider adding right-click → `MapView.MapCamera.SetTarget(ghost)` if the ghost has a `ghostMapVesselPid` registered in `GhostMapPresence`. Defer to a followup unless trivial.
- Map-node dedupe: when `GhostMapPresence` has already created a lightweight `ProtoVessel` for a ghost (for orbit line / tracking station integration), stock KSP draws a real `MapNode` for it, and `MapMarkerRenderer` draws a *second* custom icon on top. Confirm the custom icon is only drawn when there's no stock `MapNode` — grep for the condition in `ParsekUI.DrawMapMarkers`. If both draw, the label-hiding fix is moot in the dedupe case (stock label already shows). Worth verifying before starting work.

**Status:** TODO. Size: S. Mostly `MapMarkerRenderer.cs` + call-site threading of `RecordingId` + one scene-reset hook. Main risk is clicking-on-marker vs. map-view drag interaction — test both scenes interactively before merging.

---

## 385. Timeline window: move Retired Stand-ins out, add a dedicated Kerbals Status window behind a toolbar button

**Source:** maintenance request `2026-04-14`. The Retired Stand-ins list currently hangs off the bottom of the Timeline window (`UI/TimelineWindowUI.cs:523` `DrawRetiredKerbalsSection`, called from the main draw at line `238`). It's confusing in that location — "retired stand-ins" is not a timeline concept, and it takes vertical space that the timeline entry list should own.

**Concern:** The timeline window mixes three unrelated concerns:

1. Chronological entry list (the actual timeline).
2. Retired Stand-ins list (per-kerbal admin state — `cachedRetiredKerbals` fed from `LedgerOrchestrator.Kerbals.GetRetiredKerbals()`, line `225`).
3. Resource budget footer (funds/science/rep reservations — `DrawResourceBudget`, line `551`).

The retired stand-ins belong in a kerbal-centric view alongside the other scattered kerbal state that users currently have no single place to look at:

- **Reserved crew** — `CrewReservationManager.CrewReplacements` maps reserved→replacement kerbal names. Today the only way to know which kerbals are reserved is to cross-reference the game's astronaut complex and notice the `(Parsek: reserved)` suffix or similar.
- **Replacement / stand-in kerbals** — the reverse map (who is standing in for whom, and which recording / tree committed them). `KerbalsModule.cs:420`-`479` is the canonical source.
- **Retired stand-ins** — kerbals who served as a stand-in, got recovered, and are now retired from duty. Currently shown in the Timeline footer.
- **Crew end-states per recording** — `CREW_END_STATES` ConfigNodes on each `Recording`; `KerbalsModule.cs` resolves them into live `ProtoCrewMember.State` updates. A Kerbals window could surface "which recording killed/stranded/recovered which kerbal" without the user having to open the Timeline and filter.

**Desired behavior:**

1. Remove `DrawRetiredKerbalsSection` from the Timeline window draw path (`TimelineWindowUI.cs:238`). Delete the `cachedRetiredKerbals` / `lastRetiredKerbalCount` fields (line `58`, rebuild at line `225`) from the Timeline window, or move them to the new Kerbals window's state.
2. Add a small toolbar-style button in the Timeline window header — next to the existing filter / sort controls — labeled "Kerbals" (or an icon if the toolbar has one). Clicking it toggles a new `KerbalsWindowUI` sub-window, same pattern as `UI/ActionsWindowUI.cs` and `UI/SpawnControlUI.cs` (which are already coordinated from `ParsekUI.cs`).
3. New `Source/Parsek/UI/KerbalsWindowUI.cs` following the existing UI sub-window convention:
   - Own window rect + `showWindow` toggle persisted on the parent `ParsekUI`.
   - Single `OnGUI` draw method with sections:
     - **Reserved crew** (grouped by the recording / tree that reserved them) — pull from `CrewReservationManager.CrewReplacements`, invert the map if the "reserved → replacement" direction is more natural than "replacement → reserved" for users.
     - **Active stand-ins** — replacements currently in play, with their originating recording name and status.
     - **Retired stand-ins** — existing content from `DrawRetiredKerbalsSection`, same list, same styling (`timelineGrayStyle` equivalent).
     - **Crew end-states summary** (optional v1) — count of dead / missing / recovered kerbals across all recordings. Collapsed by default if implemented.
4. Log on open/close like other sub-windows (`ParsekLog.Info("UI", "Kerbals window opened/closed")`).
5. Keep the resource budget footer where it is — it's still timeline-relevant (reservations are per-recording-in-flight and match the timeline's horizontal scope).

**State sources to wire up:**

- `CrewReservationManager.CrewReplacements` (line `22`) — `IReadOnlyDictionary<string, string>`. Keys are reserved kerbal names, values are replacement kerbal names.
- `LedgerOrchestrator.Kerbals.GetRetiredKerbals()` — existing retired list.
- `RecordingStore.CommittedRecordings` joined with `CrewReservationManager.ExtractCrewFromSnapshot(rec.GhostVisualSnapshot)` and `ExtractCrewFromSnapshot(rec.VesselSnapshot)` to compute start vs. end crew per recording. This is the data `KerbalsModule.cs:475` already uses — consider exposing a pure-static helper there so the Kerbals window can reuse it without duplicating the parse.
- `CREW_END_STATES` ConfigNodes on each `Recording` (search for `crewEndStatesResolved` / `CREW_END_STATES` in `Recording.cs` and `RecordingTree.cs` for the load path).

**Interaction with existing windows:**

- `UI/ActionsWindowUI.cs` already shows some kerbal-adjacent state (retired list is also referenced there, confirm which window owns the canonical display). If actions window duplicates the retired list today, the Kerbals window should become the single canonical display and Actions should link/point to it or drop that section.
- `ParsekUI.cs` coordinates sub-window lifetime. Add a `kerbalsWindowUI` field and dispatch `OnGUI` from the main `ParsekUI.OnGUI` like the other sub-windows.

**Tests:**

- Unit test the pure data shaping: a helper method on `KerbalsWindowUI` (or a pulled-out static) that takes `CrewReplacements` + `committedRecordings` + `retiredKerbals` and returns structured sections ready for rendering. Test empty-state, reserved-only, retired-only, and mixed populations.
- No in-game runtime test needed unless the window exercises a code path the existing KerbalsModule tests don't cover.

**Files to touch:**

- `Source/Parsek/UI/TimelineWindowUI.cs` — remove `DrawRetiredKerbalsSection`, `cachedRetiredKerbals`, `lastRetiredKerbalCount`; remove the call at line `238`; drop the cache-rebuild line at `225`; add the "Kerbals" toolbar button in the timeline header.
- `Source/Parsek/UI/KerbalsWindowUI.cs` — new file, follow `UI/ActionsWindowUI.cs` structure.
- `Source/Parsek/ParsekUI.cs` — register the new sub-window.
- `Source/Parsek/CrewReservationManager.cs` — add a public helper `BuildReservedStandinPairs()` if the inverted/structured view is cleaner than the raw `CrewReplacements` dict. Optional.
- `Source/Parsek.Tests/` — unit tests for the new pure data-shaping helper.
- `CHANGELOG.md` under Unreleased: "New Kerbals Status window (reserved crew, active stand-ins, retired stand-ins) accessible from the Timeline window. Retired Stand-ins list removed from the Timeline footer."

**Open questions:**

- Toolbar entry vs. Timeline-header button: the user asked for "a button *in the timeline window*", so put it there first. If the window turns out to be broadly useful, a separate main-toolbar entry can come later.
- Window persistence: should window position survive scene transitions? Follow whatever `ActionsWindowUI` does for consistency.
- Sandbox mode: the resource budget footer hides in sandbox (`TimelineWindowUI.cs:554`-`559`). The Kerbals window probably shouldn't hide in sandbox — kerbal state still matters there. Confirm and don't blanket-copy the mode guard.

**Status:** TODO. Size: S-M. Mostly UI plumbing + one new sub-window; no core logic changes. Cleans up the Timeline window's information density and gives kerbal admin state a proper home.

---

## 384. Copy the Learstar A1 mission from the S16 career into the test-career injector fixture as a far-away / map-view smoke test

**Source:** maintenance request `2026-04-14`. The injected test career has lots of near-KSC content (Pad Walk, KSC Hopper, Flea Flight, etc.) and a handful of reentry recordings, but no representative mission with significant map-view / far-away state. The S16 campaign has a Learstar A1 flight that is a natural smoke test for this category.

**Concern:** `Source/Parsek.Tests/SyntheticRecordingTests.cs` line `5497` calls `AddRealCareerRecordings(writer, kspRoot)` which reads the frozen fixture at `Source/Parsek.Tests/Fixtures/DefaultCareer/` (see `ResolveDefaultCareerFixtureDir` line `5952`). Any real-career recording used by `dotnet test --filter InjectAllRecordings` must live in that fixture — it does NOT read from the live S16 save at test time.

Learstar A1 is currently only in `Kerbal Space Program/saves/s16/persistent.sfs` (tree id `ab7b637507104dd8b621868485d7047e`, root recording `1bbb50cf98654a23a60b3248848b0301`). The tree has `maxDist = 500195928.56` m ( ~500 Mm, well outside Kerbin's 84 Mm SOI radius) — so it's a genuine map-view / far-away test case, not just another suborbital.

**What to copy:**

1. The full `RECORDING_TREE` node with id `ab7b637507104dd8b621868485d7047e` from `saves/s16/persistent.sfs`, including all child `RECORDING` nodes. As of the `2026-04-14` snapshot this includes at least:
   - `1bbb50cf98654a23a60b3248848b0301` ("Learstar A1", 172 points, rewind save `parsek_rw_0a74d6`)
   - `393b82ccb697492bb7b35c6c621f9d07` ("Learstar A1 Debris", 27 points, `isDebris=True`)
   - Plus the remaining `Learstar A1 Debris` siblings under the same tree (grep for `Learstar` in `saves/s16/persistent.sfs` shows at least three debris recordings around lines 329, 776, 817, 840 — count them fresh when doing the actual copy, the S16 save grows).
2. The matching sidecar files under `saves/s16/Parsek/Recordings/` — for each recording's `recordingId`, copy `<id>.prec`, `<id>_vessel.craft`, `<id>_ghost.craft` (and the readable `.txt` mirrors only if the fixture already stores them — check the existing DefaultCareer fixture before deciding; keeping mirrors out of the fixture is probably fine since they're regenerable and add bytes to the git tree).
3. The rewind save file `parsek_rw_0a74d6.sfs` from `saves/s16/` — copy to `Source/Parsek.Tests/Fixtures/DefaultCareer/` so rewind-resume test paths can exercise the Learstar recording without needing a live S16 save. Verify whether the existing fixture already includes rewind saves (it should — `CopyRealRecordingFiles` at line `6026` copies them for live injection).
4. Any `MILESTONE_STATE` entries tied to the Learstar flight — `AddRealCareerRecordings` already forwards all `MILESTONE_STATE` nodes from the fixture's `ParsekScenario` wholesale, so once the tree lives in the fixture persistent.sfs the milestones come along automatically.

**Careful: the fixture persistent.sfs is a minimal hand-pruned save, not a clone of a live career.** Dropping the `RECORDING_TREE` node in isn't enough — verify:

- No references to vessels/crew/parts that don't exist elsewhere in the fixture. Debris-only children should be self-contained (snapshot + trajectory), but the root recording's ghost snapshot must not depend on a MODULE from a mod the fixture persistent.sfs doesn't declare. If there's a mismatch, either strip the offending MODULE from the ghost snapshot or note in this TODO that the fixture needs an extra part-database shim.
- No hard-coded universe time mismatches. The injector resolves `baseUT` dynamically (`ReadUTFromSave`, line `5349`) and offsets synthetic recordings from it (30s/60s/... see `SyntheticRecordingTests.cs:77`). Real career recordings keep their original UT range because they're copied verbatim — confirm Learstar A1's StartUT/EndUT don't collide with any existing real or synthetic recording's window in the test save. If the S16 campaign time is wildly later than the test save's daytime baseUT, the recording will render as "far future"; that may or may not be desirable for the smoke test. Worth checking what the current `AddRealCareerRecordings` flow does for the existing fixture recordings as a reference point.
- Sidecar file format compatibility. The fixture currently stores `v1`-`v3` era files. Whatever format Learstar was recorded in must either match or go through a migration on load. Check `recordingFormatVersion` on the copied nodes (S16 Learstar shows `recordingFormatVersion = 3`) against the other fixture recordings — if they match, no migration concern.

**Steps to actually do the copy:**

1. Grep `saves/s16/persistent.sfs` for `Learstar` and collect every line from the tree's opening brace to its closing brace. Easiest via a small helper script or the `scripts/collect-logs.py` pattern — or just manual line extraction with the line numbers from grep and careful brace matching.
2. Paste the full `RECORDING_TREE` block into `Source/Parsek.Tests/Fixtures/DefaultCareer/persistent.sfs` under the existing `ParsekScenario` SCENARIO node, after the current recording trees. Preserve surrounding indentation exactly.
3. For each `recordingId` in the copied tree, copy `saves/s16/Parsek/Recordings/<id>.prec` + `_vessel.craft` + `_ghost.craft` to `Source/Parsek.Tests/Fixtures/DefaultCareer/Parsek/Recordings/`. Do NOT copy the `.txt` mirrors unless the fixture already has them for other recordings.
4. Copy `saves/s16/parsek_rw_0a74d6.sfs` to `Source/Parsek.Tests/Fixtures/DefaultCareer/` if the other real-career fixture recordings have their rewind saves stored there; otherwise skip and drop the `rewindSave` value from the root recording.
5. Run `dotnet test --filter InjectAllRecordings`. The existing assertions on line `5518`-`5535` only check for specific synthetic vessel names; add a new assertion `Assert.Contains("vesselName = Learstar A1", content)` so a future fixture regression (someone accidentally strips it) gets caught by the suite.
6. Launch KSP, load the test career, confirm Learstar A1 appears in the recordings table, the ghost map presence shows at tracking station far from Kerbin, and the timeline plays back through map view without exceptions. This is the smoke test payoff — no unit test can exercise the map-view code path.

**Fixture size impact:** Learstar A1's 172 trajectory points + debris children + vessel/ghost snapshots will add bytes to the git-tracked fixture. Check total size with `du -sh Source/Parsek.Tests/Fixtures/DefaultCareer` before and after the copy — if the increase is more than a few hundred KB, trim debris children that aren't strictly needed for the map-view smoke test (the root recording alone is the load-bearing one). Note the final size in the commit message.

**Files to touch:**

- `Source/Parsek.Tests/Fixtures/DefaultCareer/persistent.sfs` (add `RECORDING_TREE` block)
- `Source/Parsek.Tests/Fixtures/DefaultCareer/Parsek/Recordings/` (new sidecar files)
- `Source/Parsek.Tests/Fixtures/DefaultCareer/parsek_rw_0a74d6.sfs` (optional rewind save)
- `Source/Parsek.Tests/SyntheticRecordingTests.cs` (add `vesselName = Learstar A1` assertion near line `5518`-`5535`)
- `CHANGELOG.md` (Dev / internal section — not user-facing): "test career injector now includes a far-away / map-view smoke-test recording (Learstar A1 from S16 campaign)"

**Status:** DONE (PR #304). Size: S. Required a small runtime code change: `AddRealCareerRecordings` was extended to iterate `scenarioNode.GetNodes("RECORDING_TREE")` and forward trees via `ScenarioWriter.AddTree` (previously only standalone RECORDINGs were read, and standalone format is blocked by the T56 filter). All 5 Learstar recordings retained (root `1bbb50cf98654a23a60b3248848b0301` + 4 debris children) along with the `parsek_rw_0a74d6.sfs` rewind save placed under the new `DefaultCareer/Parsek/Saves/` subdirectory (the existing `CopyRealRecordingFiles` loop reads rewind saves from `sourceCareerDir/Parsek/Saves/`, not from the fixture root). `InjectAllRecordings` gained three assertions (`vesselName = Learstar A1`, `vesselName = Learstar A1 Debris`, `treeName = Learstar A1`). Fixture size: 1.1M → 1.7M (`du -sh`; +577098 bytes, dominated by the 385 KB rewind save and the 74 KB root `.prec`). Follow-up: nested the `Learstar A1 / Debris` group under the main `Learstar A1` group by adding a `GROUP_HIERARCHY` node to the fixture ParsekScenario and extending `ScenarioWriter` with `AddGroupHierarchyEntry(child, parent)` + emission in `BuildScenarioNode`; `AddRealCareerRecordings` now forwards every `GROUP_HIERARCHY/ENTRY` from the fixture so the UI shows debris as a collapsible sub-group of the main mission group instead of a flat sibling.

**Reinjection gotcha:** running `dotnet test --filter InjectAllRecordings` from a sibling git worktree silently short-circuits (xUnit reports Passed in ~100 ms) because `ResolveKspRoot()` probes relative to `ProjectRoot` and does not find `Kerbal Space Program/` outside the worktree. The MSBuild `-p:KSPDir=...` property only wires up DLL references at build time. To actually reinject, set the `KSPDIR` environment variable for the test process: `KSPDIR="C:\Users\vlad3\Documents\Code\Parsek\Kerbal Space Program" dotnet test --filter InjectAllRecordings -p:KSPDir=...`.

---

## 383. Ghost engine flames should scale with nozzle diameter like stock KSP

**Source:** maintenance request `2026-04-14`. Ghost flames on big engines (Mainsail, Mammoth, Rhino, Twin-Boar, KS-25x4 etc.) look visibly too small compared to the same engines running live in-game on the player vessel.

**Concern:** `EngineFxBuilder.cs` clones the engine FX prefabs and places them on the ghost without rescaling to match the engine's thrust transform / nozzle size. The only size adjustments currently applied are:

- Line `512`: `fxInstance.transform.localScale *= 0.35f` for the RAPIER white flame (`isRapierWhiteFlame`).
- Line `517`-`518`: `main.startSizeMultiplier *= 0.45f` and `startSpeedMultiplier *= 0.75f` also for the RAPIER white flame only.
- Lines `273` and `305`: `fxClone.transform.localScale = child.localScale` copies the *source child* local scale, which is typically `(1,1,1)` for prefab FX children — not the engine's thrust transform scale.

Nothing in the pipeline reads the engine's nozzle diameter / thrust transform scale and propagates it to the FX. Stock KSP gets this right because the live `ModuleEngines` updates the particle system scale from the thrust transform each frame; Parsek never sees that update because the ghost FX is a static clone built at snapshot time.

**What "proportional to nozzle diameter" means in practice:**

KSP's engine FX emit from one or more `thrustTransform` GameObjects under the part. The `thrustTransform.lossyScale` (or `localScale` up the chain) encodes how the modeler sized the nozzle — a Mainsail's thrust transform is bigger than a Reliant's. Stock KSP scales the running effect's `startSize` / `startSpeed` by `thrustTransform.lossyScale.magnitude` (or a similar factor — investigate via `ilspycmd` on `ModuleEngines.FXUpdate` / `EffectList` before implementing). The same factor, captured at snapshot time, should be applied to the ghost FX clone.

**Data capture (snapshot-time):** For each `ModuleEngines` on the recorded vessel, record a per-module `ThrustTransformScale` (float). Options:

1. Average of `thrustTransform.lossyScale.magnitude / sqrt(3)` across all `thrustTransforms` on the module. This normalizes `(1,1,1)` → `1.0`.
2. Bounding sphere radius derived from the thrust transform positions — closer to "nozzle diameter" but harder to reason about when an engine has multiple widely-spaced thrust points.

Start with option 1. Add the field to the engine FX info payload (`GhostTypes.cs` or wherever `EngineGhostInfo` lives — grep for it). Serialize alongside existing engine FX data if it needs to survive save/load (check whether ghost FX is rebuilt from `_ghost.craft` or cached).

**Application (build-time):** In `EngineFxBuilder.TryBuildEngineFX` (look for the main `BuildEngineFX` entry point, not just `TryBuildEngineFX`), after the `fxInstance` is cloned and parented:

- Multiply `fxInstance.transform.localScale *= thrustTransformScale`.
- Walk `fxInstance.GetComponentsInChildren<ParticleSystem>(true)` and multiply each `main.startSizeMultiplier *= thrustTransformScale`.
- Consider whether `startSpeedMultiplier` should also scale (stock appears to scale it too, preserving the flame aspect ratio). Verify visually.
- RAPIER white flame's `0.35f` / `0.45f` / `0.75f` constants need to compose with the new scale — keep them as *relative* adjustments applied on top of the nozzle-derived base.

**Edge cases to check:**

- Engines with multiple thrust transforms (Mammoth, Twin-Boar, KS-25x4) — the per-transform FX is currently instantiated per thrust point. Nozzle diameter here should be per-point, not averaged, so each flame plume stays the right size.
- Engines with zero thrust transforms or `lossyScale == Vector3.zero` (broken modded parts) — fall back to `1.0`, don't divide by zero.
- Fixed-scale model FX entries (`MODEL_MULTI_PARTICLE`) that already encode size in the config — these should *not* get a second scale factor applied. Discriminate by node type or test on a few stock engines (Ant, Spider, Reliant) to confirm they still look right.
- Gimbal-animated engines — make sure the scale multiplication doesn't get reset by any per-frame code path that re-assigns `localScale`.

**Files to touch:**

- `Source/Parsek/EngineFxBuilder.cs` — main build path, apply scale.
- `Source/Parsek/GhostTypes.cs` or wherever `EngineGhostInfo` is defined — add `ThrustTransformScale` field (verify file first).
- `Source/Parsek/GhostVisualBuilder.cs` — snapshot-time capture site that feeds `EngineFxBuilder`.
- `Source/Parsek.Tests/` — unit test for the scale computation helper (pure-static function that takes a list of `Vector3` scales and returns the factor).
- `Source/Parsek/InGameTests/RuntimeTests.cs` — runtime test that snapshots a Mainsail on a test vessel, builds the ghost, and asserts the flame FX scale is approximately `mainsailThrustScale / reliantThrustScale` bigger than a Reliant-built ghost. Visual verification still needed.
- `CHANGELOG.md` under Unreleased: "Ghost engine flames now scale with nozzle size, fixing visibly undersized flames on Mainsail, Mammoth, and other large engines."

**Status:** TODO. Size: S-M. Mostly data capture + one scale multiplication; the hard part is picking the right factor and visually validating against stock. Decompile `ModuleEngines.FXUpdate` / `EffectList.OnEvent` first to confirm the formula stock actually uses.

---

## ~~382. Group "W" button should cycle to the next watchable vessel on each press~~

**Source:** maintenance request `2026-04-14`. Today the group `W` button only ever resolves to one target (the group's "main" recording), so repeated presses toggle watch on/off for that single vessel. The user wants a **watch-next** semantics instead.

**Concern:** `UI/RecordingsTableUI.cs` line `1336`-`1414` draws the group `W` button. It calls `FindGroupMainRecordingIndex(descendants, committed)` (line `2503`) which picks the non-debris descendant with the earliest `StartUT`, then routes that through `GhostPlaybackLogic.ResolveEffectiveWatchTargetIndex` (line `3586` in `GhostPlaybackLogic.cs`) to get a single `resolvedWatchIdx`. The button enters watch mode on that one target (`flight.EnterWatchMode(resolvedWatchIdx)`, line `1409`). There is no concept of "next" — pressing `W` on a group with five watchable vessels will always pick the same main recording.

**Desired behavior:** `W` on a group acts like a cyclic watch-next iterator over the group's watchable descendants.

- Press 1: enter watch on the first eligible descendant (current "main" pick is fine as the seed).
- Press 2: advance to the next eligible descendant in a stable order, switching watch target.
- Press N: after the last eligible descendant, wrap back to the first (or exit watch — pick one, see open questions).
- The `W*` indicator should still light up whenever *any* descendant in the group is currently watched, not just the first one.
- Tooltip should reflect "next target: X" instead of "target: X" so the user knows what the next press will do.

**Eligibility filter:** "watchable" = same filter the current single-target path uses (`hasGhost && sameBody && inRange && !IsDebris`). A vessel that is offscreen because of body/range should be skipped in the rotation — it never would have been enterable anyway. The rotation candidate set must also exclude the currently watched vessel's resolved chain head when it maps back to a group sibling (otherwise pressing `W` a second time on a single-vessel-watchable group would re-enter the same vessel).

**State storage:** Per-group index cursor. Options:

1. Transient in `RecordingsTableUI` (lost on UI close / scene change) — simplest, matches per-session mental model.
2. Persisted in `GroupHierarchyStore` alongside `expandedGroups`. Heavier, only worth it if users complain about losing position.

Start with option 1. Dictionary keyed by group name → last-entered `RecordingId` (not index — index is unstable across rewind/truncate, see bug #279 context on the same button). On each press, find the current cursor position in a freshly computed eligible list (by `RecordingId`), advance to the next, store the new `RecordingId`.

**Interaction with existing watch infrastructure:**

- `flight.EnterWatchMode(newIdx)` while already in watch mode should switch targets cleanly. Verify `WatchModeController` handles the switch without exiting and re-entering (which would reset camera anchoring). If it doesn't, either fix the controller or call a new `flight.SwitchWatchTarget(newIdx)` method.
- The per-group log line at `1391`-`1395` (bug #279 infrastructure) currently logs transitions for a single resolved target. For watch-next, log `current→next` advances explicitly with the rotation position, and keep the cached `resolvedTargetId` pointing at the *next* target so the transition log still makes sense.
- `lastCanWatchByGroup` and `lastResolvedWatchTargetByGroup` caches (line `143`-`144`) need to either be repurposed for the cursor or coexist with a new `groupWatchCursorByGroupName` dict. Prefer a new dict — the existing caches are specifically for logging transitions.

**Open questions:**

- After the last eligible vessel, does pressing `W` wrap to the first, or exit watch mode? Exit-then-wrap feels cleaner ("I've seen them all") but wrap is simpler. Pick wrap for v1.
- Should the cursor reset when the eligible set changes (e.g., a new vessel becomes in-range)? Yes — if the stored `RecordingId` is no longer in the eligible list, fall back to the first eligible entry.
- Does the ordering need to be user-visible? Stable order by `StartUT` (same as `FindGroupMainRecordingIndex`) is the natural default. If users want VesselName ordering, defer to a follow-up.

**Files to touch:**

- `Source/Parsek/UI/RecordingsTableUI.cs` — group `W` button draw site (line `1336`-`1416`), add cursor dict, replace single-target resolution with rotation logic.
- `Source/Parsek/GhostPlaybackLogic.cs` — consider adding a pure-static `AdvanceGroupWatchCursor(descendants, committed, cursorRecId, flight.WatchedRecordingIndex)` helper so the rotation decision is unit-testable independent of UI.
- `Source/Parsek/WatchModeController.cs` — verify clean target switch, extend if needed.
- Tests covering the new cursor advancement logic (empty eligible set, single-entry rotation, wrap, cursor-stale-recovery).
- `CHANGELOG.md` under Unreleased: "Group `W` button now cycles to the next watchable vessel in the group on each press instead of always toggling the same target."

**Status:** ~~Fixed~~. High-visibility UX improvement for groups with multiple simultaneous ghosts.

**Fix:** Added `GhostPlaybackLogic.AdvanceGroupWatchCursor` (new pure-static helper + `GroupWatchAdvanceResult` struct) that returns the next watchable descendant in stable `(StartUT, RecordingId)` order, skipping the currently-watched target so repeat presses advance the rotation. `UI/RecordingsTableUI.cs` group W button now stores a transient per-group cursor (`groupWatchCursorByGroupName`, keyed by group name, valued by the last-entered RecordingId) and wires it to the helper. Single-eligible-and-watched groups toggle off cleanly; stale cursors fall back to the first eligible entry; an eligibility-set fingerprint replaces the per-target value in the bug `#279` transition log so cursor advances no longer spam the log. `PruneStaleWatchEntries` gained a fifth `groupWatchCursorByGroupName` overload and now drops cursor entries whose stored RecordingId has left the committed list (or clears the whole dict when the committed list is empty). Covered by new `GroupWatchCursorTests` (16 cases) and an extended `PruneStaleWatchEntries_StaleGroupCursorEntries_Removed` test.

---

## ~~381. "Loop every" semantics: switch from end-to-start gap to launch-to-launch period~~

**Source:** maintenance request `2026-04-14`. The current "Loop every" field accepts negative values, which is confusing and only makes sense under the current end-to-start model.

**Concern:** `GhostPlaybackLogic.ResolveLoopInterval` (line 226 in `Source/Parsek/GhostPlaybackLogic.cs`) treats `LoopIntervalSeconds` as the gap between the previous cycle's *end* and the next cycle's *start*, with `Math.Max(-duration + minCycleDuration, interval)` clamping so that negative values represent overlap (next loop launches before the current one finishes). Downstream consumers (`GhostPlaybackEngine.TryComputeLoopPlaybackUT`, `ParsekKSC.GetLoopIntervalSeconds`, `ParsekFlight.GetLoopIntervalSecondsForWatch`, the `LoopTimeUnit.Auto` branch) all follow the same end-to-start convention.

The issue: recording duration is variable (a KSC Hopper is 30s, a Mun landing is 40 min), so "end + X seconds" is a useless unit for scheduling — users can't reason about cadence without first knowing every recording's length. Launch-to-launch period is what users actually want ("launch one every 10 minutes").

**Proposed change:**

1. Reinterpret `LoopIntervalSeconds` as **launch-to-launch period** — the fixed delta between successive cycle start times. The field is non-negative by definition.
2. Reject negative values in the UI (`UI/RecordingsTableUI.cs` loop period editor) and in `ResolveLoopInterval`. Remove the `-duration + minCycleDuration` lower bound and replace it with `Math.Max(minCycleDuration, interval)`.
3. Adapt the `LoopTimeUnit.Auto` path (`ParsekSettings.autoLoopIntervalSeconds`, default `10.0f`): Auto should now mean "launch every T seconds" with T = the global auto setting, independent of recording duration. Today Auto also feeds through `ResolveLoopInterval` end-to-start, so the semantics flip here too.
4. Migration: existing recordings have `LoopIntervalSeconds = 10.0` (new default on `Recording.cs:44`) or user-edited values. A straight reinterpretation means a recording that was "10s gap after a 40s cycle" (= 50s launch-to-launch) becomes "10s launch-to-launch" (= 30s overlap). Decide whether to migrate on load (add `duration` to stored value on first v0 read) or accept the one-time behavior change and note it in CHANGELOG. Serialized field name `loopIntervalSeconds` in `.sfs` and `.prec` stays the same.
5. Update `GhostPlaybackLogic.ResolveLoopInterval` tests, `GhostPlaybackEngine.TryComputeLoopPlaybackUT` tests, and any in-game tests that exercised the overlap (negative-interval) path. Overlap as a feature is preserved naturally: if `LoopIntervalSeconds < duration`, the next cycle launches before the previous one ends — same visual effect, no sign convention needed.
6. Rename the UI label to clarify: "Launch every" instead of "Loop every", so the mental model matches the math.

**Files to touch:**

- `Source/Parsek/GhostPlaybackLogic.cs` (`ResolveLoopInterval`, line 226+)
- `Source/Parsek/GhostPlaybackEngine.cs` (`TryComputeLoopPlaybackUT`, `GetLoopIntervalSeconds`)
- `Source/Parsek/ParsekKSC.cs` (`GetLoopIntervalSeconds`, line 732)
- `Source/Parsek/ParsekFlight.cs` (`GetLoopIntervalSecondsForWatch`, `autoLoopIntervalSeconds` plumbing at 8427/8504/8516)
- `Source/Parsek/ParsekSettings.cs` (`autoLoopIntervalSeconds` docstring/range)
- `Source/Parsek/Recording.cs` (default value comment, line 44)
- `Source/Parsek/UI/RecordingsTableUI.cs` (loop period editor — reject negatives)
- `Source/Parsek/UI/SettingsWindowUI.cs` (global auto-loop field description)
- Tests covering `ResolveLoopInterval` and loop UT computation
- `CHANGELOG.md` (behavior change notice) and this entry on completion

**Status:** ~~TODO~~ DONE (branch `fix/381-launch-to-launch`, 2026-04-15).

**Fix:** Reinterpreted `LoopIntervalSeconds` as the launch-to-launch period.

- Core math: `cycleDuration = Math.Max(intervalSeconds, GhostPlaybackLogic.MinCycleDuration)` everywhere. The `duration + intervalSeconds` formula is gone.
- Overlap dispatch: new helper `GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration) => intervalSeconds < duration`. Replaces `intervalSeconds < 0` checks in `GhostPlaybackEngine.UpdateLoopingPlayback`, `ParsekKSC.UpdateKscGhosts`, and `WatchModeController.TryStartWatchSession` + `ResolveWatchPlaybackUT`.
- Pause window: `intervalSeconds > duration && cycleTime > duration` (was `intervalSeconds >= 0` or `> 0`). When period equals duration, cycles are back-to-back with no pause. When period is shorter, overlap handles it.
- `ResolveLoopInterval` defensively clamps all values below `MinCycleDuration` to `MinCycleDuration` and emits `ParsekLog.Warn("Loop", ...)`. The old `Math.Max(-duration + minCycleDuration, interval)` formula is gone — `duration` is no longer a parameter to clamp against.
- Dead-code fallback `if (cycleDuration <= MinLoopDurationSeconds) cycleDuration = duration;` deleted from `GhostPlaybackEngine.TryComputeLoopPlaybackUT` (~line 1282), `ParsekKSC.TryComputeLoopUT` (~line 708), and `WatchModeController.ResetLoopPhaseForWatch` (~line 1479). Under `Math.Max` it's unreachable.
- UI: column header in `RecordingsTableUI.cs` renamed from "Every" to "Period" with a tooltip describing launch-to-launch semantics. Negative values rejected outright in `CommitLoopPeriodEdit`; positive-but-below-min clamped with an info log. Settings window label "Auto-loop every" → "Auto-launch every" with a new tooltip; `CommitAutoLoopEdit` also clamps to `MinCycleDuration`.
- `ComputeLoopPhaseFromUT` doc comment notes the semantic shift: negative intervals no longer mean zero-pause / continuous playback; they clamp to `MinCycleDuration=1` (extreme overlap). Phase helper does not own the overlap dispatch — callers consult `IsOverlapLoop`.
- Migration: no per-recording migration. Default `10.0` is unaffected. Pre-#381 saves with negative values are defensively clamped at playback time; load-time warn in both `RecordingTree.Load` (~line 696) and `ParsekScenario.LoadRecordingMetadata` (~line 2666) surfaces the issue so users can re-enter the intended period.
- Docstrings added to `Recording.LoopIntervalSeconds` and `ParsekSettings.autoLoopIntervalSeconds`. `RecordingOptimizer.CanMerge` no longer hardcodes `10.0` — uses `GhostPlaybackLogic.DefaultLoopIntervalSeconds`.
- Tests: `AutoLoopTests` rewrote `ResolveLoopInterval_ManualMode_NegativePreserved` → `_NegativeClampsToMin` with log assertion, added `_BelowMin_Clamps` / `_Zero_Clamps` / `_AutoMode_Independent_Of_Duration` / `_AutoMode_NegativeGlobal_ClampsToMin` / `_PeriodShorterThanDuration_Preserved`. `LoopPhaseTests` rewrote the whole `ComputeLoopPhaseFromUT_Tests` class under #381 semantics plus added `PeriodShorter` / `PeriodEqual` / `PeriodGreater` and `ZeroInterval_ClampsToMin` / `NegativeInterval_ClampsToMin`. `GhostPlaybackEngineTests` rewrote the `TryComputeLoopPlaybackUT_*` block to use period=110 > duration=100 for single-ghost/pause scenarios, added `_PeriodLongerThanDuration_HasPauseWindow`, `_PeriodEqualsDuration_NoPauseWindow`, `_PeriodShorterThanDuration_OverlapCycles`, `_NegativeInterval_DefensivelyClamped_NoThrow`; loop-range tests now use interval=50 with a 40s range. `KscGhostPlaybackTests` rewrote `TryComputeLoopUT_*` and `GetLoopIntervalSeconds_*` tests — zero/negative intervals now assert clamping to `MinCycleDuration`, added `TryComputeLoopUT_PeriodShorterThanDuration_Overlaps` and `_NegativeInterval_ClampsDefensively_NoThrow`. `RuntimePolicyTests` rewrote 7 `NegativeInterval_*` tests as `PeriodShorter_*` / `ShortPeriod_*` plus updated the `TryComputeLoopPlaybackUT_RespectsPlaybackAndPauseWindows` theory to use period=30 with duration=20. `BugFixTests.Bug84_LoopPhaseOverflowTests.ComputeLoopPhaseFromUT_LargeElapsed_NoOverflow` and `.TryComputeLoopPlaybackUT_LargeElapsed_NoOverflow` rewritten to use period=1s with 3e9/5e9 elapsed seconds (matching the new `MinCycleDuration` floor). `AnchorLifecycleTests.AnchorLoaded_PhaseComputed_AtMidCycle` and `.AnchorReloaded_PhaseRecomputed_NotFromStart` updated to use period=110. Total: 6208 passing (1 pre-existing Unity-runtime skip: `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`).

---

## ~~372. PR #253 / #254 follow-up: dead helpers left behind after batch-skip pivot~~

**Source:** light review of PRs `#246`-`#255` after `v0.8.0` ship.

**Concern:** PR `#253` ("FLIGHT save/load test cleanup and quickload canary guards") added new infrastructure to support live `OnSave`/`OnLoad` round-trip tests under synthetic scenario loads:

- `ParsekFlight.NormalizeAfterSyntheticScenarioLoad`
- `ParsekFlight.DestroyAllTimelineGhosts(string ghostMapReason)` overload (default `"rewind"`)
- `WatchModeController.ClearAfterSyntheticScenarioLoad`
- `WatchModeController.HasDeferredWatchAfterFastForward` exposure
- `Source/Parsek.Tests/.../SyntheticScenarioLoadHelpers.cs` (test helper)

The very next PR `#254` ("Fix `#332` follow-up: keep destructive quickload tests out of FLIGHT batches") changed the diagnosis: the live round-trip tests weren't the cause, the quickload canary itself was breaking KSP. `#254` deleted the live round-trip tests and `SyntheticScenarioLoadHelpers.cs` wholesale, but left the production-side helpers from `#253` in place. They now have no live caller.

**Why it didn't get caught:** the cleanup happened in the same day as the original add, and `#254` was framed as a test-runner change, not a production code revert.

**Status:** ~~Fixed~~. Cleanup pass, low priority.

**Fix:** deleted the four orphans after a grep-confirmed zero-caller check across `Source/`, `docs/`, and `scripts/`:

- `ParsekFlight.NormalizeAfterSyntheticScenarioLoad` removed outright.
- `ParsekFlight.DestroyAllTimelineGhosts(string ghostMapReason = "rewind")` reverted to its pre-`#253` parameterless signature with a hard-coded `"rewind"` ghost-map removal reason; the two live call sites (`ParsekFlight.OnDestroy` scene teardown and `ParsekUI` "wipe all recordings") already called it without arguments.
- `WatchModeController.ClearAfterSyntheticScenarioLoad` removed outright — this was the ~15-field duplicate of `ResetWatchState` flagged here as a maintenance trap.
- `ParsekFlight.HasDeferredWatchAfterFastForward` property removed (`pendingWatchAfterFFId` itself stays, still live in the deferred-FF handoff path).

Build: clean (0 warnings, 0 errors). Tests: 6195/6196 passing; the single failure is the pre-existing `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` Unity-runtime `SecurityException` tracked as `#380`.

---

## ~~380. `scripts/release.py` aborts on the pre-existing `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` test failure~~

**Source:** `v0.8.1` release execution on `2026-04-14`. Discovered while running `python scripts/release.py` from clean `main` after `PR #293` merged.

**Concern:** `Parsek.Tests.GhostPlaybackEngineTests.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` throws `System.Security.SecurityException : ECall methods must be packaged into a system module` when the xUnit harness tries to instantiate a Unity `GameObject` outside the Unity runtime. The failure has been mentioned as "pre-existing" in `PR #258`, `#274`, `#276`, `#279`, and several other reviews during the `0.8.0` → `0.8.1` cycle.

`scripts/release.py` line `104` runs `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` unfiltered, so the single failure aborts the entire release flow with `ERROR: Running Tests failed (exit code 1)` and the script never reaches the `package(version)` step. The `v0.8.1` release was packaged manually by:

1. Running `dotnet test --filter "FullyQualifiedName!~SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT"` to confirm everything else is green (`6195/6195` passing on that filter).
2. Calling `release.py`'s `package(read_version())` directly via `python -c` to skip the failed test gate but reuse the same zip layout.

**Why it bites now and not for `0.8.0`:** the test was added or broke after `v0.8.0` shipped (`PR #258` was the first review to mention it). `release.py` has been silently broken for the entire `0.8.1` cycle.

**Two clean fixes (pick one):**

1. **Mark the test `[Fact(Skip = "...")]` in source.** Lowest-friction; the in-game runtime suite covers the same behavior with a real Unity `GameObject`. Suggested skip reason: `"Unity GameObject instantiation requires runtime — covered by the in-game runtime suite (`InGameTests/RuntimeTests.cs`)."`
2. **Add `--filter "FullyQualifiedName!~SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT"` to the `dotnet test` invocation in `scripts/release.py`.** Keeps the test definition intact for future Unity-runtime fixers, but moves the exclusion into the release script itself. Document it inline so the next person knows why.

**Fix:** Applied option 1 from this entry on branch `fix/release-py-test-skip`. Added `[Fact(Skip = "Unity GameObject instantiation requires runtime - covered by the in-game runtime suite (InGameTests/RuntimeTests.cs).")]` to `Source/Parsek.Tests/GhostPlaybackEngineTests.cs` line 1193 (the one test attribute is the only source change). `scripts/release.py` now runs clean: `dotnet test` reports `Failed: 0, Passed: 6195, Skipped: 1, Total: 6196`, and the script reaches the `package` step and produces `Parsek-v0.8.2.zip` in the repo root. No matching in-game test exists yet under `Source/Parsek/InGameTests/` for `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`, so the skip reason keeps the generic pointer to the runtime suite from the todo wording; future work can add a dedicated in-game test if priming-specific coverage is wanted.

**Status:** ~~Fixed~~. Previously blocked the next scriptable release; unblocked by this branch.

---

## 379. PR #285 follow-up: load-bearing storage dedupe rewrite needs fresh-eyes pass

**Source:** light review of PRs `#276`-`#285` after `v0.8.0` ship.

**Concern:** PR `#285` ("Fix rewind resume metadata and background recording cleanup") rewrote `RecordingStore.AppendPointsFromTrackSections` and `RecordingStore.AppendOrbitSegmentsFromTrackSections` to use full suffix-prefix overlap dedupe via the new `FindTrajectoryPointSuffixPrefixOverlap` helper, replacing the previous single-boundary-element dedupe. It also tightened `FlatTrajectoryExtendsTrackSectionPayload` to reject non-monotonic flat points / orbit segments and added `PruneSinglePointDestroyedDebrisLeaves` with a `SidecarLoadFailed` guard.

The rewrite is in the hottest path of the storage layer (every flush + every load + every merge runs through `Append*FromTrackSections`). The light review found no bugs, but this is exactly the kind of code that benefits from a second reviewer.

**What to look at:**

- `FindTrajectoryPointSuffixPrefixOverlap` worst-case complexity (O(n*m) where n = flat tail length, m = first-section prefix length). Confirm the bound is acceptable on large recordings (Kerbal X breakup with hundreds of debris frames).
- Boundary case: flat list ends exactly at the start of a section with zero overlap — should return overlap = 0, not 1.
- Boundary case: flat list ends in a non-monotonic spike that would be rejected by `FlatTrajectoryExtendsTrackSectionPayload` — confirm dedupe doesn't get called in that path.
- `PruneSinglePointDestroyedDebrisLeaves` guard list (`Points.Count==1 && no TrackSections && no Orbit && no SurfacePos && !SidecarLoadFailed`) — confirm `SidecarLoadFailed` is set anywhere a sidecar load fails, not just `LoadRecordingFiles`.

**Status:** TODO. No visible bug, just the size/criticality of the rewrite warrants a second pass. Roll into the next storage-layer review session.

---

## 378. PR #266 follow-up: section-authoritative exact-match cost on large recordings

**Source:** light review of PRs `#266`-`#275` after `v0.8.0` ship.

**Concern:** PR `#266` tightened `ShouldWriteSectionAuthoritativeTrajectory` to require `FlatTrajectoryExactlyMatchesTrackSectionPayload` in addition to `HasCompleteTrackSectionPayloadForFlatSync`. The exact-match check rebuilds the points + orbit segments from sections and compares element-by-element against the flat lists on every save.

For typical recordings this is fine. For pathological cases (a long-tree mission with thousands of points or many orbital checkpoints), `OnSave` now does a full rebuild + compare on every save tick, which is O(n) work per recording per save. With many recordings in a session this could show up as save-tick stutter.

Two paths if it bites:

1. Cache a "flat is in sync with sections" bit on the `Recording` and invalidate it inside the methods that mutate either side (any place `MarkFilesDirty()` is called from a section/flat mutation). The exact-match check then becomes O(1).
2. Drop the exact-match requirement and trust that any `MarkFilesDirty()` caller has already kept flat and sections in sync. Risk: regressing to the pre-`#266` problem where drifted flat/section data wrote a section-authoritative sidecar that didn't round-trip.

**Status:** TODO. Not a known regression — flag for diagnostics review only. Add a verbose log line in the slow path that times the exact-match check and emit a warning if it exceeds a few ms.

---

## 377. PR #266 / #270 / #276 etc. minor smells found during #266-#292 review

**Source:** light review of PRs `#266`-`#292` after `v0.8.0` ship. Collected here so they don't disappear; none are blocking.

- **`#267` dead `var activeWatchTarget` recompute.** `EnterWatchMode` calls `GetWatchTarget(...)` after `TryApplySwitchedWatchCameraState` already called `SetTargetTransform`, just to populate a log line. Cosmetic.
- **`#269` legacy group inference fragility.** `EnsureAutoGeneratedTreeGroups` lazily populates the new fields from `RecordingGroups[0]` for trees loaded from pre-fix saves. If the user reordered groups before the fix landed, the inference picks the wrong "primary" group. Acceptable as best-effort but worth a note in the migration log.
- **`#271` dead `ComputeSnapshotVisualRootLocalOffset` helper.** After dropping the debris CoM shift, the helper unconditionally returns `Vector3.zero`. Either delete it or comment it as an extension point.
- **`#272` odd `||` guard in double-click handler.** `if (flight != null || recIndex >= 0)` enters `HandleWatchRequest` even when `flight == null` so the helper can emit the "materialize" message. Counterintuitive but intentional.
- **`#274` per-frame `EnsureGhostOrbitRenderers` while restore pending.** `UpdateMapFocusRestore` calls `GhostMapPresence.EnsureGhostOrbitRenderers()` every frame until the renderers appear. Cheap but worth a one-time-per-pending-restore gate.
- **`#276` implicit `PartEvents` sort-order assumption.** `IsInertPartEventForTailTrim` and `FindLastInterestingUT` walk `PartEvents` from the tail backward and break at the first non-inert event. Correct only if `PartEvents` is sorted by UT — `StopRecording()` does sort it, but this is an implicit invariant. Either add a debug-only `Assert.That(IsSorted())` precondition or sort defensively in the helpers.
- **`#279` `LoadSlots` duplicate-counter bookkeeping.** Counter is incremented AFTER the dictionary write, so a duplicate owner silently overwrites the previous slot before the diagnostic counter sees it. Minor: bookkeeping is incorrect even though the runtime behavior (last-write-wins) is what existing code expected.
- **`#291` hand-rolled quaternion math duplication.** `RotateVectorByQuaternion`/`InverseRotateVectorByQuaternion`/`NormalizeQuaternion` exist purely for testability (Unity `Quaternion * Vector3` operator can't run in unit tests). Math is standard Rodrigues form. `DecomposeOrbitDirectionInTargetFrame` could share a helper with `CompensateCameraAngles` but it's not blocking.

**Status:** TODO. Cleanup pass, very low priority.

---

## 376. PR #265 follow-up: dual storage of `AutoAssignedStandaloneGroupName` and easy-to-miss clear paths

**Source:** light review of PRs `#256`-`#265` after `v0.8.0` ship.

**Concerns:** PR `#265` added auto-grouping of EVA tree recordings into a `Mission / Crew` subgroup and storage of an `AutoAssignedStandaloneGroupName` marker so later commits can adopt orphan rows back into the correct group.

1. **Dual storage redundancy.** The marker is stored both as a `Recording.AutoAssignedStandaloneGroupName` field (persisted in `.sfs` via `autoAssignedStandaloneGroup`) AND in a static `Dictionary<string, string> autoAssignedStandaloneGroupsByRecordingId` keyed by `RecordingId`. The dict is populated lazily from the field by `TryGetAutoAssignedStandaloneGroup`. Apparent reason: field-less test paths still need the lookup. Worth either (a) collapsing to one source of truth, or (b) documenting the invariant explicitly in a comment so future readers don't get the two out of sync.
2. **Easy-to-forget `ClearAutoAssignedStandaloneGroup` on new group-mutation paths.** Every existing group-mutation entry point (`AddRecordingToGroup`, `RemoveRecordingFromGroup`, `AddChainToGroup`, `RemoveChainFromGroup`, `RenameGroup`, `RemoveGroupFromAll`, `ReplaceGroupOnAll`) now calls `ClearAutoAssignedStandaloneGroup`. Any new group-mutation entry point added later must also call it, or a manual edit will silently get re-adopted on the next tree commit. Worth adding a centralized helper (e.g. `ApplyGroupMutation(rec, action)`) instead of relying on per-call discipline.
3. **"Pull whole chain into group block" rule** in `RecordingsTableUI.BuildGroupDisplayBlocks`: when one row of a chain is in a group, the entire chain is added to the group block via `AddDisplayBlockMembers(members, fullChain)`. `directMembers` gates which rows enter the group, so non-group chain siblings only show up for display ordering. Internally consistent but slightly surprising in mixed chain+group corpora; deserves a focused playtest with a chain that spans group/non-group boundaries.

**Status:** TODO. None of these are bugs today. Roll into the next opportunistic cleanup of `RecordingStore` group plumbing.

---

## 375. PR #258 follow-up: chatty `TrackGhostAppearance` Info-level logs and `allowActivation` threading surface

**Source:** light review of PRs `#256`-`#265` after `v0.8.0` ship.

**Concerns:** PR `#258` ("Fix debris ghost initial replay frame") gates ghost activation on `ResolveGhostActivationStartUT` (first playable payload UT) instead of `traj.StartUT`, and adds a `ghost_visuals` sub-container so debris CoM offset can be applied at the visual root without shifting the playback root transform. Two follow-up items:

1. **`TrackGhostAppearance` logs at `ParsekLog.Info` on every first-visible-frame** for every ghost, recording root + part + renderer-bounds + playback-point + snapshot-frame positions. This is deliberate observability the author added to confirm the fix works in the wild, but with many debris recordings active, normal gameplay will produce a high-volume `Info`-level log stream. Demote to `Verbose` (or `VerboseRateLimited` with a per-recording key) once the fix is field-validated.
2. **`allowActivation` parameter is now threaded through ~8 positioner methods** plus a new `deferVisibilityUntilPlaybackSync` state and `ShouldAutoActivateGhost` helper. The plumbing is correct but creates a new "every positioning path must remember to opt in" maintenance contract. Worth either documenting the contract in a comment near `ActivateGhostVisualsIfNeeded`, or considering an inversion (default = activate, explicit `defer = true` only for the loop/sync paths that actually need to defer).
3. **Self-described as needing runtime validation.** The PR merged before in-game verification on a representative debris-heavy scenario.

**Status:** TODO. The behavioral change is sound; this is observability cleanup + contract documentation + the runtime validation step.

---

## ~~374. PR #263 follow-up: `KerbalAssignmentActionsMatch` UT comparator inconsistent literal types~~

**Source:** light review of PRs `#256`-`#265` after `v0.8.0` ship.

**Concern:** `KerbalAssignmentActionsMatch` compares `StartUT`/`EndUT` with `Math.Abs(a.UT - b.UT) > 0.1` mixing `double`-typed difference with what visually look like `float` literals (the codebase comparator elsewhere uses `0.1f` for similar tolerances). Both compile to a `double` constant after promotion, so semantically identical, but the inconsistency is a code-style smell that suggests the author may have meant a tighter or looser tolerance. Worth a quick confirmation that `0.1` seconds is the intended tolerance for "same kerbal assignment row", not an artifact of float/double conversion.

`FindTraitForKerbal` is also worth a glance: it walks `GameStateStore.Baselines` from the end on every call, O(`baselines * crew`). Fine for current baseline counts (few per session) but if anything ever adds baselines on a per-recording or per-vessel cadence it becomes hot. Note for the kerbal subsystem watch list.

**Status:** ~~No-op — premise incorrect.~~

**Fix:** Investigated and confirmed no change is needed. The literals are already type-matched to the field types — `GameAction.UT` is `double` (compared with `0.1`), `GameAction.StartUT` and `GameAction.EndUT` are `float` (compared with `0.1f`). The "inconsistency" is the bug entry author mistakenly assuming all three fields shared one type. The sibling comparator `FindMatchingExistingAction` in the same file (`LedgerOrchestrator.cs`) uses the identical literal-style-to-field-type mapping at lines `775` / `777` / `779`, so the pattern is the established convention for this file. Standardizing the literal would actively break that convention, not fix it. The `FindTraitForKerbal` hot-path note remains tracked elsewhere as a watch-list item, not a fix target.

---

## ~~373. PR #262 follow-up: `ResolveImmediateLandedGhostClearanceMeters` silently regresses to legacy floor when active vessel is null~~

**Source:** light review of PRs `#256`-`#265` after `v0.8.0` ship.

**Concern:** PR `#262` extended `ApplyLandedGhostClearance` with a `minClearanceMeters` parameter so past-end / surface-hold paths use the same distance-aware floor as `LateUpdate`. The `ResolveImmediateLandedGhostClearanceMeters` helper falls back to a hardcoded `0.5` when `body == null` or `FlightGlobals.ActiveVessel == null`. That `0.5` matches the legacy pre-fix value, so when the fallback fires we silently regress to the old behavior. In practice no active vessel typically means no watcher and no visible ghost, so the regression is invisible — but if any non-watch code path positions a landed ghost while `ActiveVessel` is null (cold-start, scene transition), the long-range terrain-sinking symptom that `#262` was meant to fix could come back unnoticed.

Separately, `PositionAtSurface` updates `lastInterpolatedBodyName` / `lastInterpolatedAltitude` inside the new clearance branch but NOT in the non-clearance branch (where `TerrainHeightAtEnd` is `NaN`). Pre-fix code didn't set them at all, so this is a net improvement, but the asymmetry is a smell — the two branches should either both update the cache or both leave it untouched.

**Status:** ~~Fixed~~.

**Fix:** (1) Replaced the silent `return 0.5;` fallback in `ResolveImmediateLandedGhostClearanceMeters` with an explicit `ParsekLog.WarnRateLimited("TerrainCorrect", ...)` naming the ghost index, vessel name, body, and the fallback reason (`no-body`, `no-active-vessel`, or `no-body-and-no-active-vessel`), rate-limited per ghost+reason so scene-transition / cold-start paths surface without spamming. The literal `0.5` is now pinned as `ImmediateLandedGhostClearanceFallbackMeters` and the `(hasBody, hasActiveVessel)` decision is extracted into `ResolveImmediateLandedGhostClearanceFallbackReason` (pure `internal static`) with unit tests in `TerrainCorrectorTests` covering all four input combinations. Call sites in `PositionAtPoint` and `PositionAtSurface` were updated to pass `index` / `traj.VesselName` through so the warning can name the offending ghost. (2) The second claim (`PositionAtSurface` cache asymmetry) was stale at review time: the follow-up commit `e54f9a8c` that added the `ShouldApplyImmediateSurfacePositionClearance` guard already placed the `state.lastInterpolatedBodyName` / `state.lastInterpolatedAltitude` updates OUTSIDE the `if` block, so both the clearance and non-clearance branches in current HEAD already update the cache unconditionally. Consumers (`WatchModeController` camera/atmo/reentry-FX, `DriveReentryToZero`, `GhostPlaybackEngine.UpdateReentryFx`) rely on that cache being fresh after every surface position, so the symmetric "both update" behavior is the correct one and no code change is needed for this sub-item. Documented the stale premise here to avoid re-opening the review comment.

---

## ~~371. PR #248 follow-up: round-trip test for `NormalizeContinuousEvaBoundaryMerge` output~~

**Source:** light review of PRs `#246`-`#255` after `v0.8.0` ship.

**Concern:** `#248` ("Fix continuous EVA atmo-surface optimizer boundaries") added `NormalizeContinuousEvaBoundaryMerge` to `RecordingOptimizer`, which calls `RecordingStore.TrySyncFlatTrajectoryFromTrackSections(target, allowRelativeSections: true)` after re-merging EVA atmo/surface neighbors. `TrimOverlappingSectionFrames` is the new helper that drops duplicate boundary frames using `<= previousEndUT`.

The merge logic looks correct in isolation, but it runs over the same `TrackSections` payload that `#230` made authoritative on disk for `RecordingFormatVersion >= 1`. There is no explicit test that:

1. Saves a recording in section-authoritative mode (binary `v2`/`v3`),
2. Loads it,
3. Triggers `NormalizeContinuousEvaBoundaryMerge` on the loaded recording,
4. Re-saves and re-loads,
5. Asserts the post-merge `Points` / `OrbitSegments` are byte-identical to the in-memory state on the second load.

`TrimOverlappingSectionFrames` is gated to absolute + relative frames (correct for the `allowRelativeSections` flag), but a continuous-EVA recording that crosses an orbital-checkpoint boundary would skip the trim — confirm whether that combination is even possible (EVA on a body with orbital frame? unlikely but worth a unit test that documents the assumption).

**Fix:** added `EvaBoundaryMergeRoundTripTests.MergeInto_ContinuousEvaBoundary_V3Roundtrip_PointsAndOrbitSegmentsStable` — builds two EVA-tagged section-authoritative recordings (atmo + surface, continuous Kerbin boundary), saves both as v3 binary sidecars, loads them, runs `RecordingOptimizer.MergeInto` which triggers `NormalizeContinuousEvaBoundaryMerge` → `TrimOverlappingSectionFrames` → `TrySyncFlatTrajectoryFromTrackSections`, deep-copies the post-merge `Points` / `OrbitSegments`, then re-saves and re-loads the merged recording and asserts the second-load trajectory matches the captured snapshot field-by-field. Companion test `CanMergeContinuousEvaAtmoSurfaceBoundary_OrbitalPhasePair_IsNotAllowed` pins the invariant that `CanMergeContinuousEvaAtmoSurfaceBoundary` rejects orbital phase pairs (`SegmentPhase == "exo"`), so the normalizer cannot observe an `OrbitalCheckpoint` section via this code path — documents the todo question about EVA + orbital-checkpoint combinations.

**Status:** ~~Fixed~~

---

## ~~370. PR #246 follow-up: latent NRE in group watch button log line~~

**Source:** light review of PRs `#246`-`#255` after `v0.8.0` ship.

**Concern:** `#246` added `ResolveEffectiveWatchTargetIndex` so group "W" buttons follow auto-follow handoffs. The button click log line dereferences `committed[resolvedWatchIdx].VesselName` to print which segment was watched. That index can be `-1` if `ResolveEffectiveWatchTargetIndex` returns no match, which would NRE.

**Why it doesn't bite today:** `GUI.enabled = canWatch` guards the button, and `canWatch` implies `hasGhost` which implies `resolvedWatchIdx >= 0` upstream. The chain is correct but fragile — if a future refactor reorders the `canWatch`/`resolvedWatchIdx` computation, the log line becomes the regression.

**What to do (later):** defensive `resolvedWatchIdx >= 0 ? committed[resolvedWatchIdx].VesselName : "<none>"` in the log line. Trivial.

**Fix:** Both group-W log sites in `Source/Parsek/UI/RecordingsTableUI.cs` (the enable/disable transition log at ~line 1393 and the click-time log at ~line 1412) now guard `committed[resolvedWatchIdx].VesselName` with `resolvedWatchIdx >= 0 && resolvedWatchIdx < committed.Count ? ... : "<none>"`, matching the pre-existing pattern at the `resolvedTargetId` computation one scope above. `GUI.enabled = canWatch` still blocks the click path in practice; this hardens the log lines so a future refactor of the `canWatch`/`resolvedWatchIdx` ordering can't turn either into an `IndexOutOfRangeException`.

**Status:** ~~Fixed~~.

---

## 369. Post-0.8.0 review minor smells and edge cases

**Source:** light review of PRs `#236`-`#245` after `v0.8.0` ship. Collected here so they don't disappear; none are blocking.

- **`#238` `ComputePendingWatchHoldSeconds` NaN warp rate.** Guards `warpRate <= 0.01f` but not `float.NaN`. If `CurrentWarpRateNow` ever returns `NaN`, `Mathf.Ceil((... / NaN) + grace)` is `NaN`, and `Mathf.Clamp` falls through to base. Not a correctness bug today but easy to harden.
- **`#239` `PopulateCrewEndStates` EVA edge case.** Branch `GhostVisualSnapshot != null || !string.IsNullOrEmpty(EvaCrewName)` is followed by `ExtractCrewFromSnapshot(rec.GhostVisualSnapshot)` which only reads the ghost snapshot. An EVA recording with non-empty `EvaCrewName` and null ghost snapshot now takes the "empty crew" path and is marked resolved. Confirm EVA crew-end-state population is handled elsewhere and this branch is intentional.
- **`#241` cosmetic `return true; return true;`.** `RenderInRangeGhost` ends with two consecutive `return true;` statements, second is unreachable. Trivial cleanup.

**Status:** TODO. Roll into the next opportunistic cleanup pass; not worth a dedicated PR.

---

## 368. PR #240 follow-up: pad-failure heuristic falls back to Kerbin radius on unknown modded bodies

**Source:** light review of PRs `#236`-`#245` after `v0.8.0` ship.

**Concern:** `#240` introduced `TryGetPadLocalizedMotionMetrics` to detect pad-drop / fall-over launches as pad-local even when raw 3D max distance is inflated by vertical collapse. The helper computes surface range using a body radius lookup table for stock bodies (`Kerbol`, `Moho`, `Eve`, `Gilly`, `Kerbin`, `Mun`, `Minmus`, `Duna`, `Ike`, `Dres`, `Jool`, `Laythe`, `Vall`, `Tylo`, `Bop`, `Pol`, `Eeloo`), with `FlightGlobals.Bodies` lookup tried first as the runtime path.

The fallback when the body name is not in the table is **Kerbin's 600 km radius**. On a much smaller body (a modded Gilly-equivalent or a small asteroid moon), that over-estimates radius → under-estimates angular distance → surface range looks small → the heuristic could wrongly classify a real launch as "pad-local" and suppress the merge dialog. On a much larger modded body the opposite happens (pad-locality undetected, normal merge dialog flow).

**Why it likely doesn't bite in practice:** most modded planet packs (RSS, JNSQ, OPM, Beyond Home) register their bodies in `FlightGlobals.Bodies` before any flight scene loads, so the runtime lookup catches them. The fallback only kicks in if `FlightGlobals.Bodies` lookup fails for a body that exists in a recording — an unusual mid-game removal of a planet pack.

**What to do:** either log a warning when the table fallback triggers (so the user sees it once and reports the body name), or replace the table fallback with a sentinel that disables the heuristic entirely on unknown bodies (return `+∞` like the multi-body transition guard does).

**Status:** TODO. Low priority — only triggers on planet-pack uninstall mid-save.

---

## 367. PR #242 follow-up: in-game save/load breakup repro still needed

**Source:** PR `#242` body explicitly listed this as a follow-up the author did not run before merge.

**Context:** `#242` checkpoints the recorder's currently-open `TrackSection` during active-tree save serialization, then immediately reopens a continuation section with the same environment / reference frame / source / anchor metadata. Unit tests cover absolute, relative, and orbital-checkpoint checkpointing. The PR was driven by bug `#327` evidence (logs/2026-04-13 s13 bundle showed `103` live points but only `35` in the sidecar after save).

**What still needs to happen:** an in-game breakup save/load repro on current head to confirm the visible "frozen / off-trajectory after quickload" symptom is gone. The unit tests pin the helper behavior and serialization output but cannot prove the original gameplay symptom is fixed end-to-end.

**Suggested repro:** Kerbal X (or any multi-stage rocket), launch, stage to breakup, `F5` quicksave during the breakup, `F9` quickload, watch the resumed playback for the original main-stage continuation. Compare against the same bundle's symptom shape.

**Status:** TODO. Validation step, no code change expected unless the repro fails.

---

## 366. PR #236 follow-up: staged sidecar rollback can leave split state if restore step itself throws

**Source:** light review of PRs `#236`-`#245` after `v0.8.0` ship.

**Concern:** `#236` ("Phase 11.5: compress snapshot sidecars losslessly") introduced a staged-commit / rollback pattern in `RecordingStore` so multi-file sidecar writes can be rolled back atomically: each write goes to `.stage.<guid>`, then `ApplyStagedSidecarChanges` commits via `File.Replace` / `File.Move` with `.bak.<guid>` backups, and `CleanupStagedSidecarArtifacts` reverses on exception.

The reverse-order rollback path (`RestoreCommittedSidecarChange` and friends) does not wrap individual restore steps in their own `try`/`catch`. If restoration itself throws midway through (for example, the `.bak` file was deleted by an external process or the disk filled mid-restore), the remaining earlier commits stay in their committed state and the outer `catch` in `SaveRecordingFilesToPathsInternal` rethrows / logs and returns `false`. That leaves the sidecar set in a partially-rolled-back state.

**Why it matters:** the outer rollback also restores `rec.SidecarEpoch` and `rec.GhostSnapshotMode` to their pre-save values, so the in-memory recording correctly re-flags itself dirty for the next save. The risk is concrete only if the partially-committed `.prec` or `_vessel.craft` survives long enough for a load before the next save runs and overwrites it — a narrow window in normal use, much wider on a crash/Alt+F4 right after a save failure.

**Why it didn't show up in review of `#230`:** I initially attributed the staged-commit pattern to `#252` readable mirrors; the `#236`-`#245` light pass found that `#236` is actually where it landed.

**Suggested fix (later):** wrap each `RestoreCommittedSidecarChange` call in its own `try`/`catch`, log the per-step failure, and continue rolling back the remaining changes. Atomicity is best-effort already (multiple files, no transaction across them), so the goal is "minimize remaining inconsistency" rather than perfect rollback.

**Status:** TODO. Low priority — failure mode requires both a save exception and a rollback exception in the same call.

---

## 365. PR #230 follow-up: focused review of optimizer/merger flat-cache resync and epoch hydration

**Source:** light review of PRs `#226`-`#235` after `v0.8.0` ship.

**Concern:** `#230` ("Phase 11.5 recording storage optimization") bundles an on-disk format change (`TrackSections` authoritative, compact binary `v2` / sparse `v3` sidecars, snapshot alias mode) together with several runtime bug fixes discovered during playtest: optimizer boring-tail nested trims, merge metadata carry-over, section-authoritative timing bounds, stale-sidecar epoch hydration, zero-throttle debris engine playback, watch handoff bounds. Large scope + interlocking semantics in `RecordingOptimizer` / `SessionMerger` / `RecordingStore` is exactly the area that historically produces ghost-lookup and `R`/`FF` regressions.

**What to look at:**

- `RecordingOptimizer` merge metadata transfer: does it preserve branch/end-state metadata and tree ownership across nested boring-tail trims and re-merges? Any asymmetry between "trim then merge" vs "merge then trim"?
- `SessionMerger` + `TrackSections` flat-cache resync: is the flat trajectory cache rebuilt from sections after every merge path, and is it idempotent under repeated save/load?
- Epoch hydration safety: confirm `SaveRecordingFiles` in-memory epoch rollback path is correct on every early-return and exception branch, not just the happy path the PR description describes.
- Mixed-format round-trip coverage: which combinations of (`flat only` / `sections only` / `flat+sections duplicated prefix` / `v2` / `v3` / alias-mode snapshot) have round-trip tests? Any format that only goes one way?
- Zero-throttle debris engine playback: the fix is part of the storage PR but is actually runtime state. Check that the gate is not accidentally coupled to sidecar format version.

**Evidence to collect:** long-tree save/load playtest (branching + quickload-resume + breakup + EVA) on the current head; diff the sidecars before/after load to confirm idempotency; `dotnet test` focused on `RecordingStore`, `SessionMerger`, `RecordingOptimizer`, `Bug270SidecarEpochTests`, `Bug278SnapshotPersistenceTests`, mixed-format round-trips.

**Status:** TODO. Not blocking — PR is merged and playtested — but the area is still unstable (author shipped with 10+ open follow-ups `#315`-`#326`).

**First-pass review note (`docs/dev/research/pr-230-detailed-review.md`):** no visible bugs in `RecordingOptimizer`, `SessionMerger`, `RecordingStore`, `Recording.cs`, or the new `TrajectorySidecarBinary` header/probe. Found that the PR also fixes four real latent bugs in the optimizer/merger that are not called out in the changelog (`MergeInto` dropping terminal-orbit / `ChildBranchPointId` metadata, `SplitAtSection` leaking stale `CachedStats` to the new half, `TrimBoringTail` lying about trims by setting `endUT` without pruning frames, tree-state not synced when optimizer merged recordings).

**Second-pass items (open — some may already be addressed by later PRs, check before working):**

- `TrajectorySidecarBinary` reader bounds and sparse `v3` point encoding: verify reads respect stream length, handle truncated files gracefully, and that the sparse-flag combinations round-trip losslessly (especially "body default + funds override" mixed cases).
- `BackgroundRecorder` flat-cache maintenance: does the background path produce the same section/flat shape the optimizer expects? PR `#230` lists "background recorder + optimizer consistency" as in-scope; confirm by reading `BackgroundRecorder.cs` against `RecordingOptimizer.TrimBoringTail` / `MergeInto`.
- `PartStateSeeder` interaction with trimmed track sections (used for debris start-pose seeding per `#264` and friends — likely already addressed downstream).
- Round-trip coverage matrix in `RecordingStorageRoundTripTests`: which of (`v0` flat / `v1` flat+sections duplicated / `v1` section-authoritative / `v2` / `v3` / alias-mode snapshot) pairs have explicit equality assertions? Any format that only goes one way?
- Zero-throttle debris engine playback path: find the gate in the diff and verify it is not coupled to sidecar format version.
- ~~Verify staged-write rollback was part of `#230` or added later.~~ **Resolved during #236-#245 review pass:** the staged-commit / `.stage.<guid>` / `.bak.<guid>` rollback pattern was added by `#236` (Phase 11.5 snapshot compression). `#230` itself shipped with a single-pass write sequence; if a save failed after epoch bump and before all sidecars were written, the in-memory `SidecarEpoch` was off by one until `#236` landed. The window was small (one PR) but real.
- `StartUT`/`EndUT` getters now do a three-source scan plus linear payload search per call. Hot-path sample to confirm no allocation-free CPU regression, especially in the playback engine.
- `HasCompleteTrackSectionPayloadForFlatSync` returns `false` on any single empty section. If "freshly opened continuation section with no frames yet" is a normal shape for branch tips, those will quietly write flat-only on disk even when `RecordingFormatVersion >= 1`. Grep verbose logs for `used flat fallback path` on recordings that should be section-authoritative.
- `TrajectoryPointEquals` / `OrbitSegmentEquals` use exact float equality. Safe today (lossless `"R"` text + exact binary), but a future lossy codec would silently break boundary dedupe. Worth a unit test that round-trips through every codec and asserts equality on boundary pairs.

**Pre-flight before acting on these:** check `git log -p --since="2026-04-12"` for the relevant files; #230 is the start of a long Phase 11.5 follow-up tail and several items above may already be resolved.

**Resolutions found during the #266-#292 review pass:**

- **Mixed-format round-trip coverage (partial):** PR `#266` added the `FlatTrajectoryExactlyMatchesTrackSectionPayload` exact-match guard in `ShouldWriteSectionAuthoritativeTrajectory` so any drift between flat and section payloads forces flat fallback on save. PR `#270` added `FlatTrajectoryExtendsTrackSectionPayload` to preserve flat tails when they extend sections. PR `#285` rewrote `AppendPointsFromTrackSections` / `AppendOrbitSegmentsFromTrackSections` to use full suffix-prefix overlap dedupe instead of single-boundary matching, and tightened `FlatTrajectoryExtendsTrackSectionPayload` to reject non-monotonic flat tails. PR `#289` added `TryHealMalformedFlatFallbackTrajectoryFromTrackSections` that detects loaded recordings where flat duplicates a section prefix and rebuilds the safe monotonic suffix. The combined effect is that the format is now self-healing on both write and load, with strict invariants on what "flat extends sections" means. The original concern was mostly addressed by these four PRs; the remaining open item is targeted unit coverage that pins each codec pair (`v0`/`v1`/`v2`/`v3` × text/binary) explicitly.
- **`BackgroundRecorder` flat-cache consistency:** PR `#266` centralized min/max altitude tracking in `AddFrameToActiveTrackSection` (previously only in tests), and PR `#285` added the `PruneSinglePointDestroyedDebrisLeaves` path with a `SidecarLoadFailed` guard so loaded-but-failed recordings cannot be mistaken for empty leaves and pruned. The remaining open item is the single end-to-end repro that confirms background-then-foreground transitions produce the same flat shape the optimizer expects.
- **Optimizer aggressive trim safety:** PR `#276` added `IsInertPartEventForTailTrim` so zero-throttle engine/RCS events are treated as inert and don't extend tails. PR `#287` added a terminal-state-shape match before `TrimBoringTail` actually shortens `EndUT`. Both are conservative tightenings consistent with `#230`'s direction. No regressions found, but the implicit assumption that `PartEvents` is sorted by UT (made by `IsInertPartEventForTailTrim` and `FindLastInterestingUT`) deserves either a debug-only assertion or a sort precondition documented at the helper.

**Net status:** the Phase 11.5 storage area received roughly 6 follow-up PRs after `#230` between 2026-04-12 and 2026-04-14. The format is in much better shape than after `#230` alone; the open second-pass items above are now mostly observability + targeted unit coverage rather than correctness concerns.

---

## 364. PR #229 follow-up: T7 hidden-tier unload/re-entry one-shot FX and prewarm timing

**Source:** light review of PRs `#226`-`#235` after `v0.8.0` ship.

**Concern:** `#229` unloads hidden-tier ghost visuals while keeping logical playback shell state alive, then rebuilds visuals on visible-tier re-entry without replaying transient puff/one-shot effects. Two risk areas to confirm in playtest:

1. **Shell-state resurrection of part-event bookkeeping.** On re-entry the shell must already reflect every one-shot event that fired during the hidden interval (`LightOn`, `ParachuteDeployed`, `ShroudJettisoned`, `FairingJettisoned`, `GearDeployed`, `CargoBayOpened`, `RCSActivated`, `EngineIgnited`, `DeployableExtended`). If replay rewinds state on rebuild and re-fires these, the visible tier pops.
2. **Prewarm timing off-by-one near structural events.** If the tier transition window does not include a short prewarm before decouple / jettison / breakup, the first visible frame after re-entry can show a stale shell that still owns parts the logical playback has already split off.

**What to look at:** `GhostPlaybackEngine` tier-transition callback, `GhostPlaybackState` shell restore path, and the part-event replay filter used during `RebuildVisualsForVisibleTier` (or equivalent). Confirm the filter distinguishes "transient puff already fired" from "latched state that must be re-applied".

**Evidence to collect:** playtest near staging / parachute deploy / fairing jettison with many ghosts active so tier transitions actually happen; watch for one-shot FX re-fires and single-frame "stale shell" pops at the tier boundary.

**Status:** TODO. Not blocking — fix design matches the visual-efficiency principle — but the two failure modes above are plausible enough to warrant a targeted playtest before the next release.

---

## 363. PR #232 follow-up: WatchModeController refactor surface-area watch

**Source:** light review of PRs `#226`-`#235` after `v0.8.0` ship.

**Concern:** `#232` ("Fix late watched debris visibility after watch exit") describes a targeted fix, but the diff is actually a substantial refactor of `WatchModeController` (~150 lines plus a 264-line `Issue316WatchProtectionTests` file). The state split between "exact watched state" and "post-watch debris retention" now threads through hold-expiry, high-warp, cutoff, policy, and target-loss paths — all of which have their own recent regressions. Logic looks coherent in review but the surface area is large enough that flagged for a second pass.

**What to look at:**

- High-warp exit path: does post-watch debris retention correctly release when warp drops back to `1x`, or can it leak across scene changes?
- Interaction with `#235` breakup watch fallback: retention state and the breakup selector are both gated on "watched lineage", confirm there is no double-booking.
- Auto-exit vs user-initiated exit parity: both paths should clear retention once the last descendant debris finishes.
- Zone rendering interaction: `ZoneRenderingTests` gained `+216` lines — confirm the zone manager respects retention flags consistently in all camera/policy states.

**Evidence to collect:** playtest of crash at high warp with multiple debris children, compare against normal-warp crash; long debris tail (large booster breakup) to confirm retention releases at the right moment.

**Status:** TODO. Low priority — flagged only for surface area, no visible bug.

---

## ~~362. Terminal crash-end decouple fragments can still collapse to `WithinSegment` and skip a final debris branch~~

**Observed in:** `logs/2026-04-14_1954_kerbal-x-f5f9-fix-verify` (2026-04-14) while re-validating the `Kerbal X` mid-flight `F5/F9` fix. Near the very end of the resumed run, the active vessel was already being destroyed but `onPartDeCouple` still reported two late fragments (`parachuteLarge`, `HeatShield2`). The deferred split pass then classified that event as `WithinSegment newVessels=0` instead of creating one more debris branch.

**Collected evidence:**

- `KSP.log:12539` / `KSP.log:12541` log the late decouples for `parachuteLarge` and `HeatShield2`.
- `KSP.log:12545` marks the active vessel destroyed during recording.
- `KSP.log:12581` shows `2 vessel(s) caught by onPartDeCouple`.
- `KSP.log:12582` / `KSP.log:12583` then classify the event as `WithinSegment` with `newVessels=0`.
- `KSP.log:12586` immediately commits instead of resuming because the parent vessel is already destroyed.
- Earlier in the same resumed run, the actual `F5/F9` regression is gone: resumed breakup events produce new `DebrisSplit` branches and the final saved tree reaches `8 recordings, 5 branchPoints` at `KSP.log:12968`.

**Impact:** low-priority follow-up. The repaired quickload-resume path is working and the missing post-`F9` debris branches are back, but the very last crash-end shrapnel can still be dropped if it only exists transiently during terminal destruction.

**Root cause:** likely a timing edge in the deferred split classifier. `onPartDeCouple` captures live `Vessel` references, but by the time the deferred pass evaluates them after the parent vessel is already dying, those late fragments may no longer survive the viability filters as real new vessels. `ResumeSplitRecorder(...)` then correctly commits because the parent vessel is already destroyed.

**Desired policy:** decide explicitly whether terminal crash-end transient fragments should ever become recorded debris branches. If yes, the deferred split path likely needs a terminal-safe capture rule for those last fragments. If no, this should stay documented as intentional and non-blocking.

**Fix:** `DeferredJointBreakCheck` no longer iterates `decoupleCreatedVessels` (a `List<Vessel>`) when collecting new-vessel PIDs from the synchronous `onPartDeCoupleNewVesselComplete` capture. At terminal crash time, KSP has already destroyed the fragment `GameObject`s, so Unity's overloaded `UnityEngine.Object ==` makes `v == null` true for every fragment and the filter drops them all, leaving `newVesselPids.Count == 0` and collapsing the classification to `WithinSegment`. The deferred check now iterates the PID-keyed `decoupleControllerStatus` dictionary (which is populated in the same synchronous callback and survives terminal destruction because its keys are plain managed `uint`s), routing the fragments through `SegmentBoundaryLogic.ClassifyJointBreakResult` as a real `DebrisSplit`. The rest of the pipeline is already null-safe for destroyed debris: `FindVesselByPid` returns null, `preSnapshot` stays null, `preTrajectoryPoint` falls back to the synchronously-captured `decoupleCreatedTrajectoryPoints[pid]`, and `CreateBreakupChildRecording`'s destroyed-vessel branch produces a `TerminalState.Destroyed` debris leaf stamped at the split UT. Extracted the PID-collection filter to `SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids` with unit coverage in `Bug362TerminalCrashDebrisTests`. Also added a per-fragment Verbose log so future terminal-crash repros show the PID-only classification breadcrumb in KSP.log.

**Status:** ~~Fixed~~

---

## ~~361. Loaded committed recordings can keep a duplicated flat-prefix tail and fail monotonicity checks~~

**Observed in:** `logs/2026-04-14_1836_pr285-ingame-batches` (2026-04-14). Both the KSC-view and FLIGHT-mode in-game batches failed `RuntimeTests.CommittedRecordingsHaveValidData` on committed recording `393b82ccb697492bb7b35c6c621f9d07` (`Learstar A1 Debris`) because the loaded point list jumped backward from `170.92` to `155.84`.

**Root cause:** the committed sidecar on disk was already malformed in a specific flat-fallback shape: top-level `Points` duplicated the exact trajectory payload already represented by nested `TrackSections`, then appended a later monotonic tail. `DeserializeTrajectoryFrom(...)` accepted that stale flat fallback as-is whenever section-authoritative rebuild was unavailable, so runtime validation could still see a non-monotonic committed recording even after the earlier overlap-aware merge fixes.

**Fix:** flat-fallback load now detects the "duplicated track-section prefix plus later monotonic suffix" shape, rebuilds the authoritative prefix from `TrackSections`, appends only the safe monotonic suffix, and marks the recording dirty so the next save rewrites the sidecar cleanly. Added a regression test that serializes/deserializes that malformed v1 payload and pins the healed result.

**Status:** ~~Fixed~~

---

## ~~360. First ghost watch entry can flip the camera to the opposite side of the launch pad (and V toggles drift it)~~

**Observed in:** user repro on 2026-04-14 while watching a launch-pad vessel from the anchor camera. From the right side of the pad (`water on the right`), pressing `Watch Ghost` could spawn the watch camera on the opposite side (`water on the left`) even though the vessel/body context had not changed. Follow-up repro on the same day: after the PR #288 transfer fix shipped, the wrong-side framing remained and pressing `V` to toggle `Free` / `HorizonLocked` made the camera "jump all over the place" instead of staying on the same side of the ghost. Log bundle `logs/2026-04-14_2027_watch-camera-side-investigate/` captures a fresh-entry-source line where the active-vessel pivot Z (pointing vertically up) resolved into the ghost's horizon basis as `pitch=-11.3°, hdg=107.5°, localOrbit=(0.9,-0.2,-0.3)`.

**Root cause:** the PR #288 fix tried to preserve the active vessel's `FlightCamera` basis on fresh entry by snapshotting pitch/heading/world-orbit direction and decomposing it into the ghost's initial target rotation. That decomposition is meaningless in the fresh-entry case: an upright on-pad rocket's pivot forward axis is vertical, while the ghost's horizon proxy forward axis is the ghost's current velocity direction. Projecting "world up" into a velocity-aligned basis produces a heading component aligned with the basis's right axis (cross of radial and velocity), so the camera lands wherever that right axis happens to point — usually the opposite side of where the player was looking. The `V` toggle symptom comes from a second problem: `RememberCurrentWatchCameraState` / `ToggleCameraMode` stored the captured world-orbit direction on every mode swap, but the horizon proxy rotates continuously as the ghost moves, so each replay of the stored world vector snapped the camera to wherever that fixed world direction now lay in the new proxy basis — the camera drifted around the ghost on every toggle.

**Fix:** fresh watch entry no longer transfers the active vessel's camera orientation at all. `PrepareFreshWatchCameraState` and the new `BuildCanonicalFreshWatchCameraState(state)` helper now emit a canonical framing — `pitch = DefaultWatchEntryPitch (12°)`, `heading = DefaultWatchEntryHeading (0°)`, `distance = DefaultWatchEntryDistance (50 m)`, auto-selected mode — with `HasTargetRotation`/`HasWorldOrbitDirection` cleared, so `CompensateTransferredWatchAngles` falls through to apply the raw angles relative to the resolved target transform (`horizonProxy` in atmosphere, `cameraPivot` in space). `W->W` switches and chain handoffs still use the existing world-direction transfer path because both sides share the ghost's basis there. Separately, `RememberCurrentWatchCameraState` and `ToggleCameraMode` now explicitly strip `HasTargetRotation` / `HasWorldOrbitDirection` before storing the remembered mode snapshot, so `V`-toggle restore applies raw `(pitch, hdg)` in the live target frame — which tracks the ghost — instead of replaying a stale world vector. Updated `PrepareFreshWatchCameraState_*` unit tests to pin the canonical values and added `CompensateTransferredWatchAngles_NoRotationOrWorldDirection_ReturnsRawAngles` to guard the raw-angle restore path.

**Follow-up (2026-04-14, same day):** the PR #293 first pass still put the camera above or below the vessel on fresh entry. `logs/2026-04-14_2051_watch-camera-init-angle/` shows `Watch camera apply (fresh-entry-apply): ... pitch=12.0 hdg=0.0 localOrbit=(0.0,0.2,1.0) resolvedWorldOrbit=(0.4,0.0,0.9)` — Parsek's internal degree-space math was correct, but when it wrote `FlightCamera.fetch.camPitch = 12f` the KSP camera went wild. Decompiling `FlightCamera` (line `pivot.rotation = frameOfReference * Quaternion.AngleAxis(camHdg * 57.29578f, Vector3.up) * Quaternion.AngleAxis(endPitch * 57.29578f, Vector3.right)`) confirmed KSP stores `camPitch` / `camHdg` in **radians** — the `* 57.29578f` converts radians→degrees for `AngleAxis`. Parsek's `OrbitDirectionFromAngles` / `CompensateCameraAngles` / `DecomposeOrbitDirectionInTargetFrame` all treat their angle arguments as **degrees** (they `Mathf.Deg2Rad` internally). Latent unit mismatch: capture and round-trip hid it because both sides treated the `FlightCamera.fetch.camPitch` value as an opaque float, but `12f` (degrees-intent) written as `camPitch` became `12 rad = 687°` = roughly `-33°` effective pitch, aiming the camera through the vessel. All thirteen `FlightCamera.fetch.camPitch` / `camHdg` accesses in `WatchModeController.cs` now convert at the KSP boundary: `* Mathf.Rad2Deg` on reads, `* Mathf.Deg2Rad` on writes, so Parsek's internal `WatchCameraTransitionState.Pitch` / `Heading` / the `savedCameraPitch` backspace-restore fields are uniformly in degrees.

**Review follow-up (2026-04-14, same day):** independent review flagged that the auto-mode switch in `UpdateHorizonProxy` (Free↔HorizonLocked on atmosphere crossings) still stored `previousModeState` without stripping `HasTargetRotation` / `HasWorldOrbitDirection`, so a watched ghost ping-ponging across the atmosphere boundary would hit the same world-orbit drift the V-toggle fix addressed. Extracted a `RememberWatchCameraStateAsTargetRelative(cameraState)` helper that encapsulates the strip-and-store pattern, and converted all three mode-snapshot sites (`RememberCurrentWatchCameraState`, `ToggleCameraMode`, `UpdateHorizonProxy` auto-mode switch) to use it. W->W switches and chain transfers still use the world-direction path via `RememberWatchCameraState` directly because those are one-shot immediate applies on the same frame with no drift window. Added `DefaultWatchEntryConstants_AreInDegreeConvention_ProduceExpectedOrbitDirection` unit test that pins the canonical fresh-entry constants to the degree convention via `OrbitDirectionFromAngles`, so a future regression that reinterprets `DefaultWatchEntryPitch` / `DefaultWatchEntryHeading` as radians (or drops a `Deg2Rad` at a boundary) would fail — plugs the rad/deg coverage gap the review also called out.

**Status:** ~~Fixed~~ in PR `#288` (initial transfer-based attempt), superseded by PR `#293` (canonical framing + `V`-toggle drift fix + KSP `camPitch`/`camHdg` radians-to-degrees unit fix + auto-mode drift symmetry fix).

---

## ~~359. Background section flushes can duplicate flat tails and leave one-point destroyed debris stubs after merge~~

**Observed in:** the same 2026-04-14 follow-up investigation that found the missing `Rewind` button. The runtime/in-game suite was failing `CommittedRecordingsHaveValidData` and `RecordingStopMetricsValid`, and merged quickload-resume trees could retain non-monotonic flat tails or single-point destroyed debris leaves that were never meant to survive commit.

**Root cause:** loaded background recordings could already hold data in both top-level `Points`/`OrbitSegments` and nested `TrackSections`. Later flushes appended the same section payload again using only a single-boundary-element dedupe, so multi-point overlaps produced duplicated / non-monotonic flat tails that survived session merge. Separately, the crash/continuation coalescer could leave one-point destroyed debris leaf recordings in the tree, which then polluted stop-metric expectations unless pruned.

**Fix:** background append/merge now performs suffix/prefix overlap matching and refuses to preserve flat extensions that are not monotonic past the authoritative section payload. Finalize/revert also prune single-point destroyed debris leaves while explicitly protecting the tree root and active recording IDs. The stale in-game runtime-log assertion was updated to the current logging contract.

**Status:** ~~Fixed~~

---

## ~~358. Quickload-resumed destroyed-end merged recordings can lose the Rewind button and rewind baseline~~

**Observed in:** local 2026-04-14 follow-up after the revert/finalize regression fix. A tree recording was quicksaved/quickloaded in flight, ended `Destroyed`, merged into the timeline, and the Recordings window showed no `R` button even though the mission should still rewind to the original launch save.

**Root cause:** quickload resume preserved `resumeRewindSave` on the live recorder, but committed trees resolve rewind through the root recording. `SaveActiveTreeIfAny()` initially failed to mirror that metadata to the root, and several other recorder-backed commit paths (vessel-switch/background/finalize) still hand-wired only the save filename without the reserved-budget / pre-launch fallback fields. A resumed tree could therefore keep the save hint but lose the rewind baseline once the active recorder changed or the tree finalized.

**Fix:** root rewind propagation is now centralized through `CopyRewindSaveToRoot(RecordingTree, FlightRecorder, ...)` and used by quickload save, split, vessel-switch/background, stash/finalize, and revert commit paths. The helper now copies the rewind save, reserved resource budget, and pre-launch baseline from `CaptureAtStop` when available or from the live recorder fallback fields otherwise. Added focused regression coverage for both the helper behavior and the critical call-site wiring.

**Status:** ~~Fixed~~

---

## ~~357. Deferred orbital spawn can switch the live active vessel without Parsek switching its recorder/tree~~

**Observed in:** `logs/2026-04-14_1624_orbital-spawn-bug` (2026-04-14). During watched playback of `Goliath II HLV`, KSP handed control to the newly spawned real vessel, but Parsek still believed the active recorder/tree belonged to the pad-launched `Jumping Flea`. That left a short desync window immediately after spawn where subsequent logic was running against the wrong active vessel.

**Root cause:** the deferred orbital-spawn path could change `FlightGlobals.ActiveVessel` without Parsek seeing either of the normal reconciliation paths in time: the expected `onVesselChange` callback was missed, and the physics-frame recorder path never noticed the mismatch before later tree logic ran. Parsek therefore kept the stale recorder PID until some later transition happened to correct it.

**Fix:** `ParsekFlight.Update()` now runs a narrow missed-switch recovery safety net before the other tree transition handlers. When tree state is stable and the active vessel PID provably diverges from Parsek's active recorder/tree state, it replays the normal `OnVesselSwitchComplete()` handoff. Added regression coverage for the pure guard and the required `Update()` ordering.

**Status:** ~~Fixed~~

---

## ~~356. Boring-tail trim can cut a recording before the true final spawn state is reached~~

**Observed in:** `logs/2026-04-14_1724_orbital-spawn-bug` (2026-04-14). After merging the `Goliath II HLV` recording, the optimizer shortened the committed exo leaf from `endUT=610217.8` down to `309083.3` even though the eventual spawned state later changed again before the real mission end. Playback therefore finished early and the later spawn no longer represented "the final-final state that never changes again until spawn."

**Root cause:** `RecordingOptimizer.TrimBoringTail()` only enforced `lastInterestingUT + bufferSeconds`; it never proved that the tail after that trim point already matched the true terminal spawn state. That made it legal to trim while the vessel was still coasting toward a different final orbit or still changing on the surface before its real terminal rest state.

**Fix:** boring-tail trim now requires the post-trim tail to preserve the exact terminal spawn state. Orbiting/docked/suborbital recordings only trim when every remaining orbit segment/checkpoint is an exact match for the final terminal orbit shape, and landed/splashed recordings only trim when all remaining tail points exactly match the final terminal surface state. Added stable-vs-changing orbit, suborbital, and landed regressions.

**Status:** ~~Fixed~~

---

## ~~355. Flight anchor-camera ghost can miss engine plumes until watch mode~~

**Observed in:** `logs/2026-04-14_1354_flight-ghost-engine-off` (2026-04-14). In Flight, the primary `Kerbal X` ghost looked like its engines were off from the normal anchor / in-flight camera view at liftoff even though `Watch Ghost` immediately showed the same ghost with engine plumes, and KSC playback also showed the plumes correctly.

**Root cause:** the April 13 deferred-activation changes hid fresh ghosts until playback sync, but the Flight anchor-camera path was still consuming engine/RCS runtime state while that ghost was deferred/inactive. By the time the ghost became visible, the engine start/throttle events had already been applied and the one-shot runtime plume state was gone, so the first visible frame showed the mesh without the running plume FX. `Watch Ghost` and KSC playback did not regress because they replayed or applied the same runtime state while the ghost was already active.

**Fix:** ghost playback now tracks current engine/RCS/audio power while applying runtime events and restores that deferred runtime FX state on the first active frame after deferred sync, but only when visual FX are not suppressed. The fix also clears tracked power on stop/decouple/destroy paths so stale runtime FX state cannot replay later. Focused regression coverage now pins the tracked-power collection/clearing helpers and the first-activation restore gate.

**Status:** ~~Fixed~~ in PR `#281`

---

## ~~354. Orbital end-of-playback spawns can use the wrong vessel snapshot even when the orbit is correct~~

**Observed in:** `logs/2026-04-14_0419_high-warp-orbit-wrong-real-orbit` and `logs/2026-04-14_0434_orbital-spawn-wrong-snapshot-followup` (2026-04-14). After the `#353` orbit-source fixes, the real vessel for `Kerbal X` spawned onto the expected last stable Kerbin orbit instead of dying or inheriting a nonsense orbit, but the spawned vessel state still matched an older breakup-time snapshot instead of the final stable-recording state.

**Root cause:** the bad snapshot was already persisted before spawn. In the breakup-continuous tree design, the active recording keeps its earlier `ChildBranchPointId`, so `FinalizeIndividualRecording()` treated it as a non-leaf and skipped the stable-terminal re-snapshot path that normal landed/splashed/orbiting leaves use. `EnsureActiveRecordingTerminalState()` only set `TerminalStateValue`; it did not refresh `VesselSnapshot`, so the tree kept the old post-breakup `_vessel.craft` sidecar even though the terminal orbit had been updated correctly.

**Fix:** tree finalization now detects when the active recording is the effective leaf for its vessel and ends in a stable spawnable terminal state (`Landed`, `Splashed`, `Orbiting`). For that case it captures terminal orbit/position from the live vessel and rewrites the terminal snapshot before the tree is persisted, using the same stable-terminal snapshot refresh path as ordinary leaf recordings. Added regression coverage for the effective-leaf versus same-PID-continuation gating helper.

**Status:** ~~Fixed~~

---

## ~~353. High-warp orbital end-of-playback spawns can immediately die to on-rails pressure despite a valid rebuilt orbit~~

**Observed in:** `logs/2026-04-14_0301_high-warp-orbit-spawn-missing` and `logs/2026-04-14_0359_high-warp-orbit-stable-orbit-trim` (2026-04-14). During deferred high-time-warp orbital playback for `Kerbal X`, Parsek reported a successful real-vessel spawn but nothing materialized in orbit. The newer bundle also showed the ghost replaying a long stable orbital coast all the way to the original recording end instead of trimming to the normal boring-state buffer after orbit insertion.

**Root cause:** two faults were compounding on the same path. First, `RecordingOptimizer.FindLastInterestingUT()` treated a late zero-throttle `EngineIgnited` seed artifact as real activity, so stable `ExoBallistic` tails were not trimmed to `last real activity + 10s`; playback stayed alive until the raw recording end instead of resolving shortly after the vessel entered its final stable orbit. Second, when the deferred orbital spawn finally ran, the spawn path was mixing time domains: it rebuilt the real vessel at the current `spawnUT`, but it fed `SpawnAtPosition()` a lat/lon/alt + velocity sample captured at the recording endpoint UT. During high warp, that let `GetWorldSurfacePosition()` use a later body rotation while the velocity still belonged to the old inertial state, so the reconstructed orbit could collapse into a suborbital trajectory even though the recording's stored terminal orbit was valid. The reused ascent snapshot also still carried stale packed-vessel and per-part atmospheric fields (`hgt`, PQS bounds, `altDispState`, `tempExt`, `tempExtUnexp`, `staticPressureAtm`), which made the packed vessel look even less like a stock orbital save-state.

**Fix:** stable-orbit boring tails now ignore inert zero-throttle engine/RCS control-state seed artifacts when computing `lastInterestingUT`, so they trim to the normal `~10s` boring-state buffer after the last real activity instead of replaying a full orbital coast. Orbiting end-of-playback spawns also now prefer the recording's stored terminal orbit: `SpawnOrRecoverIfTooClose()` propagates that orbit to the current spawn UT, derives a current lat/lon/alt + orbital velocity from it, and only then calls `SpawnAtPosition()`. `NormalizeOrbitalSpawnMetadata()` also now scrubs both top-level packed fields and per-part atmospheric state back to stock orbital defaults before `ProtoVessel.Load()`. Added regression coverage for the terminal-orbit spawn-state selection, orbital metadata normalization, and zero-throttle engine-seed filtering in boring-tail trim.

**Status:** ~~Fixed~~

---

## ~~352. Pending-tree merge dialogs can ghost-only the active vessel even when playback would spawn it~~

**Observed in:** `wt-fix-mun-landing-persist` follow-up (2026-04-14). In the merge dialog for a pending tree, an active non-leaf vessel from a breakup-continuous Mun landing or splashdown could default to ghost-only even though the same recording would be spawnable once committed and played back.

**Root cause:** `MergeDialog.CanPersistVessel()` reuses `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd()`, but that policy originally resolved effective-leaf and non-leaf safety-net checks only through `RecordingStore.CommittedTrees`. During the merge dialog the tree is still pending, so active non-leaf recordings had no resolvable tree context and lost the breakup/effective-leaf distinction.

**Fix:** `ShouldSpawnAtRecordingEnd()` now accepts an optional `RecordingTree` context and resolves effective-leaf / non-leaf safety-net checks against either the pending tree or the committed tree. `MergeDialog.BuildDefaultVesselDecisions()` passes the pending tree through for both normal leaves and the active non-leaf fallback, and regression coverage now pins pending-tree effective-leaf, same-PID continuation, and null-`ChildBranchPointId` safety-net behavior.

**Status:** ~~Fixed~~

---

## ~~351. Long-range landed ghosts can clip into terrain on held final-pose paths~~

**Observed in:** `logs/2026-04-13_2136` ghost-underground follow-up (2026-04-13). A landed watched ghost in the visual tier could still end a playback step with its mesh partially sunk into terrain even though the normal in-flight clamp path kept earlier frames above ground.

**Root cause:** the immediate surface-position paths (`past-end` hold, loop hold/boundary, overlap expiry) still used only the small fixed landed-ghost floor while normal frame-by-frame playback already used distance-aware terrain clearance. At long watch distances, or on legacy recordings with `TerrainHeightAtEnd = NaN`, the held terminal pose could therefore clip into terrain on the last frame.

**Fix:** `PositionGhostAtPoint` now routes landed/splashed hold positioning through the same clearance-aware `ApplyLandedGhostClearance(...)` logic, and the legacy-NaN fallback keeps the historical minimum floor while allowing larger distance-aware clearance when needed. Added regression coverage for the long-range repro and the NaN fallback policy.

**Status:** ~~Fixed~~ in PR `#262`

---

## ~~350. Automatic watch handoff can briefly snap `HorizonLocked` camera orientation on retarget~~

**Observed in:** automatic watch-retarget follow-up (2026-04-13). When watch auto-follow transferred to a continuation ghost while already `HorizonLocked`, the first frame after retarget could use the new target with a stale horizon basis, causing a visible heading/pitch snap before the next per-frame horizon update corrected it.

**Root cause:** `TransferWatchToNextSegment()` and the watch-mode toggle path applied the new target before refreshing that ghost's `horizonProxy` rotation. Camera-target compensation therefore ran against stale orientation state on the first retargeted frame.

**Fix:** extracted `UpdateHorizonProxyRotation()` and now prime the target orientation before `ApplyCameraTarget()` on auto-follow retargets and watch-mode toggles, so the first `HorizonLocked` frame already uses the correct target basis.

**Status:** ~~Fixed~~ in PR `#255`

---

## ~~350. Boarded EVA re-entry playback can drop the boarded tail and final capsule spawn~~

**Observed in:** `logs/2026-04-14_0000_main-stage-forward-bias/`. User report: during playback, the last kerbal showed the initial EVA exit but not the later circling/re-entry back into the capsule, and at recording end only the two EVA kerbals spawned while the capsule with the re-boarded kerbal never materialized.

**Root cause:** The repro had two separate failures in the same board/re-entry chain.

- `SessionMerger.MergeTree()` rebuilt the parent recording's flat trajectory from stale `TrackSections` even after newer flat points had been appended post-boarding. That collapsed the merged parent down to the section payload prefix and discarded the visible tail covering the last kerbal's circling/re-entry path.
- The boarded child recording ended as a legitimate single-point landed leaf. Playback/spawn gating still treated `< 2` points as "no renderable ghost data", so the leaf never ran the normal completion/spawn path and the final capsule with the boarded kerbal was skipped.

**Fix:** `SessionMerger` now preserves the flat trajectory whenever resolved `TrackSections` only match a prefix of newer flat data, `RecordingStore`/sidecar logic keeps those cases on the conservative flat-fallback path instead of writing them as section-authoritative, and ghost playback/spawn now treats single-point leaf recordings as renderable data while seeding the same interpolated state consumers expect from the normal point path. Added regressions for the merge path, single-point playback gating/state seeding, and direct stale-section serialization fallback coverage.

**Status:** ~~Fixed~~

---

## ~~349. Repaired stand-in rows can hide historical stand-in usage from retirement logic~~

**Observed in:** final GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). After the repair path started rewriting old stand-in `KerbalAssignment` rows back to the slot owner, the kerbals walk only recorded that logical owner name in `allRecordingCrew`. Retirement and roster-healing logic still asked whether the displaced stand-in's own name had ever appeared in recordings, so a historical stand-in like `Kirrim` could be treated as unused and deleted instead of retired once the owner reclaimed the slot.

**Root cause:** The recalculation walk conflated two different identities: the logical kerbal identity used for reservations and the raw snapshot crew names needed to know which stand-in bodies were historically flown.

**Fix:** `KerbalsModule.PrePass()` now caches each recording's raw snapshot crew names, and `ProcessAction()` feeds both the repaired logical owner name and the raw snapshot names into `allRecordingCrew`. That preserves correct retirement/deletion behavior after ledger repair. Added a regression that drives the real `CreateKerbalAssignmentActions()` path for a repaired stand-in recording and verifies the historical stand-in still retires correctly.

**Status:** ~~Fixed~~

---

## ~~348. The displaced-stand-in recreation guard can also suppress retired stand-ins~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The first roster-churn fix stopped recreating any displaced, unreserved chain entry. That also covered retired stand-ins, even though the design expects retired kerbals to remain present in the roster and simply be filtered/managed.

**Root cause:** The recreation guard distinguished only "reserved" vs "not reserved". It ignored the separate retired-stand-in case where a displaced kerbal still appears in historical recordings and therefore must remain as a managed roster entry even when no longer actively reserved.

**Fix:** `ShouldEnsureChainEntryInRoster()` now keeps recreating displaced stand-ins that still appear in committed recordings, while continuing to skip genuinely unused displaced metadata. Added a regression that pins the retired stand-in branch separately from the unused branch.

**Status:** ~~Fixed~~

---

## ~~347. Historical stand-in repairs can fail once the live replacement bridge has been cleared~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The new persisted-row repair compared old ledger rows against freshly regenerated rows, but regeneration still reverse-mapped stand-ins only through the live `CREW_REPLACEMENTS` bridge. If a slot owner had already reclaimed their place and the current replacement map was empty, an old stand-in row like `Hanley` still regenerated as `Hanley` even though `KERBAL_SLOTS` still knew that stand-in belonged to `Jeb`.

**Root cause:** Reverse-mapping logic ignored persisted slot-chain metadata. `CREW_REPLACEMENTS` is transient bridge state rebuilt from the current derived roster, not a complete historical identity source.

**Fix:** Stand-in reverse-mapping now falls back to persisted `KERBAL_SLOTS` when the live replacement bridge has no match, so migration/repair can still recover the original slot owner from saved chain metadata. Added regression coverage for an empty `CREW_REPLACEMENTS` map plus populated `KERBAL_SLOTS`.

**Status:** ~~Fixed~~

---

## ~~346. Ghost-only handoff fallback can misclassify stable snapshot-less chain tips as finite `Recovered`~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The first handoff fix treated every ghost-only chain recording as an internal handoff. Auto-committed tree recordings can also become ghost-only by having `VesselSnapshot` nulled before ledger notification, including stable tips whose terminal state is still `Orbiting`, `Landed`, `Splashed`, or `Docked`.

**Root cause:** `ShouldUseGhostOnlyChainHandoffEndState()` keyed only on `ChainId + no VesselSnapshot + crew source`, so it could not distinguish unresolved handoff segments from stable ghost-only chain tips that still need their normal `Aboard`/`Unknown` semantics.

**Fix:** The ghost-only handoff fallback now applies only when the recording is still unresolved (`TerminalStateValue == null`) or has a genuinely finite terminal (`Recovered`, `Destroyed`, or `Boarded`). Stable ghost-only chain tips with intact terminals stay on the normal unresolved path. Added a regression that pins an `Orbiting` ghost-only chain tip to `Unknown` instead of forced `Recovered`.

**Status:** ~~Fixed~~

---

## ~~345. Existing ledger kerbal rows are not repaired once bad `KerbalAssignment` data is already persisted~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The earlier audit fixes corrected new kerbal-action generation, but `MigrateKerbalAssignments()` still treated any recording with at least one existing `KerbalAssignment` row as already migrated. Old saves that had pre-fix stand-in names or ghost/EVA `Unknown` end states therefore kept replaying the stale ledger data forever.

**Root cause:** The migration path only filled missing per-recording kerbal rows; it never compared stored rows against the current derived truth for that recording, so there was no repair path for already-persisted bad actions.

**Fix:** `MigrateKerbalAssignments()` now builds the desired `KerbalAssignment` set for every committed recording, compares it to the stored rows, and rewrites just that recording's kerbal actions when they diverge. Added regression coverage for both repaired stand-in identity rows and repaired ghost-only `Unknown` rows so legacy ledgers heal on the next load.

**Status:** ~~Fixed~~

---

## ~~344. Ghost-only chain segments fall back to open-ended `Unknown` kerbal reservations~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). Mid-chain vessel/EVA recordings are committed with `VesselSnapshot = null`, but `CreateKerbalAssignmentActions()` still extracted crew from `GhostVisualSnapshot`. Because the old end-state population guard only trusted `VesselSnapshot` or `EvaCrewName`, those segments emitted `KerbalAssignment` rows with `KerbalEndState.Unknown`, which the kerbals walk treated as infinite temporary reservations.

**Root cause:** Ghost-only chain handoff segments had no dedicated fallback end-state rule. The end-state population path assumed "no end snapshot" meant "can't resolve", even though these recordings are a known internal handoff case where the reservation should stay finite until later chain segments extend it.

**Fix:** Ghost-only chain recordings now participate in crew end-state population, and their fallback state is treated as a finite chain handoff (`Recovered`, or `Dead` when the segment truly ended destroyed). `CreateKerbalAssignmentActions()` now forces that shared population path before emitting actions, so commit-time, migration, and load-repair all converge on the same non-`Unknown` result. Added regression coverage for both direct action creation and the load-time safety net.

**Status:** ~~Fixed~~

---

## ~~343. Persisted displaced stand-ins can be recreated on every later roster walk~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). After bug `#339` started preserving displaced chain metadata, `ApplyToRoster()` still recreated any missing non-null chain entry before the delete/retire pass, so a stand-in that had already been deleted as displaced/unused would be re-hired and deleted again on every recalculation.

**Root cause:** The roster-ensure pass only checked whether a chain entry name was missing from `KerbalRoster`. It did not distinguish active/reserved chain occupants from displaced metadata that intentionally no longer had a live roster entry.

**Fix:** `ApplyToRoster()` now skips roster creation/recreation for displaced chain entries unless that stand-in is still actively reserved. The chain metadata remains persisted for deterministic rewinds, but deleted displaced stand-ins no longer churn back into the roster on later passes. Added a regression test that pins the helper decision for active vs displaced chain entries.

**Status:** ~~Fixed~~

---

## ~~342. Tourist passengers can leak into the managed kerbal reservation system~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `CreateKerbalAssignmentActions()` emitted actions for every crew name in the recording snapshot, and `KerbalsModule.ProcessAction()` reserved every `KerbalAssignment` regardless of role, even though the design treats tourist passengers as contract-only temporary crew.

**Root cause:** The kerbal action pipeline had no tourist-role exclusion. Action creation depended on `FindTraitForKerbal`, but there was no defense once a `KerbalAssignment` existed, and the non-runtime fallback could not identify tourists from saved roster history.

**Fix:** `FindTraitForKerbal()` now falls back to the latest saved game-state baseline crew traits when a live KSP roster is unavailable, `LedgerOrchestrator` skips crew whose resolved role is `Tourist`, and `KerbalsModule.ProcessAction()` ignores any tourist assignments already present in the ledger as defense in depth. Added regression coverage for baseline trait fallback, tourist action suppression, and tourist action creation filtering.

**Status:** ~~Fixed~~

---

## ~~341. EVA-only recordings can skip crew end-state population during migration/load repair~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). Commit-time kerbal action creation already treated `EvaCrewName` as a valid crew source, but the legacy migration path and the "populate missing end states on load" safety net only ran when `VesselSnapshot != null`.

**Root cause:** `MigrateKerbalAssignments()` and `PopulateUnpopulatedCrewEndStates()` duplicated an older, narrower guard instead of reusing the commit-time "has crew source" predicate. EVA recordings that stored only `EvaCrewName` therefore skipped end-state inference on those later paths.

**Fix:** The migration and safety-net paths now share a single `NeedsCrewEndStatePopulation()` predicate that accepts either `VesselSnapshot` or `EvaCrewName`. Added `LedgerOrchestratorTests` regressions for both private paths to pin EVA-only recordings to a resolved `Dead`/`Recovered`/etc. end state instead of `Unknown`.

**Status:** ~~Fixed~~

---

## ~~340. Permanent-loss slots can keep stale stand-in occupancy or stay permanently gone after the death is rewound away~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). Slots with an old temporary chain could still present a stand-in as the active occupant after the owner later died permanently, and a saved `permanentlyGone=True` flag could persist even after the death-causing recording was removed from the timeline.

**Root cause:** `OwnerPermanentlyGone` lived in persisted slot state but was not recomputed from scratch each recalculation pass. Once set, it stayed sticky until a full slot reset, and older chain entries could still influence occupant selection unless the active-occupant logic explicitly rejected permanently gone owners.

**Fix:** `PostWalk()` now clears and rebuilds `OwnerPermanentlyGone` from the current reservations on every pass, and regression coverage now pins both sides of the behavior: permanent owners with an existing chain have no active replacement occupant, and rewinding away the permanent loss restores the slot to a normal owner-active state.

**Status:** ~~Fixed~~

---

## ~~339. Chain reclaim can keep the deepest free stand-in active after an earlier occupant should have reclaimed~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `KerbalsModule.GetActiveOccupant()` walked chains from deepest to shallowest, so if `Jeb -> [Hanley, Kirrim]` and only Jeb remained reserved, Parsek still preferred Kirrim instead of letting Hanley reclaim.

**Root cause:** The active-occupant and retirement logic treated "deepest free stand-in" as the winner even after an earlier chain member became free again. That contradicted the design rule that reclaim stops at the first free occupant after the reserved prefix and all deeper entries become displaced.

**Fix:** Kerbal-chain evaluation now follows the reserved prefix and picks the first free occupant as active. Deeper free stand-ins are treated as displaced for retirement/deletion, and `ApplyToRoster()` keeps the chain metadata instead of clearing it wholesale so the derived state stays consistent across later recalculations. Added regression coverage in `KerbalReservationTests` for reclaim ordering and displaced deeper stand-ins.

**Status:** ~~Fixed~~

---

## ~~338. Cold-start `OnLoad` can skip persisted `KERBAL_SLOTS` before the kerbals module exists~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `ParsekScenario.OnLoad` called `LoadCrewAndGroupState()` before `LoadExternalFilesAndRestoreEpoch()`, so `LedgerOrchestrator.Kerbals?.LoadSlots(node)` ran while `LedgerOrchestrator.OnLoad()` had not yet initialized the kerbals module.

**Root cause:** Slot loading relied on an already-created `LedgerOrchestrator.Kerbals` instance, but the first-load ordering guaranteed that the module was still null on a true cold start.

**Fix:** `LoadCrewAndGroupState()` now initializes `LedgerOrchestrator` before loading slots, preserving the intended "load crew replacements + slot graph from save" behavior even on the first load after launching KSP. Added a `QuickloadResumeTests` regression that invokes the private load helper and verifies slot state is populated from the scenario node.

**Status:** ~~Fixed~~

---

## ~~337. KerbalAssignment creation can reserve a stand-in instead of the original kerbal~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `PopulateCrewEndStates` already reverse-mapped stand-in names back to the original slot owner, but `LedgerOrchestrator.CreateKerbalAssignmentActions` re-extracted raw snapshot crew and emitted the stand-in name directly.

**Root cause:** `ExtractCrewFromRecording` did not apply the same `CrewReplacements` reverse-map that `KerbalsModule.PopulateCrewEndStates` uses. When `CrewEndStates` was keyed by the original owner and the emitted action used the stand-in name, the end-state lookup missed and the ledger reserved the temporary stand-in as `Unknown`.

**Fix:** `ExtractCrewFromRecording` now reverse-maps snapshot crew through `CrewReservationManager.CrewReplacements` before building `CrewInfo`, so `CreateKerbalAssignmentActions` and `CrewEndStates` use the same kerbal identity. Added regression coverage in `LedgerOrchestratorTests` for both extraction and action creation.

**Status:** ~~Fixed~~

---

## ~~314. Save/load can prune branched recordings even when sidecars still contain real data~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During a branched `Kerbal X` flight, the user saved, loaded, and then merged the tree. Two EVA/branch recordings had real sidecars on disk before the load:

- `33ea504b82cd479cbc2198c6701a9228.prec`
- `519ae674050d40e3a462cba6328a1e34.prec`

Collected evidence from `logs/2026-04-12_1549_storage-followup-playtest/`:

- Before the load, both branch recordings were actively flushing real trajectory and part-event data, and their sidecars were rewritten (`SerializeTrajectoryInto` / `SaveRecordingFiles` for both IDs).
- On load, `RecordingStore` logged sidecar epoch mismatches for both branch recordings: `.sfs expects epoch 1, .prec has epoch 2`, then skipped sidecar load entirely.
- Immediately after load, both recordings were finalized as leaf nodes with `points=0 orbitSegs=0`.
- `PruneZeroPointLeaves` then removed both zero-point leaves and the empty branch point.
- The later merge dialog only offered the root and debris leaves; the branched EVA leaves were already gone.
- The final saved tree still points `activeRecordingId = 33ea504b82cd479cbc2198c6701a9228`, but that recording is no longer serialized in the tree body.

The skipped branch sidecars still existed on disk in both the collected snapshot and the live save, so this was not physical sidecar deletion; it was save-tree loss after load/prune.

**Additional symptom:** The root recording `eb12d51ffaa64d80a79d3a0f3886e568` also appears to come back shortened: before the load it was saved with `skippedTopLevelPoints=186`, but after merge it was rewritten with `skippedTopLevelPoints=44` and `pointCount = 44` in `persistent.sfs`. The EVA branch loss is certain; root truncation may be part of the same bug or a secondary issue.

**Root cause:** The stale-sidecar epoch guard itself was correct, but the follow-on behavior was not. When active-tree recordings hit an epoch mismatch, `LoadRecordingFiles` left them empty and the restore/finalize path still treated them like genuine zero-point debris leaves. That allowed a save/load cycle to replace a matching in-memory pending tree with a broken disk copy and later prune the hydration-failed leaves out of the tree.

**Fix:** The stale-sidecar epoch guard stays in place, but hydration failures are now explicit runtime state instead of silent empties. Active-tree restore keeps a matching in-memory pending tree when the saved active tree hits stale-sidecar epoch failures, and finalize/prune no longer classifies hydration-failed recordings as removable zero-point leaves. That prevents the destructive branch-loss path even when the epoch guard rejects the disk sidecar.

**Status:** ~~Fixed~~

---

## ~~315. TreeIntegrity PID collision check fails on historical vessel reuse~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The in-game suite reported `RecordingTreeIntegrityTests.NoPidCollisionAcrossTrees` failed with `2 vessel PID(s) claimed by multiple trees`. The collected `persistent.sfs` was otherwise structurally clean: `ParentLinksValid` passed, and manual inspection found no dangling `ParentRecordingId` references.

Concrete repro data from `logs/2026-04-12_1549_storage-followup-playtest/`: three committed `Kerbal X` roots from different trees share the same root vessel PID `2708531065`:

- tree `1683a5d7535f4370baf1ca28b7823069` root `081e7b3ce4b84acc946166a0a3b7926e`
- tree `258d8922c99a45d2a1bb4bf5f7aa7070` root `7f7eadcb943941c1a1668cd44f176459`
- tree `2dc3fa77001f4ad19e766cf6f0ac5277` root `641be2f9522d439397f4ea9fa2caabd2`

**Root cause:** `RecordingTree.RebuildBackgroundMap` populated an ambiguously named cache (`OwnedVesselPids`) from every recording's `VesselPersistentId`, while the in-game integrity test treated that cache like a globally unique cross-tree ownership set. Historical/alternate-history trees can legitimately reuse the same vessel PID, so the test was asserting the wrong contract.

**Fix:** The cache was renamed to `RecordedVesselPids` to match its real meaning ("this PID appears somewhere in this tree's recordings"), `NoPidCollisionAcrossTrees` was replaced with `BackgroundMap` integrity checks that target real corruption, and regression coverage now pins historical PID reuse as valid archived data. Chain trajectory lookup also now prefers chain-participating trees without losing the pre-claim global fallback, so overlapping historical trees do not leave chain ghosts without trajectory data.

**Status:** ~~Fixed~~

---

## ~~316. Breakup debris ghosts can spawn directly into Beyond and never become visible during playback~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During the `s4` reentry/breakup session, some `Kerbal X Debris` recordings did render normally, but later debris recordings spawned so far from the active watch context that they immediately transitioned into the hidden `Beyond` zone and never became visible to the player.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Early debris playback did enter the visible path: ghost `#5` transitioned `Physics->Visual dist=4457m`, and ghost `#7` transitioned `Physics->Visual dist=11876m`.
- Later debris playback for the same recording family did not: ghost `#10` spawned, immediately transitioned `Physics->Beyond dist=943546m`, and was hidden by distance LOD in the same tick.
- The same pattern repeated for ghost `#11`, which spawned and immediately transitioned `Physics->Beyond dist=952154m` before being hidden.
- The recordings themselves were present and merged correctly; the issue is playback visibility, not missing recording data.

**Root cause:** The archived failure was two bugs folded together. Early watched-lineage debris could miss protection because the archived build only protected the exact watched row, not same-tree breakup ancestry. Later debris could still fail even on newer ancestry-aware builds because automatic watch exit cleared the only visibility-protection source before those descendant debris recordings began playback.

**Fix:** Same-tree watched-debris protection now survives missing `LoopSyncParentIdx` by following branch ancestry, and playback-driven automatic watch exits now retain a bounded watched-lineage debris protection window through the last pending descendant debris `EndUT`. Failed replacement watch starts no longer clear that retained protection unless a new watch session is actually committed. The camera still exits normally; only the debris visibility exemption is retained. Added archived-topology regression coverage for:

- late debris with `LoopSyncParentIdx == -1` after final chain splitting
- same-tree ancestry fallback from watched segment to root-parented debris
- retained watched-lineage protection through the last late-debris playback window without repeated retention logs
- zone-rendering watch-protection resolution consuming the retained root for late debris
- null-loaded watch-start commits preserving the prior retained protection window
- the existing watch-target rule that still refuses to retarget camera to non-child same-tree debris

This closes the "spawned but never visible" playback path without loosening the camera handoff rule introduced for `#158`.

**Status:** ~~Fixed~~

---

## ~~317. Horizon-locked watch camera can align retrograde instead of prograde during reentry playback~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). While watching the `s4` reentry ghost, the user reported that horizon mode pointed the camera retrograde rather than prograde.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- The session entered and re-entered horizon watch mode multiple times during the watched descent:
  - `Watch camera auto-switched to HorizonLocked (alt=606m, body=Kerbin)`
  - `Watch camera auto-switched to Free (alt=70003m, body=Kerbin)`
  - repeated `Watch camera mode toggled to HorizonLocked (user override)` during the watched reentry path
- The archived logs did not emit the computed horizon forward vector, selected velocity frame, or a prograde/retrograde label, so the original report could not be proven or disproven from the collected bundle alone.

**Root cause:** `WatchModeController.UpdateHorizonProxy` fed raw playback velocity straight into the horizon-lock basis. During atmospheric watch playback, that can disagree with the ghost's surface-relative prograde once body rotation dominates the remaining horizontal component, making the camera appear retrograde even though the vessel is descending prograde relative to the atmosphere/ground track.

**Fix:** Horizon-locked watch mode now uses a dedicated atmospheric heading basis: in atmosphere it derives heading from `playbackVelocity - body.getRFrmVel(position)` before projecting onto the horizon plane, while outside atmosphere and on airless bodies it preserves the previous playback/inertial heading behavior. The watch-camera logs now emit the chosen horizon basis (`velocityFrame`, source, raw alignment, vectors) when the basis changes and also emit a rate-limited verbose snapshot during long watches so same-body direction shifts remain observable. Regression coverage now pins:

- atmospheric rotation-dominated inversion (surface-relative prograde wins over raw playback direction)
- above-atmosphere preservation of the old playback/inertial heading
- airless-body non-conversion
- zero-result fallback to `lastForward`

**Status:** Fixed in PR `#245`

---

## ~~318. Recordings window stats can show impossible distance / altitude summaries on loaded surface recordings~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). In the `s4` save, the recordings window showed incorrect `dist` / `max alt` values for recent recordings.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- `TrajectoryMath.ComputeStats` produced suspicious summaries immediately after load:
  - `points=29 segments=0 events=0 maxAlt=608 maxSpeed=175.1 dist=0 range=0 body=Kerbin`
  - `points=13 segments=0 events=0 maxAlt=611 maxSpeed=0.0 dist=2 range=0 body=Kerbin`
  - nearby recordings in the same table pass produced more plausible values (`points=58 ... dist=177`, `points=13 ... dist=126`)
- The recordings window computes these values live from loaded recordings rather than trusting `.sfs` cache fields, so this points at a loaded-trajectory/stats issue, not merely stale serialized UI metadata.

**Root cause:** Two storage-side consistency gaps were involved:

- section-authoritative recordings could keep stale flat `Points` / `OrbitSegments` after merge or optimizer split because `TrackSections` changed but the derived flat lists were left copied from the pre-merge/pre-split source
- `ComputeStats` treated relative-frame flattened points as if their `latitude` / `longitude` / `altitude` fields were absolute surface coordinates, even though relative sections reuse those fields for `(dx, dy, dz)` offsets

That combination was enough to produce contradictory summaries like `maxSpeed>0` with `dist=0`.

**Fix:** Section-authoritative merge/split paths now resync flat trajectory lists from `TrackSections` whenever the section payload can rebuild them losslessly, instead of keeping stale copied flats. `TrajectoryMath.ComputeStats` also now applies section altitude metadata and handles relative-frame point distances/ranges as offset-space measurements instead of feeding them through surface-distance math.

**Status:** ~~Fixed~~

---

## ~~319. Watch buttons can disable as "no ghost" after chain transfer even when the user expects an in-range watch target~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The user reported disabled watch buttons while apparently within the ghost camera cutoff distance.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Before transfer, the group-level watch affordance was valid: `Group Watch button 'Kerbal X' main=#0 "Kerbal X" enabled (hasGhost=True sameBody=True inRange=True)`.
- After `TransferWatch re-target: ghost #1 "Kerbal X" ...`, the same group button flipped to `disabled (no ghost) (hasGhost=False sameBody=False inRange=False)`.
- Later rows for descendant recordings `#12` and `#13` also logged `disabled (no ghost)`, not `disabled (out of range)`.
- Debris rows were separately disabled as `debris`, which is expected and distinct from the reported symptom.

**Root cause:** This was not a pure cutoff-distance bug. Watch auto-follow could legitimately retarget to a descendant ghost while the recordings table still evaluated the group `W` button against the group's original main recording, whose own ghost had already been destroyed. That stale source-row evaluation produced a misleading `disabled (no ghost)` state even though watch had already transferred to a valid descendant.

**Fix:** Added shared watch-target resolution in `GhostPlaybackLogic` so group watch affordances follow the same continuation lineage as watch auto-follow, including multi-hop same-PID handoffs through inactive intermediates. Group `W` now evaluates and enters watch on the resolved live target, while per-row `W` semantics stay exact-recording to avoid duplicate/stale `W*` states. Logging now includes both source and resolved watch-target context, and the fallback rules remain aligned with actual auto-follow semantics (no illegal descent through non-breakup fallback branches, no breakup fallback to different-PID children).

**Status:** Fixed

---

## ~~320. Merge confirmation should appear before the stock crash report on vessel destruction~~

**Observed in:** Phase 11.5 storage/watch follow-up playtests (2026-04-12). On vessel destruction, the old/good behavior was: Parsek's merge confirmation appeared before KSP's stock crash/flight-results report. The current ordering regressed, making the crash report take focus first.

**Desired behavior:** When a recording session ends via vessel destruction and Parsek needs merge/commit input, surface the merge confirmation before the stock crash report so the Parsek flow is not hidden behind the stock dialog.

**Root cause:** The recent follow-up fix over-corrected the original ordering bug. When the active vessel crashed and only controller-less debris leaves were still unresolved, `ShowPostDestructionTreeMergeDialog()` stopped finalizing the tree in `FLIGHT` and instead waited for those debris leaves to become terminal or for a later revert/scene-load path to claim ownership. In the real repro, one debris leaf stayed live long enough that the wait timed out, the merge dialog fell back to `OnFlightReady()`, and the deferred crash dialog could then be replayed in the wrong scene or after the wrong owner had already resolved the tree.

**Fix:** The active-crash tree path no longer waits for a later debris-only fallback owner. After the one-frame destruction settle, if the only remaining blockers are debris leaves, Parsek finalizes the tree immediately in `FLIGHT` and shows the merge dialog there. That restores the original ordering: merge confirmation first, stock crash report after the merge/discard choice. The merge dialog's default "spawnable" decisions now also reuse the real playback spawn policy, so debris and `SubOrbital` leaves no longer show up as fake surviving vessels. Regression coverage now pins the debris-only crash classification to same-scene finalization and the merge-dialog spawn-policy alignment.

**Status:** ~~Fixed~~

---

## ~~321. After the main controller vessel crashes, camera recovery should prefer the anchor vessel, not debris~~

**Observed in:** breakup/watch regression follow-up from `logs/2026-04-12_2055_main-stage-freeze-after-separation/` (`s6`). After the main controlling vessel crashed, the player expectation was to return camera focus to the anchor vessel rather than letting it drift to debris-focused behavior.

**Desired behavior:** When the main controller vessel is lost, the camera should recover to the anchor vessel / stable owning vessel context, not to a debris fragment.

**Root cause:** `GhostPlaybackLogic.FindNextWatchTarget` treated the first active tree child as a generic watch handoff fallback when no same-PID continuation existed. On breakup/crash trees, that let debris win the handoff path.

**Fix:** Breakup/crash watch recovery no longer auto-follows any different-PID breakup child as a generic fallback. Same-PID continuation and `#158` recursive hold/retry behavior are unchanged, but breakup branches without same-PID continuation now return `-1` and let the existing watch hold/exit path restore the preserved live vessel context instead of transferring to debris or another fragment. Added regression coverage for non-breakup fallback, breakup no-fallback, debris-only `-1`, and hold-path behavior.

**Status:** Fixed

---

## ~~322. Detached one-ended struts should not remain visible after separation~~

**Observed in:** breakup/separation playback follow-ups (2026-04-12), including the `s6` separation run. After separation, some struts whose opposite endpoint no longer exists remain visible even though they are only attached to a single surviving part.

**Desired behavior:** A strut should not render once separation leaves it effectively connected to only one part.

**Root cause:** Compound-part ghost build parsed the linked-mesh endpoint pose but did not carry the linked target part PID into playback state. After spawn, separation/destroy/inventory events hid direct part visuals, but compound-part replay never revalidated whether the other endpoint still existed logically, so orphaned strut visuals could remain attached to the surviving vessel.

**Fix:** Ghost build now records compound-part target persistent IDs and logical part presence, then playback revalidates compound-part visibility both at spawn and after visibility-affecting part events. The runtime path now removes logically missing targets from the presence set on decouple/destroy/inventory removal, restores compound parts only when a later placement makes the link valid again, and includes focused regression coverage for target PID parsing plus compound visibility / subtree-removal decisions.

**Status:** Fixed

---

## ~~323. Main-stage and debris group hierarchy can appear wrong after commit/playback~~

**Observed in:** `logs/2026-04-12_2128_kerbalx-booster-throttle-regression/` (`s7`). User report: a recording did not correctly group the main-stage recordings group together with the debris group.

**What the logs showed:** raw auto-grouping did run at commit time:

- `Group 'Kerbal X / Debris' assigned to parent group 'Kerbal X'`
- `Auto-grouped 1 stage(s) under 'Kerbal X', 10 debris under 'Kerbal X / Debris'`

That means the bundle did not prove a bad `GroupHierarchyStore.SetGroupParent(...)` call during commit. The failure was later: during an in-session `ParsekScenario.OnLoad`, Parsek preserved the live committed recordings from memory but still reloaded `GROUP_HIERARCHY` from the save file. If the save's hierarchy snapshot was stale, root-level or otherwise corrected debris groups could be overwritten by older parent mappings even though the committed recordings themselves were current.

Concrete evidence from the collected bundle:

- `KSP.log` reported `18 committed recordings`, `2 committed tree(s)`, but only `Saved group hierarchy: 1 entries`
- the same save still serialized recordings under both `Kerbal X` and `Kerbal X / Debris`
- `persistent.sfs` no longer contained the expected `Kerbal X / Debris -> Kerbal X` hierarchy entry

So the problem was not "bad auto-grouping on commit"; it was "stale hierarchy load clobbers the live session view."

**Fix:** `LoadCrewAndGroupState` now only reloads group hierarchy from the save on true initial load. In-session loads and rewinds keep the in-memory hierarchy as the source of truth, matching the already-preserved in-memory committed recordings. This avoids reconstructing or guessing hierarchy from group names and prevents stale `.sfs` `GROUP_HIERARCHY` data from re-parenting root-level debris groups. Coverage now includes:

- unit tests for `ShouldLoadGroupHierarchyFromSave(...)`
- a live in-game `ParsekScenario.OnLoad` regression that seeds a root-level debris group, feeds `OnLoad` a stale saved parent mapping, and proves the in-memory root-level group remains unparented while existing valid mappings survive

**Status:** ~~Fixed~~

---

## ~~324. Pad-drop launch failures can surface a merge dialog instead of auto-discarding~~

**Observed in:** 2026-04-12 follow-up playtests. User report: a vessel launched, engines were never activated, and it simply fell over / down on the pad. Despite essentially zero horizontal travel, Parsek still surfaced a merge dialog instead of treating the run as an auto-discardable launch failure.

**Desired behavior:** Near-zero-distance launch failures on or immediately around the pad should be classified as pad failures / idle launch failures and discarded automatically, not promoted to merge/commit UI.

**Root cause / hypothesis:** The current failure-discard heuristic likely keys too heavily on generic recording existence or total motion while missing the specific "never really left the pad" case when there is some vertical or physics noise but no meaningful horizontal travel. This is probably adjacent to `IsTreeIdleOnPad`, launch-failure classification, and any max-distance / distance-from-launch thresholds used before showing merge UI.

**Fix:** Pad-failure / idle-on-pad classification now keeps the existing `duration < 10 s` / `distance < 30 m` 3D rule, but adds a recording-aware pad-drop override: if a recording's surface range from launch and absolute altitude displacement from launch both stay within the same 30 m pad-local threshold, it is still treated as pad-local even when raw 3D max distance was inflated by a topple or short fall. Tree merge auto-discard and destroyed-split fallback now both use the recording-aware checks. Added unit coverage for toppled pad drops, real vertical ascent, and downhill false positives.

**Status:** Fixed

---

## ~~325. Repeated F5/F9 during a branched recording can leave child sidecars time-discontinuous and break watch handoff~~

**Observed in:** `logs/2026-04-12_2159_f5-f9-watch-regression/` (`s9`). The `Crater Crawler` tree survived repeated quickload cycles structurally, but watching the root playback failed at the branch: when root `#0` completed, watch entered the hold timer and then exited instead of handing off to the continuation.

**Latest follow-up:** 2026-04-13 local smoke reports described the same symptom family in more user-facing terms: a rover/tree mission came back with the "first part missing" after save/load, and later playback still showed a section that should have been abandoned by the reload. The current snapshot-storage validation bundle (`logs/2026-04-13_0315_s13-snapshot-storage-check/`) verified the new compressed `_craft` sidecars were healthy, so this still points at branched quickload stitching rather than snapshot compression.

**Collected evidence:**

- The final saved tree is still present and structurally valid in `persistent.sfs`: root `07bd...`, same-vessel continuation `db41...`, and EVA child `af7d...` under branch point `d3ca...`.
- The watch failure itself is visible in `KSP.log`:
  - root playback completed while watched: `PlaybackCompleted index=0 ... watched=True`
  - `FindNextWatchTarget` saw the branch point, but no re-target happened
  - watch fell into `Watch hold timer set ... (watched #0)` and then `Watch hold expired ... exiting watch`
  - only several seconds later did the child ghosts actually spawn (`Ghost #2 "Jebediah Kerman" spawned`, then `Ghost #1 "Crater Crawler" spawned`)
- The saved metadata and sidecars disagree badly on timing:
  - root `07bd...` ends at the branch as expected (`explicitEndUT = 53.68`, last point `ut ~= 53.58`)
  - continuation `db41...` claims `explicitStartUT = 53.68`, but its `.prec` first point is `ut = 83.04`
  - EVA child `af7d...` claims `explicitStartUT = 53.68` and `explicitEndUT = 72.94`, but its `.prec` first point is `ut = 82.22` and it continues past `ut = 102.39`

**Impact:** This is more than a camera/watch issue. Repeated quickload during a live branched tree can leave child recordings with stale explicit UT metadata and large gaps between the branch boundary and their actual saved trajectory. Watch handoff then fails because the same-PID continuation child is not ghost-active when the root ends, and the shorter hold expires before the delayed child ghost appears.

**Root cause:** The merged fix narrowed the user-visible failure to watch timing, not missing branch data. After repeated quickloads, resumed child recordings could still remain present but not become ghost-active until the first real payload sample well after the parent branch boundary. The old watch-end policy only used a fixed `3-5 s` real-time hold, so the watched parent could expire before the same-PID continuation ever had a chance to spawn.

**Fix:** Watch handoff now asks the committed tree for the earliest pending continuation activation UT, preferring actual trajectory bounds over stale explicit metadata, and extends the watch-end hold using that future activation time plus the current warp rate. `WatchModeController` keeps recomputing and capping that pending hold until the continuation becomes eligible, then adds a short post-activation grace window before allowing the normal watch exit. Added regression coverage for same-PID and allowed fallback branches, actual-payload-start precedence, warp-aware hold sizing, and capped pending-hold expiry handling.

**Status:** ~~Fixed~~ (PR #238)

---

## ~~327. Main stage can appear frozen or off-trajectory after separation while debris paths continue~~

**Observed in:** 2026-04-13 local smoke after the snapshot-storage PR. User report: the main stage appeared to freeze in the air or move along the wrong trajectory position, while the debris recordings continued playing normally.

**Collected evidence so far:**

- The fresh `s13` validation bundle does **not** currently reproduce the failure. In `logs/2026-04-13_0315_s13-snapshot-storage-check/KSP.log`, watched same-vessel handoff stayed on the main PID continuation:
  - `Auto-follow on completion: #0 -> #10 (vessel=Kerbal X)`
  - `Auto-follow on completion: #10 -> #13 (vessel=Kerbal X)`
  - the corresponding watch-focus lines show descending altitude (`278 m` then `65 m`), not a frozen parent trajectory
- The older `s6` breakup bundle (`logs/2026-04-12_2055_main-stage-freeze-after-separation/`) did show a different watch bug where focus transferred to debris (`#0 -> #6 "GDLV3 Debris"`), but that was the now-fixed `#321` issue and is not proof of a current regression.
- The same `s13` bundle **does** show a save/load storage gap in the main-stage `.prec` payload. At `03:03:26`, `FlushRecorderIntoActiveTreeForSerialization` logged `103` live points for root recording `034b687...`, but the sidecar written immediately afterward still contained only `trackSections=1 / sparsePoints=35`; the next quickload rebuilt only that single section. The `a936e14...` and `8109e9e...` same-PID continuations showed the same pattern at later save boundaries.
- A saved local inspector script, `tools/inspect-recording-sidecar.ps1`, confirmed the persisted `Kerbal X` chain had large gaps between section-authoritative sparse segments even though live recording had advanced further in memory before save.

**Impact:** When it happened, the parent/main-stage recording could become visually unreliable after separation even while child debris recordings continued correctly. That made the playback-integrity failure more serious than a simple camera recovery or grouping issue.

**Root cause:** `ParsekFlight.FlushRecorderIntoActiveTreeForSerialization()` appended only already-closed `recorder.TrackSections` into the active-tree recording and then cleared them, but it did **not** checkpoint the recorder's currently-open `TrackSection` first. Mid-flight saves could therefore write a section-authoritative `.prec` sidecar that was missing the live in-progress sparse trajectory chunk, and quickload playback could freeze or drift across that gap while debris/child recordings continued normally.

**Fix:** PR `#242` now checkpoints the open active `TrackSection` during save-time serialization, immediately reopens a continuation section with the same environment/reference metadata, and adds regression coverage for absolute, relative, and orbital-checkpoint cases. The changelog entry for `0.8.1` already records this shipped fix.

**Status:** ~~Fixed~~ (PR #242)

---

## ~~328. Continuous EVA chains can still remain split across `atmo` -> `surface` boundaries~~

**Observed in:** `logs/2026-04-13_0315_s13-snapshot-storage-check/` (`s13`). User expectation was that a continuous EVA should merge if the motion is continuous, but the committed Bill Kerman chain remained split into atmospheric and surface segments.

**Collected evidence:**

- `KSP.log` shows the optimizer refusing to merge before the split:
  - `Optimization pass: no merge candidates found`
  - `Split recording 'Bill Kerman' at section 3 ... 'atmo' [127..157] + 'surface' [156..158]`
- `persistent.sfs` preserves two real Bill recordings in the same tree:
  - chain index `0`, `segmentPhase = atmo`, `pointCount = 49`
  - chain index `1`, `segmentPhase = surface`, `pointCount = 2`
- Pre-fix merge policy made this behavior explicit: `RecordingOptimizer.CanAutoMerge(...)` required both `SegmentPhase` and `SegmentBodyName` to match exactly before any automatic merge was allowed.

**Impact:** On atmospheric bodies, a continuous EVA touchdown / near-surface sequence can still appear as two adjacent recordings even after the old bogus-atmospheric-stub bug (`#326`) was fixed. This is not the same defect as `#326`: both resulting segments contain real data.

**Root cause:** The optimizer treated all phase changes as meaningful split boundaries and all cross-phase neighbors as merge-ineligible. That was too strict for atmospheric-body EVA continuity: a real kerbal recording could be split into `atmo` + `surface` segments during optimization, but the later merge pass could never heal that pair because the phase tags differed. Older already-split saves also carried slight overlap at the boundary, so naïve append-only repair would risk rebuilding non-monotonic flat points from the section payload.

**Fix:** PR `#248` now treats continuous same-body EVA `atmo <-> surface` boundaries as non-meaningful optimizer splits, allows already-split EVA neighbors to rejoin when EVA identity/body/timing match, trims overlapping loaded section frames before rebuilding flat points, and suppresses misleading mixed phase labels so the repaired recording no longer presents as only `atmo` or only `surface`. Regression coverage now pins:

- split suppression for continuous same-body EVA atmosphere/surface sections
- rejection of non-EVA, unknown-body, or gapped boundaries
- repair of already-split overlapping loaded EVA pairs through the section-authoritative rebuild path
- UI/body-label formatting for repaired mixed-phase EVA recordings

**Status:** ~~Fixed~~ (PR #248)

---

## ~~332. In-game FLIGHT test batches can poison the live session (transparent diagnostics window, broken camera/watch context, null active vessel after quickload canary)~~

**Observed in:** 2026-04-13 local FLIGHT batch runs after collecting `logs/2026-04-13_1635_332-flight-save-load-anchor-ui/` and the follow-up `logs/2026-04-13_1739_332-followup-unsolved/`. User report: after running the in-game FLIGHT tests, the diagnostics window became transparent, the camera/watch anchor no longer stayed on the expected anchor vehicle on the pad, and later the whole session was left broken.

**Collected evidence:**

- `KSP.log` repeatedly threw `ArgumentException: Getting control ... position in a group with only ... controls when doing repaint` from `Parsek.SettingsWindowUI.DrawSettingsWindow`, which explains the transparent/corrupt diagnostics/settings window.
- The failing code rendered the tooltip row conditionally: tooltip present emitted `GUILayout.Space(...)` + `GUILayout.Label(...)`, tooltip absent emitted a zero-height label only. That is the same IMGUI layout/repaint mismatch already avoided in `TestRunnerShortcut`.
- The FLIGHT round-trip tests (`SaveLoadTests.ScenarioRoundTripPreservesCount`, `SceneAndPatchTests.ScenarioRoundTripPreservesTreeStructure`, `SceneAndPatchTests.CrewReplacementsRoundTrip`) call live `ParsekScenario.OnSave`/`OnLoad` mid-batch, which re-subscribes scenario/runtime state without a real scene transition.
- `ParsekFlight` already had the right cleanup primitives (`ExitWatchMode`, `StopPlayback`, `DestroyAllTimelineGhosts`), but the destructive tests were not using them, so stale watch/ghost state could survive after the synthetic `OnLoad`.
- The first bundle also showed the quickload canary path timing out after a broken restore while still reporting pass, because `QuickloadResumeHelpers.WaitForFlightReady` and `WaitForActiveRecording` only logged warnings instead of failing the test.
- The follow-up bundle proved the live-session break was still happening after that detection fix: `RuntimeTests.BridgeSurvivesSceneTransition` triggered a real `TriggerQuickload`, stock `FlightDriver.Start()` immediately threw `NullReferenceException`, and the session stayed in `scene=FLIGHT, flightReady=False, activeVessel=null` while `FlightCamera`, autopilot UI, and related systems kept throwing.
- `parsek-test-results.txt` in the follow-up bundle was stale from an earlier run, which means the broken quickload path never reached clean result export; the reliable evidence was the fresh `KSP.log`.

**Root cause:** The final diagnosis was two different harness faults, only one of which the first patch fully addressed:

- the settings/diagnostics window had a real IMGUI control-count bug in its tooltip row
- the destructive live `OnSave`/`OnLoad` tests mutated the running FLIGHT session without being isolated or normalized afterward
- the real F5/F9 quickload scene-transition canaries were still being batched into normal FLIGHT runs, so once quickload wait helpers started failing honestly, the test harness could finally reveal that stock quickload itself was sometimes leaving the live session half-dead; hardening the wait helper did not stop the destructive scene transition from running

**Fix:** `SettingsWindowUI` now always renders a stable tooltip row using cached zero-height/wrapped styles. The destructive live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite entirely instead of trying to normalize the session after mutating it. Quickload wait helpers still fail with explicit scene/readiness context, and the destructive quickload canaries are now marked single-run-only and are excluded from `Run All` / `Run category` batches, with explicit skip reasons in the runner UI and exported results. They remain available from the row play button when intentionally run in a disposable session.

**Status:** ~~Fixed~~

---

## ~~335. Crash merge dialog can open from an empty tree with the wrong message/duration while deferred split resolution is still running~~

**Observed in:** 2026-04-13 crash follow-up playtests after `#320`. User report: three in-flight crash repros showed different merge-dialog text, and on one run the mission duration looked wrong.

**Collected evidence from:** `logs/2026-04-13_1933_crash-merge-message-diff/`

- `GDLV3` behaved correctly: the tree finalized in `FLIGHT`, all leaves were non-persistable, and the dialog opened with `recordings=6, spawnable=0`.
- The first `Jumping Flea` run was wrong. The recorder had just stopped with `58 points ... over 31.2s`, but `FinalizeTreeRecordings` immediately logged the only recording as having no playback data, `PruneZeroPointLeaves` removed it, and Parsek stashed a pending tree with `0 recordings` before opening the merge dialog with `recordings=0, spawnable=0`.
- Immediately after that empty-tree dialog, `DeferredJointBreakCheck` classified the same break as `WithinSegment` and resumed/committed the pending split recorder. That proves the dialog finalized before the deferred split classification finished.
- A later `Jumping Flea` run opened with `recordings=1, spawnable=0`, which explains why the player saw different dialog wording across otherwise similar crash flows.

**Impact:** The merge dialog could be built from an already-emptied tree, which changes the dialog body, can force the displayed duration to `0s`, and risks discarding the just-recorded trajectory that the pending split recorder was still trying to append back into the tree.

**Root cause:** The `#320` debris-only crash-order fix restored same-scene finalization, but `ShowPostDestructionTreeMergeDialog()` still ignored `pendingSplitInProgress`. In false-alarm joint-break cases, the pending split recorder needs one more deferred frame to classify the break and, if it was not a real vessel split, append its captured data back into the active tree via `FallbackCommitSplitRecorder -> TryAppendCapturedToTree`. Finalizing the tree before that happened left the active leaf temporarily empty, and the later zero-point prune removed it before the dialog was shown. The older prune log text also misleadingly said "debris" even though the helper now removes any empty leaf/placeholder, not only debris.

**Fix:** The post-destruction tree dialog now waits for deferred split resolution and any pending crash-coalescer breakup emission to finish before applying the terminal/debris-only finalization policy, then re-runs the normal guards and finalizes with the repaired tree state. Regression coverage now pins the new pending-crash wait policy branch, and the zero-point prune diagnostics were clarified to describe generic empty leaves/placeholders instead of only debris.

**Residual gap:** There is still no coroutine-level regression test that drives the full `OnVesselWillDestroy -> DeferredJointBreakCheck -> TickCrashCoalescer -> ShowPostDestructionTreeMergeDialog` path. Current coverage pins the extracted pending-crash policy/helper decisions, but the end-to-end scene/coroutine ordering still depends on in-game validation.

**Status:** ~~Fixed~~

---

## ~~329. Debris explosion FX can fire noticeably after visible ground contact~~

**Observed in:** 2026-04-13 local smoke after the snapshot-storage PR. User report: there was a visible delay between debris hitting the ground and the explosion effect.

**Collected evidence / current behavior:**

- Breakup branchpoints in the current `s13` run are saved with `coalesceWindow=0.500` in `KSP.log`.
- `CrashCoalescer.Tick(...)` intentionally waits for the coalescing window to expire before emitting the `BREAKUP` branchpoint.
- Ghost explosion FX are triggered later from the playback destruction path (`TriggerExplosionIfDestroyed(...)`), not from the original impact event itself.
- The `s13` logs show the expected explosion trigger lines for multiple debris ghosts, but do not yet pin exact impact-vs-FX deltas for the user-visible cases.

**Impact:** Even when breakup classification and playback are otherwise correct, crash moments can feel late or "soft" because the visual explosion is not aligned tightly enough with the apparent impact point.

**Root cause / hypothesis:** Timing is likely being influenced by two separate mechanisms:

- the deliberate `0.5 s` crash-coalescing window before the breakup branchpoint exists
- explosion FX being tied to ghost terminal/past-end destruction timing instead of the earliest impact-aligned sample

**Fix implemented:** Destroyed debris playback now resolves an earlier explosion UT from the earliest eligible recorded `PartEventType.Destroyed` and completes the debris ghost there instead of waiting for `EndUT`. Flight and KSC playback both use the same helper, while breakup coalescing/classification timing remains unchanged.

**Status:** Fixed in PR `#241`

---

## ~~331. Debris-only breakup false alarms can truncate the active main-stage sparse trajectory after resume~~

**Observed in:** `logs/2026-04-12_2055_main-stage-freeze-after-separation/` (`s6`). User report: during playback, the main/controller stage froze mid-flight or drifted onto the wrong path while debris recordings continued normally.

**Collected evidence:**

- The active recorder started one atmospheric active `TrackSection` at `UT=42.24` and closed it at the first breakup boundary (`UT=53.82`).
- The same session then logged repeated false-alarm resumes on the root recorder with growing preserved flat-point counts:
  - `Recording resumed after false alarm (43 points preserved)`
  - `Recording resumed after false alarm (82 points preserved)`
  - `Recording resumed after false alarm (84 points preserved)`
- But the logs never showed a new active `TrackSection started` after those resumes, and later sidecar writes for the root recording still serialized only `trackSections=1 ... sparsePoints=43`.
- The saved tree metadata and the saved sidecar disagreed badly on the root/main-stage recording:
  - `persistent.sfs` still recorded root `e1767d3aa36142c1a314092aad62a9bb` with `explicitEndUT = 77.58`, `pointCount = 95`, and `childBranchPointId = 6dabc8...`
  - `tools/inspect-recording-sidecar.ps1` showed the persisted `.prec` for that same recording had only one playable section ending at `UT=53.82` with `43` points
- Playback then treated the root as finished at that first saved sparse boundary and transferred watch to debris:
  - `PlaybackCompleted index=0 vessel=GDLV3 ... watched=True`
  - `TransferWatch re-target: ghost #6 "GDLV3 Debris"`

**Impact:** The active/main-stage recording could look frozen or off-trajectory after the first debris-only breakup even though the flat recording data kept advancing in memory and the debris children continued to replay correctly. Because playback is section-authoritative, the missing resumed sparse section was enough to end the watched root early and hand control to debris.

**Root cause:** `StopRecordingForChainBoundary()` correctly closed the active `TrackSection` before building `CaptureAtStop`, but when the split later resolved as a false alarm, `ResumeAfterFalseAlarm()` only restored the flat recorder state. It did **not** reopen a replacement `TrackSection`. After that, new samples kept appending to `Recording.Points` in memory, while section-authoritative sidecar writes continued to persist only the pre-resume sparse section set. That left playback truncated at the first breakup boundary.

**Fix:** PR `#251` now reopens a continuation `TrackSection` whenever a recorder resumes after a false alarm, but it no longer trusts only the last persisted section. Resume now prefers the recorder's latest closed-or-discarded section snapshot, so brief zero-frame relative/environment flickers do not reopen stale metadata, restores the relative anchor when needed, and rehydrates packed `OrbitalCheckpoint` on-rails state by reopening the live orbit segment before the next off-rails transition. Absolute/relative resumes still seed the boundary frame for continuity, and regression coverage now pins absolute, relative, discarded-section, and orbital-checkpoint false-alarm resumes.

**Status:** Fixed in PR `#251`

---

## ~~336. Debris ghosts can appear ahead of their real playback start, then slide into place~~

**Observed in:** `logs/2026-04-13_1959_debris-slide-still-broken-after-pr258/` after the first debris-positioning follow-up on PR `#258`. User report: debris ghosts still appeared in visibly wrong places on their first frame and only settled into the correct path afterward.

**Collected evidence:**

- The fresh `GhostAppearance` logs showed the ghost root was already snapped to the recording's first flat point on the first visible frame, but the ghost had become visible before any playable section was active:
  - `Ghost #1 "Kerbal X Debris" appearance#1 reason=playback ut=19.72 ... activeFrame=none ... recordingStart@20.26 ...`
  - `Ghost #7 "Kerbal X Debris" appearance#1 reason=playback ut=27.58 ... activeFrame=none ... recordingStart@28.12 ...`
- The matching debris sidecars confirmed the same timing gap:
  - recording `ceea24e2f9d04718ad9a63c9b95dd3c2` had `ExplicitStartUT = 19.72`, but its first playable `TrackSection` frame was `ut = 20.26`
  - recording `9c9ea1b5b5964fe48311835181ba7d3d` had `ExplicitStartUT = 27.58`, but its first playable frame was `ut = 28.12`
- The first structural part events landed only after the first visible frame (`Applied 2 part events for ghost #1` / `#2` after the appearance lines), so the replay briefly showed the full breakup snapshot state before the real payload timeline caught up.
- This ruled out the earlier CoM/root-frame hypothesis as the primary cause of the visible "spawn ahead, then slide back" behavior in the fresh repro. The root was not lagging the recording; visibility was starting too early.

**Impact:** Debris ghosts can become visible off branch metadata instead of their first real playable sample, making them appear tens of meters ahead of where the user expects before the steady-state playback path "catches up". Because breakup debris is snapshot-built and event-pruned, that early-visible window is especially obvious.

**Root cause:** breakup child recordings intentionally preserve the branch boundary as `ExplicitStartUT`, but ghost activation was still keyed off `Recording.StartUT` / raw loop start instead of the first playable payload sample. For these debris recordings, `ExplicitStartUT` came from the breakup branch point, while the first real `TrackSection.frames[0].ut` landed ~0.5 s later. The engine therefore treated the ghost as in-range too early, clamped positioning to the future first point, and made the full snapshot visible before any active section or first-frame part-event pruning existed.

**Fix implemented:** ghost activation now resolves from the first playable payload timestamp instead of the outer semantic branch start. `Recording.TryGetGhostActivationStartUT()` now prefers the first real frame/checkpoint payload, `GhostPlaybackEngine` gates normal playback and loop/debris-sync start off that activation UT, and `GhostAppearance` now logs `activationStart` / `activationLead` so future bundles can prove whether a ghost became visible before its real payload window. Added focused regression coverage for:

- recordings whose `ExplicitStartUT` is earlier than the first playable debris frame
- engine-side activation-start resolution preferring the real payload start over semantic branch timing

**Fresh validation:** `logs/2026-04-13_2136` no longer reproduces the bug. The breakup debris ghosts in that bundle all become visible on playable payload frames with zero activation lead:

- booster debris ghosts `#1`-`#8` all log `activationLead=0.00` together with `activeFrame=Absolute`, never the earlier `activeFrame=none` / "visible before payload" failure
- the stack-separated main-stage debris ghost `#9` (`recId=5df4e23c98ba475493fc9790f2f5584a`) appears at `ut=70.58`, `activationStart=70.58`, `activeFrame=Absolute`, and `recordingStart-root=(0.00,0.00,0.00)` even though the breakup branch itself opened earlier at `UT=70.04`

That confirms PR `#258` removed the original "ghost is visible before its first playable frame, then slides into place" failure mode in a fresh replay/save bundle.

**Status:** ~~Fixed~~ in PR `#258`, validated by `logs/2026-04-13_2136`

---

## ~~337. Stack-decoupled main-stage debris can still look spatially late relative to the exact separation event~~

**Observed in:** `logs/2026-04-13_2136` follow-up after PR `#258`, plus user report from the same recent save. The radial-booster debris looked fixed, but the large main-stage debris created by the circular / stack decoupler still did not look quite right at first appearance.

**Collected evidence:**

- the large stage breakup child was created immediately at the branch event: `pid=2483558814`, `recId=5df4e23c98ba475493fc9790f2f5584a`, breakup `UT=70.04`
- the first playable recorded payload for that child still arrives later:
  - `BgRecorder` starts the child's `TrackSection` at `UT=70.56`
  - the first saved trajectory point in `5df4e23c...prec.txt` is `ut = 70.58`
- the first visible ghost frame is now internally consistent, which means this is **not** the old `#336` bug:
  - `Ghost #9 "Kerbal X Debris" appearance#1 reason=playback ut=70.58 activationStart=70.58 activeFrame=Absolute ... recordingStart-root=(0.00,0.00,0.00)`
- the readable snapshot shows the stage had already moved materially by the time that first playable frame existed:
  - `5df4e23c..._ghost.craft.txt` records `distanceTraveled = 72.953649124686493` at `lastUT = 70.5600000000031`

**Impact:** After the PR `#258` fix, the main-stage debris no longer flashes early and slides into place, but it can still appear noticeably displaced from the exact split moment because playback has no visible payload before the first background-sampled frame. On a large, fast-moving stack-separated stage that remaining branch-to-first-sample gap is much easier to notice than on the smaller radial boosters.

**Root cause:** This was a different failure mode from `#336`. Ghost activation was already keyed to the first playable payload frame, but breakup/background children could still be seeded from the later deferred split-check frame rather than the exact decouple callback, which stamped the branch boundary and initial child pose a few hundredths of a second late. On fast booster and stack-separated debris, that was enough to make the first visible ghost frame appear slightly ahead of the true separation point. A narrower playback-side slip could also still happen if the ghost woke visible one render tick after its activation UT.

**Implemented fix:** breakup/background child recordings now capture exact split-time seed points during `onPartDeCoupleNewVesselComplete`, using the child root-part surface pose, and the deferred split path now resolves `branchUT` from those captured seed UTs (or the exact joint-break UT) instead of blindly using the later deferred-check time. If no exact split-time pose exists, the recorder falls back to the first honest post-split live sample rather than backdating it. Playback also keeps the fresh-first-frame guard from the follow-up investigation: if a ghost wakes visible a tick late, only that very first visible frame clamps back to `activationStartUT` within a narrow window.

**Fresh validation:** `logs/2026-04-14_1449_booster-separation-improved-check` confirms the remaining split-timing bias is gone. For the affected radial-booster pairs and later breakup children, the recorder now logs exact captured split seeds (`capturedSeed=T`, `pointSource=decouple-callback`) with matching `splitUT = branchUT = seedUT`, and the corresponding `GhostAppearance` lines show `ut = activationStart`, `activationLead=0.00`, and `recordingStart-root=(0.00,0.00,0.00)`. The same bundle also shows no fallback `pointSource=deferred-live-sample` / `destroyed-before-capture` cases and no non-zero activation lead for those first appearances. The remaining `seedLiveRootDist` values in the breakup diagnostics are expected because they compare the split seed against the live debris root about `0.5 s` later in the coalescer window, not against the split instant.

**Status:** Fixed in PR `#280`, validated by `logs/2026-04-14_1449_booster-separation-improved-check`

---

## 337. Same-tree EVA branches and parachute-bearing secondaries can still be distance-LOD culled while watching a related ghost

**Observed in:** `logs/2026-04-13_2136/` during the late `Kerbal X` watch session. User report: while watch mode was focused on a `Kerbal X` ghost, the same mission's EVA kerbals and their parachute-bearing secondary visuals were still being reduced/culled as if they were far away, even though the actual watch camera was already sitting near the ghost scene.

**Collected evidence:**

- `Player.log:87208` entered watch on ghost `#19 "Kerbal X"` with `camDist=50.0`.
- `Player.log:87217` immediately afterward still evaluated that watched ghost at `dist=18.6km ... activeVessel="#autoLOC_501232"`, which shows the runtime distance number was still coming from the active vessel context rather than the actual flight camera now parked near the ghost.
- `Player.log:87223` then logged `ReduceFidelity: disabled 36/49 renderers` during that watch session.
- The same save proves the EVA recordings are part of the same tree, not unrelated standalone recordings:
  - `persistent.sfs:1073-1097` recording `a1b840609b1c4c3da4db77fa427b6639` / `Bob Kerman` has `treeId = 62395ab8dcd5426ea2ada17811bacd2c`, `evaCrewName = Bob Kerman`, and `parentRecordingId = 9b3cc91fe9a6407baa49e9036eb0ac44`.
  - `persistent.sfs:1196-1220` recording `58303cf3f681418a8e1854e1659acd25` / `Bill Kerman` has the same tree and EVA metadata.
  - `persistent.sfs:1495-1524` shows both EVA children hanging off the same `Kerbal X` branch lineage.
- The current watch-protection code only protects same-tree debris. `GhostPlaybackLogic.IsWatchProtectedRecording(...)` returns early unless `current.IsDebris`.
- The current distance resolver still measures against `FlightGlobals.ActiveVessel.transform.position` (`ParsekFlight.ResolvePlaybackDistanceForEngine(...)`), not against the actual flight camera position.

**Impact:** During watch mode, same-tree EVA branches and other non-debris secondaries can still enter reduced-fidelity or hidden tiers based on pad/active-vessel distance rather than the camera the player is actually using. That means renderer disabling and part-event suppression can hit exactly the tiny visuals the player is looking at most closely: EVA kerbals, EVA packs/parachutes, and related child-branch scene elements.

**Root cause:** The decisive runtime bug was the distance reference, not the branch ancestry. Flight ghost LOD was still keyed off `FlightGlobals.ActiveVessel.transform.position`, so a watched scene that was only ~50 m from the real camera could still be treated like an 18 km-away visual-tier ghost if the active vessel stayed on the pad. Once that happens, unwatched same-tree EVA ghosts naturally fall into reduced-fidelity tiers even though they are physically near the watched scene.

The debris-only watch-protection lineage introduced for `#316` remains separate. It is still needed for far, late debris visibility retention, but it was not the primary cause of this EVA/parachute repro.

**Desired policy:** Distance LOD for flight ghosts should use the real active flight camera position in flight view. If the camera is sitting next to the watched ghost scene, nearby EVA/debris secondaries should naturally stay in the full-fidelity tier; only content that is actually far from the real camera should be reduced or culled.

**Fix:** `ParsekFlight.ResolvePlaybackDistanceForEngine(...)` now resolves playback LOD distance from the live flight camera position in flight view, falls back to the active vessel when no usable scene camera exists or when map view is active, and has focused regression coverage in `PlaybackDistancePolicyTests`.

**Status:** Fix implemented in PR `#260`; pending fresh runtime validation

---

## ~~338. Atmospheric-body EVA touchdown follow-ups can still misreport phase and split after packed landing~~

**Observed in:** `logs/2026-04-13_2136` while validating the latest EVA optimizer follow-up. The user reported two symptoms in the Recordings list:

- Bill and Bob showed purple `Kerbin` phase cells even though their EVA recordings had real landed/surface endings.
- Jeb still committed as two separate recordings at the end (`Kerbin atmo` and `Kerbin surface`) even though this was expected to be one continuous touchdown sequence.

**Collected evidence:**

- Bill and Bob were already a single mixed EVA recording each. Their `.prec` payloads contained continuous atmospheric -> surface sections on Kerbin, but the table was suppressing the mixed phase text down to body-only `Kerbin` while still styling the cell from raw `SegmentPhase`.
- Jeb's committed payload was genuinely discontinuous at touchdown:
  - the atmospheric payload ended at `UT 213.72`
  - the later surface payload did not begin until `UT 274.98`
- The logs showed the missing bridge at the loaded -> on-rails handoff:
  - parachute cut / atmospheric section close at touchdown
  - vessel packed into landed on-rails state
  - no playable surface boundary section persisted at the handoff UT
  - later promotion reopened as a new surface section
- Review follow-up found a second storage-side risk in the same area: ordinary loaded-flight -> packed-orbit fallback shapes still depended on top-level flat `Points` / `OrbitSegments`, but section-authoritative write eligibility was only checking for non-empty `TrackSections`, not whether those sections could exactly rebuild the flat payload.

**Impact:**

- Mixed atmospheric-body EVA rows could still look like exo/orbit recordings in the UI even when they were already a single repaired touchdown recording.
- Atmospheric-body EVA touchdowns could still split into separate `atmo` and `surface` recordings when the kerbal packed into a landed no-payload on-rails state before the surface boundary was persisted.
- The same loaded->on-rails area also risked silently truncating ordinary packed-orbit continuation on save/load if section-authoritative serialization dropped flat fallback orbit segments too aggressively.

**Root cause:** This turned out to be three related follow-up defects around the same boundary:

- `RecordingsTableUI` styled the phase cell from raw `rec.SegmentPhase` even when `RecordingStore.ShouldSuppressEvaBoundaryPhaseLabel(rec)` intentionally hid the mixed EVA `atmo`/`surface` wording and displayed only the body name.
- `BackgroundRecorder.OnBackgroundVesselGoOnRails` preserved only a flat boundary point when a loaded EVA packed into a landed no-payload on-rails state after an environment-class change. That left no playable surface bridge section at the landing UT, so the optimizer still saw a real gap and split the recording.
- `RecordingStore.ShouldWriteSectionAuthoritativeTrajectory` only checked whether `TrackSections` had payload, not whether they could losslessly reproduce the flat `Points` / `OrbitSegments` trajectory actually stored on the recording.

**Fix:** PR `#266` now:

- derives mixed-EVA phase styling from the displayed label semantics instead of the stale raw phase tag, so body-only mixed rows color as surface when their last playable section is surface
- persists a one-point absolute boundary `TrackSection` when a loaded vessel crosses an environment-class boundary and then packs into a no-payload on-rails state, so atmospheric-body EVA touchdown keeps a playable surface bridge at the handoff UT
- hardens section-authoritative sidecar writes so they only activate when `TrackSections` can exactly rebuild the recording's flat `Points` and `OrbitSegments`; otherwise text/binary `.prec` output stays on the conservative flat fallback path
- adds regression coverage for mixed-EVA phase styling, the no-payload touchdown boundary path, and text/binary sidecar round-trips for the recorder loaded->on-rails fallback shape

**Status:** Fixed in PR `#266`

---

## ~~339. Same-mission tree recordings can split between the mission root and partial vessel subgroups~~

**Observed in:** `logs/2026-04-13_2136/` during the late `Kerbal X` validation pass. User report: inside the `Kerbal X` main group, one visible `Kerbal X` subgroup correctly held two recordings, but four other `Kerbal X` recordings from the same mission still sat directly under the main group instead of joining that subgroup. The same pass also called out a missing feature: EVA crew recordings should be grouped under per-mission `Crew` subgroups instead of staying at the mission root.

**Collected evidence:**

- The tree/branch commit itself already knew about the EVA children. `KSP.log` logged the EVA branch creation for the same mission tree, and the saved recordings kept both `parentRecordingId` and `evaCrewName`.
- The persisted committed recordings in `persistent.sfs` still assigned those EVA rows directly to `recordingGroup = Kerbal X`; the only auto-created subgroup under that mission was `Kerbal X / Debris`.
- The visible split inside the Recordings table was not purely a save/grouping bug. In the affected mission, only the later two `Kerbal X` rows carried a `ChainId`, so the UI nested those two together while leaving the earlier same-vessel tree siblings flat at the mission root even though they belonged to the same vessel lineage.

**Impact:** The mission tree looked internally inconsistent in the UI. Same-vessel segments from one `Kerbal X` mission could be split between a partially nested subgroup and flat root-level rows, and EVA branches were harder to scan because crew recordings were mixed into the mission root instead of being collected under a dedicated per-mission crew branch.

**Root cause:** This turned out to be two separate defects:

- commit-time tree grouping only knew how to synthesize the debris subgroup; it never created `Mission / Crew` subgroups for EVA recordings, so committed EVA rows stayed assigned to the mission root group even though their tree metadata clearly identified them as EVA children
- the Recordings table only built nested vessel blocks from `ChainId`, so same-vessel tree recordings without a chain marker stayed flat while later chain-linked siblings nested, producing the partial `Kerbal X` subgroup seen in the repro

**Fix:** PR `#265` now:

- creates `Mission / Crew` subgroups for EVA tree recordings during commit
- re-homes orphaned same-tree EVA recordings into the correct crew subgroup only when their prior standalone group was auto-assigned by Parsek, avoiding accidental adoption of same-named manual groups
- persists and clears that auto-assigned standalone-group marker through save/load and manual group edits so the adoption rule stays explicit
- nests grouped mission rows in the Recordings table by `TreeId + VesselPersistentId` lineage instead of only `ChainId`, while preserving correct chain-member ordering inside the grouped block

**Status:** Fixed in PR `#265`

---

## ~~340. Manual W watch switches reset the camera to the default ghost-entry angle~~

**Observed in:** `logs/2026-04-13_2136/` during the Kerbal X / EVA watch-switch repro in flight mode. User report: switching between active `W` watch targets (vessel, EVA kerbal, later continuation) reset the ghost camera to a default under/overhead angle instead of keeping the previous relative watch angle around the new ghost.

**Collected evidence from the bundle:**

- Automatic watch handoff already preserved the current watch camera state:
  - `TransferWatch re-target: ghost #10 "Kerbal X" ... target='horizonProxy' ... camDist=75.0`
  - `TransferWatch re-target: ghost #13 "Kerbal X" ... target='horizonProxy' ... camDist=82.9`
- Manual row/group `W` retargets did not. Every manual switch logged a fresh `EnterWatchMode` on `cameraPivot` at the hard-coded entry distance:
  - `Switching watch from #16 to #14 "Bill Kerman"`
  - `EnterWatchMode: ghost #14 "Bill Kerman" target='cameraPivot' ... camDist=50.0`
  - `Watch camera auto-switched to HorizonLocked (alt=289m, body=Kerbin)`
  - the same pattern repeated for `#14 -> #11`, `#11 -> #16`, and later `#11 -> #19`
- The reset happened even when the source watch was already horizon-locked and already using a non-default distance, which ruled out button-eligibility or same-body checks as the primary cause.

**Impact:** Manual ghost-to-ghost retargeting visibly snapped the camera to the default entry framing before the new target's mode settled, making watch mode feel inconsistent with its own automatic continuation handoff. The problem was especially obvious when switching between vessel and EVA ghosts because their `cameraPivot` / `horizonProxy` rotations differ.

**Root cause:** `EnterWatchMode()` treated every manual `W` retarget as a brand-new watch entry. On switch it called `ExitWatchMode(skipCameraRestore: true)`, then started a fresh watch session that reset the mode state to `Free`, rebound to the raw `cameraPivot`, and forced `FlightCamera.SetDistance(50f)`. Unlike `TransferWatchToNextSegment()`, it did **not** preserve the current watch orbit, and it did not compensate pitch/heading when the old and new target transforms had different rotations.

**Fix:** Manual watch retarget now snapshots the live watch-camera orbit before tearing down the old target, preserves the original player-camera restore state across repeated ghost-to-ghost switches, resolves the new ghost's effective watch mode, and reapplies the previous orbit to the new target instead of using the default watch-entry framing. The transferred pitch/heading now run through the same target-rotation compensation logic used by watch auto-follow, so `cameraPivot`/`horizonProxy` changes preserve world-facing direction instead of snapping when the target basis changes. Added coverage for:

- watch-switch mode resolution with and without explicit `V`-toggle override
- transferred watch-angle compensation preserving world direction across target-rotation changes

**Status:** Fixed in PR `#267`

---

## ~~326. Landed EVA branch can be seeded as Atmospheric, leaving a bogus 1-point EVA fragment and bad optimizer splits~~

**Observed in:** `logs/2026-04-12_2242_quickload-branch-gaps-s10/` (`s10`). The latest retry did **not** reproduce the exact `s9` watch-handoff failure; main-vessel watch transfer worked on current head. But the run exposed a different regression in the EVA branch path.

**Collected evidence:**

- At the first EVA branch (`UT=70.24`), the new EVA vessel was background-seeded as atmospheric even though the kerbal was effectively on the surface:
  - `BgRecorder TrackSection started: env=Atmospheric ... pid=1614280122 at UT=70.24`
  - then almost immediately after vessel switch/promotion:
    - `TrackSection closed: env=Atmospheric ... frames=1 duration=0.04s pid=1614280122`
    - active EVA recorder started correctly as `SurfaceStationary`
- The final tree preserves that bad fragment as a non-leaf Jeb recording with only one point:
  - runtime timeline: `Recording #2: "Jebediah Kerman" UT 70-79, 1 pts, vessel`
  - saved metadata: recording `5fa6cf9e...`, `pointCount = 1`, `childBranchPointId = c206...`
- The second EVA branch repeated the same wrong initial environment:
  - `BgRecorder TrackSection started: env=Atmospheric ... pid=1487825712 at UT=92.08`
- Later, the optimizer split the second EVA recording into two `atmo` segments:
  - `Split recording 'Jebediah Kerman' ... 'atmo' [92..99] + 'atmo' [99..127]`
  - final runtime timeline: `#5 "Jebediah Kerman" UT 92-99, 13 pts, ghost, chain idx=0` and `#6 ... UT 99-127, 44 pts, vessel, chain idx=1`

**Impact:** A surface EVA can be recorded as if it briefly started in atmosphere, which pollutes the final tree with a tiny non-leaf stub and causes later optimizer output to classify EVA chain segments as `atmo`. That matches the user-visible symptom of "gaps" / badly stitched EVA recordings even though watch transfer on the main vessel path works.

**Root cause:** This turned out to be two related defects:

- when KSP left the source vessel active for one more frame, `CreateSplitBranch -> OnVesselBackgrounded -> InitializeLoadedState` could background-seed the new EVA child from a transient pre-stable loaded state, producing the 1-point `Atmospheric` stub
- later, atmospheric-body EVA classification still trusted transient `FLYING` / `SUB_ORBITAL` state too much for ground-adjacent and splashed kerbals, so near-surface or sea-level bobbing EVAs could still flip into `Atmospheric` and create bogus optimizer splits

**Fix:** EVA branch creation now queues a PID-keyed one-shot initial environment override when the background child is an EVA spawned from a landed / splashed / prelaunch source, and `BackgroundRecorder.InitializeLoadedState` consumes that override on the first real loaded init, including delayed go-off-rails initialization. `EnvironmentDetector`, `FlightRecorder`, and `BackgroundRecorder` now also keep atmospheric-body EVAs in surface segments when they are validly near terrain or bobbing at sea level on an ocean world. Added regression coverage for the branch override helper, pending override bookkeeping, and landed/splashed atmospheric-body EVA classification.

**Status:** ~~Fixed~~

---

## ~~313. Splashed EVA spawn-at-end can place the kerbal slightly underwater~~

**Observed in:** 0.8.0 (2026-04-12). In the Phase 11.5 playtest bundle, the parent splashed vessel (`#24 "Kerbal X"`) was clamped and spawned at sea level, but the EVA child (`#25 "Raydred Kerman"`) spawned at `alt=-0.2` with `terminal=Splashed`. Log sequence:

- `Clamped altitude for SPLASHED spawn #24 (Kerbal X): 0.4 -> 0`
- `Vessel spawn for #24 (Kerbal X) ... alt=0`
- `Snapshot position override for #25 (Raydred Kerman): alt -0.213434... -> -0.213434...`
- `EVA vessel spawn for #25 (Raydred Kerman) ... alt=-0.2 terminal=Splashed`

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

**Root cause:** `VesselSpawner.ResolveSpawnPosition` only clamps splashed terminal-state altitudes when `alt > 0`. That fixes the "recorded slightly above the surface" case, but not the observed EVA endpoint case where the final trajectory point is slightly below sea level. The EVA path uses the trajectory endpoint, `OverrideSnapshotPosition` writes the negative altitude into the snapshot unchanged, and `SpawnAtPosition`/terminal-state override does not enforce a sea-surface floor for splashed EVA spawns. The parent vessel takes the clamp; the EVA child does not.

**Test gap:** `SpawnSafetyNetTests.ResolveSpawnPosition_EvaLanded_FallsThroughToSplashedClamp` only covers `alt > 0 -> 0`. There is no regression test for a splashed EVA endpoint with `alt < 0`.

**Fix:** `VesselSpawner.ResolveSpawnPosition` now floors every non-zero `TerminalState.Splashed` altitude to sea level (`0.0`), not just the `alt > 0` case. That applies uniformly to EVA, breakup-continuous, and snapshot-based splashed spawns before `OverrideSnapshotPosition` / `SpawnAtPosition` runs. Added `ResolveSpawnPosition_EvaSplashed_NegativeEndpointAltitude_FloorsToSeaLevel` and updated breakup-continuous coverage so slightly negative terminal samples cannot place a spawned vessel underwater.

**Status:** ~~Fixed~~

---

## ~~312. Duplicate-blocker recovery destroys sibling recordings~~

**Observed in:** 0.8.0 (2026-04-12). Playtest placed 4 "Crater Crawler" ghosts on the runway within ~10 m of each other. Only 2 of 4 spawned -- each new spawn destroyed the previous one. Log sequence showed `Duplicate blocker detected for #6: recovering pid=... at 8m -- likely quicksave-loaded duplicate (#112)` firing for every pair.

**Root cause:** `VesselSpawner.ShouldRecoverBlockerVessel` matched by NAME only. The #112 fix was written for a specific scenario -- KSP's quicksave restores a Parsek-spawned vessel with the same PID while Parsek is also trying to spawn the same recording, creating two copies owned by the same recording. The correct response in that case is to destroy the restored duplicate. But a name-only check cannot distinguish that scenario from "four sibling recordings of the same vessel type landed near each other" -- in the latter, each new spawn found a sibling with the same vesselName and destroyed it, cascading across all four showcases.

**Fix:** `ShouldRecoverBlockerVessel` now also requires the blocker's PID to match THIS recording's own `Recording.SpawnedVesselPersistentId`. That's the only way to be certain the blocker is a duplicate of OURSELVES (the #112 scenario). If the blocker belongs to a sibling recording (different `SpawnedVesselPersistentId`), the check returns false and `CheckSpawnCollisions` falls through to walkback, which finds a clear sub-step along the trajectory and spawns in the correct place.

Signature change: `ShouldRecoverBlockerVessel(Recording rec, string blockerName, string recordingVesselName, uint blockerPid)`. The single call site in `CheckSpawnCollisions` now passes the recording and `blockerVessel.persistentId`. Unit tests in `DuplicateBlockerRecoveryTests` rewritten to cover the new PID-match logic: the #112 self-PID case (recover), the #312 sibling case (walkback), the first-spawn / `SpawnedVesselPersistentId == 0` case (walkback), and preserved null/empty/case-sensitive behavior from the original.

**Status:** Fixed

---

## ~~311. Walkback spawns mid-air on diagonally-descending trajectories~~

**Observed in:** 0.8.0 (2026-04-12). When `TryWalkbackForEndOfRecordingSpawn` steps backward through a trajectory to find a non-overlapping candidate, it historically used the raw trajectory altitude for the clear position. For a vessel diagonally descending onto a landing site, the earlier trajectory points were 10-30 m in the air — walking back found a lateral-clear spot but placed the vessel mid-air, and it fell. Related to #309 (old `ClampAltitudeForLanded` would down-clamp the walkback result aggressively, which *accidentally* masked this, but broke mesh-object positioning).

**Root cause:** The walkback callback used `body.TerrainAltitude(lat, lon)` as the surface reference, which is PQS-only and cannot see the real surface when it includes mesh objects (Island Airfield runway, launchpad, KSC buildings). Either the vessel was spawned underground (mesh-object case) or left mid-air (regular terrain with sparse PQS fallback).

**Fix:** After walkback returns a clear candidate, fire a top-down `Physics.Raycast` at the candidate `(lat, lon)` using the same layer mask as `Vessel.GetHeightFromSurface` (`LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` — default + terrain + buildings). If the raycast hits AND the candidate altitude is more than `WalkbackSurfaceSnapThresholdMeters` (5 m) above the hit, snap the altitude down to `surface + WalkbackSurfaceClearanceMeters` (1 m). If the raycast misses (target area unloaded), fall back to the PQS safety floor via `ClampAltitudeForLanded`. The raycast catches real mesh-object surfaces that PQS terrain alone cannot represent. New helper: `VesselSpawner.TryFindSurfaceAltitudeViaRaycast(body, lat, lon, startAltAboveSurface)`. Uses `FlightGlobals.getAltitudeAtPos` to convert the hit point to ASL altitude.

**Status:** Fixed

---

## ~~310. Spawn collision detection used 2 m-cube blocker approximation~~

**Observed in:** 0.8.0 (2026-04-12). `SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels` approximated every loaded vessel as a 2 m-cube at its `GetWorldPos3D()` position and did an AABB overlap check against the spawn bounds. Large vessels (stations, planes, carriers) were under-represented, letting walkback candidates pass inside their real geometry. Small rovers spawned near a docked station would be flagged as clear despite being inside the station's wings. Conversely, AABB false-positives were possible for sparse vessels with wide bounds.

**Root cause:** The original implementation (#127 or earlier) used `FallbackBoundsSize = 2f` as a conservative placeholder for the blocker vessel's bounds because there was no easy way to get the real bounds from a loaded `Vessel` object. The spawn side had access to the snapshot-computed AABB, but the blocker side used the 2 m cube. This was accurate enough for most rocket-to-rocket cases but broke down for large blockers and for mesh-object-adjacent spawns.

**Fix:** Rewrote `CheckOverlapAgainstLoadedVessels` to use `Physics.OverlapBox` against real part colliders. Each hit is resolved to its owning `Part` via `FlightGlobals.GetPartUpwardsCached` (the same helper KSP uses in `Vessel.CheckGroundCollision`). Non-part hits (terrain, building, runway mesh colliders) are skipped via the null-Part filter — the airfield runway never blocks a spawn, but real vessel parts do.

Layer mask: `LayerUtil.DefaultEquivalent` (0x820001 = bits 0, 17, 23) — the same mask KSP itself uses inside `Vessel.CheckGroundCollision` as the "part-bearing layers" identifier. An earlier revision of this fix used `(1<<0)|(1<<17)|(1<<19)` based on a misreading of Unity's layer map (assumed layer 19 was "PartTriggers"; per Principia's verified layer enum layer 19 is PhysicalObjects and PartTriggers is actually layer 21). Using KSP's own constant directly is both correct and future-proof against layer renumbering.

OverlapBox rotation: `Quaternion.FromToRotation(Vector3.up, upAxis)` where `upAxis` is the surface normal at the spawn position (derived from the enclosing celestial body). Aligns the local-space AABB with the body's local up direction so spawns on the far side of a curved body use a correctly-oriented query box.

Legacy filters (skip debris/EVA/flag, exempt parent vessel PID, skip active vessel) retained. The `BoundsOverlap` pure helper is kept for unit tests using injected predicates.

**Status:** Fixed

---

## ~~309. Rovers on Island Airfield spawn 19 m underground~~

**Observed in:** 0.8.0 (2026-04-12). Rover recordings captured at the Island Airfield runway spawned 19 m below the runway surface, inside the raw PQS terrain. Ghost playback of the same recordings also rendered below the runway. Both spawn and ghost placement were treating the airfield as if it didn't exist.

**Root cause:** Three call sites used `body.TerrainAltitude(lat, lon)` which is PQS-only — it queries `pqsController.GetSurfaceHeight()` and returns the raw planetary surface UNDER any placed mesh object (Island Airfield, launchpad, KSC facilities). For a vessel recorded ON the airfield at alt=133.9 m, `body.TerrainAltitude()` returned ~114.9 m (raw terrain under the runway). The three sites and their bugs:

1. **`ParsekFlight.CaptureTerminalPosition`** stored `rec.TerrainHeightAtEnd = body.TerrainAltitude(...)` — losing the 19 m airfield offset. Downstream consumers computed `recordedClearance = alt - TerrainHeightAtEnd` and got ~19 m, which was meaningless to them.
2. **`VesselSpawner.ClampAltitudeForLanded`** computed `target = terrainAlt + LandedClearanceMeters` and aggressively down-clamped any altitude above the target — specifically to fix #282 (Mk1-3 pod low-clearance clipping). For airfield rovers, this buried them 17 m below the runway.
3. **`VesselGhoster.ApplyTerrainCorrection`** called `TerrainCorrector.ComputeCorrectedAltitude(currentTerrain, recordedAlt, recordedTerrain)` which computed `corrected = currentTerrain + (recordedAlt - recordedTerrain)` — mathematically correct for terrain-relative correction, but `currentTerrain` was PQS-only, so the "corrected" altitude was placed relative to PQS, burying ghosts under the runway.

Decompiling `Vessel.CheckGroundCollision` and `Vessel.GetHeightFromSurface` confirmed the right API: KSP uses `Physics.Raycast` with layer mask `LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` to find the true surface including building colliders. The `Vessel.terrainAltitude` property (documented as "height in meters of the nearest terrain **including buildings**") is reverse-computed from that raycast — available on loaded vessels.

**Fix:** Replaced PQS-only terrain queries with the correct surface-aware sources and changed the clamping philosophy from "force to terrain+2 m" to "trust the recorded altitude, only push up if below PQS safety floor":

1. **`ParsekFlight.CaptureTerminalPosition`** — captures `vessel.terrainAltitude` (raycast-derived, includes buildings) instead of `body.TerrainAltitude()`. Also logs the PQS vs. mesh offset for diagnostics.
2. **`VesselSpawner.ClampAltitudeForLanded`** — rewritten. No more down-clamp. Only clamps UP when `alt < (pqsTerrain + UndergroundSafetyFloorMeters)` (2 m), which only fires when PQS terrain has shifted up since recording (rare: KSP update / terrain mod). The #282 low-clearance case is still caught by this floor. KSP's own `Vessel.CheckGroundCollision` handles part-geometry clipping via `getLowestPoint()` on vessel load, so we don't need to front-run it. Renamed `LandedClearanceMeters` → `UndergroundSafetyFloorMeters` (semantic shift — it's a floor, not a target).
3. **`ParsekFlight.ApplyLandedGhostClearance`** — same philosophy. Trusts the recorded altitude; only pushes up if below `pqsTerrain + 0.5 m`. NaN-fallback legacy path unchanged.
4. **`VesselGhoster.ApplyTerrainCorrection`** — rewritten to apply the underground safety floor (0.5 m above PQS) instead of terrain-relative correction.
5. **Removed `TerrainCorrector.ComputeCorrectedAltitude`** — the terrain-relative correction formula was the core of the bug. Tests that encoded it also removed.

**Test rewrite:** `SpawnSafetyNetTests.ClampAltitudeForLanded_*` updated to match the new semantics. The #282 low-clearance case (176.5 m recorded, 175.6 m PQS terrain, 0.9 m clearance) is preserved as a regression guard — it now triggers the 2 m safety floor and pushes up to 177.6 m. Airfield case (133.9 m recorded, 114.9 m PQS terrain, 19 m mesh offset) passes through unchanged.

**Status:** Fixed

---

## ~~307. Rewind save lost on vessel switch during recording~~

**Observed in:** 0.8.0 (2026-04-12). When the player switches vessels during an active recording session (e.g. switching from a booster to a payload, or clicking a different vessel in the tracking station), the R (rewind) button never appears on recordings committed after the switch.

**Root cause:** `OnVesselSwitchComplete` has two flush paths for the outgoing recorder: (1) still-active recorder transitioned to background (line 1335), and (2) already-backgrounded recorder with pending flush (line 1361). Both paths called `FlushRecorderToTreeRecording` but did not call `CopyRewindSaveToRoot`. The rewind save filename from the outgoing recorder's `CaptureAtStop` was never propagated to the tree root recording. After the switch, `recorder` is set to null, and when the tree is eventually committed, `GetRewindRecording` resolves through the root -- which has a null `RewindSaveFileName`.

Related to T59 (EVA branch case), which fixed the same underlying problem in `CreateSplitBranch`. The vessel-switch paths were missed.

**Fix:** Added `CopyRewindSaveToRoot` calls in both vessel-switch flush paths in `OnVesselSwitchComplete` (lines 1337 and 1362), right after `FlushRecorderToTreeRecording` and before `recorder = null`. Uses `recorder.CaptureAtStop` as primary source with `recorder.RewindSaveFileName` as fallback, consistent with all other flush sites.

**Status:** Fixed

---

## ~~306. Ghost engine nozzles always glow red~~

**Observed in:** 0.8.0 (2026-04-12). Engine nozzle parts on ghost vessels permanently displayed a red/orange emissive glow, as if overheating. Stock KSP engines do not glow during normal operation -- the emissive channel is driven at runtime by the thermal system (`part.temperature / part.maxTemperature`).

**Root cause:** `BuildHeatMaterialStates` and `CollectReentryGlowMaterials` in `GhostVisualBuilder.cs` read `coldEmission` from the cloned prefab material via `materialClone.GetColor(emissiveProperty)`. Engine nozzle prefab materials have non-zero `_EmissiveColor` values baked in. Since ghost parts have no temperature simulation, this inherited emissive became the permanent baseline -- the nozzle always glowed at the prefab's emissive level.

**Fix:** Two changes: (1) Force `coldEmission = Color.black` and clear the emissive property on cloned materials immediately after cloning, in both `BuildHeatMaterialStates` (per-part heat) and `CollectReentryGlowMaterials` (whole-ghost reentry glow). (2) Decouple thermal animation from engine/RCS throttle -- removed `ApplyHeatState` calls from `EngineIgnited/Shutdown/Throttle` and `RCSActivated/Stopped` handlers in `GhostPlaybackLogic`. Thermal glow now driven purely by `ThermalAnimationCold/Medium/Hot` events from `ModuleAnimateHeat` polling. Thresholds adjusted: cool <40%, warm 40-80%, hot >80% (was <10%, 33%+, 66%+). Hysteresis gaps [0.35, 0.40) and [0.75, 0.80).

**Status:** Fixed

---

## ~~304. Raw #autoLOC keys in standalone recording vessel names~~

**Observed in:** Sandbox (2026-04-10). Rovers and stock vessels launched from runway/island airfield showed `#autoLOC_501182` instead of "Crater Crawler" in the Recordings Manager, timeline entries, and log messages.

**Root cause:** Three call sites read `FlightGlobals.ActiveVessel.vesselName` (which is a raw `#autoLOC_XXXX` key for stock vessels) and passed it to `RecordingStore.StashPending()` without calling `Recording.ResolveLocalizedName()`. `BuildCaptureRecording` (FlightRecorder.cs:4541) resolved the name correctly, but `StashPending` creates a new Recording object with whatever string is passed in, discarding the resolved name.

**Fix:** In `StashPendingOnSceneChange`, `ShowPostDestructionMergeDialog`, and `CommitFlight`, prefer `CaptureAtStop.VesselName` (already resolved by `BuildCaptureRecording`) with `ResolveLocalizedName` on the fallback path.

**Status:** Fixed

---

## ~~305. Standalone recordings lost on revert-to-launch~~

**Observed in:** Sandbox (2026-04-10). Rover recordings from runway/island airfield were silently discarded on revert-to-launch. The user had to manually commit via the Commit Flight button instead of getting the merge dialog automatically. Tree recordings (rockets with staging) survived reverts via the Limbo state mechanism, but standalone recordings had no equivalent.

**Root cause:** `DiscardStashedOnQuickload` (ParsekScenario.cs) unconditionally discards pending standalone recordings on any FLIGHT->FLIGHT transition with UT regression. Tree recordings survive because `PendingTreeState.Limbo` is explicitly preserved. Standalone recordings had no Limbo equivalent.

**Fix:** Added `PendingStandaloneState` enum (parallel to `PendingTreeState`) with `Finalized` and `Limbo` values. `StashPendingOnSceneChange` assigns `Limbo` when the destination scene is FLIGHT. `DiscardStashedOnQuickload` preserves Limbo standalones (mirroring tree Limbo preservation). A new Limbo dispatch block in `OnLoad` (parallel to the tree Limbo dispatch) decides:
- If `ScheduleActiveStandaloneRestoreOnFlightReady` is set (F5/F9 mid-recording): discard the stale Limbo standalone, let the restore resume from F5 data.
- If no restore is scheduled (revert-to-launch): finalize the Limbo standalone for the merge dialog.

Design aligned with bug #271 (standalone/tree unification) — the two modes now share symmetric state tracking.

**Status:** Fixed

---

## ~~298b. FlightRecorder missing allEngineKeys -- #298 dead engine sentinels only work for BackgroundRecorder~~

`PartStateSeeder.EmitEngineSeedEvents` emits `EngineShutdown` sentinels for dead engines
using `sets.allEngineKeys` (#298). `BackgroundRecorder.BuildPartTrackingSetsFromState` sets
`allEngineKeys = state.allEngineKeys`, but `FlightRecorder.BuildCurrentTrackingSets` omits it
entirely. FlightRecorder has no `allEngineKeys` field, so `SeedEngines` populates it on a
temporary `PartTrackingSets` that is immediately discarded. The subsequent `EmitSeedEvents`
call creates a new set with an empty `allEngineKeys`, emitting zero sentinels.

**Fix:** Add `private HashSet<ulong> allEngineKeys` to FlightRecorder and include it in
`BuildCurrentTrackingSets`. `SeedEngines` will then populate FlightRecorder's own set
(same reference pattern as `activeEngineKeys`), and the follow-up `EmitSeedEvents` will
see the populated set and emit the sentinels.

**Status:** ~~Fixed~~

---

## ~~297. FallbackCommitSplitRecorder orphans tree continuation data as standalone recording~~

When a vessel is destroyed during tree recording and the split recorder can't resume,
`FallbackCommitSplitRecorder` stashes captured data as a standalone recording via
`RecordingStore.StashPending`, ignoring the active tree. The tree root is truncated
(missing post-breakup trajectory) and the continuation becomes an ungrouped standalone.

Real-world repro: Kerbal X standalone recording promoted to tree at first breakup (root
gets 47 points). More breakups add debris children, root continues recording (83 more
points in buffer). Vessel crashes, joint break triggers `DeferredJointBreakCheck`,
classified as WithinSegment, `ResumeSplitRecorder` detects vessel dead, calls
`FallbackCommitSplitRecorder` which stashes 83-point continuation as standalone.

**Fix:** Extracted `TryAppendCapturedToTree` -- when `activeTree != null`, appends captured
data to the active tree recording (fallback to root) via `AppendCapturedDataToRecording`,
sets terminal state and metadata. Standalone path only runs when not in tree mode.

**Status:** ~~Fixed~~

---

## ~~296. EVA kerbal who planted flag did not appear after spawn~~

Log shows KSCSpawn successfully spawned the EVA kerbal (Bill Kerman, pid=484546861), but the user reports not seeing it. Originally attributed to post-spawn physics destruction.

**Likely duplicate of T57.** The scenario is identical: surface EVA near the launchpad, parent vessel already spawned, kerbal never materialized. The "successfully spawned" log was misleading -- `VesselSpawned = true` is set for abandoned spawns too (prevents vessel-gone reset). T57's fix (exempt parent vessel from EVA collision checks) addresses the root cause. Verify in next playtest.

**Status:** ~~Closed as likely duplicate of T57.~~

---

## ~~290. F5/F9 quicksave/quickload interaction with recordings~~

Broad investigation of F5/F9 + recording system interactions. Bug #292 (F9 after merge drops recordings) was the original smoking gun.

**Bug found:** `CleanOrphanFiles` (cold-start only) built its known-ID set from committed recordings/trees but not the pending tree. On cold-start resume, `TryRestoreActiveTreeNode` stashes the active tree into `pendingTree` before `CleanOrphanFiles` runs, so branch recordings (debris, EVA) were invisible to the orphan scanner and their sidecar files deleted. Data survived the first session (in memory) but non-active branches had `FilesDirty=false`, so sidecars were not rewritten. Second cold start degraded them to 0 points.

**Fix:** Extracted `BuildKnownRecordingIds()` (internal static) from `CleanOrphanFiles`; now includes pending tree recording IDs.

**Other scenarios verified safe:** auto-merge + F9 (no optimizer, no corruption), CommitTreeFlight + F9 (same), rewind save staleness (frozen by design), quicksave auto-refresh (user-initiated only to avoid re-entering OnSave).

**Status:** ~~Fixed~~

---

## ~~290b. RestoreActiveTreeFromPending fails on #autoLOC vessel names~~

**Observed in:** Sandbox (2026-04-11). After F9 quickload, the restore coroutine waited 3s for vessel "Kerbal X" to become active, but KSP's `vesselName` was still the raw `#autoLOC_501232` key (localization not yet resolved). The vessel WAS loaded (correct `persistentId` logged), but the name-only match failed. Tree stayed in Limbo, no recording started, and the orphaned tree eventually triggered a spurious merge dialog.

**Root cause:** `RestoreActiveTreeFromPending` matched only by `vesselName`, which is unreliable immediately after quickload because KSP defers localization resolution.

**Fix:** Added PID-based matching (`VesselPersistentId` vs `Vessel.persistentId`) as the primary check, with name match as secondary fallback. Same change applied to the EVA parent chain walk. PID is locale-proof and available immediately.

**Status:** ~~Fixed~~

---

## ~~290d. MaxDistanceFromLaunch never computed for tree recordings~~

**Observed in:** Sandbox (2026-04-11). All tree recordings had `MaxDistanceFromLaunch = 0.0` despite having real trajectory data (125+ points). `IsTreeIdleOnPad` returned true for all 7 recordings, discarding the entire flight on scene exit to Space Center.

**Root cause:** `MaxDistanceFromLaunch` is computed in `VesselSpawner.ComputeMaxDistance`, which is called from `BuildCaptureRecording`. Tree recordings reach finalization via `ForceStop` (scene exit), which intentionally skips `BuildCaptureRecording` because vessel state may be unreliable during scene transitions. BgRecorder never computes it either. Result: every tree recording has the default `0.0`.

**Fix (three parts):**
1. `FinalizeIndividualRecording` now calls `VesselSpawner.BackfillMaxDistance(rec)` for recordings with `MaxDistanceFromLaunch <= 0.0` and `Points.Count >= 2`. Extracted `ComputeMaxDistanceCore` from the existing private `ComputeMaxDistance` method.
2. `IsTreeIdleOnPad` now requires at least one recording with `Points.Count > 0` before classifying a tree as idle (guards against 0-point recordings from epoch mismatch).
3. `TryRestoreActiveTreeNode` now skips .sfs replacement when the pending tree is already `Finalized` -- the in-memory tree has post-finalize data (MaxDistanceFromLaunch, terminal states) that the .sfs version lacks because KSP's OnSave runs BEFORE `FinalizeTreeOnSceneChange`.

**Status:** ~~Fixed~~

---

## ~~290e. TerrainHeightAtEnd not captured for unloaded vessels at scene exit~~

**Observed in:** Sandbox (2026-04-11). Rover ghosts appeared under the runway surface and spawn-at-end placed the vessel below the runway, causing it to clip through and explode. The rover was a background vessel (player was controlling EVA kerbal) when the scene exited.

**Root cause:** `CaptureTerminalPosition` (which captures `TerrainHeightAtEnd`) is only called when `finalizeVessel != null`. Unloaded background vessels are not findable at scene exit, so `TerrainHeightAtEnd` stays NaN. The spawn safety net falls back to PQS terrain height (~64.8m), which is below the runway structure surface (~70m).

**Fix:** In the `isSceneExit` fallback path of `FinalizeIndividualRecording`, capture terrain height from the last trajectory point's lat/lon coordinates via `body.TerrainAltitude()` for Landed/Splashed recordings.

**Status:** ~~Fixed~~

---

## ~~290f. SegmentPhase classified LANDED vessels as "atmo"~~

**Observed in:** Sandbox (2026-04-11). Rover recordings on the runway showed "Kerbin atmo" instead of "Kerbin surface" in the Phase column.

**Root cause:** Three code paths (`TagSegmentPhaseIfMissing`, `StopRecording`, `ChainSegmentManager`) classified SegmentPhase by altitude alone (`altitude < atmosphereDepth ? "atmo" : "exo"`), ignoring the vessel's situation. A landed rover on Kerbin is technically within the atmosphere by altitude, but its situation is LANDED.

**Fix:** All three sites now check `Vessel.Situations.LANDED/SPLASHED/PRELAUNCH` first and assign "surface" phase. Also added `phaseStyleSurface` (orange) to the UI, changed atmo to blue, exo to light purple.

**Status:** ~~Fixed~~

---

## ~~290g. LaunchSiteName missing from tree recordings~~

**Observed in:** Sandbox (2026-04-11). Site column empty for most recordings in the Recordings window.

**Root cause:** `FlushRecorderIntoActiveTreeForSerialization` (called during OnSave) did not copy `LaunchSiteName` or start location fields from the recorder to the tree recording. Only `FlushRecorderToTreeRecording` (normal stop path via `BuildCaptureRecording`) copied them.

**Fix:** Added LaunchSiteName, StartBodyName, StartBiome, StartSituation copy to `FlushRecorderIntoActiveTreeForSerialization`.

**Status:** ~~Fixed~~

---

## ~~290c. F5/F9 epoch mismatch — BgRecorder and force-writes advance sidecar epoch past .sfs~~

**Observed in:** Sandbox (2026-04-11). F5 quicksave, fly with staging (debris created), F9 quickload. All background recordings lost trajectory data (0 points). Same mismatch on scene exit: Bob Kerman EVA recording lost to epoch drift, then auto-discarded by idle-on-pad false positive.

**Root cause:** `BackgroundRecorder.PersistFinalizedRecording` and the scene-exit force-write loop both call `SaveRecordingFiles`, which unconditionally increments `SidecarEpoch`. Between an F5 (which writes .sfs with epoch N) and F9, BgRecorder can call `PersistFinalizedRecording` multiple times (once per `OnBackgroundVesselWillDestroy`, once per `EndDebrisRecording`), advancing the .prec epoch to N+2 or beyond. On quickload, .sfs expects epoch N but .prec has epoch N+2, triggering `ShouldSkipStaleSidecar` and silently dropping all trajectory data.

**Fix:** Added `bool incrementEpoch = true` parameter to `SaveRecordingFiles`. Out-of-band callers (BgRecorder `PersistFinalizedRecording`, scene-exit force-write loop) pass `incrementEpoch: false` so the .prec keeps the epoch from the last OnSave. OnSave and commit paths use the default `true`, preserving original #270 cross-scene staleness detection.

**Status:** ~~Fixed~~

---

## ~~286. Full-tree crash leaves nothing to continue with — `CanPersistVessel` blocks all `Destroyed` leaves~~

**Fix:** Option (c) implemented. When `spawnCount == 0 && decisions.Count > 0`, the merge dialog body shows: "No flight branches produced a vessel that can continue flying. The recordings will play back as ghosts, but no vessel will be placed." Screen message changed from generic "Merged to timeline!" to "Merged to timeline (no surviving vessels)". Wording is terminal-state-agnostic (covers Destroyed, Recovered, Docked, Boarded). The `CanPersistVessel` blocking logic is unchanged -- the player just needed to know why nothing spawned.

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## ~~189b. Ghost escape orbit line stops short of Kerbin SOI edge~~

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability) — cosmetic, same tier as T25 fairing truss

**Status:** ~~Closed as expected behavior.~~

---

## ~~220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings~~

Intermediate tree recordings with 0 trajectory points but non-null `VesselSnapshot` could re-enter `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session), even when the recording was just an empty intermediate/probe node and could never yield crew data.

**Root cause:** Kerbal end-state inference only distinguished `CrewEndStates != null` from `CrewEndStates == null`. A recording that had already been conclusively evaluated as "no crew" stayed null forever, so later ledger/recalculation passes treated it like "not processed yet" and reran the inference every time. Missing start-snapshot cases also were not separated cleanly from legitimate no-crew recordings.

**Fix:** Recordings now persist a separate `CrewEndStatesResolved` flag. `PopulateCrewEndStates` marks crewless recordings resolved when a valid start-crew source proves there is no crew, leaves genuinely missing-start-snapshot cases unresolved, and the ledger/tree-load paths skip repeat inference once that resolved-no-crew state is known. Added regression coverage for resolved-no-crew behavior, missing-start-snapshot behavior, and save/load persistence of `crewEndStatesResolved`.

**Status:** ~~Fixed~~ (PR #239)

---

## ~~241. Ghost fuel tanks have wrong color variant~~

Parts whose base/default variant is implicit (not a VARIANT node) showed the wrong color. KSP stores the base variant's display name (e.g., "Basic") as `moduleVariantName`, but no VARIANT node has that name — `TryFindSelectedVariantNode` fell through to `variantNodes[0]` (e.g., Orange) instead of keeping the prefab default. Fix: `MatchVariantNode` returns false when the snapshot names a variant with no matching VARIANT node, so callers skip variant rule application and the prefab's base materials are preserved.

**Fix:** PR #198

---

## ~~242. Ghost engine PREFAB_PARTICLE FX fires perpendicular to thrust axis~~

On Mammoth, Twin Boar, RAPIER, Twitch, Ant, Spider, Puff, and other engines, ghost PREFAB_PARTICLE FX (smoke trails, small engine flames) fired sideways instead of along the thrust axis.

**Root cause:** Ghost model parent transforms (thrustTransform, smokePoint, FXTransform) have their local +Y axis pointing sideways, not along the thrust axis. PREFAB_PARTICLE FX emits along local +Y (Unity ParticleSystem default cone axis). Without rotation correction, particles fire perpendicular to the nozzle.

In KSP's live game, the part model hierarchy is oriented within the vessel so that transforms end up with +Y along thrust. In our ghost, the cloned prefab model keeps the raw model-space orientation where +Y is typically sideways.

**Investigation:** Decompiled `PrefabParticleFX.OnInitialize` — uses `NestToParent` (resets localRotation to identity) then sets `Quaternion.AngleAxis(localRotation.w, localRotation)` from config. Decompiled `ModelMultiParticleFX.OnInitialize` — same pattern with `Quaternion.Euler(localRotation)`. Decompiled `NestToParent` — calls `SetParent(parent)` with worldPositionStays=true, then explicitly resets localPosition=zero and localRotation=identity.

Added `LogFxDirection` diagnostic that logs `emitWorld` (local +Y in world space) and `angleFromDown` for every FX instance. This revealed the pattern: entries with explicit -90 X rotation (from config or fallback) had correct angleFromDown=180, while entries with identity had wrong angleFromDown=90.

Exception: SSME (Vector) has `thrustTransformYup` where +Y already points along thrust — applying -90 X there breaks it.

**Fix:** In `ProcessEnginePrefabFxEntries`, when config has no `localRotation`, check `ghostFxParent.up.y` at process time. If abs(y) > 0.5, the parent +Y already aligns with the thrust axis — use identity. Otherwise apply `Quaternion.Euler(-90, 0, 0)` to rotate emission onto the thrust axis. Existing entries with explicit config `localRotation` (jets with `1,0,0,-90`) are unaffected.

**Remaining:** MODEL_MULTI_PARTICLE entries without config localRotation (Mammoth `ks25_Exhaust`, Twin Boar `ks1_Exhaust`, SSME `hydroLOXFlame`, ion engine `IonPlume`) still show angleFromDown=90 in the diagnostic log. These use `KSPParticleEmitter` (legacy particle system) rather than Unity `ParticleSystem`, so their emission axis may differ from +Y — visually they appear correct despite the diagnostic reading. May need separate investigation if visual issues are reported.

**Status:** ~~Fixed~~ (PREFAB_PARTICLE path)

### 242b. Multi-mode engine ghosts show both modes simultaneously

RAPIER and Panther (and any other `ModuleEnginesFX` multi-mode engines) rendered FX for all engine modes at once instead of only the active mode. Ghost would show jet exhaust and rocket exhaust simultaneously.

**Root cause:** `TryBuildEngineFX` scanned ALL EFFECTS groups for every engine module on a part. Multi-mode engines like RAPIER have separate EFFECTS groups per mode (e.g. `running_closed` for rocket, `running_open` for jet). Without filtering, each `EngineGhostInfo` contained particles from all modes.

**Fix:** Added `GetModuleEffectGroupNames(ModuleEngines)` which downcasts to `ModuleEnginesFX` and reads `runningEffectName`, `powerEffectName`, `spoolEffectName`, `directThrottleEffectName`. These names are used to filter EFFECTS groups so each engine module only scans its own referenced groups. Base `ModuleEngines` (not FX) returns empty set and falls through to scanning all groups (backward compat). Removed the old RAPIER `midx>0` skip that suppressed the second engine module entirely. RAPIER white flame fallback guarded by `modelFxEntries.Count == 0` to avoid doubling with per-module model exhaust. Added RAPIER mode-switch showcase recording with per-moduleIndex events demonstrating jet-to-rocket-to-jet switching.

**Status:** ~~Fixed~~ (PR #220)

---

## ~~242c. Ghost variant geometry not toggled -- extra FX on multi-variant parts~~

Parts with `ModulePartVariants` that toggle geometry via GAMEOBJECTS (e.g. Poodle DoubleBell/SingleBell) show all variant geometry on the ghost, including inactive variants. The Poodle ghost has 3 thrustTransforms (2 from DoubleBell + 1 from SingleBell) instead of 2, producing 3 flames instead of 2.

**Root cause:** Ghost model mesh filtering already excluded inactive variant renderers (#241), but the engine FX builder (`EngineFxBuilder`) and RCS FX builder (`TryBuildRcsFX`) discovered transforms from the raw prefab without variant awareness. `engine.thrustTransforms` (populated by KSP from ALL matching transforms regardless of variant state) was the primary source for the Poodle bug. `FindTransformsRecursive` calls in model/prefab FX methods were secondary sources. `MirrorTransformChain` then created ghost transforms for inactive-variant objects.

**Fix:** Threaded `selectedVariantGameObjects` into `TryBuildEngineFX` and `TryBuildRcsFX`. Extracted `IsAncestorChainEnabledByVariantRule` (pure, testable) from `IsRendererEnabledByVariantRule`. Filter applied at 5 points: `FindNamedTransformsCached`, `ProcessEngineLegacyFx` (engine.thrustTransforms), `ProcessEngineModelFxEntries`, `ProcessEnginePrefabFxEntries`, and `TryBuildRcsFX` (FindTransformsRecursive). Affects 9 engine parts and 3 RCS parts with GAMEOBJECTS variant rules.

**Status:** ~~Fixed~~

---

## ~~270. Sidecar file (.prec) version staleness across save points~~

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix:** Added `SidecarEpoch` counter to Recording, incremented on every `SaveRecordingFiles` write. The epoch is stamped into both the .prec file and the .sfs metadata. On load, `LoadRecordingFiles` validates that the .prec epoch matches the .sfs epoch. On mismatch (stale sidecar from a later save), trajectory load is skipped and a warning is logged. Committed recordings are unaffected (FilesDirty stays false after first write, so .prec is never overwritten and epochs always match). Backward compatible: old saves without epoch (SidecarEpoch=0) skip validation entirely.

**Status:** ~~Fixed~~

---

## ~~271. Investigate unifying standalone and tree recorder modes~~

**Fix:** Always-tree mode -- `ParsekFlight.StartRecording` creates a single-node `RecordingTree` for every recording. Eliminated `StashPendingOnSceneChange`, `PromoteToTreeForBreakup`, `RestoreActiveStandaloneFromPending`, `ShowPostDestructionMergeDialog`, `CommitFlight`, `PendingStandaloneState`, `VesselSwitchDecision.Stop`. Chain system (`StashPending`/`CommitPending`) and standalone merge dialog retained for backward compat -- to be unified when chain system is removed.

**Status:** ~~Fixed~~

---

# TODO

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1`
section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary
`v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and
lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot
sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror
path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen
without unpacking the authoritative files first.
Remaining high-value work should stay measurement-gated and follow
`docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- fresh live-corpus rebaseline against current `v3` sidecars
- snapshot-side work should keep focusing on `_ghost.craft` / `_vessel.craft` bytes, where the remaining storage bulk still lives after the first lossless compression slice
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if the post-compression rebaseline still shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the
  missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay
  covered by sidecar/load diagnostics
- add an end-to-end active-tree salvage test that proves a later `OnSave` rewrites healed sidecars
  and clears `FilesDirty`
- add a mixed-case salvage test where several recordings fail hydration but only a subset can be
  restored from the matching pending tree

**Priority:** Current Phase 11.5 follow-on work

---

### ~~T6. LOD culling for distant ghost meshes~~

Implemented in `0.8.1` as the shipped Flight ghost LOD policy:

- `0 - 2300 m`: full fidelity
- `2300 m - 50000 m`: reduced mesh / no part events / muted expensive FX
- `50000 m - 120000 m`: hidden mesh, logical playback retained
- watched ghosts inside cutoff bypass the distance degradation path
- diagnostics now report live `full / reduced / hidden / watched override` counts

**Status:** Fixed in `0.8.1`

### ~~T7. Ghost mesh unloading outside active time range~~

The Phase 11.5 LOD follow-up landed: ghosts that enter the hidden tier now unload built mesh/resources while keeping their logical playback shell alive, then rebuild on demand when they return to a visible tier.

**Status:** Fixed in `0.8.1`

### ~~T8. Particle system pooling for engine/RCS FX~~

Phase 11.5 investigation completed as a measurement-first pass without touching FX behavior.

What shipped:

- playback diagnostics now capture live engine/RCS ghost counts, module counts, particle-system counts, and last-frame ghost spawn/destroy timings
- the showcase injection workflow can run the focused diagnostics/observability slice before mutating the save
- the in-game test runner layout/order was cleaned up so diagnostics and FX-heavy categories are easier to run repeatedly during playtests

Outcome:

- the injected showcase validation passed `Diagnostics` and `PartEventFX`
- exported logs did not show a clear FX-specific correctness or performance regression that justifies touching the current engine/RCS FX lifecycle
- the only notable failure in that bundle was `GhostCountReasonable` (`246` ghosts), which points at overall ghost population pressure rather than FX pooling specifically

Conclusion: no pooling or FX lifecycle optimization is scheduled now. Re-open only if future profiling shows playback spikes, spawn/destroy spikes, or GC pressure that clearly correlates with FX-heavy ghost churn.

**Status:** ~~Closed for Phase 11.5 -- measurement shipped, optimization deferred unless future evidence justifies it~~

---

## TODO — Ghost Visuals

### T65. Ghost audio suppression still logs disabled-source warnings on first appearance

The plume regression is fixed, but the fresh smoke bundle initially still showed a follow-up ghost-audio warning. In `logs/2026-04-14_1459_ghost-engine-fix-smoke/KSP.log`, the main `Kerbal X` ghost correctly starts engine state before its first visible Flight appearance (`GhostAudio` start lines at `15228`, `15230`, `15232`; appearance at `15235`; watch mode only later at `15244`), yet KSP logged `Can not play a disabled audio source` immediately beforehand at `15227`, `15229`, and `15231`. The same pattern repeated earlier in the session at `11904-11908` and `15135-15139`.

Root cause: the warnings did **not** come from capped-away audio sources or from the extra engines that were suppressed by the 4-source cap. The started PIDs in the warning cluster are the retained sources. The real issue was first-frame deferred activation: `GhostPlaybackEngine` applied engine events, including `SetEngineAudio(...).Play()`, while a freshly built ghost hierarchy was still inactive for playback-sync positioning. Unity logs the same warning when `Play()` is called on an inactive/disabled source. The existing deferred runtime restore already replays tracked engine/audio power after activation for `#355`; the fix is to let that restore own the first visible looped-audio start instead of calling `Play()` while the ghost is still inactive.

The `suppressed=1` suffix on the surrounding `GhostAudio` start lines was only `ParsekLog.VerboseRateLimited` bookkeeping for repeated start logs, not ghost-audio suppression bookkeeping.

**Status:** Fixed in code — targeted unit slice passes; fresh runtime smoke-log revalidation still recommended

**Priority:** Medium follow-up after merging `#355` — fixed as log noise / correctness issue, not a plume-visibility regression

---

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort

---

## TODO — Recording Data Integrity

### ~~T66. Add an in-game regression for fresh active-vessel -> ghost watch entry orientation~~

PR `#288` fixed the first `Watch Ghost` transition so it preserves the active-vessel camera basis and no longer flips the camera to the opposite side of the pad on entry. The current automated coverage only pins the fresh-entry mode/distance state prep plus the shared target-basis compensation math; it does not execute the exact stock `FlightCamera` capture -> ghost retarget path in a live KSP scene.

**Follow-up:** add an in-game/runtime repro that starts from a known pad-side anchor-camera view, enters watch mode on a same-body ghost, and asserts the watched first frame preserves the same world-side orientation relative to the vessel/body instead of mirroring to the opposite side.

**Fix:** Added `GhostPlayback.WatchEntry_SameBody_PreservesFreshEntryAngles` in `Source/Parsek/InGameTests/RuntimeTests.cs`. The test finds a same-body ghost, enters watch mode, waits one frame, and asserts that `FlightCamera.fetch.camPitch/camHdg` match the canonical fresh-entry defaults (`DefaultWatchEntryPitch=12 deg` / `DefaultWatchEntryHeading=0 deg`) within 1 degree. A secondary safety-net asserts the camera's world-space forward direction did not flip ~180 degrees (the pre-#288 regression). Teardown calls `ExitWatchMode()` to restore state.

**Priority:** Medium — the core fix is in, but only live KSP can prove the exact first-entry camera handoff end-to-end

**Status:** ~~Fixed~~

---

### ~~T65. Revert-finalized active non-leaf recordings can keep `terminal=null` and default ghost-only~~

**Observed in:** `logs/2026-04-14_1449_booster-separation-improved-check/` on the validated `fix/booster-separation-seed-timing` package (`cf5e6dac`). The booster separation seed-timing fix itself is validated in that bundle; the follow-up regression is the separate revert path:

- `KSP.log:10757` classified the load as a real revert
- `KSP.log:10771` finalized active recording `Kerbal X` as `terminal=none`, `leaf=False`
- `KSP.log:10967-10968` merge dialog treated that active non-leaf as `canPersist=False` / `spawnable=0`
- `KSP.log:11012` applied `ghost-only` to the main recording

**Root cause:** `FinalizePendingLimboTreeForRevert()` finalized the pending tree during `OnLoad`, after the scene reset but before the active non-leaf helper had any scene-exit fallback. Leaf recordings already handled "live vessel unavailable during scene exit" by inferring a terminal from trajectory data; `EnsureActiveRecordingTerminalState()` only tried `FindVesselByPid(...)` and otherwise left the active recording at `TerminalStateValue = null`. That made the merge dialog suppress the main recording even though the stashed recording already had a valid end snapshot/trajectory.

**Fix:** `EnsureActiveRecordingTerminalState()` now accepts scene-exit semantics and reuses the same trajectory-based fallback the leaf finalizer uses when the live vessel is unavailable. On the revert/finalize path it now infers the active non-leaf terminal from recorded end data instead of leaving it null. Added focused regression coverage in `Bug278FinalizeLimboTests.cs` for both the scene-exit inference case and the non-scene-exit no-op case.

**Priority:** Medium — the bug only hits revert/finalize on breakup-continuous active recordings, but it blocks the main vessel from being spawnable in the merge dialog and cascades into ghost-only ledger state

**Status:** ~~Fixed~~

---

### ~~T62. Add a cold-start kerbal slot migration integration test~~

This coverage landed in the new kerbal load pipeline suite. `KerbalLoadPipelineTests` now exercises the cold-start load ordering around `LoadCrewAndGroupState(...)` and `LedgerOrchestrator.OnKspLoad(...)`, including:

- persisted `KERBAL_SLOTS` restoring before kerbal recalculation
- stale historical `KerbalAssignment` rows being repaired back to the slot owner
- EVA-only recordings repopulating crew end states during load repair
- a second load pass staying stable instead of rewriting equivalent rows again

That gives us a dedicated restart-path regression test for the exact slot-load + migration + repair convergence path that had been missing.

**Status:** ~~Fixed~~

---

### ~~T63. Expand true `ApplyToRoster()` end-to-end coverage for repaired historical stand-ins~~

`KerbalLoadDiagnosticsTests` now pins all three adapter-seam categories:

- retired stand-in recreation and unused stand-in deletion through the facade path
- failed historical recreation not producing a false "kept" repair summary (facade path)
- a minimal real-`KerbalRoster` wrapper path for the steady-state retired-history case
- **real-roster `Remove` mutation path**: `ApplyToRoster_WrapperPath_DeletesUnusedDisplacedStandIn` exercises the full pipeline (slots, ledger walk, `ApplyToRoster(KerbalRoster)`) against a reflected `KerbalRoster` instance, confirming that displaced+unused stand-ins are deleted via the real `KerbalRosterFacade.TryRemove` adapter and that displaced+used stand-ins (retired) survive

A smoke probe confirmed that `KerbalRoster.Remove(pcm)` survives xUnit (dict remove + `GameEvents.onKerbalRemoved.Fire` works), while `roster.GetNewKerbal(Crew)` NREs due to missing `CrewGenerator`/`GameDatabase`. The `TryCreateGeneratedStandIn` and `TryRecreateStandIn` branches therefore remain covered only by the fake facade in xUnit and by in-game `CrewReservation` runtime tests.

**Fix:** Added `ApplyToRoster_WrapperPath_DeletesUnusedDisplacedStandIn` test exercising the real `KerbalRoster` adapter's Remove path end-to-end.

**Status:** ~~Fixed~~

---

### ~~T64. Add explicit diagnostics for kerbal slot/assignment repair on load~~

This shipped as `KerbalLoadRepairDiagnostics`. Parsek now emits a concise once-per-load repair summary when kerbal reservation data is actively repaired, including:

- slot source / ignored persisted slot entries
- chain-extension repairs
- repaired recording counts plus old/new assignment row totals
- stand-in remaps, end-state rewrites, and tourist rows skipped during migration
- retired stand-in recreation and unused stand-in deletion during roster application

The emission now works on both cold-start and in-session scene loads, and the regression suite pins the tricky cases that initially produced misleading output: mixed tourist + remap repairs, pure reorder/sequence-only rewrites, chain-extension reporting, and failed historical recreation.

**Status:** ~~Fixed~~

---

### ~~T60. Add regression coverage and diagnostics for R/FF enablement reasons~~

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

Available local playtest logs show R/FF row enablement is driven by recording/runtime state, not ghost distance. Examples:

- `logs/2026-04-10_engine-fx-regression/KSP.log:15443` — `FF #13 "Kerbal X": disabled — Stop recording before fast-forwarding`
- `logs/2026-04-12_0348_bugfixes-2-walkback/KSP.log:23067` — `R #4 "Crater Crawler": disabled — Stop recording before rewinding`
- `logs/2026-04-12_0213_bugfixes-2/KSP.log:12946-12961` — `BeginRewind` is immediately followed by `R #0 "Crater Crawler": disabled — Rewind already in progress`

The local `s3` / LOD archive (`logs/2026-04-11_2339_290-rover-underground/KSP.log`) predates those explicit UI-reason lines, but it does show two long active tree-recording windows:

- `StartRecording succeeded` at `9137`, then the tree is finalized/committed at `11702-12030`
- `StartRecording succeeded` at `12502`, then the tree is finalized/committed at `14659-15003`

That means the long disabled interval reported for the `s3` save lines up with an active recording window; distance is not involved, and the most likely reason in that bundle is the normal `Stop recording before rewinding/fast-forwarding` guard.

The governing code is `RecordingsTableUI` + `RecordingStore.CanRewind/CanFastForward`. Distance/watch state is only used for the `W` button path and should never affect R/FF availability.

**Conclusion:** No real R/FF distance bug found. The runtime behavior was already correct: row/group R/FF enablement comes from recording timing/save/runtime state, while watch distance only gates `W`. In the archived `s3` / LOD save, the long disabled interval tracks active recording; in later reason-logged bundles, the same buttons also legitimately disable during an active rewind.

**Fix:** Added focused coverage for:

- `CanFastForward`: 0-point recordings return `Recording not available`
- `CanFastForward`: current/past recordings return `Recording is not in the future`
- `CanRewind`: tree branches with no root rewind save return `No rewind save available` / `Rewind save file missing`
- transient blocks: `isRecording`, `IsRewinding`, and `HasPendingTree`
- UI-level guard that distance/watch state never changes R/FF enablement
- testable core helpers: `CanFastForwardAtUT` and `CanRewindWithResolvedSaveState` preserve the runtime guard order while making the missing reason cases unit-testable

**Priority:** Medium — current runtime logic is mostly correct, but the failure modes are easy to misread in playtests unless we lock them down with tests and explicit reasoning

**Status:** ~~Fixed~~

---

### ~~T55. AppendCapturedDataToRecording does not copy FlagEvents or SegmentEvents~~

`AppendCapturedDataToRecording` (ParsekFlight.cs:1836) appends Points, OrbitSegments,
PartEvents, and TrackSections, but omits FlagEvents and SegmentEvents. By contrast,
`FlushRecorderToTreeRecording` (ParsekFlight.cs:1675) correctly copies all six lists
including FlagEvents with a stable sort.

This is a pre-existing gap affecting all three call sites of `AppendCapturedDataToRecording`:
- `CreateSplitBranch` line 2137 (merge parent recording with split data at breakup)
- `CreateMergeBranch` line 2283 (merge parent recording with dock/board continuation)
- `TryAppendCapturedToTree` line 1886 (bug #297 fix -- fallback append to tree)

In practice, FlagEvents are rare (only emitted for flag planting, which is uncommon during
breakup/dock/merge boundaries), and SegmentEvents are typically empty in `CaptureAtStop`
because they are emitted into the recorder's PartEvents list, not the SegmentEvents list.
So data loss from this gap is unlikely but possible.

**Fix:** Add FlagEvents and SegmentEvents to `AppendCapturedDataToRecording`, using the same
stable-sort pattern as `FlushRecorderToTreeRecording` (lines 1712-1714). For FlagEvents use
`FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut)`. SegmentEvents have a `ut`
field and should use the same pattern. Add tests to `AppendCapturedDataTests.cs` verifying
both event types are appended and sorted.

**Fix:** Added FlagEvents and SegmentEvents (with stable sort) to both `AppendCapturedDataToRecording`
and `FlushRecorderToTreeRecording`. Two new tests in `AppendCapturedDataTests.cs`.

**Priority:** Low -- unlikely to cause visible data loss, but should be fixed for correctness

**Status:** ~~Fixed~~

---

### ~~T56. Remove standalone RECORDING format entirely~~

Follow-up to bug #271 (always-tree unification). The runtime now always creates tree recordings, and the injector now produces RECORDING_TREE nodes for all synthetic recordings.

~~Critical subtask (done):~~ Removed the temporary `TreeId != null` skip in `CanAutoSplitIgnoringGhostTriggers`. The existing `RunOptimizationPass` code already added split recordings to `tree.Recordings` and updated `BranchPoint.ParentRecordingIds`; the skip was the only thing preventing tree splits. Added `RebuildBackgroundMap()` after optimization passes for tree consistency. Fixed `TraceLineagePids` to follow chain links so root lineage PID collection works after optimizer splits.

~~Steps 1-5 (done):~~ Deleted `StashPending`/`CommitPending`/`DiscardPending` and the `pendingRecording` slot. Replaced with `CreateRecordingFromFlightData` (factory) and `CommitRecordingDirect` (commit without pending slot). Deleted `MergeDialog.Show(Recording)`, `ShowStandaloneDialog`, `ShowChainDialog`. Deleted standalone RECORDING serialization (`SaveStandaloneRecordings`/`LoadStandaloneRecordingsFromNodes`). Deleted `PARSEK_ACTIVE_STANDALONE` migration shim. Rewrote `ChainSegmentManager.CommitSegmentCore` to use new API. Cleaned ~27 standalone references from ParsekScenario. Updated `FlightResultsPatch` to use `HasPendingTree`. Deleted `GetRecommendedAction`/`MergeDefault`, `AutoCommitGhostOnly(Recording)`, `RestoreStandaloneMutableState`, `isStandalone` flag.

~~Step 6 (done):~~ Collapsed `committedRecordings` into `committedTrees`. Changed `CommittedRecordings` to `IReadOnlyList<Recording>`. Added `AddRecordingWithTreeForTesting` helper. Set `TreeId` on chain segments via `ChainSegmentManager.ActiveTreeId`. `FinalizeTreeCommit` skips already-committed recordings. Migrated ~93 test `.Add()` calls across 24 files.

**Status:** ~~Done~~

**Status:** ~~Fixed (PR #214) -- standalone RECORDING format removed, all recordings are tree recordings, CommittedRecordings is IReadOnlyList~~

---

### ~~T57. EVA spawn-at-end blocked by parent vessel collision~~

EVA recordings created by mid-flight EVA (tree branch) fail to spawn at end because the entire EVA trajectory overlaps with the already-spawned parent vessel. The spawn collision walkback exhausts every point in the trajectory and abandons the spawn.

**Fix:** Added `exemptVesselPid` parameter to `CheckOverlapAgainstLoadedVessels` and threaded it through `CheckSpawnCollisions` and `TryWalkbackForEndOfRecordingSpawn`. New `ResolveParentVesselPid` resolves the parent recording's `VesselPersistentId` via `ParentRecordingId`. EVA spawns skip the parent vessel during collision walkback while still detecting other vessels.

**Status:** ~~Fixed~~

---

### ~~T58. Debris/booster ghost engines show running effects at zero throttle after staging~~

When a booster separates (staging, decouple), the debris recording inherits the engine state from the moment of separation. If the engine was running at separation, the ghost plays back engine FX (flame, smoke) even though the throttle is 0 on the separated stage.

**Root cause:** `MergeInheritedEngineState` added inherited engines to the child's `activeEngineKeys` even when `SeedEngines` had already found the engine part but determined it was non-operational (fuel severed by decoupling). The check at line 1870 only verified the key wasn't in `activeEngineKeys`, not whether `SeedEngines` had already assessed the engine.

**Fix:** Added `allEngineKeys` parameter to `MergeInheritedEngineState`. `SeedEngines` adds ALL engine parts (operational or not) to `allEngineKeys`. If an inherited engine key is in `allEngineKeys` but not `activeEngineKeys`, the child vessel has the engine but it's non-operational -- skip inheritance. Only inherit when the engine wasn't found by `SeedEngines` at all (KSP timing issue).

**Status:** ~~Fixed~~

---

### ~~T59. Rewind save lost after mid-recording EVA branch~~

The R button never appears in the recordings table because `RewindSaveFileName` is lost during the EVA branch flow. `BuildCaptureRecording` copies the filename into `CaptureAtStop` then clears the recorder field. The EVA child recorder never captures one. At commit, only the current (EVA) recorder is checked -- both sources are null.

**Fix:** Extracted `CopyRewindSaveToRoot` (ParsekFlight, internal static) that copies `RewindSaveFileName`, reserved budget (funds/science/rep), and pre-launch budget from a `CaptureAtStop` to the tree's root recording. Called from four sites: `CreateSplitBranch` (the primary T59 fix -- copies at branch time before the EVA recorder takes over), `FinalizeTreeRecordings`, `StashActiveTreeAsPendingLimbo`, and `MergeCommitFlush`. First-wins semantics: root fields are only set if currently empty.

**Status:** ~~Fixed~~

---

## 308. Reserved kerbals appear assignable in VAB/SPH crew dialog

**Observed in:** 0.8.0 (2026-04-12). Reserved kerbals (those whose recordings are playing back as ghosts) appear auto-assigned to vessel seats in the VAB/SPH crew dialog. The player sees them in the crew panel and thinks the reservation system failed.

**Root cause:** KSP's `KerbalRoster.DefaultCrewForVessel` auto-assigns all Available kerbals into command pod seats. Reserved kerbals stay at Available status by design (changing rosterStatus caused tug-of-war bugs with `ValidateAssignments` -- see `CrewDialogFilterPatch` history). The existing `CrewDialogFilterPatch` (prefix on `BaseCrewAssignmentDialog.AddAvailItem`) correctly filters reserved kerbals from the Available crew list, but `DefaultCrewForVessel` runs before that filter, so reserved kerbals are already seated in the manifest. The flight-ready swap (`SwapReservedCrewInFlight` in `CrewReservationManager.cs`) catches this at launch time, but the user sees the wrong crew in the editor.

**Fix:** Added `CrewAutoAssignPatch` (Harmony prefix on `BaseCrewAssignmentDialog.RefreshCrewLists`). Walks the `VesselCrewManifest` before UI list creation and replaces any reserved crew with their stand-ins from `CrewReservationManager.CrewReplacements`. If no stand-in is available, the seat is cleared. Pure decision logic extracted into `DecideSlotAction` (internal static) for testability. 8 unit tests in `CrewAutoAssignPatchTests.cs`. Files: `Patches/CrewAutoAssignPatch.cs`.

**Status:** Fixed

---

## TODO — Nice to have

### ~~T67. Investigate the unrelated full-suite `GhostPlaybackEngineTests` Unity-host `SecurityException`~~

The current `Parsek.Tests` full-suite run still has a pre-existing unrelated failure in `GhostPlaybackEngineTests.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`, throwing `System.Security.SecurityException: ECall methods must be packaged into a system module.` The focused watch-camera slices pass, so this did not block PR `#288`, but the failure still weakens the normal full-suite signal.

**Follow-up:** reproduce the failing test in isolation, identify which Unity-native call path is escaping the current test harness assumptions, and either move that assertion behind an in-game/runtime test or harden the unit test so `dotnet test Source\\Parsek.Tests\\Parsek.Tests.csproj` can run cleanly again.

**Priority:** Low — unrelated to the watch-camera fix, but worth cleaning up so the normal full suite becomes trustworthy again

**Fix:** Root cause is `GhostPlaybackEngine.SpawnGhost` calling Unity's `new GameObject(...)` (an ECall) from inside an xUnit run, which throws `SecurityException: ECall methods must be packaged into a system module` outside the Unity runtime. The xUnit case was already skipped with `[Fact(Skip = ...)]` in commit 87a93389, but that left the priming assertions uncovered. The replacement in-game test now lives at `GhostPlayback.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT_InGame` in `Source/Parsek/InGameTests/RuntimeTests.cs` — it picks a committed recording, drives `engine.SpawnGhost` with a sentinel index at a midpoint UT, and asserts the same invariants (`state.ghost != null`, `deferVisibilityUntilPlaybackSync == true`, inactive ghost, populated `lastInterpolatedBodyName`, created `cameraPivot`/`horizonProxy`, ghost moved off origin). The xUnit full suite now runs with 0 failures and 1 intentional skip.

**Status:** ~~Fixed~~

### ~~T53. Watch camera mode selection~~

**Done.** V key toggles between Free and Horizon-Locked during watch mode. Auto-selects horizon-locked below atmosphere (or 50km on airless bodies), free above. horizonProxy child transform on cameraPivot provides the rotated reference frame; pitch/heading compensation prevents visual snap on mode switch.

### ~~T54. Timeline spawn entries should show landing location~~

Already implemented — `GetVesselSpawnText()` in `TimelineEntryDisplay.cs` includes biome and body via `InjectBiomeIntoSituation()`. Launch entries also include launch site name via `GetRecordingStartText()`.

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
