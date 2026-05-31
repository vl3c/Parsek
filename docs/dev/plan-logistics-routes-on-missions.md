# Supply Route v0: Re-Founding on the Missions Subsystem

Authoritative implementation plan. Builders follow this document. The Missions subsystem is LOCKED; a Supply Route v0 is re-founded as three bound things over a route-owned backing Mission segment held by Logistics.

This plan resolves all six contradictions, three gaps, and the ordering issues surfaced in reconciliation. Where a draft workstream disagreed with the reconciliation, the reconciliation wins and the divergence is called out inline.

---

## DO NOT TOUCH (locked Missions files)

These are consumed read-only. Editing any of them, or changing their contracts, is a hard violation. Before finalizing, `git diff` over this exact set MUST be empty.

- `Source/Parsek/Mission.cs` (data model: construct a logistics-owned instance via the public ctor; never edit the class)
- `Source/Parsek/MissionStore.cs` (call `SetLoopEnabled` only; never insert the route-owned Mission; never edit `NormalizeOneLoopPerTree`)
- `Source/Parsek/MissionLoopUnitBuilder.cs` (call `Build` and let `BuildSignature` run via the passed list; never edit, never add a route-aware overload)
- `Source/Parsek/MissionIntervalSelection.cs` (pass `ExcludedIntervalKeys` into `Build`; never edit `ComputeRenderWindows`)
- `Source/Parsek/MissionStructure.cs` + `MissionThroughLine*` / `MissionComposition*` / `MissionCompositionNode` (read-only structure walk)
- `Source/Parsek/MissionPeriodicity.cs`
- `Source/Parsek/GhostPlaybackLogic.cs` (`LoopUnit`, `LoopUnitSet`, `OwnerByIndex`, `LoopUnitSet` ctor, `TryComputeSpanLoopUT`: read-only; never add a public merge ctor, never change the single-owner `OwnerByIndex` contract)
- `Source/Parsek/GhostPlaybackEngine.cs` (`SetLoopUnits` and per-frame loop mechanics: push into it, never edit it)
- `Source/Parsek/Reaim/*` (re-aim is unused in v0; `bodyInfo=null`; do not wire or edit)

**The ONLY shared-host edits permitted, as new logistics-consumer wiring:**
1. `ParsekFlight.DriveMissionLoopUnits`, `ParsekKSC.DriveMissionLoopUnits`, `ParsekTrackingStation.DriveMissionLoopUnits` (the three push seams).
2. `MissionsWindowUI.cs` Loop-toggle draw/commit site (the mutual-exclusion grey + commit guard).

**Forbidden traps (verified, called out so a builder cannot slip):**
- Do NOT forward-declare `internal Mission BackingMission` on `Route.cs` as a placeholder (loop-render draft proposed this). The route does NOT store a materialized Mission. The Mission is rebuilt on demand and never persisted.
- Do NOT "extend BuildSignature" by editing `MissionLoopUnitBuilder.cs`. Route-backing missions fold into the existing signature automatically because they are appended to the list passed to `Build`.
- Do NOT add `LoopUnitSet`/`OwnerByIndex`/`SetLoopUnits` changes to "solve" same-tree-two-clocks. Mutual exclusion prevents that collision at the toggle.

---

## Kept / Adapted / Discarded

| Disposition | Items |
|---|---|
| **KEPT verbatim** | Route data model core + serialization patterns (`RouteCodec`); route-proof metadata + capture (`RouteProof*`, `RouteProofHasher`); delivery execution, per-resource fill, NO_FLOW gate (`RouteDeliveryPlanner`, `LiveDelivery*`); live delivery endpoint resolution (`RouteEndpointResolver`); funds (`RouteFundsCalculator`); ledger `RouteModule`; ELS `(routeId, cycleId)` idempotency; `RouteStore.RevalidateSources` + the existing `route-proof-hash` drift trip wire; dock/transfer/undock PROOF verification in `RouteAnalysisEngine`; `RouteCandidateFinder` sealed/eligible/dedup gates |
| **ADAPTED** | `RouteBuilder.BuildRoute` (now also emits a backing-mission DEFINITION); `RouteOrchestrator.ProcessOneRoute` (loop-clock crossing fire replaces the self-timer fire gate for loop-routes); `RouteAnalysisEngine`/`RouteCandidateFinder` reframed: the proven run is a Mission segment, geometry no longer scanned bespoke (proof verification unchanged) |
| **DISCARDED** | The separate `logistics-route-replay` branch (`OffsetReplayUnit` / `RouteReplayPlanner`): verified ZERO references in this worktree's `Source/`. "Discard" becomes verify + regression-guard test + doc-record; the user prunes the branch (agents do not delete branches) |

