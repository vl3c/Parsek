# Remove co-bubble subsystem (v0.10.0)

Status: in progress.
Branch: `remove-co-bubble`.
PR: one PR against `main`.

## Rationale

Co-bubble was the Phase 5 implementation of ghost rendering: shared
float-precision noise between two vessels in the same physics bubble via
per-pair offset traces in `.pann`, with the peer rendered as
`primary_standalone + offset`. After four iterations of scar-tissue fixes
(controlled-child snap, exit crossfade, adjacent-window blend, debris-peer
preference) v0.10.0 fixed the underlying issues at a deeper contract level
(parent-anchored debris `bodyFixedFrames`) and co-bubble was demoted to a
default-off diagnostic toggle.

PR 901 (force-Absolute re-fly toggle) and PR 909 (narrowed-gate re-fly
Relative anchor selection) closed the remaining re-fly anchor questions.
The setting defaults off, no production code path enters its branch when
off, and no public users depend on it (pre-1.0 mod). Retiring the
subsystem entirely. Git log preserves the algorithms if anything ever needs
to be recovered.

## What gets removed

### Production code (full file deletes)

- `Source/Parsek/Rendering/CoBubbleBlender.cs`
- `Source/Parsek/Rendering/CoBubbleOffsetTrace.cs`
- `Source/Parsek/Rendering/CoBubbleOverlapDetector.cs`
- `Source/Parsek/Rendering/CoBubblePrimarySelector.cs`

### Production code (in-file edits)

- `Source/Parsek/ParsekFlight.cs`
  - `GhostPosMode.CoBubble` enum value + the `CoBubble` arm of the
    LateUpdate switch (around line 1624 - 1721).
  - `GhostPosEntry.coBubblePeerRecordingId` / `coBubblePrimaryRecordingId` /
    `coBubblePointUT` fields (around 499 - 502).
  - The Update-path co-bubble blend block (around 20488 - 20620), the
    co-bubble Hit branch in the trace-emit chain (lines 20529 - 20553),
    and the special CoBubble GhostPosEntry registration. The
    `coBubblePeerRecordingId` fall-throughs in `ResolveGhostPosEntryRecordingId`
    and `TraceGhostPositionReapply` come out alongside.
  - `allowCoBubbleBlend` / `allowRenderCoBubbleBlend` static gates.
  - `TryComputeStandaloneWorldPositionForRecording`, its RELATIVE /
    body-fixed / legacy-absolute helper graph, and the per-callsite
    `LogStandaloneParentAnchoredDebrisBodyFixedFailClosed` helper used
    exclusively from those helpers.
  - `s_coBubbleEvalCount` / `s_coBubbleEvalLogLastTicks` and the
    `RecordCoBubbleEvalForLogging` / `ResetCoBubbleEvalLoggingForTesting`
    pair.
  - `LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed`
    and `FormatStandaloneCoverageRange` STAY (used from non-co-bubble
    playback paths in `TryResolvePlaybackWorldPosition`).
- `Source/Parsek/ParsekSettings.cs`
  - `useCoBubbleBlend` property + backing field + the persistence
    callback + `NotifyUseCoBubbleBlendChanged`.
- `Source/Parsek/ParsekSettingsPersistence.cs`
  - `UseCoBubbleBlendKey`, `storedUseCoBubbleBlend`, `RecordUseCoBubbleBlend`,
    the OnLoad / OnSave / ApplyToCurrent integration, the
    `Set/GetStoredUseCoBubbleBlendForTesting` test helpers.
- `Source/Parsek/UI/SettingsWindowUI.cs`
  - The "Use co-bubble peer blending" toggle block.
- `Source/Parsek/PannotationsSidecarBinary.cs`
  - `CoBubbleConfiguration` struct, `MaxCoBubbleTraceEntries`,
    `MaxCoBubbleSamplesPerTrace`, `BytesPerCoBubbleSampleRow` constants.
  - The `CoBubbleOffsetTraces` block in the read schema (Phase 5 block),
    the `CoBubbleOffsetTraces` argument on `Write`, and the Write-side
    serialization.
  - The 4-out / 5-out `TryRead` overloads that expose the
    `coBubbleTraces` out-list (the 2-out and `outlierFlags` overloads
    stay).
  - The `useCoBubbleBlend` byte (offset 52) in `ComputeConfigurationHash`
    and the `useCoBubbleBlend` parameter on the 3-arg / 4-arg / 5-arg
    overloads.
  - `CanonicalEncodingLength` shrinks by 1 byte (86 -> 85), and the
    byte-offset comments shift down by one.
