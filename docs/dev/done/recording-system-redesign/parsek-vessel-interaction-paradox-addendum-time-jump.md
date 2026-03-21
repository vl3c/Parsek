# Parsek Vessel Interaction Paradox — Addendum: Relative-State Time Jump

## 1. Purpose

This addendum describes the relative-state time jump mechanism — a targeted time-skip operation that lets the player advance UT past ghost chain tips while preserving the spatial configuration of all vessels in the loaded physics bubble. This eliminates the rendezvous-destroying drift that normal time warp causes when vessels are on slightly different orbits.

### Related documents

- `parsek-vessel-interaction-paradox.md` — the paradox resolution design, including ghost chains, spawn rules, and all edge cases.
- `parsek-recording-system-design.md` — vessel recording system architecture.
- `parsek-game-actions-system-design.md` — standalone resource ledger. Recalculation is triggered by the time jump.

---

## 2. The Problem

The player rewinds to T0. Ghost-S is in the physics bubble, chain tip at T1. The player maneuvers vessel A into a rendezvous approach — 80m from ghost-S, velocities matched, docking alignment set up.

To interact with S, the player must advance UT past T1 so that S spawns as real. Normal time warp propagates A and ghost-S on their independent Keplerian orbits. Since they are 80m apart, their orbital elements differ slightly. Over the warp interval (T0 to T1), this differential drift separates them — potentially by hundreds of meters or kilometers, depending on the interval length. The carefully set up rendezvous is destroyed.

The player must then re-do the approach maneuver from scratch after S spawns. This is tedious and defeats the purpose of the time-rewind workflow.

---

## 3. The Solution: Relative-State Time Jump

A time jump is a discrete UT skip (not a warp) that advances the game clock while keeping every vessel in the physics bubble at its exact current position. No vessel moves. No intermediate physics simulation occurs. The orbital epochs are adjusted so that Keplerian propagation remains consistent with the new UT at each vessel's unchanged position.

### 3.1 The Operation

