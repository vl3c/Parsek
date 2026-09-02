# Decision memo: the start-docked RouteOriginProof origin partner

Written 2026-09-02 for package P6, against the operator ruling: *the origin partner is
the vessel the transport docked INTO, identified from the docking-node PAIR at capture
time, independent of which half KSP made dominant on merge.*

**Verdict: SOUND.** The rule is DEPOT-TYPED SELECTION: the origin is the half whose
pre-dock `DockedVesselInfo.vesselType` is `Base` or `Station`; the other half is the
transport. Both depot-typed, neither depot-typed, or an invalid cargo owner on either
side produces NO proof.

## The design authority the previous wave could not find

`docs/parsek-logistics-supply-routes-design.md` (the archived
`done/logistics-origin-ownership-proposal.md` names it as its successor). Section 7 asks
the recorder for the "connected origin vessel PID, connection kind, ORIGIN PART PID SET,
and transport-scoped start/end manifests", and rejects a start-docked vessel that is a
"ghost/EVA/debris/invalid cargo owner"; test-intent line 1314 states "deducts from
recorded ORIGIN DEPOT, NOT TRANSPORT". The ruling is that doc's rule, not a new one, and
the old producer's `v.persistentId` violated line 1314 directly.

## What the docking-node pair carries (decompiled, KSP 1.12.5)

`DockedVesselInfo` = `{ name, rootPartUId, vesselType }`. No pid, no guid. It round-trips
through `DOCKEDVESSEL`. `ModuleDockingNode.DockToVessel` writes one on BOTH nodes, each
describing ITS OWN half (`vesselInfo.name = base.vessel.vesselName` on the caller's node,
`node.vesselInfo.name = node.vessel.vesselName` on the partner's), corroborated by
`Part.Undock(newVesselInfo)` restoring `vessel.vesselType = newVesselInfo.vesselType` on
the departing half. So the PAIR carries both halves; one node alone carries only its own.

Two readings are ruled out by the same decompile:

- **Dominance.** `Part.Couple` keeps the target side's root, so `v.rootPart.flightID`
  names the half that STAYED; `Vessel.GetDominantVessel` picks by `vesselType`, then mass,
  then a `Vessel.id` compare. The ruling forbids it and it is wrong on H57's shape anyway.
- **Docker / dockee.** It looks like an initiator record and is not one:
  `if (GetDominantVessel(base.vessel, otherNode.vessel) == base.vessel) {
  otherNode.DockToVessel(this); }` - the docker is simply the non-dominant half.

That leaves `name`, `rootPartUId`, `vesselType`. Only `vesselType` is a semantic
discriminator, and it is exactly the depot designation the doc means.

## The rule, and the identity it forces

At recording start, for each settled dock seam (`IsSettledDockSeam`, unchanged) read BOTH
nodes' `vesselInfo`, then: reject if either half is outside the valid-cargo-owner set
(`Debris`, `SpaceObject`, `Unknown`, `EVA`, `Flag`, deployed-science); exactly one half
`Base`/`Station` -> that half is the origin; both or neither -> ambiguous, no proof.

**Identity: the origin half's `rootPartUId`** (a part `flightID` - assigned per launch,
never baked into the `.craft`, so launch-unique where a `persistentId` is not), plus
`name` and `vesselType`, alongside the M1 descriptor (body + body-fixed coords +
situation, which are the docked pair's and therefore both halves').

**No pid is stamped at capture.** The absorbed half's `Vessel` is destroyed by
`Part.Couple`; the survivor's pid names whichever half won dominance.
`StartDockedOriginVesselPid` stays 0 as the bind-later slot. Resolution therefore leads
with a ROOT-PART step in `RouteEndpointResolver` (identity first, proximity only as
fallback) - without it a pid-less origin resolves to whatever surface vessel is nearest
the recorded coordinates, routinely the transport parked back at the depot. The step is
guid-free by construction: `Part.Undock` resolves
`this.vessel[newVesselInfo.rootPartUId]`, calls `SetHierarchyRoot` on it and builds the
new `Vessel` on that part, while assigning `vessel.id = Guid.NewGuid()` - so the flightID
survives the split and a launch-guid match would be actively wrong.

## What this does NOT settle (filed, not invented here)

- **The typing requirement.** `Vessel.FindDefaultVesselType()` takes the max of the
  vessel's own type and its parts', and no stock part declares `Base`/`Station` (verified
  over `GameData/Squad`: the highest any stock part declares is `Plane`). So an ordinary
  landed base is `Ship`/`Probe`/`Lander` until the player retypes it, and the rule
  fail-closes to no proof. Filed as ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT.
