using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// OPTIMIZER-SPLIT-EMPTY-TAIL-INDEXED-FIRST: in `2026-09-08_2001_GS-7-kerbalx-crash-watch-hold`
    /// the optimizer cut a crash recording at an Atmospheric -> SurfaceMobile boundary that lay
    /// after its last sample (the recording's end came from the crash UT, past the samples), so
    /// the second half got no points (`second: 0 pts`, `'surface' [0..0]`). With no payload its
    /// StartUT read 0, <see cref="RecordingOptimizer.ReindexChain"/> (by StartUT) gave it chain
    /// index 0 and the head index 1, and every reader that takes the highest chain index as the
    /// chain's end read the head, which has no terminal and no snapshot. These cells drive the
    /// real optimizer pass over that shape, its first-half mirror and a control, and pin
    /// ReindexChain's order for a payload-less member.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitEmptyHalfTests : IDisposable
    {
        private const string TreeId = "tree-gs7-crash";
        private readonly List<string> logLines = new List<string>();

        public OptimizerSplitEmptyHalfTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ------------------------------------------------------------ shapes

        private static ConfigNode Snapshot()
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("root", "0");
            vessel.AddNode("PART").AddValue("persistentId", "7");
            return vessel;
        }

        private static void AddPoints(Recording rec, double from, double to, double step, double alt)
        {
            for (double ut = from; ut <= to + 1e-9; ut += step)
                rec.Points.Add(new TrajectoryPoint { ut = ut, altitude = alt, bodyName = "Kerbin" });
        }

        private static TrackSection Section(SegmentEnvironment env, double start, double end)
        {
            return new TrackSection
            {
                environment = env, startUT = start, endUT = end,
                frames = new List<TrajectoryPoint>(),
            };
        }

        private static Recording Commit(Recording rec)
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "GS-7 shape", RootRecordingId = rec.RecordingId };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        /// <summary>
        /// The GS-7 crash recording: samples [118.9, 347.9] in the atmosphere, a SurfaceMobile
        /// section from 347.92 (after the last sample) to the crash, and the crash UT 357 as its
        /// explicit end, so the recording's EndUT passes the 5 s floor for a second half that
        /// holds no sample.
        /// </summary>
        private static Recording CrashWithBoundaryPastLastSample()
        {
            var rec = new Recording
            {
                RecordingId = "crash",
                VesselName = "Kerbal X Probe",
                TreeId = TreeId,
                VesselPersistentId = 563449404,
                ExplicitEndUT = 357.0,
                TerminalStateValue = TerminalState.Destroyed,
                GhostVisualSnapshot = Snapshot(),
                Points = new List<TrajectoryPoint>(),
            };
            AddPoints(rec, 118.9, 347.9, 1.0, 20000);
            rec.TrackSections.Add(Section(SegmentEnvironment.Atmospheric, 118.9, 347.92));
            rec.TrackSections.Add(Section(SegmentEnvironment.SurfaceMobile, 347.92, 357.0));
            return rec;
        }

        // ------------------------------------------------------------ cells

        // catches: the defect, through the real pass. The cut at 347.92 lies after every sample;
        // the second half would hold no point and read StartUT 0. The recording must stay whole.
        [Fact]
        public void BoundaryPastTheLastSample_NotSplit()
        {
            Recording rec = Commit(CrashWithBoundaryPastLastSample());
            Assert.True(rec.EndUT - 347.92 >= 5.0); // the EndUT-based floor alone passes it

            RecordingStore.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.True(string.IsNullOrEmpty(rec.ChainId));
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("split refused") && l.Contains("rec=crash")
                && l.Contains("second half would hold no trajectory payload"));
        }

        // catches: the mirror. The boundary lies before the first sample (an explicit start
        // earlier than the samples carries the 5 s floor); the first half, which keeps the id
        // and stays the branch point's child, would hold no point. The recording stays whole.
        [Fact]
        public void BoundaryBeforeTheFirstSample_NotSplit()
        {
            var rec = new Recording
            {
                RecordingId = "late-samples",
                VesselName = "Rover",
                TreeId = TreeId,
                VesselPersistentId = 4242,
                ExplicitStartUT = 100.0,
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = Snapshot(),
                VesselSnapshot = Snapshot(),
                Points = new List<TrajectoryPoint>(),
            };
            AddPoints(rec, 120.0, 300.0, 1.0, 5000);
            rec.TrackSections.Add(Section(SegmentEnvironment.SurfaceMobile, 100.0, 110.0));
            rec.TrackSections.Add(Section(SegmentEnvironment.Atmospheric, 110.0, 300.0));
            Commit(rec);
            Assert.True(110.0 - rec.StartUT >= 5.0);

            RecordingStore.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.True(string.IsNullOrEmpty(rec.ChainId));
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("split refused") && l.Contains("rec=late-samples")
                && l.Contains("first half would hold no trajectory payload"));
        }

        // catches: a gate that refuses too much. The same crash with samples running on into the
        // surface section splits as before, and the tail (terminal, snapshot side) is index 1.
        [Fact]
        public void SamplesOnBothSides_SplitsAsBefore()
        {
            Recording rec = CrashWithBoundaryPastLastSample();
            // A sample on the boundary itself (no interpolated boundary point, which needs
            // Unity's Quaternion.Slerp) and samples on into the surface section.
            AddPoints(rec, 347.92, 356.92, 1.0, 80);
            Commit(rec);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Recording tail = RecordingStore.CommittedRecordings.Single(r => r.RecordingId != "crash");
            Assert.Equal(rec.ChainId, tail.ChainId);
            Assert.Equal(0, rec.ChainIndex);
            Assert.Equal(1, tail.ChainIndex);
            Assert.True(tail.Points.Count > 0);
            Assert.Equal(TerminalState.Destroyed, tail.TerminalStateValue);
            Assert.Null(rec.TerminalStateValue);
        }

        // catches: the index half of the defect directly. A chain member with no payload reads
        // StartUT 0; ordering by StartUT put it first. It is ordered by its first track
        // section's start instead, so the head stays index 0.
        [Fact]
        public void ReindexChain_PayloadlessTailWithASection_StaysLast()
        {
            var head = new Recording { RecordingId = "head", ChainId = "c", ChainIndex = 0, Points = new List<TrajectoryPoint>() };
            AddPoints(head, 118.9, 347.9, 1.0, 20000);
            var tail = new Recording { RecordingId = "tail", ChainId = "c", ChainIndex = 1, Points = new List<TrajectoryPoint>() };
            tail.TrackSections.Add(Section(SegmentEnvironment.SurfaceMobile, 347.92, 357.0));
            Assert.Equal(0.0, tail.StartUT);
            var list = new List<Recording> { tail, head };

            RecordingOptimizer.ReindexChain(list, "c");

            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, tail.ChainIndex);
        }

        // catches: a payload-less member with nothing to order it by (no section, no explicit
        // start) going first; it goes last.
        [Fact]
        public void ReindexChain_PayloadlessMemberWithNoBound_GoesLast()
        {
            var head = new Recording { RecordingId = "head", ChainId = "c", ChainIndex = 1, Points = new List<TrajectoryPoint>() };
            AddPoints(head, 10, 20, 1.0, 100);
            var mid = new Recording { RecordingId = "mid", ChainId = "c", ChainIndex = 2, Points = new List<TrajectoryPoint>() };
            AddPoints(mid, 20, 30, 1.0, 100);
            var empty = new Recording { RecordingId = "empty", ChainId = "c", ChainIndex = 0, Points = new List<TrajectoryPoint>() };
            var list = new List<Recording> { empty, mid, head };

            RecordingOptimizer.ReindexChain(list, "c");

            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, mid.ChainIndex);
            Assert.Equal(2, empty.ChainIndex);
        }

        // catches: the control. Members with payload keep the StartUT order (an explicit start
        // earlier than the samples included), and other branches are untouched.
        [Fact]
        public void ReindexChain_MembersWithPayload_OrderedByStartUT()
        {
            var a = new Recording { RecordingId = "a", ChainId = "c", ChainIndex = 5, ExplicitStartUT = 1.0, Points = new List<TrajectoryPoint>() };
            AddPoints(a, 50, 60, 1.0, 100);
            var b = new Recording { RecordingId = "b", ChainId = "c", ChainIndex = 0, Points = new List<TrajectoryPoint>() };
            AddPoints(b, 30, 40, 1.0, 100);
            var other = new Recording { RecordingId = "o", ChainId = "c", ChainBranch = 1, ChainIndex = 9, Points = new List<TrajectoryPoint>() };
            var list = new List<Recording> { b, other, a };

            RecordingOptimizer.ReindexChain(list, "c");

            Assert.Equal(0, a.ChainIndex);
            Assert.Equal(1, b.ChainIndex);
            Assert.Equal(9, other.ChainIndex);
        }
    }
}
