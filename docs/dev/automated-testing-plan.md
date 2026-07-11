# Parsek Automated Testing Plan (v2)

Status: RATIFIED v2 (2026-07-11). v1 was reviewed by a clean-context
adversarial pass; v2 integrates the review fixes, the scenario mining
(repo design docs + community resources), the synthetic-recordings
integration, and the Ledger Accuracy Campaign. Companion file:
`automated-testing-scenario-catalog.md` (dimension registry D1-D18,
blocks B1-B10, scenario ladder, regression list R1-R26, F-series fault
scenarios, fixtures).

## 0. Goal and end state

Replace most manual playtesting with an unattended pipeline: scripted
atomic missions flown on the dev PC while Parsek records, then correctness
verified from recording files, save files, logs, and in-game canaries -
including loop playback and, at the top of the ladder, full career
accuracy. End-state targets:

- Every coverage-dimension value (catalog section 1) exercised by at least
  one automated scenario; high-risk dimension PAIRS covered by curated
  combination scenarios; the R1-R26 regression list green on rotation.
- The Ledger Accuracy Campaign end goal: a brand-new scripted career run
  (science + funds + reputation) containing supply routes and repeated
  rewind actions layering new actions onto the timeline, with the ledger
  proven exact against an independent oracle at every session boundary.
- A structured coverage ledger so "exhaustive" is measured, not vibes:
  scenarios declare the dimension values they cover; a report shows
  covered / uncovered / regression status.

## 1. Current state (audited, corrected)

Four detection layers. Shares of historical bugs each could have caught
are ROUGH ESTIMATES from one pass over the bug archives: unit ~40%,
file analysis ~10%, in-game ~30%, human-watch ~20%. The plan attacks the
last two.

Verified facts the plan builds on (all spot-checked by the review):

- Headless seams exist and work: `RecordingStore.LoadTrajectorySidecarForTesting`
  (RecordingStore.cs:6736), `CareerSaveParser` , `LedgerGroundTruthDiff.Compare`,
  injectable `bodyResolver` (TrajectoryMath.cs:187-209), `TestBodyRegistry`.
- In-game framework: 158+ tests / 68 categories, batch campaign isolation
  (TestBatchMarker, .bak capture, crash reconcile, NRE-storm abort),
  auto-export of `parsek-test-results.txt` (InGameTestRunner.cs:3324).
  Trigger is interactive-only (Ctrl+Shift+T); no env hooks exist anywhere
  in Source/Parsek (verified by grep).
- Orbital ghost drift oracle is wired and asserted in-game
  (RenderParityOracle, ComputeFaithfulOrbitParity, RenderParityBaselineTest,
  anomaly lines behind mapRenderTracing). Non-orbital playback has no
  equivalent oracle.
- Log pipeline: validate-ksp-log.ps1 drives a real xUnit pass over KSP.log;
  collect-logs.py snapshots state.
- CORRECTION (review finding): the DefaultCareer "real fixture corpus" does
  NOT load under current code. 9/14 .prec are pre-v0-reset TEXT sidecars
  (hard-rejected at RecordingStore.cs:6733), the binary ones carry stale
  generations vs CurrentRecordingSchemaGeneration=4, zero .pcrf, format
  version mismatches. It serves byte-copy injection only. Phase 0 therefore
  REGENERATES the corpus first (section 7).
- CORRECTION: auto-record on launch / EVA / first-modification are shipped
  defaults (ParsekSettings.cs:34-42). The automation problem is CONTROL
  (turning it off for fixture arrangement and playback sessions, starting
  and committing deliberately), not adding auto-record.

## 2. Automation stack (decided)

Primary: kRPC + MechJeb2 + KRPC.MechJeb driven by one external Python
orchestrator. Secondary: kOS boot scripts (RAMP / ElWanderer libraries)
for self-flying craft. Details and sources in catalog section 5 and the
research report.

- Pin an exact kRPC tag/commit (target 0.5.4; `git fetch --tags` in
  mods/krpc and verify the TestingTools flag set AT THAT TAG - the local
  clone is master-2026 and proves only master). TestingTools is NOT in
  release binaries; the setup script builds it from source against KSP
  1.12.5 DLLs (TestingTools.csproj exists; budget this step).
