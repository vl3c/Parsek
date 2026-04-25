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

## PR #575 P1 review follow-up — same-ID recording refresh

Reviewer flagged a real correctness gap in the initial splice
implementation: the loop only imported recordings whose IDs were absent
from the loaded tree (`if (loaded.Recordings.ContainsKey(recId)) continue;`).
But `SplitAtSection` mutates the *original* recording in place — it
truncates the trajectory, moves the terminal payload to the new second
half, and reassigns `ChildBranchPointId` to the second half — before
adding the second half to the tree. Skipping same-ID recordings left the
loaded tree internally inconsistent: the original recording kept its
pre-split full trajectory + the old `ChildBranchPointId` link while the
BP-update branch (already present in the splice) overwrote the parent
BP's `ParentRecordingIds` to name the new second half. The original
recording's `ChildBranchPointId` then pointed at a BP whose parent list
no longer named it.

**Decision (Option A — refresh same-ID recordings on divergence):**
when a committed recording's ID matches a loaded recording's ID, detect
divergence on split-relevant structural fields (Points count, last-point
UT, OrbitSegments count, TrackSections count, `ChildBranchPointId`,
`TerminalStateValue`, `TerminalOrbitBody`) and, on divergence, refresh
the loaded copy from the committed copy. The refresh path mirrors
`RestoreCommittedSidecarPayloadIntoActiveTreeRecording` (the sister fix
for hydration-failed records): it overwrites trajectory + terminal-state
+ child-link fields via `ApplyPersistenceArtifactsFrom` + an explicit
field-set, while preserving identity (RecordingId, TreeId, TreeOrder,
MergeState, CreatingSessionId, supersede/provisional refs). The
refreshed record is marked `FilesDirty` so the next `OnSave` rewrites
the `.sfs` + `.prec` with the post-split shape.

The active recording (passed via the new `activeRecordingId` parameter,
forwarded from `tree.ActiveRecordingId` at the call site in
`TryRestoreActiveTreeNode`) is excluded from the refresh path — its
in-memory state is being live-updated by the recorder during Re-Fly,
and clobbering it with the committed snapshot would lose the new flight
data.

Why Option A over the other candidates the prompt offered:

- **Option B (always refresh, no divergence check)** would mark every
  same-ID record dirty even when the trees match exactly, forcing
  unnecessary `.sfs`/`.prec` rewrites on every benign Re-Fly.
- **Option C (refresh without divergence detection but still always
  overwrite the trajectory)** has the same churn cost as B; the
  divergence check is cheap (5 integer compares + 3 string compares +
  1 nullable-enum compare) and meaningfully reduces dirty churn.
- Option A matches what the BP-update branch already does (compare
  `ParentRecordingIds` / `ChildRecordingIds` for divergence, only
  rewrite on mismatch), so the splice now applies one consistent
  "compare-then-overwrite" pattern across both recordings and BPs.

The structured `[Scenario][INFO]` log line gains a `refreshedRecordings`
field, and the verbose ID list now distinguishes `splicedIds=[…]` from
`refreshedIds=[…]`.

## Tests

- `Splice_TreeWithMissingPostSplitRecordings_PullsThemFromCommitted` — RP-snapshot tree with 2 recordings; committed tree has 4 (originals truncated + 2 post-split exo halves). After splice the active tree has all 4, the spliced recordings are deep clones (not aliased), and the same-ID first halves are refreshed from the committed truncated state.
- `Splice_TreeAlreadyMatches_NoSplice_LogsAlreadyInSync` — sanity case, RP snapshot already matches committed; zero splices, zero refreshes, log says `already in sync`.
- `Splice_NoCommittedTreeForId_GracefulNoOp` — loaded tree has no in-memory committed counterpart; helper returns 0 without throwing.
- `Splice_BranchPointOnlyInCommitted_ClonedIntoLoaded` — committed tree has an extra BranchPoint linking the post-split half; after splice that BranchPoint exists in the active tree.
- `Splice_StructuredLogShape_MatchesContract` — the `INFO` line includes `splicedRecordings=`, `refreshedRecordings=`, `splicedBranchPoints=`, `updatedBranchPoints=`, and `source=committed-tree-in-memory` fields.
- `Splice_TreeWithStaleFirstHalfAfterSplit_RefreshesFirstHalfFromCommitted` (P1 review): loaded has the pre-split full recording (full UT range, ChildBranchPointId pointing at the OLD BP) and committed has the post-split truncated recording (same id, atmo-only UT range, new ChildBranchPointId). Asserts the loaded recording's UT range, point count, and `ChildBranchPointId` match the committed's after splice.
- `Splice_RefreshDoesNotClobberActiveRecording` (P1 review): an `activeRecordingId` is passed to the splice; the active recording's in-memory state must be untouched even when the committed copy diverges.

In-game / playtest replay coverage is in `RuntimeTests.cs` (deferred — gated on
real `GamePersistence` round-trip; see `feedback_unity_test_coverage.md`).
