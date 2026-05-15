# Segment-scoped auto-record for Map Switch and Tracking Station Fly

Status: draft plan for review, no implementation yet.

Worktree: `Parsek-plan-switch-fly-segments`

Branch: `plan-switch-fly-segments`

Base: `origin/main` after PR #866 (`ea942184`, merged 2026-05-15).

## Summary

When the player explicitly moves focus to another vessel through stock Map view "Switch To" or Tracking Station "Fly", Parsek should immediately start an auto-recording for that focused vessel. That recording must be a distinct segment with a new recording ID, not a same-recording resume of an existing committed tree member.

The merge dialog that appears when leaving the scene should be scoped to this new segment attempt. Choosing Discard must remove only the segment created by the switch/Fly action, including its descendant attempt recordings and game-state events, while preserving all previously committed recordings and sidecars.

This plan builds on PR #866. The #866 copy-on-write restore is still the right safety base: committed trees should not be detached and reused as mutable pending state. The missing piece is the UX/data model above the clone: a switch/Fly restore should attach a new child/continuation recording to the cloned tree and mark that child as an attempt-scoped segment.

## Related Current Behavior

PR #866 fixed the data-loss bug where switching into a previously spawned committed vessel detached the committed tree and reused it as the live active tree. Discard then treated the entire timeline as pending-only and removed all recordings. The current merged behavior clones the committed tree, arms a one-shot restore context, and protects same-ID event tails on Discard.

The earlier post-switch auto-record plan (`fix-546-post-switch-first-modification-autorecord.md`) added a watcher that arms after vessel switch and starts recording when the first meaningful modification is detected. That plan intentionally did not start on switch alone. This feature changes the product behavior for explicit Map Switch / Tracking Station Fly: the switch itself is the intent signal, so the auto recorder should start immediately.

The first-modification watcher can remain useful as a fallback for cases where immediate start is unsafe or where a focus change is not recognized as an explicit stock Switch/Fly action.

When an immediate switch/Fly segment starts successfully, it must consume the intent marker and clear or suppress the first-modification watcher for that same vessel switch. The watcher is a fallback path only; it must not remain armed in a way that can create a second segment for the same stock action.

## Goals

1. Map view "Switch To" and Tracking Station "Fly" immediately start an auto-recording segment for the focused vessel.
2. The segment uses a new recording ID distinct from any committed or background recording ID.
3. If the focused vessel belongs to a committed spawned-vessel tree, the live flight `activeTree` is a clone of that committed tree plus the new segment.
4. If the focused vessel belongs to the current active/background tree, the new segment is attached as a continuation child rather than promoting/resuming the old recording ID.
5. If the focused vessel is unrelated to Parsek history, Parsek starts a fresh standalone recording/tree, or a child segment of the tree the player just left when that parent context is available.
6. Scene-exit merge UI, accept, discard, save/reload, F5, and F9 all operate at segment-attempt scope.
7. Existing #866 committed-history protection stays in place and remains useful for clone restore and same-ID safety.

## Non-goals

1. Do not rewrite the general recording tree format unless the implementation proves a new branch type is necessary.
2. Do not change stock KSP quicksave/quickload semantics.
3. Do not remove the first-modification watcher until the new immediate-start paths have runtime coverage.
4. Do not broaden ReFly behavior. Stock Tracking Station "Fly" is distinct from Parsek ReFly marker arrivals.

## Existing Code Seams

`ParsekScenario.OnVesselSwitching` currently records that a focus switch is pending. `ParsekScenario.OnLoad` uses the pending switch marker plus UT-backwards checks to distinguish normal FLIGHT-to-FLIGHT vessel switches from quickload/revert/F5/F9 paths. This guard should remain strict.

`ParsekFlight.OnVesselSwitchComplete` handles the final FLIGHT-side handoff. It backgrounds the previous active recorder/tree, decides whether the new active vessel is tracked in the active tree, arms the post-switch watcher, and suppresses special ReFly marker arrivals.

`ParsekFlight.TryRestoreCommittedTreeForSpawnedActiveVessel` and `TryTakeCommittedTreeForSpawnedVesselRestore` are the committed spawned-vessel restore path touched by #866. They now clone the committed tree and arm a committed-tree restore attempt instead of detaching committed storage.

