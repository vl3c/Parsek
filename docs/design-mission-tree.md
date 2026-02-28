# Design: Recording Tree (Multi-Vessel Recording)

## Problem

Parsek currently records one vessel at a time. Real KSP missions involve multiple vessels created through undocking, EVA, and staging. When the player reverts, all vessels that existed at the end should be accounted for — spawned at their correct positions, states, and orbits.

## Terminology

**Recording tree**: Parsek's data structure. A tree of recordings produced from a single launch. The root is the launched vessel. The tree branches at undock/EVA events and merges at dock/board events. One launch = one recording tree. Each node in the tree is a recording for one vessel over one time interval.

**KSP mission**: the game's concept. A mission starts at launch and ends when the vessel returns to Kerbin (recovery). Parsek does not model missions — it models recording trees. A single KSP mission may correspond to one path through the recording tree, but the tree can contain many concurrent paths (vessels in orbit, on other bodies, EVA kerbals).

These are distinct concepts. A recording tree can produce multiple ongoing KSP missions (e.g. a Mun lander on one branch, an orbiter on another — each is its own ongoing mission in KSP terms). Parsek only cares about the recording tree.

## Mental model

A recording tree starts as a single root (the launched vessel) and branches every time a vessel splits. Docking merges branches back together.

```
Launch
  │
  ├── Composite vessel (t0 → t1)
  │     undock
  │     ├── Orbit part (t1 → end)          ← leaf: in orbit
  │     └── Remaining vessel (t1 → t2)
  │           undock
  │           ├── Mun lander (t2 → end)    ← leaf: on Mun
  │           └── A+B combined (t2 → t3)
  │                 undock
  │                 ├── A (t3 → t4)
  │                 │     EVA
  │                 │     ├── A unmanned (t4 → end)   ← leaf: drifting
  │                 │     └── Kerbal (t4 → end)       ← leaf: EVA
  │                 └── B (t3 → end)       ← leaf: landed on Kerbin
```

Each **node** is a recording — one vessel over one time interval. **Branch points** are the connections between nodes (split/merge events). Each **leaf** is a recording with no children — a vessel that still exists at revert/commit time.

Docking is the reverse: two branches converge into one. So the structure is technically a **DAG** (directed acyclic graph), not a pure tree. But tree is the common case.

## Recording tree boundary

**One launch = one recording tree.** The tree includes all vessels causally connected to that launch through undock/dock/EVA/board events.

A recording tree does NOT span multiple launches. If the player launches vessel A, goes to Space Center, then launches vessel B which later docks with A — vessel B has its own recording tree. The docked-with-A vessel was already spawned from A's committed recording tree; it's just a game object now, not part of B's tree.

A recording tree ends when the player reverts or commits. At that point:
- All surviving leaf vessels get spawned at their final positions
- Each spawned vessel becomes a normal game object (can be recovered, continued, etc.)
- The recording tree is committed to the timeline for ghost playback

## Definitions

**Recording**: one vessel's recording over one continuous time interval. Contains trajectory points, orbit segments, part events — everything a Recording has today. A recording starts at:
- **Launch** (root of the tree)
- **Undock** (branch — vessel splits into two)
- **EVA** (branch — kerbal leaves vessel)
- **Dock** (merge — two vessels combine, continuation recording starts)
- **Board** (merge — kerbal enters vessel, continuation recording starts)

And ends at:
- **Undock** (this vessel splits — two child recordings start)
- **EVA** (kerbal leaves — two child recordings start)
- **Dock** (two vessels merge — one child recording starts)
- **Board** (kerbal enters vessel — one child recording starts)
- **Destruction** (terminal — no children, no spawn)
- **Recovery** (terminal — no children, no spawn, e.g. StageRecovery mod)
- **Revert/Commit** (terminal — leaf, eligible for spawning)

