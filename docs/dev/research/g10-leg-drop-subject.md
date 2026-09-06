# G10 leg-drop subject - feasibility (2026-09-06)

QUESTION. What subject makes `RouteTrajectoryLineRenderer.FilterLegsToEndpointBodies`
DROP a leg on a driven run, so G10's last open reading
(`Route line build: ... transferDropped=[1-9]`) can be taken?

**VERDICT: NO SUBJECT IS REACHABLE, and the standing explanation of why is WRONG.**
The blocker is not "no committed fixture carries a physics-frame transfer stretch"
(three do, one of them inside G10's own subject). The blocker is that
`Route.RecordingIds` names composition RUN HEADS, and the renderer resolves each entry
to that single `Recording`, so every chain-CONTINUATION segment of a member run - which
is where an interplanetary transfer always lands - is invisible to the route line.

## The predicate: what exactly makes a leg droppable

A leg is dropped iff all four hold (`Display/RouteTrajectoryLineRenderer.cs`):

1. `IsInterBodyByEndpoints(route)` (:220) - the route's `Origin.BodyName` and at least
   one `Stops[].Endpoint.BodyName` are both known and DISAGREE. Only then does
   `BuildRouteMemberLegs` (:424) call the filter at all.
2. `ResolveEndpointBodies` (:437) succeeds over the built groups: `originBody` = the
   `bodyName` of the leg with the EARLIEST `startUT`, `destinationBody` = the leg with
   the LATEST `endUT`, both across ALL members.
3. `originBody != destinationBody` (:502, the round-trip stand-down; equal means keep
   everything and report `transferDropped=0`).
4. At least one surviving leg carries a `bodyName` equal to neither (:508-:520).

A LEG is a merged run of >= 2 UT-sorted non-orbital `TrajectoryPoint`s sharing one
`bodyName`, not covered by an `OrbitSegment` interval
(`GhostTrajectoryPolylineRenderer.BuildLegsForRecording` :2810-:2847, `FlushPolylineRun`
:2884), built ONLY from `resolve(recId)` for `recId in route.RecordingIds`, and kept
only when `legStartUT < route.RecordedDockUT` (`LegWithinDockClip` :355).

**So the subject requirement is exact: one id IN `route.RecordingIds` must resolve to a
recording whose own sidecar carries >= 2 consecutive non-orbital samples on a body that
is neither the earliest-leg body nor the latest-leg body, before the dock UT.**

## Why no such member exists anywhere (surveyed, not argued)

`Route.RecordingIds` is `RouteBackingMission.ComputeMemberRecordingIds` (:363) ->
`StripSegMarker(node.HeadLegId)` (:446/:466). `MissionCompositionBuilder` walks a RUN
via `ContinuationSuccessor` (`MissionComposition.cs:150-158`), so every chain
continuation is inside the head's run, and every interval it produces keys as
`headLegId` or `headLegId + "/segN"` (:265-:295). All of those strip to the RUN HEAD id.
`resolve` is `RecordingStore.TryFindCommittedRecordingById` (:690) - one recording, no
chain walk.

(a) COMMITTED FIXTURES. Only two carry a `ROUTE` node at all: `depot-route-recorded`
(1, same-body) and `interbody-route-recorded` (2). Every member of all three routes is
single-body: Kerbin/Kerbin/Kerbin/Kerbin, Kerbin/Kerbin/Mun/Mun,
Kerbin/Kerbin/Duna/Duna. The three-body carriers in that same fixture are
`36c7688b...` (Kerbin 628 / **Sun 108** / Duna 664) and `cc8ec5e4...` (Kerbin 622 /
**Sun 199** / Duna 160) - both `chainIndex = 1`, i.e. continuations of the runs headed by
`d23e453b...` (Kerbin 242 only) and `ffffab0a...`. `36c7688b` sits in the Duna route's
own `CREATION_TREE_RECORDINGS` and its Sun samples form TWO consecutive runs of 50 and 52
points over UT 68378174.7 - 68378313.4, ~4.0 Ms before the dock clip 72353218.8: it would
produce Sun legs and drop them, if only it were resolvable.
Five fixtures carry an UNPROMOTED proof window (`bdock-recorded`,
`rover-route-{recorded,career}`, `rover-relay-{,c-}recorded`) so a route could be created
over them in-run - all five are Kerbin-only in every recording.
Fixtures that DO carry a three-body RUN HEAD - `duna-park-recorded` (Kerbin/Sun/Duna),
`duna-one-recorded` (Kerbin/Sun/Duna/**Ike**), `duna-direct-recorded`,
`mun-minmus-recorded` (Kerbin/Mun/Minmus) - carry ZERO `ROUTE_CONNECTION_WINDOWS`, so
`RouteAnalysisEngine` answers `MissingRouteProof` and `ClassifyCreateRefusal` refuses
`candidate-ineligible MissingRouteProof` (`TestCommandRouteCommand.cs:260-273`).

(b) OPERATOR DEV SAVES (read-only survey of all 19 under
`Kerbal Space Program/saves`). Four carry routes: `l2` (2x Kerbin->Kerbin),
`logistics-rover-c` (Kerbin), `orbital supply route DELIVERY test` (Kerbin->Kerbin), and
`orbital supply route` (the fixture's own source). None has a third-body member.
`orbital supply route CLEAN` is the one live candidate - 3 proof windows, ZERO `ROUTE`
nodes, so `alreadyPromoted` is false and a seam create would compute a REAL member set -
but it is the same campaign: its Sun carriers are the same two `chainIndex = 1`
continuations, so a created route reads `transferDropped=0` for the same reason. `s15`
holds the three-body run heads (`61e91771`, `aa48920e`) and ZERO proof windows.

(c) BY CONSTRUCTION. Dead under this repo's own doctrine. The member list is DERIVED
state; no offline builder can run `ComputeMemberRecordingIds`, and the two in-run creation
paths are closed (already promoted, or no proof). Authoring `RECORDING_IDS` or a
`ROUTE_CONNECTION_WINDOWS` node by hand is exactly the edit `harness/lib/savepatch.py`
refuses by contract ("THE PARSEK PAYLOAD IS NEVER TOUCHED ... editing one would make the
patch unfalsifiable"), and `[[fixture.liveState]]` is FLIGHTSTATE-only, so the per-lane
route is closed too.

## The consequence, and the two ways out

`RouteBackingMission.cs:337-346` states the intent in its own words: the route "widens
`RecordingIds` / `SourceRefs` to cover the whole rendered path". The renderer reads those
ids as whole recordings, so the rendered path is only each run's HEAD. On G10's own
subject that means the Kerbin -> Duna route line draws a 174 s pad ascent plus the depot's
Duna legs and omits the 8.5 Ms journey (628 + 108 + 664 samples). It is not inter-body
specific: `depot-route-recorded`'s member `44129e52` (238 pts) likewise hides its
continuation `a85a7ae0` (1324 pts, the rendezvous and dock approach). Filed as
ROUTE-LINE-MEMBER-DROPS-CONTINUATION-SEGMENTS.

1. **CHEAPEST, and a product fix rather than a fixture: resolve a member id to its
   through-line RUN, not to one recording.** The existing committed
   `interbody-route-recorded` then drops 1-2 Sun legs with NO new flight and no new
   fixture. Re-pins `members`/`groups`/`legs` on B32 / V26M / V26T, so it is a lane
   re-flight, not a doc edit. Decide the defect first; G10 must not be closed by a
   fixture built around it.
2. **If the run-head member set is ruled INTENDED**, the reading needs an operator save.
   Spec in the G10 roadmap entry.

ASCII only; no em dashes.
