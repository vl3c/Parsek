# Design: Autorun Hooks (Module M-A3)

Status: DRAFT (2026-07-11). Module M-A3 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, sections 3.2, 7, 9, 11b). This is the
per-module design doc the plan's section 11b requires before any code. Sibling
modules: M-A1 (offline Recording Analyzer, owns the invariant core this doc
consumes), M-A2 (command seam), M-A5 (harness core / orchestrator).

## Problem

The in-game test framework (`InGameTestRunner`, 158+ tests across 68 categories)
is the only layer that can exercise ghost visuals, PartLoader resolution, live
crew roster, CommNet, and the batch campaign-isolation machinery. Today it is
reachable only by a human pressing Ctrl+Shift+T and clicking Run All / Run
category (the Run All / Run category buttons in `TestRunnerShortcut.DrawWindow`). An
unattended nightly pipeline (plan section 10) cannot press that button, cannot
read the pass/fail outcome without a human eyeballing a table, and cannot make
KSP exit so the orchestrator can move to the next scenario. The result today is
that ~30 percent of the historical bug surface (in-game layer, plan section 1)
stays behind a manual gate.

M-A3 removes that gate with four env-gated hooks so an external orchestrator can:
boot KSP into a prearranged save, have a test batch run itself once the scene
settles (H1), read a stable machine-readable completion marker from the log (H3),
walk the live `RecordingStore` through the same structural invariant core the
offline analyzer uses (H5), and have KSP quit itself cleanly without corrupting
the campaign save (H2). Every hook is inert when its env var is unset, so normal
interactive play and normal `dotnet test` are byte-identical to today.

## Terminology

- Autorun batch: a test batch started by H1 in response to
  `PARSEK_AUTORUN_TESTS`, as opposed to a human-initiated batch from the
  Ctrl+Shift+T window. The two use the identical runner entry points; "autorun"
  names only the trigger.
- Scene settle: the concrete condition H1 waits for before invoking the runner,
  so tests do not run against a half-initialized scene. Defined precisely in
  Behavior (H1).
- Arming / single-fire: H1 fires the runner at most once per scene-entry. "Arm"
  = the hook is enabled and has not yet fired for the current scene.
- BATCH_COMPLETE line: the grep-stable machine-readable log line H3 emits at
  batch end. Its exact format is a CONTRACT consumed by the orchestrator.
- Invariant core: M-A1's pure, file-I/O-free `IRecordingInvariant` rule set that
  runs over an `AnalyzerModel` (materialized `Recording` / `RecordingTree` /
  `TrackSection` objects plus tombstones / supersede rows / raw ledger). Owned by
  M-A1; consumed by M-A3's H5, which builds an `AnalyzerModel` from live state
  (types defined below).
- Killed-run verdict: the plan's timeout self-defense (section 9 item 3). If a
  batch hangs, the EXTERNAL orchestrator kills the KSP process; the hooks do not
  implement their own watchdog. Out of scope for M-A3 (see What Doesn't Change).

## Mental Model

```
  external orchestrator (M-A5, Python)
        |
        | sets env before launch:
        |   PARSEK_AUTORUN_TESTS=RecordingInvariants,GhostVisual
        |   PARSEK_AUTORUN_EXIT=1
        v
  KSP process boots ---> loads the prearranged save ---> scene settles
        |                                                     |
        |                              H1 (TestRunnerShortcut.Update poll):
        |                              armed? scene settled? crash-reconcile done?
        |                                                     |
        |                                                     v
        |                              runner.RunCategory("RecordingInvariants")
        |                              (or RunAll if PARSEK_AUTORUN_TESTS=all)
        |                                                     |
        |                              RunBatch coroutine (UNCHANGED core):
        |                                CaptureBatchBaseline -> tests ->
        |                                teardown restore -> ExportResultsFile
        |                                                     |
        |                              H3: emit BATCH_COMPLETE line   <--- H5 category
        |                              (in the batch-end region,           runs here as
        |                               after ExportResultsFile)           an ordinary
        |                                                     |            in-game test
        |                              H2 (if PARSEK_AUTORUN_EXIT):        that walks the
        |                                restore already done, export      live store
        |                                already done -> quit KSP
        v
  orchestrator greps log for BATCH_COMPLETE + reads parsek-test-results.txt
```

Ordering is the whole game for H2. The batch-end region of `RunBatch`
(`InGameTestRunner.RunBatch`) already runs, in order: NRE-storm
sampling, `EndBatchExceptionMonitor`, isolation teardown
(`TeardownDiskOnlyIsolation` / `CleanupBatchFlightBaselineSave`),
`ClearTestBatchMarker`, then `ExportResultsFile`, then the optional Space Center
bounce recovery. H2 attaches AT THE END of that chain, after export. A quit that
fires before the teardown restore would leave `persistent.sfs` holding the
test-batch mutations (the clean `.bak` never re-applied) and corrupt the
campaign. So H2 is not "quit when tests finish"; it is "quit as the last step of
the existing teardown ordering, after restore and after export."

H1, H3, and H5 are additive and independent of that ordering:
- H1 is a new arm/fire block in `TestRunnerShortcut.Update`, gated on the env var
  and on scene-settle, that calls the SAME `RunAll` / `RunCategory` the button
  calls.
- H3 is one new log line in the batch-end region.
- H5 is a new in-game test CATEGORY (`RecordingInvariants`), discovered and run
  by the existing runner like any other category. It is not a hook into runner
  plumbing at all; it is test content that happens to be what the autorun most
  wants to run headlessly.

## Data Model

No serialized data. No ConfigNode schema change. No save-file footprint. The
hooks read process environment variables and in-memory runner state only. This
is deliberate (see Backward Compatibility): a corrupted or malformed env var can
never persist into a save.

### Env var surface (the external contract)

```
PARSEK_AUTORUN_TESTS   value: "all" | "<category>[,<category>...]"
                       unset/empty  -> H1 inert (no autorun)
                       "all"        -> runner.RunAll()
                       "Cat"        -> runner.RunCategory("Cat")
                       "A,B,C"      -> sequential RunCategory per token
                                       (see Behavior H1 multi-category)
                       case-sensitive category match (categories are
                       Ordinal-compared everywhere in the runner)

PARSEK_AUTORUN_EXIT    value: "1"
                       "1"          -> H2 armed: quit after teardown+export
                       anything else/unset -> H2 inert
```

### H3 line format (the versioned orchestrator contract)

```
[Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=<N> passed=<N> failed=<N> skipped=<N> category=<sel> scene=<Scene>
```

- Prefix `[Parsek][INFO][TestRunner] ` is produced by `ParsekLog.Info`
  (ParsekLog.cs:461 renders `[Parsek][{level}][{subsystem}] {message}`; the level
  token is the uppercase `INFO`, not the `Info` casing the plan's section 3.2
  wrote illustratively).
- `v1` is the CONTRACT VERSION. Any change to the token set or their meaning
  bumps this to `v2` and updates the LogContract test (see Test Plan). The
  orchestrator matches on `BATCH_COMPLETE v1 ` so a future `v2` line does not get
  silently misparsed by an old orchestrator.
- `total` = the runner's `considered` count (tests with `Status != NotRun` at
  batch end, the same quantity the existing "Test run complete" summary logs at
  InGameTestRunner.cs:1851-1853). `passed`/`failed`/`skipped` = `runner.Passed`
  / `Failed` / `Skipped`.
