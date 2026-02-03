# KSP1 Mod Research: Parallel-Sequential Missions System

## Overview

This document compiles research on 20 KSP1 mods relevant to developing the Parallel-Sequential Missions System. Each mod is evaluated for code reusability, relevance to our project, license compatibility, and maintenance status.

---

## Priority Tier 1: Core Functionality (Directly Relevant)

### 1. FMRS (Flight Manager for Reusable Stages)
**Relevance: ★★★★★ CRITICAL**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/FMRS |
| Original | https://github.com/SIT89/FMRS |
| License | **MIT** ✓ |
| Latest | v1.2.9.6 (September 2025) |
| KSP Version | 1.12.5 |
| Status | **Actively Maintained** |

**Why It's Critical:**
FMRS already implements the core mechanic we need: **time jumping and save point generation**. It creates save points at vessel separation, allows jumping back in time to control dropped stages, and merges vessels back into the main save.

**Key Features to Study:**
- Save point generation at separation events
- Time revert mechanism
- Vessel state preservation during time jumps
- Save file merging after stage recovery
- Tracking station integration

**Code Components of Interest:**
- Save point creation/management
- Vessel switching across time
- State serialization/deserialization
- Event detection (separation, landing)

**Usability:** High - MIT license allows direct code reuse. Architecture closely matches our needs.

---

### 2. Persistent Trails
**Relevance: ★★★★★ CRITICAL**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/JPLRepo/KSPPersistentTrails |
| Original | https://github.com/GrJo/KSPPersistentTrails |
| License | **MIT** ✓ |
| Latest | v1.11 (March 2020) |
| KSP Version | 1.9.1 |
| Status | Unmaintained (needs recompilation) |

**Why It's Critical:**
Persistent Trails implements **vessel geometry recording and replay** - exactly what our MVP needs for kinematic playback.

**Key Features to Study:**
- Track recording (position, rotation over time)
- Ghost vessel spawning
- Physics colliders on replayed vessels
- Playback with time controls (loop, fast-forward)
- File format for storing track data

**Key Source Files:**
- `Track.cs` - Core track data structure
- `TrackManager.cs` - Manages multiple recordings
- `ReplayWindow.cs` - Playback UI
- `OffRailsObject.cs` - Handling off-rails replays
- `RecordingThresholds.cs` - Sampling configuration

**Usability:** High - MIT license. Needs updating for KSP 1.12.x but architecture is directly applicable.

---

### 3. Kerbal Alarm Clock (KAC)
**Relevance: ★★★★☆ HIGH**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/TriggerAu/KerbalAlarmClock |
| License | **MIT** ✓ |
| Latest | v3.14.0.0 (April 2022) |
| KSP Version | 1.12.3 |
| Status | Stable/Complete |

**Why It's Important:**
KAC provides **time-based event scheduling and warp control** - essential for managing timeline events.

**Key Features to Study:**
- Alarm creation at specific universal times
- Automatic warp-to functionality
- SOI change detection
- Maneuver node storage/restoration
- Cross-vessel alarm tracking
- API for other mods to integrate

**Code Components of Interest:**
- Time event scheduling system
- Warp control integration
- Persistent alarm storage
- kOS integration API

**Usability:** High - MIT license, well-documented API, stable codebase.

---

### 4. MechJeb2
**Relevance: ★★★★☆ HIGH**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/MuMech/MechJeb2 |
| License | **GPL-3.0** ⚠️ |
| Latest | v2.14.x |
| KSP Version | 1.12.x |
| Status | **Actively Maintained** |

**Why It's Important:**
MechJeb provides the **autopilot algorithms** needed for milestone-based mission execution. For our full vision (adaptive playback), we need MechJeb-style maneuver execution.

**Key Features to Study:**
- Ascent guidance
- Maneuver node execution
- Landing guidance
- Rendezvous/docking automation
- Orbital mechanics calculations (MechJebLib)

**Code Components of Interest:**
- `MechJebLib/` - Standalone orbital mechanics library (public domain!)
- Attitude control systems
- Maneuver planning algorithms
- PID controllers

**License Note:** GPL-3.0 is copyleft - if we use MechJeb code directly, our mod must also be GPL. However, **MechJebLib is public domain** and can be used freely.

**Usability:** Medium - Core algorithms usable via MechJebLib (public domain). Full integration requires GPL compliance.

---

### 5. kOS (Kerbal Operating System)
**Relevance: ★★★★☆ HIGH**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/KSP-KOS/KOS |
| License | **GPL-3.0** ⚠️ |
| Latest | v1.4.0.0 |
| KSP Version | 1.12.x |
| Status | **Actively Maintained** |

