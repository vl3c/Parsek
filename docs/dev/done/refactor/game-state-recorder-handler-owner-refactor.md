# GameStateRecorder Facility Polling Owner Extraction Plan

Status: facility polling slice implemented and archived. Remaining handler
families are tracked in `docs/dev/plans/refactor-remaining-opportunities.md`.

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-proposal-gamestate-handlers`

## Goal

Move one coherent `GameStateRecorder` handler family into an internal helper/owner with zero behavior changes.

This is slice 1 of a possible broader handler-owner refactor. Contracts, R&D, currency, milestones, and strategies are out of scope for this slice; they are mapped below only to define boundaries.

The refactor must preserve:

- `GameEvents` subscription targets and ordering.
- Emitted `GameStateEvent` fields.
- Suppression guards.
- Recording tags and `Emit` behavior.
- Event source strings.
- Logs and log levels.
- Direct ledger forwarding predicates.
- Emit-before-ledger ordering.

## Ownership Map

`GameStateRecorder` remains the owner of cross-family policy:

- `Subscribe` / `Unsubscribe`.
- `Emit`.
- recording tag resolution.
- `SuppressCrewEvents`, `SuppressResourceEvents`, and `IsReplayingActions`.
- `ResetForTesting`.
- `ShouldForwardDirectLedgerEvent`.

Handler family ownership:

- Contracts: own contract payloads, contract snapshots, and direct KSC ledger forwarding.
- Tech and part purchase: own R&D capture. Keep part-purchase bypass seams compatible with existing tests.
- Funds, science, reputation, and science subjects: move together only. `OnScienceChanged` owns `latestScienceChangeCapture`, consumed by `OnScienceReceived`.
- Progress milestones: keep in `GameStateRecorder` for now. This family is tightly coupled to Harmony static entry points and the pending reward map.
- Strategy lifecycle / KSC actions: second-wave candidate only. Static Harmony/test entry points make this higher risk than facility polling.
- Facility polling: best first slice. This family owns live facility/building cache state, seed/poll behavior, and pure transition helpers.

## Best First Slice

Move only facility polling into an internal owner, e.g. `GameStateFacilityRecorder`.

`GameStateRecorder` should keep forwarding methods with the existing names:

- `SeedFacilityCacheFromCurrentState()` as an instance facade.
- `PollFacilityState()` as an instance facade.
- `CheckFacilityTransitions(...)` as a static facade.
- `CheckBuildingTransitions(...)` as a static facade.

The new owner should own:

- `lastFacilityLevels`.
- `lastBuildingIntact`.
- live facility/building seeding.
- live facility/building polling.
- pure transition helpers.

Important implementation constraint: create one persistent `GameStateFacilityRecorder` instance per `GameStateRecorder`. Do not construct the helper per method call. `SeedFacilityCacheFromCurrentState()` and `PollFacilityState()` must share the same cache dictionaries.

The helper should take the parent `GameStateRecorder` in its constructor and call back into the parent for cross-family policy:

- `Emit(ref evt, ...)`
- `ShouldForwardDirectLedgerEvent(...)`
- `HasLiveRecorder()`

Do not add new static policy seams for this slice. The helper owns only facility-polling state and mechanics; `GameStateRecorder` still owns event emission, recording tags, and ledger-forwarding policy.

The existing call shape must remain unchanged:

- `ParsekScenario` keeps calling `stateRecorder.SeedFacilityCacheFromCurrentState()` before `Subscribe()`.
- `Subscribe()` keeps calling `PollFacilityState()` after resource seeding.
- Tests keep calling `GameStateRecorder.CheckFacilityTransitions(...)` and `GameStateRecorder.CheckBuildingTransitions(...)`.

Keep the live polling loops mechanically intact. Do not rebuild the live path through the pure dictionary helpers, because that risks changing event order, first-seen cache behavior, cache update timing, logs, or the `FacilityUpgraded`-only ledger forwarding gate.

Preserve the existing `"GameStateRecorder"` log subsystem tag for moved facility logs. Do not rename these to `"GameStateFacilityRecorder"`:

- `"Game state: Facility cache seeded from current state ..."`
- `"Game state: {eventType} '{key}' ..."`
- `"Facility poll pass: facilitiesChecked=..."`

No new `ResetForTesting` hook is needed. `GameStateRecorder.ResetForTesting()` is static and does not touch the instance facility caches today; after extraction the helper caches still live on the recorder instance and are discarded with it.

## Do-Not-Move Boundaries

Do not move or alter:

- `Subscribe` / `Unsubscribe` registration lines.
- `Emit`.
- recording tag resolution.
- suppression flags.
- `ShouldForwardDirectLedgerEvent`.
- `OnScienceChanged` without `OnScienceReceived`.
- milestone static APIs unless exact `GameStateRecorder.*` facades remain.
- log strings, log levels, event source strings, or detail formats.
- the `"GameStateRecorder"` log subsystem tag.
- direct ledger forwarding order.

## Validation

Focused validation:

```bash
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~GameStateEventTests|FullyQualifiedName~GameStateEventConverterTests|FullyQualifiedName~DiscardFateTests|FullyQualifiedName~CommittedActionTests|FullyQualifiedName~QuickloadResumeTests|FullyQualifiedName~GameStateStoreExtractedTests|FullyQualifiedName~ResourceBudgetTests"
```

Full validation:

```bash
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
dotnet build Source/Parsek/Parsek.csproj
```

The focused filter intentionally includes `GameStateEventTests`, which pins `CheckFacilityTransitions` and `CheckBuildingTransitions`.

If the local environment blocks full xUnit execution, use the repo fallback:

```bash
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

Record the environment blocker clearly if that fallback is needed.

## Rollback

Before commit:

```bash
git restore Source/Parsek/GameStateRecorder.cs
git restore Source/Parsek/GameStateFacilityRecorder.cs
```

After commit:

```bash
git revert <commit>
```

This slice must not include schema, enum, serialization, or subscription behavior changes, so rollback should not require save migration or cleanup logic.

## Review Result

A separate GPT-5.5 xhigh review agreed that the facility-first boundary is clean after these corrections:

- Add `GameStateEventTests` to targeted validation.
- Require a single persistent facility owner instance per `GameStateRecorder`.
- Keep the live facility/building polling loops verbatim instead of rebuilding them through pure transition helpers.

Additional review tightening:

- Use an explicit parent back-reference for `Emit`, tag/ledger policy, and live-recorder checks.
- Preserve instance facades for live calls and static facades for pure helper tests.
- Do not add reset plumbing for instance facility caches.
- Preserve the `"GameStateRecorder"` subsystem tag on moved logs.

Do not start a second slice until the facility extraction has shipped cleanly with no regressions in `GameStateEventTests` and the full xUnit suite on the target branch.

## Implementation Result

Implemented slice 1 by adding `GameStateFacilityRecorder` and keeping `GameStateRecorder` instance/static facades intact.

Validation run:

```bash
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~GameStateEventTests|FullyQualifiedName~GameStateEventConverterTests|FullyQualifiedName~DiscardFateTests|FullyQualifiedName~CommittedActionTests|FullyQualifiedName~QuickloadResumeTests|FullyQualifiedName~GameStateStoreExtractedTests|FullyQualifiedName~ResourceBudgetTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
dotnet build Source/Parsek/Parsek.csproj
```

Results:

- Focused xUnit: 425 passed.
- Full xUnit: 9679 passed.
- `Parsek.csproj` build: succeeded with 0 warnings and 0 errors.
