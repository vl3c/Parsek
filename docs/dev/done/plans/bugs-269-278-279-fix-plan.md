# Fix Plan: Bugs #269, #278, #279

Status: archived 2026-04-14. Historical fix-plan draft; `#278`/`#279` landed in PR `#176`, and `#269` landed later in PR `#184`.

**Revision note (2026-04-10):** While this branch was open, PR #176 merged a different fix for #278 with the **correct** root-cause diagnosis (this draft's diagnosis was wrong). The PR #176 fix changed `FinalizePendingLimboTreeForRevert` to use real vessel situation instead of blanket-stamping leaves as Destroyed. The snapshot-loss part of the original #278 hypothesis was already covered by #280's PR #167. This plan has been updated to reflect that:

- The "Phase 2" snapshot persistence work in this PR is **defense-in-depth, not the #278 root cause**. It closes real coverage gaps in #280's wiring (the TTL/destroyed sub-path of `CheckDebrisTTL`) and removes a destructive-delete race in `SaveRecordingFiles`. Both are safety nets that harden the persistence path against future regressions; neither is needed for the user-visible #278 symptom which PR #176 already fixed.
- The #279 logging work is unchanged — independent of the #278 root cause, still valid.
- The #286 user-facing complaint entry is unchanged — independent design discussion.

## Background

Three open bugs from `docs/dev/todo-and-known-bugs.md`:

- **#269** — In-game test coverage for quickload-resume flow (PR #160 follow-up)
- **#278** — Capsule not spawned at end of recording (2026-04-09 playtest)
- **#279** — Watch button unavailable from F5 onwards (2026-04-09 playtest, downstream of #278)

