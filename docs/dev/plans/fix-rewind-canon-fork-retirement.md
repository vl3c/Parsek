# Fix: parent Rewind retires canon (Immutable) Re-Fly forks

## Worktree

- Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-rewind-canon-forks`
- Branch: `fix-rewind-canon-forks`
- Base: `origin/main` at `94d4e7c2`
- Reproduction bundle: `logs/2026-05-10_1713/`

## Bug evidence

User flow in the reproduction bundle:

1. Launch `Kerbal X` (recording `1354326d…`). Decouple `Kerbal X Probe` at UT 456; the
   Probe is destroyed (`32d9674c…`, `terminal=Destroyed`).
2. Click Re-Fly on the Probe RP (`rp_c6c6efeb`). New fork `rec_b1566…` is recorded;
   the Probe reaches a stable orbit (`terminal=Orbiting` at UT ~979).
3. Re-Fly merge auto-commits at 17:05:49. The fork is sealed
   `mergeState=Immutable` and supersedes the Destroyed priorTip
   (`Supersede rel=rsr_ec217fa1… old=32d9674c… new=rec_b1566…`).
4. Boring-tail trim shortens `rec_b1566.endUT` to UT 992.2.
5. The live Probe vessel is committed to the persistent state — visible in
   `saves/s14/quicksave.sfs` (timestamped 17:05) as a real
   `VESSEL { name = Kerbal X Probe; sit = ORBITING; ORBIT { SMA = 1186923; ECC = 0.323 } … }`
   with `persistentId = 2823934496` matching `rec_b1566.vesselPersistentId`. The merge
   produced the canon orbital vessel exactly as designed.
6. User goes to SPACECENTER, then clicks the **`Group 'Kerbal X' Rewind`** button at
   UT 301.9 (parent tree's root, NOT the Probe).
7. `RewindInvoker` adjusts UT 301.9 → 286.9 and loads the parent's pre-launch
   `parsek_rw_6d0aec.sfs` (a quicksave from before the Probe ever existed).
8. `RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure` drops the
   supersede relation `32d9674c… → rec_b1566…` because `rec_b1566.StartUT = 456.79
   > rewindUT = 286.9`. The drop puts `rec_b1566` into
   `RetiredForkRecordingIds` and `32d9674c` into `RestoredRecordingIds`.
9. `EnsureRewindRetirementsForRollback` writes a `RecordingRewindRetirement` for
   `rec_b1566`. Log: `[Rewind] Retired rewound-out fork rec=rec_b1566… restored=32d9674c… rewindUT=286.9`.
10. After the rewind reload + post-rewind FLIGHT scene (17:07:19), the Spawner sees
    `rewindRetired=true` for `rec_b1566` and suppresses spawn:
    `[Flight] Ghost playback skip state: #11 … rewindRetired=True` →
    `[Spawner] Spawn suppressed for #11 "Kerbal X Probe":` (empty reason text).
11. Post-rewind `persistent.sfs` (17:13) has no Kerbal X Probe vessel — only the
    canary ghost and the new prelaunch craft. The canon orbital Probe is gone.

The root recording (`32d9674c…`) is also wrongly restored (un-superseded) by step
8/9, contradicting the merge: an Immutable merge means the priorTip is
permanently retired, but the rewind path treats `Supersede` as undoable.

## Root cause

`RecordingStore.DropSupersedesRewoundOutOfExistenceDetailedPure`
(`Source/Parsek/RecordingStore.cs:4774`) selects relations to drop solely on:

- `rel.OldRecordingId ∈ rewoundOutOldIds` (owner subtree at-or-after rewindUT), and
- `liveRecordings[rel.NewRecordingId].StartUT >= rewindUT` (or `rel.UT` fallback).

It does **not** consult `Recording.MergeState`. So a fork sealed at
`MergeState.Immutable` (canon — sealed by `SupersedeCommit` after a stable terminal
classifier closure) is dropped + retired identically to a `NotCommitted` /
`CommittedProvisional` provisional.