---

## Phase 0: Pre-Work Unification (blocking, no behavior change)

**Goal:** Pin the shared decisions every downstream phase consumes, so the five workstreams cannot diverge.

**Tasks:**
1. **Shared status policy table.** NEW `Source/Parsek/Logistics/RouteStatusPolicy.cs` with two `internal static bool` predicates over all 9 `RouteStatus` values (verified: Active=0, InTransit=1, WaitingForResources=2, WaitingForFunds=3, DestinationFull=4, EndpointLost=5, MissingSourceRecording=6, SourceChanged=7, Paused=8):
   - `BindsTree(RouteStatus)` (blocks manual loop): TRUE for **all 9** except none. Recommended v0: a route binds its tree whenever it exists and is not explicitly cleared. Active, InTransit, WaitingForResources, WaitingForFunds, DestinationFull, Paused, EndpointLost, MissingSourceRecording, SourceChanged all BIND. (Broken routes keep owning the tree until the player removes them, preventing the self-heal double-owner collision.)
   - `GhostDriving(RouteStatus)` (render the loop): TRUE for Active, InTransit, WaitingForResources, WaitingForFunds, DestinationFull; FALSE for Paused, EndpointLost, MissingSourceRecording, SourceChanged.
   - This resolves the loop-render vs mutual-exclusion status-policy contradiction. Both the guard and the ghost-driving selector call this one file.
2. **Loop-route discriminator.** Define `Route.IsLoopRoute => !string.IsNullOrEmpty(BackingMissionTreeId)` (a property over the field added in Phase 1). All v0 routes are loop-routes (v0 has no non-loop dispatch model). The self-timer paths (`NextDispatchUT`, `InTransit`/`TransitDuration` arrival, `PendingDeliveryUT` fire) are dead for every v0 route but stay serialized/present (diagnostics; possible future Send-Once).
3. **Clock-ownership decision, pinned.** The route's dispatch PHASE is owned by the loop-clock CROSSING detector + `LastObservedLoopCycleIndex`, NOT by the Mission anchor. Verified: `MissionLoopUnitBuilder` floors `baseAnchorUT = Math.Max(anchor, spanEndUT)` (cs:228-229), so a `DispatchWindowEpochUT`-valued anchor (= recording `StartUT`, RouteBuilder.cs:237) is silently overridden to `spanEndUT`. The Mission's `LoopIntervalSeconds` drives render CADENCE only. Remove all "phase anchor = dispatch epoch" language from any draft text.
4. **One backing-mission helper, one name.** Three drafts proposed `RouteBackingMissionSelection`/`RouteBackingMission`, `RouteBackingMissionBuilder`, `RouteBackingMissionFactory`. Unify to ONE file `Source/Parsek/Logistics/RouteBackingMission.cs` exposing `ComputeExcludedIntervalKeys(...)` + `BuildMission(route, currentUT)`. The window START derives from the **tree ROOT** launch (`committedTree.RootRecordingId` `StartUT` / the launch composition interval), NOT `analysis.SourceRecording.StartUT` (verified: `SourceRecording` is the mid-flight merged dock child with no launch site, RouteBuilder.cs:87-98). The END is `UndockUT`.

**Tests (xUnit):** `RouteStatusPolicyTests` per-value matrix for both predicates (an enum append must fail loudly).

**Files:** NEW `RouteStatusPolicy.cs`. (No `Route.cs` edits yet; `IsLoopRoute` lands in Phase 1 with the field.)

---

## Phase 1: Route Entity + Persistence (entity workstream)

**Goal:** Add the canonical backing-mission field set ONCE, with codec round-trip, the derivation helper, and the on-demand Mission constructor. This is the single atomic `Route.cs` + `RouteCodec.cs` change; no other phase adds Route fields.

