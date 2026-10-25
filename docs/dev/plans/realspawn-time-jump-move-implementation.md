# Real Spawn Control Time Jump: Move the Player - Implementation Plan

Design: `docs/dev/design-realspawn-time-jump-move.md` (owner rulings 2026-10-25). This plan is
the build order and the file map. Base: main ef863bd.

Two steps, each its own PR, each landed only after its lanes fly green:

- **Step 1, Leaves**: the whole new mechanism, wired only to `WarpToDeparture`.
- **Step 2, Ready**: `WarpToRecordingEnd` and `Warp to Next Spawn` switched to it, then the
  jump shift deleted.

---

## Step 1: Leaves

### 1.1 Pure core (new `Source/Parsek/TimeJumpPlacement.cs`, no Unity calls beyond `Vector3d`)

All vectors are Zup body-relative (the `Orbit.pos` / `Orbit.vel` /
`getRelativePositionAtUT` / `getOrbitalVelocityAtUT` frame). Nothing swizzles.

- `struct LocalFrame { Vector3d ur, ut, un, omega; }` and
  `LocalFrame Build(Vector3d r, Vector3d v)`: `ur = r/|r|`, `un = (r x v)/|r x v|`,
  `ut = un x ur`, `omega = (r x v)/|r|^2`. Degenerate input (`|r x v|` ~ 0) returns a flag;
  the gate refuses.
- `RelativeState Capture(LocalFrame f0, Vector3d rRef0, Vector3d vRef0, Vector3d r, Vector3d v)`:
  `d = E0^T (r - rRef0)`, `e = E0^T ((v - vRef0) - omega0 x (r - rRef0))`.
- `void Place(LocalFrame f1, Vector3d rRef1, Vector3d vRef1, RelativeState s, out Vector3d r, out Vector3d v)`:
  `r = rRef1 + E1 d`, `v = vRef1 + E1 e + omega1 x (E1 d)`.
- `QuaternionD FrameRotation(LocalFrame f0, LocalFrame f1)` = `E1 E0^T`, applied to attitude.
- `GroupMember SelectGroup(...)`: a loaded orbital vessel joins the group when its distance to
  A is at most `ParsekFlight.NearbySpawnRadius` (250 m) and its relative speed to A, taken
  from ORBIT states (not transform finite differences), is at most `MaxRelativeSpeed`
  (2 m/s) (ruling Q2). Surface vessels and ghost map ProtoVessels never join.
- `MoveGateVerdict EvaluateGates(...)`: in order, `Atmosphere` (A, a group member, or the
  reference at T0 or T1 below its body's atmosphere top), `BodyChange` (reference body at T1
  differs from T0, or from A's), `Burns` (coasting gate, ruling Q6: propagate the reference's
  T0 state on a two-body conic to T1 and compare with its recorded T1 state;
  `dv + |dr| / (T1 - T0)` above 2 m/s refuses), `Degenerate`, `DestinationTaken`. Returns the
  first failure plus a numeric detail for the log.
- `SlideResult ComputeSpawnRoomSlide(...)` (used by Step 2; built and tested now): the
  smallest along-track shift (away from the reference; behind it when the along-track offset
  is zero) that clears every overlap reported by an injected overlap predicate, in 1 m steps
  up to a cap of 250 m, then +5 m margin. Cap exceeded -> `DestinationTaken`.

Unit tests (`TimeJumpPlacementTests.cs`):
- Map round trip: circular LKO and e = 0.05 references, jumps 10 s / 600 s / 1200 s / 6 h,
  along-track, radial and normal offsets with relative velocity: `Capture` at T0 then
  `Place` at T0 returns the input to 1e-6 m / 1e-9 m/s; at T1 the local components equal the
  T0 ones to 1e-6.
- Attitude: a port facing the reference at T0 faces it at T1.
- Gates: each refusal reason with its threshold edge (1.99 / 2.01 m/s), the coast-passes
  case, a 0.8 m/s RCS-trim ghost passes, a 30 m/s orbit raise refuses.
- Group: 249 m / 1.9 m/s joins, 251 m or 2.1 m/s stays out, surface and ghost map vessels
  never join.
- Slide: no overlap -> 0; overlap -> smallest clearing shift + margin; cap -> refuse.

