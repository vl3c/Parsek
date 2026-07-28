# Automated Testing System - Status

Last updated: 2026-07-27 (H7-H20 ALL FLOWN, all 14 PASS on attempt 1, 805 s / 13.4 min
for the group - 49-71 s each, against 2,825 s for B13 alone. What flying added over the
static derivation, precisely: the `total=` values were already gated by the source-sync
test and needed no flight; what no static analysis predicts is the passed / skipped
SPLIT, and 13 of the 14 pin that as a LITERAL, so a PASS means the runner printed the
pinned line token for token. All 13 pre-flight derivations were right. H20 is the
exception - its interim pin proves only passed>=1, and its exact split is in no artifact
(collect-logs fires only on non-PASS, instance log since overwritten), so it keeps the
interim form pending one ~49 s re-fly. Tiering decided on failure mode rather than cost:
H18 promoted to daily because it is the sole guard for the GameEvents subscription
contract and a dropped Add() is silent; the other 13 stay nightly pending flake data.
Prior: 2026-07-26 (THE IN-GAME CATEGORY GAP, measured and half closed.
Parsek ships 539 in-game runtime tests across 97 categories; committed scenarios
drove EIGHT of those categories, so 89 were written, passed under Ctrl+Shift+T, and
never executed in any unattended run. The inventory is now DERIVED from the C#
attributes rather than guessed at, every category triaged A/B/C with the reason
stated, and 14 categories wired as batch-only specs H7-H20 over the existing
gloops-airshow fixture: 22 of 97 categories driven, 201 of 539 declarations inside a
driven category and 179 of them that would actually execute.
None has flown - 13 pin their tally WHOLE from a source derivation that closes
(attribute-exact total, plus a transitive scan proving no reachable
InGameAssert.Skip), and H20 carries the honest interim form because its split is
genuinely fixture-measured. Detail + fly order:
autotest-ingame-category-inventory.md.
Three findings fell out. (1) The prior FUTURE recommendation to wire
EvaSpawnPosition AND CrewReservationLive over the injected corpus was half wrong:
the corpus injector STRIPS every spawnedPid line by construction, so
CrewReservationLive can only ever emit the vacuous total=2 passed=0 skipped=2 -
it needs a C# corpus change, not a spec. (2) A FOURTH vacuity trap, invisible to
the anti-vacuity gate: a test that RUNS, PASSES and asserts over nothing (a store
walk over an empty store), sometimes behind a silent yield break that reports
PASSED rather than Skipped. Only the fixture defends against it, so the four
corpus-backed members pin recordings.count=272. (3) A harness bug:
hlib._pin_literal_word excluded `-`, making all seven hyphenated Pipeline-*
categories structurally unpinnable while the runtime parser accepted them fine.
Prior: 2026-07-26 (PLAYBACK is now gated, twice. S1.6-render-parity
LIVE-PROVEN on its first flight and S1.7-maprender-parity built and LIVE-PROVEN
the same day: the production recorded-vs-rendered parity oracle had existed and
been wired for months with 47 in-game tests asserting through it, and no
scenario had ever driven either category. Both pin their batch tally WHOLE and
both carry a load-bearing negative control - a deliberately wrong reference that
must FLAG on the same draw the correct reference reads as zero - measured at
~545x tolerance on S1.6 and ~488x on S1.7, so neither zero-drift assertion can
be a circle compared with itself. S1.7's first flight also exposed a harness
FALSE POSITIVE: the Tier-C anomaly sweep was a bare substring search over
KSP.log, so a PhaseSpineSwap test line whose LABEL is `parity-drift` and whose
body reads `over=False` reddened a run containing ZERO `phase=Anomaly` raises.
The sweep is now anchored on the tracers' real raise shape, and the same
investigation found the reverse defect, left UNRESOLVED and now REPORTED per-run:
the harness token set has drifted from what the mod emits - `icon-jump` is dead
(the probe raises `reason=icon-teleport`) and NINE further reasons are ungated.
That enumeration is now derived from the C# source by a harness test rather than
hand-listed, after the first pass counted five and missed the four raises that
reach EmitAnomaly through MapRenderTrace's cutover-hardening wrappers.
Also FIXED: `allowedAnomalies` was misplaced under `[expectations.logContracts]`
in all 28 pre-existing specs, so S1.4's declared exception had never been in
force; validate_spec now REJECTS the misplaced form and every spec declares the
key where run.py reads it, S1.4 keeping the gate strength it actually flew with.
The first 2026-07-26 merge of main brought four more specs written against the
old shape (B11 / B12 / B15 / B16; B13 and B14 had pre-moved their own key in
anticipation of exactly this collision) and they were relocated in that merge
commit. The second merge the same day (batch-coverage + tally-gate) needed no
relocation: main still carries the misplaced form in 32 of its own 36 specs
because it has never received this branch, the auto-merge kept the relocated
side on every one of them, and M1 / M2 were authored with the key already in
`[expectations]`. All 38 committed specs declare it where it binds; the scan
that proves it is `test_no_committed_spec_still_carries_the_misplaced_key`.
Prior: 2026-07-26 (batch coverage) - VACUOUS-BATCH class found and closed; the
first two D11 scenarios; four of the seven RunTests tallies now MEASURED off
live flights and the other three honestly labelled; and one real Parsek defect
caught by the new M1 scenario's very first flight.

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
mention total=" tautology, with a reason-required opt-out. Scope of that
guarantee, after review found three ways around the first cut and all three were
closed: a batch-owning spec must now declare exactly ONE RunTests step naming
exactly ONE category (a second step ran ungated; a multi-category aggregate
cannot express per-constituent non-vacuity), and vacuity detection requires ONE
single required pattern to reject the whole vacuous family plus the known
batch-independent decoy line, because evaluate_expectations searches each
pattern over the whole log independently. It still blocks only passed==0 - the
`passed=[1-9][0-9]*` placeholder form accepts 1-of-42 by design.

SECOND ORDER: pinning the tally whole makes each pin a hardcoded copy of a number
that lives in C#, so CommittedBatchTallySourceSyncTests now cross-checks every
pinned tally against the [InGameTest] attributes in Source/Parsek - total exactly,
skipped as a floor (run-time InGameAssert.Skip guards are not statically
derivable, which is why L1-passive-sandbox legitimately pins skipped=3 over a
category whose attributes force 0). Adding an in-game test to a pinned category
now reds locally instead of on the next nightly. The same sweep also asserts
RECOGNITION completeness: any `InGameTest` / `InGameTestAttribute` token sitting
in an attribute bracket that the strict parse did not claim (a stacked
`[Obsolete(...), InGameTest(...)]` list, the explicit `Attribute` suffix, a
namespace-qualified name, or a `[method: ...]` target) is reported as UNCLAIMED
and reds, so a form the parser does not model can never silently shrink a
category total.

NEW: M1-mission-loop-unit and M2-periodicity-solver take D11 from 0/18 to 8/18 by
running the Missions / Periodicity in-game categories, which needed no fixture
and had never run because no spec named them; both gate the mission-loop PLAN and
the periodicity SOLVER, explicitly not the playback.

2026-07-26 ran SIX flights, of which FIVE passed. The one red is M1 flight 1, and
it is the run that found the sidecar leak below - the pin doing its job, not a
blemish: B10 PASS, M1 PARSEK-FAIL, M2 PASS, M1 re-fly PASS, L1-passive-sandbox
PASS, S1.4 PASS (`harness/results/summary.txt`).

MEASURED - exact pin already committed before the flight, so the run's
expectations PASS matched the whole line: B10 `total=4 passed=4 skipped=0` (the
vacuity fix proven - the same step used to emit `total=2 passed=0 skipped=2`),
L1-passive-sandbox `total=4 passed=1 skipped=3`, M2 `total=11 passed=7
skipped=4`, M1 `total=12 passed=5 skipped=7`. H5's `total=2 passed=2 skipped=0`
is measured off its own 2026-07-19 archived line.

NOT fully measured, and now labelled as such rather than claimed: S1.4's
`total=42 passed=40 skipped=2` - the 2026-07-26 run executed the LOOSE
`passed=[1-9][0-9]*` pin, so it proves total=42 / failed=0 / category / scene but
not the 40/2 split, and that run archived no log (collectLogs ran=false on a
PASS). H6's `total=7 passed=7 skipped=0 ... scene=FLIGHT` is DERIVED: the
2026-07-24 PASS was against a pin carrying only `failed=0 skipped=0`, and the
scene token has no H6-specific evidence at all (it is inferred from the shared
gloops-airshow fixture's FLIGHT LoadGame route). Both pins STAY - each fails
LOUD, never green - and both are re-derivable from the spec comments.

M1's first flight also found a REAL Parsek-side defect and its `recordings.count
= {0,0}` pin is what caught it: in-game tests that register synthetic trees
through the real `RecordingStore.CommitTree` were leaving orphan `.prec` /
`.pann` / `.prec.txt` sidecars in the produced save. `CommitTree` flushes
`SaveRecordingFiles` per recording; `RemoveCommittedTreeById` is memory-only by
design; once those ids leave the store `CleanOrphanFiles` preserves the files
forever - the S0.5 discard-residue shape again. Fixed at the SHARED path: the
one-test `PersistenceSplitOptimizerTestCleanup` is generalized to
`InGameTestSidecarReaper` with a known-ids guard, wired into every enumerated
in-game site that reaches a real commit - the five DIRECT `CommitTree` callers
plus, after review found them, the four that reach one INDIRECTLY through the
production merge / live-recording paths (both `MergeDialog` and `SceneExitMerge`
deferred-merge canaries and the EVA ghost-snapshot canary), all now reaping at
the shared `RemoveCommittedTreeByIdForRuntimeTest` helper. Re-flown green with
the pin untouched.

Prior 2026-07-25 (ORBIT lane): REVIEWED by three Opus reviewers and the
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
| THIS FILE | Status: what is shipped, proven, gated; the historical roadmap record |
| `autotest-roadmap.md` | FORWARD build order: what we still cannot reproduce, grouped by cause, and the ranked dependency-justified sequence to close it |
| `automated-testing-plan.md` | Strategy + rationale (why the system is shaped this way; L-track definition) |
| `automated-testing-scenario-catalog.md` | The INTENDED universe: dimension registry D1-D18 vocabulary, scenario blocks, tiers, regression rotation |
| `design-autotest-*.md` (12 docs) | Per-module design authority (how each module works; binding contracts) |
| `harness/README.md` | Harness module mechanics: ownership boundary, how to run, submodule readiness |
| `todo-and-known-bugs.md` | Finding forensics: the full evidence trail behind every live finding |
| `harness/coverage/registry.toml` | The machine-readable coverage denominator (authoritative cell list) |
| `autotest-ingame-category-inventory.md` | The in-game category axis in DETAIL: all 97 categories with per-scene batch eligibility and self-skip surface, the A/B/C wiring triage, and the H7-H20 PENDING-OPERATOR runbook |

If a status statement appears anywhere else, it is a pointer to this file or
it is wrong. MAINTENANCE RULE: any PR that changes a module's status,
live-proves a scenario, adds a test case, or opens/closes a gate updates this
file in the same PR (same discipline as CHANGELOG).

## One-paragraph summary

The system flies KSP missions unattended (kRPC + MechJeb autopilot, or the
Parsek file-drop command seam), records them with Parsek, and verifies the
result through a seven-verifier chain (driver validity, in-game test batch,
offline recording analyzer, log validation, results schema, anomaly sweep,
expectations). Twenty-five test cases are live-proven green end-to-end (the 21
rows in the Live-proven table below plus the four EVA cases in their own
section), including Mun/Minmus/Duna flybys with a certified no-1x-coast warp
profile, the Mun/Minmus ORBIT pair, the Mun/Minmus LANDING pair and the Eve
flyby. (B1-pad-hop was de-listed from live-proven on 2026-07-25: its PASSes
proved the flight but its chute never opened, and its terminal could not tell
the difference. See its row below and gate 7. Its PASS in the 2026-07-25 full
sweep does NOT re-prove it - that sweep predates the merge of the canopy-gated
terminal, so it ran the old contract. B10 and L1-passive-sandbox were re-proven
on 2026-07-26 in a corrected batch category, after their earlier greens were
shown to have executed zero tests; M1 and M2 are new and flew the same day, and
S1.6 and S1.7 are the two render-parity cases this branch flew.) All
infrastructure modules are shipped and merged. The FIRST two-vessel lane
(B-DOCK: dock/transfer/undock, the logistics-route recording entry point) is
IMPLEMENTED and headless-green, pending a headless fixture-forge run + its
first flight. The Mun/Minmus ORBIT lane (B11/B12: capture burn, park, and a
commit while parked in a FOREIGN SOI) is LIVE-PROVEN on both axes as of
2026-07-25, as is the Mun/Minmus LANDING lane (B13/B14: a recording that ENDS on
foreign soil and is COMMITTED there); B15-eve-flyby is green and B16-eve-orbit
is committed but not yet flown. PLAYBACK is no longer a blind spot either:
S1.6 + S1.7 drive 47 in-game parity tests between them. Coverage stands at 84 of
241 registry cells claimed by at least one scenario (83 at the 2026-07-26
recompute; +1 for D3 `parent-anchored-debris`, claimed 2026-07-27 when the five
Kerbal X flights gained the debris-population token and a measured `count.min`).
That 84 is RECOMPUTED from
`hlib.compute_coverage` over the 38 committed specs + the registry at this
merge, not carried forward from either side: the "52" this sentence used to
print had drifted across many spec additions (it predates the EVA, B-DOCK,
ORBIT, LANDING and EVE lanes), the "70 of 239" the ORBIT lane measured on
2026-07-25 was already stale by the time the landing and Eve lanes merged (that
tree alone recomputes to 74 of 240), the "77 of 238" the batch-coverage lane
carried predated the orbit/landing registry cells, and the two sides of THIS
merge printed 75 of 241 and 82 of 240 - each right only for its own tree. The
claimed-vs-GREEN split is a DIFFERENT number and is deliberately not restated
here: it needs the run archive, and `harness/results/*.json` plus
`harness/coverage/coverage.{json,txt}` are generated + gitignored, so re-derive
it from a full results set rather than trusting a number in prose. The last
measured split was 2026-07-25's 58 green and 12 claimed-but-never-green over
that day's 70, and the never-green 12 were every cell claimed only by the two
un-flown rewind scenarios plus EVA-4's `chute-two-phase`. Breadth (EVA, orbit,
landing, docking, career-ledger lanes) is the frontier.

## Infrastructure modules (all SHIPPED and merged)

| Module | What it gives Parsek testing | Status |
|---|---|---|
| M-A1 offline analyzer | Recording invariants (INV1-INV9) over any save, RED gate, per-save findings baseline | SHIPPED (#1300/#1302/#1306); AnalyzerVersion 3; core in Parsek.dll so in-game H5 runs the same rules |
| M-A2 command seam | Drives Parsek actions kRPC cannot (record/commit/discard, rewind, dialogs, KSC actions, EVA) | SHIPPED (#1301); 18 implemented verbs, 11 reserved (M-C1 + M-C2 grew the table) |
| M-A3 autorun hooks | Unattended in-game test batches (PARSEK_AUTORUN_*) | SHIPPED (#1305) |
| M-A5 harness core | The orchestrator: admission, staging, seam driving, budget kill, verifier chain, verdicts, coverage/flake ledgers | SHIPPED (#1307, #1316); UNMET-mission tail skip added 2026-07-25 (per-verb `SEAM_VERB_TAIL_ROLE`: after an unmet mission only `cleanup` verbs are driven, so an EVA-4-class world-mutating tail can no longer fire over a flight that never reached its envelope). Settings-sidecar baseline added 2026-07-26: `SetSetting` on a sidecar-tracked setting persists INSTANCE-WIDE and Parsek applies it over every loaded save, so S1.4's `mapRenderTracing=true` had pinned the per-frame render tracer on for every later run; run.py now writes a deterministic tracers-OFF baseline at stage AND at teardown, making tracer state a declared per-scenario property (a scenario that wants it adds its own SetSetting step). Anomaly sweep ANCHORED 2026-07-26: `grep_anomaly_tokens` was a bare substring search for each Tier-C token over the whole KSP.log, so any line that merely NAMED a token was a hit - S1.7's first flight reddened PARSEK-FAIL(anomaly) on a test diagnostic reporting `over=False` in a log with ZERO `phase=Anomaly` lines. The matcher moved into hlib and now requires the tracers' actual raise shape (`phase=Anomaly ... reason=<token>`, the one shape both `MapRenderTrace.EmitAnomaly` and `LedgerTrace.FormatAnomaly` produce). Same change adds a REPORT-ONLY `unlistedReasons` channel for the ANOMALY_TOKENS drift (see gates). `allowedAnomalies` misplacement promoted from WARN to ERROR the same day, checked over every `[expectations.<sub>]` table, with all 28 pre-existing specs relocated. Post-mission OUTCOME gate added 2026-07-26 (EVA-4 flight 3): the autopilot carve-out that made EVERY post-mission seam step non-gating was over-general - it dropped the ONE channel that observed a dead kerbal (`driver.steps[6].verdict=ERROR`, `allExpectedMet: false`, and `driverValidity: PASS` on the same run). New per-verb `SEAM_VERB_POST_MISSION_ROLE` (TOTAL over the implemented verbs, unit-gated): `outcome` verbs (the four M-C2 EVA verbs) gate via a new `missionOutcome` verifier row -> `PARSEK-FAIL(mission-outcome)`, `recording` verbs stay non-gating exactly as before so the carve-out's own rationale is preserved. Classified PARSEK-FAIL rather than driver-INVALID deliberately: a driver-stage failure preempts and SKIPS every verifier below it and is retryable, which would both discard the evidence and let an intermittent subject death retry into a PASS-with-a-flake-note |
| M-A6 provisioner | Reproducible pinned KSP instance (kRPC 0.5.4 + MechJeb 2.15.1 + KRPC.MechJeb 0.8.1 + built TestingTools) | SHIPPED (#1303/#1308/#1318) |
| M-B1 mission library | Pure mission state machines + kRPC runner (flights become deterministic, diagnosable instruments) | SHIPPED (#1313); hardened by the flyby campaign |
| M-B2 ledger oracle | Seam-declared action manifests -> expected career totals -> save diff (PARSEK-FAIL(ledger)) | SHIPPED (#1314); stock-award-pattern gate below |
| M-B3 ledger scripts | The L1 scenario six-pack | SHIPPED (#1324); LIVE-PROVEN 2026-07-23 (career fixtures file-constructed headlessly; 7/7 ledger scenarios green, now daily tier). Caveat recorded 2026-07-26: the ORACLE half of those 7 was genuine, but L1-passive-sandbox's and B10's in-game BATCH half executed zero tests under the old category - both re-flown green in `GameActionsHealth` on 2026-07-26 |
| M-C1 seam verbs batch 1 | InvokeRewind, AnswerMergeDialog, TimeJump, KscAction, SaveGame | SHIPPED (#1320/#1325) |
| M-C2 EVA verbs + missions | EvaExit/EvaBoard/PlantFlag -> crew/EVA/flag recording coverage | LIVE-PROVEN 2026-07-24; 18 implemented verbs, 11 reserved; verbs + pure deciders + hlib companions + EVA-1/2/3 specs land, both fixtures forged headlessly, all three scenarios flown green, live-prove list P1-P6 closed |
| EVA-4 atmospheric chute | EvaChuteDeploy (the kerbal personal parachute) + mission `eva4_atmo_chute` -> mid-flight atmospheric EVA branch, kerbal-owned atmospheric TrackSections, two-phase chute part events ON the kerbal, kerbal DOWN-alive terminal | LIVE-PROVEN 2026-07-24 (flight 2 full PASS); 19 implemented verbs, 11 reserved; all four first-flight pins closed (count 3, kerbalEVA token, semi-deployed rate measured -> descent budget trimmed 480 -> 240, kerbal lands alive), plus the K=2 window debounce + raw-alive CompleteOk conjunct hardenings. DE-LISTED from live-proven 2026-07-25 (the first full sweep red'd it: the kerbal's canopy cut itself mid-descent and the kerbal died) and FIXED HEADLESSLY 2026-07-26 from the archived log + decompiled KerbalEVA, no new flight: (b) a >3.5 m/s collision fires `On_stumble` from `st_semi_deployed_parachute` into `st_ragdoll`, and leaving that state calls `evaChute.CutParachute()` - closed by a bounded OBSERVED pre-chute standoff on `EvaExit` (`minStandoffMeters`, EVA-4 sets 30, debounced 2 polls, TWO non-fatal bounds - 8 s wall clock AND `standoffFloorAltMeters` 500, the latter load-bearing because the kerbal is unchuted and free-falling for the stage); (a) the MISSION cannot see the kerbal at all (its terminal is the handoff and its process exits before the EVA), so the closure is the harness-side `missionOutcome` gate plus an mlib handoff declaration. RE-PROVEN 2026-07-26 (flight 4, PASS on attempt 1, wall 409 s, all seven verifiers) with the closure verified STRUCTURALLY rather than by the green outcome - a live kerbal proves nothing about a dead one; see the runbook + residual in `todo-and-known-bugs.md` |

## Test cases (all 53 committed scenarios)

LIVE-PROVEN = at least one fully-unattended PASS with every verifier green.
The "Parsek surface verified" column is the reason the case exists.

### Live-proven (21)

| Test case | Tier | Parsek surface verified | Coverage cells |
|---|---|---|---|
| H6-route-rewind-timeline | daily | Route-rewind lifecycle rows, dormant classify + Tick materialize, kept-route reconciliation (Restore(cutoff) reconciliation-bundle path) | D9 reconciliation-bundle; D10 route-x-rewind; D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-24: first live run = FULL PASS attempt 1, all seven verifiers green, in-game batch perCategory=1 - the route-rewind wave's last automated acceptance item. Batch tally pinned WHOLE 2026-07-26: `failed=0 skipped=0` still accepted an EMPTY batch (`total=0 passed=0 ...`), so the pin is now `total=7 passed=7 failed=0 skipped=0 category=RouteRewindTimeline scene=FLIGHT`. That pin is DERIVED, not measured: the 2026-07-24 PASS was against a contract carrying only `failed=0 skipped=0`, so it proves those two; `total=passed=7` follows from the category's 7 scene-agnostic batch-allowed tests plus skipped=0; and `scene=FLIGHT` is inferred from the gloops-airshow fixture's Focusable/FLIGHT LoadGame route (the rule H5 and S1.4 measured on the same fixture), with no H6-specific evidence. Re-pin from the measured line the first time an H6 run archives a log |
| B2-lko-ascent | nightly | Ascent-to-orbit recording, orbital checkpoints, 6-booster parent-anchored debris children model | D1; D3 orbital-checkpoint; D4 atmospheric/exo-propulsive; D14 kerbin |
| B4-reentry-splashdown | nightly | Full-cycle recording (ascent/deorbit/reentry/splashdown intact), exo-ballistic sections, rails-warp recording | D1; D3; D4 +exo-ballistic; D14 kerbin/warp-rails |
| B5-mun-flyby | nightly | Cross-SOI cohesive coast recording (Kerbin->Mun->Kerbin), on-rails checkpoints across warp, warp-reseed seams | D1; D3; D4 +cohesive-cross-body-coast; D14 kerbin/mun/warp-rails. NO-1X CERTIFIED at HEAD config (flight 26: wall 465 s, warp audit exit 0) |
| B6-minmus-flyby | nightly | Same cells on the minmus axis | As B5 with D14 minmus. GATE: 20 km course-correct target predates finding 16d; guarded (arrival gate + impact terminal fail clean); re-target ~150 km only if it reds. **CONFIRMATION RE-FLY PAID 2026-07-25 (wall 359 s, all seven verifiers green):** B6's prior live proof predated the no-1x-coast aim-then-warp (4219832b6) and B6 shares the machine, the correction params AND the 4,000 s `transferBurnTimeoutSeconds` that B12 flight 1 proved cannot cover a Minmus-class correction node's 73,733 s wait - B6 was exposed and had simply not re-flown. It is ALSO the mission most exposed to B12 flight 2's coast warp-thrash (same long Minmus coast). Both shared-machine fixes landed with the B12 forensics and the confirmation flight flew them green on the flyby side, so B6's LIVE-PROVEN mark is honest at HEAD again |
| B7-duna-flyby | nightly | Multi-SOI interplanetary recording (Kerbin->Sun->Duna->Sun), 100,000x warp recording, SOI-count | As B5 with D14 duna/soi-count/warp-high. **GATE CLOSED 2026-07-25 - B7 AT HEAD IS INTERMITTENT.** The gate ("HEAD's 300 km target has not itself flown; the pass flew 50 km") was paid by flying it. Run 1: both attempts `MISSION-ASSERT-FAIL body='Ike' (expected 'Duna' or exit 'Sun')`, wall 747 s / 735 s. Run 2 (the first full sweep): attempt 1 INVALID on the same Ike capture, attempt 2 PASS, terminal PASS at `attempts=2 wallTotal=1564s`. So it is a FLAKY scenario, not a hard red - the approach always transits Ike's shell, but whether Ike is close enough to capture depends on its phase at arrival. The 300 km target was hit correctly (`pe=310089`); the inbound approach transits Ike's orbital shell and Ike captured the craft (`window[19] body=Duna alt=3,687,346` -> `window[20] body=Ike alt=897,085`). NOT an ORBIT-lane regression: `corrBudgetAnchorUt=none` (the correction re-anchor never engaged, so it ran main's bound) and `phaseWarpIssues=1` (the coast latch worked, which is why it reached Duna at all - every archived pre-fix B7 run stopped at `Kerbin to Sun` and never reached the target SOI). Needs a B7 SPEC decision - accept an Ike encounter as a legitimate Duna-system arrival, or aim to clear Ike's shell - deliberately not taken in the ORBIT lane. Forensics in `todo-and-known-bugs.md` |
| S0.5-live-record-discard | daily | Live record start/stop marker pairing + DiscardTree returns the store to zero (caught the orphan-sidecar leak) | D1 discard-rollback; D5 single-node; D14 |
| S0.6-live-record-commit | daily | Commit on top of the injected corpus without corpus loss (the save-hollowing guard class) | D5; D14; D16 sidecar-prec |
| S1.4-injected-playback | daily | 272-tree corpus injection, load, ghost map presence + polyline render with no anomalies | D6 basic-playback/ghost-map-presence/non-orbital-polyline; D16 sidecar-prec/sidecar-pcrf. Batch contract hardened 2026-07-26 (was `failed=0` only) and its PENDING-OPERATOR CLOSED the same day by a live flight: the loose `passed=[1-9][0-9]* skipped=[0-9]+` placeholder is replaced by `total=42 passed=40 failed=0 skipped=2 category=GhostPlayback scene=FLIGHT`. Evidence standing, PARTLY MEASURED: the run executed the LOOSE pin (the exact one was committed after it), so it proves total=42 / failed=0 / category / scene and passed>=1; the 40/2 split was read off that run's live log, which was not archived (`collectLogs: {ran: false}` on a PASS) and is not re-derivable from any committed artifact. The 2 skips are 1 structural (`AllowBatchExecution=false`, attribute-exact) + 1 fixture-determined self-skip; the other 10 conditional guards did not fire on this corpus. The pin STAYS - a wrong split reds loud with the numbers to re-derive from |
| H5-invariants-corpus | daily | The full synthetic corpus (306 recordings / 276 trees) loads intact and holds every recording invariant in-game | D14 sandbox/scene-flight; D16 sidecar-prec/schema-gate. Batch tally pinned WHOLE 2026-07-26 from the MEASURED 2026-07-19 line: `total=2 passed=2 failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT` |
| B10-career-passive-safety | daily | Fresh career + stock actions only = ZERO economy drift (the BUG-A science/funds corruption class), now with a batch that genuinely executes | D8 funds/science/reputation/recalc-from-ut0; D14 career/cold-load-ut0/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category: `total=4 passed=4 failed=0 skipped=0 ... scene=SPACECENTER`, MEASURED. Its earlier "green" runs executed ZERO tests (see the de-listing note in todo-and-known-bugs.md); this is the vacuity fix proven live. D16 schema-gate stays dropped - it was claimed over a save with zero recordings |
| L1-passive-sandbox | daily | Sandbox cold load moves nothing (recalc/orchestrator/patcher inert); the one scene- and mode-independent suppression-flag assertion actually runs | D8 recalc-engine/orchestrator/ksp-state-patcher; D14 sandbox/scene-ksc. RE-PROVEN 2026-07-26 in the corrected `GameActionsHealth` category (its first flight there): `total=4 passed=1 failed=0 skipped=3 ... scene=SPACECENTER`, MEASURED, and the 3 skips ARE the subject (SANDBOX has no pools). Ledger oracle green, hardDivergences=0 |
| B11-mun-orbit | nightly | COMMIT-IN-FOREIGN-SOI (the new D1 cell): a recording that ENDS parked in another body's SOI and is COMMITTED there - the commit path, the terminal classification, and the background-recording handoff for a tree whose terminal state is "in orbit around the Mun". Every other lunar/interplanetary case (B5/B6/B7) flies THROUGH an SOI and comes back or continues, so none of them reaches this surface. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `captureEnabled` param - ascent, transfer, TLI, corrections, warp policy all byte-identical to the 26 B5 flights; NEW is the four-phase tail PLAN-CAPTURE (MechJeb circularize-at-periapsis) -> CAPTURE-BURN (NodeExecutor, autowarp EXPLICIT; done evidence is a BOUND orbit, since a hyperbolic approach reads a NEGATIVE apoapsis) -> PARK (throttle cut, nodes cleared, SAS+RCS held, rails dropped to 1x, 180 game-s held dwell) -> ORBIT-COMMIT (the B-DOCK route-1 mid-mission seam CommitTree) -> ORBIT-COMMITTED. Leaving the target SOI anywhere in that tail is an ASSERT-FAIL, so B5's free-return cannot green this mission | D1 auto-record-launch + commit-in-foreign-soi (the NEW cell); D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/mun/warp-rails (D5 bg-recording was CLAIMED and then REMOVED 2026-07-25: `CommitTreeFlight` nulls both `backgroundRecorder` and `PhysicsFramePatch.BackgroundRecorderInstance` before returning, no assertion or token covers a handoff, and `settle_frames = 0` ends the mission on the commit frame - BDOCK-1 is the honest claimant of that cell). LIVE-PROVEN 2026-07-25 (flight 2 FULL PASS attempt 1, all seven verifiers green, wall 1,268 s, analyzer red=0): capture apoapsis flipped -1,560,099 -> +138,789 m at eccentricity 0.000127, all six assertions met, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED. Its coast issued the native warp ONCE with ZERO cancels, which is why the Mun lane never showed B12 flight 2's coast warp-thrash; the shared fix for that landed after this pass and does not alter any frame this flight took (a coast that never cancels a valid warp is unaffected). CONFIRMATION RE-FLY PAID (flight 3, 2026-07-25): B12 flight 3's periapsis-bound fix CHANGED this mission's flown profile - TARGET-FLYBY now warps to periapsis_ut - 900 instead of riding the rails flyby stair, and the COAST handoff stops the inherited warp - so a LIVE-PROVEN mission owed one flight on the changed profile. It flew FULL PASS again: wall 1,269 s, all six assertions met, capture eccentricity 0.000127 (flight 2 read the same number, so the profile change did not move the capture quality), and TARGET-FLYBY collapsed from 8,213 game seconds to 27 game seconds on 2 warp commands. The lane's re-fly debt is now clear on both axes. FLIGHT 4 (2026-07-25) FULL PASS, wall 1,271 s (`results/2026-07-25_0400_B11-mun-orbit.json`): the COUNT-PIN run - the first B11 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}. **FLIGHT 5 (2026-07-25) FULL PASS attempt 1, wall 1,270.195 s - the POST-REVIEW re-fly, and the first flight on which this mission's headline claim is actually VERIFIED.** Owed because the review pass added a `time_to_periapsis > 0` conjunct to the capture arming gate (a frame-level change to a path the green flights took) and because the new commit-terminal token needed its first live proof. Both landed: the token reads `terminalState=Orbiting terminalOrbitBody=Mun` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Mun` (the committed craft), 6 `Destroyed` (the radial boosters), 1 `Orbiting Kerbin` (the flameout-staged ascent core), matching the count-pin derivation exactly. GATED since 2026-07-26 (review round 2): a regression that drops one recording while adding a spurious one still reads 8, so the count alone cannot catch it - and until this round only the Mun row was actually required. Both specs now also require `terminalState=Orbiting terminalOrbitBody=Kerbin` and `terminalState=Destroyed terminalOrbitBody=(null)`, so each of the three topology CLASSES is asserted (the count still owns the total). Measured on the same fixture and launcher in `logs/2026-07-25_1216_B7-duna-flyby/KSP.log`: 8 terminal lines, 1 `Orbiting Kerbin` and 6 `Destroyed (null)`. Flight 1 (2026-07-24) flaked at CAPTURE-BURN on our own no-start watchdog colliding with MechJeb's 600 s pre-ignition WARPALIGN hold; fixed with an OBSERVED NodeExecutor.Enabled channel + a node-clock no-start classifier (forensics in todo-and-known-bugs.md) |
| B12-minmus-orbit | nightly | Same cells on the minmus axis (a thin alias over the same capture-enabled machine, exactly as B6 is to B5) | D1 auto-record-launch + commit-in-foreign-soi; D3 orbital-checkpoint; D4 atmospheric / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/minmus/warp-rails (D5 bg-recording removed 2026-07-25 for the same reason as B11 - nothing gates it). LIVE-PROVEN 2026-07-25 (flight 4 FULL PASS, all six mission assertions met, wall 580 s; flight 5 repeated it at wall 580 s and was the COUNT-PIN run - the first B12 pass carrying `verifiers.expectations.observed.recordings.count`, which read 8 and pinned the window to {8, 8}, `results/2026-07-25_0349_B12-minmus-orbit.json`; **FLIGHT 6 FULL PASS attempt 1, wall 580.826 s - the POST-REVIEW re-fly that first VERIFIES the lane's headline claim**: the new commit-terminal token reads `terminalState=Orbiting terminalOrbitBody=Minmus` for the parked craft, and the 8-recording topology is now legible instead of merely counted - 1 `Orbiting Minmus`, 6 `Destroyed` boosters, 1 `Orbiting Kerbin` core, matching the count-pin derivation exactly. GATED since 2026-07-26 (review round 2): the spec required only the target-body row, so a regression dropping the ascent-core recording and adding a spurious booster would still read 8 and still pass - both specs now also require `terminalState=Orbiting terminalOrbitBody=Kerbin` and `terminalState=Destroyed terminalOrbitBody=(null)`, measured on the same fixture and launcher in `logs/2026-07-25_1216_B7-duna-flyby/KSP.log` (1 and 6 lines). Owed because the review added a `time_to_periapsis > 0` arming conjunct that changes a frame the green flights took. NOTE the flight-6 lesson: its FIRST attempt red'd PARSEK-FAIL on the new token and looked exactly like "the commit records the wrong terminal body", but the flight had run the PREVIOUS DLL - harness flights use the provisioned `automation/stock-minimal` instance, and `dotnet build` only deploys to the dev instance. Run `provision.py --profile stock-minimal` after any C# change; see the note in `.claude/CLAUDE.md`): capture eccentricity 0.00026, PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED, and the two shared-machine warp fixes this mission's own forensics produced both held - COAST-TO-TARGET flew 194,543 game seconds in 26 wall seconds (ratio 7,535) on 3 warp commands, and the periapsis-bounded TARGET-FLYBY armed the capture on the orbit's clock instead of blowing through it. At 580 s wall it is the CHEAPEST of the two orbit cases (B11 costs 1,269 s), which makes the Minmus axis the better default for a fast regression check of the shared capture machine. PRIOR FLIGHTS (the forensics that produced three of the lane's four findings): FLIGHT 3 FLOWN 2026-07-25: the coast fix WORKED (COAST-TO-TARGET 26 wall / 194,704 game = ratio 7,543 on 3 warp commands, down from never-finishing) and the run reached a capture burn. THIRD SHARED-machine defect found, named at a glance by the new warpUtilisation block: TARGET-FLYBY read 2 wall / 8,213 game (ratio 5,341, 2 commands) and blew straight through periapsis - entered at ut 268,934.5 / alt 1,902 km descending at -236 m/s, PLAN-CAPTURE at ut 277,147.5 / alt 41,609 m CLIMBING at +92 m/s. Two compounding causes: the COAST -> TARGET-FLYBY handoff emitted NO warp cleanup so the craft crossed the SOI still running the coast's RAILSx10000 warp (the first flyby poll alone advanced 3,907 game seconds), and capture mode fell through to a rails flyby stair floored at flybyWarpFactor whose distance term knows nothing about the periapsis CLOCK. The late burn produced a bound but wildly eccentric 325 x 5.3 km orbit grazing Minmus, CORRECTLY rejected by the capture window as an under-burn. FIXED: `mlib.capture_flyby_warp_target` - the only legitimate warp target inside the target SOI is `periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS` (900, covering our arming + plan, MechJeb's halfBurnTime ignition lead and its 600 s pre-ignition hold), read from `Orbit.TimeToPeriapsis` via the opt-in `read_periapsis`; past the bound or with the clock unreadable the machine does NOT warp (fail closed, 1x); and the handoff now stops the inherited coast warp. FLIGHT 2: the correction fix WORKED (both rounds cleared, `rounds=2`, round 1's 73,720 game-second aim-warp ran as ONE continuous warp from ut 475.3 to 74,195.7 with no cancel), then INVALID `mission-budget-expired` inside COAST-TO-TARGET at ut 225,990 with 41,655 game seconds still to go. SECOND SHARED-machine defect found: the coast derives its native warp target from `time_to_soi` EVERY poll, and KSP cannot read the patched-conic SOI time while re-patching under a warp ramp - measured over flight 2's COAST frames, tts was finite on 2,451 of 2,451 unwarped frames and NaN on 1,154 of 1,161 warping ones - so the machine cancelled its own warp on the blind read and re-armed on the next unwarped poll: 3,603 `warp_to_ut` issues, 3,602 `cancel_warp`, a rails rate that never escaped ~2.7x, ~40 game-s per wall-s. METASTABLE, which is why it had never been seen: B11 flight 2's Mun coast issued the warp ONCE (0 cancels, 30/30 warping frames finite) and locked in at RAILSx1000. FIXED: `mlib.coast_native_warp_hold` - the target is an ABSOLUTE UT, so a blind read UNDER WARP HOLDS the armed command; only a blind read with the game NOT warping is evidence the encounter is gone. Plus a NAMED `coast-warp-thrash` fast-fail past `MAX_PHASE_WARP_ISSUES` (500; a healthy coast issues 1) and a per-phase `warpUtilisation` block in the mission result whose `gameSecondsPerWallSecond` names this class in one line. Budget RE-DERIVED bottom-up from flight 2's measured spans (~2,280 nominal / ~3,100 worst) and deliberately NOT raised - 4200/4700 stand. FLIGHT 1 (2026-07-25): MISSION-FLAKE at CORRECTION-BURN (wall 286 s), UPSTREAM of the capture tail and in the SHARED B5/B6 machine, not in anything B12-specific. ROOT CAUSE: the no-1x-coast PR (4219832b6) turned the DIY correction burner into AIM-THEN-WARP (aim, then natively warp to `node_ut - nodeArrivalMarginSeconds`, then throttle) but left the phase bounded by `transferBurnTimeoutSeconds` - a GAME-time budget that the warp itself spends. Measured: B11/Mun needs 2,994 s of its 4,000 s budget for that wait (75%, passes), B12/Minmus needs 73,733 s (entry ut 475.3, MechJeb node at ut 74,208.3) and can NEVER pass. FIXED in the shared machine: `mlib.correction_budget_expired` suppresses the budget while an aim-warp is in flight and re-anchors it at the warp ARRIVAL (the same seam that already re-anchors the no-start clock - and a game-time bound cannot bound a STALLED warp anyway, which advances no game time; the runner's warp-stall watchdog + the WALL budget own that), and `mlib.classify_correction_timeout` NAMES the expiry (`correction-burner-no-start` / `correction-burn-incomplete`) so it can never again ride the generic timeout. Every correction round give-up now also carries a `corrGiveup` reason on the machine-diff line. B11 flight 2 passed on this same machine, so no budget number needed changing. Also inherits B11 flight 1's CAPTURE-BURN fix unchanged and `read_node_executor` is on; wall budgets 4200/4700 (raised from 3600/4100 for MechJeb's MEASURED ~600 s pre-ignition hold). Same PROVISIONAL pins; the Minmus-specific one is `captureBurnTimeoutSeconds` 200000 (its SOI edge is ~2,187 km up but arrival speeds are ~5x lower than the Mun's, so the executor's SOI-entry -> periapsis autowarp coast is ~10-20 game hours, not 1-3) |
| B13-mun-landing | nightly | LANDED-ON-ANOTHER-BODY (the new D1 `commit-landed-foreign-body` cell): a recording that ENDS on Mun soil and is COMMITTED there - the `Landed` terminal classification for a foreign-body tree, SURFACE-class TrackSections OFF Kerbin (the environment classifier's AIRLESS `Approach -> Surface*` path, which cannot occur where an atmosphere classifies first - hence the previously unclaimed D4 `surface-stationary`, the class THIS flight measured), the landing-leg part events, and the landed-vessel ghost / playback surface. B11/B12 end in ORBIT around a foreign body and B1/B4 land on KERBIN, so nothing else in the suite reaches this end state. Machine: the LIVE-PROVEN `mlib.b5_decide` with the new `landingEnabled` param on top of `captureEnabled` - PRELAUNCH through PARK byte-identical to the five B11 flights; NEW is the tail DESCENT (MechJeb `LandUntargeted`; warp-PASSIVE because MechJeb's landing states own the warp through the shared `Core.Node.Autowarp` flag the runner sets explicitly; `DeployGears` true, `DeployChutes` FALSE because the Mun is airless, `RcsAdjustment` false because the stage has no thruster blocks) -> LANDED-SETTLE (throttle cut, autopilot released, SAS held, rails 1x, a held settled dwell gated on target body + landed situation + BOTH speed components) -> SURFACE-COMMIT (the same route-1 mid-mission seam CommitTree) -> SURFACE-COMMITTED. FOUR named DESCENT give-ups on top of the budget: `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off `LandingAutopilot.Enabled`; a landed frame exits DESCENT BEFORE the supervisor runs, because MechJeb disables its own module on the landed frame and a perfect landing must not read as a dead autopilot), `landing-no-progress` (+ a separate `altitude-unreadable` name), `landing-touchdown-timeout`, `landing-vessel-lost` (a crash reads as neither a timeout nor a success) | LIVE-PROVEN 2026-07-25 FOR THE HAPPY PATH: FULL PASS attempt 1, wall 2,747.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Mun`, airless `Approach -> SurfaceStationary`, measured count 8 (window PINNED). Most expensive scenario in the suite at 2,825 s harness wall. NOT live-proven: none of the four DESCENT give-ups nor `landed-never-stable` fired on either flight - they carry unit + fly-loop coverage only (see the COVERAGE HONESTY bullet under roadmap item 3) |
| B14-minmus-landing | nightly | Same cells on the minmus axis (a thin alias over the same landing-enabled machine, exactly as B12 is to B11), except the D4 surface class: B14 claims the previously unclaimed `surface-mobile`, which is what its own flight measured, and B13 keeps `surface-stationary`. NOT redundant for a LANDING: Minmus's ~0.05 g against the Mun's ~0.17 g makes MechJeb's descent-speed policy fly a long slow low-thrust settle instead of a short suicide burn, and the flats make an untargeted landing far more likely to end on level ground | LIVE-PROVEN 2026-07-25 FOR THE HAPPY PATH: FULL PASS attempt 1, wall 2,083.9 s, all verifiers green. `terminalState=Landed terminalOrbitBody=Minmus`, airless `Approach -> SurfaceMobile`, touchdown -0.25 m/s vertical / 0.06 m/s horizontal, measured count 8 (window PINNED). The CHEAPER landing axis, so the better default regression check of the shared landing tail. Same NOT-live-proven list as B13: the give-up ladder never fired here either |
| B15-eve-flyby | nightly | SECOND interplanetary destination, and the first INWARD transfer: multi-SOI recording Kerbin->Sun->Eve->Sun on the SAME machine and the SAME five params B7 flies. STATED SKEPTICALLY, as the spec does - this largely RE-EXERCISES B7's cross-SOI surface at a different body (a D14 MULTIPLIER, not a new mechanism); what it adds is a stable interplanetary regression subject, since B7 itself is FLAKY (Ike grabs its 300 km approach on roughly half of sweeps), plus the cheap prerequisite for B16. THE THING IT DELIBERATELY DOES NOT CLAIM: Eve's 90 km atmosphere - the one genuinely new Parsek surface Eve could reach - is not touched (the pass is aimed at 1,000 km), would not record if it were (the recorders early-return on `isOnRails` / `packed`), and is NOT ASSERTABLE TODAY at all because no emitted log line pairs an environment class with a body name; a follow-up aerobraking variant needs `body=` added to the `TrackSection started:` format string first, mirroring the B13 `terminalOrbitBody` fix | D1 auto-record-launch; D3 orbital-checkpoint; D4 atmospheric (the KERBIN ascent's cell, NOT an Eve one) / exo-propulsive / exo-ballistic / cohesive-cross-body-coast; D14 kerbin/eve/soi-count/warp-rails/warp-high. NO NEW REGISTRY VALUE - D14 already carries `eve`, so this lane triggers no growth-rule obligation. FLOWN. The "no new mlib code" claim was REFUTED by flights 1-3 and the lane now carries ONE argued param key plus three param-gated machine changes (`EveLaneIsAParameterChangeTests` rewritten, not deleted, to pin the single-key delta). (1) THE INWARD TRANSFER - ANSWERED, and the answer was not the one the spec anticipated. MechJeb plans an inner window fine; what it gets wrong is the EJECTION SIZE, because `DeltaVAndTimeForInterplanetaryTransferEjection` computes the post-burn speed at the park's SEMI-MAJOR AXIS and applies it at whatever radius its ejection geometry picks. On a circular park those coincide; on the 0.085-eccentric park MechJeb's own sloppy high-altitude circularization leaves, they do not, and the SAME planner priced the SAME ejection at 652.843 m/s from that park against 775.873 m/s from flight 5's round one, a MEASURED 123.0 m/s shortfall - the heliocentric leg's perihelion sat 2.46e9 m above Eve's aphelion, so no encounter was ever geometrically possible and `nextBody` never once read Eve. mlib still has no direction anywhere; the defect was a THRESHOLD and a MISSING ACTION, not a sign. Fixed by `parkTrimEccMax` (circularize-at-apoapsis before planning) plus a plan-time `reachesTargetOrbit=` verdict. (2) GILLY, and the answer is that it is NOT Ike: 126 km SOI vs 1,050 km (~69x smaller cross-section), 17-57% of the SOI radius vs 6.7%, 12 deg inclination vs 0.2 deg. Residual risk non-zero; a Gilly capture is a NAMED ASSERT-FAIL, deliberately not whitelisted - and it did NOT occur on the green run. GREEN on flight 7 (2026-07-26): full scenario PASS on attempt 1, every verifier PASS/SKIPPED, analyzer red=0, 86 `nextBody=Eve` reads, flyby periapsis 22,032,532 m against the 100,000 m floor, exit to Sun. Both correction rounds flew (378.32 m/s re-aim then a 3.35 m/s trim), which needed `maxCorrectionDvMps` 200 -> 450 - the 200 was a MOON-transfer calibration, and an interplanetary arrival-PHASE fix is legitimately dearer (measured affordable: ~1,944 m/s remained at Sun-SOI entry). PINS CLOSED: recordings count {8, 8}, mission wall 1,236 s, scenario wall 1,286 s, ejection-window wait 11,827,993 game s (0.80 synodic) |
| M1-mission-loop-unit | daily | The mission-loop PLAN against LIVE stock ephemerides: cross-tree partner-journey link discovery + include/normalize mutation + the REAL MissionLoopUnitBuilder shared span clock landing member windows on the recorded dock/undock UTs; joint landing+station arrival hold (with the landing-only byte-identical-off control); the resonant Jool inner-three configuration hold; incommensurate Bop failing CLOSED to faithful | D11 partner-journey/land-dock-dual-constraint/arrival-hold/multi-moon-config-hold/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26 - the first spec to claim ANY D11 cell (the dimension was 0/18). LIVE-PROVEN 2026-07-26, and it EARNED ITS KEEP ON FLIGHT 1: batch tally exact (`total=12 passed=5 failed=0 skipped=7`), but its `recordings.count = {0,0}` pin red'd on a real Parsek-side defect - in-game tests driving the real `RecordingStore.CommitTree` left orphan sidecars the memory-only tree teardown could not reach. Fixed via the shared `InGameTestSidecarReaper`; re-flown FULL PASS with the pin untouched. Gates the PLAN, not the playback: no ghost, icon, cycle boundary or elapsed period is observed |
| M2-periodicity-solver | daily | The periodicity SOLVER against LIVE stock ephemerides: the re-aim feasibility scan over a pinned synodic period, UvLambert transfer synthesis that must actually encounter the target, the window schedule, the eccentric/inclined stage-A un-projection + stage-B tof band (Moho / Eeloo), heliocentric-parking r1==park-end, and deterministic clean declines at the band edge | D11 reaim-lambert/eccentric-inclined-targets/heliocentric-parking-departure/fail-closed-to-faithful; D14 sandbox/scene-ksc. NEW 2026-07-26, M1's sibling (separate spec because run.py's `_driven_category` reads only the FIRST RunTests category and a per-category line with no aggregate is a defined fault - one batch per spec). LIVE-PROVEN 2026-07-26, full PASS attempt 1; tally MEASURED and deliberately NOT total=passed: `total=11 passed=7 failed=0 skipped=4` (1 FLIGHT-scene member + 3 `AllowBatchExecution=false` diagnostics). Gates the SOLVER, not the playback |
| S1.6-render-parity | daily | The FIRST cell that gates PLAYBACK rather than recording: drives the in-game `GhostMap` batch so the production recorded-vs-rendered parity oracle (`RenderParityOracle` + `MapRenderProbe.ComputeFaithfulOrbitParity` / `ComputeSynthesizedConicParity`) actually runs unattended, tracer pinned on, `allowedAnomalies = []`. Anti-vacuity is MANDATORY here: a pinned whole tally plus two `[TestRunner]` measurement lines emitted only after a real diff ran on live ghost geometry | D6 recorded-vs-rendered-parity (new registry value); D14 sandbox/scene-flight. LIVE-PROVEN 2026-07-26: first flight = PASS, every verifier green. `total=25 passed=14 failed=0 skipped=11 category=GhostMap scene=FLIGHT`; the 11 skips are 9 TRACKSTATION scene-eligibility + 2 documented loop-icon self-skips. Negative control measured 1049421 m against a 1927 m tolerance (~545x), so the zero-drift assertion provably can still fail. FLOWN TWICE: flight 1 (run `2026-07-26_0950`) MEASURED the tally while the spec still carried the loose `passed=[1-9][0-9]*` conjunct, so the exact line was a transcription; flight 2 (run `2026-07-26_1207`, PASS, expectations mismatches=0, anomalySweep hits=[] unlistedReasons=[], log archived at `logs/2026-07-26_1207_S1.6-render-parity/KSP.log`) ran the spec AS COMMITTED and is what actually EVALUATED the exact pin, both measurement lines and both forbidden patterns. Caveat on flight 2: the instance's deployed DLL was a sibling worktree's build whose GhostMap surface is identical to main's (25 attributes, 16 FLIGHT + 9 TRACKSTATION in both), so the pin is not yet evaluated against a main-built DLL. Does NOT cover Recording.Points / TrackSection frames, the flight-scene ghost mesh, anything across time (one frame per assertion), or re-aim solve correctness |
| S1.7-maprender-parity | daily | S1.6's follow-up over the STRONGER category: drives the in-game `MapRender` batch (22 tests, all Scene = FLIGHT) - the parity baselines with the typed PhaseChain spine driving, multi-body concurrent ghosts, the re-aimed-loop lens distinction, and the descent / re-stitch / dock-undock / overlap / parent-anchored / BG-on-rails spine cells. Anti-vacuity accounts for the SINK TRAP: four MapRender test files install `ParsekLog.TestSinkForTesting`, which diverts rather than tees, so the obvious candidate (the three-oracle flag-on baselines) can never reach KSP.log. Pins the two arms that do: both `MultiBodyConcurrent` lines (`sampled=True skip=(none) hasMeas=True over=False`, the Mun arm doubling as the cross-body-leak proof) and the re-aimed-loop line | D6 recorded-vs-rendered-parity; D14 sandbox/scene-flight - deliberately NO new registry value (depth on an axis S1.6 opened, not breadth). LIVE-PROVEN 2026-07-26: `total=22 passed=21 failed=0 skipped=1 category=MapRender scene=FLIGHT`, the single skip being the `AllowBatchExecution = false` high-warp canary; zero scene-eligibility attrition. Negative control 1319093 m against 2701 m (~488x). Its first flight also EXPOSED the anomaly-sweep false positive (below) |

### Committed, not yet live-run (14)

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| B1-pad-hop | nightly | Auto-record-on-launch, atmospheric TrackSections, and a genuinely CHUTE-BORNE ground-arrival recording: the two-phase ParachuteSemiDeployed -> ParachuteDeployed part events on the craft's own parachuteSingle (D7 chute-two-phase, claimed 2026-07-25) | DE-LISTED from live-proven 2026-07-25. The 2026-07-19/20 PASSes proved the FLIGHT, not the CHUTE: their recordings carry ZERO Parachute* part events, and the DOWN terminal - which gated only on the machine's own COMMANDED chute latch - awarded a ~300 m/s terminal-velocity impact the "chute-deployed impact" success end. Root cause (decompiled ModuleParachute + two flights of evidence, forensics in todo-and-known-bugs.md): the fixture's parachuteSingle persists `automateSafeDeploy = 0` (open only while SAFE) and stock DeploySafe never reads SAFE at terminal velocity in dense air, so an ALTITUDE-triggered arm sat inert in ARMED forever. FIXED: arm at the apoapsis crossing while still slow (the technique EVA-4 flight 2 live-proved on this exact fixture and craft), and gate BOTH the DOWN terminal and the new `craftCanopyObserved` assertion on the OBSERVED kRPC ParachuteState. Its next nightly run IS its re-prove; three things it pins are P1 the final full-canopy leg to the ground, the one segment EVA-4 never times because it always hands off in mid-air (budgets DERIVED from EVA-4 flight-2 measurements rather than guessed: descent 240 -> 360 s, mission 600 -> 900 s, wall 900 -> 1320 s; the first draft's 600 s descent assumed a ~30 m/s semi-deployed crawl and was wrong by ~8x - the semi-deployed craft sinks at up to -236 m/s, and chuteFullDeployAltMeters was raised 1000 -> 2500 to match EVA-4's live-proven value because the full canopy needs 894 m just to brake), P2 which end it reaches - computed touchdown is ~8-9 m/s and the parts' own crashTolerance values predict fins and booster destroyed with the pod intact, so expect LANDED with debris, DOWN accepted as the fallback, P3 the recordings count on the chuted profile |
| BDOCK-1-station-interceptor | nightly | FIRST two-vessel flight (18-phase machine): cross-tree Dock branch, authoritative onVesselsUndocking split, RouteConnectionWindow recorded-delta contract (the new `Route window delta:` line), same-craft-twice launch identity. Flight-1/2 wall budgets re-timed; flight-3 lesson (STATION-SEPARATE / INT-SEPARATE) + flight-4 lesson (two-step SEPARATE: drop the spent lifter AND ignite the orbital engine, thrust-verified, cap 2) both live-confirmed through RENDEZVOUS on flight 5; flight-5 lesson (MATCH-VELOCITY kill-rel-vel retargeted XFromNow ~15 s lead + bounded 600 s give-up + per-frame diagnostics + one-shot dropped-target re-acquire); flight-8 lesson (prox-ops rule: abort the pending kill-rel-vel node executor at DOCK entry before the docking AP owns the ship, else it rails-warps + packs the port target null + NREs); flight-9 lesson (core.target one-Update sync trap: stagger the docking-AP enable one poll after the port target); flight-10/11 lesson (prox-ops observability [angular_velocity/sas/rcs/docking_ap_status + per-frame DOCK diag line] + attitude hold [SAS+RCS after each separation and at DOCK entry] + LIVENESS watchdogs [budgets bound SLOW, watchdogs bound BROKEN: DOCK enable-never-took / died-mid-approach / no-progress fast flakes, TRANSFER stall fast flake, bounded dropped-target re-arm x3]). flight-13 ROOT CAUSE (behind every dock failure since flight 7): pre-`launch_vessel`-reload PART handles are stale - the reload destroys every Part, so the captured docking-port handle resolves to a destroyed part and assigning it silently CLEARS the target; VESSEL handles survive (P9 answered). Fix: resolve port + docking-state + transfer tanks LIVE at call time. Flight 13's liveness layer fast-flaked in 10 s with the named E1a reason (wall 2133 s) and pinned this. Flight 16 (2026-07-24): MISSION-OK END TO END (launch, separate, mid-mission commit seam, launch_vessel, rendezvous, hard dock, LF 40 + mono 15 transfers, undock, TERMINAL) - and the verifier chain caught the FIRST mission-machinery-found Parsek recording defect: analyzer RED, INV4-PARTEVENT-PID x13 on the Station recording d5355cc6. Root cause: the launch_vessel FLIGHT->FLIGHT reload is classified as a quickload (stale vesselSwitchPending), and RestoreActiveTreeFromPending's NAME fallback adopted the fresh-rollout Interceptor (same .craft, same "Kerbal X" name, different Vessel.id) and PID-remapped the Station recording onto it, so the whole Interceptor flight recorded into the Station recording with foreign craft-baked part pids. FIXED Parsek-side: QuickloadResumeMatchGuard (fresh-rollout pid + launch-guid gates in the restore match loop); forensics in todo-and-known-bugs.md flight-16 entry | LIVE-PROVEN 2026-07-24: flight 17 on the guard build = MISSION-OK + analyzer red=0 (the QuickloadResumeMatchGuard fix verified on a clean two-tree save; the one residual red was the spec's own dock token - docking MERGES trees, Parsek logs 'Tree merge created: type=Dock', only splits log 'Tree branch created'); flight 18 = FULL PASS, all seven verifiers green, fifth consecutive hard dock. Re-tiered nightly. 18-flight campaign, zero manual sessions |
| FORGE-bdock-station | operator | (Not a Parsek-surface test) FIXTURE-FORGE: launch_vessel the docking Kerbal X onto the pad + SaveGame -> stamps the bdock-station-pad fixture headlessly (replaces the operator fixture flight) | None - runnable now on a provisioned instance; harvest tool normalizes the output |
| FORGE-eva3-pad | operator | (Not a Parsek-surface test) FIXTURE-FORGE (EVA-3 sibling): launch_vessel the Kerbal X onto the pad with THREE named crew + SaveGame -> stamps the eva3-pad-3crew fixture headlessly. Uses the review-follow-up-2 crew (by NAME) + launch_site plumbing | DONE 2026-07-24: forge run + `harvest_bdock_station.py --target-name eva3-pad-3crew` produced the committed eva3-pad-3crew fixture, and EVA-3 flew it to a full PASS (the Kerbal X pad-EVA reachability caveat did NOT materialize) |
| FORGE-eva2-lko | operator | (Not a Parsek-surface test) FIXTURE-FORGE, the FIRST ORBITAL one (mission `forge_lko`): boots the SAME bdock-forge-base, launch_vessel the Kerbal X with TWO named crew (Valentina + Bob), then flies the LIVE-PROVEN B-DOCK Interceptor-leg shape - MechJeb ascent, circularization with node-executor autowarp EXPLICIT (flight-12 lesson), the two-step separation contract (drop the spent core AND ignite the orbital stage, thrust-verified, cap 2), then a PARK phase that cuts throttle, clears nodes, holds SAS+RCS and requires a HELD stable ~100 km circular orbit (pe >= 75 km, tumble <= 0.05 rad/s) before SaveGame. Crew is gated ON THE PAD (crew_count >= minCrew, fail-closed on the -1 unread sentinel) so an uncrewed stamp flakes in 300 s instead of after a 10-minute flight. autoRecordOnLaunch pinned false so the fixture carries no recordings / trees / ledger state (the stamped .sfs does keep an inert populated `SCENARIO{name=ParsekScenario}` node - `gameStateEventCount=18` + one MILESTONE_STATE row - which is what suppresses PreParsekBackup at load) | DONE 2026-07-24: forge run = MISSION-OK / PASS, 268 s wall, full profile PRELAUNCH -> LAUNCH -> ASCENT -> CIRCULARIZE -> SEPARATE -> PARK -> ORBIT; harvested with `harvest_bdock_station.py --target-name eva2-lko-crewed --expect-situation ORBITING` (the harvest's new optional situation gate, added for this orbital harvest); the `eva2-lko-crewed` fixture is COMMITTED and EVA-2-orbital-board flew it green on its first flight |
| S1.5-rewind-loop | nightly (RE-TIERED from operator 2026-07-26) | TimeJump-past-EndUT spawn, then rewind-strip-respawn cycle observables | First scheduled nightly run IS its live-prove. Its two operator-tier reasons were both FALSE at HEAD: (1) "the seam has no flight-entry verb" - `LoadGame` IS one (`TestCommandLoadGame.DecideLoadRoute` FOCUS route -> `StartAndFocusVessel`), and `fixtures/saves/gloops-airshow` carries `activeVessel = 1` so it lands in FLIGHT, which EVA-1/2/3 + S0.5/S0.6 have been exploiting nightly since 2026-07-24; (2) the "do not schedule before the integration-fixes PR merges" TimeJump dependency - PR #1322 is MERGED (eb94607dd). Still PENDING-OPERATOR for the crew-re-reservation / resource-reset asserts (sandbox host, no career fixture) |
| S4.1-rewind-merge | nightly (RE-TIERED from operator 2026-07-26) - **seam fix PROVEN 2026-07-28; now PASSES but FLAKE-QUARANTINED (1-in-2) on a second finding** | Full re-fly cycle: InvokeRewind a crashed slot, merge-dialog fold, corpus survival, read-back guard | First scheduled nightly run IS its live-prove. Same corrected flight-entry premise as S1.5. **It is also the DEDICATED rewind-then-teardown case** (rewind, conclude, never re-fly), which is exactly the reproduction for `R1-EMPTY-PROVISIONAL` - the finding R1 flight 2 exposed - so it carries `expectedFail.bugId = "R1-EMPTY-PROVISIONAL"` with `subkind = "expectation"`: on a Debug DLL it demotes to EXPECTED-FAIL rather than redding the nightly, a DIFFERENT failure still reds as PARSEK-FAIL, and the day the finding is fixed it reports XPASS and the keys must be deleted. Beyond that the surviving caveat is PENDING-VERIFIER, not pending-operator: the supersede-relation / tombstone asserts under `[expectations.rewind]` are RESERVED (evaluate_expectations records them SKIPPED) until the M-C2 rewind save-parse verifier lands, so what it gates today is the four re-fly log contracts + the recording-count floor . **BLOCKED 2026-07-28 (run `2026-07-28_1521`): `verdict=INVALID`, `driverValidity FAIL subkind=driver-verdict-mismatch`, wall 366 s, IDENTICAL on both attempts - deterministic, not flake.** `AnswerMergeDialog` times out at its 120 s budget (`reason=answer-timeout`) because S4.1 flies nothing between the rewind and the conclusion, so the tree exits as Limbo and the merge dialog spawns on the DEFERRED POST-transition path (`Showing deferred tree merge dialog in SPACECENTER`) which the seam cannot answer - it drives and answers only the PRE-transition `SceneExitInterceptor` dialog. `DecideAnswerCompletion` behaved correctly: it reported `AnswerTimeout` (unapplied) rather than falsely claiming a committed merge, exactly as its doc-comment anticipates. ROOT CAUSE is deeper than the dialog path: no `ParsekScenario.OnSave` runs between the in-memory marker write and the driven scene change, so SPACECENTER `OnLoad` reads `Marker loaded: none`, `LoadTimeSweep` discards the provisional (`Zombies discarded=1`), the session ends `<cleared>`, and the dialog that appears is a PLAIN whole-tree merge dialog which the seam's `FindReFlyMergePopup` cannot match because it is gated on `markerLive`. **FIXED the same day in the SEAM, not the product** (`S4.1-DEFERRED-DIALOG`): `AnswerMergeDialogImpl` now calls `SceneExitInterceptor.SafeWritePersistent(SPACECENTER)` before its `LoadScene`, because the raw `LoadScene` modelled a scene exit no stock UI route performs - stock `saveAndExit` saves BEFORE the prefix fires, and `SafeWritePersistent` exists for the stock routes that do not. Same error class as the R1 fixture omitting `RECORDING_TREE isActive=True`. The sweep discarding the provisional was NOT a defect: an unpersisted marker plus a NotCommitted provisional is indistinguishable from a crash mid-re-fly, and discarding is the designed recovery. Four spec lies had to be corrected with it: the `expectedFail` keys DELETED (their signature no longer exists in the code, so they could only ever mask an unrelated failure), `AppendRelations outcome=refused-unflown-provisional` ADDED as a required contract, `supersedeRows` flipped `min = 1` -> `max = 0` (as written it would have red S4.1 for CORRECT behaviour once the M-C2 verifier landed), and the `supersede-relation` D9 claim moved to R1 while `head-tip-split` moved to nobody and is now honestly uncovered. **RUN `2026-07-28_1932`: PASS on attempt 2, INVALID on attempt 1 (`flakedThenPassed`, 237 s).** The seam fix is PROVEN on both attempts - `SafeWritePersistent` fires, `Marker loaded: sess_...` replaces `Marker loaded: none`, the sweep reports `Marker valid=True; spare=1 discarded=0` instead of `discarded=1`, and the newly-required `outcome=refused-unflown-provisional` contract fires. The save is re-staged from template between attempts, so the pass is from a clean fixture. The RESIDUAL is a different and more interesting cause, filed as `S4.1-IDLE-DISCARD`: the scene-exit idle-on-pad auto-discard (`TryAutoDiscardIdleActiveTree`) tears the re-fly tree down WITHOUT stash while the marker is still live, so no pending tree and no dialog ever exist and the verb waits 120 s for something unreachable. S4.1 sits exactly on the idle boundary (rewind to PRELAUNCH, fly nothing), so it lands on either side run to run. Currently flake-quarantined (`rate=0.75 over 7d`, spanning pre-fix history). NOT trustworthy as a nightly gate until the product question in that entry is settled. **Consequence for the cadence: it is scheduled (`CADENCE_TIERS` maps nightly -> `(daily, nightly)`) and now burns ~366 s a night for no verdict**, and because INVALID is a driver/tooling event it fails QUIETLY rather than redding the sweep. Its `expectedFail` keys STAY FOR NOW on the one honest ground - the run never reached PASS, so the predicted XPASS is untested - but a subsequent review argues for removing them outright, because the signature they name no longer exists in the code, so they can never match and would instead demote an unrelated expectation-subkind failure under a resolved bug id. See the correction in todo-and-known-bugs.md; an earlier claim that `SupersedeCommit.cs:1124` gave a second reason to keep them was WRONG (that Error branch is guarded by non-in-place, and S4.1's marker is in-place). `expected-fail bugId=R1-EMPTY-PROVISIONAL matched=False` confirms the taxonomy refused to let a driver failure shelter under a Parsek bug id |
| R1-rewind-loop-flown | operator (**PROMOTION NOW EARNED, not yet applied** - see the FLIGHT 4 note) | FIRST rewind cycle driven from a REAL FLOWN flight: the delegated live-proven B2 ascent machine, a mid-flight CommitTree + StopRecording + RecordingState issued through the NEW verb-agnostic seam bridge (`ACTION_PARSEK_SEAM_COMMAND`), the dispatcher's `recording-active` gate carried as an OBSERVED precondition (`recorderIdleBeforeRewind` reads `recording=false` off a RecordingState reply before the rewind is commanded), then a real Rewind-to-Separation from FLIGHT judged by an OBSERVATION - the game clock RUNNING BACKWARD (`clockRewound`, corroborated by `vesselStateChanged`), never by InvokeRewind's own OK (which rides as one strictly-additional `rewindSeamAccepted` row). Also the first mission to prove a mission can drive ANY seam verb mid-flight, not only CommitTree | FLOWN ONCE (2026-07-26, run `2026-07-26_2212`): `INVALID(autopilot-flake)`, wall 520 s / 2 attempts - and it EARNED ITS KEEP ON FLIGHT 1 by finding a real ordering defect in the mission. PROVEN LIVE first try: the generalized seam path drove `CommitTree` mid-flight (`treeCommittedBeforeRewind value=OK met=True`), the delegated ascent reached ORBIT (ap 84,051 / pe 75,784), and the SUB-ID SCHEME WORKED (`id=0003.commit` and `id=0003.rewind` are separate commands in KSP.log - no dedupe swallow, no advance on the wrong OK). FAILED on `reject id=0003.rewind cmd=InvokeRewind reason=recording-active`: `TestCommandDispatcher` refuses InvokeRewind while a recorder is live, and although `CommitTreeFlight` stops the recorder and nulls both handles, `TryRestoreCommittedTreeForSpawnedActiveVessel` starts a fresh `promotion` recording on the surviving stage 14 ms after the commit returns OK. FIXED by adding STOP + RECORDER-IDLE phases (see todo-and-known-bugs.md). NOT the cause, and now cleared: `rp_b9_root` resolved fine (`Keeping session-prov rp=rp_b9_root`). Attempt 1 of that run died `INVALID tooling-venv` because a fresh worktree has no `missions/.venv` - the runbook now leads with `bootstrap_venv.py`. Operator-tiered on reason (b): ~1,900 s worst case x `retry.policy = "once"` is ~63 min a night for a lane that is not yet green. HONEST SCOPE (a limit, not a blocker): the rewind TARGET is the INJECTED `rp_b9_root`, NOT a RewindPoint this flight authored, because (a) an ordinary ascent authors NO RP - `ParsekFlight.TryAuthorRewindPointForSplit` needs a MULTI-CONTROLLABLE split and a dropped booster has no `ModuleCommand` - and (b) nothing can name a live RP anyway: `InvokeRewindImpl` matches `RewindPointId` exactly and live ids are fresh GUIDs (`RewindPointAuthor.cs`), with no seam channel exposing them. So it is a rewind-AFTER-a-flight test, not a rewind-your-own-separation loop. Closing (b) is tracked in todo-and-known-bugs.md. **FLIGHT 2 (2026-07-26, run `2026-07-26_2237`): THE REWIND WORKED** - `MISSION-OK`, wall 207 s, all seven phases, every assertion met including `clockRewound value=267.832` (the clock genuinely ran BACKWARD), `vesselStateChanged value=PRE_LAUNCH` and `recorderIdleBeforeRewind value=false`; driverValidity / analyzer(red=0) / logValidate / anomalySweep all PASS. The STOP -> RECORDER-IDLE fix is LIVE-PROVEN. The RUN still classified PARSEK-FAIL on the forbidden `[Parsek][ERROR]` pattern, and that is the lane's FIRST PARSEK FIND: `R1-EMPTY-PROVISIONAL` (`AppendRelations invariant violation ... reason=empty Points`), written up in todo-and-known-bugs.md. The mission-side cause was that a rewind with no re-flight is only HALF a loop, so the re-fly provisional carried zero Points; CLOSED by adding the second flight (REWOUND is now a waypoint that throttles up + stages, RELAUNCH requires MEASURED altitude gain, LOOP-POINTS requires a `points > 0` read, and the spec pins `Added [1-9][0-9]* supersede relations`). The forbidden-ERROR contract was NOT relaxed - it did its job. **FLIGHT 3 (2026-07-26, run `2026-07-26_2303`): THE LOOP CLOSED** - all ten phases, `MISSION-OK`, wall 212 s, `postRewindFlightObserved value=100.617` (a MEASURED climb, so RELAUNCH is not riding a commanded throttle) and `postRewindFlightRecordedSomewhere value=24`; driverValidity / analyzer(red=0) / logValidate / anomalySweep all PASS. It also DIAGNOSED the finding, which is the real value: the 24 points went into a BRAND-NEW tree (`820de77e` "B9 Slot 1" / recording `f6155f8d`, 58 POINT nodes on disk) while the marker's provisional `rec_5b0697a6...` stayed at ZERO - i.e. the re-fly's provisional is never bound to a recorder, so the re-flight is recorded somewhere else entirely, ZERO supersede rows are written, and in RELEASE the pre-rewind branch and the re-flown branch both stay live as unrelated histories. Severity raised to HIGH (user-visible correctness, build-independent). The predicted non-convergent recovery is now OBSERVED (`RunFinisher drive-forward FAILED at phase=Split`, aborting OnLoad); the save-wipe class was ASSESSED and does NOT apply (the store loads before the merge-journal phase - `committedRecordings=13` at the exception - and the post-run save retains all 13/3), though the abort does skip LoadTimeSweep + the RP reaper. R1 deliberately does NOT carry the expectedFail tag: it is operator-tier, in no cadence, so it cannot redden a sweep, and its red is the loudest evidence the bug is open on the PRIMARY use path . **FLIGHT 4 (2026-07-28, run `2026-07-28_1509`): PASS - the lane is GREEN, first attempt, wall 304 s.** This is the run that RESOLVED `R1-EMPTY-PROVISIONAL` as a FIXTURE ARTIFACT rather than a product defect. Every link of the flight-3 chain inverted once the RP quicksave was made production-shaped (PR #1360 made `BuildRewindPointQuicksave` author `RECORDING_TREE isActive=True` and derive the sidecar VESSEL guid to agree with `recordedVesselGuid`): `activeTreeRestoredFromSave` False -> **True**, the launch-guid veto (`conclusively differs`) fired -> **0 occurrences**, the bug #585 marker swap refused (`marker-tree-id-mismatch`) -> **fired**, the re-flight landed in a NEW tree -> **`tree-b9-stack-root`** (23 points, the origin's own tree), supersede rows 0 -> **`Added 1 supersede relations for subtree rooted at b9-booster-a`**, and `[Parsek][ERROR]` lines present -> **0**. Two side-answers it also settled: KSP DOES preserve an authored VESSEL `pid` across the ProtoVessel load (so the contingency lever of aligning `VesselSnapshotBuilder.ProbeShip`'s snapshot pid was NOT needed and not applied), and the new fail-loud raises produce NO false positives (`outcome=unbound-refly-provisional` and `outcome=refused-unflown-provisional` both absent from a healthy loop). **PROMOTION TO NIGHTLY IS NOW EARNED but deliberately NOT APPLIED in this pass** - the promotion rule was 'after its first green flight' and that is satisfied, and the measured cost is far below the estimate the operator tier was chosen on (304 s actual vs the ~1,900 s worst case that motivated ~63 min/night), but the lane has ONE green run against three prior non-green ones (INVALID(autopilot-flake), then two PARSEK-FAIL), so an operator should make the scheduling call rather than have it inferred from a single sample |
| L1-hire-kerbal-career | daily | Hire debits funds by exactly the pinned cost, nothing else | First live run (2026-07-23) RED = seam double-debit: the hire verb manually mirrored a stock debit that stock already applies (Funding.onCrewHired via OnCrewmemberHired), charging the pool twice. Fixed (seam AddFunds removed); single cost re-pinned -62113 (seed 500000 -> 437887). Re-run confirms hardDivergences=0 + re-tiers to daily |
| L1-dismiss-kerbal-career | daily | Dismiss is pool-neutral | Fixture committed (fresh-career, dismiss Bill Kerman); first green live run re-tiers to daily |
| L1-research-node-career | daily | Research debits science exactly | Fixture committed (fresh-career, basicRocketry=5 verified); first green live run re-tiers to daily |
| L1-research-node-science | daily | Same in science mode (no funds/rep pools) | Fixture committed (fresh-science); RnDPresent widen landed; first green live run re-tiers to daily |
| L1-upgrade-facility-career | daily | Facility upgrade debits funds per-level exactly | First live run (2026-07-23) ledger math PASSED (-150000, hardDivergences=0) but logContract RED = FacilityUpgraded never recorded: the facility recorder only polled on scene load (and cold-load seeded an empty baseline), so a seam upgrade-then-quit was never captured. Fixed (subscribe GameStateFacilityRecorder to OnKSCFacilityUpgrading, event-driven). Re-run confirms "Game state: FacilityUpgraded" present + re-tiers to daily |
| B16-eve-orbit | operator (PROMOTE to nightly after its first green flight) | The FIRST mission to fly B7's five interplanetary params AND B11/B12's capture tail together: capture burn, held park and mid-mission seam CommitTree after a HELIOCENTRIC traverse. HONEST SCOPE - B11/B12 already own the D1 `commit-in-foreign-soi` cell on two bodies, so doing it a third time at Eve adds no commit path and no terminal classification. The ONE thing it adds is that the capture tail has only ever run after a LUNAR transfer: here the committed tree's terminal orbit body is reached through TWO SOI transitions with an arrival v_inf ~4x the Mun's. Eve's atmosphere is NOT claimed (the park is at ~5,000 km) - see B15 | Same as B15 plus D1 commit-in-foreign-soi. NO NEW REGISTRY VALUE: D14 already carries `eve` and D1 already carries `commit-in-foreign-soi`. D5 bg-recording NOT claimed (the B11/B12 reasoning: CommitTreeFlight nulls both recorder handles before returning and settle_frames = 0) | FIRST FLIGHT. FEASIBILITY IS CLOSED ON ARITHMETIC, not assumed: capture-to-circular at a 5,000 km park costs a DERIVED ~735 m/s (dv = sqrt(v_inf^2 + 2mu/r) - sqrt(mu/r), v_inf ~931 m/s calibrated from B7's three MEASURED Duna arrival hyperbolas) against ~1,850 m/s available - DERIVED from the MEASURED end-of-flight fuel state of those same three B7 runs (LF 494.560 / 496.959 / 495.479 of 720) via stock part cfgs and the rocket equation, and cross-checked against B11's MEASURED 277.016 m/s capture node. A 2.5x margin, ~2.0x after the full correction cap. The committed survey's ~1500-1600 m/s figure is PESSIMISTIC and now known why: it predates B5 finding 15, so it did not know the flameout watchdog lets the CORE fly the transfer, leaving the upper stage nearly full. The park is HIGH for a GILLY reason, not a fuel one - the dv optimum (18,157 km) sits inside Gilly's 14,175 km periapsis shell, so `parkMaxApoapsisMeters` 13,000 km doubles as the Gilly exclusion. Inherits B15's inward-transfer unknown. PROVISIONAL pins: recordings count {7, 9}, both wall budgets, `captureBurnTimeoutSeconds`. COST WARNING: at a budgeted 4200 s wall this would become the second most expensive scenario after B13's MEASURED 2,825 s. THAT COST IS WHY IT IS `operator`-TIERED, NOT `nightly`: an UNFLOWN 4,700 s lane with `retry.policy = "once"` would spend up to ~2.6 h a night, and a systematic first-flight failure (which is what all six pre-green B15 attempts were) would red the whole sweep every night until someone flew it. Fly it explicitly (`--id B16-eve-orbit`), close the PROVISIONAL pins, then set `tier = "nightly"` in the spec and here |

### EVA (M-C2 + EVA-4), committed (4): 3 LIVE-PROVEN, 1 blocked

EVA-1 and EVA-3 are LIVE-PROVEN (2026-07-24). EVA-4 was live-proven the same day,
DE-LISTED on 2026-07-25 when the first full sweep red'd it (the kerbal's own canopy
cut itself mid-descent and the kerbal died), fixed headlessly on 2026-07-26 (that
defect plus the fail-open it exposed), and RE-PROVEN the same day by flight 4 -
PASS on attempt 1, all seven verifiers, wall 409 s. EVA-2 is still blocked on
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
| EVA-4-atmo-chute | nightly | Mid-flight ATMOSPHERIC EVA branch (every other EVA case exits on the ground or in orbit), atmospheric TrackSections on the KERBAL's own falling-vessel recording, the EVA chute captured as a two-phase part event on the kerbal (D7 chute-two-phase, previously unclaimed), and the DOWN terminal applied to a KERBAL recording with the kerbal ALIVE | FLIGHT 1 (2026-07-24) ASSERT-FAILED AS DESIGNED, re-tuned, re-fly pending. The machine, the named-failure design and the diagnostics all worked: `eva-window-missed: altitude 702m fell below the window floor 800m (vspeed -295.2m/s, situation FLYING, craftChute armed)`, phasesReached PRELAUNCH/ASCENT/COAST/DESCENT, apoapsisWindow met (19,879 m), no budget burn (107 s wall). MEASURED profile: peak altitude 11,965 m at ut 60.6; unchuted descent settles at TERMINAL -301 m/s by ~2,700 m; chute armed at 2,382 m / -301 m/s and 5.1 s later at 855 m the rate had moved 4.7 m/s. ROOT CAUSE (recording + decompile, not inference): the pod's `.prec` carries ZERO Parachute* part events, and decompiled `ModuleParachute.cs:1255-1290` gates ACTIVE->SEMIDEPLOYED on `automateSafeDeploy >= deploymentSafeState` while the fixture persists `automateSafeDeploy = 0` (only while SAFE) - which DeploySafe never reads at ~300 m/s in dense air. Arming low was INERT, not late; a craft at terminal velocity never slows on its own. THREE FIXES: (1) ARM WHILE SLOW - the machine now arms on the COAST->DESCENT transition frame itself (falling through into the descent body so there is no one-poll delay; measured entry rates -7.4/-16.9/-26.1/-35.5 m/s, bound 30), i.e. at the apoapsis crossing where DeploySafe is trivially SAFE and Kerbin is already ~0.2 atm; (2) RAISE the stock full-deploy altitude from the fixture's 1000 m to 2500 m via kRPC `Parachute.DeployAltitude` (a PAW tweakable) so the full canopy exists well above the EVA band - the Mk16 animation is ~8 s (`deploymentSpeed = 0.12`); (3) GATE ON OBSERVED STATE - new opt-in `craft_chute_state` telemetry channel (kRPC `ParachuteState`, "" unread = fail-closed) so the window requires the chute to READ Deployed, never the commanded latch that was true for the whole failed flight. Window re-tuned [800,2400]/60 -> [700,2100]/25; descent budget provisionally raised 240 -> 480 s and runtime 1560 -> 1920 s because the semi-deployed rate was not measured yet. A new `craftCanopyObserved` assertion row reports observed-vs-commanded in the result JSON. Same-evidence FINDING SPUN OFF: B1-pad-hop's chute never opens either (its 2026-07-20 recording has zero Parachute* events and ends at 65 m) - B1 passes because its DOWN terminal only checks the COMMANDED latch. NOTE on the failed attempt's artifacts: run.py USED TO drive the remaining seam steps regardless of the mission outcome, so flight 1 DID perform a terminal-velocity hatch EVA after the ASSERT-FAIL (EvaExit at ~356 m / -277 m/s, kerbal chute semi-deployed at 221 m, landed alive, tree committed) - no false PASS (the run classifies INVALID(mission) before the tail and the save is re-staged per attempt), but a window-missed run's collected save/log carried a spurious EVA branch + landing and could burn ~120 + 420 s of deferral budget. FIXED harness-side 2026-07-25 (see the M-A5 row): an UNMET mission step now drives the CLEANUP tail only (StopRecording + FlushAndQuit), so this scenario's EvaExit / EvaChuteDeploy / CommitTree are skipped on a window-missed attempt ; LIVE-PROVEN 2026-07-24 (flight 2 FULL PASS, all seven verifiers: canopy observed Deployed, handoff 1,606 m / -23.2 m/s, kerbal chuted descent steady -4.5 m/s, ParachuteCut at touchdown, down=true situation=LANDED alive=true. All four pins closed: P1 count PINNED 3, P2 `'kerbalEVA` token confirmed, P3 semi-deployed rate MEASURED at about -236 m/s peak with the whole DESCENT phase 61.6 s -> descentTimeoutSeconds trimmed 480 -> 240 (~3.9x margin; step/runtime budgets deliberately left at 900/1920 as wall-clock envelopes), P4 kerbal lands alive. Post-live hardenings: K=2 EVA-window debounce and the RAW-alive CompleteOk conjunct) ; DE-LISTED from live-proven 2026-07-25 by the first full sweep and FIXED HEADLESSLY 2026-07-26 (branch `eva4-failopen`), then RE-PROVEN 2026-07-26 (flight 4, PASS on attempt 1, wall 409 s, all seven verifiers: apoapsisWindow 19696.874, evaWindowReached 1592.752, evaWindowDescentRate -18.560, craftCanopyObserved 11964.692, missionOutcome PASS gating=2, expectations mismatches=0). FLIGHT 3 red'd `PARSEK-FAIL(expectations)` at 187 s wall: the kerbal's canopy went SemiDeployed at 1,650 m and Cut 200 ms later, and the kerbal accelerated -11 -> -109 m/s into the ground. TWO defects, both diagnosed from the archive with no new flight. (b) THE CUT: not a parachute decision at all - `On_stumble` is registered on `st_semi_deployed_parachute` (KerbalEVA.cs:8153) with `GoToStateOnEvent = st_ragdoll`, is fired only from the collision callback above `stumbleThreshold = 3.5` m/s (KerbalEVA.cs:12700), and `OnSemiDeployedParachuteModeLeft` calls `evaChute.CutParachute()` on every exit but a full-deploy transition (KerbalEVA.cs:11152-11169). The collected log's ONE `Event Stumble not assigned to state Ragdoll` line, 16 ms after the cut, is the second frame of that contact. The collider is MEASURED, not inferred (corrected in panel review): the kerbal's own `.prec` carries a pod-anchored `Relative` section whose anchor-local metres put it 0.82 m from the pod at `ParachuteSemiDeployed` and 1.50 m at the cut. ALSO CORRECTED: the first draft blamed the LENGTH of the semi-deployed window, but `OnFullyDeployedParachuteModeLeft` (KerbalEVA.cs:11219) cuts UNCONDITIONALLY and `On_stumble` is registered on the full-deploy state too, so a full canopy is equally exposed and the `deployAltitude` knob would NOT have fixed this - the operative variable is PROXIMITY AT CANOPY TIME. FIX: bounded OBSERVED standoff on `EvaExit` (`minStandoffMeters=30`, 2-poll debounce, and TWO non-fatal bounds - 8 s wall clock plus `standoffFloorAltMeters=500`; the wall-clock-only first draft was sized on a 6.2x-wrong altitude figure and would have flown a low handoff into the ground with the canopy never armed). (a) THE FAIL-OPEN: the mission returned MISSION-OK over the dead kerbal, and NO mission assertion could ever have caught it - the machine's terminal is the handoff and the subprocess exits before `EvaExit` creates the kerbal vessel. The observed channel that DID see it (`eva-chute-kerbal-lost`) was recorded as `driver.steps[6].verdict=ERROR` and consulted by nothing; `driverValidity` reported PASS beside `allExpectedMet: false`. FIX: `SEAM_VERB_POST_MISSION_ROLE` + the `missionOutcome` verifier row + `PARSEK-FAIL(mission-outcome)`, plus an mlib handoff declaration so MISSION-OK states what it did not verify) |

### In-game batch wiring H7-H20, all 14 LIVE-PROVEN (14)

The gap these close: Parsek ships 539 in-game runtime tests across 97 categories, and
before this group committed specs drove EIGHT of them. The other 89 were written,
passed when an operator pressed Ctrl+Shift+T, and never executed in any unattended
run. Full enumeration, the A/B/C triage behind which 14 were picked, and the
PENDING-OPERATOR fly order live in
[`autotest-ingame-category-inventory.md`](autotest-ingame-category-inventory.md).

All 14 are batch-only specs on the S1.4 / H6 shape: LoadGame the committed
`gloops-airshow` host, pin `autoRecordOnLaunch` false, one `RunTests` step naming one
category, FlushAndQuit. Driven categories go 8 -> 22 of 97. Declarations inside a
driven category go 125 -> 201 of 539, and the subset that actually EXECUTES (surviving
both runner filters at the scene each spec drives) goes 103 -> 179. All 76
declarations the new group adds execute; the whole 22-declaration gap sits in the
eight pre-existing driven categories, over half of it at FLIGHT rather than
SPACECENTER (`GhostMap` alone is 9) and 4 of it `AllowBatchExecution=false` rather
than scene-skipped. Per-category decomposition is in the inventory doc.

EVIDENCE STANDING FOR THE WHOLE GROUP: **ALL 14 FLOWN 2026-07-27**
(`python run.py --tag ingame-batch`, against a DLL built and provisioned from the
branch). All 14 PASS on attempt 1, every verifier PASS or SKIPPED,
`batchComplete found=True failed=0 perCategory=1` on each, **805 s (13.4 min) wall for
the whole group**, 49-71 s per scenario.

WHAT THE FLIGHTS ADDED OVER THE PRE-FLIGHT DERIVATION, precisely, because the two are
easy to conflate. The `total=` values were already statically derivable from the
`[InGameTest]` attributes and gated by `CommittedBatchTallySourceSyncTests`; flying
proves nothing new about them. What flying measured is the **passed / skipped
SPLITS**, which no static analysis predicts because a run-time `InGameAssert.Skip`
can move them at any time. Thirteen specs pin those splits as LITERAL patterns, and
`evaluate_expectations` requires a required pattern to match, so a PASS means the
runner printed the pinned line TOKEN FOR TOKEN. All thirteen pre-flight derivations
were correct.

`H20` is the one member NOT settled by the sweep. Its pin is the loose interim form,
so its PASS proves only `total=2`, `failed=0`, `passed >= 1` - it cannot distinguish
`passed=2 skipped=0` from `passed=1 skipped=1`. The exact split survives in no
artifact: collect-logs fires only on non-PASS (`collectLogs: {ran: false}`) and the
instance KSP.log was overwritten by later scenarios in the same sweep - the identical
evidence gap S1.4 documented. It KEEPS the interim pin; one ~49 s re-fly closes it.

COST, and the estimate that was wrong: this runbook estimated ~4-10 min per scenario
and ~80 min for the group, extrapolating from mission-flying scenarios. A batch-only
scenario is dominated by KSP boot plus save load, not by the batch, so the real figure
is 49-71 s each. For scale, `B13` alone is a measured 2,825 s - the whole H7-H20 group
costs under a third of one landing mission.

| Test case | Tier | Parsek surface verified | Blocker |
|---|---|---|---|
| H7-trajectory-math | nightly | Sampling predicate + quaternion helpers against LIVE Unity arithmetic; `ShouldRecordPoint` against the density preset the running game loaded (D2 density-presets/threshold-debounce) | LIVE-PROVEN 2026-07-27, 49 s: `total=8 passed=8 failed=0 skipped=0 category=TrajectoryMath scene=FLIGHT` matched token for token |
| H8-spawn-rotation | nightly | The srfRelRotation-vs-world-rotation spawn-node contract and terminal-pose frame preference, as PURE arithmetic over fabricated quaternions - the review round withdrew the live-Kerbin/Mun claim and the D14 kerbin/mun tokens with it (D13 surface-orbit-reseed only) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=10 passed=10 failed=0 skipped=0`. The scene inference is load-bearing here (all 10 are FLIGHT-scoped) |
| H9-incomplete-ballistic | nightly | Ballistic tail extrapolation through atmosphere/terrain/SOI, patched-conic snapshot integration, extrapolated-segment map line (D1 ballistic-extrapolation/scene-exit-finalization) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=8 passed=8 failed=0 skipped=0` |
| H10-finalize-backfill | nightly | Terminal-orbit backfill from OrbitSegment, no-overwrite guards, and the four stale-cached-tuple endpoint realignments (D1 finalization-cache) | LIVE-PROVEN 2026-07-27, 56 s, matched token for token: `total=7 passed=7 failed=0 skipped=0` |
| H11-pipeline-anchor | nightly | Anchor epsilon vs recorded geometric offset across all seven anchor situations. Only 1 of the 7 cells resolves through a live body; 4 install a constant-returning stub resolver, so the review round withdrew the D3 claims (D14 only) | LIVE-PROVEN 2026-07-27, 55 s, matched token for token: `total=7 passed=7 failed=0 skipped=0`. Seven frame-yielding coroutines, so this is the first budget to re-time |
| H12-switch-segment | nightly | The Map Switch-To arming PREFIX gate across focus modes + intent arm/clear with no marker leak (D1 switch-segment) | LIVE-PROVEN 2026-07-27, 70 s, matched token for token: `total=6 passed=6 failed=0 skipped=0` |
| H13-ksp-api-smoke | nightly | FIXTURE CANARY: loaded-scene / Kerbin / PartLoader / root-path sanity plus `ActiveVesselExists` and `FlightCameraExists`, the only POSITIVE proof that the fixture took the Focusable/FLIGHT route the other 13 infer (D14 only - no Parsek behavior is under test) | LIVE-PROVEN 2026-07-27, 50 s, matched token for token: `total=6 passed=6 failed=0 skipped=0`. FLY THIS FIRST: a wrong route reds here as `passed=4 skipped=2` naming the real scene, before 13 other runs are wasted |
| H14-corpus-data-health | nightly | Body names, OrbitSegment bodies, PartLoader part resolution and time ranges across all 272 injected recordings (D16 sidecar-prec/schema-gate) | LIVE-PROVEN 2026-07-27, 71 s, matched token for token: `total=4 passed=4 failed=0 skipped=0`, plus `recordings.count = 272` which is the real anti-vacuity guard (see the fourth-trap note below) |
| H15-corpus-ghost-visuals | nightly | A ghost mesh built from EVERY recording in the corpus; every stored part name resolved through the live PartLoader (D16 sidecar-craft/sidecar-pcrf) | LIVE-PROVEN 2026-07-27, 70 s, matched token for token: `total=4 passed=4 failed=0 skipped=0` + count 272. Heaviest batch in the group; the one most likely to need a budget bump |
| H16-corpus-spawn-health | nightly | Stuck `SpawnAbandoned` and out-of-bounds `SpawnDeathCount` across the corpus (D13 three-cycle-abandon) | LIVE-PROVEN 2026-07-27, 69 s, matched token for token: `total=3 passed=3 failed=0 skipped=0` + count 272. One of its three cells (`SpawnedPidConsistency`) is INERT over this corpus and the spec says so |
| H17-flight-integration | nightly | Recorded lat/lon/alt vs `GetWorldSurfacePosition` over the corpus, ParsekFlight liveness, active-vessel surface API, Harmony patch operational (D3 surface-body-fixed; D16 sidecar-prec) | LIVE-PROVEN 2026-07-27, 69 s, matched token for token: `total=4 passed=4 failed=0 skipped=0` + count 272. Its first cell bails SILENTLY over an empty store, so the count pin is what makes it mean anything |
| H18-pipeline-smoothing | daily | Coast-jitter suppression, structural-event flag alignment and child-seed parity, and the LIVE GameEvents subscription contract - a dropped `GameEvents.X.Add(...)` compiles, unit-tests green, and silently stops recording docks (D2 structural-event-snapshots) | LIVE-PROVEN 2026-07-27, 50 s, matched token for token: `total=4 passed=4 failed=0 skipped=0`, plus the `asserted=5 of 5 GameEvents bindings` line. One caveat stated in the spec: the wiring helper's only Skip branch is a KSP field rename, unreachable on 1.12.5 |
| H19-recording-finalization | nightly | BackgroundRecorder finalization-cache apply: destroyed-cache tail trim at the deletion UT, stable-cache Orbiting finalization, active-crash tail append (D1 finalization-cache) | LIVE-PROVEN 2026-07-27, 49 s, matched token for token: `total=3 passed=3 failed=0 skipped=0` |
| H20-eva-spawn-position | nightly | EVA spawn within 10 m of the recorded endpoint and at least 50 m off the parent; trajectory walkback when the endpoint overlaps (D13 terrain-correction/trajectory-walkback) | The ONLY interim pin in the group: `total=2 passed=[1-9][0-9]* failed=0 skipped=[0-9]+`. Both cells carry run-time Skip guards; gloops-airshow satisfies the crewed / landed / solid-ground / vessel-type ones from the save file, but the walkback cell's `WalkbackFixtureCoversParent` answer is measured at run time. FLY LAST and replace the pattern with the whole tally. Runbook item 14 |

TIERING, decided on the measured cost: **13 stay `nightly`, `H18-pipeline-smoothing`
is promoted to `daily`.** Cost does not discriminate here - every member is 49-71 s and
the whole group is 805 s against 2,825 s for B13 alone - so the promotion is decided on
FAILURE MODE instead. H18 is the only guard anywhere in the suite for the live
GameEvents subscription contract, and a dropped `GameEvents.X.Add(...)` is SILENT: it
compiles, every xUnit cell stays green, and Parsek stops recording docks / undocks /
EVAs from that moment with nothing else to catch it. A day of latency on that is worth
50 s a day. The other 13 have flown exactly ONCE, and the house convention (see the EVA
rows above) is to promote on flake data rather than on a single green; revisit after
about three nightly runs. Note H18's promotion is narrower than it looks: the flight
also showed that cell is commanded-vs-observed for one of its five bindings, so what
runs daily guards FOUR production subscriptions, not five.

Two findings from building this group, both with full detail in the inventory doc:

- **The FUTURE recommendation was half-wrong, and the data says why.** The 2026-07-25
  EVA decision recommended a dedicated batch-only scenario over the injected corpus
  for `EvaSpawnPosition` AND `CrewReservationLive`. `EvaSpawnPosition` shipped as H20.
  `CrewReservationLive` CANNOT ship that way: both its cells short-circuit on
  `spawnedCount == 0`, and the corpus injector
  (`SyntheticRecordingTests.CleanSaveStart` -> `RemoveSpawnedPidLines`) strips every
  `spawnedPid = ` line out of the staged save by construction, so the corpus is
  guaranteed to contain zero spawned-endpoint recordings and the batch would emit
  exactly the vacuous `total=2 passed=0 failed=0 skipped=2`. Unblocking it is a C#
  corpus-writer change, and the same change makes H16's inert third cell meaningful.
- **A FOURTH trap, invisible to every existing defence.** `batch_contract_vacuity_gap`
  catches `passed == 0`. It cannot catch a test that RUNS, PASSES and asserts over
  nothing - a store walk counting violations passes on zero items, and several tests
  bail early with a silent `return` / `yield break` plus a Verbose log INSTEAD of an
  `InGameAssert.Skip`, so they are reported PASSED rather than Skipped. Only the
  FIXTURE defends against it, which is why the four corpus-backed members pin
  `recordings.count = 272`.

A three-reviewer panel ran against this branch before merge: 26 findings, no
blockers. Four changed what the specs CLAIM rather than what they do, and all four
are corrected here. H18's GameEvents cell is commanded-vs-observed for one of its
five bindings (the test seeds `onPartJointBreak` itself via `SubscribePartEvents`,
so that one fails open); H11 and H8 both claimed live CelestialBody work they do not
perform, and their D3 / D14 claims are withdrawn; H20's interim pin named the one
guard that IS derivable (`WalkbackFixtureCoversParent`, true with about 913 m of
margin) instead of the live `Physics.OverlapBox` that is not; and the `spawnedPid`
mechanism cited here and in the todo doc was wrong - `CleanSaveStart` runs before the
corpus writer injects and does not run on the harness path at all, the real
invariants being that `RecordingBuilder.WithSpawnedPid` has zero callers and
`AddRealCareerRecordings` injects `RECORDING_TREE` nodes only. That last one was an
active trap: "making the stated mechanism real" by adding `-CleanStart` would strip
the fixture's VESSEL nodes and route every corpus spec to SPACECENTER. Also fixed:
the new test class had no anti-vacuity floor (emptying its GROUP table left all eight
cells passing over zero specs), its membership guard compared an intersection so a
new spec was invisible to it, and H15's budget is raised to 1200 s because its
failure mode is a KILLED run that produces no tally to re-derive from.

A harness bug was found and fixed on the way: `hlib._pin_literal_word`'s character
class excluded `-`, so all seven hyphenated categories (`Pipeline-Anchor`,
`Pipeline-Smoothing`, `Pipeline-Frame`, `Pipeline-Outlier`, `Pipeline-Terrain`,
`Pipeline-AnchorPropagate`, `Pipeline-Anchor-BubbleEntry`) were structurally
unpinnable - `statically_checkable` went False and the sync sweep rejected the spec
with a message blaming the author. The runtime parser never had the gap (`_BATCH_RE`
reads a non-space category token), so this was a static-path-only disagreement with
the line the game actually prints.

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

- Headless: 918 mission-machine + 768 harness + 203 provisioner unittest
  cells; 18,669 xUnit on the C# side (18,668 passed + 1 skipped: analyzer,
  seam, log contracts, the new route-window delta formatter). Re-measured
  2026-07-26 from `autotest-render-parity` AFTER its SECOND merge of
  `origin/main` (which carried `autotest-orbit-missions` ->
  `autotest-landing-missions` -> `autotest-eve-missions` ->
  `autotest-batch-coverage` -> `autotest-tally-gate`); the harness figure
  moved 664 -> 726 because that merge is the first tree holding both sides of
  those cells, then 726 -> 742 on `ingame-test-wiring` (the H7-H20 group's
  `IngameBatchWiringGroupTests`, the hyphenated-category pin cells, and the
  inventory-doc source-sync gate).
  This line inherits SILENTLY through a clean auto-merge and is wrong
  the moment either side adds a test, so re-measure rather than editing it by
  memory - `cd harness && python -m unittest discover -s missions/lib -q`
  (and `-s lib -q`, `-s provision -q`), plus
  `cd Source/Parsek.Tests && dotnet test`.
- Per-run: the 7-verifier chain + collect-logs on every non-PASS.
- In-game: 539 runtime tests / 97 categories (autorun-able), H5 invariants,
  log-contract tests. Counted mechanically by
  `hlib.parse_ingame_test_declarations` over `Source/Parsek`, not by hand
  (the hand-and-grep number was 534 / 96: five namespace-qualified
  declarations were invisible to both). AUTORUN-ABLE IS NOT THE SAME AS
  AUTORUN: **22 of the 97 categories are driven by a committed spec** (up from
  8 before the H7-H20 group). That covers 201 of the 539 declarations, 179 of
  which would actually execute; the 22-declaration gap is decomposed per category
  in the inventory doc (it is NOT simply the SPACECENTER categories - over half is
  at FLIGHT). The remaining 75 categories still run only when an operator presses
  Ctrl+Shift+T.
  Per-category eligibility, the A/B/C triage of what is left, and the reason
  each un-wired category is un-wired live in
  [`autotest-ingame-category-inventory.md`](autotest-ingame-category-inventory.md);
  the split is re-derived there, never hand-counted.
- Spec-vs-source: every batch-owning spec's pinned `BATCH_COMPLETE` tally is
  cross-checked against the C# `[InGameTest]` attributes it describes
  (`CommittedBatchTallySourceSyncTests`), so adding an in-game test to a
  pinned category fails locally instead of on the next nightly. The same
  sweep asserts RECOGNITION completeness, so an attribute spelling the parse
  does not model reds instead of quietly shrinking a total.
- Findings baseline: 5 historical saves baselined; fresh harness saves run
  baseline-Forbid (structural fresh-save guard).
- Coverage ledger: 96 / 241 registry cells claimed (the growth metric),
  RE-DERIVED at the merge through `hlib.compute_coverage` over the 52 committed
  specs + the merged registry - neither side's number survives it. `main` read
  84 / 241 over its 38 specs (83 at the 2026-07-26 recompute; D3
  `parent-anchored-debris` is the cell R1's debris gate added). This branch read
  95 / 241 over its 52. The merged figure is recomputed rather than taken from
  either, because the two changed disjoint things: main CLAIMED a new cell and
  this branch both claimed cells and WITHDREW two. The withdrawals were
  deliberate - H11's D3 tokens (four of its seven cells run with the production
  anchor resolver replaced by a constant-returning stub, so the frames it named
  are not actually exercised) and H8's D14 kerbin / mun (its "KerbinPadCase" /
  "MunCase" are fixture labels over fabricated quaternions - the file never looks
  up a CelestialBody). A withdrawn false claim is worth more than a claimed cell:
  the failure mode of over-claiming is a later audit reading a dimension as
  covered and skipping the test that would have caught the regression. The green-backed subset is the OTHER half of
  this metric and needs the run archive, which is gitignored
  (`harness/results/*.json`, `harness/coverage/coverage.{json,txt}`), so
  re-derive it on a checkout that has one; last measured 2026-07-25 at 58 of
  that day's 70. Never carry either number forward by hand.

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
| `gameSecondsPerWallSecond` live + per phase | The ratio that named two shared-machine warp defects was end-of-run only with ZERO programmatic consumers. It is now a phase-history column, the OPEN phase's live `phaseWarp` block in the status payload, and a LOW marker. **Revised 2026-07-26:** ratio + wall ALONE marked only false positives (4 of 4 on healthy B11, the identical 2 on all 5 healthy B12 runs; healthy CORRECTION-BURN reads 43.6 against a defect at ~40, so the ratio cannot separate them). The marker now also requires `armedWarpCommands > 0` - we ASKED for warp and did not get it - where `ACTION_CANCEL_WARP` and `SET_RAILS_WARP(0)` do not count as arming. On healthy B11 and all 5 healthy B12 runs it fires on ZERO rows. Still INFORMATIONAL; the BROKEN case stays owned by `warp_liveness_starved` |
| `PHASE_BUDGET_KEYS` covers the ORBIT tail | The table covered 8 phases and was blind to PLAN-CAPTURE / CAPTURE-BURN / PARK / ORBIT-COMMIT, so B11's CAPTURE-BURN (642 wall s, the most expensive phase in the suite) printed `budget n/a`. All four keys plus the B1/B4/EVA-4/FORGE/B-DOCK phases are mapped, each mirroring mlib's own `_*_phase_budget`, and the four ORBIT phases have heuristic branches reading `captureExecDownStreak` / `parkStableStreak` / `nodeExec` |
| Gate-flip window dumps rate-limited | MEASURED: one B12 stdout log is 43 MB / 181,786 lines, 79.5% of it `window[NN/20]` payload from 7,218 gate-flip dumps (7,207 from the single field `gate warpToCmd`). Even a healthy B5 run spends 50% of its lines on window payload carrying 71% duplicate frames. **Revised 2026-07-26:** that whole run holds only **16 distinct `(phase, gate-field)` pairs**, so the FIRST occurrence of each pair is now admitted unconditionally (16 windows instead of 7,218 - a bigger reduction than the time rule, and no novel flip ever loses its context) and the 10 s limit (= the ring's own span) applies to REPEATS only. `phase-transition` / `terminal-*` / `vessel-lost` dumps stay unconditional, the `gate warpToCmd` line itself is untouched, and one batch-summary line per flight names how many were suppressed. `GateFlipSuppressionFlightTests` now drives the real fly loop through the suppression path (before it, removing the limit entirely and rate-limiting every reason both survived the whole suite) |
| Committed `harness/coverage/duration.json` | `flake.json` tracked outcomes and nothing tracked duration, and every artifact carrying one is gitignored. Consequence: the B12 spec claimed B11 was the SHORTER run, backwards across four measured runs each (B11 p50 1,317 s, B12 p50 627 s), unnoticed. **Revised 2026-07-26:** the first cut was DESTRUCTIVE - it recomputed the record from the gitignored per-checkout `results/` dir and truncate-wrote it, so a fresh worktree flying one scenario replaced the 24-entry file with 1 entry (observed live), and a measured scenario's `n=5` became `n=1`, disarming the warn. The ledger now stores a bounded per-scenario SAMPLE tail keyed by `endedUtc` and MERGES into the committed file (`hlib.duration_samples` + `hlib.merge_durations`), the write is tmp + `os.replace`, and an unreadable ledger SKIPS the write with an Error instead of being replaced. **Revised again 2026-07-26 (review round 2):** the committed file was still SUMMARY-ONLY - all 24 entries lacked `samples`, so every scenario would have taken `merge_durations`' BOOTSTRAP branch on the next run and neither the watermark rule nor the bounded tail was exercised by the artifact in the repo. It has been regenerated through the production `duration_samples` + `merge_durations` path over the archived `results/*.json`: 24/24 entries now carry samples, 23 keep their summary numbers to the digit and B10 picks up the second PASS it had never merged (n 1 -> 2). The B12 spec claim is corrected |
| Per-scenario retry cost + `missionWallSeconds` | B7-duna burned 794 + 776 = 1,570 s across two INVALID attempts and produced nothing, traceable only as two unrelated summary lines. Each scenario now logs `scenario cost attempts=N wallTotal=Xs terminal=Y` and carries `attemptsWallSeconds`; the result also carries the mission's own `missionWallSeconds`, so the harness-vs-mission residue is a subtraction. That residue MEASURED at a stable 40-67 s across 16 runs (KSP boot ~35 s + verifier chain ~10 s), which is why the seven individual call sites are deliberately NOT instrumented - past ~120 s is the signal to look closer |

## Known gates and latent items (forensics in todo-and-known-bugs.md)

0. ANOMALY_TOKENS has DRIFTED from what the mod raises, and the drift is a
   FAIL-OPEN. Found 2026-07-26 while anchoring the sweep. `icon-jump` is a DEAD
   token: `MapRenderProbe` raises the icon-teleport family with
   `reason=icon-teleport`, so the harness's token can never fire and a real icon
   teleport - the exact class the map-render wave has been chasing - passes the
   sweep. NINE further reasons are raised and ungated: `icon-teleport`,
   `icon-off-orbit`, `unaccounted-drawn-recording`, `gap-vs-retire`,
   `decision-vs-old-truth`, `clock-not-ready`, `retire-not-held`,
   `anchor-resolve-fail` and `factory-parity`. (The first version of this gate
   said FIVE. The four it missed are the cutover-hardening raises, which reach
   `EmitAnomaly` through thin `MapRenderTrace` wrappers rather than at their guard
   site in `ShadowRenderDriver` / `AnchorFrameResolver`, so they do not show up in
   a grep for `EmitAnomaly` call sites. They emit the same `phase=Anomaly ...
   reason=<token>` line as any direct raise. The list is now DERIVED FROM SOURCE
   by `AnomalyGroundTruthEnumerationTests` and pinned in
   `hlib.ANOMALY_REASONS_RAISED_UNGATED`, so the count cannot drift silently
   again.) Deliberately NOT resolved in the same change: the call is per token -
   some are coverage instruments rather than defect signals
   (`unaccounted-drawn-recording` is the S0 polyline-coverage probe;
   `factory-parity` is a shadow comparator that never drives a draw), and the one
   that most likely SHOULD be gated (`icon-teleport`) would widen S1.4 without
   anyone knowing whether it fires there - every S1.4 flight predates the
   `unlistedReasons` channel, so its next nightly is the measurement that should
   decide the rename. NOTE what the deferral is not:
   an earlier wording said widening "moves verdicts on every committed scenario",
   which stopped being true in this same change - only S1.4, S1.6 and S1.7 arm the
   map tracer now, so only those three could move. Interim:
   `hlib.unlisted_anomaly_reasons` REPORTS every raised reason absent from the set,
   run.py warn-logs it and records it in the result JSON as
   `anomalySweep.unlistedReasons` - non-gating, but the drift is now visible on
   every run instead of silent. Pinned by
   `AnomalyGrepAnchoringTests.test_icon_jump_is_a_dead_token_against_what_the_mod_emits`
   plus `AnomalyGroundTruthEnumerationTests`.
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
   NOT A THIRD INSTANCE - a SIBLING class, found 2026-07-25 by the first full
   sweep and diagnosed 2026-07-26. (An earlier revision of this item opened
   "THIRD INSTANCE FOUND" and then contradicted itself two sentences later;
   the retraction is kept visible because this item is the canonical triage
   index for the class and a reader who counts EVA-4 as a member would apply
   the wrong prescription.) EVA-4's chute opened and then CUT itself, the
   kerbal fell from -11 to -109 m/s and died, and the MISSION still returned
   `MISSION-OK reason=all telemetry assertions met` - none of its four
   assertions covers kerbal survival, which is that mission's stated purpose.
   It is NOT an instance of THIS class: `EvaChuteDeploy` already had the observed channel, already debounced
   it (3 consecutive gone-reads) and already fast-failed on a distinctly named
   give-up (`eva-chute-kerbal-lost`). The observation was made, was correct, and
   was durably recorded (`driver.steps[6].verdict = ERROR`,
   `driver.allExpectedMet: false`) - and then no gate read it
   (`verifiers.driverValidity.status: "PASS"` on the same run). That is the
   failure mode which REMAINS after commanded-vs-observed is fixed, and it is
   worth its own name: **OBSERVED-BUT-UNGATED**. Closed structurally by
   `hlib.SEAM_VERB_POST_MISSION_ROLE` + the `missionOutcome` verifier row +
   `PARSEK-FAIL(mission-outcome)`, so the run reds on the outcome step's own
   verdict with no dependence on a spec author's log-token regexes. Full
   forensics (both defects) in `todo-and-known-bugs.md` under the EVA-4 section.
   AUDIT QUESTION IT RAISES: every gate that reads a step verdict should be
   checked for the same shape - a recorded observation nothing consults.
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
   `warp-liveness-starved` floor, which judges an EPISODE-LOCAL game-s/wall-s
   ratio computed in the fly loop - NOT the per-phase `gameSecondsPerWallSecond`
   the block reports (on flight 2 those read 1.41 and ~39 respectively; see the
   re-fly sweep note under item 2 of the scenario roadmap for why the difference
   is load-bearing). The floor has never fired in the field and bounds the
   post-fix residual rather than flight 2's own thrash; its mechanism is covered
   by `test_shells.WarpLivenessRealMachineTests`. The AUDIT itself remains blind
   to the class - a real gap in an existing gate, not a new instrument.
   WARP TEARDOWN, corrected 2026-07-26 (review round 2): the floor's terminal
   used to return with the warp still armed, justified by a comment claiming
   nothing drives the game afterwards - `hlib.plan_unmet_mission_tail` does
   (StopRecording + FlushAndQuit run after ANY unmet mission, MISSION-FLAKE
   included). It now cancels the warp inline, best-effort. RESIDUAL: the other
   two fly-loop terminals (wall reaper, unexpected-warp flake) still return
   without a teardown; forensics and the reasoning for leaving them are in
   `todo-and-known-bugs.md`.
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
10. NO SCENARIO EVER KILLS ITS SUBJECT IN A LIVE FLIGHT (recorded 2026-07-26
   with the EVA-4 re-prove). Flight 4 passed, but a LIVE kerbal proves nothing
   about a DEAD one: the `eva-chute-kerbal-lost` path and the
   `PARSEK-FAIL(mission-outcome)` gate it feeds are proven end to end only by
   xUnit over the pure decision (`KerbalTreatedAlive_RealLossSurvivesTheDebounce`,
   `Completion_KerbalLost_BeatsEverything`,
   `Completion_LossWinsOnceTheDebounceExpires`,
   `Completion_TimesOutHonestly_WhenBudgetEndsMidLossDebounce`) plus the fake-KSP
   smoke run that replays flight 3's step stream, plus a code read. The green
   flight exercised the OTHER branch of every one of those decisions.
   DELIBERATELY NOT CLOSED by flying a fatal profile: a scenario engineered to
   kill a kerbal buys one bit at the cost of a fixture nobody maintains and a
   run whose artifacts are indistinguishable from a real regression. The pure
   decision is the right home for it. What DOES need saying is the narrower
   in-game gap: the standoff's Unity-side wiring (`EvaluateStandoff`,
   `TryCompleteEvaExit`) has no headless cell, so a refactor dropping the
   `&& standoffSatisfied` conjunct would keep every suite green - the live
   `evaexit standoff cleared` logContract token is the only thing that catches
   it, and it now has one green run behind it.

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
4. B9 rewind observation session (S1.5 + S4.1) - NO operator session needed as
   of 2026-07-26. Both are now normal unattended NIGHTLY runs (re-tiered from
   operator; the "no flight-entry verb" premise was false - `LoadGame` focuses
   the save's active vessel into FLIGHT, and `gloops-airshow` carries
   `activeVessel = 1`). What their first nightly run establishes is the
   LIVE-LOAD fidelity of the B9 fixture's cloned per-slot vessels. What stays
   genuinely outstanding is PENDING-VERIFIER, not operator: S4.1's
   supersede-row / tombstone asserts under `[expectations.rewind]` are RESERVED
   until the M-C2 rewind save-parse verifier lands.
5. B1 chute re-prove: NO operator session needed - it is a normal unattended
   nightly run. Listed here only because it is the gate that returns B1 to
   live-proven, and because its result pins P1/P2/P3 in the B1 spec.
6. ~~H7-H20 tally measurement~~ DONE 2026-07-27: all 14 flown via
   `python run.py --tag ingame-batch`, all 14 PASS on attempt 1, 805 s (13.4 min)
   total. Thirteen pinned splits confirmed token for token. ONE residue, and it is
   ~49 s of work: `H20-eva-spawn-position` carries the loose interim pin, so its
   PASS proves only `total=2 failed=0 passed>=1`; its exact split is in no artifact
   (collect-logs fires only on non-PASS and the instance log was overwritten by the
   rest of the sweep), so re-fly that ONE scenario and pin it whole.
6. R1 rewind-loop-flown - FLOWN THREE TIMES (2026-07-26). Flight 1 found a
   mission ordering defect (fixed), flight 2 proved the rewind works and
   found `R1-EMPTY-PROVISIONAL`, flight 3 closed the loop and DIAGNOSED that
   finding to root cause. It is not green and should not be: its
   `Added [1-9][0-9]* supersede relations` contract is the positive assertion
   that fails until the Parsek-side fix lands. No further operator run is
   needed to advance the diagnosis. The ONE cheap experiment that would
   narrow the fix scope is a rewind + re-fly with NO prior in-flight commit
   (see the finding's PROVEN-vs-INFERRED note): if the provisional gets the
   points there, the trigger is "a pending tree already exists at rewind
   time" rather than the re-fly path in general. Runbook below.

### R1-rewind-loop-flown: operator runbook [EXECUTED END-TO-END 2026-07-28, works verbatim]

**Verified 2026-07-28**: these three steps were run in this exact order on a fresh
worktree and produced the lane's first PASS (run `2026-07-28_1509`, wall 304 s). Step 1
was genuinely required (the worktree had no `.venv`), and step 2 was genuinely required
(the automation instance was carrying a DLL from before the branch under test - verify it
afterwards by grepping the DEPLOYED `automation/stock-minimal/.../Parsek.dll` for a
distinctive new UTF-16 string, NOT the dev instance's copy).


**Step 1 - the mission venv (DO NOT SKIP on a fresh worktree).** Every autopilot
scenario spawns its mission in `harness/missions/.venv`, which is gitignored and
therefore ABSENT in a newly created worktree. Without it the run dies at
pre-launch ADMIT with `INVALID tooling-venv (terminal, no KSP boot)` - which is
exactly what killed attempt 1 of the first R1 flight (2026-07-26). It is cheap
and idempotent, so run it every time rather than guessing:

```bash
cd harness && python missions/bootstrap_venv.py
```

**Step 2 - provision** (the harness runs a DIFFERENT KSP instance from the dev
one; needed whenever the automation instance is stale relative to the branch
under test, and always when the branch changed C#):

```bash
cd harness && python provision/provision.py --profile stock-minimal
```

**Step 3 - fly it** (one scenario, explicit `--id`; `operator` tier is in no
cadence):

```bash
cd harness && python run.py --id R1-rewind-loop-flown
```

Live watch while it flies:

```bash
cd harness && python status.py            # phase, gates, last events
tail -f results/<runId>_mission.stdout.log
```

WHAT CONFIRMS THE CYCLE. Four independent surfaces; require ALL of them, and
treat the first two as the load-bearing pair (the rest can be true while the
rewind did not happen):

1. The mission's OWN observations - `results/<runId>_mission.json`. FOUR rows
   carry `channel: "observed"` and all four must be `met: true`:
   `recorderIdleBeforeRewind` (`value: "false"`), `clockRewound` (a POSITIVE
   `value`, the seconds the clock ran backward), `vesselStateChanged`, and the
   two loop rows `postRewindFlightObserved` (metres climbed after the rewind) +
   `postRewindFlightRecordedSomewhere` (a POSITIVE point count; `-1` is the UNREAD
   sentinel, `0` means the second flight recorded nothing). If
   `rewindSeamAccepted` is met and `clockRewound` is not, the verb returned OK
   and nothing rewound - the exact failure this lane exists to make visible. If
   the rewind rows are met and the LOOP rows are not, the rewind worked and the
   re-flight did not, which is the R1-EMPTY-PROVISIONAL shape.
2. The mission log, in order (`results/<runId>_mission.stdout.log`). Note the
   FOUR seam commands and the FOUR distinct ids - the STOP + RECORDER-IDLE pair
   is what flight 1 was missing:
   ```
   [Mission][Info][Seam] seam command written [id=<step>.commit cmd=CommitTree]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.commit cmd=CommitTree verdict=OK -> OK
   [Mission][Info][COMMIT] phase COMMIT -> STOP ...
   [Mission][Info][Seam] seam command written [id=<step>.stop cmd=StopRecording]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.stop cmd=StopRecording verdict=OK -> OK payload=...stopped=true,idle=false...
   [Mission][Info][STOP] phase STOP -> RECORDER-IDLE ...
   [Mission][Info][Seam] seam command written [id=<step>.state0 cmd=RecordingState]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.state0 cmd=RecordingState verdict=OK -> OK payload=...recording=false...
   [Mission][Info][RECORDER-IDLE] phase RECORDER-IDLE -> REWIND ...
   [Mission][Info][Seam] seam command written [id=<step>.rewind cmd=InvokeRewind rp=rp_b9_root slot=1]; polling 420s
   [Mission][Info][Seam] seam command response id=<step>.rewind cmd=InvokeRewind verdict=OK -> OK payload=...rewound=true...
   [Mission][Info][REWIND] phase REWIND -> VERIFY ...
   [Mission][Info][VERIFY] phase VERIFY -> REWOUND ut=<LOWER than the RECORDER-IDLE->REWIND ut>
   [Mission][Info][REWOUND] phase REWOUND -> RELAUNCH ...
   [Mission][Info][RELAUNCH] action set_throttle value=1.000
   [Mission][Info][RELAUNCH] action activate_stage ...
   [Mission][Info][RELAUNCH] phase RELAUNCH -> LOOP-POINTS ...
   [Mission][Info][Seam] seam command written [id=<step>.loop0 cmd=RecordingState]; polling 120s
   [Mission][Info][Seam] seam command response id=<step>.loop0 cmd=RecordingState verdict=OK -> OK payload=...points=<NON-ZERO>...
   [Mission][Info][LOOP-POINTS] phase LOOP-POINTS -> LOOP-CLOSED ...
   ```
   `recording=false` on the `state0` reply is the load-bearing token in the first
   half: it is the dispatcher's own `recording-active` gate, READ rather than
   assumed. If the log shows `recording=true` probes marching up `state1`,
   `state2`, ... the post-commit promotion re-armed a recorder that will not stop
   - capture the KSP.log, that is a Parsek finding.
   `points=<NON-ZERO>` on the `loop0` reply is the load-bearing token in the
   second half. A rewind that is never flown again leaves the re-fly provisional
   EMPTY, which is exactly what made flight 2 red (R1-EMPTY-PROVISIONAL). If the
   probes march up `loop1`, `loop2`, ... still reading `points=0`, the craft flew
   but nothing recorded it.
   All FIVE seam ids MUST differ (`.commit` / `.stop` / `.state0` / `.rewind` /
   `.loop0`): the C# seam skips duplicate ids, so identical ids mean a command was
   never executed. The `ut=` on the `VERIFY -> REWOUND` line must be LOWER than
   the one on the `RECORDER-IDLE -> REWIND` line - that is the backward clock,
   readable straight off the log without opening the result JSON.
3. Parsek's own re-fly milestones in the collected `KSP.log` (these are the
   spec's required logContracts, so a miss reds the run):
   ```
   Re-Fly (Rewind-to-Separation) StartInvoke
   Invocation complete
   ConsumePostLoad: restoring bundle with route-retire cutoffUT=
   Restored: recs=... pendingScience=...
   Added <N (>= 1)> supersede relations for subtree rooted at ...
   ```
   The last one is THE loop-closed proof and the count is pinned NON-ZERO on
   purpose: `Added 0 supersede relations` is what a refused batch emits, and
   flight 2's log had no `Added ...` line at all because the invariant check
   refused it first.
4. The run verdict: `results/<runId>.json` PASS with all seven verifiers green,
   `expectations` mismatches 0, `anomalySweep` hits `[]`.

IF IT REDS: the mission verdict now NAMES the cause. Since the 2026-07-26
follow-up, a non-OK seam response surfaces PARSEK's own `msg` reason verbatim
(`Parsek's reason: <reason>`) instead of the machine speculating, and the same
string rides `assertions[rewindSeamAccepted].rejectReason` in the result JSON.
Read that first. Known causes, in likelihood order:
- `Parsek's reason: recording-active` - the flight-1 failure. It should now be
  unreachable: the machine stops the recorder and OBSERVES `recording=false`
  before commanding the rewind. If it reappears, something re-armed a recorder
  between the idle probe and the dispatch, which is a new finding.
- `REJECTED unknown-rp` on the InvokeRewind response: the `rewind-b9` injection
  did not land in the staged run save (check the `[Stage] inject=rewind-b9` line
  and the inject exit code). The spec composes the preset with the `b2-lko-craft`
  template; `run.py`'s inject step targets the RUN save, not the template.
  (Flight 1 CLEARED this one: `rp_b9_root` resolved fine.)
- `Parsek's reason: refly-gate <reason>` - `RewindInvoker.CanInvoke` declined.
  The reason is verbatim; the likely one on this composed fixture is the
  deep-parse PartLoader precondition over the cloned sidecar vessels.
- `rewind-not-observed`: the verb returned OK but the clock never moved back.
  That is a REAL finding, not a harness fault - capture the KSP.log and do not
  "fix" it by relaxing `minUtRegressionSeconds`.
- `never climbed ... m` (RELAUNCH): the rewind put the craft back but the second
  flight never happened. On the `rp_b9_root` slot the craft lands PRE_LAUNCH on
  the pad, so throttle + stage should fly it; if the stage is spent or the craft
  is not the one expected, that is a fixture question, not a tolerance question.
- `still read points=0` (LOOP-POINTS): the craft flew and nothing recorded it.
  Check `autoRecordOnLaunch` is still true after the re-fly load - this is a real
  Parsek question, not a harness one.
- `forbidden matched \[Parsek\]\[ERROR\]` with `AppendRelations invariant
  violation ... reason=empty Points`: that is R1-EMPTY-PROVISIONAL. It should now
  be unreachable in R1 (the loop closes), and it is deliberately still reachable
  in `S4.1-rewind-merge`, which carries it as `expectedFail.bugId`.
- `INVALID tooling-venv` with no KSP boot: step 1 was skipped.

## Roadmap (agreed order; each item named by its Parsek utility)

**The FORWARD build order now lives in `docs/dev/autotest-roadmap.md`** (what we
cannot reproduce yet grouped by cause, plus the ranked dependency-justified sequence
R1-R14 starting from the D1 basics). Items 1-6 below stay as the HISTORICAL record of
the mission lanes that closed and the findings each produced; item 7 stays as the
unscheduled-candidates list. Consult the roadmap doc for what to build next.

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
   broken case without narrowing the correct one. Read that per terminal: for
   the two THRASH terminals it is real evidence (`action warp_to_ut` counts
   exactly 1 per phase on all four flights, against a cap of 500), and for
   `warp-liveness-starved` it is worth nothing, because no archived episode was
   ever even JUDGED. Measured from `warpUtilisation` across every archived
   `harness/results/*_mission.json`, no phase that armed a NATIVE warp reached
   the floor's 180 wall-second minimum window: the longest is COAST-TO-TARGET at
   76.4 s (B7), then CORRECTION-BURN 69.6 s, TARGET-FLYBY 30.2 s,
   PLAN-CORRECTION 3.7 s, PLAN-CAPTURE 0.6 s.
   CLOSED 2026-07-26, and the answer was NOT the one the caveat assumed. Three
   findings, all from primary evidence:
   1. **This floor could not have caught B12 flight 2 either, and the window is
      not why.** Flight 2's thrash CANCELLED the command every other frame
      (3,603 `warp_to_ut` against 3,602 `cancel_warp`, the
      `gate warpToCmd <target>->none` / `none-><target>` pair alternating frame
      by frame), and the fly loop resets the liveness episode the moment
      `warp_to_cmd` clears. The episode never lasted two frames. The THRASH
      counter is what bounds that shape; this floor bounds the POST-FIX
      RESIDUAL, where `coast_native_warp_hold` removed the cancel half of the
      cycle but a crawling rails rate is untouched by that fix. That shape has
      never been flown, is reachable, and nothing else in the stack can see it.
   2. **The floor does not consume `gameSecondsPerWallSecond`**, despite what
      its own rationale used to say. That is a PER-PHASE average; the floor
      computes an EPISODE-LOCAL ratio in the fly loop. On flight 2 the two
      differ by 27x - the phase row reads ~39 (one successful 146,070
      game-second warp burst earlier in the same phase dominates the mean),
      the thrashing episode reads 1.41. Fed the phase number a 5.0 floor is
      silent on the defect it exists for. Both numbers in the docs are correct;
      they measure different things, and only the episode one is a give-up.
   3. **The disarm, not the window, is what protects the long 1x holds** - so
      the window must never be described as that margin. MEASURED, 31 archived
      phase rows across SEVEN phase names run PAST 180 wall-seconds at a ratio
      BELOW the 5.0 floor (REENTRY 428.4 s @ 1.45, DEORBIT 349.8 s @ 1.00, DOCK
      247.1 s @ 1.00, MJ-ASCENT 198.5-199.3 s @ 1.33 across 17 rows, INT-ASCENT
      194.6 s @ 1.55, STATION-ASCENT 194.3 s @ 1.83, PARK 180.2-180.6 s @ 1.00
      across 9 rows), and CAPTURE-BURN has been measured at 138.0 s @ 1.10, only
      42 seconds short of being judged. Every one would FIRE if `warp_to_cmd`
      were left armed across it. CAPTURE-BURN reads `warpCommands=0` on all ten
      archived captures because `_b5_enter_plan_capture` clears the command and
      the PARK entry clears it again; both clears are now pinned by tests.
   WHAT SHIPPED: no constant changed (180.0 / 5.0 stand, so no frame any flown
   mission took can move), both are now anchored on the measurement above rather
   than round, the PROVISIONAL note is gone, and the mechanism is covered by
   `test_shells.WarpLivenessRealMachineTests` - the REAL b5 machine driven
   through `fly_loop` on flight 2's post-fix telemetry (same body, same altitude
   band, same 2.76x rails rate, same measured 1.41 game-s per wall-s), which
   fires the named give-up while the thrash counter stays at 1 issue of 500.
   FIELD STATUS stays unflown, and that is now recorded as the CORRECT state
   rather than a debt: every healthy armed episode we fly is 0.5-76.4 wall-s and
   finishes far inside the window, and reaching the floor in the field would
   mean reintroducing the defect.
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
   measurement, both with `verifiers.expectations.status = PASS` and
   `mismatches = []`. The DERIVED topology is identical to B11/B12 - 6 Destroyed
   boosters, 1 Orbiting/Kerbin flameout core - with ONLY the root's terminal
   changed, which is precisely the discriminator this lane exists to move; the
   per-recording breakdown was read off the live log during the run and is not
   re-verifiable, because `collectLogs.ran = false` kept no KSP.log on either
   flight. B14 touched
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
   `FormatCommitTerminalLine` (and the over-cap summary line) now resolve the
   body TERMINAL-STATE-AWARE: `TerminalPosition.body` first for the SURFACE
   terminals (`Landed` / `Splashed`), `TerminalOrbitBody` first otherwise, each
   falling back to the other. The state-awareness is load-bearing - NOTHING ever
   CLEARS `TerminalOrbitBody`, so a craft that orbited the Mun and later landed
   on Kerbin would otherwise print `terminalState=Landed terminalOrbitBody=Mun`.
   9 xUnit cells.
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
     `commit-landed-foreign-body`, D4 `surface-stationary` (B13) and D4
     `surface-mobile` (B14) - each claimed by the scenario that MEASURED it, and
     each gated by a class-specific log token so neither can be satisfied by the
     other's reading. Both D4 values were previously unclaimed by any scenario.
   - LIVENESS: DESCENT carries FOUR distinctly named give-ups on top of its
     GAME budget - `landing-autopilot-not-enabled` (COMMANDED-vs-OBSERVED off
     `LandingAutopilot.Enabled`), `landing-no-progress` (the independent
     altitude-trend channel, with a separate `altitude-unreadable` name;
     debounced over `LANDING_STALL_DEBOUNCE_FRAMES`, and disarmed while its
     anchor sits below `landingProgressMinDropMeters` AGL where the drop it
     asks for does not exist below the craft),
     `landing-touchdown-timeout` and `landing-vessel-lost` (a crash must read
     as neither a timeout nor a success). LANDED-SETTLE adds
     `landed-never-stable`.
     WHERE THE TOUCHDOWN CARVE-OUT ACTUALLY LIVES (corrected 2026-07-26): the
     hazard is real - MechJeb's `FinalDescent` calls `StopLanding()` on the
     frame it observes `LandedOrSplashed`, so an observed FALSE after touchdown
     is the module reporting SUCCESS - but the guarantee is provided by
     `b5_decide`'s ORDERING, which exits DESCENT on the first landed situation
     BEFORE the supervisor runs. `classify_landing_autopilot` is therefore only
     ever called with `touched_down=False` in flight, and its own touchdown
     conjunct is an order-independent backstop for callers that do not own that
     ordering, not a live path. Both halves are pinned by their own cell; the
     earlier "REQUIRED, not defensive" wording described the hazard correctly
     and the implementation incorrectly.
   - ONE KNOWN GAP, filed rather than papered over (full text in
     `todo-and-known-bugs.md`): DESCENT has no WALL-time bound of its own,
     because the `warp-liveness-starved` floor is armed only by OUR OWN native
     warp and arming it across a phase that legitimately runs at 1x would
     false-flake a healthy landing. It is theoretical on the current pin -
     MechJeb issued zero warp commands across both descents, so there is no
     MechJeb warp to wedge. (The lane originally filed a SECOND gap, that the
     commit-terminal line could not name the body for a `Landed` terminal. That
     one was CLOSED by the Parsek fix described above, not carried.)
   - COVERAGE HONESTY: the DESCENT autopilot supervisor's debounce / re-issue /
     DEAD ladder has NO live coverage - neither flight emitted a non-zero
     `landingApDownStreak` or `landingApReissues` line, because
     `LandingAutopilot.Enabled` read 1 on every DESCENT frame THE SUPERVISOR
     EVALUATED. Not on every polled frame, and the difference is worth stating:
     the archived telemetry shows `landAP=0` on B13's PARK -> DESCENT entry frame
     (ut 21,734.345, decided in PARK, before the engage went out) and on the
     TOUCHDOWN frame of BOTH flights (B13 ut 23,088.285, B14 ut 278,581.702) -
     MechJeb disabling its own module on the landed frame, exactly as the
     touchdown-before-supervisor ordering in `b5_decide` assumes. Same for all
     four named DESCENT give-ups and `landed-never-stable`: never fired live. What
     is live-proven is the happy path plus the two exit gates it crossed
     (touchdown detection on the frame MechJeb disables its own module, and the
     settled dwell).
     WHAT THE LADDER DOES CARRY (2026-07-26): the whole debounce -> bounded
     re-issue -> named DEAD fast-fail now runs through the REAL fly loop
     against a scripted control
     (`test_shells.LandingAutopilotLadderFlightTests`, 6 cells), so everything
     on OUR side of the seam is measured rather than assumed - the debounce
     DEPTH (a flickering channel never reaches a re-issue, which the
     all-disabled cells could not distinguish from a 1-frame debounce), the
     re-issue ACTION reaching the seam carrying the spec's vehicle
     configuration, the bound on how many can be issued, the give-up NAME, and
     the `landingApDownStreak` / `landingApReissues` gate lines an operator
     would grep. Two mutations were used to prove the cells bite: a 1-frame
     debounce reds 2 cells, an unbounded re-issue reds 3. That leaves MechJeb's
     OWN behaviour as the only untested variable, which is exactly what a live
     firing would add and what no harness-side test can substitute for. Do not
     promote the ladder to LIVE-PROVEN on the strength of these cells.
   - PINS CLOSED: four of the five first-flight pins are closed from measured
     data (recordings count re-pinned `{8, 9}` -> `{8, 8}`;
     `descentTimeoutSeconds` trimmed 3000 -> 2200 against measured spans of
     1,353.9 / 1,381.3 game-s; `landedMaxHorizontalSpeedMps` tightened 1.0 ->
     0.5 against a worst measured 0.195 m/s, with the vertical floor left at
     1.0 by decision; `landedDwellSeconds` confirmed at 120). The fifth, the
     wall budgets, is closed AS SIZED: B13 finished at 55% of its mission
     budget and B14 at 50%, so B13 5000 / 5600 and B14 4200 / 4800 stand.
     B13 IS the MOST EXPENSIVE scenario in the suite (2,825 s measured against
     BDOCK-1's 2,164 s).
4. Ledger campaign resumption once career fixtures exist (L1 -> L2+): the
   initiative's END GOAL.
5. B-DOCK first flight - the docking/rendezvous lane (dock-undock recording
   structure) is now IMPLEMENTED (`autotest-bdock-impl`); remaining is the
   headless fixture-forge run (`FORGE-bdock-station` -> harvest -> commit
   `bdock-station-pad`), re-tier BDOCK-1 pending-fixture -> nightly, and the
   first flight (P1-P9 live-proves). It unlocks the D10 route-candidate +
   D5 cross-tree-dock/undock-split recording surface.
6. Eve lane: B15-eve-flyby is LIVE-PROVEN (2026-07-26, full scenario PASS on
   attempt 1 after seven flights; 86 `nextBody=Eve` reads against zero on every
   earlier attempt, flyby periapsis 22,032,532 m, exit to Sun). The INWARD
   unknown is ANSWERED - MechJeb plans an inner window fine, but its interplanetary
   ejection planner sizes the burn at the parking orbit's SEMI-MAJOR AXIS while
   burning at a different radius, so an eccentric park under-ejects badly
   (MEASURED: the same planner priced the same Eve ejection at 652.843 m/s from
   flight 3's ecc-0.08495 park and 775.873 m/s from flight 5's round one, and
   that 123.0 m/s shortfall left the heliocentric leg missing Eve's orbit by
   2.46e9 m). Fixed by the param-gated park round-out trim (`parkTrimEccMax`),
   with a plan-time `reachesTargetOrbit=` verdict so a mis-aimed transfer is
   named at the plan rather than 11.8M game seconds later. See the todo doc's
   B15/B16 section for the full derivation. B16 inherits the fix and still needs
   its first run - it sits at `tier = "operator"` until that flight is green
   (an unflown 4,700 s x retry lane would red a nightly sweep for ~2.6 h a
   night), with a PROMOTE note in its spec. The real Kerbin->Eve
   ejection-window wait (the only budget term with a 14,700,000 game-second
   range) MEASURES at ~11.83e6 game seconds.
7. Candidates (unscheduled): stock-award pattern
   rewrite, nightly rotation shakedown, EVA registry growth (D5/D12 cells),
   an orbital-rendezvous-dock D10 registry value + a same-craft-twice
   identity D18 value (the two B-DOCK coverage gaps).
