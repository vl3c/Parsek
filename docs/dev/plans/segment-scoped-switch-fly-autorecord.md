# Segment-scoped auto-record for stock Fly / Switch-To buttons

Status: draft plan for review, no implementation yet.

Worktree: `Parsek-plan-switch-fly-segments`

Branch: `plan-switch-fly-segments`

Base: `origin/main` after PR #866 (`ea942184`, merged 2026-05-15).

## Summary

When the player explicitly moves focus to another vessel through a stock UI button — Tracking Station "Fly", KSC nearby-vessel marker "Fly", or Map view "Switch To" — Parsek should immediately start an auto-recording for that focused vessel. That recording must be a distinct segment with a new recording ID, not a same-recording resume of an existing committed tree member.

Keyboard vessel cycling (`[` / `]`) and other generic focus changes must NOT trigger immediate-start; those remain covered by the existing first-modification watcher (`fix-546-…`). Only confirmed stock UI button handlers arm the segment.

Note on Map view "Switch To": decompilation confirms this is `KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject` (internal name `"Focus"`, but the user-visible label is `#autoLOC_465671 = "Switch To"` returned by `OnCheckEnabled` for owned vessels). Its `OnSelect()` `OwnedVessel` branch calls `FlightGlobals.SetActiveVessel(vessel)` directly — the same lower-level call `[` / `]` uses (`VesselSwitching.Update`), but the UI dispatch (`FocusObject.OnSelect`) is a narrow Harmony target distinct from the keyboard path. Patch `FocusObject.OnSelect`, not `FlightGlobals.SetActiveVessel`.

**Decompilation correction:** an earlier draft of this plan stated that Map view had no stock Switch-To button, based on a pass that only inspected `MapContextMenu` (the popup container) and `MapNode` (icon class) and skipped the option-entry classes under `KSP.UI.Screens.Mapview.MapContextMenuOptions.*`. The `FocusObject` option entry was the miss: its constructor is `base("Focus")` (internal name "Focus"), so a class-name grep doesn't flag it, but `OnCheckEnabled` returns `#autoLOC_465671 = "Switch To"` for owned vessels and `OnSelect` calls `FlightGlobals.SetActiveVessel`. Any future reader cross-checking past plan claims should trust this corrected version. The earlier text has been overwritten in 546f00cd; the correction is recorded here so the contradiction in git history is auditable.

The merge dialog that appears when leaving the scene should be scoped to this new segment attempt. Choosing Discard must remove only the segment created by the switch/Fly action, including its descendant attempt recordings and game-state events, while preserving all previously committed recordings and sidecars.

This plan builds on PR #866. The #866 copy-on-write restore is still the right safety base: committed trees should not be detached and reused as mutable pending state. The missing piece is the UX/data model above the clone: a switch/Fly restore should attach a new child/continuation recording to the cloned tree and mark that child as an attempt-scoped segment.

## Related Current Behavior

PR #866 fixed the data-loss bug where switching into a previously spawned committed vessel detached the committed tree and reused it as the live active tree. Discard then treated the entire timeline as pending-only and removed all recordings. The current merged behavior clones the committed tree, arms a one-shot restore context, and protects same-ID event tails on Discard.

The earlier post-switch auto-record plan (`fix-546-post-switch-first-modification-autorecord.md`) added a watcher that arms after vessel switch and starts recording when the first meaningful modification is detected. That plan intentionally did not start on switch alone. This feature changes the product behavior for explicit stock UI button clicks (TS Fly, KSC marker Fly, Map view Switch To): the click itself is the intent signal, so the auto recorder should start immediately.

The first-modification watcher continues to cover every other focus-change class: `[` / `]` keyboard cycling, in-flight boarding/EVA, dock/undock focus shifts, and recovery flows. Those must not immediate-start a segment because they pass through `FlightGlobals.SetActiveVessel` (or equivalent) without firing a stock UI button click.

When an immediate Fly segment starts successfully, it must consume the intent marker and clear or suppress the first-modification watcher for that same vessel-switch event. The watcher is a fallback path only; it must not remain armed in a way that can create a second segment for the same stock action.

The consume + disarm hook differs by source:

- TS Fly and KSC marker Fly arm intent in TRACKSTATION / SPACECENTER and consume it in FLIGHT after `OnLoad` (post-scene-entry). The watcher disarm runs from the same `OnLoad`-tail seam.
- Map view Switch To arms intent inside FLIGHT and consumes it in the same FLIGHT scene from `OnVesselSwitchComplete`. The watcher disarm runs from `OnVesselSwitchComplete`, not `OnLoad`. Implementers must not put the Map-source disarm in the scene-load handler.

Either entry point produces the same end-state: switch segment armed, first-modification watcher disarmed, marker cleared once the segment recorder is live.

## Goals