**Tasks:**
1. **Canonical Route fields** (merging entity + delivery field lists into one change) on `Route.cs`, all defaulting to null/empty/-1:
   - `BackingMissionTreeId` (string)
   - `ExcludedIntervalKeys` (HashSet<string> of `MissionCompositionNode.HeadLegId` values that end-trim to [launch..undock])
   - `RecordedDockUT` (double), `DockMemberRecordingId` (string) lifted from `RouteConnectionWindow.DockUT`
   - `LoopAnchorUT` (double, set on activate)
   - `LastObservedLoopCycleIndex` (long, default -1)
   - `IsLoopRoute` property (Phase 0 discriminator)
   - **DROP** the RouteSourceRef.UndockUT field and the `FirstDifferingField` "undock-ut" case that the entity draft proposed. **Reason (verified, critical):** `RouteStore.BuildLiveSourceRefForComparison` (RouteStore.cs:407) rebuilds the live ref from a bare `Recording`, which carries no single undock UT, so `live.UndockUT` defaults to 0.0 while captured `sref.UndockUT != 0` -> every route is permanently flagged `SourceChanged` on the first `RevalidateSources` pass, with no auto-recovery. Undock drift is ALREADY covered: `RouteProofHasher` folds `window.UndockUT` (cs:78) and `window.DockUT` (cs:77) into `RouteProofHash`, which is already the `route-proof-hash` `FirstDifferingField` case (RouteStore.cs:464). `RouteSourceRef.cs` Equals/GetHashCode and `RouteStore.FirstDifferingField` stay UNTOUCHED.
   - Document on `Route.cs` that these fields are held by logistics and the Mission object is NEVER inserted into `MissionStore`. Guarantee `BackingMissionTreeId == SourceRefs[].TreeId` for the v0 single-source route (resolves the guard/selector key-mismatch gap).
2. **`RouteCodec` round-trip.** In `SerializeInto`: write `backingMissionTreeId`, `recordedDockUT`/`loopAnchorUT` (`ToString("R", InvariantCulture)`), `dockMemberRecordingId`, sparse `lastObservedLoopCycleIndex` (omit when -1), and `ExcludedIntervalKeys` as repeated `excludedInterval` values under an `EXCLUDED_INTERVALS` child node (empty set writes no node). In `DeserializeFrom`: read all, missing node -> empty set, missing scalar -> default, `lastObservedLoopCycleIndex` absent -> -1. A missing backing-mission definition does NOT reject the route (only the existing zero-STOP / malformed-SOURCE rejects stand). Pre-1.0: graceful default, no migration.
3. **Derivation helper** `RouteBackingMission.ComputeExcludedIntervalKeys(RecordingTree tree, double undockUT, double launchUT)` -> HashSet<string>. Build via the exact pipeline the loop builder uses (verified names): `MissionStructureBuilder.Build(tree)` -> `MissionThroughLineBuilder.Build(structure)` -> `MissionCompositionBuilder.Build(structure)`. Walk every selectable `MissionCompositionNode`; exclude nodes representing post-undock survivor/payload segments. Prefer keying the trim on the Undock `BranchPoint`'s child leg ids (robust against structural-peel edge clamping) over a pure `StartUT >= undockUT` scan; pin with a straddling-interval boundary test (use a small UT epsilon at the exact boundary). Guard: NaN/`undockUT <= launchUT` / no composition roots -> empty set + Verbose reason (whole segment renders, honest fallback). Single post-walk Verbose summary (tree id, undockUT, scanned/excluded/kept counts).
4. **On-demand Mission constructor** `RouteBackingMission.BuildMission(Route route, double currentUT)` -> Mission: `new Mission(route.Id + "-backing", route.BackingMissionTreeId, route.Name)`, copy `ExcludedIntervalKeys` into `Mission.ExcludedIntervalKeys` ONLY (do NOT populate `ExcludedThroughLineHeadIds`; that coarse field drops a whole vessel, resolving the def-shape contradiction). Set `LoopPlayback=true`, `LoopIntervalSeconds = route.DispatchInterval`, `LoopTimeUnit = Sec`, `LoopAnchorUT = route.LoopAnchorUT` DIRECTLY on the route-owned object (never `MissionStore.SetLoopEnabled`, which mutates store siblings). Document that the builder floors the anchor to `spanEndUT`; the route does not own render phase. The stable derived id keeps logs greppable and feeds `BuildSignature` cache invalidation. Verbose-log construction.

**Missions seams consumed (read-only):** `MissionStructureBuilder.Build(tree)` -> `MissionThroughLineBuilder.Build(structure)` -> `MissionCompositionBuilder.Build(structure)`; `MissionCompositionNode.HeadLegId`/`OwnerHeadId`/`IsSelectable`/`StartUT`/`EndUT`; `Mission` ctor + `ExcludedIntervalKeys`/`LoopPlayback`/`LoopIntervalSeconds`/`LoopTimeUnit`/`LoopAnchorUT`; `MissionIntervalSelection.ComputeRenderWindows` semantics (verification target).

