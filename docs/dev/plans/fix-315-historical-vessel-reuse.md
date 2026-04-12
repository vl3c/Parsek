# Fix 315: Historical Vessel Reuse and Tree PID Semantics

## Investigation Summary

Bug 315 is not pointing at obviously corrupted save data. The captured follow-up storage playtest from 2026-04-12 shows:

- `RecordingTreeIntegrityTests.ParentLinksValid` passed.
- The collected `persistent.sfs` has no dangling `ParentRecordingId` references in the affected trees.
- The failing PID is `2708531065`, reused by three committed `Kerbal X` trees:
  - tree `75f7952937744f419b17d241e4437214`, root `081e7b3ce4b84acc946166a0a3b7926e`
  - tree `1012a690f76a4a39a5e9e506c2fb7808`, root `641be2f9522d439397f4ea9fa2caabd2`
  - tree `6e021a9de85c42e58e1e90041b832e42`, root `7f7eadcb943941c1a1668cd44f176459`

The important detail is that these are overlapping alternate histories, not a simple single linear continuation:

- tree `75f...`: `Kerbal X` runs from UT `10.62` to `86.09`
- tree `1012...`: `Kerbal X` runs from UT `12.58` to `158.99`
- tree `6e021...`: `Kerbal X` runs from UT `11.66` to `245.57`

That means cross-tree PID reuse is currently a valid archived-history shape.

## Current Code Meaning

`RecordingTree.RebuildBackgroundMap()` currently builds `OwnedVesselPids` from every non-zero `Recording.VesselPersistentId` in the tree.

That set is only used at runtime by `GhostPlaybackLogic.IsVesselOwnedByTree()`, which answers a tree-local question:

- does this tree contain this PID anywhere in its recorded history?

That is not the same as global ownership across all committed trees.

The failing in-game test, `RecordingTreeIntegrityTests.NoPidCollisionAcrossTrees`, assumes the opposite: it treats `OwnedVesselPids` as a globally unique claim set. With overlapping alternate histories, that contract is too strict.

The failure output is also slightly misleading: in the captured run there is one shared PID (`2708531065`), but the test increments once per colliding tree pair, so it reports `2 vessel PID(s)` for the `(tree0, tree1)` and `(tree0, tree2)` collisions.

## Additional Audit Findings

The repro does not currently prove a live runtime failure in ghost-chain evaluation:

- the duplicated `Kerbal X` root PID does not appear in `GhostChainWalker` logs for this save
- the relevant EVA branch points in `persistent.sfs` do not carry `targetVesselPid`
- `GhostChainWalker.ScanBackgroundEventClaims()` excludes root-lineage PIDs, so the overlapping root PID is not treated as a background-claim PID in this repro

Even so, the audit found that several runtime helpers are PID-only and should stay conceptually separate from the "every PID ever recorded in this tree" set:

- `GhostChainWalker.ComputeAllGhostChains()` groups claims by global PID
- `ParsekFlight.FindBackgroundRecordingForVessel()` returns the first committed recording whose PID and UT match
- ghost map / ghost chain state is keyed by PID

That makes it important to keep archived-history membership distinct from any future globally unique "claim" set.

There is also an existing intentional cross-tree PID reuse case in unit coverage:

- `GhostChainWalkerTests.CrossTree_TwoLinks_ChainsExtend` depends on PID-based cross-tree linking as a feature

So the fix must not turn "same PID appears in multiple committed trees" into a blanket integrity violation.

## Proposed Fix Direction

### 1. Clarify the cached tree PID set

Replace the ambiguous `OwnedVesselPids` meaning with a name and comments that match actual usage, for example:

- `RecordedVesselPids`
- or `TreeRecordedVesselPids`

Then update `GhostPlaybackLogic.IsVesselOwnedByTree()` to use the renamed set and document the real contract:

- true means "this PID appears somewhere in this tree's recordings"
- false does not imply anything about other trees

### 2. Replace the failing integrity check with a real invariant

Remove or rewrite `RecordingTreeIntegrityTests.NoPidCollisionAcrossTrees`.

Replace it with checks that target actual corruption risk, for example:

- every `BackgroundMap` key resolves to an existing recording in the same tree
- the mapped recording's `VesselPersistentId` matches the `BackgroundMap` key
- eligible background recordings are represented consistently after `RebuildBackgroundMap()`
- duplicate eligible background recordings for the same PID inside one tree are treated as an explicit failure unless the implementation defines a documented deterministic winner

These checks match the runtime structures that must stay coherent after save/load.

### 3. Add explicit regression coverage for historical PID reuse

Add unit coverage for the valid case this repro exposed:

- multiple committed trees can contain the same root `VesselPersistentId`
- ghost-skip logic still treats that PID as tree-local recorded history
- the integrity suite no longer flags this archived overlap as corruption

This should use synthetic trees rather than the full playtest save.

### 4. Guard against future misuse of archived-history PIDs

Audit PID-only helpers and add focused tests so future code does not accidentally reuse the archived-history membership set as a global ownership set.

Priority targets:

- `GhostChainWalker`
- `ParsekFlight.FindBackgroundRecordingForVessel()`
- any future cross-tree dedup or claim-tracking logic

Add explicit negative regressions here, not just a code audit:

- archived root/history PID overlap by itself must not be mistaken for a background/claim collision
- overlapping rewind continuations that really are the same claimed vessel lineage must still merge into one ghost chain
- a chain trajectory lookup should prefer chain-participating trees when they cover the UT, but still fall back to global PID lookup before the first claim when no chain-local trajectory exists

If a truly globally unique runtime claim set is needed later, introduce a separate cache derived from branch-point targets / background claims instead of overloading the "all recorded PIDs" cache.

## Verification Plan

- Unit tests for the renamed tree PID cache semantics
- Unit tests for the new historical-reuse regression
- Unit tests for the replacement integrity checks around `BackgroundMap`
- Focused PID-helper regressions for overlapping rewind continuations and pre-claim trajectory fallback
- In-game `TreeIntegrity` suite rerun against the 2026-04-12 follow-up storage playtest
- Manual review of any PID-only helper touched by the fix to confirm it is using the right semantic set

## Open Question For Implementation

Renaming the cache is the cleanest way to remove the semantic mismatch, but if the team wants the smallest patch, the fallback is:

- keep the field name temporarily
- rewrite comments
- replace the failing in-game test and add regression coverage

That would fix the immediate false positive, but it would preserve a misleading API name.
