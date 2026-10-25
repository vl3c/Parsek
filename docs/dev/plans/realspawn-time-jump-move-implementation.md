# Real Spawn Control Time Jump: Move the Player - Implementation Plan

Design: `docs/dev/design-realspawn-time-jump-move.md` (owner rulings 2026-10-25). This plan is
the build order and the file map. Base: main ef863bd. Reviewed 2026-10-25 by a clean-context
agent; its eight findings are folded in (noted "review N").

Two steps, each its own PR, each landed only after its lanes fly green:

- **Step 1, Leaves**: the whole new mechanism, wired only to `WarpToDeparture`.
- **Step 2, Ready**: `WarpToRecordingEnd` and `Warp to Next Spawn` switched to it for an
  ORBITAL player vessel, then the jump shift deleted. A landed / splashed / prelaunch player
  vessel keeps today's `ExecuteJump` path unchanged (review 4).

---

## Step 1: Leaves

### 1.1 Pure core (new `Source/Parsek/TimeJumpPlacement.cs`)

Positions and velocities are Zup body-relative (the `Orbit.pos` / `Orbit.vel` /
`getRelativePositionAtUT` / `getOrbitalVelocityAtUT` frame). Nothing swizzles them. Attitude is
NOT handled here (review 6, see 1.3).

- `struct LocalFrame { Vector3d ur, ut, un, omega; }` and `bool TryBuild(Vector3d r, Vector3d v,
  out LocalFrame f)`: `ur = r/|r|`, `un = (r x v)/|r x v|`, `ut = un x ur`,
  `omega = (r x v)/|r|^2`. Degenerate input (`|r x v|` ~ 0) returns false; the gate refuses.
- `RelativeState Capture(LocalFrame f0, Vector3d rRef0, Vector3d vRef0, Vector3d r, Vector3d v)`:
  `d = E0^T (r - rRef0)`, `e = E0^T ((v - vRef0) - omega0 x (r - rRef0))`.
- `void Place(LocalFrame f1, Vector3d rRef1, Vector3d vRef1, RelativeState s, out Vector3d r, out Vector3d v)`:
  `r = rRef1 + E1 d`, `v = vRef1 + E1 e + omega1 x (E1 d)`.
- `bool IsGroupMember(...)`: distance to A at most `ParsekFlight.NearbySpawnRadius` (250 m) and
  relative speed to A from ORBIT states at most `MaxRelativeSpeed` (2 m/s) (ruling Q2). Surface
  vessels and ghost map ProtoVessels never join.
- `double EstimateBurnDeltaV(elementsT0, elementsT1)` (ruling Q6, review 1): the reference's own
  manoeuvring between T0 and T1, measured on the ORBIT, not on a state difference at T1 (a state
  difference grows a 0.8 m/s trim into about 12 m/s over 20 minutes through along-track drift).
  Small-change Gauss estimate: `max(v |da| / (2a), v |de| / 2) + v * dplane`, where `de` is the
  eccentricity-vector difference and `dplane` the angle between the two orbit normals. For a
  single prograde impulse on a circular orbit it returns the impulse. Elements are compared in
  a rotation-safe way (1.2: both orbits resolved at the same body rotation).
