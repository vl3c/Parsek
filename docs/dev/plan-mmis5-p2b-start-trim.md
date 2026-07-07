# Mini-plan: M-MIS-5 P2b - start-trim lift (mid-tree docked-origin shuttle routes)

Status: RATIFIED with the build (this is the P2b mini-plan gate required by
`docs/dev/plan-mmis5-dock-interval-boundary.md` D5 / P2b). Parent plan:
`docs/dev/plan-mmis5-dock-interval-boundary.md` (P1 SHIPPED 2026-07-04; D4
end-flip is the precedent for every span-geometry move here). File:line
references verified against `main @ c99d55f` (2026-07-07).

Stacking note: the parent plan phases P2a (honest detection) before P2b
(acceptance). The `mmis5-p2a-starttrim-detect` branch was never pushed (no
remote branch, no PR), so this branch bases on `main` and FOLDS THE P2a
DETECTOR IN: the fail-closed target of every unsupported shape below IS the
P2a reject (`MidRecordingStartTrimUnsupported = 9`), so acceptance cannot ship
without the detector anyway. Both undocked-start gates are instrumented
(verdict C6 satisfied). If a separate P2a lands first, the conflict is
localized to the same two gate sites.

## 0. Behavior change in one sentence

A route whose tree roots mid-flight (no KSC launch, no start-docked proof) but
whose run begins at a fully recorded docked-origin window (dock at depot A,
load, undock - the undock->undock shuttle profile) is now ACCEPTED: the route
renders and loops `[origin undock .. last dock]`, the origin resolves to the
depot behind that window, and every other newly distinguishable shape fails
closed to the honest status-9 reject instead of the misleading generic 6.

## 1. OQ1 - DECIDED: the lifted window starts at the ORIGIN UNDOCK

Recorded decision (parent plan §8 recommended ORIGIN UNDOCK; ratified here):

- **D4 symmetry.** Rendered content = the in-flight legs; docked stretches are
  excluded at BOTH ends. The end already flipped to the dock (D4); the start
  is the undock. The ghost departs depot A and retires at depot B.
- **Mechanically cheaper.** The origin undock is a STRUCTURAL peel edge (the
  depot-side branch peels there), so the docked interval `[dockA..undockA)`
  ends EXACTLY at the boundary and the start-trim needs only an
  ends-at/before rule plus the origin-undock-child scoping - no `@dock`
  sub-interval participation at the boundary.
- **Origin debit is dispatch-time ledger work, not render work** (the M1
  model): the depot debit does not need the docked loading stretch rendered.
- **Cadence honesty.** Span = `lastDock - originUndock` = the actual transit;
  `DispatchInterval = N x span` matches the physical shuttle period without
  the loading stay padding it (the same honesty D4 bought on the end side).
- **Clock safety.** `RecordedDockUT` (the last dock) sits at the span end,
  end-inclusive - exactly the geometry the D4 R2 test
  (`DockPhaseAtSpanEnd_CrossingFiresOncePerCycle`) already pins.

## 2. The supported shape (and only this shape)

Analysis-side definition, checked by a new pure resolver
`RouteAnalysisEngine.TryResolveMidTreeDockedOrigin` (internal static, directly
tested), consulted ONLY when `IsUndockedStartOrigin(originRec)` is true (KSC
and start-docked-rooted trees never reach it - byte-identity by construction):

- `ordered.Count >= 2` completed connection windows on the source path
  (ordered ascending by DockUT; the M4a ordering already rejects NaN /
  duplicate DockUTs upstream). The FIRST window W1 is the origin candidate;
  the rest are the stops.
- A committed TREE is present (the `AnalyzeRecording` null-tree path cannot
  derive the start-trim - fail closed to 9).
- `W1.UndockUT` finite, `>= W1.DockUT`, and STRICTLY before `ordered[1].DockUT`
  (non-overlap; also guarantees every stop's dock phase lies inside the
  lifted span so `RouteLoopClock.IsDockUTInSpan` can fire it).
- `HasEndpointProof(W1)` (guaranteed by the earlier per-window endpoint-proof
  gate; kept as a defensive check).

Resolver verdicts:
- **Resolved** -> the run is accepted with origin = W1 (subject to all
  remaining gates); `stops = ordered[1..]`.
- **Degenerate family** (>= 2 windows but null tree / overlap / non-finite or
  inverted UndockUT / missing endpoint proof) -> status 9 with a
  `RejectDetail` naming the origin dock UT (the P2a contract).
- **Not the family** (fewer than 2 windows) -> fall through to the existing
  rejects unchanged (genuine undocked start stays status 6; the harvest gate
  keeps its refined reject).

## 3. Seams (the todo bullet's (a)-(e), made concrete)

