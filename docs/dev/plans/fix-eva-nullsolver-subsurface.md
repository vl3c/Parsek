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

There are two reliable distinguishing signals available at branch-creation time:

1. **The structural-event snapshot.** `OnCrewOnEva` already calls `recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA")` on the parent capsule (`ParsekFlight.cs:6356`) which writes `eventType=EVA flags=1 lat=-0.0961 lon=-74.8274 alt=65.3` to the trajectory. The parent's surface position is the EVA kerbal's spawn position (KSP spawns EVA kerbals at the airlock part's world transform, ~2 m from the capsule).
2. **`Vessel.situation` and `vessel.isEVA`.** A freshly-spawned EVA kerbal has `situation = LANDED` and `isEVA = true`. A destroyed vessel does not survive long enough for the finalizer to read these reliably, but at branch-creation time the EVA vessel is alive and these properties are populated.

Either signal lets us short-circuit before the live-orbit fallback runs.

## Goal

For the EVA-branch-creation pathway only, treat a `NullSolver` snapshot failure as "landed at the parent's structural-event position" rather than "destroyed". The recording terminal is `Landed`, not `Destroyed`, and the vessel snapshot remains valid for playback rendering.

For all other `NullSolver` cases (destroyed stock debris, decoupled probes that lose their solver, scene-exit on dead vessels) keep today's `Destroyed` classification — those are still real and the existing tests rely on it.

## Invariant to enforce

An EVA-kerbal recording whose parent's structural-event snapshot resolved to a valid surface position must never have its vessel snapshot blocked from playback by an Extrapolator `SubSurfaceStart` classification.

## Proposed implementation

Three viable shapes — reviewers should weigh in on which is preferred.

### Option A (preferred): make the branch-creation path seed the new recording's terminal explicitly

When `OnCrewOnEva` synthesizes the `bgChild` EVA recording (or `activeChild`, depending on which side the EVA kerbal lands on — confirm by re-reading the existing branch creation path at `ParsekFlight.cs:6363` -> `DeferredEvaBranch`), set the recording's terminal directly from the EVA kerbal's live `Vessel` state:

- `vessel.situation` -> `TerminalState.Landed` for `LANDED` / `SPLASHED`, `Orbiting` for orbital EVAs (rare but possible)
- terminal lat/lon/alt from `vessel.latitude`, `vessel.longitude`, `vessel.altitude` (these are surface coordinates that work without an Orbit solver)
- terminal UT from `evaEventUT`

This seeded terminal is then cached by `FinalizerCache.Refresh` BEFORE `TryFinalizeRecording` runs and BEFORE the NullSolver fallback gets a chance to compute garbage. The Extrapolator's sub-surface guard never sees the recording because its terminal is already non-null.

Concretely: extend the EVA branch creation path with a `SeedEvaChildTerminalFromVessel(Recording evaChild, Vessel evaVessel, double evaEventUT)` helper. Call it during branch construction, before the new recording is handed to `FinalizationCache.Refresh` for the first time.

Advantage: minimal blast radius. Only the EVA branch creation site changes. The Extrapolator, the live-orbit fallback, and the NullSolver handling all stay exactly as they are — they remain correct for the destroyed-vessel case they were designed for.

Risk: must verify the seeded terminal is not later overwritten by a periodic `FinalizerCache.Refresh` that re-runs `TryFinalizeRecording`. Spot-check `IncompleteBallisticSceneExitFinalizer.cs` for "already classified" short-circuits — there's a guard at `LogAlreadyClassifiedDestroyedSkip` (`IncompleteBallisticSceneExitFinalizer.cs:322`), need to confirm a similar early-exit exists for `terminal=Landed` and that it kicks in before the snapshot pipeline.

### Option B: teach the Extrapolator to use the parent's structural-event position as the start state for EVA children

When `IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshot` hits `NullSolver` on a recording with `ParentBranchPointId != null && parentBranchPoint.Type == BranchPointType.EVA`:

1. Read the parent recording's last `EVA` structural-event snapshot at `evaEventUT`.
2. Build the start state from that surface position via `body.GetWorldSurfacePosition(lat, lon, alt)` instead of `vessel.orbit.getPositionAtUT`.
3. Classify as `Landed` (or whatever the situation flag in the structural event implies).

Advantage: works without changing the branch-creation site. Recovery happens at the finalization seam.

Risk: more surface area to test. Every NullSolver path through the finalizer now branches on parent-EVA vs not. The new code path needs to handle: parent recording missing, structural event missing, lat/lon/alt out of range, body lookup failure, etc.

### Option C: detect "live EVA kerbal" in `TryBuildStartStateFromVessel`

Add a precondition in `TryBuildStartStateFromVessel` (`IncompleteBallisticSceneExitFinalizer.cs:1571`):

```
if (vessel != null && vessel.isEVA && vessel.LandedOrSplashed)
{
    // Fresh EVA kerbal — vessel.orbit is unreliable. Use surface coordinates
    // resolved from the live vessel transform.
    return TryBuildSurfaceStartStateFromVessel(vessel, commitUT, out startState);
}
```

`TryBuildSurfaceStartStateFromVessel` calls `vessel.mainBody.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, vessel.altitude)`, packages it into a `BallisticStateVector` with zero velocity. The Extrapolator's sub-surface guard then sees `alt ≈ 65 m` not `-599652 m` and doesn't fire.

Advantage: completely localized; one new check + one new helper.

Risk: every EVA recording now runs through the Extrapolator's surface-scan + horizon-cap logic even though the recording is trivially "landed at this spot". Probably classifies correctly (terminal = `Landed`) but wastes cycles on a degenerate state vector (zero velocity). Worth verifying via the existing pure-static `BallisticExtrapolatorTests`.

## Stale-cache cleanup (orthogonal but related)

`IncompleteBallisticSceneExitFinalizer.cs:280-310` already has `ClearStaleDestroyedVerdictForResume` for exactly this kind of "transient NullSolver fingerprinted as Destroyed" case — it's used by the Re-Fly resume path. The proposed fix should call into the same helper if it exists and is exported, or extract the existing logic into a shared utility so both paths converge.

Worth a reviewer eye: should the EVA branch creation path proactively call `ClearStaleDestroyedVerdictForResume` (or its inverse, a "this terminal is authoritative, don't recompute" sentinel) on the new EVA child to make absolutely sure no later cache refresh re-classifies it?

## Tests

xUnit (`Source/Parsek.Tests/`):

- `EvaChildTerminalSeedingTests.cs` — pure-static test for the new `SeedEvaChildTerminalFromVessel` (Option A) or the structural-event-driven start state (Option B). Cases:
  - LANDED EVA at lat/lon/alt -> terminal = Landed at that position
  - SPLASHED EVA -> terminal = Landed (or whatever the existing Splashed mapping is)
  - Orbital EVA (extremely rare) -> seeded terminal is Orbiting or falls through to the existing patched-conic pipeline (the parent has a valid solver in this case)
  - Parent structural event missing -> regression-safe fallback (return false, let the existing pipeline run, log a verbose diagnostic)

- `IncompleteBallisticSceneExitFinalizerNullSolverEvaTests.cs` — assert that NullSolver + parent-EVA-branch + valid structural-event input produces `terminal=Landed`, not `terminal=Destroyed`, and does NOT log `Start rejected: sub-surface state`.

- Existing `IncompleteBallisticSceneExitFinalizerTests` and `BallisticExtrapolatorSubSurfaceGuardTests` (or whatever the current names are) must still pass — the destroyed-debris NullSolver path remains identically classified as `Destroyed`. Regression guards.

In-game runtime test (`Source/Parsek/InGameTests/RuntimeTests.cs`, scene `FLIGHT`):

- `EvaKerbalGhostHasVesselSnapshot` — load a 1-crew capsule, EVA, scene-exit, then enter Watch mode on the resulting recording's EVA child. Assert:
  - Bill Kerman ghost spawns with `parts > 0`
  - Vessel snapshot is non-null
  - No `Spawn suppressed for #N "Bill Kerman": no vessel snapshot` line in the recent log buffer

xUnit can't replicate this because the bug needs KSP's real EVA spawn sequence (Vessel created with uninitialized Orbit).

### Logging

The current `WARN [PatchedSnapshot] SnapshotPatchedConicChain: vessel=<X> solver unavailable` line at `PatchedConicSnapshot.cs:134` should keep firing for both destroyed and fresh-EVA cases — it's the diagnostic that tells us the snapshot couldn't compute. Add a separate `INFO` line at the new EVA-aware short-circuit:

```
ParsekLog.Info("Extrapolator",
    "EvaChildTerminalSeeded: rec={recId} parentRec={parentRecId} " +
    "body={body} lat={lat:F4} lon={lon:F4} alt={alt:F1} terminal=Landed " +
    "reason=fresh-EVA-no-solver");
```

Suppress the existing `Start rejected: sub-surface state ... classifying recording as Destroyed` `WARN` for the EVA short-circuited case — that line currently fires once per recording (deduped by `subSurfaceDestroyedClassificationLogs.Add(key)`) and a reviewer correlating logs to disk state will be confused if we keep it.

## Non-goals

- No change to `PatchedConicSnapshot.FailureReason` semantics. NullSolver still means "no patched-conic chain available", still surfaces to all current callers.
- No change to the destroyed-vessel classification path. The sub-surface guard stays. The Extrapolator's existing tests stay green.
- No new `RecordingFormatVersion` bump. Terminal state is a runtime field; no on-disk schema changes.
- No change to ghost map presence, Watch eligibility, or playback rendering beyond removing the upstream cause that produces `parts=0` ghosts.
- No fix for the orphan recording problem (the second EVA's data loss). That's `plan-eva-deferred-autorecord-orphan`, separate PR.

## Key files

- `Source/Parsek/PatchedConicSnapshot.cs:113-136` — where NullSolver originates and is rate-limited
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:282-310` — `ClearStaleDestroyedVerdictForResume` precedent for the same bug shape
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:345-377` — `LogSubSurfaceDestroyedClassificationOnce`, the `WARN` site that fires for Bill
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:452-508` — NullSolver allowance + bail-out logic for non-NullSolver failures
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:1571-1597` — `TryBuildStartStateFromVessel`, the live-orbit fallback that produces the garbage state
- `Source/Parsek/BallisticExtrapolator.cs:100` — `SubSurfaceDestroyedAltitude = -100.0` constant
- `Source/Parsek/BallisticExtrapolator.cs:195-236` — sub-surface guard that fires
- `Source/Parsek/ParsekFlight.cs:6356` — `recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA")` is where the parent's surface position gets captured
- `Source/Parsek/ParsekFlight.cs:6363` — `DeferredEvaBranch` is launched, branch construction follows
- `logs/2026-05-13_2337_eva-kerbals-missing/KSP.log` lines 57189-57196 — full reproducer trace for Bill

## Open questions for reviewers

1. Option A vs B vs C — which seam is the right one? Option A localizes to the EVA branch creator and leaves the Extrapolator alone. Option B/C add complexity to the Extrapolator side. My current preference is A.
2. Should the new EVA-aware seed mark the terminal as "authoritative, do not recompute" via a sentinel, or rely on the existing "already-classified" short-circuit pattern? The latter is simpler but couples this fix to that short-circuit's exact semantics.
3. Should we also fix the symmetric pad-EVA case where the EVA kerbal is the *active* recording (no parent in tree)? Currently that path doesn't hit this bug because it doesn't go through the patched-conic snapshot path the same way. Worth confirming the auto-record-EVA-from-pad runtime test (`RuntimeTests.cs:775-861`) catches the regression we'd introduce if we changed it.
4. Stock KSP also has `vesselType = EVA`. Could we gate the short-circuit on `vessel.vesselType == VesselType.EVA` rather than `vessel.isEVA && vessel.LandedOrSplashed`? Need to confirm both flags are set before `OnCrewOnEva` runs.
5. The `ClearStaleDestroyedVerdictForResume` helper exists for Re-Fly resume. Should the proposed fix also call it (or equivalent) so that *if* a stale Destroyed cache entry survives somehow, it gets cleaned up rather than persisted to disk?
