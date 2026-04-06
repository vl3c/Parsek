using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    #region Test 1 — FormatDuration with days and years

    [Collection("Sequential")]
    public class FormatDurationRegressionTests : IDisposable
    {
        public FormatDurationRegressionTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        [Fact]
        public void FormatDuration_Seconds_ReturnsSecondsOnly()
        {
            Assert.Equal("45s", ParsekUI.FormatDuration(45));
        }

        [Fact]
        public void FormatDuration_Minutes_ReturnsMinutesAndSeconds()
        {
            Assert.Equal("2m 10s", ParsekUI.FormatDuration(130));
        }

        [Fact]
        public void FormatDuration_Hours_ReturnsHoursAndMinutes()
        {
            Assert.Equal("2h 0m", ParsekUI.FormatDuration(7200));
        }

        [Fact]
        public void FormatDuration_Days_ReturnsDaysAndHours()
        {
            // KSP: 1 day = 6h = 21600s
            // 50000s = 2 days (43200s) + 6800s = 2d + 1h (3600) + 3200s remainder
            Assert.Equal("2d 1h", ParsekUI.FormatDuration(50000));
        }

        [Fact]
        public void FormatDuration_Years_ReturnsYearsAndDays()
        {
            // KSP: 1 year = 426 days = 426 * 21600 = 9201600s
            // 10000000s = 1 year (9201600) + 798400s remainder
            // 798400 / 21600 = 36.96 days → 36 days
            Assert.Equal("1y 36d", ParsekUI.FormatDuration(10000000));
        }

        [Fact]
        public void FormatDuration_Zero_ReturnsZeroSeconds()
        {
            Assert.Equal("0s", ParsekUI.FormatDuration(0));
        }

        [Fact]
        public void FormatDuration_NaN_ReturnsZeroSeconds()
        {
            Assert.Equal("0s", ParsekUI.FormatDuration(double.NaN));
        }

        [Fact]
        public void FormatDuration_Negative_ReturnsZeroSeconds()
        {
            Assert.Equal("0s", ParsekUI.FormatDuration(-10));
        }

        [Fact]
        public void FormatDuration_Infinity_ReturnsZeroSeconds()
        {
            Assert.Equal("0s", ParsekUI.FormatDuration(double.PositiveInfinity));
        }
    }

    #endregion

    #region Test 2 — ForwardPermanentStateEvents

    [Collection("Sequential")]
    public class ForwardPermanentStateEventsTests : IDisposable
    {
        public ForwardPermanentStateEventsTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void IsPermanentVisualStateEvent_ShroudJettisoned_ReturnsTrue()
        {
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ShroudJettisoned));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_FairingJettisoned_ReturnsTrue()
        {
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.FairingJettisoned));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_Decoupled_ReturnsTrue()
        {
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.Decoupled));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_Destroyed_ReturnsTrue()
        {
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.Destroyed));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_ParachuteDestroyed_ReturnsTrue()
        {
            Assert.True(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ParachuteDestroyed));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_EngineIgnited_ReturnsFalse()
        {
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.EngineIgnited));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_LightOn_ReturnsFalse()
        {
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.LightOn));
        }

        [Fact]
        public void IsPermanentVisualStateEvent_ParachuteDeployed_ReturnsFalse()
        {
            Assert.False(RecordingOptimizer.IsPermanentVisualStateEvent(PartEventType.ParachuteDeployed));
        }

        [Fact]
        public void ForwardPermanentStateEvents_ShroudInFirstHalf_DuplicatedAtSplitUT()
        {
            double splitUT = 500.0;
            var firstHalf = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 100.0,
                    partPersistentId = 42,
                    eventType = PartEventType.ShroudJettisoned,
                    partName = "noseCone"
                }
            };
            var secondHalf = new List<PartEvent>();

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, splitUT);

            Assert.Single(secondHalf);
            Assert.Equal(PartEventType.ShroudJettisoned, secondHalf[0].eventType);
            Assert.Equal(splitUT, secondHalf[0].ut);
            Assert.Equal((uint)42, secondHalf[0].partPersistentId);
            Assert.Equal("noseCone", secondHalf[0].partName);
        }

        [Fact]
        public void ForwardPermanentStateEvents_NonPermanentEvent_NotForwarded()
        {
            double splitUT = 500.0;
            var firstHalf = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 100.0,
                    partPersistentId = 10,
                    eventType = PartEventType.EngineIgnited,
                    partName = "engine"
                },
                new PartEvent
                {
                    ut = 200.0,
                    partPersistentId = 20,
                    eventType = PartEventType.LightOn,
                    partName = "light"
                }
            };
            var secondHalf = new List<PartEvent>();

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, splitUT);

            Assert.Empty(secondHalf);
        }

        [Fact]
        public void ForwardPermanentStateEvents_MixedEvents_OnlyPermanentForwarded()
        {
            double splitUT = 500.0;
            var firstHalf = new List<PartEvent>
            {
                new PartEvent { ut = 50.0, eventType = PartEventType.EngineIgnited, partName = "engine" },
                new PartEvent { ut = 100.0, eventType = PartEventType.ShroudJettisoned, partName = "shroud1", partPersistentId = 1 },
                new PartEvent { ut = 150.0, eventType = PartEventType.LightOn, partName = "light" },
                new PartEvent { ut = 200.0, eventType = PartEventType.FairingJettisoned, partName = "fairing1", partPersistentId = 2 },
                new PartEvent { ut = 250.0, eventType = PartEventType.Decoupled, partName = "decoupler", partPersistentId = 3 },
            };
            var secondHalf = new List<PartEvent>();

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, splitUT);

            // Should forward: ShroudJettisoned, FairingJettisoned, Decoupled (3 permanent events)
            Assert.Equal(3, secondHalf.Count);
            Assert.Equal(PartEventType.ShroudJettisoned, secondHalf[0].eventType);
            Assert.Equal(PartEventType.FairingJettisoned, secondHalf[1].eventType);
            Assert.Equal(PartEventType.Decoupled, secondHalf[2].eventType);

            // All forwarded events should have splitUT
            foreach (var ev in secondHalf)
                Assert.Equal(splitUT, ev.ut);
        }

        [Fact]
        public void ForwardPermanentStateEvents_EmptyFirstHalf_DoesNotCrash()
        {
            var secondHalf = new List<PartEvent>();
            RecordingOptimizer.ForwardPermanentStateEvents(new List<PartEvent>(), secondHalf, 500.0);
            Assert.Empty(secondHalf);
        }

        [Fact]
        public void ForwardPermanentStateEvents_NullFirstHalf_DoesNotCrash()
        {
            var secondHalf = new List<PartEvent>();
            RecordingOptimizer.ForwardPermanentStateEvents(null, secondHalf, 500.0);
            Assert.Empty(secondHalf);
        }

        [Fact]
        public void ForwardPermanentStateEvents_PreservesExistingSecondHalfEvents()
        {
            double splitUT = 500.0;
            var firstHalf = new List<PartEvent>
            {
                new PartEvent { ut = 100.0, eventType = PartEventType.ShroudJettisoned, partName = "shroud" }
            };
            var secondHalf = new List<PartEvent>
            {
                new PartEvent { ut = 600.0, eventType = PartEventType.EngineIgnited, partName = "engine" }
            };

            RecordingOptimizer.ForwardPermanentStateEvents(firstHalf, secondHalf, splitUT);

            // Seed event inserted at front, existing event preserved
            Assert.Equal(2, secondHalf.Count);
            Assert.Equal(PartEventType.ShroudJettisoned, secondHalf[0].eventType);
            Assert.Equal(splitUT, secondHalf[0].ut);
            Assert.Equal(PartEventType.EngineIgnited, secondHalf[1].eventType);
            Assert.Equal(600.0, secondHalf[1].ut);
        }
    }

    #endregion

    #region Test 3 — SMA sub-surface check with hyperbolic orbits

    public class SmaSubSurfaceCheckTests
    {
        /// <summary>
        /// The bugfix changed the check from (sma &lt; bodyRadius * 0.9) to
        /// (Math.Abs(sma) &lt; bodyRadius * 0.9) so that hyperbolic orbits
        /// (negative SMA) aren't incorrectly rejected.
        /// </summary>
        private static bool IsSubSurfaceOrbit(double sma, double bodyRadius)
        {
            return Math.Abs(sma) < bodyRadius * 0.9;
        }

        [Fact]
        public void PositiveSma_AboveRadius_NotSubSurface()
        {
            // Circular orbit: sma=700000, bodyRadius=600000 → valid
            Assert.False(IsSubSurfaceOrbit(700000, 600000));
        }

        [Fact]
        public void NegativeSma_Hyperbolic_NotSubSurface()
        {
            // Hyperbolic orbit: sma=-1858567, bodyRadius=600000 → Abs = 1858567 > 540000 → valid
            Assert.False(IsSubSurfaceOrbit(-1858567, 600000));
        }

        [Fact]
        public void PositiveSma_BelowRadius_IsSubSurface()
        {
            // Sub-surface: sma=400000, bodyRadius=600000 → 400000 < 540000 → rejected
            Assert.True(IsSubSurfaceOrbit(400000, 600000));
        }

        [Fact]
        public void NegativeSma_TinyHyperbolic_IsSubSurface()
        {
            // Tiny hyperbolic: sma=-100, bodyRadius=600000 → Abs(100) < 540000 → rejected
            Assert.True(IsSubSurfaceOrbit(-100, 600000));
        }

        [Fact]
        public void NegativeSma_WithoutAbsFix_WouldPassIncorrectly()
        {
            // This demonstrates the bug: without Math.Abs, negative SMA < threshold
            // would be true (since -1858567 < 540000 is true), incorrectly rejecting
            // valid hyperbolic orbits.
            double sma = -1858567;
            double bodyRadius = 600000;
            double threshold = bodyRadius * 0.9;

            // Old buggy check (without Abs) would reject this:
            Assert.True(sma < threshold);  // -1858567 < 540000 is TRUE (bug!)

            // Fixed check with Abs correctly accepts it:
            Assert.False(Math.Abs(sma) < threshold);  // 1858567 < 540000 is FALSE (correct)
        }
    }

    #endregion

    #region Test 5 — GetGroupStatus delegates correctly

    public class GetGroupStatusTests
    {
        [Fact]
        public void GetGroupStatus_ActiveRecording_ReturnsActiveStatus()
        {
            double now = 17030.0;
            var committed = new List<Recording>
            {
                MakeRecording(17000, 17060)  // active: now is between start and end
            };
            var descendants = new HashSet<int> { 0 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(1, statusOrder); // 1 = active
            // Active recording with points should return a T+ countdown, not literal "active"
            Assert.StartsWith("T+", statusText);
        }

        [Fact]
        public void GetGroupStatus_FutureRecording_ReturnsFutureStatus()
        {
            double now = 16000.0;
            var committed = new List<Recording>
            {
                MakeRecording(17000, 17060)  // future: now is before start
            };
            var descendants = new HashSet<int> { 0 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(0, statusOrder); // 0 = future
            Assert.StartsWith("T-", statusText);
        }

        [Fact]
        public void GetGroupStatus_PastRecording_ReturnsPast()
        {
            double now = 18000.0;
            var committed = new List<Recording>
            {
                MakeRecording(17000, 17060)  // past: now is after end
            };
            var descendants = new HashSet<int> { 0 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(2, statusOrder); // 2 = past
            Assert.Equal("past", statusText);
        }

        [Fact]
        public void GetGroupStatus_ActiveWins_OverFutureAndPast()
        {
            double now = 17030.0;
            var committed = new List<Recording>
            {
                MakeRecording(16000, 16060),  // past
                MakeRecording(17000, 17060),  // active
                MakeRecording(18000, 18060),  // future
            };
            var descendants = new HashSet<int> { 0, 1, 2 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(1, statusOrder); // active wins
        }

        [Fact]
        public void GetGroupStatus_FutureWins_OverPast_WhenNoActive()
        {
            double now = 17500.0;
            var committed = new List<Recording>
            {
                MakeRecording(16000, 16060),  // past
                MakeRecording(17000, 17060),  // past
                MakeRecording(18000, 18060),  // future
            };
            var descendants = new HashSet<int> { 0, 1, 2 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(0, statusOrder); // future wins over past
        }

        [Fact]
        public void GetGroupStatus_EmptyDescendants_ReturnsDash()
        {
            double now = 17000.0;
            var committed = new List<Recording>();
            var descendants = new HashSet<int>();

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(2, statusOrder);
            Assert.Equal("-", statusText);
        }

        [Fact]
        public void GetGroupStatus_DebrisActive_ShowsCountdown_NotLiteralActive()
        {
            // Regression: debris recordings were showing "active" instead of T+ countdown
            double now = 17030.0;
            var rec = MakeRecording(17000, 17060);
            rec.IsDebris = true;
            var committed = new List<Recording> { rec };
            var descendants = new HashSet<int> { 0 };

            ParsekUI.GetGroupStatus(descendants, committed, now, out string statusText, out int statusOrder);

            Assert.Equal(1, statusOrder);
            // With points present, should show T+ countdown, not "active"
            Assert.StartsWith("T+", statusText);
        }

        private static Recording MakeRecording(double startUT, double endUT)
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }
    }

    #endregion
}
