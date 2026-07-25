# Automated Testing System - Status

Last updated: 2026-07-25 (ORBIT lane REVIEWED by three Opus reviewers and the
findings applied: two blocking liveness/commanded-vs-observed defects in the new
capture tail are fixed, the lane's headline claim is now actually VERIFIED by a
commit-terminal log token instead of only asserted, and all four affected
missions were RE-FLOWN green on attempt 1 - B11 1,270 s, B12 581 s, B5 468 s,
B6 359 s. B7 was flown at HEAD too and does NOT pass, for the pre-existing
300 km-target reason its own row already gated on, not a lane regression.
Details in roadmap item 2. Prior: Mun/Minmus ORBIT lane CLOSED and LIVE-PROVEN: roadmap
item 2 - "capture burn + commit-in-target-orbit terminal" - is implemented AND
flown green as B11-mun-orbit + B12-minmus-orbit. The roadmap's informal "B8" label collided
with the catalog's existing B8/B9/B10 rows, so the lane took B11/B12 and both
docs now carry the mapping. The lane buys ONE Parsek surface no flyby reaches:
a recording that ENDS parked in a foreign SOI and is COMMITTED there. Built by
turning on a new `captureEnabled` param in the LIVE-PROVEN B5 flyby machine -
ascent, transfer, TLI, corrections and the whole warp policy are byte-identical
to the 26 B5 flights, and with the flag off none of the new code is reachable -
plus a four-phase tail: PLAN-CAPTURE (MechJeb circularize-at-periapsis) ->
CAPTURE-BURN (NodeExecutor with autowarp set EXPLICITLY; the done evidence is a
BOUND orbit, since a hyperbolic approach reads a NEGATIVE apoapsis) -> PARK (the
forge_lko held-dwell gate re-pointed at a foreign body: throttle cut, nodes
cleared, SAS+RCS held, rails dropped to 1x for 180 game-s of recorded coverage)
-> ORBIT-COMMIT (the B-DOCK route-1 mid-mission seam CommitTree) ->
ORBIT-COMMITTED. Leaving the target SOI anywhere in the tail is an ASSERT-FAIL,
so B5's free-return cannot green an orbit mission. Every actor-dependent phase
carries a GAME budget AND a distinctly named fast-fail
(capture-executor-no-start, capture under-burn, never-stabilized vs
never-HELD-stable, tree-commit-seam-returned-X). New D1 registry cell
`commit-in-foreign-soi`. 46 new headless tests; all four suites green. BOTH
FLOWN GREEN 2026-07-25: B11 FULL PASS three times (flight 2; flight 3 as the
confirmation the changed TARGET-FLYBY profile owed; flight 4, the count-pin run)
at wall 1,269-1,271 s with capture eccentricity 0.000127 and the flyby warp down
to 27 game-s / 2 commands from 8,213 game-s; B12 FULL PASS on flights 4 and 5 at
wall 580 s with capture eccentricity 0.00026 and a 194,543 game-second coast
flown in 26 wall-s at ratio 7,535 on 3 warp commands. B6-minmus-flyby also
re-flew green (wall 359 s), paying off the
confirmation it owed after the correction-budget regression. Four shared-machine
findings came out of the lane - our 600 s no-start watchdog colliding with
MechJeb's own 600 s pre-ignition hold, a GAME-time correction budget spent by the
aim-then-warp it waits on, the metastable coast warp-thrash behind KSP's NaN
`time_to_soi` under a warp ramp, and warp inherited across the SOI boundary at
10,000x - all four with forensics in todo-and-known-bugs.md. Both count windows
are now PINNED at {8, 8} from measured green runs (B11 flight 4, B12 flight 5),
with the caveat recorded in both specs: the count is COMMIT-BLIND (sidecars are
written for the ACTIVE tree too, and two never-committed runs produced the same
8), so it guards recording TOPOLOGY and the commit is guarded by log tokens
instead.

Prior: B1-pad-hop DE-LISTED from live-proven: its
2026-07-19/20 PASSes proved the flight but its chute never opened - the
recordings carry ZERO Parachute* events - and its DOWN terminal gated on the
machine's own COMMANDED chute latch, so a ~300 m/s terminal-velocity impact was
awarded the "chute-deployed impact" success end for four months. Same
automateSafeDeploy=0 root cause EVA-4 flight 1 hit. FIXED with the same
live-proven technique: arm at the apoapsis crossing, and gate BOTH the DOWN
terminal and a new craftCanopyObserved assertion on the OBSERVED kRPC
ParachuteState; the spec also now requires the ParachuteSemiDeployed /
ParachuteDeployed Part-event tokens, so B1 claims D7 chute-two-phase for the
first time. Budgets DERIVED from EVA-4 flight-2's measurements rather than
guessed. New gate 7 names the general class: commanded-vs-observed assertions
fail OPEN, and B4's chuteDeployed is the known open instance. Its next nightly
IS its re-prove. Prior: EVA-4-atmo-chute LIVE-PROVEN on flight 2 - FULL PASS
attempt 1, all seven verifiers: the craft's canopy OBSERVED Deployed, handoff
at 1,606 m / -23.2 m/s, the kerbal out mid-air, its own chute verified, a
steady -4.5 m/s chuted descent and "down=true situation=LANDED alive=true",
with the mid-flight EVA branch + Atmospheric TrackSections on the kerbal's own
recording. All four operator pins closed: count PINNED 3, the `'kerbalEVA`
part-name token confirmed, the semi-deployed descent rate MEASURED at about
-236 m/s peak (which trimmed descentTimeoutSeconds 480 -> 240), and the kerbal
lands alive. Two post-live in-family hardenings on the same branch: the
EVA-window gate is now K=2 debounced (stock flips ParachuteState to DEPLOYED at
the START of the ~8 s canopy animation, so one glitched frame could have
certified a terminal-velocity EVA), and EvaChuteDeploy's CompleteOk now
requires the RAW per-poll aliveness read so a death inside the 3-poll loss
debounce cannot green out. Prior: EVA-4-atmo-chute FLEW ITS FIRST FLIGHT and
ASSERT-FAILed exactly as designed - fast, self-explaining, no budget burn:
"eva-window-missed: altitude 702m fell below the window floor 800m (vspeed
-295.2m/s, ... craftChute armed)". Root-caused from the measured per-frame
profile + the produced recording + decompiled ModuleParachute: arming the
craft's chute at 2500 m is INERT, not late - the fixture persists
automateSafeDeploy=0 and stock never opens a chute at ~300 m/s in dense air, so
the recording carries ZERO Parachute* events. Re-tuned to arm at the APOAPSIS
crossing, raise the stock full-deploy altitude, and gate the EVA window on the
chute's OBSERVED kRPC state instead of the machine's own "we commanded it"
latch. Prior: EVA-4-atmo-chute lands, NEVER FLOWN: the first
ATMOSPHERIC mid-flight EVA case - new seam verb `EvaChuteDeploy` [the kerbal
personal parachute, driving the same public `ModuleEvaChute.Deploy()` both
stock player paths call] + new mission `eva4_atmo_chute` whose terminal is an
AIRBORNE EVA window rather than a landing, reusing the committed b1-pad-craft
fixture. Claims the previously-unclaimed D7 chute-two-phase cell. Awaits its
first live flight. Prior: FORGE-eva2-lko lands: the FIRST ORBITAL fixture-forge
[mission forge_lko] that stamps the crewed-LKO fixture EVA-2 is waiting on, so
EVA-2-orbital-board's only remaining gate is the operator forge run + harvest.
Prior: H6-route-rewind-timeline LIVE-PROVEN on its first live

Prior: the whole EVA lane (EVA-1/2/3 + both forges) is LIVE-PROVEN; every
recordings-count window is PINNED to its measured topology (EVA-1 4, EVA-2 2,
EVA-3 7, EVA-4 3) because logContracts are presence-only.
run - FULL PASS attempt 1, all seven verifiers green - the route-rewind wave's
last automated acceptance item; EVA-1-pad-flag first flight = EvaExit/EvaBoard/
commit chain green + analyzer clean, three PlantFlag/EvaExit defects found +
fixed across flights 1-3 [live CanPlantFlag() gate read; verified ladder
release; and the seam now waits for the SiteRename popup before answering so
afterFlagPlanted fires + the FlagEvent is captured]). Prior: EVA lane
prep - PR #1345 review follow-ups 1-5 all addressed; crew-by-name + launch_site
plumbing threaded; FORGE-eva3-pad forge spec + harvest path land; EVA
batch-autorun evaluated = NOT wired; 2026-07-23 M-C2 EVA verbs + EVA-1/2/3 specs;
B-DOCK dock/transfer/undock lane + fixture-forge; all headless-green. This file
is the single at-a-glance answer
to "what is done, what is proven, what is gated" for the automated testing
initiative, so nobody has to re-derive status from code.

## Purpose - never forget it

This system exists for exactly one reason: MAKING PARSEK BETTER. Every
mission, verb, and scenario is an instrument for verifying Parsek's behavior
- that recordings are correct, complete, and schema-clean; that the ledger
reproduces career state exactly; that rewind/re-fly, playback, ghosts, and
routes survive real flight histories. Flying rockets is never the point: a
mission earns its place only by the Parsek recording/ledger/rewind surface it
exercises. The end goal is the L-track Ledger Accuracy Campaign (grand oracle
career runs with repeated rewinds, oracle-diffed at every session boundary).
When prioritizing work, ask "what Parsek defect class does this catch?" -
that question has already paid: the initiative's first real catches include
the INV2 double-cover recorder seam defect and the S0.5 orphan-sidecar leak.

## Doc map (no duplicate documentation)

Each fact about this system lives in exactly one place:

