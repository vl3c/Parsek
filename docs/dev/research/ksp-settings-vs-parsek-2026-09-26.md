# Stock KSP player settings vs Parsek (2026-09-26)

Scope: every player-facing setting of stock KSP 1.12.5 (+ Making History, Breaking Ground),
crossed with what Parsek reads or assumes. Evidence: full ilspycmd decompile of the dev
instance Assembly-CSharp, `settings.cfg` of the dev and automation instances, Parsek at
HEAD 958314016. Companion files in this directory (line numbers are at HEAD 958314016):
`ksp-settings-inventory-2026-09-26.md` (the complete
stock list: 103 GameParameters fields, 306 settings.cfg keys, 13 cheats + 8 cheat screens),
`ksp-settings-parsek-usage-2026-09-26.md` (every Parsek read, with file:line), and
`ksp-settings-debris-budget-trace-2026-09-26.md` (the section 9 trace).

Verdicts: OK = every value handled (usually because Parsek records OUTCOMES, not causes);
BUG = a value produces wrong behaviour, fix is not a design question; RULING = behaviour
depends on a product decision; CHECK = plausible issue, not yet traced to a failure;
N/A = no player UI or no interaction.

Where it matters: most difficulty options are editable mid-career (pause menu > Settings >
Difficulty Options; any edit flips the preset to Custom). NOT editable after new game:
ResourceAbundance, Starting Funds/Science/Rep, BypassEntryPurchaseAfterResearch (career),
ActionGroupsAlways, EnableKerbalExperience. GameSettings (settings.cfg) are GLOBAL across
saves. Cheats are process-static, never saved.

---

## 1. Game modes

| Mode | Parsek | Verdict |
|---|---|---|
| CAREER | primary target | OK |
| SCIENCE_SANDBOX | consistent singleton null guards (179 sites), science-only pool tracking, Timeline career view limited | OK; costs a fixed 120-frame (2 s) singleton wait per load (DeferredSeed + ApplyBudgetDeductionWhenReady wait for ALL three singletons) |
| SANDBOX | patchers no-op, UI hides career surfaces | OK; same 2 s wait in ApplyBudgetDeductionWhenReady |
| MISSION / MISSION_BUILDER (Making History) | treated as sandbox in UI only; ParsekScenario is AddToAllGames; nothing references MissionSystem | RULING (Q3): recording/rewind inside a scripted mission is untested and can fight MissionSystem (mission-forced facility locks, preventVesselRecovery, mission end dialogs) |
| SCENARIO / SCENARIO_NON_RESUMABLE (training, stock scenarios) | not distinguished | RULING (Q3), same question |

## 2. Difficulty presets and GameParameters

### 2.1 Career economy (CareerParams)

| Setting | Parsek interaction | Verdict |
|---|---|---|
| ScienceGainMultiplier (Easy 2, Mod 0.9, Hard 0.6) | Stock adds the PRE-multiplier value to `subject.science`, then `scienceValue *= ScienceGainMultiplier` before AddScience (`ResearchAndDevelopment.cs:1813`). Parsek captures `subject.science` (`GameStateRecorder.cs:933`) and credits it as ScienceAwarded (`GameStateEventConverter.cs:418`). Ledger earns x1, stock earns xM. On non-Normal careers the drawdown clamp holds the live value with a toast, and a rewind recalc resets science to the x1 total. | BUG. Fix: stamp the multiplier (or the post-multiplier awarded value) on the ScienceEarning at capture time; keep subject-cap math pre-multiplier. Must NOT read the current multiplier at replay (mid-career edits must not rescale history). |
| RepLossDeclined (Easy 0, Normal 1, Mod 2, Hard 3) | Stock `Contract.Decline` takes rep. ContractDeclined events are dropped (`GameStateEventConverter.cs:331`); `ReputationPenaltySource.ContractDecline` exists but is never constructed. Loss is held only by the rep DOWN clamp, refunded on rewind. Affects Normal too. | BUG. Fix: emit a KSC-origin ReputationPenalty(ContractDecline) row with the amount stock actually applied. |
| FundsGain/RepGain/FundsLoss/RepLoss multipliers | Stock bakes them into contract rewards at generation, hire cost, death rep, facility repair; Parsek records the resulting amounts and reads FundsLossMultiplier at repair time. | OK (mid-career edits correctly affect only the future) |
| StartingFunds / Science / Reputation | Ledger seeds from the live pool, not the parameter. | OK, except StartingFunds = 0 (slider allows it): `EnsureInitialFundsSeed` only seeds non-zero, PatchFunds then skips as "unseeded", and the 600-frame value wait spins ~10 s every load (also Science mode with 0 science). BUG (small): treat a zero pool as a valid seed once singletons are present. |
| TechTreeUrl | tech ids are strings; custom trees (HETTN in c2) work | OK |

