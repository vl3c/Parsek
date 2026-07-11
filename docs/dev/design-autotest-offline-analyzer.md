# Design: Offline Recording Analyzer (Module M-A1)

Status: DRAFT (2026-07-11). Module M-A1 of the automated testing initiative
(`docs/dev/automated-testing-plan.md` section 11b). This is the Step 3 design
doc; the vision + scenarios are the plan (section 7) and the scenario catalog.
Plain ASCII, no em dashes.

---

## Problem

Parsek's correctness lives almost entirely in files it writes: recording trees
in `persistent.sfs`, trajectory / snapshot / ghost / annotation sidecars under
`saves/<save>/Parsek/Recordings/`, rewind quicksaves under
`saves/<save>/Parsek/RewindPoints/`, and the ledger state under
`saves/<save>/Parsek/GameState/`. Today the only way to know a save is
internally consistent is to load it into KSP and watch. Bugs that corrupt these
files (a RELATIVE section written with body-fixed lat/lon, a chain with a broken
`ChainIndex` run, a supersede row pointing at a missing recording, a sidecar
whose schema generation drifted from the tree metadata) are invisible until
playback misbehaves, and by then the failing frame has scrolled past.

We need a headless, no-KSP tool that takes any save directory and answers "is
every recording tree + sidecar set + `ParsekScenario` node structurally valid
against the contracts the production code enforces?" It has to run three ways:
as a per-PR CI regression floor over a fixture corpus (minutes, no game), as a
post-run verifier over saves produced by scripted missions (the harness reads
its verdict), and as ad hoc triage over a user bug-report save (a human reads
its report). The same rule set must later be walkable in-game against the live
`RecordingStore` (module M-A3 / hook H5), so the rules cannot bake in any
file-I/O assumption.

## Terminology

- **Invariant / rule**: one named check over loaded data structures. A rule
  produces zero or more **findings**. Each rule declares the production member
  that defines or enforces the contract it checks (its `CitedContract`), so a
  rule asserting a wrong contract dies in code review rather than as a false
  alarm in nightly.
- **Finding**: one observation with a **verdict level** (FAIL / WARN / INFO /
  STALE-FIXTURE), a rule id, a target (recording id / tree id / section index /
  file path), and a human message.
- **Verdict level**:
  - **FAIL**: a contract the production loader would reject or that guarantees
    broken playback. A run with any FAIL is red.
  - **WARN**: suspected wrong but not proven a bug yet, or a contract not yet
    pinned to citable production behavior (e.g. ABSOLUTE lat/lon range before
    KSP longitude normalization is cited). Visible, does not fail the run.
  - **INFO**: inventory / provenance / counts. Never fails a run.
  - **STALE-FIXTURE**: the analyzed data carries a schema generation that does
    not match the fixture set's stamp. Distinct from FAIL: the data is not
    wrong, the fixture is out of date (plan section 7 versioning policy).
- **Analysis subject**: one save directory. The analyzer emits exactly one
  machine-readable report file and one human summary per subject.
- **Loaded model**: the in-memory `Recording` / `RecordingTree` /
  `TrackSection` / `OrbitSegment` / ledger objects after the analyzer has
  hydrated them from disk. Rules run over the loaded model, never over raw
  bytes.
- **Codec seam**: a headless-callable serialize/deserialize pair that never
  touches Unity or `ScenarioModule` (e.g. `RecordingTreeRecordCodec`,
  `RecordingManifestCodec`, `TrajectorySidecarBinary`). Round-trip rules run
  here, never at `ParsekScenario.OnSave/OnLoad` (not xUnit-drivable).

## Mental Model

```
                 +-------------------------------------------+
   save dir  --> |  LOADER (analyzer, file I/O lives here)   |
                 |  parse .sfs -> trees; hydrate sidecars     |
                 |  parse ledger; every parse failure is a    |
                 |  FINDING, never a crash                     |
                 +----------------------+--------------------+
                                        |  loaded model (pure data)
                                        v
                 +-------------------------------------------+
                 |  INVARIANT CORE (pure, no file I/O)        |
                 |  IRecordingInvariant[] over the model      |
                 |  each rule -> findings + CitedContract     |
                 +----------------------+--------------------+
                                        |  List<Finding>
                                        v
                 +-------------------------------------------+
                 |  REPORTERS                                 |
                 |  machine report (stable JSON-ish .txt)     |
                 |  + human summary (grep-friendly lines)     |
                 +-------------------------------------------+

   Three entry points, ONE core + ONE rule set:
     - xUnit [Trait Manual] test  -> CI floor over fixture corpus
     - scripts/analyze-recordings.ps1 -> harness post-run + ad hoc triage
     - (future) in-game H5 category -> same rules over live RecordingStore
```

The hard separation is loader vs core. The loader owns all file I/O and all
"the bytes did not parse" findings. The core is a set of pure functions over
already-loaded objects: give it a `List<Recording>`, a `List<RecordingTree>`,
the `ParsekScenario`-equivalent state (tombstones, supersede rows), and the
parsed `CareerSaveSnapshot`, and it returns findings. The in-game H5 category
(M-A3) will feed the same core the live `RecordingStore.CommittedRecordings`
walked RAW (the whole committed list, NOT routed through `EffectiveState`) and
get identical findings, because the core never asked where the data came from.
H5 deliberately does not route through `EffectiveState.ComputeERS`: ERS is the
visibility-filtered effective set and would HIDE the superseded rows INV7 exists
to check (a superseded recording ERS drops is still a row whose
`SupersedeTargetId` must resolve). The offline loader walks the whole save
directory including those rows; the in-game walker feeds the same complete raw
set so the same core produces the same verdict.

Design decision, grounded: the core must resolve `TrackSection.referenceFrame`
per UT and dispatch RELATIVE sections through the production offset contract,
never read `TrajectoryPoint.latitude/longitude/altitude` as body-fixed lat/lon
for a RELATIVE section. `TrackSection` (`TrackSection.cs`) stores anchor-local
metre offsets in those exact fields for `ReferenceFrame.Relative`
(`.claude/CLAUDE.md` RELATIVE contract; recorder side
`FlightRecorder.ApplyRelativeOffset` -> `TrajectoryMath.ComputeRelativeLocalOffset`).
A naive flat-`Points` reader would itself commit the documented RELATIVE-misread
bug and WARN on every parent-anchored debris recording (plan section 11 Phase 0
exit criterion).

## Data Model

All new types live in `Source/Parsek.Tests/Analyzer/` (justification in
Behavior). None of them persist to save files, so there is no ConfigNode
serialization to design for the model itself; the report format is a separate
frozen output contract (see Report format below).

### Core interfaces and records

```
namespace Parsek.Tests.Analyzer

enum VerdictLevel { Info = 0, Warn = 1, Fail = 2, StaleFixture = 3 }

struct Finding
    string   RuleId          // stable, e.g. "INV1-UT-MONOTONIC"
    VerdictLevel Level
    string   Target          // recordingId / treeId / "<save>" / file path
    int      SectionIndex     // -1 when not section-scoped
    string   Message          // human, one line, ASCII
    string   CitedContract    // production member the rule checks (REQUIRED)

interface IRecordingInvariant
    string RuleId { get; }
    string CitedContract { get; }   // e.g. "RecordingStore.IsRecordingSchemaCompatible"
    IEnumerable<Finding> Evaluate(AnalyzerModel model)

// AnalyzerModel is the pure, already-loaded input. No Stream, no path, no
// FileInfo on it. This is what the future in-game H5 category also builds.
class AnalyzerModel
    string SaveName
    IReadOnlyList<Recording>      Recordings          // flat, all trees flattened
    IReadOnlyList<RecordingTree>  Trees
    IReadOnlyList<LedgerTombstone> Tombstones
    IReadOnlyList<RecordingSupersedeRelation> SupersedeRelations
    CareerSaveSnapshot            CareerSave          // null when not career / unparsable
    IReadOnlyList<GameAction>     Ledger              // RAW Ledger.Actions (unfiltered); INV8 computes the ELS filter internally from Tombstones
    IReadOnlyList<LoadFault>      LoadFaults          // parse failures the loader recorded
    FixtureStamp?                 FixtureStamp        // null for non-fixture subjects
    Func<string, CelestialBody>   BodyResolver        // injected; TestBodyRegistry in xUnit

struct LoadFault                 // a file that failed to parse == a finding, not a crash
    string FilePath
    string FileKind              // "trajectory" / "snapshot" / "tree-node" / "ledger" / "rewindpoint" / "annotation"
    string Reason                // parser's failure reason string
    string RecordingId           // when resolvable from the path, else null

struct FixtureStamp
    int SchemaGeneration
    string Provenance            // "synthetic" | "harvested"
```

