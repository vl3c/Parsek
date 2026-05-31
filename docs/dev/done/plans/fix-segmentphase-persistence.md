# Fix Plan: SegmentPhase Persistence and Classifier Cleanup

Date: 2026-05-11

Worker: B

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-segmentphase-persistence`

Branch: `fix-segmentphase-persistence`

Base: `c84010d8 Merge pull request #818 from vl3c/fix-tail-orbit-state-vector-frame` (`origin/main`)

Evidence bundles:

- `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_1713`
- `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_2123`

## Scope

This phase owns only the follow-up plan for:

1. Persisting a recording's final/end `SegmentPhase` and `SegmentBodyName`
   instead of leaving saved tree records at fork/start state.
2. Making the `ParsekFlight.StopRecording` final phase tag path actually feed
   persisted tree recordings, or moving that responsibility to the real
   persistence path.
3. Removing the duplicated live-vessel SegmentPhase classifier from the three
   current sites.

No production or test code is changed in this planning phase.

## Verified Evidence

### 2026-05-10_1713: stale saved phase on orbital fork

`logs\2026-05-10_1713\saves\s14\persistent.sfs` contains the reported
`rec_b1566ae4b795437fb1af8bc5d6c1b1e0` row:

- lines 1099-1106: `recordingId = rec_b1566...`, `explicitEndUT = 992.234...`,
  `terminalState = 0`.
- `Source/Parsek/TerminalState.cs` defines `0` as `Orbiting`.
- lines 1110-1117: terminal orbit metadata is present on `Kerbin`.
- lines 1123-1124: saved `segmentPhase = atmo`,
  `segmentBodyName = Kerbin`.

This confirms the mismatch: the persisted endpoint is orbital, but the phase
label persisted as atmospheric.

The same bundle's `KSP.log` shows the Re-Fly fork at 17:04:43.113:

- `AtomicMarkerWrite: in-place continuation forked - fork rec_b1566...`
- `inheritedFrom=origin sourceRec=32d9674c...`

The previous Stage 1 plan identified that source as an atmo parent segment, so
the stale value's origin is consistent with fork/start state rather than final
state.

The final tree save path in this run was scene-exit/force-stop, not the UI
`StopRecording` wrapper:

- `KSP.log` 17:05:49.025: `Recording stopped...`
- next line: `Auto-stopped recording due to scene change`
- next line: `Flushed recorder to tree recording 'rec_b1566...'`

That matters because `FlightRecorder.ForceStop()` intentionally does not build
`CaptureAtStop`, so any fix that only copies `CaptureAtStop.SegmentPhase` will
not cover scene-exit finalization.

### 2026-05-10_2123: fork-start tag now persists, but still not a final tag

`logs\2026-05-10_2123\KSP.log` verifies the Stage 1 fork-start tag:

- 21:18:09.296:
  `TagForkInitialSegmentPhase: tagged from live vessel rec=rec_f1363... body=Kerbin phase=exo situation=SUB_ORBITAL alt=70084m`

The saved row in `logs\2026-05-10_2123\saves\s15\persistent.sfs` has:

- lines 1113-1120: `recordingId = rec_f1363...`, `terminalState = 0`
  (`Orbiting`).
- lines 1137-1138: `segmentPhase = exo`, `segmentBodyName = Kerbin`.

This run does not expose a wrong label because the fork-start state was already
`exo`, and the final orbital state is also `exo`. It does verify that the saved
value can come from `TagForkInitialSegmentPhase`. A future atmo/surface
fork-start that ends in orbit would still save the start tag unless finalization
overwrites it.

This run also used scene-exit/force-stop for the active Re-Fly fork:

- `KSP.log` 21:19:16.006: `Recording stopped...`
- next line: `Auto-stopped recording due to scene change`
- next line: `Flushed recorder to tree recording 'rec_f1363...'`
- 21:19:16.008: finalization logs the record as `terminal=Orbiting`.

### Code facts

`Source/Parsek/ParsekFlight.cs`:

- `TagSegmentPhaseIfMissing` at lines 3773-3795 duplicates body/altitude/situation
  classification and writes only when `pending.SegmentPhase` is empty.
- `FlushRecorderToTreeRecording` at lines 3101-3171 appends points/events,
  marks files dirty, and copies start-location fields. It does not copy
  `CaptureAtStop.SegmentPhase` or `CaptureAtStop.SegmentBodyName`.
- `StopRecording` at lines 9827-9871 calls `recorder.StopRecording()`, applies
  chain metadata to `recorder.CaptureAtStop`, then duplicates the classifier and
  writes the final phase to `recorder.CaptureAtStop` only.
