using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the helpers the flight map deferred-create and orbit-reseed passes are
    /// composed of. The end-to-end pass contract is pinned by
    /// <see cref="FlightMapPassCharacterizationTests"/>; these cells pin each helper's own
    /// outputs, including the lists and stores it must leave alone.
    /// </summary>
    [Collection("Sequential")]
    public class FlightMapPassPhaseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;

        public FlightMapPassPhaseTests()
        {
            GhostMapPresence.ResetForTesting();
            GhostMapPresence.ClearFlightMapPresenceState();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin : null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            GhostMapPresence.ClearFlightMapPresenceState();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static OrbitSegment Seg(double start, double end, double sma, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = start, endUT = end, bodyName = body, semiMajorAxis = sma,
                eccentricity = 0.01, inclination = 0.3, epoch = start
            };
        }

        private static Recording SegmentRecording(string id, string terminalBody = "Kerbin")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Phase " + id,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = terminalBody,
                TerminalOrbitSemiMajorAxis = 700000,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.3,
                TerminalOrbitEpoch = 100,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin", altitude = 70000 },
                    new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 100000 }
                },
                OrbitSegments = new List<OrbitSegment> { Seg(400, 600, 700000) },
            };
        }

        private static Recording LandedSegmentRecording(string id)
        {
            var rec = SegmentRecording(id);
            rec.TerminalStateValue = TerminalState.Landed;
            rec.TerminalOrbitBody = null;
            rec.TerminalOrbitSemiMajorAxis = 0;
            return rec;
        }

        private static Recording GapRecording(string id, bool bracket)
        {
            var rec = SegmentRecording(id);
            rec.OrbitSegments = new List<OrbitSegment> { Seg(200, 300, 700000), Seg(500, 600, 800000) };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100, endUT = 900,
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    frames = new List<TrajectoryPoint>(),
                }
            };
            rec.Points = bracket
                ? new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 350, bodyName = "Kerbin", altitude = 90000 },
                    new TrajectoryPoint { ut = 450, bodyName = "Kerbin", altitude = 150000 },
                }
                : new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 420, bodyName = "Kerbin", altitude = 150000 },
                    new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 200000 },
                };
            return rec;
        }

        private static Recording RetainedRecording(string id)
        {
            var rec = SegmentRecording(id);
            rec.TerminalOrbitBody = null;
            rec.TerminalOrbitSemiMajorAxis = 0;
            rec.VesselSnapshot = new ConfigNode("VESSEL");
            return rec;
        }

        private int CountLines(string fragment)
        {
            int n = 0;
            foreach (string l in logLines)
                if (l.Contains(fragment)) n++;
            return n;
        }

        // ------------------------------------------------ ResolvePendingMapCreateLoopSynthesis

        [Fact]
        public void LoopSynthesis_NotInWindow_FalseAndSilent()
        {
            bool accepted = GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(
                2, SegmentRecording("a"), false, 0.0);

            Assert.False(accepted);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LoopSynthesis_InWindowSameBody_TrueWithPolicyLine()
        {
            bool accepted = GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(
                2, SegmentRecording("b"), true, 1500.0);

            Assert.True(accepted);
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("Pending map-create accepting terminal-orbit synthesis for loop member "
                    + "rec=#2 vessel=\"Phase b\" pendingLoopShift=1500.0"));
        }

        [Fact]
        public void LoopSynthesis_InWindowCrossBody_FalseWithNoPolicyLine()
        {
            bool accepted = GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(
                2, SegmentRecording("c", terminalBody: "Mun"), true, 1500.0);

            Assert.False(accepted);
            Assert.DoesNotContain(logLines, l => l.Contains("[Policy]"));
            Assert.Contains(logLines, l => l.Contains("reason=cross-body-terminal"));
        }

        [Fact]
        public void LoopSynthesis_LineIsRateLimitedPerIndex()
        {
            var rec = SegmentRecording("d");
            GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(1, rec, true, 10.0);
            GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(1, rec, true, 10.0);
            GhostMapPresence.ResolvePendingMapCreateLoopSynthesis(3, rec, true, 10.0);

            Assert.Equal(1, CountLines("rec=#1 vessel"));
            Assert.Equal(1, CountLines("rec=#3 vessel"));
        }

        // ------------------------------------------------ ResolvePendingMapCreateSource

        [UnityEntryFact]
        public void Source_CoveredSegment_ReturnsSegmentAndWritesCache()
        {
            var rec = SegmentRecording("e");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var pending = new GhostMapPresence.PendingMapVessel(rec, false, null);

            var source = GhostMapPresence.ResolvePendingMapCreateSource(
                0, pending, rec, 500, RecordingStore.CommittedRecordings, false, false,
                out OrbitSegment segment, out TrajectoryPoint _);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal(400, segment.startUT);
            Assert.Equal(-1, GhostMapPresence.flightStateVectorCachedIndices[0]);
            Assert.Contains(logLines, l => l.Contains("map-presence-pending-create: rec=e"));
        }

        [UnityEntryFact]
        public void Source_BeforeActivation_ReturnsNoneAndStillWritesCache()
        {
            var rec = SegmentRecording("f");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var pending = new GhostMapPresence.PendingMapVessel(rec, false, null);

            var source = GhostMapPresence.ResolvePendingMapCreateSource(
                0, pending, rec, 50, RecordingStore.CommittedRecordings, false, false,
                out OrbitSegment _, out TrajectoryPoint _);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.True(GhostMapPresence.flightStateVectorCachedIndices.ContainsKey(0));
            Assert.Contains(logLines, l => l.Contains("reason=before-activation"));
        }

        [UnityEntryFact]
        public void Source_IndexOutsideCommitted_ResolvesFromTheTrajectory()
        {
            var rec = SegmentRecording("g");
            var pending = new GhostMapPresence.PendingMapVessel(rec, false, null);

            var source = GhostMapPresence.ResolvePendingMapCreateSource(
                7, pending, rec, 500, RecordingStore.CommittedRecordings, false, false,
                out OrbitSegment _, out TrajectoryPoint _);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal(-1, GhostMapPresence.flightStateVectorCachedIndices[7]);
        }

        [UnityEntryFact]
        public void Source_ReadsTheCachedStateVectorIndex()
        {
            var rec = new Recording
            {
                RecordingId = "h",
                VesselName = "Phase h",
                TerminalStateValue = TerminalState.SubOrbital,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100, bodyName = "Kerbin", altitude = 75000, velocity = new Vector3(0, 2200, 0) },
                    new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 90000, velocity = new Vector3(0, 2100, 0) },
                }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            GhostMapPresence.flightStateVectorCachedIndices[0] = 0;
            var pending = new GhostMapPresence.PendingMapVessel(rec, false, null);

            var source = GhostMapPresence.ResolvePendingMapCreateSource(
                0, pending, rec, 500, RecordingStore.CommittedRecordings, false, false,
                out OrbitSegment _, out TrajectoryPoint point);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.StateVector, source);
            Assert.Equal(100, point.ut);
            Assert.Equal(0, GhostMapPresence.flightStateVectorCachedIndices[0]);
        }

        // ------------------------------------------------ TearDownFlightMapGhostOutOfLoopWindow

        [UnityEntryFact]
        public void TearDown_AllocatesBothListsAndQueuesNullBody()
        {
            List<int> remove = null;
            List<(int idx, string expectedBody)> requeue = null;

            GhostMapPresence.TearDownFlightMapGhostOutOfLoopWindow(
                4, GhostPlaybackLogic.LoopUnitSet.Empty, ref remove, ref requeue);

            Assert.Equal(new[] { 4 }, remove);
            Assert.Single(requeue);
            Assert.Equal(4, requeue[0].idx);
            Assert.Null(requeue[0].expectedBody);
        }

        [UnityEntryFact]
        public void TearDown_AppendsToExistingLists()
        {
            var remove = new List<int> { 1 };
            var requeue = new List<(int idx, string expectedBody)> { (1, "Mun") };
            List<int> removeBefore = remove;

            GhostMapPresence.TearDownFlightMapGhostOutOfLoopWindow(
                2, GhostPlaybackLogic.LoopUnitSet.Empty, ref remove, ref requeue);

            Assert.Same(removeBefore, remove);
            Assert.Equal(new[] { 1, 2 }, remove);
            Assert.Equal(2, requeue.Count);
            Assert.Equal((2, (string)null), requeue[1]);
        }

        // ------------------------------------------------ TryDriveFlightMapGapFromPoints

        [UnityEntryFact]
        public void GapGlide_BracketingPoint_RecordsSentinelKeyAndCache()
        {
            var rec = GapRecording("i", bracket: true);
            List<KeyValuePair<int, (string body, double sma, double ecc)>> updates = null;

            bool glided = GhostMapPresence.TryDriveFlightMapGapFromPoints(3, rec, 400, 0.0, ref updates);

            Assert.True(glided);
            Assert.Single(updates);
            Assert.Equal(3, updates[0].Key);
            Assert.Equal(("Kerbin", 0.0, 0.0), updates[0].Value);
            Assert.Equal(0, GhostMapPresence.flightStateVectorCachedIndices[3]);
            Assert.Contains(logLines, l => l.Contains("Flight gap-points glide: member=3 effUT=400.0 body=Kerbin")
                && l.Contains("reFlySuppressed=False"));
        }

        [UnityEntryFact]
        public void GapGlide_NoBracketingPoint_FalseLeavesUpdatesNull()
        {
            var rec = GapRecording("j", bracket: false);
            List<KeyValuePair<int, (string body, double sma, double ecc)>> updates = null;

            bool glided = GhostMapPresence.TryDriveFlightMapGapFromPoints(3, rec, 400, 0.0, ref updates);

            Assert.False(glided);
            Assert.Null(updates);
            Assert.True(GhostMapPresence.flightStateVectorCachedIndices.ContainsKey(3));
            Assert.Contains(logLines, l => l.Contains(
                "Flight gap-points glide skipped (no bracketing point): member=3 effUT=400.0"));
        }

        // ------------------------------------------------ ResolveFlightMapNoSegmentArm

        private void NoSegment(Recording rec, double ut, (string, double, double) lastKey,
            out List<KeyValuePair<int, (string body, double sma, double ecc)>> updates,
            out List<int> remove, out List<(int idx, string expectedBody)> requeue)
        {
            updates = null;
            remove = null;
            requeue = null;
            GhostMapPresence.ResolveFlightMapNoSegmentArm(
                0, rec, ut, ut, 0.0, lastKey, rec.OrbitSegments,
                ref updates, ref remove, ref requeue);
        }

        [UnityEntryFact]
        public void NoSegment_TerminalFallbackChanged_RecordsTheKey()
        {
            var rec = SegmentRecording("k");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NoSegment(rec, 300, ("Kerbin", 1.0, 0.0), out var updates, out var remove, out var requeue);

            Assert.Single(updates);
            Assert.Equal(("Kerbin", 700000.0, 0.01), updates[0].Value);
            Assert.Null(remove);
            Assert.Null(requeue);
        }

        [UnityEntryFact]
        public void NoSegment_TerminalFallbackUnchanged_RecordsNothing()
        {
            var rec = SegmentRecording("l");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NoSegment(rec, 700, ("Kerbin", 700000.0, 0.01), out var updates, out var remove, out var requeue);

            Assert.Null(updates);
            Assert.Null(remove);
            Assert.Null(requeue);
            Assert.True(GhostMapPresence.flightStateVectorCachedIndices.ContainsKey(0));
        }

        [UnityEntryFact]
        public void NoSegment_FutureSegment_RemovesAndRequeuesWithItsBody()
        {
            var rec = LandedSegmentRecording("m");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NoSegment(rec, 300, ("Kerbin", 700000.0, 0.01), out var updates, out var remove, out var requeue);

            Assert.Null(updates);
            Assert.Equal(new[] { 0 }, remove);
            Assert.Single(requeue);
            Assert.Equal((0, "Kerbin"), requeue[0]);
        }

        [UnityEntryFact]
        public void NoSegment_PastTheLastSegment_RemovesWithoutRequeue()
        {
            var rec = LandedSegmentRecording("n");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NoSegment(rec, 700, ("Kerbin", 700000.0, 0.01), out var updates, out var remove, out var requeue);

            Assert.Null(updates);
            Assert.Equal(new[] { 0 }, remove);
            Assert.Null(requeue);
        }

        [UnityEntryFact]
        public void NoSegment_TerminalRealSpawnPending_KeepsTheGhost()
        {
            var rec = RetainedRecording("o");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NoSegment(rec, 950, ("Kerbin", 700000.0, 0.01), out var updates, out var remove, out var requeue);

            Assert.Null(updates);
            Assert.Null(remove);
            Assert.Null(requeue);
            Assert.Contains("o", GhostMapPresence.flightTerminalMapRetentionLoggedIds);
        }

        [UnityEntryFact]
        public void ReseedPass_LoopMemberNoSegment_UsesTheLoopSampleUTNotTheLiveUT()
        {
            // A Mission-loop member in its window: live UT 2300 maps to effUT 300, before the
            // only segment (400-600). The no-segment arm must resolve and scan at effUT (the
            // future segment re-queues it), not at the live UT (past every segment, where it
            // would be removed without a re-queue). Off the loop path effUT == currentUT, so
            // only a loop member tells the two apart.
            var rec = LandedSegmentRecording("u");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            GhostMapPresence.flightLastMapOrbitByIndex[0] = ("Kerbin", 700000, 0.01);
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, spanStartUT: 100, spanEndUT: 900,
                cadenceSeconds: 2000, phaseAnchorUT: 100);
            var units = new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });

            var pass = typeof(GhostMapPresence).GetMethod("RunFlightMapOrbitReseedPass",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(pass);
            try
            {
                pass.Invoke(null, new object[] { 2300.0, units, RecordingStore.CommittedRecordings });
            }
            catch (System.Reflection.TargetInvocationException e) { throw e.InnerException; }

            Assert.Contains(logLines, l => l.Contains("map-presence-orbit-update: rec=u currentUT=300.0"));
            Assert.True(GhostMapPresence.flightPendingMapVessels.ContainsKey(0));
            Assert.Equal("Kerbin", GhostMapPresence.flightPendingMapVessels[0].ExpectedSoiGapBody);
            Assert.False(GhostMapPresence.flightLastMapOrbitByIndex.ContainsKey(0));
            Assert.Contains(logLines, l => l.Contains(
                "Re-queued recording #0 to pendingMapVessels (gap between orbit segments expectedBody=Kerbin)"));
        }

        // ------------------------------------------------ TryFindFirstFutureOrbitSegmentBody

        [Fact]
        public void FutureSegment_NoneAfter_FalseAndNullBody()
        {
            var segs = new List<OrbitSegment> { Seg(100, 200, 1), Seg(300, 400, 1) };

            Assert.False(GhostMapPresence.TryFindFirstFutureOrbitSegmentBody(segs, 300, out string body));
            Assert.Null(body);
        }

        [Fact]
        public void FutureSegment_ReturnsTheFirstStartingAfter()
        {
            var segs = new List<OrbitSegment>
            {
                Seg(100, 200, 1, "Kerbin"), Seg(300, 400, 1, "Mun"), Seg(500, 600, 1, "Minmus")
            };

            Assert.True(GhostMapPresence.TryFindFirstFutureOrbitSegmentBody(segs, 250, out string body));
            Assert.Equal("Mun", body);
        }

        [Fact]
        public void FutureSegment_EmptyList_False()
        {
            Assert.False(GhostMapPresence.TryFindFirstFutureOrbitSegmentBody(
                new List<OrbitSegment>(), 0, out string body));
            Assert.Null(body);
        }

        // ------------------------------------------------ LogFlightTerminalMapRetention

        [Fact]
        public void Retention_FirstInfoThenRateLimitedVerbose()
        {
            var rec = RetainedRecording("p");

            GhostMapPresence.LogFlightTerminalMapRetention(rec, 2, 950);
            GhostMapPresence.LogFlightTerminalMapRetention(rec, 2, 951);
            GhostMapPresence.LogFlightTerminalMapRetention(rec, 2, 952);

            Assert.Equal(1, CountLines("[INFO][Policy] Map presence retained"));
            Assert.Equal(1, CountLines("[VERBOSE][Policy] Map presence retained"));
            Assert.Contains(logLines, l => l.Contains(
                "rec=p idx=2 vessel=\"Phase p\" currentUT=950.00 reason=pending-terminal-real-spawn"));
            Assert.Contains("p", GhostMapPresence.flightTerminalMapRetentionLoggedIds);
        }

        [Theory]
        [InlineData(true, false, "code-a", "reason=code-a")]
        [InlineData(true, false, null, "reason=cannot-spawn-safely")]
        [InlineData(false, true, "code-b", "reason=code-b")]
        [InlineData(false, true, null, "reason=deferred")]
        public void Retention_ReasonFollowsTheSafetyVerdict(
            bool cannotSpawn, bool deferred, string code, string expected)
        {
            var rec = RetainedRecording("q");
            rec.TerminalSpawnCannotSpawnSafely = cannotSpawn;
            rec.TerminalSpawnSafetyDeferred = deferred;
            rec.TerminalSpawnSafetyReasonCode = code;

            GhostMapPresence.LogFlightTerminalMapRetention(rec, 0, 10);

            Assert.Contains(logLines, l => l.Contains("Map presence retained") && l.EndsWith(expected));
        }

        [Fact]
        public void Retention_NoRecordingId_KeysOnTheIndex()
        {
            var rec = RetainedRecording("r");
            rec.RecordingId = null;

            GhostMapPresence.LogFlightTerminalMapRetention(rec, 6, 10);

            Assert.Contains("6", GhostMapPresence.flightTerminalMapRetentionLoggedIds);
            Assert.Contains(logLines, l => l.Contains("rec=(null) idx=6"));
        }

        // ------------------------------------------------ ApplyFlightMapOrbitReseedResults

        [Fact]
        public void Apply_AllNull_ChangesNothing()
        {
            GhostMapPresence.flightLastMapOrbitByIndex[0] = ("Kerbin", 1, 0);

            GhostMapPresence.ApplyFlightMapOrbitReseedResults(
                null, null, null, RecordingStore.CommittedRecordings);

            Assert.Single(GhostMapPresence.flightLastMapOrbitByIndex);
            Assert.Empty(GhostMapPresence.flightPendingMapVessels);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Apply_UpdatesThenRemovals()
        {
            GhostMapPresence.flightLastMapOrbitByIndex[0] = ("Kerbin", 1, 0);
            GhostMapPresence.flightLastMapOrbitByIndex[1] = ("Kerbin", 2, 0);
            var updates = new List<KeyValuePair<int, (string body, double sma, double ecc)>>
            {
                new KeyValuePair<int, (string, double, double)>(0, ("Mun", 5, 0.1)),
                new KeyValuePair<int, (string, double, double)>(1, ("Mun", 6, 0.2)),
            };

            GhostMapPresence.ApplyFlightMapOrbitReseedResults(
                updates, new List<int> { 1 }, null, RecordingStore.CommittedRecordings);

            Assert.Single(GhostMapPresence.flightLastMapOrbitByIndex);
            Assert.Equal(("Mun", 5.0, 0.1), GhostMapPresence.flightLastMapOrbitByIndex[0]);
        }

        [Fact]
        public void Apply_Requeue_AddsPendingWithSoiFallbackAndLine()
        {
            var rec = SegmentRecording("s");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            GhostMapPresence.ApplyFlightMapOrbitReseedResults(
                null, null, new List<(int idx, string expectedBody)> { (0, "Mun") },
                RecordingStore.CommittedRecordings);

            var pending = GhostMapPresence.flightPendingMapVessels[0];
            Assert.Same(rec, pending.Trajectory);
            Assert.True(pending.AllowSoiGapStateVectorFallback);
            Assert.Equal("Mun", pending.ExpectedSoiGapBody);
            Assert.Contains(logLines, l => l.Contains("[MapPresence]") && l.Contains(
                "Re-queued recording #0 to pendingMapVessels (gap between orbit segments expectedBody=Mun)"));
        }

        [Fact]
        public void Apply_Requeue_SkipsPendingAndOutOfRangeIndices()
        {
            var rec = SegmentRecording("t");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var existing = new GhostMapPresence.PendingMapVessel(rec, false, "Duna");
            GhostMapPresence.flightPendingMapVessels[0] = existing;

            GhostMapPresence.ApplyFlightMapOrbitReseedResults(
                null, null, new List<(int idx, string expectedBody)> { (0, "Mun"), (3, null) },
                RecordingStore.CommittedRecordings);

            Assert.Single(GhostMapPresence.flightPendingMapVessels);
            Assert.Equal("Duna", GhostMapPresence.flightPendingMapVessels[0].ExpectedSoiGapBody);
            Assert.False(GhostMapPresence.flightPendingMapVessels[0].AllowSoiGapStateVectorFallback);
            Assert.Empty(logLines);
        }
    }
}