### Loader output and report

```
class AnalysisReport
    string SaveName
    string AnalyzerVersion       // frozen report-format version, bumped only on format change
    int    SubjectSchemaGeneration   // discovered from the data
    List<Finding> Findings       // sorted: Level desc, then RuleId, then Target
    Counts Counts                // { fail, warn, info, staleFixture }

struct Counts { int Fail; int Warn; int Info; int StaleFixture; }
```

### Report format (frozen output contract)

Two files per subject, written next to a results directory the caller picks:

- `<save>.analysis.json`: machine-readable. A stable, sorted JSON object.
  Fields in fixed order: `analyzerVersion`, `saveName`, `subjectSchemaGeneration`,
  `counts` (`fail`/`warn`/`info`/`staleFixture`), then `findings` as an array of
  `{ ruleId, level, target, sectionIndex, message, citedContract }` sorted by
  (level desc, ruleId, target, sectionIndex). Determinism is a hard requirement:
  the same input bytes produce byte-identical JSON (no timestamps, no absolute
  paths, no dictionary-iteration order). Floats serialize with
  `ToString("R", CultureInfo.InvariantCulture)` (house rule from `.claude/CLAUDE.md`).
- `<save>.analysis.txt`: human summary. One header line
  `[Analyzer] save=<name> generation=<n> FAIL=<a> WARN=<b> INFO=<c> STALE=<d>`
  then one line per finding: `<LEVEL> <ruleId> target=<t>[#section] <message>`.
  Grep-friendly, mirrors the `validate-ksp-log.ps1` output style.

`AnalyzerVersion` is a constant in the analyzer; it is bumped only when the
report schema changes, and a test pins the format (see Test Plan). Adding a new
rule does NOT bump it (rules are data inside `findings`).

## Behavior

### Where the code lives (proposal + justification)

- `Source/Parsek.Tests/Analyzer/` holds the invariant core (`IRecordingInvariant`,
  `AnalyzerModel`, `Finding`, the concrete rule classes), the loader
  (`SaveDirectoryLoader`), and the reporters. It lives in the Tests project
  because it consumes the `internal` headless seams that are already exposed to
  tests: `RecordingStore.LoadTrajectorySidecarForTesting`,
  `RecordingStore.LoadSnapshotSidecarForTesting`,
  `RecordingStore.TryProbeTrajectorySidecar`,
  `RecordingStore.IsRecordingSchemaCompatible`, `RecordingTreeRecordCodec`,
  `RecordingManifestCodec`, `CareerSaveParser.Parse`, `EffectiveState.ComputeERS`
  / `ComputeELS`, and the `TrajectoryMath` `bodyResolver` seam with
  `Tests.TestBodyRegistry`. Re-homing them to a separate assembly would require
  making all those seams `public`; that is a bigger contract change than this
  module should force, and the plan (section 3.1) says pin against public TEST
  APIs, which these `internal`-to-Tests seams already are.
- A `[Trait("Category", "Manual")]`-adjacent xUnit entry point
  (`Analyzer/OfflineAnalyzerTests.cs`) runs the analyzer over the fixture corpus
  in CI, and over an env-var-supplied directory for ad hoc use. It follows the
  exact pattern of the existing `InjectAllRecordings` Manual test
  (`docs/dev/synthetic-recordings.md`): env-var input, CI-safe skip when the
  input directory is absent.
- `scripts/analyze-recordings.ps1` is the thin CLI driver, modeled on
  `scripts/validate-ksp-log.ps1` (which already "drives a real xUnit pass",
  plan section 1). It resolves the target save dir, sets the env var, invokes
  `dotnet test` filtered to the analyzer entry point, and surfaces the report
  files. This keeps ONE execution path (the xUnit host) for all three run modes,
  so the harness and the human get identical verdicts.
- `Source/Parsek.Tests/Analyzer/RecordingSectionDump.cs` is a permanent
  `[Trait("Category","Manual")]` triage helper that extends the ad-hoc-triage
  mode. Given `PARSEK_DUMP_SAVE` (+ optional `PARSEK_DUMP_RECORDING`) it loads the
  same hydrated model via `SaveDirectoryLoader` and dumps each recording's
  `TrackSection` `referenceFrame` / `environment` / `source` / `isBoundarySeam` /
  span / frame-and-checkpoint counts plus the resolved rewind-save state
  (`SAVES-PRESENT` / `SAVES-MISSING` / `no-rewind-save`), to a
  `<save>/analysis/<id|all>.sectiondump.txt`. This is the ground-truth tool a human
  uses to confirm an INV2 overlap or an INV9 missing-rewind finding against the
  ACTUAL section kinds instead of guessing from the report line; it is how the
  2026-07-11 tuning pass proved the INV2 overlaps were checkpoint-vs-checkpoint /
  empty-physical double-cover and the INV9 "missing" saves were a wrong-directory
  probe.

### Run modes

All three modes call the same `Analyzer.Run(saveDir, resultsDir)`. Both
parameters are REQUIRED (there is no zero-arg overload): `resultsDir` is where
the two report files are written and must be passed explicitly so a caller can
never accidentally scatter reports next to a user's save. In CI mode the caller
supplies the default `resultsDir` = the test output directory
(`Source/Parsek.Tests/bin/Debug/net472/analyzer-results/`, resolvable from the
xUnit working dir); the CLI driver defaults it to a `analysis/` folder beside the
target save; the harness passes its per-run results directory.

1. **CI regression floor**: input dir = the fixture corpus
   (`PARSEK_ANALYZER_SAVE` unset -> default corpus path under the test tree);
   `resultsDir` = the CI default test-output directory named above.
   Any FAIL fails the build. STALE-FIXTURE also fails the build (a stale corpus
   is a maintenance failure, per plan section 7).
2. **Harness post-run**: input dir = a mission-produced save. The harness reads
   `<save>.analysis.json` `counts.fail`; nonzero is a PARSEK-FAIL verdict
   (plan section 9 step 2). WARN is surfaced but not failing.
3. **Ad hoc triage**: input dir = a user bug-report save copied locally. The
   human reads `<save>.analysis.txt`. Malformed input is expected and produces
   findings, never a stack trace (see Edge Cases).

### The loader

`SaveDirectoryLoader.Load(saveDir, bodyResolver) -> AnalyzerModel`:

1. Locate `persistent.sfs` (and any numbered `*.sfs` the caller names). Parse
   with `ConfigNode.Load`. A parse failure is one `LoadFault{ FileKind="sfs" }`
   and an empty model, not an exception.
2. Find the `ParsekScenario` SCENARIO node. Hydrate recording-tree records via
   `RecordingTreeRecordCodec.LoadRecordingFrom` per RECORDING node; each throw
   is caught and recorded as a `LoadFault{ FileKind="tree-node", RecordingId=... }`.
   Read `LedgerTombstones` and supersede relations from the scenario node.
