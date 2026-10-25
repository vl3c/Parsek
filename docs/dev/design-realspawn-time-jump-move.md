# Parsek Real Spawn Control Time Jump: Move the Player - Design Note

*Status: APPROVED (owner, 2026-10-25). Step 1 (`Leaves`) shipped on branch `claude/project-thread-1gkt0y`, lane
`RSC-2-leaves-warp` LIVE-PROVEN `2026-10-25_1302`. Step 2 (`Ready`) open. Replaces the "nothing moves" freeze of the
Real Spawn Control Warp in orbit. Owner rulings: interview of 2026-10-25, thread "Redesign time
jump to move the player". Source analysis (code trace, numbers, alternatives):
`time-jump-move-player-2026-10-25.md` in the project files.*

---

## 1. Introduction

The Real Spawn Control window lists the ghosts within 250 m and 2 m/s of the player's vessel.
Each row's `Warp` skips the clock: a `Ready` row to the moment its ghost becomes a real vessel,
a `Leaves` row to just before its ghost burns away from the orbit.

Today the skip freezes the player's bubble in place while the clock jumps (design rule 13,
Section 14.5 of the flight recorder design). Ghosts are not frozen: they keep playing their
recordings. So:

- A `Leaves` Warp in orbit always loses its ghost. After a 10 minute skip in low Kerbin orbit
  the ghost is about 1,100 km away when it burns.
- A `Ready` ghost has to be spawned on a time-shifted copy of its own orbit to appear beside
  the player. The spawned vessel is then off its own recording, which breaks any later flight
  that meets it where it was recorded, and the shift is lost on a quickload while the spawn is
  blocked.
- The player's vessel ends up a whole skip's worth of orbit out of place relative to every real
  vessel in the universe: a free orbital phasing manoeuvre.

### 1.1 What the player sees after this change

The player presses `Warp`. The clock jumps, and the player's vessel is beside the ghost, at the
same distance, in the same direction and with the same relative speed and attitude as at the
click. Planet rotation and the sun angle show that time passed. The ghost is where its
recording says it is at the new time, and so is the player's vessel, within the small drift the
jump cancels. A `Ready` ghost then becomes real on its recorded spot; a `Leaves` ghost lights
its engine beside the player 15 seconds later and flies away.

When the move cannot be done safely the `Warp` is greyed and its hover says why.

### 1.2 Worked example

The player sits 100 m behind ghost B in an 80 km Kerbin orbit, closing at 0.05 m/s, docking
ports aligned. B's recording ends in 20 minutes. Warp: the clock jumps 20 minutes, the player's
vessel is 100 m behind B, still closing at 0.05 m/s, ports still aligned, and both are where the
timeline says (B on its recorded end state). Without the jump, plain physics would have drifted
the player to about 330 m from B; the 230 m of drift is the only thing the jump gives away, and
it is the reason the feature exists.

---

## 2. Design Rules

These replace rule 13 of the flight recorder design (Section 2.3) and Section 14.5's operation.

1. **The clock jumps, and the group keeps its place beside the chosen ghost.** In orbit, the
   player's vessel and the loaded vessels moving with it are placed so that their position,
   velocity and attitude relative to the chosen ghost's RECORDED state are the same at the
   target time as at the click. Everything else, every ghost included, follows the timeline.
2. **Ghosts never change.** The chosen ghost's recording is read to decide where the player's
   own vessels go; no ghost and no recording is modified (owner ruling Q4: a player action may
   read a ghost's recording to place the player's own vessels).
3. **Refuse, never approximate.** A jump that cannot be done by this rule is refused with a
   greyed `Warp` and a reason in its existing hover (ruling Q1). There is no fallback to the
   freeze in orbit.
4. **The chosen ghost spawns on its record.** A `Ready` ghost becomes real exactly on its
   recorded end state, never on a shifted copy. The player's group makes room for it, not the
   other way round (ruling Q7b).
