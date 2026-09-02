# G10 inter-body route composition - seam feasibility (2026-09-02)

QUESTION. Can an in-run Kerbin -> Duna-class INTER-BODY route be created AND
sealed through the M-A2 seam (`SealSlot` + `RouteCommand action=create`) over the
committed Duna-park fixtures, so that G10's `V26M` / `V26T` lanes get a live
`ClassifyRouteScope = InterBody` reading?

**VERDICT: NO.** Two independent blocking conditions, one of them a PRODUCT
blocker that no fixture, no flight and no operator save can clear.

## Blocker 1 (product, decisive): nothing ever sets `DispatchWindowPeriod`

`RouteTrajectoryLineRenderer.ClassifyRouteScope`
(`Source/Parsek/Display/RouteTrajectoryLineRenderer.cs:139-153`) reads
`Route.DispatchWindowPeriod` as "the authoritative flag (0 = same-body, the
synodic period for inter-body)". `Route.cs:142` documents the same contract.

`RouteBuilder` hard-codes it:

    Source/Parsek/Logistics/RouteBuilder.cs:486
        DispatchWindowPeriod = 0.0,

and that is the ONLY assignment in `Source/Parsek/` outside the codec's parse
(`RouteCodec.cs:97` write / `:311` read) and two hand-built synthetics in
`InGameTests/RouteLineDrawInGameTest.cs:242` / `:293`. A repo-wide grep for
`WindowPeriod` over `Source/` returns no other production writer. Therefore:

* every route created by ANY production path (Create Route dialog,
  `RouteCreationService`, the `RouteCommand action=create` seam verb) carries
  period 0;
* `ClassifyRouteScope` can only return `SameBody` or `MalformedMixedBodies` for
  such a route - `InterBody` is unreachable from creation;
* a genuinely inter-body route (members spanning Kerbin and Duna) classifies
  `MalformedMixedBodies`, and `ClassifyRouteLineSkip`
  (`RouteTrajectoryLineRenderer.cs:161-172`) then SKIPS its line entirely;
* `FilterLegsToEndpointBodies` - the ratified transfer-leg DROP the G10 entry
  names - sits behind `if (route.DispatchWindowPeriod != 0.0)`
  (`:245`), so it is unreachable too.

CONFIRMED AGAINST LIVE OPERATOR DATA, not argued. The operator's plain
`orbital supply route` save (`Kerbal Space Program/saves/orbital supply route/persistent.sfs`)
carries the real thing at line 4358: `name = Route: KSC -> Duna`,
`isKscOrigin = True`, `status = Active`, `reaimWindowBasisEngaged = True`,
`transitDuration = 8527534.18` - and `dispatchWindowPeriod = 0` (line 4367). Its
four `RECORDING_IDS` resolve to bodies {Kerbin, <transfer, no startBodyName>,
Duna, Duna}. Mixed bodies at period 0 is exactly `MalformedMixedBodies`. The
second (`Paused`) route at line 4224 carries `dispatchWindowPeriod = 0` too.

So the todo entry's guess was right: that save IS the `MalformedMixedBodies`
case, and it is NOT an `InterBody` subject. Harvesting it as `B32` would pin the
malformed reading, not the one G10 wants.

Note the basis is a DIFFERENT mechanism and is healthy: `RouteWindowBasis`
(`RouteLoopClock.cs:59-83`) is derived per tick from the loop unit, never from
the period, which is why `reaimWindowBasisEngaged = True` coexists with period 0
and why `H34-logistics-inter-body` can gate `basis=ReaimWindows` today.

Filed as ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE in `todo-and-known-bugs.md`.

## Blocker 2 (subject data): no fixture carries an inter-body dock

`RouteCommand action=create` walks `ClassifyCreateRefusal`
(`Source/Parsek/TestCommands/TestCommandRouteCommand.cs:260-272`): UnknownTree ->
CandidateDismissed -> TreeNotSealed -> CandidateIneligible -> AlreadyPromoted.
`CandidateIneligible` carries the `RouteAnalysisStatus`, and
`RouteAnalysisEngine` returns `MissingRouteProof`
(`RouteAnalysisEngine.cs:456-457, 1160`) when the source recording has no
`RouteConnectionWindows`.

Only four committed fixtures carry `ROUTE_CONNECTION_WINDOW` at all, and all
four are Kerbin-only (`startBodyName = Kerbin` on every member):

| fixture | dockUT / undockUT | transferKind | bodies |
| --- | --- | --- | --- |
| `bdock-recorded` (persistent.sfs:2111) | 8949.268 / 8950.588 | DockingPort | Kerbin |
| `depot-route-recorded` (:1816) | 17478.249 / 17594.509 | DockingPort | Kerbin |
| `rover-route-recorded` (:1034) | 513.540 / 594.280 | DockingPort | Kerbin |
| `rover-route-career` (:1171) | 513.540 / 594.280 | DockingPort | Kerbin |

`depot-route-recorded` already carries the stored `name = Route: Kerbin -> Kerbin`
with `dispatchWindowPeriod = 0` (persistent.sfs:2446, :2452) - V18T's subject.

The Duna fixtures carry ZERO connection windows. `duna-park-recorded` (tree
`ced78481`, 14 recordings) and `duna-one-recorded` (tree `1ccdb192`, 13
recordings) both grep 0 hits for `ROUTE_`, `DOCK`, `UNDOCK` and
`routeOriginProof`; their members are Kerbin-start (the launch) plus Duna-start
(arrival / probe / EVA), with six `isDebris = True` leaves. There is no depot at
Duna, no dock, no delivery manifest. `SealSlot` on either tree would succeed and
then `RouteCommand action=create` would answer
`candidate-ineligible MissingRouteProof`.

## The predicate a builder script would assert (H34 shape)

Modelled on `RouteInterBodyBuilderShapeInGameTest`'s own preconditions, a
`harness/tools/build_interbody_route_recorded.py` would have to assert, over the
harvested save:

    tree = committed_tree(route.backingMissionTreeId)
    assert all(r.mergeState == "Immutable" for r in tree.recordings)   # SealSlot
    assert route.status in {"Active", "Paused"}
    assert route.recordedDockUT > 0 and destination window kind in {DockingPort, Grapple}
    bodies = {r.startBodyName or r.segmentBodyName for r in route.recordingIds}
    assert len(bodies) >= 2 and "Kerbin" in bodies                     # inter-body membership
    assert route.dispatchWindowPeriod != 0.0                           # <-- FAILS TODAY

The last clause fails on every save that exists or can be flown today, and it is
the clause `V26M` / `V26T` would be built to read. Every earlier clause is
satisfiable by an operator flight (spec in the G10 roadmap entry and in
SUBJECT-CANDIDATE-INTERPLANETARY-ROUTE).

## Consequence for G10

G10 is BLOCKED on a product change, not on a subject. The order is: (1) make
`RouteBuilder` populate the synodic `DispatchWindowPeriod` for a cross-parent
build (or retire the field and classify scope from member bodies + re-aim
basis); (2) fly the operator save the roadmap entry now specifies; (3) harvest
B32; (4) author V26M / V26T. Doing (2) first buys a fixture that pins the
malformed reading and nothing else.