3. For each recording, resolve sidecar paths via `RecordingPaths`
   (`BuildTrajectoryRelativePath`, `BuildVesselSnapshotRelativePath`,
   `BuildGhostSnapshotRelativePath`, `BuildAnnotationsRelativePath`), validating
   ids through `RecordingPaths.ValidateRecordingId`. Hydrate trajectory via
   `RecordingStore.LoadTrajectorySidecarForTesting`, snapshots via
   `RecordingStore.LoadSnapshotSidecarForTesting`. A `false` return or throw is
   a `LoadFault` with the probe's failure reason
   (`RecordingStore.TryProbeTrajectorySidecar` supplies `probe.FailureReason`).
4. Parse the career save via `CareerSaveParser.Parse(gameNode)` when the save is
   career; a null / unparsable result leaves `CareerSave = null` and records a
   `LoadFault{ FileKind="career" }` only when the ledger rule needs it.
5. Load the RAW ledger. The loader materializes `Ledger.Actions` verbatim into
   `AnalyzerModel.Ledger` and the tombstone rows into `AnalyzerModel.Tombstones`,
   with NO pre-filtering. The loader does NOT compute an ELS-ready list: the ELS
   filter is a RULE concern, not a loader concern. INV8 owns computing the ELS
   filter from the raw actions plus the tombstones (see INV8), so its
   tombstone-resolves-against-a-real-action check has something to check. Pushing
   an already-filtered "ELS-ready" list into the model would make that check
   vacuous by construction (a filtered list can never contain a tombstone
   pointing at an action it already dropped). The analyzer cannot call the live
   static `EffectiveState.ComputeELS` anyway (it reads process statics); INV8
   reconstructs the filter from the loaded `Tombstones` using the exact ELS
   definition cited in `EffectiveState.cs` (ELS = actions minus any action whose
   `ActionId` is tombstoned).

`SuppressLogging` on `RecordingStore` is set true during load so the analyzer
does not spam KSP-style logs; the loader emits its own analyzer-tagged lines.

### The invariant rules

Ten invariant families from plan section 7, plus 7b annotation staleness, plus
the LOADER-FAULT rule that turns loader parse failures into findings. Each is one
`IRecordingInvariant` (or a small cluster sharing a `CitedContract`).

- **LOADER-FAULT** (`RuleId LOADER-FAULT`). A file the loader could not parse is
  itself a finding, never a crash (design "The loader"). The loader records a
  `LoadFault` per unparsable file and keeps going; this rule emits a FAIL for
  every `LoadFault` whose `FileKind` is one of `{sfs, tree-node, ledger}`. Those
  three kinds have no other owning rule, so before it existed a corrupt
  `persistent.sfs`, a throwing RECORDING tree node, or an unparsable `ledger.pgld`
  analyzed GREEN. The `trajectory` and `snapshot` kinds are deliberately EXCLUDED:
  INV5 owns `.prec` faults and INV4 owns `_vessel/_ghost.craft` faults, each with
  its own recording-scoped context, so a fault is reported exactly once. The
  finding Target is the recording id when the fault carries one, else a
  `<fileKind>` token; the message carries only the filename (not the absolute
  path) so report bytes stay deterministic. CitedContract:
  `SaveDirectoryLoader.Load` / `ConfigNode.Load`.
- **INV1 UT monotonicity** (`RuleId INV1-UT-MONOTONIC`). Per `TrackSection`, the
  `frames` / `bodyFixedFrames` / `checkpoints` UT sequences are non-decreasing;
  the flat `Recording.Points` UT sequence is non-decreasing; per-section
  `startUT <= endUT`. Violation = FAIL. A NaN UT (any point UT, or a section
  `startUT`/`endUT`) is also FAIL: `double.IsNaN` is checked explicitly because a
  NaN never trips the strict back-step / ordering comparison (`NaN < x` and
  `NaN > x` are both false), so a NaN-poisoned sidecar would otherwise analyze
  GREEN and then break `TrajectoryMath`'s binary-search sampler at playback. One
  NaN finding per sequence, same bounding style as the first-back-step reporting.
  CitedContract: `TrajectoryPoint.ut` + `TrackSection.startUT/endUT` ordering
  assumed by `TrajectoryMath` sampling.
- **INV2 no double-cover** (`RuleId INV2-NO-DOUBLE-COVER`). No two sections'
  `[startUT,endUT]` spans overlap in interior UT. Gaps are allowed
  (on-rails BG spans emit no TrackSections;
  `TrackSection.boundaryDiscontinuityMeters` models legitimate seams). A gap not
  covered by any section AND not bridged by an `OrbitSegment` -> WARN
  (`INV2-UNCOVERED-SPAN`), not FAIL. Overlap -> FAIL. CitedContract:
  `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` (sections are disjoint
  producers) + `TrackSection.boundaryDiscontinuityMeters` + the on-rails
  no-TrackSection contract (`BackgroundRecorder` `BackgroundOnRailsState`).
  **Uncovered-span tolerance floor (tuning 2026-07-11):** a section is built from
  sampled frames, so its `startUT`/`endUT` are its first/last frame UT, and at a
  section boundary the last frame of section A and the first frame of section B
  are one sample apart. A sub-sample-step gap between `end(A)` and `start(B)` is a
  legitimate boundary seam, not a coverage hole, and must NOT WARN. The
  `INV2-UNCOVERED-SPAN` WARN is therefore gated on a floor of
  `Inv2NoDoubleCover.UncoveredSpanToleranceSeconds = 8.0s`, the coarsest recorder
  single-sample step (`ParsekSettings.GetMaxSampleInterval(SamplingDensity.Low)` =
  8.0s; Medium 3.0s, High 1.0s). The floor is set at the coarsest cadence so it is
  density-agnostic. This removed the bulk of the real-save WARN noise (0.04-0.28s
  boundary seams across c1 / s15 / orbital-supply-route / mun); genuine coverage
  gaps in flown saves are hundreds to millions of seconds (e.g. a stray far-future
  `OrbitalCheckpoint` leaving a ~1.5M-second hole) and stay WARN. The double-cover
  FAIL path is UNCHANGED and stays strict: the overlapping-checkpoint pattern
  observed in real saves is a producer bug (`OrbitSegmentCheckpointBridge` clips
  only against physical sections, not checkpoint-vs-checkpoint), not a legitimate
  coarse/fine wrap, so no type-aware exemption is granted (see
  `docs/dev/todo-and-known-bugs.md` "Overlapping / duplicate TrackSections").
- **INV3 RELATIVE contract** (`RuleId INV3-RELATIVE-CONTRACT`). For a
  `ReferenceFrame.Relative` section: `anchorRecordingId` (non-loop) OR
  `anchorVesselId` (loop) must be present, and out-of-`[-90,90]`/`[-180,180]`
  values in the `frames` lat/lon fields are EXPECTED (they are metre offsets)
  and never flagged. For a `ReferenceFrame.Absolute` section, out-of-range
  lat/lon -> WARN (`INV3-ABSOLUTE-RANGE`) until KSP longitude normalization is
  cited, then promote to FAIL. A RELATIVE section with neither anchor -> FAIL.
  CitedContract: `TrackSection.referenceFrame` / `anchorRecordingId` /
  `anchorVesselId` + the RELATIVE offset contract
  (`TrajectoryMath.ComputeRelativeLocalOffset` /
  `ParsekFlight.TryResolveRelativeOffsetWorldPosition`).
- **INV4 part-event PID resolution** (`RuleId INV4-PARTEVENT-PID`). Every part
  event's PID resolves against the paired snapshot's part-PID set. For synthetic
  ghosts the builder assigns `persistentId = 100000 + idx*1111`
  (`VesselSnapshotBuilder.AddPart`); the rule checks membership, not the
  formula. Unresolvable PID with a snapshot present -> FAIL; no snapshot present
  (destroyed / showcase) -> INFO. CitedContract: `VesselSnapshotBuilder.AddPart`
  PID assignment + the ghost-event lookup contract (`.claude/CLAUDE.md` ghost
  event <-> snapshot PID).