- `MoveGateVerdict EvaluateGates(...)`, in order: `Atmosphere` (A, a group member, or the
  reference at T0 or T1 below its body's atmosphere top), `BodyChange` (reference body at T1
  differs from T0, or from A's), `Burns` (`EstimateBurnDeltaV > 2 m/s`), `Degenerate`,
  `DestinationTaken`. Returns the first failure plus a numeric detail for the log. Only called
  for an orbital A (a landed A never reaches it, review 4).
- `SlideResult ComputeSpawnRoomSlide(...)` (used by Step 2; built and tested now): the smallest
  along-track shift (away from the reference; behind it when the along-track offset is zero)
  that clears every overlap from an injected predicate, in 1 m steps up to 250 m, then +5 m.
  Cap exceeded -> `DestinationTaken`.

Unit tests (`TimeJumpPlacementTests.cs`):
- Map round trip: circular LKO and e = 0.05 references, jumps 10 s / 600 s / 1200 s / 6 h,
  along-track, radial and normal offsets with relative velocity: local components at T1 equal
  the T0 ones to 1e-6.
- Burn estimate: 0.8 m/s along-track impulse at T0 + 10 s with a 1200 s jump returns about 0.8
  (passes); a 30 m/s orbit raise refuses; a 2 m/s plane change refuses at the edge (1.99 /
  2.01); a pure coast returns 0.
- Gates: each refusal reason; group edges (249 m / 1.9 m/s joins, 251 m or 2.1 m/s out, surface
  and ghost map vessels never join); slide (no overlap 0, smallest clearing shift + margin, cap
  refuses).

### 1.2 Reference states

`TimeJumpReference.TryResolveLeaves(Recording rec, double t0, double t1, ...)`: the OrbitSegment
covering T0 (`TrajectoryMath.FindOrbitSegment`, the one `SelectiveSpawnUI.ComputeDepartureInfo`
reads, so T1 <= its `endUT`), built through the production construction
(`MapRenderProbe.BuildOrbitFromSegment`), evaluated with `getRelativePositionAtUT` /
`getOrbitalVelocityAtUT` at T0 and T1. One conic, so no rotation dependence and the burn
estimate is 0 by construction.

Logged once per jump: the gap between the reference's T0 state and the drawn ghost transform
(`TryResolvePlaybackWorldPosition`); ruling Q7, the recording wins.

### 1.3 Shell (`TimeJumpManager.ExecutePlacementJump`, new), over two frames (review 2, 5)

Frame F (the click):
1. Stop warp; `NotePlayheadSweepBeforeJump`.
2. Build the group from orbit states and capture, in one synchronous pass, each member's
   `RelativeState` against the reference's T0 state, and its attitude encoded against the
   REFERENCE's world orbital frame at T0 with the recorder's convention
   (`LookRotation(orbitalVelocity_WORLD, radialOut_WORLD)`, the
   `FlightRecorder.StampVesselOrbitalFrameRotation` contract; review 6: no Zup rotation is ever
   applied to an attitude).
3. Re-evaluate the gates; a failure logs `Placement jump refused: reason=...` and returns with
   nothing changed (the UI greyed the button already; this is the race backstop).
4. `PutLoadedVesselsOnRails()` and `OrbitPhysicsManager.HoldVesselUnpack(n)` so stock, not
   Parsek, unpacks later. Packing fires `onVesselGoOnRails`, so a live recorder on A opens its
   orbit segment on A's T0 orbit (existing).
5. `Planetarium.SetUniversalTime(T1)`, `ArmTimeJumpLaunchAutoRecordSuppression`,
   `ConverterJumpPolicy.Apply` (as `ExecuteJump`). Arm the pending placement.

Frame F+1 (after `Planetarium.FixedUpdate` has rebuilt body rotation and Zup for T1):
6. Resolve the reference's T1 state now (Leaves: the segment; Ready: the spawner's resolver,
   see Step 2), so a resolver that reads live body rotation sees T1's (review 2).
7. In one synchronous pass, for each member: `orbit.UpdateFromStateVectors(r', v', body, T1)`
   plus the orbit driver update so the packed vessel is drawn there this frame; attitude
   decoded against the reference's T1 world orbital frame and written to the packed vessel.
   Non-group loaded vessels are untouched: their orbits carry them.
8. Recorder: for A, the existing teleport bookkeeping (`FlightRecorder.OnCheatTeleport` with a
   `cause` argument, logged `cause=time-jump`; on rails and same body, so `OnRailsRecapture`:
   the T0 segment closes at T1 and a fresh one opens on the placed orbit). For a background
   group member, the new `BackgroundRecorder.RecaptureOnRailsOrbit(pid, ut)`, built on the
   close-and-reopen pattern of `CheckpointAllVessels`. `NotifyRecorder` keeps writing TIME_JUMP.
