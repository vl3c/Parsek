# ParsekScenario static-state map, 2026-11-08

The static-state map for `ParsekScenario` (split rule S4: "build the static mutable state
map before moving anything"). For each of the type's 31 reassignable static fields it lists
where the field is declared, which methods write and read it, how code outside the type
reaches it, and which reset paths clear it. Later changes that move this state need it, and
so do the wave 2 stream W2 extractions (2b). An extraction must not reorder a write against
a read.

Measured on the refactor branch `claude/project-thread-bkwmrr` at `9ad9008bd`, from the
compiled Debug `Parsek.dll` and `Parsek.Tests.dll` of that tree. No `ParsekScenario*.cs`
file changed between that commit and `1cadb13ef`. Line numbers are at that commit. The 2b
extractions insert phase methods above `OnLoad` in `ParsekScenario.cs`, so each one moves
down every field declared below `OnLoad`. Read-only: no production code changed.

## How it was measured

The tool is the one that produced the `GhostMapPresence` map
(`ghostmappresence-static-state-map-2026-11-07.md`):

```
python3 scripts/refactor/static_state_map.py --type Parsek.ParsekScenario --json ps.json Parsek.dll
python3 scripts/refactor/static_state_map.py --type Parsek.ParsekScenario --fields-from ps.json --json ps-tests.json Parsek.Tests.dll
```

It reads IL, where every field operand names its declaring type. That matters here because
the bare names are reused elsewhere: `initialLoadDone` and `lastSaveFolder` are also static
fields of `MilestoneStore` and `GameStateStore`. The `StoreSaveRoundTripTests` cells that
reflect over a field called `initialLoadDone` target those two stores, not this one.

The columns are built from the tool's JSON as follows:

- **written by** / **read by**: `ParsekScenario` methods whose access to the field includes
  a reassign (or not). Lambdas, closures, local functions and iterator state machines count
  toward the method that contains them. Reset roots (below) and the type initialiser are
  left out of this column; `(T)` marks a `*ForTesting` seam. `(alias)` marks a read whose
  value goes into another location: a local, a closure or an argument array. Of those,
  only `RefreshPendingQuickloadTrimScope` writes through the copy. It sets `TrimScope` and
  `TrimScopeReason` on the context object that `pendingQuickloadResumeContext` holds.
- **reached from outside through**: the `ParsekScenario` method that touches the field,
  followed by the production types outside `ParsekScenario` that call it directly. Rows
  marked `(field)` are outside code that uses the field itself. Calls from in-game tests
  (`Parsek.dll`) appear as `(in-game tests)`. Reset roots are left out because they appear
  in the last column.
- **reset paths**: a reset root that writes the field, directly or through a
  `ParsekScenario` method it calls.

Reset roots:

| abbr | method | production callers |
| --- | --- | --- |
| COLD | `ResetSaveScopedSessionState` and the `ResetScenarioSessionStatics` it calls | `OnLoad`, at the top of every Cold load, right after the load is classified |
| FOLDER | `DetectSaveFolderChange` (only when the save folder changed) | `OnLoad` prologue; `ShouldKeepBackupHold` |
| MENU | `OnMainMenuTransition` | `OnDestroy`; the main-menu hook `RegisterMainMenuHook` installs |
| BATCH | `PrepareForIsolatedBatchFlightBaselineRestore` | `InGameTestRunner.RestoreBatchFlightBaselineCore`; `ParsekTestCommandAddon.SafeWriteCrashImpl` |
| CRASH | `PrepareInMemoryStateForTestBatchCrashReload` | `RunTestBatchCrashReconcileCore` (the next OnLoad after a crashed in-game batch) |
| RIT | `ResetInstanceForTesting` | none (xUnit only) |

## Counts

The type has 31 static fields, all reassignable, and the same 31 the plan counts. One of
them, `CrashReconcileInProgress`, is a static auto-property. The type has no `static
readonly` collections. Five partial files declare them:

- `ParsekScenario.cs`: 21
- `ParsekScenario.SessionReset.cs`: 5
- `ParsekScenario.RevertLedgerSnapshot.cs`: 3
- `ParsekScenario.LoadDroppedRecordings.cs`: 1
- `ParsekScenario.DiscardedFlightRestore.cs`: 1

Constants and the nested types' statics are left out.

## The map

### Load-cycle and save-identity flags

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `initialLoadDone` | ParsekScenario.cs:679 | bool | `EnterBackupHold`, `OnLoad` | `CaptureInSessionStagedStateHandoff`, `CaptureInitialBaseline`, `LoadCrewAndGroupState`, `ShouldKeepBackupHold` | - | BATCH, CRASH, FOLDER, MENU |
| `lastSaveFolder` | ParsekScenario.cs:689 | string | `DetectSaveFolderChange` | `OnLoad`, `ShouldKeepBackupHold` (alias) | - | MENU |
| `committedTreeStateLoaded` | ParsekScenario.cs:688 | bool | `OnLoad`, `set_CommittedTreeStateLoadedForTesting` (T) | `PreserveRecordingStateIfLoadFault`, `get_CommittedTreeStateLoadedForTesting` (T) | - | BATCH, COLD, CRASH, FOLDER, MENU |
| `budgetDeductionApplied` | ParsekScenario.cs:690 | bool | `LoadExternalFiles`, `OnLoad` | - | - | BATCH, COLD, CRASH |
| `coldLoadsThisProcess` | ParsekScenario.SessionReset.cs:19 | int | `set_ColdLoadsThisProcessForTesting` (T) | `OnLoad`, `get_ColdLoadsThisProcessForTesting` (T) | - | COLD |
| `onLoadInProgress` | ParsekScenario.cs:149 | bool | `OnLoad`, `SetOnLoadInProgressForTesting` (T) | `TryResolveLiveUniversalTimeForRouteMarkers`, `get_IsOnLoadInProgress` | `get_IsOnLoadInProgress` <- FacilityStatePatcher, KerbalsModule, KspStatePatcher, LedgerOrchestrator, Patches.StrategyActivationGate | none |
| `lastOnSaveScene` | ParsekScenario.cs:695 | GameScenes | `OnSave` | `OnLoad` | - | MENU |
| `lastWrittenSaveStamp` | ParsekScenario.SessionReset.cs:144 | string | `WriteSaveStamp`, `set_LastWrittenSaveStampForTesting` (T) | `CheckLoadedSaveStamp` (alias), `get_LastWrittenSaveStampForTesting` (T) | - | none |
| `announcedInSessionSaveLoadSource` | ParsekScenario.SessionReset.cs:198 | string | `AnnounceInSessionSaveLoad`, `ConsumeAnnouncedInSessionSaveLoad` | - | `AnnounceInSessionSaveLoad` <- TestCommands.ParsekTestCommandAddon | COLD |
| `lastLoadDroppedRecordingIds` | ParsekScenario.LoadDroppedRecordings.cs:22 | HashSet<string> | `NoteLoadDroppedRecordings`, `TakeLoadDroppedRecordingIds` | - | - | COLD, RIT |

### Scene transition, vessel switch and merge dialog

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `vesselSwitchPending` | ParsekScenario.cs:279 | bool | `OnLoad`, `TryArmVesselSwitchFlag`, `set_VesselSwitchPendingForTesting` (T) | `IsVesselSwitchPendingFresh`, `get_VesselSwitchPendingForTesting` (T) | `IsVesselSwitchPendingFresh` <- ParsekFlightMergeFlow | BATCH, COLD, CRASH |
| `vesselSwitchPendingFrame` | ParsekScenario.cs:280 | int | `OnLoad`, `TryArmVesselSwitchFlag`, `set_VesselSwitchPendingForTesting` (T) | `IsVesselSwitchPendingFresh`, `get_VesselSwitchPendingFrameForTesting` (T) | `IsVesselSwitchPendingFresh` <- ParsekFlightMergeFlow | BATCH, COLD, CRASH |
| `flightSceneReadyForVesselSwitchFlag` | ParsekScenario.cs:292 | bool | `ClearFlightSceneReadyForVesselSwitchFlag`, `NoteFlightSceneReadyForVesselSwitchFlag`, `set_FlightSceneReadyForVesselSwitchFlagForTesting` (T) | `TryArmVesselSwitchFlag`, `get_FlightSceneReadyForVesselSwitchFlagForTesting` (T) | `NoteFlightSceneReadyForVesselSwitchFlag` <- ParsekFlight | COLD |
| `lastSceneChangeRequestedUT` | ParsekScenario.cs:317 | double | `OnLoad`, `StampSceneChangeRequestedUT` | - | `StampSceneChangeRequestedUT` <- ParsekFlight | COLD, MENU |
| `currentFlightLaunchUT` | ParsekScenario.cs:329 | double | `OnLoad`, `SetCurrentFlightLaunchUTForTesting` (T) | - | - | COLD, RIT |
| `mergeDialogPending` | ParsekScenario.cs:711 | bool | `OnLoad`, `set_MergeDialogPending` | `SafetyNetAutoCommitPending`, `get_MergeDialogPending` | `get_MergeDialogPending` <- (in-game tests), MergeDialog, Patches.MapFocusObjectOnSelectPatch, TestCommands.ParsekTestCommandAddon; `set_MergeDialogPending` <- (in-game tests), MergeDialog | BATCH, COLD, CRASH |
| `cachedAutoMerge` | ParsekScenario.cs:706 | bool | `SetCachedAutoMergeForTesting` (T), `get_IsAutoMerge` | - | `get_IsAutoMerge` <- Patches.MapFocusObjectOnSelectPatch, SceneExitInterceptor, TestCommands.ParsekTestCommandAddon | none |

### Revert ledger snapshot

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `flightEntryLedgerActionIds` | ParsekScenario.RevertLedgerSnapshot.cs:31 | HashSet<string> | `RememberFlightEntryLedgerRows`, `SetFlightEntryLedgerActionIdsForTesting` (T) | `PruneRevertedFlightLedgerRows`, `get_FlightEntryLedgerActionIdsForTesting` (T) | - | COLD, RIT |
| `prelaunchEntryLedgerActionIds` | ParsekScenario.RevertLedgerSnapshot.cs:35 | HashSet<string> | `RememberFlightEntryLedgerRows`, `SetPrelaunchEntryLedgerActionIdsForTesting` (T) | `PruneRevertedFlightLedgerRows`, `get_PrelaunchEntryLedgerActionIdsForTesting` (T) | - | RIT |
| `freshLaunchEntryPending` | ParsekScenario.RevertLedgerSnapshot.cs:38 | bool | `OnLoad`, `RememberFlightEntryLedgerRows`, `SetFreshLaunchEntryPendingForTesting` (T) | - | - | COLD, RIT |

### Active-tree restore and quickload-resume carriers

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `ScheduleActiveTreeRestoreOnFlightReady` | ParsekScenario.cs:2324 | ActiveTreeRestoreMode | `OnLoad` | `RecalculateLedgerForInSessionLoad` | - | BATCH, COLD, CRASH |
| `pendingActiveTreeResumeRewindSave` | ParsekScenario.cs:2315 | string | `TryDeclineDiscardedActiveTree`, `TryRestoreActiveTreeNode` | - | - | BATCH, COLD, CRASH |
| `pendingQuickloadResumeContext` | ParsekScenario.cs:2316 | QuickloadResumeContext | `ClearPendingQuickloadResumeContext`, `ConfigurePendingQuickloadResumeContext` | `GetPendingQuickloadLoadKind`, `GetPendingQuickloadQuicksaveFacts`, `GetPendingQuickloadTrimScope`, `MatchesPendingQuickloadResumeContext`, `RefreshPendingQuickloadTrimScope` (alias) | `ClearPendingQuickloadResumeContext` <- FlightRecorder; `GetPendingQuickloadLoadKind` <- FlightRecorder; `GetPendingQuickloadQuicksaveFacts` <- FlightRecorder; `GetPendingQuickloadTrimScope` <- FlightRecorder; `MatchesPendingQuickloadResumeContext` <- FlightRecorder; `RefreshPendingQuickloadTrimScope` <- RewindInvoker | COLD |
| `lastRestoredQuicksaveTreeFacts` | ParsekScenario.cs:2311 | QuicksaveTreeFacts | `ClearRestoredQuicksaveTreeFactsForTesting` (T), `ConfigurePendingQuickloadResumeContext`, `TryDeclineDiscardedActiveTree`, `TryRestoreActiveTreeNode` | - | - | COLD |
| `pendingFreshRecordingAfterDiscard` | ParsekScenario.DiscardedFlightRestore.cs:54 | FreshRecordingAfterDiscardRequest | `ClearFreshRecordingAfterDiscardForTesting` (T), `ConsumeFreshRecordingAfterDiscard`, `TryDeclineDiscardedActiveTree`, `TryRestoreActiveTreeNode` | `get_FreshRecordingAfterDiscardTreeIdForTesting` (T), `get_IsFreshRecordingAfterDiscardArmed` | `ConsumeFreshRecordingAfterDiscard` <- ParsekFlight | BATCH, COLD, CRASH, RIT |
| `pendingSaveAttemptEvents` | ParsekScenario.SessionReset.cs:25 | ConfigNode | `ApplySaveAttemptEventsAfterActiveTreeRestore`, `LoadGameStateFromNodeOrFiles` | `TryRearmSaveAttemptForRestoredTree`, `get_HasPendingSaveAttemptEventsForTesting` (T) | - | COLD |
| `heldSaveAttemptEvents` | ParsekScenario.SessionReset.cs:33 | ConfigNode | `ApplySaveAttemptEventsAfterActiveTreeRestore`, `DropHeldSaveAttemptEvents`, `ResolveHeldSaveAttemptEventsOnArm` | `get_HasHeldSaveAttemptEventsForTesting` (T) | `ResolveHeldSaveAttemptEventsOnArm` <- ParsekFlight | COLD |

### Instance and crash reconcile

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `s_instance` | ParsekScenario.cs:217 | ParsekScenario | `OnAwake`, `OnDestroy`, `SetInstanceForTesting` (T) | `get_Instance` | `get_Instance` <- 69 types | RIT |
| `CrashReconcileInProgress` | ParsekScenario.cs:72 | bool (auto-prop) | `RunTestBatchCrashReconcileCore`, `set_CrashReconcileInProgress` | `get_CrashReconcileInProgress` | R `InGameTests.TestRunnerShortcut::UpdateAutorun` (field); `get_CrashReconcileInProgress` <- (in-game tests) | none |

### Test seams

| field | declared | type | written by (ParsekScenario methods) | read by (ParsekScenario methods) | reached from outside through | reset paths |
| --- | --- | --- | --- | --- | --- | --- |
| `CurrentTimelineUTProviderForTesting` | ParsekScenario.cs:239 | Func<double> | - | `GetCurrentTimelineUTForLedgerRecalc` | `GetCurrentTimelineUTForLedgerRecalc` <- 7 types | RIT |
| `PersistentSavePathOverrideForTesting` | ParsekScenario.cs:602 | string | - | `PreserveRecordingStateIfLoadFault`, `TryLoadCarryForwardSourceParsekNode` | `TryLoadCarryForwardSourceParsekNode` <- RevertInterceptor, RewindLauncher | none |

## xUnit tests that write the state directly

These rows come from the same scan over `Parsek.Tests.dll`, using the field list from the
`Parsek.dll` run. These tests set their state by writing the field itself, not by calling a
method. Moving the field to another type, or behind a property, breaks them at compile time:

| field | test classes writing it |
| --- | --- |
| `CurrentTimelineUTProviderForTesting` | `CommittedMilestoneReachTests`, `FreshRolloutRefusedTreeDiscardTests`, `LedgerHeldWindowTests`, `RecordThresholdOncePerCareerTests`, `RewindUtCutoffTests`, `TreeCommitCurrentClockWalkTests` |
| `pendingActiveTreeResumeRewindSave` | `ColdLoadRestoreAttemptRearmTests`, `InPlaceResumeSidecarVersionTests`, `InSessionStagedListsCarryTests`, `ParsekFileCollectorTests`, `QuickloadCommittedAfterQuicksaveTests`, `QuickloadIntoDiscardedFlightTests`, `QuickloadResumeTests` |
| `PersistentSavePathOverrideForTesting` | `OptimizerFlushSidecarEpochTests`, `OrphanCleanupSafetyGuardTests`, `UnserializableTreeCarryForwardTests` |
| `ScheduleActiveTreeRestoreOnFlightReady` | `QuickloadResumeTests` |

Some tests read or write fields by name through reflection, which the compiler cannot see.
A rename, or a move to another type, fails these at run time and not at compile time:

- `initialLoadDone`, from five classes: `QuickloadResumeTests`, `KerbalLoadDiagnosticsTests`,
  `KerbalLoadPipelineTests`, `FormatGoldenV1Tests` and `InSessionStagedListsCarryTests`,
  all through `typeof(ParsekScenario).GetField`.
- `budgetDeductionApplied`, `vesselSwitchPending` and `vesselSwitchPendingFrame`, from
  `QuickloadResumeTests`, through its `SetPrivateStaticField` / `GetPrivateStaticField`
  helpers.

Every other field in the tables is reached in tests through a `*ForTesting` seam or through
behaviour.

## What the map says

1. **OnLoad's own static writes.** `OnLoad` writes 11 of the fields itself:
   - `initialLoadDone`
   - `committedTreeStateLoaded`
   - `budgetDeductionApplied`
   - `onLoadInProgress`
   - `mergeDialogPending`
   - `vesselSwitchPending` and `vesselSwitchPendingFrame`
   - `lastSceneChangeRequestedUT`
   - `currentFlightLaunchUT`
   - `freshLaunchEntryPending`
   - `ScheduleActiveTreeRestoreOnFlightReady`

   It reads three directly: `coldLoadsThisProcess`, `lastOnSaveScene` and `lastSaveFolder`.
   The rest it reaches through the methods it calls. The `OnLoadPhase*` methods extracted
   so far (2b-1, and 2b-2 in #2484) touch none of the 31 fields directly, so no write moved
   relative to these. A later 2b phase that carries one of these writes along has to keep it
   in the same order relative to the reads in the table. The characterization token stream
   (`ParsekScenarioOnLoadCharacterizationTests`) pins that order, because every assignment
   target is a token.

2. **Most of the state is private to the type.** Outside production code touches only one
   field directly: `InGameTests.TestRunnerShortcut.UpdateAutorun` reads the
   `CrashReconcileInProgress` auto-property. All other outside access goes through named
   methods:
   - `Instance`, from 69 types.
   - `IsOnLoadInProgress`, from five types: `FacilityStatePatcher`, `KspStatePatcher`,
     `LedgerOrchestrator`, `KerbalsModule` and `Patches.StrategyActivationGate`.
   - `IsAutoMerge`, from `SceneExitInterceptor`, `Patches.MapFocusObjectOnSelectPatch` and
     the command seam.
   - `MergeDialogPending`, from `MergeDialog` (which also sets it),
     `Patches.MapFocusObjectOnSelectPatch` and the command seam.
   - The quickload-resume context accessors, from `FlightRecorder` and `RewindInvoker`.
   - `StampSceneChangeRequestedUT`, `NoteFlightSceneReadyForVesselSwitchFlag`,
     `ResolveHeldSaveAttemptEventsOnArm` and `ConsumeFreshRecordingAfterDiscard`, from
     `ParsekFlight`.
   - `IsVesselSwitchPendingFresh`, from `ParsekFlightMergeFlow`.
   - `GetCurrentTimelineUTForLedgerRecalc`, from seven types.

3. **Three lifetimes.**
   - Process lifetime. COLD does not clear these fields:
     - the save identity: `initialLoadDone`, `lastSaveFolder` and `lastOnSaveScene`.
       MENU and FOLDER clear these.
     - `lastWrittenSaveStamp`, which has no reset path at all.
     - `coldLoadsThisProcess`, a counter that COLD increments and nothing clears.
   - Save-scoped session state. COLD clears this on every Cold load, through
     `ResetScenarioSessionStatics`; the list is everything that method assigns. BATCH and
     CRASH clear the subset an in-game batch restore needs.
   - Instance-scoped: `s_instance`, set in `OnAwake` and cleared in `OnDestroy`.

4. **Never reset in a player session.** These are by design; they are recorded here so that
   a move keeps them:
   - `onLoadInProgress`: set and cleared inside `OnLoad`'s own try/finally.
   - `CrashReconcileInProgress`: cleared by the deferred reload coroutine once the reconcile
     completes. It stays set on an incomplete disk revert, so the harness timeout is the
     reaper.
   - `cachedAutoMerge`: the `IsAutoMerge` getter rewrites it from the settings on every read.
   - `lastWrittenSaveStamp`.
   - The two test seams `CurrentTimelineUTProviderForTesting` and
     `PersistentSavePathOverrideForTesting`. No production method writes either one. RIT
     resets only the first, so a test that sets the second must restore it itself.

5. **One asymmetry, stated in the source.** COLD clears `flightEntryLedgerActionIds` but not
   `prelaunchEntryLedgerActionIds`. The declaration comment says the prelaunch set is "kept
   across loads, as stock keeps PreLaunchState", and only a fresh launch rewrites it
   (`RememberFlightEntryLedgerRows`). It is recorded here as intent, not as a defect. A move
   of the revert-ledger snapshot must keep it.

## For the W2 tasks

- **2b (OnLoad and the other long methods).** Phase methods are `internal static`, so they
  can see every field here. The extractions keep `onLoadInProgress` and the `loadPhase`
  local in `OnLoad`, as the plan requires. The other `OnLoad` writes listed in item 1 stay
  where the characterization stream pins them. `TryRestoreActiveTreeNode` and
  `TryDeclineDiscardedActiveTree` are the only writers of
  `pendingActiveTreeResumeRewindSave` and `lastRestoredQuicksaveTreeFacts` outside the reset
  roots. `ConfigurePendingQuickloadResumeContext` also writes
  `lastRestoredQuicksaveTreeFacts`, so an extraction from either method must keep the
  order of those writes as it is.
- **A later move of this state** (out of the type, or behind a service) has three costs:
  - the reflection reads above, which fail at run time, not at compile time
  - the four fields xUnit writes directly
  - `Instance`'s 69 caller types
