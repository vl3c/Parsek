# Part-action recording audit — what exists, what we record, what we should record

**Date:** 2026-08-09. **Method:** 103 distinct stock `PartModule` types enumerated from
`Squad/Parts` + `SquadExpansion` cfgs; each module's `[KSPEvent]` / `[KSPAction]` /
`[KSPField(isPersistant)]` surface decompiled from `Assembly-CSharp.dll` (KSP 1.12.5, ilspycmd);
cross-matched against Parsek's 35 `PartEventType` values, 22 `GameStateEventType` values, the
59 subscribed `GameEvents`, the playback dispatch in `GhostPlaybackLogic.ApplyPartEvents`, and the
Re-Fly restore path. 167 cross-matched rows. Static read only — no KSP run, no harness flight.

---

## 0. The restore model, first — it reclassifies most of the audit

Before any "the world desyncs after a rewind" claim can be evaluated, what the Rewind Point
quicksave actually reloads has to be established. It is a **full KSP save**, not a vessel snapshot:
`FlightRecorder.cs:4489` writes it with `GamePersistence.SaveGame(...)`, so the file carries the
entire `GAME` node — `ResearchAndDevelopment`, `ProgressTracking`, `ContractSystem`,
`ResourceScenario`, `DeployedScience`, `ROCManager`, `KerbalRoster`, `ScenarioUpgradeableFacilities`.

That yields a clean four-way rule for any world-state facet `F`:

| Where `F` lives | Outcome |
| --- | --- |
| `GAME` node **and** in the seven-facet patch set (`KspStatePatcher.cs:87-93`) | Correct both directions. Nothing to do. |
| `GAME` node but **not** patched | Reverted to rewind UT and never rolled forward. Correct for the superseded branch, **silent regression** for every surviving branch. |
| A `PART`/`MODULE` node on the **selected slot** | Restored verbatim. **Benign.** |
| On a **non-selected vessel** | **Destroyed** — see §1. |

**What this makes benign** (restored byte-for-byte, no work needed): `ModuleScienceExperiment`
`Deployed`/`Inoperable` + held `ScienceData`; `ModuleScienceContainer` contents; MPL
`dataStored`/`storedScience`; ISRU-converted tank contents; per-part resource amounts;
`PartResource._flowState`/`_flowMode`; crossfeed; the `ACTIONGROUPS` bitmask; `referenceTransformId`;
`activeControlPointName`; `vesselType`; crew seat assignment; `ModuleInventoryPart` slots;
`ModuleDeployablePart.BROKEN`; `ModuleWheelDamage.isDamaged`; ablator remaining; gimbal /
control-surface / robotic persistent positions.

Two claims that looked like MUSTs and are **not**: **ore depletion** and **biome/planet unlock**.
`grep ResourceMap Source/` returns 0 hits — the grep is right, the conclusion drawn from it was
wrong. `ResourceMap.DepletionInfo` is populated from `ResourceScenario`, a `ScenarioModule` inside
the `GAME` node the quicksave restores. Undone for free.

Recording any of the above buys **divergence detection** between the recorded branch and a re-fly.
That is a diagnostic goal, not a save-consistency one, and it should not be funded from the
ghost-visual budget.

---

## 1. The finding that outranks everything else: Re-Fly deletes the fleet

> **STATUS: FIXED 2026-08-09** on branch `refly-world-preservation`. Removal is now scoped to
> this RP's other slots; unrelated vessels, `SpaceObject` asteroids/comets and flags are
> preserved, and `stripUnmatchedVessels` is `false` so the `LeftAlone` branch is live. The
> `#587` name-matched debris kill is a separate pass and was not touched. See
> `docs/dev/todo-and-known-bugs.md` → `REFLY-DELETES-NON-SLOT-WORLD`. Everything below is the
> pre-fix record.

`RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly` walks `FLIGHTSTATE`'s `VESSEL` nodes with

```
bool keep = (vesselPid != 0u && selectedVesselPids.Contains(vesselPid))
         || (rootPartPid != 0u && selectedRootPartPids.Contains(rootPartPid));
```

(`RewindInvoker.cs:1975-1976`) and `RemoveNode`s everything else (`:2000-2001`). The scrub is
mandatory — `RequireSelectedSlotScrubApplied` throws rather than load an unscrubbed save
(`:1901-1912`) — and `PostLoadStripper` then runs with `stripUnmatchedVessels: true`
(`RewindInvoker.cs:805`) as a second net. There is no repopulation path anywhere.

This directly contradicts the binding design doc.
`docs/dev/done/parsek-rewind-separation-design.md:593`, step 4:

> **Else: leave alone.** The vessel does not belong to this RP's slot set (pre-existing stock
> vessel, different tree, debris, etc.)

