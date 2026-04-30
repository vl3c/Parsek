using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9) round-trip tests for the
    /// per-point <c>flags</c> byte added in
    /// <see cref="RecordingStore.StructuralEventFlagFormatVersion"/> (v10).
    ///
    /// <para>Three contracts under test:
    /// <list type="number">
    ///   <item>v10 round-trip preserves the StructuralEventSnapshot flag for
    ///       flagged points and 0 for unflagged points.</item>
    ///   <item>v9 round-trip (legacy: writer pinned to v9, reader sees the v9
    ///       byte stream) defaults <c>flags = 0</c> on read AND keeps every
    ///       other per-point field intact (positional sanity — a desync would
    ///       mangle ut/lat/lon/alt or quaternion data).</item>
    ///   <item>v10 with mixed flagged + non-flagged points round-trips each
    ///       point's flag value independently.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class TrajectorySidecarBinaryStructuralEventTests : IDisposable
    {
        private readonly string tempDir;

        public TrajectorySidecarBinaryStructuralEventTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-trajectory-flag-tests-" + Guid.NewGuid().ToString("N"));
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

        // ----- v10 round-trip (flagged point) -----

        [Fact]
        public void V10RoundTrip_StructuralEventFlagSet_PreservesFlag()
        {
            const double t0 = 50000.0;
            var rec = new Recording
            {
                RecordingId = "phase9-v10-flagged",
                RecordingFormatVersion = RecordingStore.StructuralEventFlagFormatVersion,
            };
            var pt = new TrajectoryPoint
            {
                ut = t0,
                latitude = 0.1,
                longitude = -74.5,
                altitude = 80.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(2f, 0f, 0f),
                bodyName = "Kerbin",
                funds = 1000,
                science = 1.5f,
                reputation = 0.25f,
                recordedGroundClearance = double.NaN,
                flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot,
            };
            rec.Points.Add(pt);

            string path = Path.Combine(tempDir, "v10-flagged.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.StructuralEventFlagFormatVersion, probe.FormatVersion);
            Assert.True(probe.Supported);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                restored.Points[0].flags);
            // Every other field must round-trip too — guards against a
            // positional desync regression.
            Assert.Equal(t0, restored.Points[0].ut);
            Assert.Equal(80.0, restored.Points[0].altitude);
            Assert.Equal("Kerbin", restored.Points[0].bodyName);
            Assert.Equal(1000.0, restored.Points[0].funds);
            Assert.True(double.IsNaN(restored.Points[0].recordedGroundClearance));
        }

        // ----- v10 unflagged point round-trips with flags=0 -----

        [Fact]
        public void V10RoundTrip_NoFlag_PreservesZero()
        {
            const double t0 = 50500.0;
            var rec = new Recording
            {
                RecordingId = "phase9-v10-unflagged",
                RecordingFormatVersion = RecordingStore.StructuralEventFlagFormatVersion,
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
                recordedGroundClearance = double.NaN,
                flags = 0,
            };
            rec.Points.Add(pt);

            string path = Path.Combine(tempDir, "v10-unflagged.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.StructuralEventFlagFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            Assert.Equal((byte)0, restored.Points[0].flags);
        }

        // ----- v9 legacy load defaults flags=0, preserves positional layout -----

        [Fact]
        public void V9LegacyRead_DefaultsFlagsToZero_AndPreservesEveryOtherField()
        {
            const double t0 = 51000.0;
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
                recordedGroundClearance = 1.5,
                // The v9 writer ignores this — but the in-memory value would
                // otherwise round-trip via the v10 reader if the gate were broken.
                flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot,
            };

            var rec = new Recording
            {
                RecordingId = "phase9-v9-legacy",
                // Pin the writer to v9 (TerrainGroundClearanceFormatVersion). The
                // version-selection ladder picks v9 — exactly the layout a Phase 7
                // save on disk has.
                RecordingFormatVersion = RecordingStore.TerrainGroundClearanceFormatVersion,
            };
            rec.Points.Add(legacyPoint);

            string path = Path.Combine(tempDir, "v9-legacy.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.TerrainGroundClearanceFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            Assert.Single(restored.Points);
            var restoredPoint = restored.Points[0];

            // Phase 9 contract: legacy v9 readers default flags=0.
            Assert.Equal((byte)0, restoredPoint.flags);

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
            // v9 still carries recordedGroundClearance — verify it round-trips.
            Assert.Equal(1.5, restoredPoint.recordedGroundClearance);
        }

        // ----- v10 mixed flagged + non-flagged points -----

        [Fact]
        public void V10RoundTrip_MixedFlaggedAndUnflaggedPoints_RoundTripsEachIndependently()
        {
            const double t0 = 52000.0;
            var rec = new Recording
            {
                RecordingId = "phase9-v10-mixed",
                RecordingFormatVersion = RecordingStore.StructuralEventFlagFormatVersion,
            };
            var p0 = MakePoint(t0, flags: 0);                                                     // unflagged tick
            var p1 = MakePoint(t0 + 1, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot); // dock event
            var p2 = MakePoint(t0 + 2, flags: 0);                                                  // unflagged tick
            var p3 = MakePoint(t0 + 3, flags: (byte)TrajectoryPointFlags.StructuralEventSnapshot); // joint break
            rec.Points.Add(p0);
            rec.Points.Add(p1);
            rec.Points.Add(p2);
            rec.Points.Add(p3);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0,
                endUT = t0 + 3,
                sampleRateHz = 1f,
                frames = new List<TrajectoryPoint> { p0, p1, p2, p3 },
                checkpoints = new List<OrbitSegment>(),
            });

            string path = Path.Combine(tempDir, "v10-mixed.prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch: 1);

            Assert.True(TrajectorySidecarBinary.TryProbe(path, out TrajectorySidecarProbe probe));
            Assert.Equal(RecordingStore.StructuralEventFlagFormatVersion, probe.FormatVersion);

            var restored = new Recording();
            TrajectorySidecarBinary.Read(path, restored, probe);

            // Section-authoritative round-trip rebuilds the flat Points list
            // from the section frames; assert against the section's frames so
            // the test exercises the same code path used by every loaded
            // recording at runtime.
            Assert.Single(restored.TrackSections);
            var section = restored.TrackSections[0];
            Assert.Equal(4, section.frames.Count);
            Assert.Equal((byte)0, section.frames[0].flags);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                section.frames[1].flags);
            Assert.Equal((byte)0, section.frames[2].flags);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                section.frames[3].flags);
        }

        private static TrajectoryPoint MakePoint(double ut, byte flags)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 80000.0,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(0f, 200f, 0f),
                bodyName = "Kerbin",
                funds = 1000,
                science = 1.0f,
                reputation = 0.25f,
                recordedGroundClearance = double.NaN,
                flags = flags,
            };
        }
    }
}
