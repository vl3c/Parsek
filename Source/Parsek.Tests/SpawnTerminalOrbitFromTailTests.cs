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
        public void TryFindLatestCoastTrajectoryFrame_NonFiniteVelocity_SkipsFrame()
        {
            var rec = new Recording { RecordingId = "r1" };
            // First section: valid coast frame.
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 100, endUT: 110,
                BuildPoint(ut: 105, lat: 0, lon: 0, alt: 200000, vel: new Vector3(1500, 0, 0))));
            // Last section: corrupted frame (NaN velocity component).
            rec.TrackSections.Add(BuildAbsoluteCoastSection(
                startUT: 110, endUT: 120,
                BuildPoint(ut: 118, lat: 0, lon: 0, alt: 201000,
                    vel: new Vector3(float.NaN, 0, 0))));

            bool found = VesselSpawner.TryFindLatestCoastTrajectoryFrame(
                rec, "Kerbin", out TrajectoryPoint frame);

            // The latest section's last frame is corrupt and is skipped — the
            // current rule rejects the section's tail as a whole rather than
            // reaching backward inside the same section's frames list.
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

        // ---------- TryDeriveTerminalOrbitSeedFromTrajectoryTail (negative cases) ----------
        // The success path requires Unity transforms (body.GetWorldSurfacePosition) so it
        // is covered by the in-game test. These tests cover the short-circuit branches.

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NullRecording_ReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: null, body: body,
                out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NullBody_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "r1" };

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: null,
                out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TryDeriveTerminalOrbitSeedFromTrajectoryTail_NoCoastFrame_LogsAndReturnsFalse()
        {
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var rec = new Recording { RecordingId = "rec-empty" };

            bool ok = VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail(
                rec: rec, body: body,
                out _, out _, out _, out _, out _, out _, out _, out _);

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
                rec: rec, body: body,
                out _, out _, out _, out _, out _, out _, out _, out _);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Tail-derived terminal orbit skipped")
                && l.Contains("reason=segment-newer-than-tail")
                && l.Contains("tailUT=105.00")
                && l.Contains("segmentEndUT=200.00"));
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
