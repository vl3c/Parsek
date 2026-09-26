# Parsek vs stock KSP settings, game modes and difficulty parameters

Worktree: `ksp-settings-parsek-analysis-443444` (HEAD 958314016). All line numbers are
`Source/Parsek/...` unless stated. Stock behaviour was checked against a fresh ilspycmd
decompile of the dev instance's KSP 1.12.5 `Assembly-CSharp.dll` (decompiled copies, not committed:
`GameSettings.cs`, `GameParameters+*.cs`, `ProtoCrewMember.cs`, `PauseMenu.cs`,
`ResearchAndDevelopment.cs`, `GameVariables.cs`, `ACS_full.cs`). "Inferred" marks a
conclusion drawn from code paths but not driven live; "Speculation" marks a guess.

Legend for the verdict column: OK = other values handled; GAP = a value of the setting
produces wrong or surprising behaviour; N/A = test/automation-only read.

---------------------------------------------------------------------------------------

## 0. Headline findings (most important first)

1. **ScienceGainMultiplier is silently not modelled for experiment science (GAP, every
   non-Normal preset).** Stock `ResearchAndDevelopment.SubmitScienceData` does
   `subject.science += scienceValue;` THEN `scienceValue *= Career.ScienceGainMultiplier;`
   THEN `AddScience(scienceValue)` and `OnScienceRecieved.Fire(scienceValue, ...)`
   (decompiled `ResearchAndDevelopment.cs:1784-1797`). Parsek's capture
   (`GameStateRecorder.cs:930-938`) stores `subject.science` (pre-multiplier), and
   `GameActions/GameStateEventConverter.cs:418` makes that the `ScienceEarning.ScienceAwarded`
   that `ScienceModule` credits to the running science pool (`GameActions/ScienceModule.cs:220-253`).
   So the ledger's science pool earns x1 while stock earns x2 (Easy), x0.9 (Moderate),
   x0.6 (Hard). Inferred consequence: x>1 -> ledger running balance below live -> the
   "Keep what you earned" UP clamp (`KspStatePatcher.ApplyDrawdownGuard`, doc at
   `GameActions/KspStatePatcher.cs:3944-3956`) holds the live pool with a toast/WARN, and
   an authoritative (rewind) recalc drops the surplus; x<1 -> ledger above live -> DOWN
   clamp. The per-subject diminishing-returns patch is consistent (both sides pre-multiplier).
   Every committed harness fixture and both xUnit career fixtures carry
   `ScienceGainMultiplier = 1` (see section 6), so no test can see this.

2. **Declined-contract reputation loss (`Career.RepLossDeclined`) never reaches the ledger
   (GAP, default preset included).** Stock `Contract.Decline` does
   `Reputation.Instance.AddReputation(-RepLossDeclined, ContractDecline)` when > 0
   (decompiled `Contracts.Contract.cs:2695-2706`; presets Easy 0, Normal 1, Moderate 2,
   Hard 3). Parsek drops `ContractDeclined` events (`GameActions/GameStateEventConverter.cs:331`)
   and drops non-strategy `ReputationChanged` (`:288-306`); `ReputationPenaltySource.ContractDecline`
   exists (`GameActions/GameAction.cs:377`) but no code constructs it (only `Strategy`,
   `KerbalDeath`, `StrategyConverter` are ever assigned: `GameStateEventConverter.cs:1146`,
   `LedgerOrchestrator.cs:1670`, `:4728`). Inferred: the decline hit is kept live only by the
   reputation DOWN clamp (`KspStatePatcher.cs:1961-1980`, toast "Held your reputation at the
   current value") and is lost (rep refunded) on the next authoritative recalc. Not
   difficulty-specific, but its magnitude is a difficulty setting.

3. **Hard preset (`Flight.CanQuickLoad=false`, `Flight.CanRestart=false`) is not
   respected / partially breaks re-fly (GAP, design question).** Parsek reads neither flag
   (0 hits). Rewind-to-Separation loads RP quicksaves regardless, so it re-grants
   quickload on a no-quickload career. Conversely stock `PauseMenu` only builds the
   Revert-to-Launch button under `if (Parameters.Flight.CanRestart)` (decompiled
   `PauseMenu.cs:1334`, `:1594`), so `ReFlyRevertButtonGate` forcing
   `FlightDriver.CanRevertToPostInit = true` (`ReFlyRevertButtonGate.cs:143-145`) does
   nothing on Hard: the Retry / Discard Re-fly dialog reached through
   `RevertInterceptor` (Harmony on `FlightDriver.RevertToLaunch`, `RevertInterceptor.cs:826`)
   is unreachable from the Esc menu (inferred; scene-exit still reaches the merge dialog via
   `SceneExitInterceptor`, which does model the CanRestart no-save path, `SceneExitInterceptor.cs:15`, `:501`).

4. **Crew death vs `MissingCrewsRespawn` / `RespawnTimer` is a deliberate override (OK by
   design, documented).** Stock `Part` death calls `pcm.Die()` (-> Dead) then, if
   `MissingCrewsRespawn`, `StartRespawnPeriod()` (-> Missing, back to Available after
   `RespawnTimer`) (decompiled `ACS_full.cs:331540-331584`, `ProtoCrewMember.cs:3399-3446`).
   Parsek derives death from the recording terminal state, not the roster
   (`KerbalsModule.InferCrewEndState`, `KerbalsModule.cs:1352-1398`), makes it a permanent
   reservation (`KerbalsModule.cs:807`), and deliberately does not touch rosterStatus when
   stock respawns the kerbal (`KerbalsModule.cs:2311-2313`; ground-truth carve-out
   `LedgerGroundTruthDiff.cs:750-872`). Net effect: with respawn ON the kerbal comes back
   Available in stock but stays unusable in Parsek forever (until a rewind tombstones the
   death). Separately, `VesselSpawner` and `CrewReservationManager` flip `Missing` crew to
   `Available` when a snapshot needs them (`VesselSpawner.cs:3545-3575`,
   `CrewReservationManager.cs:84-88`), with the comment "Missing is transient (KSP's natural
   respawn timer recovers it)" (`VesselSpawner.cs:3679-3683`) - an assumption that holds only
   when `MissingCrewsRespawn = true`. With it false, stock would have killed them at `UTaR`;
   Parsek's rescue resurrects them (inferred; the rescue only fires for names in a snapshot
   being spawned).

