# Fix Plan: tree-rooted Rewind permanently hides CommittedProvisional priorTip

## Worktree

- Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-tree-rewind-supersede-old-side`
- Branch: `fix-tree-rewind-supersede-old-side`
- Base: `origin/main` at `8377b200` (Merge PR #838 reset-recorder-renderer-v0)
- Reproduction bundle: `logs/2026-05-13_2335_kerbal-x-booster-ghost-missing/`

## Problem

A user records the Kerbal X mission, Re-Flies the decoupled probe portion (crash → crash), seals the re-fly, then rewinds the tree-root Kerbal X recording to launch and enters Watch on it. The original probe ghost (`bc4390be…`) never appears. The user expected to see the probe ghost replay (either the original or the re-fly version) as Watch playback reached the probe's start time.

Log evidence (`KSP.log` line numbers):

- 67448–68755 — Re-Fly merge commits cleanly. Supersede `rsr_e87a36fe…`: `old=bc4390be… new=rec_3c0f…`. `mergeState=CommittedProvisional terminalKind=Crashed` (line 68430).
- 69289 — user clicks Seal on the re-fly slot.
- 69398 — `Sealed slot=1 rec=rec_3c0f… terminal=Destroyed reaperImpact=willReap reaped=1`. Seal sets `slot.Sealed=true` but does **not** flip the recording's `MergeState`.
- 69402 — user clicks the Group 'Kerbal X' Rewind button on `#0 "Kerbal X"`.
- 69418 — `User confirmed rewind to "Kerbal X" at UT 7.44`.
- 69423 — `UT adjusted: 7.4 → 0.0 (lead time 15s)` — `rewindAdjustedUT=0.0`.
- 69429–69432 — `Rewind supersede rollback: dropped=1 retiredForks=1 retiredOldSides=1 restored=1 rewindUT=0.0 owner='Kerbal X'`. Pass 1 retires the fork (`rec_3c0f`); Pass 2 retires the priorTip (`bc4390be`) because its `StartUT=23.6 > rewindAdjustedUT=0.0`.
- 70041, 70044 — after the scene reload: `Ghost playback skip state: #7 id=bc4390be… vessel="Kerbal X Probe" skip=True reason=rewind-retired … rewindRetired=True` (and the symmetric row for `rec_3c0f`).
- 70546 — user clicks W on Kerbal X. Watch mode enters. The probe ghost never spawns.

## Root cause

`RecordingStore.EnsureRewindRetirementsForRollback` Pass 2 (`Source/Parsek/RecordingStore.cs:5666-5720`) writes a permanent `RecordingRewindRetirement` row for every restored priorTip whose `StartUT > rewindAdjustedUT`, with no regard for the dropped supersede's `MergeState`. That was correct under the original design intent of [`fix-rewind-old-side-retirement.md`](fix-rewind-old-side-retirement.md), which targeted a 2026-05-10 playtest where a successful Re-Fly (Orbiting → `MergeState=Immutable`) was supposed to permanently retire the failed attempts.

But [`fix-rewind-canon-fork-retirement.md`](fix-rewind-canon-fork-retirement.md) later changed the pure rollback (`DropSupersedesRewoundOutOfExistenceDetailedPure`, `RecordingStore.cs:5180-5297`) so that supersedes with `NewRecording.MergeState == Immutable` no longer drop on a parent rewind — they go into `pendingImmutablePreservations` and are kept (the priorTip stays superseded via the surviving relation). With canon-forks in place, the only supersedes that reach `pendingDrops` are those whose fork is **NotCommitted** or **CommittedProvisional**, plus Immutable forks that get demoted because their own priorTip is itself a non-canon fork retired in the same batch.

Non-canon forks (`CommittedProvisional`) are by definition rewindable — the merge classifier set them that way precisely because the user might re-try (`TerminalKindClassifier.cs:39-53`, `SupersedeCommit.cs:479-481`). The user's playtest is exactly this shape: Crashed fork, sealed slot, `MergeState=CommittedProvisional`. Pass 2 hides the priorTip anyway, contradicting the "rewindable" contract.

That non-goal was explicitly acknowledged at the time:

> **`CommittedProvisional` forks.** Whether parent rewind should preserve those is a separate, more ambiguous design question (re-rewindable by definition). Not changed by this PR.
> — `fix-rewind-canon-fork-retirement.md` non-goals

