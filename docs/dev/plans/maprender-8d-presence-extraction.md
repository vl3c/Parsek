# Phase 8d - map-presence ownership migration into the scene adapter

*Sub-plan of the map/TS render cutover (`docs/dev/plans/maprender-rewrite-status.md`,
`docs/dev/plans/map-ts-render-rewrite-phases.md`). Phases 8a-8c are merged: the Director now owns the
StockConic icon/line drive (8a + Cat-1), the TracedPath polyline leg ownership (8b), and the
marker / icon-suppression decision (8c), all behind the `mapRenderDirectorDrive` gate. Phase 8d
migrates the remaining big legacy surface, the ghost MAP-PRESENCE lifecycle, behind the same gate so
that 8e can delete the legacy path and drop the gate.*

## What 8d migrates

The ghost map-presence lifecycle is the last large autonomous surface the Director does not own. Today
it lives in `ParsekPlaybackPolicy.CheckPendingMapVessels` (about 660 lines) plus six presence
dictionaries (`pendingMapVessels`, `lastMapOrbitByIndex`, `stateVectorOrbitTrajectories`,
`stateVectorCachedIndices`, `soiGapStateVectorExpectedBodies`, `chainMapOwner`), an enqueue tail, and a
scene-change teardown. The flight controller drives it once per playback frame via a direct
`policy.CheckPendingMapVessels(...)` call. Tracking-station presence is driven separately through
`GhostMapPresence.UpdateTrackingStationGhostLifecycle`.

The migration target is `GhostMapPresence` (the existing ProtoVessel-lifecycle owner), reached through
the `IGhostMapScene` adapter so the flight host stops calling the policy directly and the presence body
moves out of `ParsekPlaybackPolicy` into the render layer.

## Hard constraint - presence does NOT gate on the director drive

The presence tick MUST run regardless of `mapRenderDirectorDrive` / `IsDirectorDriveActive`. The
Director deliberately SKIPS re-aim and overlap members (the shadow can only resolve a single faithful
pid->recording mapping); those ghosts still need their map presence created and torn down every frame.
Gating presence on the director drive would silently drop them from the map. The gate in 8d selects
only WHERE the identical presence work runs (legacy in-policy vs migrated in-GhostMapPresence), never
WHETHER it runs.

## Slicing

### 8d.0 - adapter presence seam (DONE - no behavior change)
Route the flight per-frame presence tick through the scene adapter instead of calling the policy
directly. The `CheckPendingMapVessels` body and the six dictionaries stay exactly where they are; only
the call path changes.

- `IGhostMapScene.DriveMapPresence(double currentUT)` - new adapter member.
- `GhostMapSceneBase` declares it `abstract` (the FLIGHT and TS bodies differ).
- `MapViewScene` overrides it as `policy?.CheckPendingMapVessels(currentUT)`, with the policy injected
  once at init via `SetPresenceDriver(policy)` (same injected-ref style as `SetFrameInputs`).
- `TrackingStationScene` overrides it (delegating to `UpdateTrackingStationGhostLifecycle(LoopUnits)`)
  for compile symmetry; NOT yet routed from any TS caller.
- `ParsekFlight` calls `mapViewScene.DriveMapPresence(Planetarium.GetUniversalTime())` in the same
  per-frame slot the direct call used (between `RetryHeldGhostSpawns()` and the shadow-driver block).

Byte-identical: same method, same argument, same execution slot. `policy?.` cannot diverge from the old
unconditional `policy.` because `SetPresenceDriver` runs unconditionally at init before any frame. No
director gate added. Source-gate test `MapPresenceSeamTests` locks the host off the direct call.

### 8d.1 - relocate the body (gated, legacy as gate-off fallback)
Move the ~660-line `CheckPendingMapVessels` body and the six presence dictionaries into
`GhostMapPresence.UpdateFlightMapGhostLifecycle`. Under `mapRenderDirectorDrive` the seam dispatches to
the migrated body; with the gate off it dispatches to the legacy in-policy body (kept verbatim as the
fallback, deleted in 8e). This is the riskiest slice: the body owns proto creation/destruction,
state-vector orbit caching, SOI-gap handling, and chain map ownership. Expect multiple in-game
validation cycles (looped re-aim "Duna One" + a non-looped Mun mission through landing), watching for
any presence regression: a ghost that should be on the map vanishing, a stale ghost that should be torn
down lingering, or a state-vector / SOI-gap glitch. Gate OFF must stay byte-identical.

### 8d.2 - enqueue + teardown ownership
Move the presence enqueue tail and the scene-change teardown into the same migrated owner, behind the
same gate, legacy as the gate-off fallback.

### 8d.3 - decompose + pure-extract
Once the body lives in the render layer, split it into named sub-passes and extract the pure decision
predicates (eligibility, retire, state-vector refresh) as `internal static` helpers with unit tests, so
the migrated body is covered the way the rest of the pipeline is.

### 8e - delete legacy + drop the gate (final, shared with the rest of the cutover)
Delete the legacy in-policy presence body, the autonomous Driver walk, the
`activeLegRecordings` / `ghostsWithSuppressedIcon` legacy fallbacks, and the grace fields; grep-audit no
readers remain; remove the `mapRenderDirectorDrive` gate so the Director pipeline is the single default.

## Validation discipline
Every behavioral slice (8d.1 onward) is in-game gated by the maintainer before merge: build, deploy,
verify the deployed DLL is the one just built (size+mtime + a UTF-16 log-string signature), run the two
reference missions with the gate on, then toggle the gate off and confirm byte-identical legacy
behavior. 8d.0 is structural (no behavior change), so it ships on review + green tests like 8b.0.
