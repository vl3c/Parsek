# Fix Plan: watch-mode double probe ghost after Re-Fly supersede rollback

## Worktree

- Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-watch-double-probe-ghost`
- Branch: `fix-watch-double-probe-ghost`
- Base: `06f676a1 Merge PR #777 fix-debris-hide-parent-range for testing`
- Reproduction bundle: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-09_0002_double-ghost-probe-booster`

This worktree starts from the same merged PR #776 + #777 test build that produced
the log bundle.

## Implementation status

Implemented in this worktree.

- Added persisted `RecordingRewindRetirement` state with ConfigNode save/load.
- `RecordingStore.DropSupersedesRewoundOutOfExistence` now returns dropped
  relation details and writes one retirement for each distinct rewound-out fork.
- `ParsekScenario` and `ReconciliationBundle` now round-trip retirements.
- `EffectiveState.ComputeERS`, flight/watch playback, KSC playback, Tracking
  Station map presence/materialize handoffs, deferred/held spawn policy,
  recordings-table/group display, group hierarchy cleanup, and fast-forward
  guards all consume the combined timeline-inactive state.
- `LoadTimeSweep` removes orphan retirements whose retired recording vanished;
  `TreeDiscardPurge` removes retirements tied to a discarded tree.
- Logs distinguish `rewind-retired` from `superseded-by-relation`.

Validation run:

```text
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName!~InjectAllRecordings"
Passed: 10965

dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
Build succeeded
```

Full unfiltered `dotnet test` is blocked only by the existing local KSP lock:
`InjectAllRecordings` refused to purge the KSP save recording directory because
`KSP.log` is locked by the running game. The build/test deploy copy also reports
expected access-denied warnings for `Parsek.dll`, `Parsek.pdb`, and
`Parsek.version` while KSP has the installed files locked.

## Bug evidence

During watch mode, the probe booster was rendered twice because two committed
recordings of the same vessel were simultaneously effective:

- Original probe branch: recording `#8`,
  `038909d34c0846cf83f778cc982aab35`, vessel `Kerbal X Probe`,
  vessel PID `65291382`, root part PID `3087746488`.
- Re-Fly continuation: recording `#11`,
  `rec_21dfc05ffb3e42288735ee9bf0ebfb66`, same vessel PID and root part.

The important log sequence:

1. `KSP.log:10445` - decouple created the original controlled child probe.
2. `KSP.log:10545` - original probe recording `#8` was created.
3. `KSP.log:131673` - in-place continuation fork created `#11`, superseding `#8`.
4. `KSP.log:257045` - merge wrote the intended supersede relation for `#8 -> #11`.
5. `KSP.log:257105` - ERS rebuilt with `skippedSuperseded=1`, so `#8` was hidden.
6. `KSP.log:257975` - root Rewind-to-Launch dropped that supersede relation.
7. `KSP.log:258036` - the drop was re-applied after `LoadScene`.
8. `KSP.log:258261` / `258265` - scenario saved with `RecordingSupersedes saved: 0`.
9. `KSP.log:310383` - watch playback spawned ghost `#8`.
10. `KSP.log:319341` - watch playback spawned ghost `#11`.
11. `KSP.log:325328` through `325339` - both ghosts were active at UT `237.370`,
    separated by only a few metres.

PR #776 made the relative continuation render correctly, and PR #777 made the
debris parent-coverage gate do its job. Neither PR is the direct cause. They
made the already-existing state contradiction visible: the old source recording
was restored by the rewind supersede rollback, but the new fork recording stayed
playable.

## Root cause

`RecordingStore.DropSupersedesRewoundOutOfExistence` currently does only half of
the rewind rollback:

- It removes `RecordingSupersedeRelation` rows whose old recording belongs to
  the rewound tree and whose new fork starts after the adjusted rewind UT.
- That correctly restores the old source recording, fixing the earlier bug where
  Rewind-to-Launch left the source ghost skipped as `superseded-by-relation`.
- It does not retire, hide, or otherwise suppress the fork recording that was
  just declared "rewound out of existence".

So after the relation is dropped:

- the old source is visible because it is no longer superseded;
- the new fork is also visible because nothing else suppresses it;
- if the fork shares the same vessel/root snapshot, watch mode renders a double
  ghost.

The live code path is:

- `Source/Parsek/RecordingStore.cs:4511` - call site during rewind.
- `Source/Parsek/RecordingStore.cs:4603` - cross-`LoadScene` re-apply.
- `Source/Parsek/RecordingStore.cs:4657` - pure drop predicate.
- `Source/Parsek/EffectiveState.cs:536` - ERS only filters `NotCommitted`,
  relation-superseded recordings, and active-session suppressed subtree rows.
