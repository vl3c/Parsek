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
    /// at the moment of the transaction (tagged with a rollout dedup key that carries
    /// the vessel PID), tag it directly when a recording is already live, and let a
    /// subsequent recording adopt the action via
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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 42, recordingId: null);

            var actions = Ledger.Actions;
            var rollout = actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.NotNull(rollout);
            Assert.Null(rollout.RecordingId);
            Assert.Equal(5000f, rollout.FundsSpent);
            Assert.Equal(100.0, rollout.UT);
            Assert.Equal("rollout:42:100", rollout.DedupKey);

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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 0.0, vesselPersistentId: 0, recordingId: null);
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 200.0, cost: -1.0, vesselPersistentId: 0, recordingId: null);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("OnVesselRolloutSpending: non-positive cost"));
        }

        [Fact]
        public void OnVesselRolloutSpending_LiveRecordingTag_OwnsActionImmediately()
        {
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 77, recordingId: "rec-live");

            var rollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.Equal("rec-live", rollout.RecordingId);
            Assert.Equal("rollout:77:100", rollout.DedupKey);
            Assert.Equal(5000f, rollout.FundsSpent);
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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 99.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-A", startUT: 110.0, vesselPersistentId: 10);

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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds + 30.0;

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-A", startUT, vesselPersistentId: 10);

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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);
            LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-A", startUT: 110.0, vesselPersistentId: 10);

            var second = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-B", startUT: 115.0, vesselPersistentId: 10);

            Assert.Null(second);

            // Stronger: the original adoption must survive intact — rec-A still owns
            // the (now-DedupKey-cleared) VesselBuild action, and no other VesselBuild
            // action exists.
            var owned = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-A", owned.RecordingId);
            Assert.Null(owned.DedupKey);
            Assert.Equal(5000f, owned.FundsSpent);
            Assert.Equal(100.0, owned.UT);
        }

        [Fact]
        public void TryAdoptRolloutAction_AtExactWindowBoundary_Adopts()
        {
            // Boundary pin: 60.0 s gap exactly is INSIDE the window (strict > comparison).
            // Adoption must succeed.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds; // 160.0

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-A", startUT, vesselPersistentId: 10);

            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);
            Assert.Equal(5000f, adopted.FundsSpent);
        }

        [Fact]
        public void TryAdoptRolloutAction_JustOutsideWindow_DoesNotAdopt()
        {
            // Boundary pin: 60.0 + epsilon is OUTSIDE the window.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds + 0.1; // 160.1

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-A", startUT, vesselPersistentId: 10);

            Assert.Null(adopted);
        }

        [Fact]
        public void TryAdoptRolloutAction_MultipleUnclaimed_AdoptsMostRecent()
        {
            // LIFO: player rolls out (cost1) at UT=100, cancels, rolls out again (cost2)
            // at UT=130, then launches the recording at UT=140. The rollout
            // immediately preceding the launch (cost2 at 130) is the one that should be
            // adopted; the older cancelled one (cost1 at 100) stays stranded null-tagged.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 4000.0, vesselPersistentId: 10, recordingId: null);
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 130.0, cost: 7000.0, vesselPersistentId: 10, recordingId: null);

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-launched", startUT: 140.0, vesselPersistentId: 10);

            Assert.NotNull(adopted);
            // Most recent rollout wins.
            Assert.Equal(7000f, adopted.FundsSpent);
            Assert.Equal(130.0, adopted.UT);
            Assert.Equal("rec-launched", adopted.RecordingId);

            // Older rollout still sits null-tagged so the ledger total stays in sync
            // with KSP's actual deduction (the cancelled one was never refunded).
            var stranded = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                a.RecordingId == null);
            Assert.Equal(4000f, stranded.FundsSpent);
            Assert.Equal(100.0, stranded.UT);
            Assert.StartsWith("rollout:", stranded.DedupKey);
        }

        [Fact]
        public void TryAdoptRolloutAction_MismatchedVesselPid_DoesNotAdopt()
        {
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 4000.0, vesselPersistentId: 10, recordingId: null);
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 130.0, cost: 7000.0, vesselPersistentId: 20, recordingId: null);

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-launched", startUT: 140.0, vesselPersistentId: 10);

            Assert.NotNull(adopted);
            Assert.Equal(4000f, adopted.FundsSpent);
            Assert.Equal(10u, LedgerOrchestrator.GetRolloutVesselPid("rollout:10:100"));

            var foreign = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                a.FundsSpent == 7000f);
            Assert.Null(foreign.RecordingId);
            Assert.Equal("rollout:20:130", foreign.DedupKey);
        }

        [Fact]
        public void TryAdoptRolloutAction_LegacyUnclaimedKey_DoesNotBypassPidGate()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 4000f,
                RecordingId = null,
                DedupKey = "rollout:100"
            });

            Assert.Equal(0u, LedgerOrchestrator.GetRolloutVesselPid("rollout:100"));
            Assert.False(LedgerOrchestrator.RolloutActionMatchesVessel("rollout:100", 10));

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-after-upgrade", startUT: 110.0, vesselPersistentId: 10);

            Assert.Null(adopted);

            var legacy = Ledger.Actions.Single();
            Assert.Null(legacy.RecordingId);
            Assert.Equal("rollout:100", legacy.DedupKey);
        }

        [Fact]
        public void FindOrAdoptRolloutAction_LegacyOwnedKey_UsesRecordingOwnership()
        {
            var legacyOwned = new GameAction
            {
                UT = 99.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                RecordingId = "rec-live",
                DedupKey = "rollout:99"
            };
            Ledger.AddAction(legacyOwned);

            bool ownedDuringRecording;
            var rollout = LedgerOrchestrator.FindOrAdoptRolloutAction(
                "rec-live", startUT: 100.0, vesselPersistentId: 10, out ownedDuringRecording);

            Assert.Same(legacyOwned, rollout);
            Assert.True(ownedDuringRecording);
            Assert.Equal("rollout:99", rollout.DedupKey);
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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 99.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);

            var rec = new Recording
            {
                RecordingId = "rec-launched",
                VesselPersistentId = 10,
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
                VesselPersistentId = 10,
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
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 7500.0, vesselPersistentId: 10, recordingId: null);

            // Player walks back to VAB without launching — no recording is ever
            // committed and CreateVesselCostActions is never called.
            var stranded = Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild).ToList();

            Assert.Single(stranded);
            Assert.Equal(7500f, stranded[0].FundsSpent);
            Assert.Null(stranded[0].RecordingId);
        }

        [Fact]
        public void CreateVesselCostActions_AdoptedRolloutPlusResidualDelta_EmitsBoth()
        {
            // Rare case: the rollout deducts the build cost, then the recording observes
            // an additional cost between PreLaunchFunds (captured pre-launchpad) and
            // the first frame's funds (captured post-launchpad). Both must land on the
            // ledger — the adopted rollout under its UT, and a recording-tagged residual
            // FundsSpending(VesselBuild) on top.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 99.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);

            var rec = new Recording
            {
                RecordingId = "rec-residual",
                VesselPersistentId = 10,
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Landed
            };
            // Pre-launch: 50000 — first point: 49250 — implies 750 of additional
            // spending landed between PreLaunchFunds capture and first frame.
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 49250.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 49250.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-residual", startUT: 100.0, endUT: 200.0);

            // The 750 residual action must be returned by CreateVesselCostActions,
            // tagged to the recording.
            var residual = actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-residual", residual.RecordingId);
            Assert.Equal(750f, residual.FundsSpent);
            Assert.Equal(100.0, residual.UT);

            // The adopted rollout still sits in the ledger under its own UT, now owned
            // by the recording — the cost is split across two actions, neither lost.
            var adopted = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild &&
                a.RecordingId == "rec-residual" &&
                a.UT == 99.0);
            Assert.Equal(5000f, adopted.FundsSpent);
            Assert.Null(adopted.DedupKey);

            // Diagnostics: the residual branch must log the split so post-mortem
            // analysis sees both contributions.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("residual buildCost=750") &&
                l.Contains("rolloutCost=5000"));
        }

        [Fact]
        public void CreateVesselCostActions_LiveTaggedRolloutBeforeFirstPoint_DoesNotDoubleCharge()
        {
            // Manual pre-launch recording: the rollout fires while the recorder is already
            // live, so OnVesselRolloutSpending tags the action directly to the recording.
            // If the first sampled point lands after the rollout, CreateVesselCostActions
            // must subtract the already-owned rollout cost instead of charging it again.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 99.0, cost: 5000.0, vesselPersistentId: 10, recordingId: "rec-live");

            var rec = new Recording
            {
                RecordingId = "rec-live",
                VesselPersistentId = 10,
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 45000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 45000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-live", startUT: 100.0, endUT: 200.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            var ledgerRollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-live", ledgerRollout.RecordingId);
            Assert.Equal("rollout:10:99", ledgerRollout.DedupKey);
            Assert.Equal(5000f, ledgerRollout.FundsSpent);
        }

        [Fact]
        public void CreateVesselCostActions_LiveTaggedRolloutPlusResidualDelta_EmitsOnlyResidual()
        {
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 99.0, cost: 5000.0, vesselPersistentId: 10, recordingId: "rec-live");

            var rec = new Recording
            {
                RecordingId = "rec-live",
                VesselPersistentId = 10,
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 44250.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 44250.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-live", startUT: 100.0, endUT: 200.0);

            var residual = actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal(750f, residual.FundsSpent);
            Assert.Equal("rec-live", residual.RecordingId);

            var rollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-live", rollout.RecordingId);
            Assert.Equal("rollout:10:99", rollout.DedupKey);
            Assert.Equal(5000f, rollout.FundsSpent);
        }

        // ----------------------------------------------------------------
        // Save/load roundtrip
        // ----------------------------------------------------------------

        [Fact]
        public void RolloutAction_RoundTripsThroughSerializeDeserialize_RemainsAdoptable()
        {
            // PR #307 follow-up made GameAction.Serialize/DeserializeFundsSpending
            // round-trip the DedupKey. Our new "rollout:<pid>:<UT>" tag must survive a .sfs
            // save/load and still be adoptable on the next launch — otherwise a player
            // who quits + reloads after a rollout but before launching would lose the
            // adoption hook and end up double-charged on the next launch+record.
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut: 100.0, cost: 5000.0, vesselPersistentId: 10, recordingId: null);
            var original = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            string originalDedupKey = original.DedupKey;
            Assert.StartsWith("rollout:", originalDedupKey);

            // Round-trip through ConfigNode.
            var parent = new ConfigNode("LEDGER");
            original.SerializeInto(parent);
            var actionNode = parent.GetNode("GAME_ACTION");
            var reloaded = GameAction.DeserializeFrom(actionNode);

            // DedupKey survived intact and still passes the StartsWith("rollout:")
            // gate inside TryAdoptRolloutAction.
            Assert.Equal(originalDedupKey, reloaded.DedupKey);
            Assert.Null(reloaded.RecordingId);
            Assert.Equal(GameActionType.FundsSpending, reloaded.Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, reloaded.FundsSpendingSource);
            Assert.Equal(5000f, reloaded.FundsSpent);
            Assert.Equal(100.0, reloaded.UT);

            // Replace the original in-memory action with the deserialized one to
            // simulate a load and verify adoption still works on a recording started
            // after the reload.
            LedgerOrchestrator.ResetForTesting();
            Ledger.AddAction(reloaded);

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-after-load", startUT: 110.0, vesselPersistentId: 10);

            Assert.NotNull(adopted);
            Assert.Equal("rec-after-load", adopted.RecordingId);
            Assert.Null(adopted.DedupKey);
            Assert.Equal(5000f, adopted.FundsSpent);
        }
    }
}
