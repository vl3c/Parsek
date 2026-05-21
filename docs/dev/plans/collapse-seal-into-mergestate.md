# Collapse Seal into MergeState: single source of truth for re-fly slot open/closed

Status: REVIEWED CLEAN (both Opus reviewers, 3 rounds: correctness + completeness). Ready to implement.
Branch: cleanup-remove-unseal-remnants
Worktree: Parsek-cleanup-unseal

## 1. Goal

Make `Recording.MergeState` the single source of truth for whether a re-fly child
slot is open (re-flyable / an "Unfinished Flight") or closed (sealed / concluded /
permanent). Delete `ChildSlot.Sealed`, `ChildSlot.SealedRealTime`, the `considerSealed`
parameter, the `"slotSealed"` reason, and the `sealedSlotsContributing` reaper counter.

This removes the last remnant of the retired "Un-Seal" architecture (a sealed recording
could be restored to re-flyable). Sealing is already permanent in behavior; this makes
it permanent and singular in representation.

`ChildSlot.Stashed` / `StashedRealTime` are KEPT (a separate, orthogonal concept).

## 2. Final model

MergeState values and their single meaning (read from a slot's EFFECTIVE chain TIP):

- `NotCommitted` (0): recording in progress (active recorder, or re-fly provisional
  pre-merge). RP kept alive. Excluded from ERS/ELS. Unchanged.
- `CommittedProvisional` (1): committed AND slot OPEN (re-flyable Unfinished Flight, or
  a stashed stable leaf). RP kept alive. Included in ERS/ELS.
- `Immutable` (2): committed AND slot CLOSED / sealed / concluded / canon / finalized.
  Permanent. Reapable once all slots are Immutable. Included in ERS/ELS. Preferred as a
  RELATIVE anchor. Survives a parent rewind as canon.

Open/closed is read ONLY from the slot's effective tip MergeState. `slot.Sealed` is
gone. Terminal-shape qualification (crash / stranded-EVA / orbit / stashed-stable-leaf)
still decides whether a `CommittedProvisional` slot is SHOWN as a UF row, but does not
decide open/closed.

## 3. Why MergeState, and why this is the right single source

Investigation found exactly three sites that distinguish `CommittedProvisional` from
`Immutable`, and all read the SAME conceptual axis:

- Open vs closed (reaper `RewindPointReaper.cs`, classifier `UnfinishedFlightClassifier.cs`):
  today delegated to `slot.Sealed`.
- Parent-rewind canon (`RecordingStore.cs:6549/6861/6959/7031-7091`,
  `LoadTimeSweep.cs:608/851/872`): an `Immutable` supersede fork survives a parent
  rewind as canon; a `CommittedProvisional` fork is rolled back as a tentative attempt.
- Anchor reliability (`AnchorDetector.cs:258` `IsSealedRecordingAnchor`): an `Immutable`
  recording is preferred as a RELATIVE anchor because its trajectory is finalized.

All three read `Immutable` = "concluded / canon / finalized" and `CommittedProvisional`
= "open / tentative / retryable." Putting the truth on `slot.Sealed` would force these
recording-level decisions to look up a RewindPoint/ChildSlot bit (layering inversion);
putting it on `MergeState` keeps it where those decisions already live. Direction A.

The legacy rationale for the decoupling (keep v0.9.0 `Immutable` crash rows re-flyable)
is dead: schema generation 4 removed legacy-save migration, and project policy is no
pre-1.0 compatibility carve-outs.

## 4. The central correctness problem: re-commit must not un-seal

`ApplyRewindProvisionalMergeStates` (`RecordingStore.cs:1077`, called from `CommitTree`
at `:1030`) demotes `Immutable` recordings to `CommittedProvisional` when the slot
qualifies as an open UF by shape. It runs on EVERY `CommitTree`, including topology
re-commits. Today it does not re-open a concluded slot because of two guards that BOTH
disappear under this change:

1. It only considers recordings `== Immutable` (`:1089`); slots demoted on first commit
   are already CP and skipped.
2. Its `TryQualify(..., considerSealed: true, ...)` call (`:1111`) rejects sealed slots
   (`slotSealed`).

After the change, a concluded slot's tip is `Immutable` and there is no `slot.Sealed`.
A later `CommitTree` would see an `Immutable`, open-UF-shaped slot and re-demote it to
`CommittedProvisional`, silently un-sealing it. This is real for BOTH:

- manually sealed open-UF slots (e.g. a crashed booster the player sealed), and
- AUTO-sealed conclusions whose fork is `Immutable` + open-UF-shaped, proven by
  `InGameTests/MergeNonFocusReFlyToOrbitImmutableTest.cs`. AT MERGE TIME, a non-focus
  re-fly that reaches Orbiting is sealed via the focus-override path
  (`stableTerminalFocusSlot` -> `MergeState.Immutable`). On a LATER generic promotion
  pass, `ApplyRewindProvisionalMergeStates` calls `TryQualify` with NO `focusSlotOverride`
  (`RecordingStore.cs:1110`), so that same fork now matches the static-focus
  `stableLeafUnconcluded` branch (`UnfinishedFlightClassifier.cs:334-347`) and would be
  re-demoted to CP. (This is protected today only because that fork's slot is
  `slot.Sealed = true`, which the `considerSealed: true` gate rejects.)

### 4.1 Resolution: first-commit guard (keyed on committed-TREE membership)

`ApplyRewindProvisionalMergeStates` must demote `Immutable -> CommittedProvisional` ONLY
for recordings being committed AS PART OF A TREE for the first time. Skip a recording
(do NOT demote, treat as already-concluded) if EITHER:

- (tree-membership) its id is in the snapshot of all `committedTrees[].Recordings` dict
  ids taken at the LITERAL TOP of `CommitTree` (before the union/replace path at
  `:993`/`:1877` and before `FinalizeTreeCommit` at `:1035` mutate anything), OR
- (supersede-fork identity) its id appears as a `NewRecordingId` in
  `scenario.RecordingSupersedes`. A supersede fork's MergeState is set authoritatively by
  `SupersedeCommit.FlipMergeStateAndClearTransient`, NOT by promotion, so promotion must
  never re-derive it.

Both conditions are needed and neither subsumes the other:

- A manually sealed, NEVER-re-flown crash tip is NOT a supersede fork but IS in a
  committed tree (committed on first commit) -> caught by tree-membership.
- A NON-IN-PLACE re-fly fork (e.g. non-focus re-fly to stable Orbit -> `Immutable`) is a
  supersede fork but is NOT migrated into any committed tree:
  `MigrateActiveReFlyForkIntoCommittedTree` early-returns for `!InPlaceContinuation`
  ("fork lives in its own tree", `MergeJournalOrchestrator.cs:844`, proven by
  `MergeJournalForkMigrationTests.cs:129`). If its RP survives the merge (a sibling slot
  still open) and the fork's tree is later re-committed, slot-driven promotion (4.2)
  would resolve the slot tip to this `Immutable` Orbit fork, find it absent from every
  committed tree, and re-demote it -> un-sealing canon. The fork-identity skip closes
  this. Caught by supersede-fork identity.
- A mid-flight `CommitRecordingDirect` open-UF tip on first commit is NEITHER (not a
  fork, not yet in a committed tree) -> NOT skipped -> demoted to CP. Correct.

Key on committed-TREE membership, NOT the flat `RecordingStore.CommittedRecordings` list.
The flat list is polluted mid-flight: open-UF chain/split tips are committed to the flat
list DURING flight via `CommitRecordingDirect` (`ChainSegmentManager.cs:546` for chain
continuations; `ParsekFlight.cs:5013` `FallbackCommitSplitRecorder` for split-branch
crash tips, reachable for `VesselDestroyed` at `:4992`). These are born `Immutable`,
never touch MergeState, and live in the flat list + the ACTIVE tree dict, but NOT in any
COMMITTED tree until their tree's first `CommitTree`. A flat-list-keyed guard would skip
exactly these open-UF tips (treating them as "already committed") and leave them
`Immutable`; after the reaper workaround is deleted (stage 3) the reaper would reap their
re-flyable RPs (data loss). Committed-tree membership is the correct "already concluded
as canon" signal:

- Open-UF chain/split tip on its tree's first commit: in the flat list + active tree, but
  NOT in any committed tree and NOT a supersede fork -> NOT skipped -> demoted to CP.
  Correct.
- Re-fly fork (in-place OR non-in-place) at a later re-commit: caught by supersede-fork
  identity (its id is a `NewRecordingId` in `scenario.RecordingSupersedes`) -> skipped ->
  stays at its `SupersedeCommit`-assigned state (CP for crash, Immutable for the
  non-focus-Orbit conclusion). Correct (canon preserved). In-place forks are ALSO in the
  committed tree (`MigrateActiveReFlyForkIntoCommittedTree` `:900`); non-in-place forks
  are not, which is why the fork-identity condition is required.
- Manually sealed slot at a later re-commit: demoted to CP on first commit, then sealed
  to Immutable; it is in the committed tree -> skipped -> stays Immutable. Correct.
- Continuation tip created mid-flight AFTER a prior commit, then re-committed: the new
  tip is not in the prior committed tree and not a fork -> demoted; old recordings are in
  the committed tree -> skipped. Correct.

Verified facts: promotion runs at `:1030` before `FinalizeTreeCommit` (add to flat list
`:1504`, swap committed tree `:1523`); the union path adds incoming recordings to the
committed tree dict at `:1877` (inside CommitTree, before `:1030`), which is why the
snapshot MUST be taken at the literal top of `CommitTree`, before that mutation. A stable
mid-flight `CommitRecordingDirect` leaf (e.g. a child that landed) is also not skipped by
the tree-keyed guard, but promotion correctly leaves it `Immutable` because it does not
qualify as an open UF by shape. Re-fly forks reach `Immutable`/`CP` via
`SupersedeCommit.FlipMergeStateAndClearTransient`, not via promotion, so for forks the
guard's only job is to PREVENT re-demotion; committed-tree membership does that.

Rejected alternative: born `NotCommitted` (classify exactly once). Cleaner state machine
but leakier: the active/pending tree is serialized pre-commit (`ParsekScenario.cs:914/990`),
the codec omits `mergeState` only when `Immutable` (`RecordingTreeRecordCodec.cs:421`),
so a born-`NotCommitted` normal recording would persist `NotCommitted` to disk and need
a NEW explicit "flip to Immutable at first commit" step that does not exist today. The
committed-tree-keyed guard resolves the same ambiguity with no serialization change. Not
chosen.

### 4.2 Promotion must demote the slot's TIP, not just branch-linked HEADs

Today promotion iterates `tree.Recordings` and demotes per-recording via
`TryResolveRewindPointForRecording`, which matches HEADs (branch-linked / origin). A
slot's effective chain TIP can be a continuation recording that does not match, so it
stays `Immutable` while the slot is an open UF. That is exactly the gap the reaper
workaround compensates for (`RewindPointReaper.cs:226-258`), and it is why the workaround
cannot be deleted until promotion reaches tips.

Change promotion to be SLOT-DRIVEN: for each RewindPoint in the tree, for each ChildSlot,
resolve the slot's effective tip (`slot.EffectiveRecordingId(supersedes)` /
`EffectiveState.ResolveChainTerminalRecording`), and if the slot qualifies as an open UF
by shape AND the tip is first-commit (per 4.1), set the TIP's
`MergeState = CommittedProvisional`. Stable-EVA conclusion (section 7.3) leaves the tip
`Immutable`.

