using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// When the optimizer joins two chain segments, the absorbed recording's id disappears.
    /// Every reference other recordings hold to it (a Relative section's anchor, a parent
    /// anchor) must follow to the merge target, or those ghosts resolve against a missing
    /// recording. Reached after a Re-Fly merge was given up past its split: the split had
    /// re-pointed other recordings' Relative sections at TIP, and nothing keeps HEAD and TIP
    /// apart once the session ends (review of the recording split / trim / merge fixes,
    /// item 4).
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerMergeAnchorRetagTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerMergeAnchorRetagTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = false;
        }

        private static Recording Segment(string id, int chainIndex, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Chain",
                ChainId = "chain-anchor",
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                PlaybackEnabled = true,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        [Fact]
        public void OptimizerMerge_RepointsOtherRecordingsAnchorsFromTheAbsorbedIdToTheTarget()
        {
            var head = Segment("rec-head", 0, 17000, 17030);
            var tip = Segment("rec-tip", 1, 17030, 17060);
            var docker = new Recording { RecordingId = "rec-docker", VesselName = "Docker" };
            docker.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                anchorRecordingId = "rec-tip",
                startUT = 17040,
                endUT = 17050,
                frames = new List<TrajectoryPoint>(),
            });
            docker.Points.Add(new TrajectoryPoint { ut = 17040 });
            docker.Points.Add(new TrajectoryPoint { ut = 17050 });
            var debris = new Recording
            {
                RecordingId = "rec-debris", VesselName = "Debris",
                IsDebris = true, ParentAnchorRecordingId = "rec-tip",
            };
            debris.Points.Add(new TrajectoryPoint { ut = 17041 });
            debris.Points.Add(new TrajectoryPoint { ut = 17045 });
            RecordingStore.AddRecordingWithTreeForTesting(head);
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            RecordingStore.AddRecordingWithTreeForTesting(docker);
            RecordingStore.AddRecordingWithTreeForTesting(debris);

            CommittedListOptimizer.RunOptimizationPass();

            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == "rec-tip");
            Assert.Equal("rec-head", docker.TrackSections[0].anchorRecordingId);
            Assert.Equal("rec-head", debris.ParentAnchorRecordingId);
            Assert.True(docker.FilesDirty);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("anchors re-pointed rec-tip -> rec-head")
                && l.Contains("sections=1") && l.Contains("parentAnchors=1"));
        }
    }
}