- `Source/Parsek/ParsekFlight.cs:14667` - runtime playback flags compute their
  own relation-superseded set directly from committed recordings.

## Correct behavior

When a plain Rewind-to-Launch rewinds past a previously committed Re-Fly fork:

1. The old source recording must be restored to playback.
2. The rewound-out fork recording must not render, spawn, create map presence,
   or offer watch/fast-forward as if it were still part of the active timeline.
3. The fork data should not be physically deleted as part of this bug fix.
   Deletion is higher risk because it touches sidecars, recording trees, ledger
   attribution, and recovery workflows.
4. The state must survive the same `LoadScene` restoration path that currently
   forced the supersede-drop re-apply.
5. Existing future same-tree recordings that were not produced by a dropped
   supersede relation must remain playable. This fix must not regress #589,
   where future same-tree recordings intentionally stayed spawn-eligible.
6. Retirement is one-way for v1. If the player later flies into a similar future,
   that produces a new recording id rather than un-retiring the old fork.

## Preferred fix

Keep the existing supersede relation drop, but pair it with an explicit
recording-level retirement ledger for the fork side of each dropped relation.

Working name: `RecordingRewindRetirement`.

This is intentionally separate from `RecordingSupersedeRelation`:

- A supersede relation says "old recording is replaced by new recording".
- A rewind retirement says "this new fork was committed in a future that the
  player has now rewound out of the active timeline".

Do not encode this as an inverse supersede relation (`new -> old`). That would
reuse existing filters cheaply, but it would violate the old-to-new supersede
contract, make multi-old-to-one-new merges ambiguous, and create confusing
`EffectiveRecordingId` chains.

Do not solve this by setting `PlaybackEnabled=false`. That suppresses ghost
rendering, but `PlaybackEnabled` intentionally does not suppress terminal spawn
after bug #433, and it is a user-facing visual toggle rather than timeline
state.

## Data model

Add a new persisted class:

```csharp
public sealed class RecordingRewindRetirement
{
    public string RetirementId;              // "rrt_<guid>"
    public string RecordingId;               // retired fork recording id
    public string RestoredRecordingId;       // source recording restored by the drop
    public string SourceSupersedeRelationId; // relation that caused this retirement, if known
    public double RewindUT;                  // adjusted rewind UT used for the drop
    public double CreatedUT;                 // current UT when retirement was written
    public string CreatedRealTime;           // ISO timestamp
    public string Reason;                    // "rewound-out-supersede-fork"
}
```

Persist it in `ParsekScenario` under:

```text
RECORDING_REWIND_RETIREMENTS
{
    ENTRY
    {
        retirementId = ...
        recordingId = ...
        restoredRecordingId = ...
        sourceSupersedeRelationId = ...
        rewindUT = ...
        createdUT = ...
        createdRealTime = ...
        reason = rewound-out-supersede-fork
    }
}
```

Add `ParsekScenario.RecordingRewindRetirements`, save/load counts, and bump the
recording-visibility cache version when this list changes. This implementation
reuses `SupersedeStateVersion` because retirements affect the same ERS visibility
cache as supersede relations. Every retirement add/remove path must call
`ParsekScenario.BumpSupersedeStateVersion()` exactly once for the mutation batch.

Also wire this list into `ReconciliationBundle`. That bundle currently snapshots
and restores rewind points, supersede relations, and ledger tombstones during
reconciliation flows. A new timeline-state ledger must round-trip there too, or
quicksave/Re-Fly reconciliation can silently lose a retirement and resurrect the
double ghost later.

## Rewind mutation flow

Refactor the current drop helper so it returns the dropped relation details, not
just an integer count.

Proposed result type:

```csharp
internal sealed class RewindSupersedeRollbackResult
{
    public int DroppedRelationCount;
    public List<RecordingSupersedeRelation> DroppedRelations;
    public HashSet<string> RestoredRecordingIds;
    public HashSet<string> RetiredForkRecordingIds;
}
```

Then update both live entry points:

1. `RecordingStore.InitiateRewind`
   - after `RewindContext.SetAdjustedUT(...)`;
   - call the rollback helper;
   - remove qualifying supersede rows;
   - append retirement entries for each distinct dropped `NewRecordingId`.