`CHANGELOG.md:1089` shows the scrub was added deliberately ("every real vessel except the selected
Re-Fly vessel is removed"), so this is a shipped intent that diverged from the design without the
design being updated. **Whether the current behavior is wanted is a call only you can make** — but
these are its consequences:

**1a. The career fleet.** Rewinding one booster separation on mission 12 removes the Mun station,
relay satellites, rovers, deployed-science clusters and every other vessel from the loaded game.
The ledger still carries their recovery credits and contract completions, so the economy claims a
fleet that no longer exists, and `DeployedScience` / `ROCManager` rows point at destroyed vessels.

**1b. Asteroids and comets — strictly worse.** Every `VesselType.SpaceObject` node is a `VESSEL`
node, so every discovered asteroid and tracked comet is deleted too, including active
grapple-contract targets. A station can be relaunched; a procedurally-spawned, discovery-timed
asteroid cannot. A narrow "keep player-controlled vessels" fix would still destroy them.

**1c. Planted flags — a fix already coded at the wrong layer.**
`PostLoadStripper.ShouldPreserveVesselType` returns true for `VesselType.Flag`
(`PostLoadStripper.cs:238-241`) and bypasses before the slot match (`:118-131`). But the scrub
already removed the flag `VESSEL` nodes from the .sfs, so no flag vessel exists in `FlightGlobals`
when the bypass is consulted. **That branch is dead in production.** The `MilestoneAchievement` row
survives in the ELS; the marker does not.

**Fix site for all three: the scrub keep-predicate.** Not the ledger, not a `PartEventType`.
If the deletion is load-bearing for stock patched-conic sanity (the `#587` pre-existing-debris
rationale), the minimum coherent version is: keep everything, strip only debris and Parsek-tracked
slot siblings, and log the removals.

---

## 2. The second structural miss: career-bearing actions with a modest visual

The audit was organized around two sieves — "is it visible to a bystander" and "does the quicksave
restore it". An action that is career-bearing but visually modest falls through **both**. That is
how these reached the end of a full audit unnamed, with production reference counts measured:

| Module | Stock declarations | Parsek production refs | What it does |
| --- | --- | --- | --- |
| `ModuleScienceExperiment` | 158 | **1** (a comment at `VesselSpawner.cs:744`) | 8 `[KSPEvent]`s incl. `DeployExperiment`, EVA collect, reset, clean. `Deployed` is persistent and gates the deploy animation (Goo canister, Science Jr doors). |
| `ModuleDataTransmitter` | 201 | 6 (all static `AntennaSpec`) | `StartTransmission` / `StopTransmission`. Multi-second animated sequence that converts stored data into career science. Read only as a CommNet spec, never as a timeline. |
| `ModuleTestSubject` | **709** (2nd-most-declared module in the game) | **0** | `RunTestEvent`, armed by part-test contracts. Completes contract parameters → funds + rep. A whole contract genre with no representation. |
| `ModuleOrbitalSurveyor` | 2 | **0** | `PerformSurvey` — the module that produces the planet-unlock facet. |
| `ModuleToggleCrossfeed` | 158 | **0** | `ToggleEvent` + 3 actions. Standalone crossfeed, distinct from the docking-node field. |
| `ModuleGenerator` / `ModuleReactionWheel` | 94 / 90 | **0** / **0** | Activate/Shutdown; reaction-wheel authority is a direct re-fly trajectory input. |
| `ModuleGimbal` | 243 | **0** | The most common continuous visual motion in the game. |
| `ModuleSurfaceFX` | 183 | **0** | Launch dust/water plume — the most conspicuous thing about a rocket leaving the pad. |

`ModuleSurfaceFX` is the sharpest of these because it is **free**: synthesizable at playback from
recorded engine power + recorded altitude, exactly the way `ApplyAblationChar` already synthesizes
reentry char.

---

### CORRECTION 2026-08-12 (`part-event-fidelity`/P8) — the science-timeline row is IMPRECISE, and it changes a verdict

The table above says of `ModuleScienceExperiment` that "`Deployed` is persistent and **gates the
deploy animation** (Goo canister, Science Jr doors)". That reads as "the animation is a property of
the experiment module, so recording the experiment is the only way to get the visual" — and on that
reading P8 owed the science timeline a recorder. It does not, and the wave recorded **zero** new
event types for it. Four per-module verdicts, each with the evidence that settled it.

**`ModuleScienceExperiment` (158 declarations) — WON'T, because the deploy visual is ALREADY
RECORDED.** The animation is not the experiment module's at all. The Goo canister and Science Jr
each carry a **separate `ModuleAnimateGeneric`** named `Deploy`, wired to the experiment through
`FxModules = 0` (`Squad/Parts/Science/GooExperiment/gooExperiment.cfg:29-46`,
`.../MaterialBay/materialBay.cfg:34-52`). `ModuleScienceExperiment` is **not** in
`FlightRecorder.HasDedicatedAnimateHandler`'s list, so `CheckAnimateGenericState` polls that
animation exactly like any other standalone one, and playback animates it through the ordinary
deployable family. The visual was covered before P8 and is covered now.

What P8 added instead is the **verification** that had never existed: a live cell
(`PartEventFidelityInGameTests.ScienceExperimentDeployVisualIsAlreadyCoveredByTheAnimateGenericPath`)
asserting both halves on a real prefab — that the recorder does not skip the part, and that the
resulting event really does move the ghost. If this verdict ever stops being true, that cell reds. A
doc sentence cannot do that, which is the whole reason this correction is written down: the imprecise
sentence nearly bought four event types nobody needed.

`OnExperimentDeployed` stays unused, and that is now a decision rather than an oversight: it is a
duplicate signal for a visual already polled, and the career side is already captured through
`OnScienceReceived` / `OnScienceChanged` with UT + reasonKey + recordingId.

**`ModuleDataTransmitter` (201) — WON'T, because the only stock transmit visual is one the recorder
ALREADY polls, and the transmit-progress visual proper has no stock setters.**

*Evidence corrected 2026-08-12.* An earlier draft of this paragraph claimed `DeployFxModuleIndices`
and `ProgressFxModuleIndices` are set by **ZERO** stock parts. That was a **grep-name trap** and the
claim was false. Those are the C# FIELD names; the cfg KEYS `ModuleDataTransmitter.OnLoad` parses
into them are `DeployFxModules` and `ProgressFxModules` (decompiled, KSP 1.12.5:
`DeployFxModuleIndices = KSPUtil.ParseArray(node.GetValue("DeployFxModules"), int.Parse)`). A grep
for the field name over `GameData` returns a confident zero because the string never appears in a
cfg at all. Grepping the cfg KEY finds **six** stock antennas, every one of them setting
`DeployFxModules = 0`: `HighGainAntenna`, `commDish88-88`, `commsAntennaDTS-M1`, `commsAntenna16`,
`HG-5`, `HG-5_v2`. `ProgressFxModules` does have zero stock setters.

The WON'T verdict stands, now on the corrected evidence:

- **`DeployFxModules = 0` resolves to the part's own `ModuleDeployableAntenna`** — verified in all
  six cfgs, where module index 0 is that module and `ModuleDataTransmitter` is index 1. Stock drives
  it with `SetFXModules(deployFxModules, 1f)` at transmit start and back to its recorded start
  position at the end, so the stock "transmit visual" IS the dish extending and re-stowing. That is
  a `ModuleDeployablePart.deployState` change, and `FlightRecorder`'s deployable poll
  (`FindModuleImplementing<ModuleDeployablePart>()` — `ModuleDeployableAntenna` derives from it) has
  always recorded those as `DeployableExtended` / `DeployableRetracted`. Already covered; a
  transmit event type would be a second recorder for one visual.
- **`ProgressFxModules` — the transmit-progress visual proper — genuinely has zero stock setters.**
  That is the scalar stock zeroes at transmit start and then drives from the transfer's
  remaining-data ratio as the packets go out, and no stock part wires anything to it. Recording a
  progress "visual" would be inventing one.

The career side is already captured. `busy` is reachable through the public `IsBusy()` if a future
wave ever wants the timeline for a non-visual reason.

The trap generalizes and is cheap to repeat: for any array a module parses by hand in `OnLoad`, the
cfg key and the C# field name need not match, so a field-name grep over `GameData` proves nothing.
Grep the cfg key, and confirm it by reading the `OnLoad` that consumes it.

**`ModuleTestSubject` (709, the second-most-declared module in the game) — WON'T.** The tested
part's own action is already recorded by whichever family owns it (an engine test is an engine event,
a decoupler test is a decouple), and contract completion is already in `GameStateEvent` types
2/15/17. `RunTest` fires `onTestRun` (verified present) and has no visual of its own.

