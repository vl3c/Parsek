using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// WARP-REQUEST-SURVIVES-A-FAILED-REWIND. A Warp to time request (the pending forward jump
    /// and the deferred flight warp) belongs to the game it was armed in: a rewind that does not
    /// launch drops the pending jump, a Cold load and the main-menu transition drop both, and the
    /// consumer re-reads the request after the Space Center settles. The loads a legitimate warp
    /// crosses (a plain rewind's, a flight exit's) are not Cold, so they keep it. The real
    /// InitiateRewind failure paths are driven in <c>RewindLauncherUnitTests</c>, the Cold reset
    /// in <c>SaveScopedSessionResetTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class WarpToTimeRequestTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WarpToTimeRequestTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            WarpToTimeRequest.ResetForTesting();
        }

        public void Dispose()
        {
            WarpToTimeRequest.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------- the requests ----------

        [Fact]
        public void DropAll_DropsBothRequests_AndLogsEachWithTheReason()
        {
            WarpToTimeRequest.Set(12345.0);
            WarpToTimeRequest.SetDeferredKscWarp(2, 30, 4, 5);
            logLines.Clear();

            int dropped = WarpToTimeRequest.DropAll("cold-load:save-file");

            Assert.Equal(2, dropped);
            Assert.False(WarpToTimeRequest.HasPending);
            Assert.Equal(0.0, WarpToTimeRequest.TargetUT);
            Assert.Equal(Guid.Empty, WarpToTimeRequest.SessionId);
            Assert.False(WarpToTimeRequest.HasDeferredKscWarp);
            Assert.Equal(0, WarpToTimeRequest.DeferYear);
            Assert.Equal(0, WarpToTimeRequest.DeferDay);
            Assert.False(WarpToTimeRequest.IsDeferredKscWarpStale());
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Pending warp dropped: targetUT=12345.0 reason=cold-load:save-file"));
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Deferred flight warp dropped: Y2 D30 4:05 reason=cold-load:save-file"));
        }

        [Fact]
        public void DropAll_WithNothingArmed_DropsNothingAndLogsNothing()
        {
            Assert.Equal(0, WarpToTimeRequest.DropAll("main-menu"));
            Assert.DoesNotContain(logLines, l => l.Contains("dropped"));
        }

        // ---------- the launch wrapper ----------

        [Fact]
        public void ArmAndLaunch_Launched_KeepsThePendingWarpForTheReload()
        {
            bool armedDuringLaunch = false;
            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                5000.0, () => { armedDuringLaunch = WarpToTimeRequest.HasPending; return true; },
                "rewind-not-launched");

            Assert.True(launched);
            Assert.True(armedDuringLaunch, "the request must be armed before the launch requests the load");
            Assert.True(WarpToTimeRequest.HasPending);
            Assert.Equal(5000.0, WarpToTimeRequest.TargetUT);
            Assert.False(WarpToTimeRequest.IsStale());
            Assert.DoesNotContain(logLines, l => l.Contains("Pending warp dropped"));
        }

        [Fact]
        public void ArmAndLaunch_NotLaunched_DropsThePendingWarp()
        {
            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                5000.0, () => false, "rewind-not-launched");

            Assert.False(launched);
            Assert.False(WarpToTimeRequest.HasPending);
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Pending warp dropped: targetUT=5000.0 reason=rewind-not-launched"));
        }

        [Fact]
        public void ArmAndLaunch_LaunchThrows_DropsThePendingWarpAndRethrows()
        {
            Assert.Throws<InvalidOperationException>(() => WarpToTimeController.ArmPendingWarpAndLaunch(
                5000.0, () => throw new InvalidOperationException("boom"), "rewind-not-launched"));

            Assert.False(WarpToTimeRequest.HasPending);
            Assert.Contains(logLines, l => l.Contains("Pending warp dropped: targetUT=5000.0 reason=rewind-not-launched"));
        }

        // ---------- the consumer ----------

        [Fact]
        public void Consumer_RequestStillArmed_Proceeds()
        {
            Assert.False(WarpToTimeConsumer.RequestDroppedWhileSettling(true, "Pending warp"));
            Assert.DoesNotContain(logLines, l => l.Contains("not run"));
        }

        [Fact]
        public void Consumer_RequestDroppedDuringTheLoad_StopsAndLogs()
        {
            Assert.True(WarpToTimeConsumer.RequestDroppedWhileSettling(false, "Deferred flight warp"));
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Deferred flight warp not run: the request was dropped while the Space Center loaded"));
        }

        // Both coroutines re-read their request right after the first yield (the scenario's
        // OnLoad, which drops it on a Cold load, runs between onLevelWasLoaded and that resume)
        // and before anything acts on it.
        [Fact]
        public void Consumer_BothCoroutinesRecheckAfterTheFirstYield()
        {
            string src = Prepared("WarpToTimeConsumer.cs");

            string deferred = Body(src, "IEnumerator ConsumeDeferredKscWarp()");
            int yieldD = IndexOrFail(deferred, "yield return null;");
            int checkD = IndexOrFail(deferred, "RequestDroppedWhileSettling(WarpToTimeRequest.HasDeferredKscWarp,");
            int execute = IndexOrFail(deferred, "WarpToTimeController.ExecuteAtKsc(");
            int readYear = IndexOrFail(deferred, "WarpToTimeRequest.DeferYear");
            Assert.True(yieldD < checkD && checkD < readYear && readYear < execute);

            string pending = Body(src, "IEnumerator ConsumePendingWarp()");
            int yieldP = IndexOrFail(pending, "yield return null;");
            int checkP = IndexOrFail(pending, "RequestDroppedWhileSettling(WarpToTimeRequest.HasPending,");
            int readTarget = IndexOrFail(pending, "WarpToTimeRequest.TargetUT");
            int jump = IndexOrFail(pending, "TimeJumpManager.ExecuteForwardJump(");
            Assert.True(yieldP < checkP && checkP < readTarget && readTarget < jump);
        }

        // ---------- the clear sites, and the loads a legitimate warp crosses ----------

        // A plain rewind's load (Rewind-to-Launch, Warp-to-game-start: RewindContext.IsRewinding,
        // set before the LoadGame) classifies PlainRewind, never Cold, whatever else is pending;
        // the flight exit of a deferred warp (no rewind, no announced save-file load) and the
        // Space Center arrival it leads to classify InSession. Only a Cold load runs the session
        // reset that drops the requests, so both legitimate crossings keep them.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void LegitimateCrossings_PlainRewindLoad_IsNeverCold(bool saveFileLoadPending, bool revertPending)
        {
            Assert.Equal(EarlyLoadKind.PlainRewind, LoadReconcilePolicy.ClassifyEarly(
                initialLoadDone: true, isRewinding: true, reFlyInvokePending: false,
                discardReFlyPending: false, saveFileLoadPending: saveFileLoadPending, revertPending: revertPending));
        }

        [Fact]
        public void LegitimateCrossings_FlightExitToSpaceCenter_IsInSession()
        {
            Assert.Equal(EarlyLoadKind.InSession, LoadReconcilePolicy.ClassifyEarly(
                initialLoadDone: true, isRewinding: false, reFlyInvokePending: false,
                discardReFlyPending: false, saveFileLoadPending: false, revertPending: false));
            Assert.NotEqual(LoadKind.Cold, LoadReconcilePolicy.ClassifyRefined(
                EarlyLoadKind.InSession, isRevert: false, isFlightToFlight: false, utWentBackwards: false));
        }

        // The requests are dropped in exactly two production places: the save-scoped reset
        // (which OnLoad runs only for a Cold load; GameStateSaveNodeTests pins that gate) and
        // the main-menu branch of OnMainMenuTransition. A third caller on a load path a
        // legitimate warp crosses would break Warp to time after a rewind.
        [Fact]
        public void DropAll_IsCalledOnlyByTheColdResetAndTheMainMenuTransition()
        {
            var callers = new List<string>();
            foreach (string path in Directory.GetFiles(ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                int n = Occurrences(prepared, "WarpToTimeRequest.DropAll(");
                for (int i = 0; i < n; i++)
                    callers.Add(Path.GetFileName(path));
            }
            callers.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "ParsekScenario.SceneTransitions.cs", "ParsekScenario.SessionReset.cs" }, callers);

            string reset = Body(Prepared("ParsekScenario.SessionReset.cs"),
                "internal static void ResetSaveScopedSessionState(string reason)");
            Assert.Equal(1, Occurrences(reset, "WarpToTimeRequest.DropAll("));

            string mainMenu = Body(Prepared("ParsekScenario.SceneTransitions.cs"),
                "private void OnMainMenuTransition(GameScenes newScene)");
            int gate = IndexOrFail(mainMenu, "if (newScene == GameScenes.MAINMENU)");
            int gateOpen = mainMenu.IndexOf('{', gate);
            string mainMenuBranch = SourceScanText.BraceMatchedBlock(mainMenu, gateOpen);
            Assert.Equal(1, Occurrences(mainMenuBranch, "WarpToTimeRequest.DropAll("));
        }

        // StartRewind arms the pending jump only through the wrapper that drops it when the
        // launch returns false, for both rewind kinds.
        [Fact]
        public void StartRewind_ArmsThePendingWarpOnlyThroughTheDroppingWrapper()
        {
            string src = Prepared("WarpToTimeController.cs");
            string start = Body(src, "private static void StartRewind(RewindTarget target, double targetUT)");
            Assert.Equal(0, Occurrences(start, "WarpToTimeRequest.Set("));
            Assert.Equal(2, Occurrences(start, "ArmPendingWarpAndLaunch("));
            Assert.Contains("() => RewindLauncher.InitiateRewind(ownerNow)", Collapse(start));
            Assert.Contains("() => RewindLauncher.InitiateRewindToCareerStart(CareerStartSnapshot.SaveFileName)",
                Collapse(start));
            Assert.Equal(1, Occurrences(src, "WarpToTimeRequest.Set("));
        }

        // ---------- helpers ----------

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }

        private static string Prepared(string fileName)
        {
            string path = Path.Combine(ParsekSourceRoot(), fileName);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
        }

        private static string Body(string prepared, string signatureFragment)
        {
            int sig = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found: " + signatureFragment);
            Assert.True(prepared.IndexOf(signatureFragment, sig + 1, StringComparison.Ordinal) < 0,
                "signature is ambiguous: " + signatureFragment);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "'" + needle + "' not found");
            return idx;
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string Collapse(string text)
        {
            return Regex.Replace(text, @"\s+", " ");
        }
    }
}