2. `RecordingStore.ReapplyRewindSupersedeDropAfterLoad`
   - after `ParsekScenario.OnLoad` reloads scenario state;
   - re-run the same relation drop and retirement creation;
   - this is required because the current logs show KSP can restore stale
     scenario-side supersede state across `LoadScene`.

Retirement creation is co-located with the relation drop after
`RewindContext.SetAdjustedUT(...)`. If the later `LoadGame` step fails, the
existing rewind reset path discards the in-memory relation drop and the matching
retirement mutation together, keeping rollback symmetric.

Retirement creation rules:

- Retire each distinct `rel.NewRecordingId` only if it resolves to a live
  committed recording whose `StartUT >= rewindAdjustedUT`.
- In multi-generation rollback (`A -> B`, `B -> C`) retirement wins over
  restoration for middle nodes. `A` is restored, `B` and `C` are retired. Do not
  report `B` as both restored and retired just because it appeared as an
  `OldRecordingId` in one dropped relation.
  Example: if the player rewinds `A` before either fork starts, `B` was still
  committed in the rewound future even though it is the source of `B -> C`, so
  `B` must remain hidden along with `C`.
- Do not retire when the new fork starts before the adjusted rewind UT.
- Do not retire unrelated-tree relations.
- Do not duplicate an existing retirement for the same recording id.
- Log one summary:

```text
[Parsek][INFO][Rewind] Rewind supersede rollback: dropped=1 retiredForks=1 restored=1 rewindUT=6.52 owner='Kerbal X'
```

Also log each retirement at `Info` or bounded `Verbose`:

```text
[Parsek][INFO][Rewind] Retired rewound-out fork rec=rec_21df... restored=038909... sourceRel=rsr_... rewindUT=6.52
```

`rewindUT` is the adjusted rewind target; the double-ghost observation happened
later during watch playback at UT `237.370`.

## Visibility and playback filters

Retired recordings must behave like relation-superseded recordings for playback
and map presence, but with their own reason in logs.

First add one central helper that returns inactive timeline-state ids and
reasons:

```csharp
internal enum TimelineInactiveReason
{
    None,
    SupersededByRelation,
    RewindRetired,
}

internal static Dictionary<string, TimelineInactiveReason> ComputeTimelineInactiveRecordingIds(
    IReadOnlyList<Recording> committed,
    IReadOnlyList<RecordingSupersedeRelation> supersedes,
    IReadOnlyList<RecordingRewindRetirement> retirements)
```

This avoids separate callers drifting between "superseded" and
"rewind-retired". `ComputeRewindRetiredRecordingIds(...)` can remain as a small
building block, but production policy code should prefer the combined helper.

Update these sites:

- `EffectiveState.ComputeERS`
  - compute retired recording ids from `ParsekScenario.RecordingRewindRetirements`;
  - skip retired rows;
  - add `skippedRewindRetired` to the ERS rebuild log.

- `ParsekFlight.ComputePlaybackFlags`
  - compute retired ids once per frame next to relation-superseded ids;
  - add a new `GhostPlaybackSkipReason.RewindRetired`;
  - add the `GhostPlaybackSkipReasonExtensions.ToLogToken()` case
    (`rewind-retired`);
  - add `GhostPlaybackFrameCounters.rewindRetired` and wire the
    `GhostPlaybackEngine` per-frame skip switch/summary builder to increment and
    report it;
  - pass `rewindRetired` into skip-detail logging;
  - force `needsSpawn=false` for retired recordings.
  - because retired recordings resolve to `skipGhost=true`, existing
    `WatchModeController.ValidateWatchedGhostStillActive()` exits watch mode if
    the currently watched fork becomes retired and its ghost is destroyed.

- `ParsekPlaybackPolicy`
  - skip retired recordings in spawn-death checks;
  - purge deferred spawn and flag replay queues for retired recording ids;
  - release held ghosts and held-ghost retry state for retired ids, matching the
    existing relation-superseded cleanup path;
  - include retired counts in existing rate-limited policy logs.

- `GhostMapPresence`
  - skip tracking-station ghost map presence and tracking-station spawn handoffs
    for retired recordings;
  - include retired ids in the tracking-station suppressed-id set used for
    ghost presence, orbit lines, and targeting, not only terminal spawn handoff;
  - mirror the relation-superseded helper shape so map code and flight code do
    not diverge.

