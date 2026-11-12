using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Rendering;
using Parsek.Tests.Analyzer;
using Xunit;
using Xunit.Abstractions;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// CHARACTERIZATION of todo REFLY-ANCHOR-EPSILON-1300KM: pins CURRENT behaviour, which is
    /// the defect. It passes today and is meant to be flipped (assertions 2 and 4 inverted) by
    /// the fix.
    ///
    /// <para>
    /// The seed epsilon of an OrbitalCheckpoint / BubbleEntry / BubbleExit anchor is
    /// <c>worldRef - smoothed</c>, where one side is a Kepler position at the boundary UT
    /// (<c>new Orbit(...).getPositionAtUT(t_b)</c>: inertial, placed with the CURRENT
    /// <c>Planetarium.Zup</c>) and the other is a body-fixed sample turned into world space by
    /// <c>body.GetWorldSurfacePosition</c>, which uses the body's CURRENT rotation
    /// (<c>CelestialBody.BodyFrame</c>, decompiled 1.12.5). Both are read when the session is
    /// rebuilt (the Re-Fly invocation), not at <c>t_b</c>, so the epsilon carries the body's
    /// rotation between <c>t_b</c> and the rebuild: a chord of
    /// <c>2 r cos(lat) |sin(pi (now - t_b) / T_rot)|</c>, up to twice the orbital radius.
    /// </para>
    ///
    /// <para>
    /// Headless model of that world (no CelestialBody can be built in xUnit): the Kepler side
    /// is Parsek's own port of stock <c>Orbit</c> (<see cref="TwoBodyOrbit"/>, stock frame, the
    /// headless Zup angle is the identity), and the surface side places (lat, lon, alt) at the
    /// inertial longitude <c>lon + initialRotation + 360 * now / T_rot</c> (stock
    /// <c>CelestialBody.CBUpdate</c>'s <c>rotationAngle</c>). Assertion 1 proves that model
    /// against the real fixture bytes: at the boundary's own UT the two sides agree to metres
    /// on every Kerbin and Mun bubble boundary of <c>cs-career-mun4</c> (the RW-5 / RW-6 /
    /// RW-7 host), so the recorded data are consistent and the 1,300 km is not a fixture
    /// artifact. The propagator itself (candidate builder over the real recordings, per-source
    /// dispatch, <c>worldRef - smoothed</c>, priority write, HR-9 Warn) is production code;
    /// only the two world-frame reads go through the test seams.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorEpsilonRebuildClockCharacterizationTests : IDisposable
    {
        // CS Orbiter's LKO leg (tree "CS Orbiter"): its first bubble exit, section 7, at
        // UT 689.83. The earliest anchor the propagator resolves in committed order.
        private const string OrbiterRecordingId = "e312c6a92d184ddc8eeab566190f94d7";
        private const int OrbiterExitSection = 7;
        private const double OrbiterExitUT = 689.83373901340747;

        // The RW-5 / RW-6 / RW-7 Re-Fly clock: G's flight is held under 185800 and the
        // rewind point sits at the pad split (~185785.6).
        private const double ReFlyRebuildUT = 185800.0;

        private sealed class BodyModel
        {
            public double Radius, Mu, RotationPeriod, InitialRotationDeg;
        }

        private static readonly Dictionary<string, BodyModel> Bodies = new Dictionary<string, BodyModel>(StringComparer.Ordinal)
        {
            { "Kerbin", new BodyModel { Radius = 600000.0, Mu = 3.5316e12, RotationPeriod = 21549.425, InitialRotationDeg = 90.0 } },
            { "Mun", new BodyModel { Radius = 200000.0, Mu = 6.5138398e10, RotationPeriod = 138984.38, InitialRotationDeg = 230.0 } },
        };

        private readonly List<string> logLines = new List<string>();
        private readonly ITestOutputHelper output;

        public AnchorEpsilonRebuildClockCharacterizationTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- the KSP world model ------------------------------------------------------

        private static Vector3d SurfaceAtRotation(string bodyName, double latDeg, double lonDeg, double altM, double rotationUT)
        {
            BodyModel b = Bodies[bodyName];
            double r = b.Radius + altM;
            double inertialLon = (lonDeg + b.InitialRotationDeg + 360.0 * rotationUT / b.RotationPeriod) * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            return new Vector3d(
                r * Math.Cos(lat) * Math.Cos(inertialLon),
                r * Math.Cos(lat) * Math.Sin(inertialLon),
                r * Math.Sin(lat));
        }

        private static Vector3d? Kepler(List<OrbitSegment> checkpoints, double ut)
        {
            if (checkpoints == null || checkpoints.Count == 0) return null;
            // Same segment pick as TrajectoryMath.EvaluateOrbitSegmentAtUT.
            OrbitSegment? maybe = TrajectoryMath.FindOrbitSegment(checkpoints, ut);
            OrbitSegment seg = maybe ?? (ut <= checkpoints[0].startUT ? checkpoints[0] : checkpoints[checkpoints.Count - 1]);
            if (!Bodies.TryGetValue(seg.bodyName ?? "", out BodyModel b)) return null;
            if (!TwoBodyOrbit.TryCreateFromSegment(seg, b.Mu, out TwoBodyOrbit orbit)) return null;
            return orbit.GetPositionAtUT(ut);
        }

        private static TrajectoryPoint? SampleAt(List<TrajectoryPoint> frames, double ut)
        {
            if (frames == null || frames.Count == 0) return null;
            if (ut <= frames[0].ut) return frames[0];
            if (ut >= frames[frames.Count - 1].ut) return frames[frames.Count - 1];
            for (int i = 0; i + 1 < frames.Count; i++)
            {
                TrajectoryPoint a = frames[i], c = frames[i + 1];
                if (ut < a.ut || ut > c.ut) continue;
                double t = c.ut > a.ut ? (ut - a.ut) / (c.ut - a.ut) : 0.0;
                var p = a;
                p.latitude = a.latitude + (c.latitude - a.latitude) * t;
                p.longitude = a.longitude + (c.longitude - a.longitude) * t;
                p.altitude = a.altitude + (c.altitude - a.altitude) * t;
                p.ut = ut;
                return p;
            }
            return null;
        }

        /// <summary>
        /// ProductionAnchorWorldFrameResolver's two Kepler / sample reads, with the world
        /// frame of the rebuild instant <see cref="nowUT"/>.
        /// </summary>
        private sealed class ModelResolver : IAnchorWorldFrameResolver
        {
            private readonly double nowUT;
            public ModelResolver(double nowUT) { this.nowUT = nowUT; }

            public bool TryResolveRelativeBoundaryWorldPos(Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            { worldPos = default; return false; }

            public bool TryResolveOrbitalCheckpointWorldPos(Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                worldPos = default;
                int ck = side == AnchorSide.Start ? sectionIndex - 1 : sectionIndex + 1;
                if (ck < 0 || ck >= rec.TrackSections.Count) return false;
                if (rec.TrackSections[ck].referenceFrame != ReferenceFrame.OrbitalCheckpoint) return false;
                Vector3d? k = Kepler(rec.TrackSections[ck].checkpoints, boundaryUT);
                if (!k.HasValue) return false;
                worldPos = k.Value;
                return true;
            }

            public bool TryResolveSoiBoundaryWorldPos(Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
                => TryResolveOrbitalCheckpointWorldPos(rec, sectionIndex, side, boundaryUT, out worldPos);

            public bool TryResolveLoopAnchorWorldPos(Recording rec, int sectionIndex, AnchorSide side, double sampleUT, out Vector3d worldPos)
            { worldPos = default; return false; }

            public bool TryResolveBubbleEntryExitWorldPos(Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
            {
                worldPos = default;
                int physIdx = side == AnchorSide.Start ? sectionIndex - 1 : sectionIndex + 1;
                if (physIdx < 0 || physIdx >= rec.TrackSections.Count) return false;
                TrackSection phys = rec.TrackSections[physIdx];
                if (phys.source != TrackSectionSource.Active && phys.source != TrackSectionSource.Background) return false;
                if (phys.referenceFrame != ReferenceFrame.Absolute) return false;
                if (phys.frames == null || phys.frames.Count == 0) return false;
                TrajectoryPoint pt = side == AnchorSide.Start ? phys.frames[phys.frames.Count - 1] : phys.frames[0];
                if (!Bodies.ContainsKey(pt.bodyName ?? "")) return false;
                // GetWorldSurfacePosition: the body's rotation at the REBUILD instant.
                worldPos = SurfaceAtRotation(pt.bodyName, pt.latitude, pt.longitude, pt.altitude, nowUT);
                return true;
            }
        }

        private static Func<Recording, int, double, Vector3d?> ModelSmoothed(double nowUT)
        {
            return (rec, sectionIndex, ut) =>
            {
                TrackSection s = rec.TrackSections[sectionIndex];
                if (s.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                    return Kepler(s.checkpoints, ut);
                if (s.referenceFrame != ReferenceFrame.Absolute) return null;
                TrajectoryPoint? p = SampleAt(s.frames, ut);
                if (!p.HasValue || !Bodies.ContainsKey(p.Value.bodyName ?? "")) return null;
                // Spline stand-in: a tag-0 spline lowers through GetWorldSurfacePosition too.
                return SurfaceAtRotation(p.Value.bodyName, p.Value.latitude, p.Value.longitude, p.Value.altitude, nowUT);
            };
        }

        // ---- fixture ------------------------------------------------------------------

        private static AnalyzerModel LoadFixture()
        {
            string dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves", "cs-career-mun4"));
            Assert.True(File.Exists(Path.Combine(dir, "persistent.sfs")), "fixture missing: " + dir);
            bool prior = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try { return SaveDirectoryLoader.Load(dir, _ => null); }
            finally { ParsekLog.SuppressLogging = prior; }
        }

        private static RecordingTree TreeOf(AnalyzerModel model, Recording rec)
        {
            return model.Trees.FirstOrDefault(t => t?.Recordings != null && t.Recordings.ContainsKey(rec.RecordingId));
        }

        private Dictionary<string, double> RunPropagatorAt(AnalyzerModel model, double nowUT)
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            bool prior = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try
            {
                foreach (Recording rec in model.Recordings)
                    AnchorCandidateBuilder.BuildAndStorePerSection(rec, TreeOf(model, rec));
            }
            finally { ParsekLog.SuppressLogging = prior; }

            ParsekLog.SuppressLogging = false;
            logLines.Clear();
            AnchorPropagator.SmoothedPositionForTesting = ModelSmoothed(nowUT);
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "rebuild-clock" },
                model.Recordings, model.Trees, surfaceLookup: null, resolver: new ModelResolver(nowUT));

            var eps = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Recording rec in model.Recordings)
                for (int s = 0; s < rec.TrackSections.Count; s++)
                    foreach (AnchorSide side in new[] { AnchorSide.Start, AnchorSide.End })
                        if (RenderSessionState.TryLookup(rec.RecordingId, s, side, out AnchorCorrection ac)
                            && (ac.Source == AnchorSource.BubbleExit || ac.Source == AnchorSource.BubbleEntry
                                || ac.Source == AnchorSource.OrbitalCheckpoint))
                            eps[rec.RecordingId + "|" + s + "|" + side] = ac.Epsilon.magnitude;
            return eps;
        }

        // ---- cells --------------------------------------------------------------------

        [Fact]
        public void FixtureBubbleBoundaries_KeplerAndSampleAgreeAtTheBoundaryUT()
        {
            // At the boundary's own UT the Kepler point and the recorded physics sample are
            // the same point to metres, on both bodies: the recording is not the 1,300 km.
            AnalyzerModel model = LoadFixture();
            int checkedCount = 0;
            double worst = 0.0;
            foreach (Recording rec in model.Recordings)
            {
                foreach (var kv in AnchorCandidateBuilder.Compute(rec, TreeOf(model, rec)))
                    foreach (AnchorCandidate c in kv.Value)
                    {
                        if (c.Source != AnchorSource.BubbleExit && c.Source != AnchorSource.BubbleEntry) continue;
                        var atBoundary = new ModelResolver(c.UT);
                        if (!atBoundary.TryResolveBubbleEntryExitWorldPos(rec, kv.Key, c.Side, c.UT, out Vector3d sample)) continue;
                        Vector3d? k = Kepler(rec.TrackSections[kv.Key].checkpoints, c.UT);
                        if (!k.HasValue) continue;
                        int physIdx = c.Side == AnchorSide.Start ? kv.Key - 1 : kv.Key + 1;
                        TrackSection phys = rec.TrackSections[physIdx];
                        TrajectoryPoint pt = c.Side == AnchorSide.Start ? phys.frames[phys.frames.Count - 1] : phys.frames[0];
                        if (!string.Equals(pt.bodyName, TrajectoryMath.FindOrbitSegment(rec.TrackSections[kv.Key].checkpoints, c.UT)?.bodyName ?? pt.bodyName, StringComparison.Ordinal))
                            continue; // an SOI-crossing boundary: different bodies, not this model
                        double d = (k.Value - sample).magnitude;
                        worst = Math.Max(worst, d);
                        checkedCount++;
                    }
            }
            output.WriteLine("bubble boundaries checked=" + checkedCount + " worstMismatchM=" + worst.ToString("F2", CultureInfo.InvariantCulture));
            Assert.True(checkedCount >= 80, "checked " + checkedCount);
            Assert.True(worst < 100.0, "worst Kepler-vs-sample mismatch at t_b = " + worst + " m");
        }

        [Fact]
        public void ReFlyRebuild_SeedEpsilonCarriesBodyRotationSinceBoundary_Characterization()
        {
            AnalyzerModel model = LoadFixture();
            Dictionary<string, double> eps = RunPropagatorAt(model, ReFlyRebuildUT);

            string key = OrbiterRecordingId + "|" + OrbiterExitSection + "|" + AnchorSide.Start;
            Assert.True(eps.ContainsKey(key), "no BubbleExit anchor for the CS Orbiter LKO exit");

            // The chord of Kerbin's rotation between the boundary and the rebuild.
            Recording orbiter = model.Recordings.First(r => r.RecordingId == OrbiterRecordingId);
            TrackSection phys = orbiter.TrackSections[OrbiterExitSection - 1];
            TrajectoryPoint pt = phys.frames[phys.frames.Count - 1];
            double r = 600000.0 + pt.altitude;
            double chord = 2.0 * r * Math.Cos(pt.latitude * Math.PI / 180.0)
                * Math.Abs(Math.Sin(Math.PI * (ReFlyRebuildUT - OrbiterExitUT) / 21549.425));
            output.WriteLine("orbiter exit eps=" + eps[key].ToString("R", CultureInfo.InvariantCulture)
                + " chord=" + chord.ToString("R", CultureInfo.InvariantCulture));

            // CURRENT BEHAVIOUR (the defect): ~1,308 km, the todo's sample value
            // (epsilonMagnitudeM=1307870.24), and exactly the rotation chord.
            Assert.InRange(eps[key], 1300000.0, 1315000.0);
            Assert.InRange(Math.Abs(eps[key] - chord), 0.0, 200.0);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-Anchor]")
                && l.Contains("exceeds bubble radius")
                && l.Contains("recordingId=" + OrbiterRecordingId)
                && l.Contains("sectionIndex=" + OrbiterExitSection)
                && l.Contains("anchorSource=BubbleExit"));

            // Each anchor carries its own rotation phase, so the magnitudes are NOT one
            // propagated value: they spread from tens of km to thousands.
            List<double> warned = eps.Values.Where(v => v > 2500.0).OrderBy(v => v).ToList();
            output.WriteLine("anchors=" + eps.Count + " warned=" + warned.Count
                + " min=" + warned.First().ToString("F0", CultureInfo.InvariantCulture)
                + " max=" + warned.Last().ToString("F0", CultureInfo.InvariantCulture));
            Assert.True(warned.Count >= 100, "warned " + warned.Count);
            Assert.True(warned.Last() - warned.First() > 1000000.0, "spread " + (warned.Last() - warned.First()));
        }

        [Fact]
        public void RebuildAtTheBoundaryUT_SameAnchorEpsilonIsMetres()
        {
            // The same anchor computed with the world as it stands at its own boundary UT
            // (what playback sees when it reaches that boundary): metres, no Warn.
            AnalyzerModel model = LoadFixture();
            Dictionary<string, double> eps = RunPropagatorAt(model, OrbiterExitUT);
            string key = OrbiterRecordingId + "|" + OrbiterExitSection + "|" + AnchorSide.Start;
            Assert.True(eps.ContainsKey(key));
            output.WriteLine("orbiter exit eps at t_b=" + eps[key].ToString("R", CultureInfo.InvariantCulture));
            Assert.True(eps[key] < 100.0, "eps " + eps[key]);
            Assert.DoesNotContain(logLines, l => l.Contains("exceeds bubble radius")
                && l.Contains("recordingId=" + OrbiterRecordingId)
                && l.Contains("sectionIndex=" + OrbiterExitSection + " side=Start"));
        }
    }
}
