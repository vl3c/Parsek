# R0 spikes - implementation plan

Status line: PLAN (2026-11-14), not started. Design sections covered: 12 (feasibility table
and "still to prove"), 15.11 (S8 to S10), 7.3 (sites), 7.4 (looks), 7.7 (flag replica,
launch areas), 7.12 (observable debug actions), 15.9 (file access on sync clients), 15.5
(flattened relative stretches), 15.13 (costs). Phase R0.

## 1. Goal and scope

R0 turns every "still to prove" cell of design section 12 and every spike of 15.11 into a
measured answer before feature code starts. A spike is throwaway code run on the owner's
Windows PC inside KSP, or a plain script, that answers one question with numbers. Its
product is a spike report in `docs/dev/research/`, not code.

Built in R0:

- one never-merged spike branch `spike/mp-r0` that holds every spike's code (task S0);
- one dedicated spike KSP instance and one copy of it without the DLCs, so the dev install
  and the harness instances stay untouched (S0);
- one small tooling pull request that does merge: a high-resolution mode for the committed
  terrain scan tool, because its numbers become the site table (S1b);
- one spike report per spike (docs-only pull requests), and a close-out that writes the
  results back into the design (S13).

Explicitly not built: no feature code, no prerefactor, no change to production code on
`main` (the spike branch is never merged and never opened as a pull request), no harness
lane on `main`, no co-op feature, no release, version bump or tag.

The spikes and what waits on them:

| Spike | Question it settles | Design decision it settles | Waiting downstream |
|-------|---------------------|----------------------------|--------------------|
| S1b | Exact centre of sites 1 to 3 with the whole KSC footprint dry | 7.3 site table rows | R2-01 (table values and `SiteRow.LaunchAreas`), P1-01 shape of `HomeSpaceCenter` offsets |
| S2 | Does KSC science (buildings, pad, runway, KSC area) work at a moved site; does a copy give none | 7.3 science parity, biome override yes or no | R2-09, R2-10, R2-15 |
| S3 | The four looks, the colors, replacement sets, a DLC suit on a no-DLC game, a suit on a ghost kerbal | 7.4 look table and colors; A4 shape | R5-08, R5-10, R5-11, R5-12; soft: R5-02 (A4), R5-04, R5-07, R5-09; R4-10 (colors) |
| S4 | Can the working KSC move (launch, recovery, Space Center, camera) and when | 7.3 moving checklist, the reconcile hook | S4a: R1-04, R2-01, R2-05, P1-03 VERIFY; S4b: R2-03, R2-04, R2-08, R2-11, R2-15; `SiteRow.LaunchAreas` of R2-01 |
| S5 | Can stripped KSC copies stand as inert scenery, at what cost; same heights in two processes | 7.3 scenery, 15.13 scenery row | R2-02, R2-03, R2-06, R2-07, R2-15 |
| S6 | Does an unknown scenario node survive; main menu entry; create a save from code | 6.2 race mark, 7.2 menu and setup | R1-03 (mark), R1-10 (create), R1-14 (menu, dialog) |
| S7 | Flag replica built the way the code builds a flag, with the stock plaque dialog and supplied text; click range | 7.7 readable rival flags | R3W-24, R3W-25 |
| S8 | What a sync client costs a file open; is a copy worker thread needed | 15.9 worker thread (owner yes or no) | R6-10, R3E-23; soft: R3E-16, T-02 |
| S9 | Which debug actions a mod can observe, with no false positives, including milestones completed outside a flight | 7.12 tool list and disclosure | R3E-09, R6-01 |
| S10 | How many relative stretches have a usable body-fixed copy; how far a flattened stretch drifts | 15.5 flatten rule | R3W-04 |
| S11 (new) | What one ghost costs per frame at race scale | 15.13 per-frame budget | the cap of R3W-14 (A5 budget) |
| S12 (new, optional) | Do SHA-256 and the signing classes work and how fast inside KSP's Mono | 12 last row, 7.15 signed standings | R7 only |

S11 and S12 are added because the design names their risk without a spike: 15.13 says the
costs are "to be measured in the spikes" but only S5 measures one, and the last row of
section 12 asks to "prove the runtime's signing classes work inside KSP" with no spike id.

## 2. Starts after

- Version 1.1.0 is released and the owner says go (brief; design 14).
- Nothing from another slice. No prerefactor (P1 to P12) or addition (A1 to A7) is needed
  first: every spike runs on `main` as it stands at the go commit plus throwaway code.
- Inside R0: S0 first (spike branch and instances). S4a (probe) before S1b scoring, S4b,
  S2 and S5. S4b before S2, S5 and S7's last step. S8a before S8b. S10a before S10b. S13
  last.

Tasks in other slices that depend on this one (final ids, `task-index.md` section A):

- S1b: R2-01.
- S4a: R1-04 (is the new `Game` set at the scene load request, OV-51), R2-01, R2-05 (does
  the launch-site circle cache hold the KSC pad and runway, OV-52), and the P1-03 VERIFY of
  `r0b-prep-small.md` section 2 (folded into S4a here).
- S4b: R2-03, R2-04, R2-08, R2-11, R2-15.
- S5: R2-02 (heights in two processes), R2-03, R2-06, R2-07, R2-15.
- S2: R2-09, R2-10, R2-15.
- S3: R5-08, R5-10, R5-11, R5-12, R4-10; soft: R5-02, R5-04, R5-07, R5-09.
- S6: R1-03, R1-10, R1-14.
- S7: R3W-24, R3W-25.
- S8a, S8b: R3E-23, R6-10; soft: R3E-16, T-02.
- S9: R3E-09, R6-01.
- S10a, S10b: R3W-04.
- S11: R3W-14 (the cap of the A5 budget).
- S12: R7 only (no plan yet).

## 3. What the code looks like today

### 3.1 Hosting: what already exists to run a spike cheaply

- In-game tests are discovered by reflection over Parsek's own assembly only
  (`Source/Parsek/InGameTests/InGameTestRunner.cs:320`, `Assembly.GetExecutingAssembly()`),
  so a spike test must live in Parsek's source on the spike branch. The attribute takes
  `Category`, `Scene` and `AllowBatchExecution`
  (`Source/Parsek/InGameTests/InGameTestAttribute.cs:13`, `:18`, `:38`).
- The command seam is armed by `PARSEK_TEST_COMMANDS=1`, read once in Awake
  (`Source/Parsek/TestCommands/ParsekTestCommandAddon.cs:39`, `:55`). Verbs a spike can
  reuse: `RunTests` (`Source/Parsek/TestCommands/TestCommandVerbs.cs:64`, dispatched at
  `ParsekTestCommandAddon.cs:1795`, any scene per `TestCommandDispatcher.cs:518`),
  `LoadGame` (`TestCommandVerbs.cs:65`), `PlantFlag` (`:75`), `CaptureScreenshot` (`:207`),
  `GoToEditor` (`:247`), `LaunchFromEditor` (`:248`), `DriveVessel` (`:352`),
  `LaunchSiteProbe` (`:396`), `NewGame` (`:405`). So a spike drives its actions as an
  in-game test of category `SpikeSx` run through `RunTests`; no new verb is needed (a new
  verb would also red the hlib vocabulary sync tests).
- `NewGame` already creates and starts a save from code:
  `Source/Parsek/TestCommands/ParsekTestCommandAddon.NewGame.cs:254-304` calls
  `GamePersistence.CreateNewGame(p.Save, stockMode, parameters, p.FlagUrl,
  GameScenes.SPACECENTER, EditorFacility.None)`, fires `GameEvents.onGameNewStart` and
  calls `HighLogic.CurrentGame.Start()`. A seam lane using it: `harness/scenarios/
  CS-0-new-career-game.toml` (steps `LoadGame`, `SetSetting`, `NewGame`, `SaveGame`,
  `FlushAndQuit`; tier operator).
- Harmony: `ParsekHarmony` applies every type in the assembly that carries `[HarmonyPatch]`
  (`Source/Parsek/ParsekHarmony.cs:51-57`). A spike patch must therefore NOT carry the
  attribute; it is applied by hand from a separate Harmony id only when its spike is armed,
  or it would run in every session of the spike DLL.
- The terrain scan tool `harness/tools/kerbin_equator_scan.py` (kRPC `surface_height`,
  `biome_at`; arguments at `:393-402`: `--coarse-step 0.1`, `--fine-step 0.025`,
  `--search-deg 30`, `--shortlist 4`, `--selftest`) writes to `<umbrella>/logs/terrain/
  <date>/` (`:385`), documented at `harness/README.md:2817`. It is not a lane and has no
  test file; its only self-check is `--selftest` (`:343`). It defines the KSC centre as the
  midpoint of pad and runway, not the KSC `PQSCity` origin.
- The survey's fine grids exist only for the tool's shortlist: `fine_opposite_1` spans lon
  106.575 to 108.025, `quarter_east_3` 18.075 to 19.525, and none lies near -162.3; the fine
  step 0.025 deg is about 262 m (`docs/dev/research/kerbin-equator-survey-2026-11-14/`).
  The survey's 11 latitude rows run -0.5737 to 0.4263; none is the KSC row -0.074 exactly.
- Harness instances: profiles `stock-minimal`, `stock-tree`, `modded-compat`
  (`harness/provision/profiles/`). `stock-minimal` copies KSPCommunityFixes, CommunityTechTree
  and ProbesBeforeCrew from the dev install, adds kRPC, TestingTools, MechJeb2 and Parsek
  (`stock-minimal.toml:14-29`) and pins low graphics (`:34-40`). `Squad` and `SquadExpansion`
  are junctioned to the dev install at CLONE (`harness/provision/provlib.py:47-48`), so a
  provisioned instance has the DLCs exactly when the dev install has them. kRPC binds the
  fixed ports 50000 / 50001 (`harness/run.py:106-110`), so two kRPC instances cannot run at
  once on default settings.