| Doc | Owns |
|---|---|
| THIS FILE | Status: what is shipped, proven, gated; roadmap order |
| `automated-testing-plan.md` | Strategy + rationale (why the system is shaped this way; L-track definition) |
| `automated-testing-scenario-catalog.md` | The INTENDED universe: dimension registry D1-D18 vocabulary, scenario blocks, tiers, regression rotation |
| `design-autotest-*.md` (12 docs) | Per-module design authority (how each module works; binding contracts) |
| `harness/README.md` | Harness module mechanics: ownership boundary, how to run, submodule readiness |
| `todo-and-known-bugs.md` | Finding forensics: the full evidence trail behind every live finding |
| `harness/coverage/registry.toml` | The machine-readable coverage denominator (authoritative cell list) |

If a status statement appears anywhere else, it is a pointer to this file or
it is wrong. MAINTENANCE RULE: any PR that changes a module's status,
live-proves a scenario, adds a test case, or opens/closes a gate updates this
file in the same PR (same discipline as CHANGELOG).

## One-paragraph summary

The system flies KSP missions unattended (kRPC + MechJeb autopilot, or the
Parsek file-drop command seam), records them with Parsek, and verifies the
result through a seven-verifier chain (driver validity, in-game test batch,
offline recording analyzer, log validation, results schema, anomaly sweep,
expectations). Fourteen test cases are live-proven green end-to-end, including
Mun/Minmus/Duna flybys with a certified no-1x-coast warp profile and the
Mun/Minmus ORBIT pair. (B1-pad-hop was de-listed from live-proven on 2026-07-25:
its PASSes proved the flight but its chute never opened, and its terminal could
not tell the difference. See its row below and gate 7. Its PASS in the
2026-07-25 full sweep does NOT re-prove it - that sweep predates the merge of
the canopy-gated terminal, so it ran the old contract.) All
infrastructure modules are shipped and merged. The FIRST two-vessel lane
(B-DOCK: dock/transfer/undock, the logistics-route recording entry point) is
IMPLEMENTED and headless-green, pending a headless fixture-forge run + its
first flight. The Mun/Minmus ORBIT lane (B11/B12: capture burn, park, and a
commit while parked in a FOREIGN SOI) is LIVE-PROVEN on both axes as of
2026-07-25. Coverage stands at 70 of 239 registry cells claimed by at least one
scenario, of which 12 have a green run behind them. The 70 is recomputed from
`hlib.compute_coverage` over the committed specs + registry, not carried
forward: the "52" this sentence used to print had drifted across many spec
additions (it predates the EVA, B-DOCK and ORBIT lanes), so the ORBIT lane's
`commit-in-foreign-soi` cell is only the newest of the additions it was missing.
`harness/coverage/coverage.{json,txt}` are generated + gitignored, so re-derive
rather than trusting a number in prose. Breadth (EVA, orbit, landing, docking,
career-ledger lanes) is the frontier.

## Infrastructure modules (all SHIPPED and merged)

