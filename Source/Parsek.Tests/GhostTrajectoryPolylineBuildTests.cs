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
            ParsekSettings.CurrentOverrideForTesting = null;
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

        // --- Phase 8b.1: TracedPath treatment ownership routing (no double-draw) ---

        [Fact]
        public void ShouldDrawLegOwnedByTreatment_DirectorOwns_RoutesThroughTreatment()
        {
            // The Director owns this ghost's active leg as a fresh TracedPath this frame, so the Driver
            // routes the draw through the treatment (and stands down on its own direct TryDrawLeg).
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawLegOwnedByTreatment(
                directorOwnsTracedPath: true));
        }

        [Fact]
        public void ShouldDrawLegOwnedByTreatment_DirectorDoesNotOwn_DriverDrawsDirect()
        {
            // Gate off / no fresh TracedPath intent / no ghost pid: the Driver draws directly,
            // byte-identical to today.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawLegOwnedByTreatment(
                directorOwnsTracedPath: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TreatmentOwnership_DriverAndTreatmentAgreeOnTheSameBoolean(bool directorActive)
        {
            // No-double-draw guarantee: the Driver's "route through the treatment" decision and the
            // treatment's "I own this leg" decision are the SAME boolean, so for any frame exactly one of
            // {treatment-draw, Driver-direct-draw} runs - the leg can never be drawn twice.
            Assert.Equal(
                GhostTrajectoryPolylineRenderer.ShouldDrawLegOwnedByTreatment(directorActive),
                Parsek.MapRender.TracedPathTreatment.ShouldOwnLeg(directorActive));
        }

        // --- Phase 8b.2 / 8e S3b / 8e S4: ownership-signal authority (sole actual-draw set) ---

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResolveOwnership_ReturnsDrewMembership(bool inDrewSet)
        {
            // 8e S3b: the legacy set is gone - the drew set is the single ownership source (it was a proven
            // superset of the old legacy set, so collapsing to it preserves the exact pre-S3b membership).
            // 8e S4: the director-drive gate param is gone too, so the predicate is now an identity over the
            // drew membership (any leg that actually drew - owned-treatment OR Driver-direct, the 8e S3a.1
            // decouple).
            Assert.Equal(inDrewSet,
                GhostTrajectoryPolylineRenderer.ResolveNonOrbitalLegOwnership(inDrewSet));
        }

        [Fact]
        public void ResolveOwnership_NoDraw_NoOwnership_NoNewGap()
        {
            // THE no-new-gap invariant: when NO leg actually drew, the signal is FALSE, so the proto orbit
            // line / icon is NOT hidden. The drew set is populated only on an actual draw, so "Director
            // decided TracedPath but nothing drew" can never report ownership. proto hidden IFF a leg drew.
            Assert.False(GhostTrajectoryPolylineRenderer.ResolveNonOrbitalLegOwnership(inDrewSet: false));
        }

        [Fact]
        public void IsRenderingNonOrbitalLeg_EndToEnd_Dispatches()
        {
            // End-to-end (the dispatch IsPolylineOwningGhostPhase ultimately calls): post-S3b the drew set
            // is the SOLE source, and 8e S4 made it unconditional, so a drew-set recording is owned and a
            // non-drew recording is not.
            const string recDrew = "rec-drew-set";
            const string recNotDrawn = "rec-not-drawn";
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(recDrew, inDrewSet: true);

            Assert.True(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(recDrew));
            Assert.False(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(recNotDrawn));

            // Null recordingId is never owned.
            Assert.False(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(null));
        }

        [Fact]
        public void DrewSetPublish_AnyDrawNonTracedPath_PublishesToDrewSet()
        {
            // 8e S3a.1: the drew-set publish is DECOUPLED from the Director's TracedPath classification -
            // an ANY-draw leg that the Director classified StockConic (the re-aim "bridge" leg) publishes
            // to the drew set just like an owned-treatment leg, modeled here via SetOwnershipPublishForTesting
            // with inDrewSet:true (the real Driver populates the drew set on the `if (anyDrawn)` condition,
            // on EITHER path). The recording is reported owned - so a StockConic bridge leg is accounted by
            // the drew set, which is exactly the coverage S3a.1 closed and S3b now relies on as the sole
            // source.
            const string recBridge = "rec-stockconic-bridge";
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(recBridge, inDrewSet: true);

            Assert.True(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(recBridge));
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

        // --- DecidePolylineWalkInclusion: the renderHidden gate decision table
        //     (launch->escape seam render fix). This is the pure seam the Driver's per-recording
        //     inclusion gate routes through; the playtest bug was the OLD gate skipping the whole
        //     launch recording on a hidden PRIMARY even though a boundary-overlap SECONDARY was live.

        [Fact]
        public void WalkInclusion_PrimaryRendersNoSecondary_WalkPrimaryOnly()
        {
            // The common case (every non-launch-hold member / aligned loop): primary in-window, no
            // secondary -> walk, draw the primary legs only. Byte-identical to the pre-fix gate.
            GhostTrajectoryPolylineRenderer.DecidePolylineWalkInclusion(
                primaryRenders: true, hasSecondary: false,
                out bool skip, out bool drawPrimary, out bool drawSecondary);

            Assert.False(skip);
            Assert.True(drawPrimary);
            Assert.False(drawSecondary);
        }

        [Fact]
        public void WalkInclusion_PrimaryHiddenNoSecondary_Skip()
        {
            // The old renderHidden skip, unchanged: primary hidden (downstream orbital phase, no
            // in-window leg) and no live secondary -> skip the whole recording.
            GhostTrajectoryPolylineRenderer.DecidePolylineWalkInclusion(
                primaryRenders: false, hasSecondary: false,
                out bool skip, out bool drawPrimary, out bool drawSecondary);

            Assert.True(skip);
            Assert.False(drawPrimary);
            Assert.False(drawSecondary);
        }

        [Fact]
        public void WalkInclusion_PrimaryHiddenSecondaryLive_WalkSecondaryOnly()
        {
            // THE FIX: the launch recording's primary head is hidden (instance N is at the destination,
            // an orbital phase outside the launch member's window) but the boundary-overlap secondary
            // (instance N+1's in-SOI ascent) is live in this member's own window. The OLD gate skipped
            // here, dropping the recording before the second-head + secondary-forward passes could run,
            // so the launch ascent polyline + escape conic ahead of the icon never drew. The new gate
            // WALKS the recording with the primary legs gated off and the secondary drawing.
            GhostTrajectoryPolylineRenderer.DecidePolylineWalkInclusion(
                primaryRenders: false, hasSecondary: true,
                out bool skip, out bool drawPrimary, out bool drawSecondary);

            Assert.False(skip);
            Assert.False(drawPrimary); // primary genuinely has nothing in-window this frame
            Assert.True(drawSecondary); // the early-launch ascent renders
        }

        [Fact]
        public void WalkInclusion_PrimaryRendersSecondaryLive_WalkBoth()
        {
            // REACHED in the single-recording validated case (review H1): when the launch member's window
            // is the whole recording, during the borrow window the primary (instance N near the
            // destination) is still in-window (primaryRenders=true) AND the secondary (N+1 ascent) is live,
            // so BOTH passes run for the same recording in one frame. That is exactly why the forward-arc
            // cache had to be namespaced per (recording, primary/secondary) - see ForwardArcDictKey_*.
            GhostTrajectoryPolylineRenderer.DecidePolylineWalkInclusion(
                primaryRenders: true, hasSecondary: true,
                out bool skip, out bool drawPrimary, out bool drawSecondary);

            Assert.False(skip);
            Assert.True(drawPrimary);
            Assert.True(drawSecondary);
        }

        // === Review H1: forward-arc cache namespace (primary vs boundary-overlap secondary) ===========
        // During the borrow window the Driver runs the forward pass twice for the SAME recording in one
        // frame (primary head + early-launch secondary head). The two heads select disjoint arc sets, so a
        // single recording-id cache key made the second pass overwrite the first (the primary's forward
        // conic then vanished). ForwardArcDictKey namespaces the secondary so both coexist.

        [Fact]
        public void ForwardArcDictKey_Primary_IsRecordingIdVerbatim()
        {
            // The primary pass keeps the recording id verbatim, so every non-launch-hold member / aligned
            // loop is byte-identical to the pre-fix cache key.
            Assert.Equal("rec-abc", GhostTrajectoryPolylineRenderer.ForwardArcDictKey("rec-abc", false));
        }

        [Fact]
        public void ForwardArcDictKey_Secondary_IsDistinctFromPrimary()
        {
            string primary = GhostTrajectoryPolylineRenderer.ForwardArcDictKey("rec-abc", false);
            string secondary = GhostTrajectoryPolylineRenderer.ForwardArcDictKey("rec-abc", true);
            Assert.NotEqual(primary, secondary);
            // The secondary key must START with the recording id (so it is recognizably the same recording)
            // but differ, so the two ForwardArcSet entries coexist in the cache within one frame.
            Assert.StartsWith("rec-abc", secondary);
        }

        [Fact]
        public void ForwardArcDictKey_DistinctRecordings_SecondaryKeysNeverCollide()
        {
            // The secondary suffix must not let one recording's secondary key equal another recording's
            // primary key (the control-char suffix guarantees this even for prefix-related ids).
            string secAbc = GhostTrajectoryPolylineRenderer.ForwardArcDictKey("abc", true);
            Assert.NotEqual("abc", secAbc);
            Assert.NotEqual(
                GhostTrajectoryPolylineRenderer.ForwardArcDictKey("abc", true),
                GhostTrajectoryPolylineRenderer.ForwardArcDictKey("abcd", false));
        }

        [Fact]
        public void ForwardArcDictKey_NullOrEmpty_PassesThrough()
        {
            // A null/empty recording id is never namespaced (the create early-returns on it anyway).
            Assert.Null(GhostTrajectoryPolylineRenderer.ForwardArcDictKey(null, true));
            Assert.Equal("", GhostTrajectoryPolylineRenderer.ForwardArcDictKey("", true));
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

        // --- BodyFixedLongitudeAtUT (one-sided-bracket diagnostic rotation basis) ---

        [Fact]
        public void BodyFixedLongitudeAtUT_LiveEqualsStartUT_ReturnsRecordedLonUnchanged()
        {
            // At the aligned instant (liveUT == legStartUT) there is no spin to undo, so the
            // body-fixed point is evaluated at its raw recorded longitude.
            Assert.Equal(
                50.0,
                GhostTrajectoryPolylineRenderer.BodyFixedLongitudeAtUT(50.0, 1000.0, 1000.0, 360.0),
                10);
        }

        [Fact]
        public void BodyFixedLongitudeAtUT_LiveAheadOfStartUT_CounterRotatesWest()
        {
            // period 360 s -> 1 deg/s. Live is 10 s past legStartUT, so the planet has spun +10 deg
            // east; the corrected longitude rolls back 10 deg west to undo that drift.
            Assert.Equal(
                40.0,
                GhostTrajectoryPolylineRenderer.BodyFixedLongitudeAtUT(50.0, 1000.0, 1010.0, 360.0),
                10);
        }

        [Fact]
        public void BodyFixedLongitudeAtUT_ShiftScalesWithElapsedSpin()
        {
            // A half-period of elapsed live time counter-rotates by exactly 180 deg.
            Assert.Equal(
                -130.0, // 50 - 180
                GhostTrajectoryPolylineRenderer.BodyFixedLongitudeAtUT(50.0, 1000.0, 1180.0, 360.0),
                10);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void BodyFixedLongitudeAtUT_NonRotatingOrInvalidPeriod_ReturnsRecordedLon(double period)
        {
            // A non-rotating body (or an unavailable rotation period) has no spin drift to undo, so
            // the recorded longitude is returned verbatim regardless of the legStartUT/liveUT gap.
            Assert.Equal(
                50.0,
                GhostTrajectoryPolylineRenderer.BodyFixedLongitudeAtUT(50.0, 1000.0, 9999.0, period),
                10);
        }

        // --- Pan-stability: WillLegDraw (FIX 1 decide-pass will-draw predicate) ---

        [Fact]
        public void WillLegDraw_ResolvedBodyTwoPoints_WillDraw()
        {
            // Mirrors TryDrawLeg's only non-degenerate early returns: body resolved + m>=2.
            Assert.True(GhostTrajectoryPolylineRenderer.WillLegDraw(2, true));
            Assert.True(GhostTrajectoryPolylineRenderer.WillLegDraw(200, true));
        }

        [Fact]
        public void WillLegDraw_FewerThanTwoPoints_WontDraw()
        {
            // Matches TryDrawLeg's `if (m < 2) return false`.
            Assert.False(GhostTrajectoryPolylineRenderer.WillLegDraw(1, true));
            Assert.False(GhostTrajectoryPolylineRenderer.WillLegDraw(0, true));
        }

        [Fact]
        public void WillLegDraw_BodyNotResolved_WontDraw()
        {
            // Matches the decide-pass body-null skip (the call site continues before reaching the draw).
            Assert.False(GhostTrajectoryPolylineRenderer.WillLegDraw(2, false));
            Assert.False(GhostTrajectoryPolylineRenderer.WillLegDraw(200, false));
        }

        // --- Pan-stability: IsHeadInInterLegGap (FIX 2 gap-vs-orbital-exit classifier) ---

        [Fact]
        public void IsHeadInInterLegGap_HeadInsideOverallSpan_True()
        {
            // Head between the first leg's start and the last leg's end (e.g. a connector/deorbit gap).
            Assert.True(GhostTrajectoryPolylineRenderer.IsHeadInInterLegGap(100.0, 500.0, 300.0));
        }

        [Fact]
        public void IsHeadInInterLegGap_HeadOnSpanBoundaries_True()
        {
            Assert.True(GhostTrajectoryPolylineRenderer.IsHeadInInterLegGap(100.0, 500.0, 100.0));
            Assert.True(GhostTrajectoryPolylineRenderer.IsHeadInInterLegGap(100.0, 500.0, 500.0));
        }

        [Fact]
        public void IsHeadInInterLegGap_HeadPastLastLeg_False()
        {
            // Genuine orbital-phase exit past the recorded legs: must fall through to the deep fallback.
            Assert.False(GhostTrajectoryPolylineRenderer.IsHeadInInterLegGap(100.0, 500.0, 600.0));
        }

        [Fact]
        public void IsHeadInInterLegGap_HeadBeforeFirstLeg_False()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.IsHeadInInterLegGap(100.0, 500.0, 50.0));
        }

        // --- Pan-stability: TryHoldLastGood (FIX 2 held-position freshness bounds) ---

        [Fact]
        public void TryHoldLastGood_FreshNearUT_HoldsCachedPosition()
        {
            var pos = new Vector3(1f, 2f, 3f);
            GhostTrajectoryPolylineRenderer.SetLastGoodOnLineForTesting(
                "rec-hold", pos, headUT: 1000.0, frame: 100, legIndex: 2);

            // 1 frame later, head advanced 0.5s: within both bounds -> held.
            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-hold", headUT: 1000.5, frame: 101, out Vector3 outPos, out int outLeg);

            Assert.True(held);
            Assert.Equal(pos, outPos);
            Assert.Equal(2, outLeg);
        }

        [Fact]
        public void TryHoldLastGood_NoCacheEntry_DeepFallback()
        {
            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-absent", headUT: 1000.0, frame: 100, out _, out _);
            Assert.False(held);
        }

        [Fact]
        public void TryHoldLastGood_StaleByFrame_DeepFallback()
        {
            GhostTrajectoryPolylineRenderer.SetLastGoodOnLineForTesting(
                "rec-stale-frame", new Vector3(1f, 1f, 1f), headUT: 1000.0, frame: 100, legIndex: 0);

            // Just past the frame-age bound (MaxFrameAge + 1).
            int staleFrame = 100 + GhostTrajectoryPolylineRenderer.LastGoodMaxFrameAge + 1;
            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-stale-frame", headUT: 1000.0, frame: staleFrame, out _, out _);
            Assert.False(held);

            // Exactly at the bound is still held.
            int edgeFrame = 100 + GhostTrajectoryPolylineRenderer.LastGoodMaxFrameAge;
            Assert.True(GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-stale-frame", headUT: 1000.0, frame: edgeFrame, out _, out _));
        }

        [Fact]
        public void TryHoldLastGood_StaleByHeadUT_DeepFallback()
        {
            GhostTrajectoryPolylineRenderer.SetLastGoodOnLineForTesting(
                "rec-stale-ut", new Vector3(1f, 1f, 1f), headUT: 1000.0, frame: 100, legIndex: 0);

            // Head advanced just past the UT-delta bound.
            double staleUT = 1000.0 + GhostTrajectoryPolylineRenderer.LastGoodMaxHeadUtDeltaSeconds + 0.1;
            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-stale-ut", headUT: staleUT, frame: 101, out _, out _);
            Assert.False(held);

            // Exactly at the bound is still held (and works backwards in UT too).
            double edgeUT = 1000.0 + GhostTrajectoryPolylineRenderer.LastGoodMaxHeadUtDeltaSeconds;
            Assert.True(GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-stale-ut", headUT: edgeUT, frame: 101, out _, out _));
            Assert.True(GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-stale-ut", headUT: 1000.0 - GhostTrajectoryPolylineRenderer.LastGoodMaxHeadUtDeltaSeconds,
                frame: 101, out _, out _));
        }

        [Fact]
        public void TryHoldLastGood_ClearedByClear_DeepFallback()
        {
            GhostTrajectoryPolylineRenderer.SetLastGoodOnLineForTesting(
                "rec-cleared", new Vector3(1f, 1f, 1f), headUT: 1000.0, frame: 100, legIndex: 0);
            GhostTrajectoryPolylineRenderer.Clear();

            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-cleared", headUT: 1000.0, frame: 100, out _, out _);
            Assert.False(held);
        }

        [Fact]
        public void TryHoldLastGood_ClearedByReleaseForRecording_DeepFallback()
        {
            GhostTrajectoryPolylineRenderer.SetLastGoodOnLineForTesting(
                "rec-released", new Vector3(1f, 1f, 1f), headUT: 1000.0, frame: 100, legIndex: 0);
            GhostTrajectoryPolylineRenderer.ReleaseForRecording("rec-released");

            bool held = GhostTrajectoryPolylineRenderer.TryHoldLastGoodForTesting(
                "rec-released", headUT: 1000.0, frame: 100, out _, out _);
            Assert.False(held);
        }

        // ====================================================================
        // Forward additive pass (Step 3, forward-trajectory-render plan)
        // ShouldDrawForwardLeg / BuildForwardArcKey / SelectForwardArcSegmentIndices
        // ====================================================================

        // A future leg overlapping the forward window (and NOT under the head) draws forward.
        [Fact]
        public void ShouldDrawForwardLeg_FutureLegOverlappingWindow_Draws()
        {
            // head at 50 (on the current element), window (40, 300]; a future leg [200,260] overlaps.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                legStartUT: 200.0, legEndUT: 260.0,
                forwardWindowStartUT: 40.0, forwardStopUT: 300.0, headUT: 50.0));
        }

        // The CURRENT leg (head inside its span) is drawn by the head-gated pass, never the forward pass.
        [Fact]
        public void ShouldDrawForwardLeg_CurrentLeg_NotForward()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                legStartUT: 40.0, legEndUT: 100.0,
                forwardWindowStartUT: 40.0, forwardStopUT: 300.0, headUT: 50.0));
        }

        // A leg entirely AFTER the forward stop is excluded (next-SOI / past full-loop element).
        [Fact]
        public void ShouldDrawForwardLeg_LegPastStop_NotDrawn()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                legStartUT: 320.0, legEndUT: 380.0,
                forwardWindowStartUT: 40.0, forwardStopUT: 300.0, headUT: 50.0));
        }

        // A leg entirely BEFORE the window (a completed past leg) is excluded.
        [Fact]
        public void ShouldDrawForwardLeg_PastLeg_NotDrawn()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                legStartUT: 0.0, legEndUT: 30.0,
                forwardWindowStartUT: 40.0, forwardStopUT: 300.0, headUT: 50.0));
        }

        // An empty forward range (stop <= windowStart, e.g. icon on a full-loop closed orbit) draws nothing.
        [Fact]
        public void ShouldDrawForwardLeg_EmptyRange_NeverDraws()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                legStartUT: 200.0, legEndUT: 260.0,
                forwardWindowStartUT: 40.0, forwardStopUT: 40.0, headUT: 50.0));
        }

        // The cache key changes on the SELECTED segment set or a re-aim window rollover (revised run rule:
        // keyed on the actual selected set, not currentElementIndex, since past arcs are now included).
        [Fact]
        public void BuildForwardArcKey_ChangesOnSelectedSetOrWindow()
        {
            var baseSet = new List<int> { 0, 2, 3 };
            string baseKey = GhostTrajectoryPolylineRenderer.BuildForwardArcKey(baseSet, 7);
            // Different selected set -> different key.
            Assert.NotEqual(baseKey, GhostTrajectoryPolylineRenderer.BuildForwardArcKey(new List<int> { 0, 2 }, 7));
            Assert.NotEqual(baseKey, GhostTrajectoryPolylineRenderer.BuildForwardArcKey(new List<int> { 0, 2, 4 }, 7));
            // Different re-aim window -> different key.
            Assert.NotEqual(baseKey, GhostTrajectoryPolylineRenderer.BuildForwardArcKey(baseSet, 8));
            // Same set + window -> stable key (cache hit; the selector emits ascending so order is stable).
            Assert.Equal(baseKey, GhostTrajectoryPolylineRenderer.BuildForwardArcKey(new List<int> { 0, 2, 3 }, 7));
            // Empty / null set -> stable sentinel key, distinct from any non-empty selection.
            string empty = GhostTrajectoryPolylineRenderer.BuildForwardArcKey(new List<int>(), 7);
            Assert.Equal(empty, GhostTrajectoryPolylineRenderer.BuildForwardArcKey(null, 7));
            Assert.NotEqual(baseKey, empty);
        }

        // The forward-arc selector excludes the CURRENT arc (head bracketing) so the stock current arc is
        // never double-drawn, and includes a later same-body above-surface transfer arc.
        [Fact]
        public void SelectForwardArcSegmentIndices_ExcludesCurrentArc_IncludesFutureArc()
        {
            var segs = new List<OrbitSegment>
            {
                CleanDunaSegment(0.0, 100.0),    // current: head brackets this
                CleanDunaSegment(100.0, 200.0),  // future arc (above surface)
            };
            // head=50 on seg 0; window (0, 200].
            var picked = GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                segs, forwardWindowStartUT: 0.0, forwardStopUT: 200.0, headUT: 50.0, surface: DunaSurface());
            Assert.Equal(new[] { 1 }, picked.ToArray());
        }

        // A below-surface descent segment is NOT selected as a forward ARC (it draws as a forward LEG B').
        [Fact]
        public void SelectForwardArcSegmentIndices_BelowSurfaceSegment_ExcludedFromArcs()
        {
            var segs = new List<OrbitSegment>
            {
                CleanDunaSegment(0.0, 100.0),       // current
                DegenerateDunaSegment(100.0, 200.0) // below-surface descent -> leg, not arc
            };
            var picked = GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                segs, forwardWindowStartUT: 0.0, forwardStopUT: 200.0, headUT: 50.0, surface: DunaSurface());
            Assert.Empty(picked);
        }

        // An empty forward range selects no arcs.
        [Fact]
        public void SelectForwardArcSegmentIndices_EmptyRange_SelectsNothing()
        {
            var segs = new List<OrbitSegment>
            {
                CleanDunaSegment(0.0, 100.0),
                CleanDunaSegment(100.0, 200.0),
            };
            var picked = GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                segs, forwardWindowStartUT: 0.0, forwardStopUT: 0.0, headUT: 50.0, surface: DunaSurface());
            Assert.Empty(picked);
        }

        // The hot-path buffer overload (forward-render review finding) clears-and-fills a reused scratch
        // list with the SAME selection as the allocating overload, so the per-frame List<int> alloc is gone
        // without changing which segments are picked. Reusing the buffer across calls never carries stale
        // indices (Clear() runs first), even when a later call selects fewer or zero arcs.
        [Fact]
        public void SelectForwardArcSegmentIndices_BufferOverload_ClearsAndFills_SameAsAllocating()
        {
            var segs = new List<OrbitSegment>
            {
                CleanDunaSegment(0.0, 100.0),    // current: head brackets this
                CleanDunaSegment(100.0, 200.0),  // future arc (above surface)
            };
            var scratch = new List<int> { 99, 98 }; // pre-seeded stale entries that MUST be cleared
            GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                segs, forwardWindowStartUT: 0.0, forwardStopUT: 200.0, headUT: 50.0,
                surface: DunaSurface(), indices: scratch);
            Assert.Equal(new[] { 1 }, scratch.ToArray()); // stale 99/98 gone, only the future arc kept

            // Re-fill the SAME buffer with an empty-range call: it must come back empty (no leftover).
            GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                segs, forwardWindowStartUT: 0.0, forwardStopUT: 0.0, headUT: 50.0,
                surface: DunaSurface(), indices: scratch);
            Assert.Empty(scratch);

            // A null buffer is tolerated (no throw); a null segment list clears the buffer.
            GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                null, 0.0, 200.0, 50.0, DunaSurface(), null);
            var scratch2 = new List<int> { 7 };
            GhostTrajectoryPolylineRenderer.SelectForwardArcSegmentIndices(
                null, 0.0, 200.0, 50.0, DunaSurface(), scratch2);
            Assert.Empty(scratch2);
        }

        // ====================================================================
        // Chain-aware run membership (playtest-4 chain-boundary fix)
        // CollectChainRunMembers
        // ====================================================================

        private static Recording MakeChainMember(string id, string chainId, double startUT)
        {
            var rec = new Recording { RecordingId = id, ChainId = chainId };
            rec.Points.Add(MakePoint(startUT, 0.0, 0.0, 70.0));
            rec.Points.Add(MakePoint(startUT + 100.0, 0.0, 0.0, 100.0));
            return rec;
        }

        // A standalone (non-chain) recording collects as a single member carrying its committed index:
        // byte-identical to the pre-chain forward pass.
        [Fact]
        public void CollectChainRunMembers_StandaloneRecording_SingleMember()
        {
            var rec = MakeChainMember("rec-solo", chainId: null, startUT: 100.0);
            var committed = new List<Recording> { MakeChainMember("rec-other", null, 0.0), rec };
            var members = new List<(int index, Recording rec)>();

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, rec, 1, members);

            Assert.Single(members);
            Assert.Equal(1, members[0].index);
            Assert.Same(rec, members[0].rec);
        }

        // Chain members are collected from the committed list regardless of committed ORDER, returned
        // sorted by StartUT (the shared recorded-UT axis the run window is computed on), each carrying
        // its own committed index (the resolver key for ResolveEffectiveMapOrbitSegments).
        [Fact]
        public void CollectChainRunMembers_ChainMembers_SortedByStartUT_OthersExcluded()
        {
            const string chain = "chain-a";
            var launch = MakeChainMember("rec-launch", chain, startUT: 100.0);
            var middle = MakeChainMember("rec-middle", chain, startUT: 300.0);
            var tail = MakeChainMember("rec-tail", chain, startUT: 600.0);
            var otherChain = MakeChainMember("rec-other-chain", "chain-b", startUT: 200.0);
            var standalone = MakeChainMember("rec-standalone", null, startUT: 400.0);
            // Committed order deliberately scrambled relative to time order.
            var committed = new List<Recording> { tail, standalone, launch, otherChain, middle };
            var members = new List<(int index, Recording rec)>();

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, middle, 4, members);

            Assert.Equal(3, members.Count);
            Assert.Same(launch, members[0].rec);
            Assert.Same(middle, members[1].rec);
            Assert.Same(tail, members[2].rec);
            // Committed indices preserved per member.
            Assert.Equal(2, members[0].index);
            Assert.Equal(4, members[1].index);
            Assert.Equal(0, members[2].index);
        }

        // A descent-trigger member is excluded from the chain run: it renders ONLY via its own trigger-gated
        // primary pass, so the run must not draw its body-fixed descent leg on the loop clock (the descent-line-
        // on-the-loiter desync). The non-descent siblings still run.
        [Fact]
        public void CollectChainRunMembers_DescentMember_ExcludedFromRun()
        {
            const string chain = "chain-d";
            var launch = MakeChainMember("rec-launch", chain, startUT: 100.0);   // committed index 2
            var middle = MakeChainMember("rec-middle", chain, startUT: 300.0);   // committed index 4
            var descent = MakeChainMember("rec-descent", chain, startUT: 600.0); // committed index 0 (descent member)
            var committed = new List<Recording> { descent, MakeChainMember("x", null, 0.0), launch, MakeChainMember("y", "z", 0.0), middle };
            var members = new List<(int index, Recording rec)>();

            // A re-aim unit whose descent set = committed index 0 (the descent recording).
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            var unit = new Parsek.GhostPlaybackLogic.LoopUnit(
                2, new[] { 0, 2, 4 }, 0.0, 1000.0, 2000.0, 0.0, 2000.0, null, null, plan, sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false, recordedSoiExitUT: double.NaN,
                descentMemberIndices: new[] { 0 }, recordedDeorbitUT: 500.0, descentEndUT: 600.0,
                destinationBodyRotationPeriodSeconds: 65517.86, loiterPeriodSeconds: 4000.0, captureShiftSeconds: -100000.0);
            var units = new Parsek.GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, Parsek.GhostPlaybackLogic.LoopUnit> { { 2, unit } },
                new Dictionary<int, int> { { 0, 2 }, { 2, 2 }, { 4, 2 } });

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, middle, 4, members, units);

            // The descent member (index 0) is gone; launch + middle remain (sorted by StartUT).
            Assert.Equal(2, members.Count);
            Assert.Same(launch, members[0].rec);
            Assert.Same(middle, members[1].rec);
            Assert.DoesNotContain(members, m => ReferenceEquals(m.rec, descent));

            // Without the loop-unit set (null), the descent member is NOT excluded (byte-identical to before).
            var membersUngated = new List<(int index, Recording rec)>();
            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, middle, 4, membersUngated);
            Assert.Equal(3, membersUngated.Count);
        }

        // Builds a re-aim LoopUnit + set whose descent member set is the given committed indices, owner=2.
        private static Parsek.GhostPlaybackLogic.LoopUnitSet MakeDescentUnitSet(
            int[] descentIndices, int[] memberIndices, int transferMemberIndex = -1)
        {
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            var unit = new Parsek.GhostPlaybackLogic.LoopUnit(
                2, memberIndices, 0.0, 1000.0, 2000.0, 0.0, 2000.0, null, null, plan, sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false, recordedSoiExitUT: double.NaN,
                descentMemberIndices: descentIndices, recordedDeorbitUT: 500.0, descentEndUT: 600.0,
                destinationBodyRotationPeriodSeconds: 65517.86, loiterPeriodSeconds: 4000.0, captureShiftSeconds: -100000.0,
                transferMemberIndex: transferMemberIndex);
            var ownerByIndex = new Dictionary<int, int>();
            foreach (int mi in memberIndices) ownerByIndex[mi] = 2;
            return new Parsek.GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, Parsek.GhostPlaybackLogic.LoopUnit> { { 2, unit } }, ownerByIndex);
        }

        // DEFECT 2 regression: a chain=none descent member passed as `rec` itself must NOT be re-added.
        // CollectChainRunMembers' non-chain direct-add path (and the empty-members defensive fallback) bypass
        // the chain-member exclusion loop, so a standalone (chain=none) descent member used to leak back into
        // the run and draw its descent leg on the visible member's loop-clock head. The fix leaves the member
        // set EMPTY for it (its descent line comes only from its own trigger-gated primary pass).
        [Fact]
        public void CollectChainRunMembers_NonChainDescentMemberAsRec_NotReAdded()
        {
            // committed index 0 = a standalone (chain=none) descent member.
            var descent = MakeChainMember("rec-descent-standalone", chainId: null, startUT: 600.0);
            var committed = new List<Recording> { descent, MakeChainMember("x", null, 0.0) };
            // Descent set = committed index 0; member set includes it (owner=2 is unrelated here).
            var units = MakeDescentUnitSet(new[] { 0 }, new[] { 0, 2 });

            var members = new List<(int index, Recording rec)> { (9, descent) }; // stale entry must clear
            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, descent, 0, members, units);

            // The standalone descent member is fully excluded -> empty run (no leg/arc/bridge draws for it).
            Assert.Empty(members);

            // Without the loop-unit set the standalone member still collects as a single member (unchanged).
            var ungated = new List<(int index, Recording rec)>();
            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, descent, 0, ungated);
            Assert.Single(ungated);
            Assert.Same(descent, ungated[0].rec);
        }

        // A NON-descent standalone recording is unaffected by the descent guard (still a single-member run).
        [Fact]
        public void CollectChainRunMembers_NonChainNonDescentMember_StillSingleMember()
        {
            var visible = MakeChainMember("rec-visible-standalone", chainId: null, startUT: 600.0);
            var committed = new List<Recording> { visible, MakeChainMember("x", null, 0.0) };
            // Descent set = committed index 1 (NOT index 0); index 0 is the visible member.
            var units = MakeDescentUnitSet(new[] { 1 }, new[] { 0, 1 });

            var members = new List<(int index, Recording rec)>();
            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, visible, 0, members, units);

            Assert.Single(members);
            Assert.Same(visible, members[0].rec);
        }

        // The IsDescentTriggerMember seam (shared by the three forward-run exclusion sites) truth table.
        [Fact]
        public void IsDescentTriggerMember_TruthTable()
        {
            var units = MakeDescentUnitSet(new[] { 0 }, new[] { 0, 2 });

            // index 0 is the descent member -> true.
            Assert.True(GhostTrajectoryPolylineRenderer.IsDescentTriggerMember(0, units));
            // index 2 is a unit member but NOT in the descent set -> false.
            Assert.False(GhostTrajectoryPolylineRenderer.IsDescentTriggerMember(2, units));
            // index 5 is not a member at all -> false.
            Assert.False(GhostTrajectoryPolylineRenderer.IsDescentTriggerMember(5, units));
            // null loop-unit set -> false (byte-identical no-trigger path).
            Assert.False(GhostTrajectoryPolylineRenderer.IsDescentTriggerMember(0, null));

            // A unit with NO descent trigger -> false even for a member index.
            var noTrigger = new Parsek.GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, Parsek.GhostPlaybackLogic.LoopUnit>
                {
                    { 2, new Parsek.GhostPlaybackLogic.LoopUnit(2, new[] { 0, 2 }, 0.0, 1000.0, 2000.0, 0.0) }
                },
                new Dictionary<int, int> { { 0, 2 }, { 2, 2 } });
            Assert.False(GhostTrajectoryPolylineRenderer.IsDescentTriggerMember(0, noTrigger));
        }

        // Clamp B: the forward-run window's chainDataEndUT is capped at the SHIFTED parking-conic end
        // (RecordedDeorbitUT + CaptureShiftSeconds) for the NON-descent transfer member of a descent-trigger
        // unit, so once the loiter icon passes the shifted conic the past-end run no longer paints the member's
        // own unshifted recorded approach tail + captureShift-gap bridge. MakeDescentUnitSet sets
        // recordedDeorbitUT = 500, captureShiftSeconds = -100000 (shifted conic end = -99500), descent set = {0},
        // owner/member 2 = the non-descent transfer member.
        [Fact]
        public void ClampChainDataEndForDescentTransfer_CapsTransferMemberAtShiftedConicEnd()
        {
            const double shiftedConicEnd = 500.0 + (-100000.0); // recordedDeorbit + captureShift = -99500
            var units = MakeDescentUnitSet(new[] { 0 }, new[] { 0, 2 }, transferMemberIndex: 2);

            // index 2 = the destination transfer member, window end PAST the shifted conic end -> clamped down to it.
            double clamped = GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(
                600_000.0, 2, units);
            Assert.Equal(shiftedConicEnd, clamped, 6);

            // A window already at/before the shifted conic end is untouched (no upward clamp).
            double below = GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(
                shiftedConicEnd - 1000.0, 2, units);
            Assert.Equal(shiftedConicEnd - 1000.0, below, 6);
        }

        [Fact]
        public void ClampChainDataEndForDescentTransfer_UnchangedWhenGuardFalse()
        {
            const double endUT = 600_000.0;
            var units = MakeDescentUnitSet(new[] { 0 }, new[] { 0, 2 }, transferMemberIndex: 2);

            // index 0 is NOT the destination transfer member (it is a descent member) -> excluded by the guard,
            // returned unchanged (its descent line comes only from its own trigger-gated pass, not this clamp).
            Assert.Equal(endUT,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(endUT, 0, units), 6);

            // index 5 is not a member at all -> unchanged.
            Assert.Equal(endUT,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(endUT, 5, units), 6);

            // null loop-unit set -> unchanged (byte-identical no-trigger path).
            Assert.Equal(endUT,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(endUT, 2, null), 6);

            // A unit with NO descent trigger -> unchanged even for the transfer member index.
            var noTrigger = new Parsek.GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, Parsek.GhostPlaybackLogic.LoopUnit>
                {
                    { 2, new Parsek.GhostPlaybackLogic.LoopUnit(2, new[] { 0, 2 }, 0.0, 1000.0, 2000.0, 0.0) }
                },
                new Dictionary<int, int> { { 0, 2 }, { 2, 2 } });
            Assert.Equal(endUT,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(endUT, 2, noTrigger), 6);
        }

        // Clamp B is narrowed to the EXACT destination transfer member (TransferMemberIndex): a non-descent
        // RIDE-ALONG member in a different/unshifted frame (member 3 here, the member-40 Kerbin-probe analogue) is
        // NOT clamped - its forward-render window must not be clipped at the TRANSFER member's shifted conic end.
        // The old "every non-descent member" gate (!IsDescentTriggerMember) wrongly clamped it.
        [Fact]
        public void ClampChainDataEndForDescentTransfer_RideAlongMemberNotClamped()
        {
            const double shiftedConicEnd = 500.0 + (-100000.0); // the transfer member's shifted conic end
            // members = {0 descent, 2 transfer, 3 ride-along}; transfer = 2.
            var units = MakeDescentUnitSet(new[] { 0 }, new[] { 0, 2, 3 }, transferMemberIndex: 2);

            // Sanity: the transfer member (2) IS clamped down to the shifted conic end.
            Assert.Equal(shiftedConicEnd,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(600_000.0, 2, units), 6);
            // The ride-along (3) - a non-descent, non-transfer member - is NOT clamped (unchanged), even with a
            // window end far past the transfer member's shifted conic end.
            Assert.Equal(600_000.0,
                GhostTrajectoryPolylineRenderer.ClampChainDataEndForDescentTransfer(600_000.0, 3, units), 6);
        }

        // A chain recording missing from the committed list (detached caller input) falls back to the
        // single-member run instead of an empty member set (the pass must still draw something).
        [Fact]
        public void CollectChainRunMembers_ChainRecordingNotInCommitted_FallsBackToSelf()
        {
            var rec = MakeChainMember("rec-detached", "chain-x", startUT: 100.0);
            var committed = new List<Recording> { MakeChainMember("rec-unrelated", null, 0.0) };
            var members = new List<(int index, Recording rec)>();

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(committed, rec, 7, members);

            Assert.Single(members);
            Assert.Equal(7, members[0].index);
            Assert.Same(rec, members[0].rec);
        }

        // Null committed list / null rec are tolerated (single-member fallback / empty fill).
        [Fact]
        public void CollectChainRunMembers_NullInputs_Tolerated()
        {
            var rec = MakeChainMember("rec-null-committed", "chain-y", startUT: 100.0);
            var members = new List<(int index, Recording rec)> { (9, rec) }; // stale entry must clear

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(null, rec, 3, members);
            Assert.Single(members);
            Assert.Equal(3, members[0].index);

            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(
                new List<Recording>(), null, 0, members);
            Assert.Empty(members);

            // Null scratch tolerated (no throw).
            GhostTrajectoryPolylineRenderer.CollectChainRunMembers(null, rec, 0, null);
        }

        // The playtest-4 scenario at the window level: the LAUNCH chain member carries ZERO
        // OrbitSegments (handoff below orbit), the NEXT member carries the suborbital ascent conic +
        // the full-loop parking ellipse. Computed over the CONCATENATED chain segments, the run window
        // spans from the trajectory start (-inf: the launch leg is included) up to the ellipse start -
        // both while the icon still rides the launch leg AND after the handoff - so the composite
        // pad-to-ellipse line draws as one run. Per-member windows could not do this: the launch member
        // alone has no segments (no run at all), and the next member alone cannot reach the launch leg.
        [Fact]
        public void ChainConcatenatedWindow_LaunchMemberInheritsRunFromNextMember()
        {
            // Next member's effective segments (the launch member contributes none).
            double muKerbin = 3.5316e12;
            var ascentConic = new OrbitSegment
            {
                startUT = 500.0, endUT = 900.0, bodyName = "Kerbin",
                eccentricity = 0.4, semiMajorAxis = 500000.0
            };
            // Parking ellipse spanning >= one period (full loop): T(700km, Kerbin) ~ 5240s.
            var parkingEllipse = new OrbitSegment
            {
                startUT = 900.0, endUT = 11000.0, bodyName = "Kerbin",
                eccentricity = 0.001, semiMajorAxis = 700000.0
            };
            var concat = new List<OrbitSegment> { ascentConic, parkingEllipse };
            Func<string, double> mu = _ => muKerbin;

            // Icon on the LAUNCH leg (before the first conic): run reaches back to -inf (launch leg
            // included) and stops at the ellipse start.
            var preHandoff = ForwardRenderWindow.ComputeForwardWindow(concat, 200.0, mu);
            Assert.True(preHandoff.HasForwardRange);
            Assert.Equal(double.NegativeInfinity, preHandoff.RunStartUT);
            Assert.Equal(900.0, preHandoff.StopUT);
            // The launch member's ascent leg [100,450] overlaps the run and is the CURRENT head leg ->
            // drawn by the head-gated pass, not the forward pass.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                100.0, 450.0, preHandoff.RunStartUT, preHandoff.StopUT, headUT: 200.0));
            // The next member's coast leg [600,700] is a run leg already pre-handoff.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                600.0, 700.0, preHandoff.RunStartUT, preHandoff.StopUT, headUT: 200.0));

            // After the handoff (icon on the next member's ascent conic), the LAUNCH member's leg
            // still overlaps the run window (no longer under the head)...
            var postHandoff = ForwardRenderWindow.ComputeForwardWindow(concat, 600.0, mu);
            Assert.True(postHandoff.HasForwardRange);
            Assert.Equal(double.NegativeInfinity, postHandoff.RunStartUT);
            Assert.Equal(900.0, postHandoff.StopUT);
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDrawForwardLeg(
                100.0, 450.0, postHandoff.RunStartUT, postHandoff.StopUT, headUT: 600.0));
            // ...but the body-fixed hide rule (playtest 5) DROPS it from the persistent run: the launch
            // member carries no conics, so the leg is not conic-anchorable and would rotate with the
            // planet against the inertial arcs (the observed gap-then-overlap sweep). It draws only
            // while the icon rides it (head-gated pass).
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                new List<OrbitSegment>(), "Kerbin", 100.0, 450.0));

            // Icon ON the parking ellipse: the run clears (stock draws the repeating ellipse) - the
            // launch leg clears with it, matching the reset-at-boundary rule.
            var onEllipse = ForwardRenderWindow.ComputeForwardWindow(concat, 1000.0, mu);
            Assert.False(onEllipse.HasForwardRange);
        }

        // ====================================================================
        // Body-fixed run-leg hide (playtest-5 rule)
        // IsRunLegAnchorCandidate
        // ====================================================================

        // A vacuum-maneuver leg bracketed by a same-body conic on BOTH sides (the escape burn / orbit
        // raise) is conic-anchorable: it participates in the persistent run (drawn in the inertial frame).
        [Fact]
        public void IsRunLegAnchorCandidate_BothSideBracket_True()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0.0, endUT = 100.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 },
                new OrbitSegment { startUT = 160.0, endUT = 400.0, bodyName = "Kerbin", semiMajorAxis = 900000.0 },
            };
            Assert.True(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                segs, "Kerbin", legStartUT: 100.0, legEndUT: 160.0));
        }

        // A launch ascent (after-only bracket) stays body-fixed -> NOT a persistent run leg.
        [Fact]
        public void IsRunLegAnchorCandidate_AscentAfterOnly_False()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 200.0, endUT = 500.0, bodyName = "Kerbin", semiMajorAxis = 700000.0 },
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                segs, "Kerbin", legStartUT: 50.0, legEndUT: 200.0));
        }

        // A descent-to-surface (before-only bracket) stays body-fixed -> NOT a persistent run leg.
        [Fact]
        public void IsRunLegAnchorCandidate_DescentBeforeOnly_False()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0.0, endUT = 300.0, bodyName = "Duna", semiMajorAxis = 400000.0 },
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                segs, "Duna", legStartUT: 300.0, legEndUT: 380.0));
        }

        // No conics at all (the launch chain segment, atmospheric-only recordings) -> body-fixed only.
        [Fact]
        public void IsRunLegAnchorCandidate_NoConics_False()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                new List<OrbitSegment>(), "Kerbin", 100.0, 200.0));
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                null, "Kerbin", 100.0, 200.0));
        }

        // Conics of a DIFFERENT body never bracket (the lookup is same-body by contract).
        [Fact]
        public void IsRunLegAnchorCandidate_OtherBodyConics_False()
        {
            var segs = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0.0, endUT = 100.0, bodyName = "Mun", semiMajorAxis = 300000.0 },
                new OrbitSegment { startUT = 160.0, endUT = 400.0, bodyName = "Mun", semiMajorAxis = 300000.0 },
            };
            Assert.False(GhostTrajectoryPolylineRenderer.IsRunLegAnchorCandidate(
                segs, "Kerbin", legStartUT: 100.0, legEndUT: 160.0));
        }

        // ====================================================================
        // Seam bridge (playtest-6): TryBuildSeamBridgeLocalPoints /
        // SeamBridgeAngleRad / SelectBridgeArcIndex
        // ====================================================================

        // Helper: rotate v about the +Z axis by angle (radians) - the synthetic "planet spin".
        private static Vector3d RotZ(Vector3d v, double angle)
        {
            double c = System.Math.Cos(angle), s = System.Math.Sin(angle);
            return new Vector3d(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
        }

        // Synthetic arc B: a gentle curve in the XY plane starting on +X at radius 700 km.
        private static Vector3d[] MakeArcPoints(int count, double radius)
        {
            var pts = new Vector3d[count];
            for (int i = 0; i < count; i++)
            {
                double sweep = 0.002 * i; // ~0.11 deg per sample, a slow prograde arc
                pts[i] = RotZ(new Vector3d(radius, 0, 0), sweep) * (1.0 + 0.0005 * i);
            }
            return pts;
        }

        [Fact]
        public void SeamBridge_EndpointsExact()
        {
            // endA = B's first sample rotated by 10 deg about Z and 2% closer to the body: the bridge
            // must START exactly on endA and END exactly on B's merge sample.
            var arc = MakeArcPoints(80, 700000.0);
            const double seamAngle = 10.0 * System.Math.PI / 180.0;
            Vector3d endA = RotZ(arc[0], seamAngle) * 0.98;
            const int merge = 60;
            var outPts = new Vector3d[merge + 1];

            bool ok = GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                endA, arc, arcScale: 1.0, mergeCount: merge,
                maxAngleRad: GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians,
                outPoints: outPts, seamAngleRad: out double measured);

            Assert.True(ok);
            Assert.Equal(seamAngle, measured, 6);
            Assert.True((outPts[0] - endA).magnitude < 1.0,
                "bridge start must land on the leg end (off by " + (outPts[0] - endA).magnitude + " m)");
            Assert.True((outPts[merge] - arc[merge]).magnitude < 1.0,
                "bridge end must land on B's merge sample (off by " + (outPts[merge] - arc[merge]).magnitude + " m)");
        }

        // === Small-gap CHORD bridge (launch->escape seam render) ===
        // The sub-5-deg launch-aligned ascent->escape gap is filled by a STRAIGHT chord, not the merge
        // slice (whose ~200-370km bulge dwarfs the gap). These pin the pure chord builder + the four-band
        // angle classifier that routes a seam to skip / chord / merge-slice.

        [Fact]
        public void SeamBridgeChord_EndpointsExactAndStraight()
        {
            Vector3d endA = new Vector3d(700000.0, 0.0, 0.0);
            Vector3d conicNear = new Vector3d(690000.0, 60000.0, 0.0); // ~5 deg away, slightly nearer
            const int merge = 60;
            var outPts = new Vector3d[merge + 1];

            bool ok = GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeChordLocalPoints(
                endA, conicNear, arcScale: 1.0, mergeCount: merge, outPoints: outPts);

            Assert.True(ok);
            Assert.True((outPts[0] - endA).magnitude < 1e-6, "chord must start exactly on the leg end");
            Assert.True((outPts[merge] - conicNear).magnitude < 1e-6, "chord must end exactly on the conic near point");
            // A straight chord has zero perpendicular deviation from its endpoint-to-endpoint line.
            Assert.True(GhostTrajectoryPolylineRenderer.MaxChordDeviation(outPts, merge + 1) < 1e-6,
                "the chord must be perfectly straight (no bulge)");
            Assert.True((outPts[merge / 2] - (endA + conicNear) * 0.5).magnitude < 1e-6,
                "the midpoint must be the average of the two endpoints");
        }

        [Fact]
        public void SeamBridgeChord_AppliesArcScale()
        {
            // The conic near point arrives in body-LOCAL metres; arcScale converts it to the leg's space.
            Vector3d endA = new Vector3d(70.0, 0.0, 0.0);
            Vector3d conicNearLocal = new Vector3d(690000.0, 60000.0, 0.0);
            const int merge = 60;
            var outPts = new Vector3d[merge + 1];
            Assert.True(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeChordLocalPoints(
                endA, conicNearLocal, arcScale: 1e-4, mergeCount: merge, outPoints: outPts));
            Assert.True((outPts[merge] - conicNearLocal * 1e-4).magnitude < 1e-9,
                "the chord end must apply arcScale to the conic near point");
        }

        [Fact]
        public void SeamBridgeChord_DegenerateInputs_ReturnFalse()
        {
            Vector3d a = new Vector3d(700000.0, 0, 0), b = new Vector3d(690000, 60000, 0);
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeChordLocalPoints(a, b, 1.0, 60, null));
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeChordLocalPoints(a, b, 1.0, 0, new Vector3d[61]));
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeChordLocalPoints(a, b, 1.0, 60, new Vector3d[10]));
        }

        [Fact]
        public void ClassifySeamBridgeByAngle_FourBands()
        {
            double deg = System.Math.PI / 180.0;
            // > 45 deg or infinite -> honest gap, no bridge.
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.SkipAngleTooLarge,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(double.PositiveInfinity));
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.SkipAngleTooLarge,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(60.0 * deg));
            // (5, 45] -> the conic merge slice (existing moderate-misalignment smoother).
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.MergeSlice,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(20.0 * deg));
            // (0.5, 5] -> the new straight chord (the launch->escape gap lives here, ~0.5-4.6 deg).
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.Chord,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(4.6 * deg));
            // <= 0.5 deg -> the leg already meets the conic; no (degenerate) bridge.
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.SkipMeetsConic,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(0.2 * deg));
            // Boundaries: 5 deg -> chord (inclusive), 45 deg -> merge slice (inclusive), 0.5 deg -> meets.
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.Chord,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians));
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.MergeSlice,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians));
            Assert.Equal(GhostTrajectoryPolylineRenderer.SeamBridgeKind.SkipMeetsConic,
                GhostTrajectoryPolylineRenderer.ClassifySeamBridgeByAngle(GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians));
        }

        [Fact]
        public void SeamBridge_ZeroAngle_DegeneratesToArcLeadIn()
        {
            // Seam closed (endA == B[0]): the bridge IS B's lead-in, point for point.
            var arc = MakeArcPoints(80, 700000.0);
            const int merge = 60;
            var outPts = new Vector3d[merge + 1];

            bool ok = GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                arc[0], arc, 1.0, merge,
                GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians, outPts, out double measured);

            Assert.True(ok);
            Assert.True(measured < 1e-6);
            for (int i = 0; i <= merge; i += 15)
                Assert.True((outPts[i] - arc[i]).magnitude < 1.0,
                    "zero-angle bridge point " + i + " must equal B's sample");
        }

        [Fact]
        public void SeamBridge_AngleTooLarge_NoBridge()
        {
            // 90 deg seam > the 45 deg gate: no bridge (honest gap instead of a wild spiral).
            var arc = MakeArcPoints(80, 700000.0);
            Vector3d endA = RotZ(arc[0], 90.0 * System.Math.PI / 180.0);
            var outPts = new Vector3d[61];

            bool ok = GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                endA, arc, 1.0, 60,
                GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians, outPts, out double measured);

            Assert.False(ok);
            Assert.Equal(System.Math.PI / 2.0, measured, 6);
        }

        [Fact]
        public void SeamBridge_DegenerateInputs_NoBridge()
        {
            var arc = MakeArcPoints(80, 700000.0);
            var outPts = new Vector3d[61];
            // Zero-length A end.
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                Vector3d.zero, arc, 1.0, 60, 10.0, outPts, out _));
            // Antiparallel rays (no unique axis) - even with a permissive gate.
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                -arc[0], arc, 1.0, 60, 10.0, outPts, out _));
            // Null / too-short buffers.
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                arc[0], null, 1.0, 60, 10.0, outPts, out _));
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                arc[0], arc, 1.0, 60, 10.0, new Vector3d[10], out _));
            // mergeCount beyond the arc sample count.
            Assert.False(GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                arc[0], MakeArcPoints(30, 700000.0), 1.0, 60, 10.0, outPts, out _));
        }

        [Fact]
        public void SeamBridgeAngleRad_MeasuresRayAngle_DegenerateIsInfinite()
        {
            Vector3d x = new Vector3d(1000.0, 0, 0);
            Assert.Equal(System.Math.PI / 2.0,
                GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(x, new Vector3d(0, 2000.0, 0)), 9);
            Assert.Equal(0.0, GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(x, x * 5.0), 9);
            Assert.True(double.IsPositiveInfinity(
                GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(Vector3d.zero, x)));
        }

        // Min-angle gate (re-aim launch-aligned-ascent render-polish fix): the now-common case where the
        // body-fixed launch ascent ALREADY MEETS the inertial escape conic (a few-km positional gap, a
        // tiny seam angle) must SKIP the bridge - the fixed ~74 deg conic merge slice would bulge a
        // disproportionate ~200-370 km off such a near-meet, reading as a spurious extra segment. The
        // gate in DecideSeamBridges is exactly `angleRad <= BridgeMinAngleRadians`; this exercises that
        // decision through the same pure SeamBridgeAngleRad the gate calls, at Kerbin geometry.
        [Fact]
        public void SeamBridge_MinAngleGate_NearMeetSkips_RealGapBridges()
        {
            // Threshold pinned at 5 deg.
            Assert.Equal(5.0 * System.Math.PI / 180.0,
                GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians, 9);
            // ...and comfortably below the 45 deg max (the gate window is non-empty).
            Assert.True(GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians
                < GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians);

            // Leg endpoint near Kerbin's surface (radius ~670 km from centre).
            const double r = 670000.0;
            Vector3d legRel = new Vector3d(r, 0.0, 0.0);

            // NEAR-MEET: the launch-aligned ascent end and the conic seam are 4.59 deg apart (the largest
            // redundant launch bridge from the aa48920e playtest; a ~54 km chord). Gate condition true ->
            // bridge SKIPPED (the leg meets the conic; no disproportionate bridge).
            Vector3d seamNearMeet = RotZ(legRel, 4.59 * System.Math.PI / 180.0);
            double nearMeetAngle = GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(legRel, seamNearMeet);
            Assert.True(nearMeetAngle <= GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians,
                "a near-aligned launch handoff (4.59 deg) must fall at/below the min gate and skip the bridge");

            // Also the aligned-seam ~0-3 deg population the design doc reports collapses to: skips.
            Vector3d seamAligned = RotZ(legRel, 0.31 * System.Math.PI / 180.0);
            Assert.True(
                GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(legRel, seamAligned)
                    <= GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians,
                "a 0.31 deg aligned-seam handoff must skip the bridge");

            // REAL GAP: a genuine moderate misalignment (the 26.77 deg 8538d9e1 case, a ~310 km chord -
            // comparable to the bridge's own bulge, within the designed 5-45 deg range). Gate condition
            // false -> bridge STILL DRAWS (it smooths a real visible gap; not re-opened by this fix).
            Vector3d seamRealGap = RotZ(legRel, 26.77 * System.Math.PI / 180.0);
            double realGapAngle = GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(legRel, seamRealGap);
            Assert.True(realGapAngle > GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians,
                "a 26.77 deg moderate-misalignment gap must stay above the min gate and still bridge");
            Assert.True(realGapAngle <= GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians,
                "26.77 deg is within the 45 deg max, so the bridge is not skipped as too-large either");

            // A mid-range designed bridge (10 deg) also still draws: this fix does not narrow the 5-45
            // deg range other missions / same-parent loops rely on.
            Assert.True(
                GhostTrajectoryPolylineRenderer.SeamBridgeAngleRad(
                    legRel, RotZ(legRel, 10.0 * System.Math.PI / 180.0))
                    > GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians,
                "a 10 deg moderate-misalignment gap must still bridge");
        }

        // The adjacency rule, both sides (playtest 7): a conic neighbours a leg seam when it shares
        // the body and ends (start-side) / starts (end-side) within [seam - maxGap, seam + 1s].
        [Fact]
        public void IsBridgeAdjacentConic_BothSides()
        {
            // END side (conic CONTINUES the leg): leg ends at 200, conic [204, 800] qualifies.
            Assert.True(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Kerbin", 204.0, 800.0, "Kerbin", legSeamUT: 200.0, atLegStart: false,
                maxSeamGapSeconds: 120.0));
            // ...but not with a 1 s gap budget (starts 4 s after).
            Assert.False(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Kerbin", 204.0, 800.0, "Kerbin", 200.0, false, 1.0));
            // A conic starting BEFORE the leg end (a past arc) never continues it.
            Assert.False(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Kerbin", 100.0, 195.0, "Kerbin", 200.0, false, 120.0));

            // START side (conic PRECEDES the leg): leg starts at 500, conic [100, 496] qualifies.
            Assert.True(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Duna", 100.0, 496.0, "Duna", legSeamUT: 500.0, atLegStart: true,
                maxSeamGapSeconds: 120.0));
            // A conic ending AFTER the leg start (overlapping forward) does not precede it.
            Assert.False(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Duna", 100.0, 502.0, "Duna", 500.0, true, 120.0));
            // Other body never neighbours.
            Assert.False(GhostTrajectoryPolylineRenderer.IsBridgeAdjacentConic(
                "Ike", 100.0, 496.0, "Duna", 500.0, true, 120.0));
        }

        // Intervening-ascent-leg rule (launch-escape-seam render fix): a launch records across two
        // consecutive body-fixed legs (pad ascent -> continuation ascent) feeding ONE escape conic.
        // Only the body-fixed leg IMMEDIATELY adjacent to the conic may bridge to it; the earlier leg's
        // end is adjacent only in UT (the continuation leg sits between it and the conic), so its bridge
        // would shortcut over the continuation - it must skip.
        [Fact]
        public void HasInterveningContinuationLeg_LaunchAscentChain_PadLegSkips_ContinuationBridges()
        {
            // Chain: pad ascent [0, 70] (idx 0), continuation ascent [70, 200] (idx 1); escape conic
            // starts at 200. Both legs are "Kerbin" body-fixed bridge candidates.
            var legs = new[]
            {
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 0.0, endUT = 70.0, bodyName = "Kerbin" },   // pad ascent
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 70.0, endUT = 200.0, bodyName = "Kerbin" }, // continuation ascent
            };
            const double conicStartUT = 200.0;

            // PAD leg (idx 0) END side: seam = 70, conic starts at 200. The continuation leg (idx 1)
            // STARTS at 70 and ends before the conic -> it intervenes -> the pad leg's bridge SKIPS.
            Assert.True(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 0, candBodyName: "Kerbin",
                legSeamUT: 70.0, conicSeamUT: conicStartUT, atLegStart: false,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int padIntervening, out double padInterveningSeam));
            Assert.Equal(1, padIntervening);
            Assert.Equal(70.0, padInterveningSeam, 6);

            // CONTINUATION leg (idx 1) END side: seam = 200, conic starts at 200. No other Kerbin leg
            // starts in (200, 200) -> the only other leg starts at 0, far before -> NOT intervening ->
            // the continuation leg's bridge still DRAWS (and the near-meet gate resolves it).
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 1, candBodyName: "Kerbin",
                legSeamUT: 200.0, conicSeamUT: conicStartUT, atLegStart: false,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int contIntervening, out _));
            Assert.Equal(-1, contIntervening);
        }

        // A single body-fixed leg immediately followed by a conic (no intervening same-body leg) must
        // NOT be flagged - the conic IS its immediate next segment, so the legitimate leg->conic bridge
        // is preserved.
        [Fact]
        public void HasInterveningContinuationLeg_SingleLegThenConic_DoesNotSkip()
        {
            var legs = new[]
            {
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 0.0, endUT = 100.0, bodyName = "Kerbin" },
            };
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 0, candBodyName: "Kerbin",
                legSeamUT: 100.0, conicSeamUT: 100.0, atLegStart: false,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int intervening, out _));
            Assert.Equal(-1, intervening);
        }

        // A different-body leg between this leg's end and the conic is NOT a continuation here (an SOI
        // seam, not a same-body chain) and must not suppress the bridge.
        [Fact]
        public void HasInterveningContinuationLeg_DifferentBodyLeg_DoesNotSkip()
        {
            var legs = new[]
            {
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 0.0, endUT = 70.0, bodyName = "Kerbin" },
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 70.0, endUT = 200.0, bodyName = "Mun" }, // different body
            };
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 0, candBodyName: "Kerbin",
                legSeamUT: 70.0, conicSeamUT: 200.0, atLegStart: false,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int intervening, out _));
            Assert.Equal(-1, intervening);
        }

        // START-side symmetry: a conic feeding into a leg that is preceded by ANOTHER body-fixed leg
        // (which sits between the conic and this leg) must skip; the immediately-adjacent leg bridges.
        [Fact]
        public void HasInterveningContinuationLeg_StartSide_PrecedingLegSkips()
        {
            // Conic ends at 100; leg A [100, 200] descends from the conic; leg B [200, 300] continues
            // after A. Both "Duna" body-fixed.
            var legs = new[]
            {
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 100.0, endUT = 200.0, bodyName = "Duna" }, // immediately after conic
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 200.0, endUT = 300.0, bodyName = "Duna" }, // further along
            };
            const double conicEndUT = 100.0;

            // Leg B (idx 1) START side: seam = 200, conic ends at 100. Leg A (idx 0) ENDS at 200 and
            // starts after the conic -> A sits between the conic and B -> B's start-side bridge SKIPS.
            Assert.True(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 1, candBodyName: "Duna",
                legSeamUT: 200.0, conicSeamUT: conicEndUT, atLegStart: true,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int bIntervening, out double bInterveningSeam));
            Assert.Equal(0, bIntervening);
            Assert.Equal(200.0, bInterveningSeam, 6);

            // Leg A (idx 0) START side: seam = 100, conic ends at 100. No other Duna leg ends in
            // (100, 100) -> A is the immediate successor of the conic -> A's bridge still DRAWS.
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                legs, selfIndex: 0, candBodyName: "Duna",
                legSeamUT: 100.0, conicSeamUT: conicEndUT, atLegStart: true,
                seamTolSeconds: GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds,
                out int aIntervening, out _));
            Assert.Equal(-1, aIntervening);
        }

        // Null / empty inputs are tolerated (no intervening leg found).
        [Fact]
        public void HasInterveningContinuationLeg_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                null, 0, "Kerbin", 70.0, 200.0, false, 1.0, out int i1, out _));
            Assert.Equal(-1, i1);
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                new GhostTrajectoryPolylineRenderer.BridgeLegSpan[0], 0, "Kerbin",
                70.0, 200.0, false, 1.0, out int i2, out _));
            Assert.Equal(-1, i2);
            // Empty body name -> no body to match.
            Assert.False(GhostTrajectoryPolylineRenderer.HasInterveningContinuationLeg(
                new[] { new GhostTrajectoryPolylineRenderer.BridgeLegSpan
                    { startUT = 70.0, endUT = 200.0, bodyName = "Kerbin" } },
                -1, "", 0.0, 200.0, false, 1.0, out int i3, out _));
            Assert.Equal(-1, i3);
        }

        // The signed-gap (overshoot) rule (playtest 7, maintainer rule): bridge only when the previous
        // element's end sits BEHIND the next element's start along the direction of travel.
        [Fact]
        public void IsSeamGapAhead_GapVsOvershoot()
        {
            Vector3d travelDir = new Vector3d(1.0, 0.0, 0.0);
            Vector3d prevEnd = new Vector3d(100.0, 50.0, 0.0);
            // Next start AHEAD of the previous end along travel: a real gap -> bridge.
            Assert.True(GhostTrajectoryPolylineRenderer.IsSeamGapAhead(
                prevEnd, new Vector3d(140.0, 50.0, 0.0), travelDir));
            // Next start BEHIND the previous end (overshoot, lines already overlap) -> no bridge.
            Assert.False(GhostTrajectoryPolylineRenderer.IsSeamGapAhead(
                prevEnd, new Vector3d(60.0, 50.0, 0.0), travelDir));
            // Coincident (anchored leg sitting exactly on the seam) -> no bridge.
            Assert.False(GhostTrajectoryPolylineRenderer.IsSeamGapAhead(
                prevEnd, prevEnd, travelDir));
        }

        // The past/future visibility rule (playtest 7): only PAST body-fixed legs hide; future
        // body-fixed legs (the Duna landing descent) and all anchorable legs draw.
        [Fact]
        public void ShouldHideBodyFixedRunLeg_OnlyPastNonAnchorable()
        {
            // Past + body-fixed: hidden (would rotate-sweep behind the icon).
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldHideBodyFixedRunLeg(
                anchorCandidate: false, legEndUT: 100.0, headUT: 200.0));
            // FUTURE body-fixed (the landing descent): draws.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldHideBodyFixedRunLeg(
                anchorCandidate: false, legEndUT: 300.0, headUT: 200.0));
            // Anchorable legs never hide, past or future.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldHideBodyFixedRunLeg(
                anchorCandidate: true, legEndUT: 100.0, headUT: 200.0));
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldHideBodyFixedRunLeg(
                anchorCandidate: true, legEndUT: 300.0, headUT: 200.0));
        }

        // The on-demand bridge sample span (playtest-8 star fix): duration/3 for short conics, but
        // clamped by period/3 for multi-revolution segments - the ~660-rev parking-ellipse loiter
        // previously sampled 61 points tens of thousands of seconds apart (arbitrary orbit phases),
        // which drew as a star polygon around Kerbin.
        [Fact]
        public void ComputeBridgeSampleSpan_PeriodClampsMultiRevSegments()
        {
            // Multi-rev ellipse: duration 11.4M s, period 5240 s -> span = period/3, NOT duration/3.
            Assert.Equal(5240.0 / 3.0, GhostTrajectoryPolylineRenderer.ComputeBridgeSampleSpanSeconds(
                0.0, 11400000.0, periodSeconds: 5240.0), 9);
            // Short conic (sub-period): duration/3.
            Assert.Equal(858.0 / 3.0, GhostTrajectoryPolylineRenderer.ComputeBridgeSampleSpanSeconds(
                100.0, 958.0, periodSeconds: 2680.0), 9);
            // Hyperbolic / unknown mu (NaN period): duration/3 fallback.
            Assert.Equal(900.0 / 3.0, GhostTrajectoryPolylineRenderer.ComputeBridgeSampleSpanSeconds(
                0.0, 900.0, periodSeconds: double.NaN), 9);
            // Degenerate segment -> 0 (caller skips); tiny segments floor at 1 s.
            Assert.Equal(0.0, GhostTrajectoryPolylineRenderer.ComputeBridgeSampleSpanSeconds(
                100.0, 100.0, 5000.0));
            Assert.Equal(1.0, GhostTrajectoryPolylineRenderer.ComputeBridgeSampleSpanSeconds(
                100.0, 101.5, 5000.0));
        }

        // Terminal-leg exception input (playtest 11): the past-hide rule applies only when an
        // ABOVE-SURFACE conic follows the leg within the run - the Duna landing trail (nothing after
        // it) stays visible; below-surface conics never count (they are not drawn).
        [Fact]
        public void AnyAboveSurfaceConicStartsAtOrAfter_TerminalVsFollowed()
        {
            var followed = new List<OrbitSegment>
            {
                CleanDunaSegment(100.0, 400.0),
                CleanDunaSegment(500.0, 900.0),
            };
            // Leg ending at 480: the conic at 500 follows within the window -> hide applies.
            Assert.True(GhostTrajectoryPolylineRenderer.AnyAboveSurfaceConicStartsAtOrAfter(
                followed, ut: 480.0, windowStopUT: 1000.0, surface: DunaSurface()));
            // Window stops before the follower -> nothing follows IN THE RUN.
            Assert.False(GhostTrajectoryPolylineRenderer.AnyAboveSurfaceConicStartsAtOrAfter(
                followed, 480.0, windowStopUT: 500.0, surface: DunaSurface()));
            // Terminal landing trail: every conic starts before the leg end -> keep visible.
            Assert.False(GhostTrajectoryPolylineRenderer.AnyAboveSurfaceConicStartsAtOrAfter(
                followed, ut: 950.0, windowStopUT: double.PositiveInfinity, surface: DunaSurface()));
            // A BELOW-SURFACE follower does not count (never drawn).
            var belowOnly = new List<OrbitSegment> { DegenerateDunaSegment(500.0, 900.0) };
            Assert.False(GhostTrajectoryPolylineRenderer.AnyAboveSurfaceConicStartsAtOrAfter(
                belowOnly, 480.0, double.PositiveInfinity, DunaSurface()));
            // Null list tolerated.
            Assert.False(GhostTrajectoryPolylineRenderer.AnyAboveSurfaceConicStartsAtOrAfter(
                null, 480.0, double.PositiveInfinity, DunaSurface()));
        }

        // Chord-deviation diagnostic (playtest-11 straightness instrumentation): a circular arc
        // bulges from its chord; collinear points read ~0.
        [Fact]
        public void MaxChordDeviation_ArcVsStraight()
        {
            // Quarter circle of radius 1000: max chord deviation = r*(1 - cos(45 deg)) ~ 292.9.
            var arc = new Vector3d[31];
            for (int i = 0; i <= 30; i++)
            {
                double a = (System.Math.PI / 2.0) * i / 30.0;
                arc[i] = new Vector3d(1000.0 * System.Math.Cos(a), 1000.0 * System.Math.Sin(a), 0);
            }
            double dev = GhostTrajectoryPolylineRenderer.MaxChordDeviation(arc, 31);
            Assert.True(System.Math.Abs(dev - 292.89) < 1.0, "quarter-circle deviation was " + dev);

            // Collinear points: ~0.
            var line = new Vector3d[10];
            for (int i = 0; i < 10; i++) line[i] = new Vector3d(i * 100.0, i * 50.0, 0);
            Assert.True(GhostTrajectoryPolylineRenderer.MaxChordDeviation(line, 10) < 1e-9);

            // Degenerate inputs -> 0.
            Assert.Equal(0.0, GhostTrajectoryPolylineRenderer.MaxChordDeviation(null, 10));
            Assert.Equal(0.0, GhostTrajectoryPolylineRenderer.MaxChordDeviation(line, 2));
        }

        // The TS marker OrbitSegmentActive veto (playtest-12 icon fix): a recorded conic covering the
        // current UT vetoes the marker ONLY while the polyline does NOT own the phase. When it owns
        // (proto line/icon hidden - or the proto destroyed on a below-surface descent), the marker is
        // the sole position indicator and must draw.
        [Fact]
        public void ClassifyAtmosphericMarkerSkip_OrbitSegmentVeto_BypassedWhenPolylineOwns()
        {
            var rec = new Recording { RecordingId = "rec-veto-bypass" };
            rec.Points.Add(MakePoint(100.0, 0.0, 0.0, 60000.0, "Duna"));
            rec.Points.Add(MakePoint(400.0, 0.0, 1.0, 50000.0, "Duna"));
            rec.OrbitSegments.Add(DegenerateDunaSegment(150.0, 350.0)); // covers currentUT below

            // Not owned: the covering conic vetoes the marker (the orbit icon is presumed to draw).
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting("rec-veto-bypass", false);
            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.OrbitSegmentActive,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 250.0, suppressedIds: null));

            // Polyline owns the phase: the marker is the sole indicator -> the veto is bypassed.
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting("rec-veto-bypass", true);
            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.None,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(rec, 0, 250.0, null));
        }

        // RecordedLongitudeAtUT is the exact inverse of BodyFixedLongitudeAtUT (playtest-12
        // gap-fill): a conic sample converted through the live rotation, counter-rotated to the
        // recorded basis, must roundtrip back through the draw path's forward correction.
        [Fact]
        public void RecordedLongitudeAtUT_RoundtripsWithBodyFixedLongitudeAtUT()
        {
            const double rotationPeriod = 65517.859; // Duna
            const double sampleUT = 70963500.0;
            const double liveUT = 2410196581.0;
            const double lonAtLive = 42.5;

            double recorded = GhostTrajectoryPolylineRenderer.RecordedLongitudeAtUT(
                lonAtLive, sampleUT, liveUT, rotationPeriod);
            double roundtrip = GhostTrajectoryPolylineRenderer.BodyFixedLongitudeAtUT(
                recorded, sampleUT, liveUT, rotationPeriod);
            Assert.Equal(lonAtLive, roundtrip, 6);

            // Degenerate rotation period: identity.
            Assert.Equal(lonAtLive, GhostTrajectoryPolylineRenderer.RecordedLongitudeAtUT(
                lonAtLive, sampleUT, liveUT, 0.0), 9);
        }

        // The frameless-gap conic fill (playtest-12 straight-chord fix): a 208 s recorded-data gap
        // covered by a BELOW-SURFACE conic gains interior points sampled from that conic; short gaps,
        // uncovered gaps, and above-surface conics (which split the leg instead) are untouched.
        [Fact]
        public void FillFramelessGapsFromConics_FillsOnlyCoveredLongGaps()
        {
            var rec = new Recording { RecordingId = "rec-gapfill" };
            // The below-surface descent conic covering the long gap (the seg-21 shape).
            rec.OrbitSegments.Add(DegenerateDunaSegment(1000.0, 1210.0));

            var pts = new List<TrajectoryPoint>
            {
                MakePoint(990.0, 0.0, 10.0, 58000.0, "Duna"),
                MakePoint(1000.0, 0.0, 11.0, 57000.0, "Duna"),   // gap start
                MakePoint(1208.0, 0.0, 30.0, 50100.0, "Duna"),   // 208 s frameless gap
                MakePoint(1212.0, 0.0, 30.2, 50000.0, "Duna"),
            };

            int sampled = 0;
            GhostTrajectoryPolylineRenderer.ConicGapSampler sampler =
                (OrbitSegment seg, double ut, out double lat, out double lon, out double alt) =>
                {
                    sampled++;
                    lat = 0.0;
                    lon = 11.0 + (ut - 1000.0) * (19.0 / 208.0); // synthetic curve interior
                    alt = 57000.0 - (ut - 1000.0) * 33.0;
                    return true;
                };

            int inserted = GhostTrajectoryPolylineRenderer.FillFramelessGapsFromConics(
                pts, rec, DunaSurface(), sampler);

            Assert.True(inserted > 0, "the covered 208 s gap must gain interior points");
            Assert.Equal(sampled, inserted);
            Assert.True(inserted <= GhostTrajectoryPolylineRenderer.GapFillMaxPointsPerGap);
            // All inserted points sit strictly inside the gap.
            for (int i = 4; i < pts.Count; i++)
                Assert.True(pts[i].ut > 1000.0 && pts[i].ut < 1208.0,
                    "inserted point at " + pts[i].ut + " must lie inside the gap");

            // No sampler / no surface provider -> no-op.
            var pts2 = new List<TrajectoryPoint>(pts.GetRange(0, 4));
            Assert.Equal(0, GhostTrajectoryPolylineRenderer.FillFramelessGapsFromConics(
                pts2, rec, DunaSurface(), null));
            Assert.Equal(0, GhostTrajectoryPolylineRenderer.FillFramelessGapsFromConics(
                pts2, rec, null, sampler));

            // An ABOVE-surface conic covering the gap does not fill (the cover splits the leg there
            // instead; filling would double-draw the arc).
            var recAbove = new Recording { RecordingId = "rec-gapfill-above" };
            recAbove.OrbitSegments.Add(CleanDunaSegment(1000.0, 1210.0));
            Assert.Equal(0, GhostTrajectoryPolylineRenderer.FillFramelessGapsFromConics(
                pts2, recAbove, DunaSurface(), sampler));

            // A short gap (under GapFillMinSeconds) is untouched.
            var shortPts = new List<TrajectoryPoint>
            {
                MakePoint(1000.0, 0.0, 11.0, 57000.0, "Duna"),
                MakePoint(1010.0, 0.0, 12.0, 56800.0, "Duna"),
            };
            Assert.Equal(0, GhostTrajectoryPolylineRenderer.FillFramelessGapsFromConics(
                shortPts, rec, DunaSurface(), sampler));
        }

        // The rotating-frame drift predicate (playtest-9): at low altitude KSP's world frame
        // co-rotates with the main body, freezing once-captured "inertial" offsets against the live
        // frame; any InverseRotAngle drift beyond epsilon must trigger the in-place resample.
        [Fact]
        public void HasFrameRotationDrift_DetectsRotation()
        {
            // No drift: inertial-frame era (angle frozen) -> cache holds.
            Assert.False(GhostTrajectoryPolylineRenderer.HasFrameRotationDrift(123.456, 123.456));
            Assert.False(GhostTrajectoryPolylineRenderer.HasFrameRotationDrift(123.456, 123.456 + 1e-9));
            // One frame of Kerbin rotation at 1x (~0.0003 deg) is far above epsilon -> resample.
            Assert.True(GhostTrajectoryPolylineRenderer.HasFrameRotationDrift(123.456, 123.4563));
            // Wrap-scale changes obviously drift.
            Assert.True(GhostTrajectoryPolylineRenderer.HasFrameRotationDrift(359.9, 0.1));
        }

        // A START-side bridge feeds the conic's TAIL REVERSED through the same pure helper: slice[0]
        // (the conic's end, full seam rotation) must land exactly on the leg start, slice[M] (the tail
        // merge sample) exactly on the conic.
        [Fact]
        public void SeamBridge_ReversedTailSlice_EndpointsExact()
        {
            var arc = MakeArcPoints(80, 400000.0);
            const int merge = 60;
            // Reversed tail slice: slice[i] = arc[last - i].
            var slice = new Vector3d[merge + 1];
            int lastIdx = arc.Length - 1;
            for (int i = 0; i <= merge; i++) slice[i] = arc[lastIdx - i];
            // Leg start = the conic end rotated 8 deg about Z (the landing leg under live rotation).
            const double seamAngle = 8.0 * System.Math.PI / 180.0;
            Vector3d legStart = RotZ(arc[lastIdx], seamAngle);
            var outPts = new Vector3d[merge + 1];

            bool ok = GhostTrajectoryPolylineRenderer.TryBuildSeamBridgeLocalPoints(
                legStart, slice, 1.0, merge,
                GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians, outPts, out double measured);

            Assert.True(ok);
            Assert.Equal(seamAngle, measured, 6);
            Assert.True((outPts[0] - legStart).magnitude < 1.0,
                "reversed-tail bridge must start exactly on the leg start");
            Assert.True((outPts[merge] - arc[lastIdx - merge]).magnitude < 1.0,
                "reversed-tail bridge must end exactly on the conic's tail merge sample");
        }

        // --- Descent-icon decouple: ResolveUndrawnLegFallback (pure marker-ride fallback decision) ---
        // When the leg covering the marker's head UT was NOT drawn this frame, a CONIC-ANCHORED leg's
        // drawn line is ~96 deg off the body-fixed head, so the marker must HOLD its last on-line position;
        // a NON-anchored body-fixed leg (descent / atmospheric / surface) draws the raw body-fixed points,
        // so the caller's fresh body-fixed head is already on the line and is preferred (no stale hold).
        // The Unity-coupled wiring (wasAnchored stamped in TryDrawLeg, read in TryAnchorMarkerToPolyline)
        // is covered by the in-game test (project rule: Unity-coupled -> in-game).

        [Fact]
        public void UndrawnAnchoredLeg_HoldsLastGoodOnLine()
        {
            Assert.Equal(
                GhostTrajectoryPolylineRenderer.UndrawnLegFallback.HoldLastGoodOnLine,
                GhostTrajectoryPolylineRenderer.ResolveUndrawnLegFallback(legWasConicAnchored: true));
        }

        [Fact]
        public void UndrawnNonAnchoredLeg_UsesFreshBodyFixedHead()
        {
            // The descent case: a body-fixed surface/atmospheric leg that was not redrawn this frame must
            // fall through to the caller's fresh body-fixed head, NOT a <=5 s-stale hold, so the icon stays
            // current and on the line at warp / on the IMGUI Layout-pass dropout.
            Assert.Equal(
                GhostTrajectoryPolylineRenderer.UndrawnLegFallback.UseFreshBodyFixedHead,
                GhostTrajectoryPolylineRenderer.ResolveUndrawnLegFallback(legWasConicAnchored: false));
        }
    }
}
