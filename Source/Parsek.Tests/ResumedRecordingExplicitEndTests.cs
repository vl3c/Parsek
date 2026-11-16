using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-ATTEMPT-STALE-END-UT: the quickload-resume trim stamps the resume cutoff as the
    /// explicit end of the recording it resumes (a Re-Fly attempt is resumed that way at its
    /// invoke), and nothing lifted it once the recorder flew on, so the save wrote the attempt's
    /// start as its end while its sidecar held the whole flight. Shape from the AST-4 reading run
    /// 2026-11-15: cutoff 358.01, an orbit coast to 1097.53, one point there, save at 1100.03.
    /// </summary>
    [Collection("Sequential")]
    public class ResumedRecordingExplicitEndTests : IDisposable
    {
        private const double CutoffUT = 358.01153320304235;
        private const double CoastEndUT = 1097.5333926390797;
        private const double SaveUT = 1100.0333926390774;
        private const uint ForkPid = 1637664549u;

        private readonly List<string> logLines = new List<string>();

        public ResumedRecordingExplicitEndTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static Recording ResumedFork()
        {
            var rec = new Recording
            {
                RecordingId = "rec_fork",
                VesselName = "AST Klaw Probe",
                VesselPersistentId = ForkPid,
                ExplicitStartUT = CutoffUT,
            };
            // The resume prep trims the fresh fork at the cutoff: it is open, so it is stamped.
            ParsekScenario.TrimRecordingPastUT(rec, CutoffUT);
            return rec;
        }

        private static OrbitSegment Coast()
        {
            return new OrbitSegment
            {
                startUT = CutoffUT,
                endUT = CoastEndUT,
                bodyName = "Kerbin",
                semiMajorAxis = 699278.49,
                eccentricity = 0.0105,
                inclination = 0.0895,
            };
        }

        private static TrajectoryPoint CoastEndPoint()
        {
            return new TrajectoryPoint { ut = CoastEndUT, bodyName = "Kerbin", altitude = 102759.0 };
        }

        private static void AppendFlownData(
            List<TrajectoryPoint> points, List<OrbitSegment> segments, List<TrackSection> sections)
        {
            OrbitSegment coast = Coast();
            segments.Add(coast);
            points.Add(CoastEndPoint());
            sections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = CutoffUT,
                endUT = CoastEndUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { coast },
            });
            sections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = CoastEndUT,
                endUT = SaveUT,
                frames = new List<TrajectoryPoint> { CoastEndPoint() },
                checkpoints = new List<OrbitSegment>(),
            });
        }

        private static Recording FlownFork()
        {
            Recording rec = ResumedFork();
            AppendFlownData(rec.Points, rec.OrbitSegments, rec.TrackSections);
            return rec;
        }

        // catches: the trim stamp not being where the stale value came from (the investigation's
        // premise); the stamped end is the cutoff, and EndUT already reads the flown bounds.
        [Fact]
        public void ResumedFork_IsStampedAtTheCutoff_AndEndUTReadsTheFlownBounds()
        {
            Recording rec = FlownFork();

            Assert.Equal(CutoffUT, rec.ExplicitEndUT);
            Assert.Equal(SaveUT, rec.EndUT);
        }

        // catches: the defect itself, a flown resumed recording keeping the cutoff as its
        // explicit end after the recorder appended its flight.
        [Fact]
        public void Lift_FlownResumedFork_MovesTheExplicitEndToTheRecordedEnd()
        {
            Recording rec = FlownFork();

            bool lifted = ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test");

            Assert.True(lifted);
            Assert.Equal(SaveUT, rec.ExplicitEndUT);
            Assert.Equal(SaveUT, rec.EndUT);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Explicit end lifted to the recorded data: rec=rec_fork context=test")
                && l.Contains("oldExplicitEndUT=358.01")
                && l.Contains("newExplicitEndUT=1100.03"));
        }

        // catches: the fix reaching only the helper and not the stop flush the scene exit and
        // every vessel switch run before finalization.
        [Fact]
        public void StopFlush_FlownResumedFork_LiftsTheExplicitEnd()
        {
            Recording rec = ResumedFork();
            var tree = new RecordingTree { Id = "tree", TreeName = "AST Klaw", ActiveRecordingId = rec.RecordingId };
            tree.Recordings[rec.RecordingId] = rec;
            var recorder = new FlightRecorder();
            AppendFlownData(recorder.Recording, recorder.OrbitSegments, recorder.TrackSections);
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));

            flight.FlushRecorderToTreeRecording(recorder, tree);

            Assert.Equal(SaveUT, rec.ExplicitEndUT);
            Assert.Single(rec.Points);
            Assert.Contains(logLines, l => l.Contains("Explicit end lifted to the recorded data")
                && l.Contains("context=FlushRecorderToTreeRecording"));
        }

        // catches: the save writing the stale end next to a sidecar that holds the whole flight;
        // pointCount counts the one flat point and was never stale.
        [Fact]
        public void Save_AfterTheLift_WritesTheRecordedEnd()
        {
            Recording rec = FlownFork();
            ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test");

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);

            Assert.Equal(SaveUT.ToString("R", CultureInfo.InvariantCulture), node.GetValue("explicitEndUT"));
            Assert.Equal("1", node.GetValue("pointCount"));
        }

        // catches: a raw-field reader still taking the cutoff as the end: the final-section
        // lookup picked the opening orbit coast instead of the last section.
        [Fact]
        public void FinalTrackSection_AfterTheLift_IsTheLastSection()
        {
            Recording rec = FlownFork();
            Assert.True(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out TrackSection before));
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, before.referenceFrame);

            ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test");

            Assert.True(ParsekFlightMergeFlow.TryGetFinalTrackSection(rec, out TrackSection after));
            Assert.Equal(ReferenceFrame.Absolute, after.referenceFrame);
            Assert.Equal(SaveUT, after.endUT);
        }

        // catches: a resumed recording that shed debris after the resume and flew on being read
        // as closed at that split (its stale explicit end sits before the branch UT), so it is no
        // longer the vessel's leaf at finalization.
        [Fact]
        public void BreakupAfterTheResume_FlownOn_LeavesTheVesselLineOpenAfterTheLift()
        {
            Recording rec = FlownFork();
            rec.ChildBranchPointId = "bp_breakup";
            var debris = new Recording { RecordingId = "rec_debris", VesselPersistentId = 42u, IsDebris = true };
            var recordings = new Dictionary<string, Recording>(StringComparer.Ordinal)
            {
                { rec.RecordingId, rec },
                { debris.RecordingId, debris },
            };
            var bp = new BranchPoint
            {
                Id = "bp_breakup",
                Type = BranchPointType.Breakup,
                UT = 700.0,
                ParentRecordingIds = new List<string> { rec.RecordingId },
                ChildRecordingIds = new List<string> { debris.RecordingId },
            };
            var bps = new List<BranchPoint> { bp };

            Assert.False(RecordingTree.ChildBranchLeavesVesselLineOpen(rec, recordings, bps));

            ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test");

            Assert.True(RecordingTree.ChildBranchLeavesVesselLineOpen(rec, recordings, bps));
        }

        // catches: the lift inventing an explicit end where finalization owns the default.
        [Fact]
        public void Lift_UnsetExplicitEnd_StaysUnset()
        {
            var rec = new Recording { RecordingId = "rec_open" };
            AppendFlownData(rec.Points, rec.OrbitSegments, rec.TrackSections);

            Assert.False(ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test"));
            Assert.True(double.IsNaN(rec.ExplicitEndUT));
        }

        // catches: the lift moving an end a terminal stamped (destruction, recovery), which is
        // an authored end fact the terminal UT readers key on.
        [Fact]
        public void Lift_TerminalStampedEnd_IsLeftAlone()
        {
            Recording rec = FlownFork();
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.ExplicitEndUT = 900.0;

            Assert.False(ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test"));
            Assert.Equal(900.0, rec.ExplicitEndUT);
        }

        // catches: the lift overriding parent-anchored debris, whose end follows its own
        // coverage clamp.
        [Fact]
        public void Lift_ParentAnchoredDebris_IsLeftAlone()
        {
            Recording rec = FlownFork();
            rec.IsDebris = true;
            rec.ParentAnchorRecordingId = "rec_parent";

            Assert.False(ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test"));
            Assert.Equal(CutoffUT, rec.ExplicitEndUT);
        }

        // catches: the lift lowering an explicit end that extends past the data (an outer
        // boundary such as a split UT is allowed to sit later than the payload).
        [Fact]
        public void Lift_ExplicitEndLaterThanTheData_IsLeftAlone()
        {
            Recording rec = FlownFork();
            rec.ExplicitEndUT = 2000.0;

            Assert.False(ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test"));
            Assert.Equal(2000.0, rec.ExplicitEndUT);
        }

        // catches: the lift firing on a resumed recording the recorder has not appended to yet
        // (the save between the Re-Fly invoke and the recorder binding).
        [Fact]
        public void Lift_ResumedForkWithNoData_KeepsTheCutoff()
        {
            Recording rec = ResumedFork();

            Assert.False(ParsekFlight.LiftStampedExplicitEndToTrajectoryEnd(rec, "test"));
            Assert.Equal(CutoffUT, rec.ExplicitEndUT);
            Assert.Equal(CutoffUT, rec.EndUT);
        }
    }
}
