using System;
using System.Collections.Generic;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9) tests for the structural-event
    /// snapshot helpers on <see cref="FlightRecorder"/>:
    ///
    /// <list type="bullet">
    ///   <item><see cref="FlightRecorder.ApplyStructuralEventFlag"/> — pure flag-set
    ///       helper; xUnit-friendly seam.</item>
    ///   <item><see cref="FlightRecorder.BuildStructuralEventSnapshot"/> — Vessel-driven
    ///       overload; not exercised here (requires Unity runtime — covered by the
    ///       in-game test <c>Pipeline_Smoothing_StructuralEvent_FlagSampleAlignedToBranchPointUT</c>).</item>
    /// </list>
    ///
    /// <para>Pin the bit-0 contract and the idempotence invariant so future bits
    /// stay additive.</para>
    /// </summary>
    [Collection("Sequential")]
    public class FlightRecorderStructuralEventTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlightRecorderStructuralEventTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ----- ApplyStructuralEventFlag — bit set + idempotence -----

        [Fact]
        public void ApplyStructuralEventFlag_OnUnflaggedPoint_SetsBitZero()
        {
            var pt = MakePoint(ut: 100.0);
            Assert.Equal((byte)0, pt.flags);

            TrajectoryPoint flagged = FlightRecorder.ApplyStructuralEventFlag(pt);

            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                flagged.flags);
            Assert.True(((TrajectoryPointFlags)flagged.flags & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot);
        }

        [Fact]
        public void ApplyStructuralEventFlag_OnAlreadyFlaggedPoint_IsIdempotent()
        {
            var pt = MakePoint(ut: 100.0);
            pt.flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot;

            TrajectoryPoint flagged = FlightRecorder.ApplyStructuralEventFlag(pt);

            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                flagged.flags);
        }

        [Fact]
        public void ApplyStructuralEventFlag_PreservesOtherFlagBits()
        {
            // Bit 1 is reserved but should round-trip alongside bit 0 — the
            // helper must use OR not assignment.
            var pt = MakePoint(ut: 100.0);
            pt.flags = 0x02; // hypothetical future bit

            TrajectoryPoint flagged = FlightRecorder.ApplyStructuralEventFlag(pt);

            Assert.Equal((byte)0x03, flagged.flags);
            Assert.True(((TrajectoryPointFlags)flagged.flags & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot);
            // Future bit preserved.
            Assert.Equal(0x02, flagged.flags & 0x02);
        }

        [Fact]
        public void ApplyStructuralEventFlag_PreservesAllOtherFields()
        {
            // Pin every TrajectoryPoint field is preserved across the flag-set.
            // Guards against an accidental field reset via struct re-init.
            var pt = new TrajectoryPoint
            {
                ut = 12345.6,
                latitude = 0.123,
                longitude = -74.567,
                altitude = 1500.5,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.927f),
                velocity = new Vector3(10f, 20f, 30f),
                bodyName = "Kerbin",
                funds = 99999.0,
                science = 42.5f,
                reputation = 0.5f,
                recordedGroundClearance = 1.5,
                flags = 0,
            };

            TrajectoryPoint flagged = FlightRecorder.ApplyStructuralEventFlag(pt);

            Assert.Equal(pt.ut, flagged.ut);
            Assert.Equal(pt.latitude, flagged.latitude);
            Assert.Equal(pt.longitude, flagged.longitude);
            Assert.Equal(pt.altitude, flagged.altitude);
            Assert.Equal(pt.rotation, flagged.rotation);
            Assert.Equal(pt.velocity, flagged.velocity);
            Assert.Equal(pt.bodyName, flagged.bodyName);
            Assert.Equal(pt.funds, flagged.funds);
            Assert.Equal(pt.science, flagged.science);
            Assert.Equal(pt.reputation, flagged.reputation);
            Assert.Equal(pt.recordedGroundClearance, flagged.recordedGroundClearance);
        }

        // ----- Phase 9 binary round-trip via the helper-built point -----

        [Fact]
        public void ApplyStructuralEventFlag_BinaryRoundTripPreservesFlag()
        {
            // End-to-end: helper-built point → binary write → binary read.
            // Pins the contract that the recorder's structural-event
            // snapshot is observable to the AnchorCandidateBuilder consumer
            // after a round-trip through disk (the production playback path).
            var pt = MakePoint(ut: 200.0);
            TrajectoryPoint flagged = FlightRecorder.ApplyStructuralEventFlag(pt);

            var rec = new Recording
            {
                RecordingId = "phase9-helper-roundtrip",
                RecordingFormatVersion = RecordingStore.StructuralEventFlagFormatVersion,
            };
            rec.Points.Add(flagged);

            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "parsek-fr-phase9-roundtrip-" + Guid.NewGuid().ToString("N") + ".prec");
            try
            {
                TrajectorySidecarBinary.Write(tempPath, rec, sidecarEpoch: 1);

                Assert.True(TrajectorySidecarBinary.TryProbe(tempPath, out TrajectorySidecarProbe probe));
                Assert.Equal(RecordingStore.StructuralEventFlagFormatVersion, probe.FormatVersion);

                var restored = new Recording();
                TrajectorySidecarBinary.Read(tempPath, restored, probe);

                Assert.Single(restored.Points);
                Assert.Equal(
                    (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                    restored.Points[0].flags);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }

        // ----- §12 contract: simultaneous calls share physics-frame UT -----

        [Fact]
        public void ApplyStructuralEventFlag_SamePhysicsClock_TwoVessels_ProduceMatchingUT()
        {
            // §12: "Both vessels' snapshots are taken from the same physics state".
            // The helper takes a TrajectoryPoint with .ut already set; the caller
            // (AppendStructuralEventSnapshot) feeds the same eventUT to both
            // vessels' BuildTrajectoryPoint call. Pin that two flagged points
            // built from the same eventUT carry identical UTs.
            var ptA = MakePoint(ut: 12345.6);
            var ptB = MakePoint(ut: 12345.6);

            TrajectoryPoint flaggedA = FlightRecorder.ApplyStructuralEventFlag(ptA);
            TrajectoryPoint flaggedB = FlightRecorder.ApplyStructuralEventFlag(ptB);

            Assert.Equal(flaggedA.ut, flaggedB.ut);
            Assert.Equal(flaggedA.flags, flaggedB.flags);
        }

        private static TrajectoryPoint MakePoint(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 1000.0,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                bodyName = "Kerbin",
                recordedGroundClearance = double.NaN,
                flags = 0,
            };
        }
    }
}
