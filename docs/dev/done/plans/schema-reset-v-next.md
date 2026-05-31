# Schema reset to generation 3 (clean-slate, no backwards compat)

## Goal

Pre-1.0 development has no public users, so backwards compatibility for old
recordings/saves is explicitly not a goal. Over several generations the loader
accumulated tolerance seams that only existed to read pre-reset recordings: the
legacy v5 world-offset RELATIVE contract, the `committed`-bool to MergeState
migration, the Phase-F tree-resource residual seam, the legacy
rewind-suppression marker normalizer, and a set of no-op format-version
contract-upgrade helpers. This reset bumps the schema generation, rejects
everything older, and deletes those seams.

## Bump mechanics

- `RecordingStore.CurrentRecordingSchemaGeneration` bumped `2 -> 3`.
- `RecordingStore.CurrentRecordingFormatVersion` stays `1`.
- `RecordingStore.IsRecordingSchemaCompatible` needs no structural change: the
  generation-older / generation-newer thresholds compare against the constant,
  so the rejection boundary moves automatically. Generation 2 and older now
  reject with reason `generation-older`; a higher generation rejects with
  `generation-newer`.
- Every downstream gate reads the generation symbolically
  (`RecordingTreeRecordCodec`, `RecordingTree.Load`, `RecordingSidecarStore`,
  `Ledger`, `TrajectorySidecarBinary`, `SnapshotSidecarCodec`), so the bump is
  a one-line constant change plus the seam deletions.
- There is only the one `CurrentRecordingSchemaGeneration` constant. There is
  no separate named per-generation constant (the prior CLAUDE.md claim of a
  `ControlledChildParentAnchorSchemaGeneration` named constant was stale and was
  corrected in this PR).

## Deleted seams, and why each is dead

Each seam was grepped for live (non-test) callers before deletion. Three are
"dead under the no-compat policy" rather than dead by the type system, and are
flagged for the reviewer below.

- **`RecordingStore.UsesRelativeLocalFrameContract` / `DescribeRelativeFrameContract`**
  - Dead by code: `UsesRelativeLocalFrameContract` returned constant `true` and
    only gated the v5 false branch of `ResolveRelativePlaybackPosition`.
    `DescribeRelativeFrameContract` returned constant `"anchor-local"`; its only
    callers were diagnostic log strings, where the literal is inlined.
- **`RecordingStore` legacy `committed`-bool to MergeState migration cluster**
  - `LegacyMergeStateMigrationCount`, `EmitLegacyMergeStateMigrationLogOnce`,
    `BumpLegacyMergeStateMigrationCounterForTesting`,
    `ResetLegacyMergeStateMigrationForTesting`, plus the `committed`-bool read in
    `RecordingTreeRecordCodec.LoadRecordingFrom`. The `committed` bool never
    shipped in any release, so no on-disk recording carries it. Dead under the
    no-compat policy (flagged for reviewer).
- **`TrajectoryMath.ComputeRelativeOffset(Vector3d,Vector3d)` and
  `ApplyRelativeOffset(Vector3d,double,double,double)`**
  - The legacy v5 world-offset overloads. The only production caller was the
    dead v5 false branch of `ResolveRelativePlaybackPosition`, which now calls
    `ApplyRelativeLocalOffset` directly. (The unrelated live recorder method
    `FlightRecorder.ApplyRelativeOffset(ref TrajectoryPoint, Vessel)` is a
    different overload and is untouched.)
- **`FlightRecorder.ShouldEmitStructuralEventSnapshot` /
  `ResolveRelativeContractUpgradeTarget` / `MaybeUpgradeActiveRecordingRelativeContract`**
  - `ShouldEmitStructuralEventSnapshot` returned constant `true` (inlined at the
    two BackgroundRecorder call sites). `ResolveRelativeContractUpgradeTarget`
    returned `CurrentRecordingFormatVersion` and had no production caller.
    `MaybeUpgradeActiveRecordingRelativeContract` was an empty no-op test seam
    with no caller at all.
