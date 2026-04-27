using System;
using System.Collections.Generic;
using UnityEngine;
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

        #region BreakupDuration correct (actual breakup span, not idle window)

        [Fact]
        public void BreakupDuration_SingleSplit_IsZero()
        {
            // Single split: lastSplitUT == windowStartUT, so duration = 0
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);

            var bp = coalescer.Tick(100.7);
            Assert.NotNull(bp);
            Assert.Equal(0.0, bp.BreakupDuration, 10);
        }

        [Fact]
        public void BreakupDuration_MultipleSplits_IsLastMinusFirst()
        {
            // Duration should be the actual breakup span (last split - first split),
            // NOT the idle window time (tick - first split).
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.OnSplitEvent(100.2, 1001, false);
            coalescer.OnSplitEvent(100.4, 1002, false);

            var bp = coalescer.Tick(100.6);
            Assert.NotNull(bp);
            // Duration = 100.4 - 100.0 = 0.4 (actual breakup span)
            Assert.Equal(0.4, bp.BreakupDuration, 10);
        }

        [Fact]
        public void BreakupDuration_DoesNotIncludeIdleWindowTime()
        {
            // Even with a long idle gap after the last split,
            // duration only measures the actual breakup span.
            var coalescer = new CrashCoalescer(window: 2.0);
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.OnSplitEvent(100.3, 1001, false);

            // Tick at 102.0 (2.0s after window start), but last split was at 100.3
            var bp = coalescer.Tick(102.0);
            Assert.NotNull(bp);
            // Duration = 100.3 - 100.0 = 0.3 (not 2.0)
            Assert.Equal(0.3, bp.BreakupDuration, 10);
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
                l.Contains("[Coalescer]") && l.Contains("Coalescing window opened") &&
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
                l.Contains("[Coalescer]") && l.Contains("BREAKUP emitted") &&
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
                l.Contains("[Coalescer]") && l.Contains("Controlled child added") &&
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
                l.Contains("[Coalescer]") && l.Contains("Debris fragment added"));
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
                l.Contains("[Coalescer]") && l.Contains("Reset"));
        }

        [Fact]
        public void Log_Reset_LoggedOnWindowExpiry()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, false);
            coalescer.Tick(100.5);

            // Reset is called internally when window expires
            Assert.Contains(logLines, l =>
                l.Contains("[Coalescer]") && l.Contains("Reset"));
        }

        #endregion

        #region BranchPoint.ToString for Breakup

        [Fact]
        public void BreakupBranchPoint_ToString_IncludesTypeAndId()
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
            Assert.Contains("test123", str);
        }

        [Fact]
        public void NonBreakupBranchPoint_ToString_IncludesTypeAndId()
        {
            var bp = new BranchPoint
            {
                Id = "test456",
                UT = 200.0,
                Type = BranchPointType.Undock,
            };

            string str = bp.ToString();
            Assert.Contains("Undock", str);
            Assert.Contains("test456", str);
        }

        #endregion

        #region Multi-vessel split (all children counted)

        [Fact]
        public void MultiVesselSplit_AllChildrenReported()
        {
            // Simulate a 3-way split producing 2 debris + 1 controlled child
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);
            coalescer.OnSplitEvent(100.0, 3000, childHasController: true);
            coalescer.OnSplitEvent(100.0, 4000, childHasController: false);

            Assert.Equal(2, coalescer.CurrentDebrisCount);
            Assert.Single(coalescer.ControlledChildPids);
            Assert.Equal(3000u, coalescer.ControlledChildPids[0]);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(2, bp.DebrisCount);
        }

        [Fact]
        public void MultiVesselSplit_SameUT_AllCountedSeparately()
        {
            // All splits at the same UT (single frame) should each be counted
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, childHasController: false);
            coalescer.OnSplitEvent(100.0, 1001, childHasController: false);
            coalescer.OnSplitEvent(100.0, 1002, childHasController: false);

            Assert.Equal(3, coalescer.CurrentDebrisCount);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(3, bp.DebrisCount);
            // Single split at UT=100.0, so duration = 0
            Assert.Equal(0.0, bp.BreakupDuration, 10);
        }

        #endregion

        #region LastEmittedControlledChildPids

        [Fact]
        public void LastEmittedControlledChildPids_AvailableAfterTick()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true);
            coalescer.OnSplitEvent(100.1, 3000, childHasController: true);
            coalescer.OnSplitEvent(100.2, 4000, childHasController: false);

            // Before Tick, ControlledChildPids has the live data
            Assert.Equal(2, coalescer.ControlledChildPids.Count);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            // After Tick + Reset, live ControlledChildPids is cleared
            Assert.Empty(coalescer.ControlledChildPids);

            // But LastEmittedControlledChildPids preserves the snapshot
            Assert.Equal(2, coalescer.LastEmittedControlledChildPids.Count);
            Assert.Equal(2000u, coalescer.LastEmittedControlledChildPids[0]);
            Assert.Equal(3000u, coalescer.LastEmittedControlledChildPids[1]);
        }

        [Fact]
        public void LastEmittedControlledChildPids_EmptyWhenNoControlledChildren()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, childHasController: false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Empty(coalescer.LastEmittedControlledChildPids);
        }

        [Fact]
        public void LastEmittedControlledChildPids_OverwrittenByNextBreakup()
        {
            var coalescer = new CrashCoalescer();

            // First breakup with controlled child
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true);
            coalescer.Tick(100.5);
            Assert.Single(coalescer.LastEmittedControlledChildPids);
            Assert.Equal(2000u, coalescer.LastEmittedControlledChildPids[0]);

            // Second breakup with different controlled child
            coalescer.OnSplitEvent(200.0, 5000, childHasController: true);
            coalescer.Tick(200.5);
            Assert.Single(coalescer.LastEmittedControlledChildPids);
            Assert.Equal(5000u, coalescer.LastEmittedControlledChildPids[0]);
        }

        #endregion

        #region LastEmittedDebrisPids

        [Fact]
        public void LastEmittedDebrisPids_AvailableAfterTick()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 1000, childHasController: false);
            coalescer.OnSplitEvent(100.1, 1001, childHasController: false);
            coalescer.OnSplitEvent(100.2, 1002, childHasController: true); // controlled, not debris

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            // Debris PIDs should contain 1000 and 1001 but NOT 1002 (controlled)
            Assert.Equal(2, coalescer.LastEmittedDebrisPids.Count);
            Assert.Equal(1000u, coalescer.LastEmittedDebrisPids[0]);
            Assert.Equal(1001u, coalescer.LastEmittedDebrisPids[1]);
        }

        [Fact]
        public void LastEmittedDebrisPids_ControlledPidsNotIncluded()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true);
            coalescer.OnSplitEvent(100.1, 3000, childHasController: true);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            Assert.Empty(coalescer.LastEmittedDebrisPids);
            Assert.Equal(2, coalescer.LastEmittedControlledChildPids.Count);
        }

        [Fact]
        public void LastEmittedDebrisPids_PreservedAfterReset()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 4000, childHasController: false);
            coalescer.OnSplitEvent(100.1, 4001, childHasController: false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            // After Tick + internal Reset, LastEmitted is preserved
            Assert.False(coalescer.HasPendingBreakup);
            Assert.Equal(2, coalescer.LastEmittedDebrisPids.Count);
            Assert.Equal(4000u, coalescer.LastEmittedDebrisPids[0]);
            Assert.Equal(4001u, coalescer.LastEmittedDebrisPids[1]);
        }

        [Fact]
        public void LastEmittedDebrisPids_FourBoosters_AllTracked()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 5000, childHasController: false);
            coalescer.OnSplitEvent(100.0, 5001, childHasController: false);
            coalescer.OnSplitEvent(100.0, 5002, childHasController: false);
            coalescer.OnSplitEvent(100.0, 5003, childHasController: false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            Assert.Equal(4, bp.DebrisCount);
            Assert.Equal(4, coalescer.LastEmittedDebrisPids.Count);
            Assert.Equal(5000u, coalescer.LastEmittedDebrisPids[0]);
            Assert.Equal(5001u, coalescer.LastEmittedDebrisPids[1]);
            Assert.Equal(5002u, coalescer.LastEmittedDebrisPids[2]);
            Assert.Equal(5003u, coalescer.LastEmittedDebrisPids[3]);
        }

        [Fact]
        public void LastEmittedDebrisPids_OverwrittenByNextBreakup()
        {
            var coalescer = new CrashCoalescer();

            // First breakup with 2 debris
            coalescer.OnSplitEvent(100.0, 1000, childHasController: false);
            coalescer.OnSplitEvent(100.1, 1001, childHasController: false);
            coalescer.Tick(100.5);
            Assert.Equal(2, coalescer.LastEmittedDebrisPids.Count);

            // Second breakup with 1 different debris
            coalescer.OnSplitEvent(200.0, 9000, childHasController: false);
            coalescer.Tick(200.5);
            Assert.Single(coalescer.LastEmittedDebrisPids);
            Assert.Equal(9000u, coalescer.LastEmittedDebrisPids[0]);
        }

        [Fact]
        public void Log_DebrisAdded_IncludesPid()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 7777, childHasController: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Coalescer]") && l.Contains("Debris fragment added") &&
                l.Contains("pid=7777"));
        }

        #endregion

        #region Pre-captured snapshots (#157)

        [Fact]
        public void PreCapturedSnapshot_SurvivedThroughEmission()
        {
            var coalescer = new CrashCoalescer(0.1);
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", "Debris Fragment");

            coalescer.OnSplitEvent(100.0, 42, false, preSnapshot: snapshot);

            // Before emission — snapshot not accessible via LastEmitted
            Assert.Null(coalescer.GetPreCapturedSnapshot(42));

            // Tick past window
            var bp = coalescer.Tick(100.2);
            Assert.NotNull(bp);

            // Now accessible via GetPreCapturedSnapshot
            var retrieved = coalescer.GetPreCapturedSnapshot(42);
            Assert.NotNull(retrieved);
            Assert.Equal("Debris Fragment", retrieved.GetValue("name"));
        }

        [Fact]
        public void PreCapturedSnapshot_NullWhenNotProvided()
        {
            var coalescer = new CrashCoalescer(0.1);
            coalescer.OnSplitEvent(100.0, 42, false);
            coalescer.Tick(100.2);

            Assert.Null(coalescer.GetPreCapturedSnapshot(42));
        }

        [Fact]
        public void PreCapturedSnapshot_ClearedOnReset()
        {
            var coalescer = new CrashCoalescer(0.1);
            var snapshot = new ConfigNode("VESSEL");
            coalescer.OnSplitEvent(100.0, 42, false, preSnapshot: snapshot);
            coalescer.Tick(100.2);

            // Verify it exists after emission
            Assert.NotNull(coalescer.GetPreCapturedSnapshot(42));

            // Start a new window — lastEmitted is overwritten on next emission
            coalescer.OnSplitEvent(200.0, 99, false);
            coalescer.Tick(200.2);

            // Old snapshot should be gone (overwritten by new emission)
            Assert.Null(coalescer.GetPreCapturedSnapshot(42));
        }

        [Fact]
        public void PreCapturedSnapshot_MultipleDebris()
        {
            var coalescer = new CrashCoalescer(0.5);
            var snap1 = new ConfigNode("VESSEL");
            snap1.AddValue("name", "Debris1");
            var snap2 = new ConfigNode("VESSEL");
            snap2.AddValue("name", "Debris2");

            coalescer.OnSplitEvent(100.0, 10, false, preSnapshot: snap1);
            coalescer.OnSplitEvent(100.1, 20, false, preSnapshot: snap2);
            coalescer.Tick(100.6);

            Assert.Equal("Debris1", coalescer.GetPreCapturedSnapshot(10)?.GetValue("name"));
            Assert.Equal("Debris2", coalescer.GetPreCapturedSnapshot(20)?.GetValue("name"));
            Assert.Null(coalescer.GetPreCapturedSnapshot(99)); // not added
        }

        [Fact]
        public void PreCapturedTrajectoryPoint_SurvivesThroughEmission()
        {
            var coalescer = new CrashCoalescer(0.1);
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            coalescer.OnSplitEvent(100.0, 42, false, preTrajectoryPoint: point);

            Assert.Null(coalescer.GetPreCapturedTrajectoryPoint(42));

            var bp = coalescer.Tick(100.2);
            Assert.NotNull(bp);

            TrajectoryPoint? retrieved = coalescer.GetPreCapturedTrajectoryPoint(42);
            Assert.True(retrieved.HasValue);
            Assert.Equal(point.ut, retrieved.Value.ut, 6);
            Assert.Equal(point.altitude, retrieved.Value.altitude, 6);
            Assert.Equal(point.velocity, retrieved.Value.velocity);
        }

        [Fact]
        public void PreCapturedTrajectoryPoint_ClearedOnNextEmission()
        {
            var coalescer = new CrashCoalescer(0.1);
            var point = new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 345.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(1f, 2f, 3f),
                bodyName = "Kerbin"
            };

            coalescer.OnSplitEvent(100.0, 42, false, preTrajectoryPoint: point);
            coalescer.Tick(100.2);
            Assert.True(coalescer.GetPreCapturedTrajectoryPoint(42).HasValue);

            coalescer.OnSplitEvent(200.0, 99, false);
            coalescer.Tick(200.2);

            Assert.Null(coalescer.GetPreCapturedTrajectoryPoint(42));
        }

        #endregion

        #region ShouldSkipDeadOnArrivalControlledChild (todo item 20)

        [Fact]
        public void ShouldSkipDeadOnArrivalControlledChild_DeadAndNoSnapshot_ReturnsTrue()
        {
            // Regression: 2026-04-25_1047 playtest "Unknown 0s recording".
            // When the controllable child's live Vessel is gone AND no
            // pre-captured snapshot is available, ProcessBreakupEvent must
            // skip recording creation. Otherwise the table grows a 1-point
            // "Unknown" row with no playback or replay value.
            Assert.True(ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                childVesselIsAlive: false, hasPreCapturedSnapshot: false));
        }

        [Fact]
        public void ShouldSkipDeadOnArrivalControlledChild_DeadButHasSnapshot_ReturnsTrue()
        {
            // Regression: 2026-04-26_1332 Re-Fly merge created a single-point
            // "Unknown" recording from a controlled child whose live vessel
            // was already gone. A pre-captured snapshot is not enough to make
            // that dead-on-arrival child a useful controllable recording.
            Assert.True(ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                childVesselIsAlive: false, hasPreCapturedSnapshot: true));
        }

        [Fact]
        public void ShouldSkipDeadOnArrivalControlledChild_AliveNoSnapshot_ReturnsFalse()
        {
            // Regression: an alive vessel with no pre-captured snapshot will
            // still get its snapshot built from the live Vessel by
            // SeedBreakupChildSnapshots. The skip must not fire here.
            Assert.False(ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                childVesselIsAlive: true, hasPreCapturedSnapshot: false));
        }

        [Fact]
        public void ShouldSkipDeadOnArrivalControlledChild_AliveWithSnapshot_ReturnsFalse()
        {
            // Regression: the normal happy path — live vessel + pre-captured
            // snapshot — must always produce a recording.
            Assert.False(ParsekFlight.ShouldSkipDeadOnArrivalControlledChild(
                childVesselIsAlive: true, hasPreCapturedSnapshot: true));
        }

        #endregion
    }
}
