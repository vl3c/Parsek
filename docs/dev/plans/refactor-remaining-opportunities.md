# Remaining Refactor Opportunities

Date: 2026-04-30

Status: active reference after the Refactor-4 archive and the merged
post-archive cleanup work. This is not approval to move production code. Every
item below still needs a focused proposal, explicit scope, and review before
implementation.

This document is intentionally a short live inventory. Completed design and
implementation notes are archived under `docs/dev/done/refactor/`; do not
re-open those slices unless a new behavior-changing design explicitly requires
it.

## Already Landed

The previous inventory listed several areas that are now complete or covered by
new owner seams. Treat these as closed for generic refactor planning:

- Recording storage and sidecar ownership: `SidecarFileCommitBatch`,
  `RecordingSidecarStore`, `TrajectoryTextSidecarCodec`,
  `RecordingManifestCodec`, `RecordingTreeRecordCodec`, and
  `SnapshotSidecarCodec` own the current sidecar/codecs surface behind
  compatibility wrappers.
- Recording group ownership: `RecordingGroupStore` owns auto-generated tree
  groups, standalone auto-assignment, group naming, and group mutation helpers.
- Ledger decomposition: `KscActionExpectationClassifier`,
  `KscActionReconciler`, `PostWalkActionReconciler`, `LedgerLoadMigration`,
  `LedgerRecoveryFundsPairing`, and `LedgerRolloutAdoption` cover the
  high-value low-risk LedgerOrchestrator slices.
- KSP state / game-state first slices: `FacilityStatePatcher` and
  `GameStateFacilityRecorder` own the facility patching and facility polling
  slices. Other state and event families remain listed below only when a crisp
  proposal is still useful.
- Thin UI presentation seams: `TestRunnerPresentation`,
  `SettingsWindowPresentation`, `SpawnControlPresentation`,
  `GroupPickerPresentation`, `RecordingsTableFormatters`, and
  `UnfinishedFlightsGroup` cover the pure decision/formatting seams that were
  safe to extract without moving broad IMGUI state.
- Recording finalization support: `RecordingFinalizationCache`,
  `RecordingFinalizationCacheProducer`, `RecordingFinalizationCacheApplier`,
  and `IncompleteBallisticSceneExitFinalizer` now own the reliability hooks
  that used to make finalization look like one generic extraction target.
- Runtime builder seams: `EngineFxBuilder` and `GhostVisualBuilder` now have
  explicit headless ownership-style coverage for important builder decisions.
  Further runtime/rendering movement is still coupled and remains below.

## Best Remaining Candidates

### KspStatePatcher Non-Facility Families

Done: `FacilityStatePatcher`.

Remaining candidates:

- Science, per-subject science, and R&D unlock helpers.
- Funds and reputation patch helpers.
- Milestones/progress patch helpers.
- Contract patch helpers.

Why it remains: `KspStatePatcher.PatchAll` still owns several state families
after the facilities split. The remaining methods are coherent clusters, but
the patch order, suppression scope, singleton readiness probes, and exact log
text are behavior-critical.

Proposal requirements:

- Preserve the exact `PatchAll` order and `SuppressionGuard.ResourcesAndReplay`
  scope.
- Keep `KspStatePatcher` as the compatibility facade for the first slice.
- List every reflection and UI mutation dependency for the chosen family.
- Validate with focused patcher tests plus the full non-injection xUnit gate.

### GameStateRecorder Non-Facility Handler Families

Done: `GameStateFacilityRecorder`.

Remaining candidates:

- Contract event handlers.
- Tech research, part purchase, and science subject handlers.
- Funds, science, and reputation event handlers.
- Progress/milestone handlers.
- Strategy and KSC action handlers.

Why it remains: `GameStateRecorder` still owns multiple event-handler families
with subscription, suppression, emit, and ledger-forwarding rules. A handler
family extraction can be low risk only if it is scoped around one
subscription/emit cluster.

Proposal requirements:

- Map `GameEvents` subscriptions and unsubscriptions before moving code.
- Preserve emitted `GameStateEvent` fields, event source strings, and
  emit-before-ledger ordering.
- Keep existing suppression guard usage and log text stable.
- Validate with focused recorder/ledger tests and the full non-injection xUnit
  gate.

### RecordingStore Remaining Orchestration

Done: sidecar commit/store/codecs, manifest codec, tree record codec, and
`RecordingGroupStore`.

Remaining candidates:

- Optimizer orchestration and commit-time optimizer plumbing.
- Deletion and cleanup orchestration.
- Rewind/load invocation helpers that still live behind `RecordingStore`.
- State-version and timeline notification ownership.

Why it remains: `RecordingStore` has fewer storage responsibilities than before,
but still mixes timeline hub state, lifecycle orchestration, and UI-facing
compatibility facades. These slices should avoid active Re-Fly, unfinished-flight,
or Phase 13 logistics work unless that work has merged and the proposal is
rebased.

Proposal requirements:

- Do not change recording IDs, sidecar paths, file formats, deletion semantics,
  rewind behavior, or UI-facing contracts.
- Keep `RecordingStore` compatibility wrappers in the first implementation
  slice.
- Include a rollback plan for any moved static state.
- Validate with focused recording-store/tree tests and the full non-injection
  xUnit gate.

## Coupled Or Lower-Priority Candidates

### RecordingsTableUI Stateful Surfaces

Done: `RecordingsTableFormatters` and `UnfinishedFlightsGroup`.

Remaining candidates:

- Row rendering for recording entries.
- Group tree rendering.
- Recording block/chain rendering.
- Loop period editing cell and edit-focus state.

Why it is coupled: the table still has broad shared IMGUI state and callback
coupling. The easy pure helpers have already moved; future work needs a field
and callback ownership map before touching code.

### LedgerOrchestrator Residual Bands

Done: KSC classifier/reconciler, post-walk reconciler, load migration, recovery
funds pairing, rollout adoption, and related facades.

Remaining candidates only if a crisp owner emerges:

- Commit-window earnings reconciliation bands.
- Vessel-cost action creation helpers.
- Recalculation and KSP patch orchestration.
- Science reconciliation helpers shared across commit-window and post-walk
  paths.
- Tree-commit notification helpers.

Why it is lower priority: the highest-value low-risk `LedgerOrchestrator`
slices have already landed. Further splits risk crossing ledger mutation order,
resource/currency ordering, patch timing, and logging policy boundaries.

### Runtime, Ghost, And Rendering Owners

Candidates:

- `GhostPlaybackEngine`
- `GhostMapPresence`
- `ParsekKSC`
- `VesselGhoster`
- deeper `EngineFxBuilder` / `GhostVisualBuilder` behavior movement beyond the
  current test seams

Why it is coupled: these files touch runtime-only KSP behavior, rendering, map
presence, and ghost trajectory work. They should be deferred while ghost
trajectory/rendering or Re-Fly hardening branches are active.

Proposal requirements:

- Include in-game validation, not only xUnit.
- Preserve log text/tags and per-frame rate-limit keys.
- Preserve visual asset contracts, PID lookup behavior, and spawn/despawn
  ordering.

### Math, Serialization, And Optimizer Owners

Candidates:

- `BallisticExtrapolator`
- `RecordingOptimizer`
- `TrajectorySidecarBinary`
- Branch-point serialization ownership after `RecordingTreeRecordCodec`
- Binary/text sidecar format unification

Why it is coupled: these areas are math- or format-sensitive. Some are no longer
pure zero-logic refactors unless the proposal proves byte-for-byte payload and
ordering equivalence. Binary/text sidecar unification is explicitly deferred
until a schema design is approved.

### Rewind And Lifecycle Invocation Owners

Candidates:

- Rewind invocation helpers.
- Scenario lifecycle hooks in `ParsekScenario`.
- Remaining finalization boundary cleanup after the cache producer/applier split.
- `ParsekFlight` finalization and checkpoint helpers.

Why it is coupled: these paths interact with unfinished-flight behavior, Rewind
Point routing, save/load lifecycle, scene transitions, and active Re-Fly
session state. They need checkpoint ownership and rollback planning before code
moves.

## Cross-Cutting Follow-Ups

- Build a static mutable state map for `GameStateRecorder`,
  `LedgerOrchestrator`, `RecordingStore`, `ParsekScenario`,
  `WatchModeController`, and `GhostPlaybackEngine`.
- Audit magic thresholds, literal reason keys, and rate-limit keys after
  `ParsekConfig` centralization.
- Identify compatibility facades that can be removed only after source and
  reflection call sites move.

## Suggested Next Proposal

The lowest-overlap next proposal is a non-facility `KspStatePatcher` state
family map or a non-facility `GameStateRecorder` handler-family map. Avoid
`RecordingStore`, `RecordingsTableUI`, ghost rendering, and rewind/finalization
slices while Phase 13 logistics prerequisites or Re-Fly follow-ups are moving.