### 2.2 Difficulty (DifficultyParams)

| Setting | Parsek interaction | Verdict |
|---|---|---|
| MissingCrewsRespawn / RespawnTimer | Parsek derives death from the recording terminal state and makes it a permanent reservation; it does not follow stock's Dead -> Missing -> Available respawn. So with respawn ON (Easy/Normal default) a kerbal stock brings back after 2 h stays unusable in Parsek until a rewind tombstones the death. Separately `CrewReservationManager.cs:84` / `VesselSpawner` rescue Missing -> Available on the premise "Missing is transient"; with respawn OFF stock kills directly (Missing only arises from other paths), so low risk. | RULING (Q2) |
| BypassEntryPurchaseAfterResearch | read at purchase time and modelled both ways | OK (new-game-only in career, so the "current flag reinterprets history" caveat is save-edit only) |
| IndestructibleFacilities / BuildingImpactDamageMult | destruction read from stock ScenarioDestructibles; recorded destruction rows replay as recorded | OK |
| ResourceAbundance | not read; routes replay recorded amounts | OK (new-game-only) |
| ReentryHeatScale | thermal only; ghost reentry FX derived from recorded speed/density; overheat destruction recorded as events | OK |
| EnableCommNet | only read in `GhostCommNetRelay` (dead code, todo GUI-D3); ghosts CommNet-inert in every setting | OK in effect; ruling already pending under GUI-D3. If the relay is ever wired: null-guard `HighLogic.CurrentGame` and re-evaluate on difficulty change |
| AllowOtherLaunchSites (MH) | launch-site capture exists; alt-site replay/spawn with the setting off untested (registry D17 unflown) | CHECK (low) |
| AutoHireCrews | stock hires before flight; Parsek reservations hire replacements | CHECK (low): confirm the auto-hire path emits the same hire event Parsek's ledger captures |
| persistKerbalInventories | not examined against inventory-carrying recordings / logistics pickups | CHECK (low) |
| AllowStockVessels | craft browser only | N/A |

### 2.3 Advanced (AdvancedParams) and CommNet (CommNetParams)

| Setting | Verdict |
|---|---|
| AllowNegativeCurrency (Mod/Hard true) | CHECK: displayed pool is Total - Reserved; with negatives allowed stock lets the player spend money reserved for the committed future. Parsek's stock click-blocks read CommittedFutureIndex, not stock affordability, so likely covered for tech/facility/hire; plain part purchases and other spends not traced. Route dispatch refuses `funds < cost` regardless (stricter than stock, acceptable). |
| EnableKerbalExperience / ImmediateLevelUp | OK: Parsek appends careerLog and delegates to stock `UpdateExperience`, which applies both flags |
| PressurePartLimits, GPartLimits, GKerbalLimits, KerbalGToleranceMult, ResourceTransferObeyCrossfeed, ActionGroupsAlways, PartUpgradesIn*, EnableFullSAS* | OK: live physics / editor only, recordings capture outcomes |
| requireSignalForControl, plasmaBlackout, range/DSN/occlusion modifiers, enableGroundStations | OK: affect live vessels only; ghosts are CommNet-inert (see EnableCommNet) |

### 2.4 Flight / Editor / TrackingStation / SpaceCenter params

| Setting | Parsek interaction | Verdict |
|---|---|---|
| Flight.CanQuickLoad = false (Hard) | Rewind-to-Separation loads RP quicksaves regardless, re-granting quickload on a no-quickload career | RULING (Q1) |
| Flight.CanRestart = false (Hard; also flips CanLeaveToEditor) | Stock PauseMenu builds the Revert button only when CanRestart (`PauseMenu.cs:1334`), so `ReFlyRevertButtonGate` forcing `CanRevertToPostInit` does nothing: the re-fly Retry / Discard path through `RevertInterceptor` is unreachable from Esc (inferred). Scene exit still reaches the merge dialog (`SceneExitInterceptor` models this branch). | RULING (Q1), and whichever way it goes the re-fly exit path on Hard needs a live check |
| Flight.CanTimeWarpHigh/Low | Parsek time jumps / warp-to set UT directly and ignore them | N/A in practice (no player UI; scenarios/missions only) - folds into Q3 |
| Flight.CanSwitchVesselsFar | mirrored by `MapFocusObjectOnSelectPatch` | OK |
| CanQuickSave, CanAutoSave, CanEVA, CanBoard, CanUseMap, CanSwitchVesselsNear, CanLeaveTo*, Editor.*, TrackingStation.*, SpaceCenter.* | no player UI (debug menu / scenarios / missions) | N/A (folds into Q3) |

## 3. GameSettings (settings.cfg, global)

