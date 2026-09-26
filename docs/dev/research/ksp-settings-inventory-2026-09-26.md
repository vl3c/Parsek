# Stock KSP 1.12.5 player-facing settings inventory

Source-verified inventory for the Parsek settings-interaction analysis.

Sources used:
- `ilspycmd` full decompile of `Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll` (1.12.5.3190) (3299 .cs files, not committed). Field names, attributes, enum members and preset code below are copied from the decompile; call sites come from a mechanical grep of `GameSettings.<X>` / `Parameters.<Section>.<X>` / `CustomParams<T>().<X>` over the whole decompile.
- `Kerbal Space Program/settings.cfg` (the live dev-instance GameSettings file; NOTE the dev instance is modded and some values are non-default, see the "dev value" column).
- `Kerbal Space Program/saves/c2/persistent.sfs` PARAMETERS + SCENARIO blocks (serialized names).
- English labels resolved from `GameData/Squad/Localization/dictionary.cfg` and `GameData/SquadExpansion/*/Localization/dictionary.cfg` (en-us).
- Both DLCs are installed in the dev instance: `GameData/SquadExpansion/MakingHistory` and `GameData/SquadExpansion/Serenity` (Breaking Ground).

Relevance tags used in the "Rel" column (for the Parsek analysis):
- SIM = changes simulation outcome (physics, heating, damage, resources, orbits, crew survival).
- PERS = persistence / save-load / vessel-set / revert / quickload / autosave behaviour.
- TIME = UT display, calendar, time warp, SOI/warp limits.
- CAREER = funds / science / reputation / contracts / tech / facilities / crew roster economy.
- VIS = rendering / FX / map-line display only.
- UI = HUD / window / dialog only.
- IN = input binding.
- DEV = logging / debug / telemetry.

---

## 1. New-game setup

### 1.1 Game modes (`Game.Modes`, serialized as `GAME/Mode = <name>`)

