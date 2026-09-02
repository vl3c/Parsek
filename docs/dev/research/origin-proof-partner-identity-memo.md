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