- `category` = the selector that produced this batch: `all`, a single category
  name, or `multi:<count>` for a multi-category autorun (each constituent
  category also emits its own line; see H1 multi-category).
- `scene` = `HighLogic.LoadedScene` at emit time. Multi-scene runs emit one line
  per batch (per scene), matching the per-scene accumulation in
  `parsek-test-results.txt`.
- Values never contain spaces (category names are single tokens; scene is an
  enum). This keeps the line splittable on whitespace by a trivial grep/awk.

### H5 dependency interface (consumed from M-A1)

M-A1 owns the invariant core. M-A3 does NOT define its own core shape: it
consumes M-A1's exact types (`AnalyzerModel`, `IRecordingInvariant`, `Finding`,
`VerdictLevel`) unchanged. H5 constructs an `AnalyzerModel` from live in-game
state and runs M-A1's `IRecordingInvariant` rule set over it, then maps M-A1's
`Finding` verdicts to in-game outcomes. There is no `RecordingInvariantCore`,
no `InvariantViolation`, and no `InvariantSeverity` in this design; those were an
earlier incompatible sketch and are deleted. Rule ids follow M-A1's format
(e.g. `INV1-UT-MONOTONIC`) everywhere in this doc.

The in-game model builder (see Behavior H5) supplies each `AnalyzerModel` field
from live state, walking the store RAW (no `EffectiveState` routing):

```
new AnalyzerModel {
    SaveName          = HighLogic.SaveFolder,
    Recordings        = RecordingStore.CommittedRecordings,   // RAW committed list
    Trees             = RecordingStore.CommittedTrees,
    Tombstones        = ParsekScenario.Instance ledger tombstones,
    SupersedeRelations= ParsekScenario.Instance supersede rows,
    Ledger            = Ledger.Actions,                        // RAW, unfiltered (INV8 filters internally)
    CareerSave        = null,                                  // in-game FAIL career-diff is the LedgerGroundTruthHarness seam, not H5
    LoadFaults        = empty,                                 // nothing parsed from disk here
    FixtureStamp      = null,                                  // live store is not a stamped fixture corpus
    BodyResolver      = FlightGlobals.Bodies lookup (name -> CelestialBody),
}
```

Verdict mapping (M-A1 `VerdictLevel` -> in-game outcome):
- `Fail` -> `InGameAssert` failure (the test fails, carrying RuleId + Target +
  Message).
- `Warn` -> a `ParsekLog.Warn` line; the test does NOT fail.
- `Info` -> ignored (inventory / provenance; not surfaced as a test signal).
- `StaleFixture` -> unreachable in-game (`FixtureStamp` is null, so no rule emits
  it); if one ever did, it is treated as `Info` (ignored).

This is what makes "same invariant core as the offline analyzer" literally true:
one rule set, two model builders (M-A1's `SaveDirectoryLoader` offline, H5's
live-store builder in-game). The load-bearing requirement is only that M-A1's
rules are pure over `AnalyzerModel` so the in-game builder can feed them a
live-store-sourced model.

## Behavior

### H1 - Autorun batch trigger

Location: a new arm/fire path in `TestRunnerShortcut`, which is already the DDOL
`[KSPAddon(Instantly, once=true)]` addon that survives scene transitions
(the DDOL addon that survives scene transitions). It already polls every frame in
`Update()` and already owns an `InGameTestRunner runner` field. H1 reuses that
lifecycle; it does not add a second addon. NOTE: `TestRunnerShortcut` constructs
the runner LAZILY, only when the Ctrl+Shift+T window is first opened, so under
autorun (no window ever opened) the `runner` field can be null when H1 wants to
fire. H1 must therefore instantiate the runner through the SAME lazy factory the
window-open path uses (not a second construction path) before invoking
`RunAll` / `RunCategory`, so autorun and interactive runs share one runner
lifecycle.

