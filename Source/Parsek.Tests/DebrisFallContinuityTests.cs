using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A dropped booster that outlives the sampling range (the GS-8 shape: last in-range sample
    /// 33 s before stock unloads it 22.5 km away, an on-rails conic of one or two frames, a
    /// predicted tail to the impact) replays its whole fall with no hang and no jump. The fall
    /// is a simulated one with drag; the recorder's rate rule, triggers, section close and
    /// on-rails flush, the finalizer's live-orbit tail and the flight dispatch's own frame /
    /// segment finders are the production code (todo DEBRIS-GHOST-HANGS-PAST-THE-SAMPLING-RANGE).
    /// </summary>
    [Collection("Sequential")]
    public class DebrisFallContinuityTests : IDisposable
    {
        private const uint Pid = 7301u;
        private const string RecId = "booster-fall";
        private const double Mu = 3.5316e12;
        private const double Radius = 600000.0;
        private const double Dt = 0.02;
        private const double StartUT = 62.34;
        private const double UnloadDistance = 22500.0;

        private readonly List<string> logLines = new List<string>();

        private static readonly TrajectoryMath.BoundaryFrame.BodyRotation Kerbin =
            new TrajectoryMath.BoundaryFrame.BodyRotation
            {
                BodyName = "Kerbin",
                Radius = Radius,
                GravParameter = Mu,
                Rotates = false,
                FixedRotationAngleDeg = 0.0,
            };

        public DebrisFallContinuityTests()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ---------------------------------------------------------------- the real fall

        private struct State
        {
            public double UT;
            public Vector3d Pos;
            public Vector3d Vel;
        }

        private static State Step(State s)
        {
            double r = s.Pos.magnitude;
            Vector3d gravity = s.Pos * (-Mu / (r * r * r));
            // Terminal speed about 230 m/s near the ground, as the GS-8 boosters read.
            Vector3d drag = s.Vel * (-1.85e-4 * s.Vel.magnitude);
            Vector3d vel = s.Vel + (gravity + drag) * Dt;
            return new State { UT = s.UT + Dt, Vel = vel, Pos = s.Pos + vel * Dt };
        }

        /// <summary>The booster from its separation to stock's unload, one state per tick.</summary>
        private static List<State> FallUntilUnload()
        {
            var fall = new List<State>();
            var s = new State
            {
                UT = StartUT,
                Pos = new Vector3d(Radius + 4440.0, 0.0, 0.0),
                Vel = new Vector3d(230.0, 200.0, 0.0),
            };
            while (FocusDistance(s.UT) < UnloadDistance)
            {
                fall.Add(s);
                s = Step(s);
            }
            fall.Add(s);
            return fall;
        }

        // The focused vessel keeps climbing: 2.3 km at 16.6 s, 22.5 km at 53 s.
        private static double FocusDistance(double ut)
        {
            double t = ut - StartUT;
            return 16.0 + 8.0 * t * t;
        }

        private static TrajectoryPoint Point(State s, Quaternion rotation)
        {
            Assert.True(TrajectoryMath.BoundaryFrame.TryStoredFrameToBodyFixed(
                s.Pos, Kerbin, s.UT, out double lat, out double lon, out double alt));
            return new TrajectoryPoint
            {
                ut = s.UT,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = rotation,
                velocity = new Vector3((float)s.Vel.x, (float)s.Vel.y, (float)s.Vel.z),
                bodyName = "Kerbin",
            };
        }

        private static Quaternion Tumble(double ut)
        {
            double half = 0.128 * (ut - StartUT) * 0.5; // the booster's recorded 0.128 rad/s
            return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        // ---------------------------------------------------------------- recording it

        private static Dictionary<string, ExtrapolationBody> Bodies()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = Mu,
                    Radius = Radius,
                    AtmosphereDepth = 70000.0,
                    DeletionAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude,
                    SphereOfInfluence = 84159286.0,
                }
            };
        }

        /// <summary>
        /// Records the fall. <paramref name="oldBehaviour"/> is the shape the GS-8 flight wrote:
        /// nothing sampled past the range and no closing frame.
        /// </summary>
        private static Recording Record(List<State> fall, bool isDebris, bool oldBehaviour, out int beyondSamples)
        {
            var rec = new Recording
            {
                RecordingId = RecId,
                VesselName = "Kerbal X Debris",
                VesselPersistentId = Pid,
                IsDebris = isDebris,
                ParentAnchorRecordingId = "parent-rec",
                TerminalStateValue = TerminalState.Destroyed,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = "Kerbin",
            };
            var tree = new RecordingTree { Id = "fall-tree" };
            tree.Recordings[RecId] = rec;
            tree.BackgroundMap[Pid] = RecId;
            var bg = new BackgroundRecorder(tree);
            bg.InjectLoadedStateWithEnvironmentForTesting(Pid, RecId, SegmentEnvironment.Atmospheric, StartUT);

            beyondSamples = 0;
            float minSetting = ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
            float maxSetting = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float dirThreshold = ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speedThreshold = ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium) / 100f;

            // The last tick is the unload's own, taken by the on-rails transition.
            for (int i = 0; i < fall.Count - 1; i++)
            {
                State s = fall[i];
                double distance = FocusDistance(s.UT);
                bool beyond = ProximityRateSelector.IsBeyondRange(distance);
                if (beyond && oldBehaviour)
                    continue;
                double interval = BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                    ProximityRateSelector.GetSampleInterval(distance), rec, beyond);
                BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                    ProximitySamplingTier.None, false, beyond, interval, minSetting, maxSetting, rec,
                    out float min, out float max);
                var velocity = new Vector3((float)s.Vel.x, (float)s.Vel.y, (float)s.Vel.z);
                Quaternion rotation = Tumble(s.UT);
                bg.ResolveBackgroundSampleTriggersForTesting(
                    Pid, s.UT, velocity, rotation, false, beyond, min, max, minSetting,
                    dirThreshold, speedThreshold, out bool motion, out bool attitude);
                if (!motion && !attitude)
                    continue;
                bg.AppendLoadedSampleForTesting(Pid, Point(s, rotation));
                bg.SetLoadedSampleBaselineForTesting(Pid, s.UT, velocity, rotation);
                if (beyond)
                    beyondSamples++;
            }

            State unload = fall[fall.Count - 1];
            TrajectoryPoint boundary = Point(unload, Tumble(unload.UT));
            bg.FlushLoadedStateForOnRailsTransitionForTesting(
                Pid, SegmentEnvironment.Atmospheric, true, boundary, unload.UT,
                oldBehaviour ? (TrajectoryPoint?)null : boundary);

            // The on-rails conic: two frames, then the debris recording ends (it unloaded).
            ExtrapolationResult coast = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = unload.UT,
                    bodyName = "Kerbin",
                    position = unload.Pos,
                    velocity = unload.Vel,
                    orbitalFrameRotation = Quaternion.identity,
                },
                Bodies());
            OrbitSegment conic = Assert.Single(coast.segments);
            conic.isPredicted = false;
            conic.endUT = unload.UT + 2 * Dt;
            rec.OrbitSegments.Add(conic);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.Atmospheric,
                source = TrackSectionSource.Checkpoint,
                startUT = conic.startUT,
                endUT = conic.endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { conic },
            });
            return rec;
        }

        private static bool FinalizeTail(Recording rec)
        {
            OrbitSegment conic = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            Assert.True(BallisticExtrapolator.TryPropagate(
                conic, Mu, conic.endUT, out Vector3d position, out Vector3d velocity));
            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                new PatchedConicSnapshotResult
                {
                    FailureReason = PatchedConicSnapshotFailureReason.NullSolver,
                    Segments = new List<OrbitSegment>()
                },
                Bodies(),
                delegate(out BallisticStateVector startState)
                {
                    startState = new BallisticStateVector
                    {
                        ut = conic.endUT,
                        bodyName = "Kerbin",
                        position = position,
                        velocity = velocity,
                        orbitalFrameRotation = Quaternion.identity,
                    };
                    return true;
                },
                (startState, bodies) => BallisticExtrapolator.Extrapolate(
                    startState, bodies, warnOnSubSurfaceStart: false),
                null,
                liveVesselConfirmedAlive: true, // VesselUnloadStateTests: alive through its protoVessel
                spinAngularVelocityVesselLocal: Vector3.zero,
                result: out IncompleteBallisticFinalizationResult result);
            if (!built || result.appendedOrbitSegments == null)
                return false;
            rec.OrbitSegments.AddRange(result.appendedOrbitSegments);
            rec.ExplicitEndUT = result.terminalUT;
            return true;
        }

        // ---------------------------------------------------------------- replaying it

        private static Vector3d Cartesian(double lat, double lon, double alt)
        {
            double r = Radius + alt;
            double la = lat * Math.PI / 180.0;
            double lo = lon * Math.PI / 180.0;
            return new Vector3d(
                r * Math.Cos(la) * Math.Cos(lo), r * Math.Cos(la) * Math.Sin(lo), r * Math.Sin(la));
        }

        private static Vector3d OnConic(OrbitSegment segment, double ut)
        {
            Assert.True(TrajectoryMath.BoundaryFrame.TryOrbitSegmentBodyFixedAtUT(
                new List<OrbitSegment> { segment }, ut, _ => Kerbin,
                out _, out double lat, out double lon, out double alt), "conic at " + ut);
            return Cartesian(lat, lon, alt);
        }

        /// <summary>Where the flight dispatch draws the ghost: checkpoint section, Absolute
        /// section frames, then the orbit tail past the last section.</summary>
        private static Vector3d Replay(Recording rec, double ut, out string surface)
        {
            int idx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, ut);
            if (idx >= 0)
            {
                TrackSection section = rec.TrackSections[idx];
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    surface = "conic";
                    return OnConic(section.checkpoints[0], ut);
                }
                surface = "section";
                Assert.True(ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                    rec.TrackSections, idx, out List<TrajectoryPoint> frames, out _));
                int cached = -1;
                Assert.True(TrajectoryMath.InterpolatePoints(
                    frames, ref cached, ut, out TrajectoryPoint before, out TrajectoryPoint after, out float t));
                Vector3d a = Cartesian(before.latitude, before.longitude, before.altitude);
                Vector3d b = Cartesian(after.latitude, after.longitude, after.altitude);
                return a + (b - a) * t;
            }

            Assert.True(GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                rec, ut, out OrbitSegment tail, out _), "no surface at " + ut);
            surface = tail.isPredicted ? "tail" : "conic";
            return OnConic(tail, ut);
        }

        private sealed class Walk
        {
            public double WorstStepMeters;
            public double WorstStepUT;
            public double LongestHoldSeconds;
            public double EndAltitude;
            public readonly HashSet<string> Surfaces = new HashSet<string>();
        }

        private static Walk WalkFrom(Recording rec, double fromUT)
        {
            var walk = new Walk();
            double endUT = rec.ExplicitEndUT;
            Vector3d previous = Replay(rec, fromUT, out _);
            double hold = 0.0;
            for (double ut = fromUT + Dt; ut <= endUT; ut += Dt)
            {
                Vector3d now = Replay(rec, ut, out string surface);
                walk.Surfaces.Add(surface);
                double step = (now - previous).magnitude;
                if (step > walk.WorstStepMeters)
                {
                    walk.WorstStepMeters = step;
                    walk.WorstStepUT = ut;
                }
                hold = step < 1e-6 ? hold + Dt : 0.0;
                walk.LongestHoldSeconds = Math.Max(walk.LongestHoldSeconds, hold);
                previous = now;
            }
            walk.EndAltitude = previous.magnitude - Radius;
            return walk;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DroppedPiece_FollowsItsFallAcrossSectionConicAndTail(bool isDebris)
        {
            List<State> fall = FallUntilUnload();
            State unload = fall[fall.Count - 1];
            Recording rec = Record(fall, isDebris, oldBehaviour: false, out int beyondSamples);
            Assert.True(FinalizeTail(rec));

            // The path: a 250 m/s fall is 5 m per frame; the conic and the tail carry on from
            // the state the last frame was taken at.
            Walk walk = WalkFrom(rec, StartUT + 12.0);
            Assert.True(walk.WorstStepMeters < 8.0,
                $"jump of {walk.WorstStepMeters:R} m at UT {walk.WorstStepUT:R}");
            Assert.True(walk.LongestHoldSeconds < 0.1, $"hung for {walk.LongestHoldSeconds:R} s");
            Assert.InRange(walk.EndAltitude, -1.0, 10.0);
            Assert.Equal(new[] { "conic", "section", "tail" }, new SortedSet<string>(walk.Surfaces));

            // The recorded arc is the real one, within what linear interpolation of the samples
            // loses (a h^2 / 8 under drag plus gravity).
            double worst = 0.0;
            foreach (State s in fall)
            {
                if (s.UT < StartUT + 12.0) continue;
                worst = Math.Max(worst, (Replay(rec, Math.Min(s.UT, unload.UT - 1e-3), out _) - s.Pos).magnitude);
            }
            Assert.True(worst < 12.0, $"worst deviation from the real fall {worst:R} m");

            // The section reaches the conic and the tail starts where the conic ends.
            TrackSection loaded = rec.TrackSections[0];
            Assert.Equal(unload.UT, loaded.endUT);
            Assert.Equal(unload.UT, loaded.frames[loaded.frames.Count - 1].ut);
            OrbitSegment tail = rec.OrbitSegments[1];
            Assert.True(tail.isPredicted);
            Assert.Equal(rec.OrbitSegments[0].endUT, tail.startUT, 6);

            // Cost: about 1 Hz for debris and half that for a controlled child, tumbling or not.
            double beyondSeconds = unload.UT - (StartUT + Math.Sqrt((2300.0 - 16.0) / 8.0));
            double floor = isDebris
                ? ProximityRateSelector.BeyondRangeDebrisInterval
                : ProximityRateSelector.FarInterval;
            Assert.InRange(beyondSamples, beyondSeconds / 3.1, beyondSeconds / floor + 1.0);
        }

        [Fact]
        public void TheOldShape_HangsForHalfAMinuteThenJumpsKilometres()
        {
            // The control: the same fall recorded the way GS-8 wrote it trips both halves of the
            // continuity check above.
            List<State> fall = FallUntilUnload();
            Recording rec = Record(fall, true, oldBehaviour: true, out int beyondSamples);
            Assert.Equal(0, beyondSamples);
            Assert.True(FinalizeTail(rec));

            Walk walk = WalkFrom(rec, StartUT + 12.0);

            Assert.True(walk.LongestHoldSeconds > 30.0, $"hold {walk.LongestHoldSeconds:R} s");
            Assert.True(walk.WorstStepMeters > 2000.0, $"jump {walk.WorstStepMeters:R} m");
            Assert.Equal(fall[fall.Count - 1].UT, walk.WorstStepUT, 1);
        }

        // ---------------------------------------------------------------- the closing frame

        private static BackgroundRecorder LoadedRecorder(
            out Recording rec, SegmentEnvironment env = SegmentEnvironment.Atmospheric)
        {
            rec = new Recording { RecordingId = RecId, VesselPersistentId = Pid };
            var tree = new RecordingTree { Id = "close-tree" };
            tree.Recordings[RecId] = rec;
            tree.BackgroundMap[Pid] = RecId;
            var bg = new BackgroundRecorder(tree);
            bg.InjectLoadedStateWithEnvironmentForTesting(Pid, RecId, env, 100.0);
            return bg;
        }

        private static TrajectoryPoint At(double ut, double alt) =>
            new TrajectoryPoint { ut = ut, altitude = alt, bodyName = "Kerbin", rotation = Quaternion.identity };

        [Fact]
        public void OnRailsFlush_ClosingFrameEndsTheSection_AndReplacesTheBoundaryPoint()
        {
            BackgroundRecorder bg = LoadedRecorder(out Recording rec);
            bg.AppendLoadedSampleForTesting(Pid, At(100.0, 5000.0));
            bg.AppendLoadedSampleForTesting(Pid, At(101.0, 4800.0));

            bg.FlushLoadedStateForOnRailsTransitionForTesting(
                Pid, SegmentEnvironment.Atmospheric, true, At(101.9, 4600.0), 101.9, At(101.9, 4601.0));

            TrackSection section = Assert.Single(rec.TrackSections);
            Assert.Equal(new[] { 100.0, 101.0, 101.9 }, section.frames.ConvertAll(p => p.ut));
            Assert.Equal(4601.0, rec.Points[rec.Points.Count - 1].altitude);
            Assert.DoesNotContain(rec.Points, p => p.altitude == 4600.0);
            Assert.Equal(101.9, rec.ExplicitEndUT);
            Assert.DoesNotContain(logLines, l => l.Contains("Boundary point sampled"));
        }

        [Fact]
        public void OnRailsFlush_NoClosingFrame_KeepsTheBoundaryPoint()
        {
            BackgroundRecorder bg = LoadedRecorder(out Recording rec);
            bg.AppendLoadedSampleForTesting(Pid, At(100.0, 5000.0));

            bg.FlushLoadedStateForOnRailsTransitionForTesting(
                Pid, SegmentEnvironment.Atmospheric, true, At(101.9, 4600.0), 101.9);

            Assert.Single(Assert.Single(rec.TrackSections).frames);
            Assert.Equal(4600.0, rec.Points[rec.Points.Count - 1].altitude);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]") && l.Contains("Boundary point sampled"));
        }

        [Fact]
        public void OnRailsFlush_NoPayloadBoundarySection_CarriesTheBoundaryItself()
        {
            // Landing past the range: the seam section holds the frame at the close UT and
            // playback bridges into it, so the atmospheric section takes no closing frame.
            BackgroundRecorder bg = LoadedRecorder(out Recording rec);
            bg.AppendLoadedSampleForTesting(Pid, At(100.0, 20.0));

            bg.FlushLoadedStateForOnRailsTransitionForTesting(
                Pid, SegmentEnvironment.SurfaceMobile, false, At(101.9, 5.0), 101.9, At(101.9, 5.0));

            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Single(rec.TrackSections[0].frames);
            Assert.True(rec.TrackSections[1].isBoundarySeam);
            Assert.Equal(101.9, Assert.Single(rec.TrackSections[1].frames).ut);
        }

        private static string Decide(
            bool hasVessel = true,
            bool loaded = true,
            bool packed = false,
            bool active = true,
            ReferenceFrame frame = ReferenceFrame.Absolute,
            SegmentEnvironment env = SegmentEnvironment.Atmospheric,
            int frames = 3,
            double last = 100.0,
            double close = 100.8)
        {
            return BackgroundRecorder.DecideSectionClosingFrame(
                hasVessel, loaded, packed, active, frame, env, frames, last, close);
        }

        [Fact]
        public void DecideSectionClosingFrame_Table()
        {
            Assert.Null(Decide());
            Assert.Null(Decide(env: SegmentEnvironment.ExoBallistic));
            Assert.Null(Decide(env: SegmentEnvironment.SurfaceMobile));

            Assert.Equal("recorded-vessel-not-found", Decide(hasVessel: false));
            Assert.Equal("vessel-unloaded", Decide(loaded: false));
            Assert.Equal("vessel-packed", Decide(packed: true));
            Assert.Equal("already-sampled-at-stop", Decide(last: 100.8));
            Assert.Equal("no-open-section", Decide(active: false));
            Assert.Equal("section-not-absolute", Decide(frame: ReferenceFrame.Relative));
            Assert.Equal("surface-stationary", Decide(env: SegmentEnvironment.SurfaceStationary));
            Assert.Equal("section-has-no-frames", Decide(frames: 0, last: double.NaN));
        }

        [Theory]
        [InlineData("BackgroundRecorder.cs", "public void OnBackgroundVesselGoOnRails(", "\"go-on-rails\"", "loadedStates.Remove(pid)")]
        [InlineData("BackgroundRecorder.cs", "private void OnVesselRemovedFromBackground(", "\"background-end\"", "CloseBackgroundTrackSection(loadedState")]
        [InlineData("BackgroundRecorder.cs", "public void OnBackgroundVesselWillDestroy(", "\"background-destroy\"", "CloseBackgroundTrackSection(loadedState")]
        [InlineData("BackgroundRecorder.Finalization.cs", "public void FinalizeAllForCommit(", "\"background-commit\"", "CloseBackgroundTrackSection(state")]
        public void EveryEndOfALoadedRecording_TakesItsClosingFrameBeforeTheSectionCloses(
            string file, string method, string context, string close)
        {
            string path = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "Recording", file);
            string source = System.IO.File.ReadAllText(path);
            int body = source.IndexOf(method, StringComparison.Ordinal);
            Assert.True(body >= 0, method);
            int frame = source.IndexOf(context, body, StringComparison.Ordinal);
            int closes = source.IndexOf(close, body, StringComparison.Ordinal);
            Assert.True(frame > body && closes > frame, context + " frame=" + frame + " close=" + closes);
        }
    }
}