**Tests (xUnit):**
- `RouteCodecTests`: round-trip multi-key `ExcludedIntervalKeys` + all new scalars; empty-definition round-trip writes no `EXCLUDED_INTERVALS` node and loads an empty set (not a reject); sparse `-1` cycle index; old-save graceful default.
- `RouteBackingMissionTests`: `ComputeExcludedIntervalKeys` excludes exactly post-undock head-leg ids, keeps launch..undock; window START = `root.StartUT` on a multi-leg tree (launch -> mid-flight dock child -> undock), NOT `SourceRecording.StartUT`; straddling + exact-boundary cases; NaN/empty fallback empty-set + logged reason. `BuildMission` copies excluded set, sets loop fields from the route schedule, uses route tree id, leaves `MissionStore.Missions` count unchanged, and `ExcludedThroughLineHeadIds` stays empty.
- `RouteSourceRefTests`: assert NO new `UndockUT` field exists and `RevalidateSources` trips `SourceChanged` via the existing `route-proof-hash` case when the proof hash drifts (proves coverage without the bricking field).

**Files:** `Route.cs`, `RouteCodec.cs` (existing); NEW `RouteBackingMission.cs`. Tests: `RouteCodecTests.cs`, `RouteSourceRefTests.cs`, NEW `RouteBackingMissionTests.cs` (place under `Source/Parsek.Tests/Logistics/`).

**REVIEW CHECKPOINT A** (clean-context Opus, after Phase 1): the field-set + codec moved atomically with their round-trip test (no silent data loss); the `RouteSourceRef.UndockUT` field is genuinely absent; the derivation anchors START on the tree root; the route-owned Mission never reaches `MissionStore`.

---

## Phase 2: Mutual-Exclusion Guard (mutual-exclusion workstream)

**Goal:** Enforce "a tree is a supply route XOR a manually looped mission, never both." Lands BEFORE the render union so a newly-activated route's tree never has a live manual loop competing for the single owner index. (mutual-exclusion's plan was verified sound as written; only the status policy is now externalized to Phase 0.)

**Tasks:**
1. NEW `Source/Parsek/Logistics/RouteTreeGuard.cs`: `IsTreeBoundToActiveRoute(string treeId)` (any committed route whose `RouteStatusPolicy.BindsTree(status)` binds that treeId via `SourceRefs[].TreeId`), `RouteBindingFor(treeId, out Route)` (tooltip/log), `BoundTreeIds()` (reconcile + tests). Pure w.r.t. Unity; reads only `RouteStore.CommittedRoutes` (route store surface, not recording ERS, so outside the ERS/ELS grep gate; confirm against the allowlist). Verbose-log TRUE results.
2. **Missions-window edit** (the one allowed host edit here): in `MissionsWindowUI.DrawMissionHeader` Loop toggle block, compute `routeBound = RouteTreeGuard.IsTreeBoundToActiveRoute(mission.TreeId)`; wrap the Loop label+toggle in `GUI.enabled = !routeBound` (render greyed-OFF); add a belt-and-suspenders commit guard `if (routeBound) { log; skip }` at the top of the `loopNow != mission.LoopPlayback` branch so no turn-ON reaches `MissionStore.SetLoopEnabled` even if a future layout refactor drops the `GUI.enabled` wrap; render an inline "Looped by route: <name>" affordance (ASCII only; distinctive UTF-16 string for DLL verification). Cache a per-frame bound-tree set in `OnGUI` if row count grows (never across frames).
3. `ForceClearManualLoopForRouteTree(treeId, currentUT)` in `RouteTreeGuard`: iterate `MissionStore.Missions` read-only; for each looping Mission on that tree call the existing `MissionStore.SetLoopEnabled(m, false, currentUT)`; Info-log a single summary; idempotent. Call it at BOTH `RouteStore.AddRoute` production sites (`LogisticsWindowUI` + `RouteCreationDialog`) and any explicit activate path. Release on delete/clear is passive (predicate recomputes false next frame; clearing must NOT auto-start a manual loop).
4. **Load-time reconcile** in `ParsekScenario.OnLoad`, AFTER `MissionStore` load/normalize and AFTER `RouteStore.LoadRoutesFrom` + `RevalidateSources("OnLoad")` (so statuses are final): for each `treeId` in `BoundTreeIds()` call `ForceClearManualLoopForRouteTree`. Info-log a reconcile summary. No save-shape rewrite (binding derives from existing fields; zero codec impact).

**Missions seams consumed (read-only / consume-not-edit):** `MissionStore.SetLoopEnabled(Mission, bool, double)`; `MissionStore.Missions`.

**Tests:** xUnit `RouteTreeGuardTests` (bound TRUE/FALSE matrix via `SourceRefs[].TreeId`, cross-tree FALSE, multi-sourceref distinct trees, null/empty skip, log assertions); `ForceClearManualLoopForRouteTree` (route-tree mission cleared, other-tree mission untouched; seed via `EnsureDefaultsForTrees` -> `FindOriginalMission` -> `SetLoopEnabled` + `RecordingStore.ResetForTesting` in teardown); load-time reconcile (save with both -> route wins, different-tree loop survives). In-game `RuntimeTests` [Logistics, FLIGHT]: route-bound tree's toggle renders disabled and a turn-ON does not flip `mission.LoopPlayback`; non-route tree stays togglable.