`FlightRecorder.DecideOnVesselSwitch` still models tracked background switches as promotion/resume of an existing recording. The new feature should avoid same-ID promotion for explicit Switch/Fly recording starts and use a new continuation segment instead.

`MergeDialog.MergeDiscard` already gives ReFly attempts a scoped discard hook before falling back to full pending-tree discard. The new feature should add an analogous switch-segment discard hook rather than relying on full-tree discard.

`RecordingStore.DiscardPendingTree`, `RecordingStore.ArmCommittedTreeRestoreAttempt`, and the #866 event-tail purge remain important. They should protect committed IDs, but new switch/Fly segments should mostly use new IDs and attempt markers rather than same-ID event-tail cleanup.

## Required Stock-Action Intent Marker

The generic vessel-switch seams are not enough to decide immediate auto-record. `GameEvents.onVesselSwitching` and `GameEvents.onVesselChange` fire for more than the two requested stock actions, including boarding, missed-switch recovery, ReFly-related focus changes, and other KSP focus transitions.

Implementation must add a positive intent marker that is set only by confirmed stock Map view "Switch To" or stock Tracking Station "Fly" UI actions. The Tracking Station source is already known: `Patches/GhostTrackingStationPatch.cs` patches `SpaceTracking.FlyVessel` in `GhostTrackingFlyPatch`. Extend that patch, or add a sibling postfix/prefix, so the non-ghost passthrough path arms a `TrackingStationFly` intent before stock transitions to FLIGHT.

Map view "Switch To" is the remaining method-source research item. Candidate sources should be narrow stock UI handlers in the MapView / OrbitTargeter / target flyout path. Do not use a blanket `FlightGlobals.SetActiveVessel` prefix as the intent source; that path is shared by boarding, dock/undock, ReFly arrivals, recovery flows, and other focus changes.

Until a stock-action source is confirmed, the generic vessel-switch callbacks should only consume an already-armed intent marker; they should not infer immediate-start intent by themselves.

Suggested intent marker fields:

- `IntentId`: stable GUID for the pending UI action.
- `Action`: `MapSwitchTo` or `TrackingStationFly`.
- `TargetVesselPersistentId`: expected target vessel PID, if available when the UI action fires.
- `SourceScene`: `FLIGHT` or `TRACKSTATION`.
- `CapturedRealtime`: wall-clock freshness guard for stale UI markers.
- `CapturedUT`: UT freshness guard when available.

The segment start path should require a fresh intent marker matching the focused vessel. If the marker is missing, stale, or points at a different vessel, do not immediate-start a switch segment. In that case the existing first-modification watcher may still arm according to its current rules.

Missed-switch recovery may create a segment only when it is recovering a previously captured explicit stock-action intent. A generic recovery pass must not become another source of immediate auto-record.

The intent marker must be serialized through `ParsekScenario.OnSave` / `ParsekScenario.OnLoad`, not kept only in static memory. Tracking Station Fly arms the marker in TRACKSTATION and consumes it in FLIGHT after scene load. F5/save while still in TRACKSTATION before the FLIGHT scene loads must preserve the marker or deliberately clear it with a logged stale-intent reason; it must not leave an untracked static-only intent.

## Proposed Data Model

Add an active switch/Fly segment attempt marker. Naming can be finalized during implementation, but this plan uses `SwitchSegmentSession`.

Terminology for this plan:

- `activeTree`: the live mutable tree in `ParsekFlight` during FLIGHT.
- `committed tree`: the durable tree kept by `RecordingStore` after Merge.
- `pendingTree`: the `RecordingStore` stash used for scene-exit/reload decisions and `DiscardPendingTree`.
- `active clone`: the #866 copy-on-write clone used as `activeTree` while the original committed tree remains committed.

Avoid "active pending tree" in code/docs for this feature. A tree can be live `activeTree`, stashed `pendingTree`, or committed; conflating those slots caused the original data-loss class of bugs.

Suggested fields:

