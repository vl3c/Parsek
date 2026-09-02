# Career ledger fixtures (M-B3)

> **Craft files:** a craft flown by two or more of these fixtures is committed once
> in `../ships/` and overlaid into the staged save's `Ships/VAB/` per
> `../shared-ships.toml`. Add a manifest row rather than copying a `.craft` into a
> fixture; `SharedShipsManifestTests` reds on a re-introduced copy. See
> `harness/README.md` -> "Fixture saves and the shared craft library".

File-constructed, synthetic, pure-stock save templates for the L1 ledger-accuracy
scripts and B10. Built headlessly (no KSP launch) by trimming the cleanest existing
CAREER dev save down to a deterministic clean-slate KSC. Source template: the dev
install `test career` save (KSP 1.12.5), reset per the M-B3 operator checklist in
`docs/dev/todo-and-known-bugs.md`.

Each fixture is `persistent.sfs` + `persistent.loadmeta` + `AddOns/DistantObject/Settings.cfg`.
No craft in flight, no active/offered contracts, no completed milestones, no unlocked
tech beyond the mode default `start` node, all facilities at level 0, no Parsek footprint
(no `Parsek/` dir, no `ParsekScenario` SCENARIO node, no `ParsekSettings` custom-param),
default career difficulty multipliers (all 1.0). Third-party mod SCENARIO nodes
(`Trajectories`, `Tracking_Persistence`) stripped; `start` tech node parts reduced to a
pure base-stock set (ReStock / Making-History parts dropped).

## fresh-career (GAME Mode = CAREER)

Shared by B10 + the four career L1 scripts (hire / dismiss / research / upgrade) +
`R7c-rewind-spacecenter`, and the derivation base for `career-pad-craft` (and, below
that, `career-science-pad` / `career-earned-pad`, and below `career-science-pad` again,
`career-contract-pad`) and for `strategy-career`.

**Its `rep = 0` is load-bearing DOWNSTREAM, not just here.** KSP's granular reputation
curve is state-dependent, and `CL-2-pod-impact-ledger` - which flies `career-pad-craft`,
derived from this save - pins KSP's OWN post-curve digits as an EXACT-DIGIT logContract
regex: `Added -9.999828 (-10) reputation: 'VesselLoss'`, plus
`Added 0.9999995 (1) reputation: 'Progression'`. Measured against `oracle.apply_rep_curve`
as of PR #1508's residual-step port - which now reproduces that Progression pin to all
seven printed digits at rep 0, so it is a calibrated instrument rather than an analogy -
a -10 nominal lands at -9.9996061 from rep 0 against -10.0001546 from rep 25, a 5.5e-4
shift and ~500x the last printed digit. Note which surface binds: the oracle's reputation
FACET would NOT red (5.5e-4 sits far inside its 0.1 tolerance); the exact-digit regex is
what would have to be re-flown. A consumer that needs a nonzero reputation therefore gets a SIBLING
(see `strategy-career`), never a seed here; `FreshCareerStaysUnseededTests` in
`harness/lib/test_strategy_career_fixture.py` is what makes that a red rather than an
argument.

| Facet | Pinned value | Read by |
| --- | --- | --- |
| Funding funds | `500000` | `CareerSaveParser` seed (hasFunds) |
| ResearchAndDevelopment sci | `100` | seed (hasScience) |
| Reputation rep | `0` | seed (hasRep) |
| Facilities (all 10) | `lvl = 0` (level 0) | facility facet |
| Roster (hired crew) | EXACTLY Jebediah / Bill / Bob / Valentina Kerman (4 Crew, 0 assigned, 0 Parsek reservations/stand-ins) | hire cost curve input |
| Applicant (hire target) | `Verhat Kerman` (Engineer) | `L1-hire-kerbal-career` step arg |
| Dismissable kerbal | `Bill Kerman` | `L1-dismiss-kerbal-career` step arg |

Author constants declared in the specs (assert `expected == save` on the touched pool).
THIS TABLE IS THE LEDGER for the specs' `VERIFY-PENDING-OPERATOR` markers - a spec
claiming outstanding operator work and a row here reading **VERIFIED** cannot both be
right. On 2026-07-31 TWO specs were in exactly that state (`L1-research-node-career`,
and `-science` riding its constant). The other two stale tags failed the other way:
this table AGREED with them and both were wrong together (hire, upgrade-facility), which
is worse - a ledger that agrees with a stale spec proves nothing. Keep the two in step:

| Spec | Constant | Status |
| --- | --- | --- |
| `L1-research-node-career` | `basicRocketry` science = `-5.0` | **VERIFIED**: the source save's `basicRocketry` Tech node carried `cost = 5` (stock 1.12.5 tech data). |
| `L1-hire-kerbal-career` | hire funds = `-62113.0` at hired-count 4 | **VERIFIED** 2026-07-31 off run `2026-07-23_1952`: the GameVariables recruit-cost curve measured -62113, NOT the -24000 guessed here. The spec was corrected on the live run (`L1-hire-kerbal-career.toml`, "LIVE-CONFIRMED (2026-07-23)"); this row was not, and stayed wrong for eight days. |
| `L1-upgrade-facility-career` | Tracking Station level 0->1 funds = `-150000.0` | **VERIFIED** 2026-07-31: the first live run's ledger math passed at exactly -150,000 with hardDivergences=0 (that run red'd only on a since-fixed `FacilityUpgraded` logContract); green at `2026-07-23_1955`. |
| `L1-dismiss-kerbal-career` | all pools = `0.0` | pool-neutral (stock does not refund a hire). |

Budget check: `500000 - 150000 (upgrade) - 62113 (hire) = 287887 >= 0`. (Was written
against the guessed 24000 hire cost; still comfortably positive at the measured one.)

## career-pad-craft (GAME Mode = CAREER, 1 VESSEL)

The first CAREER save in the repo carrying a flyable craft (roadmap item R11). Used by
`CL-1-pod-impact`, the crew-loss atom, and available to any later scenario whose subject
is a career-ledger consequence of a FLIGHT rather than of a KSC button.

Built BY CONSTRUCTION, headlessly, by `harness/tools/build_career_pad_craft.py` - no
forge flight and no operator session. `python harness/tools/build_career_pad_craft.py
--check` re-verifies every post-condition against the COMMITTED bytes, so a reviewer can
confirm the fixture is what the recipe produces without regenerating it. That path
is WIRED, not decorative: `FixtureDriftTests` in
`harness/missions/lib/test_cl1_crew_loss.py` runs the same verify in-process AND
re-runs the splice over the current inputs, asserting byte-identity with the
committed save - so a change to `fresh-career` or `b1-pad-craft` reds in the suite
instead of drifting silently into a live flight.

Exactly three edits against `fresh-career`'s save, plus a `persistent.loadmeta`
restamp (vessel count + UT; every other loadmeta field is unchanged because the
splice moves no pool):

1. `fresh-career`'s empty `FLIGHTSTATE` replaced by `b1-pad-craft`'s, with every
   non-`Ship` `VESSEL` dropped (that removes one `type = SpaceObject` asteroid, which
   `fresh-career`'s own `DiscoverableObjects` scenario never registered and which would
   otherwise be a free variable in any consumer's `expectations.recordings.count`).
   `activeVessel` re-indexed onto the surviving ship.
2. The `ROSTER` row for `Jebediah Kerman` replaced by `b1-pad-craft`'s, so it is the
   `state = Assigned` row KSP itself wrote alongside this exact vessel.
3. The donor's INERT `SCENARIO{name=ParsekScenario}` node (7 lines: `scene`,
   `missionHideArchived`, `gameStateEventCount`; no recordings, no trees, no ledger
   state) copied in. **This is load-bearing and it cost a flight to learn.**
   `fresh-career` deliberately carries no Parsek footprint, which is right for the
   four L1 scripts because they enter through the seam's SPACECENTER route, where
   `LoadGameImpl` writes `persistent.sfs` after `UpdateScenarioModules`
   (autotest-status known-gate 6). The FLIGHT focus route does not. CL-1 flight 1
   (2026-07-28) flew the whole profile correctly and produced ZERO recordings: not
   one `[Scenario]` line in the collected KSP.log, and no `ParsekScenario` node in
   the produced save, so `OnSave` never ran. Every fixture that has ever flown
   carries this node; the only ones without it are the three never-flown `fresh-*`
   KSC templates. A POPULATED node is also what suppresses `PreParsekBackup` at
   load, so an emptied one would be a different and untested shape.

The craft is copied VERBATIM - same `pid`, same `persistentId`, same parts, same
`stg = 2`, same `automateSafeDeploy = 0` on the chute - so `b1-pad-craft`'s MEASURED
flight profile applies to it byte for byte.