- `Source/Parsek/Rendering/SmoothingPipeline.cs`
  - `s_cachedConfigurationHashCoBubbleFlag` field.
  - `UseCoBubbleBlendResolverForTesting` test seam + `ResolveUseCoBubbleBlend`.
  - `CoBubblePeerResolverForTesting`, `CoBubblePeerSignatureRecomputeForTesting`
    test seams.
  - `s_deferredValidations` set + `DeferredCoBubbleValidation` struct +
    `EnqueueDeferredValidation`, `DeferredCoBubbleValidationsCountForTesting`.
  - `s_deferredCoBubbleRecomputes` dictionary + `EnqueueDeferredCoBubbleRecompute`,
    `DeferredCoBubbleRecomputesCountForTesting`.
  - `RecomputeDeferredCoBubbleTraces`, `RevalidateDeferredCoBubbleTraces`,
    `ResolveOwnerPannPathForDeferredRecompute`.
  - `DetectAndStoreCoBubbleTracesForRecording`, `PersistPeerPannFiles`,
    `PeerPannPathResolverForTesting` test seam.
  - `ClassifyTraceDrift` (three overloads), `ResolvePeerRecording`.
  - The `treeLocalLoadSet` parameter on `LoadOrCompute`, plus the
    inline co-bubble dispatch block in `LoadOrCompute` (the deferred
    enqueue and the inline DetectAndStore+PersistPeerPann calls).
  - The 4-out `TryRead` call in `LoadOrCompute` reduces to the 3-out
    `outlierFlags` overload.
  - The co-bubble detection block inside `PersistAfterCommit`
    (PR P2-A, P3-1 work).
  - The `coBubbleTraces` argument on `TryWritePann`'s `Write` call
    (passes null after).
  - The `Pipeline-CoBubble` Info / Warn / Verbose log lines.
  - `ERS-exempt` file-header comment narrows / drops the co-bubble
    rationale.
- `Source/Parsek/Rendering/SectionAnnotationStore.cs`
  - `CoBubbleTraces` dictionary, `PutCoBubbleTrace`, `TryGetCoBubbleTraces`,
    `RemoveCoBubbleTracesForRecording`, `RemoveCoBubbleTrace`,
    `GetCoBubbleTraceCountForRecording`. Also the `CoBubbleTraces.Remove`
    call in `RemoveRecording` and `ResetForTesting`.
- `Source/Parsek/Rendering/RenderSessionState.cs`
  - `PrimaryByPeerInternal`, `PrimaryRecordingIdsInternal` maps.
  - `CoBubblePrimarySelectionLogged`, `CoBubbleWindowEnterLogged`,
    `CoBubbleWindowExitLogged`, `CoBubbleTraceMissLogged` dedup sets.
  - `TryGetDesignatedPrimary`, `IsPrimary`, `PutPrimaryAssignmentForTesting`,
    `PrimaryByPeerInternalContainsValueLocked`, `PrimaryAssignmentCount`.
  - `NotifyCoBubblePrimarySelection`, `NotifyCoBubbleWindowEnter`,
    `NotifyCoBubbleWindowExit`, `NotifyCoBubbleTraceMiss`.
  - `ResolvePrimaryAssignmentsAndLog`, the call from
    `RunAnchorPropagatorAndResolvePrimaries` (renamed to
    `RunAnchorPropagator` since it no longer drives primary
    resolution).
- `Source/Parsek/Rendering/AnchorCorrection.cs`
  - `AnchorSource.CoBubblePeer = 7` slot. Keep the numeric slot reserved
    with a comment so the persisted byte layout stays stable across the
    cleanup (the value is never written by any producer; the consumer
    in `AnchorPropagator` is also removed).
- `Source/Parsek/Rendering/AnchorPriority.cs`
  - Drop the "Phase 5 territory" remark on rank slot 7; the slot stays
    in the array.
- `Source/Parsek/Rendering/AnchorPropagator.cs`
  - The `cand.Source == AnchorSource.CoBubblePeer` defensive arm in
    `TryResolveSeedEpsilon`. Updates the class docstring's deferred-sources
    list and the dispatch table comment.
- `Source/Parsek/TrajectoryMath.cs`
  - `FrameTransform.LowerOffsetFromInertialToWorld`,
    `FrameTransform.LiftOffsetFromWorldToInertial`.
- `Source/Parsek/ParsekScenario.cs`
  - The two post-tree-hydration `RecomputeDeferredCoBubbleTraces` /
    `RevalidateDeferredCoBubbleTraces` invocation blocks (around
    3560-3576 and 3945-3959).