Read-once caching: the env var is read once in `Awake` (or first `Update`) into a
parsed struct `{ enabled, selector, categories[] }`, so a per-frame
`Environment.GetEnvironmentVariable` is avoided and a mid-process env mutation
cannot change behavior (env is a launch-time contract).

Arming scene: H1 arms in ANY game scene that the batch selector has eligible
tests for, because the runner already filters by scene
(`FilterSceneEligibleBatchCandidates`) and a category like `RecordingInvariants`
is FLIGHT-scoped while another might be SPACECENTER-scoped. H1 does not itself
decide "which scene" - it fires in whatever scene KSP settled into, and the
runner's existing scene-eligibility filter runs the subset that fits. If zero
tests are eligible for the current scene, the batch runs, marks everything
scene-skipped, and still emits a BATCH_COMPLETE line (which is correct and
informative for the orchestrator: "this scene had nothing to run").

Scene-settle definition (concrete): H1 fires only once ALL of these hold on the
same frame:
1. `HighLogic.LoadedSceneIsGame` is true and the scene is not `LOADING`.
2. `HighLogic.CurrentGame != null` and `!string.IsNullOrEmpty(HighLogic.SaveFolder)`
   (a save is actually loaded - this is the same triple the runner's
   `ClassifyBatchIsolationMode` inputs on, InGameTestRunner.cs:326-328).
3. For FLIGHT scenes specifically: `FlightGlobals.ready == true` AND
   `FlightGlobals.ActiveVessel != null` AND `!FlightGlobals.ActiveVessel.packed`
   (the vessel is unpacked and physics-live). This mirrors what FLIGHT in-game
   tests assume; running before unpack races the recorder and PartLoader.
4. For non-FLIGHT game scenes (SPACECENTER, TRACKSTATION, EDITOR): condition 1+2
   plus a settle delay (see below). There is no ActiveVessel gate there.
5. A settle delay of N consecutive qualifying frames has elapsed
   (`AutorunSettleFrames`, proposed 30 frames ~= 0.5 s at 60 fps) to let stock
   one-frame-late initialization (camera, UI, ScenarioModule OnLoad) finish. The
   counter resets to 0 if any condition regresses (e.g. a sub-scene reload).
6. The crash-reconcile gate is clear (see interaction below).

Single-fire per scene-entry, re-arm per scene: H1 keeps a
`bool autorunFiredThisScene` flag reset on `GameEvents.onGameSceneLoadRequested`
(the addon already subscribes to that event, TestRunnerShortcut.cs:68, for input
lock reset). So:
- One process that loads straight into FLIGHT and stays there: H1 fires exactly
  once.
- A batch that reloads the flight scene as part of isolation teardown (the
  runner does FLIGHT->FLIGHT reloads): the reset would re-arm H1, which would
  double-fire. GUARD: H1 also checks `!runner.IsRunning` before firing, and adds
  a process-level `autorunConsumedForProcess` latch keyed by selector so that
  once the FULL autorun selector has been consumed, re-entry does not restart it.
  The intended single-process semantics are "run the selector once, then stop"
  (the orchestrator restarts KSP for the next scenario). Re-arm-per-scene exists
  only to support a selector whose categories legitimately span scenes that the
  orchestrator drives by scripted scene changes; in v1 (see below) that path is
  scoped out and the process-latch is the operative rule.

Multi-category selector: `PARSEK_AUTORUN_TESTS=A,B` - v1 runs them by issuing
`RunCategory` per token SEQUENTIALLY, waiting for `!runner.IsRunning` between
tokens (the runner is single-batch; `RunCategory` early-returns if `isRunning`,
InGameTestRunner.cs:349). Each token produces its own batch and its own
BATCH_COMPLETE line, plus one final `category=multi:<count>` summary line
aggregating the union counts. This keeps each category's campaign isolation
independent (each `RunCategory` captures + tears down its own baseline).

v1 scope / documented limitation - multi-scene batches: the runner accumulates
results across scenes in `InGameTestInfo.ResultsByScene` and the export file
unions them (FormatResultsReport, InGameTestRunner.cs:3358). But a single
autorun PROCESS settles into ONE scene and H1 fires there. Driving a category
whose tests span multiple scenes requires scripted scene changes between
sub-batches, which is the orchestrator's job (M-A5 multi-session orchestration)
via the command seam (M-A2), NOT H1's. v1 H1 therefore runs
CURRENT-SCENE-eligible tests of the selector and relies on the orchestrator to
relaunch/redrive for other scenes. This is stated as a limitation, not a bug:
BATCH_COMPLETE always reports `scene=`, so the orchestrator knows exactly which
scene's slice it got.

Interaction with crash-reconcile: if a PRIOR batch in a DIFFERENT process was
interrupted (KSP killed, hard quit), `ParsekScenario.OnLoad` runs
`RunTestBatchCrashReconcile`, which reverts `persistent.sfs` from the `.bak`,
restores the Parsek sidecar dir, and SCHEDULES A DEFERRED REAL in-memory reload
(TestBatchMarker.cs:10-22). That reload changes `HighLogic.CurrentGame` out from
under any batch. H1 MUST NOT fire until reconcile has fully completed, or the
autorun batch would capture its baseline against a half-reverted save. GATE: H1
checks that no `TestBatchMarker` reconcile is pending.

