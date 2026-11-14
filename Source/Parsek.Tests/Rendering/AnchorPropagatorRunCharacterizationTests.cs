using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Characterization of <see cref="AnchorPropagator"/>'s DAG walk (the test-friendly
    /// <c>Run</c> overload) written against the method as it stood before it was split into
    /// named steps. Every cell drives one scenario and compares its whole transcript: every
    /// log line the run wrote, in order (the walk summary's <c>durationMs</c> masked), every
    /// call into the resolver and smoothed-position seams, and every anchor the session map
    /// holds afterwards. The expected transcripts live in
    /// <c>AnchorPropagatorRunCharacterizationTests.Expected.cs</c>; a few cells below also
    /// assert the meaning of a scenario directly.
    /// </summary>
    [Collection("Sequential")]
    public partial class AnchorPropagatorRunCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorPropagatorRunCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetStores();
        }

        public void Dispose()
        {
            ResetStores();
            ParsekLog.ResetTestOverrides();
        }

        private static void ResetStores()
        {
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            AnchorPropagator.ResetForTesting();
        }

        // ---- the scenario model ---------------------------------------------------------

        internal sealed class RunScenario
        {
            public ReFlySessionMarker Marker = new ReFlySessionMarker { SessionId = "char", TreeId = "char-tree", ActiveReFlyRecordingId = "char-active" };
            public List<Recording> Recordings = new List<Recording>();
            public List<RecordingTree> Trees = new List<RecordingTree>();
            public Func<string, double, double, double, Vector3d> SurfaceLookup;
            public StubResolver Resolver;
            public StubResolver OverrideResolver;
            public Action Arrange;
            public List<AnchorCorrection> PreSeeds = new List<AnchorCorrection>();
            public Func<string, bool> Suppressed;
            public Func<Recording, int, double, Vector3d?> Smoothed;
        }

        /// <summary>A resolver that records each call and answers from a table.</summary>
        internal sealed class StubResolver : IAnchorWorldFrameResolver
        {
            public Action<string> Sink;
            public Func<string, int, AnchorSide, double, Vector3d?> Rel;
            public Func<string, int, AnchorSide, double, Vector3d?> Loop;

            private bool Answer(string kind, Func<string, int, AnchorSide, double, Vector3d?> f,
                Recording rec, int s, AnchorSide side, double ut, out Vector3d worldPos)
            {
                Vector3d? v = f?.Invoke(rec?.RecordingId, s, side, ut);
                worldPos = v ?? Vector3d.zero;
                Sink?.Invoke(string.Format(CultureInfo.InvariantCulture,
                    "[stub] {0} rec={1} s={2} side={3} ut={4} -> {5}",
                    kind, rec?.RecordingId, s, side, ut.ToString("R", CultureInfo.InvariantCulture),
                    v.HasValue ? Vec(v.Value) : "miss"));
                return v.HasValue;
            }

            public bool TryResolveRelativeBoundaryWorldPos(Recording rec, int sectionIndex, AnchorSide side, double boundaryUT, out Vector3d worldPos)
                => Answer("rel", Rel, rec, sectionIndex, side, boundaryUT, out worldPos);

            public bool TryResolveLoopAnchorWorldPos(Recording rec, int sectionIndex, AnchorSide side, double sampleUT, out Vector3d worldPos)
                => Answer("loop", Loop, rec, sectionIndex, side, sampleUT, out worldPos);
        }

        internal static string Vec(Vector3d v)
        {
            return "(" + v.x.ToString("R", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("R", CultureInfo.InvariantCulture) + ","
                + v.z.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        private static readonly Regex DurationRx = new Regex(@"durationMs=[0-9.]+", RegexOptions.Compiled);

        // A double printed round-trip carries trig results, whose last bits can differ between
        // runtimes' math libraries (mono's libm, the .NET Framework CRT); the transcript keeps
        // ten significant digits of any number with more than ten.
        private static readonly Regex LongNumberRx = new Regex(@"-?[0-9]+\.[0-9]+(E[-+]?[0-9]+)?", RegexOptions.Compiled);

        internal static string Canonical(string line)
        {
            line = DurationRx.Replace(line, "durationMs=*");
            return LongNumberRx.Replace(line, m =>
            {
                string digits = m.Value.Split('E')[0].Replace("-", "").Replace(".", "").TrimStart('0');
                if (digits.Length <= 10) return m.Value;
                double v = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return v.ToString("G10", CultureInfo.InvariantCulture);
            });
        }

        /// <summary>
        /// Runs <paramref name="scenario"/> through <paramref name="run"/> on fresh stores and
        /// returns its transcript: the captured lines (log, resolver and smoothed seams) in
        /// order, then the anchor map over every recording slot, then the map's size.
        /// </summary>
        internal static List<string> Transcribe(RunScenario scenario, Action<RunScenario> run, List<string> lines)
        {
            ResetStores();
            scenario.Arrange?.Invoke();
            foreach (AnchorCorrection seed in scenario.PreSeeds)
                RenderSessionState.PutAnchorForTesting(seed);
            if (scenario.Suppressed != null)
                AnchorPropagator.SuppressionPredicateForTesting = scenario.Suppressed;
            if (scenario.Smoothed != null)
            {
                Func<Recording, int, double, Vector3d?> inner = scenario.Smoothed;
                AnchorPropagator.SmoothedPositionForTesting = (rec, s, ut) =>
                {
                    Vector3d? v = inner(rec, s, ut);
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "[smoothed] rec={0} s={1} ut={2} -> {3}",
                        rec?.RecordingId, s, ut.ToString("R", CultureInfo.InvariantCulture),
                        v.HasValue ? Vec(v.Value) : "null"));
                    return v;
                };
            }
            if (scenario.Resolver != null)
                scenario.Resolver.Sink = lines.Add;
            if (scenario.OverrideResolver != null)
            {
                scenario.OverrideResolver.Sink = line => lines.Add("[override] " + line);
                AnchorPropagator.ResolverOverrideForTesting = scenario.OverrideResolver;
            }

            lines.Clear();
            run(scenario);

            var transcript = new List<string>();
            foreach (string l in lines)
                transcript.Add(Canonical(l));
            if (scenario.Recordings != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Recording rec in scenario.Recordings)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || rec.TrackSections == null) continue;
                    if (!seen.Add(rec.RecordingId)) continue;
                    for (int s = 0; s < rec.TrackSections.Count; s++)
                        foreach (AnchorSide side in new[] { AnchorSide.Start, AnchorSide.End })
                            if (RenderSessionState.TryLookup(rec.RecordingId, s, side, out AnchorCorrection ac))
                                transcript.Add(string.Format(CultureInfo.InvariantCulture,
                                    "anchor {0}|{1}|{2} source={3} ut={4} eps={5}",
                                    rec.RecordingId, s, side, ac.Source,
                                    ac.UT.ToString("R", CultureInfo.InvariantCulture), Canonical(Vec(ac.Epsilon))));
                }
            }
            transcript.Add("anchorCount=" + RenderSessionState.Count.ToString(CultureInfo.InvariantCulture));
            return transcript;
        }

        internal static void RunCurrent(RunScenario s)
        {
            AnchorPropagator.Run(s.Marker, s.Recordings, s.Trees, s.SurfaceLookup, s.Resolver);
        }

        private List<string> Transcript(string name)
        {
            return Transcribe(Scenarios[name](), RunCurrent, logLines);
        }

        // ---- builders -------------------------------------------------------------------

        internal static readonly Func<string, double, double, double, Vector3d> Identity =
            (body, lat, lon, alt) => new Vector3d(lat, lon, alt);

        internal static TrajectoryPoint Pt(double ut, double lat = 0, double lon = 0, double alt = 0, string body = "Kerbin")
        {
            return new TrajectoryPoint { ut = ut, latitude = lat, longitude = lon, altitude = alt, bodyName = body, rotation = Quaternion.identity };
        }

        internal static TrackSection Sec(double startUT, double endUT,
            ReferenceFrame frame = ReferenceFrame.Absolute,
            TrackSectionSource source = TrackSectionSource.Active,
            List<TrajectoryPoint> frames = null, string body = "Kerbin")
        {
            return new TrackSection
            {
                referenceFrame = frame,
                environment = SegmentEnvironment.ExoBallistic,
                source = source,
                startUT = startUT,
                endUT = endUT,
                frames = frames ?? new List<TrajectoryPoint> { Pt(startUT, body: body), Pt(endUT, body: body) },
                checkpoints = new List<OrbitSegment>(),
            };
        }

        internal static OrbitSegment Lko(double startUT, double endUT, string body = "Kerbin")
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

        internal static TrackSection Ckpt(double startUT, double endUT, string body = "Kerbin")
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Checkpoint,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { Lko(startUT, endUT, body) },
            };
        }

        internal static Recording Rec(string id, params TrackSection[] sections)
        {
            var rec = new Recording { RecordingId = id, VesselName = id };
            rec.TrackSections.Clear();
            rec.TrackSections.AddRange(sections);
            return rec;
        }

        internal static AnchorCandidate C(double ut, AnchorSource source, AnchorSide side = AnchorSide.Start)
        {
            return new AnchorCandidate(ut, source, side);
        }

        internal static AnchorCorrection Seed(string id, int section, AnchorSide side, double ut, double x,
            AnchorSource source = AnchorSource.LiveSeparation, double y = 0.0)
        {
            return new AnchorCorrection(id, section, side, ut, new Vector3d(x, y, 0.0), source);
        }

        internal static BranchPoint Bp(string id, double ut, BranchPointType type, string[] parents, string[] children)
        {
            return new BranchPoint
            {
                Id = id, UT = ut, Type = type,
                ParentRecordingIds = parents == null ? null : new List<string>(parents),
                ChildRecordingIds = children == null ? null : new List<string>(children),
            };
        }

        internal static RecordingTree Tree(string id, params BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = id };
            tree.BranchPoints.AddRange(bps);
            return tree;
        }

        internal static SmoothingSpline FlatSpline(double t0, double t1, float x, float y, float z, byte frameTag = 0)
        {
            return new SmoothingSpline
            {
                SplineType = 0, Tension = 0.5f, FrameTag = frameTag, IsValid = true,
                KnotsUT = new[] { t0, t1 },
                ControlsX = new[] { x, x },
                ControlsY = new[] { y, y },
                ControlsZ = new[] { z, z },
            };
        }

        internal static CelestialBody Kerbin()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            body.rotationPeriod = 21549.425;
            body.initialRotation = 90.0;
            body.rotates = true;
            return body;
        }

        internal static void UseKerbin()
        {
            CelestialBody kerbin = Kerbin();
            AnchorPropagator.BodyResolverForTesting = name => name == "Kerbin" ? kerbin : null;
        }

        /// <summary>Kerbin as the root and the Mun on its own conic around Kerbin.</summary>
        internal static void UseKerbinAndMun()
        {
            CelestialBody kerbin = Kerbin();
            CelestialBody mun = TestBodyRegistry.CreateBody("Mun", 200000.0, 6.5138398e10);
            mun.rotationPeriod = 138984.38;
            mun.initialRotation = 230.0;
            mun.rotates = true;
            mun.orbitDriver = (OrbitDriver)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(OrbitDriver));
            Orbit o = (Orbit)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Orbit));
            o.inclination = 0.0;
            o.eccentricity = 0.0;
            o.semiMajorAxis = 12000000.0;
            o.LAN = 0.0;
            o.argumentOfPeriapsis = 0.0;
            o.meanAnomalyAtEpoch = 1.7;
            o.epoch = 0.0;
            o.referenceBody = kerbin;
            mun.orbitDriver.orbit = o;
            AnchorPropagator.BodyResolverForTesting = name => name == "Kerbin" ? kerbin : name == "Mun" ? mun : null;
        }

        // ---- the scenarios --------------------------------------------------------------

        internal static readonly Dictionary<string, Func<RunScenario>> Scenarios =
            new Dictionary<string, Func<RunScenario>>(StringComparer.Ordinal)
        {
            // Seed pass: the zero-epsilon and skipped sources.
            ["seed-surface-continuous"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0,
                    new[] { C(0, AnchorSource.SurfaceContinuous), C(100, AnchorSource.SurfaceContinuous, AnchorSide.End) });
                return s;
            },
            ["seed-dock-candidate-skipped"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0, new[] { C(0, AnchorSource.DockOrMerge) });
                return s;
            },
            ["seed-live-separation-candidate-no-resolver"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0, new[] { C(0, AnchorSource.LiveSeparation) });
                return s;
            },
            ["seed-live-separation-and-reserved7-with-resolver"] = () =>
            {
                var s = new RunScenario { Resolver = new StubResolver() };
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0,
                    new[] { C(0, AnchorSource.LiveSeparation), C(100, AnchorSource.Reserved7, AnchorSide.End) });
                return s;
            },
            // Seed pass: the resolver-backed sources.
            ["seed-resolver-hits"] = () =>
            {
                var s = new RunScenario
                {
                    Resolver = new StubResolver
                    {
                        Rel = (id, sec, side, ut) => new Vector3d(10, 20, 30),
                        Loop = (id, sec, side, ut) => new Vector3d(7, 8, 9.5),
                    },
                    Smoothed = (rec, sec, ut) => new Vector3d(1, 2, 3),
                };
                s.Recordings.Add(Rec("A", Sec(0, 100), Sec(100, 200)));
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutAnchorCandidates("A", 0,
                        new[] { C(0, AnchorSource.RelativeBoundary), C(100, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("A", 1, new[] { C(150, AnchorSource.Loop) });
                };
                return s;
            },
            ["seed-resolver-misses"] = () =>
            {
                var s = new RunScenario { Resolver = new StubResolver(), Smoothed = (rec, sec, ut) => new Vector3d(1, 2, 3) };
                s.Recordings.Add(Rec("A", Sec(0, 100), Sec(100, 200)));
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutAnchorCandidates("A", 0,
                        new[] { C(0, AnchorSource.RelativeBoundary), C(100, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("A", 1, new[] { C(150, AnchorSource.Loop) });
                };
                return s;
            },
            ["seed-resolver-hit-no-smoothed"] = () =>
            {
                var s = new RunScenario
                {
                    Resolver = new StubResolver
                    {
                        Rel = (id, sec, side, ut) => new Vector3d(10, 20, 30),
                        Loop = (id, sec, side, ut) => new Vector3d(2, 2, 2),
                    },
                    Smoothed = (rec, sec, ut) => null,
                };
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0,
                    new[] { C(0, AnchorSource.RelativeBoundary), C(100, AnchorSource.SoiTransition, AnchorSide.End), C(50, AnchorSource.Loop) });
                return s;
            },
            ["seed-resolver-sources-no-resolver"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0,
                    new[] { C(0, AnchorSource.RelativeBoundary), C(100, AnchorSource.SoiTransition, AnchorSide.End), C(50, AnchorSource.Loop) });
                return s;
            },
            ["seed-resolver-hit-smoothed-from-spline"] = () =>
            {
                // No smoothed seam: the production evaluator reads the section's spline. Section 0
                // resolves no body, so the tag-0 spline goes through the surface lookup; section 1
                // has no spline; section 2's spline is inertial (no body, no lookup); section 3 has
                // no body name.
                var s = new RunScenario
                {
                    SurfaceLookup = Identity,
                    Resolver = new StubResolver { Rel = (id, sec, side, ut) => new Vector3d(100, 200, 300) },
                };
                s.Recordings.Add(Rec("A", Sec(0, 100), Sec(100, 200), Sec(200, 300),
                    Sec(300, 400, frames: new List<TrajectoryPoint> { Pt(300, body: null) })));
                s.Arrange = () =>
                {
                    AnchorPropagator.BodyResolverForTesting = name => null;
                    SectionAnnotationStore.PutSmoothingSpline("A", 0, FlatSpline(0, 100, 4, 5, 6));
                    SectionAnnotationStore.PutSmoothingSpline("A", 2, FlatSpline(200, 300, 4, 5, 6, frameTag: 1));
                    SectionAnnotationStore.PutSmoothingSpline("A", 3, FlatSpline(300, 400, 4, 5, 6));
                    for (int i = 0; i < 4; i++)
                        SectionAnnotationStore.PutAnchorCandidates("A", i, new[] { C(i * 100 + 50, AnchorSource.RelativeBoundary) });
                };
                return s;
            },
            ["seed-resolver-override-wins"] = () =>
            {
                var s = new RunScenario
                {
                    Resolver = new StubResolver { Rel = (id, sec, side, ut) => new Vector3d(1, 1, 1) },
                    OverrideResolver = new StubResolver { Rel = (id, sec, side, ut) => new Vector3d(9, 9, 9) },
                    Smoothed = (rec, sec, ut) => Vector3d.zero,
                };
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("A", 0, new[] { C(0, AnchorSource.RelativeBoundary) });
                return s;
            },
            // Seed pass: the boundary-instant sources.
            ["seed-boundary-instant-hits"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                // B: physics [4980,5000] then checkpoint: a BubbleExit on section 1.
                s.Recordings.Add(Rec("B",
                    Sec(4980, 5000, frames: new List<TrajectoryPoint> { Pt(4980, 1, 2, 70000), Pt(5000, 1.5, 2.5, 70100) }),
                    Ckpt(5000, 5600)));
                // E: checkpoint then physics [5600,5620]: a BubbleEntry on section 0's End.
                s.Recordings.Add(Rec("E",
                    Ckpt(5000, 5600),
                    Sec(5600, 5620, frames: new List<TrajectoryPoint> { Pt(5600, -3, 40, 69000), Pt(5620, -3.2, 40.5, 68000) })));
                // O: Absolute with a spline then checkpoint: an OrbitalCheckpoint on section 0's End.
                s.Recordings.Add(Rec("O", Sec(4980, 5000), Ckpt(5000, 5600)));
                s.Arrange = () =>
                {
                    UseKerbin();
                    SectionAnnotationStore.PutAnchorCandidates("B", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("E", 0, new[] { C(5600, AnchorSource.BubbleEntry, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("O", 0, FlatSpline(4980, 5000, 10f, 20f, 70500f));
                    SectionAnnotationStore.PutAnchorCandidates("O", 0, new[] { C(5000, AnchorSource.OrbitalCheckpoint, AnchorSide.End) });
                };
                return s;
            },
            ["seed-boundary-instant-misses"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                s.Recordings.Add(Rec("nospline", Sec(4980, 5000), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("edge", Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("nockpt", Sec(4980, 5000), Sec(5000, 5600)));
                s.Recordings.Add(Rec("notphys", Sec(4980, 5000, source: TrackSectionSource.Checkpoint), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("relative", Sec(4980, 5000, frame: ReferenceFrame.Relative), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("emptyframes", Sec(4980, 5000, frames: new List<TrajectoryPoint>()), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("mismatch", Sec(4980, 5000, body: "Mun"), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("nobody", Sec(4980, 5000, body: "Duna"), Ckpt(5000, 5600, body: "Duna")));
                s.Arrange = () =>
                {
                    UseKerbin();
                    SectionAnnotationStore.PutAnchorCandidates("nospline", 0, new[] { C(5000, AnchorSource.OrbitalCheckpoint, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("edge", 0, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("nockpt", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("notphys", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("relative", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("emptyframes", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("mismatch", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("nobody", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                };
                return s;
            },
            ["seed-boundary-instant-late-misses"] = () =>
            {
                // A surface lookup that throws for latitude 77 and saturates the opposite way
                // for latitude 88 (to +max) and for the Kepler side (to -max).
                var s = new RunScenario
                {
                    SurfaceLookup = (body, lat, lon, alt) =>
                    {
                        if (lat == 77) throw new InvalidOperationException("lookup");
                        if (lat == 88) return new Vector3d(1.7e308, 0, 0);
                        return new Vector3d(-1.7e308, 0, 0);
                    },
                };
                s.Recordings.Add(Rec("splinebody", Sec(4980, 5000, body: "Mun"), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("frametag", Sec(4980, 5000), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("throws",
                    Sec(4980, 5000, frames: new List<TrajectoryPoint> { Pt(4980, 77), Pt(5000, 77) }), Ckpt(5000, 5600)));
                s.Recordings.Add(Rec("infinite",
                    Sec(4980, 5000, frames: new List<TrajectoryPoint> { Pt(4980, 88), Pt(5000, 88) }), Ckpt(5000, 5600)));
                s.Arrange = () =>
                {
                    UseKerbin();
                    SectionAnnotationStore.PutSmoothingSpline("splinebody", 0, FlatSpline(4980, 5000, 1, 2, 70000));
                    SectionAnnotationStore.PutAnchorCandidates("splinebody", 0, new[] { C(5000, AnchorSource.OrbitalCheckpoint, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("frametag", 0, FlatSpline(4980, 5000, 1, 2, 70000, frameTag: 7));
                    SectionAnnotationStore.PutAnchorCandidates("frametag", 0, new[] { C(5000, AnchorSource.OrbitalCheckpoint, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("throws", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                    SectionAnnotationStore.PutAnchorCandidates("infinite", 1, new[] { C(5000, AnchorSource.BubbleExit) });
                };
                return s;
            },
            // Seed pass: what is not read.
            ["seed-skips-suppressed-null-and-empty"] = () =>
            {
                var s = new RunScenario { Resolver = new StubResolver { Loop = (id, sec, side, ut) => new Vector3d(1, 0, 0) }, Smoothed = (rec, sec, ut) => Vector3d.zero };
                s.Recordings.Add(null);
                s.Recordings.Add(new Recording { RecordingId = "nosections", TrackSections = null });
                s.Recordings.Add(Rec("suppressed", Sec(0, 100)));
                s.Recordings.Add(Rec("nocands", Sec(0, 100)));
                s.Recordings.Add(Rec("kept", Sec(0, 100)));
                s.Suppressed = id => id == "suppressed";
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutAnchorCandidates("suppressed", 0, new[] { C(10, AnchorSource.Loop) });
                    SectionAnnotationStore.PutAnchorCandidates("kept", 0, new[] { C(10, AnchorSource.Loop) });
                    SectionAnnotationStore.PutAnchorCandidates("kept", 5, new[] { C(10, AnchorSource.Loop) });
                };
                return s;
            },
            // Priority contention inside the seed pass and against a pre-seeded anchor.
            ["seed-priority-contention"] = () =>
            {
                var s = new RunScenario
                {
                    Resolver = new StubResolver
                    {
                        Rel = (id, sec, side, ut) => new Vector3d(1, 0, 0),
                        Loop = (id, sec, side, ut) => new Vector3d(3, 0, 0),
                    },
                    Smoothed = (rec, sec, ut) => Vector3d.zero,
                };
                s.Recordings.Add(Rec("A", Sec(0, 100), Sec(100, 200), Sec(200, 300), Sec(300, 400)));
                s.PreSeeds.Add(Seed("A", 3, AnchorSide.Start, 300, 50));
                s.Arrange = () =>
                {
                    // Section 0 Start: Loop (rank 2) then SurfaceContinuous (rank 6): the second loses.
                    // Section 0 End: SurfaceContinuous then Loop: the second wins.
                    SectionAnnotationStore.PutAnchorCandidates("A", 0, new[]
                    {
                        C(0, AnchorSource.Loop), C(0, AnchorSource.SurfaceContinuous),
                        C(100, AnchorSource.SurfaceContinuous, AnchorSide.End), C(100, AnchorSource.Loop, AnchorSide.End),
                    });
                    // Section 1 Start: SoiTransition then OrbitalCheckpoint (both rank 3): the lower
                    // enum (OrbitalCheckpoint) wins. Section 1 End: RelativeBoundary then Loop
                    // (both rank 2): the second (Loop, higher enum) loses.
                    SectionAnnotationStore.PutAnchorCandidates("A", 1, new[]
                    {
                        C(100, AnchorSource.SoiTransition), C(100, AnchorSource.OrbitalCheckpoint),
                        C(200, AnchorSource.RelativeBoundary, AnchorSide.End), C(200, AnchorSource.Loop, AnchorSide.End),
                    });
                    // Section 2 Start: three writes to one slot, the contention line logs once.
                    SectionAnnotationStore.PutAnchorCandidates("A", 2, new[]
                    {
                        C(200, AnchorSource.SurfaceContinuous), C(200, AnchorSource.BubbleExit), C(200, AnchorSource.BubbleEntry),
                    });
                    // Section 3 Start holds a LiveSeparation seed: a seed candidate loses.
                    SectionAnnotationStore.PutAnchorCandidates("A", 3, new[] { C(300, AnchorSource.RelativeBoundary) });
                };
                return s;
            },
            ["seed-bubble-radius-warning"] = () =>
            {
                var s = new RunScenario
                {
                    Resolver = new StubResolver
                    {
                        // A NaN reference on section 1 lands a NaN epsilon, which warns too.
                        Rel = (id, sec, side, ut) => sec == 1 ? new Vector3d(double.NaN, 0, 0) : new Vector3d(5000, 0, 0),
                        Loop = (id, sec, side, ut) => new Vector3d(2500, 0, 0),
                    },
                    Smoothed = (rec, sec, ut) => Vector3d.zero,
                };
                s.Recordings.Add(Rec("A", Sec(0, 100), Sec(100, 200)));
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutAnchorCandidates("A", 0, new[]
                    {
                        C(0, AnchorSource.RelativeBoundary), C(100, AnchorSource.Loop, AnchorSide.End),
                    });
                    SectionAnnotationStore.PutAnchorCandidates("A", 1, new[] { C(150, AnchorSource.RelativeBoundary) });
                };
                return s;
            },
            // Seed pass: SoiTransition, read at the boundary instant with the checkpoint conic
            // carried into the Absolute side's body.
            ["seed-soi-transition-hits"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                // K: Kerbin Absolute then a conic around the Mun (an SOI entry, End side).
                s.Recordings.Add(Rec("K", Sec(4980, 5000), Ckpt(5000, 5600, body: "Mun")));
                // M: a conic around Kerbin then Mun Absolute (Start side).
                s.Recordings.Add(Rec("M", Ckpt(4400, 5000), Sec(5000, 5020, body: "Mun")));
                s.Arrange = () =>
                {
                    UseKerbinAndMun();
                    SectionAnnotationStore.PutSmoothingSpline("K", 0, FlatSpline(4980, 5000, 1f, 2f, 9000000f));
                    SectionAnnotationStore.PutAnchorCandidates("K", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("M", 1, FlatSpline(5000, 5020, -3f, 40f, 2000000f));
                    SectionAnnotationStore.PutAnchorCandidates("M", 1, new[] { C(5000, AnchorSource.SoiTransition) });
                };
                return s;
            },
            ["seed-soi-transition-misses"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                s.Recordings.Add(Rec("nospline", Sec(4980, 5000), Ckpt(5000, 5600, body: "Mun")));
                s.Recordings.Add(Rec("edge", Sec(4980, 5000)));
                s.Recordings.Add(Rec("nockpt", Sec(4980, 5000), Sec(5000, 5600)));
                s.Recordings.Add(Rec("splinebody", Sec(4980, 5000, body: "Duna"), Ckpt(5000, 5600, body: "Mun")));
                s.Recordings.Add(Rec("conicbody", Sec(4980, 5000), Ckpt(5000, 5600, body: "Duna")));
                s.Recordings.Add(Rec("frametag", Sec(4980, 5000), Ckpt(5000, 5600, body: "Mun")));
                s.Arrange = () =>
                {
                    UseKerbinAndMun();
                    SectionAnnotationStore.PutAnchorCandidates("nospline", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("edge", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutAnchorCandidates("nockpt", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("splinebody", 0, FlatSpline(4980, 5000, 1, 2, 70000));
                    SectionAnnotationStore.PutAnchorCandidates("splinebody", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("conicbody", 0, FlatSpline(4980, 5000, 1, 2, 70000));
                    SectionAnnotationStore.PutAnchorCandidates("conicbody", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                    SectionAnnotationStore.PutSmoothingSpline("frametag", 0, FlatSpline(4980, 5000, 1, 2, 70000, frameTag: 7));
                    SectionAnnotationStore.PutAnchorCandidates("frametag", 0, new[] { C(5000, AnchorSource.SoiTransition, AnchorSide.End) });
                };
                return s;
            },
            // Edge pass: which branch points become edges.
            ["edge-branch-point-types"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                var types = new[]
                {
                    BranchPointType.Undock, BranchPointType.EVA, BranchPointType.Dock, BranchPointType.Board,
                    BranchPointType.JointBreak, BranchPointType.Launch, BranchPointType.Breakup, BranchPointType.Terminal,
                    BranchPointType.VesselSwitchContinuation, BranchPointType.GroundPartPlaced,
                };
                var bps = new List<BranchPoint>();
                for (int i = 0; i < types.Length; i++)
                {
                    string child = "C" + i.ToString(CultureInfo.InvariantCulture);
                    s.Recordings.Add(Rec(child, Sec(40, 140)));
                    bps.Add(Bp("bp" + i, 50 + i, types[i], new[] { "P" }, new[] { child }));
                }
                s.Trees.Add(Tree("t", bps.ToArray()));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                return s;
            },
            ["edge-malformed-branch-points"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("Q", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(0, 100)));
                s.Trees.Add(null);
                var noBps = new RecordingTree { Id = "nobps" };
                noBps.BranchPoints = null;
                s.Trees.Add(noBps);
                s.Trees.Add(Tree("t",
                    null,
                    Bp("nullparents", 10, BranchPointType.Dock, null, new[] { "C" }),
                    Bp("nullchildren", 11, BranchPointType.Dock, new[] { "P" }, null),
                    Bp("emptyids", 12, BranchPointType.Dock, new[] { "", null, "P" }, new[] { null, "", "C" }),
                    Bp("self", 13, BranchPointType.Dock, new[] { "P" }, new[] { "P" }),
                    Bp("many", 14, BranchPointType.Undock, new[] { "P", "Q" }, new[] { "C", "Q" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.PreSeeds.Add(Seed("Q", 0, AnchorSide.End, 100, 9));
                return s;
            },
            ["edge-breakup-debris-and-unknown-child"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("stage", Sec(50, 150)));
                var debris = Rec("debris", Sec(50, 60));
                debris.IsDebris = true;
                s.Recordings.Add(debris);
                var debrisViaUndock = Rec("debris-undock", Sec(50, 60));
                debrisViaUndock.IsDebris = true;
                s.Recordings.Add(debrisViaUndock);
                s.Trees.Add(Tree("t",
                    Bp("breakup", 75, BranchPointType.Breakup, new[] { "P" }, new[] { "stage", "debris", "ghost-child" }),
                    Bp("undock", 55, BranchPointType.Undock, new[] { "P" }, new[] { "debris-undock" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 9));
                return s;
            },
            ["edge-chains"] = () =>
            {
                var s = new RunScenario();
                Recording Chain(string id, string chainId, int index, double start, double end)
                {
                    var r = Rec(id, Sec(start, end));
                    r.ChainId = chainId;
                    r.ChainIndex = index;
                    r.Points.Add(Pt(start));
                    r.Points.Add(Pt(end));
                    return r;
                }
                // Chain "k" listed out of index order, abutting.
                s.Recordings.Add(Chain("k2", "k", 2, 200, 300));
                s.Recordings.Add(Chain("k0", "k", 0, 0, 100));
                s.Recordings.Add(Chain("k1", "k", 1, 100, 200));
                // Chain "t": the child starts inside the 1 ms boundary tolerance but after the
                // parent's last section ends.
                s.Recordings.Add(Chain("t0", "t", 0, 0, 100));
                s.Recordings.Add(Chain("t1", "t", 1, 100.0005, 200));
                // Chain "g" with a gap between members.
                s.Recordings.Add(Chain("g0", "g", 0, 0, 100));
                s.Recordings.Add(Chain("g1", "g", 1, 100.5, 200));
                // A lone member, an unindexed member, and an unchained recording.
                s.Recordings.Add(Chain("lone", "lone", 0, 0, 100));
                s.Recordings.Add(Chain("unindexed", "k", -1, 300, 400));
                s.Recordings.Add(Chain("nochain", null, 3, 0, 100));
                s.Recordings.Add(null);
                s.PreSeeds.Add(Seed("k0", 0, AnchorSide.End, 100, 3));
                s.PreSeeds.Add(Seed("g0", 0, AnchorSide.End, 100, 4));
                s.PreSeeds.Add(Seed("t0", 0, AnchorSide.End, 100, 6));
                return s;
            },
            ["edge-outgoing-index-misses"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(new Recording { RecordingId = "nosec", TrackSections = null });
                s.Recordings.Add(Rec("C", Sec(0, 300)));
                s.Trees.Add(Tree("t",
                    Bp("unknownparent", 50, BranchPointType.Dock, new[] { "ghost" }, new[] { "C" }),
                    Bp("nosections", 50, BranchPointType.Dock, new[] { "nosec" }, new[] { "C" }),
                    Bp("outside", 250, BranchPointType.Dock, new[] { "P" }, new[] { "C" }),
                    Bp("inside", 60, BranchPointType.Dock, new[] { "P" }, new[] { "C" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 2));
                return s;
            },
            ["edge-cycle-duplicate"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(50, 150)));
                s.Trees.Add(Tree("t1", Bp("a", 75, BranchPointType.Dock, new[] { "P" }, new[] { "C" })));
                s.Trees.Add(Tree("t2", Bp("b", 75, BranchPointType.Undock, new[] { "P" }, new[] { "C" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                return s;
            },
            ["edge-suppressed"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("SP", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(50, 150)));
                s.Recordings.Add(Rec("SC", Sec(50, 150)));
                s.Trees.Add(Tree("t",
                    Bp("to-suppressed-child", 60, BranchPointType.Dock, new[] { "P" }, new[] { "SC" }),
                    Bp("from-suppressed-parent", 61, BranchPointType.Dock, new[] { "SP" }, new[] { "C" }),
                    Bp("both", 62, BranchPointType.Dock, new[] { "SP" }, new[] { "SC" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.PreSeeds.Add(Seed("SP", 0, AnchorSide.Start, 0, 6));
                s.Suppressed = id => id == "SP" || id == "SC";
                return s;
            },
            ["edge-missing-child-and-child-section"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("late", Sec(80, 150)));
                s.Recordings.Add(new Recording { RecordingId = "nosec", TrackSections = null });
                s.Trees.Add(Tree("t",
                    Bp("child-has-no-section-at-ut", 60, BranchPointType.Dock, new[] { "P" }, new[] { "late" }),
                    Bp("child-has-no-sections", 61, BranchPointType.Dock, new[] { "P" }, new[] { "nosec" }),
                    Bp("child-unknown", 62, BranchPointType.Dock, new[] { "P" }, new[] { "ghost" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                return s;
            },
            ["edge-upstream-end-preferred"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("Q", Sec(0, 100)));
                s.Recordings.Add(Rec("C1", Sec(50, 150)));
                s.Recordings.Add(Rec("C2", Sec(50, 150)));
                s.Trees.Add(Tree("t",
                    Bp("from-both", 60, BranchPointType.Dock, new[] { "P" }, new[] { "C1" }),
                    Bp("from-start-only", 60, BranchPointType.Dock, new[] { "Q" }, new[] { "C2" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 1));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.End, 100, 2));
                s.PreSeeds.Add(Seed("Q", 0, AnchorSide.Start, 0, 3));
                return s;
            },
            ["edge-nine-one-applied"] = () =>
            {
                // Recorded samples come from rec.Points; with no spline the smoothed side mirrors
                // the recorded side (no-spline-skip, splineHit=false). D carries splines on both
                // sides (applied cleanly), E has a spline on the parent only.
                var s = new RunScenario { SurfaceLookup = Identity };
                Recording WithPoints(string id, double start, double end, double lat)
                {
                    var r = Rec(id, Sec(start, end));
                    r.Points.Add(Pt(start, lat));
                    r.Points.Add(Pt(60, lat));
                    r.Points.Add(Pt(end, lat));
                    return r;
                }
                s.Recordings.Add(WithPoints("P", 0, 100, 100));
                s.Recordings.Add(WithPoints("C", 50, 150, 110));
                s.Recordings.Add(WithPoints("D", 50, 150, 130));
                s.Trees.Add(Tree("t",
                    Bp("plain", 60, BranchPointType.Dock, new[] { "P" }, new[] { "C" }),
                    Bp("splined", 60, BranchPointType.Undock, new[] { "P" }, new[] { "D" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 3));
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutSmoothingSpline("P", 0, FlatSpline(0, 100, 101, 0, 0));
                    SectionAnnotationStore.PutSmoothingSpline("D", 0, FlatSpline(50, 150, 125, 0, 0));
                };
                return s;
            },
            ["edge-nine-one-fallbacks"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                var p = Rec("P", Sec(0, 100));
                p.Points.Add(Pt(70, 1));
                s.Recordings.Add(p);
                var rel = Rec("rel", Sec(50, 150, frame: ReferenceFrame.Relative));
                rel.Points.Add(Pt(70, 2));
                s.Recordings.Add(rel);
                s.Recordings.Add(Rec("nosample", Sec(50, 150)));
                var badbody = Rec("badbody", Sec(50, 150));
                badbody.Points.Add(Pt(70, 3, body: null));
                s.Recordings.Add(badbody);
                var relParentChild = Rec("rp-child", Sec(50, 150));
                relParentChild.Points.Add(Pt(70, 4));
                s.Recordings.Add(relParentChild);
                var relParent = Rec("rp", Sec(0, 100, frame: ReferenceFrame.Relative));
                s.Recordings.Add(relParent);
                s.Trees.Add(Tree("t",
                    Bp("to-relative", 70, BranchPointType.Dock, new[] { "P" }, new[] { "rel" }),
                    Bp("to-nosample", 70, BranchPointType.Dock, new[] { "P" }, new[] { "nosample" }),
                    Bp("to-badbody", 70, BranchPointType.Dock, new[] { "P" }, new[] { "badbody" }),
                    Bp("from-relative", 70, BranchPointType.Dock, new[] { "rp" }, new[] { "rp-child" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.PreSeeds.Add(Seed("rp", 0, AnchorSide.Start, 0, 6));
                return s;
            },
            ["edge-offset-precedence-and-edge-order"] = () =>
            {
                var s = new RunScenario { SurfaceLookup = Identity };
                // Both sides of one edge fail: the parent's reason names the fallback tag.
                s.Recordings.Add(Rec("rp", Sec(0, 100, frame: ReferenceFrame.Relative)));
                s.Recordings.Add(Rec("nosample", Sec(50, 150)));
                // Both sides sampled, only the child splined: the offsets still apply.
                var p = Rec("P", Sec(0, 100));
                p.Points.Add(Pt(70, 1));
                s.Recordings.Add(p);
                var c = Rec("C", Sec(50, 150));
                c.Points.Add(Pt(70, 2));
                s.Recordings.Add(c);
                // One parent slot with a chain edge and a branch-point edge: the branch-point
                // edges are built first, so they walk first.
                var k0 = Rec("k0", Sec(0, 100));
                k0.ChainId = "k"; k0.ChainIndex = 0;
                k0.Points.Add(Pt(0)); k0.Points.Add(Pt(100));
                var k1 = Rec("k1", Sec(100, 200));
                k1.ChainId = "k"; k1.ChainIndex = 1;
                k1.Points.Add(Pt(100)); k1.Points.Add(Pt(200));
                s.Recordings.Add(k1);
                s.Recordings.Add(k0);
                s.Recordings.Add(Rec("X", Sec(50, 150)));
                s.Trees.Add(Tree("t",
                    Bp("both-fail", 70, BranchPointType.Dock, new[] { "rp" }, new[] { "nosample" }),
                    Bp("child-splined", 70, BranchPointType.Undock, new[] { "P" }, new[] { "C" }),
                    Bp("beside-chain", 60, BranchPointType.Dock, new[] { "k0" }, new[] { "X" })));
                s.PreSeeds.Add(Seed("rp", 0, AnchorSide.Start, 0, 6));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.PreSeeds.Add(Seed("k0", 0, AnchorSide.Start, 0, 4));
                s.Arrange = () => SectionAnnotationStore.PutSmoothingSpline("C", 0, FlatSpline(50, 150, 3, 0, 0));
                return s;
            },
            ["edge-no-surface-lookup"] = () =>
            {
                var s = new RunScenario();
                var p = Rec("P", Sec(0, 100));
                p.Points.Add(Pt(70, 1));
                var c = Rec("C", Sec(50, 150));
                c.Points.Add(Pt(70, 2));
                s.Recordings.Add(p);
                s.Recordings.Add(c);
                s.Trees.Add(Tree("t", Bp("d", 70, BranchPointType.Dock, new[] { "P" }, new[] { "C" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                return s;
            },
            ["edge-deferred-by-priority-and-fan-in"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("Q", Sec(0, 100)));
                s.Recordings.Add(Rec("held", Sec(50, 150)));
                s.Recordings.Add(Rec("fan", Sec(50, 150)));
                s.Recordings.Add(Rec("down", Sec(100, 200)));
                s.Trees.Add(Tree("t",
                    Bp("to-held", 60, BranchPointType.Dock, new[] { "P" }, new[] { "held" }),
                    Bp("fan-a", 61, BranchPointType.Dock, new[] { "P" }, new[] { "fan" }),
                    Bp("fan-b", 62, BranchPointType.Dock, new[] { "Q" }, new[] { "fan" }),
                    Bp("from-held", 120, BranchPointType.Dock, new[] { "held" }, new[] { "down" }),
                    Bp("from-fan", 130, BranchPointType.Dock, new[] { "fan" }, new[] { "down" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.PreSeeds.Add(Seed("Q", 0, AnchorSide.Start, 0, 7));
                s.PreSeeds.Add(Seed("held", 0, AnchorSide.Start, 50, 1));
                return s;
            },
            ["edge-multi-hop-and-radius-warning"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("C", Sec(100, 200)));
                s.Recordings.Add(Rec("B", Sec(50, 150)));
                s.Recordings.Add(Rec("A", Sec(0, 100)));
                s.Trees.Add(Tree("t",
                    Bp("bc", 120, BranchPointType.Dock, new[] { "B" }, new[] { "C" }),
                    Bp("ab", 60, BranchPointType.EVA, new[] { "A" }, new[] { "B" })));
                s.PreSeeds.Add(Seed("A", 0, AnchorSide.Start, 0, 3000, y: 4));
                return s;
            },
            ["edge-two-section-parent-deferred-slot"] = () =>
            {
                var s = new RunScenario();
                s.Recordings.Add(Rec("B", Sec(0, 100), Sec(100, 200)));
                s.Recordings.Add(Rec("C", Sec(200, 300)));
                s.Recordings.Add(Rec("A", Sec(0, 200)));
                s.Trees.Add(Tree("t",
                    Bp("bc", 200, BranchPointType.Dock, new[] { "B" }, new[] { "C" }),
                    Bp("ab", 150, BranchPointType.Dock, new[] { "A" }, new[] { "B" })));
                s.PreSeeds.Add(Seed("A", 0, AnchorSide.Start, 0, 7));
                s.PreSeeds.Add(Seed("B", 0, AnchorSide.End, 100, 11));
                return s;
            },
            ["edge-seed-feeds-walk"] = () =>
            {
                // A seed candidate written by the seed pass anchors the parent slot the walk reads.
                var s = new RunScenario
                {
                    Resolver = new StubResolver { Loop = (id, sec, side, ut) => new Vector3d(4, 0, 0) },
                    Smoothed = (rec, sec, ut) => new Vector3d(1, 0, 0),
                };
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(50, 150)));
                s.Trees.Add(Tree("t", Bp("d", 60, BranchPointType.Board, new[] { "P" }, new[] { "C" })));
                s.Arrange = () =>
                {
                    SectionAnnotationStore.PutAnchorCandidates("P", 0, new[] { C(0, AnchorSource.Loop) });
                    SectionAnnotationStore.PutAnchorCandidates("C", 0, new[] { C(50, AnchorSource.SurfaceContinuous) });
                };
                return s;
            },
            // Inputs that are absent.
            ["empty-inputs"] = () => new RunScenario { Recordings = null, Trees = null, Marker = null },
            ["no-seed-no-walk"] = () =>
            {
                var s = new RunScenario { Marker = new ReFlySessionMarker() };
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(50, 150)));
                s.Trees.Add(Tree("t", Bp("d", 60, BranchPointType.Dock, new[] { "P" }, new[] { "C" })));
                return s;
            },
            ["duplicate-recording-ids"] = () =>
            {
                // byId keeps the LAST recording with an id; the seed pass reads every list entry.
                var s = new RunScenario { Resolver = new StubResolver { Loop = (id, sec, side, ut) => new Vector3d(ut, 0, 0) }, Smoothed = (rec, sec, ut) => Vector3d.zero };
                s.Recordings.Add(Rec("P", Sec(0, 100)));
                s.Recordings.Add(Rec("C", Sec(500, 600)));
                s.Recordings.Add(Rec("C", Sec(50, 150)));
                s.Recordings.Add(new Recording { RecordingId = "", VesselName = "noid" });
                // The same parent listed twice: its slot is enqueued once.
                s.Recordings.Add(s.Recordings[0]);
                s.Trees.Add(Tree("t", Bp("d", 60, BranchPointType.Dock, new[] { "P" }, new[] { "C" })));
                s.PreSeeds.Add(Seed("P", 0, AnchorSide.Start, 0, 5));
                s.Arrange = () => SectionAnnotationStore.PutAnchorCandidates("C", 0, new[] { C(10, AnchorSource.Loop, AnchorSide.End) });
                return s;
            },
        };

        public static IEnumerable<object[]> ScenarioNames()
        {
            foreach (string name in Scenarios.Keys)
                yield return new object[] { name };
        }

        // ---- cells ----------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(ScenarioNames))]
        public void Run_TranscriptMatchesTheCharacterizedMethod(string name)
        {
            Assert.True(Expected.TryGetValue(name, out string[] expected), "no expected transcript for " + name);
            List<string> actual = Transcript(name);
            Assert.Equal(expected, actual.ToArray());
        }

        [Fact]
        public void EveryExpectedTranscriptHasAScenario()
        {
            Assert.Equal(Expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
                Scenarios.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void Summary_CountsEverySeedAndWalkOutcome()
        {
            List<string> t = Transcript("seed-priority-contention");
            string summary = Assert.Single(t, l => l.Contains("DAG walk summary:"));
            Assert.StartsWith("[Parsek][INFO][Pipeline-AnchorPropagate] DAG walk summary: sessionId=char ", summary);
            // Written: section 0 Start Loop, 0 End SurfaceContinuous then Loop, 1 Start
            // SoiTransition then OrbitalCheckpoint (rank tie, lower enum), 1 End RelativeBoundary,
            // 2 Start SurfaceContinuous then BubbleExit then BubbleEntry (rank tie, lower enum
            // each time). Lost: 0 Start SurfaceContinuous, 1 End Loop, 3 Start RelativeBoundary
            // (against the LiveSeparation seed).
            Assert.Contains("seedCandidatesEmitted=9 candidatesDeferredByPriority=3", summary);
            Assert.Contains("edgesVisited=0 edgesPropagated=0", summary);
            Assert.EndsWith("boundaryInstantMaxEpsilonM=0.000", summary);
        }

        [Fact]
        public void Seed_ResolverSourcesWriteWorldRefMinusSmoothed()
        {
            List<string> t = Transcript("seed-resolver-hits");
            Assert.Contains("anchor A|0|Start source=RelativeBoundary ut=0 eps=(9,18,27)", t);
            Assert.Contains("anchor A|1|Start source=Loop ut=150 eps=(6,6,6.5)", t);
            Assert.Contains(t, l => l.Contains("resolvedRel=1 resolvedOrb=0 resolvedSoi=0 resolvedLoop=1 resolvedBubble=0 deferredNoResolver=0 deferredNoSpline=0"));
            // SoiTransition never asks the resolver: it is read at the boundary instant, and
            // here it has no checkpoint neighbour.
            Assert.Contains("anchor A|0|End source=SoiTransition ut=100 eps=(0,0,0)", t);
            Assert.DoesNotContain(t, l => l.StartsWith("[stub] soi"));
        }

        [Fact]
        public void Seed_SoiTransitionResolvesAtTheBoundaryInstantInBothDirections()
        {
            List<string> t = Transcript("seed-soi-transition-hits");
            Assert.Contains(t, l => l.Contains("resolvedSoi=2 ") && l.Contains("resolvedOrb=0 "));
            Assert.Contains(t, l => l.StartsWith("anchor K|0|End source=SoiTransition") && !l.EndsWith("eps=(0,0,0)"));
            Assert.Contains(t, l => l.StartsWith("anchor M|1|Start source=SoiTransition") && !l.EndsWith("eps=(0,0,0)"));
            Assert.DoesNotContain(t, l => l.Contains("boundaryInstantMaxEpsilonM=0.000"));

            List<string> m = Transcript("seed-soi-transition-misses");
            // Only the missing spline counts as deferredNoSpline; every miss still holds its slot.
            Assert.Contains(m, l => l.Contains("resolvedSoi=0 ") && l.Contains("deferredNoSpline=1 ") && l.Contains("seedCandidatesEmitted=6 "));
            Assert.Contains(m, l => l.Contains("recordingId=conicbody") && l.EndsWith("reason=soi-kepler-unresolved"));
        }

        [Fact]
        public void Seed_AResolverHitWithNoSmoothedPositionCountsBothAsResolvedAndAsDeferred()
        {
            // Pinned as found: the resolved counters count resolver hits, so a candidate whose
            // smoothed position is missing is counted once under resolved* and once under
            // deferredNoSpline, and its anchor is the zero stub.
            List<string> t = Transcript("seed-resolver-hit-no-smoothed");
            Assert.Contains(t, l => l.Contains("resolvedRel=1 resolvedOrb=0 resolvedSoi=0 resolvedLoop=1 resolvedBubble=0 deferredNoResolver=0 deferredNoSpline=2"));
            Assert.Contains("anchor A|0|Start source=RelativeBoundary ut=0 eps=(0,0,0)", t);
        }

        [Fact]
        public void Seed_BubbleRadiusWarningFollowsTheLandedValue()
        {
            List<string> t = Transcript("seed-bubble-radius-warning");
            Assert.Contains(t, l => l.StartsWith("[Parsek][WARN][Pipeline-Anchor] Anchor") && l.Contains("anchorSource=RelativeBoundary epsilonMagnitudeM=5000 "));
            Assert.Contains(t, l => l.StartsWith("[Parsek][WARN][Pipeline-Anchor] Anchor") && l.Contains("sectionIndex=1 ") && l.Contains("anchorSource=RelativeBoundary epsilonMagnitudeM=NaN "));
            // Exactly the radius is not over it.
            Assert.DoesNotContain(t, l => l.Contains("exceeds bubble radius") && l.Contains("anchorSource=Loop"));
        }

        [Fact]
        public void Walk_PriorityLosersCountAsDeferred()
        {
            List<string> t = Transcript("edge-deferred-by-priority-and-fan-in");
            // held keeps its LiveSeparation seed; the fan-in's second write and the second write
            // into down lose the DockOrMerge tie.
            Assert.Contains("anchor held|0|Start source=LiveSeparation ut=50 eps=(1,0,0)", t);
            Assert.Contains("anchor fan|0|Start source=DockOrMerge ut=61 eps=(5,0,0)", t);
            Assert.Contains("anchor down|0|Start source=DockOrMerge ut=120 eps=(1,0,0)", t);
            Assert.Contains(t, l => l.Contains("edgesVisited=5 edgesPropagated=2") && l.Contains("candidatesDeferredByPriority=3"));
        }

        [Fact]
        public void Walk_AChainChildInsideTheToleranceButPastTheParentsSectionsGetsNoEdge()
        {
            // Pinned as found: the chain builder accepts a boundary gap up to 1 ms, but the edge
            // index looks the parent section up at the CHILD's start UT, which lies past the
            // parent's last section, so the edge is dropped without a log line.
            List<string> t = Transcript("edge-chains");
            Assert.Contains(t, l => l.Contains("Edge propagated: k0->k1 bpType=chain"));
            Assert.Contains(t, l => l.Contains("Edge propagated: k1->k2 bpType=chain"));
            Assert.DoesNotContain(t, l => l.Contains("t0->t1") || l.Contains("child=t1"));
            Assert.DoesNotContain(t, l => l.StartsWith("anchor t1|"));
        }
    }
}
