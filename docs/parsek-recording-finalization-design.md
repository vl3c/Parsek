# Design: Recording Finalization Reliability

*Shipped design contract for sealing active and background recordings when the live KSP vessel ends before Parsek can run the normal scene-exit finalizer. The recording-finalization cache feature has landed and is treated as baseline behavior, with the known limitation below.*

*Related docs: [`parsek-flight-recorder-design.md`](parsek-flight-recorder-design.md), [`parsek-rewind-to-separation-design.md`](parsek-rewind-to-separation-design.md), [`dev/done/plans/incomplete-ballistic-extrapolation.md`](dev/done/plans/incomplete-ballistic-extrapolation.md), [`dev/done/plans/recording-finalization-reliability.md`](dev/done/plans/recording-finalization-reliability.md), [`dev/manual-testing/test-recording-finalization-cache.md`](dev/manual-testing/test-recording-finalization-cache.md).*

---

## Status

Recording-finalization reliability is implemented and production behavior follows the precedence defined here:

1. Fresh live finalization wins when the vessel/runtime can still be inspected.
2. A matching finalization cache wins when the live finalizer declines or the vessel is missing.
3. Trajectory inference remains only a degraded last-resort fallback and must log that degradation.

The landed implementation includes:

- `RecordingFinalizationCache` for in-memory terminal payloads.
- `RecordingFinalizationCacheProducer` for active, loaded-background, and on-rails refreshes.
- `RecordingFinalizationCacheApplier` for identity checks, stale/invalid rejection, predicted-tail trimming, terminal metadata stamping, and dirty-sidecar handling.
- Active/background ownership transfer when a recording moves between focused and background sampling.
- Scene-exit, crash, background destroy, unload/missing-vessel, debris TTL, and out-of-bubble cache consumers.
- Maneuver-node-safe finalization: UI maneuver nodes are detected and stock node-contaminated tails are discarded in favor of current-state propagation.
- Headless xUnit coverage and `RecordingFinalization` in-game runtime canaries, with the remaining gameplay checklist kept in the manual-testing doc.

The abandoned-stage contract (owner rulings of 2026-11-18 and 2026-11-21, todo ABANDONED-STAGE-REPLAY) sets what a predicted ending is: a drag-free fall ends at ground or water impact, an orbit dipping below the deletion line crashes on its first pass, a tail with no natural end settles on its final orbit and spawns there, an orbit whose low point is among the peaks and in which Parsek's ground scan finds no crash ends where the scan stopped checking and spawns there with its crew, stock deciding its fate from then on (Q15), a stage under open chutes descends to a touchdown, a stage still under chutes when the player leaves the flight scene is lost as stock decides, a Destroyed ending kills the crew whether stock destroyed the vessel or Parsek predicted it, any recovery of the vessel overrides a predicted crash, flying the stage again drops the prediction, and a prediction stock contradicts or a craft stock deletes is squared with the game in FLIGHT. Every rule below is the current one; the ruling ids name where it came from.

Known limitation:

- `docs/dev/done/todo-and-known-bugs-v5.md` item 639 tracked a patched-conic atmospheric-tail cache refresh that could flip from `Destroyed` to `Orbiting` from a far-future segment. It was closed 2026-05-10 as not-pursued: it is a diagnostic-only cache thrash, not a correctness bug, because the active-recorder `VesselDestroyedDuringRecording` override is authoritative. Reopen only if a future path comes to depend on that cache without the override.

## Problem

Historically, Parsek could synthesize a predicted tail for some incomplete recordings when the player exited the flight scene while the live vessel still existed. That was not enough for mission-tree correctness. A recording can end because the player switches focus, a background sibling leaves the physics bubble, KSP deletes an unfocused atmospheric vessel, the active vessel crashes before scene exit, or `FlightGlobals` is unavailable during teardown. Without a cached terminal payload, those paths could commit only the last sampled point, infer a weak terminal state from stale data, or mark the vessel destroyed without the synthetic trajectory that explains how it got there.

This is foundational for Rewind to Separation. The Unfinished Flights classifier needs trustworthy terminal state and end-time data to decide whether a sibling really ended badly and whether a rewind point should stay actionable. A terminal state that depends on which KSP object happened to survive until scene exit is not a durable gameplay contract.

## Landed Implementation Audit

The shipped implementation keeps the original scene-exit finalizer and extrapolator, and adds cache refresh/application around the timing and ownership gaps:

- `IncompleteBallisticSceneExitFinalizer.TryApply` snapshots a live vessel's patched-conic chain, then runs `BallisticExtrapolator.Extrapolate` when needed.
- `BallisticExtrapolator.ShouldExtrapolate` correctly targets flying, suborbital, escaping, and low-periapsis orbital cases while skipping landed, splashed, prelaunch, docked, and stable orbiting vessels.
- `BackgroundRecorder` records loaded physics-bubble siblings, on-rails orbit checkpoints, part death and joint-break events, debris TTL expiry, and background-vessel destruction.
- `FinalizeTreeRecordings` runs for scene exit, manual commit, revert commit, and post-destruction merge paths.
- `FlightRecorder` owns the focused recording cache and refreshes it at recording start, cadence, and lifecycle/state transitions.
- `BackgroundRecorder` owns per-PID caches for loaded and on-rails background recordings, including inherited caches from active -> background transitions.
- `ParsekFlight` resolves and applies active/background caches during tree finalization while preserving fresh-live precedence.

The historical reliability gaps now resolve as follows:

| End mode | Landed behavior | Reliability contract |
|---|---|---|
| Scene exit, live vessel exists | Fresh scene-exit finalizer appends predicted/extrapolated segments and terminal state. | Fresh live finalization wins over any cache. |
| Scene exit, vessel missing | Tree finalization resolves the active/background cache and applies it before inference. | Missing runtime objects no longer imply weak last-sample finalization when a cache exists. |
| Active crash before scene exit | `OnVesselWillDestroy` forces a cache refresh; post-destruction finalization consumes the cache after crash coalescing. | Destroyed active recordings keep a cache-extended endpoint instead of relying only on the final sampled point. |
| Background vessel destroyed | `OnBackgroundVesselWillDestroy` refreshes/flushes and keeps the cache; `ParsekFlight.DeferredDestructionCheck` applies it only after true destruction is confirmed, then persists the sidecar. | Background destroyed recordings get durable terminal state and synthetic tail metadata without treating false unload/docking signals as terminal. |
| Debris TTL or out-of-bubble end | Background TTL/missing-vessel paths refresh/apply cache before removing the recording from `BackgroundMap`. | KSP deletion endpoints are preserved and capped to stock deletion UT. |
| Focus switch / backgrounding | Focus switch transfers cache ownership from `FlightRecorder` to `BackgroundRecorder`; it does not finalize by itself. | A later unload/destroy path still has the last focused projected ending. |
| UI maneuver node present | `PatchedConicSnapshot` reports `EncounteredManeuverNode`; the finalizer discards stock node-contaminated tails and falls back to current-state propagation. | Planned UI burns remain hypothetical and cannot truncate or redirect finalization. |

## Terminology

- **Finalization** - sealing a `Recording` with a terminal state, authoritative `ExplicitEndUT`, terminal orbit/surface metadata, and any synthetic predicted tail needed for playback.
- **Synthetic tail** - predicted or extrapolated trajectory data after the last real recorded sample. It is playback/terminal metadata, not a replacement for actual history.
- **Finalization cache** - an in-memory, last-known-good synthetic tail for one live recording owner. It is refreshed while the vessel exists and consumed when the vessel ends unexpectedly.
- **Active recorder cache** - cache owned by the focused `FlightRecorder`.
- **Background cache** - cache owned by `BackgroundRecorder` per tracked background PID/recording id.
- **Premature end** - any recording-end path where Parsek cannot rely on a fresh live-vessel scene-exit finalizer: crash, KSP delete, physics-bubble unload, debris TTL expiry, focus-switch abandonment, or teardown with unavailable runtime APIs.
- **Actual projected chain** - the future path implied by the vessel's current state vector and current thrust/coast state. It explicitly excludes maneuver nodes and other UI planning tools.