- `Source/Parsek/RecordingSidecarStore.cs`
  - The `treeLocalLoadSet` parameter on `LoadRecordingFiles` (two
    overloads collapse to one), `LoadRecordingFilesFromPathsInternal`,
    and the `LoadRecordingFilesFromPathsForTesting` shim. The two
    `LoadOrCompute` call sites drop the argument.
- `Source/Parsek/RecordingStore.cs`
  - The `treeLocalLoadSet`-bearing `LoadRecordingFiles` overload
    collapses into the single-arg form.
- `Source/Parsek/ParsekConfig.cs`
  - The "co-bubble/chain rendering" mention in the
    `InitialVisibleFrameClampWindowSeconds` docstring drops the
    "co-bubble" word.
- `Source/Parsek/InGameTests/RuntimeTests.cs`
  - The `Pipeline_CoBubble_Live` and `Pipeline_CoBubble_GhostGhost`
    in-game tests + their `#region Pipeline-CoBubble` band.
  - The `useCoBubbleBlend` skip-gate at the top of
    `RecorderRelative_RefliedSibling_PicksSameTreeAnchor` (the test
    no longer needs to skip).

### Tests (full file deletes)

- `Source/Parsek.Tests/Rendering/CoBubbleBlenderTests.cs`
- `Source/Parsek.Tests/Rendering/CoBubbleOverlapDetectorTests.cs`
- `Source/Parsek.Tests/Rendering/CoBubbleSidecarRoundTripTests.cs`
- `Source/Parsek.Tests/Rendering/CoBubbleStandalonePrimaryTests.cs`
- `Source/Parsek.Tests/Rendering/UseCoBubbleBlendSettingTests.cs`

### Tests (in-file edits)

- `Source/Parsek.Tests/Rendering/AnchorCorrectionConsumerHookTests.cs`
  - `AllowRenderCoBubbleBlend_Suppressed_SuppressesBlend` test method.
- `Source/Parsek.Tests/Rendering/SmoothingPipelineTests.cs`
  - The `LoadOrCompute_DriftedAlgStamp_RecomputesCoBubbleTraces` test
    method.
  - Any test that uses `CoBubbleOverlapDetector.*ForTesting` seams,
    `SmoothingPipeline.UseCoBubbleBlendResolverForTesting`,
    `DeferredCoBubbleRecomputesCountForTesting`,
    `RecomputeDeferredCoBubbleTraces`,
    `SectionAnnotationStore.TryGetCoBubbleTraces`,
    `Pipeline-CoBubble` log assertions, or the 4-out
    `TryRead` overload exposing `List<CoBubbleOffsetTrace>`: delete
    or rewrite as no-co-bubble where the test was multi-purpose.
- `Source/Parsek.Tests/Rendering/PannotationsSidecarRoundTripTests.cs`
  - Drop `coBubbleCount` mentions in the truncated-payload offset
    comments (no test logic actually exercises the co-bubble block).
- `Source/Parsek.Tests/Rendering/RenderSessionStateTests.cs`
  - Trim the "co-bubble path" comment on the naive-relative test.
- `Source/Parsek.Tests/Rendering/OutlierFlagsSidecarRoundTripTests.cs`
  - Drop the `coBubbleTraces: null` keyword arguments (the
    `Write(...)` signature no longer has that parameter).
- `Source/Parsek.Tests/Rendering/UseOutlierRejectionSettingTests.cs`
  - Drop the `useCoBubbleBlend:` keyword arguments on
    `ComputeConfigurationHash` calls (the parameter is gone from the
    overload).
- `Source/Parsek.Tests/Harness/HarnessScenarios.cs`
  - Replace the comment "the correct visual co-bubble" with "the
    correct visual composition".

### Docs

- Archive: `docs/dev/plans/phase5-cobubble-blend.md` deleted in this
  PR. Git log preserves the design doc.
- Trim: `docs/parsek-ghost-trajectory-rendering-design.md` removes the
  Stage 5 / Phase 5 co-bubble sections (search the file for
  "Co-Bubble", "co-bubble", "Phase 5" and delete the co-bubble-specific
  passages; keep adjacent text on smoothing, anchors, terrain).
- `docs/dev/todo-and-known-bugs.md`: strike-through any open co-bubble
  bugs / TODOs with a "removed in v0.10.0 co-bubble retirement" note;
  do not move them.
- `docs/dev/done/todo-and-known-bugs-v5.md`: leave as-is (historical
  archive).
