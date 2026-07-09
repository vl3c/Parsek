# Claw / Grapple Coupling Internals (decompiled findings)

Status: Phase 0 findings note for the logistics claw connection producer.
Source: ilspycmd 7.2.1 decompile of `Assembly-CSharp.dll` from the local KSP 1.12.5 install
(`Kerbal Space Program/KSP_x64_Data/Managed/`). Types decompiled: `ModuleGrappleNode`,
`ModuleDockingNode` (contrast), `Part` (`Couple` / `Undock` / `decouple`), `ModuleAsteroid`,
`ModuleSpaceObjectResource`, `ModuleAsteroidDrill`, `DockedVesselInfo`, `Vessel.GetDominantVessel`,
`GameParameters.AdvancedParams`. Part configs read: `GameData/Squad/Parts/Utility/GrapplingDevice/part.cfg`,
`GameData/Squad/Parts/Misc/PotatoRoid/part.cfg`.

This note answers, from decompiled stock code only: how the claw couples, which vessel survives,
what the release fires, how it differs from docking ports, and what is special about asteroid grabs.

## 1. Couple event flow

### 1.1 Capture state machine

`ModuleGrappleNode` runs a `KerbalFSM` with states `Ready`, `Grappled`, `Grappled (same vessel)`,
`Disengage`, `Disabled`. In `Ready`, every FixedUpdate raycasts forward from the `grappleNode`
transform (`FindContactParts`, range `captureRange = 0.06`). The `Contact` event fires when:

- hit point within `captureRange` and `Vector3.Dot(node.forward, -hit.normal) > captureMinFwdDot`
  (0.998, i.e. a nearly head-on contact), and
- for a different vessel, relative velocity below `captureMaxRvel = 0.3 m/s`.

On contact with a DIFFERENT vessel, `on_contact.OnEvent` sets `dockedPartUId = otherPart.flightID`
and picks the surviving vessel by dominance:

```
if (Vessel.GetDominantVessel(vessel, otherPart.vessel) != vessel && !otherPart.vessel.isEVA)
    Grapple(otherPart, dockerSide: base.part);   // claw side couples INTO the other vessel
else
    Grapple(otherPart, dockerSide: otherPart);   // other vessel couples INTO the claw vessel
```

`Vessel.GetDominantVessel(v1, v2)`: higher `VesselType` enum value wins; on a type tie the heavier
total mass wins; within 0.01t the larger `Vessel.id` Guid wins. This is the SAME helper
`ModuleDockingNode` uses (3 call sites), so claw and dock agree on which vessel survives.
Note `SpaceObject` (asteroids) sorts below `Probe/Ship/Station`, so a ship grabbing an asteroid
always survives and the asteroid vessel is absorbed.

### 1.2 The couple itself is Part.Couple, same primitive as docking

`Grapple(other, dockerSide)` builds BOTH `DockedVesselInfo`s (own `vesselInfo` + `otherVesselInfo`,
each holding only `name`, `vesselType`, `rootPartUId`; persisted in the part node as `DOCKEDVESSEL` /
`DOCKEDVESSEL_Other`), positions the vessels, adds a synthetic `AttachNode` (`id = "grapple"`),
then calls exactly one of:

- `base.part.Couple(other)` when the claw's vessel is being absorbed (dockerSide == claw part), or
- `other.Couple(base.part)` when the grabbed vessel is being absorbed.

Decompiled `Part.Couple(tgtPart)` semantics (identical for docking, since
`ModuleDockingNode.DockToVessel` also ends in `base.part.Couple(node.part)`):

1. `GameEvents.onPartCouple.Fire(FromToAction(this, tgtPart))` FIRST, before any merge:
   `this.vessel` is still the old (dying) vessel at fire time.
2. Both vessels' staging cleared; all parts of the dying vessel re-rooted (`SetHierarchyRoot`,
   `setParent(tgtPart)`), attach joint created, `SetVessel(tgtPart.vessel)`.
3. `GameEvents.onVesselPersistentIdChanged.Fire(oldVessel.persistentId, tgtPart.vessel.persistentId)`,
   then the old `Vessel` component is destroyed (`DestroyImmediate`). The old `Vessel.id` (Guid) and
   its persistentId are gone; all parts keep their own `flightID` / part persistentIds unchanged.
