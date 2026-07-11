# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.
- `done/todo-and-known-bugs-v6.md` - the v0.9.2 / v0.9.3 bug-closure wave and the first half of the v0.10.0 cycle: Re-Fly supersede / anchor-propagation / co-bubble-retirement closures, the watch-mode W-cycle + chain-seam fixes, the schema generation-3 reset, the Missions window (tab + looping + periodicity + zero-drift reschedule), re-aim interplanetary transfers, the Map/TS render-tracer MVP (PR #1005), and the debris-rendering / switch-fly auto-record closures. Archived 2026-06-05.
- `done/todo-and-known-bugs-v7.md` - the v0.10.1 / v0.10.2 / v0.10.3 finish-up: logistics milestones M1-M6 (non-KSC origin, mod resources / harvest, pickup, multi-stop / multi-origin / round-trip, inter-body, legibility) + the claw producer; missions M-MIS-1..6 / 8 / 9 and the re-aim / periodicity / phasing solver stack; the Map/TS render rewrite cutover; the career-economy bug wave (BUG-A..H, the records-milestone recalc storm, contract-discard desync); and the ledger ground-truth audit closures. Archived 2026-07-09.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## ~~FIXED~~ - Supply-route inventory delivery only targeted the FIRST inventory module on the destination (2026-07-11, branch `fix-delivery-multimodule`)

**Bug:** `LiveDeliveryCapacityProbe.ProbeLoadedFirstEmpty` / `ProbeUnloadedFirstEmpty` stopped at the first `ModuleInventoryPart` and reported "no slot" when that one module was full, and `LiveDeliveryWriters.WriteInventoryLoaded` / `WriteInventoryUnloaded` wrote into the first inventory module they found. A destination whose first cargo container is full but whose later containers have free slots reported inventory items as undelivered. `consumedSlots` was a bare slot-index HashSet, only correct under the single-module assumption. Pickup and origin-debit (`LiveInventoryPickupWriter`) already scanned ALL inventory modules; this was a delivery-side-only gap.

**Fix:** widened the probe/planner/writer slot contract from a bare `int` to the module-qualified `InventorySlotAddress` struct (part index, module index, slot index; deterministic order = vessel part order, then module order within the part, then ascending slot index). The probe now walks ALL inventory modules on the captured loaded/unloaded branch, `consumedSlots` keys by address, and the writers resolve and store into exactly the (part, module, slot) the planner assigned, on the same captured `isLoaded` branch (defensive range/type checks Warn and report a failed store on mid-tick divergence). The unloaded slot-count fallback of 9 (unpersisted `InventorySlots`) is preserved per module. Plans are built fresh per delivery and never persisted (verified: RouteCodec / ledger rows carry no assigned slots), so no schema change.

**Tests:** planner multi-module address propagation (`RouteDeliveryPlannerTests`), address value semantics + pure unloaded scan/store helpers + the "Inventory store" log contract (`LiveDeliveryMultiModuleTests`), and the in-game `Delivery_MultiModule_FirstContainerFullSecondReceives` (loaded branch: fills the first container live, asserts the probe hands out the second container's slot and the writer stores there; the unloaded branch's logic is the headless-pinned ConfigNode path).

**Operator runbook (pending):** run the in-game `Delivery_MultiModule_FirstContainerFullSecondReceives` in a disposable FLIGHT session on a vessel with two cargo containers (Run All + Isolated or the row play button). Additionally, an UNLOADED-destination delivery onto a small container (e.g. a 3-slot SEQ) would exercise the prefab slot-count fallback (`ResolveUnloadedSlotCountFallback`), which needs `PartLoader` and has no headless test.

**Review follow-ups (same PR):** (1) unloaded phantom-slot fix - `InventorySlots` is a non-persistent KSPField so proto modules never carry it; the old hardcoded 9-slot assumption handed out phantom slot indices on smaller containers (stock 3-slot SEQ), which the writer persisted as UI-inaccessible stores, and the multi-module walk was widening that exposure; the probe now resolves the real count from the part PREFAB's module (`ResolveUnloadedSlotCountFallback`), falling back to 9 only when the prefab is unresolvable. (2) A present-but-unparseable `InventorySlots` value no longer zeroes the count via `int.TryParse`'s out-param (module falsely full). (3) `default(InventorySlotAddress)` is now invalid instead of reading as the valid (0,0,0) root-slot address. (4) The probe caches exhaustion - once a full walk returns None, later manifest items short-circuit instead of re-scanning the vessel per item. Known accepted lenience (pre-existing, unchanged): a STOREDPART with a missing/unparseable `slotIndex` is not counted occupied (corrupted-save-only double-book risk), and the unloaded branch matches modules by exact `moduleName == "ModuleInventoryPart"` while the loaded branch matches subclasses via `is` (same asymmetry as `LiveInventoryPickupWriter`).

**Merge reconciliation with `inventory-delivery-parity` (PR #1294, landed on main first):** the two changes are orthogonal (this one = WHERE items go across containers; that one = HOW MANY units / WHETHER they fit by volume/mass) and were merged into one unified contract: `InventoryDeliveryLine` carries both a module-qualified `AssignedSlot` and a `Units` count; the planner splits by stack capacity, admits by volume/mass, then assigns a per-stack (part, module, slot) address across all containers; the writers store `Units` into the exact assigned address. The parity branch's volume/mass budget originally read the FIRST inventory module only; because this branch made slot assignment multi-module, the budget was widened to SUM `packedVolumeLimit` / `massLimit` and occupancy across ALL inventory modules (any unlimited module makes that axis unlimited). Residual bounded imprecision (documented in `TryReadContainerBudget`): the budget is vessel-granularity while stock enforces per-container, so a specific container could individually over/under-fill; only the vessel TOTAL is bounded (stock automated delivery bypasses per-slot volume enforcement anyway). Per-container budgets coordinated with slot placement are a deferred refinement.

---

## ~~FIXED~~ - Supply routes silently lost cargo when the destination was full (found in the 2026-07-11 logistics inventory review; branch `logistics-destfull-gate`)

**Was:** `LiveRouteRuntimeEnvironment.DestinationHasCapacity` was the v0 always-true stub, so a cycle always dispatched, the origin was physically debited (or KSC funds charged) for the FULL manifest, and the apply-time partial fill dropped whatever did not fit - the remainder vanished (the transport is a ghost; nothing comes back) with only a "delivered-partial" status reason. The design doc's "implemented v0 gate" note even claimed a zero-capacity destination still blocked the cycle, which was never true.

**Fix (all-or-nothing, per the 2026-07-11 maintainer decision):** the gate is un-stubbed via the pure `RouteDestinationCapacityCheck`: every stop's full delivery manifest (resources AND stored-part inventory slots) must fit its resolved destination - evaluated with the SAME planner+probe the delivery applier uses, so the gate cannot drift from the write - or the route holds `DestinationFull` naming the first item that does not fit (`stored-part:<partName>` for inventory slots). Unresolvable stop vessels fail OPEN (the endpoint gate owns them). The apply-time clamp stays as the backstop for capacity that shrinks mid-cycle; such a partial now records `Route.LastPartialDeliverySummary`/`UT`/`CycleId` (sparse in the codec; same-cycle partial windows APPEND into one report and only a LATER cycle's full delivery clears it, so a multi-stop cycle's later full window cannot erase an earlier window's loss) and the Logistics detail panel shows "Last delivery was partial: <actual/requested per short item>". Same-destination stops share one probe across the gate walk so their COMBINED manifest is checked (review fix - a fresh probe per stop let two windows to one station each claim the full tank).

**Also in the branch (hold-reason legibility):** inventory shortfall holds name the PART instead of the identity hash (`inventory:<partName>`, origin + pickup-source gates; 64-hex tails from pre-legibility persisted holds keep the generic text), and a NEW near-miss token `inventory-state:<partName>` fires when the origin physically holds the part but its state (charge/fuel/contents) differs from the recorded cargo (`LiveInventoryPickupWriter.CountStoredByPartName`, classification only - admission stays hash-exact).

**In-game verification pending (operator):** a route to a full destination must show "Held: no room for X" / "Held: no slot for 'part'" and not debit the origin; filling the destination mid-transit is not scriptable headless.

## ~~FIXED~~ - Supply-route inventory delivery loaded-vs-unloaded parity gaps: stacked quantity lost on the loaded path, no volume/mass admission on the unloaded path, non-stackable over-compression (branch `inventory-delivery-parity`)

Three parity gaps in the inventory delivery writer bundle (`Logistics/LiveDeliveryWriters.cs` + `LiveDeliveryCapacityProbe.cs` + `RouteDeliveryPlanner.cs`), verified against decompiled stock (KSP 1.12.5):

- **Gap A (stacked quantity lost, loaded path):** the route inventory manifest compresses identical stored parts per identity hash and carries the delivered count in the STOREDPART wrapper's `quantity`; `WriteInventoryLoaded` rebuilt a `ProtoPartSnapshot` from the inner PART node only and called stock `StoreCargoPartAtSlot` once, which unconditionally stores `quantity = 1`. A Quantity=10 item delivered 10 units to an UNLOADED destination but 1 unit to a LOADED one. **Fix:** after the store, the writer raises the slot's stack via stock `UpdateStackAmountAtSlot` (which clamps to the StoredPart's `stackCapacity`, sourced from the snapshot's `moduleCargoStackableQuantity` that stock `ProtoPartSnapshot.Save` persists) and reads the slot back for the unit-accurate actual. The store's success is also verified by slot read-back because stock `StoreCargoPartAtSlot(ProtoPartSnapshot, int)` returns true even when a null `partInfo` makes it store nothing.
- **Gap B (no volume/mass admission, unloaded path):** decompilation showed stock's packed-volume/mass enforcement lives in the storage UI (`HasCapacity` / `PartDroppedOnInventory`), NOT in `StoreCargoPartAtSlot`, so BOTH automated branches bypassed it. **Fix:** explicit admission at probe time (`LiveDeliveryCapacityProbe.ProbeInventoryUnitsThatFit` + `ConsumeInventoryCapacity`, pure core `ComputeUnitsThatFit`), mirroring stock's accounting: prefab `ModuleCargoPart.packedVolume * units` and `(prefab.mass + prefab.GetResourceMass()) * units` against the container prefab's `packedVolumeLimit` / `massLimit` (read from the container part's PREFAB on BOTH branches so admission is branch-symmetric; limit <= 0 = unlimited; `packedVolume < 0` = not storable; an unresolvable ITEM or CONTAINER prefab fails closed). An exact fit is protected from float flooring by a 1e-9 unit epsilon (60L/0.6L must admit 100, not 99). The budget read and per-part footprints are memoized per planning pass. The planner marks non-fitting units as skipped (`AssignedSlot = -1`, `Units` = unplaced count, surfaced as `inventoryUnitsSkipped` in the delivery summary log), so the writers never receive them - probe/writer symmetry preserved on both branches.
- **Gap C (non-stackable over-compression):** the planner assigned ONE slot per manifest item regardless of Quantity, so 10 non-stackable units recorded across 10 slots persisted as an invalid `quantity=10` in a single slot on the unloaded path. **Fix:** the planner splits each item into ceil(Quantity / stackCapacity) slot-sized stacks (`InventoryDeliveryLine.Units`), resolving stackCapacity from the STOREDPART wrapper's `stackCapacity`, then the inner PART's `moduleCargoStackableQuantity`; the live prefab's `ModuleCargoPart.stackableQuantity` is consulted ONLY for snapshot-less items (a snapshot silent on both values reconstructs as stack-1 on both stock load paths, so the plan must not widen past 1); resource-bearing payloads are forced to 1 (stock forces those to stack size 1 on load).

