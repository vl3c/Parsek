using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #419: debris recording trajectory non-monotonic at the
    /// parent-breakup boundary. The in-game gate is
    /// <c>CommittedRecordingsHaveValidData</c>, which walks
    /// <c>rec.Points</c> and asserts <c>Points[i].ut &gt;= Points[i-1].ut</c>.
    ///
    /// The failure shape was: a debris recording held 13 inherited / duplicated
    /// points ending at UT 170.92, followed by a fresh seed at breakup UT
    /// 155.84 — yielding <c>Points[13].ut 155.84 &lt; Points[12].ut 170.92</c>.
    ///
    /// These tests cover the two pure helpers that enforce the invariant:
    /// <see cref="FlightRecorder.IsAppendUTMonotonic"/> (sampler-level guard
    /// used by <c>BackgroundRecorder.ApplyTrajectoryPointToRecording</c>) and
    /// <see cref="FlightRecorder.TrimPointsAtOrAfterUT"/> (called by
    /// <c>CreateBreakupChildRecording</c> as a final invariant check).
    /// Plus a regression pin mirroring the exact .prec shape captured from
    /// <c>logs/2026-04-14_1801_orbital-spawn-bug</c>.
    /// </summary>
    [Collection("Sequential")]
    public class Bug419DebrisMonotonicityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug419DebrisMonotonicityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- IsAppendUTMonotonic -----

        [Fact]
        public void IsAppendUTMonotonic_NullPoints_ReturnsTrue()
        {
            // Null list is treated as "no prior history" — any append is trivially monotonic.
            Assert.True(FlightRecorder.IsAppendUTMonotonic(null, 100.0));
        }

        [Fact]
        public void IsAppendUTMonotonic_EmptyPoints_ReturnsTrue()
        {
            // Empty list: first append is always monotonic regardless of UT.
            var points = new List<TrajectoryPoint>();
            Assert.True(FlightRecorder.IsAppendUTMonotonic(points, 155.84));
        }

        [Fact]
        public void IsAppendUTMonotonic_StrictlyIncreasingUT_ReturnsTrue()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 155.84 },
                new TrajectoryPoint { ut = 156.86 },
            };
            Assert.True(FlightRecorder.IsAppendUTMonotonic(points, 157.52));
        }

        [Fact]
        public void IsAppendUTMonotonic_EqualUT_ReturnsTrue()
        {
            // Same-UT duplicates are tolerated — boundary seeds and flush-time
            // overlap dedup routinely produce them.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 155.84 },
                new TrajectoryPoint { ut = 170.92 },
            };
            Assert.True(FlightRecorder.IsAppendUTMonotonic(points, 170.92));
        }

        [Fact]
        public void IsAppendUTMonotonic_StrictlyDecreasingUT_ReturnsFalse()
        {
            // The exact failure shape: last UT = 170.92, incoming = 155.84.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 155.84 },
                new TrajectoryPoint { ut = 170.92 },
            };
            Assert.False(FlightRecorder.IsAppendUTMonotonic(points, 155.84));
        }

        // ----- TrimPointsAtOrAfterUT -----

        [Fact]
        public void TrimPointsAtOrAfterUT_NullPoints_ReturnsZero()
        {
            Assert.Equal(0, FlightRecorder.TrimPointsAtOrAfterUT(null, 100.0));
        }

        [Fact]
        public void TrimPointsAtOrAfterUT_EmptyPoints_ReturnsZero()
        {
            var points = new List<TrajectoryPoint>();
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 100.0);
            Assert.Equal(0, removed);
            Assert.Empty(points);
        }

        [Fact]
        public void TrimPointsAtOrAfterUT_AllPointsBeforeBoundary_ReturnsZero()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 120.0 },
                new TrajectoryPoint { ut = 140.0 },
            };
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 150.0);
            Assert.Equal(0, removed);
            Assert.Equal(3, points.Count);
        }

        [Fact]
        public void TrimPointsAtOrAfterUT_BoundaryAtSampleGrid_RemovesBoundaryAndLater()
        {
            // Breakup UT lands exactly on an existing sample — inclusive trim
            // removes the boundary sample itself so the re-seeded authoritative
            // initial point (also at breakupUT) becomes Points[0] cleanly.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 120.0 },
                new TrajectoryPoint { ut = 155.84 }, // breakup UT
                new TrajectoryPoint { ut = 160.0 },
                new TrajectoryPoint { ut = 170.92 },
            };
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 155.84);
            Assert.Equal(3, removed);
            Assert.Equal(2, points.Count);
            Assert.Equal(100.0, points[0].ut);
            Assert.Equal(120.0, points[1].ut);
        }

        [Fact]
        public void TrimPointsAtOrAfterUT_BoundaryBetweenSamples_RemovesOnlyLaterPoints()
        {
            // Off-grid breakup: boundary UT falls between two samples. The trim
            // keeps the earlier sample (UT < breakup) and drops everything >=
            // breakup, preserving monotonicity for the subsequent seed at
            // breakupUT.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 150.0 },
                new TrajectoryPoint { ut = 160.0 },
                new TrajectoryPoint { ut = 170.92 },
            };
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 155.84);
            Assert.Equal(2, removed);
            Assert.Equal(2, points.Count);
            Assert.Equal(100.0, points[0].ut);
            Assert.Equal(150.0, points[1].ut);
        }

        [Fact]
        public void TrimPointsAtOrAfterUT_AllPointsAtOrAfterBoundary_RemovesAll()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 200.0 },
                new TrajectoryPoint { ut = 210.0 },
            };
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 100.0);
            Assert.Equal(2, removed);
            Assert.Empty(points);
        }

        // ----- ApplyTrajectoryPointToRecording guard -----

        [Fact]
        public void ApplyTrajectoryPointToRecording_FirstPoint_AppendsAndSetsEndUT()
        {
            var rec = new Recording { RecordingId = "test419-empty" };
            var pt = new TrajectoryPoint { ut = 155.84, bodyName = "Kerbin" };

            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec, pt);

            Assert.Single(rec.Points);
            Assert.Equal(155.84, rec.Points[0].ut);
            Assert.Equal(155.84, rec.ExplicitEndUT);
        }

        [Fact]
        public void ApplyTrajectoryPointToRecording_MonotonicAppend_Appends()
        {
            var rec = new Recording { RecordingId = "test419-monotonic" };
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" });
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 120.0, bodyName = "Kerbin" });

            Assert.Equal(3, rec.Points.Count);
            Assert.Equal(120.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void ApplyTrajectoryPointToRecording_EqualUT_AppendsDuplicate()
        {
            // Same-UT duplicates are accepted so boundary seeds (two
            // breakup-boundary points at the same UT from different sampler
            // paths) still flow through without dropping data.
            var rec = new Recording { RecordingId = "test419-equal-ut" };
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 155.84, bodyName = "Kerbin" });
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 155.84, bodyName = "Kerbin" });

            Assert.Equal(2, rec.Points.Count);
        }

        [Fact]
        public void ApplyTrajectoryPointToRecording_NonMonotonicAppend_RejectsAndLogs()
        {
            // The exact regression shape: Points[0..12] ending at UT 170.92,
            // then a belated append at UT 155.84 (breakup seed consumed after
            // physics-frame samples already advanced, or a duplicate from a
            // flush-overlap mismatch). Without the guard, this produced the
            // Points[13].ut 155.84 < Points[12].ut 170.92 failure signature.
            var rec = new Recording { RecordingId = "test419-regression" };
            double[] utGrid =
            {
                155.84, 156.86, 157.52, 158.24, 159.12, 160.10, 161.20,
                162.38, 163.62, 164.92, 166.92, 168.92, 170.92
            };
            foreach (var ut in utGrid)
            {
                BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                    new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            }
            Assert.Equal(13, rec.Points.Count);

            // Belated append at the breakup UT — must be rejected, not added.
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 155.84, bodyName = "Kerbin" });

            Assert.Equal(13, rec.Points.Count);
            Assert.Equal(170.92, rec.Points[12].ut);
            // Verify monotonicity is preserved across the whole list.
            for (int i = 1; i < rec.Points.Count; i++)
                Assert.True(rec.Points[i].ut >= rec.Points[i - 1].ut,
                    $"Non-monotonic at index {i}: {rec.Points[i].ut} < {rec.Points[i - 1].ut}");

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") &&
                l.Contains("rejected non-monotonic UT") &&
                l.Contains("test419-regression") &&
                l.Contains("#419"));
        }

        [Fact]
        public void ApplyTrajectoryPointToRecording_NonMonotonicAppend_EndUTStaysAtLastGoodValue()
        {
            // Rejected non-monotonic appends must not poison ExplicitEndUT either,
            // otherwise save/load diagnostics would see a recording whose metadata
            // claims an earlier end than its actual Points tail.
            var rec = new Recording { RecordingId = "test419-endut" };
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 170.92, bodyName = "Kerbin" });
            Assert.Equal(170.92, rec.ExplicitEndUT);

            BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                new TrajectoryPoint { ut = 50.0, bodyName = "Kerbin" });

            Assert.Equal(2, rec.Points.Count);
            Assert.Equal(170.92, rec.ExplicitEndUT);
        }

        [Fact]
        public void ApplyTrajectoryPointToRecording_NullRecording_DoesNotThrow()
        {
            var pt = new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" };
            var ex = Record.Exception(() =>
                BackgroundRecorder.ApplyTrajectoryPointToRecording(null, pt));
            Assert.Null(ex);
        }

        // ----- Regression pin matching the .prec shape captured from the
        // ----- 2026-04-14_1801_orbital-spawn-bug log bundle. -----

        [Fact]
        public void Regression_DuplicateThirteenPointBlock_GuardRejectsAllDuplicates()
        {
            // Exact reproduction of the captured .prec Points sequence: 13
            // samples at UTs 155.84 → 170.92, then a duplicate of the same 13
            // samples. Without the guard, all 26 would be appended and
            // Points[13].ut 155.84 < Points[12].ut 170.92 (the reported failure).
            // With the guard, the second block is rejected entry-by-entry so
            // the final recording holds only the first 13 monotonic samples.
            var rec = new Recording { RecordingId = "393b82ccb697492bb7b35c6c621f9d07" };
            double[] utGrid =
            {
                155.84000000000592, 156.86000000000644, 157.52000000000677,
                158.24000000000714, 159.12000000000759, 160.10000000000809,
                161.20000000000866, 162.38000000000926, 163.6200000000099,
                164.92000000001056, 166.92000000001158, 168.92000000001261,
                170.92000000001363
            };
            foreach (var ut in utGrid)
            {
                BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                    new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            }
            // Attempt to append the same block again.
            foreach (var ut in utGrid)
            {
                BackgroundRecorder.ApplyTrajectoryPointToRecording(rec,
                    new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            }

            // The guard rejects only strict UT regressions (incoming < last). In the
            // duplicate block the first 12 duplicates all have UT < 170.92, so they
            // are all rejected; the final duplicate at UT == 170.92 is accepted
            // (same-UT duplicates are tolerated by design — boundary-seed dedup is
            // a downstream concern). The failure-mode invariant that matters is
            // monotonicity, which holds for the final 14-point list.
            Assert.Equal(14, rec.Points.Count);
            for (int i = 1; i < rec.Points.Count; i++)
                Assert.True(rec.Points[i].ut >= rec.Points[i - 1].ut,
                    $"Non-monotonic at index {i}: {rec.Points[i].ut} < {rec.Points[i - 1].ut}");

            int rejectionLogs = 0;
            foreach (var l in logLines)
                if (l.Contains("rejected non-monotonic UT") && l.Contains("#419"))
                    rejectionLogs++;
            Assert.Equal(12, rejectionLogs);
        }

        // ----- TrimPointsAtOrAfterUT applied at the breakup boundary -----

        [Fact]
        public void TrimAtBreakupBoundary_EmptyChildRecording_IsNoOp()
        {
            // The normal debris-creation path: the child recording is brand-new
            // with no points yet. The trim call is a cheap invariant check and
            // must be a no-op.
            var points = new List<TrajectoryPoint>();
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(points, 155.84);
            Assert.Equal(0, removed);
            Assert.Empty(points);
        }

        [Fact]
        public void TrimAtBreakupBoundary_GridAlignedBreakup_NewSeedStartsMonotonic()
        {
            // Simulate the full post-fix flow: child recording inherited 5
            // points running past the breakup UT; the coalescer trims at the
            // breakup boundary and then the initial-point seed is applied.
            var child = new Recording { RecordingId = "test419-grid" };
            child.Points.Add(new TrajectoryPoint { ut = 150.0 });
            child.Points.Add(new TrajectoryPoint { ut = 155.84 });  // breakup UT
            child.Points.Add(new TrajectoryPoint { ut = 160.0 });
            child.Points.Add(new TrajectoryPoint { ut = 165.0 });
            child.Points.Add(new TrajectoryPoint { ut = 170.92 });

            double breakupUT = 155.84;
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(child.Points, breakupUT);
            Assert.Equal(4, removed);
            Assert.Single(child.Points);
            Assert.Equal(150.0, child.Points[0].ut);

            // Now apply the authoritative initial seed at the breakup UT.
            BackgroundRecorder.ApplyTrajectoryPointToRecording(child,
                new TrajectoryPoint { ut = breakupUT, bodyName = "Kerbin" });
            Assert.Equal(2, child.Points.Count);
            Assert.Equal(150.0, child.Points[0].ut);
            Assert.Equal(breakupUT, child.Points[1].ut);

            // Simulate post-breakup physics-frame samples climbing.
            BackgroundRecorder.ApplyTrajectoryPointToRecording(child,
                new TrajectoryPoint { ut = breakupUT + 0.5, bodyName = "Kerbin" });
            BackgroundRecorder.ApplyTrajectoryPointToRecording(child,
                new TrajectoryPoint { ut = breakupUT + 1.0, bodyName = "Kerbin" });

            Assert.Equal(4, child.Points.Count);
            for (int i = 1; i < child.Points.Count; i++)
                Assert.True(child.Points[i].ut >= child.Points[i - 1].ut);
        }

        [Fact]
        public void TrimAtBreakupBoundary_OffGridBreakup_LastInheritedIsStrictlyBefore()
        {
            // Off-grid breakup UT (between two inherited samples). The trim
            // must leave the last kept sample with UT < breakupUT so the
            // incoming initial-point seed at breakupUT extends the list
            // monotonically (strictly greater).
            var child = new Recording { RecordingId = "test419-offgrid" };
            child.Points.Add(new TrajectoryPoint { ut = 100.0 });
            child.Points.Add(new TrajectoryPoint { ut = 150.0 });
            child.Points.Add(new TrajectoryPoint { ut = 160.0 });
            child.Points.Add(new TrajectoryPoint { ut = 170.92 });

            double breakupUT = 155.84;
            int removed = FlightRecorder.TrimPointsAtOrAfterUT(child.Points, breakupUT);
            Assert.Equal(2, removed);
            Assert.Equal(2, child.Points.Count);
            Assert.True(child.Points[child.Points.Count - 1].ut < breakupUT,
                "Last inherited sample UT must be strictly less than breakup UT");

            BackgroundRecorder.ApplyTrajectoryPointToRecording(child,
                new TrajectoryPoint { ut = breakupUT, bodyName = "Kerbin" });
            Assert.Equal(3, child.Points.Count);
            Assert.Equal(breakupUT, child.Points[2].ut);
            for (int i = 1; i < child.Points.Count; i++)
                Assert.True(child.Points[i].ut >= child.Points[i - 1].ut);
        }
    }
}
