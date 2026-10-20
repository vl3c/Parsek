# Re-Fly ledger safety: implementation plan

Status: IMPLEMENTED 2026-10-20 on branch `claude/project-thread-vy0m14` (PR #2151): phases 1-4, a
clean review's five fixes, and the lanes (RF-L1..RF-L4 new, nine reshaped). Lanes not flown yet.
Design authority: `docs/parsek-rewind-to-separation-design.md` section 2.12 (wins over this plan
where they disagree; fix the plan, not the code, when they do). Evidence:
`docs/dev/research/refly-ledger-paradox-2026-10-19.md`. Todos:
`REFLY-MERGE-RETIRES-VALUE-LATER-SPENDING-USED`, `REFLY-RESUMED-MERGED-DISCARD-RESTORES-LOSSES`,
`REWIND-LEAVES-VESSELS-WITH-A-COMMITTED-FUTURE-LIVE` (Re-Fly invoke part only).

Owner rulings this plan implements (V, 2026-10-19):

- Option 1: only a flight with nothing to give back can be re-flown (Rule 1).
- During a re-fly only the re-flown vessel and its own EVA crew can be flown; awards are filed
  under the vessel that caused them (Rule 3).
- Discard is never refused; an attempt is a temporary unmerged flight until it merges. An
  attempt a quickload resumed after its merge is unmerged again.
- A quickload or load restores the game exactly as it was at the save; in particular it undoes
  Space Center purchases, upgrades and hires made after the save (reverses QL-R2 for quickloads;
  rewinds keep D2).

Every `file:line` below was read on branch `claude/project-thread-vy0m14` at 2f9256d (main
0f2b6ce). Line numbers drift: each step names the symbol, and every VERIFY must be confirmed
against the code before the step is written, not taken from this file.

## Ground rules

- Red first: every behaviour change starts with a failing xUnit test (or a failing in-game test
  where only live KSP can show it), committed in the same commit as the fix.
- Docs per commit (CLAUDE.md): CHANGELOG entry under the current version (3-5 lines, player
  words), todo entries, design-doc paragraphs that 2.12 marks as superseded get rewritten in the
  commit that makes them true, and `docs/dev/autotest-status.md` for lane changes.
- Logging: every new gate decision logs one line with the reason (formats below); loops use the
  batch-counting convention.
- No new UI surface. Text goes only into existing surfaces: the Unfinished Flights hover, the
  Re-Fly confirmation dialog body, the merge dialog body.
- Order matters: Phase 1 alone already stops new deficits for every new merge, so it lands
  first and can ship on its own.

## Phase 1: the retire set and the value gate (Rule 1) plus the migration sweep

1. **One retire-set builder.** Extract steps 2-5 of `SupersedeCommit.CommitTombstones`
   (SupersedeCommit.cs:2484; scope filter `TombstoneAttributionHelper.InSupersedeScope` :2562,
   cutoff `ComputeTombstoneRewindCutoffUT` :2543, pre-rewind keep `IsPreRewindAttributedAction`
   :2583/:2601, already-tombstoned skip :2683, `TombstoneEligibility.IsSupersedeTombstoneEligible`
   :2690) into a pure `internal static` builder that takes the closure, the cutoff, the actions
   and the existing tombstones and returns the rows. `CommitTombstones` calls it and writes the
   tombstones exactly as today (byte-identical output: pin with the existing
   `SupersedeCommitTombstoneTests` before and after).
   - VERIFY: the closure the gate needs is rooted at the slot's CURRENT tip
     (`EffectiveState.EffectiveTipRecordingId`, design 1.6 walk), while `CommitTombstones` gets it
     from the marker (`ComputeSubtreeClosureInternal(marker, SupersedeTargetId ??
     OriginChildRecordingId)`, :207-209). Confirm both give the same set for a slot with a live
     marker; if not, the builder takes the closure as an input and each caller computes it.
   - VERIFY: the cutoff without a marker. `ComputeTombstoneRewindCutoffUT(marker)` needs the
     rewind point; the gate has the slot's RP. Add an RP-based overload, same value.
2. **Loss vs sealing classification.** `internal static bool IsLossRow(GameAction)` with the
   complete list from design 2.12 Terms (KerbalAssignment Dead/Aboard/Unknown, ReputationPenalty
   KerbalDeath/Other, FacilityDestruction). Everything else in the retire set is sealing,
   including types added later. Test: an exhaustive theory over every `GameActionType` x relevant
   sub-enum, so a new enum member fails the test until it is classified on purpose.
3. **`SupersedeCommit.RetireSetHoldsSealingRow(slot, out string summary)`** returns the first
   sealing row as `Type:ActionId` (same summary format as today's :1873). Replace the
   ScienceEarning-only `IsRetryBlockingRecordingAction` (:2199) behind
   `TryFindRetryBlockingWorldAction` (:1814) so every existing caller moves at once:
   - Site A: `UnfinishedFlightClassifier.TryRejectRecordingScopedWorldAction` :519 (arms
     crashed :347, focus override :388, stashedStableLeaf :405, stableLeafUnconcluded :474).
   - Stash resolver: `TryResolveStashableRewindPointForRecording` :636.
   - Site B-1: `ShouldKeepReFlySlotOpenAfterMerge` :1288 (call at :1311).
   - `ReFlyAutoSealPreviewer.Preview` (ReFlyAutoSealPreview.cs:137): drop the hand-copied
     ScienceEarning filter (`CollectScienceReasons` :223-251) and read the predicate.
   - `RewindInvoker.EvaluateCanInvoke` (RewindInvoker.cs:71): new precondition after the
     active-marker check (:125), reason `recordingAction:<Type>:<ActionId>`.
   - Keep the `recordingAction:` prefix (`RecordingActionReasonPrefix` :14) so the auto-seal reason
     catalog (`TryBuildRecordingActionCloseReason` :1759) keeps working.
   - VERIFY: the deployed-science exemption (`DeployedScienceLedgerTests`
     `IsRetryBlockingRecordingAction_DeployedScienceNeverSealsAReFlySlot` :334). Under Rule 1 a
     deployed-science credit IS a gain of the flight. Read why it was exempted (todo history)
     before deciding; if the reason was that the credit belongs to the deployed station rather
     than the flight, it is not in the retire set at all (the builder decides, not an exemption).
   - VERIFY: the per-scenario cache (:1880-1935) keys. A ledger change after the row was drawn
     must invalidate it (the CanInvoke re-check exists for exactly this).
   - VERIFY: Preview runs while the merge dialog is open, when the attempt's rows are not in
     `Ledger.Actions` yet. Phase 4's row-build step feeds it; until Phase 4 lands Preview reads
     what it reads today.
4. **Migration sweep.** New `LoadTimeSweep` step after Step 8.5 /
   `SweepMissingRewindPointQuicksaves` (LoadTimeSweep.cs:256-257), before the summary: for every
   open slot tip (stashed included) whose retire set holds a sealing row, flip `Immutable` the way
   `ConcludeProvisionalSlotTips` (:554) does, so the RP reaper can take its point. Log
   `[LoadSweep] Value-sealed slots (section 2.12): found=<n> sealed=<m>`. No tombstone or deficit
   repair.
5. **Player text.** Unfinished Flights group hover gains one sentence (only flights that earned
   nothing after their separation are listed); the Re-Fly confirmation dialog's career paragraph
   is rewritten to match. Check `TooltipEchoBudgetTests`.
6. **Tests.** Classifier arms (crashed flight that earned funds / a milestone / a contract
   outcome is rejected with the right reason; one that only lost crew qualifies; one that earned
   nothing qualifies), Stash refusal, CanInvoke refusal after a ledger change, B-1 (re-fly that
   gained seals, re-fly that only crashed stays open), Preview parity with the predicate, sweep
   (found/sealed counts, idempotent on a second load), builder parity with `CommitTombstones`.

## Phase 2: Discard and Retry restore the ledger, and a quickload restores the save

1. **Row removal helper.** `Ledger.RemoveActionsForRecordings(ICollection<string>, string
   callSite)` beside `ClearRecordingTagForRecordings` (Ledger.cs:659): removes rows tagged to the
   ids, never a seed, bumps the ledger version, one summary log line. The unused single-id
   `RemoveActionsForRecording` (:265, test-only caller) is folded into it or deleted.
2. **Every Re-Fly discard path removes instead of untagging:** `PruneActiveReFlyAttemptOwnedTopology`
   (MergeDialog.ReFlyDiscard.cs:196 and :220), `DiscardReFlyAttemptRecordingsAndRewindPoints`
   (:641), Retry (`RepairSessionBeforeRetry` :373, reached from `RevertInterceptor.RetryHandler`
   RevertInterceptor.cs:313; it prunes staged rows only today), and the load-time zombie discard
   (`LoadTimeSweep.RemoveDiscardRecordings` :1359, which never touches the ledger today). The
   resumed-merged attempt (OQ-1) gets no exception (owner ruling 2026-10-19).
   - VERIFY: the post-discard recalculation runs after the removal on every path (the Esc-menu
     Discard loads the RP quicksave; the merge-dialog Discard and the rewind end do not).
   - VERIFY: untagged session rows (design 7.42) stay; only rows tagged to the attempt's
     recording ids go. After Phase 3 there are no untagged session rows.
   - VERIFY: `SupersedeCommit.cs:3335` (`ClearRecordingTagForRecording` in
     `ConcludeRetiredProvisional`) is a conclude path, not a discard; leave it.
3. **A quickload undoes Space Center rows after the save.** Flip the
   `UntaggedLedgerRowsAfterCutoff` cell for QuickloadFlight / InSessionOther
   (LoadReconcilePolicy.cs:748-751, "a quickload keeps untagged KSC rows (QL-R2, designed)") from
   Keep to a retire of untagged rows after the save UT, implemented beside the existing
   abandoned-future retire (`ParsekScenario.Trim.cs` `RetireAbandonedFutureLedgerRows` :1244 ->
   `Ledger.RetireAbandonedFutureActions` :388). Seeds and route rows: VERIFY against the audit
   below before deciding. `LoadReconcilePolicyTests`, `LoadReconcileWiringGateTests` and
   `QuickloadAbandonedFutureLedgerTests` (:335 pins D2/QL-R2 today) change with it; game-actions
   design 15.5 AS BUILT paragraph is rewritten.
   - The wider owner principle (a quickload or load restores the save exactly) is audited
     separately (todo QUICKLOAD-RESTORES-THE-SAVE-EXACTLY); this phase does only what the Re-Fly
     discard needs plus the purchase ruling.
4. **Tests.** Each discard path leaves `Ledger.Actions` equal to the pre-invoke list (set
   equality on ActionIds and amounts); Retry inherits no row; zombie sweep removes rows; worked
   example from the todo (+30 science merged, 30-science node bought, F9 into the session,
   Discard: node purchase gone, science balance never below zero).
5. **Lanes.** `QL-1-ksc-actions-then-earlier-quicksave` flips its expectation (purchases after
   the save are gone). `LAB-10b`, `QL-5`, `QL-5c`, `QL-5d` re-read: their ledger windows should
   still hold; re-fly them.

## Phase 3: session isolation (Rule 3)

1. **Switch block.** Refuse a player switch while `ParsekScenario.ActiveReFlySessionMarker` is
   set to any vessel that is not the re-flown vessel's walked stretch or its own EVA kerbal.
   - Map Switch-To / focus Fly: `MapFocusObjectOnSelectPatch.Prefix` (Patches/
     MapFocusObjectOnSelectPatch.cs:119, returns bool) refuses before
     `DecidePreSwitchDialogAction` (:300 / :1168).
   - Tracking Station Fly: `SwitchIntentTrackingStationFlyPatch.Prefix` (GhostTrackingStationPatch.cs
     :675) is void; add the refusal to the bool-returning `GhostTrackingFlyPatch.Prefix` (:621) or
     a new bool Prefix on the same method.
   - KSC marker Fly: `KscVesselMarkerFlyPatch.Prefix` (KscVesselMarkerFlyPatch.cs:27) becomes
     bool-returning.
   - `[` / `]`: Parsek has no patch today (only observers, ParsekScenario.cs:8836,
     ParsekFlight.cs:3708). VERIFY by decompiling which stock method handles the vessel-cycle
     keys (`ilspycmd` on Assembly-CSharp; candidates `FlightGlobals.SetActiveVessel` callers in
     the flight input path) and patch THAT entry, not `FlightGlobals.SetActiveVessel` itself:
     stock calls `SetActiveVessel` for EVA, board, dock and undock, which must stay allowed.
   - One pure decision `ReFlySwitchGate.Decide(markerLive, targetPid, allowedPids, path)` with a
     truth-table test; log `[ReFlySession] Info: Switch refused sess=<s> path=<p> target=<pid>
     reason=<r>`.
   - VERIFY: the allowed set (the walked stretch of design 1.6 plus its own EVA kerbals) as the
     session progresses: a re-fly that itself separates gets a new vessel the player may NOT
     switch to (design says it keeps a session RP and can be re-flown after the merge).
2. **Awards by cause.** Today every award takes the live active recording's tag
   (`GameStateRecorder.ResolveCurrentRecordingTag` :196/:201 -> `ParsekFlight.GetActiveRecordingIdForTagging`
   :246) and `ReFlyAwardTagging.Resolve` (ReFlyAwardTagging.cs:67) substitutes the provisional.
   Resolve the tag from the vessel the event names at `Emit` (GameStateRecorder.cs:144) and the
   science sites (:799, :1184): the recording of that vessel in the active tree, else the live
   recording. No session row is untagged. VERIFY per event type which vessel the stock event
   carries (contract parameter, recovery, destruction, science subject).
3. **Deadline clock.** `ContractsModule.PrePass(actions, walkNowUT)` (ContractsModule.cs:240)
   uses `walkNowUT ?? lastActionUT` (:310). The invoke recalculation passes `double.MaxValue`
   (RewindInvoker.cs:1445). Split the deadline clock from the walk cutoff through
   `RecalculationEngine.Recalculate` (:144) -> `RunWalk` (:191) -> PrePass (:853-862) so the
   session judges deadlines at the live UT. Test with `ContractDeadlineAbsoluteUTTests`.
4. **Claimed-vessel hold at invoke.** Replace Step 3b (`RetireResurrectedVesselRecoveryRows`,
   RewindInvoker.cs:1535, called at :1378) with handing every vessel the committed future uses
   after the rewind UT (matched by `VesselLaunchIdentity`) to the claimed hold: today the hold is
   fed only by `ParsekFlight.EvaluateAndApplyGhostChains` (ParsekFlight.cs:13187 ->
   `FilterAndGhostChains` :13280 -> `VesselGhoster.GhostVessel` :13342) and drawn by
   `UpdateClaimedVesselHoldGhosts` (ParsekFlight.ClaimedVesselHoldGhost.cs:57) from
   `chain.HeldBackVessels()`. Add a second feed for these vessels, with the rewind-save shape
   captured as at RecordingStore.cs:6952-6960. VERIFY the hold's release (first claim UT) for a
   vessel with no chain.
5. **Tests.** Switch gate truth table; award tagging per event type with two vessels in the
   session; deadline clock; hold feed (a resurrected vessel with a committed future is held and
   its committed recovery row stays; one without stays live); Discard after a held vessel leaves
   no tombstone (Step 3b's leak is gone).

## Phase 4: the pure row build, the loss swap and the dominance check (Rules 2, 4, 5)

1. **Split `FileRecordingCommitRows`** (GameActions/LedgerOrchestrator.cs:345) into a pure build
   step (ConvertEvents :362, pending science :371, vessel cost :376, kerbal assignment :385,
   death penalty :419, over a copy of the recording for crew end states and an explicit seed-origin
   input) and the append step (the writes at :358, :382, :406, :442, :448). Tree commit
   (`NotifyLedgerTreeCommitted` :8780) output must be byte-identical: pin with the existing
   ledger tests before the split.
2. **Dominance check in `MergeDialog`**, run on dialog open (`ShowTreeDialog` MergeDialog.cs:137,
   buttons :272-299, pre-transition overload :351) and again on the Merge click before
   `CommitPendingTree` (MergeDialog.Commit.cs:82). Inputs: the build step's rows, the retire set
   (Phase 1), the ledger. On cloned modules: compute `L` and both variants of `L'`, pick the
   variant (Rule 2), check Invariant LS (balances at every UT, affordability of every spend, the
   world clause for kerbals respecting `KerbalsModule` respawn and for facilities). Refuse: the
   body text says why (existing `BuildReFlyDialogBody`, MergeDialog.DialogText.cs:76); Discard
   and Retry stay. Logs `[LedgerSwap] Info: Merge dominance check ...` and `Loss swap ...` per
   design.
   - VERIFY: cloned modules really touch no live state (no `KspStatePatcher`, no seed write).
3. **Variant B wiring** in `MergeJournalOrchestrator.RunMerge` (MergeJournalOrchestrator.cs:168,
   CommitTombstones :265): persist `MergeJournal.LossSwapVariant` (key `lossSwapVariant`, absent =
   A) before `Begin` so the finisher (:560) replays the same choice; Variant B tombstones the
   re-fly's own ReputationPenalty loss rows with `RetiringRecordingId` = the re-fly. VERIFY that
   adding the key is additive (no schema generation bump: it is a new optional key on the
   journal, not a recording shape change).
4. **Tests.** Rule 4 (re-fly fails a contract a later committed flight completes: refused;
   re-fly completes a milestone earlier: passes and the credit moves earlier), Rule 2 (re-fly
   kills more crew: Variant B, reputation never below `L`), world clause (re-fly kills a kerbal a
   later committed recording flies before respawn: refused), crash between Begin and Finalize
   replays the same variant.

## Lanes and validation

Validation is automated harness lanes flown on V's PC through Remote Control, one at a time
(shared KSP install), after `provision.py` deploys the branch DLL.

- Reshape `LAB-10-lab-refly-supersede` and `LAB-10b-lab-refly-discard`: their replaced flight
  transmits lab science, so under Rule 1 it is no longer re-flyable. LAB-10 becomes the negative
  lane (the slot is not offered, reason `recordingAction:ScienceLabEarning:...`); LAB-10b moves
  to a flight that earned nothing.
- VERIFY every other Re-Fly lane (`RF-*`, `CI-2`, `CL-4`, `QL-3`, `QL-5*`, `QL-7`, and any spec
  that invokes a rewind point): does its replaced flight earn anything after the split? A grep
  of each spec's mission plus its ledger expectations decides; a lane that relies on re-flying
  an earning flight is reshaped, never deleted.
- New lanes: a bank-then-retire attempt (earn, spend at the Space Center, the slot is not
  offered); a booster re-fly that lands (recovery is added, nothing taken back); a switch
  attempt during a session (refused, logged); a quickload past a Space Center purchase (gone).
- Each new lane follows "Authoring a fast lane" in `harness/README.md`.

## Not in this plan

- `LEDGER-RETIREMENT-PATHS-OUTSIDE-REFLY` O2-O7 (separate owner decisions).
- The wider "a load restores the save exactly" work (todo QUICKLOAD-RESTORES-THE-SAVE-EXACTLY;
  scope ruled 2026-10-19: drop everything after the save, other committed flights included),
  except the Space Center purchase cell above, which Phase 2 needs and which that work keeps.
- Plain Rewind-to-Launch's claimed hold (todo REWIND-LEAVES-VESSELS-WITH-A-COMMITTED-FUTURE-LIVE).

## Appendix: what an in-session load keeps today (audit 2026-10-19, at 2f9256d)

Input to todo QUICKLOAD-RESTORES-THE-SAVE-EXACTLY. QF = QuickloadFlight (F9 in flight), ISO =
InSessionOther (Space Center pause-menu load). A main-menu Load is Cold
(`OnMainMenuTransition` sets `initialLoadDone=false`, ParsekScenario.cs:8786) and reads the save,
except the external ledger / event / milestone files. The in-session branch
(ParsekScenario.cs:3838-4570) never calls `LoadRecordingTrees` (:4617) or `LoadExternalFiles`
(:3822). No policy cell below carries a KnownGapTodoId.

| Category | Today (QF / ISO) | Differs from the save? | Cited basis |
|---|---|---|---|
| Committed recordings / trees | memory (RecordingStore.cs:338-341); the save only resets spawn pid, `lastResIdx`, terminal-abandon (ParsekScenario.cs:4152, 4194-4247) | yes: a tree committed after the save stays; one deleted after does not return; one discarded after records again | D2, QL-R1, user guide :402 |
| Resumed flight | save node + sidecars; `DecideCommittedCopyRestore` (HydrationRepair.cs:20-75) and the resume trim (Trim.cs:818) | mostly no; leftovers in the QUICKLOAD-RESUME-* / QUICKLOAD-TRIM-* / QUICKLOAD-REFLY-SCOPE-* todos | OQ-2, D2 |
| Tagged events | memory; only the trimmed set's future purged (LoadReconcilePolicy.cs:644-652) | yes, other trees' events | OQ-2 |
| Tagged ledger rows | memory; trimmed set retired (Ledger.cs:388, Trim.cs:1203-1235, policy :677-686) | yes, other trees' rows | OQ-2 |
| Untagged Space Center rows | kept (policy :748-751); walk to the save UT, rows re-apply later (ParsekScenario.cs:4490-4496) | yes | QL-R2; reversed 2026-10-19 (Phase 2 step 3) |
| Milestones | memory, `RestoreMutableState(resetUnmatched:false)` (ParsekScenario.cs:4351, policy :922-926) | yes, replay index of a later milestone | none |
| Supersede rows, retirements, tombstones | memory (InSessionHandoff.cs:152, ParsekScenario.cs:3793); step B hands a resumed tree's rows to the save except the resumed attempt's (policy :458-484) | yes, a Re-Fly merged after the save stays | PR #2038, OQ-1, user guide :401 |
| Merge journal | memory when null or Complete (policy :486-509) | yes | PR #2038 |
| Re-Fly marker | save, validated by `LoadTimeSweep` (policy :552-555) | can be cleared | OQ-1 |
| Rewind points / files | owner partition (policy :511-534); files never deleted for a dropped point | yes | OQ-3 |
| Crew replacements | save (ParsekScenario.cs:5538-5548); reservations derive from memory's recordings | yes, crew of later trees reserved | D2 |
| Groups, archive, missions | memory (:5561; policy :868-905) | yes | none |
| Vessel names | memory, re-applied (policy :964-967) | yes | names design 7.6 ruling 5 |
| Logistics routes | reconciled at the cutoff (ParsekScenario.cs:4471); later routes dormant (RouteRewindClassifier.cs:34-42) | partly | ruling 2026-10-07, D2 |
| Pending science | QF cleared; ISO kept (policy :775-780) | ISO yes | none |
| Sidecar files | not re-read for committed recordings; orphans stay until a cold load | yes (disk only) | QUICKLOAD-KEEPS-POST-SAVE-REFLY-FORKS |
| Settings, switch session, intents, batch marker | save / settings.cfg | no (install-wide settings by design) | user guide :272 |

Stale comment to fix with that work: ParsekScenario.cs:3765 says the lists are rebuilt "from .sfs
rather than reusing stale in-memory state"; since PR #2038 they come from memory.
