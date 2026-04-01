# RSW Departure-Aware Spawn Warp

## Problem

The Real Spawn Warp (RSW) system allows a player to warp to a nearby ghost's `EndUT` so the ghost becomes a real vessel for docking/EVA interaction. It uses epoch-shifting to freeze all vessel positions during the time jump, preserving the rendezvous geometry.

**The bug:** If the ghost is mid-recording (e.g., in a parking orbit) and will later depart to a different orbit or SOI before `EndUT`, the warp creates an impossible situation:

1. Player intercepts ghost in 100km Kerbin parking orbit
2. Ghost recording shows: parking orbit until UT=1000, then Mun transfer burn, then Mun orbit at EndUT=5000
3. Player clicks "Warp" in RSW — epoch-shift freezes positions, clock jumps to UT=5000
4. Player is still in Kerbin parking orbit (correct — epoch-shifted)
5. Ghost spawn resolves from `VesselSnapshot` lat/lon/alt — which is in **Mun orbit**
6. The vessel materializes at the Mun. The player warped expecting a rendezvous but the target vanished.

The epoch-shift preserves the *current* relative geometry, but the spawn position reflects the *recorded final state*, which can be in a completely different orbit or SOI.

## Solution: Two-Part Approach

### Part A: Departure Detection Guard

Detect when a ghost's final spawn position diverges from its current position and **replace the "Warp to Spawn" button with "Fast Forward to Departure"**.

**Detection method:** Compare the orbit segment active at `currentUT` with the orbit segment active at `EndUT` (or the terminal orbit). If any orbital parameter differs — or if the body changes — the ghost will depart before spawn time.

**Departure UT:** The `endUT` of the current orbit segment. This is the last moment the ghost is guaranteed to be in the same orbit the player intercepted.

### Part B: UI Enhancement

Transform the RSW window from a single-action ("Warp") to a context-aware display:

| Scenario | State Column | Button | Action |
|----------|-------------|--------|--------|
| Ghost stays in same orbit until spawn | — | **Warp to Spawn** | Epoch-shifted time jump to `EndUT` (existing behavior) |
| Ghost will depart before spawn | "Departs in T-Xm Xs" | **FF to Departure** | Non-epoch-shifted fast-forward to departure UT (orbit propagates naturally) |
| Ghost has no orbit segments | — | **Warp to Spawn** | Existing behavior (trajectory-point-only recordings) |

## Detailed Design

### 1. `SelectiveSpawnUI.ComputeDepartureInfo` (new pure static method)

```csharp
internal struct DepartureInfo
{
    public bool willDepart;       // true if ghost leaves current orbit before EndUT
    public double departureUT;    // endUT of the current orbit segment (0 if !willDepart)
    public string destination;    // body name of next segment, or "maneuver" if same body
}

internal static DepartureInfo ComputeDepartureInfo(
    Recording rec, double currentUT)
```