**`ModuleOrbitalSurveyor` (2) — WON'T for P8.** The M700's deploy is already recorded by the
AnimationGroup family, and `PerformSurvey` produces no part visual — it fires
`GameEvents.OnOrbitalSurveyCompleted` (verified present). The planet-unlock facet is a LEDGER concern
and belongs to a ledger wave, not a part-event one. The hook is named here so the next reader does
not re-derive it.

**The shape shared by all four.** Each fails the audit's own two sieves the same way: either the
visual already has a recorder, or there is no visual at all. That is why the honest P8 output for §2
is one verification cell plus this note rather than four new event types — and it is worth saying
plainly, because "the audit found it unrecorded" reads like a mandate right up until you check
whether anything is actually unrecorded.

---

## 3. Where the event types actually stand (35 at audit time; 44 after P8)

**Well covered, recorded and played:** decouple, dock/undock (as tree topology), destroyed,
shroud jettison, fairing deploy, parachute semi/full/cut/destroyed, engine ignite/shutdown,
RCS start/stop, deployable extend/retract, gear deploy/retract, cargo bay open/close, lights +
blink + rate, thermal 3-state *(playback path only — see below)*, inventory place/remove.

**Recorded but with no playback effect at all:** `Docked` (21) and `Undocked` (22) are the only two
members with no `case` in `ApplyPartEvents` (`GhostPlaybackLogic.cs:1220-1392`). Correctly so —
there is no visual to render and the vessel-level truth is the chain-segment tree. Their *identity*
fields are however broken as metadata: `Docked` carries the **merged vessel** pid in a field the
rest of the pipeline treats as a part pid, and `Undocked` hard-codes pid `0`
(`ParsekFlight.cs:12193`, `:12206`).

