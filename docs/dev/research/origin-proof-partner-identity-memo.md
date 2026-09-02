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

## ~~What this does NOT settle (filed, not invented here)~~ - ALL THREE ARE NOW FIXED

The three residues this memo filed were all closed by the P12 amendment below, and the
bullets are struck rather than deleted so the derivation still reads in order.

- ~~**The typing requirement.**~~ `Vessel.FindDefaultVesselType()` takes the max of the
  vessel's own type and its parts', and no stock part declares `Base`/`Station` (verified
  over `GameData/Squad`: the highest any stock part declares is `Plane`). So an ordinary
  landed base is `Ship`/`Probe`/`Lander` until the player retypes it, and the rule
  fail-closed to no proof. **FIXED: the requirement is deleted - no code decides on a
  vessel type any more.** The mechanism note stands as the reason the old rule was
  unshippable.
- ~~**F2 residue.**~~ A base that starts a recording with a transport docked to it
  recorded itself as its own origin. **FIXED by the undock-side binding**, which is the
  fix shape this bullet named.
- ~~**Transport-scoped manifests.**~~ `StartTransportResources` covered the whole merged
  vessel. **FIXED for the origin proof**: the bind re-scopes both ends to the transport
  half, using the per-half part sets cut out of the merged part tree at capture.
  `RouteRunCargoManifest` is a separate producer and stays merged-scoped.

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
3. **The pickup is validated as a FLOW.** The TRANSPORT half's manifests at start and at the
   undock decide: a rise in an admitted resource is `Gain`, otherwise non-empty admitted cargo
   is `Carried`, otherwise `None`. **ONLY `Gain` validates.** Authority: 19.2.2 item 2 defines
   Loaded as cargo that FLOWED from another vessel ONTO the transport, and 19.2.1 makes origin
   causal. An earlier draft also admitted `Carried`, on the grounds that a docked pair is one
   `Vessel` with crossfeed; that was rejected on review because it validates residual anything -
   a pure DELIVERY undock, where the transport's cargo went DOWN, still leaves fuel and
   monopropellant aboard and would have named the delivery endpoint as the run's supply origin.
   `Carried` is kept as an observed classification, for the log only. The case this fails closed
   on - an inflow that PREDATES the recording (dock, load, quicksave, reload, start, undock) -
   is filed as ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING.

**WHY THE OLD RULE HAD TO GO, measured rather than argued.** A sibling session's rover-relay
flight (KSP.log 2026-09-02 20:29, DLL from main `96ac15dfb`, three same-craft-file rovers landed
near KSC) logged `RouteOriginProof skipped: no depot half recId=1461186781 ... seams=2
candidates=0` with `nearType=5 farType=5` on BOTH docks - two Rovers, neither typed `Base` or
`Station`. The rover-to-rover relay is the shape the roadmap is aimed at, and the discriminator
captured nothing on it. That flight also supplies the binding oracle replayed headlessly in
`StartDockedOriginBindingTests`: at UT 276.00 the half not kept is rover B and the transport delta
is LiquidFuel +200 (B binds), at UT 402.50 the half not kept is rover A and the delta is -126.8
(a delivery, and write-once keeps the origin on B).

**WHY PART SETS, AND WHY SAME-CRAFT HALVES DO NOT COLLIDE.** The bind matches the two
post-split part sets against the two captured halves. A review challenged the premise: if both
halves come from the same craft file, would they not carry identical part `persistentId`s, so the
overlap reads `BothHalvesActive` and nothing ever binds? MEASURED AND REFUTED against a real save
with three same-craft-file rovers live at once (`saves/logistics-rover-B/persistent.sfs`,
2026-09-02): rover A (22 parts), rover B (17) and rover C (18) have PAIRWISE DISJOINT part id
sets - zero shared ids across all three pairs - and distinct vessel pids and guids. That is KSP's
own rule working: a craft-baked `persistentId` is regenerated on launch when it collides with a
CURRENTLY-LIVE vessel, and two docked halves are by definition both live. The identical-set shape
is therefore not reachable from same-craft launches; it is pinned anyway
(`IdenticalHalfPartSets_RefuseToBind_FailClosed`) because the refusal is the right fail-closed
answer if a hand-authored fixture, a hand-edited save, or a future Parsek-spawned copy that
bypasses KSP's dedup ever produces it.

**WHY THE STOP STAMP IS ADVISORY.** `UnboundAtStop` is written at a recorder stop, and on the
live undock path a stop runs BEFORE the split - `OnVesselsUndocking` stops the recorder and defers
the branch one frame - so the conclusion can be premature. H57's 2026-09-02 flight measured exactly
that: the stop stamped `UnboundAtStop`, the deep clone carried it onto the parent recording, and
the bind refused with `reason=NoPairPending`. Only `BoundAtUndock` is final; an `UnboundAtStop`
proof still binds, and the bind line records `recoveredFromStopStamp=1` when it did.

**WHAT THIS DOES NOT SETTLE.** The binding follows FOCUS. In the mirror shape - the player keeps
flying the depot and the transport departs into the background - "the half the player did not keep
flying" is the transport, so the bind names the wrong half. It is inert on the recording that
carries it (a base that never moves produces no delivery window, so no route is built from it),
but it is filed as ROUTE-ORIGIN-PROOF-BIND-FOLLOWS-FOCUS-NOT-THE-RUN rather than left implicit.
