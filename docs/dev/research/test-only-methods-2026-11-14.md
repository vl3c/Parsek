# Test-only methods: the parked list (2026-11-14)

Owner rulings: the list as a whole is PARKED (2026-11-14 21:07Z: "keep the information about the
methods which are really dead and useless somewhere so we can easily find them when the time
comes"). The `really-dead` subset is APPROVED FOR REMOVAL as the final change of the current
refactor pass (2026-11-14 21:16Z), after the folder move, as its own vetted PR. Nothing was deleted
by the work that wrote this list. Todo entry: `DEAD-CODE-TEST-ONLY-METHODS-PARKED`.

## What the list is

Every production method in Parsek.dll that the uncalled-code fixpoint of `scripts/arch/ilanalysis.py`
(README "Code facts" -> "Uncalled code") does not reach from a root, at main 24abf0dba (Debug build).
Each one is a method the game never runs. B7 (PR #2577, 2026-11-12) deleted the uncalled methods
no test called; what it left is mostly kept alive by unit tests alone, plus the test hooks, plus a
few methods that only dead methods call. Each row carries a verdict, a one-line reason and, for
the `really-dead` rows, the tests and docs that go with the method.

Files:

- `docs/dev/research/test-only-methods-2026-11-14.tsv` - one row per method (773 rows), tab
  separated, ASCII. Columns:
  - `module` - the `scripts/arch/modules.toml` module of the declaring type.
  - `type`, `method` - the IL key without the `Parsek.` namespace prefix, signature included
    (`Type::Method(sig)`, nested types as `Outer/Inner`, property accessors as `get_X`). The key is
    the identity: it survives the folder move.
  - `fileHint` - the file under `Source/Parsek/` that declared it at 24abf0dba. A hint only;
    files are about to move.
  - `il` - instructions, generated code folded in (`ilanalysis.size`).
  - `nTests`, `tests` - how many test methods call it directly, and up to three of them.
  - `verdict`, `note` - below.
  - `deadTests` (really-dead rows only) - every test method that calls it, as
    `file::method[tag]`: `[only-dead]` when the test calls no other production method outside the
    really-dead set (constructors, accessors, `ParsekLog` and `*ForTesting` hooks not counted), so
    it goes with the method; `[edit:N-live]` when it also calls N live methods, so the deletion PR
    edits it rather than deleting it.
  - `docRefs` (really-dead rows only) - every line under `docs/`, `.claude/`, `harness/` and
    `scripts/` that names it as `Type.Method` or `Type::Method` (the arch pin files and
    `coverage_snapshot.json` left out).
  - `lastCaller` (really-dead rows only) - the newest commit `git log -S` lists where the count
    of `Method(` over `Source/Parsek` fell (a proxy for when its last caller went); `count never
    fell; added <commit>` when no listed commit lowered it (it never had a caller, or the caller
    went in a merge, which `git log -S` does not list); `n/a` when the name is shared with live
    code.
- This document.

## How to regenerate

The mechanical columns (module, method, il, nTests) come back with these commands from the repo
root on a Linux box with mono-devel (`ikdasm`) and the KSP reference DLLs (`KSPDIR`):

```bash
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj -p:SkipKspDeploy=true
python3 scripts/arch/ilfacts.py Source/Parsek/bin/Debug/Parsek.dll --json /tmp/prod.json --summary
python3 scripts/arch/ilfacts.py Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll \
    --modules none --json /tmp/tests.json
python3 scripts/arch/ilanalysis.py /tmp/prod.json --tests /tmp/tests.json --no-history   # the counts
python3 - /tmp/prod.json /tmp/tests.json > /tmp/test-only-methods.tsv <<'EOF'
import sys
sys.path.insert(0, "scripts/arch")
import ilanalysis
prod, tooling = ilanalysis.load_facts(sys.argv[1])
tests = ilanalysis.load_facts(sys.argv[2], None)[0]
config = ilanalysis.load_config()
index = ilanalysis.Index(prod, tooling)
callers, _ = ilanalysis.test_callers(index, tests)
dead = ilanalysis.uncalled(index, config, set(callers))
suffixes = config["uncalled"]["test_hook_suffixes"]
for k in sorted(dead["uncalled"]):
    e = prod["methods"][k]
    print("\t".join([index.module(e["type"]), k[len("Parsek."):] if k.startswith("Parsek.") else k,
                     str(ilanalysis.size(e)), str(len(callers.get(k, ()))),
                     "hook" if ilanalysis.is_test_hook(k, suffixes) else ""]))
EOF
```

At 24abf0dba that snippet prints exactly the 773 `module` / `method` / `il` / `nTests` values of
the committed TSV (checked by a join on `method`). The verdicts and notes are judgment; to refresh
them on a later tree, join the new output to this TSV on `method`, keep the verdicts of rows that
are still there, and judge only the new rows. A method that left the new output became live again
or was deleted.

## Reconciled counts

At 24abf0dba (Debug), against `scripts/arch/facts_pins.toml`:

| Count | Value |
|---|---:|
| methods the roots do not reach | 773 (17,333 instructions) |
| ... no unit test calls either | 76 |
| ... a unit test calls | 697 |
| hook-named (`test_hook_suffixes`: `ResetForTesting`, `ForTesting`, `ForTests`) | 282 (252 a test calls, 30 orphaned) |
| the ratchet: uncalled minus hook-named | 773 - 282 = 491 |
| `[uncalledInAssembly] bound` in `facts_pins.toml` | 491 (equal) |

The 491 non-hook rows split as 445 a test calls and 46 no test calls (reached only through other
dead methods, or pinned by a source gate). The README's B7 line (787 methods, 17,484 instructions,
78 without a test) was measured on the tree B7 left; main has moved since.

## Verdicts

- `test-hook` - exists for tests by design: every hook-named method (282), plus the non-hook test
  observers, override resets and test setup entry points (`get_PendingCount`,
  `ResetVesselExistsOverride`, `RegisterPreviewRecordingForTailLift` ...). Not a deletion
  candidate. The 30 hook-named methods no test calls are listed below.
- `tested-helper-of-live-code` - a dead method whose logic the live code runs in another form: a
  pure helper the live path inlines (`GhostPartEventApplyTally::RenderLines`), or a method whose
  body a source gate pins verbatim (the `ParsekUI` hover check). Its tests check live behavior.
- `dead-but-tests-guard-live-logic` - no live caller, but its tests are the coverage of live
  logic: an overload, wrapper or forwarder into a live method (a forwarder into live logic is
  always this verdict, never `really-dead`), a bool view of a live classifier, or a cluster whose
  tests pin a live contract. The note names the live twin. Deleting one means re-pointing its
  tests at the twin first.
- `kept-by-design` - a recorded keep decision (a grep gate, a test's exemption list, a design
  doc section the harness cites) or a name read by reflection. The note says what records it and
  whether it is a root-rule gap (none of the 32 is: a root-rule gap would be a method the game
  does run that the fixpoint misses).
- `unsure` - 4 rows that need an owner call; the note says why.
- `really-dead` - deletion-grade (below).

**`really-dead` means all of these were checked and hold:**

1. No production caller: the IL fixpoint (InGameTests count as production callers there) and a
   comment-stripped identifier scan of `Source/Parsek` (string literals blanked) find no call
   outside the declaration and other really-dead methods. Every production caller of every
   really-dead method is itself really-dead, so the set deletes as a unit; a method a non-dead
   method still calls was moved out (`TombstoneEligibility::TryPairBundledRepPenalty`, called by
   the kept world-action cluster).
2. Not reached by name: no really-dead name appears in a string literal under `Source/` except its
   own log lines and test assertion messages; no `[KSPEvent]` / `[KSPAction]` / `[KSPField]` /
   Harmony / `InGameTest` attribute (the only attribute on any of them is the compiler's
   `IteratorStateMachine` on `OutlierFlags::get_RejectedSampleIndices`); no Unity message name;
   no override or interface implementation (the `ITrajectorySample` members were moved out); no
   M-A2 seam verb table, harness source-sync cell or `ledgeraudit` anchor reads one of their names
   or literals (every literal of every really-dead method was searched under `harness/`,
   `scripts/`, `Source/Parsek.Tests` and `docs/`: only shared subsystem tags matched).
3. No logic lost: every live method a really-dead method calls is still reached from some test
   that survives the deletion (a call-graph walk from the test-called methods outside the set).
4. No keep recorded: no grep gate, test exemption list, design section the harness cites, or doc
   comment above the declaration (scanned for "kept", "retained", "future", "reserved",
   "intentionally" ...) keeps it on purpose.

When a row failed any check, or a keep was recorded anywhere, it is not `really-dead`.

## Summary

Per verdict (methods, instructions folded):

| Verdict | Methods | IL instructions |
|---|---:|---:|
| really-dead | 124 | 6387 |
| dead-but-tests-guard-live-logic | 196 | 5219 |
| tested-helper-of-live-code | 18 | 460 |
| kept-by-design | 32 | 930 |
| unsure | 4 | 296 |
| test-hook | 399 | 4041 |
| total | 773 | 17333 |

Per module (methods per verdict, and the instructions of the really-dead rows):

| Module | dead | guard | helper | kept | unsure | hook | total | dead IL |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Config | 0 | 1 | 0 | 0 | 0 | 20 | 21 | 0 |
| Controllers | 29 | 31 | 0 | 0 | 0 | 67 | 127 | 1849 |
| Core | 3 | 1 | 0 | 0 | 0 | 2 | 6 | 158 |
| Crew | 2 | 4 | 0 | 0 | 0 | 7 | 13 | 78 |
| Display | 0 | 1 | 0 | 0 | 0 | 3 | 4 | 0 |
| GameActions | 14 | 47 | 0 | 1 | 0 | 50 | 112 | 548 |
| Ghost | 26 | 23 | 1 | 1 | 1 | 52 | 104 | 1400 |
| LogIO | 0 | 0 | 0 | 0 | 0 | 4 | 4 | 0 |
| Logistics | 1 | 9 | 1 | 2 | 0 | 5 | 18 | 32 |
| MapRender | 12 | 5 | 4 | 26 | 3 | 24 | 74 | 253 |
| Missions | 2 | 2 | 0 | 0 | 0 | 14 | 18 | 90 |
| Patches | 0 | 1 | 0 | 0 | 0 | 5 | 6 | 0 |
| Reaim | 3 | 1 | 0 | 1 | 0 | 1 | 6 | 164 |
| Recording | 17 | 29 | 0 | 0 | 0 | 98 | 144 | 896 |
| Rendering | 3 | 2 | 0 | 0 | 0 | 8 | 13 | 239 |
| Rewind | 3 | 27 | 2 | 1 | 0 | 14 | 47 | 155 |
| SwitchIntent | 0 | 1 | 0 | 0 | 0 | 0 | 1 | 0 |
| Trajectory | 0 | 1 | 0 | 0 | 0 | 1 | 2 | 0 |
| UI | 9 | 10 | 10 | 0 | 0 | 24 | 53 | 525 |

## Ready to delete

The really-dead set by module, as the deletion PR would take it (method names only, one entry
per signature, so an overloaded name can appear twice; the TSV has the signatures and the
tests).

- **Controllers** (29 methods, 1849 IL): `ForeignInventoryTransfer::CollectTransferMemberRecordingIds`, `ParsekFlight::BuildRecordedAnchorFallbackGapLog`, `ParsekFlight::DistanceOutsideRecordedAnchorCoverage`, `ParsekFlight::IsTrackableVesselType`, `ParsekFlight::RecordedAnchorPointListCoversUT`, `ParsekFlight::ResolveBodyFixedPrimaryPlaybackFrames`, `ParsekFlight::ResolveDockRouteTargetPid`, `ParsekFlight::ShouldSkipMutableActiveReFlyAnchorCandidate`, `ParsekFlight::ShouldUseDenseReFlyAbsolutePlaybackFrames`, `ParsekFlight::ShouldUsePreReFlyAnchorTrajectory`, `ParsekFlight::ShouldWarnRecordedAnchorFallbackGap`, `ParsekFlight::TryFindBodyFixedPrimaryBridgeFrame`, `ParsekFlightGhostPositioner::CountDensebodyFixedFramesForSection`, `ParsekFlightGhostPositioner::PointBelongsToSection`, `ParsekFlightGhostPositioner::TryGetBracketWidthSeconds`, `ParsekFlightGhostPositioner::TryGetDenseSectionBracketWidthSeconds`, `ParsekFlightPostSwitchAutoRecord::HasMeaningfulCrewDelta`, `ParsekFlightPostSwitchAutoRecord::HasMeaningfulInventoryDelta`, `ParsekFlightPostSwitchAutoRecord::HasMeaningfulPartStateChange`, `ParsekFlightPostSwitchAutoRecord::HasMeaningfulPartStateTokenChange`, `ParsekFlightPostSwitchAutoRecord::HasMeaningfulResourceDelta`, `ParsekPlaybackPolicy::StartsInOrbit`, `TimeJumpManager::ComputeJumpTargetUT`, `VesselSpawner::ComputeRecordedTerminalOrbitMeanAnomalyAtUT`, `VesselSpawner::ResolveRelocatedAltitude`, `VesselSpawner::SelectRelocatedAltitude`, `VesselSpawner::ShouldRegenerateIdentity`, `VesselSpawner::TryApplyPreferredSpawnRotation`, `VesselSpawner::TryGetPreferredRecordedOrbitSeedForSpawn`
- **Core** (3 methods, 158 IL): `GroupHierarchyStore::CollectDescendants`, `GroupHierarchyStore::GetDescendantGroups`, `MilestoneStore::GetPendingEventCount`
- **Crew** (2 methods, 78 IL): `CrewReservationManager::ClearReplacements`, `CrewReservationManager::ShouldProcessCrewForReservation`
- **GameActions** (14 methods, 548 IL): `GameStateFacilityRecorder::CheckBuildingTransitions`, `GameStateFacilityRecorder::CheckFacilityTransitions`, `GameStateRecorder::CheckBuildingTransitions`, `GameStateRecorder::CheckFacilityTransitions`, `GameStateRecorder::CreateMilestoneEvent`, `GameStateStore::ClearBaselines`, `GameStateStore::ClearEvents`, `GameStateStore::ClearScienceSubjects`, `GameStateStore::GetUncommittedEventCount`, `GameStateStore::RestoreScienceInRnD`, `KspStatePatcher::ResolveFundsPatchTarget`, `KspStatePatcher::ResolveNegativeCurrencyPatchTarget(string,float64,float64,float64,float64,bool)`, `KspStatePatcher::ResolveSciencePatchBaseTarget`, `StrategiesModule::IsManagedStrategy`
- **Ghost** (26 methods, 1400 IL): `GhostAudioPresets::ResolveOneShotClip`, `GhostChain::ClaimsPid`, `GhostMapPresence::ComputeGhostDisplayInfo`, `GhostMapPresence::FindSupersededRecordingIds`, `GhostMapPresence::StartsInOrbit`, `GhostPlaybackEngine::IsGhostWithinVisualRange`, `GhostPlaybackEngine::OnAnchorVesselLoaded`, `GhostPlaybackEngine::OnAnchorVesselUnloaded`, `GhostPlaybackEngine::TryConvertLegacyGapToLoopPeriodSeconds`, `GhostPlaybackLogic::ComputeLoopPhaseFromUT`, `GhostPlaybackLogic::ComputeTargetResourceIndex`, `GhostPlaybackLogic::ComputeWatchCycleOnLoopRebuild`, `GhostPlaybackLogic::FindImmediateChainContinuationIndex`, `GhostPlaybackLogic::IsAnchorLoaded`, `GhostPlaybackLogic::ResolveEffectiveTreeWatchTargetIndex`, `` GhostPlaybackLogic::ResolveEffectiveWatchTargetIndex(int32,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingTree>,System.Func`2<int32,bool>) ``, `` GhostPlaybackLogic::ResolveEffectiveWatchTargetIndex(int32,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingTree>,System.Func`2<int32,bool>,System.Collections.Generic.HashSet`1<int32>,int32) ``, `GhostPlaybackLogic::ShouldExitWatchForCutoff`, `GhostPlaybackLogic::ShouldForceWatchedFullFidelity`, `GhostPlaybackLogic::ShouldLoopPlayback`, `GhostPlaybackLogic::ShouldPauseTimelineResourceReplay`, `GhostPlaybackLogic::TryComputeLoopPlaybackUT`, `GhostPlaybackLogic::ValidateLoopAnchor`, `RenderingZoneManager::LogLoopedGhostSpawnDecision`, `RenderingZoneManager::ShouldRenderMesh`, `RenderingZoneManager::ShouldRenderPartEvents`
- **Logistics** (1 methods, 32 IL): `Logistics.RouteOriginCargoCheck::RequiresInventoryDebit`
- **MapRender** (12 methods, 253 IL): `MapRender.OrbitalState::FromSegment`, `MapRender.PhaseId::get_IsEmpty`, `MapRender.PhaseSeam::get_RequiresTangentMatch`, `MapRender.RenderCompositionManifest::CloseOpenDwell`, `MapRender.RenderGeometrySampler::FlattenSingle`, `MapRender.RenderGeometrySampler::ShiftSampleUTs`, `MapRender.RenderGeometrySampler::ToBodyRelative`, `MapRender.RenderParityOracle::ParityModeToken`, `MapRender.StockConicTreatment::get_Kind`, `MapRender.TracedPathTreatment::get_Kind`, `MapRenderTrace::Decision`, `MapRenderTrace::EvaluateGate`
- **Missions** (2 methods, 90 IL): `MissionEventDigest::FormatRowText`, `MissionEventDigest::LowerFirst`
- **Reaim** (3 methods, 164 IL): `Reaim.TransferWindowMath::HohmannPhaseAngleTargetDegrees`, `Reaim.TransferWindowMath::NextDepartureUT`, `Reaim.TransferWindowMath::TimeToNextWindowSeconds`
- **Recording** (17 methods, 896 IL): `AnchorDetector::FindNearestAnchor`, `AnchorDetector::IsInDockingApproach`, `BackgroundRecorder::IsOrbitalCheckpointSituation`, `BackgroundRecorder::ShouldStopDebrisRecording`, `ChainTipStaleVessel::FindChainForTip`, `ProximityRateSelector::GetSampleRateHz`, `RecordingFinalizationCacheProducer::TryBuildAtmosphericDeletionCache`, `RecordingGroupStore::MarkAutoAssignedStandaloneGroup`, `RecordingOptimizer::IsUnstableTerminalState`, `RecordingSidecarStore::TryProbeSnapshotSidecar`, `RecordingStore::RefreshQuicksaveAfterMerge`, `RecordingTree::FindDuplicateBackgroundMapPids`, `RelativeAnchorResolution::Decide`, `SectionWarpRuns::PhysicsFrameCount`, `SegmentBoundaryLogic::IsControllerPart`, `SessionMerger::AnchorIdentityKey`, `TreeCommitService::FindBranchPointById`
- **Rendering** (3 methods, 239 IL): `Rendering.OutlierFlags::Empty`, `Rendering.OutlierFlags::UnpackBitmap`, `Rendering.OutlierFlags::get_RejectedSampleIndices`
- **Rewind** (3 methods, 155 IL): `` EffectiveState::IsRewindRetired(Parsek.Recording,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingRewindRetirement>) ``, `ReFlySwitchGate::FirstAllowedIndex`, `RewindSpawnScope::IsStableState`
- **UI** (9 methods, 525 IL): `ParsekUI::HeaderToCellTextDeltaPx`, `RecordingsTableFormatters::FormatDistance`, `RecordingsTableUI::FormatDistance`, `RecordingsTableUI::ScrollToRecording`, `SelectiveSpawnUI::IsSpawnCandidate`, `TimelineWindowUI::ScrollToCareerSubject`, `UI.Gallery.GuiMockSession::NoteScopeBroken`, `WideWindowLayout::ClampScrollX`, `WideWindowLayout::MaxScrollX`

How the deletion PR should go (after the folder move):

1. Rebuild and regenerate the list (commands above); drop any row that became live.
2. Delete the methods module by module. Delete each `[only-dead]` test and edit each
   `[edit:N-live]` test (it also covers live code). 68 test calls are edits and 311 deletes.
3. Gates that name a really-dead method in a NEGATIVE assertion stay green after the deletion but
   lose their subject: `RecordingsTableUITests` (asserts the row and group watch blocks do not
   name `ResolveEffectiveWatchTargetIndex`) and `TimelineGoToMissionTests` (asserts no
   `tableUI.ScrollToRecording`). Keep or reword them.
4. Edit the docs the `docRefs` column names outside `docs/dev/done/` (archives keep history):
   `docs/parsek-logistics-supply-routes-design.md` (`RequiresInventoryDebit`), `docs/dev/autotest-status.md` and
   `docs/dev/design-load-restores-the-save.md` (`RefreshQuicksaveAfterMerge`),
   `docs/dev/design-ui-basic-advanced.md` (`RecordingsTableUI.ScrollToRecording`),
   `docs/dev/design-gui-inventory.md` (`ParsekUI.HeaderToCellTextDeltaPx`), the comment in
   `harness/scenarios/V8-eve-player-loop.toml` (`HohmannPhaseAngleTargetDegrees`), and the two
   2026-09-11 GUI research notes. No CLAUDE.md line names a really-dead method.
5. Re-pin `[uncalledInAssembly]` (it falls by the number deleted) and the coverage snapshot
   inputs if they move; the `scripts/refactor/split_*.py` one-shot scripts name some of these
   methods in their move tables and are historical, so they need no edit.

## Really-dead, largest first

Tests: up to three of the test methods that go with it (the TSV lists all, with files).

| # | Method | IL | Tests that go with it | What it was / when it went |
|---:|---|---:|---|---|
| 1 | `RecordingsTableUI::ScrollToRecording(string)` | 254 | ExpandsTheTargetsGroupAndEveryAncestor[only-dead]; HandlesATargetWithNoGroups[only-dead]; LeavesHiddenGroupsAloneWhenTheFilterIsOff[edit:2-live]; +9 more | Recordings-tab cross-link scroll; its last caller went with f8bc370b5 (Timeline GoTo targets the mission, not the hidden Recordings tab); RecordingsTableApiTests cover only it |
| 2 | `` GhostPlaybackLogic::ResolveEffectiveTreeWatchTargetIndex(Parsek.Recording,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingTree>,System.Func`2<int32,bool>,System.Collections.Generic.HashSet`1<int32>,int32) `` | 204 | - | only the dead ResolveEffectiveWatchTargetIndex chain calls it |
| 3 | `` ParsekFlight::ShouldUseDenseReFlyAbsolutePlaybackFrames(System.Collections.Generic.List`1<Parsek.TrackSection>,int32,Parsek.TrackSection,System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,float64,int32&,float64&,float64&,string&) `` | 180 | ShouldUseDenseReFlyAbsolutePlaybackFrames_DenseAbsoluteSectionBracket_ReturnsTrue[only-dead]; ShouldUseDenseReFlyAbsolutePlaybackFrames_DenseBracketNotTighter_ReturnsFalse[only-dead]; ShouldUseDenseReFlyAbsolutePlaybackFrames_ExcludesBoundaryPointsOwnedByNextSection[only-dead]; +1 more | dense Re-Fly absolute-frame decision (75221b880); no live caller |
| 4 | `VesselSpawner::TryGetPreferredRecordedOrbitSeedForSpawn(Parsek.Recording,float64&,float64&,float64&,float64&,float64&,float64&,float64&,string&)` | 166 | TryGetPreferredRecordedOrbitSeedForSpawn_PrefersLastOrbitSegment[only-dead] | endpoint-aligned spawn orbit seed (9ed5c9456); no live caller |
| 5 | `GhostPlaybackLogic::ComputeLoopPhaseFromUT(float64,float64,float64,float64)` | 162 | AnchorLoaded_PhaseComputed_AtMidCycle[only-dead]; AnchorReloaded_PhaseRecomputed_NotFromStart[only-dead]; ComputeLoopPhaseFromUT_IntMaxBoundary_HandledCorrectly[only-dead]; +20 more | pre-engine loop phase clock (03d47e507 era); the live loop clock is the LoopUnit path; no live caller |
| 6 | `ParsekFlight::ResolveBodyFixedPrimaryPlaybackFrames(Parsek.IPlaybackTrajectory,Parsek.TrackSection,float64)` | 158 | ResolveBodyFixedPrimaryPlaybackFrames_TargetAfterShadow_ReturnsOriginalFrames[only-dead] | body-fixed primary frame resolver left on ParsekFlight after the PF2 positioner lift; no live caller |
| 7 | `Rendering.OutlierFlags::get_RejectedSampleIndices()` | 137 | RejectedSampleIndices_EnumeratesAscending[edit:1-live] | iterator over rejected samples; no live reader |
| 8 | `GhostMapPresence::ComputeGhostDisplayInfo(Parsek.GhostChain,string)` | 132 | ComputeGhostDisplayInfo_ActiveChain_ShowsSpawnUT[only-dead]; ComputeGhostDisplayInfo_BlockedChain_ShowsBlocked[only-dead]; ComputeGhostDisplayInfo_NullChain_ReturnsSafeDefaults[only-dead]; +2 more | Phase 6f ghost display info builder (4a7af303b); no live caller |
| 9 | `` ParsekFlightGhostPositioner::TryGetDenseSectionBracketWidthSeconds(System.Collections.Generic.List`1<Parsek.TrackSection>,int32,Parsek.TrackSection,System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,float64,float64&) `` | 131 | DenseBracketWidth_MissingSideOrNoPoints_False[only-dead]; DenseBracketWidth_NearestPointsInSectionAroundUt[only-dead] | only the dead ShouldUseDenseReFlyAbsolutePlaybackFrames calls it |
| 10 | `RecordingStore::RefreshQuicksaveAfterMerge(string,int32)` | 121 | RefreshQuicksaveAfterMerge_InvokesSaveGameExactlyOnce[only-dead]; RefreshQuicksaveAfterMerge_NormalCall_InvokesSaveGame_LogsAtInfo[only-dead]; RefreshQuicksaveAfterMerge_SaveGameReturnsNull_LogsWarn[only-dead]; +1 more | quicksave refresh on merge/discard, retired by f74523ea4 (stop writing the player's quicksave on merge and discard); docs still name it historically |
| 11 | `ParsekFlight::TryFindBodyFixedPrimaryBridgeFrame(Parsek.IPlaybackTrajectory,Parsek.TrackSection,float64,Parsek.TrajectoryPoint&)` | 113 | TryFindBodyFixedPrimaryBridgeFrame_UsesPriorAbsoluteSectionBoundary[only-dead] | only the dead ResolveBodyFixedPrimaryPlaybackFrames calls it |
| 12 | `RenderingZoneManager::LogLoopedGhostSpawnDecision(string,float64,bool,bool)` | 106 | LogLoopedGhostSpawn_FullFidelity_LogsCorrectly[only-dead]; LogLoopedGhostSpawn_Simplified_LogsCorrectly[only-dead]; LogLoopedGhostSpawn_Suppressed_LogsCorrectly[only-dead]; +3 more | looped-ghost spawn log of the old playback path (T25 Phase 9, d2e26d3ca); no live caller |
| 13 | `VesselSpawner::ResolveRelocatedAltitude(Parsek.Recording,CelestialBody,float64,float64)` | 104 | RelocatedAltitude_NotLandedLike_UsesTheSnapshotAltElseZero[only-dead] | relocated spawn altitude from the bounding-box collision work (e6ac78016); no live caller |
| 14 | `` AnchorDetector::FindNearestAnchor(uint32,Vector3d,System.Collections.Generic.List`1<System.ValueTuple`2<uint32,Vector3d>>,System.Collections.Generic.HashSet`1<uint32>) `` | 101 | FindNearestAnchor_LogsWhenAnchorFound[only-dead]; FindNearestAnchor_NoLogWhenNoAnchor[only-dead]; FindNearestAnchor_AllVesselsInTree_ReturnsZero[only-dead]; +14 more | pid-based nearest-anchor search from P3a-2; anchors are found by recording id since f5cf3b68f (FindNearestRecordingAnchor); no live caller |
| 15 | `` GhostPlaybackLogic::ResolveEffectiveWatchTargetIndex(int32,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingTree>,System.Func`2<int32,bool>,System.Collections.Generic.HashSet`1<int32>,int32) `` | 101 | - | the full resolver behind the 4-arg overload (dead since #382) |
| 16 | `KspStatePatcher::ResolveNegativeCurrencyPatchTarget(string,float64,float64,float64,float64,bool)` | 100 | - | logging overload reached only from the two dead Resolve*PatchTarget methods; no test calls it |
| 17 | `GameStateStore::RestoreScienceInRnD()` | 97 | - | R&D science restore of the removed Wipe All (226c3116d); only ClearScienceSubjects (dead) calls it |
| 18 | `GhostPlaybackLogic::TryComputeLoopPlaybackUT(float64,float64,float64,float64,float64&,int64&)` | 94 | TryComputeLoopPlaybackUT_LargeElapsed_NoOverflow[only-dead]; BoundaryConsistency_WithTryComputeLoopPlaybackUT[only-dead]; TryComputeLoopPlaybackUT_PeriodGreaterThanDuration_InPause[only-dead]; +3 more | pre-engine loop playback UT (lifted with PF4, c0a51b4ca, but no live caller); 6 tests cover only it |
| 19 | `ParsekFlight::ShouldUsePreReFlyAnchorTrajectory(Parsek.Recording,uint32,string,Parsek.ReFlySessionMarker)` | 93 | ShouldUsePreReFlyAnchorTrajectory_RequiresActiveInPlaceRecording[edit:1-live] | pre-Re-Fly anchor trajectory decision; left by B7 because a test calls it |
| 20 | `Reaim.TransferWindowMath::TimeToNextWindowSeconds(float64,float64,float64,float64)` | 91 | NextDepartureUT_AddsTimeToNextWindow[only-dead]; TimeToNextWindow_EqualPeriods_Infinity[only-dead]; TimeToNextWindow_JustMissed_WrapsToNearlyAFullSynodic[edit:1-live]; +1 more | synodic wait time; only NextDepartureUT (dead) calls it |
| 21 | `` ForeignInventoryTransfer::CollectTransferMemberRecordingIds(System.Collections.Generic.IEnumerable`1<Parsek.RecordingTree>) `` | 90 | MemberIds[only-dead]; AnEarlierFlightsSpawnedLoosePart_IsKeptForTheHold_NotStripped[edit:2-live]; Control_TransferMembersOnly_StripsTheBoardedVessel[edit:2-live] | transfer-member id collector for the rewind strip (ed93fe996); the rewind now holds such vessels (a055f1c6e) and nothing calls it |
| 22 | `` GhostPlaybackLogic::FindImmediateChainContinuationIndex(Parsek.Recording,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>) `` | 85 | - | only the dead ResolveEffectiveWatchTargetIndex chain calls it |
| 23 | `` GameStateFacilityRecorder::CheckFacilityTransitions(System.Collections.Generic.Dictionary`2<string,float32>,System.Collections.Generic.Dictionary`2<string,float32>,float64) `` | 80 | - | facility-level transition diff; only the dead GameStateRecorder forwarder calls it |
| 24 | `MilestoneStore::GetPendingEventCount()` | 79 | GetPendingEventCount_CountsVisibleCommittedEvents[edit:2-live]; GetPendingEventCount_ExcludesFilteredEvents[edit:2-live]; GetPendingEventCount_ExcludesHiddenMilestones[only-dead]; +2 more | count for the old Actions-button badge (a GameStateStore comment still names it); no live caller |
| 25 | `` ParsekFlightPostSwitchAutoRecord::HasMeaningfulPartStateTokenChange(System.Collections.Generic.ICollection`1<string>,System.Collections.Generic.ICollection`1<string>) `` | 79 | HasMeaningfulPartStateTokenChange_DetectsDigestDifferences[only-dead] | post-switch delta predicate; no live caller |
| 26 | `RecordingSidecarStore::TryProbeSnapshotSidecar(string,Parsek.SnapshotSidecarProbe&)` | 79 | LoadSnapshotSidecar[only-dead]; ScenarioWriter_WritesExpectedSidecars_ForRepresentativeFixture[edit:3-live]; SnapshotSidecar_CompressedFormat_UsesOptimalCompression_AndRoundTrips[only-dead]; +5 more | logged probe over SnapshotSidecarCodec.TryProbe; no live caller (a todo entry lists it among moved members) |
| 27 | `` EffectiveState::IsRewindRetired(Parsek.Recording,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingRewindRetirement>) `` | 76 | IsRewindRetired_RawOverload_ReturnsFalseForParentAnchoredChild[only-dead] | raw (no closure) retirement check; the live 3-arg overload computes the closure instead and never calls it; one test pins the raw overload itself |
| 28 | `UI.Gallery.GuiMockSession::NoteScopeBroken(Parsek.UI.Gallery.GuiMockSuppressionSite,string)` | 75 | NoteScopeBrokenIsAOneShotAndIsInertWithoutAMatchingSession[edit:2-live] | one-shot "mock scope broken" warning; its call site went with the Career window removal (6bc541443); brokenReason is then never written |
| 29 | `GhostPlaybackEngine::TryConvertLegacyGapToLoopPeriodSeconds(Parsek.IPlaybackTrajectory,float64,float64&,float64&)` | 74 | TryConvertLegacyGapToLoopPeriodSeconds_NoDuration_False[only-dead]; TryConvertLegacyGapToLoopPeriodSeconds_NonFiniteGap_FalseAndGapEchoed[only-dead]; TryConvertLegacyGapToLoopPeriodSeconds_PeriodIsDurationPlusGap[only-dead]; +1 more | legacy loop-gap to period conversion (schema reset era, 4a99b4ea6); no live caller |
| 30 | `Rendering.OutlierFlags::UnpackBitmap(uint8[],int32)` | 74 | PackedBitmap_RoundTrip_AtVariousLengths[edit:1-live] | bitmap unpack (live code reads bits through IsRejected); only a round-trip test |
| 31 | `RecordingTree::FindDuplicateBackgroundMapPids()` | 71 | BackgroundMapEligibility_IgnoresOptimizerSplitIntermediateChainSegments[edit:2-live]; FindDuplicateBackgroundMapPids_ReturnsEligibleDuplicatesOnly[only-dead] | duplicate background-map pid finder (70f6c28e5); no live caller |
| 32 | `` GameStateFacilityRecorder::CheckBuildingTransitions(System.Collections.Generic.Dictionary`2<string,bool>,System.Collections.Generic.Dictionary`2<string,bool>,float64) `` | 69 | - | building-transition diff (8c63c0a5e); only the dead GameStateRecorder forwarder calls it |
| 33 | `CrewReservationManager::ClearReplacements()` | 68 | ClearReplacements_NullRoster_ClearsRescuePlacedMarkerSet[edit:2-live] | bulk wipe of crew replacements + rescue markers against the live roster; no live caller (the reset paths use ResetReplacementsForTesting / the cold reset) |
| 34 | `` TimelineWindowUI::ScrollToCareerSubject(Parsek.TimelineCareerCategory,string,System.Nullable`1<Game/Modes>) `` | 68 | ScrollToCareerSubject_HiddenCategoryKeepsTheView[only-dead]; ScrollToCareerSubject_OpensTheWindowOnTheCategoryView[only-dead] | Career-window cross-link into the Timeline; the Career window was removed (6bc541443) |
| 35 | `TreeCommitService::FindBranchPointById(Parsek.RecordingTree,string)` | 63 | FindBranchPointById_MatchesOrdinalAndSkipsNulls[only-dead] | branch-point lookup; no live caller |
| 36 | `GameStateStore::GetUncommittedEventCount()` | 59 | GetUncommittedEventCount_AllUncommittedWhenNoMilestones[edit:1-live]; GetUncommittedEventCount_CountsEventsAfterLastMilestone[edit:2-live]; GetUncommittedEventCount_IgnoresHiddenTaggedEvents[edit:2-live]; +1 more | uncommitted count for the old Actions badge (d1ba5b291 / 8c16f1440); no live caller |
| 37 | `MapRenderTrace::EvaluateGate(float64,float64,bool,bool,bool,bool)` | 59 | EvaluateGate_FirstSeen_EmitsInitialWindow[only-dead]; EvaluateGate_Force_EmitsImportantWithForceReason[only-dead]; EvaluateGate_Important_EmitsImportantWithImportantReason[only-dead]; +3 more | Tier-B emit gate (force / important / initial window / window); no live caller |
| 38 | `` TimeJumpManager::ComputeJumpTargetUT(System.Collections.Generic.Dictionary`2<uint32,Parsek.GhostChain>,uint32) `` | 59 | ComputeJumpTargetUT_EarlierIndependentTips_ReturnsSelectedSpawnUT[only-dead]; ComputeJumpTargetUT_Logs[only-dead]; ComputeJumpTargetUT_NonExistentPid_ReturnsZero[only-dead]; +2 more | warp-to-spawn jump target from the pre-redesign Spawn Control (163f1fade, redesigned in f16423dd4); no live caller |
| 39 | `` GroupHierarchyStore::CollectDescendants(string,System.Collections.Generic.List`1<string>,int32) `` | 58 | - | recursive walk of groupParents; only GetDescendantGroups (dead) calls it |
| 40 | `VesselSpawner::ComputeRecordedTerminalOrbitMeanAnomalyAtUT(Parsek.Recording,float64,float64)` | 56 | ComputeRecordedTerminalOrbitMeanAnomalyAtUT_InvalidInputs_UsesRecordedValue[only-dead]; ComputeRecordedTerminalOrbitMeanAnomalyAtUT_ShiftsToSpawnUT[only-dead] | terminal-orbit mean anomaly at a UT (a0864c3b3); no live caller |
| 41 | `` ChainTipStaleVessel::FindChainForTip(System.Collections.Generic.Dictionary`2<uint32,Parsek.GhostChain>,string) `` | 55 | TransportDominant_Fixture_StationHalfWithNewPidIsTheTipOfTheClaimedChain[edit:1-live]; TwoClaims_Fixture_OneChainKeyedByTheDepotEndsAtTheStationTip[edit:2-live]; TwoClaims_Fixture_TheChainHoldsBackTheDepotAndTheStation[edit:1-live] | tip-id chain lookup (cec0e0a7f / 5e3a58568 moved to claimed pids); no live caller |
| 42 | `GhostChain::ClaimsPid(uint32)` | 55 | TwoClaims_Fixture_TheChainHoldsBackTheDepotAndTheStation[edit:1-live] | chain pid-claim predicate (41a2e020a); no live caller |
| 43 | `MapRender.RenderGeometrySampler::ShiftSampleUTs(float64[],float64)` | 55 | FaithfulLoopShiftedPair_FlattensToZeroDrift[edit:3-live]; ShiftSampleUTs_NonFiniteShift_ReturnsCopyUnchanged[only-dead]; ShiftSampleUTs_Null_ReturnsEmpty[only-dead]; +3 more | UT shift of rendered samples; no live caller |
| 44 | `` ParsekFlight::DistanceOutsideRecordedAnchorCoverage(System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,float64) `` | 55 | DistanceOutsideRecordedAnchorCoverage_ReportsEndpointFallbackGap[only-dead] | recorded-anchor coverage distance; left by B7 (7f5818fa3) because a test calls it |
| 45 | `AnchorDetector::IsInDockingApproach(float64)` | 54 | IsInDockingApproach_LogsWhenDetected[only-dead]; IsInDockingApproach_NoLogWhenOutOfRange[only-dead]; IsInDockingApproach_AtBoundary_ReturnsFalse[only-dead]; +3 more | docking-approach distance predicate (P3a-1, 712e67b24); no live caller |
| 46 | `MissionEventDigest::FormatRowText(Parsek.MissionEventRow)` | 54 | Digest_ControllerTree_NamesThePartnerAndItsMission[only-dead]; Digest_GapRow_AppearsOnlyForAnIncludedLinkPastTheThreshold[only-dead]; Digest_NoMatch_UsesTheGenericUnrecordedText[only-dead]; +1 more | row text for the Missions Events foldout; its caller went with f712ec12c (Missions tab: remove the Events foldout) |
| 47 | `` ReFlySwitchGate::FirstAllowedIndex(System.Collections.Generic.IList`1<uint32>,System.Collections.Generic.ICollection`1<uint32>) `` | 52 | FirstAllowedIndex_SkipsRefusedCandidates[only-dead] | first allowed candidate in the stock cycle order (95e205522); no live caller |
| 48 | `` ParsekFlightPostSwitchAutoRecord::HasMeaningfulInventoryDelta(System.Collections.Generic.Dictionary`2<string,Parsek.InventoryItem>) `` | 51 | ManifestDeltaHelpers_ClassifyCrewResourceAndInventoryChanges[only-dead] | post-switch delta predicate; no live caller |
| 49 | `` ParsekFlightGhostPositioner::TryGetBracketWidthSeconds(System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,float64,float64&) `` | 50 | BracketWidth_NeedsTwoPoints[only-dead] | only the dead ShouldUseDenseReFlyAbsolutePlaybackFrames calls it |
| 50 | `RecordingFinalizationCacheProducer::TryBuildAtmosphericDeletionCache(string,uint32,string,float64,Parsek.FinalizationCacheOwner,string,Parsek.RecordingFinalizationCache&)` | 50 | TryBuildAtmosphericDeletionCache_CachesDestroyedTerminal[only-dead] | atmospheric-deletion cache producer (1c0ddc29a); no live caller; its live callees have no other direct test |
| 51 | `` GhostMapPresence::FindSupersededRecordingIds(System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>) `` | 49 | ChainAware_Standalone_OrbitRecording_GhostCreated[edit:1-live]; FindSuperseded_Empty_ReturnsEmpty[only-dead]; FindSuperseded_Null_ReturnsEmpty[only-dead]; +4 more | parent-id set (old tracking-station continuation suppression, c35068bf9); no live caller |
| 52 | `` SegmentBoundaryLogic::IsControllerPart(string,System.Collections.Generic.List`1<string>) `` | 49 | IsControllerPart_CaseSensitive_ModulecommandReturnsFalse[only-dead]; IsControllerPart_EmptyModuleList_ReturnsFalse[only-dead]; IsControllerPart_NullModuleList_ReturnsFalse[only-dead]; +6 more | controller-part predicate (fb422c8b2); no live caller |
| 53 | `ParsekFlight::ShouldSkipMutableActiveReFlyAnchorCandidate(Parsek.Recording,Parsek.ReFlySessionMarker,bool)` | 48 | ShouldSkipMutableActiveReFlyAnchorCandidate_OnlyDuringBypass[only-dead] | Re-Fly anchor-candidate skip; left by B7 because a test calls it |
| 54 | `` ParsekFlightGhostPositioner::CountDensebodyFixedFramesForSection(System.Collections.Generic.List`1<Parsek.TrackSection>,int32,Parsek.TrackSection,System.Collections.Generic.List`1<Parsek.TrajectoryPoint>) `` | 48 | CountDenseFrames_CountsOnlyPointsInTheSection[only-dead] | only the dead ShouldUseDenseReFlyAbsolutePlaybackFrames calls it |
| 55 | `` SectionWarpRuns::PhysicsFrameCount(System.Collections.Generic.IList`1<Parsek.SectionWarpRuns/Run>) `` | 47 | Build_FoldsContiguousKindsAndTracksPhysicsMaxRate[edit:1-live] | physics-frame total over warp runs (cdce4160e); no live caller (the harness parses the warpRuns token, not this) |
| 56 | `SessionMerger::AnchorIdentityKey(Parsek.TrackSection)` | 47 | AnchorIdentityKey_RelativeWithoutAnchor_ReturnsEmpty[only-dead]; MergeTree_BackgroundToActiveRelativeAnchorRecordingMismatch_SkipsHeal[edit:1-live] | anchor identity string for Relative sections (8e4da822a / f5cbc68b8); no live caller |
| 57 | `GhostPlaybackLogic::ValidateLoopAnchor(uint32)` | 46 | ValidateLoopAnchor_VesselExists_ReturnsTrue[only-dead]; ValidateLoopAnchor_VesselMissing_LogsWarning[only-dead]; ValidateLoopAnchor_VesselMissing_ReturnsFalse[only-dead]; +1 more | loop-anchor validation of the old path (00a042368 era); no live caller |
| 58 | `` ParsekFlight::RecordedAnchorPointListCoversUT(System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,float64) `` | 46 | RecordedAnchorPointListCoversUT_NullOrEmpty_ReturnsFalse[only-dead]; RecordedAnchorPointListCoversUT_SinglePointRequiresSameUT[only-dead]; RecordedAnchorPointListCoversUT_TargetAfterEnd_ReturnsFalse[only-dead]; +2 more | recorded-anchor coverage predicate; only the dead ResolveBodyFixedPrimaryPlaybackFrames calls it |
| 59 | `RecordingsTableFormatters::FormatDistance(float64)` | 46 | - | m/km/Mm formatter; only the RecordingsTableUI forwarder (dead) calls it |
| 60 | `` GhostPlaybackLogic::IsAnchorLoaded(uint32,System.Collections.Generic.HashSet`1<uint32>) `` | 45 | AnchorReloaded_PhaseRecomputed_NotFromStart[only-dead]; AnchoredLoop_AnchorLoaded_ShouldSpawn_ReturnsTrue[edit:1-live]; AnchoredLoop_AnchorNotLoaded_GhostSuppressed[only-dead]; +14 more | anchor-loaded check of the old playback path (d2e26d3ca); no live caller |
| 61 | `` ParsekFlightPostSwitchAutoRecord::HasMeaningfulResourceDelta(System.Collections.Generic.Dictionary`2<string,float64>,float64) `` | 45 | HasMeaningfulResourceDelta_IgnoresNearZeroNoise[only-dead]; ManifestDeltaHelpers_ClassifyCrewResourceAndInventoryChanges[only-dead] | post-switch delta predicate; no live caller |
| 62 | `` ParsekFlightPostSwitchAutoRecord::HasMeaningfulCrewDelta(System.Collections.Generic.Dictionary`2<string,int32>) `` | 44 | ManifestDeltaHelpers_ClassifyCrewResourceAndInventoryChanges[only-dead] | post-switch delta predicate; no live caller (the watch decides through other evaluators) |
| 63 | `ParsekFlight::BuildRecordedAnchorFallbackGapLog(string,string,uint32,float64,float64)` | 43 | BuildRecordedAnchorFallbackGapLog_IncludesGapAndRecordingContext[only-dead] | recorded-anchor fallback gap log line; its live caller went with B7 (7f5818fa3), which kept it only because a test calls it |
| 64 | `` ParsekFlightPostSwitchAutoRecord::HasMeaningfulPartStateChange(System.Collections.Generic.IEnumerable`1<Parsek.PartEventType>) `` | 43 | HasMeaningfulPartStateChange_CosmeticOnly_ReturnsFalse[only-dead]; HasMeaningfulPartStateChange_NonCosmeticTrigger_ReturnsTrue[only-dead] | post-switch delta predicate; no live caller |
| 65 | `Reaim.TransferWindowMath::HohmannPhaseAngleTargetDegrees(float64,float64)` | 43 | DegenerateSmas_ReturnNaN[edit:1-live]; HohmannPhaseAngle_EarthToMars_About44Degrees[only-dead]; HohmannPhaseAngle_KerbinToDuna_About44Degrees[only-dead] | Hohmann phase-angle formula; no live caller |
| 66 | `` GhostPlaybackLogic::ComputeTargetResourceIndex(System.Collections.Generic.List`1<Parsek.TrajectoryPoint>,int32,float64) `` | 41 | ComputeTargetResourceIndex_FindsHighestPassedPoint[only-dead]; ComputeTargetResourceIndex_NoAdvanceWhenBeforeNextPoint[only-dead] | resource-replay index from before #436 Phase F (3aea9b51d); no live caller |
| 67 | `BackgroundRecorder::ShouldStopDebrisRecording(float64,float64,bool,bool)` | 40 | ShouldStopDebrisRecording_DestroyedTakesPriorityOverTTL[only-dead]; ShouldStopDebrisRecording_NaN_TTL_VesselDestroyed_ReturnsDestroyed[only-dead]; ShouldStopDebrisRecording_NaN_TTL_VesselNotLoaded_ReturnsOutOfBubble[only-dead]; +7 more | debris stop-reason decision (destroyed / ttl / unloaded); no live caller |
| 68 | `GhostPlaybackLogic::ComputeWatchCycleOnLoopRebuild(int64,bool,bool,bool)` | 40 | WatchCycleOnRebuild_NotWatching_Unchanged[only-dead]; WatchCycleOnRebuild_Watching_AlreadyInHold_StaysInHold[only-dead]; WatchCycleOnRebuild_Watching_ExplosionAlreadyFired_ReturnsReady[only-dead]; +3 more | watch-cycle decision of the old playback path (T25 Phase 9, d2e26d3ca); no live caller |
| 69 | `` ParsekFlightGhostPositioner::PointBelongsToSection(System.Collections.Generic.List`1<Parsek.TrackSection>,int32,Parsek.TrackSection,float64) `` | 39 | PointBelongsToSection_FallsBackToInclusiveSectionBounds[only-dead]; PointBelongsToSection_UsesSectionLookupWhenIndexValid[only-dead] | only the dead dense-frame helpers call it |
| 70 | `GhostMapPresence::StartsInOrbit(Parsek.IPlaybackTrajectory,float64)` | 38 | - | orbit-start predicate; only ParsekPlaybackPolicy.StartsInOrbit (dead) calls it |
| 71 | `MissionEventDigest::LowerFirst(string)` | 36 | - | only FormatRowText (dead) calls it |
| 72 | `Logistics.RouteOriginCargoCheck::RequiresInventoryDebit(Parsek.Logistics.Route)` | 32 | RequiresInventoryDebit_NonKscWithInventory_True[only-dead] | shape detector of the M1 inventory-origin-debit HOLD that M3 Phase 5 (567389bc3) lifted; no live caller since |
| 73 | `` VesselSpawner::TryApplyPreferredSpawnRotation(ConfigNode,Parsek.Recording,System.Nullable`1<Parsek.TrajectoryPoint>,string) `` | 31 | TryApplyPreferredSpawnRotation_NonSurfaceTerminal_LeavesSnapshotRotUntouched[only-dead] | preferred-point spawn rotation (4956fe53d); no live caller |
| 74 | `Reaim.TransferWindowMath::NextDepartureUT(float64,float64,float64,float64,float64)` | 30 | NextDepartureUT_AddsTimeToNextWindow[only-dead]; NextDepartureUT_DegeneratePeriods_NaN[only-dead] | next transfer-window UT; no live caller |
| 75 | `` RelativeAnchorResolution::Decide(uint32,System.Func`2<uint32,bool>) `` | 30 | Decide_AnchorPidZero_OutcomeRetired[only-dead]; Decide_ResolverNull_OutcomeRetired[only-dead]; Decide_ResolverReturnsFalse_OutcomeRetired[only-dead]; +2 more | anchor resolved/retired decision; no live caller |
| 76 | `WideWindowLayout::ClampScrollX(float32,float32,float32)` | 29 | ClampScrollX_NaNIsZero[only-dead]; ClampScrollX_StaysInsideTheContent[only-dead] | sideways-scroll clamp from #1879 (0f064e9a1); no live caller |
| 77 | `ProximityRateSelector::GetSampleRateHz(float64)` | 28 | GetSampleRateHz_DockingRange_Returns5Hz[only-dead]; GetSampleRateHz_FarRange_ReturnsHalfHz[only-dead]; GetSampleRateHz_MidRange_Returns2Hz[only-dead]; +1 more | Hz view of GetSampleInterval (914e7cf63); no live caller; 4 tests pin the Hz numbers only |
| 78 | `Rendering.OutlierFlags::Empty(int32,int32)` | 28 | Empty_AllFalse_ZeroRejectionsZeroMask[edit:1-live] | empty-flags factory; no live caller |
| 79 | `RewindSpawnScope::IsStableState(int32)` | 27 | IsStableState_LandedSplashedPrelaunchOrbiting[only-dead]; IsStableState_StableStates[only-dead]; IsStableState_UnstableStates[only-dead] | LANDED/SPLASHED/PRELAUNCH/ORBITING situation predicate moved here in 68fbbef09; no live caller |
| 80 | `MapRender.RenderGeometrySampler::ToBodyRelative(Vector3d,Vector3d)` | 26 | ToBodyRelative_NonFiniteBody_ReturnsNaN[only-dead]; ToBodyRelative_NonFiniteWorld_ReturnsNaN[only-dead]; ToBodyRelative_SubtractsBodyPosition[only-dead] | world-to-body-relative subtraction with NaN guard; no live caller |
| 81 | `RecordingGroupStore::MarkAutoAssignedStandaloneGroup(Parsek.Recording,string)` | 25 | AutoAssignedStandaloneGroupMarker_RoundTripsThroughRecordingNode[edit:2-live]; CommitTree_ReplacesStandaloneAutoGroupWhenAdoptingOrphanEva[edit:1-live]; ManualGroupMutations_ClearAutoAssignedStandaloneGroupMarker[edit:2-live] | standalone auto-group stamp from the deleted chain-segment commit path (e312ebc4d); no live caller |
| 82 | `GameStateRecorder::CreateMilestoneEvent(string,float64)` | 24 | CreateMilestoneEvent_ConvertsToMilestoneAchievement[edit:1-live]; CreateMilestoneEvent_NullId_DefaultsToEmpty[only-dead]; CreateMilestoneEvent_PopulatesFieldsCorrectly[only-dead] | milestone event factory (c6fa63965); the live handler builds its events itself; 3 tests pin only the factory |
| 83 | `MapRender.RenderParityOracle::ParityModeToken(Parsek.MapRender.RenderParityOracle/ParityMode)` | 24 | ComputeDrift_SynthesizedMode_CarriesModeOnResult[edit:1-live]; ParityDriftToken_EmitsThroughGatedAnomalySink[edit:3-live] | parity-mode log token; no live caller |
| 84 | `GameStateStore::ClearEvents()` | 23 | ClearBaselines_IndependentFromClearEvents[edit:2-live]; GameStateStore_ClearEvents_RemovesAll[edit:2-live] | event wipe of the removed Wipe All button (7149d5cf5); no live caller |
| 85 | `GhostPlaybackLogic::ShouldForceWatchedFullFidelity(bool,float64,float32)` | 23 | ShouldForceWatchedFullFidelity_AtCutoff_ReturnsFalse[only-dead]; ShouldForceWatchedFullFidelity_InvalidDistance_ReturnsFalse[only-dead]; ShouldForceWatchedFullFidelity_NotWatched_ReturnsFalse[only-dead]; +1 more | watched-ghost full-fidelity rule (8e2426641 / c8ce5c828); no live caller |
| 86 | `MapRender.RenderCompositionManifest::CloseOpenDwell(uint32,float64)` | 23 | Build_SnapshotsStillOpenDwells_WithoutClosingThem[edit:2-live]; BuildSyntheticScene[edit:8-live] | per-pid dwell close for "ghost retired / scene exit"; no live caller (stale dwells close through the sweep) |
| 87 | `SelectiveSpawnUI::IsSpawnCandidate(float64,float64,bool,bool,float64,float64,float64,float64)` | 23 | IsSpawnCandidate_AboveMaxRelativeSpeed_False[only-dead]; IsSpawnCandidate_AllConditionsMet_True[only-dead]; IsSpawnCandidate_AtExactMaxRelativeSpeed_True[only-dead]; +6 more | pre-redesign Spawn Control candidate predicate (f16423dd4); the live candidate path is SpawnControlPresentation / ComposeFlightFinalNeedsSpawn |
| 88 | `MapRender.RenderGeometrySampler::FlattenSingle(Vector3d)` | 22 | FlattenSingle_OnePoint[only-dead] | one-point flatten; no live caller |
| 89 | `VesselSpawner::SelectRelocatedAltitude(bool,float64,bool,float64,bool)` | 22 | SelectRelocatedAltitude_PrefersTerrainThenSnapshot[only-dead] | only the dead ResolveRelocatedAltitude calls it |
| 90 | `GhostPlaybackEngine::IsGhostWithinVisualRange(int32)` | 21 | IsGhostWithinVisualRange_BeyondZone_ReturnsFalse[only-dead]; IsGhostWithinVisualRange_NoState_ReturnsFalse[only-dead]; IsGhostWithinVisualRange_PhysicsZone_ReturnsTrue[only-dead]; +1 more | engine range query; no live caller |
| 91 | `GroupHierarchyStore::GetDescendantGroups(string)` | 21 | GetDescendantGroups_MultiLevel_ReturnsGrandchildren[only-dead]; GetDescendantGroups_NoDescendants_ReturnsEmptyList[only-dead]; GetDescendantGroups_NullGroup_ReturnsEmptyList[only-dead]; +1 more | descendant-group query; no live caller, only GroupManagementTests |
| 92 | `GameStateStore::ClearScienceSubjects()` | 20 | ClearScienceSubjects_ClearsOriginalsAndCommitted[edit:1-live]; ClearScienceSubjects_RemovesAll[only-dead] | science-subject wipe of the removed Settings data-management buttons (adb05df44); no live caller |
| 93 | `` RecordingOptimizer::IsUnstableTerminalState(System.Nullable`1<Parsek.TerminalState>) `` | 20 | IsSpawnableTerminal_MatchesShouldSpawnAtRecordingEndRefusalSet[edit:3-live] | not-spawnable terminal predicate (1730cc54e era); no live caller |
| 94 | `GhostAudioPresets::ResolveOneShotClip(Parsek.PartEventType)` | 17 | ResolveOneShotClip_KnownTypes[only-dead]; ResolveOneShotClip_UnknownTypesReturnNull[only-dead] | Destroyed -> explosion clip map (e63aeded9 era); no live caller |
| 95 | `MapRenderTrace::Decision(bool,bool,string)` | 17 | - | only EvaluateGate (dead) calls it |
| 96 | `BackgroundRecorder::IsOrbitalCheckpointSituation(Vessel/Situations)` | 16 | IsOrbitalCheckpointSituation_Escaping_ReturnsTrue[only-dead]; IsOrbitalCheckpointSituation_Flying_ReturnsFalse[only-dead]; IsOrbitalCheckpointSituation_Landed_ReturnsFalse[only-dead]; +4 more | ORBITING/SUB_ORBITAL/ESCAPING predicate (a3e128d93 era); no live caller |
| 97 | `ParsekFlight::ShouldWarnRecordedAnchorFallbackGap(float64)` | 16 | ShouldWarnRecordedAnchorFallbackGap_OnlyWarnsForLargeFiniteGap[only-dead] | recorded-anchor fallback gap threshold; left by B7 because a test calls it |
| 98 | `GameStateStore::ClearBaselines()` | 15 | ClearBaselines_IndependentFromClearEvents[edit:2-live]; ClearBaselines_RemovesAll[edit:1-live] | baseline wipe of the removed Wipe All button (7149d5cf5); no live caller |
| 99 | `KspStatePatcher::ResolveFundsPatchTarget(Parsek.FundsModule,float64,bool)` | 15 | Funds_FutureReservedDeficit_IsNotWrittenIntoTheLivePool[edit:5-live]; Funds_PastPenaltyDeficit_StaysNegative_NoUpliftClamp[edit:4-live] | funds patch target from before the guard read what stock applied (64f3362fb); no live caller |
| 100 | `KspStatePatcher::ResolveSciencePatchBaseTarget(Parsek.ScienceModule,float64,bool)` | 15 | Science_FutureReservedDeficit_IsNotWrittenIntoTheLivePool[edit:4-live] | science patch target from before 64f3362fb; no live caller |
| 101 | `WideWindowLayout::MaxScrollX(float32,float32)` | 15 | MaxScrollX_IsTheOverflowOrZero[only-dead] | only ClampScrollX (dead) calls it |
| 102 | `GhostPlaybackLogic::ShouldExitWatchForCutoff(float64,float32)` | 13 | ShouldExitWatchForCutoff[only-dead]; ShouldExitWatchForCutoff_AppliesUniformly[only-dead] | only ShouldForceWatchedFullFidelity (dead) calls it |
| 103 | `MapRender.PhaseId::get_IsEmpty()` | 13 | IsEmpty_DefaultStruct[only-dead] | empty-id predicate; no live reader |
| 104 | `ParsekFlight::IsTrackableVesselType(VesselType)` | 13 | IsTrackableVesselType_Debris_ReturnsFalse[only-dead]; IsTrackableVesselType_EVA_ReturnsTrue[only-dead]; IsTrackableVesselType_Probe_ReturnsFalse[only-dead]; +2 more | vessel-type filter from the split-event detection (74624f09f); no live caller |
| 105 | `StrategiesModule::IsManagedStrategy(string)` | 13 | PlanFromModule[edit:3-live] | managed-strategy predicate (9bbfd43cf); no live caller |
| 106 | `` GhostPlaybackLogic::ResolveEffectiveWatchTargetIndex(int32,System.Collections.Generic.IReadOnlyList`1<Parsek.Recording>,System.Collections.Generic.IReadOnlyList`1<Parsek.RecordingTree>,System.Func`2<int32,bool>) `` | 12 | ResolveEffectiveWatchTargetIndex_BreakupDifferentPid_DoesNotFallbackToActiveChild[only-dead]; ResolveEffectiveWatchTargetIndex_ChainMultiHopThroughInactiveIntermediate_ReturnsDeepActiveSegment[only-dead]; ResolveEffectiveWatchTargetIndex_ChainSingleHop_ReturnsActiveContinuation[only-dead]; +5 more | group-watch resolver the UI stopped using at #382 (the rotation cursor); RecordingsTableUITests.GroupWatchUsesRotationCursor_PinnedBySourceInspection only asserts the UI does NOT name it |
| 107 | `ParsekUI::HeaderToCellTextDeltaPx(int32,int32,int32,int32)` | 12 | HeaderToCellTextDelta_CensusNumbers[edit:1-live] | header-to-cell text offset arithmetic used only to state GUI-census numbers in one test (7c90492bd); no live caller |
| 108 | `CrewReservationManager::ShouldProcessCrewForReservation(ProtoCrewMember/RosterStatus)` | 10 | ShouldProcessCrew_Assigned_ReturnsTrue[only-dead]; ShouldProcessCrew_Available_ReturnsTrue[only-dead]; ShouldProcessCrew_Dead_ReturnsFalse[only-dead]; +1 more | "extracted for testability" not-Dead predicate; the reservation path no longer calls it |
| 109 | `ParsekFlight::ResolveDockRouteTargetPid(bool,uint32,uint32)` | 10 | ResolveDockRouteTargetPid_UsesOtherDockedVessel[only-dead] | dock partner pid choice (84588dc50); no live caller |
| 110 | `` GameStateRecorder::CheckBuildingTransitions(System.Collections.Generic.Dictionary`2<string,bool>,System.Collections.Generic.Dictionary`2<string,bool>,float64) `` | 9 | BuildingTransition_Destroyed_DetectedCorrectly[only-dead]; BuildingTransition_NoChange_NoEvents[only-dead]; BuildingTransition_Repaired_DetectedCorrectly[only-dead] | forwarder to GameStateFacilityRecorder.CheckBuildingTransitions (dead); 3 tests pin only the diff |
| 111 | `` GameStateRecorder::CheckFacilityTransitions(System.Collections.Generic.Dictionary`2<string,float32>,System.Collections.Generic.Dictionary`2<string,float32>,float64) `` | 9 | FacilityTransition_Downgraded_DetectedCorrectly[only-dead]; FacilityTransition_NoChange_NoEvents[only-dead]; FacilityTransition_Upgraded_DetectedCorrectly[only-dead] | forwarder to GameStateFacilityRecorder.CheckFacilityTransitions (dead); 3 tests pin only the diff |
| 112 | `ParsekPlaybackPolicy::StartsInOrbit(Parsek.IPlaybackTrajectory,float64)` | 8 | StartsInOrbit_NoSegments_ReturnsFalse[only-dead]; StartsInOrbit_OrbitOnly_NoPoints_ReturnsTrue[only-dead]; StartsInOrbit_UTBeforeSegment_ReturnsFalse[only-dead]; +1 more | forwarder to GhostMapPresence.StartsInOrbit, which is dead too; no live caller of either |
| 113 | `RenderingZoneManager::ShouldRenderMesh(float64)` | 8 | ShouldRenderMesh_AtVisualBoundary_ReturnsFalse[only-dead]; ShouldRenderMesh_BeyondVisualRange_ReturnsFalse[only-dead]; ShouldRenderMesh_InsideVisualRange_ReturnsTrue[only-dead]; +5 more | distance < visual-range predicate (bd3efcbff); no live caller |
| 114 | `RenderingZoneManager::ShouldRenderPartEvents(float64)` | 8 | ShouldRenderPartEvents_AtFullFidelityBoundary_ReturnsFalse[only-dead]; ShouldRenderPartEvents_BeyondPhysicsBubble_ReturnsFalse[only-dead]; ShouldRenderPartEvents_InsideFullFidelityRange_ReturnsTrue[only-dead]; +5 more | distance predicate (bd3efcbff); no live caller |
| 115 | `VesselSpawner::ShouldRegenerateIdentity(bool)` | 8 | ShouldRegenerateIdentity_DefaultFalse_ReturnsTrue[only-dead]; ShouldRegenerateIdentity_PreserveTrue_ReturnsFalse[only-dead] | !preserveIdentity flag (58bd8981d); no live caller |
| 116 | `GhostPlaybackEngine::OnAnchorVesselLoaded(uint32)` | 7 | OnAnchorVesselLoaded_AddsToSet[only-dead]; OnAnchorVesselUnloaded_RemovesFromSet[only-dead] | anchor-vessel notification of the T25 query API (7a3cc4652); nothing raises it |
| 117 | `GhostPlaybackEngine::OnAnchorVesselUnloaded(uint32)` | 7 | OnAnchorVesselUnloaded_NonexistentId_NoError[only-dead]; OnAnchorVesselUnloaded_RemovesFromSet[only-dead] | as OnAnchorVesselLoaded |
| 118 | `GhostPlaybackLogic::ShouldLoopPlayback(bool)` | 6 | ShouldLoopPlayback_RespectsRecordingFlag[only-dead] | trivial loop flag; its caller went with B7 (7f5818fa3) |
| 119 | `GhostPlaybackLogic::ShouldPauseTimelineResourceReplay(bool)` | 6 | ShouldPauseTimelineResourceReplay_ReflectsRecordingState[only-dead] | pre-#436 resource replay pause flag; no live caller |
| 120 | `MapRender.OrbitalState::FromSegment(Parsek.OrbitSegment)` | 5 | OrbitalState_WrapsOrbitSegment_AtItsEpoch[only-dead] | factory at the segment's start UT; no live caller |
| 121 | `MapRender.PhaseSeam::get_RequiresTangentMatch()` | 5 | OrbitLandingSeam_IsRigidG1_OnCamera[edit:1-live]; Build_ArrivalSeam_IsFlexibleSoiG0_OffCamera[only-dead]; FlexibleSoiFactory_IsFlexibleG0_CarriesCrossing[edit:1-live]; +2 more | derived flag (continuity G1); no live reader |
| 122 | `RecordingsTableUI::FormatDistance(float64)` | 3 | FormatDistance_CorrectOutput[only-dead] | forwarder to RecordingsTableFormatters.FormatDistance; only FormatDistanceTests call it |
| 123 | `MapRender.StockConicTreatment::get_Kind()` | 2 | Kinds_AreDistinct[only-dead] | treatment kind property; never read live |
| 124 | `MapRender.TracedPathTreatment::get_Kind()` | 2 | Kinds_AreDistinct[only-dead] | treatment kind property; never read live |

## Orphaned hooks

Hook-named methods (verdict `test-hook`) no test calls any more. Each can go with no test edit,
but they are hooks, so they are not in the approved set:

- `Display.GhostTrajectoryPolylineRenderer::get_CacheForTesting()` (2)
- `GhostMapPresence::GetOverlapInstanceCyclesForTesting(int32)` (44)
- `GhostMapPresence::ResetGapGlideCountersForTesting()` (6)
- `GhostPlaybackEngine::IsCompletionFiredForTesting(int32)` (5)
- `GhostPlaybackEngine::ResetCadenceLogCacheForTesting()` (6)
- `GhostPlaybackEngine::ResolveRenderSurfaceForTesting(bool,bool)` (4)
- `GhostPlaybackEngine::SynchronizeLoadedGhostForWatchForTesting(int32,Parsek.IPlaybackTrajectory,Parsek.GhostPlaybackState,float64,float64)` (10)
- `KerbalsWindowUI::get_TabCountForTesting()` (8)
- `LedgerRecoveryFundsPairing::get_PendingRecoveryFundsCountForTesting()` (3)
- `LogisticsWindowUI::IsRowExpandedForTesting(string)` (9)
- `MapRender.RenderCompositionRecorder::ResetWroteManifestLatchForTesting()` (3)
- `MissionsWindowUI::IsLegExpandedForTesting(string)` (11)
- `MissionsWindowUI::IsVesselExpandedForTesting(string)` (9)
- `MissionsWindowUI::get_RenamingMissionTextForTesting()` (3)
- `ParsekFileCollector::get_CachedReferrerCountForTesting()` (3)
- `ParsekFlightPlaybackHost::ClearGhostSkipReasonLogStateForTesting()` (5)
- `ParsekScenario::BuildScenarioLifecycleExceptionMessageForTesting(string,string,System.Exception)` (10)
- `ParsekScenario::ClearFreshRecordingAfterDiscardForTesting()` (4)
- `ParsekScenario::get_CommittedTreeStateLoadedForTesting()` (2)
- `` ParsekSettingsPersistence::SetStoredVerboseLoggingForTesting(System.Nullable`1<bool>) `` (6)
- `` ParsekSettingsPersistence::SetStoredWarpDateForTesting(System.Nullable`1<int32>,System.Nullable`1<int32>,System.Nullable`1<int32>,System.Nullable`1<int32>) `` (12)
- `ReFlySettleStabilityTracker::SetLastFloatingOriginShiftFrameForTesting(int32)` (4)
- `RecordingFinalizationCacheProducer::ResetSuppressedNoDeltaCountForTesting()` (4)
- `RecordingsTableUI::IsChainExpandedForTesting(string)` (9)
- `RecordingsTableUI::IsGroupExpandedForTesting(string)` (9)
- `RecordingsTableUI::get_RenamingRecordingTextForTesting()` (3)
- `SelectiveSpawnUI::get_KerbinTimeOverrideForTesting()` (2)
- `SessionSuppressionState::ObserveMarkerTransitionsForTesting()` (3)
- `StructureListWindowUI::get_TimeCellsForTesting()` (3)
- `StructureListWindowUI::get_WindowTitleForTesting()` (6)

## Surprises

- **Gates that pin dead code.** The `ParsekUI` hover check (`IsMouseOverOpenAuxiliaryWindows` and
  the `IsMouseOverOpenWindow` family) has its body pinned verbatim by
  `ParsekUIWindowSetCharacterizationTests.HoverCheck_ReadsTheTableThenSettingsThenTheTestRunner`,
  yet nothing calls it; `LogisticsWindowUI::DetailLine(GUIContent)` is pinned by a
  `TableRowInsetAlignmentTests` gate. Both `tested-helper-of-live-code`: the gate must change
  before the code can.
- **The Re-Fly world-action safety cluster is dead since 1b92384ab** (the value gate):
  `SupersedeCommit::TryFindRecordingScopedWorldAction` and its family,
  `IsWorldStateChangingRecordingAction` and `TombstoneEligibility::TryPairBundledRepPenalty`. Its
  tests pin the live world-action safety cache, so `dead-but-tests-guard-live-logic`.
- **`TreeDiscardService::RemoveRecordingAt` and its prune cluster** have been dead since the
  2026-09-26 removal of the player deletion surfaces. CLAUDE.md still names `RemoveRecordingAt`,
  and its 15 tests pin the live `RemoveCommittedAt` index contract: re-point the tests and edit
  CLAUDE.md before deleting.
- **Recorded keeps the fixpoint cannot see:** the `GhostRenderReconciler` store cluster
  (`scripts/grep-audit-render-reconciler-unwired.ps1`: an unwiring, not a deletion), the engine's
  `GhostPlaybackEngine::IsGhostOnBody` (`WatchEntryAcceptanceWiringGateTests` exempts it as "still
  correct, still unit-tested"), the `ITrajectorySample` discriminator (design section 5.3) and the
  `PhaseSeamClassifier::Classify` cluster, which the harness render-composition rule cites as its
  C# authority.
- **Keeps written only in doc comments.** Six otherwise dead methods carry a comment that keeps
  them on purpose: `RouteAnalysisEngine::HasResourcePickup` (a "future hook", with its helper
  `IsIgnoredResource`), `ReaimTransferSynthesizer::ProjectOntoPlane` (a "documented gated
  contingency"), `ReFlyAutoSealPreviewResult::FormatHumanReadable` ("kept for callers that need
  compact inline copy"), the two `SegmentProvenanceTokens` chokepoints, and
  `ParsekFlight::allowAnchorCorrection` ("retained for the unit-test surface"; its live twin is
  `allowAnchorCorrectionInterval`). The first five are `kept-by-design`, the last
  `dead-but-tests-guard-live-logic`; none is in the approved set.
- **Reflection from tests:** `PurchaseOwnershipBook::get_Count` is read by name by the
  session-reset sweeps.
- **B7 leftovers:** most really-dead rows are methods B7 left only because a test called them,
  for example the `ParsekFlight` recorded-anchor and body-fixed playback statics and the
  `GhostPlaybackLogic` loop and watch helpers of the pre-engine playback path.

## Mechanical and judgment

Mechanical (re-runnable): the 773-row set and its counts, the instruction sizes, the test
callers, the hook-name split, the reconciliation with `facts_pins.toml`, and for every
really-dead row the four checks above (IL callers, comment-stripped source scan, string and
harness / scripts / docs scans, attribute and override roots), the closure of the set under
production calls, the test-reach walk, the `[only-dead]` / `[edit]` tags and the `lastCaller`
proxy.

Judgment: every verdict other than the 282 hook-named rows, and every note (what a method was
for, which live twin its tests guard, whether a keep is recorded). The `lastCaller` column is a
pickaxe proxy, not proof: a name shared with live code, a move between files or a rename can
blur it.