- **`RecordingTree.LegacyResourceResidual` seam**
  - The `LegacyResourceResidual` nested type, the `legacyResidual` field + its
    clone in `DeepClone`, `ConsumeLegacyResidual`, `SetLegacyResidualForTesting`,
    `LoadLegacyResidual`, the residual load call in `Load`, and the Phase-F
    residual diagnostic log block. The residual was consumed only by
    `LedgerLoadMigration.MigrateLegacyTreeResources` (also deleted), so once that
    consumer is gone the residual is write-only. Dead under the no-compat policy
    (flagged for reviewer).
- **`RecordingTree.NormalizeLegacyRewindSuppressionMarkers` /
  `MarkLegacyRewindSuppressionAsSource`** and the `Load` call site.
  - These re-homed legacy `legacy-unscoped` rewind-suppression markers to a
    same-recording source on load of pre-reset trees. Dead under the no-compat
    policy (flagged for reviewer). The `RewindSpawnSuppressionReasonLegacyUnscoped`
    constant is KEPT: it still has live writers in `GhostPlaybackLogic` (runtime
    missing-reason fallback) and `RecordingTreeRecordCodec` (load-time
    missing-reason fallback).
- **`LedgerLoadMigration.MigrateLegacyTreeResources` and helper cluster**
  - `MigrateLegacyTreeResources`, `TryInjectLegacyFundsEarning`,
    `InjectLegacyScienceEarning/Spending`, `InjectLegacyReputationEarning/Penalty`,
    `FindLegacyMigrationSyntheticMatches`, `WarnLegacyFormatGate`,
    `ComputeTreeStartUT`, `HasAnyLedgerCoverage`, `IsMatchingLegacy*Migration`,
    the tolerance constants, plus the `LedgerOrchestrator.MigrateLegacyTreeResources`
    wrapper and its call in `OnKspLoad`. These consumed
    `tree.ConsumeLegacyResidual()`, which is `null` once the residual seam is
    gone. `IsResourceImpactingAction` is KEPT (its `LedgerOrchestrator` wrapper is
    still live and a Theory test pins the enum surface). The
    `FundsEarningSource.LegacyMigration` enum value is KEPT: it is still consumed
    by `GameActionDisplay` and `PostWalkActionReconciler`, and ledger rows that
    carry the tag still need to round-trip.

## The injector survives automatically

The synthetic-recording injector (`InjectAllRecordings`) keeps working with no
generator change. The generators stamp the generation symbolically from
`RecordingStore.CurrentRecordingSchemaGeneration` rather than a literal, so the
bump propagates for free. Confirmed by `dotnet test --filter InjectAllRecordings`
passing post-bump.

## Don't-over-delete (verified load-bearing, kept)

- `FlightRecorder.RestoreTrackSectionAfterFalseAlarm` else-branch (null boundary
  seed) is still live for current-gen Relative sections lacking a body-fixed
  primary; only the stale "(legacy v5/v6)" comment was reworded.
- `RecordingTree` `RecordingFormatVersion == -1` rejected-sentinel handling and
  `PruneRejectedRecordingReferences` (current rejection path).
- `RewindSpawnSuppressionReasonLegacyUnscoped` (live writers remain).
- `FundsEarningSource.LegacyMigration` enum value (live consumers remain).
- `LedgerLoadMigration.IsResourceImpactingAction` (live wrapper + enum-surface test).

## Commit split

1. Schema bump + dead-seam deletion + test fixtures + docs.
2. Mechanical, isolated removal of the now-dead `recordingFormatVersion`
   parameter from `TrajectoryMath.ResolveRelativePlaybackPosition` and its
   cascade through every caller (high churn, kept separate for reviewability).

## Out of scope

The broader rendering-doc reconciliation (stale `v6`/`v7`/`v13` framing across
design docs) is a separate PR.
