# Phase 11.5: Recording Storage Optimization Plan

## Status

This plan covers the storage-focused half of Phase 11.5. It is separate from the ghost LOD work
already tracked in `phase-11-5-ghost-lod-implementation.md`.

Original measured findings from archived recordings:

- Total sidecar payload in sampled logs: about `54.6 MB`
- `.prec` trajectory/event sidecars: about `23.9 MB` (`43.7%`)
- `_ghost.craft` snapshots: about `20.6 MB` (`37.7%`)
- `_vessel.craft` snapshots: about `10.2 MB` (`18.6%`)
- For sectioned `.prec` files, point-node duplication averages about `2.1x`
- About `73.5%` of `_vessel.craft` / `_ghost.craft` pairs in the archive are byte-identical

These numbers were large enough to justify format work before logistics routes add more long-lived
recordings.

Implementation status on `fix/phase-11-5-recording-storage`:

- slices `1-5` in this plan have landed
- current-format `.prec` sidecars are now section-authoritative `v1+` and binary/sparse `v3`
- ghost/vessel snapshot alias mode is live for identical snapshot pairs
- fixture, round-trip, mixed-format, and scenario-writer coverage landed with the format work
- slices `6-7` remain deferred
- the next PR after this branch merges should target snapshot-side shrink before revisiting more
  trajectory work

---

## Goal

Reduce recording size on disk without changing visible playback behavior, tree semantics, spawn
behavior, or route analysis inputs.

The first implementation pass should focus on structural waste that is already measured in the
current format:

1. duplicated trajectory/orbit data inside `.prec`
2. duplicated vessel/ghost snapshot files
3. verbose text encoding for trajectory points
4. redundant per-point fields that rarely change

Only after those are addressed should we consider more aggressive trajectory thinning.

---

## Locked Decisions

These decisions are part of this plan and should not be reopened during implementation unless a
blocking bug appears.

- Preserve backward read compatibility with existing `version = 0` `.prec` files.
- Do not attempt a one-shot migration of all sidecars on load.
- Do not change runtime playback contracts in the first pass. `Recording`, `IPlaybackTrajectory`,
  ghost playback, watch mode, spawn logic, and optimizer logic should keep working against the same
  in-memory model.
- Do not start by loosening active-recording sampling thresholds globally.
- Do not mix storage optimization with ghost LOD or unrelated performance refactors.
- Keep the optimization staged: one logical storage concern per commit or PR-sized slice.

---

## Repo-Specific Guardrails

The project `CLAUDE.md` adds implementation constraints that apply to every slice in this plan.

### Logging

Storage work must be logged comprehensively but without spam:

- every save/load format dispatch, alias/dedup decision, cleanup decision, fallback path, guard
  condition skip, and unknown-version rejection must be logged
- use `ParsekLog.Info` / `Warn` for important one-shot storage lifecycle events
- use `ParsekLog.Verbose` for one-shot diagnostic detail during load/save/cleanup
- use `ParsekLog.VerboseRateLimited` for repeated scans or per-frame summaries only
- when iterating over collections of files or recordings, use local counters and emit one summary
  line after the loop instead of per-item logs unless the item count is small and bounded
- do not add per-point or per-node logs inside serialization hot paths

### Testing

Every new logic-bearing helper in this work should be structured for direct testing:

- make pure decision and transform helpers `internal static` where practical
- add unit tests for every new guard, dispatch branch, reconstruction helper, alias decision,
  cleanup rule, and thinning invariant
- add log-assertion tests for important decision paths using `ParsekLog.TestSinkForTesting`
- use `[Collection("Sequential")]` where storage tests touch shared static state
- add in-game tests when the correctness depends on live KSP integration rather than pure file
  transforms, especially for:
  - actual save/load through `ScenarioModule`
  - snapshot alias behavior in a real save
  - mixed-format sidecar load under KSP scene lifecycle

### Per-Commit Hygiene

As slices land, each behavior-changing commit should also check:

- `CHANGELOG.md`
- `docs/dev/todo-and-known-bugs.md`

The docs must stay aligned with the current implementation, not the first draft of the slice.

---

## Current Format Constraints

### What is expensive today

The current serializer writes trajectory data as text `ConfigNode` values with round-trip numeric
formatting:

- `SerializePoint` writes `ut`, `lat`, `lon`, `alt`, `rotX/Y/Z/W`, `body`, `velX/Y/Z`,
  `funds`, `science`, `rep`
- `SerializeTrackSections` writes nested `POINT` or `ORBIT_SEGMENT` nodes again per section
- `SaveRecordingFiles` writes `.prec` as a text sidecar after building an in-memory `ConfigNode`

Relevant code:

- `Source/Parsek/RecordingStore.cs:2493-2510`
- `Source/Parsek/RecordingStore.cs:2864-2923`
- `Source/Parsek/RecordingStore.cs:3470-3516`
- `Source/Parsek/FlightRecorder.cs:5218-5227`

### What the archive shows

The active recorder dual-writes points into:

- the flat `Recording.Points` list
- the current `TrackSection.frames` list

That dual-write is useful in memory today, but on disk it creates measurable duplication:

- top-level `POINT`s hold the flat track
- nested `TRACK_SECTION { POINT ... }` nodes hold the section tracks
- boundary seeding can repeat a boundary point across adjacent sections by design

This means the first on-disk win is structural: avoid writing the same trajectory twice.

---

## Non-Goals For This Pass

Do not mix these into the first storage implementation pass:

- changing the active sampler heuristics in `TrajectoryMath.ShouldRecordPoint`
- changing route semantics or resource manifest meaning
- lazy loading of partial recordings
- replacing `ConfigNode` snapshot storage with a custom vessel snapshot format
- KSP save-file metadata shrink work
- multiplayer `.gloop` packaging

These can follow once the format-level waste is removed.

---

## Target End State

After the staged work in this plan:

- `.prec` stores one authoritative trajectory representation, not two
- new sidecars use a versioned compact format
- the reader supports both legacy `v0` and compact versions
- identical vessel/ghost snapshots are stored once and referenced twice in metadata
- resource/science/funds payload is sparse where possible
- trajectory simplification, if enabled, is post-commit and error-bounded

The in-memory model may remain richer than the on-disk model as long as load reconstructs it
deterministically.

---

## Staging Overview

Implementation is split into seven slices. Slices `1-5` are now implemented on this branch. Slices
`6-7` remain follow-on work gated by the measured results from the shipped trajectory-side changes
and the pending snapshot-size pass.

1. `Shipped` — baseline instrumentation and golden fixtures
2. `Shipped` — `.prec` format `v1`: remove duplicated on-disk flat trajectory/orbit copies
3. `Shipped` — snapshot deduplication for `_vessel.craft` and `_ghost.craft`
4. `Shipped` — `.prec` format `v2`: compact point/event encoding
5. `Shipped` — sparse point payloads and section dictionaries
6. `Deferred` — post-commit trajectory thinning
7. `Deferred` — optional compression and windowed loading

Each shipped slice landed with explicit tests. Remaining slices should still require a fresh
before/after storage measurement before implementation starts.

---

## Slice 1: Baseline Instrumentation And Golden Fixtures

### Goal

Create a safe baseline before format work starts.

### Changes

- Add a storage investigation note under `docs/dev/research/` or extend the diagnostics doc with:
  - representative large files
  - bytes by sidecar type
  - bytes per point-node
  - observed duplication ratios
- Add test fixtures or helper builders for representative recordings:
  - atmospheric active recording with multiple track sections
  - orbital checkpoint recording
  - mixed active/background recording
  - recording with part events and branch/optimizer split boundaries
- Extend the existing test fixture generators and version helpers so the test harness can emit both:
  - legacy text `ConfigNode` sidecars
  - new-version compact sidecars
- Add golden round-trip tests that assert:
  - loaded `Points`, `TrackSections`, `OrbitSegments`, `PartEvents`, `FlagEvents`, `SegmentEvents`
    are semantically equal before and after save/load
  - `RecordingStats` and playback-relevant derived behavior do not change

### Required tests

- `RecordingStoreRoundTripTests` for v0 fixtures
- regression test that boundary-seeded sections still reconstruct the same playback path
- regression test that optimizer split boundaries remain continuous after save/load
- generator-level tests for `RecordingBuilder`, `ScenarioWriter`, and format-version helpers so
  future slices exercise the real fixture path, not only hand-built nodes
- log-assertion tests for load/save format dispatch and fallback logging

### Exit criteria