> **STATUS: FIXED 2026-08-11** on branch `playback-fidelity` (P5/P6 S1). Both
> `SetEngineEmission` and `SetRcsEmission` now scale the plume as a RATIO of a build-time
> captured baseline, so it composes with the #383 size boost and the world-space velocity
> floor instead of overwriting them; `ComputeScaledRcsEmissionRate` / `ComputeScaledRcsSpeed`
> are that ratio's numerator and denominator, which is how they got production callers with
> their showcase floors intact. The audio path is deliberately untouched — it already read the
> magnitude, and touching it would double-apply. Line numbers below are pre-fix.

**Recorded magnitude that playback throws away.** `EngineThrottle` is recorded with a 0.05 deadband
and `SetEngineEmission` (`GhostPlaybackLogic.cs:2172-2204`) branches only on `power > 0f`. Every
reader of `currentPower` was enumerated: only `:2405` and `:2423` (audio volume/pitch curves)
consume the magnitude; `:1812, :2298, :2344, :2378, :2872, :2894, :3453, :3485` are all boolean
gates. **A 1.0 → 0.3 throttle sweep is audible only.** The RCS scaling helpers
`ComputeScaledRcsEmissionRate` / `ComputeScaledRcsSpeed` (`:3354`, `:3367`) exist and are called
from **nothing in production** — the only call sites in the solution are
`Source/Parsek.Tests/RuntimePolicyTests.cs:210,219,228,230`.

> This is worst on the install Parsek went to the most trouble to support. `WaterfallCompat.cs`,
> `ReStockPatchFxIndex.cs` and `PristinePartFxResolver.cs` exist so ghost plumes reproduce
> Waterfall FX — and Waterfall's whole premise is a plume that is a continuous function of throttle.

**Five probes that are documented as shipped and are inert on 100% of stock parts.**
`docs/dev/done/next-parts-event-support-priority.md:43-47` claims `ModuleAeroSurface`,
`ModuleRobotArmScanner`, `ModuleControlSurface`, `ModuleAnimateHeat` and dynamic wheel/leg motion
are "now recorded and showcased". True of the synthetic showcase path only. Root causes:

- `module.Fields` is `[KSPField]`-only by construction (`FlightRecorder.cs:3733-3748`).
- `ModuleControlSurface`'s real field is `deploy`; the probe table lists `deployed`
  (`FlightRecorder.ReflectionClassifiers.cs:238-249`). **One missing string.**
- `ModuleAnimateHeat`'s live scalars `animState`/`inputState` are plain public fields with no
  attribute; the usable accessor is the `IScalarModule.GetScalar` **property**. Not fixable by
  adding names — needs a typed cast.
- Piston: `traverseVelocity` is a `[KSPAxisField]` **speed slider, constant during a stroke**. It
  resolves, so the working `servoTransformPosition` fallback at `:876-881` is never reached. Same
  shape for wheel suspension (`suspensionOffset` is a config constant shadowing live `suspensionPos`).

**A confidently wrong signal that playback faithfully renders.** Two cases, and they are correctness
bugs rather than fidelity gaps:

- **Parachute repack replays as a cut.** `CheckParachuteState` (`FlightRecorder.cs:1665-1690`)
  collapses STOWED, ACTIVE and CUT into state `0`, so a repack emits `ParachuteCut`, and
  `ApplyParachuteCutEvent` zeroes the canopy *and hides the cap* — a repacked chute renders as an
  empty can.
- **Wheel spin is percent-of-torque replayed as RPM.** The probe reads `driveOutput`, which
  decompiles to `Mathf.Abs(driveInput * maxDriveTorque / maxTorque) * 100 * resourceFraction`,
  replayed at `value * 6` deg/s (`GhostPlaybackLogic.cs:3588`). A coasting rover shows stationary
  wheels and reverse is indistinguishable from forward.

