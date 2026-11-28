# Parsek Far Watch - Design Document

*Design specification for the camera drone and live follow: watching any flying ghost past 300 km and at any body, with no loading screen.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies the engineering design of steps 2 to 4 of the far-watch plan: the camera drone, live follow on the same body, and live follow anywhere.*

**Status:** DESIGN (step 0 of the implementation plan, 2026-11-27). The step 1 throwaway proof flew on 2026-11-27 and its findings are folded in (18.1). Nothing built for production.
**Authority:** the owner-approved player design "Watching any ghost from launch to end" (rev 45, 2026-11-27) holds every player rule and ruling. This note never changes one; where it cites a rule it says "ruled".
**Out of scope:** Mission Rewatch mode (steps 5 to 7) gets its own design note; section 15 lists only the drone properties it will rely on. Watch-lane integration is step 7.
**Related docs:** `docs/dev/done/design-camera-follow-ghost.md` (today's watch mode, E5), `docs/parsek-flight-recorder-design.md` (background recording), `docs/dev/design-map-ts-render-architecture.md` (map ghosts), `docs/dev/design-save-format-policy.md`, `docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md` (stock-control annotation).
**Evidence base:** Parsek `origin/main` at `ba7cd9eb`; KSP 1.12.5 `Assembly-CSharp` decompiled. Audits A to F of 2026-11-27 (`far-watch/audits/`) are the full per-site lists; this note summarises them and records the decisions.

---

## 1. Introduction

Today watch mode refuses a ghost past 300 km (`WatchModeController.WatchEnterCutoffMeters`, exit at 305 km) or at another body (`ResolveWatchSameBodyDecision`), because KSP centres floating-origin precision, PQS terrain and `FlightGlobals.currentMainBody` on `FlightGlobals.ActiveVessel`, not on the camera. Far watch makes a temporary, invisible, one-part real vessel (the camera drone) the active vessel and moves it along the watched ghost's path, so KSP builds its world around the ghost while the camera keeps looking at the ghost.

This document covers:
- the drone: identity, build, movement, hold, kill avoidance, destruction;
- the focus hand-over out to the drone and back to the player's ship;
- the ship-safety check that admits a far watch and forces a return;
- saves written while the drone exists;
- the career fences (contracts, milestones, ledger capture);
- the decision for every audited site that reads the flown ship or checks for a map ghost, and the source gate that keeps it decided;
- map and UI artefacts, greyed controls, edge cases, logging, tests and phasing.

It does not cover Mission Rewatch mode, modded installs (checked after step 7) or any new player-facing surface.

### 1.1 What the player sees

The player rules are in the approved design, sections "Live follow", "Your ship while you watch far away", "Other ghosts around the camera", "What changes on screen" and "Gameplay edge cases". In short:

| Situation | What happens |
|---|---|
| Watching a ghost that passes 300 km, ship safe on rails | No snap-back. The camera stays on the ghost with sharp models, real terrain and the right planet. Overlay unchanged in form, plus the forced-return time when there is one (ruled). |
| W / Watch / map Watch on a far ghost or one at another body | Enabled for any flying ghost when the ship is safe; the watch starts directly (ruled). |
| Ship not safe on rails | Today's behaviour: snap-back at 300 km with one screen message giving stock's reason; greyed W with the hover reason "your ship must be landed, on the pad, or in an orbit clear of the atmosphere to follow this far" (ruled). |
| `[` `]`, or the W* button | Ends the far watch; the camera comes back to the ship with no loading screen (ruled). |
| Esc | Pauses; the watch goes on. Save, Load, Revert, Space Center (and the other scene exits) are greyed with a hover reason (ruled). |
| Ship's path is about to reach air or ground, its maneuver node comes due, or an alarm Jump | Brought back first, with one screen message (ruled). |
| Watched ghost ends and becomes a real ship | The camera stays on that ship until the player stops (ruled). |

### 1.2 Worked example

A career save at UT 1,000,000. The player's ship `Relay-1` is in a 120 km circular Kerbin orbit and is being recorded (tree `T1`, active recording `R7`). A committed ghost `Mun Lander` is on its ascent.

```
UT 1,000,000.0  W on Mun Lander, 40 km away: near watch (today's path).
UT 1,000,212.0  ghost 290 km from Relay-1. Safety: Relay-1 orbit Pe 118 km > 70 km + margin,
                no transition ahead -> Allowed, no return point. Spawning.
UT 1,000,212.0  drone pv built 50 m from Relay-1 at Relay-1's state vector, pid 418223911 fresh,
                vesselType Debris, discovery None. Loaded at frame +2.
UT 1,000,212.1  HandoverOut: ForceSetActiveVessel(drone). Stock packs every vessel; onVesselChange;
                OnVesselSwitchComplete backgrounds R7 into T1.BackgroundMap; camera re-applied.
UT 1,000,212.2  Following: drone orbit rewritten to the ghost's state each tick; hold renewed.
UT 1,000,212.4  Relay-1 passes 2.5 km from the drone and unloads (stock), recorded on rails.
UT 1,000,900.0  ghost lands on the Mun; the drone hops SOI with it; Mun terrain loads under it.
UT 1,000,960.0  player presses [ : HandoverBack. Drone orbit written 300 m from Relay-1 (Kerbin).
                Relay-1 loads at +3 frames (stock load range 2250 m). ForceSetActiveVessel(Relay-1).
                Camera restored to the watch-entry values. drone.Die(). Hold renewed 10 more
                ticks, then released. Idle. R7 promotes from background on the first modification,
                same recording id.
```

---

## 2. Design Philosophy

1. **The drone is the flown ship to KSP, and nothing else to Parsek.** Every system that must follow the camera already follows `ActiveVessel`; every Parsek site that means "the player's ship" is redirected or skipped. No site is left to luck: the source gate (section 11.4) keeps the decision explicit.
2. **A far watch is an ordinary vessel switch** (ruled): the flight stays open, the recording backgrounds and resumes as the same recording, merge / Revert / Discard work as after any switch. No new recording state.
3. **Fail toward the player's ship.** Any failure before the drone holds focus leaves today's near watch or snap-back; any failure after it routes through the one teardown, which gives focus back before anything else.
4. **Never take stock's unloaded-switch branch.** Focus moves only to a loaded vessel, in both directions, asserted at the call.
5. **Fence upstream of the reward.** A drone-caused stock completion would be paid by stock and booked by Parsek; the fence stops the stock condition, not the capture.
6. **Pure decisions, thin shells.** Every rule (admission, forced return, hysteresis, save repair, fence predicates, W stepping) is an `internal static` function with its own tests; the live code gathers inputs and applies results.
7. **No new UI.** Only greyed stock controls with their stock tooltip, existing Parsek hover text inside its budget, the existing watch overlay, and one screen message per event.

---

## 3. Terminology

| Term | Definition |
|---|---|
| Camera drone (drone) | A temporary real `Vessel` Parsek builds, makes `FlightGlobals.ActiveVessel` and moves along the watched ghost. Not a map ghost (`GhostMapPresence.ghostMapVesselPids` never holds it). |
| Player ship | The vessel that was active when the far watch began (`savedCameraVessel` in watch mode, resolved by pid + `Vessel.id`). Parked on rails while the drone flies. |
| Near watch | Today's watch mode: the camera on a ghost, the player ship active. |
| Far watch | Watch mode while the drone is active. |
| Hand-over out / back | The two forced switches: ship -> drone, drone -> ship. |
| Hold | `OrbitPhysicsManager.HoldVesselUnpack`, renewed every physics tick while the drone exists. Global: no vessel unpacks while it is set. |
| Exposed vessel | A vessel alive today only because it is within its own pack range of the ship (stock `Vessel.CheckKill` pressure branch); it becomes killable when the drone takes focus. |
| Return point | The UT by which the far watch must have handed back (air, ground, node, alarm). |

| Design concept | Implementation class (proposed) | Module |
|---|---|---|
| Drone identity | `CameraDroneIdentity` (drone pid + guid, player-ship pid + guid, their Set / Clear, pure predicates over plain values; static) | Core |
| Live drone checks | `CameraDrone.Is(Vessel)`, `CameraDrone.IsActive()` (`NoInlining` live reads) | Controllers |
| Drone vessel ops | `CameraDrone` (build, move, hold, strip, destroy) | Controllers |
| Far-watch state machine | `FarWatchController` (instance owned by `WatchModeController`) | Controllers |
| Pure decisions | `FarWatchDecisions` (admission, hysteresis, return planner, W step) | Controllers |
| Ship safety | `FarWatchShipSafety` (pure over plain orbit inputs) | Controllers |
| Map-ghost-or-drone check | `GhostMapPresence.IsGhostOrDroneVessel` (the drone half reads `CameraDroneIdentity.IsDrone`) | Ghost |
| Player ship resolver | `FarWatchPlayerVessel.Resolve()` (falls back to `ActiveVessel` with no drone) | Controllers |
| Award attribution | `FarWatchAwardScope` (the vessel a stock award is for; section 10.3); its stamp prefixes in `Patches/FarWatchAwardScopePatch.cs` | GameActions (both belts, `GameStateRecorder` and `ProgressRewardPatch`, call it, and GameActions cannot reach Patches) |
| Career fences | `Patches/FarWatchCareerFencePatches.cs` | Patches |
| Drone switch / load / pause patches | `Patches/CameraDronePatches.cs`, `Patches/FarWatchPauseMenuPatch.cs` | Patches |

Module placement. Core is a headless module (`scripts/arch/modules.toml` `[headless.modules]`), so `CameraDroneIdentity` holds only plain values (two `uint` pids, two `Guid`s) and pure predicates over them (`IsDrone(uint pid, Guid guid)`, `TryGetPlayerShip(out uint pid, out Guid guid)`): no game type, so `headless_pins.toml` gains no leak. Every live overload lives in Controllers. A site in a lower module (Recording, Crew, Ghost, GameActions, Rewind) passes the `persistentId` and `id` of the vessel it already holds to the Core predicate, and a PLAYER-SHIP site there resolves the ship with the stock lookup it already uses (`FlightGlobals.FindVessel(guid, out v)`) from `TryGetPlayerShip`. So every new edge points down (Recording / Crew / Ghost / Patches / Controllers -> Core, Patches -> Controllers in the same layer), no backward layer pair is added and `layer_pins.toml` does not move. The fields are private and written only by `CameraDroneIdentity`'s own `Set` / `Clear` (called by `FarWatchController`), so the `[crossTypeStaticWrites]` ratchet in `facts_pins.toml` gains no entry. `lifetimes.toml` gains the type (section 6.1).

---

## 4. Mental Model

### 4.1 States

```
            start request (W, Watch, takeover at 290 km)
   +------+  admitted by Admission()        +----------+  drone.loaded      +--------------+
   | Idle | -------------------------------> | Spawning | -----------------> | HandoverOut  |
   +------+                                  +----------+                    +--------------+
      ^                                         |   not loaded in 30 frames,      | switch done, recorder
      |                                         |   build threw                   | backgrounded, camera
      |                                         v                                  v re-applied
      |                                     +---------+                     +-----------+
      |   identity cleared,                 | Aborted | <--- drone lost ---  | Following |
      |   hold released                     +---------+      ship lost       +-----------+
      |                                         |                              |  stop / [ ] / W* /
      |                                         | (focus already on ship:      |  return point /
  +----------+   drone.Die() returned           |  Teardown directly;          |  ghost < 250 km /
  | Teardown | <--------------------------------+  else HandoverBack first)    |  alarm Jump
  +----------+                                                                 v
      ^                    ForceSetActiveVessel(ship) returned true    +--------------+
      +----------------------------------------------------------------| HandoverBack |
                                                                       +--------------+
```

### 4.2 Transitions

| From | To | Trigger | Guard (all must hold) | Action |
|---|---|---|---|---|
| Idle | Spawning | W / Watch on a far ghost; near-watch ghost reaches 290 km; map Watch; Missions window Watch | `Admission()` = Allowed (section 8); FLIGHT scene; no pending window (section 8.3) | Drop warp to 1x; build drone pv beside the ship (section 6.2); register identity BEFORE `pv.Load`; renew hold; the mover holds the drone from its first frame (6.3) |
| Spawning | HandoverOut | `drone.loaded` and 2 frames | ship still active; Admission still Allowed | Stop autosave coroutine; add input lock; disarm a post-switch auto-record watch already armed for the ship (`HasArmedPostSwitchAutoRecordWatch`, logged; the hand-back's ordinary switch re-arms it); `ForceSetActiveVessel(drone)` under the switch token |
| Spawning | Aborted | not loaded in 30 frames; build threw; Admission flipped | none | `drone.Die()` (not active); stay near watch or snap back with stock reason |
| HandoverOut | Following | `ForceSetActiveVessel` returned true; `onVesselChange(drone)` handled | the recorder is backgrounded (`IsBackgrounded`, `ActiveRecordingId == null`) by the next Update | Re-apply watch camera and map target; detach the drone's patched-conic solver; start moving |
| HandoverOut | Aborted | switch returned false | none | Focus still on ship; `drone.Die()`; near watch or snap-back |
| Following | HandoverBack | `[` `]`, W*, stop button, return point reached, ghost within 250 km of the ship, alarm Jump / Switch To, watched ghost ends with nothing left to follow (ruled hold first) | none (always allowed) | Drop warp to 1x; place the drone beside the ship (7.2 step 1) |
| any | Idle (no hand-back) | `onGameSceneLoadRequested` (section 7.5) | none | Scene-exit teardown: no switch, identity kept for the strip until the next save or load |
| Following | Aborted | drone destroyed or NaN state; ship destroyed or recovered | none | see section 7.6 |
| HandoverBack | Teardown | `ship.loaded` and 3 frames, then `ForceSetActiveVessel(ship)` true | `ship.loaded` asserted at the call | Restore watch-entry camera; `drone.Die()` |
| HandoverBack | Aborted | ship not loaded in 300 frames | none | section 7.6 |
| Teardown | Idle | drone gone; hold renewed 10 ticks after the ship's body PQS answers a raycast under the ship | none | Clear identity; release hold; restart autosave; remove input lock; one screen message when the return was forced (ruled) |
| Aborted | Teardown / Idle | as noted | none | as noted |

One teardown owns every exit (pattern A5): `ExitWatchMode` with or without `skipCameraRestore` calls `FarWatchController.RequestEnd(reason)` first while the state is not Idle, and the near-watch camera restore runs only after Teardown reaches Idle. `WatchModeController.cs:3939` (active vessel dying) and `ParsekFlight.RetargetStockCamerasBeforeTimelineGhostCleanup` (`ParsekFlight.cs:2703`, its `ActiveVessel` read at `:2714`, reached from `ExitWatchModeBeforeTimelineGhostCleanup` `:2650`) are routed through it.

---

## 5. KSP API Surface (verified)

Spot-checked in the decompile for this note (line numbers are original decompile lines):

| Fact | Where | Consequence |
|---|---|---|
| `ForceSetActiveVessel` -> `setActiveVessel(v, force: true)` skips `ClearToSave` and the `DiscoveryInfo.Level != Owned` refusal | `FlightGlobals.cs:2517-2536`, `:2597-2624` | The drone can be unowned and still be switched to; Parsek's safety check replaces `ClearToSave`. |
| Unloaded target: `onVesselSwitchingToUnloaded`, `SaveGame("persistent")`, `StartAndFocusVessel(Updated(), vessels.IndexOf(v))` | `FlightGlobals.cs:2651-2654` | A scene reload with a live-list index; never taken (rule 4). |
| Packed target: `GoOnRails()` on every vessel, then `onVesselSwitching`, `activeVessel = v`, `HoldVesselUnpack()`, `FlightCamera.SetTarget`, `MakeActive`, `onVesselChange` | `FlightGlobals.cs:2671-2704` | The ship's rails entry is seen while it is still active; the camera is overwritten and must be re-applied. |
| `OnFlightGlobalsReady(false)` fires at the start of every switch and `(true)` on the next `FlightGlobals.FixedUpdate` | `FlightGlobals.cs:2573`, `:2041` | See the contract correction below. |
| `HoldVesselUnpack(n)` sets a global flag with a countdown decremented in `FixedUpdate`; `LateUpdate` unpacks the active vessel when it lapses and gates every other unpack on it | `OrbitPhysicsManager.cs:161-263`, `:378-389`, `:539` | Renew every tick with n >= 2. |
| `CheckKill` pressure branch: packed, not active, not overrideOrbit, not landed, `GetPressure(altitude) > 1.0`, outside its own `pack` range of the active vessel -> `MurderCrew(); Die()` | `Vessel.cs:6863` ff. | The ship and every exposed vessel die in air once the drone is away; the active drone is exempt. |
| `CheckKill` terrain branch: not landed, `PhysicsHoldExpired()`, `altitude < terrainAltitude` (or -0.1 / -250 m when unknown) -> loaded: `onCrash` + `explode`; unloaded: `MurderCrew(); Die()`. No active-vessel exemption; run from `VesselPrecalculate` every tick | `Vessel.cs:6990` ff., `:3476`; `VesselPrecalculate.cs:468` | The drone must stay above terrain + margin in every state, Spawning and HandoverBack included (6.3). |
| `MakeActive` calls `AttachPatchedConicsSolver`, which adds a solver only when `patchedConicsUnlocked()` (Tracking Station level); `MakeInactive` calls `DetachPatchedConicsSolver`, which destroys it | `Vessel.cs:4637-4657`, `:4702-4764`, `:5335-5502` | The parked ship has no `nextPatch` chain, and an early career has none at all: the safety check propagates the ship itself (8.1). |
| `ScenarioDiscoverableObjects` kills every vessel without `StateVectors` knowledge whose signal life reaches 0, whatever its type; signal life is `1 - (UT - lastObserved) / (fadeUT - lastObserved)`, 1 with an infinite lifetime | `ScenarioDiscoverableObjects.cs:453-506`; `DiscoveryInfo.cs:229-236`, `:289` | Discovery None needs an infinite lifetime in the node (6.2). |
| When the active vessel's root part dies, `FlightLogger.checkMissionEnd` invokes `AttemptVesselSwitch` (`ForceSetActiveVessel` to the nearest controllable vessel) and starts `waitAndEndFlight`, which shows the flight-ended dialog unless the active vessel changed | `FlightLogger.cs:703-760`, `:782-840` | Drone loss path (7.6). |
| `FlightState` writes `activeVesselIdx = 0` while `!FlightGlobals.ready`, which `setActiveVessel` clears and the next `FlightGlobals.FixedUpdate` sets | `FlightState.cs:246-263`; `FlightGlobals.cs:2574`, `:2040` | The strip never trusts the captured index while the drone exists (9.1). |
| Pause-menu exits write `persistent` and then call `HighLogic.LoadScene`, which fires `onGameSceneLoadRequested` | `PauseMenu.cs:1813-1831`; `HighLogic.cs:795` | A scene-exit handler cannot refuse and runs after the exit save (7.5). |
| `FlightAutoSave.bypassAutoSave` is read only in `Start()`, before the coroutine starts | `FlightAutoSave.cs:71-99` | It cannot stop a running loop; the coroutine is stopped instead (9.2). |
| Flyby, Escape, Orbit and the other vessel nodes test `host.DiscoveryInfo.Level != Owned` and call `CrewSensitiveComplete(host)` before `AwardProgressStandard` | e.g. `KSPAchievements/CelestialBodyFlyby.cs:26-78`; `ProgressNode.cs:299` | The parked (Owned) ship earns genuine awards on rails; the ledger belt keys on the award's vessel (10.3). |
| Default `VesselRanges`: load 2250 m in every situation; unload 2500 (orbit / landed / splashed / prelaunch / escaping), 15 km sub-orbital, 22.5 km flying; pack 350 m / 10 km / 25 km | `VesselRanges.cs:130-136` | Spawn the drone within 2250 m; hand back only after `ship.loaded`. |
| `Game.Updated` builds `new FlightState()` (sets `activeVesselIdx = protoVessels.IndexOf(ActiveVessel.protoVessel)`) BEFORE `ScenarioRunner.GetUpdatedProtoModules()` runs `OnSave` | `Game.cs:1467`, `:1515`; `FlightState.cs:247-263`, `:440` | The strip in `OnSave` sees an index naming the drone; it must repair it. |
| `ContractSystem` resets every Active contract on `onVesselChange`, `onFlightReady`, `OnFlightGlobalsReady` and `onGameSceneLoadRequested` | `Contracts/ContractSystem.cs:938-942`, `:1768-1800` | The reset fence goes on `ResetContracts`, not on `OnVesselChange` alone. Proof FWP-2 counted 3 calls per switch (10.1). |
| `FlightCamera.SetTarget(Transform)` (Transform mode) parents the camera pivot under the target with no destroy rescue; only the Vessel and Part modes subscribe `OnTargetDestroyed`. The FlightCamera GameObject is a child of the pivot, and `OnDestroy` sets `fetch = null` | `FlightCamera.cs:322`, `:748`, `:812`, `:890`, `:569` | A camera left under a ghost dies with the ghost, and nothing in FLIGHT restores `fetch` (7.5, 7.6). |
| `TimeWarp.getMaxOnRailsRateIdx` reads only the active vessel (plus the ladder loop over `VesselsLoaded`): a non-landed active vessel is refused on static pressure > 0 (`NOT_IN_ATMOSPHERE`), on `geeForce` (`NOT_UNDER_ACCELERATION`) and, in the default `PeAltitude` mode, walks the rate down near the periapsis limit (`NOT_WHILE_ABOUT_TO_CRASH`). `TimeWarp.Update` switches to LOW (physics) warp for a non-landed active vessel with pressure > 0 | `TimeWarp.cs:1440` ff., `:600-612`; `GameSettings.cs:1009` | With the drone active, stock's warp limits read the drone, not the ghost (8.4). |
| `ClearToSave` returns `NOT_WHILE_ABOUT_TO_CRASH` for a SUB_ORBITAL or FLYING active vessel with `heightFromTerrain` under `QUICKSAVE_MINIMUM_ALTITUDE` (500 m) | `FlightGlobals.cs:3797-3830`; `GameSettings.cs:1381` | A surface-held drone blocks quicksave and delays the stock autosave loop (9.1). |
| `ContractParameter.Update` updates children then `OnUpdate`; `Contract.Update` checks the deadline before calling parameters | `Contracts/ContractParameter.cs:1305-1336`; `Contracts/Contract.cs:2820-2854` | A prefix on `ContractParameter.Update` leaves deadlines running. |
| `Rendezvous.OnVesselLoaded` starts `TrackCandidateVessel(v, ActiveVessel)` with no ownership test; completes when closing speed < 50 m/s | `KSPAchievements/Rendezvous.cs:28-122`, `:127-205` | The drone loading beside the ship is a Rendezvous. Fence by drone identity from creation. |
| `ClearToSave` returns `NOT_IN_ATMOSPHERE` only for `FLYING`; a sub-orbital ship above the air reads CLEAR | `FlightGlobals.cs:3686` ff. | Matches the ruled "suborbital above the air allowed". |
| `Vessel.Die` on the active vessel skips `RemoveVessel` | `Vessel.cs` `Die()` | Destroy the drone only after hand-back. |
| `FlightAutoSave.PersistentSave` is a loop that re-reads `ClearToSave` each interval and calls `SaveGame("persistent")` | `FlightAutoSave.cs:105-240` | Stop and restart the coroutine; do not flip `CanAutoSave`. |
| Pause-menu buttons are `DialogGUIButton`s with `OptionInteractableCondition` and `tooltipText` | `DialogGUIBase.cs:83-93`; `PauseMenu.cs:790-880` | Greying uses stock's own mechanisms (precedent `ClaimedVesselLaunchSitePatches.GreyFacilityProceedButton`). |

**Audit correction (D).** Audit D names `ContractSystem.OnVesselChange` (and `OnFlightReady`) as the reset path. `OnFlightGlobalsReady` also calls `ResetContracts()`, and `setActiveVessel` fires it twice per switch: `(false)` while the old vessel is still active, `(true)` one tick later. A prefix on `OnVesselChange` alone would let the `(true)` reset run every contract's `OnReset` against the drone. The fence (section 10.1) is a prefix on `ResetContracts` keyed on "the active vessel is the drone", which covers all four callers and, on the way back, skips the `(false)` call (still the drone) and lets `onVesselChange(ship)` and `(true)` re-evaluate against the ship.

Every other spot-checked fact in audits A to F matched the decompile.

---

## 6. The Drone

### 6.1 Identity

```
CameraDroneIdentity (static, Core; plain values only):
  DronePid: uint, DroneGuid: Guid     - set BEFORE pv.Load
  ShipPid: uint, ShipGuid: Guid       - the player ship, set at Spawning
  Exists: bool                        - DronePid != 0
  StripPending: bool                  - drone ids kept for a scene exit (7.5)
  SetDrone(pid, guid), SetShip(pid, guid), Clear(), ClearAfterStrip()
  IsDrone(uint pid): bool, IsDrone(uint pid, Guid guid): bool
  TryGetPlayerShip(out uint pid, out Guid guid): bool
CameraDrone (Controllers, live):
  Is(Vessel v): bool                  - IsDrone(v.persistentId, v.id)
  IsActive(): bool                    - Exists && ActiveVessel is the drone (NoInlining live read)
GhostMapPresence (Ghost):
  IsGhostOrDroneVessel(uint pid)      - ghostMapVesselPids.Contains(pid) || CameraDroneIdentity.IsDrone(pid)
```

`IsGhostMapVessel` is never widened: the switch, force-switch, GoOffRails, icon-drive and arc patches read it and must keep meaning "map ghost only" (audit C P1). Lifetime: `scripts/arch/lifetimes.toml`, category `load-transition` (the ids must outlive a scene exit until the strip has run, 7.5), cleared by `FarWatchController` Teardown, by the first `OnSave` or `OnLoad` after a scene exit (`ClearAfterStrip`), by the Cold load closure and by the in-game test runner's between-run reset (`ResetBetweenTestRuns`).

Fresh identity (audit C P3): pid from `FlightGlobals.GetUniquepersistentId()` (stock `CreateVesselNode` already does this), a new Guid, never a recorded or ghost pid, never built from a recorded snapshot. Many DRONE-NA verdicts rest on this.

### 6.2 Build

Built like the map-ghost marker (`GhostMapPresence.BuildGhostProtoVesselNode`, `ProtoVesselLifecycle.cs:1903`) with these differences (audit E section 1):

| Property | Value | Why |
|---|---|---|
| Part | one stock structural part, `strutCube`. It is not module-free: the proof build carried part modules `ModuleCargoPart` (stock inventory storability) and `KOSNameTag` (a mod's module, so the list depends on the install) and vessel modules `FlightIntegrator`, `AxisGroupsModule` and `SuspensionLoadBalancer`. Each is inert here: nothing can reach an invisible, collider-less part to store it; a name tag does nothing without a script; the flight integrator's thermal and aero effects cannot kill a packed, hardened part; axis groups read control input and the drone is not commandable; the load balancer only acts on wheels. Step 2's `CameraDroneBuildNodeTests` pins the list and an in-game cell re-checks it, so a new module on the part reds | No science, sensor, command, test-subject or trip-logger module on the active vessel (audit D Q5); never a `PartTest` / `PartRequest` target. |
| Name | `Parsek Camera Drone` (no `Ghost: ` prefix) | Distinct signature; the harness stock-load scan gains a drone-signature row so a leaked drone reds there. |
| `vesselType` | `Debris`, re-forced after load (precedent `:1831`) | Stock removes its CommNet node (`CommNetVessel.OnStart`), excludes it from the `[` `]` cycle (`VesselSwitching.Update`), makes it non-commandable; never SpaceObject (asteroid handling, spawner counts). |
| Discovery | `DiscoveryLevels.None` with `unobservedLifetime` and `fadeUT` = +inf, set in the node | No icon, label, TS row, target; every Owned-gated milestone ignores it; stock's non-force switch refuses it. A node without DISCOVERY defaults to Owned, so it is written explicitly (audit D). Expiry is keyed on discovery level, not type (section 5): a finite lifetime would let `ScenarioDiscoverableObjects` `Die()` the active drone, so `CameraDroneBuildNodeTests` pins the infinite values. |
| Situation | the ghost's non-landed situation; `landed=False`, `splashed=False` | Never `LandedOrSplashed` (no Recover button, no landed CheckKill exemption needed, no ground positioning). |
| Node flags | `vesselSpawning=False`, `skipGroundPositioning=True`, `prst=True`, `cln=False` | No spawn settle; deterministic budget; no autoclean. |
| Crew | none | |
| On `Vessel.Load` | the `GhostVesselLoadedInertPatch` twin: colliders off, renderers off, hardened (`maxTemp`, `skinMaxTemp`, `crashTolerance`, `gTolerance`, breaking forces = +inf, precedent `HardenGhostVesselPartPhysics` `:1966`) | Invisible in the camera, no collisions, no thermal or overload death. |
| Position at build | ship in flight: the ship's state vector plus 50 m along its orbit normal, matched velocity. Ship landed or splashed: 50 m straight up from the ship (along the body radius), surface-held (6.3) | Inside 2250 m load range. For a landed ship the orbit normal is horizontal and a free Kepler arc falls into the ground. |

Discovery None does not make the drone untargetable: in the proof, `FlightGlobals.fetch.SetVesselTarget(drone, overrideInputLock: true)` succeeded (`targetable=true`). A prefix on `FlightGlobals.SetVesselTarget` (`FlightGlobals.cs:1716`) refuses the drone as a target (one rate-limited log line), and `DroneHasNoPresence` asserts it (L1.2). The proof also read `orbitRenderer` = `REDRAW_AND_RECALCULATE` and a live `mapObject` on the drone and did not verify that the map draws no icon or orbit line for it; step 2's `DroneHasNoPresence` checks that in map view, and the patched-conic detach (6.3) and the map-camera redirect (12.1) are the fixes it measures.

An unlisted flown vessel (not in `FlightGlobals.Vessels`) is not viable by code reading (`FloatingOrigin.setOffset` walks the list; `activeVesselIdx` and `[` `]` index arithmetic go to -1; audit E section 1); the proof still runs the one test the approved design asks for (observation 11), and the design stays on a listed vessel unless it surprises.

### 6.3 Movement

Each physics tick while the drone exists, in EVERY state (Spawning, HandoverOut, Following, HandoverBack, Teardown until `Die`), from a `FixedUpdate` added to `ParsekFlight` forwarding to `FarWatchController.Tick`. Before Following, and from HandoverBack on, the target is the hold point beside the ship (6.2, 7.2) instead of the ghost; beside a landed or splashed ship the hold point is a surface-held state (a fixed lat / lon / altitude written each tick with the body's rotation velocity), because the terrain half of `CheckKill` does not exempt the active vessel (section 5) and a packed non-landed drone would otherwise follow its Kepler arc into the ground. Steps:

1. Read the watched ghost's state at its effective playback UT this tick (loop mapping included) directly from the recording trajectory (`IPlaybackTrajectory` at that UT), not from the ghost transform and not from the engine's resolved position: body, body-relative position, velocity, attitude. The engine stops resolving a ghost it hides above 50x warp (`WarpThresholds.GhostHide`): the proof captured through the engine's distance-resolver hooks and missed every tick of its 5 s windows at 100x in FWP-1 (`miss=250` of `ticks=250`, `miss=251` of `ticks=251`), then held its last body-fixed point even with the ghost 8 Mm away. A hidden ghost still has trajectory state, so hiding never stops the drone. Only a ghost with no trajectory state this tick (a gap) keeps the drone coasting on its last orbit.
2. Body = `FlightGlobals.getMainBody(ghostWorldPosition)`, and stock's `OrbitDriver.CheckDominantBody` is refused for the drone while it exists, so stock never runs its own SOI transition on it (as built, step 4: stock runs that check before the mover's write, see below); a mismatch with the recording's body is logged once per change. A cross-body jump (the ghost's SOI hop, a hand-back to a ship at another body, 7.2 step 1) sets the rotating frame explicitly before the first write at the new body: the old body's `inverseRotation` is cleared and the new dominant body's flag is set as `OrbitPhysicsManager.checkReferenceFrame` would set it. Stock writes only the dominant body's flag (`OrbitPhysicsManager.setRotatingFrame`, `OrbitPhysicsManager.cs:865`; `setDominantBody` `:568` and `checkReferenceFrame` `:1148` never clear the old body), and a body with `inverseRotation` set keeps writing `Planetarium.InverseRotAngle` and `Zup` (`CelestialBody.cs:1002` ff.). In FWP-1, after the Mun-to-Kerbin jump, the follow error grew linearly at about 148 m/s (742 m, 1481 m, ... 28,890 m at 1x; 211,624 m at 100x), which matches `r * (omega_Kerbin - omega_Mun)` = 600 km * 2.464e-4 rad/s = 147.8 m/s, and read 0.77 m once the ghost rose above Kerbin's rotating-frame threshold. A stale `Mun.inverseRotation`, left from holding the drone 3.9 km above the Mun, is the likely cause; it is not yet proven. Step 2 logs both bodies' `inverseRotation` flags and `FlightGlobals.RefFrameIsRotating` at every cross-body jump, and its follow lane flies the same jump.
3. Terrain floor: altitude := max(altitude, `body.TerrainAltitude(lat, lon)` + floor, floor over ocean), with `floor = 20 m + max(0, -v_up * dt) + 0.5 * g * dt^2` and `dt = TimeWarp.CurrentRate * Time.fixedDeltaTime` (the game time until the next write, during which the drone coasts on its Kepler arc; at 1x on Kerbin it is 20 m, at 1000x over a Mun peak it is about 350 m). The warp ceiling (8.4) keeps `floor` under 1 km, so the camera stays near the ghost. A landed ghost puts the drone `floor` above it; the camera still targets the ghost.
4. Validate: finite position and velocity, |r| > body radius - 1 km, eccentricity not within 1e-6 of 1. An invalid state skips the write and logs a rate-limited Warn; three in a row end the watch (a NaN orbit makes `OrbitDriver` destroy the active vessel, audit E).
5. Write only when the drone's own Kepler prediction for the next tick deviates from the target by more than 1 m or 0.1 m/s, or would cross the floor (a coasting ghost needs no per-tick write; a surface-held state is written every tick): `SetRotation(worldRot)`, `orbit.UpdateFromStateVectors(r, v, body, ut)` (Zup body-relative, the CLAUDE.md orbit-frame rule), `orbitDriver.updateFromParameters()`, then `FloatingOrigin.SetOffset` when the drone is past 500 m from the origin and `OrbitPhysicsManager.CheckReferenceFrame()` (precedent `TimeJumpManager.Placement.cs:1011-1031`).

As built (step 4): the follow reads the ghost's body each tick and no longer ends the watch when the ghost leaves the ship's body. A cross-body write of the ACTIVE drone (`CameraDrone.WriteState`) first clears every rotating-frame flag that is set on a body other than the target (pure `CameraDrone.PlanCrossBodyFrameClears`): the current dominant body through stock's own `OrbitPhysicsManager.SetRotatingFrame(false)`, any other one by writing its `inverseRotation` field. It then writes the orbit and runs `OrbitPhysicsManager.CheckReferenceFrame()` on every cross-body write, not only past 500 m from the origin. Stock fact, decompiled: `checkReferenceFrame` calls `setDominantBody` whenever the dominant body differs from the active vessel's `orbit.referenceBody`, returns early when that body does not rotate, and otherwise sets or clears its `inverseRotation` against `inverseRotThresholdAltitude`, so the new body's flag is stock's and never Parsek's. `[CameraDrone] cross-body frame before:` / `cleared=` / `after:` log both bodies' flags, the dominant body and `RefFrameIsRotating`, and every hop logs the 16.2 SOI line `drone body A -> B recordingBody=X match=True|False|n/a`; `[FarWatch] follow body= recordingBody= match=` is logged once per change. The finite-difference velocity is meaningless across a hop (the two positions are relative to different bodies), so the hop carries the last velocity the way stock carries a vessel's at an SOI change (`Orbit.cs:533`): `v_to = v_from + (from.GetFrameVelAtUT(ut) - to.GetFrameVelAtUT(ut)).xzy` (frame velocities are Zup; pure `FarWatchDecisions.CarryObtVelocityAcrossBodies`), logged as `ghost crossed into another SOI`, and the warp-limit gee window restarts (`FarWatchDecisions.ShouldRestartGeeWindow`). The terrain floor reads the body the drone is written at, so it holds at any body. Stock's `OrbitDriver.CheckDominantBody` is refused for the drone while it exists (`GhostOrbitDominantBodyPatch`, `DominantBodyCheckRoute.SkipDrone`, `__result = false` as for a ghost), because taking the body from `getMainBody` is not enough. Stock fact, decompiled: the packed drone's driver is in UPDATE mode, and `VesselPrecalculate.FixedUpdate` (`VesselPrecalculate.cs:268-271`, `MainPhysics` `:273`) calls `orbitDriver.UpdateOrbit()` (`:324`), whose UPDATE case runs `updateFromParameters()` and then `CheckDominantBody(referenceBody.position + pos)` (`OrbitDriver.cs:240`, `:346`); a different `getMainBody` there runs `RecalculateOrbit` (`:448`, `:475`) -> `OnRailsSOITransition` (`:728`, `:770`) and `onVesselSOIChanged` (`:903`). `VesselPrecalculate` sits in the `TimingStage.Precalc` slot (`TimingManager.cs:6-17`), ahead of default-order scripts such as `ParsekFlight.FixedUpdate`, so on the ghost's seam tick stock would propagate the drone's last orbit past the boundary and move it before the mover writes, and the mover would see no hop to clear the frame for (the exact script-order values live in Unity's project settings and are not in the decompile; the refusal holds either way). Refused, the drone keeps its body until the mover's cross-body write, which logs every hop; a refused check that would have changed the body logs one rate-limited `drone dominant-body check refused` Info. A drone `onVesselSOIChanged` reaching `ParsekFlight.OnVesselSOIChanged` is a Warn and is ignored. The watch's first follow tick only seeds the finite-difference sample (`FarWatchDecisions.ShouldSeedFollowSample`, `follow seeded`) and writes from the next tick, so the first write carries the ghost's real velocity rather than only the body's rotation.

The drone's patched-conic solver is detached after `MakeActive` (`DetachPatchedConicsSolver`): no per-write patch recompute, no drone orbit line or Ap / Pe, no maneuver-node editing on the drone. The navball reads the drone's `ReferenceTransform` (the ghost attitude) and the orbit velocity (the ghost speed), as ruled.

The CheatTeleport postfix (`Patches/CheatTeleportPatch.cs:24`) ignores the drone (the mover never calls `SetShipOrbit`); an Alt+F12 teleport while the drone flies is logged and overwritten by the next tick.

### 6.4 Hold and kill avoidance

- `HoldVesselUnpack(2)` every tick from `Tick` while `Exists` (renewing with 2 makes call order against `OrbitPhysicsManager.FixedUpdate` irrelevant). It also keeps every real vessel near the drone packed, which the approved design wants (a station the ghost docks with appears frozen).
- Belt: the `Vessel.GoOffRails` prefix (`GhostVesselLoadPatch.cs:422`) refuses for the drone too, with a rate-limited log, so one missed renewal cannot drop the active drone into physics at orbital speed. Confirmed by the proof: it fired exactly once per hand-over (FWP-1 twice before the kill, FWP-2 once, FWP-3 three times). The belt keys on a live drone and drops the moment the drone dies or is destroyed: the proof never cleared its drone pid at kill, and `OrbitPhysicsManager.LateUpdate` (`:378-389`) then called `GoOffRails` on the dead, still-active drone every frame, 1204 refusals in FWP-1.
- Pressure kill: not applicable (the drone is active).
- Terrain kill: the floor in 6.3.
- Thermal / overload: hardened parts.
- NaN orbit: validation in 6.3.
- Autoclean, SpaceObject expiry, debris budget: `cln=False`, type Debris with `prst=True`; `FlightStateGhostBudgetPatch` counts the drone like a ghost.

### 6.5 Destruction

`drone.Die()` only after `ForceSetActiveVessel(ship)` returned true (stock `Die` on the active vessel skips `RemoveVessel` and exits map view). Never recovered (a recovery would pay part value and fire recovery handlers). `Die` fires `onVesselWillDestroy` / `onPartDie` for the drone: every Parsek handler returns early for it (DRONE-SAME), `FlightRecorder.OnPartDie` / `BackgroundRecorder.OnBackgroundPartDie` vessel filters are checked in step 2. Identity is cleared after `Die` returns, so the career fences stay up across it.

### 6.6 What the drone must never be

- a map ghost (never in `ghostMapVesselPids`; never drawn by the Director; never given a recording index);
- Owned, commandable, controllable, crewed, EVA, a flag, a SpaceObject;
- landed or splashed;
- recorded (no recorder, background or active, ever attaches to it);
- in a save (stripped, section 9);
- on the map, in the Tracking Station, in vessel lists or labels, in the `[` `]` cycle, a target;
- a CommNet node (Debris has none; `GhostCommNetVesselPatch` becomes DRONE-SAME only if the type ever changes);
- switchable to by anything but Parsek's own token-carrying `ForceSetActiveVessel`.

---

## 7. Focus Hand-over

### 7.1 Out

1. Spawning: build and load (6.2). The drone loads packed beside the ship while the ship is still active.
2. Pre-switch: stop the autosave coroutine (section 9.2), add the far-watch input lock (`QUICKSAVE | QUICKLOAD | STAGING | THROTTLE | EVA_INPUT | VESSEL_SWITCHING`, on top of `WatchModeLockMask`), record the watch-entry camera values (`savedCameraVessel`, pitch, heading, distance, mode) if not already held from the near watch. These are the SHIP's values, restored only onto the ship at hand-back (7.2 step 5).
3. Assert `drone.loaded`, set the switch token, `ForceSetActiveVessel(drone)`, clear the token. Under the token `ParsekScenario.OnVesselSwitching` does not arm `vesselSwitchPending` (`ParsekScenario.SceneTransitions.cs:312`, one Info line): left armed, any FLIGHT -> FLIGHT scene change within 60 frames of a hand-over (`VesselSwitchPendingMaxAgeFrames`) would read as a Tracking Station switch, skip the revert strip in `OnLoad` and take the #266 pre-transition stash in `ParsekFlightMergeFlow`. A Parsek hand-over is not a player switch, at either end.
4. Stock packs every vessel while the ship is still active, so `FlightRecorder.OnVesselGoOnRails(ship)` records a normal rails entry (audit B, stock facts). Then `onVesselChange(drone)` reaches `ParsekFlight.OnVesselSwitchComplete`.
5. `OnVesselSwitchComplete(drone)` gets a drone arrival branch (DRONE-DIFFERENT, audit C row 10): run `watchMode.OnVesselSwitchComplete()` and the old-recorder-to-background block (`ParsekFlight.SwitchContinuation.cs:145-203`: `EmitPendingSplitBeforeFocusLeaves`, `TransitionToBackground(farWatchHandover: true)`, which keeps the supply-run proof (7.4), flush into the tree, ship into `BackgroundMap`, `ActiveRecordingId = null`); skip the `lastLandedUT` seed, `TryConsumeStockActionIntent`, the boarding branch and `EvaluatePostSwitchAutoRecordArmDecision` (the single most important skip, audit A riskiest 1).
6. Camera re-apply in the same frame, right after the call returns (the switch is synchronous): `SetTargetTransform(ghost pivot)`, then the camera placed by WORLD position (7.7: the offset from the pivot captured in `onVesselSwitching`, restored through `SetCamCoordsFromPosition`), distance, `pivotTranslateSharpness = 0`, watch lock (precedent the watch transfer re-apply `WatchModeController.cs` ~2660-2680 and entry `:1723`, `:1743`). Never copy `camPitch` / `camHdg` from the ship onto the ghost target: those angles are in the target's own frame, so the same numbers point the camera another way at another place or body. The proof copied them on HandoverOut, which was wrong (it measured `pivotDeltaM` 0, which checks the pivot, not the view direction). `PlanetariumCamera` is re-pointed at the ghost's map object (precedent `:2990`): stock leaves the map camera on the drone after every switch-in (the proof, which did not redirect it, read `mapTarget` = the drone on every one), so the redirect of 12.1 is required, not cosmetic. Transform-target mode is immune to `FlightCamera.LateUpdate`'s 2 km part-target retarget, but it has no destroy rescue: the pivot is parented under the ghost's camera pivot (`FlightCamera.cs:890`) and only the Vessel and Part modes subscribe `OnTargetDestroyed` (`:748`, `:812`), so if the ghost is destroyed while the camera still sits under it, the camera is destroyed with it and `FlightCamera.fetch` becomes null (section 5). Watch mode's bridge moves the camera off a dying ghost only while watch mode owns that ghost; 7.5 covers every other case.
7. Following starts on the first Update where `recorder == null || recorder.IsBackgrounded` holds; if the background flush has not happened in 3 Updates, the watch ends through Teardown (Warn).

Audit B P5 holds as a rule: keep the drone packed at the moment of each switch, so stock's "pack everything, then change `activeVessel`" order is the one Parsek's recorder expects.

### 7.2 Back

1. Move the drone to the ship's body, 300 m from the ship: along its orbit normal with matched velocity when the ship is in flight; straight up and surface-held (6.3) when it is landed or splashed. This may be an SOI change of the drone (logged); a cross-body move sets the rotating frame explicitly before the first write at the ship's body (6.3 step 2).
2. Wait for `ship.loaded` (stock loads it within 2250 m) plus 3 frames; renew the hold throughout.
3. Assert `ship.loaded`, set the token, `ForceSetActiveVessel(ship)`, clear the token. Stock packs everything again (the drone included).
4. `OnVesselSwitchComplete(ship)` runs today's path unchanged: recorder null, the ship tracked in the active tree -> `ArmTrackedBackgroundMember`; the ship's recording promotes from background on the first modification with the same id (`PromoteRecordingFromBackground`). With no active tree the ordinary outsider-idle arm applies, exactly as after any switch.
5. Restore the watch-entry camera values (not the values `MakeInactive` stored in the ship, which are the watch camera's; precedent `WatchModeController.cs:2072`). Copying pitch and heading is right here, because they were captured on the ship and go back onto the ship (7.7). For a landed or splashed ship the restored line of sight keeps its angle to the local up axis, so the camera never ends up under the terrain.
6. Teardown: `drone.Die()`; keep renewing the hold until a raycast down from the ship hits a terrain collider (landed ship) or immediately (orbit), plus 10 ticks (TimeJump precedent), then release; clear identity; remove the input lock; restart autosave.

Control is handed back while everything is still packed, so the ship unpacks with the player at the controls and SAS as it was (ruled).

### 7.3 Loaded-before-switch rule

`FarWatchController` never calls `ForceSetActiveVessel` on an unloaded vessel; the assert logs an Error and routes to Aborted. The switch-token patch on `ForceSetActiveVessel` / `SetActiveVessel` also refuses, while `Exists`, every switch Parsek did not start (stock `[` `]`, map double-click, alarm list Switch To `AlarmClockUIFrame.OnSwitchTo`, a mod), so no path can reach stock's unloaded branch with the drone up. A player switch request is turned into "end the watch first" (section 12.2).

### 7.4 Recorder

The player's recording is backgrounded into the same tree at step 7.1.5 and recorded in the on-rails background form for the whole watch (packed vessels emit no env-classified sections; the CLAUDE.md on-rails rule). It resumes as the same recording at 7.2.4. Guards that make sure nothing records the drone (pattern H3): `FlightRecorder.StartRecording` refuses while the drone is active (one log line), `PhysicsFramePatch`'s active branch returns for the drone, `ShouldArmPostSwitchAutoRecord` receives `isGhost || isDrone`, `DecideOnVesselSwitch` maps a drone arrival to `TransitionToBackground`, `ResumeAfterFalseAlarm` cannot be reached (entry refused while a split check is pending, section 8.3).

A switch voids the flight's in-progress supply-route proof (`TransitionToBackground` -> `RouteProofCapture.VoidRunManifestForBackgroundTransition`): the recorder's harvest windows die with the backgrounded leg and nothing witnesses what a background vessel mines, so a kept manifest could read a real harvest as an unaccounted gain, and the void's tombstone stops a later promotion from capturing a mid-run start baseline that would fold earlier gains into start cargo. V1 (decided by the owner 2026-11-28: a far watch keeps the proof): the far-watch hand-over keeps the run manifest instead (`TransitionToBackground(farWatchHandover: true)` -> `RouteProofCapture.ShouldKeepRunManifestOnBackgroundTransition`, Info `RouteRunManifest kept across far watch`) when the leg's start half is already on the tree recording and the recorder holds no harvest window; otherwise it voids as before (Info `RouteRunManifest not kept across far watch: ... reason=<r>`). It is safe because the ship is packed under the hold for the whole watch and nobody flies it: no harvest can start on it, and any resource it gains is unwitnessed, so the gain check rejects it naming the quantity (a void would instead drop the tree to the legacy analysis, which takes such a gain as launch cargo). The hand-back's promotion adopts the kept start half (the write-once guard reads `start-half-already-captured`), so it never re-baselines. A dock, undock, coupling or part loss of the ship while the drone holds focus (the controller compares the ship's part count with the hand-over's every tick) voids the kept manifest (`reason=far-watch-ship-changed`). Rule 2a of 8.2 is gone.

### 7.5 Single teardown

Every end of a far watch calls `FarWatchController.RequestEnd(reason)`: the UI stop paths, `[` `]`, W*, `ExitWatchMode` (all callers, including `skipCameraRestore: true`), alarm Jump, forced returns, drone or ship loss. `reason` is one closed enum, logged on every transition.

Camera safety, from the FWP-3 failure. The proof's park op called `ExitWatchMode(true)` with the camera under ghost #2's camera pivot and re-targeted nothing. `ResetWatchState` set `watchedRecordingIndex = -1` and `savedCameraVessel = null` (`WatchModeController.cs:2022`), and `RestoreCameraAfterWatchExit` returned early on the skip (`:2048`). When the loop retired ghost #2 about 9 s later (21:23:27.193), `ParsekPlaybackPolicy.HandleGhostDestroyed` called `NotifyWatchedGhostDestroying`, which returned at once because no ghost was watched (`:350-352`), so `BridgeWatchCameraOffDestroyingGhost` never ran; Unity destroyed the camera with the ghost and `FlightCamera.fetch` went null. FWP-1 ran the same park and survived only because it switched to the ship 36 ms later. The same thing happens in production whenever a watch exit skips the restore while the camera sits under a ghost: `skipCameraRestore: true`, the policy's `ExitWatchModeForHandoff` (`ParsekPlaybackPolicy.cs:819`), or any watch exit while the drone is active, where the normal restore would go to `savedCameraVessel`, which is the drone (the active vessel at `EnterWatchMode`). Two rules:
- While the drone exists, EVERY watch exit re-targets the camera IN THE SAME FRAME to a non-ghost target, whatever `skipCameraRestore` says: the drone through `FlightCamera.TargetActiveVessel()` (Vessel mode, which has the destroy rescue; 7.7), or the ship at hand-back. A restore to `savedCameraVessel` = the drone is acceptable only as that Vessel-mode target, never as a ghost transform.
- A per-frame net in the far-watch controller's `Update` (a ghost can retire in a frame with no physics tick): when `FlightCamera.fetch.Target`, or the pivot's parent chain, lies under a ghost hierarchy that watch mode does not currently own, re-target to the drone at once and log one Warn naming the ghost.

A scene exit is the one end without a hand-back. `onGameSceneLoadRequested` cannot refuse a load, and stock has already written the exit save when it fires (section 5). The scene exits are greyed (9.3), so only a mod or an ungreyed stock path gets here. `ParsekFlight.OnSceneChangeRequested` sets `sceneChangeInProgress` (`ParsekFlight.cs:1694`) before its `ExitWatchMode` call (`:1719`), so that call reaches `RequestEnd(SceneExit)`, which:
- never calls `ForceSetActiveVessel` (the scene is being torn down, and the ship may be unloaded);
- stops the mover (the hold, the input lock and the stopped autosave go with the scene); sets `StripPending` and keeps the drone ids, so the next `OnSave` or `OnLoad` in any scene strips a drone the departing game state may still hold, then calls `ClearAfterStrip`;
- leaves the recording to today's outsider-state finalize. The active tree is in the outsider shape (ship in `BackgroundMap`, `ActiveRecordingId` null), which the merge flow's scene-exit finalize (`ParsekFlightMergeFlow`, reached from `OnSceneChangeRequested`) already handles for a scene exit after any switch.

The exit save itself was written through `OnSave`, so it holds no drone and names the ship (9.1).

### 7.6 Loss paths (Aborted)

| Lost | Detection | Behaviour |
|---|---|---|
| Drone before HandoverOut | `onVesselWillDestroy(drone)`, not loaded | `Die` it if alive; near watch continues or snaps back |
| Drone while active | `onVesselWillDestroy(drone)` while active (a missed hold or a stock kill) | Early return from `ParsekFlight.OnVesselWillDestroy` (no tree-destruction dialog); `FarWatchController.OnDroneLost`: if the ship is loaded, force-switch to it; else build a replacement drone beside the ship (HandoverBack placement) and switch as soon as the ship loads. Stock reacts too: `FlightLogger.checkMissionEnd` invokes `AttemptVesselSwitch` and starts `waitAndEndFlight` (section 5). The switch-token patch lets `AttemptVesselSwitch` through only when its target is the ship; once the ship is active, `waitAndEndFlight` sees a changed active vessel and shows nothing. If the ship cannot be made active before the dialog shows, the dialog stays: it is stock's flight-ended screen with Revert and Space Center, which is V2's behaviour. Error line. Proof item 14 measured something else with the ship unloaded (FWP-1): the dead drone stayed active, no other vessel became active, no dialog showed and no `persistent` save was written for 20 s, although stock's `waitAndEndFlight` should save and show the dialog after 4 s (`FlightLogger.cs:708`; Parsek's `FlightResultsDialogLogPatches` only log). The cause is unknown, since no full log of that section was kept; the stale GoOffRails belt (6.4) may be part of it. Step 2's own lane re-measures it with both ship states. |
| Camera lost (`FlightCamera.fetch == null`) | the far-watch `Update` reads `FlightCamera.fetch` | Unrecoverable: nothing inside the FLIGHT scene recreates the flight camera, and every later frame throws, in stock code too: `FlightGlobals.UpdateInformation` (`fetch.mainCameraRef.transform`, `FlightGlobals.cs:2088-2089`), `CrewHatchController.LateUpdate`, `Vessel.MakeInactive` through `FlightCamera.CamHdg` (so the hand-back switch itself throws) and `Vessel.Die` through `CameraManager.SetCameraFlight`. FWP-3 logged 12,838 NullReferenceExceptions after it. The Space Center button and the end-flight path both touch the camera, so no stock way out is safe. The two camera rules of 7.5 exist to make it unreachable; if it is seen anyway, one Error line, and the controller stops every call that touches the camera. |
| Ship destroyed or recovered while Following | `onVesselWillDestroy(ship)` (packed, not active) | The background recorder records its end as today. No hand-back target exists. V2 (recommended): the mover parks the drone where the ship was, and Parsek shows stock's flight-ended dialog (`KSP.UI.Dialogs.FlightResultsDialog.Display`, the screen stock shows when the flown ship is lost) with its own Revert and Space Center buttons, which stay live; the pause-menu greying (9.3) does not touch them. Both buttons leave the flight through stock code, and the exit save is stripped (9.1). |
| Ship never loads on HandoverBack | 300 frames | Error; retry the placement once at 100 m; then V2's behaviour. |

As built (step 3): a watch that ends in Aborted with no hand-back to come (the ship never loaded, or the drone was lost with the ship unloaded) waits there for a scene exit, and the stop keys do nothing in Aborted. The pause-menu exits and the altimeter return-to-KSC are therefore held only while a hand-back can still come (`FarWatchTransitions.HoldsTheFlightExits`: Spawning to Teardown, not Aborted), so the player can always leave the flight; the exit save is stripped (9.1). A Switch To asked for during the watch is replayed after an abort that resolves at once (the Spawning case: the ship never lost focus), and dropped with an Info line when the watch waits in Aborted, since no hand-back gives the ship focus.

### 7.7 Camera hand-over rules

The capture and restore shape below is taken, as inspiration only (no dependency), from the public-domain mod EasyVesselSwitch (`ihsoft/EasyVesselSwitch`, `Source/Controller.cs`), which keeps the camera steady across stock vessel switches.

- Capture in `onVesselSwitching(from, to)` while `from` is still active: the camera's world position, the pivot's world position and `FlightCamera.mode`, kept as the camera's offset from the pivot so a floating-origin shift between capture and restore does not move it. Restore in `onVesselChange(to)`, after the new target is set: `setMode` back to the captured mode if stock changed it, then `SetCamCoordsFromPosition(newPivot + offset)` (`FlightCamera.cs:2547`), so the camera looks at the world the same way it did. This is the HandoverOut placement (7.1 step 6).
- Never copy `camPitch` / `camHdg` across targets. Pivot rotation is `frameOfReference * Yaw(camHdg) * Pitch(camPitch)`, and the frame belongs to the target, so the same angles point another way at another place or body. Angles are restored only onto the target they were captured on: the watch-entry values onto the ship at hand-back (7.2 step 5).
- Mode guard: the position is forced only when the mode is AUTO, FREE, ORBITAL or CHASE. A LOCKED camera is left as stock put it, and the skip is logged.
- Optional pivot glide: instead of a cut, the pivot may move from the old position to the new target over about 1 s. It is timed on unscaled time, so pause and warp do not stretch it; it adds the target's own motion since the capture each frame and absorbs floating-origin shifts; and it aborts the moment `FlightCamera.Target` changes, a watch-mode retarget included. Step 2 decides where it is used (a jump across hundreds of km reads better as a cut).
- Landed or splashed target: the placement keeps the line of sight's angle to the local up axis that it had before the switch, so the camera never ends up under the terrain. This is the hand-back to a landed ship.
- Fallback target: when no ghost is watched (the ghost ended, a parked drone, the FWP-3 case), the camera targets the one-part drone through `FlightCamera.TargetActiveVessel()` (`:2098`, Vessel mode), never a stale ghost transform.
- Backstop: Parsek's own hand-overs place the camera explicitly (7.1, 7.2). The capture and restore in the two stock switch events also covers a switch stock starts itself while the drone exists, such as `FlightLogger`'s `AttemptVesselSwitch` to the ship after a drone death (7.6).

---

## 8. The Ship-Safety Check

### 8.1 Decision function

```
FarWatchShipSafety.Evaluate(FarWatchSafetyInputs in) -> FarWatchSafetyVerdict

FarWatchSafetyInputs:
  nowUT: double
  clearToSave: ClearToSaveStatus     - stock's own reading, taken while the ship is still active
  ship: SafetyVessel
  exposed: List<SafetyVessel>        - every other vessel within ITS OWN pack range of the ship
  pending: PendingWindows            - section 8.3
  reFlyActive: bool
  shipNodeUTs: List<double>          - the ship's maneuver nodes
  shipAlarmUTs: List<double>         - alarms bound to the ship (stock Alarm Clock)
SafetyVessel:
  pid: uint, guid: Guid, name: string
  landedOrSplashed: bool, srfSpeed: double
  crewed: bool, controllable: bool
  dockingCaptureActive: bool         - a ModuleDockingNode in acquire / pre-attach state
  path: SafetyPath                   - Parsek's own propagation of the vessel's on-rails path (below)
SafetyPath:
  segments: List<OrbitSegment>       - conic pieces across SOI changes
  ending: Final | Impact | Truncated | Failed
  endUT: double                      - Impact / Truncated: where the walk stopped
SafetyBody:
  radius, atmosphereDepth (0 without air), maxTerrainHeight (pqs radiusMax - radius, 0 without PQS)
FarWatchSafetyVerdict:
  kind: Allowed | AllowedWithReturn | Refused
  returnUT: double                   - AllowedWithReturn only
  reason: FarWatchRefusal            - Refused only (closed enum, maps to the hover text)
```

`Evaluate` is pure; the live gatherer reads the vessels, nodes and alarms and builds each `SafetyPath`. Stock's `orbit.nextPatch` chain cannot be used: the patched-conic solver exists only on the active vessel and only when the Tracking Station level unlocks patched conics, and `MakeInactive` destroys it (section 5; Parsek's own `PatchedConicSnapshot` already refuses without it, `PatchedConicSnapshot.cs:519-521`). The gatherer instead runs Parsek's own propagation over the vessel's current orbit: `BallisticExtrapolator.Extrapolate` (start state from the orbit at `now`, bodies from `IncompleteBallisticSceneExitFinalizer.TryBuildExtrapolationBodies`, `stopOnFinalConic: true`, the default limits: 8 SOI changes, 50 years). It walks SOI entries and exits through `TwoBodyOrbit` and stops at a surface impact, at the deletion periapsis of an atmosphere, or at a stable final conic, which maps to `ending` Impact / Impact / Final; a walk stopped by its limits is Truncated, a refused start (missing body, invalid state) is Failed. It is one synchronous pass (one Zup, the CLAUDE.md orbit-frame rule) and needs no stock solver, so it works the same in an early career. Exposed vessels get the same walk.

