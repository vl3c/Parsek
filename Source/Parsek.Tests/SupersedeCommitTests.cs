using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): guards the merge-time supersede commit.
    ///
    /// <para>
    /// Covers the terminal-kind matrix (Landed -&gt; Immutable; Crashed
    /// -&gt; CommittedProvisional), the forward-only merge-guarded subtree
    /// walk (every descendant in the closure gets a supersede relation; a
    /// mixed-parent descendant is excluded), the transient
    /// <see cref="Recording.SupersedeTargetId"/> clear, the active-marker
    /// clear, the ERS cache invalidation via supersede state version bump,
    /// and the regular tree-merge fallback when no session is active.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class SupersedeCommitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SupersedeCommitTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
        }

        // ---------- Helpers -------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string supersedeTargetId = null,
            uint vesselPid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                SupersedeTargetId = supersedeTargetId,
                VesselPersistentId = vesselPid,
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null,
            uint targetPid = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>(),
                TargetVesselPersistentId = targetPid,
            };
        }

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0,
            };
        }

        public static IEnumerable<object[]> RetryBlockingRecordingActionCases()
        {
            // Columns: GameActionType, retryBlocking (Re-Fly/STASH gate), strictBlocking
            // (legacy ledger-safety gate; preserved for non-retry callers).
            //
            // Retry-blocking is now narrowed to ScienceEarning only — see
            // SupersedeCommit.IsRetryBlockingRecordingAction. Every KSC-scene
            // "player action" still passes the strict gate (so audit/reconciliation
            // call sites observe them) but does NOT auto-seal a Re-Fly retry,
            // because in practice it cannot reach the gate with a flight tag and,
            // for the one rollout-adoption case that does (FundsSpending(VesselBuild)),
            // the cost is paid once and survives revert/retag.
            yield return new object[] { GameActionType.ScienceEarning, true, true };
            yield return new object[] { GameActionType.ScienceSpending, false, true };
            yield return new object[] { GameActionType.FundsEarning, false, true };
            yield return new object[] { GameActionType.FundsSpending, false, true };
            yield return new object[] { GameActionType.MilestoneAchievement, false, true };
            yield return new object[] { GameActionType.ContractAccept, false, true };
            yield return new object[] { GameActionType.ContractComplete, false, true };
            yield return new object[] { GameActionType.ContractFail, false, true };
            yield return new object[] { GameActionType.ContractCancel, false, true };
            yield return new object[] { GameActionType.ReputationEarning, false, true };
            yield return new object[] { GameActionType.ReputationPenalty, false, true };
            yield return new object[] { GameActionType.KerbalAssignment, false, true };
            yield return new object[] { GameActionType.KerbalHire, false, true };
            yield return new object[] { GameActionType.KerbalRescue, false, true };
            yield return new object[] { GameActionType.KerbalStandIn, false, true };
            yield return new object[] { GameActionType.FacilityUpgrade, false, true };
            yield return new object[] { GameActionType.FacilityDestruction, false, true };
            yield return new object[] { GameActionType.FacilityRepair, false, true };
            yield return new object[] { GameActionType.StrategyActivate, false, true };
            yield return new object[] { GameActionType.StrategyDeactivate, false, true };
            yield return new object[] { GameActionType.FundsInitial, false, false };
            yield return new object[] { GameActionType.ScienceInitial, false, false };
            yield return new object[] { GameActionType.ReputationInitial, false, false };
        }

        [Fact]
        public void RetryBlockingRecordingActionCases_CoverEveryGameActionType()
        {
            var covered = new HashSet<GameActionType>(
                RetryBlockingRecordingActionCases()
                    .Select(row => (GameActionType)row[0]));
            var expected = new HashSet<GameActionType>(
                Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>());
            var missing = expected.Except(covered).ToList();
            var extra = covered.Except(expected).ToList();

            Assert.True(missing.Count == 0 && extra.Count == 0,
                "RetryBlockingRecordingActionCases must classify every " +
                "GameActionType. Missing=[" + string.Join(",", missing) +
                "] Extra=[" + string.Join(",", extra) + "]");
        }

        private static GameAction RecordingScopedAction(
            GameActionType type,
            string recordingId,
            string actionId = null)
        {
            return new GameAction
            {
                ActionId = actionId ?? "act_" + type,
                Type = type,
                RecordingId = recordingId,
                UT = 12.0,
                SubjectId = "crewReport@MunInSpaceLow",
                ScienceAwarded = 1.5f,
                NodeId = "survivability",
                Cost = 5.0f,
                FundsAwarded = 10.0f,
                FundsSpent = 10.0f,
                FundsSource = FundsEarningSource.Other,
                FundsSpendingSource = FundsSpendingSource.Other,
                NominalRep = 1.0f,
                NominalPenalty = 1.0f,
                RepSource = ReputationSource.Other,
                RepPenaltySource = ReputationPenaltySource.Other,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 960.0f,
                MilestoneRepAwarded = 1.0f,
                ContractId = "contract_1",
                ContractType = "ExploreBody",
                ContractTitle = "Explore the Mun",
                AdvanceFunds = 1000.0f,
                FundsReward = 2000.0f,
                RepReward = 2.0f,
                ScienceReward = 1.0f,
                FundsPenalty = 500.0f,
                RepPenalty = 1.0f,
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                KerbalEndStateField = KerbalEndState.Recovered,
                HireCost = 10000.0f,
                ReplacesKerbal = "Bill Kerman",
                FacilityId = "LaunchPad",
                ToLevel = 2,
                FacilityCost = 75000.0f,
                StrategyId = "FundraisingCamp",
                SourceResource = StrategyResource.Reputation,
                TargetResource = StrategyResource.Funds,
                Commitment = 0.1f,
                SetupCost = 1000.0f,
                SetupScienceCost = 5.0f,
                SetupReputationCost = 1.0f,
                InitialFunds = 25000.0f,
                InitialScience = 5.0f,
                InitialReputation = 10.0f,
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(
            string originId, string provisionalId,
            string sessionId = "sess_1", string treeId = "tree_1",
            string supersedeTargetId = null)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = supersedeTargetId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
                // Empty session-baseline: every structural BP currently
                // in the tree counts as session-authored. Tests that
                // need to seed pre-existing BPs as the baseline override
                // this list explicitly.
                PreSessionBranchPointIds = new List<string>(),
            };
        }

        // Fixture: origin (suppressed) + 1 descendant (suppressed) + unrelated
        // (outside). Mirrors SessionSuppressionWiringTests.InstallOriginClosureFixture.
        private void InstallOriginClosureFixture(
            string originId, string insideId, string outsideId,
            TerminalState? originTerminal = null, TerminalState? insideTerminal = null)
        {
            var origin = Rec(originId, "tree_1",
                childBranchPointId: "bp_c", terminal: originTerminal);
            var inside = Rec(insideId, "tree_1",
                parentBranchPointId: "bp_c", terminal: insideTerminal);
            var outside = Rec(outsideId, "tree_1");
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { originId },
                children: new List<string> { insideId });
            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
        }

        private static Recording AddProvisional(string recordingId, string treeId,
            TerminalState? terminal, string supersedeTargetId)
        {
            var provisional = Rec(recordingId, treeId,
                state: MergeState.NotCommitted,
                terminal: terminal,
                supersedeTargetId: supersedeTargetId);
            // Satisfy SupersedeCommit.AppendRelations supersede-target
            // invariant (>=1 trajectory point + non-null terminal). Tests
            // exercising the empty / null-terminal cases construct
            // provisionals directly.
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, treeId);
            return provisional;
        }

        // ---------- Terminal kind matrix -----------------------------------

        [Fact]
        public void LandedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=Immutable")
                && l.Contains("terminalKind=Landed"));
        }

        [Fact]
        public void FlipMergeState_RestoresPlaybackEnabledFromFalse()
        {
            // RewindInvoker.BuildProvisionalRecording creates Re-Fly provisionals
            // with PlaybackEnabled=false so the in-flight session does not also
            // play the attempt back as a ghost. After merge, the recording is
            // committed timeline data and must replay normally — otherwise
            // sibling-slot Re-Fly sessions later see the prior attempt's ghost
            // skipped with reason=playback-disabled.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            provisional.PlaybackEnabled = false;
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(provisional.PlaybackEnabled,
                "PlaybackEnabled must flip to true at merge so the prior attempt's ghost replays normally.");
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Supersede]")
                && l.Contains("FlipMergeStateAndClearTransient: restored PlaybackEnabled=true"));
        }

        [Fact]
        public void FlipMergeState_LeavesPlaybackEnabledTrueAlone()
        {
            // Belt-and-braces: a Re-Fly provisional that already has
            // PlaybackEnabled=true (synthetic test setup or a future call site
            // that doesn't suppress) must not log the restore line.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            provisional.PlaybackEnabled = true;
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.True(provisional.PlaybackEnabled);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("FlipMergeStateAndClearTransient: restored PlaybackEnabled=true"));
        }

        [Fact]
        public void CrashedTerminal_ProducesCommittedProvisional()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("terminalKind=Crashed"));
        }

        [Fact]
        public void CrashedTerminal_WithRpSlot_StaysOpenAndDoesNotSeal()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot> { originSlot },
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.False(originSlot.Sealed);
            Assert.Null(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("classifierReason=crashed")
                && l.Contains("autoSeal=False"));
        }

        [Fact]
        public void SplashedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Splashed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
        }

        [Fact]
        public void LandedTerminal_WithRpSlot_AutoSealsViaFocusOverride()
        {
            // v0.9.1 revision (§4.6): the player-chosen Re-Fly slot
            // reaching a stable Landed terminal seals the slot via the
            // focus override. Previously this test asserted "closes
            // without auto-seal" because Landed only triggered the
            // stableTerminal path which auto-sealed only on
            // IsHardSafetyTerminal (Recovered/Docked/Boarded). Under the
            // override, Landed/Splashed/Orbiting/SubOrbital on the
            // player-chosen slot all seal.
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
        }

        [Theory]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void HardSafetyTerminal_WithRpSlot_AutoSeals(TerminalState terminal)
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                terminal, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminal")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=classifierClosed:stableTerminal"));
        }

        [Fact]
        public void DownstreamStructuralInteraction_WithRpSlot_AutoSeals()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            provisional.ChildBranchPointId = "bp_downstream";
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=downstreamBp")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=classifierClosed:downstreamBp"));
        }

        [Fact]
        public void StashedLandedLeaf_ReFlyMerge_AutoSealsViaFocusOverride()
        {
            // v0.9.1 revision (§4.6): a Re-Fly merge of a stashed slot
            // that ends in a stable terminal (Landed here) seals via the
            // focus override. The override fires before the
            // stashed-keep-open branch in the classifier, so
            // stashedStableLeaf no longer comes out of Site B-1.
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
                Stashed = true,
                StashedRealTime = "2026-04-29T12:00:00.0000000Z",
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True")
                && l.Contains("focusSlotOverride=1"));
        }

        [Fact]
        public void OrbitingNonFocusReFlyTarget_PromotesFocusAndProducesImmutable()
        {
            // Re-Fly target promotes focus: the player just Re-Flew slot 1
            // themselves, so a stable Orbiting terminal is a concluded
            // outcome and the merge must commit Immutable. Without the
            // override the classifier would keep the slot re-flyable as
            // stableLeafUnconcluded, blocking auto-seal — see
            // SupersedeCommit.ClassifyMergeStateOrThrow's focusSlotOverride.
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            // Auto-seal fires for stableTerminalFocusSlot regardless of
            // whether the player-chosen slot matched the static focus or
            // was promoted via the override.
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_provisional")
                && l.Contains("reason=stableTerminalFocusSlot")
                && l.Contains("focusSlotOverride=1"));
        }

        [Fact]
        public void OrbitingFocusStableLeaf_StableTerminalFocusSlot_AutoSealsSlot()
        {
            // Static-focus orbit Re-Fly: same auto-seal trigger as the
            // override path. Player flew the focus slot to stable orbit;
            // slot.Sealed must be true so the row drops out of UF.
            const string bpId = "bp_stage_focus_seal";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_focus_seal",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    originSlot,
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_other", Controllable = true },
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
        }

        [Fact]
        public void OrbitingReFlyTarget_NoFocusSignalRP_AutoSealsViaFocusOverride()
        {
            // P3 review: the override must precede the noFocusSignalOrbiting
            // early-return. A Re-Fly merge from an RP captured with no
            // focus signal (FocusSlotIndex == -1, e.g. the player was
            // controlling an unrelated vessel at split time) must still
            // seal via stableTerminalFocusSlot when the player flew the
            // slot to a stable terminal.
            const string bpId = "bp_no_focus";
            var origin = Rec("rec_origin", "tree_no_focus");
            InstallTree("tree_no_focus",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_no_focus",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_no_focus";
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = -1, // no focus captured
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_other", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True")
                && l.Contains("focusSlotOverride=1"));
            // Must NOT take the noFocusSignalOrbiting early-return path.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_provisional")
                && l.Contains("reason=noFocusSignalOrbiting"));
        }

        [Fact]
        public void OrbitingReFlyTarget_StashedSlotWithStableTip_AutoSealsViaFocusOverride()
        {
            // v0.9.1 revision (§4.6): focus override fires BEFORE the
            // classifier's stashed-keep-open branch, so a stashed slot
            // Re-Flown to stable orbit closes via stableTerminalFocusSlot
            // — not via the structural-mutation gate. The structural gate
            // remains a defensive backstop and is exercised at the
            // helper level by HasReFlySessionStructuralMutation_* tests
            // below.
            const string rpId = "rp_stashed_override_seal";
            const string bpId = "bp_stashed_override_seal";
            var origin = Rec("rec_origin", "tree_stashed_override");
            InstallTree("tree_stashed_override",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_stashed_override",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            marker.SessionId = "sess_stashed_override_seal";
            marker.TreeId = "tree_stashed_override";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 300.0;
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
                Stashed = true,
                StashedRealTime = "2026-05-02T00:00:00.0000000Z",
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                UT = 100.0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("autoSeal=True")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("focusSlotOverride=1"));
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_BreakupBetweenRpAndInvokedUT_DetectedViaRpUT()
        {
            // Regression for the cutoff source: marker.InvokedUT (300) is
            // the live UT at the moment the player clicked Re-Fly, but the
            // RP quicksave threw them back to UT=100. A decouple at UT=150
            // during the Re-Fly is BEFORE marker.InvokedUT but AFTER
            // rp.UT, and must trip the gate. Using marker.InvokedUT as the
            // cutoff would miss this — the common case for any non-instant
            // Re-Fly playthrough.
            const string rpId = "rp_normal_timing";
            var rec = Rec("rec_provisional", "tree_normal_timing");
            var breakupBp = new BranchPoint
            {
                Id = "bp_normal_timing_decouple",
                Type = BranchPointType.Breakup,
                UT = 150.0,
                ParentRecordingIds = new List<string> { "rec_provisional" },
            };
            InstallTree("tree_normal_timing",
                new List<Recording> { rec },
                new List<BranchPoint> { breakupBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_normal_timing";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 300.0;
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.True(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.NotNull(detail);
            Assert.Contains("cutoffOrigin=rpUT", detail);
            Assert.Contains("sinceUT=100.00", detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_RpMissingFromScenario_FallsBackToInvokedUT()
        {
            // Defensive: if the marker's RewindPointId no longer resolves
            // on the live scenario (e.g. test fixture gap, or a future
            // call site that runs after the RP has been reaped), fall
            // back to marker.InvokedUT as the cutoff so the gate at least
            // catches branch points authored after invocation. Detail
            // string flags this with cutoffOrigin=invokedUT.
            var rec = Rec("rec_provisional", "tree_no_rp");
            var bp = new BranchPoint
            {
                Id = "bp_post_invoke",
                Type = BranchPointType.JointBreak,
                UT = 305.0,
                ParentRecordingIds = new List<string> { "rec_provisional" },
            };
            InstallTree("tree_no_rp",
                new List<Recording> { rec },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_no_rp";
            marker.RewindPointId = "rp_not_in_scenario";
            marker.InvokedUT = 300.0;
            InstallScenario(marker);

            string detail;
            Assert.True(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.NotNull(detail);
            Assert.Contains("cutoffOrigin=invokedUT", detail);
            Assert.Contains("sinceUT=300.00", detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_PreExistingPostRpBranchPoint_ExcludedByBaseline()
        {
            // Regression: the load-time
            // SpliceMissingCommittedRecordingsIntoLoadedTree path re-grafts
            // pre-Re-Fly post-RP branch points back into the loaded
            // Re-Fly tree. Without the session baseline, a pre-existing
            // structural BP authored before the player invoked Re-Fly
            // would auto-seal a stashed slot the player never mutated.
            // The marker's PreSessionBranchPointIds snapshot excludes
            // those by id.
            const string rpId = "rp_baseline_exclusion";
            const string preExistingBpId = "bp_old_breakup";
            var rec = Rec("rec_provisional", "tree_baseline");
            var preExistingBp = new BranchPoint
            {
                Id = preExistingBpId,
                Type = BranchPointType.Breakup,
                UT = 200.0,
            };
            InstallTree("tree_baseline",
                new List<Recording> { rec },
                new List<BranchPoint> { preExistingBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_baseline";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            // The BP existed at marker creation — record it as pre-session
            // so the gate excludes it from the structural-mutation count.
            marker.PreSessionBranchPointIds = new List<string> { preExistingBpId };
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_BaselineExcludesOneBpButNewOneStillDetected()
        {
            // Mixed case: tree carries one pre-existing structural BP
            // (excluded by the baseline) and one new session-authored BP
            // (present in tree, NOT in baseline). The new BP must still
            // trip the gate; the spliceExcluded counter in the detail
            // string surfaces the excluded count for log audits.
            const string rpId = "rp_mixed";
            const string oldBpId = "bp_pre_session";
            const string newBpId = "bp_new_decouple";
            var rec = Rec("rec_provisional", "tree_mixed");
            var oldBp = new BranchPoint
            {
                Id = oldBpId,
                Type = BranchPointType.Breakup,
                UT = 200.0,
                ParentRecordingIds = new List<string> { "rec_provisional" },
            };
            var newBp = new BranchPoint
            {
                Id = newBpId,
                Type = BranchPointType.Breakup,
                UT = 250.0,
                ParentRecordingIds = new List<string> { "rec_provisional" },
            };
            InstallTree("tree_mixed",
                new List<Recording> { rec },
                new List<BranchPoint> { oldBp, newBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_mixed";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            marker.PreSessionBranchPointIds = new List<string> { oldBpId };
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.True(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.NotNull(detail);
            Assert.Contains($"firstBp={newBpId}", detail);
            Assert.Contains("branchPoints=1", detail);
            Assert.Contains("spliceExcluded=1", detail);
            Assert.Contains("baseline=1", detail);
            Assert.Contains("lineage=", detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_BranchPointOnUnrelatedSiblingInSameTree_NotDetected()
        {
            // P1 review: a background sibling vessel in the SAME tree
            // that stages / undocks during the Re-Fly authors a structural
            // BP whose ParentRecordingIds points at the sibling, NOT the
            // Re-Fly target. The lineage filter must exclude it so
            // background-vessel mutations do not auto-seal the player-
            // chosen slot.
            const string rpId = "rp_unrelated_sibling";
            const string siblingBpId = "bp_sibling_decouple";
            var provisional = Rec("rec_provisional", "tree_unrelated");
            var sibling = Rec("rec_sibling", "tree_unrelated");
            // The sibling is in the same tree but a different chain.
            sibling.ChainId = "chain_sibling";
            provisional.ChainId = "chain_provisional";
            var siblingBp = new BranchPoint
            {
                Id = siblingBpId,
                Type = BranchPointType.Breakup,
                UT = 200.0,
                // Crucially: parent is the sibling, not the provisional.
                ParentRecordingIds = new List<string> { "rec_sibling" },
            };
            InstallTree("tree_unrelated",
                new List<Recording> { provisional, sibling },
                new List<BranchPoint> { siblingBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_unrelated";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            marker.PreSessionBranchPointIds = new List<string>();
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                provisional, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_StandaloneProvisionalNullChainId_DetectsViaProvisionalIdOnly()
        {
            // BuildReFlyTargetLineageRecordingIds early-returns just the
            // provisional's id when ChainId is null/empty (standalone
            // recording, no chain segments). A decouple BP authored
            // against the provisional during the session must still trip
            // the gate via the singleton lineage set — the most common
            // case for fresh-provisional Re-Flies.
            const string rpId = "rp_standalone";
            const string bpId = "bp_standalone_decouple";
            var rec = Rec("rec_provisional", "tree_standalone");
            // ChainId stays null (Recording default).
            Assert.Null(rec.ChainId);
            var bp = new BranchPoint
            {
                Id = bpId,
                Type = BranchPointType.Breakup,
                UT = 200.0,
                ParentRecordingIds = new List<string> { "rec_provisional" },
            };
            InstallTree("tree_standalone",
                new List<Recording> { rec },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_standalone";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            marker.PreSessionBranchPointIds = new List<string>();
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.True(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.NotNull(detail);
            Assert.Contains($"firstBp={bpId}", detail);
            // Lineage = {provisional} only (no chain segments).
            Assert.Contains("lineage=1", detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_BranchPointOnSameChainTail_Detected()
        {
            // Optimizer-split chain tail: the provisional is the head, a
            // tail recording shares (TreeId, ChainId, ChainBranch). A BP
            // authored against the tail (e.g. player decouples mid-chain)
            // is in the lineage and must trip the gate.
            const string rpId = "rp_chain_tail";
            const string tailBpId = "bp_chain_tail_decouple";
            var head = Rec("rec_provisional", "tree_chain");
            head.ChainId = "chain_inplace";
            head.ChainBranch = 0;
            var tail = Rec("rec_chain_tail", "tree_chain");
            tail.ChainId = "chain_inplace";
            tail.ChainBranch = 0;
            var bp = new BranchPoint
            {
                Id = tailBpId,
                Type = BranchPointType.Breakup,
                UT = 200.0,
                ParentRecordingIds = new List<string> { "rec_chain_tail" },
            };
            InstallTree("tree_chain",
                new List<Recording> { head, tail },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_chain";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            marker.PreSessionBranchPointIds = new List<string>();
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.True(SupersedeCommit.HasReFlySessionStructuralMutation(
                head, marker, out detail));
            Assert.NotNull(detail);
            Assert.Contains($"firstBp={tailBpId}", detail);
            // Lineage size = head + tail = 2.
            Assert.Contains("lineage=2", detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_LegacyMarkerWithoutBaseline_GateSkipped()
        {
            // Legacy markers persisted before PreSessionBranchPointIds
            // shipped have a null list. Without a baseline the gate cannot
            // distinguish session-authored from spliced-in BPs, so it
            // conservatively skips. Sessions in flight at upgrade time
            // therefore preserve the pre-fix keep-open behavior on merge.
            const string rpId = "rp_legacy";
            var rec = Rec("rec_provisional", "tree_legacy");
            var bp = new BranchPoint
            {
                Id = "bp_legacy",
                Type = BranchPointType.Breakup,
                UT = 200.0,
            };
            InstallTree("tree_legacy",
                new List<Recording> { rec },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_legacy";
            marker.RewindPointId = rpId;
            marker.InvokedUT = 400.0;
            marker.PreSessionBranchPointIds = null;
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = rpId,
                UT = 100.0,
                BranchPointId = "bp_seed",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>(),
            });

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_PreRewindBreakup_NotDetected()
        {
            // A Breakup branch point authored BEFORE the rewind point's UT
            // belongs to the pre-rewind flight history and must not trip
            // the structural-mutation gate. Otherwise a vessel that
            // already separated before the player invoked Re-Fly would
            // unconditionally seal on merge.
            var rec = Rec("rec_provisional", "tree_pre_rewind");
            var preRewindBp = new BranchPoint
            {
                Id = "bp_pre_rewind",
                Type = BranchPointType.Breakup,
                UT = 100.0,
                ChildRecordingIds = new List<string>(),
            };
            InstallTree("tree_pre_rewind",
                new List<Recording> { rec },
                new List<BranchPoint> { preRewindBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_pre_rewind";
            marker.InvokedUT = 200.0;
            InstallScenario(marker);

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_NonStructuralBranchPointTypes_NotDetected()
        {
            // Dock / Board / Launch / Terminal branch points are not
            // structural mutations: Dock and Board attach to a pre-existing
            // vessel without spawning a new one, Launch is the tree root,
            // Terminal marks the recording's end.
            var rec = Rec("rec_provisional", "tree_non_struct");
            var dockBp = new BranchPoint
            {
                Id = "bp_dock",
                Type = BranchPointType.Dock,
                UT = 300.0,
            };
            var boardBp = new BranchPoint
            {
                Id = "bp_board",
                Type = BranchPointType.Board,
                UT = 305.0,
            };
            var terminalBp = new BranchPoint
            {
                Id = "bp_terminal",
                Type = BranchPointType.Terminal,
                UT = 310.0,
            };
            InstallTree("tree_non_struct",
                new List<Recording> { rec },
                new List<BranchPoint> { dockBp, boardBp, terminalBp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_non_struct";
            marker.InvokedUT = 200.0;
            InstallScenario(marker);

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void HasReFlySessionStructuralMutation_DifferentTreeId_NotDetected()
        {
            // The provisional's TreeId must match marker.TreeId. If the
            // marker references a different tree (defensive: should not
            // happen but the field is independently persisted), the gate
            // returns false rather than scanning an unrelated tree.
            var rec = Rec("rec_provisional", "tree_a");
            var bp = new BranchPoint
            {
                Id = "bp_struct",
                Type = BranchPointType.Breakup,
                UT = 300.0,
            };
            InstallTree("tree_a",
                new List<Recording> { rec },
                new List<BranchPoint> { bp });
            var marker = Marker("rec_origin", "rec_provisional");
            marker.TreeId = "tree_b";
            marker.InvokedUT = 200.0;
            InstallScenario(marker);

            string detail;
            Assert.False(SupersedeCommit.HasReFlySessionStructuralMutation(
                rec, marker, out detail));
            Assert.Null(detail);
        }

        [Fact]
        public void OrbitingNonFocusReFlyTarget_OriginOnlyMarkerTarget_PromotesFocusAndProducesImmutable()
        {
            // Origin-only branch-resolution variant of the focus-override
            // path: same outcome — the Re-Fly target slot is treated as the
            // de-facto focus and the merge commits Immutable.
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("slot=1")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_provisional")
                && l.Contains("reason=stableTerminalFocusSlot")
                && l.Contains("side=origin-only")
                && l.Contains("focusSlotOverride=1"));
        }

        [Fact]
        public void OrbitingNonFocusStableLeaf_OriginOnlyMarkerTarget_WithScienceEarningAction_AutoSeals()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceEarning,
                "rec_origin",
                "act_sci_earn_origin"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("slot=1")
                && l.Contains("classifierReason=recordingAction:ScienceEarning:act_sci_earn_origin")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=recordingAction:ScienceEarning:act_sci_earn_origin"));
        }

        [Fact]
        public void OrbitingNonFocusReFlyTarget_PreflightFallbackResolvesChainedMarkerTargetAndPromotesFocus()
        {
            // The marker-target preflight fallback resolves the slot via the
            // chained supersede target; the focus override still applies
            // and the merge commits Immutable instead of leaving the slot
            // re-flyable as stableLeafUnconcluded.
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            var priorTip = Rec("rec_prior_tip", "tree_1", terminal: TerminalState.Orbiting);
            InstallTree("tree_1",
                new List<Recording> { origin, priorTip },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_prior_tip");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional",
                supersedeTargetId: "rec_prior_tip");
            var scenario = InstallScenario(marker);
            scenario.RecordingSupersedes.Add(Rel("rec_origin", "rec_prior_tip"));
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            Assert.False(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                provisional, out _, out _, out _));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_prior_tip");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_prior_tip" && r.NewRecordingId == "rec_provisional");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("slot=1")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
        }

        [Fact]
        public void OrbitingNonFocusStableLeaf_WithPriorSupersedeLineageScienceEarningAction_AutoSeals()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            var priorTip = Rec("rec_prior_tip", "tree_1", terminal: TerminalState.Orbiting);
            InstallTree("tree_1",
                new List<Recording> { origin, priorTip },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_prior_tip");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional",
                supersedeTargetId: "rec_prior_tip");
            var scenario = InstallScenario(marker);
            scenario.RecordingSupersedes.Add(Rel("rec_origin", "rec_prior_tip"));
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceEarning,
                "rec_origin",
                "act_sci_earn_origin"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=recordingAction:ScienceEarning:act_sci_earn_origin")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=recordingAction:ScienceEarning:act_sci_earn_origin"));
        }

        [Fact]
        public void TryFindRecordingScopedWorldAction_CacheInvalidatesOnLedgerMutation()
        {
            var rec = Rec("rec_cache", "tree_1");

            string summary;
            Assert.False(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Null(summary);

            Ledger.AddAction(new GameAction
            {
                ActionId = "act_sci_cache",
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_cache",
                UT = 12.0,
                SubjectId = "crewReport@MunInSpaceLow",
                ScienceAwarded = 1.5f,
            });

            Assert.True(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Equal("ScienceEarning:act_sci_cache", summary);

            Ledger.ResetForTesting();

            Assert.False(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Null(summary);
        }

        [Fact]
        public void TryFindRecordingScopedWorldAction_CacheInvalidatesOnSupersedeMutation()
        {
            var rec = Rec("rec_new", "tree_1");
            var scenario = InstallScenario(Marker("rec_old", "rec_new"));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_sci_old",
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_old",
                UT = 12.0,
                SubjectId = "crewReport@MunInSpaceLow",
                ScienceAwarded = 1.5f,
            });

            string summary;
            Assert.False(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Null(summary);

            scenario.RecordingSupersedes.Add(Rel("rec_old", "rec_new"));
            scenario.BumpSupersedeStateVersion();

            Assert.True(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Equal("ScienceEarning:act_sci_old", summary);
        }

        [Fact]
        public void EffectiveStateResetCachesForTesting_ClearsWorldActionSafetyCache()
        {
            var rec = Rec("rec_cache_reset", "tree_1");
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_sci_cache_reset",
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_cache_reset",
                UT = 12.0,
                SubjectId = "crewReport@MunInSpaceLow",
                ScienceAwarded = 1.5f,
            });

            string summary;
            Assert.True(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Equal("ScienceEarning:act_sci_cache_reset", summary);

            int versionBeforeTruncate = Ledger.StateVersion;
            Ledger.TruncateActionsForTesting(0);
            Assert.Equal(versionBeforeTruncate, Ledger.StateVersion);

            Assert.True(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Equal("ScienceEarning:act_sci_cache_reset", summary);

            EffectiveState.ResetCachesForTesting();

            Assert.False(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Null(summary);
        }

        [Theory]
        [MemberData(nameof(RetryBlockingRecordingActionCases))]
        public void TryFindRetryBlockingWorldAction_ReportsOnlyScienceEarning(
            GameActionType type,
            bool retryBlocking,
            bool strictBlocking)
        {
            var rec = Rec("rec_" + type, "tree_1");
            string actionId = "act_" + type;
            Ledger.AddAction(RecordingScopedAction(type, rec.RecordingId, actionId));

            string summary;
            Assert.Equal(strictBlocking,
                SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            if (strictBlocking)
                Assert.Equal(type + ":" + actionId, summary);
            else
                Assert.Null(summary);

            Assert.Equal(retryBlocking,
                SupersedeCommit.TryFindRetryBlockingWorldAction(rec, out summary));
            if (retryBlocking)
                Assert.Equal(type + ":" + actionId, summary);
            else
                Assert.Null(summary);
        }

        [Fact]
        public void TryFindRetryBlockingWorldAction_SkipsAutomaticActionsAndReportsScienceEarning()
        {
            var rec = Rec("rec_mixed_actions", "tree_1");
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.FundsEarning,
                rec.RecordingId,
                "act_funds_earn"));
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceEarning,
                rec.RecordingId,
                "act_sci_earn"));

            string summary;
            Assert.True(SupersedeCommit.TryFindRecordingScopedWorldAction(rec, out summary));
            Assert.Equal("FundsEarning:act_funds_earn", summary);

            Assert.True(SupersedeCommit.TryFindRetryBlockingWorldAction(rec, out summary));
            Assert.Equal("ScienceEarning:act_sci_earn", summary);
        }

        [Fact]
        public void OrbitingNonFocusStableLeaf_WithRecordingScopedScienceEarningAction_AutoSeals()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceEarning,
                "rec_provisional",
                "act_sci_earn"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=recordingAction:ScienceEarning:act_sci_earn")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=recordingAction:ScienceEarning:act_sci_earn"));
        }

        // Negative twin of the ScienceEarning case above. After the v0.9.x
        // tightening (auto-seal limited to intentional player actions taken on
        // the vessel), ScienceSpending — a KSC-scene tech-unlock — no longer
        // closes a Destroyed retry slot, even when it carries a flight-tagged
        // RecordingId.
        [Fact]
        public void DestroyedTerminal_WithRecordingScopedScienceSpendingAction_StaysRetryable()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceSpending,
                "rec_provisional",
                "act_sci_spend"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.False(originSlot.Sealed);
            Assert.Null(originSlot.SealedRealTime);
        }

        [Fact]
        public void DestroyedTerminal_WithRecordingScopedScienceEarningAction_AutoSeals()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(RecordingScopedAction(
                GameActionType.ScienceEarning,
                "rec_provisional",
                "act_sci_earn"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=recordingAction:ScienceEarning:act_sci_earn")
                && l.Contains("autoSeal=True")
                && l.Contains("autoSealReason=recordingAction:ScienceEarning:act_sci_earn"));
        }

        [Fact]
        public void DestroyedTerminal_WithOnlyNonStructuralPartEvents_KeepsOpen()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            provisional.PartEvents.Add(new PartEvent
            {
                ut = 12.0,
                partPersistentId = 100000,
                partName = "roverWheel",
                eventType = PartEventType.GearDeployed,
            });
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.False(originSlot.Sealed);
            Assert.Null(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("classifierReason=crashed")
                && l.Contains("autoSeal=False"));
        }

        [Fact]
        public void DestroyedTerminal_WithRecordingScopedMilestoneAction_KeepsOpen()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_milestone",
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec_provisional",
                UT = 12.0,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 960.0f,
                MilestoneRepAwarded = 1.0f,
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.False(originSlot.Sealed);
            Assert.Null(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("classifierReason=crashed")
                && l.Contains("autoSeal=False"));
        }

        [Fact]
        public void DestroyedTerminal_WithTombstoneEligibleKerbalDeathAction_KeepsOpen()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    originSlot,
                }
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_death",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_provisional",
                UT = 12.0,
                KerbalName = "Jebediah Kerman",
                KerbalEndStateField = KerbalEndState.Dead,
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_rep",
                Type = GameActionType.ReputationPenalty,
                RecordingId = "rec_provisional",
                UT = 12.0,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
                NominalPenalty = 2.0f,
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.False(originSlot.Sealed);
            Assert.Null(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("classifierReason=crashed")
                && l.Contains("autoSeal=False"));
        }

        [Fact]
        public void OrbitingFocusStableLeaf_ProducesImmutable()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            var originSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_origin",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 1,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_other", Controllable = true },
                    originSlot,
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            // Re-Fly target reached stable Orbiting on the static focus
            // slot: per playtest contract the slot auto-seals, closing the
            // Unfinished-Flight row and preventing further re-fly retries.
            Assert.True(originSlot.Sealed);
            Assert.NotNull(originSlot.SealedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot")
                && l.Contains("autoSeal=True"));
        }

        [Fact]
        public void OrbitingStableLeaf_SlotLookupFailure_ThrowsInsteadOfFallback()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            scenario.RecordingSupersedes.Add(Rel("rec_prior_old", "rec_prior_new"));
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_existing",
                ActionId = "act_existing",
                RetiringRecordingId = "rec_prior_new",
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_death",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_origin",
                KerbalEndStateField = KerbalEndState.Dead,
                UT = 12.0,
            });
            int relationCountBefore = scenario.RecordingSupersedes.Count;
            int tombstoneCountBefore = scenario.LedgerTombstones.Count;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.CommitSupersede(
                    scenario.ActiveReFlySessionMarker, provisional));

            Assert.Contains("Site B-1 slot lookup failed", ex.Message);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Equal("rec_origin", provisional.SupersedeTargetId);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Equal(relationCountBefore, scenario.RecordingSupersedes.Count);
            Assert.Equal(tombstoneCountBefore, scenario.LedgerTombstones.Count);
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.NewRecordingId == "rec_provisional");
            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == "act_death");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("aborting because stable-leaf classification cannot safely fall back"));
        }

        [Fact]
        public void ChainTipOrbitingStableLeaf_SlotLookupFailure_ThrowsInsteadOfFallback()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional_head", "tree_1",
                null, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            provisional.ChainId = "chain_stable";
            provisional.ChainIndex = 0;
            var tip = Rec("rec_provisional_tip", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Orbiting);
            tip.ChainId = "chain_stable";
            tip.ChainIndex = 1;
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_1");
            var tree = RecordingStore.CommittedTrees.Single(t => t.Id == "tree_1");
            tree.AddOrReplaceRecording(provisional);
            tree.AddOrReplaceRecording(tip);
            var marker = Marker("rec_origin", "rec_provisional_head");
            var scenario = InstallScenario(marker);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.FlipMergeStateAndClearTransient(
                    marker, provisional, scenario, preserveMarker: false));

            Assert.Contains("Site B-1 slot lookup failed", ex.Message);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("terminal=Orbiting")
                && l.Contains("aborting because stable-leaf classification cannot safely fall back"));
        }

        [Fact]
        public void InPlaceContinuationSlotLookupFailure_UsesFallbackWithoutError()
        {
            var provisional = AddProvisional("rec_origin", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            var marker = Marker("rec_origin", "rec_origin", supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_other",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_origin",
                        Controllable = true
                    }
                }
            });

            SupersedeCommit.FlipMergeStateAndClearTransient(
                marker, provisional, scenario, preserveMarker: false);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[ERROR][Supersede]")
                && l.Contains("Site B-1 slot lookup failed"));
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("in-place continuation: using v0.9 terminalKind classifier"));
        }

        // ---------- Subtree supersede ---------------------------------------

        [Fact]
        public void SubtreeSupersede_AllDescendantsGetRelations()
        {
            // origin -> inside1 -> inside2 linear chain; every id in the
            // closure must produce one supersede relation pointing at the
            // provisional.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_1");
            var inside1 = Rec("rec_inside1", "tree_1",
                parentBranchPointId: "bp_1", childBranchPointId: "bp_2");
            var inside2 = Rec("rec_inside2", "tree_1",
                parentBranchPointId: "bp_2");
            var bp1 = Bp("bp_1", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_inside1" });
            var bp2 = Bp("bp_2", BranchPointType.Undock,
                parents: new List<string> { "rec_inside1" },
                children: new List<string> { "rec_inside2" });
            InstallTree("tree_1",
                new List<Recording> { origin, inside1, inside2 },
                new List<BranchPoint> { bp1, bp2 });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var rels = scenario.RecordingSupersedes;
            Assert.Equal(3, rels.Count);

            // Every relation points at the provisional.
            foreach (var rel in rels)
                Assert.Equal("rec_provisional", rel.NewRecordingId);

            var oldIds = new HashSet<string>(rels.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.Contains("rec_inside1", oldIds);
            Assert.Contains("rec_inside2", oldIds);

            // Log assertions.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("rel=")
                && l.Contains("old=rec_origin") && l.Contains("new=rec_provisional"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Added 3 supersede relations")
                && l.Contains("rooted at rec_origin"));
        }

        [Fact]
        public void MixedParentDescendant_NotIncluded()
        {
            // Two roots: rec_origin (in subtree) + rec_other (outside).
            // bp_c has BOTH as parents (mixed) so the walk must halt before
            // adding rec_inside to the closure (§7.40).
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var other = Rec("rec_other", "tree_1", childBranchPointId: "bp_c");
            var inside = Rec("rec_inside", "tree_1", parentBranchPointId: "bp_c");
            var bp_c = Bp("bp_c", BranchPointType.Dock,
                parents: new List<string> { "rec_origin", "rec_other" },
                children: new List<string> { "rec_inside" });
            InstallTree("tree_1",
                new List<Recording> { origin, other, inside },
                new List<BranchPoint> { bp_c });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Only the origin is superseded; rec_inside is a mixed-parent
            // halt and must NOT be in the relation list.
            var oldIds = new HashSet<string>(
                scenario.RecordingSupersedes.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.DoesNotContain("rec_inside", oldIds);
            Assert.DoesNotContain("rec_other", oldIds);
        }

        // ---------- Chain extension / Unfinished Flight ----------------------

        [Fact]
        public void ChainExtendsThroughCrashedReFly()
        {
            // The crashed provisional commits as CommittedProvisional so the
            // slot-level Unfinished Flights predicate can keep a real RP slot
            // open. This fixture predates slot resolution and only guards the
            // commit-state / visibility half of the chain-extension behavior.
            var origin = Rec("rec_origin", "tree_1",
                parentBranchPointId: "bp_parent",
                terminal: TerminalState.Destroyed);
            var bp_parent = Bp("bp_parent", BranchPointType.Undock,
                parents: new List<string> { "rec_root" },
                children: new List<string> { "rec_origin" });
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint> { bp_parent });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");

            var rps = new List<RewindPoint>
            {
                new RewindPoint
                {
                    RewindPointId = "rp_1",
                    BranchPointId = "bp_parent",
                    ChildSlots = new List<ChildSlot>(),
                },
            };
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            scenario.RewindPoints = rps;

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);

            // The §7.43 chain-extension behavior is that the provisional stays
            // visible and committed-ish; separate tests cover slot-level UF
            // membership once a RewindPoint child slot exists.
            var provisionalVisible = EffectiveState.IsVisible(provisional, scenario.RecordingSupersedes);
            Assert.True(provisionalVisible,
                "provisional must be visible in ERS after commit (nothing supersedes it)");

            // Origin is now superseded → NOT visible.
            Assert.False(EffectiveState.IsVisible(
                origin, scenario.RecordingSupersedes));
        }

        [Fact]
        public void AppendRelations_ChainExtension_RootsAtSupersedeTargetPriorTip()
        {
            var origin = Rec("rec_origin", "tree_1");
            var priorTip = Rec("rec_refly1", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin, priorTip },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_refly2", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_refly1");
            var marker = Marker("rec_origin", "rec_refly2",
                supersedeTargetId: "rec_refly1");
            var scenario = InstallScenario(marker);

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_refly1"
                    && r.NewRecordingId == "rec_refly2");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin"
                    && r.NewRecordingId == "rec_refly2");
            Assert.Equal("rec_refly2", EffectiveState.EffectiveRecordingId(
                "rec_origin",
                new List<RecordingSupersedeRelation>
                {
                    Rel("rec_origin", "rec_refly1"),
                    scenario.RecordingSupersedes[0],
                }));
        }

        [Fact]
        public void HybridStarAndLinearGraph_ResolvesDominantTipAndAllSlotTrails()
        {
            const string bpId = "bp_probe_split";
            var origin = Rec("probeOrig", "tree_probe", parentBranchPointId: bpId);
            var reFly1 = Rec("probeReFly1", "tree_probe", parentBranchPointId: bpId);
            var reFly2 = Rec("probeReFly2", "tree_probe", parentBranchPointId: bpId);
            InstallTree("tree_probe",
                new List<Recording> { origin, reFly1, reFly2 },
                new List<BranchPoint>());
            var reFly3 = AddProvisional("probeReFly3", "tree_probe",
                TerminalState.Destroyed, supersedeTargetId: "probeReFly1");
            reFly3.ParentBranchPointId = bpId;

            var marker = Marker("probeOrig", "probeReFly3",
                treeId: "tree_probe",
                supersedeTargetId: "probeReFly1");
            var scenario = InstallScenario(marker);
            scenario.RecordingSupersedes.Add(Rel("probeOrig", "probeReFly1"));
            scenario.RecordingSupersedes.Add(Rel("probeOrig", "probeReFly2"));
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_probe",
                BranchPointId = bpId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "probeOrig",
                        Controllable = true,
                    },
                },
            });

            SupersedeCommit.AppendRelations(marker, reFly3, scenario);

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "probeReFly1"
                    && r.NewRecordingId == "probeReFly3");
            Assert.Equal("probeReFly3", EffectiveState.EffectiveRecordingId(
                "probeOrig", scenario.RecordingSupersedes));

            Assert.True(RecordingsTableUI.TryResolveRewindPointForRecording(
                reFly3, out var rpForNewTip, out int slotForNewTip));
            Assert.Same(scenario.RewindPoints[0], rpForNewTip);
            Assert.Equal(0, slotForNewTip);

            Assert.True(RecordingsTableUI.TryResolveRewindPointForRecording(
                reFly2, out var rpForOrphanBranch, out int slotForOrphanBranch));
            Assert.Same(scenario.RewindPoints[0], rpForOrphanBranch);
            Assert.Equal(0, slotForOrphanBranch);
        }

        // ---------- Transient fields / marker cleanup ----------------------

        [Fact]
        public void MergeState_Clears_SupersedeTargetId()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.Equal("rec_origin", provisional.SupersedeTargetId);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(provisional.SupersedeTargetId);
        }

        [Fact]
        public void Commit_ClearsActiveMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // §10.4 End reason=merged log.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("End reason=merged")
                && l.Contains("sess=sess_1"));
        }

        [Fact]
        public void Commit_BumpsStateVersion()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int versionBefore = scenario.SupersedeStateVersion;
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            int versionAfter = scenario.SupersedeStateVersion;

            Assert.NotEqual(versionBefore, versionAfter);
        }

        [Fact]
        public void Commit_InvalidatesErsCache_OriginVanishes()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside",
                originTerminal: TerminalState.Destroyed);
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            // Before commit: origin is session-suppressed (filtered out of ERS
            // by the marker), inside also suppressed, outside + provisional
            // visible.
            var ersBefore = EffectiveState.ComputeERS();
            var idsBefore = new HashSet<string>(ersBefore.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsBefore);
            Assert.Contains("rec_outside", idsBefore);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // After commit: origin is superseded (filtered out of ERS by
            // supersede relations), not session-suppressed. The ERS cache
            // must rebuild — origin must still be absent; provisional must
            // be present.
            var ersAfter = EffectiveState.ComputeERS();
            var idsAfter = new HashSet<string>(ersAfter.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsAfter);
            Assert.DoesNotContain("rec_inside", idsAfter);
            Assert.Contains("rec_outside", idsAfter);
            Assert.Contains("rec_provisional", idsAfter);
        }

        // ---------- Idempotence + edge cases --------------------------------

        [Fact]
        public void Commit_IsIdempotent_ReinvocationSkipsExistingRelations()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            var marker = scenario.ActiveReFlySessionMarker;

            SupersedeCommit.CommitSupersede(marker, provisional);
            int firstCount = scenario.RecordingSupersedes.Count;

            // Reset the marker and call again with the same provisional. Commit
            // must be a no-op for the relations that already exist (defensive
            // idempotence per Phase 8 scope).
            scenario.ActiveReFlySessionMarker = marker;
            SupersedeCommit.CommitSupersede(marker, provisional);
            int secondCount = scenario.RecordingSupersedes.Count;

            Assert.Equal(firstCount, secondCount);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("skip existing relation"));
        }

        [Fact]
        public void NoActiveMarker_SkipsSupersede_RegularTreeCommit()
        {
            // No scenario-installed marker. TryCommitReFlySupersede invoked from
            // MergeDialog.MergeCommit should short-circuit and leave the
            // supersede list untouched.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(marker: null);

            MergeDialog.TryCommitReFlySupersede();

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("no active re-fly session marker"));
        }

        [Fact]
        public void TryCommitReFlySupersede_NoProvisionalRecording_WarnsAndKeepsMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(
                Marker("rec_origin", "rec_ghost_provisional"));

            // Provisional recording id does not exist in the store.
            MergeDialog.TryCommitReFlySupersede();

            // Marker stays in place so the Phase 13 load-time sweep can clean
            // it up deterministically.
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("not found in committed list"));
        }

        [Fact]
        public void NullMarker_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            InstallScenario(marker: null);

            // Direct call with null marker → safe no-op + warn.
            SupersedeCommit.CommitSupersede(null, provisional);

            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("marker is null"));
        }

        [Fact]
        public void NullProvisional_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(
                ParsekScenario.Instance.ActiveReFlySessionMarker, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("provisional is null"));
        }

        // ---------- Self-supersede guards (bug/rewind-self-supersede-and-followups) -----

        // The runtime `old==new` self-skip defense in
        // `SupersedeCommit.AppendRelations` was removed when the placeholder-
        // and-redirect cascade was retired (item 11), but PR #590 re-introduced
        // it to support the in-place-continuation `AppendRelations` call path:
        // when `marker.OriginChildRecordingId == provisional.RecordingId`, the
        // session-suppressed-subtree closure includes the origin itself, and a
        // row where `old == new` would form a 1-node `EffectiveRecordingId`
        // cycle. The 4-arg overload added an `extraSelfSkipRecordingIds`
        // parameter for the optimizer-split case where the in-place provisional
        // has been split into chain HEAD + TIP (and the three-segment variant
        // where HEAD/MIDDLE/TIP are all part of the new flight): the caller
        // passes the TIP as `provisional` so `ValidateSupersedeTarget` sees a
        // non-null terminal payload, and names the other chain members in
        // `extraSelfSkipRecordingIds` so none of them ends up with a row
        // pointing at another member. The runtime self-skip guard is exercised
        // by `TryCommitReFlySupersede_InPlaceContinuation_LoneOrigin_FiltersSelfLinkOnly`;
        // the extra-self-skip set is exercised by
        // `TryCommitReFlySupersede_InPlaceContinuation_OptimizerSplit_ResolvesChainTipAndWritesSiblingRows`
        // and
        // `TryCommitReFlySupersede_InPlaceContinuation_ThreeSegmentChain_NoMemberSupersededByAnotherMember`.

        // ---------- Supersede-target invariant (item 10) -------------------

        [Fact]
        public void AppendRelations_EmptyProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: "rec_origin");
            Assert.Empty(provisional.Points);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("empty Points", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=empty Points")
                && l.Contains("refusing to write supersede rows"));
        }

        [Fact]
        public void AppendRelations_NullTerminalProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: "rec_origin");
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            provisional.Points.Add(new TrajectoryPoint { ut = 1.0 });
            Assert.Null(provisional.TerminalStateValue);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("null TerminalState", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=null TerminalState")
                && l.Contains("refusing to write supersede rows"));
        }

        // ---------- Chain-sibling expansion (item 23) ----------------------

        [Fact]
        public void AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment()
        {
            // Merge-time RecordingOptimizer.SplitAtSection produces a HEAD
            // (BP-linked, ChildBranchPointId=null after the move at
            // RecordingStore.cs:2018-2019) + TIP (terminal=Destroyed) chain
            // sharing both ChainId and ChainBranch. Marker points at the
            // HEAD. AppendRelations must write a supersede row for BOTH
            // segments so the TIP doesn't survive the merge as a stale
            // "kerbal destroyed in atmo" row alongside the new "kerbal
            // lived" provisional.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            var tip = Rec("rec_tip", "tree_1");
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.TerminalStateValue = TerminalState.Destroyed;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            InstallTree("tree_1",
                new List<Recording> { head, tip },
                new List<BranchPoint> { bp_split });

            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_head");
            var scenario = InstallScenario(Marker("rec_head", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Both chain segments must have a supersede row pointing at the
            // provisional. Neither row should be missed and the TIP must not
            // be left visible as an orphan in ERS.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_head" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_tip" && r.NewRecordingId == "rec_provisional");
        }

        // ---------- In-place continuation supersede append (bug fix) ------
        // Bug fix (in-place-supersede): when the merge is an "in-place
        // continuation" (provisional.RecordingId == origin), the prior code
        // skipped AppendRelations entirely, so chain siblings / parent
        // recordings inside the suppressed subtree never got supersede rows
        // and stayed visible after merge. The fix calls AppendRelations on
        // the in-place path too, relying on the restored old==new self-skip
        // inside AppendRelations to filter the trivial self-link.

        /// <summary>
        /// AppendRelations self-link guard: when the subtree closure contains
        /// the provisional's own id (which happens whenever
        /// origin == provisional, i.e. in-place continuation), the row is
        /// skipped instead of producing a 1-node cycle in EffectiveRecordingId.
        /// Other ids in the closure still get rows. Direct
        /// AppendRelations call so this test is independent of the
        /// MergeDialog wiring above.
        /// </summary>
        [Fact]
        public void AppendRelations_SelfLinkSkipped_OtherSubtreeIdsStillWriteRows()
        {
            // Closure: head (origin == provisional) + tip (chain sibling).
            const string kBpId = "bp_self_test";
            var head = Rec("rec_self_head", "tree_self",
                parentBranchPointId: kBpId,
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed);
            head.ChainId = "chain_self";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var tip = Rec("rec_self_tip", "tree_self",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            tip.ChainId = "chain_self";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;

            RecordingStore.AddRecordingWithTreeForTesting(head, "tree_self");
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_self");
            var tree = new RecordingTree
            {
                Id = "tree_self",
                TreeName = "tree_self",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = kBpId,
                        Type = BranchPointType.Breakup,
                        UT = 0.0,
                        ChildRecordingIds = new List<string> { "rec_self_head" },
                    },
                },
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_self_head", provisionalId: "rec_self_head",
                treeId: "tree_self");
            var scenario = InstallScenario(marker);

            int countBefore = scenario.RecordingSupersedes.Count;
            var subtree = SupersedeCommit.AppendRelations(marker, head, scenario);

            // Closure includes both head and tip; head is filtered as
            // self-link, tip becomes a row.
            Assert.Contains("rec_self_head", subtree);
            Assert.Contains("rec_self_tip", subtree);
            int countAfter = scenario.RecordingSupersedes.Count;
            Assert.Equal(countBefore + 1, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_self_tip" && r.NewRecordingId == "rec_self_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_self_head" && r.NewRecordingId == "rec_self_head");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_self_head")
                && l.Contains("new=rec_self_head"));
        }

        // ---------- Optimizer-split chain-tip resolve (review follow-up) --
        // Bug fix (review follow-up to in-place-supersede): MergeCommit runs
        // RecordingStore.RunOptimizationPass() BEFORE TryCommitReFlySupersede.
        // If the in-place provisional crossed an env boundary, the optimizer's
        // SplitAtSection moves VesselSnapshot + TerminalStateValue from the
        // HEAD to a fresh TIP recording (RecordingOptimizer.cs lines 513-514
        // and 536-537). The HEAD then has TerminalStateValue == null, which
        // fails ValidateSupersedeTarget's null-terminal clause inside
        // AppendRelations: throws in DEBUG, returns empty in RELEASE — and
        // the sibling supersede rows the in-place fix needs are NOT written.
        // The fix uses EffectiveState.ResolveChainTerminalRecording to find
        // the TIP, passes it to AppendRelations as the validated target,
        // and adds the HEAD's id to extraSelfSkipRecordingIds so neither
        // the HEAD self-link nor the TIP self-link write a row (both halves
        // of the in-place chain are part of the new flight).

        /// <summary>
        /// Direct AppendRelations test: when the caller passes the resolved
        /// chain TIP and adds the HEAD id to extraSelfSkipRecordingIds, the
        /// HEAD's closure entry is filtered, the TIP's self-link is also
        /// filtered (old==new guard), and only the unrelated sibling gets a
        /// row. Independent of the dialog wiring above.
        /// </summary>
        [Fact]
        public void AppendRelations_ExtraSelfSkip_FiltersHeadWhileTipIsTheTarget()
        {
            const string kBpParent = "bp_p_es";
            const string kBpChild = "bp_c_es";
            var head = Rec("rec_es_head", "tree_es",
                parentBranchPointId: kBpParent,
                childBranchPointId: kBpChild,
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_es";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });

            var tip = Rec("rec_es_tip", "tree_es",
                state: MergeState.Immutable,
                terminal: TerminalState.Landed);
            tip.ChainId = "chain_es";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var sibling = Rec("rec_es_sib", "tree_es",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            sibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var bpParent = Bp(kBpParent, BranchPointType.Breakup,
                parents: new List<string>(),
                children: new List<string> { "rec_es_head" });
            var bpChild = Bp(kBpChild, BranchPointType.Breakup,
                parents: new List<string> { "rec_es_head" },
                children: new List<string> { "rec_es_sib" });

            InstallTree("tree_es",
                new List<Recording> { head, tip, sibling },
                new List<BranchPoint> { bpParent, bpChild });

            // Marker points at HEAD (in-place continuation; origin == provisional == head).
            var marker = Marker(originId: "rec_es_head", provisionalId: "rec_es_head",
                treeId: "tree_es");
            var scenario = InstallScenario(marker);

            // Caller (= MergeDialog in production) passes TIP as provisional
            // + HEAD id in extraSelfSkipRecordingIds.
            int countBefore = scenario.RecordingSupersedes.Count;
            var subtree = SupersedeCommit.AppendRelations(marker, tip, scenario,
                extraSelfSkipRecordingIds: new[] { "rec_es_head" });

            // Closure contains all three: head, tip, sibling.
            Assert.Contains("rec_es_head", subtree);
            Assert.Contains("rec_es_tip", subtree);
            Assert.Contains("rec_es_sib", subtree);

            // Exactly one row written: the sibling pointing at the tip.
            int countAfter = scenario.RecordingSupersedes.Count;
            Assert.Equal(countBefore + 1, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_sib" && r.NewRecordingId == "rec_es_tip");
            // Head NOT redirected to tip.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_head");
            // Tip NOT a self-link row.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_tip");

            // Logs: tip self-link skip + head extra-self-link skip both fire.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_es_tip")
                && l.Contains("new=rec_es_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_es_head")
                && l.Contains("new=rec_es_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 1 supersede relations")
                && l.Contains("skippedSelfLink=1")
                && l.Contains("skippedExtraSelfLink=1"));
        }

        /// <summary>
        /// 3-arg AppendRelations overload (the existing entry point used by
        /// CommitSupersede + MergeJournalOrchestrator) is unchanged: passes
        /// extraSelfSkipRecordingIds=null and behaves exactly like before.
        /// Pin this so the journaled merge path is not silently affected.
        /// </summary>
        [Fact]
        public void AppendRelations_LegacyThreeArgOverload_NoExtraSkip_BehavesAsBefore()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;
            // 3-arg call site (no extra skip).
            SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            int countAfter = scenario.RecordingSupersedes.Count;

            // Two rows written (origin + inside, both pointing at the
            // provisional). No extra-self-skip count > 0.
            Assert.Equal(countBefore + 2, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_inside" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 2 supersede relations")
                && l.Contains("skippedExtraSelfLink=0"));
        }

        // ---------- Multi-segment chain skip set (review follow-up #2) ----
        // Reviewer flagged that the prior fix only added the HEAD id to
        // extraSelfSkipRecordingIds. For a 3+-segment in-place chain
        // (HEAD -> MIDDLE -> TIP), AppendRelations would write a row
        // old=MIDDLE new=TIP and silently collapse MIDDLE in ERS via
        // EffectiveRecordingId redirect, even though MIDDLE is part of the
        // SAME new in-place flight. The fix builds the skip set from the
        // full chain membership (TreeId + ChainId + ChainBranch matches
        // from RecordingStore.CommittedRecordings — the same scope
        // EffectiveState.ComputeSubtreeClosureInternal +
        // EnqueueChainSiblings use) so no in-place chain segment ends up
        // with a row pointing at another member.

        [Fact]
        public void ValidateSupersedeTarget_ReasonStrings()
        {
            string reason;

            Assert.False(SupersedeCommit.ValidateSupersedeTarget(null, out reason));
            Assert.Equal("null recording", reason);

            var emptyPoints = new Recording { Points = new List<TrajectoryPoint>(), TerminalStateValue = TerminalState.Landed };
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(emptyPoints, out reason));
            Assert.Equal("empty Points", reason);

            var nullTerminal = new Recording { TerminalStateValue = null };
            nullTerminal.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(nullTerminal, out reason));
            Assert.Equal("null TerminalState", reason);

            var ok = new Recording { TerminalStateValue = TerminalState.Landed };
            ok.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.True(SupersedeCommit.ValidateSupersedeTarget(ok, out reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ValidateSupersedeTarget_SectionAuthoritativePayloadWithEmptyPoints_ReturnsTrue()
        {
            string reason;
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(),
                TerminalStateValue = TerminalState.SubOrbital
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 10.0,
                endUT = 10.1,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10.0, bodyName = "Kerbin", altitude = 1000.0 },
                    new TrajectoryPoint { ut = 10.1, bodyName = "Kerbin", altitude = 1010.0 }
                }
            });

            Assert.True(SupersedeCommit.ValidateSupersedeTarget(rec, out reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ValidateSupersedeTarget_OrbitalCheckpointPayloadWithEmptyPoints_ReturnsTrue()
        {
            string reason;
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(),
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = 20.0,
                endUT = 30.0,
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Kerbin",
                        startUT = 20.0,
                        endUT = 30.0,
                        semiMajorAxis = 700000.0,
                        eccentricity = 0.01,
                        inclination = 0.0
                    }
                }
            });

            Assert.True(SupersedeCommit.ValidateSupersedeTarget(rec, out reason));
            Assert.Null(reason);
        }
    }
}
