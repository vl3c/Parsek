# Dark Multiplayer Architecture Analysis
**For Parsek Project — Ghost Chain Interaction & Time Bubble Reference**

Based on thorough source code analysis of the Dark Multiplayer project (`mods/DarkMultiPlayer/`). DMP is a KSP multiplayer mod whose "subspace" system solves the same fundamental problem Parsek faces: multiple vessels at different universal times, with the player needing to synchronize to interact with a specific one.

---

## 1. OVERALL PROJECT STRUCTURE

DMP is split into Client, Server, and Common assemblies:

### `Common/Common.cs` — Shared Data Model

| Type | Purpose |
|------|---------|
| `Subspace` | Time bubble definition: `serverClock` (ticks), `planetTime` (UT), `subspaceSpeed` (0.3–1.0) |
| `WarpMode` enum | MCW_FORCE, MCW_VOTE, MCW_LOWEST, SUBSPACE_SIMPLE, SUBSPACE, NONE |
| `WarpMessageType` enum | NEW_SUBSPACE, CHANGE_SUBSPACE, RELOCK_SUBSPACE, REPORT_RATE, CHANGE_WARP, SET_SUBSPACE |

### `Client/` — KSP-Side Implementation

| File | Purpose |
|------|---------|
| `TimeSyncer.cs` | Clock synchronization, subspace locking, UT calculation |
| `WarpWorker.cs` | Warp UI, subspace switching, key bindings |
| `VesselUpdate.cs` | Position interpolation/extrapolation across subspaces |
| `VesselWorker.cs` | Vessel sync orchestration, update queuing |

### `Server/Messages/` — Server Authority

| File | Purpose |
|------|---------|
| `WarpControl.cs` | Subspace creation/management, rate adjustment, persistence |

---

## 2. SUBSPACE DATA MODEL

The core `Subspace` class (`Common.cs:1029-1034`) is remarkably minimal:

```csharp
public class Subspace
{
    public long serverClock;      // Anchor: server timestamp (DateTime.UtcNow.Ticks)
    public double planetTime;     // KSP UT when subspace was created
    public float subspaceSpeed;   // Time multiplier (0.3x to 1.0x)
}
```

**UT at any moment:** `planetTime + ((currentServerTime - serverClock) / TicksPerSecond) * subspaceSpeed`

Subspaces are stored in a `Dictionary<int, Subspace>` with auto-incrementing IDs. A new subspace is created whenever a player exits time warp (capturing their current UT as a new bubble).

---

## 3. THE INTERACTION PROBLEM & DMP'S SOLUTION

**Problem:** Player A is at UT=1000, player B is at UT=2000. A wants to dock with B's vessel. The vessel exists at both times but in different states — rendezvous requires both players at the same UT.

**DMP's solution — subspace locking:**

```
TimeSyncer.LockSubspace(targetSubspaceID)
  1. TimeWarp.SetRate(0, true)         — stop current warp
  2. lockedSubspace = target            — adopt target's time reference
  3. Send CHANGE_SUBSPACE message       — notify server
  4. FixedUpdate() skews timeScale      — converge to target UT
```

**Time convergence (`TimeSyncer.FixedUpdate`, lines 173-190):**
- **Small skew (< 5s):** Adjusts `Time.timeScale` (physics multiplier) — speeds up or slows down to converge
  - Behind: `timeScale` up to 1.5x
  - Ahead: `timeScale` down to 0.3x
  - Formula: `timeWarpRate = 10^(-currentError) * subspaceSpeed`
- **Large skew (> 5s):** Direct UT set via `Planetarium.SetUniversalTime()` — packs all vessels on-rails first

**Vessel visibility during convergence:**
- Vessels from other subspaces shown via position interpolation/extrapolation
- Updates queued per vessel GUID, processed in FixedUpdate
- Interpolation buffers next update for 1-3s, lerps between consecutive updates

---

## 4. WARP-TO-SUBSPACE UI PATTERNS

### SUBSPACE_SIMPLE mode (`WarpWorker.cs:574-577`)
- Single key press warps to the most advanced subspace (highest UT)
- No selection needed — "catch up to latest"
- **Parsek analog:** "Warp to next chain tip" button

### SUBSPACE mode (full UI)
- `clientSubspaceList` tracks which player is in which subspace
- Player sees list of available subspaces with their current UT
- Key press cycles through subspaces or picks specific one
- **Parsek analog:** Ghost vessel selection panel with per-vessel "Sync & Spawn" buttons

### Subspace selection flow:
```
Player sees vessel in other subspace (ghost-like)
  → Player presses warp key / selects from UI
    → LockSubspace(targetID)
      → Time converges to target UT
        → Both in same subspace → physics interaction possible
```

---

## 5. WHAT'S DIRECTLY USEFUL FOR PARSEK

### 5.1 The "Warp to Interact" Pattern

DMP validates the UX concept Parsek needs for ghost chain tip interaction:

| DMP | Parsek Equivalent |
|-----|-------------------|
| Player sees vessel in other subspace | Player sees ghost vessel (chain not yet resolved) |
| Vessel appears as interpolated ghost | Ghost plays back from recording data |
| Player must warp to target subspace UT | Player must warp past chain tip UT |
| `LockSubspace()` → time convergence | `TimeJumpManager.ExecuteJump(tipUT)` |
| Both in same subspace = real interaction | Chain tip fires → ghost becomes real vessel |

