# Automated Testing System - Status

Last updated: 2026-07-26 (VACUOUS-BATCH class found and closed and then kept in
step with the C# it counts; the first two D11 scenarios; ALL FIVE RunTests
tallies now MEASURED off live flights, no derived value and no PENDING-OPERATOR
left among them; and one real Parsek defect caught by the new M1 scenario's very
first flight.

B10-career-passive-safety - shipped, daily tier - was proved live to read GREEN
while executing ZERO tests: its RecordingInvariants batch ran at SPACECENTER,
where both FLIGHT-scene tests are scene-skipped, and its only contract
`BATCH_COMPLETE v1 .* failed=0\b` cannot tell `passed=0 skipped=2` from two
passes. L1-passive-sandbox had the identical shape. Both fixtures are vessel-less
by design, so the CATEGORY moved to GameActionsHealth (4 scene-agnostic read-only
tests) rather than papering the pin over a category the fixture can never host.
The class is closed harness-side: hlib.validate_spec now synthesizes every
BATCH_COMPLETE line a vacuous batch could emit and REJECTS any spec whose
contract would accept one - checked by construction, not by a syntactic "must
mention total=" tautology, with a reason-required opt-out.

SECOND ORDER: pinning the tally whole makes each pin a hardcoded copy of a number
that lives in C#, so CommittedBatchTallySourceSyncTests now cross-checks every
pinned tally against the [InGameTest] attributes in Source/Parsek - total exactly,
skipped as a floor (run-time InGameAssert.Skip guards are not statically
derivable, which is why L1-passive-sandbox legitimately pins skipped=3 over a
category whose attributes force 0). Adding an in-game test to a pinned category
now reds locally instead of on the next nightly.

NEW: M1-mission-loop-unit and M2-periodicity-solver take D11 from 0/18 to 8/18 by
running the Missions / Periodicity in-game categories, which needed no fixture
and had never run because no spec named them; both gate the mission-loop PLAN and
the periodicity SOLVER, explicitly not the playback.

MEASURED 2026-07-26, five flights, all PASS: B10 `total=4 passed=4 skipped=0`
(the vacuity fix proven - the same step used to emit `total=2 passed=0
skipped=2`), L1-passive-sandbox `total=4 passed=1 skipped=3`, M2 `total=11
passed=7 skipped=4`, M1 `total=12 passed=5 skipped=7`, and S1.4 `total=42
passed=40 skipped=2` - which CLOSES the last PENDING-OPERATOR of the set (S1.4
had shipped a loose `passed=[1-9][0-9]*` because 12 of its 42 carry conditional
skips and no live tally had ever been recorded).

M1's first flight also found a REAL Parsek-side defect and its `recordings.count
= {0,0}` pin is what caught it: in-game tests that register synthetic trees
through the real `RecordingStore.CommitTree` were leaving orphan `.prec` /
`.pann` / `.prec.txt` sidecars in the produced save. `CommitTree` flushes
`SaveRecordingFiles` per recording; `RemoveCommittedTreeById` is memory-only by
design; once those ids leave the store `CleanOrphanFiles` preserves the files
forever - the S0.5 discard-residue shape again. Fixed at the SHARED path: the
one-test `PersistenceSplitOptimizerTestCleanup` is generalized to
`InGameTestSidecarReaper` with a known-ids guard, and wired into all five
commit-driving in-game tests. Re-flown green with the pin untouched.

Prior 2026-07-25: B1-pad-hop DE-LISTED from live-proven: its
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
that question has already paid: the initiative's real catches include the
INV2 double-cover recorder seam defect, the S0.5 orphan-sidecar leak, and
(2026-07-26, on a brand-new scenario's FIRST flight) the same orphan-sidecar
class in every in-game test that drives a real `RecordingStore.CommitTree`.

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
Mun/Minmus/Duna flybys with a certified no-1x-coast warp profile. (B1-pad-hop
was de-listed from live-proven on 2026-07-25: its PASSes proved the flight but
its chute never opened, and its terminal could not tell the difference. See its
row below and gate 7. B10 and L1-passive-sandbox were re-proven on 2026-07-26 in
a corrected batch category, after their earlier greens were shown to have
executed zero tests; M1 and M2 are new and flew the same day.) All
infrastructure modules are shipped and merged. The FIRST two-vessel lane
(B-DOCK: dock/transfer/undock, the logistics-route recording entry point) is
IMPLEMENTED and headless-green, pending a headless fixture-forge run + its
first flight. Coverage stands at 77 of 238 registry cells (the "52" this line
carried until 2026-07-26 was stale; the recount at that date was 69, and
M1-mission-loop-unit + M2-periodicity-solver added the 8 D11 cells below) -
breadth (EVA, orbit, landing, docking, career-ledger lanes) is the frontier.

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
| M-B3 ledger scripts | The L1 scenario six-pack | SHIPPED (#1324); LIVE-PROVEN 2026-07-23 (career fixtures file-constructed headlessly; 7/7 ledger scenarios green, now daily tier). Caveat recorded 2026-07-26: the ORACLE half of those 7 was genuine, but L1-passive-sandbox's and B10's in-game BATCH half executed zero tests under the old category - both re-flown green in `GameActionsHealth` on 2026-07-26 |
| M-C1 seam verbs batch 1 | InvokeRewind, AnswerMergeDialog, TimeJump, KscAction, SaveGame | SHIPPED (#1320/#1325) |
| M-C2 EVA verbs + missions | EvaExit/EvaBoard/PlantFlag -> crew/EVA/flag recording coverage | LIVE-PROVEN 2026-07-24; 18 implemented verbs, 11 reserved; verbs + pure deciders + hlib companions + EVA-1/2/3 specs land, both fixtures forged headlessly, all three scenarios flown green, live-prove list P1-P6 closed |
| EVA-4 atmospheric chute | EvaChuteDeploy (the kerbal personal parachute) + mission `eva4_atmo_chute` -> mid-flight atmospheric EVA branch, kerbal-owned atmospheric TrackSections, two-phase chute part events ON the kerbal, kerbal DOWN-alive terminal | LIVE-PROVEN 2026-07-24 (flight 2 full PASS); 19 implemented verbs, 11 reserved; all four first-flight pins closed (count 3, kerbalEVA token, semi-deployed rate measured -> descent budget trimmed 480 -> 240, kerbal lands alive), plus the K=2 window debounce + raw-alive CompleteOk conjunct hardenings |

## Test cases (all 30 committed scenarios)

LIVE-PROVEN = at least one fully-unattended PASS with every verifier green.
The "Parsek surface verified" column is the reason the case exists.

### Live-proven (14)

| Test case | Tier | Parsek surface verified | Coverage cells |
|---|---|---|---|
| H6-route-rewind-timeline | daily | Route-rewind lifecycle rows, dormant classify + Tick materialize, kept-route reconciliation (Restore(cutoff) reconciliation-bundle path) | D9 reconciliation-bundle; D10 route-x-rewind; D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-24: first live run = FULL PASS attempt 1, all seven verifiers green, in-game batch perCategory=1 - the route-rewind wave's last automated acceptance item. Batch tally pinned WHOLE 2026-07-26: `failed=0 skipped=0` still accepted an EMPTY batch (`total=0 passed=0 ...`), so the pin is now `total=7 passed=7 failed=0 skipped=0 category=RouteRewindTimeline scene=FLIGHT` (7 = the category's scene-agnostic, batch-allowed test count; the live PASS's skipped=0 fixes passed=total) |
| B2-lko-ascent | nightly | Ascent-to-orbit recording, orbital checkpoints, 6-booster parent-anchored debris children model | D1; D3 orbital-checkpoint; D4 atmospheric/exo-propulsive; D14 kerbin |
| B4-reentry-splashdown | nightly | Full-cycle recording (ascent/deorbit/reentry/splashdown intact), exo-ballistic sections, rails-warp recording | D1; D3; D4 +exo-ballistic; D14 kerbin/warp-rails |
| B5-mun-flyby | nightly | Cross-SOI cohesive coast recording (Kerbin->Mun->Kerbin), on-rails checkpoints across warp, warp-reseed seams | D1; D3; D4 +cohesive-cross-body-coast; D14 kerbin/mun/warp-rails. NO-1X CERTIFIED at HEAD config (flight 26: wall 465 s, warp audit exit 0) |
| B6-minmus-flyby | nightly | Same cells on the minmus axis | As B5 with D14 minmus. GATE: 20 km course-correct target predates finding 16d; guarded (arrival gate + impact terminal fail clean); re-target ~150 km only if it reds |
| B7-duna-flyby | nightly | Multi-SOI interplanetary recording (Kerbin->Sun->Duna->Sun), 100,000x warp recording, SOI-count | As B5 with D14 duna/soi-count/warp-high. GATE: HEAD's 300 km target has not itself flown (the pass flew 50 km); first nightly covers it |
| S0.5-live-record-discard | daily | Live record start/stop marker pairing + DiscardTree returns the store to zero (caught the orphan-sidecar leak) | D1 discard-rollback; D5 single-node; D14 |
| S0.6-live-record-commit | daily | Commit on top of the injected corpus without corpus loss (the save-hollowing guard class) | D5; D14; D16 sidecar-prec |
| S1.4-injected-playback | daily | 272-tree corpus injection, load, ghost map presence + polyline render with no anomalies | D6 basic-playback/ghost-map-presence/non-orbital-polyline; D16 sidecar-prec/sidecar-pcrf. Batch contract hardened 2026-07-26 (was `failed=0` only) and its PENDING-OPERATOR CLOSED the same day by a live flight: the loose `passed=[1-9][0-9]* skipped=[0-9]+` placeholder is replaced by the MEASURED `total=42 passed=40 failed=0 skipped=2 category=GhostPlayback scene=FLIGHT`. The 2 skips are 1 structural (`AllowBatchExecution=false`) + 1 fixture-determined self-skip; the other 10 conditional guards did not fire on this corpus |
| H5-invariants-corpus | daily | The full synthetic corpus (306 recordings / 276 trees) loads intact and holds every recording invariant in-game | D14 sandbox/scene-flight; D16 sidecar-prec/schema-gate. Batch tally pinned WHOLE 2026-07-26 from the MEASURED 2026-07-19 line: `total=2 passed=2 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT` |
| B10-career-passive-safety | daily | Fresh career + stock actions only = ZERO economy drift (the BUG-A science/funds corruption class), now with a batch that genuinely executes | D8 funds/science/reputation/recalc-from-ut0; D14 career/cold-load-ut0/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category: `total=4 passed=4 failed=0 skipped=0 ... scene=SPACECENTER`, MEASURED. Its earlier "green" runs executed ZERO tests (see the de-listing note in todo-and-known-bugs.md); this is the vacuity fix proven live. D16 schema-gate stays dropped - it was claimed over a save with zero recordings |
| L1-passive-sandbox | daily | Sandbox cold load moves nothing (recalc/orchestrator/patcher inert); the one scene- and mode-independent suppression-flag assertion actually runs | D8 recalc-engine/orchestrator/ksp-state-patcher; D14 sandbox/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category (its first flight there): `total=4 passed=1 failed=0 skipped=3 ... scene=SPACECENTER`, MEASURED, and the 3 skips ARE the subject (SANDBOX has no pools). Ledger oracle green, hardDivergences=0 |
| M1-mission-loop-unit | daily | The mission-loop PLAN against LIVE stock ephemerides: cross-tree partner-journey link discovery + include/normalize mutation + the REAL MissionLoopUnitBuilder shared span clock landing member windows on the recorded dock/undock UTs; joint landing+station arrival hold (with the landing-only byte-identical-off control); the resonant Jool inner-three configuration hold; incommensurate Bop failing CLOSED to faithful | D11 partner-journey/land-dock-dual-constraint/arrival-hold/multi-moon-config-hold/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26 - the first spec to claim ANY D11 cell (the dimension was 0/18). LIVE-PROVEN 2026-07-26, and it EARNED ITS KEEP ON FLIGHT 1: batch tally exact (`total=12 passed=5 failed=0 skipped=7`), but its `recordings.count = {0,0}` pin red'd on a real Parsek-side defect - in-game tests driving the real `RecordingStore.CommitTree` left orphan sidecars the memory-only tree teardown could not reach. Fixed via the shared `InGameTestSidecarReaper`; re-flown FULL PASS with the pin untouched. Gates the PLAN, not the playback: no ghost, icon, cycle boundary or elapsed period is observed |
| M2-periodicity-solver | daily | The periodicity SOLVER against LIVE stock ephemerides: the re-aim feasibility scan over a pinned synodic period, UvLambert transfer synthesis that must actually encounter the target, the window schedule, the eccentric/inclined stage-A un-projection + stage-B tof band (Moho / Eeloo), heliocentric-parking r1==park-end, and deterministic clean declines at the band edge | D11 reaim-lambert/eccentric-inclined-targets/heliocentric-parking-departure/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26, M1's sibling (separate spec because run.py's `_driven_category` reads only the FIRST RunTests category and a per-category line with no aggregate is a defined fault - one batch per spec). LIVE-PROVEN 2026-07-26, full PASS attempt 1; tally MEASURED and deliberately NOT total=passed: `total=11 passed=7 failed=0 skipped=4` (1 FLIGHT-scene member + 3 `AllowBatchExecution=false` diagnostics). Gates the SOLVER, not the playback |

### Committed, not yet live-run (12)

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| B1-pad-hop | nightly | Auto-record-on-launch, atmospheric TrackSections, and a genuinely CHUTE-BORNE ground-arrival recording: the two-phase ParachuteSemiDeployed -> ParachuteDeployed part events on the craft's own parachuteSingle (D7 chute-two-phase, claimed 2026-07-25) | DE-LISTED from live-proven 2026-07-25. The 2026-07-19/20 PASSes proved the FLIGHT, not the CHUTE: their recordings carry ZERO Parachute* part events, and the DOWN terminal - which gated only on the machine's own COMMANDED chute latch - awarded a ~300 m/s terminal-velocity impact the "chute-deployed impact" success end. Root cause (decompiled ModuleParachute + two flights of evidence, forensics in todo-and-known-bugs.md): the fixture's parachuteSingle persists `automateSafeDeploy = 0` (open only while SAFE) and stock DeploySafe never reads SAFE at terminal velocity in dense air, so an ALTITUDE-triggered arm sat inert in ARMED forever. FIXED: arm at the apoapsis crossing while still slow (the technique EVA-4 flight 2 live-proved on this exact fixture and craft), and gate BOTH the DOWN terminal and the new `craftCanopyObserved` assertion on the OBSERVED kRPC ParachuteState. Its next nightly run IS its re-prove; three things it pins are P1 the final full-canopy leg to the ground, the one segment EVA-4 never times because it always hands off in mid-air (budgets DERIVED from EVA-4 flight-2 measurements rather than guessed: descent 240 -> 360 s, mission 600 -> 900 s, wall 900 -> 1320 s; the first draft's 600 s descent assumed a ~30 m/s semi-deployed crawl and was wrong by ~8x - the semi-deployed craft sinks at up to -236 m/s, and chuteFullDeployAltMeters was raised 1000 -> 2500 to match EVA-4's live-proven value because the full canopy needs 894 m just to brake), P2 which end it reaches - computed touchdown is ~8-9 m/s and the parts' own crashTolerance values predict fins and booster destroyed with the pod intact, so expect LANDED with debris, DOWN accepted as the fallback, P3 the recordings count on the chuted profile |
| BDOCK-1-station-interceptor | nightly | FIRST two-vessel flight (18-phase machine): cross-tree Dock branch, authoritative onVesselsUndocking split, RouteConnectionWindow recorded-delta contract (the new `Route window delta:` line), same-craft-twice launch identity. Flight-1/2 wall budgets re-timed; flight-3 lesson (STATION-SEPARATE / INT-SEPARATE) + flight-4 lesson (two-step SEPARATE: drop the spent lifter AND ignite the orbital engine, thrust-verified, cap 2) both live-confirmed through RENDEZVOUS on flight 5; flight-5 lesson (MATCH-VELOCITY kill-rel-vel retargeted XFromNow ~15 s lead + bounded 600 s give-up + per-frame diagnostics + one-shot dropped-target re-acquire); flight-8 lesson (prox-ops rule: abort the pending kill-rel-vel node executor at DOCK entry before the docking AP owns the ship, else it rails-warps + packs the port target null + NREs); flight-9 lesson (core.target one-Update sync trap: stagger the docking-AP enable one poll after the port target); flight-10/11 lesson (prox-ops observability [angular_velocity/sas/rcs/docking_ap_status + per-frame DOCK diag line] + attitude hold [SAS+RCS after each separation and at DOCK entry] + LIVENESS watchdogs [budgets bound SLOW, watchdogs bound BROKEN: DOCK enable-never-took / died-mid-approach / no-progress fast flakes, TRANSFER stall fast flake, bounded dropped-target re-arm x3]). flight-13 ROOT CAUSE (behind every dock failure since flight 7): pre-`launch_vessel`-reload PART handles are stale - the reload destroys every Part, so the captured docking-port handle resolves to a destroyed part and assigning it silently CLEARS the target; VESSEL handles survive (P9 answered). Fix: resolve port + docking-state + transfer tanks LIVE at call time. Flight 13's liveness layer fast-flaked in 10 s with the named E1a reason (wall 2133 s) and pinned this. Flight 16 (2026-07-24): MISSION-OK END TO END (launch, separate, mid-mission commit seam, launch_vessel, rendezvous, hard dock, LF 40 + mono 15 transfers, undock, TERMINAL) - and the verifier chain caught the FIRST mission-machinery-found Parsek recording defect: analyzer RED, INV4-PARTEVENT-PID x13 on the Station recording d5355cc6. Root cause: the launch_vessel FLIGHT->FLIGHT reload is classified as a quickload (stale vesselSwitchPending), and RestoreActiveTreeFromPending's NAME fallback adopted the fresh-rollout Interceptor (same .craft, same "Kerbal X" name, different Vessel.id) and PID-remapped the Station recording onto it, so the whole Interceptor flight recorded into the Station recording with foreign craft-baked part pids. FIXED Parsek-side: QuickloadResumeMatchGuard (fresh-rollout pid + launch-guid gates in the restore match loop); forensics in todo-and-known-bugs.md flight-16 entry | LIVE-PROVEN 2026-07-24: flight 17 on the guard build = MISSION-OK + analyzer red=0 (the QuickloadResumeMatchGuard fix verified on a clean two-tree save; the one residual red was the spec's own dock token - docking MERGES trees, Parsek logs 'Tree merge created: type=Dock', only splits log 'Tree branch created'); flight 18 = FULL PASS, all seven verifiers green, fifth consecutive hard dock. Re-tiered nightly. 18-flight campaign, zero manual sessions |
| FORGE-bdock-station | operator | (Not a Parsek-surface test) FIXTURE-FORGE: launch_vessel the docking Kerbal X onto the pad + SaveGame -> stamps the bdock-station-pad fixture headlessly (replaces the operator fixture flight) | None - runnable now on a provisioned instance; harvest tool normalizes the output |
| FORGE-eva3-pad | operator | (Not a Parsek-surface test) FIXTURE-FORGE (EVA-3 sibling): launch_vessel the Kerbal X onto the pad with THREE named crew + SaveGame -> stamps the eva3-pad-3crew fixture headlessly. Uses the review-follow-up-2 crew (by NAME) + launch_site plumbing | DONE 2026-07-24: forge run + `harvest_bdock_station.py --target-name eva3-pad-3crew` produced the committed eva3-pad-3crew fixture, and EVA-3 flew it to a full PASS (the Kerbal X pad-EVA reachability caveat did NOT materialize) |
| FORGE-eva2-lko | operator | (Not a Parsek-surface test) FIXTURE-FORGE, the FIRST ORBITAL one (mission `forge_lko`): boots the SAME bdock-forge-base, launch_vessel the Kerbal X with TWO named crew (Valentina + Bob), then flies the LIVE-PROVEN B-DOCK Interceptor-leg shape - MechJeb ascent, circularization with node-executor autowarp EXPLICIT (flight-12 lesson), the two-step separation contract (drop the spent core AND ignite the orbital stage, thrust-verified, cap 2), then a PARK phase that cuts throttle, clears nodes, holds SAS+RCS and requires a HELD stable ~100 km circular orbit (pe >= 75 km, tumble <= 0.05 rad/s) before SaveGame. Crew is gated ON THE PAD (crew_count >= minCrew, fail-closed on the -1 unread sentinel) so an uncrewed stamp flakes in 300 s instead of after a 10-minute flight. autoRecordOnLaunch pinned false so the fixture carries no recordings / trees / ledger state (the stamped .sfs does keep an inert populated `SCENARIO{name=ParsekScenario}` node - `gameStateEventCount=18` + one MILESTONE_STATE row - which is what suppresses PreParsekBackup at load) | DONE 2026-07-24: forge run = MISSION-OK / PASS, 268 s wall, full profile PRELAUNCH -> LAUNCH -> ASCENT -> CIRCULARIZE -> SEPARATE -> PARK -> ORBIT; harvested with `harvest_bdock_station.py --target-name eva2-lko-crewed --expect-situation ORBITING` (the harvest's new optional situation gate, added for this orbital harvest); the `eva2-lko-crewed` fixture is COMMITTED and EVA-2-orbital-board flew it green on its first flight |
| S1.5-rewind-loop | operator | TimeJump-past-EndUT spawn, then rewind-strip-respawn cycle observables | Operator observation session (B9 pair) |
| S4.1-rewind-merge | operator | Full re-fly cycle: InvokeRewind a crashed slot, merge-dialog fold, corpus survival, read-back guard | Operator observation session (B9 pair) |
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

- Headless: 454 mission-machine + 544 harness + 203 provisioner unittest
  cells; 18.5k+ xUnit on the C# side (analyzer, seam, log contracts, the
  new route-window delta formatter).
- Per-run: the 7-verifier chain + collect-logs on every non-PASS.
- In-game: 539 runtime tests / 97 categories (autorun-able), H5 invariants,
  log-contract tests. Counted mechanically by
  `hlib.parse_ingame_test_declarations` over `Source/Parsek`, not by hand
  (the hand-and-grep number was 534 / 96: five namespace-qualified
  declarations were invisible to both).
- Spec-vs-source: every batch-owning spec's pinned `BATCH_COMPLETE` tally is
  cross-checked against the C# `[InGameTest]` attributes it describes
  (`CommittedBatchTallySourceSyncTests`), so adding an in-game test to a
  pinned category fails locally instead of on the next nightly. The same
  sweep asserts RECOGNITION completeness, so an attribute spelling the parse
  does not model reds instead of quietly shrinking a total.
- Findings baseline: 5 historical saves baselined; fresh harness saves run
  baseline-Forbid (structural fresh-save guard).
- Coverage ledger: 52 / 238 registry cells covered (the growth metric).

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
2. B8 Mun/Minmus ORBIT missions - capture burn + commit-in-target-orbit
   terminal: recordings that END in a foreign SOI (new commit/BG-handoff
   surface vs the free-return shape).
3. Mun/Minmus LANDING missions - upper stage landed: landed-on-other-body
   recording, surface TrackSections off Kerbin, the landing FSM seam.
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
