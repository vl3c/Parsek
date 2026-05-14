# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Done - v0.9.2 Fresh EVA child finalized as Destroyed before first child samples

- ~~A freshly-created EVA branch child could be classified as `Destroyed` within the first few milliseconds after `OnCrewOnEva`, before the child recording had any trajectory points. In the retained Bill Kerman repro, `PatchedConicSnapshot` returned `NullSolver`, `IncompleteBallisticSceneExitFinalizer` fell through to the live-orbit fallback, KSP's not-yet-initialized EVA orbit returned a position near the body origin, and `BallisticExtrapolator` saw `alt=-599652.6 m` (`SubSurfaceStart`) and accepted a `Destroyed` terminal. The parent had already recorded a valid flagged `EVA` structural surface point and the child later wrote 120 valid samples plus a vessel snapshot, but the early cached `Destroyed` result blocked the valid playback snapshot path.~~

**Root cause:** `ShouldSuppressSubSurfaceDestroyedFromRecordedPoint` already defended the exact `NullSolver + SubSurfaceStart + recorded surface contradiction` shape, but it only searched the child recording's own points/track sections. At the failing instant the EVA child had no points yet, so the guard found nothing and the finalizer trusted the garbage live-orbit fallback. The needed surface evidence was on the pre-branch parent recording as a `TrajectoryPointFlags.StructuralEventSnapshot` point. Resolving that parent also cannot use `recording.ParentRecordingId`: for EVA branch children it points at the sibling continuation child, so the pre-branch parent must be reached through `recording.ParentBranchPointId -> BranchPoint.ParentRecordingIds`.

**Fix:** Threaded the active/pending `RecordingTree` into scene-exit finalization and finalizer-cache production (`IncompleteBallisticSceneExitFinalizer.TryApply`, `RecordingFinalizationCacheProducer.TryBuildFromLiveVessel`, active recorder refresh, background recorder refresh, and tree finalization call sites). The finalizer still searches child-recorded points first. If that fails, and the recording is an EVA branch child (`EvaCrewName` plus an EVA `BranchPoint` containing the child id), it resolves the pre-branch parent via `BranchPoint.ParentRecordingIds` and searches only flagged structural-event points within the existing `SubSurfaceRecordedPointContradictionWindowSeconds` window. The parent search is section-aware: Absolute sections inspect `frames`; Relative sections inspect `bodyFixedFrames` so v6 anchor-local `frames` offsets are not misread as body-fixed lat/lon/alt. A matching parent surface point suppresses the false finalization by returning `false`; it does not seed `TerminalStateValue`, does not end the active EVA child, and does not create a fresh `Destroyed` cache entry. Destroyed debris and stale/missing parent-evidence cases still classify as `Destroyed`.

**Coverage:** `SceneExitFinalizationIntegrationTests` covers parent structural suppression for an EVA child whose `ParentRecordingId` points at the sibling, a stale child point outside the 0.5 s contradiction window falling through to valid parent evidence, stale parent evidence outside the window, a non-EVA child that must still become `Destroyed`, Relative parent sections using `bodyFixedFrames` instead of local-offset `frames`, and the positive follow-up where the real parent-structural suppression fires before the recording later finalizes as `Landed` with vessel and ghost snapshots intact. `RecordingFinalizationCacheProducerTests` pins the cache seam so a suppressed default finalizer declines safely with `subsurface-destroyed-suppressed` instead of accepting a fresh `terminal=Destroyed` cache or falling through to atmospheric deletion fallback for packed/unloaded vessels. Runtime coverage adds `EvaKerbalGhostHasVesselSnapshot` in `RuntimeTests.cs`: it forces a live EVA branch, requires the no-child-samples window, forces the immediate live cache refresh past the stable-surface prefilter, asserts it declines with `subsurface-destroyed-suppressed`, then verifies the finalized EVA row stays `Landed`, retains usable snapshots, and can build/spawn real ghost geometry without the sphere fallback.

**Status:** CLOSED 2026-05-14.

---

## Done - v0.9.2 Suppressed scene-exit discard leaked debris persistence override

- ~~When Parsek raised KSP's max persistent debris setting for recording, the suppressed scene-exit discard path stopped the in-memory tree without calling the same debris-setting restore used by ordinary recording teardown. A cancelled/suppressed tree commit, including the fresh-EVA runtime canary cleanup path, could therefore leave the player's global debris limit at Parsek's temporary recording value.~~

**Fix:** `DiscardActiveTreeForSuppressedSceneExit` now calls `RestoreDebrisPersistence()` before stopping the active recorder and dropping the tree, matching `StopRecording` and other teardown paths.

**Coverage:** `ParsekFlightDebrisPersistenceTests.DiscardActiveTreeForSuppressedSceneExit_RestoresDebrisPersistenceOverride` seeds the private override state, invokes the suppressed-discard path, and asserts the debris setter receives the saved value, `debrisOverrideActive` is cleared, and the active tree is discarded.

**Status:** CLOSED 2026-05-14.

---

## Done - v0.9.2 Deferred EVA auto-record from second EVA orphaned tree recording

- ~~When a recording tree's active vessel was flushed to background during a scene/change focus transition, `HandleTreeBackgroundFlush` cleared `ActiveRecordingId` while leaving the parent capsule tracked in `BackgroundMap`. A later second EVA from that capsule arrived with no live recorder, so `OnCrewOnEva` fell through to deferred auto-record. `StartRecording` then created a `FlightRecorder` under the existing tree without a valid active tree head, and `FlushRecorderToTreeRecording` dropped the captured EVA data at scene exit because `tree.ActiveRecordingId` was still null.~~

**Fix:** `OnCrewOnEva` now handles the non-recording tracked-parent case before the pad auto-record fallback. If the EVA source vessel resolves through `BackgroundMap` (including one rebuild when the tree head is null), Parsek defers one frame, stages an EVA branch from the background parent recording, assigns the active child as `ActiveRecordingId`, and starts a recorder only when the chosen active child matches `FlightGlobals.ActiveVessel`. The old background parent is removed/flushed and the other child is re-backgrounded under a fresh child recording only after the active recorder is confirmed; if recorder startup fails, the staged branch point, child recordings, parent `ChildBranchPointId`, `ActiveRecordingId`, `PendingBoundaryAnchor`, and `BackgroundMap` entries are rolled back. Invalid map entries and unresolved tracked-parent shapes are logged, screen-messaged, and handled without arming deferred auto-record. `StartRecording` and `HandleDeferredAutoRecordEva` share an active-tree-head guard that rejects missing ids, missing recordings, and live-PID mismatches when both the active vessel pid and active-recording pid are known; zero pids are tolerated so fresh post-switch and restore paths can populate them. The deferred retry path clears its pending flags on that guard instead of spinning every frame. `FlushRecorderToTreeRecording` now emits loud drop diagnostics with tree id, attempted recording id, recorder vessel pid, and buffered point/event/section counts.

**Coverage:** `EvaDeferredAutoRecordOrphanTests` covers the active-tree-head guard, spawned-PID restore matching, tracked-background-parent route/focus decisions, tracked-parent resolution with and without rebuild, rollback of staged branch mutations and background-map entries, rate-limit key separation, and flush-drop diagnostic counts. `RuntimeTests.EvaTwiceFromSameCapsuleProducesTwoBranches` adds an isolated in-game regression for the two-EVA branch path. Targeted xUnit slice `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter EvaDeferredAutoRecordOrphanTests` passed (27/27).

---

## Done - v0.9.2 Root Re-Fly skipped anchor propagation, child ghosts drifted off the re-flown vessel

- ~~During a "Kerbal X upper stage" Re-Fly, the decoupled `Kerbal X Probe` ghost (`635813f2…`) rendered at the wrong distance behind the live upper stage — it shot away at the divergence rate instead of holding the staging-separation relationship. The earlier "probe booster" Re-Fly (live = probe, ghost = upper stage) looked correct. Source: `logs/2026-05-14_1756_kerbalx-refly-ghost-distance/KSP.log`. The probe Re-Fly (`sess_57b2…`) logged `Pipeline-AnchorPropagate DAG walk start … seedCandidatesEmitted=6 resolvedRel=6`; the upper-stage Re-Fly (`sess_eda1…`) logged `RebuildFromMarker: in-place continuation` and then **no DAG walk at all**.~~

**Root cause:** `RenderSessionState.RebuildFromMarker` resolves the origin recording's parent BranchPoint. Re-flying the tree root has no parent BP, so it took the in-place continuation early-out (`InstallEmptyInPlaceContinuationSession`), which cleared the anchor map, logged `RebuildFromMarker complete`, and **returned without calling `AnchorPropagator.Run`**. Every other exit path (including the structurally similar `no-siblings` path) runs the propagator — its comment even spells out why: "even without LiveSeparation seeds, the propagator still emits non-LiveSeparation candidates … into the session map." The propagator's tree-DAG walk is what propagates recorded anchors down BranchPoint edges to child recordings. The probe's post-separation `TrackSection` was recorded `ref=Absolute source=Background` by the `BgRecorder` (the player stayed focused on the upper stage at staging), so with no relative anchor to the re-flown root it played back at its original absolute world coordinates while the re-flown upper stage diverged. The child Re-Fly worked because that path runs the propagator normally.