9. Stock unpacks after the hold. Its go-off-rails then runs the recorder's ordinary path with
   valid live positions: the re-captured segment closes at the unpack UT (not zero-length) and
   the boundary point is sampled on the placed orbit (review 5). Attitude is re-asserted on the
   first unpacked frame if stock reset it.
10. `RecalculateLedgerAfterTimeJump("placement", T1)`.
11. One summary line: `Placement jump: ref=<recId> kind=<Leaves|Ready> T0= T1= group=<n>
    moved=<pids> refGapToDrawn=<m> slide=<m>`, plus per member `Placed vessel: pid= name=
    offsetT0=(r,t,n) offsetT1=(r,t,n) dvRel=`.

A scene change or quickload between F and F+1 drops the pending placement (logged); the clock
has moved and every vessel keeps its natural orbit, which is a plain forward jump.

`ExecuteJump` stays for the automation `TimeJump` seam verb, for a landed player vessel, and
(until Step 2) for Ready.

### 1.4 UI

- `NearbySpawnCandidate` gains `moveRefusal` (enum) and `moveRefusalDetail`, filled by the
  proximity scan (`ParsekFlight` near line 29447) from the pure gates, for `willDepart` rows in
  Step 1. Reference elements are cached per recording id and `RecordingStore.StateVersion`; the
  scan uses a quiet resolver variant, so nothing logs per scan (review 8). A refusal is logged
  once per change (`VerboseOnChange`).
- `SpawnControlPresentation.BuildRowPresentation`: a refused `Leaves` row draws Warp disabled
  with the refusal as `WarpButtonDisabledReason` (the existing hover, `DisabledHoverEcho`),
  inside `TooltipEchoBudgetTests`.
- `SelectiveSpawnUI.FindNextSpawnCandidate` skips refused rows.
- `WarpToDeparture` calls `ExecutePlacementJump` (orbital A) or keeps `ExecuteJump` (landed A,
  where no Leaves row exists in practice).

### 1.5 Lanes (V's PC, standing approval; ask the coordinator for a PC turn)

