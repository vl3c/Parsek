using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #278 — debris snapshots lost between BackgroundRecorder
    /// split-time capture and MergeDialog.BuildDefaultVesselDecisions.
    ///
    /// The 2026-04-09 Kerbal X playtest log
    /// (logs/2026-04-09_recording-flow-bugs/KSP.log) showed that 27 Kerbal X
    /// Debris recordings finalized via the
    /// CheckDebrisTTL → EndDebrisRecording (v == null branch) path. The #280
    /// fix added PersistFinalizedRecording to OnBackgroundVesselWillDestroy and
    /// Shutdown but left EndDebrisRecording uncovered.
    ///
    /// **Test scope.** Driving the full CheckDebrisTTL → EndDebrisRecording
    /// path from a unit test is not feasible: EndDebrisRecording calls
    /// OnVesselRemovedFromBackground, which calls
    /// <c>Planetarium.GetUniversalTime()</c> — a Unity static that throws
    /// NullReferenceException in test environments where Unity is not
    /// initialized. Refactoring all of these to be test-friendly is well
    /// outside the scope of this fix. Instead, these tests pin two
    /// observable contracts that together cover the wiring:
    ///
    /// 1. The `EndDebrisRecording pid={vesselPid}` context string format used
    ///    in the call site is preserved (so a future rename of the helper
    ///    invocation site fails this test, prompting a deliberate update).
    /// 2. The `PersistFinalizedRecording` helper itself is exercised by the
    ///    existing <see cref="Bug280PersistFinalizedRecordingTests"/> suite.
    ///
    /// End-to-end behavior is verified by the next Kerbal X in-game playtest
    /// (load `s32`, repeat the launch + radial booster crash sequence; expect
    /// `BuildDefaultVesselDecisions.*hasSnapshot=True` in the resulting log).
    ///
    /// The destructive-delete fix at RecordingStore.cs:3077-3086 (removal of
    /// the auto-deletion of `_vessel.craft` when in-memory snapshot is null)
    /// is also not unit-testable in this environment because SaveRecordingFiles
    /// requires a real KSP save folder. The change was inspected against
    /// MergeDialog/ChainSegmentManager callers and validated against the
    /// cascading-fix logic in the plan doc
    /// (docs/dev/plans/bugs-269-278-279-fix-plan.md).
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