This PR settles that question: on a tree-rooted Rewind that drops a non-canon supersede, the priorTip stays visible (un-superseded). Watch playback then spawns the original ghost when the active vessel reaches its endpoint, matching the user's mental model.

## Fix

Gate Pass 2's old-side retirement on the dropped supersede's fork `MergeState`. Only retire the priorTip when at least one dropped relation targeting it had a fork in `MergeState.Immutable`. For all-non-Immutable dropped relations (the user's case), skip the retirement and let the spawn-at-endpoint path show the priorTip naturally.

### Where exactly

`RecordingStore.EnsureRewindRetirementsForRollback`, in the Pass-2 loop at `RecordingStore.cs:5666-5720`. Insert the check after the existing `seenRetiredIds` / owner / `StartUT <= rewindAdjustedUT` guards, before the `retirement = new RecordingRewindRetirement { ... }` write.

### Decision logic

For each `oldSideId` reaching Pass 2:

1. Walk `rollback.DroppedRelations`; find every relation `rel` with `rel.OldRecordingId == oldSideId`.
2. For each such relation, look up `liveRecordingsById[rel.NewRecordingId]` (the fork). The fork is the New side of the dropped supersede.
3. If **any** of those forks is `MergeState.Immutable`, write the retirement (the previous PR #807 behavior). This is only reachable through the demotion path — see "Why an Immutable fork can still reach Pass 2" below.
4. Otherwise (all dropped relations targeting this old-side had non-Immutable forks, or the forks were missing from `liveRecordingsById`), skip the retirement.

### Why the "any Immutable" direction (vs "all Immutable")

A priorTip can in principle be the OldRecordingId of multiple dropped relations (fan-in). The PR #807 plan's "Field shape" note acknowledges this:

> Old-side recordings can be the OldRecordingId of multiple dropped relations (the user's case has only one per old recording, but the data structure allows fan-in). Picking one is misleading; null says "no single source rel."

If the fan-in mixes Immutable + non-Immutable forks, treating ANY Immutable as triggering retirement is the safer / more conservative choice — a single Immutable supersede in the priorTip's history is a "permanent replacement" the rewind shouldn't undo. This matches the canon-forks contract.

In practice the fan-in case is rare; the single-relation case (the user's case) is dominant. The "any" rule reduces to the obvious behavior in the single-relation case (one relation → check its fork only).

### Two distinct ways an Immutable fork can reach Pass 2

With canon-forks (`RecordingStore.cs:5227-5230`), pure pass 1 routes Immutable forks to `pendingImmutablePreservations`, not `pendingDrops`. So a "naked" Immutable fork never makes it into `DroppedRelations`. Two paths put an Immutable fork into `DroppedRelations`; they require different treatment in the new helper:

1. **Demotion** (`RecordingStore.cs:5251-5296`). A preserved Immutable relation whose `OldRecordingId` is itself the `NewRecordingId` of a non-Immutable drop in the same batch is moved to `pendingDrops` and tagged in `DemotedImmutablePreservationIds`. Example chain: `A(NotCommitted)→B(CommittedProvisional)→C(Immutable)` with parent rewind on A demotes the B→C preservation because B is being retired as a non-canon fork.

   In production demotion shapes the demoted-Immutable fork is retired in Pass 1 (intentional drop path). The priorTip of the demoted relation (B in the example) is *also* a Pass-1 fork retirement, so when Pass 2 iterates it, `seenRetiredIds.Contains(oldSideId)` short-circuits **before** the new fork-MergeState check ever runs. The new "any Immutable fork ⇒ retire" branch is therefore not load-bearing for the demotion case in production.

2. **Forced self-rewind on canon** (`RecordingStore.cs:5216-5221`). The user explicitly rewinds the canon fork itself; the relation lands in `pendingDrops` with `ForcedSelfRewindDropIds.Add(rel.NewRecordingId)`. Pass 1 retires the fork via the `wasForcedSelfRewindDrop` intentional path (`RecordingStore.cs:5575-5577`).

   The priorTip in this case is **not** a fork being retired in the same batch (the user is undoing the canon, not rewinding past its priorTip). So `seenRetiredIds` does **not** short-circuit. The new "any Immutable fork" rule would otherwise retire the priorTip — which is the opposite of the user's intent (they want the priorTip to become the visible canon again). **This is where the `ForcedSelfRewindDropIds` carve-out in the new helper is load-bearing.**

Net: of the two paths an Immutable fork can take into `DroppedRelations`, demotion is shadowed by `seenRetiredIds` and self-rewind needs the explicit skip. The new helper handles both correctly.

Test #6 below pins the canonical demotion-shadowing behavior (no Pass-2 retirement reached). Test #9 pins the self-rewind skip. Test #10 covers the helper's truth table directly.

### Orphan-fallback (`newRec == null`)

Pure pass 1 routes orphan relations (fork missing from `liveRecordingsById`) directly to `pendingDrops` without an Immutable check (`RecordingStore.cs:5224-5230` comment). Under my fix, Pass 2 looking up the fork sees `null` → cannot confirm Immutable → skips retirement. That's the conservative choice: an orphan relation means the supersede already lost its fork; the priorTip is the only candidate left to show, so don't hide it. Existing test `OrphanRow_DropsByRelUT_WhenForkMissingFromLiveDict` (line 622) currently passes through Pass 2 with `seenRetiredIds.Contains` skip (because the fork id is in `RetiredForkRecordingIds` even though there's no live recording) — verify with a focused test (#5 below) that the orphan path doesn't regress.

### Self-rewind on canon

`ForcedSelfRewindDropIds` (`RecordingStore.cs:5216-5221`) tracks the case where the user explicitly rewinds the canon fork itself. The fork (Immutable) goes to pendingDrops with `wasForcedSelfRewindDrop=true`. Pass 1 retires it normally (intentional drop). Pass 2 reaching the priorTip: under my "any Immutable fork" rule, the priorTip would be retired. **That's wrong for self-rewind**: the user explicitly chose to undo the canon, so the priorTip should be the new visible state.

I'll widen the skip rule: skip retirement when **all** the dropped relations targeting the old-side have either (a) non-Immutable forks, or (b) Immutable forks marked as `ForcedSelfRewindDropIds`. Equivalently: retire only when at least one dropped relation has an Immutable fork that was **not** a forced self-rewind drop.

The cleanest implementation is to compute the boolean by iterating dropped relations once.

## Pure vs live boundary

No change to `DropSupersedesRewoundOutOfExistenceDetailedPure`. The pure function still emits `RetiredForkRecordingIds`, `RestoredRecordingIds`, `DroppedRelations`, `DemotedImmutablePreservationIds`, `ForcedSelfRewindDropIds` as today. The fix is contained in `EnsureRewindRetirementsForRollback` and consumes data already on the rollback result.

`EnsureRewindRetirementsForRollback`'s signature stays the same — `liveRecordingsById`, `ownerRecordingId`, `rollback`, `scenario`, `rewindAdjustedUT` are all already in scope. I'll factor the per-old-side decision into a small private helper for testability:

```csharp
// internal (not private) so the xUnit truth-table test (#10) can call it
// directly without going through DropSupersedesRewoundOutOfExistence.
internal static bool AnyDroppedRelationRetiresPriorTipPermanently(
    string oldSideId,
    RewindSupersedeRollbackResult rollback,
    IReadOnlyDictionary<string, Recording> liveRecordingsById)
{
    for (int i = 0; i < rollback.DroppedRelations.Count; i++)
    {
        var rel = rollback.DroppedRelations[i];
        if (rel == null
            || !string.Equals(rel.OldRecordingId, oldSideId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(rel.NewRecordingId))
            continue;

        // Forced self-rewind drops are the user explicitly undoing a canon;
        // priorTip should become the new visible state.
        if (rollback.ForcedSelfRewindDropIds.Contains(rel.NewRecordingId))
            continue;

        Recording forkRec;
        if (liveRecordingsById != null
            && liveRecordingsById.TryGetValue(rel.NewRecordingId, out forkRec)
            && forkRec != null
            && forkRec.MergeState == MergeState.Immutable)
        {
            return true;
        }
    }
    return false;
}
```

## Logging

- **Per-row** (Verbose, when a skip happens): `[Rewind] Old-side retirement skipped for rec={id} reason=fork-non-immutable rewindUT={ut} owner='{name}'` — observable in playtest logs so a future "wait, where's my retirement?" question is answerable.
- **Summary** (Info, in the existing rollback summary line at `RecordingStore.cs:5452-5460`): add `skippedNonImmutableOldSides={n}` alongside `retiredOldSides`. Keeps grep-friendly diagnostics.

Both lines fire only when `dropped > 0 || retired > 0 || retiredOldSides > 0 || skippedNonImmutable > 0 || ...` so a no-op rollback stays silent.

## Tests

### Update existing tests

1. **`LiveRollback_RetiresOldSides_WhenAllStartAfterRewindUT`** (line 309-383). The test claims to model the 2026-05-10 playtest (header comment line 312), but the *actual* playtest from that bundle had Orbiting (`MergeState.Immutable`) forks. Under canon-forks, those forks would now be routed to `pendingImmutablePreservations` and the relations wouldn't drop at all — meaning the 2026-05-10 playtest's shape no longer reaches Pass 2 in production. The test sidesteps this by using `MakeRec` with the default `MergeState.NotCommitted`, which bypasses canon-forks preservation but no longer corresponds to anything that happens in real saves. The test was already partly stale before this PR; the rewrite makes it explicit.

   Changes:
   - Rename to `LiveRollback_DoesNotRetireOldSides_WhenForksAreNotCommitted`.
   - Update the header comment to acknowledge the playtest-shape mismatch and point readers at test #5 below for the *current* canonical regression guard.
   - Assert `1` total retirement (the fork F only), not 4.
   - Drop the `Reason == RewoundOutOldSideReason` assertions.
   - Replace the smoking-gun `IsRewindRetired(probeDestroyed, …) == true` with `IsRewindRetired(…) == false` and document that the priorTip is intentionally visible after rollback so spawn-at-endpoint can replay it.
   - Replace the `retiredOldSides=3` log assertion with `retiredOldSides=0 skippedNonImmutableOldSides=3`.

2. **`LiveRollback_DeduplicatesRetirement_WhenMultipleOldRowsPointToSameNew`** (line 264). Existing assertion `Assert.Single(scenario.RecordingRewindRetirements)` — already implies the single fork retirement, matching new behavior. Double-check by reading the test body; it likely needs no change beyond an updated comment that explains why old-sides are not also retired (per the new contract).

3. **`LiveRollback_KeepsOwnerVisible_WhenOwnerWasOldSide`** (line 385). Currently asserts the owner-skip path in Pass 2. With NotCommitted fork (test default), the new rule already skips via the fork-MergeState gate before reaching the owner skip. To still exercise the owner-skip path, change the test's fork to `MergeState.Immutable` (via demotion) so retirement would otherwise fire, then assert the owner-skip suppresses it. Document the explicit MergeState wiring.

4. **`LiveRollback_KeepsOriginAtBoundary_WhenStartUTEqualsRewindUT`** (line 419). Unchanged in expectations — B and C still retire via Pass 1 (fork retirement), no old-side retirement reached.

### New tests

5. **`LiveRollback_DoesNotRetireOldSide_WhenForkIsCommittedProvisional`** — directly mirrors `logs/2026-05-13_2335`. Owner = parent rocket (`A`, StartUT=7.44); fork F (StartUT=24.42, `MergeState.CommittedProvisional`); priorTip B (StartUT=23.6) superseded by F. `rewindAdjustedUT=0.0` (lead-time gap). Expectations:
   - `dropped=1`, `RecordingSupersedes` empty.
   - 1 retirement (F as fork), 0 old-side retirements.
   - `EffectiveState.IsRewindRetired(B, …) == false`.
   - Log contains `Old-side retirement skipped for rec=B reason=fork-non-immutable` and the summary line shows `skippedNonImmutableOldSides=1`.

6. **`LiveRollback_RetiresOldSide_WhenForkIsImmutableViaDemotion`** — chain `A(non-canon)→B(CommittedProvisional), B→C(Immutable)` with parent rewind on A. Pure pass 1 puts A→B in pendingDrops, B→C in pendingImmutablePreservations. Pure pass 2 demotes B→C (B is in pendingRetiredNewIds). After demotion: DroppedRelations = [A→B, B→C], DemotedImmutablePreservationIds = {C}. Live Pass 1 retires B (CommittedProvisional) and C (intentional drop). Live Pass 2 iterates RestoredRecordingIds = {A, B} — A is owner-skipped, B is seenRetiredIds-skipped. **No old-side retirement reaches the new rule**. So this test asserts the canonical demotion case doesn't regress: no old-sides retired by Pass 2 (because the priorTip overlaps with a fork retirement). Doc string explains why the "Immutable-fork-permits-retirement" branch is in fact unreachable in production demotion shapes.

7. **`LiveRollback_RetiresOldSide_WhenForkIsImmutable_SyntheticDemoted`** — a synthetic constructed shape where the "Immutable fork in DroppedRelations" branch IS reached. Manually inject a relation whose New is Immutable and NewRecordingId not in any pendingRetiredNewIds-equivalent — i.e. inject via `DemotedImmutablePreservationIds.Add` directly through a test seam, OR construct two-pass-equivalent state manually. The test exercises the new conditional path so it's covered.
   - Simpler alternative: assert via the helper method directly (call `AnyDroppedRelationRetiresPriorTipPermanently` with a synthetic `RewindSupersedeRollbackResult` and verify true/false outcomes for the 4 combinations: Immutable-not-forced, Immutable-forced, CommittedProvisional, NotCommitted, orphan).
   - Prefer the direct-helper test (cheaper, more focused).

8. **`LiveRollback_OrphanRelation_DoesNotRetireOldSide`** — relation whose NewRecordingId has no entry in `liveRecordingsById`. Assert: relation drops, no fork retirement (fork missing), no old-side retirement (can't confirm Immutable).

9. **`LiveRollback_SelfRewindOnCanon_DoesNotRetireOldSide`** — user rewinds the Immutable fork itself. Pure pass 1 puts it in pendingDrops with `ForcedSelfRewindDropIds.Add(F)`. Live Pass 1 retires F (intentional). Live Pass 2 reaches the priorTip → my rule sees `ForcedSelfRewindDropIds.Contains(F)` → skip. Assert: 1 retirement (F), no old-side retirement.

10. **`AnyDroppedRelationRetiresPriorTipPermanently_HelperContract`** — small focused test on the helper. Six cases:
    - empty `DroppedRelations` → false
    - relation pointing at a different old-side → false
    - Immutable fork, not forced self-rewind → true
    - Immutable fork, forced self-rewind → false
    - CommittedProvisional fork → false
    - NotCommitted fork → false
    - fork missing from liveRecordingsById → false

### Existing test compatibility

Rough impact on the rest of `RewindSupersedeRollbackTests.cs` based on file skim (will verify during implementation):

- The "preservation / demotion" tests (lines 787-1004) operate on pure-function output and don't assert on old-side retirement; expect no change.
- Tests like `Pure_HandlesNullOwnerTreeRecordings_OwnerOnlyDrop` (line 595) don't reach Pass 2; expect no change.
- `ReapplyRewindSupersedeDropAfterLoad_*` tests (lines 460-522) exercise the second call site of the same rollback helper. The fix is automatic — `EnsureRewindRetirementsForRollback` is the same helper used by both call sites — but I'll re-verify each reapply test's assertions match the new behavior.

I'll grep the entire `Source/Parsek.Tests/` tree for the following strings during implementation and update each call site:

- `RewoundOutOldSideReason` (string + symbol)
- `retiredOldSides=`
- `Retired rewound-out old-side rec=` (the per-row log assertion)
- `IsRewindRetired(probeDestroyed`, similar smoking-gun checks on the test priorTips.

## Touch points outside the helper

- **Cache invalidation** (`RecordingStore.cs:5445-5446`). The existing `if (dropped > 0 || retired > 0 || retiredOldSides > 0)` cache-bump condition still fires correctly: when the fix turns a former retirement into a skip, `dropped` is still positive (the supersede was still dropped), so the cache invalidates correctly. No change needed.
- **`EffectiveState.ComputeRewindRetiredRecordingIds`** doesn't distinguish retirements by `Reason`. Removing old-side rows simply lets ERS show those recordings. No upstream filter relies on `Reason` distinctions beyond `LoadTimeSweep`'s legacy-Immutable-canon defense (`LoadTimeSweep.cs:645-687`) which uses `RewoundOutOldSideReason` as a tag — keeping the constant intact preserves that defense.
- **`ReconciliationBundle`** capture/restore round-trips the retirements list as-is. No data-shape change.
- **`TreeDiscardPurge`** removes retirements when their tree is discarded. Same code path for all reasons. No change.
- **Log surface**: only the per-rollback summary line at `RecordingStore.cs:5452-5460` and the per-row line at `RecordingStore.cs:5716-5719` are affected. Plus the new skip log and the new sweep log.

## Backward compatibility — one-shot load-time sweep required

Existing saves authored under PR #807 (or earlier in this fix cycle) carry stale `RecordingRewindRetirement` rows with `Reason = RewoundOutOldSideReason` for priorTips whose forks were non-Immutable. **`LoadTimeSweep.SweepOrphanRewindRetirements` will NOT remove those rows:** its orphan branch (`LoadTimeSweep.cs:615`) only fires when the retired recording is missing from `committedRecordings`. The user's reproduction priorTip (`bc4390be…`) is a fully live committed recording, so the existing sweep leaves the retirement in place forever. The ghost stays hidden across every subsequent load, with no in-game recovery path.

I'll add a **one-shot legacy sweep** to `LoadTimeSweep` that removes `RewoundOutOldSideReason` rows when the priorTip recording is alive. Justification:

- Under the new rule, no production-shape rollback writes an `RewoundOutOldSideReason` row for a live priorTip — that branch is unreachable in production demotion (shadowed by `seenRetiredIds`) and the self-rewind path explicitly skips it.
- Therefore every existing live-priorTip `RewoundOutOldSideReason` row in any save **was** written by the buggy pre-fix code path. Removing them across the board is correct.
- For orphan priorTip rows (priorTip recording absent from `committedRecordings`), the existing orphan sweep already removes them. No change there.
- The sweep is idempotent — once a save is loaded under the new code, the stale rows are gone and the sweep is a no-op on subsequent loads.

The sweep lives in `LoadTimeSweep.cs`. Sketch:

```csharp
private static int SweepLegacyLivePriorTipOldSideRetirements(
    ParsekScenario scenario,
    IReadOnlyDictionary<string, Recording> committedById)
{
    if (scenario?.RecordingRewindRetirements == null
        || scenario.RecordingRewindRetirements.Count == 0)
        return 0;

    int removed = 0;
    for (int i = scenario.RecordingRewindRetirements.Count - 1; i >= 0; i--)
    {
        var ret = scenario.RecordingRewindRetirements[i];
        if (ret == null
            || !string.Equals(ret.Reason,
                RecordingRewindRetirement.RewoundOutOldSideReason,
                StringComparison.Ordinal))
            continue;

        // Only sweep live-priorTip rows; orphan rows go through the
        // existing orphan branch and keep their current semantics.
        if (string.IsNullOrEmpty(ret.RecordingId)
            || !committedById.ContainsKey(ret.RecordingId))
            continue;

        scenario.RecordingRewindRetirements.RemoveAt(i);
        removed++;
    }

    if (removed > 0)
    {
        scenario.BumpSupersedeStateVersion();
        if (!SuppressLogging)
            ParsekLog.Info("LoadSweep",
                $"Legacy live-priorTip old-side retirement sweep: removed={removed.ToString(CultureInfo.InvariantCulture)} " +
                $"(pre-fix rows written by deprecated Pass-2 retirement; see fix-tree-rewind-supersede-old-side.md)");
    }
    return removed;
}
```

Call it from the existing `LoadTimeSweep` driver alongside `SweepOrphanRewindRetirements`. Include the removed count in the existing summary log at `LoadTimeSweep.cs:`*(wire into the existing summary line — verify exact line during implementation)*.

**Tests for the sweep:**

11. **`LoadTimeSweep_RemovesLegacyOldSideRetirement_WhenPriorTipIsLive`** — synthetic scenario: live priorTip recording in `committedRecordings`, retirement row with `RewoundOutOldSideReason` pointing at it. Run the sweep. Assert: row removed, supersede cache bumped, log line emitted.
12. **`LoadTimeSweep_KeepsLegacyOldSideRetirement_WhenPriorTipIsOrphan`** — synthetic scenario: retirement row with `RewoundOutOldSideReason` whose `RecordingId` is NOT in `committedRecordings`. Assert: legacy sweep does NOT remove it (the orphan sweep handles it via the existing path).
13. **`LoadTimeSweep_IdempotentAcrossMultipleLoads`** — run the legacy sweep twice in succession. Second call is a no-op.

I'm being explicit that this sweep is a *one-shot* recovery for pre-fix saves; the long-term invariant (post-fix) is that no live-priorTip `RewoundOutOldSideReason` rows are ever written. If a future PR re-introduces a legitimate use of this reason for live priorTips, the sweep would need to be guarded or replaced. Adding a single TODO comment at the sweep entry suffices.

### Guard: multi-old-side-to-one-Immutable-fork pre-canon-forks shape

(Added in response to two review passes after the first commit landed.)

Pre-canon-forks saves can carry a particular legacy shape that the new sweep would regress if removed unconditionally. Concretely:

- A successful Re-Fly committed `MergeState.Immutable` fork `F` that superseded **multiple** priorTips `P1`, `P2`, `P3` in the same merge batch.
- The pre-canon-forks `Pass-1` buggy code retired `F` (writing one `DefaultReason` retirement whose `RestoredRecordingId` named *one* priorTip, e.g. `P1`).
- PR #807's old-side code wrote three per-priorTip `RewoundOutOldSideReason` rows for `P1`, `P2`, `P3`.

The existing `LoadTimeSweep.RetirementPointingAtImmutable_RemovedAndSupersedeRestoredAtLoadTime` cleanup reconstructs **only one** supersede relation per fork retirement (the one named in `RestoredRecordingId`). So on load, `F→P1` is restored, but `F→P2` / `F→P3` are not. Under the original (pre-this-PR) behavior, `P2` and `P3` were kept hidden by their own old-side rows — that was the only remaining suppression mechanism.

If the new non-Immutable old-side sweep removes those rows, `P2` and `P3` become visible as "Destroyed" outcomes — re-introducing the exact regression PR #807 fixed.

**First attempt (rejected by the second review): non-durable guard.** A pre-loop scan for any `DefaultReason` fork retirement on a live Immutable recording. This was non-durable — the same per-row loop that follows the scan *removes* that fork retirement, so after the save persists, the next load's scan finds nothing and the new sweep wrongly fires, removing `P2`/`P3`'s rows.

**Final design — durable two-signal guard, second-pass structure.** The new non-Immutable old-side sweep runs as a separate second pass *after* the per-row loop completes (and after the legacy-Immutable cleanup has reconstructed `F→P1`). The guard `deferLegacyOldSideSweep` keys on signals that survive the save:

1. **`deferImmediate`** — `removedImmutableRetirements > 0`: the per-row loop *just removed* at least one legacy Immutable fork retirement this load. Covers load 1, including the case where `TryRestoreLegacyImmutableSupersede` could not reconstruct the relation (no durable signal would then exist, so load 1 is the only chance to defer).
2. **`hasSurvivingImmutableSupersede`** — a `RecordingSupersedeRelation` whose `NewRecordingId` resolves to a live `MergeState.Immutable` recording. On load 1 (reconstruction success) this is the `F→P1` relation the per-row loop just reconstructed; on load 2+ it is the same relation, persisted. **This is the durable signal.**

Either signal defers the entire second pass.

```csharp
bool deferLegacyOldSideSweep = removedImmutableRetirements > 0;
if (!deferLegacyOldSideSweep && scenario.RecordingSupersedes != null)
    foreach (rel in scenario.RecordingSupersedes)
        if (rel?.NewRecordingId resolves to a live Immutable recording)
        { deferLegacyOldSideSweep = true; break; }

// second pass over RewoundOutOldSideReason rows on live non-Immutable priorTips
foreach (retirement reverse)
    if (deferLegacyOldSideSweep) { count candidate; continue; }
    else { remove; }
```

**Residual gap (documented, accepted):** a load-2+ visit of a save whose load-1 reconstruction *failed* (`MissingMetadata` / `RestoredRecordingMissing`) has neither signal. But that save is already degraded — load 1 logged `priorTip may render alongside canon, investigate` — so the second pass sweeping is not making a healthy save worse. There is no per-row fork metadata on `RewoundOutOldSideReason` rows to do better; recording it would be a schema change out of scope here.

Post-fix saves never produce a legacy `DefaultReason` retirement on an Immutable recording (canon-forks routes Immutable forks to `pendingImmutablePreservations` instead of `pendingDrops`), so the `deferImmediate` signal is effectively legacy-save-only. The `hasSurvivingImmutableSupersede` signal *can* fire on a healthy post-fix save with a legitimate Immutable supersede — but such a save has zero `RewoundOutOldSideReason` rows to sweep (the new Pass-2 write site is unreachable in production), so deferral is a harmless no-op there.

The user's reproduction scenario (`logs/2026-05-13_2335`) has only a `CommittedProvisional` fork whose relation was *dropped* by the rewind — no Immutable retirements, no surviving Immutable supersede — so neither signal fires and the user's bug recovers cleanly on every load.

A `[LoadSweep] Legacy non-Immutable old-side sweep deferred: N candidate row(s) retained …` Info line records when the guard kicks in (only when there were actually candidate rows to consider).

**Test #14**: `LegacyOldSideSweep_DeferredAndDurableForMultiOldSideToImmutableForkShape` constructs the exact `F→{P1,P2,P3}` multi-old shape and runs the sweep **twice** on the same scenario object (a faithful load-1 / load-2 simulation, since the first run mutates `RecordingSupersedes` + `RecordingRewindRetirements` in place):

- Load 1: `deferImmediate` fires (`F`'s retirement was just removed). `F`'s retirement gone, `F→P1` reconstructed, `P1`/`P2`/`P3` rows survive, deferral log fires.
- Load 2: `F`'s retirement is already gone; `hasSurvivingImmutableSupersede` fires on the persisted `F→P1` relation. `P1`/`P2`/`P3` rows **still** survive — no `Removing legacy …` line for them. This is the regression the durable guard prevents.

## CHANGELOG / docs

- `CHANGELOG.md` under `## 0.9.x / ### Bug Fixes`: one-line entry describing the visible behavior change (priorTip ghost visible during Watch after parent Rewind of a Crashed/Provisional re-fly).
- `docs/dev/todo-and-known-bugs.md`: add a Done entry citing `logs/2026-05-13_2335_kerbal-x-booster-ghost-missing` and the design decision.
- `docs/dev/plans/fix-rewind-canon-fork-retirement.md`: append a short note that the CommittedProvisional non-goal has been resolved by this PR (point to this doc).
- This file (`fix-tree-rewind-supersede-old-side.md`): the design record.

No new in-game test. The xUnit coverage above plus the synthetic helper test pins the new contract; ghost-spawn-at-endpoint is already covered by separate Unity in-game tests for the broader spawn machinery, and a tree-rewind-then-watch end-to-end test would duplicate that with extra fixture cost.

## Out of scope

- Ledger / tombstone rollback when the supersede is dropped. Tombstones written by the original Re-Fly merge are not touched. (Same out-of-scope as PR #807 and the canon-forks fix.)
- Recordings-table affordances for the now-visible priorTip (e.g. an "originally Crashed, see also re-fly attempt" hint). The row simply re-appears with its existing terminal state.
- Deleting fork sidecars for retired forks. Pass 1's retirement still hides the fork from playback; sidecar cleanup is its own thing.
- A separate "permanent vs reversible" UI distinction in the table. Today retirement is binary (`rewind-retired`); making it semantic is future work.

## Validation

Headless:

```bash
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName~RewindSupersedeRollback"
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName!~InjectAllRecordings"
cd Source/Parsek && dotnet build
```

In-game smoke (against the user's reproduction save `saves/x1`):

1. Load `quicksave.sfs` (post-rewind, post-seal state).
2. Click W on the `Kerbal X` row.
3. Expect: probe ghost spawns at UT ~23.6 alongside the debris ghosts.
4. Verify the recordings table shows the `Kerbal X Probe` row alongside `Kerbal X`.
5. Verify the log contains `Old-side retirement skipped for rec=bc4390be… reason=fork-non-immutable` for the live-session rollback, and the summary line shows `skippedNonImmutableOldSides=1`.
6. Re-test the canonical "successful re-fly + parent rewind" flow if the playtest save still has one available, to confirm canon-forks preservation still hides the priorTip via supersede relation (no regression on the PR #807 user's case).

## Risk summary

- **Low to medium.** The change is contained in one function in `RecordingStore.cs` with a small new helper. The existing canon-forks fix already covers the Immutable-supersede case via a different path (preservation, not retirement), so removing the broad Pass-2 retirement for non-canon forks has minimal effect on production-shape scenarios. The main behavior change visible to users is "after parent-rewind of a Crashed Re-Fly, the priorTip row reappears in the recordings table and the ghost replays in Watch" — which is the intended fix.
- The test updates touch existing assertions that asserted the now-changed behavior. The PR will need careful review of the diff in `LiveRollback_RetiresOldSides_WhenAllStartAfterRewindUT` to confirm the semantic is preserved (the test still validates Pass 2's iteration; it just asserts a no-retirement outcome instead of retirement).