5. **Logistics duration text and cadence input hard-code Kerbin days and even mix
   calendars (GAP for KERBIN_TIME=false, and a latent bug for true).**
   `UI/LogisticsWindowUI.cs:3918-3929` `FormatDuration`: below 86400 s prints hours, at or above
   prints `seconds / 21600` as "d". So 7 h prints "7.0h", 24 h prints "4.0d" (Kerbin
   calendar) - and on the Earth calendar "d" is still 6 h. `Logistics/RouteCadence.cs:80`
   parses a typed "d" as 21600 s unconditionally. Every other Parsek duration formatter
   routes through `ParsekTimeFormat` (which reads `GameSettings.KERBIN_TIME` live) or stock
   `KSPUtil.PrintDateCompact`.

6. **CommNet: the only `EnableCommNet` read is in dead code.**
   `GhostCommNetRelay.RegisterNode` checks `Parameters.Difficulty.EnableCommNet`
   (`GhostCommNetRelay.cs:242`), but the class has no production caller (todo
   GUI-D3-GHOSTCOMMNETRELAY-IS-DEAD..., `docs/dev/todo-and-known-bugs.md:4796-4807`).
   Ghost proto-vessels have their `CommNetVessel` destroyed by `GhostVesselLoadPatch`
   (`Patches/GhostVesselLoadPatch.cs:471-492`), so ghosts are CommNet-inert whatever the
   setting. Behaviour is therefore setting-independent (OK in effect), but the registry cell
   D6 `commnet-relay` and the patch comment describe a relay that does not exist.

