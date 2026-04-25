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

### Rejected during P1 follow-up review — initial "skip the active id" decision

The first follow-up shipped a same-ID refresh path that **excluded the
active recording**: the helper accepted an optional `activeRecordingId`,
and when a same-ID committed recording matched that id the refresh was
skipped. The justification at the time was "the recorder is live-updating
the active recording's in-memory state during Re-Fly; clobbering it
would lose new flight data."

The P1 follow-up review rejected that decision. The reviewer's argument
was correct and decisive:

1. The active recording is precisely the one most likely to be the
   stale post-split first half. `SplitAtSection` keeps the original id
   on the truncated first half — so the active first half IS the
   post-split atmo half whose id matches the pre-split recording.
2. The new positive test `Splice_TreeWithStaleFirstHalfAfterSplit_RefreshesFirstHalfFromCommitted`
   modelled the realistic playtest case (rec_capsule_atmo as committed/
   truncated) but called the helper without the active id, so it
   exercised the non-active path that production never used. In
   production `TryRestoreActiveTreeNode` always forwards
   `tree.ActiveRecordingId`, so the active path was never refreshed.
3. The companion test `Splice_RefreshDoesNotClobberActiveRecording`
   asserted the active recording was *untouched* — correct under the
   skip semantics, wrong as a contract.

### Adopted during P1 follow-up review — refresh active too, preserve recorder-owned state

Load-order analysis at splice time:

1. KSP loads RP's `.sfs` (frozen pre-split snapshot).
2. `TryRestoreActiveTreeNode` runs in `OnLoad`. Each recording goes
   through `RecordingTree.LoadRecordingFrom` which sets up the
   structural fields from disk.
3. `SpliceMissingCommittedRecordingsIntoLoadedTree` runs HERE. **At
   this point, the recorder has NOT bound to the active recording yet** —
   there is no in-flight point being appended.
