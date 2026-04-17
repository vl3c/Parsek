# Parsek: Time-rewind mission recording for KSP1

## The Name

**Parsek** (from *parallel-sequential*) - a KSP1 mod that lets you execute multiple missions in parallel by recording them sequentially.

---

## Tag-line
Parsek - Time-rewind mission recording. Record, rewind and merge your parallel-sekuential adventures.

---

## The Problem

Kerbal Space Program has a **"boring middle" problem**. 

You launch a spacecraft to Duna. The transfer takes 200 days. You have two choices:

1. **Time warp and wait** - Stare at the screen for minutes while nothing happens
2. **Switch vessels** - But your other missions are also in transit, or you don't have any ready

Meanwhile, your space program sits idle. No new launches. No progress. Just waiting.

This breaks immersion and makes career mode feel sluggish. Real space agencies don't pause operations during interplanetary transfers - they run dozens of missions simultaneously.

---

## The Solution

**Parsek introduces time-rewind mission recording.**

Here's how it works:

1. **Launch a mission** - Fly it to completion using time warp as normal
2. **Record everything** - Parsek captures all events (trajectory, staging, maneuvers)
3. **Revert to launch** - Go back to the moment you launched
4. **Events merge to timeline** - Your recorded mission becomes scheduled future events
5. **Launch another mission** - While your first mission plays out automatically

You're not creating alternate timelines. You're not breaking causality. You're simply **planning missions in advance** and letting them execute while you focus on new challenges.

---

## The Core Insight

Think of it like **Git for space missions**:

- You work on a **branch** (recording a mission)
- When satisfied, you **commit** to main timeline
- The branch merges chronologically
- Other branches (missions) continue independently

There's always **one unified timeline**. Recorded missions are just "scheduled future events" from the timeline's perspective.

---

## Gameplay Example

**Day 1:** Launch Duna probe. Fly the entire 400-day mission. Land successfully.

**Revert to Day 1:** Your Duna mission is now "scheduled." Events will play out automatically.

**Day 1 (again):** Launch Mun lander. Complete 3-day mission. 

**Revert to Day 1:** Mun mission now scheduled.

**Day 1 (third time):** Launch space station core. Achieve orbit.

**Commit and play forward:** Watch all three missions execute. On Day 3, your Mun lander touches down. On Day 200, your Duna probe enters orbit. On Day 400, it lands. Meanwhile, you launched crew to your station on Day 50.

Your space program is now **alive** - multiple missions in flight, real operational complexity.

---

## What About Paradoxes?

**There are none.** Here's why:

### "What if I use resources that a recorded mission needs?"

The recorded mission **fails or adapts**. It was planned assuming certain conditions. If those conditions change, consequences follow. This is realistic - real missions fail when assumptions prove wrong.

### "What if I rescue a Kerbal before their accident?"

You can't. The accident is a **scheduled event**. If you intercept the vessel before the event, you're taking control - which resets the recording from that point. The "accident" never happens because you prevented it through active intervention.

### "What if construction finishes during a recorded mission?"

The recording used what was available at recording time. When events play back, the vessel might benefit from upgrades made since. This is a minor causality "glitch" but generally works in the player's favor and adds emergent gameplay.

### "What if two missions need the same launchpad?"

First-recorded mission has priority. Second mission queues or fails to launch. This encourages strategic scheduling.

**The key insight:** These aren't paradoxes. They're **consequences**. The single-timeline model means every action has deterministic outcomes.

---

## Why This Matters

### For Single Player

- **Eliminates dead time** - Always have something meaningful to do
- **Enables operational complexity** - Run a real space program, not sequential missions
- **Creates emergent narrative** - Missions interact in unexpected ways
- **Makes career mode strategic** - Plan infrastructure, not just flights
- **Race against yourself** - Ghost recordings double as racing opponents: fly a mission, revert, then try to beat your own trajectory to orbit, the Mun, or any destination

### For Multiplayer (Future Vision)

Parsek's architecture is **inherently multiplayer-ready**:

- Each player records missions independently
- Events merge to shared timeline by timestamp
- No synchronization needed during recording
- Conflicts resolve deterministically

This could enable **asynchronous multiplayer KSP** - players in different time zones contributing to a shared space program.

### For Immersion

Real space agencies operate this way. Apollo missions overlapped with Mariner probes. The ISS receives cargo while satellites launch. Parsek makes KSP feel like an actual space program.

---

## Design Principles

### 1. Single Unified Timeline
There is exactly one timeline (M). Recordings are speculative until committed. No branching, no alternate realities.

### 2. Recording is Speculation
While recording, you're exploring a possible future. Until you commit, nothing is real. Discard freely.

### 3. Interaction Resets Recording
If you take control of a recorded vessel, its future becomes unwritten. You're now responsible for its fate.

### 4. Failure is Allowed
Recorded missions can fail. If conditions prevent completion, the vessel does its best and accepts the outcome. This creates drama and realism.

### 5. Simplicity First
The MVP is intentionally limited. Complex features (adaptive execution, multiple concurrent recordings) come later.

---

## MVP Scope

The first release focuses on **proving the concept works**:

| Feature | MVP | Full Vision |
|---------|-----|-------------|
| Recording | Trajectory + staging only | Milestones + maneuvers |
| Playback | Kinematic (ghost replay) | Adaptive execution |
| Interaction | Take control = discard future | Graceful handoff |
| Concurrency | One recording at a time | Multiple parallel |
| UI | Simple commit/discard | Full timeline editor |

**MVP Target:** Record a mission, revert, see it play back as a ghost vessel, take control at any point.

---

## Technical Foundation

Parsek builds on proven KSP modding patterns:

- **FMRS** already implements time-revert for stage recovery
- **Persistent Trails** already records and replays vessel trajectories
- **Kerbal Alarm Clock** already schedules time-based events
- **MechJeb** already provides autopilot for adaptive execution

We're not inventing new paradigms. We're combining existing techniques into a coherent gameplay system.

---

## The Vision

Imagine KSP where:

- You plan a Jool-5 mission while running regular Mun tourism
- Your comm network deploys automatically while you focus on crewed missions
- You race against your own ghost to orbit - can you beat yesterday's launch profile?
- AI agencies compete in a single-player space race, their missions recorded and scheduled
- Multiple players contribute to a shared career across time zones
- Construction time matters because you're launching while ships are built

Parsek transforms KSP from a **mission simulator** into a **space program simulator**.

---

## Project Status

**Current Phase:** Research & Architecture

- ✅ Concept validated through community discussion
- ✅ Related mods identified and analyzed
- ✅ Technical architecture drafted
- 🔄 Development environment setup
- ⬜ MVP implementation
- ⬜ Alpha testing
- ⬜ Full feature development

---

## Links & References

- Original forum discussion: KSP2 Prelaunch Suggestions (February 2023)
- Mod research document: `parallel-sequential-mod-research.md`
- Architecture document: `parsek-architecture-v0.4.3.md` (historical); current index at `docs/parsek-architecture.md`

---

*Parsek - Because space programs don't wait.*
