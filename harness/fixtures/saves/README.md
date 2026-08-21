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
| Row A | `5f2c1b84-93ae-4d07-b6c1-0e8a4d51f3b9`, `ut = 5`, `seq = 1`, `deadlineUT = 9201600` | THE CONTROL. Four orders of magnitude past this flight's ~350 s span, so A stays ACTIVE from load to commit. Its `Accept:` line is what says the sidecar loaded at all |
| Row B | `c47d0a91-6b25-4e83-9f1a-2d60be3c7845`, `ut = 6`, `seq = 2`, `deadlineUT = 100` | THE EXPERIMENT, and the number is sized against TWO clocks. `PrePass` takes `nowUT` from the last surviving action's UT: on the COLD-LOAD walk that is B's own 6, so B loads ACTIVE alongside A (`activeSlots=2/2`); by the COMMIT-time walk the flight has written rows out to ~348, so `nowUT` passes 100 and the injection fires. Below 6 and B is retired before it is ever active; above ~348 and it never fires - and the second failure mode looks green |
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
   unelapsed `deadlineUT` (`ContractsModule.PrePass` injects a synthetic `ContractFail`
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
