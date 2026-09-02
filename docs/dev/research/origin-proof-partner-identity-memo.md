# Decision memo: the start-docked RouteOriginProof origin partner

Written 2026-09-02 for package P6, against the operator ruling: *the origin partner is
the vessel the transport docked INTO, identified from the docking-node PAIR at capture
time, independent of which half KSP made dominant on merge.*

**Verdict: SOUND, and implementable as one pure rule over the node pair.** The rule is
DEPOT-TYPED SELECTION: the origin is the half whose pre-dock `DockedVesselInfo.vesselType`
is `Base` or `Station`; the other half is the transport. Ties (both depot-typed, neither
depot-typed) and invalid cargo owners produce NO proof.

## (a) What the design docs say

The previous wave found only `docs/dev/design-logistics-claw-producer.md` and the archived
`docs/dev/done/logistics-origin-ownership-proposal.md`, and concluded "there is no route
design doc to cite a rule from". That is wrong: the authority is
`docs/parsek-logistics-supply-routes-design.md` (the archived proposal names it as its own
successor, section 19.2.2 / 19.4 M1). It carries two binding sentences:

- Section 7 (line 762): "The recorder must capture the connected ORIGIN VESSEL PID,
  connection kind, ORIGIN PART PID SET, and transport-scoped start/end manifests ... If the
  run starts undocked away from KSC, or the start-docked vessel is a ghost/EVA/debris/
  invalid cargo owner, route analysis rejects the candidate."
- Test-intent line 1314: "Non-KSC start-docked origin -> deducts from recorded ORIGIN
  DEPOT, NOT TRANSPORT or arbitrary nearby vessel. Catches: wrong debit identity."

So the doc already rules that origin != transport, already sanctions a PART-level origin
identity, and already lists the reject set (debris / EVA / invalid cargo owner). Today's
producer stamps `v.persistentId`, which on H57's shape names the transport - a direct
violation of line 1314. The ruling is the doc's rule, not a new one.

## (b) How the existing identity surfaces identify a partner

`DockEventGraph.ResolveMergePartner` (the dock-partner-stamp work) resolves a merge partner
from a STAMPED PID plus a launch GUID gate (`MissionCrossTreeDock.FindClaimedRecording`,
`VesselLaunchIdentity.GuidsConclusivelyDiffer`), with explicit `UnstampedZero` /
`GuidRejected` statuses. `VesselLaunchIdentity` is binding: `persistentId` is craft-baked,
so a bare pid match is never an identity match. That surface needs a pid to work, and the
node pair has none (below), so it cannot be the capture-time answer - it is the BINDING
surface for later, once a live pid exists.

## (c) What the docking-node pair actually carries (re-verified by decompile)

`DockedVesselInfo` = `{ string name; uint rootPartUId; VesselType vesselType; }`. No pid, no
guid. It round-trips through `DOCKEDVESSEL` (`vesselName` / `rootUId` / `vesselType`), so it
survives save/load. `ModuleDockingNode.DockToVessel` writes `vesselInfo` on BOTH nodes, each
carrying ITS OWN half's pre-dock identity (`vesselInfo.name = base.vessel.vesselName` on the
caller's node, `node.vesselInfo.name = node.vessel.vesselName` on the partner's) - confirmed,
and corroborated by `Part.Undock(newVesselInfo)` restoring `vessel.vesselType =
newVesselInfo.vesselType` on the departing half. So the PAIR carries both halves' identities;
one node alone carries only its own.

Two readings are ruled out by the same decompile:

- **Dominance.** `Part.Couple` keeps the target side's root, so `v.rootPart.flightID` names
  the half that STAYED. `Vessel.GetDominantVessel` picks by `vesselType` first, then mass,
  then a `Vessel.id` comparison. The ruling forbids using it, and it is wrong on H57 anyway.
- **Docker / dockee.** It looks like an initiator record and is not one:
  `ModuleDockingNode` dispatches `if (GetDominantVessel(base.vessel, otherNode.vessel) ==
  base.vessel) { otherNode.DockToVessel(this); }` - the docker is simply the NON-dominant
  half. It is dominance under another name and adds no information.

That leaves `name`, `rootPartUId`, `vesselType` per half. Only `vesselType` is a semantic
discriminator, and it is exactly the depot designation the design doc's "origin depot" means.

## (d) The rule, stated concretely

At recording start on the already-merged vessel `v`, for each settled dock seam
(`RouteProofCapture.IsSettledDockSeam`, unchanged) resolve the partner part and read BOTH
nodes' `vesselInfo`. Then:

1. Reject the seam if either half's type is not a valid cargo owner (`Debris`, `SpaceObject`,
   `Unknown`, `EVA`, `Flag`, deployed-science types) - the doc's reject set.
2. Exactly one half is `Base` or `Station` -> THAT half is the origin, the other is the
   transport. Both, or neither -> ambiguous, no proof.

WHICH HALF IS THE TRANSPORT is therefore answered by type, not by `v.rootPart`, not by which
half will remain after undock, and not by the controlling half - none of which is knowable or
correct at capture. On H57's rig the depot is the CHILD (raw `Part.Couple` puts it there) and
the rule still names it, which is what makes the cell a dominance-independence proof.

**Identity fields the proof must carry:** the origin half's `rootPartUId` (a `flightID`, which
unlike `persistentId` is assigned per launch and is NOT craft-baked, so it is a launch-unique
key), plus `name` and `vesselType`, alongside the existing M1 endpoint descriptor (body +
body-fixed coords + situation, which are the DOCKED PAIR's and therefore both halves').

**The pid is NOT stamped at capture.** The absorbed half's `Vessel` is destroyed by
`Part.Couple`, so its pid is genuinely unrecoverable; and the surviving half's pid is only
usable through the guid gate. `StartDockedOriginVesselPid` stays on the proof as the
bind-later slot and is `0` on every captured proof. Surface origins resolve through
`RouteEndpointResolver`'s proximity fallback off the descriptor; binding the pid at the undock
(where both live vessels and their guids exist, matched by `rootPartUId`) is filed as
follow-up work, not built here.

## What this does NOT settle (filed, not invented here)

- **F2 residue.** A base or station that starts a recording with a transport docked to it
  still gets a proof naming ITSELF as origin. The type rule narrows it (the proof now names
  the base half rather than "whichever half won dominance") but does not remove it; removing
  it needs undock-side routing of the proof to the NON-origin half.
- **Transport-scoped manifests.** `StartTransportResources` is scoped to the whole merged
  vessel, so the depot's own tanks currently count as transport cargo.
- **Orbital origins** resolve nowhere until the pid is bound at undock.