**Files:** NEW `RouteTreeGuard.cs`; `MissionsWindowUI.cs`, `ParsekScenario.cs`, `LogisticsWindowUI.cs`, `RouteCreationDialog.cs` (existing). Tests: NEW `RouteTreeGuardTests.cs`, `RuntimeTests.cs`.

---

## Phase 3: Render Union via Append (loop-render workstream)

**Goal:** Render the route as a Mission loop by appending its route-owned backing Mission to the SINGLE `MissionLoopUnitBuilder.Build` call at the three push seams. **Resolution of the union contradiction:** use delivery's append-to-list mechanism, NOT loop-render's separate `RouteLoopUnitUnion` LoopUnitSet helper. Appending reuses Build's existing owner/member collision logging for free and folds route missions into the existing `BuildSignature` with no new union-side code. loop-render's `RouteLoopUnitUnion` helper is DROPPED; loop-render's valuable contribution (the per-frame ghost-driving SELECTOR) is kept.

**Tasks:**
1. NEW `Source/Parsek/Logistics/RouteGhostDriverSelector.cs`: `SelectGhostDrivingBackingMissions(IReadOnlyList<Route> routes, double currentUT)` -> `IReadOnlyList<Mission>`. Filter `RouteStore.CommittedRoutes` by `RouteStatusPolicy.GhostDriving(status)`, materialize each via `RouteBackingMission.BuildMission(route, currentUT)`. VerboseRateLimited per-frame summary (ghostDriving=N, skippedByStatus=x), shared key.
2. **`ParsekFlight.DriveMissionLoopUnits`:** build `routeMissions = RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(RouteStore.CommittedRoutes, ut)`, append to the existing player `MissionStore.Missions` list, pass the UNIONED list as the single argument to the existing `MissionLoopUnitBuilder.Build(unionedMissions, RecordingStore.CommittedTrees, committed, autoLoopIntervalSeconds, bodyInfo:null)`. The IDENTICAL `committed` list (RecordingStore-derived, same frame) feeds Build so member-index alignment holds. Extend the cache-rebuild gate so the route side participates: include the ghost-driving route ids + each backing Mission's loop fields + status in the signature input (achieved by passing the unioned list into the existing `BuildSignature`; do NOT edit it). Feed the resulting `cachedLoopUnits` to `engine.SetLoopUnits` AND to the `IsMember` watch-exit check (so a watched route ghost is not torn down).
3. **`ParsekKSC.DriveMissionLoopUnits`** and **`ParsekTrackingStation.DriveMissionLoopUnits`:** same append shape. **TS ordering, corrected (per reconciliation):** the union lands INSIDE `DriveMissionLoopUnits` writing to `cachedLoopUnits`. `DriveMissionLoopUnits` is invoked before the `GhostMapPresence.UpdateTrackingStationGhostLifecycle(cachedLoopUnits)` call, so that read sees the unioned set automatically. Do NOT add a separate `cachedUnionedLoopUnits` field or reorder (that solved a non-problem). Verbose-log route-unit count folded per scene.
4. Flight re-aim readers in `ParsekFlight` (the re-aim adapter loop and `ApplyReaimWindowPlayback`): in v0 routes pass `bodyInfo=null` and produce NO re-aim units, so route members never appear there. Audit both: if either gates teardown of a member, feed the unioned set; otherwise document why route members are re-aim-irrelevant in v0.

**Missions seams consumed (read-only):** `MissionLoopUnitBuilder.Build(missions, trees, committed, autoLoopIntervalSeconds, bodyInfo:null)` (UNCHANGED, route Mission appended as part of the one list); its existing `BuildSignature`; `GhostPlaybackLogic.LoopUnitSet`/`LoopUnit` (read-only via Build output); `GhostPlaybackEngine.SetLoopUnits`. Same-tree route+manual collision is handled defensively by Build's own owner-collision drop, which mutual exclusion (Phase 2) prevents upstream.