This gate needs an observable signal that does not exist in source today, so
M-A3 ADDS one: a NEW internal read-only predicate on `ParsekScenario`
(`CrashReconcileInProgress`, working name). It does not exist now and must be
introduced by this module. It is SET when `ParsekScenario.OnLoad` detects a
recovery-reason marker and dispatches `RunTestBatchCrashReconcile` (which
schedules the deferred real reload), and CLEARED at the deferred reload's own
`OnLoad` once reconcile has fully completed. Both transitions log a line
(`Info` "crash-reconcile in progress: <reason>" on set, `Info` "crash-reconcile
complete; cleared" on clear) so the log alone shows why H1 held or released.
Concretely, H1 requires that `ParsekScenario.CrashReconcileInProgress` is false
AND `ShouldReconcileOnLoad` would now return a non-recovery reason for the live
marker (the marker is same-process or absent). Until then H1 stays armed and
keeps waiting; the settle counter does not advance. The predicate is a read-only
observable flag; it does not change any crash-reconcile behavior, only exposes
its in-flight state to the settle gate.

### H2 - Exit after tests

Location: the very end of the `RunBatch` batch-end region, AFTER
`ExportResultsFile()` (`InGameTestRunner.ExportResultsFile`) and AFTER the H3 BATCH_COMPLETE
emit, gated by the parsed `PARSEK_AUTORUN_EXIT == "1"` and by "this batch was an
autorun batch" so a human clicking Run All in a process that happens to have the
env var set does NOT quit under them (see Edge Cases).

wasAutorunBatch handoff mechanism (new runner member): H1 signals the runner
that the batch it is about to start is an autorun batch by calling a NEW runner
method `runner.MarkNextBatchAutorun()` IMMEDIATELY before it invokes `RunAll` /
`RunCategory`. That call sets a pending flag; `RunBatch` LATCHES it into the
batch at batch start (into a per-batch `bool wasAutorunBatch` read at the
batch-end region) and CLEARS the pending flag, so the mark applies to exactly the
next batch and never leaks to a later human-initiated one. A human clicking Run
All never calls `MarkNextBatchAutorun`, so its batch latches `wasAutorunBatch =
false` and H2's gate is false. `MarkNextBatchAutorun` plus the per-batch
`wasAutorunBatch` latch are new runner members added by this module.

Ordering contract (the core edge-case cluster): the batch-end region already
performs, in this order, the campaign-safety teardown:
1. NRE-storm batch-end corruption sampling.
2. `EndBatchExceptionMonitor` + `FlightCameraReloadPin.Disarm`.
3. Isolation teardown: `TeardownDiskOnlyIsolation` (DiskOnly) or
   `CleanupBatchFlightBaselineSave` (InMemoryAndDisk) - THIS is the step that
   reverts `persistent.sfs` from the clean `.bak` and sweeps the slot/snapshot.
4. `ClearTestBatchMarker`.
5. `ExportResultsFile`.

H2 runs as step 7 (step 6 is H3). It MUST NOT run before step 3. The quit is
therefore placed textually after `ExportResultsFile` in the always-runs
batch-end region, with no `yield` between the teardown and the quit so the
decision cannot go stale (the same discipline the existing Space Center bounce
uses, InGameTestRunner.cs:1776-1779).

Quit precedence over Space Center bounce: if the batch corrupted the flight
scene (NRE storm) the existing code arms a one-shot Space Center bounce to leave
the OPERATOR in a usable scene (InGameTestRunner.cs:1866-1867). Under autorun
there is no operator and the process is about to die, so H2 takes precedence:
when H2 is armed, it SKIPS the Space Center bounce and quits. The disk save is
already reverted by step 3 regardless, so skipping the bounce does not risk the
campaign; it only skips an in-process scene recovery that autorun does not need.

Quit mechanism: H2 calls a clean-quit path (`HighLogic.QuitGame()` if it triggers
the stock main-menu-then-exit flow cleanly, else `Application.Quit()`). Because
all Parsek-side campaign safety already ran in steps 1-5, the quit does not need
to trigger any Parsek scene-exit commit (the plan notes TestingTools' `Quit()`
is a bare `Application.Quit()` and does NOT commit; that is fine HERE because
autorun sessions pin auto-record OFF and commit deliberately via the command
seam BEFORE the batch, plan section 3.3). H2 emits a log line immediately before
quitting so the last durable log record is unambiguous.

### H3 - Stable machine-readable markers

Location: the batch-end region, emitted once per batch, after
`ExportResultsFile` and before H2. Reuses the counts the runner already computed
(`Passed`/`Failed`/`Skipped`/`considered`). Emits via `ParsekLog.Info` so it
lands in KSP.log with the standard structured prefix. Format is the versioned
contract from Data Model. For a multi-category autorun, each constituent
`RunCategory` batch emits its own line and a final aggregate line is emitted by
H1's multi-category driver.

The line COMPLEMENTS `parsek-test-results.txt` (which the orchestrator also
reads for per-test detail and per-scene blocks). BATCH_COMPLETE is the cheap
"did the batch finish and what was the tally" signal the orchestrator greps for
without parsing the whole results file; the results file is the drill-down.

### H5 - RecordingInvariants in-game category

A new in-game test file under `Source/Parsek/InGameTests/` (proposed
`RecordingInvariantsInGameTests.cs`) with tests carrying
`[InGameTest(Category = "RecordingInvariants", Scene = GameScenes.FLIGHT)]`.
FLIGHT-scoped because that is where a loaded career with a populated
`RecordingStore` reliably exists after the autorun save loads; the category is
also eligible to be manually run from the Ctrl+Shift+T window.

What it does each run:
1. Build an `AnalyzerModel` from live state via the model builder above: RAW
   `RecordingStore.CommittedRecordings` + `RecordingStore.CommittedTrees`,
   `ParsekScenario.Instance` tombstones + supersede rows, RAW `Ledger.Actions`,
   `CareerSave=null`, empty `LoadFaults`, `FixtureStamp=null`, a `FlightGlobals`
   body resolver. Log the counts up front (see step 4).
2. Run M-A1's `IRecordingInvariant` rule set over the model, collecting
   `Finding`s.
3. Map verdicts: any `Fail` `Finding` fails the in-game test (via `InGameAssert`,
   carrying RuleId + Target + Message); every `Warn` `Finding` is logged as a
   `ParsekLog.Warn` line but does not fail the test; `Info` is ignored;
   `StaleFixture` is unreachable (null stamp). This matches the offline
   analyzer's Warn-vs-Fail policy (plan section 7 invariants 2 and 3).
4. Empty store: the test PASSES (no recordings = no findings) but logs an
   explicit "walked 0 recordings" line so an empty-store run is distinguishable
   from a not-run one. The walk-count line (`recordings=<n> trees=<m>`, see
   Diagnostic Logging) is emitted before evaluation on every run, empty or not.

The invariant set is exactly M-A1's PURE-CORE subset that runs over an
`AnalyzerModel` with no loader-supplied inputs: INV1 (UT monotonicity), INV2 (no
double-cover of a UT span), INV3 (RELATIVE lat/lon contract), INV4 (part-event
PID resolution), INV5 (schema-generation compatibility), INV6 (resource-manifest
consistency-where-present), INV7 (tree topology: parent/branch/chain/anchor/
supersede/tombstone links resolve, no cycles, chain-index contiguity per
(ChainId, ChainBranch)), and INV8 (ledger ELS internal consistency; the
career-diff FAIL variant is the `LedgerGroundTruthHarness` seam, not H5, since
H5 passes `CareerSave=null`). The loader-scoped rules INV7b (annotation
staleness), INV9 (rewind-point file validation), and INV10 (codec round-trips)
are OFFLINE-ONLY: they depend on on-disk sidecar/RP files and `LoadFault` data
the in-game builder does not populate, so they are absent from the live model's
findings. INV5 is expected to PASS in-game (schema-incompatible recordings are
rejected at load, so the live store never holds one), but it is kept in the H5
set anyway as a cheap belt-and-suspenders check and to keep the rule set
identical to the offline pure core. Because the rule set is shared, adding a
pure-core invariant to M-A1 automatically strengthens H5 with no M-A3 change.

Execution model (sync vs coroutine, required call): v1 is a SYNCHRONOUS `void`
in-game test. The walk is read-only (it materializes references into an
`AnalyzerModel` and runs pure rules; it mutates no live state), so a single-frame
hitch inside the batch is acceptable and simpler than coroutine slicing. The
walk-count line (step 4) logs `recordings=<n> trees=<m>` up front; above a stated
store-size threshold (`AutorunInvariantWalkSizeWarnThreshold`, proposed 500
recordings) the test additionally logs a `Warn` size line
("RecordingInvariants walk over <n> recordings may hitch a frame; coroutine
slicing deferred") so a pathologically large store's hitch is explained in the
log rather than looking like a freeze. Coroutine slicing (yielding the walk
across frames for very large stores) is DEFERRED, listed here as the known future
cut; v1 ships the synchronous test.

ERS/ELS grep-gate decision (required call): H5's walker reads
`RecordingStore.CommittedRecordings` RAW, and it does NOT route through
`EffectiveState.ComputeERS()`. Justification:
- The invariant core validates the STRUCTURAL integrity of the persisted data,
  including NotCommitted provisionals, superseded entries, and tombstone/supersede
  link targets. ERS is defined as the visibility-filtered effective set (it skips
  superseded and NotCommitted entries, EffectiveState.cs:1213+). Feeding H5 the
  ERS view would HIDE exactly the rows whose links invariant 7 (tree topology)
  exists to check - a superseded recording that ERS drops is still a row whose
  `SupersedeTargetId` must resolve. The offline analyzer walks the whole save
  directory including those rows; to produce the SAME verdict from the SAME core,
  the in-game walker must feed the SAME complete set. This is the identical
  rationale the allowlist already records for `RecordingStoreTestSnapshot`
  ("supersede-aware filtering would corrupt the round-trip", allowlist lines
  12-18).
- No new allowlist entry is needed. The grep gate (`scripts/grep-audit-ers-els.ps1`)
  allowlists the directory prefix `Source/Parsek/InGameTests/` (allowlist line
  49). H5's walker lives there, so its raw `CommittedRecordings` read is already
  covered. The invariant CORE (M-A1) never reads `CommittedRecordings` at all (it
  takes materialized lists as arguments), so the core file needs no exemption
  either - it is pure by construction. This keeps the gate meaningful: the only
  raw read is in a test-surface file that is already trusted.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. `PARSEK_AUTORUN_TESTS` unset/empty. -> H1 fully inert; no batch auto-starts;
   normal interactive play. -> v1.

2. `PARSEK_AUTORUN_TESTS` set to a malformed value (whitespace only, stray
   commas like `A,,B`, leading/trailing comma). -> Parser trims tokens and drops
   empty ones; `A,,B` runs `A` then `B`; an all-empty value logs a WARN
   ("autorun selector parsed to zero categories; H1 inert") and does not fire.
   -> v1.

3. `PARSEK_AUTORUN_TESTS=SomeCategoryThatDoesNotExist`. -> `RunCategory` runs an
   empty batch (the `allTests.Where(t => t.Category == category)` filter yields
   nothing). The batch completes with total=0; BATCH_COMPLETE emits
   `total=0 ... category=SomeCategoryThatDoesNotExist`; a WARN logs "autorun
   category matched 0 discovered tests". The orchestrator sees total=0 and can
   treat it as a config error. -> v1.

4. `PARSEK_AUTORUN_TESTS=all` in a scene where every test is scene-ineligible
   (e.g. loads into SPACECENTER while the batch is mostly FLIGHT tests). -> Batch
   runs, marks eligible-elsewhere tests Skipped-for-scene, BATCH_COMPLETE reports
   the skip tally with `scene=SPACECENTER`. Not an error. -> v1.

5. A batch is already running (human clicked Run All) when H1's fire conditions
   are met. -> H1 checks `!runner.IsRunning` and does not fire; it stays armed
   and re-checks next frame. If the human batch finishes and conditions still
   hold, H1 then fires. -> v1.

6. Crash-reconcile pending from a prior killed batch. -> H1 gate holds fire until
   `CrashReconcileInProgress` clears and the deferred reload's OnLoad completes;
   only then does the settle counter advance and H1 fire, so the autorun baseline
   is captured against the reverted save, not the mutated one. -> v1.

7. KSP loads into MainMenu (no save loaded) - e.g. the orchestrator's boot flag
   failed to auto-load a save. -> Scene-settle condition 2 (`CurrentGame != null`,
   non-empty `SaveFolder`) is false in MainMenu; H1 never fires; no batch, no
   corruption. If `PARSEK_AUTORUN_EXIT=1` is also set, H2 does NOT fire either
   (H2 only runs at a batch's end, and no batch ran), so the process stays up and
   the orchestrator's timeout eventually kills it (killed-run verdict). A WARN is
   logged after a bounded wait ("autorun armed but no game scene settled within
   Ns; still waiting") so the log explains the eventual kill. -> v1.

8. Second flight-scene load re-triggers autorun (the runner's own isolation
   teardown reloads FLIGHT->FLIGHT, firing `onGameSceneLoadRequested`). -> The
   per-scene re-arm would re-fire, but the `!runner.IsRunning` guard blocks it
   during the batch, and the `autorunConsumedForProcess` latch blocks it after
   the batch. Net: exactly one autorun batch per process. -> v1.

9. `PARSEK_AUTORUN_EXIT=1` but `PARSEK_AUTORUN_TESTS` unset. -> No autorun batch
   ever starts, so H2 (which fires only at a batch's end) never triggers. The
   process does not quit on its own. This is intentional: EXIT is "quit after the
   autorun tests", and there were none. A WARN logs at startup ("PARSEK_AUTORUN_EXIT
   set but PARSEK_AUTORUN_TESTS unset; nothing will auto-run or auto-quit") so the
   misconfiguration is visible. -> v1.

10. Exit requested but `ExportResultsFile` throws. -> `ExportResultsFile` already
    swallows its own exception and WARN-logs (InGameTestRunner.cs:3343-3346). H3
    and H2 run regardless (they are after it in the region and not guarded by its
    success). The orchestrator may get a BATCH_COMPLETE line but a missing/stale
    results file; it treats the log line as authoritative for pass/fail and the
    missing file as a soft error. -> v1.

11. NRE-storm abort mid-autorun (a stock/mod bug floods exceptions after some
    test). -> The existing storm detector aborts the batch, reverts the disk save,
    and normally arms a Space Center bounce. Under autorun, H2 takes precedence:
    the disk is already reverted (abort path calls
    `ForceRevertCampaignDiskToBaseline`), H3 emits BATCH_COMPLETE with whatever
    counts were reached, and H2 quits instead of bouncing. The orchestrator sees a
    finished-with-failures batch and a clean process exit. -> v1.

12. Exit fires but the quit call itself throws or the process lingers. -> H2
    wraps the quit in try/catch, logs an ERROR on failure, and does nothing
    further; the orchestrator's timeout kills the process (killed-run verdict).
    The campaign is already safe (teardown ran before the quit attempt). -> v1.

13. Human presses Ctrl+Shift+T and clicks Run All in a process that has the env
    vars set (developer testing the autorun build interactively). -> H1's fire is
    independent of the button, but H2's "was this an autorun batch" flag is false
    for a button-initiated batch, so the human's manual batch does NOT quit KSP
    under them. Only an H1-initiated batch quits. -> v1.

14. Env var changes mid-process (some tool mutates the environment after launch).
    -> Ignored: the selector is parsed once at startup and cached. Env is a
    launch-time contract. -> v1.

15. `PARSEK_AUTORUN_TESTS=RecordingInvariants` but the loaded save has an empty
    `RecordingStore`. -> H5 tests pass (no recordings, no violations) and log
    "walked 0 recordings"; BATCH_COMPLETE reports them passed. The orchestrator
    distinguishes "0 recordings, invariants trivially hold" from "invariants
    failed" via the results file and the walk-count log line. -> v1.

16. Multi-category autorun where one category hangs (a test infinite-loops). ->
    No hook-level watchdog (out of scope); the batch never reaches its end, so no
    BATCH_COMPLETE for that category and no H2 quit. The orchestrator's timeout
    kills the process. The partial log (earlier categories' BATCH_COMPLETE lines)
    tells the orchestrator how far it got. -> v1 (watchdog deferred; see What
    Doesn't Change).

## What Doesn't Change

- Normal interactive play: with both env vars unset, `TestRunnerShortcut`,
  `InGameTestRunner`, and the whole batch-isolation machinery behave exactly as
  today. H1's added `Update` block early-returns on the unset selector before any
  work.
- `dotnet test` and the xUnit suite: unaffected. The env vars are read only by the
  in-game addon, which does not run under xUnit.
- Ship-default state: neither env var is ever set by Parsek, any build script, or
  any config file. The hooks are enabled ONLY by an external orchestrator setting
  process environment before launch. There is no Settings-UI checkbox and no
  persisted flag.
- The batch campaign-isolation contract (`TestBatchMarker`, `.bak` capture,
  `CaptureBatchBaseline`, teardown restore): behavior UNCHANGED. Autorun uses the
  identical `RunAll` / `RunCategory` entry points, so it inherits the identical
  isolation. H2 attaches strictly AFTER teardown; it does not reorder or skip any
  isolation step. The crash-reconcile path is behavior-unchanged but gains ONE
  observable addition: the new read-only `ParsekScenario.CrashReconcileInProgress`
  flag (set at OnLoad reconcile detection, cleared at the deferred reload's
  OnLoad) that H1's settle gate reads. The flag only exposes existing in-flight
  state; it does not alter what `RunTestBatchCrashReconcile` or the deferred
  reload do.
- The `parsek-test-results.txt` format and `FormatResultsReport`: unchanged. H3
  adds a log line; it does not touch the results file.
- The runner's existing public entry points (`RunAll`,
  `RunAllIncludingFlightRestore`, `RunCategory`,
  `RunCategoryIncludingFlightRestore`, `RunSingle`), `AllowBatchExecution` /
  `RunLast` semantics, and the NRE-storm abort logic: behavior unchanged. Autorun
  v1 uses `RunAll` / `RunCategory` (the ordinary batch-safe path), not the
  flight-restore variants (those are for `[isolated]` destructive tests a human
  opts into). M-A3 does ADD one new runner member, `MarkNextBatchAutorun` (plus
  the per-batch `wasAutorunBatch` latch it feeds), used only by H1/H2; it does not
  change any existing entry point's behavior and is inert when H1 never calls it.
- Timeout self-defense / watchdog: OUT OF SCOPE for M-A3 v1. If a batch hangs, the
  EXTERNAL orchestrator kills KSP (killed-run verdict, plan section 9 item 3). The
  hooks deliberately implement no internal watchdog timer; a self-kill risks
  masking a real hang as a clean exit and racing the teardown. The log-validation
  killed-run mode (plan section 9) handles the truncated log a kill produces.
- ERS/ELS routing and the grep gate: unchanged. H5 reads the raw committed list
  from an already-allowlisted directory; no allowlist edit, no new exemption.

## Backward Compatibility

No serialized data, so there is nothing to migrate. Saves written by a build
with these hooks are byte-identical to saves from a build without them, because
the hooks never write to a save. A save that happened to be open when an autorun
batch ran is protected by the UNCHANGED batch isolation (teardown reverts
`persistent.sfs` from the `.bak`), exactly as a human-run batch is today. Older
builds simply ignore the env vars (they do not read them). The BATCH_COMPLETE
line is additive to KSP.log; older log consumers that do not know it just skip an
unrecognized INFO line. The `v1` version token guarantees a future format change
cannot silently break an orchestrator pinned to `BATCH_COMPLETE v1`.

## Diagnostic Logging

Subsystem tag `TestRunner` for hook-plumbing lines (matching
`TestRunnerShortcut` / `InGameTestRunner`), `RecordingInvariants` for H5 walker
lines. Every decision point below emits a line so the log alone reconstructs why
the autorun did or did not do something - critical because autorun runs with no
human watching.

H1 (arm/fire):
- Startup: `Info` "autorun selector parsed: enabled=<b> selector='<raw>'
  categories=[<parsed>] exit=<b>" - one line at addon Awake, records the exact
  env contract the process launched with.
- Scene-settle waiting: `VerboseRateLimited` (key `autorun-settle`, 1 Hz) "autorun
  armed, waiting for settle: scene=<s> game=<b> save=<b> flightReady=<b>
  vessel=<b> packed=<b> settleFrames=<n>/<N> reconcilePending=<b>" - so a
  never-firing autorun shows exactly which condition is stuck.
- Fire: `Info` "autorun FIRING: selector=<sel> scene=<s> eligibleCount=<n>" at the
  moment `RunAll`/`RunCategory` is invoked.
- Not-firing decisions: `Verbose` one-liners for each blocked reason: batch
  already running, reconcile pending, already-consumed latch, re-arm suppressed.
- Malformed/empty selector: `Warn` "autorun selector parsed to zero categories;
  H1 inert" (edge case 2). `Warn` "autorun category '<c>' matched 0 discovered
  tests" (edge case 3, emitted when the batch's eligible count is 0 for a named
  category).
- Multi-category: `Info` "autorun multi-category: running <c> tokens sequentially:
  [<list>]" then per-token fire lines, then a final `Info`
  "autorun multi-category complete: <count> batches" before the aggregate H3 line.
- No-scene timeout: `Warn` "autorun armed but no game scene settled within <N>s;
  still waiting for orchestrator save load" (edge case 7).

H2 (exit):
- Config warn: `Warn` "PARSEK_AUTORUN_EXIT set but PARSEK_AUTORUN_TESTS unset;
  nothing will auto-run or auto-quit" (edge case 9), at startup.
- Precedence over bounce: `Info` "autorun exit armed; skipping Space Center bounce
  recovery (process is quitting)" when H2 supersedes the bounce (edge case 11).
- Just before quit: `Info` "autorun exit: teardown+export complete, quitting KSP
  cleanly (mechanism=<QuitGame|ApplicationQuit>)" - the intended last durable log
  line of the session.
- Quit failure: `Error` "autorun exit quit call failed: <ex>; orchestrator timeout
  will reap the process" (edge case 12).

H3 (marker):
- The BATCH_COMPLETE line itself (`Info`, the versioned contract). This is both a
  diagnostic and the machine contract.

H5 (invariant walker):
- `Info` "RecordingInvariants walk: recordings=<n> trees=<m>" at start (the
  walk-count line; `recordings=0` distinguishes empty-store from not-run, edge
  case 15). Above `AutorunInvariantWalkSizeWarnThreshold` recordings, an
  additional `Warn` size line ("walk over <n> recordings may hitch a frame;
  coroutine slicing deferred") explains the one-frame hitch (execution model).
- Per Fail `Finding`: the `InGameAssert` failure message carries
  `RuleId + Target + Message`; additionally `Warn`
  "RecordingInvariants FAIL <ruleId> target=<t>: <message>".
- Per Warn `Finding`: `Warn` "RecordingInvariants WARN <ruleId> target=<t>:
  <message>" (does not fail the test).
- `Info` "RecordingInvariants summary: fails=<n> warns=<n> over recordings=<n>"
  at end (batch-count convention: one summary line, not per-recording spam).

## Test Plan

Every test states the regression it catches.

Unit tests (xUnit, pure - the hooks' parse/decision logic is extracted to
`internal static` pure methods so it is testable without Unity):

- `AutorunSelectorParser` tests: `""` -> inert; `"all"` -> {all}; `"A"` -> {A};
  `"A,B,C"` -> {A,B,C}; `"A,,B"` and `" A , B "` and `",A,"` -> {A,B} (trim + drop
  empties); whitespace-only -> zero categories + inert flag.
  Fails if: a malformed env var silently runs the wrong categories or crashes the
  addon (edge cases 2, 3). Guards the external contract's parsing.
- `SceneSettleDecision` pure predicate tests: table-drive (scene, game, save,
  flightReady, vessel, packed, settleFrames, reconcilePending) -> shouldFire.
  Assert FLIGHT requires ready+vessel+unpacked; MainMenu never fires; settle
  counter must reach N; reconcilePending blocks.
  Fails if: H1 fires against a half-initialized scene (races recorder/PartLoader)
  or against a pending crash-reconcile (captures baseline on a half-reverted save)
  - edge cases 6, 7.
- `AutorunFireGate` tests: `IsRunning` blocks fire; `autorunConsumedForProcess`
  latch blocks re-fire; re-arm-per-scene resets the per-scene flag but not the
  process latch.
  Fails if: the FLIGHT->FLIGHT isolation reload double-fires the autorun (edge
  cases 5, 8).
- `H3FormatBatchCompleteLine` tests: given (total, passed, failed, skipped,
  selector, scene) assert the exact string
  `BATCH_COMPLETE v1 total=.. passed=.. failed=.. skipped=.. category=.. scene=..`,
  no spaces inside values, `v1` present.
  Fails if: a code change silently alters the orchestrator contract without a
  version bump - this is the guard that makes the contract a contract.
- `H2ExitDecision` tests: exit armed only when (`PARSEK_AUTORUN_EXIT=="1"` AND
  wasAutorunBatch); button-initiated batch (wasAutorunBatch=false) never quits
  even with the env var set (edge case 13); exit supersedes bounce when both are
  armed (edge case 11).
  Fails if: a developer's interactive Run All quits KSP under them, or exit fires
  before teardown.

Log-assertion tests (xUnit, via `ParsekLog.TestSinkForTesting`):

- H1 emits the startup selector-parse line and the FIRING line with the right
  fields; the settle-waiting line names the stuck condition.
  Fails if: a never-firing autorun leaves no diagnostic trail (the whole point of
  the settle log).
- H3 BATCH_COMPLETE line is emitted exactly once per batch at Info level with the
  structured `[Parsek][INFO][TestRunner]` prefix (assert via the same
  `StructuredLinePattern` used in `LogContractTests`).
- H2 emits the pre-quit line before invoking the quit (assert the line is present
  and the quit callback - injected as a test seam - is invoked AFTER it).
  Fails if: the quit races ahead of the durable log record, or fires without the
  teardown-complete evidence.

LogContract test (new FMT rule in `LogContractTests.cs`, plan requirement that H3
join the log-contract surface):

- `BATCH_COMPLETE line matches the versioned contract`: emit a synthetic
  BATCH_COMPLETE via the H3 formatter through `TestObserverForTesting`, assert it
  matches `^\[Parsek\]\[INFO\]\[TestRunner\] BATCH_COMPLETE v1 total=\d+ passed=\d+
  failed=\d+ skipped=\d+ category=\S+ scene=\S+$`.
  Fails if: any future edit changes the token order, names, spacing, or drops the
  version tag - the contract test is what forces a `v2` bump to be deliberate.

In-game tests (`RecordingInvariants` category, FLIGHT):

- The H5 tests themselves are the deliverable: one test per invariant (or a small
  set) that builds an `AnalyzerModel` from the live store and asserts zero Fail
  `Finding`s from the M-A1 rule set. These double as the coverage: a
  builder-synthetic corpus injected via
  `InjectAllRecordings` before an in-game run should pass; a deliberately
  malformed synthetic recording (e.g. UT non-monotonic, dangling supersede
  target) must produce a Fail.
  Fails if: the in-game walker feeds the core the wrong set (e.g. ERS-filtered,
  hiding superseded rows) so a broken link goes undetected - this is the test that
  proves the raw-`CommittedRecordings` decision is correct.
- `RecordingInvariants on empty store passes and logs walk=0`: asserts edge case
  15 (empty store is a pass, not a skip, and is logged as such).
- ERS grep-gate regression: the existing `GrepAuditTests` (runs
  `scripts/grep-audit-ers-els.ps1`) must stay green with the H5 walker added.
  Fails if: the H5 walker is placed outside `Source/Parsek/InGameTests/` (losing
  its directory allowlisting) or the invariant core accidentally reads
  `CommittedRecordings` (which would need - and lack - an exemption).

Edge-case tests: one per numbered edge case above where a pure decision exists
(1, 2, 3, 5, 6, 7, 8, 9, 11, 13, 14 are covered by the unit/log tests listed;
4, 10, 12, 15, 16 are covered by the in-game and log-assertion tests). Each
reproduces the scenario against the pure decision method and asserts the
documented behavior.
