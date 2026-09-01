# Cooperative Async Multiplayer - Codebase Inventory

*Baseline snapshot and verified-mechanics map for implementing `docs/dev/design-coop-async-multiplayer.md`.*

**Baseline test count:** TBD at kickoff (Phase 0 step 0.2 in `coop-async-multiplayer-tasks.md`)
**Worktree:** TBD at kickoff (`../Parsek-coop-multiplayer` off `origin/main`)
**Build state:** TBD at kickoff

The mechanics below were verified in code on 2026-09-01 (commit `3b18e1faf` lineage) by three investigation passes during design; line references are accurate to that date and must be refreshed by the kickoff Explore agents (step 0.4) before any task plan cites them. The FACTS are the load-bearing part; the line numbers are conveniences.

---

## 1. Affected Files

| File | Role | Changes Needed | Complexity | Status |
|------|------|----------------|------------|--------|
| `Source/Parsek/Recording.cs` | Recording model; `SidecarEpoch` at :76-82; generation stamps :12-13 | Owner / Exported / OrphanedBy / ContinuesForeign / MissingParts fields; generation 5 | Low | Pending |
| `Source/Parsek/RecordingStore.cs` (constants) | `CurrentRecordingFormatVersion = 1` :105; `CurrentRecordingSchemaGeneration = 4` :131 (history comment :106-130); `IsRecordingSchemaCompatible` :154-185 | Bump to 5 + history comment; fixture re-stamp (`Fixtures/C1Career`, `Fixtures/C2CareerPostFix`, `harness/fixtures/saves/**`, rebuild `career-earned-pad`) | Low | Pending |
| `Source/Parsek/RecordingTree.cs` | Tree model; `Load` gen gate :206-218; `RebuildBackgroundMap` :376-430 | OwnerPlayerId | Low | Pending |
| `Source/Parsek/RecordingTreeRecordCodec.cs` | RECORDING node codec (84 AddValue sites); `spawnedPid` :307/:720-733 | Owner field round-trip | Low | Pending |
| `Source/Parsek/RecordingStore.cs` | Static store; `CommitTree` :944 (7-step orchestrator :1076-1084); `FinalizeTreeCommit` :1472-1559 (append at END :1509, `BumpStateVersion` :1512, `RebuildBackgroundMap` :1522); `ShouldReplaceCommittedTree` :2075; `InsertCommittedAfter` :696 (mid-list, splitter only); `StateVersion` :502-512 | Import registration seam mirroring CommitTree; fence guards | Med | Pending |
| `Source/Parsek/RecordingStore.Optimization.cs` | `RunOptimizationPass` :15-44 (mid-list Insert :186 / RemoveAt :69; never bumps StateVersion - separate chip filed) | Exported/foreign freeze | Low | Pending |
| `Source/Parsek/RecordingStore.SupersedeTerminalSpawn.cs` | `MarkSupersededTerminalSpawnsForContinuedSources` :15 (skips already-marked :40-41; requires `SpawnedVesselPersistentId != 0` :200-201); `MarkTerminalSpawnSupersededByDockMerge` :120-187 (`uniqueSpawnMatch` :148-150, guid-gated `bakedPidMatch` :151-162) | Reconciliation: clearing/re-pointing (NOTHING clears the stamp today) | Med | Pending |
| `Source/Parsek/RecordingStore.OrphanCleanup.cs` | `CleanOrphanFiles` :345-475 (quarantines unknown ids :460; refuses when known set empty :387-398); `DeleteRecordingFiles` :27-84; `BuildKnownRecordingIds` :260-279 | None expected (imported ids are known) | Low | Pending |
| `Source/Parsek/RecordingPaths.cs` | `ValidateRecordingId` :233-263 (rejects `/`, `\`, `..`, invalid chars; platform-dependent char set) | Gate every packet id | Low | Pending |
| `Source/Parsek/FileIOUtils.cs` | `SafeWriteConfigNode` :97-145, `SafeWriteBytes` :156-181, `ReplaceDestination` :285-359 (File.Replace primary, move-aside fallback), `CopyDirectory` :391-435 (overwrite:false, partial copies left) | Retry/backoff wrapper for shared-folder writes | Low | Pending |
| `Source/Parsek/ParsekScenario.cs` | OnSave :1019-1113; `SaveStagingList` :2534-2548 / `LoadStagingList` :2693 (additive-list pattern); OnLoad ledger reconcile call :4161-4167; `IsCurrentUtCutoffSupportedScene` :635-638 (FLIGHT + SPACECENTER only); auto-unreserve sweep :4265-4286 (EndUT < now only); `LoadRecordingTrees` :5204-5320; in-memory-is-truth rule :3104-3106 | Campaign link node, merge hooks, poll coroutine, reconcile flag | Med | Pending |
| `Source/Parsek/GameActions/Ledger.cs` | `Reconcile` :763-940 (seeds kept :789-796; earnings no UT check :842-858; spendings pruned when `!preserve && UT > maxUT` :867; others :895-924); `SaveToFile` :608 whole-file rewrite; `SeedInitial*` presence scans :994/:1055/:1095 | Campaign preservation flag; owner field | Med | Pending |
| `Source/Parsek/GameActions/GameAction.cs` | `ActionId` :435 (`act_` + Guid at serialize :941-943); `Sequence` :447 (`seq`, non-zero only :947-948); legacy id hash :911-926 | OwnerPlayerId; ordering freeze | Low | Pending |
| `Source/Parsek/GameActions/RecalculationEngine.cs` | `SortActions` :273-285 (stable OrderBy UT/earning/Sequence); `Recalculate(actions, utCutoff)` :144-233 (cutoff filter :165, projection walk :176-194); `IsEarningType` :295-329; `IsSpendingType` :335-370; `IsSeedType` :240-245 | Tiebreak keys | Low | Pending |
| `Source/Parsek/GameActions/LedgerOrchestrator.cs` | `RecalculateAndPatchCore` :1865-1985; `RecalculateAndPatch()` uncut default :1480-1490; `RecalculateAndPatchForCurrentTimelineIfFutureActions` :1537-1552; `GetKspPatchDeferralReason` :2503-2529 (no flush queue); `OnKspLoad` preserve logic :2697-2706; `DeduplicateAgainstLedger` :891-928 + `GetActionKey` :932-1038; `kscSequenceCounter` :90-96 (process-static, never persisted); `authoritativeReduction` sources :1940-1945; tech patch only with cutoff :2091-2118 | Campaign cutoff mandate, authoritative merges, merge trigger | Med | Pending |
| `Source/Parsek/GameActions/LedgerRolloutAdoption.cs` | Reassigns `Sequence` on existing rows (:93, :355/:391/:431) | Freeze for Exported rows | Low | Pending |
| `Source/Parsek/GameActions/LedgerRecoveryFundsPairing.cs` | Assigns `Sequence` | Freeze for Exported rows | Low | Pending |
| `Source/Parsek/GameActions/FundsModule.cs` | `ProcessFundsSpending` :319-348 (unconditional deduct); `ProcessFacilityCost` :473-486; `ProcessKerbalHire` :491-504; `ProcessStrategySetupCost` :509-525; `ProcessFundsInitial` :275-284 (sums seeds); `GetAvailableFunds` clamp :612-619 | Once-ever facility/hire/strategy keyed sets | Med | Pending |
| `Source/Parsek/GameActions/ScienceModule.cs` | `ProcessSpending` :292-316 (deducts only if affordable; `UnaffordableRunningScience`); subject caps :37/:214-277; `ProcessScienceInitial` :456-467 | Once-ever nodeId set | Low | Pending |
| `Source/Parsek/GameActions/FacilitiesModule.cs` | `ProcessUpgrade` :102-123 (absolute `Level = ToLevel`) | Once-ever (facilityId, toLevel) | Low | Pending |
| `Source/Parsek/GameActions/StrategiesModule.cs` | `ProcessActivate` :99-125 (duplicate id overwrites) | Once-ever setup cost by strategyId | Low | Pending |
| `Source/Parsek/GameActions/MilestonesModule.cs` | `creditedMilestones` per walk :21/:27-33/:52-113 | None (template) | - | - |
| `Source/Parsek/GameActions/ContractsModule.cs` | `PrePass` `nowUT = walkNowUT ?? lastActionUT` :291-292 (uncut walks drag the horizon) | None once cutoff mandate holds | - | - |
| `Source/Parsek/GameActions/KspStatePatcher.cs` | `PatchAll` :61-98; `BuildTargetTechIdsForPatch` :473-563 (baseline seed + affordable union); `SelectTechBaselineForPatch` :582-613; `ApplyDrawdownGuard` :3439-3467; `PatchFunds` clamp :1305 | Baseline pinning | Med | Pending |
| `Source/Parsek/GameStateStore.cs` | `CaptureBaselineIfNeeded` :1054-1068 (called at :636/:1549 commits + first load :5103); `SaveBaseline` :1345; `LoadBaselines` :1381-1394 | Pinning under campaign link | Low | Pending |
| `Source/Parsek/GameStateBaseline.cs` | `CaptureCurrentState` :44-130 snapshots LIVE tech (:57-69) | None | - | - |
| `Source/Parsek/MilestoneStore.cs` | Independent state; `CreateMilestone` :87-140 (called from `RecordingStore.cs:639/:1558`); `GetCommittedTechIds` :617-636; `GetCommittedKerbalHireNames` :695-714; never rebuilt from the ledger | Registration from EVENTS bundles | Med | Pending |
| `Source/Parsek/KerbalsModule.cs` | `ProcessAction` :250-320 (name opaque, roster never consulted); `ApplyToRoster` :1495-1840 (stand-ins only :1337-1365); `ReverseMapCrewNamesInSnapshot` :498+ | Foreign materialization hook, claim rule inputs | Med | Pending |
| `Source/Parsek/CrewReservationManager.cs` | `ReserveCrewIn` :62-119 (unknown name -> Warn :117-118, no creation) | Claim rule + substitution surfaces | Med | Pending |
| `Source/Parsek/VesselSpawner.cs` | `EnsureCrewExistInRoster` :3865-3908 (name-only creation, random stats; called :1133/:1632); `TryAdoptExistingSourceVesselForSpawn` :90-116; `RegenerateVesselIdentity` :1147/:1642; `SpawnOrRecoverIfTooClose` :1772 | Shared crew-creation core with attributes | Low | Pending |
| `Source/Parsek/PlaybackScopeTracker.cs` | `NotePlayhead` :34-92 (latch :53-58); `IsHistoricalNeverReplayed` :67-73; callers `ParsekFlight.cs:18467`, `ParsekKSC.cs:303`, `GhostMapPresence.cs:7198/9040`, `TimeJumpManager.cs:80-112` | Import-time latch | Low | Pending |
| `Source/Parsek/GhostPlaybackLogic.cs` | `ShouldSpawnAtRecordingEnd` :7243 (gate order incl. `TerminalSpawnSupersededByRecordingId` :7250, no-snapshot :7288-7300, non-leaf :7347-7350, terminal :7370-7374, dedup :7444); `IsSpawnableTerminal` :7143-7154 (Orbiting/Landed/Splashed only); `sit` fallback table :7181-7211 | MissingParts reject; local spawn-link override | Low | Pending |
| `Source/Parsek/ParsekFlight.cs` | `OnPartCouple` :10812 (partner snapshot capture :10859-10925, `pendingDockSelfSnapshot`); `HandleTreeDockMerge` :12311 (`pendingDockSelfSnapshot` cleared :12365); `CreateMergeBranch` :6269 (parent closed `Docked` :6292-6296, no fresh snapshot; merged snapshot :6319/:6331-6334; supersede stamp :6480-6485 never reverted); `CreateSplitBranch` :5541 (two children :4820-4883, both in active tree :5650-5651, bg child :5672-5676); `HandleTreeBoardMerge` :12442-12481 (parents `Boarded`); `TryFindCommittedTreeForSpawnedVessel` :14745; `TryTakeCommittedTreeForSpawnedVesselRestore` :14795 (pid promote :14866-14876); `SpawnVesselOrChainTip` :17842; spawn gate ANDs :18491-18500; `EvaluateAndApplyGhostChains` :11991-12031 (two call sites :11847/:11982); `InferTerminalStateFromTrajectory` :16031-16061 (+ :16065-16077); `CommitTreeFlight` :13503-13589; `RecoverTimelineSpawnedVessel` :16558 | Dock-time parent snapshot stamp; restore block; ghost-chain third trigger; terminal-recompute wiring | Med | Pending |
| `Source/Parsek/ParsekFlight.Finalization.cs` | Terminal recompute callers gated `isLeaf && !TerminalStateValue.HasValue` :447/:538-556 | Expose cores for salvage | Low | Pending |
| `Source/Parsek/ParsekScenario.Trim.cs` | `TrimRecordingPastUT` :15-82 (clips payload, not terminal/snapshots); `RemoveEmptyBranchPoints` :196-238 | Reuse in truncation | Low | Pending |
| `Source/Parsek/RecordingOptimizer.cs` | `SplitAtSection` moves terminal fields to the tail, nulls the head :1367-1394 | Reuse cut; recompute head terminal separately | Low | Pending |
| `Source/Parsek/EffectiveState.cs` | `EffectiveRecordingId` :111 (TODO at :109 proposes a cross-tree HALT - to be DELETED, not implemented); `ComputeERS` cache key :1380-1391; `ComputeELS` :1463-1473; O(N^2) note :918-929 | Masks at ERS level; TODO deletion | Med | Pending |
| `Source/Parsek/SupersedeCommit.cs` | `AppendRelations` :194; `CommitTombstones` :2302; relation ids :422 | Retirement export/import reuse | Low | Pending |
| `Source/Parsek/RewindInvoker.cs`, `TreeDiscardPurge.cs`, `LoadTimeSweep.cs` | Own destructive verbs; `LoadTimeSweep` orphan-row handling | Retirement emission hooks | Low | Pending |
| `Source/Parsek/Patches/GhostTrackingStationPatch.cs` | Ghost blocks: `FlyVessel` :620, `OnVesselDeleteConfirm` :725, `SetVessel` :765, `OnRecoverConfirm` :860; real vessels pass through | None for foreign spawns (no ownership guards); the TS Recover passthrough is where the Recover claim hook attaches for foreign spawns (verify own-vessel recovery marking at M4.11 plan time) | Low | Pending |
| `Source/Parsek/Patches/KscVesselMarkerFlyPatch.cs` (:42), `Patches/MapFocusObjectOnSelectPatch.cs`, `Patches/GhostTrackingStationPatch.cs` (`SwitchIntentTrackingStationFlyPatch`) | Arm `StockActionIntentMarker` on real-vessel Fly / Switch-To | Unchanged: the marker flows into `TryConsumeStockActionIntent`, whose new fourth branch handles foreign targets | - | - |
| `Source/Parsek/SwitchSegmentBuilder.cs`, `SwitchSegmentConsume.cs`, `SwitchSegmentNoOpClassifier.cs`, `SwitchSegmentSession.cs` | Continuation segment creation (parent or standalone), consume decision routes, no-op auto-discard, session marker | Foreign-continuation route: standalone tree with `ContinuesForeignRecordingId`; no-op discard reused verbatim | Med | Pending |
| `Source/Parsek/Logistics/RouteStore.cs` | `RevalidateSources` :1379 (ERS-based) ; endpoint resolution by pid | Endpoint ownership gate | Low | Pending |
| `Source/Parsek/RecordingGroupStore.cs` | `AutoGroupTreeRecordings` :71-150 (runs BEFORE recordings are in the list); `GenerateUniqueGroupName` :859-909 (local scan, `#N`) | Foreign name handling | Low | Pending |
| `Source/Parsek/MissionStore.cs` | `EnsureDefaultsForTrees` :43-68 (OnLoad + Missions draw, not CommitTree) | Read-only foreign seeding | Low | Pending |
| `Source/Parsek/Timeline/TimelineBuilder.cs` (`Build` :37-80), `UI/TimelineWindowUI.cs` (single Build site :417; NOW divider :1103-1116; no pagination :1084-1092) | Attribution + filter + debt text | Low | Pending |
| `Source/Parsek/UI/RecordingsTableUI.cs` | `sortedIndices` :266 (rebuild on count change only :4597-4600); `[ERS-exempt]` :1567-1575 | Owner badge, disabled controls | Low | Pending |
| `Source/Parsek/UI/SettingsWindowUI.cs`, `UI/KerbalsWindowUI.cs` | Settings sections; roster window | Multiplayer section; owner tags | Med | Pending |
| `Source/Parsek/GhostPlaybackEngine.cs` | Index-keyed state :41-81/:461; `ReindexAfterDelete` :8826-8841 (no insert variant) | None if import appends at end | - | - |
| `Source/Parsek/GhostMapPresence.cs` | Per-index dicts :205/:675/:716-720/:10982/:10995; TS spawn handoff :8844; protos 2/tick | Conditional per-peer cap (D14) | Low | Pending |
| `Source/Parsek/MissionLoopUnitBuilder.cs` | `BuildSignature` :1722-1732 hashes every id per frame | None (measure) | - | - |

New files (all under `Source/Parsek/Multiplayer/` unless noted): `PlayerIdentity.cs`, `ExchangeKeys.cs`, `CampaignCodec.cs`, `CampaignPaths.cs`, `CampaignStore.cs`, `SaveCloneRegenerator.cs`, `SettingsStatusText.cs`, `OwnershipFence.cs`, `PacketCodec.cs`, `PacketHasher.cs`, `PacketExporter.cs`, `PacketImporter.cs`, `ImportJournal.cs`, `MergeNotifier.cs`, `ClaimDerivation.cs`, `ConflictClassifier.cs`, `ArbitrationFold.cs`, `SalvageExecutor.cs`, `MultiplayerConstants.cs`; tests under `Source/Parsek.Tests/Multiplayer/` and `Generators/CampaignFixtureBuilder.cs`; `InGameTests/MultiplayerTests.cs`.

---

## 2. Dependency Map (coupling hotspots)

- `RecordingStore` <-> everything: registration order matters (`AutoGroupTreeRecordings` runs before the recordings are appended; `MarkSupersededTerminalSpawnsForContinuedSources` writes cross-tree local stamps; `FinalizeTreeCommit` bumps `StateVersion`, which keys `ComputeERS` and `DockEventGraphCache`).
- Spawn ownership is EMERGENT today (leaf-with-spawnable-terminal) plus one never-cleared stamp; the fold's reconciliation replaces emergence with an explicit per-vessel owner. Every spawn host (`ParsekFlight`, `GhostMapPresence` TS handoff, `ParsekKSC` :1900-2200, `SpawnTreeLeaves`) reads the same gate.
- The ledger has two walk regimes (uncut vs cutoff); under a campaign link only the cutoff regime is valid. Every merge-adjacent trigger must route through `RecalculateAndPatchForCurrentTimelineUT`-style cutoff entry points.
- `MilestoneStore`, `GameStateBaseline`, and the ledger are three independent stores that today agree only because one machine wrote all three; import must feed all three coherently (EVENTS bundle, pinned baseline, actions).

## 3. Existing Patterns to Reuse

- Additive scenario nodes: `SaveStagingList` / `LoadStagingList` (+ `RemoveNodes` registration).
- Safe-write: `FileIOUtils.SafeWriteConfigNode` / `SafeWriteBytes`; transient artifact naming (`.tmp`, `.stage.`, `.bak.`) recognized by orphan cleanup.
- Once-ever dedup: `MilestonesModule.creditedMilestones`, `ContractsModule.creditedContracts`, `ScienceModule` subject caps.
- Guard family: pid-set predicate + Harmony `Prefix` returning false + ScreenMessage + explicit input-lock release (`GhostTrackingStationPatch`, `GhostVesselLoadPatch`).
- Derived cross-tree links without foreign writes: `MissionCrossTreeDock.FindLinks` (`MissionCrossTreeDock.cs:70`), persisted only on the including side (`Mission.IncludedForeignDockLinkIds`).
- Dangling-reference tolerance: `RouteStore.RevalidateSources` -> `MissingSourceRecording`.
- Pure core + thin shell with `internal static` decision functions and `[Collection("Sequential")]` tests; log capture via `ParsekLog.TestSinkForTesting`.
- Crash-driven forward completion: `MergeJournalOrchestrator` (model for the import journal's idempotent re-apply).

## 4. Magic Values Audit (to centralize in `MultiplayerConstants`)

Poll interval (90 s), incomplete-packet staleness (24 h), shared-write retry count (3) and backoff, chunked-hash time budget per tick, protocol filenames (`campaign.cfg`, `highwater.cfg`, `player.cfg`, `.ppkt`, `checkpoints/`, `players/`, `packets/`), id prefixes (`p_`, `camp_`, `pkt_`), timestamp format (`yyyy-MM-ddTHH:mm:ssZ`), seq zero-padding width (6), PluginData identity path, ScreenMessage texts, worst-case protocol path suffix for headroom checks, default folder suggestion (`Multiparsek/`).

## 5. KSP API Surface

- Save structure: `SCENARIO { name = ContractSystem }` node (strip at join so KSP regenerates); `ROSTER { KERBAL { type = Applicant } }` rows (strip at join). Verify both node shapes against a real `persistent.sfs` before M1.6.
- `HighLogic.CurrentGame.CrewRoster.GetNewKerbal(KerbalType.Crew)` + `ChangeName` (existing creation path in `EnsureCrewExistInRoster`); attribute setters for trait/gender/veteran to be confirmed at M5.1 plan time.
- File attributes for cloud placeholders: `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000) / `FILE_ATTRIBUTE_OFFLINE` (0x1000) via `FileInfo.Attributes` (Windows); best-effort elsewhere.
- Windows path limits: MAX_PATH 260 on net472/Mono without long-path opt-in; measure headroom, do not assume.
- Stock Tracking Station entry points already patched for ghosts: `SpaceTracking.FlyVessel`, `OnVesselDeleteConfirm`, `SetVessel`, `OnRecoverConfirm`; KSC `KSCVesselMarkers.FlyVessel`; map `FlightGlobals.SetActiveVessel`.

## 6. Static Mutable State

`RecordingStore` (committed lists, `StateVersion`), `Ledger` (actions, `StateVersion`), `MilestoneStore`, `GameStateStore` baselines, `MissionStore`, `RouteStore`, `PlaybackScopeTracker`, `CrewReservationManager`, `ParsekLog` sinks, `LedgerOrchestrator.kscSequenceCounter`, `ParsekProcess.ProcessSessionId`; new: campaign link, import journal, export queue, fold outputs cache. All tests touching these use `[Collection("Sequential")]` and the corresponding `ResetForTesting()` calls.