**Tests:** xUnit `RouteGhostDriverSelectorTests` (status filter via `RouteStatusPolicy`, null/non-loop skip, log assertion); source-text parity gate asserting all three `DriveMissionLoopUnits` append the route mission list to the single Build call before `SetLoopUnits`/`cachedLoopUnits` assignment (use the repo's source-text gate pattern). In-game [logistics, FLIGHT + TRACKSTATION]: cross-tree parallel (route on X + manual loop on Y both render); route ghost in tracking-station map presence; log shows merged > playerCount when a route is active.

**Files:** NEW `RouteGhostDriverSelector.cs`; `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekTrackingStation.cs` (existing). Tests: NEW `RouteGhostDriverSelectorTests.cs`, host-parity gate, `RuntimeTests.cs`.

**REVIEW CHECKPOINT B** (after Phase 3, before delivery): the three seams are byte-identical via the shared selector; the route renders trimmed at undock; no locked Missions file was edited; single-owner stays intact because Phase 2 landed first.

---

## Phase 4: Delivery on the Loop Clock (delivery workstream)

**Goal:** Replace the self-timer fire gate for loop-routes with a loop-clock crossing detector, reusing `ApplyDelivery`/funds/ledger/ELS verbatim. Decision-b's "route owns its dispatch clock" is satisfied HERE (crossing detector + `LastObservedLoopCycleIndex`), not by the Mission anchor.

**Tasks:**
1. NEW `Source/Parsek/Logistics/RouteLoopClock.cs`: `TryGetRouteLoopState(LoopUnit unit, double currentUT, out double loopUT, out long cycleIndex, out bool isInInterCycleTail)` wrapping the verified `GhostPlaybackLogic.TryComputeSpanLoopUT(currentUT, anchor, spanStart, spanEnd, cadence, out loopUT, out cycleIndex, out isInInterCycleTail, schedule:null, loiterCuts:null)`. **Consume the `isInInterCycleTail` out-param** (the draft signature omitted it): when the route's dispatch period >= transit (the v0 case), the clock parks at `spanEnd` in the tail; the dock UT (< undock = spanEnd) is traversed in the active phase, but the wrapper must thread the flag and the crossing check must ignore tail samples. XML-doc the cadence==span vs cadence>span behavior.
2. **Loop-route branch in `RouteOrchestrator.ProcessOneRoute`:** at the top, if `route.IsLoopRoute`, route to the loop-clock path and NEVER reach the `Dispatch`/`InTransit`/`InTransitComplete` appliers or the `PendingDeliveryUT` fire gate (RouteOrchestrator.cs:301-306). Build the route's `LoopUnit` (via the same selector/Build path, same `committed` list), call `RouteLoopClock.TryGetRouteLoopState`, and fire when `cycleIndex > route.LastObservedLoopCycleIndex` AND `route.RecordedDockUT` lies within the span window the just-completed cycle traversed. Keep the status gates (skip Paused/EndpointLost). On fire, call UNCHANGED `ApplyDelivery(route, currentUT, env)` with game-time `currentUT` (ledger row UT is when-applied, never the recorded dock UT), then set `LastObservedLoopCycleIndex = cycleIndex`.
3. **Eligibility WITHOUT `EvaluateRoute`** (resolves the monolith contradiction): do NOT call `RouteDispatchEvaluator.EvaluateRoute` for loop-routes (verified: it always drives toward `Dispatch -> InTransit` and the old `PendingDeliveryUT` self-timer). Either call the `IRouteRuntimeEnvironment` predicates directly (`RouteHasValidSourcesInErs`, `TryResolveEndpoint`, `OriginHasCargo`, `KscFundsAvailable`, `DestinationHasCapacity`) or extract a shared eligibility-only helper out of `EvaluateRoute` and call it from both paths. On a crossing, run eligibility BEFORE `ApplyDelivery`; on failure do not fire, increment `SkippedCycles` (so cycleId advances and dispatch/deliver pairing stays unique), log the reason, and STILL snap `LastObservedLoopCycleIndex` forward so the blocked cycle does not re-fire every tick. Update the `Route.SkippedCycles` XML doc to match the new increment behavior; coordinate the increment contract with no other writer.
4. **Warp robustness.** Orchestrator ticks ~1 Hz; during fast warp `cycleIndex` jumps N>1. Fire ONCE per tick, snap `LastObservedLoopCycleIndex` forward (do not replay each skipped cycle). ELS `(routeId, cycleId)` idempotency + the cycleId sequence (`Completed+Skipped`) serialize one delivery per id. **Drop the "CompletedCycles catch up" reasoning** (it never catches up): cycleId is a monotonic per-delivery sequence that intentionally does NOT track `cycleIndex` 1:1 after a warp snap; idempotency relies only on cycleId uniqueness + the ELS lookup. Verbose-log the jump and snap.
5. **Reset discipline.** `LastObservedLoopCycleIndex` resets to -1 on activate (first post-activate cycle fires) and persists through the codec (Phase 1) so a save/reload mid-cycle does not double-fire; ELS is the backstop if it does.

**Seams consumed (read-only / consume):** `GhostPlaybackLogic.TryComputeSpanLoopUT`; `LoopUnit` span/cadence/anchor accessors; `RouteOrchestrator.ApplyDelivery`/`ApplyDeliveryFromPlan` (KEPT verbatim); `RouteOrchestrator.IsDeliveryAlreadyInLedger` (ELS idempotency, UNCHANGED); `IRouteRuntimeEnvironment` eligibility predicates; `Recording.RouteConnectionWindows[i].DockUT` (lifted to `RecordedDockUT` at creation, owned by Phase 5 RouteBuilder).

**Tests (xUnit):** `RouteLoopClockTests` (cycleIndex increments once per period, loopUT wraps, dock-UT-in-window on expected cycle, `isInInterCycleTail` threading, warp single-fire-and-snap). `RouteLoopDeliveryFireTests` (fires once on crossing; no double-fire same cycle; ELS idempotency on replay without double-charge; Paused/EndpointLost skip; failed eligibility increments `SkippedCycles` + snaps; strictly-increasing unique cycleIds across skip-then-fire). `[Collection("Sequential")]` + `RouteStore`/`Ledger` `ResetForTesting` + log assertions. In-game [logistics, FLIGHT]: loop clock crosses `DockUT`, exactly one `RouteCargoDelivered` row at the live destination (resources/inventory filled, funds debited in Career), verified via KSP.log signatures.

**Files:** NEW `RouteLoopClock.cs`; `RouteOrchestrator.cs`, `RouteDispatchEvaluator.cs` (existing, eligibility-helper extraction only). Tests: NEW `RouteLoopClockTests.cs`, `RouteLoopDeliveryFireTests.cs` (under `Source/Parsek.Tests/Logistics/`), `RuntimeTests.cs`.

---

## Phase 5: Creation Adaptation, Dead-Code Discard, End-to-End Tests + Docs (cleanup-test workstream, LAST)

**Goal:** Re-found `RouteBuilder` creation on the unified backing-mission derivation, verify-and-guard the absent replay branch, own the end-to-end suite and docs. Gated on Phases 1-4 landing.

**Tasks:**
1. **Adapt `RouteBuilder.BuildRoute`** (KEEP proof/origin/stop/manifest/transit/source-ref logic verbatim): after eligibility resolution, set `route.BackingMissionTreeId = source.TreeId`, `route.ExcludedIntervalKeys = RouteBackingMission.ComputeExcludedIntervalKeys(committedTree, analysis.ConnectionWindow.UndockUT, rootLaunchUT)`, capture `route.RecordedDockUT = analysis.ConnectionWindow.DockUT` and `route.DockMemberRecordingId` in the same creation pass (resolves the dock-UT-capture ownership gap), set `route.LoopAnchorUT` on activate. Add a `backing-mission-unresolvable` reject reason (logged) if the structure walk yields no non-empty [launch..undock] window. Extend the "Built route" Info log with tree id, undockUT, dockUT, excluded count.
2. **Confirm no change** to `RouteAnalysisEngine.AnalyzeTree`/`AnalyzeRecording` and `RouteCandidateFinder` (proof verification, sealed/eligible/dedup gates KEPT). Add only an internal assertion/log that the chosen window carries a finite `UndockUT` and a one-line XML note pointing at `RouteBackingMission` as the new geometry owner.
3. **Discard verification.** Re-confirm via grep that `OffsetReplay*`/`RouteReplayPlanner` have ZERO references in `Source/`. Add NEW xUnit `RouteReplayBranchAbsentTests.cs` (reflection scan over the assembly's exported+internal types; fails if any such type reappears). Record in the design doc that the discard is verified-absent-in-this-line; the user prunes the `logistics-route-replay` branch (agents do not delete branches).
4. **Wire call sites:** `LogisticsWindowUI` + `RouteCreationDialog` surface the new reject reason without UI redesign (logistics-owned, edits allowed); confirm both pass `committedTree`.
5. **End-to-end tests.** xUnit: extend `RouteBuilderTests` (populates backing-mission def + dock/undock capture; reject path; KEPT logic intact), `RouteCandidateFinderTests` (gates unchanged), a `RouteBackingMissionLoopUnit` test (route-owned Mission through UNCHANGED `Build` yields one unit with [launch..undock] member window and disjoint `OwnerByIndex`). In-game [logistics]: FLIGHT route renders looping ghost over [launch..undock] only (post-undock tail absent); KSC + TS union mirror; delivery at dock; mutual-exclusion greying; cross-tree parallelism. Add an explicit precondition gate to the in-game phase (cheap reflection/grep check that the union seam + fire switch exist) so a red result is attributable to this workstream, not an incomplete upstream.
6. **Docs.** `CHANGELOG.md` (logistics 0.10.0, 1-line user-facing each, plain ASCII, no em dashes): "Supply routes now render as a looping mission segment from launch to undock." and "A tree is either a supply route or a manually looped mission, not both." `docs/dev/todo-and-known-bugs.md`: mark items done, record v0 scope cut (launch->undock, same-body) and deferred items (undock->undock shuttle, isolating the docked stretch, interplanetary re-aim via the unused `bodyInfo` seam, true multi-owner same-recording parallel rendering). `docs/parsek-logistics-supply-routes-design.md` section 0/0.7: record the `RouteBackingMission` seam and the confirmed discard. Do NOT edit any Missions design file. Run the ERS/ELS grep gate and full `dotnet test`. Verify the deployed DLL carries the new "Looped by route" UTF-16 string.

**Files:** `RouteBuilder.cs`, `RouteAnalysisEngine.cs`, `RouteCandidateFinder.cs`, `LogisticsWindowUI.cs`, `RouteCreationDialog.cs`, `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, `docs/parsek-logistics-supply-routes-design.md`, `RuntimeTests.cs` (existing); NEW `RouteReplayBranchAbsentTests.cs`, extended `RouteBuilderTests.cs`/`RouteCandidateFinderTests.cs`.

**REVIEW CHECKPOINT C** (final, after Phases 4-5): `git diff` over the locked Missions file set is EMPTY; the three push seams + the Loop-toggle guard are the only shared-host edits; deployed-DLL verified; CHANGELOG/todo/design-doc match HEAD. Do NOT use `/ultrareview` (paid; only on explicit request).

---

## Highest Residual Risks

1. **Backing-mission interval-key derivation correctness (post-undock trim).** A peel branch that straddles the undock instant, or structural-peel edge clamping (`MissionComposition` clamps peel UT into `[runStart, runEnd]`), can over- or under-trim. The window START must derive from the tree ROOT, not `SourceRecording`. Mitigation: prefer keying the trim on the Undock `BranchPoint`'s child leg ids over a pure `StartUT >= undockUT` scan; pin against ONE real dock/deliver/undock recording in-game before finalizing; do not guess-rewrite if a playtest disagrees, pin the concrete case first.
2. **Member-index alignment across the union and the orchestrator clock.** The route's backing-mission member indices are committed-list indices. Both the host Build and the orchestrator's `LoopUnit` build MUST source the exact same `RecordingStore.CommittedRecordings`-derived `committed` list (same frame). A mismatch silently indexes the wrong recording. Enforce by passing the single `committed` (and `trees`) snapshot into both; in-game tests assert recording IDENTITY, not just "a ghost appears."
3. **Loop-clock crossing under warp / reset.** ~1 Hz orchestrator vs multi-cycle warp jumps; activate/scene-reload/Rewind resets `LoopAnchorUT`. Fire once per tick + snap forward; ELS `(routeId, cycleId)` idempotency is the backstop. The cycleId sequence does NOT track `cycleIndex` 1:1 after a snap (by design). Persist `LastObservedLoopCycleIndex` so a mid-cycle save/reload does not double-fire.

## Open Decisions Still Needing the User (separated from settled items)

These are NOT blocked decisions for the settled mechanics above; they are policy confirmations the design owner should sign off before/at Phase 0:

1. **Broken-route binding policy.** `RouteStatusPolicy.BindsTree` keeps MissingSourceRecording + SourceChanged BINDING (broken route owns its tree until explicitly cleared), to prevent a self-heal double-owner collision. Recommended and adopted as the build-blocking default; confirm this over the alternative (release-on-broken, which is reachable and unsafe).
2. **Recordings-tab loop surface scope.** The spec scopes the guard to "mission rows" (Missions window). The Recordings window has separate per-recording/bulk/group/chain Loop toggles bypassing `MissionStore`. v0 leaves them untouched; confirm the Missions window is the only sanctioned manual-loop entry for route-eligible trees, or whether Recordings-tab toggles also need greying.

**Settled (no user input needed):** drop `RouteSourceRef.UndockUT` (proof hash covers undock drift); append-to-Build-list union (not a separate `LoopUnitSet` helper); route phase owned by the crossing detector (not the Mission anchor); all v0 routes are loop-routes; one `RouteBackingMission` helper; one `RouteStatusPolicy` table; eligibility via env predicates / extracted helper (not monolithic `EvaluateRoute`); v0 scope = launch->undock, same-body, `bodyInfo=null`, no re-aim.