- TestingTools AT v0.5.4 (VERIFIED at the tag, 2026-07-11; see
  design-autotest-stack-setup.md GT-2) gives exactly these RPCs/properties:
  CurrentSave, LoadSave, RemoveOtherVessels, SetCircularOrbit, SetOrbit,
  ClearRotation, ApplyRotation. The `--krpc-auto-load-*` boot flags, Quit(),
  and SetLanded() do NOT exist at v0.5.4 - they exist ONLY on unreleased
  master (a single commit past the tag, "RPC Deprecation #926"). So at the
  pinned release: BOOT is the M-A2 seam LoadGame verb (kRPC RPCs are not
  available at the main menu), QUIT is the M-A2 seam FlushAndQuit verb
  (commit-safe; master's Quit() is a bare Application.Quit() that does NOT
  trigger Parsek scene-exit commit), and landed-start fixtures ride stock
  Scenario saves or SetOrbit, NOT SetLanded.
- KRPC.MechJeb version pairing (Genhis 0.7.1 vs darchambault 0.8.1) is
  resolved at install time in the setup script; pin whichever pairs with
  the chosen kRPC tag + MechJeb build.
- Licensing: all four mods GPLv3. RPC use does not infect Parsek. Any
  Parsek-side kRPC service add-on links both kRPC and Parsek.dll and is
  clean ONLY because it is never distributed - state that, do not rely on
  a GPL label. The primary control seam avoids kRPC linkage entirely
  (section 3, file-drop).
- KSP cannot run headless; use a real small low-detail window in a
  DEDICATED cloned automation KSP instance (protects dev saves; pins
  MAX_PHYSICS_DT, framerate cap, graphics preset, autostrut policy,
  run-in-background). The harness copies Parsek.dll from bin/Debug and
  verifies via the UTF-16-grep recipe; guard against a concurrent dev
  build racing bin/Debug (copy to a staging path first, verify hash).

## 3. Control architecture

### 3.1 The command seam (Phase 1, replaces v1's H4-as-kRPC-service)

`ParsekTestCommands`: a file-drop command channel, env-gated
(`PARSEK_TEST_COMMANDS=1`), polled by a DDOL addon in the automation
instance only. Commands are one-line entries in a command file next to the
save; results append to a response file (both grep-able, crash-tolerant).
No kRPC linkage, no GPL entanglement, testable in xUnit (pure
parse/dispatch core).

Command set (grows per phase): SetSetting (e.g. autoRecordOnLaunch=false),
StartRecording, StopRecording, CommitTree, DiscardTree, RecordingState,
LoadGame(save,name) (the BOOT CHANNEL: boots the automation instance into a
save from the main menu, since kRPC RPCs are not available there),
StartLoopPlayback(recordingId|missionId), StopPlayback, EnterWatchMode,
InvokeRewind(rpId) + report of the 5-precondition gate, AnswerMergeDialog
(choice), RunInvariantReport, RunTests(category), MissionMark(label),
FlushAndQuit (commit-safe quit: force scenario save, then quit).

Additions from the design-doc re-review (these make whole tracks drivable;
spec them in Phase 1 even if implemented later):
- KscAction(type, args): stock KSC-side actions kRPC does not expose -
  tech unlock, facility upgrade, kerbal hire/dismiss, strategy activation.
  Without this, L1/L2 are undrivable as specced.
- SealSlot / StashSlot / FlySlot: the D9 MergeState transitions.
- RouteCommand(create/confirm/pause/resume/link/retarget/cadence): the
  route lifecycle is dialog- and UI-mediated; Tier 5 needs this.
- MissionConfig(legTrim/exclude/clone/period): mission shaping (S3.4).
- TimeJump(targetTip): the relative-state time jump (D18/S4.8).
- SimulateStockSwitchClick(vesselPid, site=map|ts|ksc): arms the real
  StockActionIntentMarker and invokes the PATCHED stock handler. kRPC's
  active_vessel setter bypasses the patched path entirely - without this
  command, switch-segment scenarios certify the wrong code path and go
  green while the patch rots.