| Facet | Pinned value | Why it matters |
| --- | --- | --- |
| Mode | `CAREER` | a SANDBOX death cannot exercise the ledger at all. NOTE: `CL-1-pod-impact` asserts nothing about the ledger today (its commit is unreachable - see the spec header); career still matters here because `MissingCrewsRespawn` decides the kerbal's terminal roster reading, and because the run MEASURES the career-milestone terms the ledger extension needs |
| Funding / RnD / Reputation | `500000` / `100` / `0` | inherited from `fresh-career`; the ledger-oracle seed |
| Facilities (all 10) | `lvl = 0` | inherited |
| Vessel | stock "Jumping Flea" (mk1pod.v2 + parachuteSingle + 2x GooExperiment + solidBooster.sm.v2 + fins), `sit = PRELAUNCH` on the LaunchPad | `stg = 2`, booster at `istg = 1` and chute at `istg = 0`, so ONE stage activation ignites the booster and leaves the chute stowed |
| Crew | `Jebediah Kerman`, `state = Assigned`, aboard the pod | `type = Crew` is load-bearing: kRPC `GetKerbal` scans `CrewRoster.Crew` |
| `MissingCrewsRespawn` | `False` (inherited) | with it off the kerbal settles at `Dead`; with it on stock walks `Dead -> Missing`. CL-1 pins the FIRST hop for that reason |
| Parsek footprint | none | keeps the analyzer's Forbid gate clean, and leaves `KerbalsModule.IsManaged` no reservation to claim (a claimed kerbal's `CrewStatusChanged` emit is SUPPRESSED) |

No tech-tree edit is needed: a persisted `VESSEL` node loads regardless of unlock state,
and every part on this craft is in the stock `start` node anyway.

The spec-to-fixture pairing (crew name aboard, Assigned, career, one vessel, respawn off,
no Parsek footprint) is gated by `SpecFixtureSyncTests` in
`harness/missions/lib/test_cl1_crew_loss.py` - nothing else checks it, and getting it
wrong costs a live flight to discover.

## career-science-pad (GAME Mode = CAREER, 1 VESSEL)

`career-pad-craft` with a DIRECT antenna and the ElectricCharge to spend on it. Used by
`L3-career-science-recover`, the post-fix career forge - the first scenario in the suite
that makes a career EARN rather than SPEND.

**WHY IT EXISTS, and it cost two flights to learn.** kRPC filters transmitters by
`IScienceDataTransmitter.CanTransmit()`, and stock `ModuleDataTransmitter.CanTransmit()`
returns false for `antennaType == INTERNAL` BEFORE it consults CommNet at all. The
Jumping Flea aboard `career-pad-craft` carries exactly one transmitter - `mk1pod.v2`'s
built-in INTERNAL antenna - so **stock forbids transmitting science from that craft**,
and no re-fly changes it. `L3-career-science-recover` run `2026-08-19_1823` flew a
textbook mission (peak apoapsis 19,990 m, `LANDED`, all three experiments run) and still
terminated `transmit-credited-no-science`. That is the fixture-fault class
`design-autotest-mission-library.md` Amendment A describes, so the answer is a SIBLING
fixture, not another flight.

Built BY CONSTRUCTION, headlessly, by `harness/tools/build_career_science_pad.py` - no
forge flight and no operator session. `python harness/tools/build_career_science_pad.py
--check` re-verifies every post-condition against the COMMITTED bytes. That path is
WIRED, not decorative: `CareerSciencePadFixtureDriftTests` in
`harness/missions/lib/test_science_bench_recover.py` runs the same verify in-process AND
re-runs the splice over the current `career-pad-craft`, asserting byte-identity with the
committed save - so a move anywhere up the derivation chain (`fresh-career` ->
`b1-pad-craft` -> `career-pad-craft` -> here) reds in the suite instead of drifting
silently into a live flight.

**Exactly one edit** against `career-pad-craft`'s save: three additive `PART` nodes,
surface-attached to the pod and APPENDED after the existing eight, plus the `Title`
restamp. The `persistent.loadmeta` is unchanged because the splice moves neither the
vessel count nor the UT.

1. `SurfAntenna` (Communotron 16-S). The whole point: its cfg declares
   `antennaType = DIRECT`. 0.015 t.
2. `batteryPack` (Z-100, 100 EC).
3. `batteryPack` (Z-100, 100 EC).

**The batteries are not padding.** Stock charges `packetResourceCost` per `packetSize`
Mits, and both values come off the ANTENNA rather than the experiment - through a
`SurfAntenna` (2 Mits / 12 EC) the three experiments aboard cost 156 EC to transmit:
5 packets x 12 EC per Mystery Goo (10 Mits) twice, plus 3 packets x 12 EC for the crew
report (5 Mits). `mk1pod.v2` carries 50, and the 2026-08-19 flight measured that 50 as
UNSPENT at touchdown, so 50 is genuinely what a transmit would have had to spend - enough
for the crew report alone and for neither Goo. Two Z-100s take the craft to 250 EC, a
94 EC margin over the worst case. `transmitMinScienceGain` only needs ONE subject to
credit, so the margin is defense in depth rather than a prediction.

**Hand-authoring a PART node is the failure mode the automation-first fixture rule exists
to avoid, and this is not it.** `CAREER-FORGE-NEEDS-A-DIRECT-ANTENNA` named three
concrete hazards and the splice answers each mechanically rather than by care:
a fresh `persistentId` (assigned as fixed literals and asserted unique across the vessel,
along with `uid`); the `srfN` / `attN` strings (`srfN = srfAttach, 0` with `attm = 1`,
the shape the two Mystery Goos on this same pod already carry, and every parent /
surface-attach index is range-checked); and the `stg` renumber (**not needed** - every
spliced part is `istg = -1`, and appending after the last existing part means no existing
index moves, so no `parent`, `srfN`, `attN` or `sym` reference in the save is disturbed).
The pose is DERIVED, not typed: both position and rotation are the -x Mystery Goo's
measured pair carried through a rigid yaw about the pod's +Y axis, so the parts land on
free azimuths of a ring KSP itself authored. That made the FORGE route - author a
`.craft`, add a `FORGE-*` spec, fly it, harvest it - two flights of scaffolding for
something a byte-identity gate proves for free.

| Facet | Pinned value | Why it matters |
| --- | --- | --- |
| Mode | `CAREER` | inherited; a non-career save has no pools for the forge to move |
| Funding / RnD / Reputation | `500000` / `100` / `0` | inherited unchanged; the ledger-oracle seed |
| Facilities (all 10) | `lvl = 0` | inherited |
| Vessel | the base's Jumping Flea **byte for byte** (parts 0-7), plus `SurfAntenna` + 2x `batteryPack` at indices 8-10 | the byte-identity of the first eight is asserted, so B1's MEASURED flight profile still transfers and the six specs flying the base are untouched |
| Antenna | `SurfAntenna`, `antennaType = DIRECT` (part cfg) | the ONLY reason this fixture exists. `antennaType` lives in the cfg and never in the save, so the part NAME is the assertion |
| ElectricCharge | 250 (50 pod + 2x100) | 156 EC is what the three experiments cost to transmit; gated, not commented |
| Staging | `stg = 2` unchanged, every spliced part `istg = -1` | the chute-arming logic was measured against B1's stage list; an accidentally-staged part would silently edit it |
| Mass added | +0.025 t (0.94%) | MEASURED consequence is DRAG, not weight: peak apoapsis 15,599.8 m on run `2026-08-19_1912` against 19,990 m for the bare craft, a 22% loss. Both inside the flight leg's 6,000-30,000 m window; size any tightening against 15,600 |
| Crew | `Jebediah Kerman`, `state = Assigned`, aboard the pod | inherited; `type = Crew` is load-bearing for kRPC `GetKerbal` |
| Parsek footprint | none (inert `ParsekScenario` node only) | keeps the analyzer's Forbid gate clean; measured `RED=0` |

Both spliced part names are already in this save's purchased-parts set (the ProbesBeforeCrew
tree relocation puts `SurfAntenna` and `batteryPack` in the `start` node), and `verify`
asserts that rather than trusting it - a persisted `VESSEL` loads regardless of unlock
state, so a career fixture could otherwise quietly fly a part its career cannot build.

The spec-to-fixture pairing (career, one vessel, antenna aboard, inert ParsekScenario
node) is gated by `CareerSciencePadSpecFixtureSyncTests` in
`harness/missions/lib/test_science_bench_recover.py`.

## career-contract-pad (GAME Mode = CAREER, 1 VESSEL)

`career-science-pad` with its `Title` restamped and ONE new sidecar. Used by
`L5-career-contract-complete`, the first committed scenario that drives Parsek's
contract state machine past ACCEPT.

