using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7 + §10.6): guards the three
    /// callback handlers on <see cref="RevertInterceptor"/> and the prefix
    /// gate that decides whether the stock
    /// <see cref="FlightDriver.RevertToLaunch"/> runs.
    ///
    /// <para>
    /// The actual <see cref="PopupDialog"/> rendering cannot be exercised
    /// from xUnit (no live Unity UI canvas), so the tests drive the
    /// callback-wiring side directly via <see cref="RevertInterceptor.RetryHandler"/>,
    /// <see cref="RevertInterceptor.FullRevertHandler"/>,
    /// <see cref="RevertInterceptor.CancelHandler"/>, plus the prefix gate
    /// via <see cref="RevertInterceptor.ShouldBlock"/>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyRevertDialogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyRevertDialogTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            TreeDiscardPurge.ResetTestOverrides();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlyRevertDialog.ResetForTesting();
            RevertInterceptor.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers ---------------------------------------------

        private static ReFlySessionMarker MakeMarker(
            string sessionId = "sess_p12_test",
            string treeId = "tree_p12_test",
            string rpId = "rp_p12_test",
            string originId = "rec_origin_p12")
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = "rec_provisional_p12",
                OriginChildRecordingId = originId,
                RewindPointId = rpId,
                InvokedUT = 42.0,
                InvokedRealTime = "2026-04-18T00:00:00.000Z",
            };
        }

        private static RewindPoint MakeRewindPoint(string rpId, string originId)
        {
            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp_p12",
                UT = 0.0,
                QuicksaveFilename = rpId + ".sfs",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = originId,
                        Controllable = true,
                    },
                },
            };
        }

        private static ParsekScenario InstallScenario(
            ReFlySessionMarker marker = null,
            List<RewindPoint> rps = null,
            List<RecordingSupersedeRelation> supersedes = null,
            List<LedgerTombstone> tombstones = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = rps ?? new List<RewindPoint>(),
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = tombstones ?? new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        // ---------- Retry -----------------------------------------------

        [Fact]
        public void RetryCallback_GeneratesFreshSession_ClearsMarker_ReinvokesRewind()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });

            // Capture the rp + slot the handler passes to StartInvoke without
            // running the full load path.
            RewindPoint capturedRp = null;
            ChildSlot capturedSlot = null;
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) =>
            {
                capturedRp = r;
                capturedSlot = s;
            };

            RevertInterceptor.RetryHandler(marker);

            // Marker cleared so a new RewindInvoker precondition sees no
            // active session (equivalent to generating a fresh session id on
            // the upcoming StartInvoke).
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // RewindInvoker.StartInvoke got the same RP + slot captured from
            // the marker.
            Assert.NotNull(capturedRp);
            Assert.Equal(rp.RewindPointId, capturedRp.RewindPointId);
            Assert.NotNull(capturedSlot);
            Assert.Equal(0, capturedSlot.SlotIndex);
            Assert.Equal(marker.OriginChildRecordingId, capturedSlot.OriginChildRecordingId);

            // Log contract per §10.6: End reason=retry must be emitted.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=retry")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void RetryCallback_UnresolvedRp_AbortsWithoutInvokingRewind()
        {
            var marker = MakeMarker();
            // No rp list installed — scenario has no matching RP.
            var scenario = InstallScenario(marker: marker);

            bool invoked = false;
            RevertInterceptor.RewindInvokeStartForTesting = (_, __) => invoked = true;

            RevertInterceptor.RetryHandler(marker);

            Assert.False(invoked);
            // Marker must stay in place — the caller can still attempt Full
            // Revert or Cancel after this error toast.
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("RetryHandler: cannot resolve rp="));
        }

        // ---------- Full Revert -----------------------------------------

        [Fact]
        public void FullRevertCallback_InvokesTreeDiscardPurge_WithCorrectTreeId()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            rp.BranchPointId = "bp_full";
            var bp = new BranchPoint
            {
                Id = "bp_full",
                Type = BranchPointType.Undock,
                UT = 0.0,
                RewindPointId = rp.RewindPointId,
            };

            var rec = new Recording
            {
                RecordingId = "rec_in_tree",
                VesselName = "P12_test",
                TreeId = marker.TreeId,
                MergeState = MergeState.Immutable,
            };

            var tree = new RecordingTree
            {
                Id = marker.TreeId,
                TreeName = "P12_full_revert",
                BranchPoints = new List<BranchPoint> { bp },
            };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);

            var superRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_p12",
                OldRecordingId = "rec_in_tree",
                NewRecordingId = "rec_other",
                UT = 0.0,
            };

            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp },
                supersedes: new List<RecordingSupersedeRelation> { superRel });

            // Keep the tree-purge file-delete hook a no-op so we never touch disk.
            TreeDiscardPurge.DeleteQuicksaveForTesting = _ => true;

            bool stockRevertInvoked = false;
            RevertInterceptor.StockRevertInvokerForTesting = () => stockRevertInvoked = true;

            RevertInterceptor.FullRevertHandler(marker);

            // Purge ran: RP dropped, supersede dropped, marker cleared.
            Assert.Empty(scenario.RewindPoints);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // Stock revert was re-dispatched (via the test hook, so no KSP call).
            Assert.True(stockRevertInvoked);

            // Log contract per §10.6: End reason=fullRevert.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=fullRevert")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("tree=" + marker.TreeId));
        }

        [Fact]
        public void FullRevertCallback_EmptyTreeId_ClearsMarker_StillTriggersStockRevert()
        {
            var marker = MakeMarker();
            marker.TreeId = null;
            var scenario = InstallScenario(marker: marker);

            bool stockRevertInvoked = false;
            RevertInterceptor.StockRevertInvokerForTesting = () => stockRevertInvoked = true;

            RevertInterceptor.FullRevertHandler(marker);

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.True(stockRevertInvoked);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("FullRevertHandler: marker sess=")
                && l.Contains("has empty TreeId"));
        }

        // ---------- Cancel ----------------------------------------------

        [Fact]
        public void CancelCallback_LogsAndLeavesStateAlone()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });

            int rpCountBefore = scenario.RewindPoints.Count;
            var markerBefore = scenario.ActiveReFlySessionMarker;

            RevertInterceptor.CancelHandler(marker);

            // No state touched.
            Assert.Same(markerBefore, scenario.ActiveReFlySessionMarker);
            Assert.Equal(rpCountBefore, scenario.RewindPoints.Count);

            // Log contract per §10.6: Revert dialog cancelled sess=<id>.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog cancelled")
                && l.Contains("sess=" + marker.SessionId));
        }

        // ---------- Interceptor gate ------------------------------------

        [Fact]
        public void Interceptor_NoActiveSession_AllowsStockRevert()
        {
            // Scenario installed but marker is null.
            InstallScenario(marker: null);

            ReFlySessionMarker resolved;
            bool block = RevertInterceptor.ShouldBlock(out resolved);

            Assert.False(block);
            Assert.Null(resolved);
        }

        [Fact]
        public void Interceptor_NoScenario_AllowsStockRevert()
        {
            // ParsekScenario.Instance is null (ResetInstanceForTesting in ctor).
            ReFlySessionMarker resolved;
            bool block = RevertInterceptor.ShouldBlock(out resolved);

            Assert.False(block);
            Assert.Null(resolved);
        }

        [Fact]
        public void Interceptor_ActiveSession_BlocksStockRevert_SpawnsDialog()
        {
            var marker = MakeMarker();
            InstallScenario(marker: marker);

            ReFlySessionMarker dialogMarker = null;
            RevertInterceptor.DialogShowForTesting = m => dialogMarker = m;

            bool result = RevertInterceptor.Prefix();

            Assert.False(result);
            Assert.Same(marker, dialogMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[RevertInterceptor]")
                && l.Contains("blocking stock RevertToLaunch")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void Dialog_Show_FiresHook_AndLogsShownTag()
        {
            var marker = MakeMarker();

            string hookSession = null;
            ReFlyRevertDialog.ShowHookForTesting = s => hookSession = s;

            ReFlyRevertDialog.Show(marker, () => { }, () => { }, () => { });

            Assert.Equal(marker.SessionId, hookSession);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog shown")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void Dialog_Show_NullMarker_DoesNotFireHook()
        {
            bool hookFired = false;
            ReFlyRevertDialog.ShowHookForTesting = _ => hookFired = true;

            ReFlyRevertDialog.Show(null, () => { }, () => { }, () => { });

            Assert.False(hookFired);
            Assert.Contains(logLines, l =>
                l.Contains("[RewindUI]") && l.Contains("marker is null"));
        }
    }
}