- Output rule (`.claude/CLAUDE.md` hard rules): nothing is written into the repo tree or
  the umbrella root; captures go under `<umbrella>/logs/<kind>/`. Curated results go to
  `docs/dev/research/` through a docs pull request (the survey folder is the precedent).

### 3.2 Parsek readers each spike must check

- KSC constants: `SpawnCollisionDetector.cs:216` `KscPadLatitude = -0.0972`, `:219`
  `KscPadLongitude = -74.5575`, `:222` `KscRunwayLatitude = -0.0502`, `:225`
  `KscRunwayLongitude = -74.7300`, `:232` `DefaultKscExclusionRadiusMeters = 50.0`.
- Alternate sites: `LaunchSiteExclusionZones.cs:130` declares `cachedRuntimeCircles`, read at
  `:224`, set at `:247`, cleared only by a test hook at `:325`; the cache survives a move.
- Cheat observation already in place: `Patches/CheatTeleportPatch.cs:16` postfixes
  `FlightGlobals.PostOrbitSet`; `GravityHackDetector.cs:81` reads `HackGravity` widgets via
  `Resources.FindObjectsOfTypeAll<HackGravity>()` (`Patches/HackGravityPatch.cs:14`);
  `GameStateRecorder.cs:1333` `IsCheatCurrencyReason` keys on `TransactionReasons.Cheating`.
- Ghost visuals: `GhostVisualBuilder.PartInfoBuilders.cs:766` copies
  `smr.sharedMaterials` from the prefab (a suit swap must not edit a shared material);
  `GhostPlaybackEngine.GhostVisuals.cs:166` `CreateGhostSphere` is the fallback when a part
  cannot be built; `GhostVisualBuilder.cs:1803` `SpawnFlagVessel(FlagEvent)`.
- Flattening: `GhostPlaybackEngine.SectionPositioning.cs:103`
  `TryPositionRelativeSectionAtPlaybackUT`, `:118` `bool parentAnchored =
  IsParentAnchoredBodyFixedPrimaryCandidate(traj);` is the one switch that sends a relative
  section to its body-fixed copy; `ParsekFlightGhostPositioner.cs:228`
  `TryPositionFromBodyFixedPrimary`. The body-fixed copy is written only when the caller
  passes one: `FlightRecorder.PointCommit.cs:236-243`; `TrackSection.cs:59`
  `bodyFixedFrames`; `TrajectoryTextSidecarCodec.cs:676` treats a relative section with
  `frames` but no `bodyFixedFrames` as an incomplete substitution.
- A manual xUnit pattern that reads a save from an env var and skips when unset:
  `Source/Parsek.Tests/Analyzer/OfflineAnalyzerTests.cs:252`
  `Manual_AnalyzeEnvSave_WritesReports`.
