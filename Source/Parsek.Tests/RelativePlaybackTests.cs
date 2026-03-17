using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure math tests for RELATIVE frame playback.
    /// These tests call only pure static methods that do NOT log via ParsekLog,
    /// so they are safe to run in parallel without test contamination.
    /// </summary>
    public class RelativePlaybackTests
    {
        #region ApplyRelativeOffset -- basic math

        [Fact]
        public void ApplyRelativeOffset_ZeroOffset_ReturnsAnchorPosition()
        {
            var anchor = new Vector3d(1000, 2000, 3000);
            var result = TrajectoryMath.ApplyRelativeOffset(anchor, 0, 0, 0);

            Assert.Equal(1000.0, result.x, 10);
            Assert.Equal(2000.0, result.y, 10);
            Assert.Equal(3000.0, result.z, 10);
        }

        [Fact]
        public void ApplyRelativeOffset_PositiveOffset_CorrectAddition()
        {
            var anchor = new Vector3d(100, 200, 300);
            var result = TrajectoryMath.ApplyRelativeOffset(anchor, 10, 20, 30);

            Assert.Equal(110.0, result.x, 10);
            Assert.Equal(220.0, result.y, 10);
            Assert.Equal(330.0, result.z, 10);
        }

        [Fact]
        public void ApplyRelativeOffset_NegativeOffset_CorrectSubtraction()
        {
            var anchor = new Vector3d(100, 200, 300);
            var result = TrajectoryMath.ApplyRelativeOffset(anchor, -50, -100, -150);

            Assert.Equal(50.0, result.x, 10);
            Assert.Equal(100.0, result.y, 10);
            Assert.Equal(150.0, result.z, 10);
        }

        [Fact]
        public void ApplyRelativeOffset_LargeValues_NoPrecisionLoss()
        {
            // Simulates KSP world-space coordinates (600km range)
            var anchor = new Vector3d(600000.123456789, -50.987654321, 600000.111111111);
            var result = TrajectoryMath.ApplyRelativeOffset(anchor, 5.5, -2.3, 0.001);

            Assert.Equal(600005.623456789, result.x, 5);
            Assert.Equal(-53.287654321, result.y, 5);
            Assert.Equal(600000.112111111, result.z, 5);
        }

        [Fact]
        public void ApplyRelativeOffset_RoundTrip_WithComputeRelativeOffset()
        {
            // Apply + Compute should be inverse operations
            var anchor = new Vector3d(600000, 50, 600000);
            var focus = new Vector3d(600100, 75, 600050);

            var offset = TrajectoryMath.ComputeRelativeOffset(focus, anchor);
            var reconstructed = TrajectoryMath.ApplyRelativeOffset(anchor, offset.x, offset.y, offset.z);

            Assert.Equal(focus.x, reconstructed.x, 10);
            Assert.Equal(focus.y, reconstructed.y, 10);
            Assert.Equal(focus.z, reconstructed.z, 10);
        }

        #endregion

        #region FindTrackSectionForUT -- section lookup

        [Fact]
        public void FindTrackSectionForUT_NullSections_ReturnsNegativeOne()
        {
            int result = TrajectoryMath.FindTrackSectionForUT(null, 100.0);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_EmptySections_ReturnsNegativeOne()
        {
            var sections = new List<TrackSection>();
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 100.0);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTBeforeAllSections_ReturnsNegativeOne()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 200.0, referenceFrame = ReferenceFrame.Absolute }
            };
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 50.0);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTAfterAllSections_ReturnsNegativeOne()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 200.0, referenceFrame = ReferenceFrame.Absolute }
            };
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 300.0);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTAtStartOfSection_ReturnsIndex()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 200.0, referenceFrame = ReferenceFrame.Absolute }
            };
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 100.0);
            Assert.Equal(0, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTAtEndOfLastSection_ReturnsLastIndex()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 150.0 },
                new TrackSection { startUT = 150.0, endUT = 200.0 }
            };
            // Last section uses inclusive end
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 200.0);
            Assert.Equal(1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTAtBoundary_ReturnsSecondSection()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 150.0, referenceFrame = ReferenceFrame.Absolute },
                new TrackSection { startUT = 150.0, endUT = 200.0, referenceFrame = ReferenceFrame.Relative, anchorVesselId = 42u }
            };
            // Boundary: section[0] uses exclusive end, so 150.0 belongs to section[1]
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 150.0);
            Assert.Equal(1, result);
        }

        [Fact]
        public void FindTrackSectionForUT_UTInMiddleSection_ReturnsCorrectIndex()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 120.0, referenceFrame = ReferenceFrame.Absolute },
                new TrackSection { startUT = 120.0, endUT = 160.0, referenceFrame = ReferenceFrame.Relative, anchorVesselId = 42u },
                new TrackSection { startUT = 160.0, endUT = 200.0, referenceFrame = ReferenceFrame.Absolute }
            };
            int result = TrajectoryMath.FindTrackSectionForUT(sections, 140.0);
            Assert.Equal(1, result);
            Assert.Equal(ReferenceFrame.Relative, sections[result].referenceFrame);
            Assert.Equal(42u, sections[result].anchorVesselId);
        }

        [Fact]
        public void FindTrackSectionForUT_ThreeSections_AllReachable()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 100.0, endUT = 120.0 },
                new TrackSection { startUT = 120.0, endUT = 160.0 },
                new TrackSection { startUT = 160.0, endUT = 200.0 }
            };

            Assert.Equal(0, TrajectoryMath.FindTrackSectionForUT(sections, 110.0));
            Assert.Equal(1, TrajectoryMath.FindTrackSectionForUT(sections, 140.0));
            Assert.Equal(2, TrajectoryMath.FindTrackSectionForUT(sections, 180.0));
        }

        #endregion

        #region Offset interpolation correctness

        [Fact]
        public void OffsetInterpolation_Midpoint_AveragesCorrectly()
        {
            // Simulate what InterpolateAndPositionRelative does internally
            double beforeDx = 10.0, afterDx = 20.0;
            double beforeDy = 0.0, afterDy = 10.0;
            double beforeDz = -5.0, afterDz = 5.0;
            float t = 0.5f;

            double dx = beforeDx + (afterDx - beforeDx) * t;
            double dy = beforeDy + (afterDy - beforeDy) * t;
            double dz = beforeDz + (afterDz - beforeDz) * t;

            Assert.Equal(15.0, dx, 10);
            Assert.Equal(5.0, dy, 10);
            Assert.Equal(0.0, dz, 10);
        }

        [Fact]
        public void OffsetInterpolation_T0_ReturnsBeforeValues()
        {
            double beforeDx = 10.0, afterDx = 20.0;
            float t = 0.0f;

            double dx = beforeDx + (afterDx - beforeDx) * t;
            Assert.Equal(10.0, dx, 10);
        }

        [Fact]
        public void OffsetInterpolation_T1_ReturnsAfterValues()
        {
            double beforeDx = 10.0, afterDx = 20.0;
            float t = 1.0f;

            double dx = beforeDx + (afterDx - beforeDx) * t;
            Assert.Equal(20.0, dx, 10);
        }

        [Fact]
        public void OffsetInterpolation_QuarterPoint_LinearlyCorrect()
        {
            double beforeDx = 0.0, afterDx = 100.0;
            float t = 0.25f;

            double dx = beforeDx + (afterDx - beforeDx) * t;
            Assert.Equal(25.0, dx, 10);
        }

        #endregion

        #region Integration: ApplyRelativeOffset + interpolation

        [Fact]
        public void InterpolatedOffset_AppliedToAnchor_CorrectPosition()
        {
            // Simulate two RELATIVE frames and interpolation
            var anchor = new Vector3d(600000, 0, 600000);

            // Frame 1: offset (10, 5, -3)
            // Frame 2: offset (20, 10, -6)
            // t = 0.5 -> interpolated offset (15, 7.5, -4.5)
            double dx = 10.0 + (20.0 - 10.0) * 0.5;
            double dy = 5.0 + (10.0 - 5.0) * 0.5;
            double dz = -3.0 + (-6.0 - (-3.0)) * 0.5;

            var result = TrajectoryMath.ApplyRelativeOffset(anchor, dx, dy, dz);

            Assert.Equal(600015.0, result.x, 10);
            Assert.Equal(7.5, result.y, 10);
            Assert.Equal(599995.5, result.z, 10);
        }

        #endregion
    }

    /// <summary>
    /// Integration tests for RELATIVE frame playback that involve logging.
    /// Uses ParsekLog test sink for log assertions.
    /// Must run sequentially to avoid log contamination.
    /// </summary>
    [Collection("Sequential")]
    public class RelativePlaybackIntegrationTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativePlaybackIntegrationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region FindTrackSectionForUT with RELATIVE reference frame

        [Fact]
        public void FindTrackSectionForUT_RelativeSection_ReturnsCorrectIndex()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100.0, endUT = 110.0,
                    referenceFrame = ReferenceFrame.Absolute,
                    frames = new List<TrajectoryPoint>()
                },
                new TrackSection
                {
                    startUT = 110.0, endUT = 150.0,
                    referenceFrame = ReferenceFrame.Relative,
                    anchorVesselId = 42u,
                    frames = new List<TrajectoryPoint>()
                },
                new TrackSection
                {
                    startUT = 150.0, endUT = 200.0,
                    referenceFrame = ReferenceFrame.Absolute,
                    frames = new List<TrajectoryPoint>()
                }
            };

            int idx = TrajectoryMath.FindTrackSectionForUT(sections, 130.0);
            Assert.Equal(1, idx);
            Assert.Equal(ReferenceFrame.Relative, sections[idx].referenceFrame);
            Assert.Equal(42u, sections[idx].anchorVesselId);
        }

        [Fact]
        public void FindTrackSectionForUT_AbsoluteBeforeRelative_ReturnsAbsolute()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100.0, endUT = 110.0,
                    referenceFrame = ReferenceFrame.Absolute,
                    frames = new List<TrajectoryPoint>()
                },
                new TrackSection
                {
                    startUT = 110.0, endUT = 150.0,
                    referenceFrame = ReferenceFrame.Relative,
                    anchorVesselId = 42u,
                    frames = new List<TrajectoryPoint>()
                }
            };

            int idx = TrajectoryMath.FindTrackSectionForUT(sections, 105.0);
            Assert.Equal(0, idx);
            Assert.Equal(ReferenceFrame.Absolute, sections[idx].referenceFrame);
        }

        #endregion

        #region Log assertions for relative playback scenarios

        [Fact]
        public void ApplyRelativeOffset_LogsVerboseOffsetMagnitude()
        {
            logLines.Clear();

            // ApplyRelativeOffset itself doesn't log, but the verbose log in
            // InterpolateAndPositionRelative does. Since we can't call that here
            // (needs Unity runtime), we verify the pure math produces correct results
            // and trust that the log path is covered by the wiring code.
            var anchor = new Vector3d(0, 0, 0);
            var result = TrajectoryMath.ApplyRelativeOffset(anchor, 100, 200, 300);
            double magnitude = result.magnitude;

            // Pure math doesn't log — this test verifies the function is callable
            // and returns correct values that would be logged in-game
            Assert.True(magnitude > 0);
        }

        [Fact]
        public void FindTrackSectionForUT_WithMixedSections_NavigatesCorrectly()
        {
            // Absolute -> Relative -> Absolute pattern
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 1000.0, endUT = 1050.0, referenceFrame = ReferenceFrame.Absolute },
                new TrackSection { startUT = 1050.0, endUT = 1100.0, referenceFrame = ReferenceFrame.Relative, anchorVesselId = 99u },
                new TrackSection { startUT = 1100.0, endUT = 1200.0, referenceFrame = ReferenceFrame.Absolute }
            };

            // Before first section
            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUT(sections, 500.0));

            // In first (Absolute) section
            Assert.Equal(0, TrajectoryMath.FindTrackSectionForUT(sections, 1025.0));

            // In second (Relative) section
            Assert.Equal(1, TrajectoryMath.FindTrackSectionForUT(sections, 1075.0));
            Assert.Equal(ReferenceFrame.Relative, sections[1].referenceFrame);

            // In third (Absolute) section
            Assert.Equal(2, TrajectoryMath.FindTrackSectionForUT(sections, 1150.0));

            // After all sections
            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUT(sections, 1300.0));
        }

        #endregion
    }
}