- `docs/roadmap.md`: leave the surviving mention if it's purely
  historical, drop any "Phase 5" referenced as in-flight.
- `docs/dev/plans/bubble-entry-exit-anchor.md`,
  `docs/dev/plans/debris-frame-contract-v13.md`,
  `docs/dev/plans/extend-parent-anchored-contract-to-controlled-children.md`,
  `docs/dev/plans/fix-debris-relative-tail-retirement.md`,
  `docs/dev/plans/force-absolute-refly-provisional.md`,
  `docs/dev/plans/ghost-anchor-recording-chain-plan.md`,
  `docs/dev/plans/merge-boundary-discontinuity-math-fix-plan.md`,
  `docs/dev/plans/phase6-anchor-taxonomy.md`,
  `docs/dev/plans/phase8-outlier-rejection.md`,
  `docs/dev/plans/pr708-playtest-followup-plan.md`,
  `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md`,
  `docs/dev/plans/recording-rendering-v0-reset-checklist.md`,
  `docs/dev/plans/refly-cleanup-and-v0-reset.md`,
  `docs/dev/plans/tumbling-parent-rot-interp-fix-plan.md`,
  `docs/dev/manual-testing/extend-parent-anchored-contract-to-controlled-children.md`:
  these are historical / done plan docs that reference co-bubble
  passingly. Leave them as-is. Git log + the in-repo CHANGELOG is the
  way to find the algorithms.

### Allowlist

- `scripts/ers-els-audit-allowlist.txt`: drop the `CoBubbleBlender.cs`
  entry and the Phase 5 co-bubble paragraphs that justify the
  `SmoothingPipeline.cs` entry. `SmoothingPipeline.cs` itself stays in
  the allowlist iff it still has other ERS-exempt read paths. After
  the deletion the only remaining `RecordingStore.CommittedRecordings`
  reads in SmoothingPipeline.cs are... none. Confirm and either drop
  the entry or keep the file allowlisted with a non-co-bubble
  rationale.

### CHANGELOG

- One v0.10.0 entry, one line, under 2 sentences, user-facing only.
  Match the verbose-paragraph style of the existing v0.10.0 entries.

## Schema impact

- `.pann` ConfigurationHash drops the `useCoBubbleBlend` byte at offset
  52, shifting `CanonicalEncodingLength` from 86 -> 85 bytes. Every
  existing `.pann` carries a now-stale hash; on next load the
  `config-hash-drift` path invalidates them and the pipeline recomputes.
  HR-10 covers this exactly.
- `.pann` schema bumps `AlgorithmStampVersion` is intentionally NOT
  bumped: the canonical hash change already drives the load-time
  invalidation, and there is no point burning an alg-stamp version on
  a deletion that the hash drift cleans up for free.
- `.pann` write schema drops the `CoBubbleOffsetTraces` block. Existing
  `.pann` files that contain a populated block will fail
  `config-hash-drift` first and never reach the block-read step.
- `.prec` schema unchanged. Co-bubble lives entirely in `.pann`; the
  `.prec` trajectory file has no co-bubble fields. `RecordingStore.CurrentRecordingSchemaGeneration`
  stays at 2.
- `AnchorSource.CoBubblePeer = 7` enum slot is kept as a reserved value
  with a comment marking it deprecated. Removing the slot would shift
  subsequent values' ordinals; AnchorCandidate uses `(byte)Source` for
  on-disk persistence, so preserving the byte layout avoids cascading
  invalidation.

## Test count delta

Co-bubble file deletions (approximate, by file LoC):

- CoBubbleBlenderTests.cs: ~1204 lines
- CoBubbleOverlapDetectorTests.cs: ~640 lines
- CoBubbleSidecarRoundTripTests.cs: ~922 lines
- CoBubbleStandalonePrimaryTests.cs: ~665 lines
- UseCoBubbleBlendSettingTests.cs: ~108 lines

Plus a handful of co-bubble test methods removed from
`SmoothingPipelineTests.cs` and `AnchorCorrectionConsumerHookTests.cs`.

## What stays (NOT co-bubble)

- Anchor taxonomy (`AnchorSource` 0-6, 8-9; `AnchorPropagator`).
- Parent-anchored debris `bodyFixedFrames` contract.
- `RelativeAnchorResolver` and recorded-anchor RELATIVE replay.
- `forceAbsoluteForReFlyProvisional` setting (PR 901 rollback toggle,
  retained for one release per the original plan).
- `LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed` and
  `FormatStandaloneCoverageRange` in `ParsekFlight.cs` (used from the
  playback world-position path).