- New preset `leaves-departure` in `SyntheticRecordingTests` on the `eva2-lko-crewed` host
  (CI-9's shape): a parentless background recording whose orbit tail sits on the Kerbal X's own
  orbit about 140 m ahead, ending its OrbitSegment 5 minutes after load with a prograde burn
  onto a higher orbit, recording ending later. Spec `RSC-2-leaves-warp`: `UiAction op=warp` on
  its Leaves row; require the placement summary line, the ghost-to-A distance after the jump
  within 5 m of the click distance, no spawn, and the departure burn's FX lines while A is
  within 1 km. Negative control: today's DLL reads about 650 km (2.28 km/s x 285 s).
- The host's 16 m Kerbal X Probe is itself a group member (review, lane details): RSC-2 also
  requires its offset to A unchanged within 1 m and a `Placed vessel:` line for both pids.
  No separate group lane is needed.
- Recording facet on RSC-2 (`StartRecording` before the press): an orbit segment ending at T1,
  a fresh one starting at T1 and ending at the unpack UT (length > 0), the `cause=time-jump`
  line, and the first Absolute point at or after T1 within 5 m of the placed orbit (review 5).
- RSC-1 (landed Ready) re-flown as a regression guard: unchanged.

Docs in the same PR: design status line, CHANGELOG entry (the Leaves Warp now keeps you beside
the ghost), todo entry, autotest-status rows for the new lane.

---

## Step 2: Ready

1. `TimeJumpReference.TryResolveReady`:
   - T1: the state the spawner will use for this recording's end
     (`VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState` with lag 0, else the endpoint
     state), called in frame F+1, so a tail-seeded end (`OrbitReseed.TryFromRecordedTailPair`,
     which converts recorded lat/lon with LIVE body rotation, `OrbitReseed.cs:878`) is built at
     T1's rotation exactly as the spawn will build it (review 2).
   - T0: the covering OrbitSegment, else the position-pair resolver over the points around T0
     (`TryFromRecordedTailPair` on the bracketing points; the scan runs at T0, so live rotation is
     the right one). Never `TrajectoryPoint.velocity`, which is in the recording session's
     possibly rotating world frame (review 3).
   - Burn gate at scan time: elements of the T1 end orbit compared rotation-safely: `a`, `e`
     and inclination directly; the node line and eccentricity vector after removing the body's
     rotation over `T1 - now` about its axis (exact for the tail-pair conversion). Cached per
     recording id and `StateVersion`, quiet variant (review 8).
2. Spawn room: `ComputeSpawnRoomSlide` with the predicate built from the existing guard
   (`SpawnCollisionDetector.ComputeVesselBounds` of the end snapshot, `BoundsOverlap` with the
   spawn check's padding) against each placed member's bounds (ruling Q7b).
3. `WarpToRecordingEnd` and `WarpToNextCraftSpawn` call `ExecutePlacementJump` when A is orbital
   and keep `ExecuteJump` when A is landed / splashed / prelaunch (review 4). Ready rows of an
   orbital A get the greyed reasons.
4. Other crossed ghosts (ruling Q3): no capture, so they spawn on their records through the
   ordinary path.
5. Lanes:
   - Orbital, changed: CI-9 re-armed (placement lines replace `Terminal-orbit jump shift armed`
     / `Jump-shifted terminal orbit spawn`; separation window kept 100-199 m; the host probe is
     a group member and its `Placed vessel:` line is expected; produced-save facet: the spawned
     tip's orbit equals its recorded terminal orbit at the spawn UT), VN-4 and VN-10 re-flown
     with the same allowance, a spawn-room lane (scenario d: tip parked 6 m from A), a replay
     lane (scenario g: RealSpawn, dock, commit, rewind, replay; dock within metres of the
     recorded end).
   - Landed, must stay green unchanged (review 4): RSC-1, MINE-7 / 8 / 9 / 13, CI-6 / 7 / 8 /
     10 / 11, VN-3, VN-12, and the in-game `RealSpawnControl_WarpToRecordingEnd_OnPad`.
6. Then, with the results archive grepped for `Terminal-orbit jump shift armed` outside CI-9
   (none expected), delete `TimeJumpTerminalOrbitShift.cs` and its tests, the `phaseLagSeconds`
   parameter of the spawn resolvers, `VesselGhoster.TryResolveJumpShiftedTipSpawn` /
   `SelectChainTipCollisionCheckPosition` / `ShouldWalkBackBlockedTip` /
   `LogJumpShiftedCollisionCheck` and their call sites, the lag branch of the blocked-tip ghost,
   the release / clear calls, and the `CaptureForJump` step of `ExecuteJump` (the seam verb and a
   landed A keep a plain epoch-shift freeze; on the surface nothing is captured anyway). Close
   REALSPAWN-JUMP-SHIFT-RESIDUE.

---

## Risks

- The pack / place / unpack shell: the attitude of a packed vessel across the hold,
  Krakensbane and floating origin after a large move, robotics / docking-port drift on the
  extra rails cycle. Only a flight proves it; RSC-2 is first.
- The orbit-driver update after `UpdateFromStateVectors` on a packed vessel: if the vessel is
  not moved that frame, the boundary point reads the old position; RSC-2's boundary facet
  catches it.
- The two-frame split: Zup and body rotation differ between F and F+1. Only frame-free
  quantities (local-frame components, orbital-frame-relative attitude) cross the boundary;
  every vector is rebuilt in F+1's frame.
