# Fix: drop rewound-out supersede relations on Rewind-to-Launch

## Bug evidence

`logs/2026-05-08_1740_rewind-and-refly-regressions/`. After two Re-Flys on a
Kerbal X recording (slot=1 Probe at 17:33:30 then slot=0 booster at 17:34:42),
the user clicked Rewind on the launch row at 17:36:43 and re-launched. The
original Kerbal X recording (#0) and the original Kerbal X Probe recording (#7)
stayed skipped from playback:

```
KSP.log:129371  Ghost playback skip state: #0 ... reason=superseded-by-relation
KSP.log:129373  Ghost playback skip state: #7 ... reason=superseded-by-relation
KSP.log:128867  RecordingSupersedes loaded: 2     (post-rewind, still 2)
```

`rewindUT=6.52`. Both forks (`rec_07f2168…` and `rec_929b44d…`) have
`StartUT=31.56`. The user has rewound past the moment the forks were created,
but the supersede rows persist through Rewind, so the rewound source
recordings stay suppressed during the new launch.

## Root cause

`RecordingStore.InitiateRewind` does not roll back the
`RecordingSupersedeRelation` ledger. The supersede compute
(`EffectiveState.ComputeSupersededRecordingIdsByRelation`) is unchanged — it
correctly returns the set of `OldRecordingId`s for which a `NewRecordingId`
exists in the live recordings list, regardless of UT context.

This is **not** a regression introduced by the open PR stack. The supersede
mechanics on `origin/main` and the stack tip are byte-identical (no diff in
`SupersedeCommit.cs`, `EffectiveState.cs`, `MergeJournalOrchestrator.cs`,
`RewindContext.cs`, `ParsekScenario.cs`). The reason mainline appeared to
"work" for the user is that they never compared apples-to-apples: the
mainline tests they remember were Rewind-without-prior-Re-Fly (no supersedes
existed → no suppression → ghost replays). The stack tests were
Rewind-after-Re-Fly. Same flow on mainline produces the same suppression.

## Goals

- After Rewind-to-Launch on a recording, supersede relations whose forks
  start at or after the rewindUT (and whose source belongs to the rewound
  owner's tree) are dropped.
- Branch recordings (e.g. an upper-stage Probe at index #7) are unsuppressed
  too — they carry their own supersede rows whose `OldRecordingId` is the
  branch, not the owner. Walking the whole tree, not just the owner row, is
  load-bearing.
- Multi-generational supersede chains (A → B → C with A as the rewound owner)
  collapse correctly: both A→B and B→C drop because B is reachable through
  the tree walk.
- Rewind without a prior Re-Fly is unaffected (no supersedes exist to drop).
- Rewind during an active re-fly merge journal is refused outright.

## Non-goals — out of scope

- **PRELAUNCH-state rewind save capture.** Mainline captures the rewind save
  at recording-start (vessel already FLYING), strips the active vessel by
  name, winds UT back 15 s, loads SPACECENTER. The user manually returns to
  VAB to relaunch. This works on mainline and is unchanged on the stack.
  Moving capture to PRELAUNCH would be a design improvement (no manual VAB
  navigation) but it is not a regression fix and brings significant scope
  (situation-event hook plumbing, format-version bump, OnLoad strip exemption,
  `FlightDriver.StartAndFocusVessel` route, clamp filter). Not in this PR.
- Section-boundary off-by-ε in `RelativeAnchorResolver.FindTrackSectionForUT`
  (separate PR; logged as open in `todo-and-known-bugs.md`).
- Debris relative-playback discontinuity under sparse anchor samples (separate PR).
- Auto-deletion of orphan post-rewind fork recordings (deferred follow-up).

## Implementation

Two new methods in `RecordingStore`:

`DropSupersedesRewoundOutOfExistencePure(owner, rewindAdjustedUT,
ownerTreeRecordings, liveRecordingsById, supersedes) → int`

Pure-static. Builds `rewoundOutOldIds` = {owner.RecordingId} ∪ {treeRec.RecordingId
| treeRec ∈ ownerTreeRecordings ∧ treeRec.StartUT ≥ rewindAdjustedUT}. Iterates
`supersedes`, queues drops where:
- `rel.OldRecordingId ∈ rewoundOutOldIds`, AND
- `liveRecordingsById[rel.NewRecordingId]` resolves AND its `StartUT ≥ rewindAdjustedUT`

Returns count of dropped relations. The supersede list is mutated in place
(the live entry passes `ParsekScenario.Instance.RecordingSupersedes` directly).

`DropSupersedesRewoundOutOfExistence(owner, rewindAdjustedUT) → int`

Live entry. Resolves `ownerTreeRecordings` from `committedTrees` by `owner.TreeId`,
builds `liveRecordingsById` from `committedRecordings`, fetches the supersede
list from `ParsekScenario.Instance`, delegates to the pure static.

Call site in `RecordingStore.InitiateRewind`:

1. **Merge-journal precondition** before `BeginRewindForOwner`: if
   `ParsekScenario.Instance.ActiveMergeJournal != null && Phase != Complete`,
   refuse the rewind with an error log + screen message and return early.
   Half-fix (rewind plus an unfinished merge corrupting supersedes mid-flight)
   is worse than no-fix; let `MergeJournalOrchestrator.RunFinisher` resolve
   the journal first on the next OnLoad.
2. **Drop call** AFTER `RewindContext.SetAdjustedUT(game.flightState.universalTime)`
   and before `LoadScene(SPACECENTER)`. The drop has to run after `SetAdjustedUT`
   because the comparison uses `RewindAdjustedUT` (post-load value, with the
   15 s wind-back applied). The drop also has to run before LoadScene so the
   updated supersede list is visible to the post-load OnSave that persists
   the rewind-staging state.

Persistence: `RecordingSupersedes` is saved via `ParsekScenario`'s
rewind-staging persist path (`OnSave: rewind-staging persist`). The drop is
in-memory; the next OnSave (triggered by the scene transition) persists the
new list automatically. No explicit force-save needed.

## Files touched

- `Source/Parsek/RecordingStore.cs` — add two helpers, two call sites in
  `InitiateRewind` (precondition + drop).
- `Source/Parsek.Tests/RewindSupersedeRollbackTests.cs` (new) — 7 tests.
- `CHANGELOG.md` — Bug Fixes entry.
- `docs/dev/todo-and-known-bugs.md` — mark this regression done; add open
  follow-up entries for the section-boundary off-by-ε bug and the debris
  relative-playback discontinuity (both observed in the same playtest log).

No format-version bump. No new fields on `Recording`. No codec changes.

## Test plan

- [x] `dotnet test` — 10895 passed, 0 failed (+7 net new in `RewindSupersedeRollbackTests`).
- [x] Pure-static `DropSupersedesRewoundOutOfExistencePure` directly unit-tested
      (owner row, branch row, unrelated tree, fork-before-rewind, multi-gen,
      null args, missing fork in live dict).
- [ ] In-game smoke: launch Kerbal X, do a Re-Fly via the RP slot button,
      click row Rewind, re-launch. Expected: original Kerbal X ghost replays
      from launch alongside the new vessel; no `superseded-by-relation` skip
      for #0 or #7 in the post-rewind log.

## Why this is the right scope

The reviewer's P2 finding — *"those mechanics are not themselves the recent
regression. The plan can still introduce PRELAUNCH capture as a design
improvement, but it should separate that from the regression fix and focus
the causal analysis on open-PR deltas such as persisted supersede
relations"* — is correct. The supersede drop is the minimum sufficient fix.
PRELAUNCH save capture is a design improvement that changes a working system
(per the user's pushback: *"why does the rewind save need to move earlier?
this bug was not present on mainline branch and the system was working
fine"*). Splitting it out keeps this PR focused on the regression and lets a
PRELAUNCH-capture PR be evaluated on its own design merits later if the user
wants the auto-relaunch-onto-the-pad UX improvement.
