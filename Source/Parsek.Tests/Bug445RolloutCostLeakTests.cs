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
    /// at the moment of the transaction (tagged with a <c>"rollout:"</c>-prefixed
    /// dedup key that includes rollout context), and let a subsequent recording adopt the
    /// action via
    /// <see cref="LedgerOrchestrator.TryAdoptRolloutAction"/> instead of double-charging.
    /// </summary>
    [Collection("Sequential")]
    public class Bug445RolloutCostLeakTests : IDisposable
    {
        private const uint DefaultRolloutPid = 101u;
        private const string DefaultRolloutVesselName = "Rollout Vessel";
        private const string DefaultRolloutLaunchSite = "Launch Pad";

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

        private static Recording CreateRecording(
            string recordingId,
            string startSituation = "Prelaunch",
            uint vesselPersistentId = DefaultRolloutPid,
            string vesselName = DefaultRolloutVesselName,
            string launchSiteName = DefaultRolloutLaunchSite)
        {
            return new Recording
            {
                RecordingId = recordingId,
                StartSituation = startSituation,
                VesselPersistentId = vesselPersistentId,
                VesselName = vesselName,
                LaunchSiteName = launchSiteName
            };
        }

        private static void RecordRollout(
            double ut,
            double cost,
            uint vesselPersistentId = DefaultRolloutPid,
            string vesselName = DefaultRolloutVesselName,
            string launchSiteName = DefaultRolloutLaunchSite)
        {
            LedgerOrchestrator.OnVesselRolloutSpending(
                ut,
                cost,
                vesselPersistentId,
                vesselName,
                launchSiteName);
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
            RecordRollout(
                ut: 100.0,
                cost: 5000.0,
                vesselPersistentId: 101u,
                vesselName: "Pad Hopper",
                launchSiteName: "Launch Pad");

            var actions = Ledger.Actions;
            var rollout = actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            Assert.NotNull(rollout);
            Assert.Null(rollout.RecordingId);
            Assert.Equal(5000f, rollout.FundsSpent);
            Assert.Equal(100.0, rollout.UT);
            Assert.StartsWith("rollout:", rollout.DedupKey);
            Assert.Contains("|pid=101", rollout.DedupKey);
            Assert.Contains("site=Launch%20Pad", rollout.DedupKey);
            Assert.Contains("vessel=Pad%20Hopper", rollout.DedupKey);

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

            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(1, exp.LegCount);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
            Assert.Equal("VesselRollout", exp.FundsLeg.ExpectedReasonKey);
            Assert.Equal(-5000.0, exp.FundsLeg.ExpectedDelta);
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

            Assert.True(exp.FundsLeg.IsPresent);
            Assert.Equal("RnDPartPurchase", exp.FundsLeg.ExpectedReasonKey);
        }

        // ----------------------------------------------------------------
        // TryAdoptRolloutAction
        // ----------------------------------------------------------------

        [Theory]
        [InlineData("Prelaunch", true)]
        [InlineData("PRELAUNCH", true)]
        [InlineData("Flying", false)]
        [InlineData("Orbiting", false)]
        [InlineData(null, false)]
        public void CanRecordingAdoptRolloutAction_OnlyPrelaunchStartsCanClaim(string startSituation, bool expected)
        {
            var rec = new Recording { StartSituation = startSituation };

            bool canAdopt = LedgerOrchestrator.CanRecordingAdoptRolloutAction(rec);

            Assert.Equal(expected, canAdopt);
        }

        [Fact]
        public void TryAdoptRolloutAction_WithinWindow_ClaimsAction()
        {
            // The launch+record case: rollout was logged at UT=99, recording starts at
            // UT=110. Adoption transfers ownership to the recording so the cost is
            // associated with a trip rather than dangling null-tagged.
            RecordRollout(ut: 99.0, cost: 5000.0);
            var rec = CreateRecording("rec-A");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: 110.0, rec);

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
            RecordRollout(ut: 100.0, cost: 5000.0);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds + 30.0;
            var rec = CreateRecording("rec-A");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT, rec);

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
            RecordRollout(ut: 100.0, cost: 5000.0);
            var firstRec = CreateRecording("rec-A");
            var secondRec = CreateRecording("rec-B");
            LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT: 110.0, firstRec);

            var second = LedgerOrchestrator.TryAdoptRolloutAction("rec-B", startUT: 115.0, secondRec);

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
            // Boundary pin: a gap exactly equal to RolloutAdoptionWindowSeconds is
            // INSIDE the window (strict > comparison). Adoption must succeed.
            RecordRollout(ut: 100.0, cost: 5000.0);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds;
            var rec = CreateRecording("rec-A");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT, rec);

            Assert.NotNull(adopted);
            Assert.Equal("rec-A", adopted.RecordingId);
            Assert.Equal(5000f, adopted.FundsSpent);
        }

        [Fact]
        public void TryAdoptRolloutAction_JustOutsideWindow_DoesNotAdopt()
        {
            // Boundary pin: RolloutAdoptionWindowSeconds + epsilon is OUTSIDE the window.
            RecordRollout(ut: 100.0, cost: 5000.0);
            double startUT = 100.0 + LedgerOrchestrator.RolloutAdoptionWindowSeconds + 0.1;
            var rec = CreateRecording("rec-A");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-A", startUT, rec);

            Assert.Null(adopted);
        }

        [Fact]
        public void TryAdoptRolloutAction_SameSiteDifferentVessel_DoesNotAdopt()
        {
            // Targeted recheck fix: a stranded rollout from vessel A must not be claimed
            // by an unrelated prelaunch recording B just because both happened at the
            // launch pad inside the widened 30-minute window.
            RecordRollout(
                ut: 100.0,
                cost: 5000.0,
                vesselPersistentId: 101u,
                vesselName: "Vessel A",
                launchSiteName: "Launch Pad");

            var unrelated = CreateRecording(
                "rec-B",
                startSituation: "Prelaunch",
                vesselPersistentId: 202u,
                vesselName: "Vessel B",
                launchSiteName: "Launch Pad");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-B", startUT: 110.0, unrelated);

            Assert.Null(adopted);
            var rollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Null(rollout.RecordingId);
            Assert.StartsWith("rollout:", rollout.DedupKey);
        }

        [Fact]
        public void TryAdoptRolloutAction_SameVesselDifferentSite_DoesNotAdopt()
        {
            // Site correlation matters too: reusing the same craft name on the runway
            // must not steal a stranded launch-pad rollout.
            RecordRollout(
                ut: 100.0,
                cost: 5000.0,
                vesselPersistentId: 0u,
                vesselName: "Twin",
                launchSiteName: "Launch Pad");

            var unrelated = CreateRecording(
                "rec-B",
                startSituation: "Prelaunch",
                vesselPersistentId: 0u,
                vesselName: "Twin",
                launchSiteName: "Runway");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-B", startUT: 110.0, unrelated);

            Assert.Null(adopted);
            var rollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Null(rollout.RecordingId);
            Assert.StartsWith("rollout:", rollout.DedupKey);
        }

        [Fact]
        public void TryAdoptRolloutAction_MultipleUnclaimedMatchingContext_AdoptsMostRecent()
        {
            // LIFO: player rolls out (cost1) at UT=100, cancels, rolls out again (cost2)
            // at UT=130, then launches the recording at UT=140. The rollout
            // immediately preceding the launch (cost2 at 130) is the one that should be
            // adopted; the older cancelled one (cost1 at 100) stays stranded null-tagged.
            RecordRollout(ut: 100.0, cost: 4000.0);
            RecordRollout(ut: 130.0, cost: 7000.0);
            var rec = CreateRecording("rec-launched");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-launched", startUT: 140.0, rec);

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
            RecordRollout(ut: 99.0, cost: 5000.0);

            var rec = CreateRecording("rec-launched");
            rec.PreLaunchFunds = 45000.0;
            rec.TerminalStateValue = TerminalState.Landed;
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
            var rec = CreateRecording("rec-no-rollout");
            rec.PreLaunchFunds = 50000.0;
            rec.TerminalStateValue = TerminalState.Landed;
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
            RecordRollout(ut: 100.0, cost: 7500.0);

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
        public void CreateVesselCostActions_PrelaunchAfterLongPadWait_StillAdoptsRollout()
        {
            // Review fix: the original 60 s adoption window orphaned legitimate launch
            // recordings if the player sat on the pad/runway before pressing Record.
            // A prelaunch recording started several minutes after rollout must still
            // claim the build cost so later delete/discard purges remove it correctly.
            RecordRollout(ut: 100.0, cost: 5000.0);

            double startUT = 100.0 + 300.0;
            var rec = CreateRecording("rec-long-pad-wait");
            rec.PreLaunchFunds = 45000.0;
            rec.TerminalStateValue = TerminalState.Landed;
            rec.Points.Add(new TrajectoryPoint { ut = startUT, funds = 45000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 500.0, funds = 45000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-long-pad-wait", startUT: startUT, endUT: 500.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);

            var adopted = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Equal("rec-long-pad-wait", adopted.RecordingId);
            Assert.Null(adopted.DedupKey);
            Assert.Equal(5000f, adopted.FundsSpent);
        }

        [Fact]
        public void CreateVesselCostActions_FlyingStart_DoesNotAdoptRollout()
        {
            // Review fix: the branch originally let any recording inside the time window
            // adopt the rollout. A late Record click after liftoff must NOT steal the
            // vessel build cost onto a mid-flight recording, or deleting that recording
            // would incorrectly refund a real launch expense.
            RecordRollout(ut: 100.0, cost: 5000.0);

            var rec = CreateRecording("rec-midflight", startSituation: "Flying");
            rec.PreLaunchFunds = 45000.0;
            rec.TerminalStateValue = TerminalState.Landed;
            rec.Points.Add(new TrajectoryPoint { ut = 110.0, funds = 45000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 45000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-midflight", startUT: 110.0, endUT: 200.0);

            Assert.Empty(actions);

            var rollout = Ledger.Actions.Single(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.VesselBuild);
            Assert.Null(rollout.RecordingId);
            Assert.StartsWith("rollout:", rollout.DedupKey);
        }

        [Fact]
        public void CreateVesselCostActions_AdoptedRolloutPlusResidualDelta_EmitsBoth()
        {
            // Rare case: the rollout deducts the build cost, then the recording observes
            // an additional cost between PreLaunchFunds (captured pre-launchpad) and
            // the first frame's funds (captured post-launchpad). Both must land on the
            // ledger — the adopted rollout under its UT, and a recording-tagged residual
            // FundsSpending(VesselBuild) on top.
            RecordRollout(ut: 99.0, cost: 5000.0);

            var rec = CreateRecording("rec-residual");
            rec.PreLaunchFunds = 50000.0;
            rec.TerminalStateValue = TerminalState.Landed;
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

        // ----------------------------------------------------------------
        // Save/load roundtrip
        // ----------------------------------------------------------------

        [Fact]
        public void RolloutAction_RoundTripsThroughSerializeDeserialize_RemainsAdoptable()
        {
            // PR #307 follow-up made GameAction.Serialize/DeserializeFundsSpending
            // round-trip the DedupKey. Our new "rollout:" context key must survive a .sfs
            // save/load and still be adoptable on the next launch — otherwise a player
            // who quits + reloads after a rollout but before launching would lose the
            // adoption hook and end up double-charged on the next launch+record.
            RecordRollout(ut: 100.0, cost: 5000.0);
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
            var rec = CreateRecording("rec-after-load");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction("rec-after-load", startUT: 110.0, rec);

            Assert.NotNull(adopted);
            Assert.Equal("rec-after-load", adopted.RecordingId);
            Assert.Null(adopted.DedupKey);
            Assert.Equal(5000f, adopted.FundsSpent);
        }

        [Fact]
        public void LegacyBareRolloutKey_RoundTripsThroughSerializeDeserialize_RemainsAdoptable()
        {
            // Targeted recheck fix: the immediately previous follow-up persisted
            // rollout markers as bare "rollout:<UT>" keys. After upgrading to the
            // vessel/site-aware matcher, those saves still need to reattach the
            // stored charge on the next launch+record instead of leaving it orphaned
            // after load.
            var legacy = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpent = 5000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = "rollout:100"
            };

            var parent = new ConfigNode("LEDGER");
            legacy.SerializeInto(parent);
            var actionNode = parent.GetNode("GAME_ACTION");
            var reloaded = GameAction.DeserializeFrom(actionNode);

            Assert.Equal("rollout:100", reloaded.DedupKey);
            Assert.Null(reloaded.RecordingId);
            Assert.Equal(GameActionType.FundsSpending, reloaded.Type);
            Assert.Equal(FundsSpendingSource.VesselBuild, reloaded.FundsSpendingSource);

            LedgerOrchestrator.ResetForTesting();
            Ledger.AddAction(reloaded);
            var rec = CreateRecording("rec-after-upgrade-load");

            var adopted = LedgerOrchestrator.TryAdoptRolloutAction(
                "rec-after-upgrade-load",
                startUT: 110.0,
                rec);

            Assert.NotNull(adopted);
            Assert.Equal("rec-after-upgrade-load", adopted.RecordingId);
            Assert.Null(adopted.DedupKey);
            Assert.Equal(5000f, adopted.FundsSpent);
        }
    }
}
