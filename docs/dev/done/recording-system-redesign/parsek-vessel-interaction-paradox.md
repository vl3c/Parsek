# Parsek Vessel Interaction Paradox — Design Resolution

## 1. Background

Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. Ghosts are raw Unity GameObjects — purely visual, no physics collisions, no game state modification.

GitHub: https://www.github.com/vl3c/Parsek

This document describes the paradox that arises when committed recordings interact with pre-existing vessels, and the resolution design.

### Related documents

- `parsek-recording-system-design.md` — vessel recording system architecture. Section 11 ("Interaction with Pre-Existing Persistent Vessels") needs revision based on the resolution described here.
- `parsek-game-actions-system-design.md` (v0.2) — standalone resource ledger. Not directly affected by this paradox, but depends on a consistent timeline.

---

## 2. The Paradox

When a committed recording includes a physical interaction with a pre-existing vessel (e.g. docking to a station), a dependency is created: that vessel must be in a specific state when the ghost interaction occurs during playback. If the player rewinds and modifies the vessel before the recorded interaction, the timeline breaks.

---

## 3. The Scenario (Three Timeline Runs)

### Setup

- Station S exists in 100km Kerbin orbit (launched and committed in earlier recording R0)
- S has one docking port and one probe core

### Run 1 — Normal play

The player launches vessel A and docks it to S, then commits this as recording R1.

| UT | Station S | Vessel A | What happens |
|---|---|---|---|
| 0 | real, bare | — | S orbiting from R0 |
| 1000 | real, bare | real | A launches, R1 recording starts |
| 1600 | real, combined S+A | part of S+A | A docks to S, R1 recording ends |

R1 is committed. It covers UT=1000→1600 and contains a MERGE event at UT=1600 targeting S.

### Run 2 — Rewind to UT=500

R1 is committed and claims S via the merge event. S becomes a ghost until R1 resolves.

| UT | Station S | Vessel A | What happens |
|---|---|---|---|
| 500 | ghost, bare | — | Quicksave loaded, S is ghost because R1 claims it |
| 1000 | ghost, bare | ghost | R1 playback starts, ghost-A appears |
| 1600 | real, combined S+A | part of S+A | R1 playback completes, real S+A spawns |

This works. S is untouchable as a ghost. Ghost-A approaches ghost-S. At UT=1600 the real combined vessel spawns.

### Run 3 — Rewind to UT=500 again (the paradox scenario)

R1 is still committed. The player launches a new vessel B at UT=800 and wants to dock B to S at UT=1200 — before R1 completes.

| UT | Station S | Vessel A | Vessel B | What happens |
|---|---|---|---|---|
| 500 | ghost, bare | — | — | Quicksave loaded, S still ghost |
| 800 | ghost, bare | — | real | B launches |
| 1000 | ghost, bare | ghost | real | R1 playback starts |
| 1200 | ghost, bare | ghost | real, wants to dock | **B cannot dock — S is a ghost** |
| 1600 | ghost, combined S+A | part of ghost S+A | real | R1 completes visually — ghost changes form from bare-S to S+A but remains ghost (this is a visual transition, not a spawn) |
| 1600 (next frame) | real, combined S+A | part of S+A | real | Spawn fires: real S+A materializes. B can now dock. |

B cannot interact with S before UT=1600. After UT=1600, S+A exists as real and B can dock to it. The player simply time-warps past the ghost window — zero gameplay cost in stock KSP (no time-dependent consumables). The UT=1600 moment involves two discrete steps: first the ghost visually transitions from bare-S to S+A form (still ghost), then on the next eligible frame the spawn fires and real S+A materializes.

---

## 4. The Ghost Chain Rule

**When a committed recording contains a physical interaction with a pre-existing vessel, that vessel becomes a ghost from the moment the rewind quicksave is loaded until the recording completes and the final-form vessel spawns as real.** The ghost window starts at whatever UT the rewind loads — not necessarily UT=0.

If multiple committed recordings interact with the same vessel lineage, the ghost chain extends:

| Recording | Event | Ghost chain state |
|---|---|---|
| R1 | A docks to S at UT=1600 | bare-S ghost → S+A ghost |
| R2 | B docks to S+A at UT=2000 | S+A ghost → S+A+B ghost |
| Spawn | — | real S+A+B spawns at UT=2000 |

The real vessel materializes only at the tip of the chain — after the last committed recording that touches the vessel lineage completes.

Parsek must walk the full chain of committed recordings that reference a vessel's lineage to determine the spawn point.

---

## 5. Ghosting Trigger Rule

**A committed recording forces ghosting on a pre-existing vessel if and only if the recording contains a recorded event that changes that vessel's physical state.**

### Physical state changes (TRIGGER ghosting)

**Structural:**
- Docking to vessel (MERGE)
- Undocking from vessel (SPLIT)
- Part destruction on vessel (collision, explosion, overheat)
- Crew transfer to/from vessel (kerbal = vessel; boarding = merge, EVA = split)
- Claw/AGU grab (structural MERGE)
- EVA construction: part added to or removed from vessel (PART_ADDED / PART_REMOVED SegmentEvents)

**Orbital / positional:**
- Engine burns (changes orbit)
- RCS translation (changes position/orbit)
- Staging

**Part state:**
- Deploying/retracting parts (solar panels, radiators, antennas, landing gear)
- Running/collecting science experiments
- Action group triggers that change physical part state
- Resource transfers (fuel, monoprop)

### Does NOT trigger ghosting

**Cosmetic:**
- Toggling lights
- Animations without physical effect

**Observation:**
- Switching focus to the vessel (Control From Here)
- Switching focus away from the vessel
- Camera movement, map view, info panels
- Recording the vessel's background trajectory
- SAS mode changes (captured in rotation tracking data, not as a discrete event)
- Minor orbital perturbation from physics settling (not a recorded event)

### The dividing line

The test: **would removing this event from the timeline leave the vessel in a different physical state than if it had been left alone?** If yes, the event changes physical state and triggers ghosting. If no (cosmetic or observation only), no ghosting.

---

## 6. Looped Recordings

Looped recordings that include interactions with pre-existing vessels follow a different rule:

- The **first run** of a looped recording is the real run — it follows the ghost chain logic described above. The vessel spawns as real at completion of the first run.
- **All subsequent loops** are purely visual. Ghosts appear and disappear on the loop cycle. They do not affect gameplay, do not spawn real vessels, and do not extend the ghost chain.

**Definition of "first run":** The first chronological playback of the recording after its commit point. If the recording was committed at UT=1000, the first playback spanning UT=1000→1600 is the real run. If the player rewinds to before UT=1000 and fast-forwards again, the playback starting at UT=1000 is still the first run — "first" is defined by the recording's timeline position, not by how many times the player has rewound. The real run always corresponds to the recording's original UT span. Every subsequent loop iteration (UT=1600→2200, UT=2200→2800, etc.) is visual only.

This prevents the problem of a looped docking recording permanently ghosting a station. The station becomes real after the first run completes; loops are scenery.

---

## 7. Undocking Scenarios

Undocking follows the same ghost logic as docking. If R1 involves undocking a module from S (a SPLIT event), S's physical state changes — it loses parts. Therefore:

- S is a ghost until R1 completes
- At completion, S spawns as real in its post-undocking form (fewer parts)
- The undocked module also spawns as a separate real vessel at its final recorded position

Both products of the split are independently subject to the ghost chain rules. If a later committed recording R2 docks something to the undocked module, that module's ghost chain extends and its spawn is suppressed until R2 completes — independent of S's own chain.

---

## 8. Edge Cases

### 8.1 Vessel destruction terminates the chain

If R1 crashes into S and destroys S's controller, the ghost chain terminates with no spawn. Ghost-S plays the crash visually, then disappears. There is no real vessel at the chain tip because there is nothing left to spawn.

If S breaks into multiple pieces during the crash: any surviving piece that has a controller (e.g. a secondary probe core) is a vessel under Parsek's identity model and spawns as a real vessel at its final recorded position. Pieces without a controller are debris and do not spawn. The ghost plays the breakup animation; controller-carrying pieces transition from ghost to real at the chain tip, debris pieces simply vanish.

