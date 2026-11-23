using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A parent-anchored recording (a dropped booster, a decoupled core) falls along its own
    /// predicted tail past its last track section and ends there (owner rulings Q1a / Q2); the
    /// retirement of every other uncovered UT is unchanged. Contract:
    /// <see cref="GhostPlaybackEngine.IsInAcceptedPredictedTail"/>.
    /// </summary>
    [Collection("Sequential")]
    public class ParentAnchoredPredictedTailTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private readonly List<string> logLines = new List<string>();

        public ParentAnchoredPredictedTailTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- fixtures ----

        private static OrbitSegment Conic(
            double startUT, double endUT, bool predicted = true, double sma = 700000.0)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                semiMajorAxis = sma,
                eccentricity = 0.0,
                epoch = 0.0,
                isPredicted = predicted,
            };
        }

        private static TrajectoryPoint Pt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, latitude = 1.0, longitude = 2.0, altitude = 60000.0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
            };
        }

        private static TrackSection AbsoluteSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint> { Pt(startUT), Pt(endUT) },
            };
        }

        private static TrackSection RelativeSection(double startUT, double endUT, bool bodyFixed = true)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = startUT,
                endUT = endUT,
                anchorRecordingId = "parent-rec",
                frames = new List<TrajectoryPoint> { Pt(startUT), Pt(endUT) },
                bodyFixedFrames = bodyFixed
                    ? new List<TrajectoryPoint> { Pt(startUT), Pt(endUT) }
                    : null,
            };
        }

        /// <summary>
        /// Relative 100-110, Absolute 110-150 (last point 150), predicted tail 150-200, Destroyed.
        /// </summary>
        private static MockTrajectory DroppedStage(bool isDebris = true)
        {
            var traj = new MockTrajectory
            {
                RecordingId = "stage-rec",
                VesselName = "Kerbal X Probe",
                IsDebris = isDebris,
                ParentAnchorRecordingId = "parent-rec",
                TerminalStateValue = TerminalState.Destroyed,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = "Kerbin",
                EndUTOverride = 200.0,
            };
            traj.Points.Add(Pt(100.0));
            traj.Points.Add(Pt(150.0));
            traj.TrackSections.Add(RelativeSection(100.0, 110.0));
            traj.TrackSections.Add(AbsoluteSection(110.0, 150.0));
            traj.OrbitSegments.Add(Conic(150.0, 200.0));
            return traj;
        }

        private static bool Retires(IPlaybackTrajectory traj, double ut, out string reason)
        {
            bool retires = GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, ut, out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic);
            reason = diagnostic.Reason;
            return retires;
        }

        // The retirement and the dispatch must agree: a UT that is not retired past the last
        // section is one the orbit-tail dispatch positions, never the flat points.
        private static void AssertDrawnByTheTail(IPlaybackTrajectory traj, double ut)
        {
            Assert.False(Retires(traj, ut, out string reason));
            Assert.Equal(DebrisRelativePlaybackPolicy.CoveredByPredictedTailReason, reason);
            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, ut));
            Assert.False(GhostPlaybackEngine.AuthoredFrameGapHasShadowCoverage(traj, ut));
        }

        // ---- the coverage decision ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PredictedTailPastTheLastSection_IsCoverage_DebrisAndControlledChild(bool isDebris)
        {
            MockTrajectory traj = DroppedStage(isDebris);

            AssertDrawnByTheTail(traj, 150.01);
            AssertDrawnByTheTail(traj, 175.0);
            AssertDrawnByTheTail(traj, 200.0);
        }

        [Theory]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Orbiting)]
        public void PredictedTail_IsCoverage_WhateverTheEnding(TerminalState terminal)
        {
            MockTrajectory traj = DroppedStage();
            traj.TerminalStateValue = terminal;

            AssertDrawnByTheTail(traj, 175.0);
        }

        [Fact]
        public void InsideTheSections_TheSectionReasonsAreUnchanged()
        {
            MockTrajectory traj = DroppedStage();

            Assert.False(Retires(traj, 105.0, out string relative));
            Assert.Equal("covered-by-body-fixed-primary", relative);
            Assert.False(Retires(traj, 130.0, out string absolute));
            Assert.Equal("non-relative-section", absolute);
            Assert.False(Retires(traj, 150.0, out string lastInstant));
            Assert.Equal("non-relative-section", lastInstant);
        }

        [Fact]
        public void PastTheTailEnd_Retires()
        {
            MockTrajectory traj = DroppedStage();

            Assert.True(Retires(traj, 200.001, out string reason));
            Assert.Equal("no-covering-section", reason);
        }

        [Fact]
        public void BeforeTheFirstSection_Retires_EvenInsideAPredictedSegment()
        {
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments[0] = Conic(90.0, 200.0);

            Assert.True(Retires(traj, 95.0, out string reason));
            Assert.Equal("no-covering-section", reason);
        }

        [Fact]
        public void GapBetweenSections_Retires_EvenInsideAPredictedSegment()
        {
            MockTrajectory traj = DroppedStage();
            traj.TrackSections[0] = RelativeSection(100.0, 108.0);
            traj.OrbitSegments[0] = Conic(90.0, 200.0);

            Assert.True(Retires(traj, 109.0, out string reason));
            Assert.Equal("no-covering-section", reason);
        }

        [Fact]
        public void GapBeforeALaterCheckpointSection_Retires_ThoughTheTailDispatchWouldTakeIt()
        {
            // The last flat point precedes the gap, so only the section order says the tail has
            // not begun: an on-rails span after the gap is still recorded.
            MockTrajectory traj = DroppedStage();
            traj.Points[1] = Pt(110.0);
            traj.TrackSections[1] = new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 112.0,
                endUT = 150.0,
            };
            traj.OrbitSegments[0] = Conic(110.0, 200.0);

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 111.0));
            Assert.True(Retires(traj, 111.0, out string reason));
            Assert.Equal("no-covering-section", reason);
            Assert.False(Retires(traj, 130.0, out string checkpoint));
            Assert.Equal("non-relative-section", checkpoint);
            AssertDrawnByTheTail(traj, 175.0);
        }

        [Fact]
        public void UncoveredRelativeSection_Retires_EvenInsideAPredictedSegment()
        {
            // Relative metadata outliving its frames is not coverage, with or without a tail.
            MockTrajectory traj = DroppedStage();
            traj.TrackSections.Clear();
            TrackSection section = RelativeSection(100.0, 110.0);
            section.endUT = 160.0;
            traj.TrackSections.Add(section);
            traj.Points[1] = Pt(110.0);
            traj.OrbitSegments[0] = Conic(110.0, 200.0);

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 140.0));
            Assert.True(Retires(traj, 140.0, out string reason));
            Assert.Equal("relative-and-body-fixed-frames-out-of-range", reason);
            AssertDrawnByTheTail(traj, 160.5);
        }

        [Fact]
        public void RelativeLastSection_TheTailStartsWhereItsFramesEnd()
        {
            MockTrajectory traj = DroppedStage();
            traj.TrackSections.RemoveAt(1);
            traj.Points[1] = Pt(110.0);
            traj.OrbitSegments[0] = Conic(110.0, 200.0);

            Assert.False(Retires(traj, 110.0, out string inside));
            Assert.Equal("covered-by-body-fixed-primary", inside);
            AssertDrawnByTheTail(traj, 110.01);
        }

        [Fact]
        public void NonPredictedOrbitSegmentPastTheSections_Retires()
        {
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments[0] = Conic(150.0, 200.0, predicted: false);

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 175.0));
            Assert.True(Retires(traj, 175.0, out string reason));
            Assert.Equal("no-covering-section", reason);
        }

        [Fact]
        public void RecordedSegmentThenPredictedTail_OnlyThePredictedPartIsCoverage()
        {
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments.Clear();
            traj.OrbitSegments.Add(Conic(150.0, 170.0, predicted: false));
            traj.OrbitSegments.Add(Conic(170.0, 200.0));

            Assert.True(Retires(traj, 160.0, out _));
            AssertDrawnByTheTail(traj, 170.0);
            AssertDrawnByTheTail(traj, 190.0);
        }

        [Fact]
        public void UnusablePredictedElements_Retire()
        {
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments[0] = Conic(150.0, 200.0, sma: 0.0);

            Assert.True(Retires(traj, 175.0, out string reason));
            Assert.Equal("no-covering-section", reason);
        }

        [Fact]
        public void SmallGapBeforeThePredictedSegment_IsBridged()
        {
            // The GS-8 shape: last point 298.288, section end 298.42, predicted start 298.428.
            MockTrajectory traj = DroppedStage();
            traj.TrackSections[1] = AbsoluteSection(110.0, 150.13);
            traj.OrbitSegments[0] = Conic(150.138, 200.0);

            Assert.False(Retires(traj, 150.12, out string inside));
            Assert.Equal("non-relative-section", inside);
            AssertDrawnByTheTail(traj, 150.134);
            AssertDrawnByTheTail(traj, 150.14);
        }

        [Fact]
        public void LongGapBeforeThePredictedSegment_Slides_FromAPointASectionHolds()
        {
            // Owner ruling L5.
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments[0] = Conic(190.0, 250.0);
            traj.EndUTOverride = 250.0;

            AssertDrawnByTheTail(traj, 170.0);
            AssertDrawnByTheTail(traj, 220.0);
        }

        [Fact]
        public void LongGap_LastPointInNoSection_Retires()
        {
            // No section holds the last flat point, so its frame is unknown: no slide from it.
            MockTrajectory traj = DroppedStage();
            traj.TrackSections[1] = AbsoluteSection(110.0, 149.0);
            traj.OrbitSegments[0] = Conic(190.0, 250.0);

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 170.0));
            Assert.True(Retires(traj, 170.0, out string reason));
            Assert.Equal("no-covering-section", reason);
            AssertDrawnByTheTail(traj, 220.0);
        }

        [Fact]
        public void EmptyParentId_StaysRetired_InsideTheTail()
        {
            MockTrajectory traj = DroppedStage();
            traj.ParentAnchorRecordingId = string.Empty;

            Assert.True(Retires(traj, 175.0, out string reason));
            Assert.Equal("parent-recording-id-empty", reason);
        }

        [Fact]
        public void NotParentAnchored_IsUntouched()
        {
            MockTrajectory traj = DroppedStage();
            traj.ParentAnchorRecordingId = null;

            Assert.False(Retires(traj, 175.0, out string reason));
            Assert.Null(reason);
            Assert.False(Retires(traj, 250.0, out _));
        }

        [Fact]
        public void LiveLoopAnchor_IsUntouched()
        {
            MockTrajectory traj = DroppedStage();
            traj.LoopAnchorVesselId = 77u;

            Assert.False(Retires(traj, 175.0, out string reason));
            Assert.Null(reason);
            Assert.False(Retires(traj, 250.0, out _));
        }

        [Fact]
        public void NoTrackSections_TailIsCoverage_RestRetires()
        {
            MockTrajectory traj = DroppedStage();
            traj.TrackSections.Clear();

            AssertDrawnByTheTail(traj, 175.0);
            Assert.True(Retires(traj, 125.0, out string reason));
            Assert.Equal("no-track-sections", reason);
        }

        [Fact]
        public void NoPoints_TailIsCoverage_UnlessASurfacePoseWouldWin()
        {
            MockTrajectory traj = DroppedStage();
            traj.Points.Clear();

            Assert.False(Retires(traj, 175.0, out string reason));
            Assert.Equal(DebrisRelativePlaybackPolicy.CoveredByPredictedTailReason, reason);
            Assert.True(Retires(traj, 200.5, out _));

            traj.SurfacePos = new SurfacePosition { body = "Kerbin" };
            Assert.True(Retires(traj, 175.0, out _));

            traj.SurfacePos = null;
            traj.OrbitSegments[0] = Conic(150.0, 200.0, predicted: false);
            Assert.True(Retires(traj, 175.0, out _));
        }

        [Fact]
        public void NaNUT_Retires()
        {
            Assert.True(Retires(DroppedStage(), double.NaN, out _));
        }

        [Fact]
        public void SectionPolicyAlone_StillReportsTheMiss()
        {
            // The policy answers for the sections; the engine adds the tail. KSC and the map
            // ask the policy only with a covering Relative section in hand.
            Assert.True(DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                DroppedStage(), 175.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal(DebrisRelativePlaybackPolicy.NoCoveringSectionReason, diagnostic.Reason);
            Assert.True(DebrisRelativePlaybackPolicy.IsSectionMissAPredictedTailMayCover(diagnostic));
        }

        [Fact]
        public void ResolveTrackSectionsEndUT_IsTheMaximum()
        {
            MockTrajectory traj = DroppedStage();
            Assert.Equal(150.0, DebrisRelativePlaybackPolicy.ResolveTrackSectionsEndUT(traj));
            traj.TrackSections.Insert(0, AbsoluteSection(0.0, 160.0));
            Assert.Equal(160.0, DebrisRelativePlaybackPolicy.ResolveTrackSectionsEndUT(traj));
            traj.TrackSections.Clear();
            Assert.Equal(double.NegativeInfinity, DebrisRelativePlaybackPolicy.ResolveTrackSectionsEndUT(traj));
            Assert.Equal(double.NegativeInfinity, DebrisRelativePlaybackPolicy.ResolveTrackSectionsEndUT(null));
        }

        // ---- the positioning paths ----

        [Fact]
        public void LoadedPosition_InTheTail_PositionsFromOrbit()
        {
            var positioner = new RecordingPositioner();
            var state = LoadedState();

            Invoke("PositionLoadedGhostAtPlaybackUT", new GhostPlaybackEngine(positioner),
                3, DroppedStage(), state, 175.0);

            Assert.Equal(new[] { "orbit@175" }, positioner.Calls);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.DoesNotContain(logLines, l => l.Contains("recorded-relative-retired"));
        }

        [Fact]
        public void LoopPosition_InTheTail_PositionsFromOrbit_PastItRetires()
        {
            var positioner = new RecordingPositioner();
            var engine = new GhostPlaybackEngine(positioner);

            var inTail = new GhostPlaybackState { vesselName = "Kerbal X Probe" };
            Invoke("PositionLoopAtPlaybackUT", engine, 3, DroppedStage(), inTail, 175.0, true, "test-loop");
            Assert.Equal(new[] { "orbit@175" }, positioner.Calls);
            Assert.False(inTail.anchorRetiredThisFrame);

            var past = new GhostPlaybackState { vesselName = "Kerbal X Probe" };
            Invoke("PositionLoopAtPlaybackUT", engine, 3, DroppedStage(), past, 201.0, true, "test-loop");
            Assert.Equal(new[] { "orbit@175" }, positioner.Calls);
            Assert.True(past.anchorRetiredThisFrame);
            Assert.Contains(logLines, l => l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("coverageReason=no-covering-section")
                && l.Contains("callsite=test-loop"));
        }

        [Fact]
        public void PreSpawnAndWatchGate_InTheTail_DoesNotRetireOrExitWatch()
        {
            var engine = new GhostPlaybackEngine(new RecordingPositioner());
            int exits = 0;
            engine.OnLoopCameraAction += evt => exits++;
            var state = new GhostPlaybackState { vesselName = "Kerbal X Probe" };
            object[] args =
            {
                3, DroppedStage(isDebris: false), state, 175.0, 175.0, 1f, "test-pre-spawn", true, false
            };

            bool handled = (bool)Method("TryHandleParentAnchoredDebrisCoverageRetired").Invoke(engine, args);

            Assert.False(handled);
            Assert.Equal(0, exits);
            Assert.False(state.anchorRetiredThisFrame);

            args[3] = 201.0;
            Assert.True((bool)Method("TryHandleParentAnchoredDebrisCoverageRetired").Invoke(engine, args));
            Assert.Equal(1, exits);
            Assert.True(state.anchorRetiredThisFrame);
        }

        // ---- the endpoint ----

        [Fact]
        public void Endpoint_AtTheTailEnd_IsPositionedThere_AndNotACoverageMiss()
        {
            MockTrajectory traj = DroppedStage();
            var positioner = new RecordingPositioner();
            var state = LoadedState();

            Assert.Equal(200.0, GhostPlaybackEngine.ResolveRecordingEndpointCoverageUT(traj));
            Assert.False(GhostPlaybackEngine.ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(traj));
            try
            {
                Invoke("PositionGhostAtRecordingEndpoint", new GhostPlaybackEngine(positioner), 3, traj, state);
            }
            catch (TargetInvocationException)
            {
                // After the pose the endpoint reads FlightGlobals, which no test host can load.
            }

            Assert.Equal(new[] { "orbit@200" }, positioner.Calls);
            Assert.False(state.anchorRetiredThisFrame);
            // The explosion decision HandlePastEndGhost reaches once the endpoint is not retired.
            Assert.True(GhostPlaybackLogic.ShouldTriggerExplosion(
                false, traj.TerminalStateValue, true, traj.VesselName, 3));
            Assert.False(GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(traj, out _));
        }

        [Fact]
        public void Endpoint_OnARecordedSegmentPastTheSections_StaysACoverageMiss()
        {
            MockTrajectory traj = DroppedStage();
            traj.OrbitSegments[0] = Conic(150.0, 200.0, predicted: false);

            Assert.Equal(200.0, GhostPlaybackEngine.ResolveRecordingEndpointCoverageUT(traj));
            Assert.True(GhostPlaybackEngine.ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(traj));
        }

        [Fact]
        public void HandlePastEndGhost_ExplodesOnlyAfterTheEndpointCoverageCheck()
        {
            // Source gate (live-only path): the retired endpoint returns before the explosion,
            // and the explosion follows the endpoint pose.
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Ghost", "GhostPlaybackEngine.Completion.cs"));
            int start = source.IndexOf("private void HandlePastEndGhost(", StringComparison.Ordinal);
            int pose = source.IndexOf("PositionGhostAtRecordingEndpoint(i, traj, state);", start, StringComparison.Ordinal);
            int retired = source.IndexOf("if (endpointRetired)", pose, StringComparison.Ordinal);
            int explode = source.IndexOf("TriggerExplosionIfDestroyed(state, traj, i, ctx.warpRate);", retired, StringComparison.Ordinal);
            Assert.True(start >= 0 && pose > start && retired > pose && explode > retired);
        }

        // ---- the drawn-tail line ----

        [Fact]
        public void DrawnTail_LogsOneLinePerRecording()
        {
            var state = new GhostPlaybackState { vesselName = "Kerbal X Probe" };
            MethodInfo log = typeof(GhostPlaybackEngine).GetMethod(
                "LogParentAnchoredPredictedTailDrawn", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(log);

            log.Invoke(null, new object[] { 9, DroppedStage(), state, 149.0 });
            Assert.DoesNotContain(logLines, l => l.Contains("parent-anchored predicted tail"));
            MockTrajectory topLevel = DroppedStage();
            topLevel.ParentAnchorRecordingId = null;
            log.Invoke(null, new object[] { 9, topLevel, state, 175.0 });
            Assert.DoesNotContain(logLines, l => l.Contains("parent-anchored predicted tail"));

            log.Invoke(null, new object[] { 9, DroppedStage(), state, 175.0 });
            log.Invoke(null, new object[] { 9, DroppedStage(), state, 175.02 });
            Assert.Single(logLines, l => l.Contains("[Anchor]")
                && l.Contains("parent-anchored predicted tail: recording=#9 vessel=\"Kerbal X Probe\" "
                    + "recordingId=stage-rec active=F playbackUT=175.00 sectionsEndUT=150.00 endUT=200.00"));
        }

        [Fact]
        public void DrawnTailMessage_NamesTheActiveGhost()
        {
            Assert.Contains("active=T playbackUT=175.00",
                GhostPlaybackEngine.BuildParentAnchoredPredictedTailDrawnMessage(
                    9, DroppedStage(), 175.0, active: true));
        }

        [Fact]
        public void RenderInRange_LogsTheDrawnTailAfterActivation()
        {
            // Source gate (live-only path): the line is a witness of a drawn frame.
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Ghost", "GhostPlaybackEngine.InRangeRender.cs"));
            int post = source.IndexOf("ApplyNonRetiredPostPosition(\r\n", StringComparison.Ordinal);
            if (post < 0)
                post = source.IndexOf("ApplyNonRetiredPostPosition(\n", StringComparison.Ordinal);
            int line = source.IndexOf("LogParentAnchoredPredictedTailDrawn(i, traj, state, visiblePlaybackUT);",
                StringComparison.Ordinal);
            Assert.True(post >= 0 && line > post);
        }

        // ---- the continuity anchor ----

        [Fact]
        public void ContinuityAnchor_FlatPointPastARelativeSection_NeverForAParentAnchoredRecording()
        {
            MockTrajectory child = DroppedStage();
            MockTrajectory topLevel = DroppedStage();
            topLevel.ParentAnchorRecordingId = null;
            TrackSection relative = RelativeSection(100.0, 110.0);
            TrackSection absolute = AbsoluteSection(110.0, 150.0);

            Assert.False(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(child, relative, true));
            Assert.False(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(child, relative, false));
            Assert.False(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(topLevel, relative, true));
            Assert.True(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(topLevel, relative, false));
            Assert.True(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(child, absolute, true));
            Assert.True(ParsekFlightGhostPositioner.MayAnchorContinuityOnFlatPoint(child, absolute, false));
        }

        // ---- KSC and the map, which dispatch the tail before any coverage question ----

        private static Recording DroppedStageRecording(bool isDebris)
        {
            MockTrajectory shape = DroppedStage(isDebris);
            var rec = new Recording
            {
                RecordingId = "stage-rec",
                VesselName = "Kerbal X Probe",
                IsDebris = isDebris,
                ParentAnchorRecordingId = "parent-rec",
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 200.0,
            };
            rec.Points.AddRange(shape.Points);
            rec.TrackSections.AddRange(shape.TrackSections);
            rec.OrbitSegments.AddRange(shape.OrbitSegments);
            return rec;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Ksc_ParentAnchoredTail_RidesTheOrbitTail(bool isDebris)
        {
            var bodyPos = new Vector3d(1.0e6, 2.0e6, 3.0e6);
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                DroppedStageRecording(isDebris), ref cachedIndex, ref cachedKey, 190.0,
                (string body, double lat, double lon, double alt, out Vector3d pos, out Quaternion rot) =>
                {
                    rot = Quaternion.identity;
                    pos = new Vector3d(lat * 100.0, lon * 100.0, alt);
                    return true;
                },
                (Recording r, TrackSection s, int i, double ut, out ParsekKSC.KscAnchorFrame anchor) =>
                {
                    anchor = default(ParsekKSC.KscAnchorFrame);
                    return false;
                },
                out ParsekKSC.KscPoseResolution pose,
                (string body, out Vector3d pos, out double mu) =>
                {
                    pos = bodyPos;
                    mu = KerbinMu;
                    return true;
                });

            Assert.True(resolved);
            Assert.Equal(ParsekKSC.KscOrbitTailBranch, pose.Branch);
            Assert.Equal(700000.0, (pose.WorldPos - bodyPos).magnitude, 0);
        }

        [Fact]
        public void Map_ParentAnchoredTail_IsInsideTheMarkerWindow_PastItIsNot()
        {
            Recording rec = DroppedStageRecording(isDebris: false);

            Assert.True(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 175.0));
            Assert.False(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 201.0));
            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.None,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 175.0, suppressedIds: null));
        }

        [Fact]
        public void Map_BodyFixedPrimaryMiss_InsideARelativeSection_KeepsItsReason()
        {
            Recording rec = DroppedStageRecording(isDebris: true);
            rec.TrackSections[0] = RelativeSection(100.0, 110.0, bodyFixed: false);
            int cachedIndex = 0;

            Assert.False(GhostMapPresence.TryResolveStateVectorMapPointPure(
                rec, 105.0, ref cachedIndex, out _, out string skipReason));
            Assert.Equal("relative-only-without-body-fixed-primary", skipReason);
        }

        // ---- helpers ----

        private static MethodInfo Method(string name)
        {
            MethodInfo method = typeof(GhostPlaybackEngine).GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method;
        }

        private static void Invoke(string name, GhostPlaybackEngine engine, params object[] args)
        {
            Method(name).Invoke(engine, args);
        }

        private static GameObject LoadedGhost()
        {
            var ghost = (GameObject)FormatterServices.GetUninitializedObject(typeof(GameObject));
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            cachedPtr?.SetValue(ghost, new IntPtr(1));
            return ghost;
        }

        private static GhostPlaybackState LoadedState()
        {
            return new GhostPlaybackState { vesselName = "Kerbal X Probe", ghost = LoadedGhost() };
        }

        private sealed class RecordingPositioner : IGhostPositioner
        {
            internal readonly List<string> Calls = new List<string>();

            private void Add(string kind, double ut)
            {
                Calls.Add(kind + "@" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx) => Add("points", ut);

            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx,
                RelativeSectionPlaybackTarget target) => Add("relative", ut);

            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target,
                out double bracketBeforeUT, out double bracketAfterUT)
            {
                bracketBeforeUT = bracketAfterUT = double.NaN;
                Add("body-fixed", playbackUT);
                return false;
            }

            public void PositionAtPoint(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, TrajectoryPoint point) => Add("point", point.ut);

            public void PositionAtSurface(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state) => Add("surface", 0.0);

            public void PositionFromOrbit(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut) => Add("orbit", ut);

            public void PositionLoop(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx) => Add("loop", ut);

            public bool TryResolveExplosionAnchorPosition(int index,
                IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                worldPosition = Vector3.zero;
                return true;
            }

            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
            {
                return new ZoneRenderingResult();
            }
        }
    }
}