- `FinalizeTreeRecordings` at lines 11119-11140 uses `ForceStop()` for active
  tree finalization when the recorder is still running, then calls
  `FlushRecorderToTreeRecording`.
- `FinalizeIndividualRecording` at lines 11753-12120 already resolves terminal
  state, terminal orbit, terminal position, and stable terminal snapshots from
  the live vessel or fallback evidence. It currently does not update
  `SegmentPhase`.

`Source/Parsek/FlightRecorder.cs`:

- `BuildCaptureRecording` at lines 6300-6364 copies data/snapshots/start fields
  but does not classify `SegmentPhase`.
- `StopRecording` at lines 6599-6622 builds `CaptureAtStop`.
- `StopRecordingForChainBoundary` at lines 6628-6650 builds `CaptureAtStop`.
- `ForceStop` at lines 9325-9345 explicitly does not build `CaptureAtStop`.

`Source/Parsek/ChainSegmentManager.cs`:

- `CommitBoundarySplit` at lines 722-740 unconditionally writes
  `pending.SegmentPhase` and `pending.SegmentBodyName` from the completed
  boundary phase. This should remain authoritative for boundary-split chain
  segments.
- `CommitVesselSwitchTermination` at lines 755-797 duplicates the live-vessel
  classifier against `recordedVessel`.

`Source/Parsek/RewindInvoker.cs`:

- `CopyInheritedIdentityForFork` no longer copies `SegmentPhase` /
  `SegmentBodyName`.
- `TagForkInitialSegmentPhase` at lines 292-321 tags the provisional fork from
  the live post-Strip vessel using `ParsekFlight.TagSegmentPhaseIfMissing`.
  That tag is intentionally a start/fork tag and must be overwritten when a
  reliable final/end tag is available.

## Proposed Fix

### 1. Extract one SegmentPhase classifier

Add a single helper for the current live-vessel classifier. Preferred shape:

- File: `Source/Parsek/SegmentPhaseClassifier.cs` or, if we want the smallest
  diff, an internal static helper region in `ParsekFlight.cs`.
- API:
  - `internal static bool TryClassify(Vessel vessel, out string phase, out string bodyName)`
  - `internal static string ClassifyFromValues(Vessel.Situations situation, bool hasAtmosphere, double altitude, double atmosphereDepth, double approachAltitude)`

The value-based helper is for headless xUnit coverage. The vessel helper should
only adapt KSP `Vessel` data into the pure helper. This avoids tests that need
to instantiate Unity/KSP vessel objects.

Replace duplicate classifier bodies in:

- `ParsekFlight.TagSegmentPhaseIfMissing`
- `ParsekFlight.StopRecording`
- `ChainSegmentManager.CommitVesselSwitchTermination`

Keep existing vocabulary unchanged:

- `LANDED`, `SPLASHED`, `PRELAUNCH` -> `surface`
- atmospheric body and `altitude < atmosphereDepth` -> `atmo`
- atmospheric body and `altitude >= atmosphereDepth` -> `exo`
- non-atmospheric body and `altitude < FlightRecorder.ComputeApproachAltitude(body)` -> `approach`
- otherwise -> `exo`

### 2. Make `CaptureAtStop` tags persist on normal/manual stop paths

Update `FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree)`:

- After resolving `treeRec` and before marking files dirty, if
  `rec.CaptureAtStop` has non-empty `SegmentPhase`, copy both
  `SegmentPhase` and `SegmentBodyName` to `treeRec`.
- This copy must override a fork-start tag. Do not guard on
  `string.IsNullOrEmpty(treeRec.SegmentPhase)`.
- Do not compare `rec.CaptureAtStop.RecordingId` to `treeRec.RecordingId`.
  `BuildCaptureRecording` creates a fresh GUID at
  `FlightRecorder.cs:6306`, so that ID is not the tree row ID.
- Log one concise `Verbose` line when the copy changes either field:
  `FlushRecorderToTreeRecording: applied final SegmentPhase from CaptureAtStop rec=... old=... new=...`.

Guardrails:

- Use a concrete helper predicate:
  `ShouldApplyFinalSegmentPhaseFromCapture(Recording treeRec, string activeRecordingId, Recording captureAtStop)`.
- Required predicate conditions for the first patch:
  - `treeRec != null`
  - `captureAtStop != null`
  - `captureAtStop.SegmentPhase` is non-empty
  - `activeRecordingId` is non-empty and equals `treeRec.RecordingId`
  - `ShouldAllowFinalEndpointSegmentPhase(treeRec, activeRecordingId)` is true