Example: an early-career ship (Tracking Station level 1, no patched conics) on a Mun free return whose Kerbin periapsis is 30 km. The walk leaves the Mun SOI, then enters a Kerbin conic with periapsis 30 km < `h_safe(Kerbin)` = 71 km. `dangerUT` is the moment that conic falls through 71 km, so the verdict is AllowedWithReturn with `returnUT = dangerUT - 300 s`, or Refused when that is under 600 s away.

### 8.2 Formula

For a vessel V that is not `landedOrSplashed`, with `h_safe(B) = max(B.atmosphereDepth, B.maxTerrainHeight) + 1000 m`:

```
dangerUT(V) = the first t >= now on V's path where altitude(t) < h_safe(body(t)),
              searched analytically per segment (periapsis of the arc inside [startUT, endUT]);
              ending Impact: at the latest endUT;
              ending Truncated: endUT (nothing is known past it, so it counts as danger);
              +inf only when every segment keeps periapsis >= h_safe and the ending is Final.
```

This is stricter than stock's own predicate (`GetPressure(alt) > 1.0` for the pressure kill, `altitude < terrainAltitude` for the terrain kill): above the atmosphere top the pressure is zero and above the highest terrain no terrain exists, so neither branch of `CheckKill` can fire (the same `minPeAllowed` shape as stock `TimeWarp.getMaxOnRailsRateIdx`).