### 8.2 Spawn-point collision

At a chain tip, a real vessel spawns at the ghost's position. If the player has parked another vessel at or near that position (possible because ghosts have no collision), the spawn could cause physics overlap — KSP's collision detection fires, parts clip, and both vessels may be destroyed.

**Resolution: blocked spawn with ghost extension.**

The spawn system uses two distance thresholds and bounding box collision detection:

**Warning radius (200m):** As UT approaches a chain tip, Parsek checks whether any real vessel is within 200m of the spawn point. If yes, a persistent on-screen warning appears showing the distance, the spawning vessel's name, and a countdown. The warning remains on screen as long as the conflict exists. This is a UI-only threshold — it does not block spawning.

**Bounding box overlap check (spawn blocker):** At the chain tip UT, Parsek computes the bounding box of the vessel about to spawn (from its recorded part layout) and checks for geometric overlap with bounding boxes of all real vessels in the loaded physics bubble, plus a small padding margin. If any overlap is detected, the spawn is blocked. This is a purely geometric test — there is no distance threshold beyond the bounding boxes themselves.

**Ghost extension via orbital propagation:** When spawn is blocked, the ghost continues past the recording's end time. The recording's final orbital elements (or surface coordinates) are used to propagate the ghost's position via Keplerian orbit math (or surface-stationary persistence). No recorded trajectory data is needed — the ghost coasts naturally on rails.

**Spawn at propagated position:** Each physics frame while blocked, Parsek rechecks bounding box overlap at the ghost's current propagated position. When overlap clears (the player moves their vessel away), the real vessel spawns at the ghost's current propagated position at the current UT — not at the original recorded endpoint. This is physically correct: the spawned vessel is exactly where a real vessel on that orbit would be at the current time.

**If the player never moves:** The ghost persists indefinitely. The warning stays on screen. During time warp both ghost and real vessel are non-physical so no conflict exists, but on warp exit the check runs again. The player must actively move their vessel to unblock the spawn. This is an intentional gameplay constraint — parking on top of a ghost has consequences.

**Surface case:** Same logic but simpler. Ghost stays at surface coordinates. No orbital propagation needed. Player drives their rover away from the base's footprint, spawn fires.

**Full sequence:**

| Step | What happens |
|---|---|
| UT approaching chain tip | Warning appears: "S+A spawning in Xs — vessel B is Ym from spawn point" |
| UT reaches chain tip | Bounding box overlap check. If overlap: spawn blocked, ghost continues on propagated orbit. |
| Each frame while blocked | Recheck bounding box overlap at ghost's current propagated position. Warning persists. |
| Overlap clears | Spawn real vessel at ghost's current propagated position. Ghost destroyed. Warning dismissed. |

### 8.3 Independent ghost chains

If R1 docks A to station S1, and R2 docks B to station S2, and both are committed, then on rewind both S1 and S2 are independently ghosted. If the player wants S1 and S2 to dock to each other, they must wait for both chains to resolve (both become real), then dock the real versions in a new recording R3. This falls out naturally from the rules — each ghost chain is independent.

The player should be informed on screen that real vessels only spawn at the end of their timeline.

### 8.4 Recording scope and vessel claiming

A recording is tied to its vessel controller. A recording does NOT claim a pre-existing vessel merely by switching focus to it. Each vessel has its own separate recording timeline. When the player switches control to a different vessel during a recording session, they are switching to that vessel's own recording chain — handled by the multi-vessel merge algorithm.

A recording can only claim a pre-existing vessel through physical interaction (Section 5). Observation, focus switching, and background trajectory capture do not create claims.

This means R1 cannot silently claim unrelated vessel C just because the player switched to C mid-session. If the player only observed C without physical interaction, C remains real after rewind.

### 8.5 Intermediate spawn suppression

When a ghost chain has multiple links — bare-S → S+A → S+A+B — Parsek must suppress the spawn at intermediate points and only spawn at the final tip.