- CrashAfterJournalPhase(X): test-only crash injection for merge-journal
  recovery (F7).

This seam is what makes T9/T12-class scenarios (rewind invoke, merge
dialog answers) drivable at all - the review found v1 had no control
surface for them. A kRPC ParsekTestService remains an OPTIONAL later
convenience; every capability lands in the file seam first. Because the
roadmap's Gloops extraction will relocate engine files and split
assemblies, the seam and the analyzer pin against PUBLIC TEST APIs, never
file/line internals.

### 3.2 In-game hooks (all env-gated, inert in normal play)

- H1 Auto-run batch: `PARSEK_AUTORUN_TESTS=<all|categories>` in
  TestRunnerShortcut (already DDOL) after scene settle.
- H2 Exit after tests: `PARSEK_AUTORUN_EXIT=1` after ExportResultsFile.
- H3 Stable markers: `[Parsek][Info][TestRunner] BATCH_COMPLETE total=N
  passed=N failed=N skipped=N` + MissionMark lines for orchestration.
- H4 = the command seam above (Phase 1, not Phase 3).
- H5 In-game RecordingInvariants category: same invariant set as the
  offline analyzer, walked against live RecordingStore.

### 3.3 Recording lifecycle policy (resolves the v1 ordering contradiction)

Session-start sequence: boot the instance -> seam LoadGame into the injected
save (the boot channel; there are no TestingTools boot flags at the pinned
v0.5.4 release) -> SetSetting pins (auto-record OFF, tracing flags as the
scenario needs) -> fixture arrangement. Missions that test recording start it
explicitly (StartRecording) and commit deliberately: CommitTree via the seam,
or scene-exit commit by kRPC scene change to Space Center, THEN FlushAndQuit.
Fixture arrangement (SetOrbit teleports, RemoveOtherVessels) always happens
with recording off - a teleport recorded as a trajectory is a corrupted
fixture, not a test. Scenarios that specifically test auto-record behavior
(D1 values) re-enable it as their first step.

## 4. Coverage model (how "exhaustive" stays structured)

The combinatorial space (initial state x vehicle x actions x destination x
Parsek features x career mode x warp x scene) is far too large to
enumerate. Structure:

1. **Dimension registry** (catalog section 1, D1-D18; D18 = ghost chains /
   paradox prevention, added by the design-doc re-review which found the
   mod's headline promise had zero automated observers; D15 UI is out of
   scope except the TimelineBuilder pure projection) is the single source
   of truth for what exists to cover.
2. **Scenario specs**: every scenario is a declarative file (YAML) in
   `harness/scenarios/`: id, tier, fixture (template save + craft +
   injected recordings), mission script ref, dimension values covered,
   expectations manifest, runtime budget, retry policy.
   The manifest vocabulary must express the designs' edge policies, not
   just counts (re-review must-fix). Schema blocks:
   - recordings: count, tree shape, section frame kinds, event kinds,
     resource deltas with tolerances;
   - perRecording: terminalState, endUT range (incl. stock-deletion-UT
     cap), predictedTail flag, mergeState;
   - rewind: supersedeCount, tombstoneCount, rpState (incl. reaper);
   - loop: LoopStartUT/EndUT, first-play floor;
   - route: hold-reason strings;
   - world: vesselPid -> resource totals and roster states, verified by a
     save-file world-diff verifier (CareerSaveParser extension - it
     already parses per-vessel resource totals);
   - logContracts: required decision/amber lines. The designs define
     correctness as logged decisions plus fail-closed ambers ("observable
     from logs alone" is a named principle in the missions, rewind, and
     finalization docs) - hold reasons, amber fail-closed states,
     chain-walker decisions, and cache-application summaries are
     assertable TODAY as log contracts with zero new seams; route
     assertions there before building heavier machinery.
3. **Coverage ledger**: a generated report (harness tool) mapping registry
   values -> scenarios covering them -> last green run. Uncovered values
   are the visible backlog. This is how we stay structured instead of
   drowning in combinations.