**WHY IT EXISTS.** `ContractsModule` has four transitions plus `PrePass`'s
synthetic-fail injection, and only `ProcessAccept` had a gate: `career-earned-pad`
carries a fixture-spliced `type = 5` row, and its builder's TRAP 3 explains why that
fixture must NOT also carry a terminal row. So the terminal side had to come from a
different fixture - and, to be worth flying, from the CODE rather than from another
spliced row.

**THE WHOLE FIXTURE IS THE SIDECAR.** The save differs from the donor on ONE line (the
`Title`), asserted so, which is what lets `L5` reuse every one of `L3`'s flight-leg
parameters and pools without re-measuring them. `Parsek/GameState/ledger.pgld` carries
exactly TWO `type = 5` rows and no terminal row of any kind.

| Facet | Pinned value | Why it matters |
|---|---|---|
| Row A | `5f2c1b84-93ae-4d07-b6c1-0e8a4d51f3b9`, `ut = 5`, `seq = 1`, `deadlineAbsUT = 9201600` | THE CONTROL. Four orders of magnitude past this flight's ~350 s span, so A stays ACTIVE from load to commit. Its `Accept:` line is what says the sidecar loaded at all |
| Row B | `c47d0a91-6b25-4e83-9f1a-2d60be3c7845`, `ut = 6`, `seq = 2`, `deadlineAbsUT = 100` | THE EXPERIMENT, and the number is sized against TWO clocks. `PrePass` takes `nowUT` from the last surviving action's UT: on the COLD-LOAD walk that is B's own 6, so B loads ACTIVE alongside A (`activeSlots=2/2`); by the COMMIT-time walk the flight has written rows out to ~348, so `nowUT` passes 100 and the injection fires. Below 6 and B is retired before it is ever active; above ~348 and it never fires - and the second failure mode looks green |
| Terminal rows | NONE (`type = 6` and `type = 7` both absent, asserted) | THE INVERTED TRAP 3. A fixture-carried fail would make every token pass without `ContractsModule.PrePass` ever running |
| `advanceFunds` | 0 on BOTH rows | TRAP 1: `FundsModule.ProcessContractAccept` credits an advance unconditionally, so a nonzero one moves the funds pool off 500000 before the flight starts |
| Penalty pack | `fundsPenalty = 9000` on both; `repPenalty = 4` on A, `1` on B | The funds figure is a real generated PartTest's `values[4]` off `375b4446-...` in `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`. B's reputation figure is deliberately NOT that contract's 4: this career ends the flight at reputation 2, and a 4-point debit would drive the pool NEGATIVE - a state stock supports but no committed run has exercised, which would put an unrelated first on the same flight |
| Accept UTs | 5 and 6, both below the save's `UT = 9.0599999999998957` | `Ledger.Reconcile` prunes any contract-lifecycle row whose UT exceeds the save clock on cold load. A row authored in the future simply vanishes and the flight logs nothing |
| `recordingId` | absent on both | a contract is accepted at Mission Control, not inside a flight, and a tag naming a recording the save does not hold is pruned by the same reconcile |
| Stock `CONTRACTS` | EMPTY, asserted | the fixture makes NO claim about KSP-side contract state - see below |
| Sibling sidecars | none (`events.pgse` / `milestones.pgsm` / `baseline_*.pgsb` absent) | each loader logs a benign "starting fresh" line. The visible consequence is `PatchContracts: no snapshot for contractId=... - skipping` per ledger-active contract, which is expected and inert |
| Craft / pools | the donor's, byte-identical | funds 500000, science 100, reputation 0, and B1's Jumping Flea with `L3`'s measured apoapsis window |

**THE COMPLETE SIDE IS NOT HERE, AND THAT IS A MEASUREMENT.** The first build of this
fixture spliced a `state = Active` `PartTest` CONTRACT into the save so the mission's
launch staging would complete it live, with the whole mechanism derived from the
decompiled `Assembly-CSharp` first. Run `2026-08-20_2217_L5-career-contract-complete`
flew it MISSION-OK with every verifier green and the completion never fired: the
contract was gone from `ContractSystem` before the mission's first frame, and stock
re-OFFERED a fresh contract with the identical subject 8 s later. The cause is
upstream of contracts entirely - the spliced `Progress { FirstLaunch }` node did not
restore, so `PartTest.MeetRequirements()` read false and `Contract.Update()` retired
the contract on its first tick. Filed with the full evidence as
`SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE` in `docs/dev/todo-and-known-bugs.md`.
Until that is explained, a save-authored Active `PartTest` cannot be made to survive
in this lineage, which is why this fixture's every claim lives in the ledger.

Built BY CONSTRUCTION by `harness/tools/build_career_contract_pad.py`; `--check`
re-verifies every post-condition against the COMMITTED bytes and is WIRED through
`CareerContractPadFixtureDriftTests` and `CareerContractPadDeadlineArithmeticTests`
in `harness/lib/test_career_contract_pad.py`.

## career-earned-pad (GAME Mode = CAREER, 1 VESSEL)

The suite's only fixture that is BOTH a career with populated per-identity facets AND
focusable into the FLIGHT scene. Used by `L4-ledger-groundtruth-strict`, the
career-ledger B.4 strict-per-identity lane.

**WHY IT EXISTS.** The in-game `LedgerGroundTruth` cell is `Scene = GameScenes.FLIGHT`,
and strict mode promotes every report-only per-identity divergence to a hard failure -
so arming it needs a subject whose per-identity facets are actually populated. Exactly
one committed career has them: the save harness run
`2026-08-19_2130_L3-career-science-recover` produced, a driven flight that earned three
science subjects, transmitted them, and RECOVERED a crewed craft. That save carries
**zero VESSEL nodes**, precisely because the craft was recovered - so booting it routes
`LoadGame` to NoVesselSpaceCenter and a FLIGHT-scene batch scene-skips its only
declaration, the vacuity defect B10 and `L1-passive-sandbox` were re-flown to fix.
Splicing a PRELAUNCH craft into a career that has none is the same problem
`build_career_pad_craft.py` solved for `fresh-career`, with the halves reversed: there
the career was empty and the craft was the payload; **here the career IS the payload**.

Built BY CONSTRUCTION, headlessly, by `harness/tools/build_career_earned_pad.py` - no
forge flight and no operator session. `python harness/tools/build_career_earned_pad.py
--check` re-verifies every post-condition against the COMMITTED bytes. That path is
WIRED, not decorative: `CareerEarnedPadFixtureDriftTests` in
`harness/lib/test_career_earned_pad.py` runs the same verify in-process AND re-runs the
splice over the current inputs, asserting byte-identity with the committed save. Its
BASE is the xUnit fixture `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`, so a
re-harvest of that career reds here rather than leaving a strict-armed flight measuring
a subject nobody meant to ship.

**Five edits** against the harvested career save, plus one against the copied
`Parsek/GameState/ledger.pgld`:

1. `career-science-pad`'s single `type = Ship` VESSEL, inserted into the base's OWN
   FLIGHTSTATE. The base's node is KEPT rather than replaced - the one deliberate
   departure from `build_career_pad_craft.py`'s whole-node swap - because it carries
   `UT = 408.72`, the clock all 14 ledger actions were written against; the donor's is
   `9.06`, i.e. before every one of them.
2. The spliced vessel's `pid` and `persistentId` are RE-STAMPED. **This is the
   load-bearing edit, not hygiene.** The donor craft IS the craft this career flew and
   recovered, so its committed identity (`f77e4207...` / `2905720181`) is byte-identical
   to both recordings' `recordedVesselGuid` / `vesselPersistentId`, and
   `LedgerGroundTruthDiff.CompareRecovery` treats a recovery credit whose vessel is still
   PRESENT in the save as a divergence - ALWAYS-HARD when guid-corroborated, report-only
   when pid-only, and strict promotes the report-only one anyway. A verbatim splice would
   have red the armed run on a fixture artifact that reads exactly like a product defect.
3. The `rewindSave = parsek_rw_*` hints are stripped and `Parsek/Saves/` is not copied,
   the two halves `CommittedFixtureRewindSaveTests` pins together.
4. Jebediah's ROSTER `state` is flipped `Available` -> `Assigned`, and **nothing else in
   his row is touched**. The base builder swaps the whole row for the donor's; that here
   would delete his `CAREER_LOG` (`flight = 1`, `Land,Kerbin`, `Flight,Kerbin`,
   `Recover`) - the SAVE side of the diff's `KerbalXp` facet. The reconstruction credits
   those entries off the ledger, so losing them would manufacture a `PhantomInRecon`
   divergence: report-only by default, promoted by strict, and a fixture artifact again.
