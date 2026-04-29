using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 7 (design doc §13, §17.3.2, §18 Phase 7) round-trip tests for the
    /// per-point <c>recordedGroundClearance</c> field added in
    /// <see cref="RecordingStore.TerrainGroundClearanceFormatVersion"/> (v9).
    ///
    /// <para>Three contracts under test:
    /// <list type="number">
    ///   <item>v9 round-trip preserves the clearance value (positive,
    ///       finite double).</item>
    ///   <item>v9 round-trip preserves a NaN clearance for non-SurfaceMobile
    ///       points (legacy sentinel within a v9 file).</item>
    ///   <item>A pre-v9 file (e.g. v8) loaded under the v9 reader fills
    ///       <c>recordedGroundClearance = NaN</c> AND keeps every other
    ///       per-point field intact (positional sanity — a desync would
    ///       mangle ut/lat/lon/alt or quaternion data).</item>
    /// </list>
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class TrajectorySidecarBinaryTerrainTests : IDisposable
    {
        private readonly string tempDir;

        public TrajectorySidecarBinaryTerrainTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-trajectory-terrain-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // ----- v9 round-trip (finite clearance) -----

        [Fact]
        public void V9RoundTrip_SurfaceMobilePoint_PreservesFiniteClearance()
        {
            const double t0 = 40000.0;
            const double clearance = 1.5; // 1.5 m above terrain — nominal rover.
            var rec = new Recording
            {
                RecordingId = "phase7-v9-finite",
                RecordingFormatVersion = RecordingStore.TerrainGroundClearanceFormatVersion,
            };
            var pt = new TrajectoryPoint
            {
                ut = t0,
                latitude = -0.1,
                longitude = -74.5,
                altitude = 75.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(2f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 1000,
                science = 1.5f,
                reputation = 0.25f,
                recordedGroundClearance = clearance
            };
            rec.Points.Add(pt);

            string path = Path.Combine(tempDir, "v9-finite.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.TerrainGroundClearanceFormatVersion, probe.FormatVersion);
            Assert.True(probe.Supported);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            Assert.Equal(clearance, restored.Points[0].recordedGroundClearance);
            // Every other field must round-trip too — guards against a
            // positional desync regression.
            Assert.Equal(t0, restored.Points[0].ut);
            Assert.Equal(75.0, restored.Points[0].altitude);
            Assert.Equal("Kerbin", restored.Points[0].bodyName);
            Assert.Equal(1000.0, restored.Points[0].funds);
        }

        // ----- v9 with NaN sentinel point -----

        [Fact]
        public void V9RoundTrip_NaNClearance_PreservesNaN()
        {
            const double t0 = 41000.0;
            var rec = new Recording
            {
                RecordingId = "phase7-v9-nan",
                RecordingFormatVersion = RecordingStore.TerrainGroundClearanceFormatVersion,
            };
            var pt = new TrajectoryPoint
            {
                ut = t0,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 80000.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 200f, 0f),
                bodyName = "Kerbin",
                recordedGroundClearance = double.NaN
            };
            rec.Points.Add(pt);

            string path = Path.Combine(tempDir, "v9-nan.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.TerrainGroundClearanceFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            Assert.True(double.IsNaN(restored.Points[0].recordedGroundClearance),
                "v9 round-trip must preserve NaN sentinel for non-SurfaceMobile points");
        }

        // ----- v8 legacy load defaults to NaN, preserves positional layout -----

        [Fact]
        public void V8LegacyRead_DefaultsClearanceToNaN_AndPreservesEveryOtherField()
        {
            const double t0 = 42000.0;
            var legacyPoint = new TrajectoryPoint
            {
                ut = t0 + 5,
                latitude = 0.123,
                longitude = -74.456,
                altitude = 80000,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.927f),
                velocity = new Vector3(10f, 20f, 30f),
                bodyName = "Kerbin",
                funds = 12345,
                science = 4.5f,
                reputation = 0.25f,
                // The v8 writer ignores this — but the in-memory value would
                // otherwise round-trip via the v9 reader if the gate were broken.
                recordedGroundClearance = 99.0
            };

            var rec = new Recording
            {
                RecordingId = "phase7-v8-legacy",
                // Pin the writer to v8 (BoundarySeamFlagFormatVersion). The
                // version-selection ladder will pick v8, producing a v8 binary
                // file — exactly the layout a pre-Phase-7 save on disk has.
                RecordingFormatVersion = RecordingStore.BoundarySeamFlagFormatVersion
            };
            rec.Points.Add(legacyPoint);

            string path = Path.Combine(tempDir, "v8-legacy.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.BoundarySeamFlagFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            var restoredPoint = restored.Points[0];

            // Phase 7 contract: legacy v8 readers default-NaN.
            Assert.True(double.IsNaN(restoredPoint.recordedGroundClearance),
                "v8 → v9 reader must default recordedGroundClearance to NaN");

            // Positional sanity: every other field round-trips intact. A
            // desync would mangle these.
            Assert.Equal(legacyPoint.ut, restoredPoint.ut);
            Assert.Equal(legacyPoint.latitude, restoredPoint.latitude);
            Assert.Equal(legacyPoint.longitude, restoredPoint.longitude);
            Assert.Equal(legacyPoint.altitude, restoredPoint.altitude);
            Assert.Equal(legacyPoint.rotation.x, restoredPoint.rotation.x);
            Assert.Equal(legacyPoint.rotation.y, restoredPoint.rotation.y);
            Assert.Equal(legacyPoint.rotation.z, restoredPoint.rotation.z);
            Assert.Equal(legacyPoint.rotation.w, restoredPoint.rotation.w);
            Assert.Equal(legacyPoint.bodyName, restoredPoint.bodyName);
            Assert.Equal(legacyPoint.funds, restoredPoint.funds);
            Assert.Equal(legacyPoint.science, restoredPoint.science);
            Assert.Equal(legacyPoint.reputation, restoredPoint.reputation);
        }

        // ----- v9 multi-section round-trip (ensure section frames preserve clearance too) -----

        [Fact]
        public void V9RoundTrip_SectionFrames_PreserveClearancePerFrame()
        {
            const double t0 = 43000.0;
            var rec = new Recording
            {
                RecordingId = "phase7-v9-section",
                RecordingFormatVersion = RecordingStore.TerrainGroundClearanceFormatVersion,
            };
            var p0 = MakeSurfaceMobilePoint(t0, -0.1, -74.5, 76.0, clearance: 1.0);
            var p1 = MakeSurfaceMobilePoint(t0 + 1, -0.1001, -74.5001, 76.5, clearance: 1.5);
            var p2 = MakeSurfaceMobilePoint(t0 + 2, -0.1002, -74.5002, 77.0, clearance: 2.0);
            rec.Points.Add(p0);
            rec.Points.Add(p1);
            rec.Points.Add(p2);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0,
                endUT = t0 + 2,
                sampleRateHz = 1f,
                frames = new List<TrajectoryPoint> { p0, p1, p2 },
                checkpoints = new List<OrbitSegment>(),
                minAltitude = 76f,
                maxAltitude = 77f,
            });

            string path = Path.Combine(tempDir, "v9-section.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.TerrainGroundClearanceFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.TrackSections);
            var section = restored.TrackSections[0];
            Assert.Equal(3, section.frames.Count);
            Assert.Equal(1.0, section.frames[0].recordedGroundClearance);
            Assert.Equal(1.5, section.frames[1].recordedGroundClearance);
            Assert.Equal(2.0, section.frames[2].recordedGroundClearance);
        }

        private static TrajectoryPoint MakeSurfaceMobilePoint(
            double ut, double lat, double lon, double alt, double clearance)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(2f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 1000,
                science = 1.0f,
                reputation = 0.25f,
                recordedGroundClearance = clearance
            };
        }
    }
}