4. `GameEvents.onVesselWasModified.Fire(survivor)` then
   `GameEvents.onPartCoupleComplete.Fire(FromToAction(this, tgtPart))` LAST.

After `Couple` returns, `ModuleGrappleNode.Grapple` restores physicsless-part transforms, logs
asteroid/comet analytics, transfers `DiscoveryInfo` object size onto the survivor, and fires
`GameEvents.onVesselWasModified.Fire(base.vessel)` once more.

**Answer to the headline question: yes, a claw couple fires `GameEvents.onPartCouple` with the
same `Part.Couple` semantics as a docking-port couple.** Same event, same ordering, same vessel
merge, same persistentId behavior.

### 1.3 The two differences that matter for capture

1. **No `onVesselDocking`.** `ModuleDockingNode.DockToVessel` fires
   `GameEvents.onVesselDocking(fromPid, toPid)` BEFORE `Part.Couple` and an explicit extra
   `GameEvents.onVesselPersistentIdChanged(fromPid, toPid)` after it. `ModuleGrappleNode` fires
   NEITHER. Any capture site keyed on `onVesselDocking` never sees a claw grab.
2. **From/to part asymmetry.** In a dock, both `onPartCouple` endpoints are `ModuleDockingNode`
   parts. In a claw couple, one endpoint is the claw part (has `ModuleGrappleNode`) and the other
   is an ARBITRARY grabbed part: whatever surface the raycast hit (a PotatoRoid, a tank side, a
   derelict's pod). Which side is `from` depends on dominance: `from` is always the part whose
   vessel is being absorbed (`from = claw part` when the claw ship is absorbed into a heavier
   station; `from = grabbed part` when the claw ship survives). Producer detection must therefore
   test BOTH endpoints for `ModuleGrappleNode` and must not expect a module on the other side.

### 1.4 Special couple cases

- **Same-vessel grapple** (`Grappled (same vessel)` state): builds only a `PartJoint`
  (`PartJoint.Create(..., AttachModes.SRF_ATTACH)`), never calls `Part.Couple`, fires no couple
  events. Invisible to route capture, and correctly so: no vessel identity or resource scope
  changes.
- **EVA kerbal grab**: `otherPart.vessel.isEVA` forces the claw vessel as survivor,
  calls `KerbalEVA.OnGrapple()`, force-switches the active vessel, then couples normally.
  So grabbing a kerbal DOES fire `onPartCouple` and does merge vessels.
- **Armed vs disabled**: the FSM only reaches `Contact` from `Ready` (claw arms open). `Disabled`
  (claw closed) cannot couple. Arming state never fires couple-family events, so it cannot open
  a phantom window.

## 2. Release event flow

### 2.1 Stock release is Part.Undock, the same contract as docking undock

`ModuleGrappleNode.Release()` (the only reachable release, see 2.3) picks the correct side:

```
if (base.part.parent != otherPart) otherPart.Undock(otherVesselInfo);
else                               base.part.Undock(vesselInfo);
```

then removes the synthetic grapple AttachNode, applies `undockEjectionForce` (5) half to each side,
logs asteroid/comet release analytics, restores the released vessel's `DiscoveryInfo` size, and
runs the FSM `Undock` event into `Disengage` (re-engage blocked until `minDistanceToReEngage = 1m`).

Decompiled `Part.Undock(DockedVesselInfo)` (confirms the contract recorded in `.claude/CLAUDE.md`,
"Docking-port undock event order"):

1. `GameEvents.onPartUndock.Fire(this)` FIRST, once, part still on the combined vessel.
2. `attachJoint.DestroyJoint()` (synchronous `onPartJointBreak`).
3. New `Vessel` component created on the split root; `vessel.id = Guid.NewGuid()` (FRESH Guid and
   a fresh persistentId; `DockedVesselInfo` restores only `vesselName` and `vesselType`).
4. `GameEvents.onVesselWasModified.Fire(oldVessel)`, `GameEvents.onPartUndockComplete.Fire(this)`,
   then `GameEvents.onVesselsUndocking.Fire(oldVessel, newVessel)` LAST and unconditionally on
   every path, with final vessel identities.

**So the claw release fires `onPartUndock` then `onVesselsUndocking`, in that order, exactly like a
docking-port undock.** Parsek's existing undock pipeline
(`ParsekFlight.OnPartUndock` -> `OnVesselsUndocking` -> `DeferredUndockBranch` -> `CreateSplitBranch`)
receives claw releases with no event-shape difference.

### 2.2 Which part the events carry

`onPartUndock` carries the part that `Undock` was invoked on: the CLAW part when the claw is the
child side (its parent is the grabbed part), otherwise the GRABBED part. Same asymmetry note as
1.3: only one endpoint has `ModuleGrappleNode`.

### 2.3 The Decouple path is unreachable in stock

`ModuleGrappleNode.Decouple()` would route through `Part.decouple()`, which fires a DIFFERENT
event family (`onPartDeCouple` first, then `onPartDeCoupleComplete` and
`onPartDeCoupleNewVesselComplete(oldVessel, newVessel)`; no `onPartUndock`, no
`onVesselsUndocking`). However the `Decouple` KSPEvent is declared `active = false` and NOTHING in
the decompiled class ever activates it; the `DecoupleAction` KSPAction is guarded on
`Events["Decouple"].active` and is therefore a no-op. Stock claw release always goes through
`Part.Undock`. (A modded claw that activates `Decouple` would split via the decouple family and
land outside our connection window; treat as abnormal end, not a release.)

### 2.4 Other separation paths

Joint failure (crash, overstress) destroys the grapple joint outside `Release()`: no
`onPartUndock`, only `onPartJointBreak` plus the generic vessel-split machinery. Same situation as
a structural failure at a docking port; not a release corner, must fail the window closed.

## 3. Pivot / free-pivot specifics (vs ModuleDockingNode)

- The grapple joint is an `ActiveJointPivot` (`pivotRange = 30` degrees) layered on the normal
  attach joint. `SetLoose` (Free Pivot) / `LockPivot` toggle `ActiveJoint.DriveMode`
  Neutral/Park; `IsJointUnlocked()` reports it. These are joint drive modes only: NO coupling
  events, no vessel identity change, no resource scope change. Free Pivot does not open or close
  anything.
- `updateGrappleTransform` rotates `nodeTransform` with the pivot every LateUpdate while grappled.
  Cosmetic; ghost visuals clone the part hierarchy and do not run the module, so a ghost shows the
  claw at its captured pose (acceptable; same class of approximation as animated parts today).
- `ModuleDockingNode` contrast: docking ports have gendered node pairing, acquire-force FSM, and
  a `crossfeed` field they manage; the grapple node has none of these. The claw stores
  `grapplePos/grappleOrt/grappleOrt2` + `dockUId` + `grappledSameVessel` + both DOCKEDVESSEL nodes
  in its ConfigNode; on load with `state` containing "Grappled" it rebuilds the synthetic
  AttachNode and pivot joint from those values.

## 4. Asteroid (PotatoRoid) specifics

- `PotatoRoid` part config: `name = PotatoRoid` (NO underscores, so the underscore-to-dot runtime
  conversion is a no-op; `PartLoader.getPartInfoByName("PotatoRoid")` resolves directly, but the
  snapshot path should still be pinned by a test), `vesselType = SpaceObject`, cfg `mass = 150`
  (overridden procedurally), `attachRules = 1,1,1,1,1`, single module `ModuleAsteroid`
  (`density = 0.03`). Comets are the parallel `PotatoComet` part with `ModuleComet`.
- `ModuleAsteroid : PartModule, IVesselAutoRename, IPartMassModifier`: procedural mesh + mass from
  `seed`; mass changes at runtime via `SetAsteroidMass` as drills consume it (`GetModuleMass`
  reports the delta over the prefab mass). Consequence for snapshots: an asteroid's mass and mesh
  are NOT reproducible from the part name alone; they come from the module's `seed` +
  current mass state. Ghost-visual building of a grabbed asteroid needs the module's procedural
  mesh, which our clone-the-renderers approach gets for free from the live part, but an in-game
  test must confirm (M-MIS-10 already flags this as the highest-risk unverified cell).
- Asteroid RESOURCES are not `PartResource` tanks. Abundance lives in `ModuleSpaceObjectResource`
  (`resourceName`, `abundance`, ...) modules added to the space object; `ModuleAsteroidDrill` is a
  `BaseDrill` (a `BaseConverter` subclass) that converts asteroid mass into Ore in the MINER's
  tanks. Two consequences:
  1. A couple/release scoped-manifest snapshot of an asteroid endpoint sees no PartResources on
     the asteroid itself; every material gain appears on the mining vessel's side as drill OUTPUT.
  2. The M2 harvest window capture keys on the `BaseConverter` base class, so asteroid/comet
     drills on an already-grappled asteroid are ALREADY captured (confirmed shipped, see
     todo-and-known-bugs.md M2 cross-note resolution).
- `vesselType = SpaceObject` means dominance always favors the grabbing ship (section 1.1):
  `onPartCouple.from` is the PotatoRoid part, `to` is the claw part, and the asteroid's vessel
  (its Guid and persistentId) is destroyed by the merge. On release the asteroid becomes a NEW
  vessel with a fresh Guid; only name and `vesselType = SpaceObject` are restored via
  `DockedVesselInfo`. Any endpoint identity scheme for asteroids must therefore not rely on
  Vessel.id continuity across grab/release cycles; the stable handle is the PotatoRoid part's
  `flightID` / part persistentId, which survives both directions.

## 5. Resource crossfeed across a claw couple

- The claw part config sets `fuelCrossFeed = False`, the part has no `ModuleToggleCrossfeed`, and
  `ModuleGrappleNode` never touches `part.fuelCrossFeed` (contrast: `ModuleDockingNode` has a
  `crossfeed` KSPField default true, sets `part.fuelCrossFeed`, and exposes EnableXFeed /
  DisableXFeed events firing `onPartCrossfeedStateChange`).
- Flow-rule crossfeed (engine/RCS draw) therefore never crosses the claw joint in stock.
- MANUAL transfer (the right-click pump) works across any same-vessel connection unless the
  difficulty option `GameParameters.AdvancedParams.ResourceTransferObeyCrossfeed` is on; the
  decompile shows it defaults false and is set true only by the Moderate and Hard presets. So:
  Easy/Normal saves can pump fuel across a claw; Moderate/Hard saves cannot (well-known stock
  behavior, no override on the claw part).
- Consequence for RouteProofCapture: NONE structurally. The connection-window scoped-manifest
  capture snapshots PartResources at the couple/release corners; whatever transfer stock actually
  permitted shows up as deltas, and a Moderate/Hard save simply produces zero-delta fuel windows
  (inventory and crew movement are unaffected by crossfeed rules). No crossfeed-specific code is
  needed; a doc note in the design suffices.

## 6. Summary table

| Aspect | Docking port | Claw (stock) |
|---|---|---|
| Couple primitive | `Part.Couple` | `Part.Couple` (same) |
| `onPartCouple` | yes, both ends are MDN parts | yes, one end claw part, other ARBITRARY part |
| `onVesselDocking` | yes, before Couple | NO |
| Extra `onVesselPersistentIdChanged` | yes (MDN fires one) | only the one inside `Part.Couple` |
| Survivor selection | `Vessel.GetDominantVessel` | same helper |
| Release primitive | `Part.Undock` | `Part.Undock` (same; Decouple path unreachable) |
| `onPartUndock` -> `onVesselsUndocking` order | yes | yes, identical |
| Released vessel identity | fresh Guid + persistentId | same |
| Crossfeed | MDN-managed, default on | `fuelCrossFeed = False`, no toggle |
| Same-vessel connect | onSameVesselDock event | PartJoint only, NO events |
| Pivot | rigid | ActiveJointPivot, drive-mode only, no events |

## 7. Implications carried into the design note

1. The recorder and the undock pipeline already receive claw couples/releases through the exact
   dock event pair (`onPartCouple` / `onVesselsUndocking`); parity gaps, if any, are in
   module-type filters, not event wiring.
2. The connection producer seam must classify a couple by testing both `onPartCouple` endpoints:
   `ModuleDockingNode` on both -> Dock; `ModuleGrappleNode` on either -> Grapple. Anything keyed
   on `onVesselDocking` misses claws by construction.
3. Grapple endpoint identity: the grabbed side may be a bare part on a vessel with no docking
   modules at all (asteroid, derelict). Endpoint resolution must fall back to vessel-level
   identity via the recorded couple corner, not port identity, and must tolerate the released
   vessel's fresh Guid.
4. EVA kerbal grabs open a real couple window but are not a logistics connection: named rejection.
5. Same-vessel grapples and pivot mode changes are correctly invisible; no code needed.
6. Crossfeed requires no capture change; corner snapshots already measure actual transfer.