5. **One `Offered` `CONTRACT` row is re-stated `Active`, and a matching `type = 5`
   (`ContractAccept`) `GAME_ACTION` row is appended to the copied ledger.** Added
   2026-08-20; together they are the D8 `contracts` cell. Before them the career's seven
   contracts were all `Offered`, which is the worst of the three states for the gate:
   enough to keep `LedgerGroundTruthDiff.CompareContracts` from taking its
   "save has no contract facet -> skip" exit, so the facet always counted toward the
   pinned `facetsCompared=10`, and not enough to make it compare anything - both sides
   read 0. **It is fixture-carried because a driven accept is unreachable**, not merely
   expensive: the M-A2 seam's `KscAction` has four kinds and none of them is "accept a
   contract", and `Contract.Accept()` is a UI-only entry point Parsek itself
   Harmony-blocks. It is faithful anyway - the hand-played c2 career's own ledger carries
   a `PartTest` accept whose row has no `recordingId` and originates at KSC, and this row
   is that one's shape field for field. **Three arithmetic traps** are honored and
   asserted by `verify_ledger`: `advanceFunds = 0` (a positive advance is credited
   unconditionally by `FundsModule` and would move the HARD-gated funds pool), an
   unelapsed `deadlineAbsUT` (`ContractsModule.PrePass` injects a synthetic `ContractFail`
   at an elapsed deadline, emptying the active set AND applying two penalties), and NO
   `type = 6` completion row (it would free the slot and re-vacuify the compare). WHICH
   contract is load-bearing too: `19e7ba6c...` tests `liquidEngine2.v2` at
   `sit = LANDED`, and the pad craft carries neither, so it cannot resolve mid-batch.

| Facet | Pinned value | Why it matters |
| --- | --- | --- |
| Mode | `CAREER` | the cell is career-only and Skips otherwise |
| Funding / RnD / Reputation | `536558` / `111.599998` / `1.99999881` | the EARNED pools, not the seed. A fixture reading 500000 / 100 / 0 would mean the splice dropped the career it exists to carry |
| Vessel | one `type = Ship` Jumping Flea, `sit = PRELAUNCH`, `activeVessel = 0` | the FLIGHT route, and nothing else. It is the key, not the subject |
| Vessel identity | `pid` / `persistentId` re-stamped, asserted to collide with no recorded identity | see edit 2 - the difference between a real red and a manufactured one |
| FLIGHTSTATE UT | `408.72`, asserted greater than every `explicitEndUT` | the career's own clock survived the splice |
| Parsek footprint | 1 committed tree, 2 recordings, `ledger.pgld` with 15 actions | THE PAYLOAD. The tree carries no `isActive` key, so the cell's no-active-uncommitted-tree guard passes. 14 of the 15 rows are the harvest's; the 15th is the appended `ContractAccept` (edit 5) |
| Contracts | 7 `CONTRACT` nodes, EXACTLY ONE of them `Active` (`19e7ba6c...`), with a matching `type = 5` ledger row | see edit 5. Live-measured `ParseContracts: total=7 active=1` -> `CompareContracts: reconActive=1 saveActive=1 phantoms=0 mismatches=0 missing=0` on run `2026-08-20_1644` |
| Crew | `Jebediah Kerman`, `state = Assigned`, `CAREER_LOG` intact | see edit 4 |
| Trajectory mirrors | `.prec.txt` committed, `_vessel.craft.txt` / `_ghost.craft.txt` absent | the harness gates the two mirror families in OPPOSITE directions; carrying exactly one family is what lets this fixture be a plain copy of the base tree |

The spec-to-fixture pairing is gated by `L4SpecFixtureSyncTests` in the same file, and
the structural counts by `CommittedFixtureSweepTests.RECORDED_FIXTURES` in
`harness/lib/test_saveparse.py`.

## career-same-name-pad (GAME Mode = CAREER, 1 VESSEL, 2 recordings)

The recovery correlator's repro subject: a career that has already flown its pad craft
ONCE - two chained `Jumping Flea` recordings under launch guid `f77e4207...` - and has
banked NOTHING from it, with the same craft back on the pad under a FRESH launch guid.
Used by `L6-career-same-name-recover`.

**WHY IT EXISTS, and it was measured rather than assumed.**
`KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY` stage 2 needs a live recovery where the
correlator's stage-1 launch-guid filter actually has same-name candidates to drop. L6's
first reading run (`2026-09-02_1137`) tried to get that for free by re-flying
`science_bench_recover` over `career-earned-pad`, which already carries those two
recordings - and it flew, landed and collected, but TRANSMIT credited ZERO career
science, because that save is L3's PRODUCED one and its launchpad biome is already
banked to cap. The mission's structural transmit -> recover gate therefore failed BEFORE
recovery, the phase the correlator fires in, and the schema forbids a transmit floor
below 0.001 (`science_bench_recover.schema.toml`), so no param rescues it. **The
banked-science conflict is intrinsic to reusing a produced save.**

**THE SPLIT THAT ANSWERS IT:** take the RECORDINGS from the produced save
(`Source/Parsek.Tests/Fixtures/C2CareerPostFix/`) and the CAREER from the PRE-FLIGHT one
(`career-science-pad`, the save L3 flies). The two halves are two moments of ONE
timeline, which is why those recordings' `preLaunchFunds = 500000` /
`preLaunchScience = 100` are the host's own live pools.

Built BY CONSTRUCTION, headlessly, by `harness/tools/build_career_same_name_pad.py` - no
forge flight and no operator session. `--check` re-verifies every post-condition against
the COMMITTED bytes and is WIRED through `CareerSameNamePadFixtureDriftTests` in
`harness/lib/test_career_same_name_pad.py`, which also re-runs the splice and asserts
byte-identity, so a re-harvest of `C2CareerPostFix` reds here.

**Five edits**, all against the HOST save text:

1. `C2CareerPostFix`'s `RECORDING_TREE` (both `Jumping Flea` recordings, verbatim - no
   id, UT or point count rewritten) spliced into the host's `ParsekScenario` node, with
   `Parsek/Recordings/` copied alongside it.
2. The `rewindSave = parsek_rw_*` hint stripped, because `Parsek/Saves/` is not copied -
   the two halves `CommittedFixtureRewindSaveTests` pins together.
3. **The load-bearing edit:** the host VESSEL's `pid` re-stamped to `9b3c71e4...`. The
   host craft IS the craft those recordings recorded, so its committed `pid` is
   byte-identical to their `recordedVesselGuid`; left alone, the live launch and both
   recordings read as the SAME launch and the filter would drop nothing.
4. `persistentId` deliberately NOT re-stamped. KSP bakes it into the `.craft` and reuses
   it on every launch, so a genuine relaunch DOES collide on pid while carrying a fresh
   `Vessel.id`. Keeping `2905720181` on both sides is what makes this a repro of the trap
   the todo entry names rather than a case nothing could confuse.
5. The career clock moved to the produced save's own `409.56` (and the vessel's `lct` /
   `lastUT` with it), so both recordings lie wholly in the PAST of a craft that has just
   rolled out. The host's `9.06` would leave committed recordings running into the
   future at load.

**What is deliberately NOT copied:** the produced save's `Parsek/GameState/ledger.pgld`.
Its rows credit the very science this fixture must leave un-banked, and the
recalculation engine patches KSP state from the ledger - splicing it back would rebuild
the blocker. Two recordings with no ledger rows is coherent; a banked pool with an
un-banked ledger is not.

## strategy-career (GAME Mode = CAREER, 0 VESSELS)

`fresh-career` with a reputation seed and nothing else. Used by
`L3-strategy-currency-conversion`, and it exists to close that spec's one pinned
coverage gap.

**WHY IT EXISTS.** `OperationStrategy_RewardMultiplier_IsCapturedOnNominalReason`
(named `..._IsNotCaptured` until 2026-08-21, when the funds gate was reason-qualified
and its Progression uplift turned out to be capture-worthy) drives stock
`LeadershipInitiative`, and on `fresh-career` it self-skipped every run with
`'LeadershipInitiative' cannot be activated on this save: Cannot afford Setup Cost: Not
enough Reputation`, so the matrix's only real hole was its own negative control.

**THE REQUIREMENT, READ FROM STOCK.** `Strategies.cfg`'s `LeadershipInitiative` declares
`initialCostReputationMin = 10.0` / `initialCostReputationMax = 100.0` /
`factorSliderDefault = 0.05`; `Strategy.InitialCostReputation` is
`FactorLerp(min, max)` = `Mathf.Lerp(10, 100, 0.05)` = **14.5**, and
`Strategy.CanBeActivated` compares the CURRENT pool against it (an activation-time
check, not a persisted one). `Strategy.Create` assigns `factorSliderDefault` whenever
`Factor == 0`, which the base's EMPTY `STRATEGIES` node guarantees.