---

## 4. Initial state: the answer, and the one structural hole

**Parsek is not delta-only.** There are three layers:

1. The recorded snapshot **is** a full `ProtoVessel` serialization (`VesselSpawner.TryBackupSnapshot`
   → `vessel.BackupVessel()` + `pv.Save(node)`), captured at every `StartRecording`
   (`FlightRecorder.cs:6080-6082`). `deployState`, `currentRotation`, `ACTIONGROUPS`, `flowState`,
   docking FSM state and robotics positions are all physically on disk in `<id>_vessel.craft`.
2. **The ghost builder throws almost all of it away.** `AdvanceTimelineGhostBuild` reads only
   `name`/`part`, `persistentId` and pos/rot from each PART node (`GhostVisualBuilder.cs:431-441`),
   then builds from `ap.partPrefab` (`:446`, `:464`). Exactly **three** module states are read from
   the snapshot: `ModuleJettison.isJettisoned`, `ModuleProceduralFairing` fsm + XSECTION, and
   `ModulePartVariants` selection.
3. **Everything else rides synthetic seed events.** `PartStateSeeder.SeedPartStates` probes a
   **16-family whitelist** and `EmitSeedEvents` converts each non-default state into a PartEvent at
   start UT. Only the ON/EXTENDED/DEPLOYED direction is seeded; the stowed default is the ghost's
   build-time prefab pose.

**The structural hole is the promotion gate** (`FlightRecorder.cs:6544-6581`). Every
chain-continuation segment — dock, undock, quickload-resume, BG→FG promote, **Re-Fly fork**,
`CreateSplitBranch`, `CreateMergeBranch`, switch/Fly continuation — deliberately emits **engine
seeds only**, to protect the `#263` `FindLastInterestingUT` boring-tail-trim invariant. That
rationale is sound. Its consequence is that the post-rewind half of every re-fly renders gear up,
panels folded, lights off and bays closed for its whole span, and nothing self-corrects because a
ghost only ever replays its own recording's events.

The three snapshot-read families (shrouds, fairings, variants) are the only ones immune — which is
also the shape of the cheapest fix, and the existence proof that it works.

**Additional hole:** robotic events are in **neither** split-seed family —
`IsPermanentVisualStateEvent` (5 members) nor `IsReversibleVisualStateEvent` (18 members) — and
`ReapplySpawnTimeModuleBaselinesForLoopCycle` never calls `ApplyRoboticPose`. So a HEAD/TIP split
drops servo pose for the tail, and a looped ghost starts cycle N+1 wherever cycle N ended.

---

## 5. Recommendations

### MUST — correctness; do these first

| # | Item | Site | Cost |
| --- | --- | --- | --- |
| **C1** | Restore non-slot vessels (fleet, **asteroids/comets**, flags) to the loaded save | `RewindInvoker.cs:1975` keep-predicate | small |
| **C2** | Pre-invoke advisory naming what the revert will drop | `CanInvoke` | small |
| **M1** | Read the initial-state baseline from the snapshot at **build time**, not as seed events | `GhostVisualBuilder.AddPartVisuals` (already receives `partNode`) | **zero storage** |
| **M4** | Add robotic events to both split-seed families + loop-restart | `RecordingOptimizer.SeedEvents.cs` | **zero storage** |
| **M3** | 4-state parachute classifier + `ParachuteRepacked` | `FlightRecorder.cs:1665-1690` | zero net |
| **M5** | Vessel guard on the engine/RCS caches + rebuild from `OnVesselWasModified` | `FlightRecorder.cs:3417`, `:6520` | **negative** |
| **M6** | Emit terminal events on the **background** rails transition | `BackgroundRecorder.cs:2316-2336` | ~2-4/round trip |

**M1 and M4 must land together.** A `RecordingTreeSplitter` HEAD/TIP cut produces a TIP that
inherits the *parent's* snapshot, taken at the original launch UT — so M1 alone would give every
post-rewind fork a **pre-launch** baseline, which is worse than no baseline because it is
confidently wrong.

### SHOULD — real fidelity, modest cost

- ~~**S1. Consume the throttle magnitude already on disk.**~~ **DONE 2026-08-11** (`playback-fidelity`), as a ratio of a captured baseline rather than absolute writes. Wire emission rate, start size and start
  speed to `power` on both engine and RCS paths; the RCS helpers already exist. Zero storage,
  largest fidelity-per-line ratio in the audit, and the fix Waterfall users will notice.
