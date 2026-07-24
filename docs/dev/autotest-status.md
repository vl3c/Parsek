# Automated Testing System - Status

Last updated: 2026-07-23 (M-C2 EVA verbs + EVA-1/2/3 specs implemented; B-DOCK
dock/transfer/undock lane + fixture-forge implemented; both headless-green,
pending live proof / first flight). This file is the single at-a-glance answer
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
expectations). Ten test cases are live-proven green end-to-end, including
Mun/Minmus/Duna flybys with a certified no-1x-coast warp profile. All
infrastructure modules are shipped and merged. The FIRST two-vessel lane
(B-DOCK: dock/transfer/undock, the logistics-route recording entry point) is
IMPLEMENTED and headless-green, pending a headless fixture-forge run + its
first flight. Coverage stands at 52 of 238 registry cells - breadth (EVA,
orbit, landing, docking, career-ledger lanes) is the frontier.

## Infrastructure modules (all SHIPPED and merged)

| Module | What it gives Parsek testing | Status |
|---|---|---|
| M-A1 offline analyzer | Recording invariants (INV1-INV9) over any save, RED gate, per-save findings baseline | SHIPPED (#1300/#1302/#1306); AnalyzerVersion 3; core in Parsek.dll so in-game H5 runs the same rules |
| M-A2 command seam | Drives Parsek actions kRPC cannot (record/commit/discard, rewind, dialogs, KSC actions, EVA) | SHIPPED (#1301); 18 implemented verbs, 11 reserved (M-C1 + M-C2 grew the table) |
| M-A3 autorun hooks | Unattended in-game test batches (PARSEK_AUTORUN_*) | SHIPPED (#1305) |
| M-A5 harness core | The orchestrator: admission, staging, seam driving, budget kill, verifier chain, verdicts, coverage/flake ledgers | SHIPPED (#1307, #1316) |
| M-A6 provisioner | Reproducible pinned KSP instance (kRPC 0.5.4 + MechJeb 2.15.1 + KRPC.MechJeb 0.8.1 + built TestingTools) | SHIPPED (#1303/#1308/#1318) |
| M-B1 mission library | Pure mission state machines + kRPC runner (flights become deterministic, diagnosable instruments) | SHIPPED (#1313); hardened by the flyby campaign |
| M-B2 ledger oracle | Seam-declared action manifests -> expected career totals -> save diff (PARSEK-FAIL(ledger)) | SHIPPED (#1314); stock-award-pattern gate below |
| M-B3 ledger scripts | The L1 scenario six-pack | SHIPPED (#1324); LIVE-PROVEN 2026-07-23 (career fixtures file-constructed headlessly; 7/7 ledger scenarios green, now daily tier) |
| M-C1 seam verbs batch 1 | InvokeRewind, AnswerMergeDialog, TimeJump, KscAction, SaveGame | SHIPPED (#1320/#1325) |
| M-C2 EVA verbs + missions | EvaExit/EvaBoard/PlantFlag -> crew/EVA/flag recording coverage | IMPLEMENTED PENDING IN-GAME PROOF; 18 implemented verbs, 11 reserved; verbs + pure deciders + hlib companions + EVA-1/2/3 specs land; awaits the operator live-prove list (P1-P6) |

## Test cases (all 25 committed scenarios)

LIVE-PROVEN = at least one fully-unattended PASS with every verifier green.
The "Parsek surface verified" column is the reason the case exists.

### Live-proven (10)

| Test case | Tier | Parsek surface verified | Coverage cells |
|---|---|---|---|
| B1-pad-hop | nightly | Auto-record-on-launch, atmospheric TrackSections, chute-deployed ground-arrival recording (DOWN contract) | D1 auto-record-launch; D4 atmospheric; D14 kerbin |
| B2-lko-ascent | nightly | Ascent-to-orbit recording, orbital checkpoints, 6-booster parent-anchored debris children model | D1; D3 orbital-checkpoint; D4 atmospheric/exo-propulsive; D14 kerbin |
| B4-reentry-splashdown | nightly | Full-cycle recording (ascent/deorbit/reentry/splashdown intact), exo-ballistic sections, rails-warp recording | D1; D3; D4 +exo-ballistic; D14 kerbin/warp-rails |
| B5-mun-flyby | nightly | Cross-SOI cohesive coast recording (Kerbin->Mun->Kerbin), on-rails checkpoints across warp, warp-reseed seams | D1; D3; D4 +cohesive-cross-body-coast; D14 kerbin/mun/warp-rails. NO-1X CERTIFIED at HEAD config (flight 26: wall 465 s, warp audit exit 0) |
| B6-minmus-flyby | nightly | Same cells on the minmus axis | As B5 with D14 minmus. GATE: 20 km course-correct target predates finding 16d; guarded (arrival gate + impact terminal fail clean); re-target ~150 km only if it reds |
| B7-duna-flyby | nightly | Multi-SOI interplanetary recording (Kerbin->Sun->Duna->Sun), 100,000x warp recording, SOI-count | As B5 with D14 duna/soi-count/warp-high. GATE: HEAD's 300 km target has not itself flown (the pass flew 50 km); first nightly covers it |
| S0.5-live-record-discard | daily | Live record start/stop marker pairing + DiscardTree returns the store to zero (caught the orphan-sidecar leak) | D1 discard-rollback; D5 single-node; D14 |
| S0.6-live-record-commit | daily | Commit on top of the injected corpus without corpus loss (the save-hollowing guard class) | D5; D14; D16 sidecar-prec |
| S1.4-injected-playback | daily | 272-tree corpus injection, load, ghost map presence + polyline render with no anomalies | D6 basic-playback/ghost-map-presence/non-orbital-polyline; D16 sidecar-prec/sidecar-pcrf |
| H5-invariants-corpus | daily | The full synthetic corpus (306 recordings / 276 trees) loads intact and holds every recording invariant in-game | D14 sandbox/scene-flight; D16 sidecar-prec/schema-gate |

### Committed, not yet live-run (12)

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| H6-route-rewind-timeline | daily | Route-rewind lifecycle rows, dormant classify + Tick materialize, kept-route reconciliation | None - its next daily run IS its live-prove |
| BDOCK-1-station-interceptor | pending-fixture | FIRST two-vessel flight (18-phase machine): cross-tree Dock branch, authoritative onVesselsUndocking split, RouteConnectionWindow recorded-delta contract (the new `Route window delta:` line), same-craft-twice launch identity. Flight-1/2 wall budgets re-timed; flight-3 lesson (STATION-SEPARATE / INT-SEPARATE) + flight-4 lesson (two-step SEPARATE: drop the spent lifter AND ignite the orbital engine, thrust-verified, cap 2) both live-confirmed through RENDEZVOUS on flight 5; flight-5 lesson (MATCH-VELOCITY kill-rel-vel retargeted XFromNow ~15 s lead + bounded 600 s give-up + per-frame diagnostics + one-shot dropped-target re-acquire); flight-8 lesson (prox-ops rule: abort the pending kill-rel-vel node executor at DOCK entry before the docking AP owns the ship, else it rails-warps + packs the port target null + NREs); flight-9 lesson (core.target one-Update sync trap: stagger the docking-AP enable one poll after the port target); flight-10/11 lesson (prox-ops observability [angular_velocity/sas/rcs/docking_ap_status + per-frame DOCK diag line] + attitude hold [SAS+RCS after each separation and at DOCK entry] + LIVENESS watchdogs [budgets bound SLOW, watchdogs bound BROKEN: DOCK enable-never-took / died-mid-approach / no-progress fast flakes, TRANSFER stall fast flake, bounded dropped-target re-arm x3]). Flight 10 reached DOCK; the AP died and rode the full budget (the liveness defect now fixed) | Forge run + fixture commit (below), then re-tier to nightly + flight 11 |
| FORGE-bdock-station | operator | (Not a Parsek-surface test) FIXTURE-FORGE: launch_vessel the docking Kerbal X onto the pad + SaveGame -> stamps the bdock-station-pad fixture headlessly (replaces the operator fixture flight) | None - runnable now on a provisioned instance; harvest tool normalizes the output |
| S1.5-rewind-loop | operator | TimeJump-past-EndUT spawn, then rewind-strip-respawn cycle observables | Operator observation session (B9 pair) |
| S4.1-rewind-merge | operator | Full re-fly cycle: InvokeRewind a crashed slot, merge-dialog fold, corpus survival, read-back guard | Operator observation session (B9 pair) |
| B10-career-passive-safety | daily | Fresh career + stock actions only = ZERO economy drift (the BUG-A science/funds corruption class) | Fixture committed (fresh-career); first green live run re-tiers to daily |
| L1-passive-sandbox | daily | Sandbox cold load moves nothing (recalc/orchestrator/patcher inert) | Fixture committed (fresh-sandbox); + seed-baseline no-pools gate must accept an empty-manifest sandbox template (see fixtures README) |
| L1-hire-kerbal-career | daily | Hire debits funds by exactly the pinned cost, nothing else | First live run (2026-07-23) RED = seam double-debit: the hire verb manually mirrored a stock debit that stock already applies (Funding.onCrewHired via OnCrewmemberHired), charging the pool twice. Fixed (seam AddFunds removed); single cost re-pinned -62113 (seed 500000 -> 437887). Re-run confirms hardDivergences=0 + re-tiers to daily |
| L1-dismiss-kerbal-career | daily | Dismiss is pool-neutral | Fixture committed (fresh-career, dismiss Bill Kerman); first green live run re-tiers to daily |
| L1-research-node-career | daily | Research debits science exactly | Fixture committed (fresh-career, basicRocketry=5 verified); first green live run re-tiers to daily |
| L1-research-node-science | daily | Same in science mode (no funds/rep pools) | Fixture committed (fresh-science); RnDPresent widen landed; first green live run re-tiers to daily |
| L1-upgrade-facility-career | daily | Facility upgrade debits funds per-level exactly | First live run (2026-07-23) ledger math PASSED (-150000, hardDivergences=0) but logContract RED = FacilityUpgraded never recorded: the facility recorder only polled on scene load (and cold-load seeded an empty baseline), so a seam upgrade-then-quit was never captured. Fixed (subscribe GameStateFacilityRecorder to OnKSCFacilityUpgrading, event-driven). Re-run confirms "Game state: FacilityUpgraded" present + re-tiers to daily |

### EVA (M-C2), committed, pending in-game proof (3)

Verbs + pure deciders + hlib companions + specs are committed and green in
every headless suite; the "Blocker" is the operator live-prove list (P1-P6 in
`design-autotest-eva-missions.md`), which pins the recordings-count windows
(R-C), the exact structural-snapshot / orbital-auto-record log wording, and
the flag-capture proof. Parsek surfaces: EVA/Board tree branch points +
EvaCrewName, FlagEvent fidelity, crew conservation, foreground vs deferred EVA
recording paths.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| EVA-1-pad-flag | nightly | Foreground EVA branch (structural snapshot + EvaCrewName), FlagEvent capture into the foreground recorder, board merge back to the pod | In-game proof: P1 fixture sanity, P3 count-window pin, P5 ladder-drop safety, P6 flag-capture proof |
| EVA-2-orbital-board | pending-fixture | Deferred auto-record-on-EVA path (D1 auto-record-eva) + re-board; the settleSeconds dwell beats the auto-record race (F7) | Fixture `eva2-lko-crewed` (P2), then P3 count / P4 orbital auto-record wording |
| EVA-3-multi-kerbal | pending-fixture | Two sequential EVA branch points + two board merges in one tree; the F2 quiescence conjunct protects the second exit | Fixture `eva3-pad-3crew` (P2), then P3 count-window pin |

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

- Headless: 330 mission-machine + 402 harness + 203 provisioner unittest
  cells; 18.5k+ xUnit on the C# side (analyzer, seam, log contracts, the
  new route-window delta formatter).
- Per-run: the 7-verifier chain + collect-logs on every non-PASS.
- In-game: 158 runtime tests / 42 categories (autorun-able), H5 invariants,
  log-contract tests.
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

## Operator items outstanding

1. Career fixture saves (3) - DONE + LIVE-PROVEN (no operator session): file-
   constructed (fresh-career / fresh-science / fresh-sandbox), 7/7 ledger
   scenarios green, re-tiered pending-fixture -> daily, hire/upgrade author
   constants confirmed, the seed-baseline no-pools gate resolved.
2. EVA fixture saves (2): `eva2-lko-crewed` + `eva3-pad-3crew` (M-C2 P2);
   re-tiers EVA-2/EVA-3 pending-fixture -> daily/nightly. Plus the EVA
   live-prove session (P1/P3/P5/P6) that promotes EVA-1 nightly -> daily.
   (Both fixtures are now forge-able headlessly via the B-DOCK forge.)
3. Stock-award real-line capture session (unblocks the pattern rewrite).
4. B9 rewind observation session (S1.5 + S4.1).

## Roadmap (agreed order; each item named by its Parsek utility)

1. M-C2 in-game proof - the verbs + hlib companions + EVA-1/2/3 specs are
   IMPLEMENTED (headless-green); the remaining work is the operator live-prove
   list (P1-P6): the two new fixtures (`eva2-lko-crewed`, `eva3-pad-3crew`),
   the first EVA-1/2/3 runs to pin count windows + log-token wording, and the
   ladder-drop / flag-capture confirmations. Unlocks the crew/EVA/flag
   recording surface no flight can reach.
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
