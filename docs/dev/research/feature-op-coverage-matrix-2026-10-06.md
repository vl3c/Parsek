# Gameplay-level coverage: feature x timeline-operation matrix (2026-10-06)

Measured on `main` at `eafdd64e`. Read-only research; nothing flown.
Routes are covered only at matrix level here; the route-centred analysis is
`docs/dev/research/integration-coverage-gaps-2026-10-06.md` (referenced as "the integration doc").
Quicksave/quickload depth, ore/ISRU and resource conservation are covered in depth by
`docs/dev/research/coverage-extension-plan-2026-10-06.md` and appear here only as matrix cells.

## 0. Method and evidence

- All 367 specs re-parsed with `tomllib` (a one-off tomllib parse, not committed): step verbs + args (including `RunTests` category lists and LoadGame `scene`),
  `[dimensionsCovered]`, expectation blocks, the REQUIRED `logContracts` tokens, fixture, driver
  mission. Fixture game mode read from each `harness/fixtures/saves/*/persistent.sfs` `Mode =`.
- Green status re-derived from the CURRENT `docs/dev/autotest-status.md` (lines 1757-4929):
  a row in "Live-proven (196)" or in a section headed LIVE-PROVEN = LIVE; a per-program row whose
  text records a PASS / LIVE-PROVEN / GREEN flight = PASS. Result: 231 LIVE + 128 PASS = **359
  green**, 4 expected-fail (EVA-5, EX-1, RB-1, RB-2), 3 red-by-finding (L3-career-science-recover,
  V1, V7T), 1 ambiguous (GUI-12). (an earlier coarser classification had 74 unclassified rows, almost all of
  which are PASS rows in per-program sections.)
- In-game: 126 categories / ~652-667 `[InGameTest]` declarations (`grep -c 'InGameTest('` = 667;
  attribute-parsed 652). Category->lane map from the specs' `RunTests` args (one-off scripts, not committed).
- xUnit: 1,117 test files carrying 23,030 `[Fact]`/`[Theory]` attributes. Per-cell headless
  evidence is a test FILE or METHOD name found by grep (a one-off script, plus method-name greps
  quoted inline); "weak" means the feature and the op co-occur only in file content.
- Op definitions used for the LIVE column (all from step verbs, never prose):
  - RC record+commit: `CommitTree`, `AnswerMergeDialog merge`, autopilot mission, or a D1 `commit-*` claim.
  - MD merge vs discard: `AnswerMergeDialog discard`, D1 `discard-rollback`, SceneExitMerge / MergeDialog category.
  - RFLY: `InvokeRewind` (or a mission that drives it: cl3, r1, v1).
  - RTL: `InvokeRewindToLaunch` (or the `kx_rewind_watch` mission).
  - F9: a mid-lane `LoadGame` WITHOUT a `scene` arg after a `SaveGame` (17 green lanes), or the
    QuickloadResume category. **Back-in-time F9 (save at T1, advance, reload T1) is driven by ZERO
    lanes** - every seam reload happens at the instant it was saved (one-off scan; e.g. S4.4 is
    `SaveGame quicksave` immediately followed by `LoadGame quicksave`). Only the in-game cell
    `QuickloadResume.Quickload_MidRecording_ResumesSameActiveRecordingId` (H65,
    `RuntimeTests.cs:13120`) quickloads 2 s back, inside one recording.
  - REV stock revert: **no seam verb exists** (`TestCommandVerbs.cs` verb list; reserved set at
    `:343-361`). Only in-game: RevertFlow (H64), RevertVesselStrip (LT-1), the Rewind category's
    ReFlyRevertDialog cells (R7a).
  - WARP: `WarpToUT` (13 lanes; FLIGHT-only, `ParsekTestCommandAddon.WarpToUT.cs:50`), D14
    `warp-*` / D2 `physics-warp-*` claims, or a B-mission's own rails/physics warp.
  - TJ: `TimeJump` (91 lanes), D9 `fast-forward`, the RSC / Missions Warp buttons.
  - SCN: `ExitToSpaceCenter`, `GoToEditor`/`LaunchFromEditor`, recover verbs, `LoadGame scene=`.
  - COLD: lane boots a harvested (recorded/merged/earned) fixture and asserts the feature, i.e.
    the feature survived a real save + quit + cold OnLoad. Explicit cross-process chains: RF-10
    (boots RF-9's save), RF-13R (boots RF-13's merged save).
  - REP: >= 2 rewinds in one lane (GS-9, RF-2, RF-3, RF-11, S4.1, RF-4, RF-15, H58).
  - CAR: career / science-mode fixture (51 green lanes; 19 career/science fixtures exist).

Legend: **L** = green harness lane(s); **IG** = in-game category executed by a green lane
(host in parens); **H** = xUnit; **NONE** = nothing found; **UNK** = could not determine.
"(same-instant)" = reload at the save instant only.

## 1. Feature x timeline-operation matrix

### 1a. Record / merge / rewind columns