The downstream consequence chain:

- `EnsureRewindRetirementsForRollback` (RecordingStore.cs:4944) writes the
  retirement.
- `EffectiveState.ComputeRewindRetiredRecordingIds` reads it.
- `ParsekFlight.ComputePlaybackFlags` sets `skipGhost=true reason=rewind-retired`.
- `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` (or the spawner gate calling it)
  refuses spawn because the recording is timeline-inactive.
- `RecordingSupersedeRelation` row is gone, so `EffectiveState.ComputeERS`
  un-suppresses `32d9674c…` — both the original Destroyed Probe recording AND
  the (now-retired) Orbiting Probe recording exist in the timeline view.

## Why the prior fix (`fix-watch-double-probe-ghost-after-rewind.md`) is the wrong shape for canon forks

The double-ghost fix added retirement so that, when a relation drops, the fork
also becomes invisible — preventing the source recording AND the fork from both
rendering simultaneously. That is correct for **non-canon** forks
(`NotCommitted` / `CommittedProvisional`), where the parent rewind genuinely
invalidates the fork.

For **canon (`Immutable`) forks** the right answer is the opposite: the relation
must NOT drop. The fork remains canon, the priorTip remains superseded, no
double ghost arises because the source is still hidden. Retirement is then a
non-issue (it's only ever written for forks we actively dropped).

The contract the merge advertises is on `SupersedeCommit.cs`:

> `MergeJournalOrchestrator.RunFinisher` flips MergeState to Immutable when the
> classifier closes a stable terminal (`Orbiting`/`Landed`/`Splashed`/etc.)…
> Immutable = sealed forever; the rewind slot is closed.

If a parent rewind can later un-supersede an Immutable fork, "sealed forever" is
a lie. This PR makes the rewind path honor the contract.

## Goals

1. Parent-tree Rewind never drops a `RecordingSupersedeRelation` whose
   `NewRecordingId` resolves to a recording with `MergeState == Immutable`.
2. Therefore no `RecordingRewindRetirement` is written for a canon fork by
   `EnsureRewindRetirementsForRollback`.
3. After the rewind reload, the canon recording stays:
   - timeline-visible (not `rewind-retired` / not `superseded-by-relation`);
   - spawn-eligible (`ShouldSpawnAtRecordingEnd` returns `(true, "")`).
4. The first time the user re-enters FLIGHT after the rewind and the canon
   fork's ghost reaches `PastEffectiveEnd`, the spawn-at-recording-end path
   re-materializes the orbital ProtoVessel. The user gets their Probe back.
5. The non-canon double-ghost fix continues to work for `NotCommitted` and
   `CommittedProvisional` forks (existing behavior; existing tests stay green).

## Non-goals

- **Live persistent vessel preservation through the rewind reload itself.** The
  rewind quicksave by definition predates Re-Fly forks (it's anchored at the
  parent's recording-start UT). The canon vessel disappears from the world
  immediately after the reload — but the recording's spawn-at-end mechanism
  re-creates it once the user re-enters flight and the ghost runs to its
  endpoint. We do NOT inject the fork's spawned ProtoVessel into the loaded
  rewind quicksave; that's a much larger change touching save round-trip,
  PartLoader timing, and orbit-frame conversion. Out of scope here.

  **UX gap acknowledged:** between the rewind reload and the moment the
  user's re-flight progresses through the canon fork's `EndUT`, the canon
  vessel is invisible — no Tracking Station entry, no map marker, no flight
  scene presence. Only the recording row in the table indicates it exists.
  This is jarring but consistent with "spawn at recording end is lazy."
  A follow-up could add a "pending materialization" badge on the recording
  row or a TS map placeholder; that's deferred.
- **`CommittedProvisional` forks.** Whether parent rewind should preserve those
  is a separate, more ambiguous design question (re-rewindable by definition).
  Not changed by this PR.
- **Ledger rollback.** `LedgerTombstone` rows written by the original
  `Supersede` are not touched. The merge already chose to tombstone certain
  actions; preserving the supersede preserves the tombstones too, which is
  consistent.
- **UI affordance changes.** Recordings table / group hierarchy already correctly
  display Immutable canon forks; no change needed there.
- Rewinding past a `CommittedProvisional` with a stable terminal but
  unsealed slot — falls under the "CommittedProvisional" non-goal above.

## Fix

Two-pass change in the pure rollback function. The first pass tags candidate
preservations; the second pass enforces a "no-orphaned-canon" invariant by
dropping any Immutable preservation whose `Old` is itself being retired in this
same batch. Plus counters and log lines so the behaviour is observable.

The single-pass shortcut from an earlier draft of this plan is REJECTED — see
the mixed-chain analysis in "Edge cases" below — because it produces a
double-materialization shape (A AND C both visible) that re-introduces the
exact regression the prior `fix-watch-double-probe-ghost-after-rewind` PR
existed to prevent.

### `RewindSupersedeRollbackResult` (RecordingStore.cs:57)

Add:

```csharp
public int SkippedImmutableForkCount;
public readonly HashSet<string> SkippedImmutableForkRecordingIds =
    new HashSet<string>(StringComparer.Ordinal);
```

so the live caller can log a single observable summary.

### `RecordingStore.cs:4774` `DropSupersedesRewoundOutOfExistenceDetailedPure`

Two-pass logic:

**Pass 1** — same as today, but classify each candidate drop into one of
three buckets instead of dropping unconditionally:

- `pendingImmutablePreservations`: relation whose `newRec.MergeState ==
  Immutable` (canon fork → tentatively preserve).
- `pendingDrops`: relation whose new fork is non-Immutable or orphan-fallback
  (drop + retire as today).

**Pass 2** — invariant enforcement on the tentative preservations.

The set we need to test against is "ids being retired by this batch", which is
`pendingDrops[*].NewRecordingId` — the `New` of each drop is the fork that
gets retired. (`OldRecordingId` of a drop is the priorTip that gets
*restored*, not retired — the inverse direction; using `OldRecordingId` here
would be the bug the reviewer flagged.)

Build `pendingRetiredNewIds` = `{ rel.NewRecordingId | rel ∈ pendingDrops }`.
For each `rel ∈ pendingImmutablePreservations`:

- If `rel.OldRecordingId ∈ pendingRetiredNewIds` → demote to drop. The canon
  fork's priorTip is itself being retired in this batch, so the canon has no
  live source to be canon over. Dropping preserves the
  no-double-materialization invariant at the cost of one canon row that has
  no live tip to merge into; an explicit info log records this fallback.
- Otherwise → confirm preservation.

```csharp
// Pass 1: classify
var pendingDrops = new List<RecordingSupersedeRelation>();
var pendingImmutablePreservations = new List<RecordingSupersedeRelation>();
for (...)
{
    // existing rewoundOutOldIds + effectiveForkUT filter...
    if (newRec != null && newRec.MergeState == MergeState.Immutable)
        pendingImmutablePreservations.Add(rel);
    else
        pendingDrops.Add(rel);
}

// Pass 2: demote orphaned-canon preservations to drops.
// pendingRetiredNewIds = the set of fork ids that will be retired by Pass 1's
// drops. A canon fork whose priorTip is itself being retired loses canon
// status (its supersede chain is broken upstream).
var pendingRetiredNewIds = new HashSet<string>(
    pendingDrops.Select(r => r.NewRecordingId).Where(s => !string.IsNullOrEmpty(s)),
    StringComparer.Ordinal);
foreach (var rel in pendingImmutablePreservations)
{
    if (!string.IsNullOrEmpty(rel.OldRecordingId)
        && pendingRetiredNewIds.Contains(rel.OldRecordingId))
    {
        pendingDrops.Add(rel);  // demote: priorTip retired → canon has nothing to be canon over
        result.DemotedImmutablePreservationCount++;
        result.DemotedImmutablePreservationIds.Add(rel.NewRecordingId);
    }
    else
    {
        result.SkippedImmutableForkCount++;
        result.SkippedImmutableForkRecordingIds.Add(rel.NewRecordingId);
    }
}

// Existing apply-drops loop (unchanged) processes pendingDrops.
```

Add `DemotedImmutablePreservationCount` + ids to `RewindSupersedeRollbackResult`
for log + test observability.

### Defensive guard at `EnsureRewindRetirementsForRollback` (RecordingStore.cs:4944)

The retirement function only sees `RetiredForkRecordingIds` (which by Pass-2
construction excludes successfully-preserved Immutable forks). Add a defensive
inner guard anyway, in case a future maintainer extends `RetiredForkRecordingIds`:

```csharp
// Inside the per-id loop, after the StartUT < rewindUT skip:
if (retiredRec.MergeState == MergeState.Immutable)
{
    if (!SuppressLogging)
        ParsekLog.Warn("Rewind",
            $"Skipping retirement for Immutable canon recording rec={retiredId} " +
            $"— predicate-classifier should have preserved this. Investigate.");
    continue;
}
```

### Defensive load-time sweep in `LoadTimeSweep`

Add a sweep that removes any `RecordingRewindRetirement` row pointing at a
recording with `MergeState == Immutable` (recovering legacy saves written by
the buggy code path). Bump `SupersedeStateVersion` if any rows were removed.
Existing `SweepOrphanRewindRetirements` is the natural extension point.

### `RecordingStore.DropSupersedesRewoundOutOfExistence` (RecordingStore.cs:4850)

Extend the existing summary log:

```text
[Parsek][INFO][Rewind] Rewind supersede rollback: dropped=N retiredForks=N
  restored=N skippedImmutable=N rewindUT=286.9 owner='Kerbal X'
```

When `SkippedImmutableForkCount > 0`, also emit one structured info line per
preserved canon fork (matching the existing `Retired rewound-out fork` cadence):

```text
[Parsek][INFO][Rewind] Preserved canon fork across parent rewind:
  rec=rec_b1566… priorTip=32d9674c… mergeState=Immutable
  forkStartUT=456.8 rewindUT=286.9 owner='Kerbal X'
```

This is the audit trail for "why did the spawn fire after a parent rewind" —
absence of this line is the regression signal.

### Fallback semantics: `newRec == null`

The existing code falls back to `rel.UT` (the relation's stored merge time) when
`liveRecordingsById` doesn't contain the new recording. In that case we cannot
read MergeState. Behaviour: **continue dropping orphan rows.** Rationale:
- An Immutable fork would be present in `committedRecordings` (the dictionary
  source); if it's missing the fork is already gone (purged out of band) and
  the relation is just a dangling reference whose continued existence would
  silently suppress `OldRecordingId`.
- This matches the existing one-sided-orphan rationale at line 4807-4808.

## Edge cases

1. **Multi-generation chain `A → B (Immutable) → C (Immutable)` with rewind
   past A's start.** Pass 1: both relations classify as Immutable
   preservations. Pass 2: neither's `Old` is in `pendingDrops`, so both are
   confirmed-preserved. Chain stays intact: A stays superseded by B, B stays
   superseded by C, C is the live tip. Correct.

2. **Mixed chain `A → B (CommittedProvisional) → C (Immutable)`.** Pass 1:
   `A → B` classifies as drop (B not Immutable); `B → C` classifies as
   Immutable preservation candidate. Pass 2: `pendingRetiredNewIds = {B}`
   (B is `A → B`'s `New`); `B → C`'s `Old` is B, which IS in
   `pendingRetiredNewIds`, so `B → C` demotes to drop.

   Result: A is restored, B retires, C retires. The
   no-double-materialization invariant holds. C loses canon status — the user
   committed a Re-Fly that descended from a non-canon middle node, and parent
   rewind invalidates the whole chain. This is the documented behaviour, not
   silent corruption.

3. **Three-generation chain `A → B (Immutable) → C (Provisional) → D
   (Immutable)`.** Pass 1: `A → B` preservation; `B → C` drop; `C → D`
   preservation. Pass 2: `pendingRetiredNewIds = {C}`. `A → B`'s Old is A
   (not in {C}) → preserve. `C → D`'s Old is C (in {C}) → demote.
   Result: A stays superseded by B (relation preserved), B is the canon
   tip, C and D both retire. The user keeps the first stable canon (B); the
   later attempts that depended on the unstable middle node C are correctly
   discarded.

4. **Owner is the canon fork itself.** If the user clicks Rewind on the canon
   fork's own row (not the parent), the rewind is intended to be a
   self-rewind; `owner.RecordingId` is in `rewoundOutOldIds`. Relations where
   the canon fork is the `Old` would drop — that's fine, the user is asking to
   undo this canon recording. The Immutable guard does not protect a fork from
   its own Rewind.

5. **Same-tree `NotCommitted` provisional + Immutable canon fork side by side.**
   Provisional drops normally, canon stays. Existing
   `LoadTimeSweep`/`MergeJournalOrchestrator` paths unchanged.

6. **`ReapplyRewindSupersedeDropAfterLoad`** (line 4628). Calls the same drop
   helper, so the predicate change applies automatically. No second site to
   patch.

7. **Rewind quicksave on an Immutable fork's start UT exactly.** `StartUT >=
   rewindAdjustedUT` is the existing match condition. If the user rewinds *to*
   the canon fork's start UT, `effectiveForkUT >= rewindAdjustedUT` is true and
   the existing code would drop. With our fix, Immutable preserves regardless
   (Pass 2 demotion only fires if the priorTip is also being retired).
   That's the correct contract: "rewind exactly to the moment before this canon
   recording started" should still leave the canon recording intact (the user
   can re-Re-Fly the canon recording's source if they want a divergent
   timeline; the `Re-Fly` button is the right tool for that, not parent
   Rewind).

8. **Malformed save with `MergeState=Immutable` on a never-sealed recording.**
   Hand-edited or corrupted .sfs sets `mergeState = Immutable` without a
   corresponding `Supersede` history. The predicate would treat it as canon and
   preserve any relation pointing at it. Defence: the `LoadTimeSweep`
   defensive sweep added above checks "if a retirement points at an Immutable
   recording, drop the retirement"; the inverse case ("Immutable but no
   Supersede relation") is detectable but out of scope here — log it as a
   warning in a follow-up sweep, do not auto-mutate state in this PR.

## Files changed

- `Source/Parsek/RecordingStore.cs`
  - Extend `RewindSupersedeRollbackResult` with `SkippedImmutableForkCount`,
    `SkippedImmutableForkRecordingIds`, `DemotedImmutablePreservationCount`,
    `DemotedImmutablePreservationIds`.
  - Two-pass classification + demotion in
    `DropSupersedesRewoundOutOfExistenceDetailedPure`.
  - Defensive Immutable guard inside `EnsureRewindRetirementsForRollback`.
  - Extend the live-caller summary log + add per-preserved-fork info log
    + per-demoted-fork info log.
- `Source/Parsek/LoadTimeSweep.cs`
  - Defensive sweep: drop any retirement whose recording is Immutable.
  - Bump `SupersedeStateVersion` if any rows removed.
- `Source/Parsek.Tests/RewindSupersedeRollbackTests.cs` *(extend)*
  - 8 predicate-level tests (see "Tests" below).
- `Source/Parsek.Tests/EffectiveStateTests.cs` *(extend)*
  - 1 ERS visibility test.
- `Source/Parsek.Tests/RewindSpawnSuppressionTests.cs` *(extend — already
  covers the spawn-side rewind interaction; preferred home over a new file)*
  - 1 spawn-eligibility end-to-end test.
- `Source/Parsek.Tests/LoadTimeSweepTests.cs` *(extend)*
  - 1 defensive-sweep test.
- `CHANGELOG.md`
  - Bug-fix entry.
- `docs/dev/todo-and-known-bugs.md`
  - New entry (then mark ~~done~~ in the same commit).

No schema bump (no on-disk format change). No `ConfigNode` round-trip change.
No new public API.

## Tests

`Source/Parsek.Tests/RewindSupersedeRollbackTests.cs` (predicate level):

1. `Rollback_PreservesRelation_WhenForkIsImmutable`
   - `old → new`, `new.MergeState = Immutable`, `new.StartUT >= rewindUT`.
   - Assert: relation NOT dropped.
   - Assert: no retirement created for `new`.
   - Assert: `RestoredRecordingIds` does NOT contain `old.RecordingId`.
   - Assert: `SkippedImmutableForkCount == 1`, `SkippedImmutableForkRecordingIds`
     contains `new.RecordingId`.

2. `Rollback_DropsRelation_WhenForkIsCommittedProvisional`
   - Same setup but `MergeState = CommittedProvisional`.
   - Assert: relation dropped, retirement written (existing behaviour).
   - Assert: `SkippedImmutableForkCount == 0`.

3. `Rollback_DropsRelation_WhenForkIsNotCommitted`
   - Same setup but `MergeState = NotCommitted`.
   - Assert: relation dropped, retirement written.

4. `Rollback_MixedChain_DemotesImmutablePreservation_WhenPriorTipIsRetired`
   - `A → B (CommittedProvisional, post-rewind) → C (Immutable, post-rewind)`.
   - Pass 1: `A → B` drops, `B → C` tentatively preserved.
   - Pass 2: `B → C`'s `Old` is B which is in `pendingDrops` →
     `B → C` demotes to drop.
   - Assert exact counts: `dropped=2, retired=2, restored=1`,
     `skippedImmutable=0`, `demotedImmutablePreservation=1`.
   - Assert: A is restored; B and C are both retired.

5. `Rollback_TwoGenerationCanon_ChainPreservedIntact`
   - `A → B (Immutable, post-rewind) → C (Immutable, post-rewind)`.
   - Pass 1: both classify as Immutable preservations.
   - Pass 2: neither demoted (no Old in pendingDrops).
   - Assert: zero drops, zero retirements; both Immutable rows preserved;
     `skippedImmutable=2`.

6. `Rollback_LogsSkippedImmutableSummary`
   - Use `ParsekLog.TestSinkForTesting`.
   - Assert: summary line contains `skippedImmutable=N`.
   - Assert: per-fork preserved log line contains `mergeState=Immutable` and
     the recording id.

7. `Rollback_DropsImmutable_WhenLiveLookupMissing` *(orphan fallback)*
   - Relation `old → new`, `liveRecordingsById` does NOT contain `new`.
   - `rel.UT >= rewindUT`.
   - Assert: relation dropped (orphan fallback wins; we cannot read
     MergeState).

8. `Rollback_OwnerIsImmutableFork_RewindOnSelfStillDrops`
   - `owner == new (Immutable)`, user rewinds the canon fork's own row.
   - Assert: relations where `owner` is `Old` drop normally (the Immutable
     guard only protects against cross-tree parent rewinds, not self-rewinds).
   - Note: this case is unusual but documents intent.

`Source/Parsek.Tests/EffectiveStateTests.cs` (visibility level):

9. `ComputeERS_IncludesPreservedCanonFork_AfterParentRewind`
   - Set up: committed `[old (Destroyed), new (Immutable, post-rewind)]` plus
     supersede relation `old → new` plus rewind retirements that DON'T list
     `new` (the predicate preserved it).
   - Assert: ERS contains `new`, does NOT contain `old`.
   - Assert: `new` is not classified as `RewindRetired` or `SupersededByRelation`.

`Source/Parsek.Tests/RewindSpawnSuppressionTests.cs` (extend — file already
covers the rewind ↔ spawn interaction):

10. `ShouldSpawnAtRecordingEnd_ReturnsTrue_ForPreservedCanonForkAfterParentRewind`
    - Recording fixture: `MergeState=Immutable`, `terminal=Orbiting`,
      `VesselSnapshot != null`, `VesselSpawned=false`,
      `SpawnedVesselPersistentId=0`, `SpawnSuppressedByRewind=false`.
    - **Tree fixture caveat:** `ShouldSpawnAtRecordingEnd` calls
      `IsEffectiveLeafForVessel(rec, treeContext)` and
      `IsNonLeafInTree(rec, treeContext)`. For a recording with
      `IsTreeRecording=true` and a non-empty `TreeId`, those helpers walk the
      `RecordingStore.CommittedTrees` registry. Two viable test shapes:
      - **Preferred:** populate `RecordingStore.CommittedTrees` with a
        synthetic tree that contains only this recording (so it's the
        effective leaf). Use `RecordingStore.ResetForTesting()` in
        `Dispose` to avoid leaking state between tests. Mark the recording
        with `ChildBranchPointId = null` so the helpers' "non-leaf parent
        of branch point" path is skipped. See
        `IsNonLeafInTreeTests` / `TreeCommitTests` for the existing fixture
        pattern.
      - **Fallback:** set `IsTreeRecording=false` (artificial — defeats the
        regression-guard intent). Use only if the tree-fixture path proves
        unreasonably heavy.
    - Assert: `ShouldSpawnAtRecordingEnd(rec, isActiveChainMember: false,
      isChainLooping: false, treeContext)` returns `(true, "")`.
    - This is the end-to-end regression guard — the spawn path
      observably reaches "needsSpawn" for a preserved canon fork.

`Source/Parsek.Tests/LoadTimeSweepTests.cs`:

11. `Sweep_RemovesOrphanRetirementForImmutableRecording`
    - Set up: scenario has a `RecordingRewindRetirement` whose
      `RecordingId` resolves to a recording with `MergeState=Immutable`.
    - Assert: sweep removes the retirement; `SupersedeStateVersion` is bumped.
    - Assert: warning log emitted naming the recording id.

In-game smoke test (optional but high-value for the user-visible payoff):

- `InGameTests/MergeImmutableCanonSurvivesParentRewindTest.cs` — set up a
  two-recording scenario (parent + canon Immutable child), invoke rewind on
  parent, verify child remains in ERS post-rewind, replay forward through the
  child's `EndUT`, assert real vessel materializes via spawn-at-end.

## Validation

```bash
cd Source/Parsek.Tests && dotnet test --filter RewindSupersedeRollback
cd Source/Parsek.Tests && dotnet test
cd Source/Parsek && dotnet build
```

Smoke (using the bug repro):

1. Start from a save where the user has a Re-Fly merge that produced an
   `Immutable` canon orbital child (Probe in this case).
2. Click parent's Group Rewind to a UT before the decouple.
3. Expected logs:
   - `[Rewind] Preserved canon fork across parent rewind: rec=…`.
   - Summary line shows `skippedImmutable=1`, `dropped=0` (or `dropped=N` for
     non-canon siblings).
   - No `[Rewind] Retired rewound-out fork rec=<canon-id>…`.
4. Re-fly the parent forward through the original decouple UT.
5. Once the canon child's ghost playback hits `PastEffectiveEnd` in flight,
   confirm `[Spawner] … using recorded terminal orbit propagated to current UT
   …` then a successful real-vessel spawn.
6. Tracking Station now shows the canon Probe as a real vessel.

## Documentation updates (same commit)

- `CHANGELOG.md` — Bug Fixes:
  > Parent-tree Rewind no longer retires canon (Immutable-merge) Re-Fly
  > children. The canon recording's supersede relation is preserved across
  > parent rewinds, so the priorTip stays retired and the canon recording's
  > spawn-at-recording-end path re-materializes the persistent vessel after
  > replay reaches its terminal state.

- `docs/dev/todo-and-known-bugs.md` — add then mark done in the same commit:
  > Parent Rewind incorrectly retired Immutable Re-Fly forks, dropping their
  > supersede relations and silently suppressing terminal spawn. Fix gates the
  > drop on `MergeState == Immutable`. See
  > `docs/dev/plans/fix-rewind-canon-fork-retirement.md`.

- `docs/dev/plans/fix-watch-double-probe-ghost-after-rewind.md` — add a
  trailing "Follow-up" section pointing to this plan: retirement remains
  correct for non-canon forks; the present PR adds the Immutable carve-out.

## Risk

- **Regression in the original double-ghost fix.** Mitigated by the Pass-2
  demotion rule (Edge case #2). The mixed-chain `A → B(Provisional) →
  C(Immutable)` would otherwise leave both A and C visible — exactly the
  regression PR #776/#777 fixed. The two-pass logic demotes `B → C` to a drop
  whenever B is itself being retired, preserving the no-double-materialization
  invariant. Existing `RewindSupersedeRollbackTests` cover the
  CommittedProvisional/NotCommitted paths and stay green; new tests #4 and
  #5 cover the mixed-chain demotion and the all-canon preservation cases.
- **Stale ProtoVessel injection.** Out of scope (see Non-goals). The vessel
  comes back via spawn-at-recording-end; "where is my Probe between rewind and
  re-flight" is a UX gap acknowledged in Non-goals — there is no in-flight
  feedback during the window between rewind-load and the moment the user
  re-flies forward through the canon fork's `endUT`.
- **Cache invalidation.** The existing `BumpSupersedeStateVersion()` call
  fires when `dropped > 0 || retired > 0`. If only Immutable preservations
  happen (`dropped == 0 && retired == 0`), the cache need not be bumped —
  `EffectiveState` reads `RecordingSupersedes` and `RecordingRewindRetirements`
  unchanged from before, so cached ERS stays correct. `RecordingsTableUI`
  does not key off `SupersedeStateVersion` (re-reads ERS each draw). Verified
  by the cache-bump guard at `RecordingStore.cs:4930`.
- **Coupling hazard: `MarkRewoundTreeRecordingsAsGhostOnly`** at
  `ParsekScenario.cs` ~line 2226. This helper currently scopes
  `SpawnSuppressedByRewind` to the active/source recording only (per #589).
  If that scope ever expands to cover same-tree future recordings, the
  preserved canon fork would be silently re-suppressed at spawn time even
  though our predicate kept its supersede relation. Document the coupling in
  a code comment near the new predicate; keep an eye on it during review of
  any future Rewind-scope changes.
- **Live persistent-vessel name strip.** `HandleRewindOnLoad` at
  `ParsekScenario.cs` ~line 2207 collects `CollectAllRecordingVesselNames()`
  and strips matching vessels from `flightState.protoVessels`. For the
  primary repro (rewind quicksave from before the canon vessel existed), the
  strip is a no-op. But for any rewind whose quicksave DOES contain the
  canon vessel (e.g., the user makes a manual quicksave after the merge,
  then rewinds the parent past the fork start), the strip will delete the
  canon vessel from the loaded save anyway. The recording's spawn-at-end
  still materializes it on next replay, so the user-visible outcome is the
  same as the primary repro. Worth flagging because future maintainers may
  expect the strip to honour Immutable forks; it does not, today, and that's
  a separate fix scoped to the strip-by-name semantics, not this PR.
- **Malformed save with `mergeState=Immutable` on a never-sealed recording.**
  Edge case #7. The defensive `LoadTimeSweep` cleanup added by this PR only
  removes orphaned retirements pointing at Immutable recordings; it does not
  validate that an Immutable recording has a corresponding supersede
  history. A hand-edited save claiming Immutable status will get
  canon-survives-rewind treatment. Mitigation cost is low (one more sweep
  rule) but explicitly out of scope here — log the gap for follow-up rather
  than auto-mutating state in this PR.