At UT=1600, R1 completes and would normally spawn real S+A. But R2 claims S+A's controller (docking B at UT=2000). Parsek detects this: the controller that would spawn at UT=1600 is referenced by a later committed recording. Therefore the spawn is suppressed, S+A continues as a ghost, and only S+A+B spawns as real at UT=2000.

The rule: **before spawning a real vessel at a chain link, check whether any committed recording further down the chain claims that vessel's controller. If yes, suppress the spawn and continue the ghost chain.**

Parsek must walk the full chain at rewind time and pre-compute the spawn point for each vessel lineage.

### 8.6 Cross-perspective interaction attribution

A recording is of vessel B. During that recording, B collides with pre-existing station S and breaks off S's solar panel. R_B is committed. R_B's primary subject is B, but S's physical state was changed.

This does NOT require a special "claiming" mechanism. The timeline merge algorithm already walks background recordings of all vessels in the physics bubble, detects the part destruction event on S, and attributes it to S's own recording timeline. S's timeline now contains a state-changing event from a committed recording, which triggers ghosting through the standard rule (Section 5).

The recording system's existing multi-vessel merge logic handles this — it checks background recordings for events on all vessels, not just the focused vessel. No new mechanism is needed.

### 8.7 Single recording with vessel separation

R1 launches vessel A. During flight, A separates into A1 and A2. A1 docks to station S1. A2 docks to station S2. R1 is committed.

The DAG handles this naturally. R1's segment ends at the separation event and generates two new recording chains: one for A1, one for A2. Each chain links independently to its respective station through the standard ghost chain rules. S1 and S2 are ghosted independently.

This is graph traversal through the recording segment DAG — the chain walker follows edges through split/merge nodes. No special case logic is needed beyond what the DAG already provides. The complexity is in the number of chains to walk, not in the rules themselves.

### 8.8 Vessel recovery terminates the chain

R1 deorbits S and the player recovers it. On rewind, ghost-S plays the deorbit trajectory. At the chain tip, nothing spawns — S was recovered, not destroyed, but the result is the same: no vessel remains in space.

This follows the same pattern as destruction (8.1) but is intentional. The game actions system credits the recovery funds and any science carried on board through the normal earning-action pipeline. The vessel's timeline simply ends.

### 8.9 Spawn queue and time warp

When the player time-warps past a ghost chain tip, the real vessel needs to spawn. This uses the same warp queue pattern established in the game actions system: spawns are deferred to warp exit, not executed mid-warp.

KSP already makes vessels non-physical during time warp — vessels pass through each other and physics is suspended. This means the spawn moment is not time-critical during warp. The sequence is:

1. Player enters time warp. Ghosted vessels continue as ghosts (visually indistinguishable from warp behavior since all vessels are non-physical during warp).
2. UT crosses one or more chain tips during warp.
3. Player exits time warp. Parsek's spawn queue fires: all vessels whose chain tips were crossed during warp spawn as real.
4. Spawn queue blocks re-entering time warp until all spawns within the current loaded physics bubble (~2.3km of the active vessel) are resolved. Spawns outside the physics bubble (e.g. a station at the Mun while the player is at KSC) do not block warp — they are queued and processed when the player enters their physics bubble.

This prevents physics explosions from multiple vessels materializing simultaneously at high warp speeds.

> **Correction (addendum v1.4):** Spawns outside the physics bubble are NOT queued. A ProtoVessel is created immediately at the chain tip UT — the vessel appears in tracking station and map view right away. Only the visual conversion (ghost GO → loaded Vessel) requires the player's physical presence. See addendum Section 7.1.

### 8.10 Self-protecting property

The ghosting model is self-protecting against conflicting recordings by construction. Once R1 is committed and claims S, S becomes a ghost. A ghost has no physics, no colliders, no docking ports — the player cannot create a second recording R2 that also modifies ghost-S. The only way to interact with S again is to wait for the chain tip spawn and target the post-chain real vessel. This guarantees that all recordings modifying a vessel lineage are naturally ordered — no branching conflicts are possible.

### 8.11 Asteroids, comets, and debris