| Feature | Record+commit | Merge vs discard | Re-Fly | Rewind-to-Launch | F5/F9 | Stock revert |
|---|---|---|---|---|---|---|
| Spawn-at-end | L EVA-6, EVA-7, CI-9, LF-1, SS-1; IG Spawner/SpawnCollision/SpawnTerminalOrbit (LT-1), SpawnRotation (H8), SpawnHealth (H16) | H SwitchSegmentDiscardScopeTests (`Discard_AfterCommittedSpawnedSwitch_*`); live NONE | L CI-2, CI-3, CI-4 (chain-tip claim / re-derive), S1.5 (strip-respawn); H RewindSpawnSuppressionTests, RewindHistorySpawnScopeTests | L EVA-6, EVA-7, EVA-9, EVA-10, LF-1, LF-2, RR-1; H RewindTimelineTests (`ShouldSpawn_*`) | L CI-3, LF-1/2, EVA-9/10 (same-instant); back-in-time NONE | IG RevertVesselStrip (LT-1); H RevertVesselStripTests, RevertRewindTerminalVerdictTests |
| Ghost visuals | L B1, B4, B29, BAY-1, EVA-1/4, GS-4..12 (+30 renderComposition lanes); IG GhostPlayback (S1.4), PartEventFX+GhostLifecycle (LT-5), GhostVisuals (H15), SnapshotBaseline (H32), PlaybackFidelity (H36), ReentryFx (H52), GhostAudio (H30) | L S4.3 (Re-Fly discard with ghost slots alive); tree discard NONE | L RF-8 (watch + map + tracers during Re-Fly), S4.3; IG Rewind `GhostSuppressionDuringReFly` | L GS-4, GS-6, GS-7, GS-8, GS-9, GS-12, BAY-1 (mesh lifecycle after RTL) | L S1.9 (same-instant) | NONE |
| Loops / periodicity | L GS-12, LF-1/2, SE-1, MC-5; IG Missions (M1), Periodicity (M2), MissionPhasing (M3) | NONE | **NONE** live; H none found (no xUnit method pairs loop + supersede) | L LF-2 (loop armed before RTL), GS-12 | L LF-1/2 (same-instant); H LoopFirstRunSpawnTests, AutoLoopTests | NONE |
| Crew (reservations, stand-ins, deaths) | L CL-1, CL-2, L7, EVA-7; IG CrewReservation (H31, 11/15 exec), CrewReservationLive (LT-4), AutoHireReservation (AH-1), KerbalInventorySpawn (H72) | live NONE; IG Rewind `TreeDiscardRemovesSupersedesAndTombstones` (exec UNK) | L CL-3, CL-4, RF-12S, RF-13, RF-19, S4.2, RF-12W (`KerbalRecoveryOnSupersede` PASSED); H TombstoneReloadMigrationTests, SupersedeCommitTombstoneTests, RewindCrewLossFixtureTests | L GS-4 (crew swap token), EVA-7 (inventory); H RewindUtCutoffTests (`CutoffZero_...ProjectsCrewReservations`), KerbalDeathRespawnTests | L RF-13, RF-12S (merge then same-instant reload); back-in-time NONE | NONE live; H weak |
| Science (experiments, transmit, recovery) | L L6 x2, L7, HC-2, L1-research x2 (spend) | live NONE; H RevertDiscardTests / QuickloadDiscardTests (pending subjects cleared) | **NONE green** (RB-1/RB-2 EXPECTED-FAIL); H RewindUtCutoffTests `ScienceEarning_CutoffFiltersLater` | L HC-2 (`PatchScience 107 -> 100` after RTL) | NONE; H QuickloadDiscardTests `..._WithStaleScienceSubjects_Clears` | NONE; H RevertDiscardTests `UnstashPendingTreeOnRevert_ClearsPendingScienceSubjects` |
| Deployed (BG) science | NONE (EVA-8/9/10 place clusters on SANDBOX, no science awarded) | NONE | NONE | NONE | NONE | NONE - filed DEPLOYED-SCIENCE-FLOW-LANE-NEEDS-A-HOST (todo:855); IG `DeployedScienceGhost` never run by any spec |
| Contracts | L L5 (deadline lapse in flight) | IG Contracts (LT-3: `GenuineDiscard_RehomesContractCompletion`, `AbandonDiscard_...`) | live NONE: IG Rewind `ContractTombstonesAcrossSupersede` SKIPS on every host (RF-6/RF-12*: sandbox, no contracts, RF-12W toml:107; R7a/R7c career hosts: no live session, R7a toml:216); H ContractRewindSnapshotTests, SupersedeCommitTombstoneTests | live NONE (KB-3/KB-4 run on a rewound-SHAPE fixture, no real rewind); H RewindUtCutoffTests `ContractAccept_AdvanceCutoff` etc. | NONE | NONE |
| Strategies | NONE | NONE | NONE; H SupersedeCommitTests, TombstoneEligibilityTests (weak) | NONE; H RewindUtCutoffTests `StrategyActivate_CutoffFiltersLater` | NONE | NONE |
| Funds / reputation | L CL-2, HC-1, HC-2, L1-*, L6, RVR-4 | NONE | L CL-4 (rep penalty tombstoned, log token); RB-1/2 expected-fail; H SupersedeCommitTombstoneTests, RewindReadbackGuardTests | L HC-1, HC-2 (PatchFunds/Science/Reputation tokens); oracle refuses rewind lanes (`hlib.py:6966-6992`) | L KB-1 (same-instant) | NONE; H RevertDoubleRolloutTests |
| Tech / R&D | L L1-research-node-career/-science (KSC) | NONE | NONE; H RewindTechStickinessTests | NONE; H RewindTechStickinessTests, CommittedTechUnlockPatchTests | NONE | NONE |
| Facility upgrades | L L1-upgrade-facility | NONE | NONE; H weak | NONE live (KB-2/KB-3 rewound-shape); H RewindUtCutoffTests `FacilityUpgrade_CutoffFiltersLater` | L KB-1 (demolish/repair across save+reload) | NONE |
| Docking / undocking | L BDOCK-1, BDOCK-2, SD-1, CI-9; IG RouteDockCapture (H55/H56), ClawCouple (H42) | NONE | L CI-2, CI-4 (Re-Fly before a committed dock: partner claimed, cross-tree chain), RF-2/3/6/8 host bdock-recorded | **NONE** (no docking fixture carries a rewindSave) | L CI-3 (quicksave mid-Re-Fly, chain re-derived) | NONE |
| EVA / board / ground parts / flags | L EVA-1 (flag+board), EVA-2, EVA-3, EVA-4, EVA-8, CI-1; IG EvaSpawnPosition (H20) | NONE | L RF-19 (EVA + reboard inside session), RF-20 (stashed EVA slot); H ReFlySessionEvaMergeTests, ReFlySeparationsOnlyTests | L EVA-6, EVA-7, EVA-9, EVA-10 | L EVA-9/10, RF-20 (same-instant) | NONE |
| Flags specifically | L EVA-1 only (recorded) | NONE | L S4.2 (pre-existing flag preserved by Re-Fly scrub) | NONE | NONE | NONE |
| Staging / debris | L GS-1..3, GS-10/11, B-lanes, CA-1; IG Coalescer (H62) | L S4.3, RF-17 (discard of a crashed-booster Re-Fly) | L RF-1, RF-9, S4.1-S4.4, R1, CL-3 | L GS-4..12 | L RF-1, RF-9, S4.4 | IG RevertFlow (H64: stage off pad, revert) |
| Landed / splashed endings | L B1, B4, B13, B14, CL-1, L6 | NONE | L RF-12L, RF-1 | L EVA-6/7, LF-1/2, RR-1 | L LF-1/2, RF-1 | NONE |
| Orbital endings | L B11-B30, GS-2, GS-3 | NONE | L RF-12S, R1 (LKO only) | **NONE** | L RF-20 | NONE |
| Map / TS presence | L GS-4/6/7/8, V2, V3C; IG GhostMap (H44, S1.6), MapRender (S1.7), MapPresence (H28), TrackingStation (H23), MapView (H47) | NONE | L RF-7M, RF-7T, RF-8; H Bug616GhostMapReFlyLookaheadTests | L GS-4/6/7/8, MC-4 | in-place NONE (reload-into-TS is a scene change) | NONE |
| CommNet relay | NONE (relay ghosts are injected presets) | NONE | NONE | NONE | NONE | NONE |
| Real Spawn Control | L CI-9 (dock with a real-spawned tip, commit) | NONE | NONE | NONE | NONE | NONE |
| Watch mode | L GS-7, MC-5 | NONE | L RF-8 | L GS-4, GS-7, GS-9 | NONE | NONE |
| Missions window / merge dialog | L H21 (IG SceneExitMerge), H63 (IG MergeDialog), H67, MS-1 | L H21, H63, RF-3, RF-17, S4.3, GUI-7 | L ~26 lanes answer the dialog after Re-Fly (S4.1, CL-3, RF-*) | L LF-2 (MissionConfig then RTL) | L S4.4, CI-3 | IG Rewind `ReFlyRevertDialog_*` (R7a) |
| KSC / TS scene behaviour | L CL-2 (auto-commit outside flight), H67 | L H21 (discard at exit) | L R7c (IG Rewind @SPACECENTER), RF-1/4/9 | L GS-*, EVA-6/7 (RTL loads into SpaceCenter) | L KB-1, ST-4 (KSC reload) | NONE |
| Mod-compat FX | L MC-4 (Making History), MC-5 (PersistentRotation); IG WaterfallCompat (MC-1), ReStockCompat (MC-2) | NONE | NONE | L MC-4 | NONE | NONE |
| Logistics routes (see integration doc) | L RVR-* | NONE | NONE real (SYNTH H6, H38-40) | L H58 (sandbox, foreign tree) | same-instant only (H59, V27M, V18T) | H RouteRevertSafetyTests |