**Logic:**
1. If `rec.OrbitSegments == null || rec.OrbitSegments.Count == 0` → `willDepart = false` (no orbit data, can't detect)
2. Find current segment: `TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT)`
3. If no current segment found → `willDepart = false` (ghost is off-rails / in atmosphere — not an orbital intercept scenario)
4. Find final segment: `TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, rec.EndUT)`, or if none, use terminal orbit fields
5. Compare current vs final using `OrbitsMatch(currentSeg, finalSeg)`:
   - If match → `willDepart = false`
   - If no match → `willDepart = true`, `departureUT = currentSeg.endUT`
6. Determine `destination`:
   - If final segment has different `bodyName` → destination = final body name (e.g., "Mun")
   - If same body but different orbital elements → destination = `"maneuver"` (burn changes orbit but stays in same SOI)

**Why compare current vs final (not current vs next)?** The player cares about one thing: "will this ghost still be here when it spawns?" Comparing against the final/spawn orbit answers exactly that. An intermediate segment that returns to the same orbit would correctly show no departure.

### 2. `SelectiveSpawnUI.OrbitsMatch` (new pure static method)

```csharp
internal static bool OrbitsMatch(OrbitSegment a, OrbitSegment b)
```

Compares two orbit segments for functional equivalence:
- `bodyName` must match (string equality)
- `semiMajorAxis` within 0.1% relative tolerance (`|a - b| / max(|a|, |b|) < 0.001`)
- `eccentricity` within absolute tolerance 0.0001
- `inclination` within 0.01 degrees

**Why these tolerances?** Orbital mechanics: even tiny parameter differences cause km-scale drift over an orbit. But we need *some* tolerance because the same stable orbit re-captured after a brief off-rails physics phase will have slightly different elements due to floating-point integration. The tolerances are tight enough that any intentional maneuver (even a tiny correction burn) will be detected.

**Why not compare all 6 Keplerian elements?** SMA, eccentricity, and inclination define the orbit shape and plane. LAN, argument of periapsis, and mean anomaly define *where* in the orbit — but two segments of the same circular orbit captured at different times will have different mean anomalies. We care about shape match, not phase match.

For the special case of comparing against terminal orbit fields (not an `OrbitSegment`), build a temporary struct from `rec.TerminalOrbit*` fields.

### 3. `NearbySpawnCandidate` Enhancement

Add fields to `NearbySpawnCandidate`:

```csharp
public bool willDepart;        // ghost will leave current orbit before EndUT
public double departureUT;     // UT when ghost departs (0 if !willDepart)
public string destination;     // "Mun", "maneuver", etc.
```

Populated during `UpdateProximityCheck` by calling `ComputeDepartureInfo`.

### 4. UI Changes in `ParsekUI.cs`

#### New column: "State"

Add a **"State"** column (width ~110px) between "In T-" and the button column. Content:

| Condition | State text | Color |
|-----------|-----------|-------|
| `!willDepart` | (empty) | — |
| `willDepart` | `"Departs in T-Xm Xs"` | Yellow |
| `willDepart && departureUT <= currentUT` | `"Departing → {destination}"` | Orange |

Use `SelectiveSpawnUI.FormatCountdown(departureUT - currentUT)` for the countdown.

#### Button changes

Replace the single "Warp" button logic:

```
if (!cand.willDepart)
    → Button label: "Warp"
    → Action: flight.WarpToRecordingEnd(cand.recordingIndex)  [existing]

if (cand.willDepart)
    → Button label: "FF to Dep."
    → Action: flight.FastForwardToDeparture(cand.recordingIndex, cand.departureUT)  [new]
```

The "FF to Departure" uses `TimeJumpManager.ExecuteForwardJump` (non-epoch-shifted), which lets orbits propagate naturally. The player arrives at the moment just before the ghost departs — they can then watch the departure in real time, or the ghost might still be close enough for a quick rendezvous window.

### 5. `ParsekFlight.FastForwardToDeparture` (new method)

```csharp
internal void FastForwardToDeparture(int recordingIndex, double departureUT)
```

Similar to `FastForwardToRecording` but:
- Target UT = `departureUT` (not `rec.StartUT`)
- Uses `TimeJumpManager.ExecuteForwardJump(departureUT)` — non-epoch-shifted
- Screen message: `"Fast-forwarded to departure of '{vesselName}' ({delta:F0}s)"`
- Logging: `[Flight] FastForwardToDeparture: jumping to UT={departureUT:F1} for '{vesselName}' (delta={delta:F1}s)`

### 6. Tooltip / Screen Notification

When a departing ghost first enters RSW proximity range, modify the existing screen notification:

- Current: `"Nearby craft: {name}. Open the Real Spawn Control window to fast forward and interact."`
- Departing: `"Nearby craft: {name} (departs to {destination} in T-Xm). Open Real Spawn Control."`

This gives the player immediate context without needing to open the window.

## Edge Cases

### Ghost with no orbit segments (trajectory-only recording)
No departure detection possible. Show normal "Warp" button. These are typically short surface/atmospheric recordings where departure isn't a concern.

### Ghost currently off-rails (between orbit segments)
`FindOrbitSegment(currentUT)` returns null. This means the ghost is in a physics-recorded phase (burn, atmospheric flight). Can't reliably predict departure. Show normal "Warp" button. In practice, off-rails phases are short — the ghost will enter an orbit segment soon, and the next proximity check (1.5s) will detect the departure.

### Ghost in final orbit segment (current segment covers EndUT)
`OrbitsMatch` compares the segment against itself (or terminal orbit which should match). `willDepart = false`. Correct — the ghost stays in this orbit until spawn.

### Departure UT is very close (< 30 seconds)
Still show "FF to Dep." — the player gets a few seconds of real-time observation before the ghost departs. No special handling needed.

### Recording with only one orbit segment
If it covers both `currentUT` and `EndUT`, `willDepart = false`. If it doesn't cover `EndUT` (ends before recording ends — ghost goes off-rails for final phase), compare against terminal orbit or mark `willDepart = false` (can't determine).

