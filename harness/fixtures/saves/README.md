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

Shared by B10 + the four career L1 scripts (hire / dismiss / research / upgrade).

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
