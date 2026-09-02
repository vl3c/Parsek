# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.
- `done/todo-and-known-bugs-v6.md` - the v0.9.2 / v0.9.3 bug-closure wave and the first half of the v0.10.0 cycle: Re-Fly supersede / anchor-propagation / co-bubble-retirement closures, the watch-mode W-cycle + chain-seam fixes, the schema generation-3 reset, the Missions window (tab + looping + periodicity + zero-drift reschedule), re-aim interplanetary transfers, the Map/TS render-tracer MVP (PR #1005), and the debris-rendering / switch-fly auto-record closures. Archived 2026-06-05.
- `done/todo-and-known-bugs-v7.md` - the v0.10.1 / v0.10.2 / v0.10.3 finish-up: logistics milestones M1-M6 (non-KSC origin, mod resources / harvest, pickup, multi-stop / multi-origin / round-trip, inter-body, legibility) + the claw producer; missions M-MIS-1..6 / 8 / 9 and the re-aim / periodicity / phasing solver stack; the Map/TS render rewrite cutover; the career-economy bug wave (BUG-A..H, the records-milestone recalc storm, contract-discard desync); and the ledger ground-truth audit closures. Archived 2026-07-09.
- `done/todo-and-known-bugs-v8.md` - the v0.10.4+ automated-testing buildout and the 2026-08 finding wave: the M-A5/M-A6/M-B1/M-B2/M-C1/M-C2 module records and the whole B1-B30 / EVA / BDOCK / CL / GS / V-lane flight-forensics ledger, the M-A7 render-composition Phases 1-2 closures, the interplanetary-program measurement records (Eve/Dres/Moho/Jool/Eeloo/moon-to-moon/B29 Kerbin return), the watch-mode entry-refusal family, the ledger/career capture-fix wave, the 2026-08-29 hygiene pass closures (#426 rejected-by-design, #428 won't-build), and every other struck entry through 2026-08-29. Archived 2026-08-29.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## ~~LOG-FORMAT-CULTURE-SWEEP: ~1400 `:F` format specifiers inside `ParsekLog.*` calls looked culture-broken after a comma-locale unit-test run printed `targetUT=150,00`~~ [FOUND 2026-09-02 during the PR #1594 KSC log-key fix. CLOSED 2026-09-02 by evidence: no runtime defect, no code change]

**What was suspected.** CLAUDE.md said "InvariantCulture everywhere", the unit-test host on this ro-RO machine rendered a `{targetUT:F2}` log line as `targetUT=150,00`, and a tokenizing scan of `Source/Parsek` (every `:F0`..`:F6` / `:E` / `:N` specifier inside a `ParsekLog.*` call span, multi-line calls included) counted 1412 such sites across 112 files (638 `F1`, 264 `F0`, 214 `F2`, 171 `F3`, 82 `F4`, 24 `F5`, 18 `F6`, 1 `E2`; `ParsekFlight.cs` 138, `GhostMapPresence.cs` 92, `RuntimeTests.cs` 90, `VesselSpawner.cs` 72, `BackgroundRecorder.cs` 67), plus 661 more in strings built outside a log call (2073 in the tree). 813 of the 1412 already sit inside a `string.Format(CultureInfo.InvariantCulture, ...)` (the `ic` / `IC` / `Inv` locals are all that constant), so the population that would actually have been converted is about 600, not the whole count; still a 100-file mechanical diff.

**What the audit established.** (1) KSP itself pins the culture: the decompiled 1.12.5 `Assembly-CSharp` has exactly one culture assignment, `Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture("en")` in `HighLogic.Awake`, and `AddonLoader` starts addons from its `sceneLoaded` callback, after that Awake. Parsek neither sets a culture nor starts a thread (`new Thread` / `Task.Run` / `ThreadPool` / `Parallel` all grep to zero in `Source/Parsek`). (2) Real flights on this comma-locale machine print dot decimals: the H40 collected log (`logs/2026-08-28_2358_H40-logistics-isolated-depot-route/KSP.log`, 35585 `[Parsek]` lines) has 3991 case-insensitive `ut=` / `UT=` dot-decimal values across 3430 lines (regex `[a-z]*ut=[0-9]+\.[0-9]+`, case-insensitive) and zero comma-decimal ones, and the same sweep over all 376 collected `KSP.log` files under `logs/` finds zero comma-decimal `ut=` anywhere; the only `digit,digit` hits are vector and list separators (`(0.000,0.000,0.000)`, `run=[3923,15006]`), whose components are themselves dot-formatted `F3` output. The dev instance's live `KSP.log` reads the same. (3) The harness would already have caught a comma: `W1-watch-distance-cutoff` pins `splitUT=212\.20` / `loopUT=434\.5` and `CL-2-pod-impact-ledger` pins `-9\.999828` in `logContract` regexes, and both fly green. Not checked: `CreateSpecificCulture` keeps user overrides, so a Windows user whose OWN culture is en-US and who has customized the decimal separator could in principle still see a comma - unreachable here (this machine is ro-RO, so no override applies), and probably inert under Unity's Mono, which does not read Windows regional customizations.

**Resolution.** The CLAUDE.md rule is scoped to serialization plus anything a unit test reads (log-sink captures, pure formatters, test-pinned tooltip text), with the runtime evidence recorded next to it so the next comma-locale test failure is fixed at the ONE site the test reads (the `ParsekKSC.Playback.FormatUt` pattern from PR #1594) rather than triggering a repo-wide sweep. No `ParsekLog.F(double, digits)` helper: a helper that 1400-odd sites do not need is a second convention to police. The one runtime exception is the M-A2 command seam (`docs/dev/design-autotest-command-seam.md`), whose response grammar the harness parses and which formats invariantly by its own contract. Three in-source comments that stated the retired runtime rationale (`ParsekKSC.Playback.FormatUt`, the `FlightRecorder` time-regression Warn, `MissionRevealInGameTests.Px`) now name the test host as the reason; the code was already correct.

## ~~OPTIMIZER-PASS-STALE-INDEX-CACHES: the mid-session optimization pass restructures the committed list without bumping StateVersion or reindexing any index-keyed live state~~ [FOUND 2026-09-01 by a design review of `RecordingStore.Optimization.cs` (no flight evidence; latent). FIXED the same day]

**What the review found.** `RecordingStore.RunOptimizationPass` mutates `committedRecordings` mid-list - `recordings.RemoveAt(idxB)` in the merge pass and `recordings.Insert(recIdx + 1, second)` in the split pass - and a grep of `RecordingStore.Optimization.cs`, `RecordingOptimizer*.cs` and their callers found zero `BumpStateVersion()` calls and zero `GhostPlaybackEngine.ReindexAfterDelete` calls (there was no insert-side reindex at all). The pass runs mid-session from `MergeDialog.Commit` (tree merge in FLIGHT) and `ChainSegmentManager.CommitSegmentCore` (chain-segment commit in FLIGHT); the two load-time callers in `ParsekScenario` are harmless because every store rebuilds afterwards. The PR #1590 review then found the same shape one layer out: `RecordingTreeSplitter` inserts the post-rewind TIP through `InsertCommittedAfter` (mid-list, bump only) in the SAME `MergeCommit` call; `ParsekPlaybackPolicy.heldGhosts` and every `GhostMapPresence` recording-index store were shifted by neither the new handlers nor the existing `DeleteRecording` precedent; `ChainSegmentManager.CommitChainSegment` read `Count - 1` as "the segment just committed" AFTER the pass that can merge it away; and `SampleContinuationVessel` has no id guard, so a continuation whose recording was absorbed would append the live vessel's points into whatever shifted into its slot.

**Why the shifts are reachable with live ghosts, not just on fresh recordings.** The freshly committed recordings sit at the end of the list, so a merge among them or a split of one of them shifts only indices above them (no engine state yet). But (a) a split deferred by the active Re-Fly marker (`RunOptimizationSplitPass`'s `deferredActiveReFlyId`) lands on the NEXT pass, when that recording sits mid-list with ghosts above it; (b) a chain-segment commit merges the new segment INTO the previous segment, and if the merged recording then presents an environment boundary the split inserts right after that mid-list index; (c) any pre-existing pair whose `CanAutoMerge` user-intent gates flip back to defaults mid-session (un-hide both halves, re-enable playback) merges mid-list. In all three, `ghostStates` / `overlapGhosts` / `loopPhaseOffsets` / the logged sets / `WatchModeController.watchedRecordingIndex` / `ChainSegmentManager.ContinuationRecordingIdx` above the change point one recording off, and `EffectiveState.ComputeERS` keeps serving whatever list it cached before the pass (its cache keys on `RecordingStore.StateVersion`).

**Fix (as landed after the PR #1590 review widened it).** (1) The optimizer calls `BumpStateVersion()` at each `RemoveAt` / `Insert` (not once after both passes, so a later step throwing cannot leave the ERS cache stale, and a subscriber running inside the pass already sees a fresh set). (2) One store seam, `RecordingStore.CommittedListNotifications.cs`: `CommittedRecordingRemoving(index, rec)` fires BEFORE the removal (list unshifted, so `engine.DestroyGhost` and its `OnGhostDestroyed` subscribers - `GhostMapPresence`'s index-keyed teardown reads `committed[index]` - see the right recording), `CommittedRecordingRemoved(index, rec, absorbedInto)` fires AFTER it carrying the optimizer merge target (null for a plain delete), and `CommittedRecordingInserted(index)` fires after any mid-list insert. Raised by the optimizer's two mutation sites, by `InsertCommittedAfter` (the Re-Fly origin splitter's TIP insert, the second mid-session producer the review found) and by `RemoveRecordingAt` (every delete path; `ParsekFlight.DeleteRecording` / `DeleteGhostOnlyRecording` dropped their private copies of the teardown + reindex block and ride the notifications). A throwing subscriber is contained and Error-logged. (3) `ParsekFlight` subscribes at Awake and unsubscribes FIRST in OnDestroy (before anything that can throw; a dead instance left on a static event would run ahead of the next scene's and abort its reindex). Its handlers shift every index-keyed slot: `GhostPlaybackEngine.ReindexAfterDelete/Insert` (one `ShiftIndexKeyedState` list), `ParsekPlaybackPolicy.ReindexHeldGhostsAfter*` (the held set used to release the NEIGHBOUR's ghost on `InvalidIndex` after a shift), `GhostMapPresence.ReindexPresenceAfter*` (every recording-index-keyed presence store, the `(recIdx, cycle)` overlap instances, the pid->index and chainId->index reverse maps; plus `HandleCommittedRecordingRemoving`, which tears down a ghost-less retained proto at the slot because `DestroyGhost` never fires `OnGhostDestroyed` for a slot without ghost state), `WatchModeController.OnRecordingDeleted/Inserted` (one `RebindWatchSlots` body over pure `ComputeWatchIndexAfter*` -> `ResolveIndexById`), and `ChainSegmentManager.OnCommittedRecordingRemoved/Inserted`, which rebind both continuation slots BY ID (`RebindContinuationIndices`) - a continuation whose own recording was merged away retargets to `absorbedInto`, one whose recording was deleted stops; the per-frame sampler `SampleContinuationVessel` indexes the list directly with no id guard, so "the id guard will catch it" was never true. `CommitChainSegment` resolves "the segment we just committed" through `ResolveCommittedIndexThroughAbsorption` instead of `Count - 1`, because the pass inside `CommitSegmentCore` can fold that segment into its predecessor. All int-keyed shifts go through one `IndexShift` helper. `RecordingsTableUI.RebuildSortedIndices` keys on `StateVersion` alone. Restricting the pass to load time was rejected: the mid-session merge of a freshly committed chain segment into its predecessor is the product behavior the recordings table shows.

**Deliberately not touched.** The first half of a split keeps its ghost state and plays its truncated trajectory (the same in-place truncation `TrimBoringTail` already applies to live recordings); the second half spawns through the normal path. Watching a merge-ABSORBED recording exits watch mode (delete semantics) rather than retargeting to the merge target that now carries the trajectory - not a regression (pre-fix the watched slot pointed at a ghost playing the wrong recording). The residue that is still index-stale is filed as COMMITTED-LIST-SILENT-REMOVERS below. Pinned by `OptimizationPassInvalidationTests` (ERS staleness before/after on both a split and a merge, per-mutation bump timing, notification order against the unshifted / shifted list with the merge target, mid-list insert index, `InsertCommittedAfter` / `RemoveRecordingAt` raising, throwing-subscriber containment, `IndexShift` + engine / held / map / watch / chain mirrors, continuation retarget-vs-stop, absorption resolution).

## ~~COMMITTED-LIST-SILENT-REMOVERS: four store removal helpers and the KSC / TS ghost hosts still leave index-keyed state stale~~ [FOUND 2026-09-01 by the PR #1590 review (cross-file tracer). PRE-EXISTING, narrowed by that PR. FIXED 2026-09-02]

**What.** `RecordingStore.RemoveCommittedInternal` / `RemoveCommittedById` / `RemoveChainRecordings` / `RemoveCommittedTreeById` bump `StateVersion` but raise no `CommittedRecording*` notification. Their production FLIGHT callers are `MergeDialog.ReFlyDiscard.cs` (discarding a re-fly session removes several recordings mid-list), `RewindInvoker`, `LoadTimeSweep` (OnLoad, harmless), `RecordingTreeSplitter.RollBackInMemory` (exception path) and `ParsekScenario` tree replacement. After any of those runs with ghosts alive, every engine / held / map / watch / continuation slot above the removal points one recording off - the same shape OPTIMIZER-PASS-STALE-INDEX-CACHES fixed for the optimizer, the splitter's insert and the delete paths. Also: `ParsekKSC` (`kscGhosts` / `kscOverlapGhosts`) and the tracking station subscribe to nothing, so the pass running there mid-session (deferred merge dialog outside FLIGHT, reachable only with the harness-pinned `autoMerge=false`) shifts under them; `PruneOrphanedIndexKeys` drops only keys `>= Count` with no id check. And `RecordingsTableUI`'s other index-keyed cross-frame state (`pendingDeleteGhostOnlyIndex`, the rename index, the per-row log-dedup dicts) captures an index in one OnGUI frame and applies it the next; a restructure between the two frames (an EVA-exit chain commit) applies it to the neighbour.

**Fix (2026-09-02).** One primitive, `RecordingStore.RemoveCommittedAtWithNotifications`, is now the only way a recording leaves the flat list: `RemoveRecordingAt`, `RemoveCommittedInternal`, `RemoveCommittedById`, `RemoveChainRecordings` and (through `RemoveCommittedInternal`) `RemoveCommittedTreeById` all route through it, so Removing / Removed + the `StateVersion` bump cannot be forgotten by the next helper. `ClearCommittedInternal` removes top-down through the same primitive: the Opus review of PR #1591 found its second production caller, `ReconciliationBundle.Restore`, reached from `RewindInvoker`'s failed-quicksave-load rollback in FLIGHT with ghosts alive and no scene change, where a silent wipe + re-add left every ghost slot keyed against the rebuilt list. `ParsekKSC` subscribes in `Start` (tears down the removed slot's ghost + overlap set on Removing, shifts `kscGhosts` / `kscOverlapGhosts` / the logged sets / the cadence log on Removed and Inserted through the pure `ShiftKscIndexKeyedState`; launch schedules and the unit-selection log are cleared because the next frame rebuilds them), which also fixes the Space Center table delete that used to leave every ghost above the deleted row playing its neighbour's trajectory. `ParsekTrackingStation` subscribes for the static map-presence stores it drives plus its `atmosCachedIndices` hint table. `RecordingsTableUI` captures the pending ghost-only delete, the open rename and the double-click candidate by `RecordingId` (`RecordingStore.IndexOfRecordingId` resolves on consume; a vanished id is Warn-logged and dropped) and starts its four per-row change-log dedup dicts over on every sort rebuild. Pinned by `CommittedListNotificationTests` (each helper raises at the matched index with the right pre/post counts, chain removal iterates descending, tree removal raises per member, the KSC shift in both directions). The Re-Fly discard / rewind / rollback flows now get ghost-teardown-per-removal from the FLIGHT handlers by construction; that behavior has still not been driven on a live flight. THE LOOK IS NOW A LANE: `harness/scenarios/S4.3-refly-discard-with-ghosts.toml` (authored 2026-09-02, never flown) drives exactly that sequence and pins ReFlyDiscard's per-recording removal line plus both `[Flight]` handler lines.

## AUTOMATION-GAP-KSC-TABLE-DELETE: no seam verb deletes a recording from the recordings table, so PR #1591's `ParsekKSC` subscriber (the Space Center delete that used to leave every ghost above the row playing its neighbour) cannot be driven live [OPENED 2026-09-02 while authoring flight lanes for every live-gated todo entry. HARNESS GAP, not a defect]

Every other live-gated case from that pass now has a lane (S4.3, S4.4, L6; the landed-host origin-proof probe flew as `H56` in PR #1599 and CONFIRMED its entry) or a cell (the currency tooltip). This one does not: the KSC delete path is `RecordingsTableUI -> RecordingStore.DeleteRecordingFull(index)` behind an IMGUI button, and the command seam has no recording-mutation verb below `DiscardTree`. The subscriber is pinned headlessly (`CommittedListNotificationTests.KscIndexKeyedState_ShiftsOnInsertAndDelete_AndClearsRebuiltTables`) and the SPACECENTER host lane it would ride exists (V22K boots `kerbin-splashdown-recorded` into SPACECENTER with `kscGhosts` populated).

**Fix (when taken).** One additive seam verb, `DeleteRecording index=<n>` (AnyScene; FLIGHT routes to `ParsekFlight.DeleteRecording`, SPACECENTER / TRACKSTATION to `RecordingStore.DeleteRecordingFull`, mirroring the table's own branch), rejecting `index-out-of-range` and, in FLIGHT, `delete-blocked` when `CanDeleteRecording` is false. Then a V22K-shaped lane: boot SPACECENTER, let the KSC ghosts spawn (V22K's dwell), `DeleteRecording` a low index, and pin `[KSCGhost] Committed recording #N (...) removing - destroying its KSC ghost` plus `KSC ghost state reindexed after committed removal at #N` (both Verbose) with no `[ERROR]`. The verb is small and the lane is a copy; it is filed rather than built here because adding a mutating seam verb is the M-A2 design's own additive process, not a scenario author's.

## ~~SENDONCE-BLOCKED-CYCLE-NEVER-PAUSES: a "Send Once" whose cycle is BLOCKED consumes the cycle but leaves the route Active with the one-shot still armed~~ [FOUND 2026-08-30 on a real flight (log `logs/2026-08-30_1106_rover-route/KSP.log`, route `fd6ee2ff`). FIXED the same day]

**What the flight showed.** A single-stop KSC rover supply route (span 45.04 s, cadence 1x)
created Paused. First "Send Once" (10:59:14) armed `PauseAfterCurrentCycle`; the loop clock
caught up instantly (warp-jump path), cycle-0 delivered 97.6 LiquidFuel + 2 inventory parts, and
the route correctly went `Active->Paused reason=delivered-then-paused`. Second "Send Once"
(10:59:30) armed again; cycle-1 hit
`BLOCKED kind=DestinationFull reason=stored-part:evaScienceKit` (the first delivery had filled
the destination's inventory slots). The blocked branch consumed the cycle (`SkippedCycles+=1`,
`lastObserved` snapped) and returned - and the route stayed **Active with the ghost looping
indefinitely and `PauseAfterCurrentCycle` still armed**, which would have silently delivered at
an arbitrary future crossing the moment the destination freed up. The player's report was "send
once turned into an endless cycle".

**Root cause.** `Route.PauseAfterCurrentCycle` was honored on the DELIVERED paths only -
`RouteOrchestrator.ApplyDeliveryFromPlan`'s armed tail (`delivered-then-paused` /
`delivered-partial-then-paused`) and `ApplyDelivery`'s replay backstop
(`delivered-replay-then-paused`, itself a later patch of the same omission, PR #1327 finding 3).
The two BLOCKED branches - `ProcessLoopRoute`'s single-stop `BLOCKED kind=` return and
`ProcessMultiStopCrossings`'s `LoopRoute(multi) ... BLOCKED` return - consumed the armed cycle
without ever reading the flag. Neither arming provenance was served: Send Once (the one-shot's
cycle IS over) nor `TryPause` on an InTransit route (the player asked to pause; a blocked cycle
still completes the pause).

**Fix.** New `RouteOrchestrator.TryHonorArmedPauseOnBlockedCycle` mirrors the delivered tail
row-for-row: consume `PauseAfterCurrentCycle` + `SendOnceArmed`, `TransitionTo(Paused)` with the
new reason `blocked-then-paused` (constant `RouteOrchestrator.BlockedThenPausedReason`), emit the
`RoutePaused` lifecycle marker through the SAME `EmitRouteLifecycleMarker` helper (sequence 0 -
a blocked cycle emitted no rows to order behind), and `RouteStore.DropRouteEscrow` as the quiesce
transition. Two deliberate non-actions: the hold `RecordHold` just wrote is KEPT (the Logistics
window must still name why it blocked), and the recovery credit is NOT re-flushed (both blocked
branches already call `EmitPendingRecoveryCredit` at the top). Called from both blocked branches;
on the multi-stop one it also clears `stillDue` so the catch-up loop stops processing later owed
cycles on a route that just went quiet. The cadence-modulo-skip branch (`SKIPPED by cadence
modulo`) is deliberately NOT touched: it advances the marker and consumes nothing, and a
send-once arm cannot land on it (see the sub-entry below).

**Player feedback, same fix.** A warp-catch-up one-shot resolves inside the frame the click is
consumed, so both resolutions were silent on screen. Both now post through the existing
`ParsekLog.ScreenMessage` seam, gated on the `SendOnceArmed` provenance only (an ordinary loop
cycle and a plain pause-after-cycle arm stay silent): text built by the new pure
`RouteSendOncePresentation` (`BuildDeliveredMessage` / `BuildBlockedMessage`, InvariantCulture
counts, the blocked wording reused verbatim from `LogisticsHoldPresentation.DescribeHold` so the
toast and the detail panel can never disagree).

**Tests.** `RouteSendOnceBlockedPauseTests` (single-stop honor + flags + kept hold + marker +
toast + the go-quiet pin + an UNARMED negative control, the multi-stop half, the `TryPause`
InTransit provenance, and the cadence-determinism pins) plus three delivered-path cells in
`RouteOrchestratorDeliveryTests` (send-once toast, ordinary-cycle silence, pause-after-cycle
silence).

### ~~Sub-question: can a send-once arm land on a cadence-modulo skip?~~ [ANSWERED 2026-08-30 - NO]

`TrySendOneCycleNow` only pulls `NextDispatchUT` forward and clears `NextEligibilityCheckUT`; it
does not touch `WindowAnchorCycleIndex` or `LastObservedLoopCycleIndex`, and the loop path
ignores `NextDispatchUT` entirely (the dock-crossing detector owns the dispatch phase). The
modulo residual is live only under the `ReaimWindows` basis (`nResidual > 1`), where the D3
anchor-adoption rule makes the FIRST owed crossing after creation / activation / rebase / engage
ADOPT the anchor and DELIVER. A route whose anchor is already adopted can present a
modulo-skipped window to a send-once arm, but that window emits nothing, bumps no
`SkippedCycles`, records no hold and consumes no cycle - the arm survives it and fires on the
next deliverable window, which is the intended "every Nth window" semantics. So the skip is not
a consumed cycle and must not honor the pause. No change made.

## SENDONCE-RESIDUAL-PATHS: three Send-Once arm paths the blocked-cycle fix deliberately did not take, surfaced by PR #1582's clean review [FOUND 2026-08-30 by the pre-merge review of the fix. ALL PRE-EXISTING (none introduced by the fix); filed together because they are one family: "which cycle resolutions consume the arm, and does every consumer say so". Items 1 and 2 FIXED 2026-09-01; item 3 CLASSIFIED and left open by design; item 4 ADDED and FIXED 2026-09-01 from the same family, found while fixing 1 and 2 - see below. Only item 3 remains, deliberately]

**~~1. Multi-stop delivered path can end a Send Once ACTIVE (the fixed defect's delivered-side
sibling).~~** [FIXED 2026-09-01] `ProcessMultiStopCrossings` fired every due window of the cycle
in one pass with no status re-check, and `ApplyDeliveryFromPlan`'s armed tail ran PER WINDOW:
window A's delivery consumed the arm and paused (with a toast saying "Paused"), then window B's
delivery in the same pass saw the flag already cleared, fell into the ordinary else, and
transitioned the route BACK to Active. Related: the catch-up `while (stillDue)` loop never
re-checked status between passes, so a route paused by window A of pass 1 could have pass 2
dispatch + debit a whole new cycle (the blocked branch got the `stillDue = false` guard in the
fix; the delivered branch had no equivalent).

Fix: the arm is honored at CYCLE completion, never per window, keyed to the signal that ALREADY
means "this delivery is the cycle" - `ApplyDeliveryContext.BumpCompletedCycle`, which is true
only on the single-stop / legacy path and false on EVERY multi-stop window (a pickup-only last
window still completes a cycle, so no delivery window can be trusted to be "the" one). Both
armed tails inside `ApplyDelivery` / `ApplyDeliveryFromPlan` (the delivered one and the
`delivered-replay-then-paused` one) are gated on it, so an earlier window delivers WITHOUT
consuming the arm or transitioning to Paused; single-stop reduces to the previous behaviour
byte-for-byte (its ledger rows are unchanged - the `RoutePaused` marker still lands at the
window's own `stopIndex * SeqStride + 4`). The multi-stop resolution moved to the new
`RouteOrchestrator.TryHonorArmedPauseOnCompletedCycle`, called from the `cycleLastDockReached`
branch beside the once-per-cycle `CompletedCycles` bump and the cycle-complete escrow sweep -
the same seam, for the same reason. It emits the same `delivered-then-paused` /
`delivered-partial-then-paused` reason (partial read off the cycle-scoped
`Route.LastPartialDeliveryCycleId`), lands its marker at the LAST stop's stride block + 4, and
posts a counts-free toast (`RouteSendOncePresentation.BuildCycleDeliveredMessage`) because a
cycle's windows can straddle several ticks, so no site can quote the whole cycle's actuals.
For the catch-up loop: `ProcessLoopRoute` now re-checks `RouteStatusPolicy.GhostDriving` - the
SAME gate it opens with - after every pass and breaks out, so no later owed cycle is dispatched
on a route that stopped driving its ghost mid-tick (the honored pause also clears `stillDue`
directly, mirroring the blocked branch). Owed cycles are then rebased away by `TryActivate`, as
documented below. Covered by `SendOnceArmed_MultiStop_BothWindowsInOneTick_EndsPaused_OneToast`,
`..._WindowsAcrossTwoTicks_PausesOnlyOnCompletion`,
`..._CatchUpStopsAfterThePause_NoNextCycleDispatch`,
`Unarmed_MultiStop_BothWindowsInOneTick_StaysActive_NoPauseNoToast`,
`PauseArmedWhileInTransit_MultiStopCycleCompletes_PausesWithoutToast`
(`RouteSendOnceBlockedPauseTests`) and the core gate pin
`StatusTransition_MultiStopWindow_ArmedPause_DeliversWithoutConsumingTheArm`
(`RouteOrchestratorDeliveryTests`).

**~~2. The ELS replay backstop consumes the arm silently.~~** [FIXED 2026-09-01]
`ApplyDelivery`'s replay branch (`delivered-replay-then-paused`) paused without a toast - the one
resolution shape where the player is MOST likely to click again (the run produced no new
delivery). It now posts `RouteSendOncePresentation.BuildAlreadyDeliveredMessage` under the same
`SendOnceArmed`-only gate as the other toasts, capturing the provenance flag before consuming it
exactly as the delivered tail does. Counts-free by construction: the delivered row is already in
the ledger and the branch re-plans nothing, so there are no actuals to quote. Covered by
`IdempotentReplay_SendOnceArmed_PostsAlreadyDeliveredToast` plus the two negative controls
`IdempotentReplay_PauseAfterCycleArmed_PausesWithoutToast` /
`IdempotentReplay_Unarmed_PostsNoToast_StaysActive`.

**3. Endpoint-lost at delivery leaves the arm live.** [OPEN - classified 2026-09-01 as
DELIBERATE, not a bug to close] That branch transitions to `EndpointLost` without touching
`PauseAfterCurrentCycle`/`SendOnceArmed`, and both flags persist through the codec. Inert today
(`EndpointLost` is not ghost-driving, and `TryActivate` - the only exit - clears the flags).

**Why the arm deliberately SURVIVES an endpoint loss** (the classification decision, made while
fixing 1 and 2 and recorded here so nobody "completes the family" by consuming it): an endpoint
loss is RETRYABLE, not a resolution. `ApplyEndpointLost` / the delivery-time loss both set
`NextEligibilityCheckUT = currentUT + WaitRetryIntervalSec` precisely because the endpoint may
come back on its own (the surface-proximity fallback re-resolves, the destination reloads, the
vessel comes back into range) - the route is waiting, not finished. That puts it in the SAME
class as the two `IsPostponementHold` kinds (`SourcesStale`, `WaitingForPartner`), which the
#1582 fix already exempts from consuming the arm on the blocked path: the player's one run has
not happened yet, and cancelling their Send Once for a hold that self-clears is exactly the
failure mode that exemption exists to prevent. Consuming the arm here would ALSO be
inconsistent with the blocked path, where `EndpointLost` arrives as an eligibility kind and is
handled by `TryHonorArmedPauseOnBlockedCycle`'s postponement allowlist policy - two paths, one
kind, opposite answers.

What keeps it OPEN is the stale-arm hazard, not the current behaviour: if any future path ever
returns an endpoint-recovered route to a ghost-driving status WITHOUT clearing the flags, a
one-shot armed hours ago would silently fire. The invariant to hold is "every entry back into
ghost-driving either honors or clears the one-shot"; `TryActivate` is the only such entry today
and it clears (`ClearOneShotFlags`, which the rewind classifier already treats as a hazard
surface). Any new one must do the same - or, if endpoint-lost is ever reclassified as a
resolution, it should be added to the postponement allowlist's mirror, not fixed at one call
site.

**~~4. A SINGLE-STOP pure-PICKUP loop route never honors the arm on its delivered path.~~**
[FOUND 2026-09-01 while fixing 1 and 2; FIXED 2026-09-01 in the same family]
`EmitLoopCycle`'s `else` branch (no delivery manifest) bumped `CompletedCycles` and returned; the
armed tail lives inside `ApplyDelivery`, which that branch never calls. So a Send Once on a
pickup-only single-stop route completed its cycle and kept looping with both flags armed - the
same end state as the blocked defect #1582 fixed, reached through a different door. (A blocked
cycle on such a route DOES pause, via `TryHonorArmedPauseOnBlockedCycle`, so the two resolutions
disagreed.) The multi-stop equivalent was already covered by item 1's fix, because
`TryHonorArmedPauseOnCompletedCycle` sits at cycle completion and does not care which halves the
windows emitted.

Fix: that `else` branch IS the cycle's completion for a pure-pickup route, so it now calls the
same `TryHonorArmedPauseOnCompletedCycle` helper, immediately after the `CompletedCycles` bump -
the single-stop mirror of the multi-stop `cycleLastDockReached` call site, with the same
`delivered-then-paused` reason, the same last-stop stride+4 marker slot (Sequence 4 at one stop,
where the single-stop delivered tail also lands it), the same counts-free toast and the same
pending-recovery-credit flush. The helper is a no-op when nothing is armed, so an ordinary
pickup cycle is byte-identical. The DELIVERED path is untouched: the single-stop delivered tail
still resolves inside `ApplyDeliveryFromPlan` under its `BumpCompletedCycle` gate (folding it
into the helper too was considered and left alone - it would move a working, ledger-pinned
resolution for tidiness alone). Still unverified against a real save; no known pickup-only route
exists yet. Covered by `SendOnceArmed_PurePickupCycle_EndsPaused_ClearsFlags_OneToast`,
`Unarmed_PurePickupCycle_StaysActive_NoPauseNoToast` and
`PauseAfterCycleArmed_PurePickupCycle_PausesWithoutToast` (`RouteLoopPickupFireTests`); all
three go red against the pre-fix branch.

Also banked from the same review (comment-only, corrected in the follow-up commit): the
blocked-cycle marker CAN share its `(routeId, UT, Sequence=0)` key with a
`RouteRecoveryCredited` row on the career-KSC path (rows distinguished by Type; no consumer
orders between them), and `TryActivate` REBASES owed cycles away rather than resuming them
(desirable - documented so nobody "restores" a resume that would replay unauthorized cycles).

## ~~ROUTECADENCE-1X-2X-FLAP-SUSPECTED: a paused single-stop route appeared to oscillate between cadence 1x and 2x every couple of seconds~~ [INVESTIGATED 2026-08-30 off the same log. NOT A DEFECT - the three transitions are three player clicks. No product change]

The reading that raised it: `RouteCadence: cadence 1x->2x interval 45.039->90.079 (rebase)` at
10:58:02.544, `2x->1x` at 10:58:04.948 (preceded by
`ResolveLoopUnit ... loop-unit cache rebuilt reason=builder-inputs-changed`), `1x->2x` again at
10:58:06.634 - on a Paused route with the Logistics window open.

**Why it is not a flap.** Every one of the three is immediately followed by
`[UI] Logistics: Cadence route=fd6ee2ff N=<n> result=applied`, which is emitted from exactly one
place - `LogisticsWindowUI`'s deferred `pendingCadenceRoute` apply. `pendingCadenceRoute` is set
only inside the `-` / `+` `GUILayout.Button` click handlers (row stepper and detail stepper); the
typed-interval commit path applies through `RouteCadence.ApplyMultiplier` directly and logs its
own line. `CadenceMultiplier` / `DispatchInterval` have no other production writer than
`RouteCadence.ApplyMultiplier` and `RouteBuilder` at creation - no tick path, no cache rebuild,
no loop-unit resolution mutates them. The 2.4 s and 1.7 s spacing is human click speed. So the
sequence is `+`, `-`, `+`.

**Why the `builder-inputs-changed` rebuild sits in the middle of it.** `ApplyMultiplier`'s M5
windowed-basis probe calls `ResolveLoopUnit` BEFORE mutating the interval. The loop-unit cache
key is `MissionLoopUnitBuilder.BuildSignature(...)`, which folds the backing mission's loop
period - and that period IS `Route.DispatchInterval`, which the PREVIOUS click had just changed.
So the rebuild is the previous click's consequence observed at the next click's probe, not a
cause of anything. (The third click's rebuild line is present too, collapsed into the
`| suppressed=1` rate-limit tail.) Pinned by `TickLoop_NeverMutatesCadenceOrInterval_PausedOrActive`,
`ApplyMultiplier_SameN_IsNoOp_NoRebase_NoIntervalRewrite` and
`DeriveDispatchInterval_IsDeterministic` in `RouteSendOnceBlockedPauseTests`.

**The `cadence=95` vs `interval=90.08` question, answered.** Not a discrepancy and not a tail
gap. `MissionPeriodicity.Solve` returned `method=unconstrained P=5 lock=yes` for this
single-member tree, so `MissionLoopUnitBuilder` phase-locked the unit and ran
`QuantizeCadenceToMultipleOfP(90.08, 5)` = `ceil(90.08/5)*5` = `19*5` = **95** - the log says so
in as many words (`PhaseLock APPLIED: ... cadence 90.079999999918186->95`). The render/delivery
clock deliberately runs on the quantized cadence so the ghost's relaunch schedule sits on
faithful windows; the route's own `DispatchInterval` stays the raw `N * span`. Intended.

## ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT: a KSC route whose tree ROOT recording carries no `VesselSnapshot` dispatches for FREE in career [FOUND 2026-08-30 off the same `logs/2026-08-30_1106_rover-route` flight while diagnosing a `cost=0` Verbose line. LATENT CAREER DEFECT — the flight was SANDBOX so nothing was mischarged. FIXED 2026-09-01. **RE-OPENED 2026-09-01**: lane RVR-4's first flight measured the shipped fix as insufficient on the real tree. RE-FIXED 2026-09-02 (round 2 below); STILL UNFLOWN - the RVR-4 re-fly is what closes this]

**Evidence.** Every UI repaint logged `ComputeDispatchFundsCostForRoute: route fd6ee2ff source
recording cf8d06fc... not in ERS or has no VesselSnapshot; cost=0`. The recording IS in ERS (a
committed member of tree `73e50f1e`); what it lacks is a `VesselSnapshot` — every
`SaveRecordingFiles: id=cf8d06fc...` line reads `wroteVessel=False` while the other three tree
members read `True`.

**Mechanism.** The always-tree ROOT is created with a `GhostVisualSnapshot` only
(`ParsekFlight.cs` says so at the creation site: "VesselSnapshot is captured later at
stop/split time"), and a root that never reaches a capture site — here the runway stub that
ended at the rover's undock split, 0 trajectory points — keeps `VesselSnapshot == null`
forever. `RouteBuilder` sets `SourceRefs[0]` to the tree root, and
`ComputeDispatchFundsCostForRoute` (`RouteOrchestrator.cs`, ~5039) returns 0 on
`source.VesselSnapshot == null` BEFORE the M2 run-manifest branch is reachable, even though
this root DOES carry a run manifest (`RouteRunManifest start written ... parts=16 res=1`). In
career that zero flows into BOTH the funds eligibility gate (vacuously passes) and the actual
charge: the KSC dispatch is silently free, with one Verbose line as the only trace.

**Fix (2026-09-01): a member fallback for the PARTS term, the root's manifest for the
RESOURCE term.** `SourceRefs[0]` (the tree root) stays the PREFERRED basis, so every route
that already costs from its root is byte-identical. When the root carries no
`VesselSnapshot` (or is absent from ERS), `ComputeDispatchFundsCostForRoute` now walks
`route.SourceRefs` IN ORDER and prices the PARTS term from the first member whose ERS
recording has one; the RESOURCE term still prefers the ROOT's COMPLETE `RouteRunManifest`
(the launch load is the root's fact, and the root carries the manifest even when it carries
no snapshot), falling back to the chosen member's snapshot resources through the legacy
stop-snapshot walk. The other half of the decision - do NOT capture a snapshot on the root
at the orphaning split - was rejected: it would rewrite recorder behavior for a costing
question, and would not repair a single existing save.

**The combined-vessel exclusion, and why it is load-bearing.** The dock-merged child IS a
`SourceRefs` member: `RouteBuilder.BuildRouteSourceRefs` adds the window-carrying leaf
unconditionally ("The leaf (dock child) is ALWAYS a member"), and the M-MIS-5 origin carrier
joins the same way. That recording's `VesselSnapshot` is the MERGED snapshot
(`ParsekFlight`'s dock-merge site stamps the window and the combined snapshot together), so
a naive first-member-with-a-snapshot walk would have charged the player for the destination
station's parts on every cycle. `RouteOrchestrator.IsCombinedVesselSourceMember` skips such
a member on three positive facts, any one of which suffices: it carries a
`RouteConnectionWindow` (the dock-merge site is that list's ONLY writer, so carrying one IS
being a merged child - and this catches the origin carrier too, whose StartUT is well before
the delivery dock), its id is the route's own `DockMemberRecordingId`, or its recording
starts at/after `Route.RecordedDockUT`. The exclusion applies to the FALLBACK walk only.
`RouteStore.RevalidateSources` / `RouteProofHasher` are indifferent to which member is
costed - they inspect every ref equally and have no notion of a costed member.

**When nothing can be costed.** If no member is in ERS with a usable snapshot the method
still returns 0, but that case is now a distinct rate-limited line (this method runs per UI
repaint): `FundsCost: route <id> UNCOSTED - no SourceRefs member ... (a career KSC dispatch
charges nothing)`. The costed path logs `FundsCost basis=<launch-manifest|stop-snapshot>
route=... source=<root id> snapshotSource=<priced member id> fallback=<0|1>`.

**Tests** (`RouteOrchestratorTests`): `..._RootHasSnapshot_PricesRoot_NoFallback` (the
regression pin), `..._SnapshotlessRoot_FallsBackToMemberParts_KeepsRootLaunchManifest`,
`..._SnapshotlessRoot_IncompleteManifest_UsesMemberSnapshotResources`,
`..._SnapshotlessRoot_SkipsDockMergedMember` (merged child ordered AHEAD of the transport so
the walk must SKIP it, not merely never reach it),
`..._NoMemberWithUsableSnapshot_ReturnsZero_LogsUncosted`, and
`IsCombinedVesselSourceMember_MatchesEachPositiveFact_NotThePreDockTransport`. WHICH
snapshot the walk visited is proved mechanically rather than by the basis line alone: headless
`LookupPartCost` prices everything at 0, so every visited part name emits its own
`Unknown part cost: name=...` Warn, and each fixture recording carries a distinct part name.
Unblocks Tier C item 9 of the supply-route coverage program (`autotest-roadmap.md`); that
career lane is still unauthored and has never flown.

### ROUND 2 - the 2026-09-01 fix was insufficient on the real tree (RE-OPENED 2026-09-01 by RVR-4 flight 1; RE-FIXED 2026-09-02, unflown)

**What flew.** Lane `RVR-4-rover-route-career-cost`, first flight `2026-09-01_2204`
(`harness/results/2026-09-01_2204_RVR-4-rover-route-career-cost_shots/KSP.log`). Verdict
`PARSEK-FAIL(expectation)` on the two cost tokens; the delivery chain itself was green
(97.6 LiquidFuel `path=unloaded` plus two inventory units, cycle 1 `DestinationFull`
exactly as RVR-2 measured). What the log carries instead of a charge:

```
FundsCost: route 1dcc955a UNCOSTED - no SourceRefs member (of 2, root cf8d06fc...) is in
ERS with a usable VesselSnapshot; cost=0 (a career KSC dispatch charges nothing)
DispatchDebit: route 1dcc955a cycle=cycle-0 ut=1600 cost=0 careerKsc=1
```

**Blocker 1 (the earlier one, and the one the spec did not predict): after a load NO member
has a `VesselSnapshot` at all.** `ParsekScenario.cs` (~4275, load phase
`crew-auto-unreserve`) sweeps every committed recording and, for
`rec.VesselSnapshot != null && !rec.VesselSpawned && currentUT > rec.EndUT`, unreserves the
crew and NULLS `rec.VesselSnapshot`. On this flight that fired 4.6 s before the dispatch -
log lines 11505-11508, `Auto-unreserved crew for recording #0 (A)`, `#2 (B)`, `#3 (B)`,
`#4 (A)`; `#2` is the dock-merged leaf `f2fb77ea`, loaded moments earlier with
`hasVesselSnapshot=True`. That is why the flight logged NO
`FundsCost: ... skipping member ...` line (grep count 0): the fallback walk short-circuited
on `member.VesselSnapshot == null` before `IsCombinedVesselSourceMember` ever ran. The
`_vessel.craft` sidecar is still on disk; only the in-memory spawn surface is gone.

**Blocker 2 (the one the round-1 fix aimed at, mis-modelled): there is no un-merged member
to fall back to.** The route's members are exactly `[cf8d06fc (root, no snapshot),
f2fb77ea (dock-merged leaf)]` - the flight logged `ComputeMemberRecordingIds: ...
keptIntervals=1 members=1` and then `Built route ... members=2 excluded=3`.
`RouteBackingMission.CollectMemberRecordingIds` collects one id per composition
**through-line head** (`StripSegMarker(node.HeadLegId)`), and the transport's driving legs
are further structural intervals of the SAME vessel line, so their `HeadLegId` strips back
to the root id. The spec's expected basis `4370a799` is not a member at all (and, starting
after the dock, would be excluded as combined anyway). The unit fixture in
`RouteOrchestratorTests` had assumed a separate transport member the builder does not
produce for this shape, which is how a green suite shipped a fix with no candidate.

**Round-2 fix, both halves in `RouteOrchestrator.ComputeDispatchFundsCostForRoute`.**

1. *Durable costing surface.* Every basis read now goes through
   `ResolveCostingSnapshot(rec, out surface)`, which prefers `VesselSnapshot` and falls back
   to `GhostVisualSnapshot` - the same ConfigNode (a `CreateCopy` taken at the same capture
   sites, with its own `_ghost.craft` sidecar), which the sweep does not touch. It is read
   strictly READ-ONLY. The chosen surface is logged as `snapshotSurface=vessel|ghost`. This
   applies to the PREFERRED root basis and to the fallback walk alike. The
   `ParsekScenario` sweep itself is deliberately UNCHANGED.
2. *Transport-subset basis.* When no un-merged member yields a snapshot, the dock-merged
   member is priced RESTRICTED to its `RouteConnectionWindow.TransportPartPersistentIds`
   (`ResolveTransportSubsetWindow` picks the window matching `Route.RecordedDockUT`, else
   the earliest dock). Those parts are the launch vehicle - a transport cannot reach the
   dock carrying parts it did not launch with, EVA construction aside - and the endpoint's
   parts are never billed. The resource basis is unchanged: the root's COMPLETE run
   manifest first, else the (now filtered) snapshot's own `RESOURCE` amounts.
   `RouteFundsCalculator` gained a five-argument overload taking the pid set; both existing
   overloads route through the same walk with `restrict == null`, pinned by
   `..._NullRestriction_MatchesUnrestrictedBases`.
3. *Two fail-closed refusals*, both landing on the UNCOSTED breadcrumb rather than a wrong
   charge: an empty/missing transport pid set (nothing separates transport from endpoint),
   and a pid set that matches ZERO `PART` nodes on the chosen surface (which would emit the
   resource term with no parts term - a plausible-looking but wrong bill). A `PART` with no
   parseable `persistentId` is excluded under any restriction.

The costed line is now
`FundsCost basis=<launch-manifest|stop-snapshot> route=... source=<root id>
snapshotSource=<priced member id> fallback=<0|1>[ subset=transport] snapshotSurface=<vessel|ghost>[ parts=<n>/<total>] cost=...`.

**New tests.** `RouteOrchestratorTests`: `..._OnlyDockMergedMember_PricesTransportSubset`,
`..._OnlyDockMergedMember_IncompleteManifest_UsesFilteredSnapshotResources`,
`..._EmptyTransportPidSet_StaysUncosted`,
`..._TransportPidsMatchNoPart_StaysUncosted`,
`..._GhostSurfaceOnMergedMember_PricesTransportSubset`,
`..._GhostSurfaceOnRoot_PricesRootDirectly`,
`ResolveTransportSubsetWindow_PrefersRouteDockUT_ThenEarliest`,
`ResolveCostingSnapshot_PrefersVessel_FallsBackToGhost`. `RouteFundsCalculatorTests`:
`..._TransportSubset_ExcludesEndpointPartsAndResources` (the value proof - the depot's
9000-cost part and 250 of depot fuel stay out),
`..._TransportSubset_ManifestBasis_PricesTransportPartsOnly`,
`..._TransportSubset_UnidentifiablePart_Excluded`,
`..._NullRestriction_MatchesUnrestrictedBases`, `CountRestrictedParts_ReportsKeptOverTotal`.
The five round-1 pins are unchanged and still green.

**OBSERVATION (report-only, NOT a fix, filed for a maintainer look).** The
`crew-auto-unreserve` sweep nulls `VesselSnapshot` on EVERY aged committed recording, not
just spawn candidates: its purpose is releasing crew reservations, but dropping the snapshot
is a side effect that every later consumer of the spawn surface inherits. After any load,
`rec.VesselSnapshot` is null for every committed recording whose `EndUT` has passed without
a spawn - so any code that reads it as "the recording's parts" silently sees nothing, which
is exactly how a costing bug survived a green unit suite and a shipped fix. Route costing is
now insulated (it reads `GhostVisualSnapshot` instead), but the general question - should
crew unreservation drop the snapshot at all, given the sidecar is still on disk and
`GhostVisualSnapshot` is retained regardless - is unanswered here. No change proposed;
recorded so the next reader of a `VesselSnapshot == null` surprise finds the cause in one
grep rather than one flight.

**OBSERVATION (2026-09-01, REPORT-ONLY, NOT A DEFECT CLAIM): on a FILE-CONSTRUCTED career
fixture the guarded uplift holds ledger milestone funds out of the live pool, and says so
every recalc.** Measured on RVR-4 round 2 (`2026-09-01_2228`), which is the run that also
measured the costed dispatch above:

```
PatchFunds: GUARDED UPLIFT clamped resource=Funds running=29200 live=11000
            wouldBeTarget=29200 clampedTo=11000 (no time-travel context)
            - spent value held; ledger may be missing a spending channel
PatchReputation: GUARDED UPLIFT clamped resource=Reputation running=0.999999463558197
            live=0 wouldBeTarget=0.999999463558197 clampedTo=0 ...
```

`rover-route-career` is `rover-route-recorded` stamped into CAREER by construction, so it
inherits a `Parsek/GameState/ledger.pgld` carrying five `MilestoneAchievement` rows worth
18,200 funds (and 1 reputation) with NO corresponding live-pool history - the source save
was SANDBOX, where KSP computes milestone awards but no pool exists to receive them.
`FundsModule` adds all five to the running balance (distinct `milestoneId`s, first-hit
branch), `EnsureInitialFundsSeed` seeds from the live pool because there is no
`FundsInitial` row, and `KspStatePatcher.PatchFunds` then sees a running balance above the
live value and does the conservative thing: `ApplyDrawdownGuard`'s keep-what-you-earned
branch refuses the uplift and holds the pool at the spent value. The WARN repeats on every
recalc for the life of the save.

THE GUARD IS BEHAVING AS DESIGNED - "ledger may be missing a spending channel" is exactly
what a ledger with award rows and no matching history looks like, and it cannot tell that
apart from a real leak. Nothing here is proposed as a fix. It is filed because (a) a
reader meeting the repeating WARN on a synthetic career fixture should find the cause in
one grep rather than diagnosing a funds bug, and (b) it is load-bearing for RVR-4: the
clamp is precisely why that lane's live pool equals its 11,000 seed, which is what makes
cycle 1 go `FundsShort shortfall=3820` at `2 * cost - seed`. The lane deliberately does
NOT pin the WARN (a fixture that later gained a spending channel would stop emitting it,
and that must not red); the arithmetic it produces is pinned instead. If a future career
fixture is built by construction from a sandbox harvest, expect the same line and expect
its live pools to be the seeded values rather than the ledger's totals.

## ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING: the start-docked proof is produced and attached to `CaptureAtStop`, and in always-tree mode nothing forwards it onto a `Recording` [FOUND 2026-09-02 by lane H57's first flight (`2026-09-02_1005`), which red on exactly this. OPEN, product defect, blocks the whole start-docked origin feature]

**What the flight measured.** H57's subject cell started a recording on a settled docked
pair, the producer fired (`RouteOriginProof captured: ... partnerBody=Kerbin surface=1`),
the cell then stopped the recording and found NO recording in the tree carrying a
`RouteOriginProof`.

**The caller set, re-derived rather than read off comments, and corrected once already.**
`Recording.RouteOriginProof` has exactly two writers in `Source/Parsek`:
`Recording.ApplyPersistenceArtifactsFrom` (`Recording.cs:912`) and
`Recording.CloneWithPersistenceArtifacts` (`Recording.cs:1023`).

THE OMISSION LIVES IN `ParsekFlight.ApplyCapturedLogisticsMetadataToRecording`
(`ParsekFlight.cs:4551`), NOT in `AppendCapturedDataToRecording`. That distinction is the
whole fix shape and an earlier draft of this entry got it wrong. The metadata helper has
TWO callers: `ParsekFlight.FlushRecorderToTreeRecording` calls it DIRECTLY
(`ParsekFlight.cs:3898`) - that is the ordinary tree-mode stop, and the path H57 red on -
and `AppendCapturedDataToRecording` (`ParsekFlight.cs:4518`) reaches it at
`ParsekFlight.cs:4538`, which is how the undock split (`ParsekFlight.cs:5580`) and the
merge (`ParsekFlight.cs:6302`) get there. An adoption written into
`AppendCapturedDataToRecording` would therefore fix the split and the merge and MISS the
ordinary stop flush entirely. The `RouteOriginProof` omission is a deliberate no-op inside
the metadata helper with its rationale at `ParsekFlight.cs:4659-4670` ("that proof reaches
committed recordings via `Recording.ApplyPersistenceArtifactsFrom` at chain-commit time").

**What the chain-commit path actually is, stated without overclaiming.**
`ChainSegmentManager.CommitSegmentCore` (`ChainSegmentManager.cs:658`) is the only
production caller of `ApplyPersistenceArtifactsFrom` in the recording pipeline, and it is
REACHABLE IN CODE, not dead: `CommitChainSegment` (`ParsekFlight.cs:1381`, `:12515`),
`CommitDockUndockSegment` (`:12397`, `:12407`, `:12418`, `:12431`),
`CommitVesselSwitchTermination` (`:11600`) and `CommitBoundarySplit` (`:11610`) are called
from ungated handlers, and only the dock/undock branch carries a legacy label. What is true
is narrower and measured: NO OBSERVED FLIGHT REACHES IT - zero `CommitSegmentCore` lines
across all 394 collected `KSP.log`s in `../logs`. So IF a chain commit does run, a
`RouteOriginProof` IS persisted, and it carries the merged-vessel pid, which means the
ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY semantics below are LIVE on that path and not merely
hypothetical.

**So the producer half is fixed and the consumer half is not reached.** The proof is built
(`RouteProofCapture.BuildStartRouteOriginProof`), held in `FlightRecorder.pendingRouteOriginProof`
between the start and the stop, and attached to `FlightRecorder.CaptureAtStop` by
`BuildCaptureRecording` (`FlightRecorder.cs:7947`). It dies there on every path any flight
has ever taken. CORROBORATED IN THE PRODUCED SAVES: the H56 and H57 runs of 2026-09-02 both
report a `ROUTE_ORIGIN_PROOF` node count of 0, H56's despite its probe reading
`proofCaptured=True outcome=captured`.

**Why this is NOT a one-line forward, and why it was left open rather than patched.** The
adoption has to go into `ApplyCapturedLogisticsMetadataToRecording` (`ParsekFlight.cs:4551`)
to cover the ordinary stop flush, and that helper is shared by the stop flush, the undock
split and the merge - so it changes tree metadata semantics for EVERY recording, not just
start-docked ones. It needs write-once semantics of its own: `pendingRouteOriginProof` is
nulled at every recorder restart (`FlightRecorder.cs:7030`), so a split-then-stop sequence
would otherwise overwrite a real proof with null. And it cannot be settled independently of
ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY below - forwarding a pid whose meaning is disputed just
persists the dispute, and the chain-commit path already would if it ever ran. Fix shape: one
write-once adoption in `ApplyCapturedLogisticsMetadataToRecording` (adopt when
`target.RouteOriginProof == null`), with a unit test for the null-source-does-not-clobber
case, taken together with the partner rule.

## ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY: the start-docked proof stamps the MERGED vessel's own pid as the origin partner, which is right only when the depot is the dominant half [FOUND 2026-09-02 by the adversarial review of the producer fix (F1/F2). OPEN, needs a design ruling before any further code]

**F1.** `FlightRecorder.CaptureStartRouteOriginProofIfDocked` stamps
`v.persistentId` into the seam candidate, so the producer can only ever emit
`partnerPid == RecordingVesselId`. That pid is the pid of the half that KEEPS the merged
`Vessel` across `Part.Undock` (the undocking subtree gets a fresh `Vessel`; the remainder
keeps the original), and which half that is comes from stock's own
`Vessel.GetDominantVessel` - vesselType priority, then mass - through
`ModuleDockingNode.DockToVessel`'s `base.part.Couple(node.part)` and `Undock`'s
`otherNode.part.parent == part` dispatch. For the canonical supply shape (a
`VesselType.Base` depot, a Rover/Ship transport) the depot is dominant, keeps the pid, and
the reading is correct. It is NOT correct in general, and it is NOT what the fix's own
in-game gate produces: H57's subject cell couples the depot INTO the transport with a raw
`Part.Couple`, so the depot is the child and LEAVES, and the recorded pid names the
transport. The cell no longer asserts pid identity (2026-09-02 follow-up); it reports the
recorded pid and pins the descriptor fields instead.

**What the docking node can actually supply.** `DockedVesselInfo` carries `name`,
`vesselType` and `rootPartUId` (a PART flightID) of each half's PRE-dock vessel, and NO
vessel pid at all - decompiled, not assumed. So the partner's vessel pid is genuinely
unresolvable at capture time. The codebase already knows part pids are the identity that
survives an undock: `RouteConnectionWindow.EndpointPartPersistentIds` is asserted on
exactly that ground by the round-trip cell.

**F2, the same ruling from the other side.** `IsSettledDockSeam` is SYMMETRIC, so a station
or a base that starts a recording while something is docked to it now gets a
`RouteOriginProof` naming itself, which flips `RouteAnalysisEngine`'s non-KSC origin gate
for a vessel that is not a transport at all. There is no route design doc in `docs/dev` to
cite a rule from (`docs/dev/done/logistics-origin-ownership-proposal.md` is the archived
source, and its start-docked bullet is the one this wave already corrected), so the rule
has to be WRITTEN before it can be cited.

**And the semantics are LIVE, not hypothetical.** `ChainSegmentManager.CommitSegmentCore`
is reachable from ungated handlers (see the entry above for the five call sites); no
observed flight has taken it - zero `CommitSegmentCore` lines across 394 collected logs -
but if one does, `Recording.ApplyPersistenceArtifactsFrom` persists a `RouteOriginProof`
carrying the merged-vessel pid onto a committed recording, and `RouteBuilder` then resolves
a non-KSC origin from it. So this is a ruling about behaviour that can already ship, not
about a branch that cannot run.

**The ruling that is needed, stated as the choice.** Either (a) the origin partner is the
half that STAYS, in which case `v.persistentId` is right by construction and the depot-side
start is a real false positive that needs an exclusion rule and a test; or (b) the origin
partner is the OTHER half of the seam, in which case the proof must carry that half's
pre-dock `name` + `rootPartUId` (new fields, schema generation bump) and the pid is bound
later, by the undock or by the existing M1 proximity rebuild. Nothing further should be
built on the current producer until this is decided.

**F3, recorded so it is not rediscovered.** The live wiring of the seam predicate's third
conjunct (`TryFindPartByFlightIdOnVessel(v, node.dockedPartUId) != null`,
`FlightRecorder.cs`) has NO headless pin: the helper takes a live `Vessel`, which xUnit
cannot construct, so mutating that argument to a constant `true` leaves the suite green.
The pure predicate itself is `[Theory]`-pinned over all eight input shapes. The ONLY pin on
the live wiring is H57's negative-control cell
(`StartDockedOrigin_PartnerUndockedBeforeStart_CapturesNoOriginProof`), which docks and
undocks before the recorder starts and must read `outcome=no-external-coupling` - and that
cell PASSED on H57's first flight, so the conjunct is live-proven even though the lane as a
whole red. H57 must be green before this work merges.

## ~~ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE (CONFIRMED 2026-09-02): the start-docked `RouteOriginProof` producer keys on a part-parent condition that a settled dock can never satisfy, so no live recording has ever carried a proof~~ [FOUND BY READING 2026-09-01 while scoping which route flights can be automated, CORROBORATED by the 2026-08-30 rover flight log. CONFIRMED live 2026-09-02 by the H56 probe. FIXED 2026-09-02, code green, NOT YET FLOWN]

**FIXED 2026-09-02.** `FlightRecorder.CaptureStartRouteOriginProofIfDocked` now builds its
candidate list from TWO producers instead of one. The new one is the settled dock seam:
for every `ModuleDockingNode` on the active vessel, `RouteProofCapture.IsSettledDockSeam`
(pure, `[Theory]`-pinned over all eight input shapes) accepts the node when it carries a
`vesselInfo` (stock creates one ONLY in `ModuleDockingNode.DockToVessel`, i.e. a real
cross-vessel dock - never for an editor-preattached pair, never in `DockToSameVessel`), a
non-zero `dockedPartUId`, AND a docked-partner part that still resolves ON THE SAME
VESSEL. Both those fields round-trip through the node's `DOCKEDVESSEL` / `dockUId` save
keys, so the seam survives save/load, which the parent-identity reading never could. A
settled dock is ONE `Vessel`, so the origin partner IS the merged vessel: the candidate
carries `v.persistentId`, `v.situation` and `v`'s body-fixed coordinates, which makes the
M1 endpoint descriptor real-coordinate and surface-typed on a landed pair, and leaves the
pid on the depot half after `Part.Undock` whenever the transport docked INTO the base (the
undocking subtree gets the fresh `Vessel`; the parent side keeps the pid). The OLD
external-parent reading is COUNTED BUT NO LONGER EMITS (2026-09-02 review follow-up, F4):
an earlier draft kept it emitting on the claim that it was "the only reading that could see
an UNSETTLED coupling", which was a hypothesis with no measurement and no decompile behind
it. It produced zero candidates on both hosts that have ever measured it. The COUNT stays
because it is the instrument that proves a captured proof came from the seam and not from
the retired rule, and two committed lanes pin `externalParentCandidates=0`
(`RouteOriginProof seam scan: settledDockSeamCandidates=N externalParentCandidates=M ...`).
The PRELAUNCH short-circuit is untouched: a clamped pad vessel is still not a delivery
origin. TWO THINGS THIS ENTRY DOES NOT CLOSE, both filed above:
ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING (the proof dies on `CaptureAtStop` in
always-tree mode) and ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY (the partner-pid rule and the
depot-side-start false positive). The feature is not usable end to end until both land.

The instrument became the gate. `OriginProofProbe_SettledDockLeavesNoExternalParent` was
renamed `OriginProof_SettledDockCapturesProofFromDockingNode` (same category, same cell id
`origin-probe`, same `OriginProofProbe:` line and therefore the same lane token - the
`RouteDockCapture` count stays 6 and no pinned tally moves). It still measures, but now
asserts three things: `externalParentParts == 0` (the retired predicate must stay dead, so
the proof cannot be coming from it), and then the producer's verdict HOST-AWARE - on a
PRELAUNCH host (H55's `logi-cargo-pad`) the `active-vessel-PRELAUNCH` skip, on any other
host (H56's landed `rover-route-recorded`) `proofCaptured=True outcome=captured`.

ONE CAVEAT, STATED RATHER THAN BURIED. The `RouteDockCapture` cells couple with a RAW
`Part.Couple` (deliberately - it is what lets the real `onPartCouple` classify a
port-to-port pair with no docking FSM in the way, and there is no magnetic acquire to
drive between two parts spawned 15 m apart). Raw `Part.Couple` writes NO docking-node
bookkeeping, so the cell now stamps it first (`StampStockDockBookkeeping`, the three
assignments quoted from the decompiled `ModuleDockingNode.DockToVessel` plus the FSM's own
`dockedPartUId = otherNode.part.flightID`; `otherNode` and the FSM state are deliberately
NOT written, because arming the node-distance event nulls `vesselInfo` straight back out).
That makes this half of the gate a stock-contract EMULATION rather than an independent
measurement. What it still buys is the whole live producer path - walk the parts, find the
node, resolve the partner ON THIS vessel, build the merged-vessel candidate, run the
resolver, populate the descriptor and the manifests - which no headless test can drive.
The non-emulated confirmation is a real player dock, which is what a future manual or
mission-driven subject would add.

STILL OPEN, and deliberately not done here: `RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof`
(category `Logistics`) is a READ-SIDE cell that self-skips until a committed recording
carries a proof. It un-skips by itself once a lane produces one - which moves RVR-1's
whole-pinned `passed=39 skipped=8`, so it is a re-pin that belongs to the commit that
reads that census off a real flight, not to this one.

**CONFIRMED 2026-09-02 by lane H56 (`2026-09-02_0545`), the probe on a LANDED host:** `OriginProofProbe: externalParentParts=0 proofCaptured=False situation=1 outcome=no-external-coupling partnerPid=2507516556` - the resolver reached its candidate walk (no PRELAUNCH short-circuit) after a settled `Part.Couple` and found ZERO externally-parented parts, and every recording start on the docked vessel logged `RouteOriginProof skipped: no external coupling ... candidates=0`. The producer is dead code on a settled dock. The roadmap's manual B4 flight is retired.

**The condition.** `FlightRecorder.CaptureStartRouteOriginProofIfDocked` builds its partner
candidates from parts where `p.parent.vessel != null && p.parent.vessel != v`
(`RouteProofCapture.TryResolveStartDockedOriginPartner` names the same rule). KSP's
`Part.Couple` reassigns `vessel` across the whole absorbed subtree - `GrappleCaptureInGameTest`
asserts exactly that after a live couple - so on any settled docked pair, and after any
save/load of one, every part reads `p.parent.vessel == v` and the candidate list is empty.

**Corroboration.** The rover flight logged `RouteOriginProof skipped: no external coupling ...
candidates=0` at EVERY recording start, including the one at 10:55:00 for the dock-merged
child created while B was docked to A (`logs/2026-08-30_1106_rover-route/KSP.log:18547`).
Zero committed fixtures carry a `ROUTE_ORIGIN_PROOF` node, every Logistics skip roster gives
the same missing-subject reason for `RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof`,
and the only tests of the resolver (`RouteOriginProofCaptureTests`) feed it hand-built
candidate lists - they prove the pure resolver and say nothing about whether the live list
can be non-empty. The design source (`docs/dev/done/logistics-origin-ownership-proposal.md`)
asserts the cross-vessel parent link without a decompile finding behind it.

**Why it has stayed invisible.** Docked-depot origins are resolved today by the M-MIS-5 P2b
mid-tree docked-origin window path (`RouteAnalysisEngine.AnalyzeWindows`), which is what the
`depot-route-recorded` fixture exercises - the start proof was never load-bearing for any
committed route.

**HISTORICAL, kept for the reasoning trail - the entry is CONFIRMED and FIXED; the
instrument named here is now the regression gate.** `OriginProofProbe_SettledDockLeavesNoExternalParent`
(`Source/Parsek/InGameTests/RouteDockCaptureInGameTest.cs`, category `RouteDockCapture`,
driven by `harness/scenarios/H55-route-dock-capture-isolated.toml`) spawns a `dockingPort2`,
couples it into the active vessel, lets it settle, counts the parts satisfying the
producer's OWN predicate through the mirrored pure core
(`RouteDockCaptureMath.IsExternallyParentedPart`, unit-tested against all four shapes), then
starts a recording on the docked vessel and reads the production producer's own log branch
back off the observer channel. It emits one grep-stable line -
`OriginProofProbe: externalParentParts=N proofCaptured=<bool> situation=S outcome=<branch>
partnerPid=P` - and asserts NO VERDICT: it is an INSTRUMENT and passes whenever it measured,
so it cannot pre-judge the question it exists to answer. The lane pins the line with the
values regexed, so a run can neither omit it nor satisfy the token by producing one
particular answer.

**How to read it.** `externalParentParts=0 proofCaptured=False` CONFIRMS this entry: fix the
producer to derive the partner from the docking node's own docked-partner information at
recording start instead of the parent-vessel identity, pin it with a self-provisioning
capture cell in the same category, and the roadmap's B4 manual flight is unnecessary.
Anything else REFUTES it: record the KSP state that yields the link and fly B4 as
originally planned. Do not fly B4 before reading this line.

**FIRST MEASUREMENT, 2026-09-01 (`harness/results/2026-09-01_2206_H55-route-dock-capture-isolated_shots/KSP.log`).
The probe was the one cell of six that passed, and it read:**

    OriginProofProbe: externalParentParts=0 proofCaptured=False situation=4
      outcome=active-vessel-PRELAUNCH partnerPid=108351093

It is HALF an answer, and the halves must not be conflated. `externalParentParts=0` is the
measurement this instrument exists for and it SUPPORTS the suspicion: after a settled
`Part.Couple` of a `dockingPort2` partner into the active vessel, not one part on the merged
vessel satisfies `p.parent != null && p.parent.vessel != null && p.parent.vessel != v`. That
is the predicate the producer builds its candidate list from, so on this evidence the list is
empty for exactly the reason the entry names. What the run did NOT do is exercise the producer
past that point: `situation=4` is PRELAUNCH (the `logi-cargo-pad` host sits clamped on the
LaunchPad), and `TryResolveStartDockedOriginPartner` returns `ActiveVesselPrelaunch` BEFORE it
walks candidates at all - so `outcome=active-vessel-PRELAUNCH` is the PRELAUNCH short-circuit,
not the empty-candidate branch, and the Captured branch remains unexercised. The count is
evidence; the producer's verdict on this host is not.

**HISTORICAL (written after the first, PRELAUNCH measurement): STATUS STAYS SUSPECTED, and the follow-up is a host change rather than a code change.** A
LANDED host reaches the candidate walk (the guard excludes PRELAUNCH only), and one is already
committed: `harness/fixtures/saves/rover-route-recorded`, whose vessels read
`startSituation = Landed launchSiteName = Runway` - the same fact RVR-1's roster records for a
different reason. Adding the `RouteDockCapture` probe to a lane on that host would produce
either `outcome=no-external-coupling candidates=0` (which CONFIRMS this entry outright, since
the walk ran and found nothing) or a `Captured` line (which refutes it). That lane does not
exist yet and is the cheapest remaining step here.

## ROUTE-DISPATCH-COST-BASIS-RESIDUALS: what the transport-subset + ghost-surface costing fix still leaves open [FOUND 2026-09-02 by the pre-merge correctness pass on PR #1597. NONE blocking - every item is a strict improvement over the pre-#1597 state, where the same routes dispatched for FREE - but each names a real follow-up. REPORT-ONLY, fix shapes recorded]

1. **The two snapshot surfaces are not the same instant.** `GhostVisualSnapshot` is captured at
   recording START (`FlightRecorder` start-state node); `VesselSnapshot` at STOP. Preferring
   vessel and falling back to ghost means a staged launch prices the post-separation stack
   in-session and the FULL launch stack after any OnLoad outside SPACECENTER (the
   crew-auto-unreserve sweep nulls the vessel surface; it is skipped at SPACECENTER, so the
   KSC-shown cost and the FLIGHT-charged cost can differ in one session). Fix shape: pick ONE
   surface by contract - the launch stack (ghost/start) is the honest basis for a KSC dispatch
   and survives sweeps and splits - and accept the one-time cost change for existing staged
   routes; the rover shape is unaffected (root carries only the ghost surface, 7410 either way).
2. **An optimizer split moves the vessel surface to the second half and nulls
   `RouteRunManifest` on both halves** (`RecordingOptimizer.TransferTerminalFieldsToSecondHalf`)
   while `SourceRefs` still names the first half - basis flips launch-manifest -> stop-walk on
   the ghost. Same fix as 1 (start surface + start manifest are split-stable).
3. **The subset path latches the FIRST combined member** (`mergedCandidate == null` guard) even
   when it carries no window (the `StartUT >= RecordedDockUT` rule alone), so a windowless
   member ahead of the real dock-merged leaf sends the route to UNCOSTED; an origin-carrier
   member ahead of it would supply the WRONG window. Fix: latch only a member whose window
   matches `RecordedDockUT`, else keep walking. Practically unreachable today - the ghost
   fallback prices the root first on every real tree - which is why it is not fixed here.
4. **`keptParts > 0` is presence, not coverage**: a snapshot matching 1 of N transport pids
   prices one part and still adds the full launch-manifest resource term. Fix: require
   `keptParts == transportPids.Count`, else UNCOSTED (better free-and-logged than plausible-
   and-wrong).
5. **Bare `persistentId` matching inside the subset** (`RouteFundsCalculator.IsRestrictedOut`):
   a same-blueprint transport/endpoint pair shares baked pids, so endpoint parts would be
   billed as transport - the standing craft-baked-pid rule. Upstream `DeriveEndpointPartPids`
   (merged minus transport) has the same blind spot. Fix: gate the subset on the window's
   endpoint set being DISJOINT from the transport set (refuse to price when they overlap).
6. **Staged launches under-charge on the subset path**: the window's transport pid set is the
   craft AT THE DOCK, so staged-away boosters are not priced while their propellant (start
   manifest) is. Item 1's start-surface contract fixes this too.
7. **`Unknown part cost:` is a plain `Warn` per unpriced PART per call** (`RouteFundsCalculator`
   ~88/199), and the ghost fallback makes the walk reachable post-load for every loaded route
   at the ~1 Hz Logistics refresh, where the UNCOSTED early return was rate-limited. A save
   carrying a removed/renamed mod part now warns per part per route per second, and the log
   validator cannot suppress WRN. Fix: once-per-part-name per session (a static seen-set), or
   `VerboseRateLimited`; note the xUnit cost cells currently COUNT these per-call warn lines
   as their proof of which parts were walked, so the tests move with the fix.
8. **Gate and emit recompute independently** (`KscFundsAvailable` and `EmitDispatchDebit` each
   call `ComputeDispatchFundsCostForRoute`); no single-compute-per-cycle capture asserts the
   gated and charged numbers agree. Low probability (one synchronous stack), recorded for
   completeness; `route.KscDispatchFundsCost` persists only the emit value.

Measured coverage note: RVR-4's green run took the root/ghost path (`fallback=0 snapshotSurface=
ghost`); the transport-subset last resort has NEVER executed in flight and is covered by its
eight xUnit cells only.

## RESERVATION-OVERLAY-GAPS: the reservation readout that replaced the dead budget UI is absent in the EDITOR and reports `Reserved: 0` in a genuine deficit [SPLIT OUT 2026-08-29 from RESOURCE-BUDGET-READOUTS-ARE-DEAD when that entry was struck as cleanup-done. Neither gap was part of that cleanup; refiled here so they survive it. **(b) FIXED 2026-09-02; (a) still open** - extending the overlay to a scene where it never showed is a product decision, not a wording fix]

**Fix for (b) (2026-09-02).** `FundsModule` / `ScienceModule` now keep the projection's unclamped
minimum (`GetProjectionMinBalance()`, the value `GetAvailable*()` floors at zero; legacy
unclamped availability when no projection is installed). `CurrencyReservationOverlay`
derives the tooltip through the pure `BuildTooltipFromLedger(runningBalance, available,
minProjected, displayed)`: a healthy pool keeps the two-line `Total / Reserved` form; an
over-committed pool (balance positive, projected minimum negative) keeps `Total / Reserved`
and adds `Short by: <over-commit magnitude>`; a genuine deficit (running balance negative AND
the bar floored at zero) replaces the pair with `Balance: <signed>`, plus `Short by` only when
the projected minimum digs deeper than the current hole (the two numbers nest, so a repeated
magnitude is omitted), because nothing is being held back and `Total: 0 / Reserved: 0` was
reporting the opposite of the truth. LIVE PROOF is authored too:
`CurrencyTooltipLiveInvariantTest.TooltipsReconcileWithTheLiveBar` (category `LedgerGroundTruth`,
2026-09-02) recalcs the live career and parses the rendered tooltips BACK to numbers, asserting
`Total - Reserved` equals the stock widget's value (within one display unit of the format), that
the deficit form appears only under a floored bar, and that a `Short by` line follows the sign of
the projected minimum; those parse-back checks are the load-bearing ones (the cell's first
assertion, that the tooltip equals the pure builder over the same getters, only guards the
getters against drifting within one statement). It rides L2 / L4, whose tallies were re-derived
to total=3, and restores the flight baseline after itself because its recalc patches the live
singletons the sibling ground-truth cell hard-asserts against. The
bar-floored condition matters: `KspStatePatcher`'s
"keep what you earned" drawdown guard can hold the bar at a positive live value while the
ledger runs below zero, and there the tooltip stays bar-anchored (`Total: <bar> / Reserved: 0
/ Short by: <magnitude>`) so it reconciles with the number on screen (the PR #1596 review
caught this). The `Reserved` slot never carries a negative number, per the analysis below.
Pinned by
`CurrencyReservationOverlayTests` (all three shapes, through both the pure builder and the
ledger derivation) and `ProjectionMinBalanceTests` (the modules expose the unclamped minimum
and drop it on `Reset`).

`CurrencyReservationOverlay` is the surface that carries the reserved-vs-available story now
that the Timeline "Resources" section and the main window's "Reserved:" line are deleted. Two
holes in it, both measured while establishing the facts for that cleanup:

**(a) No explanation in the EDITOR.** The overlay idles outside `SPACECENTER` and `FLIGHT`
(`CurrencyReservationOverlay.cs:40-46`). The EDITOR is where funds are actually spent, and it is
exactly where the stock widget's reservation-net number goes unexplained - the player sees fewer
funds than the raw pool holds and nothing says why. Fix: extend the scene gate to `EDITOR`, and
confirm the stock editor's own cost readouts do not then double-subtract.

**(b) `Total: 0 / Reserved: 0` at exactly the wrong moment.** `GetFundsTooltip`
(`CurrencyReservationOverlay.cs:190-198`) computes
`reserved = GetProjectionCurrentBalance() - GetAvailableFunds()` and floors it at zero, and the
`displayed` value it adds to is the bar, which `KspStatePatcher` has already floored at zero. In
a genuine deficit (running balance negative, available floored to zero) both terms are zero, so
the tooltip renders `Total: 0 / Reserved: 0` - reporting nothing reserved at exactly the moment
a deficit is eating the pool. `GetScienceTooltip` has the same shape.

This is NOT a one-liner, which is why the cleanup left it filed rather than taking it - but be
precise about WHY, because the obvious one-liner fails for a reason that is easy to misread.
Dropping the floor does **not** break the algebra: `Total` is computed as `displayed + reserved`,
so with a signed `reserved` the identity `bar == Total - Reserved` still holds exactly, and an
implementer who tests only the arithmetic will conclude the fix works. What breaks is the
**meaning**. `Reserved: -5,000` is not a reservation - nothing is being held back; it is the
projection running below zero, which is a different quantity that happens to be reachable by the
same subtraction. Rendering it in the Reserved slot tells the player the pool is owed a negative
amount, which is worse than the current silence, and `BuildReservationTooltip`'s own doc comment
frames both numbers as a reservation breakdown. The honest number is the over-commit magnitude,
`minProjected`, which today reaches no surface but a Verbose line (`FundsModule.cs:697-702`).
Fix: give the deficit its own tooltip phrasing driven by `minProjected` instead of forcing it
through the Total/Reserved pair.

---

## RESOURCE-BUDGET-COST-HELPERS-ARE-PRODUCTION-DEAD: `ResourceBudget`'s per-recording and per-milestone cost helpers now have test callers only [FOUND 2026-08-29 as the residue of the RESOURCE-BUDGET-READOUTS-ARE-DEAD cleanup. Dead weight, not a defect - low priority]

With `ComputeTotal` / `ComputeTotalFullCost` deleted, their per-item helpers lost their last
production caller: `CommittedFundsCost` / `CommittedScienceCost` / `CommittedReputationCost`,
`MilestoneCommittedFunds` / `MilestoneCommittedScience`, `FullCommittedFundsCost` /
`FullCommittedScienceCost` / `FullCommittedReputationCost`, and `ComputeFacilityUpgradeCost`
(which is itself a documented placeholder returning 0 on every branch). They were kept in that
cleanup because they are pure, cheap and still exercised - and because the cleanup's scope was
the aggregator pair, not a transitive sweep. Coverage is uneven, which matters if the next pass
weighs them by how well they are pinned: the `Full*Cost` trio is exercised from
`RewindLoggingTests`; `Committed*Cost` and `MilestoneCommitted*` have their own direct cells in
`ResourceBudgetTests`; `ComputeFacilityUpgradeCost` has NO cell of its own and is reached only
indirectly, through `MilestoneCommittedFunds_FacilityUpgradedReturnsZero`.

The open question is whether a tested pure-math surface with no production caller earns its
keep. `ResourceBudget` itself must stay regardless: `ParseCostFromDetail` is live
(`Patches/TechResearchPatch.cs:53`). Decide in one pass rather than one helper at a time, and
read `RewindLoggingTests` first - if its two cells re-derive expected values *through* the
production helpers rather than asserting production behavior, that is a second reason to move
them out.

**One pin was lost along the way, deliberately, and is recorded here so it can be rebuilt if it
ever matters.** Among the 31 deleted cells,
`MixedStoreShape_Invariant_TreeChildAppearsInBothCollections` was doing two jobs. Its stated job
was to justify `ComputeTotal`'s TreeId-based flat-list skip, which is why it went with the pair.
Its unstated job was to pin a `FinalizeTreeCommit` SHAPE fact that outlives the pair entirely:
that a tree child appears in BOTH `CommittedRecordings` and `CommittedTrees[i].Recordings`, and
carries a non-null `TreeId`. Nothing asserts that directly any more, and a great many cells lean
on it implicitly - 87 test files call `AddRecordingWithTreeForTesting` or `FinalizeTreeCommit`
(`grep -rl "AddRecordingWithTreeForTesting|FinalizeTreeCommit" Source/Parsek.Tests`), so a
regression in that shape would surface as a scatter of unrelated failures rather than as one
clear red. Not worth a speculative cell today; worth a direct pin in `RecordingStoreTests` the
first time that shape is touched or suspected.

---

## GLOOPS-STANDALONE-WINDDOWN: Gloops UI retired from every mode; extraction to a standalone mod pending [OPENED 2026-08-28]

Product decision (2026-08-28): Gloops becomes a standalone mod later, and Parsek
gradually winds down player-facing ghost/recording-looping surfaces to focus on
gameplay. **Step 1 shipped:** `UiSurface.MainButtonGloops` is retired in EVERY UI
mode (Advanced included) via `UiSurfaceVisibility.IsRetired` — a retirement gate
that outranks the Basic/Advanced decision in `IsVisible` — so the Gloops Flight
Recorder launcher (the window's only opener) no longer draws. Visibility-only:
the recording machinery (`ParsekFlight` gloops paths, `GloopsRecorderUI.cs`, the
Gloops group, saved ghost-only recordings), the design-7.2 close-set entry, and
the edge-case-11 in-progress guard all remain. Pinned by
`RetiredSurfacesAreHiddenInEveryMode` / `AdvancedHidesOnlyRetiredSurfaces`
(`UiComplexityModeTests.cs`); design amendment noted in
`design-ui-basic-advanced.md` (2026-08-28). **Remaining:** the actual extraction
(move `GloopsRecorderUI` + the gloops recorder paths out of Parsek) and deciding
which further looping surfaces wind down next — both unscheduled.

## SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE: the colour-changer cabin-light apply line never fires on the showcase ghosts, so whether the emissive actually toggles is unmeasurable [MEASURED 2026-08-28 on S1.9 reading run 2 (`2026-08-28_2010`): all 25 colour-changer rows spawned meshes, zero `applied color changer cabin light` lines. OBSERVATION, report-only - possibly a real ghost-render gap, possibly Pattern-A discovery correctly finding nothing on these parts]

`ApplyColorChangerLightState` (GhostPlaybackLogic.cs:6993) returns silently when
the ghost's `colorChangerInfos` carries no `isCabinLight` entry for the pid - no
line, no skip reason. On stock-minimal, S1.9's 25 "Part Showcase - Colour
Changer" rows rendered meshes but produced zero apply lines, so either (a) the
ghost visual builder's Pattern-A material discovery resolves nothing for these
parts (a render gap: the cabin-light emissive never toggles on the ghost), or
(b) the parts genuinely carry no Pattern-A cabin light and the showcase
builder's event targeting is aspirational. Distinguishing (a) from (b) needs
either a logged skip reason in the applier (the PART-EVENT-APPLIER-IS-UNLOGGED
fix shape) or a manual eyeball of a colour-changer row mid-window. S1.9
deliberately does NOT pin the token (its header records the measurement).

## SHOWCASE-LOOPFLAG-STRIPPED-AT-LOAD: every part-showcase recording is authored `WithLoopPlayback(true)` and every one has that flag CLEARED on load, so the standing part exhibition does not actually loop in game [MEASURED 2026-08-28 by `S1.9-part-showcase-render` reading run 1 (`2026-08-28_1945`), which red for an unrelated timing reason and turned this up in the same log. REPORT-ONLY, NO product change proposed, NO mechanism blamed - the sanitizer is doing exactly what it was written to do]

The line, verbatim from the collected log:

```
[Parsek][WARN][RecordingStore] SanitizeNonLoopableLoopPlayback: cleared LoopPlayback
on 243 non-loopable recording(s) (debris or pure orbital coast; no Recordings-tab loop
toggle, so the stale flag had no way to be cleared)
```

243 is the whole injected showcase corpus.
`RecordingStore.SanitizeNonLoopableLoopPlayback` clears `LoopPlayback` on any recording
that fails `Recording.IsLoopableRecording`, and a showcase row fails all five of that
predicate's arms: it has no `LaunchSiteName`, its `StartSituation` is not `"Prelaunch"`,
its `SegmentPhase` is none of atmo / approach / surface, its `DockTargetVesselPid` is 0,
and it carries no viewable RELATIVE track. So
`BuildPartShowcaseRecording`'s `WithLoopPlayback(loop: true, intervalSeconds: 0.0)` -
and the same call in every sibling showcase builder - is a NO-OP at playback time. The
rows play their 24 s clip once (30 s for the surface rover) and stop.

WHY IT MATTERS, and why it is filed rather than fixed here:
 - The showcase's whole purpose is to be a STANDING exhibition a human can walk up to
   and look at. A one-shot window that opens 30 s after injection and closes 24 s later
   is a different thing, and the discrepancy is invisible from the builder source, which
   plainly asks for a loop.
 - It also means anyone reading those builders (this lane's author included, at length)
   will derive a self-overlap model of showcase playback that the game does not run.
   That cost one flight here, and is exactly the kind of thing worth writing down.
 - The sanitizer is NOT the defect. Its Warn text even explains its own reasoning ("no
   Recordings-tab loop toggle, so the stale flag had no way to be cleared") - it exists
   to clear flags on populations the UI cannot un-set. Whether showcase rows SHOULD
   satisfy `IsLoopableRecording` (they are neither debris nor an orbital coast), or
   whether the builders should stop asking for a loop they cannot have, is a product
   decision with a real UI surface behind it, and not one a test lane should take.
 - No harness lane depended on the loop before this one, and S1.9 v2 no longer does: it
   pins the sanitizer line as a REQUIRED token, so if this ever changes the lane reds
   and names it instead of silently changing meaning.

## PART-EVENT-APPLIER-IS-UNLOGGED: the ghost part-event applier writes no per-family log line, so no automated test can distinguish "the recorded event was applied to the ghost" from "the event was silently skipped" for most part families [FOUND BY READING 2026-08-28 while authoring `S1.9-part-showcase-render`, from the source alone - NOT measured on a flight. OBSERVABILITY GAP, REPORT-ONLY. No product change made: this is the hot path under 243 simultaneous ghosts and the fix is a product decision, not a test-lane one]

`GhostPlaybackLogic.ApplyPartEvents` is the sole apply path for flight, KSC and
flight-preview ghosts, and it emits exactly ONE aggregate line per call -
`Applied N part events for ghost #N (evtIdx now N)`, VerboseRateLimited. Its per-family
handlers emit nothing at all: `ApplyLightPowerEvent`, `ApplyLightBlinkModeEvent` /
`RateEvent`, `ApplyDeployableState` (which is also the ladder / drill / deployed-science
/ animation-group / inflatable / radiator path), `ApplyDeployableBrokenState`,
`ApplyCargoBayState`, `ApplyJettisonPanelState`, the inline `FairingJettisoned` arm,
`SetEngineEmission` / `SetRcsEmission` themselves, `ApplyParachute*Event`,
`ApplyRoboticEvent` on its normal path, and `ApplyInventoryPart*Event`.

The only per-family apply evidence that exists today is `Part pid=N: applied heat
level <Hot|Medium|Cold>` (the ThermalAnimation family), `Part pid=N: applied color
changer cabin light state=<True|False>`, and `FX magnitude (engine|rcs) pid=N midx=N
power=...` (which is suppressed entirely when nothing was scaled, so a match really is
proof an emitter moved).

WHAT IT COSTS, concretely: `S1.9-part-showcase-render` renders a showcase row for
lights, gear, bays, fairings, panels and chutes and can prove the GHOST MESH was built
for each - but it cannot claim D7 `lights`, `gear`, `bays`, `fairing`, `chute-cut` or
`panels-antennas-radiators`, because CLAIM-IS-NOT-GATE requires a required token per
cell and no token exists. Six registry cells stay unclaimable by any log-reading lane
until this changes. The house rule is explicit that this should not be so
(`.claude/CLAUDE.md`: "Every action, state transition, guard condition skip, and FX
lifecycle event MUST be logged ... if it didn't get logged, it didn't happen").

The shape a fix would have to respect, so nobody reaches for the obvious one: this runs
per ghost per frame, and the showcase alone puts 243 ghosts in the scene at once with a
new overlap primary every 5 s. A bare per-event `Verbose` would be a log flood. The
convention the codebase already has for exactly this is `VerboseRateLimited` with a
per-part-per-family key - the shape `Part pid=N: applied heat level ...` already uses -
or a per-family counter folded into the existing aggregate line
(`Applied N part events ... [lights=2 deployables=1 bays=1]`), which costs one line per
ghost per interval rather than one per event.

## FIXTURE-DUNA-PARK-PROBE-CANNOT-RETURN-TO-KERBIN: the DD1 probe every committed Duna-parked fixture carries is ~550 m/s short of a Kerbin return, so the reserved `B29-duna-kerbin-return` lane could not be flown as specified [MEASURED 2026-08-26 off `fixtures/saves/duna-park-probe/persistent.sfs` while opening B29's Phase-0 door. FIXTURE PROPERTY, REPORT-ONLY - never a Parsek defect and never a spec defect; it blocked one lane's PRODUCTION, not any product question. ROUTED AROUND the same day by re-scoping B29 to depart Jool; see the second entry below]

THE ARITHMETIC, derived from the fixture's own bytes rather than from a delta-v map:

  CRAFT     `DD1 Duna Direct Probe`, type Relay, sit ORBITING, REF 6 (Duna), at
            UT 9,160,396.7636916172. 14 parts, probe (NOT crewed). Control and
            comms are FINE - `ctrl = True`, ModuleCommand, ModuleDataTransmitter
            `canComm = True`, three RA-5 relays, MechJebCore `isEnabled = True` -
            so the refusal is purely propellant.
  PARK      SMA 1,038,214.9499945882, ECC 0.0012696218422829151 = a 718.2 km
            circular Duna orbit, 2.166% of Duna's SOI.
  AVAILABLE dry 1.6300 t (sum of the PART `mass` fields; `modMass` sums to 0),
            LiquidFuel 56.214143897950763 + Oxidizer 68.706157991791599 = 124.920
            units = 0.62460 t, mass ratio 1.38319. The LV-909 reads
            `maxThrust = 60` and `key = 0 345` in its stock cfg, so
            345 * 9.80665 * ln(1.38319) = **1,097.5 m/s**.
  NEEDED    585.0 (ejection from the 718 km park, v_inf 826.1) + 1,060.5 (Kerbin
            capture AND circularization at 100 km, v_inf 918.3) = **1,645 m/s**,
            before a single correction round.
  VERDICT   ~550 m/s short, 50% over budget.

IT IS STRUCTURAL RATHER THAN TUNABLE, which is the part worth keeping: a
lower-periapsis two-burn Oberth ejection saves 12 m/s (573.0 against 585.0), and
aerocapture is not a route - the probe carries no heat shield and no aerobrake verb
exists in the composed verb set. Capturing into a bound ELLIPSE instead of
circularizing IS affordable on this craft (804 m/s total, a 24% margin), but the
absolute reserve is 214 m/s, which one mis-planned correction round consumes.

THE ROADMAP ALREADY KNEW AND THIS CONFIRMS IT RATHER THAN DISCOVERING IT: the
B-range roster said "every committed Duna-parked fixture carries the DD1 probe with
only ~1,180 m/s - short of a Kerbin capture" and reserved `B31` as the Kerbin -> Duna
SETUP lane. The 1,097.5 here refines that ~1,180 (7% apart; both far short of 1,645)
and derives it from the bytes. `B31` remains reserved and is now a when-wanted
breadth point rather than a blocker - see the roadmap's rewritten roster entry.

---

## TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS: on a 32.6 Ms loop span every ghost proto settles onto the recording's FIRST segment, 36 of 41 of them having been CREATED on a different orbit - and the TRANSITION is now caught in the act [MEASURED 2026-08-27 by `V20M-jool-kerbin-player-loop` reading run 1 (`2026-08-27_1828`, PARSEK-FAIL(expectation) on exactly this - its own destination-frame render pin), **and CORROBORATED THE SAME DAY BY `V20T-jool-kerbin-ts-arrival` reading run 1 (`2026-08-27_1857`, PARSEK-FAIL(anomaly) `icon-teleport x3`), which caught the RE-SEED ITSELF rather than its end state**. REPORT-ONLY, NO MECHANISM CLAIMED, NO product change proposed. Same family as MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP, which it sharpens in three ways]

**WHAT WAS MEASURED**, off `logs/2026-08-27_2129_V20M-jool-kerbin-player-loop/KSP.log`, on
`fixtures/saves/kerbin-return-recorded` (ONE recording, TRACK_SECTION span 32,606,575.774644222 s,
`overlapCadence` = spanDur/20 = **1,630,328.788732211 s**, twenty concurrent overlap instances):

- **41 `phase=body-orbit surface=ProtoOrbitLine` reads, ALL of them `body=Jool sma=590325785
  ecc=0.0000`** - the recording's FIRST orbit segment (the Jool park, seg#0) - at every one of the
  ten jump epochs and for every instance.
- **Every pid emitted EXACTLY ONE sample, all `from=[(first)]`.** The probe never observed a single
  proto change orbit across ten jumps.
- **THE CREATION SIDE IS CORRECT PER INSTANCE AND THE READ-BACK IS NOT.** Matching
  `Created ghost vessel ghostPid=N` against `phase=body-orbit pid=N`: cycles 0/19/20/39/40 were
  created on the Jool park, 16-18 and 36-38 on the Jool escape hyperbola
  (`sma=10246978796`), 2-15 and 22-35 on the Sun transfer (`sma=40960547563`), and cycles 1 and 21
  on seg#13, the LAST Sun coast (`sma=40758641878`, `segmentUT=60364419.7-60366070.3`) - exactly
  the segment the lane's own arithmetic puts them on at the epoch they were created. **36 of the 41
  protos were created on one orbit and read back at end of frame on another.** The five that
  "match" are exactly the five seeded on the Jool park anyway.

**AND THE TRANSITION IS NOW CAUGHT IN THE ACT, WHICH V20M's END-STATE READING COULD NOT DO.**
`V20T-jool-kerbin-ts-arrival` reading run 1 (`2026-08-27_1857`, collected at
`logs/2026-08-27_2158_V20T-jool-kerbin-ts-arrival`) drove the SAME fixture to the SAME coast-dwell
epoch through the TRACKING STATION and came back `PARSEK-FAIL(anomaly)` with
`hitCounts {icon-teleport: 3}` and nothing else - every other row green, all thirteen of its
required tokens matched. All three raises are at the coast dwell (frames 7998 x2 and 8014,
`currentUT` 94,621,776.560 / .820) and each one names the orbit pair it jumped between:

```
reason=icon-teleport TELEPORT dPos=334981793m = 24210456x expected(14m)
  | fromOrbit=[Jool|10246978796|0.9424] toOrbit=[sma=590325785 ecc=0.0000] body=Jool
  | lineActive=True drawIcons=OBJ dPosWorld=334981874m warpRate=1 dt=0.0167
```

(the other two are `dPos` 1,623,652,431 m and 2,935,994,339 m, same from/to pair).
**`10246978796 / 0.9424` IS THE POST-ESCAPE JOOL ELLIPSE** - seg#2-4 of this recording to the
digit - **AND `590325785 / 0.0000` IS SEGMENT ZERO.** So these are three protos being re-seeded
OFF their own correct per-instance segment ONTO segment zero, mid-dwell, surfacing as position
teleports.

WHAT THAT ADDS, beyond confirming the end state V20M measured:

3. **THE DIRECTION IS PROVEN, NOT INFERRED.** V20M could only pair a creation line against a
   read-back and infer that a settle happened in between. Here the settle is a single logged
   event with both orbits on it, and it runs FROM the correct segment TO segment zero -
   creation-correct / settle-wrong, demonstrated.
4. **IT HAPPENS AT DWELL TIME, NOT ONLY ACROSS A JUMP.** All three fire during the forty-tick TS
   dwell at a fixed epoch, at **`warpRate=1 dt=0.0167`** - one frame of ordinary 1x time. So the
   trigger is not "a large TimeJump" as the parent family's title supposes; a proto can settle
   onto segment zero while the clock simply runs.

**AND THESE THREE ARE A PER-RAISE WARP ATTRIBUTION DATUM THE OPEN `MapRenderProbe` THRESHOLD
ENTRY EXPLICITLY ASKS FOR** (see "`MapRenderProbe`'s `icon-jump` / `icon-teleport` threshold looks
over-sensitive above warp1000" below): that entry's owed work is bucketing raises by the warp
regime of the frame that raised them, on the hypothesis that the threshold does not survive rails
warp. These three are at **1x**, and each carries a from/to orbit pair showing a real re-seed - so
at least this population is NOT a threshold false positive, and the token has at least two
distinct producers. `V20T` therefore tolerates `icon-teleport` BARE citing `2026-08-27_1857`,
with the ceiling (6 = 2x measured) argued as prose per the V24W/V25M convention because
`test_no_committed_spec_arms_a_count_budget` holds the budget mechanism inert suite-wide.

**AND A HOST-LEVEL STATEMENT THE THIRD ROUND SETTLED, WHICH IS THE SHARPEST THING ON THIS
ENTRY: THE FLIGHT HOST'S `ProtoOrbitLine` LENS IS SEGMENT-ZERO-ONLY INDEPENDENT OF JUMP
ORDER.**

The experiment that settled it was a deliberate spec-shape round rather than an argument.
`V20M`'s jump table was REORDERED coast-epoch-first (round 4) on the mechanism above -
protos are created once, at the first jump that brings them into existence - specifically
to manufacture a Kerbin-framed FLIGHT proto and see whether the lens would then report it.
Reading run 3 (`2026-08-27_1925`, PASS attempt 1) produced exactly that and the lens still
did not report it:

- **The creation census moved by exactly one instance, in the predicted direction.**
  Seam-first (runs 1-2): 5 Jool-park / 6 Jool-escape / 28 Sun / **2 on seg#13** / 0 Kerbin.
  Coast-first (run 3): the same, except **1 on seg#13 and 1 on
  `segmentUT=60366107.0-60392908.2` = seg#16, the KERBIN ARRIVAL COAST**, with
  `referenceBody=Kerbin` x1. The instance that moved is cycle 1, born at the new first
  jump; cycle 21, born at the untouched cycle-2 seam bracket, stayed on the Sun side - an
  IN-RUN CONTROL, one flight carrying both orders over one fixture.
- **The render side confirmed it too**: `phase=GhostCreated surface=ProtoIcon
  pid=1578985464 ... body=Kerbin scene=FLIGHT` printed once, at that jump.
- **And `phase=body-orbit surface=ProtoOrbitLine` printed 41 lines, ALL
  `body=Jool sma=590325785`** - segment zero - on the very run that contained a
  Kerbin-CREATED FLIGHT proto.

So the creation side is Kerbin-framed and the end-of-frame lens is not, on the same run,
for the same recording: **the difference between the two hosts is the HOST, not the jump
order.** The corpus behind the statement is **105 FLIGHT-era `phase=body-orbit` samples
across three runs, two jump orders and four forty-tick dwells** (41 + 41 on V20M runs 1
and 3, 23 in V20T's FLIGHT prelude), not one of which reads anything but segment zero -
against the TS host's 36 samples in the same corpus carrying the real frames, including
the only `body=Kerbin sma=-392275` line anywhere in it.
**NO MECHANISM IS CLAIMED AND NO PRODUCT CHANGE IS PROPOSED.** What it cost, and what it
bought: `V20M` cannot carry a `ProtoOrbitLine` destination pin at all, so the flight-map
third of roadmap G2's bar is NOT discharged on a rendered-orbit token and its D14 `kerbin`
cell rests on the `GhostCreated` ProtoIcon form plus the seed-side token; `V20T`
discharges the TS third on a rendered-frame token. That asymmetry is now a measurement
rather than a suspicion, and it is why the pair CANNOT share one negative control.

**WHAT IT SHARPENS ABOUT THE EXISTING FAMILY** (`MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP`),
with NO mechanism claimed for either point:

1. **THE FRAME EVERY PROTO SETTLES ON IS SEGMENT ZERO, NOT ITS OWN CREATION FRAME.** The family
   entry describes protos reverting to the frame they were CREATED in. Here cycle 1 was created
   `body=Sun sma=40758641878 referenceBody=Sun` and read back `body=Jool sma=590325785`. "Creation
   frame" and "segment zero" coincide on every prior subject because those lanes' jumps land near
   the start of a short span; on a 32.6 Ms span they separate, and they separate for 36 of 41.
2. **IT IS PERMANENT UNDER THE INSTRUMENT, NOT TRANSIENT.** V17M measured the reversion as a
   transient that self-corrects at the next distant epoch. With a 1,630,328.8 s overlap pitch every
   jump in a ten-jump table crosses many re-arms, so there is no reachable jump epoch at which a
   proto reads anything but segment 0.

**THE HONEST SPLIT, AND BOTH HALVES MATTER.**

- **INSTRUMENT LIMITATION.** The M-A2 seam grammar has NO WARP VERB: `TimeJump` epoch-shifts the
  `Planetarium` clock and stops warp. So "let the clock run live across the Sun->Kerbin window
  entry and watch the protos track their own segments" is not expressible today, and **a reading
  re-pin must not add product code to make its own pin reachable.** A player at live warp re-arms
  instances one at a time and crosses boundaries with the clock running; whether the render is
  correct in THAT regime is NOT measured here and this instrument cannot measure it on a subject
  whose overlap pitch is 1.63 Ms. **NOTHING BELOW IS A CLAIM THAT LIVE PLAY IS BROKEN.**
- **PRODUCT OBSERVATION.** A proto whose creation line reads `referenceBody=Sun sma=40758641878`
  and whose end-of-frame orbit reads `body=Jool sma=590325785` is a divergence the product
  produced; the instrument only made it visible. Note also that
  `MapRenderTrace.ReconcileLineState`'s `decision-vs-truth` reconcile did NOT fire
  (`anomalySweep hits=[] counts={}`) - it compares line/icon STATE, not the orbit key, so this
  particular divergence has no existing anomaly surface at all.

**CONSEQUENCE ALREADY TAKEN, and it is a re-pin rather than a fix.** `V20M`'s destination-frame
render token (`phase=body-orbit surface=ProtoOrbitLine .*body=Kerbin`) is unreachable at EVERY
epoch on this subject, so it is kept verbatim in that spec's header as a refuted pre-registration
and replaced in `required` by the seed-side
`OrbitReseed] TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch: body=Kerbin` plus an
anti-emptiness ghost floor and a load-time endpoint premise. `V20T`'s
`phase=GhostCreated surface=ProtoIcon ... body=Kerbin scene=TRACKSTATION` pin was re-cut the same
way BEFORE it flew, because the same run measured 41 ProtoIcon lines splitting Jool 11 / Sun 30
with ZERO Kerbin over the shared `GhostMapPresence` creation path. **THAT IS A DEMOTION FROM A
RENDERED-FRAME CLAIM TO A SEED-SIDE ONE, and the rendered-frame Kerbin claim roadmap G2 asks for
STAYS OWED** - by `V20K`, or by whatever future run can reach live-frame protos on a long-pitch
subject.

**WHAT WOULD CLOSE THIS, none of it proposed here:** a seam verb that advances the clock at warp
rather than epoch-shifting it; or a shorter-span Kerbin-arrival subject whose overlap pitch is
small enough that a jump can land between re-arms; or an orbit-key half added to the
`decision-vs-truth` reconcile so the seed-versus-truth divergence raises on its own instead of
being found by hand.

---

## B29-KERBIN-RETURN-V20K-KSC-LANE-OWED: the KSC host third of G2's planet-to-Kerbin close [OPENED 2026-08-26 on branch `b29-duna-return` as B29-JOOL-KERBIN-RETURN-AUTHORED-NEVER-FLOWN. FLIGHTS 1-2 FLOWN 2026-08-27 (both INVALID, both calibration reads); RE-SCOPED ONTO THE PARENT-RELAY MODE the same day; **FLIGHT 3 PASS ATTEMPT 1 the same day** - the subject EXISTS, harvested as `fixtures/saves/kerbin-return-recorded` (one recording, 739 points, seams Jool->Sun / Sun->Kerbin, Orbiting-at-Kerbin terminal). **RE-HEADED 2026-08-29 ONTO THE SOLE RESIDUAL**: both the old name and the old `REMAINING: the V20 lanes` clause were false - the producer flew three times, and `V20M-jool-kerbin-player-loop` + `V20T-jool-kerbin-ts-arrival` shipped DISCIPLINE-COMPLETE and ARMED in PR #1548 (merged 2026-08-27). WHAT REMAINS IS ONE THING: `V20K`, the KSC host lane over these same bytes, which has NO spec file anywhere in the tree - corroborated by `docs/dev/autotest-roadmap.md` and by PR #1548's own body. The three deferred operator decisions and the operator -> nightly promotion calls are explicitly NOT debt. TODO, not a defect]

`B29-jool-kerbin-return` is committed: spec, mission shell, schema, registration
cells and a bare `[expectations.renderComposition]` declaration. FLIGHT 1
(2026-08-27) red on an inherited moon-calibration correction cap (200 -> 450, the
lane's own audit note). FLIGHT 2 flew the corrected cap to a real Kerbin SOI entry
and measured the plain planner's plan UNFLYABLE from the 590.3 Mm Jool park: the
ejection asymptote delivered an ecc-12.535 arrival (v_inf ~5,116 m/s against the
Hohmann's 2,713) whose honestly-priced capture was 3,625.035 m/s on ~2,000 in the
tank. The lane is now B26's TWO-STAGE PARENT-RELAY from the first planet park in
the family (Jool joined `mlib.STOCK_BODY_GRAVITY`): a 272.38 m/s mlib-computed
escape at the park periapsis, stage-2 Sun-frame Hohmann to Kerbin at the next
window (nominal 1,756.23 m/s), two corrections, and the 1,188.07 m/s elliptical
capture into the 150,000 x 6,000,000 m Kerbin ellipse before committing the tree.

WHAT WAS OWED, in order: the first flight; then the harvest; then `V20M` / `V20T` /
`V20K` authored off the HARVESTED bytes. The V lanes were deliberately NOT committed
ahead of the flight - the V21/G3a lesson is that a lane written against predicted
bytes costs re-pin rounds when the subject can be flown first.

**THE FIRST TWO OF THOSE THREE NOW EXIST (2026-08-27).** `V20M-jool-kerbin-player-loop`
(flight-map host) and `V20T-jool-kerbin-ts-arrival` (Tracking-Station host) are
committed, authored entirely off `kerbin-return-recorded`'s bytes, and both are pure
READING-RUN specs: nothing armed, no `gating = true` anywhere, no routing token in
`required`, `[expectations.renderComposition]` declared BARE with all three tracers on,
and `[expectations.rewind]` / `[expectations.recordings.structure]` report-only. The
fixture is registered in `test_saveparse.RECORDED_FIXTURES` and both lanes in
`test_hlib`'s operator-tier inventory and `RENDERCOMPOSE_DECLARER_SPECS`.
The derivations worth naming, because a re-harvest would move them:
  * SPAN. The unit reads the TRACK_SECTION envelope
    [27,787,321.139510822, 60,393,896.914155044] = **32,606,575.774644222 s**, with the
    span-end trap re-derived FRESH for this subject at 0.020 s / **2.520 s** rather than
    inherited from V19M's 0.020 / 1.360. `overlapCadence` = spanDur/20 =
    **1,630,328.788732211 s**, so the 20x cap beats the 30 s default by 54,344x. It is
    the LONGEST-SPAN loop subject the program has carried - 3.54 Kerbin years, 2,719x
    V19M's.
  * ANTI-VACUITY, and this is the one a reader should check first. The visited set
    {Jool, Sun, Kerbin} LOOKS nested-SOI - the Sun has TWO visited children - but
    `NestedSoiSubtree.FindNestedRoot`'s self-reference guard rejects the Sun as a root,
    and its own comment names live `["Kerbin","Sun","Duna"]` (the mirror of our sequence)
    as the reason that guard exists. So there is no fail-closed root-frame render, the
    proto lenses are intact, and V17M's TracedPath-shadow workaround is deliberately not
    used. The pins are `phase=body-orbit surface=ProtoOrbitLine .*body=Kerbin` (V20M) and
    `phase=GhostCreated surface=ProtoIcon pid=\d+ .*body=Kerbin scene=TRACKSTATION`
    (V20T). They are the most FALSIFIABLE destination pins in the program: the Kerbin
    window is 0.0853% of the span against a launch pitch 58.6x wider than it, so the
    derived census at every observation epoch is Kerbin 1 / Sun 14 / Jool-escape 3 /
    Jool-park 2 - ONE ghost of twenty - and on the TS half that one is the OLDEST
    instance, hence the LAST to spawn under the 2-per-tick throttle, which is what makes
    the forty-tick dwell load-bearing rather than hygiene.
  * ROUTING, pre-registered and gated on nothing. R2 is the deepest any committed subject
    has reached into `ReaimClassifier.Classify`: the arrival scan SUCCEEDS at seg#14
    (the door V19M's two-body subject could not open), and the walk predicts a decline at
    the partial-transfer departure gate on a string no committed lane has printed -
    `transfer departs from a heliocentric parking orbit or mid-course correction
    (deferred); staying faithful` - refused three independent ways by
    `IsHeliocentricParkingDeparture` (the Sun predecessor traverses 0.010649 rev of its
    own period so no closed park run is detected; ecc 0.16402257 > 0.1; sma 13.28% off
    Jool's own heliocentric value against a 10% tolerance). A commissioning prediction is
    corrected in the spec rather than silently applied: the `wholeRevs >= 1` conjunct is
    scoped to COMMON-ANCESTOR runs, NOT to the recording's Jool park (whose real
    0.702186-rev arithmetic is recorded as a subject property, not as a gate).
  * OPTIMIZER. `recordings.count` is WIDE at {1,3} because there are TWO splittable body
    seams, and the Sun->Kerbin one is the FIRST in the corpus whose predicted cohesion
    depends on `ShouldKeepCohesiveCrossBodyExoCoast`'s SECOND disjunct (ExoPropulsive ->
    ExoBallistic, kept whole only because both sections are OrbitalCheckpoint-framed).

**V20M READING RUN 1 HAS FLOWN: `2026-08-27_1828`, PARSEK-FAIL(expectation), attempt 1,
wall 74 s, EXACTLY ONE mismatch - its own destination-frame render pin - and every other
verifier green.** Every other pre-registration held, most of them to the digit: spanDur
echoed 32,606,575.774644222; the seeded anchor landed 0.006 s out
(`phaseAnchor=60393899.994155549`, the closest seed in the V program to date); the two
`relaunchUt=` echoes differ by exactly one span; NO jump UT moved and every one landed
within 0.006 s of its intended replay offset; the routing measured **R3** with the
predicted decline string VERBATIM (`transfer departs from a heliocentric parking orbit or
mid-course correction (deferred); staying faithful` - never printed by any committed lane
before, and reached only because this is the first subject whose arrival scan succeeds),
the predicted `off=32578749.191816326` to the digit, and `P` = Kerbin's own solar period;
and the optimizer printed `evaluated=2 ... exoCoastBodyChangeKept=2
splittableButRejected=0`, **the first measured exercise of
`ShouldKeepCohesiveCrossBodyExoCoast`'s SECOND disjunct at a real seam**. What missed is
the anti-vacuity pin, and the mechanism has its own entry above
(TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS): every proto
reads back on the recording's FIRST segment at every reachable epoch, and 36 of 41 were
created on a different orbit than the probe read.
**BOTH LANES WERE RE-PINNED IN ROUND 2** - V20M off its own run, V20T PRE-EMPTIVELY off
its sibling's, because the same measurement kills its ProtoIcon `body=Kerbin` pin too
(41 ProtoIcon lines, Jool 11 / Sun 30, ZERO Kerbin, over the shared `GhostMapPresence`
creation path). Both refuted pins are KEPT VERBATIM in their headers; both lanes now pin
the seed-side `OrbitReseed] ... body=Kerbin` token plus an anti-emptiness ghost floor and
a load-time endpoint premise. **THAT IS A DEMOTION AND IT IS RECORDED AS ONE: the
rendered-frame Kerbin claim roadmap G2's bar asks for is NOT discharged by either lane and
STAYS OWED.** Both specs keep READING-RUN posture - nothing armed, no window tightened,
the measured `exoCoastBodyChangeKept=2` recorded rather than promoted.

**BOTH OF THOSE FLEW ON 2026-08-27.** `V20M` reading run 2 (`2026-08-27_1856`) is a **PASS
attempt 1, wall 73 s, every verifier PASS or SKIPPED** on the re-pinned nine tokens - so the
round-2 replacements are measured-reachable and that lane is ARM-READY. `V20T` reading run 1
(`2026-08-27_1857`) is a **PARSEK-FAIL(anomaly), attempt 1, wall 61 s** with `icon-teleport x3`
the ONLY hit and everything else green, including all thirteen required tokens - a
pre-registered correct catch, now tolerated by name with that run beside it (ceiling 6 = 2x
measured, argued as prose because the budget mechanism is inert suite-wide). It also answered
three pre-registered questions at once: the endpoint-tail seed is NOT FLIGHT-only (45 hits in
TS), the TS init walk took READING B, and `icon-off-orbit` was SILENT - a third confirmation
for self-overlap at a parameter value no prior lane reached.

**AND IT CORRECTED ROUND 2's ONE MISTAKE.** The pre-emptive removal of V20T's
`body=Kerbin scene=TRACKSTATION` proto pin was made on V20M's FLIGHT-only evidence and was
WRONG: V20T's own run matched it, because its single jump IS the coast epoch where instance 1
is Kerbin-framed (its creation census carries `segmentUT=60366107.0-60392908.2`, seg#16, the
Kerbin arrival coast), whereas V20M creates every proto at its seam-bracket leg on the Sun side
and never re-creates them. The pin is RESTORED. **SO THE PAIR SPLITS ON G2's BAR**: V20T
discharges the TS third on a RENDERED-FRAME token; V20M's flight-map third does not, and its
`kerbin` cell rests on the seed-side token. Re-ordering V20M's jumps coast-epoch-first is a real
candidate for a future round and deliberately not taken after a green run (S4.1).

**V20T READING RUN 2 (`2026-08-27_1913`) THEN FLEW GREEN**: PASS attempt 1, wall 61 s, every verifier
PASS or SKIPPED with the tolerated token, `expectations mismatches=0` over all thirteen required
tokens - and the `icon-teleport` count RECURRED AT THE IDENTICAL 3, so its ceiling (6 = 2x measured)
now stands off a pair rather than one sample. **BOTH LANES NOW HAVE GREEN READING RUNS AND BOTH ARE
ARM-READY.**

**ROUND 4 (2026-08-27) THEN REORDERED V20M's JUMP TABLE COAST-EPOCH-FIRST**, as a deliberate new
reading round with its own pre-registration rather than a change smuggled into an arming pass. Every
UT is unchanged; cycle 1 now runs COAST -> dwell -> TAIL -> Watch and cycle 2 keeps the OLD
seam-first shape untouched as an IN-RUN CONTROL, so one flight will carry both orders over one
fixture. The cycle-1 seam bracket is traded away because `TimeJump` refuses a backward jump and
cycle 1's seam epochs precede its coast epoch - ten jumps become seven, 104 steps become 101. The
prediction is pre-registered: cycle 1 creates >= 1 instance on seg#16 (the Kerbin arrival coast) and
cycle 2 creates none, and the restored rendered-frame token is
`phase=GhostCreated surface=ProtoIcon pid=\d+ .*body=Kerbin scene=FLIGHT`, measured by V20T in its
OWN FLIGHT prelude at exactly this lane's new first jump.
**ONE THING THE ROUND-4 BRIEF ASKED FOR WAS DELIBERATELY NOT DONE, ON EVIDENCE**, and it is a
finding in its own right: `phase=body-orbit surface=ProtoOrbitLine .*body=Kerbin` was NOT restored,
because splitting every `phase=body-orbit` sample in the two collected runs by SCENE gives **64
FLIGHT-era samples (41 on V20M run 1, 23 in V20T's FLIGHT prelude), across two jump shapes and two
long dwells, and NOT ONE reads anything but segment zero** - even though one of those V20T protos
was CREATED Kerbin-framed - while the TS era of the same log carries 36 samples with the real frames
including the corpus's only `body=Kerbin sma=-392275` line, from the TRACKSTATION proto. **THAT LENS
LOOKS SCENE-BOUND RATHER THAN ORDER-BOUND**, so S1.4 does not authorise the token for a FLIGHT lane;
it is instead the open question the round-4 run measures, unrequired and unforbidden, with both
outcomes named in the spec. If it stays silent with a seg#16 creation present, "the FLIGHT host's
ProtoOrbitLine lens is segment-zero-only independent of jump order" is a sharp host-level statement
no committed lane has yet made, and it belongs on the entry above.

**THE ROUND-4 RUN FLEW AND BOTH PRE-REGISTRATIONS LANDED**: `2026-08-27_1925`, PASS attempt 1,
wall 71 s, every verifier PASS or SKIPPED. The creation census gained exactly ONE instance on
seg#16 (the Kerbin arrival coast) while the untouched cycle-2 seam bracket kept its instance on
the Sun side - the in-run control working - and
`phase=GhostCreated surface=ProtoIcon ... body=Kerbin scene=FLIGHT` printed once, at the cycle-1
coast jump. The open question resolved to its SECOND branch and is written up on the entry
above: 41 `phase=body-orbit` lines, all segment zero, on a run containing a Kerbin-CREATED
FLIGHT proto - **the FLIGHT host's ProtoOrbitLine lens is segment-zero-only independent of jump
order**. Holding that token out of `required` at round 4 was therefore correct.

**ROUND 5 IS THE ARMING PASS AND IT IS DONE (2026-08-27).** Both lanes armed off their OWN green
bytes - V20M off `2026-08-27_1925`, V20T off `2026-08-27_1913` - with `[expectations.rewind]`
and `[expectations.recordings.structure]` both `gating = true` on the measured facets (rewind
all 0; trees {1,2} keeping V2's duplicate-writer width, committedTrees / recordings {1,1},
terminalStates {Orbiting: min 1}) and `[expectations.recordings] count` tightened {1,3} ->
{1,1}. Every window was already met by both lanes' runs, so the arming re-pinned nothing and
moved no verdict. V20M ALONE promotes the measured
`Split summary: .*exoCoastBodyChangeKept=2 splittableButRejected=0` into `required` while V20T
pins the COUNT - one gate per fact across the pair. NO routing token is promoted and NO D11
cell is claimed; `renderComposition` stays declared-bare pending the operator's tier-cadence
decision. Both lanes are in `test_hlib`'s `ARMED_ALLOWLIST` with the arming rationale.

**ALL FOUR OF THOSE FLEW ON 2026-08-27 AND BOTH LANES ARE DISCIPLINE-COMPLETE.**
  * V20M ARMED RE-FLIGHT `2026-08-27_1938`: PASS attempt 1, wall 75 s, `saveParse PASS
    gating=True armed=['rewind','recordings.structure'] mismatches=[]` with all eleven required
    tokens matched - so the arming moved no verdict and re-pinned nothing, measured rather than
    argued.
  * V20T ARMED RE-FLIGHT `2026-08-27_1939` -> `_1940_a2`: PASS on attempt 2, wall 119 s,
    `note=flakedThenPassed`. **THE FLAKE IS LEDGERED HONESTLY BECAUSE IT IS THE RETRY CONTRACT
    WORKING**: attempt 1 was INVALID `driver-verdict-mismatch` on ONE step of 56 - `LoadGame
    id=0014 expect=OK verdict=REJECTED`, log reason `recording-active` - which is the EXACT class
    the spec pre-registers twice (the re-kill-pair paragraph's V5 promotion-recorder re-arm race,
    and `[retry]`'s pricing of it). Driver-INVALID, never PARSEK-FAIL: it reached no verdict about
    the product and every save-reading verifier was SKIPPED.
  * BOTH NEGATIVE CONTROLS RED ON DEMAND, each on its OWN lens, each with EXACTLY ONE mismatch and
    every other verifier row clean **including the now-gating `saveParse` (PASS, gating=True)** -
    the pairing that proves the red is on the RENDER PIN and not on the evaluator:
    `2026-08-27_1941` on `phase=GhostCreated surface=ProtoIcon pid=\d+ .*body=Duna scene=FLIGHT`
    and `2026-08-27_1942` on the `scene=TRACKSTATION` form (with the body-free TS floor left
    untouched, so the red lands on the BODY CLAUSE ALONE). Both specs reverted and verified clean.
  * The class matrix row and the G2 gap entry in `docs/dev/autotest-roadmap.md` are updated: the
    planet-to-Kerbin half is CLOSED FOR PRODUCTION on the flight-map and TS thirds, with the
    lens asymmetry stated rather than smoothed over.

**REMAINING, AND IT IS SHORT.**
  1. **`V20K`** - the KSC host lane over these same bytes. Still reserved, still the only thing
     standing between G2 and closure, and still ungated by confirmation criterion (c): until that
     run exists, nothing about the KSC host may be written up as a documented limitation anywhere.
     The question is now SHARPER rather than answered - `kerbin-return-recorded`'s first POINT is
     at Jool so `IsKscStructurallyEligible` still looks like it rejects, but this is the first
     recording in the corpus with Kerbin-bodied points at all (56 in its final section), a
     different input to the per-point playback gate than B28's subject presented.
  2. **Deferred OPERATOR decisions, none of them debt.** (a) The `renderComposition` windows on
     both lanes - declared bare on purpose; windows are written from facets accumulated on tier
     cadence under the M-A7 wave process. (b) V20T's `created 0 ghost vessel\(s\)` forbid -
     ARM-READY off run 1's measured `created 1` and deliberately untaken, because that spec
     pre-registers BOTH init-walk readings and a forbid would retroactively gate one branch of a
     question it declared open. (c) The rendered-frame `ProtoOrbitLine` claim on the FLIGHT host -
     only answerable if the M-A2 seam grammar ever gains a WARP verb; it has none today, and
     inventing one to make a pin reachable is what a reading round must not do.
  3. The ordinary operator -> nightly PROMOTION call on both lanes. One cost neither lane can price from committed bytes and both write down:
the schedules traverse 66.8 Ms (V20M) and 34.2 Ms (V20T) of game time in instantaneous
`Planetarium` clock sets, and KSP's on-rails propagation cost at that magnitude is
UNMEASURED - a death on a TimeJump watchdog would be a reading, not a calibration
failure. **THE KSC QUESTION STAYS OPEN AND NOTHING MAY BE WRITTEN UP ABOUT IT**:
`IsKscStructurallyEligible` rejects on `Points[0].bodyName != "Kerbin"` and this
recording's first POINT is at Jool, but it is also the first recording in the corpus
that HAS Kerbin-bodied points at all, which is a different input to the playback gate
than B28's subject presented. Under roadmap confirmation criterion (c) that becomes
either a closed payoff or a cited limitation only once a `V20K` run exists.

LKO WAS DESCOPED FOR MARGIN and the roadmap's G2 entry is updated to say so:
circularizing at the arrival periapsis costs 1,926.12 m/s against the ellipse's
1,188.07, which would leave ~58 m/s of reserve after corrections instead of 1,460.4.
What the V20 pair reads off the product is a KERBIN-FRAME ARRIVAL rather than an
altitude, so the descope costs the measurement nothing.

---

## FIXTURE-DEPOT-ROUTE-RECORDED-LANE-PENDING: the G1/B27 route subject is harvested, repaired and registered, but no scenario has flown against it yet [OPENED 2026-08-26 on branch `route-harvests`. TODO, not a defect]

`harness/fixtures/saves/depot-route-recorded` is committed: the operator's own
free-play sandbox save `orbital supply route DELIVERY test`, harvested
`--expect-situation ORBITING --keep-parsek` and finished by
`harness/tools/build_depot_route_recorded.py`. It is **the first committed
fixture carrying a `ROUTE`** - `5420f805...`, `status = Active`,
`completedCycles = 1`, `isKscOrigin = True`, SameBody Kerbin -> Kerbin
(`dispatchWindowPeriod = 0`), `dispatchInterval` / `transitDuration`
16,058.001895760137 s, `recordedDockUT` 17,478.248634212287, a DockingPort STOP
onto the `Depot` (pid 3620499050) with a LiquidFuel/Oxidizer delivery manifest.
Two whole recording trees, 22 recordings, 86 authoritative sidecars. Pins live in
`test_saveparse.RECORDED_FIXTURES["depot-route-recorded"]` plus the builder's own
`verify_route`; the recipe is guarded by `DepotRouteRecordedFixtureDriftTests`.

It reads GREEN under `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
(`FAIL=0 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=0`) after a two-section INV2
containment repair on the Transporter's chain segment `a85a7ae0...` - the only
RECORDED fixture in the suite with a zero-WARN reading.

**Why it is a harvest and not a forge**, restated here because it is the thing a
reader will question: route candidacy is gated on `IsTreeFullySealed` and, AT HARVEST
TIME, both verbs that could satisfy it (`SealSlot`, `RouteCommand`) were RESERVED
command-seam verbs (H35 ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH), so no driven run
could create a ROUTE at all. The register amendment is on the G1 entry in
`autotest-roadmap.md`; B27 is a FORGE-CLASS STAMP and the flight variant was deferred
behind those two verbs. **Both verbs shipped 2026-08-30**, so the deferral is lifted as a
CAPABILITY - but this fixture stays a harvest until a driven seal -> create run actually
produces one, and nothing here should be re-read as forged in the meantime.

**Three residuals, all deliberate, all non-gating:**

- **No `Ships/` at all.** Dropped by the builder: the source carried the
  operator's edited `Kerbal X.craft` plus KSP's `Auto-Saved Ship.craft` VAB
  autosave, and this is a render subject that launches nothing (the same choice
  `duna-one-recorded` makes). A launching lane must add a `shared-ships.toml` row
  or re-harvest with `Ships/` kept.
- **`activeVessel` was re-pointed**, from the source's asteroid `Ast. YRJ-552`
  (index 0) to `Depot` (index 9). The harvest's own focusability gate PASSES on
  an asteroid, so nothing upstream would have caught it; the drift test
  re-resolves the index by name + pid.
- **The route is pinned BUILDER-side, not in `saveparse`** - see the improvement
  entry below.

**What is owed**: the V18T / V18M lanes. **V18T can be authored first with no
`EnterMapView` verb** - measured, not assumed: `RouteTrajectoryLineRenderer.DrawAll`
has one production call site (`Display/GhostTrajectoryPolylineRenderer.cs:3894-3906`,
the `Camera.onPreCull` route slot) whose only guards are the planetarium-camera
identity check, `scene is TRACKSTATION or FLIGHT`, and a per-frame de-dupe. No
`MapView.MapIsEnabled` anywhere on that path; the GHOST polyline pass is the
map-gated one (`:4014`). Headline arming facet when the lane exists:
`routeLineBuilds { min = 1 }` (the first non-zero in the suite; emitted by
`RenderCompositionRecorder.NoteRouteLineBuild` on an actual cache rebuild, which
a post-load first draw always is because `OnGameStateLoad` clears the cache) plus
`routeCoDrawViolations { max = 0 }`. Registry D10 `route-map-lines` stays
UNDECLARED until a GATING token earns it. Until a lane flies, nothing reads these
bytes.

**V18T FLEW AND IS ARMED (2026-08-26).** Both headline windows landed as written
above - `routeLineBuilds { min = 1 }` (measured 1 on all three flights, the
suite's first non-zero reading of that census anywhere) and
`routeCoDrawViolations { max = 0 }` - and the negative control
`2026-08-26_2017_a2` red on exactly `PARSEK-FAIL(render-composition)` /
`renderComposition.routeLineBuilds 1 < min 5`. THE GATING TOKEN NOW EXISTS, so
D10 `route-map-lines` IS DECLARED on that lane in the same commit. V18M (the
FLIGHT-map half, which does owe `EnterMapView`) is still owed.

## FIXTURE-DUNA-PARK-RECORDED-LANE-PENDING: the heliocentric-parking-departure subject is harvested, repaired and registered, but no scenario has flown against it yet [OPENED 2026-08-26 on branch `route-harvests`. TODO, not a defect]

`harness/fixtures/saves/duna-park-recorded` is committed: tree `ced78481...`
("Kerbal X #2") out of the SAME operator save `duna-one-recorded` came from
(`logs/2026-08-25_1537_s15-duna-one-manifest-run2`), harvested
`--expect-situation PRELAUNCH --keep-parsek` and stripped to one tree / one
mission / 14 recordings by `harness/tools/build_duna_park_recorded.py`. The two
fixtures are disjoint payloads out of one harvest.

**Why it is a separate subject and not a duplicate of `duna-one-recorded`**, which
is the whole point of the entry: they are two DIFFERENT WAYS OF GETTING TO DUNA.
`duna-one-recorded` is a DIRECT transfer - it parks in KERBIN orbit, ejects, and
its three Sun segments are one conic split by warp (sma 17,604,964,389.77
throughout), so its departure burn happens inside Kerbin's SOI. THIS one is a
HELIOCENTRIC PARKING DEPARTURE, the operator's own "orbits the star until
alignment is good": its transfer `aa48920e...` (856 points, the largest recording
in the source save) escapes Kerbin almost immediately and then coasts on ONE Sun
orbit across three consecutive segments at sma 14,072,049,898.09 / ecc 0.0326934
(agreeing to ten significant figures) for 13,502,219.94 s - about 156 Kerbin days
at 3.5% outside Kerbin's own heliocentric sma, i.e. a PHASING orbit. The
departure burn is then an element STEP at UT 2,561,070,900.03 to sma
17,908,765,008.46 / ecc 0.19216 (+27% sma, +488% ecc). Duna SOI entry at
2,570,454,935.62, hyperbolic (ecc 3.6025), capturing into an ellipse at
2,570,492,255.34.

That property lives in the transfer's `ORBIT_SEGMENT` list, which NO saveparse
facet reads, so `RECORDED_FIXTURES` alone cannot tell the two subjects apart -
which is why `DunaParkSignatureTests` reads it directly and asserts the park run,
the departure step, the Duna arrival, AND (from the other side) that the direct
sibling still has no such park.

It reads GREEN under `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`
(`FAIL=0 WARN=16 RED=0`) after a four-section INV2 containment repair on
`aa48920e...`; the 16 WARNs are the same INV8 phantom-attribution class
`duna-one-recorded` carries 15 of, from the restored whole-career `ledger.pgld`.

**What is owed**: a lane of its own. The rendering question it exists to ask - how
Parsek renders a ghost that sits on a heliocentric parking orbit for 156 days and
then departs - is unmeasured, and no committed subject other than this one can
ask it. Until a lane flies, nothing reads these bytes.

## IMPROVEMENT-SAVEPARSE-NO-ROUTES-FACET: `harness/lib/saveparse.py` parses no `ROUTES` node, so the only committed ROUTE is pinned builder-side instead of in the shared facet map [OPENED 2026-08-26 alongside `depot-route-recorded`. IMPROVEMENT, not a defect]

`saveparse.parse_parsek_scenario` models RECORDING_TREE topology, supersede rows,
tombstones, rewind retirements and REWIND_POINTS - but not `ROUTES`. So when
`depot-route-recorded` landed, its ROUTE had to be pinned inside
`harness/tools/build_depot_route_recorded.py::verify_route` (id, status, backing
tree, dock member, the two clocks, the window period, the four `RECORDING_IDS`,
the four `SOURCE` rows, the STOP endpoint's resolution to a live VESSEL node),
wired into the suite through `DepotRouteRecordedFixtureDriftTests`. That works and
is guarded, but it is one fixture's private parser rather than a facet any
scenario can express a window over.

**What a `routes` facet would buy**: `[expectations.recordings.routes]` blocks in
a scenario spec (route count, status, completedCycles, endpoint kind), evaluated
by the `saveParse` verifier row like every other structure window - so the G1
lanes could gate on route STATE rather than only on render tokens, and a
`SourceChanged` flip mid-run would red the flight instead of silently producing a
dark map. Python-only change in `harness/lib/saveparse.py` plus cells in
`test_saveparse.py`; no C# and no flight. Do it before the second route fixture,
not after.

## SUBJECT-CANDIDATE-INTERPLANETARY-ROUTE: the operator's plain `orbital supply route` save carries a Kerbin -> Duna route that may be the MalformedMixedBodies case, and nothing has looked [OPENED 2026-08-26 while ranking route sources. TODO, not a defect]

Three operator saves carry route state. `orbital supply route DELIVERY test`
became `depot-route-recorded` (above). `orbital supply route CLEAN` carries NO
routes but IS the pre-route ancestor of the same backing tree `c9ef80ee...` (same
four recording ids) - a paired CONTROL candidate if a lane ever wants
"same tree, no route" beside "same tree, route".

The third, `orbital supply route` (plain), carries TWO routes: one `Paused` and
one `Active` re-aim-basis **Kerbin -> Duna**, i.e. a cross-body route rather than
`depot-route-recorded`'s SameBody one. That is a different `dispatchWindowPeriod`
regime (synodic rather than 0) and is the likely home of the
`MalformedMixedBodies` classification, which no fixture exercises. Nobody has
opened it beyond the ranking pass. It is NOT part of B27 and should get its own
subject id when someone takes it.

## M-A7-SEAM-ENDPOINT-SKIP-REASON-CENSUS: `seam-endpoint-skipped` dominates every renderCompose unevaluable count and DOUBLED between two flights of the same lane with no explanation on record [FOUND 2026-08-25 reading the V14M reading-vs-armed facets (53 vs 106 skips) and the s15 free-play manifest (512 at the cap). IMPROVEMENT, REPORT-ONLY]

Every SEAM_ENDPOINT record carries a `skipReason`, but `observed_composition_facets`
aggregates them into one opaque `seam-endpoint-skipped` count. A per-reason census
(the `SeamEndpointOracle.FormatPassSummary` shape, which already exists on the C#
log side) would explain the run-to-run variance for free and tell an arming pass
which skips are structural vs incidental. Python-only change in
`harness/lib/rendercompose.py`; no schema move needed.

## M-A7-ONE-MANIFEST-PER-PROCESS: two rich scenes in one KSP session still end as last-flush-wins - the auto-flush clobber guard only protects a dwell-bearing manifest from a DWELL-FREE later flush [FOUND 2026-08-25 during the operator's s15 free-play ground-truthing (single-subject sessions, so no data was lost). LIMITATION, REPORT-ONLY]

Fine for harness lanes (one scene, one export). Lossy for multi-subject free-play
ground-truthing: watching two different loop subjects in two flight scenes of one
session keeps only the second manifest. A per-scene partition suffix
(`parsek-render-manifest.<n>.txt`) or an append-partition inside one file would
preserve both; the harness reader would take the newest/richest. Deliberately not
built until a session actually needs it.

## ~~COLLECT-LOGS-SAVE-COPY-IS-ANALYZER-INCOMPLETE: `scripts/collect-logs.py` copies `Parsek/Recordings` but not `Parsek/Saves` or `Parsek/GameState`, so an analyzer run over a COLLECTED save always WARNs INV9 (missing rewind saves) and loses the GameState sidecars a fixture harvest needs~~ [FOUND 2026-08-25: the s15 collection WARNed INV9 on four recordings whose rewind saves exist in the live save, and the duna-one-recorded harvest had to reach into the separately-collected `parsek/` dir for GameState. TOOLING IMPROVEMENT. FIXED 2026-09-02]

**Fix (2026-09-02).** The save-copy leg now copies EVERY `Parsek/<dir>` subdirectory of the
save (`Recordings`, `Saves`, `GameState`, `RewindPoints` - the directory INV9 actually
reads - and whatever is added next) into `saves/<name>/Parsek/`, through one
`copy_parsek_sidecar_dirs` helper the flat `parsek/` leg shares; `--skip-recordings` still
excludes `Recordings` from both. A collected save is therefore analyzer-faithful and
harvest-complete. Validated against a fabricated save layout with and without
`--skip-recordings` (all four directories land, Recordings alone drops on the flag).

The original proposal named `Saves` + `GameState`; `RewindPoints` is what
`Inv9RewindPoint` resolves through `RecordingPaths`, so a copy of only those two would have
left the WARN in place.

## ROUTE-DELIVERY-CLOCK-OMITS-THE-HOLD-ARGS: `RouteLoopClock.TryGetRouteLoopState` threads only the relaunch schedule and the loiter cuts into the span clock, so on a hold-carrying or launch-aligned route-backed unit the DELIVERY clock and the RENDER clock are not the same clock [FOUND BY READING 2026-08-25 while scouting the M-A7 render-composition plan surface, from the source alone - NOT measured on a flight. LATENT on every committed route today (v0 same-body routes carry no holds). REPORT-ONLY and DELIBERATELY NOT FIXED IN THE M-A7 PR: that PR is observation-only, and changing what the delivery clock computes is a product decision of its own]

`Source/Parsek/Logistics/RouteLoopClock.cs:255-278` forwards exactly two of the
span clock's optional arguments:

```
GhostPlaybackLogic.TryComputeSpanLoopUT(
    currentUT, unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
    out loopUT, out cycleIndex, out isInInterCycleTail,
    schedule: unit.RelaunchSchedule,
    loiterCuts: unit.LoiterCuts);
```

Nine further arguments keep their defaults: `arrivalHoldSeconds`,
`arrivalHoldAtUT`, `arrivalHoldAlignPeriod`, `launchBodyRotationPeriod`,
`launchHoldEngaged`, `soiExitAtUT`, `arrivalJointSecondaryPeriod`,
`arrivalJointSecondaryTolerance` and `arrivalJointMaxWholeHoldPeriods` - every
one of which the RENDER side passes from the same `LoopUnit`. The span clock
FREEZES `loopUT` for the duration of an engaged hold and pays the frozen time
back out of the cycle, so on a unit that carries one the two clocks diverge by
the held seconds: the delivery clock's `loopUT` runs ahead of the rendered one,
and the `cycleIndex` a dock crossing is attributed to can be off by one once the
divergence exceeds the remaining span. The Phase 6 hardening comment at the call
site is accurate about what it DID thread and silent about what it did not.

WHY IT IS LATENT, NOT DEAD. A v0 same-body route's backing mission is faithful
(`bodyInfo = null`), so every hold field is zero / NaN and the omission is
byte-identical to threading them. The moment an inter-body or launch-aligned
route is backed by a mission that engages a launch hold or an arrival hold - the
same population the re-aim work already produces - the two clocks part.

WHAT M-A7 CHANGES ABOUT IT. Nothing in behavior; it makes the divergence
MEASURABLE for the first time. The render-composition manifest exports the
unit's hold fields on `PLAN.UNIT` (`arrivalHoldSeconds` / `arrivalHoldAtUT` /
`arrivalAlignPeriodSeconds` / `launchBodyRotationPeriodSeconds` /
`launchHoldEngaged` / `recordedSoiExitUT` and the three joint keys), the route's
`recordedDockUT` + dispatch window on `PLAN.UNIT.ROUTE`, the observed
`hold-engage` / `hold-release` clock events, and the `route-dock-crossing`
events with the cycle index the delivery side attributed them to. RC-ROUTE
therefore has both clocks in one file and can state the divergence as a number
rather than as this reading. Do that on a hold-carrying route lane (Phase 4,
G1's B27/V18) before proposing a fix.

---

## M-A7-RENDER-COMPOSITION-PHASES-3-AND-4: what the render-composition manifest still owes after Phases 1-2 landed, plus the four deferrals those phases took deliberately [OPENED 2026-08-25 with the M-A7 Phases 1-2 PR. TODO, not a defect. Status authority for the module is `docs/dev/autotest-status.md`; the design is `docs/dev/design-autotest-render-composition.md`]

**PHASE 3C - THE CORPUS-WIDE DECLARATION WAVE (the operator's stated architecture,
recorded 2026-08-26).** The B-flights generate recordings, the V-lanes loop them
synthetically at new UTs, and the composition manifest audits the composed render -
so the rollout is declaration, not new machinery. Plan, in waves: (A) batch-declare
the bare `[expectations.renderComposition]` block + export step + tracer steps
(where missing) across the eligible V-M lanes (V7M, V9-V13, V15M, V16M, V17M, V19M,
V21M, V22M, V23M) plus the first T-host lane (`V14T`, the TS pilot - NO tracking-
station-host manifest has ever been captured) and the K-host lane (`V22K`); each
declaration changes that lane's anomaly exposure where tracers were previously off,
which is why nothing arms at declaration. (B) readings accumulate FOR FREE via
normal tier cadence once declared - every flight of a declared lane produces facets
into its results JSON; targeted flights only where cadence is too slow. (C) arm in
batches off accumulated readings with per-lane windows, the established three-run
discipline per lane. Gates that must exist first for specific lanes: the map-open
seam verb (RC-OWN-DRAW-HALF-IS-MAP-GATED) before any lane whose subject carries
TracedPath phases arms the ownership clauses.

~~(A) batch-declare ... across the eligible V-M lanes ... plus `V14T` and `V22K`~~
**WAVE A EXECUTED 2026-08-26 - ALL FIFTEEN LANES ON THE LIST DECLARED, ZERO
DEFERRALS, NOTHING ARMED.** The declarer roster goes 4 -> 19: V7M, V9, V10, V11,
V12, V13, V15M, V16M, V17M, V19M, V21M, V22M, V23M on the flight-map host, plus
`V14T-ike-ts-arrival` (the FIRST tracking-station-host manifest the module has ever
been able to take) and `V22K-kerbin-splashdown-ksc-arrival` (the FIRST KSC-host
one). Per lane the edit is exactly three things and no more: one
`ExportRenderManifest` step immediately before `FlushAndQuit`, a bare
`[expectations.renderComposition]` last in `[expectations]`, and a compact
renderComposition arming ledger in the header - no step, no jump UT, no budget and
no existing expectation moved (the S4.1 rule). THE ONE EXPOSURE CHANGE the plan
anticipated landed on exactly two lanes: V14T and V22K armed only `mapRenderTracing`
+ `verboseLogging`, and `test_every_declarer_arms_the_tracers_the_seam_capture_needs`
requires all three of every declarer, so both gained `ghostRenderTracing`. On those
two it buys NO manifest content (every capture predicate the recorder reads is
`MapRenderTrace.IsEnabled`-gated) and adds only a previously dark FLIGHT-scene
anomaly surface - `loop-seam-teleport` and the GhostRenderTrace raises - against
tight sweeps (V22K `allowedAnomalies = []`, V14T tolerating only `icon-off-orbit`);
a red on either reading run from a newly gated raise is a TRACER-ARMING READING to
record, not a regression to re-diagnose. Two expected-thin classes are pre-registered
so an arming pass does not misread them: the four ARM-ONLY lanes (V9 / V11 / V12 /
V13) quit ~1 s after `StartLoopPlayback` and are the corpus's FLOOR case, and the two
LANDED-TERMINAL subjects (V22M / V23M) render flight-mesh only. Verified with
`discover -s lib` green (1709 tests) and `run.py --dry-run` exit 0 on all fifteen,
each showing `PARSEK_RENDER_MANIFEST=1` at [LAUNCH] and
`renderCompose(report-only: renderComposition; declared: {})` at [VERIFY], with the
three armed lanes still reading `armed:` and V6M still report-only. Roster and full
rationale: `RENDERCOMPOSE_DECLARER_SPECS` in `harness/lib/test_hlib.py`.
**NOW OWED: (B)** let readings accumulate off the normal tier cadence and take
targeted flights only where cadence is too slow (V6M is ahead of the wave: its
reading flew 2026-08-25 - though it now owes a SECOND one, since it gained the
map-open pair on 2026-08-26 and the first reading measured a shape it no longer
flies; see the RC-OWN entry and the Phase-3b bullet);
**(C)** arm in batches off those facets, per
lane, on the three-run discipline. The map-open seam-verb gate above is
PARTIALLY lifted: the verb exists (PR #1539) and V6M now drives it, but until
that re-fly reports `ownershipChanges > 0` no ownership clause may be armed
anywhere - a step added is not a reading taken.

**WAVE B ADDED TWO MORE DECLARERS 2026-08-26, ROSTER 19 -> 21, and they are a
different kind from Wave A's:** `V18T-depot-route-ts-arrival` and
`V25M-duna-park-player-loop` are NEW LANES against the two Phase-4 harvest
fixtures rather than declarations bolted onto lanes that had already flown, so
each owes a FIRST FLIGHT before it owes a window. Details in the Phase 4 bullet
below; rationale in `RENDERCOMPOSE_DECLARER_SPECS`.

**ARMED LANES 3 -> 5 ON 2026-08-26: `V25M-duna-park-player-loop` and
`V6M-mun-player-loop` both closed the full three-run discipline the same day, so
the armed roster is now V14M / V8 / V24W / V25M / V6M against 21 declarers.**
  * V25M armed off THREE readings of one unchanged drive shape - `2026-08-26_1744`
    (full measurement), `_1817` (red on `line-blink`, and the run that validated
    the RC-SEAM verifier fix live at zero FAIL findings) and `_1823` (the clean
    PASS). Structure equal to the integer on all three (dwells 3 +2 open, cycles
    0, treatments StockConic 2 / TracedPath 1, seams rigid 8 / flexible-soi 2);
    only the unevaluable census moved, 384 / 410 / 409. WINDOWS `dwells {1,32}`,
    `unevaluable {max 1400}`, `requireSeamKinds [rigid, flexible-soi]`, with
    `cycles` deliberately omitted on the V8 pattern (this subject closes zero
    cycles, so a floor would red the runs it was armed off). The ceiling is ~3.4x
    the largest reading - the siblings' ratio-to-measurement SCALED to a
    ~400-record endpoint population, not copied from their 200 / 250. ARMED
    RE-FLIGHT `2026-08-26_1837` PASS, zero mismatches, unevaluable 388 (inside the
    readings' own spread). NEGATIVE CONTROL `2026-08-26_1839`
    `PARSEK-FAIL(render-composition)` off a temporary `dwells = { min = 50 }`,
    EXACTLY ONE mismatch (`renderComposition.dwells 3 < min 50`), every sibling
    row clean, reverted in the same change.
  * V6M armed off THE PAIR `2026-08-25_2056` (map closed) + `2026-08-26_1745`
    (map open), which bracket the one change the lane made between them. WINDOWS
    `dwells {1,32}`, **`cycles {min 2, max 16}` - the floor no other lane in the
    suite can carry**, `unevaluable {max 300}` sized off the map-open reading (and
    clearing the map-closed 110 besides), `requireSeamKinds [rigid,
    flexible-soi]`. The `cycles` floor was CHECKED for vacuity rather than
    assumed: neither census carries `no-dwells-attributable-to-unit`, so the five
    closed dwells really were attributed to the unit and the isomorphism
    comparison really ran. ARMED RE-FLIGHT DISCHARGED FOUR TIMES (`_1838`,
    `_1842`, `_1843`, `_1844`, all PASS attempt 1, zero mismatches,
    `ownershipChanges = 6` on every one - the RC-OWN closure now rests on FIVE
    map-open flights). NEGATIVE CONTROL `2026-08-26_1840` red on exactly
    `PARSEK-FAIL(render-composition)` off a temporary `cycles = { min = 9 }`,
    sibling rows clean, reverted in the same change.
  * TWO FINDINGS CAME OUT OF THE PASS AND BOTH ARE FILED AT THE TOP OF THIS FILE
    RATHER THAN BURIED HERE: `V6M-CYCLE0-ARRIVALLOITER-DWELL-CLOSE-RECORD-LOST`
    (the V6M control's SECOND, uninvited mismatch, DIAGNOSED the same day off all
    six archived manifests: the cycle-0 ArrivalLoiter dwell renders identically on
    every flight and it is its CLOSE RECORD that is lost, when the sparsest-
    sampling run gets zero frames in the few-frame `7 -> -1` inter-cycle-tail
    state. A recorder bookkeeping gap, NOT renderer intermittency and NOT a
    jump-target race - which undercuts the reason V6M's arming was justified with,
    so that decision is referred back in the entry) and
    `RENDERCOMPOSE-OWNERSHIPCHANGES-IS-NOT-WINDOWABLE` (the RC-OWN premise could
    only be armed through the FAIL-finding gate, because `ownershipChanges` was a
    recorded facet and not one of the three windowable keys). BOTH ARE NOW CLOSED:
    the recorder fix landed the same day with a direct determinism proof, and the
    windowable gap closed in two halves - the schema on `window-facets-arm-v18t`,
    then V6M's own arming pass on `v6m-ownership-window` (`ownershipChanges =
    { min = 1 }`, re-flight `2026-08-26_2042` PASS, control `2026-08-26_2043`).
  * The controls were NOT shared: V25M inverted `dwells`, V6M inverted `cycles` -
    a clause V25M's block does not even carry - so each red lands on its own armed
    window rather than re-proving the shared `rendercompose` evaluator. Both were
    applied by LINE-ANCHORED edits of the real key, each verified with
    `grep -n '^<key>'` AND through `run.py --dry-run`'s `declared:` line before
    launch, which is the standing discipline since
    `NEGATIVE-CONTROL-EDIT-NEVER-REACHED-THE-KEY`.


Phases 1-2 shipped the C# recorder (env-gated, `ExportRenderManifest` verb,
scene-exit flush), the pure Python verifier `harness/lib/rendercompose.py`, and
the `renderCompose` verifier row REPORT-ONLY. FOUR committed scenarios now
DECLARE `[expectations.renderComposition]`. THREE of them (see the Phase-3
bullet) flew their report-only reading runs on 2026-08-25 - so the module is
live-proven on real game manifests - and ALL THREE OF THOSE BLOCKS WERE ARMED
that day off those readings (windows in the Phase-3 bullet), EACH LANE THEN
CLOSING THE FULL THREE-RUN WORKFLOW THE SAME DAY: V14M and V8 with six runs
between them, and V24W - the RC-WARP lane - with six of its own (three readings,
two armed re-flights, one negative control). **PHASE 3 IS THEREFORE COMPLETE AND
ITS LAST DEBT, RC-WARP, IS DISCHARGED**; D14 `warp-rails` coverage is real rather
than claimed, because the gate behind it (`warpBuckets`, armed on V24W alone) now
reds a run whose rails buckets come back empty. THE FOURTH DECLARER,
`V6M-mun-player-loop`, was authored 2026-08-25 as a BARE UNARMED block and still
owes its reading run - see the Phase-3b bullet: it is the MUN subject the
operator asked about, and the suite's first TWO-COMPLETE-CYCLE RC-CYCLE dataset.
WHAT REMAINS OWED for this module is that lane's reading + arming (Phase 3b),
Phase 4's route surfaces (via G1's B27/V18), and the parked
instrument-calibration items listed at the end of this entry - nothing in the
original Phase 3.

REMAINING PHASES.

- **Phase 3 (lanes). COMPLETE 2026-08-25 - three lanes declared, armed and
  discipline-discharged, RC-WARP included; nothing in this bullet is still owed.**
  ~~Extend two committed V lanes with an
  `[expectations.renderComposition]` block~~ LANE EXTENSIONS AUTHORED 2026-08-25:
  `harness/scenarios/V14M-ike-player-loop.toml` (the phase-lock moon loop,
  V6/V14 class) and `harness/scenarios/V8-eve-player-loop.toml` (the re-aim
  interplanetary landing loop, V8/V13 class) each gained ONE
  `ExportRenderManifest` step immediately before `FlushAndQuit`, plus a BARE
  report-only block - no `gating`, no assertion key - so the windows get authored
  FROM the first manifests' facets instead of predicted. Both lanes already armed
  the three tracers the seam capture needs (`ghostRenderTracing` /
  `mapRenderTracing` / `verboseLogging`), and NOTHING ELSE in either flown shape
  moved: no jump UT, no pacing spacer, no budget, no expectation. The DECLARER
  pin moved with them - `test_no_committed_spec_declares_the_block_yet` became
  `RenderComposeVerifierWiringTests.RENDERCOMPOSE_DECLARER_SPECS` naming both
  files, joined by three cells pinning that no declarer arms gating, that every
  declarer arms the three tracers, and that every declarer exports immediately
  before teardown. `RENDERCOMPOSE_ARMED_SPECS` is still EMPTY on purpose -
  deliberately NOT named `*ARMED_ALLOWLIST`, because the save-structure roster is
  scraped out of that file's source by a first-match regex on that name.
  ~~STILL OWED: the report-only READING RUN for each lane (nothing has yet flown
  with `PARSEK_RENDER_MANIFEST=1`)~~ BOTH READING RUNS FLOWN 2026-08-25, BOTH
  PASS, BOTH REPORT-ONLY - the module is live-proven on real game manifests:
  `2026-08-25_0953_V14M-ike-player-loop` (PASS attempt 1, wall 68 s, 22/22 steps)
  and `2026-08-25_0956_V8-eve-player-loop` (PASS attempt 1, wall 53 s, 31/31
  steps), each `renderCompose status=REPORT gating=false armedBlocks=[]
  mismatches=[]` with exactly ONE INFO finding (RC-QUAL's endpoint-ratio trend
  line) and zero WARN / zero FAIL. Facets and the two instrument observations are
  recorded in `docs/dev/autotest-status.md` -> M-A7 and in each spec's own arming
  ledger; the headline pair is V8's `hold-engage`/`hold-release` observed at 1x
  (the program's first) and V14M's all-`StockConic`, 1x-only, 107-endpoint
  reading.
  ~~ARMING DECISION PENDING OPERATOR - no window has been authored off the
  reading facets and both blocks stay BARE~~ ~~ARMING IN PROGRESS: BOTH BLOCKS
  ARMED 2026-08-25, ARMED RE-FLIGHT + NEGATIVE CONTROL STILL OWED~~ **ARMING
  DONE AND VALIDATED 2026-08-25: both blocks armed AND both lanes discharged the
  full three-run workflow the same day.** The operator
  took the call off exactly the reading facets, nothing else, and nothing in
  either flown shape moved (the S4.1 rule).
    * V14M: `dwells {1,32}` (measured 3), `cycles {1,16}` (measured 1 CLOSED
      cycle), `unevaluable {max 200}` (measured 56), `requireSeamKinds
      ["rigid","flexible-soi"]` (measured rigid 14 / flexible-soi 2).
    * V8: `dwells {1,32}` (measured 2), `unevaluable {max 250}` (measured 76 over
      a 272-record endpoint population - 2.5x V14M's, hence the proportionally
      higher ceiling), `requireSeamKinds ["rigid","flexible-soi"]` (measured
      rigid 6 / flexible-soi 4). `cycles` DELIBERATELY OMITTED: that subject
      closed ZERO cycles, so a floor would red the run it was armed off and a
      `{min = 0}` pin can never red at all.
    The count floors plus `requireSeamKinds` are the anti-vacuity halves; the
    ceilings are runaway guards, not pins, because dwell and endpoint counts move
    with frame timing. `warpBuckets` on neither, ever. Rosters and per-lane armed
    KEY-SET pins: `RENDERCOMPOSE_ARMED_SPECS` +
    `test_v14m_declares_the_render_composition_block_armed` /
    `test_v8_declares_the_render_composition_block_armed_without_a_cycles_floor`
    / `test_every_armed_block_keeps_an_anti_vacuity_floor` in
    `harness/lib/test_hlib.py`, where `test_no_declarer_arms_gating_yet` was
    replaced (per its own docstring discipline) by
    `test_every_declarers_arming_state_matches_the_recorded_rosters`.
  ~~STILL OWED: the rest of the three-run arming workflow per lane (armed
  re-flight -> negative control, the latter shareable across the pair per the
  V4/V5/V14 shared-evaluator precedent) and the per-criterion negative
  controls.~~ **DISCHARGED 2026-08-25 - six runs, both lanes:**
    * V14M: reading `2026-08-25_0953` -> armed re-flight `2026-08-25_1050` PASS
      (gating=True, ZERO mismatches; dwells 3, cycles 1, seamKinds rigid 14 /
      flexible-soi 2, unevaluable 108, one INFO RC-QUAL) -> negative control
      `2026-08-25_1052` `PARSEK-FAIL(render-composition)` off a temporary
      `cycles = { min = 5 }`, single mismatch
      `renderComposition.cycles 1 < min 5`, reverted in the same change.
    * V8: reading `2026-08-25_0956` -> armed re-flight `2026-08-25_1051` PASS
      (ZERO mismatches; dwells 2, cycles 0, seamKinds rigid 6 / flexible-soi 4,
      unevaluable 78, one INFO RC-QUAL; the `hold-engage`/`hold-release` pair
      and the `reaim-window` event both REPRODUCED) -> negative control
      `2026-08-25_1054` `PARSEK-FAIL(render-composition)` off a temporary
      `dwells = { min = 50 }`, single mismatch
      `renderComposition.dwells 2 < min 50`, reverted in the same change.
    The controls were NOT shared: each lane inverted a window of its OWN, so
    each red lands on that lane's armed clause rather than re-proving the shared
    `rendercompose` evaluator, and on both controls every sibling verifier row
    (saveParse / anomalySweep / driverValidity / logValidate / analyzer) stayed
    PASS. Also measured: V14M's armed run confirmed the sticky-bit fix
    (`mapRenderTracingOn=true`, `seam-data-unavailable-tracing-off` gone) while
    its unevaluable TOTAL rose 56 -> 108 on `seam-endpoint-skipped` variance
    (106 vs 53) - inside the `{max 200}` runaway guard by design.
  Arming stays an operator decision taken only
  after a report-only reading run whose facets match the declared windows,
  exactly as R9's `[expectations.rewind]` arming was. One MEASURED constraint on
  that decision, HONOURED IN THE ARMING: V8's reading returned
  `cut-run-period-absent: 1`, so RC-CUT's
  whole-ratio check could not evaluate the corpus's only non-zero loiter cut (no
  run period to divide by; `cutWholeRatios` came back empty). An RC-CUT window
  armed off that reading would arm a clause that never fires, so NO RC-CUT
  surface was armed - nor any RC-HOLD clause, one observed engage/release pair
  not being a window.
  ~~STILL OWED AFTER THE ARMING, and the ONLY Phase-3 debt left: the design's
  **warp schedule** bullet (RC-WARP)~~ **DISCHARGED 2026-08-25 by V24W's own
  six-flight discipline - see the RC-WARP block below.** Both subjects move the clock with
  instantaneous `TimeJump`s, so their warp histogram is 1x-only by construction
  and `warpBuckets` must never be declared on either. RC-WARP is satisfiable only
  by the V1/autopilot rails-warp ladder shape, which wants a third lane (or a
  re-shaped drive) rather than a re-pin of these two. CONFIRMED FOUR TIMES: the
  histograms came back 1x-only on both reading runs (173 samples on V14M, 296 on
  V8) and on both armed re-flights (177 and 295), every other bucket zero.
  **COMPLETE 2026-08-25 - THE LANE IS AUTHORED, THE MEASUREMENT IS TAKEN
  (reading flights 2 and 3, below), THE BLOCK IS ARMED OFF THAT PAIR, AND THE
  ARMED RE-FLIGHT (`_1722`, plus `_1811` which was meant to be the control and
  never armed) AND THE NEGATIVE CONTROL (`_1925`,
  `PARSEK-FAIL(render-composition)` on `RC-WARP [FAIL] warpBuckets.warpHigh`)
  HAVE BOTH FLOWN.**
  ~~which wants a third lane~~ the third lane exists:
  `harness/scenarios/V24W-duna-one-warp-stair.toml`, a loop-arrival map dwell
  over `fixtures/saves/duna-one-recorded` (the harvest of the first free-play
  ground-truth session - the one whose histogram, `warp100 4727 / warp1000 15488
  / warpHigh 11446` with zero 1x, is what proved this subject exists outside a
  hypothesis) driving a COMMANDED rails stair
  (10x/50x/100x/1000x/100x/50x/10x, up and back down, each step held 10 poll
  frames) at each of its three span-clock windows. `V24` is reserved in
  `autotest-roadmap.md`'s register and MINTS the `W` warp-schedule suffix
  alongside `M`/`T`/`K` - `W` names a drive shape rather than a render host,
  which is why the lane may share the flight map with V2 and still be a separate
  id. The stair is a FLAG-GATED extension of `m3_loop_arrival_dwell`
  (`dwellRampFactors` empty by default = the pre-RC-WARP machine, byte-identical
  action stream / state / assertion rows, pinned by
  `M3WarpStairInertnessTests`), so V2 / V3F / V3R and both armed composition
  lanes are untouched.
  **READING FLIGHT 1 FLEW 2026-08-25 (run `2026-08-25_1415`, PASS/REPORT) AND
  MEASURED AN EMPTY OBSERVATION.** Zero `GhostCreated` lines in the collected
  KSP.log, zero dwells / seams / histogram buckets in the manifest, while the
  recorder's own clock events (two cycle-rollovers, a hold-engage, two
  inter-cycle-tail entries) proved the unit clock ran. TWO ROOT CAUSES, both
  fixed, neither in Parsek's render pipeline:
  (1) **The dwell windows rode the RECORDED clock while the loop clock
  COMPRESSES.** A re-aim unit excises whole-period loiter intervals
  (`GhostPlaybackLogic.CompressSpanUT`); this unit logged `loiterCuts=1
  cutSeconds=11393869 compressedSpan=7001129/18394999`, and all three declared
  offsets (11.47M / 18.33M / 18.39M) exceeded the 7.00M compressed span, so
  every dwell sat in the inter-cycle tail where the clock parks at spanEnd and
  nothing renders. The cycle advance itself was correct - `phaseAnchorUt` was
  forward-advanced by the C# - so the arithmetic looked right and was not.
  ~~Fix~~ FIXED: the `MissionConfig` seam now publishes the unit's cut list and
  clock primitives (`loiterCutCount`, `loiterCuts` as `startOffset:length`
  pairs relative to `spanStartUt`, `compressedSpanSeconds`, `spanSeconds`), and
  `m3_loop_arrival_dwell` compresses every recorded offset through them at the
  single seam where an offset becomes a live UT (`_m3_window_ut`). Spec params
  stay on the RECORDED clock by design - mapping them is the machine's job.
  An offset that lands INSIDE a cut, or at/past the compressed span, is now a
  NAMED give-up at ARM time (a spec authoring error, refused rather than
  silently clamped: the cut collapses to one compressed instant, so clamping
  would green a run that dwelled somewhere other than the instant its own
  ledger claims). An older DLL that publishes no cut keys behaves exactly as
  before, pinned by a cell. **V2 / V3F / V3R were NOT latently affected** and
  this is a measurement, not an assumption: their subject
  `duna-direct-recorded` logs `loiterCuts=0 cutSeconds=0
  compressedSpan=4506891/4506891` on all nine collected flights, and V2's own
  logs carry 362 `ghosts=1` probe frames plus `body=Duna` x1421 /
  `body=Sun` x740 / `body=Kerbin` x392 - real observations, not vacuous
  passes. A direct transfer never parks long enough for
  `ReaimLoiterCompressor.ComputeCuts` to detect a >1-rev loiter run, which is
  why V24W is the first affected lane.
  (2) **The three "1x holds" never left rails warp**, so the mission cost 455
  wall seconds against the ~2,850 its own spec sized. `ACTION_CANCEL_WARP` in
  `harness/missions/mission_runner.py` zeroed the warp factors only through
  `WarpService.cancel`, and `self._warp` is created ONLY by
  `ACTION_WARP_TO_UT`. `m3_loop_arrival_dwell` moves the clock with seam
  `TimeJump` epoch shifts by design, so it had no warp service and the handler
  was a no-op: 128 consecutive `warp=RAILSx10.000` telemetry frames per hold,
  all four issued cancels logging nothing, `allow_rails_warp=True` tolerating
  the residual state, and a PASS that never once dwelled at 1x. It is the first
  mission in the suite to pair `SET_RAILS_WARP` with `CANCEL_WARP` without
  `WARP_TO_UT`, which is why a latent runner gap surfaced here and nowhere
  before. ~~Fix~~ FIXED: the primary-connection factor reset is now
  unconditional, which is what the handler's own comment already claimed;
  idempotent with `WarpService.cancel` where a service does exist.
  WHAT WAS OWED AFTER FLIGHT 1 (every leg now discharged): a
  RE-FLY of the reading run (flight 1's observation measured nothing about
  Parsek) - flown as flights 2 and 3 below - then `warpBuckets` written FROM the
  measured histogram - armed 2026-08-25 off the flight 2 + flight 3 pair - then
  the armed re-flight and a negative control inverting a required token of this
  lane's own - flown 2026-08-25 as `_1722` / `_1811` and `_1925` respectively.
  **READING FLIGHT 2 FLEW 2026-08-25 (run `2026-08-25_1502`,
  PARSEK-FAIL(anomaly) attempt 1) AND MEASURED THE FULL COMPOSITION. THE RED IS
  THE PRE-REGISTERED DOCTRINE OUTCOME, NOT A LANE DEFECT.** All ten driver steps
  met; every other verifier row green (driverValidity PASS, mission MISSION-OK,
  analyzer PASS red=0, logValidate PASS, saveParse REPORT, renderCompose REPORT
  parsed). Wall 2,895 s total / 2,817 s mission - within 2 % of the ~2,850 s the
  spec sizes and inside the unchanged 3000 / 3600 budgets, which CONFIRMS the
  flight-1 warp-cancel fix on the cost line and not just in code. Both flight-1
  root causes are measured closed: all three dwell windows now land inside the
  compressed span and RENDER (2 closed dwells + 2 open at export, `treatments
  StockConic 2`, `coverages InSegment 2`, `transitions 2`, `chainBuilds 2`,
  `lineBranches 2`, `cycles 1`), and the histogram is a real stair -
  `warp1x 322078 / warp100 10602 / warp1000 2170 / warpHigh 0 / warpPhys 0` with
  **`holdsAboveOneX = 1` and `seamsAboveOneX = 2`**, the first time any committed
  subject has put a cataloged seam AND a hold above 1x. That is RC-WARP's
  anti-vacuity statement satisfied by measurement; what remains before it can be
  ASSERTED is the arming. Other facets, recorded because the arming pass writes
  its windows from these and nothing else: seams `rigid 11` / `flexible-soi 4`,
  `seamEndpoints 1024`, `seamTangents 0`, `maxEndpointRatio 0.4366947421168355`;
  clock events `cycle-rollover 2` / `hold-engage 1` / `hold-release 1` /
  `inter-cycle-tail 1` / `reaim-window 1`, with the observed hold 7050.236864 s
  TWICE against a per-cycle plan of [39607.052804, 35444.407972] (an ~18-20 %
  observed/planned ratio at BOTH plan units - a measurement handed to RC-HOLD,
  deliberately not armed off one engage/release pair); decimation `SEAM_ENDPOINT
  292546` + truncated `41280` + skipped `512` driving `unevaluable 334342`, so
  the endpoint census on this drive shape is a SAMPLED population and no count
  window may be written over it; findings 4 x INFO `RC-QUAL` and nothing worse
  (zero FAIL, zero WARN, zero mismatches, `ownershipChanges 0`); recordings count
  13, equal to the fixture's own population, so the optimizer split nothing at
  the four cross-body seams and nothing was lost.
  ANOMALY CEILINGS AUTHORED OFF THIS RUN: `hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, and all three are now tolerated in
  the spec's `allowedAnomalies` with `2026-08-25_1502` cited on each, per the
  S1.4 "a token is added WITH the flight that shows it" rule. THEY ARE BARE, NOT
  BUDGETED, and the reason is a whole-suite property rather than a call about
  this lane: the ceilings the measurement authorizes are 130 / 8 / 8
  (icon-teleport doubled off 65 because the probe threshold defect is open and
  this lane commands rails; the other two 4x off 2, bounding a per-seam raise
  without tolerating a per-frame one), and
  `test_no_committed_spec_arms_a_count_budget` +
  `test_every_committed_spec_parses_under_the_budget_surface` in
  `harness/lib/test_hlib.py` hold the `maxCount` mechanism INERT across every
  committed spec. V14T and V15T each wrote the budget form first and each backed
  out of it for exactly this reason; arming it means moving both cells to a named
  allowlist in the same edit, and that move should cover V14T / V15T / V24W
  together as an operator decision, not arrive as a side effect of this lane's
  ledger commit.
  FIRST LIVE RAISE OF `seam-endpoint-outside-soi`, and it is a finding in its own
  right: the run reported ONE unlisted (report-only) anomaly reason,
  `seam-endpoint-outside-soi`, an instrument the M-A7 design doc's prior-art item
  3 described as "report-only, has never fired". It has now fired. Echo evidence
  in the collected manifest: `recId 61e9177193444e329247d0e8288cf91e`,
  `pidKey 839899670`, `ut 5355599259.994832`, on the departure-loiter `DWELL`
  that OPENS in the `Sun` frame and CLOSES at `Duna` (`treatment StockConic`,
  `coverage InSegment`, `openAtExport = True`) - i.e. on the arrival SOI handoff,
  exactly where a cross-SOI endpoint check is meant to have an opinion. It moved
  no verdict (not in `hlib.ANOMALY_TOKENS`, surfaced via `unlistedReasons`) and
  is deliberately NOT added to `allowedAnomalies`: a tolerance for an ungated
  token is an inert declaration `parse_allowed_anomalies` warns about, and the
  whole-set parse cell asserts zero warnings. The design doc's item 3 is
  annotated with this run id.
  **READING FLIGHT 3 FLEW 2026-08-25 (run `2026-08-25_1616`, CLEAN PASS attempt
  1) AND THE BLOCK IS NOW ARMED OFF THE `_1502` + `_1616` PAIR.** Flight 3 was
  the gate section 1 named before it flew: a re-fly of the unchanged spec with
  the three tolerances flight 2 authored, which had to return PASS with those
  tokens RECURRING inside their stated populations. It cleared the gate exactly
  rather than merely inside the populations - `hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, the same three integers, plus the
  same single report-only `seam-endpoint-outside-soi` echo. Three counts that
  repeat to the integer across two independent flights are a deterministic
  property of this drive shape, which is what the gate was for.
  THE PAIR MATCHES FACET FOR FACET, and that is what the arming rests on: dwells
  2 (+2 open), cycles 1, transitions 2, chainBuilds 2, lineBranches 2,
  `treatments StockConic 2`, `coverages InSegment 2`, seams `rigid 11` /
  `flexible-soi 4`, `seamEndpoints 1024`, `seamTangents 0`, `holdsAboveOneX 1`,
  `seamsAboveOneX 2`, four INFO `RC-QUAL` findings and nothing worse - every one
  equal to flight 2's integer; `maxEndpointRatio 0.43669474211704823` against
  0.4366947421168355; observed hold 7061.161390 s against 7050.236864 s at the
  same per-cycle plan; `unevaluable 335146` against 334342; and the histogram
  `warp1x 322868 / warp100 10626 / warp1000 2160 / warpHigh 0 / warpPhys 0`
  against `322078 / 10602 / 2170 / 0 / 0` - **every bucket within 0.5 %**.
  **ARMED 2026-08-25**: `gating = true`, `dwells = { min = 1, max = 32 }`,
  `unevaluable = { max = 500000 }`, `requireSeamKinds = ["rigid",
  "flexible-soi"]`, and - THE FIRST IN THE SUITE - `warpBuckets = ["warp100",
  "warp1000"]`. Arming off a PAIR rather than off one green reading is a
  deliberate deviation from the V14M/V8 precedent and is stated in the spec: this
  lane's subject IS a histogram, and a histogram read once is a sample.
  Declaring `warpBuckets` also arms RC-WARP's two non-list clauses at FAIL level
  (`seamsAboveOneX` and `holdsAboveOneX` must be non-zero, both backed twice at 2
  and 1), so **RC-WARP's anti-vacuity statement is now ASSERTED and not merely
  measured** - the measurement half of this debt is closed. The unevaluable
  ceiling is ~1.5x rather than the siblings' ~3.3x because 99.8 % of the census
  is the SEAM_ENDPOINT decimation (the per-pid cap reporting loudly on a rails
  drive shape), so the ceiling is the honest anti-vacuity bound over the
  decimation regime and tightening it would red on the instrument's own
  bookkeeping. NOT declared, each for a measured reason: `warpHigh` (0 twice -
  the commanded ladder tops out at KSP rails index 5, so 1000x IS this subject's
  ceiling), `cycles` (1 CLOSED twice, so V14M's `{1,16}` window WOULD hold, but
  the closed-cycle count here is a property of three supervisor-chosen windows on
  a COMPRESSED span clock and of the export instant rather than of what this lane
  contributes), any RC-CUT surface (`cut-run-period-absent: 2` on both runs), any
  RC-HOLD clause (one engage/release pair per run), and any endpoint count window
  (the population is decimated). Arming re-pinned nothing in the flown shape and
  claimed D14 `warp-rails` in the same commit - the gate that makes the claim
  true - while still declining `warp-high`. Pins: `RENDERCOMPOSE_ARMED_SPECS`
  gains the file with both reading ids, and a third per-lane armed KEY-SET pin
  (`test_v24w_declares_the_render_composition_block_armed_with_the_warp_buckets`)
  pins the exact key set including the bucket list.
  ~~STILL OWED, AND THIS ENTRY STAYS OPEN UNTIL BOTH FLY: the ARMED RE-FLIGHT
  and the NEGATIVE CONTROL~~ **BOTH FLEW 2026-08-25 AND THE DISCIPLINE IS
  COMPLETE - SIX FLIGHTS ON THIS LANE.** THE ARMED RE-FLIGHT flew TWICE:
  `2026-08-25_1722` (PASS attempt 1, `gating=True
  armedBlocks=['renderComposition']`, ZERO mismatches; dwells 2 +2 open,
  seamKinds rigid 11 / flexible-soi 4, `holdsAboveOneX 1`, `seamsAboveOneX 2`,
  histogram `warp100 10644 / warp1000 2168 / warpHigh 0`, unevaluable 334093,
  four INFO RC-QUAL and nothing worse) and `2026-08-25_1811` (PASS attempt 1,
  ZERO mismatches, `warp100 10580 / warp1000 2162 / warpHigh 0`) - 1811 having
  been flown AS the negative control and having evaluated the UNINVERTED block,
  because its substring edit hit a rationale COMMENT quoting the same
  `warpBuckets` literal ahead of the real key (entry
  NEGATIVE-CONTROL-EDIT-NEVER-REACHED-THE-KEY above), so it reclassifies as a
  second re-flight rather than being discarded. Across ALL FOUR full PASS
  flights the anomaly triple repeated to the integer (`icon-teleport 65 /
  icon-off-orbit 2 / loop-seam-teleport 2`) and every armed facet re-measured
  inside its declared window. THE NEGATIVE CONTROL flew as `2026-08-25_1925`:
  `warpBuckets` inverted to `["warpHigh"]` - a composition token of this lane's
  OWN, measured 0 on every flight - by a LINE-ANCHORED edit of the real key,
  with `run.py --dry-run`'s `declared:` line read first to confirm the control
  was actually loaded. Verdict `PARSEK-FAIL(render-composition)` attempt 1 with
  EXACTLY ONE mismatch naming the zero-count bucket (`RC-WARP [FAIL]
  warpBuckets.warpHigh: spec declared warp bucket 'warpHigh' and the manifest
  counted zero frames in it - the run did not visit that warp regime`), every
  sibling verifier row clean, the four INFO RC-QUAL findings still beside the one
  FAIL, and the composition facets equal to the PASSing flights' - so the red is
  the declaration and nothing else. The run JSON's
  `verifiers.renderCompose.declared` records `warpBuckets: ['warpHigh']`, the
  audit surface added because of the 1811 miss and demonstrated by the very
  control that needed it; the control was reverted in the same change on the
  verified real key. Deliberately not shared with V14M/V8, on their own stated
  ground: a shared inversion re-proves the `rendercompose` evaluator rather than
  this block.
  ~~ONE RED IS PRE-REGISTERED AS LIKELY AND DELIBERATELY NOT BUDGETED AWAY~~ THE
  PRE-REGISTRATION WAS DISCHARGED AND IT PAID: the spec shipped
  `allowedAnomalies = []` although the free-play session behind its fixture
  raised `icon-teleport` 95 times on a render the operator called visually
  correct (the entry below), and the header promised in writing that a red on
  that token would be the measurement rather than a defect. Flight 2 red on
  exactly that token at 65, and because nothing was pre-tolerated, the count is
  recorded instead of swallowed - which is what gave the icon-teleport
  calibration entry its second dataset and its first machine-driven
  discriminator.
  ONE THIRD-PARTY ANOMALY, FILED AND NOT ACTIONABLE: flight 1's single
  `NullReferenceException` (17:22:29.551) is `MuMech.MechJebCore.OnDestroy`
  reading `vessel.isActiveVessel` after `FlightGlobals` teardown during
  `Application.Quit`. Zero Parsek frames in the stack, unique in a 3.8 MB log,
  0.6 s after `FlushAndQuit` returned OK and the save was written, `kspExit
  code=0`. MechJeb quit noise; no Parsek work owed.
- **The manifest header's `mapRenderTracingOn` bit should be STICKY
  (was-ever-on), not export-instant** [OPENED 2026-08-25 off the two Phase-3
  reading runs. Small, C#-side, verifier-facing]. `TryExportNow`
  (`Source/Parsek/MapRender/RenderCompositionRecorder.cs`) stamps the header from
  `MapRenderTrace.IsEnabled` AT THE EXPORT INSTANT, so the bit describes one moment rather than the run. Measured: V14M's
  `2026-08-25_0953` manifest says `mapRenderTracingOn=false` while carrying 107
  seam endpoint records that only exist because tracing was on in flight, while
  the V8 sibling read `true` off the same drive shape - the two disagree about
  runs that both flew with the tracer armed (both manifests were the
  `exportReason=process-teardown` write). It is not cosmetic: the false reading
  is what put `seam-data-unavailable-tracing-off: 1` into V14M's unevaluable
  census, so a lane can look tracer-off to the verifier while its seam numbers
  are real. ~~Fix: accumulate a was-ever-on flag in the recorder (set on any
  frame the tracer is enabled) and stamp THAT~~ FIXED 2026-08-25 in the arming
  pass. `RenderCompositionRecorder.mapRenderTracingWasEverOn` is latched by
  `LatchMapRenderTracing()` once per ARMED frame (from `Update`, after the
  `IsEnabled` gate) plus once more at export, and cleared ONLY in `Reset()` -
  the same boundary at which the accumulated records are dropped, so the bit can
  never describe a population it did not accumulate with. The header KEY
  `mapRenderTracingOn` is unchanged (the Python reader keys off that spelling
  verbatim); its semantics are documented on
  `RenderCompositionManifest.ManifestHeader.MapRenderTracingOn` and in
  `rendercompose.py`'s unevaluable-discipline docstring. No second export-instant
  key was added - nothing consumes one. Cells:
  `RenderCompositionRecorderTests.StickyTracingBit_*` (four: starts false and
  latches, survives the tracer going quiet, clears with the records, still
  serializes under the unchanged key). PYTHON SEMANTICS RE-CHECKED and unchanged:
  `seam-data-unavailable-tracing-off` still raises only on "record family empty
  AND header false", which the sticky bit makes honest in BOTH directions - the
  complementary reading (header true, empty seam family) is now a MEASURED
  absence rather than an unknown instrument, and correctly raises nothing.
  CONSEQUENCE FOR THE LANES, predicted then MEASURED on V14M's armed re-flight
  `2026-08-25_1050`: the bit read `true` and
  `seam-data-unavailable-tracing-off` is gone from the census, so the fix is
  CONFIRMED - but the predicted 56 -> 55 total did not land, because
  `seam-endpoint-skipped` independently ran 106 against the reading run's 53 and
  the total read 108. The prediction was about the one reason and held; the total
  is not a controlled quantity, which is exactly why the armed
  `unevaluable = { max = 200 }` window is a runaway guard rather than a pin.
- **Phase 3b (the MUN subject + the first two-complete-cycle RC-CYCLE dataset).
  IN PROGRESS - lane AUTHORED 2026-08-25 on branch `mun-composition-lane`,
  READING RUN TAKEN 2026-08-25 (`2026-08-25_2056_V6M-mun-player-loop`), ARMING
  PASS OWED.** Additive to a COMPLETE Phase 3, not a re-opening of it: the
  three armed lanes above stay exactly as they are.
  THE OPERATOR'S QUESTION, verbatim: "did we get to test if a looped mission to
  the Mun renders correctly after 1, 2 periods?" It had TWO gaps, and this item
  closes both.
    * GAP (a): NO MUN SUBJECT. All three declarers are Ike / Eve / Duna; no
      composition accounting has ever gated on a Kerbin->Mun loop.
    * GAP (b): NO LANE ANYWHERE HAS FED RC-CYCLE TWO COMPLETE CYCLES, so the rule
      has never actually compared two structures. The mechanism, read off the
      code rather than assumed: `rendercompose._cycle_windows` pairs CONSECUTIVE
      `cycle-rollover` clock events (N events -> N-1 closed windows) and
      deliberately does NOT synthesise the trailing open cycle against the export
      UT ("a cycle the export cut in half is not a cycle"); `_rule_cycle` then
      skips any unit with `len(windows) < 2` as `no-cycle-rollover-events`
      unevaluable. `RenderCompositionRecorder.ObserveUnitFrame` emits ONE
      rollover per CHANGE of `frame.CycleIndex`, and on a SCHEDULED (zero-drift)
      unit `ComputeSpanLoopFrame` takes the schedule branch - `CycleIndex = sIdx`
      (the index of the largest scheduled launch <= currentUT), UNRESOLVED before
      the first one - so the rollover count equals the number of scheduled
      launches the clock ENTERS. Every loop lane in the suite flies TWO cycles
      and therefore closes ONE: V14M measured exactly that
      (`clockEvents {cycle-rollover: 2}` -> `cycles 1` +
      `no-cycle-rollover-events: 1`).
  WHAT WAS AUTHORED: `harness/scenarios/V6M-mun-player-loop.toml` gained a THIRD
  cycle (one `StartLoopPlayback`, the same -180/-60/+140 arrival bracket about
  the cycle-3 seam, the same +20,800 park epoch, and deliberately NO third
  `EnterWatchMode` - a watch attempt is a pinned-verdict bet), one
  `ExportRenderManifest` immediately before `FlushAndQuit`, and a BARE
  `[expectations.renderComposition]`. 21 steps -> 27, and NOTHING in the existing
  flown shape moved: no jump UT, no budget, no expectation (the S4.1 rule). The
  lane already armed the three tracers the seam capture needs. It is registered
  in `RENDERCOMPOSE_DECLARER_SPECS` as the FOURTH declarer and is deliberately
  ABSENT from `RENDERCOMPOSE_ARMED_SPECS`.
  THE CYCLE-3 ANCHOR IS DERIVED, NOT GUESSED, and the derivation is validated
  twice before it is used: replaying `TryFindNextScheduleK`'s purely periodic
  filter with the throttle `ceil((L_last + span - ut0)/T_anchor)` reproduces this
  lane's two MEASURED anchors (k=13 -> 280,176.9473801677, k=26 ->
  560,319.4747603355) and the product's own
  `scheduleWorstResidual=4347.54846243246` to the digit, then continues the
  header's faithful-k series to **k=45 -> relaunchUt 969,758.553** (throttleK 28,
  residual 3,166.503) - hence seam 986,129.115 and park 990,558.553. A mis-pin
  self-detects as the already-forbidden `timejump refused reason=backward-jump`.
  WHY THIS SUBJECT AND NOT A SECOND COPY OF V14M: V6M is PAD-ROOTED with TWO
  constraints and a NON-UNIFORM ZERO-DRIFT SCHEDULE (`zeroDrift=yes`, faithful-k
  13, 26, 45, 58), where V14M is orbit-rooted, single-constraint and
  uniform-cadence - so RC-CYCLE's `cycleLengthResidualsSeconds` trend
  (`(hi - lo) - unit.cadenceSeconds` per window) is a genuinely different surface
  here. And both windows land in the SAME warp bucket (`warp1x`, every clock move
  being an instantaneous `TimeJump`), which is RC-CYCLE's SHARP FAIL-level
  isomorphism clause rather than its cross-bucket INFO one - so the comparison
  gets its strong form on its first outing. `warpBuckets` must NEVER be declared
  here (1x-only by construction; V24W owns the histogram).
  THE READING RUN IS IN: `2026-08-25_2056_V6M-mun-player-loop`, verdict PASS,
  `renderCompose` status REPORT. Steps (1) and (2) of the owed list are DONE and
  both headline predictions LANDED. `clockEvents {cycle-rollover: 3,
  inter-cycle-tail: 2}` -> **`cycles = 2`**, the suite's first two-closed-cycle
  dataset, and **RC-CYCLE EVALUATED for the first time anywhere** rather than
  reporting `no-cycle-rollover-events` - it found the two closed cycles
  **ISOMORPHIC** and raised nothing, with both windows in the SAME `warp1x`
  bucket (`warpBuckets {warp1x: 250}`), i.e. the SHARP FAIL-level form of the
  clause rather than the cross-bucket INFO one. Facets for the arming pass:
  `treatments {StockConic: 2, TracedPath: 3}`, `dwells 5` + `openDwells 3`,
  `transitions 5`, `chainBuilds 3`, `planUnits 1`, `lineBranches 11`,
  `coverages {InSegment: 5}`, `seamEndpoints 109`,
  `seamKinds {rigid: 21, flexible-soi: 3}`, `seamTangents 0`,
  `ownershipChanges 0`, `unevaluable 110`
  (`seam-endpoint-skipped` 109 + `warp-hold-traversal-evidence-absent` 1),
  `cycleLengthResidualsSeconds [-2.53, 129297.47]` (the second window spans the
  schedule's k=26 -> k=45 step, so the large residual is the NON-UNIFORM schedule
  reading correctly), `exportReason process-teardown`, `scene FLIGHT`,
  `mapRenderTracingOn true`.
  STILL OWED: (3) an arming pass by operator decision, whose obvious first clause
  is `cycles = { min = 2 }` - the one floor no other lane in the suite can carry -
  followed by the armed re-flight and a negative control inverting a window of
  this lane's OWN. READ `RC-OWN-DRAW-HALF-IS-MAP-GATED` below FIRST: on this lane
  (and on every other manifest lane today) RC-OWN's draw->publish direction is
  structurally unevaluable, so "no RC-OWN finding" must not be armed as
  "ownership conserved".
  A FAIL-level RC-CYCLE role-structure finding on the reading run would have been
  a MEASUREMENT, not a spec bug (a bare block gates on nothing): the render-side
  statement of "cycle 2 does not arrive where cycle 1 did", which is the question
  this lane's own desync hunt asks with coarser instruments. It did not fire -
  on this subject, at this resolution, cycle 2 arrives where cycle 1 did.
- **Phase 4 (routes + product).** Ride G1's B27/V18 for the route surfaces so
  RC-ROUTE evaluates against a real route line, and start the RC-QUAL trend
  record (kink angles, endpoint ratios, hold durations) that feeds
  promote-to-fixed decisions on the ratified visual artifacts.
  **WAVE B AUTHORED 2026-08-26 - TWO NEW LANES, BOTH BARE, BOTH UNFLOWN.** These
  are not Wave A's shape: Wave A declared the block on lanes that had already
  flown, while these are NEW LANES against the two fixtures the Phase-4 harvest
  landed, so each owes a FIRST FLIGHT before it owes a window. Declarer roster
  19 -> 21.
  `V18T-depot-route-ts-arrival` over `depot-route-recorded` is **the route half
  of this bullet and G1's first lane of any kind**: the only subject in the suite
  that can emit a `ROUTE_LINE_BUILD` record or a per-unit `ROUTE` node, so
  `rendercompose._rule_route` has never executed against live data and
  `routeLineBuilds` has never read non-zero anywhere. It arms NO mission loop -
  the committed ROUTE drives - and flies the TS host first on the measured fact
  that `RouteTrajectoryLineRenderer.DrawAll`'s only call site carries no
  `MapView` gate. ~~D10 `route-map-lines` stays UNDECLARED until a gating token
  earns it.~~ **FLEW AND ARMED 2026-08-26**: two matching readings, armed
  re-flight `2026-08-26_2015` PASS, negative control `2026-08-26_2017_a2` red on
  `renderComposition.routeLineBuilds 1 < min 5`, and D10 `route-map-lines` is now
  DECLARED - the gating token earned it.
  `V25M-duna-park-player-loop` over `duna-park-recorded` is **re-aim's second
  departure class** - a heliocentric-parking departure, the path
  `ReaimClassifier`'s own exception comment names by fixture and that no
  committed lane has driven; its manifest would also carry the first
  DESTINATION-side loiter cut (43,963.92 s) rather than V8's launch-side one.
  STILL OWED on this bullet: both readings, then the arming passes, plus the
  RC-QUAL trend record, which no lane has started.

DEFERRALS TAKEN IN PHASES 1-2, each of which a lane author must know.

- **`RC-OWN-DRAW-HALF-IS-MAP-GATED`: no manifest lane has ever observed the
  polyline draw/publish half, because none of them opens the map view. INSTRUMENT
  / LANE-SHAPE GAP, NOT A PRODUCT DEFECT [FOUND 2026-08-25 by diagnosing V6M's
  reading run `2026-08-25_2056`, which raised three report-only RC-OWN FAILs].**
  The finding as it read: `recId=4cfa06ce...: a visible TracedPath dwell
  [296370, 296690] / [576510, 576830] / [985950, 986270] exists with NO ownership
  record`, one per cycle, at exactly the three arrival `TimeJump` brackets.
  THE DIAGNOSIS (manifest + collected KSP.log + source): RC-OWN's two halves do
  not share a gate. The INTENT half - `ShadowRenderDriver.RunFrame`, which stamps
  the TracedPath intent that the DWELL records AND the stock line/icon
  suppression both read - is driven from `ParsekFlight`'s per-frame update and
  runs map-open or not. The PUBLISH half - `drewNonOrbitalLegRecordings` ->
  `RenderCompositionRecorder.NoteOwnershipPublish` - sits at the END of
  `GhostTrajectoryPolylineRenderer.Driver.LateUpdate`, whose SECOND statement is
  `if (!MapView.MapIsEnabled) return;`.
  THE EVIDENCE, all three independent: (1) the collected KSP.log carries the
  Driver's awake + destroy lines and ZERO `Polyline frame:` summaries - that
  summary is emitted at the end of EVERY completed walk, so zero means every
  LateUpdate bailed early, and the only other early return is the scene gate,
  which cannot have fired (`scene = FLIGHT`); (2) all three descent dwells carry
  `markerPolyline = False` with `markerIconSuppressed = True`, i.e. the marker
  rode the icon fallback because no leg drew; (3) `ownershipChanges = 0` for the
  whole run, and no seam verb opens the map - there is no `EnterMapView` command
  and no production path calls `MapView.EnterMapView`.
  SCOPE: this is the state of EVERY manifest lane today. V24W measured
  `ownershipChanges = 0` as well and escaped only because it never opened a
  TracedPath dwell; V6M is simply the first lane whose Director did. With the map
  closed nothing is drawn on any map surface, so a visible TracedPath dwell is an
  INTENT record and not evidence of a draw: the rule's premise was never
  established, and the three FAILs were the instrument speaking about a question
  the run never asked.
  DONE IN THIS PASS (minimal, one clause): `rendercompose._rule_own`'s
  draw->publish direction now names the precondition. ZERO `OWNERSHIP_CHANGE`
  records ANYWHERE stands the direction down as the defined-unevaluable
  `ownership-publish-surface-never-ran` (counted in the census, never silent);
  ONE publish anywhere proves the walk ran past the map gate, and from there a
  recording whose visible TracedPath dwell has no ownership record keeps its FAIL
  - that IS the leg-that-never-draws defect the direction exists to catch. The
  `falls outside every published ownership interval` clause is untouched (it
  already requires that recording to have published). Two cells pin both halves
  in `harness/lib/test_rendercompose.py`.
  ~~STILL OWED, and it is NOT a rule edit: a MAP-OPEN LANE, so the draw half is
  observed at least once. That needs a new command-seam verb (a C# change: the
  seam has no map-view verb at all)~~ **THE VERB HALF IS BUILT (2026-08-26).**
  The seam now carries `EnterMapView` and `ExitMapView` (M-A2, additive: 27
  implemented / 7 reserved), so the sentence above that says "there is no
  `EnterMapView` command" is history from here on. Both are SINGLE-PHASE, no
  args, precondition `RequiresFlight`, idempotent (`OK alreadyOpen=true` /
  `alreadyClosed=true` without calling stock), and their OK is a READ-BACK of
  `MapView.MapIsEnabled` - the very property the polyline Driver's LateUpdate
  gates on - never the bare fact that the void stock call returned. Single-phase
  is a decompile fact, not a convenience: KSP 1.12.5's `MapView.enterMapView()`
  assigns `MapIsEnabled = true` and fires `GameEvents.OnMapEntered` synchronously
  before returning (the deferred `Invoke("endEnterMapTransition", ...)` after it
  only disables the UI cameras), and `exitMapView()`'s FIRST statement is
  `MapIsEnabled = false`. Refusals split on WHO declined: `REJECTED
  map-view-unavailable` is the one PRE-call gate (null `MapView.fetch`, stock
  never called), and everything AFTER the call is `ERROR` - `map-not-entered` /
  `map-not-exited` when the read-back still disagrees (stock declined through
  `ConstantMode`, `CanUseMap` off, or a `MissionSystem` camera-switch block; the
  class is deliberately NOT re-derived) and `map-view-threw` when it threw. That
  is `SimulateStockSwitchClick`'s line verbatim (REJECTED before the stock call,
  ERROR after), so a spec's `expect` has one rule to learn rather than a per-verb
  exception.
  Files: `TestCommandMapViewVerbs.cs` (pure) +
  `ParsekTestCommandAddon.MapView.cs` (applier) + the verb / interface /
  precondition tables, `hlib.IMPLEMENTED_SEAM_VERBS` with both role rows
  (`world-mutating` on the tail axis, `recording` on the post-mission axis), and
  the design doc's "Update (the map-view pair)" block.
  ~~WHAT IS STILL OWED ... (1) ADD THE STEP TO A LANE~~ **(1) IS DONE
  (2026-08-26): `V6M-mun-player-loop` ADOPTED THE PAIR**, and it took the
  placement this entry asked for. `EnterMapView` sits immediately after the three
  tracer `SetSetting`s and BEFORE `MissionConfig`, so the polyline Driver walk
  runs during EVERY observation window the lane opens - all three cycles' arrival
  brackets and all three park epochs - rather than only the tail. An
  `EnterMapView` after the first bracket would leave cycle 1 measuring the old
  map-closed shape and make the reading incomparable across the three cycles,
  which a two-closed-cycle dataset must not be.
  `ExitMapView` goes BEFORE `ExportRenderManifest`, and THAT placement is FORCED
  rather than chosen: `test_every_declarer_exports_immediately_before_teardown`
  pins the last two commands of every declarer as
  `[ExportRenderManifest, FlushAndQuit]`, so an `ExitMapView` between them reds
  the cell. It costs nothing - the recorder has accumulated every observation by
  then, and the export is an in-memory read that does not need the map open.
  THE FLOWN SHAPE MOVED (`EnterMapView` is `TAIL_ROLE_WORLD_MUTATING`), which is
  admissible without an armed re-validation debt only because V6M is
  DECLARED-UNARMED on `[expectations.renderComposition]`; the two save-structure
  blocks it DOES gate cannot see a map-view toggle (no vessel, no save, no Parsek
  persisted state), and no jump UT, budget or existing expectation moved.
  ~~WHAT IS STILL OWED ... (2) THE READING THAT PROVES THE PUBLISH FLOWS~~
  **(2) IS DONE - THIS ENTRY IS CLOSED (2026-08-26).** The reading flew as
  `2026-08-26_1745_V6M-mun-player-loop`, PASS attempt 1, and BOTH pre-registered
  criteria landed: the collected `KSP.log` carries `Polyline frame:` summaries
  (the 2026-08-25 run carried zero) and the manifest reports
  `ownershipChanges = 6` - THREE CLEAN appear/disappear PAIRS, one per TracedPath
  dwell, all on `recId=448cd680`, at `[296370, 296690]`, `[576510, 576830]` and
  `[985950, 986270]`: the SAME three brackets whose missing records raised the
  original three FAILs. RC-OWN findings went 3 -> 0 and
  `ownership-publish-surface-never-ran` is GONE from the run's unevaluable
  reasons entirely, so the stand-down that had fired on every manifest lane in
  the suite is lifted BY EVIDENCE rather than by decision.
  **OWNERSHIP IS CONSERVED ON THIS SUBJECT, and the three earlier FAILs are
  confirmed as the instrument gap this entry diagnosed rather than a
  leg-that-never-draws defect.** The caveat above is what makes that a real
  result: the verb was necessary but not sufficient, a map-open lane could still
  have published nothing, and THAT would have been a real finding. It published.
  The rest of the composition reproduced `2026-08-25_2056` exactly (cycles 2,
  isomorphic again; dwells 5 +3 open; transitions 5; seams rigid 21 /
  flexible-soi 3; cycle residuals identical to the digit), so the closure is not
  bought with a changed subject. A pass may now read "no RC-OWN finding" as
  "ownership conserved" ON A LANE THAT OPENS THE MAP AND PUBLISHES; the design
  doc's ratified deviation #5 amendment is discharged on the same evidence. What
  remains for V6M is ARMING (windows off the pair `2026-08-25_2056` +
  `2026-08-26_1745`, then the armed re-flight and the negative control), which is
  a scenario-ledger item and not this entry's.
- **`RC-SEAM` blamed the LAST boundary of a warped-over transition. VERIFIER
  MISREAD, NOT A RENDERER DEFECT. FIXED 2026-08-26** [found by diagnosing V25M's
  reading run `2026-08-26_1744`, which red
  `RC-SEAM [FAIL] TRANSITION[pid=3129690249 ut=5360143765.0]: body change
  Sun->Duna classified as a rigid seam`].
  THE DIAGNOSIS. A `TRANSITION` is a DWELL-stream event: it fires when the
  Director's rendered segment index MOVES, and a dwell only opens on a segment
  the render clock actually sat in. V25M's arrival straddle warped the head clean
  ACROSS an interior segment, so the record read
  `fromSegmentIndex 6 -> toSegmentIndex 8` and spanned TWO boundaries. The chain
  classified both correctly: boundary 7 is the real crossing (PHASE[6]
  heliocentric-transfer Sun -> PHASE[7] departure-loiter Duna) and it is
  `flexible-soi`; boundary 8 is Duna -> Duna and it is `rigid`.
  `RenderCompositionRecorder.NoteChainBuild` emits `BoundaryIndex = i` for
  segment `i`'s `LeadingSeam`, so keying the seam table on `toSegmentIndex` -
  which wave-1's `_rule_seam` did - reads the LAST boundary of the span and
  blames its correct `rigid` for a body change that happened at an earlier one.
  `2026-08-25_0956` (V8) is the near miss that shows the clause was never really
  being evaluated on this shape: the same 6 -> 8 span with a Mun -> Sun change
  passed only because BOTH crossed boundaries happened to be `flexible-soi`.
  THE FIX: `rendercompose.transition_boundaries` (pure) enumerates every boundary
  a transition crossed and takes each one's bodies from the CHAIN's own `PHASE`
  records, so the rule tests the boundary the body actually changed at. The
  observed `fromBody -> toBody` pair is used only for a SINGLE-boundary span,
  where the two agree by construction (this keeps `assembler-fallback` chains
  evaluable). A multi-boundary span the PHASE list cannot resolve is the new
  defined-unevaluable `seam-boundary-bodies-absent` - counted, never guessed. A
  retire (`toSegmentIndex = -1`) or a loop wrap names no boundary at all.
  Findings now carry `boundary=N` in the target.
  VERIFIED OFFLINE ACROSS THE WHOLE ARCHIVE: the V25M manifest re-reads with ZERO
  findings and all SIXTEEN other archived manifests are byte-identical (same
  findings, same unevaluable counts). Five cells in
  `harness/lib/test_rendercompose.py` pin the shape, including the negative
  control where the interior boundary IS rigid and the finding must name it.
- **Seam measurement is double-gated.** The tangent and endpoint evaluation
  sites are `mapRenderTracing`-gated and were NOT widened, so a manifest lane
  that wants RC-SEAM / RC-QUAL numbers must arm BOTH `PARSEK_RENDER_MANIFEST=1`
  and the `mapRenderTracing` setting. With tracing off the header says so and
  the verifier reports `seam-data-unavailable-tracing-off` as a DEFINED
  unevaluable - never a silent pass.
- **Transition endpoint positions ride the same gate.** They come from the
  `MapRenderProbe` truth push, and the probe runs only when tracing is on; with
  it off the position clauses are defined-unevaluable.
- **RC-HOLD's marker half is capped pending V6.** Leg 2 compares the observed
  `hold-release` accumulated seconds against the recomputed value with a
  tolerance of `max(2 s, 2 * local maxUtStep)`, and a hold whose accumulated
  stall never reaches the C# detector's `HoldMinStallSeconds = 5.0` emits no
  engage/release pair at all (below-resolution, not a mismatch). Sizing that
  half wants a V6-class lane that actually dwells through a hold at a known
  warp. UPDATED 2026-08-25 with the C# review pass, which replaced the
  frame-step stationarity test with STALL ACCUMULATION: the resolution floor is
  now that 5 s constant rather than one live frame step (the old test could not
  see a 1x hold at all, and misread a mid-hold warp drop as a release plus a
  second engage), and MORE THAN ONE run per `(ownerIndex, cycleIndex)` is a legal
  shape. UPDATED AGAIN by the same pass's second review: the STATIONARITY
  predicate is now RELATIVE - a frame counts as stalled only when
  `|delta loopUT| < min(HoldStationaryLoopUtEpsilonSeconds, 0.5 * liveStep)` on a
  positive live step, so the 0.25 s constant is a CEILING on the window rather
  than the window itself. The absolute form sat ABOVE ordinary 1x clock advance
  (~0.02 s per frame), so plain 1x playback accumulated a phantom stall past the
  5 s floor and reported a hold nobody planned - and a real hold that released at
  1x never emitted its release, because no 1x frame ever left the window either.
  A hold freezes the render clock EXACTLY (the hold formula returns a constant
  phase), so near-zero is the true signal and the relative form is warp-proof in
  both directions. Two consequences a lane author has to know. (1) Pairing is on
  `(ownerIndex, cycleIndex, detailA)` where `detailA` is the run's 0-based
  ORDINAL - it used to repeat the cycle index. (2) The plan predicts ONE arrival
  hold per cycle, so exactly one run per cycle is compared and the rest are
  counted `hold-run-not-attributable-to-planned-hold`.
  ~~a lane that wants the second stall adjudicated needs the
  frozen-loopUT-vs-hold-position join the manifest does not carry yet~~ **DONE
  2026-08-25 (leg-2 attribution by frozen loopUT), forced by the first free-play
  ground-truth manifest.** The join the old note said the manifest did not carry
  was already there: `ObserveUnitHoldRun` stamps `StallStartLoopUT` into
  `detailB` on BOTH hold events, and the plan says where the arrival hold freezes
  the clock - `rendercompose.arrival_hold_frozen_loop_ut`, composed the way
  `SpanClock` composes it
  (`decompress_span_ut(spanStartUT + (compress_span_ut(arrivalHoldAtUT, cuts) -
  spanStartUT), cuts)`; NOT plain `arrivalHoldAtUT`, which is the identity only
  when the hold instant sits past the end of every cut before it). A release is
  now compared only when its frozen loopUT sits within `max(2 s, 2 * local
  maxUtStep)` of that position; longest-per-cycle survives only WITHIN the
  attributable set.
  WHY IT COULD NOT WAIT: on `.scout/s15-duna-manifest.txt` - the first free-play
  session, operator-eyeballed as correct - the old rule manufactured a FAIL. One
  unit carried three engages: a 12.346 s launch-repay stall in cycle 6 frozen at
  the span end (`detailB` 70964232.983), the GENUINE arrival hold in cycle 7
  frozen exactly at `arrivalHoldAtUT` (70898646.058, observed 55400 s against a
  55581.371 s prediction - agreeing to 181 s inside a 40000 s tolerance), and a
  third engage still open at scene exit. Cycle 6's only release was the repay
  stall, so longest-per-cycle compared it against cycle 6's 53299.326 s
  arrival-hold prediction and red at delta -53286.98 s. With positions compared
  it is 65586.92 s away from where the arrival hold freezes and is simply not the
  run the prediction is about. Post-fix that manifest yields ZERO RC-HOLD
  findings, one `hold-run-not-attributable-to-planned-hold` and one
  `hold-engaged-never-released`.
  TWO FALLBACKS, so a schema-poor manifest does not silently stop being checked:
  when the plan carries no `arrivalHoldAtUT`, or when NO release on the unit
  carries a frozen position, attribution is undecidable and the rule reverts to
  the pre-calibration longest-per-cycle pick. A release missing `detailB` on a
  unit where others carry it is NOT attributable.
  NEW COUNTED UNEVALUABLE: `hold-engaged-never-released`, for an engage the
  manifest never releases. Deliberately NOT the FAIL a release-with-no-engage
  gets - a release is only ever emitted by a run the detector already engaged, so
  a release alone means the record lost a row, while an engage owes nothing yet
  and a scene exit mid-hold produces exactly this.
- **The InteriorGap duration bound uses the unit CADENCE.** The design's
  seam-gap-plus-reseed bound is the Phase-3 tightening; the module docstring
  says so.
- **RC-OWN's publish->draw direction is WARN, not FAIL, pending live
  calibration** (design deviation #5). Two ratified populations legitimately
  publish ownership without a TracedPath dwell - proto-less pid-0 recordings
  (no Director dwell exists at all) and the Driver-direct bridge / forward legs
  (the concurrent dwell is `StockConic`) - and both are exempted by name and
  counted. Whatever is left over is REPORTED at WARN and counted
  (`ownPublishWithoutDraw`) because no live run has yet shown whether a third
  benign population exists. The MIRROR direction, a draw with no publish, stays
  FAIL. A lane that wants this armed should first read `ownPublishWithoutDraw`
  off a report-only run and confirm it sits at zero.
  CALIBRATION DATA, 2026-08-25, from the first free-play ground-truth manifest
  (`.scout/s15-duna-manifest.txt`, a Kerbin-Duna re-aim loop the operator
  visually validated as correct): `ownPublishWithoutDraw = 2`, and read out of
  the record BOTH turned out to be the SAME population - and it is not "a publish
  with no draw". Both are a RENDER-ENTRY LEAD-IN: an ownership window that closes
  a fraction of a second BEFORE that recording's FIRST `DWELL` record opens.
  * `61e9177193444e329247d0e8288cf91e` (transfer member, pid 3445082362, 18
    dwells): publish `[5329053994.561617, 5329053997.561617]`, first dwell opens
    5329053998.061617 - a 0.5 s gap, under that dwell's own 2.5 s `maxUtStep`.
    Its other EIGHT publishes all intersect a dwell and raise nothing.
  * `6561c8eb97dd48d6825e9d6c7c04d22a` (member 27, pid 650833675, exactly ONE
    dwell, still open at export): publish `[5329057525.398653,
    5329057531.898653]`, its only dwell opens 5329057532.898653 - a 1.0 s gap.
  MECHANISM: `drewNonOrbitalLegRecordings` publishes on the frame the Director
  actually draws, while the `DWELL` record for that ghost opens on a later
  classification frame, so a sub-frame publish window sits ahead of the
  recording's first dwell. Neither ratified exemption can see it - (a) wants NO
  dwell anywhere and both recIds have dwells; (b) wants a CONCURRENT `StockConic`
  dwell and the dwell starts just after the window closes.
  NO RULE CHANGE was made off this. Both shapes are now pinned as fixtures in
  `harness/lib/test_rendercompose.py` (`test_s15_shape_a_...`,
  `test_s15_shape_b_...`, plus the one-second-later control that IS claimed by
  exemption (b)), so a later pass that ratifies a lead-in exemption - or promotes
  the rest of deviation #5 to FAIL - has to move them deliberately instead of
  rediscovering the population from another live run. A lane arming this
  direction today would still red on a correct session, so it stays WARN.
- **RC-COVER counts only VISIBLE dwells as coverage.** An invisible dwell whose
  coverage is `InInteriorGap` or `OutsideWindow` is a RATIFIED hidden window and
  still explains its span (counted as `coverRatifiedHiddenSpans`); an invisible
  dwell whose coverage is `InSegment` is NOT coverage - a covering segment
  existed and the leg still did not draw, which is the defect class the rule
  exists for. A lane reading a high `coverRatifiedHiddenSpans` is reading a run
  whose "coverage" is mostly cataloged darkness, not drawn line.
- ~~**The Phase-1 tail is still open:** one manual armed play session producing a
  manifest over a committed loop fixture.~~ **DONE 2026-08-25.** The session flew
  (Kerbin-Duna re-aim loop, `exportReason=scene-exit`, warp100 / warp1000 /
  warpHigh all covered, operator-eyeballed as correct); the manifest is
  `.scout/s15-duna-manifest.txt` (495 KB) and the collected run is
  `logs/2026-08-25_1537_s15-duna-one-manifest-run2`. The recorder's hooks DO fire
  in the shapes the rules expect on real geometry: 1 plan unit, 2 chain builds,
  17 closed + 2 open dwells, 17 transitions, 10 clock events (3 hold-engage /
  2 hold-release), 20 line branches, 30 ownership changes, 1024 seam endpoints.
  Everything the reading disagreed with the operator about was the INSTRUMENT's
  fault and is calibrated in this section (leg-2 attribution, the two RC-OWN
  lead-ins, the seam-endpoint volume, the `icon-teleport` entry below).
- **Free-play SEAM_ENDPOINT volume: the per-pid cap is a CLIFF over a long
  session, so the section now DECIMATES above half of it** [OPENED and FIXED
  2026-08-25 off the first free-play manifest]. A harness flight is minutes long
  and never reaches `MaxSeamRecordsPerPid = 512`; a free-play session reaches it
  in minutes and everything after it is dropped, so the section describes the
  HEAD of the session and is silent about the rest. Measured on
  `.scout/s15-duna-manifest.txt`: 1024 endpoints admitted against 28985 dropped
  (`truncated-section-seam_endpoint`) - 3% of the session, all of it at the
  front. FIX: `RenderCompositionManifest.TryPassSeamEndpointDecimation` counts
  per-pid OFFERS (not accepts - an accept-keyed rate stalls the moment the cap
  bites and reproduces the cliff) and, above
  `SeamEndpointDecimationThreshold = MaxSeamRecordsPerPid / 2`, admits one offer
  in `SeamEndpointDecimationKeepEveryNth = 8`. The caps stay the FINAL bound;
  decimation only changes WHICH offers reach them, so a pid now spans ~2304
  offers before the cliff instead of 512. Thinned drops are counted like any
  other drop but under their own TRUNCATED kind, `seam-endpoint-decimated`, so
  the Python ledger can tell a thinned section (trend intact, rate reduced) from
  a cut-off one: `decimated-section-<section>` instead of
  `truncated-section-<section>`, a `decimatedSections` facet, and an RC-QUAL
  INFO trend row. SEAM_TANGENT passes the gate untouched (shares the caps, not
  the volume problem). Below the threshold nothing changes at all, which is why
  `Source/Parsek.Tests/Fixtures/RenderManifest/sample-manifest.txt` is byte-
  identical. Cross-language token pin:
  `test_the_decimated_kind_token_is_the_one_the_writer_declares` reads the C#
  declaration, so a rename on either side reds instead of silently degrading
  every thinned section back into a cliff.
- **`MapRenderProbe`'s `icon-jump` / `icon-teleport` threshold looks
  over-sensitive above warp1000 and needs its own calibration pass**
  [OPENED 2026-08-25 off the first free-play ground-truth manifest. FILE-ONLY:
  the probe was deliberately NOT changed. Owner: whoever owns `MapRenderProbe`].
  The s15 session was played by hand and visually validated as CORRECT by the
  operator, and current code raised `icon-teleport` **95 times** during it
  (evidence: `logs/2026-08-25_1537_s15-duna-one-manifest-run2` plus the
  manifest's own `anomalyEchoes` census, which reports exactly
  `{"icon-teleport": 95}` and no other reason). 95 raises on a session with no
  observed defect is either a real render defect the operator could not see or a
  threshold that does not survive rails warp - and the manifest carries the
  discriminator for telling those apart: its warp histogram over the same frames
  is `warp1x 0 / warpPhys 0 / warp100 4727 / warp1000 15488 / warpHigh 11446`,
  i.e. the session is ENTIRELY above warp100 and mostly at warp1000+. The
  calibration pass owed is a per-raise warp attribution: bucket the 95 raises by
  the warp regime of the frame that raised them, and if they concentrate at
  warp1000+ the threshold is the suspect rather than the render. NOTE what is
  already known not to be the cause: the delta is already measured in the orbit's
  OWN reference-body frame precisely so a body's world motion cannot dominate at
  high warp (see the `MapRenderProbe` entry in `.claude/CLAUDE.md`), so this is
  not the raw-world-delta mistake being re-made. `icon-teleport` is a GATED token
  (`hlib.ANOMALY_TOKENS`, promoted 2026-08-04) with a `maxCount` budget, so an
  over-sensitive threshold costs real lanes real reds - which is why this is
  filed rather than left as a curiosity.
  **SECOND DATASET 2026-08-25, and it is the one this entry was waiting for: a
  MACHINE-DRIVEN commanded-rails session over the SAME subject raised the token
  65 times.** V24W reading flight 2
  (`harness/results/2026-08-25_1502_V24W-duna-one-warp-stair.json`) flew
  `fixtures/saves/duna-one-recorded` - the harvest of the very s15 session above
  - through a commanded stair (10x/50x/100x/1000x/100x/50x/10x at each of three
  windows) and came back `anomalySweep hitCounts {icon-teleport: 65,
  icon-off-orbit: 2, loop-seam-teleport: 2}`, PARSEK-FAIL(anomaly) on a
  deliberately empty `allowedAnomalies` with every other verifier row green.
  WHY THE PAIR MATTERS: both sides are VISUALLY-VALIDATED-SUBJECT contexts (the
  s15 session was eyeballed correct by the operator; V24W replays that same
  recorded state by machine), so what differs between them is the DRIVE, not the
  subject - 95 raises hand-played versus 65 raises machine-driven. AND THE
  DISCRIMINATOR THIS ENTRY ASKED FOR NOW EXISTS ON BOTH SIDES: s15's histogram is
  `warp1x 0 / warpPhys 0 / warp100 4727 / warp1000 15488 / warpHigh 11446`
  (entirely above warp100), while V24W's is `warp1x 322078 / warp100 10602 /
  warp1000 2170 / warpHigh 0 / warpPhys 0` (overwhelmingly AT 1x, with a bounded
  rails excursion and nothing at all above warp1000). So the raise count did NOT
  collapse with the time-above-warp100 fraction - a ~96 % 1x session still raised
  two thirds as many as a 0 % 1x one - which is a real constraint on the
  "threshold does not survive rails warp" hypothesis and the reason the owed work
  is still the per-raise warp attribution rather than a formality. Both manifests
  carry per-raise `ANOMALY_ECHO` records with `ut` stamps, so that attribution is
  now an offline join over two committed artifacts rather than another flight.
  **THIRD DATASET 2026-08-27, AND IT IS A DISCRIMINATOR RATHER THAN ANOTHER COUNT:
  `V20T-jool-kerbin-ts-arrival` reading run 1 (`2026-08-27_1857`) raised the token
  THREE times, ALL AT `warpRate=1 dt=0.0167`** - dead 1x, the regime this entry's
  hypothesis says should be clean - **and each raise carries the orbit pair it jumped
  between**: `fromOrbit=[Jool|10246978796|0.9424] toOrbit=[sma=590325785 ecc=0.0000]`,
  i.e. the recording's post-escape Jool ellipse re-seeded onto its SEGMENT ZERO, with
  `dPos` 334,981,793 / 1,623,652,431 / 2,935,994,339 m. **THOSE THREE ARE NOT THRESHOLD
  FALSE POSITIVES - they are a real re-seed the probe correctly caught**, which means
  the token has AT LEAST TWO DISTINCT PRODUCERS and the per-raise warp attribution this
  entry asks for must separate them rather than bucket them together. Their own home is
  TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS above; they
  are recorded here because they constrain THIS entry's hypothesis, not because they
  belong to it.
  CONSEQUENCE ALREADY TAKEN, and it is a tolerance and not a fix: V24W now lists
  `icon-teleport` BARE in `allowedAnomalies` citing `2026-08-25_1502`. The
  ceiling that measurement authorizes is 130 (2x the measured 65, DOUBLED rather
  than tightly bracketed precisely because this probe defect is open and that
  lane commands rails), and it cannot be declared while
  `test_no_committed_spec_arms_a_count_budget` and
  `test_every_committed_spec_parses_under_the_budget_surface` hold the budget
  mechanism inert across every committed spec - the same blocker V14T and V15T
  each hit and each backed out of. So an over-sensitive threshold currently costs
  the suite an UNBOUNDED tolerance on a third lane rather than a bounded one,
  which raises the value of fixing the probe rather than lowering it.
- **RESEED-LAG-DARK-GAP-AT-CLOCK-JUMP: the ghost proto orbit line goes dark for
  ~3 frames when a discontinuous clock step overruns the applied segment's end
  bound, because the reseed lags the clock** [OPENED 2026-08-26 off V25M reading
  2 (`harness/results/2026-08-26_1817_V25M-duna-park-player-loop.json`), the
  lane's single `line-blink` raise. GENUINE MINOR RENDER TRANSIENT - the
  instrument is NOT the suspect here. Owner: whoever owns the map-render segment
  reseed]. Mechanism, read straight off the collected trace (pid=2657480491,
  recId=aa48920e, frames 7784-7787): the third arrival TimeJump landed
  `currentUT` 43 s past the applied segment's end bound
  (`bounds=[...,5360144042.2]` vs `currentUT=5360144085.0`), the line went OFF
  with `reason=stale-segment-awaiting-reseed`, and 3 frames later the reseed
  landed (`bounds=[...,5360144182.1]`) and the line relit via
  `director-stockconic-visible`. Everything downstream behaved per contract:
  `stale-segment-awaiting-reseed` stamps NO `RenderWindowCoverage` BY DESIGN
  (its "outside bounds" is the applied bounds lagging INSIDE the window - see
  the `MapRenderTrace` entry in `.claude/CLAUDE.md`), so the
  `windowTransitionExempt` half-proof cannot apply (`priorToggleVerdict=Other`)
  and the `line-blink` raise is the instrument correctly reporting a real
  ~100 ms dark flicker at the jump. TWO FACTS THAT BOUND THE DEFECT: (1) it is
  TIMING-DEPENDENT, not deterministic - reading 1 (`2026-08-26_1744`) drove the
  IDENTICAL jump triple and raised zero, so whether the reseed lands in the
  same frame as the jump or a few frames later decides the raise; (2) TimeJump
  is not required - a high-warp frame can also advance the clock past a stale
  segment's end bound in free play (one frame at 100,000x is thousands of
  seconds), so the same dark gap is reachable by warp overrun at any segment
  boundary. Possible fix directions (NOT taken - file-only entry): hold the
  previous line state through the `stale-segment-awaiting-reseed` frames
  instead of darkening (the segment is known to be lagging, not ended), or
  reseed synchronously when the overrun is detected on the same frame.
  CONSEQUENCE ALREADY TAKEN, tolerance not fix: V25M lists `line-blink` bare in
  `allowedAnomalies` citing `2026-08-26_1817`, ceiling 2 (2x measured) in
  prose, pending the budget-mechanism allowlist move.**
- **V15M-LINEBLINK-IS-TRACEDPATH-HANDOFF-CADENCE: V15M's single `line-blink`
  raise is the Director's DESIGNED StockConic->TracedPath descent handoff at
  the Gilly loop tail, flagged only when frame cadence lands the toggle pair
  inside the 8-frame blink window - pre-existing, deterministic in UT, and NOT
  from PR #1556 or the map-render wave** [ATTRIBUTED 2026-08-28 by comparing
  the #1556 confirmation flight `2026-08-28_1703` (branch `watch-mode-fixes`
  fc49e71d5; collected log `logs/2026-08-28_2004_V15M-gilly-player-loop`)
  against the lane's arming flight `2026-08-19_1810` (branch `gilly-loop-lane`
  9301b945d; collected log `logs/2026-08-19_2111_V15M-gilly-player-loop`;
  result JSON in `Parsek-gilly-loop-lane/harness/results`). Owner: whoever
  owns the `MapRenderTrace` blink exemptions. **REMEDY (a) SHIPPED 2026-08-28**
  - see FIX DIRECTIONS at the end of this entry for what landed].
  THE TWO RUNS RAISE THE IDENTICAL EVENT, NINE DAYS AND TWO CODEBASES APART:
  same recId `77f724bb`, same `currentUT=16656457.000`, same
  `intentReason=director-traced-path-suppress`, same missing-exemption
  fingerprint (`offWindowCovered=False polylinePainted=False
  polylineOwns=False windowTransitionExempt=False bodyChanged=False
  priorToggleVerdict=InsideWindowOn toggleVerdict=Other`), `sinceFrames` 5
  (2026-08-28) vs 8 (2026-08-19), both <= `LineBlinkFrameWindow=8`. The
  2026-08-19 run predates the ENTIRE map-render wave (#1526-#1551; the M-A7
  manifest landed 2026-08-25) and ran the dead-watch-camera code (it is the
  443-NRE-storm flight), yet raised the same blink at the same UT. That kills
  BOTH candidates PR #1556's todo note deliberately left open: "main moved"
  (the raise existed before main moved) and "watch working changes what the
  map sees" (the raise fires with the watch camera dead and alive alike; the
  same-millisecond `Watch focus dist=786m` coincidence in `_1703` is script
  ordering, not causation). **PR #1556 is EXONERATED, and so is the
  map-render wave.** When #1556 merges, fold its unattributed note into this
  entry.
  MECHANISM (read off both traces, and replicated by BOTH ghost incarnations
  within EACH run): after the loop-cycle rollover the proto re-resolves onto
  the recording's tiny terminal Gilly orbital window (bodyFrame
  [16656187.2, 16656357.5], ~170 s): line ON `director-stockconic-visible`
  (Inside stamp). A few ~130 s warp frames later the drive clock crosses the
  last recorded orbit segment's end into the TracedPath descent leg;
  `ShadowRenderDriver.IsTracedPathOwnedThisFrame` flips true and the
  `GhostOrbitLinePatch` Postfix kills the line with
  `director-traced-path-suppress` (which stamps NO `RenderWindowCoverage` and
  `hasBounds=false` BY DESIGN - it is not one of the four stamping sites).
  The proto never relights: it retires ~25 frames later (`left-orbit-segments`,
  "Orbit proto retired AT terminal orbit bound"). A designed, permanent
  handoff - not a flicker.
  WHY NO EXEMPTION CAN MATCH: `bodyChanged` false (Gilly->Gilly);
  `windowTransitionExempt` needs an Outside stamp the suppress site never
  writes; `polylinePainted`/`polylineOwns` false because ownership publishes
  ONLY on an ACTUAL polyline draw and the Driver never drew this run - V15M is
  a map-closed flight-scene lane (renderCompose:
  `ownership-publish-surface-never-ran`). So the exempting fact the closed
  V1-REPLAY-LINE-BLINK diagnosis established for handoffs (the
  paint/ownership bit) is structurally unavailable exactly when the map is
  closed - i.e. exactly when NO line is visible to any viewer and a "blink"
  has no observer.
  CADENCE, NOT CODE, DECIDES THE RAISE: in BOTH runs the FIRST incarnation
  crossed the identical handoff one loop earlier and was NOT flagged
  (`sinceFrames=10 > 8`: frames 6834->6844 in `_1703`, 7108->7118 in
  `_1810`); the second incarnation landed at 5 and 8. Whether ~130 s/frame
  stepping crosses the ~170 s terminal window in <= 8 frames is frame-rate
  jitter. Corollary: **V15M has never had a green armed run** - the arming
  flight itself red'd on this exact raise (its NRE storm took the attention),
  so the 2026-08-28 red reproduces the lane's standing state; it is not a
  regression.
  NOT the creation-frame render family: that family (V20 artifacts,
  TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS)
  is overlap protos
  SETTLING onto segment zero at creation because jump order sets creation
  frames. Here incarnation 2 was created correctly onto the loop-shifted
  visible segment (segIdx 2-3, `from visible-segment`) and behaved per
  contract until the designed handoff; TimeJump's only role is cadence.
  FIX DIRECTIONS: (a) the principled exemption
  EXISTS as a positive fact stamped at a site whose branch condition IS the
  measurement: the OFF edge's branch condition is
  `IsTracedPathOwnedThisFrame(pid, frame)` - "the Director's spine says a
  non-orbital leg owns this pid this frame" is a measured transition fact,
  not a widened window. Exempt an OFF whose intent is
  `director-traced-path-suppress` with that selector true and a prior Inside
  ON. CAVEAT to design around: bare, it would also exempt a map-OPEN handoff
  where the polyline then FAILS to draw (a real dark gap) - so conjoin the
  ownership/paint bit whenever the publish surface ran, and accept the
  selector alone only when it never ran (the case with no visible line at
  all). (b) V25M-style tolerance: list `line-blink` bare in V15M
  `allowedAnomalies` citing `2026-08-19_1810` + `2026-08-28_1703` - same
  unbounded-tolerance cost as the V24W/V25M precedents while the budget
  mechanism stays inert. Preference: (a) - unlike RESEED-LAG above (a real
  transient) this raise is a measurement artifact of a designed handoff.
  **SHIPPED 2026-08-28: (a), with the caveat designed around.** The stamp is a
  new two-value enum `MapRenderTrace.LineHandoffKind` (`None` /
  `TracedPathOwned` - an enum, not a bool, so every write must be SPELLED and is
  therefore countable by a source gate) written at EXACTLY ONE site - the
  `director-traced-path-suppress` branch, whose own condition IS
  `IsTracedPathOwnedThisFrame` - riding the SAME single-writer intent channel as
  `RenderWindowCoverage` (`RecordLineIntent`). It deliberately does NOT stamp
  coverage: that site hides the line because the spine handed the leg away, not
  because a clock left a window, and the two exemptions stay disjoint (the
  coverage cell's 2/2 counts would red if the suppress site became a fifth
  coverage stamp). The pure predicate is
  `MapRenderTrace.ResolveTracedPathHandoffExempt`, fail-closed on SIX
  conjuncts: definitively DARK edge; fresh intent AGREEING with the truth read;
  handoff `TracedPathOwned`; prior toggle a proven `InsideWindowOn` (the same
  both-halves discipline `ResolveWindowTransitionExempt` enforces); and TWO
  anti-masking conjuncts. (5) When the ownership/paint publish surface RAN this
  frame, the polyline must ALSO actually have covered the ghost
  (`polylinePainted || polylineOwns`), so a map-OPEN handoff that claims the leg
  and never draws still raises. (6) THE SELECTOR-ALONE LANE IS ITSELF A POSITIVE
  FACT - it requires a positively measured CLOSED map (`mapWasOpen` false), never
  the absence of a publish. `publishSurfaceRan == false` is a NEGATIVE fact and
  strictly broader than map-closed: the Driver walk also misses its epilogue on
  the TRACKSTATION / FLIGHT controller-not-yet-awake defers (both AFTER the
  `MapView.MapIsEnabled` gate), on any exception escaping the walk body, and when
  no Driver exists - all reachable with the map OPEN and nothing drawn, i.e.
  exactly what the detector is for. The alone-lane's justification is
  `ownership-publish-surface-never-ran` meaning no line on screen for anyone to
  see blink, and only the closed map establishes that.
  "Did the publish surface run" reuses an EXISTING signal rather than a new
  per-frame flag: `GhostTrajectoryPolylineRenderer.DidOwnershipPublishRunOnFrame`
  reads the Driver's `pendingDrawsFrame` walk-completed stamp - written in the
  decide walk's epilogue, after every early return (scene gate,
  `MapView.MapIsEnabled`, controller defers), and already asked the identical
  question one slot later by `OnMapCameraPreCull`. Ordering, stated exactly
  because it is easy to invert: that stamp is written ~50 lines BEFORE
  `NoteOwnershipPublish`, not after it, and what makes the reuse sound is that
  the probe's actual inputs (the ownership + S0 paint sets) are populated during
  the per-recording walk, ahead of the stamp; the recorder publish below it is
  the manifest's own diff, which the probe never reads. Both the raise and the
  `line-blink-suppressed` lines now carry `tracedPathHandoffExempt=` /
  `intentHandoff=` / `publishSurfaceRan=` / `mapWasOpen=`, so an exempted pair
  stays visible rather than going silent - and the last two together separate
  "coverage proof missing" from "walk never reached its epilogue while the map
  was open". Gates in `LineBlinkWindowExitExemptionTests`: the archived V15M
  fingerprint replayed at both cadences (exempt post-fix), the pre-fix-behavior
  proof driven through the full replay with `intentHandoff: None` (still raises
  at the archived geometry - the hardcoded three-guard helper beside it is
  detector CHARACTERIZATION, not a fails-before proof), the first incarnation's
  `sinceFrames=10` unraised before AND after with the post-fix half deliberately
  given NON-exempting inputs so it pins the cadence arithmetic rather than
  short-circuiting at the exemption, the map-OPEN never-draws masking pin, the
  map-OPEN-but-walk-deferred pin (conjunct 6), the fail-closed conjuncts, a
  one-spelling source gate on the handoff stamp, and a pin that the
  publish-surface signal keeps reusing the walk-completed stamp AND that the
  stamp still precedes the publish. **V15M's armed `anomalySweep` is expected GREEN on the
  next nightly** - this raise was its only standing red (the lane has never had
  a green armed run; its arming flight red'd on this same event), so that sweep
  IS the regression catcher for this change.
- **GHOST-MAP-TEARDOWN-NRE-WHEN-CAMERA-TARGETED: destroying a ghost map vessel
  that is the `PlanetariumCamera`'s current target NREs stock's KnowledgeBase
  during the forced retarget** [OPENED 2026-08-26 off V25M reading 3
  (`harness/results/2026-08-26_1823_V25M-duna-park-player-loop.json`,
  `unityExceptions` report-only row: 2 NRE lines, both this one event's ERR+EXC
  pair). Owner: `GhostMapPresence`]. Stack has NO Parsek frames but the trigger
  is ours: `RemoveAllGhostVessels reason=scene-cleanup` at 21:23:49.942 destroys
  `Ghost: Kerbal X` while it is the camera target; stock then walks
  `Vessel:OnDestroy -> PlanetariumCamera:OnVesselDestroy -> SetTarget ->
  KnowledgeBase.OnMapFocusChange -> KbApp_PlanetParameters.ActivateApp`, which
  NREs on the dying MapObject's transform. INTERMITTENT BY CONSTRUCTION -
  readings 1 and 2 of the same lane (`2026-08-26_1744` / `_1817`) logged ZERO
  NREs, because it only fires when the camera happens to be targeting a ghost at
  teardown. The same collect also shows the adjacent, already-warn-logged
  `Die() threw for 'Ghost: Kerbal X Probe'` in the same sweep - two symptoms of
  the same "destroy while stock still references it" moment. Candidate fix (NOT
  taken - file-only): in `RemoveAllGhostVessels` (and the single-ghost removal
  paths), if `PlanetariumCamera.fetch?.target` resolves to the ghost being
  destroyed, retarget the camera (active vessel / home body) BEFORE `Die()`.
  Benign in effect today (scene is being torn down anyway; stock swallows the
  exception), but it dirties every armed lane's `unityExceptions` census and
  would mask a real NRE regression behind an expected one.**
- **`maxUtStep` on a DWELL is POLLUTED by a seam `TimeJump` epoch shift that
  lands inside an open dwell, so every tolerance derived from it balloons on
  jump-driven lanes** [OPENED 2026-08-25 off V24W reading flight 2
  (`2026-08-25_1502`). REPORT-ONLY: nothing red on it, no code changed, and no
  rule reads it as a gate today. Owner: `RenderCompositionRecorder` /
  `rendercompose`]. `DWELL.maxUtStep` is the largest single head-UT step observed
  while the dwell was open, and it exists so a rule can size a tolerance against
  how coarsely THAT dwell was sampled (the RC-HOLD leg-2 attribution window and
  RC-COVER's gap resolution both derive from it). It assumes the step measures
  SAMPLING RATE. A seam `TimeJump` breaks that assumption: the epoch shift is a
  discontinuity in the clock, not a coarse sample, and if it lands while a dwell
  is open it becomes that dwell's `maxUtStep`. MEASURED on flight 2's manifest
  (`harness/results/2026-08-25_1502_V24W-duna-one-warp-stair_shots/parsek-render-manifest.txt`),
  which has four `DWELL` records and a clean split:
    * `61e9177193444e329247d0e8288cf91e` seg 6 (heliocentric-transfer, closed),
      loop UT 64044033.92 -> 70898645.36: `maxUtStep = 6846984.9646892548`.
    * `6561c8eb97dd48d6825e9d6c7c04d22a` seg 1 (departure-loiter, still open at
      export), loop UT 64043475.99 -> 70931658.54: the SAME
      `maxUtStep = 6846984.9646892548`.
    * the other two, which no jump crossed: `maxUtStep = 2` and
      `maxUtStep = 53745.585304260254`.
  Two dwells reporting the identical 6,846,984.96 s to the digit is the tell -
  that is one clock event stamped into both, not two independently sampled
  intervals. The lane's own `driver.midMissionSeamWrites` records
  `verbs ["MissionConfig", "TimeJump"]`, and the depart -> arrive leg's compressed
  offsets (80,672.8 -> 6,935,286.1) bracket the value, so the producer is
  identified rather than inferred. The result JSON's
  `quality.maxUtStepSeconds = 6846984.964689255` is the max over all four, i.e.
  the run-level facet is the polluted one. IMPROVEMENT, recorder side (preferred,
  because the recorder is the only layer that can see the seam verb): PARTITION a
  dwell at TimeJump epochs, or exclude an epoch-shift step from the `maxUtStep`
  accumulation - the seam verb is observable at the point the shift is applied,
  so this is a classification the recorder can make honestly rather than a
  heuristic. VERIFIER-SIDE ALTERNATIVE if the recorder cannot be touched: cap the
  contribution `maxUtStep` may make to a derived tolerance, so one epoch shift
  cannot silently widen an RC-HOLD or RC-COVER window by six orders of magnitude.
  WHY IT IS NOT URGENT: no armed window reads `maxUtStep` today, and V14M / V8
  are TimeJump-driven but their dwells are short enough that the question has not
  yet cost a reading. It becomes urgent the moment an RC-HOLD or RC-COVER clause
  is armed on a jump-driven lane, which V24W's own arming pass will be.

---

## LANDED-TERMINAL-LOOP-HAS-NO-MAP-PRESENCE-OUTSIDE-THE-FLIGHT-SCENE: a looped recording whose terminal is Landed renders ONLY as the flight-scene mesh during its replay window - the map/TS/KSC surfaces are deliberately empty [MEASURED 2026-08-24 by the V22/V23 round-2 reading runs, the first landed-terminal loop subjects. REPORT-ONLY: every mechanism below is a deliberate product gate, not a defect; filed because it decides which lenses a surface-endpoint lane can pin, and because it is the measured render answer for surface-base supply routes]

Three independent gates, each read from its own line on the round-2 logs:

- FLIGHT map protos: `[Policy] Skipped ghost map for #2 "Kerbal X" - terminal=Landed`
  (V22M) - the policy layer deliberately creates no map proto for a
  landed-terminal member. V23M's subject shows the same absence WITHOUT the
  Policy line (zero `Skipped ghost map` lines there), so the two subjects reach
  the same emptiness through different sites - unattributed, stated as read.
- TRACKING STATION: `CreateGhostVesselsFromCommittedRecordings: created=0 from 9
  recordings ... loopMemberHidden=3` (V22T) - AND THE IN-WINDOW QUESTION IS NOW
  ANSWERED: the round-3 landed-sliver epochs (member in-window and landed, both
  subjects, both parents) read the SAME `created=0 ... loopMemberHidden=3` with
  zero GhostCreated lines from the dynamic path, so a landed-terminal loop
  member NEVER gets a TS proto at any epoch. The `factory chain` line cannot
  print in the TS for this class either (no proto-driven unit assembles).
- KSC scene: `[KSCGhost] Mission-loop unit owner=0 in inter-cycle wait at
  loopUT=... - all members hidden` (V22K) - same window gating at the third
  host.

CONSEQUENCES. (1) The map-view polyline / TracedPath / orbit-line-decision
lenses are unreachable for this class in an unattended lane: no proto exists to
drive them, and additionally NO SEAM VERB OPENS MAP VIEW, so even the always-on
polyline Driver never draws (awake, zero draws, both round-2 logs). Filed as a
coverage-program instrument gap; the M-A7 render-composition manifest is the
design-space answer. (2) The faithful-parity and seam-endpoint censuses ride
map protos, so they print NOTHING on this class (zero summary lines in both
logs) - their presence pins are structurally vacuous here and were dropped.
(3) For supply routes to surface bases, the measured product behavior is: the
route's ghost is visible ONLY in the flight scene, only during the replay
window, and the window on a landed-terminal subject ends essentially AT
touchdown (1.7 s of landed time for V22's subject, 32 s for V23's) - the map
shows nothing between cycles. Whether that is the WANTED product behavior for
routes is a product question this entry deliberately does not answer.

---

## ~~KSC-SURFACE-RESOLVED-TWO-EMITTERS-SHARE-ONE-RATE-LIMIT-KEY: the KSC host logs `KSC SURFACE playback resolved` from two sites with DIFFERENT field sets under ONE rate-limit key, so the only variant carrying `body=` can be silently suppressed by the one that does not~~ [FOUND BY AUDIT 2026-08-21 while authoring `V22K-kerbin-splashdown-ksc-arrival`, the first lane ever to pin the KSC render host. OBSERVABILITY FINDING. FIXED 2026-09-02 in the cheap shape the entry named]

**Fix (2026-09-02).** The two emitters now carry their own rate-limit keys (`ksc-surface-point-<recId>` and `ksc-surface-segment-<recId>`), and the interpolation variant carries `body=` too (`before.bodyName`, which the upstream gate has already proven equals `after.bodyName` equals `Kerbin`). Both paths are therefore independently observable and a future lane can pin the frame directly. V22K's committed pin, `KSC SURFACE playback resolved: recording=.* branch=`, matches both variants before and after (the `.*` spans the new field), and its control inversion is untouched, so the lane's header remains accurate as a description of why the body-agnostic pin was chosen; it is no longer the ONLY satisfiable pin. Pinned by `KscGhostPlaybackTests.SurfaceResolvedLines_PointAndSegmentPaths_EmitIndependentlyAndBothCarryBody`, which drives the point path and the segment path for one recording inside one rate-limit window and asserts both lines land with `body=Kerbin`.

Original entry follows for the mechanism.

`ParsekKSC.Playback.cs` resolves a KSC surface pose on two paths and both log the
same prefix:

- `:340`, in `TryResolveKscPointPose` (a single exact recorded point):
  `KSC SURFACE playback resolved: recording={DebugName} ut={ut:F2} body={bodyName} branch={Branch}`
- `:438`, in `TryResolveKscSegmentPose` (interpolation between a before/after pair):
  `KSC SURFACE playback resolved: recording={DebugName} targetUT={targetUT:F2} branch={Branch}`

Both call `ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-surface-position-{rec.RecordingId}", ..., 2.0)`
- THE SAME KEY - so inside a 2.0 s window whichever fires first suppresses the other.
During ordinary playback the ghost is interpolating between samples, so the
dominant emitter is `:438`, the variant with NO `body=` field. A reader grepping
for `body=` therefore sees an intermittent line whose absence means nothing.

WHY IT DID NOT COST A PIN: the `body != "Kerbin"` gate is UPSTREAM of both emits
(`:288-296` for the point path, `:362-372` for the segment path), each returning
false before its success line. So REACHING EITHER LINE IS ALREADY THE
KERBIN-FRAME PROOF, and V22K pins the body-agnostic
`KSC SURFACE playback resolved: recording=.* branch=` plus a forbid on the two
skip lines. The field is redundant where present and absent where it would
matter, which is exactly why the obvious pin is the wrong one.

IF IT IS EVER FIXED, the cheap shape is to give the two sites distinct rate-limit
keys and add `body=` to the segment variant. That would make the two paths
independently observable and let a future lane pin the frame directly. Any such
change must be taken deliberately: V22K's pin is written against today's
behaviour and its header says so.

## ~~AUTOMERGE-ON-BY-DEFAULT: is any player flow reachable that now auto-commits GHOST-ONLY where the dialog used to ask?~~ - ANSWERED, THEN FIXED; the entry stays live ONLY for four named residuals [RAISED 2026-08-24 by the review panel on the default-flip PR (#1523) as PLAUSIBLE-not-confirmed. OPEN QUESTION, no defect demonstrated. The behaviour itself is by design and predates the flip; what changed is that it is now the DEFAULT answer. **RESTATED 2026-08-28 (branch `ledger-followups`) for the settings-simplification clamp: "on by default" is now "on UNCONDITIONALLY". ADOPTED RESOLUTION: leave the question PINNED, unclosed, awaiting the decisive evidence named below**. **FLOWN 2026-08-29 (branch `automerge-coverage`): the ghost-only commit is DEMONSTRATED LIVE and GREEN-GATED on BOTH the cold and the warm route (`2026-08-29_1025_S0.9` and `2026-08-29_1043_S0.10`, both PASS), with every `VesselSnapshot` destroyed and no dialog, on the warm side through an entrance that requires NO fault at all. The question this entry asks is ANSWERED for reachability; what remains is FREQUENCY. STILL OPEN, because the remedy is a maintainer decision and is deliberately NOT in that branch. **FIXED AND CONFIRMED 2026-08-29 (branch `limbo-fidelity`). A non-re-fly Limbo / LimboVesselSwitch tree now commits at FULL FIDELITY through the dialog's own `MergeDialog.MergeCommit` instead of the snapshot-destroying ghost-only branch. BOTH ROUTES RE-FLOWN GREEN against a hash-verified deploy of the fix's own build — `2026-08-29_1200_S0.9` (cold) and `2026-08-29_1202_S0.10` (warm), both PASS attempt 1, every predicted number exact and the ghost-only token absent from both logs. Everything above and below this bracket is the escalation history the fix was taken against and is left INTACT — see "THE FIX (2026-08-29)" at the end of the entry**. **HEADER STRUCK 2026-09-01 to match the body**, which the 2026-08-29 hygiene trim left unreconciled: the question this header asks was answered by the `automerge-coverage` flights and then REMOVED by PR #1580, so a reader stopping at the header was being told an OPEN QUESTION stood where a fix had shipped and been confirmed. The entry is KEPT LIVE, at full length, for the four residuals under "What is still open, unchanged by the fix" at the very end — (a) FREQUENCY of the resume miss, (b) step 1 still injected rather than produced by either spec, (c) ENTRANCE A still undriven, (d) the dangling limbo dispatch — none of which reopens the ghost-only question. The escalation history below is also archived verbatim at `docs/dev/done/todo-and-known-bugs-v8.md` and at commit `7ab9ae491`]

**The 2026-08-27 settings simplification (#1549) widened this question and invalidated
half of #1523's scoping argument.** `autoMerge` is now a HIDDEN field with no player-facing
UI, and `ParsekSettings.ClampHiddenSettingsToShippingValues`
(`Source/Parsek/ParsekSettings.cs:282`, called from `ParsekScenario.OnLoad` ~:2913) forces
it back to `true` on EVERY load unless an automation env hook is armed
(`ParsekSettings.AutomationEnvPresent`) — verified in source, not assumed. Two consequences,
both load-bearing:

- **"On by default" is now "on unconditionally" for players.** There is no longer any way
  for a player to be on the OFF path: no toggle to turn it off, and a stored `False` is
  overwritten on the next load. Whatever flow this entry is asking about, if it exists,
  every player is on it.
- **#1523's "the flip reaches NEW saves only" paragraph is INVALIDATED** and is annotated
  as such at its own entry. That paragraph's mechanism was correct — KSP's
  `autoPersistance` writes every Parsek setting into every save, so an existing career
  carries `autoMerge = False` and `Load` only overlays present keys — but the clamp now
  runs AFTER that overlay and overrides the stored value. Existing careers reach the ON
  path too, from their next load onward. The harness is unaffected: the clamp stands down
  under an armed automation env hook, so fixture pins and `SetSetting autoMerge=` keep
  authority exactly as that paragraph describes.

**Adopted resolution: leave it pinned.** Still no reachable player flow constructed, and
the widening does not by itself produce one — it changes the population on the path, not
whether the path exists. Closing it on reasoning would be closing it on the same reasoning
that has failed to settle it twice.

**Decisive evidence (what would actually close it).** ~~Neither exists today.~~ **BOTH
NOW EXIST, BUILT 2026-08-29 (branch `automerge-coverage`, R4). NEITHER HAS FLOWN —
flights are separately authorized. Status: INSTRUMENTED, AWAITING THE DRIVEN RUN.**
1. ~~The plan-§7 autoMerge-ON FLIGHT in-game cell — the live exercise of the ON path that
   #1523 recorded as a coverage gap and that still does not exist.~~ **BUILT:**
   `RuntimeTests.ExitToSpaceCenter_AutoMergeOn_CommitsSilentlyAtFullFidelity`, the new
   `AutoMergeCommit` category (batch-disabled + restore-backed, mirroring the two
   `SceneExitMerge` cells that force autoMerge OFF — those two were the whole of the ON
   path's coverage, i.e. none). It records an ORBITING host, drives the same stock
   save-and-exit-to-SpaceCenter path, and asserts no `ParsekMerge` popup, a committed leaf
   whose `VesselSnapshot` SURVIVED and still passes `MergeDialog.CanPersistVessel`, the
   `Silent full-fidelity auto-commit` line present AND `Ghost-only auto-commit` absent.
   It skips naming ORBITING when it cannot self-set-up: a pad host is not a weaker
   fixture but the wrong subject (staging leaves it Ascending, which
   `CommitTreeSceneExit` nulls the snapshot for BY DESIGN, and a never-moved tree is
   idle-on-pad-discarded before the commit branch).
2. ~~A driven harness scenario that COLD-LOADS a fixture save carrying a pending tree and
   lands OUTSIDE FLIGHT...~~ **BUILT:** `harness/scenarios/S0.9-automerge-pending-limbo-cold-load.toml`
   (operator tier, reading run, never flown) over the new `pending-limbo-tree` injection
   preset (`Source/Parsek.Tests/Generators/PendingLimboTreeFixture.cs` +
   `InjectPendingLimboTree`, four-surface-synced). Vessel-less `fresh-sandbox` routes the
   load through `DecideLoadRoute`'s NoVesselSpaceCenter branch, so `LoadedScene != FLIGHT`
   holds by construction. It pins ONLY the structural preconditions and leaves the two
   branch-discriminating tokens as documented readings — pinning an answer to the question
   the spec asks would make a green run mean nothing. Nothing armed; `ARMED_ALLOWLIST`
   untouched. **Its WARM sibling `S0.10-automerge-limbo-warm-exit.toml` was authored
   alongside it once the warm chain below was assembled — see "THE TWO DRIVEN SCENARIOS".**

**FLIGHT VERDICTS (2026-08-29) — THE GHOST-ONLY BRANCH IS NOT A THEORY. It ran, on both
routes, and destroyed the snapshots.** Both scenarios flew against a provisioned instance
carrying this branch's DLL (hash-identical to the building worktree's `bin/Debug`).

- **S0.9 (cold) — `2026-08-29_1025_S0.9-automerge-pending-limbo-cold-load`, PASS attempt 1,
  58 s, every verifier PASS/SKIPPED, expectations mismatches=0, zero `[Parsek][ERROR]`.**
  Open item (b) is CLOSED: the load-time guards ALL passed the tree. The one guard that
  could plausibly have dropped it stood down for exactly the source-predicted reason —
  `Idle-on-pad auto-discard skipped: pending tree state is Limbo (not Finalized)` — and the
  site then reported
  `autocommit-outside-flight (cold-load outside-flight): entry ... autoMerge=True
  pendingState=Limbo reFlyActive=False`
  followed by
  `Ghost-only auto-commit (cold-load outside-flight, reason=state=Limbo): tree='Limbo Stack'
  recordings=2 snapshotsNulled=2`.
  **`autoMerge=True` was NOT pinned by that spec** — `fresh-sandbox` carries no `autoMerge`
  key, so it is the shipping default reached through the field initializer. The ghost-only
  commit is what a default install does.

- **S0.10 (warm) — LIVE-PROVEN by `2026-08-29_1043_S0.10-automerge-limbo-warm-exit`, PASS
  attempt 1, 54 s, every verifier PASS/SKIPPED, expectations mismatches=0, zero
  `[Parsek][ERROR]`, clobber guard silent.** All ten required tokens matched and the warm
  chain is REPRODUCIBLE, ending in
  `Ghost-only auto-commit (scene-exit, reason=state=Limbo): tree='Limbo Stack' recordings=2
  snapshotsNulled=2`.
  Flight 1 (`2026-08-29_1027`) was PARSEK-FAIL attempt 1, 55 s, mismatches=1 — **the spec's
  own token, not the product** — and it earned its keep by discovering which entrance the
  spec actually drives (below). Six of seven matched even then, and the warm chain walked. The
  `exittospacecenter start scene=FLIGHT autoMerge=true hasActiveTree=false
  hasPendingTree=true ... activeTreeVariant=None pendingTreeVariant=None` line carries three
  of the chain's assertions at once: the restore did not install, the Limbo tree survived,
  and the wedge guard found no dialog required. The dangling dispatch was observed too
  (`OnLoad: pending-Limbo tree 'Limbo Stack' deferred to OnFlightReady for quickload-resume`,
  at SPACECENTER, where that restore can never fire).

**THE TWO ENTRANCES, and why flight 1's red made the finding STRONGER.** Step 2 of the chain
has two of them, and they are not equally demanding:

- **ENTRANCE A — the give-up.** The 3 s match loop runs out and
  `RestoreActiveTreeFromPending` warns + `yield break`s (`ParsekFlight.cs:14104-14141`).
- **ENTRANCE B — no fault required.** The player leaves FLIGHT *while the 3 s wait is still
  running*. `FinalizeTreeOnSceneChangeCore`'s `restoringActiveTree` early return
  (`ParsekFlight.cs:3029-3035`) correctly declines to finalize — the coroutine owns
  `activeTree` — and the coroutine is then destroyed with the scene.

S0.10 originally pinned entrance A. The guid gate rejected the active vessel AND the parent walk on the
coroutine's FIRST iteration (`liveGuid=88a8a8a6… conclusively differs from …
recordedGuid=ecc6bdc0…`, 13:27:51.448), so the match was already impossible — but the Warn
only fires once the full 3 s ELAPSES, and `ExitToSpaceCenter` arrived 0.38 s in
(13:27:51.830). The coroutine was then killed by the scene change
(`OnDestroy: clearing stale restoringActiveTree guard (coroutine aborted by destroy)`,
13:27:52.030). **So the run drove ENTRANCE B rather than the entrance its token named** —
and entrance B is the more alarming of the two, because it needs no match failure at all:
only a player who leaves FLIGHT within three seconds of a quickload.

**Resolution (2026-08-29, maintainer adopted option (a)):** token 3 was re-pointed at what
the spec deterministically drives — the iteration-1 guid-rejection pair, the `#293`
coroutine-in-progress skip, and `coroutine aborted by destroy` — the spec's subject was
relabelled to entrance B, and the re-fly `2026-08-29_1043` confirmed it green with
`not active within 3s` verifiably ABSENT. Reachability therefore rests on two independent
entrances, and **the one now under a standing gated instrument is the one that requires no
fault**.

**SEAM VERB GAP (recorded here rather than as its own entry, because it blocks one pin and
not a product question): ENTRANCE A REMAINS UNDRIVEN.** Driving the give-up needs the exit
to arrive more than 3 s after FLIGHT entry, i.e. a dwell/wait verb — and the M-C1 seam
roster has none (`TimeJump` is an epoch shift with frozen relative positions, not a sleep;
`EvaExit`'s `settleSeconds` is the only spec-authorable dwell and is EVA-only). Both
entrances end at the same ghost-only commit, so nothing about the finding depends on
closing this; a future S0.10 run that flips to the give-up Warn is entrance A finally being
driven and should be REPORTED, never widened into an alternation — the alternation would
stop distinguishing the two, and which one the spec drives is its subject.

**WHAT IS NOW PROVEN, and what is not.** Proven: a non-`Finalized` pending tree reaches
`AutoCommitPendingTreeOutsideFlight` outside FLIGHT on both the cold and warm routes, under
the shipping `autoMerge` default, and every `VesselSnapshot` is nulled with no dialog —
steps 2/3/4 of the player chain, in the shipped DLL. NOT proven: step 1 (a real quickload
producing the Limbo stash — both runs inject it) and **how often the resume match misses on
ordinary flights**, which is the last open question and the only thing between this and a
routine player experience.

**THE PRODUCT FIX IS NOT IN THAT BRANCH, ON PURPOSE.** The remedy is scoped, not blanket —
the dialog returns for the NON-re-fly, NON-`Finalized` case specifically, or Limbo trees
preserve fidelity instead — and choosing between those against
`silent-full-fidelity-autocommit.md` §4.4 / §10 is a design decision that now has live
evidence to be taken against, rather than a change to smuggle in beside the instrument that
found it.

**HEADLESS VERDICT (2026-08-29) — the "saved trees restore as Finalized" argument is
CORRECT FOR ONE MARKER AND WRONG FOR THE OTHER, so the ghost-only branch is NOT dead
code.** Pinned by five cells in `Source/Parsek.Tests/AutoMergeGhostOnlyReachabilityTests.cs`:

- **`isPending` — re-finalized, as the entry guessed.** `TryRestorePendingTreeNode` →
  `RecordingStore.RestorePendingTreeFromSave`, which HARD-SETS
  `pendingTreeState = Finalized` (`RecordingStore.cs:2297`; the sibling
  `PromoteSavedPendingTreeAfterActiveRestore` does the same at `:2357`). A tree that
  round-trips through this marker can only ever take the full-fidelity branch.
- **`isActive` — NOT re-finalized, and this is the half the argument missed.**
  `TryRestoreActiveTreeNode` stashes `PendingTreeState.Limbo` when the node carries an
  `ActiveRecordingId` and `LimboVesselSwitch` when it does not (`ParsekScenario.cs:5737-5740`,
  the `stashState` ternary).
  The marker is not exotic: `SavePendingTreeIfAny`'s Limbo branch WRITES it
  (`ParsekScenario.cs:1843`, the `markerKey` ternary), and — unlike `SaveActiveTreeIfAny`,
  which opens with `if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;`
  (`ParsekScenario.cs:2155`) —
  that branch carries **no scene guard at all**. So a non-`Finalized` pending tree exists
  on disk, restores non-`Finalized`, and does so at a non-FLIGHT scene.
- **With that state the predicate routes to ghost-only**, and the cost is measured:
  `AutoCommitTreeGhostOnly` now returns and logs how many `VesselSnapshot`s it destroyed.

**WHAT HEADLESS COULD NOT SETTLE.** The chain COMPOSES; that is not the same as the
shipped product WALKING it, and it says nothing about whether a PLAYER produces the state.
Both gaps are now answered below — the second by the assembled warm chain, the first by
the two driven scenarios — and the open residue is stated there.

**THE WARM PLAYER CHAIN — ASSEMBLED FROM SOURCE 2026-08-29 (independent review of branch
`automerge-coverage`), verdict REACHABLE BY PLAYER ACTION. Every step is a cited line, not
an inference; what is still open is stated at the end, and it is a FREQUENCY question, not
a reachability one.** Line numbers are as of that review's tree (post-`origin/main`
7a2f27c01); each step also names its symbol so a drifted line is recoverable.

1. **The player quickloads mid-recording (F5 → F9).** FLIGHT → FLIGHT, so
   `ParsekFlight.FinalizeTreeOnSceneChangeCore`'s `scene == GameScenes.FLIGHT` arm runs
   and — when the vessel-switch pre-transition does not apply —
   `StashActiveTreeAsPendingLimbo(commitUT)` (`ParsekFlight.cs:3094`) →
   `RecordingStore.StashPendingTree(activeTree, PendingTreeState.Limbo)`
   (`ParsekFlight.cs:14491`). The tree is now parked NON-`Finalized`, by design, awaiting
   the resume.
2. **The resume's 3-second vessel-match loop misses.** `RestoreActiveTreeFromPending`
   gives up and `yield break`s (`ParsekFlight.cs:14141`) after a Warn whose own
   parenthetical is the product telling the user what to do next
   (`ParsekFlight.cs:14104-14106`): *"not active within 3s — leaving tree in Limbo (user
   can trigger merge dialog via scene exit)"*.
3. **The player follows that advice and exits to the Space Center.** But the restore never
   installed anything, so `activeTree` is null and `OnSceneChangeRequested`'s
   `if (activeTree != null) FinalizeTreeOnSceneChange(scene)` (`ParsekFlight.cs:2311`) is
   skipped entirely. Nothing re-finalizes the parked tree; it crosses the scene boundary
   still `Limbo`.
4. **SPACECENTER `OnLoad`.** The hidden-settings clamp has already forced `IsAutoMerge`
   true (`ParsekSettings.ClampHiddenSettingsToShippingValues`), so
   `ShouldSilentFullFidelityCommit` returns false on `Limbo` and
   `AutoCommitPendingTreeOutsideFlight("scene-exit")` (`ParsekScenario.cs:3889`) takes the
   ghost-only branch: every `VesselSnapshot` nulled, no dialog.

**The product's documented recovery path IS the silent-destruction path.** Step 2 promises
the dialog and step 4 is where the flip took it away.

**A second, non-fault entrance to the same state:** leaving FLIGHT while the 3-second wait
is still running. `FinalizeTreeOnSceneChangeCore`'s `restoringActiveTree` guard
(`ParsekFlight.cs:3029-3035`) returns early — correctly, the coroutine owns
`activeTree`/`recorder` — which lands on the same parked-`Limbo`-crosses-the-boundary
state without any match failure at all.

**Supporting observation, and a defect-adjacent smell in its own right: the limbo dispatch
schedules a restore that can never fire.** `OnLoad`'s `limbo-dispatch` block
(`ParsekScenario.cs:3803`, through the two `ScheduleActiveTreeRestoreOnFlightReady`
assignments at `:3818` / `:3849`) has **no scene gate**: outside FLIGHT it arms a
`onFlightReady` restore that will never run, and then the `pending-outside-flight` block
sixty lines later (`ParsekScenario.cs:3863` → `:3889`) consumes the very tree it just
scheduled. Worth a look independently of this entry.

**WHAT IS STILL OPEN — two things, and neither is "is it reachable".**
(a) **How often step 2 fires on ordinary flights.** The 3-second match is by vessel name
    and pid; the give-up Warn is the discriminator, and no run has been read for it. If it
    is common, this is a routine player experience; if it is rare, it is an edge.
(b) **Whether the load-time guards pass the tree** — schema gate, sidecar hydration +
    `DropFailedSidecarHydrationRecordings`, the mission-prune carve-out, the idle-on-pad
    discard.

**THE TWO DRIVEN SCENARIOS, AND WHY IT TAKES TWO.** S0.9 cold-loads, so it confirms only
the LOAD-PATH half — open item (b) — and reaches the site with
`context=cold-load outside-flight`. The warm chain above never cold-loads at all, so it
needs its own lane: **`harness/scenarios/S0.10-automerge-limbo-warm-exit.toml`** (operator
tier, reading run, never flown), which drives steps 2 → 3 → 4 in the shipped DLL and
reaches the site with `context=scene-exit`. That one word is the whole difference between
the two specs.

How S0.10 forces step 2 — **by fixture, not by timing race**, because a race is exactly
what a driven run cannot schedule. The `pending-limbo-tree` preset is injected into the
FOCUSABLE `eva2-lko-crewed` (so `DecideLoadRoute` takes the FLIGHT branch — the only route
there, since `scene=flight` is not an accepted `LoadGame` argument), and the tree's
`ActiveRecordingId` names a vessel, "Limbo Upper", that does not exist in that save. The
3-second match compares name, pid and `recordedVesselGuid`
(`ParsekFlight.cs:13975` / `:14047`); all three miss, on every run, on any hardware. Then
`ExitToSpaceCenter` proceeds — `ShouldShowPendingTreeDialogBeforeSceneChangeLive` computes
`hasFinalizedPendingTree` false for a Limbo tree (`SceneExitInterceptor.cs:238-244`), so
the wedge guard returns `None` — and the SPACECENTER `OnLoad` does the rest.

**Neither spec proves step 1**, and S0.10 does not measure open item (a) either: it
manufactures the miss rather than producing one. Frequency stays open.

**COLD (the same end state, reached from disk).** A save whose GAME `scene` is not FLIGHT
and whose ParsekScenario node carries the `isActive` marker — which
`SavePendingTreeIfAny`'s own comment already anticipates ("any OnSave that ran in the
resume window (autosave / scene-exit / exiting KSP before OnFlightReady fired)").

**Escalation trigger, restated now that the chain is named: a repro needs only a
quickload whose resume match misses.** Not a crash, not a mod interaction, not a
hand-edited save — F5, F9, a missed match, and the exit the product itself recommends. Any
run that shows the step-2 Warn followed by a `Ghost-only auto-commit ... reason=state=Limbo`
with `snapshotsNulled` > 0 IS that repro.

**Escalation trigger:** any repro of a player-recoverable tree silently committing
ghost-only. At that point the remedy is scoped, not blanket — the dialog returns for the
NON-re-fly, NON-`Finalized` case specifically. The re-fly ghost-only path stays silent on
purpose (`silent-full-fidelity-autocommit.md` §10) and the `Finalized` full-fidelity path
is already correct, so neither is in scope for the remedy.

`ParsekScenario.AutoCommitPendingTreeOutsideFlight` routes through the dialog's own
`MergeDialog.MergeCommit` + `BuildDefaultVesselDecisions` only when
`ShouldSilentFullFidelityCommit` qualifies. With a non-`Finalized` pending tree (Limbo
/ LimboVesselSwitch) or a live re-fly marker it falls to `AutoCommitTreeGhostOnly`,
which nulls **every** `VesselSnapshot`. Before the flip, that same state under the
shipped default showed a dialog offering full-fidelity Merge or Discard; now the
silent ghost-only commit is what a default install does.

The review could not construct a reachable player flow and neither can this entry, so
nothing is claimed. ~~a saved pending tree restores as `Finalized`
(`TryRestorePendingTreeNode`)~~ — **that clause is now CORRECTED, not merely annotated:
it is true of the `isPending` marker and FALSE of the `isActive` one, see the headless
verdict above; do not reuse it as a whole-question answer.** What survives unchanged: the
cold-start Limbo restore is written on the assumption that a cold start lands in FLIGHT,
and the auto-commit block is gated `LoadedScene != FLIGHT`. The path is also deliberate -
`silent-full-fidelity-autocommit.md` §4.4 keeps resume-flow stashes from being
heavier-committed, and §10 keeps re-fly ghost-only on purpose.

**What would close it:** ~~either a demonstration that no non-`Finalized` tree can reach
that site outside FLIGHT (in which case the branch is dead code worth saying so about),
or one that can~~ **— the first half of that disjunction is now REFUTED at the level of
save state (a non-`Finalized` tree does come off disk, at a non-FLIGHT scene, via the
`isActive` marker), so what remains is narrower: (a) S0.9's reading, i.e. whether the
shipped product actually walks that chain past the load-time guards, and (b) a
constructed player flow that parks a `Limbo` tree when an OnLoad lands outside FLIGHT.
(a) closes the "is the branch live" half; only (b) turns it into a defect,** in which
case the flip made it the default and it wants the dialog back. ~~Related: the in-game
coverage gap recorded on the default-flip entry below - the cell that would exercise the
ON path live does not exist~~ **— that cell now exists (`AutoMergeCommit`), so the ON
path's coverage gap is closed as an ASSET even though neither question yet has a driven
answer.**

---

**THE FIX (2026-08-29, branch `limbo-fidelity`) — FIDELITY IS PRESERVED, THE DIALOG DOES
NOT COME BACK.** The remedy the entry left as a maintainer decision was taken against the
two flight verdicts above, and it is the SECOND of the two options the escalation trigger
named ("the dialog returns for the NON-re-fly, NON-`Finalized` case specifically, **or
Limbo trees preserve fidelity instead**").

**The rule, stated once:** a commit that runs with no dialog must never silently DESTROY
vessel snapshots that exist. `ParsekScenario.ClassifyAutoCommitFidelity` replaces the
two-way `ShouldSilentFullFidelityCommit` gate with a three-way route, checked in an order
where each ghost-only justification is a stronger statement than the tree's own state:

| condition (in order) | route | ghost-only reason |
| --- | --- | --- |
| `!isAutoMerge` | `GhostOnly` | `not-automerge` |
| `reFlyActive` | `GhostOnly` | `re-fly-active` |
| `scene == MAINMENU` | `GhostOnly` | `mainmenu` |
| `pendingState == Finalized` | `SilentFullFidelity` | — |
| otherwise (Limbo / LimboVesselSwitch) | `LimboPreservingFullFidelity` | — |

The last row is the change; the three above it are the plan's own carve-outs, untouched.
**`ShouldSilentFullFidelityCommit` is DELETED**, not kept as a derived helper: it had no
caller left, and its `false` had become a trap — pre-fix it meant "the ghost-only branch
runs", post-fix it would mean only "not the FINALIZED route", which is equally true of the
new fidelity-preserving Limbo route. Every reference to it in this entry and in the two
spec headers is pre-fix HISTORY; the live question is the classifier's three-way answer.
Note also that `state=` was RETIRED from the ghost-only reason vocabulary: a tree state can
no longer send a commit to that branch, so a `reason=state=Limbo` line cannot be emitted
again.

**Why the same `MergeCommit` rather than a preserve-what-exists variant (the rejected
alternative).** The conservative shape — run the ghost-only pipeline minus the snapshot
nulling — was considered and rejected on four counts read out of the code, not preference:
(1) it forks a THIRD commit implementation against plan P2 ("reuse the dialog's machinery;
do not fork a second commit"); (2) it would retain snapshots nothing can ever use —
`ShouldSpawnAtRecordingEnd` rejects a mid-flight `sit=FLYING` capture at spawn time
regardless — so it buys no fidelity for them, only disk and memory; (3) dropping the null
pass drops `UnreserveCrewInSnapshot` with it, LEAKING a crew reservation on every
non-spawnable leaf, and keeping the unreserve while keeping the snapshot is worse still
(a snapshot whose crew has been released); (4) it does not preserve `GhostVisualSnapshot`
and does not fire `OnTreeCommitted`, both of which `MergeCommit` does. What made the
reuse SAFE for un-finalized shapes is that the decision machinery was built for them:
`ShouldSpawnAtRecordingEnd`'s snapshot-situation check exists for "cases where
TerminalState is null/Landed but the snapshot was captured mid-flight" (#114) — which is
exactly a Limbo stash, whose `StashActiveTreeAsPendingLimbo` sets no terminal state and
captures snapshots deliberately ("belt-and-braces … so the merge dialog can still offer
respawn"). The two commit cores are already the same call pair
(`CommitPendingTree` + `MarkTreeAsApplied`), so nothing about the tree's state is newly
exercised; only its snapshots' disposition changes.

**Why not the dialog.** Dialog-in-`OnLoad` is a known hazard class, and the whole point of
the `autoMerge` clamp is that commits are silent. Bringing a popup back on this path would
re-litigate the flip rather than fix the loss.

**New log token, deliberately not a reuse:**
`Limbo-preserving full-fidelity auto-commit ({context}, state={state}): tree='…'
recordings=N spawnable=M snapshotsPreserved=P snapshotsReleased=R`. `snapshotsPreserved`
is the mirror of the ghost-only branch's `snapshotsNulled` — what the commit KEPT where
the old branch reported what it destroyed. The `Silent full-fidelity auto-commit` line is
byte-identical to before (the in-game `AutoMergeCommit` cell greps it), and the
`autocommit-outside-flight … entry` line is untouched.

**Headless proof** (`Source/Parsek.Tests/`): `SilentFullFidelityCommitDecisionTests` now
enumerates the WHOLE 36-row matrix (autoMerge × 3 states × reFly × 3 scenes) with each
row's expected route and reason written out rather than checked against an oracle — an
oracle would be a second copy of the predicate and would agree with it when both are
wrong. `AutoMergeGhostOnlyReachabilityTests` keeps its ghost-only cost cell as the REPRO
half (that branch is still reached by the three carve-outs) and adds the fixed behaviour:
the same disk-restored Limbo tree keeps both snapshots through
`BuildDefaultVesselDecisions` + `ApplyVesselDecisions`, an unspawnable `sit=FLYING` shape
is still ghost-only'd WITH its `GhostVisualSnapshot` copied, and a re-fly leak detector
reds if the fix ever widens into the §10 carve-out. Its cells 2 and 3 FLIPPED: they used
to assert the ghost-only route for the two non-Finalized states and now assert
`LimboPreservingFullFidelity`; their reachability claim is unchanged.

**A SECOND GAP THE FIX EXPOSED, and closed with it: null-terminal leaves bypassed the
spawnable-terminal rejection.** Found in independent review of this branch, and it is a
consequence of the fix rather than a pre-existing defect anyone could reach — un-finalized
recordings only became a *committed* population once Limbo stashes started committing at
fidelity. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd`'s `IsSpawnableTerminal` rejection
sits INSIDE `if (rec.TerminalStateValue.HasValue …)`, so a recording with no terminal state
never reaches it, and the only other situation gate (`IsSnapshotSituationUnsafe`) knows
FLYING and SUB_ORBITAL. That left **`sit=ESCAPING` and `sit=DOCKED` reading as spawnable**
where the Finalized route ghost-onlys them by design (`DetermineTerminalState` maps them to
`SubOrbital` and `Docked`). Concretely: an interplanetary probe on an escape trajectory,
quickloaded, exited via entrance B — snapshot preserved AND spawn-eligible.

The remedy mirrors the Finalized route instead of inventing a second policy:
`TryMapSituationNameToTerminalState` sits beside `IsSpawnableTerminal` and maps the
snapshot's `sit` NAME to the terminal the finalize path would have stamped; a null-terminal
recording whose mirrored terminal is not spawnable is rejected. The two tables cannot drift
— `SpawnSafetyNetTests.SituationNameMapping_MirrorsDetermineTerminalState` walks KSP's own
`Vessel.Situations` enum and asserts they agree, so adding a situation to one and not the
other reds. **This rejects SPAWNING, not the snapshot**: the fix's promise is that a silent
commit never DESTROYS a snapshot, not that every snapshot spawns.

Two things worth stating plainly. First, **mitigation, which is why this is a refinement
rather than a crisis**: the common case was already covered downstream by pid+guid
adoption, so this narrows a window rather than stopping a live catastrophe. Second, a
**deliberate scope boundary**: an ABSENT or unrecognised `sit` is left on its pre-fix
answer (allowed) rather than tightened to "reject on no evidence". Every real snapshot
carries the field (`VesselSpawner.TryBackupSnapshot` -> `ProtoVessel.Save` writes it), so
the population without one is synthetic test fixtures — 16 cells across 5 files pin the
current answer, `MergeDialogVesselTests.CanPersistVessel_NullTerminalState_ReturnsTrue` by
name. Tightening it is separable, has its own blast radius, and has no demonstrated
reachable case; folding it in here would flip a named contract as a side effect of a
targeted fix. Pinned as unchanged-by-design by
`SpawnSafetyNetTests.UnfinalizedRecording_AbsentSit_IsUnchangedByDesign` so it cannot drift
either way in silence.

**The harness fixture grew two leaves to match** (`PendingLimboTreeFixture`, four leaves
now). The original two never exercised the null-terminal branch the fidelity fix depends
on: the spawnable one carried `WithTerminalState(Orbiting)`, a finalized-SHAPED recording
inside a Limbo tree, where a real `StashActiveTreeAsPendingLimbo` stamps none. A confirm
flight over that fixture would have proven the fix on a shape a quickload cannot produce.
The added `coast` (no terminal, ORBITING) and `escape` (no terminal, ESCAPING) leaves are
the genuine shape, one per outcome — and the escape leaf is the one whose result DIFFERS
between a build with the situation gate and one without (`snapshotsPreserved=3
snapshotsReleased=1` with it, `4` / `0` without), which is what makes the confirm flight
discriminating rather than merely agreeable. Both specs' predicted numbers are
machine-checked headlessly by
`AutoMergeGhostOnlyReachabilityTests.PendingLimboTreeFixture_PredictedCommitNumbers_MatchTheSpecs`,
which materializes the same tree the injector writes. That cell also caught the first
draft's derivation being WRONG in a way review would not have: the fixture tree carries no
BranchPoints, so `ParentRecordingId` alone does not make the root a non-leaf and ALL FOUR
recordings are leaves. Both specs' `recordings.count` went back to a RANGE for the same
reason — the 2026-08-29 measurement of 2 described the old shape, and re-pinning to 4 by
argument is the tighten-by-argument their own headers forbid.

**THE ONE REMAINING LOSSY PATH, out of scope by design: the OnSave safety net.**
`SafetyNetAutoCommitPending` still blanket-nulls, and it is reachable in one narrow way —
when the commit above throws, the catch leaves the tree stashed, and that retry window runs
only to the next OnSAVE, not the next load; any OnSave outside FLIGHT hits the safety net
first. That site stays ghost-only for the reason plan §4.1 gives (routing it through
`MergeCommit` would run a quicksave inside OnSave — the reentrancy hazard), and it is
defense-in-depth that is unreachable under normal operation. Both the catch comment and its
Error message now say this rather than promising a retry the safety net can pre-empt.

**THE CONFIRM FLIGHTS (2026-08-29) — THE FIX IS NOT A HEADLESS CLAIM. Both routes ran
against a provisioned instance carrying this branch's own build (deployed DLL sha256
`52faf03dd6158b45…`, hash-IDENTICAL to the building worktree's `bin/Debug`, with both new
literals grepped present), and every predicted number came back EXACTLY.**

| run | route | verdict | wall | reading |
| --- | --- | --- | --- | --- |
| `2026-08-29_1200_S0.9-automerge-pending-limbo-cold-load` | cold-load outside-flight | PASS attempt 1 | 54 s | `recordings=4 spawnable=3 snapshotsPreserved=3 snapshotsReleased=1` |
| `2026-08-29_1202_S0.10-automerge-limbo-warm-exit` | scene-exit (warm, ENTRANCE B) | PASS attempt 1 | 55 s | identical |

Every verifier PASS/SKIPPED on both, expectations mismatches=0, analyzer RED=0, zero
`[Parsek][ERROR]`, and S0.10's clobber guard silent. `Ghost-only auto-commit`,
`Auto-commit tree ghost-only` and `Silent full-fidelity auto-commit` are ALL ABSENT from
both logs (0 occurrences each) — the first two are the pre-fix outcome these specs were
built to measure, the third would have meant the tree was re-finalized somewhere. S0.10
also re-confirmed its ENTRANCE-B subject unchanged: `coroutine aborted by destroy` present,
`not active within 3s` verifiably absent.

**The per-leaf reading is the un-finalized situation gate measured live**, and it is
identical on both routes:

```
leaf='limbo_root_2f7a41c8'   vessel='Limbo Stack'    terminal=null      canPersist=True
leaf='limbo_child_9b3e05d1'  vessel='Limbo Upper'    terminal=Orbiting  canPersist=True
leaf='limbo_coast_5d1c73a2'  vessel='Limbo Coaster'  terminal=null      canPersist=True
leaf='limbo_escape_e40b6cf7' vessel='Limbo Escaper'  terminal=null      canPersist=False
ApplyVesselDecisions: ghost-only for 'Limbo Escaper' … spawn snapshot nulled, ghostVisual=True
```

The ESCAPING leaf is the only denial, and its ghost geometry is preserved on the way —
precisely what a build WITHOUT the gate would not have done (it would have read
`canPersist=True`, and the commit line would have said `snapshotsPreserved=4
snapshotsReleased=0`). That one leaf is what makes these runs a MEASUREMENT of the gate
rather than an agreement with it.

**Durability, from S0.9's produced save** (snapshotted alongside the run artifacts): all
four recordings committed into one `RECORDING_TREE`, and the escape leaf alone carries a
`_ghost.craft` sidecar. Its `_vessel.craft` remains on disk but is unreachable —
re-hydration in `ShouldSpawnAtRecordingEnd` requires a spawnable TERMINAL state and it has
none — so the denial survives a reload rather than being an in-memory-only effect.

**One wording correction worth making explicit, because the shorthand invites a wrong
reading.** "The escape leaf keeps its snapshot" is NOT what happens and never was the
promise. Its `VesselSnapshot` is RELEASED (nulled, `snapshotsReleased=1`) with
`GhostVisualSnapshot` copied and its crew reservation freed — exactly what the Merge dialog
does for a non-spawnable leaf. The fix's promise is that a silent commit never destroys a
snapshot **the dialog would have kept**, not that every snapshot survives.

**What is still open, unchanged by the fix.** (a) FREQUENCY — how often the resume match
misses on ordinary flights — is untouched; the fix makes the outcome harmless rather than
making the miss rarer. (b) Step 1 (a REAL quickload producing the Limbo stash) is still
injected rather than produced by both specs. (c) ENTRANCE A is still undriven (the seam
verb gap above). (d) The dangling limbo dispatch (`ParsekScenario.cs` limbo-dispatch block
arming an `onFlightReady` restore outside FLIGHT) is untouched and still worth a look
independently — the fix consumes the tree at fidelity, it does not stop the dispatch being
armed where it can never fire.

## ~~TEST-HYGIENE - SUPPRESSLOGGING-LEFT-ON-IN-DISPOSE: 406 xUnit classes end `Dispose()` by re-suppressing the global log, deterministically blanking the NEXT Sequential class's log capture~~ [FOUND 2026-08-29 while building the AUTOMERGE-ON-BY-DEFAULT coverage (branch `automerge-coverage`), by hitting it: a newly added class made a previously-latent ordering hazard FIRE. TEST-INFRASTRUCTURE ONLY - no product code is involved and no shipped behaviour is at risk. CLOSED 2026-09-02 at the root, without the sweep]

**Fix (2026-09-02).** `ParsekLog.TestSinkForTesting` is now a property whose setter clears `SuppressLogging` whenever a non-null sink is installed: installing a sink means "capture this class's log", so a predecessor's trailing `SuppressLogging = true` in `Dispose()` can no longer blank it. The class's own later `SuppressLogging = true` or a `SuppressScope()` still wins (the setter only acts at install time), so the two production/test uses of `SuppressScope` and every class that deliberately suppresses after installing a sink keep their meaning. The 406 `Dispose()` sites are left as they are: they are now harmless for sink captures (the observer seam `TestObserverForTesting` is deliberately not covered - a class capturing through it still has to clear the flag itself, as `ParsekLogTests` does). The audit that made the sweep expensive ("was any class RELYING on the suppression") was run mechanically instead: the full suite is green with the change, so no class relied on the blanking in a way the change breaks. Green cannot prove the absence of vacuity, and the PR #1592 review found the one it un-vacuumed: `RecordingsTableUITests` suppresses and THEN installs its sink in the constructor, so `ApplyAutoLoopRange_Disable_AlreadyNaN_NoLogEmitted`'s `DoesNotContain` was asserting against a guaranteed-empty list before and asserts against a real capture now (still green). One ordering rule comes with the fix: restoring a saved non-null sink also clears the flag, so a save/restore pair restores the sink BEFORE the flag - every in-game test that saves both already does. Pinned by `ParsekLogTests.InstallingASink_ClearsSuppressLogging_SoAPredecessorsDisposeCannotBlankTheCapture`.

Original entry follows for the mechanism.

**The shape.** `ParsekLog.SuppressLogging` is a static bool. The house pattern for a
Sequential test class that captures log output is: set `ParsekLog.TestSinkForTesting` in
the constructor, and in `Dispose()` call `ParsekLog.ResetTestOverrides()` — which already
restores `SuppressLogging = false` — and then **set it back to `true` anyway**. That last
line is the bug. It does not restore a prior value (nothing saved one); it asserts a
global default that is wrong for every class that captures logs. The next Sequential class
to run constructs its sink, runs its test, and asserts on a list that is EMPTY — because
the writes were suppressed by a class that finished before it started.

**Why it has stayed invisible.** The victim has to (a) capture logs, (b) not re-clear the
flag itself, and (c) run immediately after a suppressor within the `Sequential` collection.
xUnit's ordering makes that stable in practice but not by contract, so the population sits
green until an addition reshuffles it. That is exactly what happened here: adding
`AutoMergeGhostOnlyReachabilityTests` (which sorts directly before `AutorunExitTests`) took
`AutorunExitTests.PerformAutorunExit_ThrowingQuit_IsContainedAsError` from passing to
failing with `Assert.Contains() Failure ... In value: List<String> []` — an empty capture,
not a wrong one. It passed in isolation and failed in the full run, which is the signature.

**Population, counted mechanically (2026-08-29):** 463 files under `Source/Parsek.Tests`
contain `ParsekLog.SuppressLogging = true`; **406 of them have it inside `Dispose()`**,
which is the hazardous position. Three verified examples, all identical in shape:
`Source/Parsek.Tests/Analyzer/BaselineFilterLoggingTests.cs:29`,
`Source/Parsek.Tests/Analyzer/SaveDirectoryLoaderTests.cs:34`,
`Source/Parsek.Tests/Analyzer/Rules/Inv9RewindPointTests.cs:38`. (`SaveDirectoryLoaderTests`
and `Inv9RewindPointTests` both save and restore `RecordingStore.SuppressLogging` correctly
on the line above — so the fix pattern is already in the file, applied to the other flag.)

**Fix template — already applied in one place, deliberately not swept.**
`Source/Parsek.Tests/AutoMergeGhostOnlyReachabilityTests.cs`'s `Dispose()` calls
`ParsekLog.ResetTestOverrides()` and stops there, with a comment saying why. That is the
whole fix: drop the trailing re-suppress, or save the incoming value in the constructor and
restore THAT. Either is a one-line change per class.

**Why no sweep is proposed here.** 406 mechanical edits across the test tree is a change
whose review cost is entirely in confirming that no class was actually RELYING on the
suppression (a class that asserts on log SILENCE would legitimately want it), and that
audit is the work, not the edit. It also has a real chance of churning a file another
branch is mid-edit on. Recorded so the next person who hits an empty log capture reaches
this entry in one grep instead of re-deriving it — which is most of the value — and so a
sweep, if taken, is taken deliberately.

**Symptom to grep for:** an xUnit failure reading `Assert.Contains() Failure` with
`In value: List<String> []` (or any empty captured-log list) that PASSES when the class is
run with `--filter` and FAILS in the full suite.

## BEHAVIOR PIN — SYNTHETIC-CONTRACT-FAIL-PENALTY-CLAMPED-BY-DRAWDOWN-GUARD: the guarded-drawdown protection correctly refuses a synthetic `ContractFail` penalty that stock never debited [MEASURED 2026-08-20 by `L5-career-contract-complete`'s green flight (run `2026-08-20_2240`), the FIRST run ever to drive `ContractsModule.PrePass`'s injection under a gate. **RECLASSIFIED 2026-08-28 (branch `ledger-followups`) from open defect to a DOCUMENTED, MEASURED BEHAVIOR PIN.** REPORT-ONLY and GATED AS MEASURED: no product change is proposed, and the clamp is pinned so a change of behaviour has to be taken deliberately]

**Not a defect — a pin. Read this paragraph before the evidence below.** The guard is
behaving CORRECTLY here, and the shape it clamps CANNOT OCCUR IN A REAL CAREER. The
fixture's contract B is ledger-only: `career-contract-pad` splices nothing into
`ContractSystem`, so stock never knew B existed and therefore never debited the live pools
for its failure. The reconstruction spends the penalty pack while the live pool stays put,
which arrives at the patcher as a bare drawdown with no time-travel context — exactly the
missing-earning-channel signature PR #1097's guard exists to refuse. In a real career stock
applies the penalty to the live pool ITSELF at fail time and Parsek captures the terminal
`ContractFail` row through `GameStateEventConverter.ConvertContractFailed`, so both sides
step down by the same pack and there is nothing to clamp. The original header framed this
as "the debit never reaches the live career", which reads as a defect statement; it is a
correct refusal of a fixture-only shape. **Do NOT relax the earned-value guard on this
entry's evidence.**

**Escalation trigger — the ONE thing that would reopen this.** A driven scenario that flies
a REAL STOCK contract failure (stock debits the live pool, Parsek captures the terminal
row) and STILL measures a `GUARDED DRAWDOWN clamped` line. That, and only that, turns the
parked policy question below into a live one. A clamp measured on this or any other
ledger-only fixture does not, because the drawdown direction there is an artifact of the
fixture's construction.

**Two deferred hardening steps, recorded as FUTURE WORK — neither is done.**
1. **Gate the walk-local property.** The produced `ledger.pgld` carrying NO synthetic
   `type = 7` row is OBSERVED but UNGATED (see the note below). A `saveParse` /
   `hlib` window asserting the absence would close the residual risk that a refactor
   passing the LIVE action list to `PrePass` instead of the copy silently starts
   persisting the injected row while this spec stays green.
2. **Fly the real-stock-fail shape.** No committed run has ever driven a genuine stock
   contract failure through the patcher. Until one exists, the real-stock-fail signature
   is reasoned-from-source, not measured — and it is also the escalation trigger above.

**What fires, and it all fires correctly.** `career-contract-pad` carries two
fixture-authored `type = 5` rows and no terminal row; B's `deadlineUT = 100` sits
between the cold-load walk's clock (B's own accept UT, 6) and the commit-time walk's
(the flight's rows out to ~348). The commit-time recalc produced, in one burst:

```
PrePass: injected synthetic ContractFail for contractId='c47d0a91-...' at deadlineUT=100 fundsPenalty=9000 repPenalty=1 (nowUT=345.59999999997564 source=lastActionUT)
DeadlineExpired: contractId='c47d0a91-...' deadline passed at currentUT=100, slot freed, activeSlots=1/2
Fail: contractId='c47d0a91-...' fundsPenalty=9000 repPenalty=1 wasActive=False activeSlots=1/2
```

and the RECONSTRUCTION really did spend the pack - `running=527558` against
`live=536558` for funds, `running=0.99999749660491943` against `live=1.9999988079071045`
for reputation, i.e. exactly the 9000 and the 1.

**Where it stops.** Neither reaches KSP:

```
PatchFunds: GUARDED DRAWDOWN clamped resource=Funds running=527558 live=536558 wouldBeTarget=527558 clampedTo=536558 (no time-travel context) - earned value preserved; ledger may be missing an earning channel
PatchReputation: GUARDED DRAWDOWN clamped resource=Reputation running=0.99999749660491943 live=1.9999988079071045 wouldBeTarget=0.99999749660491943 clampedTo=1.9999988079071045 (no time-travel context) - earned value preserved; ledger may be missing an earning channel
```

**Why the guard clamps HERE, and why that does not generalise.** In THIS fixture the
save's stock `CONTRACTS` node is EMPTY BY CONSTRUCTION - `career-contract-pad` splices
nothing into `ContractSystem`, and both contracts exist only as fixture-authored
`type = 5` rows in `Parsek/GameState/ledger.pgld`. Stock therefore never debited the
live pools for B's failure, because stock never knew B existed. The reconstruction
spends the penalty pack and the live pool does not follow, so the recalc arrives at
the patcher as a bare drawdown with no time-travel context - which is exactly the
missing-earning-channel signature the guard exists to refuse. The drawdown direction
here is an artifact of the fixture's shape, guaranteed by construction, and not
evidence about how the guard treats contract penalties in general.

**A REAL stock contract fail is a different shape, and it is UNTESTED rather than
measured to clamp.** When stock fails a live contract it applies the penalty to the
live pool ITSELF at fail time, and Parsek captures the terminal `ContractFail` row
through `GameStateEventConverter.ConvertContractFailed`
(`Source/Parsek/GameActions/GameStateEventConverter.cs` ~:811-828) into `FundsModule`
and the reputation module. Both sides then move together: the reconstruction's running
value and the live pool step down by the same pack, so the patcher sees no unexplained
drawdown and there is nothing to clamp. No committed run has ever flown that shape, so
the real-stock-fail signature is an OPEN question, not a demonstrated clamp. **Do NOT
relax the PR #1097 earned-value guard on this entry's evidence** - this entry measures
a synthetic fixture in which the guard is behaving correctly.

**The policy question, and why it is parked.** IF a real-stock-fail scenario is ever
flown and IT measures a clamp, then there is a genuine policy decision to take about
the drawdown guard, with a real argument on each side: a contract failure is a debit a
player would expect to feel; equally, a guard that lets any recalc reduce a career's
funds is exactly the protection PR #1097 exists to provide. Until such a run exists
that question is not live, and nothing in this lane proposes a product change. The
honest interim state is: on this synthetic fixture the ledger reconstruction is
CORRECT and the live career is UNCHANGED, and both halves are now measured rather than
assumed. `L5-career-contract-complete` pins
`PatchFunds: GUARDED DRAWDOWN clamped resource=Funds` as a required token precisely
so a change in either direction reds and forces the decision to be taken on purpose.
The numbers are deliberately NOT in the token: `running=` moves during the recalc
burst (the same run logged an earlier clamp at `running=522200 live=531200`, before
the recovery credit landed) and `live=` is the flight's own earnings, which
`L3-career-science-recover` owns.

**Note for whoever takes it: the synthetic row is WALK-LOCAL, and that property is
OBSERVED but UNGATED.** The same run measured that the produced `ledger.pgld` carries
the two accepts and NO `type = 7` row, so the injection re-derives on every walk
rather than being persisted once. That is what makes the token stable across runs, and
it also means any fix has to keep working on a re-derived row rather than on a stored
one. It is structurally true today - `RecalculationEngine.SortActions` returns a NEW
list ("the input is not modified"), and `PrePassAllModules` hands the modules that
copy (`Source/Parsek/GameActions/RecalculationEngine.cs` ~:709), so an injected row
lives and dies inside one walk. But NOTHING GATES IT. Residual risk, one line: a
refactor that passed the LIVE action list to `PrePass` instead of the copy would
persist the synthetic row and this spec would still be green, because every run
re-copies the committed fixture over the previous run's save.

**Evidence, and it is quoted rather than pointed at.** The durable record is what is
already reproduced VERBATIM above: the three measured lines (`PrePass: injected
synthetic ContractFail ...` / `DeadlineExpired: ...` / `Fail: ...`), the two
`GUARDED DRAWDOWN clamped` lines, and the pool comparisons `running=527558` against
`live=536558` for funds and `running=0.99999749660491943` against
`live=1.9999988079071045` for reputation. They are quoted in full BECAUSE the green
run's artifacts (`2026-08-20_2240_L5-career-contract-complete.*` under
`harness/results/`) are generated and gitignored - nothing outside this entry preserves
them. Do NOT cite `logs/2026-08-21_0124_L5-career-contract-complete/` for THIS finding:
that folder is flight 1, whose ledger loaded `actions=1` and which logged ZERO
injection lines, so it contradicts rather than supports this entry. It is the correct
pointer for the Progress-node finding below, and only there.

## SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE: a `Progress { FirstLaunch }` node written into a file-constructed career save is not read back, and it silently kills any save-authored Active `PartTest` [MEASURED 2026-08-20 by `L5-career-contract-complete`'s first flight (run `2026-08-20_2217`). HARNESS-FIXTURE FINDING, REPORT-ONLY: no product change is proposed, and nothing gates it. It is filed because it BLOCKS a specific class of fixture and because the next author to try one will otherwise spend the same flight]

**What was tried.** `career-contract-pad` v1 spliced two nodes into
`career-science-pad`'s save so the `science_bench_recover` flight would COMPLETE a
real stock contract live: a `state = Active` `PartTest` on `solidBooster.sm.v2` at
`sit = PRELAUNCH`, and `Progress { FirstLaunch { completedManned = 4 } }`. The
completion mechanism was derived from the decompiled KSP 1.12.5
`Assembly-CSharp.dll` before flying, and every link still holds:
`ModuleTestSubject.OnActive()` fires `onTestRun` on a staging activation with no
situation filter of its own; `solidBoosterRT-5_v2.cfg` declares `useStaging = True`
and `situationMask = 60`, which includes `PRELAUNCH`; the mission emits its single
`ACTION_ACTIVATE_STAGE` on the first decision frame, from PRELAUNCH; and
`Contracts.Parameters.PartTest.OnPartRunTest` completes when the part name matches
and `AllChildParametersComplete()`.

**What happened.** The flight was textbook - MISSION-OK, all nine phases, analyzer
`red=0`, zero `[Parsek][ERROR]`, zero Unity exceptions, 2 recordings, 463 s wall -
and the completion never fired. The contract was gone from `ContractSystem` before
the mission's first frame, and stock re-OFFERED a fresh contract with the identical
subject 8 s later:

```
01:17:50.992 PatchContracts: ledger has 1 active contracts, ... KSP has 0 current contracts, 0 finished contracts
01:17:57.243 Game state: ContractOffered 'Test RT-5 "Flea" Solid Fuel Booster at the Launch Site.' (diagnostic, not stored)
```

**The cause, read off two stock log lines rather than inferred.** The same run
logged BOTH of these at 01:17:57, i.e. DURING the flight:

```
[Progress Node Reached]: FirstLaunch
[Progress Node Complete]: FirstLaunch
```

`KSPAchievements.FirstLaunch.TestFlight` guards its award with
`if (!base.IsComplete) { Complete(); ... }`, and `ProgressNode.Complete()` calls
`Reach()` only when `!reached`. `ProgressNode.Load` sets `reached = true` on its
FIRST line and `complete = true` in its `completedManned` branch. Both lines
appearing therefore proves both flags were false at launch: **the spliced
`FirstLaunch` node was never `Load`ed.** From there the contract's fate is
mechanical - `Contracts.Templates.PartTest.MeetRequirements()` is
`if (!ProgressTracking.Instance.NodeComplete("FirstLaunch")) return false;`, and
`Contract.Update()` re-checks `MeetRequirements()` on EVERY tick of an ACTIVE
contract, retiring it to `OfferExpired`, which is removed outright rather than kept
in `ContractsFinished` - matching the observed `0 current, 0 finished`.

**It is NOT "authored ScenarioModule children are lost".** The same run's produced
save grew `ResearchAndDevelopment`'s `Tech` node from 13 `part =` lines to 23, so
that module's child node loaded and was written back. It is also not a shape
problem: the spliced `Progress` and `CONTRACT` blocks are byte-identical in
structure and indentation to the ones `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`
carries, the save's brace balance is clean, there are no duplicate `ProgressTracking`
or `ContractSystem` SCENARIO nodes, and the two saves carry the SAME 22 scenario
modules with the same `scene` lists. Nor is it a staging failure: `career-earned-pad`
- derived from a REAL KSP-written save - splices an Active `PartTest` the same way,
and `L4-ledger-groundtruth-strict` measures all 9 of its contracts loading and
surviving (`KSP has 9 current contracts`), which is itself proof that
`MeetRequirements()` reads TRUE there and therefore that ProgressTracking DID restore
in that lineage.

**What is not known** is why the same node restores from one save and not from the
other. `ProgressTracking.OnLoad` is `if (!node.HasNode("Progress")) return;` then
`achievementTree.Load(node.GetNode("Progress"))`, `ProgressTree.Load` matches by
`progressNode.Id`, and `OnAwake` populates the tree before either - so on the code
alone it should work. The difference that remains unexamined is LINEAGE:
`career-contract-pad` descends from the file-constructed `fresh-career`,
`career-earned-pad` from a save KSP itself wrote.

**Consequence, and why the lane routed around it rather than through it.** Until
this is explained, a save-authored Active `PartTest` cannot be made to survive into a
flight in the `fresh-career` lineage, so the LIVE `ContractComplete` gate this wave
set out to build is not reachable from a file-constructed fixture. Re-flying a second
guessed node shape would spend a flight on a hypothesis. `career-contract-pad` was
therefore rebuilt with every claim in the LEDGER instead - two `type = 5` rows, one
with a deadline sized to lapse mid-flight - and `L5-career-contract-complete` now
gates `ContractsModule.PrePass`'s synthetic-fail injection, `CheckDeadlines`'s
retirement and `ProcessFail`'s penalty application, none of which had a committed gate
either. `ContractComplete` and `ContractCancel` remain ungated.

**Where to start if someone picks this up.** The cheapest next experiment is a
fixture derived from a KSP-WRITTEN career (the `career-earned-pad` lineage) carrying
an Active `PartTest` whose part is on ITS craft and whose `sit` is the situation that
craft is in when it stages - which would separate "lineage" from "authored node" in
one flight. `harness/tools/build_career_contract_pad.py`'s module docstring carries
the full derivation, and the collected evidence is
`logs/2026-08-21_0124_L5-career-contract-complete/`.


## MISSIONS-T2.2-LINEAGE-FAN-NOT-COLLAPSIBLE: the flattened per-vessel rows cannot fold a many-child separation fan [ACCEPTED LIMITATION 2026-08-20, from the Stage-2 review of the missions-UI branch]

The T2.2 flattening (one row per physical vessel, lineage-only depth) renders a vessel's
separated children unconditionally; the vessel-row caret expands/collapses only the interval
DETAIL (Advanced). The old staircase's caret collapsed the entire child subtree, so an
asparagus launch with 8 boosters could be folded to one row; the new view always shows the
~9 vessel rows (still strictly fewer rows than the old EXPANDED default, which added interval
and roster-atom rows on top). Accepted because a second meaning on the same caret needs a
three-state affordance; revisit if long missions make the tab scroll-heavy in practice.
Related deferral: `MissionPresentation.SeparationVerb` / `IsDockEventWord` classify on the
RENDERED event words rather than `BranchPointType` + cause - re-wording `BranchEventName`
silently degrades the T1.3/T1.4 phrasing to its fallbacks; the right home is a typed
classifier beside `BranchEventName` (or a `BranchPointType?` on `MissionCompositionNode`),
deliberately not done during the review-fix pass.

## BDOCK-2-SAME-TREE-DOCK-COVERAGE: no harness scenario flies a same-tree cross-session dock

`BDOCK-1-station-interceptor` covers the cross-tree dock recording pipeline; the
same-tree cross-session shape (fly A, commit, switch-fly the offshoot D, dock
A->D: records single-parent with a same-tree target, model extract section 1.5)
is flown by no spec. Candidate `BDOCK-2`: fly, commit, switch-fly, dock, assert
the recovered same-tree link derives. Not a merge gate for the dock-event-graph
scope (the derivation is pinned by synthetic in-game cells); file when docking
coverage next expands. Also missing per BDOCK-1's own header note: an
orbital-rendezvous-dock D10 value and a same-craft-twice identity D18 value.

## MAPRENDER-SEAM-LENS-EVALUATES-UNSHIFTED-EPOCH-ON-CREATION-FRAME: the seam-endpoint lens reads the RECORDED seam UT instead of the replayed one on a ghost proto's creation frame, and raises `seam-endpoint-outside-soi` against an endpoint 157x the SOI away [MEASURED 2026-08-19 by `V16T-laythe-ts-arrival`'s reading run and **RECURRED on its ARMED run `2026-08-19_2212` (PASS attempt 1)**, which makes it DETERMINISTIC for the single-jump shape rather than a one-off. REPORT-ONLY - the harness classified the reason UNLISTED and did not gate on it, and `V16M-laythe-player-loop`'s stepped-epoch censuses prove the underlying recurrence is FINE. Same family as MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP; NO product change is proposed]

**READ THE VERDICT BEFORE THE MECHANISM, because this raise names the one thing the
whole Jool research programme is watching for and it is NOT that thing.** The
accompanying census line reads `evaluated=2 outsideSoi=1`, i.e. "one replayed
SOI-entry endpoint fell outside the destination SOI" - which is precisely the
eccentric-moon phase-lock drift
`docs/dev/research/same-parent-reaim-jool-system.md` predicts for Bop and Pol.
**IT IS NOT DRIFT.** `V16M-laythe-player-loop`, same fixture, same recording, same
arrival, taking its readings at STEPPED bracket epochs where the loop shift has
bound, measured `evaluated=2 outsideSoi=0` at cycle 1 AND at cycle 2 - i.e. after a
full 20-period cadence the replayed entries really do still sit inside Laythe's SOI.
The `outsideSoi=1` below is the instrument evaluating the wrong epoch on one frame.
Do not let a future ledger cite it as an observed recurrence failure.

### The measurement, verbatim

`harness/results/2026-08-19_2115_V16T-laythe-ts-arrival_shots/KSP.log` line 11866:

    phase=Anomaly surface=ProtoOrbitLine pid=3145128013
      recId=370d38246d6e42848f140884081428af frame=6626
      currentUT=29874214.240 effUT=28814456.826 reason=seam-endpoint-outside-soi
      fromBody=Jool toBody=Laythe seamUT=28814456.8
      endpointDist=585846592m soi=3723646m ratio=157.3314 tol=1.0050
      recordedSeamUT=28814456.8 clock=raw seed=no-seed loopShift=2066254.3

and the census two lines later (11875): `seam-endpoint summary evaluated=2 outsideSoi=1`.

### What the fields say, in the order that matters

1. **`effUT` IS THE RECORDED SEAM EPOCH.** `effUT=28814456.826` equals
   `recordedSeamUT=28814456.8` equals the committed recording's own Jool->Laythe
   ORBIT_SEGMENT body change. The live clock at that frame is
   `currentUT=29874214.240`. The lens evaluated the endpoint **1,059,757 s in the
   past** - about one full loop cadence.
2. **`clock=raw seed=no-seed`** - the effective-UT resolution had no seed to work
   from, so it fell back to the raw recorded epoch rather than the replayed one.
   `loopShift=2066254.3` is present on the line but was evidently not applied to the
   value the lens tested.
3. **The distance follows mechanically.** At the recorded seam epoch the recording's
   subject is still in its JOOL PARK, so the endpoint lands 585,846,592 m from
   Laythe against a 3,723,646 m SOI - `ratio=157.33`. The raise is arithmetically
   correct about the wrong point.
4. **It is a CREATION frame.** Frame 6626 also carries
   `phase=GhostCreated surface=ProtoIcon pid=3145128013 ... body=Jool
   scene=TRACKSTATION` at a Jool-scale `worldPos`, i.e. the proto is being brought
   into existence on this frame - the same frame class as the icon entry's.

### Why it surfaced HERE and not on any earlier lane: k scales the gap

Every prior loop subject had a cadence of exactly ONE moon period, so the un-shifted
epoch sits under one period away from the live clock and the lens skips on
`body-mismatch` (V15M and V15T both read exactly that skip form). **This lane is the
suite's first k > 1 cadence** - 20 Laythe periods, 1,059,617.581 s - so the same
binding gap displaces the evaluated epoch by 1.06 Ms, far enough that the ghost's
recorded position at that epoch is in a DIFFERENT BODY'S SOI and the lens has
something to fail on. The defect is not new; the k = 20 cadence is what made it
observable.

### The family claim, and its limit

This and MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP are one family: two
different lenses (`ProtoIcon` phase, `ProtoOrbitLine` seam endpoint) raising on the
same frame class (ghost-proto creation) with the same underlying reading - the loop
shift has not bound at the moment the surface is first evaluated. The icon entry's
lines carry `loopShift=0.0`; this one carries a nonzero `loopShift` that the tested
value did not use. **THAT DIFFERENCE IS NOT EXPLAINED HERE** and it is the reason
this is filed as a sibling rather than merged into the icon entry: "the shift is
zero" and "the shift exists but was not applied" are not obviously the same bug, and
one observation of each is not enough to say.

A third possible member is noted and explicitly NOT claimed:
`V16M-laythe-player-loop`'s two `EnterWatchMode` steps were refused
`reason=no-watchable-ghost` with the adjacent engine line reading
`retired=F zone=Beyond rdist=616131735m` - a ghost that exists but sits at
Jool-park distance from a Laythe-parked observer, the same magnitude as the endpoint
above. Same order, different surface, different code path (a watch auto-select, not
a render lens), and one coincidence of magnitude is not a mechanism.

### Nothing gates it

The harness classified the reason as UNLISTED and therefore REPORT-ONLY - the result
JSON carries `unlistedReasons: ["seam-endpoint-outside-soi"]` - so this raise did not
contribute to that run's PARSEK-FAIL (the `icon-off-orbit` pair did). `V16T` does NOT
tolerate it in `allowedAnomalies`, deliberately: adding a tolerance for something
that is not currently a gate is the wrong direction. If the sweep's classification
ever changes, revisit WITH the run that shows it.

**THE DISCRIMINATING EXPERIMENT**, if anyone wants one: a lane whose cadence is k > 1
but whose observation epoch is reached through a STEPPED bracket rather than a single
jump. V16M is exactly that and raised nothing, which is suggestive but not decisive,
because V16M also never enters the tracking station. A TS lane with a stepped bracket
would separate "creation frame" from "single jump" for both members of the family at
once.

## B5-INWARD-TRANSFER-EVIDENCE-AND-TRIGGERS-ASSUME-OUTWARD: the moon-transfer machine keys BOTH its burn-done evidence and its correction-round spacing to an apsis RISING, and the two available workarounds are MUTUALLY EXCLUSIVE [FOUND BY AUDIT 2026-08-19 while authoring `B25-laythe-orbit`, the suite's first INWARD moon transfer, and BOTH WORKAROUNDS LIVE-PROVEN THE SAME DAY on that lane's flight 1 (runs `_1948` / `_2001`). REPORT-ONLY: a HARNESS constraint on `harness/missions/lib/mlib.py`, NOT a proposed product change; B25 flies with values only and accepts one named degradation]

Every b5 moon-path flight to date (Kerbin->Mun, Kerbin->Minmus, Duna->Ike,
Eve->Gilly) parks LOW and transfers UP to a moon at a HIGHER orbital radius.
`B25-laythe-orbit` is the first to go the other way: its park sits at
590,325,784.59 m, 3.28x Pol's orbit and outside the whole Jool moon system, so the
ejection is RETROGRADE, the intercept is the transfer's PERIAPSIS, and the
home-frame APOAPSIS never moves.

**FIVE OF SEVEN AREAS ARE DIRECTION-AGNOSTIC BY CONSTRUCTION**, and they are
listed so the finding is bounded rather than alarming: the MechJeb plan call
(`_b5_transfer_plan_action` -> `operation_transfer` with `rendezvous = True`, a
general two-orbit solve with no direction argument); the whole COAST phase (every
gate is body- or time-based - `snapshot.body == target_body`,
`body not in _b5_coast_bodies`, `ut + time_to_soi - soi_lead`,
`approach_latch_state` / `approach_warp_clamp` - with no apoapsis or
altitude-increasing test anywhere, and the one `vertical_speed` read taking its
absolute value); the WINDOW logic (mlib's only window solver is the PAD-ALIGN
heliocentric family, which `b5_params_from_dict` hard-rejects without
`interplanetaryTransfer`); TARGET-FLYBY and CAPTURE (the arm gate, the
`ut + time_to_periapsis - lead` warp target, the `0 < ap <= park_max_apoapsis`
capture window and `_b5_left_target_soi` are all read in the TARGET body's own
frame); and the burn-stagnation watchdog (`_b5_track_burn_stagnation` compares
BOTH apsides against their burn-entry values, so an inward burn that moves only
periapsis still registers `burned`).

**TWO SITES ENCODE "OUTWARD", and both are the same mistake: evidence keyed to an
apsis RISING.**

1. **`_b5_transfer_burn_done`'s apoapsis floor is VACUOUS on an inward transfer.**
   The default branch is `apoapsis >= transfer_min_apoapsis`. The retrograde burn
   happens AT the park, which BECOMES the transfer's apoapsis, so the home-frame
   apoapsis does not move: any floor above the park is unreachable forever, and
   any floor below it is already satisfied on the frame BEFORE the burn - which
   leaves `consumed` (an empty node list) as the sole exit evidence and disarms
   the phase's whole purpose. There is NO periapsis-side key: `grep` for
   `transfer_min_periapsis` / `transferMaxPeriapsis` returns nothing anywhere in
   `mlib`.
2. **The ALTITUDE correction trigger cannot space rounds on a descent.**
   `_b5_correction_round_ready`'s altitude mode is a bare
   `body == home and altitude >= trigger[idx]` LEVEL test, not a rising-edge
   crossing. On a descending coast a trigger above the park never fires at all and
   one below it fires on the FIRST coast frame, so a `[0, X]` list spends both
   rounds back to back at transfer start - precisely the shape the mid-coast round
   was added to prevent (the fourth B5 flight's corrected +60 km flyby periapsis
   drifting to -29 km). No altitude value places a second round mid-coast.

**AND THE TWO WORKAROUNDS COLLIDE, which is the part worth writing down.**

- For (1), `ejectionEccFloor` is a working, direction-agnostic substitute
  requiring NO mlib change: it reads the HOME-frame ECCENTRICITY, which on B25's
  hop moves from 7.944e-06 to 0.911956, and `b5_params_from_dict` does not gate it
  on `interplanetaryTransfer`. (Eight interplanetary lanes already set it, all
  just above 1 for a hyperbolic ejection; B25 is the first sub-1 use, and the
  schema's `min = 0.0` already admits that.)
- For (2), `correctionTriggerTimeToSoiSeconds` is direction-agnostic (`time_to_soi`
  descends whichever way the craft is going), but its body domain is
  `_b5_correction_via_bodies`, which on the moon path returns `via_bodies`
  VERBATIM - so it needs `viaBodyNames = ["Jool"]`.
- **THAT SETTING IS EXACTLY WHAT BREAKS (1):** the ecc branch's FIRST disjunct is
  `snapshot.body in params.via_bodies or snapshot.body == params.target_body`,
  which returns True at the park and makes the eccentricity floor vacuous. Naming
  the home body in `viaBodyNames` also does nothing useful for coast legality
  (home is already legal), and naming any OTHER body would LEGALISE a moon transit
  that `_b5_coast_bodies` should be failing loudly - the B15-flight-5 hazard, and a
  live one on a descent that crosses Pol's, Bop's, Tylo's and Vall's shells.

**WHAT B25 DOES, and it is values only.** It takes (1) - burn evidence is a
correctness question and round spacing is a quality one - setting
`ejectionEccFloor = 0.55` (the eccentricity at which the transfer's periapsis has
fallen inside Pol's orbit, so the floor certifies a genuine inward burn rather
than an arbitrary threshold) and keeping `transferMinApoapsisMeters` declared at 0
(the B15 disposition for a schema-required key the evidence does not use). It then
declares exactly ONE scheduled correction round, `[0]`, and delegates the LATE
refinement to `MAX_ARRIVAL_EXTRA_ROUNDS = 2` arrival-quality extras, which are
direction-agnostic but are a SAFETY NET rather than a refinement: they fire only
when the PREDICTED target periapsis is already BELOW
`targetPeriapsisFloorMeters`, with `time_to_soi` inside (600, 3600) s. **A merely
mediocre arrival gets no second look, and that is an accepted degradation on this
lane rather than a fix.**

**BOTH WORKAROUNDS ARE NOW LIVE-PROVEN, on `B25-laythe-orbit` flight 1** (both
attempts INVALID(driver-flake) on that lane's park WINDOW, which is a different
question - the flight itself reached CAPTURE-BURN and delivered a healthy park):

- **The ecc burn-done evidence WORKED.** `startedInHomeOrbit` met on the fixture's
  own park (`value=7.944061403402496e-06`), and then TRANSFER-BURN EXITED - which
  under `ejectionEccFloor` it can only do on a home-frame eccentricity at or above
  0.55. The corrected transfer's last home-framed telemetry reads `ecc=0.954`. The
  apoapsis floor this replaced would have been satisfied AT THE PARK, before the
  burn, which is the vacuity the substitution exists to avoid.
- **The delegation to the arrival-quality extras FIRED.** The machine reports
  `rounds=2 extraRounds=1`: the one scheduled round plus one direction-agnostic
  extra, on a DESCENDING coast. So the degradation is real but bounded, and the
  fallback is not theoretical.
- **And the empty `viaBodyNames` did its second job**: the descent crossed Pol's,
  Bop's, Tylo's and Vall's orbital shells and the collected KSP.log carries ZERO
  occurrences of any of their SOI-boundary tokens, with exactly one `Jool to
  Laythe`.

**THE HONEST FIX, if a future inward lane needs the mid-coast round back**, is a
periapsis-side burn-done predicate in `mlib` - a `transferMaxPeriapsisMeters`
("the burn LOWERED periapsis to at/below this"), which would free `viaBodyNames`
for the time-mode triggers. That is a real machine change and must be argued
rather than patched in; it is NOT proposed here, because one lane is not a
population and B25 has not flown. Pinned meanwhile by
`harness/missions/lib/test_b25_laythe_orbit.py::InwardTransferAuditTests`, which
runs `_b5_transfer_burn_done` against a park-shaped frame and a post-burn-shaped
frame and asserts False-then-True, and which reds if `viaBodyNames` ever appears
in that spec.

---

## MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP: a ghost's proto ICON sits tens of degrees around its own orbit line on the CREATION frame, after a single large TimeJump onto an epoch just inside a foreign moon's SOI [MEASURED 2026-08-18 by `V14T-ike-ts-arrival`, REPRODUCED on its armed run, shown PARENT-INDEPENDENT by `V15T-gilly-ts-arrival`, and measured at a THIRD parent 2026-08-19 by `V16T-laythe-ts-arrival` (Jool/Laythe, 129.15 deg) - which also produced the FIRST count > 1 reading (TWO raises, one frame, two proto pids) and a SECOND LENS showing the same creation-frame binding gap. **RECURRED AT COUNT 2 ON V16T's ARMED RUN `2026-08-19_2212` (PASS attempt 1, the tolerance doing its job)**. **THEN TWICE SILENT: `V17T-laythe-vall-ts-arrival` 2026-08-20 and `V19T-laythe-jool-ts-arrival` 2026-08-21, so the raise is DETERMINISTIC ONLY WITHIN THE V14T/V15T/V16T SUBJECT SHAPE and NOT across the family** - an earlier version of this bracket named only those three lanes and called the behaviour deterministic full stop, which the body has now contradicted twice. V19T was authored as the discriminator between V17T's two simultaneous variables and NARROWS THE CAUSE: nested-SOI is EXCLUDED, self-overlap is the surviving candidate, no mechanism claimed (see the table in the body). **AND SHARPENED 2026-08-27 BY `V20M-jool-kerbin-player-loop` reading run 1 - see TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS, which is the same family: on a 32.6 Ms span the frame every proto settles on is SEGMENT ZERO rather than its own CREATION frame (the two coincide on short spans and separated for 36 of 41 protos there), and the reversion is PERMANENT under the instrument rather than the transient V17M measured. No mechanism claimed for either.** REPORT-ONLY: self-correcting, tolerated by name in the three specs that raise it, `allowedAnomalies` deliberately EMPTY on the silent lanes; NO product change is proposed]

`V14T-ike-ts-arrival` run `2026-08-18_2337` came back PARSEK-FAIL(anomaly) on
attempt 1 with **all sixteen steps green** - every tracking-station route line
fired, `created 1 ghost vessel(s)`, the TS session itself swept clean. The red is
the armed Tier-C sweep doing its job: `anomalySweep hits=['icon-off-orbit']`,
**exactly one line in the whole log**.

### The measurement, verbatim

`harness/results/2026-08-18_2337_V14T-ike-ts-arrival_shots/KSP.log` line 10774:

    phase=Anomaly surface=ProtoIcon pid=2928501323 recId=05ceee33806d4079a1d9d125a1359115
      frame=6979 currentUT=9243139.000 effUT=9243139.000 reason=icon-off-orbit
      angleIconVsOrbitEff=94.05 angleEffVsLive=0.00 loopShift=0.0 effUT=9243139.0
      | lonIcon=107.35 lonOrbitEff=-157.93 lonOrbitLive=-157.93
      | iconR=1019937 orbitEffR=1019937
      | lineActive=True inc=11.886 LAN=285.703 argPe=154.625 sma=-1230685 ecc=1.1385 body=Ike

Read the three groups: **right body, right conic, right radius, wrong phase**.
`iconR` and `orbitEffR` are the SAME 1,019,937 m; `lonOrbitEff` and `lonOrbitLive`
agree exactly (`angleEffVsLive=0.00`, so the loop shift is not involved -
`loopShift=0.0`); only `lonIcon` is 94.05 deg away from both.

### The trigger shape, stated as narrowly as the evidence supports

- **Ghost-proto CREATION frame.** The same frame 6979 carries
  `phase=GhostCreated surface=ProtoIcon ... body=Ike scene=FLIGHT` and
  `phase=FirstPosition ... reason=first-truth-read`. It is the first frame the
  proto exists.
- **FLIGHT scene, not TS.** It fires at 02:38:18.648; this lane's TS `LoadGame`
  is at 02:38:19.58, about a second LATER. The tracking-station ghost
  (pid 3383498847) is created clean and raises nothing. Do not read this as a TS
  defect because it happens to appear in a TS lane's log.
- **After ONE large TimeJump.** V14T jumps 17,223 s in a single step straight onto
  the arrival epoch. **`V14M-ike-player-loop` is the control**: same fixture, same
  tracers, same arrival UT 9,243,139, but reached through a STEPPED bracket
  (-180 / -60 / +140 s). Its reading run `2026-08-18_2336` swept `hits=[]`. That
  pair is what makes "the jump shape, not the fixture" the leading reading.
- **Just inside a foreign moon's SOI, on a hyperbolic approach segment.** r =
  1,019.9 km against Ike's 1,049.6 km SOI boundary; the conic is
  `sma=-1230685 ecc=1.1385`, i.e. segments 6-9 of the committed recording.
- **Self-corrects on the next frame**, and there is exactly one line per run.

### REPRODUCED - it is a trigger, not an incident (2026-08-19)

The reading run showed this ONCE, which is the weakest possible evidence: a single
self-correcting frame is exactly what a one-off transient looks like. The ARMED
re-flight settles it. Run `2026-08-19_0002`, PASS attempt 1:

    anomalySweep status=PASS hits=[] counts={'icon-off-orbit': 1}

Read both halves. `hits=[]` is the TOLERANCE working - the token is declared in
`allowedAnomalies`, so it produces no unallowed hit and the run is green. `counts`
is the raw tally, and it reads **1 again**. Same lane, same fixture, same single
17,223-second jump, same one raise.

That upgrades the finding from an observation to a **reproducible trigger**, and it
sharpens the control at the same time: `V14M-ike-player-loop` has now flown TWICE
(`2026-08-18_2336`, `2026-08-19_0001`, both PASS) against the same fixture, the same
tracers and the same arrival UT through a STEPPED bracket, and swept `hits=[]` with an
empty `counts` on both. Two runs each, one variable between them, opposite results
every time. "The jump shape, not the fixture and not the scene" is no longer the
leading reading - it is the measured one.

WHAT IT DOES NOT UPGRADE: which surface is wrong, or whether V7T's persistent Minmus
raise shares the mechanism. Reproducibility says the trigger is stable.

### PARENT-INDEPENDENT - MEASURED 2026-08-19 at a SECOND parent and moon (V15T)

The "what is NOT established" list used to open with *whether the mechanism is
parent-specific*, and it named the experiment: fly the same single-jump shape at a
different parent. `V15T-gilly-ts-arrival` is that experiment, and it ran as an
ordinary reading run rather than as a probe - which is the stronger form, because
nothing about it was shaped to provoke the raise.

`harness/results/2026-08-19_1739_V15T-gilly-ts-arrival_shots/KSP.log` line 10814,
run `2026-08-19_1739`, PARSEK-FAIL(anomaly) attempt 1 with **all sixteen steps
green** and the TS session clean (`created 1 ghost vessel(s)`,
`phase=GhostCreated surface=ProtoIcon pid=3282224066 ... body=Gilly
scene=TRACKSTATION`):

    phase=Anomaly surface=ProtoIcon pid=687187265 recId=77f724bb1d4844c3b132a1ccc00a7df3
      frame=7757 currentUT=16267740.000 effUT=16267740.000 reason=icon-off-orbit
      angleIconVsOrbitEff=26.49 angleEffVsLive=0.00 loopShift=0.0 effUT=16267740.0
      | lonIcon=46.98 lonOrbitEff=73.92 lonOrbitLive=73.92
      | iconR=49232 orbitEffR=49232
      | lineActive=True inc=17.980 LAN=39.322 argPe=180.550 sma=-20 ecc=1996.2409 body=Gilly

**THE SIGNATURE IS IDENTICAL IN EVERY STRUCTURAL RESPECT.** Ghost-proto CREATION
frame, FLIGHT scene (20:40:10.324, ~2 s before the TS `LoadGame` at 20:40:12), after
ONE large TimeJump onto an epoch just inside a foreign moon's SOI, `iconR` and
`orbitEffR` the SAME value (49,232 m), `lonOrbitEff == lonOrbitLive` with
`angleEffVsLive=0.00` and `loopShift=0.0`, and exactly ONE line in the whole log.
Right body, right conic, right radius, wrong phase - the same three-group read.

**SO PARENT-INDEPENDENCE IS MEASURED, not inferred:** two parents (Duna, Eve), two
moons (Ike, Gilly), SOI scales differing by 8.3x (1,049,599 m vs 126,123 m), two
different recordings, deterministic at both. And the CONTROL travelled with it:
`V15M-gilly-player-loop` (run `2026-08-19_1736`, PASS) reaches the SAME arrival UT
16,267,740 through V14M's stepped -180/-60/+140 bracket on the same fixture with the
same tracers, and swept `anomalySweep hits=[] hitCounts={}`. Two body pairs, one
variable, opposite results in all four runs.

**THE MAGNITUDE IS NOT A CONSTANT, and that is the one genuinely new fact.**
`angleIconVsOrbitEff` reads **94.05 deg at Ike** and **26.49 deg at Gilly** - a 3.5x
difference in the quantity the detector thresholds on. Whatever the icon's phase is
stale BY, it scales with something body- or geometry-dependent rather than being a
fixed angular offset. NOT DIAGNOSED: two points do not separate "stale by a fixed TIME
along very different conics" from anything else, and the Gilly conic is pathological in
its own right (`sma = -20 m, ecc = 1996.24` - recorded in
`harness/scenarios/V15T-gilly-ts-arrival.toml`'s header together with the unexplained
observation that the speed those elements imply is closer to Gilly's own orbital speed
than to the encounter's v_inf). A third body pair, or the frame-by-frame comparison
below, is what would move it.

### What is NOT established

**Whether it shares a mechanism with V7T's persistent raise.** V7T raises
`icon-off-orbit` deterministically at MINMUS on every flight
(`MOON-LOOP-FINDINGS`), so the token is neither new nor moon-specific. Whether THIS
one - creation-frame, one-shot, post-single-jump - is the same mechanism as V7T's
persistent raise is UNKNOWN and would need the two compared frame by frame.

**Whether the icon or the line is wrong.** `angleIconVsOrbitEff` says only that
they disagree. The radius agreement makes a stale-phase icon the natural first
guess (the icon drawn before its position resolves against the freshly-jumped
clock), but nothing here measures which surface is authoritative on a creation
frame.

**The cheap discriminating experiment**, for whoever picks this up: give V14M (or
V15M) a variant with the single-jump shape, or give V14T (or V15T) a variant with the
stepped bracket, and see whether the raise follows the JUMP SHAPE or the SCENE. Four
lanes now exist across two body pairs, each pair sharing a fixture and differing in
exactly that one variable, so the experiment is a step-list edit rather than a new
fixture. The evidence already points hard at the jump shape (stepped: 3 runs, 0
raises; single-jump: 3 runs, 3 raises); what the variant would add is the SCENE half,
which is still confounded - every single-jump run so far is a TS lane.

### The tolerance now in force, and its ceiling

`V14T-ike-ts-arrival` declares `allowedAnomalies = ["icon-off-orbit"]`, added WITH
the flight that shows it (the S1.4 rule). It is BARE rather than
`{ token = ..., maxCount = 1 }`, and that is a deliberate, temporary weakness:
`harness/lib/test_hlib.py::MisplacedAllowedAnomaliesRejectionTests.test_no_committed_spec_arms_a_count_budget`
holds the budget mechanism INERT across the whole suite, and its own comment says
arming one is "an operator decision taken against measured `anomalySweep.hitCounts`
from a GREEN run" - which this lane does not have yet, since the run that measured
the token is the run it red'd. **THAT PRECONDITION IS NOW MET.** The armed re-flight `2026-08-19_0002` is a PASS and
it carries the reading the doctrine asks for: `anomalySweep status=PASS hits=[]
counts={'icon-off-orbit': 1}`. So the arming is READY - an operator may now declare
`allowedAnomalies = [{ token = "icon-off-orbit", maxCount = 1 }]` in
`V14T-ike-ts-arrival`, citing that run, as the suite's FIRST budgeted entry. Two things
must move together when they do: the whole-set invariant cell
`test_no_committed_spec_arms_a_count_budget` currently asserts the empty set and would
red, so it needs a named allowlist in the same edit - the same shape as
`ARMED_ALLOWLIST`, and the same discipline (record the run, not just the token).

`V15T-gilly-ts-arrival` joins on the SAME terms as of 2026-08-19: its reading run
`2026-08-19_1739` red on exactly this token with every step green - the correct catch
its spec pre-registered - and the arming added the same BARE
`allowedAnomalies = ["icon-off-orbit"]` with that run cited.

**AND ITS ARMED RE-FLIGHT MAKES THE RAISE A THIRD DETERMINISTIC SIGHTING, AND MEETS THE
CEILING PRECONDITION ON BOTH LANES.** Run `2026-08-19_1809`, **PASS attempt 1**:

    anomalySweep status=PASS hits=[] counts={'icon-off-orbit': 1}

Read it exactly as V14T's `2026-08-19_0002` was read: `hits=[]` is the tolerance working
and `counts` is the raw tally, which reads **1 again**. So the single-jump creation-frame
trigger has now fired on **FOUR runs across TWO body pairs - TWICE EACH** (V14T `_2337`
and `_0002` at Ike; V15T `_1739` and `_1809` at Gilly) - at a population of exactly 1
every time, with the stepped-bracket control silent on all four of ITS runs (V14M x2,
V15M x2). Four raises, four silences, one variable.

**THE `maxCount = 1` PRECONDITION IS NOW MET FOR BOTH LANES.** The doctrine in the
budget cell asks for "measured `anomalySweep.hitCounts` from a GREEN run"; V14T supplied
one on `2026-08-19_0002` and V15T has now supplied its own on `2026-08-19_1809`. So the
only thing still blocking the ceiling is the whole-set invariant
(`test_no_committed_spec_arms_a_count_budget`), which must move to a named allowlist in
the same edit. When it is taken it should cover BOTH lanes at once, since they are the
same trigger and now have the same evidence: three green readings, population 1 each.

Until that decision is taken the tolerance stays BARE in both specs and the ceiling
lives in their comments rather than in their declarations - so a SECOND raise in one
run would pass unnoticed. That is the honest, and now precisely bounded, cost of
respecting the invariant: the measured population is 1 on each of THREE runs across two
body pairs, so the gap between what is declared (any count) and what is observed
(exactly one) is the entire exposure.

`V14M-ike-player-loop` and `V15M-gilly-player-loop` keep `allowedAnomalies = []` and
stay the controls.

**NO PRODUCT CHANGE IS PROPOSED BY THIS LANE.** A one-frame creation-time icon
phase error that self-corrects is a rendering transient, not a recorded-data
defect; nothing in the recording, the loop unit or the committed save is affected.
What is recorded here is the measurement, the control that isolates the jump
shape, and the discriminating experiment.

### A THIRD PARENT, AND TWO PROPERTIES RETIRED (2026-08-19, `V16T-laythe-ts-arrival`)

`V16T-laythe-ts-arrival` run `2026-08-19_2115` came back PARSEK-FAIL(anomaly) on
attempt 1 with **all sixteen steps green**, exactly as the two earlier readings did.
Its measurement adds a third body pair and changes two things this entry had
previously stated as settled.

**(1) THE PER-RUN COUNT IS NOT 1.** `anomalySweep hits=['icon-off-orbit']
hitCounts={'icon-off-orbit': 2}` - **TWO raises**, on the SAME frame (6572), at the
SAME angle (129.15 deg), on TWO DIFFERENT proto pids
(`3930042019` at `iconR=985172270` and `3249379867` at `iconR=933939331`; KSP.log
lines 10835 and 10843). The creation-frame trigger hit two proto instances at once.
Four earlier runs (V14T `_2337` / `_0002`, V15T `_1739` / `_1809`) each measured
exactly one, and this entry described that as "one self-correcting frame per run".
**That was a measurement over four runs at two bodies, never a ceiling** - and it is
now falsified. THE PRACTICAL CONSEQUENCE is for the deferred
`{ token = "icon-off-orbit", maxCount = N }` arming that all three specs discuss:
**N would have to be 2, not 1**, and whoever takes that edit must re-read all three
lanes together rather than copying V14T's number.

**(2) THE MAGNITUDE STILL CORRELATES WITH NOTHING THREE POINTS CAN SEPARATE.**
`angleIconVsOrbitEff` now reads 94.05 (Ike) / 26.49 (Gilly) / **129.15** (Laythe)
across SOI radii of 1,049,599 / 126,123 / 3,723,646 m. It is not monotonic in SOI
scale, in parent mu, or in the arrival conic's eccentricity (1.1385 / 1996.24 /
1.2713). Three points, no ordering. Recorded, not modelled.

**EVERYTHING ELSE IS IDENTICAL AGAIN:** ghost-proto CREATION frame, FLIGHT scene,
after ONE large TimeJump onto an epoch just inside a foreign moon's SOI,
`iconR == orbitEffR` on both pids, `lonOrbitEff == lonOrbitLive` with
`angleEffVsLive=0.00` and `loopShift=0.0`. And the control travelled with it:
`V16M-laythe-player-loop` (run `2026-08-19_2114`, PASS) reaches the SAME arrival UT
29,874,214 through a stepped bracket and swept `hits=[] hitCounts={}` - the third
control in three programs.

**AND THE SAME RUN SHOWED THE GAP ON A SECOND LENS**, which is the most useful thing
it produced. See
MAPRENDER-SEAM-LENS-EVALUATES-UNSHIFTED-EPOCH-ON-CREATION-FRAME: on that lane's
creation frame the seam-endpoint lens evaluated the recording's **un-shifted** epoch
(`effUT` = the recorded seam UT, `clock=raw seed=no-seed`) and raised
`seam-endpoint-outside-soi`. Two different lenses, one frame class, the same
"the loop shift has not bound yet" reading - which is what turns a rendering
curiosity into a NAMED family with a candidate mechanism.

**A THIRD LENS AND A NEW TRIGGER SHAPE, measured 2026-08-20 by
`V17M-laythe-vall-player-loop`'s runs 2-3 (`2026-08-20_1859` / `_1908`,
deterministic across both), on the suite's first SELF-OVERLAPPING loop subject**
(the moon-to-moon `vall-transfer-recorded` tree: 20 concurrent instances,
overlapCadence = span/20 = 3,991.03 s). Baseline first: for this nested-SOI
recording (2 crossings) the ProtoOrbitLine producer fail-closes to a verbatim
render in the ROOT frame (`fail-closed-to-faithful ... root=Jool
bodies=Laythe/Jool/Vall`), so a seeded proto's steady-state census body is JOOL
regardless of which leg its instance is replaying. Against that baseline: at the
-180 arrival brackets the census read 17x root-frame `body=Jool` (+ 1x
creation-frame `body=Laythe`), but at BOTH park epochs (28,980,417 / 29,060,238)
ALL 19 live protos printed creation-frame `body=Laythe` and HELD it through a
40-tick census dwell. The discriminating fact between the two epoch classes: the
park jump CROSSES a self-overlap re-arm (instance-20 relaunch at 28,976,670.9;
instance-40 at 29,056,491.4) and the bracket jumps do not. So the trigger here is
not the single-large-jump shape - it is a jump crossing a loop re-arm, after
which every overlap instance's proto reverts from the root frame to the creation
frame, dwell-stable, on a THIRD lens (the orbit-line body itself, not the icon
offset or the seam-endpoint epoch). Self-correcting by the next distant epoch
(the cycle-2 -180 bracket read root-frame Jool again). REPORT-ONLY, same as the
rest of the family; NO product change is proposed.

**AND ONE NON-RECURRENCE ON THE SAME SUBJECT** (`V17T-laythe-vall-ts-arrival`
run 1, `2026-08-20_1917`): the original `icon-off-orbit` raise did NOT fire -
the first silent single-jump run after six raising ones at three parents,
despite a step shape IDENTICAL to V14T/V15T/V16T. The subject differs in two
ways at once (first self-overlapping loop; first nested-SOI recording whose
ProtoOrbitLine fail-closes to a root-frame verbatim render), so WHICH one breaks
the trigger is an open reading - no mechanism claimed.

**THAT OPEN READING IS NARROWED TO ONE CANDIDATE** (`V19T-laythe-jool-ts-arrival`
reading run `2026-08-21_0750`, PASS, `anomalySweep hits=[] counts={}`). V19T was
authored as the DISCRIMINATOR for exactly this question: it is
self-overlapping like V17T but NOT nested-SOI - its subject visits only
{Laythe, Jool}, so `NestedSoiSubtree.FindNestedRoot` needs a non-root parent
with >= 2 visited children, neither Jool nor Sun supplies one, and the
proto-orbit-line lens stays intact rather than fail-closing to the root frame.
It came back SILENT. The family now reads:

| Lane(s) | Runs | Self-overlapping | Nested-SOI | `icon-off-orbit` |
|---|---|---|---|---|
| V14T / V15T / V16T | 6 at 3 parents | NO | NO | RAISED 6/6 |
| V17T | 1 | YES | YES | silent |
| V19T | 1 | YES | NO | silent |

**SO NESTED-SOI IS EXCLUDED** - it is absent on V19T and the raise still did not
happen - **AND SELF-OVERLAP IS THE SURVIVING CANDIDATE**: present in both silent
runs, absent in all six raising ones. **NO MECHANISM IS CLAIMED.** This narrows
a two-candidate reading to one; it does not explain how self-overlap would
suppress the raise, and a THIRD variable nobody has named would defeat the
inference outright. What would settle it is a NON-self-overlapping,
non-nested-SOI TS lane that raises, or a self-overlapping one that raises.

`allowedAnomalies` STAYS EMPTY on V19T and this stays REPORT-ONLY. One silent
run is not a licence to pre-tolerate a token that has raised on six of this
family's nine runs to date, and pre-tolerating it would destroy the very signal
the next discriminating lane needs to read.

---

## SEAM-STARTRECORDING-JOINS-COMMITTED-TREE: a seam `StartRecording` on a vessel that is a committed tree's own launch cannot open a standalone tree - it no-ops onto the recording the committed-restore path re-resumed [MEASURED 2026-08-18 by `B23-ike-orbit` flight 1; WORKAROUND LIVE-PROVEN the same day by flight 2. REPORT-ONLY: a HARNESS/FIXTURE constraint on the automation surface, NOT a proposed product change]

`B23-ike-orbit` exists to produce the suite's first recording whose LAUNCH BODY
is not Kerbin: the DD1 starts already parked in Duna orbit and hops to Ike, so the
loop lanes can read it as a SAME-PARENT transfer. Its flight-1 fixture was
`duna-direct-recorded` - B17's `--keep-parsek` harvest, which carries a COMMITTED
TREE for that very vessel. The run came back **PASS attempt 1 with every
assertion met** and the product was still wrong.

### What happened, from the run's own log

Run `2026-08-18_2242`, collected at
`harness/results/2026-08-18_2242_B23-ike-orbit_shots/KSP.log`:

- **11509** - at LoadGame the load-time optimizer SPLIT the committed main
  recording `311d98e3` at `UT=4653681.9` (`first: 147 pts/2 sections, second:
  363 pts/40 sections`), minting `17c32d96` for the Duna tail. `311d98e3` is left
  as the FIRST half: a 177-second KERBIN pad-ascent fragment (bounds stamped
  `startUT=4653504.3039999772 endUT=4653681.9038905427`, line 11507).
- **12026 / 12097** - the spec's scene-entry preamble did its job:
  `stoprecording stopped=true idle=false`, then `discardtree discarded=true`.
  The promotion stub was killed and its tree torn down.
- **12167** - 11 ms later, `[#8][CommittedSpawnedRestore:post] mode=tree
  tree=ccb5e4af rec=311d98e3 pid=2200110044`: the committed-restore path
  **re-resumed the committed recording**, BETWEEN DiscardTree (01:43:23.119) and
  the seam StartRecording (01:43:23.863).
- **12210** - `startrecording recordingId=311d98e32547491e8dd37aec2526d25d
  already=true`. StartRecording no-opped onto that recording and started nothing.

The whole Duna->Ike hop was therefore appended to a Kerbin-rooted ascent
fragment. At commit the ledger line reads `startUT=4653504.3, endUT=9181612.7`
for `311d98e3` (line 14308) - **one committed recording legally carrying a
~4.5-million-second UT gap** - and the commit terminals (13995-13997) are
`311d98e3=Orbiting/Ike`, `3397c2e5=Destroyed/Kerbin`,
`17c32d96=Orbiting/Duna`, all three still inside B17's tree `ccb5e4af`.

### Why this is the TS-LOADGAME-RECORDING-ACTIVE-RACE family plus one more thing

The re-arm window is the same shape as the scene-entry race the V-lanes'
StopRecording + DiscardTree pair was written for, and the pair is NOT a latch: it
kills ONE stub, and the committed-restore path re-arms behind it. What the
V-lanes never hit is the SECOND half - **committed-tree same-launch-guid
continuation semantics**. The resumed recording belongs to a COMMITTED tree for
the same vessel launch, so `StartRecording` correctly reports the vessel as
already recording; there is no state in which it would instead fork a standalone
tree. No re-ordering of the seam steps closes the window, because the resume
happens before any step can run.

### Why no verifier caught it

Worth stating plainly, because "PASS attempt 1" is what this entry is really
about:

- the recordings **count** would have read a perfectly healthy number - it counts
  `.prec` sidecars and cannot see which tree a recording belongs to;
- the `Recording started` logContract token MATCHED, but all five occurrences
  came from the promotion path and say so on their face (`..., promotion,
  treeRec=rec[311d98e3|...]`, lines 11833 / 12159 / 14655) - the seam started
  nothing;
- nothing in the expectations vocabulary (logContracts, `saveParse`) can express
  "this recording's launch body is Duna".

So a green verdict is not evidence of the contract here, and any future lane that
starts a recording on a parked committed craft inherits the same blind spot.

### Consequence for the loop lanes

A recording rooted at Kerbin with a deep multi-hop chain is exactly what the
re-aim classifier declines, falling through to FAITHFUL - the opposite of the
same-parent phase-lock route B23 exists to produce. The defect is invisible until
a loop lane consumes the fixture, which is why it is filed rather than left in the
spec header alone.

### Workaround, and the scope of what is claimed

`B23-ike-orbit` is re-pointed at **`duna-park-probe`**, a Parsek-stripped derived
copy of the same save: `harvest_bdock_station.py --save-dir
fixtures/saves/duna-direct-recorded --target-name duna-park-probe
--expect-situation ORBITING` WITHOUT `--keep-parsek` (which prunes the `Parsek/`
sidecars), PLUS a manual excision of the residual ParsekScenario children
(`RECORDING_TREE`, `GROUP_HIERARCHY`, `MILESTONE_STATE`). **The second step is not
optional**: the harvest tool prunes sidecars but leaves the scenario node, and the
`RECORDING_TREE`'s `activeRecordingId` is precisely what drove the resume. Same
DD1, same orbit, same epoch, no committed tree, so there is nothing to resume and
`StartRecording` opens the standalone Duna-rooted tree. Guarded headlessly by
`missions/lib/test_b23_ike_orbit.py::SpecArithmeticTests` (the saveTemplate pin,
the byte-level "no `Parsek/` and no scenario children" check, and the count
arithmetic), because a one-string revert would restore the defect with every
verifier still green.

**THE WORKAROUND IS LIVE-PROVEN, and the proof is the same day's flight 2.** Run
`2026-08-18_2308` (PASS attempt 1, mission wall 370.5 s, zero Unity exceptions)
flew the identical spec against `duna-park-probe` and the seam answered

    startrecording recordingId=05ceee33806d4079a1d9d125a1359115 already=false

(KSP.log line 10509 in `harness/results/2026-08-18_2308_B23-ike-orbit_shots`) -
`already=FALSE`, the exact inversion of flight 1's line, and the whole point of
the strip. It minted a FRESH STANDALONE tree
`f55918afd70b45e284006e01729d9e9a`, crossed the boundary exactly once (`SOI change
boundary suppressed in tree mode: Duna to Ike`, 11176), and committed with
`CommitTreeFlight terminal: rec=05ceee33... terminalState=Orbiting
terminalOrbitBody=Ike` (11863). saveParse on the produced save: ONE recording, 200
points, zero supersede / tombstone / rewind rows. That save is now the committed
`ike-orbit-recorded` fixture, and its one-recording topology is pinned in
`harness/lib/test_saveparse.py::CommittedFixtureSweepTests.RECORDED_FIXTURES` -
where a 2 or a 3 would mean this defect is back.

So the entry stands as a CONSTRAINT with a proven workaround, not as an open
problem. **NO PRODUCT CHANGE IS PROPOSED BY THIS LANE.** The observed behaviour is
arguably correct - a committed tree's vessel IS still that recording's subject,
and silently forking a standalone tree on top of it would be its own hazard. What
is recorded here is (a) the automation-surface constraint, so the next spec author
does not spend a flight rediscovering it, (b) the fact that a committed recording
can legally span a multi-million-second gap, which is a property any future
consumer of committed spans should not assume away, and (c) the shape of the fix,
which is a two-step fixture derivation and NOT a spec re-ordering: no arrangement
of the seam steps closes the window, because the re-resume happens before any step
can run.

---
## HARNESS-PRODUCED-SAVE-CLOBBERED-BY-SIBLING-RUN: the machine lock serialises RUNS, not the produced save, so a finished green flight's output is destroyed by the next run that shares its `saveTemplate` leaf [FOUND 2026-08-12 while harvesting `eeloo-orbit-recorded` from `B21-eeloo-orbit`. A HARNESS LIFECYCLE GAP, not a Parsek defect - the lock is doing exactly what it says]

**What happened.** `B21-eeloo-orbit` flew green twice on 2026-08-12. The FIRST green
run, `2026-08-12_2003` (PASS attempt 1, wall 3,083 s, ~51 minutes of real flight), had
its produced save destroyed before it could be harvested. The harvest refused with

```
situation gate failed: active vessel 'Duna Rocket' is PRELAUNCH, expected one of ORBITING
```

which reads exactly like a mission that did not reach orbit - and the mission JSON for
that run says `MISSION-OK` with all six assertions met and a park at Eeloo. A second
symptom named the real cause: repeated greps of the same `persistent.sfs` returned
INCONSISTENT results, because a sibling's live `KSP_x64.exe` was rewriting the file
underneath the reads.

**The mechanism, and it is one line.** The produced-save directory is the
`saveTemplate` LEAF, not the scenario id: `hlib` derives `run_save_name = _leaf_of(
save_template)` (`harness/lib/hlib.py:2792`, with `_leaf_of` at `:2648`) and its own
comment says "The saveTemplate leaf IS runSaveName, staged as a directory the shell
rmtree's + copytree's". The shell then does exactly that at the top of staging:

```
harness/run.py:905    if os.path.isdir(target_save):
harness/run.py:906        shutil.rmtree(target_save, ignore_errors=True)
```

So EVERY scenario sharing a `saveTemplate` leaf shares ONE produced-save directory in
the instance, `<instance>/saves/<leaf>`, and each new run deletes the previous
occupant's output as its first destructive act. `<umbrella>/automation/.ksp-machine.lock`
does not help: it serialises RUNS so two KSP processes never overlap, and it is released
when a run ENDS. The produced save's useful lifetime begins exactly then.

**Verified, not inferred.** Four specs share the `b18-dres-pad` leaf - `B18-dres-lko-
ascent`, `B19-dres-orbit`, `B21-eeloo-orbit` (all in this worktree) and
`B20-moho-orbit.toml:148` in the sibling worktree `Parsek-moho-lane`. The lockfile
caught the collision in the act: after B21's second green run `_2239` ended at
`2026-08-12T23:30:45Z`, `.ksp-machine.lock` read
`{"worktree": "...\\Parsek-moho-lane", "selection": "--id B20-moho-orbit",
"startedIso": "2026-08-12T23:30:54Z"}` - NINE SECONDS later. The instance's
`saves/b18-dres-pad/persistent.sfs` now holds a single RECORDING_TREE whose every
`endpointBodyName` reads `Kerbin`; B21's Eeloo payload is gone from the instance
entirely, and survives only because it was snapshotted first. The `_2003` clobber was
the same event about 53 s after that run ended; its lockfile evidence has since been
overwritten, so that interval is reported rather than re-verified, while the mechanism
and the `_2239` timing above are both measured.

**Why it is worse than it looks.** The failure is SILENT and MISATTRIBUTED. Nothing
warns; the harvest simply describes a pad-bound vessel, and the natural reading is that
the flight failed. It cost one wrong diagnosis here before the lockfile was read. It is
also RECIPROCAL - whoever flies second destroys the first one's harvest source - so on a
machine with 28 sibling worktrees the hazard is a function of neighbours, not of
anything the flying spec did. And the cost unit is a whole ~51-minute flight.

**Mitigation used, and worth recommending.** Chain the harvest into the SAME command as
the run, snapshot the produced-save directory the moment the run returns, and harvest
from the snapshot:

```
python run.py --id <SPEC> && cp -r <instance>/saves/<leaf> <snapshot> \
  && python tools/harvest_bdock_station.py --save-dir <snapshot> --target-name <fixture> ...
```

`harvest_bdock_station.py --save-dir` already accepts an arbitrary directory, so no tool
change is needed for the workaround.

**Fixes worth considering, none taken here.** (a) Make the produced-save directory
per-RUN (`<leaf>__<runId>`) and have staging reap only its own; that removes the
collision outright but touches every consumer that resolves a produced save by leaf.
(b) Keep the leaf but have staging MOVE rather than delete an existing occupant, into
`<leaf> (superseded <runId>)`, so the bytes survive one generation. (c) Cheapest and
smallest: have staging log a WARN naming the runId whose output it is about to delete,
so the destruction is at least visible in the harness log. Even (c) would have turned
this from a wrong diagnosis into a one-line read.

---

## FIXTURE-AUDIT-DEFERRED: measured redundancy deliberately NOT acted on [OPEN, filed 2026-08-12 from the same audit. Each entry is a decision someone should make with the numbers in hand, not a defect]

Recorded so the measurements are not lost and nobody re-derives them.

- **`.prec.txt` trajectory mirrors, 215,571 lines / 50 files.** An audit pass
  recommended deleting these as derived. **DO NOT** without repointing four C#
  read sites first: `OptimizerTransferCohesionTests.cs:61,91,530` (the last globs
  every fixture's `*.prec.txt` recursively) and `ReaimTransferSynthesizerTests.cs:639`
  plus its hand-rolled `ReadOrbitSegments`. They are a live test input, not a
  review surface. The binary `.prec` is already proven headless in the same rig,
  so the switch is feasible - it needs one `dotnet test` run to validate, which
  was not available in the environment that found this.
- **Kerbal `INVENTORY` blocks - 115 nodes at HEAD (158 before this branch's `.sfs`
  deletions), ~25 distinct shapes.**
  88% of all ROSTER bytes. Load-bearing for the EVA lane (`EVA-4-atmo-chute`
  drives `EvaChuteDeploy` against this jetpack). Do not strip; recorded as the
  honest diagnosis of why `.sfs` mass is what it is.
- **Asteroid `SpaceObject` VESSEL nodes - at HEAD, 2,424 lines / 20 nodes across 20
  of 25 `.sfs`** (3,637 across 30 of 35 before this branch deleted 10 `.sfs`; the
  HEAD figures are the ones to act on). A few distinct asteroids, replicated, and
  every fixture's `DiscoverableObjects` SCENARIO node is empty, so nothing
  registered them.
  `fixtures/saves/README.md` already names this hazard for `career-pad-craft`
  (edit 1: an unregistered asteroid is "a free variable in any consumer's
  `expectations.recordings.count`") - one fixture was cleaned and the rule was
  never generalized. Removing them MOVES recording counts, so each needs its
  consumers' windows re-read against a live run.
- **`bdock-forge-base` now has zero unique bytes.** Its `persistent.sfs` and
  `.loadmeta` hash-match `gloops-airshow` exactly; its craft now come from
  `shared-ships.toml`, which WAS the whole difference. Do not simply repoint the 7
  FORGE specs at `gloops-airshow`: the distinct run-save NAME is load-bearing
  (`stage_fixture` rmtree's the target save dir, so sharing a name would collide
  forge runs with the 41 gloops specs). A save-level alias in the spirit of
  `shared-ships.toml` is the clean shape.
- **`AddOns/DistantObject/Settings.cfg`, 22 byte-identical copies / 735 lines.**
  DistantObject is not in `stock-minimal`, the profile 105 of 107 specs use. Real
  but small; 21 file touches for 735 lines is poor value against the risk.
- ~~**`sidecar-pcrf` is false coverage.**~~ **DONE 2026-08-12** (branch
  `fixture-audit-followups`): the D16 value is removed from the registry and from
  the two specs that claimed it (`H15-corpus-ghost-visuals`,
  `S1.4-injected-playback`), with `H25-serialization`'s "other three sidecar
  cells" comment corrected to two and both `autotest-status.md` rows repointed.
  Registry and specs had to move together - `validate_spec` errors on a claim the
  registry does not carry - and all 107 specs still validate. Original finding:
  zero `.pcrf` files exist anywhere
  (`git ls-files | grep -ci pcrf` -> 0), yet `harness/coverage/registry.toml:268`
  still lists the D16 value and two committed specs claim it
  (`H15-corpus-ghost-visuals.toml:44`, `S1.4-injected-playback.toml:33`). A
  correctness fix in the coverage ledger, not redundancy - left for its own change.
- ~~**`AGENTS.md:91` is stale**~~ **DONE 2026-08-12** (branch
  `fixture-audit-followups`), and it was not only line 91. Fixing that line
  collided with line 72, which already owned the schema contract and stated it
  WRONG, so the pass was widened and every claim re-checked against the source
  rather than against `.claude/CLAUDE.md` (a second stale copy is how this drift
  happens). Four fixes: line 91 now reads "Recording storage (sidecar layout)" and
  marks `.pcrf` legacy without restating the contract; line 72's
  `CurrentRecordingSchemaGeneration` goes 3 -> 4 (`RecordingStore.cs:131`); and
  lines 74-75's `DebrisParentRecordingId` becomes `ParentAnchorRecordingId`
  (`Recording.cs:33`), dropping the parenthetical that still called that rename
  future work.

## REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS: the re-aim classifier needs the whole transfer inside ONE member, so any legitimate split silently produces FAITHFUL with a misleading reason [FILED 2026-08-12 as the defense-in-depth follow-up to OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER (open question 2 of docs/dev/plans/optimizer-split-transfer-cohesion.md, recommendation accepted). NOT a regression - the cohesion fix removed the only measured instance; this is the class of failure it does not cure]

**The residual.** `ReaimClassifier.Classify` requires parking orbit + heliocentric
coast + direct-child arrival among the segments of a SINGLE loop-unit member. The
cohesion fix stopped the optimizer splitting an on-rails SOI handoff, which was the
only shape in the committed corpus that broke that requirement. It did not make the
requirement itself robust, and one split shape is DELIBERATELY preserved: a genuine
physics-frame burn straddling an SOI crossing still splits (the calibration row "SOI
traversal while burning -> split"). A mission flown that way would reproduce the
exact V9 symptom -- `no member yields a re-aim transfer`, a FAITHFUL replay, and a
reason string that blames a missing heliocentric leg the recording actually contains.

**Why it is not loud.** ~~Nothing distinguishes "this recording has no transfer" from
"this recording's transfer is spread across two members". Both emit the same
decline.~~ **LANDED 2026-08-14, branch `reaim-loud-decline`** -- see "the interim, as
shipped" below. That is what made the original defect cost a full reading run to find.
THE ENTRY STAYS OPEN: the interim only makes the failure diagnosable; Design C is
still the structural cure and is NOT implemented.

**The direction, and why it is viable** (Design C of the plan, rejected for that
branch only to keep V9's re-measure attributable to one change). The topology marker
already exists and is sound: `CopySplitIdentityFields`
(`Source/Parsek/RecordingStore.Optimization.cs`) gives split halves a shared
`ChainId`, the same `TreeId` / `RecordedVesselGuid`, chain re-indexed by StartUT, and
no branch point. `ApplyReaim` (`Source/Parsek/MissionLoopUnitBuilder.cs`) could
classify per CHAIN GROUP -- same ChainId + guid, UT-ordered concatenation -- instead
of per member. The playtest interleaving bug that forced per-member classification
would not recur, because a chain group is one vessel's non-overlapping time slices.

**What to watch out for.** Downstream carries single-transfer-member assumptions
(`transferMemberIndex` / `transferMemberRecordingId`, descent gating "EXACTLY on this
member", per-member heliocentric substitution in `ReaimPlaybackResolver`); a plan
spanning two members would strain them. That is the actual work, and it is why this
is a separate entry rather than a follow-up commit.

**Cheaper interim option worth considering first:** ~~make the failure LOUD rather than
robust -- when a decline's reason is "no heliocentric leg" but a sibling member in the
same chain group HAS one, say so in the reason string.~~ **DONE 2026-08-14** (branch
`reaim-loud-decline`). That converts a silent misclassification into a diagnosable one
for a fraction of the cost.

**The interim, as shipped.** `Source/Parsek/Reaim/ReaimSplitSiblingDiag.cs` -- pure,
diagnostic-only, no classification outcome moves. `ApplyReaim` now classifies every
member first and logs in a second pass (sibling awareness is a statement about the
OTHER members, so it cannot be written mid-loop), then appends a clause to the
`[ReaimDiag] member#N` line and to the unit-level decline line. A member is annotated
only when it declined with `ReaimClassifier.MissingHeliocentricLegReason` (hoisted to
a constant so the predicate cannot drift from the emitter), carries a `ChainId`, sits
in a >=2-member chain group where NO member classified Supported, and **the group's
UNION of segments classifies Supported** -- the real `ReaimClassifier.Classify`, run
over the members concatenated in UT order the way Design C would read them. Both sides
of a cut are annotated, from their own side: the parking half names the sibling that
holds the common-ancestor leg, and the ancestor-started half names the sibling that
holds the launch-body legs. Grep token `split-sibling-transfer`; every clause carries
this entry's id, and each leads with the union verdict (`classify Supported as
Kerbin->Duna via 'Sun'`) -- the measured proof behind the claim.

**Why the union classify is the gate, and not "a sibling records a strict ancestor".**
That weaker predicate was the first implementation, and it FALSELY annotates two
reachable shapes (both caught in review before merge, both now pinned as
must-not-annotate cells). (a) `[Kerbin parking] + [Sun coast, no arrival]` -- a probe
ejected to solar orbit, or a recording ending mid-coast: joined it still declines `no
target arrival leg after the heliocentric coast`, so there is no transfer to announce.
(b) `[Mun orbit] + [Kerbin orbit]` -- a Mun return cut at the SOI exit, i.e. the
deliberately preserved burn-split calibration row: Kerbin IS a strict ancestor of Mun,
so the weak predicate announced a "'Kerbin'-legged transfer" that is not an
interplanetary transfer at all. Running the real classifier over the union makes "no
SINGLE member carries this whole" literally true. A second review finding fixed with
it: the carrier-side clause must not assert that the common-ancestor body has no parent
-- true only when the ancestor is the Sun, false for a Mun->Kerbin->Minmus group whose
ancestor is Kerbin. It now states the fact the classifier actually acted on (this
member recorded nothing at a strict ancestor of its OWN earliest body).

**The lane trap this had to dodge, recorded because it nearly cost three guards.**
`V9-dres-player-loop`, `V11-moho-player-loop` and `V12-eeloo-player-loop` forbid the
literal `not re-aim \(no member yields a re-aim transfer\); faithful`, and `V10` /
`V11A` forbid the bare `no member yields a re-aim transfer` -- their ENGAGED regression
floors. Rewriting that text would have left all five lanes green while their guard
silently stopped matching. The clause is therefore APPENDED AFTER `faithful` (the
patterns are applied with `re.search`, so a suffix keeps every one matching) and
`plan.Reason` is untouched. No spec file changed. Guarded in-repo by
`ReaimSplitSiblingDiagTests.TheAggregateDeclineLineAnnotatesTheSplitAndKeepsTheCommittedLaneSubstrings`.

**What is still open.** Design C (per-chain-group classification), the product-visible
member seam, and every downstream single-transfer-member assumption listed above.
Also note the interim's deliberate narrowness: only the missing-heliocentric-leg
decline class is annotated. Other split shapes can decline for other reasons (a first
half with parking + coast but no arrival would emit `no target arrival leg after the
heliocentric coast`), and those stay unannotated -- widening the predicate without a
measured instance would be guessing.

## RECORDER-LABELS-ON-RAILS-CHECKPOINTS-EXOPROPULSIVE: a packed vessel cannot thrust, but the recorder still stamps some on-rails checkpoint re-emissions ExoPropulsive [FILED 2026-08-12 as the hygiene follow-up to OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER (open question 3, recommendation accepted: file it, do not act on it yet). LOW PRIORITY - the consumer that was misled has been fixed]

**The observation.** On `dres-orbit-recorded`, track section 28 is
`env=ExoPropulsive ref=OrbitalCheckpoint`, spans 25,921 s, and its single
ORBIT_SEGMENT payload is byte-identical to its ExoBallistic predecessor's -- the same
Kerbin escape hyperbola (ecc 2.3601122370442775, sma -1,007,185.2465716415). No burn
altered the conic across that boundary. A stock vessel cannot run an engine while
packed, so the ExoPropulsive label there is recorder bookkeeping, not gameplay.

**Why it is filed rather than fixed.** Two reasons, both from the plan's Design E
analysis. (1) It does nothing for already-recorded saves and fixtures, and the
regression floor for this whole area IS a recorded fixture -- so the consumer-side fix
was the one that mattered and it has landed. (2) The env label may be load-bearing
elsewhere; changing what the recorder emits is a wider blast radius than changing how
one predicate reads it, for no measured benefit today.

**What would make it worth doing.** A second consumer being misled by the same label.
If that happens, the fix is at
`FlightRecorder`'s checkpoint re-emission path, and the test is that a packed section
never carries a propulsive env.

## KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY: the recovery correlator matches by vessel NAME plus a UT tier, and the XP row makes a wrong pick irreversible [OPEN - STAGE 1 SHIPPED headless 2026-08-28 (branch `kerbal-xp-guid-filter`), STAGE 2 OUTSTANDING and gated on live proof; filed 2026-08-20 with the correlation fix above. **THE REPRO SHAPE IS NOW AUTHORED: `harness/scenarios/L6-career-same-name-recover.toml` (2026-09-02, never flown)** - `science_bench_recover` flown a second time over `career-earned-pad`, whose pad craft already flew and was recovered once and left TWO chained same-name recordings under a DIFFERENT launch guid than the spliced pad vessel carries; the XP leg's pick (which fires after the scene-exit auto-commit, so this flight's own recordings are committed beside them) should therefore read `nameMatches>=3 guidDropped=2 survivors>=1`, i.e. stage 1 resolving the same-name case live, with the tier walk running over this flight's own survivors only; it is the lane stage 2 should be live-proven on]

`LedgerOrchestrator.PickRecoveryRecordingId` matches candidate recordings by vessel NAME
(`RecoveredVesselIdentity.MatchesName`, raw or localized) and then ranks them by a UT
tier - bracketing, else most-recent-ended, else global-latest. It never consults
`Vessel.id` / `Recording.RecordedVesselGuid` or `persistentId`. Two launches of the same
craft name therefore differ only by their UT ordering, and the tier the driven career
recovery actually lands on is `most-recent-ended` - the weakest of the three.

**This is PRE-EXISTING** - the recovery funds and science legs have always resolved this
way - and it is NOT introduced by the XP correlation. What the XP row changes is the
CONSEQUENCE of a wrong pick. Funds and science rows are re-derived idempotently from the
effective ledger on every recalc, so a mis-scoped one is wrong but revisable. A
`KerbalExperience` row feeds `KerbalsModule.ReassertCareerLogEntries`, whose facade
exposes `AppendCareerLogEntries` with NO remove counterpart: once a mis-scoped row's
entries are appended to a roster, nothing walks them back except a tombstone on the row
that put them there - and a row scoped to the WRONG recording is tombstoned by the wrong
merge.

**Fix:** tighten the pick to guid-positive identity where a guid is available -
`VesselLaunchIdentity.RecordingsShareLaunch` semantics, or the stricter
`ResurrectionRetirementEligibility.IsPositivelySameLaunch` shape (pid equal AND both guids
known AND equal) - falling back to the current name+tier walk only when no guid is
recorded. Not attempted alongside the correlation fix on purpose: changing the correlator
changes the funds and science legs too, so it is its own change with its own live proof,
and doing it inside a fix whose whole argument is "use the SAME correlator the funds leg
uses" would have made both claims unfalsifiable at once.

**Not currently observable in a driven run:** every committed career fixture flies one
launch of one craft name, so the tiers are never in competition. A repro needs two
launches of the same craft name with a recovery of the second - which is also the shape
the eventual fix should be live-proven on.

### RECOMMENDATION (written 2026-08-28 on branch `ledger-hygiene-2`; NO code change made)

Reviewed as an explicitly out-of-scope question during that branch's wave, because the
remedy is a design choice and the wrong pick is irreversible. The question posed was
"guid corroboration or refusal?". **The answer is BOTH, in that order, applied to
DIFFERENT scopes** - and the reason they are not alternatives is that they fix two
different halves of the defect.

**1. Guid corroboration as a FILTER, on all three legs (funds, science, XP).** Drop from
the candidate set any name-matching recording whose `RecordedVesselGuid` CONCLUSIVELY
DIFFERS from the recovering vessel's live `Vessel.id` -
`VesselLaunchIdentity.GuidsConclusivelyDiffer` semantics, where an unknown guid on either
side is NOT conclusive and the candidate survives. Two properties make this the safe half:
it is MONOTONE (it can only remove candidates, never add or reorder one, so a pick that was
already correct cannot become wrong), and it DEGRADES to today's behavior exactly when a
guid is missing, so no legacy recording loses its correlation. Do NOT reach for the
stricter `ResurrectionRetirementEligibility.IsPositivelySameLaunch` shape (pid equal AND
both guids known AND equal) as the primary rule here: it requires a POSITIVE guid on both
sides, which the recovery seam cannot promise, and it would silently retire the correlation
for every pre-guid recording - trading a rare wrong pick for a common missing one.

**2. Refusal as the tie-breaker of last resort, on the XP leg ONLY.** When the filtered
candidate set still holds more than one recording and the winner is decided by a WEAK tier
(`most-recent-ended` or `global-latest` rather than bracketing), refuse the XP write with a
named, counted reason - `reason=ambiguous-recovery-recording`, mirroring the existing
`reason=no-recovery-recording` fail-safe. Do NOT extend that refusal to funds or science.
The asymmetry is the whole argument of this entry: a funds or science row is re-derived
idempotently from the effective ledger on every recalc, so a mis-scoped one is WRONG BUT
REVISABLE, and refusing it would drop a real payout to buy safety that leg does not need.
An XP row feeds `KerbalsModule.ReassertCareerLogEntries`, whose facade appends with no
remove counterpart, so a mis-scoped one is wrong AND UNREACHABLE except through a tombstone
on the row - written by the wrong merge. Where the consequence is irreversible, a missing
row (the pre-P9a behavior, already accepted as a cost in the entry above) strictly
dominates a wrong one.

**What NOT to do.** Do not make the XP leg refuse whenever the winning tier is weak WITHOUT
the guid filter first - `most-recent-ended` is the tier the driven career actually lands on
even in the single-launch case, so a bare tier-strength refusal would refuse the very
recoveries the correlation fix was written to capture, and `L4`'s `KerbalXp` facet would go
vacuous again. The filter is what turns "weak tier" into "weak tier AND genuinely
ambiguous". And do not fold the two into one predicate: the filter is a correlator change
that must be live-proven across all three legs, while the refusal is an XP-leg policy that
can only be observed on the two-launches-same-name shape. Landing them as one change makes
neither claim falsifiable, which is the same trap the correlation fix avoided by declining
to touch the picker at all.

**Live-proof shape, unchanged:** two launches of the same craft name, recovery of the
second. That single flight exercises both halves - the filter must make the first launch
drop out of the candidate set, and the refusal must NOT fire once it has (a spurious
`ambiguous-recovery-recording` on that run would be the fix over-firing).

### STAGE 1 SHIPPED, headless only (2026-08-28, branch `kerbal-xp-guid-filter`)

The guid-corroboration FILTER is implemented exactly as recommendation half 1 describes,
on all three legs, and is green headless. **Stage 2 (the XP-leg
`ambiguous-recovery-recording` refusal) is NOT implemented** and must not be landed before
the filter has flown - the recommendation's own argument for splitting them stands.

**What was built.** `LedgerOrchestrator.PickRecoveryRecordingId` now runs in two passes: it
gathers the name+eligibility candidate set (the zombie / session-provisional rules
untouched), passes it through `FilterRecoveryCandidatesByLaunchGuid`, and only then walks
the bracketing / most-recent-ended / global-latest tiers. The predicate is
`IsConclusiveLaunchGuidMismatch` -> `VesselLaunchIdentity.GuidsConclusivelyDiffer`, so an
unknown guid on EITHER side is not conclusive and the candidate survives. The filter is a
subsequence operation (survivors in input order, nothing added or reordered), and emptying
the set routes into the picker's PRE-EXISTING null result and hence the XP leg's existing
`reason=no-recovery-recording` refusal - a new road to an old fail-safe, not a new one.

**How the live guid reaches it.** `RecoveredVesselIdentity` gained a `LaunchGuid` field
(normalized "N", null = unknown), stamped from `VesselLaunchIdentity.ReadLaunchGuid(pv)` at
the three seams that hold a `ProtoVessel`: `ParsekScenario.OnVesselRecovered` (funds,
including the DEFERRED pairing queue, which stores the struct verbatim),
`GameStateRecorder.OnScienceReceived` (science, via a new optional `launchGuid` argument on
`TryRecordKscScienceSubject`), and
`GameStateRecorder.OnVesselRecoveryProcessingForExperience` (XP). The guid is deliberately
NOT part of `Matches` / `MatchesName` / `FormatForLog` - the event-pairing predicates and
the harness-pinned log string are unchanged.

**Two claims in the recommendation that source-reading corrected.**

1. *"the filter must live-prove across all three legs"* is right, but the legs do not need
   three seams' worth of new plumbing: the funds leg's deferred queue and the XP leg both
   already thread `RecoveredVesselIdentity`, so ONE field carries the guid to two of the
   three. Only the science leg took a signature change, because it passes a bare
   `vesselName` string.
2. *The Re-Fly provisional needed no exemption*, and the reason is worth pinning: the
   recommendation did not mention it, but a filter that dropped the active session's
   provisional would have re-opened the tombstone hazard the session-aware `NotCommitted`
   rule exists to close. It cannot.
   `RewindInvoker.BuildProvisionalRecording` leaves `RecordedVesselGuid` null, and
   `CopyInheritedIdentityForFork` later inherits the ORIGIN's guid (the fork restores from a
   quicksave that preserves the origin's `Vessel.id`). Both states are non-conclusive.
   Pinned by `Filter_ReFlyProvisionalShapeSurvives`.

**A THIRD REACHABLE SHAPE CHANGED, and the change is intentional.** Real Spawn Control
copies are affected, not only relaunches: `VesselSpawner.RegenerateVesselIdentity` writes a
FRESH vessel guid into the spawn node (it must, to avoid pid/guid collisions with the
original), so a spawned, never-recorded copy of a recorded craft carries a launch guid that
conclusively differs from the source recording's. Recovering that copy now EMPTIES the
candidate set: the funds row lands untagged where it previously carried the source
recording's id, and a crewed copy's XP write refuses with `reason=no-recovery-recording`
where it previously wrote a row. That is the more defensible behavior - recovering a COPY
should not tie career rows to the original mission's recording, and an XP row scoped to a
flight the copy never flew is precisely the irreversible mis-scope this entry is about - so
it is kept, but it is a behavior change and is named here rather than discovered later. **It
is NOT exercised by the planned L3-sibling live proof**, which flies two real launches;
proving the spawned-copy shape would need its own driven run and is not owed before stage 2.

**Headless cover:** `RecoveryPickLaunchGuidFilterTests` (16 cells) - the predicate's
both-sides-known requirement and its format-insensitivity; the MONOTONE property
(survivors are an order-preserving subset, dropped + survivors == input); the unknown-guid
no-op in both directions; the session provisional resolving from the POST-filter set (a
tie-break reading the pre-filter set could reinstate a dropped candidate and break
monotonicity); the two-launches-same-name shape WITH its negative control (the same fixture
makes the WRONG pick when no live guid is supplied, so the cell reproduces the defect before
fixing it); filter-runs-before-tier-selection; the empty-set refusal; the legacy
no-recorded-guid non-regression; the single-launch non-regression (the shape every committed
career fixture flies); and the XP leg end to end.

**Observability for the eventual flight.** One bounded line per PICK - not per recovery:
`PickRecoveryRecordingId guid filter: vessel='X' ut=<t> dropped=N remaining=M reason=<r>`,
Info at `reason=guid-conclusive-mismatch` and Verbose at `reason=no-conclusive-mismatch` /
`reason=live-launch-guid-unknown`. The three reasons are distinct on purpose: a silent
no-drop cannot be told from a filter that never ran, and a live proof has to read "active
and agreed" off the log rather than infer it. The funds and XP legs pick once, but the
SCIENCE leg picks once per science subject, so a science-heavy recovery emits one line per
subject - bounded by the subject count, each line independently true, and deliberately not
rate-limited because a dropped candidate is an attribution-changing decision on every leg
that makes it. The existing `PickRecoveryRecordingId:` summary line gains `guidDropped=`
and replaces the now-ambiguous `candidates=` token with explicit `nameMatches=` (pre-filter)
and `survivors=` (post-filter); nothing pinned the old token.

### THE LIVE-PROOF GATE (stage 2 is blocked on this)

**Which lane.** `L3-career-science-recover` is the only committed spec that drives a real
crewed recovery with the funds, science AND XP legs all firing on one flight - it is
already the subject the XP correlation was proven on, and its produced save is what
`L4-ledger-groundtruth-strict` consumes. The proof should be a SIBLING of it (a second
launch appended), not a from-scratch lane; note that re-harvesting its produced save into
`C2CareerPostFix` drags `career-earned-pad` and the `test_career_earned_pad.py` drift cell
along, so prefer a sibling spec with its own fixture unless the harvest is wanted anyway.
No committed lane flies two launches of one craft name today, which is exactly why the
entry says the defect is "not currently observable in a driven run".

**Two proofs, and they are not the same run.**

- *Minimum viable (proves the filter fires and is correct).* Launch the pad craft, hop,
  land, recover; launch the SAME craft again, hop, land, recover. At the second recovery
  the first launch's recording name-matches with a conclusively different `Vessel.id`, so
  each of the three legs must log `dropped=1 remaining=1
  reason=guid-conclusive-mismatch`. **The PICK is unchanged on this run** - the second
  recording brackets the second recovery, so tier 1 already resolved it correctly without
  the filter. That is a feature of the shape, not a weak proof: it demonstrates the filter
  active, dropping exactly the right candidate, and NOT disturbing a correct pick. It is
  also the run that must show no leg losing its correlation.
- *Full defect repro (produces a pre-filter WRONG pick).* The earlier launch must still
  SPAN the second recovery's UT - launch #1 to orbit and leave it there, then launch #2,
  hop, recover. Without the filter the orbiting sibling wins tier 1 (bracketing, largest
  EndUT) and the XP row is scoped to a flight that is still flying. This is the shape the
  headless `Picker_TwoLaunchesSameName_...` cell models, and it is the one stage 2's
  ambiguity predicate will need to reason about.

**What stage 2 may not do until then.** Land the refusal only after the minimum-viable run
shows the filter turning "weak tier" into "weak tier AND genuinely ambiguous". A bare
tier-strength refusal without a flown filter would refuse the very recoveries the
correlation fix captured, and `L4`'s `KerbalXp` facet would go vacuous again - the failure
mode the recommendation's "What NOT to do" paragraph names.

## ~~ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH: a green two-vessel docking flight cannot produce a route-candidate tree, and no seam verb can seal one~~ [FOUND 2026-08-11 while wiring `H35-logistics-route-proof`. A CAPABILITY GAP in the automation surface, not a product defect - the seal policy itself is correct. **CLOSED 2026-08-30 by fix road (1)**: `SealSlot` and `RouteCommand` are both promoted out of `ReservedVerbs` and implemented against the production paths - see the closure note at the end of this entry]

**What was measured.** The `bdock-recorded` fixture is the produced save of a FULLY
GREEN `BDOCK-1-station-interceptor` flight (run `2026-08-11_1606`, PASS attempt 1,
wall 2,146 s) - the harness's most complete two-vessel mission: ascent, mid-mission
commit, second launch, rendezvous, hard dock, LF/MP transfers both ways, undock,
terminal. Parsed over the committed bytes with `saveparse`, it carries 2 committed
trees / 19 recordings, and THREE of those recordings are
`MergeState.CommittedProvisional` - `b07cfd6c` (tree `788554a9`), and `500c0ba9` +
`4af6cfd7` in tree `8c677bba`, which is the tree that owns the save's single
`ROUTE_CONNECTION_WINDOWS` node (on the docked-state recording `f049901e`). So the
ROUTE-OWNING tree has two open provisionals.

**Why that blocks candidacy.** `RouteCandidateFinder.IsTreeFullySealed`
(`Source/Parsek/Logistics/RouteCandidateFinder.cs:83-96`) returns false unless EVERY
non-null recording in the tree is `MergeState.Immutable`:

```
88:            foreach (Recording rec in tree.Recordings.Values)
92:                if (rec.MergeState != MergeState.Immutable)
93:                    return false;
```

and `DeriveCandidates` (`:157-196`) gates on it SECOND, before eligibility analysis
and before the already-promoted check. The policy is right and should not be
loosened: an open `CommittedProvisional` is a re-flyable Unfinished Flight, and a
route built from one would flip to `RouteStatus.SourceChanged` the moment it was
re-flown (the doc comment at `:65-71` says exactly this). The problem is not the
gate; it is that a flight-class terminal - which is what an interceptor profile ends
on - leaves provisionals behind BY DESIGN, so **no automated mission produces a
sealed tree**. Sealing is a player action.

**And the seam could not perform it (until 2026-08-30).** `SealSlot` was a RESERVED
verb: `TestCommandVerbs.ReservedVerbs` listed it and `TestCommandDispatcher` answered
`Reject("not-implemented-v1")` for the whole reserved class. The only other "Seal" on
the seam surface is `MergeAnswerChoice.Seal`, which ANSWERS an already-open merge dialog
- it cannot seal a tree on demand. See the closure note below.

**Scope, stated honestly.** What is blocked is end-to-end route CREATION under
automation: candidate detection -> promotion -> a live route driven by a REAL
recorded tree. What is NOT blocked, and is now wired, is the PROOF surface those
routes are built from - `H35-logistics-route-proof` walks the recorded
`ROUTE_CONNECTION_WINDOWS` window and the `ROUTE_ORIGIN_PROOF` field with the
in-game route-proof cells and pins two of them passing. The existing route-behavior
coverage (H6's route-rewind timeline, H34's inter-body firing gate, the M3-M5 xUnit
stack) all drives SYNTHETIC routes built in memory, which is what this gap keeps it
doing.

**Fix (two roads were open):** (1) promote `SealSlot` out of `ReservedVerbs` and
implement it against the same code path the UI's Seal action uses, which makes the whole
candidate -> route pipeline drivable; or (2) add an in-game seal API a `[InGameTest]` can
call, and wire a Logistics cell that seals a committed tree, derives candidates and
asserts the promotion - cheaper, but it proves the pipeline rather than the player
workflow.

**CLOSED 2026-08-30 by road (1), and the SECOND verb went with it.** `SealSlot` and
`RouteCommand` are both promoted out of `ReservedVerbs` (28 -> 30 implemented, 7 -> 5
reserved) and implemented as thin appliers over the production paths:

- `SealSlot tree=<treeId>` seals every open member of a committed tree through
  `UnfinishedFlightSealHandler.TrySeal` - the exact call the Unfinished Flights per-row
  Seal button makes, so the tip flip, the `FilesDirty` mark, the
  `BumpSupersedeStateVersionLive()` (ERS-cache invalidation AND
  `RouteStore.RevalidateSources`), the persist and the RP reap all happen the way they do
  for a player. The D9 `rp=` + `slot=` spelling is kept beside it. IDEMPOTENT: an
  already-sealed tree answers `OK sealed=0 alreadySealed=true`.
- `RouteCommand action=create|send-once|pause|activate` creates through a new shared
  `RouteCreationService.CreatePausedFromCandidate` funnel - the build + store +
  manual-loop-clear sequence LIFTED out of `LogisticsWindowUI.CreateRouteFromCandidate`
  (a private instance method on a UI window, which is why no seam could reach it), with
  the window now calling the same funnel so the two cannot drift - and operates through
  `RouteOrchestrator.TrySendOneCycleNow` / `TryPause` / `TryActivate`.

Contracts, arg grammars and the full typed-error taxonomies:
`docs/dev/design-autotest-command-seam.md` -> "Update (the logistics verbs)" plus the
`#### SealSlot` / `#### RouteCommand` sections. What is NOT yet true, and must not be
read into this closure: no lane has FLOWN the seal -> create sequence yet, so
"routes are automated end to end" becomes true when a driven run proves it, not when
these verbs compile. `StashSlot` and `FlySlot` stay reserved on purpose - `FlySlot`'s
mechanism is already driveable as `InvokeRewind`, and nothing needs `StashSlot`'s
slot-OPEN direction.

**D10 coverage, and why H35 claims NONE of it.** The obvious temptation on a spec
whose subject is route proofs is to claim a D10 row. H35 claims none, deliberately.
Its two passing cells are READ-SIDE walkers over surfaces `BDOCK-1`'s own flight
produces and already claims (`dock-producer`, `ksc-origin`), and nothing in an H35
run drives a production route emitter at all - `Route proof dock window captured:`
(`ParsekFlight.cs:6246`) and `Route window delta:` (`RouteProofCapture.cs:728`) fire
during RECORDING, and the reading run's log contains ZERO occurrences of either.
Claiming a row off a token that is not in the log is the exact CLAIM-IS-NOT-GATE
failure the registry discipline exists to prevent. None of the ten still
zero-declarer D10 rows is evidenced either: `docked-depot-origin`, `claw-producer`,
`inventory-cargo`, `harvest-provenance`, `multi-stop`, `multi-origin-escrow`,
`round-trip-pair`, `hold-reasons`, `destination-full-gate`, ~~`route-map-lines`~~
(`harness/coverage/registry.toml:99-104`). `route-map-lines` LEFT THAT LIST on
2026-08-26: `V18T-depot-route-ts-arrival` declares it off an ARMED
`routeLineBuilds { min = 1 }` whose red is demonstrated by its own negative
control - a gate first, then the claim, which is the discipline rather than an
exception to it. FOUR MORE LEFT THAT LIST ON 2026-08-28, and the mechanism is the one
this paragraph predicted: `multi-stop`, `multi-origin-escrow`, `round-trip-pair` and
`hold-reasons` were blocked by the B4 batch-wiring bucket (their in-game cells exist
but carry `AllowBatchExecution = false`), and `H38-logistics-isolated` unblocked them
by driving the category through the R5 ISOLATED entry point. Its run 2
(`2026-08-28_1833`, PASS attempt 1) pins the tally WHOLE - `total=47 passed=39
failed=0 skipped=8` - and THAT is the gate the four claims rest on: with all three
numbers literal, a cell that fails reds `failed=0`, a cell that self-skips reds
`passed=39` and `skipped=8`, and a cell that is deleted reds `total=47` locally in
`harness/lib` before it can reach a flight. The H35 objection quoted above has also
inverted - run 2's log carries the production `[Parsek][*][Route]` units firing live
(`ReserveCargo`, `ReserveCycleEscrow`, `DropRouteEscrow`, `short-cause=escrow`,
`PartnerGate ... HOLD WaitingForPartner` / `CLEAR`, `hold recorded
kind=OriginLacksCargo`, `DispatchDebit`, `LoopRoute ... FIRED full cycle`,
`Delivery write: ... path=loaded`), so these are real units, not read-side walkers.
FIVE zero-declarer D10 rows remained at that point: `docked-depot-origin`,
~~`claw-producer`~~, `inventory-cargo`, `harvest-provenance`, `destination-full-gate`.
**`claw-producer` LEFT THAT LIST ON 2026-08-29**, claimed by
`H41-logistics-grapple-isolated` off its `2026-08-28_2216` flight - and it is the first
zero-declarer D10 row in the suite earned by PRODUCTION EMITTERS rather than by a
tally, which makes it the shape to copy. Three REQUIRED tokens name one causal chain:
`OnPartCouple producer classified: kind=Grapple fromPart=PotatoRoid
toPart=GrapplingDevice` (the production path recognising a live claw-to-asteroid
couple), `Route proof dock window captured: ... kind=Grapple` (the window actually
written - the exact analogue of what `dock-producer` is claimed on), and
`GrappleCapture PASS: ... complete=True roidGhostRenderers=1` (the window CLOSED on
release, plus the anti-vacuity half that the asteroid ghost built). A unit that stopped
producing claw route windows cannot leave all three matching. Note that lane's tally
CANNOT carry the claim - its `4/3/0/1` is byte-identical to what the ordinary batch
path prints - so the tokens ARE the gate rather than decoration layered on one.
FOUR REMAIN: `docked-depot-origin`, `inventory-cargo`, `harvest-provenance`,
`destination-full-gate`. The last of those
is the one H38 deliberately did NOT claim despite reaching it: the gate is exercised
only on the permissive branch (`DestinationHasCapacity: route <id> full manifest
fits`, x3) and `RouteStatus.DestinationFull` / `WaitDestinationFull` never fire, so
the unit could stop refusing a full destination entirely and all 39 cells would still
pass. It needs a cell that drives the REFUSAL, not another lane. The two
route-CREATION-shaped rows are still what the fix above would unblock.

**UPDATE 2026-08-28 - the refusal branch IS now measured, on H40.** The
recorded-host reading runs flew against a fixture whose active vessel carries a
FULL 720/720 LiquidFuel tank, and the all-or-nothing gate did exactly what this
note said was undriven: it held nine cells' synthetic cycles in `DestinationFull`
across three suites (origin-debit x2, round-trip x4, multi-origin x3). That is the
REFUSAL branch firing on a live flight, against a real craft, for the right
reason - the evidence `destination-full-gate` was missing. Two caveats for the
pinning round. (1) Those nine cells are being FIXED to create their own headroom
(`DestinationHeadroomFixture`), so after the fix they take the permissive branch
again and the refusal is no longer in their path - a claim must rest on a token a
POST-FIX flight still emits, not on the reading run's incidental reds.
(2) `LogisticsMultiOriginRuntimeTests.MultiOrigin_OneSourceShort_HoldsNamingSource`
holds on `OriginLacksCargo`, not on the destination gate, so it is not the cell
either. Claiming the row therefore still wants a cell that drives the refusal ON
PURPOSE (a full destination it does not drain, asserting
`RouteStatus.DestinationFull` + the `destination FULL ... holding cycle
all-or-nothing` Verbose line) - but the reading runs have now proven the branch is
reachable from a committed fixture, which is what was in doubt.

**SETTLED 2026-08-29 AT THE PINNING ROUND: THE ROW STAYS ZERO-DECLARER, and caveat (1)
above is exactly what happened.** H40's green census 3 (`2026-08-28_2122`) was measured
for it directly: **17** `DestinationHasCapacity: route <id> full manifest fits` lines and
**ZERO** occurrences of `DestinationFull` or `WaitDestinationFull` anywhere in the log;
the only `hold recorded kind=` value in the whole run is `OriginLacksCargo`, twice. So on
the flight that is actually pinned, the gate is reached constantly and takes the
PERMISSIVE branch every single time.

That is now STRUCTURAL rather than incidental, and census 1 is the proof: the nine cells
that DID drive the refusal all FAILED because of it, and the fix
(`DestinationHeadroomFixture.TryEnsureDestinationHeadroom`, commit `7cfde0c20`) gave each
of them headroom so they take the permissive branch deliberately. The suite therefore now
contains **nine cells that route AROUND the refusal and none that asserts it** - the
opposite of a declarer. A unit that stopped refusing a full destination entirely would
leave all 35 of H40's cells passing and every one of its eight pinned tokens matching.

WHAT WOULD CLOSE IT, unchanged and now with the evidence behind it: a cell that drives
the refusal ON PURPOSE - a full destination it does NOT drain, asserting
`RouteStatus.DestinationFull`, the `destination-full-<resource>` reason string from
`RouteDispatchDecision.WaitDestinationFull`, and the retry UT. Not another lane and not
another fixture: `depot-route-recorded`'s 720/720 `Depot` is already the most
destination-full state in the entire suite and it was not enough. Recorded in
`H40-logistics-isolated-depot-route.toml`'s `[dimensionsCovered]` block, which is where a
future reader will look first.

---

## REFLY-BATCH-BASELINE-DISCARDS-LIVE-SESSION: an in-game batch's baseline restore ends a live Re-Fly session, and the merge dialog never appears [OBSERVED 2026-08-12 by `S4.2-refly-world-preservation` attempt 1 of run `2026-08-11_2111`. REPORT-ONLY - not diagnosed, not fixed. Attempt 2 of the same spec ran clean, so it is INTERMITTENT. **THE DECIDING EXPERIMENT IS NOW AUTHORED: `harness/scenarios/S4.4-refly-quicksave-mid-session.toml` (2026-09-02, never flown)** - S4.2's cycle with a real `SaveGame name=quicksave` AND a real `LoadGame name=quicksave` from inside the live session in place of the batch (a player's F5 then F9). Save alone could only come back GREEN by construction - `End reason=treeDiscarded` is emitted only from load paths and the dialog's own Discard - so the LOAD half is the experiment: the quickload discard gate, the marker's save/load round trip and `LoadTimeSweep`. A killed session surfaces as INVALID(driver) `seam-timeout` on the merge step (as S4.2 attempt 1 did), with the reading in the log after the second `loadgame start`; the spec pre-registers what GREEN / INVALID / RED each mean]

S4.2's driver note called this out in advance as "the one step of this sequence
with no committed precedent" - a quicksave taken WHILE a Re-Fly session is live,
which is what `InGameTestRunner.CaptureBatchBaseline` does when it classifies a
FLIGHT scene with a live active vessel as `InMemoryAndDisk`. The note asked for
any resulting finding to land here rather than in a relaxed contract. This is it.

**What was measured**, from the collected `KSP.log` of attempt 1:

```
00:09:17.280 [ReFlySession] Started sess=sess_acab275b... rp=rp_wp_root slot=1 ... inPlaceContinuation=True
00:09:17.837 [TestCommands] runtests start category=ReFlyWorldPreservation isolated=false
00:09:17.931 [TestRunner] Final batch baseline restore (batch-complete-final-restore) from slot 'parsek-test-batch-baseline-...' scene=FLIGHT
00:09:17.971 [Scenario] Preparing save-scoped state for isolated FLIGHT batch baseline restore
00:09:18.439 [ReFlySession] End reason=treeDiscarded sess=sess_acab275b... tree=tree-wp-stack-root
...
00:09:20.819 [TestCommands] dispatch id=0005 -> DEFER reason=no-refly-dialog   (x24, then)
00:11:20.826 [TestCommands] timeout id=0005 cmd=AnswerMergeDialog deferred=120.0s reason=no-refly-dialog
```

The session ended `reason=treeDiscarded` ~1.2 s after it started, inside the
batch's baseline handling, so by the time the driver reached `AnswerMergeDialog`
there was no dialog to answer and the step timed out. The run classified
`INVALID(driver) subkind=seam-timeout`, which is the correct classification - a
driver-INVALID, never a PARSEK-FAIL.

**Why it is filed as an observation and not a defect.** Three things are true and
none of them has been separated yet:
- the batch is green either way (`BATCH_COMPLETE v1 total=6 passed=6 failed=0
  skipped=0`), and all six preservation cells passed on the discarded-session
  attempt too, so nothing about the world-preservation claim depends on this;
- none of the three conclusion tokens fired on that attempt, which is consistent
  with "the session was already gone", not with a conclusion-route regression;
- attempt 2 flew the identical spec clean, all three tokens present. `flake.json`
  quarantines the scenario at `rate=0.50 over 7d`.

**The open question** is whether this is harness-only or reaches a player. The
batch path is test-runner-specific, but the underlying act - a quicksave during a
live Re-Fly session - is what any F5 does, and `R7-SESSION-BATCH-ISOLATION` above
already records that running a category beside a live session breaks tests. If a
player F5/F9 can drive `End reason=treeDiscarded` the same way, that is a product
bug and this entry is its head; if only the batch's save-scoped-state preparation
does it, this is harness isolation and belongs with R7. Deciding needs one
deliberate experiment (quicksave mid-Re-Fly outside a batch), which was NOT run
here.

**Discriminator for the next occurrence**, so nobody re-derives it: on a repeat
`AnswerMergeDialog` timeout, check whether `[ReFlySession] End
reason=treeDiscarded` precedes the first `DEFER reason=no-refly-dialog`. If it
does, this entry; if it does not, the conclusion route.

## PART-ACTION-RECORDING-COVERAGE: audit backlog for what Parsek records vs the stock part-action surface [OPEN 2026-08-09]

Full matrix and reasoning:
`docs/dev/research/part-action-recording-audit-2026-08-09.md`. 103 stock
`PartModule` types decompiled and cross-matched against the 35 `PartEventType`
values, 22 `GameStateEventType` values, 59 subscribed `GameEvents`, the playback
dispatch and the Re-Fly restore path. That doc is the authority; this entry is
the index so the items are not lost.

**Both MUST-table `C` items are now closed.** `C1` (restore non-slot vessels,
asteroids/comets, flags) shipped 2026-08-09 - see the
REFLY-DELETES-NON-SLOT-WORLD entry above. `C2` (pre-invoke advisory naming what
the revert takes back) shipped 2026-08-11 into the Re-Fly confirmation dialog +
one `[Rewind]` Info line; details in that same entry.

**The `S` table is now CLOSED OUT: S1-S3 and the wheel corollary shipped 2026-08-11
(`playback-fidelity`), and S4 / S6 / S7 shipped 2026-08-12 (`part-event-fidelity`/P8),
which also converted the §2 science-timeline row into four explicit WON'Ts.** Nine new
`PartEventType` members (36-44), no schema-generation bump (verified against both
sidecar readers rather than assumed), and a new in-game category `PartEventFidelity`
wired as `H37-part-event-fidelity`. **CONFIRMED LIVE 2026-08-12** by the H37 re-fly
(`total=5 passed=5 failed=0 skipped=0`, every verifier green): the break/repair/loop
round trip on `solarPanelOX10C` (break subtree `rootHinge`, 13 transforms - so the
`breakName` -> `pivotName` fallback resolves in production, which was the wave's
likeliest silent no-op), the converter loop at `quarterRot=90.0001deg` with
`driftAfterStop=0deg`, the empty-deploy-name ISRU shape with 4 looping transforms, and
the EVA plume built and gated. Its FIRST flight red 3/5 and bought two fixes worth
naming here, since both are the wave's own failure mode - a replay that renders or
reports plausibly rather than correctly:
- **A silent `Play()` no-op.** `ParticleSystem.Play()` on a ghost that is not
  `activeInHierarchy` neither throws nor sets `isPlaying`, and a ghost is inactive for
  the whole of its spawn-time prefix replay - so an EVA ghost spawning mid-burst stayed
  dark for the entire burst while the log reported it emitting. Fixed with a pure
  `ClassifyEvaPlumeReconcile` that DEFERS on an inactive hierarchy, a per-frame
  `UpdateEvaJetpackPlumeForFrame` self-heal beside the launch-dust drive (dust already
  had that property for free by re-calling Play every frame), and decision-vs-truth
  logging that reads `isPlaying` BACK rather than reporting success on the strength of
  having called Play.
- **A rotation-blind test.** The Goo verification cell skipped VACUOUSLY because its
  precondition tested POSITION only, while a science canister's `Deploy` clip swings its
  doors; the re-fly's `span(pos=0 rot=29.99998deg)` on `mk2LanderCabin.v2` is the
  diagnosis in one number. Precondition and assertions are now per-component against the
  builder's own `CollectTransformDeltas` thresholds. No `RUNTIME_SKIPS` entry: the skip
  was fixed, not accepted. Three findings from P8 worth carrying forward
because each corrects something a future reader would otherwise trust:
- Deployable `BROKEN` is **REVERSIBLE** (stock `eventRepairExternal` -> `DoRepair` lands
  on RETRACTED), so it is a reversible-family split seed, not a permanent one. The audit
  described it as "permanent".
- Of the three KerbalEVA members, `JetpackDeployed` IS a `[KSPField]`; the other two are
  not. Typed casts remain correct, but the reason is "two of three are invisible to a
  `module.Fields` walk", not "none is a KSPField".
- The EVA pack MESHES are still absent, and NOT because the asset is unreachable
  (`KerbalEVA.JetpackTransform` is a public serialized `Transform`). The missing piece is
  the VISIBILITY signal - `HasJetpack`, driven by inventory contents - which Parsek does
  not record; the honest follow-on is to read the snapshot's `ModuleInventoryPart`.
- **`EmitTerminalEngineAndRcsEvents` must not mutate its tracking sets** - all four
  families, jetpack included. The first cut of the S4 terminal close removed each pid as
  it emitted, which reads like double-emit safety but breaks the false-alarm unwind: the
  STOP path leaves the sets intact ON PURPOSE so `ResumeAfterFalseAlarm` can undo an
  abandoned chain-boundary stop and keep tracking a burn that never ended, and with the
  pid gone the resumed poll's real thrust-end edge hit
  `thrustingParts.Remove(pid) == false` and emitted nothing - the pair stayed open to
  recording end, the exact failure the terminal emit exists to prevent. Double-emit safety
  is the CALLER's, exactly as for engines: the rails site
  (`EmitTerminalEventsAndClearActiveState`) clears every set right after the emit, so the
  second emit's `Count > 0` gate is a no-op. Fixed 2026-08-12; guarded by
  `PartEventFidelityTests.AFalseAlarmStopMidBurst_ResumesTracking_AndClosesAtTheREALThrustEnd`
  and `...TheRailsPathClearIsWhatMakesASecondTerminalEmitANoOp_NotARemoveOnEmit`.
- **NOTE ONLY, no fix owed:** a kerbal-to-kerbal `ContinueOnEva` switch mid-burst closes
  the DEPARTING kerbal's thrust pair only at the recording's final-stop UT, not at his
  real thrust end - late, not lost. `PruneDepartedTrackingKeys` covers the engine / RCS /
  robotic sets and does not reach the EVA sets, so the departing pid stays in
  `thrustingJetpackParts` until a terminal emit closes it. Recorded because the late close
  is a plausible-looking plume, not an obviously wrong one.

The `M` items and the remaining non-`S` items below are untouched.

**Establish the restore model before reasoning about any "world desyncs" claim.**
The RP quicksave is a full `GamePersistence.SaveGame`, so the whole `GAME` node
comes back. Anything in a `PART`/`MODULE` node on the selected slot is restored
verbatim and is BENIGN - experiment `Deployed`/`Inoperable`, container contents,
lab accrual, ISRU tank gains, per-part resource amounts,
`flowState`/`flowMode`/crossfeed, `ACTIONGROUPS`, seats, inventory, `BROKEN`
panels, ablator. So are ore depletion and biome/planet unlock (they live in
`ResourceScenario`, a `GAME`-node scenario) - `grep ResourceMap Source/` returns
0 hits and that is fine, not a gap. Recording those buys divergence DETECTION,
not correctness.

Open items, highest leverage first:

- ~~FIXED 2026-08-11 (PR #1443)~~ **Ghost initial state is read from the part PREFAB, not the recorded
  snapshot.** The snapshot IS a full `ProtoVessel` backup
  (`FlightRecorder.cs:6080-6082`) but `GhostVisualBuilder` reads only
  name/pid/pos/rot (`:431-441`) and builds from `ap.partPrefab`. Exactly three
  module states are read from it: `ModuleJettison.isJettisoned`, fairing
  fsm/XSECTION, `ModulePartVariants`. Everything else rides a 16-family
  `PartStateSeeder` whitelist. The promotion gate
  (`FlightRecorder.cs:6544-6581`) deliberately emits engine-only seeds for EVERY
  continuation segment including Re-Fly forks, so a post-rewind ghost renders
  gear up / panels folded / lights off for its whole span. Fix by reading the
  snapshot at BUILD time (sidesteps the `#263` `FindLastInterestingUT` invariant
  the gate protects). MUST land with the robotic split-seed fix - a
  `RecordingTreeSplitter` TIP inherits the parent's LAUNCH-UT snapshot, so the
  snapshot read alone gives forks a confidently-wrong pre-launch pose.
- ~~FIXED 2026-08-11 (PR #1443)~~ **Robotic events are in neither split-seed family** and
  `ReapplySpawnTimeModuleBaselinesForLoopCycle` never calls `ApplyRoboticPose`.
- ~~FIXED 2026-08-11 (PR #1445)~~ **Two confidently-wrong recorded signals** that playback faithfully renders:
  parachute REPACK is classified as CUT (`FlightRecorder.cs:1665-1690`) so a
  repacked chute renders as an empty can; and wheel spin records `driveOutput`
  (percent-of-max-torque) replayed at `value * 6` deg/s as if RPM
  (`GhostPlaybackLogic.cs:3588`), with `Mathf.Abs` making reverse identical to
  forward and a coasting rover showing stationary wheels. Re-deriving spin from
  trajectory ground speed is storage-NEGATIVE.
- ~~FIXED 2026-08-11 (branch `playback-fidelity`)~~ **Recorded magnitude that playback discards.**
  `SetEngineEmission` branched only on `power > 0f`; only the audio curves read the
  magnitude, and `ComputeScaledRcsEmissionRate`/`...Speed` had ZERO production call
  sites. **Fix:** both `Set*Emission` choke points now scale the plume as a RATIO of a
  baseline captured once at build time (`GhostVisualBuilder.CaptureFxMagnitudeBaselines`,
  run AFTER the #383 size boost and the world-space velocity floor so scaling composes
  with them rather than overwriting them), written into the cloned `KSPParticleEmitter`'s
  own persistent fields plus the `ParticleSystem` multipliers. Persistent fields, not a
  per-event computation, because `RestoreAllRcsEmissions` re-enables emitters WITHOUT
  going through `SetRcsEmission` — a per-event scale would restore a full-magnitude plume
  on a quarter-throttle thruster after every high-warp suppression. The two RCS helpers
  are the ratio's numerator and denominator, which gives them production callers with
  their showcase visibility floors intact. Degradation is one-directional: an unreadable
  baseline answers ratio 1.0, i.e. today's boolean behaviour, never a zero plume. Audio
  untouched (it already consumed the magnitude; touching it would double-apply). Review
  follow-up: the one baseline the ratio cannot scale freely is a WORLD-SPACE emitter's
  `localVelocity`, because that baseline may BE the minimum-flow floor
  (`ApplyWorldSpaceEmitterVelocityFloor`, 6 m/s) rather than a magnitude — a 0.2 ratio would
  write 1.2 m/s, back under the 4 m/s pooling threshold the floor exists to clear.
  `GhostPlaybackLogic.ScaleEmitterLocalVelocity` re-clamps that one write to the floor (never
  above the baseline's own magnitude) and leaves every other write a plain ratio; reachable only
  on ReStock's world-space SRB smoke at genuine partial throttle.
  **FLIGHT FOLLOW-UP 2026-08-12: the whole of the above was a SILENT NO-OP in the live game
  until now.** The H36 run of 2026-08-11 logged, for engine midx=0, engine midx=1 AND rcs
  midx=0, `FX magnitude write failed ... Object of type 'System.Single' cannot be converted to
  type 'System.Int32'.; plume stays at its baseline`. `KSPParticleEmitter.minEmission` and
  `maxEmission` are declared **int** (`minSize`/`maxSize` are float, `localVelocity` is Vector3
  — verified by decompiling Assembly-CSharp, not assumed); `FieldInfo.SetValue` performs no
  numeric conversion, so a boxed float threw on the FIRST of the three writes and, because all
  three shared one `try`, took the other two with it. **Fix:** every write converts to the
  `FieldInfo`'s own `FieldType` (`GhostPlaybackLogic.TryConvertMagnitudeForField`) and each
  field is written independently; integral fields round to nearest with a NONZERO floor, which
  keeps `ComputeFxMagnitudeRatio`'s "never zero for a lit engine" contract at the quantisation
  boundary, and the floor keeps the SIGN so stock's negative-`maxEmission` "does not emit"
  sentinel survives. `CaptureFxMagnitudeBaselines` now type-checks each field at capture and
  drops an unwritable one to null instead of caching it for the applier to fail on every frame.
  **Why three review passes and 66 headless cells missed it:** every headless cell pinned the
  ratio ARITHMETIC (floats in, float out) and none ever met the real field types; and the
  in-game suppress/restore cell passed VACUOUSLY, because "unchanged baseline in, unchanged
  baseline out" satisfies a round trip perfectly. Both holes are now closed — headless cells
  reflect over the real `KSPParticleEmitter` type and perform a real `SetValue` against an
  uninitialised instance (`FormatterServices.GetUninitializedObject`, no ctor, no ECall), and
  the RCS cell asserts the scaled value differs from the captured baseline BEFORE asserting the
  round trip preserves it. `FxMagnitudeWriteFailureCount` is a hard-zero assertion in both
  plume cells, because the failure LOG line is rate-limited to one per minute per module and so
  undercounted a total no-op as a curiosity.
  **CONFIRMED IN FLIGHT 2026-08-12** (H36 re-fly, run `2026-08-11_2211`, PASS 7/7). The write
  LANDS on the real cloned emitters and the scaled values are genuinely off the baseline:
  engine `part='ionEngine' fullSpeed=6.5 lowSpeed=1.95 fullEmission=350 lowEmission=105
  restored=6.5 writeFailures=0` — the int-typed `maxEmission` scaled 350 -> 105 at reduced
  throttle and came back EXACTLY to 350 at full, which is the ratio-not-rewrite property — and
  RCS `part='mk1-3pod' baselineSpeed=48 scaledSpeed=12 afterRestore=12 baselineEmission=400
  scaledEmission=104 afterEmission=104 writeFailures=0`, where the scaled value DIFFERS from the
  baseline (the anti-vacuity assertion) and then survives the `RestoreAllRcsEmissions` round
  trip unchanged. ZERO `FX magnitude write failed` lines anywhere in that run's KSP.log, against
  three (engine midx=0, engine midx=1, rcs midx=0) on the first flight.
- **Five dead reflection probes**, four of them documented as shipped at
  `done/next-parts-event-support-priority.md:43-47`. `module.Fields` is
  `[KSPField]`-only (`FlightRecorder.cs:3733-3748`); `ModuleControlSurface`'s
  real field is `deploy` and the table lists `deployed`; the piston probe latches
  `traverseVelocity`, a `[KSPAxisField]` SPEED SLIDER that resolves and is
  constant during a stroke, shadowing the working `servoTransformPosition`
  fallback; `ModuleAnimateHeat`'s live scalars are plain public fields whose
  accessor is the `IScalarModule.GetScalar` property - ONE cast lights up the
  complete already-built Hot/Medium/Cold playback path
  (`GhostPlaybackLogic.cs:3693-3770`).
- **Recorder cache and rails holes.** `cachedEngines` is assigned only in
  `ResetPartEventTrackingState` (sole caller `StartRecording`) and
  `CheckEngineState` guards only `part == null`, so a staged-away booster that
  keeps burning writes into the PARENT recording. The background rails
  transition ERASES state instead of deferring: `BackgroundRecorder.cs:2316-2336`
  drops `loadedStates` with no terminal emit, so a BG ghost's plume latches on
  for the whole rails span.
- ~~FIXED 2026-08-11 (branch `playback-fidelity`)~~ **Continuous motion to SYNTHESIZE, never
  sample:** gimbal and control-surface deflection, wheel steering, sun tracking, launch dust.
  **Fix:** one new per-frame entry, `GhostPlaybackLogic.UpdateSynthesizedMotion`, beside
  `UpdateActiveRobotics`, everything gated so a craft with none of these families pays a
  reference compare. ONE CORRECTION TO THE ORIGINAL PRESCRIPTION: the attitude derivative
  reads the ghost's APPLIED WORLD ROTATION, never `TrajectoryPoint.rotation` from a flat
  Points list — that field is anchor-local in a RELATIVE track section, so differencing two
  of them across a section boundary mixes frames and invents a rotation that never happened.
  Wheel steering became a new `RoboticVisualMode.WheelSteeringHeading` that IGNORES the
  recorded `ModuleWheelSteering` scalar for the same reason wheel spin ignores
  `driveOutput`: it was an unsigned steering INPUT, not a caliper angle. Review follow-up: the
  PRODUCER is gone too, following the #1445 precedent exactly — once playback discards every
  `RoboticMotion*` event in that mode the scalar is a write-only surface, so the recorder gate
  widened from `IsWheelMotorSpinModuleName` to `IsDerivedWheelVisualModuleName` (foreground and
  background). Storage-negative, playback stays tolerant of legacy events, nothing retired in
  `PartEventType` (hinges, pistons, rotation servos, rotors and wheel SUSPENSION still use it). Launch dust is
  narrower than written — Parsek owns its own particle system (the reentry `fireParticles`
  template) instead of driving `ModuleSurfaceFX`, is built lazily under the existing
  per-frame build cap, and is gated on a ground reference latched from
  `recordedGroundClearance`; no clearance anywhere in the trajectory means no dust,
  permanently, rather than an invented reference that would put a dust cloud around a ghost
  in orbit. Every new mutable visual resets in BOTH `ResetForLoopCycle` (the numbers) and
  `ReapplySpawnTimeModuleBaselinesForLoopCycle` (the transforms), including a new
  `WheelSteeringHeading` branch in `ApplyRoboticSpawnBaseline` — `ApplyRoboticPose` writes
  nothing for that mode, so without it a caliper left turned would carry across a loop
  boundary while the numbers claimed straight. Also review follow-up: under SUSTAINED warp the
  steering rate now DECAYS toward zero on every re-seed frame instead of holding its last value
  (`DecayRateTowardZero`), so a warping rover eases its calipers straight rather than freezing
  mid-turn; the same re-seed hold is left in place for gimbals and control surfaces on purpose
  (bounded by the clamp, invisible at warp's visual scale, self-correcting on the first
  sub-second frame). Live coverage: the `PlaybackFidelity` in-game category (7 cells) driven by
  `harness/scenarios/H36-playback-fidelity.toml` (FLOWN twice: 2026-08-11 PARSEK-FAIL 5/7, then
  LIVE-PROVEN on the 2026-08-12 re-fly after both fixes — run `2026-08-11_2211`, PASS attempt 1,
  `total=7 passed=7 failed=0 skipped=0`; the tally pin is now whole and the id has left
  `INTERIM_PIN_IDS`).
  **FLIGHT FOLLOW-UP 2026-08-12, the sun-tracking red.** `SunTrackingPivotAimsOnlyWhenFully-
  Deployed` red with `solarPanelOX10C` never resolving an aim angle. It was NOT the deployed
  gate — the same run's `Spawn baseline: stowed 1/1 deployable(s)` line proves the part's
  `DeployableGhostInfo` exists and that `ApplyDeployableFraction` applies, so the cell's
  ARM-2 immediate deploy necessarily set `deployFraction = 1`. It was the AIM FRAME.
  `DriveSunTracking` measured the angle about the PARENT's up and from the pivot's neutral
  world FORWARD, while `ApplySunTrackingAngle` post-multiplies `neutralRotation *
  AngleAxis(angle, axisLocal)` — i.e. applies it about the PIVOT'S OWN local axis. Two
  consequences: the measured axis and the applied axis disagreed whenever the pivot's neutral
  rotation was not identity, and on a pivot authored with a quarter-turn (OX-10C) the reference
  landed PARALLEL to the parent's up, its projection into the aim plane vanished, and
  `TryComputeAimAngleDegrees` declined every frame forever. **Fix:** both the axis and the
  reference are now read in the pivot's own neutral frame
  (`parentRotation * neutralRotation * axisLocal`), and the reference is ORTHOGONALISED against
  the axis by construction (`ResolveSunTrackingReferenceLocal`), so the only surviving decline
  is the legitimate one — the Sun lying along the rotation axis. This also matches stock, which
  tracks with `Atan2(pivot.InverseTransformPoint(sun).x, ....z)` applied as
  `pivot.rotation * Euler(0, y, 0)`: the pivot's own local +Y as axis, its own local +Z as the
  zero-angle reference. **Diagnosis cost, now paid down:** the cell could only list the two
  candidate causes. `DriveSunTracking` now emits two discriminating rate-limited lines
  (`Sun tracking held (gate closed) ...` with the full deployable gate state, and
  `Sun tracking held (aim unresolved) ...` with both aim-plane projection magnitudes), and the
  cell pastes `GhostPlaybackLogic.DescribeSunTrackingState` into its own failure message — a
  near-zero `targetPerp` is the legitimate hold, a near-zero `referencePerp` is a frame bug,
  and `gate=closed` is a deployable-path bug.
  **CONFIRMED IN FLIGHT 2026-08-12** (H36 re-fly, run `2026-08-11_2211`, PASS 7/7). The cell's
  own state line now reads `part='solarPanelOX10C' stowedDrift=0 aimed=90 holdDelta=0 gate=open
  deployable=present currentDeployed=True transitionActive=False deployFraction=1 transforms=13
  aimResolved=True aim=89.99999 targetPerp=1.04331008E+10 referencePerp=1 currentAngle=89.99999
  hasAimed=True` — an angle RESOLVES on the very pivot that declined every frame before, and the
  discriminating numbers read exactly as designed: `referencePerp=1` (the orthogonalised
  reference is now unit-length in the aim plane, where the frame bug drove it to zero) beside a
  large `targetPerp`, with `stowedDrift=0` proving the deployed gate still holds the pivot still
  and `holdDelta=0` proving it holds once aimed.
  The gimbal cell additionally carries the SIGN/HANDEDNESS pin for the hand-rolled quaternion
  product — the headless cells build their inputs in the convention the product assumes, which
  is circular, so the only non-circular authority is Unity's own
  `Inverse(prev) * cur -> ToAngleAxis`, compared by axis dot rather than by unsigned angle.
- **Recorded magnitude that playback discards.** `SetEngineEmission`
  (`:2172-2204`) branches only on `power > 0f`; only `:2405`/`:2423` (audio
  curves) read the magnitude. `ComputeScaledRcsEmissionRate`/`...Speed` exist at
  `:3354`/`:3367` with ZERO production call sites (only
  `RuntimePolicyTests.cs:210,219,228,230`). Worst on Waterfall installs, whose
  premise is a throttle-continuous plume.
- ~~FIXED 2026-08-11 (S5)~~ **Dead reflection probes**, documented as shipped at
  `done/next-parts-event-support-priority.md` (that section is now corrected in
  place, per-bullet, with the decompiled reason). `module.Fields` is
  `[KSPField]`-only, so a plain public field is invisible however obvious its
  name. Four real fixes and one audit correction:
  - `ModuleControlSurface` / `ModuleAeroSurface`: the real field is `deploy`
    (`[KSPField(isPersistant)]`), added at the HEAD of
    `AeroSurfaceDeployedFieldNames`. `deployAngle` / `aeroDeployAngle` are a
    separate VETO table, not deflection candidates - they are `[KSPAxisField]`
    tweakables that `OnStart` resolves from `NaN` to `ctrlSurfaceRange`, so
    treating them as "non-zero means deployed" would have classified every
    control surface in the game as permanently out. `aeroDeployAngle` leads
    because an airbrake carries BOTH and deactivates the inherited one.
  - `ModuleAnimateHeat`: not fixable by names. New non-name-keyed
    `IModuleFieldValues.TryGetScalarModuleScalar` → one `as IScalarModule` cast
    (`ModuleAnimationSetter.GetScalar => inputState`), consulted BEFORE the name
    table. Lights up the complete already-built Hot/Medium/Cold recorder +
    playback path.
  - `ModuleRoboticServoPiston`: `currentExtension` + `targetExtension` prepended;
    `traverseVelocity` (a `[KSPAxisField]` SPEED slider, constant during a
    stroke) and `targetPosition` (`private float`, never reachable) removed. Note
    the DELIBERATE divergence from the snapshot-side ghost baseline, which reads
    `targetExtension`: only that one is `isPersistant`, so it is the only one a
    saved craft carries, while only `currentExtension` sweeps live.
  - `ModuleWheelSuspension`: `suspensionOffset` (a config constant applied once to
    the wheel collider) dropped so the live `suspensionPos` vector fallback wins.
  - `ModuleRobotArmScanner`: **the audit claim was wrong; nothing changed.** It
    derives from `ModuleDeployablePart`, whose `[KSPEvent] Extend()`/`Retract()`
    the scanner actively toggles, so the probe's event-activity stage resolves.
    Its `ArmDeployState` is behind a `new` property over a private unattributed
    `_deployState` (unreachable by name) AND redundant - that setter mirrors every
    arm state onto the base `deployState` that `CheckDeployableState` polls, so an
    accessor would emit a duplicate `DeployableExtended` under a second key.
- **Recorder cache and rails holes.**
  - ~~FIXED 2026-08-11 (M5)~~ **Cached engine/RCS/robotic modules were polled
    without a vessel-identity check.** `cachedEngines` was assigned only in
    `ResetPartEventTrackingState` (sole caller `StartRecording`) and
    `CheckEngineState` guarded only `part == null`, so a staged-away booster that
    kept burning wrote into the PARENT recording. Fix: the pure
    `FlightRecorder.DecideCachedModulePoll` gates every per-frame read on
    `ReferenceEquals(part.vessel, recordedVessel)` - a LIVE object comparison, not
    a pid or guid one, so the craft-baked-pid identity rule does not apply - across
    the foreground engine/RCS/robotic polls AND their background mirrors in
    `BackgroundRecorder.PartEventPolling.cs`. `FlightRecorder.OnVesselWasModified`,
    wired from the already-subscribed `ParsekFlight.OnVesselWasModified`, rebuilds
    the three cache LISTS, which also closes the inverse hole: a welded-on
    EVA-construction engine or a newly docked module was absent from a cache built
    at `StartRecording` and emitted nothing at all for the rest of the flight.
    **Rebuild is FOREGROUND-ONLY, deliberately.** `BackgroundRecorder` assigns its
    per-vessel caches once in `InitializeLoadedState` and has no
    `onVesselWasModified` hook, so a module ARRIVING on a BG-loaded vessel still
    records nothing until that vessel next re-enters loaded state. Accepted, not
    overlooked: the BG half of the ownership guard stops the POLLING direction (a
    departed part writing into the wrong recording), and the missing direction
    needs a per-BG-vessel subscription whose cost scales with the background
    fleet. The M5b re-review sharpened the residual: the D1 key-rot shape itself
    also survives on the BG side - a BG-loaded vessel that sheds a burning part
    with NO detected split keeps the departed key in
    `loadedState.activeEngineKeys` (guard skips polls, no prune hook), and
    `EmitBackgroundRailsTerminalEvents` writes a stale `EngineShutdown` for that
    pid at the next rails transition. Bounded differently from the FG case:
    DETECTED sheds route through `CloseParentRecording`, which discards the
    parent's `loadedState` so the stale key dies unemitted; the residual
    manifests only for undetected sheds, and becomes a genuine tail artifact
    only when the vessel then stays on rails until commit.
  - ~~FIXED 2026-08-11 (M5b)~~ **Departed-part keys rotted in the tracking sets.**
    The M5 guard makes a departed booster's burnout UNOBSERVABLE, so its key never
    left `activeEngineKeys` - and the terminal emit
    (`EmitTerminalEventsAndClearActiveState` at the rails transition,
    `FinalizeRecordingState` at stop) walks the ACTIVE sets, writing an
    EngineShutdown / RCSStopped into the PARENT recording for a pid that left
    minutes earlier. It landed at the TAIL, where
    `RecordingOptimizer.IsInertPartEventForTailTrim` counts EngineShutdown as
    interesting, so the #263 boring-tail trim was defeated. Fix: the pure
    `FlightRecorder.PruneDepartedTrackingKeys`, called from `OnVesselWasModified`
    (which the `ContinueOnEva` rebuild also routes through), silently drops keys
    for departed pids across all eight cache-fed collections - `activeEngineKeys` /
    `lastThrottle` / `allEngineKeys`, `activeRcsKeys` / `lastRcsThrottle` /
    `rcsActiveFrameCount`, `activeRoboticKeys` / `lastRoboticPosition` /
    `lastRoboticSampleUT`. No synthetic event: the Decoupled event already hides
    the subtree and the child recording owns the burn. Two subtleties. (1)
    Survival is measured against `Vessel.parts` UNIONED with the rebuilt caches,
    not the caches alone, so a part whose module list momentarily reads empty (the
    dock/undock shuffle window) is not pruned into losing a continuing burn; an
    all-empty read prunes nothing at all (`CanPruneAgainstSurvivingPids`). (2)
    Engine/RCS state is MOVED to `DepartedEngineThrottles` /
    `DepartedRcsThrottles`, not dropped: `InheritedEngineState.FromRecorder` (the
    #298 parent-to-child snapshot) is taken from `ProcessBreakupEvent`, a whole
    crash-coalescer window AFTER `onVesselWasModified` fires, so an unconditional
    prune would delete "the booster was at full throttle when it came off" before
    the child debris recording could inherit it. `MergeInheritedEngineState`
    filters by the child's own part pids, so a carry-over entry only ever reaches
    the child that holds that part; an entry is dropped the moment its pid returns.
  - ~~FIXED 2026-08-11 (M6)~~ **The background rails transition ERASED state
    instead of deferring it.** `OnBackgroundVesselGoOnRails` dropped `loadedStates`
    with no terminal emit (BG ghost's plume latched on for the whole rails span),
    and on re-entry `SeedBackgroundPartStates` re-synced every tracking set to live
    truth while `TrySeedLoadedPartEvents` declined to write (`PartEvents.Count > 0`),
    so a change during the warp was erased rather than deferred. Fix, two halves:
    `EmitBackgroundRailsTerminalEvents` runs the same
    `FlightRecorder.EmitTerminalEngineAndRcsEvents` the foreground already ran at
    ITS rails transition (a vessel that packs with nothing running still emits
    nothing, so a parked rover's boring tail is not extended), and
    `CaptureRailsSpanPartStates` deep-copies the now-quiet tracking sets into
    `railsSpanPartStates`. On re-entry `TryEmitRailsSpanDiff` feeds them to the pure
    `PartStateSeeder.EmitDiffEvents` (all 17 families - six pid-keyed boolean sets,
    blinking lights, parachutes, six module-keyed deployable sets, thermal, engines,
    RCS; one-way shroud/fairing emit on ARRIVAL only; parachutes route through the
    shared 4-state `ClassifyParachuteTransitionEvent`; `EngineThrottle` quantises on
    the shared `FlightRecorder.EngineThrottleDeadband`). Snapshot is consumed once
    and dropped at every BG teardown site - `OnBackgroundVesselWillDestroy` was
    missing its drop and got one, since `persistentId` is craft-baked and an orphan
    would be handed to the next launch of the same craft. Both invariants are now
    drift-proofed by reflection/source sweeps rather than hand-enumerated lists:
    `PartTrackingSetsFieldSweepTests` asserts the deep clone copies every field to a
    distinct instance and the reconciler is SENSITIVE to every field (explicit
    exempt-list with reasons; `allEngineKeys` is the only entry), and
    `RailsSpanSnapshotTeardownGateTests` asserts every `loadedStates.Remove` method
    also drops `railsSpanPartStates` (sole exemption: the go-on-rails capture site).
    The `FlushLoadedStateForOnRailsTransitionForTesting` seam now runs the terminal
    emit and the snapshot capture too, so injected fixtures exercise the product
    transition rather than the pre-M6 one.
- **Continuous motion to SYNTHESIZE, never sample:** gimbal (`ModuleGimbal`, 243
  stock parts, ZERO Parsek references) and control-surface deflection from the
  recorded `srfRelRotation` derivative; wheel steering from heading change; sun
  tracking from `Planetarium.fetch.Sun`; launch dust (`ModuleSurfaceFX`, 183
  parts, zero references) from engine power + altitude. Precedent:
  `ApplyAblationChar` already synthesizes reentry char from live physics.
- ~~**Career-bearing modules with a modest visual, which fell through both the "is
  it visible" and "does the quicksave restore it" sieves:**~~ **RESOLVED AS FOUR
  EXPLICIT WON'Ts, 2026-08-12** (`part-event-fidelity`/P8) - net ZERO new event
  types, and the audit's own §2 row is CORRECTED in the process. Full reasoning and
  evidence in
  `docs/dev/research/part-action-recording-audit-2026-08-09.md` -> "CORRECTION
  2026-08-12"; the load-bearing findings:
  - `ModuleScienceExperiment` (158): the deploy visual was **already recorded**. The
    animation belongs to a SEPARATE `ModuleAnimateGeneric` named `Deploy` (Goo and
    Science Jr, wired via `FxModules = 0`), and `ModuleScienceExperiment` is not in
    `HasDedicatedAnimateHandler`, so `CheckAnimateGenericState` has always polled it.
    The audit's "`Deployed` ... gates the deploy animation" wording is what made the
    verdict look like a gap. P8 added the VERIFICATION that never existed instead of a
    recorder: an in-game cell that reds if the claim stops holding.
  - `ModuleDataTransmitter` (201): the only stock transmit visual is one the recorder
    ALREADY polls. **Evidence corrected 2026-08-12** - the first write-up claimed zero
    stock parts set `DeployFxModuleIndices` / `ProgressFxModuleIndices`, which was a
    GREP-NAME TRAP: those are the C# field names, while the cfg keys `OnLoad` parses into
    them are `DeployFxModules` / `ProgressFxModules`. Six stock antennas DO set
    `DeployFxModules = 0` (HighGainAntenna, commDish88-88, commsAntennaDTS-M1,
    commsAntenna16, HG-5, HG-5_v2), and index 0 is each part's `ModuleDeployableAntenna`
    - so the stock transmit visual is the dish extending, a `deployState` change the
    existing `ModuleDeployablePart` poll has always recorded. `ProgressFxModules` (the
    transmit-progress visual proper) is the one with genuinely zero stock setters. Verdict
    unchanged; a transmit event type would be a second recorder for one visual.
  - `ModuleTestSubject` (709): the tested part's own action is already recorded by its
    family, and contract completion is already in `GameStateEvent` types 2/15/17.
  - `ModuleOrbitalSurveyor` (2): M700 deploy already recorded via the AnimationGroup
    family; `PerformSurvey` has no part visual and fires
    `GameEvents.OnOrbitalSurveyCompleted`. The planet-unlock facet is a LEDGER concern
    for a ledger wave.
- **Ledger facets:** kerbal XP is zero-coverage (`ModuleTripLogger` /
  `flightLog` / `ArchiveFlightLog` / `experienceLevel` all return NOTHING across
  `Source/`) and survives a supersede that refunds the funds; it is monotone, so
  the patcher is a re-assert. Contract snapshots are ACCEPT-time only
  (`GameStateRecorder.Handlers.cs:84-93`, and `ContractsModule.cs` has zero
  occurrences of "parameter"), so `PatchContracts`' rebuild branch
  (`KspStatePatcher.cs:2050-2085`) returns a reinstated contract at 0/N
  waypoints.
- **UNTESTED INTERACTION, trace before assuming:** `ScienceChanged` is in the
  seven-facet patch set while the `ResearchAndDevelopment` node - including each
  subject's decayed `scientificValue` - is a `GAME`-node facet reverted to the
  rewind UT. A surviving branch's post-rewind `ScienceChanged` rows are
  re-applied against a rolled-back subject table. `PatchScience` was NOT traced.
  The boundary between a restored `GAME`-node scenario and a patched ledger facet
  that reads from it is where the remaining rewind bugs will be.
- **Doc defects:** `done/next-parts-event-support-priority.md:43-47` (five
  families claimed shipped, all dead probes); the rewind design doc §7.13 still
  claims v1 never un-completes a contract, but `PatchContracts` can remove a
  tombstoned finished row.
## MOON-LOOP-FINDINGS: two product observations from the V6/V7 moon quartet [FOUND 2026-08-08, both report-only. ~~neither fixed~~ **FINDING (2) IS FIXED 2026-08-29** (branch `release-hygiene`, PR #1568, R5 item 2): the Parsek half of the watch-mode teardown NRE - `GetActiveVesselSafe` now catches the `NullReferenceException` arm too, through `ReadActiveVesselGuarded`, which also un-shadows ~20 lines of `ParsekFlight.OnDestroy` teardown that had never executed in the end-inside-watch shape. The stock/MechJeb cascade in the same census is NOT ours and stays unarmed. **FINDING (1) - the deterministic 131.22 deg `icon-off-orbit` raise - REMAINS OPEN AND REPORT-ONLY**, with its discriminating experiment named but unflown. Header reconciled 2026-09-01: the 2026-08-29 hygiene trim carried the pre-fix bracket forward while the body already recorded the fix]

**(1) `icon-off-orbit`, deterministic, 131.22 deg.** `V7T-minmus-ts-arrival` reds
`PARSEK-FAIL(anomaly)` on both of its flights (`2026-08-08_1614`, `_1616`) with
one Tier-C raise, identical to the decimal:

    [MapRenderTrace] phase=Anomaly surface=ProtoIcon pid=1830757804 frame=6791
      currentUT=1345355.000 reason=icon-off-orbit angleIconVsOrbitEff=131.22
      angleEffVsLive=0.00 loopShift=0.0 | lonIcon=-57.59 lonOrbitEff=169.32
      lonOrbitLive=169.32 | iconR=2212724 orbitEffR=2212724
      | lineActive=True inc=7.304 sma=-32531 ecc=4.0269 body=Minmus

READ THAT AS AN EXCERPT, NOT A TRANSCRIPT. It is run `_1614`'s line with four
fields ELIDED for width - `recId=d1a91f6b6ea34deea44f64e08167c7c1`, the second
`effUT=1345355.000` that follows `currentUT=`, and `LAN=79.908 argPe=351.933`
between `inc=` and `sma=`. Two of the fields that ARE shown are PER-RUN and must
not be pinned: `pid=1830757804 frame=6791` here against `pid=2354385572
frame=6778` on run `_1616` (and `recId=` is the optimizer's split child, minted at
split time). Everything the finding rests on - `angleIconVsOrbitEff=131.22`,
`angleEffVsLive=0.00`, `iconR == orbitEffR`, `currentUT`, and the orbital elements
- is byte-identical across the two flights.

The effective and live orbits agree exactly (`angleEffVsLive=0.00`) and the icon
is at the correct RADIUS to the metre (`iconR == orbitEffR`), so this is a stale
ALONG-TRACK position on a correct conic, not a misplaced icon. It fires in the
FLIGHT half, ~2 s before any tracking-station ghost exists, on the single large
TimeJump that lands inside the Minmus capture leg. RULED OUT by the sibling
lanes: not "a large jump" (V7M's first bracket jump is the same ~268 ks move and
sweeps clean) and not "a jump across the SOI seam" (V6T does exactly that on the
Mun axis and sweeps clean). What is left is landing, in ONE move, INSIDE a long
high-eccentricity capture leg (Minmus ecc 4.03, occupied 6,092 s) rather than
before the seam or into Mun's ecc 1.67 leg transited in 44 s. Discriminating
experiment, named but not flown: a V7T variant that brackets its way to the same
UT. Owner: whoever owns `GhostOrbitIcon` / `MapRenderProbe`. The anomaly is
deliberately NOT added to that spec's `allowedAnomalies` - the lane stands red by
finding (V1-map-dwell-mun-orbit's precedent).

**(2) ~~Teardown NRE when a run ends inside watch mode.~~ THE PARSEK HALF IS FIXED
2026-08-29 (release-hygiene, R5 item 2); the stock/MechJeb cascade below is not
ours and stays unarmed.** `V7M-minmus-player-loop`
is the first run in the suite that quits while watch mode is active. Its reading
run (`2026-08-08_1613`) read `unityExceptions total=1` - the Parsek line below:

    NullReferenceException: FlightGlobals.get_ActiveVessel ()
      Parsek.WatchModeController.GetActiveVesselSafe ()
      Parsek.WatchModeController.RestoreCameraAfterWatchExit (Boolean)
      Parsek.WatchModeController.ExitWatchMode (Boolean, Boolean)
      Parsek.ParsekFlight.OnDestroy ()

i.e. `GetActiveVesselSafe` is not safe during scene teardown - `FlightGlobals` is
already gone by the time `OnDestroy` runs the watch-exit camera restore. Harmless
at shutdown (one line, after the last gameplay frame), report-only, not armed.
NOT worked around in the spec by exiting watch before `FlushAndQuit`: that would
hide the only run shape that reaches it.

**THE FIX (2026-08-29).** `GetActiveVesselSafe` caught the headless-host triple
(`SecurityException` / `MethodAccessException` / `MissingMethodException`) but not
`NullReferenceException`, which is what `FlightGlobals.ActiveVessel` throws once
`FlightGlobals.fetch` is destroyed. The read now sits behind
`WatchModeController.ReadActiveVesselGuarded(Func<Vessel>)` - a fourth arm for the
NRE that returns null and logs ONE Verbose `[CameraFollow] GetActiveVesselSafe:
FlightGlobals unavailable (scene teardown)` line. Verbose, not Warn: quitting
while watching a ghost is normal, and both callers
(`RestoreCameraAfterWatchExit`, `RestoreCameraToAnchorVessel`) already treat a
null active vessel as "no camera target to restore". The delegate seam exists so
all four arms are drivable headlessly - no test host can make the real
`FlightGlobals` throw on demand - and the actual read stays in a
`[MethodImpl(NoInlining)]` core per the mono JIT convention. Four cells in
`WatchModeControllerTests` (NRE arm logs + returns null and raises no
WARN/ERROR; the triple stays silent; null reader; an unexpected exception still
propagates).

SIBLING SWEEP, deliberately narrow. `WatchModeController.Runtime.cs` uses the
same catch triple in six more helpers, but only two of them are ON this teardown
path: `GetFlightCameraSafe` (reads the `FlightCamera.fetch` static FIELD, which
yields Unity's fake-null rather than throwing) and
`RemoveWatchModeControlLockSafe` (`InputLockManager`'s lock stack is an
inline-initialised static). The genuine shape-alikes -
`GetCurrentUTSafe` (`Planetarium.fetch`) and `GetCurrentWarpRateSafe`
(`TimeWarp.fetch`) - dereference a singleton the same way and would throw the
same NRE, but they are reached only from per-frame Update paths that no longer
run once `OnDestroy` has fired, so nothing has ever observed them there and
widening them now would be a speculative sweep. File them here rather than fix
them blind: if a future reading run ever prints an NRE from either, the fix is
one more arm on the same pattern.

**THE FIX UN-SHADOWS ~20 LINES OF TEARDOWN CLEANUP, and that is the part to hold
in mind when reading the next census.** The NRE did not stop at
`GetActiveVesselSafe` - it escaped `watchMode.ExitWatchMode()` at
`ParsekFlight.cs:2183` and Unity abandoned the rest of `OnDestroy`. So in the
end-inside-watch shape ONLY, everything below that line has never once executed:
the `InputLockManager.RemoveControlLock` safety net, `vesselGhoster.CleanupAll`,
`GhostMapPresence.RemoveAllGhostVessels("scene-cleanup")`, the engine camera-event
unsubscribe, `policy.Dispose` / `engine.Dispose`, the ParsekFlight-local cache
clears, and `ui.Cleanup`. All of it now runs for the first time, at a moment when
`FlightGlobals.fetch` is already gone.

CONSEQUENCE FOR THE CENSUS BELOW: a re-measured `unityExceptions` reading may show
NEW Parsek lines that were never reachable before, and they are NOT a regression
of this fix - they are newly-reached code meeting an absent `FlightGlobals`. Read
any new teardown line as a NEW finding on its own call site, not as evidence that
the NRE catch failed (the catch's own line is Verbose and carries the words
`FlightGlobals unavailable (scene teardown)`, so the two are trivially told apart).
`RemoveAllGhostVessels` is the likeliest new speaker and is already the
best-defended: it wraps each ghost's `Die()` in its own try/catch with a `Warn`,
inside a `BeginGhostTeardown`/`finally EndGhostTeardown` scope that exists exactly
because the stock `SpaceTracking` rebuild it triggers can throw during teardown
(`GhostMapPresence.cs:482`) - so its failure mode is a Warn line, not an escape.

DELIBERATELY NOT GUARDED, observed-first: none of the newly-reached calls got a
prophylactic try/catch in this pass. `CleanupAll` only `Destroy`s GameObjects and
touches no KSP singleton; `RemoveAllGhostVessels` is already guarded as above; the
rest are dictionary clears and event unsubscribes. Wrapping them blind would be
precisely the speculative sweep declined for the sibling helpers one paragraph up,
and would pre-empt the very reading that would tell us which of them actually
speaks. If a census does surface one, fix THAT call site.

FUTURE WORK, NOT DONE HERE: this removes the one Parsek line from the
end-inside-watch teardown, which is the precondition for arming an
`[expectations.unityExceptions] maxTotal` ceiling on the V-family lanes. It is
NOT sufficient on its own - the 1-vs-5 spread below is four STOCK/MechJeb NREs in
the same 40 ms window, and the un-shadowed cleanup above can add lines of its own,
so any ceiling still has to be pinned against a re-measured census (the OLD Parsek
line should be absent from it; anything new needs classifying before it is
counted), not against the readings above. Arming is an operator decision after
that reading run; nothing is armed by this fix.

The ARMED re-flight of the identical shape (`2026-08-08_1642`) read `total=5`,
which is worth recording because it bounds what a ceiling here would mean: the
same Parsek line plus FOUR stock/MechJeb teardown NREs in the same 40 ms window
(`CrewHatchController.DespawnUIs`, two in `KnowledgeBase.OnMapFocusChange` fired
off `PlanetariumCamera.OnVesselDestroy`, and `MechJebCore.OnDestroy`) - a
knock-on cascade of the same end-inside-watch teardown, not four more Parsek
defects. A 1-vs-5 spread across two green flights of one spec is exactly why no
`[expectations.unityExceptions] maxTotal` is armed on this lane, and why arming
one off either single observation would have been wrong in one direction.

---

## HARNESS-TIER-TAXONOMY: `tier` encodes cadence membership, not cost or readiness, so "run everything" needs out-of-band knowledge [RAISED 2026-08-02 by the `V1-map-dwell-mun-orbit` promotion (PR #1407). NOT STARTED. Design change against a binding authority; the shape below is a problem statement, not a chosen solution]

The invocation model we actually want is agent-driven: an agent runs the WHOLE
batch on request, or a cheap smoke subset. The selection layer is built around
scheduled cadences instead, and the two do not line up.

### What exists today, measured not assumed

`run.py` selects by `--id | --tier | --tag | --cadence` (no other selector).
`hlib.TIERS` is `("perpr", "daily", "nightly", "weekly", "pending-fixture",
"operator")` - four cadence memberships plus two EXCLUSIONS - and
`CADENCE_TIERS` is cumulative:

| cadence | tiers it resolves to |
|---|---|
| `per-pr` | `perpr` |
| `daily` | `daily` |
| `nightly` | `daily`, `nightly` |
| `weekly` | `perpr`, `daily`, `nightly`, `weekly` |

with an explicit note in `hlib` that `operator` is in NO set: those specs "are
never picked up by a cadence and run only under an explicit `--tier operator` /
`--id`". Spec counts on 2026-08-02: nightly 34, daily 22, operator 5,
`pending-fixture` 0, `weekly` 0.

### The friction, stated precisely

1. **"Run everything" is three invocations plus knowledge.** `--cadence weekly`
   is the closest thing and it IS nearly-all, but it silently omits the
   `operator` and `pending-fixture` tiers, so the full set is
   `--cadence weekly` + `--tier operator` + `--tier pending-fixture`. Nothing
   tells a caller that; you have to know `CADENCE_TIERS` leaves them out. There
   is no `--all`.
2. **There is no smoke set.** One spec is named `H13-ksp-api-smoke` and a
   `SMOKE-autopilot` fixture exists, but no tag or selector means "cheap, fast,
   proves the stack is alive". The 18 tags in use are subject-matter labels
   (`mechjeb`, `rewind`, `eva`, `mun`), not cost labels.
3. **The data to select on already exists and nothing selects on it.**
   `coverage/duration.json` (tracked) holds per-scenario `last/p50/p95`;
   `coverage/flake.json` (gitignored, advisory) holds the trust signal. A
   cost-aware or trust-aware selection is a read away and is not wired.
4. **`tier` therefore does double duty** - it is read as a cost/readiness label
   by humans (see every spec's TIER header) while functioning as cadence
   membership. `operator` is not "a human runs it"; it is "no cadence will pick
   this up", which is why a green scenario can sit invisible to automation.

### Why it bites, with the case that raised it

`V1-map-dwell-mun-orbit` sat at `tier = "operator"` while GREEN and fully
automated, invisible to every cadence, until a human promoted it on 2026-08-02.
Nothing failed; nothing could. That is the same class
`PendingOperatorTagHonestyTests` already documents for `EVA-1-pad-flag` ("the
tier stays nightly until the operator promotes it" - a pending human call with
no signal that could go red), and the same reason that check exists as two
hand-maintained lists.

### Constraints for whoever takes this

- `docs/dev/design-autotest-harness-core.md` (M-A5) is the BINDING authority for
  the selection layer; section 10 owns the cadence -> tier map. Change the doc in
  the same commit.
- `hlib.TIERS` is validated by `validate_spec`, so the vocabulary cannot be
  narrowed without a spec sweep.
- `PendingOperatorTagHonestyTests` pins the `pending-operator` carrier set in
  BOTH directions AND covers the `tier = "operator"` population, so collapsing
  or renaming the tier vocabulary reds it until that inventory is re-reasoned -
  by design.
- `harness/lib/test_doc_spec_sync.py` reads `docs/dev`, so spec/doc drift is
  caught there.
- Additive beats destructive: an `--all` selector and a cost tag can land
  without touching `TIERS` at all, which is the cheap first step.

### What done looks like

An agent can ask for "everything" or "the smokes" in ONE invocation with no
out-of-band knowledge, and selection can use the cost and trust data the harness
already records. Whether `tier` then shrinks to a pure cost/readiness label or
disappears is the open design call, not a foregone one.

[2026-08-05] Invocation is now AGENT-DRIVEN ON REQUEST: the operator asks for a tier by type, and an agent flies it via `harness/tools/tier_runner.py --tier {daily|nightly|operator}` following the `run-tier` project skill (`.claude/skills/run-tier/SKILL.md`; mechanics in `harness/README.md` -> "Running a tier on request (agent-driven)"). That is the direction this item's "the invocation model we actually want is agent-driven" line points, and it removes the out-of-band memory for the three per-tier invocations - but it is still only packaging: it adds no `--all`, no smoke selector, no cost or trust-aware selection, and touches neither `TIERS` nor `CADENCE_TIERS`. The taxonomy question above stands unchanged.

## DEV-INSTANCE-UNLOCKED: shared-state races outside the machine lock, accepted as tracked limitations [FOUND 2026-08-02 by the multi-agent exclusivity audit. DELIBERATELY NOT FIXED by the machine-lock PR]

The machine lock (`<umbrella>/automation/.ksp-machine.lock`) serializes `run.py`
and `provision.py`. It deliberately does NOT cover the DEV instance or routine dev
commands, because locking those would serialize all parallel dev work behind an
8h harness hold to protect surfaces where a collision costs a re-run, not a false
product verdict. Filing them so they stop being invisible:

1. **`dotnet build` races on the shared dev DLL.** Two worktrees building
   concurrently both copy into `Kerbal Space Program/GameData/Parsek/Plugins/Parsek.dll`
   (`Source/Parsek/Parsek.csproj`, `ContinueOnError="true"`); last writer wins, and
   the `SyncAgentInstructionMirrors` target races the same way on the two umbrella
   `CLAUDE.md` / `AGENTS.md` mirrors. Mitigation today is the hash-verify recipe in
   `.claude/CLAUDE.md`, which is operator discipline, not a guarantee.
2. **The `InjectAllRecordings` fixture purges the dev save's recordings.**
   `SyntheticRecordingTests.InjectAllRecordings` targets the dev KSP install's
   `saves/test career` and calls `PurgeRecordingSidecars`. It DOES refuse when KSP
   is live (`ScenarioWriter.TryPurgeRecordingSidecarsForInject` probes `KSP.log`
   with an exclusive open, and the test raises `SkipException`), and it early-returns
   when the target save is absent - so the residual hazard is test-vs-test, not
   test-vs-flight: two sibling worktrees running `dotnet test` at once both purge and
   re-inject the same save. It carries `[Trait("Category", "Manual")]`, which does not
   exclude it from a bare `dotnet test`. Cheap fix if it ever bites: an env-var
   `Skip` gate.
3. **`collect-logs.py` collides on minute-granularity folder names** in the shared
   `../logs/` sink and hard-exits, which loses a failing run's only evidence. A
   `-pid<N>` suffix instead of `sys.exit(1)` would close it. (Note the harness's own
   `results/` half of this class was closed separately by HARNESS-RUNID-COLLISION
   below; this is the `../logs/` sink, which still hard-exits.)
4. **The lock key is the umbrella root, not truly the machine.** Runs given
   different `--umbrella-root` values, or started from a checkout with a different
   parent (a nested `.claude/worktrees/<name>` isolation worktree), resolve different
   lockfiles while still sharing kRPC 50000/50001 and the GPU; `--instance-dir`
   bypasses umbrella resolution altogether. Deliberate - a machine-global path would
   serialize the unit suites against real runs - but it means "machine-wide" holds by
   convention for the documented sibling layout, not by construction.

Related coupling, recorded here because it is easy to miss: **the deferred
residual R8 "`_ksp_running_against` coarseness"
(`docs/dev/design-autotest-stack-setup.md:740`) must not narrow the zombie
preflight without re-reading `harness/README.md` "The machine lock"** - that
coarse probe is a second, independent guard on kRPC port 50000 and the single
GPU. Note this is NOT the R8 in `autotest-roadmap.md`, which is an unrelated
scenario-coverage item.

---

## R7-SESSION-BATCH-ISOLATION: running the `Rewind` category beside a LIVE re-fly session breaks seven of its own tests, and starves nine more [FOUND 2026-08-04 by roadmap R7's abandoned `R7b-rewind-session-live` spec. FOUR defect-items FIXED here across THREE tests ((1)+(2) in `F5MidReFlyResume`, (2b), (3)); the FOUR remaining failing tests in (4) are RECORDED, not fixed. The nine starved are collateral of (2b), explained below - the spec is NOT committed]

R7 set out to drive `Rewind` in both of its precondition modes. R7a (no live
session) shipped green. The second spec - arm a REAL session with the seam's
`InvokeRewind`, then run the category inside it, so the twelve marker-dependent
members stop skipping - did NOT ship, and the reason is worth writing down
because it is a property of the tests, not of the fixture.

**Four flights, and every one taught something.** All measured on
`career-pad-craft` + `rewind-crew-loss` + `InvokeRewind rp=rp_cl_root slot=1`,
merge dialog deliberately unanswered:

| Flight | Tally | What it found |
|---|---|---|
| `2026-08-04_1333` | `passed=14 failed=1 skipped=22` | `F5MidReFlyResume` FAILED - see (1) |
| `2026-08-04_1641` | `passed=15 failed=1 skipped=21` | after (1)+(2): `KerbalRecoveryOnSupersede` reached its core assertion for the FIRST TIME EVER and failed - see (3) |
| `2026-08-04_1644` | `passed=15 failed=1 skipped=21` | a `StartRecording` step does NOT bind to the re-fly provisional; refusal reason unchanged |
| `2026-08-04_1646` | `passed=10 failed=4 skipped=23` | after (3): four MORE tests reached their assertions and failed - see (4) |

**(1) `F5MidReFlyResume` asserted a total-count proxy. FIXED.** Its assertion
compared `RecordingStore.CommittedRecordings.Count` before and after
`LoadTimeSweep.Run()`, while its own comment said it meant "any Immutable /
CommittedProvisional entries". The total is a proxy that holds only while the
test's synthetic provisional is the sole NotCommitted entry. Beside a real
session there are two, the test installs its own marker, and the sweep CORRECTLY
discards the now-orphaned real one (`Zombie discarded rec=...
supersedeTarget=cl-pod-a`). Product was right; the assertion was reading a
correct discard as a failure. Now counts settled (non-NotCommitted) recordings.

**(2) `F5MidReFlyResume` is destructive beside a real session. FIXED.** Even with
(1), the sweep still reaps the real provisional, and restoring the marker
afterwards cannot un-discard the recording - so every later marker-dependent test
finds a marker whose `ActiveReFlyRecordingId` resolves to nothing, which is an
`InGameAssert.IsNotNull` FAIL rather than a skip. It now SKIPS when a foreign
session is live, naming the context per the house rule.

**(2b) `JournalFinisherMarkerPresentVariant` ATE the session. FIXED.** It borrows
the live marker, runs the finisher (which clears the marker - that is the
asserted behaviour), restores the prior JOURNAL, and never restores the MARKER.
It had no `try`/`finally` at all, unlike every sibling in the family. That single
omission starved NINE later members, which skipped on "No active re-fly session"
having been reachable moments earlier (`ContractTombstonesAcrossSupersede` and
`InvokeRPStripAndActivate` both ran ahead of it and saw the marker). Now restores
in a `finally`, after the assertions, so nothing is weakened.

**(3) `KerbalRecoveryOnSupersede` asserted against an unmet precondition. FIXED.**
This is the test the status doc records as having AUTO-SKIPPED SINCE IT WAS
WRITTEN for want of a kerbal death in a supersede subtree. The crew-loss fixture
finally gave it one - `found 1 kerbal-death action(s) covering 1 kerbal(s)` - and
it then failed on "KerbalAssignment+Dead action must be tombstoned after merge".
NOT a product defect: the merge logged `AppendRelations
outcome=refused-unflown-provisional ... reason=empty Points -- the re-fly attempt
has no playable trajectory, so it cannot replace the origin`, then `Tombstoned 0
career actions`. A spec that arms a session without FLYING it cannot produce a
supersede, and without a supersede there is nothing to tombstone. The test now
skips on an unflown provisional; on CL-3's flown shape (which measures
`Tombstoned 1 career actions (... Kerbal=1 ...)`) the guard is false and every
assertion runs unchanged.

**(4) NOT FIXED - four more of the same family, and why the spec was abandoned.**
With (3) no longer consuming the session through a real `CommitSupersede`, four
further members reached their assertions and failed:
`MergeInterruptionRecovery` ("Expected supersede relations to be durable at
Durable1Done; got 0" - the same unflown-provisional root cause as (3)),
`DiscardReFly_PrelaunchContext_DispatchesEditorWithFacility`
("DiscardReFlyLoadGameForTesting should fire exactly once"),
`ReFlyRevertDialog_Prelaunch_BlocksStockRevert_AndShowsDialog` ("Prelaunch body
copy should mention VAB") and
`DiscardReFly_LaunchContext_PreservesSiblingState_DispatchesSpaceCenter`
("Marker should be cleared after Discard Re-fly"). All four install their OWN
synthetic marker and drive a GLOBAL handler, then assert on global state a real
session perturbs. All four PASS in R7a, where no session exists.

THE GENERAL SHAPE: roughly a third of this category is written as
"install synthetic session state, drive the handler, assert" and is only correct
when it is the only session in the process. That is a reasonable thing for a
test invoked by hand from Ctrl+Shift+T to assume and a wrong thing for a batch
run inside a live session. Fixing them one at a time is real work with a real
risk of weakening assertions, and it was out of proportion to the remaining
yield - R7a already executes 16 members, and the session-live mode's best
measured result was 15.

WHAT A FUTURE ATTEMPT SHOULD DO DIFFERENTLY, in order:
1. FLY the re-fly. Every unflown-provisional failure above disappears if the
   provisional carries Points; that is a mission-driven spec (CL-3's shape), not
   a seam-only one. `StartRecording` after `InvokeRewind` does NOT work - flight
   `2026-08-04_1644` measured the refusal reason unchanged.
2. Then give the four in (4) the same foreign-live-session skip guard (2) got.
3. Expect the ceiling to stay near R7a's: the first member to perform a real
   merge ends the session for everyone after it, which is inherent, not a defect.

---

## GS-WAVE-DEFERRED: two gameplay-scenario-wave decisions recorded only in PR bodies until now [RAISED 2026-08-05, gameplay-scenarios wave 1 (PR #1425) + follow-ups (PR #1427). DECISIONS, NOT DEFECTS - recorded here so the PR bodies are not the only durable trace]

**1. S20 flight variant deferred (uncontrollable booster -> no RewindPoint).**
Research doc section 8 S20: a booster with parachutes but NO probe core fails the
controllable-subject gate at the split, so no RP is authored
(`TryAuthorRewindPointForBreakup` logs `Single-controllable split: no RP`). The
VERDICT is unit-covered (`SegmentBoundaryLogic.IsMultiControllableSplit` = count
>= 2) and GS-1's spec header documents the mechanism as the reason its booster
carries a probeCoreOcto2.v2. The deferred piece is only the dedicated FLIGHT: it
would need a second craft variant (build_gs1_craft.py minus the booster core), a
second forge run and a committed fixture, for a single logContract token on an
already-unit-proven negative. Build it only if a wave wants the D9 no-RP cell
flown live; the craft builder is parameterizable for it.

**2. One unreproduced missions/lib suite failure during wave 1.**
A single `python -m unittest discover -s missions/lib` run red with failures=1
mid-wave (between the GS-2 negative-control revert and its commit,
2026-08-05); the very next run and 4+ consecutive runs after were green
(1419-1424 OK), the failing cell name was never captured (output tail cut it),
and it never reproduced. Recorded per the honesty rule: if a missions/lib cell
ever flakes again, this is the prior sighting; suspect environment (temp seam
dirs) before code.

## HARNESS-SHELL-READSET-UNCHECKED: a mission shell can read a telemetry field its own control flags never populate, and nothing catches it until a flight [RAISED 2026-08-05 by GS-2 flight 1, which was lost to exactly this. IDEA, NOT STARTED - recorded with its cost so a future wave can decide rather than rediscover]

**What happened, as the concrete instance.** `gs2_orbital_probe_deploy`'s DEPLOY
gate read `TelemetrySnapshot.vessel_count`, and its shell built the control with
`read_docking=False`. That field is populated ONLY inside `if self._read_docking:`
(`mission_runner.py:678-686`), so it sat at its `0` unread sentinel for the whole
flight and the gate compared two sentinels. The shell even carried a comment
asserting the field was "in the base snapshot" - a runner-population claim that was
simply false, written by hand and checked by nobody. One flight lost
(`2026-08-05_0842`).

**Why the existing discipline did not catch it.** Every opt-in channel is carefully
documented AT THE FIELD (`crew_count` says "Read only when the control was built
with `read_crew=True`"), and `forge_lko`'s shell documents its own opt-in and why.
The convention is good. What is missing is that nothing MECHANICALLY relates the
two: a shell's flags and its machine's read-set are written in different files, by
different authors, at different times, and drift silently.

**The shape of the check.** For each mission shell: derive the set of snapshot
fields its machine actually reads, derive the set the runner populates under that
shell's control flags, and assert the first is a subset of the second. It would
have caught this at unit time, and it covers every future shell for free.

**Why it is not built.** The population side is easy (the flags are literals in
`make_control`, and the runner's opt-in blocks are greppable). The READ side is
not: "which snapshot fields does machine X read" needs either an AST walk over
functions resolved per mission - fragile, since machines are named by convention
rather than registered - or a declared read-set per machine, which is a new
contract every mission must maintain and can itself go stale. For a three-lane
wave that is more machinery than the bug costs. The judgement to revisit: **if the
mission library grows past ~5 lanes, or the next shell adds a fourth opt-in flag,
build it.**

**The cheap 80% in the meantime**, which costs nothing and is where a future author
should start: a cell asserting that any shell whose machine mentions a
population-gated field sets the matching flag, for the SHORT closed list of gated
fields (`vessel_count`/`read_docking`, `crew_count`/`read_crew`,
`craft_chute_state`/`read_chute`, `node_executor_enabled`/`read_node_executor`,
`time_to_periapsis`/`read_periapsis`, the landing block/`read_landing`,
`read_camera`). That list is short, stable, and already written down at each field.

**Blast radius of the instance, verified rather than assumed** (Lane A, 2026-08-05):
`gs2_orbital_probe_deploy` was the ONLY `read_docking=False` shell reading
`vessel_count`. Every consumer of `mlib.separation_evidence`
(`bdock_dock_transfer`, `forge_lko`) sets the flag, as do b11/b12/b13/b14/b16 and
v1; GS-1 reads the field nowhere. And the instance was a CAN-NEVER-SUCCEED hazard
rather than a silent-wrong-pass: every consumer compares `current > baseline` with
the baseline captured from the same always-0 field, so an unread channel yields
`0 > 0` and fails closed. See the trap note on the field itself for the shapes
(`== 0`, `< N`) that would NOT fail closed.

---

## R7-FIXTURE-GAPS: five `Rewind` in-game tests can never execute on any committed fixture [FOUND 2026-08-04 by roadmap R7's per-test skip-precondition read. GAP 2 FIXED 2026-08-04 and CONFIRMED the same day: R7c re-flown with rewind-b9 injected, `UnfinishedFlightsRenderingAndNoHide` EXECUTED and PASSED (run `2026-08-04_1617`, tally `passed=5 skipped=32`), and the four armed/adjacent fixture consumers confirmed unmoved (S4.1 + CL-3 armed gates green, S1.5 green, all attempt 1). `InvokeRPStripAndActivate` stays skipped in R7a DELIBERATELY: it performs a destructive RP strip+activate mid-batch beside 15 other executing cells - the emergent-batch-mutation class the R7-SESSION-BATCH-ISOLATION entry documents - so unblocking it wants its own reading pass, not a fixture swap. GAP 1 re-measured and REFRAMED, still open]

R7 wired the `Rewind` category (37 declarations, the largest previously undriven
one) as three specs. Reading every body's skip guards to size those specs turned
up two gaps that no choice of committed fixture, injection preset or driver step
can close. Recorded so the next wave does not re-derive them, and so the R7 pins'
`skipped=` values are legible rather than mysterious.

**Gap 1 - no committed fixture has a two-command-pod craft (3 tests).**

> **CORRECTION 2026-08-04 - the measurement below is WRONG, and the gap is
> smaller and differently shaped than it says.** The original read counted
> `ModuleCommand` on three templates only (`career-pad-craft`, `b2-lko-craft`,
> `gloops-airshow`) and generalised "every one of them". A mechanical per-VESSEL
> scan of ALL twelve committed saves finds **two** whose ACTIVE vessel already
> carries two `ModuleCommand` parts: `bdock-station-pad` and `eva3-pad-3crew`,
> both flying the same 86-part SANDBOX `Kerbal X` (`mk1-3pod` at part 0 and
> `probeStackLarge` at part 12, separated by the `Decoupler.2` at part 11,
> `istg = 2`). Both sides of that decoupler are controllable, so the craft IS the
> "pod + decoupler + probe core" stack the fix line below asks someone to author.
> All three tests therefore clear their FIRST guard (`commandModules < 2` ->
> `Skip("Needs 2+ command pods")`) on those two templates today.
>
> What they fail is the SECOND guard: each test calls
> `StageManager.ActivateNextStage()` exactly once and then skips unless the
> loaded controllable-vessel count went up. Both saves sit at `sit = PRELAUNCH`
> with `stg = 7`, so one press fires `istg = 6` (Mainsail + boosters + launch
> clamps), four presses short of the `istg = 2` decouple. **The missing fixture
> material is not a craft - it is a save whose staging pointer is AT the split.**
>
> Two routes, neither implemented here (scouted only):
> 1. **Produce it, do not hand-edit it.** Fly the existing `Kerbal X` to just
>    before the stage-2 decouple and snapshot - the same route that produced
>    `eva2-lko-crewed` (which is literally the post-split state: a `Kerbal X` and
>    a separate controllable `Kerbal X Probe`). No hand-authoring, no craft-file
>    consistency risk.
> 2. **Hand-edit `stg` on a copy of `bdock-station-pad`** (`stg = 7` -> `stg = 3`)
>    so one press fires `istg = 2`. Cheap to try, but carries one unverified
>    assumption - whether KSP honours a persisted `stg` for a PRELAUNCH vessel or
>    re-derives the stage pointer at flight start - and the split then happens
>    with the stack still clamped to the pad. That is survivable for these three
>    (all are `AllowBatchExecution = false` isolated-run with
>    `RestoreBatchFlightBaselineAfterExecution = true`), because they assert on
>    the RP + PID maps + quicksave file, not on the vessel surviving.
>
> The "edit `career-pad-craft`'s 18-part craft" idea in the fix line is the
> EXPENSIVE route and should not be taken now that the craft exists elsewhere.
> Its concrete blockers, for the record: `attN` / `parent` references are
> POSITIONAL part indices, so inserting a part mid-stack means renumbering every
> downstream reference; `uid` / `persistentId` must stay unique (and are
> craft-baked, see the `persistentId` gotcha); `position` / `rotation` /
> attach-node naming must be authored by hand with no editor to validate them;
> `istg` has to be assigned consistently with the vessel's `stg`; and
> `career-pad-craft` is a CAREER save, so `probeStackLarge` would also have to be
> unlocked in its tech tree. The two SANDBOX templates have none of those
> problems.

The original (superseded) measurement:

`CaptureRPOnStaging`, `SavePathRootThenMove` and `WarpZeroedDuringSave` each need
an ACTIVE vessel carrying >= 2 parts with a `ModuleCommand` whose NEXT STAGE
decouples a second CONTROLLABLE vessel - that is the shape Rewind-to-Separation
captures an RP at. Measured across the committed templates: `career-pad-craft`
18 parts / 1 `ModuleCommand`, `b2-lko-craft` 81 parts / 1, `gloops-airshow` 9
parts / 1. Every one of them has exactly ONE command pod, so all three tests skip
on all three, and they skip in R7a and R7b alike.
Fix: author a two-pod stack craft (pod + decoupler + probe core, both
controllable) as a new fixture template. That is the same work `bdock-station-*`
did for docking and it would close all three at once.

> **UPDATE 2026-08-05 - the craft in that fix line now EXISTS, and so does a route
> to author one.** The GS-1 gameplay-scenario lane needed exactly that stack (its
> own reason: a staging split authors a RewindPoint only when it is
> MULTI-CONTROLLABLE) and shipped two things this entry can reuse:
>
> - `harness/tools/build_gs1_craft.py` - the harness's FIRST craft-authoring route.
>   It answers, mechanically, every blocker the paragraph above lists as the reason
>   hand-editing is expensive: positions are DERIVED (child.pos = parent.pos +
>   parent_node_offset - child_opposite_node_offset, with the effective node offsets
>   read off stock craft `attN` tokens rather than computed from part cfgs, and the
>   formula validated against Jumping Flea and Orbiter One), radial placement comes
>   from an azimuth-to-quaternion formula validated against Jumping Flea's three fins
>   exactly, part ids and `persistentId`s are generated unique, every `MODULE` block
>   is lifted BYTE-FOR-BYTE from a stock KSP craft (so no module-index mismatch can
>   be authored in), and `--check` plus a byte-identity rebuild cell wire the whole
>   thing into the unit suite.
> - `harness/fixtures/saves/bdock-forge-base/Ships/VAB/GS1 Auto-Chute Booster.craft` -
>   mk1pod.v2 + Mk16 / `Decoupler.1` / probeCoreOcto2.v2 + FL-T200 + LV-T45 + six
>   Mk2-R + three fins, 15 parts. THREE stages: istg 2 ignites, istg 1 fires the
>   decoupler AND arms the booster chutes, istg 0 is the upper chute.
>
> WHAT IT DOES AND DOES NOT CLOSE, stated exactly so nobody over-reads it. It clears
> the FIRST guard (two `ModuleCommand` parts either side of a decoupler) trivially.
> It does NOT clear the SECOND guard as produced: the forge leaves the fixture at
> `stg = 3` PRELAUNCH, so ONE `ActivateNextStage()` fires istg 2 (ignition), not the
> split. What it does is make ROUTE 2 above cheap and low-risk: a copy of
> `gs1-two-stage-pad` with `stg = 3` -> `stg = 2` puts the staging pointer AT the
> split, carrying the same single unverified assumption (whether KSP honours a
> persisted `stg` for a PRELAUNCH vessel or re-derives it) - now against a 15-part
> stack instead of an 86-part Kerbal X, and against a booster that carries its own
> chutes, so the post-split state is survivable rather than a clamped-to-the-pad
> wreck. The fixture EXISTS as of 2026-08-05 (`FORGE-gs1-two-stage` flew and was
> harvested with `harvest_bdock_station.py --target-name gs1-two-stage-pad`), and
> GS-1's own first flight CONFIRMED that the craft splits into two controllables and
> authors a RewindPoint: `Controllable split children: [2200110033,4093180920]
> (checked=2, unresolved=0)` -> `RewindPoint begin: ... slots=2 controllablePids=2`.
> So the "author a two-pod stack craft" fix line is DISCHARGED as far as the craft
> goes. The `stg = 3 -> 2` edit that would put the staging pointer AT the split is
> still not implemented; it is scouted only.

**~~Gap 2 - `ScenarioWriter` emits no `mergeState`, so nothing can be an Unfinished
Flight (2 tests).~~ RESOLVED-PENDING-RE-FLY 2026-08-04.**
`UnfinishedFlightsRenderingAndNoHide` (SPACECENTER) and `InvokeRPStripAndActivate`
(FLIGHT) both need at least one recording satisfying
`EffectiveState.IsUnfinishedFlight`. ~~which requires `MergeState` to be `Immutable`
or `CommittedProvisional`.~~ **That reading was one predicate short**, and the
correction matters for anyone re-deriving this: `UnfinishedFlightClassifier.TryQualify`
does accept Immutable OR CommittedProvisional, but `TryResolveUnfinishedFlightRaw`
then runs `IsSlotEffectiveTipOpen`, which admits **only CommittedProvisional** - an
Immutable tip is a CLOSED (sealed) slot and rejects as `sealedTipClosed`. So the
default was never merely "not authored", it was actively the closed state.
Grepping `Source/Parsek.Tests/Generators/` for `MergeState` / `mergeState` /
`CommittedProvisional` returned ZERO hits, so no preset - `all-synthetic`,
`rewind-b9` or `rewind-crew-loss` - could satisfy the predicate. This is why R7c
injects nothing.

Fixed by three edits, all in the fixture generators (no product change):
- `RecordingBuilder.WithMergeState(MergeState)` - OPT-IN, default unset. A builder
  that never calls it keeps `Recording`'s own `Immutable` default and serializes
  byte-identically.
- `ScenarioWriter.BuildRecording` stamps the FIELD, so the wire format is the
  production one: `RecordingTree.Save` -> `RecordingTreeRecordCodec
  .SaveRewindToStagingMergeState` writes `mergeState = <enum name>` (omitted for
  Immutable) and `LoadRecordingFrom` parses it back with `Enum.TryParse`. No key is
  hand-written in the generator.
- `RewindB9Fixture`'s crashed booster and `RewindCrewLossFixture`'s crewed pod -
  the slot-1 re-fly targets - author `CommittedProvisional`, which is the state
  production reaches through `RecordingStore.ApplyRewindProvisionalMergeStates`
  (the CommitTree promotion). That pass runs at LIVE tree-commit time and needs
  `tree.BranchPoints`, so an already-committed injected tree never passes through
  it and must author the state directly.

Compatibility: the four committed specs injecting `rewind-b9` (S1.5, S4.1, R1, V1)
and the one injecting `rewind-crew-loss` (CL-3) were re-read against the change.
Every MergeState consumer on their paths special-cases only `NotCommitted`
(`EffectiveState.IsVisible` / closure / tip walking, `SupersedeCommit
.AppendRelations`' row-write guard, `LoadTimeSweep`'s zombie discard), so no gated
value can move: S4.1's ARMED `supersedeRows max = 0` / `tombstones max = 0` and
CL-3's ARMED `min = 1` floors are all decided downstream of predicates blind to the
Immutable/CommittedProvisional distinction, and `saveparse` evaluates only DECLARED
keys. The one real behavioural delta is that S4.1's un-superseded booster now keeps
its slot OPEN, so `rp_b9_root` is no longer reap-eligible at merge - S4.1 measured
`rewindPoints = 0` but deliberately does NOT declare that key, and its own comment
says so.

Headless proof: `RewindB9FixtureTests.Inject_BoosterMergeStateRoundTripsThroughTheProductionCodec`
(codec round-trip + the other two rows unchanged) and
`Inject_CrashedBoosterClassifiesAsOpenUnfinishedFlight` (the booster classifies,
the focus-slot upper stage does not, and flipping the booster back to Immutable
un-classifies it - the negative control that pins the authored key as load-bearing).

REMAINING: re-fly R7c with `injectedRecordings = "rewind-b9"` and re-pin its
`BATCH_COMPLETE` tally + `[expectations.recordings] count` (currently `min = 0,
max = 0`, which an injected corpus breaks). Until that lands, R7c is unchanged and
the cell still skips at runtime.
Note this was the same SHAPE as the `CrewReservationLive` blocker in the inventory
doc's bucket B1 (a generator that cannot author the state a category gates on).

Neither gap is a product defect: the tests are correct and the production code
they exercise is reachable in a real game. What is missing is fixture material.

---

## HARNESS-ANOMALY-SWEEP-DECORATIVE-WHEN-TRACERS-OFF: with tracers-off as the baseline, most specs' `allowedAnomalies = []` can never bite [RECORDED 2026-07-28 from the retrospective review of PRs #1345-#1363. DELIBERATE TRADE, DEFERRED - recorded so the coverage gap is not assumed closed]

### What happens

PR #1352 made tracers-off the deterministic instance baseline (`run.py` writes it at stage AND teardown), because S1.4's `mapRenderTracing=true` had been leaking instance-wide into every later run. Since then, a scenario that wants a tracer arms it with its own `SetSetting` step - and only S1.4 / S1.6 / S1.7 do (checked 2026-07-28: 3 of the 55 committed spec files set `mapRenderTracing`; none set `ledgerTracing`).

The Tier-C anomaly sweep (`hlib._anomaly_reasons`, ~line 3254) matches the tracers' raise shape (`phase=Anomaly ... reason=<token>`), and both raisers (`MapRenderTrace.EmitAnomaly`, `LedgerTrace.FormatAnomaly`) are behind those settings. So on every committed spec except S1.4/S1.6/S1.7, the sweep can raise NOTHING: the `allowedAnomalies = []` those specs all carry is decorative, and their `anomalySweep` verifier row is vacuously green - it proves the tracers were off, not that no anomaly occurred.

### Why it stays as-is for now

The alternative (tracer-on everywhere) is exactly the cross-run leak PR #1352 fixed, plus per-frame tracer cost on every flight. The trade is deliberate and deferred, not forgotten. What this entry pins: do NOT read a green `anomalySweep` row on a non-tracer spec as anomaly coverage, and if a future lane wants Tier-C coverage on a spec, the spec must arm the tracer itself (and expect the S1.4-class run-cost).

---

## Autotest coverage: build-order TODOs from the basics roadmap [TODO, branch `autotest-roadmap`]

Rationale, dependency justification, measured costs and the full uncovered-cell
breakdown are in `docs/dev/autotest-roadmap.md`. Do not restate them here; these are
the actionable units only. Coverage ground truth they are all sized against:
`hlib.compute_coverage` over the 38 committed specs returned 241 values / 83 covered /
158 uncovered, with D1 at 13 of 18 uncovered, D13 at 11 of 11, D17 at 6 of 6.
(**84 covered / 157 uncovered** since R1's debris gate landed 2026-07-27, D3 6 -> 5.
**Re-measured 2026-07-28 at `7f5efa738`: 55 specs, 242 values / 97 covered / 145
uncovered** - the denominator moved because CL-1 added D12 `crew-death-in-flight`
per the growth rule. D1 is 8 of 18 uncovered, D13 7 of 11, D17 still 6 of 6.
Status of the items below at that HEAD: R1 SHIPPED (#1362), R2 STILL OPEN (both
phantom cells verified present), R3 partly overtaken (#1357 re-tier +
R1-EMPTY-PROVISIONAL resolved as a fixture artifact; first green nightly rows
for S1.5/S4.1 still unconfirmed), R4 mostly shipped (#1358 + H21; FinalizeLimbo
and Bug289 remain), R5 SHIPPED (#1367), R11 done. Re-measure rather than
trusting this line; it is a snapshot, not a maintained total.)

**R1. Gate the recording surfaces the B-lane already produces on every nightly.**
~~Debris population on the five Kerbal X flights~~ DONE 2026-07-27 (branch
`claude/docs-pr-testing-tasks-f4bor2`); the rest below is still open.

**Shipped.** `B2-lko-ascent`, `B4-reentry-splashdown`, `B5-mun-flyby`,
`B6-minmus-flyby` and `B7-duna-flyby` all fly `fixtures/saves/b2-lko-craft` (the
stock Kerbal X), shed six radial boosters, and record each as a parent-anchored
debris child - while their windows read `count = { min = 1, max = 8|9 }`, so the
total loss of that population read PASS. Each now requires a debris-creation token
and a per-mission `count.min`. D3 `parent-anchored-debris` claimed on all five;
coverage 83 -> 84 of 241, D3 6 -> 5 uncovered. Proof: the next nightly, no new
flight. A red is a real finding - re-pin `count` to the newly measured value and
record which recordings the run produced; do NOT widen back toward 1.

**THE FIRST CUT SHIPPED THE WRONG TOKEN AND WOULD HAVE RED ALL FIVE.** Caught by
independent review before merge; recorded because the mistake is repeatable.
`Child recording created \(debris, TTL=` (`BackgroundRecorder.cs:1177`) was pinned
as "the SOLE creation site". It is one of TWO, and it is the BACKGROUND-split one:
reachable only via `OnBackgroundPartJointBreak`, which early-returns unless the
vessel is in `tree.BackgroundMap`, and `RecordingTree.IsBackgroundMapEligible`
excludes `rec.RecordingId == ActiveRecordingId`. These craft shed boosters while
ACTIVE, so it cannot fire; staging goes through `ParsekFlight.ProcessBreakupEvent`
-> `CreateBreakupChildRecording` -> `ProcessBreakupEvent: debris child created:
pid=` (Info, tag `Coalescer`, `ParsekFlight.cs:7668`). Cost had it merged: ~3.8 h
of flying and five reds that read as a Parsek recording regression. **Rule:
presence in source is not reachability on a profile. Trace the call chain or grep
an archived KSP.log before pinning** - the discipline this entry already
prescribed for the tokens it declined to claim.

The intermediate fix accepted EITHER site, because the trace above was argued from
source and never confirmed against a log. **The shipped gate requires the
FOREGROUND token alone**, settled 2026-07-27 by grepping all 60 archived B-lane
`logs/*/KSP.log` folders (B2 10, B4 8, B5 27, B6 6, B7 9): the foreground token
appears in 58 of 60, and the substring `Child recording created` - broad enough to
catch the CONTROLLED sibling at `:1185` as well - appears in **zero**. The two
without it are `2026-07-20_{1846,1854}_B2-lko-ascent`, both INVALID runs that
recorded nothing. A green ascent emits it exactly 6 times, one per booster.

**`min` is per-mission, and the rule is not the flameout watchdog.** The floor
follows whether the mission commands a debris-producing stage drop BEYOND launch
ignition. `B5`/`B6`/`B7` drop a flameout-staged core (`_b5_flameout_stage`, reached
only from `b5_decide`) -> 8. `B4` drops its service stage via an
`ACTION_ACTIVATE_STAGE` on the SOLE transition into `B4_REENTRY` -> also 8, and
`run.py` judges expectations only on a driver-valid non-short-circuited run, so a
B4 that never reached REENTRY is SKIPPED rather than judged under a lower floor.
Only `B2` stages once, at ignition -> 7 (structural: `mlib.py:401-405` records that
the spent core never autostages, because MechJeb autostage fires only on EMPTY
stages and the Kerbal X core keeps residual fuel).

**Every floor is now MEASURED, not derived.** Read 2026-07-27 off
`verifiers.expectations.observed.recordings.count` in verdict=PASS result JSONs
(the field landed in `72cf344fb`, 2026-07-25 06:48, so every citation is from that
morning): B2 7 (`2026-07-25_0824`), B4 8 (`0828`), B5 8 (`0643`, `0847`), B6 8
(`0636`, `0856`), B7 8 (`0916_a2`). Both previously-unmeasured floors - B4's
structural derivation and B6's inference from the shared decide function -
measured at exactly what was derived, so no re-pin was needed.
**Do not re-derive these counts from the archived `logs/*/` folders.** `run.py`
collects logs on NON-PASS only (`run.py:2324`), so every archived B-lane folder is
a run whose expectations were SKIPPED rather than judged; their `.prec` sidecars
number 7 for B4 and B6 because those runs aborted before the extra stage drop.
Reading that 7 as a contradiction and lowering the floor would re-open the exact
hole this entry exists to close.
The first cut used 7 everywhere; the second kept B4 at 7 by keying on
`_b5_flameout_stage` alone. Both left the exact value `B11`/`B12` record as
**considered and rejected**: "7 is the exact count a single dropped recording would
produce, so a floor of 7 would blind the only numeric guard on this run to the
regression class it exists to catch."

**Corrections to the roadmap's R1 section, found by building it:**
- **The tokens are NOT all `ParsekLog.Verbose`.** All four `BackgroundRecorder`
  population tokens (`Child recording created (debris, TTL=` :1177,
  `(controlled, no TTL):` :1185, `Debris TTL expired, ending recording:` :1307,
  `Sample rate changed: pid=` :1966) are `ParsekLog.Info`, as is the foreground
  `ProcessBreakupEvent: debris child created:`. The rest of that table is MIXED,
  not uniformly Verbose: `starting hysteresis timer` is Verbose at both sites, and
  the `Part event:` family is ~39 Verbose sites plus exactly one Info
  (`FlightRecorder.cs:3862`) once `BackgroundRecorder.PartEventPolling.cs` is
  counted too. Check the level per token, never per family.
- **The target list named the wrong specs.** It listed B11/B12/B13/B14, which
  already carry `{8,8}` pins AND eight-token contracts (B11 even requires
  `terminalState=Destroyed`, gating debris terminals), and omitted B5/B6/B7,
  which were vacuous. The systemic-vacuity table added in the same PR is the
  correct list; the Targets line predates it.

**Still open.** D5 `staging-debris-ttl` and D2 `proximity-cadence-bg` are
UNCOVERED and produced on every B-lane run. Their tokens exist
(`Debris TTL expired, ending recording:` `BackgroundRecorder.cs:1307`;
`Sample rate changed: pid=` `:1966`, both Info) but neither was claimed here
because neither is structurally guaranteed the way creation is:
`DebrisTTLSeconds = 60.0` is short relative to a booster's fall, which makes TTL
expiry LIKELY but not certain - a booster destroyed by reentry before the 60 s
elapses ends its recording through a different reason. Claiming on "likely" is
what the roadmap's own rule forbids. Cheapest close: grep an archived B-lane
KSP.log for both tokens, then claim in a follow-up. `B1-pad-hop` ({1,6}) and
`BDOCK-1` ({2,20}) also still carry main-recording-only floors, but neither flies
the Kerbal X so neither inherits this evidence: B1's breakup-child count is
documented as genuinely per-run variable, and BDOCK-1's window spans two trees and
is commented "never tightened". Both want their own measurement, not this one.
Rule, unchanged: one token per claimed class, and never loosen a token to keep a
claim.

**R2. Two registry cells cannot be honestly claimed as written. Decide before anyone
claims against them.**
`harness/coverage/registry.toml` D1 `stop-on-switch` describes a decision that does
not exist: `FlightRecorder.VesselSwitchDecision` is `{None, ContinueOnEva,
ChainToVessel, DockMerge, UndockSwitch, TransitionToBackground, PromoteFromBackground}`
with no Stop member (always-tree mode removed it). D3 `surface-body-fixed` does not
name a `ReferenceFrame` member either: the enum has exactly `Absolute`, `Relative`,
`OrbitalCheckpoint`, and the only production symbol carrying that meaning is
`TrackSection.bodyFixedFrames`, a Relative-section sub-surface which therefore overlaps
`parent-anchored-debris`.
Build: delete each cell or redefine it against a real symbol, with the rationale in the
registry comment. The coverage denominator moves, so do it before the next snapshot.

**R3. Run S1.5 and S4.1 unattended; their operator-tier premise looks stale.**
Both are `tier = "operator"` (excluded from every cadence, never run) on the stated
rationale that the verbs are `RequiresFlight` and "run unattended from the gloops
SPACECENTER host every verb only DEFERS to a TIMEOUT"
(`S1.5-rewind-loop.toml:3-8`, `S4.1-rewind-merge.toml:3-9`). Contradicting evidence,
all measured: both use `saveTemplate = "fixtures/saves/gloops-airshow"`; that fixture
carries `activeVessel = 1` with 2 VESSEL nodes so
`TestCommandLoadGame.DecideLoadRoute` returns `Focusable`, which boots to FLIGHT;
`S1.4-injected-playback` pins `category=GhostPlayback scene=FLIGHT` and
`H5-invariants-corpus` pins `category=RecordingInvariants scene=FLIGHT` on that exact
template; S0.5 / S0.6 / EVA-1 drive `RequiresFlight` verbs to green PASSes on it; and
S1.5's other stated blocker, the TimeJump completion-decider fix on branch
`autotest-integration-fixes`, MERGED as PR #1322 (commit `eb94607dd`).
Build: `python harness/run.py --id S1.5-rewind-loop` then `--id S4.1-rewind-merge`,
operator-scheduled, not on a cadence. Do NOT relax either spec's asserts to force a
pass; re-tier only on an honest green.
Why it matters: 15 of 16 D9 cells have no live proof (7 are claimed only by these two
never-run specs; the one with live proof is `reconciliation-bundle` via H6). Two boots
either turn 7 nominal cells real plus D6 `time-jump` and D8 `epoch-isolation`, or name
the real blocker, which nobody currently has.
Known residual risk to check first: `RewindInvoker.CanInvoke`'s five preconditions
include a deep parse of the RP quicksave, and `rewind-b9` writes that quicksave
synthetically; that path has never executed live.

**R4. Drive the D1 finalization family. Five specs, five boots, no code.**
`IncompleteBallistic` (8 tests), `FinalizeBackfill` (7), `RecordingFinalization` (3),
`FinalizeLimbo` (2), `Bug289` (2) are all FLIGHT-scene, all `AllowBatchExecution`
default-true, and nothing drives any of them. Closes D1 `scene-exit-finalization`,
`ballistic-extrapolation`, `finalization-cache`.
Build: five specs on the `harness/scenarios/H5-invariants-corpus.toml` template over
`gloops-airshow`. One category per spec is REQUIRED, not a choice:
`hlib.SINGLE_BATCH_SELECTOR_RULE` (`hlib.py:691`, enforced at `:2158-2190`) rejects a
second `RunTests` step and rejects a multi-category selector, for
`driver.autorun.tests` as well as `driver.steps`.
Proof: pin the WHOLE `BATCH_COMPLETE` tally from the first green run, never
`passed=[1-9][0-9]*`. These categories self-skip on fixture conditions and a
`failed=0` pin over an all-skipped batch is the vacuity defect that was already found
and closed once. Each spec must say in prose that it gates the DECISION layer in a
live KSP process, not a flown situation (the `M1-mission-loop-unit` precedent).
Independent of R3 and R5; buildable in parallel with both.

**~~R5. `RunTests` cannot reach 68 already-written tests. Add an `isolated` argument.~~**
DONE 2026-07-27. This was the largest single unlock in the roadmap. `InGameTestRunner` has a second
batch entry point, `PrepareBatchExecutionIncludingFlightRestore`, which also admits
`test.RestoreBatchFlightBaselineAfterExecution` and restores a flight baseline after
each test. `RunAllIncludingFlightRestore` / `RunCategoryIncludingFlightRestore` are
public and fully implemented (`InGameTestRunner.cs:389,420`) and are called from
exactly two INTERACTIVE places: `TestRunnerShortcut.cs:395,463` (the Ctrl+Shift+T
window) and `UI/TestRunnerUI.cs:249,375`. Neither unattended path calls them: the
seam's `RunTests` (`ParsekTestCommandAddon.cs:1494,1496`) and the autorun dispatcher
(`TestRunnerShortcut.cs:725,739,789`) both call only `RunAll()` / `RunCategory(cat)`.
Measured over `[InGameTest(...)]` argument lists: 68 tests carry
`AllowBatchExecution = false` AND `RestoreBatchFlightBaselineAfterExecution = true`,
i.e. their authors already decided a quickload-baseline restore makes them batch-safe.
By category: Logistics 38, AutoRecord 10, Rewind 6, Coalescer 2, MergeDialog 2,
QuickloadResume 2, SceneExitMerge 2, LogisticsGrapple 1, MapRender 1, TrackingStation
1, GhostPlayback 1, RevertFlow 1, PlaybackControl 1.
Twenty-six of those sit in D1-D9 categories nothing drives, and they are the ONLY
producer of D1 `auto-record-first-mod-switch` / `commit-scene-exit` /
`commit-revert-merge`, D5 `controlled-decoupled-child` / `crash-coalescing`, and D9
`rewind-to-launch`. No fixture, no mission profile and no existing verb produces them.
~~Build: `RunTests` gains an `isolated` arg routing to `RunCategoryIncludingFlightRestore`;
mirror it in the autorun selector; add the hlib spec-validation companion; land one
shakedown spec.~~ All four shipped. The autorun mirror is a separate env var,
`PARSEK_AUTORUN_ISOLATED=1`, NOT a selector prefix: the selector string is consumed
verbatim as the `category=` token both the runner stamps and
`hlib._batch_probe_categories` builds its anti-vacuity probe family from, so a prefix
would desynchronize those two copies of one name, every probe would miss on a token
mismatch, and a contract that rejects all probes is what the gate reads as SAFE. Full
argument in `design-autotest-autorun-hooks.md` "H1 - Isolated batches".

Proof, CORRECTED: the shakedown spec `H21-scene-exit-merge-isolated` pins
`total=2 passed=2 failed=0 skipped=0 category=SceneExitMerge scene=FLIGHT`. The
non-isolated form does NOT yield `total=0` as this entry originally claimed -
`PrepareBatchExecution` sets `Status = Skipped` on the tests it filters rather than
dropping them, and `BATCH_COMPLETE`'s `total` is `allTests.Count(Status != NotRun)`, so
it yields `total=2 passed=0 failed=0 skipped=2`. `total` is therefore identical on both
paths and the discriminator is the passed/skipped split. That makes the proof stronger,
not weaker: `passed=0, failed=0, total==skipped` is precisely the one-parameter vacuity
family the anti-vacuity gate enumerates, so the gate already guarantees the pin rejects
the non-isolated line, and `IsolatedBatchWiringGroupTests` asserts that rejection
explicitly rather than inheriting it.

Two things the build turned up that this entry did not anticipate:

- The hlib companion was LOAD-BEARING, not a nicety. `InGameTestDecl` did not carry
  `RestoreBatchFlightBaselineAfterExecution` at all and `derive_batch_tally` hardcoded
  the ordinary filter, so `CommittedBatchTallySourceSyncTests` would have REJECTED a
  correct isolated pin, deriving `executable = 0`.
- The FIXTURE was the expensive trap. Both `SceneExitMerge` cells stage the active
  vessel and wait for it to leave PRELAUNCH and clear 80 m on a 30 s deadline. The
  H7-H20 fixture `gloops-airshow` has a 1-part `mk1-capsule` with ZERO `ModuleEngines`,
  so on it both self-skip and print `total=2 passed=0 failed=0 skipped=2` - the SAME
  line the non-isolated failure produces. The spec uses `b2-lko-craft` (73-part stock
  launcher, 8 engines, PRELAUNCH) and a new gate asserts the PRELAUNCH + non-zero-engine
  property statically.

FLOWN: H21 PASSED on attempt 1, 2026-07-27, 101 s wall (29.6 s of it the batch),
matching its pinned tally token for token. Both questions the derivation could not
answer came back favourable: the launcher clears 80 m inside the 30 s deadline, and
the post-test baseline quickload returns the vessel to FLIGHT in PRELAUNCH so test A's
situation guard does not fire. Coverage 96 -> 97 covered, the new cell being D1
`commit-scene-exit`.

REMAINING (follow-on, not R5): the other 12 unlocked categories are now ordinary
spec-authoring work. `AutoRecord` (10) is the one to size carefully - ten
launch-and-restore cycles in one boot - but H21 measured a restore cycle at well
under 15 s, so the earlier fear of a 10-test isolated batch being unaffordable looks
overstated.

**R6-R8. Drive the reachable in-game categories.** 539 `[InGameTest]` declarations
exist in 97 categories; specs drive 8 categories / 125 declarations; 414 declarations
in 89 categories run only when a human presses Ctrl+Shift+T. 82 of those 89 categories
are fully reachable today on existing fixtures (FLIGHT / SPACECENTER / scene-agnostic
only); 7 involve TRACKSTATION or MAINMENU and have no seam route.
Ordering and per-category cell mapping are in the roadmap doc. The cheapest whole
dimension is D13 (11 of 11 uncovered, NOT capability-blocked): 29 tests already exist
and self-site off `FlightGlobals.ActiveVessel` - 26 `Scene = FLIGHT` plus 3
scene-agnostic (`SpawnHealth`), so all 29 run in a FLIGHT batch (`SpawnRotation` 10,
`TerrainClearance` 6, `SpawnHealth` 3, `SpawnTerminalOrbit` 3, `SpawnCollision` 2,
`Spawner` 2, `EvaSpawnPosition` 2, `Pipeline-Terrain` 1), and `gloops-airshow` routes
to FLIGHT.
Correction to carry: `Logistics` is 47 tests but 38 carry
`AllowBatchExecution = false`, so only 9 are batch-reachable before R5 lands.

**R9-R14. Machinery items** (each self-contained in the roadmap doc). TWO OF THE SIX
ARE NOW CLOSED; the R9-R14 bullets below are kept intact with the closures marked,
because the counts around them were measured against the six-item shape. One
NON-R bullet (the CL-2 stage-B calibration reading) is appended after R9 as
supporting measurement for R9's remaining scope - it is not a seventh machinery
item and must not be counted as one:

- **R9** structural save-content expectations plus landing the three inert `route` /
  `rewind` / `loop` expectation blocks - **HARNESS HALF SHIPPED-REPORT-ONLY
  2026-07-31** (branch `claude/r9-save-parse-verifier-tshhzv`), then **ARMED ON S4.1
  THE SAME DAY** (branch `r9-arm-s41`): the M-C2 save-parse verifier
  (`harness/lib/saveparse.py` + the `saveParse` chain row) evaluates
  `[expectations.rewind]` and the new `[expectations.recordings.structure]` block
  over the produced save's ParsekScenario surfaces. The gating PROMOTION is DONE for
  S4.1 - it is the first and only committed spec carrying `gating = true`, and
  `test_no_committed_spec_arms_gating` became an explicit allowlist pinning exactly
  `{"S4.1-rewind-merge.toml"}` so a second spec arming still needs a deliberate edit.
  Three live runs did it: `2026-07-31_1628` (report-only reading, PASS, all readings
  inside the declared `max = 0` windows), `2026-07-31_1635` (armed, PASS,
  `armedBlocks=["rewind"]`), and a NEGATIVE CONTROL that temporarily flipped
  `supersedeRows` to `min = 1` and reddened `2026-07-31_1637`
  `PARSEK-FAIL(save-structure)` with `mismatches=["rewind.supersedeRows 0 < min 1"]`
  - the gate is seen to fail, not merely assumed to work. STILL OPEN inside R9: CL-2
  stage B's windows (calibration numbers recorded below), `route`/`loop` (still
  reserved - zero declarers), and the analyzer-PR half. See the roadmap R9 entry.

  ANSWERED BY THE READING RUN: the merge REAPS `rp_b9_root`. `rewindPoints` measured
  0 on all three runs, and the produced save carries no `REWIND_POINTS` node at all
  (the empty-staging-list-writes-no-parent quirk). This was a genuine open question -
  it is recorded, deliberately NOT pinned as a window: one observation is not a
  window, and a reap count wants a second scenario before it gates.

- **CL-2 stage-B calibration, measured off the STAGE-A run** (CL-2 itself IS stage A;
  stage B is unwritten) [MEASURED 2026-07-31, run
  `2026-07-31_1641_CL-2-pod-impact-ledger` (PASS, 168 s)]. CL-2 declares NO M-C2
  block (`blocks: []`), so its `saveParse` row is pure measurement - which is exactly
  what makes it usable to size stage B's windows. The observed block, verbatim:

  ```
  "rewind":   { "supersedeRows": 0, "tombstones": 0,
                "rewindPoints": 0, "rewindRetirements": 0 }
  "recordings": { "structure": {
      "trees": 1, "committedTrees": 1, "recordings": 1,
      "terminalStates": { "Destroyed": 1 },
      "branchPoints": {}, "duplicateRecordingIds": [] } }
  ```

  READ THESE CORRECTLY. This is stage A - the fatal pod hop and its ledger rows, with
  NO rewind anywhere in the run. So the `structure` numbers are the PRE-REWIND corpus
  baseline stage B starts from (one committed tree, one Destroyed recording), and the
  `rewind` numbers are all zero because nothing rewound. Stage B rewinds ACROSS
  CL-1's crew loss, so its `[expectations.rewind]` is expected to move to
  `supersedeRows >= 1` / `tombstones >= 1` and its `structure` counts to grow by the
  re-fly fork. Pinning stage B's windows straight off this block would assert the
  absence of the very thing stage B exists to exercise: author stage B report-only
  first, read ITS facets, then arm - the same three-run promotion S4.1 just went
  through. Stage B scope: the R12 residue block in `docs/dev/autotest-roadmap.md`.
- **R10** runtime-handle plumbing so a live tree / vessel / route id can reach a verb
  (today `run.py:1157` substitutes exactly one token, `${runSave}`, and no response
  payload is ever captured) - OPEN. NOTE R12 solved the SPECIFIC instance that
  blocked it worst, without solving R10: `SimulateStockSwitchClick` takes `vessel=`
  (a stable NAME) precisely because a TOML author cannot know the pid a launch will
  mint, the same stable-addressing dodge `InvokeRewind` used. That is a per-verb
  workaround, not the general mechanism.
- **R11** a CAREER fixture with a flyable craft - ~~`FORGE-career-pad`; all three
  career-family fixtures currently have ZERO VESSEL nodes~~ **CLOSED 2026-07-28** by
  `harness/fixtures/saves/career-pad-craft`, built by construction rather than by a
  forge flight.
- **R12** `SimulateStockSwitchClick` plus a `scene=` argument on `LoadGame` -
  **SHIPPED 2026-07-30**, and it grew a third capability on the way:
  `ExitToSpaceCenter`, the live FLIGHT -> SPACECENTER transition, without which
  nothing could LEAVE flight either. Live-proven by `H23-tracking-station`,
  `S0.7-exit-auto-commit` and `S0.8-switch-click-segment`. The seam is 21 implemented
  verbs / 10 reserved. What it did NOT close is listed in the roadmap's R12 block -
  `site=ts` / `site=ksc`, the dialog cases, and unloaded targets. The CL-1 spec
  extension's stage A shipped 2026-07-30 as `CL-2-pod-impact-ledger`; its tombstone
  stage B remains.
- **R13** widening `SINGLE_BATCH_SELECTOR_RULE` to N categories with N pinned
  tallies - OPEN.
- **R14** provisioning `modded-compat` for D17 - ~~OPEN~~ **CLOSED 2026-08-04**
  (branch `modded-compat-lane`): `automation/modded-compat` provisioned live
  (exit=0, VERIFY drift=0; the profile gained the missing audio-silencing
  settings deltas on the first run's inspection, now pinned by
  `RealProfileFileTests.test_both_profiles_pin_the_unattended_settings`), and
  `MC-1-waterfall-compat` + `MC-2-restock-compat` flew the WaterfallCompat /
  ReStockCompat categories green there on attempt 1 (7/8 and 8/9 executed; the
  2 skips are the by-design inverse gates). D17 `waterfall-swe-fallback` +
  `restock` and D7 `engine-fx-waterfall-fallback` claimed. RESIDUE:
  `persistent-rotation` and `remotetech-commnet` stay source-blocked (GT-8 /
  not in the profile); `better-time-warp` and `making-history` have the
  instance but no committed spec; the FX-fingerprint A/B diff ran REPORT-ONLY
  and surfaced a corpus limitation filed as **T48 under TODO — Compatibility**
  (the synthetic corpus is trajectory-only for all but a handful of
  recordings, so a save-based A/B exercises ~1 engine key; a dedicated
  engine-showcase fixture with real vessel snapshots is the follow-up).

**Baseline caveat: three of these are already in flight.** Every count above was
measured at `1591aa59f` and EXCLUDES work open in review at the time of writing. PR
#1358 (`ingame-test-wiring`) wires 14 in-game categories as H7-H20, covering R4's
`IncompleteBallistic` / `FinalizeBackfill` / `RecordingFinalization`, R6's
`TrajectoryMath` / `Pipeline-Anchor` / `SwitchSegment` and R8's `SpawnRotation` /
`EvaSpawnPosition`, and moves the scenario count 38 -> 52; PR #1357
(`rewind-loop-lane`) re-tiers S1.5 and S4.1 to `nightly` on the same premise R3
argues; PR #1359 (`eva4-failopen`) fixes the EVA-4 fail-open. Re-measure with
`hlib.compute_coverage` / `hlib.parse_ingame_test_declarations` before acting on R3,
R4, R6 or R8 - do not treat a merged H7-H20 category as still-undriven work.

**Doc hygiene found while measuring, deliberately NOT edited (concurrent sessions own
those files).**
1. `docs/dev/autotest-status.md` contradicts itself on EVA-2. The EVA table row says
   "STILL pending-fixture: `eva2-lko-crewed` does not exist yet" while the section
   header above it says all four EVA scenarios are LIVE-PROVEN, Operator item 2 says
   both EVA fixtures were forged headlessly and committed, the fixture exists at
   `harness/fixtures/saves/eva2-lko-crewed/` with 7 VESSEL nodes, the spec reads
   `tier = "daily"`, and `harness/coverage/duration.json` carries a measured 57 s run.
   Fix: correct the two stale rows to match the rest of the file.
2. `harness/fixtures/saves/bdock-station-craft/` is an orphan: no spec LOADS it (no
   `saveTemplate` points at it). It IS named in a provenance comment at
   `BDOCK-1-station-interceptor.toml:97`, whose own `saveTemplate` is
   `bdock-station-pad`, and by `harness/tools/harvest_bdock_station.py` plus the
   design doc. Decide keep or delete; if delete, drop that comment reference with it.
3. `S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9` state a "gloops
   SPACECENTER host" premise that the `LoadRoute` contract contradicts. Correct the
   comment or replace it with an R3 measurement.

---

## ~~The harness anomaly token set has drifted from what the mod raises~~ [DEAD-TOKEN HALF FIXED 2026-07-29 branch `harness-fail-open-gates`; the NINE-UNGATED-REASONS HALF RESOLVED 2026-08-04 branch `arming-sweep` - per-token calls made: SEVEN PROMOTED into the gated set, TWO kept as report-only instruments. See the RESOLUTION paragraph below the table]

Found 2026-07-26 while anchoring the sweep above; the fix for that bug made this one visible rather than causing it. `hlib.ANOMALY_TOKENS` is described as the fixed harness-owned Tier-C set, but it no longer matches the `reason=` values the mod emits.

**FIXED HALF (2026-07-29).** The DEAD `icon-jump` token is REMOVED from `hlib.ANOMALY_TOKENS` and retired to `hlib.ANOMALY_TOKENS_DEAD`; the two tuples are now disjoint and the source-derived enumeration asserts a retired token is still raised by nothing, so one that gains a producer reds instead of quietly sitting ungated. The removal cannot move a verdict - a token no producer raises can never be a hit - which is exactly why it was safe to do without a calibration flight. What it buys is honesty: the gated set no longer advertises coverage of the icon-teleport family it has never been able to see. Shipped alongside it, a per-token COUNT BUDGET on `allowedAnomalies`:

```toml
[expectations]
allowedAnomalies = ["polyline-orbit-overlap", { token = "icon-teleport", maxCount = 3 }]
```

A bare string keeps its historical meaning (tolerated at ANY count) so all 55 committed specs parse unchanged; the table form reds at N+1. The two are different claims, and only the second catches a regression that turns a rare benign transient into a per-frame storm. `parse_allowed_anomalies` rejects a malformed entry pre-launch (a misspelled `maxcount` that silently degrades to unbudgeted is the fail-open the surface exists to close) and warns on an inert one. NO committed spec arms a budget; `anomalySweep.hitCounts` now records per-token raise counts on every run so one can be sized from a green flight instead of guessed.

**The other half stayed open until 2026-08-04:** the nine then-ungated reasons below, `icon-teleport` first among them. Nothing about the dead-token removal decided them; the arming sweep's measurements did (RESOLUTION below).

Ground truth, DERIVED FROM SOURCE (not hand-listed): `hlib.ANOMALY_REASONS_RAISED_UNGATED` carries the ungated half, and `AnomalyGroundTruthEnumerationTests` walks every `EmitAnomaly` call site under `Source/Parsek` excluding `InGameTests/`, resolves the reason argument by position for both tracer signatures, and requires the derived set to partition exactly into the gated tuple plus that one. So a new raise site nobody gates reds the harness suite instead of silently widening the fail-open.

| Raised reason | In ANOMALY_TOKENS? | Producer (decision site) |
|---|---|---|
| `parity-drift` | yes | `MapRenderProbe.cs:1531`, `:1787`, `:2422` (via `MapRenderTrace.AnomalyParityDrift`) |
| `line-blink` | yes | `MapRenderProbe.cs:896` |
| `decision-vs-truth` | yes | `MapRenderProbe.cs:689` |
| `polyline-orbit-overlap` | yes | `MapRenderProbe.cs:709` |
| `rigid-seam-tangent-discontinuity` | yes | `MapRender/CrossMemberSeamStitcher.cs:419` |
| `ledger-vs-truth` | yes | `GameActions/KspStatePatcher.cs` x6, `FacilityStatePatcher.cs:158` |
| `icon-teleport` | yes (promoted 2026-08-04) | `MapRenderProbe.cs:1079` |
| `icon-off-orbit` | yes (promoted 2026-08-04) | `MapRenderProbe.cs:1160` |
| `unaccounted-drawn-recording` | **NO** (report-only instrument) | `MapRenderProbe.cs:544` |
| `gap-vs-retire` | yes (promoted 2026-08-04) | `MapRender/GhostRenderReconciler.cs:240` |
| `decision-vs-old-truth` | yes (promoted 2026-08-04) | `MapRender/GhostRenderReconciler.cs:260` |
| `clock-not-ready` | yes (promoted 2026-08-04) | `MapRender/ShadowRenderDriver.cs:316` -> `MapRenderTrace.EmitClockNotReady` (`:1417`) |
| `retire-not-held` | yes (promoted 2026-08-04) | `MapRender/ShadowRenderDriver.cs:394` -> `MapRenderTrace.EmitRetireNotHeld` (`:1440`) |
| `anchor-resolve-fail` | yes (promoted 2026-08-04) | `MapRender/AnchorFrameResolver.cs:87` -> `MapRenderTrace.EmitAnchorResolveFail` (`:1465`) |
| `factory-parity` | **NO** (report-only instrument) | `MapRender/ShadowRenderDriver.cs:726` -> `MapRenderTrace.EmitFactoryParity` (`:1644`). POINTER CONVENTION, because this is the only live row the source-derived gate exempts by name (`wrapper_routed_pointer` in `test_hlib.py`): the raise is WRAPPER-ROUTED, so the C# `EmitAnomaly` scan attributes no call site to this reason and the pinned line is the **decision site** - the `if (!result.IsMatch)` guard inside `ShadowRenderDriver.AssertFactoryParity`. It is NOT the wrapper call's own line, and it is NOT obtained by shifting the previous pin: re-read the guard out of the source when re-pinning it |
| `seam-endpoint-outside-soi` | **NO** (report-only instrument, added with the ENCOUNTER-GEOMETRY lens) | `MapRenderProbe.cs:2361` (`TrySampleAndEmitSeamEndpoint`; decision core `MapRender/SeamEndpointOracle.cs`). READ THE PASS SUMMARY BEFORE READING THE SILENCE: `seam-endpoint summary evaluated=<n> outsideSoi=<n> skip.<reason>=<n>` (Verbose, `[Parsek][VERBOSE][MapRenderTrace]`, one per probe pass, 5 s rate-limited) says how many destination-approach checks actually ran; a zero-raise run with `evaluated=0` measured nothing at all. WHY REPORT-ONLY, because this one differs from the two instruments above: a raise WOULD be a real finding, and it took the same report-only first lap the seven promoted tokens each took. (This clause used to read "but the lens has never flown"; the 2026-08-09 census below retired that, and left the clause standing inside the very row that records the retirement. Corrected: flight is no longer a blocker - `hlib.ANOMALY_REASONS_RAISED_UNGATED`'s comment block names the three that are.) It measures the RENDERED conic at a recorded cross-body SOI handoff against the destination body's sphere - both terms propagated to the seam UT via `getTruePositionAtUT`, never a current-anchored position - and raises on `dist/soi > 1.005`. That tolerance is calibrated between two MEASURED populations: healthy = the S1.8 seam continuity, 10,146.3 m (Kerbin->Sun) and 7,284.0 m (Sun->Duna), i.e. 1.2e-4 / 1.5e-4 of the crossed sphere against a 25 km pin; defect = the 2026-06-15 looped re-aim, 1.027 (Duna) / 1.043 (Kerbin - a CALIBRATION reference only; that quantity is unproducible by the field capture, see limit (1) in the M-06 entry). KNOWN BENIGN POPULATION still to be measured: a FAITHFUL loop replay of an interplanetary transfer reads far above 1.0 by design (the destination has moved on in inertial space by the loop shift), so a raise needs the line's `seed=` / `loopShift=` fields read before it is called a defect. Deliberately NOT re-aim-gated - the whole point is that the parity oracle skips exactly those members. **FIRST REAL-GEOMETRY CENSUS 2026-08-09, and it FALSIFIED the offline derivation on two of five lanes** (full write-up + the UT arithmetic under the M-06 re-aim entry). The five V-lanes re-flown with the census on read: V4 `evaluated=1 outsideSoi=0` (Sun->Duna arrival seam - the geometry class the 1.027 defect lived in, measured INSIDE the sphere, on a frame where the faithful-parity sibling stood down `skip.reaimed-or-foreign-seed=1`), V7M `evaluated=1 outsideSoi=0` (Kerbin->Minmus arrival seam, faithful / phase-locked / same-parent, also inside), and V6M / V6T / V7T all `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1`. ZERO raises anywhere and no verdict moved (V7T's red is its own `icon-off-orbit` finding), so the report-only registration behaves. The lens is therefore NO LONGER UNPROVEN on real geometry - two healthy readings, each reproduced bit-identically on three consecutive flights, and `evaluated=[1-9]` is now REQUIRED on V4 + V7M. STILL NOT MEASURED, and both are why this stays report-only: the RATIO (printed only on a raise, so `outsideSoi=0` proves reach but not margin) and the RAISE itself |
| `loop-seam-teleport` | yes (gated at birth 2026-08-07, flight-arrival lane) | `ParsekFlight.cs` `TrackLoopSeamTeleport` -> `GhostRenderTrace.EmitAnomaly` (the third tracer signature; walker taught in the same change). SENSITIVITY, because silence gets cited as evidence: it raises on a SINGLE-FRAME world delta above `max(GhostRenderTrace.LoopSeamTeleportFloorMeters = 1,000,000 m, expected motion * dt * multiplier)`, so a clean sweep excludes discontinuities over 1,000 km between consecutive frames and nothing finer |

That WAS nine ungated reasons, not five (seven now gated per the RESOLUTION below; the table's per-row flags carry the current truth). **The first version of this table listed five**, and the four it missed are the wrapper-routed rows: the cutover-hardening raises, which reach `EmitAnomaly` through thin once-per-event `MapRenderTrace` wrappers instead of calling it at the guard site, so a grep for `EmitAnomaly` call sites does not land on them. They emit the same `phase=Anomaly ... reason=<token>` line as any direct raise (all four route through `MapRenderTrace`'s shared `EmitRaw(true, "Anomaly", ...)`), so all four were genuinely ungated then (three are promoted now; `factory-parity` stays the declared instrument). Understating the ungated count understates the size of the fail-open, which is the one thing this entry existed to size, hence the source-derived gate above. `clock-not-ready` in particular is the cold-load UT<=0 defer - a defect class this project already tracks separately.

And `icon-jump` WAS in the set but is raised by nothing - it is a DEAD token (RETIRED from the gated set 2026-07-29, see the FIXED HALF above). That one matters most: the icon-teleport family is precisely the defect class the map-render wave has spent months chasing, and the sweep has never been able to see it. Before the anchoring fix the token would occasionally "hit" by matching prose (`MapRenderHighWarpCanaryInGameTest`'s own description text contains `icon-jump`), which is a false positive dressed as coverage, not a gate. Retiring it removes the false advertisement; it does NOT add the coverage - that is the `icon-teleport` decision below.

The NINE remained unresolved through 2026-08-03, deliberately, because reconciling them is a per-token decision (defect signal vs instrumentation signal) rather than a mechanical rename. The considerations as they stood before the calls were made:
- `unaccounted-drawn-recording` is documented in `.claude/CLAUDE.md` as the S0 polyline-COVERAGE instrument, not a defect signal - gating it would red runs for an instrumentation gap.
- `factory-parity` is the same shape: a shadow-only PhaseFactory comparator that never drives a draw, so a fire is a build-bug signal, not a rendered regression.
- `gap-vs-retire` / `decision-vs-old-truth` / `icon-off-orbit` / `retire-not-held` / `anchor-resolve-fail` / `clock-not-ready` each need a call on whether a raise is a defect or an expected transient.
- `icon-teleport` is the one that most likely SHOULD be gated (renaming `icon-jump` -> `icon-teleport`). What blocks doing it here is that nobody knows whether it FIRES on a green run: the only tracer-armed scenario that walks the real 272-tree corpus is S1.4, and every S1.4 flight so far predates the `unlistedReasons` channel, so no archived result records whether an icon-teleport raise happened. Gating it blind could red a live-proven daily on the strength of a rename. S1.4's next nightly is the measurement - `anomalySweep.unlistedReasons` in its result JSON answers it for free - and the rename should follow that number, not precede it.

**What the deferral is NOT.** An earlier draft of this entry justified it with "adding any of them WIDENS the gate for every committed scenario at once, and several run with the tracer armed". That is no longer true after the sidecar baseline in this same change: exactly three specs armed the map tracer at the time (`S1.4`, `S1.6`, `S1.7` each carry `SetSetting mapRenderTracing=true`; V1 became the fourth when it landed) and the baseline pins it OFF for the rest, so every `MapRenderTrace` emit early-returns on `IsEnabled` elsewhere and widening the set can only move the tracer-armed scenarios' verdicts. The reasons above are the real ones; the blast-radius claim was overstated and is retracted here.

**RESOLUTION (2026-08-04, branch `arming-sweep`): the per-token calls, each with its measurement.** PROMOTED into `hlib.ANOMALY_TOKENS` (6 -> 13): `icon-teleport`, `icon-off-orbit`, `gap-vs-retire`, `decision-vs-old-truth`, `clock-not-ready`, `retire-not-held`, `anchor-resolve-fail`. The evidence is silence on EXERCISED geometry, which the historical corpus could not supply (its zeros were not-exercised zeros - see the sweep paragraph above): five V1 real-geometry dwells with ~130 nonzero-ghost probe frames each (`2026-07-30_1955`, `_2023`, `2026-08-01_1551`, `2026-08-02_1046`, fresh `2026-08-04_1250_a2`) raised none of the nine; 155 tracer-on historical runs raised none; and the S1.4 measurement this entry named as the decider arrived 2026-08-04 with the `unlistedReasons` channel live - run `2026-08-04_1228`, probe exercised, `hits=[] unlistedReasons=[]` - so `icon-teleport` promoted on exactly the number this entry said the rename should follow. KEPT REPORT-ONLY in `hlib.ANOMALY_REASONS_RAISED_UNGATED` (9 -> 2), for the instrument reasons the bullet list above already gave: `unaccounted-drawn-recording` (the S0 polyline-COVERAGE instrument - a raise is an instrumentation-coverage gap, and gating it would red a scenario for a probe bookkeeping miss, not a rendering defect) and `factory-parity` (a shadow-only comparator that never drives a draw - a fire is cutover diagnostics). No `maxCount` budget was armed for any token, promoted or kept: none of the seven fires at all on a healthy run, so the line-blink precedent (sharpen the predicate on the discriminating fact; a count budget cannot tell benign from defective at the same count) held without needing a budget anywhere. All four tracer-armed specs re-flew green under the widened gate on 2026-08-04. Cells: `test_the_ungated_count_is_nine_not_five` -> `test_the_ungated_count_is_two_instruments`; `test_icon_jump_is_retired_and_icon_teleport_is_still_only_reported` -> `..._is_now_gated`; new cells pin the promoted seven's membership and the wrapper-routed four's accounting; the enumeration partition test is unchanged and now proves the 13+2 split.

The report channel remains for the two kept instruments: `hlib.unlisted_anomaly_reasons` returns every raised reason absent from the set, run.py warn-logs it (`anomalySweep saw N raise(s) with reason(s) NOT in the harness token set (REPORT-ONLY, not gating)`) and records it in the result JSON under `anomalySweep.unlistedReasons`. Non-gating by construction. Pinned by `AnomalyGrepAnchoringTests.test_icon_jump_is_retired_and_icon_teleport_is_now_gated` (the `icon-teleport` decision this entry deferred was made 2026-08-04, so the cell was renamed and inverted rather than deleted - it now pins the retirement AND the promotion) and by `AnomalyGroundTruthEnumerationTests` (which stays, and is what keeps this table honest). The budget surface is pinned by `AnomalyBudgetParseTests` / `AnomalyBudgetSweepTests` / `AnomalyTokenCountTests`, plus a whole-set cell asserting no committed spec arms a `maxCount`.

## M-A5 - Harness core: the unattended orchestrator [LANDED, branch `autotest-harness`. **OPERATOR RUNBOOK DISCHARGED 2026-08-29 EXCEPT ONE STEP**: five of its six confirmations are evidenced many times over by the live corpus (a written-to `automation/stock-minimal` instance; 523 preserved run results, 311 PASS, 18 of them `tier=daily`; 97 runs carrying `BATCH_COMPLETE ... failed=0`; 392 carrying the recording-rules suppression; analyzer `red=0` against that instance; 504 collect-logs `_shots` snapshots; and tier running is now a ROUTINE agent operation via `harness/tools/tier_runner.py --tier`, which retires the block's own "an agent cannot pilot" premise). THE SIXTH IS NOT PROVEN AND STAYS OPEN: no live forced KILLED exists - zero KILLED artifacts across all preserved runs, `logValidate.killedRunMode` False on every run carrying the field, and the only KILLED proof is the fake-KSP wedge cell in `test_run_smoke.py`. That negative is bounded by a gitignored results corpus. See the annotated runbook below]

- ~~The external Python orchestrator (`harness/run.py`) that ties M-A1/M-A2/M-A3/M-A6 into an unattended pipeline: select scenarios, admit the instance, stage the fixture, launch KSP with the scenario env, drive the seam under a wall-clock budget (timeout -> process-tree kill -> KILLED, never a hang), run the verifier chain, classify into the plan's verdict taxonomy, snapshot diagnostics on failure, and record a per-run result + coverage ledger.~~ DONE (v1, seam-driven only; autopilot flight is M-B1). Design: `docs/dev/design-autotest-harness-core.md`.

**Shape:** pure decision library `harness/lib/hlib.py` (spec validation, selection, response-stream eval, verdict classification/retry/expected-fail overlay, expectations, log-validate profile selection, budget arithmetic, admission reuse over `provlib`, coverage/flake, result serialization + schema gate; 126 pytest-free unit tests) + thin I/O shell `harness/run.py` (all OS I/O behind an injectable `Runtime` seam; every decision delegated to hlib). Verdict enum `{PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS}` (XPASS added for an expected-fail scenario that unexpectedly passes; FLAKE dropped in favor of a `flakedThenPassed` note on a PASS). Two additive dev-script seams: `analyze-recordings.ps1 -FreshSaveGate` (programmatic analyzer Forbid, mutually exclusive with `-UseBaseline`/`-WriteBaseline`) and `validate-ksp-log.ps1 -KilledRun`/`-NoRecordingRun` (set `PARSEK_LIVE_SUPPRESS_RULES` to the marker-pairing rule codes; the C# `ParsekLogContractChecker.ParseSuppressionList` rejects any request to suppress FMT-001/FMT-002/WRN-001 - the cannot-mask guarantee). Fake-KSP smoke test (`harness/lib/test_run_smoke.py` + `_fake_ksp.py`) drives a full PASS + KILLED + boot-crash run through the shell with no real game, plus a direct stage_fixture containment test (a runSaveName that escapes `saves/` aborts before any rmtree).

**Adaptations (v1):** (1) admission projects the on-disk provision manifest as the expected baseline and substitutes only the deployed `Parsek.dll` sha as the substantive drift check, because the provisioner's live manifest content-hash recipe (`phase_deploy`) is not yet implemented; this detects POST-PROVISION CLOBBER only (the deployed DLL was changed AFTER the manifest was stamped), NOT a stale deploy (Parsek rebuilt in source but never redeployed, so the manifest and the deployed DLL still agree on the old hash) - stale-deploy detection needs the provisioner live hashing path and is deferred; the remaining fields admit as-recorded. (2) expected-fail signature matching supports an optional `expectedFail.subkind` that narrows the demotion to one PARSEK-FAIL class; when `subkind` is empty the match falls back to bugId-only (any PARSEK-FAIL on the scenario matches, warned at demotion time). (3) coverage/flake are refreshed IN-RUN at the end of a `run.py` invocation (`refresh_coverage_and_flake`) rather than by a standalone `coverage.py` module. (4) a retryable INVALID re-runs the WHOLE attempt (fresh stage + boot); subprocess-scoped retry (re-run only the wedged verifier subprocess, not a fresh boot) is deferred to M-A5.1.

**M-A5.1 follow-ups (harness-core, branch `autotest-ma51-followups`):**
- ~~Subprocess-scoped tooling retry (adaptation 4).~~ DONE. A wedged verifier subprocess (analyzer / log-validate tooling fault, NEVER a Parsek verdict - analyzer RED=1 is a verdict, analyzer CRASH is tooling) is re-run ONCE over the same already-produced save/log before the whole-attempt retry burns a fresh ~10-min boot; pure retry-scope classifier `hlib.classify_retry_scope`, both attempts' outcomes logged so a retry never masks nondeterminism, whole-attempt policy + INVALID taxonomy unchanged for everything else. **SF1 (review follow-up):** a subprocess-RECOVERED flake now (a) records a self-contained `verifiers.subprocessRetry` detail entry (`{stage, retried, attempt1, attempt2, recovered}`) in the durable result JSON so the recovery is auditable, and (b) accrues toward that scenario's flake/quarantine numerator via `hlib.flake_attempt_entries` (a synthetic INVALID alongside the PASS, mirroring a whole-attempt flakedThenPassed) - previously a recovered run wrote a single PASS JSON, so a chronically-wedging pwsh tool never reached the 20% quarantine threshold. **NIT 3:** the triage-only analyzer run on a driver-INVALID save (non-verdict) no longer subprocess-retries a wedged analyzer (pure waste over an already-INVALID save).
- ~~Multi-category BATCH_COMPLETE aggregate (design note N3).~~ DONE. A multi-category RunTests (`all` / `A,B`) is now gated on the `category=multi:<count>` aggregate line (union `failed=0` means EVERY category passed, defended against a mis-summarized aggregate) via pure `hlib.resolve_batch_complete`; a missing aggregate with per-category lines present reds batch-incomplete instead of silently passing off one category's per-category line. **SF2 (review follow-up):** the aggregate's `multi:<count>` is now cross-checked against the per-category line count via STRICT EQUALITY (the count IS the number of categories the autorun ran, so exactly that many per-category lines must be present) - a mismatch either way (a cut-off category batch OR an unexpected extra batch) reds `category_count_mismatch` (same treatment as `aggregate_missing`: `present=False`), never a silent pass off a mis-counted aggregate; this also un-deadens the previously-parsed-but-unread regex count group (NIT 2). Tests: pure hlib cells (`RetryScopeClassifierTests`, `MultiCategoryBatchCompleteTests`, `SubprocessRecoveredFlakeAccrualTests`) + fake-runtime smoke cells (`SubprocessScopedRetrySmokeTests`, `MultiCategoryBatchSmokeTests`).

**Operator runbook** ~~(pending, PENDING-OPERATOR)~~ **[DISCHARGED 2026-08-29 except the forced-KILLED step - see the header; the one sentence below that is now simply false is the "an agent cannot pilot" premise, retired by `tier_runner.py`]:** ~~the LIVE end-to-end path needs a provisioned instance + a real KSP, which an agent cannot pilot.~~ On a provisioned `stock-minimal` instance, run `python harness/run.py --tier daily` and confirm: the `[Harness]` admit/launch/verdict lines, a `BATCH_COMPLETE v1 ... failed=0` line, a `RED=0` analyzer header, the recording-rules log suppression on the no-recording B10 loop, a PASS `harness/results/<runId>.json`, and a coverage line; then force a KILLED (an over-budget scenario) and confirm the KILLED verdict + killed-run log mode + the collect-logs snapshot. This is the plan section 11 Phase 2 exit criterion.

## Added (headless-verified; the in-game pin is an AUTOMATED LANE, ~~awaiting its first flight~~ BOTH SPECS LIVE-PROVEN 2026-08-29) - Pre-Parsek save safety backup (branch `pre-parsek-save-backup`, lane `preparsek-backup-lane`) [PPB-1 `2026-08-29_1101` PASS attempt 1 (50 s) and PPB-2 `2026-08-29_1107` PASS attempt 1 (46 s) after `_1102` red on a SPEC authoring error rather than a product one; PR #1576. All four runbook properties are now automated surfaces and green - pristine timing (two independent pins), the Load-menu FILE SHAPE, idempotency, and the brand-new-empty skip. WHAT IS LEFT IS HUMAN AND IS NOT A PRODUCT QUESTION: (1) the Resume-Saved-Game eyeball - does the entry RENDER and read clearly, given the card shows the SOURCE save's title while the folder shows the timestamped name; (2) resuming the BACKUP folder itself (`Skip: reason=is-backup-folder`), unautomatable because the timestamped folder name is not addressable from a spec. The `operator` -> `daily` promotion is a cadence decision, not debt. Header reconciled 2026-09-01: the 2026-08-29 hygiene trim carried the pre-flight header forward while the FLIGHT LEDGER below already recorded all three runs]

**Feature.** The first time Parsek cold-loads a save with no Parsek footprint, it copies that save - before any Parsek write - into a sibling `saves/<Name> (pre-Parsek <local-ts>)/` folder that appears in KSP's Load menu, so a player who tries Parsek and uninstalls it can return to their pristine career. Runs once per save; skips brand-new empty careers. Unconditional since the 2026-08-27 settings simplification (the `autoBackupExistingSaves` toggle was removed; delete the backup folder if unwanted).

**Design.** Hook at the top of the cold-load path of `ParsekScenario.OnLoad` (gated `!initialLoadDone`, before `LoadExternalFiles`): a scenario module's `OnSave` cannot precede its own `OnLoad`, so the copied `persistent.sfs` is gameplay-state-pristine (no Parsek funds/science/crew/tech/contract/facility footprint - NOT byte-identical; the empty `SCENARIO{name=ParsekScenario}` KSP injects carries no gameplay data). Idempotency is measured from the on-disk footprint (`Parsek/` dir or a populated `ParsekScenario` node), not the in-memory OnLoad node, so a prior aborted session is caught; the marker file is only a fast-path. The copy is staged into a `.parsek-backup-staging-*` dir in the save folder (not under `Parsek/`, so a failed copy leaves no empty-`Parsek/` false footprint) and atomically `Directory.Move`d into `saves/` as the last step (a mid-copy failure never strands a half-save in the Load menu; orphan staging dirs are swept on load). Scope: persistent.sfs + loadmeta + Ships/ + Subassemblies/ (excludes quicksaves, `Parsek/`, KSP `Backup/`). Fail-open (any parse doubt backs up); fail-loud (Error + on-screen warning, no marker written -> retry next cold load). A missing on-disk persistent.sfs is skipped (and asserted before publish) so a capture failure never fabricates a payload-less "backup". Progress decision parses the on-disk file via `CareerSaveParser`, not fragile live singletons at cold OnLoad. `PreParsekBackup.cs` + `FileIOUtils.CopyDirectory`.

**Tests.** xUnit cases in `PreParsekBackupTests.cs` (ShouldBackup truth table with pinned reason literals incl. footprint-beats-brand-new, SanitizeSaveName, BuildBackupFolderName format + collision, IsBrandNewEmptySave fail-open, HasParsekGameplayFootprint empty/value-only/populated node, IsParsekBackupFolder sentinel/name, CopyDirectory tree/exclude/no-op/failure-warn; the settings round-trip / defaults / "disabled" cases were deleted with the `autoBackupExistingSaves` setting in the 2026-08-27 settings simplification). Full settings suite green.

**~~PENDING OPERATOR (in-game pin, cannot run KSP headlessly)~~ - REPLACED 2026-08-29 BY AN AUTOMATED HARNESS LANE (`PPB-1` / `PPB-2`), BOTH NOW LIVE-PROVEN.** The four-step manual runbook that stood here was never executed. It is superseded by two committed scenarios plus a new in-game category, described below; the original steps are preserved in git history (`git show <this commit>^:docs/dev/todo-and-known-bugs.md`) and their substance is carried, property for property, by the table.

**WHY IT WAS NEVER AUTOMATED, AND IT WAS NOT A HARNESS LIMITATION.** The gate has two conditions - no Parsek footprint AND not brand-new-empty - and no committed fixture satisfied both. Every career with real progress was harvested from a Parsek run and carries a populated `SCENARIO{name=ParsekScenario}`, so `HasParsekGameplayFootprint` reads it as already-touched (`reason=already-parsek-footprint`); every footprint-free save (`fresh-career`, `fresh-sandbox`, `fresh-science`, `strategy-career`) is empty by construction, so `IsBrandNewEmptySave` fires (`reason=brand-new-empty`). **No committed fixture could reach `reason=eligible`, so the backup path had never executed under the harness at all** - including under the `fresh-*` specs, whose staging comment in `run.py::stage_fixture` assumes it does (that prior-backup reap is harmless, and it is what keeps this lane's own backups from accumulating across runs). Closed by `harness/tools/build_preparsek_fixtures.py`, which derives `preparsek-untouched-career` from `career-earned-pad` (reduce the `ParsekScenario` SCENARIO node to its inert `name` + `scene` form, delete `PARAMETERS > ParsekSettings`, do not copy `Parsek/`, restamp the Title) and `preparsek-brandnew-career` from `fresh-career` (Title only, on its own leaf - four specs share `fresh-career` and the produced-save clobber race makes a shared leaf a real hazard). Drift-guarded by `harness/lib/test_preparsek_fixtures.py` (byte-identity rebuild) and, against the REAL C# predicate rather than a description of it, by `Source/Parsek.Tests/PreParsekBackupFixtureShapeTests.cs`: the untouched fixture must read `ShouldBackup -> "eligible"`, the brand-new one `"brand-new-empty"`, and - the negative control - the derivation BASE must still read `"already-parsek-footprint"`, so a strip that removed nothing cannot pass as one that worked.

**THE FIXTURE HAS TO THREAD A NEEDLE, AND THE FIRST CUT OF IT MISSED - caught in review, before the flight.** `preparsek-untouched-career` is the corpus's ONLY fixture that is both FOCUSABLE and wanted footprint-free, so two opposing constraints bear on it at once. **Delete the ParsekScenario node** - the obvious way to make a save look untouched - and the seam's FLIGHT route never instantiates the module at all: `LoadGameImpl`'s focusable branch calls `FlightDriver.StartAndFocusVessel` with no `UpdateScenarioModules` and no `SaveGame`, so `OnLoad` never runs and the backup can never fire. That is **known-gate 6**, and it already cost CL-1 flight 1 (2026-07-28), which flew a whole profile correctly and produced ZERO recordings. **Keep the donor's node** and `HasParsekGameplayFootprint` reads its 4 values + `RECORDING_TREE` child as already-touched, and the backup is skipped. Only the **INERT** form - `name` + `scene`, under that predicate's `nodes.Count == 0 && values.Count <= 2` floor - satisfies both, and it is also the FAITHFUL shape: `PreParsekBackup.cs`'s own class comment says the captured file is gameplay-pristine but NOT byte-identical precisely because KSP has already injected that empty node via AddToAllGames. A deleted-node fixture would have red PPB-1 on five contracts at once, reading exactly like a product defect. Known-gate 6 had lived only as prose in three places (`build_career_pad_craft.py`'s splice comment, the presence map, `autotest-status.md`); prose does not red, so it is now a cell - `test_saveparse.py::test_every_node_less_fixture_is_vessel_less`, every node-less fixture must be vessel-less - verified by inverting the map entry that it reds on exactly this mistake, then reverted. `preparsek-brandnew-career` correctly keeps NO node: it is vessel-less, so it routes to SPACECENTER, where `LoadGameImpl` does `UpdateScenarioModules` + `SaveGame` and KSP writes the inert node to disk itself.

**FLIGHT LEDGER (2026-08-29). Three runs, and the middle one is the reason this entry is worth reading.**

- **`2026-08-29_1101_PPB-1-untouched-career-backup` - PASS attempt 1, 50 s.** Every verifier PASS/SKIPPED, `expectations mismatches=0`, analyzer red=0, `anomalySweep hits=[]`, zero Unity exceptions. EVERY DERIVED NUMBER CAME BACK EXACT, so nothing was re-pinned: the tally token for token (`total=4 passed=4 failed=0 skipped=0 category=PreParsekBackup scene=FLIGHT`), the route as derived from the fixture's vessel count, and all four backup lines in the shapes derived from the C# format strings. Evidence: `First-contact backup: save='preparsek-untouched-career' footprint=False brandNew=False`; `Captured pre-Parsek backup: ... files=3 bytes=332060 dir='...' pristineVerdict=Pristine`; `[InGameTest] backup shape OK: ... craftDirsMirrored=1`; `[InGameTest] captured pristine OK: reason=pristine`; `[InGameTest] idempotent repeat OK: backups=1 marker=True`. ORDERING re-checked independently of the verifier, by log position: capture at line 11307, first of FOUR `OnSave: saving` lines at 11710 - so the forbid is a live discriminator, not a vacuous negative. On disk the published folder holds `persistent.sfs` + `persistent.loadmeta` + the sentinel + `Ships/VAB/Kerbal X.craft`.
- **`2026-08-29_1102_PPB-2-brandnew-career-skip` - PARSEK-FAIL(expectation) attempt 1, ONE mismatch, and THE RED WAS THE SPEC'S BUG RATHER THAN THE PRODUCT'S.** Recorded plainly because a lane that mis-reports its own author is worth more than a clean history. The spec forbade `Skip: reason=already-parsek-footprint save='preparsek-brandnew-career'` ABSOLUTELY. That token is legitimately emitted by this category's own `RepeatColdContactCreatesNoSecondBackup` cell: the in-game batch's campaign-isolation marker save runs `ParsekScenario.OnSave`, which writes a POPULATED node (4 values) to disk mid-batch, and the cell's real `MaybeBackupOnFirstColdContact()` re-invocation then reads it. DIAGNOSED BY LOG POSITION, not inference - 9992 cold gate `brand-new-empty` (the real decision, ahead of every save-write), 10152 the batch's `OnSave: saving`, 10282 the cells re-reading the populated node, 10307 the repeat invocation's footprint skip - and the produced save's node is INERT again afterwards (teardown reverts from the `.bak`), with zero backup folders created. The forbid was unsatisfiable BY CONSTRUCTION; the product was correct throughout. Re-cut to the ORDERING form (footprint must not precede brand-new), which keeps the genuinely-footprinted-fixture catch, and the corrected contracts were replayed offline over this very log (0 mismatches) BEFORE a second lock window was spent.
- **`2026-08-29_1107_PPB-2-brandnew-career-skip` - PASS attempt 1, 46 s.** Every verifier PASS/SKIPPED, `mismatches=0`, tally `total=4 passed=2 failed=0 skipped=2 category=PreParsekBackup scene=SPACECENTER` matching the derivation exactly, zero backup folders on disk, and the same brand-new-then-footprint sequence REPRODUCED - so the corrected pin is proven against the behaviour, not against one log.

**THE GENERAL LESSON, for the next lane that asserts over an on-disk save.** ANY in-game cell that RE-READS `persistent.sfs` observes a POPULATED `ParsekScenario` node MID-BATCH, because the campaign-isolation contract SAVES before teardown REVERTS. A log contract written against the on-disk footprint must therefore be scoped to a MOMENT (an ordering pin) and never stated absolutely over the whole log. Recorded in `PPB-2`'s forbidden block too, where the next author will actually be looking.

**TWO READINGS EXPLICITLY REFUTED, so they are not re-derived later.** (1) The pre-registered `values.Count > 2` threshold hypothesis was NOT the cause of the red: the SPACECENTER route's pre-boot write IS inert (`name` + `scene`), exactly as predicted, which is precisely why the cold gate could take the brand-new branch. That caveat stays open-but-unobserved. (2) This is NOT evidence that `IsBrandNewEmptySave` is dead code sitting behind the footprint check - log line 9992 is that branch DECIDING, ahead of any footprint reading.

**PROPERTY -> SURFACE (what the flights SETTLED).**

| Runbook step / property | Automated surface | Standing |
| --- | --- | --- |
| 1. V2/V3 pristine timing - the copy precedes Parsek's first save-write | TWO independent pins on `PPB-1`. (a) OUTCOME: `PreParsekBackup.EvaluateCapturedPristine` re-opens the published folder after the atomic `Directory.Move` and reports `pristineVerdict=Pristine` ON the capture line; the in-game cell `CapturedPersistentIsGameplayPristine` re-measures the same thing after the load settled, so a Parsek write leaking in LATER also reds. (b) ORDERING: `forbidden` an `OnSave: saving ...` line anywhere before `Captured pre-Parsek backup`, with `OnSave: saving` also `required` so the negative is a live discriminator. The FLIGHT route is load-bearing here and is why this pin lives on PPB-1 and not PPB-2: `LoadGameImpl`'s SPACECENTER/TRACKSTATION routes call `GamePersistence.SaveGame` before our OnLoad, the focusable route does not. READ THE ORDERING PIN'S SCOPE PRECISELY: it catches Parsek's OWN `OnSave: saving` LINE, which exists only once the module is live - it is not a general file-write detector and could not see a stock `SaveGame` that ran before the module existed. That is no hole here (the focusable route makes no such write) but it is why this pin must not be copied onto a KSC-routed lane, where it would read green vacuously. | Fully automated |
| 2. V1 appears in the Load list | PARTIAL, and stated as such. The in-game cell `BackupSiblingCarriesLoadMenuShape` asserts the FILE SHAPE KSP's Load menu enumerates - exactly one `<save> (pre-Parsek <ts>)` sibling, a parseable `persistent.sfs`, the `persistent.loadmeta` the card is rendered from, the sentinel, and a file-for-file mirror of whichever craft dirs the source had. That last half would be VACUOUS on a craftless fixture, so `preparsek-untouched-career` declares `Kerbal X` in `fixtures/shared-ships.toml` - a row that exists only for this, since the spec never launches it - and PPB-1 pins the measured `craftDirsMirrored=1`. It is the only in-game execution `FileIOUtils.CopyDirectory` gets on the backup path. | **RESIDUAL - HUMAN EYEBALL.** No automated surface can say the entry RENDERS, or that it reads clearly: the card shows the SOURCE save's title (copied loadmeta) while the folder shows the timestamped name, and whether that is confusing is a judgement. One look at Resume Saved Game after any PPB-1 run settles it. |
| 3. Idempotency | `RepeatColdContactCreatesNoSecondBackup` calls the REAL `MaybeBackupOnFirstColdContact()` a second time and requires the backup census unchanged, plus `BackupPresenceMatchesTheEligibilityDecision` (marker present -> exactly one folder, and the marker names it). The marker fast path now logs `Skip: reason=marker-present save='<name>'` at Info, so the skip is greppable. | Automated, but see the note below on what "second cold contact" does and does not mean. |
| 4. Brand-new empty career is skipped | `PPB-2` requires `Skip: reason=brand-new-empty save='preparsek-brandnew-career'` and forbids every capture token for that save; `BackupPresenceMatchesTheEligibilityDecision` takes its no-marker branch and re-runs the real gate inputs over the on-disk save, requiring `ShouldBackup` to say NO. | Fully automated |
| 3b. Resuming the BACKUP itself skips `reason=is-backup-folder` | NOT COVERED, and honestly so. It needs a second cold `OnLoad` on a DIFFERENT save folder, and the backup's folder name carries a timestamp the spec cannot know in advance - `${runSave}` substitutes the staged leaf, nothing else. `IsParsekBackupFolder` is exhaustively unit-covered (sentinel and name-fragment paths) and `PPB-1` forbids the token appearing for its OWN save. | **RESIDUAL - unautomated.** Cheap to check by hand alongside step 2. |

**A precision about idempotency, so nobody reads more into the green than is there.** `MaybeBackupOnFirstColdContact` runs only from the cold branch of `ParsekScenario.OnLoad` (`!initialLoadDone`), and `initialLoadDone` is a process-wide static reset only at a main-menu transition or a save-folder change. A second seam `LoadGame` of the SAME save inside one run is therefore NOT a second cold contact - it never calls the hook at all. What `RepeatColdContactCreatesNoSecondBackup` proves is the thing that actually matters and the thing a human reloading the save was checking: invoking the production entry point again on an already-backed-up save takes the marker fast path and writes nothing. The cross-process case (a fresh session on a save whose `Parsek/` dir now exists) is covered by the footprint gate, which is unit-proven.

**Product logging added by the lane (2026-08-29).** All in `PreParsekBackup.cs`, all grep-stable, none changing control flow: the marker fast path skip moved Verbose -> Info and gained `reason=marker-present save='<name>' marker='<path>'` (it was reason-less and save-less, so no contract could require it and two saves' skips were indistinguishable); the missing-`persistent.sfs` skip gained `reason=no-persistent-sfs`; the "no save context" skip gained `reason=no-save-context`; and the capture line gained `dir='<abs>' pristineVerdict=<Pristine|NotPristine|Unverified>`, backed by the new `EvaluateCapturedPristine` post-move re-read. The verdict is deliberately THREE-VALUED: `NotPristine` (read it, it is dirty) is a finding about the product and logs an **Error** `outcome=captured-not-pristine`; `Unverified` (could not read or parse it) says nothing about the payload and logs a **Warn** `outcome=captured-pristine-unverified`. That split matters because **the done-marker is written on every path, so nothing ever revisits this decision** - an Error raised on a transient read failure would brand a perfectly good backup permanently. The published folder is KEPT on every verdict: it is still the player's data, and retrying would only make the same file again, which is why neither path is a rollback.

**Tier and what is owed.** Both specs ship `tier = "operator"` and are now LIVE-PROVEN (PPB-1 `_1101` first flight; PPB-2 `_1107` after the authoring-error correction). The first-flight debt is DISCHARGED and no tally moved - both `BATCH_COMPLETE` lines came back token for token as derived. Nothing is armed: no `gating = true`, and deliberately no `[expectations.ledger]` block. **WHAT IS STILL OWED IS ENTIRELY HUMAN**, and it is three things, none of which any run can close: (1) the `operator` -> `daily` PROMOTION call - both are cheap (50 s / 46 s, the H-series class) so `daily` is the right home, but a cadence decision is a human one (the S1.5 precedent); (2) **the Resume-Saved-Game eyeball** - open Resume Saved Game after any PPB-1 run and confirm the `preparsek-untouched-career (pre-Parsek <ts>)` folder is LISTED and LOADS, and judge whether its card reads clearly given it shows the SOURCE save's title from the copied `.loadmeta` while the folder shows the timestamped name. The cells assert the file shape the Load menu enumerates, never the menu itself; the folder is sitting in the automation instance now, so this costs one look; (3) **resuming the BACKUP itself** (`Skip: reason=is-backup-folder`), which stays unautomated because the timestamped folder name is not addressable from a spec - `IsParsekBackupFolder` is exhaustively unit-covered and PPB-1 forbids the token for its own save, so what is missing is only the live confirmation, cheap to take alongside (2).

---

## Backlog - prioritized "what to develop next" (compiled 2026-07-06, v0.10.3; Tiers 1-4 freshness-checked 2026-07-11)

Session-compiled prioritized development backlog (survey of git log / open PRs / roadmap / design docs / this file). Ordering doctrine: correctness-first, land-shipped-work-before-new, gameplay-value-per-effort. Two premises corrected during the survey: (1) `roadmap.md` §19.4 lags - logistics **M1-M4 are all SHIPPED** in 0.10.3 (M5 inter-body + M6 legibility were the last two, both since MERGED - see the Tier 1 CLEARED note below); (2) there is **no CI** on the repo (`get_status` = 0 checks), so "ready" PRs are review-gated only (suite run locally).

### Tier 1 - CLEARED (2026-07-11): merge queue drained
Every Tier 1 merge-queue item below LANDED on `main` (verified 2026-07-11 via `gh pr view`; `gh pr list --state open` returns 0 open PRs). Kept here for history:
- **#1242** (logistics Rec-1 rewind-redelivery) - MERGED (gate playtest passed 2026-07-08). Was the one open correctness bug (rewind past a route dispatch charged funds but never re-delivered cargo).
- **#1237** (M-MIS-11 loop-unit API) - MERGED. Keystone zero-behavior refactor.
- **#1239** (M-MIS-5 P1 dock-as-interval-boundary) - MERGED.
- **#1238** (Logistics M5 inter-body) - MERGED (gate passed in-game 2026-07-08). Last logistics "Reach" milestone.
- **M6 legibility batch** #1232 / #1233 / #1234 / #1235 / #1236 - all MERGED.
- **#1220 / #1221** - CLOSED as superseded by #1242 (their docs shipped inside it).

### Tier 2 - NEXT: highest value-per-effort new work
- **Route-timeline events** (branch `logistics-route-timeline`) - SHIPPED: player Pause / Activate now emit free-standing `RoutePaused` / `RouteResumed` (new type 30) ledger rows (armed pause-after-cycle emits `RoutePaused` at the delivery applier with the delivered reason token), Send Once provenance is persisted (`Route.SendOnceArmed`, sparse) and stamped on the dispatched row (`GameAction.RouteSendOnce`, sparse) so the one-shot run is bracketed dispatch-to-pause in the timeline, and `Route.CreatedUT` (sparse) records the creation point at `RouteStore.AddRoute`. All new rows are Rec-1-retired at rewind (types 23-30 in `RouteLedgerRetire.IsRouteActionType`). Auto-flip rows LIFTED 2026-07-19 (branch `logistics-dormant-ui`): a LIVE `RevalidateSources` pass now emits `RoutePaused` (`AutoPause:MissingSourceRecording` / `AutoPause:SourceChanged`) on the into-source-problem edge and `RouteResumed` (`AutoResume:SourcesRestored`) when recovery restores an Active-family status (a restored Paused emits nothing). Emission is caller-gated (`liveEmitUT` param, default -1 = silent). Live player-driven ERS-mutation sites route through `ParsekScenario.BumpSupersedeStateVersionLive` (re-fly merge commit, tree-discard purge + marker clear, Re-Fly discard dialog, revert Retry/Discard handlers, unfinished-flight Seal/Stash), which resolves the UT defensively AND forces -1 while `ParsekScenario.OnLoad` is on the stack (central guard - a scene-change load can see a stale nonzero Planetarium UT). Deliberately silent flips remain: the OnLoad revalidation call sites, `MergeJournalOrchestrator.RunFinisher`'s crash-recovery re-drive of `FlipMergeStateAndClearTransient` (explicit `onLoadContext: true`), and the mid-rewind supersede rollback in `RecordingStore.DropSupersedesRewoundOutOfExistence` (a row stamped there would land in the rewound-out future). Those silenced flips are repaired by the CATCH-UP net: every live pass emits `RouteResumed` (`AutoResume:CatchUp`) for any Active-family route whose latest kept lifecycle row (via ELS) still says paused - idempotent, so pause-history desyncs (e.g. the RouteModule dispatch-on-paused Warn loop) self-heal on the next live pass. Still not built: any UI surfacing of the pause history. The `delivered-replay` idempotency-branch contract hole the markers inherited (review finding 3, PR #1327) is FIXED (branch `route-rewind-status-fidelity`): the replay branch now honors an armed `PauseAfterCurrentCycle` - it consumes both flags, transitions `Paused` (reason `delivered-replay-then-paused`), emits the `RoutePaused` marker at the window's stride slot (+4), flushes the owed recovery credit, and drops held escrow; the delivery/funds dedup semantics are unchanged (the marker is the only new row).
- **Route rewind-visibility extension (dormant routes)** - SHIPPED (branch `logistics-route-dormant`, plan `docs/dev/plans/plan-route-rewind-dormant-visibility.md`). The plan review found the premise everywhere else assumed was FALSE: `RouteStore.LoadRoutesFrom` is cold-start-only, so an in-session rewind never reverted routes at all - post-cutoff routes kept firing before their creation point, and pre-cutoff routes kept abandoned-future loop cursors that silently swallowed re-flown cycles (partially defeating Rec-1). Shipped fix: `ReconciliationBundle.Restore(cutoff)` classifies captured routes via `RouteRewindClassifier` (post-cutoff -> DORMANT_ROUTES list, invisible + non-firing; pre-cutoff -> forward-looking cycle state reconciled), and `RouteStore.MaterializeDueDormantRoutes` (top of orchestrator Tick) re-materializes each dormant route Paused when the timeline reaches its `CreatedUT` (occupied source tree -> dropped, live intent wins; missing sources -> visible MissingSourceRecording; round-trip pairs re-link via LinkRoutes). Residuals: ~~dormant routes have no UI and are undeletable until they materialize~~ LIFTED 2026-07-19 (branch `logistics-dormant-ui`): the Logistics window now shows a collapsed "Dormant Routes (N)" disclosure (name + "appears at date" + Delete via `RouteStore.RemoveDormantRoute`), which also closes the resurrection-surprise corner (the player can see and delete the twin before its CreatedUT). ~~`CompletedCycles`/`SkippedCycles` stay inflated after a rewind~~ FIXED (branch `route-rewind-status-fidelity`): the rewind seam derives each KEPT route's timeline-correct pause state from the kept PLAYER-DRIVEN `RoutePaused`/`RouteResumed` rows (`DeriveTimelineStatus` + `ApplyDerivedTimelineStatus`; AUTO `AutoPause:`/`AutoResume:` rows skipped; a derived Active requires a kept player pause row; validity statuses keep their live status with the verdict landing on `PreMissingStatus`), unconditionally clears the armed one-shot flags, and reconstructs the cycle counters from kept rows (`ReconstructCycleCounters`; dispatched-but-undelivered counts as skipped UNLESS a kept in-flight cycle is retained, where the sum lands ON that cycle's ordinal so a straddling cycle keeps its id and dedups). Still accepted: legacy routes without `CreatedUT` never go dormant. The compat report's axis-A "definition/counters revert via .sfs" claim (risk #9 "sound") carries a correction addendum.
- **Go-back rewind route reconcile** - FIXED (branch `fix-goback-route-reconcile`, found by the 2026-07-19 preservation-branch forensic audit). The dormant-routes extension above only wired the FIRST in-session OnLoad exit (Re-Fly: `RewindInvoker.ConsumePostLoad` -> `ReconciliationBundle.Restore(cutoff)`); the SECOND exit - the plain go-back rewind / Rewind-to-Launch / warp-back path, `ParsekScenario.HandleRewindOnLoad` - had zero route handling AND no route-row retire (the audit's assumption that `Ledger.PruneOrphanActionsAfterUT` covers it was verified FALSE: that prune sits on the revert branch only, and the go-back path preserves the in-memory static ledger untouched). Consequences before the fix: kept routes carried abandoned-future loop cursors (re-played cycles silently swallowed, "funds spent, no goods" again), and routes created after the rewind target stayed committed, visible, and firing before their own creation point. Fix: the Re-Fly seam's route block was extracted into the shared `RouteRewindClassifier.ReconcileStoreAtRewind` (behavior-identical at the Restore(cutoff) call site; both exits now share one code path and cannot drift), and `HandleRewindOnLoad` now calls `Ledger.RetireFutureRouteActionsAtRewind` (in-place Rec-1-parity retire, cutoff = `RewindContext.RewindAdjustedUT`, the UT the loaded save reverted the world to) followed by the shared reconcile, before the career cutoff walk; no ledger actions are emitted on this OnLoad path. Gated by `RouteGoBackRewindReconcileTests` (both-exits parity fixture + in-place-retire semantics + source-text hookup/ordering gate).
- **Route-rewind wave automated coverage** (branch `route-rewind-autotests`, stacked on #1330/#1331/#1332/#1333) - BUILT: the manual playtest runbook for the wave is replaced by the in-game `RouteRewindTimeline` category (`Source/Parsek/InGameTests/RouteRewindTimelineRuntimeTests.cs`, 7 scene-agnostic batch-safe tests) plus the unattended harness scenario `H6-route-rewind-timeline` (tier daily; mirrors the H5 RunTests driver). Covered live: lifecycle rows at the real Planetarium UT via `TryPause`/`TryActivate`/`TrySendOneCycleNow`, `ReconciliationBundle.Capture -> Restore(cutoff)` dormanting + kept-route status derivation / armed-flag clears / counter reconstruction + the Rec-1 retire, pending-science cutoff drop (strict-> boundary + blind rollback), dormant re-materialization through the real `RouteOrchestrator.Tick` (one test waits on the production `ParsekScenario.Update` 1 Hz tick itself), live `RevalidateSources` AutoPause/AutoResume/CatchUp rows against real ERS/ELS, and the go-back seam components (`Ledger.RetireFutureRouteActionsAtRewind` in-place + the shared `ReconcileStoreAtRewind`). S1.5/S4.1 (operator tier) gained the Re-Fly Restore-side contracts (`ConsumePostLoad: restoring bundle with route-retire cutoffUT=`, `Restored: ... pendingScience=`). Still genuinely manual (external runbook `../parsek-route-rewind-playtest-runbook.md`): Logistics-window rendering (dormant section, Sending-one-cycle label states, screen messages) and the REAL scene-load exits (`HandleRewindOnLoad` go-back, live `ConsumePostLoad`), which need an operator-piloted rewind.
- **Rec-3 reverse-on-discard** - RESOLVED (2026-07-06, option C): the observability slice SHIPPED (PR #1243, branch `claude/development-priorities-ftr2ye`, stacked on #1242) and both-persist is RATIFIED as correct; reverse writers are DECLINED, not built. The attribution blocker (ambient route rows carry no RecordingId, so a UT-window reverse would wrongly undo concurrent committed routes) plus the 0.10.2 preserve-live-earned-gameplay doctrine make keeping both funds + cargo the correct behavior. No further code work. See `docs/dev/plans/fix-logistics-rewind-determinism.md` Phase 4.
- ~~**Map-view route lines** (M6 gameplay, M) - the one unbuilt M6 gameplay item; draw route paths on the map/TS via the MapRender Director surface. Reuse `GhostTrajectoryPolylineRenderer`.~~ SHIPPED (M6, verified 2026-07-11). `Display/RouteTrajectoryLineRenderer.cs` walks `RouteStore.CommittedRoutes`, reuses `GhostTrajectoryPolylineRenderer.BuildLegsForRecording` + `TryDrawLeg`, clips each route to `RecordedDockUT`, and draws on the flight map + Tracking Station behind the `showRouteLines` setting (default on); same-body routes draw all recorded non-orbital legs, inter-body routes draw the endpoint-body legs. Shipped in commits `008bb30bb` + `7b298582d` (inter-body follow-up), xUnit + in-game covered. Remaining deferred slice (by design, not "unbuilt"): the static orbital-coast overview arc stays head-gated on the stock conic.
- ~~**M-MIS-5 P2** (L) - lift the undock->undock shuttle mid-recording start-trim limitation (`MidRecordingStartTrimUnsupported=9`); unlocks multi-stop shuttle logistics routes rejected today. Prereq: #1239.~~ **SHIPPED 2026-07-08** via #1251 (P2a detector) + #1254 (P2b start-trim lift). Supported shape accepted, degenerate shapes still rejected (NOT a full removal of status 9): an undock->undock mid-tree docked origin with a committed tree, `>=2` completed connection windows, and a finite non-overlapping origin window is now admitted with origin = the first window's undock UT (`RouteAnalysisEngine.IsSupportedMidTreeDockedOrigin` wired into the analysis gate + stand-downs; `RouteBuilder` mid-tree-origin plumbing; `RouteBackingMission.ComputeStartExcludedIntervalKeys`; `Route.RecordedOriginUndockUT` persisted; updated reject text in `RouteCreationFormatters`). Status `MidRecordingStartTrimUnsupported=9` still fires for the degenerate remainder (null/legacy `AnalyzeRecording` tree, origin window overlapping the next stop, inverted origin window, the mid-tree-origin-proof variant), which stay intentionally out of scope per `docs/dev/done/plans/plan-mmis5-p2b-start-trim.md` section 7.

### Tier 3 - LATER: verification + hygiene
- **Validation debt (the real bottleneck)** - code-complete-but-in-game-unconfirmed fixes, clustering onto ~4-5 playtest sessions: (1) career-economy (Rec-1 #1242 gate PASSED in-game 2026-07-08; still open: career-freeze milestone-storm, contract-discard-desync, OnMainMenuTransition); (2) looped re-aim descent-render (reaim-descent cluster, arc truncation, M-MIS-2 P4, cross-SOI encounter observation); (3) eccentric-target Eeloo/Moho constant pinning (M-MIS-3) - BEHAVIOURAL HALF CLOSED 2026-08-15: the band is now WALKED in flight (three departures accept outside the base band, deepest 0.1550 vs a 0.0600 base), so what remains is only the constant-pinning judgement on `EccGain` / `MaxHalfWidthFraction`, not a validation debt - see M-MIS-3-BAND-COMPUTED-NOT-EXERCISED; (4) cross-parent station resupply (M4c); (5) in-game test-runner camera-survival batch. KSP cannot run headless, so this is playtest-bound.
- **M-MIS-10 archetype verification sweep** - constellation deploy / booster flyback / off-Kerbin launch / claw couples / Elcano; cheap verify-and-file, no known break.
- ~~**Remove `MapRenderWarpControl`** temporary debug aid once re-aim descent-render is signed off.~~ DONE 2026-08-29 (release-hygiene, R5 item 1), per the aid's own removal-recipe banner: `Source/Parsek/MapRenderWarpControl.cs` + `Source/Parsek.Tests/MapRenderWarpControlTests.cs` deleted; the sole `RegisterWatchWindow` caller removed from `GhostPlaybackLogic.ResolveTrackingStationSampleUT`; its now-callerless helper `Reaim.DescentTrigger.DescentWindowEndLiveUT` + the two `DescentTriggerTests` cells deleted; the `DebugFlags` class in `ParsekConfig.cs` deleted (`MapRenderWarpEnabled` was its only member); the aid's how-to section removed from `.claude/CLAUDE.md` and `AGENTS.md`. NO CHANGELOG entry, per the banner (never a shipping feature). Nothing in `harness/` or `scripts/` gated on it - the four surviving mentions in LIVE docs are prose only (`autotest-status.md` V3C row, `design-autotest-render-composition.md`, `harness/scenarios/V3C-flight-arrival-companion.toml`, `harness/missions/lib/test_v1_map_dwell.py`), each naming it as the MOLD for a hypothetical future zone-relax aid, so they are kept as historical record rather than rewritten. Four more sit in the ARCHIVED `docs/dev/done/todo-and-known-bugs-v7.md` and are correctly untouched - an archive records what was true when it was written.
- ~~**Doc hygiene** - flip the stale "In progress - Forward trajectory rendering" header (shipped 0.10.2) + add SHIPPED markers to roadmap §19.4 M3/M4.~~ DONE (verified 2026-07-11): roadmap §19.4 already marks M1-M5 SHIPPED and no "In progress / Forward trajectory rendering" header remains in the roadmap or this file.
- **Deferred re-aim solver follow-ups** - ~~M-MIS-2 S4 re-stitch (product-decision-gated)~~ SHIPPED (PR #1263 `reaim-s4-restitch` + sign fix #1279); leg-less-chain forward-run gap remains (low-severity polish). (`SolveArrivalWindow` wiring SHIPPED on branch `mmis4-solve-arrival-window` - see the M-MIS-4 entry.)

### Tier 4 - LONG-HORIZON: the strategic arc
- **Gloops extraction -> Gloops.dll** (XL) - now a standalone-mod code-health track, NOT a multiplayer gateway: the Phase 14 design (2026-09-01) exchanges Parsek-native full-fidelity packets and explicitly decouples from the `.gloop` format (foreign recordings are full citizens: ledger rows, real spawns, interaction - all things `.gloop` strips). Prior caveats stand (engine coupling re-accreted; parallel Gloops recorder #435 to consolidate first). Don't start until logistics/missions are done - every in-flight feature still edits the engine files.
- **Phase 14 co-op async multiplayer** -> **Phase 15 space race** -> **Phase 16 mod compat**. Phase 14 DESIGN COMPLETE (2026-09-01): `docs/dev/design-coop-async-multiplayer.md` (v2 after 2 adversarial reviews + 3 code verifications) - shared-folder contribution exchange, campaigns/checkpoints, read-only foreign citizenship + ownership fence, arbitration fold + salvage ladder, merged economy (deterministic sort, once-ever spend dedup, debt, earlier-UT credit), crew claims. Implementation phased M1-M6 with a 37-task breakdown in `docs/dev/plans/coop-async-multiplayer-tasks.md` (+ `-inventory.md` verified-mechanics map, `-deferred.md` D1-D16); open decision before M2.1: schema-generation bump (doc section 11 / D16).
- **Parked mission shapes** - ~~M-MIS-6 (multi-moon, needs a design note)~~ BUILT + MERGED (PR #1256; design note `docs/dev/design-mission-multimoon-alignment.md` done; gated only on an in-game looped-Jool playtest observation) and ~~M-MIS-8 (cross-tree foreign dock, low value)~~ MERGED (PR #1261). Still parked: **M-MIS-7** (intra-SOI re-aim, gated on M-MIS-6 playtest evidence). Hold pending a concrete player ask.

### Open maintainer decisions (surfaced this session)
- **Rec-3**: RESOLVED 2026-07-06 - ratified both-persist as correct (option C); reverse writers declined. See Tier 2 / plan Phase 4.
- **Rec-2** (inter-body route hard-block): RESOLVED 2026-07-19 - inter-body routes ratified as supported, no creation gate needed. M5 inter-body synodic-faithful scheduling SHIPPED in 0.10.3 (PR #1238, roadmap 19.4, in-game gate passed 2026-07-08), so the visual-faithfulness concern behind the decision (report risk #12/#13) is resolved; delivery was always functional.

Healthy / no action needed (verified this session): the ledger/economy audit (all 5 recs shipped), the observability plan (landed), the render rewrite (cutover complete, no visible artifacts left). The pure-refactor backlog is low-ROI - ride it along with features.

---

## Dev - Logistics in-game tests: auto-spawn unloaded vessel (no manual second craft)

The 7 logistics FLIGHT in-game tests (origin-debit / pickup / multi-stop delivery,
`InGameTests/Logistics*RuntimeTests.cs`) need a live FLIGHT active vessel. The
LOADED-path tests use the ActiveVessel directly - a fueled PRELAUNCH pad rocket
satisfies them after `WaitForActiveVesselUnpack` (they check `loaded && !packed` +
an LF tank; no test rejects PRELAUNCH on `vessel.situation`, so no relaxation was
needed). The UNLOADED-path tests need a SEPARATE on-rails (unloaded) vessel with
LiquidFuel and used to SKIP whenever the save had none, forcing the player to
hand-place a second vessel.

Maintainer-chosen design: "use my pad rocket + auto-spawn the rest". New shared
fixture `InGameTests/Helpers/UnloadedFuelVesselFixture.cs`:
`EnsureUnloadedLiquidFuelVessel(minStoredLf, minFreeCapacity, result)` (coroutine)
(a) reuses any suitable pre-existing unloaded vessel (fast path, behavior-identical
for saves that already have one); else (b) snapshots the ActiveVessel via
`VesselSpawner.TryBackupSnapshot`, rewrites its LiquidFuel RESOURCE amounts via the
pure `AdjustSnapshotLiquidFuel` (>= minStoredLf stored, >= minFreeCapacity free,
flowState forced True) and spawns a FRESH-identity copy (preserveIdentity:false ->
regenerated pid, no collision) into a high (~250 km) parking ORBIT far from the
active vessel via `VesselSpawner.SpawnAtPosition(..., orbitOverride)` so KSP keeps
it on-rails / unloaded; (c) waits a bounded number of frames for the spawn to
register in `FlightGlobals.Vessels` AND settle unloaded, resolving by the returned
pid; (d) on any failure leaves `result.Vessel == null` so the caller falls back to
the existing `InGameAssert.Skip` (never worse than before). Cleanup: a SPAWNED
vessel is removed via `Vessel.Die()` + protoVessels drop in the test's finally
(`UnloadedFuelVesselFixture.Cleanup`); the batch baseline restore is the backstop.
Rewired tests: `OriginDebit_UnloadedOriginVessel_WritesProtoSnapshot`,
`OriginDebit_UnloadedDebit_SurvivesKspSaveRoundTrip`,
`MultiStop_UnloadedEndpoint_DeliversAtBothDocks`,
`PickupDebit_UnloadedEndpointVessel_WritesProtoSnapshot` (the per-suite
`TryFindUnloaded*` finders were folded into the fixture). The
inventory-pickup tests are unchanged (no unloaded variant; an unloaded inventory
fixture would need a stored cargo part the pad rocket may lack). Pure piece unit-
tested in `Source/Parsek.Tests/UnloadedFuelVesselFixtureTests.cs`. Test-infra only
(no user-facing CHANGELOG line).

**LIVE validation DONE (2026-07-10 sweep, `logs/2026-07-10_1935_ingame-test-sweep`):**
the full FLIGHT Run All + Isolated pass ran every rewired Logistics test green except
`Escrow_CompetingRouteSeesReservation_Holds`, which failed on a STALE ASSERTION, not
production: the test still pinned the pre-M6 physical hold token prefix (`source:`)
while the gate correctly emits the M6 escrow-legibility token
(`source-reserved:<pid>:<name>:<resource>:<reservingRoute>`, PR #1233 - the test was
written on the M4b branch in parallel and never updated). Fixed on branch
`fix-escrow-ingame-token`: the assertion now pins the M6 escrow contract (the
`source-reserved:` prefix - this scenario is escrow-caused by construction - plus the
pid and the reserving route A as the final token segment, with the two test route ids
given prefixes distinct within `RouteIds.Short`'s 8 chars so the pin can tell A from
B; both previously truncated to the same `ingame-e`). Test-only; re-verify with one
isolated re-run of the test in FLIGHT.

## TODO - Missions feature completion milestones (M-MIS roadmap; investigated 2026-06-10)

The single ordered list of what remains to call the Missions feature (`docs/parsek-missions-design.md`, shipped core) COMPLETE. Ordered by necessity / priority: each milestone was code-investigated on 2026-06-10 (implemented-already? viable? what exactly remains?) and the findings are recorded inline. Detailed history for the completed milestones lives in the `done/todo-and-known-bugs-v7.md` archive (cross-referenced); this list is the planning surface.

### Reuse mandate (applies to every solver-flavored milestone below)

Do NOT re-implement intercept / window math from scratch. The 2026-05-28 prior-art survey (recorded in `docs/dev/done/plans/reaim-interplanetary-transfers.md` + the prior-art note in the phase-lock entry below) already settled the sourcing:

- **`Reaim/UvLambert.cs`** is OUR owned, unit-tested (Curtis Algorithm 5.2) full-3D universal-variables Lambert solver. Extend it; do not replace it.
- **`Reaim/ITransferSolver.cs`** is the deliberate swap seam. The sanctioned fallback if UvLambert robustness proves insufficient (multi-rev, near-180-deg singularity) is porting **MechJebLib's Gooding solver** (permissive license: public domain / Unlicense; ~577 lines + `V3`/`Statics` deps) behind that seam - a port, not a rewrite.
- **`Reaim/TransferWindowMath.cs`** already carries the KerbalAlarmClock-derived (MIT, attributed) phase-angle + synodic math. TransferWindowPlanner2's porkchop grid was evaluated and deliberately NOT needed (the congruent-window model uses recorded tof + synodic spacing). KSTS / Principia: surveyed, not applicable.
- The launch-side zero-drift near-coincidence primitive (`MissionPeriodicity.NextJointNearCoincidenceUT` / `TryBuildRelaunchSchedule`) and `Reaim/DestinationArrivalSolver.SolveArrivalWindow` (WIRED since the M-MIS-4 post-M4c follow-up, branch `mmis4-solve-arrival-window`: hold-aware sampling + the joint-hold lattice feasibility scan, consumed by `ArrivalHoldPlanner.ComputeJointArrivalHold` for the D8 landing+station dual) are the in-repo multi-constraint window search. New milestones REUSE these, never re-derive them.

### M-MIS-6 - Multi-moon destinations: the looped "Jool-5" mission, window-alignment cut [BUILT, needs the in-game looped Jool playtest]

- **Investigated 2026-06-10 (answers the open uncertainty):** today a Jool-5 recording loops on the FAITHFUL path only: `ReaimClassifier` supports the Kerbin->Jool transfer (Jool is a direct Sun child) but `DestinationConstraintExtractor` fails closed at 2+ SOI-entered moons, so nothing aligns the moons; each moon-relative block self-anchors to the LIVE moon while the Jool-centric inter-moon arcs replay inertially, so every encounter seam renders disconnected (the Mun-desync mechanism, once per moon). What makes it tractable WITHOUT new math: (a) all encounters shift TOGETHER under one arrival hold, so alignment needs the moons' joint CONFIGURATION to recur, not each moon independently; (b) stock Laythe:Vall:Tylo are a near-exact 1:2:4 resonance (period ratios off by ~1e-5 from rounded SMAs), so the inner-three configuration recurs every Tylo period (~211,926 s) to well within SOI tolerance - a per-loop hold in `[0, T_config)` aligns an inner-three tour exactly like the shipped `W_N` destination-rotation hold (substitute T_config for T_rot); (c) the stock major moons are tidally locked, so landing-rotation constraints collapse into orbital phase (the tidal-lock collapse `MissionPeriodicity` already implements); (d) Bop/Pol are incommensurate with the inner three - a full 5-moon tight alignment is effectively non-recurring, so those legs get Loose tolerance via the near-coincidence search or the mission fails closed to faithful (a VALID outcome, surfaced in the UI, never silent).
- **Requirements:** (1) ~~short design note first~~ DONE - `docs/dev/design-mission-multimoon-alignment.md` (decisions D1-D8; the "2+-moon mini star systems" deferred item, `docs/parsek-missions-design.md` sect. 14.4); (2) ~~REUSE the SolveArrivalWindow wiring + generalize the per-loop hold~~ DONE; (3) ~~failing synthetic multi-moon test BEFORE any knob math~~ DONE (11 fixtures verified failing pre-implementation); (4) intra-SOI re-aim (per-leg Lambert re-solves inside the destination system) is explicitly the SECOND cut, tracked as M-MIS-7 - only justified if this hold-based model proves insufficient in playtest.
- **BUILT (branch `claude/mmis6-multi-moon-window-7fcpyh`, stacked on `mmis4-solve-arrival-window`; design `docs/dev/design-mission-multimoon-alignment.md`):** `DestinationConstraintExtractor` now EMITS the 2+-moon set (Supported, all MoonConfigs in `Constraints`, constrained-moon landing rotations in the new `MoonRotations` field; the `MaxConstrainedMoons` reject + constant are retired, and station-bearing Jool-class shapes fall to the station+moon reject). `ArrivalHoldPlanner.ComputeMultiMoonConfigHold` owns the shape: participants = moon Orbitals (SOI tolerance, never dropped) + moon/target rotations (mode ladder; Drop removes them; a tidally locked moon's rotation collapses into its orbital period for free), T_config = k*P_anchor via `MissionPeriodicity.TryFindNextScheduleK` with the smallest-duty anchor (`SelectAnchorConstraintIndex` rationale - Vall for stock, k=2, T_config ~= T_Tylo ~= 211,924s), slack-clamped anchor budget (64), engage double-gated on the scan + the hold-aware `SolveArrivalWindow` window-1 pick (the M-MIS-4 wiring, `holdAlignPeriodSeconds = T_config`, `maxWholeHoldPeriods = 0`). The clock is UNCHANGED: the config hold rides the shipped single-period per-loop path via `LoopUnit.ArrivalAlignPeriodSeconds = T_config` (no new LoopUnit/persisted fields). HONEST FINITE HORIZON (the design's correction to the investigation's recurrence claim): the resonance drifts ~0.6s/2.2s per T_config on the Vall-anchored lattice, so alignment holds for ~40 consecutive synodic windows under Loose (a Tylo-anchored lattice would give only ~8 - why the anchor is duty-selected), then leaves tolerance for centuries; the count is computed (`DestinationArrivalSolver.CountAlignedWindowPrefix`, reporting-only) and logged in the `ARRIVAL HOLD kind=config` line (`alignedWindows=`). EVERY decline ambers (never silent - the old silent no-station Jool-class None is gone): non-recurring configs (Bop/Pol, non-locked moon rotations, Jool-landing rotation under Loose/Tight), slack-starved holds, destination-side loiter cuts (L8), degenerate window spacing. `DestinationLoiterTrim` gained the `ConstrainedMoonCount >= 2` exclusion (the rotation-only trim would misalign the configuration). Tests: `MultiMoonAlignmentTests` (stock-value synthetic Jool system; engage + per-loop all-encounters-within-SOI sweep + amber polarity + byte-identity pins) + `Build_ReaimJoolMultiMoonTour_EngagesConfigHold` (builder E2E) + 3 revised pre-M-MIS-6 pins (extractor emission, station+moon reason ownership, never-silent decline).
- **MERGE GATE - AUTOMATED (2026-07-08):** `JoolConfigHoldInGameTest` (in-game, Category "Missions", SPACECENTER, batch-safe) is the merge gate. It drives the REAL `ArrivalHoldPlanner.ComputeArrivalHold` (through the REAL `DestinationConstraintExtractor` + `DestinationArrivalSolver` + `MissionPeriodicity` chain) against the LIVE Jool body graph via `FlightGlobalsBodyInfo.Instance` - which is exactly what headless could not do (the `MultiMoonAlignmentTests` xUnit fixtures pin the stock periods/SOI/velocities as constants; only an in-game run proves the SHIPPED ephemerides lock 1:2:4 and engage). Test A: the resonant inner three (Laythe/Vall/Tylo, live periods) engages the config hold, T_config is a whole multiple of the live anchor period and lands within one live Tylo period, and the single-period per-loop hold re-aligns every moon encounter within its live SOI tolerance across the horizon. Test B: adding live incommensurate Bop fails the whole set closed to faithful with an amber naming the shape. Skips cleanly on a non-stock pack / rescaled resonance (probes the live 1:2:4 lock first). Runbook: one Ctrl+Shift+T Run All in any stock save.
- **M-MIS-7 go/no-go:** ~~observational evidence from a real looped Jool tour remains wanted~~ **PARTIALLY IN, 2026-08-20.** A driven lane (`V17M`, run `2026-08-20_1841`) supplied the faithful-outcome half on a MOON-TO-MOON (not multi-moon-tour) subject: the re-aim classifier defers the shape and phase-lock declines cross-parent, so the loop replays faithful on the raw cadence - see the MEASURED block under M-MIS-7 below. What is still wanted from normal play is the OTHER half: a multi-moon tour whose encounter seams render connected across aligned windows. The measured half is evidence for M-MIS-7's *(a)* consumer only, and it declines rather than engages.
- **Viability:** ~~moderate~~ built - the resonant-inner-three + tidally-locked case maps onto shipped primitives; the general (Bop/Pol, non-resonant packs) case intentionally fails closed with amber (the design records the align-the-resonant-subset alternative as deferred to M-MIS-7 evidence).

### M-MIS-7 - Intra-SOI re-aim and multi-hop targets (Jool-like systems second cut; Ike-class targets) [GATED: on M-MIS-6 playtest evidence. **THE go/no-go OBSERVATION IS IN, MEASURED 2026-08-20 - see MEASURED below**]

- **MEASURED 2026-08-20 (`V17M-laythe-vall-player-loop`, run `2026-08-20_1841`) - THE ANSWER FOR TODAY'S CODE:** **Parsek neither RE-AIMS nor PHASE-LOCKS a moon-to-moon hop; it replays it faithfully on the raw cadence.** This is the first time the question has been asked of a real cross-parent subject (`fixtures/saves/vall-transfer-recorded`, produced by `B26-laythe-vall-transfer` flight 3), and the answer is a THIRD outcome neither pre-registered hypothesis named. The classifier WAS reached - the single-hop guard did NOT fire - and it declined on the PARKING-ORBIT / MID-COURSE structural check, `Source/Parsek/Reaim/ReaimClassifier.cs:270-277`: `[ReaimDiag] member#0 segs=13 startBody=Laythe supported=False reason='transfer departs from a heliocentric parking orbit or mid-course correction (deferred); staying faithful'`. Phase-lock declined too (`PhaseLock SKIPPED ... support=UnsupportedCrossParent`), and the render side agreed (`factory chain ... reaimed=False faithfulFallback=False`).
  - **THE DECLINE IS THE FLIGHT PROFILE'S, NOT THE MOON PAIR'S.** The only way the harness can fly a moon-to-moon hop today is the parent-relay mode, which is TWO-BURN by construction (escape, coast, then plan the transfer from the parent frame). That middle coast is a real parent-frame orbit sitting immediately before the transfer run on a different orbit, so `sunPredecessor` is true. Note the reason string says "heliocentric" but the code tests `bodyName == commonAncestor` - the check is frame-generic and here the common ancestor is JOOL.
  - **AND THE RE-ADMITTING EXCEPTION MISSED BY ONE CONJUNCT OF THREE**, which makes this a DURATION result rather than a structural one. `IsHeliocentricParkingDeparture`: near-circular PASSED (ecc 0.0126 <= 0.1), co-orbital with the launch body PASSED (4.15% <= 10%), closed-park FAILED - the coast is 43,183.50 s against a 49,717.82 s period = **0.8686 revolutions**, so `wholeRevs = 0`, `ReaimLoiterCompressor.DetectRuns` emits no run, and the `!found` early return fires. **6,534 s - 15.13% - short of one revolution.**
  - **WHAT REMAINS UNTESTED, HONESTLY:** the SUPPORTED path needs a DIRECT single-burn ejection that leaves the moon already on the sibling transfer, and **no currently flyable profile produces that shape** - MechJeb's `OperationInterplanetaryTransfer` plans it but refuses a moon-parked origin (MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN above), and the parent-relay mode that can fly the hop is two-burn by construction. So consumer (a) below is measured-declined for the shapes we can produce, and consumer (b) (the single-hop guard) was never even reached. A profile that let the post-escape coast close one full revolution would take the `DepartedFromHeliocentricPark` path instead - a DIFFERENT code path answering a DIFFERENT question, recorded as an observation and not as a plan.
  - Full derivation: `docs/dev/research/same-parent-reaim-jool-system.md` section 11.3, and `V17M`'s spec header section 0.
- **What it is:** the recursive "mini star system" model - re-solving transfer legs INSIDE a destination system instead of only the heliocentric leg. Two consumers: (a) **moon-to-moon legs of a multi-moon tour** when the M-MIS-6 hold-based joint-configuration model is insufficient (non-resonant moon packs, Bop/Pol legs, long inter-moon loiters): per-leg Lambert re-solves in the gas giant's frame + per-leg holds at each moon-SOI seam; (b) **multi-hop TARGETS** - a target that is not a direct child of the common ancestor (Ike via Duna; rejected today by the `ReaimClassifier` single-hop guard, ReaimClassifier.cs:124-130): re-aim the heliocentric leg to the parent, then the in-SOI hop to the moon is the same intra-SOI machinery.
- **Requirements:** REUSE everything - `UvLambert` is body-agnostic (mu is a parameter), so the same `ITransferSolver` seam serves Jool-centric solves; the per-loop hold clock primitives generalize per leg. This is a genuine new subsystem (per-leg seams, recursive window scheduling): budget a full design note + the failing-test-first discipline, and do NOT build it speculatively - M-MIS-6's playtest decides whether it is needed at all.
- **Viability:** hard; deliberately last among the solver milestones.

### M-MIS-10 - Scenario verification sweep: believed-supported archetypes, never explicitly verified [not sequenced - run incrementally alongside any milestone]

A 2026-06-10 online sweep of what KSP players actually fly (stock career contract types: satellite/relay, rescue, tourism, asteroid redirect, station resupply / crew rotation; the classic community challenges: Jool 5, Elcano, K-Prize, grand tours, Eve return; and the automation prior art Parsek overlaps with: Routine Mission Manager, KSTS, FMRS, MKS supply chains) found NO missing alignment subsystem beyond M-MIS-1..9 - but it surfaced a set of archetypes the recorder/missions stack should support TODAY that have no explicit test or playtest. Each needs a cheap in-game verify (file a todo entry where it breaks):

- **Constellation deployment** (resonant-orbit carrier releasing N relay sats, the CommNet career staple): an N-fork controlled-decouple tree where every branch ends in a perpetual-orbit terminal. Verify the fork-tree records, the Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end.
- **Reusable booster flyback** (FMRS-class profile - the recorder's home turf): booster = controlled-decoupled child flown back to a landing. Verify the branch loops with the main mission and the booster's landed terminal spawns/recovers correctly.
- **Launch from a NON-Kerbin body** (Eve return ascent, Mun surface -> orbit, Laythe spaceplane): `Rotation(B)` / `launchBodyName` handling is generic by construction through the zero-drift scheduler, but every test and playtest to date launches from Kerbin. Verify phase-lock + pad anchoring for an off-Kerbin launch site (also exercises rewind-from-surface there).
- **Claw couples** (asteroid / derelict grabs): verify a claw `OnPartCouple` records as a Dock-equivalent branch point, and that a claw-coupled asteroid (PotatoRoid part) survives ghost-visual building and the snapshot part-name path.
- **Long surface expeditions** (Elcano-class rover circumnavigation, days of driving): no alignment problem (surface sections are rotation-locked and render correctly at any UT) but a recording-size / optimizer / polyline-budget STRESS case; measure before declaring supported.
- **Round-trip resupply with vehicle reuse** (the Routine Mission Manager marquee profile: outbound dock, return, recover): the Missions side (whole-tree span loop incl. the return leg) should already work; the delivery-AND-recovery-per-cycle economics are logistics-roadmap territory - verify the rendering half here, leave the ledger half to logistics M1-M6.
- **Suborbital tourist hop** (career tourism staple): atmospheric-only -> unconstrained free loop; should be the trivial case - one verify run.

#### Verification sweep run 1 - automated pass + operator runbook (2026-07-06)

**Environment.** KSP 1.12.5. Parsek 0.10.3, origin/main @ `d5068e679` (PR #1235). Deployed DLL `sha256 aa4a5887bbd9146a39f923fe2209564c262077f8a36c1c10f5c11d7b1010a55e`, byte-verified equal to the worktree build (`Source/Parsek/bin/Debug/Parsek.dll`). Headless xUnit suite on this commit: 16842 passed / 0 failed / 1 skipped (25 s).

**Scope honesty note - read before trusting the table.** This run was performed by a CLI agent that CANNOT pilot KSP or observe on-screen rendering. Every M-MIS-10 acceptance criterion is an in-game OBSERVATION (loop cycles across FLIGHT / Space Center / Tracking Station, ghost-icon-rides-its-own-orbit-line, non-orbital legs not gliding below terrain, camera hand-offs at stage boundaries, re-aim plane fidelity, line jitter on pan). NONE of those were observed here. The table asserts NO in-game PASS/FAIL; it records only (a) the automated verification that WAS run and (b) the automated-coverage status of the machinery each archetype exercises. The observational cells are OPERATOR-REQUIRED and NOT YET OBSERVED - the per-archetype runbook below is what an operator runs to fill them in. The KSP.log currently in the install is save `s15` / Parsek V0.10.0 (a logistics-branch session), NOT an archetype run and NOT this build, so it is not archetype evidence; no collect-logs snapshot was fabricated from it.

**Per-archetype status** (the 7 archetypes above; the task working matrix was the first 5). Result column values: `AUTO-PARTIAL` = machinery has headless/in-game coverage but no dedicated end-to-end test of this shape; `AUTO-NONE` = no meaningful automated coverage; observational verify is `PENDING-OPERATOR` in all rows.

| # | Archetype | Machinery automated-coverage | Dedicated end-to-end test | Observational verify | Log-snapshot label |
|---|-----------|------------------------------|---------------------------|----------------------|--------------------|
| 1 | Constellation deploy (N-fork decouple -> N orbit terminals) | AUTO-PARTIAL: controlled-decouple + fork (`DecoupledSubtreeAudioStopTests`, `ControlledChildParentAnchoredPlaybackTests`, `RewindForkSegmentPhaseTests`, `ParentAnchoredChildSpineInGameTest`) + terminal spawn (`SupersedeCommitTests`, `PostSpawnTerminalStateTests`) | NO | PENDING-OPERATOR | none yet |
| 2 | Booster flyback (decoupled child flown to landing) | AUTO-PARTIAL: stage split + landed terminal (`BoosterStagingSplitTriggerTests`, `LandedGhostClearance_*` in-game, `MergeLandedReFlyCreatesImmutableSupersede`); reusable synthetic `Booster Drop`+`Booster Drop SRB` pair | NO | PENDING-OPERATOR | none yet |
| 3 | Off-Kerbin launch (Mun/Eve/Laythe pad + phase-lock) | AUTO-PARTIAL (2026-07-07, coverage run 2 - was AUTO-NONE): dedicated headless fixtures run the REAL `ExtractConstraints` + `TryBuildRelaunchSchedule` for a Mun PAD launch (`MissionPeriodicityTests.Extract_MunPad*` / `Extract_MunLaunchKerbinReturn_*`, `MissionZeroDriftScheduleTests.BuildSchedule_MunPad*` / `SelectAnchor_MunPad*`) incl. the Mun-launch + Kerbin-return cross-parent decline; in-game `RealSave_OffKerbinLaunchMission_PadAnchorsToLaunchBodyRotation` (Missions category) validates a committed off-home-pad mission against the live body graph + builder wiring, skipping cleanly when the save has none. No real off-Kerbin launch has been FLOWN + committed yet (rewind-from-surface off Kerbin still unexercised) | NO | PENDING-OPERATOR (HIGH RISK) | none yet |
| 4 | Claw couples (PotatoRoid grab as Dock-equivalent) | AUTO-STRONG since the claw producer (branch `logistics-claw-producer`, 2026-07-08): xUnit `ClawProducerTests` (classifier truth table, kind admission, empty-grapple skip, mid-run grab tree, codec + hash pins, PotatoRoid part-name pin) + in-game `LogisticsGrapple` category incl. the isolated-tier `GrappleCaptureInGameTest` automated gate (real `Part.Couple`/`Part.Undock` cycle on spawned live claw + PotatoRoid parts: Grapple stamping, EVA-suppression silence, window capture + undock completion, asteroid ghost-visual geometry, structural-grab admission verdict; one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene; the gate self-discards the ephemeral auto-record session in setup, so no pre-run operator action is needed); plus coverage run 2 (2026-07-07): `ClawCoupleRecordingTests` pins the Dock-equivalent branch point, asteroid partner resolution + route eligibility, breakup-scan rejection of the raw asteroid AND the post-grab merged ship, and the PotatoRoid snapshot part-name path (+ `VesselSnapshotBuilder.ClawedAsteroidShip` generator), and in-game `ClawCouple` category (`ClawCoupleInGameTest`) verifies PotatoRoid/GrapplingDevice PartLoader resolution incl. the underscore->dot leg and that a synthesized pod+claw+PotatoRoid snapshot survives ghost-visual building | NO | PENDING-OPERATOR narrowed to the stock 0.06 m contact-capture FSM + a full gameplay route cycle (collect opportunistically) | none yet |
| 5 | Elcano / endurance rover (long surface traverse) | AUTO-PARTIAL: surface-relative render + clearance (`Pipeline_Terrain_RoverClearance_StaysConstant`, `LandedGhostClearance_*` x5, `HorizonRotationNearSurface`); no long-recording size / optimizer / polyline-budget stress | NO | PENDING-OPERATOR | none yet |
| 6 | Round-trip resupply (render half) | AUTO-PARTIAL: whole-tree span loop (`MissionLoopUnitBuilderTests`, `MissionCompositionTests`) + dock composition (`MissionDockCompositionRuntimeTest`) | NO | PENDING-OPERATOR | none yet |
| 7 | Suborbital tourist hop (atmospheric-only free loop) | AUTO-PARTIAL: atmospheric polyline (`GhostTrajectoryPolylineBuildTests`) + free-loop span clock (`MissionPeriodicityTests`) | NO | PENDING-OPERATOR | none yet |

**Two highest-risk UNVERIFIED cells** (an operator run should prioritize them): #4 Claw couples (automated halves landed with the claw producer 2026-07-07, but the REAL contact capture at 0.06 m, the release split, and the PotatoRoid ghost-visual build have still never run live) and #3 off-Kerbin launch (pad anchoring + synodic / pad-aligned phase-lock + rewind-from-surface off Kerbin never run in-game). These are pre-existing VERIFICATION GAPS, not observed regressions - do not read them as bugs until an operator run shows a break.

**Coverage run 2 (2026-07-07, branch `claude/m-mis-10-coverage-gaps-q2j54e`) - automated tests for the two highest-risk cells.** Both #3 and #4 flipped AUTO-NONE -> AUTO-PARTIAL (see the table); the OBSERVATIONAL cells stay PENDING-OPERATOR and the runbook labels (`mmis10-offkerbin`, `mmis10-claw`) are unchanged. Findings from the investigation (none are observed breaks):

- **Claw couple routing CONFIRMED shared with dock (no defect):** KSP's claw (`ModuleGrappleNode`, via `Part.Couple`) fires the same `GameEvents.onPartCouple` Parsek subscribes to (`ParsekFlight.cs:1200`), and `ParsekFlight.OnPartCouple` has no docking-port / module-type filter, so a claw grab takes the identical tree dock-merge path (`HandleTreeDockMerge` -> `CreateMergeBranch(BranchPointType.Dock, ...)` -> `BuildMergeBranchData`). The sweep's "records as a Dock-equivalent branch point" claim holds by construction; now pinned in `ClawCoupleRecordingTests`.
- **Cosmetic gap, filed for awareness (not fixed):** `BranchPoint.cs:49` lists `"CLAW"` as an intended `MergeCause` value, but `ParsekFlight.GetMergeCauseForBranchType` (`ParsekFlight.cs:5143`) only ever emits `"DOCK"` / `"BOARD"` - a claw grab records `MergeCause="DOCK"`. Purely cosmetic today (nothing branches on a CLAW cause); differentiating it later is a conscious contract change against the pins in `ClawCoupleRecordingTests`. (Update, claw-producer merge: the CONNECTION KIND half of this finding is superseded - the live path now stamps `RouteConnectionKind.Grapple` via `ConnectionProducerClassifier`; the `ClawCoupleRecordingTests` DockingPort pin exercises the `BuildMergeBranchData` default-parameter fallback, which is unchanged. The `MergeCause="DOCK"` half still holds.)
- **PotatoRoid ghost MESH contribution is prefab-dependent (reported, not asserted):** stock asteroids build their procedural mesh at runtime via `ModuleAsteroid`, so the PotatoRoid prefab may contribute no static mesh to a ghost. `ClawCoupleInGameTest.ClawedAsteroidSnapshot_SurvivesGhostVisualBuild` hard-asserts the part RESOLVES (`skippedPrefab == 0`) and the build survives, and logs whether the asteroid contributed a mesh - the operator run should eyeball whether a grabbed-asteroid ghost looks acceptable without the rock.
- **Periodicity/scheduler Kerbin-assumption audit came back clean:** the extraction + zero-drift scheduler production path has NO home-body hardcoding - `LaunchBodyName` derives purely from the earliest recorded surface/orbit body (`MissionPeriodicity.cs:414`), `FlightGlobalsBodyInfo` reads all periods live off `CelestialBody`, and every `"Kerbin"` literal in production is a codec/deserialization fallback, a UI day-length constant, or a KSC-specific classifier. One deliberate design-scoped gate noted: logistics route origin proof requires a NAMED Kerbin launch site (`RouteAnalysisEngine.IsKscOriginRecording`, `RouteAnalysisEngine.cs:835-840`, mirrored in `RouteBuilder`), so an off-Kerbin PAD-origin supply route classifies as undocked-start (M1 workflow gate) rather than KSC-origin - logistics-roadmap territory, not a missions-path bug.

**Operator runbook** (each archetype: fly-or-reuse -> commit -> configure looped Mission in the Missions tab -> observe a few cycles in FLIGHT + Space Center + Tracking Station; `python scripts/collect-logs.py <label>` immediately after each run; then grep the collected `KSP.log`):

- Common per-cycle checks (all archetypes): mission loops as a UNIT on the shared span clock; relaunch cadence is sane for the shape (atmospheric = continuous free loop; interplanetary = SYNODIC cadence via window index `k` + continuous arrival hold + `PadAlignLaunch`); self-overlap (period < span) staggers instances; re-aim (if the shape has a transfer) resolves OR cleanly declines to faithful, never a broken / off-plane arc; the ghost icon rides its OWN orbit line; non-orbital legs (surface / atmospheric / descent) draw and RETIRE cleanly (no sub-surface glide, no blink-out at hand-offs, no doubled / jittering line on pan); watch-mode camera hands off between stages without losing the vessel. ACCEPTED RESIDUAL (note if seen, do NOT file as new): body-fixed burn arcs rendering ROTATED under a station / arrival hold - cosmetic-under-hold only.
- Ctrl+Shift+T in-game test runner: run before/after each archetype to confirm the integrated build is green in the live scene. Relevant categories: `Reaim`, `Loop`, `MapRender`, `Watch`, `Missions`/`MissionPhasing`, `ParentAnchored`, `Descent`. Results auto-export to `parsek-test-results.txt` at the KSP root (collect-logs.py grabs it).
- (1) Constellation: build an N>=3 payload carrier with N controlled decouplers into distinct orbits; verify fork-tree records N branches, Missions window renders N branches, selection/trim behaves, and N real satellites materialize at recording end. Label `mmis10-constellation`. Grep: `[Parsek][*][TerminalSpawn]`, `[Parsek][*][Fork]`, `needsSpawn=`, any `WARN`/`EXCEPTION`.
- (2) Booster flyback: two-stage craft, controlled-decouple the booster and fly it back to a landing (reuse the `Booster Drop` synthetic to eyeball the branch first). Verify the booster branch loops with the main mission and the landed terminal spawns/recovers. Label `mmis10-flyback`. Grep: `[Parsek][*][TerminalSpawn]`, `landed`, `recover`, `ParentAnchor`.
- (3) Off-Kerbin launch: launch from a non-Kerbin surface (Mun pad-equivalent is simplest). Verify pad anchoring + phase-lock relaunch and rewind-from-surface there. Label `mmis10-offkerbin`. Grep: `launchBody`, `PadAlign`, `PhaseAnchorUT`, `[Parsek][*][Relaunch]`.
- (4) Claw couples: the event pipeline, window stamping, asteroid ghost-visual build, and admission verdict are AUTOMATED (`GrappleCaptureInGameTest`, one Ctrl+Shift+T Run All + Isolated in any FLIGHT scene with a live vessel; auto-recording is handled by the test's own setup). The remaining operator observation is the stock contact capture itself: grab a PotatoRoid asteroid (or a derelict) with the Advanced Grabbing Unit in real play and verify the recorded branch matches the automated fixture's shape. Label `mmis10-claw`. Grep: `OnPartCouple producer classified`, `Route proof dock window captured` with `kind=Grapple`, `PotatoRoid`, `[Parsek][*][GhostVisual]`, part-name resolve failures.
- (5) Elcano rover: a long surface traverse (hours of driving / large sample count). Verify the recording size / optimizer / map polyline budget hold at scale and surface render stays glued to terrain. Label `mmis10-rover`. Grep: `[Parsek][*][Optimizer]`, `polyline`, `budget`, `Points=`.

**Merge-blocker read for in-flight Missions / logistics PRs:** none. The integrated `origin/main` (#1235) headless suite is fully green and the deployed DLL is byte-verified; the findings here are pre-existing coverage GAPS, not regressions, so nothing in this sweep blocks the open PRs. FAIL-triggered focused todo entries are opened only when an operator run actually observes a break (template: exact shape/config, repro steps, observed vs expected, log signature, file:line if localized).

### Explicitly out of scope (faithful replay is the accepted, UI-surfaced behavior; revisit only on playtest demand)

- **Gravity-assist / multi-heliocentric-leg transfers** - `ReaimClassifier` rejects more than one heliocentric leg (ReaimClassifier.cs:141-146); re-aiming a chained assist is a different problem class (the assist geometry constrains every leg jointly).
- **Atmo-direct / aerocapture arrival alignment** - no captured destination orbit means no boundary to insert an arrival hold at; the body-fixed entry/descent already self-anchors to the live rotation and lands at the correct geographic site on loop, so only the approach-to-entry seam misaligns.
- **Porkchop-style dv-optimal window planning** - evaluated in the 2026-05-28 survey and deliberately not needed by the congruent-window model (recorded tof + synodic spacing; M-MIS-3 adds geometry-aware tof centering, still not a porkchop grid).
- **Grand tours / multi-destination single missions** (land-on-every-body challenge flights) - joint faithful recurrence across many transited bodies is effectively never; the accepted loop behavior is faithful replay plus whatever per-leg alignment M-MIS-6/7 provide. No whole-tour alignment is planned.
- **Crew rotation / tourism as DELIVERABLES** - the mission loop renders a crew ferry fine today; counting kerbals as route cargo (crew manifests, rotation credit) is logistics-roadmap territory (`docs/parsek-logistics-supply-routes-design.md` section 19, added by PR #1113), not a Missions milestone.
- **Off-world construction launches** (Extraplanetary Launchpads-class mods: vessels rolled out from a base instead of KSC) - modded compatibility tier; revisit only on demand.

---

## TODO - RunOptimizationPass's FlushDirtyFiles bumps SidecarEpoch outside OnSave (observed 2026-08-29 while fixing the Ensure-pass integrity pair, NOT fixed - pre-existing, out of scope)

`RecordingStore.RunOptimizationPass` ends in `FlushDirtyFiles`, which calls `SaveRecordingFiles(rec)` with the default `incrementEpoch: true`. That is an OUT-OF-BAND write (the pass runs inside `ParsekScenario.OnLoad`'s `loadPhase = "optimization"`, and from the commit paths - never from `OnSave`), so it advances the `.prec`'s epoch while the on-disk `.sfs` still carries the previous one. `RecordingSidecarStore.SaveRecordingFiles`'s own bug-#270/#290 comment states the opposite rule for exactly this case: *"On out-of-band writes (incrementEpoch=false): preserve the current epoch so the .prec matches the last OnSave's .sfs. Without this, BgRecorder and scene-exit force-writes would advance the epoch independently, causing false-positive staleness on quickload (bug #290)."* If no save follows the load (load -> quit to desktop), the next cold load hydrates `rec.SidecarEpoch` from the stale `.sfs`, `ShouldSkipStaleSidecar` sees `.prec` one ahead, and the trajectory sidecar is rejected - permanently, since the `.sfs` never catches up.

Reached routinely today, not by anything new: `TrajectorySidecarBinary.Read` already runs the checkpoint bridge's Ensure with `markDirty: true` during load, so any recording needing normalization is already dirty and already flushed-with-a-bump before anything else runs. The `markDirty: true` flip in `FindSplitCandidatesForOptimizer` adds exactly ONE narrow delta on top of that: the empty-shell reconcile the read seam deliberately gates off, i.e. the c1/s15-era double-cover population. A pure re-sort is NOT a second delta - on main the bridge's dirty gate was already `markDirty && (stats.Changed || sorted)`, so a re-sort-only pass always dirtied; the `Changed`-only gate was the `RecordingStore` wrapper's LOG gate, which the `Resorted` / `AnyMutation` work fixed. That was an observability gap, not a persistence one, and it changes the flushed population by nothing. Deliberately left alone here: flipping `FlushDirtyFiles` to `incrementEpoch: false` is the obvious fix and matches the documented rule, but it changes the freshness contract for the quicksave-then-optimize-then-quickload path for every recording, which wants its own change with its own repro rather than riding a bridge-integrity PR. Fix when picked up: pass `incrementEpoch: false` from `FlushDirtyFiles`, with a cell proving a load-then-quit cycle leaves `.sfs` and `.prec` epochs equal.

## TODO - Looped re-aim interplanetary transfer: no continuous encounter into the destination SOI; line dead-ends in open space (investigated 2026-06-15, NOT fixed - regression-sensitive, deferred)

**Symptom (playtest 2026-06-15; looped 'Duna One' mission re-aimed while flying a fresh Duna mission; log `logs/2026-06-15_1906_duna-mission-investigation`, main @a4ff95b7c V0.10.0, save s15):** the re-aimed ghost's interplanetary transfer LINE is not rendered as a proper encounter. It heads toward Duna's orbit but dead-ends in open heliocentric space, never bending into Duna's SOI; viewed mid-cruise the arc's far end sits at where Duna WILL BE at arrival (empty now), and the recorded Duna-capture hyperbola is a detached segment across a gap. The instrumented form of the same defect is a ~62 deg ghost-transform teleport at BOTH SOI handoffs (`[ReaimSeam] SEAM member=30`: Kerbin->Sun jump=87.76 Mm = 1.043x Kerbin SOI, `KSP.log:21896`; Sun->Duna jump=49.19 Mm = 1.027x Duna SOI, `KSP.log:32175`). Re-aim ENGAGED cleanly (did not decline to faithful), the Lambert solved a sane prograde transfer, and the ghost ICON does reach Duna's SOI - so the orbit is correct; the defect is the stitch / encounter rendering, not a solver failure.

**Root cause (HIGH confidence; SYSTEMATIC + design-deferred, NOT inherent):** re-aim substitutes ONLY the heliocentric coast with a FRESH center-to-center Lambert (`Reaim/ReaimTransferSynthesizer.cs`: r1 = launch-body center, r2 = target-body center, recorded tof reused) and replays the recorded Kerbin-escape + Duna-capture hyperbolae VERBATIM at their original asymptotes. Two superimposed sources:
1. ENDPOINT (dominant, ~96% of the jump): Lambert endpoints are at planet CENTERS but the transfer renders FULL-SPAN (`Reaim/ReaimPlaybackResolver.cs:232-247` passes NaN render bounds). At the seam UT the synth arc is at the body center while the recorded leg ends at the SOI boundary, so each jump is ~1 SOI radius. (An earlier pass that DID trim the launch side to the SOI-exit UT was REVERTED - it opened a gap right after launch SOI exit where the orbit ghost was destroyed and the transfer line restarted displaced by the launch body's own motion. See the comment at `ReaimPlaybackResolver.cs:232-243`.)
2. ORIENTATION / SHAPE (~4% + a 2.5% sma / different ecc gap): the fresh Lambert has zero v-infinity awareness of the recorded asymptotes and reuses the recorded tof (geom tof differs by ~330,610 s here, `devFromGeom` in the `re-aimed transfer ready` line), so over a fractional 0.5835 synodic the asymptote directions and orbit shape differ from the verbatim recorded legs.

The original design (`docs/dev/done/plans/reaim-interplanetary-transfers.md:252-279, 359-361`) accepted the orientation residual as "the accepted small seam" and shipped only PadAlignLaunch; SOI-handoff continuity ("option 3: re-plan the whole patched-conic chain") was explicitly DEFERRED. The same-body 45 deg / 120 s seam-bridge (`GhostTrajectoryPolylineRenderer.cs` `IsBridgeAdjacentConic`) cannot cover a cross-SOI body-change seam by construction.

**Fix direction (the ONLY geometrically sound one; large effort):** the design's deferred "option 3" - synthesize the WHOLE patched-conic chain from one solve so the escape hyperbola's SOI-exit STATE matches the heliocentric departure v1 and the capture hyperbola's SOI-entry STATE matches the arrival v2, instead of splicing a fresh heliocentric arc onto verbatim recorded SOI legs. All three legs then meet at the same SOI-sphere position with continuous velocity, giving a real encounter into the SOI. Immutability-safe (in-memory loop-only on copied structs) and MIT-clean (reuses the already-solved v1/v2; no new solver). SEQUENCE AFTER the in-flight reaim branches land (`reaim-lambert-reliability`, `reaim-eccentric-tof`, `reaim-dest-loiter-retimer` / PR #1155, `fix-soi-trajectory-seam-coverage`) to avoid stacked re-aim rewrites.

**Rejected shortcuts (adversarially verified 2026-06-15, workflow wf_2ead60c5-a19; all NOT VIABLE except E as a stopgap):**
- (A) rigid-rotate the recorded transfer to the new epoch: geometrically false (escape is Kerbin-frame, capture is Duna-frame, only the middle leg is heliocentric; one Sun rotation cannot rotate all three) and misses the dominant endpoint gap.
- (B) anchored / shooting solve to SOI-boundary endpoints + recorded asymptotes: over-determined (a single-rev Lambert has only tof free once r1, r2 are fixed; one scalar cannot match two 3D asymptote directions) and reopens the reverted trim regression.
- (C) render-only rotate the recorded legs onto the solved asymptotes: corrects only the ~4% angular residual, leaves the ~1-SOI-radius endpoint gap, and needs the reverted trim.
- (D) cross-SOI render-only seam bridge: cannot move the ICON / proto-orbit (they ride the ghost transform, not the polyline), and a 62 deg cross-body connector is the wild-spiral / planet-intersection case the bridge was gated against.
- (E) accept the gap + clip the line / suppress the icon across the handoff: VIABLE only as a labeled STOPGAP - it does NOT make the line accurate (a tidy gap is still no encounter, the user's actual complaint) and re-litigates the reverted trim.

**Do NOT do (regression guards):** do not rewrite / finalize / load-time-modify recorded data (.prec / OrbitSegments) on any path - re-aim stays in-memory loop-only; do not vendor a GPL solver (Parsek is MIT); do not auto-extend the heliocentric draw / icon window over the SOI escape / capture window (tried + reverted, puts the ghost behind the planet); do not trim the full-span render without also fixing the capture leg (the reverted gap regression); revert-on-regression - prior re-aim rewrites went net-negative, the current single honest kink is a known baseline; pin the requirement against this one concrete case before writing code.

**Validation must be the ENCOUNTER, not the seam number:** re-run the looped Duna One re-aim playtest, collect a fresh log, and confirm the LINE visibly enters Duna's SOI in-game (a real encounter) and the ICON follows it through both handoffs - NOT merely that the `[ReaimSeam]` jump dropped below one SOI radius. Also confirm: re-aim still ENGAGED, the synth geometry is still a sane ellipse, and no new "gap-between-orbit-segments" / orbit-ghost-destroyed warnings appear at the launch SOI exit (the reverted-trim regression must not return).

**INSTRUMENT ADDED (encounter geometry) - the measurement this entry's validation clause asks for now exists, report-only.** The clause above says validation must be THE ENCOUNTER, not the seam number, and until now nothing measured the encounter: the parity oracle measures each segment against its OWN reference (blind to the gap BETWEEN segments) and explicitly skips re-aimed members (`MapRenderProbe.ComputeFaithfulOrbitParity`'s `reaimed-or-foreign-seed` skip), `loop-seam-teleport` measures the ghost TRANSFORM rather than the drawn line and is exempt at rebind frames by clock-delta, `[ReaimSeam] dist[target]` measures the transform (which this entry records as ALREADY reaching the SOI), and `xfer-vs-target@soi` measures the SOLVE (recorded as already correct). The new `seam-endpoint-outside-soi` lens takes the missing one: at the member's next recorded cross-body seam it samples the CURRENTLY RENDERED conic at that seam instant and measures `|renderedPos(seamUT) - destBody.pos(seamUT)| / destBody.sphereOfInfluence`, both terms propagated to the seam UT through `getTruePositionAtUT` (never a current-anchored position - that would disagree by each body's own displacement since the recorded instant). Correct encounter <= 1.0; raises above 1.005, a threshold calibrated between the S1.8 measured healthy seam continuity (1.2e-4 / 1.5e-4 of the crossed sphere) and this entry's measured 1.027 / 1.043. Pure core `Source/Parsek/MapRender/SeamEndpointOracle.cs` (xUnit `SeamEndpointOracleTests`), Unity capture `MapRenderProbe.ComputeSeamEndpointGeometry`, raised once-per-onset from `TrySampleAndEmitSeamEndpoint`, `mapRenderTracing`-gated, REPORT-ONLY (`hlib.ANOMALY_REASONS_RAISED_UNGATED`). THREE THINGS IT DOES NOT DO, stated so a silence is not over-read. (1) It measures the CURRENT leg's own endpoint, not the inter-segment jump: the 1.043 figure above is `|escape-end - transfer-start|` over the ORIGIN body's sphere, a different quantity in BOTH terms. The departure seam produces NO reading at all rather than a healthy one - `toBody` there is the Sun, whose sphere is not a finite encounter target, so the sample lands in `skip.no-usable-ratio`. That half of the defect is not a raise class awaiting demonstration; it is one the capture cannot construct, and the sizing of 1.005 does not depend on it (the binding term is the SMALLEST measured defect, 1.027, which V4's census proved reachable). The arrival seam is the one it sees, and that is the dominant symptom (the transfer dead-ending short of Duna). (2) A FAITHFUL loop replay of an interplanetary transfer raises it by design - the destination has moved on in inertial space by the loop shift - so read the line's `seed=` / `loopShift=` fields before calling a raise a defect. (3) **A RE-AIMED MEMBER WHOSE ARRIVAL THE PRODUCER RE-TIMED IS REFUSED, NOT MEASURED (added 2026-08-09).** The lens samples at the RECORDED seam instant mapped onto the rendered clock; the clock mapping relocates the instant, it cannot discover that the producer moved the arrival. On the F2 parking-departure path the tof search is centred on the geometric Hohmann time with the recorded tof deliberately unused (`ReaimTofSearch.BuildParkingCandidateTofs`; the s15 fixture's recorded tof is ~1.44x Hohmann), the render span is trimmed to `[RecordedDepartureUT, RecordedDepartureUT + usedTof]`, and the recorded capture leg is `ShiftInTime`'d back to meet it - so the drawn arc reaches the destination DAYS before the recorded seam. Measuring there is WRONG, not merely uncalibrated, and the resulting miss is orders of magnitude past the 1.005 tolerance: it would raise `seam-endpoint-outside-soi` on a CORRECT arc, indistinguishable in kind from the 1.027 reading the instrument exists to find. `ComputeSeamEndpointGeometry` therefore refuses the sample (`skip.reaimed-seam-instant-unknown`) when the Director's driven seed is re-aimed AND the seed's own arc end disagrees with the recorded seam by more than a second. The refusal is deliberately narrow - a re-aimed member the producer did NOT re-time (the DIRECT path, where candidate step 0 IS the recorded tof and the render span falls back to the recorded arrival) compares equal and still measures, which is why V4's census reading is unaffected. "Unknown" rather than "re-timed": the seed's end is not a usable substitute instant either, because the seed is whichever arc the Director drives THAT frame and on a two-burn departure that can be the re-phased heliocentric PARK, whose end is the departure burn. NOT FLOWN EITHER WAY - no committed lane exercises the parking-departure path, so this population has produced neither a raise nor a skip in the field, and the refusal is a reasoned decision awaiting a measurement. Registered as the third promotion blocker in `hlib.ANOMALY_REASONS_RAISED_UNGATED`'s comment block. **RE-FLOWN AFTER THE CHANGE, because a sampler edit invalidates the census it was measured against:** V4 `2026-08-09_1158` (PASS attempt 1, wall 76 s) and V7M `2026-08-09_1159` (PASS attempt 1, wall 55 s) both re-read `seam-endpoint summary evaluated=1 outsideSoi=0`, unchanged, with the new `skip.reaimed-seam-instant-unknown` bucket appearing ZERO times in either log and zero anomalies of any reason. That is the predicted result and it is what makes the guard's narrowness a measurement rather than an argument: V4's sample is a re-aim owner on the DIRECT path, where the render span falls back to the recorded arrival and the seed's end IS the recorded seam, so the guard compares equal and the lens still measures - the pinned `evaluated=[1-9]` on both lanes is intact. **ANTI-VACUITY ACCOUNTING ADDED (2026-08-09), because the first reading run proved the instrument could not report on itself.** The lens flew on five re-flights and raised ZERO times, and nothing in the log could distinguish sound geometry from a lens that never evaluated a seam - the re-flight agent had to DERIVE the answer offline from covering-segment lines plus the fixtures' `.prec` chains. It was the second: at every frame where the lens resolved a ghost, the drive clock sat in the recording's LAST `OrbitSegment`, so the capture bailed on `no-cross-body-successor` before measuring anything - these fixtures' recordings simply END in the destination park, with no cross-body successor left ahead of the clock. The fix mirrors the faithful-parity lens's own per-pass accounting: one Verbose `[Parsek][VERBOSE][MapRenderTrace]` line per probe pass, `seam-endpoint summary evaluated=<n> outsideSoi=<n>` followed by zero-or-more ` skip.<reason>=<n>` (same field grammar as `faithful-parity summary ...`, same 5 s rate-limited key shape). `evaluated` counts destination-approach checks the lens ACTUALLY PERFORMED; every other ghost-frame lands in exactly one `skip.` bucket (the twelve capture reasons - `no-rendered-orbit`, `no-recId`, `no-recording-or-segments`, `no-covering-segment`, `no-covering-body`, `body-mismatch`, `no-cross-body-successor`, `seam-ut-not-finite`, `seam-behind-clock`, `to-body-unresolved`, `reaimed-seam-instant-unknown`, `propagation-threw` - plus `no-usable-ratio` for a sample the oracle could not turn into a ratio, which is EITHER a non-finite / non-positive SOI radius, e.g. the Sun as a destination, OR a non-finite endpoint distance from degenerate rendered elements; that bucket was called `no-soi-measurement` and documented with only the first cause until 2026-08-09, which would have read a broken rendered conic as "the destination had no finite sphere"), so `evaluated + sum(skips)` is the number of ghost-frames the lens was handed. The summary is emitted ONLY when `evaluated > 0 || skip reasons > 0`, which is what keeps it out of the `probe frame summary` trap: that line primes its shared 5 s key on a ghostless frame ~2 ms after the tracer flips, and the short lanes (V4/V6M/V6T/V7M/V7T, 2.2-3.8 s) never get a second emit. Both counters are only touched from inside the per-ghost capture, so passing the guard means at least one ghost genuinely reached the lens. Pure guard + formatter `SeamEndpointOracle.ShouldEmitPassSummary` / `FormatPassSummary` (xUnit `SeamEndpointOracleTests`); the counting itself lives in the Unity sampler.

**FIRST REAL-GEOMETRY CENSUS, 2026-08-09 - AND IT FALSIFIED THE OFFLINE DERIVATION ON TWO OF THE FIVE LANES.** The paragraph immediately above records the pre-census belief: every lens-reaching frame sat in the recording's LAST `OrbitSegment`, so the capture bailed `no-cross-body-successor`, because "these fixtures' recordings simply END in the destination park". That was assembled BY HAND from covering-segment lines plus the fixtures' `.prec` chains, precisely because the lens could not report on itself. **It was wrong for V4 and V7M.** The five lanes re-flown with the census on (runs `2026-08-09_1015` / `_1016` / `_1017` / `_1018` / `_1020`), each line verbatim:

| lane | `seam-endpoint summary` | reading |
|---|---|---|
| V4-player-loop-workflow | `evaluated=1 outsideSoi=0` | **EXERCISED** - Sun->Duna arrival seam |
| V6M-mun-player-loop | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero |
| V6T-mun-ts-arrival | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero |
| V7M-minmus-player-loop | `evaluated=1 outsideSoi=0` | **EXERCISED** - Kerbin->Minmus arrival seam |
| V7T-minmus-ts-arrival | `evaluated=0 outsideSoi=0 skip.no-cross-body-successor=1` | structural zero, byte-identical in all three collected logs of that lane's two runs - but read the verdict note below before counting those as three corroborations |

ZERO raises on any lane; V7T's `icon-off-orbit` red is its own documented finding, and the encounter lens moved no verdict anywhere - the report-only registration behaves as designed. **WHAT THE TWO POSITIVE READINGS ARE.** V4's evaluated frame (frame 6464, currentUT 28,781,000, driveUT 9,127,924.233; `shadow ... segIdx=4 treatment=StockConic body=Sun`, `Cached body-frame bounds ... rawBodyFrameUT=4742953.22-9128108.76 segIndices=6-11`) covers the SUN heliocentric transfer leg ending 9,128,108.76, whose successor is the Duna approach - so the lens measured THE ARRIVAL SEAM OF AN INTERPLANETARY TRANSFER, the exact geometry class this entry's 1.027 defect lived in, and read it INSIDE the sphere. It did so on a frame where the faithful-parity sibling stood down (`skip.reaimed-or-foreign-seed=1`, Director seed `epoch+shift=NaN`) - the "deliberately NOT re-aim-gated" design property paying off in the field, on the very population the parity oracle cannot see. V7M's (frame 7777, currentUT 1,345,035, driveUT 267,563.741) covers a Kerbin leg ending 267,741.32 with the Minmus approach next: a faithful, phase-locked, same-parent arrival, also inside the sphere - first evidence that the same-parent road reads healthy rather than raising, though it is one lane and one frame, not a general result. Both reproduced BIT-IDENTICALLY on **three consecutive flights** each (`_1015`/`_1028`/`_1036`, `_1018`/`_1031`/`_1036`), so `seam-endpoint summary evaluated=[1-9]` is now REQUIRED on those two specs; all five carry the presence form `evaluated=\d+ outsideSoi=\d+`. THE REPEATED `_1036` IS NOT A TYPO and the two lanes did not share a run: run ids are scenario-qualified, and the tier flew them back to back inside one minute - `2026-08-09_1036_V4-player-loop-workflow` 10:36:03-10:36:54, then `2026-08-09_1036_V7M-minmus-player-loop` 10:36:54-10:37:49, each PASS attempt 1. Both lanes were then flown a FOURTH time after the capture gained the re-timed-arrival refusal (`_1158` V4, `_1159` V7M) and read `evaluated=1 outsideSoi=0` again, which is what establishes the refusal cost neither lane its measurement. The three structural zeros are a property of the destination-park dwell those lanes share, not of the instrument.

**THE CAPTURE'S VALUE-CORRECTNESS IS STILL ONLY HALF COVERED (filed 2026-08-09).** The oracle's arithmetic has 20+ xUnit cells and the two `evaluated=[1-9]` pins prove the capture path EXECUTES, but neither says the computed distance is the right one. Partly closed in this branch: the capture's three list/clock DECISIONS were factored out of `ComputeSeamEndpointGeometry` into pure helpers - `FindCoveringOrbitSegmentIndex` (the half-open-except-last covering rule, whose INDEX the successor walk needs), `FindCrossBodySuccessorIndex` (walk through same-body cuts, stop at the body change), `ResolveSeamUTOnRenderedClock` (the three-way baked / raw / assumed-live mapping - flipping those arms moves the sample by a whole loop shift and nothing else would notice) and `IsRecordedSeamInstantUsable` - and each is now driven headlessly in `SeamEndpointOracleTests`. STILL UNCOVERED: the `getTruePositionAtUT` pair and the destination-body lookup, which need a live `Orbit` / `CelestialBody`. That is the same "green but blind" hole `MapRenderProbe.OrbitRelativePositionYup` names as a standing obligation and the codebase answers eight times over for the sibling captures (`ComputeFaithfulOrbitParity` / `ComputeSynthesizedConicParity` are driven from live fixtures by eight InGameTests files, `ReaimedLoopSynthesizedOracleInGameTest` being the template - the seam variant needs only a two-body segment chain on top, a fixture increment rather than a missing capability). Deliberately not built in this pass. Note what the existing pins do and do not guard: killing the covering scan or the successor walk would drop `evaluated` to 0 and red V4 + V7M, but corrupting the propagation pair yields a WRONG distance and reds nothing, because `outsideSoi` is deliberately unpinned and the raise is report-only.

**TWO NAMED NEXT STEPS, both filed rather than done.**
1. **THE CENSUS DOES NOT CARRY THE RATIO.** `outsideSoi=0` proves every evaluation came in at or under 1.005; the ratio itself prints only on a raise, so the MARGIN is unmeasured - we know the arc reaches the sphere, not by how much, and cannot yet tell 1.2e-4 (S1.8-healthy) from 1.004 (one nudge below raising). Fix is a census-format change - carry the pass's max ratio, e.g. `seam-endpoint summary evaluated=<n> outsideSoi=<n> maxRatio=<r>` - and it would make the HEALTHY population pinnable for the first time, which is what turns this instrument from a tripwire into a measurement.
2. **MAKING V6M REACH ITS SEAM IS A DWELL PROBLEM, NOT A BRACKET PROBLEM.** THE ARITHMETIC, from the runs' own `Cached body-frame bounds` lines. *Mun fixture (V6M/V6T), cycle-1 `loopShift=280,142.53`*: Kerbin leg raw `[230.03, 16,404.98]` segIndices 0-7, Mun leg raw `[16,404.98, 20,939.02]` segIndices 8-10 -> cross-body seam raw **16,404.98** = live **296,547.51**. V6M's four cycle-1 jumps are 296,370 / 296,490 / 296,690 / 300,977 -> drive 16,227.47 / 16,347.47 / 16,547.47 / 20,834.8, so **the first TWO are ALREADY PRE-SEAM** (by 177.5 s and 57.5 s) with the Mun successor ahead of them. The lane still reads `evaluated=0` because that pre-seam epoch survives about one frame: the jumps fire back-to-back with no dwell, and the only probe frame at 296,370 is the ghost's CREATION frame, which the probe's own guard (`offOrbit != null && !iconSuppressedNow && !suppressionLifted`, in `MapRenderProbe.Sample`'s proto-orbit-line block) rejects - NO census bucket is emitted at that epoch at all, which is how we know the lens was never OFFERED it rather than offered it and skipping. Minimal honest change: hold at 296,370 (or 296,490) for one or two extra probe frames before jumping on. NOT done here, deliberately: V6M is green, armed, and carries a pinned `BATCH_COMPLETE` tally, and adding dwell ticks is NEW dwell structure rather than a re-aim of an existing bracket. *Minmus fixture, cycle-1 `loopShift=1,077,471.26`*: Kerbin leg raw `[230.05, 267,741.32]` segIndices 0-5, Minmus leg raw `[267,741.32, 277,001.13]` segIndices 6-9 -> seam raw **267,741.32** = live **1,345,212.58**; V7M's jumps 1,345,035 / 1,345,155 / 1,345,355 sit at -177.6 s / -57.6 s / +142.4 s. Identical bracket geometry to V6M's, and V7M's first bracket is exactly what buys its measurement - which is the cleanest possible demonstration that the difference between the two lanes is dwell, not design. **DO NOT "FIX" V6T OR V7T THIS WAY.** Both park a single TS observation ~142 s PAST their seam on purpose; that just-inside-the-SOI epoch IS the claim those lanes make, and moving it pre-seam to feed this instrument would change the subject of the scenario to buy a measurement V6M can supply for the cost of a dwell.

**References:** full investigation + ranked options doc at the umbrella root `reaim-seam-investigation.md`; log snapshot `logs/2026-06-15_1906_duna-mission-investigation/`; engaged params `KSP.log:13201` (`ENGAGED re-aim Kerbin->Duna via Sun; D0=142619013 synodic=19653075 tof=6854613`).

**ATTEMPTED + REVERTED (2026-06-17) - option 3 (whole-chain synthesis). STILL OPEN; do not re-attempt without the preconditions below.** The deferred "option 3" was built behind a default-OFF flag (P0/P1 foundation #1169, P2-P4 chain synthesis #1170) and then REVERTED (#1171 reverts #1169; #1170 closed). Plan/design docs at the umbrella root: `reaim-fix-plan.md`. Why it was abandoned:
- **It regressed the flag-ON render and never solved the bug.** First playtest (Kerbal X #2): synth escape/capture legs were garbage hyperbolae (`[ReaimSeam] chain legs: escape ecc=12.9, capture ecc=7.77`; sane is ~1.05-1.3), so departure+arrival misaligned, Duna-SOI arrival render broke, icon teleported. Log `logs/2026-06-16_2351_reaim-chain-kerbalx2-regression/`.
- **Root cause (confirmed):** `ReaimTransferSynthesizer.BuildBodyRelativeLeg` paired the transfer POSITION at the SOI crossing with the Lambert ENDPOINT velocity v1/v2 (at the planet center), an inconsistent state vector. Fixed (use `transfer.getOrbitalVelocityAtUT(crossingUT)`) + added the plan's never-built periapsis/ecc fail-closed gate (`IsSaneLegConic`), so flag-ON fails closed to baseline instead of rendering garbage.
- **But it still could not be validated:** every available test mission is a fail-closed case. Duna One threads Ike (capture fails closed). Kerbal X #2 is a heliocentric-parking (two-burn) departure (escape fails closed) AND its re-aim render is independently broken by the #1166-engages-but-#1167-Increment-2-not-built span-greater-than-synodic gap, not by this work. Validating option 3 needs a CLEAN DIRECT Kerbin->Duna recording: no parking-orbit loiter, no moon (Ike) encounter. No such mission exists in the test save.
- **Structural doubt:** a center-to-center Lambert solve carries no information about the real ejection/capture periapsis, so the synth body-relative legs inherit whatever periapsis the SOI-crossing sample implies; the gate fails the bad ones closed, but a "consistent but wrong-altitude" leg can still pass. The premise may need rework (e.g. SOI-edge-to-SOI-edge solve, or seed the ejection from the recorded parking periapsis) rather than center-to-center.

**Do NOT re-attempt option 3 without (a) a clean direct no-park no-moon Kerbin->Duna looped recording to validate against, and (b) resolving the center-to-center-periapsis structural doubt.** Confirm a validatable test case exists BEFORE building (this arc was built end-to-end before that check, which was the core process mistake).

**Precondition (a) DISCHARGED (2026-08-06, duna-direct-lane branch): the clean direct recording EXISTS as a committed fixture.** The B17 lane produced it end to end: the DD1 Duna Direct Probe (authored by construction, `harness/tools/build_dd1_craft.py`, drift-gated), the PAD-ALIGN group of the shared B5 machine (pre-launch seam TimeJump to the ejection window computed from mlib's committed stock heliocentric ephemeris + ASAP ejection), `FORGE-b17-duna-pad` and `B17-duna-direct-orbit` (nightly, ARMED). MEASURED on the green flights (run ledger on the status doc row): a confirmed 54-day pad epoch jump, launch-to-ejection-node 1,791 s (under one park orbit - the no-loiter constraint as a number), the full Kerbin->Sun->Duna traverse with every forbidden Ike/Mun/Minmus token silent, capture into a 0.002-ecc ~300 km Duna park wholly inside Ike's orbital shell, and the tree committed in-flight with `terminalOrbitBody=Duna`. The produced save is the committed `harness/fixtures/saves/duna-direct-recorded` fixture (recording sidecars included via the harvester's new `--keep-parsek` mode; pinned by the `RECORDED_FIXTURES` sweep), so the option-3 validation lane can consume the recording WITHOUT re-flying the mission. NOTE for that lane: the committed recording is not yet LOOPED - looping is a playback-time property the consumer arms on the committed recording. Constraint (b) - the center-to-center periapsis structural doubt - REMAINS OPEN and is NOT touched by this lane; do not re-attempt option 3 without resolving it.

**Arrival-validation lane FLOWN (2026-08-06, loop-arrival-lane branch): the fixture's loop REPLAYS and both modes' arrival behavior is now MEASURED + ARMED.** Tier 1 (`S1.8-soi-crossing-playback`, daily, live-proven): recorded-segment position resolution is continuous across both flown SOI seams - measured gaps 10,146.3 m (Kerbin->Sun) and 7,284.0 m (Sun->Duna) against a 25 km pin, versus the ~50,000-90,000 KM discontinuity the defect class would show; playback lands inside Duna's SOI at arrival. Tier 2 (`V2-loop-arrival-dwell`, operator, live-proven + armed): mission-level loop armed on the committed DD1 tree via the promoted MissionConfig seam verb, map camera dwelt THROUGH both recorded handoff instants and the parked tail with mapRenderTracing armed. MEASURED, re-aim mode: the classifier ENGAGES for this member (plan.Supported=True - the property Duna One and Kerbal X #2 both lacked; `ENGAGED re-aim Kerbin->Duna via Sun` D0=24396028.988 tof=4385155.5, synth ecc=0.2624, phase-locked one synodic ahead of the arm-time anchor), the engaged replay crosses both handoffs with a CLEAN gated anomaly sweep at map scale (allowedAnomalies=[]; no icon-teleport, no icon-off-orbit raise), the ghost renders on the flown Kerbin escape hyperbola (sma=-5,357,267 with the one-synodic loop shift applied), and the proto orbit hands into the DUNA frame at the arrival dwell - now a hard-required armed token. The EXPECTED red did NOT reproduce on the map surface: the 2026-06-15 `[ReaimSeam]` 62-deg transform-teleport instrument lives on the FLIGHT-scene engine path, which a map/TS dwell never drives - so V2 bounds the defect's MAP-visible surface (none, at planet-view scale) and stands as the armed regression FLOOR for the option-3 work, while the defect's reproduction and the encounter validation above remain FLIGHT-scene playtest work. MEASURED, faithful mode: UNREACHABLE at flight time on this fixture - the classifier auto-engages any supported member and no product force-faithful switch existed, so the faithful-vs-re-aim A/B the lane brief wanted needed a product knob first. **That knob now EXISTS:** `ParsekSettings.forceFaithfulLoopPlayback` (Settings > Looping, "Force faithful loop playback (no re-aim)"; default OFF, GameParameters-only persistence mirroring `transitedBodyRotationModeIndex`, and SetSetting-whitelisted so a scenario can flip it from the command seam). With it on, a re-aim-SUPPORTED mission builds a unit byte-identical to a classifier decline (no plan, no schedule, no loiter cuts / launch hold / descent members; faithful anchor `Math.Max(LoopAnchorUT, spanEndUT)`), and the builder logs a grep-stable `[Reaim] MissionLoopUnit: mission='...' FORCED FAITHFUL (re-aim supported X->Y but forceFaithfulLoopPlayback is on)` - the per-member classify verdicts still emit unconditionally, so the log proves the member IS supported rather than silently declined. Headless pair over the flown duna-direct fixture: `MissionLoopUnitBuilderTests.ForceFaithfulOff_TheFlownDunaDirectMissionEngagesReaim` / `ForceFaithfulOn_TheSameMissionStaysFaithfulWithNoReaimState`. The faithful-mode DWELL FLIGHT itself is still to be flown (do not fake it by degrading the member). Full six-finding run ledger on the V2 row in `docs/dev/autotest-status.md`.

**FLIGHT-SCENE lane FLOWN (2026-08-07, flight-arrival-lane branch): the defect's actual surface is now instrumented, its visible envelope is bounded, and the missing product knob exists.** SHIPPED: (1) `forceFaithfulLoopPlayback` (Settings > Looping; SetSetting-whitelisted; gated at the builder's plan-consumption point so ReaimDiag still proves the member supported - the faithful-vs-re-aim A/B is UNBLOCKED); (2) the Tier-C `loop-seam-teleport` instrument (GhostRenderTrace.EmitAnomaly + the pure IsLoopSeamTeleport predicate, floor 1,000 km between the measured populations - healthy recorded seams 7-10 km vs this defect's 49-88 THOUSAND km - raised from the mode-agnostic ParsekFlight.TrackLoopSeamTeleport, faithful AND re-aimed members, ghostRenderTracing-gated, harness-sweep-gated at birth); (3) the V2-filed probe pid->recId mislabel fixed (GhostMapPresence.RebindGhostRecordingId). MEASURED, five flights across three specs (V3F/V3R/V3C, run ledgers on their status rows): (a) THE ZONE GATE - the flight engine hides a far loop ghost BEFORE positioning (GuardSkip hidden-by-zone; the hide precedes PositionFromOrbit), so a parked observer can NEVER drive the seam surface; the 2026-06-15 forensics fired precisely because the player flew ALONGSIDE. This bounds the defect's user-visible envelope: map clean (V2), recorded data clean (S1.8), parked flight scene never renders it (V3F/V3R) - it is visible ONLY while flying near the re-aimed ghost. (b) THE CYCLE FLOOR - an engaged unit's phase anchor floors at the recording's span END, so the replay cycle always starts >= one synodic after the recorded flight; a companion must fly the ghost's OWN later window, never the recording's. (c) REACHABILITY PROVEN - the cycle-aligned companion (V3C run 2: B17 pad-align mission + injected looped recording, both on the D0=24,396,029 window) drove the [ReaimSeam] dist[] trace x48 from an unattended run, the first time ever; the remaining gap is that the B17 window/correction tuning (cycle-1) MISSED the Duna encounter at cycle 2 (Duna e=0.051 makes window quality cycle-dependent), so the handoff instants were crossed out-of-zone and THE ~62-DEG TELEPORT REMAINS UNREPRODUCED-BUT-UNREFUTED: nothing measured contradicts the 2026-06-15 forensics; no unattended run has yet watched the seam instant from inside the zone. NEXT STEP (advanced on the v3c-encounter-tuning branch, 2026-08-08, runs 3-5; five-agent review corrected the ledger): the wider correction budget (800 vs the measured 522.1 m/s cycle-2 cost) CLOSED the encounter - every branch flight reached Duna. The review then established the hard wall: the flight engine's zone gate is 120 km (GhostVisualRangeMeters; the earlier ~55.8 Mm reading was the smallest HIDDEN sample, an upper bound only), and the Sun->Duna seam sits AT Duna's SOI radius (47.9 Mm) - so the arrival seam is UNOBSERVABLE from any Duna park, and the post-park bracket approach is dead by construction (its cycle-1 targets were also 7,190 s short: the span clock rides phaseAnchor 24,306,768.36 post-PadAlignLaunch-shift, true cycle-1 seam 48,434,260.29). What stands: co-flight reaches the engine surface (dist[] x56 with the live craft inside the ghost's 120 km corridor mid-coast), the Ike mode is arrival-geometry variance (aim-periapsis lever: >= ~4,300 km alt or <= ~1,600 km alt vs the ~1,734-4,026 km shell), and a refused-backward-TimeJump forbidden token now makes the non-gating post-mission-step trap self-detecting. THE SEAM-INSTANT OBSERVATION now needs a CO-LOCATION design - hold the observer inside 120 km of the ghost AT the seam instant (a to-the-second co-departure, or a temporary zone-relax debug aid in the MapRenderWarpControl mold, product-side, ships-off) - then fly BOTH modes through the seam in-zone, pin faithful as baseline and the re-aim teleport as the GS-3 tolerate-plus-require regression target. **WATCH MODE IS NOT AN ALTERNATIVE TO THAT DESIGN - MEASURED, not argued (V4-player-loop-workflow, run `2026-08-08_1135`, PASS attempt 1).** The obvious cheap idea once `EnterWatchMode` was drivable was to let watch mode carry the observer to the ghost instead of building co-location. It cannot, and the disqualifying half is the CAMERA half: watch mode only re-aims the flight camera at a ghost (`WatchModeController` never moves, spawns, or re-anchors the observing vessel), so a granted watch changes nothing about where the observer IS, and its 300 km entry / 305 km exit cutoff is nowhere near the 47.9 Mm the seam sits at. NOTE THE DIRECTION OF THAT COMPARISON, which an earlier draft of this paragraph had INVERTED: 300 km > 120 km, so watch mode reaches FURTHER than the zone gate, not less - the two are separate thresholds on the same distance, and in the 120-300 km band a watch can be GRANTED on a ghost the engine has already stopped positioning. That widens the band where a watch is grantable and leaves the conclusion untouched: a camera re-aim capped at 300 km is not a route to a 47.9 Mm seam instant, so watch mode is disqualified and the co-location design still has to be built. What V4 measured on the way to that verdict is still useful to it: at the ghost's parked Duna tail the range term failed with a printed number - `GuardSkip reason=hidden-by-zone_distance=643913m`, i.e. 643.9 km on a shared ~1,038 km-radius orbit (this fixture's parked DD1 sits at alt 718,363 m over Duna: SMA 1,038,214.95, ecc 0.00127) whose maximum separation is ~2,077 km. A co-phased observer is therefore ~2x away from a granted watch at the ARRIVAL TAIL (not at the seam), which is the one place a phase-controlled fixture could buy watchability cheaply. V4 also QUALIFIED the zone arithmetic above: its one `hidden-by-zone` sample at the arrival-seam bracket read `distance=869479m` - 7.2x the 120 km gate, not the ~400x the 47.9 Mm SOI-radius argument predicts. ONE candidate for that survives and is worth resolving before the co-location design is sized: the icon driver's replay window starts AT the seam (`bounds=[28781184.5,28813228.5]`), so a pre-bounds ghost resolves to a clamped in-bounds position near Duna rather than to the SOI edge. The other candidate raised at the time - "the hide line prints `renderDistance`, cached separately from the `lastDistance` the watch conjunction reads" - is REFUTED: `ResolvePlaybackDistance` and `ResolvePlaybackActiveVesselDistance` (`GhostPlaybackEngine.cs:5693-5727`) are textually identical on the production path (the resolver overrides exist only for InGameTests) and a single `CachePlaybackDistances` call writes both fields immediately before the hide emit, so renderDistance == lastDistance in production - which STRENGTHENS the 643.9 km reading above, since `IsGhostWithinVisualRange` provably compared 643,913 against 300,000. CONSTRAINT (b) - the center-to-center periapsis structural doubt - REMAINS OPEN and is a DESIGN question; the faithful-mode SOI-sphere seam states that doubt needs are exactly what the completed companion flight will measure, so option 3 stays sequenced BEHIND that flight. Do NOT attempt option 3 until both are in hand.

**THE INSTRUMENT'S FIRST RAISE, 2026-08-11 (branch `eve-loop-lanes`, V8-eve-player-loop on the new `eve-orbit-recorded` fixture) - and it reached this defect class through a route nobody predicted.** The `seam-endpoint-outside-soi` lens raised for the first time in its existence, reproduced on all five bracketed runs (`2026-08-11_0807`/`_0810`/`_0814`/`_0818`/`_0819`) with every DISCRIMINATING field identical (ratio=4.6216, endpointDist=393342797m, soi=85109365m, seamUT=15673183.4, seed=no-seed, rendOrbit) - the one field that varies run-to-run is the `loopShift=` context (14,686,847.5-14,687,035.5, a 188 s spread; do NOT grep the full line as a verbatim marker). The _0807 line: `reason=seam-endpoint-outside-soi fromBody=Sun toBody=Eve seamUT=15673183.4 endpointDist=393342797m soi=85109365m ratio=4.6216 tol=1.0050 recordedSeamUT=15673183.4 clock=raw seed=no-seed loopShift=14687035.5 rendOrbit=[sma=11769252918 ecc=0.1552 body=Sun] lineActive=True`. THE ROUTE: the unit is ENGAGED (re-aim supported, Kerbin->Eve via Sun) but the cycle-0 window's synth DECLINED - the plane-tilt achievability gate refused ALL 27 tof candidates (`tilt-correction ... incAch=1.2358 targetInc=2.1000 tol=0.50 state=declined reason=unreachable-plane` x27; resolver verdict `synth failed across 27 tof candidates (recordedTof=3743340.79 geomTof=3679663.04 ...) (tilt correction: unreachable target plane ...) - faithful this window`) - so the window fell back to FAITHFUL and the recorded transfer, rendered one synodic late, misses the moved Eve by 4.62 SOI radii. Three things this measurement settles or reframes. (1) The instrument's first promotion blocker ("the raise has never fired; its detection path is unproven") is DISCHARGED - the detection path works, five-for-five. (2) Caveat (2) above ("a FAITHFUL loop replay raises it by design ... read seed=/loopShift= before calling a raise a defect") needs a sharper edge than it was written with: this faithful render was NOT user-chosen - `forceFaithfulLoopPlayback` is off, the mission is ENGAGED, and the degradation to a faithful window is silent per-window fallback. The player-facing outcome is exactly this entry's symptom (the looped ghost's transfer dead-ends in open space, no encounter into the destination SOI), so the "benign faithful population" and the defect population are not disjoint: a tilt-declined window puts an engaged mission INTO the missing-encounter class. Whether the right fix is a better plane treatment in the synth (so Eve windows stop declining), an explicit surfaced mode indicator, or option 3 remains a design call - NOT attempted here per this entry's own preconditions. (3) Eve's 2.1-deg inclination is the population `ReaimTransferSynthesizer`'s comments bracket between Duna's always-safe 0.06 and Moho's failing 7 - the decline is now MEASURED at one departure geometry (incAch constant 1.2358 across all 27 candidates). Whether other Eve windows (other k, other D0 phases) decline too is unmeasured; the lane observes cycle 0 only. INSTRUMENT CAVEAT measured alongside: the census line and the raise are sampled on different frames, and the 5 s shared-key rate limiter means the surviving `seam-endpoint summary evaluated=1 outsideSoi=0` line can coexist with a raise in the same run - count raises off the `phase=Anomaly` line, never off the census, when both appear.

**DIAGNOSIS SETTLED, 2026-08-11 (branch `reaim-inclined-targets`, Phase 0 - tests + docs, no behavior change).** The first raise above is now understood end to end, and the arithmetic behind it is pinned in-repo rather than read off a log. (1) THE GATE CANNOT SEE r2. Both inputs to the achievability gate are candidate-invariant: r1 is fixed at departureUT, and `nTarget = normalize(r2 x v2Target)` is the target orbit's PLANE normal, which a Kepler orbit carries unchanged wherever the target sits on it. So `incAch` is forced to be identical for all 27 tof candidates - the logged `incAch CONSTANT at 1.2358` is arithmetic, not a numerical curiosity, and no widening of the tof search can ever rescue this window. (2) THE TILT IS LOAD-BEARING FOR EVE, NOT ERROR. `UvLambert` returns v1 in span(r1, r2), so the solved conic's plane IS plane(r1, r2) - the only plane a single center-to-center conic through both endpoints can use - and at this window it exceeds the 2.6-deg bound for every candidate. Eve is ~271 Mm out of the departure plane at arrival, so the flatten the correction would have applied misses Eve by ~288 Mm = 3.4x its 85.1 Mm SOI (modelled 270.7-289.6 Mm across the band). The recorded flight reached Eve by a MID-COURSE PLANE CHANGE (fixture Sun legs: 0.0021 deg flat, then 2.0627 on Eve's own plane, then 2.0689 on approach), which no single conic can imitate. (3) THEREFORE THE DEFECT IS THE DISPOSITION, NOT THE GATE ARITHMETIC. As a correction-safety question the gate answered correctly (flattening here would miss); what is wrong is that "cannot safely correct" is implemented as "kill the candidate", when the un-corrected conic already in hand is sane, prograde, and passes through Eve's center at arrival by construction. FIX PLAN: `docs/dev/plans/reaim-inclined-target-tilt-retention.md` (Design A, tilt retention - skip the correction, keep the conic, let the downstream encounter check remain the arbiter). Phase 0 landed the measurement as headless cells: `ReaimTransferSynthesizerTests` (E1: incAch = 1.2358 and invariant across all 27 candidates, the gate refusing every one, the flatten miss > 3x SOI, plane(r1,r2) inc > bound for every candidate, transfer angle 174.99 deg, and - the strongest of them - the modelled per-candidate transfer-plane inclinations reproducing run `2026-08-11_0818`'s 27-line `inc-before` sequence term by term to four decimals, 3.3128-87.4793, in log order; E3: the fixture's broken-plane Sun chain, which kills Design D and scopes Design C out) and `UvLambertTests` (E2: the solve converges prograde and reaches r2 at step 0 and both band edges - there is no solver defect here). Nothing about the product changed in Phase 0.

**THE BENIGN POPULATION MEASURED THE SAME DAY (V8F-eve-loop-faithful, runs `2026-08-11_0853`/`_0854`/`_0857`) - and it settles the gating question in the negative.** The deliberate-faithful A/B lane (forceFaithfulLoopPlayback on, same fixture) raises FIVE times per run, reproducing to four decimals: four Sun->Eve arrival seams - one per self-overlap instance of the forced-faithful unit (overlapCadence = span/20; the ENGAGED unit does not overlap), ratios 52.6957 / 47.5095 / 138.2108 / 203.2006, growing with each instance's later clock - plus a Kerbin->Mun TRANSIT seam at 4.8024: a moon encounter recorded inside the span does not recur at shifted epochs, a benign shape caveat (2) had not catalogued. V8F pins `outsideSoi=[1-9]` as REQUIRED - the first lane where a raise is the designed reading. THE CALIBRATION FACT: the benign ratios (4.80-203.2) STRADDLE the defect reading (4.6216 above), so ratio alone CANNOT separate the classes - any future promotion of this token must discriminate on unit mode / seed provenance (engaged-with-declined-window vs deliberate-faithful vs re-aimed), which the raise line today carries only as context fields. Recorded on the `hlib.ANOMALY_REASONS_RAISED_UNGATED` blocker prose in the same commit.

**PHASE 1 LANDED, 2026-08-11 (branch `reaim-inclined-targets`) - the disposition is now RETAIN, and the V8 lane is expected to red until Phase 3 re-pins it.** `ReaimTransferSynthesizer.TrySynthesizeTransfer`'s tilt seam routes through a new pure `DecideTiltDisposition(incBefore, bound, planeGateSafe)` returning `Noop | Fire | Retain`. The unreachable-plane arm - previously `return false`, which killed the candidate and (when every candidate died) dropped the whole window to the faithful recorded transfer - now RETAINS: the correction is skipped, the un-corrected conic is KEPT, and control falls through to exactly the validation a Noop conic takes (the sane + direction guards already run upstream, then `CalculatePatch` + the proximity encounter check). The encounter check remains the arbiter - a retained conic that never enters the target SOI declines exactly as it did before - and no downstream guard was bypassed or reordered. There is deliberately NO near-90 special case: the measured Eve band tops out at 87.4793 deg, ~2.5 deg under the line where `IsRetrogradeTransfer` flips, and a candidate that crosses it is declined by the unchanged DIRECTION guard upstream (`IsExcessiveTiltTransfer`'s `inc <= 90` clause makes the tilt seam stand down entirely past 90, so a retain can never smuggle a retrograde conic through). NEW LOG LINE, the existing field grammar with a new state token: `tilt-correction inc-before=... bound=... targetInc=... incAch=... inc-after=NaN state=retained reason=unreachable-plane`; the `fired` / `noop` / `declined` emissions are byte-identical to before. COUNTERS: new `RetainedTiltCount`; `UnreachablePlaneDeclineCount` KEPT and still incremented on this branch, with its meaning WIDENED from "unreachable-plane declines" to "unreachable-plane GATE HITS, retained or declined" - renaming it or stopping the increment would silently change what the in-game Duna NEVER-UNREACHABLE invariant (`ReaimEndToEndInGameTest.cs:~384-417 (the Duna NEVER-UNREACHABLE invariant; anchor updated post-review)`) measures, and that invariant still relies on it staying ZERO for Duna, whose gate passes at every r1 phase so the retain arm is unreachable for it by arithmetic. `DeclinedCorrectionCount` is NOT incremented on a retain (a retain is not a decline; the candidate survives). ALSO SHIPPED (supervisor disposition on the plan's open question 1): a new grep-stable Warn at the resolver's all-candidates-failed site, `reaim window fell back faithful: member=<id> window=<k> candidates=<n> reason=<failReason>`, ADJACENT to the existing Verbose `... - faithful this window` line, whose text and level are untouched (lane contracts pin it) - this fix removes the MEASURED route into the faithful fallback, not the class (Lambert non-convergence and the other fail-closed branches still reach it), so the remainder is now loud instead of inferred from a Verbose line nobody grepped. **THE V8 LANE WILL RED ON THE NEXT FLIGHT OF THIS DLL, BY DESIGN:** its armed revision pins the OLD failure trio (`tilt correction: unreachable target plane`, `faithful this window`, `reason=seam-endpoint-outside-soi fromBody=Sun toBody=Eve`) as REQUIRED tokens - which is exactly the GS-3-style regression floor doing its job, forcing a re-read when the disposition changes. The recorded next steps are the plan's Phase 2 (in-game cells + BATCH tally re-pin) and Phase 3 (V8/V8T re-pin choreography: reading -> armed -> negative control; V8F unchanged, since forced-faithful bypasses the synth). Do NOT read that red as a defect without checking this note first.

**LANE-PROVEN, 2026-08-11 (branch `reaim-inclined-targets`, Phase 3 of the tilt-retention plan).** The fix's route into this entry's defect class is CLOSED BY MEASUREMENT at lane grade: V8-eve-player-loop's regression floor red BY DESIGN on the fixed DLL (run `_1242`: exactly the three pre-fix required tokens mismatched, nothing else) and the same log measured the healthy state - `re-aimed transfer ready` with `devFromRecorded=0s` (the step-0 candidate, i.e. the recorded tof, won), `tilt-correction ... state=retained` at the modeled inc-before=18.5369, and the seam-endpoint census flipped from the ratio=4.6216 raise to `evaluated=1 outsideSoi=0` - the replayed Kerbin->Eve transfer now arrives inside the sphere at the recorded seam instant. The lane is re-pinned to REQUIRE that healthy state with the old failure trio + the new fallback Warn inverted into forbidden (readings `_1244`/`_1245`, control `_1246`), V8T re-pinned to the now-genuinely `reaimed=True` TS chain (`_1247` baseline red, `_1252`/`_1253` green), and V8F measured byte-identical (`_1250`) - the forced-faithful population, including its five benign raises, is untouched. THE REMAINDER OF THIS ENTRY STAYS OPEN: the Duna 1.027/1.043 SOI-handoff endpoint residual is orthogonal to the tilt disposition (option 3 territory; precondition (b) still open), and the deliberate-faithful benign population still raises by design.

## FAITHFUL-PARITY-SEED-FRAGMENT-REBASE - the faithful render-parity lens stands down on FAITHFUL members whenever the Director's arc window coalesces same-conic segment fragments (measured 2026-08-09, branch `loop-geometry-gates`; REPORT-ONLY today, no verdict moves)

**What was expected and what is actually happening.** `MapRenderProbe.ComputeFaithfulOrbitParity`'s RE-AIM GATE (the `intendedSeed.HasValue && !AreSameConicElements(intendedSeed.Value, covering)` guard in `MapRenderProbe.ComputeFaithfulOrbitParity`) skips a sample with `reaimed-or-foreign-seed` when the Director's fresh StockConic seed is not the covering recorded segment, and its own comment says the faithful case is safe because "a faithful member's seed IS the recorded segment (fed verbatim)", with only "a transient seed-vs-covering mismatch at a segment boundary" skipping one frame. THAT IS NOT WHAT THE MOON LANES MEASURE. V6M-mun-player-loop is `reaimed=False`, phase-locked, same-parent - as faithful as this suite has - and its every archived and re-flown reading is `faithful-parity summary sampled=0 overTolerance=0 skip.reaimed-or-foreign-seed=1`.

**Mechanism, measured end to end from `logs/2026-08-08_1945_V6M-mun-player-loop` + `harness/results/2026-08-09_0928_V6M-mun-player-loop_shots` + the fixture's own `.prec`:**

1. The fixture's main recording (`fixtures/saves/mun-orbit-recorded`, `0fd603e389b94d6488d92f4e3c6b7957.prec`) carries THREE consecutive Mun ORBIT_SEGMENTs that are the SAME conic to ~1e-8 - `sma -509906.891`, `ecc 1.66595698`, `inc 0.764307023`, `lan 58.9508314`, `argPe 331.9699606` - and differ only in their PHASE REFERENCE:

   | # | span | epoch | mna |
   |---|------|-------|-----|
   | 8 | [16404.98191, 16448.97083] | 16404.520477891452 | -3.6058019222 |
   | 9 | [16448.99083, 16450.07248] | 16448.990834045900 | -3.5746307725 |
   | 10 | [16450.59248, 20939.02031] | 16450.592479553725 | -3.5735081116 |

2. The Director coalesces them into ONE arc window - `Loop arc-window coalesced ... fragments=3 segIndices=8-10 rawWindowUT=16404.98-20939.02` - and seeds its StockConic from FRAGMENT 8. Proof from the drive line: `8a director-drive ... shift=280142.5 epoch+shift=296547.0 seedBody=Mun`, i.e. seed epoch 16404.52, fragment 8's epoch to the digit.
3. The faithful lens resolves its covering segment on the RAW `rec.OrbitSegments` list at the recorded clock and, at the park epoch (`effUT=20834.6`), lands on FRAGMENT 10 - printed by `[MapTraj] FLIGHT covering-segment CHANGED: member=1 effUT=20834.6 body=Mun covered=True source=Segment effSegs=11 seg=[16450.6,20939.0] sma=-509907 ecc=1.666`.
4. `MapRenderProbe.AreSameConicElements` requires `|dEpoch| <= 1e-3` AND `|dMna| <= 1e-3` alongside the five shape terms. Measured `dEpoch = 46.072 s` and `dMna = 0.032294 rad`: the five shape terms match to ~1e-8 and the two phase-reference terms fail by four orders of magnitude.

**So the skip is DETERMINISTIC, not transient** - it holds for every frame the drive clock spends past fragment 8 inside that window, which on this fixture is 4,488 s of recorded time: the entire Mun arrival plus park tail, i.e. exactly the phase the lane exists to measure. The lens is blind there, and its silence has been read as "clean" on every sign-off since.

**Same signature on every family fixture measured (all three re-derived from the fixtures' own segment chains + the 2026-08-09 re-flights):**
- **Duna** (`duna-direct-recorded`, V4): segments 12-15 are one conic (`sma -137763.921`, `ecc 8.53620347`); seed epoch 9,128,108.707 = segment 12, raw covering at lookupUT 9,159,865.5 = segment 15 (epoch 9,159,565.442), `dEpoch = 31,456.7 s`. **This one matters most for how the reading has been described:** V4 is a re-aimed MEMBER, so its `reaimed-or-foreign-seed` looked like the designed skip - but re-aim is applied per SEGMENT, the parked Duna tail is a FAITHFUL leg, and the seed there is a recorded conic fed verbatim. V4's skip is this bug, not the design.
- **Minmus** (`minmus-orbit-recorded`, V7M/V7T): segments 6-9 are one conic (`sma -32530.7485`, `ecc 4.02686086`); seed epoch 267,736.679 = segment 6, raw covering at 276,934.7 = segment 9 (epoch 276,411.273).
- **V6T** reads the identical skip on V6M's fixture.

**Why V7M/V7T nonetheless read `sampled=1`, and why that is NOT evidence the gate is fine.** CORRECTED 2026-08-09 - an earlier draft of this paragraph said their summary lands on a frame where the seed is not fresh, so `intendedSeed` is null and "the re-aim gate is BYPASSED ENTIRELY ... the sample is taken only because the guard did not run". THAT MECHANISM IS FALSE, and the branch's own V4 reading falsifies the indicator it rested on: V4's frame 6464 pairs `8a director-drive ... fresh=False ... epoch+shift=NaN seedBody=-` with a summary reading `skip.reaimed-or-foreign-seed=1` on that same frame - a skip only REACHABLE with a non-null seed. The `fresh=False` on the drive line is stamped by the icon-drive patch at execution order 0; the shadow stamps the seed mid-frame after it, and the probe fetches the seed ITSELF at order 10000, so a `fresh=False` drive line does not describe what the probe saw. On V7M's and V7T's own summary frames the post-shadow `director-stockconic-visible` line (which gates on the same freshness call) is present, so the seed WAS fresh at probe time, `intendedSeed` was non-null, the gate RAN and it PASSED. THE SURVIVING MECHANISM, and the one the artifacts do support: those summary frames sit at a drive clock still inside the SEED fragment (V7T frame 7706, driveUT 267,883.7, against fragment-6 epoch 267,736.7), so seed == covering there and the gate passes, while later frames in the same coalesced window skip. THE CONCLUSION IS UNCHANGED - do not pin `sampled=[1-9]` on V7M/V7T - but it now rests on the fragment geometry rather than on a bypassed guard. What no artifact records either way is `seedKind`, which prints only on a raise and occurs zero times in the whole results corpus.

**Blast radius / severity.** REPORT-ONLY: the skip suppresses a MEASUREMENT, it changes no draw and moves no verdict, and `overTolerance` has measured 0 wherever the lens did sample. The cost is coverage - the top regression class this lens exists to catch (a wrong-elements or off-phase faithful loop draw) is unguarded across every coalesced multi-fragment arc window, which is the normal shape of a moon or planet ARRIVAL. This is the same class of failure as the stack-review BLOCKER recorded in `ComputeFaithfulOrbitParity`'s own comment ("silently skipped every loop-shifted ghost in production ... while the sign-off read zero drift"): a silent skip reading as a clean pass.

**Do NOT fix it by loosening `AreSameConicElements`' epoch/mna tolerances** - those two terms are what make the predicate able to tell a re-aimed conic from a recorded one at all, and a loose epoch compare would re-admit exactly the 18-32 Gm false positive the RE-AIM GATE comment records. Two directions that keep the discrimination: (a) compare the seed against the covering segment's conic AFTER re-basing one to the other's epoch (propagate mna forward by `dEpoch` on the shared elements and compare the propagated phase), so one physical arc fragmented N ways compares equal while a genuinely different conic still differs; or (b) resolve the covering segment on the SAME effective/coalesced list the Director seeds from rather than on raw `rec.OrbitSegments`, so seed and covering are drawn from one list by construction. (b) is smaller but couples the lens to the coalescing rule; (a) is the honest geometric statement. Either needs a headless test over the three fixture chains above, all of which are committed.

**Instrumentation gap that made this expensive to find, worth closing with the fix:** per-frame skips are counted into `faithfulParitySkipCounts` and reported ONLY through the 5.0 s rate-limited summary, which on these spec shapes emits ONCE. There is no per-skip line and no run-total, so "the lens skipped every frame" and "the lens skipped one frame" are indistinguishable in a collected log; both readings above had to be reconstructed from the drive line, the `[MapTraj]` covering line and the fixture `.prec`.

**Guarded meanwhile:** all five tracer-armed V lanes (V4/V6M/V6T/V7M/V7T) now REQUIRE `faithful-parity summary sampled=\d+ overTolerance=\d+` (presence - the `MapRenderProbe.LateUpdate` emit is gated on `sampled > 0 || skipCounts.Count > 0`, so it can only appear once the per-pid pass actually reached a resolved ghost) and FORBID the `overTolerance=[1-9][0-9]*` form. Deliberately NOT pinned: `sampled=[1-9]`, which would gate on this bug.

**EVE DATA POINTS, 2026-08-11 (branch `eve-loop-lanes`).** V8-eve-player-loop measured `faithful-parity summary sampled=1 overTolerance=0` on every bracketed run - the first non-zero `sampled` on an interplanetary loop lane (the Duna engaged lanes all read `sampled=0` via this entry's standdown). The sampled leg is the Eve unit's tilt-declined FAITHFUL-window transfer (see the first-raise paragraph in the 2026-06-15 entry below), which parity-resolves where the re-aimed Duna legs stand down - consistent with this entry's diagnosis that the standdown keys on the seed, not on faithfulness. The TS sibling V8T measured the OTHER side at its post-seam epoch: `sampled=0 skip.reaimed-or-foreign-seed=1` - the TS chain's seed lands back in this entry's standdown even on the same faithful-window unit, so the standdown is scene-path-dependent, not just member-dependent. ALSO MEASURED, same V8 runs: a parity-skip variant this entry has not previously catalogued - `Synth parity skipped: rendered orbit epoch=15174018.619 is not the baked convention (seg.epoch=15673182.924 + loopShift=14687035.5) - UNEXPLAINED epoch convention` - where the rendered epoch equals the RAW recorded epoch of a DIFFERENT (earlier) segment of the same Sun leg, unshifted. Same family (the lens's epoch-convention resolution disagreeing with the Director's fragment choice) on a new shape; filed here rather than as a new entry.

## TS-LOADGAME-RECORDING-ACTIVE-RACE - the scene-entry recorder re-arms after a StopRecording/DiscardTree pair and REJECTS the next `LoadGame` (SECOND SIGHTING 2026-08-09; V5's re-kill mitigation narrows the window, it does not close it)

**What happens.** A `seam`-driver spec that re-enters a second scene mid-run must kill the live recorder first - `TestCommandDispatcher` refuses `LoadGame` with `msg=recording-active` by design, so the load never silently discards a live recording. The TS lanes therefore issue `StopRecording` + `DiscardTree` immediately before the load. On some scene-entry orderings a scene-entry recorder RE-ARMS after that pair and before the load lands, and the load is rejected. The run is driver-INVALID: the second half of the declared sequence never executes.

**Sighting 1 (2026-08-08, V5-ts-loop-arrival run 1 attempt 1).** Measured: promotion-recorder start 50.294 -> our StopRecording 50.599 -> a SECOND start 50.842 -> `reject LoadGame reason=recording-active`. Attempt 2 on the identical spec saw no second start. The mitigation adopted then was a re-kill pair placed immediately before the load, described in V5's write-up (and inherited verbatim into V6T / V7T) as "the only placement that makes the outcome deterministic".

**Sighting 2 (2026-08-09, V7T-minmus-ts-arrival, seam-endpoint census run `2026-08-09_1020` attempt 1) - AND IT HAD THE RE-KILL PAIR.** Artifacts, all committed: `harness/results/2026-08-09_1020_V7T-minmus-ts-arrival.json` reads `verdict=INVALID`, `subkind=driver-verdict-mismatch`, wall 45 s, `driverValidity {status: FAIL}`, driver step `{id: 0013, cmd: LoadGame, expect: OK, verdict: REJECTED, met: false}` with every other step met; `harness/results/2026-08-09_102005_harness.log` reads `[Classify] verdict=INVALID ... reason=driver stage failed` then `[Retry] ... attempt=2 reason=INVALID`. The timeline in `harness/results/2026-08-09_1020_V7T-minmus-ts-arrival_shots/KSP.log` is V5's mechanism verbatim, one step further along:

```
13:20:39.913  exec id=0011 cmd=StopRecording  verdict=OK      <- the re-kill pair
13:20:40.171  exec id=0012 cmd=DiscardTree    verdict=OK
13:20:40.179  [Flight] ResumeCommittedActiveRecording: resumed committed recording ... pid=2708531065
13:20:40.182  [PhysicsPatch] Active recorder attached
13:20:40.406  [TestCommands] reject id=0013 cmd=LoadGame reason=recording-active
```

227 ms between the pair's `DiscardTree` and the re-arm. Attempt 2 executed the same step OK on the same fixture and same spec. **So the re-kill pair narrows the window; it does not close it, and "deterministic" is retracted.** Note the re-arm reason differs from V5's: this one is `ResumeCommittedActiveRecording` (the committed-tree resume path), not the fresh scene-entry promotion - the same outcome from an adjacent producer, which is a hint about where a real fix belongs.

**Why this is filed rather than folded into V7T's finding.** V6T's spec header states the contract explicitly: a recurrence on this fixture "is a corroboration of V5's finding, to be recorded, not re-diagnosed". The record nearly did not happen - the census write-up characterised all three V7T attempts as `PARSEK-FAIL(anomaly)` on `icon-off-orbit`, which is the harness's own mission-vs-Parsek orthogonality inverted: a driver-INVALID has to surface AS a driver flake, or the next stage-13 rejection reads as new. `harness/coverage/flake.json` carries the consequence the prose could not explain - `V7T-minmus-ts-arrival {numerator: 1, quarantined: true, rate: 0.25, total: 4}` (the numerator is INVALID + KILLED).

**Impact and non-impact.** No product behavior is wrong here in the sense of a wrong render or a wrong ledger - the dispatcher's refusal is a deliberate guard doing its job. What is wrong is that a recorder re-arms unbidden inside a window a spec explicitly cleared, which makes any TS-re-entry lane nondeterministic at ~1-in-4 on the evidence so far. It is also material to a deferred decision: V7T's arming is deferred until the lane "can go green on its own terms", and this is evidence the lane cannot reliably reach its anomaly evaluation at all.

**Not yet established** (do not re-diagnose from scratch next time - extend this list): whether the two re-arm producers share a trigger; whether the window is bounded by a frame count or by scene-settle timing; and whether a dispatcher-side fix (retry `LoadGame` once on `recording-active`, or have `DiscardTree` set a short re-arm inhibit) is preferable to a producer-side one. Nothing here should be treated as a spec-authoring problem: three specs already do the documented mitigation and one of them still lost.

**Sighting 3 (2026-08-11, V8T-eve-ts-arrival reading run `2026-08-11_0835` attempt 1, branch `eve-loop-lanes`) - a THIRD lane, same shape, mitigation present.** The Eve TS lane's first outing died identically: every step met through the re-kill pair, then the TS `LoadGame` (step 13) `verdict=REJECTED`, run `INVALID(driver-verdict-mismatch)`, retry-once absorbed it (`_0836` attempt 2 PASS, every step met, clean sweep). Artifacts: `harness/results/2026-08-11_0835_V8T-eve-ts-arrival.json` + the collected `logs/2026-08-11_1136_V8T-eve-ts-arrival/` folder. This corroborates the ~1-in-4 nondeterminism estimate on a THIRD fixture (eve-orbit-recorded) and a third spec carrying the documented mitigation; recorded per V6T's contract (a corroboration, not a re-diagnosis).

## REVERT-BLANKET-CLEARS-PRE-FLIGHT-TERMINAL - Revert AND Rewind may retract a genuine PRE-flight terminal verdict, not just the stale post-spawn one, with no restore leg on either path (noticed 2026-08-29 while fixing TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE; NOT measured, filed rather than chased)

**The observation, from reading the code the TS fix touches.** `ParsekScenario.OnLoad`'s `tree-mutable-state` reset calls `ClearPostSpawnTerminalState` on every committed tree recording on EVERY in-session load, revert included. Its rationale is revert-shaped and correct as far as it goes: "the spawn is undone, so a verdict the SPAWNED vessel earned is stale". But the predicate it actually applies is `VesselSpawned && (Destroyed || Recovered)` - it cannot tell a verdict earned DURING the reverted flight from one the recording already carried BEFORE it. A recording that was spawned in an earlier session and destroyed then, still carrying `VesselSpawned=true` and `Destroyed` at the moment the reverted flight launched, is retracted by the same blanket clear, and on the revert branch nothing restores it.

**TWO paths, one shape.** The same unrestorable retraction runs on the REWIND / revert `ResetAllPlaybackState` path via `RecordingStore.ResetRecordingPlaybackFields`, which sweeps ALL committed recordings and (as of the TS fix) shares the very same `ClearPostSpawnTerminalState` seam - deliberately, so the predicate cannot drift, but that path has no restore leg of any kind and no saved node in hand to build one from. Whatever is decided for Revert has to be decided for Rewind too; do not fix one and leave the other.

**Why the TS fix does not cover either, on purpose.** The restore added there is gated `if (!isRevert)` and lives in the OnLoad restore loop, so revert behaviour is unchanged by that PR and the rewind path is untouched - deliberately, because both are delicate and neither case was ever measured. The comment at the OnLoad call site names this entry.

**Why the obvious fix is not obviously right.** The reset's block comment asserts "on revert, the launch quicksave has no tree nodes so this reset is the only thing that runs". That may be stale: `SaveTreeRecordings` writes a `RECORDING_TREE` node for every committed tree on every OnSave, so a launch quicksave taken with committed trees present SHOULD carry those nodes - and the sibling `tree-state-restore` loop already treats them as authoritative on revert (it restores `spawnedPid` / `VesselSpawned` / `lastResIdx` / the BUG-C abandon fields from them, on the revert branch too). If that is right, simply dropping the `!isRevert` gate would restore each verdict to what the revert TARGET save says, which is the correct post-revert answer in both directions - the genuine pre-flight verdict comes back, and a verdict earned during the reverted flight stays cleared because the target save predates it.

**What to establish before touching it** (do not re-derive from scratch): (1) does a launch / revert-target quicksave actually carry `RECORDING_TREE` nodes with `terminalState` keys - read one off disk rather than trusting the comment; (2) does anything gate re-spawn eligibility on `TerminalStateValue` such that restoring `Destroyed` after a revert would suppress a spawn that should happen; (3) what the REWIND path could restore from, given it holds no saved node - the rewind quicksave is the obvious candidate and is already read for the pid whitelist; (4) whether the block comment needs correcting either way. The headless seam is already in place - `ParsekScenario.RestoreClearedPostSpawnTerminalState` plus the `ApplyResetLeg` helper in `SceneChangeTerminalStatePreservationTests` - so the Revert half is one gate removal plus cells once (1), (2) and (4) are answered; the Rewind half needs a source for the truth first.

## REAIM-TILT-NOOP-AT-EELOO-6.15-DEG - the tilt-RETENTION branch is still unexercised at the highest stock inclination below Moho, because the synthesized conic came in BELOW the bound (measured 2026-08-13, branch `eeloo-loop-lanes`, four green V12A-eeloo-loop-arrival runs; NOT a defect, and NOT a widening of the tilt plan's claim scope)

**The measurement, byte-identical on all four runs** (`2026-08-13_0120`, `_1513`, `_1515`, `_1536`):

```
[ReaimSeam] tilt-correction inc-before=4.0725 bound=6.6500 targetInc=6.1500 incAch=NaN inc-after=4.0725 state=noop reason=in-plane
```

The prediction was `state=retained reason=unreachable-plane`, extrapolated from the two measured points (incAch/targetInc = 0.588 at Eve, 0.729 at Dres). It is not what happened, and the miss is STRUCTURAL rather than marginal: `inc-before` 4.0725 deg is BELOW the 6.6500 deg bound, so `IsExcessiveTiltTransfer(incBefore, tiltBound)` is FALSE and control never enters the excessive branch. `incAch` is NaN because it is only computed inside that branch (`ReaimTransferSynthesizer.cs`, the `if (IsExcessiveTiltTransfer(...))` gate), and `inc-after == inc-before` because nothing was touched. **Eeloo's 6.15 deg did not force a tilt on this geometry.**

**Why this does NOT widen the tilt plan's claim scope.** The plan scopes itself "Eve only ... Moho/Dres/Eeloo are unmeasured collateral". Eeloo is the highest inclination the retention arm has ever been AIMED at - Eve 2.1 -> Dres 5.0 -> Eeloo 6.15, the last stock step below Moho's 7 - and the retention arm was STILL NOT REACHED, because **the retention branch lives inside the excessive-tilt gate and the gate never opened.** So Eeloo tested the BOUND ARITHMETIC (6.65 = Max(Max(0, 6.15), 0) + `InclinationToleranceDegrees` 0.5, confirmed to the digit) and confirmed the not-excessive arm LOGS a Noop rather than falling silent - and nothing else. The retention branch remains Eve-only-validated plus Dres's `state=retained` reading. **Any note reading "Eeloo validated retention" would be false.**

**What would actually exercise it, and it is not what the program assumed.** A geometry whose SOLVED transfer inclination exceeds the bound - NOT a higher-inclination TARGET. Target inclination sets the BOUND, so raising it makes the gate HARDER to trip; what trips the gate is the Lambert solution's own plane. Dres reached `inc-before=13.1958` against a 5.5000 bound on a shorter, more bent transfer, while this Eeloo window solves nearly in-plane at 4.0725 because a 484-day near-Hohmann coast barely leaves the ecliptic. Moho (7 deg target, but a fast bent INNER transfer) is the better candidate, and it needs a NEW FIXTURE, not a re-aim of this one.

**Recorded as a FINDING, not a failure, per the lane's own posture** - V12A declared all four tilt outcomes findings-to-report-verbatim in advance, and the noop literal is now ARMED as its regression floor (with `bound=6.6500 targetInc=6.1500` armed separately so a red distinguishes "the solved conic moved" from "the bound arithmetic moved"; proved on the negative control `2026-08-13_1537`, where inverting one digit of `inc-before` red the literal while the pair still matched). Note also that a Noop DOES emit a tilt line, so ABSENCE of a tilt line means the synth never reached the tilt block at all - a different and larger finding.

## LINE-BLINK-EXEMPTION-DOES-NOT-PIN-THE-BOUNDARY - the window-transition exemption proves "one half read Outside, the other Inside", not "the SAME boundary was crossed" (found by review 2026-08-14, branch `line-blink-census`; UNEVIDENCED across all 13 archived raises; filed rather than fixed, and the OBVIOUS fix is measurably the wrong one)

**The gap.** `MapRenderTrace.ResolveWindowTransitionExempt` exempts a `line-blink` pair
when one half classifies `WindowExitOff` and the other `InsideWindowOn`. Each half is a
real measurement made by the decision site whose branch condition IS that measurement -
but `LineRenderIntent` carries only the three-state `RenderWindowCoverage`, never the
BOUNDS the site measured against. So the exemption proves two independent claims and
presents them as one transition across a single boundary. Two consequences:

**(a) A bounds FLAP is indistinguishable from a clock transition.** If the window moves
while the clock does not, the same clock reads Inside on one frame and Outside on the
next, and a genuine one-frame dark flash is exempted. This is INHERENT to the exemption's
premise as the design authority now states it: the instrument has no bounds-stability
signal and never had one. It is not a regression introduced by this work - the same
blindness sat behind the un-exempted detector, which simply raised on everything.
**And the flap is not hypothetical:** in `logs/2026-08-12_0627_V10-dres-loop-arrival`
one ghost's bounds walk `[31276682.1,43162584.0]` (frame 7189) ->
`[31276682.7,43162584.5]` (7218) -> `[31276742.8,43162644.6]` (7248) while the lane
re-aims, i.e. the window edge advances ~0.02 s per frame on a re-aiming lane.

**(b) The sharper half: the two halves prove their claims against STRUCTURALLY DIFFERENT
bound sets.** `director-stockconic-visible` stamps `Inside` after checking
`appliedBoundsCoverHead` - the APPLIED SEGMENT bounds (`segStartUT`/`segEndUT`). The
`past-body-frame-end` / `before-body-frame-start` block stamps `Outside` against the
BODY-FRAME bounds (`startUT`/`endUT`). Nothing makes those the same interval, so the
exemption can in principle be satisfied by a pair that never crossed one boundary at all.

**UNEVIDENCED, and measured that way rather than asserted.** Across all 13 archived
raises there is no pair whose exemption rests on mismatched boundaries.

**THE OBVIOUS FIX - "carry startUT/endUT and require the two halves' bounds to be equal" -
IS NOT THE FIX, and the reason is a measurement that also CORRECTS the prediction that
motivated this entry.** The review expected equality to break 5 of the 6 now-exempted
raises, on the reasoning in (b): five of them pair an applied-segment `Inside` with a
body-frame `Outside`, so their bounds "must" differ. Read against the logs, **that is
wrong - all six pairs carry BYTE-IDENTICAL bounds on both halves**:

| raise | dark half | lit half | bounds (both halves) |
|---|---|---|---|
| V8 `_1111` | f7843 `past-body-frame-end` | f7839 `director-stockconic-visible` | `[30360218.8,30450249.6]` |
| V8 `_1114` | f7618 `past-body-frame-end` | f7611 `visible-body-frame` | `[26616878.0,30360218.8]` |
| V10 `_0627` | f7218 `before-body-frame-start` | f7219 `director-stockconic-visible` | `[31276682.7,43162584.5]` |
| V10 `_0630`a | f7232 `before-body-frame-start` | f7233 `director-stockconic-visible` | `[31276552.7,43162454.6]` |
| V10 `_0630`b | f7262 `before-body-frame-start` | f7263 `director-stockconic-visible` | `[31276682.5,43162584.3]` |
| V10 `_0632` | f7237 `before-body-frame-start` | f7238 `director-stockconic-visible` | `[31276442.6,43162344.4]` |

So (b) is a STRUCTURAL gap, not an observed divergence: on this corpus the applied
segment and the body frame COINCIDE numerically at every pair, and only `_1114` is a
both-halves-body-frame pair by construction. Equality would therefore have un-exempted
NOTHING today - the lanes would stay green.

**Which is exactly why equality is still the wrong rule.** It is satisfiable on the
corpus by coincidence, and it is fragile against the drift measured in (a): the pairs
that satisfy it are 1 frame apart (V10) or 4-7 frames apart with a window that happened
not to move (V8), while the same logs show the window moving ~0.02 s/frame during a
re-aim. An equality check is therefore a TOLERANCE question disguised as an identity one,
and picking a tolerance without measuring the drift distribution is the trap the rest of
this work avoided. Whoever picks this up needs a COHERENCE rule designed from measurement
- e.g. what relationship the `Inside` bounds must bear to the `Outside` bounds for the two
to describe one boundary (containment? shared edge? edge within a measured drift budget?)
- and the first step is measuring how the applied-segment and body-frame intervals relate
across a corpus, not adding an `==`.

**Cross-reference:** the exemption itself and its cannot-mask argument are the resolved
LINE-BLINK-JUMP-STRADDLE-DETECTOR-GAP entry below; this entry is the one thing that
review left standing, and it is a sharpening of the exemption's PREMISE rather than a
hole in its implementation.

## TODO - D11 `loiter-compression` is UNCOVERED and ~~CANNOT be covered by any committed fixture~~ NOW REACHABLE (first non-zero measurement 2026-08-11, branch `eve-loop-lanes`; the cell itself stays UNCLAIMED until a lane pins a cut token)

**SUPERSEDING MEASUREMENT (2026-08-11, branch `eve-loop-lanes`).** The
`eve-orbit-recorded` fixture (harvested from B16-eve-orbit's re-fly
`2026-08-11_0718`) is exactly the launch-side-loiter shape option (a) below
describes - B15/B16 never adopted `padAlignEjection`, so the recorded flight
waits out the Kerbin->Eve ejection window in LKO for 11,827,993 game-s - and
arming a loop on it produced the corpus's FIRST non-zero cut, on every one of
six V8-eve-player-loop runs: `loiterCuts=1 cutSeconds=11819849` with
`cut#0 start=2492 len=11819849 end=11822341` and
`compressedSpan=3944147/15763996` (first: run `2026-08-11_0802`). Option (a)'s
honestly-stated autowarp cost was paid INSIDE the B16 mission itself (MechJeb's
NodeExecutor autowarp carries the wait inside B16's measured 1,843 s wall), so
the fixture bought the loiter without any new flight shape. The cell is still
NOT claimed: per CLAIM-IS-NOT-GATE it belongs to the V8 arming commit that pins
a cut token, and `dest-trim` (option (b)) remains unexercised - only the
launch-side cut has ever fired. The paragraphs below are the original
2026-08-09 analysis, kept because its structural halves (the moons' phase-lock
unreachability, the duna fixture's sub-one-rev park, option (b)'s two gaps,
option (c)'s empty-cut) remain true and load-bearing.

## BUG-C (2026-06-07 career playtest) - `R2-B2` tree instability + NaN debris -> stock exceptions

Source: `logs/2026-06-07_1638_career-playtest/` (KSP.log, `BUGS.md` BUG-C section). Build `Parsek V0.10.0` @ `07dea8fac`. The player used NO Parsek features this session (no rewind / re-fly / loop / playback); Parsek was only background-recording. Three log signatures, separable root causes. BUG-C is largely fallout of BUG-A (ledger recalc) and BUG-B (passive ghost / vessel auto-spawn), which are tracked separately.

### 1. NaN debris -> stock `FlightIntegrator.UpdateOcclusionSolar` throw - STOCK KSP, not Parsek data (no fix)

Two `ArgumentOutOfRangeException` throws in stock `FlightIntegrator.UpdateOcclusionSolar` (KSP.log lines 205423 @15:46:42, 218683 @15:50:46), each immediately around `R2-B2M-S6 Debris had a NaN Orbit and was removed` + an on-rails Kerbin->Sun SOI transition.

Origin is **pure stock physics**, confirmed:
- `R2-B2M-S6 Debris` (pids 1333358833 / 2800168062) is the player's **real staging debris**, created at 15:42:22 by a real decouple (`Decouple created vessel during recording ... rootPart=radialDecoupler`), with real drag cubes, terrain collision (`crashed through terrain on Kerbin`), and explosions. It is NOT a Parsek ghost/spawn: line 204856 `CleanupOrphanedSpawnedVessels: no match for 'R2-B2M-S6 Debris'` is Parsek explicitly disclaiming ownership.
- Parsek background-recorded the debris and then **finalized + deleted** those recordings as non-persistable (`canPersist=False`, `DeleteRecordingFiles`) at 15:42:39, ~4 minutes before the NaN. No Parsek recording carried or authored the NaN orbit.
- The throw is the well-known stock pattern: debris clips through terrain, is packed on rails with a degenerate velocity, the resulting hyperbolic orbit escapes Kerbin->Sun, and `UpdateOcclusionSolar` indexes a body list off a NaN-derived value and throws before stock's own NaN-orbit removal runs.

Decision: do **not** Harmony-patch stock `FlightIntegrator` to swallow this. It is not Parsek data, it reproduces without Parsek, and guarding a stock NaN path from a mod is high-risk for little gain (the proper home for a stock-bug shim is KSPCommunityFixes). Filed as known-stock, no Parsek code change.

### 2. Terminal-orbit ghost "permanently abandoned" 3x - BUG-B fallout + a real durability gap (FIXED here)

`[Policy] Spawn-death detected for terminal orbit and will not be retried: #32 "R2-B2-S5" ... reason=spawned-terminal-orbit-vessel-died` fires 3x (15:46:17, 15:56:43, 15:58:58), each with a fresh Parsek-spawn pid (3390689712 / 3495642311 / 3732877540) and `deathCount=1`.

Traced each cycle: `SpawnAtPosition: vessel spawned (ORBITING, body=Sun, alt~13.4 Gm)` -> Parsek's own `CleanupOrphanedSpawnedVessels: recovering 'R2-B2-S5' (matched by name)` immediately recovers it -> `RunSpawnDeathChecks` sees it gone -> `MarkCannotSpawnSafely`. The spawn itself is **BUG-B**: Parsek auto-materializes a committed terminal-orbit `vessel`-type recording during passive play, then orphan-recovers it.

The durability gap (the part fixed here): `RunSpawnDeathChecks` sets `Recording.TerminalSpawnCannotSpawnSafely = true` ("will not be retried"), and `VesselSpawner.SpawnOrRecoverIfTooClose` (`VesselSpawner.cs:1688`) honours that flag as a pre-spawn guard. But the flag was **transient** (`Recording.cs`, "do not serialize"), so every scene reload reset it to false and the vessel re-spawned. The first spawn each session happens before the flag is set, and `TryPassTerminalOrbitSpawnSafety`'s live orbit-geometry re-check passes (a 13.4 Gm heliocentric coast is geometrically "safe"), so only the recorded spawn-death can stop it - and that was being forgotten.

Fix: persist `TerminalSpawnCannotSpawnSafely` + `TerminalSpawnSafetyReasonCode` so the abandon survives a reload, on BOTH load paths. (1) Cold start (fresh game load, `RecordingStore.ClearCommittedInternal` then `LoadRecordingTrees`): the `RecordingTreeRecordCodec` save/load (`SaveMutablePlaybackState` / `LoadRecordingResourceAndState`) round-trips the keys when the committed trees are rebuilt from disk. (2) In-session load (scene change / quickload / revert, the `returned-scene-change` branch of `ParsekScenario.OnLoad`): that path reconciles the in-memory committed recordings instead of rebuilding them - it resets every recording's terminal spawn-safety via `TerminalOrbitSpawnSafety.Clear` (~line 2320) then restores only the saved subset, so the new `ParsekScenario.RestorePersistedTerminalAbandon` re-applies the flag from the saved RECORDING node (absent on a revert quicksave, so the abandon correctly does not carry across a revert). Either way the flag is true on the next scene, so the existing `VesselSpawner.cs:1688` pre-spawn guard blocks the re-spawn and the "will not be retried" log becomes truthful across reloads. The observed 3x repro went through path (2), so the codec change alone would not have fixed it. The soft altitude-deferred hold (`TerminalSpawnSafetyDeferred`) is deliberately left transient so it re-evaluates against the propagated orbit. Tests: `RecordingTreeTests.RecordingTree_TerminalSpawnCannotSpawnSafely_RoundTrips` / `RecordingTree_NoTerminalSpawnAbandon_StaysFalseOnLoad` (codec path) + `SpawnStateReconciliationTests.RestorePersistedTerminalAbandon_*` (in-session path) + `TerminalOrbitSpawnSafetyGeometryTests` (pins the geometry-vs-durability split itself: the 13.4 Gm heliocentric coast is admitted by the geometry re-check and stopped only by the persisted abandon, while the non-finite altitude / periapsis / apoapsis family is rejected outright). This is defense-in-depth; the upstream cure (don't auto-spawn during passive play at all) is BUG-B. Note: each orphan recovery runs a stock vessel-recovery (`Recovery processing captured ... recoveryFactor=...`), a candidate contributor to BUG-A's funds drift - flagged for the BUG-A session.

### 3. Active-tree save skipped - correct-by-design merge-consent guard, root is BUG-B-adjacent identity collision (no fix here)

`[Scenario] SaveActiveTreeIfAny: skipped active tree 'R2-B2-S5' because at least one recording could not be written with current v0 sidecars` + `skipped dirty sidecar save for committed-restore overlap recording 'bb53...'` (lines 165907-165925, 15:04).

This is the merge-consent guard in `SaveActiveTreeIfAny` (`ParsekScenario.cs:1380-1390`): a dirty recording that is an `IsCommittedTreeRestoreAttemptRecordingId` (and not a marker-owned switch segment) is skipped to avoid overwriting committed history before merge consent. **No committed sidecar is corrupted**, and in this log only one recording was dirty (the overlap clone `bb53...`, 1 buffered point), so there is no meaningful new-data loss - the guard behaved correctly.

The real defect is upstream and BUG-B-adjacent: the active tree was created by `TryRestoreCommittedTreeForSpawnedActiveVessel` treating the player's **fresh-rollout real vessel** `R2-B2-S5` (pid 590316933) as a committed-spawned-clone. This is the documented craft-baked-`persistentId` collision (a new launch of the same craft reuses the baked pid that prior committed recordings of that craft also carry). The fresh-rollout fast-path ("matches captured scene-entry pid") correctly skipped restore at 14:55, but after the 15:04 scene reload the captured scene-entry pid no longer matched and it fell through to committed-tree restore - routing a normal flight into the re-fly merge-consent path. The correct cure is launch-identity (`RecordedVesselGuid` / `VesselLaunchIdentity`) discrimination at the restore site, which belongs with BUG-B / the identity subsystem, not the save guard. No safe save-path change here.

~~Latent secondary (noted, not fixed): `SaveActiveTreeIfAny` early-returns and skips the WHOLE active-tree node when any one recording is a committed-restore overlap, even if a legitimately-new marker-owned switch-segment recording in the same tree had its sidecar written - that would orphan the sidecar (no tree node references it). Not triggered destructively in this log; flagged for the switch-segment owner.~~ **FIXED** (both-or-neither sidecar invariant).

**Fix:** `SaveActiveTreeIfAny` no longer writes sidecars while it is still deciding whether the tree node can be written. Both skip predicates (the committed-restore merge-consent guard, and the hydration-failed empty-sidecar-overwrite guard) are pure functions of recording state, so the whole active tree is now CLASSIFIED first by the new `ParsekScenario.PlanActiveTreeSidecarSaves` (`internal static`, returns an `ActiveTreeSidecarSavePlan` carrying the write candidates + the existing #280 counters + `AllRecordingsWritable`). When the plan reports a skip, `SaveActiveTreeIfAny` logs the grep-stable `outcome=both-or-neither deferredSidecarWrites=<n>` Warn and returns having written NOTHING; otherwise it runs the deferred write pass and then serializes the node. Either the node and its sidecars are both persisted, or neither is. The predicates, their per-recording Warn / Verbose lines, the marker-owned bypass and the counters are unchanged - only the ordering moved, so the merge-consent contract and the Phase-D switch-segment narrowing both still hold. Residual (documented in-code, irreducible): a genuine I/O failure inside the write pass is only knowable by attempting the write, so it can still leave earlier sidecars written with no tree node - but a recording whose sidecar write failed is already not-current on disk either way. Second surface, correct by design and noted for completeness: under a skip the OTHER recordings' fresher sidecars are deliberately not flushed either (without the tree node they would be unreferenced), so they stay `FilesDirty` and are written by the next save that clears the skip - the guard defers those writes, it does not drop the data. Because the whole `Scenario.OnSave` path is not drivable from xUnit, the classify pass is unit-tested directly (`SaveActiveTreeSidecarBothOrNeitherTests`, including the exact todo shape: one committed-restore overlap + one marker-owned switch-segment recording, asserting the segment is a write candidate while the plan is not writable, order-independently) and the classify-before-write ordering is pinned by a source gate in the same file.

---

## Post-cutover map/TS render backlog (next version)

The map/TS render cutover is COMPLETE (see the DONE entry above): the modular Director pipeline is the single render path; this file has no open map/TS render bugs (the render entries above are all RESOLVED/CLOSED). This is the consolidated list of what remains in this area for a future version. NOTHING here blocks the current release.

**1. Re-aim destination phase-lock for looped INTERPLANETARY missions (the one substantial piece).** Re-aim aligns the launch body but not the destination body's rotation/phase across the loop shift, so a looped interplanetary arrival drifts. "Duna One" is closed; the generalization (non-synchronous moons, destination loiter, 2+ moons, atmo-direct entry) is the deferred Phase 4 - see the "DUNA ONE CLOSED ... Phase-4 GENERALIZATION still deferred" entry immediately below. WARNING: this sits on the re-aim seam, a known high-cost area - build the failing multi-moon test and measure before any knob math, and treat "the faithful render is good enough" as a valid outcome (do not stack speculative fixes on a working baseline).

**2. Robustness / needs-in-game confirms (deferred during the cutover, non-blocking).**
- Tracer second cut: the decision-side inc/LAN/argPe-vs-transform reconciliation layer (the reconciler core exists; see the "In progress ... tracer SECOND CUT" entry at the top of this file).
- Sec 15.1 proto re-seed latency and Sec 15.2 per-scene patched-conic divergence (deferred, need in-game characterization; details in `docs/dev/plans/maprender-rewrite-status.md`).
- Phase 7b make-before-break swap-settle: the proto-vessel swap timing on scene/treatment swaps (a brief reseed window, ~0.5s).
- Tracking-Station render-delay confirm: the 1-2s TS proto-vessel gap fix (the same-body intra-block carry) is believed working; wants a TS playtest to confirm.

**3. Minor polish (low value, all currently suppressed or sub-visual).**
- icon-off-orbit residual ~1-3 deg on looped re-aim (the core ~96.5 deg bug is fixed; this is the leftover).
- no-fresh-seed create-frame transient: 1 frame, the proto is suppressed that frame so it is invisible; the fix touches the re-aim seam, so it was skipped during the closeout.
- polyline-orbit-overlap grace transient: the OrbitLineGrace debounce, cosmetic.
- cosmetic test-name cleanup: a couple of in-game test methods still read "...LiveGate..." after the `mapRenderDirectorDrive` gate was dropped.

**4. Architectural / cleanliness (nice-to-have, not needed for function).**
- Modularize the no-conic fallback as a proper Director "fallback treatment" instead of the kept patch-level path in `GhostOrbitLinePatch` (the icon floor + `ghostsWithSuppressedIcon` + `IsIconSuppressed`). The current kept fallback is correct and working; this is purity, not function.
- Standalone ghost-mod readiness for the render side (the `IPlaybackTrajectory` boundary).

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

---

## Phase 6 known gaps (deferred to later phases)

- ~~§7.7 BubbleEntry / BubbleExit candidates are not emitted by the Phase 6 builder.~~ Shipped: `AnchorCandidateBuilder.EmitBubbleEntryExitCandidates` walks adjacent `TrackSection` pairs and emits at every `Active|Background ↔ Checkpoint` source-class transition; `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` reads the LAST/FIRST physics-active sample as the high-fidelity world reference. Mainline shipped this at `AlgorithmStampVersion=5`; on the Phase 5 stack it lands inside the v8 alg-stamp window. Residual gap: RELATIVE-frame physics-active sections adjacent to a Checkpoint segment are deferred with a `bubble-entry-exit-relative-section-deferred` Verbose (uncommon in practice — vessel docked to its anchor while a Checkpoint splices in).
- ~~§7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates.~~ Obsolete: the co-bubble subsystem was retired in PR #912 (v0.9.3). The enum slot 7 (formerly `CoBubblePeer`) is now `Reserved7`, kept only to preserve the persisted `.pann` `AnchorCandidatesList` byte layout; there is no co-bubble pipeline. Close-formation accuracy is delivered by the parent-anchored debris contract instead.
- The 2.5 km bubble-radius HR-9 Warn (`RenderSessionState.cs:836-848`) only fires from the LiveSeparation path inside `RebuildFromMarker`. Anchors written via `AnchorPropagator.TryWriteAnchor → PutAnchorWithPriority` (§7.4 / §7.5 / §7.6 / §7.7 / §7.10) skip the magnitude check, so a non-LiveSeparation ε of, say, 12 km lands silently. Lift the magnitude check into `PutAnchorWithPriority` (or the per-source dispatch) in a follow-up PR so all anchor types are uniformly guarded — pre-existing gap, not introduced by §7.7.
- §7.9 SurfaceContinuous emits a marker only with ε = 0; the per-frame terrain raycast that resolves ε is Phase 7 work. Phase 6 demoted the rank from 2 to 6 to prevent the zero stub from winning ties against real OrbitalCheckpoint ε; Phase 7 must promote back to rank 2 once the resolver ships and bump `AlgorithmStampVersion` so existing `.pann` re-resolve.
- The split anchor sources (Undock / EVA / JointBreak) currently share the `DockOrMerge` enum byte (priority rank 4 either way). Logs label them by `BranchPointType` rather than by enum value to preserve telemetry granularity. If a future phase needs to differentiate split priorities from dock priorities, expand the `AnchorSource` enum and bump `AlgorithmStampVersion`.

---

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

## 430. "Why is this blocked?" explainer for the committed-action dialog (hover half SHIPPED)

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Partial mitigation:** PR #721 adds stock R&D / Astronaut Complex / Mission Control row badges with tooltips for committed-future actions, including the event UT and source recording when available. This helps before the click, but does not replace the structured blocked-action dialog below: the dialog still needs conflict context, Timeline navigation, and the rewind shortcut.

**~~Hover explainer for greyed-out Parsek buttons~~ - SHIPPED (2026-08-29, branch
`disabled-button-hover`).** The lightweight half of this entry: hovering a DISABLED
button now puts a few-words reason in its window's `TooltipEchoBox` help strip. Shared
mechanism in `Source/Parsek/UI/DisabledHoverEcho.cs`; ~35 sites across ParsekUI,
Recordings, Missions, Timeline, Logistics, Settings and Real Spawn Control. Most already
computed the right reason (`CanFastForward` / `CanRewind` `out reason`,
`GetWatchButtonTooltip`, the Re-Fly slot reason) and merely had no way to deliver it
while greyed; the sites that had no reason got pure `internal static` derivations, all
unit-tested with a per-window strip budget gate in `DisabledHoverEchoTests.cs` and a live
IMGUI cell (`DisabledHoverEchoImguiTest.cs`, category `Settings`).

Two things that fell out of it and are worth knowing:

- **The blocked-committed-action class is NOT reachable by this mechanism, by
  construction.** Every block `CommittedActionDialog.ShowBlocked` serves fires on a STOCK
  KSP screen (`RDController` tech node, `MissionControl` Accept, `AstronautComplex`
  Recruit, `SpaceCenterBuilding` Upgrade). `GUI.tooltip` and the echo strip only exist
  inside Parsek's own IMGUI windows, so there is no strip on those screens to echo into.
  That surface is served by PR #721's stock row badges, and by the structured dialog
  below. Do not re-scope the residual as "add hover text to the blocked actions".
- **A reason predicate that disagrees with its enable predicate is the failure mode to
  watch for.** Review caught one before merge: the mission "Warp to..." carrier re-derived
  the clock leg as `NextRelaunchUT > now`, but `ShouldEnableWarpToWindow` requires
  `now + 1.0` and finiteness - so through the last second before every scheduled relaunch
  (recurring forever on a looping mission), and for a NaN/Inf relaunch UT, the button
  greyed with an EMPTY reason and the strip stayed silent. The call site now feeds the
  enable gate itself rather than re-deriving it, and
  `MissionWarpReasonIsNonEmptyExactlyWhenTheButtonIsGreyed` holds the two together over
  the boundary and the non-finite values (verified non-vacuous: the pre-fix derivation
  reds 3 of its 11 cases). Any NEW carrier site should reuse its enable predicate, never
  restate it.
- **Two buttons were actively misleading, not merely silent** - fixed in the same
  change. The Timeline's "Warp to time" and a mission's "Warp to..." are each greyed by
  several gates while keeping the ENABLED wording in their `GUIContent`, so a hover
  reported a state the player was not in. Both now resolve which gate closed
  (`TimelineWindowUI.WarpToTimeDisabledReason`,
  `MissionsWindowUI.MissionWarpToDisabledReason`).

**Owed:** an in-game eyeball on the next play session. The mechanism is proven from
decompiled Unity 2019.4 source (`GUI.DoLabel` publishes the tooltip gated only on
rect-contains-mouse plus the visible clip, and never reads `GUI.enabled`; `GUI.Label`'s
explicit-rect overload takes no control ID, so the carrier cannot perturb layout), and the
live cell exists to measure it on the machine the mod runs on - but that cell has never
been executed (it needs a KSP session), and no human has watched the strip fill in yet.
So: reasoning and unit coverage are complete; runtime confirmation is entirely owed.

**Still open (the residual) - the structured blocked-action dialog:**

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

**Status:** PARTIALLY SHIPPED. The hover explainer for greyed-out Parsek buttons landed
2026-08-29 (see above). The residual is the structured dialog: which committed action
blocks this, at what UT, plus `Go to Timeline` and the rewind shortcut - all of it on the
STOCK screens where the block actually fires, which is why it needs a dialog rather than
hover text. Size of the residual: S-M.

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- ~~GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)~~
- ~~GhostVisual per-part cloning details (~370 lines)~~
- Flight "applied heat level Cold" (46 lines, logs no-change steady state) - **BLOCKED BY PIN**
- ~~RecordingStore SerializeTrackSections per-recording verbose (184 lines)~~
- ~~KSCSpawn "Spawn not needed" at INFO level (54 lines)~~
- ~~BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)~~ - was already fixed

**2026-08-29 update (release-hygiene, R5 item 3): four of the six named sources
cut, one blocked by a harness pin, one already fixed years-stale.** Taken one at
a time, because the reason differs per source and only the first two were the
same defect:

1. **GhostVisual HIERARCHY dump - FIXED.** `DumpTransformHierarchy` emitted one
   rate-limited line PER TRANSFORM inside an unbounded recursive walk, which is
   exactly what the batch-counting convention forbids. It is now
   `AppendTransformHierarchy`, building the tree into a `StringBuilder` that
   `LogEnginePartHierarchyDump` emits as ONE line
   (`... N transforms [depth:name[components](INACTIVE)] 0:part | 1:model | ...`).
   Nothing is lost: same nodes, same components, same INACTIVE marking, one line
   per engine part per 60 s window instead of one per node.
2. **GhostVisual per-part cloning details - FIXED, one line's worth.** Read
   against the code, the per-part lines are ALREADY convention-shaped: `part_summary_`,
   `clone_summary_`, the variant lines and the skip counters are each one
   rate-limited line per part carrying loop-accumulated counters. The one
   genuine duplicate was the second `[DIAG] part '<name>' modelRoot ...` line,
   whose localRot / localPos / localScale the `part_summary_` line was already
   half-carrying (modelRoot name + modelScale); it is folded into that line, so a
   part costs one line here instead of two. NOT touched: the counter summaries -
   collapsing those would delete measurements, not spam.
3. **Flight "applied heat level" - BLOCKED BY PIN, deliberately unchanged.** It is
   a REQUIRED `logContracts` token in the committed
   `harness/scenarios/S1.9-part-showcase-render.toml`
   (`"Part pid=[0-9]+: applied heat level (?:Hot|Medium|Cold)"`), and that spec's
   own header explains why it is load-bearing: the part-event applier is almost
   entirely silent, so this line is one of only two per-family apply proofs the
   product emits at all. The obvious fix (change-key it so a no-change steady
   state stops repeating) also risks the pin in a second way - `VerboseOnChange`'s
   identity dict is not cleared on scene switch, so the first post-re-entry
   application can be dropped, which is precisely the emit S1.9 waits for. Cutting
   it therefore needs the S1.9 lane re-read first; it is not a two-line change and
   was not attempted here.
4. **RecordingStore SerializeTrackSections - FIXED.** The per-section
   `writing source=... (non-default)` Verbose is collected into a list and emitted
   as one summary after the loop (`wrote N sections, M with a non-default source:
   [1] source=Background, ...`), so a save costs one line instead of M. The
   existing `SerializeTrajectoryInto_BackgroundFixture_LogsSingleSectionSummary`
   cell passes unchanged (it asserts a single line carrying `source=Background`,
   which is now literally what the method's name says), plus two new cells in
   `TrackSectionSerializationTests` for the many-source collapse and the
   all-default silence.
5. **KSCSpawn "Spawn not needed" - FIXED.** Info -> Verbose. It is the ORDINARY
   outcome for every past-end recording the KSC scene walks, i.e. diagnostic
   detail rather than an event; the spawn-needed paths beside it stay Info. Not
   pinned anywhere in `harness/` or `scripts/`; the KSCSpawn xUnit assertions
   target a different helper (`LogPlaybackDisabledPastEndSpawnAttemptOnce`).
6. **BgRecorder CheckpointAllVessels checkpointed=0 - ALREADY FIXED, entry was
   stale.** Bug #592 (see the 2026-04-25 update below) already moved it off Info
   onto `VerboseRateLimited` keyed by the SHAPE of the result
   (`checkpoint-all-{checkpointed}-{skippedNotOrbital}-{skippedNoVessel}-{skippedDuplicateBoundary}`),
   so identical no-op summaries during a warp burst collapse while a change in
   counts still surfaces. Nothing to do; the bullet above is struck for accuracy,
   not for work done in this pass.

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

**Status:** Open on ONE named source only - the heat-level line (item 3 in the
2026-08-29 update), which is blocked behind an S1.9 harness pin and needs that
lane re-read before it can move. The other five named sources are closed. The
broader audit work stays with the Observability Audit section above.

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

### T48. FX-fingerprint A/B: the fixture half is delivered, the A/B itself has never been re-run (filed 2026-08-04 as "coverage is starved by the trajectory-only synthetic corpus", branch `modded-compat-lane`) [**FIXTURE HALF DELIVERED 2026-08-28** by PR #1563 (`part-showcase-lane`): the `part-showcase` preset injects 243 ghost-snapshot-bearing single-part rows - 47 stock engine families + 5 stock RCS, plus 36 install-conditional ReStock+ rows (17 engine / 12 RCS) auto-appended by `IsRestockPlusInstalled` - each built through `VesselSnapshotBuilder.AddPart` + `WithGhostVisualSnapshot`, which is the input `GhostVisualBuilder` feeds to `GhostFxFingerprint.LogEngineInfos` / `LogRcsInfos` right after the engine/RCS FX build. S1.9 ARMED the census at `spawned = { min = 243 }` / `requireBalanced = true`, measured 243/243/0 twice, with `MeshSpawned` gated on an engine (LV-T30) and an RCS (RV-105) row - so the FX build that T48 said could "never happen" demonstrably happens now. **RESIDUAL, AND IT IS THE WHOLE A/B**: no committed spec injects the preset on a modded install - S1.9 is pinned `instanceProfile = "stock-minimal"` and both `MC-1-waterfall-compat` / `MC-2-restock-compat` declare `injectedRecordings = "none"` - so `hlib.diff_fx_fingerprints` has NOT run over a stock-vs-modded pair since 2026-08-04. Also unmeasured: whether the ~30 s one-shot showcase window (the loop flag is stripped at load) leaves enough dwell for every family to emit its line on the modded side]

The first stock-minimal vs modded-compat `[FxFingerprint]` A/B (R14, report-only)
worked mechanically but measured almost nothing: of the 342 recordings the
all-synthetic corpus injects on a modded install, ~330 carry NO vessel snapshot
(`Spawn suppressed: ... no vessel snapshot=330`), so no ghost visual - and no
engine/RCS FX build - can ever happen for them. The engine/RCS "Part Showcase"
rows loaded but did not spawn in the run window, leaving exactly ONE
fingerprinted key per side (`solidBooster.sm.v2` from the Reentry East ghost's
snapshot). That one key already shows the expected divergence (ReStock FX models
`ReStock/FX/restock-fx-srb-{core,smoke}-1` on modded vs the stock prefabs), so
the pipeline is sound - the corpus is the bottleneck. Fix: ~~a dedicated
engine-showcase fixture save whose recordings carry REAL vessel snapshots
(one per engine/RCS part family, positioned inside spawn range of the pad
host)~~ **[DONE 2026-08-28 - the `part-showcase` preset, PR #1563; see the header]**,
then re-run `hlib.diff_fx_fingerprints` over the pair **[STILL OWED - no committed
spec injects the preset on a modded install]**; only after that
is a gate worth discussing. Extraction/diff tooling landed with R14
(`hlib.parse_fx_fingerprint_lines` / `diff_fx_fingerprints` /
`format_fx_fingerprint_diff` + `harness/tools/fx_fingerprint_diff.py`).

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