### 1b. Time / scene / persistence / repetition / mode columns

| Feature | Real warp | TimeJump / FF | Scene change | Save+quit+cold reload | Repeated rewind | Career / science |
|---|---|---|---|---|---|---|
| Spawn-at-end | L SS-1 (spawn inside rails warp, situation correction); H SelectiveSpawnUITests `EffectiveWarpUT_*` | L H66 (IG PlaybackControl: keep-vessel spawns exactly once), RSC-1, S1.5, CI-2 | L CI-6/7/10/11 (recover the spawned tip at flight/TS/KSC marker, no respawn), SE-1 | L EVA-6/7, RR-1, L4 | NONE (GS-9 asserts ghost meshes, not spawns) | **NONE** (no career lane asserts a vessel spawn; CL-2 pins `spawnable=0`) |
| Ghost visuals | L V7W, V24W, AP-1, MC-5; H warp-suppression tests in GhostPlaybackLogic / ParsekFlightWarpCheckpointTests | L ~27 V-lanes, LT-5, S1.9, H59 | L V*K / V*T arrivals, SE-1 | L V10-V30 on recorded fixtures; IG GhostPlayback over 274 injected recordings (S1.4) | L GS-9 (mesh lifecycle across 2 RTL) | **NONE** (all render lanes sandbox) |
| Loops / periodicity | L GS-12 (10x rails), OC-1, RL-1, V7W, AP-1, MC-5; H LoopIconWarpLagTests | L ~46 V-lanes, V4 (player Warp-to), W1, AT-1 | L V*T/V*K, SE-1 (editor round trip while looping), V5 | L V-lanes, MS-1 | NONE | **NONE** (H LoopRecordingCrewReservationTests only) |
| Crew | L RF-12W (crash under warp, crew non-Dead post-merge) | L CL-3, CL-4, S4.2 | L CL-2 (death auto-committed at scene exit), RF-13 | L RF-13R (new process loads merged save: crew alive), KB-5, L4, L7 | **NONE**; H TombstoneReloadMigrationTests `..._ThenTwoReloads_...` | L CL-1..4, L1-hire/dismiss, AH-1, KB-3/4/5, L4, L7 |
| Science | NONE | NONE | L HC-2 (VAB round trip), L6 | L L4 (strict per-identity), L6 | NONE | (all science rows are career) |
| Contracts | NONE | NONE | L KB-3/KB-4 (Mission Control blocks), GUI-15 | L L4, KB-3/4, LT-3 | NONE | L L5, LT-3, KB-3/4, L4 |
| Strategies | NONE; H StrategyExpiryReplayTests | NONE | L L3 x2 (KSC only) | L KB-4 (rewound-shape) | NONE | L L3 x2, KB-4 |
| Funds / reputation | NONE; H ParsekFlightWarpCheckpointTests `RecalculateLedgerAfterWarpExit_*` | NONE in career; H TimeJumpManagerTests `RecalculateLedgerAfterTimeJump_*` | L CL-2, KB-1, ZF-1, H71 | L KB-1, L4, B10 (cold load UT0), H71 | NONE | L 17 lanes (ledger block on 11) |
| Tech / R&D | NONE | NONE | L KB-3, GUI-28 | L KB-3, H48 (IG Ledger tech-unlock window) | NONE | L L1-research x2, KB-3, H48 |
| Facility upgrades | NONE | NONE | L KB-1/2/3, GUI-14 | L KB-1 | NONE | L L1-upgrade, KB-1..4 |
| Docking | L BDOCK-1 (mission rails warp) | L CI-2, RF-2/3 | NONE gating (GUI-4 photographs only) | L SD-1, CI-*, ST-3 | L RF-2, RF-3 on a docking host (dock not the subject) | **NONE** |
| EVA / ground parts / flags | **NONE** (EVA-5, the only EVA+loop lane, is EXPECTED-FAIL) | NONE gating | L EVA-9/10, RF-18, RF-20 | L EVA-6..10 | NONE | L KB-5 (EVA blocked for a held kerbal) only |
| Staging / debris | L CA-1, B-lanes | L S4.1-S4.4 | L GS-1..3, RF-1, RF-9 | L RF-* on refly-autopilot-recorded | L GS-9 | **NONE** |
| Landed / splashed | L SS-1, CA-1, B13/B14 | L V22*/V23*, LF-1/2, RVR-* | L V22K/V22T, V23T, CI-6/7/10/11 | L kerbin-splashdown / mun-landing lanes | NONE | L L6 x2, L7, HC-2 |
| Orbital endings | L B-lanes, SS-1 (terminal-orbit-safety), PWR-1..3 | H TimeJumpTerminalOrbitShiftTests | L GS-2/3, H67 (IG AutoMergeCommit, orbiting) | L orbit-recorded fixtures (V-lanes) | NONE | **NONE** |
| Map / TS presence | L V24W | L V*T, RF-7M, GUI-9 | L V*T, CN-1T, H23, H44 | L B32, V*T | NONE | **NONE** |
| CommNet relay | L CN-2 (live probe), CN-3 (rails warp timeline) | NONE | L CN-1T (TS) | synthetic presets only (CN-1) | NONE | **NONE** |
| Real Spawn Control | NONE | L RSC-1 (RSC Warp button -> jump to EndUT -> spawn) | L CI-6/7/10/11 | synthetic presets only | NONE | **NONE** |
| Watch mode | L V7W, AP-1, MC-5; H WatchModeControllerTests (warp-rate hold) | L V4, W1, V*M | **NONE** | L V*M on recorded fixtures | L GS-9 | NONE |
| Missions window / merge dialog | NONE gating | L V4 | L H21, CL-2 | L GUI-17/18/19/25/27 census | L RF-2, RF-3, RF-11 | L CL-3, CL-4, HC-1/2 |
| KSC / TS scene | IG WarpToTime @SPACECENTER (LT-2, plan resolve only); `WarpToUT` is FLIGHT-only | L V*K, V*T | (many) | L B10, PPB-1/2, H71, GUI census | L RF-4, RF-15 | L KB-*, L1-*, H45, H71, GUI-5/28 |
| Mod-compat FX | L MC-3 (BetterTimeWarp), MC-5 | L MC-5 | NONE | NONE | NONE | NONE |
| Logistics routes | NONE live (TimeJump only) | L RVR-* | dispatch FLIGHT only | L RVR-* | NONE | L RVR-4, RVR-17, GUI-23 |

