using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the foreground-recorder monotonicity guard and the load-time
    /// flat-points sanitizer added alongside the #419 family. The in-game gate is
    /// <c>CommittedRecordingsHaveValidData</c>, which walks <c>rec.Points</c> and
    /// asserts <c>Points[i].ut &gt;= Points[i-1].ut</c>.
    ///
    /// The captured failure shape was a recording whose flat Points list ran
    /// 60.8927 → 61.3727 → 60.9327: a sub-1s backwards step at a RELATIVE→Absolute
    /// section seam (source=Active) that slipped past the foreground commit path's
    /// large-regression-only trim and dual-wrote into the TrackSection frames.
    ///
    /// <see cref="FlightRecorder.ClassifyForegroundCommitTimeOrder"/> closes the
    /// recorder-side gap (foreground analog of the background
    /// <c>ApplyTrajectoryPointToRecording</c> reject); <see
    /// cref="RecordingStore.DropNonMonotonicTrajectoryPoints"/> heals recordings
    /// already persisted with the corruption when they load.
    /// </summary>
    public class ForegroundMonotonicityTests
    {
        // ----- ClassifyForegroundCommitTimeOrder -----

        [Fact]
        public void Classify_EmptyBuffer_Appends()
        {
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.Append,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(0, 50.0, 100.0));
        }

        [Fact]
        public void Classify_MonotonicIncreasing_Appends()
        {
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.Append,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(10, 61.5, 61.37));
        }

        [Fact]
        public void Classify_EqualUT_Appends()
        {
            // Boundary seeds routinely produce same-UT duplicates — tolerated.
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.Append,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(10, 61.37, 61.37));
        }

        [Fact]
        public void Classify_LargeRegression_TrimsThenAppends()
        {
            // > TimeRegressionThresholdSeconds (1.0) backwards = quickload/revert.
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.TrimThenAppend,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(10, 60.0, 61.37));
        }

        [Fact]
        public void Classify_SubThresholdRegression_Rejects()
        {
            // The exact captured shape: lastRecordedUT 61.3727, incoming 60.9327
            // (0.44s backwards) — under the 1.0s revert threshold, so it must be
            // rejected rather than trimmed or appended.
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.RejectNonMonotonic,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(
                    10, 60.932699890135723, 61.372699890135792));
        }

        [Fact]
        public void Classify_RegressionExactlyAtThreshold_Rejects()
        {
            // Exactly TimeRegressionThresholdSeconds backwards is NOT a trim
            // (trim requires strictly more than the threshold), so it is rejected.
            double last = 100.0;
            double incoming = last - FlightRecorder.TimeRegressionThresholdSeconds;
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.RejectNonMonotonic,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(10, incoming, last));
        }

        [Fact]
        public void Classify_RegressionJustPastThreshold_Trims()
        {
            double last = 100.0;
            double incoming = last - FlightRecorder.TimeRegressionThresholdSeconds - 0.001;
            Assert.Equal(FlightRecorder.ForegroundCommitTimeOrder.TrimThenAppend,
                FlightRecorder.ClassifyForegroundCommitTimeOrder(10, incoming, last));
        }

        // ----- DropNonMonotonicTrajectoryPoints -----

        [Fact]
        public void Drop_Null_ReturnsZero()
        {
            Assert.Equal(0, RecordingStore.DropNonMonotonicTrajectoryPoints(null));
        }

        [Fact]
        public void Drop_SinglePoint_ReturnsZero()
        {
            var points = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 100.0 } };
            Assert.Equal(0, RecordingStore.DropNonMonotonicTrajectoryPoints(points));
            Assert.Single(points);
        }

        [Fact]
        public void Drop_AlreadyMonotonic_ReturnsZeroAndPreservesOrder()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 100.0 }, // equal kept
                new TrajectoryPoint { ut = 110.0 },
                new TrajectoryPoint { ut = 120.0 },
            };
            Assert.Equal(0, RecordingStore.DropNonMonotonicTrajectoryPoints(points));
            Assert.Equal(4, points.Count);
        }

        [Fact]
        public void Drop_CapturedCorruptShape_DropsTheBackwardsPoint()
        {
            // Exact flat-Points shape from the f1d2e5fc recording that tripped
            // CommittedRecordingsHaveValidData.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 60.892699890135717 },
                new TrajectoryPoint { ut = 61.372699890135792 },
                new TrajectoryPoint { ut = 60.932699890135723 }, // backwards seam
                new TrajectoryPoint { ut = 61.372699890135792 },
                new TrajectoryPoint { ut = 61.392699890135795 },
            };

            int dropped = RecordingStore.DropNonMonotonicTrajectoryPoints(points);

            Assert.Equal(1, dropped);
            Assert.Equal(4, points.Count);
            for (int i = 1; i < points.Count; i++)
                Assert.True(points[i].ut >= points[i - 1].ut,
                    $"Non-monotonic at index {i}: {points[i].ut} < {points[i - 1].ut}");
            // The two boundary 61.3727 duplicates are kept; only 60.9327 is removed.
            Assert.Equal(60.892699890135717, points[0].ut);
            Assert.Equal(61.372699890135792, points[1].ut);
            Assert.Equal(61.372699890135792, points[2].ut);
            Assert.Equal(61.392699890135795, points[3].ut);
        }

        [Fact]
        public void Drop_MultipleBackwardsPoints_DropsAllAndStaysMonotonic()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 170.92 },
                new TrajectoryPoint { ut = 155.84 }, // backwards
                new TrajectoryPoint { ut = 165.0 },  // still below 170.92 — backwards
                new TrajectoryPoint { ut = 180.0 },
            };

            int dropped = RecordingStore.DropNonMonotonicTrajectoryPoints(points);

            Assert.Equal(2, dropped);
            Assert.Equal(3, points.Count);
            Assert.Equal(100.0, points[0].ut);
            Assert.Equal(170.92, points[1].ut);
            Assert.Equal(180.0, points[2].ut);
        }
    }
}