- The proof that the target is the active row comes from `tree.ActiveRecordingId`,
  not from the capture recording ID.

This makes the existing `ParsekFlight.StopRecording` final tag block useful for
tree recordings, instead of writing to a transient object that is never copied.

### 3. Cover force-stop / scene-exit paths that have no `CaptureAtStop`

Because `FlightRecorder.ForceStop()` intentionally does not create
`CaptureAtStop`, the 2026-05-10 retained saves require a second path.

Update finalization, not `ForceStop`:

- In `FinalizeIndividualRecording`, apply final endpoint `SegmentPhase` only
  after the terminal orbit refresh block that starts at `ParsekFlight.cs:12020`
  and after `RecordingEndpointResolver.RefreshEndpointDecision(rec, "FinalizeIndividualRecording")`
  at `ParsekFlight.cs:12088`.
- Apply it before the final verbose summary log at `ParsekFlight.cs:12114`.
- This placement must run for records that already had `TerminalStateValue`
  before entering `FinalizeIndividualRecording`, not only records that enter the
  `if (isLeaf && !rec.TerminalStateValue.HasValue)` block at
  `ParsekFlight.cs:11869`.

Concrete first-patch ownership predicate:

`ShouldAllowFinalEndpointSegmentPhase(Recording rec, string activeRecordingId)`

Return true only when all of these hold:

- `rec != null`
- `activeRecordingId` is non-empty and equals `rec.RecordingId`
- `string.IsNullOrEmpty(rec.ChildBranchPointId)` so the record is an active
  unsplit tree leaf, not an already-branched interior segment
- `HasCommittedChainSegmentOwnership(rec)` is false, where that helper is:
  `!string.IsNullOrEmpty(rec.ChainId) && rec.ChainIndex >= 0`
- either `string.IsNullOrEmpty(rec.ChainId)` or the record has explicit Re-Fly
  provisional identity (`ProvisionalForRpId` or `CreatingSessionId`) while still
  failing the committed-chain ownership check above
- the helper is called during active tree finalization/flush, before any
  optimizer split pass

Explicit skips:

- committed chain segments with `ChainId`/`ChainIndex` ownership
- non-active tree rows
- records already produced by `RecordingOptimizer.SplitAtSection`; this first
  patch must not run as a post-optimizer repair. Optimizer-owned records keep
  their `TrackSection.environment` phase set by `SplitAtSection`.

Overwrite rule:

- The final/end tag overwrites empty tags and Re-Fly fork-start tags.
- The final/end tag does not overwrite chain-boundary tags or optimizer split
  tags.

Endpoint source order for force-stop / scene-exit:

1. If `finalizeVesselFound`, classify from `finalizeVessel` using the extracted
   live-vessel helper.
2. Otherwise, if `TerminalStateValue` is `Orbiting` or `Docked` and
   `TerminalOrbitBody` is non-empty, set phase `exo` and body
   `TerminalOrbitBody`.
3. Otherwise, if `TerminalStateValue` is `Landed` or `Splashed` and
   `TerminalPosition`/`SurfacePos` has body context, set phase `surface` and
   that body.
4. Otherwise, if the final `TrackSection` can be identified, use
   `TrackSection.environment` via `RecordingOptimizer.EnvironmentToPhase` (or a
   shared equivalent) and body from terminal orbit/position metadata when
   available.
5. Only then consider point altitude, and only when the point is known to be
   from a `ReferenceFrame.Absolute` section or from legacy no-section data.

RELATIVE safety rule:

- Never read `TrajectoryPoint.latitude`, `TrajectoryPoint.longitude`, or
  `TrajectoryPoint.altitude` from a `ReferenceFrame.Relative` section as
  lat/lon/alt. In format v6+ RELATIVE sections those fields are anchor-local
  metres.
- For RELATIVE final sections, either resolve the endpoint through existing
  relative/absolute-shadow helpers (`TryResolveRelativeWorldPosition`,
  `TryResolveRelativeOffsetWorldPosition`, section `absoluteFrames`, or the
  established relative playback resolver path) and then classify from the
  resolved world/body altitude, or leave `SegmentPhase` unchanged and log a
  `Verbose` skip reason.
- Do not add an ad hoc relative resolver in this patch. If existing helpers
  cannot resolve the endpoint reliably from available context, skip and log.

### 4. Replace the `StopRecording` duplicated block, do not delete behavior

Change `ParsekFlight.StopRecording` lines 9847-9869 to call the shared helper
instead of inlining the classifier.