1. Stock Tracking Station "Fly", stock KSC nearby-vessel marker "Fly", and stock Map view "Switch To" (FocusObject OwnedVessel branch) immediately start an auto-recording segment for the focused vessel.
2. The segment uses a new recording ID distinct from any committed or background recording ID.
3. If the focused vessel belongs to a committed spawned-vessel tree, the live flight `activeTree` is a clone of that committed tree plus the new segment.
4. If the focused vessel belongs to the current active/background tree, the new segment is attached as a continuation child rather than promoting/resuming the old recording ID.
5. If the focused vessel is unrelated to any Parsek tree, Parsek starts a fresh standalone tree for that vessel. Never re-parent the new segment under the player's previously active tree just because that tree was the most recent context — trees are vessel-coherent, and grafting an unrelated vessel onto a prior tree would corrupt that invariant.
6. Scene-exit merge UI, accept, discard, save/reload, F5, and F9 all operate at segment-attempt scope.
7. Existing #866 committed-history protection stays in place and remains useful for clone restore and same-ID safety.
8. Keyboard vessel cycling (`[` / `]`, `VesselSwitching.Update` → `FlightGlobals.SetActiveVessel` / `ForceSetActiveVessel`) and other generic focus changes do NOT immediate-start a switch segment; those remain covered by the existing first-modification watcher only.

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

The generic vessel-switch seams are not enough to decide immediate auto-record. `GameEvents.onVesselSwitching` and `GameEvents.onVesselChange` fire for more than the two in-scope stock actions, including boarding, missed-switch recovery, ReFly-related focus changes, `[`/`]` keyboard cycling, and other KSP focus transitions.

Implementation must add a positive intent marker that is set only by confirmed stock UI actions. All three sources are confirmed by decompiling `KSP_x64_Data/Managed/Assembly-CSharp.dll`:

- **Tracking Station Fly button** — `KSP.UI.Screens.SpaceTracking.FlyVessel(Vessel v)`. Already patched at `Patches/GhostTrackingStationPatch.cs` (`GhostTrackingFlyPatch`) for ghost blocking. Extend that patch with a sibling postfix or chained prefix so the non-ghost passthrough arms a `TrackingStationFly` intent before stock transitions to FLIGHT.
- **KSC nearby-vessel marker Fly button** — `KSP.UI.Screens.KSCVesselMarker.OnFlyButtonInput` listener (registered on `FlyButton.onClick`), which dispatches to `KSP.UI.Screens.KSCVesselMarkers.FlyVessel(Vessel v)`. That handler calls `FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(v))`. Add a Harmony prefix on `KSCVesselMarkers.FlyVessel(Vessel)` to arm a `KscMarkerFly` intent before the scene transition.
- **Map view Switch To** (in-flight map mode, click a vessel and pick "Switch To") — `KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject`. Internal constructor name is `"Focus"`, but `OnCheckEnabled` returns `#autoLOC_465671 = "Switch To"` as the visible label for owned vessels. `OnSelect()` branches by `FocusMode`:
  - `OwnedVessel` → `FlightGlobals.SetActiveVessel(vessel)` + `MapView.ExitMapView()` (this is the in-scope path).
  - `UnownedVessel` → `SpaceTracking.GoToAndFocusVessel(vessel)` (loads TRACKSTATION; the player then has to click TS Fly, which is handled by the TS source above).
  - `CelestialBody` → `PlanetariumCamera.SetTarget` (no vessel switch).

  Patch shape and pitfalls:

  - **Harmony attribute**: `[HarmonyPatch(typeof(KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject), nameof(KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject.OnSelect))]`. `OnSelect` is a `protected override` in the decompiled source — confirm during implementation that the override is materialized on `FocusObject` itself (not inherited unchanged from `MapContextMenuOption`); if KSP ever moves the implementation up to the base class, `HarmonyPatch` will throw at attribute-resolution time. Watch for this in the first build.
  - **Patch shape: Prefix arms + Postfix clears on refusal.** Decompilation of `FlightGlobals.setActiveVessel` confirms `GameEvents.onVesselSwitching.Fire(data, v)`, `v.MakeActive()`, and `GameEvents.onVesselChange.Fire(v)` all execute synchronously inside the method body before `return true`. Parsek's `OnVesselSwitchComplete` listener therefore runs *before* `SetActiveVessel` returns, which is before any Postfix on the calling `FocusObject.OnSelect` would run. A Postfix-arms-on-success shape would arm the marker too late — the consume site has already fired and missed it. The correct shape:
    - **Prefix**: gate on `GetMode() == FocusMode.OwnedVessel` AND `HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar` AND a non-null `vessel` Traverse read. When all three pass, arm the marker with `vessel.persistentId` and a fresh `IntentId`, and return that `IntentId` as a `__state` parameter for the Postfix. If the switch succeeds, `OnVesselSwitchComplete` (firing synchronously inside the original `SetActiveVessel`) consumes the marker by `IntentId` match.
    - **Postfix(__state)**: if the marker is still armed under the Prefix's `IntentId` after the original returned, consume didn't fire — `SetActiveVessel` took one of the early-return paths. Clear the marker with reason `refused-no-switch`. The `__state` scoping ensures the cleanup only clears a marker armed by *this* call, not one armed by a subsequent click.
  - **Refused early-return paths** (from the decompile of `FlightGlobals.setActiveVessel`, all return `false` without firing `onVesselSwitching` / `onVesselChange`): vessel is null; vessel is already active; `ClearToSave()` fails for any of six game-state reasons (not in atmosphere, under acceleration, moving over surface, about to crash, on a ladder, throttled up); target's `DiscoveryInfo.Level != DiscoveryLevels.Owned`. A separate unloaded-vessel branch fires `onVesselSwitchingToUnloaded` and calls `FlightDriver.StartAndFocusVessel`, transitioning to a fresh FLIGHT scene load. For Map Switch-To, this path means the consume is no longer in-scene; the Postfix should detect it (active vessel didn't change to target this frame AND scene is transitioning) and clear the in-scene marker so the next FLIGHT entry can rearm fresh through the normal cross-scene flow.
  - **Defensive `vessel` access**: `Traverse.Create(__instance).Field("vessel").GetValue<Vessel>()`. If `Traverse` returns null (the field was renamed in a KSP update), log a `[GhostMap]`-style Warn and bail without arming — do not arm with PID 0. Mirror the pattern in `Patches/GhostTrackingStationPatch.cs:489-494` (`GhostTrackingDeletePatch`).
  - **Double-fire diagnostic**: log if the Prefix arms a marker and `OnVesselSwitchComplete` then fires within the same call with `newVessel.persistentId != marker.TargetVesselPersistentId`. That signals either a `[` / `]` cycle racing with the click, an unexpected secondary `SetActiveVessel` caller, or a mod conflict that bypasses `VesselSwitching.Update`'s `MapView.MapIsEnabled` guard. The marker should be cleared with `stale-target-mismatch` and the watcher allowed to take over.
  - Do not patch `FocusObject.OnSelect`'s base method (`MapContextMenuOption.OnSelect`). The base method is empty; patching it would either no-op or fire for every menu option (`SetAsTarget`, `AddManeuver`, etc.) and arm Map-switch intent for clicks that aren't switches.