- `RecordingsTableUI`, `GroupHierarchyStore`, and KSC/raw-list consumers
  - treat retired recordings as hidden from normal effective timeline display;
  - do not offer Watch, FF, Rewind, or terminal spawn affordances on retired rows;
  - add execution-time guards to Fast Forward / Go To / Watch actions, not just
    display filtering. Stale dialogs and direct action calls must refuse retired
    recording ids at `RecordingStore.FastForwardToRecording`,
    `RecordingsTableUI` action dispatch, and `ParsekFlight` fast-forward
    execution paths;
  - if a diagnostic/all-recordings view exists, label the row as
    `rewind-retired` rather than silently dropping it.

Do not add a separate Timeline-window filter unless inspection proves it is
needed. Timeline rendering already consumes `EffectiveState.ComputeERS()`, so a
central ERS filter should cover it. Separate Timeline checks risk creating a
second source of truth.

### Relative-anchor impact

Format-v11 non-loop Relative sections resolve through
`TrackSection.anchorRecordingId`. A retired fork can also be the anchor for a
downstream recording, but in the Rewind-to-Launch rollback shape that downstream
recording is part of the same rewound-out future and is retired too. Playback
flags must classify both rows as `rewind-retired` before any anchor resolver
path runs, so the result is hidden rows rather than resolver warnings.

## Purge and lifecycle

Retirements should follow the same append-mostly lifecycle as supersede rows and
ledger tombstones. Avoid sprinkling eager cleanup into every direct recording
deletion path unless the existing path already centralizes equivalent supersede
cleanup and can share a helper.

Add retirement cleanup where supersede rows are already cleaned up:

- `TreeDiscardPurge.PurgeTree`
  - remove retirements whose `RecordingId` or `RestoredRecordingId` belongs to
    the purged tree.
  - bump `SupersedeStateVersion` when any retirement row is removed.

- `LoadTimeSweep`
  - remove orphan retirements when the retired recording no longer exists.
  - keep a retirement when only `RestoredRecordingId` is missing, but log a
    warning and leave the retired recording hidden. That is safer than
    reactivating a fork with no known restored source.
  - bump `SupersedeStateVersion` when any retirement row is removed.

- direct recording deletion paths
  - first inspect the existing paths around single recording removal, chain
    removal, and clear-all. If they do not currently clean supersede/tombstone
    rows, do not invent retirement-only cleanup there.
  - if a path does centralize timeline-state cleanup, call one shared
    `PurgeRetirementsForRecordingIds(...)` helper and bump the visibility
    version exactly once.

- `RecordingStore.ResetAllPlaybackState`
  - do not clear retirements. They are timeline state, not transient spawn state.

## Ledger state

This visual fix should not attempt a broad ledger rewrite in the first patch.
The existing `fix-rewind-supersede-rollback` code already drops relation rows
without undoing tombstones, and the observed double-ghost reproduction does not
depend on ledger effects.

However, add a TODO in the plan/PR notes:

- A later ledger follow-up should evaluate whether tombstones written by the
  original `old -> new` Re-Fly merge need to be removed when `old` is restored,
  and whether actions owned by the retired `new` fork need retirement. That
  should be designed with concrete career-state repro logs, not folded into the
  rendering fix.

For this patch, do not add or remove `LedgerTombstone` rows.

## Tests

Add focused xUnit tests first.

### `Source/Parsek.Tests/RewindSupersedeRollbackTests.cs`

Extend the existing suite:

1. `Rollback_RetiresFork_WhenDroppingFutureSupersede`
   - setup `old -> new`, with `new.StartUT >= rewindUT`;
   - assert relation removed;
   - assert one retirement for `new`;
   - assert old is not retired.

2. `Rollback_DoesNotRetireFork_WhenForkBeforeRewindUT`
   - relation remains or drops according to the existing predicate;
   - no retirement for a fork before rewind.

3. `Rollback_DeduplicatesRetirement_WhenMultipleOldRowsPointToSameNew`
   - setup `oldRoot -> new` and `oldBranch -> new`;
   - both relations can drop;
   - only one retirement row for `new`.

4. `Rollback_MultiGeneration_RetirementPrecedence`
   - setup `A -> B` and `B -> C` with all post-rewind starts;
   - assert `A` is restored;
   - assert `B` and `C` are retired;
   - assert `B` is not also counted/logged as restored.

5. `Rollback_IgnoresUnrelatedTree`
   - unrelated relation remains;
   - no retirement created.

6. `ReapplyAfterLoad_RecreatesRetirement_WhenScenarioRestoresSupersede`
   - simulate first in-memory drop being lost across load;
   - call `ReapplyRewindSupersedeDropAfterLoad`;
   - assert relation removed and retirement exists.

