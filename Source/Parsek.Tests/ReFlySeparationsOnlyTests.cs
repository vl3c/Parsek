using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-09-27: Re-Fly is for vessel separations only (staging,
    /// decoupling, undocking). An EVA is not a separation and never gets a
    /// Rewind Point or a Re-Fly. Covers the authoring gate in
    /// <see cref="RewindPointAuthor"/>. The classifier
    /// side (an EVA kerbal never qualifies) lives in
    /// <c>UnfinishedFlightsMembershipTests</c> and <c>TreeCommitTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlySeparationsOnlyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlySeparationsOnlyTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointReaper.ResetTestOverrides();
            RewindPointReaper.ReleaseQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };
        }

        public void Dispose()
        {
            RewindPointAuthor.SyncRunForTesting = null;
            HighLogic.LoadedScene = GameScenes.LOADING;
            RewindPointReaper.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers -----------------------------------------------

        private static Recording Rec(string id, MergeState state, TerminalState? terminal,
            string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = state,
                TerminalStateValue = terminal,
                EvaCrewName = evaCrewName,
                ParentBranchPointId = "bp_1",
            };
        }

        private static ChildSlot Slot(int index, string originRecordingId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
            };
        }

        private static RewindPoint Rp(string id, string bpId, bool sessionProvisional,
            params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = bpId,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = sessionProvisional,
                ChildSlots = new List<ChildSlot>(slots),
            };
        }

        private static BranchPoint InstallTree(BranchPointType type, string rpId,
            params Recording[] recordings)
        {
            var bp = new BranchPoint
            {
                Id = "bp_1",
                Type = type,
                RewindPointId = rpId,
                ChildRecordingIds = recordings.Select(r => r.RecordingId).ToList(),
            };
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint> { bp },
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == "tree_1") trees.RemoveAt(i);
            trees.Add(tree);
            return bp;
        }

        private static ParsekScenario InstallScenario(List<RewindPoint> rps,
            ReFlySessionMarker marker = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps,
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // ---------- Authoring gate ----------------------------------------

        [Theory]
        [InlineData(BranchPointType.EVA, false)]
        [InlineData(BranchPointType.Undock, true)]
        [InlineData(BranchPointType.JointBreak, true)]
        [InlineData(BranchPointType.Breakup, true)]
        public void IsReFlySplitType_OnlyEvaRefused(BranchPointType type, bool expected)
        {
            Assert.Equal(expected, RewindPointAuthor.IsReFlySplitType(type));
        }

        [Fact]
        public void Begin_EvaSplit_AuthorsNoRewindPointAndLogsRuling()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var scenario = InstallScenario(new List<RewindPoint>());
            var bp = new BranchPoint { Id = "bp_eva", Type = BranchPointType.EVA };
            bool deferredRan = false;
            RewindPointAuthor.SyncRunForTesting = (rp, ctx) => deferredRan = true;

            RewindPoint result = RewindPointAuthor.Begin(
                bp,
                new List<ChildSlot> { Slot(0, "rec_pod"), Slot(1, "rec_kerbal") },
                new List<uint> { 1u, 2u });

            Assert.Null(result);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.False(deferredRan);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Rewind]")
                && l.Contains("Split type=EVA: no Rewind Point (Re-Fly is for vessel separations only) bp=bp_eva"));
        }

        [Fact]
        public void Begin_UndockSplit_StillAuthorsRewindPoint()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var scenario = InstallScenario(new List<RewindPoint>());
            var bp = new BranchPoint { Id = "bp_undock", Type = BranchPointType.Undock };
            RewindPointAuthor.SyncRunForTesting = (rp, ctx) => { };

            RewindPoint result = RewindPointAuthor.Begin(
                bp,
                new List<ChildSlot> { Slot(0, "rec_a"), Slot(1, "rec_b") },
                new List<uint> { 1u, 2u });

            Assert.NotNull(result);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal(result.RewindPointId, bp.RewindPointId);
            Assert.DoesNotContain(logLines, l => l.Contains("no Rewind Point (Re-Fly is for vessel separations only)"));
        }
    }
}