Delivery actuals are now unit-accurate: `LiveDeliveryWriters.ReadInventoryActualCount` sums stored UNITS (was: manifest lines), `ApplyDeliveryContext.InventoryWriter` carries `(item, slot, units)`, and the delivery summary log reads `inventoryUnits=actual/attempted`. UI formatters read the manifest items (unchanged shape) and needed no change. xUnit: planner multi-slot splitting / volume rejection / stack-capacity resolution (`RouteDeliveryPlannerTests`), admission core + unloaded STOREDPART node builder (`InventoryDeliveryParityTests`), unit-accurate apply (`RouteOrchestratorDeliveryTests`), stack-quantity codec round-trip (`RouteCodecTests`). In-game: `Delivery_LoadedVessel_StacksInventoryQuantityIntoSlot` (PENDING-OPERATOR: needs a FLIGHT vessel with an inventory container holding a stackable cargo part, e.g. an EVA Repair Kit, plus an empty slot) and `Delivery_Probe_AdmissionMatchesIndependentBudget` (read-only, batch-safe: cross-checks the live probe's admission wiring - budget walk, prefab limits, consumption tracking, fail-closed unknown prefab, stackable-quantity read - against an independently computed budget; needs only an inventory container on the FLIGHT vessel).

## ~~FIXED~~ - Batch baseline prime NRE (2026-07-10 verify run) - ROOT-CAUSED via the shipped diagnostics: KSP `EventData<T>.Add` cannot take a STATIC-method delegate (branches `fix-prime-diagnostics` + `fix-restore-rethrow-stack`)

**RESOLUTION (2026-07-10 rerun3, `logs/2026-07-10_2324_rerun3-stack/KSP.log:16339`):** the stack-preserving diagnostics captured the true thrower on the first re-run: `EventData'1+EvtDelegate..ctor -> EventData'1.Add -> FlightCameraReloadPin.Arm -> QuickloadResumeHelpers.CommitValidatedGameLoad`. KSP's `EventData<T>.Add` constructs an `EvtDelegate` whose ctor reads `evt.Target.GetType().Name` (decompiled, 1.12.5), so a delegate to a STATIC method (null `Target`) throws NullReferenceException inside `Add`. `FlightCameraReloadPin` is a static class with static handlers: `Arm` threw on its very first in-game call, so the #1282 camera re-pin window NEVER armed and every batch since aborted at the first isolated-restore prime (the "10-second batch": the entire isolated tier, the slow per-test scene-reload restores, was skipped). `Disarm` never threw because `Remove` compares delegate equality without constructing an `EvtDelegate`. SECOND project occurrence of this trap (the first: a static `GameEvents` OnLoad handler NRE wiped a persistent.sfs index, 2026-06-19). Headless tests could not catch it (no GameEvents at xUnit time) and compile-only review passes it. **Fix:** `Arm`/`Disarm` subscribe through cached non-capturing-lambda delegate fields (`VesselChangeHandler` / `LevelLoadedHandler`; a non-capturing lambda's `Target` is the compiler's closure singleton, non-null; the same instance keeps `Remove`'s equality match). **Pins:** `EventData<T>` is plain C# (no Unity natives), so the trap itself is now pinned headless (`KspEventDataAdd_StaticMethodDelegate_ThrowsNre...` in `FlightCameraReloadPinTests`), plus the cached-lambda idiom and a source-text gate that Arm never regresses to a naked static method group. **Verify:** re-run the FLIGHT Run All + Isolated batch: the prime must proceed (grep `Camera re-pin window armed`), the isolated tier must run (batch takes minutes again, ~441 captured), and the #1282 protection is finally live.

**Original report (2026-07-10 verify run, `logs/2026-07-10_2220_verify-run-followup/KSP.log`):** the FLIGHT batch's first isolated-test prime (`InGameTestRunner.PrimeBatchFlightBaselineBeforeFirstRestoreBackedTest` -> `RestoreBatchFlightBaselineCore`) failed with a bare `NullReferenceException` and aborted the batch at 383/441 captured results.

**Evidence chain (why the site is unrecoverable from this log):** the prime ran before `DiscardEconomyPreservationInGameTest`; all 7 `ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore` prep resets logged cleanly; NO `onGameSceneLoadRequested` handler logs appeared (so the throw landed in the window between the prep returning and `HighLogic.LoadScene` firing); the pre-wipe rollback WARN followed 1ms later. Every catch site on that path logged only `ex.Message` ("Object reference not set to an instance of an object") with no exception type or stack trace, so the throwing statement was swallowed.

**Null-safe-on-inspection candidates (all read clean, none confirmed):** `RevertDetector.ResetForTesting`, `FlightCameraReloadPin.Arm` argument interpolation, `StartAndFocusVessel` statics.

**Regression suspicion:** the previous session primed the SAME test successfully on the pre-#1281/#1282 DLL, so a regression from those PRs cannot be excluded; no mechanism was found on inspection.

