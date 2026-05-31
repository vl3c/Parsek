# Fix: Fresh EVA kerbal misclassified as Destroyed by sub-surface NullSolver fallback

**Branch:** `plan-eva-nullsolver-subsurface`
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-plan-eva-nullsolver-subsurface`
**Companion fix (separate PR):** `plan-eva-deferred-autorecord-orphan` — independent bug in the same flight, do not bundle.

## Problem

Reproducer (observed in `logs/2026-05-13_2337_eva-kerbals-missing/`):

1. Launch `Kerbal X` (3-crew capsule). Recording starts: tree `4e1d9d29` with launch recording `bf8f6c68`.
2. EVA Bill from the capsule at UT 60.4. `OnCrewOnEva` takes the `Mid-recording EVA detected` branch path. Branch point `f6d1a26a` is created with `activeChild=6952389d (capsule continuation, pid 2708531065)` and `bgChild=4ba22dea (Bill, pid 1604830044)`.
3. Within ~3 ms of branch creation, `IncompleteBallisticSceneExitFinalizer.TryFinalizeRecording` runs on the new `bgChild` (Bill). The pipeline goes:
   - `PatchedConicSnapshot.SnapshotPatchedConicChain` reports `FailureReason = NullSolver` because Bill's just-spawned EVA vessel has no patched-conic solver at all (EVA kerbals run on the jetpack motion system in stock KSP). Log: `WARN [PatchedSnapshot] SnapshotPatchedConicChain: vessel=Bill Kerman solver unavailable`.
   - `TryCompleteFinalizationFromPatchedSnapshot` (`IncompleteBallisticSceneExitFinalizer.cs:462-477`) treats `NullSolver` as a known/expected destroyed-vessel signature and falls through to the live-orbit fallback. Log: `falling back to live orbit state`.
   - `TryBuildStartStateFromVessel` (`IncompleteBallisticSceneExitFinalizer.cs:1571`) calls `vessel.orbit.getPositionAtUT(commitUT)`. For a vessel that KSP just spawned via EVA, `vessel.orbit` exists but is uninitialized garbage — position collapses near body-frame origin.
   - `BallisticExtrapolator.Extrapolate` (`BallisticExtrapolator.cs:195-236`) computes `startAltitude = |position| - body.Radius = -599652.6 m`, which is below `SubSurfaceDestroyedAltitude = -100.0`. The sub-surface guard fires and classifies the recording as `Destroyed`.
   - Log: `WARN [Extrapolator] Start rejected: sub-surface state rec=4ba22dea... body=Kerbin ut=60.392 alt=-599652.6 (threshold=-100.0); classifying recording as Destroyed`.
   - `FinalizerCache` accepts the terminal: `rec=4ba22dea pid=1604830044 status=Fresh terminal=Destroyed`.
4. At scene exit, `UnfinishedFlights.CommitTree` finds the recording with `terminal=Destroyed` but `IsUnfinishedFlight reason=strandedEva crew=Bill Kerman` and auto-seals it as `Landed`/`strandedEva` (`ParsekFlight.cs` UnfinishedFlights path). The on-disk row says "Landed", but the cached terminal state is still `Destroyed` and the recording's vessel snapshot was rejected upstream.
5. In playback, Watch on `bf8f6c68` enumerates child ghosts. Bill's recording spawns as `Ghost #10 "Bill Kerman" spawned (recording-start-snapshot, parts=0 engines=0 rcs=0)`. The earlier `Spawn suppressed for #10 "Bill Kerman": no vessel snapshot` log shows the vessel snapshot was unusable. The user sees an invisible ghost where Bill should be walking.

The recorder did capture Bill correctly: `4ba22dea.prec` has 120 points + a valid `_vessel.craft` (2880 bytes, kerbalEVA part). The data is there. The classification at branch-creation time poisoned everything downstream.

The same misfire applies to any fresh EVA where `OnCrewOnEva` triggers the branch path and the new EVA kerbal's vessel has not yet had its `orbit` initialized by KSP. Single-vessel pad EVAs that take the auto-record-fresh path are unaffected because they hit a different code path (`PrepareActiveTreeForFreshPostSwitchRecording` builds a snapshot from the live `Vessel` directly, no extrapolator round-trip).

