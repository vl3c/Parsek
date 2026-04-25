# Refactor-4 Pass 2 - Storage and Sidecar Owner Proposal

**Date:** 2026-04-25.
**Worktree:** `Parsek-refactor-4-pass2-sidecar-load`, branch
`refactor-4-pass2-sidecar-load`.
**Base:** stacked on PR #554's `refactor-4-pass2-sidecar-save` branch; PR #554
itself is intentionally left unmerged for the later 0.9.1 batch.
**Status:** Proposal plus implementation checkpoints. The
`SidecarFileCommitBatch`, save-path `RecordingSidecarStore`, and load-path
`RecordingSidecarStore` slices are complete; codec and tree-record moves remain
proposal-only until a separate approval.

## Guardrails

- Zero logic changes still applies. This proposal is not approval to move code.
- Any Pass 3 implementation must be discussed before it starts.
- Keep on-disk schemas, binary layouts, text key names, fallback policy,
  logging text, exception behavior, and test-facing wrappers stable unless a
  later explicit design says otherwise.
- `RecordingStore` should remain the compatibility facade during the first
  implementation slice. Existing production and test call sites should not be
  forced to move while ownership is being carved out.

## Current Ownership Map

| Area | Current owner | Notes |
| --- | --- | --- |
| Recording/tree store state | `RecordingStore` | Static committed/pending state, grouping, optimization, rewind, deletion, cleanup, and sidecar I/O live together. This is why direct cross-file movement is risky. |
| Save/load sidecar orchestration | `RecordingStore` | `SaveRecordingFiles`, `LoadRecordingFiles`, path resolution, sidecar epoch bump/validation, readable mirror reconciliation, snapshot fallback, and staged multi-file commits are in one region. |
| Trajectory text codec | `RecordingStore` | `SerializeTrajectoryInto`, `DeserializeTrajectoryFrom`, point/orbit/part/flag/segment/track-section text serialization, format-version gates, and flat/section fallback repair all live here. |
| Trajectory binary codec | `TrajectorySidecarBinary` | Already a focused binary codec for `.prec` v2-v6 bytes, string tables, sparse point lists, and section payloads. It still calls back to `RecordingStore` for policy helpers and version constants. |
| Snapshot binary/text codec | `SnapshotSidecarCodec` | Already a focused snapshot codec: binary magic/header, Deflate payload, CRC, legacy text fallback, and payload limits. `RecordingStore` wraps it for logging and snapshot-mode policy. |
| `.sfs` recording metadata | `RecordingTree` | `SaveRecordingInto`/`LoadRecordingFrom` own recording metadata, linkage, loop settings, mutable playback state, manifests, and legacy defaulting. They call `RecordingStore` for manifest codecs and ghost snapshot mode. |
| Paths and atomic primitive writes | `RecordingPaths`, `FileIOUtils` | These are already separate. `RecordingStore` still owns multi-file transaction staging and rollback. |

## Dependency Map

```text
RecordingTree
  -> RecordingStore.GetExpectedGhostSnapshotMode
  -> RecordingStore.ParseGhostSnapshotMode
  -> RecordingStore.Serialize/Deserialize*Manifest
  -> RecordingStore.BumpLegacyMergeStateMigrationCounterForTesting

RecordingStore sidecar region
  -> RecordingPaths
  -> FileIOUtils
  -> TrajectorySidecarBinary
  -> SnapshotSidecarCodec
  -> ParsekFlight terminal-orbit helpers
  -> RecordingEndpointResolver
  -> GhostPlaybackEngine loop migration helpers
  -> ParsekSettings writeReadableSidecarMirrors

TrajectorySidecarBinary
  -> RecordingStore version constants and section-authoritative policy
  -> FileIOUtils.SafeWriteBytes

SnapshotSidecarCodec
  -> FileIOUtils.SafeWriteBytes
```

The important constraint is that sidecar hydration is not a pure codec call.
It also runs loop migration, degenerate loop repair, terminal-orbit backfill,
endpoint backfill, snapshot policy, and failure flagging. Those policy steps
must either stay in `RecordingStore` or move as one visible orchestration owner;
they should not be hidden inside low-level codec classes.