### (a) Start-side interval exclusion - `RouteBackingMission`

- New `ComputeStartExcludedIntervalKeys(RecordingTree tree, double
  segmentStartUT)` - a SEPARATE method, not a widened signature, so every
  existing `ComputeExcludedIntervalKeys` call site stays byte-identical.
  Mirror of the end-trim (`RouteBackingMission.cs:105-188`):
  - Guards: null tree / NaN / non-positive `segmentStartUT` -> empty set +
    Verbose log (honest whole-segment fallback).
  - `boundary = segmentStartUT + BoundaryEpsilonSeconds` (the SAME `1e-6`
    constant - the symmetric epsilon rule from the parent plan). Exclude every
    selectable node with `EndUT <= boundary` (interval ends at/before the
    origin start), keep a node that merely STARTS at the boundary.
  - Origin-undock-child scoping, mirror of `CollectTerminalUndockChildLegIds`
    (`:603-637`): `CollectOriginUndockChildLegIds(tree, segmentStartUT)` picks
    the `BranchPointType.Undock` BP nearest `segmentStartUT` and excludes
    nodes rooting at its child legs - the depot-A-side peel branch starts AT
    the origin undock (`StartUT == segmentStartUT`, `EndUT > boundary`) and
    would otherwise stay rendered.
- New pure span verifier `TryComputeSelectionSpan(tree, excludedKeys, out
  spanStartUT, out spanEndUT)`: runs the composition +
  `MissionIntervalSelection.ComputeRenderWindows` and returns the min included
  StartUT / max included EndUT across owners - the same quantities the loop
  unit derives. Used by the builder's fail-closed span check (seam (b)); NOT
  called on launch-rooted routes.

### (b) `RouteBuilder` origin-start plumbing

When `analysis.IsMidTreeDockedOrigin` (else everything below keeps
`rootLaunchUT` - byte-identity):

- `spanStartUT = analysis.OriginConnectionWindow.UndockUT` (OQ1) replaces
  `rootLaunchUT` at: `transitDuration` (`RouteBuilder.cs:169`), the CRE-5
  `IsDockUTWithinSpan` check (`:362`), `DispatchWindowEpochUT` (`:406`),
  `NextDispatchUT` (`:409`), `loopAnchorUT` (`:371`), per-stop
  `DeliveryOffsetSeconds` (`:298`).
- `excludedIntervalKeys = ComputeExcludedIntervalKeys(tree, recordedDockUT,
  rootLaunchUT) UNION ComputeStartExcludedIntervalKeys(tree, spanStartUT)`.
  The start-trim lives ONLY in the excluded keys; the member/SourceRef
  derivation (`BuildRouteSourceRefs` -> `ComputeMemberRecordingIds`) is
  UNCHANGED and still covers the whole `[root..dock]` path. Rationale: the
  M-MIS-9 freeze's inverse-direction safety RELIES on SourceRefs
  fingerprinting the pre-boundary path (a pre-origin mutation must flip the
  route off ghost-driving before a stale key string can over-exclude an
  in-span interval; see the M-MIS-9 todo entry). Pre-origin intervals simply
  drop out of the rendered loop unit via the excluded keys.
- The origin window's carrier recording (`analysis.OriginSourceRecording`,
  the dock-merged child at depot A) is FORCE-ADDED to SourceRefs exactly like
  the delivery leaf (`RouteBuilder.cs:646+`): it carries the origin window
  proof and must be revalidation-tracked.
- Origin resolution (`TryResolveRouteOrigin`): new branch after the
  root-proof branch - endpoint built from
  `OriginConnectionWindow.EndpointAtDock.Value` (pid =
  `TransferTargetVesselPid`), label `midtree:pid=<pid>`; `isKscOrigin` stays
  false; cost manifests follow the docked-origin shape (CostManifest = the
  delivery manifest, debited from the depot at dispatch - the M1 contract).
- FAIL-CLOSED span check (mid-tree branch only): after deriving the excluded
  keys, `TryComputeSelectionSpan` must yield `spanStartUT` and
  `recordedDockUT` (within the boundary epsilon), else reject
  `origin-span-mismatch` with an Info log. This converts every weird
  composition shape (e.g. a pre-origin offshoot branch that outlives the
  origin undock and would drag the span start early) into an honest reject
  instead of a wrong-span route.
- New persisted field `Route.RecordedOriginUndockUT = -1.0` (mirror of
  `RecordedDockUT`), sparse in the codec (key `recordedOriginUndockUT`,
  omitted at -1.0, decode default -1.0). Consumed by the freeze prong (d) and
  diagnostics; the loop clock does NOT read it (span geometry is derived).

### (c) Analysis origin gate - `RouteAnalysisEngine`

