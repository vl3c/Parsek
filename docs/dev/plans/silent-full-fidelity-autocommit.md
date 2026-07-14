# Silent full-fidelity auto-commit

Status: IMPLEMENTED on branch `automerge-full-fidelity` (design revised after review panel, 2026-07-13)
Author: 2026-07-13
Scope: reimplement the `autoMerge` ON path so it commits recordings to the timeline
silently **with full spawn-at-end fidelity**, instead of the current lossy ghost-only
shortcut. Default stays OFF in this PR; flipping the default is a gated follow-up.

## 1. Problem

`autoMerge` (`ParsekSettings.autoMerge`, default `false`) exists so a player can skip the
per-mission "Merge to Timeline / Discard" confirmation dialog. Today the ON path is lossy:
it discards vessel snapshots and commits recordings **ghost-only**, so a vessel you leave
surviving (in orbit / landed) never re-materializes as a real vessel at its recording end.
The confirmation-dialog path (`autoMerge=false`) does NOT have this problem: it commits
with the smart per-leaf decision (`BuildDefaultVesselDecisions` -> `ShouldSpawnAtRecordingEnd`),
keeping surviving vessels spawnable.

### 1.1 The concrete lossiness (traced)

For a surviving vessel leaving flight with `autoMerge=ON`:

1. **Scene exit** — `ParsekFlight.CommitTreeSceneExit` finalizes and re-snapshots
   stable-terminal recordings (Landed/Splashed/Orbiting), keeps the snapshot **in memory**,
   marks `FilesDirty`, but does **not** force-write the sidecar. (The `autoMerge=OFF`
   branch immediately above it, `ParsekFlight.cs:2935-2949`, *does* force-write dirty
   sidecars precisely to close this hole — that fix, #289, was applied to the OFF branch
   only.) Note: `CommitTreeSceneExit` is not blanket ghost-only — it preserves the
   stable-terminal snapshots in memory (`ParsekFlight.cs:14394-14405`); the actual nulling
   happens later at OnLoad.
2. **OnLoad** (same scene transition) — the auto-merge branch (`ParsekScenario.cs:3372`)
   calls `AutoCommitTreeGhostOnly` (`ParsekScenario.cs:6776`), which **nulls every in-memory
   `VesselSnapshot`** — including the stable-terminal ones step 1 preserved — *before*
   `CommitPendingTreeAsApplied` commits. Nothing writes the snapshot to disk in between.
3. **Later flushes** — `RecordingSidecarStore.SaveRecordingFilesToPathsInternal`
   (`RecordingSidecarStore.cs:1108`) writes `_vessel.craft` **only when
   `VesselSnapshot != null`**. It is null now, so the write is skipped; a #278 guard means
   the null does not delete any existing sidecar, but there is no fresh one to write.
4. **Ghost reaches its end** — `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd`
   (`GhostPlaybackLogic.cs:4144`) sees `VesselSnapshot == null`, tries
   `TryHydrateVesselSnapshotFromSidecar`; the finalized snapshot was never written, so it
   hydrates a stale in-flight sidecar (wrong position/situation -> rejected by the
   "situation unsafe" check) or nothing -> the vessel silently does not re-materialize.

`PersistFinalizedRecording` (the immediate-write that *does* save finalized snapshots) runs
for **background/debris** recordings (`BackgroundRecorder.cs:2658`) and a flight-terminal
caller (`ParsekFlight.TerminalEvents.cs:381`) — never the main spawn-at-end scene-exit path.

### 1.2 The irony

