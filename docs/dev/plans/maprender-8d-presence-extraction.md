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

### 8d.1 - relocate the body (PURE MOVE, no gate)
Move the ~660-line `CheckPendingMapVessels` body and the six presence dictionaries
(`pendingMapVessels`, `lastMapOrbitByIndex`, `stateVectorOrbitTrajectories`, `stateVectorCachedIndices`,
`soiGapStateVectorExpectedBodies`, `chainMapOwner`) + `terminalMapRetentionLoggedIds` +
`nextMapOrbitUpdateTime` into `GhostMapPresence.UpdateFlightMapGhostLifecycle(double currentUT,
LoopUnitSet loopUnits)`.

**Decision (2026-06-05): pure move, NOT a gated dispatch.** The original sketch kept the legacy body as
a `mapRenderDirectorDrive` gate-off fallback, but the plan analysis showed the body is already a thin
orchestrator over `GhostMapPresence.*` statics (every proto/KSP mutation already routes through them; the
only engine read is `CurrentLoopUnits`; the six dicts are touched ONLY inside `ParsekPlaybackPolicy.cs`,
zero external sites). So the relocation is a FAITHFUL COPY with no logic change. A gate would toggle
between two behaviorally identical paths (validating nothing) at the cost of ~655 lines of duplication
held in sync until 8e, and gate-off could not stay literally untouched anyway (the dicts move, so the
legacy body's dict accesses would be redirected regardless). Therefore 8d.1 is a no-behavior-change
relocation (like 8d.0 / 8b.0): cut the body to `GhostMapPresence`, the seam ALWAYS calls it, no gate
branch, `CheckPendingMapVessels` deleted from the policy.

Mechanics:
- The six dicts + 2 scalars + the `PendingMapVessel` struct move to `GhostMapPresence` as `internal
  static` members (`flight*`-prefixed, matching the existing `trackingStation*` TS twins). They become
  `internal` (not `private`) so the still-in-policy enqueue tail + teardowns (8d.2) reach them directly
  during the 8d.1->8d.2 window.
- The helpers that touch only these dicts move with the body and become static:
  `RemovePreviousChainMapVessel`, `PruneTerminalMapRetentionLogKeys`, `ResolveMapPresenceSampleUT`,
  `TryGetMapOrbitKey`, `IsMaterializedForMapPresence`. Already-static helpers
  (`ShouldRunMapOrbitReseed`, `TryResolveTerminalFallbackMapOrbitUpdate`,
  `ShouldRetainMapPresenceForTerminalRealSpawn`) co-locate.
- `loopUnits` is supplied as `engine.CurrentLoopUnits` (the EXACT source the body used). The dispatcher
  must NOT substitute the scene's cached `LoopUnits` (the `MissionLoopUnitBuilder.Build` output pushed
  via `SetFrameInputs`), which is a different source per the integration contract. Add a policy
  pass-through (e.g. `ParsekPlaybackPolicy.CurrentLoopUnitsForPresence => engine.CurrentLoopUnits`) and
  have `MapViewScene.DriveMapPresence` call `GhostMapPresence.UpdateFlightMapGhostLifecycle(currentUT,
  policy.CurrentLoopUnitsForPresence)`.
- The enqueue tail (`HandleGhostCreated`) and teardowns (`HandleGhostDestroyed`,
  `HandleAllGhostsDestroying`, `Dispose`) STAY in the policy (that is 8d.2); they redirect their dict
  references to the moved `GhostMapPresence.flight*` fields. Add a `ClearFlightMapPresenceState()` for
  the bulk-clear teardowns.
- ERS/ELS: the body reads `RecordingStore.CommittedRecordings`; confirm `GhostMapPresence` is in
  `scripts/ers-els-audit-allowlist.txt` (it almost certainly already is, since the resolver statics it
  already hosts read that collection). `GrepAuditTests` is the backstop.

This is still the riskiest slice (a 655-line faithful cut-paste touching proto create/destroy,
state-vector caching, SOI-gap, chain ownership), so it ships on a HARD clean-context review + green
tests. No in-game A/B gate is needed (behavior is unchanged by construction), though the maintainer
still playtests the merged build (Duna One looped re-aim + a non-looped Mun mission through landing)
as the standard post-merge check, watching for any presence regression.

### 8d.2 - enqueue + teardown ownership (DONE - PURE MOVE, no gate)
Extract the PRESENCE portions of the two mixed-concern engine-event handlers out of the policy into
`GhostMapPresence` static methods. Both handlers do presence work AND a non-presence concern, so only
the presence half moves; the policy stays the engine-event subscriber (its documented role) and keeps
the subscription wiring + the non-presence concerns.

