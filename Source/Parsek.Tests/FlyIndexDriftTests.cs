using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure unit tests for the Tracking Station "Fly" ghost-index-drift fix
    /// (BUG #1). <see cref="GhostMapPresence.ComputeFlyIndexDrift"/> is the
    /// detector that quantifies the off-by amount stock
    /// <c>FlightDriver.StartAndFocusVessel</c> would suffer if the ghost map
    /// vessels were stripped from the saved persistent.sfs (the current
    /// StripFromSave behaviour) but NOT from the live FlightGlobals.Vessels list.
    /// The drift is exactly the count of ghost pids at a live index strictly
    /// less than the target's live index. The method is Unity-free and tested
    /// here with synthetic pid lists; the live-list strip orchestration
    /// (<see cref="GhostMapPresence.RemoveAllGhostVesselsBeforeStockFly"/>) is
    /// covered indirectly via the pure formatter below and by the in-game
    /// ts-runtime-canary tests.
    /// </summary>
    public class FlyIndexDriftTests
    {
        private static ISet<uint> Ghosts(params uint[] pids)
        {
            return new HashSet<uint>(pids);
        }

        private static IReadOnlyList<uint> Live(params uint[] pids)
        {
            return new List<uint>(pids);
        }

        // (a) Reproduced-session shape: 5 ghosts all at indices < target.
        // Player clicked Fly on "Depot" with 5 ghosts pre-created ahead of it;
        // the index drift that placed them in "Kerbal X Probe" was exactly 5.
        [Fact]
        public void FiveGhostsAllBeforeTarget_DriftIsFive()
        {
            // Live list: 5 ghosts (front-loaded as GhostMapPresence registers
            // them ahead of the real vessels) then the real target.
            var live = Live(11u, 12u, 13u, 14u, 15u, 4277041026u);
            var ghosts = Ghosts(11u, 12u, 13u, 14u, 15u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 4277041026u);

            Assert.Equal(5, drift);
        }

        // (b) Target at index 0, ghosts after it -> no ghost precedes the
        // target, so the Fly would have landed correctly even without the fix.
        [Fact]
        public void TargetAtIndexZero_GhostsAfter_DriftIsZero()
        {
            var live = Live(900u, 11u, 12u, 13u);
            var ghosts = Ghosts(11u, 12u, 13u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 900u);

            Assert.Equal(0, drift);
        }

        // (c) Ghosts interleaved (some before, some after target) -> only the
        // ones strictly before the target count toward the drift.
        [Fact]
        public void GhostsInterleaved_DriftCountsOnlyGhostsBeforeTarget()
        {
            // indices: 0=ghost 1=ghost 2=real-other 3=TARGET 4=ghost 5=ghost
            var live = Live(11u, 12u, 800u, 900u, 13u, 14u);
            var ghosts = Ghosts(11u, 12u, 13u, 14u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 900u);

            Assert.Equal(2, drift);
        }

        // (d) No ghosts in the live list -> drift 0.
        [Fact]
        public void NoGhostsInLiveList_DriftIsZero()
        {
            var live = Live(800u, 900u, 1000u);
            var ghosts = Ghosts(); // empty

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 900u);

            Assert.Equal(0, drift);
        }

        // (e) Target pid absent from the live list -> sentinel -1 (defensive:
        // another mod removed it before the strip ran).
        [Fact]
        public void TargetAbsentFromLiveList_ReturnsSentinel()
        {
            var live = Live(11u, 12u, 800u);
            var ghosts = Ghosts(11u, 12u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 999u);

            Assert.Equal(GhostMapPresence.FlyIndexDriftTargetNotInLiveList, drift);
            Assert.Equal(-1, drift);
        }

        // (f) Empty live list -> sentinel -1 (target cannot be present).
        [Fact]
        public void EmptyLiveList_ReturnsSentinel()
        {
            var live = Live();
            var ghosts = Ghosts(11u, 12u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 900u);

            Assert.Equal(GhostMapPresence.FlyIndexDriftTargetNotInLiveList, drift);
        }

        // (g) Empty ghost set but target present -> drift 0 (nothing precedes).
        [Fact]
        public void EmptyGhostSet_TargetPresent_DriftIsZero()
        {
            var live = Live(800u, 900u);
            var ghosts = Ghosts(); // empty

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 800u);

            Assert.Equal(0, drift);
        }

        // Defensive null inputs: a null live list returns the sentinel; a null
        // ghost set with the target present is treated as "no ghosts" (drift 0).
        [Fact]
        public void NullLiveList_ReturnsSentinel()
        {
            int drift = GhostMapPresence.ComputeFlyIndexDrift(null, Ghosts(11u), 900u);
            Assert.Equal(GhostMapPresence.FlyIndexDriftTargetNotInLiveList, drift);
        }

        [Fact]
        public void NullGhostSet_TargetPresent_DriftIsZero()
        {
            var live = Live(800u, 900u);
            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, null, 900u);
            Assert.Equal(0, drift);
        }

        // Target appears first when duplicated: ComputeFlyIndexDrift uses the
        // FIRST live occurrence of the target pid (KSP pids are craft-baked and
        // can collide across historical recordings; IndexOf likewise returns the
        // first match), so the drift is measured against that index.
        [Fact]
        public void DuplicateTargetPid_UsesFirstOccurrence()
        {
            // indices: 0=ghost 1=TARGET(first) 2=ghost 3=TARGET(dup)
            var live = Live(11u, 900u, 12u, 900u);
            var ghosts = Ghosts(11u, 12u);

            int drift = GhostMapPresence.ComputeFlyIndexDrift(live, ghosts, 900u);

            // Only the single ghost at index 0 precedes the first target match.
            Assert.Equal(1, drift);
        }
    }

    /// <summary>
    /// Log-capture tests for the pure diagnostic formatter
    /// <see cref="GhostMapPresence.FormatFlyStripDiagnostic"/> that the TS Fly
    /// pre-stock strip emits via <c>ParsekLog.Info("SwitchIntentPatch", ...)</c>.
    /// Asserts the emitted line carries the [SwitchIntentPatch] tag and the
    /// load-bearing diagnostic fields (targetPid / ghostCount /
    /// liveVesselsBefore / flyIndexDriftAvoided) with the expected numbers. The
    /// live-list strip orchestration itself touches FlightGlobals and is covered
    /// by the in-game ts-runtime-canary tests; this pins the message shape the
    /// orchestration logs.
    /// </summary>
    [Collection("Sequential")]
    public class FlyStripDiagnosticLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlyStripDiagnosticLogTests()
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

        [Fact]
        public void FormatFlyStripDiagnostic_ContainsAllFields()
        {
            string body = GhostMapPresence.FormatFlyStripDiagnostic(
                targetPid: 4277041026u,
                ghostCount: 5,
                liveVesselsBefore: 11,
                flyIndexDriftAvoided: 5);

            Assert.Contains("TS Fly pre-stock ghost strip", body);
            Assert.Contains("targetPid=4277041026", body);
            Assert.Contains("ghostCount=5", body);
            Assert.Contains("liveVesselsBefore=11", body);
            Assert.Contains("flyIndexDriftAvoided=5", body);
        }

        [Fact]
        public void Info_WithFlyStripDiagnostic_EmitsSwitchIntentPatchTaggedLine()
        {
            // Round-trip the formatter through the same ParsekLog.Info call the
            // orchestrator uses, so the [SwitchIntentPatch] tag wrap is asserted.
            ParsekLog.Info("SwitchIntentPatch",
                GhostMapPresence.FormatFlyStripDiagnostic(
                    targetPid: 4277041026u,
                    ghostCount: 5,
                    liveVesselsBefore: 11,
                    flyIndexDriftAvoided: 5));

            Assert.Contains(logLines, l =>
                l.Contains("[SwitchIntentPatch]")
                && l.Contains("TS Fly pre-stock ghost strip")
                && l.Contains("targetPid=4277041026")
                && l.Contains("ghostCount=5")
                && l.Contains("liveVesselsBefore=11")
                && l.Contains("flyIndexDriftAvoided=5"));
        }

        [Fact]
        public void FormatFlyStripDiagnostic_DriftZeroCase_StillReportsZero()
        {
            // drift==0 means the Fly would have landed correctly even without the
            // fix; the diagnostic still reports it so the no-drift real-vessel
            // Fly is observable.
            string body = GhostMapPresence.FormatFlyStripDiagnostic(
                targetPid: 900u,
                ghostCount: 3,
                liveVesselsBefore: 9,
                flyIndexDriftAvoided: 0);

            Assert.Contains("flyIndexDriftAvoided=0", body);
            Assert.Contains("ghostCount=3", body);
        }
    }
}