- We can serialize, load, and compare a representative recording corpus before any format change
- The test suite has explicit storage fixtures to protect subsequent slices

---

## Slice 2: `.prec` Format V1 - Remove Duplicated On-Disk Flat Trajectory Copies

### Goal

Stop writing the same trajectory and orbit data twice.

### Decision

For new-format sidecars, `TrackSections` become the authoritative on-disk source for trajectory
data whenever sections are present. The flat `Points` and `OrbitSegments` lists remain in memory but
become derived on load.

### Write behavior

For `version >= 1`:

- if `TrackSections` exist:
  - write only `TRACK_SECTION` data for points/orbit checkpoints
  - do not write top-level `POINT` nodes
  - do not write top-level `ORBIT_SEGMENT` nodes that are already represented inside sections
- if `TrackSections` do not exist:
  - preserve legacy top-level serialization as a fallback

### Load behavior

For `version >= 1`:

- deserialize `TrackSections` first
- reconstruct `rec.Points` by flattening section frames in UT order
- reconstruct `rec.OrbitSegments` by flattening checkpoint sections in UT order
- deduplicate shared boundary points/segments during reconstruction where needed

### Why this slice comes first

This is the highest-confidence, lowest-risk size reduction:

- it removes measured waste
- it does not require changing active recording
- it keeps the current runtime model intact
- it fits the existing versioned sidecar framework

### Required code work

- bump `CurrentRecordingFormatVersion`
- extend `SerializeTrajectoryInto`
- extend `DeserializeTrajectoryFrom`
- add flattening helpers:
  - `RebuildPointsFromTrackSections`
  - `RebuildOrbitSegmentsFromTrackSections`
- make duplication rules explicit in comments and tests

### Risks

- Double-counting or dropping boundary points when rebuilding `rec.Points`
- Breaking code that assumes top-level `Points` are authoritative during load
- Divergence between section data and reconstructed flat data

### Required tests

- save/load for sectioned recordings produces equivalent reconstructed `Points`
- save/load for checkpoint recordings produces equivalent reconstructed `OrbitSegments`
- boundary-seeded sections do not create extra playback kinks after flattening
- mixed legacy/new corpus load works in the same process

### Exit criteria

- New files with sections shrink materially
- Legacy files still load unchanged
- Playback and spawn tests remain green

---

## Slice 3: Snapshot Deduplication For `_vessel.craft` And `_ghost.craft`

### Goal

Eliminate duplicate snapshot files when ghost and vessel snapshots are byte-identical.

### Decision

Do not use filesystem hardlinks or symlinks. Store one canonical snapshot file and add additive
metadata telling the loader whether the ghost snapshot is:

- absent
- separate
- aliased to the vessel snapshot

This avoids platform-specific filesystem behavior and keeps deletion logic simple.

### Write behavior

When both snapshots are present:

- serialize both to temporary `ConfigNode`s or strings
- if contents are identical:
  - write only `_vessel.craft`
  - do not write `_ghost.craft`
  - write metadata flag `ghostSnapshotMode = AliasVessel`
  - if a stale `_ghost.craft` from an earlier `Separate` save already exists, delete it during the
    same save path so alias mode produces a real on-disk win
- if contents differ:
  - write both files
  - write metadata flag `ghostSnapshotMode = Separate`

When only ghost snapshot exists, keep existing behavior unless there is a reason to alias to a
missing vessel snapshot.

### Load behavior

- `AliasVessel` means `GhostVisualSnapshot = VesselSnapshot.CreateCopy()` or equivalent safe reuse
- legacy files with no mode flag keep existing load logic

### Required code work

- add additive snapshot-mode metadata in recording metadata save/load paths
- centralize snapshot comparison logic
- keep delete/orphan cleanup rules compatible with alias mode
- teach diagnostics/storage breakdown helpers that alias mode does not require a physical ghost file
- make sure storage measurements count alias-mode recordings correctly

### Risks

- Incorrect aliasing if serialization order is nondeterministic
- Loader accidentally sharing mutable `ConfigNode` instances
- Orphan cleanup deleting the canonical file while alias metadata still points to it

### Required tests

