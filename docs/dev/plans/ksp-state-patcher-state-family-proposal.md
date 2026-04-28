# KspStatePatcher State-Family Refactor Proposal

Date: 2026-04-28
Branch/worktree: `proposal-ksp-state-patcher` from `origin/main` at `afdb70466806875edf81bfe77229af6d748305ee`

## Goal

Investigate whether one KSP state-family patcher can be extracted from `Source/Parsek/GameActions/KspStatePatcher.cs` as a zero-logic refactor.

Hard boundary for any future implementation: preserve patch order, KSP reflection behavior, UI mutation timing, affordability checks, log text/tag, and public/internal call signatures.

## Current Patch Order And Entry Points

`LedgerOrchestrator.ApplyRecalculatedStateToKsp` applies kerbals first, then calls `KspStatePatcher.PatchAll`:

1. `kerbalsModule.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster)`
2. `KspStatePatcher.PatchAll(...)`
3. inside `PatchAll`, under `SuppressionGuard.ResourcesAndReplay()`:
   1. `PatchScience`
   2. `PatchTechTree`
   3. `PatchFunds`
   4. `PatchReputation`
   5. `PatchFacilities`
   6. `PatchMilestones`
   7. `PatchContracts`
   8. stable `PatchAll complete` log

There is also one direct facilities-only caller in `ParsekFlight` for warp-time facility state refresh. Any extraction must keep `KspStatePatcher.PatchFacilities(FacilitiesModule)` as the stable call surface.

## Family Map

### Science

Methods:

- `PatchScience`
- `AdjustSciencePatchTargetForPendingRecentTechResearch`
- `PatchPerSubjectScience`
- tech-tree helpers live nearby but are a separate R&D unlock family

Behavior shape:

- null module warns with exact `KspStatePatcher` tag
- null `ResearchAndDevelopment.Instance` stable-verbose skips
- missing seed stable-verbose skips to preserve live KSP values
- computes target from `ScienceModule.GetAvailableScience()`, then adjusts for pending recent tech research
- applies delta through `ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.None)`
- logs suspicious drawdown WARN for large negative deltas
- always patches per-subject science after balance patch when the singleton and seed path continues

Extraction risk:

- Medium. It looks scalar, but it has extra R&D archive side effects and the pending-tech holdback. A generic scalar patcher would either special-case science heavily or risk changing the sequence around per-subject mutation.

### Funds

Methods:

- `PatchFunds`

Behavior shape:

- null module warns
- null `Funding.Instance` stable-verbose skips
- missing seed stable-verbose skips
- computes `target - current`
- no-op threshold is `< 0.01`
- warns on suspicious drawdown
- applies delta through `Funding.Instance.AddFunds(delta, TransactionReasons.None)`
- logs exact before/after/delta/target text

Extraction risk:

- Low in isolation, medium if paired with science/reputation in a generic helper. Funds is simple, but preserving exact log text and drawdown semantics means a helper would carry many string and formatter parameters for little immediate payoff.

### Reputation

Methods:

- `PatchReputation`

Behavior shape:

- null module warns
- null `Reputation.Instance` stable-verbose skips
- missing seed stable-verbose skips
- no-op threshold is `< 0.01f`
- applies absolute target through `Reputation.Instance.SetReputation(targetRep, TransactionReasons.None)`, not delta, because the module already applied the stock curve
- no suspicious drawdown warning
- `F2` formatting instead of `F1`

Extraction risk:

- Low in isolation, medium in a scalar generic helper. It shares the outer guard/null/seed/no-op pattern with funds/science but intentionally differs in write API, delta semantics, warning behavior, and formatting.

### Facilities

Methods:

- `PatchFacilities`
- `PatchDestructionState`

Behavior shape:

- null module warns
- null `ScenarioUpgradeableFacilities.protoUpgradeables` stable-verbose skips
- level patch iterates `FacilitiesModule.GetAllFacilities()`
- mutates `UpgradeableFacility.SetLevel(targetLevel)`
- emits per-facility verbose level logs only on changes
- emits INFO summary only when `patched + notFound > 0`
- emits rate-limited verbose for skipped-only steady state or empty state
- changed facility verbose log shape is exactly `PatchFacilities: '{facilityId}' level {currentLevel} -> {targetLevel} (destroyed={state.Destroyed})`
- always calls `PatchDestructionState(allFacilities)` after level summary when the proto dictionary path is available
- destruction patch optionally skips Unity calls for tests, otherwise calls `UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>()`, then `Demolish()` / `Repair()`, then INFO summary

Extraction risk:

- Lowest for a state-family extraction if done as a move-only wrapper. Facilities is already cohesive, has one public/internal entry method plus one helper, and does not share private state with the rest of `KspStatePatcher` beyond `Tag`, `IC`, `VerboseStablePatchState`, and `SuppressUnityCallsForTesting`.

