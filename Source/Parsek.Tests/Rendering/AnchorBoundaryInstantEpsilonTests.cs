using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;
using BoundaryFrame = Parsek.TrajectoryMath.BoundaryFrame;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// The boundary-instant seed epsilon (todo REFLY-ANCHOR-EPSILON-1300KM):
    /// <see cref="TrajectoryMath.BoundaryFrame"/>'s pure frame relation and
    /// <see cref="AnchorPropagator.TryResolveBoundaryInstantEpsilon"/> on synthetic recordings
    /// with a known offset, at two rebuild clocks a quarter of a Kerbin day apart. The fixture
    /// proof is <see cref="AnchorEpsilonRebuildClockTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class AnchorBoundaryInstantEpsilonTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double KerbinPeriod = 21549.425;
        private const double KerbinInitialRotation = 90.0;
        private const double BoundaryUT = 5000.0;
        private const double ClockA = 185800.5;
        private const double ClockB = ClockA + KerbinPeriod / 4.0;

        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly CelestialBody mun;

        public AnchorBoundaryInstantEpsilonTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();

            kerbin = TestBodyRegistry.CreateBody("Kerbin", KerbinRadius, KerbinMu);
            kerbin.rotationPeriod = KerbinPeriod;
            kerbin.initialRotation = KerbinInitialRotation;
            kerbin.rotates = true;
            mun = TestBodyRegistry.CreateBody("Mun", 200000.0, 6.5138398e10);
            mun.rotationPeriod = 138984.38;
            mun.initialRotation = 230.0;
            mun.rotates = true;
            Func<string, CelestialBody> bodies = name =>
                name == "Kerbin" ? kerbin : name == "Mun" ? mun : null;
            AnchorPropagator.BodyResolverForTesting = bodies;
            SmoothingPipeline.BodyResolverForTesting = bodies;
            SmoothingPipeline.TreeResolverForTesting = _ => null;
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static BoundaryFrame.BodyRotation KerbinRotation() => new BoundaryFrame.BodyRotation
        {
            BodyName = "Kerbin",
            Radius = KerbinRadius,
            GravParameter = KerbinMu,
            RotationPeriod = KerbinPeriod,
            InitialRotationDeg = KerbinInitialRotation,
            Rotates = true,
        };

        // ---- the world model: body.GetWorldSurfacePosition at a rebuild clock ------------

        private static Vector3d Surface(double latDeg, double lonDeg, double altM, double clockUT)
        {
            double r = KerbinRadius + altM;
            double lon = (lonDeg + KerbinInitialRotation + 360.0 * clockUT / KerbinPeriod) * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            return new Vector3d(r * Math.Cos(lat) * Math.Cos(lon), r * Math.Cos(lat) * Math.Sin(lon), r * Math.Sin(lat));
        }

        private static Func<string, double, double, double, Vector3d> LookupAt(double clockUT)
        {
            return (name, lat, lon, alt) =>
            {
                if (name != "Kerbin") throw new InvalidOperationException("model has Kerbin only");
                return Surface(lat, lon, alt, clockUT);
            };
        }

        /// <summary>The model's inverse, written out here (not the code under test).</summary>
        private static void ModelBodyFixed(Vector3d p, double ut, out double lat, out double lon, out double alt)
        {
            double r = p.magnitude;
            lat = Math.Asin(p.z / r) * 180.0 / Math.PI;
            lon = Math.Atan2(p.y, p.x) * 180.0 / Math.PI - KerbinInitialRotation - 360.0 * ut / KerbinPeriod;
            lon = ((lon % 360.0) + 540.0) % 360.0 - 180.0;
            alt = r - KerbinRadius;
        }

        private static OrbitSegment LkoSegment(double startUT, double endUT, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                bodyName = body,
                inclination = 28.0,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 40.0,
                argumentOfPeriapsis = 15.0,
                meanAnomalyAtEpoch = 0.3,
                epoch = startUT,
                startUT = startUT,
                endUT = endUT,
            };
        }

        private static Vector3d StoredKepler(OrbitSegment seg, double ut)
        {
            Assert.True(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, KerbinMu, out TwoBodyOrbit orbit));
            return orbit.GetPositionAtUT(ut);
        }

        /// <summary>Physics samples ON the conic (model inverse), the last / first one lifted by
        /// <paramref name="boundaryAltOffsetM"/>.</summary>
        private static List<TrajectoryPoint> FramesOnConic(OrbitSegment seg, double fromUT, double toUT, double boundaryUT,
            double boundaryAltOffsetM, string body = "Kerbin")
        {
            var frames = new List<TrajectoryPoint>();
            // Half-second samples: the outlier classifier rejects a step longer than the
            // 2.5 km physics bubble, and LKO covers 2.3 km/s.
            for (double t = fromUT; t <= toUT + 1e-9; t += 0.5)
            {
                ModelBodyFixed(StoredKepler(seg, t), t, out double lat, out double lon, out double alt);
                if (Math.Abs(t - boundaryUT) < 1e-9) alt += boundaryAltOffsetM;
                frames.Add(new TrajectoryPoint
                {
                    ut = t, latitude = lat, longitude = lon, altitude = alt,
                    bodyName = body, rotation = Quaternion.identity,
                });
            }
            return frames;
        }

        private static TrackSection PhysicsSection(List<TrajectoryPoint> frames, ReferenceFrame frame = ReferenceFrame.Absolute)
        {
            return new TrackSection
            {
                referenceFrame = frame,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Active,
                startUT = frames.Count > 0 ? frames[0].ut : 0,
                endUT = frames.Count > 0 ? frames[frames.Count - 1].ut : 0,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
            };
        }

        private static TrackSection CheckpointSection(OrbitSegment seg)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Checkpoint,
                startUT = seg.startUT,
                endUT = seg.endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { seg },
            };
        }

        /// <summary>physics [BoundaryUT-20, BoundaryUT] then checkpoint [BoundaryUT, +600]: a BubbleExit.</summary>
        private static Recording BubbleExitRecording(string id, double altOffset)
        {
            OrbitSegment seg = LkoSegment(BoundaryUT, BoundaryUT + 600.0);
            seg.epoch = BoundaryUT - 20.0;
            var rec = new Recording { RecordingId = id, VesselName = id };
            rec.TrackSections.Add(PhysicsSection(FramesOnConic(seg, BoundaryUT - 20.0, BoundaryUT, BoundaryUT, altOffset)));
            rec.TrackSections.Add(CheckpointSection(seg));
            return rec;
        }

        private AnchorCorrection RunAndLookup(Recording rec, double clockUT, int sectionIndex, AnchorSide side)
        {
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.FitAndStorePerSection(rec);
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "boundary-instant" },
                new[] { rec }, new RecordingTree[0], LookupAt(clockUT), resolver: null);
            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, sectionIndex, side, out AnchorCorrection ac),
                "no anchor at " + rec.RecordingId + " section " + sectionIndex + " " + side);
            return ac;
        }

        // ---- pure frame relation -----------------------------------------------------

        [Fact]
        public void RotationAngle_RotatingBodyAdvancesWithUT_StaticBodyIsFixed()
        {
            var b = KerbinRotation();
            Assert.Equal(90.0, BoundaryFrame.RotationAngleDegAt(b, 0.0), 9);
            Assert.Equal(90.0 + 90.0, BoundaryFrame.RotationAngleDegAt(b, KerbinPeriod / 4.0), 9);
            b.Rotates = false;
            b.FixedRotationAngleDeg = 12.5;
            Assert.Equal(12.5, BoundaryFrame.RotationAngleDegAt(b, 123456.0), 9);
        }

        [Fact]
        public void IsUsable_RejectsDegenerateConstants()
        {
            Assert.True(BoundaryFrame.IsUsable(KerbinRotation()));
            var b = KerbinRotation(); b.Radius = 0.0;
            Assert.False(BoundaryFrame.IsUsable(b));
            b = KerbinRotation(); b.GravParameter = double.NaN;
            Assert.False(BoundaryFrame.IsUsable(b));
            b = KerbinRotation(); b.RotationPeriod = 0.0;
            Assert.False(BoundaryFrame.IsUsable(b));
            b = KerbinRotation(); b.Rotates = false; b.RotationPeriod = 0.0; b.FixedRotationAngleDeg = 3.0;
            Assert.True(BoundaryFrame.IsUsable(b));
        }

        [Fact]
        public void StoredFrameToBodyFixed_KnownPoints()
        {
            var b = KerbinRotation();
            // +x of the stored frame at UT 0: inertial longitude 0, Kerbin's prime meridian at 90.
            Assert.True(BoundaryFrame.TryStoredFrameToBodyFixed(new Vector3d(700000, 0, 0), b, 0.0,
                out double lat, out double lon, out double alt));
            Assert.Equal(0.0, lat, 9);
            Assert.Equal(-90.0, lon, 9);
            Assert.Equal(100000.0, alt, 6);
            // A quarter day later the body has turned 90 deg east under the same point.
            Assert.True(BoundaryFrame.TryStoredFrameToBodyFixed(new Vector3d(700000, 0, 0), b, KerbinPeriod / 4.0,
                out _, out lon, out _));
            Assert.Equal(180.0, lon, 9);
            // The pole.
            Assert.True(BoundaryFrame.TryStoredFrameToBodyFixed(new Vector3d(0, 0, 650000), b, 0.0,
                out lat, out _, out alt));
            Assert.Equal(90.0, lat, 9);
            Assert.Equal(50000.0, alt, 6);
            Assert.False(BoundaryFrame.TryStoredFrameToBodyFixed(Vector3d.zero, b, 0.0, out _, out _, out _));
        }

        [Fact]
        public void SplineLongitude_Tag0IsBodyFixed_Tag1SubtractsThePhase_OtherTagsRefuse()
        {
            Assert.True(BoundaryFrame.TrySplineLongitudeToBodyFixed(0, 190.0, KerbinPeriod, 1000.0, out double lon));
            Assert.Equal(-170.0, lon, 9);
            Assert.True(BoundaryFrame.TrySplineLongitudeToBodyFixed(1, 100.0, KerbinPeriod, KerbinPeriod / 4.0, out lon));
            Assert.Equal(10.0, lon, 9);
            // LiftToInertial leaves the longitude alone for a degenerate period; so does this.
            Assert.True(BoundaryFrame.TrySplineLongitudeToBodyFixed(1, 100.0, 0.0, 5000.0, out lon));
            Assert.Equal(100.0, lon, 9);
            Assert.False(BoundaryFrame.TrySplineLongitudeToBodyFixed(2, 100.0, KerbinPeriod, 0.0, out _));
        }

        [Fact]
        public void SynchronousOrbit_StaysOverOneLongitude()
        {
            // An equatorial prograde orbit whose period is the rotation period hangs over one
            // meridian. A wrong sign or a missing phase term would walk the longitude by
            // 720 * dt / T or 360 * dt / T.
            double a = Math.Pow(KerbinMu * KerbinPeriod * KerbinPeriod / (4.0 * Math.PI * Math.PI), 1.0 / 3.0);
            var seg = new OrbitSegment
            {
                bodyName = "Kerbin", inclination = 0.0, eccentricity = 0.0, semiMajorAxis = a,
                longitudeOfAscendingNode = 0.0, argumentOfPeriapsis = 0.0, meanAnomalyAtEpoch = 0.0,
                epoch = 0.0, startUT = 0.0, endUT = 4.0 * KerbinPeriod,
            };
            Func<string, BoundaryFrame.BodyRotation?> bodies = name => name == "Kerbin" ? KerbinRotation() : (BoundaryFrame.BodyRotation?)null;
            var segs = new List<OrbitSegment> { seg };
            Assert.True(BoundaryFrame.TryOrbitSegmentBodyFixedAtUT(segs, 100.0, bodies, out string body, out double lat0, out double lon0, out double alt0));
            Assert.Equal("Kerbin", body);
            foreach (double t in new[] { 3000.0, KerbinPeriod / 3.0, 2.5 * KerbinPeriod })
            {
                Assert.True(BoundaryFrame.TryOrbitSegmentBodyFixedAtUT(segs, t, bodies, out _, out double lat, out double lon, out double alt));
                Assert.Equal(lat0, lat, 6);
                Assert.Equal(lon0, lon, 6);
                Assert.Equal(alt0, alt, 3);
            }
            Assert.False(BoundaryFrame.TryOrbitSegmentBodyFixedAtUT(segs, 100.0, _ => null, out _, out _, out _, out _));
        }

        [Fact]
        public void StoredFrameFactory_IgnoresPlanetariumZup_AndMatchesTheZupFactoryHeadless()
        {
            // Headlessly Planetarium.Zup is uninitialised and the Zup factory's angle is 0, so
            // both factories build the same conic; only the stored-frame one stays so in game.
            OrbitSegment seg = LkoSegment(0.0, 1000.0);
            Assert.True(TwoBodyOrbit.TryCreateFromSegment(seg, KerbinMu, out TwoBodyOrbit zup));
            Assert.True(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, KerbinMu, out TwoBodyOrbit stored));
            Assert.Equal(40.0 * Math.PI / 180.0, stored.LongitudeOfAscendingNode, 12);
            Assert.True((zup.GetPositionAtUT(321.0) - stored.GetPositionAtUT(321.0)).magnitude < 1e-6);
            seg.eccentricity = 1.0;
            Assert.False(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, KerbinMu, out _));
        }

        // ---- the propagator path -------------------------------------------------------

        [Fact]
        public void BubbleExit_KnownOffset_IsMeasured_AndTheSameAtTwoRebuildClocks()
        {
            Recording rec = BubbleExitRecording("bubble-exit-50m", altOffset: 50.0);
            AnchorCorrection a = RunAndLookup(rec, ClockA, 1, AnchorSide.Start);
            AnchorCorrection b = RunAndLookup(rec, ClockB, 1, AnchorSide.Start);

            Assert.Equal(AnchorSource.BubbleExit, a.Source);
            // reference = the lifted physics sample, smoothed = the conic: +50 m radial.
            Assert.InRange(a.Epsilon.magnitude, 49.9, 50.1);
            Assert.Equal(a.Epsilon.magnitude, b.Epsilon.magnitude, 6);
            ModelBodyFixed(StoredKepler(rec.TrackSections[1].checkpoints[0], BoundaryUT), BoundaryUT,
                out double lat, out double lon, out double alt);
            Vector3d radialAtA = Surface(lat, lon, alt, ClockA).normalized;
            Assert.True(Vector3d.Dot(a.Epsilon.normalized, radialAtA) > 0.9999, "epsilon not radial-up at clock A");
            Assert.Contains(logLines, l => l.Contains("DAG walk summary") && l.Contains("resolvedBubble=1")
                && l.Contains("boundaryInstantMaxEpsilonM=50."));
        }

        [Fact]
        public void BubbleEntry_OnTheConic_IsCentimetres()
        {
            OrbitSegment seg = LkoSegment(BoundaryUT - 600.0, BoundaryUT);
            seg.epoch = BoundaryUT - 600.0;
            var rec = new Recording { RecordingId = "bubble-entry", VesselName = "bubble-entry" };
            rec.TrackSections.Add(CheckpointSection(seg));
            rec.TrackSections.Add(PhysicsSection(FramesOnConic(seg, BoundaryUT, BoundaryUT + 20.0, BoundaryUT, 0.0)));
            AnchorCorrection ac = RunAndLookup(rec, ClockA, 0, AnchorSide.End);
            Assert.Equal(AnchorSource.BubbleEntry, ac.Source);
            Assert.True(ac.Epsilon.magnitude < 0.05, "eps " + ac.Epsilon.magnitude);
        }

        [Fact]
        public void OrbitalCheckpoint_SplineAgainstConic_IsMetres_AndTheSameAtTwoRebuildClocks()
        {
            // ExoBallistic Absolute section -> inertial (tag 1) spline; the candidate sits on its
            // End side, the conic on the checkpoint section that follows.
            OrbitSegment seg = LkoSegment(BoundaryUT, BoundaryUT + 600.0);
            seg.epoch = BoundaryUT - 20.0;
            var rec = new Recording { RecordingId = "orbital-checkpoint", VesselName = "orbital-checkpoint" };
            rec.TrackSections.Add(PhysicsSection(FramesOnConic(seg, BoundaryUT - 20.0, BoundaryUT, BoundaryUT, 0.0)));
            rec.TrackSections.Add(CheckpointSection(seg));

            AnchorCorrection a = RunAndLookup(rec, ClockA, 0, AnchorSide.End);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline(rec.RecordingId, 0, out SmoothingSpline spline));
            Assert.Equal((byte)1, spline.FrameTag);
            AnchorCorrection b = RunAndLookup(rec, ClockB, 0, AnchorSide.End);

            Assert.Equal(AnchorSource.OrbitalCheckpoint, a.Source);
            // Float spline controls: sub-metre, never the rotation chord.
            Assert.True(a.Epsilon.magnitude < 1.0, "eps " + a.Epsilon.magnitude);
            Assert.Equal(a.Epsilon.magnitude, b.Epsilon.magnitude, 6);
            Assert.Contains(logLines, l => l.Contains("DAG walk summary") && l.Contains("resolvedOrb=1"));
        }

        // ---- misses keep the slot at zero and say why -----------------------------------

        private string MissReasonFor(Recording rec, int sectionIndex, AnchorCandidate cand)
        {
            bool ok = AnchorPropagator.TryResolveBoundaryInstantEpsilon(
                rec, sectionIndex, cand, LookupAt(ClockA), out Vector3d eps, out _, out string reason);
            Assert.False(ok);
            Assert.Equal(0.0, eps.magnitude, 12);
            return reason;
        }

        [Fact]
        public void Bubble_SampleOnAnotherBody_IsBodyMismatch()
        {
            Recording rec = BubbleExitRecording("mismatch", 0.0);
            List<TrajectoryPoint> frames = rec.TrackSections[0].frames;
            TrajectoryPoint last = frames[frames.Count - 1];
            last.bodyName = "Mun";
            frames[frames.Count - 1] = last;
            Assert.Equal("body-mismatch", MissReasonFor(rec, 1,
                new AnchorCandidate(BoundaryUT, AnchorSource.BubbleExit, AnchorSide.Start)));
        }

        [Fact]
        public void Bubble_RelativeNeighbour_IsDeferred()
        {
            Recording rec = BubbleExitRecording("relative", 0.0);
            TrackSection phys = rec.TrackSections[0];
            phys.referenceFrame = ReferenceFrame.Relative;
            rec.TrackSections[0] = phys;
            Assert.Equal("relative-section-deferred", MissReasonFor(rec, 1,
                new AnchorCandidate(BoundaryUT, AnchorSource.BubbleExit, AnchorSide.Start)));
        }

        [Fact]
        public void Bubble_EmptyNeighbourFrames_AndNonCheckpointOwner_Miss()
        {
            Recording rec = BubbleExitRecording("frames-empty", 0.0);
            TrackSection phys = rec.TrackSections[0];
            phys.frames = new List<TrajectoryPoint>();
            rec.TrackSections[0] = phys;
            Assert.Equal("frames-empty", MissReasonFor(rec, 1,
                new AnchorCandidate(BoundaryUT, AnchorSource.BubbleExit, AnchorSide.Start)));

            Recording rec2 = BubbleExitRecording("owner-not-checkpoint", 0.0);
            Assert.Equal("no-checkpoint-section", MissReasonFor(rec2, 0,
                new AnchorCandidate(BoundaryUT, AnchorSource.BubbleEntry, AnchorSide.End)));
        }

        [Fact]
        public void Kepler_UnknownBody_IsKeplerUnresolved()
        {
            Recording rec = BubbleExitRecording("unknown-body", 0.0);
            TrackSection ck = rec.TrackSections[1];
            OrbitSegment seg = ck.checkpoints[0];
            seg.bodyName = "Eeloo";
            ck.checkpoints[0] = seg;
            Assert.Equal("kepler-unresolved", MissReasonFor(rec, 1,
                new AnchorCandidate(BoundaryUT, AnchorSource.BubbleExit, AnchorSide.Start)));
        }

        [Fact]
        public void OrbitalCheckpoint_NoSpline_CountsAsDeferredNoSpline()
        {
            OrbitSegment seg = LkoSegment(BoundaryUT, BoundaryUT + 600.0);
            var rec = new Recording { RecordingId = "oc-no-spline", VesselName = "oc-no-spline" };
            rec.TrackSections.Add(PhysicsSection(FramesOnConic(seg, BoundaryUT - 20.0, BoundaryUT, BoundaryUT, 0.0)));
            rec.TrackSections.Add(CheckpointSection(seg));
            SectionAnnotationStore.PutAnchorCandidates(rec.RecordingId, 0, new[]
            {
                new AnchorCandidate(BoundaryUT, AnchorSource.OrbitalCheckpoint, AnchorSide.End),
            });
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "oc-no-spline" },
                new[] { rec }, new RecordingTree[0], LookupAt(ClockA), resolver: null);

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, 0, AnchorSide.End, out AnchorCorrection ac));
            Assert.Equal(0.0, ac.Epsilon.magnitude, 12);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Pipeline-Anchor]") && l.Contains("skip-no-spline")
                && l.Contains("source=OrbitalCheckpoint"));
            Assert.Contains(logLines, l => l.Contains("DAG walk summary") && l.Contains("deferredNoSpline=1")
                && l.Contains("resolvedOrb=0"));
        }
    }
}