| Value (int) | Name | UI label | Where chosen | Notes |
|---|---|---|---|---|
| 0 | SANDBOX | "Sandbox" (#autoLOC_190706) | Main menu > Start New | No funds/science/rep scenarios; `GetDefaultParameters` forces `BypassEntryPurchaseAfterResearch=true`, `AllowStockVessels = (preset != Hard)`, `EnableKerbalExperience=false`. |
| 1 | CAREER | "Career" (#autoLOC_190722) | Main menu > Start New (default selection) | Funding / Reputation / R&D / Contracts / Strategies / facility upgrades. |
| 2 | SCENARIO | "Scenario" (#autoLOC_6003000) | Main menu > Scenarios / Training | Resumable scenario saves (`saves/scenarios`). |
| 3 | SCENARIO_NON_RESUMABLE | "Scenario" | Training / tutorials | Not resumable. |
| 4 | SCIENCE_SANDBOX | "Science" (#autoLOC_190714) | Main menu > Start New | Science + R&D, no funds/rep; forces `BypassEntryPurchaseAfterResearch=true`, `EnableKerbalExperience=false`. |
| 5 | MISSION | (no Description attr) | Making History > Play Missions | Forces `SpaceCenter.CanGoToRnD/CanGoToAdmin/CanGoToMissionControl=false`, `AllowOtherLaunchSites=false`, `BypassEntryPurchaseAfterResearch=true`, `EnableKerbalExperience=true`, `persistKerbalInventories=false`. |
| 6 | MISSION_BUILDER | (no Description attr) | Making History > Mission Builder | `Game.IsMissionMode` is true for MISSION and MISSION_BUILDER. |

`GameParameters.GameMode` flags enum (used by `CustomParameterUI.gameMode` filters, NOT the same numbering as `Game.Modes`): NONE=0, SANDBOX=1, SCIENCE=2, CAREER=4, MISSION=8, NOTMISSION=7, ANY=15. `DifficultyOptionsMenu` maps Game.Modes -> filter: SANDBOX->1, SCIENCE_SANDBOX->2, CAREER->4, MISSION and MISSION_BUILDER->8.

Default for CAREER/SCENARIO paths (`default:` branch of `GetDefaultParameters`): `EnableKerbalExperience=true`, `AllowOtherLaunchSites=false`, `persistKerbalInventories=false`.

### 1.2 New-game dialog fields (`MainMenu`, `GamePersistence.CreateNewGame(name, mode, newGameParameters, flagURL, startScene, editorFacility)`)

| Field | Serialized as | Notes |
|---|---|---|
| Save name | save folder name, `GAME/Title` = "<name> (<MODE>)" | |
| Game mode | `GAME/Mode` | Sandbox / Science / Career radio (MainMenu.newGameMode, default CAREER). |
| Flag | `GAME/flag` (Game.flagURL) | Flag browser. Changeable later at KSC flag pole (`SpaceCenter.CanSelectFlag`). |
| Difficulty preset + Difficulty Options | `PARAMETERS` node | Button opens `DifficultyOptionsMenu.Create(mode, params, newGame: true, ...)`. |
| (implicit) Seed | `GAME/Seed` (int, -1 = unset) | Procedural seed (asteroids, crew names etc.). Not player-editable in UI. |
| (implicit) ROCSeed | `GAME/ROCSeed` | Breaking Ground surface-feature (ROC) seed. Not player-editable. |
| (implicit) launchID | `GAME/launchID` (uint, starts 1) | Increments per launch; stock uses it for part `launchID`. |
| (implicit) defaultVABLaunchSite / defaultSPHLaunchSite | `GAME/defaultVABLaunchSite = LaunchPad`, `defaultSPHLaunchSite = Runway` | Last-used launch site per editor (Making History launch sites). |
| (implicit) versionCreated / versionFull / modded / envInfo | `GAME/...` | Informational. |

### 1.3 Difficulty presets (`GameParameters.Preset`, serialized `PARAMETERS/preset = <name>`)

Enum: Easy (#autoLOC_7000039), Normal, Moderate, Hard, Custom. The preset buttons are shown ONLY when `isNewGame`; opening Difficulty Options mid-game (`newGame:false`) immediately forces `preset = Custom`. Editing any control also flips the preset to Custom.

Values set by `GameParameters.SetDifficultyPresets()` (base) plus each `CustomParameterNode.SetDifficultyPreset()`; anything not listed = class default (see section 2). Custom = plain `new GameParameters()` (class defaults).

| Parameter | Class default | Easy | Normal | Moderate | Hard |
|---|---|---|---|---|---|
| Difficulty.AllowStockVessels | false | true | false | false | false |
| Difficulty.AllowOtherLaunchSites | false | true | true | true | true (then forced false by mode except SANDBOX/SCIENCE) |
| Difficulty.IndestructibleFacilities | false | true | false | false | false |
| Difficulty.MissingCrewsRespawn | true | true | true | false | false |
| Difficulty.BypassEntryPurchaseAfterResearch | true | true | true | false | false (forced true in Sandbox/Science/Mission) |
| Difficulty.ResourceAbundance | 1.0 | 1.2 | 1.0 | 0.8 | 0.5 |
| Difficulty.ReentryHeatScale | 1.0 | 0.5 | 1.0 | 1.0 | 1.0 |
| Difficulty.EnableCommNet | false | false | true | true | true |
| Difficulty.persistKerbalInventories | false | false | false | false | false |
| Difficulty.RespawnTimer | GameSettings.DEFAULT_KERBAL_RESPAWN_TIMER (7200 s) | same | same | same | same |
| Flight.CanRestart (revert) | true | true | true | true | false |
| Flight.CanLeaveToEditor | true | true | true | true | false |
| Flight.CanQuickLoad | true | true | true | true | false |
| Career.StartingFunds | 25000 | 250000 | 25000 | 15000 | 10000 |
| Career.StartingScience | 0 | 0 | 0 | 0 | 0 |
| Career.StartingReputation | 0 | 0 | 0 | 0 | 0 |
| Career.FundsGainMultiplier | 1.0 | 2.0 | 1.0 | 0.9 | 0.6 |
| Career.RepGainMultiplier | 1.0 | 2.0 | 1.0 | 0.9 | 0.6 |
| Career.ScienceGainMultiplier | 1.0 | 2.0 | 1.0 | 0.9 | 0.6 |
| Career.FundsLossMultiplier | 1.0 | 0.5 | 1.0 | 1.5 | 2.0 |
| Career.RepLossMultiplier | 1.0 | 0.5 | 1.0 | 1.5 | 2.0 |
| Career.RepLossDeclined | 1.0 | 0 | 1.0 | 2.0 | 3.0 |
| Career.TechTreeUrl | GameData/Squad/Resources/TechTree.cfg | same | same | same | same |
| AdvancedParams.BuildingImpactDamageMult | 0.05 | 0.03 | 0.05 | 0.1 | 0.2 |
| AdvancedParams.ImmediateLevelUp | false | true | false | false | false |
| AdvancedParams.AllowNegativeCurrency | false | false | false | true | true |
| AdvancedParams.ResourceTransferObeyCrossfeed | false | false | false | true | true |
| CommNetParams.requireSignalForControl | false | false | false | false | false |
| CommNetParams.rangeModifier | 1.0 | 1.5 | 1.0 | 0.8 | 0.65 |
| CommNetParams.occlusionMultiplierVac | 0.9 | 0 | 0.9 | 1.0 | 1.0 |
| CommNetParams.occlusionMultiplierAtm | 0.75 | 0 | 0.75 | 0.85 | 1.0 |
| CommNetParams.plasmaBlackout | false | false | false | false | false |
| CommNetParams.DSNModifier / enableGroundStations | 1.0 / true | unchanged | unchanged | unchanged | unchanged |
| MissionParamsGeneral.enableFunding | false | false | false | true | true |
| MissionParamsGeneral.startingFunds | 100000 | 250000 | 100000 | 25000 | 10000 |
| MissionParamsGeneral.enableKerbalLevels | false | false | false | true | true |
| MissionParamsGeneral.kerbalLevelPilot/Scientist/Engineer | 4/4/4 | 5/5/5 | 4/4/4 | 2/2/2 | 1/1/1 |
| MissionParamsGeneral.preventVesselRecovery | false | false | false | false | true |
| MissionParamsExtras.facilityOpenAC | false | false | false | true | true |
| MissionParamsExtras.astronautHiresAreFree | true | true | true | true | false |
| MissionParamsExtras.facilityOpenEditor | false | false | false | false | false |
| MissionParamsExtras.launchSitesOpen | false | true | true | true | true |
| MissionParamsExtras.cheatsEnabled | false | true | false | false | false |
| MissionParamsFacilities.facilityLevel* (9 fields) | 3 | 3 | 3 | 2 | 1 |

Quirk worth knowing: in `SetDifficultyPresets` the Normal preset object is added to the dictionary and THEN `EnableCommNet = true` is set on the same reference, so Normal has CommNet on (verified). Easy explicitly sets `EnableCommNet = false`.

AdvancedParams.EnableKerbalExperience has no preset value; it is a nullable backing field defaulting to "Mode is CAREER or MISSION", and `GetDefaultParameters` sets it per mode (false Sandbox/Science, true otherwise). `AdvancedParams.OnLoad` back-fills it (CAREER -> true) when the key is missing.

---

## 2. GameParameters (per-save, `GAME/PARAMETERS`, `HighLogic.CurrentGame.Parameters`)

Editing surfaces:
- New game: `MainMenu` -> `DifficultyOptionsMenu.Create(mode, params, newGame: true, ...)`.
- Mid-game: pause menu (Esc) in any scene -> "Settings" (`MiniSettings`) -> "Difficulty Options: <preset>" button -> `DifficultyOptionsMenu.Create(HighLogic.CurrentGame.Mode, HighLogic.CurrentGame.Parameters, newGame: false, ...)`. The button is always enabled (`() => true`).
- Mission Builder: `MEGUIParameterGameParameters` -> `DifficultyOptionsMenu.Create(Game.Modes.MISSION, ..., newGame: true)` (mission author sets the mission's parameters).
- Debug menu (Alt+F12) Gameplay > Difficulty screen (`KSP.UI.Screens.DebugToolbar.Screens.GamePlay.Difficulty`) writes directly: AllowStockVessels, MissingCrewsRespawn, Flight.CanRestart, Flight.CanLeaveToEditor, Flight.CanQuickSave, Flight.CanQuickLoad, CheatOptions.IgnoreAgencyMindsetOnContracts.
- Anything else only via editing persistent.sfs (Flight/Editor/TrackingStation/SpaceCenter mostly exist for scenarios, tutorials and missions).

Mid-game rules in `DifficultyOptionsMenu`:
- Hidden when `!isNewGame`: preset buttons; Difficulty.ResourceAbundance; Career.StartingFunds/StartingScience/StartingReputation; Difficulty.BypassEntryPurchaseAfterResearch (shown only for CAREER+newGame, or MISSION); every CustomParameterUI with `newGameOnly = true` (AdvancedParams.ActionGroupsAlways, AdvancedParams.EnableKerbalExperience).
- Everything else below marked "UI" is editable mid-game.

### 2.1 FlightParams (`PARAMETERS/FLIGHT`, `Parameters.Flight`)

| Field | Type | Default | UI | Rel | Read by |
|---|---|---|---|---|---|
| CanQuickSave | bool | true | debug Difficulty screen only | PERS | QuickSaveLoad, PauseMenu, KSCPauseMenu |
| CanQuickLoad | bool | true (Hard: false) | "Allow Quickloading" (#autoLOC_189670), all modes | PERS | QuickSaveLoad, PauseMenu, KSCPauseMenu |
| CanAutoSave | bool | true | none | PERS | FlightAutoSave, GamePersistence |
| CanUseMap | bool | true | none | UI | MapView, CameraManager |
| CanSwitchVesselsNear | bool | true | none | PERS | VesselSwitching ([ / ] keys) |
| CanSwitchVesselsFar | bool | true | none | PERS | MapContextMenuOptions.FocusObject, OrbitRendererBase |
| CanTimeWarpHigh | bool | true | none | TIME | TimeWarp, UITimeWarpController |
| CanTimeWarpLow | bool | true | none | TIME | TimeWarp |
| CanEVA | bool | true | none | SIM | KerbalPortrait, CrewHatchDialog, GameVariables, tutorials |
| CanIVA | bool | true | none | UI | CameraManager, KerbalPortrait |
| CanBoard | bool | true | none | SIM | KerbalEVA |
| CanRestart | bool | true (Hard: false) | "Allow Reverting Flights" (#autoLOC_189662); toggling also sets CanLeaveToEditor to the same value | PERS | PauseMenu, KSCPauseMenu, FlightResultsDialog, AltimeterSliderButtons |
| CanLeaveToEditor | bool | true (Hard: false) | via CanRestart toggle; debug screen separately | PERS | PauseMenu, FlightResultsDialog (revert to VAB/SPH) |
| CanLeaveToTrackingStation | bool | true | none | PERS | PauseMenu, FlightResultsDialog |
| CanLeaveToSpaceCenter | bool | true | none | PERS | PauseMenu, FlightResultsDialog, AltimeterSliderButtons, Game |
| CanLeaveToMainMenu | bool | true | none | PERS | PauseMenu, FlightResultsDialog, MissionEndDialog |

### 2.2 EditorParams (`PARAMETERS/EDITOR`, `Parameters.Editor`) - no player UI

| Field | Type | Default | Read by |
|---|---|---|---|
| CanSave | bool | true | EditorDriver |
| CanLoad | bool | true | EditorDriver |
| CanStartNew | bool | true | EditorDriver |
| CanLaunch | bool | true | EditorDriver |
| CanLeaveToSpaceCenter | bool | true | EditorDriver, EditorLogic |
| CanLeaveToMainMenu | bool | false | EditorDriver, EditorLogic |
| startUpMode | int | 0 | Game, MissionSystem, MEFlowUINode |
| craftFileToLoad | string | "" | Game |

### 2.3 TrackingStationParams (`PARAMETERS/TRACKINGSTATION`) - no player UI

| Field | Type | Default | Read by |
|---|---|---|---|
| CanFlyVessel | bool | true | SpaceTracking |
| CanAbortVessel | bool | true | SpaceTracking (Terminate / Recover) |
| CanLeaveToSpaceCenter | bool | true | SpaceTracking |
| CanLeaveToMainMenu | bool | false | SpaceTracking |

### 2.4 SpaceCenterParams (`PARAMETERS/SPACECENTER`) - no player UI (MISSION mode forces RnD/Admin/MissionControl false)

| Field | Type | Default | Read by |
|---|---|---|---|
| CanGoInVAB | bool | true | VehicleAssemblyBuilding, MissionSystem, tutorials |
| CanGoInSPH | bool | true | SpacePlaneHangarBuilding, MissionSystem |
| CanGoInTrackingStation | bool | true | TrackingStationBuilding, PauseMenu |
| CanLaunchAtPad | bool | true | LaunchSiteFacility |
| CanLaunchAtRunway | bool | true | (no reader found by grep) |
| CanGoToAdmin | bool | true | AdministrationFacility, MissionSystem |
| CanGoToAstronautC | bool | true | AstronautComplexFacility, MissionSystem |
| CanGoToMissionControl | bool | true | MissionControlBuilding, MissionSystem |
| CanGoToRnD | bool | true | RnDBuilding, MissionSystem, TutorialScience |
| CanSelectFlag | bool | true | FlagPoleFacility |
| CanLeaveToMainMenu | bool | true | KSCPauseMenu |

### 2.5 DifficultyParams (`PARAMETERS/DIFFICULTY`, `Parameters.Difficulty`)

"Basic" tab (`#autoLOC_190231`) "General Options" box and "Game Systems" box; "Career Options" box for career fields.

| Field | Type | Default | UI label | Modes shown | Mid-game | Rel | Read by |
|---|---|---|---|---|---|---|---|
| AutoHireCrews | bool | false | "Auto-Hire Crewmembers before Flight" | all but MISSION/MISSION_BUILDER | yes | CAREER | KerbalRoster, MissionSystem |
| MissingCrewsRespawn | bool | true | "Missing Crews Respawn" | all but MISSION/MISSION_BUILDER | yes | CAREER / SIM (dead/MIA kerbals return) | ProtoCrewMember, Part, AstronautComplex |
| RespawnTimer | float (s) | 7200 (= GameSettings.DEFAULT_KERBAL_RESPAWN_TIMER) | "Crew respawn timer: N hours" slider 1-100 h | same | yes | CAREER / TIME | ProtoCrewMember |
| BypassEntryPurchaseAfterResearch | bool | true | "No Entry Purchase Required on Research" | CAREER (new game only), MISSION (interactable only if enableFunding) | no (career) | CAREER | Funding, RDTech, ResearchAndDevelopment, PartUpgradeHandler, PartListTooltip |
| AllowStockVessels | bool | false | "Include Stock Vessels" | all | yes | UI (craft browser lists stock craft) | LoadCraftDialog, VesselSpawnDialog, DirectoryController, LaunchSiteFacility |
| IndestructibleFacilities | bool | false | "Indestructible Facilities" | all | yes | SIM (KSC building damage) | DestructibleBuilding |
| ResourceAbundance | float | 1.0 | "Resource Abundance: N%" slider 10-120% | all | NO (new game only) | SIM | ResourceUtilities |
| ReentryHeatScale | float | 1.0 | "Re-Entry Heating: N%" slider 0-120% | all | yes | SIM | FlightIntegrator |
| EnableCommNet | bool | false (Normal+ true) | "Enable Comm Network" | all | yes | SIM (probe control) | CommNetScenario, FullVesselControlSkill; gates CommNetParams UI interactability |
| AllowOtherLaunchSites | bool | false | "Allow other Launchsites" (only if Making History installed) | all but MISSION/MISSION_BUILDER | yes | PERS (launch sites) | EditorDriver, CommNetHome |
| persistKerbalInventories | bool | false | "Persist Kerbal Inventory Loadout" | all | yes | CAREER | PartCrewManifest |

### 2.6 CareerParams (`PARAMETERS/CAREER`, `Parameters.Career`)

| Field | Type | Default | UI label | Modes | Mid-game | Rel | Read by |
|---|---|---|---|---|---|---|---|
| TechTreeUrl | string | GameData/Squad/Resources/TechTree.cfg | none (mods/save edit; dev c2 uses HETTN) | CAREER, SCIENCE | n/a | CAREER | ResearchAndDevelopment, RDTechTree, RnDDebugUtil |
| StartingFunds | float | 25000 | "Starting Funds: N" slider 0-500k step 1k | CAREER | NO | CAREER | Funding (initial pool) |
| StartingScience | float | 0 | "Starting Science: N" slider 0-5000 step 10 | CAREER | NO | CAREER | ResearchAndDevelopment |
| StartingReputation | float | 0 | "Starting Reputation: N" slider -1000..1000 step 10 | CAREER | NO | CAREER | Reputation |
| FundsGainMultiplier | float | 1.0 | "Funds Rewards: N%" 10-1000% | CAREER | yes | CAREER | GameVariables |
| RepGainMultiplier | float | 1.0 | "Reputation Rewards: N%" | CAREER | yes | CAREER | GameVariables, Reputation |
| ScienceGainMultiplier | float | 1.0 | "Science Rewards: N%" | CAREER, SCIENCE | yes | CAREER | GameVariables, ResearchAndDevelopment, ExperimentResultDialogPage, RDArchivesController |
| FundsLossMultiplier | float | 1.0 | "Funds Penalties: N%" | CAREER | yes | CAREER | GameVariables, SpaceCenterBuilding, UpgradeableFacility |
| RepLossMultiplier | float | 1.0 | "Reputation Penalties: N%" | CAREER | yes | CAREER | GameVariables, Reputation, SpaceCenterBuilding |
| RepLossDeclined | float | 1.0 | "Decline Penalty: N" 0-10 | CAREER | yes | CAREER | Contracts.Contract |

### 2.7 CustomParameterNode subclasses in stock Assembly-CSharp (complete list)

Found by grepping for `: GameParameters.CustomParameterNode` / `: CustomParameterNode`: exactly five stock types. Custom nodes are discovered at startup over ALL loaded assemblies (`GameParameters.GenerateParameterTypes`, logs `[GameParameters]: Loaded custom parameter class X.`), saved as `PARAMETERS/<TypeName>`, and rendered in DifficultyOptionsMenu tabs by `Section`. Mods add their own (dev c2 save shows ParsekSettings, HETTNCustomParams_*, BTWCustomParams*, CTB, TC).

| Type | Section / tab | Title | GameMode | HasPresets |
|---|---|---|---|---|
| GameParameters.AdvancedParams | "Advanced" (order 0) | "Advanced Options" | ANY | yes |
| CommNet.CommNetParams | "Advanced" (order 1) | "CommNet Options" | ANY | yes |
| Expansions.Missions.MissionParamsGeneral (internal) | "Mission" (order 0) | "Gameplay Options" | MISSION | yes |
| Expansions.Missions.MissionParamsFacilities (internal) | "Mission" (order 1) | "Facility Options" | MISSION | yes |
| Expansions.Missions.MissionParamsExtras (internal) | "Mission" (order 2) | "Extra Options" | MISSION | yes |

`CustomParameterUI` attribute fields: title, toolTip, gameMode (default ANY), newGameOnly, autoPersistance (default true), unlockedDuringMission. Subclasses CustomIntParameterUI (min/max/stepSize/displayFormat), CustomFloatParameterUI (min/max/logBase/stepCount/displayFormat/asPercentage/addTextField), CustomStringParameterUI (lines).

#### 2.7.1 AdvancedParams (`PARAMETERS/AdvancedParams`, `CustomParams<GameParameters.AdvancedParams>()`)

| Field | Type | Default | UI label | gameMode filter | newGameOnly | Interactable when | Rel | Read by |
|---|---|---|---|---|---|---|---|---|
| AllowNegativeCurrency | bool | false | "Allow Negative Funds/Science" | 14 = SCIENCE+CAREER+MISSION | no | always | CAREER | Funding, ResearchAndDevelopment |
| PressurePartLimits | bool | false | "Part Pressure Limits" | ANY | no | always | SIM | Part, Vessel |
| GPartLimits | bool | false | "Part G-Force Limits" | ANY | no | always | SIM | Part, Vessel |
| GKerbalLimits | bool | false | "Kerbal G-Force Limits" (G-LOC) | ANY | no | always | SIM | ProtoCrewMember, GeeForceTolerance, KerbalPortrait, TourismContract |
| KerbalGToleranceMult | float | 1.0 | "Kerbal G-Force Tolerance" 0.01-10 log | ANY | no | GKerbalLimits | SIM | ProtoCrewMember |
| ResourceTransferObeyCrossfeed | bool | false | "Resource Transfer Obeys Crossfeed Rules" | ANY | no | always | SIM | UIPartActionController |
| ActionGroupsAlways | bool | false | "Always Allow ActionGroups" | 12 = CAREER+MISSION | YES | always | UI (editor AG availability) | GameVariables |
| BuildingImpactDamageMult | float | 0.05 | "Building Impact Damage Multiplier" 0.01-1 log | ANY | no | !Difficulty.IndestructibleFacilities | SIM | DestructibleBuilding |
| EnableKerbalExperience (property over bool?) | bool | CAREER/MISSION true, else false | "Enable Kerbal Experience" | ANY | YES | always | CAREER / SIM (skills) | ModuleExperienceManagement; KerbalExperienceEnabled(mode) read by KerbalRoster, APSkillExtensions, CrewWidget, CheatsCareer and ~10 others |
| ImmediateLevelUp (auto-property) | bool | false (Easy true) | "Kerbals Level Up Immediately" | NOTMISSION | no | EnableKerbalExperience | CAREER | ProtoCrewMember, ModuleExperienceManagement |
| PartUpgradesInSandbox | bool | false | "All Part Upgrades Applied in Sandbox" | SANDBOX | no | always | SIM (part stats) | PartUpgradeHandler |
| PartUpgradesInCareer | bool | true | "Part Upgrades" | 6 = SCIENCE+CAREER | no | always | SIM | PartUpgradeHandler |
| PartUpgradesInMission | bool | false | "All Part Upgrades Applied in Mission" | MISSION | no | always | SIM | PartUpgradeHandler |
| EnableFullSASInSandbox | bool | false | "All SAS Modes on all probes" | 3 = SANDBOX+SCIENCE | no | always | SIM | APSkillExtensions |
| EnableFullSASInMissions | bool | false | "All SAS Modes on all probes" | MISSION | no | always | SIM | APSkillExtensions |

#### 2.7.2 CommNetParams (`PARAMETERS/CommNetParams`, `CustomParams<CommNet.CommNetParams>()`); whole block interactable only when `Difficulty.EnableCommNet`

| Field | Type | Default | UI label | Range | Rel | Read by |
|---|---|---|---|---|---|---|
| requireSignalForControl | bool | false | "Require Signal for Control" | | SIM | ModuleCommand |
| plasmaBlackout | bool | false | "Plasma Blackout" | | SIM | CommNetVessel |
| rangeModifier | float | 1.0 | "Range Modifier" | 0.1-100 log | SIM | CommNetVessel |
| DSNModifier | float | 1.0 | "DSN Modifier" | 0-100 log | SIM | GameVariables |
| occlusionMultiplierVac | float | 0.9 | "Occlusion Modifier, Vac" | 0-1.1 | SIM | CommNetBody |
| occlusionMultiplierAtm | float | 0.75 | "Occlusion Modifier, Atm" | 0-1.1 | SIM | CommNetBody |
| enableGroundStations | bool | true | "Enable Extra Groundstations" | | SIM | CommNetHome |

#### 2.7.3 MissionParamsGeneral (Making History, MISSION only)

| Field | Type | Default | UI label | Interactable when | Read by |
|---|---|---|---|---|---|
| enableFunding | bool | false | "Enable Funding" | always | GamePersistence |
| startingFunds | float | 100000 | "Starting Funds" 0-1,000,000 (+text field) | enableFunding | Funding |
| spacer | string | "" | (layout spacer) | | |
| enableKerbalLevels | bool | false | "Enable Kerbal Levels" | always | CrewGenerator |
| kerbalLevelPilot | int | 4 | "Pilot Max Level" 0-5 | enableKerbalLevels | CrewGenerator |
| kerbalLevelScientist | int | 4 | "Scientist Max Level" 0-5 | enableKerbalLevels | CrewGenerator |
| kerbalLevelEngineer | int | 4 | "Engineer Max Level" 0-5 | enableKerbalLevels | CrewGenerator |
| spacer2 | string | "" | (spacer) | | |
| preventVesselRecovery | bool | false | "Prevent Recovery" | always | AltimeterSliderButtons, KSCVesselMarker, SpaceTracking, LaunchSiteFacility |

#### 2.7.4 MissionParamsExtras (MISSION only)

| Field | Type | Default | UI label | Interactable when | Read by |
|---|---|---|---|---|---|
| facilityOpenAC | bool | false | "Astronaut Complex Open" | always | MissionSystem |
| astronautHiresAreFree | bool | true | "Astronauts are Volunteers" | facilityOpenAC | AstronautComplex |
| facilityOpenEditor | bool | false | "VAB/SPH Open" | always | MissionSystem, EditorDriver, LaunchSiteFacility |
| launchSitesOpen | bool | false | "Other Launchsites Open" | facilityOpenEditor | EditorDriver |
| cheatsEnabled | bool | false (Easy true) | "Cheats Enabled" | always | DebugScreen (gates Alt+F12 in MISSION mode) |
| spacer, spacer2 | string | "" | spacers | | |

#### 2.7.5 MissionParamsFacilities (MISSION only), int 1-3, default 3 (Moderate 2, Hard 1)

facilityLevelAdmin ("Administration Level"), facilityLevelAC ("Astronaut Complex Level"), facilityLevelLaunchpad ("Launchpad Level"), facilityLevelMC ("Mission Control Level"), facilityLevelRD ("R&D Level"), facilityLevelRunway ("Runway Level"), facilityLevelSPH ("SPH Level"), facilityLevelTS ("Tracking Station Level"), facilityLevelVAB ("VAB Level"). Read by `Expansions.Missions.MissionsUtils`; Launchpad/Runway/SPH/VAB also by `ActionCreateVessel`.

---

## 3. GameSettings (global, `settings.cfg` next to KSP_x64.exe; static fields on `GameSettings`)

Counts: 306 scalar `KEY = value` lines at top level of the dev settings.cfg, plus 142 child nodes (INPUT_DEVICES, 139 key/axis binding nodes including AXIS_CUSTOM, TRACKIR, TERRAIN). `GameSettings` declares the same scalars (plus internal `UI_SCALE_DISABLEDMODEANDSTAGE`, which is written but not a public field). Global, not per-save: every save on the install shares these. Applied by `GameSettings.ApplySettings()` (e.g. `Time.maximumDeltaTime = PHYSICS_FRAME_DT_LIMIT`, `Application.runInBackground = SIMULATE_IN_BACKGROUND`, `QualitySettings.pixelLightCount = LIGHT_QUALITY`).

Edit surfaces:
- Main menu > Settings (`KSP.UI.Screens.Settings.SettingsScreen`, prefab-driven reflected controls; tabs General / Graphics / Input, sub-sections seen in localization: Gameplay, System, Scenery, Volume, Sound Normalizer, Video, Rendering, Vessel, Game, Controllers, Mouse, Character Controls, Editors, Track IR). Exact per-control membership of the main-menu screen lives in the prefab and could not be enumerated from code; labels below marked "(main)" come from the #autoLOC_19007xx-19009xx settings label block.
- In-game pause menu > Settings (`MiniSettings`): Audio volumes (`AudioFXSettings.DrawMiniSettings`), Video subset (`VideoSettings.DrawMiniSettings`), Gameplay/UI subset (`GameplaySettingsScreen.DrawMiniSettings`), Alarm Clock per-game settings (`AlarmClockSettingsUI`), plus the Difficulty Options button. Marked "(in-game)".
- Several keys have NO UI at all (edit settings.cfg by hand). Marked "cfg only" where verified absent from both code-driven screens and the label block; otherwise "UI unverified".

Columns: Default = value assigned in `GameSettings.SetDefaultValues()`; Dev = dev settings.cfg value when it differs.

### 3.1 Simulation, physics, persistence and time (the Parsek-critical set)

| Key | Type | Default | Dev | UI | Rel | Read by | Meaning |
|---|---|---|---|---|---|---|---|
| PHYSICS_FRAME_DT_LIMIT | float | 0.04 | | (main) "Max Physics Delta-Time per Frame" | SIM / TIME | GameSettings.ApplySettings -> Time.maximumDeltaTime | Caps physics catch-up per rendered frame; when the CPU cannot keep up, game time runs slower than wall time ("yellow clock"). |
| SIMULATE_IN_BACKGROUND | bool | true | | (main) "Simulate In Background" | TIME | ApplySettings -> Application.runInBackground | Game keeps running when the window loses focus. |
| ORBIT_DRIFT_COMPENSATION | bool | true | | (main) "Orbital Drift Compensation" | SIM | VesselPrecalculate | Snaps a coasting unpowered vessel back to its Kepler orbit to cancel numerical drift. |
| PHYSICS_EASE | bool | true | | (main) "Ease in Gravity" | SIM | VesselPrecalculate | Gradual gravity/physics ease-in when a landed vessel goes off rails (loads). |
| MAX_VESSELS_BUDGET | int | 250 | 10000 | (main) "Max Persistent Debris" | PERS | FlightState (at save) | When the saved vessel list exceeds the budget, non-persistent non-commandable vessels (debris) are auto-cleaned oldest-first; -1 = no limit. |
| DECLUTTER_KSC | bool | true | False | (main) "Tidy up debris cluttering KSC" | PERS | FlightState (at save) | Landed non-commandable non-persistent vessels inside KSC or a stock launch site get `SetAutoClean` on save. |
| KERBIN_TIME | bool | true | | (main) "Display Kerbin Time (6h days, 426d years)" vs "Earth Time" | TIME (display only) | KSPUtil (day/year length for date formatting) | Calendar used to FORMAT UT; UT itself is unchanged. |
| SHOW_DEADLINES_AS_DATES | bool | false | | (main) "Contract Deadlines as Dates" | UI | MissionControl, KSPRichTextUtil | Contract deadline display. |
| AUTOSAVE_INTERVAL | float (s) | 300 | | UI unverified (mods such as BetterTimeWarp write it) | PERS | FlightAutoSave | Periodic autosave interval. |
| AUTOSAVE_SHORT_INTERVAL | float (s) | 30 | | UI unverified | PERS | FlightAutoSave | Retry interval when an autosave was blocked (unsafe state). |
| SAVE_BACKUPS | int | 5 | | cfg only | PERS | FlightAutoSave, GamePersistence | Number of rotating persistent.sfs backups in saves/<name>/Backup. |
| CAN_ALWAYS_QUICKSAVE | bool | false | | cfg only | PERS | QuickSaveLoad, MissionSystem | Bypass the "clear to save" check (moving/under acceleration/near ground). |
| QUICKSAVE_MINIMUM_ALTITUDE | float (m) | 500 | | cfg only | PERS | FlightGlobals (ClearToSave) | Minimum altitude for quicksave while not landed. |
| ORBIT_WARP_DOWN_AT_SOI | bool | true | | UI unverified | TIME | TimeWarp | Auto-drops time warp when approaching an SOI change. |
| ORBIT_WARP_MAXRATE_MODE | TimeWarp.MaxRailsRateMode {VesselAltitude, PeAltitude} | PeAltitude | | UI unverified | TIME | TimeWarp | Whether max on-rails warp is limited by current altitude or by periapsis altitude. |
| ORBIT_WARP_PEMODE_SURFACE_MARGIN | double (m) | 250 | | UI unverified | TIME | TimeWarp | Margin added to the Pe-mode altitude limit. |
| ORBIT_WARP_ALTMODE_LIMIT_MODIFIER | float | 1 | | UI unverified | TIME | CelestialBody | Scales per-body warp altitude limits. |
| SHOW_PWARP_WARNING | bool | true | False | cfg / dialog "don't show again" | UI | TimeWarp | Physics-warp warning dialog. |
| WARP_TO_MANNODE_MARGIN | float (s) | 60 | | UI unverified | TIME | NavBallBurnVector, AutoWarpToUT, ManeuverNodeEditorManager | Lead time when auto-warping to a maneuver node. |
| DEFAULT_KERBAL_RESPAWN_TIMER | double (s) | 7200 | | cfg only | CAREER | GameParameters (Difficulty.RespawnTimer initializer) | Default for new games' respawn timer. |
| PRELAUNCH_DEFAULT_THROTTLE | float | 0 | | (main) "Default Throttle in Prelaunch" | SIM | FlightInputHandler | Initial throttle on the pad. |
| SHOW_WRONG_VESSEL_TYPE_CONFIRMATION | bool | true | | (main) "Show Wrong Vessel Type on Launch Confirmation" | UI | PreFlightCheck | VAB craft on runway / SPH craft on pad warning. |
| SHOW_EXIT_TO_MENU_CONFIRMATION | bool | true | False | (main) "Show Exit to Main Menu Confirmation" | UI | PauseMenu | |
| LEGACY_ORBIT_TARGETING | bool | false | | Debug menu Physics screen toggle | SIM (targeting math) | Orbit, ScreenPhysics | Old closest-approach algorithm. |
| CONIC_PATCH_LIMIT | int | 3 | 4 | (main) "Conic Patch Limit"; (in-game) | VIS / TIME | PatchedConicSolver (patchLimit = max(v,1)), GameVariables | Number of predicted SOI patches (also capped by Tracking Station level). |
| CONIC_PATCH_DRAW_MODE | int -> PatchRendering.RelativityMode {LOCAL_TO_BODIES, LOCAL_AT_SOI_ENTRY_UT, LOCAL_AT_SOI_EXIT_UT, RELATIVE, DYNAMIC} | 3 (RELATIVE) | | (main) "Conic Patch Draw Mode"; (in-game) | VIS | PatchedConicRenderer | Frame in which future patches are drawn. |
| ALWAYS_SHOW_TARGET_APPROACH_MARKERS | bool | false | | (main) "Always Show Closest Approach for Target" | VIS | PatchedConics | |
| ORBIT_FADE_STRENGTH | float | 1 | | (main) "Orbit Line Fade Strength" | VIS | OrbitRendererBase | |
| ORBIT_FADE_DIRECTION_INV | bool | false | | (main) "Orbit Line Fade Reversed" | VIS | OrbitRendererBase | |
| MAP_MAX_ORBIT_BEFORE_FORCE2D | int | 150 | | cfg only | VIS | MapView | Orbit-count threshold for forcing 2D map rendering (name-derived; body not read). |
| RADAR_ALTIMETER_EXTENDED_CALCS | bool | true | | cfg only | SIM (altitude reporting) | Vessel | Extended radar-altitude computation (terrain + objects). |
| VESSEL_ANCHOR_VELOCITY_THRESHOLD | float | 0.05 | | cfg only | SIM | Vessel | Landed-vessel anchoring (anti-slide) thresholds; this and the next five. |
| VESSEL_ANCHOR_TIME_THRESHOLD | float | 1.5 | | cfg only | SIM | Vessel | |
| VESSEL_ANCHOR_ANGLE_CHANGE_THRESHOLD | float | 20 | | cfg only | SIM | Vessel | |
| VESSEL_ANCHOR_ANGLE_TIME_THRESHOLD | float | 0.1 | | cfg only | SIM | Vessel | |
| VESSEL_ANCHOR_BREAK_FORCE_FACTOR | float | 1.05 | | cfg only | SIM | Vessel | |
| VESSEL_ANCHOR_BREAK_TORQUE | float | 100 | | cfg only | SIM | Vessel | |
| WATERLEVEL_BASE_OFFSET | double | 1.5 | | cfg only | SIM | PartBuoyancy | Buoyancy water-level tuning. |
| WATERLEVEL_MAXLEVEL_MULT | double | 0.1 | | cfg only | SIM | PartBuoyancy | |
| COMET_REENTRY_FRAGMENT | bool | true | | UI unverified | SIM | ModuleComet | Comets fragment on atmospheric entry. |
| MIN_DISTANCE_FROM_OTHER_SPLASHES | float | 1.5 | | cfg only | VIS | FXMonger | Splash FX spacing. |
| MIN_TIME_BETWEEN_SPLASHES | float | 0.025 | | cfg only | VIS | FXMonger | |
| CELESTIAL_BODIES_CAST_SHADOWS | bool | true | | UI unverified | VIS | CelestialBody, PQS, PQSLandControl | |
| INPUT_KEYBOARD_SENSIVITITY | float | 2 | | cfg only | IN | FlightInputHandler | Keyboard control ramp rate. |
| IVA_RETAIN_CONTROL_POINT | bool | false | | (main) "Retain Control Point on Enter IVA" | SIM (control reference) | CameraManager | |
| DEBUG_MAX_SETPOSITION_ALTITUDE | double | 1e13 | | cfg only | DEV | FlightGlobals | Upper bound for the debug Set Position cheat. |

### 3.2 EVA, construction, wheels, parts

| Key | Type | Default | UI | Rel | Read by | Meaning |
|---|---|---|---|---|---|---|
| EVA_ROTATE_ON_MOVE | bool | true | (main) "EVAs Auto-Rotate to Camera" | IN | KerbalEVA, FlightInputHandler, SASDisplay | |
| EVA_SHOW_PORTRAIT | bool | true | UI unverified | UI | KerbalEVA, KerbalPortrait(Gallery) | Portrait for EVA kerbal. |
| EVA_DEFAULT_HELMET_ON | bool | true | UI unverified (GameplaySettingsScreen backup list) | SIM | KerbalEVA, HelmetSuitPickerWindow, ActionCreateKerbal | Kerbals exit with helmet on. |
| EVA_DEFAULT_NECKRING_ON | bool | true | same | SIM | same | |
| EVA_DIES_WHEN_UNSAFE_HELMET | bool | true | cfg only | SIM | KerbalEVA | Removing helmet in unsafe atmosphere kills the kerbal. |
| EVA_INHERIT_PART_TEMPERATURE | bool | false | cfg only | SIM | FlightEVA | EVA kerbal starts at the exited part's temperature. |
| EVA_SCREEN_MESSAGE_X / _Y | float | 0 / 200 | cfg only | UI | ScreenMessages | |
| EVA_LADDER_CHECK_END | bool | true | (in-game) "Kerbals stop at end of ladder" | SIM | KerbalEVA | |
| EVA_LADDER_JOINT_WHEN_IDLE | bool | true | cfg only | SIM | KerbalEVA | Joint kerbal to ladder when idle. |
| EVA_LADDER_JOINT_BREAK_VELOCITY | double | 100 | cfg only | SIM | KerbalEVA | |
| EVA_LADDER_JOINT_BREAK_ACCELERATION | double | 12 | cfg only | SIM | KerbalEVA | |
| EVA_MAX_SLOPE_ANGLE | float | 45 | cfg only | SIM | KerbalEVA | Walkable slope. |
| EVA_INVENTORY_RANGE | float | 5 | cfg only | SIM | ModuleInventoryPart, EVAConstructionModeController, UIPartActionWindow | |
| EVA_CONSTRUCTION_RANGE | float | 7 | cfg only | SIM | KerbalEVA, EVAConstructionModeEditor, ModuleCargoPart, WeldFX, CheatsCareer | |
| EVA_CONSTRUCTION_COMBINE_ENABLED | bool | true | cfg only | SIM | EVAConstructionMode*, Part | Multiple kerbals combine mass limits. |
| EVA_CONSTRUCTION_COMBINE_NONENGINEERS | bool | true | cfg only | SIM | EVAConstructionMode* | |
| EVA_CONSTRUCTION_COMBINE_RANGE | float | 7 | cfg only | SIM | EVAConstructionModeEditor | |
| PART_REPAIR_MASS_PER_KIT | float | 0.05 | cfg only | SIM | ModuleDeployablePart, ModuleWheelDamage | |
| PART_REPAIR_MAX_KIT_AMOUNT | int | 4 | cfg only | SIM | same | |
| WHEEL_WEIGHT_STRESS_MULTIPLIER | float | 1 | UI unverified ("Wheel Stress" label exists) | SIM | ModuleWheelDamage | |
| WHEEL_SLIP_STRESS_MULTIPLIER | float | 1 | UI unverified | SIM | ModuleWheelDamage | |
| WHEEL_SUBSTEPS_ACTIVE | int | 8 | cfg only | SIM | ModuleWheelBase | |
| WHEEL_SUBSTEPS_INACTIVE | int | 4 | cfg only | SIM | ModuleWheelBase | |
| WHEEL_AUTO_SPRINGDAMPER | bool | true | UI unverified | SIM | ModuleWheelSuspension | |
| WHEEL_AUTO_STEERINGADJUST | bool | true | UI unverified | SIM | ModuleWheelSteering | |
| LEGS_ADVANCED_SUSPENSIONDAMPER | bool | true | cfg only | SIM | (no reader found outside GameSettings) | |
| WHEEL_DAMAGE_IMPACTCOLLIDER_ENABLED | bool | true | cfg only | SIM | ModuleWheelDamage, Part | |
| WHEEL_DAMAGE_WHEELCOLLIDER_ENABLED | bool | true | cfg only | SIM | ModuleWheelBase, ModuleWheelDamage | |
| AUTOSTRUT_SYMMETRY | bool | true | cfg only | SIM | Part | Symmetry counterparts share autostrut mode. |
| ADVANCED_TWEAKABLES | bool | false | (main)+(in-game) "Advanced Tweakables" | SIM (exposes autostrut, crossfeed, etc.) | Part, BaseAction, UIPartAction*, BaseServo | |
| ADDITIONAL_ACTION_GROUPS | bool | false | UI unverified | SIM | ActionGroupList, AxisGroupsModule, ActionGroupsPanel, EditorActionGroups | Extra action groups / axis groups. |
| SCIENCE_EXPERIMENT_SHOW_TRANSFER_WARNING | bool | true | dialog "don't show again" | UI | ModuleScienceExperiment | |
| SHOW_VESSEL_NAMING_IN_FLIGHT | bool | true | cfg only | UI | Part | |
| VESSEL_NAMING_PRIORTY_LEVEL_MAX / _DEFAULT | int | 20 / 10 | cfg only | UI | VesselRenameDialog | |
| CONTROLPOINT_VISUALS_ENABLED | bool | false | cfg only | UI | ModuleCommand | |
| CONTROLPOINT_ARROWLENGTH | float | 2.5 | cfg only | UI | ModuleCommand | |
| CONTROLPOINT_COLOR_FORWARD / _UP / _RIGHT | string rgba | see cfg | cfg only | UI | (parsed in GameSettings) | |
| SERENITY_CONTROLLER_IGNORES_VESSEL | bool | false | cfg only | SIM | ModuleRoboticController (Breaking Ground KAL) | |
| SERENITY_ROCS_VISUAL_SPEED | float | 500 | cfg only | VIS | PQSMod_ROCScatterQuad | |

### 3.3 Delta-V, navball, staging, maneuver tool, alarms

| Key | Type | Default | UI | Read by | Meaning |
|---|---|---|---|---|---|
| DELTAV_CALCULATIONS_ENABLED | bool | true | UI unverified | DeltaVGlobals, VesselDeltaV | Stock dV simulation on/off. |
| DELTAV_APP_ENABLED | bool | true | UI unverified | DeltaVApp | |
| DELTAV_APP_TWOCOLUMN_MODE | bool | false | app toggle | DeltaVApp | |
| DELTAV_BURN_PERCENTAGE | float | 0.5 | cfg | NavBallBurnVector | |
| DELTAV_BURN_ESTIMATE_COLORS / DELTAV_BURN_TIME_COLORS | bool | true | cfg | NavBallBurnVector | |
| DELTAV_ACTIVE_STAGE_UPDATE_SECS / DELTAV_ALL_STAGES_UPDATE_SECS / DELTAV_VESSEL_EVENT_DELAY_SECS | float | 0.2 / 4 / 1 | cfg | VesselDeltaV | Recalc cadence. |
| DELTAV_ACTIVE_VESSEL_TIMESTEP / DELTAV_CALCULATIONS_TIMESTEP / DELTAV_CALCULATIONS_BIGTIMESTEP | float | 5 / 5 / 100 | cfg | VesselDeltaV, DeltaVStageInfo | Sim timesteps. |
| DELTAV_USE_TIMED_VESSELCALCS | bool | false | cfg | VesselDeltaV | |
| STAGE_GROUP_INFO_ITEMS | string | "ISP,THRUST,TWR,BURNTIME" | stage info UI | DeltaVAppValues, DeltaVGlobals | |
| STAGE_GROUP_INFO_WIDTH_EDITOR / _FLIGHT | int | 100 / 120 | cfg | StageGroup | |
| STAGE_GROUP_INFO_NAME_PERCENTAGE | float | 0.5 | cfg | StageGroupInfoItem | |
| EXTENDED_BURNTIME | bool | false | (in-game) "Extended Burn Indicator" | NavBallBurnVector | |
| AUTOHIDE_NAVBALL | bool | false | (main) "Autohide Navball in Map View" | NavBallToggle | |
| MANEUVER_TOOL_TRANSFER_DEGREES | float | 10 | cfg | TransferTypeSimple, AlarmTypeTransferWindow | |
| MANEUVER_TOOL_CALC_TIMEOUT | float | 10 | cfg | TransferTypeSimple | |
| MANEUVER_TOOL_CB_COLLISION_ADJUSTMENT | float | 5 | cfg | TransferTypeSimple | |
| SHOW_DELETE_ALARM_CONFIRMATION | bool | true | dialog | AlarmClockUIAlarmRow, AlarmClockUINextAlarm | |
| ALARM_ROW_DISPLAYED_FLIGHT | bool | false | UI state | FlightUIModeController | |
| KERBNET_ALIGNS_WITH_ORBIT / KERBNET_REFRESH_FAST_INTERVAL / KERBNET_REFRESH_SLOW_INTERVAL / KERBNET_BACKGROUND_FLUFF | bool/float/float/bool | true / 3.5 / 7 / true | KerbNet dialog | KerbNetDialog, KerbNetMode | |

### 3.4 Gameplay UI, camera, editor

| Key | Type | Default | Dev | UI | Read by |
|---|---|---|---|---|---|
| LANGUAGE | string | "en-us" | | launcher / main menu language | Localizer (loaded in GameSettings) |
| FLT_VESSEL_LABELS | bool | true | False | (main) "Show Vessel Labels" | VesselLabels |
| SHOW_SPACE_CENTER_CREW | bool | true | | (main) "Show Space Center Crew" | EditorDriver |
| TEMPERATURE_GAUGES_MODE | int 0-3 (bit0 gauges, bit1 thermal highlights; F10 cycles) | 3 | | (main)+(in-game) "Temperature Gauges" / "Thermal Highlights" | FlightOverlays, TemperatureGauge(System) |
| CAMERA_DOUBLECLICK_MOUSELOOK | bool | false | | (main)+(in-game) | CameraMouseLook |
| DOUBLECLICK_MOUSESPEED | float | 0.2 | | cfg | Mouse |
| CAMERA_FX_EXTERNAL / CAMERA_FX_INTERNAL | float | 1 / 1 | | (main)+(in-game) "Camera Wobble" | FlightCamera / InternalCamera |
| FLT_CAMERA_ORBIT_SENS / FLT_CAMERA_ZOOM_SENS / FLT_CAMERA_WOBBLE | float | 0.04 / 0.5 / 0.1 | | cfg | FlightCamera |
| FLT_CAMERA_CHASE_SHARPNESS | float | 1.5 | | cfg | FlightGlobals |
| FLT_CAMERA_CHASE_USEVELOCITYVECTOR | bool | true | | cfg | (no reader outside GameSettings) |
| VAB_CAMERA_ORBIT_SENS / VAB_CAMERA_ZOOM_SENS | float | 0.04 / 0.1 | | cfg | VAB/SPH/EVA/IVA/Internal/GAP cameras |
| VAB_USE_CLICK_PLACE / VAB_USE_ANGLE_SNAP / VAB_ANGLE_SNAP_INCLUDE_VERTICAL | bool | true / false / false | ANGLE_SNAP True | editor toggles | EditorLogic, gizmos, ModuleProceduralFairing, EVAConstructionModeEditor |
| VAB_FINE_OFFSET_THRESHOLD | float | 20 | | cfg | EditorLogic |
| VAB_CRAFTNAME_CHAR_LIMIT | int | 128 | | cfg | EditorLogic |
| EDITOR_UNDO_REDO_LIMIT | int | 32 | | cfg | EditorLogic |
| ADVANCED_MESSAGESAPP / CONFIRM_MESSAGE_DELETION | bool | true / true | | (in-game) | MessageSystem |
| NAVIGATION_GHOSTING | bool | false | | (in-game) "Ghosted Navigation Markers" | NavWaypoint |
| SHOW_VERSION_WATERMARK | bool | false | | (main) | ExperimentalVersionReadout |
| UI_SCALE | float | 1.0 | 1.2 | (main)+(in-game) "UI Scale" (restart for full effect) | 17 UI classes |
| UI_OPACITY | float | 0.5 | | cfg | RTCanvas, UITransparencyController |
| UI_MAINCANVAS_PIXEL_PERFECT / UI_ACTIONCANVAS_PIXEL_PERFECT / UI_TOOLTIPCANVAS_PIXEL_PERFECT | bool | false | | cfg / debug | UIMasterController |
| UIELEMENTSCALINGENABLED | bool | true | | cfg | FlightUIModeController, HelixGauge |
| UI_SCALE_TIME / _ALTIMETER / _MAPOPTIONS / _APPS / _STAGINGSTACK / _MODE / _NAVBALL / _CREW | float | 1 | | (in-game) per-element scale sliders | FlightUIModeController (+ApplicationLauncher, StageManager, ...) |
| UI_POS_NAVBALL | float | 0 | -0.74 | (in-game) "NavBall Pos" | FlightUIModeController |
| UI_POS_ALTIMETER_SLIDEDOWN_HOVER_HEIGHT | float | 15 | | cfg | AltimeterSliderButtons |
| UI_COLOR_INACTIVE_TEXT / UI_COLOR_ACTIVE_TEXT / UI_COLOR_INACTIVE_MINISETTNIGS_TEXT | string rgba | | | cfg | (parsed in GameSettings) |
| MAPNODE_BEHINDBODY_OPACITY | float | 0.5 | | cfg | MapNode |
| COMMNET_LOWCOLOR_BRIGHTNESSFACTOR | float | 0.5 | | (in-game) "CommNet Line Brightness Factor" | CommNetUI |
| PART_HIGHLIGHTER_BRIGHTNESSFACTOR | float | 1 | | (in-game) | EditorLogic, FlightUIModeController |
| INFLIGHT_HIGHLIGHT | bool | true | | (in-game) "Part Highlighter Enabled in Flight" | Part |
| HIGHLIGHT_FX | bool | true | | (main) "Highlight FX"; (in-game) | HighlightingSystem |
| PAW_COLLAPSED_GROUP_NAMES | string | "" | | UI state | (parsed in GameSettings) |
| PAW_NUMERIC_SLIDERS | bool | false | | cfg | UIPartAction* |
| PAW_PREFERRED_HEIGHT / PAW_SCREEN_OFFSET_X | float | 10000 / 200 | | cfg | UIPartActionWindow |
| CRAFT_STEAM_UNSUBSCRIBE_WARNING | bool | true | | dialog | CraftBrowserDialog, VesselSpawnDialog |
| COLOR_PART_* (17 keys), COLOR_RD_SEARCH_* (2), COLOR_LIGHT_PRESET_1..5, COLOR_FIREWORK_PRESET_* (5) | string rgba | see cfg | | cfg only | parsed in GameSettings (highlight / light / firework palettes) |
| FEMALE_EYE_OFFSET_X / _Y / _Z / _SCALE | float | 0 / 0.00908 / 0.0338 / 2.03 | | cfg | CameraManager (IVA eye position) |
| TUTORIALS_EDITOR_ENABLE / TUTORIALS_FLIGHT_ENABLE | bool | false | | cfg | (no reader outside GameSettings) |
| TUTORIALS_MISSION_SCREEN_TUTORIAL_COMPLETED / TUTORIALS_MISSION_BUILDER_ENTERED / TUTORIALS_ESA_MISSION_SCREEN_TUTORIAL_COMPLETED | bool | false | BUILDER_ENTERED True | one-shot flags | MainMenu, MissionPlayDialog, MissionsUtils, tutorials |
| SHOW_ANALYTICS_DIALOG / SHOW_WHATSNEW_DIALOG / SHOW_WHATSNEW_DIALOG_VersionsShown | bool/bool/string | true/true/"" | False/False/1.12.5 | (main) "Show Stats Tracking Dialog on Startup" | MainMenu, WhatsNewDialog |
| dontShowLauncher | bool | false | | (main) "Don't Show Game Launcher" | launcher |
| CALL_HOME_PROMPT / DONT_SEND_IP / SEND_PROGRESS_DATA | bool | false | | analytics | (no reader outside GameSettings) |
| CHECK_FOR_UPDATES | bool | true | | (main) | Versioning |

### 3.5 Making History / Breaking Ground UI keys

| Key | Default | Read by |
|---|---|---|
| MISSION_SHOW_CREATE_VESSEL_WARNING | true | MissionsApp |
| MISSION_SHOW_TEST_MISSION_WARNING / MISSION_SHOW_NO_BRIEFING_WARNING / MISSION_SNAP_TO_GRID | true / true / false | MissionEditorLogic |
| MISSION_STEAM_UNSUBSCRIBE_WARNING | true | MissionPlayDialog |
| MISSION_BUILDER_GAPHEIGHT | 400 | MEActionPane |
| MISSION_SHOW_STOCK_PACKS_IN_BRIEFING | false | MissionBriefingDialog, MissionEditorLogic |
| MISSION_LOG_NODE_ACTIVATIONS | true | MissionSystem |
| MISSION_SHOW_EXPANSION_INFO / SERENITY_SHOW_EXPANSION_INFO | true | MainMenu, About dialogs |
| MISSION_MINIMUM_CANVAS_ZOOM | 0.2 | MENodeCanvas |
| MISSION_GAP_CAMERA_VAB_CONTROLS | true | GAPVesselCamera |
| MISSION_DELETE_REMOVES_IN_PROGRESS_MISSIONS | true | MissionsBrowserDialog |
| MISSION_NAVIGATION_GHOSTING | true | NavWaypoint |
| MISSION_VALIDATOR_MODE (ValidatorMode) | Manual | MissionEditorValidator, MissionValidationDialog |
| MISSION_TEST_AUTOMATIC_CHECKPOINTS | true | MissionSystem, MissionsApp |

### 3.6 Graphics / scenery

| Key | Type | Default | Dev | UI | Read by / meaning |
|---|---|---|---|---|---|
| SCREEN_RESOLUTION_WIDTH / _HEIGHT | int | 1280 / 720 | 2560 / 1440 | (main) Screen Resolution | SettingsGraphicsResolution |
| FULLSCREEN | bool | false | True | (main) | ApplySettings |
| QUALITY_PRESET | int | 5 | | (main) "Render Quality" | ApplySettings (Unity quality level) |
| ANTI_ALIASING | int | 2 | 8 | (main)+(in-game) | ApplySettings, SettingsBoolTogglePPFX |
| TEXTURE_QUALITY | int | 1 | 0 | (main)+(in-game) (0 = full res) | ApplySettings |
| SYNC_VBL | int | 1 | 0 | (main) V-Sync | LoadingScreen, ApplySettings |
| LIGHT_QUALITY | int | 8 | 64 | (main)+(in-game) "Pixel Light Count" | QualitySettings.pixelLightCount |
| SHADOWS_QUALITY | int | 4 | | (main)+(in-game) "Shadow Cascades" | ApplySettings |
| FRAMERATE_LIMIT | int | 120 | | (main) "Frame Limit" | MainMenu, ApplySettings |
| SHADOWS_FLIGHT/KSC/TRACKING/EDITORS/MAIN/DEFAULT_PROJECTION | int | 0/0/0/1/1/0 | | cfg | DynamicShadowSettings |
| AMBIENTLIGHT_BOOSTFACTOR / _MAPONLY / _EDITONLY | float | 0 | | (in-game) "Ambient Light Boost" x3 | DynamicAmbientLight |
| PLANET_SCATTER | bool | false | True | (main) "Terrain Scatters" | PQS_KSPBinder |
| PLANET_SCATTER_FACTOR | float | 0.5 | 1 | (main) "Scatter Density" | PQS_KSPBinder |
| UNSUPPORTED_LEGACY_SHADER_TERRAIN | bool | false | | (main) "SM3 Terrain Shaders" (legacy) | PQS_KSPBinder |
| TERRAIN_SHADER_QUALITY | int | (set elsewhere; dev 3) | | (main) terrain shader quality | PQS, MainMenuTerrainSelector, ScaledSpaceFader, others |
| TERRAIN node (preset = Low/Default/High + per-body PLANET minDistance/min/maxSubdivision) | node | preset Default | High | (main) "Terrain Detail" | PQS subdivision. NOTE: changes terrain mesh resolution and therefore landed-vessel ground height slightly. |
| AERO_FX_QUALITY | int | 3 | | (main)+(in-game) | AerodynamicsFX, FXCamera |
| SURFACE_FX | bool | true | | (main) "Surface FX" | ModuleSurfaceFX |
| FALLBACK_UNDERWATER_MODE | int | 1 | | (main) "Underwater FX" | (no reader outside GameSettings) |
| REFLECTION_PROBE_REFRESH_MODE | int | 0 | 3 | (in-game) | FlightReflectionProbe |
| REFLECTION_PROBE_TEXTURE_RESOLUTION | int | 1 | 4 | (in-game) | FlightReflectionProbe |
| SCREENSHOT_SUPERSIZE | int | 0 | | cfg | ScreenShot |
| COMET_SHOW_GEYSERS / COMET_MAXIMUM_GEYSERS / COMET_SHOW_NEAR_DUST / COMET_MAXIMUM_NEAR_DUST_EMITTERS | bool/int/bool/int | true/50/true/50 | | UI unverified | ModuleComet, CometManager |

### 3.7 Audio

MASTER_VOLUME (0.5), SHIP_VOLUME (0.5), AMBIENCE_VOLUME (0.5), MUSIC_VOLUME (0.35), UI_VOLUME (0.5), VOICE_VOLUME (0.5) - (main)+(in-game) sliders; read by AudioFX, VolumeNormalizer, MusicLogic, FXGroup, KerbalEVA, ModuleCargoPart, AlarmClockScenarioAudio, etc. SOUND_NORMALIZER_ENABLED (true), SOUND_NORMALIZER_THRESHOLD (1), SOUND_NORMALIZER_RESPONSIVENESS (16), SOUND_NORMALIZER_SKIPSAMPLES (0) - (main) "Sound Normalizer"; read by VolumeNormalizer. Rel: none for simulation.

### 3.8 Input

Scalars: CURRENT_LAYOUT_SETTINGS (keyboard layout file, e.g. Qwerty.cfg), AxisSensitivityMin/Max (0.25/5), AXIS_INCREMENTAL_SPEED_MULTIPLIER_STORAGE/_DEFAULT/_VALUES, SPACENAV_FLIGHT_SENS_ROT/_LIN (5/1), SPACENAV_CAMERA_SENS_ROT/_LIN (30/20), SPACENAV_CAMERA_SHARPNESS_LIN/_ROT (8/10), TRACKIR_ENABLED (false) + TRACKIR node. All IN.

Binding nodes (primary/secondary/group/modeMask/modeMaskSec; axes have PRIMARY/SECONDARY with name/axis/inv/sensitivity/deadzone/scale). Complete list from the dev settings.cfg (139 bindings):

- Flight rotation / throttle: PITCH_DOWN, PITCH_UP, YAW_LEFT, YAW_RIGHT, ROLL_LEFT, ROLL_RIGHT, THROTTLE_UP, THROTTLE_DOWN, THROTTLE_CUTOFF, THROTTLE_FULL, PRECISION_CTRL.
- Translation: TRANSLATE_DOWN/UP/LEFT/RIGHT/FWD/BACK, Docking_toggleRotLin, UIMODE_STAGING, UIMODE_DOCKING.
- Systems: SAS_HOLD, SAS_TOGGLE, RCS_TOGGLE, LAUNCH_STAGES (staging), LANDING_GEAR, BRAKES, HEADLIGHT_TOGGLE, AbortActionGroup, CustomActionGroup1..10, AGROUP_SELECT_NEXT/PREV.
- Parsek-relevant game-flow keys: QUICKSAVE (F5), QUICKLOAD (F9), TIME_WARP_INCREASE (.), TIME_WARP_DECREASE (,), TIME_WARP_STOP (/), FOCUS_NEXT_VESSEL (]), FOCUS_PREV_VESSEL ([), MAP_VIEW_TOGGLE (M), PAUSE (Esc), MODIFIER_KEY (Alt; Alt+F12 opens the debug menu).
- Camera / view: CAMERA_MODE, CAMERA_NEXT, CAMERA_RESET, CAMERA_MOUSE_TOGGLE, CAMERA_ORBIT_UP/DOWN/LEFT/RIGHT, ZOOM_IN, ZOOM_OUT, SCROLL_VIEW_UP/DOWN, SCROLL_ICONS_UP/DOWN, NAVBALL_TOGGLE.
- UI toggles: TOGGLE_UI (F2), TOGGLE_STATUS_SCREEN (F3), TAKE_SCREENSHOT (F1), TOGGLE_LABELS (F4), TOGGLE_TEMP_GAUGES (F10), TOGGLE_TEMP_OVERLAY (F11), TOGGLE_FLIGHT_FORCES (F12), TOGGLE_SPACENAV_FLIGHT_CONTROL, TOGGLE_SPACENAV_ROLL_LOCK.
- Wheels: WHEEL_STEER_LEFT/RIGHT, WHEEL_THROTTLE_DOWN/UP.
- EVA: EVA_forward/back/left/right, EVA_yaw_left/right, EVA_Pack_forward/back/left/right/up/down, EVA_Jump, EVA_Run, EVA_ToggleMovementMode, EVA_TogglePack, EVA_Use, EVA_Board, EVA_Orient, EVA_Lights, EVA_Helmet, EVA_ChuteDeploy, EVA_CONSTRUCTION_MODE_TOGGLE.
- Editor: Editor_pitchUp/pitchDown/yawLeft/yawRight/rollLeft/rollRight/resetRotation, Editor_modePlace/modeOffset/modeRotate/modeRoot, Editor_coordSystem, Editor_toggleSymMethod, Editor_toggleSymMode, Editor_toggleAngleSnap, Editor_fineTweak, Editor_partSearch, Editor_zoomScrollModifier.
- Axes: AXIS_PITCH, AXIS_ROLL, AXIS_YAW, AXIS_THROTTLE, AXIS_THROTTLE_INC, AXIS_CAMERA_HDG, AXIS_CAMERA_PITCH, AXIS_TRANSLATE_X/Y/Z, AXIS_WHEEL_STEER, AXIS_WHEEL_THROTTLE, AXIS_CUSTOM (4 AXIS_KEY_BINDINGs), axis_EVA_translate_x/y/z, axis_EVA_pitch/yaw/roll, AXIS_MOUSEWHEEL.
- Other nodes: INPUT_DEVICES, TRACKIR, TERRAIN (graphics, see 3.6).

### 3.9 Logging / debug (DEV)

VERBOSE_DEBUG_LOG (false; Contracts, FlightLogger, Orbit, Part, Vessel), SHOW_CONSOLE_ON_ERROR, CONSOLE_BUFFER_SIZE (128; DebugScreenConsole), LOG_INSTANT_FLUSH, LOG_ERRORS_TO_SCREEN, LOG_EXCEPTIONS_TO_SCREEN (KSPLog), LOG_JOINT_BREAK_EVENT (PartJoint), LOG_MISSING_KEYS_TO_FILE, SHOW_TRANSLATION_KEYS_ON_SCREEN (Localizer), LOG_DELTAV_VERBOSE, LOG_FXMONGER_VERBOSE, FI_LOG_TEMP_ERROR, FI_LOG_OVERTEMP (FlightIntegrator), COLLECT_ROC_STATS (ROCManager), DEBUG_AERO_GUI, DEBUG_AERO_DATA_PAWS (PhysicsGlobals), SETTINGS_FILE_VERSION ("1.3.0"). Several are toggled from the Alt+F12 Debugging screen. "Verbose Logging" appears in the main System section.

---

## 4. Cheats and debug options (Alt+F12 debug menu)

Access: `KSP.UI.Screens.DebugToolbar.DebugScreenSpawner` opens on MODIFIER_KEY (Alt) + F12 in any scene. In MISSION mode it is refused unless `MissionParamsExtras.cheatsEnabled` (`DebugScreen`). Tabs are prefab-defined via `AddDebugScreens.ScreenWrapper` (some gated by installed expansion). Cheat state is STATIC (`CheatOptions` static fields): not saved in the save file, global to the process, persists across scene changes and save loads until the game is restarted or toggled off. A legacy IMGUI `DebugToolbar` (F12 handler) toggles the same statics.

### 4.1 CheatOptions (static bools, all default false)

| Field | Debug UI | Rel | Read by (besides the toggle) | Effect |
|---|---|---|---|---|
| InfinitePropellant | "Infinite Propellant" | SIM | ModuleEngines(FX), ModuleRCS, ResourceConverter, ModuleResourceDrain, ModuleResourceHandler, KerbalEVA, ModulePartFirework, ModuleRobotArmScanner | Resources not drawn. |
| InfiniteElectricity | "Infinite Electricity" | SIM | Part, ResourceConverter, ModuleRobotArmScanner | EC not drawn. |
| NoCrashDamage | "No Crash Damage" | SIM | Part, CollisionEnhancer, ModuleComet, ModuleDeployablePart, ModuleWheelDamage | Impacts do not destroy parts. |
| IgnoreMaxTemperature | "Ignore Max Temperature" | SIM | Part | No overheat explosions. |
| UnbreakableJoints | "Unbreakable Joints" | SIM | Part | Part joints do not break. |
| PauseOnVesselUnpack | "Pause on vessel unpack" | TIME | Vessel | Game pauses when a vessel unpacks (goes off rails). |
| BiomesVisible | "Biomes visible in map" | VIS | BiomesVisibleInMap | Biome overlay. |
| AllowPartClipping | "Allow Part Clipping in Editors" | SIM (craft build) | EditorLogic(Base), EVAConstructionModeEditor | |
| NonStrictAttachmentOrientation | "Non-Strict Part Attachment Orientation Checks" | SIM (craft build) | EditorLogic, EVAConstructionModeEditor | |
| IgnoreAgencyMindsetOnContracts | Gameplay > Difficulty screen | CAREER | Contracts.Agents.Agent | Contract agent mindset ignored. |
| IgnoreEVAConstructionMassLimit | Cheats | SIM | Part | |
| IgnoreKerbalInventoryLimits | Cheats | SIM | ModuleInventoryPart | |
| MiddleMouseClickSetPosition | Cheats (Set Position) | SIM | SetPosition | Middle click teleports the vessel. |

### 4.2 Action cheats (Cheats tab, `KSP.UI.Screens.DebugToolbar.Screens.Cheats.*`)

| Screen | What it does | Rel / Parsek notes |
|---|---|---|
| HackGravity | Toggle + slider 0.01-10: sets `CelestialBody.GeeASL = original * factor` for every body; reset restores. | SIM: changes gravity for all bodies live (orbits, ascent). Not persisted. |
| SetOrbit | Body + SMA/ecc/inc/LAN/argPe/mEp (and rendezvous mode) -> `FlightGlobals.fetch.SetShipOrbit` / `SetShipOrbitRendezvous`. | SIM: teleports active vessel onto an orbit (discontinuity in trajectory). |
| SetPosition | Body + lat/lon/alt, landed option -> `FlightGlobals.fetch.SetVesselPosition`, `ToggleVesselEaseIn`; altitude capped by DEBUG_MAX_SETPOSITION_ALTITUDE. | SIM: teleport. |
| CheatsCareer | Funds +/-1k/+/-100k via `Funding.Instance.AddFunds(x, TransactionReasons.Cheating)`; Science +/-10/+/-100 via `ResearchAndDevelopment.CheatAddScience`; Reputation +/-10/+/-100 via `Reputation.AddReputation(x, TransactionReasons.Cheating)`; Max Tech (`ResearchAndDevelopment.CheatTechnology`), Max Facilities (`ScenarioUpgradeableFacilities.CheatFacilities`), Max XP (`KerbalRoster.CheatExperience`, only when experience enabled), Fix Repairable Parts (`Part.CheatRepair`). | CAREER: currency deltas carry TransactionReasons.Cheating. |
| MaxProgression | Left click `ProgressTracking.CheatProgression()`, right click `CheatEarlyProgression()` (home-body early milestones). | CAREER: completes progress nodes (milestone rewards). |
| CheatsComets | Spawn comet (`SpawnComet`), toggle coma/dust/tails/geysers/collider. | PERS: creates a new vessel (comet). |
| CheatsObjectThrower | Throws spawned objects from the camera. | PERS/SIM: spawns physics objects. |
| Strings toggles (StringsShowKeysToggle / StringsMissingKeysToggle / StringsOverrideMELock) | Localization debugging. | DEV |

### 4.3 Other debug-menu screens that write game state

- Gameplay > Difficulty: writes AllowStockVessels, MissingCrewsRespawn, Flight.CanRestart, Flight.CanLeaveToEditor, Flight.CanQuickSave, Flight.CanQuickLoad, IgnoreAgencyMindsetOnContracts (bypasses DifficultyOptionsMenu; CanQuickSave has no other UI).
- Gameplay > Kerbals (KerbalScreen, ScreenKerbalCreate): create/edit kerbals in the roster.
- Contracts (ScreenAddContracts, ScreenContractTools, ...): add/complete/cancel contracts.
- Missions (ScreenAddMissions, ScreenMissionTools): Making History mission debug.
- Physics: Aero / Drag / Thermal / KerbalEVAMaterial / Physics screens edit PhysicsGlobals at runtime (save/load physics database file; autostrut visualize; LEGACY_ORBIT_TARGETING).
- Resources > Debugging: log-to-screen, pixel-perfect, translation-key logging toggles.
- Serenity: ROCs, Robotics debug.
- Debug: dV info, flight info, FPS graph, memory, input locks, version info.
- Console (DebugScreenConsole): stock console commands (`ConsoleCommands` class exists, reads AllowStockVessels).

---

## 5. Per-save toggles set elsewhere that behave like settings

| Where | Serialized | Fields | UI | Notes |
|---|---|---|---|---|
| Alarm Clock (stock since 1.12) | `SCENARIO name=AlarmClockScenario / SETTINGS` (`AlarmClockSettings`) | defaultRawTime (300 s), defaultManeuverMargin (C# `defaultMapNodeMargin`, 60 s), soundName ("Classic"), soundRepeats (3) | In-game Settings "Alarm Clock - per game": Default Manual Alarm, Maneuver/Map Margin, Sound Name, Sound Repeats | Also scenario-level `UIUpdatePeriod`, `warpChangeTimeSafteyMultiplier` (1.2), `warpChangeIndicatorDuration` (3) with no UI. Alarms themselves stop time warp at their UT (TIME). |
| New-game intro tutorials | `SCENARIO name=ScenarioNewGameIntro` | kscComplete, editorComplete, tsComplete | one-shot dialog dismissals | UI only. |
| Asteroid / comet spawning | `SCENARIO name=DiscoverableObjects` (`ScenarioDiscoverableObjects`) | spawnInterval (15), maxUntrackedLifetime (20), minUntrackedLifetime (1), spawnOddsAgainst (2), spawnGroupMinLimit (3), spawnGroupMaxLimit (10), sizeCurve, lastSeed, min/maxAsteroidClass | none (config / save edit) | Spawns PERS vessels (unowned asteroids/comets) over time. |
| Sentinel telescope | `SCENARIO name=SentinelScenario` | NextSpawnTime | none | Sentinel-driven asteroid discovery timing. |
| Breaking Ground deployed science | `SCENARIO name=DeployedScience` | ScienceTimeDelay (60), DataSendFailedTimeDelay (600), DIMINISHINGRETURNS table | none | Deployed experiments transmit on a timer (background science accrues over UT). |
| Breaking Ground surface features | `GAME/ROCSeed`, `GAME/RemovedROCs`, `SCENARIO name=ROCScenario` | seed + removed-ROC list | none | Surface feature placement; removed ROCs persist. |
| Comet names | `GAME/CometNames` | | none | Naming registry. |
| Default launch sites | `GAME/defaultVABLaunchSite`, `defaultSPHLaunchSite` | | editor launch-site picker (Making History) | |
| Flag | `GAME/flag` | | KSC flag pole | |
| Strategies (Admin building) | `SCENARIO name=StrategySystem` | active strategies | Administration building | Career currency conversion multipliers (behave like difficulty modifiers while active). CAREER. |
| Kerbal inventory | `SCENARIO name=KerbalInventoryScenario` | per-kerbal loadouts | Astronaut Complex / editor crew | Persisted only if Difficulty.persistKerbalInventories. |
| Contract / progress / funding / R&D / reputation / facilities / destructibles / vessel recovery / part upgrades / achievements / custom waypoints / CommNet / resources | `SCENARIO name=ContractSystem, ScenarioContractEvents, ProgressTracking, Funding, ResearchAndDevelopment, Reputation, ScenarioUpgradeableFacilities, ScenarioDestructibles, VesselRecovery, PartUpgradeManager, ScenarioAchievements, ScenarioCustomWaypoints, CommNetScenario, ResourceScenario` | state, not settings | | Listed for completeness (stock KSPScenario declarations found by grep). |
| Making History mission settings | mission file `GameParameters` + Mission Builder node params | MissionParamsGeneral/Extras/Facilities (section 2.7) plus per-mission briefing / scoring set in the Mission Builder | Mission Builder | MISSION mode only. |

Not found as a player setting in 1.12.5: a comet/asteroid spawn-rate slider, a KSPedia toggle (KSPedia has no persisted setting in GameSettings), or any "physics warp max" setting (physics warp rates are fixed arrays on TimeWarp).

---

## 6. Things that could not be fully verified

- Main-menu Settings screen control membership: the screen is prefab-driven (`SettingsScreen` + reflected `SettingsControl*` components naming GameSettings fields as strings). Labels in the #autoLOC_19007xx-19009xx block confirm the Gameplay / System / Scenery / Video / Rendering / Volume / Input sections listed above, but for keys marked "UI unverified" (warp mode/margins, wheel stress, delta-V toggles, comet FX, EVA helmet defaults, autosave intervals, ADDITIONAL_ACTION_GROUPS, CELESTIAL_BODIES_CAST_SHADOWS) I could not prove from code whether a main-menu control exists. "cfg only" means no reader in the settings UI classes and no matching label was found; still a best-effort classification.
- `TEMPERATURE_GAUGES_MODE` bit meaning (bit0 gauges, bit1 thermal highlight) is inferred from FlightOverlays cycling 0..3 and the two in-game labels; bit assignment not read line by line.
- `MAP_MAX_ORBIT_BEFORE_FORCE2D` meaning is name-derived.
- `HackGravity` writes `CelestialBody.GeeASL`; whether that setter also recomputes `gravParameter` (and so affects on-rails orbits) was not traced.
- `Vessel.isPersistent` exact predicate for the MAX_VESSELS_BUDGET prune was only partially read (loaded path iterates `parts[i].isPersistent`); the prune skips persistent and commandable vessels, so in practice it removes debris.
- Call-site lists come from a textual grep over decompiled code; obfuscated bodies can hide indirect reads (e.g. reflection by string in the settings prefabs).
- AUTOSAVE_INTERVAL: the dev save carries a BetterTimeWarp custom node that writes stock autosave intervals, so on the dev instance these values may be mod-managed.
