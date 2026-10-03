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
| StartingFunds / Science / Reputation | Ledger seeds from the live pool, not the parameter. | OK. StartingFunds = 0 FIXED 2026-09-26 and live-proven by ZF-1 2026-10-03 (see the closing section); originally: StartingFunds = 0 (slider allows it): `EnsureInitialFundsSeed` only seeds non-zero, PatchFunds then skips as "unseeded", and the 600-frame value wait spins ~10 s every load (also Science mode with 0 science). BUG (small): treat a zero pool as a valid seed once singletons are present. |
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
| AllowOtherLaunchSites (MH) | launch-site capture exists; alt-site replay/spawn with the setting off untested (registry D17 unflown) | CHECK (low). 2026-09-27: every stock site now retires an ending like KSC, and the stale-site tag is fixed (todo KSP-SETTINGS-FOLLOWUPS-RECORDING-2026-09-27) |
| AutoHireCrews | stock hires before flight; Parsek reservations hire replacements | CHECK (low): confirm the auto-hire path emits the same hire event Parsek's ledger captures |
| persistKerbalInventories | traced 2026-09-27: a spawned-at-end vessel gave each kerbal his current roster inventory (duplicated or lost cargo; the flag widens the duplication). Fixed by capturing crew inventories with the snapshot and restoring them at spawn (todo KERBAL-INVENTORY-NOT-RESTORED-AT-SPAWN); logistics pickups walk part modules only, unaffected | FIXED |
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
| Set Orbit / Set Position / middle-click teleport | trajectory discontinuity inside a live recording; no guard | CHECK (low): at least log it; a teleport mid-recording could be split into a new section. 2026-09-27: FIXED, logged + section seam / body split (todo KSP-SETTINGS-FOLLOWUPS-RECORDING-2026-09-27) |
| Hack Gravity | changes `GeeASL` of every body live; recorded orbit segments captured under hacked gravity are replayed with real mu (whether gravParameter is recomputed not traced) | accepted (cheat), unless you want it logged. 2026-09-27: one Warn per recording (owner ruling) |
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

### S5 status (2026-09-29, branch `settings-axis`): re-derived, then flown

Operator request 2026-09-29 (the game-settings test axis) superseded the "no dedicated lane"
half of the Q4 ruling. Re-derivation from a fresh decompile (KSP 1.12.5 Assembly-CSharp):

- The ONLY readers of `GameSettings.MAX_VESSELS_BUDGET` / `DECLUTTER_KSC` are the parameterless
  `FlightState()` constructor, `GameSettings.SetDefaultValues` and the gameplay settings screen
  (full IL scan). That constructor has three callers: `Game.Updated` (every save with a
  planetarium: FLIGHT, TRACKSTATION, SPACECENTER), `FlightDriver.Start` and the `Game`
  constructor. So the budget is a SAVE-TIME filter on what is written, never a live cull: a
  pruned vessel stays in the scene and is gone on the next load of that save.
- What it removes: walking the reversed vessel list from its oldest end, vessels with
  `!isPersistent && !isCommandable` until `list.Count <= MAX_VESSELS_BUDGET`
  (`isCommandable` = `vesselType > Debris`, IL verified). EVERY live non-dead vessel counts;
  only Debris-typed, non-persistent ones can be removed. Ships, probes, relays, stations, EVA
  kerbals, flags, asteroids are never removed. Declutter only FLAGS (`cln`) Debris landed at
  KSC / a stock launch site, and stock deletes a flagged vessel (as a recovery) only when it
  is unloaded in a game with currencies.
- Ghost map vessels (GhostMapPresence.cs: `BuildGhostProtoVesselNode` sets the recording's own
  type, `prst = True`, `cln = False`; debris recordings get no map ghost) exist in FLIGHT and
  TRACKSTATION only, are live in `FlightGlobals.Vessels` when the constructor runs, and are
  stripped later (`ParsekScenario.OnSave` -> `StripFromSave`, ParsekScenario.cs:1328).
