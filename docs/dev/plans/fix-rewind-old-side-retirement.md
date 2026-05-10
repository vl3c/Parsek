# Plan: retire old-side recordings rewound out of existence

## Problem

After a successful Re-Fly merge followed by a Rewind to a UT *before* every recording in the rewound subtree, the original (pre-Re-Fly) recordings re-appear in the recordings table — including a "Destroyed" outcome the user successfully Re-Flew past.

Reproduction from `logs/2026-05-10_1713`:

- Tree owner is `Kerbal X` rocket recording `1354326d` (StartUT 301.94, launch lead 15 s ⇒ rewindAdjustedUT 286.9).
- Subtree contains four Kerbal X Probe recordings: `32d9674c` (atmo), `cbec2f47` (exo, `terminalState=Destroyed`), `97d7d55d` (exo continuation), and the Re-Fly fork `rec_b1566ae4` (Orbiting). All four start at UT ≥ 456 — well after the rewindAdjustedUT.
- Re-Fly committed three supersede relations (`32d9674c → rec_b1566ae4`, `cbec2f47 → rec_b1566ae4`, `97d7d55d → rec_b1566ae4`); cbec2f47 is correctly hidden in the recordings UI immediately after merge (`memberCount=9` in the post-merge log).
- User then Rewinds. `Rewind supersede rollback: dropped=3 retiredForks=1 restored=3` — only `rec_b1566ae4` is retired; the three "old" recordings are merely unsuperseded.
- After the rewind, the recordings UI shows `memberCount=11` and `cbec2f47` (Destroyed) is visible again, even though it represents content that happened *after* the rewind point and was overridden by a successful Re-Fly.

## Root cause

`RecordingStore.cs:4944` `EnsureRewindRetirementsForRollback` only iterates `rollback.RetiredForkRecordingIds` (the *new* sides of dropped supersedes). The *old* sides land in `rollback.RestoredRecordingIds` and are never considered for retirement, even when their `StartUT > rewindAdjustedUT`.

`DropSupersedesRewoundOutOfExistenceDetailedPure` already proves these old sides are "rewound out": the relation is only dropped if the OldRecordingId is in `rewoundOutOldIds` (owner ∪ tree recs with `StartUT >= rewindAdjustedUT`). For owner.StartUT == rewindAdjustedUT (the canonical "rewind to launch" case the existing tests model), the owner is in rewoundOutOldIds but is the recording the user is returning to — leaving it as `Restored` is correct. For other tree recordings whose `StartUT > rewindAdjustedUT`, leaving them as `Restored` is wrong: they happened in the future the rewind erased.

## Fix

In `EnsureRewindRetirementsForRollback`, after the existing fork-retirement loop, iterate `rollback.RestoredRecordingIds` and emit an additional retirement for any restored id whose live `StartUT > rewindAdjustedUT` (strict). Skip the owner explicitly so the owner's row is never retired even when its StartUT exceeds rewindAdjustedUT due to the rewind-to-launch lead time.

### Why strict `>` and not `>=`

The existing fork loop uses `<` (i.e. retires forks at `StartUT >= rewindAdjustedUT`). For the old side, strict `>` matters: the canonical "rewind to launch" pattern has `owner.StartUT == rewindAdjustedUT`, and `DetailedRollback_MultiGenerationalChain_RetiresForksAndRestoresOnlyOrigin` pins that the owner stays in `RestoredRecordingIds` and is *not* retired. Using strict `>` keeps that test green without an explicit owner check, and the explicit owner check is added separately as a defense in depth (covers cases where owner.StartUT > rewindAdjustedUT due to the launch lead-time gap, like the playtest).

### Why exclude the owner

The rewind lands at `rewindUT = owner.StartUT - RewindToLaunchLeadTimeSeconds`. So `rewindAdjustedUT < owner.StartUT` is normal for rewind-to-launch. Without the explicit owner skip, an owner that is *itself* the OldRecordingId of a dropped supersede (theoretical but possible in stacked re-flies) would be retired — wrong because the owner is the rewind target.

### New retirement reason

`RecordingRewindRetirement.DefaultReason = "rewound-out-supersede-fork"` describes the existing fork-side retirement. Add a new constant `RewoundOutOldSideReason = "rewound-out-supersede-old-side"` and use it for the new entries. Keeps post-mortem grep + log diagnostics unambiguous about which loop wrote the row.

### Field shape on the new retirement