- `SessionId`: stable GUID for this attempt.
- `TreeId`: tree ID for the live `activeTree` / stashed `pendingTree` carrying this segment attempt.
- `ParentRecordingId`: recording the new segment continues from, when known.
- `ActiveSegmentRecordingId`: new recording ID created for this switch/Fly segment.
- `SourceVesselPersistentId`: vessel PID before the switch, when available.
- `FocusedVesselPersistentId`: focused live vessel PID after the switch/Fly.
- `SwitchUT`: UT at which the segment was created.
- `EntryReason`: `MapSwitch`, `TrackingStationFly`, or `RecoveredConfirmedIntent`.
- `IntentId`: explicit stock-action intent that authorized immediate segment start.
- `PreSessionBranchPointIds`: branch points that existed before this segment was attached.
- `CommittedTreeId`: original committed tree ID if the segment was attached to a committed clone.

The marker should be serialized with `ParsekScenario` while the attempt is active or pending. It is the authority for segment-scoped discard after save/reload.

### Recording ownership field: do not overload `CreatingSessionId`

`Recording.CreatingSessionId` is already owned by Re-Fly sessions and is load-bearing in `LoadTimeSweep` (zombie NotCommitted discard and RP reap matching), `MergeJournalOrchestrator` (supersede/tombstone copy, RP reaper), `MergeDialog.TryDiscardActiveReFlyAttempt` (provisional cleanup), and `ParsekFlight` provisional-recording detection. Any non-empty value is currently interpreted as Re-Fly session output.

Stamping a switch-segment recording with `CreatingSessionId` would silently expose it to all of those paths and can corrupt the Re-Fly merge journal under save/reload. The implementation must therefore add a separate ownership field on `Recording`:

- `Recording.SwitchSegmentSessionId` (new, default `null`).
- Serialized through the existing recording codec and `ParsekScenario.OnSave` / `OnLoad`.
- Carried by `Recording.DeepClone` next to the existing `CreatingSessionId` / `SupersedeTargetId` / `ProvisionalForRpId` copy block.
- Discard, sidecar cleanup, and event purge match by `SwitchSegmentSessionId == SwitchSegmentSession.SessionId`, not by `CreatingSessionId`.
- `CreatingSessionId` stays exclusively Re-Fly's field; no implementation step in this plan should write or check it.

Wherever this plan previously said "stamp / match by `CreatingSessionId`", read "stamp / match by `SwitchSegmentSessionId`".

## Segment Creation

Introduce one focused helper for the actual tree mutation, for example:

`CreateSwitchContinuationSegment(RecordingTree tree, string parentRecordingId, Vessel liveVessel, double switchUT, SwitchSegmentEntryReason reason)`

The helper should:

1. Require a fresh matched stock-action intent marker.
2. If the focused vessel has a background recorder entry, close and flush that parent at the switch UT before the child is attached.
3. Create a new `Recording` with a new GUID.
4. Set vessel identity from the focused live vessel.
5. Set start time to the switch UT and capture an initial boundary sample so the segment is not empty.
6. Set `SwitchSegmentSessionId` to the active `SwitchSegmentSession.SessionId`. Do not touch `CreatingSessionId`.
7. Attach the segment under the selected parent recording via a branch point.
8. Set `tree.ActiveRecordingId` to the new segment ID.
9. Remove the live vessel PID from background-recorder maps after the parent boundary flush, if present.
10. Log the intent ID, parent ID, segment ID, tree ID, source PID, focused PID, reason, and UT.

The parent-side boundary flush is part of the segment creation contract. The current promotion path relies on `BackgroundRecorder.OnVesselRemovedFromBackground` to close sections and persist the final parent boundary; switch-continuation creation needs an equivalent close/flush before the new child starts, or the parent and child can overlap with stale/open trajectory state.

Prefer adding a new branch type such as `BranchPointType.VesselSwitchContinuation` or `BranchPointType.FocusSwitchContinuation`. Reusing `Launch` is mechanically possible but semantically misleading and makes logs/tests harder to read. If a new enum value is added, update scenario save/load and test generators in the same implementation commit.

The new branch type should be a non-claiming boundary/continuation marker. It should not carry split metadata (`SplitCause`, `DecouplerPartId`), merge metadata (`MergeCause`, `TargetVesselPersistentId`), breakup metadata, or terminal metadata. It records an observation/recording boundary, not a physical vessel ownership transfer.