This document is the corrected version of the original draft after an independent code review surfaced several errors and gaps. **Plan B (#278/#279) is implementation-ready and ships first.** **Plan A (#269) is documented but deferred to a follow-up PR** because the in-game test framework changes are out of scope for the bug-fix work.

---

## Plan B — #278 + #279 (this PR)

### What the 2026-04-09 playtest log actually shows

`logs/2026-04-09_recording-flow-bugs/KSP.log` evidence:

| Line | Event |
|---|---|
| 9688–10690 | `BgRecorder` logs `Captured snapshot for child vessel … hasSnapshot=True` for every Kerbal X Debris at split time |
| 9662–10558 | `[OnVesselWillDestroy:entry]` fires for each debris pid |
| 9722, 9769, 9816, … | The actual finalization site for some debris is `CheckDebrisTTL → EndDebrisRecording` (the `v == null` "destroyed/despawned" branch at `BackgroundRecorder.cs:684`), NOT directly `OnBackgroundVesselWillDestroy` |
| 11539–11558 | `MergeDialog.BuildDefaultVesselDecisions` reports `hasSnapshot=False canPersist=False` for every Kerbal X Debris |
| 11548 | Bob Kerman EVA still has `hasSnapshot=True canPersist=False` — proves the loss is specific to background-split debris |

### What the #280 fix actually changed

Commit `5b9a494` added `BackgroundRecorder.PersistFinalizedRecording` and wired it into `OnBackgroundVesselWillDestroy` (`BackgroundRecorder.cs:1219`) and `Shutdown` (`BackgroundRecorder.cs:1293`). It writes the `.prec` AND `_vessel.craft` sidecars via `RecordingStore.SaveRecordingFiles`, bypassing the FilesDirty/OnSave path.

Critical: **the #280 fix did NOT touch `EndDebrisRecording`** (the TTL path at `BackgroundRecorder.cs:746`). The 2026-04-09 log proves this path was the actual finalization site for many of the lost-snapshot debris.

### What the original review (Plan B v1) got wrong

- v1 framed Phase 2 (TTL path fix) as **conditional on a Phase 1 re-test** outcome. The reviewer correctly observed that the log evidence already proves the TTL path is hit, so Phase 2 is **mandatory** regardless of re-test outcome.
- v1 missed `ParsekFlight.cs:5810` — `FinalizeIndividualRecording` defensively nulls `VesselSnapshot` when the vessel pid lookup fails. Combined with `RecordingStore.cs:3077-3086`'s destructive delete of stale `_vessel.craft`, this creates a second loss path: persist-on-destroy (#280) writes the file, FinalizeIndividualRecording nulls in-memory, next OnSave sees null + FilesDirty → DELETES the file.
- v1 treated `SaveRecordingFiles` as additive. It is destructive when `VesselSnapshot == null`.

### Implementation (this PR)

**Three surgical changes + tests + docs.**

#### Change 1 — `BackgroundRecorder.EndDebrisRecording` persists snapshot to disk

`Source/Parsek/BackgroundRecorder.cs` `EndDebrisRecording` (~line 746). After the recording is finalized (`TerminalStateValue` set, terminal orbit captured) and BEFORE `OnVesselRemovedFromBackground` cleans up tracking state, call:

```csharp
PersistFinalizedRecording(rec, $"EndDebrisRecording pid={vesselPid}");
```

This mirrors the #280 fix into the TTL/destroyed path. Idempotent — `SaveRecordingFiles` is safe to call multiple times for the same recording (it's a full rewrite).

**Why this matters:** the 2026-04-09 log shows debris vessels finalized via this path. Without persistence here, the in-memory snapshot is the only copy until the next OnSave, which is exactly where #280's root-cause hole lives.

#### Change 2 — `RecordingStore.SaveRecordingFiles` does NOT destructively delete `_vessel.craft`

`Source/Parsek/RecordingStore.cs` lines 3077-3086. Current code:

```csharp
else if (File.Exists(vesselPath))
{
    try { File.Delete(vesselPath); }
    catch (Exception ex) { Log($"Failed deleting stale vessel snapshot ..."); }
}
```

This auto-deletion was a "stale-cleanup" idea but is in fact destroying perfectly good data — when an in-memory null is transient (e.g., set by `FinalizeIndividualRecording`'s defensive null at `ParsekFlight.cs:5810` after the snapshot was already persisted on disk), the next save destroys the on-disk copy.

Replace with: leave the file alone. Document the rationale in a comment. Stale-cleanup is a separate concern that should be handled by an explicit recording-deletion path, not by every save.

**Defense-in-depth.** This change alone closes the destructive-delete race even if other code paths null the in-memory snapshot.

#### Change 3 — `RecordingsTableUI` Watch button transition logging (#279)

`Source/Parsek/UI/RecordingsTableUI.cs`. Per the bug entry, add INFO-level logging when the per-row Watch button's enabled state flips, keyed by recording id. Implementation:

- New private field `Dictionary<int, bool> lastWatchEnabledByIndex` on `RecordingsTableUI`.
- In the per-row W button block (around line 773), after computing `canWatch`, compare against the dict. On transition emit:
  ```
  ParsekLog.Info("UI", $"Watch button #{ri} '{rec.VesselName}' {(canWatch ? "enabled" : "disabled")} " +
      $"(hasGhost={hasGhost} sameBody={sameBody} inRange={inRange} debris={rec.IsDebris})")
  ```
  Update `lastWatchEnabledByIndex[ri] = canWatch`.
- Same change in the group-level W button block (~line 1131).
- Cleanup pass: after iterating committed recordings, prune dict keys not in the current set. Avoids leaking entries when recordings are removed (rewind/truncate).
- **Rate-limit**: the dict diff naturally handles `OnGUI`'s multi-event-per-frame firing. One log per actual transition. Mirror the existing `lastCanFF` pattern for the FF button (`RecordingsTableUI.cs:815-818`).

**Tooltip audit (4a from review):** Per-row W button at `RecordingsTableUI.cs:769-800` already covers all 4 disabled branches (`IsDebris`, `!hasGhost`, `!sameBody`, `!inRange`) as of the #275 fix. Group-level W button at `RecordingsTableUI.cs:1131` lacks `&& !rec.IsDebris` but `FindGroupMainRecordingIndex` excludes debris, so it's harmless asymmetry. Add a `// IsDebris excluded by FindGroupMainRecordingIndex` comment for clarity. No code change needed beyond the comment.

#### Tests

New file `Source/Parsek.Tests/BackgroundRecorderSnapshotPersistenceTests.cs`:

1. `EndDebrisRecording_PersistsSnapshotToDisk` — pin Change 1. Build a synthetic background tree, drive the destroyed-via-TTL path, assert `SaveRecordingFiles` wrote the `.prec` and `_vessel.craft` sidecars.
2. `SaveRecordingFiles_PreservesSidecarWhenInMemorySnapshotIsNull` — pin Change 2. Write a sidecar via a real save, null the in-memory snapshot, call `SaveRecordingFiles` again, assert the sidecar still exists on disk.

Update `Source/Parsek.Tests/RecordingsTableUITests.cs`:

3. `WatchButtonTransitionLogging_OneLinePerTransition` — pin Change 3. Drive the per-row gating predicate twice with different state, assert exactly one INFO log line was emitted (use `ParsekLog.TestSinkForTesting`). Then drive 100 invocations with stable state and assert 0 additional log lines (no spam).

Working dir caveat: tests run from `bin/Debug/net472/`, so any path-relative I/O needs 5 `..` to reach the project root. Use `RecordingStore.SuppressLogging = true` and `RecordingStore.ResetForTesting()` for cleanup. Mark tests `[Collection("Sequential")]` if they touch shared static state.

#### Docs

- Strike through #278 and #279 entries in `docs/dev/todo-and-known-bugs.md` with `Status: Fixed` and a one-line summary referencing this PR.
- Add a new bug entry (next free number, likely #285) capturing the user-facing complaint that #278's fix does NOT resolve: **even with snapshots restored, `MergeDialog.CanPersistVessel` hard-blocks `terminal=Destroyed`, so a full-tree crash leaves `spawnable=0` and the user has nothing to continue with.** Three options to weigh: (a) pre-crash F5 fallback, (b) relax Destroyed gating with snapshot, (c) status-quo + UX message in the merge dialog. This is a design decision, not a bug fix.
- Open a follow-up tech-debt entry for `ParsekFlight.cs:5810` defensive null. The defensive null at `FinalizeIndividualRecording` came from refactor commit `8edc692` without a strong rationale and could destroy snapshot data in same-session merge cases (no F9). Removing it has wide blast radius — there are 100+ `VesselSnapshot != null` readers across the codebase. Defer until someone has time to audit each reader.
- One-liner CHANGELOG entries for #278 and #279 (per the HARD RULE).

#### What this PR does NOT fix

- The "no F9, full-tree crash, in-session merge" path. `FinalizeIndividualRecording`'s defensive null still wipes the in-memory snapshot before the merge dialog reads it. Mitigated for the post-F9 case by Change 1 (TTL persist) + #280 (destroy persist) + Change 2 (no sidecar delete) — F9 reload re-hydrates from disk. Same-session no-F9 case is the remaining gap.
- The user-facing "nothing to continue with after crash" complaint — opens new bug entry instead.
- Bug #269 in-game tests — see Plan A below, deferred to follow-up PR.

---

## Plan A — #269 (deferred follow-up PR)

### Corrected approach

The reviewer correctly flagged the original Plan A's `InGameTestStateBridge` as over-engineered. The simpler approach is to **piggyback on the `ParsekHarmony` pattern**: change `TestRunnerShortcut` from `[KSPAddon(KSPAddon.Startup.EveryScene, false)]` to `[KSPAddon(KSPAddon.Startup.Instantly, true)]` and add `DontDestroyOnLoad(gameObject)` in `Awake`. The existing instance survives F9 — no separate bridge class needed.

### Corrected test list

Drop or defer two of the original 7 tests:

- **Test 3 (vessel switch finalize)** — DEFERRED. Original Plan A claimed `FlightGlobals.SetActiveVessel(otherVessel)` triggers `FinalizePendingLimboTreeForRevert`. It does not — that path requires a full scene reload via `OnVesselSwitching → vesselSwitchPending` flag → next `OnLoad`. The in-process switch observes nothing. Either rewrite to drive `OnLoad` synthetically (brittle) or defer until #266 lands and the test contract is no longer "temporary".
- **Test 7 Full (cold-start manual sentinel)** — DROPPED. Not a test; a manual QA procedure. Document separately in `docs/dev/manual-testing/`.

The remaining 5 tests in Plan A v1 (Tests 1, 2, 4, 5, 6) are valid — Tests 1, 4, and 6 are the highest-value ones because they exercise the actual quickload-resume coroutine that PR #160 added, which has zero in-game coverage today.

### Other corrections

- Don't cite `RecordingStore.cs:2217-2242` as the F9 quickload mirror — that's the rewind path, which loads `SPACECENTER` not `FLIGHT`. The right reference for F9 is the standard `GamePersistence.LoadGame` + `HighLogic.LoadScene(GameScenes.FLIGHT)` flow.
- Specify state reset between tests: any test that mutates `MilestoneStore.CurrentEpoch` or `ParsekSettings.Current.autoMerge` MUST restore in a `try/finally`.
- Modal dialogs (save confirmation, merge dialog) can block test coroutines. Tests that trigger them need to either auto-dismiss or skip if the dialog appears.
- The bridge result still needs to surface in the post-reload `TestRunnerShortcut` instance. Plan A v1 left this unspecified. Approach: post-reload, the surviving runner instance reads its own static state buffer and renders the test result row directly — no reflection table needed because the runner host is now the persistent class itself.

### Implementation phases (when this PR is written)

1. Change `TestRunnerShortcut` attributes + `DontDestroyOnLoad(gameObject)` in `Awake`. Add a canary `BridgeSurvivesSceneTransition` test to pin the behavior.
2. Add `Source/Parsek/InGameTests/Helpers/QuickloadResumeHelpers.cs` per Plan A v1's helper list (minus the wrong RecordingStore citation).
3. Implement Tests 1, 4, 6 in `Source/Parsek/InGameTests/QuickloadResumeTests.cs`. These are the must-haves.
4. Implement Tests 2, 5 as bonus coverage.
5. Document Test 3 deferral with a `// TODO: re-enable when #266 lands` placeholder method.
6. Document Test 7 cold-start as a manual procedure in `docs/dev/manual-testing/cold-start-quickload-resume.md`.

---

## Risk callouts (this PR)

- **Change 1** could double-write the sidecar if both `OnBackgroundVesselWillDestroy` AND `EndDebrisRecording` fire for the same vessel (e.g., destroyed vessel that already had its TTL armed). `SaveRecordingFiles` is idempotent — same content, no corruption. The cost is one extra disk write per debris vessel. Acceptable.
- **Change 2** changes destructive behavior of `SaveRecordingFiles` — anything that relies on auto-cleanup of stale sidecars will leave files around. Audit shows no such caller; cleanup is done by explicit recording-deletion paths (`DeleteRecordingFiles`). Safe.
- **Change 3** allocates a small dict per `RecordingsTableUI` instance. No memory pressure. Cleanup pass prevents leaks across rewind/truncate.
- **Tests** use real disk I/O. Path resolution from `bin/Debug/net472/` needs 5 `..` per the MEMORY.md note.

## Validation

After commits land:

1. Re-run the 2026-04-09 Kerbal X scenario (load `s32`, repeat launch + radial booster crash). Expected:
   - All Kerbal X Debris show `hasSnapshot=True` in `BuildDefaultVesselDecisions`.
   - Merge dialog reports non-zero `spawnable` count.
   - W buttons clickable on at least the non-debris rows.
   - New `Watch button #N enabled` log lines visible at the moment ghosts come into range.
2. Run the unit test suite — baseline should be 2419+ passing per MEMORY.md. The 3 new tests should pass cleanly.
3. Verify the new follow-up bug entry exists.