4. **Combination strategy**: (a) each-value-once via the cheapest tier
   that reaches it (B-blocks first); (b) curated high-risk PAIR list
   (e.g. relative-frame x high-warp, loop x SOI transition, rewind x
   routes, discard x contracts, cold-load x every module) driven by the
   bug archives - each pair gets a dedicated scenario; (c) the regression
   list R1-R26 pins historically bug-producing points regardless of pair
   logic; (d) the L-track (section 6) runs its own action-set
   combinatorics for the ledger, where enumeration IS tractable
   (9 modules x 3 career modes x action sets).
5. **Growth rule**: a new Parsek feature adds its dimension values to the
   registry in the same PR; the coverage report immediately shows the gap.

## 5. Scenario program

The ladder (catalog sections 2-3): B1-B10 atomic blocks first, then tiers
0-5, then torture tests (Apollo-style compact double-dock, one Jool-5 leg,
Kessler swarm of satellites for simultaneous-ghost stress). Autopilot
feasibility per archetype and known failure modes are cataloged; Tier 0-3
is all TRIVIAL/SCRIPTED via kRPC.MechJeb; rover/SSTO/EVA-jetpack/Eve/ISRU
are deferred (HARD, hand-built autopilots).

Stock Scenario saves (Space Station 1, Mun Orbit, Powered Landing,
ARM_Asteroid1/2, Dynawing Final Approach, EVA in Kerbin Orbit) are
first-class fixtures: they isolate recorder-critical maneuvers with zero
ramp-up flying and ship version-matched with KSP.

Multi-session orchestration (catalog section 6) is a first-class harness
capability from Phase 3 on: fly-commit-restart-observe cycles are the ONLY
way to reach re-fly merges, routes, synodic re-aim cadence, and loop
self-overlap.

## 6. Ledger Accuracy Campaign (L-track)

Dedicated track, runs alongside the mission tiers, because the ledger has
what nothing else has: a computable independent oracle. Every stock career
total (funds, science, rep, contract states, tech, facilities, roster) can
be recomputed from the action history and diffed against the ledger's
ERS/ELS output AND the raw persistent.sfs (CareerSaveParser +
LedgerGroundTruthDiff already implement the diff; the harness supplies
generated action histories and the oracle bookkeeping).

Spine: every mission and L-script emits a machine-readable ACTION MANIFEST
(what was earned / spent / reserved / killed, at what UT). The oracle
computes expected state from accumulated manifests; any nonzero diff at
any checkpoint is a caught bug localized to a UT window. Career-mode axis:
every L-level runs in Career, Science, and Sandbox (module activation
differs).

Two oracle-correctness rules (re-review must-fixes - these are the two
ways the L-track could produce confidently wrong verdicts):
- INDEPENDENCE OF AMOUNTS: manifests capture stock-emitted reward/cost
  magnitudes AT EVENT TIME (GameEvents callbacks, stock log lines,
  pre-recalc snapshots of the stock currency singletons) - never by
  calling Parsek's recalculation code, which would make L5 circular
  (game-actions design 15.6 warns exactly this). Documented exception:
  the reputation curve is asserted via SetReputation semantics (15.1).
- RATIFIED-RESIDUAL CARVE-OUT: on a plain (non-rewind) discard, route
  funds and physical route cargo persist BY DESIGN (logistics 10.6,
  maintainer-ratified, [Rec-3 residual] diagnostic). L3's "discard rolls
  back exactly the flight's effects" whitelists free-standing route rows
  on non-rewind discard and asserts the residual diagnostic line instead.
  Generally: rollback oracles consult ratified-behavior carve-outs before
  declaring drift.

- L0: per-module xUnit floor (largely exists) + ground-truth diff wired
  into EVERY harness run as a standard verifier.
- L1: single-module action scripts, stock actions only (complete contract,
  research node, upgrade facility, hire/dismiss, strategy, milestone,
  EVA science) -> snapshot -> zero drift. Automated BUG-A/BUG-F passive
  safety (R1/R2). Includes warp, scene changes, save/load, cold restart.
- L2: cross-module interactions (contract completion touches funds + rep +
  science; strategy currency conversion; milestone-fed rewards; facility
  spend + refund windows).
