# DECLUTTER_KSC / MAX_VESSELS_BUDGET vs Parsek - trace

Paths: STOCK = an `ilspycmd` decompile of KSP 1.12.5 Assembly-CSharp (file names = type names); SRC = worktree Source/Parsek/.
Anything not read directly is marked (inferred).

## 1. Stock predicates

- `Vessel.isCommandable` = `vesselType > VesselType.Debris` (STOCK Vessel.cs:949).
  Debris is enum value 0 (VesselType.cs:6), so EVERY type except Debris counts as
  "commandable": SpaceObject, Unknown, Flag, EVA, DroppedPart, DeployedGroundPart all
  survive both mechanisms. Commandability has nothing to do with ModuleCommand; only
  the vesselType field matters.
- `Vessel.isPersistent` (Vessel.cs:951-1008): if `loaded`, it scans parts and returns
  true when any `parts[i].isPersistent` is true, and WRITES the backing field
  (`persistent = true/false`, :981/:994). Unloaded: it returns the backing field
  `persistent` (:1002). `Part.isPersistent` is a plain public field (Part.cs:276), and
  no stock code sets it (a grep for assignments found only KSPAction/BaseEvent, which are
  different members). So a loaded stock vessel is always non-persistent (inferred: only
  mods set Part.isPersistent). The backing field round-trips through the save as `prst`
  (ProtoVessel.cs:238 capture, :2257 save, :1677 parse, :2676
  `vesselRef.isPersistent = persistent` on load).
- `LandedInKSC` (Vessel.cs:836): takes `landedAt` (from protoVessel.landedAt when
  unloaded) and checks whether it contains "KSC", "LaunchPad" or "Runway".
- `LandedInStockLaunchSite` (Vessel.cs:892): `landedAt` contains the name of any
  `PSystemSetup.Instance.StockLaunchSites[i]`.
- `SetAutoClean(reason)` (Vessel.cs:12077-12098) only sets `autoClean = true` plus the
  reason, and is a no-op if the flag is already set. It deletes nothing at that point.
  The flag is captured into the ProtoVessel (ProtoVessel.cs:183-184), saved as `cln` /
  `clnRsn` (:2218-2245), and re-applied on load (`vesselRef.SetAutoClean`, :2616-2627).
  The deletion happens in `Vessel.Update()` (Vessel.cs:7865, the check at :7899-7931):
  when `autoClean && !loaded && HighLogic.CurrentGame.CurrenciesAvailable` it calls
  `Clean(reason)` -> `ProtoVessel.Clean` (ProtoVessel.cs:3902). On the home world,
  `ProtoVessel.Clean` fires `GameEvents.onVesselRecovered(this, true)`, so the vessel is
  RECOVERED for funds, science and rep (:3966-3984). It then removes the vessel from
  `flightState.protoVessels` (:4021) and runs `DestroyImmediate(vesselRef.gameObject)`
  (:4033). If the vessel is loaded and not active, Clean unloads it first (:3932-3956);
  the active vessel is never cleaned.
  So the deletion happens on the first Update frame where the vessel is UNLOADED in a
  career or science game. That can be the same flight scene, after the vessel drops
  out of load range, or the next scene (KSC / TS), where every vessel is unloaded. In
  sandbox (no currencies) an autoclean-flagged vessel is never deleted (inferred:
  sandbox has CurrenciesAvailable false). `DestroyImmediate` routes through
  `Vessel.OnDestroy`, which fires `onVesselDestroy` (Vessel.cs:9146). It does NOT fire
  `onVesselWillDestroy`, which only `Die()` fires (:8850).

### FlightState() ctor order (FlightState.cs:33-250)
1. The candidate list is `FlightGlobals.Vessels`, walked from the last index down
   (:44-47), so unloaded and packed vessels are included. Only null vessels are skipped.
2. DEAD vessels are skipped with the warning "not saved because it was dead" (:63-77).
3. Declutter (:79-149), in the same loop: `DECLUTTER_KSC && !isCommandable &&
   !isPersistent && situation == LANDED` -> `SetAutoClean` if LandedInKSC and/or
   LandedInStockLaunchSite. The vessel is still ADDED to the list and saved, with
   `cln=True`.