- **INV5 schema gate** (`RuleId INV5-SCHEMA-GATE`, plus two sibling rule ids the
  one rule emits under: `INV5-ORPHAN-SIDECAR` and `INV5-GENERATIONS`). Every
  recording + trajectory sidecar passes
  `RecordingStore.IsRecordingSchemaCompatible` (format 1, generation 4). A
  recording whose metadata and sidecar disagree, or that fails the gate, -> FAIL
  with the exact reason string (`generation-missing` / `generation-older` /
  `generation-newer` / `format-version-mismatch`) under `INV5-SCHEMA-GATE`. A
  `.prec` on disk with no matching tree recording -> WARN under
  `INV5-ORPHAN-SIDECAR`. The generations inventory is emitted under
  `INV5-GENERATIONS` and is CONDITIONAL-INFO: it fires only when informative
  (more than one distinct generation is present, or the single generation present
  is not the current one), so a homogeneous current-gen save stays finding-free
  (the clean-data-is-green convention). The distinct rule ids let a triage grep /
  the harness parser separate a hard schema reject (red) from an orphan (WARN) or
  the inventory (INFO) without parsing the message body. CitedContract:
  `RecordingStore.IsRecordingSchemaCompatible`,
  `RecordingStore.CurrentRecordingFormatVersion`,
  `RecordingStore.CurrentRecordingSchemaGeneration`.
- **INV6 resource manifest consistency** (`RuleId INV6-RESOURCE-MANIFEST`).
  Where a resource manifest is present, it round-trips through
  `RecordingManifestCodec.SerializeResourceManifest` /
  `DeserializeResourceManifest` and its per-resource deltas are internally
  consistent. Manifests are OPTIONAL: a Gloops / showcase ghost with no manifest
  is INFO, not a finding. A missing manifest on a NORMAL committed flight
  recording is also INFO in v1 (the presence-rule table lists recording kinds and
  none currently require a manifest); tightening the flight-recording cell to
  WARN is a candidate once fixture data shows manifests are universal on flown
  recordings. CitedContract:
  `RecordingManifestCodec.SerializeResourceManifest` +
  `ResourceManifest.ComputeResourceDelta`.