### 1c. Column totals and structural facts

- Green lanes per op: RC ~119, MD 7, Re-Fly 33, RTL 25, F9 in place 17 (**back-in-time 0**),
  stock revert **0** (3 in-game cells), real warp 13 `WarpToUT` + ~30 mission-warp B/GS lanes,
  TimeJump 91, scene change 51, career/science 51, repeated rewind 8.
- **Career x {ghost visuals, loops, map/TS, CommNet, RSC, watch, docking, staging, orbital
  endings, spawn-at-end} is NONE.** Career lanes assert economy, crew and stock-screen blocks; no
  career lane asserts a single ghost, loop, map or spawn token (token scan over the 47 green
  career lanes' required logContracts).
- **Stock revert and back-in-time F9 are the two most common player timeline operations and have
  no driven lane at all.** Revert has no seam verb; F9 lanes only reload the instant they saved.
- **Every rewind-family cell in career is log-token level.** The ledger oracle refuses any lane
  with InvokeRewind / InvokeRewindToLaunch / AnswerMergeDialog (`hlib.py:6966-6992`, the L4
  deferral), and the HC lanes run LedgerGroundTruth BEFORE their RTL, not after.
- **Contracts x Re-Fly has no executing in-game cell.** An earlier working map (the
  integration research's scratch file, not committed) listed
  `ContractTombstonesAcrossSupersede` as SYNTH-LIVE via RF-6/RF-12; RF-12W's own header records it
  "STILL SKIPS - sandbox host, no contracts" (`RF-12W-rewind-batch-after-warp-crash.toml:107`),
  and every Rewind-category host is sandbox except R7a/R7c (career-pad-craft / fresh-career,
  both session-absent, where the cell skips on "No active re-fly session",
  `R7a-rewind-session-absent.toml:216`). The committed integration doc does not repeat the claim.
- Four in-game categories are run by no spec: DeployedScienceGhost (1), ChainTipBlockedGhost (2),
  GhostReapplyFrame (1), GuiMock (4). The Rewind category executes at most 17 of 39 cells on any
  one host (R7a pin `passed=17 skipped=22`; RF-12* 12-14; R7c 6), and R7-FIXTURE-GAPS
  (todo:10793) records cells no committed fixture can execute.

## 2. Per-layer density by subsystem

LOC = lines of `.cs` in the group (one-off line count per file group). "Named" = xUnit files
whose name starts with a source file name in the group (heuristic for "targets it"); "Ref" = xUnit
files referencing any type the group declares (over-counts shared types). IG = in-game
declarations in categories mapped to the group. Lanes = green lanes touching the feature.

| Subsystem | LOC | xUnit named files / facts | xUnit ref files | IG tests (cats) | Green lanes | LOC per named fact |
|---|---|---|---|---|---|---|
| Recording (FlightRecorder, BG, store, optimizer, sidecars) | 76,670 | 76 / 2,348 | 729 | 110 (31) | ~119 RC | 33 |
| Scenario / persistence / scene control (ParsekFlight 32.4k, ParsekScenario 9.9k, ParsekKSC 3.4k, ParsekTrackingStation 2.9k, TimeJump, WarpToTime, SceneExit) | 51,175 | 12 / 220 | 394 | ~58 (14) | all boot through it | **233** |
| Ghost playback + visuals + FX | 50,057 | 33 / 811 | 254 | 126 (18) | ~60 | 62 |
| UI (windows, merge dialog, GUI tree) | 45,946 | 40 / 827 | 189 | 26 (10) | ~30 census | 56 |
| Map / TS presence | 43,034 | 32 / 816 | 395 | 68 (6) | ~42 | 53 |
| GameActions (ledger, modules, patcher, GameStateRecorder) | 39,929 | 39 / 1,511 | 361 | 27 (8) | ~45 career | 26 |
| Logistics (routes) | 32,893 | 85 / 1,473 | 179 | 67 (9) | 46 | 22 |
| Rewind / Re-Fly / supersede | 28,160 | 43 / 904 | 806 | 46 (6) | 33 Re-Fly + 25 RTL | 31 |
| Missions / periodicity / re-aim | 22,816 | 48 / 1,203 | 168 | 32 (4) | ~61 loop | 19 |
| Stock-UI reservation layer (+ Patches/) | 18,828 | 15 / 249 | 97 | 13 (2) | ~8 (KB-*, H45, GUI-28) | 76 |
| Spawn (VesselSpawner, chains, safety) | 16,118 | 22 / 476 | 270 | 38 (11), 2 never run | ~51 | 34 |
| Crew / Kerbals | 7,449 | 7 / 138 | 98 | 18 (3) | 24 | 54 |
| Watch mode | 5,882 | 1 / 59 | 148 | **2** (1) | 18 | 100 |
| CommNet ghosts | 2,472 | 2 / 67 | 10 | 15 (3) | **4** | 37 |

Per-class spot checks (xUnit files referencing the class name / facts in them):
ParsekScenario 9,939 LOC -> 1 named file, 11 facts; ParsekKSC 3,372 LOC and
ParsekTrackingStation 2,873 LOC -> 0 named files; **GhostCommNetManager 1,146 LOC -> 0 xUnit
files reference it**; RevertInterceptor 851 LOC -> 5 ref files, 0 named; QuickloadResumeHelpers
549 LOC -> 1 ref file; VesselGhoster 1,501 LOC -> 8 ref files; LiveDeliveryWriters 535 LOC -> 4;
EngineFxBuilder 2,145 LOC -> 4; GhostVisualBuilder 9,397 LOC -> 25 ref files (16 named facts);
WatchModeController 5,007 LOC -> 15 ref files; CrewReservationManager 2,281 LOC -> 0 named
(44 ref).

**Flagged (large code, thin tests, and on the timeline-op path):**
1. **Scenario / scene control (OnLoad branch matrix).** 233 LOC per named fact, 4-9x every
   other group. This is where cold / in-session-revert / F9 / scene-change / RTL / Re-Fly loads
   diverge (`ParsekScenario.cs:680` `IsQuickloadOnLoad`, `:3762`, `:4319`
   "returned-revert"/"returned-scene-change") and where the integration doc's confirmed defect
   3.1 lives (route reconcile wired to 2 of 5 load paths). RevertInterceptor and
   QuickloadResumeHelpers are nearly untested headless, and the matching live ops (revert, F9
   back-in-time) are undriven.
2. **Watch mode**: 5.9k LOC, one named test file, 2 in-game tests; watch x scene change / F9 /
   career NONE.
3. **CommNet ghosts**: manager untested headless, 4 lanes, no rewind / Re-Fly / career / F9 cell.
4. **Crew / Kerbals**: 7.4k LOC behind 138 named facts and no conservation invariant anywhere
   (analyzer INV1-INV12 have no crew rule); crew x revert / discard / repeated rewind NONE.
5. **Stock-UI reservation layer**: 76 LOC per fact; its live proof (KB-2/3/4) runs on
   rewound-SHAPE fixtures, never after a real rewind.
6. **Ghost visual builder / FX**: thin headless, mitigated by 126 in-game cells and ~60 render
   lanes, but all sandbox.

Well covered relative to size: Logistics (in isolation), Missions/periodicity, GameActions
headless (plus two existing fuzzers), Rewind headless.

## 3. Top 20 untested gameplay scenarios

Score = likelihood a real player does it (1-5) x damage if broken (1-5). "Nearest" = the closest
existing coverage. Hosts named are committed fixtures unless marked NEW.

| # | Player story | Features x ops | L x D | Nearest today | Layer that should own it | Host fixture |
|---|---|---|---|---|---|---|
| 1 | Career flight: F5 in orbit, transmit science, complete a contract milestone, crash, F9 back, redo, recover | science + contracts + funds x back-in-time F9 x career | 5x5=25 | H65 in-game 2 s quickload (sandbox); H QuickloadDiscardTests | Lane (SaveGame quicksave -> mission continues -> LoadGame quicksave) + in-game LedgerGroundTruth AFTER the F9 + headless backward-cutoff pin | career-science-pad (science_bench_recover machine) |
| 2 | Career: with committed history ghosting, launch, earn milestones, Revert to Launch, then Revert to VAB, relaunch | funds/science/milestones + ghosts x stock revert x career | 5x4=20 | IG RevertFlow (H64, sandbox, empty store) | NEW `Revert` seam verb + lane; extend RevertFlow to a career host | career-earned-pad (2 committed recordings) |
| 3 | Re-Fly a booster crash in a career where the superseded flight completed a contract and recovered science | contracts + science + funds x Re-Fly x career | 4x5=20 | RB-1/RB-2 EXPECTED-FAIL; IG ContractTombstones skips; H ContractRewindSnapshotTests | Fix RB-1, then lane; run the Rewind category on a career host so ContractTombstones executes; L4 rewind-aware oracle | career-science-pad + injected RP (RB host); NEW career twin of refly-autopilot-recorded |
| 4 | Rewind-to-Launch in career after post-launch KSC actions (research a node, upgrade a facility, hire, accept a contract), then check the stock screens | tech + facilities + crew + contracts x real RTL x KSC | 4x4=16 | KB-2/3/4 on rewound-SHAPE fixtures; HC-1/2 RTL with funds tokens only | Lane: KscAction x4 -> InvokeRewindToLaunch -> StockScreen + RunTests(LedgerGroundTruth) after RTL | career-earned-pad + in-run launch (RR-1 pattern) |
| 5 | Spawn-at-end of a crewed vessel in career after warp: stand-ins released, Astronaut Complex correct, recovery pays once | spawn + crew + funds x warp x career | 4x4=16 | L4 missed-endut tokens; SS-1 (sandbox) | Lane + new analyzer crew-conservation invariant | NEW career twin of kerbin-splashdown-recorded |
| 6 | Discard a just-flown tree in the merge dialog after a crash that killed a kerbal and spent funds | crew + funds x discard x career | 4x4=16 | IG Contracts discard (LT-3); H21/H63 sandbox, no crew | In-game MergeDialog cell on a career host + lane | career-pad-craft (CL-1 machine) |
| 7 | Crewed Mun landing, EVA, plant a flag, a kerbal dies on a second EVA, Re-Fly the descent, merge, F9, warp past the spawn | landed + EVA + flag + crew death x Re-Fly x F9 x warp | 3x5=15 | RF-12L (Kerbin), EVA-10 (no RP), CL-3 (pod) | Campaign lane with per-step invariant reads | NEW crewed Mun-landing fixture with an RP (mun-landing-recorded has rps=0) |
| 8 | Dock a crew vehicle to a station, then Rewind-to-Launch the crew launch and warp past the dock | docking + spawn + crew x RTL x warp | 3x5=15 | CI-2/CI-4 (Re-Fly only); no docking fixture has a rewindSave | Lane recording the second launch in-run then RTL latest (RR-1 pattern) | bdock-station-pad / bdock-recorded + in-run launch |
| 9 | Repeated rewinds in career with crew: RTL three times, Re-Fly twice | crew + funds + rep x repeated rewind x career | 3x4=12 | GS-9 (sandbox, ghosts), RF-2/3 (sandbox) | Lane + headless op-sequence fuzzer | career-pad-craft (CL-3/CL-4 RP preset) |
| 10 | Quit mid open Re-Fly session, restart KSP, continue, merge | Re-Fly x cold reload | 3x4=12 | RF-10/RF-13R reload AFTER merge; S4.4 same process | Two-lane chain (harvest mid-session save, boot it) | refly-autopilot-recorded chain |
| 11 | Recover a Parsek-spawned vessel in career, then rewind past the recovery | spawn + funds x recovery x RTL x career | 3x4=12 | CI-6/7/10/11 (sandbox, no funds) | Lane + analyzer duplicate-spawn invariant | NEW career twin of the gloops-airshow chain-tip-recovery preset |
| 12 | Orbital insertion around the Mun, Re-Fly the transfer stage across the SOI change | orbital ending + staging x Re-Fly across SOI | 3x4=12 | R1 / RF-12S (LKO only); roadmap "rewind-across-SOI" open | Lane | NEW mun-orbit fixture with an RP |
| 13 | Undock, EVA-transfer a kerbal between the halves, Re-Fly one half, merge | undock + EVA crew transfer + crew x Re-Fly | 3x4=12 | CI-2/CI-4 (no crew), integration doc 3.8 seam | Lane + in-game crew cell | bdock-recorded + crew (NEW crewed variant) |
| 14 | Sit in the Tracking Station and warp 100 days with many committed endings, loops and relays | spawn + loops + CommNet x warp x TS/KSC | 4x3=12 | LT-2 WarpToTime plan resolve only; `WarpToUT` is FLIGHT-only | NEW scene-agnostic warp verb + lane | duna-one-recorded / kerbin-splashdown-recorded |
| 15 | Real Spawn Control a crewed vessel early, then Rewind-to-Launch or Re-Fly its mission | RSC + crew + spawn x RTL / Re-Fly | 2x5=10 | RSC-1 (warp+spawn), CI-* (sandbox) | Lane + analyzer vessel-identity invariant | gloops-airshow chain-tip preset |
| 16 | Plant a flag, Rewind-to-Launch, warp past: flag duplicated or lost | flag x RTL x warp | 3x3=9 | EVA-1 records only | Lane | kerbin-splashdown-recorded (EVA-6 machine + PlantFlag) |
| 17 | Contract deadline crosses during a warp at the KSC, then a rewind past the deadline | contracts x warp x rewind x KSC | 3x3=9 | L5 (deadline in flight) | Headless first, then lane | career-contract-pad |
| 18 | Watch a ghost, then F9 / change scene / merge a Re-Fly while watching | watch x F9 x scene x Re-Fly | 3x3=9 | RF-8 (watch during Re-Fly); watch x scene NONE | In-game Watch category growth + lane step | kerbin-splashdown-recorded |
| 19 | CommNet ghost relay provides control to a real probe mid-burn, then the relay's mission is rewound | CommNet x RTL / Re-Fly | 2x4=8 | CN-2 / CN-3 (warp only) | Lane | duna-park-probe + relay preset |
| 20 | Breaking Ground cluster in career transmits over warp, then Re-Fly / Revert | deployed science x warp x Re-Fly x revert x career | 2x4=8 | NONE (todo:855) | Lane after the science-bg-pad builder | NEW science-bg-pad (costed in todo:855) |

Next below the cut: strategies across RTL/Re-Fly (2x3), mod-compat FX through rewinds (2x2),
loops x Re-Fly (moot after PLAYER-LOOPING-REMOVAL except through routes - see integration doc).

## 4. Coverage-extension strategy

### 4.1 What belongs in each layer

- **xUnit (headless, cheap, red-before-green).** Pure invariants and op-sequence properties over
  the extracted logic: ledger cutoff semantics for each load kind (RTL, Re-Fly, F9 backward,
  revert prune, discard), supersede/tombstone closure, crew recompute, spawn decision, route
  reconcile. Plus SOURCE-WIRING gates in the style of `RouteGoBackRewindReconcileTests`: a table
  of load paths (cold, in-session revert, F9, scene change, RTL, Re-Fly, Discard Re-fly) x
  reconcilers (ledger prune/cutoff, route reconcile + loop cursors, crew recompute, spawn state,
  ghost-map rebuild) that fails when a reconciler is wired to some paths and not others - the
  defect-3.1 class, which no flight is needed to catch.
- **In-game (live KSP, one boot).** Read-only invariant categories that work over ANY scene
  state (CrewReservation, GhostChains `NoDoubleGhosting`, SpawnHealth `SpawnedPidConsistency`,
  CrewReservationLive `SpawnedVesselsNotGhosts`, TreeIntegrity, RecordingInvariants,
  LedgerGroundTruth) plus the cells that need a live stock singleton (revert, quickload, stock
  screens). Make the existing never-executing cells execute: ContractTombstones on a career
  host, the four never-run categories, the R7-FIXTURE-GAPS cells.
- **Harness lanes (two-feature).** One new axis at a time, as the integration doc's Phase B does
  for routes: each row-1..13 story above as a lane, armed after a reading run.
- **Long campaign lanes (operator / nightly).** One career and one sandbox "everything" save
  driven through 30-60 mixed steps, so the first red names a seam rather than "the career
  broke". The integration doc's IC-3 is the route instance of this.
- **Offline analyzers / oracles over produced saves.** The multiplier: the analyzer already runs
  on EVERY lane's produced save (verifier chain), so a new invariant rule instantly becomes a
  check in all ~359 green lanes and every future fuzzer step. Today INV1-INV12 cover trajectory,
  topology, codec, ledger referential integrity and RP ids; there is **no crew-conservation, no
  vessel-identity / duplicate-spawn and no resource-conservation rule**, and the ledger oracle
  cannot model a rewound career.

### 4.2 Generic capabilities that multiply coverage

1. **Analyzer invariants over the produced save** (INV13+), reusing `CareerSaveParser`:
   - Crew conservation: every roster kerbal is exactly one of available / assigned to exactly one
     live vessel seat / reserved by exactly one effective recording / Dead / Missing; no kerbal
     is both reserved and seated; stand-ins map to a reserved original.
   - Vessel identity: no two live vessels share a `RecordedVesselGuid`-derived launch identity or
     a spawned pid; no live vessel belongs to a superseded recording; no ghost-map ProtoVessel is
     persisted; spawned-once per effective leaf.
   - Resource conservation (other agent's audit owns the detail).
   - Ledger: INV8 part (b) career reconstruction (currently "reconstruction-not-available").
2. **A rewind-aware ledger oracle (the L4 deferral, `hlib.py:6966`).** Expected pools =
   seed + ELS rows with UT <= the load's cutoff, tombstones applied; lets every rewind / merge /
   F9 lane carry `[expectations.ledger]`.
3. **Per-step invariant reads inside one lane.** Implement the reserved `RunInvariantReport` verb
   (`TestCommandVerbs.cs:343-361`) as a non-batch, read-only, in-scene evaluation of the analyzer
   rules + the read-only in-game invariant cells, printing one grep-stable verdict line per call.
   Needed because `SINGLE_BATCH_SELECTOR_RULE` (`hlib.py:2339`) allows exactly one RunTests step
   per lane.
4. **Missing timeline verbs:** stock `Revert` (to launch / to VAB, through stock's own
   FlightDriver path), a true stock quickload (`Quickload` via QuickSaveLoad, distinct from the
   seam `LoadGame` boot channel), and a scene-agnostic warp (KSC / TS) - `WarpToUT` is
   FLIGHT-only. `CrashAfterJournalPhase` (reserved) covers the merge-journal crash matrix.
5. **"Everything" fixtures.** One career and one sandbox save holding: crewed vessels in flight,
   a docked station, a landed base with a flag, open RPs (Re-Fly), a `rewindSave` (RTL), a
   pending spawn-at-end, a loop / route, accepted contracts, an active strategy, a deployed
   cluster, a CommNet relay. Forged with the FORGE pattern (mission + harvest) so it is
   production-shaped. Missing today: any career fixture with an RP or a rewindSave, any docking
   fixture with a rewindSave, any crewed Mun-landing fixture with an RP.
6. **The timeline-op fuzzer lane.** A spec GENERATOR (`harness/tools/`) that writes a seeded,
   declarative step list (keeping specs static and reviewable) from the op alphabet
   {SaveGame, LoadGame back-in-time, Revert, TimeJump, WarpToUT, InvokeRewind(rp from
   ListHandles), InvokeRewindToLaunch latest, AnswerMergeDialog merge|discard, ExitToSpaceCenter,
   LoadGame scene=trackstation, RealSpawn, Recover, KscAction, EvaExit/Board}, with a guard
   grammar (no jump before OnFlightReady - REFLY-LANES-JUMP-BEFORE-FLIGHT-READY; no recorder-live
   reload). After each step: `RunInvariantReport`; at the end: analyzer + saveParse + rewind-aware
   ledger oracle. The seed and step index are printed so a red run is replayed as the failing
   prefix (manual shrinking). Nightly rotates one seed; operator tier sweeps many.
7. **Headless op-sequence fuzzer** extending `LedgerStateFuzzerTests` (8 facts) and
   `EffectiveStateGraphFuzzerTests`: random sequences of commit / Re-Fly-supersede / RTL cutoff /
   F9-backward / revert-prune / discard over a synthetic ledger + tree + roster, asserting
   idempotence (op twice == once), ELS monotonicity, seed survival, crew conservation, and
   "rewind then re-commit the same flight == original". This catches most of the in-game
   fuzzer's logic bugs at millisecond cost.

### 4.3 Order of work

1. **Headless first (no flights):** the load-path x reconciler wiring gate; the op-sequence
   ledger/crew fuzzer; pins for the top-3 stories' pure halves (F9-backward cutoff, revert prune
   with committed history, contract/science tombstones on supersede).
2. **Analyzer INV13 crew conservation + INV14 vessel identity / duplicate spawn**, baselined over
   every committed fixture, then gated (they immediately cover all lanes).
3. **Verbs:** `Revert`, stock `Quickload`, `RunInvariantReport`, scene-agnostic warp.
4. **Fixtures:** career twin with an RP + rewindSave (unblocks rows 3, 4, 9 and the integration
   doc's IR-3/IR-7), crewed Mun landing with an RP, docking fixture with a rewindSave,
   science-bg-pad (todo:855).
5. **Two-feature lanes** for stories 1-8 (reading run, then arm), F9-backward and Revert first.
6. **Rewind-aware ledger oracle (L4)** so the career lanes from step 5 gate on pools, not tokens.
7. **Fuzzer lane** (nightly, one seed per night) on the everything-fixtures, then the **campaign
   lanes** (career + sandbox, operator tier), with the integration doc's IC-3 as the route slice.
