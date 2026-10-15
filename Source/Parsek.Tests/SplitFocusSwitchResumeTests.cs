using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo VN8-FOCUS-SWITCH-SPLIT-PARENT-LEG-RECORDS-NEW-VESSEL: when the control part leaves
    /// on the new vessel, stock switches focus to it before Parsek's deferred split check. The
    /// split recorder must then resume on the vessel it was recording, the pending split must be
    /// emitted while that recording is still the active one (so the new vessel gets its own
    /// child recording), and only then does focus hand over. The live path needs KSP; these
    /// cells pin the pure decisions, the coalescer's early emit, and the wiring.
    /// </summary>
    [Collection("Sequential")]
    public class SplitFocusSwitchResumeTests : IDisposable
    {
        private const string FlightPath = "ParsekFlight.cs";
        private const string RecorderPath = "FlightRecorder.cs";

        private readonly List<string> logLines = new List<string>();

        public SplitFocusSwitchResumeTests()
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

        // ---- ShouldResumeSplitOnRecordedVessel ----

        [Fact]
        public void ResumeOnRecordedVessel_FocusMovedToSplitChild_True()
        {
            // VN-8: recorded pod 2200110033 alive, stock focused the booster 3363353297.
            Assert.True(ParsekFlight.ShouldResumeSplitOnRecordedVessel(
                2200110033u, recordedVesselAlive: true, activeVesselPid: 3363353297u));
        }

        [Fact]
        public void ResumeOnRecordedVessel_FocusStayed_False()
        {
            Assert.False(ParsekFlight.ShouldResumeSplitOnRecordedVessel(
                2200110033u, recordedVesselAlive: true, activeVesselPid: 2200110033u));
        }

        [Fact]
        public void ResumeOnRecordedVessel_RecordedVesselGone_False()
        {
            // Nothing to resume on: keep the active-vessel resume (and its fallback commit).
            Assert.False(ParsekFlight.ShouldResumeSplitOnRecordedVessel(
                2200110033u, recordedVesselAlive: false, activeVesselPid: 3363353297u));
        }

        [Fact]
        public void ResumeOnRecordedVessel_UnknownPids_False()
        {
            Assert.False(ParsekFlight.ShouldResumeSplitOnRecordedVessel(
                0u, recordedVesselAlive: true, activeVesselPid: 3363353297u));
            Assert.False(ParsekFlight.ShouldResumeSplitOnRecordedVessel(
                2200110033u, recordedVesselAlive: true, activeVesselPid: 0u));
        }

        // ---- ResolveBreakupParentVesselPid ----

        [Fact]
        public void BreakupParent_IsTheRecordersVessel_NotTheFocusedChild()
        {
            Assert.Equal(2200110033u, ParsekFlight.ResolveBreakupParentVesselPid(
                recorderVesselPid: 2200110033u, activeVesselPid: 3363353297u));
        }

        [Fact]
        public void BreakupParent_NoRecorder_FallsBackToActiveVessel()
        {
            Assert.Equal(3363353297u, ParsekFlight.ResolveBreakupParentVesselPid(
                recorderVesselPid: 0u, activeVesselPid: 3363353297u));
        }

        // ---- CrashCoalescer.EmitNow ----

        [Fact]
        public void EmitNow_NothingPending_ReturnsNull()
        {
            var coalescer = new CrashCoalescer();
            Assert.Null(coalescer.EmitNow());
            Assert.False(coalescer.HasPendingBreakup);
        }

        [Fact]
        public void EmitNow_InsideWindow_EmitsTheDecoupleWithItsControlledChild()
        {
            var coalescer = new CrashCoalescer();
            coalescer.OnSplitEvent(26.88, 3363353297u, childHasController: true,
                splitCause: "DECOUPLE", decouplerPartId: 1500054019u);

            // Window still open: the timed Tick holds it back.
            Assert.Null(coalescer.Tick(26.90));

            BranchPoint bp = coalescer.EmitNow();

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal("DECOUPLE", bp.SplitCause);
            Assert.Equal(26.88, bp.UT);
            Assert.Equal(1500054019u, bp.DecouplerPartId);
            Assert.Equal(new[] { 3363353297u }, coalescer.LastEmittedControlledChildPids);
            Assert.False(coalescer.HasPendingBreakup);
            Assert.Null(coalescer.Tick(30.0));
            Assert.Contains(logLines, l => l.Contains("[Coalescer]") && l.Contains("DECOUPLE emitted: ut=26.88"));
        }

        [Fact]
        public void EmitNow_SameBranchPointShapeAsTick()
        {
            var early = new CrashCoalescer();
            early.OnSplitEvent(100.0, 2000u, childHasController: false);
            early.OnSplitEvent(100.2, 2001u, childHasController: false);
            BranchPoint a = early.EmitNow();

            var timed = new CrashCoalescer();
            timed.OnSplitEvent(100.0, 2000u, childHasController: false);
            timed.OnSplitEvent(100.2, 2001u, childHasController: false);
            BranchPoint b = timed.Tick(101.0);

            Assert.Equal(b.Type, a.Type);
            Assert.Equal(b.UT, a.UT);
            Assert.Equal(b.DebrisCount, a.DebrisCount);
            Assert.Equal(b.BreakupDuration, a.BreakupDuration);
            Assert.Equal(b.BreakupCause, a.BreakupCause);
        }

        // ---- wiring ----

        [Fact]
        public void DeferredJointBreakCheck_ResumesOnRecordedVesselThenHandsFocusOver()
        {
            string body = PreparedMethodBody(FlightPath, "IEnumerator DeferredJointBreakCheck()");
            int decide = IndexOrFail(body, "ShouldResumeSplitOnRecordedVessel(");
            int resume = IndexOrFail(body, "keepRecordedVessel: focusLeftRecordedVessel");
            int fallback = IndexOrFail(body, "EmitFallbackDecoupleEventsForNewVessels(");
            int handOff = IndexOrFail(body, "OnVesselSwitchComplete(");
            Assert.True(decide < resume && resume < fallback && fallback < handOff,
                "split resume wiring: expected decide < resume(keepRecordedVessel) < fallback decouple events < "
                + "OnVesselSwitchComplete hand-off, got " + decide + "," + resume + "," + fallback + "," + handOff);
        }

        [Fact]
        public void EveryFocusHandOffEmitsThePendingSplitBeforeTheFlush()
        {
            string onSwitch = PreparedMethodBody(FlightPath, "void OnVesselSwitchComplete(Vessel newVessel)");
            AssertEmitsBeforeEachFlush(onSwitch, "OnVesselSwitchComplete", expectedFlushes: 2);

            string bgFlush = PreparedMethodBody(FlightPath, "private void HandleTreeBackgroundFlush()");
            AssertEmitsBeforeEachFlush(bgFlush, "HandleTreeBackgroundFlush", expectedFlushes: 1);
        }

        [Fact]
        public void ProcessBreakupEvent_PairsTheParentRecordingWithTheRecordersVessel()
        {
            string body = PreparedMethodBody(FlightPath, "void ProcessBreakupEvent(BranchPoint breakupBp)");
            IndexOrFail(body, "ResolveBreakupParentVesselPid(");
            IndexOrFail(body, "breakupControllablePairs.Add((parentVessel, activeRec))");
        }

        [Fact]
        public void ResumeAfterFalseAlarm_KeepRecordedVessel_ResolvesByRecordingVesselId()
        {
            string body = PreparedMethodBody(RecorderPath, "public void ResumeAfterFalseAlarm(bool keepRecordedVessel)");
            int pick = IndexOrFail(body, "keepRecordedVessel");
            int byPid = IndexOrFail(body, "FindVesselByPid(RecordingVesselId)");
            int assign = IndexOrFail(body, "RecordingVesselId = v.persistentId");
            Assert.True(pick < byPid && byPid < assign,
                "ResumeAfterFalseAlarm must pick the recorded vessel before re-stamping RecordingVesselId");
        }

        private static void AssertEmitsBeforeEachFlush(string body, string name, int expectedFlushes)
        {
            const string flush = "FlushRecorderToTreeRecording(";
            const string emit = "EmitPendingSplitBeforeFocusLeaves(";
            int count = 0;
            int searchFrom = 0;
            int previousFlush = -1;
            while (true)
            {
                int f = body.IndexOf(flush, searchFrom, StringComparison.Ordinal);
                if (f < 0) break;
                int e = body.LastIndexOf(emit, f, StringComparison.Ordinal);
                Assert.True(e > previousFlush,
                    name + ": a FlushRecorderToTreeRecording call has no EmitPendingSplitBeforeFocusLeaves "
                    + "between it and the previous flush");
                previousFlush = f;
                searchFrom = f + flush.Length;
                count++;
            }
            Assert.Equal(expectedFlushes, count);
        }

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(Path.Combine(ParsekSourceRoot(), relPath)));
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "signature not found in " + relPath + ": " + signatureFragment);
            Assert.True(prepared.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "signature is ambiguous in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            Assert.True(open >= 0, "no body for " + signatureFragment);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "expected to find '" + needle + "'");
            return idx;
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }
    }
}
