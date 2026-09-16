using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 7 (design doc §13, §17.3.2, §18 Phase 7) round-trip tests for the
    /// per-point <c>recordedGroundClearance</c> field added in
    /// <see cref="RecordingStore.CurrentRecordingFormatVersion"/> (v9).
    ///
    /// <para>Three contracts under test:
    /// <list type="number">
    ///   <item>v9 round-trip preserves the clearance value (positive,
    ///       finite double).</item>
    ///   <item>v9 round-trip preserves a NaN clearance for non-surface
    ///       points (legacy sentinel within a v9 file).</item>
    ///   <item>A NaN-clearance point restored from the CURRENT binary
    ///       renders at its recorded altitude, and a finite-clearance point
    ///       beside it is terrain-corrected, so the NaN test in
    ///       <c>ResolvePhase7EffectiveAltitude</c> is what decides. There is
    ///       no pre-v9 read path to cover: older generations are rejected
    ///       outright by <c>RecordingStore.IsRecordingSchemaCompatible</c>
    ///       and <c>TrajectorySidecarBinary.Write</c> always stamps
    ///       <c>CurrentBinaryVersion</c>.</item>
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
        public void V9RoundTrip_SurfacePoint_PreservesFiniteClearance()
        {
            const double t0 = 40000.0;
            const double clearance = 1.5; // 1.5 m above terrain — nominal rover.
            var rec = new Recording
            {
                RecordingId = "phase7-v9-finite",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
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
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
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
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
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
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            Assert.True(double.IsNaN(restored.Points[0].recordedGroundClearance),
                "v9 round-trip must preserve NaN sentinel for non-surface points");
        }

        // ----- v8 legacy load defaults to NaN, preserves positional layout -----

        [Fact]
        public void CurrentBinaryRead_PreservesClearance_AndEveryOtherField()
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
                recordedGroundClearance = 99.0
            };

            var rec = new Recording
            {
                RecordingId = "phase7-current",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            rec.Points.Add(legacyPoint);

            string path = Path.Combine(tempDir, "clearance-current.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            var restoredPoint = restored.Points[0];

            Assert.Equal(legacyPoint.recordedGroundClearance, restoredPoint.recordedGroundClearance);

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
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
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
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.TrackSections);
            var section = restored.TrackSections[0];
            Assert.Equal(3, section.frames.Count);
            Assert.Equal(1.0, section.frames[0].recordedGroundClearance);
            Assert.Equal(1.5, section.frames[1].recordedGroundClearance);
            Assert.Equal(2.0, section.frames[2].recordedGroundClearance);
        }

        // ----- P2-2 review pass: NaN clearance -> renderer fall-through end-to-end -----

        /// <summary>
        /// P2-2: round-trip a binary file through the codec, then route every
        /// restored point through the renderer's
        /// <see cref="ParsekFlight.ResolvePhase7EffectiveAltitude"/> helper.
        /// The NaN-clearance points must come back at their recorded altitude
        /// with the terrain resolver never consulted; the one finite-clearance
        /// point in the same file must be terrain-corrected. Both halves are
        /// needed: with NaN points only, a helper stubbed to
        /// <c>return recordedAltitude</c> would pass, so the NaN test at
        /// <c>ParsekFlight.TailLift.cs</c> would not be the deciding term.
        /// Catches a regression where a future refactor wires the helper to
        /// the wrong altitude (e.g. stores effectiveAltitude back into
        /// <c>point.altitude</c>, or passes <c>recordedGroundClearance</c>
        /// from the wrong field). No pre-v9 file is involved: the writer
        /// always stamps the current binary version, so the NaN sentinel is
        /// what a non-surface point carries inside a current file.
        /// </summary>
        [Fact]
        public void NaNClearance_EveryRestoredPoint_RoutesThroughRendererToRecordedAltitude()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            TerrainCacheBuckets.ResetForTesting();
            // The renderer helper must not call the resolver for the NaN
            // points (silent fall-through), and must call it exactly once for
            // the single finite-clearance control point. Track to catch both
            // a regression that resolves terrain with NaN clearance and a
            // helper that never resolves terrain at all.
            int resolverCalls = 0;
            TerrainCacheBuckets.TerrainResolverForTesting = (name, lat, lon) =>
            {
                resolverCalls++;
                return 999.0; // far from any of the recorded altitudes: a
                              // wrong-path regression would surface here.
            };
            const double controlClearance = 3.25;
            const double controlRecordedAltitude = 41.0;
            var fakeKerbin = TestBodyRegistry.CreateBody(
                "Kerbin", radius: 600000.0, gravParameter: 3.5316e12);

            const double t0 = 50000.0;
            // Multi-point fixture spanning the relevant scenarios: ascent
            // (Atmospheric), coast (ExoBallistic), surface samples.
            var pts = new List<TrajectoryPoint>
            {
                MakePoint(t0,      0.0,      0.0,    72.0, "Kerbin"),
                MakePoint(t0 + 5,  0.001,    0.001, 1500.0, "Kerbin"),
                MakePoint(t0 + 10, 0.005,    0.003, 5000.0, "Kerbin"),
                MakePoint(t0 + 30, 0.020,    0.015, 78000.0, "Kerbin"),
                MakePoint(t0 + 60, 0.050,    0.040, 80000.0, "Kerbin"),
            };
            // The control: a surface point in the SAME file carrying a finite
            // clearance. Without it an unconditional "return recordedAltitude"
            // stub passes every assertion below.
            var controlPoint = MakeSurfaceMobilePoint(
                t0 + 90, 0.060, 0.050, controlRecordedAltitude, controlClearance);

            var rec = new Recording
            {
                RecordingId = "phase7-nan-clearance-end-to-end",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            foreach (var p in pts) rec.Points.Add(p);
            rec.Points.Add(controlPoint);

            string path = Path.Combine(tempDir, "nan-clearance-end-to-end.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Equal(pts.Count + 1, restored.Points.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                var p = restored.Points[i];
                Assert.True(double.IsNaN(p.recordedGroundClearance),
                    $"Point {i} must carry the NaN clearance sentinel");

                double effective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                    fakeKerbin, p.latitude, p.longitude,
                    p.altitude, p.recordedGroundClearance,
                    ReferenceFrame.Absolute);

                Assert.Equal(p.altitude, effective);
            }

            Assert.Equal(0, resolverCalls);

            var restoredControl = restored.Points[restored.Points.Count - 1];
            Assert.Equal(controlClearance, restoredControl.recordedGroundClearance, 6);
            double controlEffective = ParsekFlight.ResolvePhase7EffectiveAltitude(
                fakeKerbin, restoredControl.latitude, restoredControl.longitude,
                restoredControl.altitude, restoredControl.recordedGroundClearance,
                ReferenceFrame.Absolute);

            // terrain (999.0 from the injected resolver) + clearance, NOT the
            // recorded altitude: this is the half the NaN branch is chosen
            // against.
            Assert.Equal(999.0 + controlClearance, controlEffective, 6);
            Assert.NotEqual(controlRecordedAltitude, controlEffective);
            Assert.Equal(1, resolverCalls);
            TerrainCacheBuckets.ResetForTesting();
        }

        private static TrajectoryPoint MakePoint(
            double ut, double latitude, double longitude, double altitude, string bodyName)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 0f, 0f),
                bodyName = bodyName,
                funds = 1000,
                science = 1.0f,
                reputation = 0.25f,
                // Default-NaN per Phase 7 contract — every production-side
                // TrajectoryPoint constructor sets this explicitly.
                recordedGroundClearance = double.NaN,
            };
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