**Why It's Important:**
kOS demonstrates how to implement **scripted vessel control** within KSP. Our milestone-based execution could potentially use kOS scripts for complex missions.

**Key Features to Study:**
- Script execution engine
- Vessel control abstraction
- Flight computer simulation
- Event-driven programming model
- Integration with other mods (KAC, etc.)

**Code Components of Interest:**
- Vessel control interface
- Steering/throttle abstraction
- Trigger/event system
- Script persistence

**Usability:** Medium - GPL license limits direct reuse. Study for architectural patterns.

---

## Priority Tier 2: Time & Vessel Management

### 6. BetterTimeWarpContinued
**Relevance: ★★★☆☆ MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/BetterTimeWarpContinued |
| License | **GPL-3.0** ⚠️ |
| Latest | v2.3.14.1 (January 2026) |
| KSP Version | 1.12.5 |
| Status | **Actively Maintained** |

**Why It's Relevant:**
Provides **advanced time warp control** including lossless physics warp and freeze time.

**Key Features:**
- Customizable warp rates
- Physics warp without precision loss
- Time freeze (0x warp)
- Altitude limit overrides

**Usability:** Study for warp control APIs. GPL limits direct reuse.

---

### 7. StageRecovery
**Relevance: ★★★☆☆ MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/StageRecovery |
| Original | https://github.com/magico13/StageRecovery |
| License | **GPL-3.0** ⚠️ |
| Latest | v1.9.8 (October 2025) |
| KSP Version | 1.12.5 |
| Status | **Actively Maintained** |

**Why It's Relevant:**
Handles **automatic stage recovery** without manual control - demonstrates background vessel processing.

**Key Features:**
- Vessel destruction detection
- Terminal velocity calculation
- Background recovery simulation
- Fund/science/Kerbal recovery

**Usability:** Study for background vessel handling patterns.

---

### 8. RecoveryController
**Relevance: ★★★☆☆ MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/RecoveryController |
| License | **MIT** ✓ |
| Latest | Current |
| Status | Maintained |

**Why It's Relevant:**
Wrapper mod that coordinates FMRS and StageRecovery - shows how to **integrate multiple recovery systems**.

**Usability:** High - MIT license. Good example of mod coordination.

---

### 9. Easy Vessel Switch (EVS)
**Relevance: ★★☆☆☆ LOW-MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/ihsoft/EasyVesselSwitch |
| License | Public Domain (KSPDev) |
| Status | Maintained |

**Why It's Relevant:**
Improves **vessel switching** experience. Useful patterns for our vessel management UI.

**Usability:** High - Public domain.

---

### 10. VesselMover
**Relevance: ★★☆☆☆ LOW-MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/jrodrigv/VesselMover |
| License | **MIT** ✓ |
| Latest | v1.9.0 (2020) |
| Status | Needs recompilation |

**Why It's Relevant:**
Demonstrates **programmatic vessel positioning** - useful for debugging/testing recorded missions.

**Usability:** High - MIT license.

---

## Priority Tier 3: Orbital Mechanics & Planning

### 11. Transfer Window Planner
**Relevance: ★★★☆☆ MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/TriggerAu/TransferWindowPlanner |
| Newer Fork | https://github.com/Nazfib/TransferWindowPlanner2 |
| License | **MIT** ✓ |
| Latest | v1.8.0.0 (2022) |
| Status | Stable |

**Why It's Relevant:**
Contains **orbital transfer calculations** needed for predicting whether recorded missions can complete.

**Key Features:**
- Porkchop plot generation
- Delta-v calculations
- Transfer window timing

**Usability:** High - MIT license. Math routines applicable to trajectory prediction.

---

### 12. kRPC
**Relevance: ★★☆☆☆ LOW-MEDIUM**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/krpc/krpc |
| License | LGPL-3.0 |
| Latest | v0.5.4 |
| Status | Limited maintenance |

**Why It's Relevant:**
Demonstrates **external vessel control API** - could enable external tools for mission scripting.

**Key Features:**
- RPC server architecture
- Vessel/orbit data exposure
- Control input injection
- Cross-language clients

**Usability:** Medium - LGPL allows linking without full copyleft. Complex integration.

---

## Priority Tier 4: Essential Dependencies

### 13. Module Manager
**Relevance: ★★★★★ ESSENTIAL DEPENDENCY**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/sarbian/ModuleManager |
| License | **CC-BY-SA** |
| Latest | v4.2.3 (July 2023) |
| Status | Stable/Complete |

**Why It's Essential:**
Standard dependency for almost all KSP mods. Enables config patching without file conflicts.

**Usability:** Required dependency, not for code reuse.

---