4. Budget prune (:162-230), after the loop: `if MAX_VESSELS_BUDGET != -1`, walk the
   list from its END (`count2--`) while `list.Count > MAX_VESSELS_BUDGET`, and remove
   entries that are `!isPersistent && !isCommandable`, logging
   "[Flight Persistence]: Too many vessels in scene - skipping save for <name>". The
   list was built reversed, so its end is `FlightGlobals.Vessels[0]`: the OLDEST
   vessels in FlightGlobals order are pruned first.
   The count includes EVERY vessel (commandable, persistent, ghosts); only Debris
   vessels are removable. A pruned vessel stays alive in the current scene and is only
   left out of the written FlightState. It disappears on the next load of that state:
   a scene switch, F9, a StartAndFocusVessel reload, or a revert to a later snapshot.
5. `protoVessels.Add(list[i].BackupVessel())` in reverse, which restores the original
   order (:230-232).
- Settings range: defaults 250 / true (GameSettings.cs:996-997). The UI dropdown offers
  -1, 0, 25, 50, 100, 150, 250 ... 10000 (GameplaySettingsScreen.cs:44-47), so a
  player CAN pick 0 or 25.

## 2. Save order: prune happens BEFORE ScenarioModule.OnSave

`Game.Updated(scene)` (Game.cs:1450) runs `flightState = new FlightState()` when
`HighLogic.LoadedSceneHasPlanetarium` (:1452-1467). That is true for FLIGHT,
TRACKSTATION and SPACECENTER (HighLogic.cs:925-949). Only later does it call
`scenarios = ScenarioRunner.GetUpdatedProtoModules()` (:1515), which is where
`ParsekScenario.OnSave` runs (inferred: GetUpdatedProtoModules calls OnSave, the
standard ScenarioRunner path). `Game.Save` then writes SCENARIO (:1663) before
FLIGHTSTATE (:1690). So declutter and prune have already run when Parsek's OnSave
executes. `GhostMapPresence.StripFromSave` (SRC GhostMapPresence.cs:8982, called at
SRC ParsekScenario.cs:1316 on `HighLogic.CurrentGame.flightState`) removes ghosts from
the ALREADY PRUNED list. (inferred: the Updated() instance is HighLogic.CurrentGame on
the in-scene SaveGame path, which is why Parsek's strip sees the new FlightState.)

Ghost map vessels at that moment:
- Scenes: they are created only from ParsekFlight / ParsekTrackingStation /
  ParsekPlaybackPolicy / WatchModeController (grep of `GhostMapPresence.Create|Build|
  Ensure` callers), so they exist in FLIGHT and TRACKSTATION. None exist at
  SPACECENTER (inferred from that caller set; KSC ghosts are not vessels). They are
  live `Vessel`s in `FlightGlobals.Vessels` and are not DEAD, so they are in the list.
- Construction (SRC GhostMapPresence.cs:11140-11168): one `sensorBarometer` part, with
  vesselType = the recording snapshot's `type` (`ResolveVesselType`, :11273, default
  Ship). The node forces `prst=True` and `cln=False`, and the type is re-forced after
  load (:11066-11072). Debris recordings get NO map ghost (`traj.IsDebris` skip,
  :5188-5191 and :9504; also :12339).
- Commandable: yes, unless a non-debris recording's snapshot says `type = Debris`
  (inferred rare; for example a player-retyped vessel).
- Persistent: yes while unloaded (the backing field comes from `prst=True`). But a ghost
  CAN load (GhostVesselLoadedInertPatch, SRC Patches/GhostVesselLoadPatch.cs:446-467),
  and a loaded read of `isPersistent` recomputes from the barometer part and sets the
  field to false permanently. This only matters for a Debris-typed ghost.
- Conclusion: (b). Ghosts are essentially never pruned or decluttered themselves: they
  are commandable, and even in the odd Debris case pruning a ghost is harmless because
  it would be stripped anyway. But ghosts COUNT toward `list.Count`, and the strip runs
  only after the prune. In FLIGHT/TS, each ghost map vessel pushes one real Debris
  vessel over the budget. At budget 250 with, say, 150 ghost map vessels (loop
  instances each get their own ProtoVessel, :715) plus 60 real craft, only 40 real
  debris survive the save. The oldest are dropped first, so these are the debris that
  were already there before the ghosts were built.