| Setting | Parsek interaction | Verdict |
|---|---|---|
| KERBIN_TIME (Earth calendar) | `ParsekTimeFormat` and ~40 `KSPUtil.PrintDate*` sites honour it. `LogisticsWindowUI.FormatDuration` (`:3928`) divides by 21600 for "d" but switches to days at 86400, so a 24 h cadence reads "4.0d" even on the Kerbin calendar; `RouteCadence.cs:80` parses "d" as 21600 s on either calendar. | BUG: route both through `ParsekTimeFormat` |
| MAX_VESSELS_BUDGET (default 250) / DECLUTTER_KSC (default true) | Stock prunes non-persistent non-commandable vessels (debris) at save when the vessel count exceeds the budget, and auto-cleans landed debris at KSC / stock launch sites. The prune runs while stock builds the FlightState, BEFORE `ParsekScenario.OnSave` strips ghost map ProtoVessels (`GhostMapPresence.StripFromSave`), so live ghost map vessels count toward the budget and can push real debris out of the save. Background-recorded debris that stock prunes vanishes on the next load. BOTH the dev instance and the automation instance run 10000 / False, so no test or flight has ever seen player defaults. | CHECK + RULING (Q4) |
| Parsek "debris persistence" override (`ParsekFlight.EnforceMinDebrisPersistence`) | reflects for a GameSettings int named "*debris*"; none exists in 1.12.5 (live log: "No debris persistence field found") | dead code; delete or retarget at MAX_VESSELS_BUDGET depending on Q4 |
| UI_SCALE (dev instance 1.2) | Parsek IMGUI windows ignore it; stock UI scales, Parsek windows do not | RULING (Q5) |
| LANGUAGE | Stock-craft `#autoLOC` vessel names are resolved at capture time, so recording names freeze in the capture-time language; Parsek UI is English-only | OK / accepted |
| PHYSICS_FRAME_DT_LIMIT, SIMULATE_IN_BACKGROUND | UT-driven sampling and playback; lag slows game time, not data | OK |
| ORBIT_DRIFT_COMPENSATION, PHYSICS_EASE | live physics; spawned landed vessels rely on stock ease-in | OK (PHYSICS_EASE off: CHECK very low) |
| AUTOSAVE_INTERVAL, SAVE_BACKUPS, CAN_ALWAYS_QUICKSAVE, QUICKSAVE_MINIMUM_ALTITUDE | autosave mid-recording runs Parsek OnSave like any save; RP quicksaves are Parsek-written | OK |
| ORBIT_WARP_DOWN_AT_SOI, ORBIT_WARP_* , WARP_TO_MANNODE_MARGIN | stock warp limits may interrupt a Parsek warp-to; UT jumps unaffected | OK / cosmetic |
| SHIP_VOLUME, ORBIT_FADE_* | honoured by ghost audio / ghost orbit lines | OK |
| Terrain detail / scatter (graphics) | PQS height at a coordinate can differ by metres between detail levels, so a landed recording made at one level replays against a different terrain height; already a known design concern (vessel-interaction-paradox doc) handled by recorded terrain clearance | OK (known) |
| All other graphics, audio, input, EVA/wheel/construction tuning keys | live-only or visual | N/A |

## 4. Cheats (Alt+F12)

| Cheat | Parsek interaction | Verdict |
|---|---|---|
| Career cheats (+/- funds, science, rep; reason `Cheating`), Max Tech, Max Facilities, Max XP, Max Progression | Cheat currency arrives as FundsChanged/Reputation/Science events that are never converted to ledger actions: held by the UP clamp ("Kept your earned funds") until a rewind recalc wipes it. Max Tech / Facilities / Progression go through the ordinary stock events Parsek does capture (not traced individually). | RULING (Q6) |
| Infinite Propellant / Infinite Electricity | recordings show no consumption; route cost manifests derived from snapshots may under-state cost (not traced) | CHECK (low) |
| No Crash Damage, Unbreakable Joints, Ignore Max Temperature | recordings lack destruction events; replay is faithful | OK |
| Set Orbit / Set Position / middle-click teleport | trajectory discontinuity inside a live recording; no guard | CHECK (low): at least log it; a teleport mid-recording could be split into a new section |
| Hack Gravity | changes `GeeASL` of every body live; recorded orbit segments captured under hacked gravity are replayed with real mu (whether gravParameter is recomputed not traced) | accepted (cheat), unless you want it logged |
| Pause on vessel unpack, part clipping, non-strict attachment, EVA/inventory limits | N/A |

## 5. Per-save setting-like state

Alarm Clock settings, DiscoverableObjects spawn parameters, Sentinel, Breaking Ground
deployed-science timers, ROC seed, flag, default launch sites, strategies: no Parsek
interaction beyond what is already modelled (strategies are ledgered; alarm clock and
deployed science are not settings Parsek consumes). N/A.

