using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #362: terminal crash-end decouple fragments can still collapse
    /// to <see cref="JointBreakResult.WithinSegment"/> and skip a final debris branch.
    ///
    /// At terminal crash time, KSP has already destroyed the fragment <c>GameObject</c>s
    /// before <c>DeferredJointBreakCheck</c> runs. Unity's overloaded <c>Object ==</c>
    /// operator makes <c>v == null</c> true for every destroyed fragment, so the old
    /// <c>List&lt;Vessel&gt;</c>-based filter dropped them all and the classifier saw
    /// zero new vessels, returning <see cref="JointBreakResult.WithinSegment"/> and
    /// silently losing the final debris branch.
    ///
    /// The fix iterates the PID-keyed <c>decoupleControllerStatus</c> dictionary instead.
    /// Its keys are plain managed <c>uint</c>s that survive terminal destruction, so the
    /// classifier sees the real fragment PIDs and correctly returns
    /// <see cref="JointBreakResult.DebrisSplit"/>.
    ///
    /// These tests cover the pure PID-collection helper
    /// <see cref="SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids"/> and
    /// its interaction with <see cref="SegmentBoundaryLogic.ClassifyJointBreakResult"/>.
    /// The <c>ParsekFlight</c> coroutine integration requires Unity runtime and is out
    /// of scope for unit tests.
    /// </summary>
    [Collection("Sequential")]
    public class Bug362TerminalCrashDebrisTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug362TerminalCrashDebrisTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_TerminalDestroyedFragments_ProducesDebrisSplit()
        {
            // The #362 regression pin: two uncontrolled fragments captured synchronously
            // during recording. Their live Vessel references are gone by the deferred
            // check frame (terminal crash). The helper must still collect both PIDs
            // from the PID-keyed dict so the classifier returns DebrisSplit.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 456u, false },
                { 789u, false }
            };
            var backgroundMap = new Dictionary<uint, string>();
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Equal(2, newVesselPids.Count);
            Assert.Contains(456u, newVesselPids);
            Assert.Contains(789u, newVesselPids);
            Assert.False(anyNewVesselHasController);
            Assert.False(newVesselHasController[456u]);
            Assert.False(newVesselHasController[789u]);

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                recordedPid, newVesselPids, anyNewVesselHasController);
            Assert.Equal(JointBreakResult.DebrisSplit, classification);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_FiltersRecordedPid()
        {
            // The recorded vessel PID must be skipped even if it appears in the dict
            // (defensive — the synchronous capture path should not put it there, but
            // the helper must be robust if something else ever does).
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { recordedPid, true },
                { 456u, false }
            };
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap: null,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Single(newVesselPids);
            Assert.Contains(456u, newVesselPids);
            Assert.DoesNotContain(recordedPid, newVesselPids);
            Assert.False(anyNewVesselHasController);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_FiltersBackgroundMapPids()
        {
            // PIDs already tracked in the tree's BackgroundMap must be skipped —
            // they are already owned by some other recording in the same tree.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 456u, false },
                { 789u, false },
                { 999u, true }
            };
            var backgroundMap = new Dictionary<uint, string>
            {
                { 789u, "bg-recording-id" }
            };
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Equal(2, newVesselPids.Count);
            Assert.Contains(456u, newVesselPids);
            Assert.Contains(999u, newVesselPids);
            Assert.DoesNotContain(789u, newVesselPids);
            Assert.True(anyNewVesselHasController);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_DedupesExistingNewVesselPids()
        {
            // If a PID was already added (e.g., by a previous call or a different
            // capture path), it must not be duplicated in newVesselPids, and
            // newVesselHasController must not be clobbered with stale data from
            // the current dict iteration for the already-known PID.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 2000u, false },
                { 3000u, false }
            };
            var newVesselPids = new List<uint> { 2000u };
            var newVesselHasController = new Dictionary<uint, bool> { { 2000u, true } };

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap: null,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            // 2000 stays as one entry; 3000 is added.
            Assert.Equal(2, newVesselPids.Count);
            Assert.Contains(2000u, newVesselPids);
            Assert.Contains(3000u, newVesselPids);
            // Existing 2000 entry is not overwritten (it was pre-populated true).
            Assert.True(newVesselHasController[2000u]);
            // 3000 is freshly added from the dict (false).
            Assert.False(newVesselHasController[3000u]);
            // Only 3000 was new, and it is uncontrolled, so anyNew flag stays false.
            Assert.False(anyNewVesselHasController);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_ControlledChild_SetsAnyController()
        {
            // One fragment has a controller (e.g., still-intact command pod).
            // The helper must set anyNewVesselHasController and end-to-end
            // classification must be StructuralSplit, not DebrisSplit.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 456u, true }
            };
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap: null,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Single(newVesselPids);
            Assert.Contains(456u, newVesselPids);
            Assert.True(anyNewVesselHasController);
            Assert.True(newVesselHasController[456u]);

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                recordedPid, newVesselPids, anyNewVesselHasController);
            Assert.Equal(JointBreakResult.StructuralSplit, classification);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_EmptyInput_PreservesWithinSegmentClassification()
        {
            // An empty dict must not populate anything and must not set
            // anyNewVesselHasController. The downstream classifier should then
            // correctly return WithinSegment.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>();
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap: null,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Empty(newVesselPids);
            Assert.Empty(newVesselHasController);
            Assert.False(anyNewVesselHasController);

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                recordedPid, newVesselPids, anyNewVesselHasController);
            Assert.Equal(JointBreakResult.WithinSegment, classification);
        }

        [Fact]
        public void CollectSynchronouslyCapturedNewVesselPids_NullBackgroundMap_DoesNotThrow()
        {
            // A null backgroundMap parameter must be treated as "nothing to skip",
            // not as a null-ref hazard. DeferredJointBreakCheck passes
            // activeTree?.BackgroundMap, so activeTree == null is a real call shape.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 456u, false },
                { 789u, false }
            };
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap: null,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Equal(2, newVesselPids.Count);
            Assert.Contains(456u, newVesselPids);
            Assert.Contains(789u, newVesselPids);
            Assert.False(anyNewVesselHasController);
        }

        [Fact]
        public void Bug362_TerminalCrashFragments_DebrisSplitRegression()
        {
            // Integration mirror of the KSP.log evidence from
            // logs/2026-04-14_1954_kerbal-x-f5f9-fix-verify:
            //   - recordedPid 123  (synthetic "Kerbal X")
            //   - fragment pid 456 (parachuteLarge, uncontrolled)
            //   - fragment pid 789 (HeatShield2,   uncontrolled)
            // Before the fix, decoupleCreatedVessels iteration dropped both entries
            // because their destroyed GameObjects compared equal to null, giving
            // newVesselPids.Count == 0 and the classifier returned WithinSegment.
            // The PID-based helper must restore the correct DebrisSplit outcome.
            const uint recordedPid = 123u;
            var decoupleControllerStatus = new Dictionary<uint, bool>
            {
                { 456u, false }, // parachuteLarge
                { 789u, false }  // HeatShield2
            };
            var backgroundMap = new Dictionary<uint, string>();
            var newVesselPids = new List<uint>();
            var newVesselHasController = new Dictionary<uint, bool>();

            SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                recordedPid,
                decoupleControllerStatus,
                backgroundMap,
                newVesselPids,
                newVesselHasController,
                out bool anyNewVesselHasController);

            Assert.Equal(2, newVesselPids.Count);
            Assert.Contains(456u, newVesselPids);
            Assert.Contains(789u, newVesselPids);
            Assert.False(anyNewVesselHasController);

            var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                recordedPid, newVesselPids, anyNewVesselHasController);
            Assert.Equal(JointBreakResult.DebrisSplit, classification);
        }
    }
}