### 1.2 Reference states

New `TimeJumpReference.TryResolveLeaves(Recording rec, double t0, double t1, out ...)`: the
OrbitSegment that covers T0 (`TrajectoryMath.FindOrbitSegment`; the same one
`SelectiveSpawnUI.ComputeDepartureInfo` reads, so T1 <= its `endUT`), built into a KSP `Orbit`
through the production construction (`MapRenderProbe.BuildOrbitFromSegment`, the
`TrajectoryMath.EvaluateOrbitSegmentAtUT` construction), evaluated with
`getRelativePositionAtUT` / `getOrbitalVelocityAtUT` at T0 and T1. Attitude reference: the
segment's `orbitalFrameRotation` contract (encode / decode against world velocity + radial),
so the stored rotation turns with the frame.

Logged once per jump: the gap between this T0 state and the drawn ghost transform
(`TryResolvePlaybackWorldPosition`), ruling Q7: the recording wins.

### 1.3 Shell (`TimeJumpManager.ExecutePlacementJump`, new)

Same frame of work as `ExecuteJump`, minus the epoch shift and the terminal-orbit shift:

1. Stop warp; `NotePlayheadSweepBeforeJump`.
2. Build the group and capture each member's `(orbit.pos, orbit.vel, body)` and world
   attitude encoded against the reference's T0 velocity / radial (one synchronous pass).
3. Re-evaluate the gates; any failure: log `Placement jump refused: reason=...` and return
   with nothing changed (the UI greyed the button already; this is the race backstop).
4. `PutLoadedVesselsOnRails()` (existing). Packing fires `onVesselGoOnRails`, so a live
   recorder on A opens its orbit segment on A's T0 orbit (existing behaviour).
5. `Planetarium.SetUniversalTime(T1)`, `ArmTimeJumpLaunchAutoRecordSuppression`,
   `ConverterJumpPolicy.Apply` (as `ExecuteJump`).
6. For each group member: `orbit.UpdateFromStateVectors(r', v', body, T1)` then the orbit
   driver update so the packed vessel is drawn there this frame. Non-group loaded vessels are
   not touched: their orbits carry them.
7. Recorder: for A, call the existing teleport bookkeeping
   (`FlightRecorder.OnCheatTeleport`, given a `cause` argument so the log line reads
   `cause=time-jump`; same-body and on rails, so `OnRailsRecapture`: the T0 segment closes at
   T1 and a fresh one opens on the placed orbit). For a background-recorded group member, the
   new `BackgroundRecorder.RecaptureOnRailsOrbit(pid, ut)` (closes its on-rails segment at T1,
   opens one on the placed orbit). `NotifyRecorder` keeps writing TIME_JUMP.
8. `TakeVesselsOffRails`; hold the unpack (`OrbitPhysicsManager.HoldVesselUnpack`) for the
   frames the attitude write needs; write each member's attitude decoded against the
   reference's T1 velocity / radial after the next `Planetarium.FixedUpdate`.
9. `RecalculateLedgerAfterTimeJump("placement", T1)`.
10. One summary line: `Placement jump: ref=<recId> kind=<Leaves|Ready> T0= T1= group=<n>
    moved=<pids> refGapToDrawn=<m> slide=<m>`, plus per-member `Placed vessel: pid= name=
    offsetT0=(r,t,n) offsetT1=(r,t,n) dvRel=`.

`ExecuteJump` stays as is for the automation `TimeJump` seam verb and (until Step 2) Ready.

### 1.4 UI

- `NearbySpawnCandidate` gains `moveRefusal` (enum) and `moveRefusalDetail`, filled by the
  proximity scan (`ParsekFlight` around line 29447) from the pure gates, for `willDepart`
  rows in Step 1.
- `SpawnControlPresentation.BuildRowPresentation`: a `Leaves` row with a refusal is drawn
  with Warp disabled and the refusal text as `WarpButtonDisabledReason` (the existing hover
  channel, `DisabledHoverEcho`). Texts per design 4.3, kept inside `TooltipEchoBudgetTests`.
  The Leaves Warp hover drops "it does not spawn here" only if the budget needs the room;
  otherwise unchanged.
