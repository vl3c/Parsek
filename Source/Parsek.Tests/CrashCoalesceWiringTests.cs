using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Integration tests for the crash coalesce wiring: verifies that the
    /// classification→coalescer→breakup pipeline works correctly when
    /// SegmentBoundaryLogic and CrashCoalescer are composed together.
    /// </summary>
    [Collection("Sequential")]
    public class CrashCoalesceWiringTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrashCoalesceWiringTests()
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

        #region Classification → Coalescer pipeline

        [Fact]
        public void StructuralSplit_FedToCoalescer_EmitsBreakupAfterWindow()
        {
            // Simulate: joint break classified as StructuralSplit, fed to coalescer
            var coalescer = new CrashCoalescer();

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 1000, 2000 },
                anyNewVesselHasController: true);

            Assert.Equal(JointBreakResult.StructuralSplit, classification);

            // Feed to coalescer (as ParsekFlight would)
            bool childHasController = (classification == JointBreakResult.StructuralSplit);
            coalescer.OnSplitEvent(100.0, 2000, childHasController);

            // Window expires → BREAKUP emitted
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Breakup, bp.Type);
            Assert.Equal(0, bp.DebrisCount);
        }

        [Fact]
        public void DebrisSplit_FedToCoalescer_EmitsBreakupWithDebrisCount()
        {
            var coalescer = new CrashCoalescer();

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 1000, 3000 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.DebrisSplit, classification);

            bool childHasController = (classification == JointBreakResult.StructuralSplit);
            coalescer.OnSplitEvent(100.0, 3000, childHasController);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Breakup, bp.Type);
            Assert.Equal(1, bp.DebrisCount);
        }

        [Fact]
        public void WithinSegment_NotFedToCoalescer_CoalescerStaysIdle()
        {
            var coalescer = new CrashCoalescer();

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint>(),
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, classification);

            // WithinSegment should NOT be fed to coalescer
            // Coalescer should remain idle
            Assert.False(coalescer.HasPendingBreakup);
            Assert.Null(coalescer.Tick(100.5));
        }

        #endregion

        #region Rapid crash sequence (multiple splits coalesced)

        [Fact]
        public void RapidCrashSequence_MixedClassifications_SingleBreakup()
        {
            var coalescer = new CrashCoalescer();

            // First split: structural (controlled child)
            var c1 = SegmentBoundaryLogic.ClassifyJointBreakResult(
                1000, new List<uint> { 1000, 2000 }, anyNewVesselHasController: true);
            Assert.Equal(JointBreakResult.StructuralSplit, c1);
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true);

            // Second split: debris
            var c2 = SegmentBoundaryLogic.ClassifyJointBreakResult(
                1000, new List<uint> { 1000, 3000 }, anyNewVesselHasController: false);
            Assert.Equal(JointBreakResult.DebrisSplit, c2);
            coalescer.OnSplitEvent(100.1, 3000, childHasController: false);

            // Third split: another debris
            var c3 = SegmentBoundaryLogic.ClassifyJointBreakResult(
                1000, new List<uint> { 1000, 4000 }, anyNewVesselHasController: false);
            Assert.Equal(JointBreakResult.DebrisSplit, c3);
            coalescer.OnSplitEvent(100.2, 4000, childHasController: false);

            // Still within window
            Assert.Null(coalescer.Tick(100.3));

            // Verify accumulated state before window expires
            Assert.Single(coalescer.ControlledChildPids);
            Assert.Equal(2, coalescer.CurrentDebrisCount);

            // Window expires — single breakup
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Breakup, bp.Type);
            Assert.Equal(100.0, bp.UT);
            Assert.Equal(2, bp.DebrisCount); // 2 debris fragments (controlled child tracked separately)
        }

        #endregion

        #region Breakup BranchPoint wiring into tree

        [Fact]
        public void BreakupBranchPoint_CanBeWiredIntoTree()
        {
            // Create a minimal tree with an active recording
            var tree = new RecordingTree
            {
                Id = "tree1",
                TreeName = "TestVessel",
                RootRecordingId = "rec1"
            };
            var rec = new Recording
            {
                RecordingId = "rec1",
                TreeId = "tree1",
                VesselPersistentId = 1000,
                VesselName = "TestVessel"
            };
            tree.Recordings["rec1"] = rec;
            tree.ActiveRecordingId = "rec1";

            // Simulate coalescer emitting a BREAKUP
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            // Wire into tree (simulating ProcessBreakupEvent logic)
            bp.ParentRecordingIds.Add("rec1");
            tree.BranchPoints.Add(bp);
            rec.ChildBranchPointId = bp.Id;

            // Verify wiring
            Assert.Single(tree.BranchPoints);
            Assert.Equal(BranchPointType.Breakup, tree.BranchPoints[0].Type);
            Assert.Contains("rec1", tree.BranchPoints[0].ParentRecordingIds);
            Assert.Equal(bp.Id, rec.ChildBranchPointId);
        }

        [Fact]
        public void BreakupBranchPoint_HasCorrectMetadata()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: true, "OVERHEAT");
            coalescer.OnSplitEvent(100.1, 3000, childHasController: false, "OVERHEAT");
            coalescer.OnSplitEvent(100.2, 4000, childHasController: false, "OVERHEAT");

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);

            // Verify breakup metadata
            Assert.Equal("OVERHEAT", bp.BreakupCause);
            Assert.Equal(2, bp.DebrisCount);
            // Duration = last split (100.2) - first split (100.0) = 0.2
            Assert.Equal(0.2, bp.BreakupDuration, 10);
            Assert.Equal(CrashCoalescer.DefaultCoalesceWindow, bp.CoalesceWindow);
        }

        #endregion

        #region Coalescer reset on recording start

        [Fact]
        public void CoalescerReset_ClearsStateBeforeNewRecording()
        {
            var coalescer = new CrashCoalescer();

            // Simulate some accumulated state
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);
            Assert.True(coalescer.HasPendingBreakup);

            // Reset (as ParsekFlight.StartRecording does)
            coalescer.Reset();

            Assert.False(coalescer.HasPendingBreakup);
            Assert.Empty(coalescer.ControlledChildPids);
            Assert.Equal(0, coalescer.CurrentDebrisCount);

            // New events start fresh
            coalescer.OnSplitEvent(200.0, 5000, childHasController: true);
            var bp = coalescer.Tick(200.5);
            Assert.NotNull(bp);
            Assert.Equal(200.0, bp.UT);
            Assert.Equal(0, bp.DebrisCount);
        }

        #endregion

        #region Undock/EVA split data derived by BuildSplitBranchData

        // The old pair of cells here asserted that bp.Type equals the BranchPointType handed in
        // (a verbatim copy of the argument) and that a freshly constructed coalescer nobody fed
        // has no pending breakup. Both are tautologies, and the claimed "bypass" property is not
        // observable from this call at all. What IS derived by the call - and what a broken
        // undock / EVA split would corrupt - is the child wiring, so that is what is asserted.

        [Fact]
        public void UndockSplit_DerivesChildWiringFromTheSplit()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent1",
                treeId: "tree1",
                branchUT: 100.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 1000,
                activeVesselName: "Ship",
                backgroundVesselPid: 2000,
                backgroundVesselName: "Stage",
                parentGeneration: 3);

            Assert.Equal(BranchPointType.Undock, bp.Type);

            // The branch point names the parent and exactly the two children it built.
            Assert.Equal(new List<string> { "parent1" }, bp.ParentRecordingIds);
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.Contains(activeChild.RecordingId, bp.ChildRecordingIds);
            Assert.Contains(bgChild.RecordingId, bp.ChildRecordingIds);
            Assert.NotEqual(activeChild.RecordingId, bgChild.RecordingId);

            // Both children hang off this branch point, at the branch UT, in the same tree.
            Assert.Equal(bp.Id, activeChild.ParentBranchPointId);
            Assert.Equal(bp.Id, bgChild.ParentBranchPointId);
            Assert.Equal(100.0, activeChild.ExplicitStartUT, 3);
            Assert.Equal(100.0, bgChild.ExplicitStartUT, 3);
            Assert.Equal("tree1", activeChild.TreeId);
            Assert.Equal("tree1", bgChild.TreeId);

            // The active child continues the same logical vessel (parent generation); the
            // background spinoff is one generation deeper.
            Assert.Equal(3, activeChild.Generation);
            Assert.Equal(4, bgChild.Generation);
            Assert.Equal(1000u, activeChild.VesselPersistentId);
            Assert.Equal(2000u, bgChild.VesselPersistentId);

            // An undock carries no EVA wiring.
            Assert.Null(activeChild.EvaCrewName);
            Assert.Null(bgChild.EvaCrewName);
        }

        [Fact]
        public void EvaSplit_KerbalChildIsPickedByPid_BothDirections()
        {
            // The kerbal is the ACTIVE vessel: the active child carries the crew name and
            // points its ParentRecordingId at the ship child.
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent1",
                treeId: "tree1",
                branchUT: 100.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 1000,
                activeVesselName: "KerbalEVA",
                backgroundVesselPid: 2000,
                backgroundVesselName: "Ship",
                evaCrewName: "Jeb",
                evaVesselPid: 1000);

            Assert.Equal(BranchPointType.EVA, bp.Type);
            Assert.Equal("Jeb", activeChild.EvaCrewName);
            Assert.Equal(bgChild.RecordingId, activeChild.ParentRecordingId);
            Assert.Null(bgChild.EvaCrewName);

            // Mirror direction: the kerbal is the BACKGROUND vessel (an EVA from a vessel the
            // player is not flying), so the background child is the one that gets wired.
            var (_, activeChild2, bgChild2) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent1",
                treeId: "tree1",
                branchUT: 100.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 1000,
                activeVesselName: "Ship",
                backgroundVesselPid: 2000,
                backgroundVesselName: "KerbalEVA",
                evaCrewName: "Bill",
                evaVesselPid: 2000);

            Assert.Equal("Bill", bgChild2.EvaCrewName);
            Assert.Equal(activeChild2.RecordingId, bgChild2.ParentRecordingId);
            Assert.Null(activeChild2.EvaCrewName);
        }

        #endregion

        #region Log assertions for wiring

        [Fact]
        public void Log_StructuralSplit_ClassificationLogged()
        {
            // Simulate the classification + coalescer feed that ParsekFlight does
            var coalescer = new CrashCoalescer();
            uint recordedPid = 1000;
            var newVesselPids = new List<uint> { 1000, 2000 };
            bool anyNewVesselHasController = true;

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                recordedPid, newVesselPids, anyNewVesselHasController);

            Assert.Equal(JointBreakResult.StructuralSplit, classification);

            // Verify classification was logged
            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("StructuralSplit"));
        }

        [Fact]
        public void Log_DebrisSplit_ClassificationLogged()
        {
            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint> { 1000, 3000 },
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.DebrisSplit, classification);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("DebrisSplit"));
        }

        [Fact]
        public void Log_WithinSegment_ClassificationLogged()
        {
            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: new List<uint>(),
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, classification);

            Assert.Contains(logLines, l =>
                l.Contains("[Boundary]") &&
                l.Contains("WithinSegment"));
        }

        [Fact]
        public void Log_CoalescerWindowOpened_OnFirstSplitFeed()
        {
            var coalescer = new CrashCoalescer();

            // Classify and feed
            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                1000, new List<uint> { 1000, 2000 }, anyNewVesselHasController: false);
            Assert.Equal(JointBreakResult.DebrisSplit, classification);

            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Coalescer]") &&
                l.Contains("Coalescing window opened"));
        }

        [Fact]
        public void Log_BreakupEmitted_AfterWindowExpiry()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);
            coalescer.OnSplitEvent(100.1, 3000, childHasController: true);

            coalescer.Tick(100.5);

            Assert.Contains(logLines, l =>
                l.Contains("[Coalescer]") &&
                l.Contains("BREAKUP emitted") &&
                l.Contains("controlledChildren=1") &&
                l.Contains("debris=1"));
        }

        [Fact]
        public void Log_CoalescerReset_OnRecordingStart()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);

            // Reset as ParsekFlight.StartRecording does
            coalescer.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Coalescer]") &&
                l.Contains("Reset"));
        }

        #endregion

        #region Edge cases

        [Fact]
        public void EmptyNewVesselList_WithinSegment_NoCoalescerInteraction()
        {
            var coalescer = new CrashCoalescer();

            // Joint break that doesn't produce any new vessels
            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                originalVesselPid: 1000,
                postBreakVesselPids: null,
                anyNewVesselHasController: false);

            Assert.Equal(JointBreakResult.WithinSegment, classification);
            Assert.False(coalescer.HasPendingBreakup);
        }

        [Fact]
        public void MultipleBreakupSequences_IndependentlyProcessed()
        {
            var coalescer = new CrashCoalescer();

            // First breakup sequence
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);
            var bp1 = coalescer.Tick(100.5);
            Assert.NotNull(bp1);
            Assert.Equal(100.0, bp1.UT);

            // Second breakup sequence (well after first window expires)
            coalescer.OnSplitEvent(200.0, 5000, childHasController: true);
            coalescer.OnSplitEvent(200.1, 5001, childHasController: false);
            var bp2 = coalescer.Tick(200.5);
            Assert.NotNull(bp2);
            Assert.Equal(200.0, bp2.UT);
            Assert.Equal(1, bp2.DebrisCount);
        }

        [Fact]
        public void BreakupBP_HasNoChildRecordingIds_CallerMustFill()
        {
            // Verify that the coalescer emits BPs without child recording IDs
            // (ParsekFlight is responsible for filling them in)
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Empty(bp.ChildRecordingIds);
            Assert.Empty(bp.ParentRecordingIds);
        }

        [Fact]
        public void CoalescerTickBeforeWindow_DoesNotEmit()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, childHasController: false);

            // Multiple ticks within window all return null
            Assert.Null(coalescer.Tick(100.1));
            Assert.Null(coalescer.Tick(100.2));
            Assert.Null(coalescer.Tick(100.3));
            Assert.Null(coalescer.Tick(100.4));

            // Window expires
            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
        }

        #endregion

        #region Multi-vessel split reporting

        [Fact]
        public void MultiVesselSplit_AllNewVesselsReportedToCoalescer()
        {
            // Simulate a 3-way split: 1 controlled + 2 debris (as ParsekFlight would loop)
            var coalescer = new CrashCoalescer();

            // Simulate the loop that ParsekFlight now does for each new vessel PID
            var newVesselPids = new List<uint> { 2000, 3000, 4000 };
            var hasController = new Dictionary<uint, bool>
            {
                { 2000, false },
                { 3000, true },
                { 4000, false }
            };

            double branchUT = 100.0;
            foreach (var pid in newVesselPids)
            {
                coalescer.OnSplitEvent(branchUT, pid, hasController[pid]);
            }

            // All 3 vessels should be counted
            Assert.Single(coalescer.ControlledChildPids);
            Assert.Equal(3000u, coalescer.ControlledChildPids[0]);
            Assert.Equal(2, coalescer.CurrentDebrisCount);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            Assert.Equal(2, bp.DebrisCount);

            // Controlled child PID preserved in snapshot
            Assert.Single(coalescer.LastEmittedControlledChildPids);
            Assert.Equal(3000u, coalescer.LastEmittedControlledChildPids[0]);
        }

        [Fact]
        public void MultiVesselSplit_BreakupDuration_ReflectsActualSpan()
        {
            // Multi-vessel split at different UTs — duration should be actual breakup span
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(100.0, 2000, false);
            coalescer.OnSplitEvent(100.15, 3000, false);
            coalescer.OnSplitEvent(100.3, 4000, true);

            var bp = coalescer.Tick(100.5);
            Assert.NotNull(bp);
            // Duration = 100.3 - 100.0 = 0.3 (not 0.5 which includes idle time)
            Assert.Equal(0.3, bp.BreakupDuration, 10);
        }

        #endregion
    }
}
