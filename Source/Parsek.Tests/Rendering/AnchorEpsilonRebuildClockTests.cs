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
    /// Todo REFLY-ANCHOR-EPSILON-1300KM, on the real RW-5 / RW-6 / RW-7 host
    /// (<c>cs-career-mun4</c>). The seed epsilon of an OrbitalCheckpoint / BubbleEntry /
    /// BubbleExit anchor used to be <c>worldRef - smoothed</c> with one side a Kepler position
    /// placed with the CURRENT <c>Planetarium.Zup</c> and the other a body-fixed point placed
    /// with the body's CURRENT rotation, so it carried the body's rotation between the
    /// boundary and the Re-Fly rebuild: a chord of <c>2 r cos(lat) |sin(pi (now - t_b) / T)|</c>
    /// (the CS Orbiter's LKO exit read 1,307 km at a rebuild near UT 185800; the PC logs read
    /// 64 km to 22,820 km). The propagator now compares both sides in the body-fixed frame of
    /// the boundary UT, so the epsilon is metres and the same at every rebuild clock.
    ///
    /// <para>
    /// Headless world: bodies are uninitialised <see cref="CelestialBody"/> objects carrying
    /// stock Kerbin / Mun constants, read by the production code through its body seams; the
    /// world placement (<c>GetWorldSurfacePosition</c> at the rebuild instant) is the
    /// <c>surfaceLookup</c> model below, which rotates the body by stock's
    /// <c>rotationAngle = initialRotation + 360 * now / T_rot</c>. Splines are fitted by the
    /// production <see cref="SmoothingPipeline"/>. Cell 1 proves the frame relation against
    /// the fixture bytes: at each bubble boundary's own UT the stored-frame Kepler point and
    /// the recorded physics sample agree to metres on Kerbin and the Mun.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class AnchorEpsilonRebuildClockTests : IDisposable
    {
        // CS Orbiter's LKO leg (tree "CS Orbiter"): its first bubble exit, section 7, at UT 689.83.
        private const string OrbiterRecordingId = "e312c6a92d184ddc8eeab566190f94d7";
        private const int OrbiterExitSection = 7;
        private const double OrbiterExitUT = 689.83373901340747;

        // The two rebuild UTs the PC logs show for RW-5 / RW-6 / RW-7.
        private const double RebuildUtA = 185800.5;
        private const double RebuildUtB = 185786.9;

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
        private readonly Dictionary<string, CelestialBody> celestialBodies = new Dictionary<string, CelestialBody>(StringComparer.Ordinal);

        public AnchorEpsilonRebuildClockTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            foreach (var kv in Bodies)
            {
                CelestialBody body = TestBodyRegistry.CreateBody(kv.Key, kv.Value.Radius, kv.Value.Mu);
                body.rotationPeriod = kv.Value.RotationPeriod;
                body.initialRotation = kv.Value.InitialRotationDeg;
                body.rotates = true;
                celestialBodies[kv.Key] = body;
            }
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---- the KSP world model ------------------------------------------------------

        /// <summary><c>body.GetWorldSurfacePosition</c> with the body rotated as at <paramref name="rotationUT"/>.</summary>
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

        private static Vector3d? StoredFrameKepler(List<OrbitSegment> checkpoints, double ut)
        {
            if (!TrajectoryMath.TryPickOrbitSegmentForBoundaryUT(checkpoints, ut, out OrbitSegment seg)) return null;
            if (!Bodies.TryGetValue(seg.bodyName ?? "", out BodyModel b)) return null;
            if (!TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, b.Mu, out TwoBodyOrbit orbit)) return null;
            return orbit.GetPositionAtUT(ut);
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

        private static RecordingTree TreeOf(AnalyzerModel model, string recordingId)
        {
            return model.Trees.FirstOrDefault(t => t?.Recordings != null && t.Recordings.ContainsKey(recordingId));
        }

        private CelestialBody BodyByName(string name)
        {
            return name != null && celestialBodies.TryGetValue(name, out CelestialBody b) ? b : null;
        }

        private sealed class Anchor
        {
            public AnchorSource Source;
            public Vector3d Epsilon;
        }

        /// <summary>
        /// The production rebuild path over the fixture with the world as it stands at
        /// <paramref name="nowUT"/>: fit splines + emit candidates per recording, then run the
        /// propagator. Returns every OrbitalCheckpoint / Bubble anchor, keyed
        /// <c>recordingId|section|side</c>.
        /// </summary>
        private Dictionary<string, Anchor> RunRebuildAt(AnalyzerModel model, double nowUT)
        {
            RenderSessionState.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            AnchorPropagator.ResetForTesting();
            SmoothingPipeline.BodyResolverForTesting = BodyByName;
            SmoothingPipeline.TreeResolverForTesting = id => TreeOf(model, id);
            AnchorPropagator.BodyResolverForTesting = BodyByName;

            bool prior = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try
            {
                foreach (Recording rec in model.Recordings)
                    SmoothingPipeline.FitAndStorePerSection(rec);
            }
            finally { ParsekLog.SuppressLogging = prior; }

            ParsekLog.SuppressLogging = false;
            logLines.Clear();
            AnchorPropagator.Run(new ReFlySessionMarker { SessionId = "rebuild-clock" },
                model.Recordings, model.Trees,
                surfaceLookup: (name, lat, lon, alt) => SurfaceAtRotation(name, lat, lon, alt, nowUT),
                resolver: null);

            var anchors = new Dictionary<string, Anchor>(StringComparer.Ordinal);
            foreach (Recording rec in model.Recordings)
                for (int s = 0; s < rec.TrackSections.Count; s++)
                    foreach (AnchorSide side in new[] { AnchorSide.Start, AnchorSide.End })
                        if (RenderSessionState.TryLookup(rec.RecordingId, s, side, out AnchorCorrection ac)
                            && (ac.Source == AnchorSource.BubbleExit || ac.Source == AnchorSource.BubbleEntry
                                || ac.Source == AnchorSource.OrbitalCheckpoint))
                            anchors[rec.RecordingId + "|" + s + "|" + side] = new Anchor { Source = ac.Source, Epsilon = ac.Epsilon };
            return anchors;
        }

        private static string OrbiterExitKey => OrbiterRecordingId + "|" + OrbiterExitSection + "|" + AnchorSide.Start;

        // ---- cells --------------------------------------------------------------------

        [Fact]
        public void FixtureBubbleBoundaries_StoredFrameKeplerAndSampleAgreeAtTheBoundaryUT()
        {
            // The frame relation the fix rests on, against real recorded bytes: the Kepler
            // point (stored elements, LAN from Planetarium.right) and the recorded physics
            // sample, both placed at the boundary's own UT, are one point to metres.
            AnalyzerModel model = LoadFixture();
            int checkedCount = 0;
            double worst = 0.0;
            var bodiesSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Recording rec in model.Recordings)
            {
                foreach (var kv in AnchorCandidateBuilder.Compute(rec, TreeOf(model, rec.RecordingId)))
                    foreach (AnchorCandidate c in kv.Value)
                    {
                        if (c.Source != AnchorSource.BubbleExit && c.Source != AnchorSource.BubbleEntry) continue;
                        int physIdx = c.Side == AnchorSide.Start ? kv.Key - 1 : kv.Key + 1;
                        if (physIdx < 0 || physIdx >= rec.TrackSections.Count) continue;
                        TrackSection phys = rec.TrackSections[physIdx];
                        if (phys.referenceFrame != ReferenceFrame.Absolute || phys.frames == null || phys.frames.Count == 0) continue;
                        TrajectoryPoint pt = c.Side == AnchorSide.Start ? phys.frames[phys.frames.Count - 1] : phys.frames[0];
                        if (!TrajectoryMath.TryPickOrbitSegmentForBoundaryUT(rec.TrackSections[kv.Key].checkpoints, c.UT, out OrbitSegment seg)
                            || !string.Equals(seg.bodyName, pt.bodyName, StringComparison.Ordinal)
                            || !Bodies.ContainsKey(pt.bodyName ?? ""))
                            continue;
                        Vector3d? k = StoredFrameKepler(rec.TrackSections[kv.Key].checkpoints, c.UT);
                        if (!k.HasValue) continue;
                        double d = (k.Value - SurfaceAtRotation(pt.bodyName, pt.latitude, pt.longitude, pt.altitude, c.UT)).magnitude;
                        worst = Math.Max(worst, d);
                        bodiesSeen.Add(pt.bodyName);
                        checkedCount++;
                    }
            }
            output.WriteLine("bubble boundaries checked=" + checkedCount + " worstMismatchM=" + worst.ToString("F2", CultureInfo.InvariantCulture));
            Assert.True(checkedCount >= 80, "checked " + checkedCount);
            Assert.Contains("Kerbin", bodiesSeen);
            Assert.Contains("Mun", bodiesSeen);
            Assert.True(worst < 10.0, "worst Kepler-vs-sample mismatch at t_b = " + worst + " m");
        }

        [Fact]
        public void ReFlyRebuild_BoundaryEpsilonIsMetres_AndNothingExceedsTheBubbleRadius()
        {
            // Before the fix the CS Orbiter's exit read 1,307 km at a rebuild near UT 185800
            // and 230 of 234 boundary anchors tripped the HR-9 Warn (66 km to 22,818 km).
            AnalyzerModel model = LoadFixture();
            Dictionary<string, Anchor> anchors = RunRebuildAt(model, RebuildUtA);

            Assert.True(anchors.ContainsKey(OrbiterExitKey), "no BubbleExit anchor for the CS Orbiter LKO exit");
            double orbiterExit = anchors[OrbiterExitKey].Epsilon.magnitude;

            var bySource = anchors.Values.GroupBy(a => a.Source)
                .ToDictionary(g => g.Key, g => g.Select(a => a.Epsilon.magnitude).ToList());
            foreach (var kv in bySource)
                output.WriteLine(kv.Key + ": count=" + kv.Value.Count
                    + " nonZero=" + kv.Value.Count(v => v > 0.0)
                    + " max=" + kv.Value.Max().ToString("F3", CultureInfo.InvariantCulture));
            output.WriteLine("orbiter exit eps=" + orbiterExit.ToString("R", CultureInfo.InvariantCulture));
            var candidateSources = new Dictionary<AnchorSource, int>();
            foreach (Recording rec in model.Recordings)
                foreach (var kv in AnchorCandidateBuilder.Compute(rec, TreeOf(model, rec.RecordingId)))
                    foreach (AnchorCandidate c in kv.Value)
                        candidateSources[c.Source] = (candidateSources.TryGetValue(c.Source, out int n) ? n : 0) + 1;
            output.WriteLine("candidates: " + string.Join(" ", candidateSources.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
            string summary = logLines.FirstOrDefault(l => l.Contains("DAG walk summary"));
            output.WriteLine(summary ?? "<no summary>");

            Assert.True(orbiterExit < 10.0, "orbiter exit eps " + orbiterExit);
            double worst = anchors.Values.Max(a => a.Epsilon.magnitude);
            Assert.True(worst < 100.0, "worst boundary eps " + worst);
            int resolved = anchors.Values.Count(a => a.Epsilon.magnitude > 0.0);
            Assert.True(resolved >= 100, "resolved boundary anchors " + resolved);
            Assert.DoesNotContain(logLines, l => l.Contains("exceeds bubble radius"));
            Assert.Contains(logLines, l => l.Contains("[Pipeline-AnchorPropagate]")
                && l.Contains("DAG walk summary") && l.Contains("boundaryInstantMaxEpsilonM="));
        }

        [Fact]
        public void ReFlyRebuild_EpsilonIsTheSameAtEveryRebuildClock()
        {
            // The logs show the same anchor differing between the two rebuild UTs of one run.
            // Now each anchor's magnitude is a property of the recording, equal at both
            // rebuild clocks and equal to its value with the world at its own boundary UT.
            AnalyzerModel model = LoadFixture();
            Dictionary<string, Anchor> a = RunRebuildAt(model, RebuildUtA);
            Dictionary<string, Anchor> b = RunRebuildAt(model, RebuildUtB);
            Dictionary<string, Anchor> atBoundary = RunRebuildAt(model, OrbiterExitUT);

            Assert.Equal(a.Count, b.Count);
            double worstDelta = 0.0;
            foreach (var kv in a)
            {
                Assert.True(b.TryGetValue(kv.Key, out Anchor other), "missing at clock B: " + kv.Key);
                Assert.Equal(kv.Value.Source, other.Source);
                worstDelta = Math.Max(worstDelta, Math.Abs(kv.Value.Epsilon.magnitude - other.Epsilon.magnitude));
            }
            output.WriteLine("anchors=" + a.Count + " worstMagnitudeDeltaM=" + worstDelta.ToString("R", CultureInfo.InvariantCulture));
            Assert.True(worstDelta < 1e-6, "magnitude differs across rebuild clocks by " + worstDelta + " m");

            // The world vector turns with the body (it is placed with the body's rotation at
            // the rebuild), so A and B differ only by Kerbin's rotation between the clocks.
            Vector3d epsA = a[OrbiterExitKey].Epsilon, epsB = b[OrbiterExitKey].Epsilon;
            double turn = 360.0 * (RebuildUtB - RebuildUtA) / Bodies["Kerbin"].RotationPeriod * Math.PI / 180.0;
            var turned = new Vector3d(
                epsA.x * Math.Cos(turn) - epsA.y * Math.Sin(turn),
                epsA.x * Math.Sin(turn) + epsA.y * Math.Cos(turn),
                epsA.z);
            Assert.True((turned - epsB).magnitude < 1e-6, "A rotated to B differs by " + (turned - epsB).magnitude);
            Assert.Equal(atBoundary[OrbiterExitKey].Epsilon.magnitude, epsA.magnitude, 6);
        }
    }
}