## 6. Test estate blind spots

All 67 committed harness fixtures and both xUnit career fixtures carry
ScienceGainMultiplier = 1, RepLossDeclined = 1, EnableCommNet = True, CanQuickLoad = True,
KERBIN_TIME = True; both KSP instances run MAX_VESSELS_BUDGET = 10000 and DECLUTTER_KSC =
False. Registry D14 has no difficulty / multiplier / respawn / calendar / debris-budget /
cheat cells. Every BUG above is structurally invisible to the current suites; each fix
should land with a unit fixture at a non-Normal value.

## 7. Work list

BUG (no ruling needed):
1. ScienceGainMultiplier not applied to ledger science.
2. RepLossDeclined never ledgered.
3. Logistics duration/cadence hard-code a 21600 s day.
4. StartingFunds = 0 (or 0 science in Science mode) leaves the pool unseeded + 10 s load wait.
5. 2 s singleton waits on every Science/Sandbox load (wait only for singletons the mode has).
6. Dead `EnforceMinDebrisPersistence` reflection (delete, or retarget per Q4).

CHECK (trace before filing): debris budget vs ghost map vessels and BG-recorded debris;
AllowNegativeCurrency vs reserved funds on non-blocked spends; AutoHireCrews hire capture;
re-fly exit on CanRestart = false; teleport cheats mid-recording; infinite-fuel route costs.

RULING: Q1 Hard-mode quickload/revert vs rewind and re-fly; Q2 recorded death vs stock
respawn; Q3 Making History missions and training scenarios; Q4 debris budget defaults in
the harness; Q5 UI_SCALE; Q6 cheat currency.

## 8. Owner rulings (Vlad, 2026-09-26)

- Q1 Hard preset: Rewind-to-Separation and Re-Fly IGNORE Flight.CanQuickLoad / CanRestart
  (Parsek's own time mechanic). Only fix the re-fly exit path so Retry / Discard stays
  reachable when stock hides the Revert button (CanRestart = false).
- Q2 Crew respawn: follow stock. With MissingCrewsRespawn on, a recorded death frees the
  kerbal at death UT + RespawnTimer; permanent only when respawn is off.
- Q3 Mission modes: Parsek goes inert (no recording, ghosts or rewind) in MISSION,
  MISSION_BUILDER, SCENARIO and SCENARIO_NON_RESUMABLE, with one log line.
- Q4 Debris budget: trace ghost-vessel budget counting and BG-debris pruning first, then
  propose which lanes fly player defaults (250 / DECLUTTER_KSC true).
- Q5 UI_SCALE and Q6 cheat currency: defaults stand unless overridden (scale Parsek windows
  as separate work; leave cheat currency clamp-held, log it).

## 9. Debris budget trace result (detail: ksp-settings-debris-budget-trace-2026-09-26.md)

- Stock only prunes / declutters vessels typed Debris (`isCommandable => vesselType > Debris`,
  Vessel.cs:949). `Game.Updated` builds the pruned FlightState (Game.cs:1467) before
  `ScenarioRunner.GetUpdatedProtoModules` (Game.cs:1515) runs Parsek's OnSave.
- DEFECT (inferred, not flown): ghost map vessels are live in FLIGHT and TRACKSTATION, saved
  as `prst=True`, non-Debris, so they are never pruned themselves but each one counts toward
  MAX_VESSELS_BUDGET and pushes one real debris vessel out of the save. Fix shape: exclude
  ghosts from the count (e.g. raise the budget by the ghost count around the FlightState ctor).
- BG-recorded debris removed by the prune: mid-session handled (`CheckDebrisTTL` logs);
  after reload a missing pid gets a silent on-rails state and stays open until scene-exit
  inference. Add a load-time check + one summary log line.
- Low: autoclean recovery matches pending-tree recordings by vessel NAME only
  (ParsekScenario.cs:7989), can mark same-named pending debris recordings Recovered.
- Spawns: debris recordings never spawn; a spawn is exposed only if its snapshot type is Debris.
- `EnforceMinDebrisPersistence`: delete (retargeting at the budget would not help; the budget
  applies only at save).
- Harness: no per-scenario settings override; keys come from `[settings]` in
  `harness/provision/profiles/stock-minimal.toml:33` and `modded-compat.toml:50`.
- Q4 follow-up ruling (2026-09-26): flip stock-minimal.toml and modded-compat.toml [settings] to player defaults MAX_VESSELS_BUDGET=250, DECLUTTER_KSC=True; re-fly the daily tier once; the ghost-count fix ships with unit + in-game tests, no dedicated low-budget lane.