Branch type audit for implementation:

- `BranchPoint.cs`: enum value, metadata-family comment, and `BranchPoint.ToString()`.
- `GhostingTriggerClassifier.IsClaimingBranchPoint`: classify the new type as non-claiming.
- Recording tree save/load codec in `RecordingStore`: round-trip the new enum value and preserve old saves.
- `BranchPointExtensionTests`: integer value pinning, round-trip, and `ToString`.
- Test builders/generators and direct fixture helpers that construct branch points, including `ChainSaveLoadTests`, `SyntheticRecordingTests`, `BackgroundSplitTests`, `TreeCommitTests`, `SessionSuppression*Tests`, and rendering/anchor fixtures.
- Any `switch` or exhaustive-looking `BranchPointType` logic found by `rg "BranchPointType"` before implementation.

### Parent Selection Risk

`Recording.ChildBranchPointId` is a single `string` field (`null` on leaves). A recording can therefore reference at most one outgoing branch point; many call sites assign it directly (`parentRecording.ChildBranchPointId = bp.Id;` in `ParsekFlight.cs`, `BackgroundRecorder.cs`, breakup paths, etc.). A `BranchPoint`'s `ChildRecordingIds` is a list and can fan out to many child recordings, but that fan-out belongs to one branch event (Dock/Undock/Breakup/etc.). Reusing an existing branch point's child list for an unrelated switch-continuation would conflate two different branch semantics.

Implementation policy:

1. Never attach a switch continuation directly to a non-terminal historical recording, and never overwrite or repurpose its existing `ChildBranchPointId`.
2. Walk forward from the matched recording through `ChildBranchPointId` / branch-point `ChildRecordingIds` to resolve the focused vessel to terminal-leaf candidates, using live vessel PID / materialized spawn identity to pick the chain that represents that vessel.
3. Attach the new segment under a unique terminal leaf (`ChildBranchPointId == null`) by creating a new `VesselSwitchContinuation` branch point and setting the leaf's `ChildBranchPointId` to its id.
4. If zero or multiple terminal candidates match, start a standalone switch segment and log `ambiguous-parent-start-standalone` with the candidate IDs. This preserves the requested immediate auto-record behavior without corrupting or surprising the historical tree.
5. Audit chain walkers (`GhostChainWalker`, codec paths, `EffectiveState` consumers) once the new branch type lands to confirm they treat `VesselSwitchContinuation` as non-claiming and traverse it correctly.

## Behavior by Entry Path

### Map View "Switch To" to tracked active-tree/background vessel

Current behavior promotes/resumes the background recording. New behavior should:

1. Consume a fresh Map Switch intent marker matching the focused vessel.
2. Complete the normal focus switch.
3. Background the vessel the player left.
4. Close/flush the focused parent if it was in background recording.
5. Create a new continuation segment under the focused vessel's existing recording.
6. Start the recorder on the new segment immediately.
7. Arm `SwitchSegmentSession` for segment-scoped merge/discard.

This should use a new start reason or mode rather than overloading `isPromotion`. The old promotion path resumes an existing recording ID; this feature must not do that for explicit Switch/Fly segment starts.

### Map View "Switch To" or Tracking Station "Fly" to committed spawned vessel

Current #866 behavior clones the committed tree, marks the target recording active, and records into the cloned same ID. New behavior should:

1. Consume a fresh Map Switch or Tracking Station Fly intent marker matching the focused vessel.
2. Keep the #866 committed-tree clone.
3. Arm the committed-tree restore attempt so original committed files/IDs remain protected.
4. Create a new continuation segment under the target recording inside the clone.
5. Set the clone active recording to the new segment ID.
6. Start recording immediately on the new segment.
7. Treat marker-owned new segment IDs as durable pending state for save/reload, not as #866 suppressed pending-only restore IDs.
8. On Merge, commit the clone with the new child segment.
9. On Discard, remove only the switch segment attempt and leave committed history unchanged.

This does not conflict with #866. #866 is the copy-on-write safety layer; this plan changes what gets written to the cloned tree after restore.

### Switch/Fly to unrelated vessel