Disjointness invariant (verified): each ChildSlot's `OriginChildRecordingId` is a
distinct controllable vessel at the split, and `EffectiveRecordingId` is the composite
chain+supersede tip walker (1:1 forward map), so no recording is the effective tip of
two slots. Demoting one slot's tip cannot affect another slot. The Seal handler inherits
this guarantee.

## 5. Stash interaction (slot.Stashed kept, monotonic)

`ChildSlot.Stashed` / `StashedRealTime` are NOT deleted. Stash governs QUALIFICATION
(does a default-excluded stable leaf appear as a UF at all), orthogonal to open/closed.

The Stash action (`UnfinishedFlightStashHandler.TryStash`, write at
`UnfinishedFlightStashHandler.cs:59`, invoked from `RecordingsTableUI.cs:2834`; the only
writer, no auto-stash path) must ALSO demote the stashed leaf's effective tip
`Immutable -> CommittedProvisional` (it already computes `tip` at `:68`). This opens the
slot.

The retained, never-cleared `slot.Stashed` bit makes the stash->seal sequence safe
without any extra state:

- Stash: `slot.Stashed = true`, tip `Immutable -> CommittedProvisional` (open).
- Seal: tip `CommittedProvisional -> Immutable` (closed). `slot.Stashed` stays true.
- Re-stash is blocked because `TryResolveStashableRewindPointForRecording` already
  rejects `slot.Stashed == true` with `alreadyStashed` (`UnfinishedFlightClassifier.cs:453`).