- Sidecar hashing today: `SidecarVersions.cs:28` `HexLength = 16`, `:36` SHA1.
- The background-thread gate's allowlist is empty (`scripts/background-threads-audit-
  allowlist.txt`, comments only), so S8b's worker thread can exist only on the spike branch.

### 3.3 Stock facts checked without the game

Read from `ikdasm` of `/home/user/ksp-refs/KSP_x64_Data/Managed/Assembly-CSharp.dll`
(KSP 1.12.5). Signatures and field names are reliable; bodies are noisy. Each line names
what the spike still proves in game.

- `SpaceCenter`: private `latitude`, `longitude`, `srfNVector`; public `AreaRadius`, `cb`,
  `spaceCenterAreaTrigger`; public `Start()` computes `cb.GetLatitudeAndLongitude(
  transform.position, false)` and `GetRelSurfaceNVector` once. Calling `Start()` again after
  a move is the refresh (S4b proves it is enough).
- `PQSMod_MapDecalTangent`: public `radius`, `position`, `angle`, `heightMap`, `absolute`,
  `absoluteOffset`, `smoothHeight`, `removeScatter`; `OnSetup`. `PQSCity`: public `lat`,
  `lon`, `alt`, `repositionRadial`, `repositionRadiusOffset`, `reorientFinalAngle`,
  `reorientToSphere`, `launchSite`, `spaceCenterFacility`, `lod`; `OnSetup`,
  `Orientate(bool)`, `ResetCelestialBody`.
- `SpaceCenterCamera2`: public `pqsName`, `initialPositionTransformName`, `altitudeInitial`,
  public `ResetCamera()`.
- Launch sites: `LaunchSite` public constructor; `PSystemSetup.AddLaunchSite(LaunchSite)`,
  `RemoveLaunchSite`, `GetLaunchSite`, `IsFacilityOrLaunchSite`, `StockLaunchSites`,
  `SpaceCenterFacilities`. There are TWO spawn-point classes, `LaunchSite.SpawnPoint` and
  `PSystemSetup.SpaceCenterFacility.SpawnPoint`, each with `latitude`, `longitude`,
  `altitude`, `latlonaltSet` and a public `SetSpawnPointLatLonAlt()`. `LaunchSite` has
  `requiresPOIVisit` / `poiName`; `GameParameters.DifficultyParams.AllowOtherLaunchSites`.
- Mini-biomes: `CelestialBody.MiniBiomes`; `ResearchAndDevelopment.minibiomesbyUnityTag`
  (private static); `Vessel.SetLandedAt(string, GameObject, ...)` tests the collider tag
  against "Untagged" and `PSystemSetup.IsFacilityOrLaunchSite`. The biome map read is
  `CBAttributeMapSO.GetAtt(lat, lon)` (virtual), called by `ScienceUtil.GetExperimentBiome`
  (which multiplies degrees by 0.0174533, so `GetAtt` takes radians),
  `ScienceUtil.GetExperimentBiomeLocalized`, `ResourceUtilities.GetBiome`,
  `Contracts...ReachBiome.checkVesselWithinBiome`, `KerbNetModeBiome` and one vessel
  tracking coroutine. A biome override patched at `GetAtt` covers all six.
- Suits: `ProtoCrewMember.KerbalSuit` Default = 0, Vintage = 1, Future = 2, Slim = 3;
  private `_suit`, `suitTexturePath`, `comboId`; `SuitCombo` (suit type, gender, textures,
  sprite, primary color), `SuitCombos` (`ExtraCombos`, `GetCombo`), config node
  `SUITCOMBOS`. EVA part names: `kerbalEVA`, `kerbalEVAfemale`, `kerbalEVAVintage`,
  `kerbalEVAfemaleVintage`, `kerbalEVAFuture`, `kerbalEVAfemaleFuture`, `kerbalEVASlimSuit`,
  `kerbalEVASlimSuitFemale`. Which GameData folder holds the slim parts is not in the DLL.
- `MainMenu`: public `TextProButton3D` fields (`newGameBtn`, `continueBtn`, `trainingBtn`,
  `scenariosBtn`, ...), private lists `stageOneBtn` / `stageTwoBtn`; `TextProButton3D` has
  public `onTap` / `onPressed`, `SetEnable`, `Lock`.
- `ProtoScenarioModule(ConfigNode)` keeps `moduleValues` and `Save` writes them back;
  `ScenarioRunner.AddModule` logs "Cannot find a Module of typename" when the type is
  missing. Whether the node then survives every stock and Parsek save path is S6's question.
- `FlagSite`: `PlaqueText`, `ReadPlaque()`, `TakeDown`, `PickUp`;
  `KSP.UI.Screens.Flight.Dialogs.FlagPlaqueDialog.Spawn(string siteName, string plaqueText,
  Callback onDismiss)` is public static, so the stock dialog takes supplied text directly.
- `CheatOptions` public static bools: `PauseOnVesselUnpack`, `UnbreakableJoints`,
  `NoCrashDamage`, `IgnoreMaxTemperature`, `InfinitePropellant`, `InfiniteElectricity`,
  `BiomesVisible`, `AllowPartClipping`, `NonStrictAttachmentOrientation`,
  `IgnoreAgencyMindsetOnContracts`, `IgnoreEVAConstructionMassLimit`,
  `IgnoreKerbalInventoryLimits`, `MiddleMouseClickSetPosition`. No gravity field: gravity
  is the `HackGravity` widget's own state.
- Debug cheat widgets (namespace of `HackGravity`): `AllowPartClipping`,
  `BiomesVisibleInMap`, `CheatsCareer`, `CheatsComets`, `CheatsObjectThrower`,
  `HackGravity`, `IgnoreEVAConstructionMassLimit`, `IgnoreKerbalInventoryLimits`,
  `IgnoreMaxTemperature`, `InfiniteElectricity`, `InfinitePropellant`, `MaxProgression`,
  `MiddleMouseClickSetPosition`, `NoCrashDamage`, `NonStrictPartAttachment`,
  `PauseOnVesselUnpack`, `SetOrbit`, `SetPosition`, `UnbreakableJoints`. No type that fills
  resources was found.
- Entry points behind those widgets: `CheatsCareer` max tech -> `ResearchAndDevelopment.
  CheatTechnology()`; science buttons -> `ResearchAndDevelopment.CheatAddScience(float)`;
  max facilities -> `ScenarioUpgradeableFacilities.CheatFacilities()`; max XP ->
  `KerbalRoster.CheatExperience()`; repair -> `ModuleDeployablePart.CheatRepair()`;
  `MaxProgression` -> `ProgressTracking.CheatEarlyProgression()` / `CheatProgression()` ->
  `ProgressNode.CheatComplete()`, which sets the manned / unmanned flags and calls
  `ProgressNode.Complete()`: a milestone completes outside any flight. The console
  (`ConsoleCommands`) adds `funds` / `science` / `reputation` (direct `Set*` calls),
  `rendezvous` (`SetShipOrbitRendezvous`) and `b4d455` (tech, facilities, XP, early
  progression in one command). Every `SetShipOrbit`, `SetShipOrbitRendezvous` and
  `SetVesselPosition` calls `FlightGlobals.PostOrbitSet` once. Other debug screens:
  Physics (`ScreenPhysics` load / save of `PhysicsGlobals`), Difficulty, Kerbal create,
  contract tools, resources (heat).
- `GamePersistence.CreateNewGame(string, Game.Modes, GameParameters, string, GameScenes,
  EditorFacility)` is public static (and is what the `NewGame` verb calls, 3.1).

## 4. Tasks

Common to every spike task below:

- **Code lives** on branch `spike/mp-r0` in worktree `../Parsek-spike-mp-r0`, under
  `Source/Parsek/Spikes/<Sx>/` (C#) or `spikes/r0/<Sx>/` (scripts), never merged.
- **Armed** only when the env var `PARSEK_SPIKE` names the spike (S0); unarmed, the spike
  DLL behaves as `main`.
- **Logs** use the tag `Spike.<Sx>` (`[Parsek][INFO][Spike.S4] ...`); every measurement is
  one log line plus a CSV row.
- **Artifacts**: raw output (KSP.log, CSVs, screenshots, `parsek-test-results.txt`) goes to
  `<umbrella>/logs/spikes/<Sx>/<YYYY-MM-DD>/`; the report and small curated data (CSV and
  PNG, under 2 MB per spike) go to `docs/dev/research/mp-spike-<Sx>-<YYYY-MM-DD>/README.md`
  through a docs-only pull request from a fresh worktree off `origin/main` (template in 6.4).
- **Proof** of a spike is its report: the measurements table against the pass numbers
  stated here, the build it ran (spike commit sha and the deployed DLL hash), and the
  verdict PASS / FAIL / PARTIAL. A spike changes no test on `main`.
- **Registers** for every spike: its report folder; nothing in CHANGELOG, todo, CLAUDE.md
  or the GUI inventory (no player-visible change). S13 writes the results into the design.

### S0 Spike branch and spike instances

- **Change:** worktree `../Parsek-spike-mp-r0`, branch `spike/mp-r0` from `origin/main` at
  the go commit. Add `Source/Parsek/Spikes/SpikeArming.cs`: reads `PARSEK_SPIKE` once (a
  comma list, `S4,S5`) in a `[KSPAddon(Startup.Instantly, true)]` `SpikeHost` that calls
  `DontDestroyOnLoad`, logs `[Spike] armed: <list> build=PARSEK-SPIKE-R0`, and owns a
  Harmony instance `com.parsek.spike` that applies a spike's patches by hand
  (`harmony.Patch(...)`) only when that spike is armed (3.1: attribute patches would apply
  always). `SpikeCsv.cs`: writes rows to `<KSP root>/parsek-spike/<Sx>/<name>.csv` with
  invariant culture. Add `harness/provision/profiles/spike-r0.toml` on the branch: a copy of
  `stock-minimal.toml` with `instanceDir = "automation/spike-r0"` and no graphics overrides
  in `[settings]` (frame-cost spikes need the owner's normal graphics). Add
  `spikes/r0/README.md` naming each spike's entry point and env var.
- **Instances** (the owner runs these; never provisioned autonomously):
  `python provision/provision.py --profile spike-r0 --parsek-dll <spike bin/Debug DLL>`
  from the spike worktree's `harness/`. Then the no-DLC copy for S3: copy the folder to
  `automation/spike-r0-nodlc/` and remove the `SquadExpansion` junction there with
  `rmdir` (cmd) or `Remove-Item` without `-Recurse` on the link itself. A recursive delete
  would follow the junction and delete the dev install's DLC files. Verify with
  `dir /AL` that `Squad` is still a junction and `SquadExpansion` is gone, and that the dev
  install's `GameData/SquadExpansion` still exists.
- **Procedure:** after each spike build, copy the DLL into the spike instance and verify
  the deployed DLL carries the literal `PARSEK-SPIKE-R0` in UTF-16 and the spike's class
  name in UTF-8 (CLAUDE.md "Verify the deployed DLL"); pin the deployed SHA-256 in the
  spike's raw folder.
- **Proof:** one session with `PARSEK_SPIKE` unset logs no `[Spike` line and loads a career
  save; one with `PARSEK_SPIKE=S0` logs the armed line; the no-DLC copy's KSP.log shows
  `MakingHistory` and `Serenity` as not installed.
- **Size:** S. **Depends on:** go. **PC:** yes (provisioning, about 1 hour).
- **Risk:** medium; a careless delete of the no-DLC junction would destroy the dev
  install's DLC content, so the procedure names the exact command and the check after it.

### S1b Finer terrain scan of the three spots

- **Question:** the exact centre (lat, lon) of sites 1 to 3 such that the whole KSC
  footprint (the decal disk measured by S4a, provisional radius 3 km) lies on dry ground at
  the KSC row latitude and the KSC orientation, nearest to each ideal point (105.36E,
  15.36E, 164.64W), and how far the natural ground there deviates from a flat patch.
  Settles the 7.3 site table rows; waits: R2-01 (table values and `SiteRow.LaunchAreas`),
  P1-01's offsets.
- **Change (merges, unlike the rest):** a tooling pull request on `main` that extends
  `harness/tools/kerbin_equator_scan.py` with `--spot LAT,LON --spot-radius-km R
  --spot-step DEG` (a dense square grid around a point, default step 0.001 deg, about
  10 m) and `--row-lat`, plus pure scoring functions: `footprint_dry(grid, centre, radius)`
  (every sample under the disk above sea level by at least 5 m), `ring_jump(grid, centre,
  r_in, r_out, flat_h)` (height difference between the flattened level and the natural
  ground on the ring R to R + 500 m: max, mean, p95), and `best_on_row`. The scoring works
  offline on saved CSVs (`--rescore <dir>`), so S4a's radius can change without a new
  scan. `--selftest` grows cells for the new functions on a synthetic height field.
  `harness/README.md:2817` section gains the new flags.
- **Procedure:** spike instance at the Space Center with kRPC up, machine lock held by
  the owner (no `run.py` / `provision.py` in flight). One degree of Kerbin is about
  10.5 km. (1) Stock KSC grid: `--spot -0.0737,-74.6438 --spot-radius-km 6 --spot-step
  0.002` (about 21 m, about 330,000 samples, about 40 s at the measured 8,000 samples/s;
  0.001 deg is 1.3 million samples, about 3 minutes, if the ring metric needs it).
  (2) Each target: a 1-D pass along the KSC row latitude at 0.005 deg over +-3.5 deg of
  longitude around the ideal, then a dense square at 0.002 deg, radius 6 km, around each
  of the two best row candidates. Total under 15 minutes of game time.
  (3) Offline: rescore with S4a's measured decal radius and the stock KSC's own ring jump
  as the reference.
- **Pass:** for each site a centre on the KSC row with (a) every footprint sample dry,
  (b) ring-jump p95 no worse than 1.5 times the stock KSC's own p95 at the same ring, (c)
  no sea within 1 km of the footprint edge, (d) within 3.5 deg of the ideal. The report
  gives each row: lat, lon, the decal's flatten height, ring metrics, nearest water.
- **Fail / fallback:** if a site has no centre meeting (b), relax (b) to 2.5 times and
  report the visible terrain step; if none meets (a), the design's site moves further from
  the ideal (raise in the report; the 3.4 deg survey margin suggests this will not happen).
- **Artifacts:** `<umbrella>/logs/terrain/<date>/` (the tool's own default); curated CSVs
  and one height-map PNG per site in the report folder.
- **Duration:** half a day of agent work, 1 hour on the PC.
- **Proof:** `--selftest` (no game) in the tooling PR; the report.
- **Registers:** `harness/README.md` section of the tool (same PR). No hlib test file
  exists for the tool; the PR does not add one to a discovery root so CI stays unchanged.
- **Size:** S. **Depends on:** S0 (instance), S4a (decal radius, for scoring only).
  **PC:** yes. **Risk:** low; read-only sampling, re-scorable offline.

### S2 KSC science at a moved site, and none from a copy

- **Question:** do the building mini-biomes, the pad and runway landed-at names and the
  KSC-area biome follow the working KSC when it moves (S4b), what biome a moved site
  reports from the biome map, and does a stripped copy (S5) give none. Settles 7.3 science
  parity and whether a biome override is built. Waits: R2-09, R2-10, R2-15.
- **Change:** `Source/Parsek/Spikes/S2/SpikeS2Science.cs` with in-game tests of category
  `SpikeS2`, `Scene = FLIGHT` or `SPACECENTER`, `AllowBatchExecution = false`:
  `DumpTaggedColliders` (every collider under the KSC transform whose tag is not
  "Untagged": tag, path, world position converted to lat/lon/alt); `SampleBiomeGrid`
  (calls stock `ScienceUtil.GetExperimentBiome(body, lat, lon)` on a 250 m grid +-5 km
  around the stock KSC and around the moved KSC); `ReadLandedAtOnPad` (spawn a probe core
  through `LaunchFromEditor`, read `vessel.landedAt`, `ScienceUtil.GetExperimentBiome` and
  the subject id of a crew report). A prototype override:
  `SpikeS2BiomeOverride` patches `CBAttributeMapSO.GetAtt` by hand (prefix) for Kerbin
  when the point lies within R of the moved centre and returns the attribute at the stock
  KSC plus the same offset; it counts hits per caller (stack frame name, sampled 1 in 100).
- **Procedure:** on the S4b build, Science mode save. (1) At the stock site: run the three
  tests, then a crew report on pad, runway and beside two buildings. (2) Move to site 1
  (S4b). Repeat. (3) Arm the override; repeat (2). (4) With S5's copy at site 2, stand a
  rover on the copy's pad and runway; read `landedAt` and the biome.
- **Pass:** the tag set under the KSC is identical before and after the move and every
  tagged collider keeps its offset from the KSC origin within 1 m; pad and runway subject
  ids equal at both sites; with the override, at least 99 percent of the +-5 km grid
  matches the stock grid shifted by the offset and all six callers reach the override; on
  a copy `landedAt` is empty or the ground biome, never a KSC name.
- **Fallback:** without the override the moved site reports its own map biome: R2 builds
  the override (cost known from this spike). If the override cannot reach a caller (an
  inlined call), restrict it to the science paths and list the rest under disclosure.
- **Artifacts:** CSVs of colliders and grids, crew-report screenshots, KSP.log excerpt.
- **Duration:** 1 day of agent work, 2 hours on the PC.
- **Proof:** report; the in-game test results file.
- **Registers:** report folder only.
- **Size:** M. **Depends on:** S4b (S5 for step 4). **PC:** yes.
- **Risk:** medium; science identity may hide in a third place (a facility name check),
  which the subject-id comparison would show.

### S3 The four looks, replacement sets, a missing DLC, a suit on a ghost kerbal

- **Question:** what each of the four stock suits looks like on the player's own roster;
  the four UI colors; whether two `SUITCOMBOS` replacement sets load; how a game without a
  DLC loads a kerbal saved with that DLC's suit; how to put a suit's texture on a ghost
  kerbal without editing shared materials. Settles 7.4 (table, colors) and A4's shape.
  Waits: R5-08, R5-10, R5-11, R5-12, R4-10; soft: R5-02 (A4), R5-04, R5-07, R5-09.
- **Change:** `Source/Parsek/Spikes/S3/SpikeS3Looks.cs`: tests `ApplySuitToRoster`
  (sets `KerbalSuit` and `comboId` per crew member and saves), `ListSuitCombos` (logs every
  combo: suit type, gender, texture path, from which config), `BuildGhostKerbal` (builds
  the EVA ghost through the existing builder for each of the eight EVA parts and logs part
  found / sphere fallback, build time, material count), and a prototype A4 swap that clones
  the material per ghost (`renderer.materials`, never `sharedMaterials`, see
  `GhostVisualBuilder.PartInfoBuilders.cs:766`) and sets the suit texture. Two test
  `SUITCOMBOS` config files in `spikes/r0/S3/gamedata/` (copied by hand into the spike
  instance's GameData, never the dev install), each with the stock layout and a recolored
  texture.
- **Procedure:** (1) Spike instance (DLCs present): four kerbals, one per suit, IVA, EVA,
  and astronaut complex screenshots; pick four colors by sampling the suit main color from
  the screenshots. (2) Install the two combo sets; check the suit picker and the loaded
  count. (3) Save; copy the save to the no-DLC instance; load; log each kerbal's suit,
  combo and what the EVA part resolves to; go EVA with the Vintage kerbal. (4) Ghost
  kerbals: play a recorded EVA (fixture `eva2-lko-crewed`) in each suit on both instances.
- **Pass:** all four suits show; four colors with pairwise contrast ratio at least 3:1
  against the map background and each other's hue at least 30 deg apart; both combo sets
  load (ExtraCombos grows by the set size); the no-DLC load keeps the save loadable and
  logs a deterministic fallback suit (the report names it); the ghost swap gives the right
  texture with no change to the prefab's shared material (its instance id and texture
  unchanged after 10 ghost builds) and adds at most 2 ms per ghost build.
- **Fallback:** if the no-DLC game drops or rewrites the suit on load, the race stores the
  look separately (`player.cfg`) and re-applies it; if a DLC EVA part is missing, the ghost
  uses the base suit model with the color only (A4 records the substitution).
- **Artifacts:** 16 or more screenshots, the combo log, ghost build timings.
- **Duration:** 1 day of agent work, 3 hours on the PC across two instances.
- **Proof:** report with the color table and screenshots.
- **Registers:** report folder only.
- **Size:** M. **Depends on:** S0 (both instances). **PC:** yes.
- **Risk:** low; looks are cosmetic and every failure has a working fallback.

### S4a Probe: where the KSC is and what reads it

- **Question:** the KSC `PQSCity` origin, its decal's fields, the facility and launch-site
  spawn points, the pad and runway footprints, and the order of the scene and save events
  on each way into a save. Feeds S1b (decal radius, true origin), S4b (what to move, when),
  P1-01 (offsets), R2-01's `SiteRow.LaunchAreas` (7.7, OV-41) and the P1-03
  VERIFY. Waits: R1-04 and R2-01 (is the new `Game` set at the scene load request, OV-51),
  R2-05 and P1-03 (does the circle cache hold the KSC pad and runway, OV-52).
- **Change:** `Source/Parsek/Spikes/S4/SpikeS4Probe.cs`, test `DumpKscState`
  (SPACECENTER): logs every `PQSCity` and `PQSMod_MapDecalTangent` on Kerbin (name, all
  public fields of 3.3), `SpaceCenter.Instance` latitude / longitude (by reflection),
  `SpaceCenterCamera2` fields, every `PSystemSetup.SpaceCenterFacilities` entry and every
  `LaunchSite` with each spawn point's lat / lon / alt and `requiresPOIVisit`; samples the
  pad and runway collider bounds (world corners to lat / lon) for the KSC and each Making
  History site; after `LaunchSiteExclusionZones.GetAltSiteCircles` has run once, reads
  `cachedRuntimeCircles` (`LaunchSiteExclusionZones.cs:130`) by reflection and logs each
  circle's site name, centre and radius. `SpikeS4EventOrder` (armed by `PARSEK_SPIKE=S4a`):
  logs, with frame number and `HighLogic.CurrentGame` title, `onGameSceneLoadRequested`,
  `onGameStateLoad`, `onGameStatePostLoad`, `onLevelWasLoaded`, `SpaceCenter.Start` (a
  hand-applied postfix), `PQSCity.OnSetup` for "KSC", and `ParsekScenario.OnLoad` entry.
- **Procedure:** (1) Main menu -> Resume Saved; (2) main menu -> New Game; (3) seam
  `LoadGame` from the Space Center into a different save, so the main menu is bypassed (the
  race to single-player route of OV-51); (4) quickload in flight; (5) revert to launch.
  After each, run `DumpKscState`. With and without `AllowOtherLaunchSites`.
- **Pass:** this is a measuring spike: it passes when every field above is in the CSV and
  the event order is logged for all five ways in. The report answers, each with a yes or
  no per way in:
  - does `PQSCity.OnSetup` for the KSC run before `onGameStateLoad` (if yes, a move must
    re-run it);
  - is the new `Game` already in `HighLogic.CurrentGame` at `onGameSceneLoadRequested`.
    R1-04's `ParsekRaceBoot` decides the race gate there, and its one scene-request
    handler `ParsekRaceBoot.OnSceneRequest` calls `RaceSites.Reconcile` (filled in by R2-01)
    on every request, the main menu included (OV-51); R2-04 applies at that point. If the
    answer is no on a way in, the report names the first event at which it is set (R1-04's
    fallback is `onGameStatePostLoad`);
  - does the circle cache hold the KSC's own `LaunchPad` and `Runway` (the P1-03 VERIFY).
    If yes, R2-05 filters or rebuilds them when the home moves (OV-52); if no, R2-05 needs
    no filter.
- **Fallback:** none needed; a missing field is read with a debugger-free reflection dump.
- **Artifacts:** `ksc_state.csv`, `event_order.csv`, footprints CSV.
- **Duration:** half a day of agent work, 1 hour on the PC.
- **Proof:** report; event order table.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S0. **PC:** yes.
- **Risk:** low; read-only.

### S4b Move the working KSC

- **Question:** can the working KSC move to a site from S1b (provisional coordinates from
  the survey until S1b lands) and give a working launch, recovery and Space Center visit,
  with Parsek's own readers behaving, and can it move back. Settles the 7.3 moving
  checklist and the hook; waits: R2-03, R2-04, R2-08, R2-11, R2-15.
- **Change:** `Source/Parsek/Spikes/S4/SpikeS4Move.cs`, armed by `PARSEK_SPIKE=S4b` with
  `PARSEK_SPIKE_SITE=<lat>,<lon>`. Order (to check against KSCSwitcher's source on the PC,
  which the report cites): set the KSC `PQSCity` reposition fields so its origin is the
  site; `OnSetup` and `Orientate`; move the decal (`position`, `absoluteOffset` from S4a's
  flatten height rule: natural height at the site); rebuild the Kerbin PQS; call
  `SpaceCenter.Instance.Start()`; for every facility and launch site of the KSC call
  `SetSpawnPointLatLonAlt()` on both spawn-point classes; `ResetCamera()` on the Space
  Center camera. Applied at the hook S4a found earliest; logs each step with time taken. A
  `Restore` path reverses it with the values captured before the move. A check pass logs
  what Parsek reads: `SpawnCollisionDetector` constants (3.2, still stock: expected
  wrong), `LaunchSiteExclusionZones` cached circles (expected stale), the recorded
  `LaunchSiteName` of a launch, the optimizer's departure rule result on the launch's
  recording, and KSC-scene ghost positions of a recording made at the site.
- **Procedure:** career save, site 1. (1) Move; enter Space Center: camera, click every
  building, day and night (warp 6 hours). (2) Launch a probe from the pad and an aircraft
  from the runway (seam `GoToEditor`, `LaunchFromEditor`); fly 60 s; recover each 1 km
  away and on the pad. (3) Space Center again; restore; enter flight at the stock pad.
  (4) Three move-restore cycles in one process. (5) Sites 2 and 3 once each.
- **Pass:** pad and runway spawn within 5 m of the moved spawn point and the vessel
  stays intact for 60 s; Space Center camera starts within 200 m of its stock relative
  position; every building opens; recovery funds within 1 percent of the same recovery at
  the stock site; apply takes under 2 s; after restore, heights at 20 fixed points equal
  the stock heights bit for bit; three cycles with no exception in KSP.log.
- **Fallback:** if a move after the scene builds fails but one before the PQS setup works,
  R2 moves at that earlier hook (the report names it). If no in-process move works, the
  options are a Kopernicus-style config dependency or a new launch site with the Space
  Center left at the stock KSC; the second breaks owner ruling "every player's home is a
  full KSC" and goes to the owner.
- **Artifacts:** step timings CSV, readers CSV, screenshots of each site.
- **Duration:** 2 days of agent work, 3 hours on the PC.
- **Proof:** report; KSP.log excerpts of each step.
- **Registers:** report folder only.
- **Size:** M. **Depends on:** S4a. **PC:** yes.
- **Risk:** high; it is the largest unknown of the race world, and KSCSwitcher's order may
  rely on a hook Parsek does not have.

### S5 KSC copies as scenery, cost, terrain parity

- **Question:** can a copy of the KSC buildings at full level stand at another site,
  solid but inert (no facility, no click, no destruction, no science, no launch-site
  trip); what three copies cost; does a cloned decal give the same heights in two
  processes on one PC (that is the gate; a second machine is a cross-check when a second
  tester exists, DC-091). Settles 7.3 scenery and the 15.13 scenery row; waits: R2-02,
  R2-03, R2-06, R2-07, R2-15.
- **Change:** `Source/Parsek/Spikes/S5/SpikeS5Scenery.cs`: `CloneKsc(siteLat, siteLon)`
  instantiates the KSC hierarchy at the max-level prefabs; strips `UpgradeableFacility`,
  `DestructibleBuilding`, click handlers, `SpaceCenter` and its area trigger; sets every
  collider tag to "Untagged"; clones the decal mod at the site. Tests `MeasureClone`
  (time, `Profiler.GetTotalAllocatedMemoryLong` before and after), `FrameCost` (mean and
  p99 of `Time.unscaledDeltaTime` over 600 frames at a fixed camera, copies hidden vs
  shown), `HeightProbe` (writes `pqs.GetSurfaceHeight` at 50 fixed points per site).
- **Procedure:** (1) Space Center at site 0, build three copies at sites 1 to 3; measure.
  (2) Flight: fly a probe over each copy, land on its pad (seam `DriveVessel` for a
  rover), run `LaunchSiteProbe` from the editor at site 0 to confirm the copy did not add
  a launch site. (3) Crash a probe into a copy building. (4) Two processes on one PC: run
  `HeightProbe` in the spike instance and in the no-DLC copy (or two runs of the same
  instance) and diff the CSVs. A run on a second machine, when one exists, is a
  cross-check reported beside the gate, not part of it.
- **Pass:** clone time under 3 s for three copies; added memory under 150 MB; frame time
  with copies in view at most 1 ms above copies hidden at p99; `landedAt` on a copy is not
  a KSC name; a crash gives terrain-crash behaviour with no facility damage logged; heights
  identical to the last bit at all 150 points across the two processes on one PC (R2-02
  takes this reading). These budgets are
  proposed here; the owner may move them (they are the 15.13 numbers).
- **Fallback:** over budget: simplified meshes and box colliders, a copy built only when
  the camera is within 50 km (15.13 already allows "a copy far from the camera is not
  built"), or merged meshes. Heights differ: ship explicit height data instead of cloning
  the decal.
- **Artifacts:** cost CSV, height CSVs, screenshots.
- **Duration:** 2 days of agent work, 3 hours on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** M. **Depends on:** S4a; S4b for the decal move code. **PC:** yes.
- **Risk:** medium; a stock component that survives the strip (a hidden trigger) would
  show up only in the landed-at and crash checks, which the procedure runs.

### S6 Race mark survival, main menu entry, create a save

- **Question:** does a scenario node whose module type is not installed survive every way
  a save is rewritten (stock and Parsek), what is the earliest hook that can read it, can
  a main menu entry be cloned with keyboard navigation, can a race save be created and
  started from a typed dialog. Settles 6.2 and 7.2; waits: R1-03, R1-10, R1-14.
- **Change:** (a) a template save: `spikes/r0/S6/template/` holding a career
  `persistent.sfs` with an extra `SCENARIO { name = ParsekRaceSpikeMark  scene = 5, 7, 8
  race { id = spike-1  slot = 2 } }`, staged by hand into the spike instance's `saves/`.
  (b) A seam lane on the spike branch `harness/scenarios/SPIKE-S6-mark-survives.toml`
  (driver seam: `LoadGame`, `SaveGame`, quicksave and quickload, a launch and a Revert,
  `InvokeRewindToLaunch`, `FlushAndQuit`; tier operator), plus a Python checker
  `spikes/r0/S6/check_mark.py` that diffs the node in every written file
  (`persistent.sfs`, `quicksave.sfs`, rewind-point `.sfs`). (c)
  `Source/Parsek/Spikes/S6/SpikeS6Menu.cs`: a `MAINMENU` addon that clones `scenariosBtn`
  into a "Multiplayer" entry, inserts it into the private `stageTwoBtn` list by reflection,
  and opens a `MultiOptionDialog` with a `DialogGUITextInput` (folder path, paste with
  Ctrl+V) and a Create button that calls `GamePersistence.CreateNewGame`, adds a
  `ProtoScenarioModule` for the mark before `HighLogic.CurrentGame.Start()`, and starts
  the game (reusing the `NewGame` verb's sequence, 3.1). Logs the earliest hook at which
  the mark is readable from `HighLogic.CurrentGame.scenarios`.
- **Procedure:** run the lane once with Parsek's normal DLL path and once with
  KSPCommunityFixes removed from a copy of the instance (KSPCF patches save code and the
  dev install runs it). Then by hand: Discard Re-fly (`RevertInterceptor` writes
  persistent.sfs), main menu -> Multiplayer -> create -> quit -> Resume Saved.
- **Pass:** the node is byte-identical (after whitespace normalisation) in every file
  written by every path, with and without KSPCF; the menu entry shows, takes a mouse click
  and arrow keys plus Enter; the dialog's text field takes a pasted 200-character path;
  the created save loads with the mark present at the reported hook.
- **Fallback:** the node is dropped by a path: the mark moves to a side file in the save
  folder or into Parsek's own scenario node (raise to the owner, since 6.2 chose a
  separate module so older builds keep it). Menu clone fails: an IMGUI button on the main
  menu, drawn through the UI-scale host.
- **Artifacts:** checker diff output, lane result JSON, screenshots of the menu and dialog.
- **Duration:** 1 day of agent work, 2 hours on the PC.
- **Proof:** report; lane result.
- **Registers:** report folder only (the lane exists only on the spike branch).
- **Size:** M. **Depends on:** S0. **PC:** yes.
- **Risk:** medium; a Parsek path that rebuilds the scenario list (the crash reconcile,
  a rewind save copy) is the likeliest loser, and the checker reads every file it writes.

### S7 Flag replica with the stock plaque dialog

- **Question:** can a rival's flag be drawn as a replica (flag model, flag texture, click
  target) that is not a vessel, built the way the code builds a flag, and open the stock
  plaque dialog with supplied text; what click range feels right. Settles 7.7 readable
  flags and how R3W-24 builds the model; waits: R3W-24, R3W-25.
- **Change:** `Source/Parsek/Spikes/S7/SpikeS7FlagReplica.cs`. The `flag` part has no
  `.mu` mesh to clone: its meshes are generated at runtime by the part's
  `FlagDecalBackground`, which is why `GhostVisualBuilder.SpawnFlagVessel`
  (`GhostVisualBuilder.cs:1797-1803`) spawns a real flag vessel instead of cloning a model
  (OV-55). The spike therefore builds the replica the way the code builds a flag, in two
  variants, and keeps the one that works:
  - (a) `FlagDecalBackground` outside a vessel: instantiate the `flag` part prefab as a
    plain GameObject (no `Vessel`, no `ProtoVessel`, no `FlagSite` state), set the flag
    URL, let `FlagDecalBackground` generate the meshes, then strip every component but the
    renderers (log which components the prefab carries and which had to go);
  - (b) a real flag vessel as the source: spawn one through the same `ProtoVessel` path
    `SpawnFlagVessel` uses, copy the meshes, materials and transforms `FlagDecalBackground`
    built into a plain GameObject, then remove the vessel in the same frame, before any
    save can see it.
  On the kept replica it sets the texture from a flag URL, adds a trigger collider and a
  click handler (`OnMouseUpAsButton` with the camera's `queriesHitTriggers` checked) that
  calls `FlagPlaqueDialog.Spawn(siteName, plaqueText, onDismiss)`. Test
  `PlaceReplicaNearActive` (FLIGHT) places it 20 m from the active vessel at ground height,
  with a variant argument `a` or `b`.
- **Procedure:** for each variant: place a replica; compare it on screen with a real
  planted flag beside it; check `FlightGlobals.Vessels` and the next save for it (for (b),
  also that the source vessel is gone and no `FlagSite` part remains); click it from 2,
  10, 50 and 100 m on foot and from a rover; drive a rover through it; fly 10 km away and
  back (floating origin shifts); place one at the moved site of S4b.
- **Pass:** a variant passes when the replica looks like the real flag beside it (pole,
  flag and texture); the dialog shows the supplied site name and text; the replica is in
  no vessel list and no save; it stays within 0.1 m of its lat / lon / alt after 10 km of
  travel; a rover passes through with zero contacts. The report names the variant R3W-24
  builds with (prefer (a): no vessel ever exists) and recommends a click range from the
  four distances (provisional: clickable within 50 m).
- **Fallback:** neither variant gives the flag model: a simple pole and quad carrying the
  flag texture, raised to the owner as a look change of 7.7. The stock dialog cannot take
  the text: a `PopupDialog` with the same layout; clicks unreliable: a proximity prompt
  (on EVA within the range).
- **Artifacts:** screenshots (replica beside a real flag, per variant), the component list
  of the flag prefab, position drift CSV.
- **Duration:** 1 day of agent work, 2 hours on the PC (two build variants).
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S0, S4b (the last step only). **PC:** yes.
- **Risk:** low for the dialog (its entry point is public, 3.3); medium for the model,
  since the flag part has no mesh file and `FlagDecalBackground` may need a vessel to run.

### S8a File access cost on sync clients (no game)

- **Question:** how long a file open, read, stat, listing and rename take in a folder kept
  by a sync client, for files that are local, cloud-only (placeholder) and being synced,
  and on an unreachable network share. Settles 15.9: is a copy worker thread needed (the
  owner's yes), and the poll numbers. Waits: R6-10, R3E-23 (the copy worker); soft:
  R3E-16 (the copy-out), T-02.
- **Change:** `spikes/r0/S8/fileaccess.py` (stdlib, Windows): for sizes 10 KB, 1 MB,
  10 MB creates files, waits for upload, sets them cloud-only (the client's own "free up
  space"), then times `os.stat`, `os.scandir`, first `open`, first 4 KB read, full read,
  `os.replace` over an existing file, and reads `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`
  (0x400000), `RECALL_ON_OPEN` (0x40000) and `OFFLINE` (0x1000) via `os.stat().
  st_file_attributes`. Also: open on `\\10.255.255.1\x` (unreachable) and on a share whose
  host is asleep; rename while the client holds the file; how conflict copies are named.
  Writes `<umbrella>/logs/spikes/S8/<date>/fileaccess.csv`.
- **Procedure:** owner installs or uses the clients chosen (question 10.1), points each at
  a test folder, runs the script three times per client (cold after a reboot, warm,
  during a large upload).
- **Pass (decision rule, both outcomes are answers):** if any operation the copy step does
  (stat, list, open, read of a payload up to 10 MB) blocks over 250 ms in any client, and
  the attribute check cannot predict it, the copy step needs a worker thread (owner's yes
  to the one allowlisted site, 15.9). If every operation is under 50 ms once the
  attributes show "local", and the attributes are always readable without a block, the
  main thread may do it with an attribute check first.
- **Fallback:** none needed; the answer is the measurement.
- **Artifacts:** CSV per client, the conflict-name list.
- **Duration:** half a day of agent work, 2 hours on the PC.
- **Proof:** report with a table per client and the decision.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S0 (only the branch). **PC:** yes (Windows and the clients).
- **Risk:** low; plain script, no game.

### S8b A blocking read in KSP versus a worker thread

- **Question:** what a blocking file read does to KSP's frame time, and whether a worker
  thread with a main-thread queue keeps frames smooth under Unity's Mono. Confirms S8a's
  decision in the game.
- **Change:** `Source/Parsek/Spikes/S8/SpikeS8Io.cs`: test `MainThreadRead` (reads a list
  of files with `File.ReadAllBytes` in `Update`, one per frame) and `WorkerRead` (a
  `Thread` reads into a `ConcurrentQueue` that `Update` drains; the thread only reads files
  and touches no Unity or KSP object). The background-thread gate does not run on the spike
  branch; S13 notes that the real worker needs the allowlist entry.
- **Procedure:** in flight with the owner's normal graphics, point at a sync folder with
  cloud-only files and at the unreachable share; 600 frames each.
- **Pass:** worker variant p99 frame time within 5 ms of the idle baseline in every case;
  record the main-thread variant's worst frame for the report.
- **Fallback:** if the worker stalls frames too (a Mono or file-system lock), the copy step
  runs between scenes only (a loading screen), raised to the owner.
- **Artifacts:** frame-time CSVs.
- **Duration:** half a day of agent work, 1 hour on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S8a. **PC:** yes. **Risk:** low.

### S9 Which debug actions a mod can observe

- **Question:** for every stock debug action and switch, can Parsek see it happen, once per
  press, with no false positives from normal play; and for every way a milestone completes
  outside a flight, can a mod observe it. Settles the 7.12 tool list, its out-of-reach
  list and the disclosure wording; waits: R3E-09, R6-01. Owner decision D3 (design 13.A)
  already rules that a debug tool means no score (OV-34): R3E-09 records a save-level
  debug-completion fact for every completion S9 shows a mod can observe (persisted in
  R3E-15's race facts child), the standing carries it as `DISCLOSURE` rows
  `DEBUG_COMPLETION` with the milestone id and UT (codec member
  `Standing.DebugCompletions`), and R6-01 drops those rows from the board and gives the
  player the `DebugCompleted` mark. A completion S9 cannot observe is listed as out of
  reach in design 7.12.
- **Change:** `Source/Parsek/Spikes/S9/SpikeS9Cheats.cs`: hand-applied postfixes, one log
  line each (`[Spike.S9] observed: <entry> args=...`), on `ResearchAndDevelopment.
  CheatTechnology`, `CheatAddScience`, `ScenarioUpgradeableFacilities.CheatFacilities`,
  `KerbalRoster.CheatExperience`, `ProgressTracking.CheatProgression`,
  `CheatEarlyProgression`, `ProgressNode.CheatComplete`, `ModuleDeployablePart.CheatRepair`,
  `FlightGlobals.PostOrbitSet` (already observed by `CheatTeleportPatch.cs:16`), the
  console's `funds` / `science` / `reputation` commands (patch the `ConsoleCommands`
  handlers), the contract tools buttons, `ScreenPhysics` load, the difficulty screen's
  apply, `CheatsComets` and `CheatsObjectThrower` actions; a 1 Hz poll that logs any change
  of the `CheatOptions` statics (3.3) and of `HackGravity.gravityFactor` (as
  `GravityHackDetector.cs:81` reads it). Plus a normal-play control: the same patches stay
  armed through a scripted ordinary career session.
- **Procedure:** (1) Press each debug action N = 3 times; toggle each switch on and off.
  (2) Run the control session: a launch, a contract accept and complete, a science
  transmit, a facility upgrade bought normally, a tech bought normally, a recovery, a
  quickload. (3) Use the console `b4d455`, `funds 1000000`, `rendezvous`. Each way that
  completes milestones runs from a fresh career copy, so its nodes are not already done.
- **Pass:** every action logs exactly N lines for N presses; the control session logs zero
  `observed` lines; the report's table classifies each tool: observed by an entry point,
  observed by polling only (with the delay), not observable. A second table lists every
  way a milestone completes outside a flight (`MaxProgression` through
  `CheatEarlyProgression` / `CheatProgression`, `ProgressNode.CheatComplete`, the console's
  `b4d455`, the legacy cheat window, and any other caller of `ProgressNode.Complete` the
  presses reach): for each, the progress node ids it completed (the milestone id, UT and
  action a `DEBUG_COMPLETION` row of R3E-09 carries), whether a mod observes it
  (by which entry point or event, at which UT) and whether it fires the same `GameEvents`
  a real completion fires (the A3 risk of 9.2). This table is R3E-09's list of observable
  completions and design 7.12's out-of-reach list (S13 writes the latter).
- **Fallback:** an action with no clean entry point: observed by its effect (a ledger
  jump with `TransactionReasons.Cheating`, `GameStateRecorder.cs:1333`) or listed as not
  observable in the disclosure. Mod teleports (HyperEdit and similar) are out of reach and
  stay a disclosure line.
- **Artifacts:** the observed-lines CSV, the classification table.
- **Duration:** 1 day of agent work, 2 hours on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** M. **Depends on:** S0. **PC:** yes.
- **Risk:** low; patches only log, and the callers were checked in the decompile.

### S10a Count the relative stretches and their body-fixed copies (no game)

- **Question:** in real saves, how many `Relative` track sections have a complete
  body-fixed copy (at least two samples covering the section), a partial one, or none, by
  recorder source (active, background, parent-anchored debris, docking, EVA). Settles
  whether 15.5's "fewer than two samples: not drawn" rule is rare; waits: R3W-04.
- **Change:** a manual xUnit test on the spike branch,
  `Source/Parsek.Tests/Spikes/RelativeCopyCensusTests.cs`, shaped like
  `OfflineAnalyzerTests.cs:252` (reads `PARSEK_SPIKE_S10_SAVE`, skips when unset): loads
  every recording sidecar of the save through the production codec, and for every
  `Relative` section counts `frames`, `bodyFixedFrames`, coverage of the section's UT range
  and the recording's flags; writes `relative_census.csv` to the folder named by
  `PARSEK_SPIKE_OUT` (under `<umbrella>/logs/spikes/S10/`).
- **Procedure:** run over every committed fixture save with recordings (docking:
  `bdock-recorded`, `bdock-recorded-career`, `bdock-second-dock-recorded`; EVA:
  `eva2-lko-crewed`, `eva-construct-pad`; `mun-landing-recorded`) in a cloud session, then
  over the owner's own career saves on the PC (read-only: the test opens copies).
- **Pass (decision rule):** if at least 95 percent of relative seconds in the owner's saves
  have a complete copy, R3W-04 keeps "not drawn" for the rest. If fewer, the report names
  the producers that skip the copy (the seam points of `FlightRecorder.PointCommit.cs`
  pass none, 3.2) and R3W-04 needs either a recorder change on `main` first (a new task
  for the owner to place) or the small anchor resolver.
- **Fallback:** none needed; the answer is the count.
- **Artifacts:** census CSV per save.
- **Duration:** half a day of agent work; 30 minutes on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S0 (branch only). **PC:** only for the owner's saves.
- **Risk:** low; read-only.

### S10b How far a flattened stretch drifts from its partner

- **Question:** when a docking or an EVA beside a lander plays from body-fixed copies
  instead of the anchor, how far apart do the two ghosts drift at contact. Settles 15.5's
  accepted error; waits: R3W-04.
- **Change:** `Source/Parsek/Spikes/S10/SpikeS10Flatten.cs`: under
  `PARSEK_SPIKE=S10b`, a hand-applied patch forces the candidate check at
  `GhostPlaybackEngine.SectionPositioning.cs:118` to true for every recording (so each
  relative section plays from its body-fixed copy when it has one); a test logs, every
  frame of the contact window, the world distance between the two docking ports (part
  persistent ids from the fixture) or between the kerbal and the lander hatch, for the
  normal and the flattened playback.
- **Procedure:** fixture `bdock-recorded` (docking) and `eva2-lko-crewed` (EVA) on the spike
  instance; watch each contact twice (normal, flattened), the fixture's own ghosts through
  the existing single-player Watch (no rival watching, which waits with P3b);
  `CaptureScreenshot` at contact.
- **Pass:** flattened port-to-port distance within 0.5 m of the normal playback's at
  contact and frame-to-frame jitter under 0.05 m; no visible gap in screenshots at 10 m
  camera distance.
- **Fallback:** drift too large: a small anchor resolver over one player's own flights
  (rivals' flights resolve their anchor inside the same player's tree), or a general
  anchor resolver (size L, raised in the report).
- **Artifacts:** distance CSVs, contact screenshots.
- **Duration:** half a day of agent work, 1 hour on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S10a. **PC:** yes. **Risk:** medium; the forced path may
  hit a section the production code never sends there and break for an unrelated reason,
  which the normal run beside it separates.

### S11 Ghost cost per frame at race scale (new)

- **Question:** what N ghosts cost per frame in flight and map view, so R3W-14 (A5) can set
  the cap of its ghost budget for "up to three more players' worth of ghosts" (15.13
  per-frame row). Rivals are seen on the map, in the Tracking Station and as ghosts nearby;
  watching a rival flight is deferred with P3b, so no step here uses the Watch camera.
- **Change:** test `GhostCostSweep` in `Source/Parsek/Spikes/S11/SpikeS11Cost.cs`: with
  synthetic recordings injected into a copy save (`InjectAllRecordings` pattern, into the
  spike instance's save only), measures mean and p99 frame time with 0, 8, 16, 32 and 64
  ghosts visible near the camera and in map view, plus the first-open time of one
  trajectory sidecar (the lazy open of 7.6) on the main thread.
- **Procedure:** owner's normal graphics; 600 frames per step; nothing else running.
- **Pass:** a measuring spike: the report gives ms per ghost (slope) and the first-open
  time; the cap itself is set in R3W-14 from these numbers.
- **Fallback:** none.
- **Artifacts:** cost CSV.
- **Duration:** half a day of agent work, 1 hour on the PC.
- **Proof:** report.
- **Registers:** report folder only.
- **Size:** S. **Depends on:** S0. **PC:** yes. **Risk:** low.

### S12 Hashing and signing inside KSP's Mono (new, optional)

- **Question:** does `System.Security.Cryptography` SHA-256, `RSACryptoServiceProvider`
  and `ECDsa` work in KSP 1.12.5's Mono, how fast, and is the key container writable.
  Only R7 (signed standings) waits on it.
- **Change:** test `CryptoProbe` in `Source/Parsek/Spikes/S12/SpikeS12Crypto.cs`: SHA-256
  of 1 MB and 10 MB buffers; RSA 2048 and ECDsa P-256 key creation, sign and verify of a
  1 KB blob; export and import of the key as XML / parameters; each wrapped so a
  `PlatformNotSupportedException` is logged, not thrown.
- **Procedure:** rides in the S8b session.
- **Pass:** SHA-256 over 10 MB under 100 ms; at least one signing algorithm creates,
  signs, verifies and round-trips its key with no exception.
- **Fallback:** a managed implementation shipped in the mod (R7 sizes it).
- **Artifacts:** results lines in the S8 raw folder.
- **Duration:** 2 hours of agent work, 10 minutes on the PC.
- **Proof:** report section inside the S8 report.
- **Registers:** none beyond S8's report.
- **Size:** S. **Depends on:** S0. **PC:** yes. **Risk:** low.

### S13 Close-out: results into the design

- **Change:** one docs pull request from a fresh worktree off `origin/main`: design section
  12 "Still to prove" cells replaced by each spike's verdict and report link; the 7.3 site
  table rows from S1b and S4a; the 7.4 colors from S3; the 7.12 tool list from S9, with
  the milestone completions S9 found a mod cannot observe listed as out of reach; the
  15.9 decision from S8; the 15.13 budget numbers from S5 and S11; every correction of
  section 9 below that a spike confirmed. Notes that the spike branch stays unmerged.
- **Proof:** the reports it links exist; `test_doc_spec_sync.py` (it reads `docs/dev`)
  stays green.
- **Registers:** design doc; `scripts/build-design-pdfs.py` rerun if the design is in its
  `DOCS` table.
- **Size:** S. **Depends on:** every spike report. **PC:** no.
- **Risk:** low; docs only.

## 5. Parallel tracks

Sessions (one KSP process each, on the spike instance unless named):

| Session | Spikes | Game | Notes |
|---------|--------|------|-------|
| A | S8a, S10a (fixtures), S1b tooling PR, KSCSwitcher source read | none | agent work; S8a needs the owner's PC but no KSP |
| B | S1b scan | spike-r0, kRPC | needs kRPC ports 50000 / 50001 |
| C1 | S4a, then S4b, S2, S5 | spike-r0 | one process, Space Center and flight |
| C2 | S6, S7, S9 | second copy (spike-r0-nodlc is fine; S6 lane can use any) | no kRPC needed; seam only |
| C3 | S3 | spike-r0 then spike-r0-nodlc | needs both instances |
| C4 | S10b, S8b with S12, S11 | spike-r0 | frame-cost sessions run with nothing else open |

Which run at the same time on two or three game copies:

- B (kRPC) on one copy while C2 (seam, no kRPC) runs on another. Two kRPC sessions cannot
  share the fixed ports, so at most one kRPC session at a time.
- C1 and C2 on two copies once S4a is done.
- Never in parallel: S5's frame cost, S8b and S11. Their numbers need an otherwise idle PC.
- The harness machine lock covers `run.py` and `provision.py` only; spike sessions launched
  by hand do not take it, so the owner does not run a harness tier while a spike session
  runs (the zombie preflight would refuse it anyway while a KSP process is alive).

Agent work in parallel (different files on the spike branch, so no conflicts):
`Spikes/S2`, `S3`, `S4`, `S5`, `S6`, `S7`, `S8`, `S9`, `S10`, `S11`, `S12` are separate
folders. Hot files: `Source/Parsek/Spikes/SpikeArming.cs` (S0 only; every spike registers
its patches through one list there, so S0 lands first and each spike adds one line);
`harness/tools/kerbin_equator_scan.py` (S1b alone, on `main`).

Order that unblocks the most downstream planning:

1. Day 1: S0; S4a; S6; S9; S8a; S10a; S1b scan (raw grids).
2. Day 2: S4b; S1b scoring with S4a's radius; S2 steps 1 to 3.
3. Day 3: S5; S7; S2 step 4 (it needs S5's copy).
4. Day 4: S3; S10b; S8b with S12; S11.
5. Then S13.

S4a and S6 first because R2 and R1 are the first feature phases and their hooks depend on
them (S4a: R1-04, R2-01, R2-05, P1-03; S6: R1-03); S9 early because the A3 capture
(R3E-09) lands in R3 and cannot be rebuilt later. S7 runs after S4b (day 2), which its
last step needs.

## 6. Tests and proof

### 6.1 Unit tests

None on `main`. The spike branch carries one manual xUnit test (S10a) that skips unless
its env var is set, and the S1b tooling PR extends the scan tool's `--selftest`.

### 6.2 In-game tests

Categories `SpikeS2`, `SpikeS3`, `SpikeS4`, `SpikeS5`, `SpikeS7`, `SpikeS8`, `SpikeS9`,
`SpikeS10`, `SpikeS11`, `SpikeS12`, all `AllowBatchExecution = false`, run one at a time
through Ctrl+Shift+T or the `RunTests` seam verb; results land in
`parsek-test-results.txt`, copied to the spike's raw folder. They exist only on the spike
branch, so no committed batch tally moves (the hlib tally gate reads `main`).

### 6.3 Harness

One spike lane, `SPIKE-S6-mark-survives`, on the spike branch only, run with
`run.py --id SPIKE-S6-mark-survives` against the spike instance by the owner. S1b uses the
existing scan tool. Nothing lands in `harness/scenarios/` on `main`.

What needs the owner's PC: every spike except S10a over committed fixtures and the S1b
tooling PR. S8a needs the PC and its sync clients but no KSP.

### 6.4 Spike report template

One page, `docs/dev/research/mp-spike-<Sx>-<YYYY-MM-DD>/README.md`:

```
# Spike <Sx>: <question in a few words> (<YYYY-MM-DD>)

Verdict: PASS | FAIL | PARTIAL. One sentence.

## Question
<the question from r0-spikes.md, verbatim>

## Decides
<design section and the downstream task ids that waited>

## Build
spike/mp-r0 at <sha>; deployed DLL sha256 <hash>; instance <spike-r0 | spike-r0-nodlc>;
KSP 1.12.5, DLCs <yes/no>, mods <list from the instance manifest>

## Procedure as run
<numbered steps; any change from the plan says why>

## Measurements
| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|

## What the design must change
<section, old wording, new wording; or "nothing">

## Fallback taken
<none, or which one and why>

## Raw artifacts
<umbrella>/logs/spikes/<Sx>/<YYYY-MM-DD>/ (list the files); curated files in this folder

## Follow-ups
<new todo or owner question, if any>
```

## 7. Gates and registrations

- Architecture map, layers, forbidden edges, lifetimes, headless pins: not touched on
  `main`. On the spike branch the arch suite would red on the new `Spikes/` files (no
  `modules.toml` rule) and the lifetime gate on `SpikeArming`'s statics; the branch never
  opens a pull request, so CI never runs on it. Nothing from it is merged.
- Grep-audit gates: the background-thread gate (`scripts/grep-audit-background-threads.ps1`)
  would red on S8b's thread; it is a spike-branch-only fact, recorded in the S8 report as
  "the real copy worker needs one allowlist line and the owner's yes".
- Save format: no change. The S6 mark lives only in a hand-staged spike save.
- GUI gates: no change on `main`. S6's menu entry and dialog exist only on the spike
  branch; the real ones are R1's and carry the GUI registrations of 15.14.
- S1b tooling PR: `harness/README.md` tool section in the same PR; no CHANGELOG entry
  (no player-visible change); no todo item.
- Docs: each report is its own docs-only pull request (no review cycle needed per
  CLAUDE.md); S13 updates the design doc and reruns `scripts/build-design-pdfs.py` if the
  design is listed there.
- No CHANGELOG, todo, CLAUDE.md or `design-gui-inventory.md` change in R0.

## 8. Risks

- The spike DLL is deployed to the dev install by accident (a `dotnet build` from the
  spike worktree's `Source/Parsek` deploys). Mitigation: build with
  `-p:SkipKspDeploy=true` and copy to `automation/spike-r0/` by hand; S0's proof greps the
  dev install's DLL for `PARSEK-SPIKE-R0` and expects zero.
- The no-DLC copy deletes the dev install's DLC through the junction. Mitigation: the exact
  `rmdir` command and the check after it (S0).
- Spike sessions and harness runs collide on one PC. Mitigation: section 5 (no tier while
  a spike session runs; kRPC one at a time).
- KSPCommunityFixes changes a stock behaviour a spike measures (save writing in S6).
  Mitigation: S6 runs with and without it; other spikes record the mod list in the report.
- A spike passes on low graphics and fails on the owner's normal settings. Mitigation: the
  spike profile drops the graphics overrides; frame-cost spikes record the settings.
- S4b works only from a hook Parsek does not own (before `PSystemSetup`), so R2 needs a
  different design. Noticed by S4a's event order table before S4b is written.
- A spike's numbers are read from the wrong DLL. Mitigation: every report names the
  deployed DLL hash pinned in S0's procedure.
- Survey coordinates drift from S1b's final rows after other spikes ran at the provisional
  site. Mitigation: S2, S5 and S7 rely on relative offsets, not on the exact site; S13
  records the final coordinates once.

## 9. Corrections and gaps

1. **Gravity is not a `CheatOptions` switch.** 7.12 lists "hack gravity" with the stock
   cheat options; `CheatOptions` has no such field (3.3). It is the `HackGravity` widget's
   state, which Parsek already reads (`GravityHackDetector.cs:81`). The plan assumes the
   widget read.
2. **Debug actions complete milestones outside flights.** `MaxProgression`, the console's
   `b4d455` and the legacy cheat window reach `ProgressNode.CheatComplete()`, which calls
   `ProgressNode.Complete()` (3.3). The design is silent; a world-first recorded this way
   could be credited to whatever flight A3 names as covering it. S9 measures whether it
   fires the normal events; A3 must classify it. Owner question 10.3. Settled: owner
   decision D3 (design 13.A) means no score; R3E-09 records a save-level debug-completion
   fact for each completion S9 shows observable, the standing carries it (`DISCLOSURE`
   rows `DEBUG_COMPLETION`, `Standing.DebugCompletions`), R6-01 drops those rows and marks
   the player `DebugCompleted`, and the rest go to 7.12 as out of reach (OV-34).
3. **No stock "fill resources" debug action was found** among the 1.12.5 cheat types (3.3),
   though 7.12 lists one. S9 confirms in game; the plan assumes it does not exist.
4. **7.12 misses tools.** `IgnoreEVAConstructionMassLimit`, `IgnoreKerbalInventoryLimits`,
   `IgnoreAgencyMindsetOnContracts`, `MiddleMouseClickSetPosition`, max tech, max
   facilities, max XP, repair, the console's direct `funds` / `science` / `reputation`
   sets, the Physics, Difficulty, Kerbal and contract-tool screens, comets and the object
   thrower (3.3). S9 covers all of them.
5. **The survey cannot give the site table.** No fine grid covers 106.7E's surroundings
   fully or any point near -162.3, the fine step is about 262 m, and no row is the KSC
   latitude (3.1). S1b scans at about 20 m on the KSC row (agrees with `r2-sites.md`
   item 11).
6. **Survey biome data is unverified.** The survey README itself calls `biomes.csv`
   unreliable, and states that stock Kerbin has no Badlands biome, which I could not
   confirm from the DLL (biome names live in the planet's config, not the code). kRPC's
   `biome_at` may differ from stock `ScienceUtil.GetExperimentBiome` (radians, 3.3). The
   plan takes biomes from the stock function in game (S2), never from the survey.
7. **"Sample heights on two machines"** (section 12, S5) is planned as two processes on
   one PC; heights are a function of the same data and code. A second machine is a
   cross-check when a second tester exists, not a gate. Settled: two processes on one PC
   gate it; R2-02 takes that reading (DC-091).
8. **"The KSC area biome is in the biome map"** (section 12) is a hypothesis: the KSC
   science names may come from tagged colliders (`SetLandedAt`) and mini-biomes rather than
   the map. S2 measures both; R2-09 and R2-10 wait on it.
9. **Two spawn-point classes.** The 7.3 moving checklist says "launch site spawn points";
   stock has `LaunchSite.SpawnPoint` and `PSystemSetup.SpaceCenterFacility.SpawnPoint`,
   each caching lat / lon / alt (3.3). S4b resets both.
10. **The plaque dialog takes supplied text.** `FlagPlaqueDialog.Spawn(siteName,
    plaqueText, onDismiss)` is public static (3.3), so S7 is smaller than section 12
    suggests. Settled for the model half: the flag part has no mesh file, so S7 builds the
    replica through `FlagDecalBackground` or from a real flag vessel, never from a prefab
    model (OV-55).
11. **S6 as written covers stock only.** Parsek itself rewrites saves (rewind save copies,
    the Discard Re-fly persistent.sfs write, the crash reconcile). S6 checks those too.
12. **15.13 costs have no spike except S5.** S11 measures per-ghost frame cost and the
    lazy first open.
13. **The signing proof of section 12's last row has no spike id.** S12 (optional).
14. **The scan tool's "KSC centre" is the pad-runway midpoint**
    (`kerbin_equator_scan.py` KSC_CENTER), not the KSC `PQSCity` origin that a move uses.
    The site table offsets come from S4a's measured origin.
15. **The slim suit's DLC home is not proven.** 7.4 says look 1 (slim) needs nothing; the
    slim EVA parts exist by name, but which GameData folder ships them is not in the DLL.
    S3 checks on the no-DLC copy.
16. **Reference DLL path.** `CLAUDE.md` names `/workspace/ksp-refs`; this session found
    the references at `/home/user/ksp-refs`. The decompile facts of 3.3 come from there.

## 10. Questions for the owner

1. **Which sync clients should S8 measure?** The answer decides whether the race's
   background copy needs its own thread. Options: (a) OneDrive only; (b) OneDrive plus
   Dropbox or Google Drive, plus an unreachable network share; (c) every client you use.
   Recommendation: (b); friends will use a mix, and the unreachable share is the worst
   case. The S8 result is also the input to `r3-export-and-exchange.md` question 2 (the
   copy worker, R3E-23): one decision, not two (OV-58).
2. **May the spikes use their own KSP copy?** S0 provisions `automation/spike-r0/` and a
   copy without the DLCs, so the dev install and the harness instances stay as they are.
   Options: (a) yes; (b) run spikes in the dev install. Recommendation: (a); it costs about
   one hour once and keeps your own game clean.
3. **A milestone completed with the debug menu (Max Progression, the console's
   b4d455):** should the race (a) drop those completions from the board and mark the
   player as having used debug tools, (b) only mark the player, or (c) ignore it?
   Recommendation: (a); otherwise a debug click can take every world-first at once.
   Settled by owner decision D3 (design 13.A, a debug tool means no score): (a). What remains
   is S9's finding of which completions a mod can observe (OV-34).
4. **Keep the spike branch?** Options: (a) push `spike/mp-r0` to GitHub as a record, never
   merged and never opened as a pull request; (b) keep it local only. Recommendation: (a);
   the reports cite its commits.