**Step 1 — Snapshot the bubble.** For every object in the loaded physics bubble (ghosts, real vessels, debris), capture its state relative to A (the player's active vessel):

```
BubbleSnapshot
  playerVessel:       A (player-controlled)
  jumpDelta:          targetUT - T0
  objects: [
    {
      id:               object identifier
      position:         current absolute position (body-relative)
      velocity:         current absolute velocity
      attitude:         rotation quaternion
      orbitalElements:  current elements (to be epoch-shifted)
      isGhost:          bool
      chainTip:         UT of chain tip (if ghost)
    }
  ]
```

After the UT change, each object's orbital elements are epoch-shifted by `jumpDelta` so that Keplerian propagation at the new UT produces the same position and velocity. No object moves.

**Step 2 — Jump UT.** Set the game clock to the target UT. No physics frames are simulated between T0 and the target UT.

**Step 3 — Adjust orbital epochs.** For every vessel in the bubble (including A): keep its position and velocity vectors unchanged, but recompute its orbital elements to be consistent with the new UT at that position. In practice, this means shifting each orbit's mean anomaly at epoch by the jump delta `(targetUT - T0)`. The orbit shapes (SMA, eccentricity, inclination, LAN, argument of periapsis) remain identical. Only the phase reference changes so that Keplerian propagation produces the correct position at the new UT.

No vessel moves. No propagation occurs. The bubble is frozen in place and the clock jumps underneath it.

**Step 4 — Process spawn queue.** For every ghost whose chain tip was crossed during the jump:

- Destroy the ghost
- Spawn the real vessel at the ghost's current position (which has not moved — it is exactly where it was at T0)
- Apply the bounding box overlap check (Section 8.2 of the paradox document). If overlap detected, the ghost continues at that position with orbital propagation until clear.

**Step 5 — Process game actions.** Trigger the game actions system recalculation for the new UT — science, funds, reputation, kerbals, facilities, contracts, strategies. This is the same recalculate+patch operation that fires on warp exit.

**Step 6 — Resume physics.** The player is at the target UT, at the same position and velocity relative to the now-real vessel, with the same approach geometry. They can dock immediately.

### 3.2 What changes visually

The time jump advances UT, which means:

- **Planet rotation:** Kerbin (or whichever body) has rotated to its correct angle for the new UT. The surface below the player has shifted. Ground tracks are different.
- **Sun angle:** Kerbol's position relative to the current body has changed. The day/night terminator has moved. The player may jump from daylight into shadow or vice versa.
- **Star field:** Rotated to match the new UT.
- **Other planets:** All celestial bodies are at their correct positions for the new UT.

These visual changes are correct — time genuinely passed. They serve as a natural visual cue that the jump occurred.

### 3.3 What does NOT change

- **Vessel position vectors:** Every vessel is at the exact same position it was before the jump. Nothing moves.
- **Vessel velocity vectors:** Identical before and after.
- **Relative positions within the bubble:** Identical by construction — nothing moved.
- **Relative velocities within the bubble:** Identical by construction.
- **Vessel attitudes:** Preserved.
- **Approach geometry:** Docking alignment, closing rate, angle of approach — all preserved.
- **Orbit shapes:** SMA, eccentricity, inclination, LAN, argument of periapsis — all unchanged. Only the orbital epoch (mean anomaly at epoch) is shifted so that Keplerian propagation produces the correct position at the new UT.

### 3.4 Surface cases

If A and ghost-S are on the surface (e.g. rover approaching a ghost base on the Mun), the time jump is simpler:

- Both are surface-fixed (lat/lon coordinates on the body)
- The body rotates with UT, carrying both vessels with it
- Relative positions are preserved automatically (surface-fixed coordinates don't change)
- The only visual change is the sun angle — shadows shift, lighting changes

No orbital propagation is needed. The surface bubble is inherently stable across time jumps.

---

## 4. Selective Spawning UI

The player does not need to jump to the furthest chain tip in the bubble. They can selectively choose which ghost(s) to spawn, jumping only as far forward as needed.

### 4.1 UI display

When the player is within the physics bubble of one or more ghosts, the UI displays a spawn control panel:

```
Ghosts in physics bubble:

  [Sync & Spawn] Station Alpha
    Becomes real at UT=1600 (Recording R1)
    
  [Sync & Spawn] Fuel Depot + Cargo Pod
    Becomes real as combined vessel at UT=3000
    (Chain: R2 → R3)
    
  [Info] Tanker Ghost
    Loop iteration — visual only, no spawn
```

**Rules for what appears in the panel:**

- Only chain tips where a real vessel actually spawns are offered as spawn options — not intermediate chain links (see Section 8.5 of the paradox document)
- Linked chains are grouped and shown as a single spawn option with the combined vessel name
- Loop iterations beyond the first are shown as informational only — no spawn button
- Each option shows: vessel name, spawn UT, and which recording(s) created the chain

### 4.2 Spawn selection behavior

When the player selects a spawn option:

1. The target UT is the selected chain tip's UT.
2. Any independent chain tips chronologically before the target are also spawned — a ghost cannot remain ghost past its chain tip. The UI warns: "Also spawns: [vessel name] at UT=[earlier tip]."
3. Ghosts with chain tips after the target UT remain ghost at their current positions.
4. The relative-state time jump executes as described in Section 3.

### 4.3 Chained jumps

The player can perform multiple jumps in sequence:

| Jump | Action | Result |
|---|---|---|
| 1st | "Spawn Station Alpha" → jump to T1=1600 | Station Alpha becomes real. Fuel Depot stays ghost. Player docks to Alpha. |
| 2nd | "Spawn Fuel Depot" → jump to T3=3000 | Fuel Depot becomes real. Player undocks from Alpha, docks to Depot. |

Each jump performs the same epoch-shift operation on whatever is currently in the bubble (including any real vessels spawned by previous jumps). The bubble freezes again, the clock jumps, epochs adjust.

### 4.4 Chronological constraint

The player cannot jump backward with this mechanism — it only moves UT forward. To go backward, the player uses Parsek's standard rewind (load quicksave).

The player also cannot jump to a UT before the earliest unresolved chain tip in the bubble if that would require a ghost to exist past its tip. In practice this is not a constraint — the player always wants to jump forward to spawn things.

---

## 5. Scenario Walkthroughs

### 5.1 Simple two-vessel rendezvous

| UT | State | Action |
|---|---|---|
| T0=500 | A: real. Ghost-S: 80m away. | Player sets up docking approach. |
| — | — | Player selects "Spawn Station Alpha (T1=1600)" |
| T1=1600 | A: real, same position. S: real, still 80m away. | UT jumped. Planet rotated. Sun angle shifted. Nothing in the bubble moved. |
| T1+ | A docks to real S. | Recording R2 starts. |

### 5.2 Three vessels, player picks the middle one

Physics bubble at T0=500:
- A (real)
- Ghost-S1 (independent, tip T1=1600)
- Ghost-S2 (independent, tip T2=2000)
- Ghost-S3 (independent, tip T3=5000)

Player selects "Spawn S2 (T2=2000)." UI warns: "Also spawns: S1 (T1=1600)."

| Object | Before jump (T0) | After jump (T2=2000) |
|---|---|---|
| A | real | real, same position, epoch-shifted orbit |
| S1 | ghost | real (tip T1 crossed) at same position |
| S2 | ghost | real (tip T2 = target) at same position |
| S3 | ghost (tip T3=5000) | ghost, same position |

Player docks to S2. Later, if needed, selects "Spawn S3" for another jump.

### 5.3 Linked chain — player must wait for full chain

R1 docks X to S at T1. R2 docks Y to S+X at T2. Chain: bare-S → S+X → S+X+Y.

UI shows:

```
  [Sync & Spawn] Station Alpha + X + Y
    Becomes real as combined vessel at UT=2000
    (Chain: R1 → R2)
```

T1 is NOT offered as a spawn option — intermediate spawn suppression prevents it. Player must jump to T2 to get a real vessel.

### 5.4 Surface base approach

Rover A is 50m from ghost-base S on the Mun. Chain tip at T1.

Player selects "Spawn Base S." Jump to T1. Both are surface-fixed — Mun rotates, sun angle changes, but relative positions identical. Base spawns as real. Rover drives up and docks.

### 5.5 Jump, then rewind

Player jumps to T1, S1 spawns real. Player docks A to S1, commits R2. Player then rewinds to T0. Everything resets: S1 is ghost again (R1 claims it), R2 is committed and plays as ghost. The time jump left no persistent state — it was equivalent to a fancy warp. Standard rewind rules apply.

---

## 6. Interaction with Existing Systems

### 6.1 Spawn queue

The spawn queue from the paradox document (Section 8.9) operates normally during the time jump. The jump is treated as an instantaneous crossing of all chain tips between T0 and the target UT. The spawn queue processes them in chronological order.

### 6.2 Bounding box collision check

The spawn-point collision check (Section 8.2) applies to each spawn during the jump. If a spawning vessel's bounding box overlaps with A or any other vessel at its current position, the spawn is blocked and the ghost continues with orbital propagation until clear. The player receives the standard proximity warning.

### 6.3 Game actions recalculation

The time jump triggers the same recalculate+patch cycle as a warp exit. All game actions between T0 and the target UT are processed: science awards, fund earnings/spendings, reputation changes, kerbal state transitions, facility visual state. This happens once at the end of the jump, not incrementally.

### 6.4 Recording system

The time jump does not modify any recording. Recordings are immutable. The jump only affects live game state: vessel positions, orbital elements, and the spawn queue. If the player is actively recording during a time jump, the jump creates a discontinuity in the recording — the trajectory has a gap from T0 to the target UT. This should be stored as a TIME_JUMP event in the recording, with the pre-jump and post-jump state vectors, so that playback can handle the discontinuity (either by interpolating or by showing a visual cut).

### 6.5 Looped recording ghosts

Looped ghosts that are in the bubble during the jump stay at their current positions. Their loop phase advances to match the new UT — their orbital epochs are shifted like everything else. They continue their loop cycle from the correct phase at the new time. No spawn occurs (loops beyond the first are visual only).

### 6.6 Ghosts are invisible to the recording system

Ghosts are raw Unity GameObjects, not KSP Vessel objects. The background recorder operates exclusively on KSP Vessels. Therefore ghosts are invisible to the recording system by construction — they are never captured in background recordings, never attributed events by the merge algorithm, and never appear in any recording's data. If a ghost flies through the physics bubble during an active recording session, the recorder does not see it. This is not a special case or filter — it falls out naturally from the implementation.

### 6.7 Spawn blocked by immovable vessel — trajectory walkback

The bounding box spawn check (Section 6.2) blocks a spawn when it overlaps with a real vessel, expecting the player to move their vessel to clear the overlap. This creates a deadlock when the blocking vessel is immovable infrastructure — a surface base, a ground-anchored station, or any vessel the player cannot or should not move.

**The scenario:** R2 recorded a rover parking 2m from bare base C. Later, R1 (committed earlier in the timeline) added module X to C, extending C's footprint. On rewind and replay, the rover ghost's final position is now inside module X of real C+X. The spawn blocks. C+X can't move. The ghost can't move. Permanent deadlock.

**Resolution: walkback along recorded trajectory.**

When a spawn is blocked and the blocking vessel has not moved after a timeout (e.g. 5 seconds of persistent overlap):

1. Walk backward along the spawning ghost's recorded trajectory — frame by frame, checking bounding box overlap at each position.
2. Find the latest point on the trajectory where the bounding box does not overlap with any real vessel.
3. Spawn the vessel at that position.
4. Recompute orbital elements (or surface coordinates) for the new spawn position.

The result: the rover materializes a few meters back from where it originally parked — as if the base grew and the parking spot is now occupied. Physically reasonable and intuitive.

**Fallback — entire trajectory overlaps:** If the ghost's entire recorded trajectory overlaps with the real vessel (e.g. the rover drove straight into where module X now sits), show a manual placement UI: "Rover spawn blocked by Station C+X. Choose spawn location within Xm radius." The player picks a valid spot. This fallback should be rare — it requires the blocking vessel to have grown enough to cover the entire approach path.

**This refines Section 8.2 of the paradox document.** The original spawn-point collision system assumed the player could always move to clear the overlap. The trajectory walkback handles the case where the blocking vessel is immovable. The original "ghost extension + wait for clear" behavior remains the default for orbital cases where the player can maneuver away.

---

## 7. Ghost Presence in the Game World

Ghosts are not just visual playback objects — they represent vessels that theoretically exist in the world at that time, pending their chain tip resolution. This section covers how ghosts participate in game systems beyond visual rendering.

### 7.1 Loaded vs unloaded ghost spawning

Every spawn scenario in the paradox document assumes the ghost is in the loaded physics bubble (~2.3km). But chain tips can fire for vessels anywhere in the solar system while the player is elsewhere.

**Unloaded spawn (vessel outside physics bubble):** When a chain tip fires for a vessel the player is not near, Parsek creates a ProtoVessel entry in the save data at the correct orbital elements (or surface coordinates). No Unity objects are created, no bounding box check is needed, no physics settling occurs. The vessel appears immediately in the tracking station and map view, and propagates on rails like any normal unloaded vessel.

**Loaded spawn (vessel inside physics bubble):** The full spawn sequence applies — ghost replacement, bounding box overlap check, ghost extension if blocked, terrain raycast for surface cases, physics settling.

**This corrects Section 8.9 of the paradox document**, which said spawns outside the bubble are "queued and processed when the player enters their physics bubble." That was wrong. The ProtoVessel is created immediately at the chain tip UT so the vessel exists in the save from that point forward. Only the visual conversion from ghost GameObject to real Vessel requires the player's presence.

### 7.2 Ghost representation in tracking station and map view

Ghosted vessels must appear in the tracking station and map view so the player can plan missions around ghost windows.

**Tracking station:** Ghosted vessels appear with a distinct icon (differentiated from real vessels and debris). The info panel shows: vessel name, ghost status, chain tip UT ("becomes real at UT=1600"), which recording claims it.

**Map view:** Ghosted vessels display an orbit line in a distinct style (different color or dashed line). The vessel marker shows ghost status on hover/click. The player can set a ghosted vessel as a navigation target for transfer planning — approach vectors, closest approach markers, and relative velocity all work normally.

**This extends Section 8.14 of the paradox document** (ghost visual identity), which only covered in-flight 3D rendering. Map view and tracking station are equally important for gameplay — the player needs to see where ghosts are to plan intercepts and time their rendezvous or time jumps.

### 7.3 Ghost passive effects — lightweight + CommNet

Ghosts are raw Unity GameObjects, not full KSP Vessel objects. This boundary is fundamental to paradox prevention. However, some passive effects of a vessel affect the wider game world. These must be selectively implemented without promoting the ghost to a full KSP Vessel.

**Passive effects that affect gameplay and must be supported:**

1. **CommNet relay.** Antennas on the ghost relay signal, extending communication network coverage. Other real vessels' probe control and science transmission depend on relay paths. A relay constellation placed by a committed recording must provide coverage during the ghost window.

2. **CommNet occlusion.** The ghost's physical position matters for line-of-sight checks. A relay behind the Mun cannot relay signal through the Mun. The ghost must be at the correct position for occlusion math to work.

3. **Vessel as navigation target.** Real vessels can set a ghost as a rendezvous target for approach planning (distance, closest approach, relative velocity).

4. **Map view orbit display.** The ghost's orbit is visible for transfer planning.

**Passive effects that are vessel-internal and do NOT need support on ghosts:**

- Solar panel power generation (internal to vessel)
- Science lab processing (handled by timeline — see Section 7.4)
- Resource drilling / ISRU conversion (internal)
- SAS / reaction wheel authority (internal)
- Life support consumption (modded, internal)

**Cosmetic effects (nice to have, not gameplay-critical):**

- Lights illuminating terrain
- Engine plumes during ghost playback
- Flag presence

**Implementation — lightweight + selective registration:**

The ghost stays a raw Unity GameObject. No promotion to KSP Vessel. Passive effects are implemented by selectively registering with game systems:

**For loaded ghosts (in physics bubble):** Unity GameObject for visual rendering (as today), plus a CommNet node registered at the ghost's position with antenna specs from the recording data (part names, power ratings, combinability exponent).

**For unloaded ghosts (outside physics bubble):** No Unity object. A CommNet node at the orbital-propagated position, plus a tracking station entry and map view marker. This is the minimum representation for a ghost that is far from the player but still participates in the communication network.

**Recording data requirement:** The recording must store antenna part names, power ratings, and combinability exponents for each vessel, so the ghost can register correct CommNet specs without being a real vessel.

**Why not use full KSP Vessels with interactions blocked:** The full-vessel approach trades a clean guaranteed boundary for convenience. Every passive effect gained for free comes with the risk of an active effect leaking through — KSP modifying orbit from atmospheric drag, mods interacting unexpectedly, cheat menu access, save file contamination. The recording design doc chose raw GameObjects specifically to guarantee no game state modification. CommNet and map view registration are well-defined APIs that can be hooked without a full Vessel.

### 7.4 Passive resource generation during ghost windows

Passive background processes (science lab processing, ore drilling, ISRU conversion) generate resources over time on real vessels. When a vessel is ghosted, these processes are not running — the ghost is a Unity GameObject, not a KSP Vessel with active modules.

**This is not a gap.** All passive earnings were already captured during the original playthrough and exist as committed game actions on the timeline. The game actions system's recalculation walk processes them regardless of the vessel's current ghost/real status. Science earned by a lab during the recording's time span is an immutable earning action on the timeline. Funds earned from contract milestones are on the timeline. Everything is on the timeline.

Ghost status is a physical-interaction constraint, not a resource-accounting constraint. The two systems are deliberately decoupled:

- **Ghost chain system** controls physical interactions — what can the player touch, dock with, modify.
- **Game actions timeline** controls resource effects — what science was earned, what funds were spent, what reputation changed.

The timeline is complete from the original playthrough. Rewinding and ghosting a vessel does not remove, modify, or suppress any actions on the timeline. The recalculation walk reprocesses them every time, producing the correct derived values (available science, available funds, etc.) regardless of how many times the player rewinds.

After the chain tip fires and the real vessel spawns, all passive processes resume normally on the real vessel from that point forward. Any new passive earnings are captured by the recording system as new game actions when the player eventually commits.

---

## 8. Design Principles

1. **Nothing moves, the clock jumps.** The entire physics bubble stays frozen in place. Only UT changes. Orbital epochs are adjusted to maintain Keplerian consistency. This eliminates drift by construction — there is no propagation to accumulate error.

2. **Selective spawning, chronological constraint.** The player picks which chain tip to jump to. Earlier independent tips are auto-spawned (ghosts cannot exist past their tip). Later tips stay ghost.

3. **Discrete jump, not warp.** No intermediate physics frames. No drift. No propagation. The clock teleports; the vessels don't.

4. **Visual honesty.** Planet rotation, sun angle, and star field change to reflect the new UT. The player sees that time passed. Only the local bubble geometry is preserved.

5. **No persistent side effects.** The time jump is equivalent to a warp — it advances UT and processes the timeline. Rewind undoes it normally. Recordings are not modified.

6. **Game actions processed atomically.** All resource recalculation happens once at the target UT, same as warp exit. No incremental processing during the jump.

7. **Ghosts participate selectively.** Ghosts are lightweight (raw GameObjects), not full KSP Vessels. They participate in CommNet and map view through selective API registration. They do not participate in any active game system. This preserves the paradox-proof boundary while allowing the game world to feel correct.

8. **Physical interactions and resource effects are decoupled.** Ghost chains control what the player can touch. The game actions timeline controls what resources exist. Neither system depends on the other. Ghosting a vessel does not affect its resource history.

9. **Paradox prevention is enforced by the physics engine, not by validation logic.** The player cannot create a conflicting recording because conflicting interactions are physically impossible — ghost vessels have no colliders, no docking ports, no physics presence. This is not a rule that can be bypassed by mods, exploits, or the debug menu. It is a property of the simulation itself.

---

*Document version: 1.4*
*Addendum to parsek-vessel-interaction-paradox.md.*
*Describes the relative-state time jump mechanism for preserving rendezvous geometry across ghost chain tip spawns.*
*v1.1: Corrected to frozen-bubble epoch-shift model — no vessel moves, only UT and orbital epochs change.*
*v1.2: Added trajectory walkback for spawn deadlocks with immovable vessels (Section 6.7).*
*v1.3: Added explicit rule that ghosts are invisible to the recording system (Section 6.6).*
*v1.4: Added loaded vs unloaded spawning (Section 7.1), tracking station and map view representation (Section 7.2), ghost passive effects with lightweight+CommNet approach (Section 7.3), and passive resource generation clarification (Section 7.4).*