7. **Debris-persistence override is a no-op on 1.12.5 (dead code).**
   `ParsekFlight.EnforceMinDebrisPersistence` (`ParsekFlight.cs:13777-13799`) reflects over
   `GameSettings` for any static int whose name contains "debris" (`:13847-13871`) and would
   raise it to 10 while recording. KSP 1.12.5 `GameSettings` has no such field (scan of the
   decompile and of the DLL string heaps finds none), and the live dev `KSP.log` prints
   `[Parsek][VERBOSE][Flight] No debris persistence field found in GameSettings`. Harmless,
   but the premise in its comment ("decoupled stages are destroyed before Parsek can detect
   them") is not backed by any stock setting in this version.

8. **StartingFunds = 0 (reachable: stock slider min is 0, `ACS_full.cs:264025`) leaves the
   funds ledger unseeded (GAP, inferred).** `EnsureInitialFundsSeed` only seeds a non-zero
   pool (`GameActions/LedgerOrchestrator.cs:2118-2141`), and `PatchFunds` skips with
   "module has no FundsInitial seed" while unseeded (`KspStatePatcher.cs:1805-1809`). The
   deferred-seed coroutine also spins the full 600-frame value wait when all present pools
   read 0 (`ParsekScenario.cs:6405-6413`), i.e. ~10 s on every load of a fresh
   StartingFunds=0 career or a Science-mode game with 0 science.

---------------------------------------------------------------------------------------

## 1. Direct reads of `HighLogic.CurrentGame.Parameters` / `GameParameters` (production)

| Site | What is read | Decision it drives | Verdict |
|---|---|---|---|
| `GhostCommNetRelay.cs:242` | `Difficulty.EnableCommNet` | skip ghost CommNode registration | Dead code (finding 6). No null guard on `HighLogic.CurrentGame` either. |
| `GameStateRecorder.cs:207-229` (`TryGetBypassEntryPurchaseAfterResearch`) | `Difficulty.BypassEntryPurchaseAfterResearch` (null-guarded, test seam) | consumers below | OK (both values modelled) |
| `GameStateRecorder.Handlers.cs:432` | same, via `IsBypassEntryPurchaseAfterResearch` | PartPurchased funds charged = 0 under bypass | OK, read at purchase time = stock semantics |
| `GameActions/KspStatePatcher.cs:934` | same | re-hydrate `partsPurchased` for researched tech when bypass on | OK for a constant value. Reads the CURRENT flag while replaying HISTORICAL unlocks: toggling bypass mid-career changes how past unlocks are patched (inferred) |
| `GameActions/KspStatePatcher.cs:1446` | same | skip `PatchPurchasedParts` when bypass on | same caveat as above |
| `GameStateStore.cs:718` | same | legacy-save repair: zero-cost a "single purchase, zero funds events" PartPurchased row only if bypass is on now | Comment at `:700-705` explicitly avoids using current difficulty for anything else; this legacy branch still does. Low risk |
| `StockUiPartPurchase.cs:80` | same | R&D purchase annotation text/cost | OK |
| `Patches/FacilityRepairCapturePatches.cs:26-27` | `Career.FundsLossMultiplier` | per-building repair cost share in facility-repair ledger rows (`FacilityRepairCapture.BuildRepairCostShares`, multiplier clamped >= 0 at `FacilityRepairCapture.cs:80`) | OK - read at repair time, same value stock charges with; guarded for null game/params (falls back to 1) |
| `Patches/MapFocusObjectOnSelectPatch.cs:173-190` | `Flight.CanSwitchVesselsFar` | do not arm the Map Switch-To intent when stock will refuse the switch | OK, mirrors stock's own guard; fully null-guarded |
| `ParsekSettings.cs:331` (`Current`) | `Parameters.CustomParams<ParsekSettings>()` | every Parsek setting read | OK; `?.` guarded. Stock instantiates every CustomParameterNode subtype for every game (`GameParameters()` ctor, decompiled `GameParameters+DifficultyParams.cs:1165-1181`), so `GameMode.NONE` does not make it null in any mode |

Test / automation-only reads (N/A for players): `TestCommands/ParsekTestCommandAddon.SimulateSwitchClick.cs:365-367` (`CanSwitchVesselsFar`), `TestCommands/ParsekTestCommandAddon.EditorRoute.cs:113-115` (`SpaceCenter.CanGoInVAB/CanGoInSPH`), `TestCommands/ParsekTestCommandAddon.WarpToUT.cs:371` (`Flight.CanTimeWarpLow`), doc refs to `Flight.CanUseMap` in `TestCommands/TestCommandMapViewVerbs.cs:22,86` and `CanBoard` in `TestCommands/TestCommandEvaBoard.cs:20`.

Not read anywhere in `Source/Parsek` (0 hits each): `AllowNegativeCurrency`,
`MissingCrewsRespawn`, `RespawnTimer`, `IndestructibleFacilities`, `ResourceAbundance`,
`ReentryHeatScale`, `EnableKerbalExperience`, `ImmediateLevelUp`, `PartUpgradesIn*`,
`StartingFunds/Science/Reputation`, `AllowStockVessels`, `AllowOtherLaunchSites`,
`ActionGroupsAlways`, `GKerbalLimits`, `GPartLimits`, `PressurePartLimits`,
`ResourceTransferObeyCrossfeed`, `persistKerbalInventories`, `AutoHireCrews`,
`CanQuickLoad`, `CanQuickSave`, `CanAutoSave` (tests only), `CanTimeWarpHigh`,
`CanSwitchVesselsNear`, `CanEVA`, `BuildingImpactDamageMult`, `KerbalGToleranceMult`,
`TechTreeUrl`, `EnableFullSAS*`, any `*GainMultiplier` / `RepLossMultiplier` /
`RepLossDeclined`, and `CheatOptions.*`.

## 2. Game mode (`Game.Modes`) and currency-singleton null guards

### 2.1 Explicit mode checks (production)

| Site | Mode logic | Verdict |
|---|---|---|
| `GameActions/StrategyStatePatcher.cs:400-410`, `Patches/StrategyReservationPatch.cs:505-512` | strategies patched/blocked only in CAREER | OK (StrategySystem is career-only) |
| `Logistics/LiveRouteRuntimeEnvironment.cs:68-84` (`IsCareer`), used by `RouteDispatchEvaluator.cs:253`, `RouteOrchestrator.cs:1437,2534,3488,4299,4382,4449,4979` | KSC-origin route dispatch is charged funds (and gated on funds) only in CAREER; Science/Sandbox dispatch free | OK. Note `KscFundsAvailable` (`LiveRouteRuntimeEnvironment.cs:461-482`) requires `funds >= cost` and ignores `AllowNegativeCurrency` (stricter than stock; acceptable) |
| `UI/LogisticsWindowUI.cs:2908-2910, 3032-3034, 3366-3370, 3420` | route-creation summary / run-cost block by mode; null game -> SANDBOX | OK |
| `Logistics/RouteBuilder.cs:66,932`, `RouteCreationService.cs:74`, `RouteCreationFormatters.cs:337-348` | mode passed through; cost block Career+KSC only | OK |
| `ParsekUI.cs:236-240` | launcher gating, null game -> CAREER | OK |
| `UI/CareerStateWindowUI.cs:1320-1322, 1575-1600, 1884-1893, 2430-2440` | Career window only in CAREER; SANDBOX/MISSION/MISSION_BUILDER treated as sandbox; SCIENCE banner | OK |
| `Timeline/TimelineCareerCategory.cs:110-125` | CAREER all categories; SCIENCE_SANDBOX Facilities/Milestones/Tech; others none | OK |
| `Timeline/TimelineBuilder.cs:958-978` | initial-seed rows: SCIENCE shows only ScienceInitial; SANDBOX/MISSION hide all | OK |
| `GameActions/GameActionDisplay.cs:385-403` | hire cost text hidden outside CAREER | OK |
| `UI/TimelineWindowUI.cs:1017, 1234-1361` | view availability per mode | OK |

MISSION / MISSION_BUILDER: handled only as "sandbox-equivalent" in UI. `ParsekScenario` is
`[KSPScenario(ScenarioCreationOptions.AddToAllGames, FLIGHT, SPACECENTER, TRACKSTATION, EDITOR)]`
(`ParsekScenario.cs:30-31`). Speculation: whether AddToAllGames includes Making History
mission games was not checked; recording/rewind inside a scripted mission is untested and
could fight `MissionSystem` (no hits for `MissionSystem` in production code).

### 2.2 Singleton null guards (implicit sandbox / science handling)

179 `Funding/Reputation/ResearchAndDevelopment/StrategySystem.Instance == / != null`
occurrences across 36 files. The pattern is consistent: every patcher no-ops when its
singleton is missing (`KspStatePatcher.cs:187` science, `:681` tech, `:1014`, `:1160`,
`:1438` parts, `:1795` funds, `:1943` reputation, `:2156`; each logs "(sandbox mode) -
skipping"). `LedgerOrchestrator.GetResourceTrackingAvailability` (`GameActions/LedgerOrchestrator.cs:6385-6393`)
derives tracked pools from presence, so Science mode tracks science only. Seeding
(`LedgerOrchestrator.cs:2118-2309`) and baselines (`GameStateBaseline.cs:50-55`) are guarded.

Timing costs by mode (`ParsekScenario.cs`):
- DeferredSeed phase 1 waits up to `CurrencySingletonWaitMaxFrames = 120` (`:6525`) for ALL
  three singletons (`AllCurrencySingletonsPresent`, `:6540-6545`); Science mode never has
  Funding/Reputation, so it always pays the full 120 frames, then seeds what exists. Sandbox
  exits at `:6397-6402`.
- Phase 2 value wait (`:6405-6413`, 600 frames) - see finding 8.
- `ApplyBudgetDeductionWhenReady` waits 120 frames on `||` of the three (`:6587-6599`),
  i.e. always the full 2 s in Science and Sandbox.
- `ApplyRewindResourceAdjustment` deliberately sets UT unconditionally so Sandbox/Science
  still get the rewind UT (`:6640-6647`). OK.

In-game tests guard by mode with `InGameAssert.Skip` (many `RuntimeTests.cs` StrategySystem /
Reputation cells, `LedgerGroundTruthHarness.cs:78-83` career-only, `LogContractTests.cs:385-426`
Career/Science split, `StockUiFacilityMenuTests.cs:33`, `StockUiStockDecorationTests.cs:374,535`).
Seam readiness: `TestCommands/ParsekTestCommandAddon.cs:2929-2970`, `TestCommandDispatcher.cs:103,619`.

## 3. `GameSettings.*` (global player settings, settings.cfg)

| Site | Read | Use | Verdict |
|---|---|---|---|
| `ParsekTimeFormat.cs:16-37` | `KERBIN_TIME` (live, test override) | day/year constants 21600/426 vs 86400/365 for `FormatDuration`, `FormatDurationFull`, `FormatCountdown`; used by `MissionsWindowUI.FormatCountdownCompact` (`UI/MissionsWindowUI.cs:3905-3921`), `WarpToTimeMath.ComputeTargetUT` (`WarpToTimeMath.cs:51-67`), `SelectiveSpawnUI` | OK for stock calendars. Constants match stock `DefaultDateTimeFormatter` exactly (KerbinDay 21600, KerbinYear 9201600, EarthDay 86400, EarthYear 31536000; `ACS_full.cs:667248-667305`). Speculation: stock exposes a settable `KSPUtil.dateTimeFormatter` (`ACS_full.cs:669177-669202`); a calendar mod replacing it would make Parsek's own formatter and the Warp-to-date field disagree with every `PrintDateCompact` date Parsek shows |
| `UI/LogisticsWindowUI.cs:3918-3929`, `Logistics/RouteCadence.cs:80` | none (hard-coded) | route cadence display / parse | GAP (finding 5) |
| `BallisticExtrapolator.cs:109, 213` | none (hard-coded Earth year 365*24h) | extrapolation horizon cap `maxHorizonYears` | Cosmetic: "years" here is always Earth years (~3.4 Kerbin years per unit); not a display value |
| `GameActions/LedgerOrchestrator.cs:43-44` | `TimeSpan.FromDays(365)` | log rate-limit interval | N/A |
| ~40 `KSPUtil.PrintDateCompact` / `PrintDate` call sites (RecordingsTableUI, MissionsWindowUI, KerbalsWindowUI, LogisticsWindowUI, TimelineWindowUI, StructureListWindowUI, SpawnControlUI, WarpToTimeController, MissionControlStockUi, TimeRangeFilter, `ParsekFlight.cs:11795` flag plaque) | stock formatter | dates | OK - honours KERBIN_TIME and formatter replacement. `UI/TimeRangeFilter.cs:199-201` trims the stock string by position (speculation: fragile if a formatter changes the layout) |
| `GhostPlaybackLogic.cs:4071` | `SHIP_VOLUME` | ghost audio = curve x Parsek volume x `SHIP_VOLUME` x atmosphere | OK, honours player volume |
| `Display/GhostTrajectoryPolylineRenderer.cs:4047-4049` | `ORBIT_FADE_STRENGTH`, `ORBIT_FADE_DIRECTION_INV` | ghost orbit-line material matches stock fade | OK |
| `ParsekFlight.cs:13823-13871` | reflection for a "debris" int | debris persistence override | Dead on 1.12.5 (finding 7). Speculation: if some version/mod did expose it, the override mutates a GLOBAL setting in memory; a player saving Settings mid-recording would persist 10 |
| `TestCommands/ParsekTestCommandAddon.EvaGroundScience.cs:96` | `EVA_Jump` binding identity | automation | N/A |

Not read: `PHYSICS_FRAME_DT_LIMIT`, `ORBIT_DRIFT_COMPENSATION`, `CONIC_PATCH_*`, `UI_SCALE`
(no `UI_SCALE` / `GUI.matrix` scaling in the IMGUI windows; `GuiTreeRecorder.cs:649-672`
only warns when the matrix is non-identity), language (`Recording.cs:1391-1401` resolves
`#autoLOC` vessel names through the CURRENT language at capture time, so stock-craft
recording names are baked in whatever language the player ran).

## 4. Quicksave / revert / scene-exit gating

- `ReFlyRevertButtonGate.cs` forces/resets `FlightDriver.CanRevertToPostInit` (`:143-172`),
  natural value = PRELAUNCH (`:193-205`). Does not consider `Flight.CanRestart` (finding 3).
- `RevertInterceptor.cs:826,842` patches `FlightDriver.RevertToLaunch` / `RevertToPrelaunch`.
- `SceneExitInterceptor.cs:15, 501` explicitly models PauseMenu's `CanRestart` no-save branch
  (design `docs/dev/done/plans/merge-confirm-pretransition.md:62,251,278,346`). OK.
- `SupersedeCommit.cs:923` drops the forced revert override after merge. OK.
- No `CanQuickLoad` / `CanQuickSave` / `CanAutoSave` read in production: Parsek's RP
  quicksaves (`GamePersistence` writes) and rewinds work on a no-quickload career (finding 3).
- `TimeJumpManager.cs:322-365, 481-525` and `WarpToTimeController.cs:292` jump UT via
  `Planetarium.SetUniversalTime`; `ParsekScenario.cs:6647` does the same on rewind. None
  consult `Flight.CanTimeWarpHigh/Low` (a career that disables warp can still time-jump
  through Parsek). Design question, not a crash.

## 5. Parsek's own settings

- `ParsekSettings : GameParameters.CustomParameterNode` (`ParsekSettings.cs:17`) with
  `GameMode => GameParameters.GameMode.NONE` (`:56`): stock's `DifficultyOptionsMenu` skips a
  node whose `(GameMode & filter) == 0`, so NO Parsek section appears in the stock Difficulty
  Settings screen in any mode (fixed GUI-P7, `docs/dev/todo-and-known-bugs.md:4762-4792`;
  pinned by `ParsekSettingsTests.StockDifficultyScreen_DrawsNoParsekSection`). The
  `CustomParameterUI` attributes (`:113-296`) are inert but kept for `autoPersistance`.
- Persistence is two-layer:
  1. Per-save: every public field is written into the save's `PARAMETERS` node by
     `GameParameters.Save`, and restored on every save load (incl. RP quicksaves).
  2. Global sidecar `GameData/Parsek/PluginData/settings.cfg` via
     `ParsekSettingsPersistence` (`ParsekSettingsPersistence.cs:1-90`), applied OVER the
     loaded GameParameters in `ParsekScenario.OnLoad` (`ParsekScenario.cs:3474-3490`). Tracks
     readable sidecar mirrors, route lines, the three tracing toggles, UI complexity mode,
     ghost camera cutoff and the Warp-to-date draft (`warpYear/Day/Hour/Minute`,
     `:51-54`). Because it is GLOBAL, these values are shared across every save and game
     mode. The stored warp draft is a Y/D/H/M tuple interpreted with the current
     `KERBIN_TIME`, so toggling the calendar reinterprets it (minor).
- Hidden fields `autoRecordOnLaunch/OnEva/OnFirstModificationAfterSwitch`, `autoMerge`,
  `forceFaithfulLoopPlayback` are clamped to shipping values on every OnLoad
  (`ParsekSettings.ClampHiddenSettingsToShippingValues`, `ParsekSettings.cs:345-360`) unless
  an automation env hook is armed (`AutomationEnvPresent`, `:370-395`).
- Harness seam: `TestCommands/SettingWhitelist.cs:90-105` (8 GameParameters-only names,
  5 GameParameters+sidecar names), `TestCommands/TestCommandSettingApplier.cs:13-40`
  routes sidecar names through `Record*`.
- `ParsekSettings.LandingBodyAlignmentMode` is a const (`:277`), not a setting.

## 6. Test / harness coverage of settings

- `harness/coverage/registry.toml:656-709` D14 "environment axes (multipliers)" has only
  `career`, `science-mode`, `sandbox` plus bodies/warp/scene cells - no difficulty,
  multiplier, respawn, CommNet-off, calendar or cheat cells despite the header.
  `test_d14_game_mode.py` pins claims against fixture `Mode =` (todo
  D14-GAME-MODE-CLAIMS-UNPINNED, `docs/dev/todo-and-known-bugs.md:3809-3821`).
- Committed harness fixtures (`harness/fixtures/saves/*/persistent.sfs`, 67 checked):
  53 SANDBOX/Normal, 1 CAREER/Normal, 11 CAREER/Custom, 1 SANDBOX/Custom,
  1 SCIENCE_SANDBOX/Custom. ALL carry `ScienceGainMultiplier = 1`, `EnableCommNet = True`,
  `CanQuickLoad = True`. `MissingCrewsRespawn` is True on the 54 Normal ones and False on
  the 13 Custom ones (noted in `docs/dev/done/todo-and-known-bugs-v8.md:18294`).
- xUnit fixtures `Source/Parsek.Tests/Fixtures/C1Career` (Normal) and `C2Career` (Custom):
  all multipliers 1, RepLossDeclined 1, respawn True, bypass True, CommNet True.
- So findings 1, 2, 3 and 5 are structurally invisible to the current test estate.

## 7. Settings Parsek never reads but plausibly should care about

Each item: why it matters, what the code does today, assessment.

- **FundsGainMultiplier / RepGainMultiplier / FundsLossMultiplier / RepLossMultiplier.**
  Stock bakes them into contract rewards at GENERATION (`GameVariables.cs:184-209`), hire
  cost (`GameVariables.GetRecruitHireCost`, `:1121`), kerbal death/recovery rep
  (`Reputation.cs:347,374`) and facility repair. Parsek records the resulting amounts
  (contract fields at completion, `ComputeHireCost` at hire time
  `GameStateRecorder.Handlers.cs:483-512`, the captured VesselLoss event for death rep
  `GameActions/KerbalDeathRepPenalty.cs:35-42`, repair multiplier read at repair time), and
  `Funding.AddFunds` / `Reputation.SetReputation` used by the patcher apply no multiplier
  (decompiled `Funding` has no multiplier reference). So replay is consistent and
  changing a multiplier mid-career affects only future stock computations - OK.
  Exception: ScienceGainMultiplier (finding 1).
- **ScienceGainMultiplier** - GAP, finding 1.
- **RepLossDeclined** - GAP, finding 2.
- **MissingCrewsRespawn / RespawnTimer** - deliberate override plus one latent assumption,
  finding 4. The `RespawnTimer` value itself is irrelevant to Parsek (never reads UTaR).
- **AllowNegativeCurrency.** Parsek's reservations make the displayed pool
  "Total - Reserved" (`CurrencyReservationOverlay.cs:1-20`, `KspStatePatcher.PatchFunds`
  `:1782-1860`). With negative currency allowed a reservation can drive the displayed pool
  negative and stock would still allow spending; Parsek's own click-blocks read the
  committed-future index rather than stock affordability (inferred from
  `CommittedFutureIndex.cs` / `ReservationExplanation.cs` descriptions in CLAUDE.md; not
  traced). Route dispatch refuses on `funds < cost` regardless (section 2.1). Worth a
  targeted check; speculation beyond that.
- **StartingFunds / StartingScience / StartingReputation.** Seeded from the live pool, not
  from the parameter (`LedgerOrchestrator.cs:2118-2309`; design doc
  `docs/parsek-game-actions-and-resources-recorder-design.md:436-441, 800-810` says "extracted
  from the save file - not assumed from defaults"). Handles custom values, except a zero
  StartingFunds (finding 8) and the documented REPUTATION-SEED... limitation
  (`LedgerOrchestrator.cs:2291-2308`).
- **BypassEntryPurchaseAfterResearch** - read and modelled; mid-career toggle caveat (section 1).
- **IndestructibleFacilities.** Parsek reads destruction state from stock
  `ScenarioDestructibles` and only projects ledger destructions after live UT
  (`docs/dev/todo-and-known-bugs.md:2096-2111`). With indestructible = true no destruction
  events occur, so nothing to model - OK. Speculation: a recording made with
  indestructible OFF, replayed after the player toggles it ON, still carries facility
  destruction/repair ledger rows; harmless but the replayed repair costs would be charged.
- **EnableCommNet** - finding 6. If the relay is ever wired up, the check exists but
  `HighLogic.CurrentGame` is not null-guarded there, and nothing re-evaluates on an
  in-game difficulty change.
- **ReentryHeatScale.** Ghost reentry FX intensity is computed from recorded speed/density/
  Mach with hard-coded stock Physics.cfg aeroFX constants
  (`GhostVisualBuilder.cs:29-31, 7528-7560`, `GhostPlaybackEngine.cs:6359`). The heat scale is
  a thermal setting; whether stock also scales the VISUAL aeroFX by it was not verified
  (speculation). Recorded part destruction from overheating is captured as events, so a
  recording made at 50 % heat replays the same outcome later at 100 % - consistent with
  "replay what happened".
- **ResourceAbundance.** Not read. Recordings and harvest-origin routes replay recorded
  amounts (`LiveRouteRuntimeEnvironment.cs:152-160`: harvest origin has no physical source
  gate). A mid-career abundance change does not rescale existing routes - consistent with
  replay semantics; speculation that a player might expect it to.
- **EnableKerbalExperience / ImmediateLevelUp.** Parsek appends careerLog entries and calls
  stock `UpdateExperience()` (`KerbalsModule.cs:2126-2150`), and stock
  `CalculateExperiencePoints` applies `KerbalExperienceEnabled(mode)` / `ImmediateLevelUp`
  (decompiled `ProtoCrewMember.UpdateExperience`). Delegation makes it OK.
- **CanQuickLoad / CanRestart / CanTimeWarp*** - finding 3 and section 4.
- **CanSwitchVesselsNear, CanEVA, CanBoard, CanUseMap.** Parsek does not spawn EVAs or
  switch vessels on the player's behalf outside stock flows except intent-driven switches
  (`MergeDialog.ShowPreSwitchDecisionDialog` handlers call `FlightGlobals.SetActiveVessel`
  after a stock click that already passed the Far gate). OK as far as traced.
- **AllowOtherLaunchSites / Making History sites.** Launch-site capture
  (`FlightRecorder.ResolveLaunchSiteName` / `HumanizeLaunchSiteName`) is registry cell D17
  `making-history`, not flown. Speculation: replay/spawn at an alt site in a career with
  `AllowOtherLaunchSites = false` is untested.
- **persistKerbalInventories.** Logistics moves stored-part inventory
  (`LiveInventoryPickupWriter`, CLAUDE.md). Speculation: kerbal-inventory persistence on
  recovery interacting with inventory-carrying recordings was not examined.
- **G-limits / PressurePartLimits / ResourceTransferObeyCrossfeed / ActionGroupsAlways.**
  Only affect live physics; recordings capture outcomes as events. The claw research doc
  notes crossfeed-obey matters for grapple resource flow
  (`docs/dev/research/claw-grapple-coupling-internals.md:209`). Low relevance.
- **CheatOptions (Alt+F12).** Never read. Inferred consequences:
  - Cheat currency (TransactionReasons.Cheating) is emitted as a FundsChanged event
    (`GameStateRecorder.cs:520-551`) but never converted (`GameStateEventConverter.cs:288-306`
    only keeps StrategyOutput/StrategyInput), so cheated funds/science/rep are held only by
    the UP drawdown clamp ("Kept your earned funds") until an authoritative recalc (rewind)
    wipes them.
  - InfinitePropellant / InfiniteElectricity: recordings show no consumption; route cost
    manifests and resource deltas derived from snapshots would under-state cost
    (speculation - route manifest derivation not traced).
  - NoCrashDamage / UnbreakableJoints / IgnoreMaxTemperature: recordings simply lack the
    destruction events; replay is faithful to what happened (OK).
  - "Set Orbit" / teleport cheats create trajectory discontinuities in a live recording
    (speculation - no guard found).
- **KERBIN_TIME** - handled centrally except finding 5.
- **UI_SCALE** - Parsek IMGUI windows ignore it (speculation that players with UI scale
  > 100 % see small Parsek windows next to scaled stock UI).
- **Language** - see section 3 (vessel names resolved at capture time).

## 8. Existing documented decisions (docs grep)

- Stock Difficulty screen must not show Parsek settings (GUI-P7, fixed 2026-09-14).
- Science-mode facility destruction is possible; `IndestructibleFacilities` true only on Easy
  (`docs/dev/todo-and-known-bugs.md:2096-2111`).
- Career window / launcher is Career-only; Science shows Timeline career view
  (`docs/dev/todo-and-known-bugs.md:2086-2126`).
- CommNet relay ruling pending (GUI-D3, `docs/dev/todo-and-known-bugs.md:4796-4807`).
- MIA respawn of a reserved kerbal is the intended state (`KerbalsModule.cs:2311`,
  `LedgerGroundTruthDiff.cs:750-872`).
- Reputation curve and preset multipliers documented in
  `docs/dev/done/game-actions/game-actions-spike-findings.md:55-68` ("applied by the caller
  before AddReputation").
- Contract Decline costs `RepLossDeclined`, "a difficulty setting that can be zero"
  (`docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md:284`) - noted for UI
  purposes only; no ledger item filed.
- Seeds come from the save, not from defaults
  (`docs/parsek-game-actions-and-resources-recorder-design.md:436-441`).
- No todo entry found for ScienceGainMultiplier, RepLossDeclined in the ledger,
  CanQuickLoad/CanRestart vs rewind/re-fly, the logistics day constant, or cheats.

## 9. Stock difficulty presets (decompiled, for reference)

`GameParameters.SetDifficultyPresets` (`ACS_full.cs:160560-160660`):
- Easy: IndestructibleFacilities true, ResourceAbundance 1.2, ReentryHeatScale 0.5,
  EnableCommNet false, StartingFunds 250000, FundsGain 2, RepGain 2, ScienceGain 2,
  FundsLoss 0.5, RepLoss 0.5, RepLossDeclined 0.
- Normal: field defaults (MissingCrewsRespawn true, Bypass true, RepLossDeclined 1, all x1).
- Moderate: MissingCrewsRespawn false, Bypass false, Abundance 0.8, StartingFunds 15000,
  FundsGain 0.9, RepGain 0.9, ScienceGain 0.9, FundsLoss 1.5, RepLoss 1.5, RepLossDeclined 2.
- Hard: as Moderate plus Abundance 0.5, `Flight.CanRestart = false`,
  `Flight.CanQuickLoad = false`, StartingFunds 10000, gains 0.6, losses 2, RepLossDeclined 3.