- L3: actions x recording lifecycle: earn during recorded flight; commit
  vs discard vs auto-merge; discard rolls back exactly the flight's
  effects (R5); epoch isolation after revert.
- L4: actions x rewind/re-fly: reservations across rewinds, tombstones
  (kerbal death + bundled rep), supersede flips, recalc-from-UT=0 with
  many actions at distinct UTs; then TIMELINE LAYERING: act, rewind to
  mid-timeline, re-fly divergent branch, layer new actions, repeat K
  times, verifying after each layer. Layering is save manipulation plus
  short flights - cheap to automate, and it is where the combinatorial
  depth lives.
- L5: the grand oracle run (= S5.7): fresh career from zero, scripted
  multi-mission progression with supply routes dispatching across warp
  cycles, repeated rewinds interleaved, oracle diff at every session
  boundary. This is the end-goal scenario: if L5 stays green, the ledger
  is exact under everything we know how to throw at it.

## 7. Offline Recording Analyzer (Phase 0) - corrected

xUnit suite + thin CLI, input = any save directory. Invariants (corrected
per review):

1. UT monotonicity per section and per flat Points list.
2. NO DOUBLE-COVER of a UT span by sections. (NOT "no gaps": BG on-rails
   spans legitimately emit no TrackSections, and
   TrackSection.boundaryDiscontinuityMeters models legitimate seam
   discontinuities. Gaps are allowed where orbit segments cover the span;
   uncovered-by-anything spans are flagged WARN, not FAIL, initially.)
3. v6 RELATIVE contract: out-of-range lat/lon values only in RELATIVE
   sections and only with anchorRecordingId present; ABSOLUTE sections
   with out-of-range values WARN until KSP longitude normalization
   behavior is cited, then FAIL (re-review: avoid false positives).
4. Part-event PIDs resolve against the paired snapshot (100000 + idx*1111).
5. Schema gate: every sidecar passes IsRecordingSchemaCompatible; the
   analyzer inventories and reports reasons.
6. Resource manifests: consistent WHERE PRESENT (optional for Gloops /
   showcase ghosts - presence rules encoded per recording kind).
7. Tree topology: all parent/branch/chain/anchor/supersede/tombstone links
   resolve, no cycles. Chain-index contiguity is scoped PER
   (ChainId, ChainBranch) - ChainBranch > 0 marks parallel ghost-only
   continuations (flight-recorder 9A.4) - with a supersede-boundary
   exemption for HEAD/TIP splits (verify against RecordingOptimizer's
   supersede guard before shipping).
7b. Annotation sidecars (.pann) consistent with their .prec (staleness
   check, rendering design 17.3.1).
8. Ledger: internal consistency, ERS/ELS computable, ground-truth diff vs
   the save's career totals.
9. RewindPoint .sfs parse + RecordingPaths ID validation.
10. Codec round-trips at the CODEC seams (RecordingTreeRecordCodec,
    RecordingManifestCodec, sidecar codecs) - NOT at ScenarioModule level
    (not xUnit-drivable; known constraint).

Every invariant cites its enforcing/defining code in the test source
before implementation, so wrong contracts die in review, not as false
alarms.