4. Tree is stashed as pending-Limbo via `RecordingStore.StashPendingTree`.
5. KSP `onFlightReady` eventually fires → `RestoreActiveTreeFromPending`
   (the PR #585 fix) runs the recorder rebind.
6. Recorder starts appending new live points.

So the splice runs strictly before recorder rebind, and the active
recording's `Points` / `OrbitSegments` / `TrackSections` are exactly
what the `.sfs` put there — there is **no recorder-owned in-flight
payload state to lose** by overwriting them.

The helper now refreshes the active recording's structural fields from
the committed copy too. The `activeRecordingId` parameter still exists,
but now means *"this id gets the recorder-state-preserving refresh"*
instead of *"this id is skipped"*. The recorder-state-preserving refresh
performs the same `ApplyPersistenceArtifactsFrom` + explicit field-set
overwrite as the full refresh, but snapshots and restores the small
set of `[NonSerialized]` flags that load-time mitigation paths may have
already set on the loaded copy. The set, audited from `Recording.cs`:

| Field | Type | Set by |
|-------|------|--------|
| `FilesDirty` | `bool` | the recorder on every mutation; load-time hydration repair paths after structural healing |
| `SidecarLoadFailed` | `bool` | `LoadRecordingFiles` when stale-epoch / hydration failure detected |
| `SidecarLoadFailureReason` | `string` | same site as `SidecarLoadFailed` |
| `ContinuationBoundaryIndex` | `int` | continuation-rollback bookkeeping (#95) |
| `PreContinuationVesselSnapshot` | `ConfigNode` | continuation-rollback bookkeeping (#95) |
| `PreContinuationGhostSnapshot` | `ConfigNode` | continuation-rollback bookkeeping (#95) |

`FilesDirty` is OR-ed with the freshly-marked-dirty value (the structural
overwrite always marks the record dirty so the next OnSave rewrites
the `.prec`). Every other flag is restored verbatim. Non-active
recordings receive the regular full refresh — they don't need the
preserve-mode because no load-time mitigation path targets them
specifically and the committed copy's `[NonSerialized]` fields are
defaulted by `DeepClone` anyway.

Why this works without the original "lose live state" risk:

- The committed tree is the authoritative post-merge truth for the
  structural payload.
- The recorder hasn't bound yet, so there is no live payload state on
  the loaded copy that doesn't already match the committed copy.
- The only state that load-time code paths CAN have mutated between
  `.sfs` deserialization and the splice is the `[NonSerialized]` flag
  set above, which preserve-mode keeps.

### Decision summary table

- **Option A (rejected initially, adopted now): refresh active too,
  preserve recorder-owned state.** Matches the load-order facts, gives
  the playtest case the structural correctness it needs, and closes
  the only path where production stayed stale.
- **Option B (refresh all, never preserve any flags) — rejected:** would
  wipe load-time mitigation flags on the active recording; downstream
  `.prec` repair paths look at `SidecarLoadFailed` to decide whether
  to repair from a donor.
- **Option C (skip active entirely, what the first follow-up shipped) —
  rejected by P1 review:** active recording is exactly the one most
  likely to need refresh.
- **Option D (refresh active without divergence check, always): rejected**
  for the same reason the original P1 fix rejected always-refresh-non-
  active: it forces unnecessary `.sfs`/`.prec` rewrites on every benign
  Re-Fly.

The structured `[Scenario][INFO]` log line now reports the refresh count
split into the two modes:

```
refreshedRecordings=N (full=N1 recorderStatePreserved=N2)
```

so post-playtest log scans can distinguish the two paths. The verbose
ID list (`refreshedIds=[…]`) lists every refreshed id regardless of mode.

## Tests

- `Splice_TreeWithMissingPostSplitRecordings_PullsThemFromCommitted` — RP-snapshot tree with 2 recordings; committed tree has 4 (originals truncated + 2 post-split exo halves). After splice the active tree has all 4, the spliced recordings are deep clones (not aliased), and the same-ID first halves are refreshed from the committed truncated state.
- `Splice_TreeAlreadyMatches_NoSplice_LogsAlreadyInSync` — sanity case, RP snapshot already matches committed; zero splices, zero refreshes, log says `already in sync`.
- `Splice_NoCommittedTreeForId_GracefulNoOp` — loaded tree has no in-memory committed counterpart; helper returns 0 without throwing.
- `Splice_BranchPointOnlyInCommitted_ClonedIntoLoaded` — committed tree has an extra BranchPoint linking the post-split half; after splice that BranchPoint exists in the active tree.
- `Splice_StructuredLogShape_MatchesContract` — the `INFO` line includes `splicedRecordings=`, `refreshedRecordings=`, the `(full=N1 recorderStatePreserved=N2)` sub-bucket, `splicedBranchPoints=`, `updatedBranchPoints=`, and `source=committed-tree-in-memory` fields.
- `Splice_ActiveStaleFirstHalfAfterSplit_RefreshesAndPreservesRecorderOwnedState` (P1 follow-up #2): rec_R is the active recording AND the same-ID stale first half. Asserts structural fields match committed truncated first half, and pre-set `FilesDirty` / `SidecarLoadFailed` / `SidecarLoadFailureReason` survive the refresh; structured log shows `refreshedRecordings=1 (full=0 recorderStatePreserved=1)`.
- `Splice_NonActiveStaleFirstHalfAfterSplit_RefreshesInFullMode` (P1 follow-up #2): rec_R is NOT the active recording; same divergence; structured log shows `(full=1 recorderStatePreserved=0)`.
- `Splice_RecorderOwnedFlagsPreservedOnActiveRefresh` (P1 follow-up #2): pins the full audited preserve set (`FilesDirty`, `SidecarLoadFailed`, `SidecarLoadFailureReason`, `ContinuationBoundaryIndex`, `PreContinuationVesselSnapshot`, `PreContinuationGhostSnapshot`) — every flag survives the active refresh.
- `Splice_ActiveRecordingAlreadyMatchesCommitted_NoRefreshNoFlagChurn` (P1 follow-up #2): when the active recording already matches the committed copy, the divergence check short-circuits and nothing is rewritten — `FilesDirty` stays clean, the `already in sync` verbose line is emitted.

In-game / playtest replay coverage is in `RuntimeTests.cs` (deferred — gated on
real `GamePersistence` round-trip; see `feedback_unity_test_coverage.md`).
