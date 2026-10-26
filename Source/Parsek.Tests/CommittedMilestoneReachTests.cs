using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// WORLD-FIRST-REEARNED-AFTER-REWIND-PAID-AGAIN: a world-first the committed timeline
    /// credits at or before the clock is marked complete in stock (no reward, no event), even
    /// while the KSP patch is deferred under a live recorder and when the flight clock passes
    /// the row at 1x, so a flight that re-earns it after a rewind is not paid by stock again.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedMilestoneReachTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedMilestoneReachTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CommittedMilestoneReach.ResetForTesting();
        }

        public void Dispose()
        {
            CommittedMilestoneReach.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.CurrentTimelineUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private class RecordsAltitude : ProgressNode
        {
            public RecordsAltitude() : base("RecordsAltitude", startReached: false) { }
        }

        private static GameAction MilestoneRow(string id, double ut, string recordingId = "rec-flight1")
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                MilestoneId = id,
                RecordingId = recordingId,
                MilestoneFundsAwarded = 1600f,
            };
        }

        private static FieldInfo ReachedField =>
            typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance);

        private static FieldInfo CompleteField =>
            typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void SetFlags(ProgressNode node, bool reached, bool complete)
        {
            ReachedField.SetValue(node, reached);
            CompleteField.SetValue(node, complete);
        }

        /// <summary>
        /// The WF-1 shape after the rewind: nothing reached. FirstLaunch, a Kerbin body
        /// subtree with FlagPlant / SurfaceEVA / Landing, a Mun subtree with Landing, and a
        /// world-record node.
        /// </summary>
        private sealed class Tree
        {
            internal readonly ProgressTree Root = new ProgressTree();
            internal readonly ProgressNode FirstLaunch = new ProgressNode("FirstLaunch", false);
            internal readonly ProgressNode Kerbin = new ProgressNode("Kerbin", false);
            internal readonly ProgressNode KerbinFlag = new ProgressNode("FlagPlant", false);
            internal readonly ProgressNode KerbinSurfaceEva = new ProgressNode("SurfaceEVA", false);
            internal readonly ProgressNode KerbinLanding = new ProgressNode("Landing", false);
            internal readonly ProgressNode Mun = new ProgressNode("Mun", false);
            internal readonly ProgressNode MunLanding = new ProgressNode("Landing", false);
            internal readonly ProgressNode Records = new RecordsAltitude();

            internal Tree()
            {
                Root.AddNode(FirstLaunch);
                Root.AddNode(Kerbin);
                Kerbin.Subtree.AddNode(KerbinFlag);
                Kerbin.Subtree.AddNode(KerbinSurfaceEva);
                Kerbin.Subtree.AddNode(KerbinLanding);
                Root.AddNode(Mun);
                Mun.Subtree.AddNode(MunLanding);
                Root.AddNode(Records);
            }
        }

        // ================================================================
        // Pure collectors
        // ================================================================

        [Fact]
        public void CollectCreditedAtOrBefore_TakesOnceEverRowsUpToTheClock()
        {
            var actions = new List<GameAction>
            {
                MilestoneRow("Kerbin/FlagPlant", 100.0),
                MilestoneRow("Kerbin/SurfaceEVA", 90.0),
                MilestoneRow("Kerbin/FlagPlant", 905.0, "rec-flight2"), // the zeroed duplicate
                MilestoneRow("Mun/Landing", 1500.0),
                MilestoneRow("RecordsAltitude", 80.0),
                new GameAction { Type = GameActionType.FundsEarning, UT = 50.0, RecordingId = "rec-flight1" },
                MilestoneRow("", 50.0),
                null,
            };

            HashSet<string> ids = CommittedMilestoneReach.CollectCreditedAtOrBefore(
                actions, 900.0, null, out int excluded);

            Assert.Equal(new HashSet<string> { "Kerbin/FlagPlant", "Kerbin/SurfaceEVA" }, ids);
            Assert.Equal(0, excluded);

            // A row exactly at the clock counts; a row after it waits.
            Assert.Contains("Kerbin/FlagPlant",
                CommittedMilestoneReach.CollectCreditedAtOrBefore(actions, 100.0, null, out _));
            Assert.Empty(CommittedMilestoneReach.CollectCreditedAtOrBefore(actions, 89.9, null, out _));
            Assert.Empty(CommittedMilestoneReach.CollectCreditedAtOrBefore(actions, double.NaN, null, out _));
        }

        [Fact]
        public void CollectCreditedAtOrBefore_SkipsTheReFlySuppressedSubtree()
        {
            // The origin being re-flown: a merge tombstones its milestone rows, so the
            // attempt must stay able to earn the node itself.
            var actions = new List<GameAction>
            {
                MilestoneRow("Kerbin/FlagPlant", 100.0, "rec-origin"),
                MilestoneRow("Kerbin/SurfaceEVA", 90.0, "rec-other"),
                MilestoneRow("FirstLaunch", 10.0, null), // untagged: never suppressed
            };
            var suppressed = new HashSet<string> { "rec-origin" };

            HashSet<string> ids = CommittedMilestoneReach.CollectCreditedAtOrBefore(
                actions, 900.0, suppressed, out int excluded);

            Assert.Equal(new HashSet<string> { "Kerbin/SurfaceEVA", "FirstLaunch" }, ids);
            Assert.Equal(1, excluded);
        }

        [Fact]
        public void NextRowUTAfter_IsTheEarliestOnceEverRowStrictlyAfter()
        {
            var actions = new List<GameAction>
            {
                MilestoneRow("Kerbin/FlagPlant", 100.0),
                MilestoneRow("Kerbin/SurfaceEVA", 90.0),
                MilestoneRow("RecordsAltitude", 70.0),
                new GameAction { Type = GameActionType.FundsEarning, UT = 60.0 },
            };

            Assert.Equal(90.0, CommittedMilestoneReach.NextRowUTAfter(actions, 45.0));
            Assert.Equal(100.0, CommittedMilestoneReach.NextRowUTAfter(actions, 90.0));
            Assert.True(double.IsPositiveInfinity(CommittedMilestoneReach.NextRowUTAfter(actions, 100.0)));
            Assert.True(double.IsPositiveInfinity(CommittedMilestoneReach.NextRowUTAfter(null, 0.0)));
        }

        [Fact]
        public void ResolveClockUT_TrustsARealCutoffThenTheLiveClockOutsideLoads()
        {
            Func<double> live = () => 950.0;

            Assert.Equal(900.0, CommittedMilestoneReach.ResolveClockUT(900.0, true, true, live));
            Assert.Equal(950.0, CommittedMilestoneReach.ResolveClockUT(null, false, false, live));
            // The walk-everything sentinel is not a clock.
            Assert.Equal(950.0, CommittedMilestoneReach.ResolveClockUT(double.MaxValue, false, false, live));
            Assert.True(double.IsNaN(CommittedMilestoneReach.ResolveClockUT(null, true, false, live)));
            Assert.True(double.IsNaN(CommittedMilestoneReach.ResolveClockUT(null, false, true, live)));
            Assert.True(double.IsNaN(CommittedMilestoneReach.ResolveClockUT(null, false, false, null)));
            // GetCurrentTimelineUTForLedgerRecalc reads double.MaxValue with no Planetarium.
            Assert.True(double.IsNaN(CommittedMilestoneReach.ResolveClockUT(
                null, false, false, () => double.MaxValue)));
            Assert.True(double.IsNaN(CommittedMilestoneReach.ResolveClockUT(null, false, false, () => 0.0)));
        }

        // ================================================================
        // Tree pass
        // ================================================================

        [Fact]
        public void MarkCreditedNodes_CompletesCreditedNodesAndTouchesNothingElse()
        {
            var t = new Tree();
            // Complete live but credited by nothing (the live flight's own uncommitted
            // earning): add-only, it stays complete.
            SetFlags(t.MunLanding, true, true);
            // Already complete and credited.
            SetFlags(t.FirstLaunch, true, true);

            var credited = new HashSet<string>
            {
                "Kerbin/FlagPlant", "Kerbin/SurfaceEVA", "FirstLaunch", "RecordsAltitude",
                "Landing", // a bare body-child id never matches a qualified node
            };
            var counts = new CommittedMilestoneReach.ReachCounts();
            var marked = new List<string>();

            CommittedMilestoneReach.MarkCreditedNodes(t.Root, credited, ReachedField, CompleteField,
                "", ref counts, marked);

            Assert.True(t.KerbinFlag.IsComplete);
            Assert.True(t.KerbinFlag.IsReached);
            Assert.True(t.KerbinSurfaceEva.IsComplete);
            Assert.False(t.KerbinLanding.IsComplete);
            Assert.False(t.KerbinLanding.IsReached);
            Assert.False(t.Kerbin.IsComplete); // the body node itself has no row
            Assert.True(t.MunLanding.IsComplete);
            Assert.False(t.Records.IsComplete);
            Assert.Equal(2, counts.Marked);
            Assert.Equal(1, counts.AlreadyComplete);
            Assert.Equal(1, counts.SkippedRecordNodes);
            Assert.Equal(new List<string> { "Kerbin/FlagPlant", "Kerbin/SurfaceEVA" }, marked);
        }

        [Fact]
        public void Apply_MarksTheWorldFirstsAtOrBeforeTheClockOnceAndLogsThem()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            var actions = new List<GameAction>
            {
                MilestoneRow("Kerbin/FlagPlant", 100.0),
                MilestoneRow("Kerbin/SurfaceEVA", 90.0),
                MilestoneRow("Mun/Landing", 1500.0),
            };

            int marked = CommittedMilestoneReach.Apply(actions, 900.0, "test");

            Assert.Equal(2, marked);
            Assert.True(t.KerbinFlag.IsComplete);
            Assert.True(t.KerbinSurfaceEva.IsComplete);
            Assert.False(t.MunLanding.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("marked 2 stock progress node(s) complete without reward")
                && l.Contains("reason=test")
                && l.Contains("ids=[Kerbin/FlagPlant, Kerbin/SurfaceEVA]"));

            Assert.Equal(0, CommittedMilestoneReach.Apply(actions, 900.0, "test"));
        }

        [Fact]
        public void Apply_WithoutATrustedClock_MarksNothing()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            var actions = new List<GameAction> { MilestoneRow("Kerbin/FlagPlant", 100.0) };

            Assert.Equal(0, CommittedMilestoneReach.Apply(actions, double.NaN, "test"));
            Assert.Equal(0, CommittedMilestoneReach.Apply(actions, double.MaxValue, "test"));

            Assert.False(t.KerbinFlag.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("Apply skipped: no trusted clock"));
        }

        [Fact]
        public void Apply_DuringAReFly_LeavesTheOriginsWorldFirstEarnable()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            CommittedMilestoneReach.SuppressedRecordingIdsProviderForTesting =
                () => new HashSet<string> { "rec-origin" };
            var actions = new List<GameAction>
            {
                MilestoneRow("Kerbin/FlagPlant", 100.0, "rec-origin"),
                MilestoneRow("Kerbin/SurfaceEVA", 90.0, "rec-other"),
            };

            Assert.Equal(1, CommittedMilestoneReach.Apply(actions, 900.0, "test"));

            Assert.False(t.KerbinFlag.IsComplete);
            Assert.True(t.KerbinSurfaceEva.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reFlySuppressedRows=1"));
        }

        // ================================================================
        // The deferred recalculation (the WF-1 warp to UT 900 under a live recorder)
        // ================================================================

        [Fact]
        public void TimeJumpRecalc_DeferredByALiveRecorder_StillMarksTheCrossedWorldFirsts()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();
            // Flight 1's committed world-firsts, a later one still ahead of the jump.
            Ledger.AddAction(MilestoneRow("Kerbin/SurfaceEVA", 95.0));
            Ledger.AddAction(MilestoneRow("Kerbin/FlagPlant", 100.0));
            Ledger.AddAction(MilestoneRow("Mun/Landing", 1500.0));

            LedgerOrchestrator.RecalculateAndPatchForTimeJump(900.0);

            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch") && l.Contains("live recorder active"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
            Assert.True(t.KerbinFlag.IsComplete);
            Assert.True(t.KerbinSurfaceEva.IsComplete);
            Assert.False(t.MunLanding.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reason=deferred-patch") && l.Contains("clock=900.0"));
        }

        [Fact]
        public void DeferredRecalc_WithoutACutoff_UsesTheLiveClock()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 120.0;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(MilestoneRow("Kerbin/FlagPlant", 100.0));
            Ledger.AddAction(MilestoneRow("Kerbin/SurfaceEVA", 130.0));

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.True(t.KerbinFlag.IsComplete);
            Assert.False(t.KerbinSurfaceEva.IsComplete);
        }

        // ================================================================
        // The flight clock cursor (a 1x crossing runs no recalc at all)
        // ================================================================

        [Fact]
        public void ClockCursor_AppliesOncePerCrossingAndReseedsOnABackwardClock()
        {
            var rows = new List<GameAction>
            {
                MilestoneRow("Kerbin/SurfaceEVA", 95.0),
                MilestoneRow("Kerbin/FlagPlant", 100.0),
            };
            var applied = new List<double>();
            var reasons = new List<string>();
            var cursor = new CommittedMilestoneClockCursor("flight",
                after => CommittedMilestoneReach.NextRowUTAfter(rows, after),
                (ut, why) => { applied.Add(ut); reasons.Add(why); });

            Assert.False(cursor.Advance(45.0));        // the first frame only seeds
            Assert.False(cursor.Advance(80.0));
            Assert.True(cursor.Advance(96.0));         // passes the UT 95 row
            Assert.False(cursor.Advance(99.0));
            Assert.True(cursor.Advance(101.0));        // passes the UT 100 row
            Assert.False(cursor.Advance(500.0));       // nothing left
            Assert.False(cursor.Advance(50.0));        // a load back in time reseeds
            Assert.True(cursor.Advance(120.0));        // and the rows cross again
            Assert.False(cursor.Advance(double.NaN));
            Assert.False(cursor.Advance(double.MaxValue));

            Assert.Equal(new List<double> { 96.0, 101.0, 120.0 }, applied);
            Assert.All(reasons, r => Assert.Equal("flight-clock", r));
        }

        [Fact]
        public void ClockCursor_DefaultApply_MarksTheNodeTheClockPassed()
        {
            var t = new Tree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(MilestoneRow("Kerbin/FlagPlant", 100.0));
            var cursor = new CommittedMilestoneClockCursor("flight");

            cursor.Advance(45.0);
            Assert.False(t.KerbinFlag.IsComplete);
            cursor.Advance(99.0);
            Assert.False(t.KerbinFlag.IsComplete);
            Assert.True(cursor.Advance(100.5));

            Assert.True(t.KerbinFlag.IsComplete);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reason=flight-clock") && l.Contains("ids=[Kerbin/FlagPlant]"));
        }

        // ================================================================
        // Why the observed leak was 2 x 1600 (the pool side of the defect)
        // ================================================================

        [Fact]
        public void StockPoolReference_TwoCrossedWorldFirstsRepaidByStock_ReadAsOneDoubleLeak()
        {
            // WF-1 flight 2: the pad EVA re-earns Kerbin/SurfaceEVA and the flag re-earns
            // Kerbin/FlagPlant; stock pays both standard rewards (the same multiplier for
            // FLAGPLANT and SURFACEEVA, so 1600 each). Flight 1's two rows crossed the clock
            // counted as not walked (NaN), so the reference does not step them, the zeroed
            // duplicates step nothing, and both payouts are one 3200 leak. With the nodes
            // marked complete stock pays nothing and there is no leak.
            var state = new StockPoolReferenceState(StockPool.Funds);
            var rowsBefore = new List<StockPoolRow>
            {
                new StockPoolRow { ActionId = "f1-eva", UT = 95.0, StockApplied = true, Effect = double.NaN },
                new StockPoolRow { ActionId = "f1-flag", UT = 100.0, StockApplied = true, Effect = double.NaN },
            };
            var baseline = StockPoolReference.Measure(state, rowsBefore, 45.0, 500000.0, 0.0,
                authoritative: true, epsilon: 0.01);
            StockPoolReference.Commit(state, rowsBefore, baseline, 500000.0, 0.0);

            var rowsAfter = new List<StockPoolRow>
            {
                new StockPoolRow { ActionId = "f1-eva", UT = 95.0, StockApplied = true, Effect = 1600.0 },
                new StockPoolRow { ActionId = "f1-flag", UT = 100.0, StockApplied = true, Effect = 1600.0 },
                new StockPoolRow { ActionId = "f2-eva", UT = 902.0, StockApplied = true, Effect = 0.0 },
                new StockPoolRow { ActionId = "f2-flag", UT = 905.0, StockApplied = true, Effect = 0.0 },
            };

            var repaid = StockPoolReference.Measure(state, rowsAfter, 909.0, 503200.0, 0.0,
                authoritative: false, epsilon: 0.01);
            Assert.True(repaid.LeakKept);
            Assert.Equal(3200.0, repaid.Leak, 6);

            var notRepaid = StockPoolReference.Measure(state, rowsAfter, 909.0, 500000.0, 0.0,
                authoritative: false, epsilon: 0.01);
            Assert.False(notRepaid.LeakKept);
            Assert.Equal(0.0, notRepaid.Adjustment);
        }
    }
}