### Landed/splashed terminal state with orbital current segment
Ghost is currently orbiting but will land before `EndUT`. `OrbitsMatch` fails because there's no final orbit segment (surface terminal). This is a departure. `destination` = final body name, `departureUT` = current segment's `endUT`.

## Files Changed

| File | Change |
|------|--------|
| `SelectiveSpawnUI.cs` | Add `DepartureInfo` struct, `ComputeDepartureInfo`, `OrbitsMatch` |
| `SelectiveSpawnUI.cs` | Add fields to `NearbySpawnCandidate` |
| `ParsekFlight.cs` | Populate departure info in `UpdateProximityCheck`, add `FastForwardToDeparture` |
| `ParsekUI.cs` | Add "State" column, change button label/action based on `willDepart` |
| Tests | `DepartureInfoTests.cs` — `ComputeDepartureInfo` and `OrbitsMatch` unit tests |

## Test Plan

### Unit Tests (`DepartureInfoTests.cs`)

1. **OrbitsMatch — identical segments** → true
2. **OrbitsMatch — different body** → false
3. **OrbitsMatch — different SMA (>0.1%)** → false
4. **OrbitsMatch — different eccentricity (>0.0001)** → false
5. **OrbitsMatch — different inclination (>0.01 deg)** → false
6. **OrbitsMatch — within tolerances** → true (slightly different elements from float noise)
7. **OrbitsMatch — different LAN, same shape** → true (phase difference, not shape)
8. **ComputeDepartureInfo — no orbit segments** → `willDepart = false`
9. **ComputeDepartureInfo — single segment covers full range** → `willDepart = false`
10. **ComputeDepartureInfo — two segments, different body** → `willDepart = true`, destination = body name
11. **ComputeDepartureInfo — two segments, same body, different SMA** → `willDepart = true`, destination = "maneuver"
12. **ComputeDepartureInfo — currentUT outside any segment** → `willDepart = false`
13. **ComputeDepartureInfo — terminal orbit differs from current segment** → `willDepart = true`
14. **ComputeDepartureInfo — terminal orbit matches current segment** → `willDepart = false`
15. **ComputeDepartureInfo — landed terminal state, orbital current** → `willDepart = true`

### In-Game Tests

- [ ] Record multi-phase mission (parking orbit → Mun transfer → Mun orbit)
- [ ] Intercept ghost during parking orbit phase — verify "Departs in T-" state and "FF to Dep." button
- [ ] Click "FF to Dep." — verify time advances to departure UT, orbits propagate naturally
- [ ] Intercept ghost during final orbit phase — verify normal "Warp to Spawn" button
- [ ] Record single-orbit mission — verify no departure info shown, normal "Warp" button
- [ ] Verify proximity notification text includes departure info

## Implementation Order

1. `OrbitsMatch` + tests (pure static, no dependencies)
2. `ComputeDepartureInfo` + tests (pure static, depends on `TrajectoryMath.FindOrbitSegment`)
3. `NearbySpawnCandidate` field additions + `UpdateProximityCheck` population
4. `ParsekFlight.FastForwardToDeparture`
5. UI column + button changes
6. Notification text update
7. Build + run all tests
