using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TimeJumpManagerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        // Kerbin-like body constants
        private const double KerbinRadius = 600000.0;
        private const double KerbinGM = 3.5316e12;

        // 100km circular orbit at Kerbin
        private const double CircularSMA = 700000.0;

        // Period of 100km circular orbit: 2*pi*sqrt(a^3/GM)
        private static readonly double CircularPeriod =
            2.0 * Math.PI * Math.Sqrt(CircularSMA * CircularSMA * CircularSMA / KerbinGM);

        // Mean motion for 100km circular orbit: n = sqrt(GM / a^3)
        private static readonly double CircularMeanMotion =
            Math.Sqrt(KerbinGM / (CircularSMA * CircularSMA * CircularSMA));

        public TimeJumpManagerTests()
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

        // --- Helper: build a simple GhostChain ---
        private static GhostChain MakeChain(uint pid, double spawnUT,
            bool terminated = false, string tipRecId = null)
        {
            return new GhostChain
            {
                OriginalVesselPid = pid,
                SpawnUT = spawnUT,
                GhostStartUT = spawnUT - 100,
                TipRecordingId = tipRecId ?? $"rec-{pid}",
                TipTreeId = $"tree-{pid}",
                IsTerminated = terminated
            };
        }

        #region ComputeEpochShiftedMeanAnomaly

        /// <summary>
        /// A forward jump advances the mean anomaly by n * delta.
        /// Guards: mean anomaly correctly computed for a circular orbit.
        /// </summary>
        [Fact]
        public void ComputeEpochShift_CircularOrbit_MeanAnomalyAdvances()
        {
            double oldM = 0.5;
            double oldEpoch = 1000.0;
            double newEpoch = 1100.0;
            double delta = newEpoch - oldEpoch; // 100s

            double expected = oldM + CircularMeanMotion * delta;
            const double twoPi = 2.0 * Math.PI;
            expected %= twoPi;
            if (expected < 0) expected += twoPi;

            double result = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                oldM, oldEpoch, CircularSMA, KerbinGM, newEpoch);

            Assert.Equal(expected, result, 10); // 10 decimal places
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("ComputeEpochShiftedMeanAnomaly"));
        }

        /// <summary>
        /// Zero delta (same epoch) returns the original mean anomaly unchanged.
        /// Guards: no spurious shift when jump delta is zero.
        /// </summary>
        [Fact]
        public void ComputeEpochShift_ZeroDelta_Unchanged()
        {
            double oldM = 1.234;
            double epoch = 5000.0;

            double result = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                oldM, epoch, CircularSMA, KerbinGM, epoch);

            Assert.Equal(oldM, result, 10);
        }

        /// <summary>
        /// Advancing by exactly one orbital period should return the same mean anomaly
        /// (normalized to 2*pi, which equals 0 modulo 2pi, but starting from 0 it stays 0).
        /// For a non-zero starting M, the result should wrap back to the same value.
        /// Guards: full-period wrap-around normalization.
        /// </summary>
        [Fact]
        public void ComputeEpochShift_FullPeriod_ReturnsSameAnomaly()
        {
            double oldM = 1.0;
            double oldEpoch = 1000.0;
            double newEpoch = oldEpoch + CircularPeriod;

            double result = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                oldM, oldEpoch, CircularSMA, KerbinGM, newEpoch);

            // After one full period, M should return to the same value (modulo 2pi)
            Assert.Equal(oldM, result, 6); // allow floating point tolerance
        }

        /// <summary>
        /// Large forward jump producing M > 2*pi is normalized into [0, 2*pi].
        /// Guards: normalization for large positive mean anomaly values.
        /// </summary>
        [Fact]
        public void ComputeEpochShift_NormalizesTo2Pi()
        {
            // Start with M=0, advance enough to go past 2*pi
            double oldM = 0.0;
            double oldEpoch = 1000.0;
            // Jump by 1.5 periods — M should be pi
            double newEpoch = oldEpoch + 1.5 * CircularPeriod;

            double result = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                oldM, oldEpoch, CircularSMA, KerbinGM, newEpoch);

            const double twoPi = 2.0 * Math.PI;
            Assert.True(result >= 0, "Result should be >= 0");
            Assert.True(result < twoPi, $"Result {result} should be < 2*pi ({twoPi})");

            // 1.5 periods from M=0 → M = pi
            Assert.Equal(Math.PI, result, 5);
        }

        #endregion

        #region FindCrossedChainTips

        /// <summary>
        /// Single chain tip within [t0, targetUT] is returned.
        /// Guards: basic tip crossing detection.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_SingleTipCrossed()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1500.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Single(result);
            Assert.Equal((uint)100, result[0].OriginalVesselPid);
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("crossed=1"));
        }

        /// <summary>
        /// Multiple chain tips are returned sorted chronologically by SpawnUT.
        /// Guards: chronological ordering of crossed tips.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_MultipleTipsCrossed_SortedChronologically()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1800.0) },
                { 200, MakeChain(200, 1200.0) },
                { 300, MakeChain(300, 1500.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Equal(3, result.Count);
            Assert.Equal((uint)200, result[0].OriginalVesselPid); // 1200
            Assert.Equal((uint)300, result[1].OriginalVesselPid); // 1500
            Assert.Equal((uint)100, result[2].OriginalVesselPid); // 1800
        }

        /// <summary>
        /// Chain tip before t0 is NOT included — it was already crossed.
        /// Guards: lower bound exclusion.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_TipBeforeT0_NotIncluded()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 500.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Empty(result);
        }

        /// <summary>
        /// Chain tip after targetUT is NOT included — the jump doesn't reach it.
        /// Guards: upper bound exclusion.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_TipAfterTarget_NotIncluded()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 3000.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Empty(result);
        }

        /// <summary>
        /// Terminated chains are never included, even if their SpawnUT is in range.
        /// Guards: terminated chain exclusion.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_TerminatedChain_NotIncluded()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1500.0, terminated: true) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Empty(result);
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("terminated"));
        }

        /// <summary>
        /// Empty/null chains dictionary returns empty list without error.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_EmptyChains_ReturnsEmpty()
        {
            var result1 = TimeJumpManager.FindCrossedChainTips(
                new Dictionary<uint, GhostChain>(), 1000.0, 2000.0);
            Assert.Empty(result1);

            var result2 = TimeJumpManager.FindCrossedChainTips(null, 1000.0, 2000.0);
            Assert.Empty(result2);
        }

        /// <summary>
        /// Chain tip exactly at targetUT is included (boundary: SpawnUT <= targetUT).
        /// Guards: inclusive upper bound.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_TipExactlyAtTarget_Included()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 2000.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Single(result);
            Assert.Equal((uint)100, result[0].OriginalVesselPid);
        }

        /// <summary>
        /// Chain tip exactly at t0 is NOT included (boundary: SpawnUT > t0).
        /// Guards: exclusive lower bound.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_TipExactlyAtT0_NotIncluded()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1000.0) }
            };

            var result = TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Empty(result);
        }

        #endregion

        #region ComputeJumpTargetUT

        /// <summary>
        /// Returns the SpawnUT of the chain for the given vessel PID.
        /// Guards: basic target computation for a single chain.
        /// </summary>
        [Fact]
        public void ComputeJumpTargetUT_SingleChain_ReturnsSpawnUT()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1600.0) }
            };

            double target = TimeJumpManager.ComputeJumpTargetUT(chains, 100);

            Assert.Equal(1600.0, target);
        }

        /// <summary>
        /// Non-existent PID returns 0 (invalid).
        /// Guards: missing vessel PID handling.
        /// </summary>
        [Fact]
        public void ComputeJumpTargetUT_NonExistentPid_ReturnsZero()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1600.0) }
            };

            double target = TimeJumpManager.ComputeJumpTargetUT(chains, 999);

            Assert.Equal(0.0, target);
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("not found"));
        }

        /// <summary>
        /// With multiple chains, returns the selected chain's SpawnUT regardless of others.
        /// Earlier independent tips are auto-included by the jump execution, not the target computation.
        /// Guards: target is based on selection, not on chronological ordering.
        /// </summary>
        [Fact]
        public void ComputeJumpTargetUT_EarlierIndependentTips_ReturnsSelectedSpawnUT()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1200.0) },
                { 200, MakeChain(200, 1800.0) },
                { 300, MakeChain(300, 2400.0) }
            };

            // Select the middle chain
            double target = TimeJumpManager.ComputeJumpTargetUT(chains, 200);

            // Returns selected chain's SpawnUT — earlier tips (100) will be crossed during jump
            Assert.Equal(1800.0, target);
        }

        /// <summary>
        /// Null/empty chains returns 0.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void ComputeJumpTargetUT_NullChains_ReturnsZero()
        {
            Assert.Equal(0.0, TimeJumpManager.ComputeJumpTargetUT(null, 100));
            Assert.Equal(0.0, TimeJumpManager.ComputeJumpTargetUT(
                new Dictionary<uint, GhostChain>(), 100));
        }

        #endregion

        #region IsValidJump

        /// <summary>
        /// Forward jump (target > current) is valid.
        /// </summary>
        [Fact]
        public void IsValidJump_ForwardJump_True()
        {
            Assert.True(TimeJumpManager.IsValidJump(1000.0, 2000.0));
        }

        /// <summary>
        /// Backward jump (target < current) is invalid.
        /// </summary>
        [Fact]
        public void IsValidJump_BackwardJump_False()
        {
            Assert.False(TimeJumpManager.IsValidJump(2000.0, 1000.0));
        }

        /// <summary>
        /// Same UT (target == current) is invalid — no jump to perform.
        /// </summary>
        [Fact]
        public void IsValidJump_SameUT_False()
        {
            Assert.False(TimeJumpManager.IsValidJump(1000.0, 1000.0));
        }

        /// <summary>
        /// Very small forward jump is valid.
        /// Guards: no minimum jump threshold.
        /// </summary>
        [Fact]
        public void IsValidJump_TinyForwardJump_True()
        {
            Assert.True(TimeJumpManager.IsValidJump(1000.0, 1000.001));
        }

        #endregion

        #region Forward-jump auto-record suppression

        [Fact]
        public void IsForwardJumpLaunchAutoRecordSuppressed_InProgress_ReturnsTrue()
        {
            bool suppressed = TimeJumpManager.IsForwardJumpLaunchAutoRecordSuppressed(
                forwardJumpInProgress: true,
                currentFrame: 100,
                suppressUntilFrame: -1);

            Assert.True(suppressed);
        }

        [Fact]
        public void IsForwardJumpLaunchAutoRecordSuppressed_PostJumpFrameWindow_ReturnsTrue()
        {
            bool suppressed = TimeJumpManager.IsForwardJumpLaunchAutoRecordSuppressed(
                forwardJumpInProgress: false,
                currentFrame: 100,
                suppressUntilFrame: 101);

            Assert.True(suppressed);
        }

        [Fact]
        public void IsForwardJumpLaunchAutoRecordSuppressed_FrameExpiry_DoesNotRearmOnEarlierUtRollback()
        {
            // The old UT-based suppression could become true again after a rollback to an
            // earlier save. Frame-bounded suppression must stay expired once the transient
            // frames have passed, regardless of any later UT value.
            bool suppressedAfterTransient = TimeJumpManager.IsForwardJumpLaunchAutoRecordSuppressed(
                forwardJumpInProgress: false,
                currentFrame: 120,
                suppressUntilFrame: 101);

            Assert.False(suppressedAfterTransient);
        }

        #endregion

        #region CreateTimeJumpEvent

        /// <summary>
        /// CreateTimeJumpEvent produces a SegmentEvent with correct type and details.
        /// Guards: event creation with all state fields.
        /// </summary>
        [Fact]
        public void CreateTimeJumpEvent_FieldsCorrect()
        {
            var evt = TimeJumpManager.CreateTimeJumpEvent(
                1000.0, 2000.0, -0.0972, -74.5575, 67.0, 100.5f, 200.3f, 50.1f);

            Assert.Equal(SegmentEventType.TimeJump, evt.type);
            Assert.Equal(2000.0, evt.ut);
            Assert.NotNull(evt.details);
            Assert.Contains("preUT=", evt.details);
            Assert.Contains("postUT=", evt.details);
            Assert.Contains("lat=", evt.details);
            Assert.Contains("lon=", evt.details);
            Assert.Contains("alt=", evt.details);
            Assert.Contains("vx=", evt.details);
            Assert.Contains("vy=", evt.details);
            Assert.Contains("vz=", evt.details);
        }

        /// <summary>
        /// CreateTimeJumpEvent logs the event creation.
        /// Guards: diagnostic logging for TIME_JUMP events.
        /// </summary>
        [Fact]
        public void CreateTimeJumpEvent_Logs()
        {
            TimeJumpManager.CreateTimeJumpEvent(
                1000.0, 2000.0, 0, 0, 100, 0, 0, 0);

            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("TIME_JUMP event created"));
        }

        #endregion

        #region Serialization round-trip for TimeJump SegmentEvent

        /// <summary>
        /// TimeJump SegmentEvent survives serialization round-trip via RecordingStore.
        /// Guards: new enum value is correctly handled by existing serialization.
        /// </summary>
        [Fact]
        public void TimeJumpSegmentEvent_SerializationRoundTrip()
        {
            // Suppress RecordingStore logging for this test
            RecordingStore.SuppressLogging = true;
            try
            {
                var evt = TimeJumpManager.CreateTimeJumpEvent(
                    1000.0, 2000.0, -0.0972, -74.5575, 67.0, 100.5f, 200.3f, 50.1f);

                var events = new List<SegmentEvent> { evt };
                var node = new ConfigNode("ROOT");

                RecordingStore.SerializeSegmentEvents(node, events);

                var deserialized = new List<SegmentEvent>();
                RecordingStore.DeserializeSegmentEvents(node, deserialized);

                Assert.Single(deserialized);
                Assert.Equal(SegmentEventType.TimeJump, deserialized[0].type);
                Assert.Equal(evt.ut, deserialized[0].ut);
                Assert.Equal(evt.details, deserialized[0].details);
            }
            finally
            {
                RecordingStore.SuppressLogging = true;
            }
        }

        /// <summary>
        /// TimeJump enum value is 8, matching the design spec.
        /// Guards: enum value stability for backward compat.
        /// </summary>
        [Fact]
        public void TimeJumpEnumValue_Is8()
        {
            Assert.Equal(8, (int)SegmentEventType.TimeJump);
        }

        #endregion

        #region Log assertions

        /// <summary>
        /// IsValidJump logs its decision.
        /// </summary>
        [Fact]
        public void IsValidJump_Logs()
        {
            TimeJumpManager.IsValidJump(1000.0, 2000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("IsValidJump") && l.Contains("valid=True"));
        }

        /// <summary>
        /// ComputeJumpTargetUT logs the selected chain.
        /// </summary>
        [Fact]
        public void ComputeJumpTargetUT_Logs()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 42, MakeChain(42, 1600.0) }
            };

            TimeJumpManager.ComputeJumpTargetUT(chains, 42);

            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("pid=42") && l.Contains("spawnUT=1600"));
        }

        /// <summary>
        /// FindCrossedChainTips logs the count of crossed tips.
        /// </summary>
        [Fact]
        public void FindCrossedChainTips_LogsCrossedCount()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1500.0) },
                { 200, MakeChain(200, 1800.0) }
            };

            TimeJumpManager.FindCrossedChainTips(chains, 1000.0, 2000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("crossed=2"));
        }

        #endregion
    }
}