## Mental Model

Recording has two independent products:

```
actual history, sampled as the player flies
  -> trajectory points, orbit checkpoints, part events

latest projected ending, refreshed while the vessel exists
  -> predicted/extrapolated tail, terminal UT, terminal state
```

Only the first product describes what the vessel already did. The second product answers: "If Parsek loses this vessel now, what would KSP consider the natural end from this point?"

The cache is a safety net:

```
while recording:
    sample real history normally
    every 5 seconds, or on a meaningful state event:
        refresh the finalization cache from the vessel's current state
        (live or on-rails)

when a recording ends:
    if live vessel is still valid:
        compute one fresh finalization result and apply it
    else if a cache exists:
        trim the cached tail to the recording's last real sample UT and apply it
    else:
        use today's inference fallback and log the degraded result
```

KSP's unload model gives the policy its shape. Unloaded vessels move on rails along their drag-free conic; atmospheric forces are not applied to distant vessels. Decompiled `Vessel.cs` (KSP 1.12.5): a packed, non-active vessel that is not landed or splashed and is outside the active vessel's pack range is deleted, its crew killed (`MurderCrew`), when `mainBody.GetPressure(altitude) > 1.0` kPa; an unloaded vessel below the terrain is deleted too. This document calls that 1 kPa altitude of a body the **deletion line** (about 25 km on Kerbin; it is not the atmosphere top). Parsek follows the game (owner principle P-STOCK, 2026-11-21: committed endings and crew outcomes must not contradict stock's background simulation): a vessel stock would delete in the air ends `Destroyed` with its fall followed to the ground; an orbit whose low point clears the deletion line (and the highest terrain) is `Orbiting`.

## Gameplay Scenarios

### Booster Separation in Atmosphere

The player launches a two-stage rocket. The first stage has a probe core and parachutes, so it becomes a controllable sibling at staging. The player continues flying the upper stage. The booster falls behind, leaves the loaded physics bubble, and stock KSP deletes it once it is outside range and still below the atmospheric deletion threshold.

Expected Parsek behavior:

- The booster background recording has its own finalization cache.
- While the booster is loaded, the cache refreshes from its falling state: a drag-free fall to ground impact, or, once its chutes are open, a chute descent to a touchdown.
- When KSP unloads or deletes the booster, Parsek consumes the cache and appends the predicted tail past the close, never cut to the close time.
- Chutes open and stock still holds the booster: it ends `Landed` / `Splashed` at the predicted touchdown (if the descent is slow enough) and spawns there. Stock deleted it in the air: it ends `Destroyed` as stock decided, crew dead, no spawn, and its ghost descends under its chutes and ends quietly at the ground. Chutes open and the player leaves the flight scene before it is down: lost at the exit as stock decides (Q14), crew dead, quiet ghost end, no spawn. No chutes, or chutes still closed: `Destroyed` at ground impact, with the explosion there.
- The booster appears as an Unfinished Flight if its parent branch has a Rewind Point; a predicted touchdown keeps the separation open too.

### Mun Lander and Orbiter

The player undocks a Mun lander from an orbiter. The lander is active; the orbiter is a background recording in a stable Mun orbit. The player lands and exits to the Space Center.

Expected Parsek behavior:

- The lander finalizes from a fresh live vessel on scene exit.
- The orbiter background cache says the orbiter remains in stable orbit.
- If the orbiter is still live, finalization can refresh it; if not, the cache seals it as `Orbiting`.
- No synthetic atmospheric destruction is invented for a stable vacuum orbit.

### Scene Exit Mid-Burn

The player clicks Space Center during ascent while engines are firing.

Expected Parsek behavior:

- The cache may be up to 5 seconds stale, but the scene-exit path makes one fresh live-vessel finalization attempt before teardown.
- If the live attempt succeeds, it wins over the cache.
- If KSP has already torn down the vessel/runtime, the cache is good enough to avoid a frozen mid-burn endpoint.

### Focused Crash

A re-entry craft hits terrain while focused. `onVesselWillDestroy` fires, the part-death cascade begins, and a post-destruction tree merge may happen before scene exit.

Expected Parsek behavior:

- The last pre-impact cache already predicts destruction near the crash UT.
- The crash/coalescer finish path consumes that cache instead of relying only on the final sampled point.
- The recording gets `Destroyed`, a terminal UT, and a short tail that carries playback into the impact/destruction endpoint.

### Vessel Switch Abandonment

The player switches away from a controllable sibling and never returns to it. It backgrounds, goes on rails, then later unloads or is destroyed by stock rules.

Expected Parsek behavior:

- Switching focus alone is not a terminal event.
- The recording owner changes from active to background, but the cache follows the recording id.
- When the background vessel actually ends or becomes unreachable, Parsek consumes that recording's cache.

### Rewind to Separation Dependency

A staged booster ended in the background and the player wants to re-fly it. The UI must know whether the booster is genuinely crashed/destroyed, still orbiting, safely landed, or unfinalized.

Expected Parsek behavior:

- `TerminalKindClassifier` receives a reliable terminal state instead of stale last-sample inference.
- Unfinished Flights membership is based on a committed finalization contract, not on scene-exit timing.

## Data Model

The cache is in-memory only. It is not serialized, and it does not change the recording sidecar format. Recordings are still committed only when a recording ends.

Landed shape:

```csharp
internal sealed class RecordingFinalizationCache
{
    public string RecordingId;
    public uint VesselPersistentId;
    public FinalizationCacheOwner Owner;      // ActiveRecorder, BackgroundLoaded, BackgroundOnRails
    public FinalizationCacheStatus Status;    // Empty, Fresh, Stale, Failed

    public double CachedAtUT;
    public float CachedAtRealtime;
    public string RefreshReason;
    public string DeclineReason;

    public double LastObservedUT;
    public string LastObservedBodyName;
    public Vessel.Situations LastSituation;
    public bool LastWasInAtmosphere;
    public bool LastHadMeaningfulThrust;
    public string LastObservedOrbitDigest;

    public double TailStartsAtUT;
    public double TerminalUT;
    public TerminalState? TerminalState;
    public string TerminalBodyName;
    public RecordingFinalizationTerminalOrbit? TerminalOrbit;
    public SurfacePosition? TerminalPosition;
    public double? TerrainHeightAtEnd;

    public List<OrbitSegment> PredictedSegments; // isPredicted = true
}
```

Ownership:

- `FlightRecorder` owns one cache for the focused recording it is currently sampling.
- `BackgroundRecorder` owns a cache per `BackgroundMap` entry or per loaded/on-rails state. The cache is keyed by recording id and vessel PID so active -> background transfer preserves the last focused cache; background -> active promotion starts a fresh active cache from the live vessel.
- `VesselPersistentId = 0` means unknown legacy/test identity. Cache application still requires `RecordingId` to match, and when both sides have a nonzero vessel PID, those PIDs must match too.
- The cache payload is copied into the target `Recording` only when a consumer finalizes that recording.

No `Recording` field is needed for the cache itself. Applying a cache reuses existing persisted fields: `OrbitSegments`, `ExplicitEndUT`, `TerminalStateValue`, terminal orbit fields, `TerminalPosition`, and `TerrainHeightAtEnd`. A chute descent also carries its points in the cache (`PredictedDescentPoints`), copied by both background cache copies.

### Persisted markers of a predicted ending

Seven persisted markers record what the finalizer predicted, what stock was seen to hold and what stock recovered, as opposed to what was watched. All seven are ADDITIVE under the save-format policy (`docs/dev/design-save-format-policy.md`): no recording format or schema generation bump, an absent key reads as false / the old behaviour, and an older build ignores the keys (an older text reader reads the new section source as `Active`).

| Key / member | Where | Meaning |
| --- | --- | --- |
| `terminalPredicted` (`Recording.TerminalPredicted`) | recording, sparse | The Destroyed / Landed / Splashed ending was predicted while KSP still had the vessel, not observed. `StampTerminalState(predicted)` keeps it only beside those three endings; every other stamp clears it. A Q8 final orbit reached by a predicted tail is stamped too (the applier and the scene-exit finalizer). Read by the takeover, playback hiding, the stock-fate pass, the spawn gate, the recovery rule and the Unfinished Flights classifier. |
| `terminalRemnantSurvives` (`Recording.TerminalRemnantSurvives`) | recording, sparse, only beside Destroyed | An identity loss sealed the recording while a remnant KSP keeps still exists (`MarkDestroyedAtTerminal(vesselSurvives)`); the crew are then judged per part (edge case 30). |
| `quietEnd` (`Recording.QuietDestroyedEnd`) | recording, sparse | A chuted craft stock deleted, a chuted craft still in the air when the player left the flight scene (Q14), or a predicted final orbit stock deleted ends `Destroyed` with no explosion: the ghost comes down under its chutes (or rides its path) and ends there (edge cases 28, 37, 38). A marker, not derived: a chute descent ending `Destroyed` is otherwise the too-fast crash, which explodes. |
| `groundScanEnd` (`Recording.GroundScanEndOrbit`) | recording, sparse, only beside a predicted Orbiting | The tail ended where the ground scan of a conic among the peaks stopped checking (Q15, edge case 39). The terminal-orbit spawn check lets this one ending through its periapsis floor. A marker, not derived: an `Orbiting` under the floor is otherwise a defect the spawn check refuses. |
| `stockHeldUT` (`Recording.StockHeldVesselUT`) | recording, sparse | The UT at which the live game was last seen holding the craft of a predicted touchdown or final orbit, stamped in place only from a live read of the current stock state (CommitTree while the craft is in the game, and every stock-fate pass that finds it). Cleared by every time-travel load to a stock state older than the stamp, so a rewound timeline carries none. It separates "stock deleted it in this timeline" from "this timeline never had it" (edge case 37). |
| `RECOVERED_CREW` (`Recording.RecoveredCrewNames`) | recording, sparse child node | The crew stock recovered, read from the ProtoVessel at `OnVesselRecovered` and from the live vessel when the in-flight Recover arms, carried through `StampTerminalState` before the end snapshot is dropped. A kerbal it names is `Recovered` even when his start seat is a destroyed part (an IVA transfer, edge case 29). |
| `TrackSectionSource.Predicted` (= 3) | track section `src` | A section the finalizer predicted (the chute descent), not watched samples. Carried by the binary sidecar as the int and read by the text reader. |

`OrbitSegment.isPredicted` stays the marker for predicted orbit segments. The codec, `DeepClone` / `ApplyPersistenceArtifactsFrom`, `SessionMerger`, `SplitAtUT`, `AbsorbInto` and `IPlaybackTrajectory` carry the recording flags, the stamp and the recovered crew list. `RecordingTree.MergePartnerCoastEvidence` (the merged vessel's orbit at a dock or board, edge case 32) is in memory only and never serialized.

## Behavior

### Refresh Policy

Refresh every 5 seconds while a recording has a live vessel reference, plus forced refreshes on meaningful state changes:

- recording start or promotion from background
- active -> background transition
- throttle crossing idle/active thresholds
- engine or RCS activity while loaded
- atmosphere entry or exit
- SOI change
- go-on-rails or go-off-rails
- vessel situation change
- staging, undock, EVA, joint break, and part death
- time-warp rails/checkpoint boundaries
- scene-exit/background-commit finalization entry, where fresh live finalization or background force-refresh runs before any fallback consumes stale data

Refresh early-outs when the vessel state digest has not meaningfully changed. A stable coasting vacuum orbit does not need a new cache every 5 seconds if the orbit, body, situation, throttle state, and terminal result are unchanged.

### Actual Chain Only

Finalization must ignore KSP maneuver nodes. A maneuver node is an editor/planning UI object, not recorded trajectory data and not something the vessel will actually do unless the player later burns.

Landed behavior detects maneuver-node boundaries in `PatchedConicSnapshot` with `EncounteredManeuverNode`. When a stock patched-conic tail is node-contaminated, `IncompleteBallisticSceneExitFinalizer` discards that tail and falls back to current-state propagation through Parsek's conic/ballistic path. The old `StoppedBeforeManeuver` terminal behavior is retired because it turned a hypothetical UI node into a real terminal boundary.

### Active Recorder Refresh

The active recorder attempts the highest-fidelity cache:

- For live focused vessels, refresh from the live vessel state and stock node-free conic data when available.
- During powered flight or atmospheric flight, the cache is expected to change; refresh by cadence and forced triggers.
- During vacuum coast with no meaningful thrust, the cache stabilizes; repeated refreshes are skipped unless an event invalidates the digest.

### Background Recorder Refresh

Background recorders do not have the same solver access as the active vessel. They still cache a reliable terminal outcome:

- Loaded background vessels in the physics bubble can refresh from live vessel/orbit state.
- On-rails background vessels can refresh from `vessel.orbit` and existing on-rails checkpoints. Only the active vessel has a patched-conic solver (decompiled `Vessel.MakeActive` / `MakeInactive`), so a solver-less live vessel (debris, an EVA kerbal, an undocked craft) is followed along its live orbit to its natural ending, never read as "Destroyed now" (`IsLiveVesselAlive`; stock `Vessel.Unload` empties `vessel.parts`, so an unloaded vessel counts alive through its protoVessel part snapshots, `IsVesselAliveCore`).
- A vessel packed by warp inside the atmosphere records its on-rails conic too (stock moves it on that drag-free conic until it deletes, unpacks or hands it over). The span carries one fixed environment label, Atmospheric, cleared at an SOI change (`BackgroundOnRailsState.packedInsideAtmosphere`), so it adds no optimizer split candidate; the refresh keeps the live-vessel cache for such a span and rebuilds from the conic only when the cache is missing, empty or failed.
- Stable orbits cache `Orbiting` plus terminal orbit metadata. "Stable" means a bound orbit whose periapsis clears the body's **periapsis floor** (`OrbitClearance`: max(deletion line, highest terrain); the deletion line is 0 on an airless body), the same line commit classification (`RecordingTree.IsBoundOrbitClearOfFloor`), the terminal-orbit spawn check (`TerminalOrbitSpawnSafety`, operator ruling 2026-09-27 "any orbit Parsek calls Orbiting will spawn", flight-recorder design 13.5), the Re-Fly auto-seal preview and the extrapolator read. The on-rails stable-orbit cache (`TryBuildFromStableOrbitSegment`) checks the floor too: an unbound conic or one under the floor is extrapolated into its real ending. An extrapolated `Orbiting` whose last segment's periapsis is under the floor is downgraded to `SubOrbital`, except the ground-scan-end ending (owner ruling Q15, below and edge case 39), which keeps `Orbiting` and spawns.
- Falling, suborbital and escaping cases cache the predicted ending (see "Predicted endings"). A stock deletion does not cut the tail: the predicted fall continues to its end (owner ruling Q1a).

This does not create a cross-scene background recorder. The cache only protects the in-flight session that is already recording.

### Predicted Endings

`BallisticExtrapolator.ShouldExtrapolate` follows a FLYING, SUB_ORBITAL or ORBITING coast that is unbound, under the periapsis floor or unknown, and always an ESCAPING one; a bound coast clear of the floor is a stable orbit and is not extrapolated. The propagation stays a plain drag-free Kepler arc (owner ruling Q5) and ends on the first of:

- **Ground or water impact** (`reason=surface-impact`): `Destroyed` with the explosion there (Q2). There is no atmosphere-top stop. Water counts on an ocean body; on a body without an ocean the fall ends at the real ground below the datum (`ExtrapolationBody.TerrainBelowDatum`). A closed conic whose low point is among the peaks scans later periapsis passes for an impact, with the body turning under it.
- **Deletion periapsis** (`reason=deletion-periapsis`): a low point above the ground but below the deletion line ends `Destroyed` at the first periapsis pass (Q3).
- **Final conic** (`reason=stable-conic`, owner ruling Q8): a conic that is bound, has its periapsis strictly above the floor, stays inside the SOI and has no surface event or sphere change within one period is final; the tail ends `Orbiting` after `FinalConicSettleSeconds` (60 s, at most a quarter period) on it, and the vessel spawns there with its crew when the replay reaches it. An open conic around a body with no parent (the Sun) with no child sphere ahead ends `SubOrbital` the same way (`reason=final-escape`) and spawns on its path (ruling Q10). Encounters after the first period are not predicted; the spawned vessel meets them in stock.
- **Ground scan end** (`reason=ground-scan-end`, owner ruling Q15): a closed conic whose low point is among the peaks and whose later-pass scan found no impact ends `Orbiting` where the scan stopped checking (edge case 39).
- **Horizon cap**: 50 Kerbin years (KSP's calendar year), only for unresolved cases, with a Warn naming ecc, periapsis and floor.

Child SOI entries are searched inside the child's radial band with every near closest approach refined; the reverse crossing at an SOI transition instant is suppressed. A pending maneuver node keeps the stock patches up to the node and propagates the unburned coast from there. An orbit rebuilt from a recorded body-fixed sample uses the planet rotation of the sample's own UT. Every predicted segment carries the live vessel's spin.

**Chute descent** (owner rulings Q4, Q10b, Q11). A live vessel in an atmosphere with a parachute DEPLOYED or SEMIDEPLOYED gets a descent instead of the fall. A loaded vessel's ModuleParachute is read live, and a loaded part's RealChute `RealChuteModule` by reflection into the node shape RealChute persists (`RealChuteLiveRead`, public fields only, no RealChute reference); an unloaded vessel's persisted chute modules are read through `ParachuteProtoModuleRead.TryReadCanopy` (ModuleParachute, ModuleEvaChute and RealChute's RealChuteModule, PREDEPLOYED / LOWDEPLOYED read as semi-deployed, the most open canopy wins), so a loaded and an unloaded RealChute part share one read. A chute staged but still armed, stowed or cut does not count (Q10b). `ParachuteDescentPlanner` keeps the body-fixed lat / lon and descends at constant speed: deployed at the measured rate, semi-deployed at the measured rate down to the full-deploy altitude (stock 500 m above the ground), then the full-canopy rate. The touchdown is judged on the full-canopy rate estimated by the terminal-speed law v = sqrt(2 m g / (rho Cd A)): `v_land = v * 1/sqrt(clamp(drag/weight, 0.1, 10)) * sqrt(D_now / D_full) * sqrt(rho_here / rho_ground)`, with D the open chutes' summed stock drag (`semiDeployedDrag` / `fullyDeployedDrag`, the part prefab for an unloaded vessel) or, for RealChute, the open canopies' summed area (PREDEPLOYED at its pre-deployed diameter, LOWDEPLOYED / DEPLOYED at the deployed one; the two units are never mixed: a vessel with both kinds open reads no drag), drag/weight from stock `Vessel.perturbation` over gravity (unpacked loaded vessels only, else terminal speed is assumed) and the densities `body.GetDensity` at the vessel and at the ground; a 7 m/s default stays only when nothing can be scaled (`landingSource=default-*`). The stage survives only at or below `MaxSafeLandingRate` = 12 m/s; faster it ends `Destroyed` at ground impact. Survived: `Landed` on the terrain or `Splashed` at sea level, `TerminalPredicted`, the touchdown written into the end snapshot so the spawn sits where the replay lands. The descent is stored as one Absolute, Atmospheric `TrackSectionSource.Predicted` section from the last authored UT to the ending. Whether that touchdown stands is then stock's call: leaving the flight scene decides it lost (Q14, edge case 38), and a craft stock deletes later ends lost (edge case 37).

### Crew Of A Predicted Ending

A `Destroyed` ending kills the crew, whether KSP destroyed the vessel or Parsek predicted the crash (owner ruling Q6c, "if it's destroyed, the crew is dead"; stock warns that leaving flight in the atmosphere loses the vessel, P-LEAVE). The ways out are flying the stage again (Q1b) and recovering it (Q13b). Parsek never invents a reputation number: a death penalty row exists only when stock took one (Q12b). A chuted craft stock deleted is `Destroyed` with its crew dead and nothing spawned (Q4d); a chuted craft stock still holds keeps `Landed` / `Splashed` and spawns its crew. In a breakup, only kerbals whose part KSP destroyed die (Q6b, edge case 30). Any recovery of the launch-matched vessel overrides a predicted `Destroyed`, before or after its predicted UT, and the crew it names are recovered (Q13b, edge case 29); a confirmed crash stays final.

### Takeover And Reconcile

A predicted ending is a guess about a vessel KSP may still hold. Four paths square it with the game, and every one only ever moves Parsek toward stock:

- **Takeover** (owner ruling Q1b): the player flies, docks with, grabs or boards the stage again (edge cases 31-33).
- **Recovery** (Q13b): the player recovers it (edge case 29).
- **Crash reconciler** (P-STOCK, P-LEAVE): stock kept the vessel alive past its predicted crash (edge case 34).
- **Stock-fate pass** (P-STOCK, Q4d, Q8): stock deleted a craft Parsek predicted would land or settle in orbit, or kept a touchdown flying (edge case 37); a scene exit decides a chuted craft still in the air (Q14, edge case 38).

The crash reconciler and the stock-fate pass run in FLIGHT only (`PredictedCrashReconcilerHost`): stock's `Vessel.CheckKill` runs only with an active vessel, so nothing is deleted in the Tracking Station or at the Space Center, and the end-spawn gate holds a stamped prediction there until the next flight. A surviving breakup remnant (`terminalRemnantSurvives`) is a confirmed outcome, never a prediction to any of the four paths.

### Background Close

A background close (debris time limit, unload, parent close, confirmed destruction) keeps the cache's predicted ending when its terminal UT is at or after the close: a `Destroyed` / `Landed` / `Splashed` ending, and an `Orbiting` / `SubOrbital` one when the cache carries a predicted segment past the close (a final orbit). A switch close passes `retainPredictedTailPastEnd=false`, so a tail never overlaps the continuation; an ending pulled back to the close drops the cache's far `TerminalOrbit`, so the applier stamps the orbit of the last retained segment. A close after an ending the cache already predicted (a deletion Parsek did not see) is clamped to that ending.

### Applying a Cache

When a cache is consumed:

1. Find the last real authored UT on the `Recording` from `Points`, `TrackSections`, and non-predicted `OrbitSegments` (`TryGetLastAuthoredUT` skips `Predicted` sections and the flat points after their start).
2. Drop cached segments that end before that UT; zero-length segments are dropped everywhere.
3. Clamp the first retained segment start to the last real UT when safe.
4. Append retained predicted segments with `isPredicted = true`. A chute descent replaces any older one (`RemovePredictedDescent`) and appends its `Predicted` section, whose first frame is the real last sample when body-fixed (deduped), the body-fixed shadow of a Relative last sample, else the descent evaluated there; its points carry the last sample's funds / science / reputation.
5. Set `ExplicitEndUT` to the cache terminal UT if it extends the recording.
6. Set `TerminalStateValue`, terminal orbit/surface metadata, and terrain height when present. The ending is stamped `TerminalPredicted` unless the cache came from a destroy-event refresh or the caller confirmed the destruction.
7. Mark files dirty if the recording has already flushed sidecar data.
8. Emit a single summary log line naming the cache age, freshness, source, and consumer path.

Fresh live finalization has precedence over cache application. Cache application has precedence over trajectory-only inference. Trajectory-only inference remains the last-resort fallback.

### Consumer Paths

The implementation consumes cache at these seams:

- scene exit for active and background recordings when the live finalizer declines or the vessel is missing; the scene-exit apply starts every predicted segment at the last authored UT, as the cache apply does, so a tail rebuilt from an earlier recorded point survives the session merge. Step 3c of `FinalizeTreeRecordingsAfterFlush` then applies Q14 to every predicted chute touchdown of the tree (edge case 38).
- manual commit or revert commit when a leaf/background vessel is already missing but has a usable cache
- an in-flight commit (Commit Flight, the far Switch-To Case B merge, a post-crash merge): a live non-active leaf, and the active leaf when the pre-switch Merge abandons it (`CommitTreeFlightAbandoningActiveVessel`), runs the lifetime finalizer, or a fresh `Destroyed` cache when it declines, before the situation stamp (owner ruling Q1b). A plain CommitTree keeps the active vessel's live situation stamp. The background recorder is torn down after the commit (`ReleaseBackgroundRecorderAfterInFlightCommit`), so committed recordings gain no part events.
- active vessel destruction after crash coalescing decides the recording/tree should finalize
- confirmed background destruction through `ParsekFlight.DeferredDestructionCheck` after `OnBackgroundVesselWillDestroy` has refreshed/flushed and kept the cache. A destroy refresh of a packed, airborne vessel is a stock on-rails deletion: it runs the normal extrapolation coerced to `Destroyed`, never stamps the recording eagerly, and the recorder prefers the fresh fall, then the previous cache's fall, then "Destroyed now". The false-destroy branch takes back an eager stamp and drops that refresh's cache.
- debris TTL, out-of-bubble, and missing-vessel termination in `BackgroundRecorder.CheckDebrisTTL` (the Background Close rule above)
- vessel-unloaded paths that remove or strand a background recording
- any stop/abandon path that removes a recording from `BackgroundMap` or ends a live recorder without a fresh vessel
- a restored tree (an F9 or a resumed save): `EnsureBackgroundRecorderAttached` forces one cache refresh per live member (`RebuildFinalizationCachesAfterRestore`), and a member stock dropped meanwhile follows its recorded fall (`CloseMissingBackgroundMembersAtLoad`, edge case 35)

Focus switching is not itself a consumer unless the switch path actually ends the old recording. In the normal tree model, switching transfers cache ownership to the background recorder instead.

Playback draws what the cache applied. A predicted tail lies past every track section, so for a parent-anchored recording (a dropped booster, a decoupled core) it is authored coverage there: the flight ghost follows the tail, loops included, and ends at its terminal UT with the explosion of a `Destroyed` ending (`GhostPlaybackEngine.IsInAcceptedPredictedTail`; Q1a, Q2). The Space Center ghost and the map / Tracking Station markers ride the same tail.

Later paths square a committed or pending prediction with the game: the takeover (`PredictedEndingTakeover.DropPredictedEndingForTakeover`, edge cases 31-33), the recovery (`PredictedEndingRecovery`, edge case 29), the crash reconciler (`PredictedCrashReconciler`, edge case 34) and the stock-fate pass (`PredictedTouchdownStockFate`, edge cases 37-38).

## Edge Cases

1. **Live scene exit with active vessel present.** Fresh finalization runs and wins. Cache is only a fallback.
2. **Scene exit after vessel object vanished.** Cache applies. If no cache exists, trajectory inference applies and logs degraded finalization.
3. **`FlightGlobals` unavailable in teardown/headless seam.** Cache applies without touching runtime-only APIs. If the cache is empty, existing guarded decline remains.
4. **Manual commit after a sibling already unloaded.** Cache applies before the non-scene-exit missing-vessel path stamps a weak destroyed fallback.
5. **Player has a planned maneuver node.** The node is ignored. The cached chain is the real coast/powered trajectory from current state, not the UI plan.
6. **Burn in progress.** Cache refreshes by cadence and throttle/engine triggers. Scene exit attempts one fresh refresh before consuming anything.
7. **Thrust stops in vacuum.** Forced refresh captures the new coast. Later refreshes can early-out until SOI/warp/orbit changes.
8. **Atmospheric unpowered flight.** Cache refreshes while loaded; if KSP unloads or deletes the vessel, the predicted drag-free fall is kept to its end (owner ruling Q1a) and the terminal state is `Destroyed` at ground impact. A vessel under open chutes is edge case 27.
9. **Falls end at the ground; orbits clear of the deletion line are stable.** A predicted fall ends with the explosion at ground or water impact (owner ruling Q2), never at the atmosphere top or the deletion line. A bound orbit whose periapsis clears the periapsis floor (max(deletion line, highest terrain)) caches `Orbiting` and spawns; no synthetic destruction or horizon run is invented for it (Q3).
10. **Low point below the deletion line but above the ground.** The drag-free arc never reaches the ground, and stock would delete the vessel there: the ending is `Destroyed` at the first periapsis pass (`reason=deletion-periapsis`, Q3), even if the vessel is currently above the atmosphere. A low point between the deletion line and the atmosphere top is a stable orbit (edge case 9).
11. **Non-atmospheric body impact.** Cache predicts ground impact using the same ballistic terrain/sea-level fallback policy as the existing extrapolator.
12. **Background on-rails vessel lacks patched-conic solver.** Use orbit-state and ballistic/conic extrapolation only.
13. **SOI transition between refreshes.** Last cache may be stale; SOI event forces a refresh. If the vessel disappears in the same frame, the previous cache is still better than no tail and logs as stale.
14. **Crash coalescer still pending.** Do not apply cache until the coalescer has decided which tree leaves are terminal. The cache supplies the endpoint, not the structural BranchPoint decision.
15. **Debris TTL expiry while vessel still exists.** Consume cache if it is fresher than the TTL cutoff; otherwise refresh once from the live debris vessel before ending the recording.
16. **KSP deletes an unfocused atmospheric vessel before Parsek's predicted impact.** The deletion confirms the ending: terminal state `Destroyed` with `VesselDestroyed` set and the crew dead, and the cached fall is KEPT past the deletion UT to the predicted ground impact, which is the terminal UT (owner rulings Q1a, Q2). The deletion refresh never stamps the recording eagerly, so the confirmed apply lands the tail. If no fall can be predicted, the deletion UT is the terminal UT. A chuted craft is edge case 28.
17. **Cache older than allowed freshness window.** Apply only with a `Stale` log if no live refresh is possible; otherwise refresh first.
18. **Recording has no real samples.** Do not append a free-floating tail. Mark degraded/destroyed according to the existing zero-point/debris policy.
19. **Cache terminal UT is before the last real sample.** Reject the cache and fall back to live/inference. This catches stale or mismatched ownership.
20. **Recording is already finalized.** Do not overwrite terminal metadata unless the consumer is explicitly repairing a known degraded finalization and logs the replacement.
21. **Hard quit without commit.** Cache is in-memory, so it is lost. This is acceptable: Parsek commits on recording end, not continuously through power loss.
22. **Old committed recordings.** No migration. They keep whatever terminal data they already have.
23. **Many background vessels.** Refresh is per recording, but digest early-outs and 5-second cadence keep cost bounded. Implementation logs aggregate refresh counts rather than per-vessel spam.
24. **Non-final chain segment in a live tree.** A Re-Fly or resumed tree spliced from the committed tree can carry an optimizer chain HEAD (no terminal, no child branch point) next to its successor. It is never finalized as a leaf (`ParsekFlight.IsNonFinalChainSegmentInTree`): its terminal lives on the final segment. Exempt: the tree's active recording, and a segment this session recorded past its successor's start (the successor's start is its first section start when that is later than its first sample: a section opens at the optimizer cut while its first sample can be the previous section's last one). An in-flight commit also skips the lifetime finalizer for a row whose vessel continues in a later same-launch recording or in the tree's active recording (a takeover): it gets the live situation stamp.
25. **Live vessel owned by later committed history.** A pid match does not date the live vessel. When it is the terminal spawn of a different committed recording that ends after this one (`TreeCommitService.FindLaterCommittedSpawnOwner`, identity through `VesselLaunchIdentity.LiveVesselIsRecordedSpawn`: a unique spawn pid matches alone, an adoption stamp also needs the launch guid not to conclusively differ, an unknown guid falls back to pid-only), the recording is not finalized from it and keeps its committed terminal, snapshot and orbit. A chain-tip spawn keeps both the craft-baked pid and the launch guid, so a launch-guid check alone cannot tell this case apart. The tree's active recording is exempt.

26. **Tail with no natural end (escape to the Sun, a moon escape back to its planet).** Followed to the first final conic around the last body it reaches; it ends `Orbiting` 60 s into that conic (the settle span keeps a positive segment in the final body), or at the stock patch start when the stock solver found the sphere entry, and spawns there with its crew when the replay reaches that UT (owner ruling Q8). A Sun hyperbola with no child sphere ahead ends `SubOrbital` and spawns on its path (Q10). The ending is computed at commit from the last state alone, so a stage committed and rewound before it reaches the Sun's sphere still spawns around the Sun. A background close keeps such an ending (Background Close). The ending is stamped `TerminalPredicted`, so a craft stock deletes before the replay gets there ends lost instead of spawning (edge case 37).
27. **Stage under open chutes.** It descends at chute speed to a touchdown and ends `Landed` / `Splashed` there with `TerminalPredicted` (Q4), and spawns there, if its full-canopy descent is at or below 12 m/s (Q11); faster, it ends `Destroyed` at ground impact. Chutes staged but not yet open do not count (Q10b). The separation stays open in Unfinished Flights (`reason=predictedTouchdown`, read as in flight by `TerminalKindClassifier`) until flown or dismissed (Q4c); a landing the player flew closes it.
28. **Chuted craft stock deletes out of range.** Recorded as stock decided (Q4d, P-STOCK): `Destroyed`, crew dead, nothing spawned, no revival. Its descent stays the tail and the recording carries `quietEnd`: the ghost comes down under its chutes and ends at the ground with no explosion (flight, KSC, manual preview). The Re-Fly offer stays (Q4c). A fast-descent crash keeps its descent as the predicted impact and explodes.
29. **Crew of a predicted crash, and recovery.** `Dead`, as for a confirmed crash (Q6c), with the death hold; a reputation penalty only where stock took one (Q12b). Any recovery of the launch-matched vessel overrides a predicted `Destroyed`, before or after the predicted crash UT (Q13b): a live recording's predicted `Destroyed` yields to `Recovered` (`CanOverwriteTerminalState(existingPredicted)`), and a committed one is converted on a clone (`PredictedEndingRecovery`, prediction dropped as a takeover, `Recovered` at the recovery UT, committed through a same-id replace that retires the death rows). A predicted touchdown yields only to a recovery before the touchdown; a recovery before the touchdown closes the crew hold at the recovery. The crew stock recovered (`RECOVERED_CREW`) are `Recovered` whatever their start seat. A confirmed `Destroyed`, a live session's tree and a Re-Fly session's tree are left alone. Old recordings without `terminalPredicted` keep `Dead`.
30. **Breakup with a surviving piece.** An identity loss over a remnant KSP keeps stamps `terminalRemnantSurvives` beside `Destroyed`; only then are the crew judged per part (Q6b): each kerbal's seat is read by part persistentId from the end snapshot, else the start snapshot, and he is `Dead` only when a Destroyed / ParachuteDestroyed part death names his part or the end snapshot no longer carries him. A seat a Breakup / JointBreak child took is carried away with that piece, whose own recording decides (a crewed piece is always recorded). Recovering the remnant keeps `Dead` the kerbal whose part stock destroyed.
31. **The player flies a stage with a predicted ending again (takeover).** A surviving breakup remnant is never treated as a prediction here. The recording the real flight continues from drops the prediction (Q1b): predicted segments past the cut, the predicted terminal, `TerminalPredicted`, `VesselDestroyed`, terminal orbit and surface metadata, an explicit end past the cut and the crew answer; Recovered / Docked / Boarded are never touched. Every return path does it: committed-tree restore (TS Fly, KSC marker Fly, far Switch-To, alarm Jump; on the clone only, so a Discard keeps the committed copy), the intent consume (committed-clone parent, background member, and a vessel whose ended recording in the live tree holds a prediction continues under it through route 14 `ended-recording-takeover`), background promotion, committed resume and the post-switch fresh start. The cut is the takeover UT when the live vessel is still on the predicted conic there (stored element frame, tolerance max(250 m, 1e-4 r)) and the kept coast becomes recorded history; otherwise the last real sample. A same-id re-commit retires the predicted crash's crew rows and death penalty, so the real flight's rows are filed. While the launch-matched real vessel exists, a ghost on its predicted tail is hidden in FLIGHT and at the Space Center, and map presence refuses it.
32. **Dock, claw grab or EVA board onto a vessel with a predicted ending.** A takeover. In the live tree the partner's ended recording becomes the merge's second parent, its prediction dropped with the coast rule read from the merged vessel. Against an earlier committed mission, the commit (not the merge, so a Discard or revert leaves the mission as it was) drops the partner's prediction at the merge UT, ends it `Docked` / `Boarded` and re-derives its crew rows. The merged vessel's orbit is kept in memory only; after a reload the cut falls back to the partner's last real sample.
33. **Takeover after the predicted ending.** Flying a vessel the replay spawned at a predicted `Landed` / `Splashed` / `Orbiting` / `SubOrbital` ending (a chute touchdown, a final orbit) at or after that ending's UT keeps every predicted segment as history (a `Predicted` section becomes `Background`, frames and points unchanged) and drops only the ending; the flight continues from the spawn. A vessel alive after its predicted CRASH UT is still dropped (stock contradicted it). Provenance decides (`HasPredictedEnding`: `TerminalPredicted` or predicted segments past the last real sample; an explicit end counts only for `Destroyed`), so a real `Landed` ending is never read as predicted.
34. **Stock contradicts a committed predicted crash (reconciler).** `PredictedCrashReconciler` (FLIGHT only; first pass 5 s after `FlightGlobals` is ready, then on a committed-list change, the next due UT or a recheck) finds a predicted `Destroyed` whose launch-matched vessel stock still holds after crashUT + 10 s, sighted twice 2 s apart, not the active vessel, not in physics in the air, and whose trajectory no longer reaches the deletion line. It deep-clones the committed tree, drops the prediction as a takeover does, stamps the live vessel's ending and commits the clone (same-id replace: the predicted death's crew rows retire). A vessel stock removed confirms the prediction (no change); a recovery or termination row, or a live tree's dock / board partner, reads `RemovedByPlayer`, never as stock's confirmation; a confirmed crash and a breakup remnant are never candidates. P-STOCK example: an airless orbit grazing the peaks that stock keeps on rails returns to `SubOrbital`.
35. **Quickload or resumed save with background members.** The caches are rebuilt from the live members after the restore. A member stock dropped from the save whose evidence was `SubOrbital` builds a cache from its recorded data (the last orbit segment, or the orbit reseeded off the last sample with its historical planet rotation) and follows its recorded fall: `Destroyed` at impact or `SubOrbital` at the horizon, with the tail. `Orbiting` / `Landed` / `Splashed` predictions are not adopted there (a removed vessel is never made spawnable); an open parachute, a Relative-only last sample or missing body facts keep the old stamp.
36. **Background vessel packed inside the atmosphere.** Its on-rails conic is recorded (stock moves it on that drag-free conic until it deletes it), with one fixed Atmospheric label for the span, never a per-checkpoint classification; the predicted tail starts at the recorded conic's end. The load-time clip against physical sections still relabels ExoBallistic, so committed flights are untouched.
37. **Stock deletes, or keeps flying, a craft with a committed predicted touchdown or final orbit (stock-fate pass).** `PredictedTouchdownStockFate`, FLIGHT only, judges each committed predicted touchdown and predicted Q8 final orbit against the live game (live lookup through `FlightGlobals.Vessels` only: the flight state's protoVessels keep a vessel `Vessel.Die` removed until the next save). Down (landed / splashed): left as committed. Gone, `stockHeldUT` present (held by stock in this timeline), no recovery, termination or merge row and no live dock or boarding, confirmed twice 2 s apart: stock deleted it, and a clone is committed in Q4d's shape (`Destroyed`, not predicted, `quietEnd` after the descent or along the path, end UT kept, crew `Dead`, slot open as for a crash). A touchdown still in the air on rails past the touchdown + 10 s and clear of the deletion line: the touchdown is dropped and the ending follows the live vessel. A held final orbit is compared with stock's orbit (`HeldFinalOrbitComparison`): the predicted segment covering the pass UT, or past the end the final conic (a last segment no longer than the settle span) up to one period after its start (no limit for an open conic around the Sun), both propagated in the stored element frame with the live body's gravity. Another body, the ground, or a miss over max(1 km, 1% of the distance from the body centre), seen twice 2 s apart, drops the prediction the way a contradicted touchdown is dropped and the ending follows the live vessel; agreeing or not comparable (no live orbit, in physics, before the prediction, past the horizon) it is left alone. The end-spawn gate (`ShouldSpawnAtRecordingEnd`) refuses a stamped prediction whose craft is gone, and the spawn-death check clears an adopted one with no death counted, so neither a spawn nor a re-spawn brings it back before the pass converts it. A rewound timeline has no stamp (every time-travel load forgets it), so its replay spawns the craft at the touchdown or on its orbit as before.
38. **The player leaves the flight scene while a chuted craft is still in the air (Q14).** At a scene exit (Space Center, Tracking Station, main menu) and at the pre-switch Merge of a far Switch-To (`leavesFlightScene`), step 3c of `FinalizeTreeRecordingsAfterFlush` (`PredictedTouchdownStockFate.ApplyFlightSceneLeave`) turns every predicted chute touchdown of the tree into stock's lost mission: `Destroyed`, not predicted, crew dead, `quietEnd` after the descent, no spawn, slot open as a crash, whatever the player does next. A craft whose live vessel is already `Landed` / `Splashed` keeps its ending; a too-fast descent stays an exploding crash. An in-flight commit after which the player keeps flying keeps the predicted touchdown for the stock-fate pass (edge case 37). Log: `Chute descent at scene exit: ... outcome=mission-lost|kept-touched-down`.
39. **Low point among the peaks, no crash found (Q15).** A bound conic whose periapsis sits between the ground under its track and the body's highest peak (Mun, Minmus and other airless bodies; Duna) and whose later-pass ground scan finds no impact ends where the scan stopped checking: the horizon or sphere exit when every pass up to it was read, else the end of the last pass the budget (8192 samples, about 170 passes, days of game time) covered plus the 60 s settle, held before the next stretch under the peaks, so the vessel is above every peak there. It ends `Orbiting`, `TerminalPredicted`, with `groundScanEnd`, and spawns there on its orbit with its crew aboard; the spawn check skips the periapsis floor for this marker only (`reason=ground-scan-end-orbit`; the atmosphere-band deferral still applies). From then on stock decides: a crash in flight kills the crew (`Vessel.CheckKill`), and the stock-fate pass turns a craft stock deleted into a loss (edge case 37). A child sphere entered first, or a sphere exit before the scan's end, follows that event instead. A background close that pulls the ending back, and a member stock dropped from the save (edge case 35), keep the floor's `SubOrbital`. Log: `Terminal reason=ground-scan-end: ...`.

## What Doesn't Change

- Actual sampled history is still authoritative. Synthetic tails never rewrite existing trajectory points.
- Recording sidecar format does not change for the cache. It is in-memory only.
- `OrbitSegment.isPredicted` remains the persisted marker for predicted/extrapolated orbit segments that are applied to a committed recording; a predicted chute descent is a `TrackSectionSource.Predicted` section (Data Model).
- Committed recordings remain immutable after commit. Cache application happens before commit or as part of the existing finalization path. A takeover or the reconciler changes a committed prediction only by committing a clone over it (same-id replace).
- Background recording remains an in-flight, physics-bubble system. This design does not add cross-scene background simulation.
- Parsek does not simulate atmospheric drag for unloaded vessels (owner ruling Q5). It mirrors stock KSP's outcome for unfocused atmospheric deletion; the one drag-aware path is the constant-speed chute descent.
- Rewind to Separation UI and supersede semantics do not change here. They consume better terminal data from the finalization cache without changing their UI contract.
- ERS/ELS access rules do not change. The cache is pre-commit finalization state and must not introduce new raw `RecordingStore.CommittedRecordings` or `Ledger.Actions` readers outside the existing grep-audit allowlist.

## Backward Compatibility

There is no save migration and no new serialized cache node. Existing recordings keep their terminal state and predicted tail data as written. The seven predicted-ending markers (Data Model) are additive: no format or generation bump, and a recording without them reads as before (a `Destroyed` ending without `terminalPredicted` still kills its crew).

If later work discovers it needs a small persisted marker for "finalization was degraded", that must be a separate design update. The locked decision for this feature is in-memory cache only.

## Diagnostic Logging

Use subsystem tags that make the finalization decision reconstructable from `KSP.log`:

- `[FinalizerCache]` for refresh, cache status, cache application, cache rejection, and ownership transfer.
- `[Extrapolator]` for ballistic/conic propagation, terminal reason, limits, and failures.
- `[PatchedSnapshot]` for node-free stock chain capture and maneuver-node exclusion decisions.
- `[BgRecorder]` for background cache application and persistence paths, including confirmed destruction, unload, TTL, and out-of-bubble.
- `[Flight]` for tree-level finalization decisions and crash-coalescer timing.
- `[PredictedTakeover]` for a dropped or kept prediction on a takeover (`coast=kept-to-takeover` / `cut-at-last-real`, `Predicted ending kept for takeover after its end`, `keptDescentSections=N`), `[PredictedCrashReconcile]` and `[PredictedTouchdownReconcile]` for the crash and stock-fate passes (one summary line per pass, one line per conversion), and `[PredictedEndingRecovery]` for a recovery that converts or leaves a committed prediction.
- `[Extrapolator]` also logs the terminal reason (`reason=surface-impact`, `deletion-periapsis`, `stable-conic`, `final-escape`, the horizon-cap Warn) every `Chute descent predicted:` decision (canopy, rates, landing source and factors, outcome) and the Q14 verdict `Chute descent at scene exit:`.

Required log events:

- cache refresh attempt: recording id, vessel pid, owner, UT, reason, digest changed yes/no
- refresh decline: reason, whether existing cache remains usable
- cache stored: terminal state, terminal UT, segment count, source path, elapsed milliseconds
- ownership transfer: active -> background or background -> active with recording id and pid
- consumer entry: consumer path, live vessel found yes/no, cache status, cache age
- cache application summary: appended segment count, old/new `ExplicitEndUT`, terminal state
- cache rejection: stale, mismatched recording id/pid, terminal before last sample, empty payload, or already finalized
- degraded fallback: no live vessel and no usable cache, inference path used
- maneuver-node exclusion: node count or transition detected, selected node-free strategy, restore success/failure

Per-frame refresh checks must use rate-limited logs or aggregate summaries.

## Coverage Contract

The feature landed with headless unit/integration/log-assertion coverage plus in-game runtime canaries. Future behavior changes should keep that coverage shape and follow the project testing rule.

### Headless Coverage Behaviors

The unit and integration suites cover:

- cached tails append after the last real sample without overwriting or duplicating authored trajectory history
- stale caches cannot shorten a recording by moving the terminal UT before the last real sample
- active -> background transfer preserves the old focused recording cache
- stale caches apply only when the consumer explicitly allows stale fallback and no live refresh is possible
- stock atmospheric deletion forces `Destroyed` rather than `Orbiting`
- stable coast avoids unnecessary refresh churn
- planned UI maneuver nodes cannot truncate finalization or become terminal data
- stable background/on-rails orbiters classify as `Orbiting`
- falling atmospheric/suborbital background vessels classify as destroyed or degraded instead of null/orbiting
- cache application marks sidecar data dirty/persistent when background finalization has already flushed files
- the abandoned-stage contract (edge cases 8-10, 16, 26-39): `PredictedTouchdownStockFateTests`, `GroundScanEndOrbitTests`, `StockFateReconcileReviewFixTests`, `HeldFinalOrbitReconcileTests`, `SceneExitChuteDescentTests`, `RecoveryOverridesPredictedCrashTests`, `PredictedEndingRecoveryFollowupTests`, `AbandonedStageNoSolverTests`, `PredictedTakeoverAfterEndTests`, `MergePartnerTakeoverTests`, `ParachuteProtoModuleReadTests`, `AbandonedStageDeletionAltitudeTests`, `AbandonedStageAccuracyTests`, `FinalConicEndingTests`, `ParachuteDescentTests`, `PredictedCrashCrewTests`, `CrewPartFateTests`, `RecoveredPredictedEndingTests`, `PredictedEndingTakeoverTests`, `PredictedTakeoverCoastTests`, `TakeoverCrewRecommitTests`, `PredictedCrashReconcilerTests`, `QuickloadFinalizationCacheTests`, `BackgroundOnRailsAtmosphereConicTests`; harness lanes ASR-1..ASR-9 (status in `docs/dev/autotest-status.md`); in-game `OrbitSpawnClearance` carries the spawn-gate live-lookup cell

### Integration Tests

- Scene-exit finalization falls back to cache when `FlightGlobals` probe declines.
- Manual commit after a background vessel unloads applies cache instead of a generic missing-vessel fallback.
- Active crash path consumes cache after crash coalescer completion.
- Confirmed background destruction applies cache in `DeferredDestructionCheck` after `OnBackgroundVesselWillDestroy` refreshes/flushes and keeps the cache.
- Debris TTL out-of-bubble path consumes cache before removing `BackgroundMap`.
- Focus switch active -> background -> unload keeps one recording id and one cache.

### Log Assertion Tests

- Refresh/store summary includes owner, recording id, terminal state, terminal UT, and reason.
- Cache rejection logs the exact rejection reason.
- Degraded fallback logs when no live vessel and no cache exist.
- Maneuver-node exclusion logs the node-free strategy used.
- Background consume paths log aggregate counts instead of one noisy line per frame.

### Runtime Canaries And Manual Checks

The `RecordingFinalization` in-game category currently has synthetic FLIGHT canaries for:

- destroyed background cache trimming to stock deletion UT
- stable background cache applying `Orbiting` to a missing vessel
- non-scene active crash finalization consuming active cache before the destroyed fallback

The gameplay checklist in [`dev/manual-testing/test-recording-finalization-cache.md`](dev/manual-testing/test-recording-finalization-cache.md) covers atmospheric booster unload/delete, stable background orbiters, scene exit mid-burn, planned maneuver-node ignoring, focused crash, log sweeps, and cadence calibration.

## Code Layout

The completed phase plan is archived in [`dev/done/plans/recording-finalization-reliability.md`](dev/done/plans/recording-finalization-reliability.md). The landed code is split across these responsibilities:

1. `RecordingFinalizationCache` - in-memory identity, freshness, terminal, and predicted-tail payload.
2. `RecordingFinalizationCacheProducer` - active/background refresh cadence, stable-orbit cache production, atmospheric deletion cache production, and digest/no-op handling.
3. `RecordingFinalizationCacheApplier` - pure apply/reject logic, predicted-tail trimming, terminal metadata stamping, and dirty-sidecar marking.
4. `FlightRecorder` and `BackgroundRecorder` - cache ownership, refresh producers, active/background transfer, and background end consumers.
5. `ParsekFlight` and `IncompleteBallisticSceneExitFinalizer` - fresh-live precedence, scene-exit fallback consumption, crash/deferred-destruction consumption, and maneuver-node-safe finalization.

## References

- [`dev/done/plans/incomplete-ballistic-extrapolation.md`](dev/done/plans/incomplete-ballistic-extrapolation.md) - shipped scene-exit predicted-tail plan and current extrapolator context.
- [`dev/done/plans/recording-finalization-reliability.md`](dev/done/plans/recording-finalization-reliability.md) - completed implementation phase plan.
- [`dev/manual-testing/test-recording-finalization-cache.md`](dev/manual-testing/test-recording-finalization-cache.md) - runtime/manual validation checklist.
- [`dev/development-workflow.md`](dev/development-workflow.md) - design and plan/build/review workflow.
- `docs/handoff/abandoned-stage-replay/` (umbrella root, outside git) - the 2026-11-18 review "Simulated flight after switching away" and the ruling log behind edge cases 8-10, 16 and 26-38; the todo entry ABANDONED-STAGE-REPLAY carries the ruling table.
- [kOS Vessel Load Distance docs](https://ksp-kos.github.io/KOS_DOC/structures/misc/loaddistance.html) - describes loaded/unloaded and packed/on-rails behavior in KSP.
- [KSP forum discussion: 25km auto delete](https://forum.kerbalspaceprogram.com/topic/164961-25km-auto-delete/) - community documentation of stock atmospheric auto-delete rules.
- [KSP forum discussion: unloaded ships do not aerobrake](https://forum.kerbalspaceprogram.com/topic/107315-unloaded-ships-dont-aerobrake/) - community discussion of unloaded vessels not receiving normal aerobraking.
- [KSP Wiki: Atmosphere](https://kerbalspaceprogram.fandom.com/wiki/Atmosphere) - body atmosphere overview and drag context.