7. `Rollback_Result_LogIncludesDroppedAndRetiredCounts`
   - use `ParsekLog.TestSinkForTesting`;
   - assert `[Rewind]` summary contains `dropped=`, `retiredForks=`, and owner.

### `Source/Parsek.Tests/EffectiveStateTests.cs`

Add:

1. `ComputeERS_FiltersRewindRetiredRecordings`
   - committed list contains old and retired new;
   - scenario has retirement for new;
   - ERS contains old only.

2. `ComputeERS_CacheInvalidated_WhenRetirementsChange`
   - compute ERS once;
   - add retirement and bump visibility version;
   - compute again and assert the retired row disappears.

### Playback policy tests

Use the existing `GhostPlaybackSkipReason` / policy test fixtures:

1. retired recording gets `skipGhost=true` and reason `rewind-retired`;
2. retired recording has `needsSpawn=false`;
3. retired recording that is a Relative anchor for another retired recording
   keeps both recordings hidden with `rewind-retired` and does not hit resolver
   error paths;
4. deferred spawn queues are purged for retired ids;
5. held ghosts and held-ghost retry state release for retired ids;
6. tracking-station spawn handoff refuses retired ids;
7. tracking-station ghost/map presence suppressed-id computation includes retired
   ids, not just relation-superseded ids.

### Fast-forward / action guard tests

Add direct action tests so UI hiding is not the only safety layer:

1. fast-forward to a retired recording id is refused with a `[Rewind]` or `[UI]`
   log reason;
2. stale watch/FF action dispatch does not enter watch mode for a retired row;
3. if a watched fork becomes retired while watch mode is active, the destroyed
   ghost fails `ValidateWatchedGhostStillActive()` and watch exits cleanly;
4. KSC/raw-list consumers do not surface retired rows as active timeline
   candidates.

### Serialization tests

Add save/load coverage:

1. `RecordingRewindRetirement.SaveInto` / `LoadFrom` round-trips all fields with
   `InvariantCulture`;
2. `ParsekScenario` writes and loads `RECORDING_REWIND_RETIREMENTS`;
3. `ReconciliationBundle.Capture` and restore round-trip retirements;
4. missing node loads as an empty list for older saves.

## Validation

Headless:

```bash
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter RewindSupersedeRollback
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
dotnet build Source/Parsek/Parsek.csproj
```

In-game smoke:

1. Start from the retained PR #776/#777 reproduction save if still available.
2. Reproduce the flow: original Kerbal X launch, controlled probe branch,
   Re-Fly the probe branch, merge, Rewind-to-Launch on root, enter watch mode.
3. Expected logs:
   - relation drop count is non-zero;
   - retired fork count is non-zero;
   - ERS rebuild logs `skippedRewindRetired=1`;
   - ghost playback logs the retired fork as `rewind-retired`;
   - only the restored source probe ghost renders.
4. Confirm no KSP process is closed or restarted by the agent. If KSP locks any
   runtime artifacts, ask the user to close/restart it manually.

In-game regression candidate:

- Add a small `InGameTests/RuntimeTests.cs` case that constructs or loads two
  committed recordings sharing a vessel PID plus a `RecordingRewindRetirement`
  row, then asserts the active playback pass spawns only the restored source
  ghost. This is the high-value end-to-end guard for the real repro shape; it
  should run from the in-game test runner (`Ctrl+Shift+T`) because headless xUnit
  cannot prove Unity ghost lifecycle behavior.

## Documentation updates for the implementation commit

When implementing the patch, update these in the same commit:

- `CHANGELOG.md`
  - add a bug-fix entry explaining that Rewind-to-Launch after a Re-Fly now
    retires the rewound-out fork so watch mode does not render both the source
    and the fork.

- `docs/dev/todo-and-known-bugs.md`
  - add the new observed bug under open/current work;
  - mark it done after the implementation lands;
  - update the earlier "Rewind-to-Launch left source ghost suppressed" entry if
    its fix wording currently implies relation-drop alone is sufficient.

- `docs/dev/plans/fix-rewind-supersede-rollback.md`
  - add a short follow-up note that relation-drop alone caused the PR #776/#777
    watch-mode double-probe case, and point to this plan.

## Out of scope

- Deleting fork recording sidecars.
- Rewriting ledger tombstones or game actions.
- Changing PR #776 relative-frame rendering.
- Changing PR #777 parent-anchored debris coverage behavior.
- General UI for comparing alternate futures.