**WHY A SEED AND NOT AN IN-BATCH TOP-UP.** A top-up is a positive reputation ACTION and
there is no positive no-recurve action type, so it would have to survive Parsek's replica
of KSP's granular curve against the reputation guard's 0.01 epsilon - which is why the
spec previously refused to force it. A SEED is not an action: it lands as a
`ReputationInitial` row, which `ReputationModule.ProcessReputationInitial` assigns
directly, no curve. `Activate()` then charges the 14.5 with
`AddReputation(-cost, TransactionReasons.StrategySetup)` (a ledger-modelled reason), and
the cell restores the pool with `SetReputation` (absolute, no curve) in its `finally`.

**WHY A SIBLING AND NOT A SEED ON THE BASE.** See the `fresh-career` section: fourteen
committed specs sit on that save or its derivatives, and two of them pin post-curve
reputation amounts that a nonzero pool would move.

| Facet | Pinned value | Why it matters |
| --- | --- | --- |
| Reputation rep | `25` | the subject. Solved for, not chosen: floor 14.5 (the gate above), ceiling 35.0 (`UnpaidResearchProgramCfg`'s own lerped reputation setup cost, the next stock threshold a rising pool would newly unblock; `AgressiveNegotiations`' `requiredReputationMin` 38.0 is next after that). 25 is the whole number nearest the centre of `[14.5, 35.0)`. The ceiling matters because two cells in this category take whatever `ProbeActivatableStockStrategy` returns - the FIRST activatable entry in `StrategySystem.Instance.Strategies` - so the seed must move the activatable set as little as the requirement allows. `AppreciationCampaignCfg` (funds-only, already activatable at rep 0) still sits ahead of everything the seed unblocks, so the probe's pick does not move |
| loadmeta `reputationPercent` | `2` | `(int)(rep / 10f)`, verbatim what `LoadGameDialog`'s save-info reader computes. Load-menu preview only |
| Funding / RnD | `500000` / `100` | inherited from `fresh-career`; the science seed is what the two science-costing cells top up from |
| VESSEL count | `0` (inherited) | load-bearing: all seven `StrategyLifecycle` declarations are `Scene = SPACECENTER`, and it is the empty `FLIGHTSTATE` that makes `DecideLoadRoute` take the `NoVesselSpaceCenter` route where they are all eligible. One vessel would route to FLIGHT and scene-skip the whole category |
| `STRATEGIES` node | empty (inherited) | every strategy is built fresh from config at `factorSliderDefault`, which is what pins the 14.5. A persisted strategy could carry any `factor` |
| Parsek footprint | none (inherited) | same KSC-route reasoning as the base |

Built BY CONSTRUCTION, headlessly, by `harness/tools/build_strategy_career.py` - no
forge flight and no operator session. **Exactly two lines** of the save differ from
`fresh-career`: the GAME `Title` and the `Reputation` SCENARIO's `rep`. `--check`
re-verifies every post-condition against the COMMITTED bytes, and that path is WIRED:
`StrategyCareerFixtureDriftTests` / `StrategyCareerSeedBandTests` /
`L3SpecStagesTheSeededFixtureTests` in `harness/lib/test_strategy_career_fixture.py`
run the same verify in-process, re-run the splice over the current `fresh-career`
asserting byte-identity, re-derive BOTH band bounds from the stock numbers, and check
the spec still stages this fixture and pins the closed tally.

## fresh-science (GAME Mode = SCIENCE_SANDBOX)

Science pool only: `ResearchAndDevelopment sci = 100`, no Funding / Reputation /
facilities / contracts. `CareerSaveParser` sets hasFunds / hasRep false (oracle
facet-skips them). Stock starting kerbals (Jeb/Bill/Bob/Val), no applicant. Used by
`L1-research-node-science` (research `basicRocketry`, science-only assertion). Depends on
the M-C1 `research-node` SCIENCE_SANDBOX readiness widen (RnDPresent) which has landed.

## fresh-sandbox (GAME Mode = SANDBOX)

No economy pool at all: every `hasX` false. Stock starting kerbals. Used by
`L1-passive-sandbox` (pure B10 passive variant, no KscAction driven).

**KNOWN BLOCKER (not a fixture defect):** a SANDBOX template has no pools by definition,
and `run.py::_capture_seed_baseline` terminal-INVALIDs any `[expectations.ledger]`
scenario whose template parses with all pools absent (`invalid-fixture`). So
`L1-passive-sandbox` stays terminal-INVALID at run time until either (a) the seed-baseline
gate is taught to accept a no-pools template when the manifest is empty (expected == seed
== all-absent, the facet-skip path the spec assumes), or (b) the `[expectations.ledger]`
block is removed from `L1-passive-sandbox` (it then runs as a pure recording-invariants
passivity proof). ~~Left at `pending-fixture` until that is resolved.~~ RESOLVED - it is `tier = "daily"` and live-proven (see the status paragraph below).

## Recorded-state fixtures - where they ARE documented, and two that are here

This file's scope is the FILE-CONSTRUCTED career/fresh templates above. The
RECORDED-state fixtures (harvest `--keep-parsek`, where the committed RECORDING is the
payload) are documented in `harness/lib/test_saveparse.py::RECORDED_FIXTURES` - one
commented block per fixture carrying its provenance, its measured shape and its pins -
plus each one's own `harness/tools/build_*.py` header. That is deliberate rather than an
omission: the pin and the prose sit in the same place, so a re-harvest that moves the
shape reds against the paragraph describing it. Do not restate a recorded fixture's shape
here; a second copy of a moving list is a second thing to leave stale.

FOUR ENTRIES SIT HERE ANYWAY, each for its own reason. `rover-route-recorded`,
`rover-relay-recorded` and `rover-relay-c-recorded` are POINTERS worth keeping, because
the naming rule they rest on can destroy an operator's own save if it is ever forgotten -
and because the two relays' reasons to exist are things no structural facet can express
(one carries ZERO origin proofs, the other TWO that name the WRONG origin).
`rover-route-career` is not a harvest at all - it is file-constructed from two committed
inputs, exactly like everything above this section - so it belongs to this file's scope
even though its payload is recorded. No entry restates a shape `RECORDED_FIXTURES`
already pins.

### rover-route-recorded (GAME Mode = SANDBOX, 3 real vessels + 8 asteroids)

The supply-route lane host (RVR-1 / RVR-2 / RVR-3), landed 2026-08-30. Harvested from a
scratch COPY of the operator's hand-flown `logistics-rover-A` save (collected into
`.claude/worktrees/logs/2026-08-30_1106_rover-route/`), finished by
`harness/tools/build_rover_route_recorded.py`, shape pinned in `RECORDED_FIXTURES` and
wired into the suite by `harness/lib/test_build_rover_route_recorded.py`.

**IT IS NAMED FOR THE LANE AND NEVER FOR THE SOURCE SAVE, and that is a safety rule
rather than a style one.** `run.py::stage_fixture` rmtree's the same-named save inside
the automation instance, so a fixture called `logistics-rover-a` would DELETE the
operator's hand-played save the first time any scenario staged it. Every recorded harvest
follows this; `build_depot_route_recorded.py` states it in the same words.

Two facts about it belong in a human-readable place because they are what make it useful
and what a future re-harvest could silently lose:

- **Its route window is TARGET-branch**, because the two rovers carry DIFFERENT baked
  `persistentId`s. Every other committed route window in the suite is initiator-branch on
  one shared baked id (two Kerbal X descendants), which is why
  `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` and
  `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` sit in H39's and H40's
  MEASURED_SKIPPED rosters as a HARVEST requirement. This is that harvest.
- **It carries no `ROUTES` node and no `mergeState` key anywhere** - i.e. it is the route
  CANDIDATE host (both trees already fully sealed, route not yet created), the mirror
  image of `depot-route-recorded` and not interchangeable with it. That is what gives
  `RVR-2`'s driven `RouteCommand action=create` something to do.
