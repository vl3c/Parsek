using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-07 (todo OPTIMIZER-PAD-SPLIT-AT-FIVE-SECOND-THRESHOLD): the
    /// optimizer never splits off the LEADING launch-site Surface run of a flight that starts
    /// on the pad or runway. Whether a pad run became its own recording used to depend on
    /// whether ignition-to-liftoff reached the 5.0 s both-halves floor
    /// (`2026-10-07_0032_L5-career-contract-complete`: 5.04 s, split; eleven earlier L3 / L5
    /// flights: 4.48-4.70 s, no split). Touchdown splits and every later Surface boundary
    /// stay as they were, a landed start that is not a launch site keeps its split, and the
    /// seam short-circuit still wins.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerLaunchSiteLeadingRunTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OptimizerLaunchSiteLeadingRunTests()
        {
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekScenario.ResetInstanceForTesting();
        }

        private void EnableLogCapture()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        // Sections run back to back from startUT; each carries two frames on its body so the
        // optimizer's body and duration reads see a real payload.
        private static Recording MakeRecording(
            string recordingId,
            double startUT,
            params (SegmentEnvironment env, double duration, string body, bool isSeam)[] sections)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-" + recordingId,
                VesselName = "Jumping Flea",
                MergeState = MergeState.Immutable,
            };
            double cur = startUT;
            for (int i = 0; i < sections.Length; i++)
            {
                var (env, duration, body, isSeam) = sections[i];
                double sectionStart = cur;
                double sectionEnd = cur + duration;
                rec.Points.Add(new TrajectoryPoint { ut = sectionStart, altitude = 70, bodyName = body });
                rec.Points.Add(new TrajectoryPoint { ut = sectionEnd, altitude = 70, bodyName = body });
                rec.TrackSections.Add(new TrackSection
                {
                    environment = env,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = sectionStart,
                    endUT = sectionEnd,
                    sampleRateHz = 10f,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = sectionStart, altitude = 70, bodyName = body },
                        new TrajectoryPoint { ut = sectionEnd, altitude = 70, bodyName = body },
                    },
                    checkpoints = new List<OrbitSegment>(),
                    isBoundarySeam = isSeam,
                });
                cur = sectionEnd;
            }
            return rec;
        }

        // A launch start the way FlightRecorder.CaptureStartLocation stamps one on the pad.
        private static Recording AsPadLaunch(Recording rec)
        {
            rec.StartBodyName = "Kerbin";
            rec.StartSituation = "Prelaunch";
            rec.LaunchSiteName = "Launch Pad";
            return rec;
        }

        private static List<Recording> Single(Recording rec) => new List<Recording> { rec };

        private static void AssertKept(
            Recording rec, int s, RecordingOptimizer.SplitBoundaryReason expected)
        {
            RecordingOptimizer.SplitBoundaryReason reason;
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, s, out reason);
            Assert.False(splittable, $"boundary {s} of {rec.RecordingId} should not split");
            Assert.Equal(expected, reason);
        }

        private static void AssertSplits(Recording rec, int s)
        {
            RecordingOptimizer.SplitBoundaryReason reason;
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, s, out reason);
            Assert.True(splittable, $"boundary {s} of {rec.RecordingId} should split (reason={reason})");
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SurfaceInvolved, reason);
        }

        private const RecordingOptimizer.SplitBoundaryReason LaunchSiteKept =
            RecordingOptimizer.SplitBoundaryReason.SuppressedLaunchSiteLeadingRun;

        // The L5 `2026-10-07_0032` shape: recording from the first staging on the pad
        // (UT 9.60), the Flea starts moving at 13.50, its Atmospheric section opens at
        // 14.64 - a 5.04 s pad run - then the flight and a landed tail before recovery.
        private static Recording MakeL5Shape()
        {
            return AsPadLaunch(MakeRecording("l5-pad-5s", 9.60,
                (SegmentEnvironment.SurfaceStationary, 3.90, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 1.14, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 327.36, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 6.40, "Kerbin", false)));
        }

        [Fact]
        public void PadLaunch_PadRunOverFiveSeconds_IsNotSplitAtLiftoff()
        {
            var rec = MakeL5Shape();

            // The leading pad run of a pad launch is not split off at liftoff.
            AssertKept(rec, 2, LaunchSiteKept);

            // The first candidate is the touchdown, which still splits.
            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec));
            Assert.Single(candidates);
            Assert.Equal((0, 3), candidates[0]);
        }

        [Fact]
        public void PadLaunch_RunOptimizationPass_KeepsPadWithTheFlight_AndSplitsTheLandedTail()
        {
            var rec = MakeL5Shape();
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            var committed = RecordingStore.CommittedRecordings;
            Assert.Equal(2, committed.Count);
            var head = committed.Single(r => r.RecordingId == "l5-pad-5s");
            var tail = committed.Single(r => r.RecordingId != "l5-pad-5s");
            Assert.Equal(9.60, head.StartUT, 6);
            Assert.Equal(342.0, head.EndUT, 6);
            Assert.Equal("Launch Pad", head.LaunchSiteName);
            Assert.Equal(342.0, tail.StartUT, 6);
            Assert.Equal("surface", tail.SegmentPhase);
            Assert.Null(tail.LaunchSiteName);
        }

        [Fact]
        public void PadLaunch_LongPadRun_FirstCandidateIsTheAtmosphereExit()
        {
            var rec = AsPadLaunch(MakeRecording("pad-long-ascent", 17000,
                (SegmentEnvironment.SurfaceStationary, 30, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),
                (SegmentEnvironment.ExoBallistic, 600, "Kerbin", false)));

            AssertKept(rec, 1, LaunchSiteKept);

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec));
            Assert.Single(candidates);
            Assert.Equal((0, 2), candidates[0]);
        }

        [Fact]
        public void PadLaunch_AscentOrbitReentryLanding_ProducesFourSegmentsWithNoPadSegment()
        {
            var rec = AsPadLaunch(MakeRecording("pad-ascent-reentry", 17000,
                (SegmentEnvironment.SurfaceStationary, 30, "Kerbin", false), // pad
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),       // ascent
                (SegmentEnvironment.ExoBallistic, 1800, "Kerbin", false),     // orbit
                (SegmentEnvironment.Atmospheric, 200, "Kerbin", false),       // reentry
                (SegmentEnvironment.SurfaceMobile, 60, "Kerbin", false)));    // landing
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.RunOptimizationPass();

            var starts = RecordingStore.CommittedRecordings
                .Select(r => r.StartUT).OrderBy(u => u).ToArray();
            Assert.Equal(new[] { 17000.0, 17330.0, 19130.0, 19330.0 }, starts);
        }

        [Fact]
        public void PadLaunch_TouchdownAndLaterTakeoff_StillSplit()
        {
            // Pad launch, hop to a field, land, take off again, land again. Only the
            // leading pad run is kept; the touchdown, the second take-off (from a landed
            // tail that carries no launch-site start) and the second touchdown all split.
            var rec = AsPadLaunch(MakeRecording("pad-hop-twice", 17000,
                (SegmentEnvironment.SurfaceStationary, 10, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),
                (SegmentEnvironment.SurfaceStationary, 60, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 60, "Kerbin", false)));

            AssertKept(rec, 1, LaunchSiteKept);
            AssertSplits(rec, 2);
            AssertSplits(rec, 3);

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.RunOptimizationPass();

            var starts = RecordingStore.CommittedRecordings
                .Select(r => r.StartUT).OrderBy(u => u).ToArray();
            Assert.Equal(new[] { 17000.0, 17310.0, 17370.0, 17670.0 }, starts);
        }

        [Fact]
        public void RunwayStart_LandedAtTheRunway_LeadingRunNotSplit()
        {
            var rec = MakeRecording("runway-takeoff", 17000,
                (SegmentEnvironment.SurfaceStationary, 8, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 40, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 600, "Kerbin", false));
            rec.StartBodyName = "Kerbin";
            rec.StartSituation = "Landed";
            rec.LaunchSiteName = "Runway";

            AssertKept(rec, 2, LaunchSiteKept);
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec)));
        }

        [Fact]
        public void PrelaunchStartWithoutASiteName_LeadingRunNotSplit()
        {
            // A promoted / continued recording never carries a site name, but a vessel
            // still PRELAUNCH is by definition standing on a launch site.
            var rec = MakeRecording("prelaunch-no-site", 17000,
                (SegmentEnvironment.SurfaceStationary, 12, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false));
            rec.StartBodyName = "Kerbin";
            rec.StartSituation = "PRELAUNCH";

            AssertKept(rec, 1, LaunchSiteKept);
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec)));
        }

        [Fact]
        public void LandedStartAwayFromALaunchSite_LeadingRunStillSplits()
        {
            // Mirror direction: a Mun lander lifting off. No launch-site start, so its
            // landed stay is a phase of its own, exactly as before the ruling.
            var rec = MakeRecording("mun-lander-takeoff", 17000,
                (SegmentEnvironment.SurfaceStationary, 600, "Mun", false),
                (SegmentEnvironment.Approach, 60, "Mun", false),
                (SegmentEnvironment.ExoBallistic, 600, "Mun", false));
            rec.StartBodyName = "Mun";
            rec.StartSituation = "Landed";

            AssertSplits(rec, 1);
            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec));
            Assert.Equal((0, 1), candidates[0]);
        }

        [Fact]
        public void LandedKerbinStartWithNoLaunchSite_LeadingRunStillSplits()
        {
            var rec = MakeRecording("kerbin-field-takeoff", 17000,
                (SegmentEnvironment.SurfaceStationary, 30, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false));
            rec.StartBodyName = "Kerbin";
            rec.StartSituation = "Landed";

            AssertSplits(rec, 1);
            Assert.Single(RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec)));
        }

        [Fact]
        public void InheritedPadIdentityOnAnotherBody_LeadingRunStillSplits()
        {
            // A Re-Fly fork copies its origin's start fields (RewindInvoker
            // .CopyInheritedIdentityForFork), so a fork whose rewind point sits on the Mun
            // can carry the origin's "Launch Pad" / Kerbin. Its leading run is on the Mun:
            // not a launch site, so the take-off still splits.
            var rec = AsPadLaunch(MakeRecording("fork-on-mun", 17000,
                (SegmentEnvironment.SurfaceStationary, 600, "Mun", false),
                (SegmentEnvironment.Approach, 60, "Mun", false),
                (SegmentEnvironment.ExoBallistic, 600, "Mun", false)));

            AssertSplits(rec, 1);
        }

        [Fact]
        public void SeamShortCircuit_StillWinsOverTheLaunchSiteRule()
        {
            var rec = AsPadLaunch(MakeRecording("pad-seam", 17000,
                (SegmentEnvironment.SurfaceStationary, 30, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", true)));

            AssertKept(rec, 1, RecordingOptimizer.SplitBoundaryReason.SuppressedBoundarySeam);
        }

        [Fact]
        public void PadHop_SurfaceGrazeKeepsItsOwnReasons()
        {
            // A pad hop that lands back within 120 s is a surface graze: the graze rule runs
            // first and keeps both boundaries under its own reasons and counters (RF-1 pins
            // `surfaceGrazeForward=1 surfaceGrazeBackward=1 ... splittableButRejected=0`).
            var rec = AsPadLaunch(MakeRecording("pad-hop-graze", 17000,
                (SegmentEnvironment.SurfaceStationary, 10, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 60, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 60, "Kerbin", false)));

            AssertKept(rec, 1, RecordingOptimizer.SplitBoundaryReason.SuppressedSurfaceGrazeForward);
            AssertKept(rec, 2, RecordingOptimizer.SplitBoundaryReason.SuppressedSurfaceGrazeBackward);
            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec)));
        }

        [Fact]
        public void PadLaunch_BriefBounceBeforeLiftoff_TheRealLiftoffIsStillTheLeadingRun()
        {
            // A bounce on the pad (a brief Atmospheric run bracketed by Surface) is a surface
            // graze, so the launch-site stay runs on through it; the real liftoff after it is
            // still the departure from the leading run and is kept, the touchdown splits.
            var rec = AsPadLaunch(MakeRecording("pad-bounce", 17000,
                (SegmentEnvironment.SurfaceStationary, 3, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 2, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 4, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 30, "Kerbin", false)));

            AssertKept(rec, 1, RecordingOptimizer.SplitBoundaryReason.SuppressedSurfaceGrazeForward);
            AssertKept(rec, 2, RecordingOptimizer.SplitBoundaryReason.SuppressedSurfaceGrazeBackward);
            AssertKept(rec, 3, LaunchSiteKept);
            AssertSplits(rec, 4);

            var candidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec));
            Assert.Single(candidates);
            Assert.Equal((0, 4), candidates[0]);
        }

        [Fact]
        public void SplitSummary_CountsTheKeptPadRun_AfterTheExistingCounters()
        {
            EnableLogCapture();
            // The pre-ruling common case: a 4.5 s pad run, rejected by the 5 s floor and
            // counted splittableButRejected. It is now kept by the rule, counted on its own
            // counter appended at the end of the line (existing tokens stay substrings).
            var rec = AsPadLaunch(MakeRecording("pad-short", 9.6,
                (SegmentEnvironment.SurfaceStationary, 4.5, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 330, "Kerbin", false)));

            Assert.Empty(RecordingOptimizer.FindSplitCandidatesForOptimizer(Single(rec)));

            Assert.Contains(logLines, l =>
                l.Contains("[Optimizer]")
                && l.Contains("Split summary: rec=pad-short evaluated=1 grazeForward=0 grazeBackward=0 " +
                    "surfaceGrazeForward=0 surfaceGrazeBackward=0 seamSkipped=0 " +
                    "exoCoastBodyChangeKept=0 splittableButRejected=0 launchSiteRunKept=1"));
        }

        [Fact]
        public void NonLeadingSurfaceRunOfAPadLaunch_IsNotKept()
        {
            // The rule names only the FIRST departure. A recording whose first section is
            // already in flight has no leading Surface run, so a later take-off splits.
            var rec = AsPadLaunch(MakeRecording("pad-identity-airborne-start", 17000,
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false),
                (SegmentEnvironment.SurfaceStationary, 200, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false)));

            AssertSplits(rec, 1);
            AssertSplits(rec, 2);
        }

        [Theory]
        [InlineData("Launch Pad", "Prelaunch", true)]
        [InlineData("Runway", "Landed", true)]
        [InlineData("Launch Pad", "Flying", true)]
        [InlineData(null, "Prelaunch", true)]
        [InlineData(null, "PRELAUNCH", true)]
        [InlineData(null, "Landed", false)]
        [InlineData(null, "Splashed", false)]
        [InlineData(null, "EVA", false)]
        [InlineData(null, null, false)]
        [InlineData("", "", false)]
        public void IsLaunchSiteStart_ReadsTheSiteNameOrAPrelaunchStart(
            string launchSite, string startSituation, bool expected)
        {
            var rec = new Recording { LaunchSiteName = launchSite, StartSituation = startSituation };
            Assert.Equal(expected, RecordingOptimizer.IsLaunchSiteStart(rec));
        }

        [Fact]
        public void IsLaunchSiteStart_NullRecording_False()
        {
            Assert.False(RecordingOptimizer.IsLaunchSiteStart(null));
        }

        [Fact]
        public void FindLaunchSiteDepartureSection_WalksTheWholeLeadingSurfaceRun()
        {
            // Stationary -> Mobile is one Surface run; the departure is the first
            // non-Surface section after it.
            Assert.Equal(2, RecordingOptimizer.FindLaunchSiteDepartureSection(MakeL5Shape()));
        }

        [Fact]
        public void FindLaunchSiteDepartureSection_NeverLeavesTheSurface_MinusOne()
        {
            var rec = AsPadLaunch(MakeRecording("pad-only", 17000,
                (SegmentEnvironment.SurfaceStationary, 30, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 30, "Kerbin", false)));
            Assert.Equal(-1, RecordingOptimizer.FindLaunchSiteDepartureSection(rec));
        }

        [Fact]
        public void FindLaunchSiteDepartureSection_HopThatLandsBack_MinusOne()
        {
            // The whole hop is a surface graze, so the vessel never departs the leading run.
            var rec = AsPadLaunch(MakeRecording("pad-hop-only", 17000,
                (SegmentEnvironment.SurfaceStationary, 10, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 60, "Kerbin", false),
                (SegmentEnvironment.SurfaceMobile, 60, "Kerbin", false)));
            Assert.Equal(-1, RecordingOptimizer.FindLaunchSiteDepartureSection(rec));
        }

        [Fact]
        public void FindLaunchSiteDepartureSection_LeadingRunWithNoBodyKnown_StillFound()
        {
            // No StartBodyName (a legacy recording): the body guard has nothing to compare.
            var rec = MakeRecording("pad-no-start-body", 17000,
                (SegmentEnvironment.SurfaceStationary, 10, "Kerbin", false),
                (SegmentEnvironment.Atmospheric, 300, "Kerbin", false));
            rec.LaunchSiteName = "Launch Pad";
            Assert.Equal(1, RecordingOptimizer.FindLaunchSiteDepartureSection(rec));
        }

        [Fact]
        public void IsLaunchSiteDepartureBoundary_OutOfRangeIndex_False()
        {
            var rec = MakeL5Shape();
            Assert.False(RecordingOptimizer.IsLaunchSiteDepartureBoundary(rec, 0));
            Assert.False(RecordingOptimizer.IsLaunchSiteDepartureBoundary(rec, 9));
            Assert.False(RecordingOptimizer.IsLaunchSiteDepartureBoundary(null, 1));
        }
    }
}
