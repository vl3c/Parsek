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

        // --- FIX #27: below-atmosphere degenerate-segment cover exclusion ---
        //
        // Duna-like geometry: radius 320000, atmosphereDepth 50000 (atmosphere
        // top at radius 370000). A "clean" arrival segment sits at ~58 km
        // (periapsis above the atmosphere top); the degenerate descent segments
        // have periapsis BELOW the atmosphere top / surface, so the orbit line is
        // unreliable across them and the polyline must own their samples.

        private const double DunaRadius = 320000.0;
        private const double DunaAtmoTop = 50000.0;

        // Synthetic atmosphere provider keyed by body name (the pure-builder
        // seam the Driver fills from FlightGlobals at runtime).
        private static GhostTrajectoryPolylineRenderer.BodyAtmosphereProvider DunaAtmosphere()
        {
            return (string body,
                out GhostTrajectoryPolylineRenderer.BodyAtmosphereInfo info) =>
            {
                if (body == "Duna")
                {
                    info = new GhostTrajectoryPolylineRenderer.BodyAtmosphereInfo
                    {
                        atmosphereDepth = DunaAtmoTop,
                        radius = DunaRadius
                    };
                    return true;
                }
                info = default(GhostTrajectoryPolylineRenderer.BodyAtmosphereInfo);
                return false;
            };
        }

        // A clean above-atmosphere Duna segment: sma chosen so periapsis altitude
        // is ~58 km (above the 50 km atmosphere top).
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
        public void IsOrbitSegmentBelowAtmosphere_DegenerateSegment_True()
        {
            var seg = DegenerateDunaSegment(100.0, 200.0);
            Assert.True(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowAtmosphere(
                seg, DunaAtmosphere()));
        }

        [Fact]
        public void IsOrbitSegmentBelowAtmosphere_CleanSegment_False()
        {
            var seg = CleanDunaSegment(100.0, 200.0);
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowAtmosphere(
                seg, DunaAtmosphere()));
        }

        [Fact]
        public void IsOrbitSegmentBelowAtmosphere_NullProvider_False()
        {
            // No provider -> never excluded (byte-identical pre-fix behaviour).
            var seg = DegenerateDunaSegment(100.0, 200.0);
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowAtmosphere(
                seg, null));
        }

        [Fact]
        public void IsOrbitSegmentBelowAtmosphere_UnknownBody_False()
        {
            // Provider does not know the body -> not excluded.
            var seg = new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0,
                bodyName = "Eve",
                eccentricity = 0.11,
                semiMajorAxis = 340660.0
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowAtmosphere(
                seg, DunaAtmosphere()));
        }

        [Fact]
        public void IsOrbitSegmentBelowAtmosphere_HyperbolicBelowAtmosphere_True()
        {
            // Hyperbolic arrival (sma < 0, ecc > 1) with periapsis below the
            // atmosphere: sma*(1-ecc) stays a positive periapsis radius. Aim peri
            // ~10 km (below the 50 km atmosphere top).
            double peri = DunaRadius + 10000.0;
            double ecc = 1.4;
            double sma = peri / (1.0 - ecc); // negative
            var seg = new OrbitSegment
            {
                startUT = 100.0, endUT = 200.0,
                bodyName = "Duna",
                eccentricity = ecc,
                semiMajorAxis = sma
            };
            Assert.True(GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowAtmosphere(
                seg, DunaAtmosphere()));
        }

        [Fact]
        public void ComputeOrbitalCoverIntervals_ExcludesDegenerateSegment()
        {
            var segments = new List<OrbitSegment>
            {
                CleanDunaSegment(100.0, 200.0),
                DegenerateDunaSegment(200.0, 300.0)
            };
            var intervals = GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals(
                segments, DunaAtmosphere());

            // Only the clean segment remains in the cover.
            Assert.Single(intervals);
            Assert.Equal(100.0, intervals[0].startUT);
            Assert.Equal(200.0, intervals[0].endUT);
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
        public void BuildLegs_DunaDescentInsideDegenerateSegments_MergesIntoOneLegWithProvider()
        {
            // Descent points (body=Duna, ~58 km down to ~50 km) fall INSIDE the
            // degenerate descent segment's interval. WITHOUT the provider they
            // are dropped (orbit-owned) -> coverage hole. WITH the provider the
            // degenerate segment is excluded from the cover, so the descent
            // samples are picked up and merge into ONE continuous Duna leg.
            var rec = new Recording { RecordingId = "rec-duna-descent" };
            // Clean arrival orbit, then the degenerate descent segment.
            rec.OrbitSegments.Add(CleanDunaSegment(70000.0, 70100.0));
            rec.OrbitSegments.Add(DegenerateDunaSegment(70100.0, 70300.0));
            // Descent points all INSIDE the degenerate segment's [70100,70300].
            for (int i = 0; i < 20; i++)
            {
                double ut = 70100.0 + i * 10.0; // 70100..70290
                double alt = 58000.0 - i * 400.0; // 58000 down to ~50400
                rec.Points.Add(MakePoint(ut, 5.0 + 0.01 * i, -10.0, alt, "Duna"));
            }

            var withProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(
                rec, DunaAtmosphere());
            var withoutProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);

            // Without the provider: the descent points are inside the (kept)
            // degenerate interval -> dropped -> no descent leg (the hole).
            Assert.Empty(withoutProvider);

            // With the provider: the degenerate segment is excluded, so the
            // descent samples form one continuous Duna leg covering the tail.
            // The first descent point (ut=70100) sits exactly on the CLEAN
            // segment's endUT, so it stays orbit-owned (the inclusive interval
            // claims it); the leg therefore starts at the next point (70110).
            Assert.Single(withProvider);
            Assert.Equal("Duna", withProvider[0].bodyName);
            Assert.Equal(70110.0, withProvider[0].startUT);
            Assert.Equal(70290.0, withProvider[0].endUT);

            // The head-UT gate selects that leg for a descent head UT.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegAtHeadUT(
                withProvider[0].startUT, withProvider[0].endUT, 70200.0));

            // One-shot exclusion summary logged with the descent leg span.
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("excluded 1 below-atmosphere orbit segments from cover")
                && l.Contains("rec=rec-duna-descent"));
        }

        [Fact]
        public void BuildLegs_NoDegenerateSegments_ProviderIsByteIdenticalNoOp()
        {
            // REGRESSION GUARD: a recording with no below-atmosphere segments must
            // produce identical legs with and without the provider, so a normal
            // in-space orbit is untouched and its samples stay orbit-owned.
            var rec = new Recording { RecordingId = "rec-clean" };
            // A clean parking orbit (well above atmosphere) + an ascent leg.
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

            var withProvider = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(
                rec, DunaAtmosphere());
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
                l.Contains("excluded") && l.Contains("below-atmosphere orbit segments"));
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
    }
}
