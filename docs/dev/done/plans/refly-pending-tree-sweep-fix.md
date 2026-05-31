# Re-Fly Pending-Tree Sweep Fix

## Problem

`logs/2026-05-03_0059_newest` captured a Re-Fly/Watch sequence where the
mission tree was made live again after a spawned-vessel restore. The restore
detached the tree from committed storage, so the Recordings window and KSC
diagnostics briefly saw zero committed recordings. The later deferred merge
dialog restored the recordings, but the load-time sweep had already treated
valid pending-tree recording ids and groups as missing.

The important sequence from the log:

1. Re-Fly merge committed tree `Kerbal X` with 13 recordings and wrote a
   supersede relation.
2. Watching ghosts and then re-entering a spawned vessel restored the committed
   tree as the live active tree.
3. `TryTakeCommittedTreeForSpawnedVesselRestore` removed the tree from
   `RecordingStore.CommittedTrees` / `CommittedRecordings`.
4. The next save wrote `0 committed recordings` plus an `ACTIVE` tree.
5. KSC load kept the finalized pending tree and skipped `.sfs` replacement, so
   the tree existed only in `RecordingStore.PendingTree`.
6. `LoadTimeSweep.SweepOrphanSupersedes` used a committed-only
   `RecordingExists` helper and removed the valid supersede row as fully
   orphaned.
7. `GroupHierarchyStore.PruneUnusedHierarchyEntriesFromCommittedRecordings`
   used committed-only groups, so auto-generated group hierarchy could be
   pruned while the only live owner was the pending tree.
8. The deferred merge dialog reattached the tree, creating the appearance of
   recordings being restored with mixed/duplicated grouping state.

## Goals

- Treat pending-tree recordings as known recordings for load-time orphan
  supersede classification.
- Treat pending-tree recording groups as live groups for group hierarchy prune
  on load/save when a pending tree is awaiting merge.
- Keep the fix narrow: do not refactor the Recordings table away from raw
  committed indices in this PR.
- Add regression tests that reproduce the committed-empty/pending-tree-only
  window without requiring KSP runtime.
- Preserve existing cleanup behavior for truly orphaned supersede rows and
  genuinely stale auto-group hierarchy entries.

## Non-Goals

- Do not remove the transient zero-row UI state in this PR. The table still
  indexes `RecordingStore.CommittedRecordings`, and changing that surface is a
  broader UI/data-model migration.
- Do not change merge-dialog policy or Re-Fly commit/discard semantics.
- Do not weaken `CleanOrphanFiles`; it already uses
  `RecordingStore.BuildKnownRecordingIds`, which includes pending-tree ids.

## Implementation Plan

### 1. Add a cleanup/search view for known recordings

Create a small helper in `RecordingStore` that exposes known recording ids and
recording objects across:

- flat committed recordings, and
- the pending tree.

The helper must de-duplicate by `RecordingId`, prefer earlier entries, and
avoid mutating any store state. `BuildKnownRecordingIds` already does this for
sidecar cleanup ids across committed trees too, but that broader disk-safety
view is intentionally not reused here: load-time zombie cleanup removes rows
from the flat committed list and should not have those ids resurrected by the
parallel tree dictionary during supersede orphan classification.

### 2. Fix supersede orphan sweep

Update `LoadTimeSweep.SweepOrphanSupersedes` to build the known id set once
after zombie recording deletion and classify endpoints against that set.

Expected behavior:

- both endpoints in pending tree -> relation is kept;
- one endpoint committed/pending and one missing -> relation is retained with
  the existing warning;
- both endpoints genuinely absent from committed and pending state -> relation
  is removed as before;
- zombie recordings removed earlier in the same sweep still make rows eligible
  for fully orphaned cleanup unless another live/pending owner remains.

### 3. Fix group hierarchy prune live-group input

Update the group hierarchy prune path to collect live group names from the same
known-recording cleanup view, not only `CommittedRecordings`.

The relation-superseded filtering should operate against the cleanup view so a
superseded committed row does not keep a stale group alive, while pending-tree
rows still protect groups that the deferred merge dialog will restore.

The existing valid-Re-Fly-marker prune skip stays in place. This fix covers the
post-merge/post-marker pending-tree window that produced the 2026-05-03 log.

### 4. Tests

Add focused xUnit coverage:

- `LoadTimeSweepTests`: a supersede relation whose endpoints exist only in a
  finalized/limbo pending tree is not removed as fully orphaned.
- `LoadTimeSweepTests`: a genuinely fully orphaned relation is still removed
  after this change.
- `GroupManagementTests`: pending-tree-only groups protect parent hierarchy
  entries when no committed recordings exist.
- `GroupManagementTests`: relation-superseded groups are still pruned when no
  effective committed or pending recording owns them.

### 5. Documentation

Update:

- `CHANGELOG.md` under `0.9.1` bug fixes.
- `docs/dev/todo-and-known-bugs.md` with a done entry for the
  2026-05-03 pending-tree sweep corruption.

## Validation

Run at minimum:

```bash
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "LoadTimeSweepTests|GroupManagementTests"
dotnet build Source/Parsek/Parsek.csproj
```

If the filtered test command is rejected by xUnit filter syntax in this repo,
run the affected test classes individually or run the full
`Source/Parsek.Tests/Parsek.Tests.csproj` suite.

## Review Focus

Ask the review to look only at:

- whether pending-tree ids/groups are included in cleanup decisions without
  hiding real corruption;
- whether de-duplication preserves existing committed-first behavior;
- whether any caller now accidentally counts pending trees where destructive
  cleanup should intentionally ignore them.