**NOT a recording boundary:** atmospheric boundary crossings and SOI changes. The current system creates chain segments at these boundaries (`CommitBoundarySplit`). In the tree model, these are **eliminated as split points** — they are absorbed within a single recording using intra-recording orbit segments (which already handle on-rails SOI transitions). An atmospheric boundary crossing or SOI change for the same vessel does not create a branch point (one parent, one child, same vessel = not a branch).

**Branch point**: an event where one vessel becomes two (undock, EVA) or two become one (dock, board). Each branch point references the parent recording(s) and child recording(s).

**Leaf vessel**: a vessel that still exists at revert/commit time. Eligible for spawning. A leaf can be:
- Returned to Kerbin (landed/splashed) — player can recover via KSP native tools after spawn
- In orbit — stays as an active vessel in the game
- On another body — stays as an active vessel on that body
- Suborbital — spawned on its trajectory, KSP physics takes over

**Active recording**: the recording being captured with full physics-frame fidelity (the player's current vessel). Only one at a time.

**Background recording**: a recording being captured passively for a non-active vessel. Uses on-rails state: Keplerian orbit for orbiting vessels, surface position for landed/splashed vessels.

## What gets recorded for each vessel

| Vessel state | Recording method | Fidelity |
|---|---|---|
| Active (player flying) | Harmony physics-frame sampling | Full: position, rotation, velocity, part events, adaptive sampling |
| On rails, orbiting | Keplerian orbit parameters | Analytical: exact orbit reconstruction |
| On rails, suborbital (vacuum) | Keplerian orbit parameters | Analytical: follows ballistic arc until impact or rail change |
| Suborbital, in atmosphere | Last known orbit snapshot | Approximate: KSP cannot time-warp in atmosphere; vessel may be destroyed on-rails |
| On rails, landed/splashed | Surface position (body, lat, lon, alt, rotation) | Static: vessel doesn't move |
| Destroyed | Terminal event | Destruction time + last known position |

Background recordings don't need physics-frame sampling — KSP computes their state analytically while on rails. We just need to capture enough to reconstruct their position at any UT.

## The recording tree data model

A recording tree is a **rooted DAG of recordings** connected by branch events.

```
RecordingTree
├── id: string (unique per tree)
├── treeName: string (root vessel name at launch — displayed in merge dialog)
├── rootRecordingId: string
├── activeRecordingId: string? (which recording is currently receiving physics samples — null if all background)
├── recordings: dict<recordingId, Recording>
├── branchPoints: list<BranchPoint>
└── (runtime) backgroundMap: dict<persistentId, recordingId>  ← rebuilt from recordings on load, not serialized

Recording
├── id: string
├── treeId: string
├── vesselName: string
├── vesselPersistentId: uint            ← stable across load/unload, used for all lookups
├── startUT: double
├── endUT: double
├── trajectory: list<TrajectoryPoint>   (active phases — physics-frame sampled)
├── orbitSegments: list<OrbitSegment>   (background/warp phases — Keplerian)
├── surfacePosition: SurfacePosition?   (body, lat, lon, alt, rot — for landed background vessels)
├── partEvents: list<PartEvent>
├── vesselSnapshot: ConfigNode (final vessel state, for spawning)
├── ghostVisualSnapshot: ConfigNode (initial vessel state, for ghost mesh — captured at recording start: launch for root, branch time for children)
├── terminalState: Orbiting | Landed | Splashed | SubOrbital | Destroyed | Recovered | Docked | Boarded
├── terminalOrbit: Orbit (for vessels in orbit/suborbital at end)
├── terminalPosition: SurfacePosition (for landed/splashed at end)
├── parentBranchPointId: string (null for root)
└── childBranchPointId: string? (null for leaves — the branch point where this recording ends)

BranchPoint
├── id: string
├── ut: double
├── type: Undock | EVA | Dock | Board
├── parentRecordingIds: list<string>  (1 for undock/EVA, 2 for dock/board)
└── childRecordingIds: list<string>   (2 for undock/EVA, 1 for dock/board)

SurfacePosition
├── body: string (CelestialBody name)
├── latitude: double
├── longitude: double
├── altitude: double
├── rotation: Quaternion (surface-relative)
└── situation: Landed | Splashed
```

**No `isActive` flag on Recording.** A recording can transition between active and background multiple times (see "Active vessel switching"). A single bool can't capture this history. Instead, playback determines the data source at each UT by checking which data type is present: trajectory points (active phase) vs. orbit segments (background/warp phase). This is the same mechanism existing recordings already use for time warp transitions — no new logic needed.

## Vessel identity

Background vessels are tracked by `vessel.persistentId`, never by object reference. KSP destroys and recreates `Vessel` objects when they cross physics range boundaries (load/unload), during docking/undocking, and on scene changes. The `persistentId` survives all of these. **Never cache a `Vessel` reference** — always look up by `persistentId` in `FlightGlobals.Vessels` when you need to access the vessel. A cached reference can become stale (pointing to a destroyed Unity object) without warning.

**Distinguishing unload from destruction:** `onVesselDestroy` fires for both unload (going out of physics range) and actual destruction — including on-rails destruction (orbit decay into atmosphere) where `vessel.loaded` is false. The `vessel.loaded` check alone is insufficient. Instead, **defer the check by one frame** and verify whether the vessel still exists in `FlightGlobals.Vessels`:
- If the vessel is still in the list after one frame: it was just unloaded (load boundary). Don't terminate.
- If the vessel is gone from the list: it was truly destroyed. Terminate the recording.

The background recording map (`persistentId → recordingId`) must survive vessel object recreation. When a vessel is loaded back into physics range, match by persistentId to reconnect it with its background recording.

## Staging debris filter

A routine stage separation (dropping a spent booster) fires the same `onPartUndock`/`onPartJointBreak` events as a meaningful undock. Without filtering, a typical rocket produces 3-5 debris branches before reaching orbit.

**Rule: only branch for trackable vessels.** At undock/release time, check if the departing vessel meets any of:
- Has `ModuleCommand` or `ModuleProbeCore` on any part (controllable vessel)
- Is `VesselType.SpaceObject` (asteroid or comet, detected via `ModuleAsteroid` / `ModuleComet`)

If none of these, it's debris — record it as a `Decoupled` part event on the parent recording (existing behavior) but do NOT create a new branch or background recording for it.

This means spent boosters, fairings, and other debris are handled exactly as they are today (part events on the parent recording, subtree hidden on ghost). Controllable vessels and celestial objects get their own branch in the tree.

**Claw (Advanced Grabbing Unit):** `ModuleGrappleNode` grab fires `onPartCouple` (treated as dock), release fires `onPartUndock` (treated as undock). An asteroid grabbed and moved to a new orbit will get a background recording on release, and spawn at its new position at EndUT. Ghost visual for asteroids uses sphere fallback (procedural asteroid meshes are not captured in v1).

## Split events (1 parent → 2 children)

**Undock (controllable vessel):** parent recording ends, two child recordings begin. One child is the player's vessel (active recording), the other becomes a background recording. Ghost visual snapshot captured for both children at branch time (both vessels are loaded at undock).

**Undock event deduplication:** `onPartUndock` fires once per separated part, not once per event. A multi-port undock or structural break can fire multiple times in the same frame for the same parent recording. **Deduplicate:** if a branch point was already created at this UT (or within the same physics frame) for the same parent recording, skip the duplicate. Use a "last branch UT" guard per recording.

**Undock snapshot timing:** `onPartUndock` can fire before KSP finalizes the vessel split. The new vessel's part list, mass, and orbital parameters may not be correct in the same frame. **Defer the snapshot by one frame** — use a coroutine (`yield return null`) or subscribe to `onVesselWasModified` and capture the snapshot on the next callback. This ensures both child vessels have correct part lists and orbital elements before snapshotting.

**Undock (debris):** NOT a branch point. Handled as a part event on the active recording (existing decoupling behavior). No background recording created.

**EVA:** parent recording ends, two child recordings begin. The vessel continues as one child, the kerbal as another. Whichever the player controls is active.

**onPartJointBreak vessel splits:** Structural failure can split a vessel into two controllable pieces without an explicit undock event. If `onPartJointBreak` creates a new vessel (check `FlightGlobals.Vessels` after one frame), apply the same logic as an undock: check the debris filter, and if the new vessel has `ModuleCommand`/`ModuleProbeCore`, create a branch point. The timing caveat applies here too — defer the check by one frame to let KSP finalize the split.

**Multiple simultaneous EVAs:** Each EVA creates a separate split. With 3 crew: first EVA splits R5→R6(vessel)+R7(Jeb). If player switches back to vessel and Bill EVAs: R6→R8(vessel)+R9(Bill). The tree handles this — it's just sequential branching. Code must handle rapid sequential branch creation cleanly (don't assume only one branch per frame).

