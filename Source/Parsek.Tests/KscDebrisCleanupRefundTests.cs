using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSC-DEBRIS-CLEANUP-REFUND-UNLEDGERED: the Space Center's debris clean-up
    /// (<c>ProtoVessel.Clean</c> on debris landed in the KSC) fires <c>onVesselRecovered</c>
    /// with quick=true, and stock's own <c>VesselRecovery</c> listener pays the refund AFTER
    /// Parsek's handler in the same call. A debris callback with no payout context used to be
    /// skipped outright, so the refund landed as an untagged leak row. Inside stock's recovery
    /// dispatch for that same vessel it is now deferred and paired with the payout event, once;
    /// outside a dispatch it is still skipped, and a dispatch that pays nothing drops it.
    /// </summary>
    [Collection("Sequential")]
    public class KscDebrisCleanupRefundTests : IDisposable
    {
        private const uint DebrisPid = 2989750001;
        private readonly List<string> logLines = new List<string>();

        public KscDebrisCleanupRefundTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(
                fundsTracked: true, scienceTracked: null, reputationTracked: null);
            RecordingStore.ResetForTesting();
            RecoveryDispatchScope.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecoveryDispatchScope.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecoveredVesselIdentity DebrisIdentity()
        {
            return RecoveredVesselIdentity.FromRawName("CS Flea Probe Debris");
        }

        private static GameStateEvent PayoutEvent(double ut, double before, double after)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = before,
                valueAfter = after
            };
        }

        private static List<GameAction> RecoveryRowsAt(double ut)
        {
            return Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery &&
                Math.Abs(a.UT - ut) < 0.01).ToList();
        }

        [Fact]
        public void ShouldSkip_DebrisWithoutContext_OnlyOutsideItsDispatch()
        {
            string reason;
            Assert.True(LedgerRecoveryFundsPairing.ShouldSkipDeferredRecoveryFunds(
                VesselType.Debris, null, false, out reason));
            Assert.Equal("debris recovery without recovery-processing payout context", reason);

            Assert.False(LedgerRecoveryFundsPairing.ShouldSkipDeferredRecoveryFunds(
                VesselType.Debris, null, true, out reason));
            Assert.Null(reason);

            // Not debris: never skipped by this rule, inside a dispatch or not.
            Assert.False(LedgerRecoveryFundsPairing.ShouldSkipDeferredRecoveryFunds(
                VesselType.Ship, null, false, out reason));

            Assert.True(LedgerRecoveryFundsPairing.IsDebrisDeferredInsideDispatch(
                VesselType.Debris, null, true));
            Assert.False(LedgerRecoveryFundsPairing.IsDebrisDeferredInsideDispatch(
                VesselType.Debris, null, false));
            Assert.False(LedgerRecoveryFundsPairing.IsDebrisDeferredInsideDispatch(
                VesselType.Ship, null, true));
        }

        [Fact]
        public void KscCleanupRefund_InsideDispatch_IsBookedOnceAtStocksAmount()
        {
            // The RW-7 `2026-10-28_1637` order: scope open, Parsek's callback (no payout
            // context yet), stock's payout event, scope close.
            const double ut = 185930.0;
            RecoveryDispatchScope.Open(DebrisIdentity(), DebrisPid, "proto-vessel-clean");

            LedgerOrchestrator.OnVesselRecoveryFunds(
                ut, DebrisIdentity(), quick: true, vesselType: VesselType.Debris,
                payoutContext: null, dispatchPersistentId: DebrisPid);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("CS Flea Probe Debris") &&
                l.Contains("debris recovery inside stock's recovery dispatch for this vessel: deferring"));

            var evt = PayoutEvent(ut, 150632.18, 150794.86);
            GameStateStore.AddEvent(ref evt);
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);

            RecoveryDispatchScope.Close("proto-vessel-clean");

            var rows = RecoveryRowsAt(ut);
            Assert.Single(rows);
            Assert.Equal(162.68, (double)rows[0].FundsAwarded, 2);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("skipping deferred recovery-funds pairing"));
            Assert.DoesNotContain(logLines, l => l.Contains("dropped (stock paid nothing)"));

            // The same payout event seen again books nothing more.
            LedgerOrchestrator.OnRecoveryFundsEventRecorded(evt);
            Assert.Single(RecoveryRowsAt(ut));
        }

        [Fact]
        public void KscCleanup_DispatchThatPaysNothing_DropsTheRequestQuietly()
        {
            const double ut = 185931.0;
            RecoveryDispatchScope.Open(DebrisIdentity(), DebrisPid, "proto-vessel-clean");
            LedgerOrchestrator.OnVesselRecoveryFunds(
                ut, DebrisIdentity(), quick: true, vesselType: VesselType.Debris,
                payoutContext: null, dispatchPersistentId: DebrisPid);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            RecoveryDispatchScope.Close("proto-vessel-clean");

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Empty(RecoveryRowsAt(ut));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Recovery dispatch closed with no payout for deferred debris request") &&
                l.Contains("dropped (stock paid nothing)"));

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("scene switch");
            Assert.DoesNotContain(logLines, l => l.Contains("FlushStalePendingRecoveryFunds"));
        }

        [Fact]
        public void DebrisWithoutContext_OutsideAnyDispatch_IsStillSkipped()
        {
            const double ut = 185932.0;
            LedgerOrchestrator.OnVesselRecoveryFunds(
                ut, DebrisIdentity(), quick: true, vesselType: VesselType.Debris,
                payoutContext: null, dispatchPersistentId: 0);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("CS Flea Probe Debris") &&
                l.Contains("skipping deferred recovery-funds pairing (debris recovery without recovery-processing payout context)"));
        }

        [Fact]
        public void DispatchClose_DropsOnlyItsOwnVesselsDebrisRequest()
        {
            // A non-debris request deferred under the same pid, and a debris request of another
            // vessel's dispatch, both survive this dispatch's close.
            RecoveryDispatchScope.Open(DebrisIdentity(), DebrisPid, "proto-vessel-clean");
            LedgerOrchestrator.OnVesselRecoveryFunds(
                185933.0, RecoveredVesselIdentity.FromRawName("Other Debris"), quick: true,
                vesselType: VesselType.Debris, payoutContext: null, dispatchPersistentId: 42);
            LedgerOrchestrator.OnVesselRecoveryFunds(
                185933.0, RecoveredVesselIdentity.FromRawName("A Ship"), quick: false,
                vesselType: VesselType.Ship, payoutContext: null, dispatchPersistentId: DebrisPid);
            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            RecoveryDispatchScope.Close("proto-vessel-clean");

            Assert.Equal(2, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
        }
    }
}