`ShouldShowDialogBeforeSceneChange` (`SceneExitInterceptor.cs:153`) already forces the
confirmation dialog for landed/splashed vessels exiting to KSC/TS even when `autoMerge`
is ON (the #88 approval), and always for Re-Fly attempts. So today's ON path is **silent
exactly where it loses data** (orbiting survivor) and **still nags** where it is safe
(landed/splashed, whose dialog path preserves the vessel).

## 2. Current control flow (map)

The primary merge-decision seam is the pre-transition prefix (`SceneExitInterceptor`, gated
by the pure `ShouldShowDialogBeforeSceneChange`). The `ParsekScenario.OnLoad`
"pending-outside-flight" branches are the deferred fallback for when the prefix misses.

**`ShouldShowDialogBeforeSceneChange`** (`SceneExitInterceptor.cs:153`) — pre-transition:

| Condition | Result |
|---|---|
| no active tree & no switch segment | `None` |
| `reFlyActive` | `ReFlyAttempt` (dialog — always) |
| `!isAutoMerge` | `RegularMerge` (dialog) |
| autoMerge & dest == MAINMENU | `RegularMerge` (dialog) |
| autoMerge & dest in {SPACECENTER, TRACKSTATION} & landedOrSplashed | `RegularMerge` (#88 approval) |
| else (autoMerge, orbiting/other) | `None` -> auto-commit |

### 2.1 The THREE silent-commit sites (all lossy today)

The doc's first draft named only one; the review found three. All call
`CommitPendingTreeAsApplied`:

1. **OnSave safety-net** — `SafetyNetAutoCommitPending` (`ParsekScenario.cs:1054-1073`,
   commit at :1067). Defense-in-depth, documented as normally-unreachable. Runs inside
   OnSave. Uses `AutoCommitTreeGhostOnly` (:1060).
2. **Warm scene-change fallback** — `ParsekScenario.cs:3327-3384` (commit at :3378). Fires
   when the prefix missed a FLIGHT->non-FLIGHT transition. Gated on `IsAutoMerge` and the
   independent #88 gate `GhostPlaybackLogic.ShouldShowCommitApproval` (:3343-3344 ->
   `GhostPlaybackLogic.WarpLoopPolicy.cs:174`), which defers landed/splashed to
   `ShowDeferredMergeDialog`; the `else` at :3367 auto-commits ghost-only via
   `AutoCommitTreeGhostOnly` (:3372). **No re-fly gate.**
3. **Cold-load pending-outside-flight** — `ParsekScenario.cs:3712-3781` (commit at :3769).
   The "Esc > Abort Mission -> Space Center" / Resume-Saved-Game path. **Open-codes** the
   null loop (:3759-3764) then commits under `if (IsAutoMerge || MAINMENU)` (:3753) — it
   does NOT call `AutoCommitTreeGhostOnly`, so a "callers of `AutoCommitTreeGhostOnly`"
   scan misses it. Its commit is gated on `HasPendingTree` only (not Finalized), so it can
   commit a Limbo resume-stash. No re-fly gate, no #88 gate.

**Two independent #88 gates exist:** the pre-transition
`ShouldShowDialogBeforeSceneChange` table row, and the OnLoad-warm
`ShouldShowCommitApproval` call (:3344). Folding #88 requires addressing both.

Commit outcomes today: dialog "Merge to Timeline" (`MergeCommit` +
`BuildDefaultVesselDecisions`) = full fidelity; all three auto-commit sites = ghost-only.

## 3. Goal & principles

- P1. The silent auto-commit must produce the **same timeline result** as clicking "Merge
  to Timeline" — same spawn-at-end fidelity, same resource/ledger outcome.
- P2. Reuse the dialog's already-tested commit machinery (`MergeCommit` +
  `BuildDefaultVesselDecisions`); do not fork a second commit implementation.
