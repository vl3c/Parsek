# Recordings and Snapshots Around Dock / Undock Points

This document explains exactly how Parsek structures recordings, branch points, and snapshots as the player docks two vessels together and later undocks them. It is the reference for anyone working on logistics, re-fly, ghost playback, or the tree/segmentation pipeline — every change to those subsystems must keep the contracts here intact, because route-proof, ghost continuity, and ledger correctness all depend on snapshots landing at the right phases.

The walkthrough uses the 2026-05-18 dock-2 playtest as a concrete reference (`logs/2026-05-18_0015_logistics-v0-dock-test-2/`). Numbers refer to that run.

---

## 1. The actors

At a dock / undock event there are three distinct entities Parsek tracks:

1. **The two physical KSP vessels.** Each has a `persistentId` (PID). Pre-dock there are two; post-dock there is one merged vessel; post-undock there are two again (often with a different PID for the half that emerged from the docking port).
2. **Recordings.** A `Recording` is a flat time-series of trajectory points + structural events + snapshots for a single vessel identity. Each carries `VesselPersistentId`, `ExplicitStartUT`, `ExplicitEndUT`, and (since v0 logistics) optional `RouteConnectionWindows`.
3. **Branch points.** A `BranchPoint` (declared in `Source/Parsek/BranchPoint.cs`) is the tree-topology node that connects parent recording(s) to child recording(s) at a structural event. The `Type` enum distinguishes `Dock`, `Undock`, `EVA`, `Breakup`, `Board`, `Terminal`, `VesselSwitchContinuation`. Dock and Board are "merge" branch points (N parents → 1 child); Undock and EVA are "split" branch points (1 parent → N children).

These three layers are independent but coupled through ids: branch points reference recording ids in `ParentRecordingIds` / `ChildRecordingIds`, and each recording stores its own `ParentBranchPointId` / `ChildBranchPointId` so the tree can be traversed in either direction.

---

## 2. KSP's vessel-PID semantics through a dock cycle

This is the part you have to keep in mind everywhere, because Parsek's logic depends on it.

### 2.1 Dock event

KSP fires `GameEvents.onPartCouple(data)` where `data.from.part` and `data.to.part` are the two coupling parts.

- `data.to.vessel` is **always the survivor** — its `persistentId` becomes the merged vessel's PID. In modern KSP this is **not** one of the original two PIDs; KSP assigns a fresh PID at couple time. (In the playtest, the pre-dock vessels were `2167625385` (transport rover) and the destination rover; the merged vessel's PID is `3965530352`.)
- `data.from.vessel` is the absorbed side. Briefly after `onPartCouple` fires, KSP destroys this vessel object; its parts have been reparented onto `data.to.vessel`.
- The transient pre-reparent window is small but real. When Parsek reads `data.from.vessel.persistentId` and `data.to.vessel.persistentId` at the top of `OnPartCouple`, both values are still meaningful — but a `FlightRecorder.FindVesselByPid(data.to.vessel.persistentId)` call later in the same frame returns a vessel object whose `parts` list already includes both sides' parts.

This timing window is why we capture the pre-couple partner snapshot at the top of `OnPartCouple` (see §5.1).

### 2.2 Undock event

KSP fires `GameEvents.onPartUndock(undockedPart)` where `undockedPart` is the docking port that decoupled.

- At the moment `onPartUndock` fires, the parts of the un-decoupled half are typically still owned by the original merged vessel (`undockedPart.vessel.persistentId == mergedPid`). KSP creates the new vessel object for the separated half asynchronously, usually within the same frame but after `onPartUndock` returns.
- KSP **may** fire `onPartUndock` a second time with the new PID once the split is complete. **It may also not.** In the 2026-05-18 playtest, KSP fired `onPartUndock` exactly once with the transient PID; no follow-up event arrived even after the joint-break events for the docking ports fired.
- This is why Parsek's transient-state early-return at `ParsekFlight.cs:10522` needs to schedule a deferred coroutine for the route-window completion path — that path can't wait for a follow-up event that may never come.

After the split completes (next frame), `FlightGlobals.Vessels` contains both halves as separate `Vessel` objects with separate PIDs. Each half's `vessel.parts` list contains only its own parts.

---

## 3. Tree topology at a dock

At a dock event, Parsek creates a `BranchPoint` with `Type = Dock` and `MergeCause = "DOCK"`. The branch point joins:

- **Parents:** the recordings that existed before the dock (typically two — one for each docking vessel).
- **Child:** a single new merged-vessel recording that starts at the dock UT.

```
Pre-dock state                       Post-dock state
==============                       ===============

[Recording A]    [Recording B]       [Recording A]    [Recording B]
  pid=2167...     pid=3965...          terminal=Docked  terminal=Docked
  active rec      active rec           ChildBranchPointId=BP
                                            \              /
                                             \            /
                                              [BranchPoint BP]
                                                 type=Dock
                                                 mergeCause=DOCK
                                                 targetVesselPid=3965...
                                                       |
                                                       v
                                                 [Recording C]
                                                   pid=3965... (merged)
                                                   ExplicitStartUT = dockUT
                                                   ParentBranchPointId=BP
                                                   RouteConnectionWindows=[...]
```

### 3.1 What parents look like after the dock

Both parent recordings get:

- `ExplicitEndUT = dockUT` — closed at the exact dock moment.
- `TerminalStateValue = TerminalState.Docked` — marks the recording's terminal classification.
- `ChildBranchPointId = BP.Id` — back-link to the branch point.
- `MarkFilesDirty()` — sidecars need to be re-saved.

If only one of the two pre-dock vessels was Parsek-tracked (the cross-tree case yesterday's fix handled), the absorbed/foreign side has no recording to close. In that case the branch point has only one `ParentRecordingIds` entry — the active side — and the merged child still proceeds normally. See `Source/Parsek/ParsekFlight.cs:5207-5208`.

### 3.2 What the merged child looks like

A new `Recording` is created via `BuildMergeBranchData` (in `ParsekFlight.cs`). It carries:

- `RecordingId` — fresh GUID.
- `VesselPersistentId = mergedPid` (the survivor PID = `data.to.vessel.persistentId`).
- `VesselName = ResolveLocalizedName(mergedVessel.vesselName)`.
- `ExplicitStartUT = dockUT`.
- `ParentBranchPointId = BP.Id`.
- `VesselSnapshot` / `GhostVisualSnapshot` — full ConfigNode snapshots of the merged vessel (see §5.2).
- `TransferTargetVesselPid` and `TransferKind` — populated when the merge is route-eligible (a Dock with a non-zero `routeTargetVesselPid`); used by the logistics analyzer.
- `RouteConnectionWindows` — list of `RouteConnectionWindow` entries. At dock creation, exactly one open window is appended (see §6).

A fresh `FlightRecorder` is started on the merged child via `StartRecording(isPromotion: true)`. From the player's perspective, recording continues uninterrupted across the dock — they don't see a pause — but in the data model the trajectory is split at the dock UT into two recordings.

---

## 4. Tree topology at an undock

An undock is a **split** branch point. One parent recording, two children (one per separated half).

```
[Recording C, merged]                         [Recording C]
  active rec, pid=3965...                       terminal=Docked? (no, see below)
                                                ChildBranchPointId=BP'
                                                     |
                                                     v
                                               [BranchPoint BP']
                                                  type=Undock
                                                  splitCause=UNDOCK
                                                  /              \
                                                 v                v
                                          [Recording D]     [Recording E]
                                            pid=3965...      pid=<new>...
                                            (survivor)       (separated half)
                                            ParentBranchPointId=BP'
                                                                ParentBranchPointId=BP'
```

In the 2026-05-18 playtest, the undock didn't actually split the recording (we'll get to that in §7), but in the normal case:

- The merged parent `Recording C` gets `ExplicitEndUT = undockUT` and `ChildBranchPointId = BP'.Id`. Its `TerminalStateValue` is left untouched (Docked / Recovered / etc. are merge-specific; undock just ends the merge).
- Two new children are created. The recorder follows the active vessel into `Recording D`; the other half goes into the background recorder as `Recording E`.
- The `BranchPoint`'s `SplitCause = "UNDOCK"`, `DecouplerPartId = undockedPart.persistentId`.
- The route window's `UndockUT` is written on `Recording C` (the parent) via `TryCompleteLatestRouteConnectionWindow`. See §6.4.

### 4.1 EVA, joint-break, breakup

These are also split branch points (`Type = EVA`, `Type = Breakup`). The structural mechanics are the same — one parent, multiple children — but their `SplitCause` / `BreakupCause` fields differ, and they don't carry route windows.

---

## 5. Snapshots: what gets captured when