Rules, in order (the first refusal wins; refusal reasons are stock's wording where stock has one):

| # | Condition | Verdict |
|---|---|---|
| 1 | `reFlyActive` | Refused `ReFly` |
| 2 | any pending window (8.3) | Refused `Busy` (logged with the window name; hover text "Your ship is busy for a moment") |
| 2a | (removed: a far watch keeps the supply-run proof, 7.4, V1) | - |
| 2b | the ship's `ending` is Failed | Refused `PathUnknown` (logged with the failure reason). A landed or splashed ship is never walked (no path, no danger), so 2b, 8 and 9 never apply to it (step 3) |
| 3 | `clearToSave` is `NOT_IN_ATMOSPHERE`, `NOT_UNDER_ACCELERATION`, `NOT_WHILE_MOVING_OVER_SURFACE`, `NOT_WHILE_ABOUT_TO_CRASH`, `NOT_WHILE_ON_A_LADDER`, `NOT_WHILE_THROTTLED_UP` | Refused with that status (stock's localized `#autoLOC_6002346`..`6002351` text) |
| 4 | ship or any exposed vessel `dockingCaptureActive` | Refused `DockingCapture` |
| 5 | ship landed / splashed with `srfSpeed > 0.3 m/s` | Refused `NOT_WHILE_MOVING_OVER_SURFACE` |
| 6 | an exposed vessel, not landed, crewed or controllable, `dangerUT` < +inf | Refused `CrewedShipNearbyInAir` |
| 7 | an exposed vessel, not landed, neither crewed nor controllable | Allowed for it (a spent stage falls as ruled); logged |
| 8 | ship `dangerUT` finite and `dangerUT - now < 600 s` | Refused `ReturnTooClose` |
| 9 | ship `dangerUT` finite | AllowedWithReturn, `returnUT = dangerUT - 300 s` |
| 10 | otherwise | Allowed |

Then `returnUT := min(returnUT, first shipNodeUT - 180 s, first shipAlarmUT - 60 s)` and a return point that is closer than 120 s of game time refuses with `ReturnTooClose`.

### 8.3 Pending windows (audit A pattern 2)

Far watch is refused while any of these is set, because each is a one-frame deferred decision that reads `ActiveVessel`: `pendingSplitInProgress`, `pendingSplitRecorder != null`, `deferredJointBreakCheckInFlight`, `crashCoalescer.HasPendingBreakup`, `pendingTreeDockMerge`, `pendingAutoRecord`, a `ResumeAfterFalseAlarm` check in flight, a `TimeJumpManager` armed placement or `watchedPlacement`, `restoringActiveTree`, a `SwitchSegmentSession` pre-switch dialog open, an open merge dialog. The refusal turns 22 SKIP-WHILE-DRONE split / placement rows of audit A into unreachable ones; they stay listed in the gate under their code.

### 8.4 During the watch

- Re-check in GAME time, not real time. A packed ship follows its conics exactly, so the path computed at admission stays valid until something changes it. The controller compares `now` with `returnUT` every physics tick (O(1)), so no warp rate can step past the return point unseen. The path and the verdict are recomputed on any event that can change them: an SOI change of the ship or any other orbit change, a maneuver node or alarm added or removed, and as a drift check every 600 s of game time, rate-limited to one recomputation per real second. As built (step 3, `FarWatchDecisions.RecheckTrigger`), the triggers are polled once per real second against the values cached at the last evaluation: the ship's `mainBody` (`soi`), its conic's semi-major axis more than 1 m or eccentricity more than 1e-6 off (`orbit-delta`; a packed ship's conic changes only when something changes its orbit, so the element compare stands in for the 1 m position test), the node and alarm UT lists (`node`, `alarm`) and the 600 s drift; the return-point compare itself still runs every tick. A recomputed refusal ends the watch (`ShipUnsafe`), with the forced-return screen message. The exposed set is frozen at the start (no new vessel can come within pack range of a packed ship).
- Stock's warp limits read the active vessel, which is the drone (section 5), so left alone they do NOT give "the normal rules for where the ghost is" (ruled). The proof showed it. In FWP-1 the drone was surface-held 50 m above the landed ship (SUB_ORBITAL, its conic's periapsis far below the surface), and `getMaxOnRailsRateIdx` clamped every request to 1x through the `PeAltitude` walk-down (`NOT_WHILE_ABOUT_TO_CRASH`): `maxRateSeen=1.00` over 1435 refused `SetRate` calls in the 240 s op cap. A landed active ship would have been allowed to warp. In FWP-3 the drone was in Kerbin's air, so `NOT_IN_ATMOSPHERE` applied and `TimeWarp.Update` switched to LOW physics warp (`maxRateSeen=3`). So a surface-held or low drone blocks on-rails warp entirely, and a drone in air gets physics warp only.
- Decision (option b of three: a: park the drone on a safe conic whenever warp is asked for and follow the ghost only visually; b: this patch; c: no warp): while the drone is active, a Harmony patch on `TimeWarp.getMaxOnRailsRateIdx` (`TimeWarp.cs:1440` ff.) and on the in-atmosphere LOW-mode switch in `TimeWarp.Update` (`:600-612`) evaluates stock's own rules against the WATCHED GHOST's state instead of the drone's: its situation (landed or splashed), altitude above terrain, static pressure and periapsis, taken from the trajectory state the mover reads (6.3 step 1). The rules are transcribed into a pure function (`FarWatchWarpLimits`, the `GhostCommNet` precedent of a verbatim stock transcription) with stock's own reason codes, and the patch only feeds it the ghost's inputs. Stock's G-force rule reads the ghost's acceleration from its trajectory; the drone's own `geeForce` is an artefact of the orbit writes and is never read. As built (step 3 review): the ghost's felt g is read gravity-free, from the change of its specific orbital energy and angular momentum magnitude over the sample window (`FarWatchWarpLimits.NonGravitationalGee`), which a coast never changes however long the window is at high warp and which a rotating world frame does not move; a velocity difference minus the end point's gravity read the gravity vector's turn over a 200 s window as more than stock's 0.1 g. As built (step 3): the getMaxOnRailsRateIdx prefix (`Patches/FarWatchWarpLimitPatch.cs`) also writes stock's private `minPeAllowed` from the result, and the LOW-mode switch is refused through a prefix on stock's private `TimeWarp.setMode(Modes)`, which `TimeWarp.Update` calls for every automatic switch: a switch that disagrees with the ghost's wanted mode (`FarWatchWarpLimits.WantsLowMode`: not landed, pressure positive, or landed and sliding at more than 0.3 m/s, stock's own three conditions) is refused, Parsek's own switch to the ghost's mode holds a token, and a switch with the modifier key held (the player's own physics-warp request) is left to stock. When the controller has no reading of the ghost (no state yet, a missing body), the patch answers the fallback of option c for that call: at most physics-warp rates, reason `NOT_WHILE_ABOUT_TO_CRASH`. Results: a landed ghost allows on-rails warp, as stock allows a landed flown vessel; a ghost in air gets physics warp up to 4x, as stock would give a flown vessel there; a low ghost on a crashing conic is walked down as stock walks down a flown one. With no ghost watched (a parked drone), the patch uses the player ship's state. Everything stays below Parsek's ceiling in the next bullet, and the overlay names the reason whenever warp is held below the requested rate (ruled). Fallback (option c), if step 3 shows the patch is unsafe: no on-rails warp while the watched ghost is low or in atmosphere, stock's limits by the drone stand, and the overlay says so.
- Risks of the warp patch, each measured in step 3 before it ships: (1) on-rails warp over a landed or low ghost lets the surface-held drone coast `dt` game-seconds on a Kepler arc between writes, and the terrain kill has no active-vessel exemption, so the 6.3 floor and its 1 km cap are the only guard; the proof never warped there (item 15). (2) LOW physics warp in air must leave every vessel packed under the hold. FWP-3 showed it at 3x (the drone packed on every tick, the ship never unpacked); 4x is unmeasured. (3) The patch replaces a stock decision: it is inert unless the drone is active (first check), its prefix falls through to stock on any exception, and the transcription carries stock's lookahead walk (`lookAhead`) and both `ORBIT_WARP_MAXRATE_MODE` settings. (4) A mod that patches `TimeWarp` itself is checked on the modded installs after step 7. If any of these fails, the fallback (c) applies.
- Warp ceiling, tightening as the danger nears: stock's limits as evaluated at the ghost (above). Parsek adds the highest stock rate `r` with `r * 10 s <= returnUT - now` (at least 10 s of real time before the return) and `r * Time.fixedDeltaTime <= (returnUT - now) / 2` (no single tick crosses the return point), plus the terrain-floor cap of 6.3 (`floor <= 1 km`). It is applied through `TimeWarp.SetRate`; HandoverBack starts at `returnUT` at any warp, dropping to 1x first.
- The overlay says why warp is limited (ruled). When stock's limit at the ghost's location or Parsek's ceiling holds warp below the rate the player asked for, the existing overlay gains one short clause: "warp limited: near <body>" (stock's altitude limit at the ghost) or "warp limited: return in <m:ss>" (Parsek's ceiling). It is wording on the existing overlay, not a new surface. As built (step 3), the clause is a third line of the same overlay box (`FarWatchDecisions.BuildOverlayClause`; "return in <m:ss>" alone when warp is not held, and the box grows by one line only while the clause is non-empty), because the second line already carries the camera-mode hint.
- Hysteresis (ruled): takeover begins when the near-watched ghost is 290 km from the ship (so the switch completes before today's 300 km cutoff; the 305 km near-watch exit is suppressed while Spawning / HandoverOut); a far watch hands back when the watched ghost comes within 250 km of the ship (50 km of slack). A ghost at another body never triggers the 250 km rule. W or Watch on a ghost closer than 300 km while far-watching hands back first, then starts today's near watch (ruled). As built (step 3): the 250 km hand-back resumes today's near watch on the same ghost after the teardown, and a direct far start (W or Watch on a ghost past 300 km) enters watch mode only after the first follow write has put the drone beside the ghost, so the watch camera never frames the ship's side. The seam `EnterWatchMode` stays near-only (it calls today's entry, so W1's 300 km refusal is unchanged); the seam's far path is `FarWatch op=start mode=follow`. While a far watch runs, only the settled Following state takes a W: a follow switch when it follows a ghost, the hand-back for a ghost near the ship; in Spawning, HandoverOut, HandoverBack, Teardown or Aborted, and for the follow switch of a hold watch, the W is refused with a hover reason and the button greys (`FarWatchDecisions.DecideWatchRequest`), so the camera never lands on a far ghost the drone is not following. The takeover at 290 km reads the same far-start predicate as W, and every refusal paces the next attempt to one per real second (`FarWatchTakeoverGate`); the 300 km snap-back message names that refusal's own reason (an EVA kerbal reads its own sentence, not the ship-orbit one).
- The overlay shows the return time when there is one (ruled).
- `ClearToSave` also reads the drone, and is not patched: while the drone is surface-held it returns `NOT_WHILE_ABOUT_TO_CRASH` (section 5). Saves are locked or stopped during the watch anyway (9.2); 9.1 lists what that does to the residual saves.

### 8.5 Return points

| Return point | Source | Lead |
|---|---|---|
| Air or ground ahead | `dangerUT` | 300 s |
| The ship's next maneuver node | `ship.patchedConicSolver.maneuverNodes` captured at start (the solver is destroyed by `MakeInactive`; nodes are re-read from the ProtoVessel flight plan on each re-check) | 180 s (ruled "a few minutes") |
| An alarm bound to the ship | stock `AlarmClockScenario` alarms with the ship's `vesselId` | 60 s, plus the alarm's own warp drop |
| Alarm Jump to the ship | `AlarmJumpSwitchPatch` | ends the watch (ruled) |
| Alarm Jump / Switch To / map Switch To another vessel | `AlarmJumpSwitchPatch`, the new `AlarmClockUIFrame.OnSwitchTo` path, `MapFocusObjectOnSelectPatch` | hand back first, then today's rules run (ruled) |

---

## 9. Saves

### 9.1 Drone strip and the active-vessel repair

Prerequisite: the audit-F fix, PR #2842 (merged 2026-11-27). It makes `GhostMapPresence.StripFromSave` re-derive `activeVesselIdx` through the pure `ResolveActiveVesselIdxAfterGhostStrip` (`Ghost/GhostMapPresence.SaveActiveIndex.cs`: the index moves down by the ghosts before the active entry), and subscribes `onVesselSwitchingToUnloaded` -> `GhostMapPresence.RemoveAllGhostVesselsBeforeFarSwitch` so stock's live `IndexOf` matches the stripped game. Step 2 builds on it.

The drone is not in `ghostMapVesselPids`, so step 2 extends that resolver with a drone case:

1. `StripGhostMapVesselsFailClosed` (`ParsekScenario.Save.cs:33`): the early return becomes `ghostMapVesselPids.Count == 0 && !CameraDroneIdentity.Exists && !CameraDroneIdentity.StripPending`.
2. Whenever the drone ids are set, the target is the ProtoVessel whose `vesselID` equals `CameraDroneIdentity.ShipGuid`, whatever the captured index says. The index cannot be trusted while the drone exists: it names the drone after a hand-over, and it is 0 from each switch until the next `FixedUpdate` (`!FlightGlobals.ready`, section 5). Identity is by Guid because `persistentId` is craft-baked.
3. Strip ghosts (unchanged), then the drone (`RemoveAll` by drone pid AND guid; no crew, so no roster fix).
4. `activeVesselIdx = IndexOf(target)` in the stripped list, through the extended pure resolver (`ResolveActiveVesselIdxAfterGhostStrip(..., droneGuid, shipGuid, out outcome)`; with no drone ids it is PR #2842's arithmetic unchanged).
5. If the drone ids are set and the ship's proto is missing: log Error and, while the drone is live and the ship is not known lost, throw (the existing fail-closed handler aborts the save). Never fall back to index 0 while a drone exists. Two cases write the save instead: the scene-exit leftovers (`StripPending`) and a lost ship (`ShipLost`, set in the ship's `onVesselWillDestroy`, 7.6 V2, because stock's flight-ended screen and every way out of the flight save). Both strip the ghosts and the drone with no ship repair (`ResolveActiveVesselIdxAfterStripWithoutShip`): the captured index moves down by the removed entries before it, and is -1 when it named a removed entry, never 0.
6. One Info line: old index, new index, ghosts stripped, drone stripped, target by guid.

Confirmed by the proof: the strip removed the drone on every save (one entry, vessel index 2 -> 1), the ship was found by guid, the saved file named the ship and held no drone, and stock's periodic autosaves during the watch were stripped too. The no-strip control kept the drone in the save with the drone active, so the strip is required.

`ClearToSave` reads the drone while it is active. Whenever the drone is SUB_ORBITAL or FLYING within 500 m of the terrain (surface-held beside a landed ship, or low over a landing ghost), it returns `NOT_WHILE_ABOUT_TO_CRASH` ("Active Vessel is about to crash", `FlightGlobals.cs:3797-3830`; FWP-3 logged it at 21:23:19.742). Stock then refuses a quicksave and holds the autosave loop until it clears: the proof's restarted autosave wrote in 5.11 s with the drone in orbit (FWP-2) and waited 20.18 s with it surface-held (FWP-1). Neither matters in the design, since F5 is input-locked and the autosave coroutine is stopped (9.2), but the restart at Teardown runs after the ship is active again, and a residual save that does not go through `ClearToSave` (the seam `SaveGame`, stock's flight-end save) is still stripped.

The far-switch path never runs with the drone up: the switch-token patch refuses every switch Parsek did not start (7.3), and the drone itself is never a switch target outside the token. Belt, in `RemoveAllGhostVesselsBeforeFarSwitch`: if the drone ids are set (a mod calling `FlightDriver.StartAndFocusVessel` directly, say), log Error and treat the drone like a ghost there: remove it from the live list (`Die` when it is not active, else `FlightGlobals.RemoveVessel`), so stock's `IndexOf` matches the stripped save.

### 9.2 Quicksave and autosave off (ruled)

- F5 / Alt+F5 / F9: the far-watch input lock includes `QUICKSAVE | QUICKLOAD` (stock `QuickSaveLoad.cs:287`, `:312`, `:432` read them).
- Autosave: `FlightAutoSave.fetch.StopAllCoroutines()` at HandoverOut, `FlightAutoSave.fetch.Start()` at Teardown (public; re-arms the interval). Never flip `Parameters.Flight.CanAutoSave` (serialized, and the loop exits for the scene). `bypassAutoSave` was the reviewers' preferred switch, but it is read only in `Start()` (section 5), so it cannot stop a loop already running; it is not used. Proof observation 7 forces an autosave mid-watch to prove the strip backstop anyway.
- Seam `LoadGame` refuses with `far-watch-active` (pattern H7). Seam `SaveGame` is the one allowed save during a far watch, for automation only (the seam is armed only by `PARSEK_TEST_COMMANDS=1`), and refuses the names `persistent` and `quicksave`. It runs `GamePersistence.SaveGame` -> `Game.Updated()` -> `OnSave`, the same path every residual save below takes, which is what FW-1 must prove (17.4).
- Saves that can still happen while the drone exists: stock's flight-end save after an active-vessel death (`FlightLogger.waitAndEndFlight`), a crash-path save, a mod. The strip handles each.

### 9.3 Pause-menu greying (ruled)

`Patches/FarWatchPauseMenuPatch.cs`: a postfix on stock's private `PauseMenu.draw()`, which builds the button array (`Display` only spawns it), walks the returned `DialogGUIButton`s (by the localization keys of their labels: `#autoLOC_360545` Revert, `#autoLOC_360553` Load, `#autoLOC_360569` / `#autoLOC_8004249` Save, `#autoLOC_360586` Space Center, `#autoLOC_360600` Tracking Station, `#autoLOC_360632` Quit, `#autoLOC_360644` Main Menu) and wraps each one's `OptionInteractableCondition` so it reads the far watch live (greyed while a hand-back can still come, from Spawning to Teardown; stock's own condition otherwise, Aborted included, 7.6) and `tooltipText` = "Stop watching first ([ or ])" on Save, Load, Revert (and its sub-popup), Space Center, Tracking Station, Main Menu and Quit. Tracking Station, Main Menu and Quit are greyed with Space Center because stock writes the save on each way out (audit C Q2); greying them follows the same ruling. The stock Resume, Settings and the far-watch stop keys stay live. Precedent: `ClaimedVesselLaunchSitePatches.GreyFacilityProceedButton`. The altimeter return-to-KSC button is greyed through its own button state: `Patches/FarWatchAltimeterPatch.cs` holds stock's private `AltimeterSliderButtons.setUnlock(int)` at 0 (both buttons locked, the LED yellow) by the same predicate, and stock's unlock coroutine unlocks it again the frame after the watch ends because its state then differs; the click handlers `returnToSpaceCenter` and `recoverVessel` are blocked by the same predicate. The Recover button never shows (drone never landed). Stock's flight-ended dialog (`FlightResultsDialog`) is not the pause menu and is never greyed: when it shows during a far watch (7.6, V2) its Revert and Space Center leave the flight through stock code, and the exit save is stripped.

### 9.4 The PARSEK node during a far watch

`ParsekScenario.OnSave` writes the in-flight active tree as it stands: `ActiveRecordingId` null and the player's recording in `BackgroundMap`, the outsider shape any switch leaves. No transform is needed: a tree with a null `ActiveRecordingId` already loads as `PendingTreeState.LimboVesselSwitch` (`ParsekScenario.TreeRestore.cs:331-334`), and `ParsekFlight.RestoreActiveTreeFromPendingForVesselSwitch` (`ParsekFlight.TreeRestore.cs:1487`) reinstalls it, finds the active vessel (the ship, by the repaired index) in `BackgroundMap` and arms the tracked post-switch watch for it (`:1615-1627`). That is the live hand-back shape (7.2 step 4): the ship's recording promotes on the first modification with the same id. FW-1 asserts `activeRecId=<null>` in the stash line, the ship in `BackgroundMap` and the `armed tracked vessel` line.

No save-format change: the drone is never written and the PARSEK node keeps its shape (additive nothing; `docs/dev/design-save-format-policy.md` case "no change").

---

## 10. Fences

### 10.1 Contracts: one shape

Decided: **per-class fences on the stock classes audit D lists, both ways, plus a prefix on `ContractSystem.ResetContracts`.** The ruling is "fence each condition that reads the flown ship, both ways; never pause contracts as a whole", so a base `ContractParameter.Update` prefix (which would freeze every condition, non-vessel ones too) is out.

| Patch point | Predicate | Classes (audit D) |
|---|---|---|
| `OnUpdate` prefix on each class (resolved per declaring type) | skip while `CameraDrone.IsActive()` | the 18 polled classes: `PartTest`, `ReachAltitudeEnvelope`, `ReachBiome`, `ReachSpeedEnvelope`, `AsteroidParameter`, `CometParameter`, `CrewCapacityParameter`, `CrewTraitParameter`, `LocationAndSituationParameter`, `MobileBaseParameter`, `PartRequestParameter`, `ResourcePossessionParameter`, `SpecificOrbitParameter`, `SpecificVesselParameter`, `StabilityParameter`, `StationaryPointParameter`, `SurveyWaypointParameter`, `VesselSystemsParameter` |
| event-handler prefixes | skip when the event vessel is the drone | `ReachDestination.OnVesselSOIChange`, `ReachSituation.OnVesselSituationChange`, `ReachFlightEnvelope.OnVesselChange`, `VesselDestinationParameter.CheckFlightLog` |
| `SurveyWaypointParameter.CheckExperimentResults` | skip while the drone is active | the experiment judge reads the active vessel |
| `ContractSystem.ResetContracts` (private) | skip while the active vessel is the drone | `onVesselChange`, `OnFlightGlobalsReady` (both calls per switch), `onFlightReady`, scene-load request (section 5 correction) |

`ProgressTrackingParameter` (FENCE-COMPLETE) has no own fence: it completes only through the Rendezvous milestone, fenced in 10.2.

Why this shape:
- Both ways by construction for each fenced class: a skipped `OnUpdate` or handler can neither complete nor undo. Hold timers (`StabilityParameter`) and counters (`SpecificVesselParameter`'s 5-frame success counter) pause with it and resume against the ship.
- On the way back the reset fence skips the `(false)` call (the drone is still active) and lets `onVesselChange(ship)` and `OnFlightGlobalsReady(true)` re-evaluate every contract against the ship, which is stock's own switch-back behaviour.
- Confirmed by proof FWP-2: every switch made exactly 3 `ResetContracts` calls, `OnFlightGlobalsReady(false)` with the OLD active vessel, `onVesselChange` with the new one, and `OnFlightGlobalsReady(true)` one frame later with the new one; the contract count stayed 6 throughout. The intended predicate is "the active vessel is the drone", which fences 3 of the 6 calls of a round trip: out, the `onVesselChange(drone)` and `(true)` calls; back, the `(false)` call. The 3 calls that see the ship run, as in any stock switch. The proof's own fence keyed on the whole switch window and fenced all 6 (`fenced=6`); that is wider than intended and is not the design.
- Everything else keeps running: deadlines (`Contract.Update` checks `dateDeadline` before calling parameters), the 26 SAFE classes, non-vessel conditions, offers and strategies.
- 24 patch points, each resolved by `AccessTools.Method` on its declaring type with a Warn when missing (a missing one is a hole, and the in-game `FarWatch` category asserts every one resolved), first line `ParsekGameModeGate.CheckInert`, one `VerboseRateLimited` line per fenced call site.

Known gap: mod parameters (Contract Configurator's `VesselParameterGroup` and its children, any mod class) are not fenced, because a per-class list cannot name them. They are checked on the modded installs after step 7. The ledger belt (10.3) catches a drone-attributed stock award, not a mod condition.

### 10.2 Milestones and ordering

The drone is unowned (discovery None from its first frame; the proof read discovery None and never Owned), so the 16 Owned-gated nodes and the records ignore it (audit D). Two ungated nodes are fenced in `FarWatchCareerFencePatches.cs`. The Rendezvous fence is mandatory, not a belt: in FWP-2, with no fence, stock started `Now tracking Parsek Camera Drone vs. DD1` as the drone loaded beside the ship and completed Ike Rendezvous in the same frame (funds +21,120, reputation +1), and at park it tracked `DD1 vs drone` again. Parsek booked that award to the ship's recording like a genuine one, so no belt downstream can catch it (10.3).

- `Rendezvous.OnVesselLoaded`: skip when `v` is the drone OR the active vessel is the drone. Keyed on "the drone exists", not "the drone is active", because the dangerous moments are at both ends:
  - start: the drone loads beside the ship while the SHIP is active, matched velocity, in orbit -> tracker drone vs ship -> Rendezvous complete. The `v is drone` test stops it.
  - end: the ship reloads beside the returned drone while the DRONE is active -> tracker ship vs drone. The `active is drone` test stops it. The fence stays up until after `Die` returns and the identity clears; a tracker whose reference was the drone ends when the reference is destroyed.
- `CelestialBodyReturn.OnVesselSituationChange`: skip when the host is the drone (belt; the drone is never landed).

### 10.3 Ledger

Parsek's capture (`ProgressRewardPatch` postfix on `ProgressNode.AwardProgress`, `ContractParameterRewardCapturePatch` on `ContractParameter.AwardCompletion`, `GameStateRecorder.OnProgressComplete`) would book any drone-caused award as genuine and re-apply it on every recalculation. The fences stop the stock conditions upstream; the belt below catches a fence that did not hold. It must key on the vessel the award is FOR, not on "a drone exists": stock pays Flyby, Escape, Orbit and the rest to any Owned vessel (section 5), so the parked ship crossing an SOI on rails earns a genuine award mid-watch, and that award is booked as always.

- Attribution (`FarWatchAwardScope`): a prefix on `ProgressNode.CrewSensitiveComplete(Vessel)` and `(ProtoVessel)`, which every vessel node calls with its host just before `Complete` / `AwardProgressStandard` in the same call, stamps {node, vessel pid + guid, frame}. The records nodes (`RecordsSpeed`, `RecordsAltitude`, `RecordsDistance`, `RecordsDepth`) call `AwardProgress` directly, but they track only `FlightGlobals.ActiveVessel` (`RecordsAltitude.cs:82`), so their award is for the active vessel.
- Milestone belt, in `ProgressRewardPatch.RoutePostfix` after the existing `IsReplayingActions` return (`ProgressRewardPatch.cs:84`) and in `GameStateRecorder.OnProgressComplete`: when the stamp for this node and frame names the drone, or the node is a records node and `CameraDrone.IsActive()`, log one Warn naming the node and the drone (the log validator's WRN surface reds the lane) and do not book it. Any other award, the ship's included, is booked as today.
- Contract belt, in `ContractParameterRewardCapturePatch`: a completion of one of the 18 polled classes of 10.1 while `CameraDrone.IsActive()` is drone-caused by definition (those classes read only the active vessel), so it is a Warn and not booked. The four event classes are attributed by their own event vessel at the fence; any other completion is booked.
- Not booking leaves the ledger the authority: the next recalculation (`KspStatePatcher.ContractParameters / Milestones`) puts stock back. Proof observation 13 was to check that it really reverts an unbooked stock award; if it does not, the belt books it instead and only logs. The proof did not meet that premise: its forced Rendezvous was booked (next bullet), so the revert is unproven and step 2's own lane measures it.
- What the belt cannot catch: an award stock pays for the drone but books to the ship. In FWP-2 the unfenced Rendezvous fired while the ship was active, Parsek booked it as `MilestoneAchieved Ike/Rendezvous` tagged to the ship's recording (`Milestone enriched funds=21120 rep=1.0`), and recalculation kept it because it is booked. Nothing in the award names the drone, so the only defence is the fence upstream (10.2).

---

## 11. Site Decisions

### 11.1 Audit summary by decision code

Flown-ship reads (`FlightGlobals.(ActiveVessel|fetch.activeVessel)`, 218 hits outside `InGameTests/`):

| Code | A: Controllers (116) | B: other modules (102) | Total |
|---|---|---|---|
| SKIP-WHILE-DRONE | 35 | 4 | 39 |
| PLAYER-SHIP | 12 | 26 | 38 |
| CAMERA-OK | 13 | 11 | 24 |
| WATCH-INTERNAL | 13 | 2 | 15 |
| GREY | 2 | 0 | 2 |
| UNREACHABLE | 40 (8 comments) | 8 | 48 |
| TEST-SEAM | 1 | 51 | 52 |
| RULING | 0 | 0 | 0 |

By module (B): Crew 12, GameActions 1, Recording 20, TestCommands 51, Patches 6, Rewind 6, Ghost 6. Plus 7 (A) and 10 (B) cached-vessel comparisons the grep misses, decided in the audit tables.

Map-ghost checks (`IsGhostMapVessel|ghostMapVesselPids`, 191 hits, 151 rows, audit C): DRONE-SAME 64, DRONE-DIFFERENT 20, DRONE-NA 67. Vessel lifecycle subscribers: 12 subscriptions; the two needing real code are `OnVesselSwitchComplete` (drone arrival branch) and `OnVesselWillDestroy` (drone lost -> `OnDroneLost`); the rest take the combined early return.

Stock career (audit D): 49 contract parameter classes (SAFE 26, FENCE-COMPLETE 2, FENCE-UNDO 11, FENCE-BOTH 10) plus the system reset (FENCE-BOTH); 28 milestone nodes (SAFE 26, FENCE-COMPLETE 2). Fenced by section 10's 24 contract patch points and the 2 milestone fences.

The DRONE-DIFFERENT set (20 rows) is the design's real surface: the switch / force-switch / GoOffRails / loaded-inert patches (token, belt, inert twin), the icon click and orbit-line / arc / icon-drive patches (the drone draws none), `GhostOrbitDominantBodyPatch` (the drone needs real SOI handling), the pid set and its pre-register order, the save strip, the map snapshot `drone=` label, the targeting log label, and the guard contract comment.

### 11.2 Helper patterns

| Id | Helper | Sites | From |
|---|---|---|---|
| H1 | `GhostMapPresence.IsGhostOrDroneVessel(pid)`; drone in its own slot, set before `pv.Load` | the 64 DRONE-SAME rows, including delegate-passing sites (`SeedLoadedAnchorVessels`, `FilterCandidates`, `SurveyLiveLeftAloneCollisions`, `CountGhostVessels`) and the `RouteEndpointResolver` set arguments | C P1, P2, P3 |
| H2 | `FarWatchPlayerVessel.Resolve()` (Recording, so Crew, Ghost and Rewind call it without a backward layer edge; `[MethodImpl(NoInlining)]` core; falls back to `ActiveVessel` when no drone) | the PLAYER-SHIP rows still reachable mid-watch: `PredictedCrashReconcilerHost.cs:414`, `VesselSpawner.SourceIdentity.cs:251`, `VesselSpawner.EndOfRecording.cs:902`, `ParsekFlightSwitchContinuation.cs:1614`, `TimeJumpManager.Placement.cs:1205`, `ParsekFlightPlaybackHost.cs:1504`, `WatchModeController.cs:1859`, `ChainTipStaleVessel.cs:912/963`, `CrewReservationManager` swap family, `SpawnCollisionDetector.cs:1420` | A P3, B P2 |
| H3 | never record the drone: one predicate at `StartRecording` (`RecorderSession.cs:1203`, `FlightRecorder.StartRecording.cs:115`), `OnVesselSituationChange`, `OnStageActivate`, `HandleDeferredAutoRecordEva`, the post-switch arm, `PhysicsFramePatch.cs:81`, `DecideOnVesselSwitch` | 39 SKIP-WHILE-DRONE rows | A P1, B P3 |
| H4 | entry refusal on pending windows (8.3) | 22 split / placement rows | A P2 |
| H5 | recorded vessel, not active vessel, at every stop / resume / capture (`FlightRecorder.StopAndFinalize.cs:500/535/565`, `LiveSnapshot.cs:166`, `ParsekFlightSplitBrancher.cs:1513`) via `FlightRecorder.ResolveRecordedOrFallbackVessel` (the recorded vessel by pid; the active vessel only when the recorded one is gone AND the active vessel is not the drone) and, at a resume, `ShouldResumeOnRecordedVessel` (stay on the recorded vessel while the drone is active). A latent defect independent of the drone; fixed in step 2 with its own todo entry (STOP-CAPTURES-ACTIVE-NOT-RECORDED-VESSEL) | 5 | B P1 |
| H6 | one teardown (7.5) | `WatchModeController.cs:3939`, `ParsekFlight.RetargetStockCamerasBeforeTimelineGhostCleanup`, `GetActiveVesselSafe` fallbacks | A P5 |
| H7 | seam refusal: a `FarWatchActive` bit in `BuildDispatchState` refuses every vessel-mutating or scene-changing verb with `far-watch-active`, except `SaveGame` to a name other than `persistent` / `quicksave` (9.2, FW-1) and `WarpToUT` (a real rails warp through `TimeWarp.SetRate`, bounded by the 8.4 limits; step 3, for FW-2 and FW-4); `RecordingState` reports `dronePid` and `playerShipPid` | 52 TEST-SEAM rows | B P4 |
| H8 | lifecycle handler shape: every vessel GameEvent handler calls `GhostMapPresence.IsGhostOrDroneVessel` (or `CameraDrone.Is`) and returns for a hit, two real branches (H8a switch arrival, H8b drone lost); keep the drone packed at each switch | C section 2 rows 1-12 | C P6, B P5 |
| H9 | career fences and the award scope (section 10) | D's 22 vessel-reading classes (24 patch points), reset, 2 milestones, the belt | D P1-P5 |

### 11.3 Ruled-site decisions this note takes

- End-of-recording spawns during a far watch (A Q1, C Q5): run as today, because the ruled "a ghost that ends as a real ship keeps the camera" needs the watched ghost's own spawn. The spawn appears near the drone and stays packed under the hold. `DeferredActivateVessel` (`ParsekFlight.cs:3056`) does not move focus while the drone exists (ruled: no control of it without a normal switch). The collision scan excludes the drone (H1) and protects the parked ship (H2).
- No active tree (A Q2): far watch is allowed; H3 and the skip of `TryRestoreCommittedTreeForSpawnedActiveVessel` while the drone is active cover it, and the return is an ordinary switch.
- Real Spawn Control proximity scan (`ParsekFlightSpawnHost.cs:772`): skipped while the drone exists; velocity samples kept.

### 11.4 Source gate

The gate reads the compiled assembly, not source text: CLAUDE.md forbids deriving a guarded set by regex over source, and a text scan misses wrappers and cannot read an expression-bodied forwarder. It is a Python test in `scripts/arch` over the facts `scripts/arch/ilfacts.py` already builds (members keyed `Type::Name(params)`, compiler-generated code folded into its source method, each method's in-assembly calls, external members and static field access), so it runs in CI's semantic step with the other IL ratchets (`PARSEK_ARCH_IL`, Debug build; a local run needs a Debug build first).

**Site gate** (`scripts/arch/test_far_watch_sites.py`, allowlist `scripts/arch/far_watch_sites.toml`):
- A site is a production method (outside `InGameTests/`) that names `FlightGlobals::get_ActiveVessel`, `FlightGlobals::activeVessel` (the `fetch.activeVessel` read), `GhostMapPresence::IsGhostMapVessel` or the static `GhostMapPresence::ghostMapVesselPids`, or calls a declared reader wrapper. The wrapper list starts with `WatchModeController::GetActiveVesselSafe()`, `WatchModeController::ReadActiveVesselCore()` and `PredictedCrashReconcilerHost::ReadActiveVesselCore()`; the gate reds on any production method returning `Vessel` whose own body names `get_ActiveVessel` and that is not on the list or itself decided, so a new wrapper cannot hide reads.
- `ilfacts.py` gains per-member occurrence counts (`externalMemberCounts`, `callCounts`; it already walks every instruction), so an entry pins how many reads a method has, and a new read in a method that already has a decided one reds.
- Entry: `"<Type::Method(params)>" = { pattern = "<ActiveVessel|IsGhostMapVessel|ghostMapVesselPids|wrapper>", count = N, codes = ["<CODE>", ...], audit = "<A|B|C row>" }`, codes from the vocabulary of 11.1.
- Reds on: a site with no entry; an entry naming no site (stale or renamed); a count that differs; an unknown code; `RULING`. A moved line, a reformatted method or a changed line number does not red; a renamed method does, which is the point.
- Seeding (step 2, `scripts/refactor/seed_far_watch_sites.py`, a one-off): reads each audit row's `file:line` and snippet, checks the snippet against that line at `ba7cd9eb`, maps it to its enclosing method with a brace walk at that commit, and sums rows per method and pattern. Its output is accepted only when every seeded count equals the IL count of a Debug build of `ba7cd9eb`; a mismatch names the method to map by hand. After seeding, entries are keyed by member, never by line. The implementer updates an entry in the same commit as the method it changes (a swap to `IsGhostOrDroneVessel` drops that pattern's count; a PLAYER-SHIP read replaced by `FarWatchPlayerVessel.Resolve()` likewise).

**Handler gate** (same file), narrowed to what IL can prove: every handler subscribed to a vessel GameEvent calls the H8 guard. A candidate handler is a Parsek method taken by `ldftn` in a method that names a vessel `GameEvents` field (`onVessel*`, `onNewVesselCreated`, `onStageActivate`, `onPartDie`, `onCrewOnEva`, `onCrewBoardVessel`, `VesselSituation.*`). Each candidate must call `GhostMapPresence::IsGhostOrDroneVessel` or `CameraDrone::Is` (folded generated code included), or be on the committed list in the same TOML: the real branches (`OnVesselSwitchComplete`, `OnVesselWillDestroy`), `KerbalsWindowUI.OnCrewVesselChangeEvent`, and handlers of non-vessel events that share a subscribing method, each with a reason. The IL cannot show that the guard is the first statement, so the contract is "calls it"; the per-handler early return is checked by review and by the in-game `DroneHasNoPresence` test.

---

## 12. Map / UI Artefacts and Greyed Controls

The no-new-UI rule (`.claude/CLAUDE.md`, Hard rules) applies in full. The only means used: greying a stock control with stock's own tooltip / reason field (the stock-control annotation exception, D1), wording in an existing Parsek hover tooltip inside `TooltipEchoBudgetTests`' per-window budget, the existing watch overlay, and one `ParsekLog.ScreenMessage` per event that changed something (the forced return, the snap-back; both ruled).

### 12.1 Artefacts

| Where | Stock behaviour | Fix | Proof |
|---|---|---|---|
| Map icon, label, orbit line, Ap / Pe | the active vessel's line is drawn by its patched-conic renderer | discovery None (icon, label, TS row); patched-conic solver detached (line, nodes) | L1.2, L7.1 |
| Map camera on open, Backspace, after each switch | targets `ActiveVessel.mapObject` (`PlanetariumCamera.cs:403`, `MapView.cs:541`, `onVesselSwitching`) | one `PlanetariumCamera.SetTarget` redirect: a target that is the drone's mapObject becomes the watched ghost's map object (or its marker's body). Required: without it the proof read the drone as the map target after every switch-in (7.1 step 6) | L7.1 |
| Map camera fallback when the watched map ghost dies | `GhostMapPresence.CameraRetarget.cs:141` falls back to `active.mapObject` | treat the drone's mapObject as absent | unit |
| Tracking Station | lists saved vessels | nothing (stripped from every save) | FW-1 save parse |
| Vessel labels, `[` `]` | nearby loaded vessels | Debris excluded by stock; `VESSEL_SWITCHING` locked; far watch owns `[` `]` before `VesselCycleReFlySwitchPatch` (B 245) | in-game |
| Navball, altimeter, speed | read the active vessel | the drone carries the ghost's attitude and velocity | L7.1 |
| Signal indicator, link lines | `vessel.connection` | Debris has none (`TelemetryUpdate` blank) | L1.1 |
| Staging stack, resource panel, crew portraits | rebuilt for the drone (empty) | hidden during a far watch (ruled), by the stock UI's own show / hide of those panels | L7.1 |
| Stock Alarm Clock "add alarm for this vessel" | would bind an alarm to the drone | the add buttons for vessel-bound alarm types greyed with stock tooltip "Stop watching first"; any alarm still naming the drone at Teardown is deleted with one Info line | in-game |
| Target selection | sets the drone's target | dropped with the drone (logged); the ship's own target is restored by stock `ResumeTarget` on hand-back | in-game |

### 12.2 Greyed and redirected controls

| Control | During a far watch | Reason text (hover) |
|---|---|---|
| Pause menu Save, Load, Revert, Space Center, Tracking Station, Main Menu, Quit | greyed, stock `tooltipText` | "Stop watching first ([ or ])" |
| F9 (quickload) | refused: input-locked (`QUICKLOAD`), and stock's own Load control in the pause menu is greyed with the hover line (ruled "refused, with a hover line"; a key has no surface to hover, so the line sits on the control that does the same thing) | "Stop watching first ([ or ])" |
| F5 / Alt+F5 | input-locked | (none: a refused key press changed nothing) |
| Altimeter return-to-KSC | greyed | same |
| Real Spawn Control, Warp to (`ParsekFlightSpawnHost.cs:1020`, `:1161` plus their keybinds `:1323`, `:1325`), Rewind, Fast-forward, Re-Fly, the record button | greyed in their existing Parsek windows, each with a hard refusal at the entry point reading the same predicate | "Not available while watching far away" |
| Missions window, a mission's Watch button (flight) | **Watch** when a vessel of the mission is flying, at any distance or body (ruled); starts a near or far watch on it as the distance decides; greyed with the safety reason when a far watch would be needed and `Admission()` refuses; W* while watching, click to stop | the safety reason below |
| W buttons (Recordings, Timeline, group headers), map Watch entry | enabled for any flying ghost when `Admission()` allows; greyed with the safety reason otherwise | "Your ship must be landed, on the pad, or in an orbit clear of the atmosphere to follow this far" (ruled), or stock's ClearToSave text |
| Map Switch To / double-click on a real vessel, alarm Jump / Switch To | the click is taken as "end the watch, then switch" (ruled) | none needed |
| Watch overlay | unchanged in form; adds the return time when there is one, and the reason warp is held below the requested rate (8.4) (ruled) | n/a (overlay text) |

Every greyed Parsek control ships with its click-block (D1: "a mark on a live control misleads"). The new hover strings (the safety reason, "Not available while watching far away") are added to the `TooltipEchoBudgetTests` rows of the windows that carry them (Recordings and Missions are single-line windows; each is checked against its budget in step 3 and shortened there if it does not fit).

As built (step 3): `[` / `]` end a far watch from `ParsekFlight.HandleInput`'s raw key poll, the same place today's near-watch exit reads them (stock's vessel cycling is input-locked while the drone holds focus), so `VesselCycleReFlySwitchPatch` is unchanged. Map Switch To and the alarm Jump end the watch before any of their gates runs: `MapFocusObjectOnSelectPatch.PrepareOwnedVesselSwitch` asks `FarWatchDecisions.DecideOwnedVesselSwitch` first, so the Re-Fly refusal, the pre-switch Merge / Discard dialog (Case B on an unloaded target) and the intent arm never read the drone as the active vessel; it hands the far watch its own gated replay (`ReplayOwnedVesselSwitch`: the same gates, then stock's `SetActiveVessel`; a map Switch To also keeps stock's `CanSwitchVesselsFar` rule, which `FocusObject.OnSelect` applies after the gates: with the rule off the replay posts stock's own `#autoLOC_465647` message and switches nothing, while the alarm Jump and the token replays, whose stock paths never read the rule, do not read it), which runs after the hand-back with the ship active (`FarWatchDecisions.DecidePostTeardownSwitch`: replayed through the gates, never a bare switch; dropped when the target is gone or already active). Any other switch the drone's token refuses in `Patches/CameraDronePatches.cs` takes the same gated replay. The W buttons (Recordings rows and group headers, Timeline) and the Missions Watch button read one decision with their click (D1): `ParsekFlight.IsGhostWatchableAtRange` and `ParsekFlight.EnterWatchMode` both ask `DecideFarWatchRequest`, whose far start reads `FarWatchController.EvaluateFarStart`, the entry refusals of 8.3 (a kerbal on EVA, a drone already up, a pending window, a Re-Fly) first and then the ship's safety verdict, the same predicate `TryStart` refuses by. A greyed W shows that refusal's reason as its hover, and the Missions Watch button shows the first refused member's reason. The safety check reads `FlightGlobals.ClearToSave(false)`: the no-argument overload posts stock's refusal as a screen message, from the buttons' poll. Parsek has no player record button (recording starts on its own), so there is nothing to grey there.


As built (step 4): the body term is gone from every Watch entry. The map ghost menu's Watch (`GhostMapWatchHelper.ResolveLiveWatchAction`, the label the menu draws and the click `HandleWatchRequest` takes read it together) is a plain `Watch` for a ghost at any distance or body: enabled when a near watch takes it or `DecideFarWatchRequest` allows the far start, greyed otherwise with the reason in the button's stock `tooltipText` (the far refusal, or "The ghost is in another SOI" / "The ghost is out of watch range" when no far watch is available, outside FLIGHT or with no controller); the old Too Far and Other SOI labels are gone. The W buttons and the Missions Watch button read `ParsekFlight.IsGhostOnWatchableBody` (pure `WatchModeController.ResolveWatchBodyAffordance`: same body, or another body with a far watch available), and the Missions button picks its member through the pure `MissionsWindowUI.PickMissionWatchTarget`. `ResolveWatchSameBodyDecision` (E5) now answers only for the near watch; the far watch's direct start and its hand-back (`FarWatchDecisions.IsWithinNearWatch`: on the ship's body and under 300 km) read the ghost against the SHIP's body while a watch runs (`ParsekFlight.IsGhostOnShipBody`), and a far-watch entry does not reset a looping ghost's phase (`ShouldResetLoopPhaseForWatch`, `farWatchEntry`). Takeover from the near watch is body-free (the near watch is same-body by construction). `FarWatchEndReason.GhostLeftBody` and the `ghost-other-body` entry refusal are removed; `RefusedOtherBody` became `RefusedNoReading` (no finite distance this frame).
---

## 13. Edge Cases

Grouped; each says the scenario and the behaviour.

**Timing and windows**
1. **Split, decouple or joint break in the same frame as the start.** Refused by 8.3 for that frame; the next request succeeds. Takeover retries once per real second up to the near-watch exit, then snaps back with the reason.
2. **Esc pause mid-watch.** No `FixedUpdate` runs while paused, so the hold cannot lapse (audit E). Pause menu greyed (9.3).
3. **Warp hard toward the return point.** The warp ceiling (8.4) drops warp so the return starts with at least 10 s of real time; the return itself runs at 1x.
4. **A maneuver node of the ship comes due.** Return 180 s before it (ruled).
5. **Ghost hovering near 300 km.** Takeover at 290 km, hand-back at 250 km: no flip-flop (ruled 50 km slack).
6. **W on a ghost close to the ship during a far watch.** Hand back, then near watch (ruled).
7. **Watched ghost hidden above 50x warp** (`WarpThresholds.GhostHide`, ruled kept). The drone keeps following the recording trajectory, read directly and not through the engine, which stops resolving a hidden ghost (6.3 step 1; the proof lost it on every tick at 100x); the camera follows the drone's position on the ghost's path.

**Ship and nearby vessels**
8. **Ship landed but sliding / on a ladder / throttled up / in air.** Refused with stock's own reason (8.2 rule 3, 5).
9. **Suborbital ship above the air.** Allowed with a return point; refused if under 600 s (ruled).
10. **Ship's path crosses into another SOI on rails.** Already in Parsek's path walk (8.1); stock moves the ship on rails; the ship's `onVesselSOIChanged` recomputes the path and the verdict. A genuine Flyby / Escape award the ship earns on the way is booked (10.3).
11. **Docking capture in progress.** Refused (ruled).
12. **Crewed ship nearby in air.** Refused (ruled); spent stage allowed and falls (ruled).
13. **Ship was spinning or a kerbal stood on a slope.** Rails stop the spin; control is handed back before unpack (ruled).
14. **Ship destroyed while away.** Owner ruling V2 (2026-11-27): stock's flight-ended screen.
15. **Re-Fly in progress.** Refused (ruled).

**Ghost and spawns**
16. **Watched ghost ends with no ship.** The camera holds a few seconds as today, then HandoverBack (ruled).
17. **Watched ghost ends as a real ship.** Spawn runs near the drone; the camera stays on it, packed, until the player stops; no focus move (ruled; 11.3).
18. **Watched ghost is a loop anchored to the player's ship.** It plays relative to the ship, so it is within range by construction; far watch is never offered for it (the anchored-loop ghost is destroyed if its anchor unloads, audit C Q7b).
19. **Ghost hops to another body.** Body follows `getMainBody` (6.3); one dominant-body change, PQS builds at the new body; a hitch is accepted (ruled "no loading screen", not "no pause"). The rotating frame is set explicitly at the jump (6.3 step 2). Proof FWP-1: one `onDominantBodyChange` at the Kerbin-to-Mun crossing, zero drone `onVesselSOIChanged`, a 71.7 ms frame (the Duna move in FWP-2 cost 364 ms). As built (step 4): the hop is always the mover's write, never stock's, because stock's dominant-body check runs before the mover each tick and is refused for the drone (6.3 as-built note).
20. **Ghost passes a real station.** The station loads packed under the hold; the docking ghost is watched against a frozen station (ruled).
21. **Landing ghost over terrain, at any warp.** Drone at least `floor` above terrain (6.3, sized for the next tick's coast); zero `crashed through terrain` lines (L4.1). Proven at 1x only (FWP-1 Mun landing: 158 and 182 floor clamps, minimum 20.00 m above terrain, zero terrain kills); warp was refused in the proof (8.4).
22. **Another ghost ends as a real ship right beside the resting ship.** Surface spawns are offset from the ship (existing collision code, with H2); an orbital spawn beside an unloaded ship has no overlap check today: proof item.

**Saves and scenes**
23. **Autosave or a stock flight-end save while the drone exists.** Strip and repair (9.1); the PARSEK node is the outsider shape, which loads through the vessel-switch restore (9.4).
24. **Scene change requested by a mod or a stock path not greyed.** It cannot be refused and the exit save is already written (stripped by `OnSave`, index on the ship). `OnSceneChangeRequested` runs the scene-exit teardown with no switch (7.5); the drone ids stay set for the strip until the next save or load; the next scene never sees a drone. A FLIGHT -> FLIGHT change within 60 frames of a hand-over is not read as a vessel switch, because Parsek's hand-overs never arm `vesselSwitchPending` (7.1 step 3).
25. **Game crash mid-watch.** Nothing persisted names the drone unless a save happened, and any save strips it; the next load is ordinary.
26. **Harness seam call during a far watch.** Refused `far-watch-active` (H7) except the read verbs, the far-watch verbs themselves, `WarpToUT` and the named `SaveGame` FW-1 uses.

**Input**
27. **Alt+F12 teleport of the drone.** Logged, overwritten next tick (6.3).
28. **Stock Recover.** Never offered (drone never landed); `OnVesselRecoveryRequested` returns for the drone as a belt.
29. **Space pressed.** Staging locked; `OnStageActivate` returns for the drone.

---

## 14. What Doesn't Change / Out of Scope

### 14.1 What doesn't change

- Near watch below 300 km on the same body, its camera, overlay, W cycle and exits, whenever the ship is not safe or the ghost is close.
- Recording format, schema generation, tree format and every save format: nothing new is persisted.
- Ghost playback, zones and render distances: the engine already measures from `ActiveVessel`, which becomes the drone (CAMERA-OK rows); ghosts near the camera appear with no new rule (ruled).
- The background recorder's on-rails contract.
- The merge dialog, Revert to Launch, Discard: the flight stays open (ruled).
- Contract deadlines, offer generation, strategies.
- Parsek's existing ghost limits.

### 14.2 Out of scope

- Mission Rewatch mode, its world builder and its save restore (step 5 note).
- Watch lanes using far watch (step 7).
- Modded installs and other mods' per-vessel add-ons (after step 7).
- Keeping an unsafe ship alive by any trick (ruled: no honest way).
- A "warp to next event" control.

---

## 15. Drone Properties Mission Rewatch Will Rely On

The step 5 note may assume, without re-deriving:
1. The drone can be built at any body, at any state, with no player ship to spawn beside (the build takes a state vector, not a vessel).
2. The drone can be the only vessel in a flight scene and the focus vessel of a freshly loaded flight state (it is a valid ProtoVessel: Debris, discovery None, non-landed).
3. Every fence and guard keys on `CameraDroneIdentity` (exists / active), never on "a player ship exists", so contracts, milestones, recorder and ledger capture stay fenced with no ship.
4. The save strip removes the drone and fails closed when no replacement target exists; rewatch will instead never write a save (its own gate).
5. The drone mover follows any ghost by recording state and effective UT, loop-mapped or not, across SOI changes, above terrain.
6. The single teardown is reusable with a different "back" target (rewatch leaves by restoring the return point, not by HandoverBack).
7. The site gate's codes already cover the drone as the only active vessel; rewatch adds a mode bit, not new codes.

---

## 16. Diagnostic Logging

### 16.1 Tags

| Tag | Owns |
|---|---|
| `[FarWatch]` | state machine: every transition, request, refusal, return point, warp ceiling |
| `[CameraDrone]` | build, load, identity, hold renewal, orbit writes, SOI, terrain floor, strip, destroy |
| `[FarWatchSafety]` | admission and re-check verdicts, path endings |
| `[FarWatchFence]` | contract, milestone and ledger-capture fences |
| `[CameraFollow]` (existing) | camera re-apply and restore around the switches |

`scripts/arch/log_tags.json` is refreshed with `logtags.py --write` in the step that adds them.

### 16.2 Events

- **Transitions (Info, one line each):** `FarWatch transition from=<s> to=<s> reason=<r> ghostRec=<id> ship=<name> shipPid=<pid> dronePid=<pid> ut=<ut>`.
- **Admission (Info on change; InfoRateLimited with the reason in the key for the greyed buttons' poll):** `verdict=<k> reason=<r> path=<Final|Impact|Truncated|Failed> segments=<n> dangerUT=<ut|inf> returnUT=<ut|none> exposed=<n> refusedBy=<pid|none>`; a recomputation logs its trigger (`soi`, `orbit-delta`, `node`, `alarm`, `drift`).
- **Build / load / destroy (Info):** pid, guid, part, body, distance to ship, frames to load.
- **Switch (Info):** `ForceSetActiveVessel target=<drone|ship> loaded=<b> packed=<b> result=<b>`; the recorder's `onVesselSwitchComplete transitioned old recorder to background` line is pinned by FW-1.
- **Per tick (VerboseRateLimited, shared key):** writes / skips / floor clamps / invalid states as batch counters, one summary per 5 s: `ticks=<n> writes=<n> skips=<n> floorClamps=<n> maxDevM=<x> holdRenewals=<n>`.
- **SOI (Info):** `drone body <from> -> <to> recordingBody=<b> match=<b>`; at a cross-body jump also both bodies' `inverseRotation` flags and `RefFrameIsRotating` before and after the frame is set (6.3 step 2).
- **Warp limit (InfoRateLimited, the rule in the key):** `warp limit source=<ghost|ship> rule=<stock reason|ceiling> requested=<r> granted=<r>` (8.4).
- **Camera (Info / Warn):** each same-frame re-target on a watch exit, and a Warn from the per-frame net naming the ghost the camera was under (7.5).
- **Strip (Info):** `activeVesselIdx <old> -> <new> ghostsStripped=<n> droneStripped=<b>`; Error on the fail-closed path.
- **Fences (VerboseRateLimited per patch point):** `fenced <Type.Method> vessel=<pid> droneActive=<b>`; one Info line at Harmony setup listing resolved / missing patch points; Warn for a drone-attributed award at the capture (10.3), naming node or parameter and the stamped vessel.
- **Refused switches (InfoRateLimited):** `refused <SetActiveVessel|ForceSetActiveVessel> target=<pid> source=<stack hint> while far watch`.
- **Screen messages:** the two ruled events log their text at Info.

---

## 17. Test Plan

### 17.1 Unit tests (xUnit, headless)

- `FarWatchShipSafetyTests`: one cell per rule row of 8.2 (each refusal, each allowance, the 600 s and 120 s bounds), path endings (closed orbit Final, escape Final, Impact, Truncated counts as danger, Failed refuses), the early-career Mun free return with a 30 km Kerbin periapsis (8.1, with no stock solver in the inputs), SOI then dip, airless body terrain, exposed set (crewed vs spent), node and alarm lead, the re-check triggers and the two warp-ceiling bounds. Mutation: flip `>=` to `>` on `h_safe`, drop rule 6, treat Truncated as Final; each reds.
- `FarWatchDecisionsTests`: takeover 290 km, hand-back 250 km, other-body exemption, W on a near ghost while far, warp ceiling `r * 10 s`.
- `FarWatchWarpLimitsTests`: the transcribed stock rules of 8.4 against ghost inputs: a landed ghost allows on-rails warp, a ghost in air gets LOW warp up to 4x with `NOT_IN_ATMOSPHERE`, a low ghost on a crashing conic walks down with `NOT_WHILE_ABOUT_TO_CRASH` in `PeAltitude` mode and by altitude in `VesselAltitude` mode, no ghost uses the ship's inputs, and the result never exceeds Parsek's ceiling.
- `FarWatchTransitionTests`: the transition table of 4.2 as data, every (state, trigger) pair including the illegal ones.
- `ResolveActiveVesselIdxAfterStripTests`: F's table plus drone cells (drone active, ship before / after it, ship missing -> fail closed, ghosts and drone together). Mutation: fall back to 0; reds.
- `CameraDroneFloorTests`: the 6.3 floor at 1x / 100x / 1000x on Kerbin and the Mun, descending and climbing, and the warp cap that keeps it under 1 km; the surface-held state beside a landed ship.
- `VesselSwitchFlagTokenTests`: `TryArmVesselSwitchFlag` does not arm under the far-watch switch token and arms as today without it.
- `CameraDroneIdentityTests`, `IsGhostOrDroneVesselTests`: pid and guid both required; `IsGhostMapVessel` unchanged for the drone.
- `FarWatchFencePredicateTests`: each patch point's predicate at start (drone loaded, ship active), during, at end (ship loaded, drone active), after Die.
- `FarWatchAwardScopeTests`: a stamp naming the drone is not booked (Warn); a stamp naming the parked ship is booked mid-watch (a Flyby on rails); a records node while the drone is active is not booked; a polled-class contract completion while the drone is active is not booked; the belt runs after the `IsReplayingActions` return.
- `CameraDroneBuildNodeTests`: the ProtoVessel node (type Debris, DISCOVERY None with infinite `unobservedLifetime` and `fadeUT`, flags, part, name, no crew), and the build offset (orbit normal in flight, straight up beside a landed ship).
- Log-capture cells for each transition line, the strip line and a fence line.

### 17.2 Source gates

- `scripts/arch/test_far_watch_sites.py`: the site gate and the handler gate (11.4), in CI's semantic step; the `ilfacts.py` count extension gets its own cell in `test_ilfacts.py`.
- Architecture: `lifetimes.toml` entry for `CameraDroneIdentity` (`load-transition`, cleared by the Cold load closure); no `layer_pins.toml`, `headless_pins.toml` or `[crossTypeStaticWrites]` change is expected (section 3), and a red there means a placement mistake, not a re-pin; the `facts_pins.toml` complexity watch band and the uncalled-method list are re-pinned if the new methods move them; `test_saveparse.StockLoadabilityTests` gains the drone signature.
- `TooltipEchoBudgetTests` rows for the new hover strings.

### 17.3 In-game tests (category `FarWatch`, FLIGHT)

- `DroneTakesFocusAndGivesItBack`: build beside the active vessel, switch, assert packed, camera on target, switch back, drone gone, active vessel restored.
- `DroneHoldStaysOn`: 600 ticks, `packed` every tick, a nearby loaded vessel packed.
- `DroneHasNoPresence`: no icon, no orbit renderer drawing, no label, not in `[` `]` candidates, not targetable, `connection == null`, no collider hit by a raycast.
- `NavballReadsGhost`: drone attitude and speed against the ghost state within tolerance.
- `TerrainUnderGhost`: a landed ghost, drone above `terrainAltitude + 20 m`, PQS collider under the ghost.
- `SaveDuringDroneStripsIt`: `Game.Updated()` while the drone exists, and in the frame right after a hand-over (`!FlightGlobals.ready`): the flight state has no drone and names the ship.
- `ContractFencesResolve`: every one of the 24 contract patch points and the 2 milestone fences resolved and patched.
- `CameraSurvivesGhostRetire`: with the drone active and the camera on a ghost, exit watch mode with `skipCameraRestore: true` and destroy that ghost in the same frame and in a later frame; `FlightCamera.fetch` stays non-null and its target is the drone (7.5). A second cell parents the camera under an unowned ghost and asserts the per-frame net moves it off.
- `WarpFollowsGhost`: drone surface-held above a landed ghost, on-rails warp is granted; over a ghost in Kerbin's air, LOW warp up to 4x; both under the hold, with no vessel unpacked (8.4).
- Each `Skip`s naming its required context when the scene cannot provide it.

### 17.4 Automated lanes (operator PC, ids provisional; check open PR branches before claiming)

| Lane | Step | Flow | Verifiers |
|---|---|---|---|
| FW-1 drone swap | 2 | orbit fixture: seam `FarWatch start` on a ghost with the drone beside the ship, seam `SaveGame name=fw1-probe` during the watch (the one save H7 allows, 9.2), `FarWatch stop`, `LoadGame fw1-probe` | saveParse of `fw1-probe`: no drone VESSEL, `activeVessel` = the ship by pid, PARSEK active tree with `ActiveRecordingId` absent and the ship in `BackgroundMap`; log contract after the load: the `pending-LimboVesselSwitch` stash line with `activeRecId=<null>`, the `armed tracked vessel` restore line for the ship, transition lines, background line, zero fence Warn; ledger oracle: career unchanged; analyzer: the ship's recording one id |
| FW-2 far follow | 3 | Kerbin ascent ghost followed to 1000 km and back | log contract: takeover at 290 km, hand-back, drone destroyed; ledger oracle; analyzer: the ship's recording one id, background segment present. As built in step 3 (`FW-2-far-follow-hand-back`, flown green 2026-11-28): W1's host, where the ghost only approaches the landed pod, so the lane flies a direct follow start at 1,069.7 km, `WarpToUT` under the ghost's limit and the 250 km hand-back with the near watch resuming; the 290 km takeover, the ship's recording and the ledger oracle are owed a host whose ghost leaves the ship |
| FW-3 refusal in air | 3 | ship flying at 20 km | log contract: `verdict=Refused reason=NOT_IN_ATMOSPHERE`, the seam's `unsafe:NOT_IN_ATMOSPHERE` refusal; no drone built. Built (flown green 2026-11-28) on `kerbin-splashdown-pod-in-air`, W1's host with the capsule lifted to 25 km and the clock moved to UT 21560, 24 s before the loop anchor, so the loop enabled first keeps the anchor and the ascent ghost plays with no jump (`harness/tools/build_far_watch_pod_fixtures.py`). The snap-back line and screen message need a near watch taken over at 300 km while the ship flies, which this host cannot give |
| FW-4 forced return | 3 | suborbital ship above 70 km | `returnUT` logged; hand-back before entry; ship alive. Built (flown green 2026-11-28) on `kerbin-splashdown-pod-suborbital`, W1's host with the capsule on a 60 x 106.4 km arc that reaches 71 km at UT 22490 and the clock moved to UT 21560: the watch on the coast ghost starts at 21770 (720 s before the crossing), returns at about 22190 under `WarpToUT`. Ship alive is read from the log (no `ShipLost`); the save-parse row has no vessel facet for a sandbox save |
| FW-5 Mun from the map | 4 | map Watch on a Mun transfer ghost | SOI line, dominant-body change, zero drone `onVesselSOIChanged` |
| FW-6 landing terrain | 4 | Mun landing ghost | zero `crashed through terrain`, floor clamps counted |
| FW-7 contracts untouched | 4 | career with an active "reach the Mun" / orbit contract, far watch at the Mun | ledger oracle + saveParse: contract parameters byte-equal before / after; zero fence Warn; Rendezvous milestone absent |
| W1 rewritten | 3 | the existing `W1-watch-distance-cutoff` | its pinned `max 300km` token now applies only when the ship is unsafe; the lane flies an unsafe ship. As built in step 3, W1 needs no change: the seam's `EnterWatchMode` stays today's near watch (the far path is `FarWatch op=start`), so its probe A still reads the 300 km refusal on the landed pod; a player W-key probe of the refusal would need an unsafe ship |

The step 2 seam grammar: `FarWatch op=start (index=<n> | tree=<id|handle>) [mode=follow|hold]`, `op=stop`, `op=status` (`TestCommands/TestCommandFarWatch.cs`). Start answers OK at Following (ERROR `far-watch-ended:<reason>` when the watch fell back to Idle first), stop at Idle, status at once. `mode=hold` builds the drone beside the ship and hands over to it without entering the near watch or moving it, so the swap, the save strip and the hand-back can be driven with the ghost at any distance or body; step 2's `mode=follow` (the default) needs the ghost on the ship's body (entry refusal `ghost-other-body`). FW-1's ghost is on Kerbin and the ship at Ike, so FW-1 uses `mode=hold`. Step 3: a `mode=follow` start on a ghost past 300 km starts the far watch directly (12.2); every start passes the ship-safety check and refuses `unsafe:<reason>`; `op=status` (and both terminals) add `returnUT` (`none` without one), `returnCause` (`None`, `Danger`, `ManeuverNode`, `Alarm`) and `warpLimit` (`return`, `floor`, `stock` or `none`). Step 4: the grammar is `FarWatch op=start (index=<n> | tree=<id|handle>) [mode=follow|hold] [via=seam|map]`; `mode=follow` works at any body (the `ghost-other-body` refusal is gone, and `tree=` picks the lowest in-scope live ghost at any body), and `via=map` (follow only, `via-map-needs-follow`) presses the map ghost menu's Watch on the ghost's map vessel through `GhostMapWatchHelper.HandleWatchRequest`, after logging `farwatch map entry index= action= label= enabled= reason=`; it refuses `map-vessel-missing`, `map-entry-refused:<reason>` (the entry is not a plain enabled Watch) and `map-entry-not-far` (the press started a near watch, not a far one).

FW-1's save path, and why: the seam `SaveGame` during the watch runs exactly the path every residual save takes (`GamePersistence.SaveGame` -> `Game.Updated()` -> `OnSave` strip and repair), and its file loads back into FLIGHT, so one lane proves the strip, the index repair and the vessel-switch restore. Forcing the stock autosave would test a coroutine the design stops, and the scene-exit save lands in the Space Center, which proves the strip but not the load into the ship. The seam carve-out is automation-only (armed by `PARSEK_TEST_COMMANDS=1`) and refuses `persistent` and `quicksave`.

V7M / V14M quote the `activeBody=` eligibility line; their log contracts are updated with `WatchModeController.cs:684 / 946`.

### 17.5 Mutation checks

Each gate is broken on purpose once and must red: the safety rule 3 (ClearToSave ignored), rule 6 (exposed crewed vessel ignored), a Truncated path read as Final, the strip repair (index left, or index trusted while `!ready`), the Rendezvous `v is drone` test (removed), the `ResetContracts` fence (moved to `OnVesselChange`, which FW-7 and the fence-predicate cell must catch), the award belt keyed on "drone exists" (the Flyby-on-rails cell must red), the H3 arm skip (removed: FW-2's analyzer must see a new tree), a site gate entry count lowered by one, the warp patch fed the drone's state instead of the ghost's (the landed-ghost cell must red), the same-frame camera re-target on a skip-restore exit (removed: `CameraSurvivesGhostRetire` must red).

---

## 18. Implementation Phasing

| Step | Delivers | Files touched |
|---|---|---|
| 1 (throwaway, never merged) | The proof, below | a branch with a debug-only `CameraDrone` and seam verbs |
| 2 Camera drone (on top of PR #2842) | identity, build, hold, move (same body, surface-held beside a landed ship), hand-over out and back, single teardown and scene-exit teardown, drone arrival branch, save strip + repair, autosave / quicksave off, career fences and award scope, the site decisions and IL gates, H5 fix. No player-visible change | new: `Core/CameraDroneIdentity.cs`, `Controllers/CameraDrone.cs`, `Controllers/FarWatchController.cs`, `Recording/FarWatchPlayerVessel.cs`, `Patches/CameraDronePatches.cs`, `Patches/FarWatchCareerFencePatches.cs`, `GameActions/FarWatchAwardScope.cs` + `Patches/FarWatchAwardScopePatch.cs`, `TestCommands/TestCommandFarWatch.cs`, `scripts/arch/test_far_watch_sites.py`, `scripts/arch/far_watch_sites.toml`; changed: `scripts/arch/ilfacts.py` (counts), `Ghost/GhostMapPresence.cs`, `GhostMapPresence.ProtoVesselLifecycle.cs`, `Ghost/GhostMapPresence.SaveActiveIndex.cs`, `Controllers/ParsekScenario.Save.cs`, `ParsekScenario.SceneTransitions.cs` (switch flag), `Controllers/ParsekFlight.cs` (FixedUpdate, OnVesselWillDestroy, OnDestroy), `Controllers/WatchModeController.cs` (the same-frame camera re-target on every watch exit while the drone exists, 7.5), `ParsekPlaybackPolicy.cs` (`ExitWatchModeForHandoff`), the `SetVesselTarget` refusal in `Patches/CameraDronePatches.cs`, `ParsekFlight.SwitchContinuation.cs`, `ParsekFlight.RecorderSession.cs`, `Recording/FlightRecorder.StartRecording.cs`, `FlightRecorder.StopAndFinalize.cs`, `FlightRecorder.LiveSnapshot.cs`, `Patches/PhysicsFramePatch.cs`, `Patches/GhostVesselLoadPatch.cs`, `Patches/CheatTeleportPatch.cs`, `Patches/FlightStateGhostBudgetPatch.cs`, `Patches/ProgressRewardPatch.cs`, `Patches/ContractParameterRewardCapturePatch.cs`, `GameActions/GameStateRecorder.Handlers.cs`, the DRONE-SAME sites (every one left in the site gate swapped, `ParsekFlight.OnVesselWillDestroy` with the drone branch first; `ShouldMakeInert` stays ghost-only because the drone is made inert ahead of it), the PLAYER-SHIP sites (on `FarWatchPlayerVessel.Resolve()` except two kept on the active vessel, each with its reason in `far_watch_sites.toml`), the seam dispatcher, `scripts/arch/lifetimes.toml`; lane FW-1 |
| 3 Live follow, same body | admission with Parsek's path walk and the game-time re-check, return points, warp ceiling and the warp-limit patch that reads the watched ghost (8.4), hysteresis, takeover from near watch, W any ghost near the camera, the Missions window Watch button (same body), `[` `]`, pause-menu and Parsek greying (F9's hover line on Load), alarm and switch routing, overlay return time and warp reason, the two screen messages | `Controllers/FarWatchShipSafety.cs`, `Controllers/FarWatchPathGatherer.cs` (over `BallisticExtrapolator`), `Controllers/FarWatchDecisions.cs`, `Controllers/FarWatchWarpLimits.cs` (pure transcription of stock's warp rules), `Patches/FarWatchWarpLimitPatch.cs` (`TimeWarp.getMaxOnRailsRateIdx` and the LOW-mode switch in `TimeWarp.Update`), `Controllers/WatchModeController*.cs`, `Patches/FarWatchPauseMenuPatch.cs`, `Patches/AlarmJumpSwitchPatch.cs` (+ `AlarmClockUIFrame.OnSwitchTo`), `Patches/MapFocusObjectOnSelectPatch.cs`, `Patches/VesselCycleReFlySwitchPatch.cs`, `UI/SpawnControlUI.cs`, `UI/RecordingsTableUI.cs`, `UI/MissionsWindowUI.MissionRows.cs` / `.Interact.cs`, Timeline W buttons, the watch overlay (`WatchModeController` draw), `Controllers/ParsekFlightSpawnHost.cs`, `TooltipEchoBudgetTests`; lanes FW-2, FW-3, FW-4, W1 |
| 4 Live follow anywhere | other bodies, SOI hop of the drone, map Watch entry ("Watch (Too Far)" / "(Other SOI)" become plain Watch), the Missions window Watch at any body, terrain floor at any body, `GhostOrbitDominantBodyPatch` drone handling | `Controllers/WatchModeController.Diagnostics.cs` (`ResolveWatchSameBodyDecision`), `Ghost/GhostMapWatchHelper`, `UI/MissionsWindowUI.MissionRows.cs`, `Controllers/CameraDrone.cs`, `Patches/GhostOrbitDominantBodyPatch.cs`; lanes FW-5, FW-6, FW-7. As built: `GhostMapWatchHelper` lives in `Patches/GhostVesselLoadPatch.cs`, the Missions decision in `UI/MissionsWindowUI.Interact.cs`; also `Controllers/FarWatchDecisions.cs`, `FarWatchTransitions.cs`, `FarWatchController.cs` / `.Warp.cs`, `WatchModeController.cs`, `ParsekFlight.cs` / `.GhostPositioner.cs` / `.RecorderSession.cs`, the W buttons of `UI/RecordingsTableUI.*` and `UI/TimelineWindowUI.cs`, `Ghost/GhostMapPresence.Observability.cs`, `TestCommands/TestCommandFarWatch.cs` + its applier (`via=map`), `scripts/arch/far_watch_sites.toml`; lanes FW-5, FW-6, FW-7 on committed hosts (no derived fixture) |

### 18.1 Step 1 proof: exact observations

From audit E (L-numbers are its live checks), plus five this note adds:

1. Drone built and loaded: `vesselType == Debris`, `connection == null`, module list of the chosen part, no visible mesh at 1 m, no raycast hit (L1.1); no icon, line, label, TS row, `[` `]` entry, target (L1.2).
2. `ForceSetActiveVessel(drone)` with the drone loaded: event order logged (`onVesselGoOnRails(ship)` before `onVesselSwitching`, then `onVesselChange`), no `onVesselSwitchingToUnloaded`, ship packed, watch camera on the ghost the next frame (pivot delta < 1 m), map camera on the ghost (L2.1).
3. Drone held packed for 10 minutes through Esc pauses and warp 1x to 100000x; a loaded station 1 km away stays packed (L3.1).
4. Orbit-rewrite tracking of a burning ghost: drone-ghost distance < 500 m every tick, navball speed matches; per-frame cost with the solver attached vs detached (L3.2, L7.1). Added after the proof: jitter (the frame-to-frame second difference of the camera-to-ghost offset, the proof's `jitterMaxM`) at most 1 m at steady 1x (measured below 0.1 m) and at most 500 m on a follow-start or member hand-off frame (measured 60 to 195 m), so a run can pass or fail on it; the navball speed is compared with the ghost's trajectory velocity at the same UT, never with the velocity just written (the proof's `speedDiffMax` compared a write with its own read-back and was always 0).
5. Kerbin -> Mun ghost: one `onDominantBodyChange`, zero drone `onVesselSOIChanged`, Mun terrain renders, PQS hitch measured (L3.3).
6. Low / landing ghost: drone altitude >= terrain + margin every tick, zero `crashed through terrain` (L4.1).
7. Forced autosave mid-watch (short `AUTOSAVE_INTERVAL`): no drone VESSEL, `activeVessel` = ship by pid, reload focuses the ship (L6.1).
8. F5 / F9 and the pause / altimeter save buttons inert during the watch; quicksave mtime unchanged (L6.2). UNPROVEN: the proof set the locks but pressed no key.
9. Hand-back from another SOI: ship loaded before the switch, no reload, camera restored, drone destroyed after it is inactive; landed-ship unpack on the Mun and Kerbin without a bounce > 0.5 m/s (L2.2, L8.2). The hand-back half is proven (FWP-2); the bounce is UNPROVEN, never measured.
10. Negative control: ship parked at 30 km on Kerbin, drone 50 km away -> stock "was on-rails at X kPa pressure and was destroyed" line (L4.2).
11. (approved design) The unlisted-vessel test: remove the drone from `FlightGlobals.Vessels` after load and log the drone-to-origin offset after each `FloatingOrigin` re-centre (expected lag per audit E).
12. (this note) Contract resets per switch: count `ResetContracts` calls and which vessel each saw, with the fence off and on.
13. (this note) Ledger belt: an unbooked stock award (forced Rendezvous with the fence off) is reverted by the next recalculation, or not (decides 10.3's fallback). UNPROVEN: the award was booked to the ship's recording, so the premise was not met (10.3).
14. (this note) Drone death while active (forced `Die` mid-watch), ship loaded and ship unloaded: stock's `AttemptVesselSwitch` target, whether `waitAndEndFlight` shows its dialog before the ship is active, and `FlightLogger.missionEnd` afterwards. UNPROVEN: with the ship unloaded the result contradicts the expected stock path (7.6) and needs a full log; with the ship loaded it was not reached (FWP-3 lost its camera first).
15. (this note) Drone beside a landed ship on the Mun and Kerbin through Spawning and HandoverBack, at 1x and after a warp request: drone altitude above terrain every tick, zero terrain kills. Proven at 1x only; UNPROVEN at warp, because stock refused the warp (8.4).

Screenshots and the collected log go to `logs/flights/far-watch-proof/`; career funds, science, reputation, contracts and milestones identical before and after (the approved design's sixth proof item).

**Proof results 2026-11-27.** Branch `far-watch-proof` at `77394391` (throwaway, never merged), extracts in its `proof-results/` folder (`2026-11-27_1854` FWP-1 Mun, `_1913` FWP-2 Ike career, `_1914` FWP-3 Kerbin). Verdicts: FWP-1 PASS, FWP-2 PASS, FWP-3 PARSEK-FAIL.

- FWP-3 failed on a proof bug that exposed a design gap: the park op left the camera under a ghost the loop then retired, `FlightCamera.fetch` went null and 12,838 NullReferenceExceptions followed (7.1 step 6, 7.5, 7.6).
- Confirmed: event order at both switches, pivot delta 0 (item 2); the hold through pauses and warp up to 100000x, zero drone or ship unpacked frames (item 3); one dominant-body change and zero drone SOI events at the Kerbin-to-Mun crossing, 71.7 ms frame (item 5); the strip on every save, autosave restart 5.11 s (20.18 s waiting on `ClearToSave`), the no-strip control keeping the drone (item 7, 9.1); the pressure kill of the unprotected control at 6.7 kPa, 106.86 km from the active drone, and none of the drone (item 10); 0 exceptions with the drone unlisted, 503 m drone-to-origin at each re-centre (item 11); 3 `ResetContracts` per switch (item 12, 10.1); one GoOffRails refusal per hand-over (6.4); hand-back from another SOI in 0.35 s with no reload, pivot delta 2.75 m, the destroyed drone gone from both lists (item 9, first half); write cost 0.02 to 0.05 ms per write, at most 2.14 ms, no difference with the solver attached or detached (item 4, cost half).
- Contradicted or changed the design: the part is not module-free and the drone was targetable (6.2); stock's warp limits read the drone (8.4); the follow error grew at about 148 m/s after a cross-body jump (6.3 step 2); the engine stops resolving a hidden ghost (6.3 step 1); the map camera stays on the drone (7.1 step 6); the Rendezvous award fired and was booked to the ship (10.2, 10.3); `ClearToSave` refuses while the drone is surface-held (9.1).
- Key numbers: FWP-1 `maxRateSeen=1.00` over `setCalls=1435`; FWP-3 `maxRateSeen=3`; follow error 742 m to 28,890 m at 1x and 211,624 m at 100x after the jump, 0.77 m above the rotating-frame threshold; jitter below 0.1 m at steady 1x, 60 to 195 m at follow start and member hand-offs, 25 to 53 m at 10x to 50x, one 175,190 m spike (FWP-1 20:56:31) and 1006 m during the unlist test; floor clamps 158 and 182 at a 20.00 m minimum above terrain, zero terrain kills (1x); Rendezvous funds +21,120, reputation +1.
- Still unproven, re-flown in step 2's own lanes rather than in a second proof round: map and orbit-line invisibility of the drone (item 1); navball speed against the ghost and the new jitter bounds, and the cross-body follow with the rotating-frame fix (item 4); the low ghost at warp (items 6, 15); the input lock with a real F5 / F9 press (item 8); the landed-ship bounce (item 9); the ledger revert of an unbooked award (item 13); drone death with the ship unloaded and loaded (item 14); LOW warp at 4x under the hold, and the warp patch itself (8.4).
- Notes about the proof itself, not the design: its own fixes were the park camera re-target, `HandBackSettle` run as a real coroutine (the op pump returned a nested `IEnumerator` as a value and never ran it, so `bounce` read 0), the drone pid cleared on kill (the stale prefix gave `goOffRailsRefused=1206`), an F5 injected while locked, the origin-shift count kept per op (the 5 s summary reset it), and `speedDiffMax` compared with the ghost. The FWP-2 spec's `committedTrees = { min = 2, max = 3 }` should be `min = 1`: the fixture holds one committed tree plus its active-tree restore copy (`persistent.sfs` line 1467), and the lane's discard dropped the copy (saveParse is report-only, so it did not gate). The exception budget should subtract the pre-arm baseline (6 in FWP-1) and filter the stock VehiclePhysics wheel NullReferenceExceptions of the wheeled fixture vessel (150 in FWP-1, ended by the drone switch's pack; precedent: the V23M flight notes in `docs/dev/todo-and-known-bugs.md`, 11,853 on the green `2026-11-16_2256` flight).

**Step 2 flights, FW-1.** `2026-11-28_0036`: INVALID (driver-verdict-mismatch); `FarWatch start` ran 1.3 s after `StartLoopPlayback` answered (UT 24306565.13), before the loop relaunch at 24306580.07, so no loop ghost existed and the seam refused `no-far-watch-ghost` (the lane started too early; fixed in `c181f016d` with a `WarpToUT` past the relaunch). `2026-11-28_0051`: refused at the same step with `start refused reason=supply-run-proving`: V1's predicate read the start half every recording carries from its birth, so it refused every far watch during a recording (this gate; removed, the hand-over keeps the proof, 7.4, V1). Both flights stopped before the drone was built, so step 2 is still unflown past entry.

**Step 3 flights, FW-2 / FW-3 / FW-4** (copy lease on stock-minimal). `2026-11-28_1035` FW-3 PASS: the start on the ascent ghost is refused `unsafe:NOT_IN_ATMOSPHERE`, no drone. `2026-11-28_1036` FW-4 PASS: `return point set returnUT=22190.0 cause=Danger dangerUT=22490.0`, the watch ends at 22190 with reason ReturnPoint and the ship alive. `2026-11-28_1032` FW-2 PARSEK-FAIL on one log contract: the follow, the re-key to the next member and the 250 km hand-back all ran, but the warp-limit line was written only when stock's limit at the ghost lowered the asked rate, and `WarpToUT` never asked above it; the unclamped read now logs `rule=stock:none` (rate-limited), and `2026-11-28_1110` FW-2 PASS shows it at 50x, 10x and 5x. The FW-4 flight also showed the forced-return message naming the ship by its raw `#autoLOC` tag; the name now goes through the localizer.

---

## 19. Open Questions

### 19.1 For the owner (V)

The cards were posted on 2026-11-27 (V1 again on 2026-11-28); the design proceeds on the recommendations.

**V1. A supply run being proven while you start a far watch** (decided by the owner 2026-11-28: a far watch keeps the proof). The 2026-11-27 ruling refused the watch while the flight was proving a supply run, because any switch voids the run's route proof. FW-1's second flight showed that every recorded flight carries a proof from its first frame (the start half of the run manifest is written at every recording's birth), so the refusal blocked every far watch during a recording. The recommendation, built: the far-watch hand-over keeps the proof (7.4), the hand-back resumes the same recording with the same start half, and a change to the ship's structure during the watch still voids it. The refusal and its hover reason are gone. Under the 2026-11-27 ruling instead, the refusal would be restored with a predicate that reads an in-progress run (not the birth-time start half).

**V2. Your ship is lost while you watch far away** (decided by the owner 2026-11-27: stock's flight-ended screen; something the check could not foresee: a spawn landing on it, a mod). There is no ship to come back to. The camera comes back to where the ship was and the game shows stock's own "flight ended" screen with its Revert and Space Center buttons, exactly as when a flown ship is lost.

### 19.2 Decided here (with reason)

| Question (source) | Decision | Reason |
|---|---|---|
| Contract fence shape (D Q1) | per-class fences on audit D's stock classes (18 `OnUpdate`, 5 handlers) + `ResetContracts`; mod parameters a known gap checked after step 7 | owner ruling "fence each condition, both ways, never pause wholesale" (10.1) |
| Drone `vesselType` (D Q2, E) | Debris | stock removes CommNet, excludes it from `[` `]`, non-commandable; contracts fenced by identity anyway |
| Drone part (D Q5, E) | `strutCube`, with its five inert modules listed and pinned (6.2); an explicit target refusal | no science / sensor / test modules on the active vessel; the proof found the part not module-free and the drone targetable |
| End-of-recording spawns during a watch (A Q1, C Q5) | run, no focus move, drone excluded from collision, ship protected | needed by the ruled "ghost ends as a real ship" |
| No active tree (A Q2) | allowed | H3 guards cover it; the return is an ordinary switch |
| Stock controls on the drone: Recover, staging, EVA (A Q3, C Q8) | Recover never offered (never landed); staging locked and hidden; no crew | 6.2, 12 |
| Stock time warp during a watch (B Q1) | a Harmony patch on `TimeWarp.getMaxOnRailsRateIdx` and the LOW-mode switch in `TimeWarp.Update` evaluates stock's rules against the watched ghost's state (the ship's with no ghost), under Parsek's ceiling; the overlay names the reason. Fallback if step 3 finds the patch unsafe: no on-rails warp while the watched ghost is low or in atmosphere (8.4) | ruled "normal rules for where the ghost is"; the proof showed stock's limits read the drone, so a surface-held drone blocked on-rails warp (FWP-1, 1x) and a drone in air got physics warp only (FWP-3, 3x) |
| Map / TS / alarm switches to a real vessel (B Q2, C Q1) | end the watch first, then switch | ruled |
| Alt+F12 teleport (B Q6) | ignore, overwritten next tick | debug tool; the drone is re-placed anyway |
| Pause menu Tracking Station, Main Menu, Quit (C Q2) | greyed with Space Center | stock saves on each way out; same ruling |
| Alarm add / maneuver node / target on the drone (C Q4) | alarm add greyed, leftovers deleted at Teardown; no node UI (solver detached); target dropped | no drone reference survives the watch |
| Promote at hand-back or on first modification (C Q10) | on first modification, the ordinary switch-back path | ruled "an ordinary switch"; same recording id either way |
| Loop anchored to the ship (C Q7b) | near watch only | within range by construction |
| Rendezvous ordering (D Q4) | fence keyed on "drone exists", up until after `Die`; mandatory | 10.2; FWP-2 paid an unfenced Rendezvous that the belt cannot catch (10.3) |
| PARSEK node during a watch (C Q9) | written as it stands (outsider shape); loads through the vessel-switch restore | 9.4 |
| Ship safety prediction | Parsek's own path walk (`BallisticExtrapolator`), never stock `nextPatch` | the stock solver is absent on an inactive ship and in an early career (8.1) |
| Ledger belt key | the vessel the award is for (`FarWatchAwardScope`) | the parked ship earns genuine awards on rails (10.3) |
| Scene exit during a watch | no refusal, no switch; the exit save is stripped; outsider-state finalize | stock cannot refuse and saves first (7.5) |
| Return lead times | air / ground 300 s, node 180 s, alarm 60 s, refuse under 600 s / 120 s | "minutes to spare" (ruled) with margin for a 10 s real-time warp ramp |
| Takeover and hand-back distances | 290 km / 250 km | the switch completes before 300 km; 50 km slack (ruled) |
| Camera on watch exit while the drone exists | same-frame re-target to a non-ghost target on every exit, plus a per-frame net; camera placed by world position, angles copied only onto the target they came from | FWP-3 lost `FlightCamera.fetch` with a retired ghost (7.5, 7.7) |
| `ResetContracts` fence predicate | "the active vessel is the drone" (3 of 6 calls per round trip), not the whole switch window | the ship-active calls are stock's own switch behaviour (10.1) |

### 19.3 Live checks (the proof answers them)

Packed-drone situation and `geeForce` as it is moved (A Q5, D Q3); event order at both switches (A Q4, B Q7, B Q8); `FlightAutoSave.Start()` restart behaviour; whether the drone is Owned for any frame after `pv.Load` (D Q3); the orbital spawn beside an unloaded ship (edge case 22); stock's flight-end reaction to a drone death (proof 14); the surface-held drone beside a landed ship (proof 15). Answered by the 2026-11-27 proof (18.1): the event order (as designed), the autosave restart (works; it waits on `ClearToSave` while the drone is surface-held), discovery (None, never Owned), the packed drone's situation (SUB_ORBITAL when surface-held, ORBITING in orbit, `geeForce` 0.00 in orbit) and the surface-held drone at 1x. Still open: the orbital spawn beside an unloaded ship, the drone death (proof 14) and the surface-held drone at warp.

---

## References

- Approved player design: "Watching any ghost from launch to end" (rev 45, 2026-11-27), owner-held.
- Audits A to F, 2026-11-27: A controllers' active-vessel reads, B other modules, C map-ghost checks and vessel GameEvents, D stock contracts and milestones, E stock engine checks, F the save-index drift.
- `docs/dev/done/design-camera-follow-ghost.md` - today's watch mode and its deferred E5.
- `docs/dev/design-save-format-policy.md` - no format change here.
- `docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md` - stock-control annotation (D1).
