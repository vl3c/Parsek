using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the spawn-time tail-derived terminal orbit fallback added
    /// to <see cref="VesselSpawner"/> — a recording whose only stored
    /// <see cref="OrbitSegment"/> is older than its last absolute coast frame
    /// (typical post-circ-burn case where scene exit followed quickly) must
    /// have its orbit re-derived from that frame at spawn time so the safety
    /// gate uses the actual final orbit, not the stale pre-burn ellipse.
    ///
    /// Tests focus on the data-walking and freshness-comparison logic that
    /// runs without Unity transforms. The orbit-math leg
    /// (<c>body.GetWorldSurfacePosition</c> + <c>Orbit.UpdateFromStateVectors</c>)
    /// is exercised by the in-game test in <c>InGameTests/RuntimeTests.cs</c>.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnTerminalOrbitFromTailTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnTerminalOrbitFromTailTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- TryFindLatestCoastTrajectoryFrame ----------

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_NoSections_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "r1" };

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint _);

            Assert.False(found);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_PicksLastExoBallisticAbsoluteFrame()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 100, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 110, endUT: 120,
                BuildPoint(ut: 115, lat: 0.1, lon: 0.2, alt: 203000, vel: new Vector3(1700, 0.1f, -1000))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(115, frame.ut);
            Assert.Equal(203000, frame.altitude);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_SkipsExoPropulsiveTail_WalksBackToCoast()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.ExoPropulsive,
                startUT: 110, endUT: 120,
                BuildPoint(ut: 118, lat: 0.05, lon: 0.05, alt: 201000, vel: new Vector3(1600, 0, -100))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(105, frame.ut); // Walked back past the propulsive tail.
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_SkipsSurfaceSection()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.SurfaceMobile,
                startUT: 110, endUT: 120,
                BuildPoint(ut: 118, lat: 0.05, lon: 0.05, alt: 50, vel: new Vector3(20, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(105, frame.ut);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_SkipsAtmosphericAndApproach()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.Atmospheric,
                startUT: 110, endUT: 115,
                BuildPoint(ut: 112, lat: 0.05, lon: 0.05, alt: 30000, vel: new Vector3(800, 0, 0))));
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.Approach,
                startUT: 115, endUT: 120,
                BuildPoint(ut: 118, lat: 0.05, lon: 0.05, alt: 5000, vel: new Vector3(300, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(105, frame.ut);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_AllSurfaceSections_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.SurfaceStationary,
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 50, vel: Vector3.zero)));
            rec.TrackSections.Add(BuildAbsoluteSection(
                env: SegmentEnvironment.SurfaceMobile,
                startUT: 110, endUT: 120,
                BuildPoint(ut: 118, lat: 0.01, lon: 0.01, alt: 50, vel: new Vector3(10, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint _);

            Assert.False(found);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_RelativeSectionWithoutShadow_WalksBack()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0.5, lon: 1.0, alt: 200000, vel: new Vector3(1700, 0, -1000))));
            // Last section is Relative with anchor-local Cartesian dx/dy/dz in
            // lat/lon/alt fields; without absoluteFrames shadow these CANNOT be
            // reseeded as planet-relative orbits.
            var relativeSection = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 110,
                endUT = 120,
                frames = new List<TrajectoryPoint>
                {
                    BuildPoint(ut: 118, lat: 5.0, lon: -3.0, alt: 12.5, vel: new Vector3(1, 2, 3))
                }
            };
            rec.TrackSections.Add(relativeSection);

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(105, frame.ut); // Came from the Absolute section, NOT the Relative one.
            Assert.Equal(0.5, frame.latitude);
            Assert.Equal(1.0, frame.longitude);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_RelativeSectionWithShadow_UsesShadow()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            var relativeSection = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 110,
                endUT = 120,
                frames = new List<TrajectoryPoint>
                {
                    BuildPoint(ut: 115, lat: 5.0, lon: -3.0, alt: 12.5, vel: new Vector3(1, 2, 3)),
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    BuildPoint(ut: 119, lat: 0.7, lon: 8.5, alt: 203500,
                        vel: new Vector3(1736f, -1.7f, -1179f))
                }
            };
            rec.TrackSections.Add(relativeSection);

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(119, frame.ut);
            Assert.Equal(0.7, frame.latitude);
            Assert.Equal(203500, frame.altitude);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_BodyMismatch_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(
                    ut: 105, lat: 0, lon: 0, alt: 200000,
                    vel: new Vector3(1500, 0, 0), bodyName: "Mun")));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint _);

            Assert.False(found);
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_NonFiniteOnlyFrame_FallsThroughToPriorSection()
        {
            var rec = new Recording { RecordingId = "r1" };
            // First section: valid coast frame.
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            // Last section has only one frame and it's corrupt — the in-section
            // walk-back cannot recover, so the helper falls through to the prior
            // section. Companion to _NonFiniteTailFrame_WalksBackInSection which
            // covers the case where the section has earlier valid frames.
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 110, endUT: 120,
                BuildPoint(ut: 118, lat: 0, lon: 0, alt: 201000,
                    vel: new Vector3(float.NaN, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(105, frame.ut);
        }

        // ---------- ResolveLatestStoredOrbitSegmentEndUT ----------

        [Fact]
        public void ResolveLatestStoredOrbitSegmentEndUT_NoSegments_ReturnsNaN()
        {
            var rec = new Recording { RecordingId = "r1" };

            double endUT = VesselSpawner.ResolveLatestStoredOrbitSegmentEndUT(rec, "Kerbin");

            Assert.True(double.IsNaN(endUT));
        }

        [Fact]
        public void ResolveLatestStoredOrbitSegmentEndUT_PicksMaxEndUTAcrossSegments()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100, endUT = 200, semiMajorAxis = 700000
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 50, endUT = 150, semiMajorAxis = 700000
            });

            double endUT = VesselSpawner.ResolveLatestStoredOrbitSegmentEndUT(rec, "Kerbin");

            Assert.Equal(200, endUT);
        }

        [Fact]
        public void ResolveLatestStoredOrbitSegmentEndUT_FiltersByBody()
        {
            var rec = new Recording { RecordingId = "r1" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Mun",
                startUT = 100, endUT = 9999, semiMajorAxis = 250000
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100, endUT = 200, semiMajorAxis = 700000
            });

            double endUT = VesselSpawner.ResolveLatestStoredOrbitSegmentEndUT(rec, "Kerbin");

            Assert.Equal(200, endUT);
        }

        [Fact]
        public void ResolveLatestStoredOrbitSegmentEndUT_SkipsDegenerateSegments()
        {
            // Only degenerate/non-finite SMA is skipped. Valid suborbital and
            // hyperbolic arcs can have |sma| below the body radius.
            var rec = new Recording { RecordingId = "r-degen" };
            // Real segment ending at UT 200.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100, endUT = 200, semiMajorAxis = 700000
            });
            // Degenerate sma-zero "segment" ending at UT 9999 — must NOT be picked
            // as latest.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200, endUT = 9999, semiMajorAxis = 0.0
            });
            // Tiny positive sma — must NOT be picked.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200, endUT = 8888, semiMajorAxis = 0.5
            });
            // Non-finite sma — must NOT be picked.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200, endUT = 7777, semiMajorAxis = double.NaN
            });
            // Negative eccentricity is non-physical and must not be picked.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200, endUT = 6666, semiMajorAxis = 700000, eccentricity = -0.1
            });

            double endUT = VesselSpawner.ResolveLatestStoredOrbitSegmentEndUT(rec, "Kerbin");

            Assert.Equal(200, endUT);
        }

        [Fact]
        public void ResolveLatestStoredOrbitSegmentEndUT_NegativeSmaSegment_IsUsable()
        {
            var rec = new Recording { RecordingId = "r-negative" };
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100, endUT = 200, semiMajorAxis = 700000
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 200, endUT = 8888, semiMajorAxis = -700000
            });

            double endUT = VesselSpawner.ResolveLatestStoredOrbitSegmentEndUT(rec, "Kerbin");

            Assert.Equal(8888, endUT);
        }

        // ---------- TryDeriveTerminalOrbitSeedFromTrajectoryTail (negative cases) ----------
        // The success path requires Unity transforms (body.GetWorldSurfacePosition) so it
        // is covered by the in-game test. These tests cover the short-circuit branches.

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NullRecording_ReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: null, body: body, spawnUT: 0.0,
                out _, out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NullBody_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "r1" };

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: null, spawnUT: 0.0,
                out _, out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NoCoastFrame_LogsAndReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-empty" };

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body, spawnUT: 0.0,
                out _, out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Tail-derived terminal orbit skipped")
                && l.Contains("reason=no-absolute-coast-tail")
                && l.Contains("rec=rec-empty"));
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_StoredSegmentNewer_LogsAndReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-stale-tail" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            // Stored segment ends AFTER the coast tail — defer to existing path.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 110,
                endUT = 200,
                semiMajorAxis = 700000,
                eccentricity = 0.01
            });

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body, spawnUT: 0.0,
                out _, out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Tail-derived terminal orbit skipped")
                && l.Contains("reason=segment-newer-than-tail")
                && l.Contains("tailUT=105.00")
                && l.Contains("segmentEndUT=200.00"));
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_FreshnessEpsilon_BoundaryTailEqualsSegmentEndRejects()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-eps-equal" };
            // Coast-frame UT exactly at segmentEndUT — the check is `<=`, so equal triggers
            // the defer to give the existing seed path priority on boundary ties.
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 195, endUT: 200,
                BuildPoint(ut: 200, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin", startUT = 100, endUT = 200,
                semiMajorAxis = 700000, eccentricity = 0.01
            });

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body, spawnUT: 200.0,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out string declineReason);

            Assert.False(ok);
            Assert.Equal("segment-newer-than-tail", declineReason);
            Assert.Contains(logLines, l =>
                l.Contains("reason=segment-newer-than-tail"));
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_DegenerateStoredSegment_DoesNotBlockTail()
        {
            // M1 from the third Opus review: a stored OrbitSegment with sma <= 0 is
            // skipped by the existing seed picker. The freshness check must skip it
            // too, so a valid post-recording coast frame can still drive tail-derive.
            // Without the filter, this test would land on the segment-newer branch and
            // both the existing path AND tail-derive would decline → empty WARN.
            //
            // Note: this is a freshness-check test, not an end-to-end success test —
            // body.GetWorldSurfacePosition needs Unity transforms which the test stub
            // body does not have. We verify the helper progresses past the segment-
            // newer guard and into the rotation-drift guard (which fires here because
            // spawnUT - tailUT = 95s > 30s limit).
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-degen-segment" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            // Degenerate segment: zero sma, end UT later than the tail. Must NOT
            // shadow the tail.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 110, endUT = 9999,
                semiMajorAxis = 0.0
            });

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body, spawnUT: 200.0,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out string declineReason);

            Assert.False(ok);
            // Critical: NOT segment-newer-than-tail. The degenerate segment was
            // properly skipped, so the helper proceeded to the next guard.
            Assert.NotEqual("segment-newer-than-tail", declineReason);
            Assert.Equal("rotation-drift-out-of-bounds", declineReason);
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_RotationDriftBeyondLimit_LogsAndReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-drift" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            // No stored segment — the segment-newer guard does not fire, so the rotation
            // drift clamp is what stops us. spawnUT − tailUT = 1000 s ≫ 30 s limit.
            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body, spawnUT: 1105.0,
                out _, out _, out _, out _, out _, out _, out _, out _,
                out string declineReason);

            Assert.False(ok);
            Assert.Equal("rotation-drift-out-of-bounds", declineReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Tail-derived terminal orbit skipped")
                && l.Contains("reason=rotation-drift-out-of-bounds")
                && l.Contains("drift=1000.00s")
                && l.Contains("limit=30.00s"));
        }

        [Fact]
        public void TryFindLatestCoastTrajectoryFrame_NonFiniteTailFrame_WalksBackInSection()
        {
            // Reviewer-flagged regression: previously a single corrupt trailing
            // sample threw away the whole section. The walk-back loop must reach
            // the prior valid frame in the same section.
            var rec = new Recording { RecordingId = "rec-corrupt-tail" };
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 120,
                BuildPoint(ut: 110, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0)),
                BuildPoint(ut: 118, lat: 0, lon: 0, alt: 201000, vel: new Vector3(float.NaN, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            Assert.True(found);
            Assert.Equal(110, frame.ut); // Skipped the NaN-velocity tail, used the prior valid frame.
        }

        // ---------- helpers ----------

        private static TrajectoryPoint BuildPoint(
            double ut, double lat, double lon, double alt, Vector3 vel,
            string bodyName = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = Quaternion.identity,
                velocity = vel,
                bodyName = bodyName
            };
        }

        private static TrackSection BuildAbsoluteCoastSection(
            double startUT, double endUT, params TrajectoryPoint[] points)
        {
            return BuildAbsoluteSection(
                env: SegmentEnvironment.ExoBallistic,
                startUT: startUT, endUT: endUT, points);
        }

        private static TrackSection BuildAbsoluteSection(
            SegmentEnvironment env, double startUT, double endUT,
            params TrajectoryPoint[] points)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(points)
            };
        }
    }
}