- **Its endpoint inventory was REPAIRED on 2026-09-01, after RVR-2's first flight**, and
  a re-harvest must repeat the repair rather than inherit these bytes. The source save
  was written AFTER the operator had hand-created route `fd6ee2ff` over the same trees
  and driven one Send Once, so the harvested endpoint `rover fuel 0` (pid 2123618197)
  carried that delivery's output: two extra `STOREDPART` nodes and no free inventory
  slot. RVR-2 flight 1 executed its whole driven chain and then blocked at cycle 0
  (`BLOCKED kind=DestinationFull reason=stored-part:evaScienceKit`) instead of
  delivering - a route-creation lane cannot start from a destination the same delivery
  has already filled. The builder strips exactly the two slots the delivery's own
  `Inventory store:` log lines name (`part7/mod1/slot1` evaChute, `part7/mod1/slot2`
  evaScienceKit), leaves the second container verbatim, and rewrites the module's
  `inventory = ` mirror. The 297.6 / 400 LiquidFuel is post-delivery too and is
  deliberately KEPT: 102.4 of headroom against a 97.6 manifest is what makes RVR-2's
  cycle-1-fits / cycle-2-blocks chain reachable in two driven cycles.

All three are asserted by the builder's `--check`, so a re-harvest that lost any of them
reds in `harness/lib` rather than on a flight.

### rover-relay-recorded (GAME Mode = SANDBOX, 3 real vessels + 3 asteroids)

The ZERO-PROOF RELAY host (RVR-5 / RVR-6), landed 2026-09-02. **READ THE 2026-09-03
UPDATE AT THE END OF THIS SECTION FIRST**: the two fail-closed refusals described below
are both CLOSED, and RVR-5 now pins the ADMISSION of this relay rather than its refusal.
The refusal material is kept verbatim because these bytes are unchanged and the two
producer readings are still the only committed record of what the old behaviour was. Harvested from a scratch
COPY of the operator's hand-flown `logistics-rover-B` save (flown 2026-09-02, collected
into the umbrella `logs/2026-09-02_2041/`), finished by
`harness/tools/build_rover_relay_recorded.py`, shape pinned in `RECORDED_FIXTURES` and
wired into the suite by `harness/lib/test_build_rover_relay_recorded.py`.

**IT IS NAMED FOR THE LANE AND NEVER FOR THE SOURCE SAVE**, the same safety rule its
sibling states: `run.py::stage_fixture` rmtree's the same-named save inside the automation
instance, so a fixture called `logistics-rover-b` would DELETE the operator's hand-played
save the first time any scenario staged it.

The flight: three identical 16-part rovers A, B and C on the KSC shore, all LANDED on
Kerbin, each with one `ModuleCommand` and one `dockingPort2` and no grapple. C drove to B,
docked at UT 218.22, loaded +200 LiquidFuel (B 200 -> 0), undocked at UT 276.00, drove
~780 m to A, docked at UT 340.12, unloaded 126.8 LiquidFuel (A 200 -> 326.8), undocked at
UT 402.50 and drove away. Saved at UT 443.64 from the SPACE CENTER with C's recording
stopped. The three rovers sit **336 m apart (A-C), 783 m (A-B) and 983 m (B-C)** - far
outside the ~200 m docking range, so the relay is a genuine drive, and well inside physics
range of each other. **CORRECTED 2026-09-02 BY MEASUREMENT**: this used to conclude "so
any route driven over these bytes would take `path=loaded` rather than the sibling
fixture's `path=unloaded`". Separation does NOT decide the writer path on a DRIVEN lane -
RVR-7's first census read `path=unloaded` on every writer over the OTHER relay fixture,
whose rovers are just as close, because a seam `TimeJump` warps with the endpoints PACKED
and it is the load state at the DISPATCH TICK that decides. The separation still decides
what a PLAYER sees, and it is still what makes this a drive rather than a warp.

Four facts belong in a human-readable place, because they are what make it useful and what
a future re-harvest could silently lose:

- **NO ROUTE WAS EVER CREATED, and there are TWO INDEPENDENT fail-closed reasons.** The
  first is the product working by decision, the second is an open defect, and fixing only
  one still produces no route.
  1. **No `ROUTE_ORIGIN_PROOF`, because neither docked half is a player-typed depot.** The
     producer's own line, once per dock: `RouteOriginProof skipped: no depot half
     recId=1461186781 vessel='rover C' seams=2 candidates=0 isEva=False (neither docked
     half is typed Base or Station, so no supply origin was recorded; set the depot's type
     in the tracking station)`. All three rovers are `vesselType = Rover`. This is the
     standing todo entry ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT, and these are
     the first committed bytes that hold its output. It is a DIFFERENT zero from
     `rover-route-recorded`'s, whose producer skips because both trees start at a KSC site.
  2. **`RouteAnalysisStatus.MixedPickupDelivery` - an unwitnessed inventory gain, and
     unlike reason 1 this one is an OPEN PRODUCT DEFECT.** While docked at hop 1 the player
     moved the **same** `DeployedCentralStation` (and an `evaChute`) out of B's inventory
     into C's, and the station RE-HASHED in transit: it left B as `5072997a...` and arrived
     on C as `5bcde9ad...`. Stock's `ModuleInventoryPart.StoreCargoPartAtSlot(Part, int)`
     rebuilds a live `ProtoPartSnapshot`, so `ModuleGroundExpControl.OnSave` writes a
     runtime-computed `canComm` value the craft-authored `STOREDPART` never had, and
     `ComputeInventoryPayloadIdentityHash` hashes module-level values by design - so a
     value STOCK adds on the way through changes the identity of a part nobody swapped.
     `RouteAnalysisEngine.HasUnwitnessedInventoryGain` pairs gains to losses BY HASH, so
     the gain has nothing to pair with and the window fails closed. The `evaChute` and
     `evaScienceKit` moved in the same window closed cleanly, because their modules write
     nothing computed. Filed as
     LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE (OPEN, needs a design
     call). **THESE BYTES ARE THAT DEFECT'S ONLY COMMITTED SUBJECT and `RVR-5` is its
     regression instrument, so DO NOT "clean up" the fixture** - a re-harvest that moved no
     inventory would retire both silently, which is why the builder pins the whole
     gain/loss walk.
- **Both of its route windows are TARGET-branch with a cross-tree partner**, where
  `rover-route-recorded` holds that property once. Each window's `transferTargetPid` is
  carried by the single recording of its own origin tree, which is why the forest is THREE
  trees and why neither origin tree is spare payload.
- **The resource half of the relay BALANCES on both hops** (+200 / -200 and -126.8 /
  +126.8). A reader of RVR-5's `candidate-ineligible` refusal must not conclude the
  resource bookkeeping failed; it did not, the inventory half did, and only on hop 1.
- **Its `activeVessel` is a BUILDER EDIT.** The source was saved from the SPACE CENTER, so
  KSP left `activeVessel = 0` pointing at `Ast. UYX-230`, a stock asteroid in solar orbit.
  That save BOOTS - `IsLoadedGameFocusable` accepts it - straight into deep space 13.5 Gm
  out with all three rovers unloaded and every live-vessel `Logistics` guard skipping. The
  builder re-points it to index 1, `rover C`, LANDED. **A re-harvest must repeat that
  re-point**, and the harvest's `--expect-situation ORBITING` is armed against the SOURCE
  for exactly this reason (passing `LANDED` would fail the gate on a healthy source).
  The three asteroids are KEPT verbatim, on the sibling's precedent: pruning them would
  move every index the re-point resolves against for no benefit, and no lane reads them.

All four are asserted by the builder's `--check`, so a re-harvest that lost any of them
reds in `harness/lib` rather than on a flight.

**UPDATE 2026-09-03 - BOTH REFUSALS ARE CLOSED AND RVR-5 IS NOW A POSITIVE LANE.** Reason
2 was fixed by PR #1620, which rules stored inventory cargo GENERIC and matches it BY KIND
(part name + variant + per-resource fill bucket; module state ignored entirely), so stock's
transit-added `canComm` no longer touches the key - proven headless over these exact bytes
by `Source/Parsek.Tests/Logistics/InventoryPayloadKindKeyTests.cs`. Reason 1 was closed by
PR #1618, which moved the origin binding to the undock and off the depot type, plus the
analysis-side follow-up that derives the ORIGIN from the PICKUP WINDOW - the vessel the
transport took cargo FROM. Both serve the operator ruling of 2026-09-03: **route candidates
come from the ACTIONS - dock, take fuel or cargo FROM, undock, dock elsewhere, transfer TO,
undock = a valid route.** THE FIXTURE IS UNCHANGED; only what the analysis does with it
moved, and the builder still pins the whole gain/loss walk and both hash constants. Its
role is now the ZERO-PROOF half of a pair with `rover-relay-c-recorded` below.

ONE FURTHER FACT WORTH RECORDING HERE, because the section above states the opposite and
it cost a reading to settle: `RECORDED_FIXTURES` says a route driven over these bytes would
find no live endpoint, since `Part.Undock` re-pidded rovers B and A away from the pids the
windows name. That is true of the PID STEP only. `RouteEndpointResolver` walks RootPart ->
Pid -> SurfaceProximity, and the proximity step is bounded by
`RouteOrchestrator.SurfaceProximityRadiusMeters = 500` against the window's own recorded
`ENDPOINT_AT_DOCK` coordinates, which every one of these landed rovers is within metres of.
Treat "the endpoint cannot resolve" as unverified on both relay fixtures.

### rover-relay-c-recorded (GAME Mode = SANDBOX, 3 real vessels + 6 asteroids)

The WRONG-PROOF RELAY host (`RVR-7-rover-relay-c-dispatch`), landed 2026-09-03. Harvested
from a scratch COPY of the operator's hand-flown `logistics-rover-c` save (flown
2026-09-02, collected into the umbrella `logs/2026-09-03_0026_rover-c/`), finished by
`harness/tools/build_rover_relay_c_recorded.py`, shape pinned in `RECORDED_FIXTURES` and
wired into the suite by `harness/lib/test_build_rover_relay_c_recorded.py`.

**IT IS NAMED FOR THE LANE AND NEVER FOR THE SOURCE SAVE**, the same safety rule both
siblings state: `run.py::stage_fixture` rmtree's the same-named save inside the automation
instance, so a fixture called `logistics-rover-c` would DELETE the operator's hand-played
save the first time any scenario staged it.

The flight: three identical 16-part rovers named A, B and C on the KSC shore, all LANDED on
Kerbin, each with one `probeStackSmall`, two `ConformalStorageUnit` containers of three
slots, and one `dockingPort2`; no grapple. C drove to B, docked at UT 155.82, LOADED +154.4
LiquidFuel (B 200 -> 45.6) and three stored items, undocked at UT 212.54, drove to A, docked
at UT 274.18, UNLOADED 200 LiquidFuel (A 200 -> 400) and four items, undocked at UT 335.32
and drove off. Saved at UT 410.40 from the SPACE CENTER. The three rovers sit **313 m apart
(A-C), 731 m (A-B) and 1041 m (B-C)** - far outside the ~200 m docking range, so the relay is
a genuine drive, and well inside physics range of each other. **MEASURED, and NOT what
the separation predicts**: RVR-7's cycle took `path=unloaded` on every writer, because a
seam `TimeJump` warps with the endpoints PACKED and it is the load state at the DISPATCH
TICK that decides the writer path, not the distance. A player driving the same relay by
hand would see the loaded path.

**WHY IT EXISTS: IT CARRIES TWO PERSISTED `ROUTE_ORIGIN_PROOF` NODES AND BOTH NAME THE
WRONG ORIGIN.** Every other route fixture in the corpus carries ZERO
(`rover-route-recorded` skips on the KSC-site start, `rover-relay-recorded` on the untyped
depot), so these are the only committed bytes on which an analysis can be shown to IGNORE a
bound proof and derive the origin from the pickup window instead. The two lines the
2026-09-02 undock binder wrote, quoted VERBATIM from the source flight's KSP.log (lines
22361 and 25769):

```
RouteOriginProof bound at undock: recording=39ac117a8a8b4d61b1296983e7d538a8
    ut=212.54000000003492 binding=BoundToHalfB recoveredFromStopStamp=0 originHalf=B
    originRoot=3466447829 originName='C' originType=3 originPid=612987736
    guidDecision=Stamped transportRoot=549109006 transportParts=16 pickup=Carried
    pickupValidated=0 pickupDelta=[LiquidFuel=-154.4;inv:-3] startRes=1 undockRes=1
    startInv=3 undockInv=1