- A stashed-then-sealed leaf reads closed: open check is "tip is CommittedProvisional",
  and `HasStashedResolvedSlot` (`:807-820`) becomes `Stashed && tip == CommittedProvisional`.

So `slot.Stashed` independently prevents the stash->seal->re-stash un-seal, and no fourth
state is needed.

Sealing a never-stashed stable leaf is a no-op (its tip is already `Immutable`); such a
slot already counts as closed for the reaper today (a non-stashed Landed leaf returns
`stableTerminal`/not-a-UF, so the reaper treats Immutable as closed). No reaper change.

UI note: on a stashable-but-not-open row the "Seal" button is meaningless (the slot is
already `Immutable`). Consider showing only "Stash" on not-yet-open stashable rows. Minor
polish, not correctness.

## 6. Axis 1 (parent-rewind canon) and Axis 2 (anchor): code unchanged, with proof

These sites keep keying on `MergeState == Immutable` meaning "canon / finalized." The
recordings that carry `Immutable` change as follows, and the guard in 4.1 keeps it safe:

- Manual Seal of a crash slot: CP -> Immutable. Correctly treated as canon on parent
  rewind and preferred as anchor. A sealed crash IS a deliberate canonical outcome.
- AUTO-sealed forks (e.g. non-focus re-fly to Orbit) are `Immutable` and ARE supersede
  forks (`MergeNonFocusReFlyToOrbitImmutableTest.cs`). The earlier draft's claim that
  flipped tips are "never forks" was FALSE. They stay `Immutable` ONLY because the
  first-commit guard (4.1) prevents promotion from re-demoting them. With the guard, they
  remain canon on parent rewind (`RecordingStore.cs:6549`) exactly as today.