## Proposed Owners

### 1. `SidecarFileCommitBatch`

Smallest safe first owner. It would take the staged multi-file write/delete
helpers out of `RecordingStore`:

- `StagedSidecarChange`
- `CommittedSidecarChange`
- `StageSidecarWrite`
- `ApplyStagedSidecarChanges`
- `RestoreCommittedSidecarChange`
- `CleanupStagedSidecarArtifacts`
- `CleanupCommittedSidecarBackups`
- `DeleteTransientSidecarArtifact`

Recommended shape: an internal helper in a new file with a narrow API for
staging writes and deletes, applying them, and cleaning artifacts. Keep the log
tag and exception behavior identical. This owner is mechanical and does not
alter schema, save/load policy, epoch rules, or recording state.

### 2. `RecordingSidecarStore`

Second owner, after the commit batch has landed. It would own the sidecar
orchestration facade behind existing `RecordingStore` wrappers:

- `SaveRecordingFiles` and `SaveRecordingFilesToPathsInternal`
- `LoadRecordingFiles` and `LoadRecordingFilesFromPathsInternal`
- sidecar load failure marking/clearing
- sidecar epoch validation and bump placement
- readable mirror reconciliation
- snapshot sidecar load summary/fallback policy
- trajectory sidecar dispatch wrappers

`RecordingStore` should keep delegating methods with the current names during
the first split. That avoids broad test churn and keeps blame readable.

Risk: this owner touches policy, not just file I/O. It must preserve the exact
load sequence:

1. probe trajectory sidecar
2. reject unsupported/id-mismatched/stale trajectory files
3. deserialize trajectory
4. run loop migration and degenerate loop normalization
5. populate terminal orbit if needed
6. backfill endpoint decision
7. load snapshot sidecars
8. report failure without losing hydrated trajectory state

Implementation should split this owner into at least two commits: save-path
orchestration first, then load-path orchestration. Do not start this step until
the save/load split has been reviewed again. Sidecar epoch mutation and
`FilesDirty` assignment remain owned by the sidecar orchestration layer, not by
the low-level codecs. During the wrapper phase, existing `RecordingStore`
methods still expose these calls, but the mutation order must stay exactly
where the save/load sequence currently performs it.

### 3. `TrajectoryTextSidecarCodec`

Third owner. It would move text ConfigNode trajectory serialization out of
`RecordingStore` while leaving `TrajectorySidecarBinary` focused on binary
bytes:

- `SerializeTrajectoryInto`
- `DeserializeTrajectoryFrom`
- point/orbit/part/flag/segment-event serializers
- track-section text serialization
- flat trajectory vs section-authoritative sync/fallback helpers
- text format version probing

Keep `RecordingStore.SerializeTrajectoryInto` and
`RecordingStore.DeserializeTrajectoryFrom` as wrappers until all callers are
intentionally migrated. This split is valuable, but broader than the commit
batch because many tests and generators call the current wrappers.

Rejected for this pass: merging text and binary trajectory codecs into one new
abstraction. The existing binary owner is cohesive; forcing a shared interface
now would add design risk without reducing behavior risk.

Revisit after `RecordingSidecarStore` and `RecordingManifestCodec` land. At
that point a narrow `ITrajectorySidecarFormat` dispatcher may be cheaper because
sidecar orchestration will be the only trajectory sidecar caller.

### 4. `RecordingManifestCodec`

Fourth owner. It would move the manifest-like ConfigNode codecs currently
stored in `RecordingStore`:

- crew end states
- resource manifest
- inventory manifest
- crew manifest

These codecs are used by both `RecordingTree` and `ParsekScenario` standalone
metadata paths. Keep wrappers on `RecordingStore` first, then migrate call sites
only after tests confirm identical nodes and log output.

### 5. `RecordingTreeRecordCodec`

Later owner, not first. It would move record-level `.sfs` metadata serialization
out of `RecordingTree`:

- `SaveRecordingInto`
- `LoadRecordingFrom`
- playback/linkage save/load helpers
- resource/state save/load helpers
- branch point serialization only if a second, separate pass says so

This is useful, but it should wait until manifest ownership is settled because
`SaveRecordingResourceAndState` and `LoadRecordingResourceAndState` currently
bridge tree metadata, manifest codecs, rewind metadata, UI grouping tags, and
legacy merge-state migration.

## RecordingStore Target State

After the proposed owners land, `RecordingStore` should still own recording
store state and store-level operations: committed and pending recordings/trees,
grouping, tree commit/adoption, optimization, deletion/orphan cleanup, rewind
entry points, and compatibility wrappers for older call sites. It should not own
low-level sidecar transactions, trajectory text serialization, snapshot codec
details, or manifest field serialization.

## Rejected In This Pass

- No binary `.prec` format redesign.
- No snapshot sidecar wrapper/header redesign.
- No ConfigNode key renames or field reordering for `.sfs`, `.prec.txt`, or
  snapshot readable mirrors.
- No deduplication that changes exception timing, logging text, fallback order,
  or `FilesDirty` / `SidecarEpoch` mutation order.
- No replacement of `RecordingStore` static state with injected services.
- No migration of KSP path resolution out of `RecordingPaths`.
- No test rewrites just to chase new owner names while wrappers still exist.

## Recommended Pass 3 Order

1. Extract `SidecarFileCommitBatch`. This is the narrowest cross-file move and
   gives the save path a named transaction owner without changing storage
   policy.
2. Extract the save-path half of `RecordingSidecarStore` behind
   `RecordingStore` wrappers. Keep public/internal wrapper signatures stable and
   preserve `RecordingStore` log tags unless explicitly approved otherwise.
3. Extract the load-path half of `RecordingSidecarStore` behind wrappers only
   after separately reviewing sidecar epoch validation, failure flagging,
   hydrated trajectory preservation, and snapshot fallback order.
4. Extract `TrajectoryTextSidecarCodec`, again behind wrappers. Before this
   step, grep `Source/Parsek.Tests/Generators/` for direct `RecordingStore`
   codec calls so the wrapper surface is known up front. Do not merge it
   with `TrajectorySidecarBinary`.
5. Extract `RecordingManifestCodec` behind wrappers.
6. Re-evaluate `RecordingTreeRecordCodec` after the first five steps. Do not
   start it in the same PR as sidecar orchestration.

PR granularity:

- PR 1: `SidecarFileCommitBatch` only.
- PR 2: `RecordingSidecarStore` only, preferably save and load as separate
  commits after an explicit pre-review of the split.
- PR 3: `TrajectoryTextSidecarCodec` and `RecordingManifestCodec` may share a
  PR only if manifest movement is small and wrappers keep the call surface
  stable.
- PR 4: `RecordingTreeRecordCodec` only.

## Validation Scope

Focused unit slices for any sidecar/codec movement:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"
```

Focused unit slices for manifest/tree metadata movement:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~ResourceManifestSerializationTests|FullyQualifiedName~InventoryManifestSerializationTests|FullyQualifiedName~CrewManifestSerializationTests|FullyQualifiedName~RewindLoggingTests|FullyQualifiedName~TreeCommitTests|FullyQualifiedName~BackwardCompatTests"
```

Before a PR:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings
```

When KSP/log locks are clear, also run:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~InjectAllRecordings
```

Runtime canary after sidecar orchestration or codec movement:

- `CurrentFormatTrajectorySidecarsProbeAsBinary` in the in-game test runner.
- A manual save/load or quickload scenario if the implementation moves
  `LoadRecordingFilesFromPathsInternal`, readable mirror reconciliation, or
  snapshot fallback policy.

## Rollback Plan

- Keep `RecordingStore` wrappers until all moved owners are settled. This makes
  each split revertible without changing callers.
- Commit the commit-batch extraction separately from the sidecar orchestration
  extraction.