**Key DMP insight:** The player must **explicitly choose** to sync. DMP doesn't auto-sync — it shows the vessel as a ghost-like interpolation until the player decides to warp. This is exactly Parsek's planned `SelectiveSpawnUI` concept.

### 5.2 Time Convergence Strategy

DMP uses two strategies based on time gap:
- **Small gap:** Gradual `timeScale` skew (smooth, no discontinuity)
- **Large gap:** Direct UT set with vessels packed on-rails

Parsek's `TimeJumpManager` already implements the large-gap approach (epoch-shift). The small-gap gradual approach could be useful for cases where the chain tip is only seconds away — instead of a discrete jump, Parsek could just let normal time warp run.

**Recommendation:** Parsek should use its existing `TimeJumpManager.ExecuteJump()` for jumps > ~10s, and simply suggest the player use normal time warp for shorter gaps (or auto-warp at 1x-4x).

### 5.3 Chronological Spawn Ordering

DMP's subspace system doesn't allow skipping subspaces — you warp through them in order. Parsek's `TimeJumpManager.FindCrossedChainTips()` already handles this: when jumping to a target UT, all earlier chain tips are spawned in chronological order. This is validated by DMP's approach.

### 5.4 "Subspace Simple" as Default UX

DMP's simplest mode — one key press to warp to the most advanced subspace — maps to a "Warp to Next Spawn" button in Parsek. This handles the common case where the player just wants to get past all pending ghost chain tips without choosing individually.

---

## 6. VESSEL INTERPOLATION ACROSS SUBSPACES

DMP's `VesselUpdate.Apply()` (`Client/VesselUpdate.cs:160-351`) uses two modes:

### Extrapolation (default)
```csharp
vel = vel + acc * timeDiff;
pos = pos + 0.5 * vel * timeDiff;
```
Linear position + velocity integration with acceleration. Works for <3s gaps.

### Interpolation (1-3s buffer)
```csharp
double scaling = (currentUT - interpolatorDelay - prevUT) / (nextUT - prevUT);
newPosition = Vector3d.Lerp(prevPosition, nextPosition, scaling);
```
Buffers next update and lerps between two known states. Smoother but adds latency.

**Parsek comparison:** Parsek's ghost playback already does better — it has the full recorded trajectory and uses `TrajectoryMath` interpolation with adaptive sampling. No need to adopt DMP's approach here. DMP's interpolation is a compromise for real-time network data; Parsek has pre-recorded data and can be exact.

---

## 7. WHAT'S NOT USEFUL FOR PARSEK

| DMP Feature | Why Not Applicable |
|-------------|-------------------|
| Clock synchronization (NTP-style) | Parsek is single-player — one authoritative clock |
| Server-client architecture | No server in Parsek |
| Subspace rate management (slowest player) | Single player — no rate conflicts |
| Vessel update relay | Parsek has full trajectory data, not real-time updates |
| Offline player time tracking | No other players |

---

## 8. ARCHITECTURAL TAKEAWAYS

1. **Explicit sync is the right UX.** DMP players see other vessels as ghosts until they choose to sync. Parsek should do the same — show ghost, offer "Sync & Spawn" when the player wants to interact.

2. **Two-tier time convergence.** Small gap → gradual warp. Large gap → discrete jump. Parsek has the large-gap path (`TimeJumpManager`); the small-gap path is just normal KSP time warp.

3. **Chronological ordering is mandatory.** DMP enforces subspace ordering. Parsek's `FindCrossedChainTips()` already does this.

4. **Simple mode covers 80% of cases.** DMP's "warp to latest" single-button mode is the most-used. Parsek should have a "Warp to Next Spawn" button as the primary interaction, with per-vessel selection as an advanced option.

5. **Minimal data model works.** DMP's subspace is 3 fields. Parsek's `GhostChain` already has everything needed — the chain tip UT is the "subspace time" the player needs to reach.

---

## 9. RECOMMENDED PARSEK INTEGRATION

### Primary: SelectiveSpawnUI (Phase 6e-3)

The planned but unimplemented `SelectiveSpawnUI` should follow DMP's pattern:

1. **"Next Spawn" button** (like SUBSPACE_SIMPLE) — warps to the earliest pending chain tip. Covers the common case.
2. **Per-ghost selection panel** (like SUBSPACE list) — shows all pending chain tips with vessel name, spawn UT, and time-to-spawn. Player clicks to select target → `TimeJumpManager.ExecuteJump(tipUT)`.
3. **Chronological warning** — if selecting a later tip, show "Also spawns: [earlier vessel] at UT=[X]" (mirrors DMP's subspace ordering).
4. **Proximity trigger** — when the player's vessel approaches a ghost within physics range, show a prompt: "This vessel spawns at UT=[X]. Warp to interact?" (DMP doesn't have this but it's a natural single-player extension).

### Secondary: Gradual Warp for Short Gaps

For chain tips < ~30s away, instead of a discrete time jump, offer "Warp to Spawn" which sets KSP time warp to 2x-4x and auto-stops when the chain tip UT is reached. This is smoother than a jump for short waits and mirrors DMP's small-gap `timeScale` skew.
