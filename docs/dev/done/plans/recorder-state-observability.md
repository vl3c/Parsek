# Recorder State Observability

## Problem

During the post-2026-04-09 playtest cycles, reading `KSP.log` to diagnose F5/F9 recorder bugs has been slower and more error-prone than it should be. Specific issues:

- **Mode is implicit.** There's no single log line that tells you whether the current flight is in standalone mode (`activeTree == null`, `recorder` owns the buffer) or tree mode (`activeTree != null`, tree has recordings, recorder is a worker on one of them). To know, you have to cross-reference events scattered across the file — `StartRecording succeeded`, `Committed tree`, `CreateSplitBranch`, etc.
- **Recording identity gets reshuffled invisibly.** After a chain boundary, EVA, or dock event the `activeRecordingId` changes without a single line that says "activeRecordingId was X, is now Y, reason=Z". You reconstruct the change from delta logs.
- **State transitions across F5/F9 are not pinned.** Critical diagnosis questions like "what was the recorder state at OnSave?" and "what did OnLoad decide?" require stitching multiple log lines together. A structured before/after snapshot would answer both in two grep hits.
- **Misleading my own recent diagnosis.** I assumed a user-reported F5/F9 bug was in the standalone path based on sparse indirect signals; it turned out to be in the tree path (the user had done an EVA earlier, which is a standalone→tree promotion). If `[RecState]` lines had been logging on every lifecycle event, a single grep would have shown the mode unambiguously.

This is primarily a developer-tool issue, not a user-facing bug. But the cost is real: each incorrect hypothesis in a diagnosis cycle wastes a round-trip with the user and delays the actual fix.

## Goals

1. **At-a-glance mode identification.** Anywhere in the log, `grep "[RecState]"` should tell you, in one line, what mode was active at that moment and what the key ids were.
2. **Structured state snapshots** at every lifecycle boundary (recording start/stop, scene change, save/load, chain boundary, EVA, commit). The snapshot should be a single line with deterministic field order so it can be diffed across time.
3. **Short, compact recording "debug names"** so free-text logs referencing a recording (`"Committed recording from X (42 points)"`) include the full identity context without ballooning line length.
4. **Zero runtime cost when it matters.** Logging has to stay rate-limited per the project's existing conventions — no per-physics-frame state dumps, no per-ghost-per-frame spam. Snapshots fire on discrete lifecycle events only.
5. **Retrofit, not rewrite.** This PR adds observability primitives and sprinkles call sites. It does NOT restructure the recorder or scenario code, does NOT change any lifecycle behavior, does NOT introduce new serialization formats.
6. **Parity across modes.** Per the new `feedback_recorder_mode_parity.md` memory, observability must work identically for standalone and tree mode. A state snapshot must show "standalone" vs "tree" with equal fidelity.

## Non-goals

- Fixing any actual F5/F9 bug. That's a follow-up once the logging lands and we can diagnose the user's repro from a fresh playtest log.
- Unifying standalone and tree modes into a single architecture. That's #271, tracked separately.
- Exposing state in any in-game UI. Log-only for this PR.
- Persistent telemetry / external dashboards. Log-only.
- Performance profiling of the recorder. Observability, not measurement.

## Design

### New type: `RecorderStateSnapshot`

Pure static struct in a new file `RecorderStateSnapshot.cs`. Captures everything the single-line dump needs:

```csharp
internal struct RecorderStateSnapshot
{
    // Mode
    public RecorderMode mode;              // None | Standalone | Tree
    public string treeId;                  // null if standalone
    public string treeName;                // null if standalone
    public string activeRecId;             // the "live" recording we'd append to next
    public string activeVesselName;
    public uint   activeVesselPid;

    // Recorder live state
    public bool   recorderExists;
    public bool   isRecording;
    public bool   isBackgrounded;
    public int    bufferedPoints;          // recorder.Recording.Count
    public int    bufferedPartEvents;      // recorder.PartEvents.Count
    public int    bufferedOrbitSegments;
    public double lastRecordedUT;          // NaN if none

    // Tree state (only meaningful when mode==Tree)
    public int    treeRecordingCount;      // activeTree.Recordings.Count
    public int    treeBackgroundMapCount;

    // Pending tree slot (independent from standalone pending)
    public bool             pendingTreePresent;
    public PendingTreeState pendingTreeState;  // Finalized | Limbo
    public string           pendingTreeId;

    // Pending standalone slot (independent from pending tree)
    public bool   pendingStandalonePresent;    // RecordingStore.HasPending
    public string pendingStandaloneRecId;

    // Pending split recorder (breakup/dock race window — currently invisible)
    public bool pendingSplitPresent;
    public bool pendingSplitInProgress;

    // Chain manager state (chain boundaries are part of the diagnosis surface)
    public string chainActiveChainId;
    public int    chainNextIndex;
    public bool   chainBoundaryAnchorPending;
    public uint   chainContinuationPid;
    public uint   chainUndockContinuationPid;

    // Context
    public double     currentUT;           // Planetarium.GetUniversalTime() at capture time
    public GameScenes loadedScene;
}

internal enum RecorderMode { None, Standalone, Tree }
```

