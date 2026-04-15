using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §E/#403 of career-earnings-bundle plan: CreateVesselCostActions must
    /// emit FundsEarning(Recovery) whenever TerminalStateValue == Recovered and the
    /// recording has at least two points with a positive funds delta. The review
    /// (§1.3) explicitly called out that the todo's claim about CreateVesselCostActions
    /// "silently bailing" was a misdiagnosis — this regression test locks in the
    /// actual behavior so any future refactor cannot silently drop recovery
    /// earnings.
    ///
    /// Also guards the "Recovered state is required" contract: non-Recovered
    /// recordings (Destroyed, Landed, etc.) must NOT produce FundsEarning.
    /// </summary>
    [Collection("Sequential")]
    public class VesselCostRecoveryRegressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselCostRecoveryRegressionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TwoPoints_Recovered_ProducesRecoveryEarning()
        {
            // The minimal shape required by the review §E: 2 points, Recovered state,
            // positive funds delta between them.
            var rec = new Recording
            {
                RecordingId = "rec-two-point-recovery",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 48500.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-two-point-recovery", 100.0, 200.0);

            // One FundsEarning(Recovery) for 8500 + one FundsSpending(VesselBuild) for 10000.
            var recovery = actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery);
            Assert.NotNull(recovery);
            Assert.Equal(8500f, recovery.FundsAwarded);
            Assert.Equal(200.0, recovery.UT);
            Assert.Equal("rec-two-point-recovery", recovery.RecordingId);
        }

        [Fact]
        public void NotRecovered_Destroyed_NoFundsEarning()
        {
            // Destroyed state must NOT yield a recovery. Even though the funds delta
            // exists in the recording, those "funds" are synthetic from earlier captures.
            var rec = new Recording
            {
                RecordingId = "rec-destroyed",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Destroyed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 42000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-destroyed", 100.0, 200.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsEarning);
        }

        [Fact]
        public void Recovered_ButRecoveryDeltaIsZero_NoEarning()
        {
            // Recovered state with zero funds delta should not fabricate an earning.
            var rec = new Recording
            {
                RecordingId = "rec-recovered-zero",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-recovered-zero", 100.0, 200.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsEarning);
        }
    }
}
