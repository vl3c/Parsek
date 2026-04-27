# Implementation Plan: Fix Chain-Sibling Supersede Bug

## Summary

`EffectiveState.ComputeSessionSuppressedSubtreeInternal` (in `Source/Parsek/EffectiveState.cs`) walks the suppressed-subtree closure forward via `ChildBranchPointId` only. When `RecordingOptimizer.SplitAtSection` (`Source/Parsek/RecordingOptimizer.cs:365`) splits a single live recording at an environment boundary into a `ChainId`-linked HEAD + TIP, the HEAD has the BP-link to the RewindPoint while the TIP carries the `Destroyed` terminal. After re-fly merge, only the marker's origin (the HEAD) and its BP-descendants are added; the TIP is left behind as a stale, orphan, terminal-Destroyed recording shadowing the new re-fly.

Fix: extend the closure builder to chain-expand on every member it adds, including chain siblings sharing both `ChainId` AND `ChainBranch`.

## Existing-code anchor points (read before implementing)

- `Source/Parsek/EffectiveState.cs:523-613` — `ComputeSessionSuppressedSubtreeInternal`. The walk loop sits at lines 566-601. The mixed-parent halt is at lines 588-595. Cache wiring at lines 528-537 / 603-605.
- `Source/Parsek/EffectiveState.cs:259-276` — `IsChainMemberOfUnfinishedFlight`. Establishes the canonical "same chain" predicate: `ChainId` non-empty AND equal AND `ChainBranch` equal. The fix must use the same predicate so chain-siblings on a different `ChainBranch` (parallel ghost-only continuation per `Recording.cs:128`) stay independent.
- `Source/Parsek/EffectiveState.cs:297-334` — `ResolveChainTerminalRecording`. Same dual-key (`ChainId`+`ChainBranch`) precedent and the same scoping (within the owning tree's `Recordings` dict).
- `Source/Parsek/RecordingOptimizer.cs:365-607` — `SplitAtSection`. The HEAD's `ChildBranchPointId` is moved to the TIP at split time (the wiring is in `RecordingStore.cs:2018-2019`, not the optimizer itself). The optimizer also moves `TerminalStateValue` etc. to the second half.
- `Source/Parsek/RecordingStore.cs:1968-2070` — `MergeUntilStable` split caller. `original.ChainId` is freshly assigned if null (1989-1991), `second.ChainId = original.ChainId` (1991), `ChainBranch` is left untouched (split inherits the parent's ChainBranch, which for an env split is 0 by default). `ChildBranchPointId` is moved at line 2018-2019, BP `ParentRecordingIds` is rewritten at 2023-2054.
- `Source/Parsek/SupersedeCommit.cs:108-185` — `AppendRelations` consumes the closure. `Source/Parsek/SupersedeCommit.cs:271-419` — `CommitTombstones` also consumes the same closure. Both are pure consumers; both will benefit from the closure-side fix without local edits.
- `Source/Parsek/MergeJournalOrchestrator.cs:200-208` — `RunMerge` calls `AppendRelations` → `CommitTombstones` with the same `subtree` returned from the closure builder. Single computation per merge run, journal-safe across crash-resume.
- `Source/Parsek/RewindPointReaper.cs` — does NOT consume the suppressed-subtree closure (grep confirms). Only acts on `MergeState`/`RewindPoint` enumeration. Adding ids to the closure does not affect it.
- `Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs` — full closure test fixture pattern (tree install via `AddRecordingWithTreeForTesting`, scenario install with marker). The fix's tests inherit this shape.
- `Source/Parsek.Tests/EffectiveStateTests.cs:1-200` — fixture pattern for `EffectiveState` predicate tests; existing chain-walking tests at lines ~310-590 already build multi-segment chains.
- `Source/Parsek.Tests/SupersedeCommitTests.cs:1-120` — `AppendRelations` end-to-end fixture pattern.

## Step-by-step implementation

### Step 1 — Closure builder change (one method, one helper)

File: `Source/Parsek/EffectiveState.cs`

Modify `ComputeSessionSuppressedSubtreeInternal` (lines 523-613). Two contained changes:

**1a.** Inside the `while (queue.Count > 0)` loop body (currently lines 566-601), AFTER `recById.TryGetValue` resolves `currentRec` (line 569) and BEFORE the `ChildBranchPointId` early-return (line 572), call a new private helper `EnqueueChainSiblings(currentRec, recById, queue, result, ref siblingsAdded)`. Placement BEFORE the `ChildBranchPointId` early-return is deliberate: a HEAD with `ChildBranchPointId = null` (the post-split shape per `RecordingStore.cs:2018-2019`) must still chain-expand.

Re-read of `RecordingStore.cs:2018-2019`:

```
second.ChildBranchPointId = original.ChildBranchPointId;
original.ChildBranchPointId = null;
```

So the HEAD (= `original`, first segment) ends with `ChildBranchPointId = null`, the TIP (= `second`, last segment) keeps it. The marker's `OriginChildRecordingId` was captured at re-fly-time, BEFORE merge-time split, so it points at the recording that became the HEAD after the merge-time `SplitAtSection`. The walk dequeues the HEAD, finds `ChildBranchPointId = null`, currently `continue`s on line 573, and never enqueues the TIP. Confirmed bug.

The helper must run BEFORE the `string.IsNullOrEmpty(currentRec.ChildBranchPointId) continue` line so a member with a null `ChildBranchPointId` still gets its chain siblings enqueued.

**1b.** New private helper added at the bottom of the class, near `LookupBranchPoint` / `HasOutsideParent`:

```
private static void EnqueueChainSiblings(
    Recording rec,
    Dictionary<string, Recording> recById,
    Queue<string> queue,
    HashSet<string> result,
    ref int siblingsAdded)
```

Behavior:

- Early-return if `rec == null` or `string.IsNullOrEmpty(rec.ChainId)`.
- Iterate `recById.Values` (already built once at the top of the closure builder; pass it through). For each `cand`:
  - Skip if `cand == null` or `ReferenceEquals(cand, rec)`.
  - Skip if `string.IsNullOrEmpty(cand.RecordingId)`.
  - Skip if `!string.Equals(cand.ChainId, rec.ChainId, StringComparison.Ordinal)`.
  - Skip if `cand.ChainBranch != rec.ChainBranch` (per `IsChainMemberOfUnfinishedFlight` precedent; chain branches are independent).
  - Skip if `result.Contains(cand.RecordingId)` (idempotent).
  - Add `cand.RecordingId` to `result`. Increment `siblingsAdded`. Enqueue `cand.RecordingId` so the BP walk picks up `cand.ChildBranchPointId` if any (covers edge case: TIP that itself had a `ChildBranchPointId` from before the env split; covers multi-segment chains; covers the symmetric "origin is itself a TIP" hypothetical).

**1c.** Add a `siblingsAdded` counter alongside `mixedParentHalts` and `childrenAdded` (line 563-564). Append `siblingsAdded={siblingsAdded}` to the closure's summary log line at lines 607-609.

**1d.** Call the helper at the top of the dequeue body (before the `ChildBranchPointId` early-return). Do NOT call it inside the `for (int ci = 0; ci < bp.ChildRecordingIds.Count; ci++)` block — children added via the BP walk will run the dequeue once each and trigger their own chain expansion at that point. This keeps each member's chain expansion at exactly one site (the dequeue) and ensures it runs even when a member has no `ChildBranchPointId`.

**1e.** Keep the `recById` dictionary build (lines 542-551) as is — it is the source of truth for the chain scan and is already O(N).

### Step 2 — Cache invalidation: confirm sufficient

The existing cache (line 528: `int storeVersion = RecordingStore.StateVersion`) bumps whenever `CommittedRecordings` mutates — and chain siblings live in the same collection. No change required. Add a short comment in `EnqueueChainSiblings` referencing `CommittedRecordings` so future readers see the invalidation chain.

### Step 3 — `AppendRelations` and `CommitTombstones`: NO change

`SupersedeCommit.AppendRelations` consumes the closure as `IReadOnlyCollection<string>`. Adding ids in the closure builder means `AppendRelations` writes one supersede row per id automatically (the loop at `SupersedeCommit.cs:155-177` is id-blind). `CommitTombstones`'s subtree-set filter likewise picks up actions stamped against any chain-sibling recording id without local edits. The journal `RunMerge` flow (`MergeJournalOrchestrator.cs:200-208`) computes the closure once via `AppendRelations` and threads the same `subtree` to `CommitTombstones`, so crash-recovery resumption sees a consistent set.

Document this in the closure builder's xmldoc with one sentence noting that chain siblings are included so downstream consumers (supersede rows, tombstone scope) cover the full chain.

### Step 4 — Edge-case coverage walkthrough

| Edge case | Handled by |
|---|---|
| Multi-segment chain (3+) | Each enqueued sibling re-runs `EnqueueChainSiblings`. The HashSet dedup prevents revisits. |
| Different `ChainBranch` siblings | Filter `cand.ChainBranch == rec.ChainBranch` excludes them, matching `IsChainMemberOfUnfinishedFlight` contract. |
| TIP has its own `ChildBranchPointId` | Sibling is enqueued, dequeue runs the BP walk normally. |
| Origin is itself a TIP | Chain expansion at every dequeue, not just the origin, picks up the HEAD's siblings transitively (HEAD -> TIP -> back to HEAD via `ChainBranch` match -> deduped). |
| Cache invalidation | `RecordingStore.StateVersion` already invalidates on `CommittedRecordings` change. |
| Tombstone scope | Same closure flows to `CommitTombstones`. Kerbal-death actions stamped against any chain segment retire correctly. Call out in PR description / CHANGELOG. |
| `MergeJournalOrchestrator` checkpoint | Closure is computed once in `AppendRelations`. The new chain expansion happens in the closure builder, so journal-resume sees the same set. |
| Mixed-parent halt | Chain expansion runs INSIDE the `while` loop AFTER the BP halt for each individual member. The halt at lines 588-595 is per-BP-walk; chain expansion cannot bypass it because the helper does not consult BPs. |

### Step 5 — Pre-existing-data migration: do nothing, document the gap

Saves committed before the fix may have HEAD already superseded but TIP orphaned. The merge-time closure runs once, then the marker is cleared (`SupersedeCommit.cs:230-232`). There is no "next merge" that would naturally heal a completed merge.

Three candidate strategies considered:

1. **OnLoad migration sweep** in `LoadTimeSweep.Run` that, for each `RecordingSupersedeRelation`, looks up the `OldRecordingId`, finds its chain siblings on the same `ChainBranch`, and appends supersede rows for any sibling missing one. Risk: runs every load, on every save, indefinitely; chain semantics could drift in a future v2 where parallel ChainBranches are first-class; the closure-builder fix is forward-looking and one-shot migration code accretes legacy weight.
2. **Targeted player-message** at load when an orphan TIP shape is detected.
3. **Do nothing**, document in CHANGELOG that older saves with a completed crashed-then-relived chain merge may show a phantom orphan TIP, and direct affected players to `Discard` the orphan via the table.

Recommendation: **do nothing**. The bug requires a specific timing (env-crossing crash, then re-fly, then merge). The number of pre-existing affected saves is small. The forward-fix solves all future merges. Add one sentence to the CHANGELOG entry noting the limitation. If a playtest report after the fix surfaces a real affected save, escalate to strategy 1 in a follow-up.

### Step 6 — Tests

#### `Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs` — add 5 new tests

Use the existing `Rec` / `Bp` / `InstallTree` / `InstallScenario` / `Marker` helpers. Pattern: install a tree with chain-linked recordings, install a scenario with a marker pointing at the HEAD or TIP, call `ComputeSessionSuppressedSubtree`, assert membership.

1. `ChainExpansion_HeadOrigin_IncludesTip` — origin = HEAD with `ChildBranchPointId = null` (post-split shape), TIP shares `ChainId`+`ChainBranch`, has the BP-link via `ChildBranchPointId`. Closure must contain HEAD and TIP. Assert log line `siblingsAdded` non-zero.
2. `ChainExpansion_TipOrigin_IncludesHead` — origin = TIP. Closure must contain TIP and HEAD. Defends symmetry / future-proofs against marker shape changes.
3. `ChainExpansion_DifferentChainBranch_Excluded` — three segments: seg0 (`ChainBranch=0`), seg1 (`ChainBranch=0`), seg_alt (`ChainBranch=1`, same `ChainId`). Origin = seg0. Closure contains seg0 and seg1 only.
4. `ChainExpansion_ThreeSegments_AllIncluded` — origin = first segment, two more siblings on `ChainBranch=0`. All three in closure.
5. `ChainExpansion_TipWithChildBranchPointId_BpDescendantsAlsoIncluded` — TIP carries a `ChildBranchPointId` that points at a BP whose `ChildRecordingIds` lists a downstream child. Walk picks up HEAD via chain expansion AND the BP-descendant via the existing walk. Closure size = 3 (HEAD, TIP, downstream child).

#### `Source/Parsek.Tests/SupersedeCommitTests.cs` — add 2 new tests

1. `AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment` — install tree with HEAD + TIP on same chain. Call `AppendRelations` with marker pointing at HEAD. Assert `scenario.RecordingSupersedes` contains rows for both HEAD and TIP, both pointing at the provisional. (This is the headline regression pin.)
2. `CommitTombstones_KerbalDeathInTip_TombstonedWithChainOrigin` — install tree with HEAD + TIP, seed `Ledger.Actions` with a `KerbalAssignment` +Dead action stamped against the TIP's `RecordingId`. Run `AppendRelations` then `CommitTombstones`. Assert one tombstone with `ActionId` matching the kerbal-death action, and `LedgerSwapTag` log line `Tombstoned 1 (KerbalDeath=1, ...)`.

#### Pin existing tests that MUST NOT regress

These already exist and must continue passing without modification:

- `SessionSuppressedSubtreeTests.ForwardOnlyClosure_LinearChain_IncludesAllDescendants` — single-tree non-chain BP walk.
- `SessionSuppressedSubtreeTests.ForwardOnlyClosure_ExcludesAncestors` — ancestor walk forbidden.
- `SessionSuppressedSubtreeTests.MixedParentHalt_DockedMergeHaltsClosure` — dock/board mixed-parent halt unchanged.
- `SessionSuppressedSubtreeTests.NullMarker_ReturnsEmpty`.
- `SessionSuppressedSubtreeTests.MarkerWithNoChildren_ReturnsOriginOnly` — note: with the fix, a marker pointing at a recording with `ChainId == null` still returns `{origin}` only because the helper early-returns. Confirm this test still passes (it should — no `ChainId` set on the test fixture).
- `SessionSuppressedSubtreeTests.IsInSessionSuppressedSubtree_PositiveAndNegativeCases`.
- `SupersedeCommitTests` Phase 8/10 fixtures (single-recording supersede, mixed-parent halt regression).
- `EffectiveStateTests` chain-walk tests using `ResolveChainTerminalRecording`.

### Step 7 — Documentation

#### `CHANGELOG.md`

Add under v0.9.0 follow-ups (the same block where items 19-22 live):

> **Chain-sibling supersede after env-split re-fly merge.** `EffectiveState.ComputeSessionSuppressedSubtreeInternal` now chain-expands every recording it adds to the suppressed-subtree closure: for each member, every committed recording sharing both `ChainId` and `ChainBranch` is enqueued so its `ChildBranchPointId` walk runs too. Previously the walk followed `ChildBranchPointId` only, so a merge-time `SplitAtSection` env crossing (atmo↔exo) that produced a HEAD (BP-linked, terminal=null) + TIP (terminal=Destroyed) chain left the TIP behind as an orphan when the player re-flew the HEAD and merged: the new re-fly recording superseded the HEAD only, and the player ended up with two contradictory recordings (the new "kerbal lived" attempt AND the stale "kerbal destroyed in atmo" TIP). Both `SupersedeCommit.AppendRelations` and `SupersedeCommit.CommitTombstones` consume the same closure, so chain segments now get one supersede row apiece and kerbal-death actions stamped against any segment retire correctly. Different `ChainBranch` values stay independent (parallel ghost-only continuations are not auto-suppressed together). Saves committed before this fix that already completed a chain-crossing crashed re-fly merge are not retroactively healed; affected players can `Discard` the orphan TIP via the recordings table.

#### `docs/dev/todo-and-known-bugs.md`

Add as item 23 in the v0.9 follow-ups list (pattern matching items 19-22):

> 23. **Re-fly merge supersede only covered the chain head, leaving a chain-tip orphan after env-split crashes.** ~~done~~ — `EffectiveState.ComputeSessionSuppressedSubtreeInternal` (`Source/Parsek/EffectiveState.cs:523`) walked the suppressed-subtree closure forward via `ChildBranchPointId` only. Merge-time `RecordingOptimizer.SplitAtSection` splits a single live recording at env boundaries (atmo↔exo) into a `ChainId`-linked HEAD + TIP where the HEAD keeps the parent-branch-point link to the RewindPoint but ends with `ChildBranchPointId = null`, while the TIP carries the `Destroyed` terminal and the BP-link. After re-fly merge, only the HEAD got a supersede row pointing at the new provisional; the TIP stayed visible with the original "kerbal destroyed in atmo" outcome alongside the new "kerbal lived" re-fly. Fixed by adding an `EnqueueChainSiblings` helper invoked on every dequeued member: for each recording added to the closure, every committed recording sharing both `ChainId` and `ChainBranch` is also added (and re-enqueued so its own `ChildBranchPointId` walk runs). The contract matches `EffectiveState.IsChainMemberOfUnfinishedFlight` and `ResolveChainTerminalRecording` (same `ChainId`+`ChainBranch` predicate); different `ChainBranch` values remain independent. `SupersedeCommit.AppendRelations` and `CommitTombstones` automatically extend with the closure — the journal still computes the set once per merge run. Tests `ChainExpansion_HeadOrigin_IncludesTip`, `ChainExpansion_TipOrigin_IncludesHead`, `ChainExpansion_DifferentChainBranch_Excluded`, `ChainExpansion_ThreeSegments_AllIncluded`, and `ChainExpansion_TipWithChildBranchPointId_BpDescendantsAlsoIncluded` in `SessionSuppressedSubtreeTests.cs`; `AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment` and `CommitTombstones_KerbalDeathInTip_TombstonedWithChainOrigin` in `SupersedeCommitTests.cs`. No retroactive migration: pre-existing affected saves keep the orphan TIP and require a manual `Discard`.

#### `docs/parsek-rewind-to-separation-design.md`

Update §3.3 (Session-suppressed subtree definition) with one bullet noting that the closure includes chain siblings sharing both `ChainId` and `ChainBranch`. Add: "Each member also expands to its chain siblings (same `ChainId` and `ChainBranch`) so a `RecordingOptimizer.SplitAtSection` env split between HEAD and TIP is suppressed atomically." Update §6.6 step 3 likewise if it describes the closure shape.

### Step 8 — Build / test verification

```
cd Source/Parsek && dotnet build
cd Source/Parsek.Tests && dotnet test
```

Targeted test runs while iterating:

```
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName~SessionSuppressedSubtreeTests"
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName~SupersedeCommitTests"
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName~EffectiveStateTests"
```

The grep-audit gate (`scripts/grep-audit-ers-els.ps1`) does not need an exemption — `EffectiveState.cs` reads `RecordingStore.CommittedRecordings` directly already (the closure builder at line 544 is the canonical site) and is the file the audit-allowlist exists to permit.

## Sequencing

1. Add unit tests first (red).
2. Implement closure-builder change + helper (green).
3. Run full `dotnet test` and confirm no regression in the pinned existing tests.
4. Update design doc §3.3 / §6.6.
5. Update CHANGELOG.md and todo-and-known-bugs.md.
6. Stage all in one commit per the per-commit doc-update rule.

## Risks / open questions

- **Performance.** Each `EnqueueChainSiblings` call iterates `recById.Values`. For typical chains (2-3 segments) this is trivial; for pathological cases (many recordings with the same `ChainId`/`ChainBranch`) it is O(N) per added member. Acceptable: closure rebuild is gated by `RecordingStore.StateVersion` change, not per-frame, and the cache is reused across `AppendRelations` + `CommitTombstones` within a single merge run.
- **Future v2 ChainBranch semantics.** The current contract treats different `ChainBranch` values as independent. If v2 ever introduces "auto-suppress all branches of a chain" semantics, the helper's filter is the single site to relax. Add a one-line comment pointing at `IsChainMemberOfUnfinishedFlight` as the sister contract that would also need to flip.
- **Loop concern.** Chain expansion enqueues siblings, which when dequeued run chain expansion again. The HashSet dedup at `result.Contains(cand.RecordingId)` (and the `if (result.Contains(childId)) continue` already at line 584 covers BP children) prevents infinite loops. Add an explicit comment.

## Critical Files for Implementation

- `Source/Parsek/EffectiveState.cs`
- `Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs`
- `Source/Parsek.Tests/SupersedeCommitTests.cs`
- `CHANGELOG.md`
- `docs/dev/todo-and-known-bugs.md`
- `docs/parsek-rewind-to-separation-design.md` (§3.3 / §6.6)