**Fixture prerequisite (review must-fix #1):** regenerate the real-file
corpus at current schema. Sources: (a) synthetic families via
RecordingBuilder/ScenarioWriter at current generation; (b) one scripted
in-game session (or early harness smoke runs) producing genuinely-real
files; (c) thereafter, FIXTURE HARVESTING - curated recordings from every
green harness run get promoted into the corpus. Versioning policy: each
fixture set carries its schema generation stamp; a generation bump flags
all stale fixtures and the regeneration script re-produces them; the
analyzer refuses mismatched fixtures loudly. HARVESTED fixtures cannot be
regenerated by script (no-migration policy): the coverage ledger tracks
fixture provenance (synthetic vs harvested), and a generation bump emits a
re-harvest queue - scenarios whose green runs must re-donate fixtures
before the invariants they alone backed count as covered again.

## 8. Synthetic recordings integration

The existing synthetic system is a pillar, not a parallel world:

1. Same analyzer, three data sources: builder-synthetic, mission-flown,
   and harvested fixtures all pass through the identical invariant suite.
   Builders that emit invariant-violating data get caught; analyzer rules
   that are wrong get exposed by known-good-by-construction builder output.
2. Injection is the cheap playback path: InjectAllRecordings +
   inject-recordings.ps1 seed playback-observation sessions (loop canaries,
   parity baselines, map render) with zero flying. Flown missions are
   reserved for what synthesis cannot produce: real physics seams, real
   SOI numerics, real ledger interactions.
3. Injected base states unlock expensive scenario prerequisites: a tree
   with a Crashed sibling + RP for re-fly scenarios; a committed dock-run
   tree for route creation; a Jool-5-shaped tree for multi-moon holds.
   New builder presets to add: Crashed-sibling tree, route-candidate tree,
   multi-moon tree.
4. Differential contract checks, INVARIANT-LEVEL ONLY (demoted per
   re-review): synthetic recordings are approximations (identity
   rotations, hand-placed points); the check is shared invariant
   compliance between synthetic and flown analogs, never value equality -
   anything stronger produces permanent noise.
5. The generators become the fixture-regeneration engine for section 7's
   versioning policy.

## 9. Verification stack (every run)

1. Mission validity gate (kRPC telemetry): mission failed to fly =>
   verdict INVALID (autopilot flake), not Parsek failure. Retry once;
   two invalids => quarantine entry.
2. Offline Recording Analyzer over the produced save.
3. Log validation + LogContract rules. KILLED-RUN VERDICT: a timeout-kill
   produces a log that legitimately ends mid-session; the validator gets a
   killed-run mode suppressing marker-pairing rules (review must-fix).
4. parsek-test-results.txt: zero failures in auto-run categories.
5. Anomaly sweep: zero parity-drift / icon-jump / line-blink lines during
   playback sessions (documented bounded exceptions).
6. Expectations manifest from the scenario spec (tolerances, never golden
   trajectories).
7. Ledger oracle diff (L-track verifier, on every run that has actions).
8. collect-logs.py snapshot on any failure.

## 10. Operational spine

- **Cadence tiers**: per-PR = existing dotnet test + Phase 0 analyzer on
  fixtures (minutes). Daily = B1-B3 + L1 + one playback session
  (~30-45 min). Nightly = full B-set + active tier scenarios + regression
  rotation slice (budget 3-6 h; KSP boot alone is 1.5-4 min per process,
  ~15 boots/night). Weekly = torture tests + full R-list + L4/L5 as they
  come online.
- **Budgets**: every scenario spec declares a wall-clock budget; the
  orchestrator timeout enforces it (timeout => kill => killed-run verdict,
  never hang). Real-time planning numbers per archetype are in the catalog
  (LKO 6-10 min, Mun round trip 25-45 min, docking 15-30 min); harness
  records actuals from day one.
- **Flake policy**: retry-once-then-invalid for autopilot stages; a
  persistent flake ledger (scenario id, stage, rate); >20% flake rate over
  a week => scenario quarantined and flagged for redesign (smaller craft,
  more RCS, different autopilot) - MechJeb docking on heavy craft is the
  known worst offender.
- **Triage**: every red run auto-collects (collect-logs.py label =
  scenario id); verdict taxonomy INVALID / PARSEK-FAIL / KILLED / FLAKE /
  EXPECTED-FAIL keeps nightly red actionable. EXPECTED-FAIL (quarantined
  by known bug) is keyed to todo-doc bug IDs so scenarios targeting
  in-flux subsystems (re-aim: S3.3/S3.5, R10-R12) can land early without
  poisoning triage, and auto-promote to live when the bug ID closes.
- **Instance profiles**: TWO pinned automation instances (re-review fix -
  the minimal-mods policy contradicted D17 coverage): stock-minimal
  (default; kRPC + MechJeb + Parsek only) and modded-compat (adds
  Waterfall + SWE, ReStock/+, BetterTimeWarp, PersistentRotation).
  D17 scenarios and R25/R26 run only on modded-compat; NRE-storm detector
  scoped per profile.
- **Runner**: Windows scheduled task in an interactive session (KSP needs
  a GPU + interactive desktop); the dev PC stays usable because runs are
  scheduled off-hours. Results land in results/ + a one-line summary file
  the next interactive session (or a chat session) can read.

## 11. Phased roadmap

- **Phase 0 (no KSP, no new mods)**: fixture-corpus regeneration +
  versioning policy; offline Recording Analyzer with corrected invariants;
  wire ground-truth diff as a standard verifier; non-orbital parity oracle
  headless half. Exit criterion for the oracle (re-review must-fix): it
  resolves TrackSection.referenceFrame per UT and dispatches through the
  PRODUCTION resolvers (TryResolveRelativeWorldPosition, body-fixed
  primary rules, authored-coverage spans only) - a naive flat-Points
  reader would itself commit the documented RELATIVE-misread bug and
  false-alarm on every parent-anchored debris recording. Independent of
  everything else; start immediately.
- **Phase 1 (Parsek hooks)**: command seam (3.1) with the starter command
  set (incl. LoadGame, the boot channel); H1-H3; H5. First fully unattended
  KSP-open cycle: boot, seam LoadGame into the injected save, auto-run batch,
  exit, analyze.
- **Phase 2 (stack install + smoke)**: automation instance; pin + build
  kRPC tag / TestingTools / MechJeb / KRPC.MechJeb via setup script;
  harness/run.py + B1 + B2 smoke end-to-end including deliberate
  commit-then-quit; scenario spec format + coverage ledger tool.
- **Phase 3 (breadth)**: B3-B8; L1-L2 (requires KscAction seam commands);
  stock-Scenario fixtures; playback sessions (loop canaries at multiple
  warp rates); fixture harvesting; multi-session orchestration primitive;
  S1.5 rewind loop; F-series fault-injection family (boot cycles only -
  highest severity-per-minute in the plan).
- **Phase 4 (depth)**: B9-B10; L3-L4 (timeline layering); T4 tier
  (re-fly, cross-tree, station) including S4.7 chain-rewind paradox
  scenario and S4.8 time jump (D18); non-orbital parity oracle in-game
  wiring; regression rotation R1-R26.
- **Phase 5 (end goal)**: Tier 5 scenarios; routes end-to-end; L5 grand
  oracle career run; torture tests; nightly/weekly cadence fully armed.

Each phase lands via the normal worktree -> branch -> PR flow. Phase exit
criteria: the coverage ledger shows the phase's target dimension values
green for 3 consecutive scheduled runs.

## 11b. Implementation process (modular, per development-workflow.md)

This plan is the STRATEGY document; it is deliberately not a design doc.
Implementation follows docs/dev/development-workflow.md per MODULE:

- Steps 1-2 (vision + gameplay scenarios) are DONE for the initiative:
  the vision statements and the scenario catalog are those artifacts.
- Step 3: each module below gets its own design doc in the house format
  (Problem / Terminology / Mental Model / Data Model / Behavior /
  Edge Cases / What Doesn't Change / Diagnostic Logging / Test Plan with
  "what makes it fail" justifications) before any code. Modules touching
  serialization (M-A2 command file format, M-A5 fixture stamps, M-B2
  manifest schema) are "new data that persists" - full workflow, no
  shortcuts.
- Step 4: per module, Explore + Plan agents produce the ephemeral task
  breakdown (one agent session / 1-3 files / clear done condition per
  task, TaskCreate + blockedBy ordering); clean-context implement ->
  clean-context review -> clean-context fix. Plans are consumed, not
  stored; the design doc and task list are the durable artifacts.
- Step 5: land via PR (per current CLAUDE.md: PR-based landing, no local
  merges into the main checkout; KSP deploy is intentional-only).

Module decomposition (buildable units, each = one design doc + one
plan/build/review cycle; phase mapping in parentheses):

| Module | Contents | Depends on |
|---|---|---|
| M-A1 Offline Recording Analyzer | invariant framework, per-invariant rules, CLI wrapper (Ph 0) | - |
| M-A2 Command seam | ParsekTestCommands file format, poll addon, dispatch core, response protocol (Ph 1) | - |
| M-A3 Autorun hooks | H1-H3, H5 in-game invariant category (Ph 1) | - |
| M-A4 Fixture regeneration + versioning | corpus rebuild, generation stamps, re-harvest queue, builder presets (Ph 0) | M-A1 |
| M-A5 Harness core | run.py, KSP lifecycle, scenario spec schema, coverage ledger tool (Ph 2) | M-A2, M-A3 |
| M-A6 Stack setup script | pin+build kRPC/TestingTools/MJ/KRPC.MechJeb, automation instance provisioning (Ph 2) | - |
| M-B1 Mission library core | B1-B8 mission modules + stock-Scenario fixtures (Ph 2-3) | M-A5, M-A6 |
| M-B2 Action manifest + ledger oracle | manifest schema, stock-amount capture, world-diff verifier (Ph 3) | M-A5 |
| M-B3 L1-L2 ledger scripts | KscAction commands + per-module action scripts x 3 career modes (Ph 3) | M-A2, M-B2 |
| M-B4 F-series fault injection | fault fixtures + CrashAfterJournalPhase + verdicts (Ph 3) | M-A1, M-A3 |
| M-B5 Playback sessions | injection-seeded loop/parity sessions, anomaly sweep verifier (Ph 3) | M-A3, M-A5 |
| M-C1 Multi-session orchestration | fly-commit-restart-observe primitive (Ph 3-4) | M-A5 |
| M-C2 Rewind/re-fly + chain scenarios | S1.5, S4.7, S4.8, B9, TimeJump + rewind seam commands (Ph 4) | M-A2, M-C1 |
| M-C3 L3-L4 + timeline layering | (Ph 4) | M-B3, M-C2 |
| M-C4 Non-orbital parity oracle | headless half then in-game wiring (Ph 0 + 4) | M-A1 |
| M-D1 Routes + Tier 5 + L5 grand oracle | (Ph 5) | M-C1..C3 |

Independent start lanes: {M-A1, M-A2, M-A3, M-A6} have no dependencies
and can begin in parallel worktrees immediately after ratification.

## 12. Risks and open questions

- (CLOSED 2026-07-11) kRPC 0.5.4-at-tag flag set: VERIFIED at the tag. The
  `--krpc-auto-load-*` flags, Quit(), and SetLanded() are confirmed ABSENT at
  v0.5.4 (present only on unreleased master); see
  design-autotest-stack-setup.md GT-2/GT-3. Boot now goes through the M-A2
  seam LoadGame verb, quit through FlushAndQuit. KRPC.MechJeb pairing
  (genhis 0.7.1 for kRPC 0.5.4) resolved at install.
- Residual risk: no SetLanded at the pin. Landed-start scenarios ride stock
  Scenario fixtures (or SetOrbit) until a seam equivalent is justified; a
  seam-side landed-placement verb is deferred, not planned, unless a scenario
  demands a landing pose no stock fixture provides.
- MechJeb landing/docking flakiness bounds how far TRIVIAL automation
  carries; budgeted by the flake policy, may force kOS RAMP for some
  archetypes.
- Physics nondeterminism bounds assertion tightness; tolerances are per-
  scenario and tuned from actuals (InGameFixtureMath float-grid approach).
- Long-horizon scenarios (synodic re-aim cadence, inter-body routes) cost
  real wall-clock even at max rails warp; may need TestingTools SetOrbit +
  UT-jump orchestration rather than honest warping - validate that UT
  jumps do not themselves distort the systems under test.
- The 40/10/30/20 layer-share numbers are estimates, used only for
  prioritization, not reporting.

## 13. References

Catalog: `automated-testing-scenario-catalog.md`. Research + audit + review
reports: produced 2026-07-11 (agent outputs; key claims folded into this
doc). Local clones: `mods/{krpc,MechJeb2,KOS,KRPC.MechJeb}`. Key external:
krpc.github.io/krpc (TestingTools README), genhis.github.io/KRPC.MechJeb,
github.com/xeger/kos-ramp, github.com/ElWanderer/kOS_scripts,
github.com/KSP-KOS/KSLib (MIT).
