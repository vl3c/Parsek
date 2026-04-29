# Remaining Refactor Opportunities

Date: 2026-04-29

Status: active reference after the Refactor-4 archive. This is not approval to
move production code. Every item below still needs a focused proposal, explicit
scope, and review before implementation.

Refactor-4 completed the behavior-neutral owner splits that were ready to land:
storage and sidecar owners, tree record codec extraction, KSC action
classifier/reconciler extraction, post-walk reconciliation extraction,
LedgerOrchestrator load migration, recovery/rollout helpers,
FacilityStatePatcher, GameStateFacilityRecorder, RecordingGroupStore, and
RecordingsTableFormatters. The completed planning docs are archived under
`docs/dev/done/refactor/`.

The remaining opportunities below are structural only. They should preserve
serialized formats, log text and tags, event ordering, mutation ordering,
reflection behavior, public/internal compatibility facades, and existing test
surfaces unless a separate behavior-changing design is approved first.

## Best Orthogonal Candidates

### KspStatePatcher State Families

Done: `FacilityStatePatcher`.

Remaining candidates:

- Science, per-subject science, and R&D unlock helpers.
- Funds and reputation patch helpers.
- Milestones/progress patch helpers.
- Contract patch helpers.

Why it remains: `KspStatePatcher.PatchAll` still owns several distinct state
families after the facilities split. Each family has clear method clusters, but
the patch order and suppression scope are behavior-critical.

Proposal requirements:

- Preserve the exact `PatchAll` order and `SuppressionGuard.ResourcesAndReplay`
  scope.
- Keep `KspStatePatcher` as the compatibility facade for the first slice.
- List every reflection and UI mutation dependency for the chosen family.
- Validate with focused patcher tests plus the full non-injection xUnit gate.

### GameStateRecorder Handler Families

Done: `GameStateFacilityRecorder`.

Remaining candidates:

- Contract event handlers.
- Tech research, part purchase, and science subject handlers.
- Funds, science, and reputation event handlers.
- Progress/milestone handlers.
- Strategy and KSC action handlers.

Why it remains: `GameStateRecorder` still contains multiple event-handler
families with subscription, suppression, emit, and ledger-forwarding rules. A
handler-family extraction can be low risk only if it is scoped around one
subscription/emit cluster.

Proposal requirements:

- Map `GameEvents` subscriptions and unsubscriptions before moving code.
- Preserve emitted `GameStateEvent` fields, event source strings, and
  emit-before-ledger ordering.
- Keep existing suppression guard usage and log text stable.
- Validate with focused recorder/ledger tests and the full non-injection xUnit
  gate.

### RecordingStore Orchestration

Done: sidecar commit/store/codecs, manifest codec, tree record codec, and
`RecordingGroupStore`.

Remaining candidates:

- Optimizer orchestration and commit-time optimizer plumbing.
- Deletion and cleanup orchestration.
- Rewind/recording finalization orchestration.
- State-version and timeline notification ownership.

Why it remains: `RecordingStore` has fewer storage responsibilities than before,
but still mixes orchestration, lifecycle, and UI-facing compatibility state.
These slices should avoid active unfinished-flight or recording-tree work unless
that work has merged and the proposal is rebased.

Proposal requirements:

- Do not change recording IDs, sidecar paths, file formats, deletion semantics,
  rewind behavior, or UI-facing contracts.
- Keep `RecordingStore` compatibility wrappers in the first implementation
  slice.
- Include a rollback plan for any moved static state.
- Validate with focused recording-store/tree tests and the full non-injection
  xUnit gate.

### RecordingsTableUI Row And Tree Surfaces

Done: `RecordingsTableFormatters`.

Remaining candidates:

- Row rendering for recording entries.
- Group tree rendering.
- Unfinished Flights group rendering and action cells.
- Recording block/chain rendering.
- Loop period editing cell.

Why it remains: the formatter split lowered the risk for future UI work, but
the table still has broad shared IMGUI state and callback coupling. This area is
not a good parallel target while unfinished-flight UI changes are active.

Proposal requirements:

- Start with a field and callback ownership map.
- Keep all visible labels, tooltips, sort behavior, selection behavior, and
  button enablement stable.
- Prefer a render-helper owner before moving stateful editor/session logic.
- Validate with focused formatter/table tests where possible, then run the full
  non-injection xUnit gate. Runtime visual review is required for visible UI
  changes.

## Lower-Priority Or Coupled Candidates

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

Why it is lower priority: the highest-value low-risk LedgerOrchestrator slices
have already landed. Further splits risk crossing ledger mutation order,
resource/currency ordering, patch timing, and logging policy boundaries.

### Runtime, Ghost, And Rendering Owners

Candidates:

- `GhostVisualBuilder`
- `GhostPlaybackEngine`
- `GhostMapPresence`
- `ParsekKSC`
- `EngineFxBuilder`
- `VesselGhoster`

Why it is coupled: these files touch runtime-only KSP behavior, rendering, map
presence, and ghost trajectory work. They should be deferred while ghost
trajectory/rendering branches are active.

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
- Finalization producer/applier boundaries.
- `ParsekFlight` finalization and checkpoint helpers.

Why it is coupled: these paths interact with unfinished-flight behavior, rewind
point routing, save/load lifecycle, and scene transitions. They need checkpoint
ownership and rollback planning before code moves.

## Cross-Cutting Follow-Ups

- Build a static mutable state map for `GameStateRecorder`,
  `LedgerOrchestrator`, `RecordingStore`, `ParsekScenario`,
  `WatchModeController`, and `GhostPlaybackEngine`.
- Audit magic thresholds, literal reason keys, and rate-limit keys after
  `ParsekConfig` centralization.
- Identify any remaining compatibility facades that can be removed only after
  source and reflection call sites move.

## Suggested Next Proposal

The lowest-overlap next proposal is a remaining `KspStatePatcher` state-family
map or a `GameStateRecorder` handler-family map, depending on which nearby
branches are active. Avoid `RecordingStore`, `RecordingsTableUI`, ghost
rendering, and rewind/finalization slices while unfinished-flight or ghost
trajectory work is still moving.