**Per review revision R1:** the `pending=` field is split into two independent slots (`pend.tree=` and `pend.sa=`) because `RecordingStore.HasPending` and `RecordingStore.HasPendingTree` can be set simultaneously — collapsing them into one field would lose exactly the info you need when diagnosis matters most. See `ParsekScenario.cs:474` where both are checked in the same conditional.

**Per review revision R2:** `pendingSplitPresent` + `pendingSplitInProgress` added — `ParsekFlight.pendingSplitRecorder` is a major silent transition surface (breakup, dock, undock race windows) and has been invisible in logs until now.

**Per review revision R3:** chain manager state fields (`chainActiveChainId`, `chainNextIndex`, etc.) added — chain boundaries are part of the diagnosis surface and currently undiagnosable from the state dump alone.

### New helper: `ParsekFlight.CaptureRecorderState()`

Returns a `RecorderStateSnapshot` filled from `this.activeTree`, `this.recorder`, `RecordingStore.PendingTree` / `.Pending`, and the KSP globals. Pure data-gathering, no side effects, no logging.

### New helper: `ParsekLog.RecState(string phase, RecorderStateSnapshot snap)`

Single entry point for emitting a `[RecState]` line. Delegates to the existing `ParsekLog.Write` pipeline with tag `"RecState"`. Format is deterministic and field-ordered:

```
[Parsek][INFO][RecState][#<seq>][<phase>] mode=<mode> tree=<treeId8|->|treeName> rec=<recId8|->|vesselName|pid=<pid>> rec.prev=<prevRecId8|-> rec.live=<bool>/<bg> rec.buf=<points>/<events>/<orbits> lastUT=<ut:F1> tree.recs=<N>/<bgMap> pend.tree=<id8|->:<Limbo|Finalized> pend.sa=<id8|-> pend.split=<bool>/<inProgress> chain=<id8|-|idx=<N>> ut=<ut:F1> scene=<scene>
```

Design notes:
- `phase` is a short free-text tag identifying the call site (e.g., `"OnFlightReady"`, `"OnSave:pre-write"`, `"StashTreeLimbo:post"`, `"RestoreCoroutine:matched"`). Kept as a constant at the call site for grep-ability.
- **Sequence number** (`#<seq>`) — confirmed per review Q1. Static atomic counter incremented on every `RecState` emission. Printed in the prefix for grep+sort ordering. Gives us "was a log line dropped?" detection.
- **`rec.prev` field** — per review's "missing" item #1. When the current `activeRecId` differs from the previously-emitted snapshot's `activeRecId`, print the previous id here. Implemented via a static last-seen-recId cache on `ParsekLog`. Only non-`-` when there was an actual transition since the last snapshot. Directly answers the "activeRecorderId changed invisibly" complaint in the Problem section.
- **`pend.tree=` and `pend.sa=` separate slots** — per review R1. Both can be present simultaneously; collapsing would lose info.
- **`pend.split=`** — per review R2.
- **`chain=`** — per review's missing item #2. Encodes the chain manager's `ActiveChainId` (truncated) and next index; shows `-` when no active chain.
- IDs truncated to 8 characters for line length; full IDs remain in other log lines at the event sites for cross-reference.
- `-` placeholders for absent fields (null ids, `NaN` UT, etc.) — not omitted — so the field order is stable across lines and `cut -d ' ' -f N` is a valid parse.
- `mode`, `rec.live`, `pend.tree`, `pend.sa` are the most diagnostic-valuable fields; they always appear.
- `rec.live=<bool>/<bg>` encodes both `IsRecording` and `IsBackgrounded` in one slash-separated field to keep line length bounded.
- `tree.recs=<N>/<bgMap>` encodes both `activeTree.Recordings.Count` and `activeTree.BackgroundMap.Count`.