- Theoretical, not reproduced: a Debris-typed ghost that has been loaded, sits LANDED
  at KSC and is unloaded in career would be autocleaned by stock and recovered for
  funds. Parsek's `OnVesselRecovered` ignores ghost pids
  (SRC ParsekScenario.cs:7855), but stock would still credit the recovery (inferred;
  requires a landed ghost map proto, which normally does not exist for landed
  terminals).

## 3. BG-recorded debris and Parsek spawns

- In-session disappearance is handled. `BackgroundRecorder.CheckDebrisTTL`
  (SRC BackgroundRecorder.cs:1430-1455) sees `FindVesselByPid == null` and ends the
  recording with `BackgroundRecordingEndReason.VesselDestroyedOrDespawned`, logging
  `Debris TTL: vessel destroyed/despawned, ending recording: vesselPid=...`. It then
  finalizes through the `background_debris_end_missing` cache refresh (:1620-1627) and
  `OnVesselRemovedFromBackground`. Only TTL-tracked debris (`debrisTTLExpiry`) get
  this check. Autoclean mid-flight goes through `Clean()` -> `DestroyImmediate`, so
  ParsekFlight.OnVesselWillDestroy (SRC ParsekFlight.cs:3241) does NOT fire (stock
  never fires WillDestroy on this path) and Parsek subscribes to no `onVesselDestroy`.
  The only event Parsek sees is `onVesselRecovered`, whose terminal update touches
  PENDING-tree recordings by NAME only (SRC ParsekScenario.cs:7989-8025). Committed
  recordings are never changed.
- Across a reload (prune followed by a scene switch, F9, or a far Switch-To reload): the
  tree restores from ParsekScenario with BackgroundMap entries for the vanished pids.
  The `BackgroundRecorder(tree)` ctor (SRC BackgroundRecorder.cs:208-233) creates an
  on-rails state for every non-Destroyed map entry, with no vessel lookup and no log.
  After that, nothing names the missing vessel until finalization. Scene-exit
  finalization then takes the `vesselMissing` path (SRC ParsekFlight.cs:16945-16958,
  `PopulateTerminalPositionFromLastPoint`), which infers the terminal from the last
  recorded point (inferred: the exact terminal chosen for a restored missing BG leaf
  was not traced end to end). Whether `debrisTTLExpiry` is restored on load was not
  verified (inferred: not restored, so the TTL "vanished" check would not fire for
  restored debris). Net: no dedicated log line says the vessel was pruned by stock.
  The recording stays open as an on-rails entry until commit (an orphan-ish state,
  inferred).