- Open-UF tips that today are `Immutable`-but-open become `CommittedProvisional` on
  first commit (4.2). These are origin/continuation recordings, not re-fly forks, so they
  were never preserved-as-canon forks; demoting them to CP changes nothing at the canon
  sites. They do lose the `IsSealedRecordingAnchor` preference (anchor population shrinks
  slightly), which is acceptable and arguably more correct (an open/re-flyable recording
  is not a stable anchor). Note this in the design doc.

Net: `RecordingStore.cs:6549/6861/6959/7031-7091`, `LoadTimeSweep.cs:608/851/872`, and
`AnchorDetector.cs:258` need NO code change. Their correctness depends entirely on the
first-commit guard (4.1) holding.

## 7. Detailed change list

### 7.1 ChildSlot.cs + serialization

- Remove `public bool Sealed;` (`:50`) and `public string SealedRealTime;` (`:56`).
- `SaveInto`: remove the `"sealed"` (`:88-89`) and `"sealedRealTime"` (`:90-91`) writes.
- `LoadFrom`: remove the `"sealed"` (`:125-128`) and `"sealedRealTime"` (`:130`) reads.
- Keep `Stashed` / `StashedRealTime`.

ChildSlot serializes via `RewindPoint.SaveInto` (`RewindPoint.cs:104`) into the SCENARIO
`.sfs` (`ParsekScenario.OnSave` `:1429-1435`, load `:1543-1566`), NOT the recording
schema. So `CurrentRecordingSchemaGeneration` (= 4, `RecordingStore.cs:131`) does NOT
need bumping; the scenario `.sfs` has no generation gate and tolerates a dropped key.
No other serializer of `slot.Sealed` exists (confirmed: only `ChildSlot.cs`).

No migration (no-compat policy). Note in CHANGELOG: a pre-change save with an OPEN UF
slot whose tip is `Immutable` (the cases the deleted reaper workaround protected) becomes
reap-eligible on the first OnLoad reaper pass after this change, BEFORE any new commit
re-runs promotion. This is acceptable under pre-1.0 no-compat but is real data loss of a
re-flyable RP for such legacy slots. (A pre-change `sealed=True` slot whose tip is
already `Immutable` is unaffected.)

### 7.2 Seal becomes a MergeState transition (UnfinishedFlightSealHandler.cs)

- `TrySeal`: replace `slot.Sealed = true; slot.SealedRealTime = ...` (`:64-68`) with:
  resolve the slot's effective tip (`EffectiveState.ResolveChainTerminalRecording` /
  `EffectiveTipRecordingId`) and set `tip.MergeState = MergeState.Immutable`,
  `tip.FilesDirty = true`. Idempotent: if tip already `Immutable`, no-op.
- Keep persist-before-reap, `BumpSupersedeStateVersion`, and reap logic. Update logs
  (drop `sealedRealTime`; log tip id + old->new MergeState).
- Dialog copy (`:160-163`) already says "This cannot be undone."

### 7.3 Auto-seal sites

- `SupersedeCommit.FlipMergeStateAndClearTransient` already sets
  `provisional.MergeState = classification.NewState` (`:761`), which is `Immutable` for
  the auto-seal (concluded) cases. `ApplyAutoSealAfterSafetyClose` (`:777`, body
  `:1897-1905`) only sets `classification.Slot.Sealed = true` / `SealedRealTime` on top
  of that. VERIFY that `classification.AutoSealSlot == true` implies
  `classification.NewState == Immutable` for every auto-seal case (TerminalKind.Landed
  -> Immutable; focus-override `stableTerminalFocusSlot` -> Immutable). If so, DELETE
  `ApplyAutoSealAfterSafetyClose`'s slot mutation entirely (the `Immutable` state already
  encodes closed); keep any logging. Remove the `slot.Sealed` read guard at `:1897`.
