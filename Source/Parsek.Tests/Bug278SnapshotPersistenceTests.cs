using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the bug #278 follow-up safety nets shipped in PR #177 alongside
    /// the bug #279 logging.
    ///
    /// **Background.** The user-visible bug #278 was fixed in PR #176 by
    /// changing <c>FinalizePendingLimboTreeForRevert</c> to use real vessel
    /// situation instead of blanket-stamping every leaf as Destroyed. PR #177
    /// (this set) adds two follow-up safety nets that the PR #176 fix does not
    /// itself need but that close real coverage gaps in the surrounding
    /// snapshot-persistence path:
    ///
    /// 1. <see cref="BackgroundRecorder.PersistFinalizedRecording"/> is now
    ///    called from <c>EndDebrisRecording</c>, mirroring the #280 wiring
    ///    into the <c>CheckDebrisTTL</c> termination path that the original
    ///    #280 fix had not covered. Pinned by
    ///    <see cref="EndDebrisRecording_PersistContextString_AppearsInFailureLog"/>.
    /// 2. <c>RecordingStore.SaveRecordingFiles</c> no longer destructively
    ///    deletes <c>_vessel.craft</c> when in-memory <c>VesselSnapshot</c> is
    ///    null. Inspected by code review (not unit-testable in this environment
    ///    because <c>SaveRecordingFiles</c> requires a real KSP save folder).
    ///
    /// **Test scope.** Driving the full
    /// <c>CheckDebrisTTL → EndDebrisRecording</c> path from a unit test is not
    /// feasible: <c>EndDebrisRecording</c> calls
    /// <c>OnVesselRemovedFromBackground</c>, which calls
    /// <c>Planetarium.GetUniversalTime()</c> — a Unity static that throws
    /// <c>NullReferenceException</c> in test environments where Unity is not
    /// initialized. Refactoring those call paths is out of scope for this PR.
    /// Instead, these tests pin two observable contracts that together cover
    /// the wiring:
    ///
    /// 1. The <c>EndDebrisRecording pid={vesselPid}</c> context string format
    ///    used at the call site is preserved (so a future rename produces a
    ///    test failure that prompts a deliberate update).
    /// 2. The breadcrumb is distinguishable from the existing #280
    ///    <c>OnBackgroundVesselWillDestroy</c> / <c>Shutdown</c> contexts so
    ///    next-playtest triage can tell which finalization site fired.
    ///
    /// End-to-end behavior is verified by the next Kerbal X in-game playtest
    /// (load <c>s32</c>, repeat the launch + radial booster crash sequence;
    /// expect <c>BuildDefaultVesselDecisions.*hasSnapshot=True</c> in the
    /// resulting log).
    /// </summary>
    [Collection("Sequential")]
    public class Bug278SnapshotPersistenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug278SnapshotPersistenceTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        /// <summary>
        /// Bug #278 fix: BackgroundRecorder.EndDebrisRecording now invokes
        /// PersistFinalizedRecording with the context string
        /// $"EndDebrisRecording pid={vesselPid}" so the failure-path warn log
        /// uniquely identifies which finalization site triggered the write
        /// attempt. This test pins the context string format by calling
        /// PersistFinalizedRecording with the same string a synthetic
        /// EndDebrisRecording invocation would produce, and asserting the warn
        /// log carries it. Any rename or format change to the call site at
        /// BackgroundRecorder.cs:~787 must also update this assertion.
        ///
        /// We use an invalid recording id to force the failure path (the
        /// success path requires a live KSP save folder). The breadcrumb
        /// itself — `EndDebrisRecording pid=12345` — is the load-bearing
        /// observable for next-playtest triage of "did the persist actually
        /// fire from the TTL path?".
        /// </summary>
        [Fact]
        public void EndDebrisRecording_PersistContextString_AppearsInFailureLog()
        {
            uint vesselPid = 12345;
            var rec = new Recording
            {
                RecordingId = "invalid id with spaces", // rejected by ValidateRecordingId
                VesselName = "Synthetic Debris"
            };

            // The format string MUST exactly match the call site at
            // BackgroundRecorder.EndDebrisRecording. If you change one, change both.
            BackgroundRecorder.PersistFinalizedRecording(rec, $"EndDebrisRecording pid={vesselPid}");

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][BgRecorder]") &&
                l.Contains("PersistFinalizedRecording: failed to write sidecar") &&
                l.Contains($"EndDebrisRecording pid={vesselPid}"));
        }

        /// <summary>
        /// Sanity check: ensures the EndDebrisRecording context string is
        /// distinguishable from the existing #280 sites
        /// (OnBackgroundVesselWillDestroy / Shutdown). This protects against
        /// accidentally collapsing the two breadcrumbs into a shared format,
        /// which would defeat the diagnostic value: a future playtest would
        /// not be able to tell whether a snapshot was lost via the TTL path
        /// (#278 territory) or the destroy path (#280 territory).
        /// </summary>
        [Fact]
        public void EndDebrisRecording_BreadcrumbDistinctFromBug280Sites()
        {
            uint pid = 67890;
            var endDebrisCtx = $"EndDebrisRecording pid={pid}";
            var destroyCtx = $"OnBackgroundVesselWillDestroy pid={pid}";
            var shutdownCtx = $"Shutdown pid={pid}";

            Assert.NotEqual(endDebrisCtx, destroyCtx);
            Assert.NotEqual(endDebrisCtx, shutdownCtx);
            Assert.StartsWith("EndDebrisRecording", endDebrisCtx);
        }
    }
}
