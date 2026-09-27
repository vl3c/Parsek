using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-09-27 (REFLY-SEPARATION-SLOT-THROUGH-OWN-EVA): Re-Fly is for
    /// vessel separations only, and a separation slot follows ITS OWN VESSEL through that
    /// vessel's EVA branch points and through a Board that re-merges the same vessel with
    /// its own EVA crew. The shapes come from <see cref="ReFlyThroughEvaFixture"/>:
    /// staging RP (booster slot 0, upper stage slot 1), Jeb EVAs from the upper stage.
    ///
    /// <para>Cells marked "red on the old walk" fail against the pre-ruling code, where the
    /// slot's walk stopped at the EVA branch point and the slot was refused
    /// <c>downstreamBp</c>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyThroughEvaTests : IDisposable
    {
        private static readonly List<RecordingSupersedeRelation> NoSupersedes =
            new List<RecordingSupersedeRelation>();

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyThroughEvaTests()
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
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RouteStore.ResetForTesting();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RouteStore.ResetForTesting();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            KspStatePatcher.ResetForTesting();
        }

        // ------------------------------------------------------------------
        // Fixture plumbing
        // ------------------------------------------------------------------

        private static RecordingTree Install(
            ReFlyThroughEvaVariant variant, bool treeBranchingParent = false)
        {
            RecordingTree tree = ReFlyThroughEvaFixture.MaterializeTree(variant, treeBranchingParent);
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static ParsekScenario InstallScenario(
            RewindPoint rp, ReFlySessionMarker marker = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            if (rp != null) scenario.RewindPoints.Add(rp);
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ChildSlot UpperSlot(RewindPoint rp)
            => rp.ChildSlots[ReFlyThroughEvaFixture.UpperSlotIndex];

        private static ChildSlot BoosterSlot(RewindPoint rp)
            => rp.ChildSlots[ReFlyThroughEvaFixture.BoosterSlotIndex];

        private static ReFlySessionMarker Marker(
            string originId, string supersedeTargetId, string provisionalId = "te-fork")
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_thru_eva",
                TreeId = "tree_thru_eva",
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = supersedeTargetId,
                RewindPointId = ReFlyThroughEvaFixture.RewindPointId,
                InvokedUT = ReFlyThroughEvaFixture.SplitUT,
                RewindPointUT = ReFlyThroughEvaFixture.SplitUT,
                PreSessionBranchPointIds = new List<string>(),
            };
        }

        private bool QualifyUpper(RecordingTree tree, RewindPoint rp, out string reason)
        {
            Recording origin = tree.Recordings[UpperSlot(rp).OriginChildRecordingId];
            return UnfinishedFlightClassifier.TryQualify(origin, UpperSlot(rp), rp, out reason);
        }

        // ------------------------------------------------------------------
        // The walk (decision 1)
        // ------------------------------------------------------------------

        [Fact]
        public void Walk_Reboard_FollowsTheVesselThroughItsOwnEvaAndBoard()
        {
            // Red on the old walk: it stopped at te-upper (the EVA branch point).
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId], tree,
                followOwnEvaBoard: true, collectDetail: true);

            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterBoardId, walk.Tip.RecordingId);
            Assert.Equal(1, walk.EvaHops);
            Assert.Equal(1, walk.BoardHops);
            Assert.Null(walk.StopReason);
            Assert.False(walk.HasForeignJoin);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, walk.OwnKerbalRecordingIds);
            Assert.Equal(
                new[] { ReFlyThroughEvaFixture.UpperId, ReFlyThroughEvaFixture.UpperAfterEvaId,
                        ReFlyThroughEvaFixture.UpperAfterBoardId },
                walk.VesselRecordingIds.ToArray());

            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("SwitchContinuationWalk: hop from=" + ReFlyThroughEvaFixture.UpperId)
                && l.Contains("to=" + ReFlyThroughEvaFixture.UpperAfterEvaId)
                && l.Contains("kind=eva"));
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("to=" + ReFlyThroughEvaFixture.UpperAfterBoardId)
                && l.Contains("kind=board"));
        }

        [Fact]
        public void Walk_FromTheVesselContinuation_ReachesTheSameTip()
        {
            // A walk that starts MID-stretch never crossed the EVA, so the Board's kerbal
            // parent must still be recognised as the vessel's own crew. Measured live on
            // RF-18's first flight (2026-09-27_1334, flown under the id RF-16): before the pre-registration the walk
            // from the continuation stopped at the Board with reason=boardForeignParent.
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                tree.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId], tree, true, true);
            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterBoardId, walk.Tip.RecordingId);
            Assert.Null(walk.StopReason);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=boardForeignParent"));

            // Still foreign when the kerbal did come from another vessel.
            var foreign = Install(ReFlyThroughEvaVariant.ForeignKerbalBoardsUpper);
            SlotVesselWalk foreignWalk = EffectiveState.WalkSlotVessel(
                foreign.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId], foreign, true, true);
            Assert.Equal("boardForeignParent", foreignWalk.StopReason);
        }

        [Fact]
        public void Walk_EvaBoardOptOut_StopsAtTheEva_ForTheMapPresenceBorrow()
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            Assert.Equal(ReFlyThroughEvaFixture.UpperId,
                EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                    tree.Recordings[ReFlyThroughEvaFixture.UpperId], tree,
                    followOwnEvaBoard: false).RecordingId);
            Assert.Equal(ReFlyThroughEvaFixture.UpperId,
                EffectiveState.EffectiveTipRecordingId(ReFlyThroughEvaFixture.UpperId,
                    NoSupersedes, null, null, followOwnEvaBoard: false));
        }

        [Fact]
        public void Walk_KerbalWalkNeverFollowsTheVesselEdges()
        {
            // The kerbal's own recording ends at the Board, whose merged child carries the
            // VESSEL's pid, so the kerbal walk must not hop it (it would make the kerbal an
            // Unfinished Flight subject through the vessel's terminal).
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                tree.Recordings[ReFlyThroughEvaFixture.KerbalId], tree, true, true);
            Assert.Equal(ReFlyThroughEvaFixture.KerbalId, walk.Tip.RecordingId);
            Assert.Equal("boardNotSameVessel", walk.StopReason);
        }

        [Fact]
        public void Walk_ForeignKerbalBoardsTheVessel_StopsAtThatBoard()
        {
            // Bill came over from the booster and boarded the upper stage: a merge with a
            // foreign vessel's crew. The walk stops (boardForeignParent) exactly as a Dock.
            var tree = Install(ReFlyThroughEvaVariant.ForeignKerbalBoardsUpper);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId], tree, true, true);
            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterEvaId, walk.Tip.RecordingId);
            Assert.Equal("boardForeignParent", walk.StopReason);
            Assert.Equal(ReFlyThroughEvaFixture.ForeignBoardBranchPointId, walk.StopBranchPointId);
        }

        [Fact]
        public void Pure_ClassifySameVesselBoard_Cases()
        {
            var walk = new SlotVesselWalk();
            walk.AddOwnKerbalRecordingId("k_own");
            var vessel = new Recording { RecordingId = "v1", VesselPersistentId = 7u };
            var merged = new Recording { RecordingId = "v2", VesselPersistentId = 7u };
            var otherVessel = new Recording { RecordingId = "w2", VesselPersistentId = 9u };
            var kerbalMerged = new Recording { RecordingId = "k2", VesselPersistentId = 7u, EvaCrewName = "Jeb" };

            BranchPoint Board(params string[] parents) => new BranchPoint
            {
                Id = "bp", Type = BranchPointType.Board,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string> { "child" },
            };

            Assert.Null(EffectiveState.ClassifySameVesselBoard(Board("k_own", "v1"), vessel, merged, walk));
            Assert.Equal("boardForeignParent",
                EffectiveState.ClassifySameVesselBoard(Board("k_foreign", "v1"), vessel, merged, walk));
            Assert.Equal("boardNotSameVessel",
                EffectiveState.ClassifySameVesselBoard(Board("k_own", "v1"), vessel, otherVessel, walk));
            Assert.Equal("boardNotSameVessel",
                EffectiveState.ClassifySameVesselBoard(Board("k_own"), vessel, merged, walk));
            Assert.Equal("boardNotSameVessel",
                EffectiveState.ClassifySameVesselBoard(Board("k_own", "v1"), vessel, kerbalMerged, walk));
            Assert.Equal("boardNotSameVessel",
                EffectiveState.ClassifySameVesselBoard(
                    Board("k_own", "v1"), new Recording { RecordingId = "v1" }, merged, walk));
        }

        [Fact]
        public void Pure_FindOwnEvaVesselContinuation_PicksTheSamePidNonKerbalChild()
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            BranchPoint eva = EffectiveState.FindBranchPointByIdInTree(
                tree, ReFlyThroughEvaFixture.EvaBranchPointId);

            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterEvaId,
                EffectiveState.FindOwnEvaVesselContinuation(
                    tree, eva, tree.Recordings[ReFlyThroughEvaFixture.UpperId]).RecordingId);
            // A kerbal is never the vessel side of an EVA split.
            Assert.Null(EffectiveState.FindOwnEvaVesselContinuation(
                tree, eva, tree.Recordings[ReFlyThroughEvaFixture.KerbalId]));
            // A vessel that is not the split's parent does not own it.
            Assert.Null(EffectiveState.FindOwnEvaVesselContinuation(
                tree, eva, tree.Recordings[ReFlyThroughEvaFixture.BoosterId]));
        }

        // ------------------------------------------------------------------
        // Classification (decisions 1, 2, 4)
        // ------------------------------------------------------------------

        [Fact]
        public void Classifier_Reboard_ThenCrash_IsAnUnfinishedFlight()
        {
            // The brief's example. Red on the old walk (reason=downstreamBp).
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.True(QualifyUpper(tree, rp, out string reason), reason);
            Assert.Equal("crashed", reason);
            Assert.Contains(logLines, l => l.Contains("[UnfinishedFlights]")
                && l.Contains("IsUnfinishedFlight=true rec=" + ReFlyThroughEvaFixture.UpperId)
                && l.Contains("reason=crashed")
                && l.Contains("walkedEva=1 walkedBoard=1"));
        }

        [Fact]
        public void Classifier_KerbalBoardsForeignVessel_IsRefusedWithTheNewReason()
        {
            var tree = Install(ReFlyThroughEvaVariant.KerbalBoardsForeignVessel);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.False(QualifyUpper(tree, rp, out string reason));
            Assert.Equal(UnfinishedFlightClassifier.EvaCrewJoinedForeignVesselReason, reason);
            // The vessel continuation after the EVA is a slot member (the walked tip) and
            // reaches the same verdict: its walk starts at the stretch head.
            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterEvaId,
                UpperSlot(rp).EffectiveRecordingId(NoSupersedes));
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                tree.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId], UpperSlot(rp), rp,
                out string tipReason));
            Assert.Equal(UnfinishedFlightClassifier.EvaCrewJoinedForeignVesselReason, tipReason);
            Assert.Contains(logLines, l => l.Contains("[UnfinishedFlights]")
                && l.Contains("reason=evaCrewJoinedForeignVessel")
                && l.Contains("kerbalRec=" + ReFlyThroughEvaFixture.KerbalId)
                && l.Contains("joinBp=" + ReFlyThroughEvaFixture.ForeignBoardBranchPointId)
                && l.Contains("walkedEva=1"));

            // Mirror: the booster the kerbal boarded took a foreign merge too, so its own
            // slot stops at that Board (downstreamBp), exactly as before the ruling.
            Recording booster = tree.Recordings[ReFlyThroughEvaFixture.BoosterId];
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                booster, BoosterSlot(rp), rp, out string boosterReason));
            Assert.Equal("downstreamBp", boosterReason);
        }

        [Fact]
        public void Classifier_TwoEvas_OneKerbalLeavesForForeign_BlocksTheSlot()
        {
            var tree = Install(ReFlyThroughEvaVariant.TwoEvasOneLeavesForForeign);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.False(QualifyUpper(tree, rp, out string reason));
            Assert.Equal(UnfinishedFlightClassifier.EvaCrewJoinedForeignVesselReason, reason);
            Assert.Contains(logLines, l => l.Contains("reason=evaCrewJoinedForeignVessel")
                && l.Contains("kerbalRec=" + ReFlyThroughEvaFixture.SecondKerbalId)
                && l.Contains("walkedEva=2 walkedBoard=1"));
        }

        [Theory]
        [InlineData(ReFlyThroughEvaVariant.KerbalDiesOnEva)]
        [InlineData(ReFlyThroughEvaVariant.KerbalStaysOnEva)]
        public void Classifier_KerbalLeftOnEvaOrDead_DoesNotBlock(ReFlyThroughEvaVariant variant)
        {
            // Red on the old walk (downstreamBp at the EVA branch point).
            var tree = Install(variant);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.True(QualifyUpper(tree, rp, out string reason), reason);
            Assert.Equal("crashed", reason);
            Assert.Contains(logLines, l => l.Contains("reason=crashed")
                && l.Contains("walkedEva=1 walkedBoard=0"));
        }

        [Fact]
        public void Classifier_ForeignKerbalBoardsTheVessel_StaysDownstreamBp()
        {
            var tree = Install(ReFlyThroughEvaVariant.ForeignKerbalBoardsUpper);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.False(QualifyUpper(tree, rp, out string reason));
            Assert.Equal("downstreamBp", reason);
            Assert.Contains(logLines, l => l.Contains("reason=downstreamBp")
                && l.Contains("walkStop=boardForeignParent"));
        }

        [Fact]
        public void Classifier_EvaReportScienceOnTheKerbal_BlocksRetry()
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_eva_report",
                Type = GameActionType.ScienceEarning,
                RecordingId = ReFlyThroughEvaFixture.KerbalId,
                UT = 210.0,
                SubjectId = "evaReport@KerbinInSpaceLow",
                ScienceAwarded = 8f,
            });

            // Red on the old gate: the kerbal's recording was outside the safety set.
            Assert.False(QualifyUpper(tree, rp, out string reason));
            Assert.Equal("recordingAction:ScienceEarning:act_eva_report", reason);

            // The walked tip is a slot member too and must reach the same verdict: its
            // safety scan reads the stretch HEAD, so the EVA it never crossed still counts.
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId], UpperSlot(rp), rp,
                out string tipReason));
            Assert.Equal("recordingAction:ScienceEarning:act_eva_report", tipReason);
        }

        [Fact]
        public void Classifier_AutomaticConsequenceOnTheKerbal_StaysRetryable()
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_eva_milestone",
                Type = GameActionType.MilestoneAchievement,
                RecordingId = ReFlyThroughEvaFixture.KerbalId,
                UT = 210.0,
                MilestoneId = "Kerbin/FlagPlant",
            });

            Assert.True(QualifyUpper(tree, rp, out string reason), reason);
            Assert.Equal("crashed", reason);
        }

        [Fact]
        public void SafetyGateIds_CoverTheWalkedVesselTheCrewAndItsPlacedParts()
        {
            var tree = Install(ReFlyThroughEvaVariant.ReboardWithPlacedPart);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());

            HashSet<string> ids = SupersedeCommit.CollectRecordingIdsForSafetyGate(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId]);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, ids);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterBoardId, ids);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, ids);
            Assert.Contains(ReFlyThroughEvaFixture.PlacedPartId, ids);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, ids);
        }

        [Fact]
        public void HardSafetyTerminal_ReadsTheVesselsRealTip()
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint());
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId].TerminalStateValue =
                TerminalState.Recovered;

            Assert.True(SupersedeCommit.IsHardSafetyTerminal(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId]));
        }

        [Fact]
        public void ProbeSlotWithNoEva_IsUntouched()
        {
            // The booster never puts anyone out: its walk has no EVA hop and it reads its
            // own terminal (Landed, a stable non-focus leaf is not qualifying by default).
            var tree = Install(ReFlyThroughEvaVariant.Reboard);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                tree.Recordings[ReFlyThroughEvaFixture.BoosterId], tree, true, true);
            Assert.Equal(ReFlyThroughEvaFixture.BoosterId, walk.Tip.RecordingId);
            Assert.Equal(0, walk.EvaHops);
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                tree.Recordings[ReFlyThroughEvaFixture.BoosterId], BoosterSlot(rp), rp,
                out string reason));
            Assert.Equal("stableTerminal", reason);
            Assert.DoesNotContain(logLines, l => l.Contains("rec=" + ReFlyThroughEvaFixture.BoosterId)
                && l.Contains("walkedEva="));
        }

        // ------------------------------------------------------------------
        // Mirror: every slot-tip consumer agrees with the walk
        // ------------------------------------------------------------------

        [Fact]
        public void TipConsumers_AgreeOnTheWalkedTip_AndTheReaperKeepsTheOpenSlot()
        {
            // Red on the old walk: the tip stayed at te-upper, CommitTree promoted nothing
            // past the EVA, and the reaper reaped the RP.
            RecordingTree tree = ReFlyThroughEvaFixture.MaterializeTree(ReFlyThroughEvaVariant.Reboard);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            RecordingStore.CommitTree(tree);

            string tipId = UpperSlot(rp).EffectiveRecordingId(NoSupersedes);
            Assert.Equal(ReFlyThroughEvaFixture.UpperAfterBoardId, tipId);
            Assert.Equal(tipId, EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId], null).RecordingId);
            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId].MergeState);
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(UpperSlot(rp)));
            Assert.False(RewindPointReaper.IsReapEligible(rp, NoSupersedes));
            Assert.True(EffectiveState.IsUnfinishedFlight(
                tree.Recordings[ReFlyThroughEvaFixture.UpperId]));
            // The walked tip resolves to the same slot (slot membership agrees).
            Assert.Equal(ReFlyThroughEvaFixture.UpperSlotIndex,
                EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    rp, tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId], NoSupersedes));

            // Seal flips the WALKED tip; the reaper then closes the slot.
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId].MergeState = MergeState.Immutable;
            tree.Recordings[ReFlyThroughEvaFixture.BoosterId].MergeState = MergeState.Immutable;
            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(UpperSlot(rp)));
            Assert.True(RewindPointReaper.IsReapEligible(rp, NoSupersedes));
        }

        [Fact]
        public void CommitTree_ForeignJoin_PromotesNothing_SoTheRpReaps()
        {
            RecordingTree tree = ReFlyThroughEvaFixture.MaterializeTree(
                ReFlyThroughEvaVariant.KerbalBoardsForeignVessel);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            RecordingStore.CommitTree(tree);

            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(UpperSlot(rp)));
            Assert.True(RewindPointReaper.IsReapEligible(rp, NoSupersedes));
            Assert.Contains(logLines, l => l.Contains("not promoted reason=evaCrewJoinedForeignVessel"));
        }

        // ------------------------------------------------------------------
        // Closure + supersede write-set (decision 3)
        // ------------------------------------------------------------------

        [Fact]
        public void SessionSuppressedSubtree_IncludesTheEvaKerbalAndTheReboardedVessel()
        {
            // Red on the old closure: the kerbal was skipped as a side-off and the Board
            // halted the walk (mixed parent), leaving te-jeb-eva and te-upper-u2 visible.
            Install(ReFlyThroughEvaVariant.ReboardWithPlacedPart);
            var marker = Marker(ReFlyThroughEvaFixture.UpperId, ReFlyThroughEvaFixture.UpperAfterBoardId);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(), marker);

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            Assert.Contains(ReFlyThroughEvaFixture.UpperId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterBoardId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.PlacedPartId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, closure);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("admitted own-EVA kerbal child=" + ReFlyThroughEvaFixture.KerbalId));
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("evaKerbalsAdded=1")
                && l.Contains("placedPartsAdded=1"));
        }

        [Fact]
        public void SupersedeClosure_RootedAtTheWalkedTip_SeedsTheStretchBackward()
        {
            // Merge time: the marker's SupersedeTargetId is the walked tip, so the closure
            // must reach back to the EVA to include the kerbal.
            Install(ReFlyThroughEvaVariant.Reboard);
            var marker = Marker(ReFlyThroughEvaFixture.UpperId, ReFlyThroughEvaFixture.UpperAfterBoardId);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(), marker);

            var closure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, ReFlyThroughEvaFixture.UpperAfterBoardId);
            Assert.Contains(ReFlyThroughEvaFixture.UpperId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterBoardId, closure);
            Assert.Contains(logLines, l => l.Contains("seeded own-vessel stretch root="
                + ReFlyThroughEvaFixture.UpperAfterBoardId));
        }

        [Fact]
        public void SupersedeClosure_ForeignBoard_KeepsTheMixedParentHalt()
        {
            Install(ReFlyThroughEvaVariant.KerbalBoardsForeignVessel);
            var marker = Marker(ReFlyThroughEvaFixture.UpperId, ReFlyThroughEvaFixture.UpperAfterEvaId);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(), marker);

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterAfterBoardId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, closure);
        }

        [Fact]
        public void Pure_IsOwnEvaKerbalClosureChild_Gates()
        {
            var vessel = new Recording { RecordingId = "v", VesselPersistentId = 5u };
            var kerbal = new Recording { RecordingId = "k", EvaCrewName = "Jeb", VesselPersistentId = 6u };
            var eva = new BranchPoint
            {
                Id = "bp", Type = BranchPointType.EVA, UT = 200.0,
                ParentRecordingIds = new List<string> { "v" },
                ChildRecordingIds = new List<string> { "v1", "k" },
            };
            var marker = Marker("v", "v2");
            string reason;

            Assert.True(EffectiveState.IsOwnEvaKerbalClosureChild(eva, vessel, kerbal, marker, out reason));
            Assert.Equal("admit", reason);

            var undock = new BranchPoint { Id = "u", Type = BranchPointType.Undock, UT = 200.0,
                ParentRecordingIds = new List<string> { "v" } };
            Assert.False(EffectiveState.IsOwnEvaKerbalClosureChild(undock, vessel, kerbal, marker, out reason));
            Assert.Equal("notEvaBranchPoint", reason);

            Assert.False(EffectiveState.IsOwnEvaKerbalClosureChild(
                eva, vessel, new Recording { RecordingId = "v1", VesselPersistentId = 5u }, marker, out reason));
            Assert.Equal("notKerbal", reason);

            Assert.False(EffectiveState.IsOwnEvaKerbalClosureChild(
                eva, new Recording { RecordingId = "other", VesselPersistentId = 8u }, kerbal, marker, out reason));
            Assert.Equal("notParent", reason);

            var uncommitted = new Recording { RecordingId = "k", EvaCrewName = "Jeb",
                MergeState = MergeState.NotCommitted };
            Assert.False(EffectiveState.IsOwnEvaKerbalClosureChild(eva, vessel, uncommitted, marker, out reason));
            Assert.Equal("notCommitted", reason);

            var early = new BranchPoint { Id = "e", Type = BranchPointType.EVA, UT = 50.0,
                ParentRecordingIds = new List<string> { "v" } };
            Assert.False(EffectiveState.IsOwnEvaKerbalClosureChild(early, vessel, kerbal, marker, out reason));
            Assert.Equal("beforeRewind", reason);
        }

        [Fact]
        public void CommitSupersede_SupersedesTheCrewEva_AndTombstonesTheEvaDeath()
        {
            // Decision 3: a kerbal who died on the EVA is brought back by the re-fly - the
            // death row on his EVA recording is retired through the ordinary tombstone path.
            Install(ReFlyThroughEvaVariant.KerbalDiesOnEva);
            var marker = Marker(ReFlyThroughEvaFixture.UpperId, ReFlyThroughEvaFixture.UpperAfterEvaId);
            var scenario = InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(), marker);
            var provisional = new Recording
            {
                RecordingId = "te-fork",
                VesselName = "TE Upper",
                TreeId = "tree_thru_eva",
                MergeState = MergeState.NotCommitted,
                TerminalStateValue = TerminalState.Landed,
                VesselPersistentId = ReFlyThroughEvaFixture.UpperPid,
                SupersedeTargetId = ReFlyThroughEvaFixture.UpperAfterEvaId,
            };
            // Production shape (RewindInvoker.BuildProvisionalRecording): the fork carries
            // the origin's parent branch point, which is how Site B-1 finds its slot.
            provisional.ParentBranchPointId = ReFlyThroughEvaFixture.SplitBranchPointId;
            provisional.Points.Add(new TrajectoryPoint { ut = 100.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional);

            Ledger.AddAction(new GameAction
            {
                ActionId = "act_jeb_eva_death",
                Type = GameActionType.KerbalAssignment,
                RecordingId = ReFlyThroughEvaFixture.KerbalId,
                KerbalName = ReFlyThroughEvaFixture.EvaCrewName,
                UT = 200.0,
                StartUT = 200f,
                EndUT = 260f,
                KerbalEndStateField = KerbalEndState.Dead,
            });

            SupersedeCommit.CommitSupersede(marker, provisional);

            var oldIds = new HashSet<string>(
                scenario.RecordingSupersedes.Select(r => r.OldRecordingId), StringComparer.Ordinal);
            Assert.Contains(ReFlyThroughEvaFixture.UpperId, oldIds);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, oldIds);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, oldIds);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, oldIds);
            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == "act_jeb_eva_death");
        }

        // ------------------------------------------------------------------
        // Splitter redirect: the tree-branching parent that spans the rewind point
        // ------------------------------------------------------------------

        [Fact]
        public void Splitter_TipDoesNotSpan_SplitsTheSpanningStretchAncestor()
        {
            // Red on the old splitter: it looked only at SupersedeTargetId (the walked tip,
            // which starts at the board), skipped, and the whole launch recording was
            // superseded (or, before the walk, the slot never qualified at all).
            var tree = Install(ReFlyThroughEvaVariant.Reboard, treeBranchingParent: true);
            Recording root = tree.Recordings[ReFlyThroughEvaFixture.RootId];
            MakeSplittable(root, ReFlyThroughEvaFixture.LaunchUT,
                ReFlyThroughEvaFixture.SplitUT, ReFlyThroughEvaFixture.EvaUT);
            var marker = Marker(ReFlyThroughEvaFixture.RootId, ReFlyThroughEvaFixture.UpperAfterBoardId);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(upperIsTreeBranchingParent: true), marker);

            Assert.Same(root, RecordingTreeSplitter.FindSpanningOwnVesselStretchAncestor(
                tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId], marker,
                ReFlyThroughEvaFixture.SplitUT, EffectiveState.PidPeerStartUtEpsilonSeconds));

            var result = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, ParsekScenario.Instance);

            Assert.False(result.Skipped, result.SkipReason);
            Assert.Equal(ReFlyThroughEvaFixture.RootId, result.HeadRecordingId);
            Assert.Equal(result.TipRecordingId, marker.SupersedeTargetId);
            Assert.Contains(logLines, l => l.Contains("[Splitter]")
                && l.Contains("splitting own-vessel stretch ancestor '" + ReFlyThroughEvaFixture.RootId + "'"));

            // The forward closure from the new TIP reaches the whole walked history and the
            // crew EVA; the pre-rewind HEAD is carved out of the write-set.
            var closure = EffectiveState.ComputeSubtreeClosureInternal(marker, marker.SupersedeTargetId);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, closure);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterBoardId, closure);
            Recording head = EffectiveState.FindCommittedRecordingByIdRaw(ReFlyThroughEvaFixture.RootId);
            Assert.True(SupersedeCommit.IsPreRewindCarveOut(head, marker, out _));
        }

        [Fact]
        public void SupersedeStretchSeed_NeverReachesAPreRewindSpanningRecording()
        {
            // If the splitter could not cut the spanning origin, the stretch seeding must not
            // pull it (with its launch history) into the supersede write-set.
            var tree = Install(ReFlyThroughEvaVariant.Reboard, treeBranchingParent: true);
            var marker = Marker(ReFlyThroughEvaFixture.RootId, ReFlyThroughEvaFixture.UpperAfterBoardId);
            InstallScenario(ReFlyThroughEvaFixture.BuildRewindPoint(upperIsTreeBranchingParent: true), marker);

            var closure = EffectiveState.ComputeSubtreeClosureInternal(
                marker, ReFlyThroughEvaFixture.UpperAfterBoardId);
            Assert.Contains(ReFlyThroughEvaFixture.UpperAfterEvaId, closure);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.RootId, closure);
        }

        private static void MakeSplittable(Recording rec, double startUT, double midUT, double endUT)
        {
            TrajectoryPoint P(double ut) => new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
            rec.Points.Clear();
            rec.Points.Add(P(startUT));
            rec.Points.Add(P(midUT));
            rec.Points.Add(P(endUT));
            rec.TrackSections.Clear();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { P(startUT), P(midUT), P(endUT) },
            });
        }

        // ------------------------------------------------------------------
        // Kerbals window Lost hover (decision 5)
        // ------------------------------------------------------------------

        [Fact]
        public void ReFlyReach_OpenSlot_ReachesTheEvaDeath_ASealedSlotDoesNot()
        {
            var tree = Install(ReFlyThroughEvaVariant.KerbalDiesOnEva);
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId].MergeState =
                MergeState.CommittedProvisional;

            HashSet<string> reach = EffectiveState.ComputeOpenSlotReFlyReachRecordingIds();
            Assert.Contains(ReFlyThroughEvaFixture.KerbalId, reach);
            Assert.Contains(ReFlyThroughEvaFixture.UpperId, reach);
            Assert.DoesNotContain(ReFlyThroughEvaFixture.BoosterId, reach);
            Assert.True(KerbalsPresentation.ShouldOfferLostReFlyRemedy(
                ReFlyThroughEvaFixture.KerbalId, EffectiveState.BuildLossReFlyReachablePredicate()));

            // Sealed: no open Re-Fly, so the way back is not offered.
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId].MergeState = MergeState.Immutable;
            Assert.Empty(EffectiveState.ComputeOpenSlotReFlyReachRecordingIds());
            Assert.False(KerbalsPresentation.ShouldOfferLostReFlyRemedy(
                ReFlyThroughEvaFixture.KerbalId, EffectiveState.BuildLossReFlyReachablePredicate()));
        }

        // ------------------------------------------------------------------
        // Stash (the StashSlot seam verb's production path): RF-18/RF-20's shape - the
        // pod stack is the tree-branching parent, the FOCUS slot, and ends Orbiting after
        // its own EVA and re-board, so it is a stable leaf the default predicate excludes
        // until the player stashes it.
        // ------------------------------------------------------------------

        private RecordingTree InstallOrbitingFocusReboard(out RewindPoint rp)
        {
            var tree = Install(ReFlyThroughEvaVariant.Reboard, treeBranchingParent: true);
            tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId].TerminalStateValue =
                TerminalState.Orbiting;
            rp = ReFlyThroughEvaFixture.BuildRewindPoint(upperIsTreeBranchingParent: true);
            InstallScenario(rp);
            return tree;
        }

        [Fact]
        public void Stash_FocusSlotThroughOwnEvaAndBoard_OpensTheWalkedTip()
        {
            RecordingTree tree = InstallOrbitingFocusReboard(out RewindPoint rp);
            Recording root = tree.Recordings[ReFlyThroughEvaFixture.RootId];
            Recording tip = tree.Recordings[ReFlyThroughEvaFixture.UpperAfterBoardId];
            UnfinishedFlightStashHandler.UtcNowForTesting =
                () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                // Before: the focus verdict read through the walk, not an Unfinished Flight.
                Assert.Equal(ReFlyThroughEvaFixture.UpperAfterBoardId,
                    UpperSlot(rp).EffectiveRecordingId(NoSupersedes));
                Assert.False(UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(tip, out _));
                Assert.Contains(logLines, l => l.Contains("[UnfinishedFlights]")
                    && l.Contains("reason=stableTerminalFocusSlot")
                    && l.Contains("walkedEva=1 walkedBoard=1"));

                // The slot's origin row and its walked-tip row both offer Stash on the SAME
                // slot (the rows ResolveRewindPointSlotIndexForRecording matches, as for a
                // plain chain's head and tip).
                foreach (string id in new[] { ReFlyThroughEvaFixture.RootId,
                                              ReFlyThroughEvaFixture.UpperAfterBoardId })
                {
                    Assert.True(UnfinishedFlightClassifier.TryResolveStashableRewindPointForRecording(
                        tree.Recordings[id], out RewindPoint resolvedRp, out int slotIdx,
                        out string why), id + ": " + why);
                    Assert.Same(rp, resolvedRp);
                    Assert.Equal(ReFlyThroughEvaFixture.UpperSlotIndex, slotIdx);
                }
                // The kerbal's row never does (an EVA kerbal is never re-flyable).
                Assert.False(UnfinishedFlightClassifier.TryResolveStashableRewindPointForRecording(
                    tree.Recordings[ReFlyThroughEvaFixture.KerbalId], out _, out _, out _));

                // What the seam verb hands the handler: the slot's effective tip.
                Assert.True(UnfinishedFlightStashHandler.TryStash(tip, out string reason), reason);

                Assert.True(UpperSlot(rp).Stashed);
                Assert.Equal("2000-01-01T00:00:00.0000000Z", UpperSlot(rp).StashedRealTime);
                Assert.False(BoosterSlot(rp).Stashed);
                Assert.Equal(MergeState.CommittedProvisional, tip.MergeState);
                Assert.Equal(MergeState.Immutable, root.MergeState);
                Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(UpperSlot(rp)));
                Assert.False(RewindPointReaper.IsReapEligible(rp, NoSupersedes));

                // After: the slot's ORIGIN row reads as an Unfinished Flight (the Fly
                // button's predicate), qualified through the stashed branch on the walked
                // tip. The tip row is a peer of the same slot and is deduped onto the
                // origin anchor (EffectiveState.TryResolveUnfinishedFlight), so it does
                // not draw a second Fly row.
                Assert.True(UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(root, out string ufWhy), ufWhy);
                Assert.False(UnfinishedFlightClassifier.IsVisibleUnfinishedFlight(tip, out _));
                Assert.True(UnfinishedFlightClassifier.TryQualify(
                    root, UpperSlot(rp), rp, out string rootReason), rootReason);
                Assert.Equal("stashedStableLeaf", rootReason);
                Assert.Contains(logLines, l => l.Contains("[UnfinishedFlights]")
                    && l.Contains("Stashed slot=" + ReFlyThroughEvaFixture.UpperSlotIndex)
                    && l.Contains("rec=" + ReFlyThroughEvaFixture.UpperAfterBoardId)
                    && l.Contains("tip=" + ReFlyThroughEvaFixture.UpperAfterBoardId)
                    && l.Contains("tipMergeState=CommittedProvisional tipDemoted=True")
                    && l.Contains("terminal=Orbiting reaperBlocked=True"));
                Assert.Contains(logLines, l => l.Contains("IsUnfinishedFlight=true")
                    && l.Contains("reason=stashedStableLeaf")
                    && l.Contains("walkedEva=1 walkedBoard=1"));

                // A second press is refused by the same resolver that hides the button.
                Assert.False(UnfinishedFlightStashHandler.TryStash(tip, out string again));
                Assert.Equal("alreadyStashed", again);
            }
            finally
            {
                UnfinishedFlightStashHandler.ResetForTesting();
            }
        }

        [Fact]
        public void Stash_KerbalLeftForForeignVessel_IsRefusedAndWritesNothing()
        {
            // Decision 2 caps the stash too: the slot is not re-flyable even on request.
            var tree = Install(ReFlyThroughEvaVariant.KerbalBoardsForeignVessel);
            Recording tip = tree.Recordings[ReFlyThroughEvaFixture.UpperAfterEvaId];
            tip.TerminalStateValue = TerminalState.Orbiting;
            var rp = ReFlyThroughEvaFixture.BuildRewindPoint();
            InstallScenario(rp);

            Assert.False(UnfinishedFlightStashHandler.TryStash(tip, out string reason));
            Assert.Equal(UnfinishedFlightClassifier.EvaCrewJoinedForeignVesselReason, reason);
            Assert.False(UpperSlot(rp).Stashed);
            Assert.Equal(MergeState.Immutable, tip.MergeState);
            Assert.Contains(logLines, l => l.Contains("[UnfinishedFlights]")
                && l.Contains("Stash unavailable rec=" + ReFlyThroughEvaFixture.UpperAfterEvaId)
                && l.Contains("reason=evaCrewJoinedForeignVessel"));
        }

        [Fact]
        public void Pure_ShouldOfferLostReFlyRemedy()
        {
            Func<string, bool> reach = id => id == "r_dead";
            Assert.True(KerbalsPresentation.ShouldOfferLostReFlyRemedy("r_dead", reach));
            Assert.False(KerbalsPresentation.ShouldOfferLostReFlyRemedy("r_other", reach));
            Assert.False(KerbalsPresentation.ShouldOfferLostReFlyRemedy(null, reach));
            Assert.False(KerbalsPresentation.ShouldOfferLostReFlyRemedy("r_dead", null));
        }
    }
}