- Parsek spawns: `IsDebris` recordings never spawn ("debris recording (visual-only)",
  SRC GhostPlaybackLogic.cs:8263-8266; also IsFinalSpawnSegment :8373). Pad and
  runway-end flights are RETIRED with no vessel (memory, PR #1783). Parsek does not
  touch `prst`/`cln`/`type` on spawned vessels: a grep of VesselSpawner.cs and
  VesselSnapshotOps.cs for "prst", "cln", "type" and isPersistent found nothing; the
  only vesselType use is the collision-skip filter at VesselSpawner.cs:365/382. So a
  spawned vessel keeps its snapshot type. It is exposed to both mechanisms only if the
  snapshot type is `Debris`, which a controlled child never has, since it keeps its
  command-part type (inferred), or a vessel the player retyped to Debris (inferred
  rare). Depots without command parts are exposed only if typed Debris. A
  Ship-typed depot is safe. Nothing marks spawns persistent, and nothing needs to for
  non-Debris types.

## 4. Existing references

- Grep over SRC for `MAX_VESSELS`, `DECLUTTER`, `AutoClean`, `"prst"`, `"cln"` and
  `isPersistent`: the only hits are the ghost node's `prst=True` / `cln=False`
  (GhostMapPresence.cs:11165-11166). Nothing reads either setting.
- `ParsekFlight.EnforceMinDebrisPersistence` (SRC ParsekFlight.cs:13777, called at
  :13711 on recording start, restored at :2196/:3215/:13875) reflects for a static int
  GameSettings field whose name contains "debris" (:13852-13860). No such field exists
  (a grep of STOCK GameSettings.cs for static int *debris* returns nothing), so the
  code is dead and every call logs "No debris persistence field found" once.
  Retargeting it at MAX_VESSELS_BUDGET does NOT make sense:
  (a) the budget is a total-vessel cap applied at SAVE, not a debris-lifetime cap, so
      raising it to 10 changes nothing for split detection, which happens in-session
      where pruned vessels are still alive;
  (b) its floor of 10 is below the default 250, so it would only fire for players who
      picked 0 or 25, and restoring on stop would re-expose the recorded debris at the
      next save;
  (c) writing a player's GameSettings from a mod persists into settings.cfg if the
      player opens settings meanwhile (inferred).
  Delete it, together with `MinDebrisForRecording`, `debrisOverrideActive`,
  `savedMaxPersistentDebris`, the testing overrides and their tests.

## 5. Recommendation

Defects (by expected impact):
1. GHOSTS INFLATE THE BUDGET (real, needs player defaults; verified mechanism, not yet
   flown). Repro: career, default 250. Commit enough non-debris recordings that the
   FLIGHT or TS scene holds N ghost map vessels, with N + real vessels > 250 and at
   least some real Debris vessels saved earlier (for example a few stages in orbit from
   older flights). Save by switching to KSC or by F5. KSP.log then shows
   "[Flight Persistence]: Too many vessels in scene - skipping save for <X Debris>", the
   Parsek log shows "Stripped N ghost ProtoVessel(s) from save", and the reloaded save
   holds 250 - N - real non-debris vessels of debris. Without Parsek those debris would
   have been saved. Fix shape: before stock builds FlightState, exclude ghosts from the
   count. Options: a Harmony prefix/postfix on `FlightState()` that temporarily
   removes ghost pids from `FlightGlobals.Vessels`, or a prefix that raises
   MAX_VESSELS_BUDGET by `ghostMapVesselPids.Count` for the ctor's duration and
   restores it in a finally block. Log the adjustment.
2. PRUNED/AUTOCLEANED BG DEBRIS LEAVE A SILENT OPEN ENTRY (inferred severity). A
   restored tree whose BackgroundMap pid has no live vessel gets an on-rails state
   with no log. Add a load-time check with one summary line that names missing BG pids
   and closes them at their last recorded UT. The autoclean path fires only
   onVesselRecovered with no WillDestroy, so the live BG recorder also misses it for
   non-TTL entries.
3. AUTOCLEAN RECOVERY BY NAME (low). An autoclean of "<Craft> Debris" at KSC or in TS
   with a pending tree present runs `UpdateRecordingsForTerminalEvent` by name, which
   can stamp same-named pending debris recordings as Recovered and null their
   snapshots. Outside FLIGHT it can also write a FundsEarning(Recovery) ledger row for
   a non-Parsek vessel. That row is correct accounting for a real stock recovery, but
   the ledger oracle must expect it.
4. Delete `EnforceMinDebrisPersistence` (dead code, section 4).

Harness: no per-scenario settings override exists (no settings key in hlib.py or
run.py). `[settings]` in `harness/provision/profiles/stock-minimal.toml` (line 33 on)
applies deltas over a copy of the dev settings.cfg, which is where 10000 / False come
from; `modded-compat.toml` line 50 mirrors it. Options:
- Minimal: add `MAX_VESSELS_BUDGET = "250"` and `DECLUTTER_KSC = "True"` to both
  profiles' `[settings]`. This flips EVERY lane to player defaults, so re-fly the daily
  tier afterwards. Any lane that stacks debris past 250 total (loop/ghost-dense
  fixtures) or lands debris at KSC in career would change behavior.
- Targeted: to reproduce defect 1 fast, use a small budget such as 25 (a valid
  dropdown value). This needs either a new profile (for example
  `stock-minimal-playerdefaults.toml`) or a new per-spec settings override in
  hlib/run.py. Candidate hosts: ghost-dense committed fixtures such as the census or
  loop fixtures (LF-2, LT-1/LT-2), plus any lane that leaves orbital debris from
  earlier flights. B1-pad-hop in a career save is the natural DECLUTTER_KSC host: an
  SRB or stage lands at KSC, F5, fly away beyond the load range, and expect stock
  "cluttering up KSC" recovery with no Parsek terminal corruption.