- ~~**S2. Interpolate the two-pose deployables**~~ **DONE 2026-08-11** (`playback-fidelity`). Progress is a pure function of the RECORDED EVENT UT rather than wall time, so prefix catch-up, scrubs, warp and loop cycles all land correct with no extra state; the clip length is the prefab's own. Original text: interpolate instead of snapping (solar/gear/bays). Zero storage;
  per-frame cost bounded by transition duration, not flight duration.
- ~~**S3. Synthesize continuous motion at playback rather than recording it**~~ **DONE 2026-08-11**
  (`playback-fidelity`), with ONE correction to the prescription below: the attitude derivative is
  taken from the ghost's APPLIED WORLD ROTATION (`state.ghost.transform.rotation`), never from
  `TrajectoryPoint.rotation` read out of a flat Points list. In a RELATIVE track section that field
  holds an anchor-local rotation rather than `srfRelRotation`, so differencing two of them across a
  section boundary mixes frames and invents a rotation that never happened. The transform is
  post-resolution on every playback path, which makes it the one reading that is frame-correct
  everywhere. Launch dust is also narrower than written: Parsek owns its particle system outright
  (the reentry `fireParticles` template) rather than driving `ModuleSurfaceFX`, and it is gated on a
  ground reference latched from `recordedGroundClearance` — no clearance, no dust, permanently.
  Original text — gimbal deflection and
  control-surface deflection from the frame-to-frame derivative of recorded `srfRelRotation`; wheel
  steering from heading change over ground; solar/antenna tracking by aiming at
  `Planetarium.fetch.Sun`; launch dust (`ModuleSurfaceFX`) from engine power + altitude.
  **Precedent: `ApplyAblationChar` already does exactly this** for reentry char. Zero storage, zero
  recording cost.
  - ~~**Corollary: re-derive wheel spin from ground speed**~~ **DONE 2026-08-11** (PR #1445), and
    wheel STEERING joined it on `playback-fidelity` for the same reason: the recorded
    `ModuleWheelSteering` scalar was a steering INPUT, not a caliper angle — and, on the review
    pass, its producer was deleted alongside the motor one (the recorder emission gate is now
    `FlightRecorder.IsDerivedWheelVisualModuleName`), so both derived wheel visuals cost zero
    bytes rather than one being ignored at read time. Original text: delete
    the `driveOutput` family. It is
    storage-*negative* and strictly more correct than the signal it replaces.
- ~~**S4. EVA jetpack deploy/thrust + ragdoll.**~~ **DONE 2026-08-12** (`part-event-fidelity`/P8),
  with the anchor-filter reading CONFIRMED (the filters have since moved to `:5250` / `:5439` and
  both remain anchor-candidate filters). Six members: the jetpack deploy/stow pair, the thrust
  pair, and the ragdoll pair. THREE corrections to the prescription below.
  - **The typed-cast rationale was wrong in one particular, and the particular matters.** The plan
    said none of the three KerbalEVA members is a `[KSPField]`. `JetpackDeployed` IS
    (`[KSPField(isPersistant = true)]`, KerbalEVA.cs:185); `JetpackIsThrusting` and `isRagdoll` are
    plain public fields. Typed casts are still the right call — a `module.Fields` walk, the
    recorder's usual route into a type it cannot reference, would have silently missed TWO of the
    three — but the reason is "two of three are invisible to a Fields walk", not "none is a
    KSPField". Do not build a reflection path on the original premise.
  - **Only THRUST is debounced.** `JetpackIsThrusting` is recomputed every FixedUpdate from
    `fuelFlowRate > PropellantConsumption / 2 * thrustPercentage * 0.01`, so it flickers across
    consecutive frames on a single tap; it reuses the RCS frame threshold rather than introducing a
    second number to keep in step. Deploy and ragdoll are clean FSM edges and get none.
  - **The ragdoll POSE is a deliberate WON'T; the ragdoll EVENTS are not.** A ragdoll is a physics
    outcome with no clip to sample, so a replayed pose would be invention. The events still earn
    their keep: they gate the thrust plume (a tumbling kerbal is not flying, and stock cuts thrust
    on FSM ragdoll entry) and they mark the timeline. State the asymmetry rather than reading the
    events as an unfinished pose feature.
  - **The pack MESHES stayed out, and NOT because the asset was unreachable.** The probe succeeded:
    `KerbalEVA.JetpackTransform` is a public serialized `Transform`, so no name guessing is needed,
    and stock's `UpdatePackModels` does nothing but `SetActive` on it. The blocker is the GATE. That
    flag is `HasJetpack`, driven by whether an `evaJetpack` sits in the kerbal's
    `ModuleInventoryPart`, and Parsek records no such signal — so every gate reachable from recorded
    data renders visibly wrong: showing the pack on the first deploy event pops a backpack into
    existence mid-spacewalk and leaves every ground EVA without one, while showing it
    unconditionally puts a jetpack on kerbals who carry none. The honest follow-on is to read the
    snapshot's `ModuleInventoryPart` contents as a new baseline surface. Recorded at the canopy
    lazy-clone site in `GhostVisualBuilder`, where a future reader would look.
- **S5. Fix the dead probes.** `deploy` + `deployAngle` strings for aero/control surfaces;
  `currentExtension`/`targetExtension` prepended for pistons; drop `suspensionOffset`;
  `(module as IScalarModule).GetScalar` for `ModuleAnimateHeat` — **one cast unlocks a complete,
  already-implemented recorder + playback path** (`GhostPlaybackLogic.cs:3693-3770`).
- ~~**S6. Deployable `BROKEN`.**~~ **DONE 2026-08-12** (`part-event-fidelity`/P8). One correction
  to the description: it is **NOT permanent**. `eventRepairExternal` is active exactly while BROKEN
  and `DoRepair()` returns the part to RETRACTED, so `DeployableBroken` is a REVERSIBLE-family
  member (split-seed family 3, seeded verbatim like the parachute trio) rather than a
  `ForwardPermanentStateEvents` type. Two ordering rules carry the fix, both bugs that would have
  rendered plausibly rather than obviously: entering BROKEN must SILENTLY drop the pid from the
  extended set (a panel normally breaks while extended, so leaving the flag would emit a spurious
  `DeployableRetracted` and playback would fold the panel neatly shut before hiding it), and leaving
  BROKEN must emit `DeployableRetracted` EXPLICITLY (playback needs a positive instruction to
  un-hide). The same precedence is required on the rails-span reconciler, where a break shows up as
  two simultaneous set changes. The visual is `panelBreakTransform.gameObject.SetActive(false)`;
  note that a LIVE break instead detaches the subtree as debris via `breakPanels()` and the hide is
  what a later load applies, so hiding is the right net rendering for a ghost either way.
- ~~**S7. Drill/ISRU running animation.**~~ **DONE 2026-08-12** (`part-event-fidelity`/P8), and
  deliberately NOT by extending the harvest window. That window is vessel-scoped and exists to
  attribute harvested resources; the visual is per-PART, so P8 added a separate per-part
  `CheckConverterState` keyed on `BaseConverter.IsActivated` (a `[KSPField(isPersistant = true)]
  public bool`, so it covers `ModuleResourceConverter`, `ModuleResourceHarvester` and the
  asteroid/comet drills with no module-name list) — which also left the background wrapper with no
  window machinery to replicate. 2 events per mining session as estimated. Playback samples the
  RUNNING clip at twelve phases rather than interpolating two poses, because a cyclic clip's
  endpoints are the SAME pose and a two-pose delta is zero — the drill would "run" by standing
  still. THE STOCK POPULATION IS FIVE PARTS, NOT THE FOUR THE PLAN ENUMERATED: RadialDrill
  (`Drill_Running`), MiniDrill (`Drill`), the large ISRU (`ProcessorLarge_running`) and the orbital
  scanner (`miniscanner`) all carry running clips, and the last two have an EMPTY
  `deployAnimationName` — which is why the loop info is built independently of
  `TryGetAnimationGroupDeployAnimation`.
- **S8. Kerbal XP as a ledger facet.** Monotone, so the patcher is a re-assert not a diff.
  `ModuleTripLogger` (101 parts) is the mechanism that writes it; zero references today.
- **S9. Contract re-snapshot at RP capture** rather than at accept — fixes the rebuild branch
  returning a contract at 0/5 waypoints.

### WON'T — explicit non-goals

- **Continuous sampling** of gimbal / control-surface deflection / steering / suspension. A 10-50 Hz
  float stream per surface per part across every simultaneous ghost is exactly what the design
  principle forbids. Synthesize (S3) or leave it.
- **Every persistent-but-invisible configuration flag** — thrust limiter, the 11 `ModuleRCS`
  actuation flags, docking `crossfeed`/`targetAngle`, Control From Here, SAS mode, target selection,
  hibernation, stage lock, `flowState`/`flowMode`. All `visibleToBystander: none`, all restored
  verbatim by the quicksave. *Caveat worth recording:* reaction-wheel authority and crossfeed **are**
  genuine re-fly trajectory inputs, so the verdict rests on the restore model, not on "they don't
  matter". If the restore model is ever revised, revisit this row.
- **Per-part science module state**, `RouteCargo*` tombstone eligibility, ore depletion, biome
  unlock — all benign per §0.
- **Playback cases for `Docked`/`Undocked`** — no visual exists. Fix their identity fields as
  metadata hygiene only.
- **Fairing clamshell**, **wheel damage meshes** (reversing the strip reintroduces a fixed bug),
  **light RGB / steerable head** (free rider on M1), **KAL-1000 controller state** (its fidelity is
  the union of M4 + S5).

---

## 6. One untested interaction worth tracing next

`ScienceChanged` is a career-level aggregate that **is** in the seven-facet patch set, while the
`ResearchAndDevelopment` node — including each subject's decayed `scientificValue` — is a `GAME`-node
facet reverted to rewind UT. After a Re-Fly, a surviving branch's post-rewind `ScienceChanged` rows
are re-applied against a subject-value table that has been rolled back. If `PatchScience` recomputes
from subject state rather than replaying deltas, the payouts will not reconcile.

Stated as an untested interaction, not a finding — `PatchScience` was not traced. The boundary
between a restored `GAME`-node scenario and a patched ledger facet that reads from it is where the
remaining rewind bugs will be.

### TRACED 2026-08-11 — the feared mechanism is VERIFIED-SAFE; one bounded secondary defect found

**The mechanism above cannot mis-total, for three independent reasons.**

1. `ScienceChanged` never reaches the ledger at all: `GameStateEventConverter` DROPS it. Ledger
   `ScienceEarning` rows come from `OnScienceRecieved` captures that record `subjectId`, the award
   and `subjectMaxValue` **at earn time** (`GameStateRecorder.cs`), so the earn value was fixed
   before any rewind and is never re-derived.
2. The walk is a fresh delta replay against ledger fields only. `ScienceModule.Reset()` clears all
   subject state on every recalc, and `ProcessEarning` computes
   `effective = min(recordedAward, cap − creditedTotal)` from the action's own fields. No KSP
   singleton is read anywhere in the walk, so a rolled-back subject-value table cannot influence a
   payout.
3. The live subject table is OVERWRITTEN, not consulted. `PatchPerSubjectScience` writes
   `kspSubject.science = CreditedTotal` verbatim and recomputes `scientificValue` from the target
   and the cap; live subjects absent from the surviving ledger are zeroed. The pool patch is a
   separate guarded delta, and both run in the same `PatchAll` at the rewind apply boundary with
   `authoritativeReduction = true`. `LedgerGroundTruth` already pins the pool scalars hard.

**The secondary defect (real, reachable, bounded).** `PatchPerSubjectScience` could not CREATE a
missing live subject: `GetSubjectByID == null` counted `notFound` and skipped. A subject first
earned AFTER the rewind point by a branch that SURVIVES the merge is absent from the
quicksave-restored R&D table while the surviving ledger still credits it, so the re-assert skipped
it. The Science Archive then under-reported that subject permanently, and a re-run of the
experiment paid FULL value from a fresh zero-science subject while the ledger clamped the new row
to the remaining headroom — leaving running < live, which the next non-authoritative recalc's
`ApplyDrawdownGuard` resolves by clamping the pool UP to live. The over-award was permanent, bounded
per subject by `scienceCap`.

Fixed 2026-08-11: `ResolveMissingSubjectCreation` (pure) plus a reflection insert into
`ResearchAndDevelopment.scienceSubjects`, gated on a POSITIVE ledger target so a legitimately
un-earned subject still stays out of the Archive. Decompile-verified against KSP 1.12.5
`Assembly-CSharp`: the field is `private Dictionary<string, ScienceSubject> scienceSubjects`; the
public ctor `ScienceSubject(id, title, dataScale, subjectValue, scienceCap)` starts at
`science = 0` / `scientificValue = 1`; and the placeholder's cosmetic fields self-heal, because
`GetExperimentSubject` routes through the private `getScienceSubject`, which refreshes
`title`/`scienceCap`/`subjectValue`/`dataScale` from a freshly built subject while PRESERVING the
`science` value we re-asserted. A broken reflection handle degrades to exactly the pre-fix skip,
with a one-shot WARN.

---

## 7. Documentation defects found in passing

- `docs/dev/done/next-parts-event-support-priority.md:43-47` claims five families are "now recorded
  and showcased"; all five are dead probes on stock parts.
- ~~`docs/dev/done/parsek-rewind-separation-design.md:593` step 4 ("leave alone") is contradicted
  by the shipped scrub.~~ **Resolved 2026-08-09 by changing the code, not the doc** — step 4 was
  right and the two production overrides were the divergence. The design doc needs no edit.
- ~~The rewind design doc §7.13 still claims v1 never un-completes a contract; `PatchContracts` can
  remove a tombstoned finished row.~~ **Resolved 2026-08-11 by correcting the doc.** §7.13 and the
  matching stale "v1 does NOT un-fail" sentence in §7.14 now describe shipped behavior:
  `ContractAccept`/`Complete`/`Fail`/`Cancel` are all tombstone-eligible, and `PatchContracts`
  removes the tombstoned finished row and reinstates the contract as Active from its snapshot.