Asteroids and comets have no controller — under Parsek's identity model they are debris. However, background recording captures trajectories of all objects in the physics bubble, including debris. When a committed recording grabs an asteroid with the AGU (claw), the timeline merge algorithm walks the background recording, finds the CLAW merge event on the asteroid, and attributes it to the asteroid's timeline.

This triggers ghosting through the standard rule: a structural MERGE is a physical state change. The asteroid is ghosted from rewind until the chain tip where the combined vessel (ship + asteroid) spawns as real. The background recording provides the trajectory data needed for the ghost.

No special mechanism is needed. Asteroids, comets, and any debris in the physics bubble that gets physically interacted with all flow through the same pipeline: background recording captures trajectory → merge algorithm attributes events → physical state change triggers ghosting → ghost plays from background trajectory data.

The only nuance is that the ghost trajectory comes from background recording data rather than from an active recording chain, since the object has no controller and therefore no primary recording timeline of its own. Background recording only captures trajectory data while the object is loaded in the physics bubble. If the asteroid was outside the physics bubble for part of the ghost window, the ghost falls back to orbital propagation (Keplerian elements from the last known state) for that portion, same as the orbital checkpoint system used for on-rails vessels.

### 8.12 Surface base position drift

If a ghosted vessel is a surface base, KSP's procedural terrain system can produce slightly different terrain heights at the same coordinates between scene loads — varying by up to a few meters depending on terrain detail level, shader LOD, and floating point precision at distance from the scene origin.

When the real vessel spawns at the chain tip, its recorded altitude may not match current terrain height. This could cause the vessel to spawn clipped into the ground (terrain rose) or floating above it (terrain dropped).

**Resolution: terrain raycast correction plus physics settling.**

**Step 1 — Store terrain height in recording.** For any segment that ends in a surface-stationary state, the recording stores both the vessel altitude and the terrain height at that position as separate values. This allows computing the vessel's ground clearance at recording time: `recordedClearance = recordedAlt - recordedTerrainHeight`.

**Step 2 — Terrain raycast at spawn time.** Before spawning, Parsek casts a ray downward from above the recorded coordinates to get the current terrain height at that lat/lon on that body.

**Step 3 — Altitude correction.** Spawn altitude is set to `currentTerrainHeight + recordedClearance`. This places the vessel at the same relative height above current terrain, regardless of how terrain height has shifted.

**Step 4 — Physics settling.** After spawn, the vessel is given a brief physics settling period (a few frames) where landing legs and wheels interact naturally with terrain. This handles sub-meter errors that the raycast doesn't catch — terrain mesh detail below the raycast resolution, slight slope differences, and landing gear compression.

This two-phase approach (raycast for coarse correction, physics for fine settling) matches how KSP itself handles loading landed vessels. The raycast prevents large altitude mismatches that could break landing legs or cause tip-overs, and physics settling handles the remainder.

