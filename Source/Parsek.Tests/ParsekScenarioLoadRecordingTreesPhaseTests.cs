using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the per-tree phases extracted from
    /// <c>ParsekScenario.LoadRecordingTrees</c> (<c>LoadRecordingTreesPhase*</c>). Their place
    /// inside the method is pinned by <see cref="ParsekScenarioLoadRecordingTreesCharacterizationTests"/>;
    /// these cells pin what each one does. Touches shared static state (ParsekLog,
    /// RecordingStore), hence Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioLoadRecordingTreesPhaseTests : System.IDisposable
    {
        public ParsekScenarioLoadRecordingTreesPhaseTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "T1" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            return tree;
        }

        // ---- sidecar hydration ----

        // An id the path validator refuses makes LoadRecordingFiles answer false before it
        // touches the file system (or clears a failure reason already on the recording), so
        // the cells drive the real loader headless.

        [Fact]
        public void HydrateSidecars_CountsFailuresAndTheSyntheticFixtureMarkersAmongThem()
        {
            var genuine = new Recording { RecordingId = "bad/one" };
            var synthetic = new Recording { RecordingId = "bad/two" };
            RecordingSidecarStore.MarkSidecarLoadFailure(synthetic, "trajectory-missing");
            var degradedWithPoints = new Recording { RecordingId = "bad/three" };
            degradedWithPoints.Points.Add(new TrajectoryPoint { ut = 1 });
            RecordingSidecarStore.MarkSidecarLoadFailure(degradedWithPoints, "trajectory-missing");

            ParsekScenario.LoadRecordingTreesPhaseHydrateSidecars(
                TreeOf(genuine, synthetic, degradedWithPoints), out int failures, out int syntheticFailures);

            Assert.Equal(3, failures);
            Assert.Equal(1, syntheticFailures);
        }

        [Fact]
        public void HydrateSidecars_AnEmptyTree_ReportsZeroForBoth()
        {
            ParsekScenario.LoadRecordingTreesPhaseHydrateSidecars(
                TreeOf(), out int failures, out int syntheticFailures);

            Assert.Equal(0, failures);
            Assert.Equal(0, syntheticFailures);
        }

        // ---- committed list and running totals ----

        [Fact]
        public void AddTreeRecordings_AddsEachToTheCommittedListAndAddsItsCountsToTheTotals()
        {
            var a = new Recording { RecordingId = "rec_a", VesselName = "A" };
            a.Points.Add(new TrajectoryPoint { ut = 1 });
            a.Points.Add(new TrajectoryPoint { ut = 2 });
            a.OrbitSegments.Add(new OrbitSegment());
            a.PartEvents.Add(new PartEvent());
            a.PartEvents.Add(new PartEvent());
            a.PartEvents.Add(new PartEvent());
            a.TrackSections.Add(new TrackSection());
            a.VesselSnapshot = new ConfigNode("VESSEL");
            var b = new Recording { RecordingId = "rec_b", VesselName = "B" };
            b.Points.Add(new TrajectoryPoint { ut = 3 });
            b.TrackSections = null;

            int recCount = 10, points = 100, orbitSegments = 20, partEvents = 30, withSections = 4, withSnapshots = 5;
            ParsekScenario.LoadRecordingTreesPhaseAddTreeRecordingsToCommitted(
                TreeOf(a, b), ref recCount, ref points, ref orbitSegments, ref partEvents,
                ref withSections, ref withSnapshots);

            Assert.Equal(12, recCount);
            Assert.Equal(103, points);
            Assert.Equal(21, orbitSegments);
            Assert.Equal(33, partEvents);
            Assert.Equal(5, withSections);
            Assert.Equal(6, withSnapshots);
            var ids = new List<string>();
            foreach (var r in RecordingStore.CommittedRecordings) ids.Add(r.RecordingId);
            Assert.Contains("rec_a", ids);
            Assert.Contains("rec_b", ids);
        }

        [Fact]
        public void AddTreeRecordings_AnEmptyTrackSectionList_IsNotCountedAsHavingSections()
        {
            var a = new Recording { RecordingId = "rec_a", VesselName = "A" };
            int recCount = 0, points = 0, orbitSegments = 0, partEvents = 0, withSections = 0, withSnapshots = 0;

            ParsekScenario.LoadRecordingTreesPhaseAddTreeRecordingsToCommitted(
                TreeOf(a), ref recCount, ref points, ref orbitSegments, ref partEvents,
                ref withSections, ref withSnapshots);

            Assert.Equal(1, recCount);
            Assert.Equal(0, withSections);
            Assert.Equal(0, withSnapshots);
        }
    }
}
