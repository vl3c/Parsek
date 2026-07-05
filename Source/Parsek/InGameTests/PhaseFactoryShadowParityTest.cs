using System.Collections.Generic;
using System.Globalization;
using Parsek.Display;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 2 / migration §4 - the SHADOW-PARITY in-game gate: over a set of regression-representative
    // recordings, the new PhaseFactory's emitted GEOMETRY must byte-match the live ChainAssembler's
    // GhostRenderChain, so ZERO factory-parity anomalies fire. This is the Phase-2 gate to Phase 3.
    //
    // It runs the SAME shadow path the production hook runs (ShadowRenderDriver.GetOrBuildChain calls
    // ChainAssembler.Build then, under MapRenderTrace.IsEnabled, builds the PhaseFactory chain + compares),
    // but exercised directly over real Recording objects (which ARE IPlaybackTrajectory) with the
    // FlightGlobals-backed body-surface provider, so the below-surface descent split and the body radii
    // are the REAL ones (not a synthetic null surface as in the headless unit tests). For each scenario it
    // asserts GeometryParityComparator.Compare matches AND (with the tracer forced on, sink captured) that
    // no reason=factory-parity line was emitted - the same emit decision the production hook makes.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics). Career-independent; FLIGHT scene (needs
    // FlightGlobals.Bodies for the surface provider). Restores the committed store + tracer flag in finally.
    public class PhaseFactoryShadowParityTest
    {
        private const string Kerbin = "Kerbin";
        private const string Mun = "Mun";
        private const string Sun = "Sun";

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Phase 2 shadow-parity: the PhaseFactory's emitted geometry byte-matches the "
                + "ChainAssembler over the regression scenarios (zero factory-parity anomalies) - the gate "
                + "to Phase 3")]
        public void ShadowParity_RegressionScenarios_ZeroFactoryParityAnomalies()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == Kerbin);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            // Real FlightGlobals-backed body radius provider (mirrors GhostMapSceneBase.ResolveBodySurface).
            GhostTrajectoryPolylineRenderer.BodySurfaceProvider surface =
                (string bodyName, out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info) =>
                {
                    info = default(GhostTrajectoryPolylineRenderer.BodySurfaceInfo);
                    if (string.IsNullOrEmpty(bodyName))
                        return false;
                    CelestialBody b = FlightGlobals.GetBodyByName(bodyName);
                    if (b == null)
                        return false;
                    info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo { radius = b.Radius };
                    return true;
                };

            double kerbinR = kerbin.Radius;
            var scenarios = BuildScenarios(kerbinR);

            bool prevForceEnabled = MapRenderTrace.ForceEnabledForTesting;
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;
            var captured = new List<string>();

            try
            {
                MapRenderTrace.ForceEnabledForTesting = true;
                ParsekLog.SuppressLogging = false;
                ParsekLog.TestSinkForTesting = line => captured.Add(line);

                for (int i = 0; i < scenarios.Count; i++)
                {
                    Scenario sc = scenarios[i];

                    GhostRenderChain assembler = ChainAssembler.Build(
                        sc.Traj, committedIndex: i, instanceKey: 0, sc.WindowStart, sc.WindowEnd,
                        faithfulFallback: false, surface: surface,
                        orbitSegmentsOverride: sc.Override, reaimAncestorBody: sc.Ancestor);

                    PhaseChain factory = PhaseFactory.BuildPhaseChain(
                        sc.Traj, committedIndex: i, instanceKey: 0, sc.WindowStart, sc.WindowEnd,
                        faithfulFallback: false, surface: surface,
                        orbitSegmentsOverride: sc.Override, reaimAncestorBody: sc.Ancestor);

                    GeometryParityComparator.ParityResult result =
                        GeometryParityComparator.Compare(factory, assembler);

                    ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                        "ShadowParity scenario={0} assemblerSegs={1} factoryPhases={2} match={3} {4}",
                        sc.Name, assembler.SegmentCount, factory.PhaseCount, result.IsMatch,
                        result.IsMatch ? string.Empty : result.ToString()));

                    // Mirror the production hook's emit decision: a mismatch would fire factory-parity.
                    if (!result.IsMatch)
                        MapRenderTrace.EmitFactoryParity(
                            sc.Traj.RecordingId, sc.WindowStart,
                            string.Format(CultureInfo.InvariantCulture,
                                "diverging={0} seg={1} countMismatch={2} {3}",
                                result.DivergingField, result.SegmentIndex, result.CountMismatch,
                                result.Detail ?? string.Empty));

                    InGameAssert.IsTrue(result.IsMatch,
                        "Scenario '" + sc.Name + "' geometry diverged: " + result);
                }

                // The end-to-end gate: NO factory-parity anomaly fired across the regression set.
                foreach (string line in captured)
                    InGameAssert.IsFalse(
                        line.Contains("reason=" + MapRenderTrace.AnomalyFactoryParity),
                        "Phase-2 shadow gate: a factory-parity anomaly fired; saw: " + line);
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForceEnabled;
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevSuppress;
            }
        }

        private struct Scenario
        {
            public string Name;
            public Recording Traj;
            public double WindowStart;
            public double WindowEnd;
            public IReadOnlyList<OrbitSegment> Override;
            public string Ancestor;
        }

        private static List<Scenario> BuildScenarios(double kerbinR)
        {
            var list = new List<Scenario>();

            // (1) Faithful ascent -> Kerbin parking orbit -> Mun arrival (the canonical full chain).
            {
                var rec = NewRec("shadow-fullchain");
                AddPoints(rec, Kerbin, 0, 8, 5);   // ascent
                AddPoints(rec, Mun, 320, 360, 3);  // arrival traced
                rec.OrbitSegments.Add(Orbit(Kerbin, 10, 300, kerbinR + 100000, 0.0));
                rec.TrackSections.Add(Sec(0, 8, SegmentEnvironment.Atmospheric));
                rec.TrackSections.Add(Sec(10, 300, SegmentEnvironment.ExoBallistic));
                rec.TrackSections.Add(Sec(320, 360, SegmentEnvironment.Approach));
                list.Add(new Scenario { Name = "faithful-full-chain", Traj = rec, WindowStart = 0, WindowEnd = 400 });
            }

            // (2) Below-atmosphere descent: an orbit whose periapsis dips below Kerbin's surface must fall
            // to TracedPath (FIX #27) - exercises the real body radius in the cover predicate.
            {
                var rec = NewRec("shadow-descent");
                AddPoints(rec, Kerbin, 12, 28, 5);
                rec.OrbitSegments.Add(Orbit(Kerbin, 10, 30, kerbinR - 100000, 0.0)); // peri below surface
                rec.TrackSections.Add(Sec(12, 28, SegmentEnvironment.Atmospheric));
                list.Add(new Scenario { Name = "below-surface-descent", Traj = rec, WindowStart = 0, WindowEnd = 40 });
            }

            // (3) BG on-rails: orbit-bridge OrbitSegments, NO env-class TrackSections, NO points. A
            // Kerbin->Sun body change must produce an all-orbital chain (FlexibleSoi seam, no descent).
            {
                var rec = NewRec("shadow-bg-onrails");
                rec.OrbitSegments.Add(Orbit(Kerbin, 10, 1000, kerbinR + 200000, 0.6));
                rec.OrbitSegments.Add(Orbit(Sun, 1000, 5000, 13_000_000_000.0, 0.1));
                list.Add(new Scenario { Name = "bg-on-rails-all-orbital", Traj = rec, WindowStart = 0, WindowEnd = 6000 });
            }

            // (4) Single-recording empty Points: conic-only chain.
            {
                var rec = NewRec("shadow-conic-only");
                rec.OrbitSegments.Add(Orbit(Kerbin, 10, 300, kerbinR + 80000, 0.0));
                list.Add(new Scenario { Name = "single-recording-conic-only", Traj = rec, WindowStart = 0, WindowEnd = 400 });
            }

            // (5) Re-aimed member: a recorded Sun heliocentric leg replaced by a reference-distinct re-aimed
            // override (different sma); the override conic must flow through both paths identically.
            {
                var rec = NewRec("shadow-reaim");
                AddPoints(rec, Kerbin, 0, 6, 4);
                AddPoints(rec, Mun, 320, 340, 3);
                rec.OrbitSegments.Add(Orbit(Sun, 10, 300, 9_000_000_000.0, 0.2));
                var over = new List<OrbitSegment>
                {
                    Orbit(Sun, 10, 300, 7_777_000_000.0, 0.5, isPredicted: false),
                };
                list.Add(new Scenario
                {
                    Name = "reaimed-heliocentric-leg",
                    Traj = rec, WindowStart = 0, WindowEnd = 400, Override = over, Ancestor = Sun,
                });
            }

            // (6) Fully empty recording: empty chain on both sides.
            {
                var rec = NewRec("shadow-empty");
                list.Add(new Scenario { Name = "fully-empty", Traj = rec, WindowStart = 0, WindowEnd = 40 });
            }

            // (7) Faithful parent-anchored controlled-decoupled CHILD (IsDebris=false,
            // ParentAnchorRecordingId set) with a TracedPath leg near the parent + a conic. The child gets a
            // ParentAnchoredChild anchor (NOT a BodyAnchor), so the traced leg has no BodyAnchor payload to
            // resolve its frame body from; the factory must reproduce the assembler-stamped GEOMETRY body
            // name losslessly anyway (the FrameBodyName round-trip fix), or factory-parity FALSE-fires on a
            // correct factory. Regression-set member for the zero-factory-parity gate (it would have caught
            // the bug); the headless byte-parity proof is PhaseFactoryTests.
            {
                var rec = NewRec("shadow-faithful-child");
                rec.IsDebris = false;
                rec.ParentAnchorRecordingId = "shadow-parent-anchor";
                AddPoints(rec, Mun, 0, 6, 4);                 // traced leg near the parent (Mun)
                rec.OrbitSegments.Add(Orbit(Mun, 10, 30, 250000.0, 0.0));
                rec.TrackSections.Add(Sec(0, 6, SegmentEnvironment.Atmospheric));
                rec.TrackSections.Add(Sec(10, 30, SegmentEnvironment.ExoBallistic));
                list.Add(new Scenario { Name = "faithful-parent-anchored-child", Traj = rec, WindowStart = 0, WindowEnd = 40 });
            }

            return list;
        }

        private static Recording NewRec(string label)
            => new Recording
            {
                RecordingId = label + "-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek " + label,
                PlaybackEnabled = true,
            };

        private static void AddPoints(Recording rec, string body, double startUT, double endUT, int n)
        {
            if (n < 2) n = 2;
            for (int i = 0; i < n; i++)
            {
                double t = startUT + (endUT - startUT) * i / (n - 1);
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = t,
                    latitude = 0.0,
                    longitude = i,
                    altitude = 1000.0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = body,
                });
            }
        }

        private static OrbitSegment Orbit(string body, double s, double e, double sma, double ecc,
            bool isPredicted = false)
            => new OrbitSegment
            {
                startUT = s, endUT = e, bodyName = body,
                semiMajorAxis = sma, eccentricity = ecc, epoch = s, isPredicted = isPredicted,
                orbitalFrameRotation = Quaternion.identity, angularVelocity = Vector3.zero,
            };

        private static TrackSection Sec(double s, double e, SegmentEnvironment env)
            => new TrackSection { startUT = s, endUT = e, environment = env };
    }
}