## Merge events (2 parents → 1 child)

**Dock:** two parent recordings end, one child recording begins. The child is the merged vessel. Ghost visual snapshot captured from the combined vessel at dock time.

**Docking with a background vessel:** When the active vessel approaches a background vessel, KSP loads it back into physics range. At dock time (`onPartCouple`):
1. Identify the coupling partner by `persistentId`
2. Look up its background recording in the tree
3. **Immediately flag the absorbed vessel's `persistentId` as "docking in progress"** (see below)
4. End that background recording at dock UT — its final state is the last known orbit parameters (it was on rails until moments before docking)
5. Create branch point with two parents (active + background) → one child (merged vessel)
6. Start new active recording on the merged vessel

**Docking and `onVesselDestroy` race condition:** When A docks with B, KSP designates one vessel as dominant (usually the one with more parts). The absorbed vessel's `Vessel` object is destroyed — `onVesselDestroy` fires in the same frame as `onPartCouple`. The event order is: `onPartCouple` → vessel merge → `onVesselDestroy` (absorbed). The one-frame deferred destruction check (see "Vessel identity") would see the absorbed vessel gone from `FlightGlobals.Vessels` and incorrectly classify it as destroyed.

**Fix:** maintain a `dockingInProgress` set of `persistentId` values. In `onPartCouple`, add the absorbed vessel's ID. In the deferred destruction check, skip any vessel in this set. Clear the set after the deferred check completes. This prevents dock-absorption from being misclassified as destruction.