## Root cause

`IncompleteBallisticSceneExitFinalizer` was designed to handle ballistic ascent / coast / re-entry finalization for vessels whose recording stopped mid-flight. Its `NullSolver` allowance was added (per the comments at `IncompleteBallisticSceneExitFinalizer.cs:454-456` and `PatchedConicSnapshot.cs:119-125`) so that destroyed debris and EVA kerbals with no solver could still be classified.

The allowance has two failure modes, only one of which is currently handled:

- **Destroyed vessel:** `NullSolver` because the orbit solver was torn down. `getPositionAtUT` returns the body-frame origin (already-cleared state). Sub-surface guard fires, classification = `Destroyed`. Correct.
- **Freshly-spawned EVA kerbal:** `NullSolver` because the vessel has not yet had its solver initialized (EVA kerbals run on the jetpack motion system, no patched-conic solver). `vessel.orbit` exists but is uninitialized; `getPositionAtUT` returns garbage. Sub-surface guard fires, classification = `Destroyed`. **Wrong** — the vessel is alive, landed at the parent capsule's lat/lon/alt, captured a valid surface snapshot ~30 ms earlier.

The pipeline can't distinguish "solver torn down" from "solver never built" by reading the `Orbit` object alone — both produce a `null`/empty `IDiscoverableObjectsState`, both fall through to the live-orbit fallback, both produce nonsense coordinates.

There are reliable distinguishing signals available, but the fix must not assume
that the fresh EVA vessel already reports a stable `LANDED`/`SPLASHED`
situation. The observed path reached `IncompleteBallisticSceneExitFinalizer` at
all, which means the normal cache fast path did not treat the vessel as a
surface terminal. Today `RecordingFinalizationCacheProducer.TryBuildFromLiveVessel`
checks `TryBuildSurfaceTerminalCache` first, and `BallisticExtrapolator.ShouldExtrapolate`
would also decline `LANDED` / `SPLASHED` / `PRELAUNCH` before the patched-conic
snapshot runs. So the runtime signature is probably "fresh EVA identity with a
transient extrapolatable situation", not simply "fresh EVA is `LANDED`".