| Module | What it gives Parsek testing | Status |
|---|---|---|
| M-A1 offline analyzer | Recording invariants (INV1-INV9) over any save, RED gate, per-save findings baseline | SHIPPED (#1300/#1302/#1306); AnalyzerVersion 3; core in Parsek.dll so in-game H5 runs the same rules |
| M-A2 command seam | Drives Parsek actions kRPC cannot (record/commit/discard, rewind, dialogs, KSC actions, EVA) | SHIPPED (#1301); 18 implemented verbs, 11 reserved (M-C1 + M-C2 grew the table) |
| M-A3 autorun hooks | Unattended in-game test batches (PARSEK_AUTORUN_*) | SHIPPED (#1305) |
| M-A5 harness core | The orchestrator: admission, staging, seam driving, budget kill, verifier chain, verdicts, coverage/flake ledgers | SHIPPED (#1307, #1316); UNMET-mission tail skip added 2026-07-25 (per-verb `SEAM_VERB_TAIL_ROLE`: after an unmet mission only `cleanup` verbs are driven, so an EVA-4-class world-mutating tail can no longer fire over a flight that never reached its envelope) |
| M-A6 provisioner | Reproducible pinned KSP instance (kRPC 0.5.4 + MechJeb 2.15.1 + KRPC.MechJeb 0.8.1 + built TestingTools) | SHIPPED (#1303/#1308/#1318) |
| M-B1 mission library | Pure mission state machines + kRPC runner (flights become deterministic, diagnosable instruments) | SHIPPED (#1313); hardened by the flyby campaign |
| M-B2 ledger oracle | Seam-declared action manifests -> expected career totals -> save diff (PARSEK-FAIL(ledger)) | SHIPPED (#1314); stock-award-pattern gate below |
| M-B3 ledger scripts | The L1 scenario six-pack | SHIPPED (#1324); LIVE-PROVEN 2026-07-23 (career fixtures file-constructed headlessly; 7/7 ledger scenarios green, now daily tier) |
| M-C1 seam verbs batch 1 | InvokeRewind, AnswerMergeDialog, TimeJump, KscAction, SaveGame | SHIPPED (#1320/#1325) |
| M-C2 EVA verbs + missions | EvaExit/EvaBoard/PlantFlag -> crew/EVA/flag recording coverage | LIVE-PROVEN 2026-07-24; 18 implemented verbs, 11 reserved; verbs + pure deciders + hlib companions + EVA-1/2/3 specs land, both fixtures forged headlessly, all three scenarios flown green, live-prove list P1-P6 closed |
| EVA-4 atmospheric chute | EvaChuteDeploy (the kerbal personal parachute) + mission `eva4_atmo_chute` -> mid-flight atmospheric EVA branch, kerbal-owned atmospheric TrackSections, two-phase chute part events ON the kerbal, kerbal DOWN-alive terminal | LIVE-PROVEN 2026-07-24 (flight 2 full PASS); 19 implemented verbs, 11 reserved; all four first-flight pins closed (count 3, kerbalEVA token, semi-deployed rate measured -> descent budget trimmed 480 -> 240, kerbal lands alive), plus the K=2 window debounce + raw-alive CompleteOk conjunct hardenings |

## Test cases (all 30 committed scenarios)

LIVE-PROVEN = at least one fully-unattended PASS with every verifier green.
The "Parsek surface verified" column is the reason the case exists.

### Live-proven (14)

| Test case | Tier | Parsek surface verified | Coverage cells |
|---|---|---|---|
| H6-route-rewind-timeline | daily | Route-rewind lifecycle rows, dormant classify + Tick materialize, kept-route reconciliation (Restore(cutoff) reconciliation-bundle path) | D9 reconciliation-bundle; D10 route-x-rewind; D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-24: first live run = FULL PASS attempt 1, all seven verifiers green, in-game batch perCategory=1 - the route-rewind wave's last automated acceptance item |
| B2-lko-ascent | nightly | Ascent-to-orbit recording, orbital checkpoints, 6-booster parent-anchored debris children model | D1; D3 orbital-checkpoint; D4 atmospheric/exo-propulsive; D14 kerbin |
| B4-reentry-splashdown | nightly | Full-cycle recording (ascent/deorbit/reentry/splashdown intact), exo-ballistic sections, rails-warp recording | D1; D3; D4 +exo-ballistic; D14 kerbin/warp-rails |
| B5-mun-flyby | nightly | Cross-SOI cohesive coast recording (Kerbin->Mun->Kerbin), on-rails checkpoints across warp, warp-reseed seams | D1; D3; D4 +cohesive-cross-body-coast; D14 kerbin/mun/warp-rails. NO-1X CERTIFIED at HEAD config (flight 26: wall 465 s, warp audit exit 0) |
| B6-minmus-flyby | nightly | Same cells on the minmus axis | As B5 with D14 minmus. GATE: 20 km course-correct target predates finding 16d; guarded (arrival gate + impact terminal fail clean); re-target ~150 km only if it reds. **CONFIRMATION RE-FLY PAID 2026-07-25 (wall 359 s, all seven verifiers green):** B6's prior live proof predated the no-1x-coast aim-then-warp (4219832b6) and B6 shares the machine, the correction params AND the 4,000 s `transferBurnTimeoutSeconds` that B12 flight 1 proved cannot cover a Minmus-class correction node's 73,733 s wait - B6 was exposed and had simply not re-flown. It is ALSO the mission most exposed to B12 flight 2's coast warp-thrash (same long Minmus coast). Both shared-machine fixes landed with the B12 forensics and the confirmation flight flew them green on the flyby side, so B6's LIVE-PROVEN mark is honest at HEAD again |
| B7-duna-flyby | nightly | Multi-SOI interplanetary recording (Kerbin->Sun->Duna->Sun), 100,000x warp recording, SOI-count | As B5 with D14 duna/soi-count/warp-high. **GATE CLOSED 2026-07-25 - B7 AT HEAD IS INTERMITTENT.** The gate ("HEAD's 300 km target has not itself flown; the pass flew 50 km") was paid by flying it. Run 1: both attempts `MISSION-ASSERT-FAIL body='Ike' (expected 'Duna' or exit 'Sun')`, wall 747 s / 735 s. Run 2 (the first full sweep): attempt 1 INVALID on the same Ike capture, attempt 2 PASS, terminal PASS at `attempts=2 wallTotal=1564s`. So it is a FLAKY scenario, not a hard red - the approach always transits Ike's shell, but whether Ike is close enough to capture depends on its phase at arrival. The 300 km target was hit correctly (`pe=310089`); the inbound approach transits Ike's orbital shell and Ike captured the craft (`window[19] body=Duna alt=3,687,346` -> `window[20] body=Ike alt=897,085`). NOT an ORBIT-lane regression: `corrBudgetAnchorUt=none` (the correction re-anchor never engaged, so it ran main's bound) and `phaseWarpIssues=1` (the coast latch worked, which is why it reached Duna at all - every archived pre-fix B7 run stopped at `Kerbin to Sun` and never reached the target SOI). Needs a B7 SPEC decision - accept an Ike encounter as a legitimate Duna-system arrival, or aim to clear Ike's shell - deliberately not taken in the ORBIT lane. Forensics in `todo-and-known-bugs.md` |
| S0.5-live-record-discard | daily | Live record start/stop marker pairing + DiscardTree returns the store to zero (caught the orphan-sidecar leak) | D1 discard-rollback; D5 single-node; D14 |
| S0.6-live-record-commit | daily | Commit on top of the injected corpus without corpus loss (the save-hollowing guard class) | D5; D14; D16 sidecar-prec |
| S1.4-injected-playback | daily | 272-tree corpus injection, load, ghost map presence + polyline render with no anomalies | D6 basic-playback/ghost-map-presence/non-orbital-polyline; D16 sidecar-prec/sidecar-pcrf |
| H5-invariants-corpus | daily | The full synthetic corpus (306 recordings / 276 trees) loads intact and holds every recording invariant in-game | D14 sandbox/scene-flight; D16 sidecar-prec/schema-gate |
| B11-mun-orbit | nightly | COMMIT-IN-FOREIGN-SOI (the new D1 cell): a recording that ENDS parked in another body's SOI and is COMMITTED there - the commit path, the terminal classification, and the background-recording handoff for a tree whose terminal state is "in orbit around the Mun". Every other lunar/interplanetary case (B5/B6/B7) flies THROUGH an SOI and comes back or continues, so none of them reaches this surface. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `captureEnabled` param - ascent, transfer, TLI, corrections, warp policy all byte-identical to the 26 B5 flights; NEW is the four-phase tail PLAN-CAPTURE (MechJeb circularize-at-periapsis) -> CAPTURE-BURN (NodeExecutor, autowarp EXPLICIT; done evidence is a BOUND orbit, since a hyperbolic approach reads a NEGATIVE apoapsis) -> PARK (throttle cut, nodes cleared, SAS+RCS held, rails dropped to 1x, 180 game-s held dwell) -> ORBIT-COMMIT (the B-DOCK route-1 mid-mission seam CommitTree) -> ORBIT-COMMITTED. Leaving the target SOI anywhere in that tail is an ASSERT-FAIL, so B5's free-return cannot green this mission | D1 auto-record-launch + commit-in-foreign-soi (the NEW cell); D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/mun/warp-rails (D5 bg-recording was CLAIMED and then REMOVED 2026-07-25: `CommitTreeFlight` nulls both `backgroundRecorder` and `PhysicsFramePatch.BackgroundRecorderInstance` before returning, no assertion or token covers a handoff, and `settle_frames = 0` ends the mission on the commit frame - BDOCK-1 is the honest claimant of that cell). LIVE-PROVEN 2026-07-25 (flight 2 FULL PASS attempt 1, all seven verifiers green, wall 1,268 s, analyzer red=0): capture apoapsis flipped -1,560,099 -> +138,789 m at eccentricity 0.000127, all six assertions met, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED. Its coast issued the native warp ONCE with ZERO cancels, which is why the Mun lane never showed B12 flight 2's coast warp-thrash; the shared fix for that landed after this pass and does not alter any frame this flight took (a coast that never cancels a valid warp is unaffected). CONFIRMATION RE-FLY PAID (flight 3, 2026-07-25): B12 flight 3's periapsis-bound fix CHANGED this mission's flown profile - TARGET-FLYBY now warps to periapsis_ut - 900 instead of riding the rails flyby stair, and the COAST handoff stops the inherited warp - so a LIVE-PROVEN mission owed one flight on the changed profile. It flew FULL PASS again: wall 1,269 s, all six assertions met, capture eccentricity 0.000127 (flight 2 read the same number, so the profile change did not move the capture quality), and TARGET-FLYBY collapsed from 8,213 game seconds to 27 game seconds on 2 warp commands. The lane's re-fly debt is now clear on both axes. FLIGHT 4 (2026-07-25) FULL PASS, wall 1,271 s (`results/2026-07-25_0400_B11-mun-orbit.json`): the COUNT-PIN run - the first B11 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}. **FLIGHT 5 (2026-07-25) FULL PASS attempt 1, wall 1,270.195 s - the POST-REVIEW re-fly, and the first flight on which this mission's headline claim is actually VERIFIED.** Owed because the review pass added a `time_to_periapsis > 0` conjunct to the capture arming gate (a frame-level change to a path the green flights took) and because the new commit-terminal token needed its first live proof. Both landed: the token reads `terminalState=Orbiting terminalOrbitBody=Mun` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Mun` (the committed craft), 6 `Destroyed` (the radial boosters), 1 `Orbiting Kerbin` (the flameout-staged ascent core), matching the count-pin derivation exactly. A regression that dropped the Mun-parked recording while adding a spurious one would still read 8 but would change these lines. Flight 1 (2026-07-24) flaked at CAPTURE-BURN on our own no-start watchdog colliding with MechJeb's 600 s pre-ignition WARPALIGN hold; fixed with an OBSERVED NodeExecutor.Enabled channel + a node-clock no-start classifier (forensics in todo-and-known-bugs.md) |
| B12-minmus-orbit | nightly | Same cells on the minmus axis (a thin alias over the same capture-enabled machine, exactly as B6 is to B5) | D1 auto-record-launch + commit-in-foreign-soi; D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/minmus/warp-rails (D5 bg-recording removed 2026-07-25 for the same reason as B11 - nothing gates it). LIVE-PROVEN 2026-07-25 (flight 4 FULL PASS, all six mission assertions met, wall 580 s; flight 5 repeated it at wall 580 s and was the COUNT-PIN run - the first B12 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}, `results/2026-07-25_0349_B12-minmus-orbit.json`; **FLIGHT 6 FULL PASS attempt 1, wall 580.826 s - the POST-REVIEW re-fly that first VERIFIES the lane's headline claim**: the new commit-terminal token reads `terminalState=Orbiting terminalOrbitBody=Minmus` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Minmus`, 6 `Destroyed` boosters, 1 `Orbiting Kerbin` core, matching the count-pin derivation exactly. Owed because the review added a `time_to_periapsis > 0` arming conjunct that changes a frame the green flights took. NOTE the flight-6 lesson: its FIRST attempt red'd PARSEK-FAIL on the new token and looked exactly like "the commit records the wrong terminal body", but the flight had run the PREVIOUS DLL - harness flights use the provisioned `automation/stock-minimal` instance, and `dotnet build` only deploys to the dev instance. Run `provision.py --profile stock-minimal` after any C# change; see the note in `.claude/CLAUDE.md`): capture eccentricity 0.00026, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED, and the two shared-machine warp fixes this mission's own forensics produced both held - COAST-TO-TARGET flew 194,543 game seconds in 26 wall seconds (ratio 7,535) on 3 warp commands, and the periapsis-bounded TARGET-FLYBY armed the capture on the orbit's clock instead of blowing through it. At 580 s wall it is the CHEAPEST of the two orbit cases (B11 costs 1,269 s), which makes the Minmus axis the better default for a fast regression check of the shared capture machine. PRIOR FLIGHTS (the forensics that produced three of the lane's four findings): FLIGHT 3 FLOWN 2026-07-25: the coast fix WORKED (COAST-TO-TARGET 26 wall / 194,704 game = ratio 7,543 on 3 warp commands, down from never-finishing) and the run reached a capture burn. THIRD SHARED-machine defect found, named at a glance by the new warpUtilisation block: TARGET-FLYBY read 2 wall / 8,213 game (ratio 5,341, 2 commands) and blew straight through periapsis - entered at ut 268,934.5 / alt 1,902 km descending at -236 m/s, PLAN-CAPTURE at ut 277,147.5 / alt 41,609 m CLIMBING at +92 m/s. Two compounding causes: the COAST -> TARGET-FLYBY handoff emitted NO warp cleanup so the craft crossed the SOI still running the coast's RAILSx10000 warp (the first flyby poll alone advanced 3,907 game seconds), and capture mode fell through to a rails flyby stair floored at flybyWarpFactor whose distance term knows nothing about the periapsis CLOCK. The late burn produced a bound but wildly eccentric 325 x 5.3 km orbit grazing Minmus, CORRECTLY rejected by the capture window as an under-burn. FIXED: `mlib.capture_flyby_warp_target` - the only legitimate warp target inside the target SOI is `periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS` (900, covering our arming + plan, MechJeb's halfBurnTime ignition lead and its 600 s pre-ignition hold), read from `Orbit.TimeToPeriapsis` via the opt-in `read_periapsis`; past the bound or with the clock unreadable the machine does NOT warp (fail closed, 1x); and the handoff now stops the inherited coast warp. FLIGHT 2: the correction fix WORKED (both rounds cleared, `rounds=2`, round 1's 73,720 game-second aim-warp ran as ONE continuous warp from ut 475.3 to 74,195.7 with no cancel), then INVALID `mission-budget-expired` inside COAST-TO-TARGET at ut 225,990 with 41,655 game seconds still to go. SECOND SHARED-machine defect found: the coast derives its native warp target from `time_to_soi` EVERY poll, and KSP cannot read the patched-conic SOI time while re-patching under a warp ramp - measured over flight 2's COAST frames, tts was finite on 2,451 of 2,451 unwarped frames and NaN on 1,154 of 1,161 warping ones - so the machine cancelled its own warp on the blind read and re-armed on the next unwarped poll: 3,603 `warp_to_ut` issues, 3,602 `cancel_warp`, a rails rate that never escaped ~2.7x, ~40 game-s per wall-s. METASTABLE, which is why it had never been seen: B11 flight 2's Mun coast issued the warp ONCE (0 cancels, 30/30 warping frames finite) and locked in at RAILSx1000. FIXED: `mlib.coast_native_warp_hold` - the target is an ABSOLUTE UT, so a blind read UNDER WARP HOLDS the armed command; only a blind read with the game NOT warping is evidence the encounter is gone. Plus a NAMED `coast-warp-thrash` fast-fail past `MAX_PHASE_WARP_ISSUES` (500; a healthy coast issues 1) and a per-phase `warpUtilisation` block in the mission result whose `gameSecondsPerWallSecond` names this class in one line. Budget RE-DERIVED bottom-up from flight 2's measured spans (~2,280 nominal / ~3,100 worst) and deliberately NOT raised - 4200/4700 stand. FLIGHT 1 (2026-07-25): MISSION-FLAKE at CORRECTION-BURN (wall 286 s), UPSTREAM of the capture tail and in the SHARED B5/B6 machine, not in anything B12-specific. ROOT CAUSE: the no-1x-coast PR (4219832b6) turned the DIY correction burner into AIM-THEN-WARP (aim, then natively warp to `node_ut - nodeArrivalMarginSeconds`, then throttle) but left the phase bounded by `transferBurnTimeoutSeconds` - a GAME-time budget that the warp itself spends. Measured: B11/Mun needs 2,994 s of its 4,000 s budget for that wait (75%, passes), B12/Minmus needs 73,733 s (entry ut 475.3, MechJeb node at ut 74,208.3) and can NEVER pass. FIXED in the shared machine: `mlib.correction_budget_expired` suppresses the budget while an aim-warp is in flight and re-anchors it at the warp ARRIVAL (the same seam that already re-anchors the no-start clock - and a game-time bound cannot bound a STALLED warp anyway, which advances no game time; the runner's warp-stall watchdog + the WALL budget own that), and `mlib.classify_correction_timeout` NAMES the expiry (`correction-burner-no-start` / `correction-burn-incomplete`) so it can never again ride the generic timeout. Every correction round give-up now also carries a `corrGiveup` reason on the machine-diff line. B11 flight 2 passed on this same machine, so no budget number needed changing. Also inherits B11 flight 1's CAPTURE-BURN fix unchanged and `read_node_executor` is on; wall budgets 4200/4700 (raised from 3600/4100 for MechJeb's MEASURED ~600 s pre-ignition hold). Same PROVISIONAL pins; the Minmus-specific one is `captureBurnTimeoutSeconds` 200000 (its SOI edge is ~2,187 km up but arrival speeds are ~5x lower than the Mun's, so the executor's SOI-entry -> periapsis autowarp coast is ~10-20 game hours, not 1-3) |
| B13-mun-landing | nightly | LANDED-ON-ANOTHER-BODY (the new D1 `commit-landed-foreign-body` cell): a recording that ENDS on Mun soil and is COMMITTED there - the `Landed` terminal classification for a foreign-body tree, SURFACE-class TrackSections OFF Kerbin (the environment classifier's AIRLESS `Approach -> Surface*` path, which cannot occur where an atmosphere classifies first - hence the previously unclaimed D4 `surface-stationary`), the landing-leg part events, and the landed-vessel ghost / playback surface. B11/B12 end in ORBIT around a foreign body and B1/B4 land on KERBIN, so nothing else in the suite reaches this end state. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `landingEnabled` param on top of `captureEnabled` - PRELAUNCH through PARK byte-identical to the five B11 flights; NEW is the tail DESCENT (MechJeb `LandUntargeted`; warp-PASSIVE because MechJeb's landing states own the warp through the shared `Core.Node.Autowarp` flag the runner sets explicitly; `DeployGears` true, `DeployChutes` FALSE because the Mun is airless, `RcsAdjustment` false because the stage has no thruster blocks) -> LANDED-SETTLE (throttle cut, autopilot released, SAS held, rails 1x, a held settled dwell gated on target body + landed situation + BOTH speed components) -> SURFACE-COMMIT (the same route-1 mid-mission seam CommitTree) -> SURFACE-COMMITTED. FOUR named DESCENT give-ups on top of the budget: `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off `LandingAutopilot.Enabled`, with a REQUIRED touchdown carve-out - MechJeb disables its own module on the landed frame, so without it a PERFECT landing would fast-fail as a dead autopilot), `landing-no-progress` (+ a separate `altitude-unreadable` name), `landing-touchdown-timeout`, `landing-vessel-lost` (a crash reads as neither a timeout nor a success) | LIVE-PROVEN 2026-07-25: FULL PASS attempt 1, wall 2,747.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Mun`, airless `Approach -> SurfaceStationary`, measured count 8 (window PINNED). Most expensive scenario in the suite at 2,825 s harness wall |
| B14-minmus-landing | nightly | Same cells on the minmus axis (a thin alias over the same landing-enabled machine, exactly as B12 is to B11). NOT redundant for a LANDING: Minmus's ~0.05 g against the Mun's ~0.17 g makes MechJeb's descent-speed policy fly a long slow low-thrust settle instead of a short suicide burn, and the flats make an untargeted landing far more likely to end on level ground | LIVE-PROVEN 2026-07-25: FULL PASS attempt 1, wall 2,083.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Minmus`, airless `Approach -> SurfaceMobile`, touchdown -0.25 m/s vertical / 0.06 m/s horizontal, measured count 8 (window PINNED). The CHEAPER landing axis, so the better default regression check of the shared landing tail |

### Committed, not yet live-run (14)

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| B1-pad-hop | nightly | Auto-record-on-launch, atmospheric TrackSections, and a genuinely CHUTE-BORNE ground-arrival recording: the two-phase ParachuteSemiDeployed -> ParachuteDeployed part events on the craft's own parachuteSingle (D7 chute-two-phase, claimed 2026-07-25) | DE-LISTED from live-proven 2026-07-25. The 2026-07-19/20 PASSes proved the FLIGHT, not the CHUTE: their recordings carry ZERO Parachute* part events, and the DOWN terminal - which gated only on the machine's own COMMANDED chute latch - awarded a ~300 m/s terminal-velocity impact the "chute-deployed impact" success end. Root cause (decompiled ModuleParachute + two flights of evidence, forensics in todo-and-known-bugs.md): the fixture's parachuteSingle persists `automateSafeDeploy = 0` (open only while SAFE) and stock DeploySafe never reads SAFE at terminal velocity in dense air, so an ALTITUDE-triggered arm sat inert in ARMED forever. FIXED: arm at the apoapsis crossing while still slow (the technique EVA-4 flight 2 live-proved on this exact fixture and craft), and gate BOTH the DOWN terminal and the new `craftCanopyObserved` assertion on the OBSERVED kRPC ParachuteState. Its next nightly run IS its re-prove; three things it pins are P1 the final full-canopy leg to the ground, the one segment EVA-4 never times because it always hands off in mid-air (budgets DERIVED from EVA-4 flight-2 measurements rather than guessed: descent 240 -> 360 s, mission 600 -> 900 s, wall 900 -> 1320 s; the first draft's 600 s descent assumed a ~30 m/s semi-deployed crawl and was wrong by ~8x - the semi-deployed craft sinks at up to -236 m/s, and chuteFullDeployAltMeters was raised 1000 -> 2500 to match EVA-4's live-proven value because the full canopy needs 894 m just to brake), P2 which end it reaches - computed touchdown is ~8-9 m/s and the parts' own crashTolerance values predict fins and booster destroyed with the pod intact, so expect LANDED with debris, DOWN accepted as the fallback, P3 the recordings count on the chuted profile |
| BDOCK-1-station-interceptor | nightly | FIRST two-vessel flight (18-phase machine): cross-tree Dock branch, authoritative onVesselsUndocking split, RouteConnectionWindow recorded-delta contract (the new `Route window delta:` line), same-craft-twice launch identity. Flight-1/2 wall budgets re-timed; flight-3 lesson (STATION-SEPARATE / INT-SEPARATE) + flight-4 lesson (two-step SEPARATE: drop the spent lifter AND ignite the orbital engine, thrust-verified, cap 2) both live-confirmed through RENDEZVOUS on flight 5; flight-5 lesson (MATCH-VELOCITY kill-rel-vel retargeted XFromNow ~15 s lead + bounded 600 s give-up + per-frame diagnostics + one-shot dropped-target re-acquire); flight-8 lesson (prox-ops rule: abort the pending kill-rel-vel node executor at DOCK entry before the docking AP owns the ship, else it rails-warps + packs the port target null + NREs); flight-9 lesson (core.target one-Update sync trap: stagger the docking-AP enable one poll after the port target); flight-10/11 lesson (prox-ops observability [angular_velocity/sas/rcs/docking_ap_status + per-frame DOCK diag line] + attitude hold [SAS+RCS after each separation and at DOCK entry] + LIVENESS watchdogs [budgets bound SLOW, watchdogs bound BROKEN: DOCK enable-never-took / died-mid-approach / no-progress fast flakes, TRANSFER stall fast flake, bounded dropped-target re-arm x3]). flight-13 ROOT CAUSE (behind every dock failure since flight 7): pre-`launch_vessel`-reload PART handles are stale - the reload destroys every Part, so the captured docking-port handle resolves to a destroyed part and assigning it silently CLEARS the target; VESSEL handles survive (P9 answered). Fix: resolve port + docking-state + transfer tanks LIVE at call time. Flight 13's liveness layer fast-flaked in 10 s with the named E1a reason (wall 2133 s) and pinned this. Flight 16 (2026-07-24): MISSION-OK END TO END (launch, separate, mid-mission commit seam, launch_vessel, rendezvous, hard dock, LF 40 + mono 15 transfers, undock, TERMINAL) - and the verifier chain caught the FIRST mission-machinery-found Parsek recording defect: analyzer RED, INV4-PARTEVENT-PID x13 on the Station recording d5355cc6. Root cause: the launch_vessel FLIGHT->FLIGHT reload is classified as a quickload (stale vesselSwitchPending), and RestoreActiveTreeFromPending's NAME fallback adopted the fresh-rollout Interceptor (same .craft, same "Kerbal X" name, different Vessel.id) and PID-remapped the Station recording onto it, so the whole Interceptor flight recorded into the Station recording with foreign craft-baked part pids. FIXED Parsek-side: QuickloadResumeMatchGuard (fresh-rollout pid + launch-guid gates in the restore match loop); forensics in todo-and-known-bugs.md flight-16 entry | LIVE-PROVEN 2026-07-24: flight 17 on the guard build = MISSION-OK + analyzer red=0 (the QuickloadResumeMatchGuard fix verified on a clean two-tree save; the one residual red was the spec's own dock token - docking MERGES trees, Parsek logs 'Tree merge created: type=Dock', only splits log 'Tree branch created'); flight 18 = FULL PASS, all seven verifiers green, fifth consecutive hard dock. Re-tiered nightly. 18-flight campaign, zero manual sessions |
| FORGE-bdock-station | operator | (Not a Parsek-surface test) FIXTURE-FORGE: launch_vessel the docking Kerbal X onto the pad + SaveGame -> stamps the bdock-station-pad fixture headlessly (replaces the operator fixture flight) | None - runnable now on a provisioned instance; harvest tool normalizes the output |
| FORGE-eva3-pad | operator | (Not a Parsek-surface test) FIXTURE-FORGE (EVA-3 sibling): launch_vessel the Kerbal X onto the pad with THREE named crew + SaveGame -> stamps the eva3-pad-3crew fixture headlessly. Uses the review-follow-up-2 crew (by NAME) + launch_site plumbing | DONE 2026-07-24: forge run + `harvest_bdock_station.py --target-name eva3-pad-3crew` produced the committed eva3-pad-3crew fixture, and EVA-3 flew it to a full PASS (the Kerbal X pad-EVA reachability caveat did NOT materialize) |
| FORGE-eva2-lko | operator | (Not a Parsek-surface test) FIXTURE-FORGE, the FIRST ORBITAL one (mission `forge_lko`): boots the SAME bdock-forge-base, launch_vessel the Kerbal X with TWO named crew (Valentina + Bob), then flies the LIVE-PROVEN B-DOCK Interceptor-leg shape - MechJeb ascent, circularization with node-executor autowarp EXPLICIT (flight-12 lesson), the two-step separation contract (drop the spent core AND ignite the orbital stage, thrust-verified, cap 2), then a PARK phase that cuts throttle, clears nodes, holds SAS+RCS and requires a HELD stable ~100 km circular orbit (pe >= 75 km, tumble <= 0.05 rad/s) before SaveGame. Crew is gated ON THE PAD (crew_count >= minCrew, fail-closed on the -1 unread sentinel) so an uncrewed stamp flakes in 300 s instead of after a 10-minute flight. autoRecordOnLaunch pinned false so the fixture carries no recordings / trees / ledger state (the stamped .sfs does keep an inert populated `SCENARIO{name=ParsekScenario}` node - `gameStateEventCount=18` + one MILESTONE_STATE row - which is what suppresses PreParsekBackup at load) | DONE 2026-07-24: forge run = MISSION-OK / PASS, 268 s wall, full profile PRELAUNCH -> LAUNCH -> ASCENT -> CIRCULARIZE -> SEPARATE -> PARK -> ORBIT; harvested with `harvest_bdock_station.py --target-name eva2-lko-crewed --expect-situation ORBITING` (the harvest's new optional situation gate, added for this orbital harvest); the `eva2-lko-crewed` fixture is COMMITTED and EVA-2-orbital-board flew it green on its first flight |
| S1.5-rewind-loop | operator | TimeJump-past-EndUT spawn, then rewind-strip-respawn cycle observables | Operator observation session (B9 pair) |
| S4.1-rewind-merge | operator | Full re-fly cycle: InvokeRewind a crashed slot, merge-dialog fold, corpus survival, read-back guard | Operator observation session (B9 pair) |
| B10-career-passive-safety | daily | Fresh career + stock actions only = ZERO economy drift (the BUG-A science/funds corruption class) | Fixture committed (fresh-career); first green live run re-tiers to daily |
| L1-passive-sandbox | daily | Sandbox cold load moves nothing (recalc/orchestrator/patcher inert) | Fixture committed (fresh-sandbox); + seed-baseline no-pools gate must accept an empty-manifest sandbox template (see fixtures README) |
| L1-hire-kerbal-career | daily | Hire debits funds by exactly the pinned cost, nothing else | First live run (2026-07-23) RED = seam double-debit: the hire verb manually mirrored a stock debit that stock already applies (Funding.onCrewHired via OnCrewmemberHired), charging the pool twice. Fixed (seam AddFunds removed); single cost re-pinned -62113 (seed 500000 -> 437887). Re-run confirms hardDivergences=0 + re-tiers to daily |
| L1-dismiss-kerbal-career | daily | Dismiss is pool-neutral | Fixture committed (fresh-career, dismiss Bill Kerman); first green live run re-tiers to daily |
| L1-research-node-career | daily | Research debits science exactly | Fixture committed (fresh-career, basicRocketry=5 verified); first green live run re-tiers to daily |
| L1-research-node-science | daily | Same in science mode (no funds/rep pools) | Fixture committed (fresh-science); RnDPresent widen landed; first green live run re-tiers to daily |
| L1-upgrade-facility-career | daily | Facility upgrade debits funds per-level exactly | First live run (2026-07-23) ledger math PASSED (-150000, hardDivergences=0) but logContract RED = FacilityUpgraded never recorded: the facility recorder only polled on scene load (and cold-load seeded an empty baseline), so a seam upgrade-then-quit was never captured. Fixed (subscribe GameStateFacilityRecorder to OnKSCFacilityUpgrading, event-driven). Re-run confirms "Game state: FacilityUpgraded" present + re-tiers to daily |

### EVA (M-C2 + EVA-4), committed (4): all four LIVE-PROVEN

EVA-1, EVA-3 and EVA-4 are LIVE-PROVEN (2026-07-24); EVA-2 is still blocked on
its `eva2-lko-crewed` fixture, so its "Blocker" is the operator forge run plus
the remaining live-prove items (P3 count / P4 orbital auto-record wording in
`design-autotest-eva-missions.md`). Parsek surfaces: EVA/Board tree branch
points + EvaCrewName, FlagEvent fidelity, crew conservation, foreground vs
deferred EVA recording paths, and (EVA-4 only) the mid-flight ATMOSPHERIC EVA
branch with the kerbal's own falling-vessel recording.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| EVA-1-pad-flag | nightly | Foreground EVA branch (structural snapshot + EvaCrewName), FlagEvent capture into the foreground recorder, board merge back to the pod | First flight 2026-07-24: EvaExit (ladder release applied) + EvaBoard merge-back + StopRecording + CommitTree chain all green, analyzer save CLEAN. TWO gate/release defects FOUND + FIXED (both edge-triggered-FSM family): (1) PlantFlag gate read `Events["PlantFlag"].active`, an edge-triggered cache that latches stale-false when a kerbal lands and stands still on the pad; gate now reads live `CanPlantFlag()` + plantable-fsm-state. (2) Flight-2 (with the fixed gate + `blocked=` diag) NAMED the real blocker: `blocked=fsm=Ladder_Idle,no-ground-contact` for the full 180 s - EvaExit's `released=true` was a FALSE POSITIVE. The release fired `On_ladderLetGo` during the transitional `st_ladder_acquire` state (~0.2 s post-exit) where the event is not registered = a silent `RunEvent` no-op, so the kerbal hung on the hatch ladder forever. `ApplyLadderRelease` now fires ONLY from a receptive ladder state, VERIFIES the fsm left the ladder (synchronous RunEvent), bounded re-fire cap 3, and `released=true` means verified-left. (3) Flight-3 (both FSM fixes landed): the plant went in PHYSICALLY (`Kerbin/FlagPlant` milestone credited) but the recording-layer FlagEvent was never captured - `afterFlagPlanted` never fired. Decompiled `FlagSite`: the SiteRename popup that fires `afterFlagPlanted` (inside its button `afterDialog` callback) only spawns after the FULL plant-animation timer (`On_flagPlantComplete` KFSMTimedEvent) in `OnPlacementComplete`, but the seam's "edge case 10" fallback declared the dialog "answered-externally" as soon as the FlagSite vessel existed (created at `flagPlant_OnEnter`, ~110 ms in, before the animation), false-OK'd the plant, and `flushandquit` tore the scene down before the popup ever spawned. Fixed (seam side): the answered-externally inference is DELETED; the seam now waits for the real popup (`DecideSiteRenameDialogAction`) then invokes its dismiss button's own callback so `afterFlagPlanted` fires and Parsek captures the FlagEvent; honest `flag-timeout` if the popup never spawns. See todo-and-known-bugs.md. Re-flight pending to prove all three fixes + P6 flag-capture (`Flag planted: ... date stamped` + `Flag event captured`) end to end ; LIVE-PROVEN 2026-07-24 (flight 4 full PASS; 3 seam/fsm defects found+fixed: stale plant-gate cache, silent no-op ladder release, SiteRename dialog false-OK that skipped afterFlagPlanted) |
| EVA-2-orbital-board | daily | Deferred auto-record-on-EVA path (D1 auto-record-eva) + re-board; the settleSeconds dwell beats the auto-record race (F7) | STILL pending-fixture: `eva2-lko-crewed` does not exist yet. Its FORGE now does - FORGE-eva2-lko (mission `forge_lko`, operator tier) - so the remaining gate is the operator forge RUN + `harvest_bdock_station.py --target-name eva2-lko-crewed --expect-situation ORBITING`. The spec's fixture block now states the forged contract EVA-2 relies on (orbital stage only, 2 named crew with Valentina as crew[0], ~100 km circular pe >= 75 km, throttle cut, nodes cleared, SAS+RCS held, zero Parsek state). Then P3 count / P4 orbital auto-record wording, then re-tier to daily |
| EVA-3-multi-kerbal | nightly | Two sequential EVA branch points + two board merges in one tree; the F2 quiescence conjunct protects the second exit | Fixture `eva3-pad-3crew` COMMITTED (P2 done, forged 2026-07-24). First flight 2026-07-24: driverValidity PASS (all 4 EVA verbs OK, each exit->board cycle under 0.8 s wall), analyzer red=0, logValidate PASS, anomalySweep PASS; the only red was 2 missing logContract tokens (`detected boarding from EVA`, `Tree board merge completed`) for BOTH cycles. A PARSEK defect FOUND + FIXED: an EVA branch parks the kerbal's recording in BackgroundMap and only the post-switch first-modification watcher promotes it, so a `release=false` exit-then-board inside ~0.18 s left `recorder=null` at the board and BOTH the boarding detection and `HandleTreeBoardMerge` failed closed - the saved tree carried 2 EVA branch points and ZERO Board branch points, kerbal recordings terminal Destroyed instead of Boarded. `OnCrewBoardVessel` now rebinds a background-only EVA recording to the live recorder at the board (`DecideEvaBoardPromotion`, 11 xUnit cells); the seam was deliberately NOT changed (a wait-for-merge there would reclassify a dropped merge as driver INVALID instead of PARSEK-FAIL). Re-flight pending to pin the P3 count window and confirm 2 EVA branches + 2 boarding detections + 2 board merges. Batch autorun evaluated = NOT wired (batchComplete SKIPPED, see EVA-1 spec) ; LIVE-PROVEN 2026-07-24 (flight 2 full PASS after the board-merge data-loss fix; 2 promotions/2 merges/7 recordings) |
| EVA-4-atmo-chute | nightly | Mid-flight ATMOSPHERIC EVA branch (every other EVA case exits on the ground or in orbit), atmospheric TrackSections on the KERBAL's own falling-vessel recording, the EVA chute captured as a two-phase part event on the kerbal (D7 chute-two-phase, previously unclaimed), and the DOWN terminal applied to a KERBAL recording with the kerbal ALIVE | FLIGHT 1 (2026-07-24) ASSERT-FAILED AS DESIGNED, re-tuned, re-fly pending. The machine, the named-failure design and the diagnostics all worked: `eva-window-missed: altitude 702m fell below the window floor 800m (vspeed -295.2m/s, situation FLYING, craftChute armed)`, phasesReached PRELAUNCH/ASCENT/COAST/DESCENT, apoapsisWindow met (19,879 m), no budget burn (107 s wall). MEASURED profile: peak altitude 11,965 m at ut 60.6; unchuted descent settles at TERMINAL -301 m/s by ~2,700 m; chute armed at 2,382 m / -301 m/s and 5.1 s later at 855 m the rate had moved 4.7 m/s. ROOT CAUSE (recording + decompile, not inference): the pod's `.prec` carries ZERO Parachute* part events, and decompiled `ModuleParachute.cs:1255-1290` gates ACTIVE->SEMIDEPLOYED on `automateSafeDeploy >= deploymentSafeState` while the fixture persists `automateSafeDeploy = 0` (only while SAFE) - which DeploySafe never reads at ~300 m/s in dense air. Arming low was INERT, not late; a craft at terminal velocity never slows on its own. THREE FIXES: (1) ARM WHILE SLOW - the machine now arms on the COAST->DESCENT transition frame itself (falling through into the descent body so there is no one-poll delay; measured entry rates -7.4/-16.9/-26.1/-35.5 m/s, bound 30), i.e. at the apoapsis crossing where DeploySafe is trivially SAFE and Kerbin is already ~0.2 atm; (2) RAISE the stock full-deploy altitude from the fixture's 1000 m to 2500 m via kRPC `Parachute.DeployAltitude` (a PAW tweakable) so the full canopy exists well above the EVA band - the Mk16 animation is ~8 s (`deploymentSpeed = 0.12`); (3) GATE ON OBSERVED STATE - new opt-in `craft_chute_state` telemetry channel (kRPC `ParachuteState`, "" unread = fail-closed) so the window requires the chute to READ Deployed, never the commanded latch that was true for the whole failed flight. Window re-tuned [800,2400]/60 -> [700,2100]/25; descent budget provisionally raised 240 -> 480 s and runtime 1560 -> 1920 s because the semi-deployed rate was not measured yet. A new `craftCanopyObserved` assertion row reports observed-vs-commanded in the result JSON. Same-evidence FINDING SPUN OFF: B1-pad-hop's chute never opens either (its 2026-07-20 recording has zero Parachute* events and ends at 65 m) - B1 passes because its DOWN terminal only checks the COMMANDED latch. NOTE on the failed attempt's artifacts: run.py USED TO drive the remaining seam steps regardless of the mission outcome, so flight 1 DID perform a terminal-velocity hatch EVA after the ASSERT-FAIL (EvaExit at ~356 m / -277 m/s, kerbal chute semi-deployed at 221 m, landed alive, tree committed) - no false PASS (the run classifies INVALID(mission) before the tail and the save is re-staged per attempt), but a window-missed run's collected save/log carried a spurious EVA branch + landing and could burn ~120 + 420 s of deferral budget. FIXED harness-side 2026-07-25 (see the M-A5 row): an UNMET mission step now drives the CLEANUP tail only (StopRecording + FlushAndQuit), so this scenario's EvaExit / EvaChuteDeploy / CommitTree are skipped on a window-missed attempt ; LIVE-PROVEN 2026-07-24 (flight 2 FULL PASS, all seven verifiers: canopy observed Deployed, handoff 1,606 m / -23.2 m/s, kerbal chuted descent steady -4.5 m/s, ParachuteCut at touchdown, down=true situation=LANDED alive=true. All four pins closed: P1 count PINNED 3, P2 `'kerbalEVA` token confirmed, P3 semi-deployed rate MEASURED at about -236 m/s peak with the whole DESCENT phase 61.6 s -> descentTimeoutSeconds trimmed 480 -> 240 (~3.9x margin; step/runtime budgets deliberately left at 900/1920 as wall-clock envelopes), P4 kerbal lands alive. Post-live hardenings: K=2 EVA-window debounce and the RAW-alive CompleteOk conjunct) |

## Mission-machine trust layer

The shared flyby machine (mlib) was hardened by 19+ live findings so that a
mission FAILURE is attributable to Parsek or the contract - never to
autopilot noise. Capabilities (all live-proven): native warp-to-UT with
zombie-safe cancel + asymmetric retargeting; certified no-1x coast
(`harness/warp_audit.py --fail-on-violation`, contiguous + cumulative);
flameout staging under both the DIY burner and the throttle-collapsing
MechJeb executor; bounded correction give-ups with warp-time-excluded
clocks; closed-loop arrival quality (patched-conic next_pe telemetry,
no-encounter creation, impact-certain early terminal); planner-bias margin
targets (finding 16d); 20+ telemetry channels + machine-state/gate-evidence
lines + live status CLI (`harness/status.py`). Full forensics per finding:
`todo-and-known-bugs.md`.

## Verification layers (all active)

- Headless: 592 mission-machine + 483 harness + 203 provisioner unittest
  cells; 18,647 xUnit on the C# side (analyzer, seam, log contracts, the
  new route-window delta formatter).
- Per-run: the 7-verifier chain + collect-logs on every non-PASS.
- In-game: 158 runtime tests / 42 categories (autorun-able), H5 invariants,
  log-contract tests.
- Findings baseline: 5 historical saves baselined; fresh harness saves run
  baseline-Forbid (structural fresh-save guard).
- Coverage ledger: 52 / 238 registry cells covered (the growth metric).

## Run telemetry - what a live run actually shows you

A 2026-07-25 audit measured the live surface and found that every phase budget
in the system is GAME time and there was no WALL budget anywhere in it. That let
a real failure through: a B12 run died on `mission-budget-expired` after burning
57% of its wall budget in ONE phase while every displayed budget read ~7.5%
consumed. Six surfacing changes landed; none of them is a new subsystem, all
six publish or compare numbers the runner already measured.

| Change | What it fixes |
|---|---|
| WALL block in the status payload + `status.py` panel (`wallElapsedSeconds` / `wallRemainingSeconds` / `wallBudgetSeconds` / `phaseWallSeconds`) | The panel printed `wall ~N (telemetry-line est.)` with NO denominator. It now reads `mission wall: 39m39s / 1h10m (57%) \| phase wall 39m39s`, falling back to the line-count estimate for an older/stale status file. `status.py` also reads the real denominator out of `driver.steps[].budget` (it only ever looked at `[driver.missionParams]`, where the wall budget does not live) |
| `gameSecondsPerWallSecond` live + per phase | The ratio that named two shared-machine warp defects was end-of-run only with ZERO programmatic consumers. It is now a phase-history column, the OPEN phase's live `phaseWarp` block in the status payload, and a LOW marker (>= 120 s wall at < 100 game-s per wall-s). The marker is INFORMATIONAL by construction: the measured thrash reads ~40 while MechJeb's legitimate 600 s pre-ignition hold reads 7.96, so the ratio alone cannot separate broken from deliberate. The BROKEN case stays owned by `warp_liveness_starved` |
| `PHASE_BUDGET_KEYS` covers the ORBIT tail | The table covered 8 phases and was blind to PLAN-CAPTURE / CAPTURE-BURN / PARK / ORBIT-COMMIT, so B11's CAPTURE-BURN (642 wall s, the most expensive phase in the suite) printed `budget n/a`. All four keys plus the B1/B4/EVA-4/FORGE/B-DOCK phases are mapped, each mirroring mlib's own `_*_phase_budget`, and the four ORBIT phases have heuristic branches reading `captureExecDownStreak` / `parkStableStreak` / `nodeExec` |
| Gate-flip window dumps rate-limited | MEASURED: one B12 stdout log is 43 MB / 181,786 lines, 79.5% of it `window[NN/20]` payload from 7,218 gate-flip dumps (7,207 from the single field `gate warpToCmd`). Even a healthy B5 run spends 50% of its lines on window payload carrying 71% duplicate frames. Gate-flip dumps now fire at most once per 10 s (= the ring's own span, so admitted dumps are contiguous and non-overlapping); `phase-transition` / `terminal-*` / `vessel-lost` dumps stay unconditional, the `gate warpToCmd` line itself is untouched, and one batch-summary line per flight names how many were suppressed |
| Committed `harness/coverage/duration.json` | `flake.json` tracked outcomes and nothing tracked duration, and every artifact carrying one is gitignored. Consequence: the B12 spec claimed B11 was the SHORTER run, backwards across four measured runs each (B11 p50 1,317 s, B12 p50 627 s), unnoticed. `run.py` now writes a committed per-scenario `{n, p50, p95, last, lastVsP50}` record over PASS results only and warns when `last > 1.5 * p50` (gated at 3+ samples). The B12 spec claim is corrected |
| Per-scenario retry cost + `missionWallSeconds` | B7-duna burned 794 + 776 = 1,570 s across two INVALID attempts and produced nothing, traceable only as two unrelated summary lines. Each scenario now logs `scenario cost attempts=N wallTotal=Xs terminal=Y` and carries `attemptsWallSeconds`; the result also carries the mission's own `missionWallSeconds`, so the harness-vs-mission residue is a subtraction. That residue MEASURED at a stable 40-67 s across 16 runs (KSP boot ~35 s + verifier chain ~10 s), which is why the seven individual call sites are deliberately NOT instrumented - past ~120 s is the signal to look closer |

## Known gates and latent items (forensics in todo-and-known-bugs.md)

1. B6 20 km / B7 300 km course-correct targets - see the test-case table.
2. Runner-only kRPC behaviors are LIVE-VERIFIED ONLY (no headless guard can
   exercise MechJeb server state): intercept-only planner flags, executor
   abort-before-native-AP, deceleration_time override, Smart A.S.S. off.
   Their symptom signatures are the first triage suspects on recurrence.
3. STOCK_AWARD_PATTERNS are dead against real KSP logs: the ledger-oracle
   capture cross-check is a structural no-op until the pattern rewrite
   (needs the operator stock-award capture session).
4. Flake ledgers (generated, gitignored) reset 2026-07-22 post-campaigns;
   quarantine (sticky, >0.20) is reporting-only and now reflects post-merge
   reality only.
5. INV2 double-cover recorder seam: REAL Parsek defect (first big catch),
   being fixed in its own lane.
6. No-vessel LoadGame boot contract (ledger lane): the SPACECENTER route in
   `ParsekTestCommandAddon.LoadGameImpl` now writes `persistent.sfs`
   (`GamePersistence.SaveGame(game, "persistent", save, OVERWRITE)`) AFTER
   `UpdateScenarioModules` and BEFORE `Start()`, matching stock
   `MainMenu.OnLoadDialogPipelineFinished`. Load-bearing because the KSC scene
   bootstrap `SpaceCenterMain.Start()` re-reads `persistent.sfs` from disk and
   runs `SetProtoModules` on THAT game, not the in-memory `HighLogic.CurrentGame`;
   without the write the fresh-* fixtures booted to KSC with no ParsekScenario,
   so `OnLoad` never ran and the `GameStateRecorder` never subscribed (the 5
   ACTING L1 cases reded on the missing recorded-action log line though the
   ledger oracle passed). Fixed 2026-07-23.
7. COMMANDED-vs-OBSERVED assertion class (found 2026-07-25 via B1, fixed for
   B1 and EVA-4). An assertion or terminal that reads the machine's own "we
   issued the command" latch proves only that the machine acted, never that
   KSP complied - and it fails OPEN, which is the worst direction for a test.
   B1 shipped four months of green nightlies on a chute that never opened.
   AUDIT DEBT: `evaluate_b4_assertions`'s `chuteDeployed` is still a commanded
   latch, and the B4 fixture (`b2-lko-craft`) carries the same
   `automateSafeDeploy = 0` with the same altitude-triggered deploy at 3000 m.
   That does NOT mean B4 is broken: B4 is live-proven with a real SPLASHDOWN, so
   something did slow that craft, and the reentry profile differs from a pad hop.
   It means B4's chute claim rests on the same unfalsifiable evidence B1's did,
   and needs its own diagnosis from a B4 recording (check for `Parachute*` part
   events) before anyone concludes either way. Any new assertion over a
   part-module state must read the module, not the command.
   THIRD INSTANCE FOUND 2026-07-25 (the first full sweep): EVA-4's chute opened
   and then CUT itself, the kerbal fell from -11 to -109 m/s and died, and the
   MISSION still returned `MISSION-OK reason=all telemetry assertions met` -
   none of its four assertions covers kerbal survival, which is that mission's
   stated purpose. Survival is currently proven only by seam log tokens. Filed
   in `todo-and-known-bugs.md` under the EVA-4 section.
8. The no-1x-coast certification CANNOT SEE the coast warp-thrash class
   (orbit lane finding iii, 2026-07-25). `warp_audit.py` looks for a contiguous
   30-second window at 1x, and thrash 1x is FRAME-INTERLEAVED (issue, cancel on
   the next blind read, re-issue), so it never forms one; the certification is
   also a WALL-CLOCK profile check while the defect is a COMMAND invariant
   ("a healthy coast issues ONE warp command, not 3,603"). B5 flight 26 is
   certified no-1x at HEAD config and the metastable thrash would still have
   passed it. Covered for now by the machine-side `coast-warp-thrash` fast-fail
   (`MAX_PHASE_WARP_ISSUES` = 500, counted per phase entry and also armed at the
   correction aim-warp and flyby warp sites as `correction-aim-warp-thrash` /
   `flyby-warp-thrash`) plus the per-phase `warpUtilisation` block and the
   `warp-liveness-starved` floor that consumes `gameSecondsPerWallSecond`,
   but the AUDIT itself remains blind to the class - a real gap in an existing
   gate, not a new instrument.
9. ~~B11 / B12 recordings-count windows are PROVISIONAL at {1, 9}~~ **CLOSED
   2026-07-25.** Both are PINNED at `{min 8, max 8}` from
   `verifiers.expectations.observed.recordings.count` on a measured green run
   (B11 flight 4, B12 flight 5), so every flown scenario is now pinned to its
   measured topology. What replaced it is a NAMED limitation, not a gate: the
   count is COMMIT-BLIND. `run.py count_recordings` counts `.prec` sidecars and
   `ParsekScenario` OnSave writes them for the ACTIVE (uncommitted) tree too, so
   two archived runs that crossed into the target SOI and NEVER committed
   (zero `CommitTreeFlight` lines) still produced exactly 8. The window guards
   recording TOPOLOGY; the commit itself is guarded by the logContract tokens,
   which now include the foreground SOI-crossing line and a per-recording
   terminal verdict naming `terminalOrbitBody`.

## Operator items outstanding

1. Career fixture saves (3) - DONE + LIVE-PROVEN (no operator session): file-
   constructed (fresh-career / fresh-science / fresh-sandbox), 7/7 ledger
   scenarios green, re-tiered pending-fixture -> daily, hire/upgrade author
   constants confirmed, the seed-baseline no-pools gate resolved.
2. EVA fixture saves (2) - DONE + LIVE-PROVEN 2026-07-24 (no operator flying):
   both `eva2-lko-crewed` and `eva3-pad-3crew` were FORGED HEADLESSLY
   (FORGE-eva2-lko / FORGE-eva3-pad) and are committed, EVA-2 re-tiered
   pending-fixture -> daily, and all three EVA scenarios flew green, which
   settled P1/P3/P4/P5/P6 (count windows now pinned exactly, log-token wording
   confirmed, flag capture proven). The only EVA item left is the optional
   promotion of EVA-1 / EVA-3 nightly -> daily once flake data exists.
3. Stock-award real-line capture session (unblocks the pattern rewrite).
4. B9 rewind observation session (S1.5 + S4.1).
5. B1 chute re-prove: NO operator session needed - it is a normal unattended
   nightly run. Listed here only because it is the gate that returns B1 to
   live-proven, and because its result pins P1/P2/P3 in the B1 spec.

## Roadmap (agreed order; each item named by its Parsek utility)

1. M-C2 in-game proof - DONE 2026-07-24. The verbs + hlib companions +
   EVA-1/2/3 specs are implemented and the whole live-prove list (P1-P6) is
   closed: both fixtures (`eva2-lko-crewed`, `eva3-pad-3crew`) forged
   headlessly and committed, all three EVA scenarios flown green, count windows
   pinned exactly, log-token wording confirmed, ladder-drop + flag-capture
   proven. The crew/EVA/flag recording surface no flight can reach is now
   covered.
2. Mun/Minmus ORBIT missions - capture burn + commit-in-target-orbit terminal:
   recordings that END in a foreign SOI (new commit/BG-handoff surface vs the
   free-return shape). **DONE 2026-07-25** as **B11-mun-orbit** +
   **B12-minmus-orbit**, both LIVE-PROVEN and the lane's re-fly debt paid:
   - B11 FULL PASS three times - flight 2, flight 3 as the confirmation the
     changed TARGET-FLYBY profile owed, and flight 4 as the count-pin run. Wall
     1,269-1,271 s, all six assertions met, capture eccentricity 0.000127,
     TARGET-FLYBY warp 27 game-s / 2 commands (was 8,213 game-s).
   - B12 FULL PASS on flights 4 and 5 (5 is the count-pin run). Wall 580 s, all
     six assertions met, capture eccentricity 0.00026, a 194,543 game-second
     coast in 26 wall-s at ratio 7,535 on 3 warp commands.
   - B6-minmus-flyby, exposed to two of the shared-machine defects, re-flew
     green (wall 359 s), so its LIVE-PROVEN mark is honest at HEAD again.
   Four shared-machine findings came out of the lane (no-start watchdog vs
   MechJeb's own 600 s pre-ignition hold; a GAME-time correction budget spent by
   the aim-then-warp it waits on; the metastable coast warp-thrash behind KSP's
   NaN `time_to_soi` under a warp ramp; warp inherited across the SOI boundary at
   10,000x) - full forensics in `todo-and-known-bugs.md`. B5/B7 exposure was
   RE-ASSESSED after review (the earlier "both changes gate on `capture_enabled`
   / opt-in fields" claim was WRONG: two of the four fixes - the correction-budget
   re-anchor and the coast native-warp latch - are ungated shared-machine
   changes, which is exactly why B6 owed a re-fly). Corrected position:
   - B5 is covered by PROXY, not by a gate. `B11-mun-orbit` carries params
     IDENTICAL to `B5-mun-flyby` (same `targetBodyName`, `correctionTriggerAlts`,
     `transferBurnTimeout`, `coastTimeout`, `coastWarpFactor`, `flybyWarpFactor`,
     `soiLead`, `nodeArrivalMargin`) and flew that coast + correction
     configuration green on the fixed machine.
   - B7 was FLOWN at HEAD 2026-07-25 rather than argued about, and it does NOT
     pass - but for a PRE-EXISTING reason, not a lane regression. See the B7 row
     and the todo entry: it is the first flight of HEAD's 300 km periapsis target
     (the gate that row already carried), the target was hit correctly
     (`pe=310089`), and the approach was captured by IKE on both attempts. The
     lane's changes are exonerated on the evidence: `corrBudgetAnchorUt=none`
     (the re-anchor never engaged, so it ran main's exact bound) and
     `phaseWarpIssues=1` (the coast latch worked, which is WHY the flight reached
     Duna at all - every archived pre-fix B7 run died at `Kerbin to Sun` and
     never reached the target SOI).
   POST-REVIEW RE-FLIGHT SWEEP (2026-07-25, all four green on attempt 1 on the
   fixed machine + the redeployed DLL). Three Opus reviewers returned FIX-FIRST;
   the machine fixes changed frames the green flights had taken (a
   `time_to_periapsis > 0` arming conjunct, three new named warp terminals armed
   on the SHARED machine), so every affected mission was re-flown rather than
   argued about:
   | Scenario | Result | Why it was owed |
   |---|---|---|
   | B11-mun-orbit | PASS, wall 1,270.195 s | arming conjunct + first live proof of the commit-terminal token |
   | B12-minmus-orbit | PASS, wall 580.826 s | same |
   | B5-mun-flyby | PASS, wall 468.009 s | `flyby-warp-thrash` / `correction-aim-warp-thrash` / `warp-liveness-starved` are new terminals reachable on the flyby family |
   | B6-minmus-flyby | PASS, wall 359.425 s | same |
   None of the three new terminals fired on a healthy flight, so they bound the
   broken case without narrowing the correct one.
   ID NOTE: this item was informally called "B8", but B8/B9/B10 are already
   taken in `automated-testing-scenario-catalog.md` section 2 (loop-B7-as-
   mission / crash-rewind-refly / career passive safety) and B3 is the EVA
   branch, so the lane is B11 + B12. The count follow-up is CLOSED: both windows
   are PINNED at {8, 8} off a measured
   `verifiers.expectations.observed.recordings.count` (B11 flight 4, B12 flight
   5). The pin is COMMIT-BLIND by construction - it guards recording topology,
   not the commit - so the commit claim is carried by the logContract tokens
   instead (see known-gate item 8).
3. Mun/Minmus LANDING missions - upper stage landed: landed-on-other-body
   recording, surface TrackSections off Kerbin, the landing FSM seam.
   **DONE 2026-07-25 - BOTH AXES LIVE-PROVEN ON THEIR FIRST FLIGHT** as
   **B13-mun-landing** + **B14-minmus-landing** (branch
   `autotest-landing-missions`, stacked on `autotest-orbit-missions`).
   | Scenario | Result | Landed terminal | Airless surface entry |
   |---|---|---|---|
   | B14-minmus-landing | FULL PASS attempt 1, wall 2,083.9 s | `terminalState=Landed terminalOrbitBody=Minmus` | `Approach -> SurfaceMobile` |
   | B13-mun-landing | FULL PASS attempt 1, wall 2,747.9 s | `terminalState=Landed terminalOrbitBody=Mun` | `Approach -> SurfaceStationary` |
   Both measured `observed.recordings.count = 8`, pinning both windows from
   measurement. The topology is identical to B11/B12 - 6 Destroyed boosters,
   1 Orbiting/Kerbin flameout core - with ONLY the root's terminal changed,
   which is precisely the discriminator this lane exists to move. B14 touched
   down at -0.25 m/s vertical / 0.06 m/s horizontal on the craft's own three
   landing legs; no craft modification, no separate lander stage, no downloaded
   craft (the stock Kerbal X upper stage already carries legs, heat shield,
   chute, ladders and solar panels, and the measured fuel at PARK - lf 592.8 at
   Mun, 650.6 at Minmus - leaves better than 3x the landing cost).
   A PARSEK FIX WAS REQUIRED for the lane to be verifiable at all: a LANDED
   recording never carries `TerminalOrbitBody` (`CaptureTerminalOrbit` returns
   early for surface situations and `UsesTerminalOrbitMetadata` excludes
   `Landed`), so every landed commit line read `terminalOrbitBody=(null)` and
   the ONE fact this lane proves - WHICH body - was unprovable from logs.
   `FormatCommitTerminalLine` now falls back to `TerminalPosition.body`, with
   orbit metadata still authoritative when both are set (4 new xUnit cells).
   MECHJEB CAVEAT CONFIRMED LIVE: the untargeted descent runs at 1x with ZERO
   warp commands under the installed 2.15.1 pin, exactly as the decompile
   predicted, so the wall budget was sized for the no-warp case and the flight
   finished at 48% of it rather than being reaped.
   COST: B13 at 2,825 s is now the most expensive scenario in the suite
   (previously BDOCK-1 at 2,164 s), and the pair adds ~83 minutes, taking the
   full-suite p50 from ~137 to ~219 minutes. A nightly rotation probably cannot
   afford B13 + B14 + BDOCK-1 together; the duration ledger now prices that
   decision instead of leaving it to guesswork. Same id reasoning as B11/B12: B8/B9/B10 are taken
   in the catalog and B3 is the EVA branch, so the LANDING lane takes B13/B14.
   - MACHINE: the LIVE-PROVEN `mlib.b5_decide` with ONE new param,
     `landingEnabled`, on top of `captureEnabled`. PRELAUNCH through PARK is
     byte-identical to the five B11 and six B12 flights; `landingEnabled`
     implies `captureEnabled` by construction, because the only door into
     DESCENT is the capture lane's PARK dwell. NEW is the four-phase tail
     DESCENT (MechJeb `LandUntargeted`, warp-PASSIVE - MechJeb's landing states
     own the warp via the shared `Core.Node.Autowarp` flag the runner sets
     explicitly) -> LANDED-SETTLE (throttle cut, autopilot released, SAS held,
     rails 1x, a held settled dwell) -> SURFACE-COMMIT (the same route-1
     mid-mission seam CommitTree) -> SURFACE-COMMITTED.
   - NEW SURFACES (the reason the lane exists): terminal classification `Landed`
     for a foreign-body tree, SURFACE-class TrackSections OFF Kerbin (the
     classifier's AIRLESS `Approach -> Surface*` path, unreachable where an
     atmosphere classifies first), landing-leg part events, and the
     landed-vessel ghost / playback surface. NEW registry cells: D1
     `commit-landed-foreign-body` and D4 `surface-stationary` (previously
     unclaimed by any scenario).
   - LIVENESS: DESCENT carries FOUR distinctly named give-ups on top of its
     GAME budget - `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off
     `LandingAutopilot.Enabled`, with a REQUIRED touchdown carve-out because
     MechJeb disables its own module on the landed frame),
     `landing-no-progress` (the independent altitude-trend channel, with a
     separate `altitude-unreadable` name), `landing-touchdown-timeout` and
     `landing-vessel-lost` (a crash must read as neither a timeout nor a
     success). LANDED-SETTLE adds `landed-never-stable`.
   - TWO KNOWN GAPS, both filed rather than papered over (full text in
     `todo-and-known-bugs.md`): (a) the commit-terminal log line CANNOT name the
     body for a `Landed` terminal - `CaptureTerminalOrbit` writes
     `TerminalOrbitBody` only for ORBITING / SUB_ORBITAL / FLYING / ESCAPING and
     `UsesTerminalOrbitMetadata` excludes Landed, so
     `terminalState=Landed terminalOrbitBody=Mun` can never match and the specs
     require the body-agnostic form plus the SOI token; (b) DESCENT has no
     WALL-time bound of its own, because the `warp-liveness-starved` floor is
     armed only by OUR OWN native warp and arming it across a phase that
     legitimately runs at 1x would false-flake a healthy landing.
   - PENDING-OPERATOR: five first-flight pins (recordings count `{8, 9}`,
     `descentTimeoutSeconds`, the two wall budgets, the settled-speed floors and
     the landed dwell), each naming the flight that closes it. B13 will be the
     MOST EXPENSIVE scenario in the suite (mission budget 5000 s wall against
     B11's 3000 and BDOCK-1's measured 2,164 s), which is stated in the spec
     rather than hidden.
4. Ledger campaign resumption once career fixtures exist (L1 -> L2+): the
   initiative's END GOAL.
5. B-DOCK first flight - the docking/rendezvous lane (dock-undock recording
   structure) is now IMPLEMENTED (`autotest-bdock-impl`); remaining is the
   headless fixture-forge run (`FORGE-bdock-station` -> harvest -> commit
   `bdock-station-pad`), re-tier BDOCK-1 pending-fixture -> nightly, and the
   first flight (P1-P9 live-proves). It unlocks the D10 route-candidate +
   D5 cross-tree-dock/undock-split recording surface.
6. Candidates (unscheduled): Eve flyby (cheap B7 clone), stock-award pattern
   rewrite, nightly rotation shakedown, EVA registry growth (D5/D12 cells),
   an orbital-rendezvous-dock D10 registry value + a same-craft-twice
   identity D18 value (the two B-DOCK coverage gaps).