**Board (EVA kerbal enters vessel):** two parent recordings end (vessel + kerbal), one child recording begins (vessel with kerbal aboard).

**Board with a foreign vessel (not in the tree):** If an EVA kerbal boards a vessel that has no recording in the tree (e.g. a vessel from a previous launch, or a vessel the player switched to externally):
- The EVA recording (R7) ends — it's the only parent
- A new active recording starts on the boarded vessel with one parent (R7)
- The foreign vessel's prior existence is not part of this tree — only the post-boarding recording is
- This is a single-parent branch point (type: Board), not a two-parent merge

**Docking with a vessel from a previous recording tree:** Same principle. The previous tree's vessel was already spawned as a game object. In the current tree, the dock event has only one parent recording (the current tree's vessel). The other vessel is just a game object that gets absorbed. The child recording captures the merged vessel going forward.

## Terminal events (recording ends, no children)

**Destruction:** vessel destroyed. Record time and last position. No spawn. See "Vessel identity" section for distinguishing destruction from unload.

**Recovery:** vessel recovered mid-flight (e.g. StageRecovery mod). `onVesselRecoveryProcessing` fires. Record time and recovery value. No spawn.

**Revert/Commit:** player ends the recording tree. All recordings end at current UT. Leaf vessels are eligible for spawning.

**Commit Flight button (no revert):** commits the entire tree immediately. The current vessel stays active (`VesselSpawned=true`). All other leaf vessels spawn at their current positions. All crew reserved across all leaves. This is commit path #8 extended to trees — the player's vessel remains in-game, everything else spawns alongside it.

## Active vessel switching