- **F2 residue.** A base that starts a recording with a transport docked to it still
  records itself as its own origin; removing that needs undock-side routing of the proof
  to the non-origin half, which is also where the pid binds.
- **Transport-scoped manifests.** `StartTransportResources` covers the whole merged
  vessel, so the depot's tanks count as transport cargo.

---

## AMENDMENT 2026-09-02 (P12): the operator's ruling supersedes the depot-typed selection

**The memo's verdict above is SUPERSEDED in its selection half and STANDS in its evidence half.**
What the docking-node pair carries, why dominance and docker/dockee are both ruled out, and why
the identity is the root part `flightID` rather than a `persistentId` - all of that is unchanged
and is what this package is built on. What is deleted is the answer it gave to "which half is the
depot?".

**THE RULING.** There is no "base" type that matters; bases are ordinary vessels. A route
candidate is defined by TRANSFERS AND DOCKS: the transport takes cargo at one docked partner and
delivers it at another. Nothing in the product decides anything on a `VesselType` any more.
`RouteProofCapture.IsDepotVesselType` is deleted as an authority; the type survives only as a
logged and serialized informational field, and the one remaining type check is the design doc's
own valid-cargo-owner reject set (`Debris` / `SpaceObject` / `Unknown` / `EVA` / `Flag` /
deployed-science), which asks whether a thing can own cargo at all, not what its role is.

**WHAT REPLACES IT, in three parts.**

1. **Capture defers.** Both halves of the settled seam go onto the proof as a PAIR
   (`RouteOriginProof.StartDockedPair`), each with its `rootPartUId`, name, informational type,
   and the part-`persistentId` set it owns on the merged vessel - derived by cutting the seam edge
   in the merged part tree, which is the only way a half's parts are knowable while both halves
   are one `Vessel`. `StartDockedOriginRootPartUId` stays 0 and the bind state is
   `PairPendingBinding`.
2. **The undock binds.** The origin is the half the player did NOT keep flying: at the split the
   ACTIVE side is the half the recorder follows (the transport, by `DeferredUndockBranch`'s own
   rule) and the BACKGROUND side is the origin. Matching is by PART SET, never by vessel pid - a
   `persistentId` is craft-baked. The origin's live pid is stamped only behind
   `VesselLaunchIdentity`, which refuses a pid equal to the recorded launch's whose guids do not
   conclusively differ; that refusal is the self-origin guard. A recording that ends still docked
   is `UnboundAtStop` and never an origin.
3. **The pickup is validated.** The TRANSPORT half's manifests at start and at the undock decide:
   a rise in an admitted resource is `Gain`, otherwise non-empty admitted cargo is `Carried`,
   otherwise `None`. Only the first two make the proof an origin. Authority: design 19.2.2 item 2
   ("Loaded" - cargo flowed FROM another vessel ONTO the transport) plus its workflow sentence
   "start the supply run docked to the origin (making it a Loaded provenance via the start-docked
   window)"; `Carried` is admitted because while docked the pair is ONE `Vessel` with stock
   crossfeed, so cargo aboard the transport at the split was part of the merged stack the origin
   is half of.

**WHY THE OLD RULE HAD TO GO, measured rather than argued.** A sibling session's rover-relay
flight (KSP.log 2026-09-02 20:29, DLL from main `96ac15dfb`, three same-craft-file rovers landed
near KSC) logged `RouteOriginProof skipped: no depot half recId=1461186781 ... seams=2
candidates=0` with `nearType=5 farType=5` on BOTH docks - two Rovers, neither typed `Base` or
`Station`. The rover-to-rover relay is the shape the roadmap is aimed at, and the discriminator
captured nothing on it. That flight also supplies the binding oracle replayed headlessly in
`StartDockedOriginBindingTests`: at UT 276.00 the half not kept is rover B and the transport delta
is LiquidFuel +200 (B binds), at UT 402.50 the half not kept is rover A and the delta is -126.8
(a delivery, and write-once keeps the origin on B).

**WHAT THIS DOES NOT SETTLE.** The binding follows FOCUS. In the mirror shape - the player keeps
flying the depot and the transport departs into the background - "the half the player did not keep
flying" is the transport, so the bind names the wrong half. It is inert on the recording that
carries it (a base that never moves produces no delivery window, so no route is built from it),
but it is filed as ROUTE-ORIGIN-PROOF-BIND-FOLLOWS-FOCUS-NOT-THE-RUN rather than left implicit.