The block is not strictly unreachable: manual stop creates `CaptureAtStop` and
can tag it. It is effectively dead for persisted tree recordings today because
`FlushRecorderToTreeRecording` ignores the tag, and it is skipped entirely for
scene-exit `ForceStop` paths. After steps 2 and 3, the block becomes a useful
normal-stop capture step and no longer carries its own divergent classifier.

## Proposed Tests

### Unit tests for classifier

Add `Source/Parsek.Tests/SegmentPhaseClassifierTests.cs`:

- landed/splashed/prelaunch -> `surface`
- atmospheric below atmosphere depth -> `atmo`
- atmospheric at/above atmosphere depth -> `exo`
- non-atmospheric below approach threshold -> `approach`
- non-atmospheric at/above approach threshold -> `exo`

These should call the value-based helper, not construct `Vessel`.

### Unit tests for capture-to-tree propagation

Add focused tests near existing flight/follow-up structural coverage:

- `FlushRecorderToTreeRecording_AppliesCaptureAtStopSegmentPhase_OverridesForkStartTag`
  - tree record starts with `SegmentPhase = "atmo"`, `SegmentBodyName = "Kerbin"`.
  - recorder `CaptureAtStop` carries `SegmentPhase = "exo"`, `SegmentBodyName = "Kerbin"`.
  - `CaptureAtStop.RecordingId` is deliberately different from
    `treeRec.RecordingId`.
  - `activeRecordingId` equals `treeRec.RecordingId`.
  - after flush, tree record is `exo`.
- `FlushRecorderToTreeRecording_CaptureIdDiffersFromTreeId_StillAppliesWhenActiveRowMatches`
  - direct regression for the `FlightRecorder.cs:6306` fresh GUID fact.
  - asserts the predicate does not compare capture ID to tree ID.
- `FlushRecorderToTreeRecording_NoCapturePhase_PreservesExistingTag`
  - avoids accidental clearing when `CaptureAtStop` is absent or untagged.
- `FlushRecorderToTreeRecording_DoesNotOverwriteChainBoundaryTag`
  - active row has committed chain identity and existing `SegmentPhase`.
  - capture phase differs.
  - predicate returns false and tag is preserved.
- `FlushRecorderToTreeRecording_DoesNotOverwriteOptimizerSplitRecord`
  - record is not the active tree row and already has a phase derived from a
    `TrackSection.environment` split.
  - capture phase differs.
  - predicate returns false and tag is preserved.

`FlushRecorderToTreeRecording` is currently an instance private method, so the
implementation may need either:

- make a tiny internal static helper that copies phase fields and unit-test that
  helper directly, or
- widen the flush method only if there is already a test-access pattern for
  `ParsekFlight`.

Prefer the helper to avoid exposing the full flush method.

### Unit tests for finalization fallback

Add tests around `FinalizeIndividualRecording` / `FinalizeTreeRecordingsAfterFlush`
using the existing `FinalizationLiveVesselAccess` seam:

- `FinalizeIndividualRecording_LiveVessel_OverwritesForkStartSegmentPhase`
  - recording begins with `SegmentPhase = "atmo"`.
  - fake live-vessel access determines `TerminalState.Orbiting` and provides a
    classifier result of `exo/Kerbin`.
  - after finalization, `SegmentPhase = "exo"`.
- `FinalizeIndividualRecording_PreExistingTerminalState_StillAppliesEndpointPhase`
  - recording enters with `TerminalStateValue = Orbiting`.
  - verifies the endpoint phase helper runs after the terminal orbit refresh and
    endpoint decision refresh path, not only inside the `!TerminalStateValue.HasValue`
    block.
- `FinalizeTreeRecordingsAfterFlush_ForceStopNoCaptureAtStop_AppliesEndpointPhase`
  - active tree row has no `CaptureAtStop`, stale `SegmentPhase = "atmo"`,
    `TerminalStateValue = Orbiting`, and `TerminalOrbitBody = "Kerbin"`.
  - finalization applies `exo/Kerbin`.
- `FinalizeIndividualRecording_RelativeEndpointWithoutResolvedWorldPosition_PreservesAndLogs`
  - final section is `ReferenceFrame.Relative`.
  - point `latitude`/`longitude`/`altitude` contains anchor-local metre payload.
  - no terminal orbit/position and no relative resolver result are available.
  - existing `SegmentPhase` is preserved and a verbose skip log is emitted.
