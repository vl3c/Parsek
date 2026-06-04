using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Display;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="GhostTrajectoryPolylineRenderer"/>'s pure
    /// builder + cache helpers (design plan
    /// docs/dev/plans/map-trajectory-polyline.md §3 / §5.1). The Driver
    /// MonoBehaviour + Vectrosity submission live in commit 2 and are
    /// covered by §5.2 / in-game tests.
    /// </summary>
    [Collection("Sequential")]
    public class GhostTrajectoryPolylineBuildTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostTrajectoryPolylineBuildTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostTrajectoryPolylineRenderer.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostTrajectoryPolylineRenderer.Clear();
        }

        // --- BuildLegsForRecording: per-section dispatch ---

        [Fact]
        public void BuildLegs_NoTrackSections_OneLegCoveringRecordingPoints()
        {
            // Pure flat Recording.Points recording -> one fallback leg.
            var rec = new Recording { RecordingId = "rec-flat" };
            rec.Points.Add(MakePoint(100.0, -0.1, -74.5, 70.0));
            rec.Points.Add(MakePoint(200.0, -0.05, -74.5, 20000.0));
            rec.Points.Add(MakePoint(300.0, 0.0, -74.5, 100000.0));

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(3, legs[0].PointCount);
            Assert.Equal("Kerbin", legs[0].bodyName);
            Assert.Equal(100.0, legs[0].startUT);
            Assert.Equal(300.0, legs[0].endUT);
            // The builder caches the recorded body-fixed (lat, lon, alt)
            // triples verbatim -- no geometry conversion happens in the pure
            // builder; the Driver converts via GetWorldSurfacePosition per
            // frame. Verify the triples match the source points index-wise.
            Assert.Equal(new[] { -0.1, -0.05, 0.0 }, legs[0].lats);
            Assert.Equal(new[] { -74.5, -74.5, -74.5 }, legs[0].lons);
            Assert.Equal(new[] { 70.0, 20000.0, 100000.0 }, legs[0].alts);
        }

        [Fact]
        public void BuildLegs_SingleAbsoluteSection_OneLegFromSectionFrames()
        {
            var rec = new Recording { RecordingId = "rec-abs" };
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(100.0, -0.1, -74.5, 70.0),
                MakePoint(150.0, -0.05, -74.5, 5000.0),
                MakePoint(200.0, 0.0, -74.5, 30000.0)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 100.0, endUT = 200.0,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 10f
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(3, legs[0].PointCount);
            Assert.Equal("Kerbin", legs[0].bodyName);
        }

        [Fact]
        public void BuildLegs_OrbitSegmentOnly_NoLegs()
        {
            var rec = new Recording { RecordingId = "rec-orbit-only" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 0.0,
                endUT = 600.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0
            });
            // Flat Points entirely inside the orbital cover.
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 100000.0));
            rec.Points.Add(MakePoint(500.0, 0.0, 0.0, 100000.0));

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Empty(legs);
        }

        [Fact]
        public void BuildLegs_AbsoluteSectionThenOrbitSegment_OneLegBeforeOrbit()
        {
            // Ascent leg [0,200] followed by parking orbit [200,1000].
            var rec = new Recording { RecordingId = "rec-ascent-then-orbit" };
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(0.0, -0.1, -74.5, 70.0),
                MakePoint(100.0, -0.05, -74.5, 30000.0),
                MakePoint(200.0, 0.0, -74.5, 90000.0)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 0.0, endUT = 200.0,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 10f
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200.0, endUT = 1000.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal("Kerbin", legs[0].bodyName);
            // Section endUT lands exactly at the orbital interval start -- the
            // filter excludes UT >= orbitalStart, so the trailing point at
            // ut=200 is suppressed. The leg should have the first two frames.
            Assert.Equal(2, legs[0].PointCount);
        }

        [Fact]
        public void BuildLegs_TwoOrbitSegmentsWithGap_OneLegBetween()
        {
            // Two parking orbits with a capture-burn gap in between covered
            // by an Absolute section, plus flat Points across all three
            // intervals.
            var rec = new Recording { RecordingId = "rec-capture" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 0.0, endUT = 200.0,
                bodyName = "Kerbin", semiMajorAxis = 700000.0
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 300.0, endUT = 500.0,
                bodyName = "Kerbin", semiMajorAxis = 850000.0
            });
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(210.0, 0.0, 0.0, 100000.0),
                MakePoint(250.0, 0.0, 0.0, 105000.0),
                MakePoint(290.0, 0.0, 0.0, 110000.0)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoPropulsive,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 200.0, endUT = 300.0,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 10f
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(3, legs[0].PointCount);
        }

        [Fact]
        public void BuildLegs_RelativeSectionWithBodyFixedFrames_UsesBodyFixed()
        {
            // RELATIVE section with full bodyFixedFrames coverage -- the
            // builder MUST source from bodyFixedFrames, NOT frames (which in
            // a real RELATIVE section carry anchor-local metre offsets, not
            // lat/lon/alt).
            var rec = new Recording { RecordingId = "rec-rel-bodyfixed" };
            var relativeFramesBogusInterpretation = new List<TrajectoryPoint>
            {
                MakePoint(100.0, 1.5e6, -2.3e6, 3.4e6),  // metres, NOT lat/lon/alt
                MakePoint(150.0, 1.4e6, -2.4e6, 3.5e6),
                MakePoint(200.0, 1.3e6, -2.5e6, 3.6e6)
            };
            var bodyFixed = new List<TrajectoryPoint>
            {
                MakePoint(100.0, 0.2, -74.5, 80000.0),
                MakePoint(150.0, 0.25, -74.5, 82000.0),
                MakePoint(200.0, 0.3, -74.5, 84000.0)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = 100.0, endUT = 200.0,
                frames = relativeFramesBogusInterpretation,
                bodyFixedFrames = bodyFixed,
                checkpoints = new List<OrbitSegment>(),
                anchorRecordingId = "anchor-rec",
                sampleRateHz = 10f
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(3, legs[0].PointCount);
            // Body resolved from the body-fixed list, not the anchor-local
            // bogus list.
            Assert.Equal("Kerbin", legs[0].bodyName);
            // The cached lat/lon/alt triples MUST come from bodyFixedFrames
            // (real spherical lat/lon/alt), NOT from frames (anchor-local
            // metre offsets). Reading the metre offsets as lat/lon/alt is the
            // CLAUDE.md RELATIVE-frame footgun that puts the leg inside the
            // planet. Assert the cached lats are the body-fixed degrees.
            Assert.Equal(new[] { 0.2, 0.25, 0.3 }, legs[0].lats);
            Assert.Equal(new[] { 80000.0, 82000.0, 84000.0 }, legs[0].alts);
        }

        [Fact]
        public void BuildLegs_RelativeSectionWithoutBodyFixedFrames_SkipsLeg()
        {
            // RELATIVE section with null bodyFixedFrames -- the builder
            // MUST skip the leg rather than fall through to frames (which
            // are anchor-local metres, not lat/lon/alt). Reading them as
            // lat/lon/alt would place the leg deep inside the planet.
            var rec = new Recording { RecordingId = "rec-rel-no-bodyfixed" };
            var anchorLocalMetres = new List<TrajectoryPoint>
            {
                MakePoint(100.0, 1.5e6, -2.3e6, 3.4e6),
                MakePoint(150.0, 1.4e6, -2.4e6, 3.5e6),
                MakePoint(200.0, 1.3e6, -2.5e6, 3.6e6)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                source = TrackSectionSource.Active,
                startUT = 100.0, endUT = 200.0,
                frames = anchorLocalMetres,
                bodyFixedFrames = null,
                checkpoints = new List<OrbitSegment>(),
                anchorRecordingId = "anchor-rec",
                sampleRateHz = 10f
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Empty(legs);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]") && l.Contains("skippedRelNoBodyFixed=1"));
        }

        // --- Cache invalidation ---

        [Fact]
        public void RecordingMutates_ContentHashChanges()
        {
            var rec = new Recording { RecordingId = "rec-hash" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));
            long h1 = GhostTrajectoryPolylineRenderer.ComputeContentHash(rec);

            rec.Points.Add(MakePoint(300.0, 0.0, 0.0, 150.0));
            long h2 = GhostTrajectoryPolylineRenderer.ComputeContentHash(rec);

            Assert.NotEqual(h1, h2);
        }

        [Fact]
        public void ContentHash_TrackSectionUTChange_InvalidatesEvenWhenCountsPreserved()
        {
            // A supersede-time re-cut could preserve (Points.Count,
            // OrbitSegments.Count, TrackSections.Count, EndUT) -- the XOR
            // of every TrackSection.startUT/endUT MUST catch the in-place
            // rewrite (§1.4).
            var rec = new Recording { RecordingId = "rec-section-recut" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0, endUT = 200.0,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });
            long h1 = GhostTrajectoryPolylineRenderer.ComputeContentHash(rec);

            // Mutate the start in place. Same count, same EndUT.
            var ts = rec.TrackSections[0];
            ts.startUT = 110.0;
            rec.TrackSections[0] = ts;
            long h2 = GhostTrajectoryPolylineRenderer.ComputeContentHash(rec);

            Assert.NotEqual(h1, h2);
        }

        [Fact]
        public void RefreshForRecording_CacheHit_DoesNotRebuild()
        {
            var rec = new Recording { RecordingId = "rec-cache-hit" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));

            GhostTrajectoryPolylineRenderer.RefreshForRecording(rec);
            int firstCount = GhostTrajectoryPolylineRenderer.CacheCountForTesting;
            int initialBuildLogs =
                logLines.Count(l => l.Contains("[GhostMap]") && l.Contains("Polyline build:"));

            GhostTrajectoryPolylineRenderer.RefreshForRecording(rec);
            int secondBuildLogs =
                logLines.Count(l => l.Contains("[GhostMap]") && l.Contains("Polyline build:"));

            Assert.Equal(1, firstCount);
            Assert.Equal(GhostTrajectoryPolylineRenderer.CacheCountForTesting, firstCount);
            // Cache hit means the second call did NOT log a fresh "Polyline
            // build" line.
            Assert.Equal(initialBuildLogs, secondBuildLogs);
        }

        [Fact]
        public void RefreshForRecording_RecordingMutates_CacheInvalidates()
        {
            var rec = new Recording { RecordingId = "rec-rebuild" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));

            GhostTrajectoryPolylineRenderer.RefreshForRecording(rec);
            int buildsBefore =
                logLines.Count(l => l.Contains("[GhostMap]") && l.Contains("Polyline build:"));

            // Mutate -- append a new point. The hash flips.
            rec.Points.Add(MakePoint(300.0, 0.0, 0.0, 150.0));
            GhostTrajectoryPolylineRenderer.RefreshForRecording(rec);
            int buildsAfter =
                logLines.Count(l => l.Contains("[GhostMap]") && l.Contains("Polyline build:"));

            Assert.True(buildsAfter > buildsBefore, "expected a fresh polyline build after mutation");
        }

        [Fact]
        public void ReleaseForRecording_RemovesCachedEntryAndLogs()
        {
            var rec = new Recording { RecordingId = "rec-release" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));
            GhostTrajectoryPolylineRenderer.RefreshForRecording(rec);
            Assert.Equal(1, GhostTrajectoryPolylineRenderer.CacheCountForTesting);

            GhostTrajectoryPolylineRenderer.ReleaseForRecording("rec-release");

            Assert.Equal(0, GhostTrajectoryPolylineRenderer.CacheCountForTesting);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]") && l.Contains("Polyline cache release: rec=rec-release"));
        }

        [Fact]
        public void Clear_DropsEveryCachedEntry()
        {
            for (int i = 0; i < 3; i++)
            {
                var r = new Recording { RecordingId = "rec-bulk-" + i };
                r.Points.Add(MakePoint(100.0 + i, 0.0, 0.0, 70.0));
                r.Points.Add(MakePoint(200.0 + i, 0.0, 0.0, 100.0));
                GhostTrajectoryPolylineRenderer.RefreshForRecording(r);
            }
            Assert.Equal(3, GhostTrajectoryPolylineRenderer.CacheCountForTesting);

            GhostTrajectoryPolylineRenderer.Clear();

            Assert.Equal(0, GhostTrajectoryPolylineRenderer.CacheCountForTesting);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]") && l.Contains("Polyline cache clear: dropped=3"));
        }

        // --- Downsampling ---

        [Fact]
        public void Downsample_BelowCap_ReturnsAllPoints()
        {
            var pts = MakePointList(0, 50);
            var down = GhostTrajectoryPolylineRenderer.DownsamplePreservingEndpoints(pts, 200);
            Assert.Equal(50, down.Count);
            Assert.Equal(pts[0].ut, down[0].ut);
            Assert.Equal(pts[49].ut, down[49].ut);
        }

        [Fact]
        public void Downsample_AboveCap_PreservesEndpointsAndReturnsCapCount()
        {
            var pts = MakePointList(0, 1000);
            int cap = 200;
            var down = GhostTrajectoryPolylineRenderer.DownsamplePreservingEndpoints(pts, cap);
            Assert.Equal(cap, down.Count);
            Assert.Equal(pts[0].ut, down[0].ut);
            Assert.Equal(pts[999].ut, down[cap - 1].ut);
        }

        // --- Render-time densification (sparse-leg smoothing) ---

        [Fact]
        public void Densify_SparseArc_SubdividesToTarget()
        {
            // The s15 Duna deorbit leg shape: ~35 degrees of longitude over a
            // handful of samples (here 3, ~17.5 deg per chord) renders as a
            // faceted polygon. Densify must subdivide each chord so every drawn
            // segment spans at most the target angular step.
            double[] lats = { 0.0, 0.0, 0.0 };
            double[] lons = { 73.9, 91.0, 108.6 };
            double[] alts = { 58245.0, 54000.0, 49998.0 };
            double[] uts = { 70963373.6, 70963513.0, 70963652.6 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts,
                GhostTrajectoryPolylineRenderer.DensifyTargetSegmentDegrees,
                GhostTrajectoryPolylineRenderer.MaxDensifiedPointsPerLeg,
                out var oLats, out var oLons, out var oAlts, out var oUts);

            // Far more points than the 3 recorded samples (~35 deg / 0.3 deg).
            Assert.True(oLons.Length > 100,
                "expected dense subdivision, got " + oLons.Length);
            // No drawn segment exceeds the target angular step (allow a hair of
            // float slack).
            for (int i = 0; i < oLons.Length - 1; i++)
            {
                double seg = GhostTrajectoryPolylineRenderer.GreatCircleArcDegrees(
                    oLats[i], oLons[i], oLats[i + 1], oLons[i + 1]);
                Assert.True(seg <= GhostTrajectoryPolylineRenderer.DensifyTargetSegmentDegrees + 1e-6,
                    "segment " + i + " spanned " + seg + " deg");
            }
        }

        [Fact]
        public void Densify_PreservesRecordedEndpointsExactly()
        {
            double[] lats = { 1.0, 2.5 };
            double[] lons = { 73.9, 108.6 };
            double[] alts = { 58245.0, 49998.0 };
            double[] uts = { 1000.0, 2000.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out var oAlts, out var oUts);

            // First and last vertices are the recorded samples, byte-for-byte.
            Assert.Equal(lats[0], oLats[0]);
            Assert.Equal(lons[0], oLons[0]);
            Assert.Equal(alts[0], oAlts[0]);
            Assert.Equal(uts[0], oUts[0]);
            int last = oLats.Length - 1;
            Assert.Equal(lats[1], oLats[last]);
            Assert.Equal(lons[1], oLons[last]);
            Assert.Equal(alts[1], oAlts[last]);
            Assert.Equal(uts[1], oUts[last]);
        }

        [Fact]
        public void Densify_PreservesAllInteriorRecordedVerticesExactly()
        {
            // Recorded interior samples must remain vertices unchanged; only
            // points BETWEEN them are inserted.
            double[] lats = { 0.0, 0.0, 0.0 };
            double[] lons = { 0.0, 20.0, 40.0 };
            double[] alts = { 60000.0, 55000.0, 50000.0 };
            double[] uts = { 100.0, 200.0, 300.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out var oAlts, out var oUts);

            // Each recorded (lon, alt, ut) triple appears exactly in the output.
            for (int r = 0; r < lons.Length; r++)
            {
                int found = -1;
                for (int i = 0; i < oLons.Length; i++)
                {
                    if (oLons[i] == lons[r] && oAlts[i] == alts[r] && oUts[i] == uts[r])
                    {
                        found = i;
                        break;
                    }
                }
                Assert.True(found >= 0, "recorded sample " + r + " not preserved verbatim");
            }
        }

        [Fact]
        public void Densify_UTStrictlyIncreasing()
        {
            double[] lats = { 0.0, 0.0, 0.0 };
            double[] lons = { 0.0, 17.0, 34.0 };
            double[] alts = { 60000.0, 55000.0, 50000.0 };
            double[] uts = { 70963373.6, 70963513.0, 70963652.6 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out _, out _, out _, out var oUts);

            for (int i = 1; i < oUts.Length; i++)
                Assert.True(oUts[i] > oUts[i - 1],
                    "UT not strictly increasing at " + i + ": " + oUts[i - 1] + " -> " + oUts[i]);
        }

        [Fact]
        public void Densify_AlreadyDense_ReturnedUnchanged()
        {
            // A leg already finer than the target (0.01 deg/segment) must NOT be
            // touched: same length, identical values. This is the dense in-atmo
            // descent that must not regress or balloon.
            int n = 50;
            var lats = new double[n];
            var lons = new double[n];
            var alts = new double[n];
            var uts = new double[n];
            for (int i = 0; i < n; i++)
            {
                lats[i] = 0.0;
                lons[i] = i * 0.01; // 0.01 deg per segment, well under 0.3
                alts[i] = 50000.0 - i * 100.0;
                uts[i] = 1000.0 + i;
            }

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out var oAlts, out var oUts);

            Assert.Equal(n, oLons.Length);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(lats[i], oLats[i]);
                Assert.Equal(lons[i], oLons[i]);
                Assert.Equal(alts[i], oAlts[i]);
                Assert.Equal(uts[i], oUts[i]);
            }
        }

        [Fact]
        public void Densify_RespectsHardCap()
        {
            // A pathologically coarse arc (a near-half-circumference chord) would
            // need hundreds of inserts; the hard cap must bound the output.
            double[] lats = { 0.0, 0.0 };
            double[] lons = { -90.0, 90.0 }; // 180 deg great-circle arc
            double[] alts = { 60000.0, 50000.0 };
            double[] uts = { 100.0, 200.0 };
            int cap = 64;

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, cap,
                out var oLats, out var oLons, out _, out var oUts);

            Assert.True(oLons.Length <= cap,
                "output " + oLons.Length + " exceeded cap " + cap);
            // Endpoints still preserved even when capped.
            Assert.Equal(lons[0], oLons[0]);
            Assert.Equal(lons[1], oLons[oLons.Length - 1]);
            // UT still monotone under the cap.
            for (int i = 1; i < oUts.Length; i++)
                Assert.True(oUts[i] > oUts[i - 1]);
        }

        [Fact]
        public void Densify_LongitudeSeamCrossing_NoWildSwing()
        {
            // Crossing the +/-180 seam: a raw lat/lon lerp would swing the
            // inserted point ~360 deg the wrong way (through lon 0). Slerping the
            // surface unit normal keeps every inserted point on the short
            // great-circle arc, so consecutive inserted longitudes stay near the
            // seam (either ~+180 or ~-180), never near 0.
            double[] lats = { 0.0, 0.0 };
            double[] lons = { 175.0, -175.0 }; // 10 deg arc across the seam
            double[] alts = { 50000.0, 50000.0 };
            double[] uts = { 100.0, 200.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out _, out _);

            Assert.True(oLons.Length > 10);
            // Every interior point must be near the seam (|lon| > 170), never
            // near the far side (lon ~0).
            for (int i = 0; i < oLons.Length; i++)
                Assert.True(System.Math.Abs(oLons[i]) > 170.0,
                    "interpolant " + i + " swung to lon " + oLons[i] + " (wrong way across the seam)");
            // And each drawn segment is small (no chord through the body).
            for (int i = 0; i < oLons.Length - 1; i++)
            {
                double seg = GhostTrajectoryPolylineRenderer.GreatCircleArcDegrees(
                    oLats[i], oLons[i], oLats[i + 1], oLons[i + 1]);
                Assert.True(seg <= 0.3 + 1e-6, "segment " + i + " spanned " + seg + " deg");
            }
        }

        [Fact]
        public void Densify_PoleCrossing_StaysOnSphere()
        {
            // An arc passing near the pole: a raw lat/lon lerp distorts badly
            // there. Slerping the unit normal keeps every point on the unit
            // sphere, so re-deriving the great-circle arc per segment stays
            // bounded by the target.
            double[] lats = { 88.0, 88.0 };
            double[] lons = { 0.0, 180.0 }; // over the top of the pole
            double[] alts = { 50000.0, 50000.0 };
            double[] uts = { 100.0, 200.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out _, out _);

            for (int i = 0; i < oLats.Length; i++)
            {
                Assert.True(oLats[i] >= -90.0 && oLats[i] <= 90.0,
                    "lat out of range at " + i + ": " + oLats[i]);
                Assert.True(oLons[i] >= -180.0 - 1e-9 && oLons[i] <= 180.0 + 1e-9,
                    "lon out of range at " + i + ": " + oLons[i]);
            }
            for (int i = 0; i < oLats.Length - 1; i++)
            {
                double seg = GhostTrajectoryPolylineRenderer.GreatCircleArcDegrees(
                    oLats[i], oLons[i], oLats[i + 1], oLons[i + 1]);
                Assert.True(seg <= 0.3 + 1e-6, "segment " + i + " spanned " + seg + " deg");
            }
        }

        [Fact]
        public void Densify_AltitudeLinearlyInterpolated()
        {
            // Midpoint of a single chord must carry the linearly interpolated
            // altitude (the body-fixed contract: inserted points land at the
            // right altitude so GetWorldSurfacePosition maps them correctly).
            double[] lats = { 0.0, 0.0 };
            double[] lons = { 0.0, 1.0 };
            double[] alts = { 60000.0, 50000.0 };
            double[] uts = { 100.0, 200.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out _, out _, out var oAlts, out _);

            // Inserted altitudes are monotone between the two endpoints (60000 ->
            // 50000) and strictly inside the band.
            for (int i = 1; i < oAlts.Length - 1; i++)
            {
                Assert.True(oAlts[i] < oAlts[i - 1] || oAlts[i] == oAlts[i - 1],
                    "altitude not monotone descending at " + i);
                Assert.True(oAlts[i] < 60000.0 && oAlts[i] > 50000.0,
                    "interpolated altitude out of band at " + i + ": " + oAlts[i]);
            }
        }

        [Fact]
        public void Densify_SinglePoint_ReturnedVerbatim()
        {
            double[] lats = { 5.0 };
            double[] lons = { 10.0 };
            double[] alts = { 50000.0 };
            double[] uts = { 100.0 };

            GhostTrajectoryPolylineRenderer.DensifyBodyFixedArcs(
                lats, lons, alts, uts, 0.3, 512,
                out var oLats, out var oLons, out var oAlts, out var oUts);

            Assert.Single(oLats);
            Assert.Equal(5.0, oLats[0]);
            Assert.Equal(10.0, oLons[0]);
            Assert.Equal(50000.0, oAlts[0]);
            Assert.Equal(100.0, oUts[0]);
        }

        [Fact]
        public void GreatCircleArcDegrees_KnownArc()
        {
            // Equator: lon 0 to lon 90 is a 90-degree great-circle arc.
            double arc = GhostTrajectoryPolylineRenderer.GreatCircleArcDegrees(
                0.0, 0.0, 0.0, 90.0);
            Assert.True(System.Math.Abs(arc - 90.0) < 1e-6, "arc was " + arc);
            // Identical points: zero arc.
            double zero = GhostTrajectoryPolylineRenderer.GreatCircleArcDegrees(
                12.0, 34.0, 12.0, 34.0);
            Assert.True(zero < 1e-6, "arc was " + zero);
        }

        [Fact]
        public void SlerpLatLon_MidpointHalvesTheArc()
        {
            // t=0.5 on an equatorial 0..90 arc lands at lon 45 (lat 0).
            GhostTrajectoryPolylineRenderer.SlerpLatLon(
                0.0, 0.0, 0.0, 90.0, 0.5, out double lat, out double lon);
            Assert.True(System.Math.Abs(lat) < 1e-6, "lat was " + lat);
            Assert.True(System.Math.Abs(lon - 45.0) < 1e-6, "lon was " + lon);
        }

        [Fact]
        public void BuildLeg_SparseDunaDeorbitArc_DensifiesAndPreservesSpan()
        {
            // End-to-end through BuildLegFromBodyFixedPoints: the s15 leg shape
            // (sparse ~35 deg Duna arc) is subdivided, but the leg's recorded
            // span endpoints are preserved exactly.
            var pts = new List<TrajectoryPoint>
            {
                MakePoint(70963373.6, -2.8, 73.9, 58245.0, "Duna"),
                MakePoint(70963513.0, -2.7, 91.0, 54000.0, "Duna"),
                MakePoint(70963652.6, -2.6, 108.6, 49998.0, "Duna")
            };

            var leg = GhostTrajectoryPolylineRenderer.BuildLegFromBodyFixedPoints(
                pts, "Duna", out int preDensifyCount);

            Assert.Equal(3, preDensifyCount);
            Assert.True(leg.PointCount > 100,
                "expected densified leg, got " + leg.PointCount);
            Assert.True(leg.PointCount <=
                GhostTrajectoryPolylineRenderer.MaxDensifiedPointsPerLeg);
            // Recorded span endpoints preserved exactly.
            Assert.Equal(70963373.6, leg.startUT);
            Assert.Equal(70963652.6, leg.endUT);
            Assert.Equal(58245.0, leg.alts[0]);
            Assert.Equal(49998.0, leg.alts[leg.PointCount - 1]);
        }

        [Fact]
        public void BuildLegs_SparseLeg_LogsDensifySummary()
        {
            var rec = new Recording { RecordingId = "rec-densify-log" };
            // A sparse ~30 deg arc on the equator: must densify and log post>pre.
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 60000.0, "Duna"));
            rec.Points.Add(MakePoint(200.0, 0.0, 30.0, 50000.0, "Duna"));

            GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("Polyline densify:")
                && l.Contains("rec=rec-densify-log")
                && l.Contains("densifiedLegs=1"));
        }

        [Fact]
        public void BuildLegs_AbsoluteSectionAboveCap_CapsPointCount()
        {
            var frames = MakePointList(0, 1000);
            var rec = new Recording { RecordingId = "rec-cap" };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0, endUT = 999.0,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null
            });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(GhostTrajectoryPolylineRenderer.MaxPolylinePointsPerLeg,
                legs[0].PointCount);
            // Endpoints preserved at the cap.
            Assert.Equal(frames[0].ut, legs[0].startUT);
            Assert.Equal(frames[999].ut, legs[0].endUT);
        }

        // --- Body grouping (SOI fallback path) ---

        [Fact]
        public void GroupByBody_SOICrossing_EmitsSeparateLegs()
        {
            // Flat Recording.Points crossing Kerbin->Mun outside any section.
            var rec = new Recording { RecordingId = "rec-soi" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 100000.0, "Kerbin"));
            rec.Points.Add(MakePoint(150.0, 0.1, 0.0, 200000.0, "Kerbin"));
            rec.Points.Add(MakePoint(200.0, 0.2, 0.0, 5000.0, "Mun"));
            rec.Points.Add(MakePoint(250.0, 0.3, 0.0, 4000.0, "Mun"));

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Equal(2, legs.Count);
            Assert.Equal("Kerbin", legs[0].bodyName);
            Assert.Equal("Mun", legs[1].bodyName);
        }

        // --- RecordingBuilder path (per §5.3 plan) ---

        [Fact]
        public void BuildLegs_ViaRecordingBuilder_AbsoluteAscent_BuildsLegThroughPoints()
        {
            // The plan's §5.3 in-game test pattern. Verifies the builder
            // works end-to-end via the RecordingBuilder generator, which is
            // the same surface the in-game test uses.
            var builder = new RecordingBuilder("test-polyline-1")
                .WithRecordingId("test-polyline-1");
            // Add the TrackSection through the structured AddTrackSection
            // path so the section's list fields are populated; mirror the
            // recorder's body-fixed frames.
            var frames = new List<TrajectoryPoint>
            {
                MakePoint(100.0, -0.1, -74.5, 70.0),
                MakePoint(200.0, -0.05, -74.5, 20000.0),
                MakePoint(600.0, 0.0, -74.5, 100000.0)
            };
            builder.AddTrackSection(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                100.0, 600.0, frames: frames, sampleRateHz: 10f);

            var rec = new Recording { RecordingId = "test-polyline-1" };
            foreach (var ts in builder.GetTrackSections())
                rec.TrackSections.Add(ts);

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal(3, legs[0].PointCount);
            Assert.Equal("Kerbin", legs[0].bodyName);
        }

        // --- Contiguous-span merge (burn fragmented into many env-class sections) ---

        [Fact]
        public void BuildLegs_ContiguousAbsoluteSections_MergeIntoOneLeg()
        {
            // The recorder fragments a burn into several short same-body Absolute
            // sections (env-class flip-flop). They must MERGE into one leg so the
            // whole burn renders continuously, not one stub per fragment under the
            // head-UT draw gate.
            var builder = new RecordingBuilder("rec-burn").WithRecordingId("rec-burn");
            builder.AddTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, TrackSectionSource.Active,
                100.0, 110.0, frames: new List<TrajectoryPoint> { MakePoint(100.0, 0.0, 0.0, 100000.0), MakePoint(110.0, 0.1, 0.0, 101000.0) }, sampleRateHz: 10f);
            builder.AddTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, TrackSectionSource.Active,
                110.0, 120.0, frames: new List<TrajectoryPoint> { MakePoint(110.0, 0.1, 0.0, 101000.0), MakePoint(120.0, 0.2, 0.0, 102000.0) }, sampleRateHz: 10f);
            builder.AddTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, TrackSectionSource.Active,
                120.0, 130.0, frames: new List<TrajectoryPoint> { MakePoint(120.0, 0.2, 0.0, 102000.0), MakePoint(130.0, 0.3, 0.0, 103000.0) }, sampleRateHz: 10f);
            var rec = new Recording { RecordingId = "rec-burn" };
            foreach (var ts in builder.GetTrackSections()) rec.TrackSections.Add(ts);

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Single(legs);
            Assert.Equal("Kerbin", legs[0].bodyName);
        }

        [Fact]
        public void BuildLegs_NonOrbitalSpansSplitByOrbit_SeparateLegs()
        {
            // Two non-orbital spans separated by an orbital coast stay separate
            // legs (the orbit arc owns the gap; the polyline must not chord it).
            var builder = new RecordingBuilder("rec-split").WithRecordingId("rec-split");
            builder.AddTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, TrackSectionSource.Active,
                100.0, 118.0, frames: new List<TrajectoryPoint> { MakePoint(100.0, 0.0, 0.0, 100000.0), MakePoint(118.0, 0.1, 0.0, 101000.0) }, sampleRateHz: 10f);
            builder.AddTrackSection(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, TrackSectionSource.Active,
                302.0, 320.0, frames: new List<TrajectoryPoint> { MakePoint(302.0, 0.2, 0.0, 102000.0), MakePoint(320.0, 0.3, 0.0, 103000.0) }, sampleRateHz: 10f);
            var rec = new Recording { RecordingId = "rec-split" };
            foreach (var ts in builder.GetTrackSections()) rec.TrackSections.Add(ts);
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 120.0, endUT = 300.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 });

            var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Equal(2, legs.Count);
        }

        [Fact]
        public void OrbitalIntervalBetween_IntervalInGap_True()
        {
            var iv = new List<(double startUT, double endUT)> { (120.0, 300.0) };
            Assert.True(GhostTrajectoryPolylineRenderer.OrbitalIntervalBetween(110.0, 310.0, iv));
        }

        [Fact]
        public void OrbitalIntervalBetween_NoIntervalInGap_False()
        {
            var iv = new List<(double startUT, double endUT)> { (120.0, 300.0) };
            Assert.False(GhostTrajectoryPolylineRenderer.OrbitalIntervalBetween(305.0, 310.0, iv));
        }

        [Fact]
        public void OrbitalIntervalBetween_TouchingEndpoint_False()
        {
            // A burn sample exactly at the orbit-arc boundary is contiguous, not split.
            var iv = new List<(double startUT, double endUT)> { (120.0, 300.0) };
            Assert.False(GhostTrajectoryPolylineRenderer.OrbitalIntervalBetween(300.0, 320.0, iv));
        }

        // --- Static visibility filter (MAJOR fix: polyline is a static
        //     full-path bridge; it must NOT inherit the per-head-UT gates
        //     OrbitSegmentActive / NativeIconActive) ---

        [Fact]
        public void StaticSkip_NormalRecording_NotSkipped()
        {
            var rec = new Recording { RecordingId = "rec-static-ok" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));

            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, null);

            Assert.Equal(GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.None, reason);
        }

        [Fact]
        public void StaticSkip_WithOrbitSegments_StillNotSkipped()
        {
            // The polyline must remain visible even when the recording has
            // OrbitSegments (i.e. the playback head could be in an orbital
            // phase). The OLD code routed through ClassifyAtmosphericMarkerSkip
            // which returned OrbitSegmentActive and blinked the whole polyline
            // out. The static filter ignores orbital cover entirely.
            var rec = new Recording { RecordingId = "rec-has-orbit" };
            rec.Points.Add(MakePoint(0.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(1000.0, 0.0, 0.0, 100000.0));
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 0.0, endUT = 1000.0,
                bodyName = "Kerbin", semiMajorAxis = 700000.0
            });

            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, null);

            Assert.Equal(GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.None, reason);
        }

        [Fact]
        public void StaticSkip_DebrisRecording_Skipped()
        {
            var rec = new Recording { RecordingId = "rec-debris", IsDebris = true };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));

            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, null);

            Assert.Equal(GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.Debris, reason);
        }

        [Fact]
        public void StaticSkip_NoTrajectoryPoints_Skipped()
        {
            var rec = new Recording { RecordingId = "rec-empty" };

            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, null);

            Assert.Equal(
                GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.NoTrajectoryPoints,
                reason);
        }

        [Fact]
        public void StaticSkip_Suppressed_Skipped()
        {
            var rec = new Recording { RecordingId = "rec-suppressed" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(200.0, 0.0, 0.0, 100.0));
            var suppressed = new HashSet<string> { "rec-suppressed" };

            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, suppressed);

            Assert.Equal(
                GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.SuppressedByChainFilter,
                reason);
        }

        [Fact]
        public void StaticSkip_NullRecording_Skipped()
        {
            var reason = GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(null, null);
            Assert.Equal(
                GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.NullRecording,
                reason);
        }

        // --- Deactivation sweep predicate (stale-mesh hide) ---

        [Fact]
        public void ShouldDeactivateLeg_ActiveAndNotDrawnThisFrame_Deactivates()
        {
            // Drawn on frame 41, sweep running on frame 42: hide the stale mesh.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDeactivateLeg(
                currentlyActive: true, lastDrawnFrame: 41, drawFrame: 42));
        }

        [Fact]
        public void ShouldDeactivateLeg_ActiveAndDrawnThisFrame_StaysVisible()
        {
            // Drawn this very frame: keep it visible.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDeactivateLeg(
                currentlyActive: true, lastDrawnFrame: 42, drawFrame: 42));
        }

        [Fact]
        public void ShouldDeactivateLeg_AlreadyInactive_NoOp()
        {
            // Already hidden: nothing to do even though it was not drawn.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDeactivateLeg(
                currentlyActive: false, lastDrawnFrame: 10, drawFrame: 42));
        }

        [Fact]
        public void ShouldDeactivateLeg_NeverDrawn_ActiveLineHidden()
        {
            // lastDrawnFrame default 0 (never stamped) with an active line is the
            // recording-removed / first-hidden case: hide it.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDeactivateLeg(
                currentlyActive: true, lastDrawnFrame: 0, drawFrame: 42));
        }

        // --- Per-leg head-UT gate (line follows the ghost) ---

        [Fact]
        public void ShouldDrawLegAtHeadUT_HeadInsideSpan_Draws()
        {
            // Ghost is currently flying this leg's window: show it.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                legStartUT: 100.0, legEndUT: 200.0, headUT: 150.0));
        }

        [Fact]
        public void ShouldDrawLegAtHeadUT_HeadBeforeSpan_Hidden()
        {
            // Ghost has not reached this leg yet (e.g. a later phase): hide.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                legStartUT: 100.0, legEndUT: 200.0, headUT: 50.0));
        }

        [Fact]
        public void ShouldDrawLegAtHeadUT_HeadAfterSpan_Hidden()
        {
            // Ghost has moved past this leg (e.g. into an orbital phase): hide.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                legStartUT: 100.0, legEndUT: 200.0, headUT: 250.0));
        }

        [Fact]
        public void ShouldDrawLegAtHeadUT_HeadOnBoundaries_Draws()
        {
            // Inclusive at both ends so the leg does not flicker off for a frame
            // exactly at its first / last recorded sample.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                legStartUT: 100.0, legEndUT: 200.0, headUT: 100.0));
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                legStartUT: 100.0, legEndUT: 200.0, headUT: 200.0));
        }

        // --- FIX #27: below-SURFACE degenerate-segment cover exclusion ---
        //
        // Duna-like geometry: radius 320000, atmosphere top 50000 above radius.
        // The exclusion boundary is the SURFACE (CHANGE 2), not the atmosphere
        // top: only a DEGENERATE segment whose conic plunges below the ground is
        // claimed by the polyline. A valid orbit that merely grazes the
        // atmosphere at periapsis but stays above the surface is still drawn by
        // the orbit line, so it must NOT be excluded (the latent in-space
        // eccentric-orbit double-draw the review flagged).

        private const double DunaRadius = 320000.0;
        private const double DunaAtmoTop = 50000.0;

        // Synthetic surface provider keyed by body name (the pure-builder seam
        // the Driver fills from FlightGlobals at runtime). Only the radius is
        // needed under the surface boundary.
        private static GhostTrajectoryPolylineRenderer.BodySurfaceProvider DunaSurface()
        {
            return (string body,
                out GhostTrajectoryPolylineRenderer.BodySurfaceInfo info) =>
            {
                if (body == "Duna")
                {
                    info = new GhostTrajectoryPolylineRenderer.BodySurfaceInfo
                    {
                        radius = DunaRadius
                    };
                    return true;
                }
                info = default(GhostTrajectoryPolylineRenderer.BodySurfaceInfo);
                return false;
            };
        }

        // A clean Duna segment whose periapsis is ~58 km, above BOTH the surface
        // and the atmosphere top: orbit-owned, never excluded.
        private static OrbitSegment CleanDunaSegment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT,
                bodyName = "Duna",
                eccentricity = 0.004,
                // peri = sma*(1-ecc) - radius ; aim ~58 km
                semiMajorAxis = (DunaRadius + 58000.0) / (1.0 - 0.004)
            };
        }

        // A GRAZING Duna segment: periapsis ~12 km, BELOW the 50 km atmosphere
        // top but ABOVE the surface, with a high apoapsis. The orbit line draws
        // this correctly, so the polyline must NOT exclude it (this is the
        // CHANGE-2 case: the OLD below-atmosphere boundary would have wrongly
        // excluded it). Mirrors the recording's Kerbin seg#00/01 shape.
        private static OrbitSegment GrazingDunaSegment(double startUT, double endUT)
        {
            double peri = DunaRadius + 12000.0; // 12 km: below atmo top (50 km), above surface
            double ecc = 0.30;                  // high apoapsis
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT,
                bodyName = "Duna",
                eccentricity = ecc,
                semiMajorAxis = peri / (1.0 - ecc)
            };
        }

        // A degenerate Duna descent segment: periapsis well BELOW the surface
        // (matches the real recording's seg#20/21, periapsis ~-17 km).
        private static OrbitSegment DegenerateDunaSegment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT,
                bodyName = "Duna",
                eccentricity = 0.11,
                semiMajorAxis = 340660.0 // peri = 340660*0.89 - 320000 = ~-17 km
            };
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_DegenerateSegment_True()
        {
            var seg = DegenerateDunaSegment(100.0, 200.0);
            Assert.True(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_CleanSegment_False()
        {
            var seg = CleanDunaSegment(100.0, 200.0);
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_GrazingOrbit_NotExcluded()
        {
            // CHANGE 2: an in-space eccentric orbit whose periapsis dips below the
            // atmosphere top but stays ABOVE the surface (high apoapsis) is drawn
            // correctly by the orbit line and must NOT be claimed by the polyline
            // (the latent double-draw the review flagged). The OLD
            // below-atmosphere boundary would have excluded it.
            var seg = GrazingDunaSegment(100.0, 200.0);
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_NullProvider_False()
        {
            // No provider -> never excluded (byte-identical pre-fix behaviour).
            var seg = DegenerateDunaSegment(100.0, 200.0);
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, null));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_UnknownBody_False()
        {
            // Provider does not know the body -> not excluded.
            var seg = new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0,
                bodyName = "Eve",
                eccentricity = 0.11,
                semiMajorAxis = 340660.0
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_HyperbolicBelowSurface_True()
        {
            // Hyperbolic arrival (sma < 0, ecc > 1) with periapsis below the
            // surface: sma*(1-ecc) stays a positive periapsis radius. Aim peri
            // ~-10 km (below the surface).
            double peri = DunaRadius - 10000.0;
            double ecc = 1.4;
            double sma = peri / (1.0 - ecc); // negative
            var seg = new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0,
                bodyName = "Duna",
                eccentricity = ecc,
                semiMajorAxis = sma
            };
            Assert.True(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void IsOrbitSegmentBelowSurface_HyperbolicGrazingAtmosphere_NotExcluded()
        {
            // A hyperbolic arrival whose periapsis grazes the atmosphere but stays
            // above the surface (peri ~+5 km): the orbit line draws it, so it is
            // NOT excluded under the surface boundary.
            double peri = DunaRadius + 5000.0;
            double ecc = 1.4;
            double sma = peri / (1.0 - ecc);
            var seg = new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0,
                bodyName = "Duna",
                eccentricity = ecc,
                semiMajorAxis = sma
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface(
                seg, DunaSurface()));
        }

        [Fact]
        public void ComputeOrbitalCoverIntervals_ExcludesDegenerateKeepsGrazing()
        {
            var segments = new List<OrbitSegment>
            {
                CleanDunaSegment(100.0, 200.0),
                GrazingDunaSegment(200.0, 300.0),     // below atmo top, above surface: KEEP
                DegenerateDunaSegment(300.0, 400.0)   // below surface: EXCLUDE
            };
            var intervals = GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals(
                segments, DunaSurface());

            // The clean + grazing segments stay; only the degenerate one drops.
            Assert.Equal(2, intervals.Count);
            Assert.Equal(100.0, intervals[0].startUT);
            Assert.Equal(200.0, intervals[0].endUT);
            Assert.Equal(200.0, intervals[1].startUT);
            Assert.Equal(300.0, intervals[1].endUT);
        }

        [Fact]
        public void ComputeOrbitalCoverIntervals_NullProvider_KeepsAllSegments()
        {
            var segments = new List<OrbitSegment>
            {
                CleanDunaSegment(100.0, 200.0),
                DegenerateDunaSegment(200.0, 300.0)
            };
            var intervals = GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals(
                segments, null);
            Assert.Equal(2, intervals.Count);
        }

        [Fact]
        public void BuildLegs_RealDunaDescentSectionGeometry_MergesAcrossExcludedGaps()
        {
            // CHANGE 3: reproduces the ACTUAL final-descent geometry from the
            // recording 61e9177... (the region the user sees as the broken "last
            // segment"): ref=0 Absolute section FRAME clusters separated by
            // frameless ref=2 OrbitalCheckpoint gaps that the two degenerate
            // below-surface Duna segments (seg#20/21) span. The playback head
            // 70963566 lands INSIDE the seg#21 frameless gap.
            //
            // Pre-fix: the [70963441,70963444] cluster ends at 70963444 < head ->
            // activeLeg=-1 -> hole. After excluding seg#20/21 from the cover,
            // OrbitalIntervalBetween returns false across them, so all the Duna
            // ref=0 clusters MERGE into ONE continuous Duna leg spanning the tail
            // that ShouldDrawLegAtHeadUT selects for head 70963566.
            var rec = new Recording { RecordingId = "rec-duna-real-descent" };

            // The clean arrival orbit ending exactly where the first frame cluster
            // starts (seg#19: periapsis ~58 km, above surface, NOT excluded).
            rec.OrbitSegments.Add(CleanDunaSegment(70962515.846912, 70963373.370910));
            // The two degenerate descent segments spanning the frameless gaps.
            rec.OrbitSegments.Add(DegenerateDunaSegment(70963396.390905, 70963441.181716)); // seg#20
            rec.OrbitSegments.Add(DegenerateDunaSegment(70963444.461716, 70963652.559612)); // seg#21

            // ref=0 Absolute frame clusters (the real section boundaries).
            AddAbsoluteDunaSection(rec, 70963373.370910, 70963381.030908, 20);
            AddAbsoluteDunaSection(rec, 70963381.030908, 70963383.330908, 2);
            AddAbsoluteDunaSection(rec, 70963383.330908, 70963391.150906, 3);
            AddAbsoluteDunaSection(rec, 70963391.150906, 70963396.390905, 4);
            // frameless ref=2 gap [70963396.390905, 70963441.181716] spanned by seg#20
            AddOrbitalCheckpointSection(rec, 70963396.390905, 70963441.181716);
            AddAbsoluteDunaSection(rec, 70963441.181716, 70963444.461716, 12);
            // frameless ref=2 gap [70963444.461716, 70963652.559612] spanned by seg#21
            AddOrbitalCheckpointSection(rec, 70963444.461716, 70963652.559612);
            AddAbsoluteDunaSection(rec, 70963652.559612, 70963652.639612, 2);

            var withProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(
                rec, DunaSurface());
            var withoutProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            // WITHOUT the provider: the degenerate segments stay in the cover, so
            // OrbitalIntervalBetween breaks the clusters into fragmented legs and
            // the [70963441,70963444] cluster ends before the head -> the head
            // (70963566) lands in no leg (the hole the user saw).
            const double headUT = 70963566.0;
            bool anyLegCoversHeadWithout = withoutProvider.Exists(l =>
                GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(l.startUT, l.endUT, headUT));
            Assert.False(anyLegCoversHeadWithout);

            // WITH the provider: the degenerate segments are excluded, so the Duna
            // clusters MERGE into ONE continuous leg spanning ~[70963373,70963652].
            Assert.Single(withProvider);
            var merged = withProvider[0];
            Assert.Equal("Duna", merged.bodyName);
            // Start is the first kept frame after seg#19's inclusive endUT
            // (the exact-boundary frame at 70963373.371 is orbit-owned).
            Assert.True(merged.startUT >= 70963373.0 && merged.startUT < 70963382.0,
                "merged leg startUT was " + merged.startUT.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(merged.endUT >= 70963652.0 && merged.endUT <= 70963653.0,
                "merged leg endUT was " + merged.endUT.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

            // The head 70963566 (inside the seg#21 frameless gap) now lands inside
            // the merged leg.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                merged.startUT, merged.endUT, headUT));

            // Two below-surface segments excluded; one-shot summary logged.
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("excluded 2 below-surface orbit segments from cover")
                && l.Contains("rec=rec-duna-real-descent"));
        }

        [Fact]
        public void BuildLegs_NoDegenerateSegments_ProviderIsByteIdenticalNoOp()
        {
            // REGRESSION GUARD: a recording with no below-surface segments must
            // produce identical legs with and without the provider, so a normal
            // in-space orbit (incl. a grazing one) is untouched and its samples
            // stay orbit-owned.
            var rec = new Recording { RecordingId = "rec-clean" };
            // A clean parking orbit (well above surface) + a grazing orbit + an
            // ascent leg.
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = 0.0, endUT = 100.0,
                frames = new List<TrajectoryPoint>
                {
                    MakePoint(0.0, -0.1, -74.5, 70.0, "Duna"),
                    MakePoint(50.0, -0.05, -74.5, 20000.0, "Duna"),
                    MakePoint(100.0, 0.0, -74.5, 60000.0, "Duna")
                },
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 10f
            });
            rec.OrbitSegments.Add(CleanDunaSegment(100.0, 600.0));
            rec.OrbitSegments.Add(GrazingDunaSegment(600.0, 1200.0));

            var withProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(
                rec, DunaSurface());
            var withoutProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            Assert.Equal(withoutProvider.Count, withProvider.Count);
            for (int i = 0; i < withProvider.Count; i++)
            {
                Assert.Equal(withoutProvider[i].PointCount, withProvider[i].PointCount);
                Assert.Equal(withoutProvider[i].bodyName, withProvider[i].bodyName);
                Assert.Equal(withoutProvider[i].startUT, withProvider[i].startUT);
                Assert.Equal(withoutProvider[i].endUT, withProvider[i].endUT);
                Assert.Equal(withoutProvider[i].lats, withProvider[i].lats);
                Assert.Equal(withoutProvider[i].lons, withProvider[i].lons);
                Assert.Equal(withoutProvider[i].alts, withProvider[i].alts);
            }
            // No exclusion summary logged for a clean recording.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("excluded") && l.Contains("below-surface orbit segments"));
        }

        // Adds an Absolute (ref=0) Duna section with `count` frames evenly spaced
        // across [startUT, endUT], descending in altitude through the tail.
        private static void AddAbsoluteDunaSection(
            Recording rec, double startUT, double endUT, int count)
        {
            var frames = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double f = count == 1 ? 0.0 : (double)i / (count - 1);
                double ut = startUT + (endUT - startUT) * f;
                double alt = 58000.0 - 8000.0 * ((ut - 70963373.0) / (70963652.6 - 70963373.0));
                frames.Add(MakePoint(ut, -2.8 + 0.001 * i, -5.4 + 0.001 * i, alt, "Duna"));
            }
            rec.TrackSections.Add(new TrackSection
            {
                // Matches the real recording's env=2 (ExoBallistic) ref=0 frames.
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT, endUT = endUT,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 3f
            });
        }

        // Adds a frameless (ref=2) OrbitalCheckpoint section spanning a gap (no
        // per-frame trajectory points; the orbit-arc / excluded segment owns it).
        private static void AddOrbitalCheckpointSection(
            Recording rec, double startUT, double endUT)
        {
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Active,
                startUT = startUT, endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                bodyFixedFrames = null,
                sampleRateHz = 0f
            });
        }

        // --- Helpers ---

        private static TrajectoryPoint MakePoint(
            double ut, double lat, double lon, double alt, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
        }

        private static List<TrajectoryPoint> MakePointList(double startUT, int count)
        {
            var list = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
                list.Add(MakePoint(startUT + i, 0.0 + 0.001 * i, 0.0, 100.0 + 10.0 * i));
            return list;
        }

        // --- FindBracketingOrbitSegments (rotation-vs-not seam-gap diagnostic) ---

        [Fact]
        public void FindBracketingOrbitSegments_PicksLoiterBeforeAndEscapeAfter()
        {
            // loiter ends at the burn start (1000), escape starts at the burn end (1114).
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0.0,    endUT = 1000.0, bodyName = "Kerbin", eccentricity = 0.001 },
                new OrbitSegment { startUT = 1114.0, endUT = 5000.0, bodyName = "Kerbin", eccentricity = 1.19 },
            };

            GhostTrajectoryPolylineRenderer.FindBracketingOrbitSegments(
                segs, "Kerbin", 1000.0, 1114.0, out int before, out int after);

            Assert.Equal(0, before);
            Assert.Equal(1, after);
        }

        [Fact]
        public void FindBracketingOrbitSegments_IgnoresOtherBodyAndDegenerateSegments()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0.0,   endUT = 1000.0, bodyName = "Sun" },     // wrong body
                new OrbitSegment { startUT = 500.0, endUT = 500.0,  bodyName = "Kerbin" },  // degenerate
                new OrbitSegment { startUT = 200.0, endUT = 999.0,  bodyName = "Kerbin" },  // valid before
            };

            GhostTrajectoryPolylineRenderer.FindBracketingOrbitSegments(
                segs, "Kerbin", 1000.0, 1114.0, out int before, out int after);

            Assert.Equal(2, before);
            Assert.Equal(-1, after); // no Kerbin segment starts at/after the burn end
        }

        [Fact]
        public void FindBracketingOrbitSegments_NoneWhenNoAdjacency()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 2000.0, endUT = 3000.0, bodyName = "Kerbin" }, // entirely after
            };

            GhostTrajectoryPolylineRenderer.FindBracketingOrbitSegments(
                segs, "Kerbin", 1000.0, 1114.0, out int before, out int after);

            Assert.Equal(-1, before);
            Assert.Equal(0, after);
        }

        // --- IsSeamResidualTooLarge (conic-anchor Duna/Ike regression guard) ---

        [Fact]
        public void IsSeamResidualTooLarge_KerbinEscapeZeroResidual_Anchors()
        {
            // The Kerbin escape burn seam met the leg exactly (residual 0 km) -> must NOT be rejected.
            Assert.False(GhostTrajectoryPolylineRenderer.IsSeamResidualTooLarge(0f, 0f, 730f));
        }

        [Theory]
        [InlineData(46543f, 46392f, 37000f)] // Duna leg 11 (arrival hyperbola arm)
        [InlineData(599f, 590f, 810f)]       // Ike flyby leg 12
        [InlineData(430f, 3041f, 380f)]      // alt-60km leg (elliptical-bracketed yet far off)
        public void IsSeamResidualTooLarge_DunaIkeArrivalLegs_Rejected(
            float residStartKm, float residEndKm, float legRadiusKm)
        {
            Assert.True(GhostTrajectoryPolylineRenderer.IsSeamResidualTooLarge(
                residStartKm, residEndKm, legRadiusKm));
        }

        [Fact]
        public void IsSeamResidualTooLarge_SmallAbsoluteOnLargeRadius_RejectedByAbsoluteFloor()
        {
            // 60 km residual on a huge radius: relative is tiny but the absolute floor still rejects it.
            Assert.True(GhostTrajectoryPolylineRenderer.IsSeamResidualTooLarge(60f, 10f, 1_000_000f));
        }

        [Fact]
        public void IsSeamResidualTooLarge_TinyResidualWithinTolerance_Anchors()
        {
            // A few km on a 730 km orbit (under the 50 km floor and under 5 percent) -> anchor.
            Assert.False(GhostTrajectoryPolylineRenderer.IsSeamResidualTooLarge(3f, 4f, 730f));
        }
    }
}
