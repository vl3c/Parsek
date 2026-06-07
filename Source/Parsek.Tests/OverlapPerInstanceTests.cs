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
            // CurrentUTNow defaults to Planetarium.GetUniversalTime() (Unity, NRE in xUnit). Override
            // it after ResetForTesting (which restores GetCurrentUTSafe) so the gate-decision log path
            // resolves a UT without Unity.
            GhostMapPresence.CurrentUTNow = () => 5130.0;
            RecordingStore.ClearCommittedInternal();
            RecordingStore.CommittedTrees.Clear();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f
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

        /// <summary>The empty loop-unit set (source-a-only path; no Mission loops).</summary>
        private static GhostPlaybackLogic.LoopUnitSet NoUnits => GhostPlaybackLogic.LoopUnitSet.Empty;

        /// <summary>
        /// A non-looping recording (rec.LoopPlayback == false) with the given [start,end] window.
        /// Used as a SOURCE (b) member: a Mission-unit overlap member does NOT carry its own loop flag.
        /// </summary>
        private static Recording MakeMemberRec(double startUT, double endUT, string id = "rec-member")
            => MakeLoopRec(startUT, endUT, loopInterval: 30, loopPlayback: false, id: id);

        /// <summary>
        /// A single-member self-overlapping Mission unit over [spanStart, spanEnd] with the given
        /// overlap cadence (&lt; span => self-overlap). Optional per-member trim window. PhaseAnchor
        /// defaults to spanStart. Mirrors the construction in MissionSpanClockTests.
        /// </summary>
        private static GhostPlaybackLogic.LoopUnitSet MakeOverlapUnitSet(
            int memberIdx, double spanStartUT, double spanEndUT, double overlapCadenceSeconds,
            double phaseAnchorUT = double.NaN,
            (double startUT, double endUT)? memberWindow = null)
        {
            if (double.IsNaN(phaseAnchorUT))
                phaseAnchorUT = spanStartUT;

            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> windows = null;
            if (memberWindow.HasValue)
                windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    { memberIdx, new GhostPlaybackLogic.LoopUnit.MemberWindow(
                        memberWindow.Value.startUT, memberWindow.Value.endUT) }
                };

            var unit = new GhostPlaybackLogic.LoopUnit(
                memberIdx, new[] { memberIdx }, spanStartUT, spanEndUT,
                cadenceSeconds: spanEndUT - spanStartUT, phaseAnchorUT: phaseAnchorUT,
                overlapCadenceSeconds: overlapCadenceSeconds, memberWindows: windows);
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { memberIdx, unit } };
            var ownerByIndex = new Dictionary<int, int> { { memberIdx, memberIdx } };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
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
            Assert.True(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, NoUnits));
        }

        [Fact]
        public void IsOverlapRecording_NotLooping_False()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30, loopPlayback: false) };
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, NoUnits));
        }

        [Fact]
        public void IsOverlapRecording_LoopingLongPeriod_False()
        {
            // period (500) > duration (200): looping but NOT overlapping (single-ghost path).
            var committed = new List<Recording> { MakeLoopRec(100, 300, 500) };
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, NoUnits));
        }

        [Fact]
        public void IsOverlapRecording_Null_False()
        {
            Assert.False(GhostMapPresence.IsOverlapRecording(null, 0, new List<Recording>(), NoUnits));
        }

        [Fact]
        public void ShouldDriveOverlapPerInstance_OverlapRecording_True()
        {
            // 8e S4: the director-drive gate was dropped, so overlap recordings ALWAYS take the
            // per-instance path (this is just IsOverlapRecording now).
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            Assert.True(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed, NoUnits));
        }

        [Fact]
        public void ShouldDriveOverlapPerInstance_NonOverlap_False()
        {
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30, loopPlayback: false) };
            Assert.False(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed, NoUnits));
        }

        // =================================================================
        //  Source (b): Mission-unit self-overlap gate (rec.LoopPlayback == false)
        // =================================================================

        [Fact]
        public void IsOverlapRecording_MissionUnitOverlap_NoRecLoopFlag_True()
        {
            // The maintainer's case: the recording does NOT loop on its own (rec.LoopPlayback=false),
            // but it is a member of a looped Mission unit whose overlap cadence (20s) is shorter than
            // its span (200s) -> self-overlap. Source (b) must drive it even with no rec loop flag.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20);

            Assert.False(committed[0].LoopPlayback);
            Assert.True(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, units));
            Assert.True(GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed, units));
            // With NO loop units it is NOT an overlap recording (the bug: gate rejected it -> one icon).
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, NoUnits));
        }

        [Fact]
        public void IsOverlapRecording_MissionUnit_CadenceAtOrAboveSpan_False()
        {
            // Cadence >= span: a single span-clock instance, NOT self-overlap. Source (b) rejects it.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(0, 1000, 1200, overlapCadenceSeconds: 200); // == span
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, units));
        }

        [Fact]
        public void IsOverlapRecording_MissionUnit_ZeroMemberDuration_False()
        {
            // A member trimmed to a zero-length window can't replay; the member-duration floor rejects it.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(0, 1000, 1200, overlapCadenceSeconds: 20,
                memberWindow: (1100, 1100));
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, units));
        }

        [Fact]
        public void IsOverlapRecording_PreferUnitWhenBoth()
        {
            // A recording that is BOTH a standalone loop (rec.LoopPlayback, period 30 < dur) AND a
            // self-overlapping unit member: source (b) is preferred. The resolved schedule must be the
            // UNIT's (scheduleStart from the unit anchor + member offset), not the standalone loop's.
            var committed = new List<Recording> { MakeLoopRec(1000, 1200, 30) }; // standalone overlap too
            var units = MakeOverlapUnitSet(
                0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000);

            Assert.True(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, units));
            bool ok = GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, units,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out _);
            Assert.True(ok);
            // Unit tuple: memberStart == spanStart == 1000 (no trim); scheduleStart from the unit anchor
            // (5000 + (1000-1000) = 5000), NOT the standalone loop's own EffectiveLoopStartUT (1000).
            Assert.Equal(1000.0, playbackStartUT, 3);
            Assert.Equal(5000.0, scheduleStartUT, 3);
            Assert.Equal(200.0, duration, 3);
            // overlapCadence 20s over a 200s span: ceil(200/20)=10 <= cap 20 -> cadence stays 20.
            Assert.Equal(20.0, effectiveCadence, 3);
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
                committed[0], 0, committed, NoUnits,
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
                committed[0], 0, committed, NoUnits, currentUT,
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
                committed[0], 0, committed, NoUnits,
                out _, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out _);

            GhostMapPresence.OverlapCyclesForTesting(
                committed[0], 0, committed, NoUnits, currentUT,
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
        //  Source (b): Mission-unit schedule + cycle-set equivalence
        // =================================================================

        [Fact]
        public void ResolveOverlapSchedule_MissionUnit_MatchesEngineTuple()
        {
            // Mirror the engine's unit tuple (GhostPlaybackEngine.cs:2163-2183 + 3570-3571 cap re-clamp):
            // member [1000,1200] (no trim), anchor 5000, overlapCadence 20s.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(
                0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000);

            bool ok = GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, units,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out double cycleDuration);
            Assert.True(ok);

            // Engine tuple (verbatim):
            double memberStartUT = 1000, memberEndUT = 1200;
            double expectedDuration = memberEndUT - memberStartUT;          // 200
            double expectedSchedule = GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(
                5000, 1000, memberStartUT);                                 // 5000
            double expectedCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                20, expectedDuration, GhostPlayback.MaxOverlapGhostsPerRecording); // 20
            Assert.Equal(memberStartUT, playbackStartUT, 3);
            Assert.Equal(expectedSchedule, scheduleStartUT, 3);
            Assert.Equal(expectedDuration, duration, 3);
            Assert.Equal(expectedCadence, effectiveCadence, 3);
            Assert.Equal(System.Math.Max(expectedCadence, LoopTiming.MinCycleDuration), cycleDuration, 3);
        }

        [Fact]
        public void OverlapCyclesForTesting_MissionUnit_MatchesGetActiveCyclesFromUnitTuple()
        {
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(
                0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000);

            GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, units,
                out _, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out _);

            double currentUT = 5130.0; // 130s into the schedule (launches every 20s from 5000)
            GhostMapPresence.OverlapCyclesForTesting(
                committed[0], 0, committed, units, currentUT,
                out long mapFirst, out long mapLast);

            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long engineFirst, out long engineLast);

            Assert.Equal(engineFirst, mapFirst);
            Assert.Equal(engineLast, mapLast);
            Assert.True(mapLast - mapFirst + 1 <= GhostPlayback.MaxOverlapGhostsPerRecording);
        }

        [Fact]
        public void ResolveOverlapSchedule_MissionUnit_MemberTrim_YieldsTrimmedDuration()
        {
            // The mission trims this member to [1050, 1150] (a pod shown only after the decouple).
            // The overlap schedule must use the TRIMMED window: duration 100, playbackStart 1050,
            // schedule staggered by the member offset (memberStart - spanStart = 50) from the anchor.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(
                0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000,
                memberWindow: (1050, 1150));

            bool ok = GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, units,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out _, out _);
            Assert.True(ok);
            Assert.Equal(1050.0, playbackStartUT, 3);
            Assert.Equal(100.0, duration, 3);                       // trimmed (200 -> 100)
            Assert.Equal(5050.0, scheduleStartUT, 3);               // 5000 + (1050 - 1000)
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

        // =================================================================
        //  Gate-decision logging (the playtest blind spot)
        // =================================================================

        [Fact]
        public void LogOverlapGateDecision_MissionLoop_ShowsIsMemberAndUnitOverlaps()
        {
            // The diagnostic the playtest lacked: a re-fly Mission member (rec.LoopPlayback=false)
            // driven via source (b) must log isMember=true unitOverlaps=true so a "one icon instead of
            // N" report is diagnosable from the log alone, without a rebuild.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(0, 1000, 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000);
            bool shouldDrive = GhostMapPresence.ShouldDriveOverlapPerInstance(committed[0], 0, committed, units);
            Assert.True(shouldDrive);

            GhostMapPresence.LogOverlapGateDecision(
                0, committed[0], committed, units, gateOn: true, shouldDrive: shouldDrive);

            Assert.Contains(logLines, l =>
                l.Contains("Overlap gate decision")
                && l.Contains("isMember=True")
                && l.Contains("unitOverlaps=True")
                && l.Contains("loopPlayback=False")
                && l.Contains("shouldDrive=True"));
        }

        [Fact]
        public void LogOverlapGateDecision_StandaloneLoop_ShowsAutoOverlapNotMember()
        {
            // Source (a) standalone loop: the line shows autoIsOverlapLoop=True isMember=False.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            GhostMapPresence.LogOverlapGateDecision(
                0, committed[0], committed, NoUnits, gateOn: true, shouldDrive: true);

            Assert.Contains(logLines, l =>
                l.Contains("Overlap gate decision")
                && l.Contains("loopPlayback=True")
                && l.Contains("autoIsOverlapLoop=True")
                && l.Contains("isMember=False"));
        }

        [Fact]
        public void LogOverlapGateDecision_StableVerdict_CoalescesAndReEmitsOnFlip()
        {
            // The gate verdict is stable for the vast majority of frames, so the line must
            // be change-detected: a steady verdict collapses to a single line and re-emits
            // (with a suppressed count) only when the decision actually flips.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };

            for (int i = 0; i < 5; i++)
                GhostMapPresence.LogOverlapGateDecision(
                    0, committed[0], committed, NoUnits, gateOn: true, shouldDrive: false);

            Assert.Equal(1, logLines.Count(l => l.Contains("Overlap gate decision")));

            // Flip the verdict -> exactly one new line, carrying the coalesced count.
            GhostMapPresence.LogOverlapGateDecision(
                0, committed[0], committed, NoUnits, gateOn: true, shouldDrive: true);

            Assert.Equal(2, logLines.Count(l => l.Contains("Overlap gate decision")));
            Assert.Contains(logLines, l =>
                l.Contains("Overlap gate decision")
                && l.Contains("shouldDrive=True")
                && l.Contains("suppressed="));
        }

        // =================================================================
        //  Slice (iii): per-instance head-UT resolution (the marker ride set)
        // =================================================================

        [Fact]
        public void TryGetLiveOverlapHeadUTs_SameCycleSetAsOverlapCyclesForTesting()
        {
            // The N markers that ride the shared polyline must be the SAME live cycle set slice (i)
            // materializes (and the flight engine drives): TryGetLiveOverlapHeadUTs's cycle set ==
            // OverlapCyclesForTesting's [firstCycle, lastCycle].
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            double currentUT = 250.0;

            GhostMapPresence.OverlapCyclesForTesting(
                committed[0], 0, committed, NoUnits, currentUT,
                out long expectedFirst, out long expectedLast);

            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, NoUnits, currentUT, buffer);

            Assert.True(ok);
            var cycles = buffer.Select(e => e.cycle).ToList();
            // Contiguous [first, last] set, byte-identical to OverlapCyclesForTesting.
            var expected = new List<long>();
            for (long c = expectedFirst; c <= expectedLast; c++) expected.Add(c);
            Assert.Equal(expected, cycles);
        }

        [Theory]
        [InlineData(130.0)]
        [InlineData(250.0)]
        [InlineData(400.0)]
        public void TryGetLiveOverlapHeadUTs_CycleSetMatchesGetActiveCycles(double currentUT)
        {
            // 5s period over a 200s duration -> up to 40 cycles, capped at 20: exercises the cap clamp,
            // mirroring OverlapCyclesForTesting_MatchesGetActiveCycles but on the slice-(iii) head path.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 5) };
            GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, NoUnits,
                out _, out double scheduleStartUT,
                out double duration, out double effectiveCadence, out _);

            GhostPlaybackLogic.GetActiveCycles(
                currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording,
                out long engineFirst, out long engineLast);

            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, NoUnits, currentUT, buffer);

            Assert.True(ok);
            Assert.Equal(engineLast - engineFirst + 1, buffer.Count);
            Assert.Equal(engineFirst, buffer[0].cycle);
            Assert.Equal(engineLast, buffer[buffer.Count - 1].cycle);
            Assert.True(buffer.Count <= GhostPlayback.MaxOverlapGhostsPerRecording);
        }

        [Fact]
        public void TryGetLiveOverlapHeadUTs_PerCycleHeadEqualsComputeOverlapCyclePlaybackUT()
        {
            // Each instance's head UT must be ComputeOverlapCyclePlaybackUT(cycle) DIRECTLY (NOT routed
            // through the span-clock collapse), and the heads must be DISTINCT across cycles.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            double currentUT = 250.0;

            GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, NoUnits,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out _, out double cycleDuration);

            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, NoUnits, currentUT, buffer);
            Assert.True(ok);
            Assert.True(buffer.Count >= 2, "expected several overlapping cycles for distinctness check");

            var seen = new HashSet<double>();
            foreach (var (cycle, headUT) in buffer)
            {
                double expected = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT, scheduleStartUT, playbackStartUT, duration, cycleDuration, cycle);
                Assert.Equal(expected, headUT, 6);
                // Distinct heads across cycles (staggered launches sit at different playback phases).
                Assert.True(seen.Add(headUT), $"duplicate head UT {headUT} for cycle {cycle}");
            }
        }

        [Fact]
        public void TryGetLiveOverlapHeadUTs_MissionUnit_PerCycleHeadEqualsComputeUT()
        {
            // Source (b): a self-overlapping Mission member (rec.LoopPlayback=false). The head set must
            // still be ComputeOverlapCyclePlaybackUT per cycle off the unit tuple.
            var committed = new List<Recording> { MakeMemberRec(1000, 1200) };
            var units = MakeOverlapUnitSet(
                0, spanStartUT: 1000, spanEndUT: 1200, overlapCadenceSeconds: 20, phaseAnchorUT: 5000);
            double currentUT = 5130.0;

            GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, units,
                out double playbackStartUT, out double scheduleStartUT,
                out double duration, out _, out double cycleDuration);

            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, units, currentUT, buffer);
            Assert.True(ok);
            Assert.NotEmpty(buffer);

            foreach (var (cycle, headUT) in buffer)
            {
                double expected = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                    currentUT, scheduleStartUT, playbackStartUT, duration, cycleDuration, cycle);
                Assert.Equal(expected, headUT, 6);
            }
        }

        [Fact]
        public void TryGetLiveOverlapHeadUTs_NonOverlap_False()
        {
            // A looping-but-NON-overlapping recording (period > duration) is single-instance: false.
            var committed = new List<Recording> { MakeLoopRec(100, 300, 500) };
            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, NoUnits, 250.0, buffer);
            Assert.False(ok);
            Assert.Empty(buffer);
        }

        [Fact]
        public void TryGetLiveOverlapHeadUTs_BeforeScheduleStart_False()
        {
            // currentUT before the first launch: no live instances yet -> false (legacy tail).
            var committed = new List<Recording> { MakeLoopRec(100, 300, 30) };
            var buffer = new List<(long cycle, double headUT)>();
            bool ok = GhostMapPresence.TryGetLiveOverlapHeadUTs(
                committed[0], 0, committed, NoUnits, 50.0, buffer);
            Assert.False(ok);
            Assert.Empty(buffer);
        }

        [Fact]
        public void TryGetOverlapInstancePidForCycle_EmptyStore_ReturnsZero()
        {
            // Pure-suborbital case: overlapInstanceVessels is empty for the recording, so every cycle's
            // proto lookup is 0 -> the marker path draws every polyline marker (the maintainer's mission).
            Assert.Equal(0u, GhostMapPresence.TryGetOverlapInstancePidForCycle(0, 0));
            Assert.Equal(0u, GhostMapPresence.TryGetOverlapInstancePidForCycle(0, 5));
            Assert.Equal(0u, GhostMapPresence.TryGetOverlapInstancePidForCycle(3, 2));
        }
    }
}