- Hoist the resolver call ABOVE the non-harvest gate: when Resolved, BOTH
  undocked-start rejects (`:538` and `:673`) are bypassed for this run (the
  docked origin covers the un-launched / un-harvested delivery), and
  `isHarvestOrigin` is NOT set. When Degenerate, return status 9 (both gates
  covered by the single hoisted site - C6). When NotFamily, both gates run
  byte-identically.
- The origin window W1 is dropped from the STOPS and from the
  no-delivery-AND-no-load per-window gate (an empty/parked origin window is
  legitimate - it is the origin, not a stop); it KEEPS the unwitnessed
  inventory-gain gate (fail closed) and keeps contributing its witnessed load
  term to the summed-load / gain-check / flow-closure math (witnessing is
  unchanged; only the debit surface moves from a per-window pickup debit to
  the M1 origin debit).
- Anchor scalars re-point to the first remaining stop so every existing
  scalar consumer sees "the first stop" exactly as today.
- `RouteAnalysisResult` gains `IsMidTreeDockedOrigin`,
  `OriginConnectionWindow`, `OriginSourceRecording`.
- `RouteCreationFormatters`: status-9 text updated (mid-flight starts are now
  supported when the run begins at a fully recorded docked-origin window;
  the remaining 9s are the degenerate shapes); `ResolveOriginIdentity`
  classifies a mid-tree docked origin as Depot.

### (d) M-MIS-9 START-side freeze prong - `ComputeAutoExcludedNewIntervalKeys`

Mirror of prong 2 (`RouteBackingMission.cs:465-497`): when
`route.RecordedOriginUndockUT > 0`, union
`ComputeStartExcludedIntervalKeys(tree, route.RecordedOriginUndockUT)` into
the auto-excluded set (creation-time keys filtered out, same Info-on-change
logging). Prong 1 (base-id) is direction-agnostic and unchanged; the
pre-origin intervals' base ids are known at creation (they sit in the
creation-time excluded set), so a pre-origin `/segN` re-peel of a known
member is re-trimmed by UT, not by key string. A route without a persisted
`RecordedOriginUndockUT` (every existing route) runs the end prong only -
byte-identical. All existing guards (empty creation excluded set, empty
SourceRefs) apply unchanged.

### (e) Span / loop-clock phase re-derivation for a non-launch span start

Audited (D4 precedent): NO clock or span-math edits needed.

- `MissionLoopUnitBuilder` span = `[min trimmed member StartUT, max trimmed
  member EndUT]` (`MissionLoopUnitBuilder.cs:209-217`) - the start-trim moves
  `spanStartUT` to the origin undock exactly the way D4 moved `spanEndUT` to
  the dock. Cadence = `max(LoopIntervalSeconds, span)` shrinks to the transit
  leg - the designed behavior (section 1).
- `RouteLoopClock` phrases every phase relative to `SpanStartUT`
  (`dockPhaseOffset = recordedDockUT - SpanStartUT`,
  `RouteLoopClock.cs:389-457`); the invariant to protect is `RecordedDockUT`
  (and every per-stop dock phase) INSIDE the lifted span - guaranteed by the
  resolver's strict `W1.UndockUT < ordered[1].DockUT` plus CRE-5 on the new
  span, and pinned by a raised-span-start clock test.
- `MissionPeriodicity.UT0` = the same trimmed-member min - re-roots
  automatically. Routes do not phase-lock through the backing mission
  (`RouteBackingMission.cs:58-66`), so the dropped pad-rotation constraint of
  a non-launch span start is inert for routes; the loop-unit test asserts
  cadence stays span-derived.
- `GhostPlaybackLogic.SpanClock` / member windows are already span-start
  relative; per-member clamps handle "starts later" today.

## 4. Failure directions (fail-closed table)

| Shape | Outcome |
|---|---|
| KSC-rooted or start-docked-rooted tree | Gates untouched (resolver never consulted) - byte-identical route |
| Undocked root, < 2 windows | Status 6 (or the harvest-refined reject) - byte-identical |
| Undocked root, >= 2 windows, null tree (`AnalyzeRecording`) | Status 9 |
| Origin window overlaps the next stop (`UndockUT >= next DockUT`), non-finite / inverted UndockUT, missing endpoint proof | Status 9 |
| Supported shape but derived selection span != `[originUndock .. lastDock]` (odd composition, pre-origin offshoot outliving the undock) | Builder reject `origin-span-mismatch` |
| Malformed span inputs at build (NaN, `dock <= originUndock`) | Existing `backing-mission-unresolvable` / CRE-5 `dock-ut-out-of-span` rejects, now over the lifted span |