- `RecordingStore.AutoSealStableEvaCommitSlot` (`:1160-1191`): under the new model a
  stable-EVA conclusion just means "do NOT demote this first-commit tip; leave it
  `Immutable`." Remove the `slot.Sealed` read guard (`:1168`) and the
  `slot.Sealed = true; SealedRealTime = ...` writes (`:1176-1177`). The method (or the
  caller at `:1120-1127`) simply skips the CP demotion for the stable-EVA case (the tip
  stays `Immutable` = concluded). Keep `BumpSupersedeStateVersion` if state-version
  consumers need it; otherwise the no-demote path needs no bump.
- `LoadTimeSweep.cs:340`: replace `slot.Sealed = true` with setting each slot's effective
  tip `MergeState = Immutable` (the RP quicksave is gone; no slot can be re-flown ->
  all concluded). Tips resolvable at load.

### 7.4 Stash demotes tip to CommittedProvisional (UnfinishedFlightStashHandler.cs)

- `TryStash`: in addition to `slot.Stashed = true` (`:59`), demote the stashed leaf's
  effective tip `Immutable -> CommittedProvisional` (guarded: only from `Immutable`),
  set `FilesDirty`. Bump `SupersedeStateVersion`. See section 5.

### 7.5 Promotion (RecordingStore.ApplyRewindProvisionalMergeStates)

- Add the first-commit guard (4.1): skip a recording if its id is in the snapshot of all
  `committedTrees[].Recordings` ids (taken at the literal TOP of CommitTree, before
  union/replace/Finalize mutate) OR its id is a `NewRecordingId` in
  `scenario.RecordingSupersedes` (supersede fork). Do NOT key on the flat
  `CommittedRecordings` list (NB1). Perf: build a `HashSet<string>` of `NewRecordingId`s
  once before the slot loop, not per-tip.
- Make it slot/tip-driven (4.2): iterate the tree's RewindPoints + ChildSlots, resolve
  each slot's effective tip, demote the TIP to CP when the slot qualifies by shape and
  the tip is first-commit. Keep the stable-EVA "leave Immutable" branch.
- Remove the `considerSealed: true` argument at the `TryQualify` call (`:1111`) ONLY in
  the stage where the parameter is deleted (7.7); until then keep passing it (see 9).

### 7.6 Reaper (RewindPointReaper.cs)

- `IsReapEligible`: a slot is closed iff its effective tip is `Immutable`; open iff
  `NotCommitted` or `CommittedProvisional` (null/orphan tip = closed, as today).
- Delete the "Immutable + !slot.Sealed + Qualifies => keep alive" workaround (`:226-258`).
- Delete the `slot.Sealed` close branch (`:259-263`), the `sealedSlotsContributing`
  counter and all its logging (`:98/116-119/128/168/188-197/261`), and simplify the
  signature.
- Delete the `UnfinishedFlightClassifier.Qualifies(..., considerSealed: true)` call
  (`:250`).
- Net rule: RP reapable iff not SessionProvisional AND every slot's effective tip is
  `Immutable`.
- Sequencing: do this AFTER 7.5 proves open tips are reliably CP (see 9, staged order).

### 7.7 Classifier (UnfinishedFlightClassifier.cs)

- Remove the `considerSealed` parameter from `Qualifies` (`:16-24`) and `TryQualify`
  (`:26-34`); delete the `if (considerSealed && slot.Sealed)` gate + `slotSealed` reason
  (`:109-115`). `TryQualify` becomes a pure SHAPE predicate (committed + branch/origin +
  terminal-shape + stash). Promotion uses this shape predicate.
- Add a single open/closed read: `IsOpenUnfinishedFlight(rec, ...)` = shape-qualifies AND
  the slot's effective tip `MergeState == CommittedProvisional`. Reaper, UI, disk-usage
  use THIS, not the shape predicate.