Patches must be on these specific UI handlers, not on `FlightGlobals.SetActiveVessel` / `FlightGlobals.ForceSetActiveVessel` / `FlightDriver.StartAndFocusVessel`. Those lower-level methods are shared by `[` / `]` cycling (`VesselSwitching.Update`), boarding, dock/undock, ReFly arrivals, save loading, scenario startup, and recovery flows. A prefix at that depth would false-fire on every one of them.

`VesselSwitching.Update` (the `[` / `]` cycle handler) guards on `MapView.MapIsEnabled` and reads `GameSettings.FOCUS_PREV_VESSEL` / `FOCUS_NEXT_VESSEL`. Confirmed it never produces a stock UI button click signal; it is the canonical example of a focus change that must NOT immediate-start a segment, even though it ends up in the same `FlightGlobals.SetActiveVessel` call that `FocusObject.OnSelect` uses.

Generic vessel-switch callbacks (`onVesselSwitching`, `onVesselChange`) should only consume an already-armed intent marker; they should not infer immediate-start intent by themselves.

Suggested intent marker fields:

- `IntentId`: stable GUID for the pending UI action.
- `Action`: `TrackingStationFly`, `KscMarkerFly`, or `MapSwitchTo`.
- `TargetVesselPersistentId`: expected target vessel PID (available from the patched handler's `Vessel v` argument or `FocusObject.vessel` field).
- `SourceScene`: `TRACKSTATION`, `SPACECENTER`, or `FLIGHT` (Map view "Switch To" arms intent from inside FLIGHT and is consumed in the same scene after `MapView.ExitMapView()` returns).
- `CapturedRealtime`: wall-clock freshness guard for stale UI markers. **TTL**: 10 seconds wall-clock for TS Fly / KSC marker Fly (covers scene-load latency on slow installs); 2 seconds wall-clock for Map Switch-To (consume happens same-frame in stock; anything longer is a stuck marker). Past TTL the consume site logs `stale-intent` and clears the marker without arming a segment.
- `CapturedUT`: UT freshness guard when available. Cleared without arming if UT regressed since `CapturedUT` (quickload between arm and consume).

The segment start path should require a fresh intent marker matching the focused vessel. If the marker is missing, stale, or points at a different vessel, do not immediate-start a switch segment. In that case the existing first-modification watcher may still arm according to its current rules.

Missed-switch recovery (the existing fallback that catches focus changes Parsek didn't get a clean signal for) must not become another source of immediate auto-record. If the recovery seam discovers a still-armed serialized intent marker, treat it as cross-run-orphaned (see below) and clear it with `stale-cross-run`; do not consume it.

The intent marker must be serialized through `ParsekScenario.OnSave` / `ParsekScenario.OnLoad`, not kept only in static memory. Tracking Station Fly arms the marker in TRACKSTATION and consumes it in FLIGHT after scene load. F5/save while still in TRACKSTATION before the FLIGHT scene loads must preserve the marker or deliberately clear it with a logged stale-intent reason; it must not leave an untracked static-only intent.

**Cross-run orphan**: if the player saves with a TS Fly or KSC Fly marker armed, exits the game, then loads that save on a fresh process, the marker survives serialization but is causally orphaned — no UI button was clicked in this session. The `OnLoad` tail must detect this case (marker present, source scene matches the loaded scene, but the loaded scene is the source scene itself and not a FLIGHT entry transitioning *from* it) and clear the marker with reason `stale-cross-run`. Wall-clock TTL would eventually clear it too, but the cross-run clear should fire deterministically on load so the player isn't surprised by a delayed auto-start after they click around in TS.

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
- `EntryReason`: `TrackingStationFly`, `KscMarkerFly`, or `MapSwitchTo`. (No "recovered" value — see the cross-run-orphan paragraph above for why recovery clears rather than consumes.)
- `IntentId`: explicit stock-action intent that authorized immediate segment start.
- `PreSessionBranchPointIds`: branch points that existed before this segment was attached.
- `CommittedTreeId`: original committed tree ID if the segment was attached to a committed clone.

The marker should be serialized with `ParsekScenario` while the attempt is active or pending. It is the authority for segment-scoped discard after save/reload.

### Marker lifetime: cross-scene vs in-scene asymmetry

The three sources have very different marker lifetimes; the implementation uses one uniform marker struct but the consume site and serialization need differ:

- **TS Fly / KSC marker Fly**: armed in TRACKSTATION / SPACECENTER, consumed in FLIGHT after `OnLoad` runs. Marker must survive `GamePersistence.SaveGame("persistent", ...)` (which `SpaceTracking.GoToAndFocusVessel` and `FlightDriver.StartAndFocusVessel` both invoke before scene transition), Unity scene tear-down, and FLIGHT scene load. Full `ParsekScenario.OnSave` / `OnLoad` round-trip is required.
- **Map view Switch To**: armed inside FLIGHT in the postfix on `FocusObject.OnSelect`, consumed in the same FLIGHT scene from `OnVesselSwitchComplete` (typically within 1–2 frames). Marker lifetime is effectively ephemeral. Serialization is still wired up uniformly (so the data model stays simple), but F5 mid-Switch-To is essentially impossible: `MapView.ExitMapView()` + `SetActiveVessel` + `OnVesselSwitchComplete` run synchronously inside the same Unity update tick.

If the player somehow does F5 while a Map-source marker is armed (e.g. an outside script pauses execution, a mod inserts a yield), the consume site must treat the resumed marker as stale on next FLIGHT entry: TTL-clear if the TARGET PID is no longer the active vessel, otherwise consume normally. Do not treat the Map-source marker as durable cross-scene state.

### Ledger and ERS / ELS routing

Marker-owned segment recordings produce game-state events through the existing `GameStateRecorder` → `Ledger.Actions` pipeline. Any code that decides whether a pending switch-segment recording or its events are visible to ERS/ELS callers must route through `EffectiveState.ComputeERS()` / `ComputeELS()`, never read `RecordingStore.CommittedRecordings` / `Ledger.Actions` directly (per the file-level CLAUDE.md gate enforced by `scripts/grep-audit-ers-els.ps1`). The new `Recording.SwitchSegmentSessionId` field is metadata-only; it does not need an ELS exemption, but any new query helper that filters by it must thread through `EffectiveState`. Discard-time `Ledger.Actions` cleanup for marker-owned recordings follows the same per-recording-id purge contract as #866's `PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard` but matched by `SwitchSegmentSessionId` instead of cutoff UT.

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

The parent-side boundary flush is part of the segment creation contract. The current promotion path relies on `BackgroundRecorder.OnVesselRemovedFromBackground` to close sections and persist the final parent boundary; switch-continuation creation should reuse that same helper (do not duplicate the flush logic in a second code path) immediately before attaching the new child. If `OnVesselRemovedFromBackground` is not safe to call when the focused vessel is no longer in the background map (e.g. for the TS Fly / KSC Fly cases where the vessel has just been brought live), the helper should branch on background presence and call the existing close path only when applicable. When the focused vessel is NOT in the BG map at segment-creation time, log `expected-bg-entry-missing` with the vessel PID and source reason so the case is detectable post-hoc (instead of silently skipping the flush and discovering missing boundary data later).

Prefer adding a new branch type such as `BranchPointType.VesselSwitchContinuation` or `BranchPointType.FocusSwitchContinuation`. Reusing `Launch` is mechanically possible but semantically misleading and makes logs/tests harder to read. Adding a new enum value requires bumping `RecordingTree.TreeFormatVersion` (or the equivalent recording-format counter) in the same commit. Per the pre-1.0 no-backwards-compat policy (`memory/feedback_no_recording_compat.md`), this is NOT a migration: old saves with the previous format version are simply rejected at load. The test obligation is therefore forward-compat / `default:`-arm hygiene, not load-old-data correctness — covered by the GhostingTriggerClassifier audit below. Update scenario save/load and test generators in the same implementation commit.

The new branch type should be a non-claiming boundary/continuation marker. It should not carry split metadata (`SplitCause`, `DecouplerPartId`), merge metadata (`MergeCause`, `TargetVesselPersistentId`), breakup metadata, or terminal metadata. It records an observation/recording boundary, not a physical vessel ownership transfer.

Branch type audit for implementation:

- `BranchPoint.cs`: enum value, metadata-family comment, and `BranchPoint.ToString()`.
- `GhostingTriggerClassifier.IsClaimingBranchPoint`: classify the new type as non-claiming. Audit every `switch (bp.Type)` consumer for a `default:` arm — the default must either log+treat-as-non-claiming or be exhaustive-cased to include `VesselSwitchContinuation`. Silent default fall-through is the canonical way a new enum value introduces a regression. This is the load-bearing test for the format bump; old-format saves are rejected at load per the no-backwards-compat policy, so there is nothing to migrate.
- Recording tree save/load codec in `RecordingStore`: round-trip the new enum value. No legacy-load test needed (rejected per policy); add a new-format round-trip test only.
- `BranchPointExtensionTests`: integer value pinning, round-trip, and `ToString`.
- Test builders/generators and direct fixture helpers that construct branch points, including `ChainSaveLoadTests`, `SyntheticRecordingTests`, `BackgroundSplitTests`, `TreeCommitTests`, `SessionSuppression*Tests`, and rendering/anchor fixtures.
- Any `switch` or exhaustive-looking `BranchPointType` logic found by `rg "BranchPointType"` before implementation.

### Composing with existing branch types during an active segment

A switch segment is alive in FLIGHT — meaning physical events (board, EVA, dock, undock, breakup, joint break) can fire on the segment vessel during the segment. The segment should not block those; it should compose:

- Board / EVA / Dock / Undock / JointBreak / Breakup events during an active switch segment generate their existing `BranchPointType.*` branch points as normal, with the active switch-segment recording as the parent and the new physical-event branch's child recording as the next leaf. The new child recording inherits `SwitchSegmentSessionId` only if it represents a continuation of the switched-to vessel; spawned debris from a Breakup, EVA kerbal exits, etc. that produce distinct vessels are physical splits and do NOT carry the marker.
- The merge dialog scope remains the original switch segment plus the children spawned during it. Discard removes the switch segment and the marker-owned subset; physical-split children of an unmarked vessel that happened to occur during the segment are evaluated under their own existing rules (e.g. dock-merge children stay if the dock target was already committed).
- If the switch segment vessel undergoes a terminal event (Crash, Recovery) before scene exit, the segment closes with a terminal branch point in the usual way. Discard still removes the segment + terminal; Merge commits both.

### Parent Selection Risk

`Recording.ChildBranchPointId` is a single `string` field (`null` on leaves). A recording can therefore reference at most one outgoing branch point; many call sites assign it directly (`parentRecording.ChildBranchPointId = bp.Id;` in `ParsekFlight.cs`, `BackgroundRecorder.cs`, breakup paths, etc.). A `BranchPoint`'s `ChildRecordingIds` is a list and can fan out to many child recordings, but that fan-out belongs to one branch event (Dock/Undock/Breakup/etc.). Reusing an existing branch point's child list for an unrelated switch-continuation would conflate two different branch semantics.

Implementation policy:

1. Never attach a switch continuation directly to a non-terminal historical recording, and never overwrite or repurpose its existing `ChildBranchPointId`.
2. Walk forward from the matched recording through `ChildBranchPointId` / branch-point `ChildRecordingIds` to resolve the focused vessel to terminal-leaf candidates, using live vessel PID / materialized spawn identity to pick the chain that represents that vessel.
3. Attach the new segment under a unique terminal leaf (`ChildBranchPointId == null`) by creating a new `VesselSwitchContinuation` branch point and setting the leaf's `ChildBranchPointId` to its id.
4. If zero or multiple terminal candidates match, start a standalone switch segment and log `ambiguous-parent-start-standalone` with the candidate IDs. This preserves the requested immediate auto-record behavior without corrupting or surprising the historical tree.
5. Audit chain walkers (`GhostChainWalker`, codec paths, `EffectiveState` consumers) once the new branch type lands to confirm they treat `VesselSwitchContinuation` as non-claiming and traverse it correctly.

## Behavior by Entry Path

Two of the three in-scope entry points (Tracking Station Fly, KSC marker Fly) cross scenes from TRACKSTATION / SPACECENTER into FLIGHT and consume the intent marker after `OnLoad`. The third (Map view Switch To) arms intent from inside FLIGHT and is consumed in the same FLIGHT scene after `MapView.ExitMapView()` returns and `OnVesselSwitchComplete` fires.

### Fly / Switch-To a committed spawned vessel

For TS Fly and KSC marker Fly, #866 behavior clones the committed tree on FLIGHT scene entry, marks the target recording active, and records into the cloned same ID. For Map view Switch To, the player is already in FLIGHT; the committed-tree clone path runs at the moment `OnVesselSwitchComplete` switches into a vessel whose PID matches a committed spawned-vessel recording. In both cases:

1. Consume a fresh `TrackingStationFly`, `KscMarkerFly`, or `MapSwitchTo` intent marker matching the focused vessel.
2. Keep the #866 committed-tree clone.
3. Arm the committed-tree restore attempt so original committed files/IDs remain protected.
4. Create a new continuation segment under the target recording inside the clone.
5. Set the clone active recording to the new segment ID.
6. Start recording immediately on the new segment.
7. Treat marker-owned new segment IDs as durable pending state for save/reload, not as #866 suppressed pending-only restore IDs.
8. On Merge, commit the clone with the new child segment.
9. On Discard, remove only the switch segment attempt and leave committed history unchanged.

This does not conflict with #866. #866 is the copy-on-write safety layer; this plan changes what gets written to the cloned tree after restore.

### Map view Switch To while a different tree is already live

Map view Switch To uniquely fires while the player is already in FLIGHT with an `activeTree` for the prior vessel. The intent-consume path must:

1. Let the existing `OnVesselSwitchComplete` background the previous active vessel through the normal stash/promote logic.
2. Close/flush the background recorder for the focused vessel if a background entry exists, so the parent boundary is durable before the new continuation attaches.
3. Run the committed-spawned-vessel branch above if the focused vessel matches a committed tree; otherwise run the unrelated-vessel branch below.

The pre-switch active tree continues to be Parsek's responsibility through its own normal flow (background recorder for the now-inactive prior vessel, no segment created on it). The new switch segment is owned exclusively by `SwitchSegmentSession`.

### Fly / Switch-To a vessel unrelated to any Parsek tree

If the focused vessel has no committed or active Parsek tree, start a fresh recording immediately under a new standalone tree. Use the new `VesselSwitchContinuation` start mode rather than overloading `isPromotion` — there is no parent recording to resume.

### Fly / Switch-To a vessel with a non-terminal historical match

Per `Parent Selection Risk` above: walk forward to a unique terminal leaf and attach the new continuation there. If zero or multiple terminal candidates match, start a standalone tree and log `ambiguous-parent-start-standalone`.

### Generic focus changes that are NOT in scope

- `[` / `]` keyboard cycling (`VesselSwitching.Update` → `FlightGlobals.SetActiveVessel` / `ForceSetActiveVessel`). Goes through the same lower-level `SetActiveVessel` as `FocusObject.OnSelect`, but does not route through any UI button handler, so the intent marker is never armed.
- `FocusObject.OnSelect` `UnownedVessel` branch (the "Track" label path that bounces to TRACKSTATION). It loads TS; the player must then click TS Fly to actually transition to FLIGHT, and that click is handled by the TS source.
- `FocusObject.OnSelect` `CelestialBody` branch (camera target only, no vessel switch).
- Boarding / EVA transitions (covered by existing `BranchPointType.EVA` / `BranchPointType.Board` paths).
- Dock / undock focus shifts (covered by existing `BranchPointType.Dock` / `BranchPointType.Undock`).
- ReFly marker arrivals (covered by `MergeJournalOrchestrator` and `MergeDialog.TryDiscardActiveReFlyAttempt`).
- Missed-switch recovery without a captured stock-action intent.
- Internal `FlightDriver.StartAndFocusVessel` invocations during save load, scenario startup, or recovery.

The existing first-modification watcher (`fix-546-…`) continues to cover these as a fallback. If the immediate-start path succeeds, the watcher must be disarmed for that scene transition.

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

The second dialog must offer Cancel alongside Merge / Discard — Cancel returns the player to FLIGHT with the segment-pruned pending tree intact. Scene exit is blocked only until the player picks one of the three; there must not be a state where the first dialog is dismissed but the scene is half-transitioning while the second one is waiting. Mirror the input-lock contract `MergeDialog` already uses for its single dialog (`MergeDialog.MergeDiscard` releases the lock only on a terminal choice).

Every scoped discard exit should leave exactly one owner for the surviving tree state: either the original committed tree remains in committed storage, or the pruned non-committed pending tree remains pending. No active clone should remain armed with a cleared segment marker.

### Two rapid switches semantics

The same `SwitchSegmentSession` slot only holds one active attempt. Behavior when a second stock-action intent fires while a segment is already active:

- **Different target vessel** (e.g. Map Switch-To from vessel A to vessel B, then immediately Map Switch-To from vessel B to vessel C, all without leaving FLIGHT): close the active segment as a normal in-progress recording — do NOT auto-merge or auto-discard. Background it through the usual `OnVesselSwitchComplete` background-the-prior-vessel flow. Then open a new switch segment for vessel C with a fresh `SessionId`. The merge dialog at scene exit will show both segments (A→B and B→C) under the segment-scope path, each owned by its own `SwitchSegmentSessionId`. Discard removes both as marker-owned attempts; Merge commits both.
- **Same target vessel** (rapid double-click on the same Switch-To button): the first action arms the marker; the second one fires the postfix again and sees `FlightGlobals.ActiveVessel.persistentId == marker.TargetVesselPersistentId` already true. Log `duplicate-intent-same-target` and no-op (do not re-arm, do not start a second segment).
- **Marker armed but never consumed before next intent**: TTL-clear the orphan with `stale-intent-superseded`, then arm the new intent.

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
8. Clone-parent sidecar interaction: the new segment's parent recording inside the active clone has the same ID as the original committed parent. #866's `SaveActiveTreeIfAny` skips the clone-parent sidecar save under `IsCommittedTreeRestoreAttemptRecordingId`, which is correct — we don't want the clone parent overwriting the committed parent's sidecar. But the implementation must ensure the new child segment's `ParentBranchPointId` is written only into the clone's in-memory tree and propagated to the committed tree only on Merge. The clone's tree-level metadata (BranchPoints list, ActiveRecordingId) is segment-scoped and not committed until Merge; the parent recording's `ChildBranchPointId` field is mutated on the clone copy of the parent but the original committed parent's `ChildBranchPointId` stays unset until Merge. Discard restores the clone's `ChildBranchPointId` mutation by simply dropping the clone.
9. Tests should prove marker-owned segment events, sidecars, and milestone-relevant state survive F5/reload and are then purged on Discard.

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
- The entry reason (TS Fly / KSC marker Fly / Map Switch-To) should drive the verb in the copy ("After your Fly into Kerbal X Probe…" vs "After your Switch To Kerbal X Probe…"). Players associate the action with the button label they clicked; mismatched copy will read as a Parsek bug.

Suggested template strings:

- TS Fly / KSC Fly: `"Keep your new flight on '{vesselName}'? Choosing Discard returns to the committed timeline; choosing Merge appends this segment under it."`
- Map Switch-To: `"Keep your switch into '{vesselName}'? Choosing Discard returns to the committed timeline; choosing Merge appends this segment under it."`

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
19. Tracking Station Fly, KSC marker Fly, and Map Switch-To entry reasons are logged distinctly.
20. ReFly scoped discard still wins for Parsek ReFly marker arrivals and does not use switch-segment logic.
21. `[` / `]` keyboard cycling (`VesselSwitching.Update`) and `FlightGlobals.SetActiveVessel` / `ForceSetActiveVessel` invocations from non-UI paths (boarding, dock/undock, recovery, save load) leave the intent marker unset and do not immediate-start a segment, even though they share `SetActiveVessel` with `FocusObject.OnSelect`.
22. `FocusObject.OnSelect` Unowned and CelestialBody branches do not arm the intent marker; only the OwnedVessel branch does. (In-game test in `RuntimeTests.cs` — `FocusObject.OnSelect` is private/inaccessible from xUnit assemblies, so cover this via the live-runtime test framework with the FLIGHT scene category, not as a unit test.)

Runtime/in-game tests or manual test script:

1. From Tracking Station, click Fly on a previously committed orbiting vessel; confirm auto-record starts immediately with a new ID, exit scene, choose Discard, and confirm timeline count is unchanged.
2. From KSC, click a nearby-vessel marker, then click Fly; confirm same behavior as test 1.
3. In FLIGHT, open Map view, click on another previously committed vessel's orbit/icon, pick "Switch To" from the popup; confirm auto-record starts immediately with a new ID on the focused vessel, then exit scene and Discard — confirm timeline count is unchanged.
4. Repeat all three paths (TS Fly, KSC marker Fly, Map Switch To) with Merge and confirm a new child segment appears.
5. F5 after Fly / Switch-To, F9 back to that quicksave, continue recording, exit and Merge.
6. F5 in Tracking Station after pressing Fly but before FLIGHT finishes loading; reload and confirm the intent is either consumed once in FLIGHT or explicitly cleared stale with no auto-start.
7. F5 before Fly / Switch-To, then perform the action, then F9 to the pre-action save; confirm no stale pending marker and no committed-history loss.
8. Trigger two switch actions in quick succession (e.g., Map Switch-To vessel A, then Map Switch-To vessel B without leaving FLIGHT); confirm only one active segment marker exists and the final focused vessel owns it.
9. Fly / Switch-To and immediately leave without major vessel changes; confirm the dialog is segment-scoped and idle/no-op cleanup does not remove committed history.
10. In FLIGHT, press `[` / `]` to cycle vessels; confirm no segment is started by the cycle itself and the first-modification watcher behaves as before.
11. In FLIGHT map view, click "Switch To" on an unowned vessel (the Track path); confirm the game loads TRACKSTATION without arming a Parsek intent, and that no segment is created. (If the player then clicks TS Fly, the TS source arms intent normally.)

## Implementation Order

1. Add the plan/todo docs and review this design before code changes.
2. Add the `SwitchSegmentSession` marker and the new `Recording.SwitchSegmentSessionId` field (codec round-trip, `DeepClone`, scenario save/load) with serialization tests.
3. Add the pure tree helper for switch continuation segment creation with topology tests.
4. Add the new non-claiming `BranchPointType.VesselSwitchContinuation` value and complete the branch-type audit list above.
5. Extend `GhostTrackingFlyPatch` to arm serialized `TrackingStationFly` intent on the non-ghost passthrough.
6. Add a Harmony prefix on `KSCVesselMarkers.FlyVessel(Vessel)` to arm serialized `KscMarkerFly` intent before the scene transition.
7. Add a Harmony Prefix + Postfix pair on `KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject.OnSelect()`. The Prefix arms `MapSwitchTo` intent only when `GetMode() == FocusMode.OwnedVessel` AND `CanSwitchVesselsFar` is true AND `Traverse.Create(__instance).Field("vessel").GetValue<Vessel>()` returns non-null, returning the new `IntentId` as `__state`. The Postfix(`__state`) clears the marker with `refused-no-switch` if it is still armed under that `IntentId` after the original returned (consume didn't fire because `SetActiveVessel` took an early-return path). See "Patch shape: Prefix arms + Postfix clears on refusal" above for the decompilation rationale.
8. Add the new `autoRecordOnExplicitVesselSwitch` setting next to the existing `autoRecordOnFirstModificationAfterSwitch` (do not rename or replace the existing one; the two cover different focus-change classes). **Default: ON.** The escape hatch in Resolved Decision #5 only flips it OFF if playtest finds Map Switch-To ping-pong noisy.
9. Wire confirmed stock-action entry paths in `OnVesselSwitchComplete` / post-load to consume the intent marker and create/start segments immediately.
10. Narrow #866 suppression/save sites by name: `ShouldSuppressCommittedTreeRestoreAttemptEventPersistence`, `IsPendingOnlyCommittedTreeRestoreAttemptRecordingId`, `ShouldDeferPendingEventMilestoneFlushForSave`, `SaveActiveTreeIfAny` dirty sidecar skip, and `GameStateStore.SaveEventFile`.
11. Add segment-scoped merge/discard cleanup, final tree disposition, second-dialog behavior for remaining pending work, and event purge.
12. Add save/load/F5/F9 tests.
13. Update dialog copy and UI tests.
14. Run full headless tests.
15. Run targeted in-game tests and collect logs.

## Resolved Decisions

1. **Branch type name**: `BranchPointType.VesselSwitchContinuation`. Matches existing `OnVesselSwitching` / `OnVesselSwitchComplete` naming in the codebase; "Focus" reads as camera/UI rather than the actual action.
2. **Settings**: add a separate `autoRecordOnExplicitVesselSwitch` toggle, keep `autoRecordOnFirstModificationAfterSwitch` as-is. The two cover orthogonal cases: this feature handles explicit Fly UI buttons; the existing watcher continues to handle `[` / `]` cycling and other generic focus changes.
3. **Stock intent sources** (all confirmed by decompiling `Assembly-CSharp.dll`): `SpaceTracking.FlyVessel` (TS), `KSCVesselMarkers.FlyVessel` (KSC nearby-vessel marker), and `MapContextMenuOptions.FocusObject.OnSelect` OwnedVessel branch (in-flight map view "Switch To"; internal class name `FocusObject` but visible label is `#autoLOC_465671 = "Switch To"`). Patch each specific UI handler; do not patch `FlightGlobals.SetActiveVessel` / `ForceSetActiveVessel` / `FlightDriver.StartAndFocusVessel` (shared with `[` / `]` and other non-UI paths).
4. **Zero-duration segments**: always show the segment-scoped merge dialog. Do not auto-discard. The bug that motivated this plan was a Discard codepath erasing committed history; until segment-scoped Discard ships and bakes, auto-discard is unsafe. Revisit once the Discard path has field confidence and add a sub-N-second / zero-event fast path then.
5. **Map Switch-To UX escape hatch**: TS Fly and KSC Fly involve a scene transition — strong "I'm committing to fly this vessel" signal. Map Switch-To is a mid-flight focus change that some players use casually as a "peek at this vessel, then switch back" navigation. If playtest finds the new segments noisy for the ping-pong case, fall back to "Map Switch-To arms the first-modification watcher" instead of immediate-start. Manual test #8 (two rapid Map Switch-Tos) is the canary. Do not regress to this fallback by default; only flip behind the `autoRecordOnExplicitVesselSwitch` setting if playtest demands it.

## Remaining Open Decisions

None at plan-review time. Issues uncovered during implementation should be added back here with a date and reason.