- P3. Preserve every safety checkpoint the dialog exists to guarantee: revert-after-crash
  (#434), idle-on-pad auto-discard, and **Re-Fly's dialog-gated commit** — a silent commit
  must NEVER trigger a re-fly supersede.
- P4. Minimal blast radius: upgrade only the specific case that needs fidelity; leave
  re-fly / Limbo / MAINMENU / OnSave-safety-net behavior byte-for-byte unchanged.
- P5. No new serialized fields, no schema-generation bump. Control-flow rewire only.

## 4. Design

### 4.1 The routing rule

Introduce one shared predicate: a pending tree qualifies for the **full-fidelity silent
commit** iff ALL of:
- `IsAutoMerge` is ON, AND
- `PendingTreeState == Finalized` (never a Limbo resume-stash), AND
- no active re-fly (`ParsekScenario.Instance?.ActiveReFlySessionMarker == null`), AND
- the destination scene is not MAINMENU (`HighLogic.LoadedScene != GameScenes.MAINMENU`) —
  in practice SPACECENTER or TRACKSTATION, since a pending tree only reaches these
  outside-Flight sites on a non-revert exit and FLIGHT->EDITOR is revert-gated.

When it qualifies, commit via the dialog's own path:
```
var decisions = MergeDialog.BuildDefaultVesselDecisions(pending);   // 1-arg: safe, see 4.2
int spawnCount = decisions.Values.Count(v => v);
MergeDialog.MergeCommit(pending, decisions, spawnCount);            // full fidelity, no popup
```
`MergeCommit` already runs `ApplyVesselDecisions` (keeps spawnable leaves' snapshots +
preserves `GhostVisualSnapshot` per #271, nulls the rest), `CommitPendingTree`,
`MarkTreeAsApplied`, `RunOptimizationPass`, `NotifyLedgerTreeCommitted`,
`SwapReservedCrewInFlight`, clears the `SwitchSegmentSession`, and fires `OnTreeCommitted`.
Its M1 guard (`pending == RecordingStore.PendingTree`) holds at both OnLoad call sites.

When it does NOT qualify (re-fly / Limbo / MAINMENU), keep the **existing ghost-only
commit unchanged** (`AutoCommitTreeGhostOnly` + `CommitPendingTreeAsApplied` at the warm
site; the inline null loop at the cold site). This preserves today's benign behavior:
- Re-fly reaching the OnLoad fallback still ghost-only-orphans (reaped by `LoadTimeSweep`);
  the re-fly merge decision stays dialog-gated / journal-driven (never silently superseded).
- Limbo resume-stashes are never heavier-committed.
- MAINMENU (game unloading) stays lightweight — no quicksave / spawn-at-end during unload.

Apply this rule at **site 2 (warm)** and **site 3 (cold)**. **Site 1 (OnSave safety-net)
stays ghost-only** — routing it through `MergeCommit` would run `RefreshQuicksaveAfterMerge`
(a quicksave) inside OnSave, a reentrancy hazard analogous to the "never SaveGame from
inside OnLoad" rule; it is unreachable defense-in-depth that never needs spawn-at-end.

**Quicksave suppression on the silent path (impl-review fix).** Sites 2 and 3 run inside
`OnLoad`, so the silent `MergeCommit` passes `refreshQuicksaveAfterCommit: false` to skip
`RefreshQuicksaveAfterMerge` — a `GamePersistence.SaveGame` there would re-enter every
`ScenarioModule.OnSave` mid-load (the same rule) and would snapshot before the OnLoad ledger
recalc. The commit is still durable via the next normal OnSave, exactly as the old ghost-only
auto-commit was (it never refreshed the quicksave either); and because the commit completes
synchronously before the OnLoad `RecalculateAndPatch`, the just-committed tree's ledger
actions are patched into the in-memory scalars that same frame.

**Scene-exit force-write (as implemented).** For site 2 to have a durable finalized
snapshot, the scene-exit stash must force-write dirty sidecars the way the `autoMerge=OFF`
branch already does (`ParsekFlight.cs:2935-2949`). Rather than restructure the
`FinalizeTreeOnSceneChange` dispatch, the ON-branch's existing `CommitTreeSceneExit` is
kept and the force-write loop is added inside it (after the snapshot-null pass). This is
the more surgical change: `CommitTreeSceneExit` already preserves the stable-terminal
(spawn-at-end) snapshots and copies `GhostVisualSnapshot` before nulling the non-spawnable
ones, and it now also releases their crew reservation (matching the dialog path's
`ApplyVesselDecisions` ghost-only branch, so crew bookkeeping is at parity). Non-stable
leaves are never spawnable, so nulling them at scene exit loses nothing the silent commit
would have kept. Site 3 (cold load) reads the tree from disk, so it relies on that same
force-write having happened when the tree was stashed in the prior session — no additional
write needed at the cold site.

**De-duplication.** At site 2, the surrounding branch also calls
`ScreenMessages.PostScreenMessage` (:3380) and `RunOptimizationPass` (:3382); `MergeCommit`
already does both (`MergeDialog.Commit.cs:85`, :140-145). Remove the trailing duplicates
when swapping in `MergeCommit`. At site 3, drop the inline null loop and the ScenarioLog
commit line for the qualifying case (`MergeCommit` logs its own).

### 4.2 Re-Fly gate (resolves the P3 blocker)

The OnLoad fallback branches have **no re-fly gate today**, and the doc's first draft wrongly
claimed re-fly is always false there. It is not: a prefix-missed exit during an active re-fly
reaches these branches. `MergeCommit` unconditionally runs `TryCommitReFlySupersede` when
`ActiveReFlySessionMarker != null` (`MergeDialog.Commit.cs:101-102`) — writing supersede rows
and flipping MergeState, the exact irreversible timeline mutation P3 forbids doing silently.
The routing rule (4.1) excludes re-fly from the full-fidelity path, so re-fly keeps its
current ghost-only-orphan behavior and its merge stays dialog/journal-gated.

Because re-fly is excluded, the **1-arg `BuildDefaultVesselDecisions(pending)`** is
sufficient on the silent path: the 3-arg overload's `suppressedRecordingIds` /
`activeReFlyTargetId` closure only matters for a live re-fly commit, which never reaches
this path.

### 4.3 #88 fold — make landed/splashed silent (both gates)

With the fidelity fix, the silent path is safe for surviving vessels, so #88 no longer needs
to interrupt silent mode. Fold BOTH gates:
- **Pre-transition** (`ShouldShowDialogBeforeSceneChange`, `SceneExitInterceptor.cs:173-178`):
  drop the `autoMerge & landed/splashed & KSC/TS -> RegularMerge` row so it returns `None`
  (auto-commit).
- **OnLoad-warm** (site 2): replace the `ShouldShowCommitApproval`-based `showApproval` gate
  (:3343-3366) with the re-fly gate from 4.1. `ShouldShowCommitApproval`
  (`GhostPlaybackLogic.WarpLoopPolicy.cs:174`) then has no remaining caller (verified: only
  :3344) — remove it and its 8 pinning tests (`Bug156Tests.cs:314-341`).
- Site 3 (cold load) never had an approval gate, so folding #88 there is automatic: its
  qualifying case simply upgrades from ghost-only to full fidelity.

Kept as dialogs under `autoMerge`: **Re-Fly** (irreversible timeline branch) and **MAINMENU**
(no destination scene; may be an abandon). The player's discard escape hatch when silent is
the Recordings window (post-hoc discard); revert-after-crash is separately protected (4.4).

This #88 fold is the one genuinely debatable UX call; it is separable from 4.1. If a reviewer
prefers conservatism, ship 4.1 alone and leave both #88 gates in place. Recommendation: fold
it — otherwise `autoMerge=ON` still nags on every landed/splashed mission, defeating the goal.

### 4.4 Edge cases preserved (unchanged by this PR)

- **Revert-after-crash (#434).** `ShowPostDestructionTreeMergeDialog` (`ParsekFlight.cs:3118`)
  always **stashes** on destruction regardless of `IsAutoMerge` (:3263); the commit happens
  later at OnLoad after a non-revert exit, and both fallback branches are `!isRevert`-gated
  (:3327 warm; the cold branch runs only on non-revert loads). The silent MergeCommit cannot
  bake a recording before the stock crash/results Revert. Unchanged.
- **Idle-on-pad.** Auto-discarded (Finalized-only) before commit on all paths (:3349 warm,
  :3738 cold, :5361 deferred). Unchanged.
- **Switch-segment sessions.** Can reach the silent path; routing through `MergeCommit` is
  strictly better than the ghost-only path — it clears the `SwitchSegmentSession` marker on
  success (`MergeDialog.Commit.cs:120-130`), which the ghost-only path did not.
- **Quickload-resume Limbo.** Excluded from the full-fidelity path by the `Finalized` gate
  (4.1); resume-flow stashes are never heavier-committed.
- **RouteRunPrompt + screen message.** `MergeCommit` fires
  `Logistics.RouteRunPrompt.NotifyTreeCommitted` (:139, a one-time non-blocking prompt only
  for trees carrying a route proof) and a "Merged - N vessel(s)..." screen message
  (:140-145). This matches the dialog path exactly, so the silent path gains parity, not new
  modal interruptions. Deliberate: kept for consistency. (If we later want the silent path
  fully quiet, add a `silent` flag threaded into `MergeCommit` — out of scope here.)

## 5. Snapshot / sidecar lifecycle after the change

For a surviving (Orbiting/Landed/Splashed) recording on the qualifying silent path:
1. Scene exit: finalize re-snapshots the terminal state, snapshot preserved in memory,
   dirty sidecars **force-written** (finalized `_vessel.craft` durable on disk).
2. Commit: `MergeCommit` -> `ApplyVesselDecisions` keeps the spawnable leaf's snapshot
   (does NOT null it), commits, marks applied, optimizes.
3. Spawn-at-end: `ShouldSpawnAtRecordingEnd` finds a live in-memory snapshot (or hydrates
   the freshly-written sidecar after a reload) -> materializes the vessel correctly.

No timing hole: the finalized snapshot is durable before any null, and the spawnable leaf is
never nulled. Cold-load (site 3) relies on the prior session's force-written sidecar.

## 6. Setting semantics & default

- The `autoMerge` toggle stays, with a cleaner meaning: **ON = silent full-fidelity
  auto-commit; OFF = confirm each mission**. Update the tooltip
  (`UI/SettingsWindowUI.cs:291`) to "Commit recordings to the timeline automatically, with
  no confirmation dialog."
- **Default stays `false` in this PR.** Reimplementing the ON path and flipping the default
  are separate risks; validate the new ON path in-game first (§8), then flip in a follow-up.
  The eventual goal (per the feature request) is default-ON. Flipping is a one-line change
  (`ParsekSettings.cs:46` + `UI/SettingsWindowPresentation.cs` defaults) + a CHANGELOG note.

## 7. Test plan

**xUnit (pure / headless):**
- `ShouldShowDialogBeforeSceneChange` (#88 fold): existing tests that currently assert
  `RegularMerge` FLIP to `None` and must be updated/renamed —
  `SceneExitInterceptorTests.Decision_AutoMergeOn_LandedAtKsc_ReturnsRegularMerge` (:331),
  `Decision_AutoMergeOn_LandedAtTs_ReturnsRegularMerge` (:343), and the pending-tree overload
  `LivePendingTreeDecision_FinalizedLandedPendingTree_AutoMergeOn_ReturnsRegularMerge` (:134).
  Add: orbiting -> `None`, Re-Fly -> `ReFlyAttempt`, MAINMENU -> `RegularMerge`,
  `!isAutoMerge` -> `RegularMerge` (unchanged).
- `ShouldShowCommitApproval` removal: delete `Bug156Tests.cs:314-341` (the 4
  Landed/Splashed-at-KSC/TS-return-true cases) with the helper.
- The full-fidelity routing predicate (4.1) is pure and unit-testable in isolation: qualifies
  iff autoMerge & Finalized & no-reFly & KSC/TS. Cover each disqualifier (reFly, Limbo,
  MAINMENU) returning "keep ghost-only".
- `ScenarioAutoCommitResourcesAppliedTests`: the silent path now routes through
  `MergeDialog.MergeCommit` -> `RecordingStore.CommitPendingTree` + `MarkTreeAsApplied`, NOT
  the `CommitPendingTreeAsApplied` wrapper these tests exercise directly, so they do not break;
  `SceneExitAutoMerge_AdvancesRecordingIndexes` (:162) comment updated to note the retired routing.
- Full-fidelity retention (the P1 contract): `MergeDialogVesselTests.ApplyVesselDecisions_
  KeepsSpawnableSnapshot_NullsGhostOnly` (added) pins that a spawnable leaf keeps its
  `VesselSnapshot` while a ghost-only leaf is nulled with its `GhostVisualSnapshot` preserved
  (`ApplyVesselDecisions` widened to `internal static`). `BuildDefaultVesselDecisions` /
  `CanPersistVessel` already cover which leaves are spawnable.

**In-game (`InGameTests`, live KSP):**
- FLIGHT test: with `autoMerge=ON`, fly to a stable orbit, trigger the scene-exit commit,
  assert the committed leaf is spawn-at-end eligible (snapshot present / sidecar written), not
  ghost-only. Use the existing `autoMerge` save/restore try/finally pattern
  (`RuntimeTests.cs:13407`).

**Harness / autotest:** no scenario `.toml` currently declares a D1 "auto-merge" case and
`AnswerMergeDialog` is a reserved/unimplemented verb, so the #88 fold strands no live harness
scenario today; it only constrains the future auto-merge coverage design. Flag for the harness
owner as a note, not a required change.

**Post-Change Checklist:** no enum/field/schema changes; run full `dotnet test`.

## 8. In-game validation runbook (PENDING OPERATOR — for the default-flip follow-up)

1. `autoMerge=ON`: launch, reach stable LKO, exit to Space Center. Expect: no dialog;
   recording committed; vessel present as a real vessel (Tracking Station / spawn-at-end),
   NOT ghost-only.
2. `autoMerge=ON`: land a vessel, exit to KSC. Expect: no dialog (#88 folded); vessel persists.
3. `autoMerge=ON`: quit mid-mission (Esc > Abort/Space Center) with a surviving orbital
   vessel, then Resume Saved Game. Expect (cold-load site): recording committed full-fidelity,
   vessel persists (this exercises site 3).
4. `autoMerge=ON`: crash a vessel, pick **Revert** from the stock results dialog. Expect:
   career rolled back, no committed recording/ghost survives (#434 intact).
5. `autoMerge=ON`: Re-Fly attempt, exit flight. Expect: the Re-Fly confirmation dialog STILL
   appears (not silently committed / superseded).

## 9. Risks & rollback

- **Risk (resolved):** `MergeCommit` at OnLoad runs heavier work than `AutoCommitTreeGhostOnly`
  did — optimization pass, crew swap, `NotifyLedgerTreeCommitted`. The one genuinely new
  timing, a `RefreshQuicksaveAfterMerge` quicksave firing inside the OnLoad frame (flagged by
  the impl-review panel), is suppressed via `refreshQuicksaveAfterCommit: false` (§4.1), so no
  `SaveGame` runs mid-load and no quicksave captures pre-recalc scalars.
- **Risk:** more spawn-at-end materialization + slightly more sidecar data (spawnable leaves
  retain snapshots). Intended, correct, matches the dialog path.
- **Rollback:** control-flow rewire behind the existing `autoMerge` flag (still default OFF).
  If the new ON path misbehaves, players are unaffected (default OFF) and the branch reverts
  cleanly — no schema/data migration.

## 10. Out of scope

- Flipping the default to ON (gated follow-up, §6/§8).
- Any change to the `autoMerge=OFF` dialog path.
- Gloops / `IsGhostOnly` recordings (already correctly ghost-only via
  `ShouldSpawnAtRecordingEnd`).
- Re-Fly / MAINMENU / OnSave-safety-net silent full-fidelity commit (kept as-is by design).
- A `silent` flag to suppress `MergeCommit`'s route prompt / screen message (§4.4).
