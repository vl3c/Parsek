# Overlap rendering 2b - one map icon per live overlap instance

*Part of the map/TS render cutover (`docs/dev/plans/maprender-rewrite-status.md`). Integration #2 has two
parts: 2a (PR #1051, DONE) removed the conservative `SkipOverlap` skip so the Director processes an
overlap member and renders ONE ghost at the newest cycle. 2b (this doc) makes the map render ONE icon +
orbit line + polyline PER LIVE OVERLAP INSTANCE so the map matches flight reality.*

## Why (decided 2026-06-06)

A looped mission whose loop period is shorter than its length relaunches before the previous replay
finishes, so several staggered replays run at once (overlap). In FLIGHT there are N meshes; on the MAP
today there is exactly ONE icon (the newest/selected cycle). The maintainer's call: "render a single
icon on the polyline and keep it unique until the loop run ends... that's lying to the player. Render an
icon for every vessel that launches, even when overlapping, so the player sees the overlap in map view
and it reflects rendering reality from flight view. Make it accurate."

So the map must show N icons (one per live overlap instance), each at its own cycle's phase, with its own
trajectory.

## The architectural fact: N icons require N ProtoVessels

The on-map icon is the stock orbit-driver's icon, one per `Vessel.persistentId` (`GhostOrbitLinePatch`
keys on `__instance.vessel.persistentId`). There is no "draw N icons against one vessel" path in KSP. So
N icons means N ProtoVessels. Today `GhostMapPresence` is strictly one-ProtoVessel-per-recording
(`vesselsByRecordingIndex`, `ghostMapVesselPids`); 2b makes it N-per-overlapping-recording.

## Mirror the flight engine (do NOT reinvent the math)

The flight engine already computes everything 2b needs - reuse it:
- `GhostPlaybackEngine.overlapGhosts` = `Dictionary<int, List<GhostPlaybackState>>` keyed by recording
  index (the staggered instances; the primary `ghostStates[i]` is the newest cycle).
- `GhostPlaybackLogic.GetActiveCycles` -> `[firstActiveCycle, lastActiveCycle]` (the simultaneously-live
  cycle range; `firstCycle` clamped to `lastCycle - cap + 1`).
- `GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(...)` -> the per-instance playback UT (pure,
  deterministic). The map's per-instance epoch shift = `currentUT - ComputeOverlapCyclePlaybackUT(...)`.
- Stable per-instance identity = `(recordingIndex, cycleIndex)` (the flight `GhostPlaybackState.
  loopCycleIndex`). Key the map's per-instance ProtoVessels / chains / polylines / markers on this.
- Cap = `MaxOverlapGhostsPerRecording = 20` (cadence is already raised so `ceil(duration/cadence) <= cap`,
  so the map inherits the bound for free; do not add a new cap).

## Scope: FULL per-instance (not icon-only)

Each instance gets its own orbit line + icon + non-orbital polyline + marker. Icon-only (N icons sharing
one orbit line) is rejected: it is visibly wrong for non-orbital ascent/descent instances (an icon with
no polyline under it), and the orbit line comes free with the per-instance ProtoVessel anyway. The cost
driver (ProtoVessel count) is identical either way.

## What is already per-instance-ready vs what must change

Ready (keyed by pid, not recording index): the Director caches (`priorIntentByPid`, `chainByPid`,
`seedByPid`, `tracedPathByPid`), `ChainAssembler.Build` / `GhostRenderChain` already carry a real
`instanceKey` dimension (the map just hardcodes `0`), `vesselPidToRecordingId` is already many-pids->one-
recId, the marker decision `ShouldDrawNonProtoMarkerForGhost(pid)` is per-pid. So once each instance is a
distinct ProtoVessel with a distinct pid, the Director / seed patches / icon "just work" per-pid.

Must become per-(recording, cycle): the presence store (`vesselsByRecordingIndex`), the create/destroy
lifecycle funnel, `GetGhostVesselPidForRecording`, the polyline cache + active-leg sets + marker-hold
(`polylineCache`, `activeLegRecordings`, `directorOwnedLegRecordings`, `lastGoodOnLine` - keyed by
`RecordingId` today), and the polyline Driver's single-head-UT walk.

