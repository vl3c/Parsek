using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct unit tests for the pure decision units of ParsekFlightGhostPositioner: bracket and
    /// section lookups, the boundary-bridge frame search, the checkpoint orbit lookup and cache
    /// key, the predicted orbit-tail continuity gate and key, the tail-lift reason helpers, the
    /// Trace-Sep row helpers and the small formatters.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPositionerUnitTests : IDisposable
    {
        public GhostPositionerUnitTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static TrajectoryPoint Pt(double ut, string body = null, double lat = 0, double lon = 0, double alt = 0)
        {
            return new TrajectoryPoint { ut = ut, bodyName = body, latitude = lat, longitude = lon, altitude = alt };
        }

        private static List<TrajectoryPoint> Pts(params double[] uts)
        {
            var list = new List<TrajectoryPoint>();
            foreach (double ut in uts)
                list.Add(Pt(ut));
            return list;
        }

        private static TrackSection Section(ReferenceFrame frame, double start, double end,
            List<TrajectoryPoint> frames = null, List<TrajectoryPoint> bodyFixed = null,
            List<OrbitSegment> checkpoints = null)
        {
            return new TrackSection
            {
                referenceFrame = frame,
                startUT = start,
                endUT = end,
                frames = frames,
                bodyFixedFrames = bodyFixed,
                checkpoints = checkpoints,
            };
        }

        private static OrbitSegment Seg(double start, double end, bool predicted = false, string body = "Kerbin")
        {
            return new OrbitSegment { startUT = start, endUT = end, isPredicted = predicted, bodyName = body };
        }

        // --- ResolveBodyFixedPrimaryBracketUTs ---

        [Fact]
        public void BodyFixedBracket_FewerThanTwoFrames_IsNaN()
        {
            ParsekFlightGhostPositioner.ResolveBodyFixedPrimaryBracketUTs(null, 5, out double b, out double a);
            Assert.True(double.IsNaN(b) && double.IsNaN(a));
            ParsekFlightGhostPositioner.ResolveBodyFixedPrimaryBracketUTs(Pts(5), 5, out b, out a);
            Assert.True(double.IsNaN(b) && double.IsNaN(a));
        }

        [Theory]
        [InlineData(15.0, 10.0, 20.0)]
        [InlineData(10.0, 0.0, 10.0)]
        [InlineData(20.0, 10.0, 20.0)]
        [InlineData(-5.0, 0.0, 10.0)]
        [InlineData(99.0, 20.0, 30.0)]
        public void BodyFixedBracket_InsideOrClampedToEndpoints(double ut, double before, double after)
        {
            ParsekFlightGhostPositioner.ResolveBodyFixedPrimaryBracketUTs(Pts(0, 10, 20, 30), ut,
                out double b, out double a);
            Assert.Equal(before, b);
            Assert.Equal(after, a);
        }

        // --- FindNearestTrackSectionIndexForContinuityAnchor ---

        [Fact]
        public void NearestContinuitySection_EmptyIsMinusOne()
        {
            Assert.Equal(-1, ParsekFlightGhostPositioner.FindNearestTrackSectionIndexForContinuityAnchor(null, 5));
            Assert.Equal(-1, ParsekFlightGhostPositioner.FindNearestTrackSectionIndexForContinuityAnchor(
                new List<TrackSection>(), 5));
        }

        [Theory]
        [InlineData(-1.0, 0)]
        [InlineData(5.0, 0)]
        [InlineData(15.0, 1)]
        [InlineData(19.9999995, 2)]
        [InlineData(500.0, 2)]
        [InlineData(double.NaN, 2)]
        public void NearestContinuitySection_LastSectionStartingAtOrBeforeAnchor(double ut, int expected)
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 0, 10),
                Section(ReferenceFrame.Absolute, 10, 20),
                Section(ReferenceFrame.Absolute, 20, 30),
            };
            Assert.Equal(expected, ParsekFlightGhostPositioner.FindNearestTrackSectionIndexForContinuityAnchor(sections, ut));
        }

        // --- predicted orbit-tail continuity ---

        [Fact]
        public void TailContinuity_RequiresPointsAndPredictedSegment()
        {
            var rec = new Recording { RecordingId = "r" };
            Assert.False(ParsekFlightGhostPositioner.ShouldApplyPredictedOrbitTailContinuity(null, Seg(100, 200, true), 101));
            Assert.False(ParsekFlightGhostPositioner.ShouldApplyPredictedOrbitTailContinuity(rec, Seg(100, 200, true), 101));
            rec.Points.Add(Pt(100));
            Assert.False(ParsekFlightGhostPositioner.ShouldApplyPredictedOrbitTailContinuity(rec, Seg(100, 200, false), 101));
            Assert.True(ParsekFlightGhostPositioner.ShouldApplyPredictedOrbitTailContinuity(rec, Seg(100, 200, true), 101));
        }

        [Theory]
        // last point at 100; segment starts at 100 -> blend window max(5, 0 + 2) = 5 s
        [InlineData(100.0, 100.0, false)]
        [InlineData(100.0, 100.5, true)]
        [InlineData(100.0, 105.0, true)]
        [InlineData(100.0, 105.1, false)]
        // segment starts 6 s after the last point -> blend 8 s
        [InlineData(106.0, 108.0, true)]
        [InlineData(106.0, 108.1, false)]
        // a 20 s gap caps the blend at 10 s
        [InlineData(120.0, 110.0, true)]
        [InlineData(120.0, 110.1, false)]
        public void TailContinuity_OnlyInsideTheBlendWindowAfterTheLastPoint(double segStart, double ut, bool expected)
        {
            var rec = new Recording { RecordingId = "r" };
            rec.Points.Add(Pt(90));
            rec.Points.Add(Pt(100));
            Assert.Equal(expected,
                ParsekFlightGhostPositioner.ShouldApplyPredictedOrbitTailContinuity(rec, Seg(segStart, 300, true), ut));
        }

        [Fact]
        public void TailContinuityCacheKey_JoinsIdentityAndElements()
        {
            var rec = new Recording { RecordingId = "rec-1" };
            var seg = new OrbitSegment
            {
                bodyName = "Mun", startUT = 10.5, endUT = 20, epoch = 3, semiMajorAxis = 250000.25,
                eccentricity = 0.1, inclination = 7,
            };
            Assert.Equal("rec-1|Mun|9.75|10.5|20|3|250000.25|0.1|7",
                ParsekFlightGhostPositioner.BuildPredictedOrbitTailContinuityCacheKey(rec, seg, 9.75));
        }

        [Fact]
        public void TailContinuityCacheKey_FallsBackToVesselNameThenPlaceholder()
        {
            var named = new Recording { RecordingId = "", VesselName = "Probe" };
            var seg = new OrbitSegment { startUT = 1, endUT = 2 };
            Assert.StartsWith("Probe|<no-body>|0|1|2|",
                ParsekFlightGhostPositioner.BuildPredictedOrbitTailContinuityCacheKey(named, seg, 0));
            Assert.StartsWith("<no-recording-id>|<no-body>|",
                ParsekFlightGhostPositioner.BuildPredictedOrbitTailContinuityCacheKey(null, seg, 0));
        }

        // --- checkpoint orbit lookup and cache key ---

        [Fact]
        public void CheckpointSegment_NoCheckpoints_False()
        {
            Assert.False(ParsekFlightGhostPositioner.TryFindCheckpointOrbitSegment(
                Section(ReferenceFrame.OrbitalCheckpoint, 0, 10), 5, out _, out int idx));
            Assert.Equal(-1, idx);
            Assert.False(ParsekFlightGhostPositioner.TryFindCheckpointOrbitSegment(
                Section(ReferenceFrame.OrbitalCheckpoint, 0, 10, checkpoints: new List<OrbitSegment>()), 5, out _, out _));
        }

        [Fact]
        public void CheckpointSegment_FirstCoveringCheckpointInclusive()
        {
            var section = Section(ReferenceFrame.OrbitalCheckpoint, 0, 40, checkpoints: new List<OrbitSegment>
            {
                Seg(0, 10, body: "a"), Seg(10, 20, body: "b"), Seg(30, 40, body: "c"),
            });
            Assert.True(ParsekFlightGhostPositioner.TryFindCheckpointOrbitSegment(section, 10, out var seg, out int idx));
            Assert.Equal("a", seg.bodyName);
            Assert.Equal(0, idx);
            Assert.True(ParsekFlightGhostPositioner.TryFindCheckpointOrbitSegment(section, 40, out seg, out idx));
            Assert.Equal(2, idx);
            Assert.False(ParsekFlightGhostPositioner.TryFindCheckpointOrbitSegment(section, 25, out _, out idx));
            Assert.Equal(-1, idx);
        }

        [Fact]
        public void CheckpointCacheKey_IsNegativeAndStrided()
        {
            long k000 = ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 0, 0);
            Assert.Equal(long.MinValue, k000);
            Assert.Equal(k000 + 1, ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 0, 1));
            Assert.Equal(k000 + 1000000L, ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 1, 0));
            Assert.Equal(k000 + 1000000000000L, ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(1, 0, 0));
            Assert.True(ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(int.MaxValue, 999999, 999999) < 0);
        }

        [Fact]
        public void CheckpointCacheKey_ClampsNegativeAndOverflowingParts()
        {
            Assert.Equal(ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 0, 0),
                ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(-5, -1, -1));
            Assert.Equal(ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 999999, 999999),
                ParsekFlightGhostPositioner.BuildCheckpointOrbitCacheKey(0, 5000000, 7000000));
        }

        // --- boundary bridge frames ---

        [Fact]
        public void AbsoluteFrameList_ByReferenceFrame()
        {
            var frames = Pts(1);
            var bodyFixed = Pts(2);
            Assert.Same(frames, ParsekFlightGhostPositioner.GetAbsolutePlaybackFrameList(
                Section(ReferenceFrame.Absolute, 0, 1, frames, bodyFixed)));
            Assert.Same(bodyFixed, ParsekFlightGhostPositioner.GetAbsolutePlaybackFrameList(
                Section(ReferenceFrame.Relative, 0, 1, frames, bodyFixed)));
            Assert.Null(ParsekFlightGhostPositioner.GetAbsolutePlaybackFrameList(
                Section(ReferenceFrame.OrbitalCheckpoint, 0, 1, frames, bodyFixed)));
        }

        [Theory]
        [InlineData(10.0, double.NaN, double.NaN, true)]
        [InlineData(10.0, 11.0, double.NaN, true)]
        [InlineData(10.0, 10.0, double.NaN, false)]
        [InlineData(10.0, 10.0000005, double.NaN, false)]
        [InlineData(10.0, double.NaN, 9.0, true)]
        [InlineData(10.0, double.NaN, 10.0, false)]
        [InlineData(10.0, double.NaN, 9.9999995, false)]
        public void BridgeFrame_StrictUtWindowWithEpsilon(double ut, double requireBefore, double requireAfter, bool expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.IsEligibleBoundaryBridgeFrame(
                Pt(ut), requireBefore, requireAfter, null));
        }

        [Theory]
        [InlineData(null, "Kerbin", true)]
        [InlineData("Kerbin", null, true)]
        [InlineData("Kerbin", "Kerbin", true)]
        [InlineData("Kerbin", "kerbin", false)]
        [InlineData("Kerbin", "Mun", false)]
        public void BridgeFrame_BodyMatchesWhenBothKnown(string candidateBody, string requiredBody, bool expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.IsEligibleBoundaryBridgeFrame(
                Pt(5, candidateBody), double.NaN, double.NaN, requiredBody));
        }

        [Fact]
        public void AdjacentFrame_InvalidInputs_False()
        {
            var sections = new List<TrackSection> { Section(ReferenceFrame.Absolute, 0, 10, Pts(1, 2)) };
            Assert.False(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                null, 0, true, double.NaN, double.NaN, null, out _));
            Assert.False(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, -1, true, double.NaN, double.NaN, null, out _));
            Assert.False(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 1, true, double.NaN, double.NaN, null, out _));
            var checkpointOnly = new List<TrackSection> { Section(ReferenceFrame.OrbitalCheckpoint, 0, 10, Pts(1)) };
            Assert.False(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                checkpointOnly, 0, true, double.NaN, double.NaN, null, out _));
        }

        [Fact]
        public void AdjacentFrame_ForwardTakesFirstEligible_BackwardTakesLastEligible()
        {
            var frames = new List<TrajectoryPoint>
            {
                Pt(1, "Mun"), Pt(2, "Kerbin"), Pt(3, "Kerbin"), Pt(4, "Mun"),
            };
            var sections = new List<TrackSection> { Section(ReferenceFrame.Absolute, 0, 10, frames) };

            Assert.True(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 0, true, double.NaN, double.NaN, "Kerbin", out var fwd));
            Assert.Equal(2, fwd.ut);
            Assert.True(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 0, false, double.NaN, double.NaN, "Kerbin", out var back));
            Assert.Equal(3, back.ut);
            Assert.True(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 0, false, 3.0, double.NaN, null, out var beforeThree));
            Assert.Equal(2, beforeThree.ut);
            Assert.False(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 0, true, double.NaN, 4.0, null, out _));
        }

        [Fact]
        public void AdjacentFrame_RelativeSectionSearchesBodyFixedFrames()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 10, frames: Pts(1), bodyFixed: Pts(7)),
            };
            Assert.True(ParsekFlightGhostPositioner.TryFindAdjacentAbsolutePlaybackFrame(
                sections, 0, true, double.NaN, double.NaN, null, out var frame));
            Assert.Equal(7, frame.ut);
        }

        // --- section membership and bracket widths ---

        [Fact]
        public void PointBelongsToSection_UsesSectionLookupWhenIndexValid()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 0, 10),
                Section(ReferenceFrame.Absolute, 10, 20),
            };
            Assert.False(ParsekFlightGhostPositioner.PointBelongsToSection(sections, 0, sections[0], 10));
            Assert.True(ParsekFlightGhostPositioner.PointBelongsToSection(sections, 1, sections[1], 10));
            Assert.True(ParsekFlightGhostPositioner.PointBelongsToSection(sections, 1, sections[1], 20));
        }

        [Fact]
        public void PointBelongsToSection_FallsBackToInclusiveSectionBounds()
        {
            var section = Section(ReferenceFrame.Absolute, 0, 10);
            Assert.True(ParsekFlightGhostPositioner.PointBelongsToSection(null, 0, section, 10));
            Assert.True(ParsekFlightGhostPositioner.PointBelongsToSection(new List<TrackSection>(), 3, section, 0));
            Assert.False(ParsekFlightGhostPositioner.PointBelongsToSection(null, 0, section, 10.1));
        }

        [Fact]
        public void CountDenseFrames_CountsOnlyPointsInTheSection()
        {
            var section = Section(ReferenceFrame.Relative, 10, 20);
            Assert.Equal(0, ParsekFlightGhostPositioner.CountDensebodyFixedFramesForSection(null, -1, section, null));
            Assert.Equal(3, ParsekFlightGhostPositioner.CountDensebodyFixedFramesForSection(
                null, -1, section, Pts(5, 10, 15, 20, 25)));
        }

        [Fact]
        public void DenseBracketWidth_NearestPointsInSectionAroundUt()
        {
            var section = Section(ReferenceFrame.Relative, 10, 30);
            var points = Pts(5, 12, 18, 24, 35);
            Assert.True(ParsekFlightGhostPositioner.TryGetDenseSectionBracketWidthSeconds(
                null, -1, section, points, 20, out double width));
            Assert.Equal(6, width);
            Assert.True(ParsekFlightGhostPositioner.TryGetDenseSectionBracketWidthSeconds(
                null, -1, section, points, 18, out width));
            Assert.Equal(0, width);
        }

        [Fact]
        public void DenseBracketWidth_MissingSideOrNoPoints_False()
        {
            var section = Section(ReferenceFrame.Relative, 10, 30);
            Assert.False(ParsekFlightGhostPositioner.TryGetDenseSectionBracketWidthSeconds(
                null, -1, section, null, 20, out double width));
            Assert.True(double.IsNaN(width));
            Assert.False(ParsekFlightGhostPositioner.TryGetDenseSectionBracketWidthSeconds(
                null, -1, section, Pts(5, 12, 18, 35), 20, out width));
            Assert.True(double.IsNaN(width));
        }

        [Fact]
        public void BracketWidth_NeedsTwoPoints()
        {
            Assert.False(ParsekFlightGhostPositioner.TryGetBracketWidthSeconds(null, 1, out double w));
            Assert.True(double.IsNaN(w));
            Assert.False(ParsekFlightGhostPositioner.TryGetBracketWidthSeconds(Pts(1), 1, out w));
            Assert.True(ParsekFlightGhostPositioner.TryGetBracketWidthSeconds(Pts(0, 4, 10), 5, out w));
            Assert.Equal(6, w);
        }

        [Fact]
        public void IsRelativeSectionAtUT_ByContainingSectionFrame()
        {
            var rec = new Recording { RecordingId = "r" };
            Assert.False(ParsekFlightGhostPositioner.IsRelativeSectionAtUT(rec, 5));
            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 0, 10));
            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 10, 20));
            Assert.False(ParsekFlightGhostPositioner.IsRelativeSectionAtUT(rec, 5));
            Assert.True(ParsekFlightGhostPositioner.IsRelativeSectionAtUT(rec, 15));
            Assert.False(ParsekFlightGhostPositioner.IsRelativeSectionAtUT(rec, 25));
            Assert.False(ParsekFlightGhostPositioner.IsRelativeSectionAtUT(null, 15));
        }

        // --- point Hermite deviation and spline gate ---

        [Theory]
        [InlineData(0.0, 10.0)]
        [InlineData(100.0, 10.0)]
        [InlineData(1000.0, 50.0)]
        [InlineData(4000.0, 200.0)]
        [InlineData(100000.0, 250.0)]
        public void HermiteMaxDeviation_IsFivePercentOfChordClamped(double chord, double expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.ComputePointHermiteMaxDeviationMeters(
                new Vector3d(0, 0, 0), new Vector3d(chord, 0, 0)), 9);
        }

        [Fact]
        public void HermiteMaxDeviation_NonFiniteChordUsesTheFloor()
        {
            Assert.Equal(10.0, ParsekFlightGhostPositioner.ComputePointHermiteMaxDeviationMeters(
                new Vector3d(0, 0, 0), new Vector3d(double.NaN, 0, 0)));
            Assert.Equal(10.0, ParsekFlightGhostPositioner.ComputePointHermiteMaxDeviationMeters(
                new Vector3d(0, 0, 0), new Vector3d(double.PositiveInfinity, 0, 0)));
        }

        [Theory]
        [InlineData(true, true, false, "suppressed")]
        [InlineData(true, false, false, "suppressed")]
        [InlineData(false, false, false, "normal-playback-linearized")]
        [InlineData(false, true, true, "eligible")]
        public void SplinePositioningGate(bool suppress, bool allowNormal, bool expected, string reason)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.allowSplinePositioningForPlayback(
                suppress, allowNormal, out string actualReason));
            Assert.Equal(reason, actualReason);
        }

        // --- tail lift ---

        [Theory]
        [InlineData("recording-missing", true)]
        [InlineData("body-missing", true)]
        [InlineData("terminal-body-missing", true)]
        [InlineData("current-body-not-terminal", true)]
        [InlineData("current-terrain-nan", true)]
        [InlineData("no-points", false)]
        [InlineData("terminal-missing", false)]
        [InlineData("Body-Missing", false)]
        [InlineData(null, false)]
        public void TailLiftTransientReasons(string reason, bool expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.IsTransientTailLiftInactiveReason(reason));
        }

        private static Recording SurfaceEnd(TerminalState? terminal, double recordedTerrain)
        {
            var rec = new Recording { RecordingId = "r", TerminalStateValue = terminal, TerrainHeightAtEnd = recordedTerrain };
            rec.Points.Add(Pt(10, "Kerbin"));
            return rec;
        }

        [Fact]
        public void TailLiftReason_CheckOrder()
        {
            Assert.Equal("recording-missing", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(null, false, double.NaN));
            Assert.Equal("no-points", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                new Recording(), false, double.NaN));
            Assert.Equal("body-missing", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(null, double.NaN), false, double.NaN));
            Assert.Equal("terminal-missing", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(null, double.NaN), true, double.NaN));
            Assert.Equal("non-surface-terminal", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(TerminalState.Orbiting, double.NaN), true, double.NaN));
            Assert.Equal("recorded-terrain-nan", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(TerminalState.Landed, double.NaN), true, double.NaN));
            Assert.Equal("current-terrain-nan", ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(TerminalState.Landed, 100), true, double.NaN));
        }

        [Theory]
        [InlineData(TerminalState.Landed, 101.9, "delta-below-threshold")]
        [InlineData(TerminalState.Splashed, 98.1, "delta-below-threshold")]
        [InlineData(TerminalState.Recovered, 102.0, "inactive")]
        [InlineData(TerminalState.Landed, 90.0, "inactive")]
        public void TailLiftReason_DeltaThreshold(TerminalState terminal, double currentTerrain, string expected)
        {
            Assert.Equal(expected, ParsekFlightGhostPositioner.ResolveTailLiftInactiveReasonCore(
                SurfaceEnd(terminal, 100), true, currentTerrain));
        }

        [Fact]
        public void TailLiftTerminalBodyName_FallsBackToLastPointBody()
        {
            var empty = new Recording { RecordingId = "r" };
            Assert.Equal("Mun", ParsekFlightGhostPositioner.ResolveTailLiftTerminalBodyName(empty, Pt(1, "Mun")));
            Assert.Null(ParsekFlightGhostPositioner.ResolveTailLiftTerminalBodyName(empty, Pt(1, "")));
        }

        [Fact]
        public void TailLiftTerminalPointIsAbsolute_BySectionAtLastPoint()
        {
            var lastPt = Pt(15);
            Assert.True(ParsekFlightGhostPositioner.TailLiftTerminalPointIsAbsolute(null, lastPt));
            var rec = new Recording { RecordingId = "r" };
            Assert.True(ParsekFlightGhostPositioner.TailLiftTerminalPointIsAbsolute(rec, lastPt));
            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 0, 10));
            Assert.True(ParsekFlightGhostPositioner.TailLiftTerminalPointIsAbsolute(rec, lastPt));
            rec.TrackSections.Add(Section(ReferenceFrame.Relative, 10, 20));
            Assert.False(ParsekFlightGhostPositioner.TailLiftTerminalPointIsAbsolute(rec, lastPt));
            rec.TrackSections[1] = Section(ReferenceFrame.Absolute, 10, 20);
            Assert.True(ParsekFlightGhostPositioner.TailLiftTerminalPointIsAbsolute(rec, lastPt));
        }

        // --- Trace-Sep row helpers ---

        [Fact]
        public void TraceBracketFrames_LastAtOrBeforeAndFirstAtOrAfter()
        {
            var frames = Pts(0, 10, 10, 20);
            ParsekFlightGhostPositioner.FindTraceBracketFrames(frames, 10, out var before, out var after);
            Assert.Equal(10, before.Value.ut);
            Assert.Equal(10, after.Value.ut);

            ParsekFlightGhostPositioner.FindTraceBracketFrames(frames, 15, out before, out after);
            Assert.Equal(10, before.Value.ut);
            Assert.Equal(20, after.Value.ut);

            ParsekFlightGhostPositioner.FindTraceBracketFrames(frames, -1, out before, out after);
            Assert.False(before.HasValue);
            Assert.Equal(0, after.Value.ut);

            ParsekFlightGhostPositioner.FindTraceBracketFrames(frames, 21, out before, out after);
            Assert.Equal(20, before.Value.ut);
            Assert.False(after.HasValue);
        }

        [Fact]
        public void TraceBracketFrames_NullOrEmpty_BothNull()
        {
            ParsekFlightGhostPositioner.FindTraceBracketFrames(null, 1, out var before, out var after);
            Assert.False(before.HasValue || after.HasValue);
            ParsekFlightGhostPositioner.FindTraceBracketFrames(new List<TrajectoryPoint>(), 1, out before, out after);
            Assert.False(before.HasValue || after.HasValue);
        }

        [Fact]
        public void TraceBracketFrame_Format()
        {
            Assert.Equal("<none>", ParsekFlightGhostPositioner.FormatTraceBracketFrame(null));
            Assert.Equal("ut=12.5 lla=(-0.25,170,1234.5)",
                ParsekFlightGhostPositioner.FormatTraceBracketFrame(Pt(12.5, "Kerbin", -0.25, 170, 1234.5)));
        }

        [Fact]
        public void RecordedAnchorLocalDistanceBefore_StopsAtFirstLaterFrame()
        {
            var frames = new List<TrajectoryPoint>
            {
                Pt(0, lat: 3, lon: 4, alt: 0),
                Pt(10, lat: 0, lon: 6, alt: 8),
                Pt(20, lat: 100, lon: 0, alt: 0),
                Pt(5, lat: 1, lon: 0, alt: 0),
            };
            Assert.Equal(10, ParsekFlightGhostPositioner.ResolveRecordedAnchorLocalDistanceBefore(frames, 15), 9);
            Assert.Equal(5, ParsekFlightGhostPositioner.ResolveRecordedAnchorLocalDistanceBefore(frames, 9), 9);
            Assert.True(double.IsNaN(ParsekFlightGhostPositioner.ResolveRecordedAnchorLocalDistanceBefore(frames, -1)));
        }

        [Fact]
        public void LastBodyFixedFrameAtOrBefore_LaterSectionReplacesEarlier()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 10, bodyFixed: Pts(0, 5, 50)),
                Section(ReferenceFrame.Absolute, 10, 20),
                Section(ReferenceFrame.Relative, 10, 20, bodyFixed: Pts(12, 14)),
                Section(ReferenceFrame.Relative, 20, 30, bodyFixed: Pts(40)),
            };
            Assert.Equal(14, ParsekFlightGhostPositioner.FindLastBodyFixedFrameAtOrBefore(sections, 15).Value.ut);
            Assert.Equal(5, ParsekFlightGhostPositioner.FindLastBodyFixedFrameAtOrBefore(sections, 11).Value.ut);
            Assert.False(ParsekFlightGhostPositioner.FindLastBodyFixedFrameAtOrBefore(sections, -1).HasValue);
            Assert.Equal(40, ParsekFlightGhostPositioner.FindLastBodyFixedFrameAtOrBefore(sections, 45).Value.ut);
            var unordered = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 30, bodyFixed: Pts(0, 20, 5)),
            };
            Assert.Equal(0, ParsekFlightGhostPositioner.FindLastBodyFixedFrameAtOrBefore(unordered, 11).Value.ut);
        }

        [Fact]
        public void TraceDistance_Format()
        {
            Assert.Equal("NaN", ParsekFlightGhostPositioner.FormatTraceDistance(double.NaN));
            Assert.Equal("12.346", ParsekFlightGhostPositioner.FormatTraceDistance(12.3456));
        }

        // --- small formatters ---

        [Fact]
        public void StandaloneCoverageRange_Format()
        {
            Assert.Equal("(none)", ParsekFlightGhostPositioner.FormatStandaloneCoverageRange(double.NaN, 5));
            Assert.Equal("(none)", ParsekFlightGhostPositioner.FormatStandaloneCoverageRange(5, double.NaN));
            Assert.Equal("[1.5,20]", ParsekFlightGhostPositioner.FormatStandaloneCoverageRange(1.5, 20));
        }

        [Fact]
        public void FiniteOrNa_Format()
        {
            Assert.Equal("n/a", ParsekFlightGhostPositioner.FormatFiniteOrNa(double.NaN, "F1"));
            Assert.Equal("n/a", ParsekFlightGhostPositioner.FormatFiniteOrNa(double.NegativeInfinity, "F1"));
            Assert.Equal("2.5", ParsekFlightGhostPositioner.FormatFiniteOrNa(2.5, "F1"));
        }

        [Fact]
        public void ShortRecordingId_TruncatesToEight()
        {
            Assert.Equal("-", ParsekFlightGhostPositioner.ShortRecordingId(null));
            Assert.Equal("-", ParsekFlightGhostPositioner.ShortRecordingId(""));
            Assert.Equal("abc", ParsekFlightGhostPositioner.ShortRecordingId("abc"));
            Assert.Equal("12345678", ParsekFlightGhostPositioner.ShortRecordingId("123456789abc"));
        }

        [Fact]
        public void CountKeysAbove_StrictlyGreater()
        {
            var dict = new Dictionary<int, string> { { 1, "a" }, { 3, "b" }, { 5, "c" }, { 7, "d" } };
            Assert.Equal(2, ParsekFlightGhostPositioner.CountKeysAbove(dict, 3));
            Assert.Equal(4, ParsekFlightGhostPositioner.CountKeysAbove(dict, -1));
            Assert.Equal(0, ParsekFlightGhostPositioner.CountKeysAbove(dict, 7));
        }

        [Fact]
        public void RecordedRelativeOffsetLog_Format()
        {
            Assert.Equal(
                "RELATIVE playback: contract=anchor-local version=3 dx=3.00 dy=4.00 dz=0.00 |offset|=5.00m anchorRec=anc-1 source=recorded",
                ParsekFlightGhostPositioner.BuildRecordedRelativeOffsetAppliedLog(3, 3, 4, 0, "anc-1"));
            Assert.Contains("anchorRec=(missing)",
                ParsekFlightGhostPositioner.BuildRecordedRelativeOffsetAppliedLog(3, 0, 0, 0, null));
        }

        [Fact]
        public void RecordedRelativeOffset_ReadsLatLonAltAsMetres()
        {
            Vector3d offset = ParsekFlightGhostPositioner.RecordedRelativeOffset(Pt(1, null, 5, -6, 7));
            Assert.Equal(5, offset.x);
            Assert.Equal(-6, offset.y);
            Assert.Equal(7, offset.z);
        }

        // --- relative anchor focus tree ---

        [Fact]
        public void FocusTree_FindsCommittedThenPending()
        {
            Assert.False(ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree(null, out var tree));
            Assert.Null(tree);
            Assert.False(ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree("r1", out tree));

            var committed = new RecordingTree { Id = "c" };
            committed.Recordings["r1"] = new Recording { RecordingId = "r1" };
            RecordingStore.CommittedTrees.Add(committed);
            Assert.True(ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree("r1", out tree));
            Assert.Same(committed, tree);

            var pending = new RecordingTree { Id = "p" };
            pending.Recordings["r2"] = new Recording { RecordingId = "r2" };
            PendingTreeSlot.StashPendingTree(pending);
            Assert.True(ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree("r2", out tree));
            Assert.Same(pending, tree);
            Assert.False(ParsekFlightGhostPositioner.TryFindRelativeAnchorFocusTree("r3", out tree));
            Assert.Null(tree);
        }
    }
}