If the focused vessel is unrelated to any active or committed Parsek tree, start a fresh recording immediately. If the player switched away from an active tree and a parent context is available, create a child segment using the existing "fresh start parent" concept. If there is no safe parent, create a standalone root recording/tree.

The first-modification watcher may remain as a fallback when immediate start is suppressed because the vessel is packed, the scene is not ready, the switch context is ambiguous, or Parsek is already inside a special attempt such as ReFly.

If the immediate start succeeds, the fallback watcher should be explicitly disarmed for that switch. If immediate start is rejected or delayed, the plan allows the watcher to arm only when the rejection reason is safe for delayed first-modification recording.

## Merge and Discard Scope

Add a switch-segment discard hook before the existing full pending-tree discard fallback, mirroring the placement of `TryDiscardActiveReFlyAttempt`.

Suggested flow in `MergeDialog.MergeDiscard`:

1. If active ReFly attempt exists, keep current ReFly scoped discard.
2. Else if active switch/Fly segment attempt exists, discard only that segment attempt.
3. Else fall back to `RecordingStore.DiscardPendingTree`.

Switch-segment discard should remove:

- `ActiveSegmentRecordingId`.
- Any recording with `SwitchSegmentSessionId == SessionId`.
- Any descendant recording/branch point created after the session started.
- Attempt-owned sidecar files.
- Attempt-owned game-state events and contract snapshots.
- Dangling parent/child branch references.
- The persisted `SwitchSegmentSession` marker.

Switch-segment discard must preserve:

- All previously committed recording IDs.
- The parent recording payload and sidecars.
- Other committed tree members and groups.
- Game-state events at or before the switch for committed recording IDs.

On Merge, commit the pending tree normally and clear the switch marker only after the commit succeeds. For new segment IDs, game-state events should be persisted with the attempt marker so F5/save/reload can continue the segment; Discard is responsible for purging those attempt-owned events.

### Final Disposition After Scoped Discard

For a committed spawned-vessel restore, the active clone has the same tree ID as the committed tree while the original committed tree remains in `RecordingStore` because of #866. After switch-segment discard removes the new segment from that clone, the implementation should drop the active/pending clone wrapper, clear the committed-tree restore attempt, clear `SwitchSegmentSession`, and return UI/state to the already-committed tree. It should not commit the pruned clone back over the original tree just to complete Discard.

For a switch segment added to an existing non-committed pending tree, the first Discard click removes only the segment attempt. If pre-existing pending changes remain after the segment is pruned, the scene-exit flow must immediately show the normal whole-pending-tree merge/discard dialog for the remaining work and must not proceed to the stock scene transition until the player resolves that second scope. The segment-scoped discard hook must not fall through to `RecordingStore.DiscardPendingTree` from the first dialog; a whole-tree discard requires a second explicit player choice.

Every scoped discard exit should leave exactly one owner for the surviving tree state: either the original committed tree remains in committed storage, or the pruned non-committed pending tree remains pending. No active clone should remain armed with a cleared segment marker.

## Interaction With #866 Event Filtering

PR #866 suppresses durable event-file writes for same-ID post-commit restore tails and for pending-only IDs inside an active committed-tree restore attempt. A new switch/Fly segment inside the cloned tree would otherwise look like a pending-only restore ID and have its events skipped, which conflicts with F5/save/reload.

Implementation should narrow all affected #866 suppression/save sites when `SwitchSegmentSession` is active:

1. Same-ID event tails for original committed recording IDs remain suppressed until Merge.
2. New recording IDs owned by `SwitchSegmentSession` are not suppressed merely because a committed-tree restore attempt is armed.
3. The switch marker must be saved before or atomically with marker-owned event persistence, so a later reload can either continue the pending segment or purge it on Discard.
4. Discard purges marker-owned events and contract snapshots by `SwitchSegmentSessionId` and owned recording IDs.
5. Save-time milestone/pending-event flush deferral must also be narrowed. The current #866 behavior defers milestone flushing while a committed-tree restore attempt is active; that is correct for same-ID unmerged restore tails, but marker-owned new segment events must not remain memory-only across F5/save/reload.
6. The implementation can either allow marker-owned new-ID milestone/event flushes after the marker is durable, or persist the marker-owned pending milestone state with the active segment marker. It must preserve #866 same-ID tail protection while making the switch segment reloadable.
7. Dirty sidecar skip must distinguish original committed-overlap recordings from marker-owned new segment recordings. Original committed sidecars stay protected before Merge; marker-owned new segment sidecars must be durable enough for F5/save/reload.
8. Tests should prove marker-owned segment events, sidecars, and milestone-relevant state survive F5/reload and are then purged on Discard.