### Tech Tree

Methods:

- `BuildTargetTechIdsForPatch`
- `GetSelectedTechBaselineUt`
- `PatchTechTree`
- private proto-node reflection and UI refresh helpers

Behavior shape:

- target-set construction preserves affordability checks by adding only affordable `ScienceSpending` actions at or before the cutoff
- patching is rewind-gated by the caller passing `targetTechIds`; null target is the normal no-op path for non-cutoff recalculations
- mutates both `ResearchAndDevelopment.Instance` proto tech state and static `AssetBase.RnDTechTree` node state
- uses reflection for `ResearchAndDevelopment.protoTechNodes`
- refreshes the R&D tech-tree UI after state mutation

Extraction risk:

- Not a slice-1 candidate. It mixes affordability filtering, KSP reflection, static tech-tree mutation, and UI refresh timing. A move-only extraction is possible later, but it should be isolated from facilities and covered by tech-tree rewind tests.

### Milestones

Methods:

- `PatchMilestones`
- `PatchProgressNodeTree`
- `PatchRepeatableRecordNode`
- repeatable-record helpers

Behavior shape:

- uses `ProgressTracking.Instance.achievementTree`
- patches `ProgressNode` reached/complete state via reflection instead of firing stock completion paths
- repeatable Records* nodes have a special path for stock record/reward fields
- `SuppressUnityCallsForTesting` affects repeatable-record type detection and stock max-record lookup in headless tests
- logs credited/unreached/skipped summary under `KspStatePatcher`

Extraction risk:

- Not a slice-1 candidate. This family is reflection-heavy and shares `SuppressUnityCallsForTesting`, so moving it with facilities would broaden the risk surface and weaken the "do not move shared suppression" boundary.

### Contracts

Methods:

- `PatchContracts`
- `PartitionContractsForPatch`

Behavior shape:

- preserves non-Active contracts and the append-only finished/history buckets
- unregisters stale Active contracts only after partitioning current contracts against ledger active IDs
- restores missing active contracts from cloned `ConfigNode` snapshots because `Contract.Load` mutates its input
- re-registers restored contracts and fires `GameEvents.Contract.onContractsLoaded.Fire()` for UI refresh

Extraction risk:

- Not a slice-1 candidate. This family has KSP contract lifecycle side effects, ConfigNode mutation hazards, and UI/event timing. It is a better later extraction than a generic scalar helper, but should be reviewed separately.

### Kerbals

Methods:

- not in `KspStatePatcher`
- `KerbalsModule.ApplyToRoster(KerbalRoster)`
- `KerbalsModule.ApplyToRoster(IKerbalRosterFacade)`

Behavior shape:

- called before `PatchAll`, outside `SuppressionGuard.ResourcesAndReplay()`
- uses `SuppressionGuard.Crew()`, not resource/replay suppression
- owns a roster facade for testability
- mutates stand-ins, deleted unused stand-ins, retired stand-ins, and the crew replacement bridge
- includes rescue-completion guard behavior with pid-scoped marker checks
- logs under `KerbalsModule`, not `KspStatePatcher`

Extraction risk:

- Do not fold into a `KspStatePatcher` family extraction. It is already extracted, has a different suppression domain, and has extensive tests around `ApplyToRoster` semantics. The only safe refactor here is orchestration naming/documentation, not code movement.

## Recommendation

Extract the facilities state family first.

Do not start with a generic funds/science/reputation scalar patcher. The visual similarity is real, but the differences are exactly the fragile parts: science's pending-tech holdback and per-subject archive mutation, reputation's absolute setter and curve contract, funds/science suspicious drawdown logging, and different no-op/format thresholds. A generic helper would be a logic-risking abstraction disguised as mechanical cleanup.

The facilities extraction can be zero-logic if implemented as a pure move:

- add `Source/Parsek/GameActions/FacilityStatePatcher.cs`
- move `PatchFacilities` and `PatchDestructionState` bodies into that class unchanged
- preserve `KspStatePatcher.PatchFacilities(FacilitiesModule)` as a wrapper with the same internal signature
- preserve `KspStatePatcher.PatchDestructionState(IReadOnlyDictionary<string, FacilitiesModule.FacilityState>)` unconditionally as a wrapper with the same internal signature
- keep log tag text as `KspStatePatcher`, either by passing the tag or by defining the same literal in the extracted class
- duplicate the current `VerboseStablePatchState` helper privately inside `FacilityStatePatcher`, with the same `KspStatePatcher` tag literal, so stable verbose calls preserve the exact identity/state/message values
- keep `KspStatePatcher.SuppressUnityCallsForTesting` in place; `FacilityStatePatcher` should read the existing flag because it is shared by non-facility milestone/repeatable-record test paths
- note that current greps find no direct `KspStatePatcher.PatchDestructionState` callers outside `PatchFacilities`, including in `Source/Parsek.Tests`, but the wrapper is still mandatory because the method is currently an internal static call signature

