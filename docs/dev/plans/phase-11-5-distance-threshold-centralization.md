# Phase 11.5: Distance Threshold Centralization

## Goal

Before implementing new ghost LOD behavior, centralize the distance thresholds that already
exist across rendering, watch mode, audio, KSC playback, spawn scoping, relative-frame
anchoring, and background sampling.

Authoritative code location: `Source/Parsek/DistanceThresholds.cs`

---

## Threshold Inventory

| Concern | Threshold | Meaning | Primary consumers |
|---|---:|---|---|
| Physics bubble | `2300 m` | Full loaded-physics boundary around the active vessel | `RenderingZoneManager`, `AnchorDetector`, `ProximityRateSelector`, `ParsekFlight` spawn scoping |
| Relative exit hysteresis | `2500 m` | Prevent RELATIVE frame entry/exit toggling near the bubble edge | `AnchorDetector` |
| Docking approach | `200 m` | High-priority near-vessel range | `AnchorDetector`, `ProximityRateSelector` |
| Mid sampling range | `1000 m` | Background recorder's middle sample-rate tier | `ProximityRateSelector` |
| Ghost visual range | `120000 m` | Flight-scene ghost mesh visibility limit | `RenderingZoneManager`, `ParsekFlight.ComputeTerrainClearance` |
| Looped ghost simplified range | `50000 m` | Looped ghosts simplify before they stop spawning | `RenderingZoneManager` |
| Watch cutoff default | `300 km` | Default user-configurable watch distance guard | `ParsekSettings`, `WatchModeController`, `ParsekFlight` |
| Airless horizon-lock threshold | `50000 m` | Below this altitude, watch camera auto-locks horizon on airless bodies | `WatchModeController` |
| KSC ghost cull | `25000 m` | KSC-scene camera cull range for expensive ghost updates | `ParsekKSC` |
| Ghost audio rolloff | `30 m / 5000 m` | Unity audio falloff min/max distances | `GhostVisualBuilder` |

---

## Important Current Policy Notes

- The same conceptual `2300 m` boundary was previously duplicated under multiple names.
  This refactor makes it a single source of truth.
- `KSC` keeps a separate `25 km` cull because that scene uses a different camera and
  different performance tradeoffs than flight playback.
- Ghost audio has no dedicated flight-scene distance mute threshold beyond Unity rolloff;
  in-flight muting is currently driven by zone hide, warp suppression, and soft-cap actions.

### Watch-mode policy decision

The watch cutoff now applies uniformly to all ghosts.

- Watch button eligibility uses the configured cutoff distance.
- `EnterWatchMode` refuses ghosts at or beyond the cutoff distance.
- Active in-flight watch exit also uses the same cutoff rule.

Follow-up LOD policy: when a ghost is actively watched and remains within the cutoff,
it should stay at full fidelity even if its normal unwatched distance tier would reduce
or hide it.
