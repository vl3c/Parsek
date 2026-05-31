using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
            // override, Landed/Splashed/Orbiting on the player-chosen
            // slot all seal; SubOrbital does not (still in flight,
            // falls through to stableLeafUnconcluded).
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
            // override path. Player flew the focus slot to stable orbit; the
            // slot's effective tip becomes Immutable so the row drops out of UF.
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
        public void SubOrbitalStableLeaf_SlotLookupFailure_DoesNotThrow_FallsBackToInFlight()
        {
            // Sibling of OrbitingStableLeaf_SlotLookupFailure_ThrowsInsteadOfFallback.
            // SubOrbital is no longer in RequiresSlotAwareMergeClassification's
            // set (a suborbital arc is still in flight, not a seal-triggering
            // stable terminal), so a SubOrbital provisional whose slot lookup
            // fails must NOT throw. The v0.9 TerminalKindClassifier fallback
            // routes SubOrbital to InFlight kind, which lands the merge at
            // MergeState.Immutable with AutoSealSlot=false (slot stays open).
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.SubOrbital, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            // Must not throw.
            SupersedeCommit.CommitSupersede(
                scenario.ActiveReFlySessionMarker, provisional);

            // v0.9 fallback (TerminalKindClassifier.Classify(SubOrbital) ==
            // InFlight) lands at MergeState.Immutable with AutoSealSlot=false.
            // The Immutable here is "the recording is real", NOT "the slot
            // sealed" - slot-aware seal state cannot be set when slot lookup
            // failed, so the slot stays open in the rewind point.
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            // The v0.9 fallback path logs that slot lookup failed but did
            // NOT throw, falling back to the v0.9 terminalKind classifier.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("falling back to v0.9 terminalKind classifier")
                && l.Contains("terminal=SubOrbital"));
            // No auto-seal log line for this provisional (the fallback path
            // never sets AutoSealSlot=true; the dedicated auto-seal log lines
            // include autoSeal=True only when the seal actually fired).
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("rec_provisional")
                && l.Contains("autoSeal=True"));
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

        // ---------- Pre-rewind debris write-set filter -----------------------
        //
        // Bug repro: `logs/2026-05-15_2342_refly-debris-disappeared`. The user
        // re-flew the upper stage of a multi-stage launch. The closure walk
        // pulled in 6 debris recordings that physically separated BEFORE the
        // rewind point (StartUT 23.66–25.12, RP.UT ≈ 29.42) and 2 that
        // separated DURING the in-place re-fly. All 8 received supersede rows
        // at commit, hiding the 6 pre-rewind ones from the recordings UI even
        // though they are independent vessel histories the re-fly did not redo.
        //
        // Fix: `EnqueueDebrisChildren` closure inclusion stays as-is (so
        // PR #858 render carve-out + PR #860 watch/map-presence scoping keep
        // their behavior during active sessions), but `AppendRelations` now
        // filters its write-set by `IsPreRewindDebris(rec, marker)` and
        // returns the filtered subtree so `CommitTombstones` restricts
        // ledger tombstone scope to the same set.

        // Wrapper around AppendRelations so the per-test boilerplate stays
        // small: installs a tree with origin + debris children, adds a
        // provisional, installs a marker with the requested cutoff fields,
        // and returns the (scenario, subtree-returned-by-AppendRelations).
        private (ParsekScenario, IReadOnlyCollection<string>) RunAppendForDebrisFixture(
            double rewindPointUT, double invokedUT,
            params (string id, bool isDebris, double startUT, string parent)[] debrisRows)
        {
            const string treeId = "tree_prdb";
            const string originId = "rec_origin_prdb";
            const string provisionalId = "rec_prov_prdb";

            var origin = Rec(originId, treeId,
                childBranchPointId: "bp_origin_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            origin.ExplicitStartUT = 0.0;
            // EndUT must be well past rewindUT + eps so the new
            // chain-head carve-out (Task A3) does not fire on origin
            // itself — the fixture tests the debris-case branch and any
            // origin whose EndUT <= rewindUT + eps would now be carved
            // out as a chain head. Pre-Task-A3 fixtures used
            // EndUT = 5.0 which silently fired the new predicate.
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 5.0 });
            origin.ExplicitEndUT = 100.0;

            var recordings = new List<Recording> { origin };
            var bps = new List<BranchPoint>
            {
                // Single shared Breakup BP keeps the test minimal; debris
                // each point at it via ParentBranchPointId so the closure
                // walk's EnqueueDebrisChildren admits them.
                Bp("bp_origin_child", BranchPointType.Breakup,
                    parents: new List<string> { originId },
                    children: debrisRows
                        .Where(r => r.parent == originId)
                        .Select(r => r.id).ToList()),
            };
            foreach (var row in debrisRows)
            {
                var rec = Rec(row.id, treeId,
                    parentBranchPointId: "bp_origin_child",
                    state: MergeState.Immutable, terminal: TerminalState.Destroyed);
                rec.ExplicitStartUT = row.startUT;
                rec.IsDebris = row.isDebris;
                if (row.isDebris) rec.ParentAnchorRecordingId = row.parent;
                // Pass 7: IsPreRewindCarveOut now reads bounds from
                // TryGetActualTrajectoryBounds (sampled content only). Add
                // a single Point at the row's startUT so the debris has
                // actual bounds matching its Explicit metadata; otherwise
                // the predicate falls through and pre-rewind debris would
                // erroneously receive supersede rows in this fixture.
                rec.Points.Add(new TrajectoryPoint { ut = row.startUT });
                recordings.Add(rec);
            }
            InstallTree(treeId, recordings, bps);

            var provisional = AddProvisional(provisionalId, treeId,
                TerminalState.Landed, supersedeTargetId: originId);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_prdb",
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = "rp_prdb",
                InvokedUT = invokedUT,
                RewindPointUT = rewindPointUT,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);

            var subtree = SupersedeCommit.AppendRelations(marker, provisional, scenario);
            return (scenario, subtree);
        }

        [Fact]
        public void AppendRelations_PreRewindDebris_NoSupersedeRow_NotInReturnedSubtree()
        {
            // Single pre-rewind debris: gets no supersede row and is dropped
            // from the returned subtree so downstream CommitTombstones leaves
            // its attributed ledger actions alone.
            var (scenario, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: 29.42, invokedUT: 29.42,
                ("rec_debris_pre", true, 23.66, "rec_origin_prdb"));

            Assert.Single(scenario.RecordingSupersedes);
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_pre");
            // Origin itself still gets a row.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin_prdb");
            Assert.Contains("rec_origin_prdb", subtree);
            Assert.DoesNotContain("rec_debris_pre", subtree);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip pre-rewind PreRewindDebris")
                && l.Contains("old=rec_debris_pre"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("skippedPreRewindCarveOut=1"));
        }

        [Fact]
        public void AppendRelations_PostRewindDebris_RowWritten()
        {
            // Post-rewind debris (StartUT >= cutoff) is still in the
            // write-set and the returned subtree.
            var (scenario, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: 29.42, invokedUT: 29.42,
                ("rec_debris_post", true, 34.10, "rec_origin_prdb"));

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_post");
            Assert.Contains("rec_debris_post", subtree);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("skippedPreRewindCarveOut=0"));
        }

        [Fact]
        public void AppendRelations_DebrisAtRewindPointUtBoundary_RowWritten()
        {
            // Boundary case: StartUT exactly at the cutoff
            // (RewindPointUT - PidPeerStartUtEpsilonSeconds = 29.42 - 0.05).
            // The gate is strict `<`, so this debris is admitted (it
            // separated at-or-after the epsilon-tolerated rewind moment).
            const double rp = 29.42;
            var (scenario, _) = RunAppendForDebrisFixture(
                rewindPointUT: rp, invokedUT: rp,
                ("rec_debris_boundary", true, rp - 0.05, "rec_origin_prdb"));

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_boundary");
        }

        [Fact]
        public void AppendRelations_MixedPreAndPostRewind_OnlyPostRowsWritten()
        {
            // The user's repro: 6 pre-rewind debris (StartUT 23.66–25.12) +
            // 2 post-rewind debris (StartUT 34.10, 37.14) + origin. Pre-rewind
            // debris stays out of the supersede write-set and out of the
            // returned subtree; the 2 post-rewind debris and the origin are
            // each superseded; the summary log reports skippedPreRewindCarveOut=6.
            var (scenario, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: 29.42, invokedUT: 29.42,
                ("rec_debris_pre_1", true, 23.66, "rec_origin_prdb"),
                ("rec_debris_pre_2", true, 23.66, "rec_origin_prdb"),
                ("rec_debris_pre_3", true, 24.36, "rec_origin_prdb"),
                ("rec_debris_pre_4", true, 24.36, "rec_origin_prdb"),
                ("rec_debris_pre_5", true, 25.12, "rec_origin_prdb"),
                ("rec_debris_pre_6", true, 25.12, "rec_origin_prdb"),
                ("rec_debris_post_1", true, 34.10, "rec_origin_prdb"),
                ("rec_debris_post_2", true, 37.14, "rec_origin_prdb"));

            // Rows: 2 post-rewind debris + 1 origin = 3.
            Assert.Equal(3, scenario.RecordingSupersedes.Count);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin_prdb");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_post_1");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_post_2");
            for (int i = 1; i <= 6; i++)
                Assert.DoesNotContain(scenario.RecordingSupersedes,
                    r => r.OldRecordingId == $"rec_debris_pre_{i}");

            // Returned subtree (consumed by CommitTombstones) drops pre-rewind
            // debris but keeps post-rewind debris + origin.
            Assert.Contains("rec_origin_prdb", subtree);
            Assert.Contains("rec_debris_post_1", subtree);
            Assert.Contains("rec_debris_post_2", subtree);
            for (int i = 1; i <= 6; i++)
                Assert.DoesNotContain($"rec_debris_pre_{i}", subtree);

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("skippedPreRewindCarveOut=6"));
        }

        // ---------------------------------------------------------------
        // Regression: "v0.9.2 In-place Re-Fly debris attributed to pre-rewind
        // root recording". The user's repro had 2 debris that separated DURING
        // an in-place re-fly. The bug anchored them to the pre-rewind origin, so
        // the supersede closure (rooted at the origin) swept them up and the ERS
        // filter hid them after commit, when they conceptually belong to the new
        // continuation and should stay visible as its child debris.
        //
        // The recorder now swaps tree.ActiveRecordingId to the continuation fork
        // before the breakup is processed (proven in
        // Bug585InPlaceContinuationRestoreTests.ResolveInPlaceContinuationTarget_
        // InPlaceMarker_SwapsTarget), so CreateBreakupChildRecording anchors redo
        // debris to the fork (== marker.ActiveReFlyRecordingId), not the origin.
        //
        // This test pins the downstream half: post-rewind debris parented to the
        // continuation are OUTSIDE the superseded closure (which is rooted at the
        // origin / supersede target), so they get no supersede row, drop out of
        // the tombstone-scope subtree, and stay visible — while an origin-parented
        // post-rewind debris (genuine original-timeline breakup the re-fly redid)
        // is still superseded and hidden.
        // ---------------------------------------------------------------
        [Fact]
        public void AppendRelations_PostRewindDebrisParentedToContinuation_StaysVisible()
        {
            const string treeId = "tree_cont";
            const string originId = "rec_origin_cont";
            const string provisionalId = "rec_prov_cont";

            var origin = Rec(originId, treeId,
                childBranchPointId: "bp_origin_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            origin.ExplicitStartUT = 0.0;
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 5.0 });
            // EndUT well past rewindUT + eps so the chain-head carve-out does not
            // fire on origin itself (mirrors RunAppendForDebrisFixture).
            origin.ExplicitEndUT = 100.0;

            // Origin-parented post-rewind debris (original-timeline breakup the
            // re-fly redid) → still superseded + hidden.
            var debrisOrigin = Rec("d_origin_post", treeId,
                parentBranchPointId: "bp_origin_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            debrisOrigin.IsDebris = true;
            debrisOrigin.ParentAnchorRecordingId = originId;
            debrisOrigin.ExplicitStartUT = 34.10;
            debrisOrigin.Points.Add(new TrajectoryPoint { ut = 34.10 });

            // The user's two redo debris (StartUT 34.10 and 37.14) the recorder
            // anchored to the continuation fork → must stay visible after commit.
            var debrisCont1 = Rec("d_cont_post_1", treeId,
                parentBranchPointId: "bp_prov_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            debrisCont1.IsDebris = true;
            debrisCont1.ParentAnchorRecordingId = provisionalId;
            debrisCont1.ExplicitStartUT = 34.10;
            debrisCont1.Points.Add(new TrajectoryPoint { ut = 34.10 });

            var debrisCont2 = Rec("d_cont_post_2", treeId,
                parentBranchPointId: "bp_prov_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            debrisCont2.IsDebris = true;
            debrisCont2.ParentAnchorRecordingId = provisionalId;
            debrisCont2.ExplicitStartUT = 37.14;
            debrisCont2.Points.Add(new TrajectoryPoint { ut = 37.14 });

            var bps = new List<BranchPoint>
            {
                Bp("bp_origin_child", BranchPointType.Breakup,
                    parents: new List<string> { originId },
                    children: new List<string> { "d_origin_post" }),
                Bp("bp_prov_child", BranchPointType.Breakup,
                    parents: new List<string> { provisionalId },
                    children: new List<string> { "d_cont_post_1", "d_cont_post_2" }),
            };

            InstallTree(treeId,
                new List<Recording> { origin, debrisOrigin, debrisCont1, debrisCont2 },
                bps);

            var provisional = AddProvisional(provisionalId, treeId,
                TerminalState.Landed, supersedeTargetId: originId);
            provisional.ChildBranchPointId = "bp_prov_child";

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_cont",
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = "rp_cont",
                InvokedUT = 29.42,
                RewindPointUT = 29.42,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);

            var subtree = SupersedeCommit.AppendRelations(marker, provisional, scenario);

            // Origin-parented post-rewind debris IS superseded → hidden.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "d_origin_post");
            Assert.False(EffectiveState.IsVisible(debrisOrigin, scenario.RecordingSupersedes),
                "origin-parented post-rewind debris must be superseded/hidden after commit");

            // The two continuation-parented redo debris get NO supersede row,
            // never enter the tombstone-scope subtree (they are outside the
            // closure rooted at the origin / supersede target), and stay visible.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "d_cont_post_1");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "d_cont_post_2");
            Assert.DoesNotContain("d_cont_post_1", subtree);
            Assert.DoesNotContain("d_cont_post_2", subtree);
            Assert.True(EffectiveState.IsVisible(debrisCont1, scenario.RecordingSupersedes),
                "continuation-parented redo debris must stay visible after commit");
            Assert.True(EffectiveState.IsVisible(debrisCont2, scenario.RecordingSupersedes),
                "continuation-parented redo debris must stay visible after commit");
        }

        [Fact]
        public void AppendRelations_NaNRewindPointUT_FallsBackToInvokedUT()
        {
            // Legacy marker pattern: RewindPointUT is NaN (field default).
            // The cutoff falls back to InvokedUT - eps, matching the
            // EnqueuePidPeerSiblings cutoff and giving us the same
            // pre-rewind exclusion behavior for legacy sessions.
            var (scenario, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: double.NaN, invokedUT: 29.42,
                ("rec_debris_pre", true, 20.0, "rec_origin_prdb"),
                ("rec_debris_post", true, 35.0, "rec_origin_prdb"));

            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_pre");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_post");
            Assert.DoesNotContain("rec_debris_pre", subtree);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void AppendRelations_NonPositiveRewindPointUT_FallsBackToInvokedUT(double rp)
        {
            // Defensive: zero / negative RewindPointUT is treated as unset
            // (same convention as ShouldRenderSuppressedCompanionDebris and
            // documented on the ReFlySessionMarker.RewindPointUT field).
            // The cutoff falls back to InvokedUT - eps.
            var (scenario, _) = RunAppendForDebrisFixture(
                rewindPointUT: rp, invokedUT: 29.42,
                ("rec_debris_pre", true, 20.0, "rec_origin_prdb"));

            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_pre");
        }

        [Fact]
        public void AppendRelations_BothCutoffsUnset_NoFilteringApplied()
        {
            // No usable cutoff: ComputePreRewindCutoff returns NaN and
            // IsPreRewindDebris fails open. Pre-fix behavior (every debris
            // gets a row) is preserved so a half-populated marker doesn't
            // silently hide debris.
            var (scenario, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: double.NaN, invokedUT: 0.0,
                ("rec_debris_old", true, 20.0, "rec_origin_prdb"));

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_debris_old");
            Assert.Contains("rec_debris_old", subtree);
        }

        [Fact]
        public void AppendRelations_NonDebrisPostRewindRecording_StillGetsSupersedeRow()
        {
            // A non-debris recording that ends post-rewind (EndUT
            // > rewindUT + eps) must still get a supersede row — Task A3's
            // chain-head carve-out only fires when EndUT <= rewindUT + eps.
            // This locks in that post-rewind non-debris recordings (the
            // TIP-side of a chain, pid-peers, BP children) keep their rows.
            //
            // Pre-Task-A3 the same fixture used a non-debris recording
            // whose EndUT defaulted to 0.0 (no Points / no ExplicitEndUT)
            // and asserted "non-debris is never carved out". Under Task A3,
            // EndUT == 0.0 <= rewindUT + eps matches the chain-head
            // predicate; the test was renamed and the child reshaped to a
            // clearly-post-rewind recording so the assertion exercises the
            // path it was meant to exercise.
            const string treeId = "tree_postrw";
            const string originId = "rec_origin_postrw";
            const string childId = "rec_child_postrw";
            const string provisionalId = "rec_prov_postrw";

            var origin = Rec(originId, treeId,
                childBranchPointId: "bp_origin_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            origin.ExplicitStartUT = 0.0;
            origin.ExplicitEndUT = 100.0; // past rewindUT + eps
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var child = Rec(childId, treeId,
                parentBranchPointId: "bp_origin_child",
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            child.ExplicitStartUT = 35.0; // > rewindUT - eps
            child.ExplicitEndUT = 50.0;   // > rewindUT + eps
            // IsDebris stays false (default).

            var bps = new List<BranchPoint>
            {
                Bp("bp_origin_child", BranchPointType.Breakup,
                    parents: new List<string> { originId },
                    children: new List<string> { childId }),
            };
            InstallTree(treeId,
                new List<Recording> { origin, child }, bps);

            var provisional = AddProvisional(provisionalId, treeId,
                TerminalState.Landed, supersedeTargetId: originId);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_postrw",
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = "rp_postrw",
                InvokedUT = 29.42,
                RewindPointUT = 29.42,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == childId);
        }

        [Fact]
        public void IsPreRewindDebris_DebrisWithoutParentAnchorRecordingId_ReturnsFalse()
        {
            // Legacy v11 debris loaded without ParentAnchorRecordingId has
            // no v12 ownership link. The closure walk's
            // EnqueueDebrisChildren refuses to admit such rows in the first
            // place, but if some other path put it in the closure, the
            // IsPreRewindDebris gate keeps its defensive parent-required
            // guard so it follows the unfiltered legacy path.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 29.42,
                InvokedUT = 29.42,
                PreSessionBranchPointIds = new List<string>(),
            };
            var legacyDebris = new Recording
            {
                RecordingId = "rec_legacy",
                IsDebris = true,
                // ParentAnchorRecordingId intentionally null
                ExplicitStartUT = 10.0,
            };
            Assert.False(SupersedeCommit.IsPreRewindDebris(legacyDebris, marker));
        }

        [Fact]
        public void IsPreRewindDebris_NullInputs_ReturnFalse()
        {
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 29.42,
                PreSessionBranchPointIds = new List<string>(),
            };
            var rec = new Recording
            {
                RecordingId = "rec_d",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_parent",
                ExplicitStartUT = 10.0,
            };

            Assert.False(SupersedeCommit.IsPreRewindDebris(null, marker));
            Assert.False(SupersedeCommit.IsPreRewindDebris(rec, null));
            Assert.False(SupersedeCommit.IsPreRewindDebris(null, null));
        }

        [Theory]
        [InlineData(29.42, double.NaN, 29.37)]  // RewindPointUT path
        [InlineData(double.NaN, 29.42, 29.37)]  // InvokedUT fallback
        [InlineData(29.42, 50.0, 29.37)]        // RewindPointUT wins when both set
        public void ComputePreRewindCutoff_ResolvesExpectedField(
            double rp, double invoked, double expected)
        {
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = rp,
                InvokedUT = invoked,
                PreSessionBranchPointIds = new List<string>(),
            };
            double actual = SupersedeCommit.ComputePreRewindCutoff(marker);
            Assert.Equal(expected, actual, 10);
        }

        [Theory]
        [InlineData(double.NaN, 0.0)]    // both unset
        [InlineData(0.0, double.NaN)]    // both unset (zero rp)
        [InlineData(-1.0, -1.0)]         // both negative
        public void ComputePreRewindCutoff_NoUsableField_ReturnsNaN(
            double rp, double invoked)
        {
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = rp,
                InvokedUT = invoked,
                PreSessionBranchPointIds = new List<string>(),
            };
            Assert.True(double.IsNaN(SupersedeCommit.ComputePreRewindCutoff(marker)));
        }

        [Fact]
        public void ComputePreRewindCutoff_NullMarker_ReturnsNaN()
        {
            Assert.True(double.IsNaN(SupersedeCommit.ComputePreRewindCutoff(null)));
        }

        [Fact]
        public void AppendRelationsReturnValue_FilteredSubtreeExcludesPreRewindDebrisFromTombstoneScope()
        {
            // The user-visible secondary effect: AppendRelations's return
            // value flows directly into CommitTombstones as the subtree set
            // (see CommitSupersede.cs:96 / MergeJournalOrchestrator.cs:208).
            // TombstoneAttributionHelper.InSupersedeScope checks
            // action.RecordingId membership in that set, so any ledger
            // action attributed to a pre-rewind debris will be left alone
            // automatically once the returned subtree excludes the debris
            // id. Without this filter, a pre-rewind kerbal death would be
            // undone at commit even though the debris recording stays
            // visible after this fix — internally inconsistent state.
            var (_, subtree) = RunAppendForDebrisFixture(
                rewindPointUT: 29.42, invokedUT: 29.42,
                ("rec_debris_pre", true, 23.66, "rec_origin_prdb"),
                ("rec_debris_post", true, 35.0, "rec_origin_prdb"));

            var subtreeSet = new HashSet<string>(subtree, StringComparer.Ordinal);

            var preRewindAction = new GameAction
            {
                ActionId = "act_prdb_pre",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_debris_pre",
                UT = 24.0,
                KerbalEndStateField = KerbalEndState.Dead,
            };
            var postRewindAction = new GameAction
            {
                ActionId = "act_prdb_post",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_debris_post",
                UT = 36.0,
                KerbalEndStateField = KerbalEndState.Dead,
            };

            Assert.False(TombstoneAttributionHelper.InSupersedeScope(
                preRewindAction, subtreeSet),
                "Pre-rewind debris action must drop out of tombstone scope.");
            Assert.True(TombstoneAttributionHelper.InSupersedeScope(
                postRewindAction, subtreeSet),
                "Post-rewind debris action must remain in tombstone scope.");
        }

        // ---------- IsPreRewindCarveOut (Task A3) --------------------------
        // Generalizes the IsPreRewindDebris predicate to also carve out the
        // post-split chain HEAD a future Task-A4 SplitOriginAtRewindUT
        // orchestrator will produce. The new chain-head case is dormant
        // today (no caller manufactures HEAD with EndUT == rewindUT until
        // Task A4 lands) but these tests lock in the contract so the moment
        // the orchestrator ships, AppendRelations carves HEAD out
        // automatically. Boundary semantics:
        //   - Debris case (lower-boundary test): StartUT < rewindUT - eps.
        //   - Chain-head case (upper-boundary test): EndUT <= rewindUT + eps.
        // The asymmetric epsilon sign matters: a first-revision draft of
        // Task A3 would have shipped a unified `rewindUT - eps` cutoff for
        // BOTH predicates, making HEAD's `EndUT == rewindUT` fail the test
        // and reintroducing the very bug Task A1-A8 exists to fix.
        //
        // Pass 7: both predicates now read bounds from
        // `Recording.TryGetActualTrajectoryBounds` (sampled content only,
        // ExplicitStartUT/EndUT excluded). Fixtures below carry a minimal
        // two-Point trajectory matching their Explicit bounds so the
        // predicate has actual content to evaluate; the bounds happen to
        // equal Explicit*UT in these tests but the assertions exercise the
        // ACTUAL view.

        private static void StampActualBounds(Recording rec, double startUT, double endUT)
        {
            // Add minimal Points so `HasActualTrajectoryBounds` is true and
            // `TryGetActualTrajectoryBounds` returns the supplied range.
            // Pass 7 of fix-supersede-identity-scope: `IsPreRewindCarveOut`
            // now reads bounds from sampled content, not the
            // ExplicitStartUT/EndUT-blended view.
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            });
            if (endUT > startUT)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = endUT,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                });
            }
        }

        [Fact]
        public void IsPreRewindCarveOut_PreRewindDebris_ReturnsTrueWithReason()
        {
            // Debris recording that physically separated strictly before
            // the rewind UT — the original PR #858 carve-out shape.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var debris = new Recording
            {
                RecordingId = "rec_debris_carveout",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_carveout",
                ExplicitStartUT = 22.5, // < 34.0 - 0.05
                ExplicitEndUT = 28.0,
            };
            StampActualBounds(debris, 22.5, 28.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                debris, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindDebris, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_PreRewindChainHead_ReturnsTrueWithReason()
        {
            // Pass 4: chain-shape predicate (not id-match). HEAD must share
            // TIP's ChainId+ChainBranch with a lower ChainIndex and an EndUT
            // at or before rewindUT + eps. TIP must be reachable via
            // RecordingStore.CommittedRecordings so the lookup at
            // FindCommittedRecordingByIdForCarveOut succeeds.
            var tip = new Recording
            {
                RecordingId = "rec_tip_pass4",
                IsDebris = false,
                ChainId = "chain_pass4",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_pass4",
                PreSessionBranchPointIds = new List<string>(),
            };
            var head = new Recording
            {
                RecordingId = "rec_head_carveout",
                IsDebris = false,
                ChainId = "chain_pass4",
                ChainBranch = 0,
                ChainIndex = 0,
                ExplicitStartUT = 8.42,
                ExplicitEndUT = 34.0, // == rewindUT exactly
            };
            StampActualBounds(head, 8.42, 34.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                head, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindChainHead, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_ProvisionalAnchoredOnSupersedeTarget_DoesNotCarveOutParent()
        {
            // fix-refly-relative-anchor Phase 4 audit: ReFlyAnchorSelection
            // writes TrackSection.anchorRecordingId on the provisional's
            // Relative sections, pointing at the supersede target. The
            // carve-out filter must NOT pick up that edge and reclassify the
            // supersede target (TIP) as a pre-rewind carve-out — TIP is the
            // recording the provisional supersedes, not a pre-rewind chain
            // sibling. This test pins that invariant by building TIP with the
            // expected post-rewind shape (ChainIndex >= provisional's index,
            // EndUT past rewindUT) AND a TrackSection.anchorRecordingId edge
            // back to itself (mirroring what the recorder writes for a
            // boundary-relative provisional). The carve-out filter walks
            // chain shape only and must still return False with reason=None.
            var tip = new Recording
            {
                RecordingId = "rec_tip_provisional_anchor",
                IsDebris = false,
                ChainId = "chain_anchor_audit",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            // Add a Relative TrackSection whose anchorRecordingId points
            // back at this recording (the section-level edge the Phase 2
            // bypass authors on a re-fly provisional). The carve-out filter
            // does not read TrackSection fields, so this edge must be
            // invisible to the predicate.
            var tipSection = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 34.0,
                endUT = 52.0,
                anchorRecordingId = "rec_tip_provisional_anchor",
                anchorVesselId = 0u,
            };
            tip.TrackSections.Add(tipSection);
            StampActualBounds(tip, 34.0, 52.0);
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_provisional_anchor",
                PreSessionBranchPointIds = new List<string>(),
            };

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                tip, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NonHeadSiblingEndingAtRewindUT_NotCarvedOut()
        {
            // Pass 2 review User-H2: an EVA recording ending on a Board BP at
            // exactly rewindUT lives on a DIFFERENT chain (EVA has its own
            // vessel identity → its own ChainId). The chain-shape predicate
            // correctly excludes it from the carve-out and lets it receive
            // its legitimate supersede row.
            var tip = new Recording
            {
                RecordingId = "rec_tip_neg",
                ChainId = "chain_main",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_neg",
                PreSessionBranchPointIds = new List<string>(),
            };
            var unrelatedSiblingEndingAtRewind = new Recording
            {
                RecordingId = "rec_eva_or_fairing",
                IsDebris = false,
                ChainId = "chain_eva", // DIFFERENT chain
                ChainBranch = 0,
                ChainIndex = 0,
                ExplicitStartUT = 12.0,
                ExplicitEndUT = 34.0, // same as rewindUT — but DIFFERENT chain
            };
            StampActualBounds(unrelatedSiblingEndingAtRewind, 12.0, 34.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                unrelatedSiblingEndingAtRewind, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NoSupersedeTargetIdOnMarker_NotCarvedOut()
        {
            // Pass 4: a marker without SupersedeTargetId can't resolve TIP, so
            // the chain-head case is unavailable. The recording falls through
            // to the legacy "write the row" default.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = null,
                PreSessionBranchPointIds = new List<string>(),
            };
            var head = new Recording
            {
                RecordingId = "rec_head_no_tip_id",
                IsDebris = false,
                ChainId = "chain_orphan",
                ChainBranch = 0,
                ChainIndex = 0,
                ExplicitStartUT = 8.0,
                ExplicitEndUT = 34.0,
            };
            StampActualBounds(head, 8.0, 34.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                head, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NestedReFly_HEAD2EqualsFork1_CarvedOutCorrectly()
        {
            // Pass 4 regression test: this is the case my Pass 2 id-match
            // form silently failed. On the second Re-Fly of the same slot,
            // marker.OriginChildRecordingId is the slot's stable origin
            // (HEAD₁.id from the first Re-Fly), but the second split operates
            // on fork₁ (which becomes HEAD₂ in place). HEAD₂.RecordingId is
            // fork₁.id, not HEAD₁.id — the id-match form did NOT fire and
            // HEAD₂ got a supersede row pointing at fork₂, silently re-
            // introducing the very bug this PR exists to fix.
            //
            // The chain-shape predicate fires correctly because HEAD₂
            // (= fork₁) and TIP₂ are chain siblings on a fresh ChainId Y
            // (the second Re-Fly's chain); marker.OriginChildRecordingId
            // doesn't participate.

            // First Re-Fly's wreckage in the store (HEAD₁ + TIP₁ + fork₁,
            // all on chain X). We only need the supersede-row consumer
            // shape, not full topology.
            var fork1Head2 = new Recording
            {
                RecordingId = "fork1_id",
                IsDebris = false,
                ChainId = "chain_Y",   // fresh chain from the second Re-Fly's split
                ChainBranch = 0,
                ChainIndex = 0,        // HEAD₂ position
                ExplicitStartUT = 34.0, // fork₁ originally started at rewindUT₁
                ExplicitEndUT = 60.0,   // ends at the SECOND rewindUT
            };
            var tip2 = new Recording
            {
                RecordingId = "tip2_id",
                ChainId = "chain_Y",
                ChainBranch = 0,
                ChainIndex = 1,        // TIP₂ position
                ExplicitStartUT = 60.0,
                ExplicitEndUT = 90.0,
            };
            StampActualBounds(fork1Head2, 34.0, 60.0);
            StampActualBounds(tip2, 60.0, 90.0);
            RecordingStore.AddCommittedInternal(fork1Head2);
            RecordingStore.AddCommittedInternal(tip2);

            // Second Re-Fly's marker. OriginChildRecordingId still points at
            // the slot's original recording (HEAD₁.id) — this is what
            // tripped my Pass 2 id-match form.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_refly_2",
                RewindPointUT = 60.0,
                InvokedUT = 60.0,
                OriginChildRecordingId = "head1_id", // SLOT's stable origin
                SupersedeTargetId = "tip2_id",        // post-second-split TIP
                ActiveReFlyRecordingId = "fork2_id",
                PreSessionBranchPointIds = new List<string>(),
            };

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                fork1Head2, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindChainHead, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_PostRewindChainSibling_ReturnsFalse()
        {
            // Post-rewind chain sibling: StartUT == rewindUT, EndUT well past
            // rewindUT + eps. Must NOT be carved out — it's the TIP-side
            // chain member that should still receive a supersede row.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var tip = new Recording
            {
                RecordingId = "rec_tip_carveout",
                IsDebris = false,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(tip, 34.0, 52.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                tip, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NonDebrisSpansRewindUT_ReturnsFalse()
        {
            // The PRE-split origin from the user's 2026-05-16 repro: a
            // single recording covering [8.42, 52.7] that straddles
            // rewindUT=34.0. Task A4's orchestrator will SPLIT this into
            // HEAD + TIP; this test locks in that pre-split origin is NOT
            // carved out by mistake — the orchestrator runs first, then the
            // closure walk finds HEAD as a chain sibling of TIP, and HEAD's
            // EndUT == rewindUT triggers the chain-head case. Until the
            // split runs, the spanning recording must receive a row.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var spanningOrigin = new Recording
            {
                RecordingId = "rec_origin_spanning",
                IsDebris = false,
                ExplicitStartUT = 8.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(spanningOrigin, 8.0, 52.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                spanningOrigin, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_DebrisDoesNotMatchChainHead_StillReturnsDebrisReason()
        {
            // Predicate ordering test: a debris recording whose StartUT is
            // strictly pre-rewind AND whose EndUT happens to be at rewindUT
            // matches both predicates' type-agnostic UT tests. The
            // implementation checks the debris case FIRST (per the plan's
            // code block), so the debris reason wins. This confirms the
            // ordering — a future refactor that flipped the order would
            // mis-tag this recording as a chain head.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var debrisAtBoundary = new Recording
            {
                RecordingId = "rec_debris_at_boundary",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_carveout",
                ExplicitStartUT = 20.0, // < 34.0 - 0.05
                ExplicitEndUT = 34.0,  // == rewindUT
            };
            StampActualBounds(debrisAtBoundary, 20.0, 34.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                debrisAtBoundary, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindDebris, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NaNRewindPointUT_FallsBackToDebrisCutoff()
        {
            // Legacy marker pattern: RewindPointUT is NaN, debris case falls
            // back to ComputePreRewindCutoff(InvokedUT - eps). The chain-head
            // case is gated on RewindPointUT directly (NOT via
            // ComputePreRewindCutoff) because chain-head testing requires
            // the ACTUAL rewind UT, not a fallback approximation — so the
            // chain-head predicate is unavailable for legacy markers and
            // only the debris case fires.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = double.NaN,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var debris = new Recording
            {
                RecordingId = "rec_debris_legacy",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_legacy",
                ExplicitStartUT = 20.0, // < 34.0 - 0.05
                ExplicitEndUT = 28.0,
            };
            StampActualBounds(debris, 20.0, 28.0);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                debris, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindDebris, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NaNAllUTs_ReturnsFalse()
        {
            // Half-populated legacy marker: both RewindPointUT and InvokedUT
            // are NaN / non-positive. Both predicates fall back to "no
            // cutoff available" and return false — the pre-fix default
            // (write the row) is preserved per the no-backward-compat
            // policy for pre-1.0 saves.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = double.NaN,
                InvokedUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var debris = new Recording
            {
                RecordingId = "rec_debris_nomarker",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_nomarker",
                ExplicitStartUT = 5.0,
                ExplicitEndUT = 10.0,
            };
            StampActualBounds(debris, 5.0, 10.0);
            var head = new Recording
            {
                RecordingId = "rec_head_nomarker",
                IsDebris = false,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 34.0,
            };
            StampActualBounds(head, 0.0, 34.0);

            Assert.False(SupersedeCommit.IsPreRewindCarveOut(
                debris, marker, out var debrisReason));
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, debrisReason);

            Assert.False(SupersedeCommit.IsPreRewindCarveOut(
                head, marker, out var headReason));
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, headReason);
        }

        [Fact]
        public void IsPreRewindCarveOut_NoActualTrajectoryBounds_NotCarvedOut()
        {
            // Pass 7 regression test (consumer-side defense): a recording
            // with no Points / OrbitSegments / playable TrackSections must
            // never be carved out, even if its (Explicit-only or all-zero)
            // metadata satisfies every other predicate. This pins the bug
            // class closed against:
            //   1. Future producer regressions that recreate empty HEADs.
            //   2. Legacy saves authored under the pre-Pass-7 splitter,
            //      which may carry an empty HEAD whose chain shape still
            //      matches TIP (StartUT=EndUT=0, ChainId set, ChainIndex<TIP).
            // The carve-out's contract is "keep visible recordings whose
            // data predates the rewind". Zero content = no launch portion
            // to protect; the recording should fall through to a normal
            // whole-recording supersede row.
            var tip = new Recording
            {
                RecordingId = "rec_tip_pass7",
                IsDebris = false,
                ChainId = "chain_pass7",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(tip, 34.0, 52.0);
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_pass7",
                PreSessionBranchPointIds = new List<string>(),
            };

            // Empty HEAD that satisfies the chain-head predicate on metadata
            // but has zero sampled content. Bounds default to StartUT=0,
            // EndUT=0; chain shape matches TIP at ChainIndex 0 vs 1.
            var emptyHead = new Recording
            {
                RecordingId = "rec_empty_head_pass7",
                IsDebris = false,
                ChainId = "chain_pass7",
                ChainBranch = 0,
                ChainIndex = 0,
                // Deliberately no Points / TrackSections / OrbitSegments —
                // exercises the no-actual-bounds branch.
                ExplicitStartUT = double.NaN,
                ExplicitEndUT = double.NaN,
            };
            Assert.False(emptyHead.HasActualTrajectoryBounds);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                emptyHead, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_StaleExplicitStartUT_DebrisDoesNotEscape()
        {
            // Pass 7 regression test: a debris recording that physically
            // separated AFTER the rewind UT but carries an inherited
            // ExplicitStartUT < rewindUT (set when the debris child was
            // first created, anchored to the parent's branchUT) must NOT
            // be carved out as pre-rewind. Reading bounds from sampled
            // content prevents the misclassification.
            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var debris = new Recording
            {
                RecordingId = "rec_debris_stale_start",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_stale",
                // Stale logical start (parent's branchUT). Sampled content
                // actually starts at UT=40 — strictly post-rewind.
                ExplicitStartUT = 8.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(debris, 40.0, 52.0);

            // Sanity: blended StartUT exposes the stale Explicit value, but
            // actual bounds match the sampled content.
            Assert.Equal(8.0, debris.StartUT);
            Assert.True(debris.TryGetActualTrajectoryBounds(
                out double actualStart, out _));
            Assert.Equal(40.0, actualStart);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                debris, marker, out var reason);

            Assert.False(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.None, reason);
        }

        [Fact]
        public void IsPreRewindCarveOut_StaleExplicitEndUT_GenuineHeadStillCarvedOut()
        {
            // Pass 7 regression test: a genuine HEAD half whose actual data
            // ends at rewindUT but whose stale ExplicitEndUT carries a
            // POST-rewind value must STILL be carved out (its actual content
            // is pre-rewind launch data — the stale metadata must not block
            // protection). Reading bounds from sampled content lets the
            // predicate fire.
            var tip = new Recording
            {
                RecordingId = "rec_tip_stale_end",
                IsDebris = false,
                ChainId = "chain_stale_end",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(tip, 34.0, 52.0);
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_stale_end",
                PreSessionBranchPointIds = new List<string>(),
            };
            var head = new Recording
            {
                RecordingId = "rec_head_stale_end",
                IsDebris = false,
                ChainId = "chain_stale_end",
                ChainBranch = 0,
                ChainIndex = 0,
                ExplicitStartUT = 8.42,
                // Stale ExplicitEndUT carried over from before some trim
                // pass — sits past rewindUT + epsilon.
                ExplicitEndUT = 99.0,
            };
            // Actual sampled content ends at the rewind UT.
            StampActualBounds(head, 8.42, 34.0);

            // Sanity: blended EndUT exposes the stale Explicit value; actual
            // bounds reflect the sampled content.
            Assert.Equal(99.0, head.EndUT);
            Assert.True(head.TryGetActualTrajectoryBounds(out _, out double actualEnd));
            Assert.Equal(34.0, actualEnd);

            bool result = SupersedeCommit.IsPreRewindCarveOut(
                head, marker, out var reason);

            Assert.True(result);
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindChainHead, reason);
        }

        [Fact]
        public void AppendRelations_FilterBlockUsesCarveOut_ChainHeadSkipped()
        {
            // Integration test: scenario with origin + chain TIP + fork
            // (provisional). Origin has EndUT == rewindUT (acts as HEAD
            // after a hypothetical split), TIP has StartUT == rewindUT,
            // fork is the post-rewind provisional. The closure walk starts
            // at TIP (marker.SupersedeTargetId = TIP), enqueues origin as a
            // chain sibling, and AppendRelations carves origin out via the
            // PreRewindChainHead reason. Result: a row `TIP -> fork` is
            // written, NO row for origin.
            const string treeId = "tree_chainhead";
            const string headId = "rec_head_ch";
            const string tipId = "rec_tip_ch";
            const string forkId = "rec_fork_ch";
            const string chainId = "chain_ch";

            var head = Rec(headId, treeId,
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            head.ExplicitStartUT = 8.42;
            head.ExplicitEndUT = 34.0; // == rewindUT exactly
            head.ChainId = chainId;
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            StampActualBounds(head, 8.42, 34.0);

            var tip = Rec(tipId, treeId,
                state: MergeState.Immutable, terminal: TerminalState.Destroyed);
            tip.ExplicitStartUT = 34.0;
            tip.ExplicitEndUT = 52.0;
            tip.ChainId = chainId;
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            StampActualBounds(tip, 34.0, 52.0);

            InstallTree(treeId,
                new List<Recording> { head, tip },
                new List<BranchPoint>());

            var fork = AddProvisional(forkId, treeId,
                TerminalState.Landed, supersedeTargetId: tipId);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_ch",
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = headId,
                SupersedeTargetId = tipId,
                RewindPointId = "rp_ch",
                InvokedUT = 34.0,
                RewindPointUT = 34.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            var scenario = InstallScenario(marker);

            var subtree = SupersedeCommit.AppendRelations(marker, fork, scenario);

            // The chain sibling HEAD is in the closure (chain-sibling enqueue)
            // but is carved out before row-write.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == tipId && r.NewRecordingId == forkId);
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == headId);
            // Filtered subtree (consumed by CommitTombstones) excludes HEAD.
            Assert.DoesNotContain(headId, subtree);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip pre-rewind PreRewindChainHead")
                && l.Contains("old=" + headId));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("skippedPreRewindCarveOut=1"));
        }

        [Fact]
        public void IsPreRewindDebris_Wrapper_DelegatesToCarveOut()
        {
            // Backward-compat wrapper for the in-game tests at
            // InGameTests/MergeCrashedReFlyCreatesCPSupersedeTest.cs and
            // InGameTests/MergeLandedReFlyCreatesImmutableSupersedeTest.cs.
            // The wrapper returns true ONLY for the debris case, so a
            // chain-head match must report false through the wrapper even
            // though IsPreRewindCarveOut returns true for it.
            // Pass 4 update: chain-shape carve-out needs a TIP installed in
            // CommittedRecordings so the lookup at
            // FindCommittedRecordingByIdForCarveOut resolves.
            var tip = new Recording
            {
                RecordingId = "rec_tip_wrap",
                ChainId = "chain_wrap",
                ChainBranch = 0,
                ChainIndex = 1,
                ExplicitStartUT = 34.0,
                ExplicitEndUT = 52.0,
            };
            StampActualBounds(tip, 34.0, 52.0);
            RecordingStore.AddCommittedInternal(tip);

            var marker = new ReFlySessionMarker
            {
                RewindPointUT = 34.0,
                InvokedUT = 34.0,
                SupersedeTargetId = "rec_tip_wrap",
                PreSessionBranchPointIds = new List<string>(),
            };

            var debris = new Recording
            {
                RecordingId = "rec_debris_wrap",
                IsDebris = true,
                ParentAnchorRecordingId = "rec_origin_wrap",
                ExplicitStartUT = 22.0,
                ExplicitEndUT = 28.0,
            };
            StampActualBounds(debris, 22.0, 28.0);
            Assert.True(SupersedeCommit.IsPreRewindDebris(debris, marker));

            var head = new Recording
            {
                RecordingId = "rec_head_wrap",
                IsDebris = false,
                ChainId = "chain_wrap",
                ChainBranch = 0,
                ChainIndex = 0,
                ExplicitStartUT = 8.0,
                ExplicitEndUT = 34.0,
            };
            StampActualBounds(head, 8.0, 34.0);
            // IsPreRewindCarveOut would return true (PreRewindChainHead),
            // but the wrapper is debris-only and must return false.
            Assert.True(SupersedeCommit.IsPreRewindCarveOut(head, marker, out var reason));
            Assert.Equal(SupersedeCommit.PreRewindCarveOutReason.PreRewindChainHead, reason);
            Assert.False(SupersedeCommit.IsPreRewindDebris(head, marker));
        }
    }
}
