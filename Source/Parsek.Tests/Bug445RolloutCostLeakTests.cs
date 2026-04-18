using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #445: KSP fires <c>TransactionReasons.VesselRollout</c> when the player
    /// launches a vessel from VAB/SPH onto the launchpad/runway. The deduction lands
    /// before <c>FlightRecorder.CapturePreLaunchResources</c> runs, so the recording-side
    /// <c>CreateVesselCostActions</c> derives a near-zero build cost from
    /// <c>PreLaunchFunds - rec.Points[0].funds</c>. If the player cancels the rollout
    /// without ever starting a recording, no <c>FundsSpending(VesselBuild)</c> action
    /// is committed and the ledger total drifts above KSP's actual funds balance.
    ///
    /// Fix: route the deduction through <see cref="LedgerOrchestrator.OnVesselRolloutSpending"/>
    /// at the moment of the transaction (tagged with <c>DedupKey="rollout:&lt;UT&gt;"</c>),
    /// and let a subsequent recording adopt the action via
    /// <see cref="LedgerOrchestrator.TryAdoptRolloutAction"/> instead of double-charging.
    /// </summary>
    [Collection("Sequential")]
    public class Bug445RolloutCostLeakTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug445RolloutCostLeakTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----------------------------------------------------------------
        // OnVesselRolloutSpending
        // ----------------------------------------------------------------

        [Fact]
        public void OnVesselRolloutSpending_WritesFundsSpendingVesselBuild()
        {
            // The cancelled-rollout case: the player rolls out, KSP deducts the build
            // cost, then the player cancels without ever starting a recording. The
            // ledger MUST still see the deduction, otherwise its funds total drifts
            // above what KSP actually shows.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 100.0, cost: 5000.0);

            var actions = Ledger.Actions;
            var rollout = actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.NotNull(rollout);
            Assert.Null(rollout.RecordingId);
            Assert.Equal(5000f, rollout.FundsSpent);
            Assert.Equal(100.0, rollout.UT);
            Assert.StartsWith("rollout:", rollout.DedupKey);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("VesselRollout spending recorded") &&
                l.Contains("cost=5000"));
        }

        [Fact]
        public void OnVesselRolloutSpending_NonPositiveCost_Skipped()
        {
            // Defensive: a zero or negative "cost" (refund-style FundsChanged) is not
            // a build deduction and must NOT synthesize a spending action.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 100.0, cost: 0.0);
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 200.0, cost: -1.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: non-positive cost"));
        }

        // ----------------------------------------------------------------
        // ClassifyAction (reconciliation pairing)
        // ----------------------------------------------------------------

        [Fact]
        public void ClassifyAction_FundsSpendingVesselBuild_PairsWithVesselRolloutEvent()
        {
            // Reconciliation must pair the synthesized rollout action against a
            // FundsChanged event whose key is "VesselRollout" — otherwise the existing
            // RnDPartPurchase classifier would log spurious WARNs.
            var action = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f
            };

            var exp = LedgerOrchestrator.ClassifyAction(action);

            Assert.Equal(LedgerOrchestrator.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(GameStateEventType.FundsChanged, exp.EventType);
            Assert.Equal("VesselRollout", exp.ExpectedReasonKey);
            Assert.Equal(-5000.0, exp.ExpectedDelta);
        }

        [Fact]
        public void ClassifyAction_FundsSpendingOther_StillPairsWithRnDPartPurchase()
        {
            // Regression guard: the new VesselBuild branch must not steal the existing
            // RnDPartPurchase classification used by KSC part purchases.
            var action = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f
            };

            var exp = LedgerOrchestrator.ClassifyAction(action);

            Assert.Equal("RnDPartPurchase", exp.ExpectedReasonKey);
        }

        // ----------------------------------------------------------------
        // TryAdoptRolloutAction
        // ----------------------------------------------------------------

        [Fact]
        public void TryAdoptRolloutAction_WithinWindow_ClaimsAction()
        {
            // The launch+record case: rollout was logged at UT=99, recording starts at
            // UT=110. Adoption transfers ownership to the recording so the cost is
            // associated with a trip rather than dangling null-tagged.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 99.0, cost: 5000.0);

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: 110.0);

            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);
            Assert.Null(adopted.DedupKey);
            Assert.Equal(5000f, adopted.FundsSpent);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("TryAdoptRolloutAction: recording 'rec-A' adopted rollout action"));
        }

        [Fact]
        public void TryAdoptRolloutAction_OutsideWindow_LeavesActionUnclaimed()
        {
            // A stale rollout from a launch session more than RolloutAdoptionWindowSeconds
            // before the new recording must NOT be hijacked.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 100.0, cost: 5000.0);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds + 30.0;

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT);

            Assert.Null(adopted);
            // Original rollout action stays as-is — null RecordingId and intact DedupKey.
            var rollout = Ledger.Actions.First(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Null(rollout.RecordingId);
            Assert.StartsWith("rollout:", rollout.DedupKey);
        }

        [Fact]
        public void TryAdoptRolloutAction_AlreadyAdopted_DoesNotReclaim()
        {
            // Double-adoption guard: once a rollout is owned by recording rec-A, a later
            // recording rec-B must not steal it.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 100.0, cost: 5000.0);
            LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: 110.0);

            var second = LedgerOrchestrator.TryAdoptRolloutAction("rec-B", startUT: 115.0);

            Assert.Null(second);
        }

        // ----------------------------------------------------------------
        // CreateVesselCostActions integration
        // ----------------------------------------------------------------

        [Fact]
        public void CreateVesselCostActions_AdoptsRolloutInsteadOfDoubleCharging()
        {
            // The full normal launch+record flow:
            //   1. KSP fires VesselRollout, OnVesselRolloutSpending writes a 5000-funds
            //      FundsSpending(VesselBuild) at UT=99 with null RecordingId.
            //   2. Recording starts at UT=100 — CapturePreLaunchResources sees funds=45000
            //      (post-rollout). Recording ends at UT=200 with funds=45000 (no in-flight
            //      cost). PreLaunchFunds - first-point = 0, so no recording-side build
            //      action is created.
            //   3. CreateVesselCostActions adopts the rollout instead of leaving it
            //      null-tagged AND must not emit a duplicate VesselBuild action.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 99.0, cost: 5000.0);

            var rec = new Recording
            {
                RecordingId = "rec-launched",
                PreLaunchFunds = 45000.0,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 45000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 45000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-launched", startUT: 100.0, endUT: 200.0);

            // CreateVesselCostActions itself returns no NEW build action — the rollout
            // was adopted in place inside the ledger. The adopted action stays under
            // FundsSpending(VesselBuild) but now carries the recording's id.
            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            var ledgerRollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-launched", ledgerRollout.RecordingId);
            Assert.Null(ledgerRollout.DedupKey);
            Assert.Equal(5000f, ledgerRollout.FundsSpent);
        }

        [Fact]
        public void CreateVesselCostActions_NoRollout_StillEmitsFromRecordingDelta()
        {
            // Backward-compat case: recording was started on a vessel that did not
            // come through the rollout path (e.g. spawned in flight, or the Parsek
            // mod was just installed and missed the launch's VesselRollout fire).
            // CreateVesselCostActions must still derive the build cost from the
            // recording's PreLaunchFunds-to-first-point delta, exactly as before.
            var rec = new Recording
            {
                RecordingId = "rec-no-rollout",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 45000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 45000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-no-rollout", startUT: 100.0, endUT: 200.0);

            var build = actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-no-rollout", build.RecordingId);
            Assert.Equal(5000f, build.FundsSpent);
        }

        [Fact]
        public void CancelledRollout_LeavesUnclaimedSpendingInLedger()
        {
            // The headline #445 case: rollout fires, player cancels without recording.
            // The ledger MUST keep the FundsSpending(VesselBuild) action so its funds
            // total stays in sync with KSP's deducted balance.
            LedgerOrchestrator.OnVesselRolloutSpending(ut: 100.0, cost: 7500.0);

            // Player walks back to VAB without launching — no recording is ever
            // committed and CreateVesselCostActions is never called.
            var stranded = Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild).ToList();

            Assert.Single(stranded);
            Assert.Equal(7500f, stranded[0].FundsSpent);
            Assert.Null(stranded[0].RecordingId);
        }
    }
}
