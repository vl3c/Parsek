# The four sites (R2) - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered:
`docs/dev/design-competitive-multiplayer.md` 7.3 in full, the site rows of section 12, 7.14
(science parity), 15.6 and 15.7 where they name the sites, section 8 cases 16, 16a, 22, 23,
24, 25, 26 and 31, prerefactor P1 (consumer side). Phase R2 ("the world of the race, still
without rivals").

## 1. Goal and scope

Built in this phase:

- The site table `kerbin-equator-1`: four fixed sites on Kerbin's equator at their natural
  ground height, with each site's pad and runway points, its launch areas and the placement
  numbers every game uses for that site. Shipped with the mod, pure data in `RaceCore`.
- `RaceSites` in `RaceWorld`: one process-lifetime owner of everything a race changes on
  Kerbin, with one apply and one restore that are each other's inverse:
  - three terrain patches (sites 1, 2, 3) in every race save, whoever plays it;
  - the working space center moved to the local player's home site (only when the home site
    is not site 0), with every stock cache of its position refreshed;
  - three scenery copies of the KSC (fully built, solid, inert) at the three other sites;
  - the home position Parsek's own rules read (`HomeSpaceCenter`, P1) set to the home site.
- One reconcile at every scene load request, called by R1-04's `ParsekRaceBoot` (no second
  addon): the wanted state (stock, or a race save's table and home slot) is compared with
  what is applied, and the difference is applied. A single-player save always finds stock
  Kerbin, whichever way it was reached.
- Launch rules: only the own space center and, when the race uses them, the stock alternate
  sites; a guard on the stock launch entry point and the matching stock control state.
- Science parity: the KSC area and building mini-biomes at the home site, none at a copy;
  a biome override only if spike S2 shows it is needed.
- The modified-Kerbin refusal (the Kerbin fingerprint), filling R1-10's refusal slot and
  checked at apply.
- In-game tests (one category per scene, hosted by T-29's lanes RACE-9, RACE-10 and RACE-11
  and by the R2 lanes) and three harness lanes, SITES-1, SITES-2 and SITES-3, that prove a
  launch, a recovery and a Space Center visit at each site, and stock Kerbin after leaving a
  race save.

Not in this phase:

- Anything about rivals: ghosts, leftovers, flags, the launch-area filter on what is shown
  (R3). R2 ships the launch-area rows of the table; R3W-12 ships the filter that reads them.
- The setup dialog, the race mark, slot assignment, the manifest (R1). R2 reads the mark.
- Looks (R5), the board (R4), the Space Race window (R4; it shows each site's height from the
  table R2 ships).
- A marker drawn at a rival's site (owner ruling: none), a Space Center scene for a rival's
  site (design 7.3: never).
- Moving the stock alternate launch sites, or adding launch sites of any kind.

## 2. Starts after

Prerefactors and additions (design 15.11):

- **P1-01** (`HomeSpaceCenter` in `Core`, no setter). R2-05 adds `Apply` / `RestoreStock`
  on it and is its first non-stock writer. Hard dependency for R2-05.
- **P1-02** (the shared launch-area shape test, `LaunchArea` / `LaunchAreaGeometry` in
  `Core`). The site table's launch areas are `LaunchArea` values (R2-01).
- **P1-03** (the launch-site circle cache keyed on `HomeSpaceCenter`'s version and dropped at
  the main menu). Hard dependency for R2-05.
- **P12-02** (the Controllers rule covers `ParsekRace*` files). R1-04's `ParsekRaceBoot.cs`,
  which R2-01 edits, relies on it.
- **P10-01** (create-and-start core) only indirectly, through R1-10's and R1-11's
  `RaceSetup` actions that the harness lanes use.
- **A7** (one "find a game's scenario node before its scenarios load" helper, R1-02 and
  R1-07), through R1-04's gate, which R2 calls while the next scene is not built yet.

R1 tasks:

- R1-01: the three module scaffolds `RaceCore` / `RaceWorld` / `RaceExport` with layers,
  headless and forbidden rules (every R2 file lands in one of them);
- R1-03: `RaceLink` (fields `siteSlot`, `siteTableId`, `alternateSites`);
- R1-04: `RaceGate`, including a read of the mark from a `Game` whose scenario modules have
  not loaded yet, and `ParsekRaceBoot`, the one process-lifetime addon whose
  `onGameSceneLoadRequested` handler calls `RaceSites.Reconcile` (R2-01);
- R1-10: the refusal slot in `RaceInstallCheck` (R2-02 fills it with the Kerbin
  fingerprint), the manifest's DIFFICULTY carrying `AllowOtherLaunchSites = alternateSites`
  (R2-08 relies on it) and the seam action `RaceSetup action=create`;
- R1-11: the seam action `RaceSetup action=join` (a save at slots 1 to 3).

Test and harness tasks:

- T-03: the `[fixture.race]` block whose `${raceShared}` (`<instance>/rs/<n>`) is the
  temporary shared folder the lanes create and join against;
- T-07: the seam read `ListHandles kind=sites` and the lane RACE-0-stock-sites-baseline that
  pins the stock values (SITES-1, SITES-2);
- T-29: the host lanes RACE-9 (SPACECENTER), RACE-10 (FLIGHT) and RACE-11 (TRACKSTATION),
  each one `RunTests` over a comma list of categories in a joined race save, which run R2's
  in-game categories `RaceSites`, `RaceSitesFlight` and `RaceSitesTrackingStation`.

Spikes (planned by another agent; their expected outcomes are what this plan assumes):

| Spike | Expected outcome this plan assumes | Tasks that change if it differs |
|-------|-------------------------------------|---------------------------------|
| S1b (finer scan) | Exact centre longitude for sites 1 to 3 at the KSC footprint latitude, each with the whole footprint on dry ground at the KSC orientation; heights about 135, 520, 676 m | R2-01 (table values and launch-area rows) |
| S2 (science) | Building mini-biomes and the KSC area are tag-driven and follow the moved objects; copies with retagged colliders give none | R2-09, R2-10, R2-15 |
| S4a (probe) | The reconcile point (scene load request) has the new `Game` in `HighLogic.CurrentGame`; the KSC origin and spawn points are measured; the P1-03 VERIFY says whether the runtime alternate-site read holds the KSC's own `LaunchPad` / `Runway` | R2-01 (and R1-04, the same hook; the pad and runway footprints for the launch-area rows), R2-05 (the cache answer) |
| S4b (move) | Moving the "KSC" `PQSCity` before the scene builds, then `SpaceCenter.Start()` and the facility spawn-point refresh, gives a working launch, recovery and Space Center at every site | R2-03, R2-04, R2-08, R2-11, R2-15 |
| S5 (scenery, terrain) | A KSC copy built from the max-level prefabs, stripped and retagged, is solid and inert; a cloned decal gives identical heights in two processes on one PC (a second machine is a cross-check, not a gate); three copies cost an acceptable load time and frame time | R2-02, R2-03, R2-06, R2-07, R2-15 |

## 3. What the code looks like today

### 3.1 Stock (decompiled `Assembly-CSharp.dll`, KSP 1.12.5, from `/home/user/ksp-refs`)

`ilspycmd` 8.2 ran with `DOTNET_ROLL_FORWARD=Major`; `ikdasm` gave the IL used for the
caller searches. Line numbers are in the decompiled text of each type (obfuscation noise
included), so they locate a statement, not a source line.

The working space center:

- `PSystemSetup.pqsTransformToCache = "KSC"` (PSystemSetup.cs:340); `SetupSystem` caches
  `scTransform = pqs.transform.Find("KSC")` (:724) and every scene setup sets the floating
  origin from it (`SetSpaceCentre`, :2386). The space center, its facilities, spawn points
  and the Space Center camera's start transform are all found under that one transform.
- `SpaceCenter.Start()` is public and caches `latitude`, `longitude` and `srfNVector` from
  its transform once (SpaceCenter.cs `Start`); the component lives for the process. Its
  `spaceCenterAreaTrigger` collider is switched off only in the Space Center scene
  (`OnGameSceneLoaded`).
- Stock methods that read that cache (IL caller search for `SpaceCenter::get_Latitude`,
  `get_Longitude`, `get_SrfNVector`, `GreatCircleDistance`, `Instance`):
  `VesselRecovery.OnVesselRecovered` (recovery value by distance, VesselRecovery.cs:49-63),
  `FinePrint.Contracts.SurveyContract.RandomizeNearKSC`, `KSP.UI.UIWarpToNextMorning.Setup`,
  `KSPAchievements.RecordsDistance.iterateVessels`, `CelestialBody.GetRandomLatitudeAndLongitude`,
  `MusicLogic`, `ConsoleCommands`. Every one reads it through `SpaceCenter.Instance`, so one
  `Start()` call after a move fixes all of them.
- `PQSCity.Orientate(bool)` (PQSCity.cs:735) places the city from `repositionRadial`,
  `repositionRadiusOffset` and, for the rotation,
  `FromToRotation(reorientInitialUp, radial) * AngleAxis(reorientFinalAngle, up)`. A city
  moved along the equator therefore turns about its own up axis by the longitude change
  unless `reorientFinalAngle` is corrected. `Orientate` also refreshes `lat` / `lon` and the
  spawn points of a linked facility, and fires `GameEvents.OnPQSCityOrientated`.
- `PSystemSetup.SpaceCenterFacility.SpawnPoint` caches `latitude` / `longitude` / `altitude`
  (`latlonaltSet`) in `SetSpawnPointLatLonAlt` (PSystemSetup.cs:83). Stock refreshes them
  only on a facility level change (`onUpgradeableObjLevelChange`, :634). The list is public
  (`SpaceCenterFacilityLaunchSites`, :367) and so is `SetSpawnPointsLatLonAlt` (:245).
- The launch itself reads the live transform: `FlightDriver` takes
  `spawnPoint2.GetSpawnPointTransform()` for the floating origin, the PQS target and the
  vessel (FlightDriver.cs:655-685). So a moved city launches from its new place.
- `SpaceCenterCamera2.Start` nests its pivot under
  `pqs.transform.Find(initialPositionTransformName)` (SpaceCenterCamera2.cs:155-177), fresh
  in each Space Center scene.
- The map's KSC marker (`KSCSiteNode.GetWorldPos`) returns the Runway facility's transform,
  so it follows the move.
- `CommNet.CommNetHome.CreateNode` caches `lat` / `lon` / `alt` from its transform
  (CommNetHome.cs, `CreateNode`), on network initialisation; whether the KSC home node sits
  under the "KSC" transform is not visible in code (VERIFY in S4b).

Terrain:

- The flattening patch is a `PQSMod_MapDecalTangent` (public `radius`, `position`, `angle`,
  `heightMap`, `heightMapDeformity`, `absolute`, `absoluteOffset`, `smoothHeight`,
  `removeScatter`, ...; PQSMod_MapDecalTangent.cs:1-103). `OnSetup` derives its rotation and
  inclusion angle from those fields.
- `PQS` keeps its mod list in a private array filled by `SetupMods` (PQS.cs:450, :4470),
  which collects child mods sorted by `order`. `SetupMods` runs from `StartSphere` on a first
  or forced start and from the internal `ResetModList` (:4625); `RebuildSphere` (:1357) is
  `ResetSphere` plus a non-forced `StartSphere`; `ForceStart` (:1363) forces it. Which call
  re-collects an added mod AND rebuilds the quads in each scene is what S4b / S5 must settle.

Building behaviour that a copy must not keep:

- `Upgradeables.UpgradeableObject` swaps level models: `UpgradeLevel.facilityPrefab` is
  instantiated at the facility transform's local pose (Upgradeables.UpgradeableObject.cs:39-60).
  A live clone carries the LOCAL player's current level; the max-level prefab is the one to
  use. `UpgradeableFacility.RegisterInstance` registers with
  `ScenarioUpgradeableFacilities` by id (Upgradeables.UpgradeableFacility.cs:61).
- `DestructibleBuilding` registers with `ScenarioDestructibles` by its string id in `Start`
  (DestructibleBuilding.cs:567, :774); two buildings with one id would share stock state.
- `SpaceCenterBuilding` is the click and context-menu host in the Space Center scene
  (SpaceCenterBuilding.cs:739-1078).
- Collider tags drive "where is this vessel": `Part.OnCollisionEnter` / `OnCollisionStay`
  call `vessel.SetLandedAt(c.gameObject.tag, ...)` for any collider not tagged
  `Untagged` (Part.cs:7787-7791, :9078-9085), and `Part.OnTriggerEnter` does the same for a
  trigger while landed (Part.cs:9488, :9561-9583). `Vessel.SetLandedAt` ignores `Untagged`
  (Vessel.cs:2293). `MiniBiome.ConvertTagtoLandedAt` maps tags containing Pad, LaunchPad,
  Runway, KSC and Grounds to landed-at names (MiniBiome.cs).
- Stock reads those names by substring: `ShipConstruction.FindVesselsLandedAt` (the launch
  site clear check, ShipConstruction.cs:1940-2015), `VesselRecovery` (full value on
  "LaunchPad" / "Runway", 98 % on "KSC", VesselRecovery.cs:84-109),
  `ModuleScienceExperiment` (biome = landed-at name, else the biome map,
  ModuleScienceExperiment.cs:2303-2320), `Vessel.LandedInKSC`, and the solar panel and
  ground science line-of-sight tests that ignore objects tagged "KSC"
  (`ModuleDeployableSolarPanel.CalculateTrackingLOS`, `ModuleGroundSciencePart`).
- The biome map path is `ScienceUtil.GetExperimentBiome` / `GetExperimentBiomeLocalized`
  (ScienceUtil.cs:118, :140), over `body.BiomeMap.GetAtt(lat, lon)`.

Launch sites and their rules:

- KSC pad and runway are `SpaceCenterFacility` entries; the Making History sites are
  `LaunchSite` entries in `PSystemSetup.LaunchSites` (:455), set up only when the expansion
  is installed (:516, :872).
- `GameParameters.DifficultyParams.AllowOtherLaunchSites` (GameParameters.cs:223) is what
  `EditorDriver.OtherLaunchSitesValid()` (EditorDriver.cs:1402) reads (:1528) to offer the
  other sites; `EditorDriver.ValidLaunchSite(string)` (:1148) checks a name.
- Every new launch passes `FlightDriver.StartWithNewLaunch(fullFilePath, missionFlagURL,
  launchSiteName, manifest)` (FlightDriver.cs:1093) (VERIFY in S4b that the Space Center
  picker path reaches it too).

Scene events:

- `HighLogic.SetLoadSceneEventsAndFlags` fires `GameEvents.onGameSceneLoadRequested(scene)`
  before Unity loads the scene (HighLogic.cs:793-795); `Game.Start()` calls
  `HighLogic.LoadScene(startScene)` (Game.cs:1752, :1857).

### 3.2 Parsek

Readers of the space center position or of launch-site names:

| Reader | Reads | After the move |
|--------|-------|----------------|
| `SpawnCollisionDetector` constants `KscPadLatitude/Longitude`, `KscRunwayLatitude/Longitude`, `DefaultKscExclusionRadiusMeters` (SpawnCollisionDetector.cs:216-232) | stock pad and runway threshold | wrong at sites 1-3; P1 routes them through `HomeSpaceCenter` |
| `SpawnCollisionDetector.IsWithinKscExclusionZone` (:240), `ClassifyKscExclusionZone` (:271), `DecideLaunchSiteEndOfFlightRetirement` (:349) | those constants | follows `HomeSpaceCenter` after P1 |
| `LaunchSiteExclusionZones.Classify` (LaunchSiteExclusionZones.cs:179), `GetAltSiteCircles` (:220), cache `cachedRuntimeCircles` (:130) | KSC via the detector, Making History sites from `PSystemSetup.LaunchSites` | the alternate sites do not move, but the read admits the names `LaunchPad` / `Runway` (:269-270), so the cache may hold the stock KSC's own pad and runway; S4a decides, and if so R2-05 filters or rebuilds them (OV-52); P1-03 keys the cache on `HomeSpaceCenter`'s version |
| `VesselSpawner.EvaluateKscEndOfFlightRetirement` (VesselSpawner.KscRetirement.cs:87, :127, :153) | the classifier above | follows `HomeSpaceCenter` |
| `LaunchSiteOnlyGhost` hidden set (LaunchSiteOnlyGhost.cs:36-39, :274) | classifier, cached against `RecordingStore.StateVersion` and the tree count | stale across a move unless the key also holds `HomeSpaceCenter`'s version; R2-05 adds it (OV-53) |
| `FlightRecorder.ResolveLandedAtLaunchSite` (FlightRecorder.StartRecording.cs:336-342) and `CaptureStartLocation` (:247-272) | the live `landedAt` name, then `FlightDriver.LaunchSiteName` (:364) | stock names at the own site; a copy with retagged colliders gives no name |
| `RecordingOptimizer.IsLaunchSiteStart` (RecordingOptimizer.cs:648) and `IsLaunchSiteDepartureBoundary` (:724) | `LaunchSiteName` and `StartSituation` only, no position | no change |
| `RouteAnalysisEngine.IsKscOriginRecording` (Logistics/RouteAnalysisEngine.cs:1453) | a non-empty `LaunchSiteName` on Kerbin | a copy must never give a launch-site name, or a route from a rival's pad reads as a KSC origin |
| `ParsekKSC.IsGhostInCullRange` (ParsekKSC.cs:1779) and the KSC-scene playback (ParsekKSC.Playback.cs) | distance to the scene camera; recorded positions | follows the moved camera; no position constant |
| `GameStateFacilityRecorder` (GameStateFacilityRecorder.cs:38, :413), `GameStateBaseline` (GameStateBaseline.cs:128), `FacilityStatePatcher.PatchDestructionState` (GameActions/FacilityStatePatcher.cs:452) | `FindObjectsOfType<DestructibleBuilding>()` | a copy that kept the component would add buildings with the stock ids |
| `StockUiFacilityDecoration.HostOf` (StockUiFacilityDecoration.cs:263) | the `SpaceCenterBuilding` of a stock context menu | copies hold none, so no menu opens on them |
| `LiveVesselSnapshot.TryResolveBiome` (LiveVesselSnapshot.cs:720) | biome map at a lat / lon | the home site's own map biome; accepted as "the neighbourhood" (design 7.3) |
| in-game test `RuntimeTests` (InGameTests/RuntimeTests.cs:16444) | `IsWithinKscExclusionZone` | test only |

Hooks and patterns R2 reuses:

- `ParsekScenario.OnMainMenuTransition` (ParsekScenario.SceneTransitions.cs:247) is the main
  menu reset, subscribed as an INSTANCE method because a static `EventData` handler throws
  (:221-228). R1-04's `ParsekRaceBoot`, which calls R2's reconcile, follows the same rule.
- `LaunchSiteExclusionZones.IsMakingHistoryInstalled` (LaunchSiteExclusionZones.cs:99) is
  the expansion check the launch rules reuse.
- Harness: `SWS-1-save-switch-through-main-menu` (harness/scenarios/) already switches saves
  through the main menu in one process; `MC-4-making-history-desert` launches from an
  alternate site on `stock-minimal`; seam verbs `NewGame`, `LoadGame`, `GoToEditor`,
  `LaunchFromEditor`, `LaunchSiteProbe`, `Recover`, `ExitToSpaceCenter`, `RevertToLaunch`,
  `RunTests` exist (TestCommands/TestCommandVerbs.cs).

Module placement today: `SpawnCollisionDetector.cs` is Ghost (scripts/arch/modules.toml:96),
`LaunchSiteExclusionZones.cs` is Controllers (:174), `LaunchSiteOnlyGhost.cs` is Ghost (:180),
`ParsekScenario*` is Controllers (:322). `RaceWorld` (features) therefore must not call
`LaunchSiteExclusionZones` (a backward edge into entry); it only sets `HomeSpaceCenter` (Core)
and the Parsek readers follow that value.

### 3.3 How the known mods do it (from memory, every point to verify)

- **KSCSwitcher**: finds Kerbin's PQS, the `PQSCity` named "KSC" and the
  `PQSMod_MapDecalTangent` named "KSC"; per site it sets the city's `repositionRadial`,
  `repositionRadiusOffset` and `reorientFinalAngle` and the decal's `position`, `angle`,
  `absoluteOffset` and radius, rebuilds the sphere, re-runs `SpaceCenter.Start` and resets
  the Space Center camera. It keeps the chosen site per save in a scenario module and
  re-applies on load: the same per-save, per-process shape as `RaceSites`.
- **Kopernicus**: edits the same "KSC" city and decal from config while the planetary
  system is built, once per process; it does not undo, so a game with Kopernicus is
  "Kerbin modified" for the race.
- **Kerbal Konstructs**: places static objects as children of the body's PQS with a
  `PQSCity` each for placement and LOD, and registers launch sites with
  `PSystemSetup.AddLaunchSite`. Its stock-building statics are built from the
  `UpgradeLevel.facilityPrefab` objects of the stock facilities with the facility and
  destruction components removed: the same recipe R2-06 uses for a copy.

## 4. Tasks

### 4.0 The shape the tasks build

Files (all new unless stated):

| File | Module | Holds |
|------|--------|-------|
| `RaceCore/SiteTable.cs` | RaceCore | the table: `SiteRow` (slot, centre lat / lon, ground height, pad point, runway threshold and end, strip half-width, launch areas as P1-02 `LaunchArea` values) and `SiteTable.Get(siteTableId)`; derived values `HomePointsFor(slot)` (added by R2-05) and `LongitudeShift(slot)` (added by R2-04), each with its first production caller; `SiteRow.LaunchAreas` is a readonly field, read through `LaunchAreaGeometry.Contains`; no separate launch-area file (R3W-12 reads the rows, OV-41) |
| `RaceCore/SitePlacementMath.cs` | RaceCore | pure placement math over doubles: the radial for a lat / lon and the decal offset for a height (R2-03), the city offset and the `reorientFinalAngle` correction for a longitude shift (R2-04) |
| `RaceCore/RaceSitesPlan.cs` | RaceCore | `SitesState` (Stock, or table id + home slot) and the pure reconcile decision `Decide(applied, wanted)` |
| `RaceCore/RaceSitesSequence.cs` | RaceCore | the ordered apply and restore steps over a port `IRaceSitesWorld`, with rollback on a failed step |
| `RaceCore/KerbinFingerprint.cs` | RaceCore | expected stock Kerbin values and the pure comparison |
| `RaceCore/SceneryStripPolicy.cs` | RaceCore | pure keep / remove / retag decision per component type name, tag and trigger flag |
| `RaceCore/RaceLaunchRules.cs` | RaceCore | pure "may this game launch from site name X" |
| `RaceWorld/RaceSites.cs` | RaceWorld | the live owner: applied state, stock baseline, `Reconcile(wanted, reason)`, the `IRaceSitesWorld` adapter (partials below) |
| `RaceWorld/RaceSites.Terrain.cs` | RaceWorld | add / remove the three decals, re-collect mods, rebuild |
| `RaceWorld/RaceSites.Move.cs` | RaceWorld | move / restore the "KSC" city, refresh the stock caches |
| `RaceWorld/RaceScenery.cs` | RaceWorld | build / destroy one KSC copy |
| `RaceWorld/KerbinProbe.cs` | RaceWorld | the live reads behind the fingerprint |
| `ParsekRaceBoot.cs` (R1-04's, edited) | Controllers | R1-04's one process-lifetime addon; its handler on `onGameSceneLoadRequested` calls the hook `OnSceneRequest(RaceGateDecision, GameScenes)` on every request (the main menu included); R2-01 fills that hook: the wanted state from the gate decision, then `RaceSites.Reconcile` (OV-51; no second addon); it also hosts R2-07's fallback scene-ready tick |
| `Patches/RaceLaunchGuardPatch.cs` | Patches | prefix on `FlightDriver.StartWithNewLaunch`, postfix on `EditorDriver.OtherLaunchSitesValid` |
| `InGameTests/RaceSitesTests.<Area>.cs` | InGameTests | one category per scene (OV-09): `RaceSites` (SPACECENTER, race save), `RaceSitesFlight` (FLIGHT), `RaceSitesTrackingStation` (TRACKSTATION), `RaceSitesEditor` (EDITOR), `RaceSitesStock` (SPACECENTER, single-player save after a race) |

`RaceSites` state, all process lifetime (design 15.7, "the one piece of state that outlives
a save"):

- `applied` (`SitesState`): Stock, or (`siteTableId`, `homeSlot`). Written only inside
  `RaceSites` methods.
- `baseline` (`StockSiteBaseline`): the stock city's placement fields, the stock decal's
  fields, the facility spawn-point values and `SpaceCenter.Latitude/Longitude`, captured on
  the first apply of the process while Kerbin is stock, re-checked on every restore.
- `decals` (slot -> GameObject) and `copies` (slot -> GameObject): what apply added, so
  restore removes exactly that. Named `ParsekRaceSiteDecal<slot>` / `ParsekRaceSiteCopy<slot>`
  so tests and the fingerprint can find them.
- `lastFailure` (string): why the last apply failed, for the log and the tests.

When it applies and removes: R1-04's `ParsekRaceBoot.OnSceneRequest` calls `Reconcile` on
every `onGameSceneLoadRequested` request (the main menu included), which stock
fires before it loads the next scene (HighLogic.cs:795). The wanted state is Stock when the
next scene is the main menu or `HighLogic.CurrentGame` carries no race mark, and
(`siteTableId`, `siteSlot`) from `RaceGate` otherwise. `RaceSitesPlan.Decide` gives one of:
`Nothing`, `Apply(slot)`, `Restore`, `RestoreThenApply(slot)`, `Refuse(reason)`. So:

- a race save's first scene, created or loaded: Apply;
- every scene change inside one race save (flight, quickload, revert, rewind, Re-Fly): Nothing;
- main menu: Restore;
- a single-player save reached from a race save by ANY route (main menu, the in-game load,
  a harness `LoadGame`): Restore before its first scene builds;
- another race save with another home slot: RestoreThenApply (case 31).

In a single-player session with nothing applied the call returns after one comparison
and logs nothing.

Logging (tag `RaceSites`, design section 10):

- Info `Sites applied: table=<id> home=<slot> lat=<f4> lon=<f4> height=<f1> decals=<n>
  copies=<n> moved=<bool> ms=<n>`;
- Info `Sites restored to stock: reason=<main-menu|single-player-save|other-race-save>
  decals=<n> copies=<n> ms=<n>`;
- Info `Reconcile: scene=<s> applied=<state> wanted=<state> action=<action>` (only when
  either state is not Stock);
- Info per copy `Scenery copy built: slot=<n> objects=<n> removedComponents=<n>
  retaggedColliders=<n> removedTriggers=<n> levelPrefabs=<n>` (at most three per apply);
- Warn `Sites apply failed at step=<step>: <reason>; restored to stock` and
  `Kerbin is modified: <reason>`; Warn `Stock baseline drift: <field>` on a restore that
  finds stock values changed;
- Verbose for each step of the sequence.

### 4.1 Task list

#### R2-01 Site table and the reconcile decision, wired log-only

- **Change:** `RaceCore/SiteTable.cs` with table `kerbin-equator-1`: site 0 from the stock
  numbers (pad -0.0972 / -74.5575, runway threshold -0.0502 / -74.7300 as in
  SpawnCollisionDetector.cs:216-225, footprint centre -0.074 / -74.64, ground 66 m); sites 1
  to 3 PROVISIONAL from the survey (106.7 E / 135 m, 18.8 E / 520 m, 162.3 W / 676 m) until
  S1b. Pad, runway threshold and end of site k are site 0's points shifted by the centre
  longitude difference (same latitude, same orientation); launch areas per row as P1-02
  `LaunchArea` values (pad `LaunchArea.Circle` 100 m, runway `LaunchArea.Strip` plus 50 m;
  design 7.3, 13.C.7), the only race launch-area definition: R3W-12 tests a point against a
  row's areas with P1-02's `LaunchAreaGeometry.Contains` (OV-41; no `RaceLaunchAreas.cs`).
  Unknown table id answers null. `RaceCore/RaceSitesPlan.cs` with `SitesState` and
  `Decide`. `RaceWorld/RaceSites.cs` with `Reconcile(wanted, sceneName)` that in this task
  only logs the decision. R2-01 fills R1-04's empty hook
  `ParsekRaceBoot.OnSceneRequest(RaceGateDecision, GameScenes)`, which runs on every
  `onGameSceneLoadRequested` request (the main menu included): it builds the wanted state
  from the gate decision and calls `RaceSites.Reconcile` (OV-51; no second addon).
  Placement rules for the new files. Uncalled-method ratchet (`docs/dev/arch/README.md`
  :509-512): every member lands with a production caller. `SiteTable.Get`, `Decide` and
  `Reconcile` are reached from `OnSceneRequest`; the table itself is built in `SiteTable`'s
  static initializer (a root), so this task adds the `LaunchArea.Strip` factory to P1-02's
  `LaunchArea.cs` together with `SiteRow.LaunchAreas` (P1-02 does not create it); if the
  owner says no to P1-02's longitude wrap, `LaunchArea` itself (constructor, `Circle`,
  `Strip`) lands in this task's PR and `LaunchAreaGeometry` with R3W-12, so the
  `SiteTableTests` containment cells then move to R3W-12; `SiteRow.LaunchAreas` is a
  readonly field (no method); `HomePointsFor` lands with R2-05 and `LongitudeShift` with
  R2-04, their first callers.
- **Proof:** xUnit `SiteTableTests` (four rows, slots 0-3, site 0 equal to the stock
  constants, derived points of site k at the same latitude and shifted longitude, longitude
  wrap at 162.3 W, unknown id null, every launch area contains its own pad and runway
  through `LaunchAreaGeometry.Contains`); `RaceSitesPlanTests` (truth table over Stock /
  race(slot) pairs, including same save, other slot, unknown table -> `Refuse`);
  log-capture test of the `Reconcile:` line. Live: one `Reconcile:` line per scene change
  in a race save, none in a single-player save.
- **Registers:** nothing new in `scripts/arch/modules.toml` (RaceCore / RaceWorld folders
  are R1-01's, `ParsekRaceBoot.cs` is placed by P12-02's rule in R1-04); headless pins for
  the RaceCore files; `scripts/arch/log_tags.json` (`RaceSites` -> RaceWorld) via
  `logtags.py --write`; facts pins if a method crosses the complexity watch band.
- **Size:** M. **Depends on:** R1-01, R1-03, R1-04, P12-02, P1-02.
  **Spike:** S1b changes the three rows' numbers and launch-area rows only (S4a measures
  the footprints); S4a changes the hook if the new
  `Game` is not yet in `HighLogic.CurrentGame` at the scene request (then R1-04 moves the
  one handler, gate and reconcile together, for example to a prefix on `Game.Start`).
- **PC:** yes, one look at the log in a race save and a single-player save.
- **Risk:** low; log-only.

#### R2-02 Kerbin fingerprint and the modified-Kerbin refusal

- **Change:** `RaceCore/KerbinFingerprint.cs`: the expected stock values (radius 600000 m,
  rotation period, gravitational parameter, the "KSC" city and "KSC" decal present with their
  stock placement numbers, terrain heights at eight sample points away from every site and
  alternate site, each within a tolerance) and `Evaluate(observed) -> (ok, reason)`; a planet
  pack assembly (Kopernicus, KSCSwitcher) loaded is a reason too. `RaceWorld/KerbinProbe.cs`
  reads the observed values (`FlightGlobals.GetHomeBody()`, `pqsController.GetSurfaceHeight`,
  `AssemblyLoader.loadedAssemblies`), Unity reads kept in `NoInlining` cores (CLAUDE.md, mono).
  `RaceSites.CheckKerbinStock(out reason)` fills the empty Kerbin slot of R1-10's
  `RaceInstallCheck` refusal list (create and join; design 7.2, case 24): R2-02 owns the
  whole fingerprint, radius and rotation period included, and R1-10 keeps only the slot
  (OV-31); `Reconcile` returns `Refuse` before any change when it fails. Ratchet: the slot
  call and `Reconcile` are the production callers of `CheckKerbinStock`,
  `KerbinFingerprint.Evaluate` and `KerbinProbe`, all in this PR.
- **Proof:** xUnit `KerbinFingerprintTests` (stock values pass; each changed value, a
  missing city or decal and a planet-pack assembly each fail with their own reason; culture
  invariant reason text). In-game test `RaceSitesStock.KerbinIsStock` (SPACECENTER of a
  single-player save, hosted by SITES-2) records the eight heights it reads, so the table of
  expected heights is taken from a real game once (S5 compares them across two processes on
  one PC; a second machine is a cross-check).
- **Registers:** the one call in R1-10's refusal slot; headless pins;
  `design-gui-inventory.md` only if R1's dialog wording changes.
- **Size:** S to M. **Depends on:** R2-01, R1-10. **Spike:** S5 (heights equal in two
  processes on one PC; if not, the terrain check is dropped and only bodies and assemblies
  remain).
- **PC:** yes (expected heights, one in-game run).
- **Risk:** medium; a too-tight tolerance refuses honest installs, which the two-process
  sample settles (a second machine cross-checks it).

#### R2-03 The three terrain patches in every race save

- **Change:** `RaceSites.Terrain.cs`: for slots 1, 2, 3 a new GameObject under Kerbin's PQS
  holding a `PQSMod_MapDecalTangent` whose fields are copied from the stock "KSC" decal, with
  `position` = the site's radial and `absoluteOffset` = the site's height from
  `SitePlacementMath` (created here with only those two members, the ones this task calls);
  then the mod list re-collected and the terrain rebuilt (the call S4b /
  S5 names). The stock decal is never touched, so site 0 keeps its stock patch (design 7.3).
  Restore destroys the three objects and re-collects and rebuilds. `RaceSitesSequence`
  gets its first two steps (`AddDecals`, `RemoveDecals`); `IRaceSitesWorld` is declared with
  them. `RaceSites.Reconcile` now applies and restores (decals only).
- **Proof:** xUnit `RaceSitesSequenceTests` with a fake world (order of steps, rollback after
  a failed step, restore removes exactly what apply added, a second apply is a no-op);
  `SitePlacementMathTests` (radial from lat / lon round trip, offsets). In-game
  `RaceSites.PatchHeights` (SPACECENTER, race save; hosted by RACE-9): `GetSurfaceHeight` at
  nine points of each site's footprint equals the table height within 0.5 m for all four
  sites; `RaceSitesStock.NoPatchesWhenStock` (single-player save; hosted by SITES-2): no
  `ParsekRaceSiteDecal*` object and natural heights at site 1 to 3 points.
- **Registers:** `scripts/arch/lifetimes.toml` entries for the `RaceSites` statics (process,
  with reasons) and `lifetime_pins.toml` if needed; R1-06's `race-no-writer` allowlist names
  `RaceSites` (the stock space center objects, design 15.4); CLAUDE.md key-file entry for
  `RaceSites` (process-lifetime Kerbin state, apply / restore as a pair), copied to
  AGENTS.md.
- **Size:** M. **Depends on:** R2-01. **Spike:** S4b / S5: which PQS call re-collects the mod
  list and rebuilds quads (`ResetModList` is internal, PQS.cs:4625; `ForceStart`, :1363);
  if a rebuild is needed after the scene builds, the step moves to the first scene-ready
  callback and the sequence gets a "deferred" step.
- **PC:** yes.
- **Risk:** medium; terrain rebuild timing is the least known part of stock here.

#### R2-04 Move the working space center to the home site

- **Change:** `RaceSites.Move.cs`: when the home slot is not 0, capture the stock baseline
  (once per process), set the "KSC" city's `repositionRadial` to the home radial,
  `repositionRadiusOffset` to the home height plus the stock city-minus-decal difference
  read from the baseline, and `reorientFinalAngle` to the stock angle corrected by the
  longitude shift (`SitePlacementMath.FinalAngleFor` over `SiteTable.LongitudeShift`, both
  added here with the city offset, their first production caller); call `Orientate()`. Then refresh
  every cache of section 3.1: `SpaceCenter.Instance.Start()`, `SetSpawnPointsLatLonAlt()` on
  each of `PSystemSetup.Instance.SpaceCenterFacilityLaunchSites`. Restore writes the
  baseline back and refreshes the same caches. New sequence steps `MoveCity`,
  `RestoreCity`, `RefreshStockCaches` in that order after the decals.
- **Proof:** xUnit `SitePlacementMathTests` extended (final-angle correction is zero for
  site 0, sign and size for each site, wraps); `RaceSitesSequenceTests` (the city moves
  only after the decals, restores before they go). In-game `RaceSites.HomeSiteMoved`
  (SPACECENTER, RACE-9): `SpaceCenter.Instance.Latitude/Longitude` within 50 m of the table
  centre; the LaunchPad and Runway spawn points equal the table's pad and runway threshold
  within 2 m; runway bearing (threshold to end) equal to stock within 0.1 degree (catches a
  wrong sign of the angle correction); `RaceSitesStock.StockRestored` (single-player save
  after a race save, SITES-2): every baseline field equal to stock.
- **Registers:** lifetimes for the baseline; CLAUDE.md entry extended (the stock caches
  refreshed and why); `CHANGELOG.md` only if race saves are player-reachable by then.
- **Size:** M. **Depends on:** R2-03. **Spike:** S4b (the main one). If S4b finds another
  stock cache, it becomes one more refresh call in this step and one more assertion; if
  moving the city is not enough for the Space Center camera, add `ResetCamera` at the first
  Space Center scene after an apply.
- **PC:** yes.
- **Risk:** high; a missed cache shows only in game (recovery, contracts, the morning warp).

#### R2-05 Parsek's own home position follows the home site

- **Change:** P1-01 lands `HomeSpaceCenter` with no setter; this task adds it (OV-53):
  `HomeSpaceCenter.Apply(HomeSpaceCenter)` (bumps `Version`) and
  `HomeSpaceCenter.RestoreStock()` on that type, over one private static that only those
  two methods write (`Current` returns it, after P1-03's `CurrentOverrideForTesting`).
  `RaceSites` calls `Apply` with `SiteTable.HomePointsFor(slot)` (added here, its first
  caller; `Apply`, `RestoreStock` and their caller land in one PR) at the end of apply and
  `RestoreStock` at the start of restore (the statics ratchet of design 15.14).
  `LaunchSiteOnlyGhost` adds `HomeSpaceCenter`'s version to its cache key
  (LaunchSiteOnlyGhost.cs:36-39, the recompute guard near :90); P1 does not. The circle
  cache: P1-03 already drops it on a version change; if S4a's P1-03 VERIFY shows the
  runtime read holds the stock KSC's own `LaunchPad` / `Runway`, this task also edits
  `LaunchSiteExclusionZones.GetAltSiteCircles` to leave those two names out (the home pad
  and runway come from `HomeSpaceCenter` through the detector) or rebuilds them from
  `HomeSpaceCenter` when it is not stock (OV-52); `RaceWorld` still never calls
  `LaunchSiteExclusionZones`.
- **Proof:** xUnit: the retirement predicate with `HomeSpaceCenter` set to site 2 retires a
  landed end on site 2's pad and not on the stock pad (`DecideLaunchSiteEndOfFlightRetirement`);
  `Apply` bumps the version and `RestoreStock` returns `Stock`; `LaunchSiteOnlyGhost`
  recomputes when only the version changes; with S4a's answer yes, the circle cache holds
  no circle at the stock pad after `Apply` to site 2 (P1-03's reader seam). In-game
  `RaceSitesFlight.RetirementCirclesAtHome` (FLIGHT, race save at slot 1 or above; hosted by
  RACE-10): the KSC zone classifier answers Pad at the home pad's spawn point. The
  single-player suite and lanes EX-1, LF-1, LF-2 stay green (stock value unchanged outside a
  race).
- **Registers:** `scripts/arch/lifetimes.toml`: `HomeSpaceCenter`'s static tagged `process`
  ("the home center of the loaded game; written only by Apply / RestoreStock, which
  `RaceSites` calls at every scene request that changes the sites") and
  `lifetime_pins.toml` re-pinned; facts pins (statics written by another production type
  stays flat: the write goes through `HomeSpaceCenter`'s own methods).
- **Size:** S. **Depends on:** R2-04, P1-01, P1-03. **Spike:** S4a (the P1-03 VERIFY decides
  whether the circle-cache filter is needed).
- **PC:** yes (one in-game test).
- **Risk:** low.

#### R2-06 One scenery copy of the KSC, built inert

- **Change:** `RaceCore/SceneryStripPolicy.cs`: allowlist of component types a copy keeps
  (`Transform`, `MeshFilter`, `MeshRenderer`, `LODGroup`, non-trigger `Collider` kinds,
  `Light` subject to S5); everything else is removed; every kept GameObject is retagged
  `Untagged`; trigger colliders are removed. `RaceWorld/RaceScenery.cs`: `Build(slot)` clones
  the "KSC" hierarchy under an INACTIVE holder (so no `Awake` or `Start` runs on stock
  components), replaces each upgradeable facility's current level instance with an instance
  of its LAST `UpgradeLevel.facilityPrefab` at the level's local pose, applies the policy,
  adds a configured `PQSCity` at the slot (radial, offset, corrected angle from
  `SitePlacementMath`, the stock LOD ranges), parents it under Kerbin's PQS, re-collects
  mods and activates. `Destroy(slot)` removes it. In this task the copy is built only by an
  in-game test, not by apply: the attribute-rooted `CopyIsInert` (InGameTests is live for
  the ratchet, `docs/dev/arch/README.md` "Uncalled code") is the first caller of `Build`,
  `Destroy` and `SceneryStripPolicy`; R2-07 adds the apply caller.
- **Proof:** xUnit `SceneryStripPolicyTests` (each stock type named in 3.1 is removed:
  `UpgradeableFacility`, `UpgradeableObject`, `DestructibleBuilding`, `SpaceCenterBuilding`,
  `SpaceCenter`, `CommNetHome`, `PQSCity`, any unknown MonoBehaviour; tags always become
  `Untagged`; triggers removed). In-game `RaceSites.CopyIsInert` (SPACECENTER, RACE-9):
  builds a copy
  at slot 2, then asserts no component outside the allowlist under it, every collider
  untagged and non-trigger, `FindObjectsOfType<DestructibleBuilding>().Length` and
  `FindObjectsOfType<UpgradeableFacility>().Length` unchanged by the copy, the copy's pad
  spawn transform (found by the stock facility's `spawnTransformURL`) at the table's pad
  point of slot 2 within 2 m; destroys it.
- **Registers:** headless pins; `scripts/grep-audit-*` none.
- **Size:** M. **Depends on:** R2-04 (shares `SitePlacementMath` angle correction).
  **Spike:** S5 (whether the inactive clone plus prefab swap gives the full max-level
  look; whether a `PQSCity` on a clone is picked up by the PQS; whether `Light` stays).
- **PC:** yes.
- **Risk:** high; stock components the allowlist does not know may carry behaviour, which
  is why it is an allowlist.

#### R2-07 Copies at every other site, as part of apply

- **Change:** apply builds a copy at each slot other than the home slot (three copies; at
  site 0 when the home slot is not 0, design case 25 for empty slots); restore destroys them
  first. Sequence steps `BuildCopies`, `DestroyCopies`. If S5 finds three copies too costly
  at load, a copy is built when the camera first comes within its LOD range instead
  (per-copy lazy build from a scene-ready tick on R1-04's `ParsekRaceBoot`), with the same
  placement.
- **Proof:** in-game `RaceSites.CopiesPresent` (SPACECENTER, RACE-9) and
  `RaceSitesFlight.CopiesPresent` (FLIGHT, RACE-10), race save: exactly the three expected
  `ParsekRaceSiteCopy<slot>` objects, none at the home slot, the same pad-point checks as
  R2-06 for each; `RaceSitesStock.NoCopiesWhenStock` (single-player save, SITES-2). The
  `Sites applied` line reports copy count and milliseconds (S5's numbers, pinned loosely in
  SITES-1's logContract).
- **Registers:** lifetimes; CLAUDE.md entry (copies, the allowlist and why).
- **Size:** M. **Depends on:** R2-06. **Spike:** S5 (cost; lazy or eager).
- **PC:** yes.
- **Risk:** medium; frame and memory cost of three building sets.

#### R2-08 Launch rules: the own site and, when the race uses them, the alternate sites

- **Change:** `RaceCore/RaceLaunchRules.cs`: `Decide(alternateSites, makingHistoryInstalled,
  siteName) -> (allowed, reason)`: "LaunchPad" and "Runway" always; the four Making History
  names (LaunchSiteExclusionZones.cs:79) only when `alternateSites` and the expansion is
  installed; any other name refused. `Patches/RaceLaunchGuardPatch.cs`: in a race save a
  prefix on `FlightDriver.StartWithNewLaunch` skips a refused launch with a Warn line and one
  screen message (an event: the launch did not happen), and a postfix on
  `EditorDriver.OtherLaunchSitesValid` returns false when the race does not use the
  alternate sites, so the stock picker shows them greyed (the stock-control annotation
  exception: the mark and the block read the same predicate). No change outside a race save.
- **Proof:** xUnit `RaceLaunchRulesTests` (matrix of setting x expansion x name, unknown
  names, empty name); log-capture test of the refusal line. In-game
  `RaceSitesEditor.LaunchRules` (EDITOR; hosted by SITES-3's one `RunTests` step):
  `OtherLaunchSitesValid()` follows the race setting. Harness SITES-3 (R2-14).
- **Registers:** `design-gui-inventory.md` (one screen message added, count 88 -> 89);
  `TooltipEchoBudgetTests` untouched (no tooltip text); Harmony patch list in CLAUDE.md
  ("ParsekHarmony.cs + Patches/" entry).
- **Size:** S to M. **Depends on:** R2-01, R1-03 (`RaceLink.alternateSites`), R1-10 (the
  manifest's `AllowOtherLaunchSites`). **Spike:** S4b (that the Space Center picker path
  also reaches `StartWithNewLaunch`; if not, a second prefix on that path).
- **PC:** yes.
- **Risk:** low; there is no stock path to a copy's pad, so the guard is a backstop.

#### R2-09 Science parity: the KSC mini-biomes at home, none at a copy

- **Change:** none in production if S2 confirms the expectation; this task lands the proof.
  In-game tests in FLIGHT at the home site of a race save at slot 1 or above (category
  `RaceSitesFlight`, hosted by RACE-10):
  `RaceSitesFlight.HomePadMiniBiome` (the launched vessel's `landedAt` contains "LaunchPad"
  and the experiment biome stock would use is the LaunchPad mini-biome);
  `RaceSitesFlight.HomeKscAreaMiniBiome` (a probe placed on the KSC grounds of the home site
  gets the "KSC" landed-at name); `RaceSitesFlight.CopyGivesNoMiniBiome` (a downward raycast
  onto a copy's pad, VAB roof and grounds hits only `Untagged`, non-trigger colliders, which
  by Part.cs:7787-7791 and :9561-9583 cannot set `landedAt`).
- **Proof:** the three tests above; a note in the test file citing the stock lines.
- **Registers:** RACE-10's pinned tally (`CommittedBatchTallySourceSyncTests`).
- **Size:** S. **Depends on:** R2-04, R2-07. **Spike:** S2. If S2 finds the KSC area to be a
  biome-map biome, R2-10 applies.
- **PC:** yes.
- **Risk:** medium; the main fairness risk of Science and Career races (design 7.3).

#### R2-10 (conditional on S2) Biome override around the sites

- **Change:** only if S2 shows the KSC area biome comes from the biome map. A pure
  `RaceCore` rule "inside radius R of the home site's centre the area biome is the KSC
  biome; inside radius R of the stock KSC when the home slot is not 0 it is the biome the
  map gives just outside that radius" and a postfix on `ScienceUtil.GetExperimentBiome` and
  `GetExperimentBiomeLocalized` (ScienceUtil.cs:118, :140) applying it in a race save. Both
  directions are needed: the home site gains the biome, and the stock location (a copy in
  that game) loses it.
- **Proof:** xUnit for the rule (inside, outside, on the radius, both directions, site 0
  home is a no-op); in-game test at the home site and at the stock location.
- **Registers:** Harmony patch list; CLAUDE.md.
- **Size:** S to M. **Depends on:** R2-09, S2.
- **PC:** yes.
- **Risk:** medium; a biome rule in a stock read path touches every experiment.

#### R2-11 Every stock "home" surface at the home site

- **Change:** in-game tests for the surfaces design 7.3 names, and the fixes S4b asks for
  (expected: none beyond R2-04). SPACECENTER: the camera pivot within 2 km of the home
  centre; the morning warp target uses the home longitude (`SpaceCenter.Instance.Longitude`
  equals the table). TRACKSTATION: the KSC map node's world position (`KSCSiteNode`, the
  Runway facility transform) at the home runway. FLIGHT: `SpaceCenter.Instance.GreatCircleDistance`
  of the vessel on the home pad below 100 m; the KSC CommNet home node within 2 km of the
  home centre after network initialisation. If S4b shows the KSC `CommNetHome` is not under
  the "KSC" transform, `RaceSites.Move.cs` moves it too and refreshes its node.
- **Proof:** in-game `RaceSites.SpaceCenterCamera` (RACE-9),
  `RaceSitesTrackingStation.MapKscNode` (RACE-11), `RaceSitesFlight.RecoveryDistance` and
  `RaceSitesFlight.CommNetHome` (RACE-10).
- **Registers:** the pinned tallies of RACE-9, RACE-10 and RACE-11.
- **Size:** S (tests) to M (if CommNet needs a move). **Depends on:** R2-04. **Spike:** S4b.
- **PC:** yes.
- **Risk:** medium.

#### R2-12 Harness lane SITES-1: a launch, a recovery and a Space Center visit at each site

- **Change:** `harness/scenarios/SITES-1-four-sites.toml` on `stock-minimal` (Making History
  present): with `RaceSetup action=create` (R1-10) create a race (slot 0) in the temporary
  shared folder `${raceShared}` (T-03), then `RaceSetup action=join` (R1-11) three times
  (slots 1, 2, 3, first free in order), each a new save. Per save: Space Center settles;
  `ListHandles kind=sites` (T-07) gives the working space center, the pad and runway spawn
  points, the recovery reference point and the patch heights, each compared with that slot's
  `kerbin-equator-1` row (values pinned in the spec, tolerances as in R2-03 and R2-04);
  `GoToEditor` VAB; `LaunchFromEditor` a small craft to the pad; `ListHandles kind=sites`
  again in flight; `Recover`; back at the Space Center the recovery is logged with full
  value; `ExitToSpaceCenter` visit. One save also runs `RevertToLaunch` (a load inside a race
  save must reconcile to Nothing). No `RunTests` step: a spec has at most one, and the
  per-site values come from `ListHandles` (OV-03); the `RaceSites`, `RaceSitesFlight` and
  `RaceSitesTrackingStation` cells run on T-29's hosts RACE-9, RACE-10 and RACE-11 (OV-09).
  logContract pins `Sites applied: table=kerbin-equator-1 home=<slot>` once per save and no
  `Sites apply failed`.
- **Proof:** the lane; pre-registered readings in its header (a `Sites apply failed` line,
  a `ListHandles kind=sites` value off its row, a launch that lands at the stock pad in a
  slot 1-3 save). No batch, so no `INTERIM_PIN_IDS` entry.
- **Registers:** `docs/dev/autotest-status.md` row; `python harness/tools/lane_catalog.py`
  (new lane, `test_lanecatalog`); `harness/coverage/registry.toml` values if a new
  dimension value is used; check open PR branches for the `SITES-` prefix before taking it
  (`RS-1` is taken by `RS-1-relay-route-endpoint-lost-rescan`).
- **Size:** M. **Depends on:** R2-04, R2-05, R2-07, R2-08, R2-09, R2-11, R1-10, R1-11, T-03,
  T-07.
- **PC:** yes (a flight).
- **Risk:** medium; a long lane (four saves); tier by measured p50 after the first flight.

#### R2-13 Harness lane SITES-2: back to stock Kerbin, and race to race

- **Change:** `harness/scenarios/SITES-2-sites-restore.toml`, the one lane for stock Kerbin
  after a race save (it absorbs T-14's RACE-5, OV-04): create a race and join it twice as in
  SITES-1 (saves at slots 1 and 2); load the slot 2 save; `ListHandles kind=sites` shows
  slot 2's row. Then (a) `NewGame` SANDBOX with `as=` through the main menu (SWS-1's
  route): `ListHandles kind=sites` equals RACE-0's stock values (T-07's baseline, pinned in
  the spec); (b) back to the slot 2 save with `LoadGame`: applied again; (c) the slot 1 save
  loaded straight from the slot 2 save: `Sites restored ... reason=other-race-save` then
  `Sites applied ... home=1`, and `ListHandles kind=sites` shows slot 1's row; (d) a direct
  seam `LoadGame` of the single-player save straight from the slot 1 save's Space Center
  (no main menu; `harness/run.py:1450-1451`): `ListHandles kind=sites` equals RACE-0's stock
  values again. Last, the spec's one `RunTests category=RaceSitesStock` in that
  single-player save (`StockRestored`, `NoPatchesWhenStock`, `NoCopiesWhenStock`,
  `KerbinIsStock`; T-14's `RaceStockKerbin` cells join them). Design cases 23 and 31.
- **Proof:** the lane, with logContract on the restore and apply lines in order and the
  `ListHandles` comparisons against RACE-0; never-flown pins (`total=` exact) and an entry
  in `IngameBatchWiringGroupTests.INTERIM_PIN_IDS` until the first flight.
- **Registers:** as SITES-1, plus `CommittedBatchTallySourceSyncTests` pins for
  `RaceSitesStock` and the new category in the lane catalog.
- **Size:** M. **Depends on:** R2-12, T-07.
- **PC:** yes.
- **Risk:** low.

#### R2-14 Harness lane SITES-3: launch rules and the alternate sites setting

- **Change:** `harness/scenarios/SITES-3-launch-rules.toml` on `stock-minimal`: one race
  created with the alternate sites on (a launch from the Desert pad works, as MC-4 does it)
  and one with them off (the spec's one `RunTests category=RaceSitesEditor` in the editor:
  `LaunchRules`, the picker offers no other site; a forced launch to "Desert_Launch_Site"
  through the seam is refused with the `RaceLaunchGuard` line). Plus a vessel left on the
  home pad: `LaunchSiteProbe` reports the stock "launch site not clear" dialog at the home
  site (the tags moved with the city).
- **Proof:** the lane; never-flown pins and an `INTERIM_PIN_IDS` entry until the first
  flight.
- **Registers:** as SITES-2 (`RaceSitesEditor` pins). A forced-site launch arg on
  `LaunchFromEditor` if it has none (TestCommands; then `GuiCensusSeamVerbTests` / hlib
  sync if the verb grammar changes).
- **Size:** S to M. **Depends on:** R2-08, R2-12.
- **PC:** yes.
- **Risk:** low.

#### R2-15 Fold the findings into the design and the plan of record

- **Change:** docs only: design 7.1 and 15.7 (reconcile at every scene request, not only at
  the main menu), 7.3 (the stock patch never moves; every race save carries the three new
  patches; the orientation correction; the copy recipe; the KSC area as a mini-biome if S2
  confirms it), section 12 rows (the stock caches list of 3.1). Rebuild the design PDF with
  `scripts/build-design-pdfs.py` if the design is in its `DOCS` table.
- **Proof:** none (docs).
- **Registers:** `test_doc_spec_sync.py` reads `docs/dev` (keep lane ids consistent).
- **Size:** S. **Depends on:** R2-04, R2-09 (and S2, S4b, S5 results).
- **PC:** no.
- **Risk:** low.

### 4.2 Order

R2-01, then R2-02 / R2-03 / R2-08 (independent), then R2-04 (after R2-03), then R2-05 /
R2-06 / R2-11 (after R2-04), then R2-07 (after R2-06), R2-09 (after R2-07), R2-10 if needed,
then the lanes R2-12, R2-13, R2-14, then R2-15.

Inert in single-player saves at every step: the only code that runs outside a race save is
the one reconcile comparison `ParsekRaceBoot` makes at a scene request (R2-01) and the two
Harmony patches' race-gate check (R2-08). Between R2-03 and R2-04 a race save at slot 1 to
3 still plays at the stock KSC with the new patches standing empty; race saves exist only in
development builds then.

## 5. Parallel tracks

- Track A (terrain and move): R2-03 -> R2-04 -> R2-05.
- Track B (launch rules): R2-08, any time after R2-01.
- Track C (fingerprint): R2-02, any time after R2-01 and R1-10.
- Track D (scenery): R2-06 can be written beside R2-04 (its own files `RaceScenery.cs`,
  `SceneryStripPolicy.cs`) but lands after R2-04 because it uses the angle correction in
  `SitePlacementMath.cs`; R2-07 wires it into the sequence.
- Track E (proof): R2-09, R2-11 after R2-04 / R2-07; lanes last.

Hot files that force an order: `RaceWorld/RaceSites.cs` and `RaceCore/RaceSitesSequence.cs`
(every step is added there: R2-03, R2-04, R2-05, R2-07), `RaceCore/SitePlacementMath.cs`
(R2-03, R2-04, R2-06), `InGameTests/RaceSitesTests.cs` (every task adds tests; split into
`RaceSitesTests.<Area>.cs` partials to avoid conflicts), the pinned tallies of T-29's
RACE-9, RACE-10 and RACE-11 specs (each new test re-pins its host's `total=`),
`scripts/arch/lifetimes.toml`, `scripts/arch/headless_pins.toml`,
`docs/dev/flight-profiles.md` (generated by the lane catalog), `docs/dev/autotest-status.md`.
R2-01 adds one call to R1-04's `ParsekRaceBoot.cs`, and R2-05 edits P1-01's
`HomeSpaceCenter.cs` and P1-03's `LaunchSiteExclusionZones.cs`: each lands after the task
that created the file.

## 6. Tests and proof

Unit tests (xUnit, cloud or local, no game):

| Test class | Proves |
|------------|--------|
| `SiteTableTests` | the table's rows, derived points, wrap, launch areas |
| `RaceSitesPlanTests` | the reconcile truth table, including every load route of 4.0 |
| `RaceSitesSequenceTests` | step order, rollback on failure, restore is the inverse, idempotence, the log lines (log sink) |
| `SitePlacementMathTests` | radial, offsets, the final-angle correction |
| `KerbinFingerprintTests` | each reason, invariant text |
| `SceneryStripPolicyTests` | the allowlist and retag rule |
| `RaceLaunchRulesTests` | the launch matrix |
| retirement / `LaunchSiteOnlyGhost` tests (R2-05) | the single-player rule follows the home site |

In-game tests, one category per scene (OV-09), each `InGameAssert.Skip` with the context it
needs ("needs a race save at slot 1 to 3", "needs a single-player save"); a host lane runs
one batch in one scene, so each category has exactly one host:

| Category | Scene and save | Host | Tests |
|----------|----------------|------|-------|
| `RaceSites` | SPACECENTER, race save at slot 1 to 3 | RACE-9 (T-29) | `PatchHeights`, `HomeSiteMoved`, `CopyIsInert`, `CopiesPresent`, `SpaceCenterCamera` |
| `RaceSitesFlight` | FLIGHT, race save at slot 1 to 3, vessel on the home pad | RACE-10 (T-29) | `RetirementCirclesAtHome`, `CopiesPresent`, `HomePadMiniBiome`, `HomeKscAreaMiniBiome`, `CopyGivesNoMiniBiome`, `RecoveryDistance`, `CommNetHome` |
| `RaceSitesTrackingStation` | TRACKSTATION, race save at slot 1 to 3 | RACE-11 (T-29) | `MapKscNode` |
| `RaceSitesEditor` | EDITOR, race save without the alternate sites | SITES-3 (R2-14) | `LaunchRules` |
| `RaceSitesStock` | SPACECENTER, single-player save after a race save | SITES-2 (R2-13) | `KerbinIsStock`, `NoPatchesWhenStock`, `StockRestored`, `NoCopiesWhenStock` |

What they prove together: the working space center at each site (pad and runway spawn,
Space Center scene, recovery distance), terrain heights equal the table, scenery present and
inert, sites removed on leaving (design 11, in-game list).

Parity across games: a copy at slot k and the working center at slot k are placed by the
same `SitePlacementMath` calls from the same row, and both tests compare against the table's
numbers, so the SITES-1 readings of different saves (`ListHandles kind=sites`) compare with
each other through the table. S5 compares the same heights read in two processes on one PC;
a second machine is a cross-check, not a gate.

Harness lanes: SITES-1 (four sites, no batch), SITES-2 (restore, race to race, compared with
T-07's RACE-0 stock baseline, both load routes; hosts `RaceSitesStock`), SITES-3 (launch
rules; hosts `RaceSitesEditor`). Each spec has at most one `RunTests` step; per-site values
come from `ListHandles kind=sites` (T-07). All need the owner's PC (flights). Unit tests run
anywhere.

## 7. Gates and registrations

- **Architecture map** (`scripts/arch/modules.toml`): no new root file (the reconcile call
  sits in R1-04's `ParsekRaceBoot.cs`, placed by P12-02's `ParsekRace*` rule);
  `Patches/RaceLaunchGuardPatch.cs` by the Patches folder rule; RaceCore / RaceWorld files
  by R1-01's folder rules. Re-pin module counts if the suite asks.
- **Layers and forbidden edges:** RaceCore files name only the floor (LogIO, Core); RaceWorld
  files name RaceCore, Core (`HomeSpaceCenter`) and stock types, never `Controllers`,
  `GameActions`, `Recording` or `Ghost` (none needed). `ParsekRaceBoot` (Controllers) and
  the patch (Patches) name the `RaceWorld` facade and `RaceCore`; R2-05's edit of
  `LaunchSiteExclusionZones` (Controllers) reads only `HomeSpaceCenter` (Core).
  `layer_pins.toml` must not move; if it does, a file is in the wrong module.
- **Headless:** every RaceCore file stays leak-free (`headless_pins.toml` unchanged except
  for R1-01's module entry). Live reads live only in `RaceWorld`.
- **Static lifetimes:** every `RaceSites` static tagged `process` in
  `scripts/arch/lifetimes.toml` with the reason "Kerbin's objects outlive a save; reconciled
  at every scene request"; `HomeSpaceCenter`'s static (R2-05) tagged `process` the same
  way; `RaceLaunchGuardPatch` holds no state.
- **Code facts:** statics written by another production type: `HomeSpaceCenter` is written
  through its own `Apply` / `RestoreStock` (R2-05); `RaceSites` statics through its own
  methods. Uncalled methods:
  each pure piece lands with its production caller in the same task (4.1).
- **Log tags:** `RaceSites`, `RaceLaunchGuard` added to `scripts/arch/log_tags.json`.
- **Grep audits:** the background-thread gate is untouched (no threads). R1-06's
  `race-no-writer` allowlist names `RaceSites` (stock space center objects) explicitly
  (R2-03).
- **Save format:** none. R2 persists nothing; `siteSlot` and `siteTableId` are R1-03's
  `RaceLink`. A new site table is a new id, never an edited row (design 15.10).
- **LoadReconcilePolicy:** no new load and no new state category; the reconcile reads the
  mark, writes nothing to the save.
- **GUI:** one screen message (R2-08) and, depending on question 10.1, one more
  (`design-gui-inventory.md` counts); no window, no tooltip, the greyed alternate sites use
  the stock-control annotation exception.
- **In-game tests and harness:** five categories, one per scene (section 6) ->
  `lane_catalog.py`; `RaceSites`, `RaceSitesFlight` and `RaceSitesTrackingStation` hosted by
  T-29's RACE-9, RACE-10 and RACE-11 (every new test re-pins that host's `total=`,
  `CommittedBatchTallySourceSyncTests`), `RaceSitesStock` by SITES-2, `RaceSitesEditor` by
  SITES-3; one `RunTests` step per spec at most; never-flown SITES-2 and SITES-3 in
  `INTERIM_PIN_IDS` (SITES-1 runs no batch); `autotest-status.md` rows; lane ids `SITES-1`,
  `SITES-2`, `SITES-3` unique across open PRs.
- **Docs per commit:** CLAUDE.md "Key source files" entry for `RaceSites` (R2-03, extended
  by R2-04 and R2-07) and the Patches list (R2-08), then `cp .claude/CLAUDE.md AGENTS.md`;
  `CHANGELOG.md` entries only for what a player of a race save sees; todo entries for
  anything the spikes leave open.

## 8. Risks

- **Terrain rebuild timing** (R2-03): a decal added after the PQS built its quads shows only
  after a rebuild, and a rebuild during a scene can hitch. Noticed by `PatchHeights` (heights
  read through the mod chain) and a visual check in SITES-1; S4b / S5 settle the call.
- **A stock cache not on the list** (R2-04): something still reads the stock location
  (a contract, a dialog, a marker). The IL caller search of 3.1 covers `SpaceCenter`'s
  members; S4b's full visit and SITES-1 cover the rest. A new finding is one more refresh call.
- **Wrong orientation** (R2-04, R2-06): the city or a copy rotated by the longitude shift.
  The runway bearing assertion catches it.
- **A copy keeps behaviour** (R2-06): a stock component outside the known list. The strip
  is an allowlist, the census assertion counts what remains, and the registry counts
  (`DestructibleBuilding`, `UpgradeableFacility`) must not change.
- **Restore incomplete** (all): a single-player save on a changed Kerbin. Noticed by
  `StockRestored`, `NoPatchesWhenStock`, `NoCopiesWhenStock` and SITES-2, and by the baseline
  drift Warn.
- **Apply fails halfway**: the sequence rolls back to stock and logs the step. The race
  save then would load on stock Kerbin; question 10.1.
- **Cost** (R2-07): three building sets. S5 measures; the lazy build is the fallback.
- **Another mod moves the KSC** (KSCSwitcher, Kopernicus, Kerbal Konstructs edits): the
  fingerprint refuses (R2-02); SITES lanes run on stock installs only.
- **Development race saves made before R2-04** at slot 1 to 3 have vessels at the stock
  pad; after R2-04 they load at the moved site. Delete them; nothing is released.

## 9. Corrections and gaps

1. **Removal only at the main menu is not enough.** Design 7.1 and 15.7 remove the sites
   "on every return to the main menu". Stock also loads another save from inside a game
   (and the harness `LoadGame` does), without passing the main menu. The plan reconciles
   at every scene load request (HighLogic.cs:795) against the mark of `HighLogic.CurrentGame`,
   which covers both routes and race-to-race (case 31). Settled: R1-04's one addon
   `ParsekRaceBoot` calls `RaceSites.Reconcile` on every scene request (OV-51).
2. **The stock flatten patch never moves.** Design 7.3 says the move takes "the stock space
   center object and its flattening terrain patch", and also "Site 0's stock patch stays
   where it is ... a player at another site gets a new patch there". The plan follows the
   second: the stock decal stays; EVERY race save, whatever its home slot, adds the three
   patches of sites 1 to 3 (design "The terrain is the same in every game").
3. **The KSC area is probably a mini-biome, not a biome-map biome.** Design 7.3 expects the
   KSC biome to stay at the stock location in Kerbin's biome map and asks for an override.
   Stock sets the landed-at name from collider and trigger TAGS (Part.cs:7791, :9583) and
   the experiment biome uses that name first (ModuleScienceExperiment.cs:2303-2320); the
   survey's biome map read finds no KSC biome at the KSC's longitudes
   (`kerbin-equator-survey-2026-11-14/biomes.csv`, though its README calls biome names
   unreliable). The plan makes the override conditional (R2-10) on S2.
4. **The move list in 7.3 is incomplete.** Beyond `SpaceCenter` and the spawn points:
   the city's rotation needs a longitude correction (PQSCity.cs:735); the facility spawn
   points refresh only on a level change (PSystemSetup.cs:634); `SpaceCenter`'s cache is
   read by survey contracts, the morning warp, the distance records, random surface points
   and music, not only recovery (3.1); `CommNetHome` caches its position at network
   initialisation. All are on R2-04 / R2-11 and S4b's list.
5. **A copy taken from the live KSC would show the local player's levels and register
   duplicate stock ids.** Design 7.3 asks for fully built copies; the recipe must use the
   last `UpgradeLevel.facilityPrefab` and strip before `Awake`, because
   `DestructibleBuilding` and `UpgradeableFacility` register by id
   (DestructibleBuilding.cs:774, Upgradeables.UpgradeableFacility.cs:61) and Parsek's
   facility code scans `FindObjectsOfType<DestructibleBuilding>()` at four sites (3.2).
6. **"Alternate sites off" and a mid-race difficulty change.** The race setting maps onto
   stock `AllowOtherLaunchSites` (GameParameters.cs:223, EditorDriver.cs:1528), which a
   player can change; case 21 allows and discloses difficulty changes. The plan's guard
   enforces `RaceLink.alternateSites` whatever the difficulty says (question 10.2).
7. **The launch-site circle cache does not need resetting for the move.** Design 7.3 lists
   it among what must follow the move; the cached circles are the Making History sites,
   which do not move (LaunchSiteExclusionZones.cs:220-247). P1's reset still fixes the
   partial-read freeze, a single-player matter. Settled: not established, because the
   read admits `LaunchPad` / `Runway` (LaunchSiteExclusionZones.cs:269-270); S4a's P1-03
   VERIFY decides, and R2-05 filters or rebuilds those circles if they are cached (OV-52).
8. **The optimizer's launch-site rule reads no position** (RecordingOptimizer.cs:648),
   confirmed; design 12 lists it as a reader to check. No work.
9. **`LaunchSiteOnlyGhost` caches against `RecordingStore.StateVersion` only**
   (LaunchSiteOnlyGhost.cs:36-39). P1's "version for caches to key on" must reach it; R2-05
   does it if P1 did not. Settled: P1 does not; R2-05 adds the key and the
   `HomeSpaceCenter` `Apply` / `RestoreStock` setter with its lifetime tag (OV-53).
10. **A recording's launch-site name does not say which site.** At every home site the
    stock names are "LaunchPad" / "Runway". R3 must take the site from the standing's
    player (`siteSlot`), never from `LaunchSiteName`.
11. **The footprint latitude.** The survey sampled longitudes on eleven latitude rows; the
    table needs each site's footprint centre at the KSC's latitude (-0.074) with the whole
    KSC footprint dry. S1b must check that row, not the nearest dry sample.
12. **Two area sets.** Single player keeps 50 m circles at the home pad and runway
    threshold (`HomeSpaceCenter`, P1); the race's launch areas are wider (100 m pad, strip
    plus 50 m). Both come from the same site row; neither replaces the other. Settled: the
    race's launch areas are `SiteTable` rows built on P1-02's shape test, and R3W-12 calls
    them; there is no `RaceLaunchAreas.cs` (OV-41).

## 10. Questions for the owner

1. **A race save whose sites cannot be set up.** When a race save at sites 1 to 3 is loaded
   and Parsek cannot rebuild its space center (Kerbin was changed by a planet mod after the
   race started, or setting up the site fails), the player's vessels at their home site
   would stand on unflattened ground at the stock location's absence, and stock may destroy
   them. Options: (a) stop the load, go back to the main menu with one message naming the
   reason, the save untouched; (b) load it anyway on stock Kerbin with one warning.
   Recommendation: (a), because (b) can destroy vessels and the save would be written.
2. **Alternate launch sites turned on mid-race.** In a race created without the alternate
   sites, a player can switch stock's "allow other launch sites" difficulty option on later.
   Options: (a) the race setting wins: launches from the alternate sites stay refused, and
   the option shows greyed; (b) allow it and only disclose it next to the player's name, like
   other difficulty changes. Recommendation: (a), because the setting decides how far every
   player has to travel, which is the point of the creator's choice.
