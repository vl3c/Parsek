# LedgerOrchestrator static-state map, 2026-11-08

The static-state map for `LedgerOrchestrator` (split rule S4: "build the static mutable state
map before moving anything"). For every static field of the type: where it is declared, which
methods write it, who outside the type writes or reads it, which reset paths clear it, and
which hosts reach a writer. It is the input for the long-method extractions of wave 2 stream
W7 (7b), which must not reorder a write against a read, and for any later move of the type's
state into services (7c).

Measured on the refactor branch `claude/project-thread-bkwmrr` at `e21f22290`, from the
compiled Debug `Parsek.dll` and `Parsek.Tests.dll` of that tree. Read-only: no production code
changed.

## How it was measured

`scripts/refactor/static_state_map.py` (wave 2 W5a, `docs/dev/research/ghostmappresence-static-state-map-2026-11-07.md`
explains the method and why a source grep cannot answer "who touches this field") over
`ikdasm` output: every IL field operand names its declaring type, and a stack simulation
follows each loaded value to the instruction that consumes it. Lambdas, closures and local
functions are credited to the source method that wrote them.

How the table columns were derived from the tool's JSON:

- **written by**: `LedgerOrchestrator` methods whose access kinds include reassign, addr,
  mutate, or `aliased` (the value stored into another field). The type initialiser and the two
  reset helpers (`ResetForTesting`, `ResetForSaveLoad`) are left out of this column; `(T)`
  marks a `*ForTesting` seam.
- **read by**: `LedgerOrchestrator` methods that only read it, including the module accessor
  properties (`get_Funds`, ...).
- **outside the type**: production methods of other types that write (W) or read (R) the field
  itself. In-game tests (in `Parsek.dll`) are named separately.
- **reset paths**: a reset root clears the field when the root, or any `LedgerOrchestrator`
  method it calls transitively, writes it.
- **hosts reaching a writer**: the production types outside `LedgerOrchestrator` that reach a
  writer through `LedgerOrchestrator`'s own call chains. Ledger state has no scene of its own
  (the ledger is the same in every scene), so the hosts are listed instead of scenes.

Reset roots:

| abbr | method | production callers |
| --- | --- | --- |
| RFT | `ResetForTesting` | `ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore`, `ParsekScenario.PrepareInMemoryStateForTestBatchCrashReload` (the in-game test batch's isolation paths), and xUnit |
| RFSL | `ResetForSaveLoad` | `ParsekScenario.ResetSaveScopedSessionState` (save switch, main menu) |
| LOAD | `OnLoad` | `ParsekScenario.LoadGameStateFromNodeOrFiles`. Calls `Initialize` (so it re-creates nothing when already initialized) and clears the three seed latches; `SeedKscSequenceCounterFromLoadedLedger` only RAISES the KSC counter. |
| INIT | `Initialize` | lazy: 21 `LedgerOrchestrator` entry points call it (every live-event door, the commit and recovery paths, `RecalculateAndPatchCore`), plus `ParsekScenario.LoadCrewAndGroupState`, `ParsekScenario.ResetSaveScopedSessionState` and `ReFlyMergeDominance.EvaluateLiveCore`. Idempotent: returns at once while `initialized` is true. |

## Counts

35 static non-literal fields: 30 reassignable (12 of them test seams) and 5 `static readonly`
(two `HashSet<string>` log de-duplicators, the committed-future id set, an empty array and a
`double`). Every one is declared in `LedgerOrchestrator.cs`; the other nine partial files
declare none. Three are `[ThreadStatic]` (the `*TrackedOverrideForTesting` trio).

## The map

### The eight resource modules and their latch

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `scienceModule` | LedgerOrchestrator.cs:23 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `BuildLedgerStructuralSnapshot`, `CanAffordScienceSpending`, `get_Science` | - | RFT, RFSL, INIT, LOAD | every ledger host (23 types) |
| `milestonesModule` | LedgerOrchestrator.cs:24 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `RecalculateAndPatchCore`, `get_Milestones` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `contractsModule` | LedgerOrchestrator.cs:25 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `BuildLedgerStructuralSnapshot`, `UpdateSlotLimitsFromFacilities`, `get_Contracts` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `fundsModule` | LedgerOrchestrator.cs:26 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `BuildLedgerStructuralSnapshot`, `CanAffordFundsSpending`, `get_Funds` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `reputationModule` | LedgerOrchestrator.cs:27 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `BuildLedgerStructuralSnapshot`, `get_Reputation` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `facilitiesModule` | LedgerOrchestrator.cs:28 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `BuildLedgerStructuralSnapshot`, `GetUpgradeableFacilityLevel`, `UpdateSlotLimitsFromFacilities`, `get_Facilities` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `strategiesModule` | LedgerOrchestrator.cs:29 | reassignable | `Initialize` | `ApplyRecalculatedStateToKsp`, `UpdateSlotLimitsFromFacilities`, `get_Strategies` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `kerbalsModule` | LedgerOrchestrator.cs:30 | reassignable | `Initialize`, `SetKerbalsForTesting(T)` | `ApplyRecalculatedStateToKsp`, `RecalculateAndPatchCore`, `RecalculateIfKerbalReservationReleaseDue`, `TryEnsureKerbalsModuleForTombstoneRosterCleanup`, `get_Kerbals` | - | RFT, RFSL, INIT, LOAD | every ledger host |
| `initialized` | LedgerOrchestrator.cs:31 | reassignable | `Initialize` | `TryEnsureKerbalsModuleForTombstoneRosterCleanup`, `get_IsInitialized` | - | RFT, RFSL, INIT, LOAD | every ledger host |

"Every ledger host" is the 23 production types that reach `Initialize`: `CareerLedgerClockCursor`,
`CommittedTreeRestoreAttempt`, `FacilityRepairCapture`, `FacilityUpgradeCapture`,
`GameStateFacilityRecorder`, `GameStateRecorder`, `LiveOnlyIncome`, `MergeDialog`,
`MergeJournalOrchestrator`, `ParsekFlight`, `ParsekFlightMergeFlow`, `ParsekFlightSplitBrancher`,
`ParsekKSC`, `ParsekScenario`, `ParsekTrackingStation`, `Patches.CrewAutoAssignPatch`,
`Patches.TechResearchPatch`, `ReFlyMergeDominance`, `RewindInvoker`, `SupersedeCommit`,
`SwitchSegmentAttempts`, `TimeJumpManager`, `TreeDiscardService`.

The modules are read outside the type only through the accessor properties. Production callers
of those accessors: `Kerbals` 26 methods in 17 types (the crew reservation and stock-UI
crew layers, `KerbalsWindowUI`, `ParsekScenario`, `SupersedeCommit`, `TimelineBuilder`, three
patches, the command seam); `Science` (`CurrencyReservationOverlay`, `ScienceSubjectPatch`);
`Funds` (`CurrencyReservationOverlay`); `Facilities` (`ParsekFlight.OnTimeWarpRateChanged`);
`IsInitialized` (`GameStateRecorder` x2, `ParsekFlight.OnTimeWarpRateChanged`,
`ScienceSubjectPatch`). `Contracts`, `Milestones`, `Reputation` and `Strategies` are read only
by in-game tests.

### Seed latches and the KSC sequence

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `fundsSeedDone` | LedgerOrchestrator.cs:32 | reassignable | `OnLoad`, `SeedInitialResourceBalances` | (its writers) | - | RFT, RFSL, LOAD | 18 types (every recalc host) |
| `scienceSeedDone` | LedgerOrchestrator.cs:33 | reassignable | `OnLoad`, `SeedInitialResourceBalances` | (its writers) | - | RFT, RFSL, LOAD | 18 types |
| `repSeedDone` | LedgerOrchestrator.cs:34 | reassignable | `EnsureReputationSeedForCommit`, `OnLoad`, `SeedInitialResourceBalances` | (its writers) | - | RFT, RFSL, LOAD | 18 types |
| `kscSequenceCounter` | LedgerOrchestrator.cs:55 | reassignable | `AllocateKscSequence`, `OnLiveOnlyIncome`, `OnStrategyCurrencyConversion`, `SeedKscSequenceCounterFromLoadedLedger`, `TryAddKscSpendingAction` | `get_KscSequenceCounterForTesting` | - | RFT, RFSL (LOAD raises it to the loaded maximum) | 11 types: `CommittedTreeRestoreAttempt`, `FacilityRepairCapture`, `FacilityUpgradeCapture`, `GameStateFacilityRecorder`, `GameStateRecorder`, `LiveOnlyIncome`, `MergeDialog`, `ParsekFlightMergeFlow`, `ParsekScenario`, `SwitchSegmentAttempts`, `TreeDiscardService` |

`SeedInitialResourceBalances` is reached only from `RecalculateAndPatchCore`;
`EnsureReputationSeedForCommit` from `NotifyLedgerTreeCommitted`,
`FileRetiredReFlyProvisionalRows` and `ResolveCommitReputationSeedOrigin`. Of the five
`kscSequenceCounter` writers, `OnLiveOnlyIncome`, `OnStrategyCurrencyConversion` and
`TryAddKscSpendingAction` increment it inline and store the value into the new row
(`aliased(stfld)`); the other KSC rows take `AllocateKscSequence()` (8 callers).

### Committed-future snapshot and the clock-advance flag

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `committedFutureActionIdsAtLastPatch` | LedgerOrchestrator.cs:240 | readonly collection | `CaptureCommittedFutureSnapshot`, `InvalidateCommittedFutureSnapshot` | `ComputeClockCrossing` | - | RFT, RFSL | 18 types (every recalc host) |
| `committedFutureSnapshotValid` | LedgerOrchestrator.cs:242 | reassignable | `CaptureCommittedFutureSnapshot`, `InvalidateCommittedFutureSnapshot` | `ComputeClockCrossing` | - | RFT, RFSL | 18 types |
| `clockAdvanceRecalcInProgress` | LedgerOrchestrator.cs:243 | reassignable | `RunAsClockAdvance` (saves, sets, restores in `finally`) | `RecalculateAndPatchCore` | - | RFT | `CareerLedgerClockCursor`, `ParsekFlight`, `TimeJumpManager` |

`CaptureCommittedFutureSnapshot` runs only inside `RecalculateAndPatchCore`;
`InvalidateCommittedFutureSnapshot` from `OnKspLoad` and
`ParsekScenario.RecalculateLedgerForInSessionLoad`. `RunAsClockAdvance` wraps exactly two
recalcs: `RecalculateAndPatchForClockAdvance` and `RecalculateAndPatchForTimeJump`.

### Kerbal reservation-release trigger

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `lastTriggeredReservationReleaseUT` | LedgerOrchestrator.cs:231 | reassignable | `RecalculateIfKerbalReservationReleaseDue` | (its writer) | - | RFT, RFSL | `ParsekKSC.Update`, `ParsekTrackingStation.Update`, `Patches.CrewAutoAssignPatch.Prefix` |
| `postWalkCountAfterReservationReleaseTrigger` | LedgerOrchestrator.cs:235 | reassignable | `RecalculateIfKerbalReservationReleaseDue` | (its writer; passed to `KerbalsModule.ResolveLastTriggeredReleaseUT`) | - | RFT, RFSL | same three |

Both start at their sentinels (`NaN`, `-1`) in the type initialiser and are restored to them by
both reset helpers.

### Log de-duplication and constants

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `emittedReconcileWarnKeys` | LedgerOrchestrator.cs:44 | readonly collection | `LogReconcileWarnOnce` | - | - | RFT only | `MergeDialog`, `ParsekFlightMergeFlow`, `ParsekFlightSplitBrancher`, `ParsekScenario`, `PostWalkActionReconciler` |
| `emittedScienceReconcileDumpKeys` | LedgerOrchestrator.cs:46 | readonly collection | `LogScienceCommitReconcileDumpOnce`, `TryRegisterPostWalkDumpKey` | - | - | RFT only | same five |
| `OneShotReconcileSkipLogIntervalSeconds` | LedgerOrchestrator.cs:42 | readonly double | - | `LogReconcileSkippedOnce` (rate-limit interval argument) | - | none | - |
| `EmptyPendingScience` | LedgerOrchestrator.cs:203 | readonly (empty array) | none (see note) | `BuildPendingScienceSubsetForRecording`, `BuildTreeCommitRowsPreview`, `BuildTreePendingScienceRouting`, `FinalizeScopedPendingScienceCommit`, `NotifyLedgerTreeCommitted` | - | none | - |

Note on `EmptyPendingScience`: the tool reports `mutate:Add` / `read:get_Count` for it in
`NotifyLedgerTreeCommitted`. That is a mis-kinded value, not a write: the field is a
zero-length `PendingScienceSubject[]` behind `IReadOnlyList` (an `Add` would throw), the method
stores it into the `perRecordingPending` local, and the call
`filedCommits.Add(FileRecordingCommitRows(...perRecordingPending...))` has a six-argument
operand that `ikdasm` prints across several lines, which the one-line instruction parser
under-counts, so the tagged argument is left on the simulated stack as the `Add` receiver.
The method attribution is right; the kind is wrong. Nothing writes the field.

### Event

| field | declared | kind | written by | read by | outside the type | reset paths | hosts reaching a writer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `OnTimelineDataChanged` | LedgerOrchestrator.cs:79 | reassignable (delegate) | - | `RecalculateAndPatchCore` (invokes it at the end of every recalc) | W `ParsekUI` ctor / `Cleanup` (+= / -=), W `StockUiOverlayController.Awake` / `OnDestroy` (+= / -=); R `RecordingTreeSplitter.RunPostSplitSteps` and `RollBackInMemory` (invoke it directly); in-game tests: `GuiMockApplyInGameTest` writes, `FlightIntegrationTests` invokes | RFT only | - |

### Test seams

| field | declared | read by (production use of the seam) | written by | reset paths |
| --- | --- | --- | --- | --- |
| `fundsTrackedOverrideForTesting` | LedgerOrchestrator.cs:36 | `GetResourceTrackingAvailability` | `SetResourceTrackingAvailabilityForTesting(T)` | RFT |
| `scienceTrackedOverrideForTesting` | LedgerOrchestrator.cs:38 | `GetResourceTrackingAvailability` | `SetResourceTrackingAvailabilityForTesting(T)` | RFT |
| `repTrackedOverrideForTesting` | LedgerOrchestrator.cs:40 | `GetResourceTrackingAvailability` | `SetResourceTrackingAvailabilityForTesting(T)` | RFT |
| `OnRecordingCommittedFaultInjector` | LedgerOrchestrator.cs:185 | `FileRecordingCommitRows` | xUnit | RFT |
| `OnRecordingCommittedPostSciencePersistFaultInjector` | LedgerOrchestrator.cs:195 | `FileRecordingCommitRows` | xUnit | RFT |
| `ProgressSeedProbeForTesting` | LedgerOrchestrator.cs:248 | `ReadProgressSeedProbe` | xUnit | RFT |
| `CurrencyPoolProbeForTesting` | LedgerOrchestrator.cs:251 | `ReadCurrencyPoolProbe` | xUnit | RFT |
| `UncommittedRecordingIdsForTesting` | LedgerOrchestrator.cs:255 | `TryReadUncommittedRecordingsAtSeedCapture` | xUnit | RFT |
| `LoadedFlightStateUTForTesting` | LedgerOrchestrator.cs:259 | `ReadLoadedFlightStateUT` | xUnit | RFT |
| `ExpectedCurrencySingletonsForTesting` | LedgerOrchestrator.cs:263 | `ExpectedCurrencySingletonsForBaseline` | xUnit | RFT |
| `DeferOneFrameForTesting` | LedgerOrchestrator.cs:279 | `ResolveKscDeferHost` | xUnit | RFT |
| `NowUtProviderForTesting` | LedgerOrchestrator.cs:355 | `GetRawClockUT` | xUnit; in-game `IncompleteBallisticRuntimeTests.DrawdownGuard_ScienceTechUnlockWindow_NoSpuriousDownClamp` | RFT |

Each seam is read in exactly one production method, which falls through to the live KSP read
when the seam is null.

## xUnit tests that write the state directly

From the same scan over `Parsek.Tests.dll` (field list taken from the `Parsek.dll` run). Only
the internal seams and the event are written directly; every private field is reached through
methods.

| field | test classes writing it |
| --- | --- |
| `CurrencyPoolProbeForTesting` | `ColdSpaceCenterLoadSeedTests`, `KspSettingsLedgerTests`, `ReputationSeedUncommittedRecordingTests` |
| `DeferOneFrameForTesting` | `LedgerOrchestratorTests`, `PartUpgradePurchaseTests` |
| `ExpectedCurrencySingletonsForTesting` | `ColdSpaceCenterLoadSeedTests`, `ReputationSeedUncommittedRecordingTests` |
| `LoadedFlightStateUTForTesting`, `UncommittedRecordingIdsForTesting` | `ReputationSeedUncommittedRecordingTests` |
| `NowUtProviderForTesting` | `KerbalReservationReleaseTests`, `KspStatePatcherTests`, `LedgerHeldWindowTests`, `LedgerOrchestratorTests`, `ReputationSeedUncommittedRecordingTests`, `RewindReadbackGuardTests`, `RewindUtCutoffTests`, `StockAnnotationConsistencyTests` |
| `OnRecordingCommittedFaultInjector`, `OnRecordingCommittedPostSciencePersistFaultInjector` | `PendingScienceSubjectsClearTests` |
| `OnTimelineDataChanged` | `LedgerOrchestratorTests`, `OptimizerSplitCommitSingleWalkTests`, `RecordingTreeSplitterTests`, `TombstoneReloadMigrationTests` (and `CommittedFutureIndexTests` reads it) |
| `ProgressSeedProbeForTesting` | `PreLedgerProgressSeedTests` |

Reflection the compiler cannot see:

- `SaveScopedSessionResetTests` inventories EVERY static field of `LedgerOrchestrator` by
  reflection (`GetFields(Static | DeclaredOnly)`): each must be reset by
  `ParsekScenario.ResetSaveScopedSessionState` (through `ResetForSaveLoad`) or be named in
  `scripts/session-reset-allowlist.txt` with a reason (18 entries for this type today: the 12
  seams, the clock-advance flag, the two log de-duplicators, the two constants and the event).
  A field moved to another type leaves this inventory unless that type is added to its
  `InventoryTypes` list; a new static on `LedgerOrchestrator` fails it until it is classified.
  It also calls the private `AllocateKscSequence` by name (`SaveScopedSessionResetTests.cs:582`).
- `GetMethod("PopulateUnpopulatedCrewEndStates")` (`LedgerOrchestratorTests` x5) and
  `GetMethod("MigrateKerbalAssignments")` (`LedgerOrchestratorTests` x5,
  `ReFlyMergedDeathCoRowTests`, `TombstoneReloadMigrationTests`): a rename or move of either
  private method fails those tests at run time, not at compile time.

## What the map says

1. **The ledger's own state is small; the rest is seams.** Of 30 reassignable statics, 12 are
   test seams read in exactly one production method each. The real state is 18 fields in five
   groups: the eight modules plus `initialized`, the three seed latches, the KSC sequence
   counter, the committed-future snapshot pair plus the clock-advance flag, and the reservation
   release trigger pair. The ledger rows themselves live in `Ledger`, not here.

2. **Nothing outside the type writes a field except the event.** Production code outside the
   type writes only `OnTimelineDataChanged` (two UI subscribers) and reads only it
   (`RecordingTreeSplitter` invokes it). The modules are read through accessor properties;
   every other field is reached only through `LedgerOrchestrator` methods. So a move of any
   private group changes no caller outside the type.

3. **Two lifetimes, and two fields deliberately outlive a save switch.** `ResetForSaveLoad`
   clears the modules, `initialized`, the seed latches, the KSC counter, the snapshot pair and
   the release trigger pair. It does NOT clear the two log de-duplicators (once per play
   session by design, allowlisted) or `clockAdvanceRecalcInProgress` (scoped by
   `RunAsClockAdvance`'s try/finally). `OnLoad` re-runs the lazy `Initialize` (a no-op while
   initialized), clears the seed latches, and only raises the KSC counter to the loaded
   ledger's maximum sequence, never lowers it.

4. **Re-entrancy scoping.** `clockAdvanceRecalcInProgress` is the type's one scoped flag:
   `RunAsClockAdvance` saves the previous value, sets it, and restores it in `finally`. An
   extraction that splits either wrapped recalc must keep the set and the restore in the same
   try/finally.

5. **Order constraints the extractions must keep.** `RecalculateAndPatchCore` reads
   `clockAdvanceRecalcInProgress`, `kerbalsModule`, `milestonesModule` and
   `OnTimelineDataChanged`, and through its callees writes the seed latches and the snapshot
   pair (`CaptureCommittedFutureSnapshot`) and re-initialises the modules. `ComputeClockCrossing`
   reads the snapshot pair the previous patch captured, so a block that captures must stay after
   every block of the same recalc that reads it.

## For the W7 tasks

- **7b (extract the >= 90-line methods).** Measured from the same JSON, these long methods
  touch NO `LedgerOrchestrator` static, directly or through any `LedgerOrchestrator` callee:
  `PickRecoveryRecording`, `BuildStrategyConversionAction`, `ComputeEarningsWindowEmittedDeltas`,
  `GetActionKey`, `EnsureInitialReputationSeed`, `DeduplicateAgainst`,
  `ResolveTreePendingScienceOwnerRecordingId`, `ClassifyKerbalAssignmentRepair`,
  `MigrateKerbalAssignments`. (They may read other types' statics: `PickRecoveryRecording`
  reads `RecordingStore.CommittedRecordings` and `ParsekScenario.Instance`.) The others reach
  state through callees: `NotifyLedgerTreeCommitted` reaches the modules, the seed latches, the
  KSC counter, the snapshot pair and the two log de-duplicators;
  `TryRecordRecoveryKerbalExperience` and `OnRealVesselRecovered` the same less the
  de-duplicators; `RecalculateAndPatchCore`
  and `OnKspLoad` the modules, seeds and snapshot; `PreserveIrreversibleLiveGameplayOnDiscard`
  and `FileRetiredReFlyProvisionalRows` the modules and the KSC counter (and `repSeedDone`);
  `ApplyRecalculatedStateToKsp` only reads the modules. An extracted block keeps every one of
  those calls in its original order.
- **7c (move services).** A move of a private group into a service changes no outside caller
  (point 2), but it moves fields out of `SaveScopedSessionResetTests`' inventory: the new
  type must join `InventoryTypes` and its fields must be reset by `ResetSaveScopedSessionState`
  or allowlisted, with the same classification they have today.

## Reproducing

```bash
# from a worktree of the measured commit
KSPDIR=/home/user/ksp-refs scripts/cloud-test.sh -class Parsek.Tests.RewindLoggingTests  # builds both DLLs
python3 scripts/refactor/static_state_map.py Source/Parsek/bin/Debug/Parsek.dll \
    --type Parsek.LedgerOrchestrator --json lo.json
python3 scripts/refactor/static_state_map.py \
    Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll \
    --type Parsek.LedgerOrchestrator --fields-from lo.json --json lo-tests.json
```

The columns above are derived from `lo.json` by the rules in "How it was measured". Outputs go
to the scratchpad or `logs/`, never the repo.