## Slices

- **(i) Map presence - N-per-overlapping-recording lifecycle [the bulk].** A per-instance store
  (`(recIdx, cycle) -> Vessel`) alongside the existing one-per-recording store (for non-overlap).
  `EnsureOverlapInstances(recIdx, traj, units, currentUT)`: call `GetActiveCycles`, create a ProtoVessel
  per missing live cycle (each gets a fresh KSP-unique pid), destroy instances whose cycle < firstCycle,
  store the per-instance epoch shift. Hook into both `UpdateFlightMapGhostLifecycle` and
  `UpdateTrackingStationGhostLifecycle` behind the overlap-only gate. Throttle creates (mirror the flight
  engine's `MaxSpawnsPerFrame`). Extend the scene-switch teardown to clear the new store.
- **(ii) Director per-instance enumeration + instanceKey.** `scene.GhostPids` already yields every
  instance pid once they are real ProtoVessels, so `RunFrame` enumerates them unchanged. Feed
  `ChainAssembler.Build` the instance's `cycle` as `instanceKey` instead of `0` (resolve pid->cycle via
  the per-instance store). Per-pid seed/epoch-shift flow through the existing pid-keyed paths.
- **(iii) Polyline + marker per-instance.** Re-key `polylineCache` (geometry shared by RecordingId; only
  the head-UT / active-leg / hold STATE goes per-(RecordingId, cycle)), `activeLegRecordings` /
  `directorOwnedLegRecordings`, `lastGoodOnLine`. The Driver's LateUpdate walk enumerates the recording's
  live cycles and draws one leg per cycle at each `ComputeOverlapCyclePlaybackUT`. Preserve the #1050
  timing invariants (decide+publish at -50, draw at `onPreCull`).

## Efficiency + safety

- **Overlap-ONLY gate:** gate the per-instance path on `units.TryGetUnitForMember(i,..) && cadence < span`
  (the predicate `ClassifyScope` already computes). Non-overlapping recordings (the vast majority) stay
  EXACTLY one-per-recording: zero new objects, zero new cost.
- **Bounded N** = `min(ceil(duration/cadence), 20)`; typical 3-6.
- **Biggest risk:** ProtoVessel create/destroy churn under time warp (cycles relaunching fast ->
  `pv.Load`/`Die` storms). Mitigate: reuse the engine's computed cycles, throttle creates, cap at 20.
- **Gate-OFF** stays legacy one-per-recording. Faithful + non-overlap members unaffected.

## Tests + validation

- xUnit pure: "map instance set == engine cycle set" equivalence against `overlapGhosts`; per-instance
  key/signature uniqueness; gate-off / non-overlap one-per-recording assertion.
- In-game (`ExtendedRuntimeTests`): map instance count == flight `overlapGhosts[i].Count + 1`, capped at 20.
- Maintainer in-game: an overlapping launch-to-orbit / short mission (loop period < length), map / TS
  view - see N icons matching the N flight ghosts 1:1, each on its own orbit + polyline, appearing /
  expiring as cycles relaunch; gate-off -> byte-identical one-per-recording.

## Size + sequencing

LARGE (multi-PR, ~3 slices). Stacks on 2a (PR #1051). The hard math is done (flight engine); the work is
mechanical breadth (re-key ~12 maps recording->instance) plus the one genuinely new piece, the
per-instance ProtoVessel lifecycle (slice i). Land the slices in order, each behind the overlap-only gate
and in-game gated.

## Adjacent follow-up (separate, not 2b)

The #1051 validation log surfaced two SUPPRESSED `TS shadow RunFrame threw ArgumentNullException` (caught
by the try/catch, in the Tracking Station, not in the overlap window). Pre-existing, harmless at runtime,
but a swallowed NRE in the TS shadow path worth a small separate investigation.