- `EffectiveState.IsUnfinishedFlight(rec)` (`:828`) is a thin wrapper; the load-bearing
  qualification call is inside `TryResolveUnfinishedFlightRaw` at `EffectiveState.cs:1011-1012`,
  which today passes `TryQualify(..., considerSealed: true, ...)`. Apply the new
  open/closed filter THERE: require the slot's effective tip `MergeState ==
  CommittedProvisional` (open). This makes sealed (`Immutable`) slots disappear from the
  UF group and fixes the stash-then-seal leaf re-qualification (Q6 resolved YES: a
  closed/Immutable tip is not an open UF). The Immutable closed-filter MUST be applied at
  this `TryResolveUnfinishedFlightRaw` level BEFORE the anchor-dedupe admit-on-unresolved
  fallback (`EffectiveState.cs:930-943`), otherwise an `Immutable` peer could slip through
  the malformed-slot fallback. VERIFY no caller of `IsUnfinishedFlight` actually wants the
  shape-only meaning; if one does, point it at the shape predicate.
- `HasStashedResolvedSlot` (`:807-820`): `Stashed == true && Sealed == false` ->
  `Stashed == true && tip == CommittedProvisional`.
- `TryResolveStashableRewindPointForRecording` (`:405-515`): remove the `if (slot.Sealed)`
  rejection (`:461-467`). Its existing `TryQualify(..., considerSealed: true, ...)` call
  (`:470`) is a SECOND `considerSealed` argument-removal site (besides
  `RecordingStore.cs:1111`); drop the argument in the stage-3 parameter deletion. After
  the param is gone the call is shape-only and already rejects shape-qualifying slots as
  `alreadyUnfinishedFlight`; `slot.Stashed` rejects re-stash as `alreadyStashed`; a fresh
  stable Landed leaf (shape does not auto-qualify) still proceeds via
  `IsManualStashOverrideReason`.
- The `MergeState in {Immutable, CommittedProvisional}` "is committed" gate (`:46-48`)
  STAYS in the shape predicate (promotion needs to accept the born-`Immutable`
  first-commit recording).

### 7.8 Disk usage (RewindPointDiskUsage.cs) + UI tooltip

- Re-bucket by effective tip MergeState: live-crashed (NotCommitted), stable-open
  (CommittedProvisional), concluded (Immutable).
- `:225` `if (slot.Sealed)` -> derive from tip MergeState; `:238` remove `considerSealed`.
- Rename `SealedPendingCount` field (`:63`) and the `:312` summary string to
  "concluded"/"immutable".
- `SettingsWindowUI.cs:465` tooltip text mentioning "sealed-pending slots" -> "concluded".

### 7.9 Remaining reads

- Resolve every remaining `slot.Sealed` / `SealedRealTime` / `considerSealed` /
  `"slotSealed"` / `sealedSlotsContributing` reference; none survive in production.

## 8. Test plan

Unit (xUnit) (~70+ assertions to migrate):

- `RewindPointReaperTests`: rewrite matrix to tip MergeState; drop
  `sealedSlotsContributing` asserts. CAUTION: the four `Reap_Immutable*_KeepsRpAlive`
  tests (`:347/379/515/545`) encode the DELETED workaround's semantics: they set a chain
  TIP to `MergeState.Immutable` (e.g. `:411`) and assert `reaped == 0` plus the
  `immutable-qualifies-as-unfinished-flight` log line. These call `ReapOrphanedRPs()`
  directly without a promotion pass, so a mechanical "replace `slot.Sealed = true` with
  tip `Immutable`" would leave them asserting keep-alive on an Immutable tip, which after
  7.6 reaps -> tests FAIL. They must INVERT: set the kept-alive tip to
  `CommittedProvisional` (the post-4.2-promotion state) and drop the `immutable-qualifies`
  log assertion. "Closed" cases set the tip `Immutable`.
- `UnfinishedFlightSealHandlerTests` (`:115/116/195/203/257`): assert `TrySeal` sets tip
  `Immutable`, idempotent, bumps state version, reaps when last slot closes.
- `UnfinishedFlightClassifierTests` (10 `considerSealed` call sites): drop the param;
  add open(CP)/closed(Immutable) cases via the new `IsOpenUnfinishedFlight`.
- `SupersedeCommitTests` (~38 Sealed/SealedRealTime asserts, lines per inventory): replace
  `Assert.True(slot.Sealed)` with `Assert.Equal(MergeState.Immutable, tip.MergeState)`,
  `Assert.False(slot.Sealed)` with `Assert.Equal(MergeState.CommittedProvisional, ...)`;
  drop `SealedRealTime` asserts.
- `TreeCommitTests` (`:689/690/695/740/752/774`): update auto-seal + the
  `Assert.Equal("slotSealed", ...)` reason case.
- `RewindPointRoundTripTests` (`:46/94-95/103-104/110/189-190`): drop sealed/sealedRealTime
  round-trip; keep stashed/stashedRealTime.
- `UnfinishedFlightsMembershipTests` (`:112/512/876/902`): rewrite the `reason=slotSealed`
  log asserts.
- `LoadTimeSweepOrphanRpTests` (`:83/152/205`): orphan-RP sweep sets tip Immutable.
- `RecordingsTableUIStashRewindTests`, `AnchorDetectorTests` (the sealed-vs-unsealed anchor
  test keys on MergeState already; verify), `DiskUsageDiagnosticsTests` (`:105`): update.
- NEW regression tests:
  1. Seal a crash slot (tip -> Immutable), re-commit a sibling tree (forces
     ApplyRewindProvisionalMergeStates), assert the sealed tip STAYS Immutable and the
     RP reaps. (The 4.1 clobber guard.)
  2. Non-focus re-fly to Orbit (fork -> Immutable), re-commit, assert the fork stays
     Immutable AND survives a parent rewind as canon (`RecordingStore.cs:6549`).
  3. Stash a stable leaf (tip -> CP), Seal it (tip -> Immutable), assert it is NOT
     re-stashable (`alreadyStashed`) and is hidden from the UF group.
  4. Multi-controllable split -> crash one child (open-UF crash tip committed mid-flight
     via `CommitRecordingDirect`, so it is in the flat list + active tree but NOT a
     committed tree) -> first `CommitTree` -> assert promotion demotes the tip to
     `CommittedProvisional` (the committed-tree-keyed guard does NOT skip it) and the RP
     is NOT reaped. This pins the NB1 fix in 4.1.
  5. NON-IN-PLACE re-fly to stable Orbit (fork -> Immutable, NOT migrated into the
     committed tree per `MergeJournalOrchestrator.cs:844`/`MergeJournalForkMigrationTests.cs:129`)
     with a SURVIVING sibling-slot RP -> re-commit the fork's tree -> assert the fork
     stays `Immutable` (caught by the supersede-fork-identity skip, not tree membership)
     and is NOT re-demoted/un-sealed. Pins the non-in-place fork hole in 4.1.

In-game (InGameTests):

- `UnfinishedFlightSealDialogTest` (`:137/140-144`): assert tip MergeState -> Immutable
  on confirm, unchanged on cancel; drop SealedRealTime asserts.
- `MergeAndSealReFlyClosesSlotTest` (`:93/123/140-141`): "Merge & Seal" sets tip Immutable.
- `MergeReFlyStructuralMutationAutoSealsTest` (`:95/108/110-111`): auto-seal sets tip
  Immutable; drop SealedRealTime assert.
- `MergeReFlyToSubOrbitalKeepsSlotOpenTest` (`:69/113`): tip stays CommittedProvisional.
- `StableLeafUnfinishedFlightsRuntimeTest` (`:191/193`): non-focus stable leaf auto-seal
  sets tip Immutable; delete the hardcoded SealedRealTime timestamp assert.
- `MergeNonFocusReFlyToOrbitImmutableTest`, `MergeLandedReFlyCreatesImmutableSupersedeTest`,
  `MergeCrashedReFlyCreatesCPSupersedeTest`: verify still pass (they already assert
  MergeState).

No shared generator fixtures set `Sealed` (ChildSlot test data is built inline), so
`Source/Parsek.Tests/Generators/` needs no change.

## 9. Staged implementation order (each stage compiles + tests green)

1. Promotion: add the first-commit guard (4.1) and make it slot/tip-driven (4.2), with
   the reaper workaround branch STILL in place and `considerSealed` STILL passed at
   `:1111` (the parameter is not yet deleted). Goal: open-UF tips become CP reliably,
   old safety net still catching misses. Instrument to confirm no slot relies on the
   workaround.
2. Seal / auto-seal / Stash handlers (7.2, 7.3, 7.4) write MergeState. Keep `slot.Sealed`
   reads AND the field for now so the reaper/classifier still compile and behave; but
   note these handlers now ALSO move MergeState. Caution: do not ship a state where Seal
   moves MergeState but the reaper still reads a never-set `slot.Sealed` (sealed slots
   would stop reaping). So in this stage, keep `slot.Sealed` writes in the handlers TOO
   (write both) until stage 3 flips the readers. Net: handlers write both
   `tip.MergeState=Immutable` AND `slot.Sealed=true` transitionally.
3. Flip readers to MergeState: reaper (7.6), classifier (7.7), disk-usage (7.8). Delete
   the workaround branch, `sealedSlotsContributing`, the `considerSealed` parameter (now
   remove the argument at `:1111`), `slotSealed`. Stop writing `slot.Sealed` in the
   handlers (drop the transitional double-write from stage 2).
4. Delete `ChildSlot.Sealed` / `SealedRealTime` + serialization (7.1). Compile-forcing
   step that surfaces any straggler.
5. Tests (section 8) updated alongside; full `dotnet test` green at each stage where
   possible.
6. Docs (section 10).
7. Final self-review + a clean-context Opus review of the diff before PR.

Compile note: `considerSealed` is a required positional parameter with no default
(`UnfinishedFlightClassifier.cs:30`). Its arguments at `RecordingStore.cs:1111` AND
`UnfinishedFlightClassifier.cs:470` (the internal call in
`TryResolveStashableRewindPointForRecording`) cannot be removed before the parameter is
deleted (stage 3). Stage 1-2 keep passing them.

## 10. Docs

- `docs/parsek-rewind-to-separation-design.md`: rewrite sections 2.2, 6.24, the
  SealHandler pseudocode (`:1399-1417`, esp. `:1406` and `:1417` "Decoupling is
  load-bearing"), the reaper rule (~`:2139`), and the `sealedSlotsContributing` log spec
  (`:1380/1908`). Remove the "Seal does NOT touch MergeState" invariant and the "Un-Seal
  affordance is deferred unless playtest demands it" hedge (`:64`). State: Seal is a
  permanent CP->Immutable transition; open iff CP, closed iff Immutable; Stash demotes
  tip to CP; `slot.Stashed` is the monotonic re-stash guard. Note the anchor-population
  shrink (section 6).
- `MergeState.cs:9-18` doc-comment: remove the dead legacy `committed`-bool migration
  text; state Immutable = sealed/closed as the authoritative single open/closed signal.
- `CHANGELOG.md`: one user-facing line (sealing a re-fly / unfinished flight is now
  permanent; internal state simplified). No em dashes. Note the legacy-save reap caveat
  from 7.1.
- `docs/dev/todo-and-known-bugs.md`: record the cleanup; the "STASH auto-seal persisted
  reason metadata" item (`:1620-1624`) is now obsolete (no `Sealed` bit to attach
  metadata to) -- mark it.
- `docs/roadmap.md:305` ("Persistent slot signals -- ChildSlot.Sealed..."): update.
- `.claude/CLAUDE.md`: update the SupersedeCommit / RewindPointReaper / ChildSlot one-line
  descriptions if their contracts changed.
- This plan moves to `docs/dev/done/` once shipped.

Archived docs that mention `slot.Sealed` descriptively and do NOT need editing:
`docs/dev/done/*`, `docs/dev/research/extending-rewind-to-stable-leaves.md`,
`docs/dev/plans/fix-suborbital-not-stable-terminal.md`,
`docs/dev/plans/fix-tree-rewind-supersede-old-side.md`.

## 11. Risks

- Un-seal-on-recommit (section 4): highest risk. Mitigated by the first-commit guard
  (4.1, sequencing-verified) + regression tests 1 and 2.
- Parent-rewind canon: safe ONLY because the guard keeps auto-sealed/sealed forks
  `Immutable` (section 6). Regression test 2 pins it.
- Promotion not reaching a tip -> a should-be-open slot reads `Immutable` -> reaper
  reaps a re-flyable RP (data loss). Mitigated by slot-driven promotion (4.2) + staging
  (keep workaround until proven) + disjointness invariant.
- Guard mis-keying (NB1): a flat-list-keyed guard would skip open-UF tips committed
  mid-flight via `CommitRecordingDirect` and the reaper would reap them. Mitigated by
  keying the guard on committed-TREE membership (4.1) + regression test 4. The stage-1
  instrumentation (confirm no slot relies on the workaround) will surface any residual
  case before stage 3 deletes the workaround.
- Legacy saves with open-but-Immutable-tip slots reaped on first load (7.1). Accepted
  under no-compat; CHANGELOG note.
- Wide test churn (~70+ asserts). Mechanical; preserve intent.

## 12. Decisions locked (formerly open questions)

1. Clobber guard: skip demotion if the recording is in the committed-TREE-membership
   snapshot (taken at the top of CommitTree) OR is a supersede fork (`NewRecordingId` in
   `RecordingSupersedes`). NOT the flat CommittedRecordings list (polluted mid-flight by
   CommitRecordingDirect, NB1) and NOT born-NotCommitted. The fork-identity condition
   covers non-in-place forks that never enter a committed tree. Sequencing-verified.
2. Stash tip: Stash demotes Immutable->CP; `slot.Stashed` retained and monotonic guards
   re-stash (section 5).
3. Flipped tips CAN be forks; the guard keeps forks Immutable so canon is preserved
   (section 6). Earlier "never a fork" claim retracted.
4. Promotion is slot/tip-driven; slot effective tips are disjoint (4.2).
5. No Immutable tip must stay open after the workaround is deleted, GIVEN promotion
   reaches tips (4.2) + guard (4.1). Staged so the workaround is deleted only after
   promotion is proven (stage 3 after stage 1).
6. `IsUnfinishedFlight` / `IsOpenUnfinishedFlight` return false for an Immutable tip;
   promotion uses the shape-only `TryQualify` (7.7).