## Safest First Slice

Slice 1 should be facilities-only, wrapper-preserving, and move-only:

1. Create `FacilityStatePatcher` as `internal static`.
2. Copy the exact current `PatchFacilities` and `PatchDestructionState` logic into it, plus a private `VerboseStablePatchState` helper that calls `ParsekLog.VerboseOnChange("KspStatePatcher", identity, stateKey, message)`.
3. Replace `KspStatePatcher.PatchFacilities(...)` with a one-line wrapper that delegates.
4. Replace `KspStatePatcher.PatchDestructionState(...)` with a one-line wrapper that delegates.
5. Keep `PatchAll` order unchanged: the wrapper call remains in the same place.
6. Keep the direct `ParsekFlight` call unchanged: it still calls `KspStatePatcher.PatchFacilities(...)`.

Do not extract funds/science/reputation in the same commit. Do not move tech-tree, milestones, contracts, or kerbals in this slice.

## Risks

- Log identity drift: moving code can accidentally change `Tag`, message text, rate-limit keys, or stable-verbose identity/state pairs. This would break log contracts and make playtest diffs noisy.
- Per-facility verbose drift: preserve the exact changed-level message shape `PatchFacilities: '{facilityId}' level {currentLevel} -> {targetLevel} (destroyed={state.Destroyed})`.
- Test suppression drift: `SuppressUnityCallsForTesting` must continue to suppress `FindObjectsOfType<DestructibleBuilding>()` in headless xUnit.
- Shared suppression drift: `SuppressUnityCallsForTesting` also gates non-facility milestone/repeatable-record paths, so a facilities-only extraction must not move or rename that member.
- Wrapper omission: `ParsekFlight` and tests currently call `KspStatePatcher.PatchFacilities`; changing that signature broadens the refactor and violates the boundary.
- Internal signature drift: `KspStatePatcher.PatchDestructionState(...)` is an internal static surface today and must remain available even if current callers are sparse.
- Summary timing drift: `PatchDestructionState` must still run after the facility-level summary on the same paths where it runs today.
- INFO spam regression: bug #596 fixed skipped-only/empty facility summaries. Any extraction must preserve the exact gate and rate-limited verbose keys.
- Namespace/file placement churn: the class should stay under `namespace Parsek` and `Source/Parsek/GameActions/` to avoid project/include surprises.

## Tests For Future Implementation

Minimum headless validation:

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~KspStatePatcherTests`
- include the existing facilities tests:
  - `PatchFacilities_NullModule_LogsWarning`
  - `PatchFacilities_EmptyProtoUpgradeables_CompletesWithoutCrash`
  - `PatchFacilities_NotFound_LogsInfoSummary`
  - `PatchFacilities_Empty_DoesNotLogInfo_UsesRateLimitedVerbose`
- include `PatchAll_SetsSuppressFlagsAndRestores` and `PatchAll_RepeatedSandboxNoop_LogsStableSkipsOnce` to pin orchestration wrappers and stable skip logging

Broader focused validation:

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~KspStatePatcherTests|FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~VesselCostRecoveryRegressionTests|FullyQualifiedName~Bug445RolloutCostLeakTests"`

Runtime validation if facility behavior changes unexpectedly:

- KSC scene facility upgrade/destroy/repair path through the in-game test runner
- verify `KSP.log` has the same `PatchFacilities:` and `PatchDestructionState:` lines, with no skipped-only INFO flood

## Rollback

Rollback should be simple because the first slice is move-only:

1. Restore the original `PatchFacilities` and `PatchDestructionState` method bodies in `KspStatePatcher.cs`.
2. Delete `FacilityStatePatcher.cs`.
3. Keep all callers on `KspStatePatcher`, so no caller rollback should be needed.
4. Re-run the same `KspStatePatcherTests` filter.

If any runtime playtest shows changed facility timing, skip logic, or log shape, revert the extraction commit rather than patching forward. The intended value is structural cleanup only; it should not require behavior fixes.

## Deferred Follow-Ups

- Consider a later scalar-resource helper only after adding characterization tests for exact funds/science/reputation no-op logs, suspicious drawdown logs, and reputation absolute-set behavior.
- Keep kerbals separate. It is already its own family patcher and should remain ordered before `PatchAll` unless a separate orchestration proposal proves a safer shape.
- Tech tree, milestones, and contracts are not good first extraction targets because they rely heavily on reflection, stock UI refresh, or ConfigNode contract load behavior.
