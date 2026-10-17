using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A switch / Fly continuation segment (a committed vessel flown again from the Tracking
    /// Station, a KSC marker or Map Switch-To) is built by <see cref="SwitchSegmentBuilder"/>
    /// before the vessel's snapshot is taken, so it starts with no
    /// <see cref="Recording.GhostVisualSnapshot"/>. The recorder captures the start snapshot a
    /// moment later in <c>StartRecording</c>; the start backstop must forward it, or the commit
    /// fills the gap from the END snapshot: the segment's ghost then shows the vessel as it ended
    /// (a docked pair for a flight that docked mid-segment), and its crew rows name the crew it
    /// ended with instead of the crew it started with (a kerbal moved into another vessel during
    /// the segment drops out of it).
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentStartSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentStartSnapshotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static TrajectoryPoint Boundary(double ut) => new TrajectoryPoint
        {
            ut = ut,
            altitude = 100.0,
            bodyName = "Kerbin",
        };

        private static RecordingTree TreeWithContinuation(out Recording segment)
        {
            var tree = new RecordingTree
            {
                Id = "tree-station",
                TreeName = "Station",
                BranchPoints = new List<BranchPoint>(),
            };
            var parent = new Recording
            {
                RecordingId = "rec-station",
                TreeId = tree.Id,
                VesselPersistentId = 5001u,
                VesselName = "Station",
                ExplicitStartUT = 100.0,
                GhostVisualSnapshot = CrewSnapshot("Valentina Kerman"),
            };
            parent.Points.Add(Boundary(100.0));
            tree.AddOrReplaceRecording(parent);
            tree.RootRecordingId = parent.RecordingId;
            tree.ActiveRecordingId = parent.RecordingId;

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, parent.RecordingId, focusedVesselPersistentId: 5001u,
                focusedVesselName: "Station", switchUT: 400.0,
                entryReason: SwitchSegmentEntryReason.TrackingStationFly,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: "rec-segment", newBranchPointId: "bp-continue",
                initialBoundaryPointFactory: Boundary,
                sourceVesselPersistentId: 0u);
            Assert.True(result.Created, result.FailureReason);
            segment = tree.Recordings["rec-segment"];
            return tree;
        }

        // The precondition the fix covers: the builder knows no snapshot.
        [Fact]
        public void TheBuilder_LeavesTheSegmentWithoutAStartSnapshot()
        {
            TreeWithContinuation(out Recording segment);
            Assert.Null(segment.GhostVisualSnapshot);
        }

        // catches: the start backstop not forwarding the recorder's start snapshot onto the
        // continuation segment (its ghost and crew then come from the end snapshot).
        [Fact]
        public void TheStartBackstop_ForwardsTheStartSnapshot_AsACopy()
        {
            RecordingTree tree = TreeWithContinuation(out Recording segment);
            ConfigNode start = CrewSnapshot("Valentina Kerman");

            bool changed = FlightRecorder.ApplyStartRecordingTreeBackstop(
                tree, null, null, null, null, null, null, null, start);

            Assert.True(changed);
            Assert.NotNull(segment.GhostVisualSnapshot);
            Assert.NotSame(start, segment.GhostVisualSnapshot);
            Assert.Equal(new List<string> { "Valentina Kerman" },
                CrewReservationManager.ExtractCrewFromSnapshot(segment.GhostVisualSnapshot));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("forwarded start ghost snapshot")
                && l.Contains("rec-segment"));
        }

        // catches: the backstop overwriting a start snapshot a recording already carries
        // (a split child, a resumed recording with history): the first capture wins.
        [Fact]
        public void TheStartBackstop_NeverReplacesAnExistingStartSnapshot()
        {
            RecordingTree tree = TreeWithContinuation(out Recording segment);
            ConfigNode original = CrewSnapshot("Valentina Kerman");
            segment.GhostVisualSnapshot = original;

            FlightRecorder.ApplyStartRecordingTreeBackstop(
                tree, null, null, null, null, null, null, null, CrewSnapshot("Jebediah Kerman"));

            Assert.Same(original, segment.GhostVisualSnapshot);
        }
    }
}