- `HandleGhostCreated` = camera auto-follow (`TryAutoFollowChainSeamSpawn`, STAYS) + the map-presence
  enqueue (MOVED). New `GhostMapPresence.HandleFlightGhostCreatedMapPresence(GhostLifecycleEvent evt,
  GhostPlaybackLogic.LoopUnitSet loopUnits)`; `engine.CurrentLoopUnits` becomes the `loopUnits` param.
  The policy handler becomes `TryAutoFollowChainSeamSpawn(evt);
  GhostMapPresence.HandleFlightGhostCreatedMapPresence(evt, engine.CurrentLoopUnits);`.
- `HandleGhostDestroyed` = the Verbose log + `heldGhosts.Remove` (policy soft-cap state, STAYS) + the
  map-presence teardown (MOVED). New `GhostMapPresence.HandleFlightGhostDestroyedMapPresence(int index)`.
- `HandleAllGhostsDestroying` + `Dispose` already delegate via `ClearFlightMapPresenceState()` (8d.1), so
  they are unchanged. The engine-event subscription/unsubscription wiring stays in the policy.
- `ShouldDeferLoopShiftedMapPresence` stays `internal static` in the policy (it has `RuntimePolicyTests`
  callers); the moved create method calls it cross-class.

Faithful copy (empty code-only diff on both moved blocks under the permitted edits: `engine.CurrentLoopUnits`
-> param, `evt.Index` -> `index`, stripped redundant `GhostMapPresence.` self-qualifiers, cross-class
`ShouldDeferLoopShiftedMapPresence` qualification). No behavior change, no gate. Source-gate tests added.
Build clean; full suite green (13418).

### 8d.3 - decompose + pure-extract (DONE - behavior-preserving)
Decomposed `UpdateFlightMapGhostLifecycle` (~655 lines) into three named `private static void` pass
methods + an orchestrator, and extracted the trivial pure predicates. Note: most decision logic was
ALREADY delegated to extracted pure helpers (`ResolveMapPresenceSampleUT`,
`IsTerminalOrbitSynthesisSafeForLoopMember`, `ResolveMapPresenceGhostSource`, `IsStateVectorGhostSource`,
`TryGetMapOrbitKey`, `ShouldRunMapOrbitReseed`, `ShouldDeferLoopShiftedMapPresence`, etc.), so the new
pure-test surface was small; the main deliverable was the structural decomposition.

- Orchestrator: `RunFlightMapDeferredCreatePass(currentUT, loopUnits)` (Pass 1, always); then the gate +
  preamble stay INLINE (the `mapReseedTimerElapsed`/`mapReseedHeadLeftSegment` computation, the
  `ShouldRunMapOrbitReseed` early-return, `nextMapOrbitUpdateTime`, the `committed == null` early-return,
  `PruneTerminalMapRetentionLogKeys`) so BOTH early-returns still skip Pass 2 AND Pass 3; then
  `RunFlightMapOrbitReseedPass(currentUT, loopUnits, committed)` (Pass 2 body) and
  `RunFlightMapStateVectorUpdatePass(currentUT, loopUnits, committed)` (Pass 3 body).
- Each pass body is line-for-line identical to its original location (verified against HEAD).
- Two pure predicates extracted (`internal static bool`, unit-tested): `IsMapCreateAcceptedSource`
  (Pass-1 create-accept: `Segment || IsStateVectorGhostSource || TerminalOrbit || EndpointTail`) and
  `IsSegmentBearingGhostSource` (Pass-3: `Segment || TerminalOrbit || EndpointTail`). Correct sites
  verified (the state-vector-inclusive one at create-accept, the segment-bearing-only one in Pass 3).

No behavior change, no gate. Source-gate decomposition test + predicate truth-table tests added. Build
clean; full suite green (13431). This completes the 8d presence migration; the flight map-presence
lifecycle now lives entirely in `GhostMapPresence` with the policy as the thin engine-event subscriber.

### 8e - delete legacy + drop the gate (final, shared with the rest of the cutover)
Delete the legacy in-policy presence body, the autonomous Driver walk, the
`activeLegRecordings` / `ghostsWithSuppressedIcon` legacy fallbacks, and the grace fields; grep-audit no
readers remain; remove the `mapRenderDirectorDrive` gate so the Director pipeline is the single default.

## Validation discipline
Every behavioral slice (8d.1 onward) is in-game gated by the maintainer before merge: build, deploy,
verify the deployed DLL is the one just built (size+mtime + a UTF-16 log-string signature), run the two
reference missions with the gate on, then toggle the gate off and confirm byte-identical legacy
behavior. 8d.0 is structural (no behavior change), so it ships on review + green tests like 8b.0.
