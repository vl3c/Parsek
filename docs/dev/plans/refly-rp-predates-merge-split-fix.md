# Re-Fly load preserves post-RP merge tree mutations

## Rationale (chosen approach: Option F = C+B hybrid, splice from in-memory committed tree)

The frozen RP `.sfs` records the tree shape at RP creation time. Any subsequent
`SplitAtSection` (or other tree mutation) writes new `.prec` sidecars and updates
`RecordingStore.CommittedTrees` in memory, but never rewrites the historical RP
`.sfs`. When Re-Fly loads that RP, the loaded `RECORDING_TREE` ConfigNode lists
only the pre-split recording IDs. The post-split halves are present on disk and
in `CommittedTrees` (the live session that initiated Re-Fly is still alive at
that moment) but absent from the loaded tree, so `TryRestoreActiveTreeNode`
calls `RemoveCommittedTreeById` and the missing halves vanish.

The fix splices recordings that exist in the in-memory committed tree but are
absent from the loaded tree into the loaded tree BEFORE it replaces the
committed copy. The committed-tree recordings are deep-cloned (so subsequent
mutations stay isolated), `MarkFilesDirty()` is called so the post-load `.sfs`
will rewrite the merged shape with fresh sidecar epochs, and a structured log
line reports the splice.

Rejected alternatives:

- **Option A (re-freeze the RP `.sfs` after every commit)**: requires running
  `GamePersistence.SaveGame` from non-flight code paths and possibly
  retrofitting the RP file to a newer game state — too invasive for a fix
  release, plus unbounded I/O on rapid-merge sessions.
- **Option B alone (replay journal)**: would need a full split/supersede
  journal; only `RecordingSupersedeRelation` exists today, and splits aren't
  journaled. Building that is a phase-sized refactor.
- **Option C alone (load orphaned `.prec`)**: would have to scan the recordings
  directory and reconcile — but the in-memory committed tree is already a
  cleaner truth source (avoids "are these `.prec` files for THIS tree?"
  ambiguity).
- **Option D (defer split to post-Re-Fly)**: changes the merge contract; many
  paths assume the split lands at first commit.
- **Option E (lazy split view)**: cross-cutting refactor of timeline / UI /
  playback / spawner.

## What changes

`ParsekScenario.TryRestoreActiveTreeNode` calls a new helper
`SpliceMissingCommittedRecordingsIntoLoadedTree` after sidecar hydration but
before `RemoveCommittedTreeById`. The helper iterates the in-memory committed
tree (looked up via `FindCommittedTreeById`) and, for any recording not present
in the loaded tree, deep-clones it, calls `MarkFilesDirty()` so the next
`OnSave` rewrites the `.sfs`, and adds it via `AddOrReplaceRecording`. Logs:

- `Scenario` `INFO`: `TryRestoreActiveTreeNode: spliced {n} post-RP recording(s) from committed tree '{name}' (id={id}) — pre-splice loaded={a}, committed={b}, after={a+n}` plus a per-recording verbose line listing the spliced IDs.
- Net result line is logged regardless of whether splices occurred (zero is fine, surfaces "we checked").

If a `BranchPoint` in the committed tree references the spliced recording, that
linkage is preserved by the splice (BranchPoints are tree-level state, the
loaded tree carries them as-is from the `.sfs`; if the committed tree has newer
BranchPoint chain links they survive in `CommittedTrees`'s state until
`RemoveCommittedTreeById` runs — but that runs AFTER the splice, on a copy that
already has the missing recordings). When the loaded tree's BranchPoint list
diverges from the committed tree's, the splice ALSO copies any BranchPoint
present in the committed tree but missing in the loaded one, and any
BranchPoint in the loaded tree whose ParentRecordingIds reference an only-in-
committed recording is updated from the committed copy.

## Tests

- `Bug601ReFlyPostMergeSplitPreservation_TreeWithMissingPostSplitRecordings_SplicesFromCommitted` — RP-snapshot tree with 1 recording; committed tree has 3 (the original + 2 post-split). After `TryRestoreActiveTreeNode` the active tree has all 3, the spliced recordings are deep clones (not aliased) and `FilesDirty=true`.
- `Bug601ReFlyPostMergeSplitPreservation_TreeAlreadyMatches_NoSplice` — sanity case, RP snapshot already matches committed; zero splices, log says `0 spliced`.
- `Bug601ReFlyPostMergeSplitPreservation_NoCommittedTree_GracefulNoOp` — loaded tree has no in-memory committed counterpart; helper returns 0 without throwing.
- `Bug601ReFlyPostMergeSplitPreservation_BranchPointSplice_FromCommittedTree` — committed tree has an extra BranchPoint linking the post-split half; after splice that BranchPoint exists in the active tree.
- `Bug601ReFlyPostMergeSplitPreservation_StructuredLogShape` — the `INFO` line matches the spec above.

In-game / playtest replay coverage is in `RuntimeTests.cs` (deferred — gated on
real `GamePersistence` round-trip; see `feedback_unity_test_coverage.md`).