### New helper: `Recording.DebugName` property

Compact string like `rec[abc12345|KerbalX|tree|0]`:
- Recording ID (8 chars)
- Vessel name
- Mode label (`tree` if `TreeId != null`, else `sa`)
- Chain index (or `-` if not chained)

Used in free-text log messages that reference a recording to give the reader instant context. Cheap — just a concat.

### Call sites

All are discrete lifecycle events; none fire per physics frame. The original list covered commit/stash/restore well but under-covered the **promotion** and **vessel-switch** transitions (per review #2). Revised list below adds those:

**ParsekFlight.cs — scene + flight lifecycle:**
1. `OnFlightReady` — after scene init completes
2. `OnSceneChangeRequested` — before any stashing logic runs
3. `StartRecording` (public instance method) — after the new recorder is constructed and `isPromotion` decided
4. `FinalizeTreeOnSceneChange` entry — before deciding which stash path runs
5. `StashActiveTreeAsPendingLimbo` — entry AND before-return
6. `CommitTreeRevert` — entry AND before-return
7. `CommitTreeSceneExit` — entry AND before-return
8. `StashPendingOnSceneChange` (standalone path) — entry AND before-return
9. `RestoreActiveTreeFromPending` coroutine — at start (`"Restore:start"`), after vessel match (`"Restore:matched"`), after `StartRecording(isPromotion:true)` (`"Restore:after-start"`)
10. `FlushRecorderIntoActiveTreeForSerialization` — entry AND after clear

**ParsekFlight.cs — vessel switch + promotion (review #2 — CRITICAL GAP):**
11. `OnVesselSwitchComplete` — entry AND after any mutation (backgrounds recorder, clears `ActiveRecordingId`, nulls recorder — major silent transition today)
12. `PromoteRecordingFromBackground` — entry AND exit (key tree-mode pivot, constructs new recorder + sets `ActiveRecordingId`)

**ParsekFlight.cs — breakup / destruction path (review #2 — the user's actual repro lives here):**
13. `PromoteToTreeForBreakup` — entry AND exit (this is the standalone→tree promotion that the EVA playtest case hit — THE single most diagnosis-valuable site)
14. `OnVesselWillDestroy` — entry (logs mode snapshot so a later breakup has context)
15. `ShowPostDestructionMergeDialog` — entry
16. `ShowPostDestructionTreeMergeDialog` — entry

**ParsekFlight.cs — split / chain boundary (review #2):**
17. `CreateSplitBranch` (EVA / undock / joint-break) — entry AND exit
18. `ResumeSplitRecorder` — entry (false-alarm split recovery path)
19. `FallbackCommitSplitRecorder` — entry
20. `onPartCouple` dock merge handler — before `pendingSplitRecorder = recorder` assignment
21. `OnPartUndock` handler — entry (before deferred split decision)
22. `OnCrewOnEva` handler — entry
23. `OnCrewBoardVessel` handler — entry

**ParsekScenario.cs — save/load flow:**
24. `OnSave` — `"OnSave:pre"` at start and `"OnSave:post"` after writing the active tree
25. `OnLoad` — four phases: `"OnLoad:settings-applied"`, `"OnLoad:active-tree-restored"`, `"OnLoad:revert-decided"`, `"OnLoad:limbo-dispatched"`
26. `TryRestoreActiveTreeNode` — after stashing the disk version as Limbo
27. `FinalizePendingLimboTreeForRevert` — entry AND after `MarkPendingTreeFinalized`
28. `HandleRewindOnLoad` — entry AND exit

That's ~35–40 individual emission points (some sites emit at entry + exit). Each is a one-liner `ParsekLog.RecState("phase", CaptureRecorderState());`. Total LOC addition for call sites is ~40 lines. The helper does the real work; the call sites are trivial.

**Parity note (per `feedback_recorder_mode_parity.md`):** sites 11–12, 13–16, 17–23 all touch both modes or straddle mode promotions. Every one needs to emit regardless of whether the state at entry was standalone or tree.

### Existing log line enrichment

Beyond the dedicated `[RecState]` dumps, update a handful of existing high-value log messages to include `Recording.DebugName` instead of bare `VesselName` or `RecordingId`:

- `RecordingStore.CommitTree` — already logs `"Committed tree 'X' (N recordings)"` — add `DebugName` for each child recording at Verbose.
- `FlightRecorder.StartRecording` — already logs `"StartRecording succeeded: pid=X"` — add `DebugName` of the tree's active recording when in tree mode.
- `RecordingStore.StashPending` / `StashPendingTree` — add `DebugName` to the stash log.

Scope for enrichment: at most ~10 existing lines, identified during implementation by grepping for `ParsekLog` calls that print a `RecordingId` or `VesselName` alone.

### Logging level + rate limiting

- `[RecState]` dumps at `Info` level by default. They fire on lifecycle events (not per-frame) so unthrottled is fine.
- A single rate-limited fallback (`VerboseRateLimited("RecState", "idle-snapshot", ..., 30.0)`) in an idle scheduler tick, to guarantee a state line every 30 seconds even during a long steady-state flight. This one is `Verbose`, not `Info`, so it's off by default — enabled via the verbose-logging setting.
- No per-physics-frame snapshots. If someone later wants that, it goes behind a separate debug toggle.

### Test strategy

**Pure unit tests (xUnit)** — tests for the snapshot struct format and the `DebugName` builder. Both are pure functions:

1. `RecorderStateSnapshot` field defaults when fed a null/empty ParsekFlight-like input.
2. `CaptureRecorderState` returns `mode=None` when no recorder, no tree, no pending.
3. `CaptureRecorderState` returns `mode=Standalone` with correct fields when recorder is set but activeTree is null.
4. `CaptureRecorderState` returns `mode=Tree` with correct fields when activeTree is set.
5. `Recording.DebugName` format across the variants: tree with chain index, tree without chain, standalone, null id edge case, localized vessel name.
6. `ParsekLog.RecState` output format — use the `TestSinkForTesting` pattern to capture emitted lines, assert exact format and field order against a golden string.
7. Guard: `[RecState]` line is stable across the same input (deterministic serialization, no locale leaks — use `InvariantCulture` for the `lastUT` / `ut` fields).

**Integration (no Unity)** — the snapshot capture itself needs a `ParsekFlight` instance, which is a `MonoBehaviour`. For unit testing, refactor `CaptureRecorderState` to take its inputs as explicit parameters:

```csharp
internal static RecorderStateSnapshot CaptureFromParts(
    RecordingTree activeTree,
    FlightRecorder recorder,
    RecordingTree pendingTree,
    PendingTreeState pendingTreeState,
    Recording pendingStandalone,
    double currentUT,
    GameScenes loadedScene);
```

Then the instance method is just:

```csharp
internal RecorderStateSnapshot CaptureRecorderState() =>
    RecorderStateSnapshot.CaptureFromParts(
        activeTree, recorder,
        RecordingStore.PendingTree, RecordingStore.PendingTreeStateValue,
        RecordingStore.Pending,
        Planetarium.GetUniversalTime(), HighLogic.LoadedScene);
```

The pure static is trivially unit-testable; the instance method is thin enough to leave to in-game coverage.

**Log-assertion tests** — use the existing `ParsekLog.TestSinkForTesting` pattern to verify `[RecState]` lines fire at the expected phases. Test at the `ParsekLog` layer (feeding `CaptureFromParts` manually), not via `ParsekFlight` (which needs Unity).

**Integration-style phase-sequence test** (per review #4). Drive a synthetic standalone→tree promotion sequence by calling `CaptureFromParts` with hand-constructed state at each phase, emitting via `ParsekLog.RecState("phase", snap)`, and asserting the captured log lines appear in the expected order with the expected field values at each step. Specifically simulate: `start-standalone → OnSave:pre → promotion-to-tree → OnSave:pre → StashTreeLimbo:pre → TryRestoreActiveTreeNode → OnLoad:limbo-dispatched → Restore:start → Restore:matched → Restore:after-start`. This is the exact sequence the user's EVA+F5+F9 repro walks through; it's the case that burned a diagnosis cycle. The test doesn't validate behavior — it validates that every transition leaves a structured fingerprint in the log.

**Sequence-number monotonicity test** — emit 100 `RecState` lines, parse the `#<seq>` prefix, assert strictly increasing. Guards against race conditions on the static counter.

**`prevRecId` transition test** — emit two snapshots with the same `activeRecId`; assert the second line's `rec.prev` is `-`. Then emit a third with a different `activeRecId`; assert `rec.prev` is the previous id. Then a fourth with the third's id again; assert `rec.prev` is `-`. Locks in the "only show on transition" semantics.

### Rollout

Single PR, single commit if feasible — the changes are additive and low-risk. Atomicity is not load-bearing the way it was for the quickload-resume fix; splitting into "snapshot type + helpers" and "call sites" is acceptable if the PR grows.

- Feature branch: `fix/recorder-observability` (already created)
- Base: `origin/main` (post-PR #160)
- Target: `main`
- Expected diff: ~400 LOC, mostly call-site additions and the new snapshot file, plus ~150 LOC of tests.

## Open questions (resolved per review)

1. **Sequence numbers** — **YES**, implemented. Static atomic counter, `#<seq>` in prefix. Review confirmed cheap + catches dropped lines.
2. **`pendingSplitRecorder` state** — **YES**, added as `pend.split=<bool>/<inProgress>`. Review confirmed load-bearing for breakup/dock diagnosis.
3. **Auto-snapshot on every recorder-subsystem `Warn`** — **DEFERRED** per review, but trivial to add later. If the first round of playtest diagnosis still shows we lose context on warns, we'll add one-line `RecState` emissions immediately after every `Warn` in `ParsekFlight`/`FlightRecorder`/`ParsekScenario`.

## Artifacts referenced by this plan

- `feedback_recorder_mode_parity.md` (memory file, created in this session) — the "any change to one recorder mode must be mirrored in the other" rule. This plan cites it in the parity note and in the call site list. **Confirmed present.**
- `docs/dev/todo-and-known-bugs.md` #271 (added in this worktree) — "Investigate unifying standalone and tree recorder modes". This plan is the complement: until #271 lands, we make the divergence at least observable.

## Review round 1 — addressed (summary)

The clean-Opus review on the initial plan flagged three actionable items and three nice-to-haves. All addressed in this doc:

| Review item | Resolution |
|---|---|
| R1: `pending=` field collapses tree + standalone into one slot (info loss) | Split into `pend.tree=` and `pend.sa=` — two independent fields |
| R2: `pendingSplitRecorder` state invisible (breakup/dock diagnosis gap) | Added `pend.split=<bool>/<inProgress>` field + snapshot fields |
| R3: Call site list under-covers vessel-switch / promotion / breakup paths (the actual playtest case lives in `PromoteToTreeForBreakup`) | Added sites 11–23: `OnVesselSwitchComplete`, `PromoteRecordingFromBackground`, `PromoteToTreeForBreakup`, `OnVesselWillDestroy`, `ShowPost*MergeDialog`, `ResumeSplitRecorder`, `FallbackCommitSplitRecorder`, `onPartCouple`, `OnPartUndock` |
| Open Q1: sequence numbers | Confirmed — `#<seq>` in prefix |
| Open Q2: `pendingSplitRecorder` field | Confirmed — see R2 row |
| Open Q3: Warn-auto-snapshot | Deferred per review |
| Missing #1: `activeRecordingId` change reason | Added `rec.prev=` field with static last-seen cache, only non-`-` on transition |
| Missing #2: chain manager state | Added `chain=<id8\|-\|idx=<N>>` field + snapshot fields |
| Missing #3: parity memory rule artifact | Confirmed file exists (`feedback_recorder_mode_parity.md`) |
| Testability #4: integration sequence test | Added "Integration-style phase-sequence test" that walks the EVA+F5+F9 repro scenario |

## Acceptance criteria

1. `grep "[RecState]" KSP.log` on a post-fix playtest log clearly shows, in chronological order, every mode transition, every F5/F9 event, every commit/revert, every EVA/undock, with enough field detail to diagnose mismatches without cross-referencing other log lines.
2. The user's reported F5/F9 tree-mode bug from the last playtest can be diagnosed from a single fresh log grep, not a detective story across hundreds of log lines.
3. Running the full xUnit suite still passes; the new tests pin the log format.
4. Standalone and tree mode produce indistinguishable-quality state dumps — parity rule respected.

## Scope sanity check

This is a developer-tool PR. It must NOT expand into:
- Any behavior change to the recorder
- Any new fields on `Recording` beyond `DebugName` (a computed property, not a stored field)
- Any save-file format changes
- Any UI additions

If during implementation I feel an urge to add one of these "just because we're here", stop and split it into a separate PR.