**Ghost extension applies here too.** If the bounding box spawn check from 8.2 blocks the spawn (e.g. a rover is parked on the base's footprint), the ghost continues at its surface coordinates. When the spawn eventually fires, the raycast correction uses terrain height at that moment.

### 8.13 KSP load vs Parsek rewind

KSP load (quickload or Load Game) is a hard reset — the save file becomes truth. How this interacts with ghost chains depends on whether the loaded save contains the committed recordings:

- **Load a save from before R1 was committed:** R1 does not exist in this save's timeline. No ghost chain. S is a normal real vessel. The player is back to a clean state.
- **Load a save from after R1 was committed:** R1 exists in this save. Ghost chain rules apply from the loaded UT forward — S is ghosted if R1 claims it and the loaded UT is before R1's chain tip.
- **Load a save from after R1's chain tip:** R1 has already completed in this save. The real S+A (or whatever the chain tip produced) is a normal persistent vessel. No active ghost chain.

Parsek must re-evaluate all ghost chains on any save load, using the loaded save's committed recording set and current UT as inputs. This is the same chain walk that happens on Parsek rewind, applied to KSP's native load path.

### 8.14 Ghost visual identity

Ghosts must be visually distinguishable from real vessels. If the player cannot tell ghost-S from real-S, they will attempt to dock to it and be confused when it fails.

Ghost vessels should have a distinct visual treatment. The specific implementation (transparency, color tint, outline effect, label overlay) is deferred to the coding agent, but the requirements are:

- Ghosts must be immediately recognizable as non-real at a glance, both up close and at distance
- The visual treatment must not obscure the vessel's shape (the player needs to see what vessel it is)
- Ghost vessels should display a label or tooltip indicating their status: vessel name, "Ghost — spawns at UT=X", and which recording claims them
- The visual treatment should be consistent across all ghost types (chain ghosts, loop ghosts, background trajectory ghosts)

EVA kerbals pass through ghosts without interaction — the kerbal clips through the ghost mesh. This is another reason visual distinction matters: without it, the player may EVA a kerbal toward what appears to be a real vessel, only to find the kerbal passes through.

---

## 9. Design Principles

1. **Ghosts are the only reliable paradox prevention.** UI blocks and reservation systems can be bypassed by physics. A ghost vessel has no physics, no colliders, no docking ports — physical interaction is impossible by construction.

2. **Ghost until tip of chain.** A vessel that is claimed by a committed recording stays ghost until the final committed interaction in its lineage completes. The real vessel spawns only at the chain tip.

3. **Physical state is the trigger.** Only events that change a vessel's orbit, part configuration, resources, or crew force ghosting. Observation and cosmetic changes do not.

4. **Time warp is the workaround.** Players time-warp past ghost windows. In stock KSP with no time-dependent consumables, this has zero gameplay cost.

5. **First loop is real, rest are visual.** Looped recordings only spawn real vessels on the first iteration. Subsequent loops are ghost-only scenery.

6. **No deletion, no paradox.** Committed recordings are permanent. The ghost chain can only grow (new recordings added), never shrink (recordings removed). This guarantees monotonic convergence to a consistent timeline.

---

## 10. Impact on Recording Design Document

Section 11 of `parsek-recording-system-design.md` (v2.4) — "Interaction with Pre-Existing Persistent Vessels" — needs revision:

- **11.1 Core Rule** currently says "Parsek NEVER modifies any parameter of a real persistent vessel." This needs refinement: Parsek now converts real vessels to ghosts (hiding them) and spawns real vessels at recording completion. It still never modifies a real vessel's state directly — it replaces them entirely.

- **11.2 Background Tracks** remains correct — observation does not trigger ghosting.

- **11.3 Merge Events** needs the full ghost chain model described here, replacing the simpler "ghost approaches and despawns" behavior.

- **New subsection needed:** Ghosting trigger taxonomy (Section 5 of this document).

- **New subsection needed:** Ghost chain resolution and spawn-at-tip logic (Section 4 of this document).

- **New subsection needed:** Looped recording interaction rules (Section 6 of this document).

- **New subsection needed:** Edge cases — destruction, spawn collision, independent chains, recording scope, intermediate spawn suppression, cross-perspective attribution, multi-vessel separation, vessel recovery, spawn queue, self-protecting property, asteroids/comets/debris, surface base position drift, KSP load behavior, ghost visual identity (Section 8 of this document).

- **Recording data addition:** Surface-stationary segment endpoints must store terrain height separately from vessel altitude, to enable ground clearance calculation at spawn time (Section 8.12).

- **Ghost rendering requirement:** Ghost vessels must have a distinct visual treatment to be distinguishable from real vessels (Section 8.14).

---

*Document version: 1.5*
*Resolves all seven open questions from the original paradox context document.*
*All deferred items now resolved.*
*Fourteen edge cases documented: vessel destruction (with surviving controller handling), spawn-point collision (resolved: bounding box check + ghost extension + propagated spawn), independent ghost chains, recording scope, intermediate spawn suppression, cross-perspective interaction attribution, single recording with vessel separation, vessel recovery, spawn queue and time warp (physics bubble scoped), self-protecting property, asteroids/comets/debris ghosting (with orbital propagation fallback), surface base position drift (resolved: terrain raycast + physics settling), KSP load vs Parsek rewind, ghost visual identity.*