**Major behavioral change from current system:** The current implementation stops recording when the active vessel changes (`FlightRecorder.DecideVesselSwitch()` returns `VesselSwitchDecision.Stop`). The tree model **inverts this**: vessel switching transitions the old recording from active to background, not stop. The `VesselSwitchDecision` enum needs new values (`TransitionToBackground`, `PromoteFromBackground`). `Stop` should only fire on scene exit (which auto-commits the tree). This is the single largest refactoring in the recording system.

When the player switches vessels (`[`/`]`, map view click, tracking station):

1. Current active recording transitions to **background**
   - Capture current orbital parameters or surface position
   - Stop physics-frame sampling
   - Part event polling stops (background vessels don't have loaded parts)

2. New vessel becomes the **active** recording
   - If it's already a background recording in this tree: promote to active, resume physics-frame sampling
   - If it's an unrelated vessel (not part of this tree): all tree recordings become background, tree keeps recording passively. No new tree node is created for the external vessel.

Key rule: **switching vessels does not end the recording tree.** It just moves the active cursor to a different recording in the tree. If the player switches to a vessel outside the tree, all tree recordings continue in background. When the player switches back to any tree vessel, that one becomes active again.

**A single recording can transition between active and background multiple times.** For example, the player undocks an orbiter, flies the lander, switches back to the orbiter, goes to Space Center, returns to the orbiter. That orbiter recording accumulates: orbit segments (background phase 1) + trajectory points (active phase 1) + orbit segments (background phase 2) + trajectory points (active phase 2). This is analogous to how existing recordings mix trajectory points and orbit segments during time warp transitions — the same recording holds both data types interleaved by UT.

**Timing:** Use `onVesselSwitchComplete` (not `onVesselSwitch`) to detect the switch. There's a frame or two delay while KSP loads the target vessel — the switch isn't safe to act on until the vessel is fully loaded.

**Promotion from background to active:** When the player switches to a background-recorded vessel, it may need to be loaded first (if it's out of physics range, KSP loads it on switch). After `onVesselSwitchComplete` fires, the vessel is loaded and ready for physics-frame sampling.

## Ghost playback

During playback, ALL recordings in the tree play simultaneously:

- Active recordings: full kinematic playback with part events, engine FX, etc. (existing system)
- Background recordings (orbiting): analytical orbit position (existing OrbitSegment system)
- Background recordings (landed): static ghost at recorded surface position
- Destroyed recordings: ghost disappears at destruction time
- Docked recordings: at dock time, two ghosts merge into one (hide the docking ghost, continue with merged ghost)

## Leaf vessels and spawning

At revert/commit, collect all **leaf** vessels — recordings that have no children and are not destroyed/recovered.

Each leaf has:
- A final vessel snapshot (for spawning)
- A terminal state (orbit, landed, splashed, suborbital)
- A terminal position/orbit (where to put it)

### Spawn timing

All leaf vessels spawn at the tree's EndUT (when the last ghost finishes). This is when the timeline "catches up" to the recording tree.

For vessels that stopped moving before EndUT (e.g. the Mun lander landed at t2 but recording tree continues until t5), the vessel's ghost sits at its landed position from t2 to t5, then the real vessel spawns at t5.

### Spawn position for orbital leaves

Orbital leaf vessels must NOT spawn at their raw snapshot position (which was captured at recording end time). If time has passed between EndUT and the actual spawn moment, the vessel will have drifted. **Propagate the `terminalOrbit` to the current UT** using KSP's `Orbit.getPositionAtUT()` to compute the correct spawn position. Landed/splashed leaves use their `terminalPosition` directly (they don't move).

**Crash-bound trajectories:** If a suborbital leaf's `terminalOrbit.PeA < 0` (periapsis below surface) and the vessel has passed periapsis by spawn UT, propagation produces a position underground. Before spawning, check for this case: if the propagated altitude is below terrain height, skip the spawn (the vessel crashed between recording end and spawn time) and mark the recording as `Destroyed`. Alternatively, spawn at the last safe altitude on the trajectory — but this is complex and v1 should treat it as destruction.

### Crew reservation

Every leaf vessel that will spawn needs its crew reserved. The current single-vessel reservation system extends to N vessels: iterate all leaf snapshots, reserve all crew, hire replacements.

## Merge dialog

Two options only: **Merge** or **Discard**. No recovery sub-question.

```
"Mun Expedition" — 5 vessels, 12m 30s

  Orbit Stage .... Kerbin orbit (180km)
  Mun Lander ..... Landed on Mun
  Capsule A ...... Kerbin orbit (95km)
  Jeb Kerman ..... EVA, Kerbin orbit
  Capsule B ...... Landed on Kerbin  ← you are here

  [Merge to Timeline]  [Discard]
```

- **Merge to Timeline**: commit whole recording tree. All surviving leaf vessels spawn at EndUT where they are. All crew reserved. Destroyed vessels don't spawn.
- **Discard**: nothing committed, no vessels spawn, no crew reserved.

The vessel count in the dialog shows surviving leaves only (not destroyed/recovered). If any vessels were destroyed, show separately: e.g. "4 vessels (1 destroyed)".

### No recovery option in the merge dialog

Parsek does not handle vessel recovery. KSP already provides this natively:

- **Tracking Station**: right-click any vessel → "Recover Vessel". Works for any vessel in any situation (landed, orbiting, splashed, suborbital) on any body.
- **Flight scene**: green "Recover" button for landed/splashed active vessels.
- **Funds**: KSP calculates recovery value automatically based on distance from KSC.

After Parsek spawns all leaf vessels, the player uses KSP's native recovery tools to recover whichever vessels they want. This is standard KSP gameplay — no mod intervention needed.

### Why all surviving vessels always spawn

After revert, the player is back on the pad. None of the tree's vessels physically exist in the game yet (we reverted). The only way to make them exist is to spawn them when the timeline catches up. The game state must reflect where everything ended up, just like a save file would. Recovery is the player's choice after the fact, using KSP's native tools.

## Resource tracking

Resource deltas (funds, science, reputation) are tracked per-tree, not per-recording. The `RecordingTree` holds tree-level resource fields:
- `preTreeFunds / preTreeScience / preTreeReputation` — game state at tree creation (launch)
- `deltaFunds / deltaScience / deltaReputation` — net change across the tree's lifetime

Individual recordings within the tree do NOT track resource deltas independently. The tree-level delta is computed at commit time as `(current game state) - (pre-tree state)`. During playback, resource deltas are applied as a single lump at the tree's EndUT (not incrementally per trajectory point). This simplifies aggregation — the per-recording `PreLaunchFunds`/`Science`/`Reputation` fields from the current Recording class are removed in tree mode.

Migration: existing single-recording commits keep their per-recording fields. When loaded as single-node trees, the tree-level delta is computed from the recording's existing resource fields.

## What doesn't change

- Ghost playback loop — already supports multiple simultaneous ghosts
- Orbit segment recording — already captures Keplerian parameters
- Part event system — per-recording, no changes needed
- Adaptive sampling — only applies to the active recording

## Edge cases

**Scene exit (go to Space Center, Tracking Station, etc.):**
The current system auto-commits pending recordings on scene exit (commit path #9). The tree model follows the same pattern for v1: **leaving the Flight scene auto-commits the entire tree.** All active and background recordings are finalized, the tree is committed to the timeline, leaf vessels spawn at EndUT. This avoids the complexity of serializing an in-progress tree across scene changes. The player must stay in the Flight scene for the tree to keep recording.

(Future: persisting trees across scene changes is desirable but requires full tree serialization in `ParsekScenario.OnSave/OnLoad` — a major architectural change deferred past v1.)

**Player switches to an unrelated vessel (not in the tree) without leaving Flight:**
All tree recordings become background. The tree keeps recording passively. When the player switches back to any tree vessel, that recording becomes active again. This is distinct from scene exit — the Flight scene is still loaded, so on-rails tracking continues.

**Vessel destroyed while backgrounded:**
`onVesselDestroy` fires — but must distinguish unload from actual destruction (see "Vessel identity" section). Only terminate the recording if the vessel is truly destroyed.

**Vessel recovered while backgrounded (StageRecovery or manual):**
`onVesselRecoveryProcessing` fires — terminate that recording with `Recovered` terminal state and recovery value.

**Multiple dockings creating a mega-vessel:**
Each dock event merges two recordings into one child. The tree can have multiple merge points. Ghost playback shows vessels approaching and combining.

**Undock → re-dock the same vessels:**
Legal. Creates a diamond pattern in the DAG: split at t1, merge at t2. Both branches play as ghosts in between. **Known visual artifact:** at t1 the parent ghost disappears and two child ghosts appear; at t2 two child ghosts disappear and the merged ghost appears. If the vessels changed between t1 and t2 (parts lost, fuel burned), there's a visual "jump" at each transition. Acceptable — the ghost snapshots are point-in-time captures, not continuous.

**Rapid sequential undocks (e.g. jettisoning multiple pods for reentry):**
Each undock creates a branch point. The active recording ends, two children start, then the player immediately undocks again from the new active child. This creates a cascade: R→(BG+R')→(BG'+R'')→... Each undock must fully complete (branch point created, both child recordings initialized) before the next one fires. The code must not assume a minimum time between branch events.

**Rapid undock-dock cycles:**
Each event creates a branch point. Short recordings may be collapsed or filtered if they're below the minimum sample threshold.

**Physics-only co-location (loose cargo, kerbals not in seats):**
Vessels or EVA kerbals that travel with another vessel purely through physics contact (e.g. rover inside an open cargo bay, kerbal standing on a boat deck) are NOT tracked by the recording tree — no dock/undock/EVA event fires, so no branch is created. After revert + spawn, the parent vessel appears at its destination but the loose passenger/cargo reverts to its pre-launch save position.

This applies to:
- Rovers placed in cargo bays without a docking port or decoupler
- EVA kerbals riding on the exterior or standing on a vessel (not in a seat)
- Any vessel held in place by collision physics alone

**What IS tracked:**
- Crew in seats → part of the vessel snapshot (always correct)
- EVA'd during this recording tree → branch exists, background recording tracks position
- Decoupled/undocked during this tree → branch exists (if vessel has command capability)
- Docked during this tree → merge event captures the combined vessel

**Mitigation:** players should use docking ports or command seats for cargo/passengers they want tracked. This is a documented v1 limitation.

**EVA Construction (KSP 1.11+):**
A kerbal on EVA can pick up, detach, move, and attach parts between vessels without docking. This can change vessel structure (parts added/removed) and potentially split vessels (removing a structural part). It is unknown whether EVA construction fires standard `onPartCouple`/`onPartUndock` events or uses a separate code path. **Needs investigation.** If standard events fire, the existing branch point logic handles vessel splits. If not, EVA construction vessel splits would be missed. Part additions/removals would need new part event types (`PartAttachedByEVA`/`PartRemovedByEVA`) for ghost visual fidelity.

**EVA-assembled vessels (building a rover from inventory parts):** A kerbal placing parts from inventory can create an entirely new vessel with no structural connection to any existing vessel — no dock/undock event fires. The new vessel appears in `FlightGlobals.Vessels` but Parsek has no branch for it. After revert + spawn, it won't exist. Potential fix: listen for `onVesselCreate` and start a background recording if the new vessel appears near an active EVA recording. Needs investigation — `onVesselCreate` also fires for debris and other irrelevant vessels.

Flagged as a v1 investigation item.

**Quicksave/quickload mid-tree:**
The player can press F5 (quicksave) while a recording tree is actively being built, then F9 (quickload) to return to that state. The recording tree's in-progress state — all recordings, branch points, background recording map, active recording cursor — must be serialized into quicksaves via `ParsekScenario.OnSave`. On quickload, the tree state must be fully restored so recording can resume seamlessly. If this proves too complex for v1, quickload mid-tree can be treated as a mini-revert: discard the in-progress tree (same as "Discard") and restart cleanly from the quicksave state. The key constraint: no orphan vessels or leaked crew after quickload.

**Recording tree with 20+ vessels (space station assembly):**
Performance concern. Background recording is cheap (just orbit snapshots), but ghost playback of 20 vessels needs the LOD/culling work from Phase 4.

**Suborbital atmospheric background vessels:**
A vessel in atmosphere cannot go on rails (KSP blocks time warp below ~70km on Kerbin). When the player switches away from a suborbital atmospheric vessel, KSP unloads it but may destroy it (decaying trajectory). The background recording captures the last known orbit at switch time. If the vessel is subsequently destroyed, `onVesselDestroy` fires and terminates the recording normally. If it survives (e.g. lands), the ghost is approximate (shows the last-known orbit, not the actual descent). This is an acceptable v1 limitation — atmospheric background vessels are rare edge cases (the player would normally stay on the vessel during descent).

**Background vessel SOI changes:**
A background vessel captured with orbit parameters at branch time might undergo SOI changes or crash into a body during KSP's on-rails propagation. Since background recordings only snapshot the orbit at branch time, they won't reflect this. **Acceptable limitation for v1.** The ghost will show the vessel at its initial orbit; the spawned vessel at EndUT will be at whatever state KSP's propagation put it in (which is correct — Parsek spawns the real vessel, not a frozen version). The ghost playback is approximate for background vessels; the spawn is always correct.

## Backward compatibility

Existing single-recording commits work unchanged. A single recording with no branches is just a recording tree with one node and no branch points.

The existing chain system (ChainId, ChainIndex, ChainBranch) is a flat approximation of the recording tree. **Simplest migration:** treat all pre-tree recordings as standalone single-node trees (ignore chain topology). Chains from before the tree system are rare — the mod is new. Full chain→tree conversion (inferring branch point types from `ParentRecordingId` and `EvaCrewName`) is possible but fiddly and not worth the complexity for v1.

## Background recording persistence

Background vessel states must survive scene changes (player goes to Space Center and back). On-rails orbits are deterministic — KSP tracks them — so we snapshot orbital parameters at scene change and resume recording on return to Flight. Landed vessels are static (surface position) and trivial to resume.

## Background recording depth

Full part events require the vessel to be loaded (within physics range ~2.5km). Unloaded background vessels can only provide orbital/position data. Part events are only recorded for the active vessel. This means automated actions on background vessels (solar panels auto-deploying at sunrise, antenna extending on signal loss) are not captured and won't replay on the ghost. The background ghost shows the vessel's initial visual state at all times.

This is acceptable for v1 — background vessel ghosts only need to show position, not part animations. The player-controlled vessel gets the full visual replay. If the player wants full part event fidelity for a specific vessel, they can switch to it (making it active) before the events happen.

## Serialization

Each recording tree is stored as a sidecar file, similar to existing recording files. A `RECORDING_TREE` ConfigNode contains:
- Tree metadata (id, rootRecordingId)
- `RECORDING` child nodes (one per recording in the tree)
- `BRANCH_POINT` child nodes

Each recording within the tree uses the existing recording serialization format (trajectory points, orbit segments, part events, snapshots). The tree file replaces the per-recording sidecar files — all recordings in one tree live in one file group.

**ConfigNode list fields:** `parentRecordingIds` and `childRecordingIds` on `BRANCH_POINT` are serialized as repeated key values (one `parentId = <id>` per parent, one `childId = <id>` per child). This is the standard KSP ConfigNode pattern for variable-length lists (same as how `CREW` entries work in vessel nodes).

**Thread safety:** All recording tree mutations (branch creation, recording start/stop, background map updates) happen on the main Unity thread. KSP's game events (`onPartUndock`, `onPartCouple`, `onVesselDestroy`, etc.) all fire on the main thread.