- `FinalizeIndividualRecording_RelativeEndpointWithResolvedWorldPosition_UsesResolvedAltitude`
  - only if the implementation wires an existing relative/absolute-shadow
    resolver seam.
  - assert classification uses the resolved world/body altitude, not raw
    RELATIVE point fields.
- `FinalizeIndividualRecording_AbsoluteEndpointFallback_MayUsePointAltitude`
  - final section is `ReferenceFrame.Absolute` or legacy no-section data.
  - terminal metadata is unavailable.
  - fallback may use the final point altitude/body.
- `FinalizeIndividualRecording_ChainOwnedTag_Preserved`
  - active row has committed chain identity; endpoint evidence differs.
  - predicate returns false and existing tag remains unchanged.
- `FinalizeIndividualRecording_OptimizerOwnedTag_Preserved`
  - record is not the active row and has an optimizer-split phase from
    `TrackSection.environment`.
  - predicate returns false and existing tag remains unchanged.
- `FinalizeIndividualRecording_NoReliableEndpointEvidence_LeavesExistingPhaseAndLogs`
  - ensures fallback does not invent bad labels.

If `FinalizationLiveVesselAccess` cannot expose enough classification data
without touching Unity `Vessel`, add an injectable classifier delegate to that
seam.

### Regression/integration tests

Add a synthetic save/codec regression only if needed:

- Build a recording row with `terminalState = Orbiting`, terminal orbit metadata,
  and stale `segmentPhase = atmo`.
- Run the finalization helper or tree commit helper and assert `segmentPhase`
  becomes `exo`.

Runtime/in-game test is optional for the first implementation because the
classification and persistence bugs can be covered headlessly. If runtime
validation is requested later, run a Re-Fly from atmo/flying to stable orbit and
verify `parsek-test-results.txt` plus the saved `.sfs` row.

## Documentation Updates for Implementation Commit

When production behavior changes, update in the same commit:

- `CHANGELOG.md`
- `docs/dev/todo-and-known-bugs.md`

Close or rewrite the current open items:

- `Open - SegmentPhase saved value reflects start state, not end state`
- `Open - dead-code SegmentPhase tag block in ParsekFlight.StopRecording`
- `Open - duplicated SegmentPhase classifier in three sites`

Do not update those in this planning-only commit unless the plan itself is
committed separately.

## Risks

- Overwriting optimizer or chain semantic tags would regress split display and
  merge behavior. This is the main risk. Use a named predicate and tests.
- Scene-exit fallback from trajectory-only evidence can misclassify unusual
  destroyed/recovered/boarded endpoints. Start conservative: leave unchanged
  when evidence is not reliable.
- RELATIVE trajectory payloads are not lat/lon/alt. Any fallback that reads raw
  RELATIVE `TrajectoryPoint` fields would recreate the existing v6 relative-frame
  class of bugs. Tests must cover the preserve-and-log path.
- Moving classification too deep into `FlightRecorder` would increase coupling
  to KSP scene state. Prefer applying final endpoint tags in `ParsekFlight`
  finalization where the tree and terminal context are already available.
- Making private flush code public for tests would unnecessarily widen API
  surface. Prefer small internal static helpers.

## Rollback Plan

The implementation should be easy to revert as one behavior commit:

1. Revert classifier extraction call-site changes.
2. Revert capture-to-tree phase copy helper.
3. Revert finalization endpoint phase application helper.

The rollback would return saved labels to current behavior without changing
recording schema or sidecar format. No migration should be required because
`segmentPhase` and `segmentBodyName` already exist in format v12 and older save
rows tolerate either value being absent.

## Decisions for First Patch

1. Fix active unsplit tree leaves only. Allow empty chain identity and explicit
   Re-Fly provisional identity; skip committed chain segments and non-active
   optimizer-owned rows.
2. Do not classify RELATIVE endpoint fallback from raw point fields or stale
   `EndpointBodyName`/`SegmentBodyName`/`StartBodyName`. Use terminal
   orbit/position, TrackSection environment paired with terminal or
   absolute-shadow endpoint evidence, existing relative/absolute-shadow
   resolution, or preserve and log.
3. Run endpoint phase application after terminal orbit refresh and endpoint
   decision refresh in `FinalizeIndividualRecording`, before the final summary
   log, and make it run for pre-existing terminal states.
4. Do not compare `CaptureAtStop.RecordingId` with the tree recording ID.
   Active-row proof comes from `tree.ActiveRecordingId`.
5. Classifier placement remains a small implementation choice. Preferred is a
   new `SegmentPhaseClassifier.cs` with a pure value-based method for tests and
   a vessel adapter for production call sites.