- Verdict: (c). Before `Patches/FlightStateGhostBudgetPatch.cs` ghosts counted in FLIGHT and
  TRACKSTATION saves (never at the Space Center, which has none) and each one pushed one real
  Debris vessel, oldest first, out of the written save once the scene held more vessels than
  the budget; no commandable real vessel and no ghost was ever removed. With the patch every
  REGISTERED ghost is added back to the budget for that one build, so they no longer count.
  The residual gap: a ghost vessel that is live but not registered in `ghostMapVesselPids`
  still counts; the one known producer is the in-game test runner in the Tracking Station
  (todo INGAME-BATCH-TS-ORPHANS-GHOST-MAP-VESSELS), no player path.
- Live: `VB-1-ghost-vessel-budget` reading `2026-09-29_1657` (budget 8, eight ghosts, eight
  real vessels plus one asteroid stock spawned at the load) logged `excluded 8 ghost map
  vessel(s) ... MAX_VESSELS_BUDGET 8 -> 16` and stock dropped exactly ONE debris, the real
  excess (nine real against eight), not nine. The same run's in-game batch reproduced the
  pre-fix world by accident: with the eight ghosts orphaned by the runner's cleanup, the next
  two saves dropped all six debris. The armed lane flies at budget 10 (slack for the asteroid
  spawner) and must drop nothing. It did (2026-09-30, branch `vb1-final`): reading
  `2026-09-30_1837` and armed `_1902`, both PASS attempt 1, `excluded 8 ... 10 -> 18` on both
  save builds, no stock prune, all six debris kept; D14 `vessel-budget` claimed.
- Harness: a spec now declares a per-run delta, `[runtime] kspSettings`, so the line above
  ("no per-scenario settings override") is no longer true.

### Closing the three remaining settings risks (2026-10-03, branch `settings-risks`)

Operator request 2026-10-03: non-default reward multipliers where money or science change
hands, quickload off during a Re-Fly, and a career that starts at zero funds.

- Quickload off during a Re-Fly: CLOSED BY EVIDENCE, no flight. Readers of
  `Flight.CanQuickLoad` in a full decompile: `QuickSaveLoad` (F9 and the hold-F9 load dialog),
  `PauseMenu` (Esc load) and `KSCPauseMenu` (Space Center load button) only; of
  `Flight.CanQuickSave`: the same three. `GamePersistence.SaveGame` / `LoadGame` and
  `FlightDriver.StartAndFocusVessel` read neither, and they are the only stock calls behind
  RP authoring, the Re-Fly load, merge durable saves and the commit quicksave refresh. RF-16
  (`2026-09-27_1341`) re-flew with the flag off in the persistent save, the RP quicksave and the
  loaded game. Ruling Q1 stands as built.
- Zero starting funds (S4): FIXED in d5e3ec399 (2026-09-26, confirmed-zero seed keyed on
  Funding's OnLoad signal) and now LIVE-PROVEN: `ZF-1-zero-funds-career` on
  `career-pad-craft-zero-funds`, reading `2026-10-03_1521`, armed `_1542`. The seed is
  `amount=0 confirmedZero=True`, the deferred seed waits 0 frames, a 4182 recovery and a 300
  rollout reconcile with no guard clamp, an unaffordable hire is refused, LedgerGroundTruth
  `hardFailures=0`. Section 2.1's StartingFunds row and work-list item 4 are therefore done.
- Reward multipliers with money changing hands: LIVE-PROVEN by
  `HC-2-hard-career-earn-spend` on `career-science-pad-hard` (reading `2026-10-03_1523`, armed
  `_1533`). Milestones are scaled by stock inside `AwardProgress`'s arguments (the
  `GetContract*CompletionFactor` multipliers), so Parsek records the paid amount; the science
  multiplier is stamped at capture (S1). Every amount was exactly 0.6x L3's x1 flight, the
  recovery unscaled, the funds total equal to the pre-flight prediction (523758), no guard
  clamp, LedgerGroundTruth `hardFailures=0`, then a clean Rewind-to-Launch. Not exercised: the
  x2 loss multipliers and contract rewards.