Named sites to audit:

- `RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence`
- `RecordingStore.IsPendingOnlyCommittedTreeRestoreAttemptRecordingId`
- `ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave`
- `ParsekScenario.SaveActiveTreeIfAny`, especially the dirty sidecar skip gated by `RecordingStore.IsCommittedTreeRestoreAttemptRecordingId`
- `GameStateStore.SaveEventFile`, which consumes the suppression predicate and filters event/contract snapshot writes

## Save/Load, F5, and F9

This feature needs explicit quicksave and quickload handling because the player may switch/Fly, record a segment, save, reload, then choose Merge or Discard later.

Required behavior:

1. F5 after switch/Fly persists the active tree, active segment ID, and `SwitchSegmentSession`.
2. F9 back to that quicksave resumes the active segment, not the parent committed/background recording.
3. F9 or reload to a pre-switch save clears the switch marker and drops pending attempt state without touching committed history.
4. UT-backwards detection in `ParsekScenario.OnLoad` continues to protect quickload/revert from being mistaken for a vessel switch.
5. If the game is saved while the merge dialog is pending, reload restores enough marker state for Discard to remain segment-scoped.
6. If the game crashes after attempt sidecars are written but before merge, committed parent sidecars must remain durable. Attempt sidecars may be orphan-cleaned when no pending marker/tree references them.
7. If a committed-tree restore attempt and switch segment are both active, F5/F9 must restore both contexts consistently: the committed-history guard for same-ID tails and the segment marker for new-ID pending events.
8. Save-time pending-event and milestone flush behavior must not lose marker-owned new-ID events just because the #866 committed-tree restore attempt is also active.
9. Tracking Station Fly intent survives save/F5 while still in TRACKSTATION before FLIGHT loads, or clears with a logged stale-intent reason and no immediate auto-start.

For new segment IDs, do not blindly apply the #866 same-ID event-tail suppression. The segment is real pending state and must survive save/reload if the marker survives. The discard path should purge by `SwitchSegmentSessionId` and segment IDs instead.

## Dialog and UI Copy

The dialog should no longer imply that the player is merging a whole restored committed tree when the only new work is a switch/Fly segment.

Desired copy should communicate:

- The focused vessel name.
- That the pending work is a switched-vessel segment.
- The segment start UT or duration, if available.
- Discard removes this segment only.

The recordings window count should not drop committed entries after Discard. A good runtime assertion is: timeline count before switch equals timeline count after switch-segment Discard.

## Tests

Headless xUnit tests:

1. `CreateSwitchContinuationSegment` creates a new recording ID, attaches it under the chosen parent, sets active ID to the child, and leaves parent payload unchanged.
2. Non-leaf parent handling does not overwrite an existing child branch: it attaches to exactly one safe terminal leaf or starts a standalone recording with an ambiguity log.
3. Committed spawned-vessel restore creates clone plus new segment ID, not same-ID resume.
4. Discard after committed spawned-vessel switch removes only the switch segment and preserves all committed recording IDs, sidecars, groups, and original game-state events.
5. Merge after committed spawned-vessel switch commits the cloned tree with the new segment.
6. Save/load while a switch segment is active restores `SwitchSegmentSession` and resumes the segment ID.
7. F9/reload to a pre-switch save clears the switch marker and pending attempt state.
8. Event persistence for new segment IDs survives save/reload while pending and is purged on Discard, even when a #866 committed-tree restore attempt is armed.
9. Milestone/pending-event flush behavior preserves marker-owned new-ID state across F5/reload while still deferring or suppressing same-ID committed restore tails.
10. A successful immediate switch/Fly segment start consumes the intent marker and leaves no first-modification watcher armed for that same switch.
11. Generic vessel switches without a fresh stock-action intent marker do not immediate-start a switch segment.
12. Boarding, docking/undocking focus changes, ReFly marker arrivals, and missed-switch recovery without a confirmed intent marker do not immediate-start a switch segment.
13. Parent/background close/flush runs before the child segment starts and records a parent boundary.
14. Scoped discard of a committed-restore clone drops the active/pending clone wrapper and clears both the switch marker and committed-tree restore attempt.
15. Pre-existing pending changes survive a segment-scoped Discard and trigger a second explicit whole-pending-tree merge/discard decision.
16. Non-terminal historical matches attach only to a unique terminal leaf; ambiguous matches start standalone and log the ambiguity.
17. Tracking Station Fly intent marker survives save/F5 in TRACKSTATION before the FLIGHT scene consumes it, or clears with a logged stale-intent reason.
18. Two Switch/Fly actions in quick succession do not double-consume an intent, lose the active segment marker, or leave two immediate-start paths racing.
19. Map Switch and Tracking Station Fly entry reasons are logged distinctly.
20. ReFly scoped discard still wins for Parsek ReFly marker arrivals and does not use switch-segment logic.

Runtime/in-game tests or manual test script:

1. Spawn/commit a vessel in orbit, switch to it from Map view, confirm auto-record starts immediately with a new ID, exit scene, choose Discard, and confirm timeline count is unchanged.
2. Repeat from Tracking Station using stock "Fly".
3. Repeat both paths with Merge and confirm a new child segment appears.
4. F5 after switch/Fly, F9 back to that quicksave, continue recording, exit and Merge.
5. F5 in Tracking Station after pressing Fly but before FLIGHT finishes loading; reload and confirm the intent is either consumed once in FLIGHT or explicitly cleared stale with no auto-start.
6. F5 before switch, switch/Fly, then F9 to the pre-switch save; confirm no stale pending marker and no committed-history loss.
7. Trigger two Switch/Fly actions in quick succession; confirm only one active segment marker exists and the final focused vessel owns it.
8. Switch/Fly and immediately leave without major vessel changes; confirm the dialog is segment-scoped and idle/no-op cleanup does not remove committed history.

## Implementation Order

1. Add the plan/todo docs and review this design before code changes.
2. Add the `SwitchSegmentSession` marker and the new `Recording.SwitchSegmentSessionId` field (codec round-trip, `DeepClone`, scenario save/load) with serialization tests.
3. Add the pure tree helper for switch continuation segment creation with topology tests.
4. Extend the known Tracking Station `SpaceTracking.FlyVessel` patch to arm serialized intent on non-ghost passthrough.
5. Identify and test the narrow stock Map Switch intent source; do not use `FlightGlobals.SetActiveVessel`.
6. Add the new non-claiming branch type and complete the branch-type audit list above.
7. Wire confirmed stock-action entry paths to create/start segments immediately.
8. Narrow #866 suppression/save sites by name: `ShouldSuppressCommittedTreeRestoreAttemptEventPersistence`, `IsPendingOnlyCommittedTreeRestoreAttemptRecordingId`, `ShouldDeferPendingEventMilestoneFlushForSave`, `SaveActiveTreeIfAny` dirty sidecar skip, and `GameStateStore.SaveEventFile`.
9. Add segment-scoped merge/discard cleanup, final tree disposition, second-dialog behavior for remaining pending work, and event purge.
10. Add save/load/F5/F9 tests.
11. Update dialog copy and UI tests.
12. Run full headless tests.
13. Run targeted in-game tests and collect logs.

## Open Decisions for Review

1. Exact branch type name: `VesselSwitchContinuation`, `FocusSwitchContinuation`, or another clearer option.
2. Whether to rename/replace the existing `autoRecordOnFirstModificationAfterSwitch` setting or add a separate `autoRecordOnExplicitVesselSwitch` setting.
3. Which stock KSP method is the correct, narrow intent source for Map view "Switch To".
4. Whether zero-duration switch segments should still show a merge dialog or use a special auto-discard rule. The user-reported bug came from Discard, so any auto-discard must be proven segment-scoped first.