There are five distinct kinds of "snapshot" in the dock/undock pipeline. Don't conflate them.

### 5.1 Pre-couple partner snapshot — `pendingDockPartnerSnapshot`

**Captured in:** `OnPartCouple`, BEFORE `recorder.AppendStructuralEventSnapshot` and `StopRecordingForChainBoundary`.

**Of what:** the partner vessel only (the side of the couple pair that ISN'T the recorder's vessel), while `data.from.vessel` and `data.to.vessel` still reference distinct vessel objects (i.e. KSP hasn't reparented yet).

**Why:** the route window's `DOCK_ENDPOINT_RESOURCES` and `DOCK_ENDPOINT_INVENTORY` must reflect the partner's pre-dock state. Once KSP reparents the parts onto `data.to.vessel`, any `FindVesselByPid(mergedPid)` snapshot includes both sides' parts and resource extraction over-counts. This was sub-bug A in the 2026-05-18 playtest: `DOCK_ENDPOINT_RESOURCES = LiquidFuel 400/800` instead of the endpoint's actual `200/400`.

**Stored as:** `ConfigNode pendingDockPartnerSnapshot` + `uint pendingDockPartnerSnapshotPid` on `ParsekFlight`. Consumed in `CreateMergeBranch` and cleared in `ClearDockUndockState` and the deferred-merge cleanup paths.

**When this can be null:** the retroactive `onPartCouple (tree, retroactive)` branch (where `OnPhysicsFrame` stopped the recorder before `OnPartCouple` fired) doesn't currently capture this snapshot. In that case `CreateMergeBranch` falls back to `TryDeriveEndpointPartPidsFromPartner` (live vessel → active-tree recording → committed recording), accepting the over-count risk. The retroactive path is rare and not blocking.

### 5.2 Merged-vessel snapshot — `mergedSnapshot`

**Captured in:** `CreateMergeBranch`, via `VesselSpawner.TryBackupSnapshot(mergedVessel)` after `FlightRecorder.FindVesselByPid(mergedPid)`.

**Of what:** the post-couple merged vessel (`data.to.vessel`), which by this point holds all parts from both pre-couple sides.

**Why:** the merged child recording needs a full vessel snapshot for ghost playback (`mergedChild.VesselSnapshot = mergedSnapshot.CreateCopy()` for serialization, `mergedChild.GhostVisualSnapshot = mergedSnapshot` for visual rendering). The route window's `DockTransportResources` are also extracted from this snapshot — the merged snapshot is correct for transport's contribution because `TransportPartPersistentIds` only contains transport's parts.

**Stored as:** transient local in `CreateMergeBranch`; persisted via `mergedChild.VesselSnapshot` and `mergedChild.GhostVisualSnapshot`.

### 5.3 Structural event snapshot (couple side)

**Captured in:** `OnPartCouple`, via `recorder.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock")` and the mirrored `backgroundRecorder?.AppendStructuralEventSnapshot(...)`.

**Of what:** a high-fidelity `TrajectoryPoint` at the exact dock UT for each vessel still in the recording. The point is flagged as a structural-event boundary (see `Pipeline-Smoothing structural event snapshot UT=...`). It lands in the parent recording's active section.

**Why:** ghost playback needs a precise sample at the boundary so the trajectory doesn't interpolate across the dock seam, and the optimizer uses the flagged point to gate splittable boundary detection.

**Important:** this is a `TrajectoryPoint`, not a full vessel snapshot. Resources/inventory aren't captured here — that's the route window's job.

### 5.4 Structural event snapshot (undock side)

**Captured in:** `OnPartUndock`, BEFORE `recorder.StopRecordingForChainBoundary`, via the same `AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock")` mechanism.

**Of what:** a `TrajectoryPoint` at the undock UT for the recorded vessel (filtered by `RecordingVesselId`).

**Why:** mirrors the couple-side rationale. Required for `Trajectory-Anchor` candidate detection and visual continuity.

**Important:** this snapshot fires even in the transient-state early-return path? **No.** The current code structure runs `AppendStructuralEventSnapshot` AFTER the `newPid == recorder.RecordingVesselId` early-return at line 10522. This means in the transient-only case (the 2026-05-18 playtest), the undock UT structural event snapshot does NOT get written. The recording continues unbroken through the undock without a flagged boundary. This is a known gap; the new deferred route-window-completion coroutine compensates only for the route window, not for ghost-playback boundary detection.

### 5.5 Route window snapshots (dock-side and undock-side)

These live on the `RouteConnectionWindow` record attached to the merged child recording. See §6 below — they're complex enough to deserve their own section.

---

## 6. RouteConnectionWindow lifecycle

The route window is the data structure logistics uses to compute "what cargo moved during this dock window?" It lives on the merged child recording (`Recording.RouteConnectionWindows`) and goes through three phases.

### 6.1 Phase 1: window opened at dock

`CreateMergeBranch` calls `RouteProofCapture.BuildDockRouteConnectionWindow(...)` when `branchType == BranchPointType.Dock && routeTargetVesselPid != 0`. The returned window is appended to `mergedChild.RouteConnectionWindows`.

Fields populated at dock:

| Field | Source | Notes |
|---|---|---|
| `WindowId` | `"dock-{dockUT:R}-target-{targetPid}"` | Stable id for cross-referencing |
| `DockUT` | merge UT | The structural-event UT |
| `UndockUT` | `double.NaN` | Sentinel — overwritten at undock |
| `TransferTargetVesselPid` | partner PID from event | The endpoint vessel |
| `TransferKind` | `DockingPort` | v0 only kind |
| `TransportPartPersistentIds` | from `transportSnapshot` parts | Recorder vessel's pre-dock parts |
| `EndpointPartPersistentIds` | from pre-couple partner snapshot (preferred) / `bgParentRec.VesselSnapshot` / committed recording | Partner's pre-dock parts only |
| `DockTransportResources` | `ExtractResourceManifest(mergedSnapshot, transportPids)` | Filtered to transport parts |
| `DockEndpointResources` | `ExtractResourceManifest(endpointPreCoupleSnapshot ?? mergedSnapshot, endpointPids)` | Partner's pre-dock state when override available |
| `DockTransportInventory` | same source as transport resources | |
| `DockEndpointInventory` | same source as endpoint resources | |
| `EndpointAtDock` | partner vessel's body/lat/lon/alt at couple UT | For dispatch-time endpoint resolution |
| `TransferEndpointSituation` | partner's `Vessel.situation` enum | LANDED / ORBITING / etc. |

`IsComplete` returns `false` while `UndockUT` is still NaN. `RouteAnalysisEngine` ignores incomplete windows.

### 6.2 Phase 2: docked window in progress

While the merged child is recording, the player transfers fuel / cargo through stock KSP UI. No route-window mutation happens during this phase — the resources/inventory are just on the merged vessel's parts, and they move freely. The route window's dock-side baselines stay frozen.

### 6.3 Phase 3: window closed at undock — normal path

When `onPartUndock` fires with the proper (non-transient) PID, `OnPartUndock` calls `StopRecordingForChainBoundary` and queues `DeferredUndockBranch`. One frame later, the coroutine:

1. Finds the new (separated) vessel via `FlightRecorder.FindVesselByPid(newVesselPid)`.
2. Calls `CreateSplitBranch(BranchPointType.Undock, activeVessel, newVessel, branchUT)`.
3. `CreateSplitBranch` snapshots both halves (`activeSnapshot` for the active vessel, `bgSnapshot` for the background half).
4. Calls `RouteProofCapture.TryCompleteLatestRouteConnectionWindow(parentRec, branchUT, activeSnapshot, bgSnapshot)`.
5. `TryCompleteLatestRouteConnectionWindow` finds the latest incomplete window on the parent and calls `CompleteRouteConnectionWindowAtUndock(window, undockUT, activeSnapshot, bgSnapshot)`.
6. `CompleteRouteConnectionWindowAtUndock`:
   - Verifies `TryVerifyRoutePartSetsSeparated` — each snapshot must contain transport-side parts OR endpoint-side parts, never both, with exactly one snapshot per side. If a single snapshot still has parts from both sides, completion fails (KSP hasn't fully split — should never happen with the +1-frame defer).
   - Writes `window.UndockUT = undockUT`.
   - Computes `UndockTransportResources` and `UndockEndpointResources` from the side-scoped snapshots.
   - Computes `UndockTransportInventory` and `UndockEndpointInventory` analogously.

After this, `IsComplete` returns `true` and `RouteAnalysisEngine.AnalyzeTree` will consider the window viable. Net cargo movement = `UndockTransportResources - DockTransportResources` (transport deltas) and `UndockEndpointResources - DockEndpointResources` (endpoint deltas), which by conservation must sum to zero per resource.

### 6.4 Phase 3: window closed at undock — transient-state path (post-2026-05-18 fix)

When `onPartUndock` fires only once with `newPid == recorder.RecordingVesselId` (transient state, no follow-up event), the chain-split path never runs. Instead:

1. `OnPartUndock` calls `TryScheduleDeferredRouteWindowCompletionOnUndock`.
2. That helper inspects `activeTree.Recordings[activeTree.ActiveRecordingId]` (the merged child, which carries the open window).
3. If any `RouteConnectionWindow.IsComplete == false`, it schedules `DeferredCompleteRouteWindowOnUndock(recordingId, undockUT)`.
4. The coroutine waits one frame for KSP to finish the split, then walks `FlightGlobals.Vessels` to find the two halves by part-PID membership (each half's `vessel.parts` contains either transport-only PIDs or endpoint-only PIDs — never both).
5. Snapshots each half via `VesselSpawner.TryBackupSnapshot`.
6. Calls `TryCompleteLatestRouteConnectionWindow(rec, undockUT, transportSnapshot, endpointSnapshot)`.

Idempotency: the coroutine re-checks `IsComplete` before doing any work. If a follow-up `onPartUndock` did fire AND the chain-split path completed the window already, the deferred coroutine no-ops. Likewise if a future `OnPartUndock` triggers chain-split AFTER the deferred coroutine has already completed the window, `TryCompleteLatestRouteConnectionWindow` finds no incomplete window and returns false harmlessly.

This path completes the route window but does NOT create an Undock branch point or split the merged child. The merged child recording continues unbroken through the undock UT in this case — its `ExplicitEndUT` advances normally as the recorder samples post-undock points, until the recording is closed by some later event (tree commit, scene exit, etc.).

---

## 7. Concrete walkthrough: the 2026-05-18 dock-2 playtest

Two rovers, both named "rover fuel transfer":
- Vessel A (destination): pid `3965530352`, parked in place with `LiquidFuel = 200 / 400` in one mk2 tank.
- Vessel B (transport): pid `2167625385`, freshly launched, `LiquidFuel = 200 / 400` in its own mk2 tank.

Active recorder is on vessel B from UT 98.5 onwards (Recording `430d3a0d`).

### 7.1 At dock (UT 118.06)

KSP fires `onPartCouple` with `data.from.vessel.pid=2167625385`, `data.to.vessel.pid=3965530352` (or possibly a freshly-rebound merged PID — log shows `mergedPid=3965530352`).

**Parsek-side timeline:**

1. `OnPartCouple` runs. Resolves `partnerPidFromEvent=3965530352`, validates `partnerKnown=True` via the cross-tree fix.
2. Pre-couple partner snapshot captured: `pendingDockPartnerSnapshot` = snapshot of vessel A (pid `3965530352`) with its 28 parts and `LiquidFuel = 200/400`.
3. Structural event snapshot appended to recording `430d3a0d` at UT 118.06 (Dock).
4. `recorder.StopRecordingForChainBoundary()`. `430d3a0d` gets `ExplicitEndUT = 118.06`, `TerminalStateValue = Docked`.
5. `pendingTreeDockMerge = true`, `pendingDockMergedPid = 3965530352`, `pendingDockRouteTargetPid = 3965530352`.

Next frame, `HandleTreeDockMerge` runs:

6. `CreateMergeBranch(BranchPointType.Dock, mergedPid=3965530352, activeParentId=430d3a0d, bgParentId=null, mergeUT=118.06, ...)`.
7. Merged-vessel snapshot captured: 28 parts, `LiquidFuel = 400/800` (both tanks now on the merged vessel).
8. `mergedChild = Recording 5b385a6f`, `VesselPersistentId=3965530352`, `ParentBranchPointId=BP(40c3ad0d)`, `VesselSnapshot = mergedSnapshot.CreateCopy()`.
9. Route window built: `endpointPreCoupleSnapshot = pendingDockPartnerSnapshot`, so `DockEndpointResources` extracts from the partner's pre-dock state → `LiquidFuel = 200/400` (correct!). Window appended to `5b385a6f.RouteConnectionWindows`.
10. Recorder restarts on `5b385a6f` with `isPromotion=true`. `ChildBranchPointId` set on `430d3a0d`.

Tree state after dock:
- `430d3a0d` (parent, terminal Docked, ExplicitEndUT 118.06)
- `BP(40c3ad0d)`, Type Dock, mergeCause DOCK, targetVesselPid 3965530352
- `5b385a6f` (child, ExplicitStartUT 118.06, RouteConnectionWindow open)

### 7.2 During docked window (UT 118.06 → 144.76)

Player drives the merged rover and triggers stock fuel transfer: pumps 200 LF from vessel A's tank to vessel B's tank. Net merged-vessel total stays at 400 LF; individual tank balance shifts.

Recorder `5b385a6f` samples ~60 trajectory points across 26.7 seconds. The route window is unchanged (its dock-side baseline is frozen).

### 7.3 At undock (UT 144.76)

KSP fires `onPartJointBreak` for `dockingPort2` (the endpoint-side docking port, structural=T), then `onPartUndock(dockingPort2)`. The `undockedPart.vessel.persistentId = 3965530352` (KSP hasn't reparented yet).

**Parsek-side timeline:**

1. `OnPartUndock` entry. `newPid = 3965530352 == recorder.RecordingVesselId` → transient state.
2. **NEW (post-2026-05-18 fix):** `TryScheduleDeferredRouteWindowCompletionOnUndock` schedules `DeferredCompleteRouteWindowOnUndock("5b385a6f", undockUT=144.76)`.
3. `OnPartUndock` returns. **Critically, the structural-event snapshot for the undock is NOT written here** (see §5.4 gap).

Next frame:

4. `DeferredCompleteRouteWindowOnUndock` runs. KSP has now split the vessels: vessel A keeps pid `3965530352` (the survivor with the empty tank, `LiquidFuel = 0/400`); vessel B gets new pid `3077499886` (the full tank, `LiquidFuel = 400/400`).
5. Coroutine walks `FlightGlobals.Vessels` and identifies:
   - Vessel pid 3077499886 has parts matching `TransportPartPersistentIds` → transport half.
   - Vessel pid 3965530352 has parts matching `EndpointPartPersistentIds` → endpoint half.
   - Neither vessel has parts from both sets → KSP has cleanly split.
6. Snapshots both halves.
7. Calls `TryCompleteLatestRouteConnectionWindow(rec=5b385a6f, undockUT=144.76, transportSnapshot, endpointSnapshot)`.
8. Window completes: `UndockUT = 144.76`, `UndockTransportResources[LiquidFuel] = 400/400`, `UndockEndpointResources[LiquidFuel] = 0/400`.

Tree state after undock (in this transient-only case, with NO chain split):
- `430d3a0d` (parent, terminal Docked)
- `BP(40c3ad0d)`, Type Dock
- `5b385a6f` (still active recording, RouteConnectionWindow now COMPLETE, ExplicitEndUT not yet written)

No Undock branch point is created in this path. The merged child recording continues recording on the survivor vessel (pid 3965530352) until the tree is committed or scene exits.

### 7.4 At commit (UT 154.56)

Player commits the tree. `5b385a6f` gets `ExplicitEndUT = 154.56`, terminal classification, sidecars written. `RouteAnalysisEngine.AnalyzeTree(40f429f8)` walks the source path (via `CollectSourcePathRecordingIds`), finds `5b385a6f.RouteConnectionWindows[0]` is complete, accepts the candidate, computes the delivery manifest from `UndockTransportResources - DockTransportResources` (and the endpoint side), and posts the "Create Supply Route?" dialog.

---

## 8. Where each piece of state lands in persistent.sfs

For a save authored after a dock/undock cycle:

```
RECORDING_TREE
  id = ...
  rootRecordingId = <pre-dock recording id>
  activeRecordingId = <merged child id>          # in the transient-only case, no split
  RECORDING <pre-dock>                            # parent A
    explicitEndUT, terminalState=Docked, childBranchPointId
  RECORDING <merged-child>                        # the dock branch's child
    parentBranchPointId
    transferTargetPid, transferKind
    ROUTE_CONNECTION_WINDOWS
      WINDOW
        windowId, dockUT, undockUT
        transferTargetPid, transferKind, transferEndpointSituation
        TRANSPORT_PART_PIDS / ENDPOINT_PART_PIDS
        DOCK_TRANSPORT_RESOURCES / DOCK_ENDPOINT_RESOURCES
        UNDOCK_TRANSPORT_RESOURCES / UNDOCK_ENDPOINT_RESOURCES
        DOCK_TRANSPORT_INVENTORY / DOCK_ENDPOINT_INVENTORY
        UNDOCK_TRANSPORT_INVENTORY / UNDOCK_ENDPOINT_INVENTORY
        ENDPOINT_AT_DOCK
  BRANCH_POINT
    id, ut, type=2 (Dock)
    parentId, childId, mergeCause=DOCK, targetVesselPid
```

If an Undock branch was created (the non-transient case), there are additional entries:
- The merged child gets `childBranchPointId` set, `explicitEndUT` advanced to the undock UT.
- A new BRANCH_POINT with `type=3` (Undock), parents the merged child, children the two post-undock halves.
- Two new RECORDING nodes for the halves, each with `parentBranchPointId` set.

---

## 9. Known gaps and invariants to preserve

When modifying any code in this area, do not violate:

1. **`onPartCouple` semantics — capture pre-couple state before KSP reparents.** Any new snapshot or PID reference that's supposed to reflect the partner's pre-dock state must be taken inside `OnPartCouple`, before `StopRecordingForChainBoundary`, while `data.from.vessel != data.to.vessel`. Once `CreateMergeBranch` runs (next frame), `FindVesselByPid(mergedPid)` returns the merged vessel and pre-dock state is gone.
2. **`onPartUndock` may fire exactly once with the transient PID.** Don't assume a follow-up event will arrive. Any logic that absolutely must run on undock has to schedule a deferred coroutine from the transient-state branch and rely on `FlightGlobals.Vessels` after one frame.
3. **Route window completion is idempotent.** Both the chain-split path and the deferred coroutine path may run for the same undock. `TryCompleteLatestRouteConnectionWindow` checks `IsComplete` and returns false if the window is already closed — never double-write.
4. **Route window part-PID sets must be partner-scoped, not merged-scoped.** Endpoint-side resource extraction must use a snapshot that contains only endpoint parts. If the snapshot is the merged vessel and the part-PID set includes transport parts (e.g. from a `CollectPartPersistentIds(mergedSnapshot)` shortcut), the dock-side baseline inflates.
5. **Structural event snapshots are `TrajectoryPoint`s, not vessel snapshots.** They flag boundaries for the optimizer and ghost playback. Resources/inventory are captured separately by the route window.
6. **The transient-only undock path skips the structural event snapshot.** This is a gap (see §5.4) — ghost playback across the unbroken merged-child recording may interpolate across the undock UT without a flagged boundary. Not blocking for logistics, but worth fixing when revisiting ghost continuity.

---

## 10. File index

| Path | Role |
|---|---|
| `Source/Parsek/ParsekFlight.cs` | `OnPartCouple`, `OnPartUndock`, `CreateMergeBranch`, `CreateSplitBranch`, `HandleTreeDockMerge`, `DeferredUndockBranch`, `TryScheduleDeferredRouteWindowCompletionOnUndock`, `DeferredCompleteRouteWindowOnUndock`, `pendingDockPartnerSnapshot` field |
| `Source/Parsek/RouteProofCapture.cs` | `BuildDockRouteConnectionWindow`, `TryCompleteLatestRouteConnectionWindow`, `CompleteRouteConnectionWindowAtUndock`, `TryVerifyRoutePartSetsSeparated` |
| `Source/Parsek/RouteProofMetadata.cs` | `RouteConnectionWindow` data type, `IsComplete` predicate |
| `Source/Parsek/BranchPoint.cs` | `BranchPoint` + `BranchPointType` enum (Dock, Undock, Board, Breakup, EVA, ...) |
| `Source/Parsek/Recording.cs` | `Recording` type with `ParentBranchPointId` / `ChildBranchPointId` / `RouteConnectionWindows` |
| `Source/Parsek/RecordingTree.cs` | `RecordingTree.Recordings`, `BranchPoints`, `BackgroundMap`, `ActiveRecordingId`, `RootRecordingId` |
| `Source/Parsek/FlightRecorder.cs` | `AppendStructuralEventSnapshot`, `StopRecordingForChainBoundary`, recorder lifecycle |
| `Source/Parsek/VesselSpawner.cs` | `TryBackupSnapshot`, `ExtractResourceManifest`, `ExtractInventoryPayloadItems`, `CollectPartPersistentIds` |
| `Source/Parsek/Logistics/RouteAnalysisEngine.cs` | `AnalyzeTree`, `AnalyzeWindow`, `CollectSourcePathRecordingIds`, the eligibility gates |