- identical vessel/ghost snapshots write one file and load two equivalent in-memory snapshots
- differing snapshots still write and load separately
- switching from `Separate` to `AliasVessel` deletes a stale on-disk `_ghost.craft`
- delete/cleanup paths remove the right files in alias and separate modes
- diagnostics/storage scan does not warn about a missing ghost file in alias mode
- legacy recordings with both files still load

### Exit criteria

- Identical snapshot pairs no longer cost two files
- Cleanup and load semantics are stable

---

## Slice 4: `.prec` Format V2 - Compact Point/Event Encoding

### Goal

Replace verbose text `ConfigNode` point/event storage with a compact versioned binary payload while
keeping the same runtime semantics.

### Decision

Use a new sidecar version and keep the `.prec` extension if convenient, but the file contents for
the new version can be binary. The reader cannot rely on `ConfigNode.Load` for version detection in
that case, so this slice must introduce an explicit dispatch envelope:

- legacy text files keep the current `ConfigNode` text format
- binary files begin with a small magic header such as `PRKB` plus a format version
- `LoadRecordingFiles` sniffs the first bytes and dispatches to the legacy text path or the binary
  reader before normal deserialization begins

The reader branches by detected header/version, not by extension alone.

The binary format should be simple and explicit, not clever:

- section-oriented chunks
- small field headers
- integerized coordinates and deltas
- string tables for repeated names
- varint/ZigZag style integer packing

### Initial encoding rules

Per section:

- `ut`: delta from section start in fixed ticks or milliseconds
- `lat/lon/alt`:
  - absolute sections: quantized fixed-point integers
  - relative sections: quantized `dx/dy/dz`
- `rotation`: compressed quaternion
- `velocity`: quantized vector
- `bodyName`: string-table index
- career values: sparse or section-level defaults if unchanged

Events:

- part names and body names go through string tables
- enum values stored as bytes or varints
- UT stored as section-relative or recording-relative deltas

### Explicit non-goals for v2

- No adaptive entropy codec
- No complicated cross-file dictionaries
- No background recompression thread

### Required code work

- add a binary read/write path alongside the legacy `ConfigNode` path
- add a header-sniff dispatch step before `ConfigNode.Load`
- update `CurrentRecordingFormatVersion` and format-version tests accordingly
- extend fixture generators such as `RecordingBuilder` / `ScenarioWriter` so the test harness can
  emit both legacy and compact sidecars

### Risks

- Quantization error affecting playback interpolation or spawn positions
- Reader/writer bugs across multiple section reference frames
- Debuggability loss if the format becomes opaque too early
- Mixed-format tests giving false confidence if fixture generators still only emit legacy text

### Required tests

- binary round-trip equality within declared tolerances
- max position/rotation/velocity error tests
- mixed section types load correctly
- spawn-critical end-point values stay within safe tolerances
- v0 and v1 files still load alongside v2 files
- fixture generators and format-version tests exercise the real mixed-format write/load path

### Exit criteria

- New sidecars are materially smaller than v1
- Playback tests pass under declared quantization bounds

---

## Slice 5: Sparse Point Payloads And Section Dictionaries

### Goal

Stop paying per-point cost for fields that are constant or rarely change.

### Candidates

- `bodyName`: store once per section where possible
- career values:
  - section default + sparse overrides only when changed
- constant or near-constant velocity components in long stable intervals
- repeated part names/body names through section or file dictionaries

### Decision

This slice is still format work, not behavioral work. It should preserve the same effective
trajectory after load.

### Required tests

- sparse career values reconstruct exactly
- section-level body storage reconstructs equivalent per-point bodies
- mixed sparse and explicit payloads interoperate

### Exit criteria

- Measured bytes per point drop further without touching sampling policy

---

## Slice 6: Post-Commit Trajectory Thinning

### Goal

Reduce point count after commit using error-bounded simplification rather than weaker live sampling.

### Decision

Do not thin during active recording. Thin after commit, before sidecar write, with strict keep rules.

### Keep rules

Always keep:

- first and last point of each recording
- first and last point of each `TrackSection`
- section boundary seed points
- points adjacent to part/flag/segment events
- points where `funds`, `science`, or `reputation` changes relative to the previous surviving point
- points required for spawn walkback / terminal-state fidelity
- points at body or reference-frame transitions

Candidates for removal:

- straight low-curvature segments
- long stationary holds
- dense low-value hover jitter

### Algorithm options

Primary:

- per-section Douglas-Peucker or Visvalingam-style simplification with domain-specific thresholds

Additional simple pass:

- stationary hold collapse using position, altitude, and rotation thresholds

### Industry analog

This matches what mapping/GIS and replay systems usually do: error-bounded polyline
simplification after capture, not just heuristic live sampling.

### Risks

- Removing points that matter to spawn collision walkback
- Over-thinning atmospheric segments with high local curvature
- Breaking optimizer split continuity
- Breaking resource replay timing by removing resource-change points
- Corrupting `LastAppliedResourceIndex` unless it is rebased through an old→new index map

### Additional invariants

The thinning pass must explicitly preserve the point-indexed resource replay contract:

- `LastAppliedResourceIndex` is a persisted index into `Recording.Points`
- replay timing currently keys off the UT of surviving points

Therefore Slice 6 must do both of the following:

1. preserve every point that carries a resource state change, and
2. rebase `LastAppliedResourceIndex` through an old→new point index map after thinning

If implementation shows that rebasing is too fragile, the slice should be cut back rather than
weakening the invariant.

### Required tests

- simplification never removes required keep-rule points
- walkback still finds the same or equivalent non-overlap positions
- max spatial and angular error stays below declared thresholds
- long stationary segments collapse as expected
- `LastAppliedResourceIndex` is rebased correctly after thinning
- resource replay still occurs at the same effective UT for recordings with resource deltas

### Exit criteria

- Point count drops on representative files without visible regressions

---

## Slice 7: Optional Compression And Windowed Loading

### Goal

Take the remaining low-risk wins after structural and format work is done.

### Compression

If the compact binary format is still large enough to matter:

- add optional whole-file compression for new versions
- keep it versioned and transparent to the reader

This should be measured against the binary format first; compression may no longer be worth the
complexity.

### Windowed loading

If memory, not disk, becomes the next bottleneck:

- load trajectory payloads lazily for recordings outside the active playback window
- keep metadata eagerly loaded

This is deliberately last because it changes runtime data residency instead of pure storage.

---

## Migration Strategy

### Reader policy

- Always support reading legacy `v0`
- Add support for each new version incrementally
- Fail clearly on unknown future versions

### Writer policy

- New saves write the latest format version
- Legacy sidecars are not rewritten on load
- Rewrites happen only when a recording becomes dirty or is explicitly re-saved

### Safety policy

- Version changes must not bypass sidecar epoch validation
- Unknown-format files must degrade safely and loudly
- Old recordings must stay playable even if they are never rewritten

---

## Test Strategy

Minimum suite after each slice:

- targeted `RecordingStore` round-trip tests
- playback interpolation tests
- spawn/walkback tests
- optimizer split/merge tests
- background/on-rails checkpoint tests
- any diagnostics tests touching storage breakdown
- log-assertion tests for the new decision paths introduced by the slice

New test categories to add:

- mixed-format corpus loading in one session
- per-version golden fixtures
- exact boundary reconstruction tests
- snapshot alias/dedup tests
- quantization tolerance tests
- fixture-generator coverage for both text and binary sidecars
- in-game save/load verification where pure unit tests cannot prove the KSP integration path

---

## Rollout Order

Recommended implementation order:

1. Slice 1
2. Slice 2
3. Slice 3
4. Measure again
5. Slice 4
6. Measure again
7. Decide whether Slice 5 or Slice 6 is more valuable
8. Leave Slice 7 for later unless measurement says otherwise

This ordering intentionally harvests the obvious structural waste before any more invasive point
encoding or thinning work.

---

## Risks To Watch Closely

- Flat-list reconstruction from section data can silently drift from current playback assumptions
- Boundary points can be duplicated or dropped if flattening is naive
- Snapshot alias mode can complicate deletion/orphan cleanup
- Binary point quantization can break spawn or orbital accuracy if bounds are chosen poorly
- Trajectory thinning can damage walkback and terminal-state fidelity if applied too early

The implementation should prefer explicit invariants and golden fixtures over clever compression.

---

## Recommended First Wave

If implementation starts immediately, the first wave should be:

1. add fixtures and baseline measurements
2. remove duplicated on-disk trajectory/orbit copies
3. deduplicate identical vessel/ghost snapshots
4. re-measure before touching point quantization or thinning

That sequence is the best balance of impact, risk, and reviewability.
