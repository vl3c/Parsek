using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Parsek;
using Parsek.Rendering;
using Parsek.Tests.Analyzer;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// The two residuals of todo REFLY-ANCHOR-EPSILON-1300KM, on SYNTHETIC recordings (no
    /// committed fixture has either shape: every RelativeBoundary anchor in
    /// <c>harness/fixtures/saves</c> is Absolute at the boundary, and the one SoiTransition
    /// candidate, in <c>interbody-route-recorded</c>, sits across a 9.7e6 s gap).
    /// <list type="bullet">
    ///   <item>RelativeBoundary whose anchor is in an OrbitalCheckpoint section: the anchor pose
    ///   was the shared recorder / playback pose (stock <c>Orbit</c> at the boundary UT through
    ///   the CURRENT <c>Planetarium.Zup</c> and body position), the smoothed side a body-fixed
    ///   point placed with the body's CURRENT rotation, so the seed epsilon carried the body's
    ///   rotation since the boundary: <c>2 r cos(lat) |sin(pi (now - t_b) / T)|</c>, about
    ///   962 km a quarter Kerbin day after the boundary in LKO and 1,360 km half a day after.</item>
    ///   <item>SoiTransition: the checkpoint conic around one body through stock <c>Orbit</c>
    ///   (that body's CURRENT position), the spline around the other placed with its CURRENT
    ///   rotation: the Mun's motion plus the frame body's rotation since the boundary.</item>
    /// </list>
    /// Both now compare the two sides at the boundary instant in one frame, so the epsilon is the
    /// designed 3 m (the Absolute samples sit 3 m below the true path) at every rebuild clock.
    ///
    /// <para>
    /// Headless world: stock Kerbin and Mun constants, the Mun on its stock conic around a fixed
    /// Kerbin, and <c>Planetarium.Zup</c> the identity, so stock's world position of a conic is
    /// <c>body.position + rel.xzy</c> and <c>GetWorldSurfacePosition</c> at the rebuild instant is
    /// <see cref="Place"/>. The production resolver gets "alive" bodies (non-zero Unity pointer, so
    /// the shared pose's Unity null test passes and stock <c>Orbit</c> runs headless) and the
    /// placement model through its internal constructor; the propagator and the smoothing pipeline
    /// get uninitialised bodies with the same constants. Unity's <c>Quaternion.LookRotation</c> and
    /// <c>AngleAxis</c> are native calls, so a Harmony prefix routes them to the managed
    /// <see cref="TrajectoryMath.PureLookRotation"/> and an axis-angle transcription for the
    /// duration of each cell. The smoothed side of a RelativeBoundary is read through
    /// <see cref="AnchorPropagator.SmoothedPositionForTesting"/>, which here does what production's
    /// <c>DispatchSplineWorldByFrameTag</c> does: the fitted spline at the UT, lowered and placed
    /// at the rebuild clock.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorEpsilonResidualTests : IDisposable
    {
        private sealed class BodyModel
        {
            public double Radius, Mu, RotationPeriod, InitialRotationDeg;
        }

        private static readonly Dictionary<string, BodyModel> Bodies = new Dictionary<string, BodyModel>(StringComparer.Ordinal)
        {
            { "Kerbin", new BodyModel { Radius = 600000.0, Mu = 3.5316e12, RotationPeriod = 21549.425, InitialRotationDeg = 90.0 } },
            { "Mun", new BodyModel { Radius = 200000.0, Mu = 6.5138398e10, RotationPeriod = 138984.38, InitialRotationDeg = 230.0 } },
        };

        // Kerbin's world position (fixed), and the Mun's stock conic around it.
        private static readonly Vector3d KerbinWorld = new Vector3d(1500.0, -2500.0, 4000.0);
        private static readonly OrbitSegment MunAroundKerbin = new OrbitSegment
        {
            bodyName = "Kerbin",
            semiMajorAxis = 12000000.0,
            eccentricity = 0.0,
            inclination = 0.0,
            longitudeOfAscendingNode = 0.0,
            argumentOfPeriapsis = 0.0,
            meanAnomalyAtEpoch = 1.7,
            epoch = 0.0,
        };

        private const double BoundaryUT = 50000.0;
        private const double DesignedEpsilonM = 3.0;
        private const double SampleStep = 0.5;
        private const int SampleCount = 31;

        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PositionField = typeof(CelestialBody).GetField(
            "_position", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private readonly ITestOutputHelper output;
        private readonly List<string> logLines = new List<string>();
        private readonly Dictionary<string, CelestialBody> propagatorBodies = new Dictionary<string, CelestialBody>(StringComparer.Ordinal);
        private readonly Dictionary<string, CelestialBody> resolverBodies = new Dictionary<string, CelestialBody>(StringComparer.Ordinal);
        private readonly Planetarium.CelestialFrame originalZup;
        private readonly Harmony harmony;
        private const string HarmonyId = "parsek.tests.anchor-epsilon-residuals";

        public AnchorEpsilonResidualTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetStores();
            originalZup = Planetarium.Zup;
            Planetarium.Zup = new Planetarium.CelestialFrame
            {
                X = new Vector3d(1.0, 0.0, 0.0),
                Y = new Vector3d(0.0, 1.0, 0.0),
                Z = new Vector3d(0.0, 0.0, 1.0),
            };

            foreach (var kv in Bodies)
            {
                propagatorBodies[kv.Key] = Configure(TestBodyRegistry.CreateBody(kv.Key, kv.Value.Radius, kv.Value.Mu), kv.Value);
                CelestialBody alive = (CelestialBody)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(CelestialBody));
                CachedPtr.SetValue(alive, new IntPtr(1));
                alive.bodyName = kv.Key;
                alive.Radius = kv.Value.Radius;
                alive.gravParameter = kv.Value.Mu;
                resolverBodies[kv.Key] = Configure(alive, kv.Value);
            }
            // The Mun's own conic, which the SOI comparison reads through orbitDriver.orbit.
            foreach (var set in new[] { propagatorBodies, resolverBodies })
            {
                CelestialBody mun = set["Mun"];
                mun.orbitDriver = (OrbitDriver)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(OrbitDriver));
                Orbit o = (Orbit)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Orbit));
                o.inclination = MunAroundKerbin.inclination;
                o.eccentricity = MunAroundKerbin.eccentricity;
                o.semiMajorAxis = MunAroundKerbin.semiMajorAxis;
                o.LAN = MunAroundKerbin.longitudeOfAscendingNode;
                o.argumentOfPeriapsis = MunAroundKerbin.argumentOfPeriapsis;
                o.meanAnomalyAtEpoch = MunAroundKerbin.meanAnomalyAtEpoch;
                o.epoch = MunAroundKerbin.epoch;
                o.referenceBody = set["Kerbin"];
                mun.orbitDriver.orbit = o;
            }

            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(AnchorEpsilonResidualTests), nameof(LookRotationPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(AnchorEpsilonResidualTests), nameof(LookRotationUpPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(Quaternion), nameof(Quaternion.AngleAxis), new[] { typeof(float), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(AnchorEpsilonResidualTests), nameof(AngleAxisPrefix)));
        }

        public void Dispose()
        {
            harmony.UnpatchAll(HarmonyId);
            Planetarium.Zup = originalZup;
            ResetStores();
            ParsekLog.ResetTestOverrides();
        }

        private static void ResetStores()
        {
            RecordingStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
        }

        private static CelestialBody Configure(CelestialBody body, BodyModel m)
        {
            body.rotationPeriod = m.RotationPeriod;
            body.initialRotation = m.InitialRotationDeg;
            body.rotates = true;
            return body;
        }

        // ---- Unity's native quaternion calls, managed ------------------------------------

        private static bool LookRotationPrefix(Vector3 __0, Vector3 __1, ref Quaternion __result)
        {
            __result = TrajectoryMath.PureLookRotation(__0, __1);
            return false;
        }

        private static bool LookRotationUpPrefix(Vector3 __0, ref Quaternion __result)
        {
            __result = TrajectoryMath.PureLookRotation(__0, Vector3.up);
            return false;
        }

        private static bool AngleAxisPrefix(float __0, Vector3 __1, ref Quaternion __result)
        {
            Vector3 axis = __1.normalized;
            double half = __0 * Math.PI / 360.0;
            float s = (float)Math.Sin(half);
            __result = new Quaternion(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(half));
            return false;
        }

        // ---- the world model ---------------------------------------------------------------

        private static Vector3d Swizzle(Vector3d v) => new Vector3d(v.x, v.z, v.y);

        private static double RotationAngleDeg(string body, double ut)
        {
            BodyModel b = Bodies[body];
            return b.InitialRotationDeg + 360.0 * ut / b.RotationPeriod;
        }

        private static Vector3d StoredKepler(OrbitSegment seg, double ut)
        {
            Assert.True(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, Bodies[seg.bodyName].Mu, out TwoBodyOrbit orbit));
            return orbit.GetPositionAtUT(ut);
        }

        /// <summary>The body's position relative to Kerbin in the stored element frame.</summary>
        private static Vector3d StoredBodyPosition(string body, double ut)
        {
            return body == "Mun" ? StoredKepler(MunAroundKerbin, ut) : Vector3d.zero;
        }

        private static Vector3d BodyWorld(string body, double nowUT) => KerbinWorld + Swizzle(StoredBodyPosition(body, nowUT));

        /// <summary><c>body.GetWorldSurfacePosition</c> with the world as it stands at <paramref name="nowUT"/>.</summary>
        private static Vector3d Place(string body, double latDeg, double lonDeg, double altM, double nowUT)
        {
            double r = Bodies[body].Radius + altM;
            double lon = (lonDeg + RotationAngleDeg(body, nowUT)) * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            Vector3d zUp = new Vector3d(r * Math.Cos(lat) * Math.Cos(lon), r * Math.Cos(lat) * Math.Sin(lon), r * Math.Sin(lat));
            return BodyWorld(body, nowUT) + Swizzle(zUp);
        }

        /// <summary>A stored-frame position relative to <paramref name="body"/>, as body-fixed lat / lon / alt at <paramref name="ut"/>.</summary>
        private static void Lower(string body, Vector3d stored, double ut, out double lat, out double lon, out double alt)
        {
            double r = stored.magnitude;
            lat = Math.Asin(stored.z / r) * 180.0 / Math.PI;
            lon = TrajectoryMath.BoundaryFrame.WrapLongitudeDeg(
                Math.Atan2(stored.y, stored.x) * 180.0 / Math.PI - RotationAngleDeg(body, ut));
            alt = r - Bodies[body].Radius;
        }

        private void SetClock(double nowUT)
        {
            foreach (var kv in resolverBodies)
                PositionField.SetValue(kv.Value, BodyWorld(kv.Key, nowUT));
        }

        private CelestialBody PropagatorBody(string name) =>
            name != null && propagatorBodies.TryGetValue(name, out CelestialBody b) ? b : null;

        private CelestialBody ResolverBody(string name) =>
            name != null && resolverBodies.TryGetValue(name, out CelestialBody b) ? b : null;

        private ProductionAnchorWorldFrameResolver ResolverAt(double nowUT)
        {
            return new ProductionAnchorWorldFrameResolver(
                ResolverBody,
                p => Place(p.bodyName, p.latitude, p.longitude, p.altitude, nowUT),
                p => Quaternion.identity);
        }

        /// <summary>
        /// Absolute frames along <paramref name="storedPath"/> (stored-frame positions relative to
        /// <paramref name="body"/>), lowered at each sample's own UT and
        /// <see cref="DesignedEpsilonM"/> below it.
        /// </summary>
        private static List<TrajectoryPoint> AbsoluteFrames(string body, double startUT, Func<double, Vector3d> storedPath)
        {
            var frames = new List<TrajectoryPoint>();
            for (int i = 0; i < SampleCount; i++)
            {
                double t = startUT + i * SampleStep;
                Lower(body, storedPath(t), t, out double lat, out double lon, out double alt);
                frames.Add(new TrajectoryPoint
                {
                    ut = t,
                    bodyName = body,
                    latitude = lat,
                    longitude = lon,
                    altitude = alt - DesignedEpsilonM,
                    rotation = Quaternion.identity,
                });
            }
            return frames;
        }

        private static TrackSection Absolute(List<TrajectoryPoint> frames)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Active,
                startUT = frames[0].ut,
                endUT = frames[frames.Count - 1].ut,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
            };
        }

        private static TrackSection Checkpoint(OrbitSegment seg)
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

        private static OrbitSegment Window(OrbitSegment seg, double startUT, double endUT)
        {
            seg.startUT = startUT;
            seg.endUT = endUT;
            return seg;
        }

        /// <summary>
        /// The production rebuild with the world as it stands at <paramref name="nowUT"/>: fit and
        /// emit candidates, then run the propagator with the production resolver. Returns the
        /// anchor written to (<paramref name="rec"/>, <paramref name="section"/>, <paramref name="side"/>).
        /// </summary>
        private AnchorCorrection RebuildAt(
            double nowUT, RecordingTree tree, IReadOnlyList<Recording> recordings,
            Recording rec, int section, AnchorSide side)
        {
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            SmoothingPipeline.BodyResolverForTesting = PropagatorBody;
            SmoothingPipeline.TreeResolverForTesting = id => tree;
            AnchorPropagator.BodyResolverForTesting = PropagatorBody;
            AnchorPropagator.SmoothedPositionForTesting = (r, s, ut) =>
            {
                if (!SectionAnnotationStore.TryGetSmoothingSpline(r.RecordingId, s, out SmoothingSpline spline) || !spline.IsValid)
                    return null;
                string body = r.TrackSections[s].frames[0].bodyName;
                Vector3d p = TrajectoryMath.CatmullRomFit.Evaluate(spline, ut);
                if (!TrajectoryMath.BoundaryFrame.TrySplineLongitudeToBodyFixed(
                        spline.FrameTag, p.y, Bodies[body].RotationPeriod, ut, out double lon))
                    return null;
                return Place(body, p.x, lon, p.z, nowUT);
            };
            SetClock(nowUT);

            bool prior = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try
            {
                foreach (Recording r in recordings)
                    SmoothingPipeline.FitAndStorePerSection(r);
            }
            finally { ParsekLog.SuppressLogging = prior; }

            ParsekLog.SuppressLogging = false;
            logLines.Clear();
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "anchor-residuals" },
                recordings, new[] { tree },
                surfaceLookup: (name, lat, lon, alt) => Place(name, lat, lon, alt, nowUT),
                resolver: ResolverAt(nowUT));

            Assert.True(RenderSessionState.TryLookup(rec.RecordingId, section, side, out AnchorCorrection ac),
                "no anchor at " + rec.RecordingId + "|" + section + "|" + side + "; log:\n" + string.Join("\n", logLines));
            return ac;
        }

        private static string Fmt(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static readonly double[] ClockOffsets = { 0.0, 0.25, 0.5 };

        // ---- residual (a): RelativeBoundary with an orbital-checkpoint anchor ---------------

        // LKO anchor around Kerbin, with a recorded attitude so the offset rides the anchor rotation.
        private static readonly OrbitSegment AnchorConic = new OrbitSegment
        {
            bodyName = "Kerbin",
            semiMajorAxis = 680000.0,
            eccentricity = 0.001,
            inclination = 6.0,
            longitudeOfAscendingNode = 40.0,
            argumentOfPeriapsis = 30.0,
            meanAnomalyAtEpoch = 0.4,
            epoch = 0.0,
            orbitalFrameRotation = new Quaternion(0.1825742f, 0.3651484f, 0.5477226f, 0.7302967f),
        };

        private static readonly Vector3 AnchorLocalOffset = new Vector3(30f, -20f, 12f);

        private Recording BuildRelativeBoundaryPair(AnchorSide side, out RecordingTree tree, out Recording focus, out int absIndex)
        {
            tree = new RecordingTree { Id = "tree-relative-boundary" };
            var anchor = new Recording
            {
                RecordingId = "anchor-orbital",
                TreeId = tree.Id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            anchor.TrackSections.Add(Checkpoint(Window(AnchorConic, BoundaryUT - 600.0, BoundaryUT + 600.0)));

            // The focus's true position at the boundary: the anchor pose there (the shared pose
            // at its own instant is the recorder's truth) plus the anchor-local offset.
            SetClock(BoundaryUT);
            Assert.True(ParsekFlight.TryComputeOrbitalCheckpointAnchorPose(
                anchor, AnchorConic, resolverBodies["Kerbin"], 0, BoundaryUT, out AnchorPose truthPose));
            Vector3d truthWorld = truthPose.WorldPos + (Vector3d)(truthPose.WorldRotation * AnchorLocalOffset);
            Vector3d storedOffset = Swizzle(truthWorld - KerbinWorld) - StoredKepler(AnchorConic, BoundaryUT);
            Func<double, Vector3d> focusPath = t => StoredKepler(AnchorConic, t) + storedOffset;

            focus = new Recording
            {
                RecordingId = "focus-relative",
                TreeId = tree.Id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            var relFrames = new List<TrajectoryPoint>();
            double relStart = side == AnchorSide.End ? BoundaryUT : BoundaryUT - 20.0;
            for (int i = 0; i <= 10; i++)
                relFrames.Add(new TrajectoryPoint
                {
                    ut = relStart + i * 2.0,
                    bodyName = "Kerbin",
                    latitude = AnchorLocalOffset.x,
                    longitude = AnchorLocalOffset.y,
                    altitude = AnchorLocalOffset.z,
                    rotation = Quaternion.identity,
                });
            var rel = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Active,
                startUT = relStart,
                endUT = relStart + 20.0,
                anchorRecordingId = anchor.RecordingId,
                frames = relFrames,
                checkpoints = new List<OrbitSegment>(),
            };
            if (side == AnchorSide.End)
            {
                // Absolute -> Relative: the Absolute section ends at the boundary.
                focus.TrackSections.Add(Absolute(AbsoluteFrames("Kerbin", BoundaryUT - (SampleCount - 1) * SampleStep, focusPath)));
                focus.TrackSections.Add(rel);
                absIndex = 0;
            }
            else
            {
                focus.TrackSections.Add(rel);
                focus.TrackSections.Add(Absolute(AbsoluteFrames("Kerbin", BoundaryUT, focusPath)));
                absIndex = 1;
            }
            tree.AddOrReplaceRecording(anchor);
            tree.AddOrReplaceRecording(focus);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return anchor;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RelativeBoundary_OrbitalAnchor_EpsilonIsTheDesignedMetresAtEveryRebuildClock(bool absoluteThenRelative)
        {
            AnchorSide side = absoluteThenRelative ? AnchorSide.End : AnchorSide.Start;
            Recording anchor = BuildRelativeBoundaryPair(side, out RecordingTree tree, out Recording focus, out int absIndex);
            var recordings = new List<Recording> { anchor, focus };
            double period = Bodies["Kerbin"].RotationPeriod;

            var magnitudes = new List<double>();
            Vector3d? bodyFixedReference = null;
            foreach (double f in ClockOffsets)
            {
                double now = BoundaryUT + f * period;
                AnchorCorrection ac = RebuildAt(now, tree, recordings, focus, absIndex, side);
                Assert.Equal(AnchorSource.RelativeBoundary, ac.Source);
                double m = ac.Epsilon.magnitude;
                magnitudes.Add(m);
                output.WriteLine("side=" + side + " clock=boundary+" + Fmt(f) + "T |eps|=" + Fmt(m) + " m");

                // The epsilon is a world vector at the rebuild: turned back by the body's rotation
                // since the boundary it is the same body-fixed vector at every clock.
                Vector3d bodyFixed = UnrotateWorld("Kerbin", ac.Epsilon, now);
                if (bodyFixedReference.HasValue)
                    Assert.True((bodyFixed - bodyFixedReference.Value).magnitude < 0.05,
                        "body-fixed epsilon moved with the rebuild clock: " + Fmt((bodyFixed - bodyFixedReference.Value).magnitude) + " m");
                else
                    bodyFixedReference = bodyFixed;
            }
            foreach (double m in magnitudes)
                Assert.InRange(m, DesignedEpsilonM - 0.5, DesignedEpsilonM + 0.5);
        }

        /// <summary>A world vector at <paramref name="nowUT"/> expressed on the body's own axes.</summary>
        private static Vector3d UnrotateWorld(string body, Vector3d world, double nowUT)
        {
            Vector3d zUp = Swizzle(world);
            double a = -RotationAngleDeg(body, nowUT) * Math.PI / 180.0;
            return new Vector3d(
                zUp.x * Math.Cos(a) - zUp.y * Math.Sin(a),
                zUp.x * Math.Sin(a) + zUp.y * Math.Cos(a),
                zUp.z);
        }

        [Fact]
        public void BoundaryInstantOrbitalPose_AtTheCurrentInstant_IsTheSharedPose()
        {
            // Parity with the recorders' and playback's shared pose when the boundary is now, on
            // a non-spinning attitude and a spinning one.
            var spinning = AnchorConic;
            spinning.angularVelocity = new Vector3(0.06f, -0.03f, 0.09f);
            spinning.startUT = BoundaryUT - 100.0;
            spinning.endUT = BoundaryUT + 100.0;
            int spinningSeen = 0;
            foreach (OrbitSegment seg in new[] { Window(AnchorConic, BoundaryUT - 100.0, BoundaryUT + 100.0), spinning })
            {
                if (TrajectoryMath.IsSpinning(seg)) spinningSeen++;
                SetClock(BoundaryUT);
                Assert.True(ParsekFlight.TryComputeOrbitalCheckpointAnchorPose(
                    null, seg, resolverBodies["Kerbin"], 3, BoundaryUT, out AnchorPose shared));
                CelestialBody kerbin = propagatorBodies["Kerbin"];
                Assert.True(ProductionAnchorWorldFrameResolver.TryComputeBoundaryInstantOrbitalAnchorPose(
                    null, seg, AnchorPropagator.ReadBodyRotationCore(kerbin),
                    p => Place(p.bodyName, p.latitude, p.longitude, p.altitude, BoundaryUT),
                    3, BoundaryUT, out AnchorPose instant));
                double dPos = (instant.WorldPos - shared.WorldPos).magnitude;
                double dot = Math.Abs(Quaternion.Dot(instant.WorldRotation, shared.WorldRotation));
                output.WriteLine("spin=" + TrajectoryMath.IsSpinning(seg) + " dPos=" + Fmt(dPos) + " m dot=" + dot.ToString("R", CultureInfo.InvariantCulture));
                Assert.True(dPos < 0.01, "position differs by " + Fmt(dPos) + " m");
                Assert.True(dot > 0.99999, "rotation differs: dot=" + dot.ToString("R", CultureInfo.InvariantCulture));
                Assert.Equal(3, instant.ResolvedSectionIndex);
            }
            Assert.Equal(1, spinningSeen);
        }

        // ---- residual (b): SoiTransition ---------------------------------------------------

        private static readonly OrbitSegment ConicAroundMun = new OrbitSegment
        {
            bodyName = "Mun",
            semiMajorAxis = 2000000.0,
            eccentricity = 0.3,
            inclination = 10.0,
            longitudeOfAscendingNode = 20.0,
            argumentOfPeriapsis = 50.0,
            meanAnomalyAtEpoch = 0.2,
            epoch = 0.0,
        };

        private static readonly OrbitSegment ConicAroundKerbin = new OrbitSegment
        {
            bodyName = "Kerbin",
            semiMajorAxis = 11000000.0,
            eccentricity = 0.2,
            inclination = 3.0,
            longitudeOfAscendingNode = 10.0,
            argumentOfPeriapsis = 0.0,
            meanAnomalyAtEpoch = 1.5,
            epoch = 0.0,
        };

        /// <summary>
        /// One recording crossing between Kerbin and the Mun at the boundary: the checkpoint conic
        /// around <paramref name="checkpointBody"/>, the Absolute samples the same path seen from
        /// <paramref name="absoluteBody"/> (3 m below it). End = Absolute then checkpoint; Start =
        /// checkpoint then Absolute.
        /// </summary>
        private Recording BuildSoiCrossing(string absoluteBody, string checkpointBody, AnchorSide side,
            out RecordingTree tree, out int absIndex)
        {
            OrbitSegment conic = checkpointBody == "Mun" ? ConicAroundMun : ConicAroundKerbin;
            Func<double, Vector3d> pathSeenFromAbsolute = t =>
                StoredKepler(conic, t) + StoredBodyPosition(checkpointBody, t) - StoredBodyPosition(absoluteBody, t);
            tree = new RecordingTree { Id = "tree-soi" };
            var rec = new Recording
            {
                RecordingId = "soi-crossing",
                TreeId = tree.Id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            if (side == AnchorSide.End)
            {
                rec.TrackSections.Add(Absolute(AbsoluteFrames(absoluteBody, BoundaryUT - (SampleCount - 1) * SampleStep, pathSeenFromAbsolute)));
                rec.TrackSections.Add(Checkpoint(Window(conic, BoundaryUT, BoundaryUT + 3000.0)));
                absIndex = 0;
            }
            else
            {
                rec.TrackSections.Add(Checkpoint(Window(conic, BoundaryUT - 3000.0, BoundaryUT)));
                rec.TrackSections.Add(Absolute(AbsoluteFrames(absoluteBody, BoundaryUT, pathSeenFromAbsolute)));
                absIndex = 1;
            }
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return rec;
        }

        [Theory]
        [InlineData("Kerbin", "Mun", true)]
        [InlineData("Mun", "Kerbin", true)]
        [InlineData("Kerbin", "Mun", false)]
        [InlineData("Mun", "Kerbin", false)]
        public void SoiTransition_EpsilonIsTheDesignedMetresAtEveryRebuildClock(string absoluteBody, string checkpointBody, bool absoluteThenCheckpoint)
        {
            AnchorSide side = absoluteThenCheckpoint ? AnchorSide.End : AnchorSide.Start;
            Recording rec = BuildSoiCrossing(absoluteBody, checkpointBody, side, out RecordingTree tree, out int absIndex);
            var recordings = new List<Recording> { rec };
            double period = Bodies[absoluteBody].RotationPeriod;

            var magnitudes = new List<double>();
            Vector3d? bodyFixedReference = null;
            foreach (double f in ClockOffsets)
            {
                double now = BoundaryUT + f * period;
                AnchorCorrection ac = RebuildAt(now, tree, recordings, rec, absIndex, side);
                Assert.Equal(AnchorSource.SoiTransition, ac.Source);
                double m = ac.Epsilon.magnitude;
                magnitudes.Add(m);
                output.WriteLine(absoluteBody + "->" + checkpointBody + " side=" + side + " clock=boundary+" + Fmt(f) + "T |eps|=" + Fmt(m) + " m");
                Vector3d bodyFixed = UnrotateWorld(absoluteBody, ac.Epsilon, now);
                if (bodyFixedReference.HasValue)
                    Assert.True((bodyFixed - bodyFixedReference.Value).magnitude < 0.05,
                        "body-fixed epsilon moved with the rebuild clock: " + Fmt((bodyFixed - bodyFixedReference.Value).magnitude) + " m");
                else
                    bodyFixedReference = bodyFixed;
            }
            foreach (double m in magnitudes)
                Assert.InRange(m, DesignedEpsilonM - 0.5, DesignedEpsilonM + 0.5);
        }

        [Fact]
        public void SoiTransition_TheConicCarriedIntoTheOtherBodyIsTheTruePath()
        {
            // The frame carry on its own: the Mun-relative conic carried into Kerbin's body-fixed
            // frame at t equals the same instant's Kerbin-relative position, lowered at t; and the
            // reverse carry inverts it.
            double t = BoundaryUT + 1234.5;
            Func<string, TrajectoryMath.BoundaryFrame.BodyRotation?> rotations = name =>
                Bodies.ContainsKey(name) ? AnchorPropagator.ReadBodyRotationCore(propagatorBodies[name]) : (TrajectoryMath.BoundaryFrame.BodyRotation?)null;
            Func<string, TrajectoryMath.BoundaryFrame.BodyOrbitLink?> links = name =>
            {
                if (name == "Kerbin") return new TrajectoryMath.BoundaryFrame.BodyOrbitLink();
                if (name == "Mun") return new TrajectoryMath.BoundaryFrame.BodyOrbitLink
                {
                    ParentName = "Kerbin",
                    ParentGravParameter = Bodies["Kerbin"].Mu,
                    Elements = MunAroundKerbin,
                };
                return null;
            };
            var segs = new List<OrbitSegment> { Window(ConicAroundMun, t - 10.0, t + 10.0) };
            Assert.True(TrajectoryMath.BoundaryFrame.TryOrbitSegmentInBodyFixedOfAtUT(
                segs, t, "Kerbin", rotations, links, out string segBody, out double lat, out double lon, out double alt));
            Assert.Equal("Mun", segBody);
            Lower("Kerbin", StoredKepler(ConicAroundMun, t) + StoredBodyPosition("Mun", t), t,
                out double eLat, out double eLon, out double eAlt);
            double err = (TrajectoryMath.BoundaryFrame.BodyFixedCartesian(lat, lon, alt, 600000.0)
                - TrajectoryMath.BoundaryFrame.BodyFixedCartesian(eLat, eLon, eAlt, 600000.0)).magnitude;
            Assert.True(err < 1e-3, "carry error " + err.ToString("R", CultureInfo.InvariantCulture) + " m");

            // No common root: refused.
            Func<string, TrajectoryMath.BoundaryFrame.BodyOrbitLink?> orphan = name =>
                name == "Mun" ? new TrajectoryMath.BoundaryFrame.BodyOrbitLink() : links(name);
            Assert.False(TrajectoryMath.BoundaryFrame.TryOrbitSegmentInBodyFixedOfAtUT(
                segs, t, "Kerbin", rotations, orphan, out _, out _, out _, out _));
        }
    
        // ---- the one committed SoiTransition candidate --------------------------------------

        [Fact]
        public void InterbodyFixture_TheOnlySoiCandidateSpansANineMillionSecondGap()
        {
            // PINS A SUSPECTED CANDIDATE-BUILDER DEFECT (todo REFLY-ANCHOR-EPSILON-1300KM, item 5):
            // the SOI test compares body names across adjacent sections, not time adjacency, so
            // the Kerbal X's ascent (Absolute on Kerbin, ends at 42874302) and its first Duna
            // checkpoint (starts at 52566541, 9.69e6 s later) make a SoiTransition candidate at the
            // checkpoint's start. It is inert today (the ascent section is Atmospheric, so it has
            // no spline and the propagator defers it), but a fit on that section would compare a
            // spline 9.69e6 s past its last sample. When the builder learns adjacency, flip this.
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves", "interbody-route-recorded"));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "persistent.sfs")), "fixture missing: " + dir);
            AnalyzerModel model;
            bool prior = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try { model = SaveDirectoryLoader.Load(dir, _ => null); }
            finally { ParsekLog.SuppressLogging = prior; }

            var soi = new List<string>();
            foreach (Recording rec in model.Recordings)
            {
                if (rec?.TrackSections == null) continue;
                RecordingTree recTree = null;
                foreach (RecordingTree t in model.Trees)
                    if (t?.Recordings != null && t.Recordings.ContainsKey(rec.RecordingId)) { recTree = t; break; }
                foreach (var kv in AnchorCandidateBuilder.Compute(rec, recTree))
                    foreach (AnchorCandidate c in kv.Value)
                        if (c.Source == AnchorSource.SoiTransition)
                        {
                            TrackSection abs = rec.TrackSections[kv.Key];
                            int ckIdx = c.Side == AnchorSide.End ? kv.Key + 1 : kv.Key - 1;
                            TrackSection ck = rec.TrackSections[ckIdx];
                            double gap = c.Side == AnchorSide.End ? ck.startUT - abs.endUT : abs.startUT - ck.endUT;
                            soi.Add(rec.RecordingId + "|" + kv.Key + "|" + c.Side + "|" + abs.environment
                                + "|gap=" + Math.Round(gap).ToString(CultureInfo.InvariantCulture));
                        }
            }
            Assert.Equal(new[] { "ffffab0a00b743a89ae829aa3c83cc92|4|End|Atmospheric|gap=9692239" }, soi.ToArray());
        }
    }
}
