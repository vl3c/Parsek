# Parsek — Logistics (Supply Routes) Design

*Design specification for Parsek's stock-first automated cargo delivery system — covering Supply Run detection, Supply Route creation, dispatch scheduling, resource/inventory transfer between vessels, endpoint resolution, transfer window computation, and future round-trip linking across the rewind timeline.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies how committed recording chains can be confirmed as Supply Runs, then turned into Supply Routes that repeat the stock resource and inventory transfers the player already performed.*

**Version:** 0.7 (v0 implemented and shipped: a route is a looped Mission segment plus a delivery binding; this supersedes the bespoke chain-sequential playback of 0.5. See section 0, which is authoritative.)
**Status:** IMPLEMENTED. Supply Routes v0 shipped in Parsek 0.10.0 (developed on the `logistics-v0-implementation` branch, PR #875, plus the run-cost-display, recovery-credit, and Logistics-window UI follow-ups). Phases 0-6 of `docs/dev/plan-logistics-routes-on-missions.md` are built, tested (full xUnit suite green), and merged; the locked Missions subsystem is consumed read-only (its git diff is empty across the whole branch). Section 0 (the route-on-Missions architecture, including 0.9 dispatch cadence and 0.10 the code map) describes the implemented system and is authoritative. Sections 1-18 are the original standalone-route design, retained for the durable core (data model, delivery execution, endpoint resolution, edge cases) and for design rationale; where any of them conflicts with section 0, section 0 wins. The v0 cut is deliberately narrow: docking-only, delivery-only, single-stop, same-body routes. Pickup routes, mixed pickup/delivery windows, multi-stop routes, round-trip linking, non-docking connection producers, non-KSC undocked-start origins, and inter-body re-aimed routes remain future work (section 17).
**Prerequisite:** Phase 10 (Location Context) is complete and Phase 11 provides the base vessel-level resource/inventory manifests. Logistics v0 also required the connection-scoped capture extensions listed in section 4.9, plus the actual-code alignment tasks in section 4.10; these are in place.
**Out of scope:** Ghost playback engine behavior, broad recording/chain semantics, and game-actions recalculation architecture. Logistics does add route-proof recording metadata as an additive prerequisite; see `parsek-flight-recorder-design.md` and `parsek-game-actions-and-resources-recorder-design.md` for the underlying systems.

---

## 0. Architecture update (v0.10.0): Supply routes on the Missions foundation

*This section is authoritative and supersedes the playback assumptions in the rest of this document. Sections 1-11 below describe the original v0.5 model in which a route owned a bespoke "chain-sequential" replay; that replay is replaced by the Missions subsystem (landed on main, v0.10.0). The route's data model, resource/funds/ledger, endpoint resolution, and proof capture are unchanged.*

### 0.1 Why this changed

When this document was first written, no abstraction existed above raw recordings, so a route had to define its own ordered "source chain" and drive a bespoke replay. The Missions subsystem now provides exactly that layer: a Mission is a persisted, named selection over a recording tree (`Mission`, `MissionStore`); a segment of it is an interval selection (`MissionIntervalSelection`); and a Mission loops as one unit over a shared span clock in flight, the Space Center, and the Tracking Station (`MissionLoopUnitBuilder` -> `GhostPlaybackLogic.LoopUnitSet` -> the engine). A looped Mission is, by design, the precursor of a supply route (`design-mission-abstractions.md` section 6). So a route no longer invents replay; it reuses Missions.

### 0.2 Locked constraint: consume Missions, never modify it

The Missions subsystem (`Mission`, `MissionStore`, `MissionLoopUnitBuilder`, `MissionIntervalSelection`, `MissionStructure` / `MissionThroughLine` / `MissionComposition`, and the engine's `LoopUnit` / `LoopUnitSet` / `SetLoopUnits` machinery) is LOCKED. Logistics consumes its public components and must not change its types, its one-loop-per-tree rule, or the engine's single-owner index model. Every integration below is a new logistics-side consumer wired at the host seam, not an edit to a Missions file.

### 0.3 The route, restated in the Missions model

A Supply Route v0 is three things bound together:

1. **A backing Mission segment** — the run from launch (A) to the cargo delivery at the dock (B). Rendering STOPS at the docking moment (playtest follow-up): the docked-together combined vessel (the dock-merged child, spanning dock..undock) is NOT rendered. Expressed as a Mission over the source tree with an interval selection of `[launch .. dock]` (drop the dock-merged child + all post-dock peel intervals: `MissionIntervalSelection` end-trims a vessel by excluding its trailing interval keys). This is "a loopable mission in the backend."
2. **A delivery binding** — the dock / transfer / undock cargo proof (`RouteConnectionWindow`) that says WHAT moves and the dock UT at which it moves. Logistics-only; Missions carries no resource deltas. Unchanged from sections 4.9 / 5.2.
3. **A route lifecycle** — status, dispatch schedule, funds, endpoint resolution, ledger. Owned by the route (decision b, section 0.5). The durable core from sections 6-7 is kept.

Creation / verification is unchanged in spirit (section 5): a route is offered when a committed tree proves launch -> dock -> cargo delta -> undock. What changes is that the proven run is expressed as a Mission segment rather than a bespoke chain, and it then "appears in the supply routes list as a loopable run."

### 0.4 Rendering: a route reuses the loop engine as a consumer

A route does NOT render anything itself (no bespoke replay; the discarded `logistics-route-replay` `OffsetReplay` is fully superseded). When a route is in a ghost-driving state, logistics builds a `LoopUnit` for its backing Mission segment through the EXISTING `MissionLoopUnitBuilder.Build`, and contributes it to the per-frame `LoopUnitSet` that the host already pushes to the engine (the `ParsekFlight.DriveMissionLoopUnits` seam, mirrored in `ParsekKSC` / `ParsekTrackingStation`). All consuming locked components:

- The route owns its backing Mission DEFINITION (tree id + `[launch..dock]` interval selection), held by logistics (in the route entity / `RouteStore`), NOT inserted into `MissionStore` — the store would prune / normalize it by tree and surface it as a player mission. The Mission object is constructed by logistics and handed to `MissionLoopUnitBuilder.Build` as a one-element list (the builder gates on `Mission.LoopPlayback`, so the route sets that on its own object). The single helper `RouteBackingMission` owns this geometry seam (the only place logistics reads the locked Missions composition pipeline): `ComputeExcludedIntervalKeys` derives the post-DOCK trim (the docked combined-vessel child and everything after the dock), `ComputeMemberRecordingIds` derives the rendered `[root..dock]` member-recording set (so a multi-recording flight widens `RecordingIds` / `SourceRefs` past the dock-child leaf and `RevalidateSources` tracks the whole rendered path), and `BuildMission` materializes the route-owned Mission on demand. The window START derives from the tree ROOT launch, NOT the mid-flight dock child; the END is the connection window's `DockUT` (rendering stops at the dock; the `segmentEndUT` param of `RouteBackingMission`). `RouteAnalysisEngine` / `RouteCandidateFinder` verify PROOF only and no longer scan trajectory geometry. The post-dock trim is primarily the `StartUT >= dock` boundary (the merged child starts exactly at the dock, a clean recording boundary); the terminal-undock-child collection remains as belt-and-suspenders for the post-undock survivor legs.
- At the host push seam, the route's unit(s) are unioned into the same `LoopUnitSet` the player missions produce, before the single `SetLoopUnits` call. Union is on disjoint committed indices only (`LoopUnitSet.OwnerByIndex` is single-valued — one owner per recording index).
- Delivery fires when that loop clock crosses the recorded dock UT each cycle, reusing the existing delivery / funds / ledger path (sections 6.3-6.5). The dock UT is a phase within the backing Mission's span.

### 0.5 Two clocks: route lifecycle is separate from manual mission looping (decision b)

"Looping a mission" (the Missions-window checkbox — a visual repeat, no delivery, no cost, player-controlled) and "running a supply route" (delivery + funds + dispatch + status) are SEPARATE features. The route's runtime does not depend on the player's manual mission-loop checkbox: it loops via its own backing Mission, not the player's toggle. (The two are kept mutually exclusive per tree by the guard in section 0.6 — setting a route disables the manual toggle — but that is a UX guard, not a runtime dependency.) The route parameterizes its backing Mission's loop (period = dispatch interval, phase anchor = dispatch epoch) from its own schedule, and the loop renders those parameters. So there are two conceptual clocks — the route's dispatch schedule driving the backing Mission's loop — with the route as the driver.

### 0.6 Render parallelism: what works, what is deferred

- **Across different trees (the common case):** a route on tree X and a manual mission loop on tree Y both render at once. Their committed indices are disjoint, so their units never collide. Fully supported.
- **Same tree, same recordings — mutual exclusion (the rule):** the engine keys each committed recording index to exactly ONE loop owner (`LoopUnitSet.OwnerByIndex` is single-valued) and `MissionStore` enforces one loop per tree, so a route loop and a manual loop on the SAME tree cannot both render as two ghosts. Rather than resolve this at render time, v0 forbids the state outright: **a tree is either a supply route or a manually looped mission, never both.** Setting / activating a supply route for a tree turns OFF and disables (greys) the manual Loop toggle on that tree's mission rows; while a route is set on the tree, manual looping cannot be turned on; clearing the route re-enables the toggle. So the collision can never arise. This is also the correct visual (one looping ghost of a run, not two identical overlapping ones).
- **True parallel rendering of one recording on two independent clocks is DEFERRED.** It would require a multi-owner engine redesign, which is forbidden here. This matches the deferral already recorded in `design-mission-abstractions.md` open question 7 ("True multi-Mission looping of the SAME recording ... deferred to logistics").

### 0.7 What this supersedes, keeps, discards

- **Supersedes (replaced by Missions):** the bespoke "chain-sequential ghost playback" of sections 1.2 and 3 (Terminology: "Supply Route ... Uses chain-sequential ghost playback (not the per-recording loop system)") and any route-owned visual replay. The route's visual is now a Mission loop.
- **Keeps (durable core, unchanged):** the data model and serialization (section 4); route-proof recording metadata + capture (4.9, 5.2); delivery execution, per-resource independent fill, and the NO_FLOW / flow-state gate (6.3-6.5); endpoint resolution for the live DELIVERY target (section 7 — distinct from rendering; Missions does not resolve delivery targets); funds; the ledger Route module; and the dispatch-eligibility checks (6.1).
- **Discards:** the separate `logistics-route-replay` branch (`OffsetReplayUnit` / `RouteReplayPlanner`) — fully superseded by `MissionLoopUnitBuilder` + the two-cadence `LoopUnit`. Confirmed verified-absent in this line's `Source/` (grep + the `RouteReplayBranchAbsentTests` reflection guard, which fails the build if any such type reappears). Agents do not delete branches; the user prunes the `logistics-route-replay` branch.

### 0.8 v0 scope (keep it simple)

- v0 supply routes are **launch -> dock only** (one outbound run ending at the DOCK / delivery moment; the docked period AND the post-undock return tail are not part of the route segment). Rendering stops at the dock, which here is a clean recording boundary (the transport's solo recording ends at the couple; the merged combined-vessel child starts there), so trimming to the dock does NOT hit the deferred "isolate the docked stretch within one recording" gap. "Undock -> undock" (a shuttle already in orbit cycling between stations) and isolating a mid-recording docked stretch are still deferred (a known Missions interval-boundary gap: dock is not yet an interval boundary; see `design-mission-abstractions.md` "Docking & undocking (v1)" gap 1).
- **Same-body routes only for v0** (rover-to-base, tanker-to-LKO-station). The delivery clock builds the backing Mission with the SAME `bodyInfo` the render uses (DEL-1), so a same-body ORBITAL route phase-locks to the launch-pad rotation window exactly like a looped Mission (delivery fires on the relaunch the player sees), while a suborbital / atmospheric route stays uncompressed. Interplanetary re-aim / synodic windows exist in `MissionLoopUnitBuilder` (the `bodyInfo` seam) and stay deferred.

### 0.9 Dispatch cadence (Phase 6)

A route's effective dispatch cadence is `N x` the backing mission's NATURAL relaunch period, where that period is whatever the Missions layer already computes for the run: the span (suborbital same-body), the launch-pad-rotation window (orbital same-body), or the synodic transfer window (inter-body, via `MissionPeriodicity` / re-aim). `N` (`Route.CadenceMultiplier`) is an integer `>= 1`.

- **Default `N = 1` is the FLOOR / minimum loop time.** The route dispatches as fast as the run allows; it cannot go faster (the Phase 5 `DispatchInterval >= span` clamp and the Missions periodicity already enforce the floor). The player raises `N` in the Logistics window cadence stepper to launch LESS often (`2x`, `3x`, ...). The stepper shows both the multiplier and the resulting human cadence (e.g. `1x (~14m)` / `2x (~28m)`); `1x` is annotated as the minimum.
- **v0 same-body:** `DispatchInterval = N x TransitDuration` (derived in `RouteBuilder` at creation, recomputed by `RouteCadence.ApplyMultiplier` when `N` changes); `CadenceMultiplier` `N` is the player handle. For a suborbital / atmospheric route the loop clock's cadence IS that `DispatchInterval`. For an ORBITAL same-body route the backing unit (built with `bodyInfo = FlightGlobalsBodyInfo.Instance`, matching render) phase-locks: `PhaseAnchorUT` snaps to the launch-pad rotation window and `CadenceSeconds` quantizes up to a multiple of the rotation period, so the delivery clock and the rendered ghost share one cadence (DEL-1). Delivery fires when the loop clock reaches the recorded dock phase, not at cycle start (DEL-2).
- **Synodic-via-Missions reuse (the seam, no inter-body routes yet):** `RouteLoopClock` threads the backing-mission `LoopUnit`'s OWN relaunch schedule (`RelaunchSchedule`) + loiter cuts (`LoiterCuts`) into `GhostPlaybackLogic.TryComputeSpanLoopUT` instead of hardcoding `null`. For a v0 same-body route `RelaunchSchedule` / `LoiterCuts` are `null`, so the schedule passthrough itself is a no-op; same-body cadence alignment comes from the `bodyInfo` phase-lock above (DEL-1), not from this passthrough. When an inter-body route is later enabled, the backing Mission's unit carries the Missions-layer synodic / re-aim schedule, and delivery then fires on the SAME re-aimed launch UTs the ghost renders on — a route backed by a looped Mission inherits `MissionPeriodicity` / re-aim scheduling for free through this passthrough. Post-v0, `N` becomes a modulo on the scheduled-launch index rather than a multiplier on a fixed interval. Inter-body routes are NOT enabled in v0; this is only the seam. The `LoopUnit` schedule fields are consumed READ-ONLY (no locked Missions / engine file is edited).

### 0.10 Implemented code map (v0)

v0 (Phases 0-6) is built. The route-on-Missions system lives under `Source/Parsek/Logistics/` and consumes the locked Missions subsystem read-only. The seam files added or reshaped for the re-founding:

- `RouteStatusPolicy` (Phase 0): single source of truth over all 9 `RouteStatus` values. `BindsTree` (does the route own the tree's loop) and `GhostDriving` (does the route contribute a `LoopUnit` this frame). Exhaustive switch, throwing default, so a new status cannot silently fall through.
- `RouteBackingMission` (Phase 1): the only place logistics reads the locked Missions composition pipeline. `ComputeExcludedIntervalKeys` derives the post-undock trim, `ComputeMemberRecordingIds` derives the rendered `[root..undock]` member set, `BuildMission` materializes the route-owned Mission on demand for `MissionLoopUnitBuilder.Build` (never inserted into `MissionStore`).
- `RouteTreeGuard` (Phase 2): `IsTreeBoundToActiveRoute` + `ForceClearManualLoopForRouteTree`. Backs the mutual-exclusion guard (section 0.6) that greys the manual Loop toggle on both the Missions tab and the Recordings tab (per-recording / group / chain / bulk sites).
- `RouteGhostDriverSelector` (Phase 3): `SelectGhostDrivingBackingMissions`. The host unions these into the existing `LoopUnitSet` before its single `SetLoopUnits` push, at the three seams `ParsekFlight` / `ParsekKSC` / `ParsekTrackingStation` (`DriveMissionLoopUnits`).
- `RouteOrchestrator` (Phase 4): delivery on the loop clock. `EmitLoopCycle` fires the FULL cycle (origin/funds debit via the extracted dispatch body + delivery, one `cycleId`) when the clock crosses the recorded dock UT, reusing the section 6.3-6.5 path. On the ERS/ELS audit allowlist for the member-index alignment read.
- `RouteLoopClock` (Phase 4 + 6): wraps `GhostPlaybackLogic.TryComputeSpanLoopUT` as the per-cycle crossing predicate, and (Phase 6) threads the backing unit's own `RelaunchSchedule` + `LoiterCuts` into it (the section 0.9 synodic passthrough seam; a no-op for v0 same-body).
- `RouteBuilder` (Phase 5) + `RouteCreationDialog`: creation from a proven tree. Clamps `DispatchInterval >= span` and widens the member set to `[root..undock]`. The dialog defaults the interval to the full root-to-undock span.
- `Route.CadenceMultiplier` + `RouteCadence` + `RouteCodec` (Phase 6): the cadence handle `N` (clamped `>= 1`), its sparse serialization (omitted when 1), and `DispatchInterval = N x span`. The cadence stepper lives in `LogisticsWindowUI` (section 0.9).

Coverage: roughly 85 phase-affected xUnit cases plus the full suite green (13,513 passing, 0 failing); in-game coverage in `Source/Parsek/InGameTests/LogisticsRouteOnMissionsRuntimeTests.cs` (live delivery at the dock, cadence stepper) and `LogisticsRouteTreeGuardRuntimeTests.cs` (the mutual-exclusion guard). The locked-Missions git diff is empty across the whole branch and the ERS/ELS grep gate is green.

---

## 1. Introduction

> **Legacy section.** Sections 1-18 are the original standalone-route design. They are kept for the durable core (data model in section 4, delivery in 6.3-6.5, endpoint resolution in section 7, edge cases in section 10) and for rationale. For how a route actually renders and clocks in the shipped v0, section 0 is authoritative: a route is a looped Mission segment, not a bespoke chain-sequential replay. Read "chain-sequential ... replays the source chain" in this section as the superseded v0.5 model (see section 0.7).

This document specifies how Parsek turns a player-flown Supply Run into an automated Supply Route. A Supply Route is chain-sequential: the player flies a cargo mission, docks to a destination, uses stock KSP transfer systems, undocks, commits the recording, confirms that the run should become a route, and Parsek replays the source chain on each dispatch cycle while repeating the recorded cargo transfer.

- Logistics window / Supply Routes vocabulary
- Supply Run detection and route analysis
- How stops and delivery manifests are derived from docking windows in the committed chain
- Chain-sequential playback: source recordings play in order, delivery between recordings, restart after dispatch interval
- The dispatch cycle: check timing, check capacity, charge or deduct origin cargo, deliver
- Endpoint resolution: surface proximity fallback vs orbital PID matching
- Transfer window scheduling via synodic period computation
- Future round-trip linking for paired one-way routes
- Cargo modification on unloaded vessels (resources via ProtoPartResourceSnapshot, inventory via STOREDPARTS ConfigNodes)
- Timeline integration with explicit route ledger modules and epoch isolation for revert safety
- Module architecture: self-contained `Logistics/` directory with 4 integration seams
- Edge cases: destruction, full tanks, competing routes, time warp, reverts

### 1.1 What happens when the player creates a route

The player flies a mission normally — drive a rover with fuel to a base, launch a tanker to an orbital station, or send a cargo lander to a depot. During the flight, the transport docks to the destination, transfers cargo through stock KSP systems, then undocks. Docking ports are the only v1 connection type. The route model still names the broader concept "stock connection window" so claw/grapple or stock crossfeed/fuel-line support can be added later without replacing the route shape, but v1 should not block on those paths.

After the recording commits, Parsek analyzes the committed chain. If it finds a complete docking connection window with a delivery resource or inventory delta, the merge/recordings UI prompts: **"Create Supply Route from this Supply Run?"** The player sees a route summary (origin, endpoint, delivery manifest, KSC dispatch cost if any, transit time), sets or accepts the dispatch interval, and confirms. The Supply Route goes live.

No explicit **Record Supply Run** button is required for v1. Reducing actions is the better default: if the player docked, moved cargo, undocked, and committed, Parsek should assume route creation is likely and ask. A later helper button may still mark intent or suppress unrelated prompts, but it must not be required for correctness.

### 1.2 What happens each cycle

On schedule, the route scheduler begins a chain-sequential cycle. It tells the ghost playback engine to play the first source recording when visuals are available, but route state advances by UT. When the recorded delivery boundary UT is reached, delivery happens at the endpoint. The scheduler continues through the remaining source recordings, then waits for the dispatch interval before starting the next cycle.

Before each cycle, the route evaluates whether dispatch is possible. It checks the destination (is there room?), the origin (are there enough resources or inventory items, unless it is KSC), the KSC funds cost in Career, and whether the transit window is open. If everything checks out, origin cargo is deducted or the KSC dispatch funds cost is charged. The ghost replays the recorded chain visually during transit.

### 1.3 What the player sees

| Situation | What happens |
|-----------|-------------|
| Eligible Supply Run committed | Parsek offers to create a Supply Route from the run. |
| Supply Route confirmed | Route analysis extracts the endpoint and cargo manifest from the committed chain. Player sees summary, sets interval, confirms. |
| Route dispatches on schedule | Proven non-KSC origin cargo is deducted, or KSC dispatch cost is charged in Career. Ghost begins chain-sequential replay. |
| Route delivers | Cargo (resources, inventory) appears at the resolved endpoint vessel as the ghost reaches the recorded transfer point. |
| Destination tanks full | Dispatch waits on the same scheduled cycle. Origin NOT deducted, nothing delivered. The ghost still flies the route (the world looks busy) but transfers/charges nothing; the route status names the blocking reason. |
| Origin runs out of resources | Dispatch delayed until resources available. Route resumes automatically. |
| Destination destroyed (surface) | Nearest-compatible vessel fallback can reconnect to a rebuilt base at the same location. |
| Destination destroyed (orbital) | Route halted (`EndpointLost`). Player must re-target to new station. |
| Source recording missing | Route halted (`MissingSourceRecording`). No cargo transfers without the proof recording. |
| Source recording deleted during transit | Current cycle aborts before delivery and route becomes `MissingSourceRecording`. |
| Source recording rewritten/superseded | Route halted (`SourceChanged`). Player must recreate or explicitly reanalyze the route from the current proof data. |
| Player reverts past a dispatch | Epoch isolation invalidates route events. Stock quicksave/load restores stock vessels; Parsek timeline rewind uses route ledger modules to reverse or mask route effects before live cargo mutation is allowed. |
| Two routes linked as round-trip | Future feature: they alternate, Route A completes -> Route B dispatches -> B completes -> A dispatches. |

### 1.4 Example: fuel delivery rover

```
SUPPLY RUN:
  Phase 1: Rover departs KSC runway with 200 LF
  Phase 2: Rover docks at base (connection EndResources: 195 LF)
  Phase 3: Player transfers 150 LF to base (rover: 45 LF, base: +150 LF)
  Phase 4: Rover undocks and drives clear
  Player commits recording; Parsek offers "Create Supply Route?"

ROUTE ANALYSIS PRODUCES:
  Origin:     KSC runway area (Career dispatch cost = stock part cost + used/delivered cargo cost)
  Endpoint:   base location (from stock connection target PID + connection coordinates)
  Delivery:   150 LF per cycle (rover loses 150 LF; base gains 150 LF and retains it through undock)
  Transit:    chain duration (~10 min)
  Interval:   player-set (minimum = chain duration)

EACH CYCLE (chain-sequential):
  UT=0:     Dispatch. Career funds charged if applicable. Ghost rover starts the first source recording.
  UT=stop:  Recorded delivery boundary reached. 150 LF added to base, clamped to tank capacity.
            Ghost continues through the rest of the source chain.
  UT=total: Chain complete. Cycle done. Wait for dispatch interval.
            If base full at dispatch check -> dispatch waits; no LF added, origin not deducted.
```

### 1.5 Example: Minmus mining supply chain

```
ROUTE A: KSC -> Minmus Base (fuel supply)
  Origin:     KSC (Career dispatch cost charged)
  Delivery:   800 LF + 978 Ox per cycle
  Result:     mining base stays fueled for drill + ISRU operation

ROUTE B: Minmus Base -> Kerbin Depot (ore delivery)
  Origin:     Minmus base (non-KSC -- Supply Run starts docked to the base depot)
  Cost:       1200 Ore + 600 LF + 733 Ox per cycle (used/delivered manifest)
  Delivery:   1200 Ore per cycle
  Gate:       dispatch delayed until base has all required resources

CHAIN BEHAVIOR:
  Route A delivers fuel -> base mines ore -> Route B ships ore to depot.
  If Route A stops -> base runs dry -> Route B pauses indefinitely.
  If depot tanks full -> Route B waits on its current scheduled cycle, base accumulates ore.
```

---

## 2. Design Philosophy

These principles govern every design decision in the logistics system. They are listed here because they inform every section that follows.

### 2.1 Realism and fidelity

1. **Fly it once, automate it forever.** Routes replicate exactly what the player did during the recording. The delivery amount, transit duration, and fuel cost are all derived from the real flight — not configured abstractly.

2. **Transit takes real time.** The route duration equals the recording duration. A 3-year Eeloo transfer takes 3 years per cycle. A 10-minute rover drive takes 10 minutes. No shortcuts.

3. **Transfer windows are respected.** Inter-body routes dispatch at the synodic period of the origin and destination bodies, phase-anchored to the original Supply Run start UT. Same-body routes can dispatch at any time but not faster than the original recording.

### 2.2 Resource safety

4. **Stock proof-of-work.** Parsek may automate stock actions the player already performed, but it must not invent storage, cargo, transfer rules, crew, or production rules. A Supply Route exists because a committed Supply Run proves the transport, path, stock connection, cargo delta, and disconnect. In v1 that proof is specifically dock, deliver, and undock.

5. **No infinite cargo glitches.** Routes deliver exactly what was transferred during the recording — no more, no less. KSC origins are not free in Career: each dispatch charges stock-realistic funds for the source vessel parts plus the resource/inventory quantities used or delivered by the Supply Run. Non-KSC origins deduct the resource/inventory quantities used or delivered from a real origin vessel, but v1 only allows that when the run starts docked to that origin depot and records its PID. Recovery funds from the original flown vessel are one-time unless a later round-trip design explicitly models repeat recovery.

6. **Don't waste origin cargo.** Origin is only deducted if at least one delivery item can be accepted at the destination. If destination is completely full, the dispatch waits on the same scheduled cycle and origin pays nothing. Per-item delivery is independent: each item fills what fits, and shortfalls are logged and shown instead of silently disappearing.

7. **Dock + deliver + undock required in v1.** A route can only be created from a recording chain where the transport forms a detected docking connection to an endpoint, delivery cargo moves from the transport into the endpoint while docked, the endpoint retains that cargo through undock, and the transport undocks afterward. Claw/grapple and stock crossfeed/fuel-line paths are deferred until docking routes are reliable.

### 2.3 Abstraction model

8. **No physical vessel during transit.** Route execution is pure math — deduct at origin, wait, add at destination. The ghost is visual only. No physics, no collisions, no orbit propagation.

9. **Endpoints are vessels first, locations second.** Endpoint PID is the primary identity. Surface endpoints may fall back to a single nearest compatible vessel near the recorded coordinates, but Parsek does not create an abstract area warehouse. Orbital endpoints use PID only. This keeps delivery close to stock vessel semantics while still tolerating surface base rebuilds.

10. **The system doesn't produce cargo.** Parsek moves resources and inventory items between stock vessels. Mining, ISRU, solar power, manufacturing, and crew hiring are the player's responsibility. Routes chain naturally: the output of one feeds the input of another.

### 2.4 Timeline integration

11. **Dispatches and deliveries are ledger-backed timeline events.** v1 live stock mutation is gated on adding explicit route dispatch/delivery `GameActionType` entries and route ledger modules. Reverts invalidate dispatches from abandoned timelines. Stock quicksave/load restores stock vessels from `.sfs`; Parsek timeline rewind must use the route ledger modules to reverse or mask KSC funds, physical origin debits, and endpoint deliveries. Until that mechanism exists, route creation can analyze candidates but must not mutate stock vessels.

12. **Routes persist across scenes and save/load.** All route state is serialized in the .sfs. The scheduler runs in all scenes via ParsekScenario.

---

## 3. Terminology

**Logistics** — the player-facing feature/window that manages Supply Routes.

**Supply Route** — a separate entity that defines one recurring cargo transfer from an origin to one endpoint. Created from a committed Supply Run after player confirmation. Uses chain-sequential ghost playback (not the per-recording loop system).

**Supply Run** — the concrete player-flown recording chain that proves a route. It contains the transport path, stock connection window, resource/inventory delta, and disconnect. In v1 the connection window is dock/undock.

**Route stop / endpoint** — the destination vessel and location for the route. v1 exposes one endpoint per Supply Route. The data model keeps a `Stops` list so multi-stop Supply Runs can be added later without replacing the save shape.

**Stock connection window** — the bounded time interval where the transport is connected to the endpoint by a stock mechanism and cargo can move. Docking port dock/undock is the only v1 window. Claw/grapple and other stock transfer paths are future producers for the same interface once detection is proven.

**Endpoint** — origin or destination location of a route. Defined by body, coordinates, and the target vessel PID from the stock connection window. Surface endpoints can fall back to one nearest compatible vessel near the recorded coordinates. Orbital endpoints use PID only.

**Delivery manifest** — the per-resource and inventory amounts delivered at the endpoint. v1 is delivery-only: resource and inventory deltas must represent cargo leaving the transport and appearing on the endpoint part set while docked. Pickup routes and mixed pickup/delivery windows are deferred.

**Cost manifest** — the resource and inventory quantities used or delivered by the Supply Run. For non-KSC origins, this is deducted from the recorded start-docked origin depot each cycle.

**KSC dispatch cost** — in Career, the funds cost charged when a KSC-origin route dispatches. It is computed from stock costs for the source vessel parts plus the resource/inventory quantities used or delivered by the Supply Run.

**Dispatch** — the moment a route cycle begins. Origin resources are checked and deducted only after the route ledger event is recorded. A timeline event is created.

**Delivery** — the moment delivery occurs at a stop boundary. Resources and inventory are added to the endpoint vessel only through the route delivery ledger/applier path. A timeline event is created.

**Round-trip link** — future scheduling constraint pairing two one-way routes: "don't dispatch me until my partner completes."

**Route analysis engine** — pure logic that walks the committed Supply Run chain to extract the endpoint, delivery manifest, source recording IDs, and transit duration from recording data (resource/inventory manifests, stock connection target PID, connection kind, location context).

---

## 4. Data Model

### 4.1 ResourceAmount

`ResourceAmount` is the existing internal type in `Source/Parsek/ResourceManifest.cs`; logistics reuses it and must not introduce a duplicate definition.

```csharp
internal struct ResourceAmount
{
    public double amount;
    public double maxAmount;
}
```

### 4.2 InventoryPayloadItem

```csharp
internal class InventoryPayloadItem
{
    public string PartName;                 // stock part name from STOREDPART
    public string VariantName;              // variant/theme identity when present
    public int Quantity;                    // stack quantity for identical payload snapshots
    public int SlotsTaken;                  // stock inventory slot cost
    public double UnitDryCost;              // dry part/module cost, excluding stored resources
    public Dictionary<string, double> StoredResources; // resources carried inside the stored part
    public string IdentityHash;             // hash of the canonical STOREDPART snapshot
    public ConfigNode StoredPartSnapshot;   // full stock STOREDPART node for reconstruction
}
```

Inventory route manifests use exact stored-part payload snapshots, not part-name counts. Two stored parts with different variants, resource contents, module state, or stock snapshot data are different payload items. `Quantity` may compress identical snapshots only after the canonical `STOREDPART` payload is equal. `StoredPartSnapshot` must preserve the stock node identity as `STOREDPART`; wrapper nodes used by Parsek serialization must be stripped before hashing or reconstructing inventory.

`IdentityHash` is computed from a deterministic canonical form, not from `ConfigNode.ToString()` directly. Canonicalization must include the node name, sorted value entries by key then value, and child nodes sorted by node name then canonical payload. Numeric values that logistics writes must use `ToString("R", CultureInfo.InvariantCulture)` before hashing. Existing stock string values are preserved as strings. The hash input must exclude transient ordering, whitespace, comments, `slotIndex`, stack `quantity`, and vessel-local lifecycle fields inside the nested `STOREDPART > PART` snapshot (`persistentId`, launch/attachment/position/rotation/temperature/derived cost-mass style fields) so a save/load round trip, slot move, stack split, or dock-to-undock inventory move does not change payload identity. Slot usage and quantity remain separate manifest fields, while nested `RESOURCE` and `MODULE` payload state remains identity-defining.

### 4.3 RouteEndpoint

```csharp
internal struct RouteEndpoint
{
    public string bodyName;        // e.g., "Kerbin", "Mun"
    public double latitude;        // from dock event trajectory point
    public double longitude;
    public double altitude;
    public uint vesselPersistentId;         // 0 = KSC sentinel for KSC origins; non-zero for all stops and non-KSC origins
    public bool isSurface;         // true = landed/splashed/prelaunch endpoint; enables surface fallback
}
```

`vesselPersistentId = 0` is reserved for KSC origins where there is no stock origin vessel to resolve. Non-KSC origins and all route stops must use a non-zero vessel PID captured from the stock connection boundary.

### 4.4 RouteConnectionKind

```csharp
internal enum RouteConnectionKind
{
    DockingPort,
    Grapple,        // stock Advanced Grabbing Unit / claw, if target detection is reliable
    StockCrossfeed, // stock transfer/crossfeed path, if endpoint detection is reliable
    Unknown
}
```

### 4.5 RouteStatus

```csharp
internal enum RouteStatus
{
    Active,             // dispatching on schedule
    InTransit,          // dispatched, waiting for transit duration to elapse
    WaitingForResources, // origin exists but lacks resources — delayed
    WaitingForFunds,    // Career KSC-origin route lacks dispatch funds — delayed
    DestinationFull,    // destination can't accept delivery — waiting for capacity
    EndpointLost,       // destination/origin vessel gone (orbital PID miss or no surface vessels)
    MissingSourceRecording, // route source recording chain is gone; route cannot dispatch
    SourceChanged,      // source recording exists but no longer matches the route proof fingerprint
    Paused              // player manually paused
}
```

### 4.6 RouteStop

```csharp
internal class RouteStop
{
    public RouteEndpoint Endpoint;                          // where this stop is
    public RouteConnectionKind ConnectionKind;               // how the Supply Run connected
    public Dictionary<string, double> DeliveryManifest;     // per-resource delivery amounts (positive only in v1)
    public List<InventoryPayloadItem> InventoryDeliveryManifest; // exact stored-part payloads delivered in v1
    public int SegmentIndexBefore;                          // 0-based source recording whose completion UT triggers this stop
    public double DeliveryOffsetSeconds;                    // seconds from CurrentCycleStartUT to this stop boundary
}
```

v1 exposes a single-stop route in the UI. Multi-stop Supply Runs are a planned extension; the list shape stays now so save data does not need to be replaced later.

### 4.7 Route

```csharp
internal class Route
{
    // Identity
    public string Id;                    // unique route ID (GUID)
    public List<string> RecordingIds;    // ordered chain of source recording IDs
    public string Name;                  // player-visible name (editable)
    public List<RouteSourceRef> SourceRefs; // immutable source proof/version refs captured at route creation

    // Endpoints
    public RouteEndpoint Origin;
    public List<RouteStop> Stops;        // ordered stops along the route
    public bool IsKscOrigin;             // true = Career charges KSC funds instead of physical origin cargo

    // Resource transfer
    public Dictionary<string, double> CostManifest;       // per-resource quantities used or delivered
    public List<InventoryPayloadItem> InventoryCostManifest; // exact stored-part payloads used or delivered
    public double KscDispatchFundsCost;                   // stock part + used/delivered cargo funds per KSC dispatch

    // Timing
    public double TransitDuration;       // seconds (= total chain duration)
    public double DispatchInterval;      // seconds between cycle starts
    public double DispatchWindowEpochUT; // original flight start UT; anchors inter-body synodic phase
    public double DispatchWindowPeriod;  // 0 for same-body, synodic period for inter-body
    public double NextDispatchUT;        // UT of next scheduled dispatch
    public double? CurrentCycleStartUT;  // UT when the in-transit cycle began; null when idle
    public double? NextEligibilityCheckUT; // retry backoff for resource/funds waits; null when not waiting
    public int CurrentSegmentIndex;      // 0-based active source-recording index; -1 when not in transit

    // Per-stop pending delivery (computed at each stop boundary during transit)
    public double? PendingDeliveryUT;    // UT when next route boundary is due (null if not in transit)
    public int PendingStopIndex;         // stop due at PendingDeliveryUT, or -1 when current boundary has no stop

    // Linking
    public string LinkedRouteId;         // paired route for round-trip (null if standalone)

    // State
    public RouteStatus Status;
    public bool PauseAfterCurrentCycle; // pause requested while InTransit; transition to Paused after completion
    public int CompletedCycles;          // total successful cycle completions
    public int SkippedCycles;            // reserved diagnostic counter for explicit skip policies; v1 wait states do not increment it
}
```

```csharp
internal class RouteSourceRef
{
    public string RecordingId;
    public string TreeId;
    public int TreeOrder;
    public int RecordingFormatVersion;
    public int RecordingSchemaGeneration;
    public int SidecarEpoch;
    public double StartUT;
    public double EndUT;
    public string RouteProofHash;  // hash of route-relevant connection windows/manifests used by this route
}
```

**Forward compatibility with multi-stop routes:** A v1 Supply Route has `Stops.Count == 1`. Later multi-stop routes can reuse the same list without changing the top-level route save node.

**Route playback segment definition:** In this document, a route playback segment is one source recording in `RecordingIds`. `CurrentSegmentIndex` indexes `RecordingIds` directly: `-1` means no active transit, `0` means the first source recording is active, `1` means the second source recording is active, and so on. It does not index trajectory samples, internal `TrackSection` ranges, or lower-level chain-manager segments. `RouteStop.SegmentIndexBefore` is the source-recording index whose visual completion boundary normally corresponds to that stop, but delivery timing is pinned by `RouteStop.DeliveryOffsetSeconds`.

**Source immutability contract:** A route stores its timing, delivery offsets, manifests, and source proof fingerprints when it is created. The scheduler uses the stored route fields, not live recording durations, to decide dispatch and delivery timing. At dispatch and before each in-transit delivery, route revalidation compares `SourceRefs` against the current committed recordings. A missing source sets `MissingSourceRecording`; a source with the same ID but changed route-relevant epoch/fingerprint sets `SourceChanged`. Route effects stop until the player revalidates/recreates the route from the current committed recording data.

### 4.8 Serialization format

```
ROUTE
{
    id = <guid>
    name = Mun Fuel Run

    RECORDING_IDS
    {
        id = <recording-guid-1>
        id = <recording-guid-2>
        id = <recording-guid-3>
    }
    SOURCE_REFS
    {
        SOURCE
        {
            recordingId = <recording-guid-1>
            treeId = <tree-guid>
            treeOrder = 0
            recordingFormatVersion = 0
            recordingSchemaGeneration = 1
            sidecarEpoch = 12
            startUT = 42654.0
            endUT = 47000.0
            routeProofHash = 93C2...
        }
    }

    ORIGIN
    {
        bodyName = Kerbin
        latitude = -0.0972
        longitude = -74.5577
        altitude = 75.2
        vesselPersistentId = 0
        isSurface = True
    }

    STOP
    {
        ENDPOINT
        {
            bodyName = Mun
            latitude = 3.2001
            longitude = -45.1234
            altitude = 612.5
            vesselPersistentId = 67890
            isSurface = True
        }
        connectionKind = DockingPort
        segmentIndexBefore = 1
        deliveryOffsetSeconds = 600.0
        DELIVERY_MANIFEST
        {
            LiquidFuel = 150.0
            Oxidizer = 183.3
        }
        INVENTORY_DELIVERY_MANIFEST
        {
            ITEM
            {
                partName = smallSolarPanel
                variantName =
                quantity = 2
                slotsTaken = 2
                unitDryCost = 75.0
                identityHash = 8F17...
                STOREDPART
                {
                    // canonical stock STOREDPART snapshot, including variant/module/resource payload
                }
            }
        }
    }
    isKscOrigin = True
    kscDispatchFundsCost = 12500.0
    transitDuration = 12345.6 // total source path duration; delivery timing uses STOP.deliveryOffsetSeconds
    dispatchInterval = 43200.0
    dispatchWindowEpochUT = 42654.0
    dispatchWindowPeriod = 0.0
    nextDispatchUT = 258654.0
    // currentCycleStartUT omitted when null
    // nextEligibilityCheckUT omitted when null
    currentSegmentIndex = -1
    // pendingDeliveryUT omitted when null
    pendingStopIndex = -1
    linkedRouteId =
    status = Active
    pauseAfterCurrentCycle = False
    completedCycles = 5
    skippedCycles = 0

    COST_MANIFEST
    {
        LiquidFuel = 155.0
        Oxidizer = 183.3
    }
    INVENTORY_COST_MANIFEST
    {
        ITEM
        {
            partName = smallSolarPanel
            variantName =
            quantity = 2
            slotsTaken = 2
            unitDryCost = 75.0
            identityHash = 8F17...
            STOREDPART
            {
                // same canonical payload identity used for delivery and cost
            }
        }
    }
    // KSC-origin routes use ORIGIN.vesselPersistentId = 0. Non-KSC origins and all STOP endpoints use real vessel PIDs.
    // When Status == InTransit, pendingDeliveryUT/pendingStopIndex identify the next due boundary.
    // When Status == InTransit, currentCycleStartUT identifies the scheduler elapsed time for visual handoff.
    // When Status == WaitingForResources or WaitingForFunds, nextEligibilityCheckUT gates retry polling.
    // Actual deliverable amounts are recomputed at delivery time from current endpoint capacity.
    // Delivery timing uses serialized deliveryOffsetSeconds; live recording duration changes do not shift a route.
}
```

Routes are stored in their own `ROUTES` ConfigNode section inside ParsekScenario's save data, alongside recordings and game actions. Additive — saves without routes load fine. Routes reference recordings by ID but are independent entities. A route whose source recordings are missing is disabled, not allowed to keep moving cargo without the proof recording.

### 4.9 Recording extensions (Phase 11 + Logistics prerequisites)

Phase 11 adds base vessel-level manifest metadata to recordings. The current code has `ResourceAmount`, `InventoryItem`, `Recording.StartResources`, `Recording.EndResources`, `Recording.StartInventory`, `Recording.EndInventory`, manifest codecs, and the resource/inventory extractors in `VesselSpawner`. Those are useful diagnostics, but they are not sufficient proof for Logistics.

Logistics also needs connection-scoped manifests: during docking, KSP merges vessels, so aggregate vessel resources cannot prove that cargo moved between the transport and endpoint. Route analysis must filter manifests by the original transport part persistent IDs and original endpoint part persistent IDs captured at the docking boundary, then resolve those same part sets after undock.

**Resource manifests** (current Phase 11 base):

```csharp
public Dictionary<string, ResourceAmount> StartResources;  // manifest at recording start
public Dictionary<string, ResourceAmount> EndResources;     // manifest at recording end
```

`ResourceAmount` is a struct with `amount` and `maxAmount` fields. Resources are summed across all parts. ElectricCharge and IntakeAir are excluded (environmental noise). Extracted by `VesselSpawner.ExtractResourceManifest(ConfigNode vesselSnapshot)`.

Implementation prep must verify the always-tree path persists these fields on the actual committed `Recording` objects, not only on transient `FlightRecorder.CaptureAtStop` objects. In the current code, always-tree root recordings are created before `FlightRecorder.StartRecording`, and `ParsekFlight.AppendCapturedDataToRecording` / `FlushRecorderToTreeRecording` append trajectory data but do not currently make the manifest ownership invariant obvious. Before route analysis ships, tests must prove committed tree recordings carry the intended start/end manifests after normal commit, dock/undock, split/merge, scene-exit, and optimizer paths.

**Connection-scoped manifests** (v1 logistics prerequisite; implementation in progress):

```csharp
internal sealed class RouteConnectionWindow
{
    public string Id;
    public RouteConnectionKind Kind;                          // DockingPort only in v1
    public double DockUT;
    public double UndockUT;
    public uint TransportVesselPidAtDock;
    public uint EndpointVesselPidAtDock;
    public List<uint> TransportPartPersistentIds;             // original transport part set
    public List<uint> EndpointPartPersistentIds;              // original endpoint part set
    public Dictionary<string, ResourceAmount> DockTransportResources;
    public Dictionary<string, ResourceAmount> UndockTransportResources;
    public Dictionary<string, ResourceAmount> DockEndpointResources;
    public Dictionary<string, ResourceAmount> UndockEndpointResources;
    public List<InventoryPayloadItem> DockTransportInventory;
    public List<InventoryPayloadItem> UndockTransportInventory;
    public List<InventoryPayloadItem> DockEndpointInventory;
    public List<InventoryPayloadItem> UndockEndpointInventory;
    public RouteEndpoint EndpointAtDock;
    public Vessel.Situations TransferEndpointSituation;       // endpoint vessel situation at docking
}

internal sealed class RouteOriginProof
{
    public uint StartDockedOriginVesselPid;                   // non-KSC origin depot, if recording starts docked
    public Dictionary<string, ResourceAmount> StartTransportResources;
    public Dictionary<string, ResourceAmount> EndTransportResources;
    public List<InventoryPayloadItem> StartTransportInventory;
    public List<InventoryPayloadItem> EndTransportInventory;
}
```

Store completed connection windows on the tree recording that represents the docked merged vessel. In the always-tree flow, a dock creates a `BranchPointType.Dock` merge and a merged child recording; the later undock creates a split from that merged recording. The route window naturally spans that merged child. If the window cannot be completed with a matching undock, the route candidate is invalid.

Current implementation prep has the always-tree dock/undock skeleton in place: `RouteProofCapture` builds the dock window from the merged vessel snapshot, records transport/endpoint part PID sets, filters resource manifests by those PID sets, preserves exact `STOREDPART` inventory payload snapshots, and completes the same window at the later undock from the two split-vessel snapshots. Active-as-target dock merges must not look up the absorbed endpoint vessel after the merge, because stock may already have destroyed it; endpoint proof is captured from the dock event when available and otherwise restored from the absorbed background parent snapshot. Runtime validation is still required for the stock dock event's before/after vessel references, but missing active-as-target endpoint proof is no longer an accepted route candidate.

The docked aggregate vessel manifest is still useful for diagnostics, but Logistics v1 must compute delivery from matched transport-loss and endpoint-gain fields. Cost uses full-run fields scoped to the original transport part set. Missing connection-scoped fields mean the recording cannot become a Supply Route.

Serialized as:

```
RESOURCE_MANIFEST
{
    RESOURCE { name = LiquidFuel  startAmount = 3600  startMax = 3600  endAmount = 200  endMax = 3600 }
    RESOURCE { name = Oxidizer    startAmount = 4400  startMax = 4400  endAmount = 244  endMax = 4400 }
}
```

**Stock connection target vessel PID** (v1 logistics prerequisite):

```csharp
public uint TransferTargetVesselPid;       // PID of vessel connected to at this segment boundary (0 = no route-relevant connection)
public RouteConnectionKind TransferKind;   // DockingPort only in v1; future-proofed for later producers
```

For the first implementation, `TransferTargetVesselPid` is populated from the dock-merge path as the other vessel in the stock docking event, from the active recording's perspective. It must never fall back to the post-dock merged vessel PID: if the other vessel cannot be identified, store `0` and make route analysis reject the candidate as missing endpoint proof. Route analysis should consume the generic `TransferTargetVesselPid` / `TransferKind` contract, not reach into legacy `DockTargetVesselPid` directly.

**Inventory manifests** (current Phase 11 base plus required Logistics extension):

KSP 1.12 `ModuleInventoryPart` items are stored parts in cargo containers. The current Phase 11 implementation returns `Dictionary<string, InventoryItem>` keyed by part name with count and slot usage. That is good for diagnostics, but it is not enough for route delivery.

Logistics v1 requires exact payload snapshots. The extension to `ExtractInventoryManifest` must walk MODULE > STOREDPARTS > STOREDPART nodes, preserve each canonical `STOREDPART` ConfigNode, record slot usage, variant identity, stored resources, and a stable identity hash. Delivery and cost matching operate on those payload identities, not just part names. This enables automated parts delivery without treating variants, stacked payloads, or resource-filled stored parts as fungible.

**Crew manifests** (implemented — Phase 11, not consumed by Logistics v1):

Crew composition by trait (Pilot/Scientist/Engineer/Tourist). Logistics v1 does not deliver generic kerbals; stock KSP crew are named roster entities, not fungible cargo. Crew rotation should be a later feature with explicit roster, hiring, and reservation semantics.

All manifest types are additive: missing node = no data. Route entity data in the separate `ROUTES` section does not require a recording format bump. Recording-side route proof fields target additive missing-node defaults, but the final implementation must confirm the current `RecordingFormatVersion` / `RecordingSchemaGeneration` policy with the `RecordingTreeRecordCodec` owner. If the current schema gate requires a generation bump for newly persisted recording metadata, bump the schema and add compatibility tests instead of asserting "no bump" by default.

### 4.10 Actual code contract before v0 implementation

The first implementation phase is not `RouteScheduler`. It is a recorder/data-contract alignment phase. The route module can stay clean only if committed recordings expose trustworthy route-proof data first.

**Current code facts:**

- Always-tree mode is canonical. A Supply Run should be analyzed from `RecordingStore.CommittedTrees`, `RecordingTree.Recordings`, and `RecordingTree.BranchPoints`, not from a standalone recording list.
- `Recording.StartResources` / `EndResources` and `StartInventory` / `EndInventory` exist, and the tree codec serializes them. Tests must prove the committed always-tree path actually populates them after the active recorder flushes.
- `Recording.DockTargetVesselPid` exists for legacy dock segments. Always-tree dock merges need route-facing metadata on the dock branch/window: target PID, connection kind, endpoint situation, endpoint coordinates, and the two part PID sets.
- `BranchPoint.MergeCause` / `TargetVesselPersistentId` already serialize. `TargetVesselPersistentId` also feeds `GhostChainWalker`, so logistics must leave it at `0` unless it has explicit, tested dock endpoint proof.
- `TrajectoryPoint` has `latitude` / `longitude` / `altitude` / `bodyName`; `Recording` does not have `StartLatitude` or `StartLongitude` fields. Route origin/endpoint coordinates must come from the selected trajectory point or from `RouteConnectionWindow.EndpointAtDock`, not from nonexistent recording-level coordinate fields.
- `GameActionType` has no route dispatch/delivery entries yet. v0 decision: live route effects must use explicit route action types plus recalculation/rollback support. Route-local persisted state may cache scheduler state, but it must not be the sole authority for stock funds/cargo mutation. Shipping hidden stock mutations outside the ledger contract is not allowed.

**Required prep tasks:**

1. **Manifest invariant:** define and test exactly which committed tree recording owns `StartResources`, `EndResources`, `StartInventory`, `EndInventory`, and the route transport-scoped start/end manifests. This must cover single-node trees, dock merges, undock splits, scene-exit commits, and optimizer rewrites.
2. **Connection window capture:** add a serializable route connection window on the docked merged recording. Populate it at dock with endpoint PID/situation/coordinates, transport and endpoint part PID sets, and dock-side scoped resource/inventory manifests. Complete it at undock with undock-side scoped manifests.
3. **Exact inventory payloads:** add `InventoryPayloadItem` extraction/serialization while preserving the existing lightweight `InventoryItem` diagnostics. Route delivery must depend on exact payload identity, not part-name counts.
4. **Route-facing connection fields:** map existing dock data into `TransferTargetVesselPid` / `TransferKind` and stop route analysis from depending on legacy names. v1 rejects anything except `DockingPort`.
5. **Origin proof:** detect and serialize `StartDockedOriginVesselPid` only when the Supply Run starts connected to a real non-KSC depot. Non-KSC candidates without that proof are invalid.
6. **Timeline contract:** implement route `GameActionType` entries and route ledger modules before stock mutation code lands. Career KSC charges and physical origin/destination resource edits must be replayable or invalidated across save/load and rewind using the same epoch rules as other committed effects.

Until these tasks are done, route creation must report "recording lacks logistics proof data" instead of offering a Supply Route prompt.

---

## 5. Route Creation

### 5.1 Supply Run workflow

Route creation uses an automatic post-commit prompt on an eligible Supply Run. A special pre-flight mode is not required for v1.

**Player flow:**

1. Player flies mission normally.
2. Transport docks to the destination.
3. Player transfers resources and/or stock inventory to the destination through KSP's normal UI/systems.
4. Transport undocks from the destination so the endpoint is available for the next dispatch.
5. Player commits the recording.
6. If route analysis finds an eligible Supply Run, Parsek automatically prompts "Create Supply Route from this Supply Run?"
7. Player reviews origin, endpoint, delivery manifest, KSC dispatch cost, transit time, and interval; then confirms.
8. Supply Route goes live.

**Deferred "Record Supply Run" helper:**

- May mark the current recording tree as route-intended for UI filtering and prompt suppression.
- Shows a "Supply Run Active" indicator.
- Does not change recorder behavior. The same `FlightRecorder`, chain boundaries, snapshots, and manifests are used.
- Does not make an invalid run valid. The committed chain must still contain a detected docking connection, delivery cargo delta, and undock.

### 5.2 Route analysis engine

`RouteAnalysisEngine` (in `Logistics/`) is pure logic over committed recording-tree data. Fully testable without KSP.

**Input:** a committed `RecordingTree`, or a deterministic projection from that tree into the route source path. This can come from an explicit route-intended session marker, the just-committed tree, or a "Create Supply Route" action on an existing recording tree.

Current implementation prep has the first read-only analyzer slice: it scans the active source path for exactly one completed `RouteConnectionWindow`, rejects old recordings that only have aggregate Phase 11 manifests, ignores off-path windows, rejects multi-window/multi-stop candidates for v0, rejects mixed pickup/delivery windows, requires endpoint proof, and derives resource/inventory delivery manifests only when endpoint gains are matched by transport losses. It does not yet create route entities, persist route store data, prompt the user, schedule dispatches, or mutate stock resources.

Always-tree mode means the analysis input is not just a flat chronological list. The engine must inspect:

- `RecordingTree.RootRecordingId` and `RecordingTree.Recordings` for source recordings.
- `RecordingTree.BranchPoints` for dock/undock structure.
- Completed `RouteConnectionWindow` metadata stored on the docked merged recording.
- Recording UT bounds (`StartUT` / `EndUT`), source `RecordingId`s, and trajectory points for timing and coordinates.

The engine may return an ordered `RecordingIds` list for route playback, but that list is an output of tree/path analysis, not the primary source of truth.

**Algorithm:**

```
Walk the committed tree's source path chronologically:
  Find each completed DockingPort RouteConnectionWindow:
    Verify the tree has a dock branch and a later undock/split boundary for the same window
    stop.endpoint = {
        body, lat, lon from window.EndpointAtDock or the connection-time trajectory point,
        vesselPersistentId = TransferTargetVesselPid,
        connectionKind = TransferKind,
        isSurface = connected endpoint vessel situation is Landed/Splashed/Prelaunch
    }
    transportBefore = resource/inventory manifest for the original transport part PID set
                      captured immediately before docking merge
    transportAfter = resource/inventory manifest for the same transport part PID set
                     captured immediately after undock/separation
    endpointBefore = resource/inventory manifest for the original endpoint part PID set
                     captured immediately before docking merge
    endpointAfter = resource/inventory manifest for the same endpoint part PID set
                    captured immediately after undock/separation
    transportLoss = positive deltas from transportBefore - transportAfter
    transportGain = positive deltas from transportAfter - transportBefore
    endpointGain = positive deltas from endpointAfter - endpointBefore
    endpointLoss = positive deltas from endpointBefore - endpointAfter
    if any non-ignored transportGain or endpointLoss exists:
        reject candidate as pickup/mixed-transfer window in v1
    stop.deliveryManifest = per-resource min(transportLoss, endpointGain)
                            (delivery proof is connection-scoped, not merged-vessel aggregate)
    stop.inventoryDeliveryManifest = exact InventoryPayloadItem snapshots matched between
                                     transport loss and endpoint gain by identityHash
    stop.deliveryOffsetSeconds = delivery boundary UT - first source recording StartUT

  origin = KSC launch site, or non-KSC origin depot proven by a start-docked origin vessel
  orderedRecordingIds = route playback path through the tree
  sourceRefs = immutable source version/fingerprint refs for every recording used by the route
  totalTransit = last source recording EndUT - first source recording StartUT
  costManifest = source resource quantities used or delivered over the full run
  inventoryCostManifest = exact inventory payload snapshots used or delivered over the full run
```

**Derived values:**
- **Origin** = KSC launch site, or a non-KSC origin depot vessel if the Supply Run starts docked to that depot and records its PID
- **Stops** = the one complete dock-transfer-undock delivery window accepted by v1; multiple delivery windows make the candidate ineligible
- **Origin vessel PID** = `0` for KSC routes; otherwise the start-docked origin depot vessel PID
- **Stop vessel PID** = `TransferTargetVesselPid` from the stock connection boundary
- **IsKscOrigin** = true if origin body is Kerbin and coordinates are near a launch site
- **DeliveryManifest** = matched resource amount that both left the transport part set and appeared on the endpoint part set across the dock/undock window
- **InventoryDeliveryManifest** = exact stored-part payload snapshots that both left the transport part set and appeared on the endpoint part set across the same window
- **CostManifest** = source resource quantities used or delivered over the full Supply Run
- **InventoryCostManifest** = exact stored-part payload snapshots used or delivered over the full Supply Run
- **KscDispatchFundsCost** = dry stock part cost plus stock resource/inventory cost for used-or-delivered quantities on Career KSC-origin routes
- **TransitDuration** = total source path duration (last source recording EndUT minus first source recording StartUT)
- **DeliveryOffsetSeconds** = per-stop serialized scheduler offset from cycle start to the delivery boundary; live recording duration changes never move this boundary
- **DispatchInterval** = TransitDuration (default; player can increase). For inter-body routes: defaults to synodic period of origin and destination bodies.
- **DispatchWindowEpochUT** = first recording StartUT; inter-body repeats stay phase-aligned to this UT
- **DispatchWindowPeriod** = 0 for same-body routes, synodic period for inter-body routes
- **RecordingIds** = ordered route source path recording IDs, derived from the committed tree
- **SourceRefs** = immutable source recording fingerprints used to detect deletion, optimizer rewrites, and superseded recordings after route creation

**Start-docked non-KSC origin detection:** A non-KSC route is valid only when the first source recording starts while the transport is already connected to a real origin depot. The recorder must capture the connected origin vessel PID, connection kind, origin part PID set, and transport-scoped start/end manifests without requiring a `DockUT` inside the Supply Run. If the run starts undocked away from KSC, or the start-docked vessel is a ghost/EVA/debris/invalid cargo owner, route analysis rejects the candidate instead of debiting an arbitrary nearby vessel.

**Cost calculation:**

For v1, `CostManifest` is the positive decrease in the source transport's non-ignored resources over the full Supply Run, computed over the original transport part PID set rather than the aggregate docked vessel:

```
CostManifest[resource] = max(0, startTransportResources[resource] - endTransportResources[resource])
```

This includes delivered cargo plus transit resources consumed by the flown mission. `InventoryCostManifest` uses the same principle for exact stock stored-part payload snapshots that leave the transport by the end of the run.

For KSC-origin Career routes, `KscDispatchFundsCost` is:

```
dry source transport part cost
+ stock resource unitCost * CostManifest amount
+ dry stock part cost for InventoryCostManifest items
```

Dry part cost means the source transport's parts with stored resources emptied and inventory contents excluded, so resource and inventory values are not double-counted. Inventory-contained parts use `InventoryPayloadItem.UnitDryCost` plus `StoredResources` from their captured `STOREDPART` snapshot, so a solar panel variant, a stacked spare wheel, and a resource-filled stored part are costed from the same payload identity that delivery will reconstruct. v1 does not apply recurring recovery credit.

Routes store `CostManifest` and `KscDispatchFundsCost` together for transparency and future re-costing. On KSC-origin routes, `CostManifest` is not physically deducted; `KscDispatchFundsCost` is derived from that same source data. If serialized values disagree after load/revalidation, treat that as a bug and recompute from the source recording rather than honoring the desync.

### 5.3 Validation

The route analysis pass validates the committed tree/path:

1. The committed tree contains route-proof metadata from section 4.10. If not, reject with "recording lacks logistics proof data."
2. At least one completed docking connection window exists (`TransferTargetVesselPid != 0` and `TransferKind == DockingPort`)
3. The tree branch structure has a dock merge and matching later undock/split boundary for that window
4. At least one resource or exact stock stored-part payload both left the original transport part PID set and appeared on the original endpoint part PID set between the dock and undock snapshots
5. The derived source recording path is present, playable, and has source refs/fingerprints that can be revalidated later
6. v1: exactly one delivery window is present. If two or more dock-transfer-undock delivery windows are detected, validation rejects the candidate and reports that multi-stop Supply Routes are deferred
7. v1: no pickup deltas are present in the same connection window. Formally, after EC/IntakeAir filtering, any positive `transportGain` or `endpointLoss` rejects the candidate as pickup or mixed transfer
8. v1: non-KSC origins are eligible only when the recording starts docked to a real origin depot vessel and captures that vessel PID; non-KSC candidates without that proof are rejected

If validation fails, the route confirmation UI shows what's missing (e.g., "Transport must undock from destination to enable route — the endpoint needs to be free for the next cycle").

### 5.4 Player confirmation

Route configuration panel shows derived values: origin, endpoint, delivery manifest, total transit time, origin cost manifest, KSC dispatch funds cost, and connection kind. Player can edit name, dispatch interval, and enable/disable. On confirm, route is created and scheduling begins.

### 5.5 Multi-stop routes

Multi-stop routes are a natural extension but not required for v1. The analysis engine can detect multiple dock-transfer-undock delivery windows and report them in diagnostics, but v1 must reject the route candidate instead of exposing only the first stop. This avoids charging/deducting the full Supply Run cost while delivering only part of the run, and keeps partial-failure semantics out of v1.

**Multi-stop example:**

```
Player flies: KSC -> Base A (dock, deliver 150 LF) -> Base B (dock, deliver spare parts) -> KSC

Future route analysis produces:
  Origin: KSC, Kerbin
  Stop 1: Base A on Mun -- delivers 150 LF, 183 Ox
  Stop 2: Base B on Mun -- delivers spare parts
  Total transit: 2d 4h
  Round-trip: Yes
```

---

## 6. Dispatch and Delivery

### 6.1 Dispatch evaluation

**Trigger:** The route scheduler (`RouteScheduler`) runs each physics frame (or once per second during warp) in all scenes via `RouteOrchestrator`, called from `ParsekScenario.Update`.

**v0 status (items 5+6):** The entrypoint is `RouteOrchestrator.Tick(currentUT)`, called from `ParsekScenario.Update` with a UT-delta accumulator at `RouteOrchestrator.TickIntervalSec` cadence. Dispatch and delivery are end-to-end for KSC-origin single-stop routes: the dispatch-decision chain emits `RouteDispatched` + `RouteCargoDebited` (and `RouteEndpointLost` on resolution failure); when `PendingDeliveryUT` elapses the pre-evaluator delivery hook applies the manifest to the destination vessel (loaded `PartResource` writes or unloaded `ProtoPartResourceSnapshot` writes for resources; `ModuleInventoryPart` slot stores for inventory), debits Career KSC-origin funds via `Funding.Instance`, and emits `RouteCargoDelivered` with actual-vs-requested amounts. Idempotent against ELS replay via `(RouteId, RouteCycleId)`.

**For each route with Status in {Active, WaitingForResources, WaitingForFunds, DestinationFull} and `NextDispatchUT <= currentUT`:**

Routes with Status Paused, EndpointLost, MissingSourceRecording, or SourceChanged are excluded from dispatch evaluation. `InTransit` routes are processed by the UT-driven progression loop before dispatch evaluation.

For `WaitingForResources`, `WaitingForFunds`, and `DestinationFull`, skip evaluation until `NextEligibilityCheckUT == null || NextEligibilityCheckUT <= currentUT`. The skipped guard logs a `VerboseRateLimited` dispatch backoff line. When a wait condition is found, set `NextEligibilityCheckUT = currentUT + 60s` by default. Save/load preserves the value. Route UI actions, resource-store changes, funds ledger changes, endpoint capacity changes, and route revalidation may clear it to force an immediate retry.

`MissingSourceRecording` and `SourceChanged` routes are not retried as normal dispatch candidates. They are revalidated on save load, when the recordings store changes, and from a route UI "Revalidate" action. If every source recording ID resolves and every source ref/fingerprint still matches, the route returns to `Active` and `NextDispatchUT` is recalculated from current UT. If the recordings exist but their route-relevant fingerprint changed, the route stays `SourceChanged` and must be recreated or explicitly reanalyzed from the current source data.

**Step 1: Ignore reserved round-trip fields in v1.** `LinkedRouteId` is serialized for forward compatibility only. v1 dispatch does not check it.

**Step 2: Check source recordings.** Verify every `RecordingIds` entry still resolves to a committed recording and every `SourceRefs` fingerprint still matches. If an ID is missing, set `Status = MissingSourceRecording` and stop. If an ID exists but route-relevant timing/proof data changed, set `Status = SourceChanged` and stop. No matching proof recording means no cargo transfer.

**Step 3: Check destination.** Find the endpoint vessel (section 7). If NO vessel is found -> set `Status = EndpointLost`, stop this dispatch attempt. If the vessel has zero capacity for all delivery resources and inventory items -> set `Status = DestinationFull`, set `NextEligibilityCheckUT`, and do NOT advance `NextDispatchUT`; the same scheduled dispatch becomes eligible as soon as capacity appears. If capacity is available -> proceed.

**Step 4: Check origin.** For non-KSC origins, find the start-docked origin depot vessel (section 7). If no vessel is found -> set `Status = EndpointLost` and stop this dispatch attempt. If the vessel lacks `CostManifest` resources or `InventoryCostManifest` items -> set `Status = WaitingForResources`, set `NextEligibilityCheckUT`, and do NOT advance `NextDispatchUT`. For KSC origins in Career, check that `KscDispatchFundsCost` is affordable under the existing ledger reservation rules. If funds are insufficient, set `Status = WaitingForFunds`, set `NextEligibilityCheckUT`, and do NOT advance `NextDispatchUT`. For KSC origins in Science or Sandbox, skip the funds branch entirely; do not touch `Funding.Instance`.

**Step 5: Dispatch.** Clear `NextEligibilityCheckUT`. Create the ROUTE_DISPATCHED ledger event, then apply the origin debit/funds charge through the route ledger/applier path. Deduct `CostManifest` / `InventoryCostManifest` from non-KSC origin, or charge `KscDispatchFundsCost` for KSC origin in Career. Science and Sandbox KSC origins pay no funds. Set `CurrentCycleStartUT` to the scheduled dispatch UT being processed (`NextDispatchUT`), set `CurrentSegmentIndex = 0`, compute `PendingDeliveryUT = CurrentCycleStartUT + nextStop.DeliveryOffsetSeconds`, and set `PendingStopIndex` to the stop due at that boundary or `-1`. Tell the ghost playback engine to play the first recording in `RecordingIds` when visuals are available. Set `Status = InTransit`. Advance `NextDispatchUT`.

**Per-tick catch-up loop:** `RouteOrchestrator.Tick(currentUT)` must process one route until it reaches a stable state for the current UT: first progress any `InTransit` boundaries due at or before `currentUT`, then if the route is `Active` and `NextDispatchUT <= currentUT`, run dispatch evaluation, then immediately progress any newly dispatched zero/short-duration boundaries that are also due. Repeat until the route is no longer due, enters a waiting/error status, or hits a defensive max-iterations guard with a warning. This is what makes "warp past multiple cycles" deterministic; a single tick may dispatch, deliver, complete, dispatch again, and then stop on an origin/funds/capacity blocker.

### 6.2 UT-driven chain progression

The route scheduler, not ghost playback, is authoritative for route state. `RouteOrchestrator.Tick(currentUT)` processes in-transit routes in all scenes and during time warp:

1. If any source recording is missing, set `Status = MissingSourceRecording`, stop playback if active, and abort without delivery. If a source exists but no longer matches `SourceRefs`, set `Status = SourceChanged`, stop playback if active, and abort without delivery.
2. While `Status == InTransit` and `PendingDeliveryUT <= currentUT`, process the due boundary.
3. If `PendingStopIndex >= 0`, execute delivery for that stop using current endpoint capacity.
4. Advance `CurrentSegmentIndex` to the next 0-based source recording index and compute the next `PendingDeliveryUT = CurrentCycleStartUT + stop.DeliveryOffsetSeconds` / `PendingStopIndex`.
5. If the last source recording boundary was processed, increment `CompletedCycles`, reset `CurrentCycleStartUT = null`, `CurrentSegmentIndex = -1`, `PendingDeliveryUT = null`, and `PendingStopIndex = -1`. If `PauseAfterCurrentCycle` is true, clear it and set `Status = Paused`; otherwise set `Status = Active`.

`OnPlaybackCompleted` remains a visual integration hook only. It can let the route scheduler start the next ghost source recording promptly in flight, but it must not be the only path that advances route state. Save/load and high time warp are handled by the UT-driven tick loop.

Routes do NOT use the per-recording loop toggle. The route scheduler owns all timing and sequencing. Routes and loops are siblings — both use the ghost playback engine, but routes chain multiple trajectories sequentially with delivery logic between segments.

**Visual handoff when entering flight mid-transit:** The scheduler elapsed time is `currentUT - CurrentCycleStartUT`. When visuals become available for an already in-transit route, the route policy resolves that elapsed time to a `RecordingIds` index and a recording-local offset, then starts/seeks ghost playback at that offset. It must not restart the ghost at recording UT 0 after the route has already advanced. If the current cycle has no remaining visible source recording to show, the route simply skips ghost rendering until the next source recording or cycle; delivery state still advances by UT.

### 6.3 Per-stop delivery execution

**Trigger:** `RouteOrchestrator.Tick(currentUT)` reaches a pending stop boundary. In flight, the same moment normally coincides with ghost source-recording completion; outside flight, delivery still happens by UT.

1. **Re-check source recordings.** If any `RecordingIds` entry is missing, set `Status = MissingSourceRecording`, create no delivery event, and abort. If any `SourceRefs` fingerprint no longer matches, set `Status = SourceChanged`, create no delivery event, and abort. No matching proof recording means no cargo transfer, even mid-transit.
2. **Find endpoint vessel** at the route endpoint (section 7). If no vessel is found after dispatch, log a warning and create a ROUTE_DELIVERY_FAILED timeline event. Transit cost has already been paid; no cargo is conjured.
3. **For each resource in the stop's `DeliveryManifest`:** apply to endpoint vessel tanks, clamped to current `maxAmount`. v1 manifests contain positive delivery amounts only. For loaded vessels: pre-check `PartResource.flowState`/`flowMode`, then use `Part.TransferResource()` on the exact target `PartResource`. For unloaded vessels: modify `ProtoPartResourceSnapshot.amount` or its backing `RESOURCE` node through stock snapshot objects, preserving `flowState`/`flowMode`.
4. **Deliver inventory** by reconstructing exact `InventoryPayloadItem.StoredPartSnapshot` payloads into stock `ModuleInventoryPart` slots. Loaded delivery may call `StoreCargoPartAtSlot(ProtoPartSnapshot, slot)` only after logistics has chosen a valid slot/stack target. Items that do not fit remain undelivered and are reported in the route event/log.
5. **Create ROUTE_DELIVERED timeline event and apply through the ledger/applier path.** Record requested and actual amounts so the player can see partial fills instead of silent loss. The persisted event must contain enough target identity and before/after delta data for epoch recomputation or rollback to undo the physical cargo mutation.

### 6.4 Single-delivery execution

For v1, the single-stop route is the only player-facing shape. Delivery executes once when scheduler UT reaches the recorded boundary after `SegmentIndexBefore`.

### 6.5 Per-resource independent delivery

Each delivery resource at each stop is evaluated independently:

```
For each resource in stop.DeliveryManifest:
    capacity = sum of (maxAmount - amount) across all stop vessel tanks for this resource
    deliver = min(manifest amount, capacity)
    actualDelivery[resource] = deliver
```

Origin cost is paid at dispatch time after destination capacity has been checked. Non-KSC origins deduct `CostManifest` / `InventoryCostManifest` from the proven origin depot. KSC-origin Career routes charge `KscDispatchFundsCost` instead of deducting physical cargo from KSC. Science and Sandbox KSC origins dispatch with no funds charge.

Inventory follows the same independent rule at item granularity: deliver what fits, report what did not fit, never create extra slots or abstract storage.

### 6.6 Pause, unpause, and re-target

**Pause:** Player clicks Pause in route UI -> `Status = Paused` for future dispatches. If a dispatch is already `InTransit`, set `PauseAfterCurrentCycle = true` and keep `Status = InTransit` until the cycle finishes. Delivery still executes at the endpoint; after the final boundary is processed, the route transitions to `Paused` instead of `Active`. This matches stock expectations: the supply vessel has already launched / departed, so pausing the route should not freeze cargo in mid-flight.

**Unpause:** Player clicks Resume → `Status = Active`. If the route is still `InTransit` with `PauseAfterCurrentCycle = true`, Resume clears that flag and the in-flight cycle will finish back to `Active`. Route re-enters dispatch evaluation on next scheduler tick. `NextDispatchUT` is recalculated if stale (advanced to next valid dispatch time from currentUT).

**Cancel current dispatch:** Deferred. If added later, cancellation should be explicit and should not refund already-deducted origin cargo unless the route event model explicitly records a reversible failure.

**Re-target (EndpointLost recovery):** Player selects a new destination vessel in the route UI → endpoint coordinates and vesselPersistentId updated, `Status` transitions from `EndpointLost` to `Active`. Same mechanism for origin re-targeting on non-KSC routes. Re-targeting is an explicit player intent declaration, not automatic proof that the original Supply Run visited the new vessel. The route keeps its recorded delivery and cost manifests, logs the re-target, and the UI should warn that future cycles reuse the proven transport/cargo run against the newly selected endpoint or origin. Automated surface fallback remains deliberately tight; broad endpoint generalization requires this explicit player action.

---

## 7. Endpoint Resolution

### 7.1 Algorithm

`RouteEndpointResolver.SurfaceFallbackRadiusMeters = 50.0` in v1. The radius is deliberately tight: it allows rebuilding a surface base in-place, but avoids treating neighboring pads, rovers, or storage craft as one abstract warehouse. If this proves too restrictive for large surface installations, make it a settings-backed value later rather than silently widening v1 matching.

KSC-origin routes do not resolve an origin vessel: `IsKscOrigin == true` and `Origin.vesselPersistentId == 0` mean "charge/skip KSC funds branch according to game mode." A route stop endpoint or non-KSC origin with `vesselPersistentId == 0` is invalid.

```
ResolveEndpointVessel(endpoint):
    1. Find vessel by endpoint.vesselPersistentId in FlightGlobals.Vessels
       → if found and compatible: return vessel
    2. If not found AND endpoint.isSurface == false:
       → return null (orbital endpoints have no fallback)
    3. If not found AND endpoint.isSurface:
       → scan FlightGlobals.Vessels for the nearest compatible vessel within
         RouteEndpointResolver.SurfaceFallbackRadiusMeters
         of (body, lat, lon, alt)
       → return that vessel, or null
```

### 7.2 Surface vs orbital behavior

- **Surface endpoints:** PID primary, single nearest compatible-vessel fallback within `SurfaceFallbackRadiusMeters`. `isSurface` is captured from the endpoint vessel situation (`Landed`, `Splashed`, or `Prelaunch`), not altitude, so Mun/Minmus surface bases remain surface endpoints. Handles base rebuilding without turning every nearby vessel into one abstract warehouse.
- **Orbital/in-flight endpoints:** PID only. `Flying`, `Sub-orbital`, `Orbiting`, and `Escaping` connection targets are treated as non-surface in v1. Orbital and in-flight coordinates change continuously, so proximity fallback does not work. If the vessel is destroyed and rebuilt, the player must re-target the route.

### 7.3 Compatible vessel definition

A compatible endpoint or origin fallback vessel must be a real stock vessel, not a Parsek ghost/map-presence vessel, and must be a vessel type that can plausibly own cargo. Exclude vessels in `GhostMapPresence` tracking sets and stock `VesselType` values such as `EVA`, `Flag`, `Debris`, `SpaceObject`, and `Unknown`. For destination resolution, the vessel must contain at least one eligible tank for a delivered resource or one `ModuleInventoryPart` that can store a delivered payload type, regardless of current free capacity; full-but-compatible destinations are handled by the destination-capacity check, not treated as lost endpoints. For non-KSC origin resolution, the vessel must contain at least one eligible store for the route cost manifest. If several compatible vessels remain within the surface fallback radius, choose the nearest one only.

### 7.4 Loaded vs unloaded vessels

If the endpoint or origin vessel is loaded (player is within physics range), use stock `Part.TransferResource()` for endpoint tank mutation after explicit logistics eligibility checks. `Part.RequestResource()` is a resource-flow system API, not the stock PAW tank-transfer primitive, and should not be used for deterministic endpoint edits. If unloaded, use `ProtoPartResourceSnapshot.amount` / the backing `RESOURCE` node directly through snapshot objects. Both paths apply to origin deduction and destination delivery.

Inventory delivery also has loaded and unloaded paths. For loaded vessels, use stock `ModuleInventoryPart` APIs to add reconstructed `STOREDPART` payloads after logistics validates slot limits, stack capacity, volume, mass, variant, and exact payload identity. For origin debit, remove or decrement the exact matching stored slot; do not use stock part-name-only removal helpers. For unloaded vessels, edit the relevant `ProtoPartModuleSnapshot` / `STOREDPARTS` ConfigNodes directly using the stored `InventoryPayloadItem.StoredPartSnapshot`, then update slot accounting. Both paths preserve the exact payload identity hash and report items that do not fit.

---

## 8. Transfer Window Scheduling

### 8.1 Synodic period computation

```
SynodicPeriod(originBody, destBody):
    if originBody == destBody:
        return 0  // same body, no transfer window
    if originBody is Sun or destBody is Sun:
        return 0  // no stable parent orbit to compare
    // Walk up to common parent
    a = originBody, b = destBody
    while hierarchy depth of a > hierarchy depth of b: a = a.referenceBody
    while hierarchy depth of b > hierarchy depth of a: b = b.referenceBody
    while a.referenceBody != b.referenceBody:
        a = a.referenceBody
        b = b.referenceBody
    // a and b now orbit the same parent
    T1 = a.orbit.period, T2 = b.orbit.period
    if T1 == T2: return 0
    return abs(1 / (1/T1 - 1/T2))
```

Handles cross-system routes: Mun→Laythe walks up to Kerbin/Jool orbiting Sun, then uses the Kerbin/Jool synodic period. Guards routes directly to/from the Sun and equal-period edge cases.

### 8.2 Dispatch interval rules

- **Same-body routes:** Player-set interval. Minimum = recording duration.
- **Inter-body routes:** Default = synodic period, phase-anchored to the original Supply Run start UT (`DispatchWindowEpochUT`). `NextDispatchUT` is always the smallest `DispatchWindowEpochUT + n * DispatchWindowPeriod` that is >= current UT and also respects the player's minimum interval.
- **Player interval override:** Player can increase the minimum spacing between dispatches, but v1 does not shift the transfer-window phase. A later advanced override may allow phase shifting explicitly.
- **Gravity assist routes:** Two-body synodic approximation (intermediate flybys not tracked). Player can fine-tune by increasing minimum spacing, not by changing the phase anchor in v1.

---

## 9. Round-Trip Linking (Future)

Round-trip linking is not v1 implementation scope. In v1, `LinkedRouteId` is a reserved serialization field only: route creation does not expose linking UI, dispatch ignores `LinkedRouteId`, and tests only verify that the field round-trips without changing behavior.

Future design intent:

- A round trip remains two separate one-way Supply Routes, each created from its own Supply Run.
- A later UI may link two routes as a pair so they alternate dispatch eligibility: Route A completes -> Route B may dispatch -> Route B completes -> Route A may dispatch.
- Future dispatch rules may wait while the linked partner is `InTransit`. Pausing a partner should not block the other unless that future design explicitly changes the rule.

---

## 10. Edge Cases

### 10.1 Destination destroyed, surface base
**Scenario:** Mun base destroyed. Player rebuilds at same spot.
**Behavior:** PID match fails. Surface proximity fallback finds new vessel within `SurfaceFallbackRadiusMeters`. Route auto-reconnects.
**v1 limitation:** If rebuilt outside `SurfaceFallbackRadiusMeters`, player must explicitly re-target or create a new route.

### 10.2 Destination destroyed, orbital station
**Scenario:** Kerbin station destroyed. Player rebuilds.
**Behavior:** PID match fails. No proximity fallback. `Status = EndpointLost`. Player must re-target.

### 10.3 Origin destroyed or recovered (non-KSC)
**Scenario:** Minmus base recovered for funds.
**Behavior:** Applies only to non-KSC routes whose Supply Run started docked to an origin depot. No vessels at origin → `Status = EndpointLost`. Vessels exist but empty → `Status = WaitingForResources`. Route persists. Resumes when resources appear. Surface origins have proximity fallback. KSC origins skip this check.

### 10.4 Destination tanks full
**Scenario:** Route delivers 200 LF. Base has 200/200 LF.
**Behavior:** `Status = DestinationFull`. No origin deduction, no delivery, no ROUTE_DISPATCHED event, and `NextDispatchUT` is not advanced. The ghost still flies the route so the world looks busy (a route that just stopped spawning ghosts reads as broken); it is purely visual this cycle and moves/charges nothing. The route status text names the blocking reason. Set `NextEligibilityCheckUT` for a rate-limited retry. The same scheduled dispatch resumes when player use or another stock process creates capacity. `SkippedCycles` is not incremented for repeated capacity-poll attempts against the same scheduled dispatch.

### 10.5 Destination partially full
**Scenario:** Base has room for 100 LF, full on Ox. Delivery: 150 LF + 183 Ox.
**Behavior:** Per-resource independent: deliver 100 LF, 0 Ox. Origin pays the CostManifest / KSC dispatch cost (transit already happened). ROUTE_DELIVERED records requested vs actual amounts.

### 10.6 Player reverts past a dispatch
**Scenario:** Route dispatched at UT=50000. Player reverts to UT=49000.
**Behavior:** Stock KSP revert/load restores route state and stock vessels from `.sfs`. Parsek timeline rewinds require the route ledger modules: ROUTE_DISPATCHED and ROUTE_DELIVERED entries participate in epoch isolation and tombstone invalidation, and the route cargo/funds modules reverse or mask invalidated origin debits, KSC charges, and endpoint deliveries. If those modules are not implemented for a mutation path, that route effect path must stay disabled.

### 10.7 Time warp past multiple cycles
**Scenario:** Three cycles due at UT=50000, 50500, 51000.
**Behavior:** The per-tick catch-up loop alternates in-transit progression and dispatch evaluation until the route is no longer due or hits a blocker. All due cycles are processed sequentially in one deterministic route order. Each dispatch checks destination capacity and origin affordability independently. First may deplete origin or fill destination, blocking subsequent cycles without advancing the blocked `NextDispatchUT`.

### 10.8 Transport still docked at recording end
**Scenario:** Player forgets to undock.
**Behavior:** Validation fails. "Create Supply Route" absent. Tooltip: "Transport must undock from destination."

### 10.9 No cargo transferred during connection
**Scenario:** Player docks and undocks without transferring delivery cargo.
**Behavior:** Validation fails. Tooltip: "No resource or inventory transfer detected."

### 10.10 Multiple connection windows in one recording chain
**Scenario:** Supply Run has dock-transfer-undock-dock-transfer-undock.
**Behavior:** Validation fails for v1. UI explains that multiple delivery stops were detected and multi-stop route execution is deferred. The player can re-fly or split the mission into one-stop Supply Runs.

### 10.11 Route dispatch while player is at destination
**Scenario:** Player at Mun base when delivery arrives.
**Behavior:** Destination loaded. Uses `Part.TransferResource()` on eligible endpoint tanks. Resources appear in real-time.

### 10.12 Competing routes at same origin
**Scenario:** Two routes share Minmus base. Base has enough for one, not both.
**Behavior:** v1 — FIFO by NextDispatchUT. Future: player-configurable priority.

### 10.13 Concurrent deliveries to same destination
**Scenario:** Two routes deliver to the same base on the same scheduler tick. The destination can accept one delivery but not both.
**Behavior:** v1 processes route events in deterministic FIFO order: `NextDispatchUT`, then route `Id` as a stable tie-breaker. Each route recomputes capacity immediately before its own delivery. The first route may fill the destination, causing the later route to partially deliver or report undelivered cargo. v1 does not reserve shared destination capacity across routes.

### 10.14 Linked route partner paused (future)
**Scenario:** Route A and B linked. Player pauses B.
**Behavior:** A dispatches on its own schedule. B resumes from its next scheduled dispatch when unpaused.

### 10.15 Source recording missing
**Scenario:** Source recording for a route is deleted or fails to load.
**Behavior:** `Status = MissingSourceRecording`. Route cannot dispatch and cargo transfers stop. UI explains that the proof Supply Run is gone and the route must be recreated or the recording restored. If the recording is restored by loading/reverting to a save where it exists, or by restoring recording sidecars, route load/revalidation clears the status back to `Active` and recalculates `NextDispatchUT`.

### 10.16 Source recording changed or superseded
**Scenario:** Source recording IDs still exist, but optimizer rewrite, re-fly supersede, sidecar epoch change, or route-proof metadata rewrite changes route timing or proof data after route creation.
**Behavior:** `Status = SourceChanged`. Route cannot dispatch and in-transit delivery aborts before moving cargo. UI explains that the proof Supply Run changed and the route must be recreated or explicitly reanalyzed. Stored `TransitDuration` and `DeliveryOffsetSeconds` remain unchanged for diagnosis, but they are not used to keep moving cargo against changed proof data.

### 10.17 Save/load round-trip
**Scenario:** Save, load.
**Behavior:** All Route fields serialized in ParsekScenario OnSave/OnLoad. State restored exactly.

---

## 11. v1 Limitations

- **Capacity changes during transit:** Delivery clamps to `maxAmount` at delivery time. Excess is not delivered; ROUTE_DELIVERED records requested vs actual amounts. Origin was already deducted at dispatch.
- **Zero transit duration:** Dispatch and delivery may process in same frame. Acceptable.
- **EC-only delivery:** ElectricCharge remains excluded from route manifests as environmental noise, matching Phase 11 resource snapshot rules. EC-only Supply Runs are not route-eligible in v1.
- **Resource not on destination:** If delivery manifest includes a resource the destination has no tanks for, that resource is reported as undelivered, not silently skipped.
- **Concurrent endpoint production/consumption during route creation:** Delivery proof uses the conservative `min(transportLoss, endpointGain)` over the dock/undock window. If a third vessel, ISRU, converter, or drain changes the endpoint resource during that same window, route analysis may under-credit or reject the candidate rather than trying to attribute multiple simultaneous sources and sinks. v1 expects one meaningful cargo transfer per connection window.
- **Origin loaded vs unloaded:** Same loaded/unloaded distinction as delivery.
- **Scene handling:** Route scheduler runs in all scenes via ParsekScenario. `FlightGlobals.Vessels` available for endpoint resolution.
- **Revert mechanism:** Route state serialized in .sfs. Quicksave load restores Route ConfigNode and stock vessels. Parsek timeline rewinds require explicit route ledger modules for every enabled route mutation path.
- **Inventory delivery:** Inventory items delivered to destination cargo slots. If destination lacks available slots or the part type doesn't fit, excess items are reported as undelivered.
- **v1 scheduler shape:** `CurrentCycleStartUT`, `CurrentSegmentIndex`, `PendingDeliveryUT`, and `PendingStopIndex` are serialized now for chain-sequential execution and future multi-stop compatibility. In v1 there is only one delivery stop; the fields are mostly visual sequencing plus the single delivery boundary, not a commitment to multi-stop route behavior.
- **Crew delivery:** Deferred. No generic kerbal generation in v1.
- **Route analysis edge cases:** The route analysis engine walks docking windows linearly. Complex patterns (dock to A, undock from A, dock to A again) are detected as separate candidate windows and rejected for v1. A window with no delivery cargo change is not route-eligible.

---

## 12. What Doesn't Change

- **Recording runtime behavior** — recording behavior is unchanged. The Recording schema is additively extended for logistics capture (§4.9): connection-scoped dock/undock manifests, stock connection target metadata, endpoint situation, and origin-dock proof. Logistics v1 reads those fields but never writes to recordings.
- **Ghost playback engine** — no execution changes to GhostPlaybackEngine, IPlaybackTrajectory, or IGhostPositioner. The route scheduler uses the same playback engine as loops for visuals, but route state advances from UT-driven scheduler ticks rather than depending on playback-completed events.
- **Loop system** — per-recording loop toggle, timing, cycle events all work as today. Routes do not use the loop system — they are siblings, not built on top of it. Both use the ghost playback engine, but through different scheduling paths.
- **Chain semantics** — dock/undock/split/merge gameplay semantics do not change. Logistics adds route-proof metadata to the existing always-tree boundaries, but it does not make the chain system responsible for dispatch or delivery.
- **Manifest capture systems** — existing `ExtractResourceManifest`, lightweight inventory manifests, and crew manifests remain available for diagnostics. Logistics adds exact inventory payloads and connection-scoped dock/undock manifests as additive fields. Logistics v1 consumes resources/inventory and connection metadata read-only after capture.
- **Merge dialog** — route creation may be offered after commit/merge when analysis finds an eligible Supply Run, but the merge semantics themselves do not change.
- **Crew reservation** — not touched in v1. Crew logistics is deferred until it can use named roster/crew-reservation semantics instead of generic kerbal generation.
- **Game actions system** — route dispatch/delivery events are new event types in the existing ledger, with route-specific modules for KSC funds, non-KSC origin debit, and endpoint delivery reversal/recompute. KSC-origin Career dispatch costs use existing funds-reservation checks, but physical cargo mutation is not enabled until the route modules prove epoch isolation for loaded and unloaded vessels.
- **Map markers** — deferred. No map view integration in v1.

---

## 13. Module Architecture

The logistics route system follows the same module pattern as the game actions system (v0.6): a self-contained directory with a thin orchestrator connecting it to Parsek's lifecycle hooks. The route module is removable by deleting the directory and removing the orchestrator calls — no behavioral changes to recording, playback, or the game actions system.

### 13.1 Directory structure

```
Source/Parsek/Logistics/
    Route.cs                    // data model (Route, RouteStop, RouteEndpoint, RouteStatus)
    RouteStore.cs               // static storage surviving scene changes (like RecordingStore)
    RouteScheduler.cs           // dispatch/delivery evaluation + chain-sequential playback (pure logic)
    RouteDelivery.cs            // resource and inventory mutation on loaded/unloaded vessels
    RouteEndpointResolver.cs    // vessel finding by PID + surface proximity fallback
    RouteManifestComputer.cs    // derive delivery/cost manifests from recording tree resources/inventory
    RouteAnalysisEngine.cs      // tree/path walk + connection-window extraction from committed recordings
    RouteOrchestrator.cs        // thin integration layer -- called from ParsekScenario hooks
```

### 13.2 Integration seams (5 total)

These are the only places where logistics code touches existing Parsek code:

| Seam | Where | What | How to guard |
|------|-------|------|-------------|
| **Save/Load** | `ParsekScenario.OnSave`/`OnLoad` | `RouteOrchestrator.OnSave(node)`/`OnLoad(node)` | Null-check. Missing ROUTES node = no routes. |
| **Scheduler tick** | `ParsekScenario.Update` | `RouteOrchestrator.Tick(currentUT)` | Single call, no-op if no active routes. |
| **Playback start/seek** | `ParsekPlaybackPolicy` / ghost playback start path | `RouteOrchestrator.RequestVisualPlayback(recordingId, recordingLocalOffsetUT)` | Visual-only. Used for dispatch visuals and mid-transit scene entry; no route state authority. |
| **Playback completed** | `ParsekPlaybackPolicy.HandlePlaybackCompleted` | `RouteOrchestrator.OnVisualSegmentCompleted(evt)` | Visual hint only. Route state also advances from UT-driven scheduler ticks. |
| **Timeline events** | `Ledger` / `LedgerOrchestrator` | New `GameActionType` entries for ROUTE_DISPATCHED / ROUTE_DELIVERED plus route cargo/funds modules | Additive enum values, display strings, and recompute/rollback coverage before stock mutation. |

No route-state changes to `GhostPlaybackEngine`, `RecordingStore`, `RecordingTree`, `ChainSegmentManager`, or `RecordingOptimizer`. Recording metadata changes are additive: logistics capture adds connection-scoped dock/undock manifests and origin-dock metadata for route analysis, and the tree codec persists those fields. The playback start/seek seam is visual only; if the existing ghost start path needs an offset parameter or adapter, that change must not make ghost playback authoritative for delivery. `GhostPlaybackEngine` continues to emit `PlaybackCompleted` at the same point it does today; only `ParsekPlaybackPolicy` / `RouteOrchestrator` interpret that event as a visual hint instead of an authoritative route-state transition.

### 13.3 Read-only consumption of recording data

The route module is a read-only consumer of recording data. It reads:

- `RecordingTree.Recordings` / `RecordingTree.BranchPoints` -- the always-tree source graph for the Supply Run
- `rec.StartResources` / `rec.EndResources` -- resource manifests (Phase 11)
- `rec.StartInventory` / `rec.EndInventory` -- lightweight inventory manifests (Phase 11 diagnostics)
- `rec.RouteConnectionWindows` -- transport/endpoint part PID sets and connection-scoped dock/undock resource/inventory manifests
- `RouteConnectionWindow.EndpointAtDock`, `TransferTargetVesselPid`, and `TransferKind` -- stock connection target identification
- `RouteConnectionWindow.TransferEndpointSituation` and route origin proof -- surface/orbit classification and non-KSC origin proof
- `rec.StartBodyName`, `rec.LaunchSiteName`, and trajectory points -- location context. Recording-level start latitude/longitude fields do not exist today; route coordinates come from selected `TrajectoryPoint`s or `RouteConnectionWindow.EndpointAtDock`.

The route module never writes to Recording objects. It creates Route objects in its own `RouteStore`, serialized in its own `ROUTES` ConfigNode section inside `ParsekScenario`.

### 13.4 Resource modification path

Resource delivery modifies vessels that are not part of the recording system — they are real KSP vessels at endpoint locations:

```
RouteDelivery.DeliverResources(endpointVessel, deliveryManifest)
    if loaded:    targetPart.TransferResource(resource, +amount, sourcePart)
    if unloaded:  protoPartResource.amount += amount
```

Loaded origin debits use the same primitive with a negative amount on the proven origin resource. Logistics pre-checks flow state/mode for both signs because stock PAW transfer does so before calling the primitive. This is independent of Parsek's recording/playback/ghost systems, but not independent of the ledger. Every enabled mutation path must be driven by a route event that records target vessel identity, route id, cycle, resource/item amounts requested and actually applied, and enough before/after information for recalculation or rollback.

The v0 skeleton ships a single `RouteModule` (registered at `RecalculationEngine.ModuleTier.SecondTier`, after `FundsModule`) that observes every route-scoped action type — `RouteDispatched`, `RouteCargoDebited`, `RouteCargoDelivered`, `RoutePaused`, `RouteEndpointLost` — and tracks per-route walk state. The module is observation-only: it does not yet apply KSC funds charges, debit origin resource/inventory, deliver endpoint payloads, or consult `Affordable`. As of item 5 the dispatch scheduler emits `RouteDispatched` / `RouteCargoDebited` / `RouteEndpointLost` rows; the module still observes them without mutating, and the remaining mutations (funds debit, origin/endpoint cargo, `RouteCargoDelivered`) land with item 6. A future phase may split `RouteModule` into separate KSC-funds / origin-debit / endpoint-delivery modules if separation of concerns demands it; the action-type vocabulary already keeps that option open.

If a module cannot find or safely reverse its target during epoch recomputation, it must mark the route/effect invalid and log a warning rather than silently leaving duplicated cargo behind.

### 13.5 Lifecycle isolation

Routes have their own lifecycle, independent of recordings:

- **Creation:** from a committed Supply Run after player confirmation, but the route is a separate entity with its own GUID.
- **Persistence:** own ConfigNode section (`ROUTES` in ParsekScenario), not part of recording metadata.
- **Deletion:** deleting a route does not affect its source recordings. Deleting a source recording disables the route (`MissingSourceRecording`); cargo transfers do not continue without the proof run.
- **Revert:** route state is serialized in .sfs - quicksave/load restores it. Timeline events use explicit route ledger modules for epoch isolation. Loading/reverting to a save where a missing source recording exists again revalidates `MissingSourceRecording` routes and restores them to `Active`; loading/reverting to a save where source IDs exist but fingerprints differ leaves the route `SourceChanged`.

### 13.6 Why not a separate assembly now

The roadmap defers assembly extraction to the future standalone ghost-playback boundary. For Logistics / Supply Routes, a directory-level module within `Parsek.csproj` is the right granularity. Routes need direct access to `Recording.StartResources`/`EndResources`, `RecordingStore.CommittedTrees`, `RecordingTree.BranchPoints`, `Ledger`, and `ParsekScenario` lifecycle. Cross-assembly access would require making all of these `public` or adding an interface layer — friction without benefit. If standalone ghost playback extraction happens, routes stay in Parsek (they are Parsek policy, not ghost playback).

---

## 14. Backward Compatibility

- **Saves without routes:** Load fine. ROUTE ConfigNode absent.
- **Saves without resource manifests:** Load fine. Route creation unavailable for old recordings (no manifest data).
- **Old recordings:** Cannot become routes. Player must re-fly to create a recording with manifests.
- **Format:** Route data is additive in its own `ROUTES` node. Missing node = no routes. Recording-side route-proof metadata should use missing-node defaults where possible, but the implementation must follow the current recording codec/schema policy. If `RecordingTreeRecordCodec` requires a schema generation bump for persisted recording metadata, bump it and add format-gate tests.
- **Reserved v1 fields:** v1 serializers preserve the forward-compatible route shape (`Stops`, `LinkedRouteId`, inventory manifests, `KscDispatchFundsCost`, `CurrentCycleStartUT`, pending transit fields). Non-null defaults are written; nullable fields use the omission-means-null convention from §4.8 and must restore as null. Skipping reserved fields turns future multi-stop/round-trip work into a save migration instead of an additive load.

---

## 15. Diagnostic Logging

### 15.1 Route creation
- `[Parsek][INFO][Route] Route created: id={id} name={name} recordings={count} endpoint={body}({lat},{lon}) connection={kind} cost={manifest} kscFunds={funds}`
- `[Parsek][INFO][Route] Route endpoint: {body}({lat},{lon}) vesselPersistentId={pid} delivery={manifest} inventory={manifest}`
- `[Parsek][INFO][Route] Route analysis: chain={count} recordings, deliveryWindows={count}, acceptedStops={count}`
- `[Parsek][INFO][Route] Route validation failed for chain: {reason}`

### 15.2 Dispatch evaluation
- `[Parsek][VERBOSE][Route] Dispatch check: route={name} nextUT={ut} currentUT={ut} status={status}`
- `[Parsek][INFO][Route] Dispatch: route={name} cycle={n} deducted={amounts} from origin at {body}({lat},{lon})`
- `[Parsek][INFO][Route] Dispatch: route={name} cycle={n} chargedKscFunds={funds}`
- `[Parsek][INFO][Route] Dispatch delayed: route={name} — origin missing {resource}={needed} (available={have})`
- `[Parsek][INFO][Route] Dispatch delayed: route={name} — insufficient KSC funds needed={funds} available={funds}`
- `[Parsek][INFO][Route] Dispatch waiting: route={name} — destination full (capacity={amounts}) nextEligibilityCheckUT={ut}`
- `[Parsek][VERBOSE][Route] Dispatch backoff: route={name} status={status} nextEligibilityCheckUT={ut} currentUT={ut}` (emitted through `VerboseRateLimited`)
- `[Parsek][WARN][Route] Dispatch disabled: route={name} — missing source recording id={id}`
- `[Parsek][WARN][Route] Dispatch disabled: route={name} — source recording changed id={id} expectedProof={hash} actualProof={hash}`
- `[Parsek][WARN][Route] In-transit delivery aborted: route={name} — missing source recording id={id}`
- `[Parsek][WARN][Route] In-transit delivery aborted: route={name} — source recording changed id={id}`

### 15.3 Delivery
- `[Parsek][INFO][Route] Delivery: route={name} cycle={n} requested={amounts} actual={amounts} endpointPid={pid} at {body}({lat},{lon})`
- `[Parsek][INFO][Route] Partial delivery: route={name} — {resource} delivered {actual}/{requested}`
- `[Parsek][WARN][Route] Delivery failed: route={name} — no vessels at destination`
- `[Parsek][INFO][Route] Route ledger apply: event={id} type={ROUTE_DISPATCHED|ROUTE_DELIVERED} route={name} targetPid={pid} delta={amounts}`
- `[Parsek][WARN][Route] Delivery rollback/recompute failed: route={name} event={id} targetPid={pid} reason={reason}`

### 15.4 Endpoint resolution
- `[Parsek][VERBOSE][Route] Endpoint resolve: {type} pid={pid} found={bool} fallback={nearest-compatible/none} candidates={count}`

### 15.5 State transitions
- `[Parsek][INFO][Route] Status change: route={name} {old} → {new}`

### 15.6 Timeline events
- `[Parsek][INFO][Route] Timeline event: ROUTE_DISPATCHED route={name} ut={ut}`
- `[Parsek][INFO][Route] Timeline event: ROUTE_DELIVERED route={name} ut={ut} amounts={amounts}`

---

## 16. Test Plan

### 16.1 Unit tests (pure logic, no Unity)

**Implementation-prep contract tests**
- Always-tree single-node commit persists `StartResources` and `EndResources` on the committed root recording. *Catches: manifests trapped only in transient `CaptureAtStop`.*
- `AppendCapturedDataToRecording` / active-recorder flush preserve the documented manifest ownership invariant. *Catches: route analysis seeing trajectory data without matching manifests.*
- Dock merge branch writes route-facing target PID and connection kind. *Catches: always-tree dock candidates with no endpoint identity.*
- Completed dock/undock window serializes transport/endpoint part PID sets and scoped manifests. *Catches: aggregate-vessel false proof.*
- Missing connection-scoped data rejects route creation with "recording lacks logistics proof data." *Catches: accidental prompts for old recordings.*
- Exact `InventoryPayloadItem` extraction treats variant/resource/module-state differences as distinct identities. *Catches: lossy part-name inventory routing.*
- Route origin proof is present only for start-docked non-KSC origins. *Catches: arbitrary nearby-vessel debit.*

**ExtractResourceManifest**
- Empty ConfigNode → empty manifest. *Catches: null deref on missing PART nodes.*
- Single part, one resource → manifest with one entry. *Catches: parsing errors.*
- Multi-part, same resource → amounts summed. *Catches: overwrite instead of accumulate.*
- Multiple resource types → all present. *Catches: single-resource assumption.*
- Missing RESOURCE node → skipped gracefully. *Catches: null deref.*
- Zero-amount resource → included (maxAmount matters). *Catches: filtering out zeros.*

**ComputeDeliveryManifest**
- Normal transfer on connection-scoped part PID sets: transport 200->50 LF and endpoint 0->150 LF -> delivery 150 LF. *Catches: wrong snapshot pair.*
- Docked aggregate vessel unchanged while transport tank decreases and endpoint tank increases -> delivery detected from connection-scoped manifests. *Catches: merged-vessel false negative.*
- Transport tank decreases but endpoint does not retain the cargo -> no delivery manifest entry. *Catches: docked consumption masquerading as delivery.*
- Transport gains or endpoint loses a non-ignored resource/payload during the same window -> v1 candidate rejected. *Catches: pickup/mixed-transfer windows without algorithmic detection.*
- Stored part variant/resources differ -> inventory payload identities do not collapse into one part-name count. *Catches: fungible inventory cargo.*
- LiquidFuel increased across the connection window -> no v1 delivery, candidate rejected as pickup-only. *Catches: accidental pickup route.*
- Mixed delivery and pickup deltas after EC/IntakeAir filtering -> candidate rejected. *Catches: silently dropping pickup cargo.*
- EC and IntakeAir deltas -> ignored. *Catches: environmental noise route creation.*
- No resource/inventory delta -> empty manifest (validation rejects). *Catches: false positive.*

**Route analysis engine**
- Single always-tree dock-transfer-undock window -> one stop extracted. *Catches: no stop found.*
- Two always-tree dock-transfer-undock delivery windows -> validation rejects v1 candidate. *Catches: charging full multi-stop run while delivering one stop.*
- Dock without matching undock/split -> validation fails. *Catches: incomplete pair.*
- No completed route connection windows -> validation fails. *Catches: false acceptance.*
- Docking window with no delivery resource/inventory change -> validation rejects. *Catches: empty do-nothing route.*
- Resource pickup window -> validation rejects. *Catches: accidental negative delivery.*
- Multi-body chain (Kerbin origin, Mun endpoint) -> correct body for endpoint. *Catches: assuming same body.*
- Non-DockingPort connection kind -> validation rejects in v1. *Catches: premature claw/fuel-line support.*
- Mun/Minmus landed endpoint -> `isSurface = true`. *Catches: airless-body altitude misclassification.*
- Non-KSC route without start-docked origin depot PID -> validation rejects. *Catches: unproven origin resource debit.*
- Route analysis stores `SourceRefs` fingerprints and per-stop `DeliveryOffsetSeconds` from source UT boundaries. *Catches: timing tied to mutable recording duration.*
- Source recording re-optimized/superseded after route creation -> revalidation detects fingerprint drift. *Catches: live route silently shifting delivery boundaries.*

**Route validation**
- Dock + delivery transfer + undock -> valid. *Catches: false rejection.*
- Dock + undock, no delivery transfer -> invalid. *Catches: false acceptance.*
- Dock + delivery transfer, no undock -> invalid. *Catches: missing undock check.*
- Non-docking connection -> invalid in v1. *Catches: unsupported producer accepted.*
- No docking connection -> invalid. *Catches: missing connection check.*
- Missing source recording -> route status becomes MissingSourceRecording. *Catches: cargo without proof recording.*
- Missing source recording restored on load/revalidation -> route returns to Active and recomputes NextDispatchUT. *Catches: unrecoverable missing-source status.*
- Source recording exists but proof/timing fingerprint changed -> route status becomes SourceChanged and dispatch stays disabled. *Catches: cargo moving under a rewritten proof run.*

**Chain-sequential playback**
- `CurrentCycleStartUT = null` and `CurrentSegmentIndex = -1` while idle; dispatch sets `CurrentCycleStartUT` and `CurrentSegmentIndex = 0`. *Catches: ambiguous idle/active state.*
- `CurrentSegmentIndex` indexes `RecordingIds`, not trajectory samples or TrackSections. *Catches: off-by-one against the wrong segment model.*
- UT reaches source recording 0 boundary -> scheduler starts source recording 1. *Catches: stuck on first recording.*
- UT reaches last source recording boundary -> cycle count incremented, status returns to Active and clears `CurrentCycleStartUT`. *Catches: stuck InTransit.*
- UT reaches stop boundary -> delivery triggered even without playback event. *Catches: missed delivery during warp/load/non-flight scenes.*
- Enter flight scene halfway through transit -> ghost seeks to scheduler elapsed offset or skips rendering, never restarts at recording UT 0. *Catches: rover-in-the-past visual rewind.*
- Playback completion event without due UT -> visual-only hint, no duplicate delivery. *Catches: event/UT double-processing.*
- Source recording deleted while InTransit -> status becomes MissingSourceRecording before delivery. *Catches: mid-transit cargo without proof recording.*
- Source recording changed while InTransit -> status becomes SourceChanged before delivery. *Catches: mid-transit cargo under rewritten proof data.*
- Pause requested while InTransit -> delivery still executes, then route becomes Paused. *Catches: frozen in-flight cargo or accidental future dispatch.*
- Warp past multiple full intervals -> catch-up loop dispatches/delivers/completes sequentially until current UT is stable or a blocker is reached. *Catches: one-cycle-per-tick behavior.*

**Dispatch evaluation**
- KSC origin in Career, capacity available, funds affordable -> dispatch and funds spending emitted. *Catches: free KSC mass.*
- KSC origin in Career, funds insufficient -> `WaitingForFunds`, `NextDispatchUT` unchanged. *Catches: dispatch without funds or busy skip loop.*
- WaitingForResources/WaitingForFunds/DestinationFull before `NextEligibilityCheckUT` -> no origin/destination scan. *Catches: per-frame idle polling.*
- KSC origin in Science/Sandbox -> dispatch with no funds action and no `Funding.Instance` access. *Catches: career-only cost leaking or null Funding.*
- Non-KSC start-docked origin, sufficient resources/inventory -> dispatch, deducted from origin depot. *Catches: skipping deduction.*
- Non-KSC start-docked origin, insufficient -> delayed. *Catches: dispatching without resources.*
- Destination full -> `NextDispatchUT` unchanged, `NextEligibilityCheckUT` set, origin NOT deducted. *Catches: wasted deduction and delayed retry.*
- Partial capacity -> per-resource independent, full route cost. *Catches: coupled delivery.*
- Non-KSC start-docked origin -> deducts from recorded origin depot, not transport or arbitrary nearby vessel. *Catches: wrong debit identity.*
- Two due deliveries to same destination -> deterministic FIFO (`NextDispatchUT`, then route id), second route sees capacity after first. *Catches: hidden nondeterministic contention.*
- `LinkedRouteId` set in v1 -> ignored by dispatch and no linked-partner log line is emitted. *Catches: accidentally enabling deferred round-trip behavior.*
- ROUTE_DISPATCHED and ROUTE_DELIVERED ledger modules apply and reverse KSC funds, origin debits, and endpoint deliveries in epoch recompute. *Catches: timeline events that cannot undo physical stock mutations.*

**Synodic period**
- Kerbin-Mun → ~6.4 days. *Catches: formula error.*
- Mun-Laythe → walks to Kerbin/Jool and returns a positive Kerbin/Jool synodic period. *Catches: cross-system hierarchy.*
- Kerbin-Duna/Eeloo -> positive Sun-parent synodic period. *Catches: accidentally disabling interplanetary routes.*
- Same body → 0. *Catches: division by zero.*
- Origin or destination is the Sun → 0. *Catches: missing guard without breaking Sun-parent planets.*
- Equal periods → 0. *Catches: T1==T2 division.*
- Inter-body route after pause/load -> next dispatch stays aligned to DispatchWindowEpochUT + n * DispatchWindowPeriod. *Catches: phase drift.*
- Player interval override -> increases minimum spacing without shifting phase. *Catches: window override misinterpreted as free phase shift.*

**KSC cost**
- Dry part cost excludes loaded resources and inventory contents. *Catches: double-counting cargo.*
- Resource cost uses `PartResourceDefinition.unitCost * CostManifest amount`. *Catches: free delivered resources.*
- Inventory delivered part cost includes `InventoryPayloadItem` dry part/module cost and stored resources from the captured `STOREDPART` snapshot. *Catches: free inventory parts.*

**Endpoint resolution**
- PID exists → found. *Catches: skipping PID.*
- KSC-origin route serializes `Origin.vesselPersistentId = 0` and skips origin vessel resolution. *Catches: undefined KSC sentinel.*
- Non-KSC origin with `vesselPersistentId = 0` -> invalid. *Catches: sentinel leaking into physical origins.*
- PID gone, surface, nearest compatible vessel within `SurfaceFallbackRadiusMeters` → fallback. *Catches: missing fallback.*
- PID gone, surface, nothing nearby → empty. *Catches: false match.*
- PID gone, orbital → empty. *Catches: orbital fallback.*
- Multiple surface vessels within fallback radius -> one nearest compatible endpoint, not aggregate warehouse. *Catches: magic-radius transfer.*
- Ghost map vessels, EVA, flags, debris, space objects, and vessels without compatible tanks/slots are excluded. *Catches: wrong fallback target.*

### 16.2 Log assertion tests

- Route creation logs manifest, connection kind, and endpoint. *Catches: silent creation.*
- Dispatch delayed logs missing resource and amounts. *Catches: silent delay.*
- Delivery logs requested vs actual amounts. *Catches: silent partial delivery.*
- Status change logs old→new. *Catches: silent transition.*
- Partial delivery logs reason. *Catches: silent partial.*
- MissingSourceRecording logs the missing source id. *Catches: disabled route with no diagnosis.*
- SourceChanged logs expected and actual source proof/fingerprint values. *Catches: opaque disabled route after optimizer rewrite.*
- Dispatch backoff skip logs through `VerboseRateLimited` when `NextEligibilityCheckUT` has not arrived. *Catches: silent guard skip without per-frame spam.*
- Route ledger recompute/rollback failure logs event id, route id, target PID, and reason. *Catches: epoch isolation failure with no diagnosis.*
- `LinkedRouteId` set in v1 does not log partner checks. *Catches: half-enabled round-trip scheduling.*

### 16.3 Serialization round-trip tests

- Route serialize → deserialize → all fields match. *Catches: missing field.*
- Stops list round-trip with one v1 stop. *Catches: endpoint lost.*
- RecordingIds list round-trip → all IDs preserved in order. *Catches: chain ordering lost.*
- SourceRefs list round-trip preserves route proof hashes, schema generations, sidecar epochs, and source UT bounds. *Catches: SourceChanged detection lost on save/load.*
- Stop `DeliveryOffsetSeconds` round-trips with full precision. *Catches: delivery boundary recomputed from mutable source recordings.*
- ResourceManifest round-trip with full precision. *Catches: locale formatting.*
- InventoryPayloadItem manifests, stored STOREDPART snapshots, and KscDispatchFundsCost round-trip. *Catches: stock cargo/cost data lost.*
- Null LinkedRouteId survives round-trip. *Catches: empty vs null.*
- Null `CurrentCycleStartUT`, `PendingDeliveryUT`, and `NextEligibilityCheckUT` are omitted on save and restore as null. *Catches: sentinel/null mismatch.*
- In-transit route with CurrentCycleStartUT, PendingDeliveryUT, PendingStopIndex, and CurrentSegmentIndex survives. *Catches: transit state lost.*
- Waiting route with NextEligibilityCheckUT survives. *Catches: retry cadence lost.*
- In-transit route with PauseAfterCurrentCycle survives. *Catches: pause request lost across save/load.*
- In-transit route after save/load recomputes actual deliverable capacity at delivery time. *Catches: stale pending amount delivery.*

### 16.4 Integration tests (synthetic recordings)

- Always-tree recording with dock+delivery+undock route window -> route analysis extracts one endpoint. *Catches: tree/path mismatch.*
- Always-tree recording with two delivery docking windows -> validation rejects v1 route creation. *Catches: accidental multi-stop behavior.*
- Always-tree recording with dock but no matching undock/split -> validation rejects. *Catches: missing tree boundary check.*
- Legacy/base-manifest-only recording without route connection windows -> validation rejects with missing proof data. *Catches: false prompts on old saves.*
- Inject route into save -> loads correctly with endpoint, manifests, KSC cost, and source ids. *Catches: ParsekScenario integration.*
- Inject route, supersede one source recording, then reload/revalidate -> route becomes SourceChanged. *Catches: stale route proofs after optimizer rewrite.*
- Rewind/tombstone a ROUTE_DELIVERED event in synthetic ledger state -> endpoint delivery is removed or masked by route module recompute. *Catches: non-recording vessel delta surviving timeline rewind.*

### 16.5 In-game tests (KSP runtime)

These complement the pure-logic visual-handoff tests in §16.1: unit tests prove the scheduler chooses the right recording-local offset, while in-game tests prove the live ghost playback and loaded-vessel stock APIs honor that decision.

- Loaded-vessel resource delivery uses `Part.TransferResource()`, clamps to current tank capacity, and respects `flowState`/`flowMode`. *Catches: unloaded-only implementation passing unit tests and accidental use of the resource-flow API.*
- Unloaded-vessel resource delivery edits proto resource snapshots and survives save/load. *Catches: loaded-only delivery path.*
- Loaded-vessel non-KSC origin deduction removes resources from the proven origin depot. *Catches: dispatch cost mutation hitting the wrong vessel.*
- Unloaded non-KSC origin deduction edits the proven origin depot snapshots and survives save/load. *Catches: dispatch debit only working for loaded vessels.*
- Always-tree dock merge records the other docked vessel PID and endpoint coordinates for both active-as-target and active-as-initiator paths. *Catches: route proof accidentally pointing at the merged vessel, the transport itself, or a destroyed absorbed vessel lookup.*
- Moving the same stock cargo part between two live `ModuleInventoryPart` containers preserves `InventoryPayloadItem.IdentityHash`. *Catches: unknown stock `STOREDPART > PART` fields that need to be excluded from canonical identity.*
- Loaded `ModuleInventoryPart` delivery reconstructs exact `STOREDPART` payloads and respects slot limits. *Catches: inventory slot accounting only working in serialized ConfigNodes.*
- Unloaded inventory delivery edits `STOREDPARTS` ConfigNodes directly and survives scene reload. *Catches: fragile inventory ConfigNode mutation path.*
- Flight-scene entry during an in-transit cycle starts/seeks the route ghost at scheduler elapsed time. *Catches: visual replay from the beginning after scene change.*

---

## 17. Deferred Work

For the prioritized, tiered sequence of these items into a path from v0 to feature-complete, see the "Logistics: remaining work toward feature-complete (post-0.10.0)" subsection under Phase 13 in `docs/roadmap.md`.

- **Record Supply Run helper:** v1 should automatically prompt after eligible committed runs. A helper button may be added later for intent marking or prompt filtering.
- **Non-docking stock connection producers:** claw/grapple and stock crossfeed/fuel-line paths are deferred until docking routes are reliable. They need KSP API investigation for endpoint PID, connection start, connection end, and cargo delta.
- **Pickup routes:** v1 is delivery-only. Resource and inventory pickup routes need separate stock-slot and part-identity tests before exposure.
- **Non-KSC undocked-start origins:** v1 non-KSC routes require the Supply Run to start docked to a real origin depot. Common patterns where a tanker launches from a Minmus surface base, drives/flies away undocked, and later docks to a destination are deferred until origin ownership can be proven without inventing a warehouse.
- **KSC cost tuning:** v1 charges stock-realistic funds for source vessel parts plus used/delivered resources and inventory. The Logistics UI DISPLAYS a recovery-aware net per-run cost (net = gross launch cost minus the actual distance-scaled recovery credits summed over the source tree), shown in Career for KSC-origin routes only. The per-cycle CHARGE now reconciles to this displayed net via a deferred recovery credit (`docs/dev/done/plans/logistics-recovery-credit.md`): the gross launch cost is fronted at dispatch (the dispatch debit and the `KscFundsAvailable` gate stay on gross, so you still front the full build cost), and a `RouteRecoveryCredited` ledger row credits the recovered amount back one dispatch interval later, at the next dock crossing, keyed on the prior dispatched cycle. FundsModule processes the gross debit as a spending and the credit as an earning, so both are reversed through the recalc cutoff walk (rewind) and the tombstone path (re-fly). In steady state the per-cycle net equals the displayed net; the credit is a constant deferred amount, an approximation of each run's physical recovery-landing UT (precise per-run landing with cycle overlap is deferred, plan OQ1).
- **Force dispatch now:** v1 schedules dispatch from `NextDispatchUT` only. A later explicit "Dispatch now" UI action would need its own affordability/capacity checks and would set `CurrentCycleStartUT` from the action UT.
- **Map view integration:** Route lines on the map. Deferred.
- **Dispatch priority for competing routes:** v1 uses FIFO by NextDispatchUT.

### 17.1 Supporting systems required for future work

Getting v0 fuel delivery working required a large stack of NEW systems behind the scenes: route-proof recording metadata + connection-scoped capture (`RouteProofCapture`, `RouteConnectionWindow`, sections 4.9 / 4.10), the route model + store + codec (`Route`, `RouteStore`, `RouteCodec`), the read-only candidate / analysis path (`RouteCandidateFinder`, `RouteAnalysisEngine`), the dispatch scheduler (`RouteOrchestrator`, `RouteDispatchEvaluator`, `RouteLoopClock`), the destination-side delivery writers + capacity probe (`LiveDeliveryWriters`, `LiveDeliveryCapacityProbe`, `RouteDeliveryPlanner`), the endpoint resolver (`RouteEndpointResolver`), the funds path (`RouteFundsCalculator`, `RouteRunCostCalculator`, the Route ledger module + recovery-credit pairing), and the render seam (`RouteBackingMission`, `RouteGhostDriverSelector`). This section surfaces, up front, the NEW supporting systems each REMAINING feature will need that do NOT exist in v0, so the hidden infra cost is visible before committing to a feature. Items are indexed by the Tier 1-4 list in `docs/roadmap.md` (which carries the priority sequence; this section carries the system analysis). Each "Needs" entry is tagged `(shared)` when more than one future feature reuses it, `(feature-specific)` when only this feature needs it, or `(KSP API investigation)` when it needs API spelunking before it can be scoped.

**The shared-foundation leverage points (read this first).** Two foundations sit under several Tier 2 / Tier 3 features. Building each once unlocks multiple features, which changes how the roadmap should be sequenced:

- **A generalized transfer-DIRECTION model.** v0 hardcodes one direction: cargo leaves the transport part set and lands on the endpoint part set, written by `LiveDeliveryWriters` into the destination, gated by `LiveDeliveryCapacityProbe`; `RouteAnalysisEngine.HasResourcePickup` REJECTS any reverse flow. Pickup, mixed pickup/delivery, and round-trip all need the reverse direction (read from a source, with an availability probe instead of a capacity probe). Generalizing capture + analysis + the writer/probe pair to be direction-aware ONCE is the bulk of pickup, and mixed and round-trip then reuse it. `(shared)`
- **A multi-WINDOW route data model.** v0 analyzes exactly ONE `RouteConnectionWindow` per run and a single `Stops` entry; `RouteAnalysisEngine` rejects multi-window runs outright, and the scheduler delivers at one dock phase. Multi-stop (several delivery windows in one run) and round-trip (two runs chained) both need the model, scheduler, and per-window delivery clock to handle an ORDERED set of windows rather than one. The `Stops` list and `LinkedRouteId` fields are already reserved in the save shape for exactly this, so the codec cost is small; the analysis + scheduler + per-window delivery cost is the real work. `(shared)`

**Resequencing implication.** Because the transfer-direction model is shared by pickup, mixed, and round-trip, and the multi-window model is shared by multi-stop and round-trip, it is cheaper to build those two foundations as explicit milestones rather than treat each feature as independent. In particular, round-trip linking is currently in Tier 3 as if it were standalone, but it is mostly "pickup (reverse direction) + a two-window/two-route chain constraint": once pickup (Tier 2) and the multi-window model (built for multi-stop, Tier 3) both exist, round-trip is a thin scheduling layer on top, not a fresh system. Consider building the direction model with pickup, the multi-window model with multi-stop, then doing round-trip immediately after both, ahead of the heavier non-docking-producer and crew-delivery items.

**Tier 1**

- **Map-view route lines:** Needs a route-path map renderer `(feature-specific)`. v0 already renders the ghost as a looped Mission segment via `RouteBackingMission` + `RouteGhostDriverSelector` and the ghost trajectory polyline / orbit lines already exist for missions, so this mostly reuses the existing map render path; the only new piece is selecting and drawing the route's own path layer. Largely reuses v0 systems as-is.
- **"Dispatch now" action:** Needs no new system. Reuses the existing `RouteOrchestrator` dispatch body, `RouteDispatchEvaluator` affordability/capacity checks, and `LiveDeliveryCapacityProbe`; it is a UI action that sets `CurrentCycleStartUT` from the action UT instead of waiting for `NextDispatchUT`. Reuses v0 systems as-is.
- **Dispatch priority for competing routes:** Needs a route-ordering policy `(feature-specific)` that replaces the v0 FIFO-by-`NextDispatchUT` tiebreak in `RouteDispatchEvaluator` with a player-set or value-based comparator, plus a persisted priority field on `Route` (additive codec). Small, self-contained.
- **Candidate intent helper:** Needs a per-tree intent flag `(feature-specific)` consumed by `RouteCandidateFinder` to suppress or confirm detection. The shipped Candidates list already auto-detects, so this is a small additive flag, not a new subsystem.
- **Dock-side-baseline eligibility edge case:** Needs a pre-couple transport snapshot captured inside `onPartCouple` before stock equalisation, fed to the dock-window builder in place of the post-merge snapshot, OR an approximate-equalisation detector in `RouteAnalysisEngine.HasResourcePickup` `(feature-specific)`. A capture-timing fix on the existing `RouteProofCapture` path, not a new subsystem.
- **Precise per-run recovery-landing:** Needs a second recovery clock `(feature-specific)` keyed on each run's recorded recovery UT that maps run K's recovery into whichever cycle it physically lands in, with cycle-overlap bookkeeping, replacing the v0 constant deferred credit in the recovery-credit pairing. The ledger row type and reversal already exist; this refines WHEN the credit lands.

**Tier 2**

- **Pickup routes:** Needs the generalized transfer-DIRECTION model `(shared)` end to end: a source-side transfer capture (the mirror of v0's connection-scoped DELIVERY capture, proving cargo left the ENDPOINT and arrived on the TRANSPORT), a source availability probe (the mirror of `LiveDeliveryCapacityProbe`, "is there cargo to pick up / room on the transport"), and reverse-direction writers (the mirror of `LiveDeliveryWriters`, debiting the endpoint and crediting the origin). `RouteAnalysisEngine` must stop rejecting reverse flow and instead classify it. Also needs the existing exact stock-slot / `InventoryPayloadItem` identity tests extended to the pickup direction `(feature-specific)`. This is the feature that pays for the shared direction model.
- **Mixed pickup/delivery windows:** Needs the direction model from pickup `(shared)`, plus a per-window bidirectional manifest in `RouteConnectionWindow` and a two-direction delivery applier in `RouteOrchestrator` so one dock crossing both drops off and picks up `(feature-specific)`. Hard dependency: pickup must land first.
- **Non-KSC undocked-start origins:** Needs an origin-ownership prover `(feature-specific)` that establishes a real origin depot/vessel without the v0 "must start docked" requirement (the current proof is `RouteOriginProof.StartDockedOriginVesselPid`), without inventing an abstract warehouse. Likely an origin endpoint resolver that mirrors `RouteEndpointResolver` (by-PID + surface fallback) on the ORIGIN side so recurring cargo can be debited from a resolved stock origin vessel. Mostly independent of the two shared foundations.

**Tier 3**

- **Multi-stop routes:** Needs the multi-WINDOW route data model `(shared)`: `RouteAnalysisEngine` must accept and order N delivery windows instead of rejecting multi-window runs, the scheduler / `RouteLoopClock` must fire a delivery at EACH window's dock phase in sequence, and `RouteEndpointResolver` must resolve one endpoint PER stop. The `Stops` list and `SegmentIndexBefore` / `DeliveryOffsetSeconds` fields are already reserved for this, so the codec is cheap; the analysis + multi-phase delivery clock is the real cost. This is the feature that pays for the shared multi-window model.
- **Round-trip linking:** Needs a cross-route chain-constraint scheduler `(feature-specific)` that consumes the already-reserved `LinkedRouteId` so paired routes alternate (A completes, then B dispatches). Per the leverage note, it ALSO needs the reverse-direction model (pickup) and benefits from the multi-window model, so it is cheapest built AFTER pickup and multi-stop, when both shared foundations already exist; on their own the linking layer is thin.
- **Inter-body re-aimed routes:** Needs the existing same-body delivery clock taught to use the Missions re-aim schedule `(feature-specific)`. The seam already exists and is a no-op in v0: `RouteLoopClock` threads the backing unit's `RelaunchSchedule` / `LoiterCuts` (null for same-body) into `GhostPlaybackLogic.TryComputeSpanLoopUT`. Wiring requires the route's backing Mission to carry the locked Missions layer's synodic / re-aim `MissionRelaunchSchedule` (the `bodyInfo` seam) and the delivery clock to phase-lock to the re-aimed launch UTs. No from-scratch transfer-window solver; it activates an existing seam over the locked `MissionPeriodicity` / re-aim layer.
- **Non-docking connection producers (claw/grapple, stock crossfeed/fuel-line):** Needs a connection-producer abstraction `(KSP API investigation)` behind the existing `RouteConnectionKind` enum (today only `DockingPort` is wired through capture and analysis). Each producer needs its own detection of endpoint PID, connection start, connection end, and cargo delta, which requires KSP API spelunking before it can be scoped; `RouteProofCapture` and `RouteAnalysisEngine` would then consume producers through a common interface instead of the hardcoded dock path.
- **Crew delivery:** Needs a named-roster crew transfer system `(feature-specific)` that moves real KSP roster kerbals (not the generic kerbals the v0 manifests model) and integrates with Parsek's crew-reservation system so delivered crew are reserved/named correctly. Largest of the cargo extensions because it cannot reuse the resource/inventory transfer path; depends on the crew-reservation subsystem.

**Tier 4**

- **Scenario-lifecycle test hardening:** Needs a Unity test harness `(shared)` (stubbed `Planetarium.fetch`, a `GameEvents` event-bus mock, a `MonoBehaviour` shim) so xUnit can drive `ParsekScenario.OnSave` / `OnLoad` end to end instead of pinning the route hookups with source-text grep gates. Not logistics-specific: about 120 existing harness-touching tests would benefit, so it is best built alongside the in-flight logging audit rather than charged to route work alone.

---

## 18. Implementation Plan

Implementation should proceed in dependency order. Do not enable the route-creation prompt until phases 1-3 pass their tests.

**Phase 0: code-contract alignment**

- Add failing tests for the actual always-tree manifest and dock metadata contracts listed in section 4.10.
- Confirm exactly where root/child recordings should own start/end manifests after normal commit, scene-exit commit, dock merge, undock split, and optimizer rewrite.
- Add the route timeline action/module contract before any stock resource/funds mutation code lands: explicit ROUTE_DISPATCHED / ROUTE_DELIVERED `GameActionType` values plus recompute modules for KSC funds, physical origin debit, and endpoint delivery.

**Phase 1: recorder prerequisites**

- Persist route-facing dock metadata in always-tree mode (`TransferTargetVesselPid`, `TransferKind`, endpoint situation, endpoint coordinates).
- Add `RouteConnectionWindow` capture/serialization on the docked merged recording.
- Add transport/endpoint part PID set extraction and scoped resource manifests at dock and undock.
- Add exact `InventoryPayloadItem` extraction/serialization while preserving existing lightweight inventory manifests.
- Add non-KSC start-docked origin proof.

**Phase 2: route core model**

- Create `Source/Parsek/Logistics/Route.cs`, route serializers, and `RouteStore`.
- Wire `RouteOrchestrator.OnSave` / `OnLoad` into `ParsekScenario`, no scheduler behavior yet.
- Add route serialization tests for every field, especially nullable UT fields, `SourceRefs`, `DeliveryOffsetSeconds`, and inventory payload snapshots.

**Phase 3: route analysis**

- Implement `RouteAnalysisEngine` over committed `RecordingTree` data and completed `RouteConnectionWindow`s.
- Implement delivery/cost manifest computation from scoped transport/endpoint deltas.
- Persist immutable source fingerprints and pinned stop delivery offsets so route timing cannot drift when recordings are optimized or superseded.
- Reject old/base-manifest-only recordings with a clear missing-proof reason.
- Add synthetic always-tree route fixtures and analysis tests.

**Phase 4: endpoint and delivery primitives**

- Implement endpoint resolution by PID with surface fallback only for surface endpoints.
- Implement resource capacity checks and `Part.TransferResource()` / `ProtoPartResourceSnapshot` mutation for loaded/unloaded vessels.
- Implement inventory fit/delivery only after exact payload reconstruction is proven, including loaded `ModuleInventoryPart` insertion and unloaded `STOREDPARTS` editing.
- Keep physical mutation disabled until Phase 5 ledger modules can apply and reverse the same changes.

**Phase 5: scheduler and timeline effects**

- Implement UT-driven dispatch/delivery state progression.
- Integrate KSC Career dispatch cost, non-KSC origin debit, and endpoint delivery with explicit route ledger modules.
- Implement `SourceChanged` revalidation, DestinationFull backoff without advancing `NextDispatchUT`, and the per-tick catch-up loop that alternates in-transit progression with dispatch evaluation.
- Add rewind/save-load/time-warp tests before any UI prompt creates live routes.

**Phase 6: UI and visual playback**

- Add post-commit route candidate prompt and route confirmation UI.
- Add route management surface in the logistics/recordings UI.
- Add visual playback handoff/seek as a non-authoritative hint only.

**Phase 7: runtime validation**

- Run the headless suite.
- Add/inject synthetic recordings for route creation.
- Run focused in-game tests for loaded-vessel resource delivery, origin debit, exact inventory delivery, and mid-transit scene entry.
- Run focused in-game tests for unloaded resource and inventory delivery, including direct `STOREDPARTS` ConfigNode mutation.
- Capture `KSP.log`, `parsek-test-results.txt`, and log-validation evidence.

---

## Appendix A: Gameplay Scenarios (from Step 2)

### A.1 Fuel Delivery Rover
Base with empty fuel tank near KSC. Rover drives to base, docks, transfers 150 LF through stock resource transfer, undocks, drives clear. Route: 150 LF per cycle, KSC origin with Career dispatch cost, interval = recording duration.

### A.2 Orbital Monoprop Resupply
Kerbin station at 100km. Capsule from KSC: launch, dock, transfer 650 MP, undock, deorbit. Ghost visible during launch and station approach (RELATIVE frame). Transit invisible.

### A.3 Minmus Ore Delivery (chained routes)
Route A: KSC → Minmus base (fuel). Route B: Minmus base → Kerbin depot (ore). B is gated by resource availability at Minmus. A feeds B.

### A.4 Eeloo Supply Run (interplanetary)
Dispatch at Kerbin-Eeloo synodic period (~1.9 years), phase-anchored to the original Supply Run start UT. Gravity assists use the same two-body approximation. Player can increase spacing but v1 does not shift the phase anchor.

### A.5 Failure Cases
Destination destroyed (surface: nearest compatible fallback; orbital: route pauses). Origin empty (delayed). Destination full (waiting, no deduction). Source recording missing or changed (route disabled). Revert (route ledger epoch isolation). Time warp (sequential processing). Transport still docked (validation rejects).

---

## Appendix B: Reference Documents

- `docs/dev/done/plans/phase-11-resource-snapshots.md` — archived Phase 11 plan (base resource snapshots; older route-recording workflow superseded here)
- `docs/dev/research/logistics-network-design.md` — logistics network research
- `docs/dev/research/loop-playback-and-logistics.md` — loop mechanics and orbital drift
- `docs/dev/research/resource-snapshots-preparation.md` — infrastructure analysis and unloaded vessel modification
- `docs/mods-references/` — background resource processing patterns from other KSP mods
- `docs/roadmap.md` — Phase 11, 11.5, 12