- `SelectiveSpawnUI.FindNextSpawnCandidate` skips refused rows.
- `WarpToDeparture` calls `ExecutePlacementJump` instead of `ExecuteJump`.

### 1.5 Lanes (V's PC, standing approval; ask the coordinator for a PC turn)

- New preset `leaves-departure` in `SyntheticRecordingTests` on the `eva2-lko-crewed` host
  (CI-9's shape): a parentless background recording whose orbit tail sits on the Kerbal X's
  own orbit about 140 m ahead, ending its OrbitSegment 5 minutes after load with a
  prograde burn onto a higher orbit, recording ending later. Spec `RSC-2-leaves-warp`:
  `UiAction op=warp` on its Leaves row, then require the placement summary line, ghost-to-A
  distance after the jump within 5 m of the click distance (kRPC read or a log token), no
  spawn, the departure burn's FX lines while A is within 1 km. Negative control: today's DLL
  reads about 1,100 km.
- Spec `RSC-3-placement-group`: the same host with a second real vessel (the Kerbal X
  Probe, undocked, 200 m off) inside the group; require its offset to A unchanged within 1 m
  after the jump and a `Placed vessel:` line for both pids.
- Recording facet: either lane with `StartRecording` before the press: the committed
  recording has an orbit segment ending at T1 and a fresh one starting at T1, and a
  `cause=time-jump` teleport line.
- Re-fly RSC-1 (landed Ready) as a regression guard: unchanged.

Docs in the same PR: design status line, CHANGELOG entry (the Leaves Warp now keeps you beside
the ghost), todo entry update, autotest-status rows for the new lanes.

---

## Step 2: Ready

1. `TimeJumpReference.TryResolveReady`: the reference at T1 is the state the spawner will use
   for this recording's end (`VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState` with
   lag 0, else the endpoint state); at T0 the covering OrbitSegment or the interpolated
   points. The coasting gate now does real work (physics points between T0 and T1).
2. Spawn room: `ComputeSpawnRoomSlide` with the predicate built from the existing guard
   (`SpawnCollisionDetector.ComputeVesselBounds` of the end snapshot,
   `BoundsOverlap` with the spawn check's padding) against each placed member's bounds
   (ruling Q7b).
3. `WarpToRecordingEnd` and `WarpToNextCraftSpawn` call `ExecutePlacementJump`; Ready rows get
   the greyed reasons.
4. Other crossed ghosts (ruling Q3): no capture, so they spawn on their records through the
   ordinary path.
5. Lanes: CI-9 re-armed (placement lines replace `Terminal-orbit jump shift armed` /
   `Jump-shifted terminal orbit spawn`, separation window kept 100-199 m, produced-save facet:
   the spawned tip's orbit equals its recorded terminal orbit at the spawn UT), VN-4 and VN-10
   re-flown, a spawn-room lane (scenario d: tip parked 6 m from A), a replay lane (scenario g:
   RealSpawn, dock, commit, rewind, replay; dock within metres of the recorded end).
6. Then, with the results archive grepped for `Terminal-orbit jump shift armed` outside CI-9
   (none expected), delete `TimeJumpTerminalOrbitShift.cs` and its tests, the `phaseLagSeconds`
   parameter of the spawn resolvers, `VesselGhoster.TryResolveJumpShiftedTipSpawn` /
   `SelectChainTipCollisionCheckPosition` / `ShouldWalkBackBlockedTip` /
   `LogJumpShiftedCollisionCheck` and their call sites, the lag branch of the blocked-tip
   ghost, the release / clear calls, and the `CaptureForJump` step of `ExecuteJump` (the seam
   verb keeps a plain epoch-shift freeze). Close REALSPAWN-JUMP-SHIFT-RESIDUE.

---

## Risks

- The pack / place / unpack shell (attitude of a packed vessel, Krakensbane and floating
  origin after a large move, robotics / docking-port drift on the extra rails cycle). Only a
  flight proves it; RSC-2 is first.
- The orbit-driver update after `UpdateFromStateVectors` on a packed vessel: if the vessel is
  not moved that frame, the first off-rails frame reads the old position. The lane's
  separation token catches it.
- The recorder's off-rails path after the teleport bookkeeping (`OnVesselGoOffRails`
  finalizes the re-captured segment): covered by the recording facet.