5. **Surface: nothing moves.** A landed or splashed player vessel keeps today's behaviour: the
   body carries both vessels, and the ghost spawns at its recorded end.

---

## 3. Terminology

- **A**: the player's active vessel.
- **Reference ghost**: the ghost of the row whose `Warp` was pressed (for `Warp to Next Spawn`,
  the row it picks).
- **T0 / T1**: the click UT / the jump target UT. `Ready`: the recording's end. `Leaves`: the
  departure minus the 15 s jump lead (unchanged).
- **Group**: the real vessels that move with A (Section 4.2).
- **Local frame**: the reference ghost's local directions (up, along-track, sideways), turning
  with it along its orbit. "100 m behind, ports aligned" is a fixed offset in this frame.

---

## 4. Behavior

### 4.1 The operation

1. Stop time warp and latch replay scope (as today).
2. Read the reference ghost's recorded state at T0 and at T1 (ruling Q7: the recording, not the
   drawn ghost; the gap to the drawn ghost is logged). `Ready` at T1 uses the exact resolver the
   spawner uses for that recording's end, so "spawns beside you" holds by construction.
3. For every group vessel, record its offset, relative velocity and attitude in the reference's
   local frame at T0.
4. Check the gates (Section 4.3). They were already evaluated for the greyed state; they are
   re-checked here and a failure aborts the jump with nothing changed (logged).
5. Clear the reference's spawn spot (Section 4.4).
6. Put loaded vessels on rails and hold the unpack, set the clock to T1, apply the converter
   policy (as today).
7. On the next physics frame, once body rotation and the frame have caught up with T1:
   resolve the reference's T1 state, then place each group vessel at it plus its offset,
   turned with the local frame; same for velocity. Attitude is carried relative to the
   reference's orbital frame (the recorder's `orbitalFrameRotation` convention), never as a
   rotation of a world quaternion. Every other loaded orbital vessel is left on its own orbit,
   which carries it to its natural T1 position.
8. Stock unpacks the vessels after the hold; the ledger recalculates at T1 (as today).
9. A `Ready` reference then spawns on its recorded end state through the ordinary spawn path;
   other crossed ghosts spawn as in Section 4.5.

### 4.2 Which vessels move (ruling Q2)

Only loaded orbital vessels that are close to A and on nearly the same orbit: within the
window's gates, 250 m and 2 m/s of A, measured from orbit states (not from transform
differences, which pick up the planet's rotation). Vessels docked to A are part of A. A
kerbal on EVA 50 m away moves; a station 2 km away does not. Loaded vessels outside the group
follow their own orbits through the jump; landed and splashed vessels stay with the body.

### 4.3 Gates (greyed Warp, ruling Q1)

Evaluated on the window's 1.5 s scan for each row, and again at the click.