No silent acceptance widening anywhere else; every reject logs with the
`[Parsek][INFO][Route]` diagnostics the gates already emit.

## 5. Byte-identity pins (constitutional)

- Launch-rooted (KSC) and start-docked-rooted routes: analysis result, route
  fields, codec bytes all unchanged - `RecordedOriginUndockUT` stays -1.0 and
  is OMITTED by the sparse codec; `ComputeExcludedIntervalKeys` /
  `ComputeMemberRecordingIds` signatures and call sites untouched;
  `ComputeStartExcludedIntervalKeys` is never invoked. Pinned by the existing
  `RouteBuilderTests` span/epoch tests, `RouteCodecTests.RoundTrip_Lean_NoSpuriousNodes`,
  and a new explicit pin (section 6).
- The M-MIS-9 freeze on existing routes: end prong only (no
  `RecordedOriginUndockUT`), pinned by a new no-op test.

## 6. Named tests

- `RouteBackingMissionTests.ComputeExcluded_StartTrim_ExcludesPreOriginDock`
  (parent-plan-named headline: pre-origin intervals + the docked stretch
  excluded, in-span intervals kept)
- `RouteBackingMissionTests.ComputeStartExcluded_ExactBoundary_EndAtOriginExcluded_StartAtOriginKept`
  (the epsilon tie, mirror of `Compute_ExactBoundary_StartAtDockExcluded_EndAtDockKept`)
- `RouteBackingMissionTests.ComputeStartExcluded_OriginUndockChildBranch_Excluded`
- `RouteBackingMissionTests.ComputeStartExcluded_BadInputs_EmptySetAndLogs` (Theory)
- `RouteBackingMissionTests.ComputeAutoExcluded_StartProng_NewPreOriginKey_AutoExcluded`
- `RouteBackingMissionTests.ComputeAutoExcluded_NoOriginUndockUT_EndProngOnly`
  (freeze byte-identity pin)
- `RouteAnalysisEngineTests.StartDockedShuttle_WithOriginProof_Eligible`
  (parent-plan-named: the lifted acceptance, origin window out of the stops)
- `RouteAnalysisEngineTests.ShuttleStart_DegenerateShape_EmitsMidRecordingStartTrim`
  (status 9 + RejectDetail names the origin dock UT)
- `RouteAnalysisEngineTests.GenuineUndockedStart_StillStatus6`
- `RouteAnalysisEngineTests.ShuttleStart_HarvestDataPath_AcceptedWithoutHarvestOrigin`
  (gate-2 coverage: modern recordings with complete run manifests)
- `RouteBuilderTests.Build_MidTreeOrigin_SpanIsOriginUndockToDock`
  (transit / epoch / anchor / `RecordedOriginUndockUT`)
- `RouteBuilderTests.Build_MidTreeOrigin_OriginEndpointFromOriginWindow`
- `RouteBuilderTests.Build_MidTreeOrigin_SourceRefsIncludeOriginCarrier`
- `RouteBuilderTests.Build_MidTreeOrigin_SpanMismatch_Rejected`
- `RouteBuilderTests.Build_KscRooted_OriginUndockUTStaysDefault` (byte-identity pin)
- `RouteCodecTests.RoundTrip_RecordedOriginUndockUT_SparseDefaultMinusOne`
- `RouteBackingMissionLoopUnitTests.RouteMission_MidTreeOrigin_SpanStartsAtOriginUndock_TrimmedToDock`
  (the loop-unit-level proof: spanStart == origin undock, spanEnd == dock,
  cadence == transit)
- `RouteLoopClockTests.DockPhase_RaisedSpanStart_CrossingFiresOncePerCycle`
  (seam (e) pin)
- `LogisticsRejectPresentationTests` status-9 text pin update
- In-game `LogisticsShuttleRuntimeTests` (mirrors
  `LogisticsDockBoundaryDeliveryCadenceRuntimeTests`): synthetic mid-orbit-
  rooted tree with an origin window at depot A and a delivery window at depot
  B; real analysis -> builder; asserts Eligible, span `[undockA..dockB]`, and
  two consecutive deliveries one DispatchInterval apart.

## 7. Out of scope (unchanged decisions)

- Isolating a docked A-B stretch inside ONE recording with no undock edge
  (the parent plan's PARKED shape).
- Foreign-partner-side looping (M-MIS-8).
- Round-trip linking (M4c) and inventory escrow consolidation (M4b deferral).
- KSC-rooted trees keep building launch-rooted routes; deriving a shuttle
  sub-route from a launch-rooted tree is future work (the player records the
  recurring run as its own tree, which is the natural shuttle workflow).
- No recorder or proof-capture change: the origin is recognized from the
  recorded connection window; `RouteOriginProof` capture is untouched.
