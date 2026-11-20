using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-18 (RULE 2): every separated vessel gets its own recording and
    /// ghost. Todos SPLIT-WINDOW-DROPPED-ON-RECORDER-START (staging then undock / EVA / dock
    /// inside the 0.5 s coalescing window), SAME-FRAME-SEPARATION-DROPPED (two separations in
    /// one frame) and SPLIT-WINDOW-MISSING-FROM-QUICKSAVE (F5 inside the window). The live
    /// handlers need KSP; these cells pin the coalescer mechanism and the pure decisions the
    /// flush reads. The wiring is pinned by <see cref="PendingSplitFlushWiringTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class SeparationNeverDroppedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SeparationNeverDroppedTests()
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

        // ---- the mechanism: a reset drops the window, an early emit keeps it ----

        [Fact]
        public void CoalescerReset_DropsTheWindow_EmitNowKeepsTheBooster()
        {
            // The defect: a recorder start inside the window called Reset() and the booster
            // was gone. The fix emits the window first; EmitNow yields the same branch point.
            var dropped = new CrashCoalescer();
            dropped.OnSplitEvent(100.0, 4242u, childHasController: false, splitCause: "DECOUPLE");
            dropped.Reset();
            Assert.False(dropped.HasPendingBreakup);
            Assert.Null(dropped.Tick(200.0));

            var kept = new CrashCoalescer();
            kept.OnSplitEvent(100.0, 4242u, childHasController: false, splitCause: "DECOUPLE");
            BranchPoint bp = kept.EmitNow();
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal(new[] { 4242u }, kept.LastEmittedDebrisPids);
        }

        [Fact]
        public void CoalescerWindow_UndockFedBesideADecouple_StaysOneSeparationBranch()
        {
            // A second undock in the same frame joins the window as an UNDOCK separation: the
            // window stays a JointBreak (not a breakup) and the satellite is a controlled child.
            var c = new CrashCoalescer();
            c.OnSplitEvent(50.0, 11u, childHasController: false, splitCause: "DECOUPLE");
            c.OnSplitEvent(50.0, 22u, childHasController: true, splitCause: "UNDOCK");
            BranchPoint bp = c.EmitNow();
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal(new[] { 22u }, c.LastEmittedControlledChildPids);
            Assert.Equal(new[] { 11u }, c.LastEmittedDebrisPids);
        }

        // ---- SelectBreakupParentRecorder ----

        [Fact]
        public void ParentRecorder_HintRecordingTheParentWins()
        {
            Assert.Equal(ParsekFlightSplitBrancher.BreakupParentRecorderChoice.Hint,
                ParsekFlightSplitBrancher.SelectBreakupParentRecorder(
                    parentVesselPid: 7u,
                    hintVesselPid: 7u, hasHint: true,
                    liveVesselPid: 0u, hasLive: false,
                    parkedVesselPid: 7u, hasParked: true));
        }

        [Fact]
        public void ParentRecorder_HintOfAnotherVessel_FallsBackToLive()
        {
            Assert.Equal(ParsekFlightSplitBrancher.BreakupParentRecorderChoice.Live,
                ParsekFlightSplitBrancher.SelectBreakupParentRecorder(
                    parentVesselPid: 7u,
                    hintVesselPid: 9u, hasHint: true,
                    liveVesselPid: 7u, hasLive: true,
                    parkedVesselPid: 0u, hasParked: false));
        }

        [Fact]
        public void ParentRecorder_NoLiveRecorder_UsesTheParkedSplitRecorder()
        {
            // A flush before a split branch runs while the split parked the live recorder.
            Assert.Equal(ParsekFlightSplitBrancher.BreakupParentRecorderChoice.Parked,
                ParsekFlightSplitBrancher.SelectBreakupParentRecorder(
                    parentVesselPid: 7u,
                    hintVesselPid: 0u, hasHint: false,
                    liveVesselPid: 0u, hasLive: false,
                    parkedVesselPid: 7u, hasParked: true));
        }

        [Fact]
        public void ParentRecorder_ParkedRecorderOfAnotherVessel_IsNone()
        {
            Assert.Equal(ParsekFlightSplitBrancher.BreakupParentRecorderChoice.None,
                ParsekFlightSplitBrancher.SelectBreakupParentRecorder(
                    parentVesselPid: 7u,
                    hintVesselPid: 0u, hasHint: false,
                    liveVesselPid: 0u, hasLive: false,
                    parkedVesselPid: 8u, hasParked: true));
        }

        [Fact]
        public void ParentRecorder_UnknownParentVessel_AcceptsTheHint()
        {
            Assert.Equal(ParsekFlightSplitBrancher.BreakupParentRecorderChoice.Hint,
                ParsekFlightSplitBrancher.SelectBreakupParentRecorder(
                    parentVesselPid: 0u,
                    hintVesselPid: 9u, hasHint: true,
                    liveVesselPid: 0u, hasLive: false,
                    parkedVesselPid: 0u, hasParked: false));
        }

        // ---- WireBreakupIntoTree / ShouldKeepLaterChildBranchLink ----

        [Fact]
        public void WireBreakup_ParentAlreadyEndsAtALaterBranch_KeepsThatLink()
        {
            // The switch-segment bind flushes under the prior recording after the segment's
            // branch point exists: the earlier breakup must not re-point the parent's link.
            var tree = new RecordingTree { Id = "t" };
            var parent = new Recording { RecordingId = "p", ChildBranchPointId = "switch" };
            tree.Recordings["p"] = parent;
            tree.BranchPoints.Add(new BranchPoint { Id = "switch", UT = 120.0 });
            var breakup = new BranchPoint { Id = "brk", UT = 100.0 };

            ParsekFlight.WireBreakupIntoTree(tree, "p", parent, breakup);

            Assert.Equal("switch", parent.ChildBranchPointId);
            Assert.Contains("p", breakup.ParentRecordingIds);
            Assert.Contains(breakup, tree.BranchPoints);
            Assert.Contains(logLines, l => l.Contains("[Coalescer]") && l.Contains("keeps its later child branch"));
        }

        [Fact]
        public void WireBreakup_ParentEndsAtAnEarlierBranch_RepointsTheLink()
        {
            var tree = new RecordingTree { Id = "t" };
            var parent = new Recording { RecordingId = "p", ChildBranchPointId = "old" };
            tree.Recordings["p"] = parent;
            tree.BranchPoints.Add(new BranchPoint { Id = "old", UT = 90.0 });
            var breakup = new BranchPoint { Id = "brk", UT = 100.0 };

            ParsekFlight.WireBreakupIntoTree(tree, "p", parent, breakup);

            Assert.Equal("brk", parent.ChildBranchPointId);
        }

        // ---- SelectUnprocessedDecoupleCaptures (decoupler + docking port in one frame) ----

        [Fact]
        public void UnprocessedCaptures_SkipRecordedBackgroundAndTheSplitsOwnVessels_Sorted()
        {
            var captured = new Dictionary<uint, bool>
            {
                { 50u, false }, // the booster: fed
                { 10u, true },  // the recorded vessel
                { 30u, true },  // a background member
                { 40u, false }, // the undock's own vessel
                { 20u, true },  // a controlled piece: fed
            };
            var bg = new Dictionary<uint, string> { { 30u, "rec-bg" } };

            List<uint> pids = ParsekFlight.SelectUnprocessedDecoupleCaptures(
                recordedPid: 10u, captured, bg,
                deferredJointBreakCheckInFlight: false,
                excludePidA: 40u, excludePidB: 0u);

            Assert.Equal(new[] { 20u, 50u }, pids);
        }

        [Fact]
        public void UnprocessedCaptures_WhileAJointBreakCheckOwnsThem_AreLeftAlone()
        {
            var captured = new Dictionary<uint, bool> { { 50u, false } };
            Assert.Empty(ParsekFlight.SelectUnprocessedDecoupleCaptures(
                10u, captured, null, deferredJointBreakCheckInFlight: true, 0u, 0u));
        }

        [Fact]
        public void UnprocessedCaptures_UnknownRecordedVessel_FeedsNothing()
        {
            var captured = new Dictionary<uint, bool> { { 50u, false } };
            Assert.Empty(ParsekFlight.SelectUnprocessedDecoupleCaptures(
                0u, captured, null, deferredJointBreakCheckInFlight: false, 0u, 0u));
        }

        // ---- ResolveConcurrentUndockChildPid (two undocks in one frame) ----

        [Fact]
        public void ConcurrentUndock_OfTheParkedRecordersVessel_FeedsTheLeavingVessel()
        {
            Assert.Equal(200u, ParsekFlightSplitBrancher.ResolveConcurrentUndockChildPid(
                parkedRecordedPid: 100u, oldPid: 100u, newPid: 200u));
        }

        [Fact]
        public void ConcurrentUndock_NotInvolvingTheParkedRecordersVessel_FeedsNothing()
        {
            Assert.Equal(0u, ParsekFlightSplitBrancher.ResolveConcurrentUndockChildPid(
                parkedRecordedPid: 100u, oldPid: 300u, newPid: 400u));
            Assert.Equal(0u, ParsekFlightSplitBrancher.ResolveConcurrentUndockChildPid(
                parkedRecordedPid: 0u, oldPid: 100u, newPid: 200u));
        }
    }
}