| Gate | Fails when | Hover text (existing hover, appended) |
|---|---|---|
| Space | A, a group vessel, or the reference at T0 or T1 is inside an atmosphere | `Can't warp here: inside the atmosphere.` |
| Same body | the reference's recording changes body between T0 and T1 | `Can't warp: {name} changes orbit body first.` |
| Coasting (ruling Q6) | the reference's own burns between T0 and T1 add up to more than 2 m/s, measured as the change of its orbit (size, shape, plane), not as a position difference at T1, which along-track drift would inflate | `Can't warp: {name} burns before it spawns here.` |
| Destination | a group vessel's new spot overlaps a real vessel outside the group at T1 (B itself is handled by 4.4) | `Can't warp: the spot beside {name} is taken then.` |

A `Leaves` reference always passes the coasting gate: T1 lies on the one orbit it coasts until
its departure. `Warp to Next Spawn` skips refused rows. Wording goes through
`TooltipEchoBudgetTests`.

### 4.4 Making room for the reference's spawn (ruling Q7b)

Before the jump, the existing spawn collision guard (`SpawnCollisionDetector`: the reference's
bounding box from its end snapshot via `ComputeVesselBounds`, `BoundsOverlap` with the spawn
check's own padding) is run against each group vessel's bounding box at its placed T1 spot. No
new collision test is written. If any overlap, the whole group is slid back along the reference's
orbit, away from B (along-track; for an offset that is purely radial or sideways, behind B),
by the smallest distance that clears the overlap plus a few metres. The group's internal
layout and its velocities in the local frame are unchanged. The slide is logged with the
distance. The ordinary spawn collision check still runs at the spawn and stays the backstop;
after a slide it should find nothing.

### 4.5 Other ghosts (ruling Q3)

No other ghost is shifted, held or moved. A ghost that stays a ghost is drawn at its recorded
T1 state. A ghost whose spawn time the jump crosses spawns where its own recording puts it,
through the ordinary spawn path and its ordinary collision check (walkback, or the chain-tip
hold). Co-orbital ghosts that were near the reference stay near it, by the drift their
recordings actually had.

### 4.6 Recording during the jump

The jump reuses the recorder's existing teleport handling (`FlightRecorder.CheatTeleport.cs`,
the path a stock Alt+F12 Set Orbit takes; owner ruling 2026-10-25: "we have a system which
fills up jumps like this so recordings look natural, we should use it"). Packing A at T0 fires
the ordinary go-on-rails capture, which opens an orbit segment on A's own T0 orbit. After the
placement at T1 the jump calls the same bookkeeping as an on-rails same-body teleport
(`CheatTeleportAction.OnRailsRecapture`): the T0 segment closes at T1, so the skipped span is a
natural coast, and a fresh segment opens on the placed orbit. On a later replay the vessel
coasts on its own orbit through the skipped span and then steps to its placed spot at T1, a
step of at most the drift the jump cancelled. Today's TIME_JUMP event stays. The log line names
the cause (`cause=time-jump`), not a cheat. A group vessel recorded by the background recorder
gets the same re-capture (the background recorder has no teleport handling today; Step 1 adds
the one call).

### 4.7 What does not change

The jump target times, the 15 s `Leaves` lead, the row statuses and gates, surface jumps, the
ledger recalculation, converter handling, the spawn path and its collision check, and the
automation-only `TimeJump` seam verb (keeps today's freeze; it has no reference ghost).

---

## 5. Scenario Walkthroughs

Each VERIFY line is what a lane or test must show.

**(a) Ready, LKO, 20 min.** A 100 m behind B, closing 0.05 m/s. Jump; A placed 100 m behind
B's recorded end; B spawns on its recorded end state.
VERIFY: B's saved orbit equals its recorded terminal orbit at the spawn UT; separation A-B
after spawn within 1 m of the T0 separation; relative velocity within 0.01 m/s.

**(b) Leaves, Mun departure 10 min out.** A 80 m radially outward of C. Jump to departure minus
15 s; A placed 80 m outward of C's recorded state; C's burn plays beside A.
VERIFY: ghost-to-A distance after the jump within 1 m of the T0 distance (today about
1,100 km, the negative control); no spawn.

**(c) Group with a station.** Station D 200 m from A, EVA kerbal 30 m from A, debris 1.5 km
away. D and the kerbal move with A; the debris follows its own orbit.
VERIFY: D-A and kerbal-A offsets unchanged to 1 m; the debris at its natural T1 position.

**(d) Spawn spot overlap.** A parked 6 m from B's recorded state (inside B's box after
placement). The group is slid back along the orbit until clear.
VERIFY: one slide log line with its distance; B spawns on its recorded state with no walkback
and no hold.

**(e) Ready ghost burns before its end.** B's recording does 0.8 m/s of RCS before its end:
Warp allowed, A inherits at most that. B's recording does a 30 m/s orbit raise: Warp greyed
"burns before it spawns here".
VERIFY: the pure gate passes the first and refuses the second (unit tests).

**(f) Two Ready ghosts.** B1 ends in 5 min, B2 in 20 min; Warp on B2. A placed by B2; B1
spawns on its own recorded state, near B2 by its recorded drift.
VERIFY: B1's saved orbit equals its recorded terminal orbit at its spawn UT.

**(g) Replay after docking.** Warp to B, dock, commit, rewind before T0, replay. A's ghost docks
with B's ghost where B's recording ends.
VERIFY: the replayed dock happens within metres of B's recorded end (today about 120 km off on
the CI-9 host).

**(h) Atmosphere.** Formation flight at 40 km with an atmospheric ghost: Warp greyed "inside
the atmosphere". Landed rover beside a landed base: Warp works as today.

**(i) Quickload after the jump.** Nothing transient exists: A's placed state and B's vessel are
ordinary saved vessels. VERIFY: a quickload with B's spawn blocked by an unrelated vessel
retries B on its recorded spot, not 1,000 km away.

---

## 6. Invariants

| Rule | Check |
|---|---|
| Ghosts never change game state | No ghost or recording is touched. The reference's recording is read to place the player's own vessels only (ruling Q4). |
| Ledger never reduced | No ledger rows are written; the recalculation at T1 is today's. No change of body and no atmosphere (gates), so no flyby, SOI or altitude milestone can be earned by the move. |
| No paradox | Forward only. Every spawned vessel is on its own record, so later flights that meet it agree on replay. One residual: a group member that a committed future flight meets (a real station the player parked beside) is moved by the cancelled drift, so it sits up to that drift away from where the future flight's ghost meets it. Today's freeze displaces it by the whole skip's orbit instead. |
| Append-only | No committed data changes. |
| Exploit bound | The move cancels drift between orbits inside the 250 m / 2 m/s gate, plus at most 2 m/s of the reference's own burns. The free phasing of today's freeze is gone. |

---

## 7. Implementation Phasing

**Step 1, Leaves.** Pure core (local-frame map, group builder, gates, slide), the
pack / clock / place / unpack shell in `TimeJumpManager`, the greyed reasons, the recording
seam, used only by `WarpToDeparture`. Lanes: a new `Leaves` lane (a host whose orbital ghost
departs 5 min after load; the ghost must be within the T0 distance after the jump, and the
host's nearby probe, a group member, keeps its offset: scenario c), with a recording facet for
4.6. Ready keeps today's shift until Step 2.

**Step 2, Ready.** `WarpToRecordingEnd` and `Warp to Next Spawn` move to the same path for an
orbital player vessel (a landed one keeps today's path, rule 5), with the reference at T1 from
the spawn resolver and the slide of 4.4. CI-9 re-armed (teleport lines in
place of the shift lines, scenario a's orbit facet), VN-4 and VN-10 re-flown, a replay lane for
(g). Only after those fly green and the results archive shows no seam lane relying on the
shift: delete `TimeJumpTerminalOrbitShift` and its spawn, collision, walkback and held-ghost
branches, and close REALSPAWN-JUMP-SHIFT-RESIDUE.

Unit tests (both steps): the local-frame map (circular and slightly eccentric references,
jumps from 10 s to 6 h, relative state kept to 1e-6 m), the coasting gate (coast passes, 0.8
m/s passes, 5 m/s and a phasing ghost refuse), every gate, the group builder, the slide.

---

## 8. Open Points for the Plan

- How KSP keeps attitude for a packed vessel across a placement and the unpack hold (decided
  by the first flight; the attitude is encoded against the reference's velocity and up
  directions, so a world-frame rotation cancels).
- Build order, file map and the review findings it absorbed:
  `docs/dev/plans/realspawn-time-jump-move-implementation.md`.
- Robotics and docking-port drift on the one extra rails cycle (KSPCommunityFixes covers the
  stock bugs; noted, not designed around).