### 14. Harmony (HarmonyKSP)
**Relevance: ★★★★★ ESSENTIAL DEPENDENCY**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/KSPModdingLibs/HarmonyKSP |
| License | **MIT** ✓ |
| Latest | v2.2.1.0 |
| Status | Stable |

**Why It's Essential:**
Enables **runtime method patching** - critical for hooking into KSP's internal systems.

**Key Features:**
- Prefix/postfix patches
- Transpilers for IL modification
- Non-destructive patching

**Usability:** Essential tool for mod development.

---

### 15. ClickThroughBlocker
**Relevance: ★★★☆☆ RECOMMENDED DEPENDENCY**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/ClickThroughBlocker |
| License | **GPL-3.0** ⚠️ |
| Latest | v2.1.10.21 |
| Status | Maintained |

**Why It's Important:**
Prevents UI click-through issues. Standard dependency for mods with custom windows.

**Usability:** Recommended dependency for UI.

---

### 16. ToolbarController
**Relevance: ★★★☆☆ RECOMMENDED DEPENDENCY**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/linuxgurugamer/ToolbarController |
| License | GPL-3.0 |
| Status | Maintained |

**Why It's Important:**
Provides unified toolbar button management (stock + Blizzy toolbar).

**Usability:** Recommended dependency for UI buttons.

---

## Priority Tier 5: Reference/Architectural Study

### 17. KSPCommunityFixes
**Relevance: ★★☆☆☆ REFERENCE**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/KSPModdingLibs/KSPCommunityFixes |
| License | MIT |
| Status | Actively Maintained |

**Why It's Relevant:**
Shows how to **patch KSP bugs** using Harmony. Excellent reference for understanding KSP internals.

---

### 18. Kopernicus
**Relevance: ★☆☆☆☆ LOW**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/Kopernicus/Kopernicus |
| License | LGPL-3.0 |
| Status | Maintained |

**Why It's Relevant:**
Extensive KSP internal modifications. Reference for deep engine hooks.

---

### 19. TestFlight / kOS
**Relevance: ★★☆☆☆ REFERENCE**

Various mods that implement **failure/reliability systems** - relevant for our mission failure detection.

---

### 20. Principia
**Relevance: ★☆☆☆☆ LOW**

| Attribute | Value |
|-----------|-------|
| Repository | https://github.com/mockingbirdnest/Principia |
| License | MIT |
| Status | Maintained |

**Why It's Relevant:**
Advanced n-body physics. Reference for orbital prediction systems, though overkill for MVP.

---

## License Summary

| License | Mods | Implications |
|---------|------|--------------|
| **MIT** | FMRS, Persistent Trails, KAC, VesselMover, TWP, Harmony, RecoveryController | ✓ Free to use, modify, redistribute |
| **GPL-3.0** | MechJeb, kOS, BetterTimeWarp, StageRecovery, CTB | ⚠️ Derived work must also be GPL |
| **LGPL-3.0** | kRPC, Kopernicus | Can link without GPL requirements |
| **CC-BY-SA** | Module Manager | Attribution + share-alike |
| **Public Domain** | MechJebLib, EVS | ✓ No restrictions |

**Recommended License for Our Mod:** MIT (maximizes compatibility and reuse potential)

---

## Recommended Code Study Priority

### Phase 1: MVP Foundation
1. **FMRS** - Study save point generation and time revert
2. **Persistent Trails** - Study track recording and replay
3. **Kerbal Alarm Clock** - Study time event scheduling

### Phase 2: Automation
4. **MechJebLib** (public domain) - Orbital mechanics calculations
5. **kOS** - Scripted control patterns (for reference)

### Phase 3: Polish
6. **ClickThroughBlocker** - UI best practices
7. **KSPCommunityFixes** - Harmony patching patterns

---

## Architecture Insights from Research

### From FMRS:
- Use KSP's built-in quicksave system for state snapshots
- Track vessel GUIDs across save/load cycles
- Hook into `onVesselDestroy` and `onPartDecouple` events

### From Persistent Trails:
- Sample position/rotation at configurable intervals
- Store minimal data (transform + timestamp)
- Spawn ghost vessels as debris-type craft

### From KAC:
- Use `Planetarium.GetUniversalTime()` for time tracking
- Integrate with stock warp controls via `TimeWarp.SetRate()`
- Store alarms in persistent `ConfigNode` format

---

## Next Steps

1. **Clone and compile** FMRS and Persistent Trails against KSP 1.12.5
2. **Extract key classes** from both mods as reference implementations
3. **Design unified architecture** combining:
   - FMRS's save point system
   - Persistent Trails' recording format
   - KAC's event scheduling
4. **Create project skeleton** with proper dependencies
5. **Implement MVP recording** (position + staging only)