- **INV7 tree topology** (`RuleId INV7-TREE-TOPOLOGY`). All
  `ParentRecordingId` / `ChainId` / `ChainBranch` / `anchorRecordingId` /
  `ParentAnchorRecordingId` / `SupersedeTargetId` / tombstone links resolve to
  an existing recording (or are null); no cycles in the parent graph or the
  supersede graph. `ChainIndex` contiguity is scoped PER `(ChainId, ChainBranch)`
  (`ChainBranch > 0` = parallel ghost-only continuations, flight-recorder 9A.4)
  with a supersede-boundary exemption for HEAD/TIP splits. A dangling link or a
  cycle -> FAIL; a `(ChainId, ChainBranch)` index gap not explained by a
  supersede boundary -> FAIL; a gap AT a supersede boundary -> INFO. The rule also
  iterates `model.SupersedeRelations`: a row whose `OldRecordingId` or
  `NewRecordingId` is absent from the model (a one-sided orphan) -> WARN, not FAIL.
  This is WARN to match production `LoadTimeSweep`'s orphan-supersede warn-log
  severity: the forward supersede walk terminates cleanly on a missing endpoint,
  so it is a maintenance signal rather than a broken invariant (contrast the
  `Recording.SupersedeTargetId` and tombstone `RetiringRecordingId` dangling
  cases, which stay FAIL because they break visibility / retirement resolution).
  CitedContract: `Recording.ChainId/ChainIndex/ChainBranch` +
  `RecordingTreeSplitter` HEAD/TIP split + `RecordingOptimizer.CanAutoMerge`
  supersede guard + `EffectiveState.IsSupersededByRelation` (verify against the
  optimizer's supersede guard before shipping, per plan section 7).
- **INV7b annotation staleness** (`RuleId INV7B-ANNOTATION-STALE`). Where a
  `.pann` sidecar exists, its recorded source epoch matches the paired `.prec`
  sidecar's current epoch. `PannotationsSidecarBinary.TryProbe` yields
  `probe.SourceSidecarEpoch` + `probe.SourceRecordingFormatVersion`; if the
  `.prec`'s current epoch is newer, the annotation is stale -> WARN
  (annotations are a derived cache, not authored data, rendering design 17.3.1).
  A `.pann` for a recording with no `.prec` -> WARN (orphan). CitedContract:
  `PannotationsSidecarBinary.TryProbe` (`SourceSidecarEpoch`) +
  `RecordingPaths.BuildAnnotationsRelativePath`.
- **INV8 ledger** (`RuleId INV8-LEDGER` for part (a); part (b) emits under the
  sibling id `INV8-CAREER-DIFF`). Two parts, distinct severities and distinct rule
  ids so the harness can separate an ELS-consistency failure from a career-diff
  observation.
  (a) ELS internal consistency, over the RAW model. The rule itself computes the
  ELS filter from `model.Ledger` (raw actions) plus `model.Tombstones` using the
  ELS definition cited in `EffectiveState.cs` (ELS = raw actions minus any action
  whose `ActionId` is tombstoned), then asserts every tombstone's target
  `ActionId` resolves against the RAW action list. Because the input is the raw
  list, this check is non-vacuous: a tombstone pointing at an id absent from the
  raw actions is a real dangling reference. A dangling tombstone -> FAIL. This
  part runs for every save (career or not). SINGLE-REPORT POLICY: when a
  `LoadFault{FileKind="ledger"}` is present the RAW action list is incomplete, so
  every tombstone would look dangling; part (a) then SKIPS the per-tombstone
  dangling check and defers to the LOADER-FAULT finding (mirroring INV5's tested
  faulted-trajectory skip), so a corrupt ledger is reported exactly once.
  (b) Career-diff reconstruction, career saves only, WARN severity in v1. When
  the save is career the rule reconstructs the ledger and diffs against the
  save's parsed career totals via `LedgerGroundTruthDiff.Compare`. Any divergence
  (hard or report-only) -> WARN in the offline analyzer, NOT FAIL.
  OPEN QUESTION: the offline reconstruction is not yet specified end-to-end.
  `LedgerGroundTruthDiff.Compare` needs a `LedgerReconstructionSnapshot`, which
  in-game comes from `LedgerOrchestrator.RecalculateAndPatch` plus the module
  running-readers, with `facilityMaxLevels` injected from live KSP. Headless,
  this requires (1) proving the recalculation engine is Unity-free (or extracting
  a Unity-free seam) and (2) a hardcoded stock facility-max-levels map to replace
  the live-KSP injection. Until that seam is proven, the offline career diff is
  informational-only and capped at WARN. The FAIL-severity career-diff variant
  belongs to the in-game H5 path, where the `LedgerGroundTruthHarness` seam
  already reconstructs against a live quicksave (module M-A3 / H5); there hard
  divergence (`report.HardFailures`) -> FAIL, report-only per-identity divergence
  -> WARN. A career save with no injected reconstruction reports
  reconstruction-not-available INFO under `INV8-CAREER-DIFF`; a NON-CAREER save is
  SILENT (no part-(b) finding at all, not INFO): the career diff carries no
  information on a Sandbox / Science save, and staying finding-free preserves the
  clean-data-is-green convention. CitedContract: `EffectiveState.ComputeELS` (ELS
  definition) + `CareerSaveParser.Parse` + `LedgerGroundTruthDiff.Compare` +
  `LedgerGroundTruthHarness` (in-game FAIL path).

**Conditional-INFO policy (and its trade-off).** Three INFO sites are emitted
ONLY when they carry information, deliberately preferring a finding-free clean
report over an always-present inventory line:

- an absent resource manifest (INV6) is SILENT (not even INFO) on the recording
  kinds where a manifest is optional;
- the INV5 generation inventory (`INV5-GENERATIONS`) fires only when more than the
  single current generation is present;
- the non-career INV8 part (b) is SILENT as described above.

The trade-off: a clean current-gen save produces a byte-empty findings list,
which is the clearest possible "all green" signal and keeps CI diffs quiet, but
it means the report does NOT positively confirm "I looked at manifests /
generations / the career diff and they were fine" -- absence of a finding is the
only evidence the check ran. The per-subject loader summary line
(`Analyzer: load save=... trees=n recordings=n loadFaults=n`) and the
`subjectSchemaGeneration` report field carry the "the analyzer ran and saw N
recordings at generation G" signal instead, so the silent-clean policy does not
lose the run-happened evidence; it only moves it out of the findings list.
- **INV9 rewind-save + id validation** (`RuleId INV9-REWINDPOINT`). The field
  this rule checks is `Recording.RewindSaveFileName` (the Rewind-to-Separation
  quicksave captured at recording start), stored at `Parsek/Saves/<id>.sfs` via
  `RecordingPaths.BuildRewindSaveRelativePath` with the `parsek_rw_` filename
  prefix. Every recording id and every rewind id passes
  `RecordingPaths.ValidateRecordingId` (path traversal / invalid chars) -> FAIL,
  emitted BEFORE any filesystem access. A referenced rewind save whose
  `Parsek/Saves/<id>.sfs` is missing -> WARN; present-but-unparsable -> FAIL. An
  unreferenced `parsek_rw_*.sfs` on disk -> a single per-save INFO inventory line
  (count). CitedContract: `RecordingPaths.ValidateRecordingId` +
  `RecordingPaths.BuildRewindSaveRelativePath`.
  **Tuning 2026-07-11 (three corrections after the first 8-save run):**
  1. **Directory fix.** The rule previously probed `Parsek/RewindPoints/<id>.sfs`
     (`BuildRewindPointRelativePath`, the rp_* RewindPoint system) for the
     `parsek_rw_*` `RewindSaveFileName` field, which lives in `Parsek/Saves/`.
     That flagged every present rewind save as missing (c1 18, l2 1, test career
     10, mun 1 -- all files present on disk). Now routes through
     `BuildRewindSaveRelativePath`. The rp_* RewindPoint system (referenced by
     scenario `RewindPoints` slots / `BranchPoint`s) is NOT loaded by the offline
     model, so it is out of INV9's scope.
  2. **Missing -> WARN, not FAIL.** A missing rewind save is a dangling reference,
     not proven corruption: `RecordingStore.DeleteRecordingFiles` deletes a rewind
     save with a discarded recording WITHOUT reference-counting siblings that
     share the same save via `ParsekFlight.CopyRewindSaveToRoot` ("first recorder
     wins"), a sealed (`MergeState.Immutable`) recording can no longer be rewound,
     and production treats a missing rewind hint as benign
     (`ParsekScenario.ResolveLimboResumeRewindSave`). WARN surfaces it without
     failing the run (s15 4, orbital supply route 4).
  3. **Orphan -> INFO inventory.** An unreferenced `parsek_rw_*.sfs` is an EXPECTED
     benign state (`ParsekFlight.cs`: "keep the on-disk parsek_rw_*.sfs but no
     recording ever references it"), so it is one per-save INFO count line, not a
     WARN. The prior orphan scan ran over `Parsek/RewindPoints/` and false-flagged
     LIVE rp_* RewindPoints (which the model cannot resolve) as orphans.
- **INV10 codec round-trips** (`RuleId INV10-CODEC-ROUNDTRIP`). For each loaded
  recording, re-serialize and re-deserialize at the CODEC seams and assert
  structural equality: `RecordingTreeRecordCodec.SaveRecordingInto` ->
  `LoadRecordingFrom`, `RecordingManifestCodec` serialize/deserialize pairs, and
  the trajectory sidecar `TrajectorySidecarBinary` write/read. Round-trips run
  ONLY at these seams, never at `ParsekScenario.OnSave/OnLoad` (not
  xUnit-drivable; known constraint, plan section 7 invariant 10). A non-round-
  tripping field -> FAIL naming the field. CitedContract:
  `RecordingTreeRecordCodec.SaveRecordingInto` / `LoadRecordingFrom`,
  `RecordingManifestCodec`, `TrajectorySidecarBinary`.

### Fixture versioning

The analyzer reads a `FixtureStamp` from a `fixture-generation.txt` file the
fixture corpus carries (one line: `generation=<n> provenance=<synthetic|harvested>`).
When the subject is the fixture corpus and its stamp generation differs from
`RecordingStore.CurrentRecordingSchemaGeneration`, EVERY recording in it is
reported STALE-FIXTURE, not FAIL, and the run advises re-running the M-A4
regeneration script. Harvested fixtures (which cannot be regenerated by script
per the no-migration policy) are reported STALE-FIXTURE with a re-harvest-queue
note. Non-fixture subjects (harness / triage saves) have no stamp and skip this
check entirely. CitedContract: `RecordingStore.CurrentRecordingSchemaGeneration`
+ plan section 7 versioning policy.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **Truncated `.prec` (partial write / crash mid-save)**. Scenario: a
   trajectory sidecar ends mid-record. Expected: `TryProbeTrajectorySidecar`
   returns a probe with `Supported=false` or the read throws; the loader records
   a `LoadFault{ FileKind="trajectory", Reason=<probe reason> }` and a
   `INV5-SCHEMA-GATE` FAIL. The analyzer does not crash. v1.
2. **Text-format `.prec` (pre-v0-reset corpus)**. Scenario: a 9/14-style legacy
   text sidecar. Expected: `TryProbeTrajectorySidecar` returns
   `Supported=false, FailureReason="text-sidecar-unsupported"`; loader records a
   LoadFault; INV5 FAIL with that reason. Matches the production hard reject at
   `RecordingStore.DeserializeTrajectorySidecar`. v1.
3. **Pre-reset binary magic**. Scenario: a sidecar with the old binary magic
   tag. Expected: `HasPreResetBinaryMagic` path yields a probe with the
   pre-reset failure reason; LoadFault + INV5 FAIL. v1.
4. **Recording metadata generation != sidecar generation**. Scenario: tree node
   says gen 4, `.prec` probe says gen 3. Expected: INV5 FAIL reason
   `generation-mismatch` (the loader mirrors the production check at
   `RecordingSidecarStore.LoadRecordingFiles`). v1.
5. **RELATIVE section with out-of-range lat/lon and a valid anchor**. Scenario:
   parent-anchored debris with metre offsets in the lat/lon fields
   (values > 90 / > 180). Expected: NOT flagged; the values are anchor-local
   metres by contract. INV3 passes. This is the documented false-positive the
   naive reader would produce. v1.
6. **ABSOLUTE section with out-of-range longitude**. Scenario: a lon value of
   185 in an Absolute section. Expected: WARN `INV3-ABSOLUTE-RANGE` (KSP may
   normalize; not yet cited). Deferred to FAIL once normalization is cited. v1
   as WARN.
7. **BG on-rails span with no TrackSections between two authored sections**.
   Scenario: a legitimate uncovered UT gap. Expected: WARN
   `INV2-UNCOVERED-SPAN` only if no `OrbitSegment` bridges it; if an
   `OrbitSegment` covers the gap, no finding. Never FAIL. v1.
8. **Two overlapping sections**. Scenario: sections `[100,200]` and
   `[150,250]`. Expected: INV2 FAIL (double cover). v1.
9. **Gloops / showcase ghost with no resource manifest**. Scenario: a ghost-only
   recording. Expected: INV6 INFO (manifest optional per recording kind), no
   FAIL/WARN. v1.
10. **Chain with a `ChainIndex` gap at a supersede boundary (HEAD/TIP split)**.
    Scenario: `(ChainId=X, ChainBranch=0)` has indices 0,1,3 where 2 was split
    into HEAD/TIP by a re-fly. Expected: INV7 INFO (supersede-boundary
    exemption), not FAIL. v1.
11. **Chain with a `ChainIndex` gap NOT at a supersede boundary**. Scenario:
    indices 0,1,3 with no supersede row explaining the missing 2. Expected: INV7
    FAIL. v1.
12. **Parallel ghost-only continuation (`ChainBranch=2`)**. Scenario: two
    branches share a `ChainId`, each with its own contiguous index run.
    Expected: contiguity checked PER `(ChainId, ChainBranch)`; both pass
    independently even though the combined index set has "gaps". v1.
13. **Supersede row pointing at a missing recording**. Scenario:
    `SupersedeTargetId` references an id absent from the tree. Expected: INV7
    FAIL (dangling supersede link). v1.
14. **Cycle in the parent graph**. Scenario: A.parent=B, B.parent=A (corrupt).
    Expected: INV7 FAIL (cycle), and the walk terminates via a visited set
    (no infinite loop). v1.
15. **`.pann` newer-schema or stale-epoch**. Scenario: a `.pann` whose
    `SourceSidecarEpoch` is behind the `.prec`'s current epoch. Expected:
    INV7B WARN (stale annotation). Orphan `.pann` (no `.prec`) -> WARN. v1.
16. **Career diff hard divergence**. Scenario: reconstructed funds differ from
    the save's funds beyond tolerance with no uplift-clamp explanation.
    Expected OFFLINE: INV8 WARN (part (b) is WARN-capped until the headless
    reconstruction seam is proven; open question in INV8). Expected IN-GAME (H5,
    module M-A3): INV8 FAIL via the `LedgerGroundTruthHarness` seam. v1.
17. **Career diff report-only per-identity divergence**. Scenario: a per-subject
    science facet differs but the seeded pools match. Expected: INV8 WARN both
    offline and in-game (report-only under default
    `StrictPerIdentityForTesting=false`). v1.
17b. **Dangling tombstone (ELS internal inconsistency)**. Scenario: a tombstone
    whose target `ActionId` is absent from the raw `Ledger.Actions`. Expected:
    INV8 FAIL (part (a), non-vacuous because the model carries the RAW action
    list). Runs for career and non-career saves alike. v1.
18. **Non-career (Science/Sandbox) save**. Scenario: no career totals. Expected:
    INV8 INFO (skipped), no FAIL. v1.
19. **RewindPoint id with path traversal (`../evil`)**. Scenario: a corrupt or
    malicious id. Expected: INV9 FAIL via `RecordingPaths.ValidateRecordingId`
    rejecting the traversal sequence; the analyzer never touches the escaped
    path. v1.
20. **Orphan sidecar on disk (a `.prec` with no tree node)**. Scenario: a stale
    file the tree no longer references. Expected: WARN `INV5-ORPHAN-SIDECAR`
    (inventory), not FAIL. v1.
21. **Empty save directory / no `ParsekScenario` node**. Scenario: a fresh save
    or a non-Parsek save. Expected: an EMPTY report -- zero findings of any level
    (no synthetic "no Parsek footprint" INFO is emitted), zero FAIL, clean run.
    The empty-findings state is itself the signal: the human-report header line
    (`[Analyzer] save=... generation=0 FAIL=0 WARN=0 INFO=0 STALE=0`) and the
    Verbose loader summary (`recordings=0 loadFaults=0`) carry the "analyzed, saw
    nothing" evidence, consistent with the conditional-INFO / clean-data-is-green
    policy (no findings-list line for a save that carries no Parsek data). v1.
22. **Stale fixture corpus (generation bump landed, fixtures not regenerated)**.
    Scenario: corpus stamp gen 3, code gen 4. Expected: STALE-FIXTURE for every
    recording; CI red with the distinct STALE verdict, not FAIL (so the failure
    reads as "regenerate fixtures", not "code broke"). v1.
23. **Snapshot sidecar present but unparsable**. Scenario: a corrupt
    `_vessel.craft`. Expected: `LoadSnapshotSidecarForTesting` returns false;
    LoadFault + INV4 downgrades to INFO for that recording's PID checks (cannot
    resolve without the snapshot) plus a WARN that the snapshot failed to load.
    v1.
24. **Both loop (`anchorVesselId`) and non-loop (`anchorRecordingId`) anchors
    absent on a RELATIVE section**. Scenario: a corrupt relative section.
    Expected: INV3 FAIL (RELATIVE with no anchor). v1.
25. **Concurrent codec-seam round-trip drift on a NaN/Inf float field**.
    Scenario: an `OrbitSegment` with a NaN element. Expected: INV10 compares
    with NaN-aware equality (NaN == NaN treated equal for round-trip stability);
    a NaN that becomes a different value after round-trip -> FAIL. v1.

## What Doesn't Change

- No production Parsek source changes. The analyzer consumes existing
  `internal`-to-Tests seams; it adds no new public API and no new serialized
  field. If a rule needs a seam that is currently `private`, that seam is
  promoted to `internal` in a separate, reviewed change, not silently here.
- Recording / sidecar / ledger file formats are untouched. The analyzer reads;
  it never writes to a save.
- `ParsekScenario.OnSave/OnLoad`, the in-game test runner, and the command seam
  (M-A2) are not modified by this module.
- The RELATIVE / parent-anchored / schema-generation contracts are not
  reinterpreted. The analyzer asserts them as-is; a rule that would require
  changing a contract is a design bug, caught by the `CitedContract` review gate.
- No migration or back-compat path is added for pre-generation-4 recordings
  (pre-1.0 no-migration policy). Old-generation data is REPORTED (STALE-FIXTURE
  for fixtures, FAIL for real saves via INV5), never silently upgraded.

## Backward Compatibility

There is nothing to migrate: this is a new read-only tool. The one
compatibility surface is the report format. `AnalyzerVersion` freezes the
`.analysis.json` schema; a format change bumps it and updates the
report-format-stability test. Existing fixture corpora gain a
`fixture-generation.txt` stamp (M-A4 owns writing it); a corpus without a stamp
is treated as an unstamped non-fixture subject (stamp check skipped), so the
analyzer runs against today's corpus before M-A4 lands, just without the
STALE-FIXTURE gate.

## Diagnostic Logging

The analyzer emits its own lines under subsystem tag `Analyzer` (via
`ParsekLog` when hosted in-process, or plain stdout in the CLI). Every decision
point logs, per the house logging rule.

- Loader, per subject: `Analyzer: load save='<name>' trees=<n> recordings=<n>
  loadFaults=<n>` (Info, one-shot).
- Loader, per parse failure: `Analyzer: loadFault kind=<k> recording=<id>
  path='<p>' reason=<r>` (Warn). This is the "a file that failed to parse is
  itself a finding" contract made observable.
- Schema gate (INV5), per recording: on reject,
  `Analyzer: INV5 reject recording=<id> reason=<generation-older|...>
  metadataGen=<n> sidecarGen=<m>` (Warn), mirroring the production reason
  strings so a triage grep matches both the analyzer and KSP.log.
- No-double-cover (INV2), per overlap: `Analyzer: INV2 overlap recording=<id>
  a=[<s>,<e>] b=[<s>,<e>]` (Warn); per uncovered span:
  `Analyzer: INV2 uncovered recording=<id> span=[<s>,<e>] orbitBridged=<bool>`
  (Verbose, since gaps are common and legitimate).
- RELATIVE contract (INV3): per RELATIVE section resolved,
  `Analyzer: INV3 relative recording=<id>#<sec> anchorRec=<id|none>
  anchorVessel=<pid|0> offsetSample=<x,y,z>` (Verbose) so a reviewer can confirm
  the analyzer dispatched through the offset contract, not the lat/lon reader.
- Tree topology (INV7): per dangling/cycle,
  `Analyzer: INV7 badlink recording=<id> field=<ParentRecordingId|SupersedeTargetId|...>
  target=<id> kind=<dangling|cycle>` (Warn); per chain gap,
  `Analyzer: INV7 chaingap chainId=<x> branch=<b> missingIndex=<n>
  supersedeExempt=<bool>` (Info/Warn by verdict).
- Ledger (INV8): `Analyzer: INV8 diff recording-set career hard=<n>
  reportOnly=<n> facets=<n>` (Info), reusing the counts
  `LedgerGroundTruthDiff.Compare` already logs.
- Fixture stamp: `Analyzer: fixtureStamp generation=<n> provenance=<p>
  codeGeneration=<m> stale=<bool>` (Info).
- Report write: `Analyzer: report save='<name>' FAIL=<a> WARN=<b> INFO=<c>
  STALE=<d> json='<path>'` (Info, one-shot per subject).
- Every rule, at registration, logs its `CitedContract` once:
  `Analyzer: rule <ruleId> cites <CitedContract>` (Verbose) so the contract
  citations are visible in a run log, not just source.

Batch-counting convention (house rule): the loader iterates recordings /
sidecars with local counters and logs one summary line, not one line per item,
except the bounded per-overlap / per-badlink findings which are inherently few.

## Test Plan

Every test states the bug it catches. Tests live in
`Source/Parsek.Tests/Analyzer/`. Fixtures are built with `RecordingBuilder` /
`VesselSnapshotBuilder` / `ScenarioWriter` / `DebrisFrameContractRecordingFixture`
/ `RouteFixtureBuilder` so synthetic data flows through the identical invariant
suite (plan section 8: builders that emit invariant-violating data get caught,
and known-good builder output exposes wrong rules). Body resolution uses
`TestBodyRegistry.Install` for the injected `bodyResolver`. Classes touching
`RecordingStore` / `ParsekLog` statics use `[Collection("Sequential")]` +
`ResetForTesting` (house rule).

### Per-invariant tests (positive + violating fixture each)

- **INV1 positive**: a monotonic recording -> zero INV1 findings. Fails if the
  monotonicity check has an off-by-one that flags equal-UT structural snapshots
  (`TrajectoryPoint.flags` bit 0 samples share the previous UT).
- **INV1 violating**: a `RecordingBuilder` recording with a back-stepping UT
  point -> INV1 FAIL. Fails if a regression makes the rule accept descending UT,
  which would let a corrupt sidecar through that breaks
  `TrajectoryMath` binary-search sampling.
- **INV2 positive (with legitimate gap)**: two authored sections separated by an
  orbit-segment-bridged gap -> no FAIL, no uncovered WARN. Fails if the rule
  reverts to "no gaps" and false-alarms on every BG on-rails span.
- **INV2 violating**: two overlapping sections -> INV2 FAIL. Fails if the
  overlap detector misses interior overlap, letting double-covered UT (ambiguous
  playback position) through.
- **INV3 positive (RELATIVE metre offsets)**: a parent-anchored debris fixture
  (`DebrisFrameContractRecordingFixture`) with lat/lon values > 180 and a valid
  `anchorRecordingId` -> zero INV3 findings. Fails if the rule reads the offset
  fields as body-fixed lat/lon and false-alarms (the documented RELATIVE-misread
  bug, plan Phase 0 exit criterion).
- **INV3 violating (ABSOLUTE out-of-range)**: an Absolute section with lon=185
  -> INV3 WARN, not FAIL. Fails if a corrupt Absolute longitude is silently
  accepted (would place a ghost at the wrong surface point) or wrongly FAILs
  before normalization is cited.
- **INV3 violating (no anchor)**: a RELATIVE section with both anchors absent
  -> INV3 FAIL. Fails if an unanchored relative section passes, which would make
  playback unable to resolve a world position.
- **INV4 positive**: a ghost with events whose PIDs match its
  `VesselSnapshotBuilder` parts -> zero INV4 FAIL. Fails if the membership check
  is wrong and rejects valid `100000 + idx*1111` PIDs.
- **INV4 violating**: an event PID absent from the snapshot -> INV4 FAIL. Fails
  if unresolvable PIDs pass, which silently drops a part event at playback.
- **INV5 positive**: a current-generation recording -> zero INV5 FAIL. Fails if
  the gate rejects valid gen-4 data.
- **INV5 violating (each reason)**: fixtures forced to gen 0 / gen 3 / gen 5 /
  format 2 -> INV5 FAIL with reasons `generation-missing` / `generation-older`
  / `generation-newer` / `format-version-mismatch`. Fails if the analyzer's
  reasons drift from `RecordingStore.IsRecordingSchemaCompatible`, which would
  make triage greps mismatch KSP.log.
- **INV6 positive (manifest present)**: a recording with a resource manifest
  that round-trips -> zero INV6 FAIL. Fails if a valid manifest is flagged.
- **INV6 positive (manifest absent)**: a Gloops ghost with no manifest -> INV6
  INFO, not FAIL. Fails if the rule wrongly requires manifests on ghost-only
  recordings.
- **INV6 violating**: a manifest whose deserialized deltas contradict its
  serialized totals -> INV6 FAIL. Fails if manifest corruption passes.
- **INV7 positive**: a valid multi-branch chain tree
  (undock split, `ChainBranch=0` and a `ChainBranch=2` ghost continuation) ->
  zero INV7 FAIL. Fails if per-branch contiguity is not scoped and the combined
  index set's "gaps" false-alarm.
- **INV7 violating (dangling)**: a `SupersedeTargetId` to a missing id -> INV7
  FAIL. Fails if dangling supersede links pass (broken re-fly visibility).
- **INV7 violating (cycle)**: a two-node parent cycle -> INV7 FAIL and the test
  completes (no hang). Fails if the walker lacks a visited set and infinite-loops
  on corrupt data.
- **INV7 exemption**: a HEAD/TIP split gap -> INV7 INFO, not FAIL. Fails if the
  supersede-boundary exemption regresses and false-alarms on every re-flown tree.
- **INV7b positive**: a `.pann` whose `SourceSidecarEpoch` matches the `.prec`
  -> zero INV7B finding. Fails if fresh annotations are flagged stale.
- **INV7b violating**: a `.pann` with an older `SourceSidecarEpoch` -> INV7B
  WARN. Fails if stale annotations pass, which would render a ghost from an
  outdated smoothing cache.
- **INV8 positive (career)**: a synthetic career save whose ledger reconstructs
  to the parsed totals -> zero INV8 finding. Fails if `LedgerGroundTruthDiff`
  wiring is wrong.
- **INV8 ELS-consistency violating (dangling tombstone)**: a model whose
  `Tombstones` reference an `ActionId` absent from the raw `Ledger.Actions` ->
  INV8 FAIL (part (a)). Fails if the model were fed a pre-filtered ELS list,
  which would make this check vacuous (the dangling reference could never appear
  in an already-filtered list); this is the regression BLOCKER 2 guards.
- **INV8 career-diff violating (hard, offline)**: a career save with a funds
  mismatch beyond tolerance and no uplift-clamp -> INV8 WARN (part (b) is
  WARN-capped offline until the headless reconstruction seam is proven). Fails
  if the offline analyzer promotes it to FAIL before the seam exists (the
  FAIL-severity variant is the in-game H5 path only).
- **INV8 report-only**: a per-subject-science facet drift with matching pools
  -> INV8 WARN under default strictness. Fails if report-only divergences are
  wrongly promoted to FAIL and poison harness triage.
- **INV8 non-career**: a Sandbox save -> INV8 part (b) INFO (skipped); part (a)
  ELS consistency still runs. Fails if the ledger rule crashes or FAILs on a null
  `CareerSaveSnapshot`.
- **INV9 positive**: valid rewind ids + present `Parsek/Saves/<id>.sfs` files ->
  zero INV9 findings. Fails if a valid, present rewind save is flagged missing
  (the directory-fix regression: probing `Parsek/RewindPoints/` for a
  `Parsek/Saves/` file).
- **INV9 violating (bad id)**: a rewind id containing `..` -> INV9 FAIL via
  `RecordingPaths.ValidateRecordingId`, and the analyzer never opens the escaped
  path. Fails if path-traversal ids reach the filesystem (security regression).
- **INV9 dangling (missing rewind save)**: a recording referencing an absent
  `Parsek/Saves/<id>.sfs` -> INV9 WARN, NOT FAIL. A missing rewind save is a
  dangling reference, often benign (shared rewind saves deleted with a discarded
  sibling; sealed recordings cannot rewind), so it must not red the run. Fails if
  it FAILs (the s15 / orbital-supply-route class) or is silently dropped.
- **INV9 orphan inventory**: an unreferenced `parsek_rw_*.sfs` on disk -> one
  per-save INFO count line (never WARN/FAIL). Fails if the orphan scan reverts to
  `Parsek/RewindPoints/` (false-flagging live rp_* RewindPoints) or promotes the
  benign orphan to WARN.
- **INV10 positive**: every builder recording round-trips at
  `RecordingTreeRecordCodec` + `RecordingManifestCodec` + `TrajectorySidecarBinary`
  -> zero INV10 FAIL. Fails if a codec silently drops a field on save or load.
- **INV10 violating**: a recording with a field the codec is known to mishandle
  (constructed to differ post-round-trip) -> INV10 FAIL naming the field. Fails
  if the round-trip comparison is too shallow to notice the drift.
- **INV10 NaN stability**: an `OrbitSegment` with a NaN element round-trips
  equal (NaN-aware equality) -> no FAIL. Fails if naive `==` flags stable NaN,
  producing permanent noise on predicted-tail segments.

### Verdict-level policy tests

- **FAIL fails the run**: a model with one INV1 FAIL -> `Counts.Fail == 1` and
  the CI entry point asserts red. Fails if FAIL is downgraded and CI goes green
  on corruption.
- **WARN does not fail**: a model with only INV3-ABSOLUTE-RANGE WARNs ->
  `Counts.Fail == 0`, run green, WARNs present in the report. Fails if WARN is
  wrongly escalated and blocks unrelated PRs.
- **INFO never fails**: a model with only manifest-absent INFO -> green, INFO in
  report. Fails if inventory INFO is treated as a finding.
- **STALE-FIXTURE distinct from FAIL**: a stamped corpus at the wrong generation
  -> `Counts.StaleFixture > 0`, `Counts.Fail == 0` (findings are STALE, not
  FAIL), CI red with the STALE reason. Fails if a stale corpus reads as a code
  bug (misdirected triage) or as green (silent staleness).

### Malformed-input robustness tests

- **Truncated `.prec`**: analyzer produces a LoadFault + INV5 FAIL, no
  exception escapes `Analyzer.Run`. Fails if a corrupt sidecar crashes the tool
  (ties into F-series fault scenarios; a crash instead of a finding is itself a
  bug).
- **Text / pre-reset sidecar**: LoadFault with the exact production reason
  string, no crash. Fails if the analyzer diverges from the production reject
  path.
- **Corrupt `.sfs` (unbalanced braces)**: single `sfs` LoadFault + empty model,
  no crash, and the full Evaluate pipeline turns that fault into a `LOADER-FAULT`
  FAIL so the report is RED (`IsRed`), not silently green. Fails if a malformed
  save takes down triage, or if a corrupt sfs analyzes green.
- **tree-node / ledger LoadFault**: a throwing RECORDING tree record or an
  unparsable `ledger.pgld` each yield a `LOADER-FAULT` FAIL (red run). Fails if
  either kind is dropped and analyzes green (the same blocker as the corrupt sfs).
- **Missing snapshot for INV4**: INV4 downgrades to INFO + a snapshot-load WARN,
  no crash. Fails if a missing snapshot NREs the PID rule.
- **Empty / non-Parsek save**: an EMPTY report (zero findings, zero FAIL), no
  crash; the header zeros + loader summary carry the "analyzed, saw nothing"
  signal (edge case 21). Fails if the analyzer requires a Parsek footprint and
  errors on a vanilla save, or emits a spurious finding on a clean empty save.

### Report format stability tests

- **Byte-identical determinism**: analyzing the same model twice produces
  byte-identical `.analysis.json` (findings sorted, no timestamps, no absolute
  paths, InvariantCulture floats). Fails if dictionary-iteration order or a
  wall-clock field leaks in, which would make report diffing in CI useless.
- **Sort order pinned**: a model with mixed levels serializes findings sorted by
  (level desc, ruleId, target, sectionIndex). Fails if a sort regression
  reorders findings and every downstream diff shows spurious churn.
- **AnalyzerVersion frozen**: a golden `.analysis.json` fixture is asserted
  field-for-field; changing the schema without bumping `AnalyzerVersion` fails
  this test. Fails if the output contract silently changes under a consumer
  (the harness parser).
- **Human summary contract**: the `.txt` header line matches
  `[Analyzer] save=... generation=... FAIL=.. WARN=.. INFO=.. STALE=..` and each
  finding line matches `<LEVEL> <ruleId> target=...`. Fails if a grep-based
  triage script breaks on a format drift.

### Core-purity test (H5 readiness)

- **No file I/O in rules**: a test constructs an `AnalyzerModel` entirely
  in-memory (no loader, no disk) and runs every rule -> findings computed. Fails
  if any rule reaches for a file path or `Stream`, which would block the future
  in-game RecordingInvariants category (M-A3/H5) from reusing the core.

### CitedContract-presence test

- **Every rule cites a contract**: reflection over all `IRecordingInvariant`
  implementations asserts a non-empty `CitedContract`. Fails if a new rule ships
  without naming the production member it checks, defeating the review gate that
  keeps wrong contracts out (plan section 7: "wrong contracts die in review").

## Open Questions and Deferred Review NITs

The INV8 part (b) headless-reconstruction seam is the one open question tracked
inline (see the INV8 bullet's "OPEN QUESTION"): the offline career diff stays
WARN-capped and reconstruction-not-available INFO until a Unity-free recalc seam
plus a hardcoded facility-max-levels map are proven; the FAIL-severity variant
lives on the in-game H5 path.

The following smaller items were raised in review and CONSCIOUSLY DEFERRED (not
fixed in this round). They are tracked here so they are not lost:

- **Cycle-noise (INV7 parent/supersede cycles report one node, not the edge
  set)**. A parent or supersede cycle emits a single FAIL at the node that closes
  the loop and marks the whole visited path reported. That is enough to fail the
  run and name a member, but the message does not enumerate the full cycle edge
  set, so triage must walk the tree by hand to see every participant. Deferred:
  the single finding is a correct red signal; richer cycle reporting is polish.
- **Second out-edge supersede cycles**. `CheckSupersedeCycles` follows only the
  FIRST `Old -> New` edge per node (`if (!next.ContainsKey(...)) next[old] = new`),
  so a corrupt row set giving a node two distinct out-edges only has its first
  edge walked; a cycle reachable exclusively through the second out-edge is not
  detected. Deferred: the supersede graph is near-functional in practice (one
  merge target per old id); multi-out-edge corruption is out of scope for v1.
- **Double-report of an invalid recording id**. An id that fails
  `ValidateRecordingId` can surface both as a loader `trajectory`
  `invalid-recording-id` LoadFault (via INV5) and as an INV9 `badid` FAIL. Both
  are FAIL and both are correct, but the same root cause is counted twice.
  Deferred: over-reporting a real corruption is safe; de-duping across rules
  needs a shared id-validity pass that is not worth the coupling yet.
- **`BracesBalanced` value-brace false positive**. The corruption pre-check
  counts every `{` / `}` in the raw sfs text, including braces that appear inside
  a quoted / free-text VALUE (e.g. a vessel name or note containing a brace).
  Such a value could tip the balance count and mis-flag a structurally valid save
  as `unbalanced-braces`, or mask a real imbalance. Deferred: KSP sfs values
  rarely carry literal braces and the check is a deterministic corruption signal
  for the common case; a brace-aware tokenizer is a larger change.
- **Numbered-sfs API**. The loader only reads `persistent.sfs`; the design's
  "and any numbered `*.sfs` the caller names" (quicksaves / named saves) is not
  wired to a parameter yet. Deferred: the harness and triage operate on
  `persistent.sfs`; a caller-named-sfs overload lands when a run mode needs it.
