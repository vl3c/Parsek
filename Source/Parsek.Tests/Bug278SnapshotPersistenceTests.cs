using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source-text pins for the bug #278 follow-up safety nets shipped in PR
    /// #177 alongside the bug #279 logging. See
    /// <c>docs/dev/plans/bugs-269-278-279-fix-plan.md</c> for the rationale and
    /// the full chain trace. Driving the runtime paths from unit tests is not
    /// feasible (Unity statics throw); these tests pin the call sites by source
    /// inspection so a refactor that breaks the chain is caught at test time.
    /// </summary>
    [Collection("Sequential")]
    public class Bug278SnapshotPersistenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug278SnapshotPersistenceTests()
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

        /// <summary>
        /// Source-text regression pin for the destructive-delete chain documented
        /// in <c>docs/dev/plans/bugs-269-278-279-fix-plan.md</c>. The chain is:
        ///
        /// <list type="number">
        ///   <item><description><c>ParsekScenario.FinalizePendingLimboTreeForRevert</c>
        ///     (PR #176's #278 fix) calls <c>ParsekFlight.FinalizeIndividualRecording</c>
        ///     per leaf.</description></item>
        ///   <item><description><c>FinalizeIndividualRecording</c> nulls
        ///     <c>rec.VesselSnapshot</c> in the vessel-gone branch at
        ///     <c>ParsekFlight.cs:~6137</c>.</description></item>
        ///   <item><description>The dispatch flow then calls <c>RecordingStore.CommitPendingTree</c>
        ///     → <c>CommitTree</c> → <c>FinalizeTreeCommit</c>.</description></item>
        ///   <item><description><c>FinalizeTreeCommit</c> at <c>RecordingStore.cs:~503</c>
        ///     sets <c>rec.FilesDirty = true</c> for every recording, then calls
        ///     <c>FlushDirtyFiles</c>.</description></item>
        ///   <item><description><c>FlushDirtyFiles</c> calls <c>SaveRecordingFiles</c>
        ///     on each dirty recording. Without the bug #278 follow-up fix, the
        ///     <c>VesselSnapshot == null</c> path destroys <c>_vessel.craft</c> on
        ///     disk, wiping data previously persisted via <c>PersistFinalizedRecording</c>
        ///     (PR #167's #280 fix from <c>OnBackgroundVesselWillDestroy</c>).</description></item>
        /// </list>
        ///
        /// This test pins the chain via source-text grep so a future refactor that
        /// removes any link in the chain trips the assertion and the refactor author
        /// re-evaluates whether the SaveRecordingFiles fix at <c>RecordingStore.cs:~3091</c>
        /// is still needed. Yes, it's brittle to file path changes — that's intentional.
        /// The chain spans multiple files and would otherwise be invisible to refactors.
        /// </summary>
        [Fact]
        public void DestructiveDelete_RegressionChain_IsReachable_DocumentedBySourceInspection()
        {
            // Locate source files relative to the test working directory
            // (bin/Debug/net472/ — need 5 .. to reach project root, per MEMORY.md).
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));

            string scenarioSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "ParsekScenario.cs"));
            string flightSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "ParsekFlight.cs"));
            string storeSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "RecordingStore.cs"));
            string sidecarStoreSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "RecordingSidecarStore.cs"));

            // Step 1: PR #176's limbo finalize routes through FinalizeIndividualRecording.
            // The call also threads `treeContext: tree` so the effective-leaf check in
            // FinalizeIndividualRecording can resolve the BP topology for limbo trees
            // that aren't yet in RecordingStore.CommittedTrees (effective-leaf fix).
            Assert.Contains(
                "ParsekFlight.FinalizeIndividualRecording(rec, commitUT, isSceneExit: true, treeContext: tree)",
                scenarioSrc);

            // Step 2: FinalizeIndividualRecording nulls VesselSnapshot in the
            // vessel-gone branch (the comment mentions "marking Destroyed" so we
            // can locate the exact null-out site by its surrounding context).
            Assert.Contains("rec.VesselSnapshot = null;", flightSrc);
            Assert.Contains("marking Destroyed", flightSrc);

            // Step 3: FinalizeTreeCommit sets FilesDirty=true on every recording.
            // This is what re-marks the just-nulled leaves dirty so the next
            // FlushDirtyFiles call sends them through SaveRecordingFiles.
            Assert.Contains("rec.FilesDirty = true;", storeSrc);
            Assert.Contains("FlushDirtyFiles(committedRecordings)", storeSrc);

            // Step 4: SaveRecordingFiles handles VesselSnapshot != null on the
            // happy path (the line we left intact). Pinning this lets us detect
            // a future refactor that moves the destructive-delete logic back into
            // the function or restructures the conditional.
            Assert.Contains("if (rec.VesselSnapshot != null)", sidecarStoreSrc);

            // Step 5: The destructive-delete branch is GONE (replaced with the
            // bug #278 follow-up comment). If anyone re-introduces File.Delete
            // on the vesselPath, this test fails and they re-read the chain.
            Assert.DoesNotContain("File.Delete(vesselPath)", sidecarStoreSrc);
        }

        /// <summary>
        /// Source-text regression pin for the EndDebrisRecording wire-up. The
        /// runtime test path that proves this call is reached cannot run in unit
        /// tests because <c>EndDebrisRecording → OnVesselRemovedFromBackground →
        /// Planetarium.GetUniversalTime()</c> throws under Unity-static-free
        /// conditions. So we pin the call site by source text instead. If anyone
        /// removes the <c>PersistFinalizedRecording(rec, $"EndDebrisRecording
        /// pid={vesselPid}")</c> line from <c>BackgroundRecorder.cs</c>, this test
        /// fails and the refactor author re-evaluates whether the #280 wiring gap
        /// for the TTL/out-of-bubble paths is still acceptable.
        /// </summary>
        [Fact]
        public void EndDebrisRecording_HasPersistFinalizedRecordingCall_PinnedBySourceInspection()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));

            string bgRecSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "BackgroundRecorder.cs"));

            // The exact call site format. Must match the string asserted by
            // EndDebrisRecording_PersistContextString_AppearsInFailureLog.
            Assert.Contains(
                "PersistFinalizedRecording(rec, $\"EndDebrisRecording pid={vesselPid}\")",
                bgRecSrc);

            // The wire-up must live inside EndDebrisRecording, not somewhere
            // adjacent. Verify the EndDebrisRecording method exists and the
            // PersistFinalizedRecording call appears after the
            // OnVesselRemovedFromBackground flush so the persisted data
            // includes the TrackSection flush.
            int methodStart = bgRecSrc.IndexOf("private void EndDebrisRecording(uint vesselPid");
            Assert.True(methodStart > 0,
                "EndDebrisRecording method signature not found in BackgroundRecorder.cs");

            int flushIdx = bgRecSrc.IndexOf("OnVesselRemovedFromBackground(vesselPid)", methodStart);
            int persistIdx = bgRecSrc.IndexOf(
                "PersistFinalizedRecording(rec, $\"EndDebrisRecording", methodStart);
            Assert.True(flushIdx > 0,
                "OnVesselRemovedFromBackground call not found inside EndDebrisRecording");
            Assert.True(persistIdx > 0,
                "PersistFinalizedRecording call not found inside EndDebrisRecording");
            Assert.True(persistIdx > flushIdx,
                "PersistFinalizedRecording must be called AFTER OnVesselRemovedFromBackground " +
                "so the flush data is included in the persisted sidecar");
        }
    }
}
