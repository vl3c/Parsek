# Science lab test program (2026-10-17)

Owner ask (V, 2026-10-17): flight lanes for the Mobile Processing Lab (`ModuleScienceLab` +
`ModuleScienceConverter`), LAB-2 .. LAB-14, on real craft, in the shape of the drill program
(`mining-isru-test-program-2026-10-17.md`). LAB-1 keeps its number. This is the
implementation plan: what each lane flies, what it asserts, and what has to exist first.
Planning only: nothing here was flown, provisioned or changed in C#.

Labels used below: VERIFIED (read in Parsek source, the decompiled KSP 1.12.5 assembly, a part
cfg or the fixture), BELIEF (reasoned, to be measured on the lane's first flight).
The decompile used here was saved under `<umbrella>/logs/decompile/2026-10-17_lab/`.

## Where things stand

- Live: LAB-1 (`LAB-1-converter-jump-lab-ledger`, in-game category `ConverterJumpLab`,
  `ConverterJumpLabInGameTests.cs`): 5 SPACECENTER cells on code-built single-part vessels.
  They cover the converter clock shift on an unloaded lab for both jump kinds, the stock clock
  field on the lab prefab, a hand-built lab RECOVERY credit (12.5) reaching the ledger, and
  both Harmony payout patches attached. LABX-1 (`LABX-1-lab-orbit-ascent`) is the vehicle: it
  flies `LABX Orbital Lab` to a ~107 x 92 km orbit. The harvest becomes `lab-lko-sandbox`, and
  `lab-lko-science` is built from it (SCIENCE_SANDBOX, 29 tech nodes, science 100, UT 344.6,
  Valentina in `mk1pod.v2`, Bob (Scientist, level 0) in `Large.Crewed.Lab`, CommNet on,
  ground stations on).
- Unit: `ScienceLabLedgerTests` (17 cells: transmission and recovery capture, event 24 ->
  ledger 38, tagging to a live recording, no emission during replay, uncapped, strategy take
  once, rewind past a transmission, supersede retirement), `ConverterJumpPolicyTests`,
  `TestCommandPartActionTests` (labprocess / labtransmit / lab reading),
  `DiscardEconomyPreservationTests` (rehome).
- Never exercised: a real lab in flight, processing over time, a real CommNet transmission,
  and a lab across rewind, spawn, Re-Fly, F9, revert or merge (todo GC-8, roadmap XS-5).

### Verified blockers (review 2026-10-17; Step 0 fixes them in other worktrees)

1. The fixture vessel cannot transmit. Its only `ModuleDataTransmitter` is the Mk1 pod's
   INTERNAL antenna, and stock `CanTransmit()` is false for INTERNAL. `sendDataToComms` then
   only posts "no comms", while the seam's `labtransmit` still answers OK
   (`TestCommandPartAction.cs:213-216`; the applier invokes the KSPEvent and reads state, it
   does not check the outcome). Fix: a DIRECT DTS-M1 (`mediumDishAntenna`: DIRECT,
   2e9 power, packetSize 2, packetInterval 0.35, 12 EC per packet; cfg verified) with donors
   in `eve-park-kerbalx` / `bdock-station-pad`, a LABX-1 re-fly and a re-harvest.
   `build_lab_orbit_craft.py` assumption A9 is wrong.
2. The labprocess seam can store 0 data (details under "Product surfaces", seam).
3. `RevertToLaunch` refuses on a vessel loaded in orbit. Decompiled `FlightDriver`:
   loading a save into flight sets `CanRevertToPostInit = vessel.situation == PRELAUNCH`.
   Only a launch (`StartWithNewLaunch`) sets it true, so a revert lane needs the lab loaded on
   the pad.

## Stock behaviour the lanes depend on (VERIFIED, decompiled 1.12.5 unless noted)

- labValue. Only `ExperimentResultDialogPage.UpdatePageLabValue` writes it, and only
  `ExperimentsResultDialog.SetPage` calls that (when `labSearch.HasAnyLabs`). Base: the page
  constructor's `refValue = GetReferenceDataValue * ScienceGainMultiplier`. Then x(1+SurfaceBonus)
  if landed, x(1+ContextBonus) if the subject id holds the current body name, and
  x homeworldMultiplier only if landed or splashed at home. The result is `Math.Round`ed
  (banker's rounding). It reads `FlightGlobals.ActiveVessel`'s lab. `ScienceData` neither
  saves nor loads `labValue` (Save/Load write `data, scienceValueRatio, subjectID, xmit,
  xmitBonus, title, triggered, container, extraResultString`).
  - LKO expectation, lab cfg values SurfaceBonus 0.1, ContextBonus 0.25, homeworld 0.1; Kerbin
    InSpaceLow multiplier 1: Goo 10 x 1.25 = 12.5 -> 12, Science Jr 25 x 1.25 = 31.25 -> 31,
    2HOT 8 x 1.25 = 10, PresMat 12 x 1.25 = 15. That is about 68 data. All four ScienceDefs have
    situationMask 63, and requireAtmosphere is False. The barometer's in-space subject is
    BELIEF until the first run reads it.
  - On the pad, the homeworld multiplier 0.1 rounds Goo, 2HOT and PresMat to 0. Only Science Jr
    gives 1, so lab work on the pad must come from a seeded fixture, never from pad experiments.
- `ModuleScienceLab.IsStorable` refuses a subject already in `ExperimentData` and data that
  would push `dataStored` past `dataStorage` (750). `StoreData` adds the subject id and then
  `dataStored += labValue`, so a zero labValue burns the subject's one slot.
  `ExperimentData` is the slot list.
- Research (`ModuleScienceConverter.PostProcess`, lab cfg: researchTime 7, scienceMultiplier
  5, dataProcessingMultiplier 0.5, scienceCap 500, powerRequirement 5 EC/s, ALL_VESSEL).
  - With zero ScienceSkill crew in the lab part, it calls `StopResourceConverter` (OFF, not
    paused).
  - Data used per second = `0.5 * scientists * dataStored / 1e7`, and science = 5x that. Each
    scientist counts `1 + 0.25 * level`.
  - If a step would push `storedScience` past `scienceCap`, it returns with NOTHING added: a
    stall, no data loss.
  - With 68 data and Bob level 0: 1.7e-5 science/s, 0.367 per Kerbin day, 1.0 science after
    about 58,800 s (2.7 days).
- Catch-up (`BaseConverter.GetDeltaTime`). Slices are `min(now - lastUpdateTime, MaxDeltaTime
  21600)`. For the first 10 frames a slice is cut to the EC available (`GetBestDeltaTime`).
  After that the full slice runs and the converter's TimeFactor scales the work, so a long
  unloaded absence produces only what the stored EC (about 1650) pays for. Unloaded vessels run
  nothing and catch up on load. Exact amounts are BELIEF: lanes pin `> 0` and a window, never a
  formula.
- Transmit (`ModuleScienceLab.sendDataToComms`).
  - Refuses (screen message only) while transmitting, below `storedScience < 1`, and with no
    transmitter.
  - Otherwise it sends `ScienceData(floor(storedScience), "sciencelab@<body>", triggered,
    container=flightID)`.
  - `OnTransmissionComplete` filters on container and prefix, returns on abort, and then calls
    `AddScience(storedScience, ScienceTransmission)` and `storedScience -= dataAmount`.
    QUIRK: it credits the whole stored amount but takes away only its floor, so the fraction
    is paid again on the next transmit.
  - The decrement lands at completion, not at the press.
  - `transmittingData` is not persistent, so a scene change mid-transmission drops it without
    sticking.
  - Triggered data opens no `RnDCommsStream`, so `OnScienceRecieved` never fires.
- `RecoverScienceLabs` calls `AddScience(sum of storedScience, VesselRecovery)`.
- Patents Licensing (`Strategies.cfg`, verified): its second `CurrencyConverter` EFFECT has
  `AffectReasons = ScienceTransmission, VesselRecovery, Progression` (science to funds). Its
  setup costs science (135 .. 2700, lerped at factor 0.05). Whether a bare lab `AddScience`
  raises the query the converter mutates is BELIEF; the capture patch comment says it does.

## Product surfaces (file:line)

Ledger path:
- `Patches/ScienceLabCreditCapturePatch.cs:30-58` puts a prefix and postfix on
  `ModuleScienceLab.OnTransmissionComplete`. The prefix reads the pool and `storedScience` as
  the gross. Lines `:61-90` do the same on `RecoverScienceLabs` (summed snapshot gross,
  `:106-122`).
  - `IsCredit` (`:159-166`) needs a positive pool move.
  - `Report` (`:172-184`) reports the GROSS and logs a net-of-take Verbose line when the
    pool moved by less.
- `GameStateRecorder.Handlers.cs:1515-1527` builds event 24. `:1539-1566`
  `OnScienceLabCredited` is suppressed during replay and logs
  `Game state: ScienceLabCredited +X.XX reason='..' vessel='..' ut=.. recordingId='..'`. An
  ownerless credit goes to `LedgerOrchestrator.OnKscSpending`.
- `GameStateRecorder.cs:817` logs stock's own pool move:
  `Game state: ScienceChanged +X.X (<reason>) -> <pool>`. The arrow in KSP.log is U+2192, so
  a contract matches it with `[^0-9]+`. This line is the "KSP's own R&D gain" witness.
- `GameActions/GameStateEventConverter.cs:295-296, 1171-1195`: event 24 becomes
  `ScienceLabEarning`. The pool delta is the credit; `VesselRecovery` sets method Recovered,
  anything else Transmitted.
- `GameActions/ScienceModule.cs:136, 450-470` `ProcessLabEarning`: uncapped, feeds
  `runningScience` and `totalEffectiveEarnings`.
- `GameActions/TombstoneEligibility.cs:112-116`: `ScienceLabEarning` is supersede-eligible.
  `SupersedeCommit.cs:2179-2185` (no strict block), `:2850` (counted as Science) and
  `:2744-2750` summary `Tombstoned N career actions (... Science=N ...)`.

Recorder and optimizer:
- `FlightRecorder.cs:2219-2232` `IsAnyConverterActiveOnPart` keys on `BaseConverter`, so the
  lab's `ModuleScienceConverter` counts. `:2246-2280` `CheckConverterTransition` and
  `:2288-2307` `CheckConverterState` log `Part event: ConverterActivated|ConverterDeactivated
  'Large.Crewed.Lab' pid=N (converter)`.
- `FlightRecorder.cs:9382-9385` `OnPhysicsFrame` returns while `isOnRails`, and
  `:11580-11626` sets `isOnRails` for an ORBITING vessel (only surface / in-atmosphere rails
  entries skip it). So a research toggle made during ORBITAL rails warp is recorded at the
  first physics frame after the warp. An on-and-off pair inside one warp records nothing. This
  differs from MINE-6's landed answer (toggle UT); see owner question Q4.
- `FlightRecorder.cs:8025-8028` classifies the lab as `HarvestConverterKind.NoResourceOutput`
  (no harvest-route bound).
- `RecordingOptimizer.TailTrim.cs:163-172` `FindLastResourceChangeUT`: a converter running at
  the end keeps the tail (`CountConvertersRunningAtEnd`, `:190`). `:838-852` logs
  `TrimBoringTail: skipped (resource-changing-tail) ... runningConverters=N` and the pass
  summary `... resource-changing-tail=N` (MINE token).
- `ParsekFlight.cs:21402-21411` `TreeHasConverterWorkForIdle` vetoes idle-on-pad
  (`not idle - tree has N converter toggle(s)`). `:21452-21488` `IsTreeIdleOnPad`. A pad tree
  whose only work is a TRANSMIT (no toggle, no motion) still reads idle; see Q5.
- `SwitchSegmentNoOpClassifier.cs:493-524` `IsMeaningfulPartEvent`: converter events fall to
  the default, meaningful.

Time jump and spawn:
- `ConverterJumpPolicy.cs:165-190` `Apply` (loaded live field, unloaded proto snapshot) logs
  `Converter jump policy (<kind>): ... loadedShifted= unloadedShifted= ...`.
  `:298-316` `ApplyToSpawnSnapshot` logs `Converter jump policy (spawn): ... removed=`.
  `ModuleScienceLab`'s same-named field is left alone (LAB-1 cell).
- `SpawnedHarvesterRestore.cs:26` is `ModuleResourceHarvester` only, for the drill's ground
  raycast. The lab needs no restore: nothing in stock switches a researching lab off except
  zero scientists.
- `ResumedSpawnPartCoverage.cs` is part-generic, so the lab is covered.
- `VesselSpawner.cs:3461` strips crew only for a Destroyed terminal.
- `CrewReservationManager.cs:113` gives a stand-in the reserved kerbal's trait, so a stand-in
  Scientist keeps a lab researching (at the stand-in's level).
- Ghost: the lab part has no `ModuleAnimationGroup`, so a lab ConverterActivated draws nothing
  on a ghost (`GhostVisualBuilder.cs:1975-2000` resolves loop transforms only from an
  animation). This is not a gate.

Re-Fly:
- `ParsekFlight.cs:6664-6675`: a split authors a rewind point only when two or more children
  are controllable (`Single-controllable split: no RP`).
- LABX's booster (Jumbo-64 + Mainsail under a TD-25) has no command part, so LABX-1's
  booster separation authors NO rewind point. That matters for LAB-10 (Q1).
- `RewindPointAuthor.cs:52-55`: EVA is never a Re-Fly split.

Seam (`Source/Parsek/TestCommands/`):
- `TestCommandPartAction.cs:182-200` buttons: `undock decouple controlfrom deploy retract
  startconverter stopconverter experiment labprocess labtransmit inspect radiatoron
  radiatoroff`.
  - Research start / stop is `startconverter` / `stopconverter part=Large.Crewed.Lab` (the
    part's only `BaseConverter`, so no `converter=` is needed), and needs no new button.
  - `inspect` reads `lab:storedScience=F2 dataStored=F2`, `Research:active=0|1` and
    `exp:data=N` (`ParsekTestCommandAddon.PartAction.cs:277-296`).
- `ParsekTestCommandAddon.PartAction.cs:260-274` `PressLabProcess` uses the open dialog page
  only when it is current and matches. Otherwise it reflects `sendDataToLab(datum)`, which
  calls `ProcessData(experimentData)` and then `endExperiment` (the result is discarded) with
  whatever `labValue` the shared `ScienceData` holds. CONFIRMED gap: that value is 0
  - after a load or F9,
  - inside the dialog delay,
  - or with a stale page.
  The fix is Step 0 item 1.
- The labprocess applied line reports the EXPERIMENT part's state, not the lab's. Every
  process step must be followed by `inspect part=Large.Crewed.Lab`.
- Existing verbs reused: `EvaExit kerbal=`, `EvaBoard targetPid=` (boards the nearest free
  part with an airlock: the lab, since the pod seat is full), `CrewTransfer`,
  `SimulateStockSwitchClick pid=`, `ExitToSpaceCenter`, `TimeJump ut=` (FLIGHT, epoch
  shift), `RealSpawn rec=`, `InvokeRewindToLaunch`, `InvokeRewind` / `ReFlyRevert` /
  `AnswerMergeDialog`, `SaveGame` / `LoadGame [focusPid=]`, `RevertToLaunch`, `Recover pid=`,
  `KscAction action=activate-strategy` / `assign-crew`, `GoToEditor` / `LaunchFromEditor`,
  `EnterWatchMode`, `CaptureScreenshot`, `RunTests`, `ListHandles`, `MissionMark`.
- `LoadGame` refuses `recording-active` with a live recorder (`TestCommandDispatcher.cs:765-773`).
  The only opt-in is `allowLiveRecorder=refly` during a Re-Fly. An in-flight F9 with a live
  recorder therefore takes QL-2's route: leave to the Space Center (scene-exit commit), then
  F9 from there.
- `[expectations.ledger]` needs an author-constant amount, and `validate_spec` refuses it next
  to `InvokeRewind`. Ledger checks therefore use the `ScienceLabCredited` / `ScienceChanged`
  lines, a forbidden pattern for a second credit, and `RunTests category=LedgerGroundTruth`
  (FLIGHT, 3 declarations; that it works in SCIENCE_SANDBOX is BELIEF, so check it on the
  first lane that runs it).

## Step 0 (shared prerequisites, being built in other worktrees)

1. labprocess fix in `PressLabProcess`, with xUnit cells. When no current page matches the
   part and subject, call the experiment's public `ReviewDataEvent()`. Then take
   `ExperimentsResultDialog.Instance.currentPage`, call `page.UpdatePageLabValue()` and then
   `page.OnSendToLab(page.pageData)`. Log `labValue=` and refuse
   `partaction-lab-value-zero` when it is <= 0. The lab must be on the active vessel
   (`UpdatePageLabValue` reads it). `design-autotest-command-seam.md` and hlib's
   `PARTACTION_REASONS` mirror move with it.
2. DTS-M1 on the LABX craft, LABX-1 re-flown, `lab-lko-sandbox` and `lab-lko-science`
   re-harvested.
3. `PartAction inspect ... expectSnapshot=true rec=<id>`: compares the live lab's
   `dataStored` / `storedScience` / `EXPERIMENT_DATA` with the recording's end snapshot and
   logs `snapshotMatch=`.
4. Seeded fixture variants (derived builders with drift checks):
   - `lab-pad-seeded`: the lab vessel PRELAUNCH on the pad, `storedScience` about 12.5, the
     antenna, Bob in the lab.
   - `lab-lko-seeded`: `storedScience` just under `scienceCap`, `dataStored` near
     `dataStorage`.

## Seam and C# additions this plan adds (Step 1, after Step 0)

All are small extensions of `PartAction inspect`. Files: `TestCommandPartAction.cs` (pure
format / compare, xUnit) and `ParsekTestCommandAddon.PartAction.cs` (reads), plus hlib mirrors
and `design-autotest-command-seam.md`.

- S1 `inspect expect=<field>:<v>[;...] tolerance=<abs>`, with fields `storedScience`,
  `dataStored`, `experiments` and `researching`. It logs `expectMatch=` and exposes the lab
  fields as payload keys (`labStoredScience`, `labDataStored`, `labExperiments`,
  `researching`) for `${label.key}` substitution.
  - Why: catch-up-once and F9 equality need "equal to an earlier reading", which a static
    regex cannot express (LAB-5, 6, 7, 9a). Same comparison code as S0.3, different
    reference.
- S2 lab reading extras on `inspect`:
  - `experiments=N`, the `ExperimentData` count, for LAB-2's duplicate refusal;
  - `scientists=`, the ScienceSkill crew in the lab part, for LAB-8;
  - `transmitting=0|1`, the private `transmittingData` by reflection, for LAB-4's
    mid-transmission exit;
  - `rnd=`, the R&D pool, to compare a transmit with stock's own gain in one answer.
- S3 vessel CommNet reading on `inspect`:
  `comm:connected=0|1 home=0|1 hops=N viaGhost=<recordingId or empty>`, from
  `vessel.connection` and its `ControlPath`, matching a hop node against
  `GhostCommNetManager`'s nodes. Needed by LAB-11 (and as a precondition read in LAB-4).
  Reuse the `GhostCommNetLive` cells' hop-walk helper.
- S4 (LAB-11 only) a `SyntheticRecordingTests` preset `ghost-commnet-lab` (one relay ghost on
  a higher orbit, phased to sit above the limb while the lab is behind Kerbin). Add a
  geometry test in the shape of `GhostCommNetLaneGeometryTests` that pins the window from
  the fixture's ORBIT node.
- xUnit cells (no lane needs them first, but they close cheap gaps):
  - two transmissions whose credits sum to stock's R&D gain including the fraction paid twice
    (`ScienceLabLedgerTests`);
  - a spawn snapshot keeps the lab's `dataStored` / `storedScience` / `EXPERIMENT_DATA`
    through `ConverterJumpPolicy.ShiftSpawnSnapshotModules` (`ConverterJumpPolicyTests`);
  - a Parsek-spawned lab's recovery pays once.

Not added, with reasons:
- No new verb: everything maps onto `PartAction` and existing verbs.
- No `startresearch` / `stopresearch`: `startconverter` / `stopconverter` already press
  stock's Start / Stop Research.
- No `allowLiveRecorder=quickload`: LAB-9a takes QL-2's route. A true in-flight F9 with a
  live recorder is an owner decision (Q6).
- EVA data buttons (`evacollect` from an experiment, `evastore` into the lab's
  `ModuleScienceContainer`) are deferred. The events are `externalToEVAOnly` /
  `guiActiveUnfocused`, which the candidate filter (`ev.guiActive`,
  `ParsekTestCommandAddon.PartAction.cs:125`) excludes. They need an EVA-only candidate rule,
  and the coverage they buy, an EVA scientist moving data, is container bookkeeping with no
  ledger or recording effect.

## The lanes (final order)

Common prefix for orbital lanes ("orbital prefix"):
1. `LoadGame persistent`, `StopRecording` / `DiscardTree`.
2. `SetSetting autoRecordOnLaunch=false`, `verboseLogging=true`.
3. `inspect part=Large.Crewed.Lab` (baseline).

Rails warp at ~100 km is assumed capped at 50x by Kerbin's altitude limits (BELIEF; budgets
are sized for it, `maxRate` is a ceiling). Wall times assume ~90 s per LoadGame.

### LAB-2 experiments into the lab (lab-lko-science)

Intent: the real processing path stores the stock lab value, a duplicate is refused, and
processing is not science income.

Steps:
1. Orbital prefix.
2. F9 regression guard, before any recorder (LoadGame refuses with one):
   1. `PartAction experiment part=sensorThermometer`, then `WarpToUT +5 maxRate=1` (the
      result arrives through a coroutine).
   2. `SaveGame quicksave`, then `LoadGame quicksave`.
   3. `labprocess part=sensorThermometer`, then `inspect part=Large.Crewed.Lab`. Expect
      `labValue=10` in the applied line and `dataStored=10.00`. Before the Step 0 fix this
      stored 0 and burned the slot.
3. `StartRecording`.
4. `experiment` on `GooExperiment`, `science.module`, `sensorBarometer`; `WarpToUT +10
   maxRate=1`.
5. For each of the three: `labprocess`, then `inspect part=Large.Crewed.Lab`.
6. Duplicate:
   1. `experiment part=sensorThermometer` again (rerunnable, same InSpaceLow subject; 2HOT
      biomeMask 7 has no space bit), then wait.
   2. `labprocess`, `inspect part=sensorThermometer`, `inspect part=Large.Crewed.Lab`.
7. `StopRecording`, `CommitTree`, `StopRecording`, `ListHandles committed`, `MissionMark`.

Assertions:
- applied lines `labValue=12|31|15`;
- the final lab reading `dataStored=68.00 experiments=4`;
- after the duplicate: `exp:data=1` on the thermometer, `dataStored` unchanged,
  `experiments=4`;
- `committree committed=true`, recordings count 1;
- forbidden: `Game state: ScienceChanged`, `ScienceLabCredited`, `partaction rejected`,
  `[ERROR]`.

The exact per-subject values are BELIEF (rounding, the barometer subject). Pin them after the
first flight; until then regex `labValue=[1-9][0-9]*`.

Under test: stock processing through the seam, and that Parsek books nothing for it.

A failure means:
- `labValue=0` or `lab-value-zero`: a seam defect;
- a ScienceChanged or ledger line: Parsek treating processing as income (PARSEK-FAIL).

Tier nightly, about 5 min.

### LAB-3 research over time (lab-lko-science)

Intent: research runs at the stock rate through days of rails warp, the toggles are recorded,
and a still researching lab is never trimmed or discarded.

Steps:
1. Orbital prefix, `StartRecording`.
2. Process the four experiments (LAB-2's steps 4-5, plus the thermometer).
3. `startconverter part=Large.Crewed.Lab`, then `inspect`. Expect `Research:active=1`.
4. `WarpToUT now+120 maxRate=1`. The toggle was made unpacked, so its event lands at press UT.
5. `WarpToUT +43200 maxRate=50` (2 Kerbin days), then `inspect`.
6. `stopconverter`, `WarpToUT +60 maxRate=1`, `inspect`, `startconverter` (so the recording
   ends researching).
7. `WarpToUT +7200 maxRate=50`.
8. `StopRecording`, `CommitTree`, `StopRecording`.

Assertions:
- `Part event: ConverterActivated 'Large.Crewed.Lab' pid=[0-9]+ \(converter\)` (twice) and one
  `ConverterDeactivated`;
- after the 2-day warp, `storedScience` inside a window around 0.73 (+0.73 expected at 68
  data; pin `0\.[5-8][0-9]` first, tighten after measuring) and `dataStored` slightly below 68;
- `TrimBoringTail: skipped (resource-changing-tail) .* runningConverters=1` (or the pass
  summary `resource-changing-tail=[1-9]`);
- the committed recording's EndUT equals the last warp target within a frame
  (`ListHandles committed` end UT field, or the commit line);
- forbidden: `[ERROR]`, any `ScienceChanged`.

Optional cell, if Q4 is ruled: a toggle pressed during a detached rails warp
(`WarpToUT detach=true`) is recorded at the rails exit UT. Pin whichever UT the owner rules.

Under test: the recorder's converter edge for a lab, the optimizer's running-converter
witness, and stock's rate surviving Parsek's warp handling.

A failure in the rate window points at warp or EC starvation; see finding 10 below
(measure once). A trimmed tail is PARSEK-FAIL.

Tier nightly candidate, about 17 min (1,000 s of rails warp). Re-tier by measured p50.

### LAB-3b cap stall and overflow (lab-lko-seeded)

Intent: the two stall states that play cannot reach: the cap is ~500 science against at most
5 x dataStored, and 4 experiments give ~68 data.

Steps:
1. Orbital prefix.
2. `startconverter`, `WarpToUT +7200 maxRate=50`, `inspect`.
3. `WarpToUT +7200 maxRate=50`, `inspect`.
4. `experiment part=science.module`, wait, `labprocess`, `inspect` the experiment and the lab.

Assertions:
- the first reading has `storedScience` in `(499.0, 500.00]`;
- the second reading has the same `storedScience` and the same `dataStored` (a step crossing
  the cap adds nothing and consumes no data);
- after the overflow process: `exp:data=1`, lab `dataStored` unchanged, `experiments`
  unchanged;
- forbidden: `[ERROR]`.

Under test: stock only, plus that Parsek's warp and recording do not disturb it. This is a
cheap pin.

Tier nightly, about 6 min.

EC starvation is deferred: it needs a craft variant without RTGs, and stock already scales
the work by TimeFactor. No Parsek surface reads EC for a lab.

### LAB-4 transmit, twice, and a scene change mid-transmission

Fixture: `lab-pad-seeded` (preferred) or `lab-lko-science` (fallback). The pad variant gives a
permanent KSC link and lets the refill run at landed rails warp in seconds. It needs a seeded
`dataStored` (about 200) on top of the 12.5 `storedScience` so the second transmit can refill
(Q2). The orbital fallback refills organically in ~2.7 days per transmit, about 45 min of
wall: longhaul.

Steps (pad):
1. `LoadGame`, `StartRecording`, `startconverter` (also vetoes idle-on-pad).
2. `inspect` (S3: `comm:connected=1`; S2: `rnd=`), `labtransmit part=Large.Crewed.Lab`.
3. `WarpToUT +10 maxRate=1` (12 Mits = 6 packets x 0.35 s), `inspect`. Expect
   `storedScience` about 0.50.
4. `WarpToUT` until the refill passes 1 (rails), `inspect`, `labtransmit`, `WarpToUT +10`,
   `inspect`.
5. Tail:
   1. `WarpToUT` to refill past 1, `labtransmit`, then `ExitToSpaceCenter` on the very next
      step (an inert step costs one frame; S2 `transmitting=1` read just before the exit
      proves it was in flight).
   2. `LoadGame focusPid=<lab>`, `inspect` (the stored amount is unchanged), `labtransmit`,
      wait, `inspect`.
6. `RunTests category=LedgerGroundTruth`.

Assertions:
- the first transmit: exactly one
  `Game state: ScienceLabCredited \+12\.50 reason='ScienceTransmission' .* recordingId='[0-9a-f]{32}'`
  and one `Game state: ScienceChanged \+12\.5 \(ScienceTransmission\)`. These are the
  ledger's credit and stock's gain, and they must be equal;
- the second transmit: one more credit equal to its ScienceChanged. The pair's sum equals the
  pool move and exceeds the science researched by the first fraction (the quirk): checked
  through S2 `rnd=` before / after with substitution;
- the tail: no `ScienceLabCredited` between the exit mark and the reload mark (forbidden
  pattern, windowed with `MissionMark`), then exactly one after the post-reload transmit;
- `BATCH_COMPLETE v1 total=3 ... category=LedgerGroundTruth scene=FLIGHT` with `failed=0`
  (the skipped count is measured on first flight);
- forbidden: a third credit inside any transmit window, `[ERROR]`.

Under test:
- the capture patch with a real CommNet transmission;
- tagging to the live recording;
- gross equal to stock gain;
- the twice-paid fraction booked as stock pays it;
- an interrupted transmission booking nothing.

A failure means a double or missing capture, or a mis-tag: PARSEK-FAIL.

Tier nightly, about 7 min (pad).

### LAB-5 time jumps (lab-lko-science)

Intent: no science over a jumped span, loaded or unloaded.

Steps:
1. Loaded:
   1. Orbital prefix, process the four experiments, `startconverter`,
      `WarpToUT +600 maxRate=1`, `inspect` (label j0).
   2. `TimeJump ut=+21600`, `inspect expect=storedScience:${j0.labStoredScience}
      tolerance=0.01`.
   3. `WarpToUT +3600 maxRate=50`, `inspect` (research continues).
2. Unloaded:
   1. `ExitToSpaceCenter`.
   2. Launch a second vessel from the pad: `GoToEditor`, `KscAction assign-crew` x2 for
      non-scientists, `LaunchFromEditor`. This is a LABX copy, the one craft the fixture's
      Ships carries (BELIEF that the editor route accepts it in SCIENCE_SANDBOX).
   3. `TimeJump ut=+21600` from the pad vessel, then `SimulateStockSwitchClick pid=<lab>`.
   4. `inspect expect=...` against the pre-exit reading plus at most the real elapsed time's
      catch-up.

Assertions:
- `Converter jump policy \(epoch-shift\): .* loadedShifted=1`, then
  `... unloadedShifted=[1-9] unloadedVessels=[1-9]` for the second jump;
- both `expectMatch=true`;
- `storedScience` rises after the loaded jump (research did not stop);
- forbidden: `[ERROR]`.

Under test: TA-6 on a real, crewed, researching lab.

Tier nightly, about 9 min. The unloaded half shares LAB-6's route: author both on one flight
if the reuse rule fits (LAB-6's assertions hosted here).

### LAB-6 background lab and switch back (lab-lko-science)

Intent: the lab researches only when loaded, catches up once on the switch back, and Parsek
adds nothing.

Steps:
1. Orbital prefix, process, `startconverter`, `inspect` (b0), `ExitToSpaceCenter`.
2. Pad vessel as in LAB-5, `WarpToUT +21600` at the pad (rails, real time passes).
3. `SimulateStockSwitchClick pid=<lab>`, `inspect` (b1).
4. `WarpToUT +60 maxRate=1`, `inspect` (b2).
5. `SaveGame`, `LoadGame focusPid=<lab>`, `inspect expect=storedScience:${b2...}`.

Assertions:
- b1 is above b0 by more than 0 and at most the stock rate times the absence (EC-limited
  catch-up; finding 10, measured once);
- the reload `expectMatch=true` (no second catch-up);
- no `Converter jump policy` line (nothing jumped);
- no `ScienceLabCredited`.

Fallback if the editor route fails: fold the catch-up-once check into LAB-7's Space Center
stay and say so in both specs.

Tier nightly, about 8 min.

### LAB-7 rewind, ghost, spawn (lab-lko-science)

Intent: a committed researching lab comes back through rewind as a ghost, spawns with its
stored data and research state, keeps researching, and is never paid again.

Steps:
1. Orbital prefix, `StartRecording`, process the four experiments, `startconverter`,
   `WarpToUT +3600 maxRate=50`, `StopRecording`, `CommitTree`, `StopRecording`,
   `ListHandles committed` (c0).
2. `InvokeRewindToLaunch tree=latest`, `SaveGame lab7-rewound`.
3. `EnterWatchMode`, `CaptureScreenshot` while the ghost plays.
4. `WarpToUT <end+60> maxRate=50`, `ListHandles committed` (c1),
   `MissionMark lab7-spawned-${c1.rec0spawned}-${c1.rec0spawnedPid}`.
5. `inspect pid=${c1.rec0spawnedPid} part=Large.Crewed.Lab expectSnapshot=true
   rec=${c0.rec0}`, then `WarpToUT +3600 maxRate=50`, `inspect` (keeps researching).
6. `ExitToSpaceCenter`, `WarpToUT +21600`, `LoadGame focusPid=<spawn>`, `inspect` (caught up,
   once; S1 compare after a reload).
7. Jump branch:
   1. `LoadGame lab7-rewound`.
   2. A pad vessel as in LAB-5 (the rewound world has no other vessel; TimeJump needs
      FLIGHT).
   3. `TimeJump` past the end, `ListHandles`, `inspect ... expectSnapshot=true`.

Assertions:
- `committree committed=true`, `invokerewindtolaunch complete rec=[0-9a-f]{32}`;
- both `MISSIONMARK label=lab7-...-spawned-true-[0-9]+`;
- `snapshotMatch=true` on both branches, `Research:active=1` on the spawn;
- `Converter jump policy \(spawn\): .* removed=` on the jump branch;
- Bob reserved while the ghost plays: the `Recomputed ...: N reservations remain` line with
  N >= 1 before the spawn. Assert Bob in the spawn's lab part through S2 `scientists=1`;
- forbidden: `ScienceLabCredited`, `Spawn (RETIRED|ABANDONED|FAILED)`, `uidCollisions=[1-9]`,
  `[ERROR]`.

Under test: the spawn snapshot carrying lab fields, ConverterJumpPolicy on a lab spawn, crew
reservation for a lab scientist, and no ledger replay of a lab state.

Tier nightly candidate, about 15 min.

### LAB-8 EVA crew rules (lab-lko-science)

Intent: research stops when the only scientist leaves, does NOT resume on its own after the
re-board, and the recording and reservation follow Bob (finding 4: `PostProcess` calls
`StopResourceConverter`).

Steps:
1. Orbital prefix, `StartRecording`, process, `startconverter`, `WarpToUT +60 maxRate=1`.
2. `EvaExit kerbal="Bob Kerman"` (BELIEF: the lab part has an airlock), `WarpToUT +10
   maxRate=1`, `inspect pid=<lab>` (`Research:active=0 scientists=0`).
3. `EvaBoard targetPid=<lab>`, `inspect` (still `active=0`, `scientists=1`).
4. `startconverter`, `inspect` (`active=1`).
5. `StopRecording`, `CommitTree`, `StopRecording`.

Assertions:
- `evaexit complete kerbal=Bob Kerman`, then `Part event: ConverterDeactivated
  'Large.Crewed.Lab'` before the board;
- `evaboard complete kerbal=Bob Kerman boardedPid=<lab>`;
- no `ConverterActivated` between the board and the `startconverter`, then one after it;
- the EVA branch and the board in the tree (the EVA split / board-merge lines; EVA is a
  recording split but never a rewind point);
- forbidden: `[ERROR]`.

Optional: `CrewTransfer` of Valentina into the lab proves a non-scientist does not satisfy
`crewsRequired`.

Tier nightly, about 6 min.

### LAB-9a F9 mid-research (lab-lko-science)

Intent: lab fields come back with the quicksave.

Steps:
1. Orbital prefix, process, `StartRecording`, `startconverter`, `inspect` (q0),
   `SaveGame quicksave`.
2. `WarpToUT +21600 maxRate=50`, `labprocess` of a re-run 2HOT is refused as a duplicate
   (adds nothing), `inspect` (q1, higher).
3. `ExitToSpaceCenter` (scene-exit commit; QL-2's route).
4. `LoadGame quicksave` (F9 from the Space Center into FLIGHT).
5. `inspect expect=storedScience:${q0.labStoredScience};dataStored:${q0.labDataStored};
   researching:1`.

Assertions:
- `expectMatch=true`;
- the F9 load classified (`Load classified:` line, the kind measured first flight);
- the recording outcome per the committed-copy rule, pinned after first flight;
- forbidden: `[ERROR]`.

The ledger half (a credit reverted by F9) has no cheap host in orbit: storedScience reaches 1
only after ~2.7 days. LAB-9b covers the ledger for revert. An F9 ledger cell needs a transmit
before the F9 on `lab-pad-seeded`: add it to LAB-9b as a second branch if Q2 seeds data.

Tier nightly, about 7 min.

### LAB-9b transmit then Revert to Launch (lab-pad-seeded)

The lab is PRELAUNCH, so `CanRevertToPostInit` is true.

Steps:
1. `LoadGame`, `StartRecording`, `startconverter`, `inspect` (`rnd=`, `storedScience=12.50`).
2. `labtransmit`, `WarpToUT +10 maxRate=1`, `inspect` (0.50).
3. `RevertToLaunch`, `inspect` (`storedScience=12.50`, `rnd=` back).
4. `RunTests category=LedgerGroundTruth`.

Assertions:
- one `ScienceLabCredited \+12\.50 reason='ScienceTransmission'` before the revert;
- `reverttolaunch complete`, `Load classified:` (StockRevert);
- the post-revert lab reading equal to the pre-transmit one (S1);
- `rnd=` back to the pre-transmit pool;
- LedgerGroundTruth `failed=0`;
- forbidden: any `ScienceLabCredited` after the revert mark.

Under test: the StockRevert policy cells drop a tagged lab credit along with its flight.

Tier nightly, about 4 min.

### LAB-14 recovery (lab-pad-seeded)

The lander is not needed: recovery from the pad hits the same stock `RecoverScienceLabs`.
The lander (reentry plus chutes, `b4_reentry`) adds only flight risk and is deferred with
that reason.

Steps:
1. Branch A:
   1. `LoadGame`, `StartRecording`, `startconverter`.
   2. `Recover pid=<lab>`.
2. Branch B (Parsek-spawned recovery):
   1. `LoadGame` again, `StartRecording`, `startconverter`, `WarpToUT +600`.
   2. `StopRecording`, `CommitTree`, `StopRecording`.
   3. `InvokeRewindToLaunch`, `WarpToUT` past the end (spawn).
   4. `Recover pid=${spawnPid}`.

Assertions:
- each branch: exactly one `Game state: ScienceLabCredited \+12\.50 reason='VesselRecovery'`
  and one `ScienceChanged \+12\.5 \(VesselRecovery\)`;
- branch B: no credit at the commit or the rewind;
- forbidden: a second VesselRecovery credit per branch.

Tier nightly, about 6 min.

### LAB-11 a ghost relay is the only link

Fixture: new `lab-lko-relay` = `lab-lko-science` + `enableGroundStations = False` + seeded
`storedScience` 12.5. Preset: S4.

Steps:
1. `LoadGame`, inject the preset, `WarpToUT` to the derived window where the lab is behind
   Kerbin from the KSC and the relay ghost sits above the limb.
2. Negative control first, before the relay rises or with its node out: `inspect` (S3
   `home=0`), `labtransmit`, `WarpToUT +10`.
3. Then in the window: `inspect` (S3 `home=1 viaGhost=<relay id>`), `labtransmit`,
   `WarpToUT +10`, `inspect`.

Assertions:
- no credit after the control press;
- exactly one `ScienceLabCredited \+12\.50 reason='ScienceTransmission'` after the window
  press;
- `storedScience` about 0.50.

Under test: a real stock transmission completing over a `GhostCommNetNode` path (design
15.6).

A failure with S3 `home=1` but no credit is a ghost-relay defect (PARSEK-FAIL). A failure
with `home=0` in the window is a geometry derivation error (driver).

Tier nightly, about 6 min.

### LAB-12 career strategy take

Fixture: new `lab-pad-career`, built from `strategy-career` (funds 500,000, rep 25) plus the
LABX tech nodes, a science seed of at least 300 (the Patents setup at factor 0.05 is about
263), and the seeded pad lab with its antenna.

Steps:
1. `LoadGame scene=spacecenter`.
2. `KscAction action=activate-strategy strategy=PatentsLicensingCfg`.
3. `SaveGame`, `LoadGame` (into FLIGHT on the pad lab).
4. `StartRecording`, `startconverter`, `labtransmit`, wait, `inspect`.
5. `RunTests category=LedgerGroundTruth`.

Assertions:
- one `ScienceLabCredited \+12\.50` (the gross);
- one `ScienceChanged \+X \(ScienceTransmission\)` with X below 12.5 (net);
- the `Lab payout net of a strategy take` Verbose line;
- the strategy science debit booked once (the L3 query-family tokens, pinned after the
  first flight);
- LedgerGroundTruth `failed=0`.

Under test: the gross-not-net rule in `ScienceLabCreditCapturePatch.cs:17-25` against a live
converter strategy.

BELIEF: that the lab's bare `AddScience` raises the query. If X is 12.5, stock does not apply
the take to a lab transmission and the lane pins that instead.

Tier nightly, about 6 min.

### LAB-10 Re-Fly, supersede and Discard Re-fly

The proposed host, LABX-1's booster separation, authors no rewind point: the booster has no
command part (`ParsekFlight.cs:6664-6675`). Options, cheapest first (Q1):
- (a) Add a small controllable tender (probe core plus battery on a decoupler) to the craft
  revision Step 0 is already re-flying. The separation then happens IN ORBIT: no ascent per
  run.
- (b) A probe core on the booster, then a recorded ascent from `lab-pad-seeded` plus a Re-Fly
  of the upper stage with `rf12s_refly_orbit_insert`: two ascents per run.

Steps with (a):
1. `LoadGame`, `StartRecording`, `PartAction decouple part=<tender decoupler>` (rewind point,
   two controllable slots), `labtransmit` in the lab branch, wait.
2. `StopRecording`, `CommitTree`, `StopRecording`.
3. `InvokeRewind` to that rewind point's lab slot.
4. Run 1 (merge): transmit again (or not), merge with `AnswerMergeDialog`.
5. Run 2: `ReFlyRevert choice=discard`.

Assertions:
- run 1: `Tombstoned [0-9]+ career actions \(.*Science=1`, then one new
  `ScienceLabCredited` in the attempt; LedgerGroundTruth `failed=0`;
- run 2: no tombstone line; the original credit is kept as a direct earning (the
  `DiscardEconomyPreservationTests` rehome rule);
- forbidden: a credit counted twice.

Tier: nightly with (a), about 10 min; longhaul with (b).

AUTHORED 2026-10-18 as LAB-10 (merge) and LAB-10b (Discard Re-fly) on the new
`lab-lko-seeded-transmit` (12.5 science in orbit). Corrections from the code:
- the tender's first undock is a PreAttached decouple: the lab keeps its recording
  (breakup-continuous) and is the FOCUS slot 0, so its Orbiting slot opens only after
  `StashSlot` (RF-20's route);
- run 2's rehome reading is wrong: `PreserveIrreversibleLiveGameplayOnDiscard` is never
  called on a Re-Fly discard (it reloads the rewind point's game). The original credit is
  untouched committed history; the attempt's credit is purged with the attempt. LAB-10b
  pins that.

### LAB-13 dock and undock (last)

Fixture: a docking variant, with Clamp-O-Tron ports from the bdock fixtures on the lab plus a
docked partner that carries an experiment. It comes from a forge flight and harvest, and
must use the fixed labprocess.

Steps:
1. Process the partner's experiment into the lab while docked.
2. Undock (`d5_redock`'s RD phases, or `PartAction undock`).
3. `inspect` the lab, then transmit from the lab.

Assertions:
- the lab keeps `dataStored` / `experiments` after the undock;
- one credit, tagged to the lab's post-undock recording;
- the split lines (`OnVesselsUndocking`, the split branch);
- forbidden: `[ERROR]`.

Tier nightly, about 10 min, once the fixture exists.

## Build order

1. Step 0 (other worktrees): the labprocess fix, the antenna re-fly and re-harvest, `inspect
   expectSnapshot`, `lab-pad-seeded`, `lab-lko-seeded`. Fold Q1(a) and Q2 in now if ruled,
   so the craft is re-flown once.
2. Step 1 seam: S1, S2, S3 with xUnit cells and the hlib / seam-doc mirrors; the three xUnit
   ledger and spawn cells.
3. Wave A, orbital and fixture-ready: LAB-2, LAB-3, LAB-3b, LAB-8.
4. Wave B, pad: LAB-4, LAB-9b, LAB-14.
5. Wave C: LAB-5 + LAB-6 (shared pad-vessel route), LAB-7, LAB-9a.
6. Wave D, new fixtures: LAB-11 (S4 preset plus geometry), LAB-12 (`lab-pad-career`), LAB-10
   (separation variant), LAB-13 (docking variant).

Each lane is flown one at a time on V's PC, unflown specs pinned per the INTERIM rules.

## Open questions for the owner

- Q1 LAB-10's separation. Add a decoupled controllable tender to the craft before LABX-1 is
  re-flown in Step 0 (one re-fly, cheap runs)? Or put a probe core on the booster (two ascents
  per run)? The booster as built gives no rewind point.
- Q2 Seed `dataStored` (about 200) into `lab-pad-seeded` so the pad lanes can refill past 1
  for the second transmit? Without it, LAB-4 flies in orbit as a ~45 min longhaul.
- Q3 Is the pad recovery (LAB-14) accepted in place of a lander for the ledger claim, with the
  lander deferred for flight risk only?
- Q4 Orbital research toggles made during rails warp are recorded at the rails exit, and an
  on-and-off pair inside one warp is lost (`FlightRecorder.cs:9385`, `:11626`). No ghost
  visual and no ledger effect depend on it. Accept and pin, or poll part states on rails for
  orbiting vessels?
- Q5 A pad flight whose only work is a lab transmit or recovery reads idle-on-pad
  (`ParsekFlight.cs:21452`). Its tagged credit then relies on the discard rehome. Should a lab
  payout veto idle-on-pad the way a converter toggle does?
- Q6 Should the seam grow `allowLiveRecorder=quickload`, so a lane can F9 in flight with a
  live recorder as a player does, rather than QL-2's leave-then-F9 route?
- Q7 A stock transmit refused with no link (or `storedScience < 1`) still answers PartAction
  OK. Should `labtransmit` read the outcome (`transmitting=1` right after the press) and
  answer `partaction-lab-transmit-refused`? This plan reads S2 instead, with no change to the
  button.

## Risks

- The lab airlock for `EvaExit` (LAB-8) and the editor route in SCIENCE_SANDBOX with the LABX
  craft (LAB-5 / 6 / 7) are BELIEF. Each fails as driver-INVALID on its first flight with the
  refusal named.
- The 50x rails cap at ~100 km sizes every orbital budget. A lower cap doubles LAB-3's wall.
- Catch-up amounts after unloaded spans are EC-limited and stock-defined (finding 10). Windows
  stay wide until measured.
- `LedgerGroundTruth` in SCIENCE_SANDBOX is unproven. LAB-4 is its first reader.
- Seeded fixtures are by construction. A `storedScience` / `dataStored` value KSP clamps on
  load (`PostProcess` clamps `dataStored` to `dataStorage` and `storedScience` to the cap)
  would shift LAB-3b's windows. Read the baseline `inspect` before trusting a window.
- LAB-11's geometry is derived, not flown. The lab's ~1,960 s orbit is about 6x shorter than
  CN-2's Duna orbit (12,108 s), so the occlusion window is shorter and the warp landing must
  be tighter.