- `RecordingId` = the restored id.
- `RestoredRecordingId` = `null`. Old-side recordings have nothing to "fall back to" — they *are* the original. `LoadTimeSweep.cs:605` and `TreeDiscardPurge.cs:368` both early-out on null `RestoredRecordingId`, so this is consistent with their existing handling.
- `SourceSupersedeRelationId` = `null`. Old-side recordings can be the OldRecordingId of *multiple* dropped relations (the user's case has only one per old recording, but the data structure allows fan-in). Picking one is misleading; null says "no single source rel."
- `RewindUT`, `CreatedUT`, `CreatedRealTime` — same as the fork loop.
- `Reason` = the new `RewoundOutOldSideReason`.

### Pure-vs-live boundary

The current code splits between `DropSupersedesRewoundOutOfExistenceDetailedPure` (pure, returns the rollback result) and `EnsureRewindRetirementsForRollback` (consumes the result and writes scenario rows). The new logic stays in `EnsureRewindRetirementsForRollback` — the rollback result already carries everything needed (`RestoredRecordingIds`, `liveRecordingsById`). No change to the pure function's signature or semantics; only the live entry adds retirements.

### Owner-id plumbing

`EnsureRewindRetirementsForRollback` doesn't currently take an owner. Add an `ownerRecordingId` parameter — passed by the live entry `DropSupersedesRewoundOutOfExistence` (which has `owner` in scope). Keep it nullable / optional so misuse degrades to "no owner skip" rather than crashing.

### `restoredCount` line in the rollback summary log

Today the summary log reports `dropped=3 retiredForks=1 restored=3`. After the fix, the third value drifts: of the 3 "restored" entries, 3 actually convert to additional retirements. Update the log to also report `retiredOldSides=N` so the playtest log lines stay legible:

```
Rewind supersede rollback: dropped=3 retiredForks=1 retiredOldSides=3 restored=0 rewindUT=286.9 owner='Kerbal X'
```

`restored` should reflect the post-fix count (originals that legitimately stayed visible — owner.StartUT == rewindAdjustedUT cases). Keep the field name to avoid breaking grep heuristics in old logs.

## Test plan

Add to `Source/Parsek.Tests/RewindSupersedeRollbackTests.cs`:

1. **`LiveRollback_RetiresOldSides_WhenAllStartAfterRewindUT`** — mirrors the playtest. Owner at StartUT=302, rewindAdjustedUT=286.9, three old recordings at StartUT={456, 466, 960} all superseded by a fork at StartUT=457. After `DropSupersedesRewoundOutOfExistence`:
   - 3 supersedes dropped, supersedes list empty.
   - 4 retirement entries: 1 fork + 3 old-sides.
   - The 3 old-side entries have `Reason=RewoundOutOldSideReason`, `RestoredRecordingId=null`, `SourceSupersedeRelationId=null`.
   - The cbec2f47-equivalent old-side retirement `RecordingId` matches; `EffectiveState.IsRewindRetired` returns true for it.

2. **`LiveRollback_KeepsOwnerVisible_WhenOwnerWasOldSide`** — synthetic stack: owner is itself the OldRecordingId of a dropped supersede (rewind out of nested re-fly). owner.StartUT > rewindAdjustedUT. Confirm owner is NOT retired despite being in RestoredRecordingIds.

3. **`LiveRollback_KeepsOriginAtBoundary_RestoredNotRetired`** — extends the existing `DetailedRollback_MultiGenerationalChain_RetiresForksAndRestoresOnlyOrigin` to the live entry. Owner A at StartUT=6.5, rewindAdjustedUT=6.5 ⇒ A is in RestoredRecordingIds, A is NOT retired (strict `>` filter). B and C are retired as forks.

4. **Update existing `LiveRollback_DeduplicatesRetirement_WhenMultipleOldRowsPointToSameNew`** — currently asserts `Single(scenario.RecordingRewindRetirements)` for the single fork; under the fix, the original A also gets retired (A.StartUT=6.5 == rewindAdjustedUT 6.5 ⇒ NOT retired by strict `>`; but B at 31.5 > 6.5 IS retired). Walk through carefully:
   - In that test, supersedes are `A→C, B→C`. Owner is A (StartUT 6.5), rewindAdjustedUT=6.5.
   - rewoundOutOldIds = {A, B, C}.
   - Both relations drop: RetiredForkRecordingIds={C}, RestoredRecordingIds={A, B}.
   - With strict `>`: A.StartUT=6.5 is NOT > 6.5, A stays. B.StartUT=31.5 IS > 6.5 — B gets a new old-side retirement.
   - So the test should now assert TWO retirements (one fork=C, one old-side=B), and B's retirement carries `Reason=RewoundOutOldSideReason`.

5. **`OldSideRetirement_LoggedSeparately`** — assert the log captures `[Rewind] Retired rewound-out old-side rec=…` and the rollback summary line includes `retiredOldSides=N`.

## CHANGELOG / docs

- `CHANGELOG.md` under `## 0.9.2 / ### Bug Fixes`: one-line entry describing the visible behavior change.
- `docs/dev/todo-and-known-bugs.md`: add a Done entry under v0.9.2 with the playtest log path (`logs/2026-05-10_1713`) and a one-paragraph technical summary.
- No new design doc — this is a single-function fix with new tests.

## Out of scope

- The `chainId 24e5728f` orphan (cbec2f47 chainIndex=1 with no chainIndex=0 sibling after the post-merge optimizer split) is observable in the same logs but is a separate bug in optimizer chain-reindex and not what this PR fixes.
- Generalizing retirement to "every recording with StartUT > rewindUT" (i.e. retiring even non-superseded post-rewind recordings) is a much bigger semantic change and not justified by this playtest.