**Shipped in the first diagnosability branch (`fix-prime-diagnostics`, PR #1285):**
1. Every restore/prime failure catch site in `InGameTestRunner` now ALSO logs a `ParsekLog.Error` line with the full exception detail (type + message + inner exceptions + stack trace) via the pure, xUnit-pinned `DescribeRestoreFailure(Exception)`; the test-result row keeps the short message. Sites: the prime wrapper, the per-test restore wrapper, `RestoreBatchBaselineWithRecovery` (attempt-1 retry + persistent failure; covers the final-restore and cancel-restore paths), the `PrepareBatchFlightRestoreExecution` catches, and the `CaptureBatchBaseline` isolation-capture catch.
2. The green-sphere leak seen in the same run (a leftover fallback ghost sphere riding the vessel) got a first-pass fix: `PerformPostAbortSceneCleanup` in RunBatch's always-runs batch-end region destroys the tracked `cleanupRegistry` objects and clears timeline ghost visuals (via the idempotent `PerformBetweenRunCleanup`), exception-safe, gated on the abort flag. Residual: the exception-storm abort ALSO sets `abortBatchAfterRestoreFailure` (both storm-detection sites, `TryDetectExceptionStorm` and the reload-guard flood path), so storm endings get the sweep too; the remaining uncovered ending is a user Cancel, which stops the RunBatch coroutine before the batch-end region ever runs, so a cancelled batch still leaves scene debris until the next Run* entry's `PerformBetweenRunCleanup` (pre-existing behavior, accepted).

**2026-07-10 rerun2 result (`logs/2026-07-10_2258_rerun2-fast/KSP.log` line 16364): both #1285 pieces fired but neither yielded the answer.**
- The new diagnostics captured the prime NRE, but the stack showed ONLY the reload-guard wrapper frame (`RestoreBatchFlightBaselineCoreWithReloadGuard.MoveNext` + `RunCoroutineSafely.MoveNext`). Cause: `RestoreBatchFlightBaselineCoreWithReloadGuard` re-raised the captured core failure with a bare `throw coreFailure;`, which RESETS the exception's stack trace to the rethrow site, destroying the original stack `RunCoroutineSafely` had captured from `RestoreBatchFlightBaselineCore`. The evidence window is unchanged: the throw lands after `ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore` returns and before `HighLogic.LoadScene` fires.
- The green sphere recurred despite the #1285 sweep: `PerformPostAbortSceneCleanup` ran but found nothing ("DestroyAllGhosts: clearing 0 primary + 0 overlap entries", "destroyed 0 tracked object(s)"). Root cause: the sphere was never the LIVE engine's. The `ReFlyPostLoadSettle_GhostMeshHiddenDuringWindow` in-game test (RuntimeTests.cs) builds a PRIVATE `GhostPlaybackEngine`; its `UpdatePlayback` spawned a sphere-fallback ghost visual for the snapshot-less "Re-Fly Settle Anchor" recording (log line 14881, GameObject "Parsek_Timeline_0") and the test's teardown only reset the settle tracker, abandoning the private engine WITHOUT `DestroyAllGhosts`. The orphaned mesh was invisible to every cleanup path (they all tear down the live `ParsekFlight` engine only), so the green sphere stayed riding the vessel.

**Shipped in branch `fix-restore-rethrow-stack` (diagnosability round 2 + sphere root fix):**
1. Stack-preserving rethrow: the reload-guard wrapper now re-raises via `ExceptionDispatchInfo.Capture(coreFailure).Throw()` (same exception object, original frames preserved, rethrow site appended). It was the only bare `throw capturedVariable;` in the file (all other sites use catch-callback or plain `throw;`). xUnit pin: an EDI rethrow of a captured exception preserves BOTH the original throwing frame and the rethrow wrapper frame in `DescribeRestoreFailure` output.
2. Capture-point logging: the wrapper logs `Restore core failure detail (captured at reload-guard, original stack):` with the full `DescribeRestoreFailure` detail IMMEDIATELY where the core failure is first observed (right after the `RunCoroutineSafely` yield), where the original stack is provably intact, making the diagnostics immune to any other stack-eating rethrow downstream.
3. Sphere root fix: the settle in-game test now calls `engine.DestroyAllGhosts()` on its private engine in its `finally`, destroying the engine-spawned sphere at test end. No production ghost-engine seam was at fault (the live engine never referenced the mesh).
4. Defensive orphan sweep: `PerformPostAbortSceneCleanup` gained a final step that scans root GameObjects for the engine's `Parsek_Timeline_` naming convention (pure xUnit-pinned `IsOrphanedGhostMeshName`), skips any the live engine still owns (`GhostPlaybackEngine.OwnsGhostGameObject`), destroys the rest, and reports `orphanedGhostMeshes=N` in the existing summary line. Post-abort one-shot only, exception-safe, unreachable on the normal batch path.

**Next step:** re-run the FLIGHT batch. If the prime fails again, the capture-point `Restore core failure detail (captured at reload-guard, original stack)` line (or the prime wrapper's detail line, now stack-preserving) names the true throwing statement inside `RestoreBatchFlightBaselineCore`; diagnose from there. The NRE itself remains UNDIAGNOSED.

---

## ~~FIXED~~ - Test-runner batch soft-freeze recurrence: late vessel switch destroys the FlightCamera after the pre-reload guard (2026-07-10, branch `fix-runner-reload-camera-pin`)

**Found by:** the 2026-07-10 FLIGHT Run All + Isolated re-run (`logs/2026-07-10_2114_rerun-freeze`, KSP.log lines 50840-51260): KSP soft-froze at batch end (black background, no scene change possible, ~250k per-frame NREs, 107MB log). Same stock Bug #4803 class as the 2026-07-05 freeze, but through a NEW hole the existing prevention does not cover.

**Root cause (evidence chain):** after the last isolated test (`EvaTwiceFromSameCapsuleProducesTwoBranches`, which leaves two EVA-kerbal vessels in the scene) the restore ran: (1) 21:11:57.248 the existing pre-reload camera guard (`EnsureFlightCameraSurvivesReload`) fired and re-homed the pivot onto 'Kerbal X' - the 2026-07-05 prevention worked as designed; (2) 21:11:57.464 `QuickloadResumeHelpers.CommitValidatedGameLoad` called `FlightDriver.StartAndFocusVessel(game, activeVesselIdx=9)`, which synchronously fires `onGameSceneLoadRequested`, so stock `FlightCamera.OnSceneSwitch` ran its PSystemSetup DDOL-root rescue INSIDE this call; (3) same frame, AFTER the commit returned, a LATE vessel switch to the transient EVA kerbal 'Hudmy Kerman' fired ("[FLIGHT GLOBALS]: Switching To Vessel Hudmy Kerman"; the live scene's vessel index 9, while the save's index 9 is Kerbal X - an index-space mismatch; the exact caller was never identified, so the fix must not depend on knowing it). The switch fired stock `FlightCamera.OnVesselChange`, re-parenting the pivot under the doomed EVA vessel AFTER the rescue; (4) the unload destroyed the EVA vessel with pivot+camera under it (`FlightCamera.OnTargetDestroyed` refuses to re-home when the dead target is the active vessel), `fetch` went null, and 21:11:58.216 the new scene's `FlightDriver.Start` NRE'd in `SetModeImmediate`, leaving FlightGlobals half-initialized and every per-frame consumer flooding permanently.

**Fix (test-runner/helper code only, two pieces):**
1. **Late-switch camera re-pin window** (`InGameTests/Helpers/FlightCameraReloadPin.cs`): armed inside BOTH batch-restore commit seams (`QuickloadResumeHelpers.CommitValidatedGameLoad` and `CommitNonFlightSceneLoad`, so every caller including the quickload-resume tests is covered) just before the scene-load dispatch. While armed, every `GameEvents.onVesselChange` immediately re-pins the pivot back onto the DDOL PSystemSetup root via `SetTargetTransform` (the exact stock OnSceneSwitch rescue, re-applied after the late switch; it also detaches the on-destroy callback from the dying vessel) and Warn-logs the switched vessel. The runner's restore core additionally does one unconditional end-of-frame pin after each commit (covers same-frame switches that bypass onVesselChange). Disarm: `GameEvents.onLevelWasLoaded` (fires in the new scene before Start() methods, so the new scene's legitimate `FlightDriver.Start` -> `SetActiveVessel` is never intercepted), plus a 60s realtime TTL fail-safe and explicit idempotent disarms in the runner's Cancel/batch-end/cancel-restore teardown paths (StopCoroutine skips finally blocks; mirrors the batch exception monitor teardown). All handler bodies are exception-safe.
2. **One-shot Space Center bounce recovery** (`InGameTestRunner.RunBatch` tail): after the final restore (and on the storm-abort path), the runner samples the existing flood detector over a settle window while the batch exception monitor is still live; if still flooding it logs ERROR + an on-screen message and dispatches EXACTLY ONE `HighLogic.LoadScene(SPACECENTER)` after the disk teardown + results export complete, guarded by a once-per-batch bool (pure decision `ShouldAttemptSpaceCenterBounce`, xUnit-covered).

**Explicit non-goals:** NO reload-retry loops (the disproven model - 2026-07-05 confirmed the corruption is process-permanent for the FLIGHT scene; 4 retries all flooded and produced a 469MB log; prevention + a single SC bounce are the only working models), and NO product-code Harmony patch of stock FlightCamera (test-infra only; the underlying stock bug remains KSP Bug #4803, and the idea of reporting it upstream to KSPCommunityFixes still stands).

**Tests:** `FlightCameraReloadPinTests` (arm-window decision core: ignore/re-pin/TTL-auto-disarm/disabled-TTL) + `ShouldAttemptSpaceCenterBounce` cases alongside the existing `IsExceptionStorm`/`ReloadStillFlooding` tests in `InGameTestRunnerTests`. Full suite green.

**Verify (operator):** re-run the FLIGHT Run All + Isolated batch. The batch must end with a healthy FLIGHT scene (grep for the late-switch Warn to see whether the re-pin window actually caught a switch this run). If the corruption ever recurs anyway, the runner must land you in the Space Center with a `[TestRunner]` ERROR line ("Batch ended with the flight state still NRE-flooding") instead of a frozen game.

---

## ~~FIXED~~ - S4 arrival re-stitch rotation sign inverted on live KSP (found by the 2026-07-10 in-game sweep, branch `fix-s4-restitch-bearing`)

**Found by:** the first full Ctrl+Shift+T sweep on 0.10.3 main (`logs/2026-07-10_1935_ingame-test-sweep`, 5 scenes, 2 failures). `ReaimLandingCoincidenceInGameTest.S4Restitch_RotatedDeorbitPoint_MeetsRecordedSiteAtTrigger` - the exact canary the S4 PR added for the sign question headless tests cannot prove - measured `ComputeRestitchRotationDeg` reading a live LAN=+30 orbit pair as -30 (`S4 frame validation` log line; the physical LAN-advance and body-spin rotations both measured +30 about the live spin axis `(0,-1,0)`).

**Root cause:** the adversarial-review commit (`26922fd28`, S4 BLOCKER 1) fixed the latitude AXIS (Y, not Z) but picked the bearing SENSE as `atan2(-z, x)` from the derivation "Cross(r, v_prograde) points +Y". Live KSP refutes the premise: prograde angular momentum points -Y in the world frame (`CelestialBody.angularVelocity` is -Y for prograde rotators), so `atan2(-z, x)` reads every prograde advance inverted. Production consequence: `RotateLanForParkRephase` turned the arrival chain by -theta instead of +theta (a 2*theta tear at the SOI-entry seam - the exact defect S4 exists to close); the landing site itself was never at risk (the rotation/trigger-offset pairing is sign-agnostic, so the touchdown stayed at the recorded site).

**Fix:** `ArrivalRestitch.TryBearingAndLatitude` bearing = `atan2(z, x)` (live-KSP-calibrated by the canary, not re-derived); doc comments rewritten to name the canary as the calibration source; the four sign-sensitive `ArrivalRestitchTests` pins re-pinned to the corrected sense (quarter-turn prograde/retrograde, magnitude-irrelevance, both velocity-residual cases). The connectivity / recorded-site-invariant / assembler pins are sign-agnostic and unchanged.

**Verify:** re-run the FLIGHT sweep (or just the Periodicity category) in-game: the canary must read +30/+30 with residual 0. The Duna One (s15) looped landing playtest remains the S4 merge-gate observation for the approach visibly connecting.

---

## ~~FIXED~~ - EVA spawn walkback clobbered by the degraded fallback + flaky zero-velocity landed reseed (found by the 2026-07-10 in-game sweep, branch `fix-spawn-walkback-fallback`)

**Found by:** the 2026-07-10 re-run sweep (`logs/2026-07-10_2114_rerun-freeze`, KSP.log lines 14014-14094). `FlightIntegrationTests.EvaSpawnWalkbackOnOverlap` failed with "Walkback should move the EVA off the overlapping endpoint (was 0.0 m)": the walkback found a clear position (cleared at segment [21-22]) and `OverrideSnapshotPosition` wrote it into the snapshot, yet the EVA materialized exactly at the overlapping endpoint (distFromParent=17.3 m = the original overlap distance). The 1935 sweep had passed the same test - run-to-run flaky. Beyond the test this is a real product defect: on the degraded fallback path a vessel could materialize inside an overlap the walkback had just cleared (collision/explosion risk in real gameplay).

**Root cause (two stacked):**

1. *Flaky #620 rejection of the primary spawn.* `SpawnAtPosition` rebuilds the ORBIT node via `OrbitReseed.FromWorldPosAndRecordedVelocity` with the recorded endpoint velocity. A landed endpoint with EXACTLY zero recorded velocity reseeds to h=0 / ecc=1, and KSP's `Orbit.UpdateFromStateVectors` computes SMA = -semiLatusRectum/(ecc^2-1) = 0/0 = NaN; `TryValidateFiniteMaterializationMetadata` then rejects the spawn ("orbit metadata 'SMA' is missing or non-finite", #620) and `SpawnAtPosition` returns 0. A float-residue near-zero velocity yields a finite ~r/2 SMA instead - hence the run-to-run flakiness.
2. *Endpoint clobber in the landed-like repair.* The caller fell through to the degraded fallback (`RespawnValidatedRecording` -> `BuildValidatedRespawnSnapshot` -> `TryRepairSnapshotBodyProvenance`). The landed-like repair branch unconditionally overrode the snapshot position with the recording ENDPOINT coordinates (`bool useEndpointCoords = hasEndpointCoords;`), clobbering the deliberate walkback correction.

**Fix:**

- *Deterministic landed reseed:* when the computed situation is LANDED/SPLASHED and the reseeded orbit carries any non-finite element (`OrbitReseed.HasNonFiniteOrbitElement`, pure + pinned), `SpawnAtPosition` substitutes the canonical surface orbit tuple instead (single implementation `WriteCanonicalSurfaceOrbitValues`, shared with `ApplySurfaceOrbitToSnapshot`). Non-landed situations keep the current rejection (correct there).
- *Deliberate-override stamp:* `OverrideSnapshotPosition` stamps the snapshot with `parsekDeliberatePosOverride=True`; the landed-like repair dispatches through the pure decision predicate `DecideSurfaceRepairCoordinateSource(hasStamp, hasSnapshotPos, hasSnapshotBody, hasEndpointCoords, bodyMismatch?)` -> {StampedSnapshot, Endpoint, Snapshot, Reject}. A stamped snapshot with a finite position on a non-mismatched body keeps its coordinates (the orbit is still repaired); unstamped snapshots keep the endpoint-first contract EXACTLY (that path moves stale EVA-start snapshots to the trajectory endpoint); a stamp never blocks a genuine repair (missing/NaN coords or a body mismatch still get the endpoint repair). The stamp never reaches `persistent.sfs`: it is stripped from every spawn copy before ProtoVessel load (`BuildValidatedRespawnSnapshot`, `SpawnAtPosition`, `RespawnVessel`), and (review follow-up) it is also stripped from the DURABLE `rec.VesselSnapshot` in a `finally` on every spawn-resolution exit of `SpawnOrRecoverIfTooClose` (`StripDeliberatePositionOverrideStampFromRecording`), so a dirty-recording sidecar save (`<id>_vessel.craft`) cannot carry it into a later session where spawn paths that do not re-run the resolved overrides first (KSC end spawn, ghost tip respawn) would mistake stale snapshot coordinates for a fresh deliberate override.
- Tests: exhaustive 48-row truth table for the decision predicate, non-finite-element pins, stamp lifecycle pins, and repair-precedence integration pins through `BuildValidatedRespawnSnapshot` (`SpawnWalkbackFallbackTests.cs`).

**Verify:** isolated re-run of `FlightIntegrationTests.EvaSpawnWalkbackOnOverlap` in FLIGHT (Ctrl+Shift+T): distFromEndpoint must be > 0 and the #620 "orbit metadata 'SMA'" rejection must not appear on the landed EVA spawn (grep for the new "substituting canonical surface orbit" Info line instead).

---

## Added (headless-verified, in-game pin pending) - Pre-Parsek save safety backup (branch `pre-parsek-save-backup`)

**Feature.** The first time Parsek cold-loads a save with no Parsek footprint, it copies that save - before any Parsek write - into a sibling `saves/<Name> (pre-Parsek <local-ts>)/` folder that appears in KSP's Load menu, so a player who tries Parsek and uninstalls it can return to their pristine career. Runs once per save; skips brand-new empty careers; toggle under Settings > Data Management (`autoBackupExistingSaves`, default on).

**Design.** Hook at the top of the cold-load path of `ParsekScenario.OnLoad` (gated `!initialLoadDone`, before `LoadExternalFiles`): a scenario module's `OnSave` cannot precede its own `OnLoad`, so the copied `persistent.sfs` is gameplay-state-pristine (no Parsek funds/science/crew/tech/contract/facility footprint - NOT byte-identical; the empty `SCENARIO{name=ParsekScenario}` KSP injects carries no gameplay data). Idempotency is measured from the on-disk footprint (`Parsek/` dir or a populated `ParsekScenario` node), not the in-memory OnLoad node, so a prior aborted session is caught; the marker file is only a fast-path. The copy is staged into a `.parsek-backup-staging-*` dir in the save folder (not under `Parsek/`, so a failed copy leaves no empty-`Parsek/` false footprint) and atomically `Directory.Move`d into `saves/` as the last step (a mid-copy failure never strands a half-save in the Load menu; orphan staging dirs are swept on load). Scope: persistent.sfs + loadmeta + Ships/ + Subassemblies/ (excludes quicksaves, `Parsek/`, KSP `Backup/`). Fail-open (any parse doubt backs up); fail-loud (Error + on-screen warning, no marker written -> retry next cold load). A missing on-disk persistent.sfs is skipped (and asserted before publish) so a capture failure never fabricates a payload-less "backup". Progress decision parses the on-disk file via `CareerSaveParser`, not fragile live singletons at cold OnLoad. `PreParsekBackup.cs` + `FileIOUtils.CopyDirectory`.

**Tests.** 36 xUnit cases in `PreParsekBackupTests.cs` (ShouldBackup truth table with pinned reason literals incl. footprint-beats-brand-new, SanitizeSaveName, BuildBackupFolderName format + collision, IsBrandNewEmptySave fail-open, HasParsekGameplayFootprint empty/value-only/populated node, IsParsekBackupFolder sentinel/name, CopyDirectory tree/exclude/no-op/failure-warn, settings round-trip + defaults). Full settings suite green.

**PENDING OPERATOR (in-game pin, cannot run KSP headlessly).** These KSP-runtime properties are not unit-testable:

1. **V2/V3 (pristine timing).** Back up a real pre-existing career (make a manual copy of a `saves/<Name>` first), install this DLL, load that career once. Grep `KSP.log` for `[Backup] First-contact backup:` and `[Backup] Captured pre-Parsek backup:` and confirm both appear BEFORE the first Parsek OnSave line for that session. If instead you see `[Backup] Skip: reason=already-parsek-footprint` on a save you know never had Parsek, the OnLoad-before-OnSave assumption is wrong and needs the earliest-pre-write-seam fallback.
2. **V1 (appears in Load list).** After step 1, open Resume Saved Game and confirm the `<Name> (pre-Parsek <ts>)` folder is listed and loads. Note whether its card shows the source save's title (copied `.loadmeta`); if that is confusing, regenerate the title or rely on the folder name.
3. **Idempotency.** Reload the same save several times / quickload / revert: `[Backup]` must log `Skip: reason=marker-present` (or `already-parsek-footprint`) and create no second folder. Then resume the pre-Parsek backup itself: it must log `Skip: reason=is-backup-folder` and not back up the backup.
4. **Disabled + brand-new.** With the setting off, a first-contact save logs `Skip: reason=disabled`. A brand-new empty career (Parsek installed) logs `Skip: reason=brand-new-empty` and gets no twin.

---

## Backlog - prioritized "what to develop next" (compiled 2026-07-06, v0.10.3)

Session-compiled prioritized development backlog (survey of git log / open PRs / roadmap / design docs / this file). Ordering doctrine: correctness-first, land-shipped-work-before-new, gameplay-value-per-effort. Two premises corrected during the survey: (1) `roadmap.md` §19.4 lags - logistics **M1-M4 are all SHIPPED** in 0.10.3 (M5 inter-body + M6 legibility were the last two, both since MERGED - see the Tier 1 CLEARED note below); (2) there is **no CI** on the repo (`get_status` = 0 checks), so "ready" PRs are review-gated only (suite run locally).

### Tier 1 - CLEARED (2026-07-11): merge queue drained
Every Tier 1 merge-queue item below LANDED on `main` (verified 2026-07-11 via `gh pr view`; `gh pr list --state open` returns 0 open PRs). Kept here for history:
- **#1242** (logistics Rec-1 rewind-redelivery) - MERGED (gate playtest passed 2026-07-08). Was the one open correctness bug (rewind past a route dispatch charged funds but never re-delivered cargo).
- **#1237** (M-MIS-11 loop-unit API) - MERGED. Keystone zero-behavior refactor.
- **#1239** (M-MIS-5 P1 dock-as-interval-boundary) - MERGED.
- **#1238** (Logistics M5 inter-body) - MERGED (gate passed in-game 2026-07-08). Last logistics "Reach" milestone.
- **M6 legibility batch** #1232 / #1233 / #1234 / #1235 / #1236 - all MERGED.
- **#1220 / #1221** - CLOSED as superseded by #1242 (their docs shipped inside it).

### Tier 2 - NEXT: highest value-per-effort new work
- **Rec-3 reverse-on-discard** - RESOLVED (2026-07-06, option C): the observability slice SHIPPED (PR #1243, branch `claude/development-priorities-ftr2ye`, stacked on #1242) and both-persist is RATIFIED as correct; reverse writers are DECLINED, not built. The attribution blocker (ambient route rows carry no RecordingId, so a UT-window reverse would wrongly undo concurrent committed routes) plus the 0.10.2 preserve-live-earned-gameplay doctrine make keeping both funds + cargo the correct behavior. No further code work. See `docs/dev/plans/fix-logistics-rewind-determinism.md` Phase 4.
- ~~**Map-view route lines** (M6 gameplay, M) - the one unbuilt M6 gameplay item; draw route paths on the map/TS via the MapRender Director surface. Reuse `GhostTrajectoryPolylineRenderer`.~~ SHIPPED (M6, verified 2026-07-11). `Display/RouteTrajectoryLineRenderer.cs` walks `RouteStore.CommittedRoutes`, reuses `GhostTrajectoryPolylineRenderer.BuildLegsForRecording` + `TryDrawLeg`, clips each route to `RecordedDockUT`, and draws on the flight map + Tracking Station behind the `showRouteLines` setting (default on); same-body routes draw all recorded non-orbital legs, inter-body routes draw the endpoint-body legs. Shipped in commits `008bb30bb` + `7b298582d` (inter-body follow-up), xUnit + in-game covered. Remaining deferred slice (by design, not "unbuilt"): the static orbital-coast overview arc stays head-gated on the stock conic.
- **M-MIS-5 P2** (L) - lift the undock->undock shuttle mid-recording start-trim limitation (`MidRecordingStartTrimUnsupported=9`); unlocks multi-stop shuttle logistics routes rejected today. Prereq: #1239.

### Tier 3 - LATER: verification + hygiene
- **Validation debt (the real bottleneck)** - ~13 code-complete-but-in-game-unconfirmed fixes, clustering onto ~4-5 playtest sessions: (1) career-economy (Rec-1 #1242, career-freeze milestone-storm, contract-discard-desync, OnMainMenuTransition); (2) looped re-aim descent-render (reaim-descent cluster, arc truncation, M-MIS-2 P4, cross-SOI encounter observation); (3) eccentric-target Eeloo/Moho constant pinning (M-MIS-3); (4) cross-parent station resupply (M4c); (5) in-game test-runner camera-survival batch. KSP cannot run headless, so this is playtest-bound.
- **M-MIS-10 archetype verification sweep** - constellation deploy / booster flyback / off-Kerbin launch / claw couples / Elcano; cheap verify-and-file, no known break.
- **Remove `MapRenderWarpControl`** temporary debug aid once re-aim descent-render is signed off.
- **Doc hygiene** - flip the stale "In progress - Forward trajectory rendering" header (shipped 0.10.2) + add SHIPPED markers to roadmap §19.4 M3/M4.
- **Deferred re-aim solver follow-ups** - M-MIS-2 S4 re-stitch (product-decision-gated), leg-less-chain forward-run gap. Low-severity polish. (`SolveArrivalWindow` wiring SHIPPED on branch `mmis4-solve-arrival-window` - see the M-MIS-4 entry.)

### Tier 4 - LONG-HORIZON: the strategic arc
- **Gloops extraction -> Gloops.dll** (XL) - gateway to multiplayer. Docs UNDERSTATE the effort (engine coupling re-accreted; a parallel Gloops recorder #435 to consolidate first) AND the real user-facing prerequisite is the `.gloop` file format + export/import, NOT the assembly split (`.prec` is already a `.gloop` superset), so export/import can be built on existing serialization and the split treated as a parallel code-health track. Don't start until logistics/missions are done - every in-flight feature still edits the engine files.
- **Phase 14 co-op async multiplayer** -> **Phase 15 space race** -> **Phase 16 mod compat**.
- **Parked mission shapes** M-MIS-6 (multi-moon, needs a design note) / M-MIS-7 (intra-SOI re-aim, gated on M-MIS-6) / M-MIS-8 (cross-tree foreign dock, low value). Hold pending a concrete player ask.

### Open maintainer decisions (surfaced this session)
- **Rec-3**: RESOLVED 2026-07-06 - ratified both-persist as correct (option C); reverse writers declined. See Tier 2 / plan Phase 4.
- **Rec-2** (inter-body route hard-block): left creatable; open product decision (report risk #12/#13).

Healthy / no action needed (verified this session): the ledger/economy audit (all 5 recs shipped), the observability plan (landed), the render rewrite (cutover complete, no visible artifacts left). The pure-refactor backlog is low-ROI - ride it along with features.

---

## Dev - Logistics in-game tests: auto-spawn unloaded vessel (no manual second craft)

The 7 logistics FLIGHT in-game tests (origin-debit / pickup / multi-stop delivery,
`InGameTests/Logistics*RuntimeTests.cs`) need a live FLIGHT active vessel. The
LOADED-path tests use the ActiveVessel directly - a fueled PRELAUNCH pad rocket
satisfies them after `WaitForActiveVesselUnpack` (they check `loaded && !packed` +
an LF tank; no test rejects PRELAUNCH on `vessel.situation`, so no relaxation was
needed). The UNLOADED-path tests need a SEPARATE on-rails (unloaded) vessel with
LiquidFuel and used to SKIP whenever the save had none, forcing the player to
hand-place a second vessel.

Maintainer-chosen design: "use my pad rocket + auto-spawn the rest". New shared
fixture `InGameTests/Helpers/UnloadedFuelVesselFixture.cs`:
`EnsureUnloadedLiquidFuelVessel(minStoredLf, minFreeCapacity, result)` (coroutine)
(a) reuses any suitable pre-existing unloaded vessel (fast path, behavior-identical
for saves that already have one); else (b) snapshots the ActiveVessel via
`VesselSpawner.TryBackupSnapshot`, rewrites its LiquidFuel RESOURCE amounts via the
pure `AdjustSnapshotLiquidFuel` (>= minStoredLf stored, >= minFreeCapacity free,
flowState forced True) and spawns a FRESH-identity copy (preserveIdentity:false ->
regenerated pid, no collision) into a high (~250 km) parking ORBIT far from the
active vessel via `VesselSpawner.SpawnAtPosition(..., orbitOverride)` so KSP keeps
it on-rails / unloaded; (c) waits a bounded number of frames for the spawn to
register in `FlightGlobals.Vessels` AND settle unloaded, resolving by the returned
pid; (d) on any failure leaves `result.Vessel == null` so the caller falls back to
the existing `InGameAssert.Skip` (never worse than before). Cleanup: a SPAWNED
vessel is removed via `Vessel.Die()` + protoVessels drop in the test's finally
(`UnloadedFuelVesselFixture.Cleanup`); the batch baseline restore is the backstop.
Rewired tests: `OriginDebit_UnloadedOriginVessel_WritesProtoSnapshot`,
`OriginDebit_UnloadedDebit_SurvivesKspSaveRoundTrip`,
`MultiStop_UnloadedEndpoint_DeliversAtBothDocks`,
`PickupDebit_UnloadedEndpointVessel_WritesProtoSnapshot` (the per-suite
`TryFindUnloaded*` finders were folded into the fixture). The
inventory-pickup tests are unchanged (no unloaded variant; an unloaded inventory
fixture would need a stored cargo part the pad rocket may lack). Pure piece unit-
tested in `Source/Parsek.Tests/UnloadedFuelVesselFixtureTests.cs`. Test-infra only
(no user-facing CHANGELOG line).

**LIVE validation DONE (2026-07-10 sweep, `logs/2026-07-10_1935_ingame-test-sweep`):**
the full FLIGHT Run All + Isolated pass ran every rewired Logistics test green except
`Escrow_CompetingRouteSeesReservation_Holds`, which failed on a STALE ASSERTION, not
production: the test still pinned the pre-M6 physical hold token prefix (`source:`)
while the gate correctly emits the M6 escrow-legibility token
(`source-reserved:<pid>:<name>:<resource>:<reservingRoute>`, PR #1233 - the test was
written on the M4b branch in parallel and never updated). Fixed on branch
`fix-escrow-ingame-token`: the assertion now pins the M6 escrow contract (the
`source-reserved:` prefix - this scenario is escrow-caused by construction - plus the
pid and the reserving route A as the final token segment, with the two test route ids
given prefixes distinct within `RouteIds.Short`'s 8 chars so the pin can tell A from
B; both previously truncated to the same `ingame-e`). Test-only; re-verify with one
isolated re-run of the test in FLIGHT.

## TODO - Missions feature completion milestones (M-MIS roadmap; investigated 2026-06-10)

The single ordered list of what remains to call the Missions feature (`docs/parsek-missions-design.md`, shipped core) COMPLETE. Ordered by necessity / priority: each milestone was code-investigated on 2026-06-10 (implemented-already? viable? what exactly remains?) and the findings are recorded inline. Detailed history for the completed milestones lives in the `done/todo-and-known-bugs-v7.md` archive (cross-referenced); this list is the planning surface.

### Reuse mandate (applies to every solver-flavored milestone below)

Do NOT re-implement intercept / window math from scratch. The 2026-05-28 prior-art survey (recorded in `docs/dev/done/plans/reaim-interplanetary-transfers.md` + the prior-art note in the phase-lock entry below) already settled the sourcing:

- **`Reaim/UvLambert.cs`** is OUR owned, unit-tested (Curtis Algorithm 5.2) full-3D universal-variables Lambert solver. Extend it; do not replace it.
- **`Reaim/ITransferSolver.cs`** is the deliberate swap seam. The sanctioned fallback if UvLambert robustness proves insufficient (multi-rev, near-180-deg singularity) is porting **MechJebLib's Gooding solver** (permissive license: public domain / Unlicense; ~577 lines + `V3`/`Statics` deps) behind that seam - a port, not a rewrite.
- **`Reaim/TransferWindowMath.cs`** already carries the KerbalAlarmClock-derived (MIT, attributed) phase-angle + synodic math. TransferWindowPlanner2's porkchop grid was evaluated and deliberately NOT needed (the congruent-window model uses recorded tof + synodic spacing). KSTS / Principia: surveyed, not applicable.
- The launch-side zero-drift near-coincidence primitive (`MissionPeriodicity.NextJointNearCoincidenceUT` / `TryBuildRelaunchSchedule`) and `Reaim/DestinationArrivalSolver.SolveArrivalWindow` (WIRED since the M-MIS-4 post-M4c follow-up, branch `mmis4-solve-arrival-window`: hold-aware sampling + the joint-hold lattice feasibility scan, consumed by `ArrivalHoldPlanner.ComputeJointArrivalHold` for the D8 landing+station dual) are the in-repo multi-constraint window search. New milestones REUSE these, never re-derive them.

### M-MIS-6 - Multi-moon destinations: the looped "Jool-5" mission, window-alignment cut [BUILT, needs the in-game looped Jool playtest]

- **Investigated 2026-06-10 (answers the open uncertainty):** today a Jool-5 recording loops on the FAITHFUL path only: `ReaimClassifier` supports the Kerbin->Jool transfer (Jool is a direct Sun child) but `DestinationConstraintExtractor` fails closed at 2+ SOI-entered moons, so nothing aligns the moons; each moon-relative block self-anchors to the LIVE moon while the Jool-centric inter-moon arcs replay inertially, so every encounter seam renders disconnected (the Mun-desync mechanism, once per moon). What makes it tractable WITHOUT new math: (a) all encounters shift TOGETHER under one arrival hold, so alignment needs the moons' joint CONFIGURATION to recur, not each moon independently; (b) stock Laythe:Vall:Tylo are a near-exact 1:2:4 resonance (period ratios off by ~1e-5 from rounded SMAs), so the inner-three configuration recurs every Tylo period (~211,926 s) to well within SOI tolerance - a per-loop hold in `[0, T_config)` aligns an inner-three tour exactly like the shipped `W_N` destination-rotation hold (substitute T_config for T_rot); (c) the stock major moons are tidally locked, so landing-rotation constraints collapse into orbital phase (the tidal-lock collapse `MissionPeriodicity` already implements); (d) Bop/Pol are incommensurate with the inner three - a full 5-moon tight alignment is effectively non-recurring, so those legs get Loose tolerance via the near-coincidence search or the mission fails closed to faithful (a VALID outcome, surfaced in the UI, never silent).
- **Requirements:** (1) ~~short design note first~~ DONE - `docs/dev/design-mission-multimoon-alignment.md` (decisions D1-D8; the "2+-moon mini star systems" deferred item, `docs/parsek-missions-design.md` sect. 14.4); (2) ~~REUSE the SolveArrivalWindow wiring + generalize the per-loop hold~~ DONE; (3) ~~failing synthetic multi-moon test BEFORE any knob math~~ DONE (11 fixtures verified failing pre-implementation); (4) intra-SOI re-aim (per-leg Lambert re-solves inside the destination system) is explicitly the SECOND cut, tracked as M-MIS-7 - only justified if this hold-based model proves insufficient in playtest.
- **BUILT (branch `claude/mmis6-multi-moon-window-7fcpyh`, stacked on `mmis4-solve-arrival-window`; design `docs/dev/design-mission-multimoon-alignment.md`):** `DestinationConstraintExtractor` now EMITS the 2+-moon set (Supported, all MoonConfigs in `Constraints`, constrained-moon landing rotations in the new `MoonRotations` field; the `MaxConstrainedMoons` reject + constant are retired, and station-bearing Jool-class shapes fall to the station+moon reject). `ArrivalHoldPlanner.ComputeMultiMoonConfigHold` owns the shape: participants = moon Orbitals (SOI tolerance, never dropped) + moon/target rotations (mode ladder; Drop removes them; a tidally locked moon's rotation collapses into its orbital period for free), T_config = k*P_anchor via `MissionPeriodicity.TryFindNextScheduleK` with the smallest-duty anchor (`SelectAnchorConstraintIndex` rationale - Vall for stock, k=2, T_config ~= T_Tylo ~= 211,924s), slack-clamped anchor budget (64), engage double-gated on the scan + the hold-aware `SolveArrivalWindow` window-1 pick (the M-MIS-4 wiring, `holdAlignPeriodSeconds = T_config`, `maxWholeHoldPeriods = 0`). The clock is UNCHANGED: the config hold rides the shipped single-period per-loop path via `LoopUnit.ArrivalAlignPeriodSeconds = T_config` (no new LoopUnit/persisted fields). HONEST FINITE HORIZON (the design's correction to the investigation's recurrence claim): the resonance drifts ~0.6s/2.2s per T_config on the Vall-anchored lattice, so alignment holds for ~40 consecutive synodic windows under Loose (a Tylo-anchored lattice would give only ~8 - why the anchor is duty-selected), then leaves tolerance for centuries; the count is computed (`DestinationArrivalSolver.CountAlignedWindowPrefix`, reporting-only) and logged in the `ARRIVAL HOLD kind=config` line (`alignedWindows=`). EVERY decline ambers (never silent - the old silent no-station Jool-class None is gone): non-recurring configs (Bop/Pol, non-locked moon rotations, Jool-landing rotation under Loose/Tight), slack-starved holds, destination-side loiter cuts (L8), degenerate window spacing. `DestinationLoiterTrim` gained the `ConstrainedMoonCount >= 2` exclusion (the rotation-only trim would misalign the configuration). Tests: `MultiMoonAlignmentTests` (stock-value synthetic Jool system; engage + per-loop all-encounters-within-SOI sweep + amber polarity + byte-identity pins) + `Build_ReaimJoolMultiMoonTour_EngagesConfigHold` (builder E2E) + 3 revised pre-M-MIS-6 pins (extractor emission, station+moon reason ownership, never-silent decline).
- **MERGE GATE - AUTOMATED (2026-07-08):** `JoolConfigHoldInGameTest` (in-game, Category "Missions", SPACECENTER, batch-safe) is the merge gate. It drives the REAL `ArrivalHoldPlanner.ComputeArrivalHold` (through the REAL `DestinationConstraintExtractor` + `DestinationArrivalSolver` + `MissionPeriodicity` chain) against the LIVE Jool body graph via `FlightGlobalsBodyInfo.Instance` - which is exactly what headless could not do (the `MultiMoonAlignmentTests` xUnit fixtures pin the stock periods/SOI/velocities as constants; only an in-game run proves the SHIPPED ephemerides lock 1:2:4 and engage). Test A: the resonant inner three (Laythe/Vall/Tylo, live periods) engages the config hold, T_config is a whole multiple of the live anchor period and lands within one live Tylo period, and the single-period per-loop hold re-aligns every moon encounter within its live SOI tolerance across the horizon. Test B: adding live incommensurate Bop fails the whole set closed to faithful with an amber naming the shape. Skips cleanly on a non-stock pack / rescaled resonance (probes the live 1:2:4 lock first). Runbook: one Ctrl+Shift+T Run All in any stock save.
- **M-MIS-7 go/no-go:** observational evidence from a real looped Jool tour (encounter seams rendering connected across aligned windows, the amber/faithful outcome on an incommensurate shape) remains wanted; collect it opportunistically from normal play (not a merge blocker).
- **Viability:** ~~moderate~~ built - the resonant-inner-three + tidally-locked case maps onto shipped primitives; the general (Bop/Pol, non-resonant packs) case intentionally fails closed with amber (the design records the align-the-resonant-subset alternative as deferred to M-MIS-7 evidence).

### M-MIS-7 - Intra-SOI re-aim and multi-hop targets (Jool-like systems second cut; Ike-class targets) [GATED: on M-MIS-6 playtest evidence]

- **What it is:** the recursive "mini star system" model - re-solving transfer legs INSIDE a destination system instead of only the heliocentric leg. Two consumers: (a) **moon-to-moon legs of a multi-moon tour** when the M-MIS-6 hold-based joint-configuration model is insufficient (non-resonant moon packs, Bop/Pol legs, long inter-moon loiters): per-leg Lambert re-solves in the gas giant's frame + per-leg holds at each moon-SOI seam; (b) **multi-hop TARGETS** - a target that is not a direct child of the common ancestor (Ike via Duna; rejected today by the `ReaimClassifier` single-hop guard, ReaimClassifier.cs:124-130): re-aim the heliocentric leg to the parent, then the in-SOI hop to the moon is the same intra-SOI machinery.
- **Requirements:** REUSE everything - `UvLambert` is body-agnostic (mu is a parameter), so the same `ITransferSolver` seam serves Jool-centric solves; the per-loop hold clock primitives generalize per leg. This is a genuine new subsystem (per-leg seams, recursive window scheduling): budget a full design note + the failing-test-first discipline, and do NOT build it speculatively - M-MIS-6's playtest decides whether it is needed at all.
- **Viability:** hard; deliberately last among the solver milestones.

### M-MIS-10 - Scenario verification sweep: believed-supported archetypes, never explicitly verified [not sequenced - run incrementally alongside any milestone]

A 2026-06-10 online sweep of what KSP players actually fly (stock career contract types: satellite/relay, rescue, tourism, asteroid redirect, station resupply / crew rotation; the classic community challenges: Jool 5, Elcano, K-Prize, grand tours, Eve return; and the automation prior art Parsek overlaps with: Routine Mission Manager, KSTS, FMRS, MKS supply chains) found NO missing alignment subsystem beyond M-MIS-1..9 - but it surfaced a set of archetypes the recorder/missions stack should support TODAY that have no explicit test or playtest. Each needs a cheap in-game verify (file a todo entry where it breaks):

- **Constellation deployment** (resonant-orbit carrier releasing N relay sats, the CommNet career staple): an N-fork controlled-decouple tree where every branch ends in a perpetual-orbit terminal. Verify the fork-tree records, the Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end.
- **Reusable booster flyback** (FMRS-class profile - the recorder's home turf): booster = controlled-decoupled child flown back to a landing. Verify the branch loops with the main mission and the booster's landed terminal spawns/recovers correctly.
- **Launch from a NON-Kerbin body** (Eve return ascent, Mun surface -> orbit, Laythe spaceplane): `Rotation(B)` / `launchBodyName` handling is generic by construction through the zero-drift scheduler, but every test and playtest to date launches from Kerbin. Verify phase-lock + pad anchoring for an off-Kerbin launch site (also exercises rewind-from-surface there).
- **Claw couples** (asteroid / derelict grabs): verify a claw `OnPartCouple` records as a Dock-equivalent branch point, and that a claw-coupled asteroid (PotatoRoid part) survives ghost-visual building and the snapshot part-name path.
- **Long surface expeditions** (Elcano-class rover circumnavigation, days of driving): no alignment problem (surface sections are rotation-locked and render correctly at any UT) but a recording-size / optimizer / polyline-budget STRESS case; measure before declaring supported.
- **Round-trip resupply with vehicle reuse** (the Routine Mission Manager marquee profile: outbound dock, return, recover): the Missions side (whole-tree span loop incl. the return leg) should already work; the delivery-AND-recovery-per-cycle economics are logistics-roadmap territory - verify the rendering half here, leave the ledger half to logistics M1-M6.
- **Suborbital tourist hop** (career tourism staple): atmospheric-only -> unconstrained free loop; should be the trivial case - one verify run.

#### Verification sweep run 1 - automated pass + operator runbook (2026-07-06)

**Environment.** KSP 1.12.5. Parsek 0.10.3, origin/main @ `d5068e679` (PR #1235). Deployed DLL `sha256 aa4a5887bbd9146a39f923fe2209564c262077f8a36c1c10f5c11d7b1010a55e`, byte-verified equal to the worktree build (`Source/Parsek/bin/Debug/Parsek.dll`). Headless xUnit suite on this commit: 16842 passed / 0 failed / 1 skipped (25 s).

**Scope honesty note - read before trusting the table.** This run was performed by a CLI agent that CANNOT pilot KSP or observe on-screen rendering. Every M-MIS-10 acceptance criterion is an in-game OBSERVATION (loop cycles across FLIGHT / Space Center / Tracking Station, ghost-icon-rides-its-own-orbit-line, non-orbital legs not gliding below terrain, camera hand-offs at stage boundaries, re-aim plane fidelity, line jitter on pan). NONE of those were observed here. The table asserts NO in-game PASS/FAIL; it records only (a) the automated verification that WAS run and (b) the automated-coverage status of the machinery each archetype exercises. The observational cells are OPERATOR-REQUIRED and NOT YET OBSERVED - the per-archetype runbook below is what an operator runs to fill them in. The KSP.log currently in the install is save `s15` / Parsek V0.10.0 (a logistics-branch session), NOT an archetype run and NOT this build, so it is not archetype evidence; no collect-logs snapshot was fabricated from it.

**Per-archetype status** (the 7 archetypes above; the task working matrix was the first 5). Result column values: `AUTO-PARTIAL` = machinery has headless/in-game coverage but no dedicated end-to-end test of this shape; `AUTO-NONE` = no meaningful automated coverage; observational verify is `PENDING-OPERATOR` in all rows.

| # | Archetype | Machinery automated-coverage | Dedicated end-to-end test | Observational verify | Log-snapshot label |
|---|-----------|------------------------------|---------------------------|----------------------|--------------------|
| 1 | Constellation deploy (N-fork decouple -> N orbit terminals) | AUTO-PARTIAL: controlled-decouple + fork (`DecoupledSubtreeAudioStopTests`, `ControlledChildParentAnchoredPlaybackTests`, `RewindForkSegmentPhaseTests`, `ParentAnchoredChildSpineInGameTest`) + terminal spawn (`SupersedeCommitTests`, `PostSpawnTerminalStateTests`) | NO | PENDING-OPERATOR | none yet |
| 2 | Booster flyback (decoupled child flown to landing) | AUTO-PARTIAL: stage split + landed terminal (`BoosterStagingSplitTriggerTests`, `LandedGhostClearance_*` in-game, `MergeLandedReFlyCreatesImmutableSupersede`); reusable synthetic `Booster Drop`+`Booster Drop SRB` pair | NO | PENDING-OPERATOR | none yet |
| 3 | Off-Kerbin launch (Mun/Eve/Laythe pad + phase-lock) | AUTO-PARTIAL (2026-07-07, coverage run 2 - was AUTO-NONE): dedicated headless fixtures run the REAL `ExtractConstraints` + `TryBuildRelaunchSchedule` for a Mun PAD launch (`MissionPeriodicityTests.Extract_MunPad*` / `Extract_MunLaunchKerbinReturn_*`, `MissionZeroDriftScheduleTests.BuildSchedule_MunPad*` / `SelectAnchor_MunPad*`) incl. the Mun-launch + Kerbin-return cross-parent decline; in-game `RealSave_OffKerbinLaunchMission_PadAnchorsToLaunchBodyRotation` (Missions category) validates a committed off-home-pad mission against the live body graph + builder wiring, skipping cleanly when the save has none. No real off-Kerbin launch has been FLOWN + committed yet (rewind-from-surface off Kerbin still unexercised) | NO | PENDING-OPERATOR (HIGH RISK) | none yet |
| 4 | Claw couples (PotatoRoid grab as Dock-equivalent) | AUTO-STRONG since the claw producer (branch `logistics-claw-producer`, 2026-07-08): xUnit `ClawProducerTests` (classifier truth table, kind admission, empty-grapple skip, mid-run grab tree, codec + hash pins, PotatoRoid part-name pin) + in-game `LogisticsGrapple` category incl. the isolated-tier `GrappleCaptureInGameTest` automated gate (real `Part.Couple`/`Part.Undock` cycle on spawned live claw + PotatoRoid parts: Grapple stamping, EVA-suppression silence, window capture + undock completion, asteroid ghost-visual geometry, structural-grab admission verdict; one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene; the gate self-discards the ephemeral auto-record session in setup, so no pre-run operator action is needed); plus coverage run 2 (2026-07-07): `ClawCoupleRecordingTests` pins the Dock-equivalent branch point, asteroid partner resolution + route eligibility, breakup-scan rejection of the raw asteroid AND the post-grab merged ship, and the PotatoRoid snapshot part-name path (+ `VesselSnapshotBuilder.ClawedAsteroidShip` generator), and in-game `ClawCouple` category (`ClawCoupleInGameTest`) verifies PotatoRoid/GrapplingDevice PartLoader resolution incl. the underscore->dot leg and that a synthesized pod+claw+PotatoRoid snapshot survives ghost-visual building | NO | PENDING-OPERATOR narrowed to the stock 0.06 m contact-capture FSM + a full gameplay route cycle (collect opportunistically) | none yet |
| 5 | Elcano / endurance rover (long surface traverse) | AUTO-PARTIAL: surface-relative render + clearance (`Pipeline_Terrain_RoverClearance_StaysConstant`, `LandedGhostClearance_*` x5, `HorizonRotationNearSurface`); no long-recording size / optimizer / polyline-budget stress | NO | PENDING-OPERATOR | none yet |
| 6 | Round-trip resupply (render half) | AUTO-PARTIAL: whole-tree span loop (`MissionLoopUnitBuilderTests`, `MissionCompositionTests`) + dock composition (`MissionDockCompositionRuntimeTest`) | NO | PENDING-OPERATOR | none yet |
| 7 | Suborbital tourist hop (atmospheric-only free loop) | AUTO-PARTIAL: atmospheric polyline (`GhostTrajectoryPolylineBuildTests`) + free-loop span clock (`MissionPeriodicityTests`) | NO | PENDING-OPERATOR | none yet |

**Two highest-risk UNVERIFIED cells** (an operator run should prioritize them): #4 Claw couples (automated halves landed with the claw producer 2026-07-07, but the REAL contact capture at 0.06 m, the release split, and the PotatoRoid ghost-visual build have still never run live) and #3 off-Kerbin launch (pad anchoring + synodic / pad-aligned phase-lock + rewind-from-surface off Kerbin never run in-game). These are pre-existing VERIFICATION GAPS, not observed regressions - do not read them as bugs until an operator run shows a break.

**Coverage run 2 (2026-07-07, branch `claude/m-mis-10-coverage-gaps-q2j54e`) - automated tests for the two highest-risk cells.** Both #3 and #4 flipped AUTO-NONE -> AUTO-PARTIAL (see the table); the OBSERVATIONAL cells stay PENDING-OPERATOR and the runbook labels (`mmis10-offkerbin`, `mmis10-claw`) are unchanged. Findings from the investigation (none are observed breaks):

- **Claw couple routing CONFIRMED shared with dock (no defect):** KSP's claw (`ModuleGrappleNode`, via `Part.Couple`) fires the same `GameEvents.onPartCouple` Parsek subscribes to (`ParsekFlight.cs:1200`), and `ParsekFlight.OnPartCouple` has no docking-port / module-type filter, so a claw grab takes the identical tree dock-merge path (`HandleTreeDockMerge` -> `CreateMergeBranch(BranchPointType.Dock, ...)` -> `BuildMergeBranchData`). The sweep's "records as a Dock-equivalent branch point" claim holds by construction; now pinned in `ClawCoupleRecordingTests`.
- **Cosmetic gap, filed for awareness (not fixed):** `BranchPoint.cs:49` lists `"CLAW"` as an intended `MergeCause` value, but `ParsekFlight.GetMergeCauseForBranchType` (`ParsekFlight.cs:5143`) only ever emits `"DOCK"` / `"BOARD"` - a claw grab records `MergeCause="DOCK"`. Purely cosmetic today (nothing branches on a CLAW cause); differentiating it later is a conscious contract change against the pins in `ClawCoupleRecordingTests`. (Update, claw-producer merge: the CONNECTION KIND half of this finding is superseded - the live path now stamps `RouteConnectionKind.Grapple` via `ConnectionProducerClassifier`; the `ClawCoupleRecordingTests` DockingPort pin exercises the `BuildMergeBranchData` default-parameter fallback, which is unchanged. The `MergeCause="DOCK"` half still holds.)
- **PotatoRoid ghost MESH contribution is prefab-dependent (reported, not asserted):** stock asteroids build their procedural mesh at runtime via `ModuleAsteroid`, so the PotatoRoid prefab may contribute no static mesh to a ghost. `ClawCoupleInGameTest.ClawedAsteroidSnapshot_SurvivesGhostVisualBuild` hard-asserts the part RESOLVES (`skippedPrefab == 0`) and the build survives, and logs whether the asteroid contributed a mesh - the operator run should eyeball whether a grabbed-asteroid ghost looks acceptable without the rock.
- **Periodicity/scheduler Kerbin-assumption audit came back clean:** the extraction + zero-drift scheduler production path has NO home-body hardcoding - `LaunchBodyName` derives purely from the earliest recorded surface/orbit body (`MissionPeriodicity.cs:414`), `FlightGlobalsBodyInfo` reads all periods live off `CelestialBody`, and every `"Kerbin"` literal in production is a codec/deserialization fallback, a UI day-length constant, or a KSC-specific classifier. One deliberate design-scoped gate noted: logistics route origin proof requires a NAMED Kerbin launch site (`RouteAnalysisEngine.IsKscOriginRecording`, `RouteAnalysisEngine.cs:835-840`, mirrored in `RouteBuilder`), so an off-Kerbin PAD-origin supply route classifies as undocked-start (M1 workflow gate) rather than KSC-origin - logistics-roadmap territory, not a missions-path bug.

**Operator runbook** (each archetype: fly-or-reuse -> commit -> configure looped Mission in the Missions tab -> observe a few cycles in FLIGHT + Space Center + Tracking Station; `python scripts/collect-logs.py <label>` immediately after each run; then grep the collected `KSP.log`):

- Common per-cycle checks (all archetypes): mission loops as a UNIT on the shared span clock; relaunch cadence is sane for the shape (atmospheric = continuous free loop; interplanetary = SYNODIC cadence via window index `k` + continuous arrival hold + `PadAlignLaunch`); self-overlap (period < span) staggers instances; re-aim (if the shape has a transfer) resolves OR cleanly declines to faithful, never a broken / off-plane arc; the ghost icon rides its OWN orbit line; non-orbital legs (surface / atmospheric / descent) draw and RETIRE cleanly (no sub-surface glide, no blink-out at hand-offs, no doubled / jittering line on pan); watch-mode camera hands off between stages without losing the vessel. ACCEPTED RESIDUAL (note if seen, do NOT file as new): body-fixed burn arcs rendering ROTATED under a station / arrival hold - cosmetic-under-hold only.
- Ctrl+Shift+T in-game test runner: run before/after each archetype to confirm the integrated build is green in the live scene. Relevant categories: `Reaim`, `Loop`, `MapRender`, `Watch`, `Missions`/`MissionPhasing`, `ParentAnchored`, `Descent`. Results auto-export to `parsek-test-results.txt` at the KSP root (collect-logs.py grabs it).
- (1) Constellation: build an N>=3 payload carrier with N controlled decouplers into distinct orbits; verify fork-tree records N branches, Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end. Label `mmis10-constellation`. Grep: `[Parsek][*][TerminalSpawn]`, `[Parsek][*][Fork]`, `needsSpawn=`, any `WARN`/`EXCEPTION`.
- (2) Booster flyback: two-stage craft, controlled-decouple the booster and fly it back to a landing (reuse the `Booster Drop` synthetic to eyeball the branch first). Verify the booster branch loops with the main mission and the landed terminal spawns/recovers. Label `mmis10-flyback`. Grep: `[Parsek][*][TerminalSpawn]`, `landed`, `recover`, `ParentAnchor`.
- (3) Off-Kerbin launch: launch from a non-Kerbin surface (Mun pad-equivalent is simplest). Verify pad anchoring + phase-lock relaunch and rewind-from-surface there. Label `mmis10-offkerbin`. Grep: `launchBody`, `PadAlign`, `PhaseAnchorUT`, `[Parsek][*][Relaunch]`.
- (4) Claw couples: the event pipeline, window stamping, asteroid ghost-visual build, and admission verdict are AUTOMATED (`GrappleCaptureInGameTest`, one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene with a live vessel; auto-recording is handled by the test's own setup). The remaining operator observation is the stock contact capture itself: grab a PotatoRoid asteroid (or a derelict) with the Advanced Grabbing Unit in real play and verify the recorded branch matches the automated fixture's shape. Label `mmis10-claw`. Grep: `OnPartCouple producer classified`, `Route proof dock window captured` with `kind=Grapple`, `PotatoRoid`, `[Parsek][*][GhostVisual]`, part-name resolve failures.
- (5) Elcano rover: a long surface traverse (hours of driving / large sample count). Verify the recording size / optimizer / map polyline budget hold at scale and surface render stays glued to terrain. Label `mmis10-rover`. Grep: `[Parsek][*][Optimizer]`, `polyline`, `budget`, `Points=`.

**Merge-blocker read for in-flight Missions / logistics PRs:** none. The integrated `origin/main` (#1235) headless suite is fully green and the deployed DLL is byte-verified; the findings here are pre-existing coverage GAPS, not regressions, so nothing in this sweep blocks the open PRs. FAIL-triggered focused todo entries are opened only when an operator run actually observes a break (template: exact shape/config, repro steps, observed vs expected, log signature, file:line if localized).

### Explicitly out of scope (faithful replay is the accepted, UI-surfaced behavior; revisit only on playtest demand)

- **Gravity-assist / multi-heliocentric-leg transfers** - `ReaimClassifier` rejects more than one heliocentric leg (ReaimClassifier.cs:141-146); re-aiming a chained assist is a different problem class (the assist geometry constrains every leg jointly).
- **Atmo-direct / aerocapture arrival alignment** - no captured destination orbit means no boundary to insert an arrival hold at; the body-fixed entry/descent already self-anchors to the live rotation and lands at the correct geographic site on loop, so only the approach-to-entry seam misaligns.
- **Porkchop-style dv-optimal window planning** - evaluated in the 2026-05-28 survey and deliberately not needed by the congruent-window model (recorded tof + synodic spacing; M-MIS-3 adds geometry-aware tof centering, still not a porkchop grid).
- **Grand tours / multi-destination single missions** (land-on-every-body challenge flights) - joint faithful recurrence across many transited bodies is effectively never; the accepted loop behavior is faithful replay plus whatever per-leg alignment M-MIS-6/7 provide. No whole-tour alignment is planned.
- **Crew rotation / tourism as DELIVERABLES** - the mission loop renders a crew ferry fine today; counting kerbals as route cargo (crew manifests, rotation credit) is logistics-roadmap territory (`docs/parsek-logistics-supply-routes-design.md` section 19, added by PR #1113), not a Missions milestone.
- **Off-world construction launches** (Extraplanetary Launchpads-class mods: vessels rolled out from a base instead of KSC) - modded compatibility tier; revisit only on demand.

---

## TODO - Overlapping / duplicate TrackSections written around on-rails seams (observed 2026-06-12)

**Observed (2026-06-12 audit, log `logs/2026-06-12_1915_m4c-save-recording-crew-audit`, recording `041770246260406ab85b59495eb51f45` in the "orbital supply route" save):** the committed transfer recording contains pairs of TrackSections covering the same UT span twice. Sections [18] (ref=Absolute) and [19] (ref=2/on-rails) both cover 6675562.034 -> 6676373.107 (the Mun SOI entry), and on-rails section [46] (6737626.584 -> 6738050.344) exactly spans the union of its neighbors [45] + [47]. `FindTrackSectionForUT` is ambiguous inside those windows (which section wins depends on scan order). No playback warning was observed in the session, so the impact is latent; the flat points stayed monotonic. Likely a flush-stitch bookkeeping artifact at the active<->on-rails handoff (the same session logged `AppendPointsFromTrackSections: skipped N non-monotonic frame(s) at flush stitch` #419 warnings on debris recs). Needs a producer-side repro before fixing; do NOT rewrite committed recordings.

## TODO - Looped re-aim interplanetary transfer: no continuous encounter into the destination SOI; line dead-ends in open space (investigated 2026-06-15, NOT fixed - regression-sensitive, deferred)

**Symptom (playtest 2026-06-15; looped 'Duna One' mission re-aimed while flying a fresh Duna mission; log `logs/2026-06-15_1906_duna-mission-investigation`, main @a4ff95b7c V0.10.0, save s15):** the re-aimed ghost's interplanetary transfer LINE is not rendered as a proper encounter. It heads toward Duna's orbit but dead-ends in open heliocentric space, never bending into Duna's SOI; viewed mid-cruise the arc's far end sits at where Duna WILL BE at arrival (empty now), and the recorded Duna-capture hyperbola is a detached segment across a gap. The instrumented form of the same defect is a ~62 deg ghost-transform teleport at BOTH SOI handoffs (`[ReaimSeam] SEAM member=30`: Kerbin->Sun jump=87.76 Mm = 1.043x Kerbin SOI, `KSP.log:21896`; Sun->Duna jump=49.19 Mm = 1.027x Duna SOI, `KSP.log:32175`). Re-aim ENGAGED cleanly (did not decline to faithful), the Lambert solved a sane prograde transfer, and the ghost ICON does reach Duna's SOI - so the orbit is correct; the defect is the stitch / encounter rendering, not a solver failure.

**Root cause (HIGH confidence; SYSTEMATIC + design-deferred, NOT inherent):** re-aim substitutes ONLY the heliocentric coast with a FRESH center-to-center Lambert (`Reaim/ReaimTransferSynthesizer.cs`: r1 = launch-body center, r2 = target-body center, recorded tof reused) and replays the recorded Kerbin-escape + Duna-capture hyperbolae VERBATIM at their original asymptotes. Two superimposed sources:
1. ENDPOINT (dominant, ~96% of the jump): Lambert endpoints are at planet CENTERS but the transfer renders FULL-SPAN (`Reaim/ReaimPlaybackResolver.cs:232-247` passes NaN render bounds). At the seam UT the synth arc is at the body center while the recorded leg ends at the SOI boundary, so each jump is ~1 SOI radius. (An earlier pass that DID trim the launch side to the SOI-exit UT was REVERTED - it opened a gap right after launch SOI exit where the orbit ghost was destroyed and the transfer line restarted displaced by the launch body's own motion. See the comment at `ReaimPlaybackResolver.cs:232-243`.)
2. ORIENTATION / SHAPE (~4% + a 2.5% sma / different ecc gap): the fresh Lambert has zero v-infinity awareness of the recorded asymptotes and reuses the recorded tof (geom tof differs by ~330,610 s here, `devFromGeom` in the `re-aimed transfer ready` line), so over a fractional 0.5835 synodic the asymptote directions and orbit shape differ from the verbatim recorded legs.

The original design (`docs/dev/done/plans/reaim-interplanetary-transfers.md:252-279, 359-361`) accepted the orientation residual as "the accepted small seam" and shipped only PadAlignLaunch; SOI-handoff continuity ("option 3: re-plan the whole patched-conic chain") was explicitly DEFERRED. The same-body 45 deg / 120 s seam-bridge (`GhostTrajectoryPolylineRenderer.cs` `IsBridgeAdjacentConic`) cannot cover a cross-SOI body-change seam by construction.

**Fix direction (the ONLY geometrically sound one; large effort):** the design's deferred "option 3" - synthesize the WHOLE patched-conic chain from one solve so the escape hyperbola's SOI-exit STATE matches the heliocentric departure v1 and the capture hyperbola's SOI-entry STATE matches the arrival v2, instead of splicing a fresh heliocentric arc onto verbatim recorded SOI legs. All three legs then meet at the same SOI-sphere position with continuous velocity, giving a real encounter into the SOI. Immutability-safe (in-memory loop-only on copied structs) and MIT-clean (reuses the already-solved v1/v2; no new solver). SEQUENCE AFTER the in-flight reaim branches land (`reaim-lambert-reliability`, `reaim-eccentric-tof`, `reaim-dest-loiter-retimer` / PR #1155, `fix-soi-trajectory-seam-coverage`) to avoid stacked re-aim rewrites.

**Rejected shortcuts (adversarially verified 2026-06-15, workflow wf_2ead60c5-a19; all NOT VIABLE except E as a stopgap):**
- (A) rigid-rotate the recorded transfer to the new epoch: geometrically false (escape is Kerbin-frame, capture is Duna-frame, only the middle leg is heliocentric; one Sun rotation cannot rotate all three) and misses the dominant endpoint gap.
- (B) anchored / shooting solve to SOI-boundary endpoints + recorded asymptotes: over-determined (a single-rev Lambert has only tof free once r1, r2 are fixed; one scalar cannot match two 3D asymptote directions) and reopens the reverted trim regression.
- (C) render-only rotate the recorded legs onto the solved asymptotes: corrects only the ~4% angular residual, leaves the ~1-SOI-radius endpoint gap, and needs the reverted trim.
- (D) cross-SOI render-only seam bridge: cannot move the ICON / proto-orbit (they ride the ghost transform, not the polyline), and a 62 deg cross-body connector is the wild-spiral / planet-intersection case the bridge was gated against.
- (E) accept the gap + clip the line / suppress the icon across the handoff: VIABLE only as a labeled STOPGAP - it does NOT make the line accurate (a tidy gap is still no encounter, the user's actual complaint) and re-litigates the reverted trim.

**Do NOT do (regression guards):** do not rewrite / finalize / load-time-modify recorded data (.prec / OrbitSegments) on any path - re-aim stays in-memory loop-only; do not vendor a GPL solver (Parsek is MIT); do not auto-extend the heliocentric draw / icon window over the SOI escape / capture window (tried + reverted, puts the ghost behind the planet); do not trim the full-span render without also fixing the capture leg (the reverted gap regression); revert-on-regression - prior re-aim rewrites went net-negative, the current single honest kink is a known baseline; pin the requirement against this one concrete case before writing code.

**Validation must be the ENCOUNTER, not the seam number:** re-run the looped Duna One re-aim playtest, collect a fresh log, and confirm the LINE visibly enters Duna's SOI in-game (a real encounter) and the ICON follows it through both handoffs - NOT merely that the `[ReaimSeam]` jump dropped below one SOI radius. Also confirm: re-aim still ENGAGED, the synth geometry is still a sane ellipse, and no new "gap-between-orbit-segments" / orbit-ghost-destroyed warnings appear at the launch SOI exit (the reverted-trim regression must not return).

**References:** full investigation + ranked options doc at the umbrella root `reaim-seam-investigation.md`; log snapshot `logs/2026-06-15_1906_duna-mission-investigation/`; engaged params `KSP.log:13201` (`ENGAGED re-aim Kerbin->Duna via Sun; D0=142619013 synodic=19653075 tof=6854613`).

**ATTEMPTED + REVERTED (2026-06-17) - option 3 (whole-chain synthesis). STILL OPEN; do not re-attempt without the preconditions below.** The deferred "option 3" was built behind a default-OFF flag (P0/P1 foundation #1169, P2-P4 chain synthesis #1170) and then REVERTED (#1171 reverts #1169; #1170 closed). Plan/design docs at the umbrella root: `reaim-fix-plan.md`. Why it was abandoned:
- **It regressed the flag-ON render and never solved the bug.** First playtest (Kerbal X #2): synth escape/capture legs were garbage hyperbolae (`[ReaimSeam] chain legs: escape ecc=12.9, capture ecc=7.77`; sane is ~1.05-1.3), so departure+arrival misaligned, Duna-SOI arrival render broke, icon teleported. Log `logs/2026-06-16_2351_reaim-chain-kerbalx2-regression/`.
- **Root cause (confirmed):** `ReaimTransferSynthesizer.BuildBodyRelativeLeg` paired the transfer POSITION at the SOI crossing with the Lambert ENDPOINT velocity v1/v2 (at the planet center), an inconsistent state vector. Fixed (use `transfer.getOrbitalVelocityAtUT(crossingUT)`) + added the plan's never-built periapsis/ecc fail-closed gate (`IsSaneLegConic`), so flag-ON fails closed to baseline instead of rendering garbage.
- **But it still could not be validated:** every available test mission is a fail-closed case. Duna One threads Ike (capture fails closed). Kerbal X #2 is a heliocentric-parking (two-burn) departure (escape fails closed) AND its re-aim render is independently broken by the #1166-engages-but-#1167-Increment-2-not-built span-greater-than-synodic gap, not by this work. Validating option 3 needs a CLEAN DIRECT Kerbin->Duna recording: no parking-orbit loiter, no moon (Ike) encounter. No such mission exists in the test save.
- **Structural doubt:** a center-to-center Lambert solve carries no information about the real ejection/capture periapsis, so the synth body-relative legs inherit whatever periapsis the SOI-crossing sample implies; the gate fails the bad ones closed, but a "consistent but wrong-altitude" leg can still pass. The premise may need rework (e.g. SOI-edge-to-SOI-edge solve, or seed the ejection from the recorded parking periapsis) rather than center-to-center.

**Do NOT re-attempt option 3 without (a) a clean direct no-park no-moon Kerbin->Duna looped recording to validate against, and (b) resolving the center-to-center-periapsis structural doubt.** Confirm a validatable test case exists BEFORE building (this arc was built end-to-end before that check, which was the core process mistake).

## BUG-C (2026-06-07 career playtest) - `R2-B2` tree instability + NaN debris -> stock exceptions

Source: `logs/2026-06-07_1638_career-playtest/` (KSP.log, `BUGS.md` BUG-C section). Build `Parsek V0.10.0` @ `07dea8fac`. The player used NO Parsek features this session (no rewind / re-fly / loop / playback); Parsek was only background-recording. Three log signatures, separable root causes. BUG-C is largely fallout of BUG-A (ledger recalc) and BUG-B (passive ghost / vessel auto-spawn), which are tracked separately.

### 1. NaN debris -> stock `FlightIntegrator.UpdateOcclusionSolar` throw - STOCK KSP, not Parsek data (no fix)

Two `ArgumentOutOfRangeException` throws in stock `FlightIntegrator.UpdateOcclusionSolar` (KSP.log lines 205423 @15:46:42, 218683 @15:50:46), each immediately around `R2-B2M-S6 Debris had a NaN Orbit and was removed` + an on-rails Kerbin->Sun SOI transition.

Origin is **pure stock physics**, confirmed:
- `R2-B2M-S6 Debris` (pids 1333358833 / 2800168062) is the player's **real staging debris**, created at 15:42:22 by a real decouple (`Decouple created vessel during recording ... rootPart=radialDecoupler`), with real drag cubes, terrain collision (`crashed through terrain on Kerbin`), and explosions. It is NOT a Parsek ghost/spawn: line 204856 `CleanupOrphanedSpawnedVessels: no match for 'R2-B2M-S6 Debris'` is Parsek explicitly disclaiming ownership.
- Parsek background-recorded the debris and then **finalized + deleted** those recordings as non-persistable (`canPersist=False`, `DeleteRecordingFiles`) at 15:42:39, ~4 minutes before the NaN. No Parsek recording carried or authored the NaN orbit.
- The throw is the well-known stock pattern: debris clips through terrain, is packed on rails with a degenerate velocity, the resulting hyperbolic orbit escapes Kerbin->Sun, and `UpdateOcclusionSolar` indexes a body list off a NaN-derived value and throws before stock's own NaN-orbit removal runs.

Decision: do **not** Harmony-patch stock `FlightIntegrator` to swallow this. It is not Parsek data, it reproduces without Parsek, and guarding a stock NaN path from a mod is high-risk for little gain (the proper home for a stock-bug shim is KSPCommunityFixes). Filed as known-stock, no Parsek code change.

### 2. Terminal-orbit ghost "permanently abandoned" 3x - BUG-B fallout + a real durability gap (FIXED here)

`[Policy] Spawn-death detected for terminal orbit and will not be retried: #32 "R2-B2-S5" ... reason=spawned-terminal-orbit-vessel-died` fires 3x (15:46:17, 15:56:43, 15:58:58), each with a fresh Parsek-spawn pid (3390689712 / 3495642311 / 3732877540) and `deathCount=1`.

Traced each cycle: `SpawnAtPosition: vessel spawned (ORBITING, body=Sun, alt~13.4 Gm)` -> Parsek's own `CleanupOrphanedSpawnedVessels: recovering 'R2-B2-S5' (matched by name)` immediately recovers it -> `RunSpawnDeathChecks` sees it gone -> `MarkCannotSpawnSafely`. The spawn itself is **BUG-B**: Parsek auto-materializes a committed terminal-orbit `vessel`-type recording during passive play, then orphan-recovers it.

The durability gap (the part fixed here): `RunSpawnDeathChecks` sets `Recording.TerminalSpawnCannotSpawnSafely = true` ("will not be retried"), and `VesselSpawner.SpawnOrRecoverIfTooClose` (`VesselSpawner.cs:1688`) honours that flag as a pre-spawn guard. But the flag was **transient** (`Recording.cs`, "do not serialize"), so every scene reload reset it to false and the vessel re-spawned. The first spawn each session happens before the flag is set, and `TryPassTerminalOrbitSpawnSafety`'s live orbit-geometry re-check passes (a 13.4 Gm heliocentric coast is geometrically "safe"), so only the recorded spawn-death can stop it - and that was being forgotten.

Fix: persist `TerminalSpawnCannotSpawnSafely` + `TerminalSpawnSafetyReasonCode` so the abandon survives a reload, on BOTH load paths. (1) Cold start (fresh game load, `RecordingStore.ClearCommittedInternal` then `LoadRecordingTrees`): the `RecordingTreeRecordCodec` save/load (`SaveMutablePlaybackState` / `LoadRecordingResourceAndState`) round-trips the keys when the committed trees are rebuilt from disk. (2) In-session load (scene change / quickload / revert, the `returned-scene-change` branch of `ParsekScenario.OnLoad`): that path reconciles the in-memory committed recordings instead of rebuilding them - it resets every recording's terminal spawn-safety via `TerminalOrbitSpawnSafety.Clear` (~line 2320) then restores only the saved subset, so the new `ParsekScenario.RestorePersistedTerminalAbandon` re-applies the flag from the saved RECORDING node (absent on a revert quicksave, so the abandon correctly does not carry across a revert). Either way the flag is true on the next scene, so the existing `VesselSpawner.cs:1688` pre-spawn guard blocks the re-spawn and the "will not be retried" log becomes truthful across reloads. The observed 3x repro went through path (2), so the codec change alone would not have fixed it. The soft altitude-deferred hold (`TerminalSpawnSafetyDeferred`) is deliberately left transient so it re-evaluates against the propagated orbit. Tests: `RecordingTreeTests.RecordingTree_TerminalSpawnCannotSpawnSafely_RoundTrips` / `RecordingTree_NoTerminalSpawnAbandon_StaysFalseOnLoad` (codec path) + `SpawnStateReconciliationTests.RestorePersistedTerminalAbandon_*` (in-session path). This is defense-in-depth; the upstream cure (don't auto-spawn during passive play at all) is BUG-B. Note: each orphan recovery runs a stock vessel-recovery (`Recovery processing captured ... recoveryFactor=...`), a candidate contributor to BUG-A's funds drift - flagged for the BUG-A session.

### 3. Active-tree save skipped - correct-by-design merge-consent guard, root is BUG-B-adjacent identity collision (no fix here)

`[Scenario] SaveActiveTreeIfAny: skipped active tree 'R2-B2-S5' because at least one recording could not be written with current v0 sidecars` + `skipped dirty sidecar save for committed-restore overlap recording 'bb53...'` (lines 165907-165925, 15:04).

This is the merge-consent guard in `SaveActiveTreeIfAny` (`ParsekScenario.cs:1380-1390`): a dirty recording that is an `IsCommittedTreeRestoreAttemptRecordingId` (and not a marker-owned switch segment) is skipped to avoid overwriting committed history before merge consent. **No committed sidecar is corrupted**, and in this log only one recording was dirty (the overlap clone `bb53...`, 1 buffered point), so there is no meaningful new-data loss - the guard behaved correctly.

The real defect is upstream and BUG-B-adjacent: the active tree was created by `TryRestoreCommittedTreeForSpawnedActiveVessel` treating the player's **fresh-rollout real vessel** `R2-B2-S5` (pid 590316933) as a committed-spawned-clone. This is the documented craft-baked-`persistentId` collision (a new launch of the same craft reuses the baked pid that prior committed recordings of that craft also carry). The fresh-rollout fast-path ("matches captured scene-entry pid") correctly skipped restore at 14:55, but after the 15:04 scene reload the captured scene-entry pid no longer matched and it fell through to committed-tree restore - routing a normal flight into the re-fly merge-consent path. The correct cure is launch-identity (`RecordedVesselGuid` / `VesselLaunchIdentity`) discrimination at the restore site, which belongs with BUG-B / the identity subsystem, not the save guard. No safe save-path change here.

Latent secondary (noted, not fixed): `SaveActiveTreeIfAny` early-returns and skips the WHOLE active-tree node when any one recording is a committed-restore overlap, even if a legitimately-new marker-owned switch-segment recording in the same tree had its sidecar written - that would orphan the sidecar (no tree node references it). Not triggered destructively in this log; flagged for the switch-segment owner.

---

## Post-cutover map/TS render backlog (next version)

The map/TS render cutover is COMPLETE (see the DONE entry above): the modular Director pipeline is the single render path; this file has no open map/TS render bugs (the render entries above are all RESOLVED/CLOSED). This is the consolidated list of what remains in this area for a future version. NOTHING here blocks the current release.

**1. Re-aim destination phase-lock for looped INTERPLANETARY missions (the one substantial piece).** Re-aim aligns the launch body but not the destination body's rotation/phase across the loop shift, so a looped interplanetary arrival drifts. "Duna One" is closed; the generalization (non-synchronous moons, destination loiter, 2+ moons, atmo-direct entry) is the deferred Phase 4 - see the "DUNA ONE CLOSED ... Phase-4 GENERALIZATION still deferred" entry immediately below. WARNING: this sits on the re-aim seam, a known high-cost area - build the failing multi-moon test and measure before any knob math, and treat "the faithful render is good enough" as a valid outcome (do not stack speculative fixes on a working baseline).

**2. Robustness / needs-in-game confirms (deferred during the cutover, non-blocking).**
- Tracer second cut: the decision-side inc/LAN/argPe-vs-transform reconciliation layer (the reconciler core exists; see the "In progress ... tracer SECOND CUT" entry at the top of this file).
- Sec 15.1 proto re-seed latency and Sec 15.2 per-scene patched-conic divergence (deferred, need in-game characterization; details in `docs/dev/plans/maprender-rewrite-status.md`).
- Phase 7b make-before-break swap-settle: the proto-vessel swap timing on scene/treatment swaps (a brief reseed window, ~0.5s).
- Tracking-Station render-delay confirm: the 1-2s TS proto-vessel gap fix (the same-body intra-block carry) is believed working; wants a TS playtest to confirm.

**3. Minor polish (low value, all currently suppressed or sub-visual).**
- icon-off-orbit residual ~1-3 deg on looped re-aim (the core ~96.5 deg bug is fixed; this is the leftover).
- no-fresh-seed create-frame transient: 1 frame, the proto is suppressed that frame so it is invisible; the fix touches the re-aim seam, so it was skipped during the closeout.
- polyline-orbit-overlap grace transient: the OrbitLineGrace debounce, cosmetic.
- cosmetic test-name cleanup: a couple of in-game test methods still read "...LiveGate..." after the `mapRenderDirectorDrive` gate was dropped.

**4. Architectural / cleanliness (nice-to-have, not needed for function).**
- Modularize the no-conic fallback as a proper Director "fallback treatment" instead of the kept patch-level path in `GhostOrbitLinePatch` (the icon floor + `ghostsWithSuppressedIcon` + `IsIconSuppressed`). The current kept fallback is correct and working; this is purity, not function.
- Standalone ghost-mod readiness for the render side (the `IPlaybackTrajectory` boundary).

---

## 640. Stock committed-future overlay v2 follow-ups

**Status:** TODO - future investigation / review item from PR #721.

PR #721 ships the v1 scope: stock R&D, Astronaut Complex, and Mission
Control committed-future overlays, plus click-blocks for duplicated tech,
contract accept, kerbal hire, and facility upgrade actions. The following
ideas are deliberately out of v1 scope and should be reviewed as separate
follow-ups after in-game verification:

- KSC facility-upgrade visual overlays in the top-down KSC view. The
  click-block already exists via `FacilityUpgradePatch`; v2 would add the
  visual badge and extend the overlay/click-block invariant to facilities.
- Future-completed / future-failed contract badges in Mission Control, not
  only future-accepted contract badges.
- Administration strategy activation overlays, paired with matching
  click-block behavior if the stock UI has a clickable affordance.
- Per-row claim / override UI for cases where the player intentionally wants
  to bypass a committed-future action, instead of using the global setting.
- Per-user dismissible badges for "hide this warning until next session" style
  workflows.
- Non-stock screen integrations, such as Contract Configurator's own Mission
  Control replacement or other mod-provided building screens.
- Modded flight-scene building overlays. The current v1 overlays are
  `SPACECENTER` scene-bound, while the lower-level click-blocks remain
  scene-agnostic.
- Tooltip styling polish using KSP's richer
  `KSP.UI.TooltipTypes.TooltipController_Text` path instead of the v1
  `GUI.skin.box` fallback.

**Review guidance:** keep the v1 invariant intact for every clickable action:
if a stock or modded UI exposes a clickable affordance, the overlay candidate
set and the click-block predicate must share the same `MilestoneStore` source
helper, with any UI-only suppression kept outside the click-block predicate.

---

## Phase 6 known gaps (deferred to later phases)

- ~~§7.7 BubbleEntry / BubbleExit candidates are not emitted by the Phase 6 builder.~~ Shipped: `AnchorCandidateBuilder.EmitBubbleEntryExitCandidates` walks adjacent `TrackSection` pairs and emits at every `Active|Background ↔ Checkpoint` source-class transition; `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` reads the LAST/FIRST physics-active sample as the high-fidelity world reference. Mainline shipped this at `AlgorithmStampVersion=5`; on the Phase 5 stack it lands inside the v8 alg-stamp window. Residual gap: RELATIVE-frame physics-active sections adjacent to a Checkpoint segment are deferred with a `bubble-entry-exit-relative-section-deferred` Verbose (uncommon in practice — vessel docked to its anchor while a Checkpoint splices in).
- ~~§7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates.~~ Obsolete: the co-bubble subsystem was retired in PR #912 (v0.9.3). The enum slot 7 (formerly `CoBubblePeer`) is now `Reserved7`, kept only to preserve the persisted `.pann` `AnchorCandidatesList` byte layout; there is no co-bubble pipeline. Close-formation accuracy is delivered by the parent-anchored debris contract instead.
- The 2.5 km bubble-radius HR-9 Warn (`RenderSessionState.cs:836-848`) only fires from the LiveSeparation path inside `RebuildFromMarker`. Anchors written via `AnchorPropagator.TryWriteAnchor → PutAnchorWithPriority` (§7.4 / §7.5 / §7.6 / §7.7 / §7.10) skip the magnitude check, so a non-LiveSeparation ε of, say, 12 km lands silently. Lift the magnitude check into `PutAnchorWithPriority` (or the per-source dispatch) in a follow-up PR so all anchor types are uniformly guarded — pre-existing gap, not introduced by §7.7.
- §7.9 SurfaceContinuous emits a marker only with ε = 0; the per-frame terrain raycast that resolves ε is Phase 7 work. Phase 6 demoted the rank from 2 to 6 to prevent the zero stub from winning ties against real OrbitalCheckpoint ε; Phase 7 must promote back to rank 2 once the resolver ships and bump `AlgorithmStampVersion` so existing `.pann` re-resolve.
- The split anchor sources (Undock / EVA / JointBreak) currently share the `DockOrMerge` enum byte (priority rank 4 either way). Logs label them by `BranchPointType` rather than by enum value to preserve telemetry granularity. If a future phase needs to differentiate split priorities from dock priorities, expand the `AnchorSource` enum and bump `AlgorithmStampVersion`.

---

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**2026-04-19 boundary note:** `GhostPlaybackEngine.ResolveGhostActivationStartUT` no longer casts back to `Recording`; the engine now resolves activation start from playable payload bounds through `PlaybackTrajectoryBoundsResolver` over `IPlaybackTrajectory`. #435 remains otherwise unchanged, but this leak is no longer part of the extraction risk surface.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Partial mitigation:** PR #721 adds stock R&D / Astronaut Complex / Mission Control row badges with tooltips for committed-future actions, including the event UT and source recording when available. This helps before the click, but does not replace the structured blocked-action dialog below: the dialog still needs conflict context, Timeline navigation, and the rewind shortcut.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

2026-04-25 update: deferred spawn queue outside-physics-bubble waits are no longer
a spam source; the per-recording kept line and repeated warp-ended summary were
replaced with a rate-limited queue wait summary.

2026-04-25 update (UnfinishedFlights + missed-vessel-switch):
`logs/2026-04-25_1314_marker-validator-fix/KSP.log` was 96 MB / 540k lines, of
which ~511k (94%) were `[Parsek][VERBOSE][UnfinishedFlights]
IsUnfinishedFlight=…` decisions and ~1k were `[Parsek][WARN][Flight] Update:
recovering missed vessel switch` lines. Both fired from per-frame paths:
`EffectiveState.IsUnfinishedFlight` is invoked once per recording per frame from
`RecordingsTableUI` row drawing, `UnfinishedFlightsGroup` membership filtering,
and `TimelineBuilder`; the missed-vessel-switch warn fires in `ParsekFlight`
`Update()` until the recovery handler clears the predicate, which in this
playtest took dozens to hundreds of frames per vessel. Each of the 7 return
paths in `IsUnfinishedFlight` now uses `ParsekLog.VerboseRateLimited` keyed by
`{reason}-{recordingId}` so each (recording, reason) pair logs once per
rate-limit window. The missed-vessel-switch warn now uses
`ParsekLog.WarnRateLimited` keyed by `missed-vessel-switch-{activeVesselPid}`
so each vessel logs at most once per window. Regression
`EffectiveStateTests.IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine`
calls the predicate 100x with the same recording and asserts a single emitted
line.

2026-04-25 update (post-#591 second-tier cleanup): the `2026-04-25_1933_refly-bugs`
KSP.log surfaced six more spam sources, addressed as numbered bugs #592-#596
(closed in this commit) plus #597 (open underlying-logic concern). #592 covers
the ~3300 `Time warp rate changed` / `CheckpointAllVessels` / `Active vessel
orbit segments handled` lines from KSP's chatty `onTimeWarpRateChanged`
GameEvent. #593 covers ~1190 lines from repeatable record milestones
(`Records*` IDs) re-emitting the same `Milestone funds` / `stays effective` /
`Milestone rep at UT` line on every recalc walk. #594 covers 221 KspStatePatcher
bare-Id fallback lines. #595 widens the OrbitalCheckpoint playback and Recorder
sample-skipped rate-limit windows from 1-2s to the default 5s. #596 gates the
PatchFacilities INFO summary on having actual work. #597 later closed the
underlying duplicate checkpoint work with a same-tree/same-rate/same-UT guard
plus recorder-level duplicate-boundary idempotence.

2026-04-26 update (observability Phase 1 current spam hygiene): the newest
retained package `2026-04-26_0118_refly-postfix-still-broken` surfaced a
different top-repeat set: finalizer-cache periodic summaries, repeated
patched-snapshot missing-body/captured pairs, repeated extrapolator seeded
orbital-frame-rotation lines, and small GhostMap cleanup/window repeaters. This
branch keys finalizer summaries by owner/recording/terminal state, removes the
no-delta Info backstop, keeps only the first unique classification at Info,
gates patched-snapshot and OFR-seeding details with `VerboseOnChange`, and
rate-limits empty GhostMap cleanup plus diagnostics missing-sidecar warnings.
The follow-up also gates repeated all-zero ledger summaries and sandbox/no-target
KSP patch skips with `VerboseOnChange`. Focused xUnit log assertions pin each
gate. Remaining broader audit work stays tracked by the Observability Audit
section above.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

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
