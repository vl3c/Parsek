using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// COMMITTED-RESTORE-APPENDS-TO-ROOT: a committed tree resumed on its live vessel must
    /// record onto the vessel's chain tip, never onto an earlier piece of the chain. Two
    /// halves: the optimizer split moves the tree's active recording to the split tail
    /// (as the merge already moves it to the survivor), and the restore resumes on the tip
    /// even for a save whose active id still names the head.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedRestoreChainTipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public CommittedRestoreChainTipTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private const string TreeId = "tree_chain_tip";
        private const uint VesselPid = 4242u;

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        private static TrackSection Section(SegmentEnvironment env, double[] uts)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = uts[0], endUT = uts[uts.Length - 1], sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = uts.Select(PointAt).ToList(),
            };
        }

        // Atmospheric 8-20 then ExoBallistic 20-53: the optimizer splits it once at UT 20.
        private static Recording BuildSplittableFlight(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Probe",
                TreeId = TreeId,
                VesselPersistentId = VesselPid,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Orbiting,
                RecordingFormatVersion = 0,
            };
            double[] atmoUTs = { 8.0, 14.0, 20.0 };
            double[] exoUTs = { 20.0, 27.0, 34.0, 40.0, 53.0 };
            foreach (double ut in atmoUTs) rec.Points.Add(PointAt(ut));
            for (int i = 1; i < exoUTs.Length; i++) rec.Points.Add(PointAt(exoUTs[i]));
            rec.TrackSections.Add(Section(SegmentEnvironment.Atmospheric, atmoUTs));
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, exoUTs));
            return rec;
        }

        private static RecordingTree InstallCommittedTree(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        // A committed tree after a split that left the active id on the head (the shape every
        // save written before the fix carries): head (chain 0) + tip (chain 1), same vessel.
        private static RecordingTree StaleHeadTree()
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = "rec_head",
                ActiveRecordingId = "rec_head",
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "rec_head", TreeId = TreeId, VesselName = "Probe",
                VesselPersistentId = VesselPid, ChainId = "chain", ChainIndex = 0,
                ExplicitStartUT = 100.0, ExplicitEndUT = 200.0,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "rec_tip", TreeId = TreeId, VesselName = "Probe",
                VesselPersistentId = VesselPid, ChainId = "chain", ChainIndex = 1,
                ExplicitStartUT = 200.0, ExplicitEndUT = 900.0,
                TerminalStateValue = TerminalState.Orbiting,
            });
            return tree;
        }

        // MUTATION NOTE: deleting the MoveActiveRecordingToSplitTail call in the split pass
        // reds this (the tree's active id stays on the head).
        [Fact]
        public void OptimizerSplit_MovesTheTreesActiveRecordingToTheTail()
        {
            var rec = BuildSplittableFlight("rec_split");
            var tree = InstallCommittedTree(rec);

            CommittedListOptimizer.RunOptimizationPass();

            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string head = list[0].RecordingId;
            string tail = list[1].RecordingId;
            Assert.Equal("rec_split", head);
            Assert.Equal(tail, tree.ActiveRecordingId);
            Assert.Equal("rec_split", tree.RootRecordingId);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("active recording moved from the head 'rec_split'")
                && l.Contains($"to the split tail '{tail}'"));

            // The resume now finds the tail, the piece where the vessel's flight goes on.
            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, VesselPid, out RecordingTree matched, out string recId);
            Assert.True(found);
            Assert.Same(tree, matched);
            Assert.Equal(tail, recId);
        }

        [Fact]
        public void MoveActiveRecordingToSplitTail_LeavesAnotherActiveRecordingAlone()
        {
            var tree = new RecordingTree { Id = "t", ActiveRecordingId = "rec_other" };
            var original = new Recording { RecordingId = "rec_a" };
            var second = new Recording { RecordingId = "rec_b" };

            Assert.False(CommittedListOptimizer.MoveActiveRecordingToSplitTail(tree, original, second));
            Assert.Equal("rec_other", tree.ActiveRecordingId);

            tree.ActiveRecordingId = null;
            Assert.False(CommittedListOptimizer.MoveActiveRecordingToSplitTail(tree, original, second));
            Assert.Null(tree.ActiveRecordingId);

            Assert.False(CommittedListOptimizer.MoveActiveRecordingToSplitTail(null, original, second));

            tree.ActiveRecordingId = "rec_a";
            Assert.True(CommittedListOptimizer.MoveActiveRecordingToSplitTail(tree, original, second));
            Assert.Equal("rec_b", tree.ActiveRecordingId);
        }

        // MUTATION NOTE: putting the active-id shortcut back ahead of the next-chain-segment
        // check in IsCommittedSpawnedRecordingRestorable reds this (the head wins on the
        // active preference, the B36 / B37 / B38 forge shape).
        [Fact]
        public void StaleHeadActiveId_RestoreFindsTheChainTip()
        {
            var tree = StaleHeadTree();

            Assert.False(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, tree.Recordings["rec_head"]));
            Assert.True(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, tree.Recordings["rec_tip"]));

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree }, VesselPid, out RecordingTree matched, out string recId);
            Assert.True(found);
            Assert.Same(tree, matched);
            Assert.Equal("rec_tip", recId);
        }

        // MUTATION NOTE: deleting the earlier-chain-piece branch in
        // PrepareCommittedTreeRestoreForSpawnedVessel reds this (it falls to the background
        // promote, which parks the head in the background map under the live vessel's pid).
        [Fact]
        public void StaleHeadActiveId_PrepareResumesOnTheTipAsTheActiveRecording()
        {
            var tree = StaleHeadTree();
            tree.BackgroundMap[VesselPid] = "rec_tip";

            var action = ParsekFlight.PrepareCommittedTreeRestoreForSpawnedVessel(
                tree, "rec_tip", VesselPid);

            Assert.Equal(ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording, action);
            Assert.Equal("rec_tip", tree.ActiveRecordingId);
            Assert.DoesNotContain("rec_tip", tree.BackgroundMap.Values);
            Assert.DoesNotContain("rec_head", tree.BackgroundMap.Values);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("active recording 'rec_head' (chain index 0)")
                && l.Contains("resuming on the chain tip 'rec_tip' (chain index 1)"));
        }

        [Fact]
        public void IsEarlierSegmentOfSameChain_TruthTable()
        {
            var head = new Recording { RecordingId = "h", ChainId = "c", ChainIndex = 0 };
            var tip = new Recording { RecordingId = "t", ChainId = "c", ChainIndex = 1 };
            var otherChain = new Recording { RecordingId = "o", ChainId = "d", ChainIndex = 1 };
            var otherBranch = new Recording { RecordingId = "b", ChainId = "c", ChainIndex = 1, ChainBranch = 1 };
            var unchained = new Recording { RecordingId = "u", ChainIndex = 0 };

            Assert.True(ParsekFlight.IsEarlierSegmentOfSameChain(head, tip));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(tip, head));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(head, head));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(head, otherChain));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(head, otherBranch));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(unchained, tip));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(null, tip));
            Assert.False(ParsekFlight.IsEarlierSegmentOfSameChain(head, null));
        }
    }
}