**Fix:** `InstallEmptyInPlaceContinuationSession` now takes `recordings` + `treeLookup` and runs the same `AnchorPropagator.Run` + `ResolvePrimaryAssignmentsAndLog` block as the `no-siblings` path, after its existing bookend log lines (matching that path's ordering). HR-9: a propagator throw is caught and warn-logged, degrading to the prior empty-session behaviour rather than aborting the rebuild.

**Coverage:** `RenderSessionStateLoggingTests.InPlaceContinuationRootReFly_RunsAnchorPropagator` drives `RebuildFromMarker` with an in-place continuation marker whose tree lookup returns a tree but a null parent BP (the root-Re-Fly shape), and asserts both the `in-place continuation: parent BP intentionally null` verbose line AND the `Pipeline-AnchorPropagate DAG walk start` / `DAG walk summary` lines now fire. Full suite verified (11578 / 11578).

**Status:** CLOSED 2026-05-14.

---

## Done - v0.9.2 Re-Fly child ghosts still drifted after the propagator was wired in (PR #850 follow-up)

- ~~After the Root Re-Fly fix above made `AnchorPropagator.Run` fire on the in-place continuation path, a fresh capture of the same scenario (`logs/2026-05-14_1952_refly-init-pos-diff/KSP.log`) showed the decoupled `Kerbal X Probe` ghost STILL drifting away from the re-flown upper stage. The propagator ran but did no useful work: every in-place-continuation `DAG walk summary` logged `edgesVisited=0 edgesPropagated=0`, and the one session that emitted seed candidates (`seedCandidatesEmitted=7`) resolved them all to `skip-no-spline`.~~

**Root causes (three, all downstream of "the propagator runs but the active branch is never anchored"):**

1. **`Breakup` branch points were excluded from the DAG walk.** The staging separation between the probe and the upper stage is recorded as a `BranchPointType.Breakup` (a coalesced split — the decoupler fires inside a crash/structural-failure coalescing window), but `AnchorPropagator`'s Phase-2 edge filter only walked `Dock / Board / Undock / EVA / JointBreak`. The exact event that separates the two halves was never an edge, so `edgesVisited=0`.
2. **The active provisional was never aliased to the committed origin.** Post-#734 the in-place Re-Fly forks a fresh `NotCommitted` provisional (`ReFlySessionMarker.ActiveReFlyRecordingId`, e.g. `rec_9613…`) that supersedes the committed `OriginChildRecordingId` (e.g. `9db4…`). Co-bubble traces, the primary map, and the standalone-position lookups are all keyed on the committed origin id — so the child ghost's co-bubble primary resolved `P_render(t)` against the origin recording's frozen pre-re-fly trajectory, not the live re-flown vessel, and the peer held its offset relative to where the origin *was recorded*.
3. **The co-bubble recursion guard was global, not pair-specific.** `CoBubbleBlender` rejected any recording for which `RenderSessionState.IsPrimary(...)` was true. In a multi-tier formation a recording is routinely the designated primary for one pair AND a peer of another (e.g. `9db4…` is primary of `18816…` and peer of `81f2…`); the global check forced those middle recordings to `MissRecursionGuard` and dropped their own co-bubble offset, falling them back to standalone `PointInterp`.

**Fix:**

1. `AnchorPropagator.Run` now includes `BranchPointType.Breakup` in the Phase-2 edge filter; the per-child loop skips `Recording.IsDebris` children so the v13 parent-anchored debris contract is untouched and only controlled stage halves receive a propagated `DockOrMerge` ε.
2. `RenderSessionState` records the in-place Re-Fly alias (`OriginChildRecordingId → ActiveReFlyRecordingId`, fork shape only) at the top of `RebuildFromMarker` via `RegisterInPlaceReFlyAlias`, exposes `ResolveInPlaceReFlyActiveAlias`, and clears it on `Clear()` / non-in-place / legacy-same-id markers. `ParsekFlight.TryComputeStandaloneWorldPositionForRecording` resolves the alias so a co-bubble primary that landed on the superseded committed origin is evaluated against the live provisional's recorded trajectory. HR-15 still holds — the provisional is a recording, so the read is still recorded points, not live `Vessel` state.
3. `CoBubbleBlender`'s recursion guard is now pair-specific: it short-circuits to `MissRecursionGuard` only when the recording is a primary AND has no designated primary of its own (`!TryGetDesignatedPrimary`), i.e. it is purely a primary and never a peer.

**Coverage:** `AnchorPropagationTests.Run_PropagatesAcrossBreakupEdge_ControlledChildOnly_SkipsDebris` (controlled child anchored, debris child not, `Edge propagated … bpType=Breakup` logged); `RenderSessionStateLoggingTests.InPlaceContinuationForkShape_RootReFly_PropagatesBreakupEdgeToChild` closes the PR-#850 test gap — it uses the real post-#734 fork shape (provisional id ≠ committed origin) and asserts an edge is actually visited (`edgesVisited=1 edgesPropagated=1`) and the child anchor is written, not just that the DAG walk fired; `CoBubbleBlenderTests.TryEvaluateOffset_PeerIsPrimaryElsewhereButAlsoAPeer_PassesPairSpecificGuard`; and four `RenderSessionStateTests` alias cases (fork shape, legacy same-id no-op, non-in-place clears stale, `Clear()` resets). Full suite verified (11645 / 11645).

**Remaining gap (not in this PR):** on a root in-place Re-Fly the AnchorPropagator's Phase-1 seed pass still skips every recording that the session marked suppressed, which on a root re-fly is the entire tree — so the Breakup edge is walkable but has no upstream ε to flow unless a non-suppressed seed exists. The observed bug is resolved because these child ghosts render through the co-bubble path (now correctly aliased), not the propagator ε; a propagator-level upstream seed for the in-place-root case is tracked as future work.

**Status:** CLOSED 2026-05-14.

---

## Done - v0.9.2 Tree-rewind permanently hides CommittedProvisional priorTip during Watch

- ~~After a Re-Fly with a Crashed/Destroyed outcome (the fork is sealed but stays `MergeState.CommittedProvisional` per `TerminalKindClassifier`), rewinding the tree-root parent recording and then entering Watch on it showed neither the original priorTip ghost nor the re-fly attempt. `RecordingStore.EnsureRewindRetirementsForRollback` Pass 2 retired the priorTip permanently regardless of the dropped supersede's fork `MergeState`. Reproduction: `logs/2026-05-13_2335_kerbal-x-booster-ghost-missing/KSP.log` — user Re-Flies the Kerbal X Probe (crash → crash), seals the slot, rewinds Kerbal X to launch, enters Watch. `Ghost playback skip state: #7 id=bc4390be… vessel="Kerbal X Probe" skip=True reason=rewind-retired` (line 70041). The probe ghost never spawned.~~

**Root cause:** `fix-rewind-old-side-retirement.md` (PR #807) added Pass 2 to retire the priorTip of every dropped supersede so the prior "Destroyed re-appears in the recordings table after Re-Fly + Rewind" bug was suppressed. The later `fix-rewind-canon-fork-retirement.md` made canon (`Immutable`) forks preserve the relation at pure-pass-1 — meaning Pass 2 now only fires for non-canon supersedes. PR #807's design intent applied to the `Immutable` case (where the supersede is permanent); non-canon supersedes are rewindable by definition (the user can re-try). Pass 2's unconditional retirement contradicted the rewindable contract.

**Fix:** Pass 2 now consults `AnyDroppedRelationRetiresPriorTipPermanently` — a new helper that returns true only when at least one dropped relation targeting the priorTip has a `MergeState.Immutable` fork AND is not in `ForcedSelfRewindDropIds`. For non-Immutable forks, forced self-rewinds, and orphan-fallback drops, the helper returns false and the priorTip stays visible so spawn-at-endpoint replays it. The summary log line now carries `skippedNonImmutableOldSides=N`; per-skip Verbose log records the gate firing. `LoadTimeSweep` also has a one-shot legacy sweep that recovers pre-fix saves by removing stale `RewoundOutOldSideReason` rows pointing at live non-Immutable priorTips — but it is **tree-scoped and conservative**: it defers (retains) a stale row whenever its tree also carries Immutable canon supersede state (a removed Immutable fork retirement this load, or a surviving Immutable supersede relation), because pre-canon-forks saves can pack a genuine multi-old-side-to-one-Immutable-fork shape into the same tree and old-side rows carry no fork link to tell the two apart. The user's reproduction tree has no Immutable supersede, so it recovers cleanly; same-tree-mixed legacy saves are an accepted, documented limitation (the stale row stays in its pre-fix hidden state — a missed cleanup, not a regression). See `docs/dev/plans/fix-tree-rewind-supersede-old-side.md` for the full design rationale, the tree-scoping iterations, and the truth-table coverage in `RewindSupersedeRollbackTests.AnyDroppedRelationRetiresPriorTipPermanently_TruthTable`.

---

## Done - v0.9.2 per-frame log spam across four sites caused KSP.log to grow ~60-80K Parsek lines per ~8-minute session

- ~~An 8-minute play session against the showcase recordings corpus emitted 65,277 `[Parsek]` lines (75K total, 86% Parsek). `python despam_logs.py` confirmed the well-known suppressed patterns (warp-ended-zero, deferred-spawn-kept, missed-vessel-switch) were already bounded, so the bulk came from four un-rate-limited sites: `[VERBOSE][Flight] OnVesselSwitchComplete: seeded lastLandedUT=…` (3,777 lines, ~119/sec — the per-frame `Update()` missed-vessel-switch safety net replays `OnVesselSwitchComplete` and the `Verbose` was called directly); `[VERBOSE][KSCGhost] KSC pose interpolation skipped: no points recording=rec[synth-bo|Booster Drop SRB|tree|-]` (2,953 lines, ~118/sec — synthetic recording with no sampled points hits the skip branch every KSC ghost frame); `[VERBOSE][RecordingStore] TryProbeTrajectorySidecar` + `ReadBinaryTrajectoryFile` (6,241 + 6,233 = 12,474 lines, bursts of 550-1,101/sec around CommitTree — every save calls `TrySummarizeExistingTrajectorySidecar` which re-probes + re-deserializes the existing sidecar purely to compute the trajectory-shrinkage warning, and both inner calls log as if they were the main save action); `[INFO][PlaybackTrace]` (7,528 INFO lines — the 5-second post-structural-event gate works as designed, but with looping showcase recordings each crossing a structural event per loop, the same event UT gets retraced every loop pass).~~

**Fix:** four targeted rate-limit / suppression changes plus per-event dedup state.

- **`ParsekFlight.OnVesselSwitchComplete` seeded-landed-UT log** ([ParsekFlight.cs:2852](../../Source/Parsek/ParsekFlight.cs)): wrapped the `Verbose` in `VerboseRateLimited` keyed by `seeded-landed-ut-{newVessel.persistentId}`. The 5-second rate-limit interval drops the per-frame replay storm to one line per landed vessel + a periodic `suppressed=N` rollup, without touching the parent WARN at line 8920 (already `WarnRateLimited`).

- **`ParsekKSC.TryInterpolateKscPlaybackPose` no-points log** ([ParsekKSC.cs:1340](../../Source/Parsek/ParsekKSC.cs)): wrapped the `Verbose` in `VerboseRateLimited` keyed by `ksc-no-points-{rec.RecordingId}`. Sister branches in the same method (`recording=null` and `KSC SURFACE playback resolved`) already use rate-limiting (the latter was already showing `suppressed=62534` rollups elsewhere in the same log), so this matches the existing pattern.

- **`RecordingSidecarStore.TrySummarizeExistingTrajectorySidecar` diagnostic probe** ([RecordingSidecarStore.cs:901](../../Source/Parsek/RecordingSidecarStore.cs)): two-part suppression. (1) `TryProbeTrajectorySidecar` gained a `quietOnSuccess` bool parameter that silences only the routine "encoding=… version=…" Verbose summary on a successful probe; the Warns for unsupported / pre-reset / text-sidecar conditions still fire because callers always want those (corruption, schema drift, pre-reset files). The diagnostic preflight passes `quietOnSuccess: true`. (2) The `DeserializeTrajectorySidecar` call is wrapped in a narrow `try/finally` that toggles `RecordingStore.SuppressLogging` only across the deserialize body — that method emits a Verbose summary and no Warns, so a global toggle is safe there. The catch block's WARN at the outer scope keys on the live `RecordingStore.SuppressLogging` (now always restored to the caller's intent by the inner finally, so the live value is correct). This two-part approach was an Opus-review follow-up: the original single-toggle approach was silencing the probe's Warns about real corruption.

- **`PlaybackTrace.MaybeEmitFrame` loop-replay dedup** ([PlaybackTrace.cs](../../Source/Parsek/PlaybackTrace.cs)): each unique structural-event UT is traced in full exactly once per (recId, ghostIdx) per session. `TraceState` became a class carrying a `completedEventUTs` `HashSet<double>`; `traceStates` is now nested (`recId → ghostIdx → state`) so the per-frame lookup allocates no composite string key. An event UT is retired into `completedEventUTs` the moment its window can no longer be in its first pass: (a) a gate-closed frame finds `currentUT` outside the *last-traced* event's window — either aged forward past it (`currentUT - lastTracedEventUT > 5s`) or dropped below it (`currentUT < lastTracedEventUT`, a loop wrap whose first visible frame landed between structural events). On a gate-closed frame `currentUT` is provably outside the last-traced event's window — if it were inside, the gate would be open — so this branch always retires it. Keyed on `lastTracedEventUT`, not `mostRecentEventUT`, so a later structural event the ghost was hidden through doesn't strand the earlier traced event un-retired (Opus-review P3 findings, two rounds: forward-skip past a later event, then a between-events loop wrap). Runs on the common cruise path; the lookup is allocation-free. (b) a frame for a different event UT shows (forward progress, or a loop wrap from a later event back to an earlier one); (c) `currentUT` jumps backwards onto the same event UT (loop wrap at the window edge, before any gate-closed frame retired it); (d) a frame lands before every flagged event UT (the recording looped past the event start — an unambiguous wrap signal). Once retired, every later frame for that event is suppressed. Retirement keys on **set membership, not a high-water UT comparison** — the first cut keyed on `currentUT < prev.lastEmittedUT`, which only suppressed frames below the prior pass's high-water and resumed logging the tail once replay caught up (Opus-review P2 finding). Branch (d) was added after the re-review to close the early-ended-first-pass case where a loop's first in-window frame lands above the prior high-water. The one remaining residual: a ghost that stays hidden (no `MaybeEmitFrame` calls) through a recording's entire pre-event region AND into the event window on a loop pass has no wrap signal to observe, so that loop re-emits a partial tail — but it self-heals on the next loop (which does replay a pre-event frame). Bounded to one partial tail, vs the unbounded retracing the original bug exhibited. Level intentionally stays at INFO: jitter debugging is the trace's whole purpose, and the rate of distinct events is low. Showcase recordings that loop through a decouple every ~10 seconds no longer multiply the INFO line count by loop count.

**Coverage:**
- `PlaybackTraceTests.MaybeEmitFrame_LoopWraparound_SuppressesRepeatEventWindow` traces three forward frames, then re-enters the same event window at lower UTs and asserts zero additional emissions.
- `PlaybackTraceTests.MaybeEmitFrame_LoopWraparound_NewEventStillEmits` covers the cross-event case: after suppressing a wraparound re-entry of event UT 10, a frame in event UT 100's window must still emit.
- `PlaybackTraceTests.MaybeEmitFrame_LoopWraparound_StateRecordsLastEventUT` pins the `GetLastTracedEventUTForTesting` seam so the cursor field is wired correctly.
- `PlaybackTraceTests.MaybeEmitFrame_GateCloseRetiresEvent` pins the gate-closed retirement branch: a frame past `eventUT + 5s` flips `IsEventCompletedForTesting` to true even though it emits nothing.
- `PlaybackTraceTests.MaybeEmitFrame_GateClosedPastSkippedLaterEvent_RetiresEarlierTracedEvent` is the first P3 regression: trace event A, stay hidden through a later event B's entire window, reappear on a gate-closed frame whose `mostRecentEventUT` is B — A (the last *traced* event) must still be retired, while B (never traced) must not be, so a future loop can still trace B fresh.
- `PlaybackTraceTests.MaybeEmitFrame_LoopWrapGateClosedBetweenEvents_RetiresTracedEvent` is the second P3 regression: trace only the *later* event B (early-ended), loop wrap, first visible frame lands between events A and B on a gate-closed frame (`currentUT` below B's UT) — B must be retired via the `currentUT < lastTracedEventUT` clause so a re-entry of B's window at/above the prior high-water stays suppressed.
- `PlaybackTraceTests.MaybeEmitFrame_ReEntryAtOrAboveHighWater_Suppressed` is the P2 regression: after the first pass + a gate-closed frame retire the event, a loop re-entry whose first in-window frame lands exactly at and then above the prior pass's high-water UT must still be suppressed (a high-water comparison alone would resume logging the tail there).
- `PlaybackTraceTests.MaybeEmitFrame_FirstPassEndsEarly_LoopTailNotReEmitted` covers the early-ended first pass: one frame, then the loop re-entry's first window-start frame retires the event and the rest of the tail stays suppressed.
- `PlaybackTraceTests.MaybeEmitFrame_PreEventFrameAfterWrap_RetiresTracedEvent` pins retirement branch (d): a pre-event frame after a loop wrap retires the traced event, so a subsequent in-window frame landing above the prior high-water is suppressed.
- `PlaybackTraceTests.MaybeEmitFrame_HiddenThroughPreEventAndIntoWindow_ResidualIsBoundedAndSelfHeals` documents the one known residual: a ghost hidden through the whole pre-event region AND into the window on a loop leaks a partial tail that loop, but the next loop (which replays a pre-event frame) retires the event — asserts the leak is bounded to one loop and does not recur.
- `KscGhostPlaybackTests.TryInterpolateKscPlaybackPose_NoPoints_RateLimitedLogPerRecording` calls `TryInterpolateKscPlaybackPose` 50 times on a recording with a TrackSection but no frames, asserts exactly one "no points" log emission under a fixed clock, then advances the clock 10 s and confirms the next call emits with a `suppressed=` rollup — proving the rate-limit key + interval are active per recording.
- Full xUnit suite: 11,588 / 11,588 pass.

The OnVesselSwitchComplete and TrySummarizeExistingTrajectorySidecar fixes are not directly unit-tested: the first depends on KSP runtime (`GameEvents.onVesselChange` driving the production path), the second on real file I/O for the sidecar probe. Both changes are guard-level and the rate-limit / SuppressLogging primitives they invoke have their own existing coverage in `ParsekLog`'s test suite.

**Status:** CLOSED 2026-05-13. The underlying per-frame `OnVesselSwitchComplete` replay loop in `ParsekFlight.Update()` (the missed-vessel-switch safety net at `ParsekFlight.cs:8920-8927`) is itself a separate bug — it never settles and keeps firing — but that's a real KSP-runtime issue, not a logging issue. This fix only addresses the spam symptom; the recovery-loop root cause is left as a separate item.

---

## Done - v0.9.2 Retry-from-Rewind-Point left fresh attempt unrecorded behind dialog

- ~~Pressing Esc → Revert during an active Re-Fly and choosing "Retry from Rewind Point" loaded the RP quicksave and `AtomicMarkerWrite` created the new Re-Fly fork as expected, but two failures stacked: (1) `OnFlightReady` immediately opened the tree merge/discard dialog for the parent tree, hiding the new attempt behind a popup; (2) underneath, no recorder was ever bound to the new fork, so the player's "fresh" attempt would not have been recorded even if they dismissed the dialog. Effectively Retry did nothing — the user could only click "Discard Re-Fly Attempt" in the dialog to recover. Source: `logs/2026-05-13_2049/KSP.log` lines 322656 (`AtomicMarkerWrite … fork rec_321b…`), 323525 (`Pending tree 'Kerbal X' reached OnFlightReady — showing tree merge dialog (fallback)`), absence of any `RestoreActiveTreeFromPending: resumed recording …` line for the new fork (compare 287999 for the initial invocation, which did resume). Same trigger applies to initial Re-Fly invocations whose pending tree is Finalized (post-destruction); the initial invocation in this log avoided the bug only because `ShowPostDestructionTreeMergeDialog` had not fired yet.~~

**Root cause (two-layer):**

- **Surface layer — merge dialog timing.** `ParsekFlight.OnFlightReady`'s tree-merge-dialog fallback (the "auto-commit missed" safety net) gated only on `RecordingStore.HasPendingTree && !restoringActiveTree`. After `RewindInvoker.AtomicMarkerWrite` attached the fresh fork to the pending tree and set `ActiveReFlySessionMarker`, both gates were true — but the session marker was non-null, meaning the pending tree was owned by an in-progress Re-Fly attempt, not a leaked auto-commit. The Re-Fly's natural merge-decision point is the scene-exit path (`SceneExitInterceptor`) once the attempt actually finishes, not the moment the user starts flying it.

- **Underlying layer — recorder restore was never scheduled.** During the previous Re-Fly attempt the probe was destroyed, which fired `ShowPostDestructionTreeMergeDialog` → `FinalizeTreeRecordings` → `RecordingStore.StashPendingTree(..., Finalized)`. The pending tree in memory therefore arrived at OnLoad-after-Retry in `Finalized` state. `TryRestoreActiveTreeNode` keeps the in-memory Finalized tree as-is (#290d, `ParsekScenario.cs` ~3415), so the `Limbo` dispatch branch never sets `ScheduleActiveTreeRestoreOnFlightReady = Quickload`. With `restoreMode == None` at OnFlightReady, `RestoreActiveTreeFromPending` is not scheduled; `TryRestoreCommittedTreeForSpawnedActiveVessel` bails on `HasPendingTree`; and the OnFlightReady merge-dialog fallback was the only thing keeping the player from a fully stuck "no recorder, no active tree, pending tree blocking everything" state. Suppressing the dialog without addressing this would have made the symptom invisible while leaving the underlying state broken — which the first patch attempt indeed did (review caught it).

**Fix:** Three pure decisions plus a coroutine state-gate carve-out, dispatched from OnFlightReady in two steps.

- `ParsekFlight.ShouldShowOnFlightReadyMergeDialog(hasPendingTree, restoringActiveTree, reFlySessionActive)` returns true only when a pending tree exists, no restore coroutine owns it (#293), AND no active Re-Fly session owns it. The `reFlySessionActive` input reuses `ParsekScenario.IsReFlySessionActiveForQuickloadDiscard()`, which covers both the persisted marker and the `RewindInvokeContext.Pending` window before `AtomicMarkerWrite` recreates the marker.

- `ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(restoreMode, hasPendingTree, pendingTreeIsFinalized, reFlySessionActive)` returns true only when the dispatcher arrived at OnFlightReady with no schedule, the pending tree is Finalized, and a Re-Fly session is active. The OnFlightReady dispatcher upgrades `restoreMode` to `Quickload` in that case so `RestoreActiveTreeFromPending` is scheduled the same way it would be for a Limbo tree.

- `ParsekFlight.ShouldAcceptFinalizedPendingTreeForReFlyRetry(hasPendingTree, pendingTreeIsFinalized, reFlySessionActive)` mirrors the dispatcher's decision inside the coroutine: `RestoreActiveTreeFromPending`'s state gate (`Limbo` only) now also accepts `Finalized` when the helper returns true. The coroutine's existing marker-swap path (`ResolveInPlaceContinuationTarget` + `tree.ActiveRecordingId = markerSwap.TargetRecordingId`) then redirects the wait target to the new fork's vessel name, and the post-match `recorder.StartRecording(isPromotion: true)` binds the live recorder to the fresh fork.

- The OnFlightReady call site dispatches the merge dialog through the extracted `MaybeShowPendingTreeMergeDialogOnFlightReady` helper. When the new schedule path fires, `restoringActiveTree=true` is set synchronously by `RestoreActiveTreeFromPending`'s entry, so the helper hits the `#293` skip branch first (the Re-Fly-specific skip branch logs only when the schedule path was NOT triggered — e.g. the async flight-ready-deferred path where `AtomicMarkerWrite` runs after `OnFlightReady`, leaving `RewindInvokeContext.Pending=true` and the marker null at dispatch time).

- Placeholder-mode Re-Fly markers (PID changed across rewind, or chain orphaned at `AtomicMarkerWrite` line 1096-1099) DO NOT skip the dialog and DO NOT fire the recorder-restore carve-out. The coroutine's `ResolveInPlaceContinuationTarget` returns `placeholder-pattern` for that marker shape (`ReFlySessionMarker.cs:264-273`); the wait loop targets the pre-rewind PID, times out at 3 s, and yield-breaks without binding a recorder. Both gates use the stricter `ParsekScenario.IsReFlyInPlaceContinuationActive()` (marker set AND `InPlaceContinuation == true`) so the merge dialog still fires as the recovery path in placeholder mode. The dialog-skip path additionally includes `RewindInvokeContext.Pending` for the brief invoke window where the marker has not been written yet (flicker safety).

**Coverage:** `OnFlightReadyMergeDialogGuardTests` (xUnit) enumerates three truth tables — the merge-dialog skip decision (5 dialog-side cases plus two no-pending short-circuits), the restore-mode upgrade decision (6 cases covering Limbo / Finalized / no-pending / no-Re-Fly / already-scheduled), and the Finalized-accept decision inside the coroutine (4 cases). `OnFlightReadyMergeDialogGuardInGameTest` (in-game) covers the call-site wiring for the merge-dialog skip: arms a synthetic `ReFlySessionMarker` + pending tree, drives `MaybeShowPendingTreeMergeDialogOnFlightReady` via reflection, and asserts that no `ParsekMerge` popup spawns under an active Re-Fly; the positive control with marker cleared asserts the popup DOES spawn. `ParsekScenario.ShowDeferredMergeDialog` was audited for the parallel-hole concern and left unchanged: every reachable call site fires in a non-FLIGHT scene (i.e. after the Re-Fly attempt has been concluded by the player's scene change), and `MergeDialog.ShowTreeDialog` already renders the Re-Fly-specific message + suppressed-subtree closure when a marker is active. A code comment at the dispatch site documents this audit so future readers do not re-flag it. Full suite verified (11572 / 11572).

---

## Done - v0.9.2 RecordingOptimizer.TrimBoringTail trimmed non-spawnable terminal tails

- ~~While watching a Kerbal X upper-stage playback, the decoupled `Kerbal X Probe` ghost (`rec_1e37c44e811b4e7cbecbaa9d2bcf55e1`) disappeared ~10 s after entering vacuum — even though the original on-rails capture covered 26 s further. Source: `logs/2026-05-13_2155_probe-booster-disappear/KSP.log`. The probe was finalized with `terminal=SubOrbital`, `TerminalOrbit*` healed by `PopulateTerminalOrbitFromLastSegment` to match the captured on-rails orbit (sma=601698, ecc=0.348), with a `BubbleExit` anchor at UT 413.569 marking the moment the probe drifted out of the active vessel's 2.5 km physics bubble. The trajectory file originally stored 55 points / endUT 440.6, but the post-commit optimizer logged `TrimBoringTail: trimmed 'Kerbal X Probe' from endUT=440.6 to 414.2 (removed 26.3s, 12 points; trimUT=414.2 lastInterestingUT=404.2)`, then `ExplicitEndUT` was stamped to 414.249 and that's what playback hit as `pastEffectiveEnd=True needsSpawn=False isMidChain=False` → ghost destroyed.~~

**Root cause:** `RecordingOptimizer.TailPreservesTerminalSpawnStateInternal` and `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` disagreed on which terminal states actually spawn a real vessel. The spawn policy refused `SubOrbital`/`Docked` (`SubOrbital includes FLYING and ESCAPING — vessel would materialize mid-air and crash, #45`) and only spawned for `Landed`/`Splashed`/`Orbiting`, but the trim helper lumped `SubOrbital`/`Docked` with `Orbiting` under "stable terminals" and routed them through `TailMatchesTerminalOrbit`. For the probe, the healed `TerminalOrbit*` matched the captured BG segment byte-for-byte (the optimizer's tolerances are sized for stable-orbit jitter), so the trim chopped the ballistic tail. No real vessel takes over at the trimmed UT for `SubOrbital` recordings — the boring tail IS the only post-finalize playback the player sees, and trimming it makes the ghost vanish mid-coast.

**Fix:** Added `GhostPlaybackLogic.IsSpawnableTerminal(TerminalState)` as the single source of truth (`Landed`/`Splashed`/`Orbiting` only). `ShouldSpawnAtRecordingEnd` now calls it both for `hasSpawnableTerminal` and for the terminal-state refusal branch (replacing the inline five-case `if`), as well as for the `terminalOverridesUnsafe` snapshot-situation override. `RecordingOptimizer.TailPreservesTerminalSpawnStateInternal` gates upfront on `IsSpawnableTerminal`: non-spawnable terminals (`SubOrbital`, `Destroyed`, `Recovered`, `Docked`, `Boarded`, plus anything future) refuse the trim regardless of orbit-shape match, logging through `LogUnstableTerminalTrimRefusal` with an updated message that references `IsSpawnableTerminal`. `IsUnstableTerminalState` now delegates to `!IsSpawnableTerminal(...)` so the bulk-pass log suppression bucket stays in sync. The byte-identical `ParsekFlight.IsStableSpawnTerminal` private helper was deleted and its two call sites (`RefreshActiveEffectiveLeafSnapshot` precondition, `FinalizeIndividualRecording` stable-snapshot refresh) now call `GhostPlaybackLogic.IsSpawnableTerminal` directly, so the contract is enforced in a single location. The existing `TrimBoringTail_SubOrbitalTerminalUsesOrbitGuard` test was split: `TrimBoringTail_SubOrbitalTerminal_RefusesTrim_ShapeMismatch` preserves the original shape-divergence case, and the new `_RegardlessOfShapeMatch` test mirrors the probe scenario with byte-matched terminal orbit + ExoBallistic boring tail to prove the upfront gate fires. Added `TrimBoringTail_DockedTerminal_RefusesTrim` for the other contract-violator and a parameterized `IsSpawnableTerminal_MatchesShouldSpawnAtRecordingEndRefusalSet` test that enumerates every `TerminalState` value and asserts `IsSpawnableTerminal`, `ShouldSpawnAtRecordingEnd`'s terminal-state branch, `IsUnstableTerminalState`, and `TailPreservesTerminalSpawnState` all agree — and for spawnable terminals asserts `needsSpawn=true` is actually reached (not just that the terminal-state branch didn't refuse), so any future downstream gate that would suppress a Landed/Splashed/Orbiting spawn trips the contract test.

**Status:** CLOSED 2026-05-13.

---

## Done - v0.9.2 Re-Fly post-load strip silently deleted planted flag vessels

- ~~After a player planted a flag during a recorded EVA, accepted the post-flight tree-merge dialog, and clicked Re-Fly on the Probe slot, the `PostLoadStripper.Strip` invocation deleted the flag vessel along with the other 11 unmatched sibling vessels (`vesselsBefore=13 kept=1 removed=12` in `logs/2026-05-13_2101_refly-spawn-investigation`). KSP stores planted flags as real save-level vessels of `VesselType.Flag`, and the strict-unmatched branch (enabled by `RewindInvoker.InvokeReFly`) does not consult vessel type — any vessel whose `persistentId` is not in `RewindPoint.PidSlotMap`/`RootPartPidMap` is killed via `Vessel.Die()`. Flags are not tracked by the Parsek recorder, so once stripped there is no recording-driven respawn path; the FlagPlant career milestone (a permanent player achievement) is silently destroyed.~~

**Fix:** Three-layer flag-preservation defense across two seams — `PostLoadStripper.Strip` got the primary flag-bypass branch, and the in-place continuation strip-supplement seam (`RewindInvoker.StripPreExistingDebrisForInPlaceContinuation`) got both a survey-level skip AND a kill-set protection layer. The two `RewindInvoker` layers coexist as belt-and-suspenders: the survey-level skip closes the path at the source so a preserved flag never enters `leftAlonePidNames`, and the kill-set protection layer is kept as a redundant safety net so a future refactor of the survey helper cannot silently regress flag preservation.
  1. **Primary strip (`PostLoadStripper.Strip`):** new flag-preservation branch placed BEFORE slot-map matching and BEFORE the strict-unmatched fallback. A new `ShouldPreserveVesselType(VesselType)` predicate currently returns true only for `VesselType.Flag`; matching vessels are added to `PostLoadStripResult.PreservedFlagPids` and a `Verbose` per-vessel preserve log plus an `Info` summary line ("Strip preserved N flag vessel(s): [pids]") fire when the list is non-empty. The standard strip-summary line gains a `preservedFlags=N` field. The `IStrippableVessel` interface gained a `VesselType VesselType { get; }` member; the production `LiveVesselAdapter` returns `vessel.vesselType` (falling back to `VesselType.Unknown` on null/throw — covered by a clarifying code comment because the catch path requires live KSP runtime) and tests can drive the new branch via the `StubVessel.VesselType` setter (default `VesselType.Ship`).
  2. **Survey-level skip in `RewindInvoker.StripPreExistingDebrisForInPlaceContinuation` (the user-requested upstream defense):** the production survey loop is factored into a pure `internal static List<(uint pid, string name)> BuildLeftAlonePidNamesForInPlaceContinuation(IList<IStrippableVessel>, PostLoadStripResult, Func<uint,bool> isGhostMapVessel)` helper that drops `VesselType.Flag` entries entirely. A small `ShouldSkipFromLeftAloneSurvey(IStrippableVessel)` predicate keys the skip on the actual live vessel type (not on `PreservedFlagPids` membership) so the filter is robust against a future divergence between strip bookkeeping and live vessel state, with a defensive try/catch mirroring `LiveVesselAdapter.VesselType`'s half-destroyed-Unity-GameObject fallback. The production caller now enumerates vessels via `DefaultVesselEnumeration.Instance` so the same defensive `LiveVesselAdapter` handles vessel-type access. When any flag is skipped the helper emits a one-shot `Verbose` summary ("Strip post-supplement: skipping flag v=… name='…' from leftAlone survey -- preserved by PostLoadStripper (totalFlagsSkipped=N included=M ...)") so playtest logs can confirm the upstream filter ran. This is the layer that closes the user's review note at the source: a preserved flag never reaches `ResolveInPlaceContinuationDebrisToKill`.
  3. **Kill-set protection in `RewindInvoker.StripPreExistingDebrisForInPlaceContinuation` (redundant safety net):** the protected-pid construction is factored into the new `internal static HashSet<uint> BuildProtectedPidsForInPlaceContinuation(PostLoadStripResult, ReFlySessionMarker, IReadOnlyList<Recording>)` helper, which composes the selected pid + the active recording's pid + every `PreservedFlagPids` entry. Given layer 2, this layer is redundant for flags today — but kept on purpose so a future refactor that accidentally loosens the survey filter (e.g., changing the adapter's vessel-type fallback, adding a new survey path that bypasses the helper) still has the kill-set protection layer to defend the flag. When the helper shields any flag pid it emits an `Info` summary ("BuildProtectedPidsForInPlaceContinuation: shielded N preserved flag pid(s) ...") so playtest logs can show this branch ran.

**Coverage:** `PostLoadStripperTests` adds `ShouldPreserveVesselType_FlagOnly` (predicate contract: only `Flag`, not `SpaceObject`/`Debris`/`EVA`/etc.), `Strip_FlagVessel_PreservedEvenUnderStrictStrip` (the canonical bug repro — 13-vessel-style scene with selected probe + sibling capsule + flag, strict mode on, asserts the flag is not in `StrippedPids`, not counted in `LeftAlone`, is in `PreservedFlagPids`, logs fire correctly, and the flag does NOT appear in the `Strip strict` WARN), `Strip_FlagOnly_PreservedAlongsideSelected` (sanity: just active + flag), `Strip_FlagPreserved_RegardlessOfSlotMapMembership` (defense-in-depth pin documenting the ordering invariant — the collision is impossible by construction since slot maps are built from recorded Parsek vessels and flags are never recorded, but the test guards against a future refactor folding the preserve branch after slot-map matching), and `Strip_NoFlags_PreservedFlagPidsEmpty_NoSummaryLog` (no flags ⇒ `preservedFlags=0` summary, no per-flag summary line). `Bug587StripPreExistingDebrisTests` adds the second-seam coverage in two clusters. Layer 2 (survey-level skip): `ShouldSkipFromLeftAloneSurvey_FlagVessel_ReturnsTrue`, `ShouldSkipFromLeftAloneSurvey_NonFlagVesselTypes_ReturnFalse` (sweep across Ship/Probe/Debris/EVA/Plane/Lander/Rover/Base/Station/Relay/SpaceObject/Unknown), `ShouldSkipFromLeftAloneSurvey_NullVessel_ReturnsFalse`, `ShouldSkipFromLeftAloneSurvey_VesselTypeThrows_FailsClosed_ReturnsFalse` (defensive try/catch), `BuildLeftAlone_FlagSkipped_ShipKept_EvenWhenNamesCollide` (the user's complaint scenario: a flag and a ship sharing a kill-eligible name — only the ship lands in `leftAlonePidNames`, with the Verbose log asserted), `BuildLeftAlone_FlagSurvivesKillResolverViaSurveyOnly_RegardlessOfProtectedPids` (end-to-end: drives the full pipeline with an EMPTY `protectedPids` set, proving the survey skip alone keeps the flag out of the kill set — the user-requested upstream defense in isolation), `BuildLeftAlone_RegressionGuard_WithoutSurveySkip_FlagWouldEnterKillSet_AbsentProtectedPids` (regression-guard companion: a hand-rolled `leftAlonePidNames` containing the flag + empty `protectedPids` produces a kill set containing the flag — proving removing EITHER protection layer alone restores the bug), plus `BuildLeftAlone_NullInputs_AreDefensive`, `BuildLeftAlone_GhostMapPid_Excluded`, `BuildLeftAlone_StrippedAndSelectedPids_Excluded`, and `BuildLeftAlone_ZeroPidAndEmptyName_Skipped`. Layer 3 (kill-set protection): `BuildProtectedPids_*` (helper unit tests covering empty/null inputs, selected + active rec composition, the flag-pid shield branch with its log assertion, zero-pid skip, and a no-op no-log branch), `ResolveDebris_PreservedFlagPid_NotKilled_EvenWhenNameCollidesWithDestroyedRec` (end-to-end pin: a flag pid in `PreservedFlagPids` survives the kill walk even when its `vesselName` matches a Destroyed-terminal recording, while a non-flag debris sharing the same name still dies), and `ResolveDebris_WithoutFlagProtection_NameCollidingFlagWouldDie_RegressionGuard` (companion proving the kill predicate WOULD have fired without the new protection — if a future refactor drops the `PreservedFlagPids` branch from `BuildProtectedPidsForInPlaceContinuation` while the survey skip stays, this test stays green but the previous test fails, isolating the regression to layer 3).

**Status:** CLOSED 2026-05-13.

---

## Done - v0.9.2 Re-Fly load path skipped stale `SpawnedVesselPersistentId` reconcile

- ~~A prior Re-Fly merge committed the empty Kerbal X capsule (recording `18ed6d02f…`, `canPersist=True`, `terminal=Landed`), which became a real persistent vessel (PID `2708531065`) in the save and stamped `Recording.VesselSpawned=true` + `Recording.SpawnedVesselPersistentId=2708531065`. When the player clicked Re-Fly on the Probe slot, `PostLoadStripper.Strip` removed 12 of 13 vessels — including the capsule — leaving only the active Probe (PID `3215646968`). Re-Fly playback should have re-spawned the capsule at its terminal endpoint, but instead the engine logged `[Spawner] Spawn suppressed for #18 "Kerbal X": already spawned (VesselSpawned=true)` and `PlaybackCompleted ... needsSpawn=False`. Source: `logs/2026-05-13_2101_refly-spawn-investigation`.~~

**Root cause:** `ParsekScenario.ReconcileSpawnStateAfterStrip` resets `VesselSpawned` / `SpawnedVesselPersistentId` / `SpawnAttempts` / `SpawnDeathCount` / `TerminalOrbitSpawnSafety` for any recording whose stored persistent PID is no longer in the post-strip vessel set. It runs from the plain-rewind path (`ParsekScenario.cs:1701`) and as defense-in-depth at `:2405`, but the Re-Fly invocation path in `RewindInvoker.RunStripActivateMarker` never invoked it — the sequence was `PostLoadStripper.Strip` → `SetActiveVessel` → `AtomicMarkerWrite` → `LedgerRecalc`, with no reconcile in between. The user's KSP.log had zero `Reconciled spawn state for recording` lines despite the capsule's PID being stripped.

**Fix:** `RewindInvoker.RunStripActivateMarker` now reconciles spawn state after `SetActiveVessel` succeeds and before `AtomicMarkerWrite`. The survivor set the reconcile sees is built explicitly as `flightState.protoVessels` PIDs MINUS `PostLoadStripResult.StrippedPids`, not the raw `flightState.protoVessels` list. The subtraction is mandatory: `PostLoadStripper.Strip` removes vessels via `Vessel.Die()` but does NOT remove the matching `ProtoVessel` from `HighLogic.CurrentGame.flightState.protoVessels` — that list is the save-shape mirror and does not auto-sync with `Vessel.Die()`. Passing the raw `protoVessels` list left every stripped capsule's PID in the survivor set, `ShouldResetSpawnState` returned false, `VesselSpawned` stayed true, and the spawn-suppression bug persisted despite the reconcile call existing. The survivor-set computation is extracted into `ParsekScenario.ComputeSurvivorsFromProtoVesselPids(IEnumerable<uint>, IEnumerable<uint>)` so the PID-level subtraction logic is unit-testable outside KSP (`ProtoVessel` cannot be constructed in xUnit). The Re-Fly call site logs a one-line `Post-strip reconcile: strippedPids=N protoVesselsRemaining=M survivorPidCount=K` summary so the next Re-Fly log captures whether the survivor set was computed correctly. Exceptions in the reconcile are caught and warn-logged so a non-fatal helper failure cannot abort the Re-Fly itself. The other two reconcile call sites in `ParsekScenario.cs` (revert path at `:1701`, defense-in-depth at `:2405`) still pass the raw `protoVessels` list and may have the same input-shape bug; out of scope for this fix — note: investigate after Re-Fly path is validated end-to-end. This closes the deeper invariant follow-up flagged on the previous "Re-launching same `.craft`" entry.

**Coverage:** Added `SpawnStateReconciliationTests.ComputeSurvivors_*` (6 cases) for the pure `ComputeSurvivorsFromProtoVesselPids` helper: production-shape subtraction (protoVessels still contains stripped capsule + booster PIDs because `Vessel.Die()` did not remove them; survivor set must subtract `StrippedPids`), null/empty `strippedPids`, null `protoVesselPids`, all-stripped, and harmless `strippedPids` containing PIDs not present in `protoVesselPids`. Added `Reconcile_ReFlyStripScenario_ProductionInputShape_ResetsStrippedSiblings`, which exercises the full helper-plus-reconcile path with the production input shape (raw protoVessels enumeration containing all three Kerbal X PIDs minus `StrippedPids = { capsulePid, otherSiblingPid }`) and asserts both committed siblings are reset to `VesselSpawned=false` / `SpawnedVesselPersistentId=0` / `SpawnAttempts=0` / `SpawnDeathCount=0`, the active Probe is preserved, and the helper emits the expected per-recording and summary `[Scenario]` log lines. Added `Reconcile_ReFlyStripScenario_WhenSurvivorSetIsNotSubtracted_BugReappears` as an explicit regression guard pinning the pre-fix failure mode: a buggy survivor set that includes the stripped capsule's PID leaves the recording's stale `SpawnedVesselPersistentId` in place and the engine continues to suppress re-spawn. The previous direct-set test `Reconcile_MixedRecordings_OnlyResetsStripped` is retained as helper-shape coverage with an inline comment pointing at the production-shape test. The 14 other `SpawnStateReconciliationTests` cases (pure `ShouldResetSpawnState` decisions plus the `HashSet<uint>` overload edge cases) all still pass.

**Status:** CLOSED 2026-05-13.

---

## Done - v0.9.2 Re-Fly fork ghost lit booster engine FX on a shut-down engine

- ~~During a Re-Fly of the Kerbal X upper-stage capsule, the previously-superseded `Kerbal X Probe` Re-Fly fork (`rec_152453a952804ee7b54f129bdfe2fdc1`) spawned as a ghost at UT 129.15 with its `liquidEngineMainsail.v2` booster (pid `2485666303`) showing full-throttle flame FX, even though the original recording captured an `EngineShutdown` sentinel at fork start (the engine was off). Source: `logs/2026-05-13_1844_engine-fx-zero-throttle/KSP.log`. The Re-Fly fork was created via `RewindInvoker.AtomicMarkerWrite` and recorder promotion ran `FlightRecorder.StartRecording(isPromotion: true)`, which routed through `ResetPartEventTrackingState(v, emitSeedEvents: false)`. The promotion branch unconditionally skipped seed-event emission ("`ResetPartEventTrackingState: skipping seed events (chain promotion)`"), so the fork's `PartEvents` stayed empty across the in-place flush, save, reload, and second Re-Fly. On the second Re-Fly the fork was loaded as ghost `#9`; `GhostPlaybackLogic.BuildEngineEventKeySet` returned an empty set and `AutoStartOrphanEnginePlayback` matched the "zero engine events = pure debris booster" heuristic, calling `SetEngineEmission(... 1f)` and `info.currentPower = 1f` on every engine (`Auto-started audio for orphan engine key=636330573568` / `Auto-started engine FX for orphan engine key=636330573568 pid=2485666303 midx=0`). Audio was silent (vacuum, vol=0) but the flame particles ran for ~0.34 s.~~

**Root cause:** `FlightRecorder.ResetPartEventTrackingState`'s skip branch used the *caller intent* (`isPromotion` flag) instead of any signal about whether the new recording already covers the playback orphan-engine guard. `RestoreActiveTreeFromPending` (Re-Fly fork), `CreateSplitBranch`, and `CreateMergeBranch` all create *new* recordings (zero `PartEvents`) before the promotion call, but the flag-driven gate skipped seeds anyway, so the `EngineShutdown` sentinel `PartStateSeeder.EmitEngineSeedEvents` would have written never made it into the recording. The orphan guard (`GhostPlaybackLogic.BuildEngineEventKeySet`) counts only `EngineIgnited` / `EngineThrottle` / `EngineShutdown`, so the gate needed to be engine-event aware rather than total-event aware — and the seeds emitted on the empty-engine branch needed to be engine-only, because re-emitting `DeployableExtended` / `LightOn` / `ShroudJettisoned` at a late promotion UT is exactly the bug A / #263 "seed at resume UT poisons tail trim" failure mode.

**Fix:** Engine-event aware promotion gate in `FlightRecorder.ResetPartEventTrackingState`, plus a StartUT-anchored seed UT, plus a call-order swap so the gate sees the post-trim active recording. `ChainPromotionShouldEmitEngineSeeds(Recording activeRec, out int engineEventCount, out int totalEventCount)` counts only `EngineIgnited` / `EngineThrottle` / `EngineShutdown` events — matching the orphan guard's actual contract via `GhostPlaybackLogic.BuildEngineEventKeySet` — so a recording with a lone `LightOn` still falls into the seed-emit branch. When the gate fires, `EmitEngineOnlySeedEventsForPromotion` calls `PartStateSeeder.EmitEngineSeedEvents` directly so only engine sentinels enter `PartEvents`; non-engine seeds (`DeployableExtended`, `LightOn`, `ShroudJettisoned`, etc.) remain skipped on promotion to preserve the bug A / #263 invariant. Because `EngineShutdown` sentinels are NOT inert in `RecordingOptimizer.IsInertPartEventForTailTrim`, stamping them at the current promotion UT would still poison `FindLastInterestingUT` for any quickload-resume of an empty-engine recording with live engine parts. `ResolveChainPromotionSeedUT(Recording activeRec, double currentUT)` anchors the seed UT at `Recording.StartUT` when the recording has established trajectory content (at least one Point, OrbitSegment, or playable TrackSection — checked via the new `Recording.HasActualTrajectoryBounds` predicate) and falls back to `currentUT` for genuinely fresh chain branches that have no actual trajectory data yet. The discriminator is `HasActualTrajectoryBounds`, not the sign of `StartUT`: 0.0 is a valid KSP UT (sandbox-epoch starts, debug worlds), and a recording whose `Points[0].ut == 0.0` correctly anchors sentinels at 0.0. Finally, `FlightRecorder.StartRecording` now invokes `PrepareQuickloadResumeStateIfNeeded` BEFORE `ResetPartEventTrackingState`, so the gate inspects the POST-trim active recording. Without the swap, an abandoned-future `EngineIgnited` (state recorded between the quicksave UT and the live UT at load time) would convince the gate to skip, only for `TrimRecordingPastUT` to delete that event moments later and leave the resumed recording with zero engine events — re-tripping the orphan auto-start. The two helpers were already independent (one trims a tree recording, the other resets recorder-local tracking sets), so the swap is mechanical.

**Coverage:** `OrphanEngineFxAutoStartTests` covers both helpers: `ChainPromotion_*` for the gate (null rec, null PartEvents, fresh Re-Fly fork using the actual `rec_152453a952804ee7b54f129bdfe2fdc1` id, populated quickload-resume, lone-`LightOn` round-1 P1 case, plus a `[Theory]` over the three engine event types), and `SeedUT_*` for the anchor (null rec, fresh empty rec, populated rec being resumed at non-zero start, populated rec with sandbox-epoch `StartUT == 0.0`, StartUT == currentUT, StartUT in the future, and an empty rec with only `ExplicitStartUT` set). `Trim_ThenGate_*` covers the quickload-trim x gate interaction: an abandoned-future `EngineIgnited` trimmed by `TrimRecordingPastUT` correctly flips the gate decision so engine sentinels get emitted, and a pre-cutoff `EngineIgnited` that survives trim correctly takes the skip branch. Full suite verified after each iteration.

**Status:** CLOSED 2026-05-13.

---

## Done - v0.9.2 Predicted orbit tail dropped at merge when section endUT extends past last recorded point

- ~~On a Re-Fly recording whose extrapolated finalizer tail had been reseeded at the last recorded `TrackSection.frames` UT, the merger silently dropped the reseeded predicted `OrbitSegment` and only kept a second extrapolated-only segment at a much later UT. With the reseeded segment gone and the surviving late segment's gap (~1226 s) blowing past `DestroyedPredictedOrbitTailBridgeMaxGapSeconds = 5.0`, `GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment` failed both the in-range and bridge cases and playback fell through to clamping at `t=1.0` of the last flat-point pair, freezing the ghost. Source: `logs/2026-05-13_1848_ghost-tail-render-broken`, recording `rec_152453a952804ee7b54f129bdfe2fdc1`. Trailing `TrackSection.endUT = 158.47` extended ~2.04 s past the last `frames` UT (the anchor at `156.43`); the finalizer's `TryReseedFirstPredictedTailSegmentFromRecordedAnchor` correctly moved `newStartUT=156.43`, but `SessionMerger.TrySyncFlatTrajectoryPreservingPredictedOrbitTail` used `maxTrackSectionEndUT (158.47)` as the predicted-tail floor and rejected the reseeded segment because `156.43 < 158.47`. Two recent commits interact: `c648b0b0` "Stabilize watch activation and predicted tails" reseeds at the anchor frame UT, and `de9ce0f6` + `684806c0` (PR #727) "Preserve / Harden refly finalizer tail preservation" added the merger floor.~~

**Fix:** `SessionMerger.TrySyncFlatTrajectoryPreservingPredictedOrbitTail` now computes the predicted-tail floor from the resolved payload it is about to write to `target` — `max(rebuiltPoints.Last().ut, rebuiltOrbitSegments.Last().endUT)` — falling back to `sectionEndUT` only when both rebuilt surfaces are empty (defensive; unreachable given the upstream `HasCompleteTrackSectionPayloadForFlatSync` gate). The rebuilt payload's last UT is exactly the playback hand-off bound (`GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment` reads `Points[Points.Count - 1].ut`), so a predicted segment whose `startUT >= predictedTailFloorUT` is a legitimate finalizer suffix. An earlier `min(lastSourcePointUT, maxTrackSectionEndUT)` formulation was rejected on follow-up review because a stale or truncated `source.Points` could lower the floor below the resolved payload end and silently accept a predicted segment anchored at a stale orbital state.

**Coverage:** Added `MergeTree_PreservesReseededPredictedTailWhenSectionEndUTExtendsPastLastRecordedPoint` (settle-tail repro modeled on the retained logs, asserts both predicted segments survive and the merger logs `flatSync=track-sections-preserved-predicted-orbit-tail:2`), `MergeTree_PreservesPredictedTailWhenLastPointAlignsWithSectionEndUT` (edge case with no settle tail — `rebuiltPoints.Last().ut == sectionEndUT`, so the resolved-payload floor collapses to the same value as the old `maxTrackSectionEndUT` bound), and `MergeTree_RejectsPredictedSegmentAnchoredBelowResolvedPayloadWhenSourcePointsAreTruncated` (P2 follow-up: stale/truncated `source.Points` must not lower the floor below the rebuilt payload end; verified to FAIL with the prior `min(...)` formulation and PASS with the resolved-payload floor). Existing PR #727 cases (`PreservesOrbitOnlyPredictedTailWhenFlatPointsAreStale`, `PreservesPredictedTailAfterCheckpointPrefixWithRoundTripDrift`, `PreservesPredictedTailAfterClippedCheckpointPrefix`, and `RejectsUnsafePredictedOrbitTailWhenFlatPointsAreStale` for non-predicted/non-monotonic/starts-before-section-end shapes) still pass — those tests' predicted segments either sit past `maxTrackSectionEndUT` (so past the rebuilt payload end too) or fail the predicted/monotonicity gates before the floor matters.

---

## Done - v0.9.2 Re-launching same `.craft` after a committed mission silently merged into the prior tree

- ~~When the player committed a recording (e.g. Kerbal X mission 1 ending Landed) and then launched the same `.craft` again — even with a Re-Fly in between — the new mission attached to the prior tree instead of starting its own. The auto-generated group still read "Kerbal X", and the STASH listed both missions' decoupled probes as duplicate `Kerbal X Probe` rows. Repro: `logs/2026-05-13_1850_kerbal-x-merge-bug`, mission 1 launch at 18:33:54 → commit at 18:34:48 with `3554bcbb...SpawnedVesselPersistentId=2708531065`, Re-Fly Probe at 18:35:00, mission 2 launch at 18:35:49 with the same pid 2708531065 (KSP's craft-derived persistentId is deterministic enough for re-launching the same `.craft` to recycle the previous mission's pid), then `TryTakeCommittedTreeForSpawnedVesselRestore: removed committed tree 'Kerbal X' (10 recording(s))` at 18:35:51 — the new mission was folded into the old tree.~~

**Root cause:** Re-Fly does NOT route through `HandleRewindOnLoad`/`ResetAllPlaybackState` (those gate on `RewindContext.IsRewinding`, which Re-Fly never sets). The prior committed recording kept its `SpawnedVesselPersistentId=2708531065`, and `RecordingStore.PreserveLiveRuntimeFieldsOnReplace` (the spawn-state cluster from #264) re-installs that stale pid on the Re-Fly merge replace. `TryFindCommittedTreeForSpawnedVessel` then matched the fresh launch's pid against the stale stamp, and the mission was attached to the existing tree.

**Fix:** Two pure helpers plus a `ParsekFlight.Start`-time capture step.

- `ParsekFlight.IsFreshLaunchStartupBehaviour(FlightDriver.StartupBehaviours)` returns true for `NEW_FROM_FILE` (editor Launch button) and `NEW_FROM_CRAFT_NODE` (Mission Builder / scenario inline craft launch). `FlightDriver.StartupBehaviour` (Assembly-CSharp/FlightDriver.cs:38) is KSP's own authoritative scene-startup mode: set by the editor's Launch handler / save-loader / revert path before the FLIGHT scene transitions in, stable for the entire scene's lifetime. Compared to the originally-tried `Vessel.Situations.PRELAUNCH` + `missionTime` pair, it does not expire as the player sits on the pad (game UT progresses at PRELAUNCH, so `missionTime` can grow past any threshold before staging); compared to `GameEvents.onLaunch` it is observable synchronously without a subscription race against `HandleMissedVesselSwitchRecovery`'s 1-second retry loop.

- `ParsekFlight.CaptureFreshRolloutVesselPidIfApplicable()` runs once during `Start`, and stores `FlightGlobals.ActiveVessel.persistentId` into the scene-scoped instance field `freshRolloutVesselPid` only when `IsFreshLaunchStartupBehaviour` returns true. RESUME_SAVED_FILE / RESUME_SAVED_CACHE scenes leave the field at 0 so the guard is inactive.

- `ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(activeVesselPid, freshRolloutVesselPid)` is a pure pid match. `TryRestoreCommittedTreeForSpawnedActiveVessel` calls it on every restore attempt and rejects ONLY when the active vessel's pid matches the captured rollout pid. The identity component is what keeps mid-scene vessel switches working: a player on a NEW_FROM_FILE scene who switches from the freshly-launched craft (pid X, guarded) to a nearby already-spawned committed vessel (pid Y) still resumes Y's committed recording because `X != Y`.

The bug repro is the canonical NEW_FROM_FILE path: `logs/2026-05-13_1850_kerbal-x-merge-bug/KSP.log` line 53466 shows `Loading ship from file: ...\Auto-Saved Ship.craft` immediately before the FLIGHT scene loaded, which is FlightDriver's `NEW_FROM_FILE` dispatch branch (FlightDriver.cs:334-345).

`GameEvents.onLaunch` is not used by this guard. Decompiling `Assembly-CSharp.dll` confirmed `KSP.UI.Screens.StageManager.cs:3379` fires it on first-stage activation, not on rollout, which is too late for the documented restore that runs from `HandleMissedVesselSwitchRecovery` in `Update()` ~63 ms after `Parsek Flight loaded` (well before the player presses space).

The static lookup `TryFindCommittedTreeForSpawnedVessel` is unchanged so background-promotion and missed-switch recovery for save-loaded vessels keep working. Helpers are unit-tested across all four `FlightDriver.StartupBehaviours` values plus the pid-match identity matrix.

**Follow-up:** The Re-Fly load-path symptom (downstream PID dedup blocking re-spawn) is closed by the "Re-Fly load path skipped stale `SpawnedVesselPersistentId` reconcile" entry above — `RewindInvoker.RunStripActivateMarker` now routes through `ReconcileSpawnStateAfterStrip` after the post-load strip. The deeper invariant violation in `RecordingStore.PreserveLiveRuntimeFieldsOnReplace` (the merge step re-installing the stale stamp in the first place) remains open as a hygiene item — see the "`RecordingStore.PreserveLiveRuntimeFieldsOnReplace` re-installs stale `SpawnedVesselPersistentId` across Re-Fly merge" entry below.

**Status:** CLOSED 2026-05-13.

---

## Done - v0.9.2 Rewound recording's vessel does not spawn when watched to terminal

- ~~After a Rewind-to-Separation onto a recording with a spawnable terminal state (Landed/Splashed/Orbiting), entering Watch and letting the ghost play through to its terminal point left the vessel un-materialized. `ParsekPlaybackPolicy.HandlePlaybackCompleted` reported `needsSpawn=False` because `ShouldBlockSpawnForRewindSuppression` short-circuited on the same-recording `SpawnSuppressedByRewind` marker (`#573 active/source recording protection`). Source: `logs/2026-05-12_2018_kerbalx-no-spawn`, recording `e4c8042527c649648b7f94a5175d312d`. The original #573 fix was scoped to protect against background ghost playback duplicating a vessel the player just stripped on rewind (chain-tip respawn next to the player's freshly-launched new vessel). It was overly broad for the case where the player explicitly Watches the rewound recording to its terminal point.~~

**Fix:** `ParsekScenario.TryClearSpawnSuppressionOnWatchEntry` lifts the same-recording `SpawnSuppressedByRewind` marker at Watch entry from `WatchModeController.EnterWatchMode`. Watching is the player's explicit signal that they want this recording's outcome to materialize, so the spawn-at-recording-end path runs naturally when ghost playback reaches the terminal. Only the same-recording reason is touched; legacy-unscoped markers continue to flow through `ShouldBlockSpawnForRewindSuppression`'s existing normalization path. Background ghosts the player ignores after rewind retain the marker exactly as before.

**Coverage:** `RewindSpawnSuppressionTests` covers the helper directly (same-recording marker cleared with audit log + subsequent `ShouldSpawnAtRecordingEnd` returns `needsSpawn=true`), no-op cases (null/empty/legacy-unscoped markers), and the full mark → watch → spawn sequence.

**Status:** CLOSED 2026-05-12.

---

## Done - v0.9.2 Re-Fly probe ghost duplicated after on-rails transition

- ~~In Watch mode after a probe Re-Fly reached space and vessels packed/on-rails, the probe booster ghost could appear doubled and the Recordings window could show two `Kerbal X Probe` exo/orbiting rows. In the retained repro (`logs/2026-05-11_1919_doubled-probe-ghost`), restore swapped the active recorder to the Re-Fly fork for PID `429255699`, but `RecordingTree.RebuildBackgroundMap()` left another non-active recording with the same PID eligible for background tracking. The background recorder kept flushing the old `51e41e...` recording while the active recorder wrote `rec_78ecd...`; optimization later split the stale old row into its own exo/orbiting segment, so both paths rendered and one duplicate path spawned a terminal orbital vessel.~~

**Fix:** Background-map eligibility now rejects any non-active recording whose `VesselPersistentId` matches the active recording's PID, even when the recording IDs differ. This keeps the active recorder as the only owner of the live vessel after in-place Re-Fly restore and logs `activePidSkips` during rebuild for future diagnosis.

**Coverage:** Added xUnit coverage for an in-place continuation tree containing an old probe recording and a new active fork with the same PID. The test verifies the old same-PID row is excluded from `BackgroundMap`, unrelated background vessels remain eligible, and the skip count is logged.

---

## Done - v0.9.2 probe ghost hidden by suborbital OrbitSegment radius gate

- ~~Probe-stage ghost playback could reject a valid suborbital `OrbitSegment` before resolving playback distance because the old guard treated `|sma| < body.Radius * 0.9` as invalid. The retained Kerbal X Probe repro includes an ascent segment around `sma=512 941 m` on Kerbin: below the 540 km threshold, but still the correct Kepler source for playback at that UT. Once rejected, the distance resolver could fall through to flat point metadata from a RELATIVE section and interpret anchor-local metre offsets as body-fixed lat/lon/alt, producing a bogus far-away distance and zone-hiding/jumping the ghost.~~

**Fix:** Orbit playback now uses a body-radius-independent usability check: orbital elements must be finite and `|sma| >= 1 m`, but suborbital conics are allowed. The flight distance resolver, orbit-tail gate, orbit positioning cache, checkpoint orbit cache, and pending-spawn interpolation share that rule, with degenerate segments falling back to point metadata rather than valid suborbital segments doing so.

**Coverage:** Added xUnit coverage that pins the `sma=512 941 m` suborbital case as usable, keeps zero/non-finite SMA rejected, verifies pending-spawn interpolation prefers the active suborbital orbit segment over points, verifies the orbit-tail gate skips degenerate segments, and preserves point fallback for a degenerate orbit segment.

---

## Done - v0.9.2 Re-Fly probe spawn rejected from frame-mismatch in tail-derived terminal orbit

- ~~A Re-Fly fork ending in a highly-eccentric stable orbit (the `Kerbal X Probe` recording in `logs/2026-05-10_2123` — `tOrbSma=4 547 677, tOrbEcc=0.822, periAlt ≈ 208 km`) was deferred-then-permanently-rejected at spawn time. The `TryDeriveTerminalOrbitSeedFromTrajectoryTail` helper added in the previous Done item ("Re-Fly spawn refused circularized upper stage with stale on-rails OrbitSegment") found the right tail frame but reseeded the orbit from world-absolute Y-up state vectors instead of body-relative Z-up, producing `sma=567 357, periAlt=−438 222 m` (subsurface). Safety gate deferred at currentUT=455.25 because propagated alt was −98 949 m; the rotation-drift gate then forced the retry to fall back to the recording's only stored OrbitSegment — the pre-burn ascent ellipse at `epoch=142.16, sma=512 941, periAlt=−382 km` — and the safety gate rejected `CannotSpawnSafely`. Probe never materialized; the `Kerbal X` upper-stage chained successor spawned because its tail carried an authoritative `OrbitalCheckpoint`.~~ Reproduced by `logs/2026-05-10_2123` recording `rec_f1363fc127ab47a28812ce4be6515453`. Investigation report: `docs/dev/research/probe-tail-orbit-spawn-frame-mismatch.md`.

**Root cause:** `Orbit.UpdateFromStateVectors` (decompiled from `Assembly-CSharp.dll`) requires `pos` to be RELATIVE to the reference body and `vel` to be in `Planetarium.Zup` local axes — both `(input - body.position).xzy` from the world-absolute Y-up vectors KSP exposes through `body.GetWorldSurfacePosition` / `rb_velocityD + Krakensbane.GetFrameVelocity()`. KSP's own canonical wrapper `Orbit.OrbitFromStateVectors` does this correctly. The Parsek tail-derive path was passing both axes through unchanged, producing a structurally-finite but physically-wrong orbit whose `|pos|` was off by `body.position` and whose orientation was rotated by the missing `.xzy`. `sma` is invariant under `.xzy` (axis swap preserves magnitude) but not invariant under the missing `(pos − body.position)` — `body.position` for Kerbin in flight scene with the active vessel parked on the launch pad evaluated to ~310 km of magnitude in the captured run, partially cancelling the 808 km surface offset and leaving the helper computing `sma` from `|pos|≈498 km`. For the upper stage (which had a stored `OrbitalCheckpoint` `OrbitSegment` covering its tail), the picker never reached the broken helper, so the bug only surfaced on recordings that ended in stable orbit without an authoritative segment closing them.

**Fix:** New `Source/Parsek/OrbitReseed.cs` centralizes the `Orbit.UpdateFromStateVectors` frame contract. `FromLatLonAltAndRecordedVelocity` handles body-fixed lat/lon/alt plus Y-up recorder velocity by applying `(pos - body.position).xzy` and `vel.xzy`; `FromWorldPosAndZupVelocity` handles world-absolute position plus already-Zup orbital velocity by applying the position transform only; `FromWorldPosAndRecordedVelocity` covers world-absolute position plus Y-up recorded velocity; and the pure input helpers expose those transforms to xUnit. `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` is routed through the shared tail resolver with `TailSeedUse.Spawn`, preserving the 30 s rotation-drift guard for spawn safety.

**Coverage:** Tightened the existing `TerminalOrbitFromTail_DerivesPostBurnCircularOrbit` in-game test (`Source/Parsek/InGameTests/RuntimeTests.cs`) to assert tight `sma` (within 5 km of the analytic 803 587 m), `ecc < 0.005`, and `inclination < 0.5°` — the prior assertions only checked `SpawnNow`, which the buggy frame happened to clear for that geometry. Added new in-game tests for the eccentric probe shape and for GhostMap's historical MapPresence tail seed. xUnit covers the pure `(worldPos - body.position).xzy` / recorded-velocity `.xzy` helpers, the Zup-velocity helper, the stale endpoint-tail predicate, EndpointTail dispatch narrowing, and EndpointTail visible-bounds precedence. Full residual/orbit validation remains KSP-runtime-only because `body.GetWorldSurfacePosition` and body rotation live behind Unity/KSP transforms.

**Sibling audit status after the orbit/ghost correctness pass:**

- ~~**`Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` predicted-tail reseed**~~ — fixed via `OrbitReseed.FromLatLonAltAndRecordedVelocity`, matching the recorder-velocity frame contract. Retained logs confirmed the failure mode before the fix: `logs/2026-05-10_2123/KSP.log` reported `residualMeters=670062.87`, and `logs/2026-05-10_1713/KSP.log` showed the same ~666-671 km residual class.
- ~~**`Source/Parsek/GhostMapPresence.cs` state-vector create/update paths**~~ — fixed via `ResolveStateVectorWorldPosition` plus `OrbitReseed.FromWorldPosAndRecordedVelocity`; Relative/Absolute/OrbitalCheckpoint world-position resolution remains centralized before the state-vector reseed.
- ~~**`Source/Parsek/FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment`**~~ — fixed via `OrbitReseed.FromWorldPosAndZupVelocity` for `Orbit.getOrbitalVelocityAtUT`, with non-finite orbital velocity now declining explicitly instead of falling back to `vessel.obt_velocity` in the wrong frame.
- ~~**`Source/Parsek/VesselSpawner.TryResolveEndpointStateVector` fallback**~~ — fixed via `OrbitReseed.FromLatLonAltAndRecordedVelocity` for recorder endpoint velocities.
- **Still open: `Source/Parsek/VesselSpawner.cs:1001` / spawn-position no-override paths** — caller-supplied velocity frame still depends on the entry point and remains a separate audit item.
- **Still open: `VesselGhoster.TryResolvePropagatedOrbitSeed` freshness policy** — this pass fixes GhostMapPresence map/Tracking Station ProtoVessel and orbit-line behavior; non-map propagated ghost paths should only be changed after a reproducer confirms the same stale endpoint-segment symptom there.

Player-visible breakage from these sites is masked today by other paths winning the orbit-seed picker (the spawn case here was the first one we caught where the broken helper was the *only* path the picker had).

---

## Done - v0.9.2 ghost map orbit line drawn from stale OrbitSegment for orbiting recordings whose post-burn frames superseded it

- ~~For recordings shaped like the Kerbal X bug above (one stored sub-orbital `OrbitSegment` from the pre-burn on-rails coast, plus an `ExoBallistic` Absolute tail frame that defines a post-burn circular orbit), the spawn path now reseeds correctly, but `GhostMapPresence.TryResolveGhostProtoOrbitSeed` still pulled from `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed`, which returned the stale segment. Result: in the Tracking Station and on the map view, the ghost orbit line for these recordings showed the pre-burn sub-orbital ellipse passing through the planet — even though the spawned real vessel sat on the correct post-burn orbit.~~
- **Investigation 2026-05-10:** confirmed on current `main` with retained evidence. `logs/2026-05-10_2123` recording `rec_f1363fc127ab47a28812ce4be6515453` has stale sidecar orbit segments around `sma=512941`, `ecc=0.574602`, ending at UT `415.022`, followed by later Absolute `ExoBallistic` tail frames ending at UT `453.662`. The save metadata has the correct terminal orbit (`tOrbSma=4547677.2114545386`, `tOrbEcc=0.82238029649173194`, `tOrbEpoch=459.44214255408241`). GhostMap logged the stale segment source before the terminal data became usable, so spawn and ghost-map seed selection could disagree.
- **Code path:** `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed` accepted the last same-body `OrbitSegment` without checking whether later tail frames superseded it. `GhostMapPresence.TryResolveGhostProtoOrbitSeed` inherited that behavior; `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` had already moved ahead by preferring the tail-derived seed first. `VesselGhoster.TryResolvePropagatedOrbitSeed` freshness policy remains a separate non-map follow-up above.

**Fix:** `GhostMapPresence.ResolveMapPresenceGhostSource` can now return an explicit `EndpointTail` source when a visible segment is an endpoint-stale segment: the recording is in its terminal map-presence region, the persisted endpoint phase/body is `OrbitSegment` for the same body, `RecordingEndpointResolver` itself reports `Source="endpoint-segment"`, and a `TailSeedUse.MapPresence` historical body-rotation tail seed is fresher than the latest stored segment. EndpointTail creation/update dispatches through the segment path with `source=EndpointTail`, stores synthetic tail bounds, and `TryGetVisibleOrbitBoundsForGhostVessel` now lets those stored bounds win for EndpointTail ghosts so orbit-line/icon clipping does not fall back to the stale committed segment window. TerminalOrbit-backed recordings are intentionally not promoted to EndpointTail.

**Diagnostics:** GhostMap decision lines now carry `endpointTailSeed=accept|decline`, `tailUT`, `tailSma`, `tailEcc`, latest stored segment end, rotation drift, tail frame source, historical-rotation flag, and endpoint source/phase/body details when EndpointTail is considered but declined.

**Coverage:** `GhostMapEndpointTailTests` covers stale endpoint override, legitimate in-window checkpoint preservation, TerminalOrbit-backed non-promotion, Segment decision logging when EndpointTail declines, and visible-bounds precedence after EndpointTail creation state is recorded. KSP-runtime validation remains in `GhostMapEndpointTail_UsesHistoricalTailSeedAcrossActivationDrift` because reconstructing historical body rotation depends on live KSP body transforms.

---

## Open - coverage gap: `RewindInvoker.RunStripActivateMarker` reconcile wrapper has no direct test

The Re-Fly post-load reconcile call site (`Source/Parsek/RewindInvoker.cs:~814-862`) wraps the survivor-set computation plus `ParsekScenario.ReconcileSpawnStateAfterStrip` call in a `try { … } catch { Warn(…) }` with `HighLogic.CurrentGame?.flightState`, `RecordingStore.CommittedRecordings`, and `fsReconcile.protoVessels` null guards. The two computational pieces are unit-tested by `SpawnStateReconciliationTests`: `ComputeSurvivorsFromProtoVesselPids` covers the production-shape PID subtraction, and `ReconcileSpawnStateAfterStrip` covers the reset logic. The wrapper itself — `flightState == null` skip, `committed == null || Count == 0` skip, `protoVessels == null` defensive branch, the `Info` log emission, and the warn-log on a thrown helper — is not exercised by any xUnit case.

This matches the existing pattern at `ParsekScenario.cs:1701` and `:2405`, which are also un-tested wrappers around the same helper — note: those call sites still pass the raw `flightState.protoVessels` to the original `ReconcileSpawnStateAfterStrip(List<ProtoVessel>, IReadOnlyList<Recording>)` overload (which routes through `CollectSurvivingPids`, NOT the new subtraction helper). They may suffer the same input-shape bug as the Re-Fly call site did before this PR, but no concrete repro has been captured; out of scope here. Adding direct coverage to the Re-Fly wrapper would require either (a) extracting the wrapper from `RewindInvoker.RunStripActivateMarker` into an `internal static` method that takes pre-collected `(IEnumerable<uint> protoVesselPids, IEnumerable<uint> strippedPids, IReadOnlyList<Recording> committed)` parameters and re-routing the existing call site through it, or (b) introducing a `HighLogic.CurrentGame` / `RecordingStore.CommittedRecordings` indirection seam mockable from xUnit. Both are larger than the PR scope, and the wrapper has no behavioral branching beyond the null guards + log emission — the substance lives in the already-covered helpers.

**Fix shape if revisited:** option (a) is the cheaper path — `RewindInvoker.TryReconcileSpawnStateAfterStripForReFly(IEnumerable<uint> protoVesselPids, IEnumerable<uint> strippedPids, IReadOnlyList<Recording> committed, Action<string> warnLogger = null, Action<string> infoLogger = null)`, returning the int reconcile count from the helper or `0` on null/empty guards. Add four xUnit cases: null `protoVesselPids` (defensive skip, returns 0 with empty survivor set); null/empty `committed` (skip, no log, returns 0); helper throw (warn log emitted, returns 0); happy-path summary log emission (`Post-strip reconcile: strippedPids=N protoVesselsRemaining=M survivorPidCount=K`). Severity: **low** — the wrapper is mechanical and the underlying helpers are well-covered; this is hygiene for the new call site, not a real risk.

---

## Open - `RecordingStore.PreserveLiveRuntimeFieldsOnReplace` re-installs stale `SpawnedVesselPersistentId` across Re-Fly merge

The deeper invariant beneath the v0.9.2 Re-Fly reconcile fix. `RecordingStore.PreserveLiveRuntimeFieldsOnReplace` (the #264 spawn-state cluster preservation step that runs on every Re-Fly merge `replace`) re-installs the prior recording's `SpawnedVesselPersistentId` onto the replacement recording, even when the live vessel that PID pointed to was about to be stripped from the save by `PostLoadStripper.Strip`. The Re-Fly merge re-stamps stale PIDs that no longer correspond to any live vessel.

The current PR neutralizes the downstream consequence at the Re-Fly load path (`RewindInvoker.RunStripActivateMarker` now calls `ReconcileSpawnStateAfterStrip` after the strip), so the empty-capsule re-spawn case is fixed end-to-end. However, every other consumer that pid-matches on `Recording.SpawnedVesselPersistentId` outside the Re-Fly load path is still carrying the stale stamp between merge time and the next reconcile pass — `Source/Parsek/RecoverTimelineSpawnedVessel.cs` and `Source/Parsek/SupersedeCommit.cs:ShouldMarkSupersededTerminalSpawn` (search via grep) are the two highest-risk readers. No concrete bug repro has been captured against either; this is a hygiene follow-up flagged by the v0.9.2 reviewer pass.

**Fix shape:** the cleanest single point of fix is `RecordingStore.PreserveLiveRuntimeFieldsOnReplace` itself — when preserving `SpawnedVesselPersistentId` from the existing recording onto the replacement, check whether the existing PID is present in `HighLogic.CurrentGame?.flightState?.protoVessels` (the same vessel set the new `ReconcileSpawnStateAfterStrip` overload reads). Skip the preservation when the live vessel is gone, leaving the replacement with `SpawnedVesselPersistentId=0` + `VesselSpawned=false`. Alternative shape: leave the helper alone and have every PID consumer route through a `IsLivePid` predicate that consults the same vessel-set, but that touches many more call sites. The helper-side guard is preferable. Add direct unit coverage for `PreserveLiveRuntimeFieldsOnReplace` (vessel present → preserve; vessel absent → reset to 0). Severity: **low** — the downstream symptom is now fixed at the Re-Fly load path, and no concrete bug repro exists against the remaining consumers; this entry exists so the deeper invariant violation does not get lost.

---

## Open - Re-Fly continuation ghost vanishes when active vessel crosses into Inertial reference frame

- A committed Re-Fly continuation ghost (a "Kerbal X Probe" recording, `rec_152453a952804ee7b54f129bdfe2fdc1`) stops being rendered the moment the active live vessel crosses into the KSP Inertial reference frame (around ~100 km altitude on Kerbin, frequently coincident with the active vessel going on-rails / packing). The user reports this is a recent regression — it used to render correctly through that transition.
- Authoritative log: `logs/2026-05-13_1848_ghost-tail-render-broken/KSP.log`. The last `GhostRenderTrace` event for the affected recording is at line ~130145 (`phase=AfterUpdate rec=rec_1524 ghostIndex=9 frame=75777 currentUT=160.470`). Stock KSP `Reference Frame: Inertial` appears 45 frames later at line 130166. From frame 75865 the `GuardSkip` summary lists indexes 0–8 and 10 but never index 9; engine batch summary still reports `active=1`. Pre/post-shift batch counters are identical (`noRenderableData=1 sessionSuppressed=8 supersededByRelation=1 active=1`).
- Pre-investigation refuted H1 (v13 parent-anchored debris retire path over-broadened to non-debris): `DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss` (`Source/Parsek/DebrisRelativePlaybackPolicy.cs:56-62`) requires both `traj.IsDebris && traj.DebrisParentRecordingId != null`, and every retire site routes through it. The remaining hypotheses (H2 ReFlySettle hold-frame interaction, H3 silent early-return inside `UpdatePlayback`, H4 floating-origin pop into a tightened distance band) can't be disambiguated from log alone because `GhostRenderTrace.ShouldEmitPhase` gates everything through `IsDetailedWindowOpen` (`Source/Parsek/GhostRenderTrace.cs:557-568`) — the absence of trace events does NOT prove rec_1524 fell out of the engine's iteration.

**Instrumentation:** This PR adds an `[Engine] engine-frame-iter` log line that emits approximately one sampled snapshot per second (1.0s rate limit) when `ghostRenderTracing` is on, with `suppressed=N` counting the intervening frames, listing every iterated trajectory's `(i, recordingId-short, skipReason or "None", anchorReFlyUnstable, hasRenderableData, ghostStates.ContainsKey(i), traj.EndUT)`. The 1s sample doesn't guarantee a snapshot inside a sub-second event window, but the before/after samples bracket the transition with a worst-case gap of ~2× the rate limit — compare the entry for rec_1524 in the snapshot immediately before the `Reference Frame: Inertial` log line with the one immediately after. The line bypasses `GhostRenderTrace.ShouldEmitPhase` entirely so the next repro can tell whether rec_1524 reaches the per-trajectory loop, what its producer-side `skipReason` is, whether `anchorReFlyUnstable` was set (the engine reads that flag mid-loop and skips the ghost even when `skipReason` is None — directly the H2 hypothesis), whether its trajectory has renderable data, and whether `ghostStates[9]` still holds an entry.

**Next-repro signal:** With tracing on, grep `[Engine] engine-frame-iter` around the `Reference Frame: Inertial` line. If the entry for rec_1524 disappears from the comma-separated list, the trajectory was removed from `trajectories` (the host-side `ParsekFlight` list); if it stays with `skip=None aru=F hd=T hs=T`, the engine still iterates it and the silence is purely a trace-gate artefact (rec_1524 is rendering normally, no regression); if `aru` flips F→T the producer is marking the recording as anchor-refly-unstable mid-loop (H2 confirmed); if `skip` flips to a specific reason (e.g. `before-activation`, `playback-disabled`) the producer-side flag is the source. If `hd` flips T→F, `HasRenderableGhostData` is the source (something cleared `Points`/`OrbitSegments`/`SurfacePos` on the trajectory). If `hs` flips T→F mid-session without a `Ghost #9 destroyed` line in between, `ghostStates` is being mutated outside the engine's known paths.

**Status:** OPEN.

---

## Open - stale `RewindReplayTargetSourcePid` cross-contaminates `SpawnSuppressedByRewind` across consecutive rewinds

Audit of `Recording.SpawnSuppressedByRewind` after PR #829 surfaced this. `ParsekScenario.ShouldApplyRewindSpawnSuppression` has a standalone-recording branch (`ParsekScenario.cs:5677`) that returns `same-recording` when `rewoundTreeId == null` and `rec.VesselPersistentId == rewindSourcePid`. KSP reuses persistent IDs after vessel deletion, so a session that first rewinds tree A (which sets `RecordingStore.RewindReplayTargetSourcePid = pidA`) and then rewinds a standalone recording without that field being reset will mark **every committed recording whose PID matches the stale pidA** as `same-recording`. The PID-only path is meant to cover the legitimate "standalone (no tree) rewind by source PID" case but does not currently sanity-check that the source recording matching the rewind context is present.

**Symptom:** after a second consecutive rewind on a standalone recording, an unrelated tree's recordings sharing a recycled PID stop spawning at their terminal. No log line says "wrong recording marked" — the audit trail looks correct in isolation.

**Fix shape:** (1) clear `RewindReplayTargetSourcePid` / `RewindReplayTargetRecordingId` inside `RewindContext.EndRewind` (or wherever the unconsumed-fields drain runs) so a stale value cannot survive into the next `MarkRewoundTreeRecordingsAsGhostOnly` call; (2) tighten `ShouldApplyRewindSpawnSuppression`'s standalone-PID branch to require a real `rewindRecordingId` co-presence — without a real rewind target id, the PID-only path returns false. Add `MarkRewoundTreeRecordingsAsGhostOnly_StandaloneRewindAfterTreeRewind_DoesNotMarkUnrelatedPid` regression coverage. Severity: **medium** — silent cross-contamination, but requires two rewinds in one session to trigger.

---

## Open - `ShouldBlockSpawnForRewindSuppression` mutates inside a predicate

Same PR #829 audit. `GhostPlaybackLogic.cs:4924-4952` is named/typed like a pure read but auto-clears the marker and emits an `[Rewind] Info` log when the reason is `legacy-unscoped` (or null). Callers — `ShouldSpawnAtRecordingEnd`, `ShouldSpawnAtKscEnd`, `ShouldSpawnAtTrackingStationEnd`, `ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn` — treat the function as a predicate and call it from per-frame hot paths. A legacy save that survived the load-time normalizer with a stale `legacy-unscoped` marker produces one `[Rewind]` clearance Info log on the first call and then mutates the recording mid-frame from inside what looks like a read. Idempotent in effect, but a real surprise-side-effect and log-noise hazard if the same recording is touched from multiple call sites in a single frame.

**Fix shape:** keep `ShouldBlockSpawnForRewindSuppression` strictly read-only (return false for non-same-recording reasons without clearing). Move the legacy-unscoped auto-clear into a one-shot maintenance pass that runs from `HandleRewindOnLoad` and `OnLoad` next to the existing `RecordingTree.NormalizeLegacyRewindSuppressionMarkers` so it lives alongside the other legacy-shape normalization. Add `ShouldBlockSpawnForRewindSuppression_LegacyMarker_DoesNotMutate` regression coverage that calls the predicate twice and asserts the marker is unchanged after the first call. Severity: **low** — the current implementation is correctness-equivalent on saves that load cleanly, but the architectural surprise survives PR review by being well-documented in comments rather than enforced by the type signature.

---

## Done - v0.9.2 post-staging debris forward slide caused by stale FG recorder LLA

Watch-mode playback of a parent-anchored debris ghost showed a visible ~2 m forward slide on the first lerp interval after a staging joint-break: "ghost appears in the right position then immediately slides about 2 metres in front." A previous attempt (PR 824 commits `140c1a5` / `1c85380` / `00b0df2`, all reverted in `8f57842` / `e7ccdcd` / `686a0e3`) tried to back-step every recorded sample by `Time.fixedDeltaTime * v_inertial` on the hypothesis that KSP's joint-break callbacks fire post-PhysX with `Planetarium.GetUniversalTime()` still at start-of-tick. That fix didn't kill the slide and was reverted along with all three commits.

**Resolution (PR 832):** the slide came from a one-PhysX-tick staleness in `FlightRecorder.BuildTrajectoryPoint`, not from a structural-event seed offset. The function was reading `vessel.latitude/longitude/altitude` directly, but for loaded/unpacked vessels those fields are refreshed by `Vessel.LateUpdate` AFTER PhysX has already moved `vessel.transform.position`, so every per-tick FG sample stored a position `velocity * fixedDeltaTime` behind ground truth (~4.31 m at 215 m/s in the trace). The bug was invisible during ordinary flight because the offset was uniform along velocity for the whole recording. At staging it became visible: the debris seed at `OnDecoupleNewVesselComplete` already used `body.GetLatitude/Longitude/Altitude(part.transform.position)` (fresh), debris BG samples after on-rails transition were also fresh (~9 mm delta), but the parent vessel stayed on the stale FG path, so the parent ghost rendered ~one tick behind the debris ghost in the velocity direction. PR 832 trace data fixed this with: (a) `|delta| = velocity * 0.02 s` to within 5 mm and `cos(angle(delta, velocity)) = 0.999999`; (b) cross-channel confirmation — parent's recorded body-fixed interpolation at UT=38.94 was ~(145.0, 14.9, 1970.8) while the debris recorder's live `anchorWorldPos` captured the parent at (147.5, 14.9, 1974.3), 4.29 m apart along velocity. The fix is a single-point change in `BuildTrajectoryPoint`: replace the three stale field reads with `body.GetLatitude/Longitude/Altitude(v.transform.position)`, matching the pattern already used at the joint-child seed path (FlightRecorder.cs:1090) and in `BackgroundRecorder.cs:4032`. Other recorder surfaces (Relative anchor projection in `BG_ApplyRel`, body-fixed primary writer) consume the same fresh LLA via the trajectory points BuildTrajectoryPoint emits, so they inherit the fix without separate changes.

This PR ships extended observability on top of the existing `TraceSeparation` window so the next investigation cycle can pick the right hypothesis without rebuilding between repros. New fields:

- `inFixed=` on every trace line — distinguishes FixedUpdate (pre-PhysX) capture sites from post-PhysX callbacks (`OnPartJointBreak`, `OnDecoupleNewVesselComplete`). If `inFixed=T` at a `JointBreak` row, the post-PhysX-callback hypothesis is wrong.
- `PARENT_AT_BREAK predictedSrfStep` and `predictedInertialStep` vs `|observedDelta|` — picks the right velocity frame for any back-step. If `|observedDelta|` matches `predictedSrfStep` (≈ |srfVel|·dt) but `predictedInertialStep` overshoots, the reverted fix was correcting in the wrong frame.
- `CHILD_PART_AT_BREAK childVsParentLLA / alongParentSrfVel` — signed projection of child part transform vs parent's stale-LLA reference along the parent's velocity direction. Positive value (in m) is the on-tick lead of the joint-child seed.
- `PartOriginSeed partVsVesselLLA / |observedDelta| / predictedSrfStep / predictedInertialStep` — same shape on the foreground joint-child seed site that the reverted fix patched.
- `DecoupleSeed` (new row at `OnDecoupleNewVesselDuringSplitCheck`) — observes the `new-vessel-root-part` fallback path's seed-vs-LLA delta and the new-vs-original parent LLA-world delta at the split UT.
- `BuildTP tickSinceBreak / |delta|` and `BG_CreateAbs tickSinceBreak / |delta|` — grep `tickSinceBreak=1.` to pick out the first per-tick sample after the joint break, and read `|delta|` to see whether per-tick samples have a `v·dt` offset (commit 3's hypothesis) or stay near zero (per-tick samples are in-phase, only structural-event sites need correction).
- `PositionDebris lerpAlpha / ghostWorldBefore / worldStep / |worldStep| / predictedWorld / predictedVsActual` — reconstructs InterpolateAndPosition's lerp output, captures the per-frame world jump (the visible slide), and compares the actual ghost world position against a manual bracket-LLA lerp so playback-math bugs can be distinguished from recorder-side LLA errors.
- `FG_ApplyRel` / `BG_ApplyRel` (recording side) — for every Relative-frame sample, logs the focus and anchor world positions, the world delta, the computed anchor-local offset, and a pair of distances: `recordedRelativeDist = |offset|` (what's about to be persisted into `frames[]`) and `recordedAbsoluteDist = |focusWorldPos − anchorWorldPos|` (the ground-truth world-space distance at the instant of capture). The `distMismatch` field flags any difference — these must agree exactly under the v13 local-rotation contract.
- `PositionDebris parentGhostWorld / renderedParentDist / recordedAnchorLocalDist / interpolatedAnchorLocalDist / recordedBodyFixedDist` (playback side) — `renderedParentDist` is the on-screen parent-vs-debris distance (resolved via `GhostPlaybackEngine.TryGetGhostWorldByRecordingId(traj.DebrisParentRecordingId)`, backed by the new `GhostPlaybackState.recordingId` field). `recordedAnchorLocalDist` is the bracketing-BEFORE `frames[]` entry's anchor-local offset magnitude — stable across the entire bracket, so on a wide first bracket (e.g. the 600 ms seed→first-sample gap on fresh debris recordings) it does NOT track the recorded relative motion. `interpolatedAnchorLocalDist` is the magnitude of the offset VECTOR linearly interpolated between bracketing-before and bracketing-after `frames[]` entries at `playbackUT` (lerp the vector, then take magnitude), so it does evolve across the bracket; use this against `renderedParentDist` to ask "is the rendering tracking the recorded relative motion, or actually diverging from it?" Drift between rendered and seed-only can be real physical separation captured between samples; drift between rendered and INTERPOLATED is a rendering bug. `recordedBodyFixedDist` is computed independently by finding the parent's bracketing `bodyFixedFrames[]` sample (`RecordingStore.TryFindCommittedRecordingById`) and subtracting body-fixed primary world positions. These four together let a reader see whether playback faithfully reproduces what was recorded, or whether the two recording surfaces disagree internally.

**Next step (investigation):** enable `Settings → Diagnostics → Ghost render tracing`, fly a stage-separation in flight with watch-mode debris visible, then walk the resulting `[Trace-Sep]` log lines through these decision points:
1. At the `JointBreak` row, is `inFixed` `T` or `F`?
2. Does `|observedDelta|` match `predictedSrfStep`, `predictedInertialStep`, or neither?
3. At the `PartOriginSeed` row, what is `|observedDelta|` for the joint-child seed?
4. At consecutive `BuildTP` rows with `tickSinceBreak=0.something` then `tickSinceBreak=1.something`, does `|delta|` jump or stay flat?
5. At the first `PositionDebris` row (`first=True`), what is `|worldStep|`, and is `|predDelta|` ≈ 0 (math matches) or non-trivial (math diverges)?
6. At `BG_ApplyRel` / `FG_ApplyRel` rows during the window, is `distMismatch` ≈ 0 (recorder is self-consistent) or non-zero (rotation path adds scaling)?
7. At the first `PositionDebris` row, compare `renderedParentDist` to `interpolatedAnchorLocalDist` (not `recordedAnchorLocalDist`, which is the seed-only value and conflates real physical separation with rendering error inside a wide bracket): if `renderedParentDist ≈ interpolatedAnchorLocalDist ≈ recordedBodyFixedDist`, playback reproduces recorded data faithfully; if the two recorded distances agree but `renderedParentDist` diverges, that's a playback bug; if the two recorded distances disagree, the two recording surfaces store inconsistent parent-vs-debris geometry.

Based on the answers, the fix shape is one of: back-step only `part.transform.position`-using seed sites with `srf_velocity`; correct an upstream KSP timing assumption; address a playback-side anchor-vs-frame mismatch; or fix a recorder-side conversion that loses fidelity between the relative and body-fixed surfaces. Do not re-land any version of the reverted fix without a log bundle answering all seven questions.

---

## Done - v0.9.2 controlled-vessel ghost initial slide (rolled into PR 832 LLA fix)

- ~~Watch-mode playback of an Absolute-section non-debris controlled-vessel ghost (e.g. Kerbal X Probe in `logs/2026-05-10_1713`) showed a brief visible slide on the first frame after activation. The position was correct after the slide; the user-perceived issue was the visible transition.~~

**Resolution (PR 832 in PR 824 merge chain):** the controlled-vessel first-frame slide and the post-staging debris forward slide share a single root cause — `FlightRecorder.BuildTrajectoryPoint` was reading `vessel.latitude/longitude/altitude` directly, which lag the vessel's transform by exactly one PhysX tick for loaded/unpacked vessels. Every per-tick FG sample stored a position `~velocity * fixedDeltaTime` behind ground truth (~4.3 m at orbital-ascent speeds). For a controlled-vessel ghost the first activation frame happens to land on the joint-break-frame (fresh) sample while the next sample is fully stale, so the lerp between them moves the visible offset from 0 → ~4.3 m over the first ~0.5 s of playback — exactly the "slide into position" the user reported. The Phase 1 plan's working hypothesis (`InitialVisibleFrameClampWindowSeconds` shorter than `InitialActivationHiddenMinimumFrames` activation window) was wrong: the active controlled-probe fork in the retained `2123` bundle already activated cleanly with `hiddenPoseDelta=0.000` and `clampFired=false`, and the only structurally-large first-visible jump in that bundle was the parent `Kerbal X` activation coincident with `ReFlySettle FloatingOrigin.setOffset` — a separate origin-shift artifact, not an activation-clamp issue.

**Code path:** `FlightRecorder.BuildTrajectoryPoint` now derives lat/lon/alt from `body.GetLatitude/Longitude/Altitude(v.transform.position)`, matching the pattern already used at the joint-child seed path and in `BackgroundRecorder`. No activation-gate change was needed.

**Coverage:** see the post-staging debris forward slide Done entry above for the cross-channel evidence: BuildTP `|delta|` drops from ~4.3 m on every tick pre-fix to 9 mm (LLA↔world round-trip floor) post-fix; first-frame `renderedParentDist` matches `recordedAnchorLocalDist` to 1 mm. Phase 1 observability from the original investigation (`EmitActivationDecision`, `rawPlaybackUT`, `visibleLead`, `clampFired`, `hiddenPoseDelta`, `activation-transition` detailed window) was retained because it paid for itself in the PR 832 investigation and is the right tool for any future activation-window symptom.

**Stale artifacts:** `docs/dev/plans/fix-controlled-ghost-init-slide.md` (Phase 1 observability plan, shipped) and `docs/dev/plans/fix-controlled-ghost-slide-next.md` (PR 822 next-investigation plan, never merged) are obsolete for this bug. The proposed PR 822 fresh-repro investigation and PR 823 debris-relative validation pass are obsolete after the v13 debris-frame contract (PR 824) and the BuildTrajectoryPoint LLA fix (PR 832) landed together; both PRs were closed without merging.

**Status:** CLOSED 2026-05-13 in PR 832 (merged through PR 824 chain).

---

## Done - SegmentPhase saved value reflects start state, not end state

- Active unsplit tree leaves now persist a final endpoint `SegmentPhase`/`SegmentBodyName` instead of keeping the fork-start tag. Normal stop propagates the tagged `CaptureAtStop` phase into the active tree row using `tree.ActiveRecordingId` as the row proof (not `CaptureAtStop.RecordingId`, which is a fresh GUID). ForceStop/scene-exit finalization applies the endpoint phase after terminal orbit refresh and endpoint decision refresh, including records that already had `TerminalStateValue`. Committed chain segments and optimizer-owned non-active rows are preserved. RELATIVE sections are handled conservatively: section environment only applies when paired with terminal metadata or absolute-shadow endpoint evidence, and fallback never treats raw RELATIVE point latitude/longitude/altitude or stale start/body tags as real endpoint proof.
- **Investigation 2026-05-10:** confirmed as an actual persisted-state bug. `FlightRecorder.StopRecording()` builds `CaptureAtStop`; `ParsekFlight.StopRecording()` classifies the stop-time phase into that capture; `FlushRecorderToTreeRecording()` appends points/events/sections and start metadata but never copies `CaptureAtStop.SegmentPhase` or `SegmentBodyName` into the tree recording. The persisted field is what `RecordingTreeRecordCodec` writes and what the recordings table displays.
- **Runtime evidence:** `logs/2026-05-10_1713` recording `rec_b1566...` saved `terminalState = 0` (Orbiting) with `segmentPhase = atmo`. Its sidecar starts Atmospheric but ends in ExoBallistic/OrbitalCheckpoint sections with final `env = 2`, `ref = 2`, and `sma = 1186923...`. The optimizer detected the atmo->exo split but deferred it because this was the active Re-Fly recording, so optimizer splitting cannot be the only repair path.
- **Fix:** final/end tags now overwrite empty tags and Re-Fly fork-start tags only for the active unsplit tree leaf. Chain-boundary tags and optimizer split tags stay authoritative.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - dead-code SegmentPhase tag block in `ParsekFlight.StopRecording`

- **Investigation 2026-05-10:** `ParsekFlight.StopRecording` wrote the final phase tag to `recorder.CaptureAtStop.SegmentPhase`, not to the tree recording. Since `FlushRecorderToTreeRecording` did not propagate the field, this tag never landed on disk for tree-mode recordings.
- **Fix:** the block now uses the shared classifier and its `CaptureAtStop` tag is consumed by `FlushRecorderToTreeRecording` for the active tree row. Scene-exit paths still do not create `CaptureAtStop`; those are covered by finalization endpoint tagging.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - duplicated SegmentPhase classifier in three sites

- **Investigation 2026-05-10:** `ParsekFlight.TagSegmentPhaseIfMissing`, `ParsekFlight.StopRecording`, and `ChainSegmentManager.CommitVesselSwitchTermination` duplicated the same body/altitude/situation classification logic. Source review found no behavior drift, but the duplication was a cleanup-only drift hazard.
- **Fix:** `SegmentPhaseClassifier` now centralizes live-vessel classification and environment-to-phase mapping. `ParsekFlight.TagSegmentPhaseIfMissing`, the `StopRecording` final tag block, `ChainSegmentManager.CommitVesselSwitchTermination`, and optimizer section splits share that helper.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - debris relative-playback discontinuity under sparse anchor samples

- Same playtest, same log: `Kerbal X Debris` ghosts (`rec=3461390b…`, `311b452f…`, etc.) showed `dM=13.21 expectedDM=3.54` and similar 3-7× over-shoots between consecutive playback frames at the spawn window of the slot=1 Re-Fly. The recorded relative-frame samples around UT 31 have a ~2 s gap (UT 31.04 → 33.04) with a large local-offset change between adjacent samples; playback interpolation overshoots when the parent anchor (Kerbal X booster) is moving at ~150 m/s in the gap. This shows up visually as the user's "glitchy probe-booster ghost" complaint.
- **Fix:** Format v13 makes `bodyFixedFrames` the primary render surface for parent-anchored debris and treats anchor-local `frames` as the secondary/live-anchor path only for loop-anchored debris chains whose parent is itself in an active Relative section with covered parent frames. Flight, KSC, map-state-vector, tracking-station, standalone world-position lookup, and boundary-anchor consumers all fail closed on missing, stale, or unresolvable ordinary-debris body-fixed primary samples instead of clamping or replaying recorded Relative frames, and they log the deliberate recorded-relative suppression route. The recorder now uses parent-proximity tiers (full-rate at <=250 m, half-rate/Relative entry through <=500 m, Relative exit at >550 m), forces an immediate Relative-entry sample with a body-fixed peer, and playback no longer runs the old tumbling/gate router.

**Status:** DONE 2026-05-11 via `docs/dev/plans/debris-frame-contract-v13.md`.

---

## Done - debris ghost trajectories diverge during normal playback and Re-Fly cascades

- During ordinary Watch / table playback of a Kerbal X mission, the `Kerbal X Debris` rows render at "very, very inexact and wrong" world positions. Source: `logs/2026-05-06_2246_refly-vessel-spawn-debris-watch/KSP.log`. Background-recorded debris sections are saved as `referenceFrame=Relative` with sparse sampling — `[BgRecorder] TrackSection sparse sampling: pid=2236546571 env=Atmospheric ref=Relative frames=42 maxGap=1.640s threshold=0.50s largeGaps=2`, and `pid=3856523371 ... maxGap=1.846s largeGaps=5` — and they form a debris-anchored-on-debris chain (e.g. `RELATIVE mode entered: ... anchorRecordingId=ba1913864e3d4136a7970bcb14f6ccf0 ... source=Live diagnosticPid=2859430124`, which itself is anchored on `c67802c3...`). Each link in the chain is finalized at a different UT, so playback past the anchor's `endUT` produces `[WARN][RelativeAnchorResolver] relative-anchor-unresolved: reason=anchor-out-of-recorded-range recordingId=00964eb6... anchorRecordingId=00964eb6... ut=1228.43...` (more than 2000 suppressed). Recording `e13b6f3f` runs `[1228.4,1234.8]` while its declared anchor `00964eb6` ends at `1213.4` — the anchor is destroyed 15s before this child even starts, so the live anchor pose is unresolvable and the resolver falls through to the v7 absolute shadow: `[WARN][Playback] RELATIVE recorded-anchor fallback to absolute shadow: recording #9 "Kerbal X Debris" recordingId=e13b6f3f anchorRec=00964eb6... frames=26 sectionUT=[1228.4,1234.8]`. The shadow itself is sampled with the same sparse cadence, so the visible trajectory is whatever the shadow captured — coarse, drifty, and unrelated to where the debris would actually be.
- After a Re-Fly of the capsule, debris that the player vessel sheds during the Re-Fly attempt also renders at wrong positions. `BackgroundRecorder.TryGetBackgroundEligibleAnchorRecording` (`BackgroundRecorder.cs:3687-3693`) explicitly excludes `marker.ActiveReFlyRecordingId` from anchor selection: when the player's live vessel is the active Re-Fly, its recording is filtered out of the live-anchor candidate set. New debris born off that vessel must instead anchor on a still-loaded ghost candidate or fall through to Absolute. The ghost candidates' recorded world positions diverge from the player's live position by exactly the Re-Fly delta (the whole point of Re-Fly), so any debris the new run sheds is encoded in a Relative frame whose anchor is in the wrong place. On replay the new debris snaps onto the divergent ghost anchor, not to the player's actual breakup site.
- Follow-up session `logs/2026-05-06_2351_refly-phase-d-rewind-button-debris` confirms the defect is baked into recorded/merged trajectory data, not just a ghost mesh placement issue. The retained `KSP.log` contains 31 `MergeTree: boundary discontinuity` warnings, 49 `relative-anchor-unresolved` warnings, 21 `RELATIVE recorded-anchor fallback to absolute shadow` warnings, 12 `TrackSection sparse sampling` warnings, 14 forced Absolute transitions, 3 non-monotonic flush-stitch skips, and 89 sub-surface/finalizer warnings. At `23:47:47.856`, active playback switched a Relative anchor from probe `0cf6d9a1...` to ghost debris `c2c7d56a...` with `liveCandidates=0/0 ghostCandidates=4/4`, then recordings `0123b753...` and `0cf6d9a1...` immediately fell back to absolute shadow. At `23:48:04.224`, active Re-Fly relative samples logged offsets of `|offset|=2500.28m`, `1512.96m`, and `1728.92m`; a new Relative section closed with only 28 frames over ~21s and `maxGap=1.060s`; and `d3fa1e41...` produced `anchor-out-of-recorded-range` against ghost anchor `c73cca1b...` with `suppressed=1723`. The same window cascaded absolute-shadow fallback through debris recordings `c2c7d56a...`, `6213fe30...`, and `b2b5215a...`, then forced `c73cca1b...` to Absolute at UT `16519.71`.

**Diagnosis (symptom 1, common-case debris):** The debris-anchored-on-debris chain that `BackgroundRecorder.UpdateBackgroundAnchorDetection` builds (`BackgroundRecorder.cs:3441-3530`) is fragile under three compounding conditions native to atmospheric breakups: (a) anchor recordings are themselves short, fast-moving Background debris with sparse Atmospheric `ref=Relative` sampling (the warnings show `maxGap` up to 1.846s on 0.5s-threshold sections, see `[BgRecorder] TrackSection sparse sampling: ... maxGap=1.640s largeGaps=2`); (b) anchors finalize earlier than their dependents (e.g. `00964eb6` ends at UT 1213.4 but `e13b6f3f` starts at UT 1228.4 anchored on it); and (c) `TrajectoryTextSidecarCodec.cs:1575-1577` deliberately stops persisting `anchorPid` for `recordingFormatVersion >= RecordingAnchorChainFormatVersion (=11)`, so on reload the only anchor handle is `anchorRecordingId`, which dispatches through `RelativeAnchorResolver.TryResolveAnchorPose` (`RelativeAnchorResolver.cs:80-138`) and recursively walks the chain. Every chain hop multiplies the sampling-gap interpolation error and bottoms out on the unresolvable boundary, where `TryUseRelativeAbsoluteShadowFallback` (`ParsekFlight.cs:21852-21903`) saves rendering from full retirement but only by playing back the recorder's coarse absolute-shadow snapshot — it does not restore the resolution the user expects.

**Diagnosis (symptom 2, Re-Fly debris):** `BackgroundRecorder.TryGetBackgroundEligibleAnchorRecording` (`BackgroundRecorder.cs:3687-3693`) hard-excludes the active Re-Fly recording from anchor candidacy, presumably because playback of existing non-loop Relative data must not follow the diverged live vessel. In current Phase D code, that playback contract lives in `RelativeAnchorResolver.TryResolveActiveReFlyAnchorRecording` (`RelativeAnchorResolver.cs:943-974`) and `ParsekFlight.ShouldUsePreReFlyAnchorTrajectory` (`ParsekFlight.cs:20750-20774`): when an active Re-Fly recording is resolved as an anchor, playback uses the frozen pre-Re-Fly trajectory or falls back to recorded shadow data, not the live vessel. That contract is correct for *playback* of pre-existing relative recordings, but it is catastrophically wrong when reused as a *recording* filter for new debris created during the Re-Fly: the recorder still picks the nearest non-excluded anchor, which is some other ghost vessel candidate whose recorded coords are by definition the un-Re-Flown trajectory. The new debris is then encoded as `(dx,dy,dz)` in that wrong anchor frame, persisted as a v11 Relative section, and on playback rendered against that same recorded-but-displaced anchor. Both symptoms ultimately go through the same v11 chain-resolver and v7 shadow-fallback machinery, but symptom 2's data is poisoned at recording time while symptom 1's data is sound but exhausts its anchor span on playback.

**Additional evidence from `2026-05-06_2351`:** The sidecars and final save show the bad data persisted. `rec_0fd46f70...prec.txt` contains the active replacement `Kerbal X` recording with multiple Relative sections anchored to `0cf6d9a1...`, `c2c7d56a...`, and other debris/probe recordings. `d84e050b...prec.txt`, the new Re-Fly debris from branchpoint `ecb9b42...` at UT `16506.625`, starts Absolute at alt ~1297m, then switches into a Relative section `[16507.145,16509.965]` anchored to `0123b753...` with extreme oscillating local-offset payloads in the misleading v6/v11 `latitude/longitude/altitude` fields (`lat=93.47 lon=-134.53 alt=-115.5`, then `lat=11.73 lon=-117.08 alt=-29.1`, then `lat=193.02 lon=-139.89 alt=-22.83`). The merge pass later persisted boundary discontinuities of `105148.80m`, `406011.50m`, and `8147542.00m` for old `Kerbal X Debris`; up to `16479040.00m` for new Re-Fly debris `d84e050b...`; and up to `19299100.00m` for active replacement `rec_0fd46...`, with causes alternating between `sample-skip` and `frame-mismatch`. The final save had 10 committed recordings and 5 branchpoints, including supersede `e1ea034b... -> rec_0fd46...`, plus debris/probe recordings `c73cca1b...`, `d3fa1e41...`, `c2c7d56a...`, `6213fe30...`, `b2b5215a...`, `0123b753...`, and `d84e050b...`; this rules out a transient render-only state.

**Sub-surface / terminal-state evidence:** The same session repeatedly computed live-orbit fallback states deep under Kerbin (`alt=-599xxx`) for debris, then had the finalizer suppress or reject those states because nearby recorded surface points contradicted the fallback. Examples: `Start rejected: sub-surface state ... classifying recording as Destroyed`, `TryFinalizeRecording: suppressing sub-surface Destroyed ... because a nearby recorded surface point contradicts the live-orbit fallback`, and `FinalizerCache Apply rejected ... RejectedTerminalBeforeLastSample` for `c73cca1b...`, `d3fa1e41...`, and `d84e050b...`. One retained line shows `SnapshotPatchedConicChain: vessel=Kerbal X Debris solver unavailable | suppressed=48`; another shows `Apply rejected: consumer=EndDebrisRecording reason=RejectedTerminalBeforeLastSample rec=d84e050b... lastAuthoredUT=16525.562 terminal=Destroyed terminalUT=16517.137`. This likely shares root cause with bad debris trajectories: when the background recorder loses a reliable live/recorded anchor frame, the orbit/finalizer fallback reports impossible sub-surface state, and the terminal-state cleanup has to guess whether to trust the fallback or the last authored trajectory point.

**Fix:** The final v13 contract keeps the debris-parent id, but no longer depends on legacy compatibility gates. New v13 recordings always stamp the current format and any non-v13 recording/sidecar is rejected instead of partially loaded or migrated. Parent-anchored debris records a body-fixed primary surface and an anchor-local secondary surface; ordinary debris playback uses the body-fixed primary first across flight, KSC, Tracking Station, and map-state-vector paths, while loop-anchored debris chains try live relative playback only when the child frames and each parent link have active Relative coverage and otherwise fall back to body-fixed primary. Background debris enters Relative only while its parent is loaded/unpacked and within the parent-proximity band, exits through hysteresis beyond 550 m, and records at the proximity cadence needed for nearby debris, including an immediate Relative-entry sample with a body-fixed peer. The obsolete legacy shadow gate, tumbling-parent reliability gate, Re-Fly post-load debris settle suppression, and v11/v12 migration tests were removed.

**Status:** DONE 2026-05-11 via `docs/dev/plans/debris-frame-contract-v13.md`. Remaining sub-surface finalizer polish from the old work queue is not part of the debris frame contract and should be tracked separately if it reproduces after v13 recordings.

---

## In Progress - reset recording/rendering schema versions to v0 and delete pre-release compatibility

- After the ghost rendering / Re-Fly Phase D cleanup lands, reset Parsek's recording and rendering sidecar version baselines to zero. We have no public users yet, so do not preserve the old v1-v11 compatibility ladder or spend effort migrating older saves. The goal is a cleaner codebase where "v0" means the current post-redesign recording contract, not the historic pre-v6 legacy format.

**Current implementation pass:** Branch `reset-recorder-renderer-v0` now sets `RecordingStore.CurrentRecordingFormatVersion = 0` and `CurrentRecordingSchemaGeneration = 1`, removes the historical named feature-version constants from production, changes trajectory magic to `PSK0`, snapshot magic to `PSN0`, pannotations magic to `PNA0`/`PNC0`, resets tree/snapshot/pannotations/ledger versions to 0, and keeps the mod at v0.9.2. Loaders reject pre-reset sidecars/recordings by magic or generation rather than migrating them; tree load drops non-synthetic recordings whose sidecar hydration fails. Saves also verify that existing sidecars are current before writing v0 tree metadata, rewriting stale/missing files first and skipping unsafe tree serialization if a rewrite cannot produce current sidecars. Remaining work is mainly fixture/test regeneration, wider `.sfs` ScenarioModule schema stamping, and runtime validation.

**Implementation intent:** Collapse the current full schema to v0 for new saves and sidecars. Remove or rewrite version branches whose only purpose is to support old internal saves: pre-v4 loop-interval migration, v5 predicted-orbit compatibility, pre-v6 Relative lat/lon/alt interpretation, v7 body-fixed primary history, v8 boundary-seam gates, v9 terrain-ground-clearance defaulting, v10 structural-event defaulting, v11 anchor-chain gates, v12 debris-parent gates, and v13 debris-frame gates. Prefer strict rejection or discard of older Parsek recording files with a clear WARN/UI message over best-effort migration. Keep feature flags or named constants only when they describe code behavior, not save compatibility history.

**Files / areas to audit:** `RecordingStore.cs`, `RecordingSidecarStore.cs`, `TrajectorySidecarBinary.cs`, `TrajectoryTextSidecarCodec.cs`, `RecordingTreeRecordCodec.cs`, `ParsekScenario.cs`, `FlightRecorder.cs`, `BackgroundRecorder.cs`, `ParsekFlight.cs`, `GhostMapPresence.cs`, `ParsekKSC.cs`, `ProductionAnchorWorldFrameResolver.cs`, `GhostPlaybackEngine.cs`, and rendering sidecars such as `PannotationsSidecarBinary.cs` / smoothing/co-bubble caches that embed `sourceRecordingFormatVersion`. Delete or update tests whose only value was old-version compatibility (`FormatVersionTests`, binary/text sidecar legacy round trips, loop migration tests, old Relative contract tests) and replace them with tests that pin the new v0 full contract plus strict refusal/discard of pre-reset files.

**Injector / showcase work:** `RecordingBuilder`, `RecordingStorageFixtures`, `ScenarioWriter`, and the synthetic/in-game rendering fixtures now stamp the current v0/generation-1 recording contract instead of historical format literals. `SyntheticRecordingTests.InjectAllRecordings` refuses to import the frozen `DefaultCareer` corpus when its metadata or `.prec` sidecars are pre-reset, so the old Learstar fixture is explicitly excluded until `Source/Parsek.Tests/Fixtures/DefaultCareer/` is rebaked to `recordingFormatVersion = 0`, `recordingSchemaGeneration = 1`, and `PSK0`/BinaryV0 sidecars. Run `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter InjectAllRecordings` after the local .NET Framework targeting pack issue is fixed and KSP is closed or `KSPDIR` points at an isolated install, then run the relevant in-game showcase / ghost playback tests.

**Acceptance gates:** New recordings, tree metadata, text `.prec`, binary `.prec`, pannotations/co-bubble smoothing sidecars, synthetic fixtures, and injected showcase recordings all report version `0`. Grep should show no raw historical version constants `4` through `11` used as recording-format gates, no legacy loop/predicted/relative migration helpers, no acceptable sidecar-version lag path, and no read-side silent drop for old pre-Re-Fly payloads such as `PRE_REFLY_ORIGINAL`. Loading old Parsek recordings should produce an explicit refusal/discard path rather than a partial migration. Documentation in `.claude/CLAUDE.md`, `AGENTS.md` if needed, and relevant design docs should say v0 is the post-reset baseline.

**Status:** IN PROGRESS 2026-05-11. Production build is green; the stale-version grep is clean outside the intentionally excluded `DefaultCareer` fixture; full xUnit is blocked locally by missing .NET Framework 4.7.2 reference assemblies; fixture rebake and runtime validation remain.

**P2 follow-up (2026-05-13):** review caught that `AreRecordingFilesCurrentForSave` certified header-only sidecars as safe — a `.prec` truncated past its header or a `.craft` with a valid header but bad payload checksum passed the probe and the next load would drop the recording via SidecarLoadFailed. The save gate now runs full-payload validation (trajectory: scratch read into a throwaway `Recording`; snapshot: existing `TryLoad` which decompresses + verifies CRC32 in `SnapshotSidecarCodec.cs:180`). Failure surfaces as `trajectory-payload-invalid` / `snapshot-{label}-payload-invalid` so `ParsekScenario.EnsureRecordingFilesCurrentForSave` rewrites from the in-memory rec instead. Covered by `SaveGateDeepValidationTests`.

---

## Active - v0.9.2 Re-Fly cleanup and v0 reset

- After PR #708 merges, continue from `docs/dev/plans/ghost-anchor-recording-chain-plan.md` rather than adding more stabilization into the PR708 branch. PR708's merge scope is Phases A-C plus playtest hardening: v11 `TrackSection.anchorRecordingId`, recorder-side recording-id anchor selection, non-loop Relative playback through `RelativeAnchorResolver`, frozen/body-fixed Re-Fly display alignment, Watch activation/tail/LOD stabilization, and the follow-up fixes documented in `docs/dev/plans/pr708-playtest-followup-plan.md`. Final PR708 validation evidence is `logs/2026-05-03_2007_pr708-final-watch-good`: KSP log validation passed, no Parsek errors or exception signatures were found, Watch activation gates hid the bad Probe/debris primer frames, renderer LOD hysteresis stopped the 2300m flicker, the final save contains the expected `RECORDING_TREE`, and focused/broad non-live xUnit passed (`239/239`, `10670/10670`).

**D.0 decision:** active Re-Fly ghosts must detach from the live vessel and render only at original recorded coordinates during divergent Re-Fly. Divergence is a product signal, not something the renderer should hide by translating old ghosts toward the live attempt.

**D.1 implementation:** remove the frozen body-fixed display-alignment cache and consumers (`ReFlyDisplayAlignment`, `TryGetReFlyTreeAnchorOffset`, ghost `reFlyTreeOffset`, root-part pinning, active Re-Fly render interpolation, and point-trend smoothing). Recorded-coordinate playback now feeds ghost placement directly; the separate D.5 pass removed the temporary Re-Fly activation gate while leaving the generic fresh-spawn playback-sync defer path intact.

**D.2 implementation:** remove the stale-anchor/no-live-anchor absolute-shadow fallback branches and the active-Re-Fly live-anchor bypass selector. Loop Relative playback now uses its explicit live anchor or retires; non-loop Relative playback continues through the recording-id resolver and recorded-coordinate fallback path.

**D.3 implementation:** remove the RELATIVE absolute-shadow forward-bridge fallback (`TryFindAbsoluteShadowForwardBridgeFrame`) and its adjacent-section append path. Sparse RELATIVE sections no longer borrow future absolute/shadow frames; playback stays section-local and lets the recorded-coordinate resolver contract decide the visible pose or retirement.

**D.4 implementation:** remove the non-loop live-PID anchor contract from flight playback. `IGhostPositioner.TryGetLiveAnchorWorldPosition`, `GhostPlaybackEngine.DescribeAppearanceLiveAnchorContext`, legacy anchor-PID appearance/watch logs, and recorded-Relative trace emissions that echoed `TrackSection.anchorVesselId` are gone. Non-loop Relative diagnostics now report `anchorRecordingId` or `anchorRec=missing`; loop Relative playback keeps its explicit live-anchor PID contract.

**D.5 implementation:** remove the Re-Fly external activation-defer gate (`GhostPlaybackState.externalActivationDeferred`, `RefreshReFlyAnchorActivationGate`, `ShouldRaiseExternalActivationGate`, `ReFlyActivationGate` trace/log phases) and the orphaned Re-Fly anchor-sampling helpers that only fed that gate. The engine still keeps `deferVisibilityUntilPlaybackSync` for fresh/rebuilt ghost first-frame synchronization.

**D.6 implementation:** no production change required. `ProductionAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos` was already clean after Phase C; the remaining live-PID resolver is loop-only (`TryResolveLoopAnchorWorldPos`) by design.

**D.7 implementation:** fence KSC and map Relative playback away from live vessel PID lookups. `ParsekKSC` now resolves Relative playback poses through recorded anchor IDs, and `GhostMapPresence` state-vector Relative branches use `RecordedRelativeAnchorPoseResolver` instead of `FlightRecorder.FindVesselByPid`, `ResolveAnchorInScene`, `AnchorResolvableForTesting`, or `TryResolveActiveReFlyAbsoluteShadowPoint`. The create-time active-Re-Fly lookahead is now a recorded-anchor-chain no-op instead of a live-PID suppression scan.

**Grep-audit guard:** `scripts/grep-audit-non-loop-live-pid.ps1` plus `GrepAuditNonLoopLivePidTests` enforce the deleted non-loop live-PID surfaces. `Rendering.NonLoopLivePidGuard` also exposes a DEBUG-only regression counter for future live-PID lookup attempts in non-loop Relative paths.

**Carry-forward validation:** keep the PR708 final bundle as the baseline and consider one targeted map/tracking terminal-spawn smoke if later Phase D work depends on terminal handoff behaviour. Do not treat pre-v11 recordings as correctness fixtures; regenerate any runnable regression fixture under v11 with real `anchorRecordingId` chains. Keep the transient pre-merge-dialog stranded-sidecar save warning as a separate follow-up, not a PR708 merge blocker, unless new evidence shows retained save corruption.

**Branch B (v0 format reset, now in progress above):** plan doc `docs/dev/plans/refly-cleanup-and-v0-reset.md` §3 / §4 Branch B. Reset `CurrentRecordingFormatVersion` from 13 to 0 with a discriminator that makes pre-reset saves unloadable, drop the v4-v13 reader code path, delete `TrackSection.anchorVesselId` if no longer needed after loop-anchor follow-up, keep the mod at v0.9.2. All existing playtest saves under `Kerbal Space Program/saves/` become unloadable; acceptable per the user sign-off in plan §3.5 ("no career save needs preservation"). UX on load: one-time warn log per unsupported recording, recordings-table empty state, orphan sidecars left on disk, no partial-load recovery.

*Strictly required by the reset:*

- Eight-axis version reset together: trajectory data (`.prec` binary + `.prec.txt` text), recording-tree topology (`RecordingTree.CurrentTreeFormatVersion`), vessel/ghost snapshots (`SnapshotSidecarCodec.CurrentFormatVersion`), pannotations (`PannotationsBinaryVersion` / `AlgorithmStampVersion` / `CanonicalEncoderVersion`), career ledger (`Ledger.CurrentLedgerVersion`), `ReFlySessionMarker` schema (implicit; field-presence-defined), other ScenarioModule `.sfs` data (plan §3.10). The named feature constants `LaunchToLaunchLoopIntervalFormatVersion` ... `RecordingAnchorChainFormatVersion` (`RecordingStore.cs:57-65`) collapse to a single `CurrentRecordingFormatVersion = 0`.
- Discriminator (two layers, both required because some paths are binary-only and some are `.sfs`-embedded text). Layer 1: binary magic prefix change — `PRKB` → new tag (suggested `PSK0`) for `.prec`; `PANN`/`PANC` and `PRKS` get parallel new tags. Layer 2: new `RecordingSchemaGeneration = 1` field stamped at write time with **strict equality** read gate; reject reasons distinguished in the warn log: `magic-mismatch`, `generation-missing`, `generation-older`, `generation-newer`, `format-version-mismatch`. Strict equality (not `>=`) because future resets bump only the generation, so a `>=` reader would let a future-generation save silently load on an older binary.
- Delete the v4-v11 binary write/read ladder, the `formatVersion >= N` gates throughout the codebase, the legacy `.prec.txt` load path (text codec survives only as debug-mirror writer gated by an existing diagnostics setting). See plan §3.3 for the verified gate inventory across `TrajectorySidecarBinary.cs`, `TrajectoryTextSidecarCodec.cs`, `RecordingStore.cs`, `RecordingSidecarStore.cs`, `RelativeAnchorResolver.cs`, `FlightRecorder.cs`, `ParsekFlight.cs`, `GhostPlaybackEngine.cs`, `PannotationsSidecarBinary.cs`, `SnapshotSidecarCodec.cs`, `Ledger.cs`.
- Schema refusal at every load entry point: both `LoadRecordingTrees` (committed trees) and `TryRestoreActiveTreeNode` (active trees) apply the same `IsSchemaCompatible` predicate before `AddCommittedInternal` / pending-tree stash. Drop empty trees, drop trees whose `RootRecordingId` is rejected, clear `tree.ActiveRecordingId` when it points at a rejected recording, drop `BranchPoint`/`SupersedeRelation` rows referencing rejected recordings, clear `pendingActiveTreeResumeRewindSave` (`ParsekScenario.cs:4674` declaration; assigned at `:3145`) and call `ClearPendingQuickloadResumeContext()` on active-tree refusal. Sidecar files stay on disk (no auto-delete).
- Test fixture regeneration: every checked-in `.sfs` fixture under `Source/Parsek.Tests/Fixtures/` re-baked at v0; `RecordingBuilder` / `ScenarioWriter` / `VesselSnapshotBuilder` defaults flip to v0 + generation 1; `LegacyTreeMigrationTests.cs` and `RecordingBuilderV6Tests.cs` deleted; `FormatVersionTests.cs` rewritten as discriminator-refusal tests. Loader-refusal tests with three explicit cases: legacy v11 binary (`magic-mismatch`), legacy default-0 record with no generation field (`generation-missing`), synthetic future-generation save (`generation-newer`).
- `.sfs` schema audit: stamp `RecordingSchemaGeneration` on every ScenarioModule write that needs to round-trip — `ParsekScenario.OnSave`, `ReFlySessionMarker.SaveInto`, `MergeJournal.OnSave`, `CrewReservationManager`, `GroupHierarchyStore`, `RecordingGroupStore`, `RewindInvoker` RP metadata. Reject on read where the stamp is missing or `!= CurrentSchemaGeneration`. No "default to current and stamp on next write" silent migration — that defeats strict equality.

*Bundled with Branch B (convenience, not strictly the reset):*

- Delete `TrackSection.anchorVesselId` field (`TrackSection.cs:56`). Phase D made it unused in production, but the field can only be removed when the serialized format version is changing.
- Delete `LegacyMergeStateMigrationCount`, `EmitLegacyMergeStateMigrationLogOnce`, `BumpLegacyMergeStateMigrationCounterForTesting`, `ResetLegacyMergeStateMigrationForTesting` (committed-bool tri-state migration helpers in `RecordingStore.cs:135-164`); `LegacyGloopsGroupName` (group rename migration at `RecordingStore.cs:78`); `LegacyPrefix` (log compatibility at `RecordingStore.cs:194-202`). Pre-existing one-shot migrations from older save shapes — piggybacking because the migration targets are dead.
- Delete the `RecordingTreeRecordCodec` PRE_REFLY_ORIGINAL silent-drop read tolerance (the comment-only write side at `:315` already removed in PR #751; the read tolerance becomes unreachable once loader refusal lands).
- Mod version stays at v0.9.2 — both `Parsek.version` and `AssemblyInfo.cs` (`scripts/release.py` validates they match).
- Branch A's deferred scenario assertion: once v0 fixtures exist, add watch + Re-Fly playback coverage that asserts `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` after playback completes (Branch A only ships the unit test for the guard's reset/count semantics; the runtime safety net needs the scenario fixtures Branch B creates).

*Commit shape (plan §4):*

1. Write/read gate audit — document every `>= N` gate per the §3.3 inventory, decide its fate (collapse to unconditional vs delete), no value flips yet.
2. Introduce binary magic prefix + `RecordingSchemaGeneration` field stamped on writes only; readers still accept legacy. Either Option A (promote probe data to persisted fields on `Recording`: `RecordingSchemaGenerationLoaded`, `LoadedMagicTag`, `LoadResultSchemaCompatible`) or Option B (`LoadRecordingFiles` returns a `LoadRecordingResult` struct). Pick during commit 2.
3. The actual flip: `CurrentRecordingFormatVersion = 0`, all other version constants reset per plan §3.6, legacy readers deleted, `anchorVesselId` deleted, fixtures regenerated, in-game test version literals updated, migration helpers deleted, version bump.
4. `.sfs` schema audit pass.

*Acceptance:* `dotnet test` (full headless) green against regenerated fixtures; `dotnet test --filter InjectAllRecordings` green against re-baked synthetic recordings; in-game smoke on a fresh v0 save (Watch + active Re-Fly + map view + KSC ghost view) with no `[ERROR]` lines in `KSP.log`; loader-refusal tests pass against pre-reset legacy fixtures (3 cases above); `scripts/grep-audit-non-loop-live-pid.ps1` and `scripts/grep-audit-ers-els.ps1` green; Branch B grep gate — after commit 3, `RecordingFormatVersion\s*=\s*\d+` / `formatVersion\s*=\s*\d+` / `binaryVersion\s*=\s*\d+` / `PeerSourceFormatVersion\s*=\s*\d+` literals other than 0 must be zero outside negative-test cases.

*Rollback:* tag `pre-v0-reset` on the parent commit before merging Branch B. A revert of the Branch B merge is the right shape; legacy reader deletions are too broad to forward-fix on top of v0. Document tag name and revert recipe in the Branch B PR description.

*Out of scope (Branch C or never):* the old `absoluteFrames` compatibility story has been superseded by the v13 `bodyFixedFrames` primary surface and strict pre-v13 refusal. Branch B should collapse the remaining version history into v0 rather than carrying a separate Branch C shadow-data deletion. Loop-anchored recordings still keep `LoopAnchorVesselId` live-vessel anchoring; switching that to recording-id is a separate plan. Phase F promote-to-absolute permanently deferred per `ghost-anchor-recording-chain-plan.md` §9.3.

*Documentation updates Branch B owns (same-commit):* `CHANGELOG.md` entry under v0.9.2 with a public-history note that the recording format renumbers from v11 to v0 while the mod version stays at v0.9.2; `.claude/CLAUDE.md` and `AGENTS.md` "Recording storage" gotcha blocks rewritten to v0 (remove the v6/v7/v10/v11 enum constants section); `MEMORY.md` refresh `project_format_v0_reset.md` pointer plus new `project_post_v0_reset_arc.md` entry pointing to the plan.

**Status:** Phase D implementation is complete on `refly-phase-d`; focused xUnit, broad non-injection xUnit, the ERS/ELS grep audit, and the non-loop live-PID grep audit are green. Full xUnit currently reaches the `InjectAllRecordings` test and is blocked locally because the running KSP instance holds `KSP.log`; optional in-game smoke remains the final runtime validation step before merge. Branch B (v0 format reset) is the next deliverable; pick up from a fresh worktree off `origin/main` once Branch A merges.

---

## TODO - STASH auto-seal persisted reason metadata

**Status:** TODO - deferred schema follow-up from PR #696 review.

`ChildSlot.Sealed` / `SealedRealTime` intentionally stay schema-minimal in the STASH safety PR, and the runtime INFO log distinguishes player Seal from system auto-seal with `reason=<closeReason>`. The persisted slot does not yet retain `SealedBy` / `SealedReason`, so a future Timeline or Recordings-table explanation UI would need to reconstruct the reason from logs. Add explicit persisted metadata before building any in-game "why was this sealed?" affordance.

---

## 640. Stock committed-future overlay v2 follow-ups

**Status:** TODO - future investigation / review item from PR #721.

PR #721 ships the v1 scope: stock R&D, Astronaut Complex, and Mission
Control committed-future overlays, plus click-blocks for duplicated tech,
contract accept, kerbal hire, and facility upgrade actions. The following
ideas are deliberately out of v1 scope and should be reviewed as separate
follow-ups after in-game verification:

- KSC facility-upgrade visual overlays in the top-down KSC view. The
  click-block already exists via `FacilityUpgradePatch`; v2 would add the
  visual badge and extend the overlay/click-block invariant to facilities.
- Future-completed / future-failed contract badges in Mission Control, not
  only future-accepted contract badges.
- Administration strategy activation overlays, paired with matching
  click-block behavior if the stock UI has a clickable affordance.
- Per-row claim / override UI for cases where the player intentionally wants
  to bypass a committed-future action, instead of using the global setting.
- Per-user dismissible badges for "hide this warning until next session" style
  workflows.
- Non-stock screen integrations, such as Contract Configurator's own Mission
  Control replacement or other mod-provided building screens.
- Modded flight-scene building overlays. The current v1 overlays are
  `SPACECENTER` scene-bound, while the lower-level click-blocks remain
  scene-agnostic.
- Tooltip styling polish using KSP's richer
  `KSP.UI.TooltipTypes.TooltipController_Text` path instead of the v1
  `GUI.skin.box` fallback.

**Review guidance:** keep the v1 invariant intact for every clickable action:
if a stock or modded UI exposes a clickable affordance, the overlay candidate
set and the click-block predicate must share the same `MilestoneStore` source
helper, with any UI-only suppression kept outside the click-block predicate.

## Phase 5 known gaps (deferred to later phases)

- The Phase 5 commit-time detector runs against `RecordingStore.CommittedRecordings` only — recordings persisted as part of the same commit batch but not yet appended to the live store at the time of `PersistAfterCommit` are added to the snapshot list explicitly. Multi-recording commit batches that span more than one persistence call still rely on the next `PersistAfterCommit` (or load-time lazy recompute, both of which now also persist peer-side `.pann` files symmetrically per review-pass-3 P3-1) to populate the missing-side trace.
- The `CoBubbleBlender` evaluates the offset against the primary's RECORDING for HR-15 compliance; if both the primary and peer have splines fitted, the peer's render aligns to the primary's smoothed position. If the primary's spline is missing (e.g. a section that never qualified for fit), the blender still returns the recorded offset against the primary's raw lerp. Visual residual under that condition is bounded by the primary's standalone fidelity.
- §7.7 BubbleEntry / BubbleExit and §7.9 SurfaceContinuous remain Phase 7 territory. Phase 5 did not promote either: BubbleEntry/Exit needs a session-time physics-active timeline scanner; SurfaceContinuous needs the Phase 7 per-frame terrain raycast.

## Phase 6 known gaps (deferred to later phases)

- ~~§7.7 BubbleEntry / BubbleExit candidates are not emitted by the Phase 6 builder.~~ Shipped: `AnchorCandidateBuilder.EmitBubbleEntryExitCandidates` walks adjacent `TrackSection` pairs and emits at every `Active|Background ↔ Checkpoint` source-class transition; `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` reads the LAST/FIRST physics-active sample as the high-fidelity world reference. Mainline shipped this at `AlgorithmStampVersion=5`; on the Phase 5 stack it lands inside the v8 alg-stamp window. Residual gap: RELATIVE-frame physics-active sections adjacent to a Checkpoint segment are deferred with a `bubble-entry-exit-relative-section-deferred` Verbose (uncommon in practice — vessel docked to its anchor while a Checkpoint splices in).
- ~~§7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates — Phase 5 territory.~~ Phase 5 ships a separate co-bubble offset trace pipeline (`.pann CoBubbleOffsetTraces` block + `CoBubbleBlender`); the `AnchorSource.CoBubblePeer` enum slot stays reserved for any future anchor-based co-bubble pathway but is no longer the active mechanism.
- The 2.5 km bubble-radius HR-9 Warn (`RenderSessionState.cs:836-848`) only fires from the LiveSeparation path inside `RebuildFromMarker`. Anchors written via `AnchorPropagator.TryWriteAnchor → PutAnchorWithPriority` (§7.4 / §7.5 / §7.6 / §7.7 / §7.10) skip the magnitude check, so a non-LiveSeparation ε of, say, 12 km lands silently. Lift the magnitude check into `PutAnchorWithPriority` (or the per-source dispatch) in a follow-up PR so all anchor types are uniformly guarded — pre-existing gap, not introduced by §7.7.
- §7.9 SurfaceContinuous emits a marker only with ε = 0; the per-frame terrain raycast that resolves ε is Phase 7 work. Phase 6 demoted the rank from 2 to 6 to prevent the zero stub from winning ties against real OrbitalCheckpoint ε; Phase 7 must promote back to rank 2 once the resolver ships and bump `AlgorithmStampVersion` so existing `.pann` re-resolve.
- The split anchor sources (Undock / EVA / JointBreak) currently share the `DockOrMerge` enum byte (priority rank 4 either way). Logs label them by `BranchPointType` rather than by enum value to preserve telemetry granularity. If a future phase needs to differentiate split priorities from dock priorities, expand the `AnchorSource` enum and bump `AlgorithmStampVersion`.

---

## Observability Audit - 2026-04-26

Full report: `docs/dev/observability-audit-2026-04-26.md`.
Implementation plan: `docs/dev/plan-observability-logging-visibility.md`.

Open implementation follow-up: make Parsek's runtime decisions reconstructable
from `KSP.log` without reintroducing per-frame spam. The audit prioritizes:

- P1 current spam hygiene: finalizer-cache summaries, patched-snapshot /
  extrapolator repeats, current map/proto-vessel/tracking-station repeaters,
  diagnostics sidecar warnings, ledger no-op summaries, sandbox patch skips,
  and KSC playback spam fixes.
- P2 ~~flight ghost skip reasons, playback frame skip summaries~~, rewind
  `CanInvoke` reason logging, sidecar/path severity and context, duplicate
  `OnLoad` timing cleanup, post-switch auto-record no-trigger summaries,
  background recorder drift warnings, game-action skip summaries, and ~~UI/map
  marker skip summaries for ghost/proto-vessel map presence and watch focus~~.
- P3 shared rate-limit key cleanup, repeated-warning rate limits, noisy resource
  event aggregation, production warning-prefix cleanup, and low-risk
  cleanup/reflection summaries.

Phase 0 guardrails started on `observability/guardrails`: retained-log signal
analysis, stricter post-hoc log validation, and guaranteed validation artifacts
from `collect-logs.py`.

2026-04-26 Phase 1 update: the current retained-log hygiene slice is closed for
the finalization/map signal called out in
`logs/2026-04-26_0118_refly-postfix-still-broken`. The fix keys
`FinalizerCache refresh summary` by owner/recording/terminal state, rate-limits
stable no-delta and repeated classification summaries, collapses the
patched-snapshot missing-body / captured and extrapolator seeded-OFR repeaters
with `VerboseOnChange`, rate-limits empty GhostMap cleanup, gates map-visible
window diagnostics on source/window changes, and folds the Task 1.5 ledger /
sandbox-patcher repeaters into state-change gated summaries. Focused xUnit log
assertions pin each gate. The broader observability audit remains open for later
missing-decision logs and save/load context work.

Status update (`observability/playback-visibility`): closed the Phase 2 flight
playback visibility slice for ghost skip reasons, on-change skip logging, engine
aggregate skip counters, fast-forward watch handoff reasons, and watch-camera
infrastructure failures. The branch also added map-view/proto-vessel visibility
reasoning for missing map objects, orbit renderers, draw-icon state, native-icon
suppression, renderer force-enable, and watched-ghost map-focus restore blockers.
Review follow-up: map-focus restore logging now uses one stable on-change
identity with the watched recording/pid/reason in the state key, avoiding
per-recording cache growth while preserving reason-change visibility.
Review follow-up: Flight scene teardown and `DestroyAllTimelineGhosts` now clear
ghost-skip reason state and the matching `Flight|ghost-skip|` `VerboseOnChange`
identities, with coverage showing per-recording skip reasons re-emit after
scene cleanup and rewind/timeline destruction.
Remaining observability audit items stay open.

Phase 3 persistence/rewind observability is closed on
`observability/persistence-rewind` (2026-04-26): `OnSave` / `OnLoad` now carry
top-level exception context and single phase/status timing; recording sidecar,
snapshot-probe, path-resolution, and transient cleanup failures now surface
Warn/Error context with recording id, save folder, epoch, ghost snapshot mode,
file kind, paths, staged-file count, and exception details; Rewind/Re-Fly
`CanInvoke` plus disabled slot decisions now log only on reason changes. This
closes the audit follow-up for duplicate/miscounted `OnLoad` timing, sidecar/path
failure severity/context, and rewind precondition reason visibility. Remaining
observability-audit work stays in the non-persistence phases: KSC/playback spam
hygiene, ghost skip summaries, recorder/auto-record decision logs, game-action
aggregation, and map/UI/test-runner visibility.

Review follow-up: legacy text snapshot parse exceptions again flow to the
outer `exception:<Type>` sidecar failure path; resolve-only path lookups now log
missing save context at Verbose while directory-creation entry points keep Warn;
and Rewind/Re-Fly slot `VerboseOnChange` identities are cleared when RP state is
loaded, closed, reaped, discarded, or rolled back.

Runtime-gaps branch progress (2026-04-26): Phase 4/5 recorder and
game-visible runtime decisions are now covered for the high-priority gaps:
background recorder attach/clear and drift warnings, active-to-background
missing-vessel/finalizer diagnostics, post-switch auto-record no-trigger and
manifest-delta summaries, EVA/boarding split skips, ParsekUI map-marker skip
summaries, Tracking Station atmospheric-marker skip summaries, ghost orbit-line
suppression decisions, game-action converter skip-by-type summaries, event
reject logs, kerbal recalculation counters, Real Spawn Control auto-close
reasons, and test-runner scene-eligibility skip aggregation.
Review follow-up: post-switch manifest logging preserves trigger-priority
short-circuiting, marking lower-priority delta families as `skipped` instead of
diffing every manifest category on each 0.25s evaluation tick; the background
state-drift throttle now has a backwards-UT rollback test.

Remaining observability follow-up after runtime-gaps: the earlier P1/P2
save/load exception context, sidecar/path severity expansion, rewind
`CanInvoke` reason-change logging, playback-engine frame skip counters, and
Phase 6 retained in-game log-package validation still need separate passes.

Review follow-up coverage (2026-04-26): closed the deferred log-assertion gaps
for finalizer refresh identity isolation, Diagnostics missing-sidecar path
warning scopes, `ComputePlaybackFlags` ghost-skip emit/suppress behavior,
`OnSave` exception context/RecState, and unsupported snapshot probe logging.

Post-merge spam fix (2026-04-26, `fix/rewindui-canInvokeSlot-spam`): the
2026-04-26_1025 playtest log showed 1389 identical `[RewindUI] CanInvokeSlot:
slot-ok` lines in 6 seconds for a single rp/slot — the existing
`ParsekLog.VerboseOnChange` gate did not suppress the repeats from the OnGUI
draw loop, while the matching `[Rewind] CanInvoke:` site (same code path,
same dictionary) suppressed correctly. The xUnit 200-call repro passes, so
the failure is Unity-runtime-specific. `LogRewindSlotCanInvokeDecision` now
tracks the last-emitted decision stateKey in a file-local
`Dictionary<string,string>` and only calls `ParsekLog.Verbose` when it
changes — mirroring the `lastCanInvoke` pattern already used by
`DrawUnfinishedFlightRewindButton` ~300 lines above. Existing
`ClearRewindSlotCanInvokeLogState` callers (LoadTimeSweep, RewindPointAuthor,
RewindPointReaper, TreeDiscardPurge, ParsekScenario.OnLoad) clear the new
dict alongside the original `ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix`
call. Review follow-up: removed the per-OnGUI-pass clear that
`RecordingsTableUI.DrawIfOpen` was firing while the Recordings window was
closed — it wiped the cache before TimelineWindowUI's Fly button could
reuse it, re-spamming `slot-ok` whenever Timeline was open without
Recordings. Regression tests:
`RewindSlotCanInvoke_ManyConsecutiveCalls_EmitsOnceForStableSlotOk` drives
200 calls and asserts a single emit;
`RewindSlotCanInvoke_TimelineOnlyCalls_DoNotRespamAfterRecordingsClose`
drives 200 Timeline-style calls after a single close-transition clear and
asserts only 2 emits total.

---

# Known Bugs

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**2026-04-19 boundary note:** `GhostPlaybackEngine.ResolveGhostActivationStartUT` no longer casts back to `Recording`; the engine now resolves activation start from playable payload bounds through `PlaybackTrajectoryBoundsResolver` over `IPlaybackTrajectory`. #435 remains otherwise unchanged, but this leak is no longer part of the extraction risk surface.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Partial mitigation:** PR #721 adds stock R&D / Astronaut Complex / Mission Control row badges with tooltips for committed-future actions, including the event UT and source recording when available. This helps before the click, but does not replace the structured blocked-action dialog below: the dialog still needs conflict context, Timeline navigation, and the rewind shortcut.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, loop toggle semantics on entry rows, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

2026-04-25 update: deferred spawn queue outside-physics-bubble waits are no longer
a spam source; the per-recording kept line and repeated warp-ended summary were
replaced with a rate-limited queue wait summary.

2026-04-25 update (UnfinishedFlights + missed-vessel-switch):
`logs/2026-04-25_1314_marker-validator-fix/KSP.log` was 96 MB / 540k lines, of
which ~511k (94%) were `[Parsek][VERBOSE][UnfinishedFlights]
IsUnfinishedFlight=…` decisions and ~1k were `[Parsek][WARN][Flight] Update:
recovering missed vessel switch` lines. Both fired from per-frame paths:
`EffectiveState.IsUnfinishedFlight` is invoked once per recording per frame from
`RecordingsTableUI` row drawing, `UnfinishedFlightsGroup` membership filtering,
and `TimelineBuilder`; the missed-vessel-switch warn fires in `ParsekFlight`
`Update()` until the recovery handler clears the predicate, which in this
playtest took dozens to hundreds of frames per vessel. Each of the 7 return
paths in `IsUnfinishedFlight` now uses `ParsekLog.VerboseRateLimited` keyed by
`{reason}-{recordingId}` so each (recording, reason) pair logs once per
rate-limit window. The missed-vessel-switch warn now uses
`ParsekLog.WarnRateLimited` keyed by `missed-vessel-switch-{activeVesselPid}`
so each vessel logs at most once per window. Regression
`EffectiveStateTests.IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine`
calls the predicate 100x with the same recording and asserts a single emitted
line.

2026-04-25 update (post-#591 second-tier cleanup): the `2026-04-25_1933_refly-bugs`
KSP.log surfaced six more spam sources, addressed as numbered bugs #592-#596
(closed in this commit) plus #597 (open underlying-logic concern). #592 covers
the ~3300 `Time warp rate changed` / `CheckpointAllVessels` / `Active vessel
orbit segments handled` lines from KSP's chatty `onTimeWarpRateChanged`
GameEvent. #593 covers ~1190 lines from repeatable record milestones
(`Records*` IDs) re-emitting the same `Milestone funds` / `stays effective` /
`Milestone rep at UT` line on every recalc walk. #594 covers 221 KspStatePatcher
bare-Id fallback lines. #595 widens the OrbitalCheckpoint playback and Recorder
sample-skipped rate-limit windows from 1-2s to the default 5s. #596 gates the
PatchFacilities INFO summary on having actual work. #597 later closed the
underlying duplicate checkpoint work with a same-tree/same-rate/same-UT guard
plus recorder-level duplicate-boundary idempotence.

2026-04-26 update (observability Phase 1 current spam hygiene): the newest
retained package `2026-04-26_0118_refly-postfix-still-broken` surfaced a
different top-repeat set: finalizer-cache periodic summaries, repeated
patched-snapshot missing-body/captured pairs, repeated extrapolator seeded
orbital-frame-rotation lines, and small GhostMap cleanup/window repeaters. This
branch keys finalizer summaries by owner/recording/terminal state, removes the
no-delta Info backstop, keeps only the first unique classification at Info,
gates patched-snapshot and OFR-seeding details with `VerboseOnChange`, and
rate-limits empty GhostMap cleanup plus diagnostics missing-sidecar warnings.
The follow-up also gates repeated all-zero ledger summaries and sandbox/no-target
KSP patch skips with `VerboseOnChange`. Focused xUnit log assertions pin each
gate. Remaining broader audit work stays tracked by the Observability Audit
section above.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
