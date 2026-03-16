using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CrashCoalescerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrashCoalescerTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Initial state

        [Fact]
        public void InitialState_HasPendingBreakupIsFalse()
        {
            var coalescer = new CrashCoalescer();
            Assert.False(coalescer.HasPendingBreakup);
        }

        [Fact]
        public void InitialState_TickReturnsNull()
        {
            var coalescer = new CrashCoalescer();
            Assert.Null(coalescer.Tick(100.0));
        }

        #endregion

        #region Single split, window expiry

        [Fact]
        public void SingleSplit_TickWithinWindow_ReturnsNull()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            // 0.3s into 0.5s window -- should NOT emit
            Assert.Null(coalescer.Tick(100.3));
            Assert.True(coalescer.HasPendingBreakup);
        }

        [Fact]
        public void SingleSplit_TickAfterWindow_ReturnsBreakup()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            // Exactly at window boundary
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Breakup, bp.Type);
            Assert.Equal(100.0, bp.UT);
            Assert.False(coalescer.HasPendingBreakup);
        }

        #endregion

        #region Controlled vs debris classification

        [Fact]
        public void SingleControlledChild_CorrectCounts()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true);

            Assert.Single(coalescer.ControlledChildPids);
            Assert.Equal(2000u, coalescer.ControlledChildPids[0]);
            Assert.Equal(0, coalescer.CurrentDebrisCount);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(0, bp.DebrisCount);
        }

        [Fact]
        public void SingleDebris_CorrectCounts()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 3000, childHasController: false);

            Assert.Empty(coalescer.ControlledChildPids);
            Assert.Equal(1, coalescer.CurrentDebrisCount);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(1, bp.DebrisCount);
        }

        #endregion

        #region Rapid splits (coalescing)

        [Fact]
        public void RapidSplits_ThreeInWindow_SingleBreakup()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.OnSplitEvent(100.1, 1001, false);
            coalescer.OnSplitEvent(100.2, 1002, true);

            // Still within window at T+0.3
            Assert.Null(coalescer.Tick(100.3));

            // Window expires
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Breakup, bp.Type);
            Assert.Equal(100.0, bp.UT);
            Assert.Equal(2, bp.DebrisCount);
        }

        [Fact]
        public void MixedControlledAndDebris_CorrectCounts()
        {
            var coalescer = new CrashCoalescer();
            // 2 controlled + 3 debris
            coalescer.OnSplitEvent(100.0, 1000, childHasController: true);
            coalescer.OnSplitEvent(100.05, 1001, childHasController: false);
            coalescer.OnSplitEvent(100.1, 1002, childHasController: true);
            coalescer.OnSplitEvent(100.15, 1003, childHasController: false);
            coalescer.OnSplitEvent(100.2, 1004, childHasController: false);

            Assert.Equal(2, coalescer.ControlledChildPids.Count);
            Assert.Equal(3, coalescer.CurrentDebrisCount);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(3, bp.DebrisCount);
        }

        #endregion

        #region Window boundary

        [Fact]
        public void WindowBoundary_JustBelow_ReturnsNull()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            // 0.499s is strictly less than 0.5s window
            Assert.Null(coalescer.Tick(100.499));
        }

        [Fact]
        public void WindowBoundary_Exact_ReturnsBreakup()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            // Exactly at 0.5s boundary (>= check)
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
        }

        #endregion

        #region Reset between uses

        [Fact]
        public void ResetBetweenUses_SecondBreakupHasNoLeftoverState()
        {
            var coalescer = new CrashCoalescer();

            // First breakup: 2 debris, cause CRASH
            coalescer.OnSplitEvent(100.0, 1000, false, "CRASH");
            coalescer.OnSplitEvent(100.1, 1001, false, "CRASH");
            var bp1 = coalescer.Tick(100.5);
            Assert.NotNull(bp1);
            Assert.Equal(2, bp1.DebrisCount);
            Assert.Equal("CRASH", bp1.BreakupCause);

            // After Tick emits, coalescer is reset automatically.
            // Start fresh: 1 controlled child, cause OVERHEAT
            coalescer.OnSplitEvent(200.0, 2000, true, "OVERHEAT");
            var bp2 = coalescer.Tick(200.5);
            Assert.NotNull(bp2);
            Assert.Equal(200.0, bp2.UT);
            Assert.Equal(0, bp2.DebrisCount);
            Assert.Equal("OVERHEAT", bp2.BreakupCause);
        }

        [Fact]
        public void ManualReset_ClearsAllState()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, true);
            Assert.True(coalescer.HasPendingBreakup);

            coalescer.Reset();

            Assert.False(coalescer.HasPendingBreakup);
            Assert.Empty(coalescer.ControlledChildPids);
            Assert.Equal(0, coalescer.CurrentDebrisCount);
            Assert.Null(coalescer.Tick(200.0));
        }

        #endregion

        #region No splits, just Tick

        [Fact]
        public void NoSplits_RepeatedTick_AlwaysNull()
        {
            var coalescer = new CrashCoalescer();
            Assert.Null(coalescer.Tick(100.0));
            Assert.Null(coalescer.Tick(200.0));
            Assert.Null(coalescer.Tick(300.0));
        }

        #endregion

        #region BreakupCause preserved

        [Fact]
        public void BreakupCause_OverheatPreserved()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false, "OVERHEAT");

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal("OVERHEAT", bp.BreakupCause);
        }

        [Fact]
        public void BreakupCause_StructuralFailurePreserved()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false, "STRUCTURAL_FAILURE");

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal("STRUCTURAL_FAILURE", bp.BreakupCause);
        }

        [Fact]
        public void BreakupCause_DefaultIsCrash()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false); // no explicit cause

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal("CRASH", bp.BreakupCause);
        }

        #endregion

        #region CoalesceWindow in result

        [Fact]
        public void CoalesceWindow_MatchesDefaultConstructor()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(CrashCoalescer.DefaultCoalesceWindow, bp.CoalesceWindow);
        }

        [Fact]
        public void CoalesceWindow_MatchesCustomConstructor()
        {
            var coalescer = new CrashCoalescer(window: 1.5);
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(101.5);
            Assert.NotNull(bp);
            Assert.Equal(1.5, bp.CoalesceWindow);
        }

        #endregion

        #region BreakupDuration correct

        [Fact]
        public void BreakupDuration_EqualsTickMinusFirstSplit()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(100.7);
            Assert.NotNull(bp);
            Assert.Equal(0.7, bp.BreakupDuration, 10);
        }

        [Fact]
        public void BreakupDuration_MultipleSplits_MeasuredFromFirst()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.OnSplitEvent(100.2, 1001, false);
            coalescer.OnSplitEvent(100.4, 1002, false);

            var bp = coalescer.Tick(100.6);
            Assert.NotNull(bp);
            // Duration = 100.6 - 100.0 = 0.6
            Assert.Equal(0.6, bp.BreakupDuration, 10);
        }

        #endregion

        #region Custom window size

        [Fact]
        public void CustomWindow_LargerWindow_DoesNotExpireEarly()
        {
            var coalescer = new CrashCoalescer(window: 1.0);
            coalescer.OnSplitEvent(100.0, 1000, false);

            // At 0.5s with default window this would expire, but not with 1.0s window
            Assert.Null(coalescer.Tick(100.5));
            Assert.Null(coalescer.Tick(100.9));

            // Expires at 1.0s
            var bp = coalescer.Tick(101.0);
            Assert.NotNull(bp);
        }

        #endregion

        #region BranchPoint fields

        [Fact]
        public void EmittedBranchPoint_HasValidId()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.NotNull(bp.Id);
            Assert.NotEmpty(bp.Id);
        }

        [Fact]
        public void EmittedBranchPoint_HasEmptyChildAndParentIds()
        {
            // Child/parent recording IDs are filled in by the caller, not the coalescer
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Empty(bp.ParentRecordingIds);
            Assert.Empty(bp.ChildRecordingIds);
        }

        #endregion

        #region Log assertions: window opened

        [Fact]
        public void Log_WindowOpened_OnFirstSplit()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false, "CRASH");

            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("Coalescing window opened") &&
                l.Contains("cause=CRASH"));
        }

        [Fact]
        public void Log_WindowOpened_NotOnSubsequentSplit()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false, "CRASH");
            int openedCountBefore = 0;
            foreach (var l in logLines)
                if (l.Contains("Coalescing window opened"))
                    openedCountBefore++;

            coalescer.OnSplitEvent(100.1, 1001, false, "CRASH");
            int openedCountAfter = 0;
            foreach (var l in logLines)
                if (l.Contains("Coalescing window opened"))
                    openedCountAfter++;

            // Should not have logged another "window opened"
            Assert.Equal(openedCountBefore, openedCountAfter);
        }

        #endregion

        #region Log assertions: BREAKUP emitted

        [Fact]
        public void Log_BreakupEmitted_ContainsControlledAndDebrisCounts()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, true);
            coalescer.OnSplitEvent(100.1, 1001, false);
            coalescer.OnSplitEvent(100.2, 1002, false);

            coalescer.Tick(100.5);

            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("BREAKUP emitted") &&
                l.Contains("controlledChildren=1") && l.Contains("debris=2"));
        }

        #endregion

        #region Log assertions: controlled child added

        [Fact]
        public void Log_ControlledChildAdded_LoggedWithPid()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 5555, childHasController: true);

            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("Controlled child added") &&
                l.Contains("pid=5555"));
        }

        #endregion

        #region Log assertions: debris added

        [Fact]
        public void Log_DebrisAdded_Logged()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 6666, childHasController: false);

            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("Debris fragment added"));
        }

        #endregion

        #region Log assertions: reset

        [Fact]
        public void Log_Reset_LoggedOnManualReset()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("Reset"));
        }

        [Fact]
        public void Log_Reset_LoggedOnWindowExpiry()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.Tick(100.5);

            // Reset is called internally when window expires
            Assert.Contains(logLines, l =>
                l.Contains("[CrashCoalescer]") && l.Contains("Reset"));
        }

        #endregion

        #region BranchPoint.ToString for Breakup

        [Fact]
        public void BreakupBranchPoint_ToString_IncludesBreakupMetadata()
        {
            var bp = new BranchPoint
            {
                Id = "test123",
                UT = 100.0,
                Type = BranchPointType.Breakup,
                BreakupCause = "CRASH",
                DebrisCount = 3,
                BreakupDuration = 0.45
            };

            string str = bp.ToString();
            Assert.Contains("Breakup", str);
            Assert.Contains("cause=CRASH", str);
            Assert.Contains("debris=3", str);
        }

        [Fact]
        public void NonBreakupBranchPoint_ToString_DoesNotIncludeBreakupMetadata()
        {
            var bp = new BranchPoint
            {
                Id = "test456",
                UT = 200.0,
                Type = BranchPointType.Undock,
            };

            string str = bp.ToString();
            Assert.Contains("Undock", str);
            Assert.DoesNotContain("cause=", str);
            Assert.DoesNotContain("debris=", str);
        }

        #endregion
    }
}
