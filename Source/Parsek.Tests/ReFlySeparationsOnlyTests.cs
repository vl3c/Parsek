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
    /// <see cref="RewindPointAuthor"/> and the load-time reap of Rewind Points
    /// older builds authored at an EVA split
    /// (<see cref="LoadTimeSweep.SweepLegacyEvaRewindPoints"/>). The classifier
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
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
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

        // ---------- Legacy EVA Rewind Point reap: pure decision -----------

        [Fact]
        public void ClassifyLegacyEva_NullRp_NotEva()
        {
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.NotEva,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(null, BranchPointType.EVA, null));
        }

        [Theory]
        [InlineData(BranchPointType.Undock)]
        [InlineData(BranchPointType.JointBreak)]
        [InlineData(BranchPointType.Breakup)]
        public void ClassifyLegacyEva_SeparationRp_NotEva(BranchPointType type)
        {
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.NotEva,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(rp, type, null));
        }

        [Fact]
        public void ClassifyLegacyEva_UnresolvedBranchPoint_LeftAlone()
        {
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.BranchPointUnresolved,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(rp, null, null));
        }

        [Fact]
        public void ClassifyLegacyEva_CommittedEvaRp_Reap()
        {
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.Reap,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(rp, BranchPointType.EVA, "rp_other"));
        }

        [Fact]
        public void ClassifyLegacyEva_MarkerRp_KeptForLiveSession()
        {
            // The live-session guard wins even over a session-provisional flag.
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true);
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.KeepLiveSession,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(rp, BranchPointType.EVA, "rp_1"));
        }

        [Fact]
        public void ClassifyLegacyEva_SessionProvisional_KeptForLaterLoad()
        {
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true);
            Assert.Equal(LoadTimeSweep.LegacyEvaRewindPointVerdict.KeepSessionProvisional,
                LoadTimeSweep.ClassifyLegacyEvaRewindPoint(rp, BranchPointType.EVA, null));
        }

        // ---------- Legacy EVA Rewind Point reap: sweep -------------------

        [Fact]
        public void Sweep_LegacyEvaRp_ConcludesOpenTipAndReaps()
        {
            // An older build promoted the stranded kerbal to an open Unfinished
            // Flight (CommittedProvisional). The sweep concludes it and reaps the
            // RP through the ordinary reaper (quicksave, entry, BP back-ref).
            var pod = Rec("rec_pod", MergeState.Immutable, TerminalState.Landed);
            var kerbal = Rec("rec_eva", MergeState.CommittedProvisional, TerminalState.Landed,
                evaCrewName: "Jebediah Kerman");
            var bp = InstallTree(BranchPointType.EVA, "rp_eva", pod, kerbal);
            var rp = Rp("rp_eva", "bp_1", sessionProvisional: false,
                Slot(0, "rec_pod"), Slot(1, "rec_eva"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            var result = LoadTimeSweep.SweepLegacyEvaRewindPoints(scenario);

            Assert.Equal(1, result.Found);
            Assert.Equal(1, result.Reaped);
            Assert.Equal(1, result.TipsConcluded);
            Assert.Equal(0, result.KeptOpen);
            Assert.Equal(MergeState.Immutable, kerbal.MergeState);
            Assert.True(kerbal.FilesDirty);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Equal(new[] { "rp_eva" }, deletedRpIds);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("Legacy EVA Rewind Points (Re-Fly is for vessel separations only): " +
                    "found=1 reaped=1 tipsConcluded=1 keptLiveSession=0 keptSessionProvisional=0 keptOpen=0"));
        }

        [Fact]
        public void Sweep_LegacyEvaRpReferencedByLiveMarker_Kept()
        {
            var pod = Rec("rec_pod", MergeState.Immutable, TerminalState.Landed);
            var kerbal = Rec("rec_eva", MergeState.CommittedProvisional, TerminalState.Landed,
                evaCrewName: "Jebediah Kerman");
            var bp = InstallTree(BranchPointType.EVA, "rp_eva", pod, kerbal);
            var rp = Rp("rp_eva", "bp_1", sessionProvisional: false,
                Slot(0, "rec_pod"), Slot(1, "rec_eva"));
            var scenario = InstallScenario(new List<RewindPoint> { rp },
                new ReFlySessionMarker { SessionId = "sess_live", RewindPointId = "rp_eva" });

            var result = LoadTimeSweep.SweepLegacyEvaRewindPoints(scenario);

            Assert.Equal(1, result.Found);
            Assert.Equal(1, result.KeptLiveSession);
            Assert.Equal(0, result.Reaped);
            Assert.Equal(MergeState.CommittedProvisional, kerbal.MergeState);
            Assert.Same(rp, scenario.RewindPoints.Single());
            Assert.Equal("rp_eva", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("Legacy EVA Rewind Point kept for the live Re-Fly session rp=rp_eva")
                && l.Contains("sess=sess_live"));
        }

        [Fact]
        public void Sweep_SessionProvisionalLegacyEvaRp_Kept()
        {
            var pod = Rec("rec_pod", MergeState.Immutable, TerminalState.Landed);
            var kerbal = Rec("rec_eva", MergeState.Immutable, TerminalState.Landed,
                evaCrewName: "Jebediah Kerman");
            InstallTree(BranchPointType.EVA, "rp_eva", pod, kerbal);
            var rp = Rp("rp_eva", "bp_1", sessionProvisional: true,
                Slot(0, "rec_pod"), Slot(1, "rec_eva"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            var result = LoadTimeSweep.SweepLegacyEvaRewindPoints(scenario);

            Assert.Equal(1, result.KeptSessionProvisional);
            Assert.Equal(0, result.Reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Sweep_UndockRp_Untouched()
        {
            var a = Rec("rec_a", MergeState.CommittedProvisional, TerminalState.Destroyed);
            var b = Rec("rec_b", MergeState.Immutable, TerminalState.Landed);
            InstallTree(BranchPointType.Undock, "rp_undock", a, b);
            var rp = Rp("rp_undock", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            var result = LoadTimeSweep.SweepLegacyEvaRewindPoints(scenario);

            Assert.Equal(0, result.Found);
            Assert.Equal(MergeState.CommittedProvisional, a.MergeState);
            Assert.Single(scenario.RewindPoints);
            Assert.Empty(deletedRpIds);
            Assert.DoesNotContain(logLines, l => l.Contains("Legacy EVA Rewind Point"));
        }

        [Fact]
        public void Sweep_LegacyEvaRpWithNotCommittedTip_NotReaped()
        {
            // A NotCommitted tip means a recorder may still own it: the sweep
            // never flips it and the reaper refuses the open slot.
            var pod = Rec("rec_pod", MergeState.NotCommitted, null);
            var kerbal = Rec("rec_eva", MergeState.Immutable, TerminalState.Landed,
                evaCrewName: "Jebediah Kerman");
            InstallTree(BranchPointType.EVA, "rp_eva", pod, kerbal);
            var rp = Rp("rp_eva", "bp_1", sessionProvisional: false,
                Slot(0, "rec_pod"), Slot(1, "rec_eva"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            var result = LoadTimeSweep.SweepLegacyEvaRewindPoints(scenario);

            Assert.Equal(1, result.Found);
            Assert.Equal(0, result.Reaped);
            Assert.Equal(1, result.KeptOpen);
            Assert.Equal(MergeState.NotCommitted, pod.MergeState);
            Assert.Single(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LoadSweep]")
                && l.Contains("Legacy EVA Rewind Point not reaped: a slot is still open rp=rp_eva"));
        }
    }
}
