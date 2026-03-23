# Changelog

All notable changes to Parsek are documented here.

---

## 0.5.2

### New Features

- **Real Spawn Control window** — proximity-based UI for warping to when nearby ghost craft become real vessels. Detects ghosts within 500m whose recording ends in the future. Per-craft Warp button, sortable columns (Craft, Dist, Spawns at, In T-), and "Warp to Next Spawn" quick-jump button.
- **Countdown column in Recordings Manager** — shows `T-Xd Xh Xm Xs` until each recording's vessel spawns. Updates live during playback, shows `-` when past.
- **Screen notification** when a new ghost craft enters spawn proximity range.
- **Toggle button in main window** — "Real Spawn Control (N)" under the Recordings/Game Actions group, grayed out when no candidates are nearby.

### UI Improvements

- Bottom buttons (Warp, Close, etc.) pinned to window bottom in Actions, Recordings, and Spawn Control windows.
- Recordings window widened for better readability (1106 collapsed, 1324 expanded).

---

## 0.5.1

Spawn safety hardening, ghost visual improvements, booster/debris tree recording, flag planting, and Fast Forward redesign. 20 PRs merged, 24 bugs fixed.

### Spawn Safety & Reliability

- **Bounding box collision detection** — replaced proximity-offset heuristic with oriented bounding box overlap checks against all loaded vessels (active vessel, debris, EVA, flags excluded)
- **Spawn collision retry limit** — 150-frame (~2.5s) collision block limit for non-chain spawns; walkback exhaustion flag for chain-tip spawns; spawn abandoned with WARN after limit hit (#110)
- **Spawn-die-respawn prevention** — 3-cycle death counter with permanent abandon for vessels destroyed immediately after spawn (e.g., FLYING at sea level killed by on-rails aero) (#110b)
- **Spawn abandon flag** — `SpawnAbandoned` prevents vessel-gone reset cycle from re-triggering spawn indefinitely
- **Non-leaf spawn suppression** — non-leaf tree recordings and FLYING/SUB_ORBITAL snapshot situations blocked from spawning; crew stripped from Destroyed-terminal-state spawn snapshots (#114)
- **SubOrbital terminal spawn suppression** — recordings with SubOrbital terminal state no longer attempt vessel spawn (#45)
- **Debris spawn suppression** — debris recordings (`IsDebris=true`) blocked from spawning real vessels
- **Orphaned vessel cleanup** — spawned vessels stripped from FLIGHTSTATE on revert and rewind; guards preserve already-set cleanup data on second rewind (#109)
- **ForceSpawnNewVessel on tree merge** — tree recordings correctly set ForceSpawnNewVessel during merge dialog callback, preventing PID dedup from skipping spawn after revert (#120)
- **ForceSpawnNewVessel on flight entry** — all same-PID committed recordings marked at flight entry for standalone recordings
- **Terminal state protection** — recovered/destroyed terminal state no longer corrupts committed recordings (#94)
- **Save stale data leak** — `initialLoadDone` reset on main menu transition prevents old recordings leaking into new saves with the same name (#98)

### Recording Improvements

- **Booster/debris tree recording** — `PromoteToTreeForBreakup` auto-promotes standalone recordings to trees on staging; creates root, continuation, and debris child recordings with 60s debris TTL. Continuation seeded with post-breakup points from root recording (#106 watch camera fix)
- **Controlled child recording** — `ProcessBreakupEvent` now creates child recordings for controlled children (vessels with probe cores surviving breakup), not just debris. Added to BackgroundRecorder with no TTL. Fixes RELATIVE anchor availability during playback (#61)
- **Flag planting recording/playback** — flag planting captured via `afterFlagPlanted`, stored as `FlagEvent` with position/rotation/flagUrl. Ghost flags built from stock flagPole prefab. Flags spawn as real vessels at playback end with world-space distance dedup
- **Auto-record from LANDED** — recording now triggers from LANDED state (not just PRELAUNCH) with 5-second settle timer to filter physics bounces, enabling save-loaded pad vessels and Mun takeoffs
- **Settle timer seed on vessel switch** — `lastLandedUT` seeded in `OnVesselSwitchComplete` for already-landed vessels, fixing auto-record for spawned vessels (#111)
- **Terminal engine/RCS events** — synthetic EngineShutdown, RCSStopped, and RoboticMotionStopped events emitted at recording stop for all active entries, preventing ghost plumes from persisting past recording end (#108)
- **Localization resolution** — `#autoLOC` keys resolved to human-readable names in vessel names and group headers via `Localizer.Format()` (#103)
- **Group name dedup** — multiple launches of same craft get unique group names: "Flea (2)", "Flea (3)" etc. (#104)
- **Chain boundary fix** — boundary splits skip standalone chain commits during tree mode, preventing nested groups in UI (#87)

### Ghost Visual Improvements

- **Compound part visuals** — fuel lines and struts render correctly on ghosts via PARTDATA/CModuleLinkedMesh fixup
- **Plume bubble fix** — ghost plume bubble artifacts eliminated by using KSP-native `KSPParticleEmitter.emit` via reflection instead of Unity emission module (#105)
- **Smoke trail fix** — Unity emission only disabled on FX objects that have KSPParticleEmitter; objects without it (smoke trails) keep their emission intact
- **Engine plume persistence** — `ModelMultiParticlePersistFX`/`ModelParticleFX` kept alive on ghosts for native KSP plume visuals (stripping them killed smoke trails)
- **Fairing cap** — `GenerateFairingConeMesh` generates flat disc cap when top XSECTION has non-zero radius (#85)
- **Fairing internal structure** — prefab Cap/Truss meshes permanently hidden; internal structure revealed only on `FairingJettisoned` event (#91)
- **Heat material fallback** — fallback path only clones materials that are tracked in `materialStates`, preventing red tint on non-heat parts (#86)
- **Surface ghost slide fix** — orbit segments skipped for LANDED/SPLASHED/PRELAUNCH vessels; `IsSurfaceAtUT` suppresses orbit interpolation for surface TrackSections; SMA < 90% body radius rejected (#93)
- **Terrain clamp** — ghost positions clamped above terrain in LateUpdate, preventing underground ghosts regardless of interpolation source
- **RELATIVE anchor fallback** — ghosts freeze at last known position instead of hiding when RELATIVE section anchor vessel is missing
- **Part events in Visual zone** — structural part events (fairing jettison, staging, destruction) now applied in the Visual zone (2.3-120km), not just Physics zone

### UI Improvements

- **Real Spawn Control window** — proximity-based UI showing ghosts within 500m whose recording ends in the future. Per-craft Warp button, sortable columns (Craft, Dist, Spawns at, In T-), and "Warp to Next Spawn" quick-jump button
- **Countdown column** — `T-Xd Xh Xm Xs` countdown in Recordings Manager, updates live during playback
- **Screen notification** when ghost craft enters spawn proximity range (10-second duration)
- **Toggle button** — "Real Spawn Control (N)" in main window, grayed out when no candidates nearby
- **Fast Forward redesign** — FF button performs instant UT jump forward (like time warp) instead of loading a quicksave; uses reflection for `BaseConverter.lastUpdateTime` to prevent burst resource production
- **Pinned bottom buttons** — Warp, Close, and action buttons pinned to window bottom in Actions, Recordings, and Spawn Control windows
- **Recordings window widened** — 1106 collapsed, 1324 expanded for better readability
- **Spawn abandon status** — spawn warnings show "walkback exhausted" / "spawn abandoned" status instead of silently retrying
- **Watch exit key** — changed from Backspace (conflicts with KSP Abort action group) to `[` or `]` bracket keys (#124)
- **Watch button guards** — disabled for out-of-range ghosts (tooltip: "Ghost is beyond visual range") and past recordings (#89, #90)
- **Watch overlay repositioned** — moved to left half of screen to avoid altimeter overlap

### Performance & Logging

- **CanRewind/CanFastForward log spam removed** — per-frame VERBOSE logs eliminated (was 578K lines/session, 94% of all output) (#117)
- **Main menu hook warning downgraded** — "Failed to register main menu hook" from WARN to VERBOSE (#118)
- **Spawn collision log demotion** — per-frame overlap log from Info to VerboseRateLimited (was ~24K lines/session)
- **GC allocation reduction** — per-frame allocations reduced in spawn UI via cached vessel names and eliminated redundant scans
- **Ghost FX audit** — systematic review of KSP-native component usage on ghosts; `KSPParticleEmitter` kept alive with `emit` control, `SmokeTrailControl` stripped (sets alpha to 0 on ghosts), `FXPrefab` stripped (pollutes FloatingOrigin), engine heat/RCS glow reimplementations retained (#113)
- **ParsekLog thread safety** — test overrides made thread-static to prevent cross-test pollution (#47)

### Bug Fixes

- **#45**: SubOrbital terminal state recordings no longer attempt vessel spawn
- **#47**: ParsekLog test overrides made thread-static
- **#85**: Fairing nosecone cap added to generated cone mesh
- **#86**: Heat material fallback only clones tracked materials, preventing red tint
- **#87**: Chain boundary commits no longer create nested groups in tree mode
- **#89**: Watch button disabled for ghosts beyond visual range
- **#90**: Watch button disabled for past (finished) recordings
- **#91**: Fairing internal structure hidden on ghost, revealed on jettison
- **#92**: Zone rendering tests updated for Visual-zone part events
- **#93**: Surface ghost slide fixed via orbit segment skip and SMA sanity check
- **#94**: Recovered/destroyed terminal state no longer corrupts committed recordings
- **#98**: Save data leak on same-name save recreation fixed via main menu reset
- **#103**: Localization keys resolved in vessel names and group headers
- **#104**: Multiple launches of same craft get unique group names
- **#105**: Ghost plume bubble artifacts fixed by using KSP-native emission
- **#106**: Watch camera booster fix via continuation point seeding
- **#108**: Synthetic shutdown events emitted at recording stop for all active engines/RCS
- **#109**: Spawned vessel cleanup preserved on second rewind
- **#110**: Spawn collision retry limited to 150 frames with abandon
- **#110b**: Spawn-die-respawn infinite loop stopped after 3 death cycles
- **#111**: Auto-record settle timer seeded on vessel switch
- **#114**: Non-leaf and FLYING/SUB_ORBITAL recordings blocked from spawning; crew stripped from destroyed-vessel snapshots
- **#119**: Watched ghosts exempt from zone distance hiding
- **#120**: Tree recordings set ForceSpawnNewVessel on merge
- **#61**: Controlled children now recorded after breakup (was "deferred to Phase 2")
- **#124**: Watch exit key changed from Backspace to brackets (Abort action group conflict)

---

## 0.5.0

Recording system redesign: multi-vessel sessions, ghost chain paradox prevention, spawn safety, time jump, and rendering zones. Recording format v5 → v7 (backward compatible).

### Recording System Redesign

- **Segment boundary rule** — only physical structural separation creates new segments. Controller changes, part destruction without splitting, and crew transfers are recorded as SegmentEvents within a continuing segment.
- **Crash coalescing** — rapid split events grouped into single BREAKUP BranchPoints via 0.5s window.
- **Environment taxonomy** — 5-state classification (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary) with hysteresis (1s thrust, 3s surface speed, 0.5s surface/atmospheric bounce).
- **TrackSections** — typed trajectory chunks tagged with environment, reference frame, and data source.
- **Reference frames** — ABSOLUTE for physics, ORBITAL_CHECKPOINT for on-rails, RELATIVE for anchor-vessel proximity.

### Multi-Vessel Sessions

- **Background vessel recording** — all vessels in the physics bubble sampled at proximity-based rates (<200m: 5Hz, 200m-1km: 2Hz, 1-2.3km: 0.5Hz) with full part event capture.
- **Background vessel split detection** — creates tree BranchPoints + child recordings for all new vessels from separations. Debris children get 30s TTL.
- **Debris split detection** — `onPartDeCoupleNewVesselComplete` catches booster/debris vessels synchronously at decouple time. Debris trajectory recording planned for v0.5.1.
- **Highest-fidelity-wins merge** — overlapping Active/Background/Checkpoint TrackSections merged per vessel with snap-switch at boundaries.
- **Per-vessel merge dialog** — extended dialog shows per-vessel persist/ghost-only decisions.

### Relative Frames & Anchoring

- **Anchor detection** — nearest in-flight vessel with 2300m entry / 2500m exit hysteresis. Landed/splashed vessels excluded (not loaded during playback far from surface).
- **Relative recording** — offsets stored as dx/dy/dz from anchor vessel for pixel-perfect docking playback.
- **Relative playback** — ghost positioned at anchor's current world position + stored offset, FloatingOrigin-safe.
- **Loop phase tracking** — preserves phase across anchor vessel load/unload via pure arithmetic.

### Ghost Chain Paradox Prevention

- **Ghost chain model** — committed recordings that interact with pre-existing vessels (docking, undocking, etc.) cause those vessels to become ghosts from rewind until the chain tip resolves.
- **Chain walker algorithm** — scans all committed trees for vessel-claiming events, builds ordered chains, resolves cross-tree links.
- **Intermediate spawn suppression** — multi-link chains (bare-S → S+A → S+A+B) only spawn at the tip.
- **Ghost conversion** — real vessels despawned and replaced with ghost GameObjects during chain windows.
- **PID preservation** — chain-tip spawns preserve the original vessel's persistentId for cross-tree chain linking.
- **Ghosting trigger taxonomy** — structural events, orbital changes, and part state changes trigger ghosting; cosmetic events (lights) do not.

### Spawn Safety

- **Bounding box collision detection** — spawn blocked when overlapping with loaded vessels (active vessel, debris, EVA, flags excluded).
- **Ghost extension** — ghost continues on propagated orbit/surface past recording end while spawn is blocked.
- **Trajectory walkback** — for immovable blockers, walks backward along recorded trajectory to find a valid spawn position.
- **Terrain correction** — surface spawns adjusted for terrain height changes between recording and playback.

### Time Jump

- **Relative-state time jump** — discrete UT skip that advances the game clock while keeping the physics bubble frozen in place, preserving rendezvous geometry across ghost chain windows.
- **Epoch-shifted orbits** — orbital elements recomputed at the new UT from captured state vectors for Keplerian consistency.
- **TIME_JUMP SegmentEvent** — records the discontinuity for playback handling.

### Ghost World Presence

- **CommNet relay** — ghost vessels register as CommNet nodes with antenna specs from ModuleDataTransmitter, maintaining communication network coverage during ghost windows.
- **Ghost labels** — floating text labels showing vessel name, ghost status, and chain tip UT.
- **Map view / tracking station** — infrastructure stubs for ghost orbit lines and nav targets (full KSP integration pending).

### Rendering & Performance

- **Distance-based zones** — Physics (<2.3km, full fidelity), Visual (2.3-120km, mesh only), Beyond (120km+, no mesh).
- **Zone-aware playback** — per-ghost distance computation, zone transition detection, part events gated to Physics zone.
- **Ghost soft caps** — configurable thresholds with priority-based despawning (LoopedOldest first, FullTimeline kept longest). Disabled by default until profiled.
- **Settings UI** — three slider controls for cap thresholds with enable toggle and live apply.
- **Log spam mitigation** — rate-limited high-volume diagnostics (SoftCap, zone, heat, engine FX).

### Bug Fixes (70 tracked, 48 fixed)

- **#51**: Chain ID lost on vessel-switch auto-stop — proper segment commit and chain termination
- **#52**: CanRewind log spam (485K lines) — verbose removed from success path
- **#53**: Re-show log spam (16K lines) — deduplicated via loggedReshow HashSet
- **#54**: Watch mode beyond terrain range — 2s grace period then auto-exit
- **#55**: RELATIVE anchor on debris — vessel type filtering + surface skip
- **#9**: Zero-frame TrackSections from brief RELATIVE flickers — discarded
- Active TrackSections not flushed to tree recordings — FlushRecorderToTreeRecording, CreateSplitBranch, CreateMergeBranch now copy TrackSections
- Watch mode camera re-targeting — deferred spawn no longer switches camera to spawned vessel after watch mode ends at recording boundary
- Rewind save propagation fixed across tree/EVA/split paths
- Soft cap spawn-despawn loop — suppression set prevents re-spawn after cap despawn
- Zone hide vs warp re-show loop — check currentZone before re-showing
- False RELATIVE anchor at launchpad — skip anchor detection on surface
- Watch mode on beyond-range looped ghost — loop phase offset reset
- Background split children capture vessel snapshots for ghost playback
- See `docs/dev/todo-and-known-bugs.md` for full list

### Format Changes

- Recording format v5 → v7 (additive, backward compatible)
- v6: SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types (Launch, Breakup, Terminal)
- v7: TerrainHeightAtEnd for surface spawn terrain correction
- Old recordings (v5) play back unchanged using legacy flat Points path

### Test Coverage

2994 tests (up from 1748 in v0.4.3).

---

## 0.4.3

Code refactor + UI improvements.

### Code Refactor

- ~73 methods extracted across 38 source files
- 6 new focused files: GhostTypes.cs, Recording.cs, GhostPlaybackState.cs, GhostPlaybackLogic.cs, PartStateSeeder.cs, GhostBuildResult
- ParsekFlight.cs reduced from 8,225 to ~7,000 lines
- ParsekKSC/ParsekFlight coupling eliminated

### New Features

- Hide column replaces Delete in Recordings Manager
- Group disband replaces group delete
- Context-aware rewind button (R for past, FF for future)
- Sandbox rewind fix

### Test Coverage

1748 tests (up from 1394 in v0.4.2).

---

## 0.4.2

### New Features

- Auto-loop with per-recording unit selector (sec/min/hr/auto)
- Recording groups with multi-membership and nesting
- KSC scene ghost playback with overlap support
- Time warp visual cutoffs and deferred spawn queue

### Visual Improvements

- 3-state heat model (cold/medium/hot)
- Heat shield ablation, smoke puff + spark FX on decouple/destroy
- FXModuleAnimateRCS, ModuleColorChanger, fairing ghost visuals
- EVA kerbal facing fix

### Bug Fixes

- #28-#44: 17 fixes including damaged wheel filtering, RCS debounce, FX activation, rate-limited log spam

---

## 0.4.1

### Bug Fixes

- #26: EVA crew swap after merging from KSC
- #40: Save contamination between saves
- #41: Watch camera stuck after loop explosion with time warp

### New Features

- Explosion visual effect on impact with camera hold
- Overlapping ghost support for negative loop intervals
- Loop interval editing per-recording
- KSC toolbar UI
- Short recording auto-discard (<10s AND <30m)

---

## 0.4.0

### New Features

- Orbital rotation fidelity — ghosts hold recorded SAS orientation during orbital playback
- PersistentRotation mod support — spinning vessels reproduced during ghost playback
- Camera recenters on ghost after separation events in Watch mode

### Bug Fixes

- #17: Re-entry FX too large — replaced with mesh-surface fire particles matching stock aeroFX
- #18: Engine nozzle glow persists after shutdown
- #19: Watch button broken for looped segments
- #21-#24: Ghost build spam, facility warnings, dead geometry stubs, variant warnings

### Test Coverage

1263 tests.

---

## 0.3.1

### Bug Fixes

- Auto-migrate v4 world-space rotation to v5 surface-relative
- Remove incorrect planetary spin correction
- Fix rewind save lost on atmosphere boundary false alarm
- Fix chain orphan on vessel destruction
- Fix EVA ghost showing capsule mesh

### Improvements

- Collapsible recording groups
- Recording format v5 (surface-relative rotation)

---

## 0.3.0

Initial public release.

- Position recording with adaptive sampling
- Ghost playback with opaque vessel replicas
- Rewind to any earlier timeline point
- Multi-vessel recording (undock, EVA, dock)
- Career mode: milestones, resource budgeting, action blocking
- 28 part event types replayed on ghosts
- Orbital recording with analytical Keplerian orbits
- Recordings Manager UI

**Dependencies:** Module Manager, HarmonyKSP, ClickThroughBlocker, ToolbarControl