RouteOriginProof bound at undock: recording=b9df0ee00fd84831a0d9619b4e34fc97
    ut=335.319999999985 binding=BoundToHalfB recoveredFromStopStamp=0 originHalf=B
    originRoot=701791207 originName='A' originType=3 originPid=4280917262
    guidDecision=Stamped transportRoot=3466447829 transportParts=16 pickup=Carried
    pickupValidated=0 pickupDelta=[LiquidFuel=-200.0;inv:-4] startRes=1 undockRes=1
    startInv=4 undockInv=3
```

Read the two `originName=` values against what actually happened:

- **Hop 1 (dock at B) is the PICKUP** - C took +154.4 LiquidFuel and 3 items out of B, so
  the correct origin is **B**. The binder bound **C**, the TRANSPORT ITSELF, and named B as
  the transport (`transportRoot=549109006` is B's root part). The two halves are exactly
  inverted.
- **Hop 2 (dock at A) is the DELIVERY** - it has no pickup and therefore no origin at all.
  The binder bound **A**, the DESTINATION.

Both say `pickup=Carried pickupValidated=0`: the binder recorded that it never validated
the pickup and simply bound the half it could see at the undock. **DO NOT "REPAIR" THE
PROOFS** - stripping or correcting them turns this fixture into a second, slightly
different copy of `rover-relay-recorded` and retires the only subject the override has. The
builder's `verify_wrong_origin_proofs` reds if either proof ever becomes correct.

Four more facts belong in a human-readable place:

- **THE HOP-1 IDENTITY SWAP, and it is what makes this fixture structurally different
  rather than a re-flight.** When C docked to B, KSP resolved the COMBINED vessel to
  **B's** identity: the dock-member recording `39ac117a` carries B's `persistentId` AND B's
  `recordedVesselGuid`. Three consequences. (a) **Window 0 is INITIATOR-branch** - its
  `transferTargetPid` equals the carrying recording's pid - where BOTH of
  `rover-relay-recorded`'s windows are TARGET-branch; only window 1 is TARGET-branch here,
  so do not carry the sibling's "two target-branch windows" claim across. (b) It is the
  mechanism behind wrong proof 1: `binding=BoundToHalfB` picked C, because half B of that
  seam IS C once the merged vessel took B's name. (c) The relay tree's ROOT `8604fbc7`
  carries no `_vessel.craft`, the same dock-merge-parent shape as the sibling's `31e84302`
  - and **neither operator source carried one, or a `_vessel.craft.txt`, before any harvest
  ran** (checked in both). The sibling passes `CommittedFixtureMirrorTests` only because
  its merged vessel kept C's guid; here the guid correlator cannot see the identical
  situation, so that cell grew a third, dock-merge-parent exemption in the same commit as
  this fixture.
- **THE FLOW DIRECTIONS ARE WHAT THE PROOFS CONTRADICT, and they close in BOTH
  dimensions.** Window 0's transport GAINS (+154.4 LiquidFuel; station, chute and kit each
  +1 by kind) against B losing the same, and window 1's transport LOSES (-200 LiquidFuel;
  -1 / -1 / -2 by kind) against A gaining the same. A window whose transport gains is a
  pickup and its endpoint is the SOURCE; one whose transport loses is a delivery and its
  endpoint is the DESTINATION. That derivation - source B, destination A, ONE route - is
  computed from the bytes by the builder's `verify_flow_directions`, not restated.
- **IT IS STAGED AT START-OF-CYCLE, AND THAT IS A BUILDER EDIT.** As harvested the save
  was written AFTER the relay ran, so the endpoints had already absorbed it: B held
  LiquidFuel 45.6/400 against the window's own 154.4 pickup manifest, and A **400/400 with
  6 of 6 inventory slots occupied**. `RouteOriginCargoCheck.HasRequired` (eligibility step
  6) and `RouteDestinationCapacityCheck.HasCapacityForAllStops` (step 8) are both
  all-or-nothing and both were false, so every driven cycle BLOCKED and emitted nothing. A
  route REPLAYS a recorded run against the CURRENT live endpoints, so the subject a
  dispatch lane wants is the state at the start of the NEXT cycle - and step 3 of the
  builder produces it WITHOUT inventing a number, by restoring each PHYSICAL endpoint to
  the state ITS OWN WINDOW recorded at ITS dock:

  | vessel | restored from | LiquidFuel | inventory |
  |---|---|---|---|
  | B (pid 90564594) | window 0's `DOCK_ENDPOINT_*` | 200 / 400 | station, chute, kit x2 |
  | A (pid 4280917262) | window 1's `DOCK_ENDPOINT_*` | 200 / 400 | station, chute, kit x2 |
  | C (pid 612987736) | **untouched** | 154.4 / 400 | as the relay left it |

  The `STOREDPART` bytes are **lifted verbatim** out of the window snapshots, inner
  `persistentId` included, and only FLIGHTSTATE is touched - no recording, no window, no
  branch point, no origin proof, since the windows are the repair's own input. C is left
  alone because a dispatch never reads the transport's hold: the pickup writer removes
  from the SOURCE and the delivery writer stores into the DESTINATION.
  **The precedent is `build_rover_route_recorded.py` step 3**, which strips the two
  `STOREDPART` nodes a hand-driven Send Once had already delivered into ITS endpoint -
  same class of edit, same reason, and RVR-2 flight 1 is the flight that was lost to not
  having it.
  **WHERE THE PARTS GO WAS THE HARD HALF.** The window records a `slotIndex` but not which
  of the two containers, and rover A holds a station at slot 1 in BOTH - so a
  slot-index-only rule picks the wrong one. The placement table is derived by inner `PART
  persistentId` on A (the window's three pids appear at container 0 slot 0, container 1
  slot 0 and container 1 slot 1), corroborated by what is left on C and by B's own
  surviving kit. The two stations even differ in size, 165 lines against 166: the
  delivered one went through a live `StoreCargoPartAtSlot` and picked up
  `ModuleGroundExpControl.OnSave`'s `canComm`.
  **What the repair does NOT buy is a second cycle.** After cycle 0 the endpoints are
  spent again exactly as the operator left them, which is why RVR-7 drives ONE send-once
  where RVR-2 drives two - and why it can FORBID both hold tokens.
- **Its `activeVessel` is a BUILDER EDIT.** The source was saved from the SPACE CENTER, so
  KSP left `activeVessel = 0` pointing at `Ast. RQL-681`, a stock asteroid in solar orbit.
  That save BOOTS - `IsLoadedGameFocusable` accepts it - straight into deep space with all
  three rovers unloaded and every live-vessel `Logistics` guard skipping. The builder
  re-points it to index 5, rover `C`, LANDED (type `Probe`, not the sibling's `Rover`). **A
  re-harvest must repeat that re-point**, and the harvest's `--expect-situation ORBITING`
  is armed against the SOURCE for exactly this reason - passing `LANDED` would fail the
  gate on a healthy source. The six asteroids are KEPT verbatim, on the siblings'
  precedent. **NEVER PASS `--force` to the harvest**: the situation gate is the only thing
  between a clobbered source and a silently wrong fixture.

All of the above is asserted by the builder's `--check`, so a re-harvest that lost any of
it reds in `harness/lib` rather than on a flight. The repair's own post-conditions are
re-derived from the windows rather than from constants, and one of them exists because the
first draft got it wrong in a way every count still passed: a lifted `STOREDPART` carries a
nested `PART` whose modules write their own `stagingEnabled` at a deeper indent, a
prefix-only depth test anchored on one of those, and the `inventory` CSV was spliced into
the middle of a stored part. The gate is now structural - the CSV must equal the
slot-ascending part names of the container's own `STOREDPART`s, and be absent entirely when
the container is empty - and it was mutation-tested.

### rover-route-career (GAME Mode = CAREER, the SAME 3 real vessels + 8 asteroids)

The costed-dispatch lane host (`RVR-4-rover-route-career-cost`, the roadmap's Tier C
item 9), landed 2026-09-02. **NOT A HARVEST** - which is why it is documented HERE, in
the file-constructed section's own idiom, and not only in `RECORDED_FIXTURES`. It is
`rover-route-recorded` STAMPED INTO CAREER by
`harness/tools/build_rover_route_career.py`, built headlessly from two COMMITTED inputs:
the recorded save supplies the Parsek payload and the world, `fresh-career` supplies the
career SCENARIO nodes. That is `build_strategy_career.py`'s precedent applied in the
other direction, and because both inputs are committed the drift cell can RE-RUN the
build and assert byte-identity - which `RoverRouteRecordedFixtureDriftTests` cannot do,
its own input being a collected operator save outside the repo.

**THE ENTIRE DIFF from its sibling**, and it is worth stating exhaustively because the
lane's whole claim is that nothing else moved:

| Change | Detail |
| --- | --- |
| GAME `Mode` | `SANDBOX` -> `CAREER` |
| GAME `Title` | this fixture's own leaf (the leaf IS the runSaveName `run.py` stages into) |
| ADD, verbatim from `fresh-career` | `Funding`, `ResearchAndDevelopment`, `Reputation`, `ScenarioUpgradeableFacilities`, `StrategySystem`, `ScenarioContractEvents`, `ContractSystem` - inserted at the positions that reproduce the donor's OWN ordering, so the result's scenario list is EXACTLY `fresh-career`'s plus `ParsekScenario` |
| DROP | `ScenarioNewGameIntro` (SANDBOX-only; the donor career save carries none) |
| RESEED | `Funding funds` -> `11000` (see below); loadmeta `gameMode` / `funds` / `science` restamped to match |
| UNCHANGED, asserted BYTE-IDENTICAL | the whole `ParsekScenario` node, the whole `FLIGHTSTATE` (all 11 VESSEL nodes, `activeVessel`), and every file under `Parsek/` - trees, recordings, sidecars, `GameState` |

Facility levels are the donor's, i.e. all ten at `lvl = 0`. Nothing in the lane reads
one: it loads straight into FLIGHT and `TimeJump` moves the clock with
`Planetarium.SetUniversalTime` rather than through TimeWarp.

**THE FUNDS SEED IS 11000, SOLVED - AND FLIGHT 2 SHOWED IT IS THIS LANE'S OWN GATE.**
The dispatch cost is 7410 (7250 of stock parts read off the AUTOMATION instance's own
`ModuleManager.ConfigCache`, where ProbesBeforeCrew prices `dockingPort2` at 600 against
Squad's 280, plus 200 LiquidFuel at unit cost 0.8 from the root's complete launch
manifest) - derived before the flight and measured at 7410.0000023841858 on it. The seed
is sized so the pool affords exactly ONE dispatch and not two: band `[7488.08, 14663.84)`
(floor = the highest reading any costing basis could give, ceiling = twice the lowest),
whole thousand nearest its centre.

**THE PART THAT WAS INITIALLY DERIVED WRONG, kept here because it is a general trap.**
This section first said the funds-short hold was unreachable on this fixture: the
committed `Parsek/GameState/ledger.pgld` carries five `MilestoneAchievement` rows
totalling 18,200 funds, all Effective by `MilestonesModule`'s distinct-id first-hit rule,
and `EnsureInitialFundsSeed` seeds from the LIVE pool (no `FundsInitial` row,
`baseline_0.pgsb` carries `funds = 0`) - so effective funds looked like `seed + 18200`
for any positive seed. **A LEDGER AMOUNT IS NOT A LIVE POOL AMOUNT.**
`KspStatePatcher.PatchFunds` runs its target through `ApplyDrawdownGuard`, and the "keep
what you earned" guard REFUSES an upward patch whose running balance exceeds the live
value, holding the pool at the spent value - measured on the flight as `GUARDED UPLIFT
clamped resource=Funds running=29200 live=11000 clampedTo=11000 - spent value held;
ledger may be missing a spending channel`. So the 18,200 stays ledger-side, the live pool
IS the seed, and the chain runs: cycle 0 is charged 7410 out of 11000, cycle 1 sees 3590
against 7410 and blocks `FundsShort shortfall=3820` - which is exactly `2 * cost - seed`,
the quantity the band was solved to produce. The clamp fires because a file-constructed
career carries award rows with no matching live-pool history; that is a property of this
fixture class, filed report-only in `docs/dev/todo-and-known-bugs.md`, and no token pins
it.

Every claim above is re-derived from the committed bytes by
`RoverRouteCareerFixtureDriftTests` / `RoverRouteCareerSeedBandTests` /
`RoverRouteCareerSpecSyncTests` in `harness/lib/test_build_rover_route_career.py`, which
also re-run the splice over the current inputs asserting byte-identity, compare every
sidecar as bytes, re-derive the 7410 cost and the 3820 shortfall from those bytes, and
fail if the spec ever pins a `DestinationFull` cycle-1 token (which is what the lane
measured only while the dispatch was still free). The structural
counts are pinned in `CommittedFixtureSweepTests.RECORDED_FIXTURES` as a second row
identical to `rover-route-recorded`'s - two rows that must agree, read independently.

## Re-tier

The rule: re-tier from `pending-fixture` to `daily` is the LAST step of a spec's first
green live run, coupling re-tier with confirm-green per the M-B3 checklist and avoiding
the never-run-daily self-quarantine (todo item 4).

STATUS, corrected 2026-07-28 (this section still read "all seven specs stay
`pending-fixture`", which the status doc had already superseded): the seven L1 specs are
DONE and LIVE-PROVEN, all re-tiered to `daily`, and the `L1-passive-sandbox`
seed-baseline blocker described above is resolved. `docs/dev/autotest-status.md` is the
single status authority - see its "Operator items outstanding" item 1. `career-pad-craft`
is LIVE-PROVEN as of 2026-07-28: its consumer `CL-1-pod-impact` flew green on its
second flight and is re-tiered `nightly`. Flight 1 red and found a real defect in this
fixture - the missing `ParsekScenario` node, edit 3 above.