- Avoid schema changes so rollback is source-only; no save migration or cleanup
  is needed.
- If validation finds a behavior drift, revert the most recent owner split
  rather than patching over it inside the same PR.

## Open Approval Question

Recommended next implementation, if approved: extract only
`SidecarFileCommitBatch` first. It is the smallest architectural move with a
clear owner boundary and the least chance of hidden save/load logic drift.

## Implementation Checkpoint - SidecarFileCommitBatch

Approved first slice completed in this branch:

- Added `Source/Parsek/SidecarFileCommitBatch.cs`.
- Moved only staged sidecar write/delete commit helpers out of
  `RecordingStore`.
- Left save/load orchestration, sidecar epoch mutation, `FilesDirty`,
  readable mirror policy, snapshot fallback, and codec dispatch in
  `RecordingStore`.
- Updated the rollback test to cover `SidecarFileCommitBatch` directly instead
  of reflecting into old private `RecordingStore` helpers.

Validation:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~InjectAllRecordings
```

## Implementation Checkpoint - RecordingSidecarStore Save Path

Approved second slice started from PR #552's branch, merged `origin/main` after
#552 landed, and kept to the save path only:

- Added `Source/Parsek/RecordingSidecarStore.cs`.
- Moved only save-side path resolution, sidecar epoch bump/rollback, staged
  authoritative sidecar writes, readable mirror reconciliation, and
  `FilesDirty` clearing behind the existing `RecordingStore` wrappers.
- Left `LoadRecordingFiles`, `LoadRecordingFilesFromPathsInternal`, trajectory
  probe/id/epoch validation, loop migration and degenerate-loop repair,
  terminal-orbit backfill, endpoint backfill, snapshot fallback/failure policy,
  and sidecar load-failure marking in `RecordingStore`.
- Preserved the `RecordingStore` log tag, `SidecarFileCommitBatch` transaction
  behavior, sidecar epoch mutation order, `GhostSnapshotMode` rollback, and
  `FilesDirty` mutation order.
- Added direct xUnit coverage for
  `RecordingSidecarStore.SaveRecordingFilesToPathsForTesting` while retaining
  the existing `RecordingStore` wrapper tests.

Validation completed:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~InjectAllRecordings
```

Latest post-main-merge run: focused storage slice passed 235 tests, the
non-injection gate passed 8,707 tests, and `InjectAllRecordings` passed 3 tests.

Runtime canaries are not required for this save-only mechanical move, but the
load-path extraction will need the runtime sidecar probe canary before merge.

## Implementation Checkpoint - RecordingSidecarStore Load Path

Approved third slice stacked on PR #554's save-path branch and kept to the load
path only:

- Moved `LoadRecordingFiles`, `LoadRecordingFilesFromPathsForTesting`, sidecar
  load-failure marking/clearing, sidecar epoch validation, snapshot sidecar
  load summary/fallback policy, post-hydration loop repairs, terminal-orbit
  backfill, and endpoint backfill into `RecordingSidecarStore`.
- Kept `RecordingStore` wrappers, `SnapshotSidecarLoadState`, and
  `SnapshotSidecarLoadSummary` stable for existing production and test call
  sites.
- Preserved the trajectory load order exactly: probe, supported/id/epoch gates,
  deserialize, loop migration/degenerate-loop repair, terminal-orbit backfill,
  endpoint backfill, snapshot sidecars, failure flagging.
- Left trajectory and snapshot codec dispatch in the existing codec owners.
- Added direct xUnit coverage for
  `RecordingSidecarStore.LoadRecordingFilesFromPathsForTesting`, including the
  snapshot-failure path that must preserve hydrated trajectory points.

Validation completed:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings
```

Latest run: focused storage slice passed 236 tests and the non-injection gate
passed 8,708 tests. `InjectAllRecordings` is currently blocked because
`KSP.log` and `GameData/Parsek/Plugins/Parsek.dll` are locked by a running KSP
process.

The runtime sidecar probe canary and a manual save/load or quickload canary are
still required before merging this load-path slice.
