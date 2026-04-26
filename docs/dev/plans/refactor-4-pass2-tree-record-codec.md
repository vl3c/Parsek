# Refactor-4 Pass 2 - RecordingTreeRecordCodec Proposal

**Date:** 2026-04-26.
**Worktree:** `Parsek-refactor-4-pass2-tree-record-codec-plan`, branch
`refactor-4-pass2-tree-record-codec-plan`.
**Base:** stacked on the accepted but unmerged
`refactor-4-pass2-manifest-codec` branch for the later 0.9.1 batch.
**Status:** Proposal only. Do not implement the codec extraction until this
plan is reviewed.

## Goal

Create a focused `RecordingTreeRecordCodec` owner for per-record `.sfs`
metadata serialization while keeping `RecordingTree` as the compatibility
facade. The first implementation slice should be record-only. Branch point
serialization, tree-level save/load ordering, and caller migrations stay out of
scope unless a later review explicitly approves them.

## Guardrails

- Zero logic changes. This is a same-order ownership move only.
- Keep `RecordingTree` wrapper signatures stable for production and test call
  sites.
- Preserve every ConfigNode key name, conditional write, parse fallback,
  default value, node order, and log message.
- Preserve tree-level `Save`/`Load` flow: tree header fields, recording sort,
  `AddOrReplaceRecording`, branch point pass, `RebuildBackgroundMap`,
  `NormalizeLegacyRewindSuppressionMarkers`, and one-shot legacy merge-state
  logging stay in `RecordingTree`.
- Do not move trajectory, snapshot, sidecar, or manifest codec ownership in
  this slice.
- Do not migrate direct test call sites away from `RecordingTree` wrappers in
  the first slice.
- Do not use this extraction to change endpoint, loop, grouping, rewind,
  merge-state, sidecar epoch, or snapshot-mode policy.

## Current Surface

The record serialization surface in `RecordingTree` is:

- `SaveRecordingInto`
- `LoadRecordingFrom`
- `SaveRecordingPlaybackAndLinkage`
- `LoadRecordingPlaybackAndLinkage`
- `SaveRecordingResourceAndState`
- `LoadRecordingResourceAndState`

These helpers persist basic record identity, vessel linkage, terminal state,
terminal orbit and position, loop playback, ghost snapshot mode, sidecar epoch,
EVA and chain linkage, segment and location context, rewind metadata, mutable
spawn/playback state, UI grouping tags, controller metadata, debris/ghost flags,
generation, crew end states, resource/inventory/crew manifests, dock target
PID, and merge/provisional state.

Direct test coverage currently calls the `RecordingTree` methods by name in
many classes, including `RecordingFieldExtensionTests`, `TreeCommitTests`,
`LegacyMigrationTests`, `LoopAnchorTests`, `Bug270SidecarEpochTests`,
`RewindLoggingTests`, `RewindSpawnSuppressionTests`,
`ResourceManifestSerializationTests`, `InventoryManifestSerializationTests`,
`CrewManifestSerializationTests`, `KerbalEndStateTests`,
`BackgroundSplitTests`, `GhostOnlyRecordingTests`, and
`TerrainCorrectorTests`. The wrappers are therefore part of the safety plan,
not cleanup debt for this slice.

## Proposed First Implementation Slice

Add `Source/Parsek/RecordingTreeRecordCodec.cs` with internal static methods
that own the record-level bodies:

- `SaveRecordingInto`
- `LoadRecordingFrom`
- `SaveRecordingResourceAndState`
- `LoadRecordingResourceAndState`
- private playback/linkage save/load helpers

Keep these `RecordingTree` methods as thin wrappers with their current
signatures:

- `RecordingTree.SaveRecordingInto`
- `RecordingTree.LoadRecordingFrom`
- `RecordingTree.SaveRecordingResourceAndState`
- `RecordingTree.LoadRecordingResourceAndState`

`RecordingTree.Save` and `RecordingTree.Load` should continue to call the
wrapper names. This keeps stack traces, tests, and review diffs focused on the
mechanical owner move.

## Policy Boundaries

Most record fields are plain serialization, but several lines are policy or
repair-adjacent and need explicit review during implementation:

- `RecordingStore.GetExpectedGhostSnapshotMode(rec)` in save playback/linkage
  must stay in the same write position because it aligns `.sfs` metadata with
  existing sidecars.
- Legacy loop migration in load playback/linkage uses
  `GhostPlaybackEngine.DefaultLoopPlaybackIntervalSeconds`, updates loop start
  and end fields, logs the existing `[Loop]` warning text, and calls
  `RecordingStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration`.
  If moved, the body and call order must be byte-for-byte equivalent.
- `RecordingEndpointResolver.BackfillEndpointDecision(rec,
  "RecordingTree.LoadRecordingFrom")` should remain in the
  `RecordingTree.LoadRecordingFrom` wrapper after codec hydration for the first
  slice. This keeps endpoint repair policy outside the record codec while
  preserving the existing context string and post-load position.
- Legacy group rename from `RecordingStore.LegacyGloopsGroupName` to
  `RecordingStore.GloopsGroupName` must keep setting `rec.FilesDirty = true`
  only when a rename occurs.
- Legacy `committed` bool to `MergeState` migration must keep using
  `RecordingStore.BumpLegacyMergeStateMigrationCounterForTesting()`.
  `RecordingTree.Load` remains responsible for the one-shot
  `RecordingStore.EmitLegacyMergeStateMigrationLogOnce(...)` call.
- Stray committed `SupersedeTargetId` cleanup must keep the same warning tag,
  warning text, condition, and null assignment.

The guiding rule is: low-level serialization can move; repair/order policy
must either stay in `RecordingTree` or be called out explicitly before it moves.

## Explicitly Out Of Scope

- `SaveBranchPointInto` and `LoadBranchPointFrom`.
- Tree-level `Save` and `Load` header fields.
- Recording ordering, branch point ordering, or node ordering.
- Call-site migrations from `RecordingTree` wrappers to the new codec.
- Manifest wrapper deletion from `RecordingStore`.
- New schema fields, key renames, log wording changes, or fallback changes.
- Any behavior change to endpoint backfill, loop migration, sidecar epoch,
  snapshot mode, rewind suppression, merge-state migration, or grouping.

Branch point serialization has its own dense test cluster
(`BranchPointExtensionTests`, `BranchPointRewindPointIdRoundTripTests`,
`MergeEventDetectionTests`, and parts of `RecordingTreeTests`). If it moves,
that should be a separate follow-up proposal and PR.

## PR Granularity

- PR A: this proposal only.
- PR B: `RecordingTreeRecordCodec` record-only extraction behind wrappers,
  after PR A review.
- PR C: optional branch-point codec extraction, only after PR B review and a
  separate approval.
- Post-Pass 2 cleanup: consider direct caller/test migrations and manifest
  wrapper deletion only after the tree-record codec is accepted.

## Validation Plan

For the record-only extraction:

```powershell
dotnet build Source/Parsek/Parsek.csproj
```

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~RecordingFieldExtensionTests|FullyQualifiedName~RecordingTreeTests|FullyQualifiedName~TreeCommitTests|FullyQualifiedName~LegacyMigrationTests|FullyQualifiedName~LoopAnchorTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~RewindLoggingTests|FullyQualifiedName~RewindSpawnSuppressionTests|FullyQualifiedName~ResourceManifestSerializationTests|FullyQualifiedName~InventoryManifestSerializationTests|FullyQualifiedName~CrewManifestSerializationTests|FullyQualifiedName~KerbalEndStateTests|FullyQualifiedName~BackgroundSplitTests|FullyQualifiedName~GhostOnlyRecordingTests|FullyQualifiedName~TerrainCorrectorTests"
```

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings
```

If a later branch-point slice is approved, add:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~BranchPointExtensionTests|FullyQualifiedName~BranchPointRewindPointIdRoundTripTests|FullyQualifiedName~MergeEventDetectionTests|FullyQualifiedName~RecordingTreeTests"
```

No in-game canary is required for this proposal-only PR. The implementation PR
should use the same runtime canary standard as the preceding sidecar/codec
slices if review identifies any load-order or fallback-policy movement.