1. **The structural-event snapshot.** `OnCrewOnEva` already calls `recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA")` on the parent capsule (`ParsekFlight.cs:6356`) which writes `eventType=EVA flags=1 lat=-0.0961 lon=-74.8274 alt=65.3` to the trajectory. The parent's surface position is the EVA kerbal's spawn position (KSP spawns EVA kerbals at the airlock part's world transform, ~2 m from the capsule).
2. **EVA identity.** `BuildSplitBranchData` stamps `EvaCrewName`, `ParentRecordingId`, and `ParentBranchPointId` on the kerbal child. The live vessel also exposes `isEVA` and `vesselType = EVA`. These are better gates than `vessel.situation` because situation can still be transient during the spawn frame.
3. **Recorded surface evidence.** `ShouldSuppressSubSurfaceDestroyedFromRecordedPoint` already trusts recorded trajectory points when they contradict a `NullSolver + SubSurfaceStart` live-orbit fallback. Bill's child recording eventually has 120 valid points, but the finalizer ran before those child points existed. The missing evidence at that instant is the parent's structural-event surface point.

These signals let us suppress the false `Destroyed` result after the live-orbit
fallback exposes itself as `SubSurfaceStart`.

## Goal

For a fresh EVA branch child, do not apply `SubSurfaceStart -> Destroyed` when recorded surface evidence proves the live-orbit fallback is garbage. Prefer suppressing the false destroyed result at the existing scene-exit finalizer guard over force-writing a terminal state. The normal recorder/unfinished-flight paths can later seal the EVA as `Landed`/`Splashed` once real samples exist.

For all other `NullSolver` cases (destroyed stock debris, decoupled probes that lose their solver, scene-exit on dead vessels) keep today's `Destroyed` classification — those are still real and the existing tests rely on it.

## Invariant to enforce

`NullSolver + SubSurfaceStart` must never mark an EVA branch child as `Destroyed` when a child recorded point or parent EVA structural-event snapshot within the contradiction window proves a valid surface position.

## Proposed implementation

Recommended shape:

### Option A (preferred): extend the existing sub-surface recorded-point suppression guard

Use the existing seam in `IncompleteBallisticSceneExitFinalizer` instead of adding a parallel cache rule. The current flow already matches this bug shape:

1. `TryFinalizeRecording` first calls `BallisticExtrapolator.ShouldExtrapolate` (`IncompleteBallisticSceneExitFinalizer.cs:191-196`). If KSP reports `LANDED` / `SPLASHED`, the method returns false before snapshotting. Therefore Bill's failing path proves `vessel.situation` was transient/extrapolatable at that instant.
2. `PatchedConicSnapshot` returns `NullSolver`, the finalizer samples the garbage live orbit, and `BallisticExtrapolator` returns `ExtrapolationFailureReason.SubSurfaceStart`.
3. Before applying `Destroyed`, `ShouldSuppressSubSurfaceDestroyedFromRecordedPoint` (`IncompleteBallisticSceneExitFinalizer.cs:578-691`) checks for a nearby recorded surface point and returns `false` from finalization when one contradicts the live-orbit fallback.

Why the existing guard likely did not save Bill: it only searches the child recording's own `TrackSections` / `Points` (`TryFindNearestRecordedSurfacePoint`, `IncompleteBallisticSceneExitFinalizer.cs:693-800`). The finalizer fired about 3 ms after branch creation, before the EVA child had recorded its later 120 points, so the child-local search found nothing inside `SubSurfaceRecordedPointContradictionWindowSeconds = 0.5`.

Extend that guard with a parent structural-event fallback:

- Keep the existing child-recorded-point search first. If the child already has a qualifying point, preserve today's behavior and log source names unchanged.
- Only when the child search fails, consider parent evidence for an EVA branch child:
  - `recording.EvaCrewName` is non-empty;
  - `recording.ParentBranchPointId` resolves to a `BranchPoint`;
  - the pre-branch parent is resolved through `BranchPoint.ParentRecordingIds`, not `recording.ParentRecordingId`. In EVA branch children, `ParentRecordingId` points at the sibling continuation child (`ParsekFlight.cs:3951-3965`), so use it only as an EVA topology/sibling identity signal;
  - the parent recording can be resolved from the active/pending tree context or a narrow injected resolver, not a broad global scan;
  - the parent has a `TrajectoryPointFlags.StructuralEventSnapshot` point near the EVA event. Do not call `FlightRecorder.TryFindStructuralEventSnapshotPointForUT` with its default `1e-6` tolerance; the structural snapshot is captured before the deferred branch coroutine, while the child `StartUT` is the later `branchUT`, so use the existing `SubSurfaceRecordedPointContradictionWindowSeconds` window or search around both the branch point UT and child `StartUT`;
  - the parent lookup must be section-aware like `TryFindNearestRecordedSurfacePoint`: for v6+ relative sections, `frames` can contain anchor-local metre offsets in `latitude`/`longitude`/`altitude`, while `bodyFixedFrames` keeps the body-fixed surface copy. Reuse/extract the existing absolute/body-fixed inspection logic rather than scanning the flat `Points` list blindly;
  - the point has finite body/lat/lon/altitude data, body matches the live fallback body when both are known, altitude is above the sub-surface threshold, and `abs(point.ut - startState.ut) <= SubSurfaceRecordedPointContradictionWindowSeconds`.
- Return through the existing suppression path with `recordedPointSource = "parent-structural-eva"` or similar. Do not set `TerminalStateValue`, do not synthesize a `Landed` result, and do not add a new cache terminal.

Do not use `vessel.situation` or `RawVessel.LandedOrSplashed` as the main guard. They are useful diagnostics when settled, but the bug path itself proves situation was not a reliable surface-terminal signal at the failing instant. The trustworthy signals are recording topology (`EvaCrewName` + parent branch links) and recorded surface evidence.

Why this seam is preferred:

- it reuses code already built for `NullSolver + SubSurfaceStart + recorded surface contradiction`;
- it preserves the destroyed-debris path when there is no contradictory recorded evidence;
- it suppresses the bad finalization result instead of prematurely ending an active EVA child recording;
- it keeps the log story coherent: the existing "suppressing sub-surface Destroyed" line remains the authoritative diagnostic.

### Option B: seed the child with a first structural surface point at branch creation

Instead of teaching the finalizer to consult the parent, branch creation could append/queue a first surface point on the EVA child at `evaEventUT`, flagged with `TrajectoryPointFlags.StructuralEventSnapshot`. Then the existing child-local `TryFindNearestRecordedSurfacePoint` would fire unchanged.

This is a reasonable fallback if parent lookup plumbing in the finalizer is awkward, but it is easier to get wrong:

- active-child vs background-child ownership must be handled exactly (`BuildSplitBranchData` already decides which child is the kerbal);
- the active EVA child must not be marked ended;
- child seed generation must preserve the v6 relative-frame contract and not duplicate later first real samples.

### Option C: skip finalization for recordings with too few points

A small guard such as "do not finalize recordings with fewer than N recorded samples" would address the timing problem directly: a 3 ms-old branch child with zero samples is not a meaningful ballistic trajectory.

This needs review before becoming primary:

- the point count must include `TrackSections` and legacy `Points` correctly;
- some real destruction/decouple cases may create very short recordings that still need `Destroyed` classification;
- it is broader than the EVA bug and could leave legitimate unfinished flights without a terminal unless paired with existing unfinished-flight sealing.

If used, make it a separate, well-tested guard with an explicit log reason such as `finalization-deferred-recording-too-short`.

### Option D: cache-producer preemption

The earlier cache-producer preemption idea is now a fallback, not the preferred fix. It can prevent the bad result before `PatchedConicSnapshot`, but it bypasses the existing finalizer suppression seam and would need its own evidence resolver, stale-cache behavior, and logging. Use it only if Option A cannot access the parent structural-event evidence cleanly.

### Rejected shape: situation / `LandedOrSplashed` gate

`vessel.situation == LANDED` / `RawVessel.LandedOrSplashed` is not a reliable discriminator for this bug. If it were true during the failing call, `ShouldExtrapolate` would return false and the NullSolver path would not run. The likely runtime shape is an EVA vessel whose type/recording identity is valid while orbit-derived situation is still transient.

### Rejected shape: surface start state only

Changing only `TryBuildStartStateFromVessel` to return a body-surface state for EVA vessels is insufficient:

- the observed failing path likely did not report a surface-terminal situation, or the cache/finalizer would not have run;
- `BallisticExtrapolator` does not classify zero-velocity surface starts as `Landed`; it initializes to `Orbiting`, treats zero velocity as `DegenerateStateVector`, and otherwise only switches to `Destroyed` for impact/sub-surface cases;
- making this correct would require teaching the extrapolator a new "stationary surface terminal" result, which broadens a ballistic helper that currently owns `Orbiting`/`Destroyed` outcomes.

### Rejected shape: branch-time terminal seed only

Setting `TerminalStateValue = Landed` / `ExplicitEndUT = evaEventUT` during `CreateSplitBranch` is also insufficient:

- the active EVA child must keep recording the kerbal's walk, so branch creation must not mark it ended;
- a terminal seed is less precise than recorded-point contradiction evidence;
- adding a general sticky-terminal sentinel would be a larger behavior change than suppressing this specific false destroyed finalization.

## Stale-cache cleanup

`IncompleteBallisticSceneExitFinalizer.cs:282-310` already has `ClearStaleDestroyedTerminalForResume` for stale `Destroyed` verdicts. The preferred fix should prevent new stale verdicts by suppressing the bad result before it is applied. If existing sessions may already have a poisoned EVA child, add a narrow cleanup path gated by the same EVA topology + recorded surface evidence checks. Do not relax the destroyed skip globally.

## Tests

xUnit (`Source/Parsek.Tests/`):

- Extend `SceneExitFinalizationIntegrationTests` near the existing `TryCompleteFinalizationFromPatchedSnapshot_NullSolver_FreshRecordedPointSuppressesSubSurfaceDestroyed` coverage:
  - child EVA branch recording has `EvaCrewName`, `ParentRecordingId`, `ParentBranchPointId`, but no qualifying child points;
  - branch point parent lookup resolves through `ParentBranchPointId -> BranchPoint.ParentRecordingIds`, not the child's `ParentRecordingId`;
  - parent recording has a flagged structural `EVA` point near the EVA event; the test should prove the explicit 0.5 s contradiction window works when exact `1e-6` structural lookup would miss;
  - `NullSolver + SubSurfaceStart` returns `built=false`, preserves `extrapolationFailureReason=SubSurfaceStart`, logs `suppressing sub-surface Destroyed`, and uses source `parent-structural-eva`.

- Negative suppression guards:
  - non-EVA child with parent structural point still classifies `Destroyed`;
  - EVA child whose `ParentRecordingId` points at the sibling child still resolves the pre-branch parent through the branch point;
  - EVA child with stale parent structural point outside the 0.5 s window still classifies `Destroyed`;
  - body mismatch or non-finite parent coordinates still classifies `Destroyed`;
  - v6 relative parent section uses `bodyFixedFrames`, not local-offset `frames`/flat `Points`;
  - existing child recorded point still wins over parent fallback and preserves current log source behavior.

- Add a `RecordingFinalizationCacheProducerTests` regression for the observed acceptance path:
  - the default finalizer suppression causes `TryBuildFromLiveVessel` to decline or fail safely instead of accepting a `Fresh` cache with `TerminalState=Destroyed`;
  - no `Refresh accepted ... terminal=Destroyed` / newly-classified count is emitted for the EVA child.

- If Option B is chosen instead, extend `SplitEventDetectionTests` / branch creation tests to prove the EVA child gets exactly one structural seed point and no terminal/end UT mutation.

- If Option C is added, create focused tests for zero-point/one-point recordings and at least one regression case where a short real destroyed recording still finalizes.

In-game runtime test (`Source/Parsek/InGameTests/RuntimeTests.cs`, scene `FLIGHT`):

- `EvaKerbalGhostHasVesselSnapshot` - load a 1-crew capsule, EVA, scene-exit, then enter Watch mode on the resulting recording's EVA child. Assert:
  - Bill Kerman ghost spawns with `parts > 0`;
  - vessel snapshot is non-null;
  - no `Spawn suppressed for #N "Bill Kerman": no vessel snapshot` line appears in the recent log buffer.

xUnit can pin the finalizer suppression decision, but it cannot reproduce KSP's real fresh-EVA spawn timing where the vessel has an uninitialized `Orbit`.

### Logging

Keep the existing suppression log as the primary diagnostic:

```
TryFinalizeRecording: suppressing sub-surface Destroyed for '{recId}'
because a nearby recorded surface point contradicts the live-orbit fallback ...
source=parent-structural-eva ...
```

For this fixed path, the `PatchedSnapshot` solver-unavailable warning may still appear because Option A suppresses after the snapshot/extrapolator result. `BallisticExtrapolator` may also emit its generic `Start rejected: sub-surface state ... classifying recording as Destroyed` message at `Verbose` level because the finalizer calls it with `warnOnSubSurfaceStart: false`.

The important invariant is narrower: the rec-scoped `LogSubSurfaceDestroyedClassificationOnce` warning and the cache-producer `Refresh accepted ... terminal=Destroyed` / newly-classified summary must not appear for the EVA child.

If Option C's too-short-recording guard is implemented, log it separately with recording id, point counts, owner/reason, and UT age so it is distinguishable from the recorded-surface contradiction path.

## Non-goals

- No change to `PatchedConicSnapshot.FailureReason` semantics. NullSolver still means "no patched-conic chain available", still surfaces to all current callers.
- No change to the destroyed-vessel classification path. The sub-surface guard stays. The Extrapolator's existing tests stay green.
- No new `RecordingFormatVersion` bump. Terminal state is a runtime field; no on-disk schema changes.
- No change to ghost map presence, Watch eligibility, or playback rendering beyond removing the upstream cause that produces `parts=0` ghosts.
- No fix for the orphan recording problem (the second EVA's data loss). That's `plan-eva-deferred-autorecord-orphan`, separate PR.

## Key files

- `Source/Parsek/PatchedConicSnapshot.cs:113-136` — where NullSolver originates and is rate-limited
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:282-310` — `ClearStaleDestroyedTerminalForResume` precedent for the same bug shape
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:345-377` — `LogSubSurfaceDestroyedClassificationOnce`, the `WARN` site that fires for Bill
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:452-508` — NullSolver allowance + bail-out logic for non-NullSolver failures
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:570-691` — existing `ShouldSuppressSubSurfaceDestroyedFromRecordedPoint` guard to extend
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:693-800` — child-local recorded surface point search
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:1571-1597` — `TryBuildStartStateFromVessel`, the live-orbit fallback that produces the garbage state
- `Source/Parsek/RecordingFinalizationCacheProducer.cs:166-215` - cache path that currently accepts default-finalizer `Destroyed` results
- `Source/Parsek/BallisticExtrapolator.cs:100` — `SubSurfaceDestroyedAltitude = -100.0` constant
- `Source/Parsek/BallisticExtrapolator.cs:128-156` — `ShouldExtrapolate`, proving surface situations do not reach NullSolver snapshotting
- `Source/Parsek/BallisticExtrapolator.cs:195-236` — sub-surface guard that fires
- `Source/Parsek/FlightRecorder.cs:7722-7724` — v6 relative-frame local-offset rewrite of `latitude`/`longitude`/`altitude`
- `Source/Parsek/FlightRecorder.cs:8567-8578` — body-fixed shadow frames retained for relative sections
- `Source/Parsek/FlightRecorder.cs:9051-9085` — structural-event snapshot point lookup helper
- `Source/Parsek/ParsekFlight.cs:3907-3968` - `BuildSplitBranchData`, where EVA child identity is assigned
- `Source/Parsek/ParsekFlight.cs:4034-4169` - `CreateSplitBranch`, where active/background child ownership and recording start happen
- `Source/Parsek/ParsekFlight.cs:4692-4705` - `DeferredEvaBranch` one-frame delay and later `branchUT`
- `Source/Parsek/ParsekFlight.cs:6356` — `recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA")` is where the parent's surface position gets captured
- `Source/Parsek/ParsekFlight.cs:6364` — `DeferredEvaBranch` is launched, branch construction follows
- `Source/Parsek.Tests/RecordingFinalizationCacheProducerTests.cs` - cache acceptance regression coverage
- `Source/Parsek.Tests/SceneExitFinalizationIntegrationTests.cs:1264-1452` - existing NullSolver recorded-point suppression coverage to extend
- `Source/Parsek.Tests/SplitEventDetectionTests.cs:287-365` - existing EVA branch child identity tests to extend
- `logs/2026-05-13_2337_eva-kerbals-missing/KSP.log` lines 57189-57196 — full reproducer trace for Bill

## Open questions for reviewers

1. What is the cleanest tree-local resolver for `ParentBranchPointId -> BranchPoint.ParentRecordingIds -> parent Recording` inside `ShouldSuppressSubSurfaceDestroyedFromRecordedPoint`? Prefer an injected/narrow resolver over scanning global committed recordings.
2. Should the parent structural lookup search around the child `StartUT`, the branch point UT, the original EVA event UT if available, or all of them within the existing 0.5 s contradiction window?
3. Should a minimum-point-count finalization deferral be added in addition to the EVA parent-evidence fallback, or is that too broad for this PR?
4. What exact `vessel.situation` does KSP report for Bill in the failing 3 ms window? Treat this as diagnostic evidence only, not as a proposed gate.
5. If existing saves already contain a poisoned EVA `Destroyed` terminal, should this PR include a narrow cleanup path using the same parent/child recorded surface evidence?
