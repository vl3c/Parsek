using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Slice (i) of the per-instance overlap map render
    /// (docs/dev/plans/maprender-overlap-per-instance.md): the PURE pieces of the
    /// per-(recording, cycle) map-presence lifecycle in <see cref="GhostMapPresence"/>.
    /// Verifies the overlap gate predicate, the cycle-set equivalence with the flight
    /// engine's <see cref="GhostPlaybackLogic.GetActiveCycles"/>, and the create/destroy
    /// decision helper (cap + throttle + expiry). All helpers here are Unity-free; the
    /// live ProtoVessel create/destroy is covered by the in-game test.
    /// </summary>
    [Collection("Sequential")]
    public class OverlapPerInstanceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OverlapPerInstanceTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ClearCommittedInternal();
            RecordingStore.CommittedTrees.Clear();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f,
                mapRenderDirectorDrive = true
            };
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ClearCommittedInternal();
            RecordingStore.CommittedTrees.Clear();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        //  Recording factory
        // -----------------------------------------------------------------

        /// <summary>
        /// A looping Kerbin recording. period &lt; (endUT-startUT) makes it an OVERLAP loop.
        /// LoopTimeUnit.Sec keeps the interval explicit (no auto-launch-queue cadence).
        /// </summary>
        private static Recording MakeLoopRec(
            double startUT, double endUT, double loopInterval,
            bool loopPlayback = true,
            LoopTimeUnit unit = LoopTimeUnit.Sec,
            string id = "rec-overlap")
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "OverlapTest",
                PlaybackEnabled = true,
                LoopPlayback = loopPlayback,
                LoopIntervalSeconds = loopInterval,
                LoopTimeUnit = unit,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            return rec;
        }

        // =================================================================
        //  (b) Overlap gate predicate
        // =================================================================

        [Fact]
        public void IsOverlapLoop_PeriodLessThanDuration_True()
        {
            // duration 100, period 30 -> overlap.
            Assert.True(GhostPlaybackLogic.IsOverlapLoop(30.0, 100.0));
        }

        [Fact]
        public void IsOverlapLoop_PeriodGreaterThanDuration_False()
        {
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(150.0, 100.0));
        }

        [Fact]
        public void IsOverlapLoop_PeriodEqualsDuration_False()
        {
            // Boundary: equal is NOT overlap (strict <).
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(100.0, 100.0));
        }

        [Fact]
        public void IsOverlapRecording_LoopingShortPeriod_True()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            Assert.True(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed));
        }

        [Fact]
        public void IsOverlapRecording_NotLooping_False()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30, loopPlayback: false) };
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed));
        }

        [Fact]
        public void IsOverlapRecording_LoopingLongPeriod_False()
        {
            // period (500) > duration (200): looping but NOT overlapping (single-ghost path).
            var committed = new List<Recording> { MakeLoopRec(100, 300, 500) };
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed));
        }

        [Fact]
        public void IsOverlapRecording_Null_False()
        {
            Assert.False(GhostMapPresence.IsOverlapRecording(null, 0, new List<Recording>()));
        }

        [Fact]
        public void ShouldDriveOverlapPerInstance_GateOff_False()
        {
            ParsekSettings.CurrentOverrideForTesting.mapRenderDirectorDrive = false;
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            Assert.False(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed));
            // The recording itself IS an overlap loop; only the gate is off.
            Assert.True(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed));
        }

        [Fact]
        public void ShouldDriveOverlapPerInstance_GateOn_OverlapRecording_True()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            Assert.True(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed));
        }

        [Fact]
        public void ShouldDriveOverlapPerInstance_GateOn_NonOverlap_False()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30, loopPlayback: false) };
            Assert.False(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed));
        }

        // =================================================================
        //  (a) Cycle-set equivalence with GetActiveCycles (regression lock)
        // =================================================================

        [Fact]
        public void ResolveOverlapSchedule_FeedsGetActiveCycles_MatchesDirectComputation()
        {
            // The map per-instance cycle set MUST equal the flight engine's GetActiveCycles set for
            // the same inputs. ResolveOverlapSchedule resolves (scheduleStart, duration, effective
            // cadence); feeding those into GetActiveCycles must reproduce a direct GetActiveCycles
            // call on the same raw schedule + effective cadence.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            bool ok = GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out double cycleDuration);
            Assert.True(ok);

            // duration = 200, period 30 < 200 -> overlap. Effective cadence raised so
            // ceil(duration/cadence) <= cap = 20; with 200/30 = 6.67 cycles, cadence stays 30.
            Assert.Equal(200.0, duration, 3);
            Assert.Equal(100.0, scheduleStartUT, 3); // no auto-launch-queue (Sec unit) -> own loop start
            Assert.Equal(100.0, playbackStartUT, 3);

            double currentUT = 250.0;
            GhostMapPresence.OverlapCyclesForTesting(
                committed[0], 0, committed, currentUT,
                out long mapFirst, out long mapLast);

            // Direct GetActiveCycles on the resolved schedule.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long engineFirst, out long engineLast);

            Assert.Equal(engineFirst, mapFirst);
            Assert.Equal(engineLast, mapLast);
        }

        [Theory]
        [InlineData(130.0)] // 1 cycle live (cycle 1 just launched; cycle 0 still playing)
        [InlineData(250.0)] // several overlapping
        [InlineData(400.0)] // deep into overlap, cap may clamp
        public void OverlapCyclesForTesting_MatchesGetActiveCycles(double currentUT)
        {
            // 5s period over a 200s duration -> up to 40 cycles, capped at 20: exercises the cap clamp.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 5) };
            GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed,
                out _, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out _);

            GhostMapPresence.OverlapCyclesForTesting(
                committed[0], 0, committed, currentUT,
                out long mapFirst, out long mapLast);

            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long engineFirst, out long engineLast);

            Assert.Equal(engineFirst, mapFirst);
            Assert.Equal(engineLast, mapLast);
            // Cap invariant: live cycle count never exceeds the per-recording cap.
            Assert.True(mapLast - mapFirst + 1 <= GhostPlayback.MaxOverlapGhostsPerRecording);
        }

        // =================================================================
        //  (c) Create/destroy decision helper
        // =================================================================

        [Fact]
        public void DecideOverlapInstanceChanges_AllMissing_CreatesWithinBudget()
        {
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[0],
                firstCycle: 3, lastCycle: 6, spawnBudget: 2,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toDestroy);
            // Budget 2 -> only 2 created this frame, newest-first (6 then 5).
            Assert.Equal(2, toCreate.Count);
            Assert.Contains(6L, toCreate);
            Assert.Contains(5L, toCreate);
        }

        [Fact]
        public void DecideOverlapInstanceChanges_NewestFirstUnderThrottle()
        {
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[0],
                firstCycle: 0, lastCycle: 9, spawnBudget: 1,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toDestroy);
            Assert.Single(toCreate);
            Assert.Equal(9L, toCreate[0]); // newest cycle wins the single budget slot
        }

        [Fact]
        public void DecideOverlapInstanceChanges_ExpiredBelowFirst_Destroyed()
        {
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[] { 0, 1, 2, 3 },
                firstCycle: 2, lastCycle: 3, spawnBudget: 8,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toCreate); // 2 and 3 already present
            Assert.Equal(2, toDestroy.Count);
            Assert.Contains(0L, toDestroy);
            Assert.Contains(1L, toDestroy);
        }

        [Fact]
        public void DecideOverlapInstanceChanges_AheadOfNewest_Destroyed()
        {
            // A stale instance somehow ahead of the newest live cycle (e.g. cycle window moved back
            // after a rewind): it is out of [first,last] and must be reaped.
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[] { 5 },
                firstCycle: 0, lastCycle: 3, spawnBudget: 8,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Contains(5L, toDestroy);
        }

        [Fact]
        public void DecideOverlapInstanceChanges_PartialPresent_CreatesOnlyMissing()
        {
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[] { 4, 6 },
                firstCycle: 4, lastCycle: 7, spawnBudget: 8,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toDestroy);
            Assert.Equal(2, toCreate.Count);
            Assert.Contains(7L, toCreate);
            Assert.Contains(5L, toCreate);
            Assert.DoesNotContain(4L, toCreate);
            Assert.DoesNotContain(6L, toCreate);
        }

        [Fact]
        public void DecideOverlapInstanceChanges_ZeroBudget_NoCreates()
        {
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[] { 0 },
                firstCycle: 0, lastCycle: 5, spawnBudget: 0,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toCreate);
            Assert.Empty(toDestroy); // cycle 0 is still in window
        }

        [Fact]
        public void DecideOverlapInstanceChanges_NoLiveWindow_OnlyDestroys()
        {
            // currentUT before schedule start: GetActiveCycles returns first=0,last=0 but the caller
            // gates that separately; here simulate lastCycle < firstCycle (degenerate empty window).
            GhostMapPresence.DecideOverlapInstanceChanges(
                existingCycles: new long[] { 2, 3 },
                firstCycle: 5, lastCycle: 4, spawnBudget: 8,
                out List<long> toCreate, out List<long> toDestroy);

            Assert.Empty(toCreate);
            Assert.Equal(2, toDestroy.Count);
        }

        [Fact]
        public void GetOverlapInstanceCount_EmptyByDefault()
        {
            Assert.Equal(0, GhostMapPresence.GetOverlapInstanceCount(0));
            Assert.Equal(0u, GhostMapPresence.GetNewestOverlapInstancePidForRecording(0));
        }
    }
}
