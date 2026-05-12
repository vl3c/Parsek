using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
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
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            rec.Points.Add(flagged);

            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "parsek-fr-phase9-roundtrip-" + Guid.NewGuid().ToString("N") + ".prec");
            try
            {
                TrajectorySidecarBinary.Write(tempPath, rec, sidecarEpoch: 1);

                Assert.True(TrajectorySidecarBinary.TryProbe(tempPath, out TrajectorySidecarProbe probe));
                Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

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

        // ----- ShouldEmitStructuralEventSnapshot — schema-gate fall-through -----

        [Fact]
        public void ShouldEmitStructuralEventSnapshot_PostResetV0_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldEmitStructuralEventSnapshot(
                RecordingStore.CurrentRecordingFormatVersion));
        }

        [Fact]
        public void ShouldEmitStructuralEventSnapshot_V10AndAbove_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldEmitStructuralEventSnapshot(
                RecordingStore.CurrentRecordingFormatVersion));
            Assert.True(FlightRecorder.ShouldEmitStructuralEventSnapshot(
                RecordingStore.CurrentRecordingFormatVersion + 1));
        }

        [Fact]
        public void ShouldEmitStructuralEventSnapshot_IgnoresPreResetVersionNumbers()
        {
            // Pre-reset version numbers are no longer used as behavior gates; old
            // recordings are rejected by the schema loader instead.
            for (int v = -1; v <= 13; v++)
            {
                Assert.True(FlightRecorder.ShouldEmitStructuralEventSnapshot(v));
            }
        }

        [Fact]
        public void AppendStructuralEventSnapshot_CurrentRecordingWithNoInvolvedVessels_NoAppend()
        {
            var recorder = new FlightRecorder();

            const string recId = "phase9-current-no-involved-test";
            var rec = new Recording
            {
                RecordingId = recId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
            var tree = new RecordingTree { Id = "tree-phase9-current-no-involved" };
            tree.Recordings[recId] = rec;
            tree.ActiveRecordingId = recId;
            recorder.ActiveTree = tree;

            // Flip the recording flag without going through StartRecording (which
            // would touch live KSP state); the helper guards on IsRecording first
            // and we want to exercise the schema gate next.
            recorder.IsRecording = true;

            int beforeRecordingPoints = recorder.Recording.Count;
            int beforePoints = rec.Points.Count;
            logLines.Clear();

            recorder.AppendStructuralEventSnapshot(
                eventUT: 12345.6,
                involved: new List<Vessel>(),
                eventType: "Dock");

            Assert.Equal(beforeRecordingPoints, recorder.Recording.Count);
            Assert.Equal(beforePoints, rec.Points.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("recording format"));
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

        // ----- Phase 9 follow-up: coalescer child-seed parity -----

        [Fact]
        public void CreateBreakupChildRecording_SeedAtStructuralEventUT_FlagsChildSeed()
        {
            const double eventUT = 16884.922537602102;
            var seed = MakePoint(eventUT);

            Recording child = CreateBreakupChildRecordingWithSeed(eventUT, seed);

            Assert.Single(child.Points);
            Assert.Equal(eventUT, child.Points[0].ut);
            Assert.Equal(
                (byte)TrajectoryPointFlags.StructuralEventSnapshot,
                child.Points[0].flags);
        }

        [Fact]
        public void CreateBreakupChildRecording_SeedOffsetFromStructuralEventUT_DoesNotFlagChildSeed()
        {
            const double eventUT = 16884.922537602102;
            var seed = MakePoint(eventUT + 0.02);

            Recording child = CreateBreakupChildRecordingWithSeed(eventUT, seed);

            Assert.Single(child.Points);
            Assert.Equal(seed.ut, child.Points[0].ut);
            Assert.Equal((byte)0, child.Points[0].flags);
        }

        [Fact]
        public void CreateBreakupChildRecording_FlaggedChildSeed_BinaryRoundTripPreservesFlag()
        {
            const double eventUT = 16884.922537602102;
            Recording child = CreateBreakupChildRecordingWithSeed(eventUT, MakePoint(eventUT));

            string tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "parsek-fr-phase9-child-seed-roundtrip-" + Guid.NewGuid().ToString("N") + ".prec");
            try
            {
                TrajectorySidecarBinary.Write(tempPath, child, sidecarEpoch: 1);

                Assert.True(TrajectorySidecarBinary.TryProbe(tempPath, out TrajectorySidecarProbe probe));
                Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);

                var restored = new Recording();
                TrajectorySidecarBinary.Read(tempPath, restored, probe);

                Assert.Single(restored.Points);
                Assert.Equal(eventUT, restored.Points[0].ut);
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

        [Fact]
        public void AnchorCandidateBuilder_FindsCoalescerChildSeedAtStructuralEventUT()
        {
            const double eventUT = 16884.922537602102;
            Recording child = CreateBreakupChildRecordingWithSeed(eventUT, MakePoint(eventUT));

            int childSeedIndex = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(
                child.Points,
                eventUT,
                tolerance: 0.1);

            Assert.Equal(0, childSeedIndex);
            Assert.Equal(eventUT, child.Points[childSeedIndex].ut);
        }

        private static Recording CreateBreakupChildRecordingWithSeed(double eventUT, TrajectoryPoint seed)
        {
            var tree = new RecordingTree
            {
                Id = "tree-phase9-child-seed-" + Guid.NewGuid().ToString("N"),
                TreeName = "Phase 9 Child Seed Test",
            };
            var bp = new BranchPoint
            {
                Id = "bp-phase9-child-seed-" + Guid.NewGuid().ToString("N"),
                UT = eventUT,
                Type = BranchPointType.JointBreak,
            };

            return ParsekFlight.CreateBreakupChildRecording(
                tree,
                bp,
                pid: 42,
                vessel: null,
                isDebris: true,
                fallbackName: "Debris",
                fallbackSnapshot: null,
                fallbackTrajectoryPoint: seed,
                parentGeneration: 0);
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

        // ----- BackwardStepWorldPositionByVelocity / IsBackwardExtrapolationApplicable -----
        //
        // Pure-math seam introduced to compensate for the KSP joint-break /
        // structural-event phase offset: at the moment KSP fires the event,
        // v.latitude/longitude/altitude already reflect end-of-tick PhysX
        // state, but Planetarium.GetUniversalTime() (the recorded eventUT)
        // is still start-of-tick. The recorded position is therefore one
        // Time.fixedDeltaTime ahead of its stamp, producing a visible 2-3 m
        // forward slide on the first playback frame after staging events.
        // The wrapper that handles body lat/lon/alt conversion is Unity-only
        // (uses CelestialBody helpers) — these tests pin the velocity-step
        // math and guard semantics in isolation.

        [Fact]
        public void BackwardStep_NormalCase_SubtractsVelocityTimesDt()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);
            Vector3 velocity = new Vector3(100.0f, 0.0f, 0.0f);
            float dt = 0.02f;

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(world, velocity, dt);

            Assert.Equal(1000.0 - 100.0 * 0.02, corrected.x, 6);
            Assert.Equal(2000.0, corrected.y, 6);
            Assert.Equal(3000.0, corrected.z, 6);
        }

        [Fact]
        public void BackwardStep_ZeroVelocity_LeavesWorldUnchanged()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(
                world, Vector3.zero, 0.02f);

            Assert.Equal(world.x, corrected.x, 9);
            Assert.Equal(world.y, corrected.y, 9);
            Assert.Equal(world.z, corrected.z, 9);
        }

        [Fact]
        public void BackwardStep_ZeroDt_LeavesWorldUnchanged()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);
            Vector3 velocity = new Vector3(168.0f, 0.0f, 0.0f);

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(
                world, velocity, 0.0f);

            Assert.Equal(world.x, corrected.x, 9);
            Assert.Equal(world.y, corrected.y, 9);
            Assert.Equal(world.z, corrected.z, 9);
        }

        [Fact]
        public void BackwardStep_NegativeDt_LeavesWorldUnchanged()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);
            Vector3 velocity = new Vector3(168.0f, 0.0f, 0.0f);

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(
                world, velocity, -0.02f);

            Assert.Equal(world.x, corrected.x, 9);
            Assert.Equal(world.y, corrected.y, 9);
            Assert.Equal(world.z, corrected.z, 9);
        }

        [Fact]
        public void BackwardStep_NanVelocity_LeavesWorldUnchanged()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);
            Vector3 velocity = new Vector3(float.NaN, 0.0f, 0.0f);

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(
                world, velocity, 0.02f);

            Assert.Equal(world.x, corrected.x, 9);
            Assert.Equal(world.y, corrected.y, 9);
            Assert.Equal(world.z, corrected.z, 9);
        }

        [Fact]
        public void BackwardStep_InfiniteVelocity_LeavesWorldUnchanged()
        {
            Vector3d world = new Vector3d(1000.0, 2000.0, 3000.0);
            Vector3 velocity = new Vector3(0.0f, float.PositiveInfinity, 0.0f);

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(
                world, velocity, 0.02f);

            Assert.Equal(world.x, corrected.x, 9);
            Assert.Equal(world.y, corrected.y, 9);
            Assert.Equal(world.z, corrected.z, 9);
        }

        [Fact]
        public void BackwardStep_TypicalStagingScenario_ProducesExpectedOffset()
        {
            // Empirical numbers from the bug investigation: ~168 m/s
            // forward velocity at decoupler-back staging, dt = 0.02 s.
            // Expected offset magnitude is ~3.36 m along the velocity axis,
            // matching the observed forward slide on the first lerp interval.
            Vector3d world = new Vector3d(0.0, 0.0, 0.0);
            Vector3 velocity = new Vector3(168.0f, 0.0f, 0.0f);
            float dt = 0.02f;

            Vector3d corrected = FlightRecorder.BackwardStepWorldPositionByVelocity(world, velocity, dt);

            Assert.Equal(-3.36, corrected.x, 6);
            Assert.Equal(0.0, corrected.y, 6);
            Assert.Equal(0.0, corrected.z, 6);
        }

        [Fact]
        public void IsApplicable_NormalCase_True()
        {
            Assert.True(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(100.0f, 0.0f, 0.0f)));
        }

        [Fact]
        public void IsApplicable_ZeroDt_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.0f, new Vector3(100.0f, 0.0f, 0.0f)));
        }

        [Fact]
        public void IsApplicable_NegativeDt_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                -0.02f, new Vector3(100.0f, 0.0f, 0.0f)));
        }

        [Fact]
        public void IsApplicable_NanDt_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                float.NaN, new Vector3(100.0f, 0.0f, 0.0f)));
        }

        [Fact]
        public void IsApplicable_InfiniteDt_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                float.PositiveInfinity, new Vector3(100.0f, 0.0f, 0.0f)));
        }

        [Fact]
        public void IsApplicable_NanVelocityComponent_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(0.0f, float.NaN, 0.0f)));
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(float.NaN, 0.0f, 0.0f)));
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(0.0f, 0.0f, float.NaN)));
        }

        [Fact]
        public void IsApplicable_InfiniteVelocityComponent_False()
        {
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(float.NegativeInfinity, 0.0f, 0.0f)));
            Assert.False(FlightRecorder.IsBackwardExtrapolationApplicable(
                0.02f, new Vector3(0.0f, float.PositiveInfinity, 0.0f)));
        }

        [Fact]
        public void IsApplicable_ZeroVelocity_True()
        {
            // Zero velocity is fine — produces an identity transform but
            // the guard still considers it "applicable".
            Assert.True(FlightRecorder.IsBackwardExtrapolationApplicable(0.02f, Vector3.zero));
        }
    }
}
