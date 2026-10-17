using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The lab mark a flight Recover leaves for the Space Center idle-on-pad checks
    /// (IDLE-ON-PAD-DISCARDS-LAB-PAYOUT-FLIGHT) must not outlive its flight: with auto-merge
    /// off, a merge clicked after stock paid takes the paired FundsChanged(VesselRecovery)
    /// branch and never consumes the payout mark, and a discard consumes nothing. A leaked
    /// mark would keep a later reload of a save taken on the pad before the Recover from the
    /// silent idle discard.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryLabMarkLifecycleTests : IDisposable
    {
        private const uint Pid = 2905720181u;
        private const string LaunchGuid = "0123456789abcdef0123456789abcdef";
        private const string RecId = "lab-rec";

        private readonly List<string> logLines = new List<string>();

        public RecoveryLabMarkLifecycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree PadLabTree()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "PRELAUNCH");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", "Pad Lab");
            var rec = new Recording
            {
                RecordingId = RecId,
                VesselName = "Pad Lab",
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 900.0,
                MaxDistanceFromLaunch = 0.3,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10.0, bodyName = "Kerbin", funds = 40000 });
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin", funds = 40000 });
            var tree = new RecordingTree { Id = "tree-padlab", TreeName = "Pad Lab", RootRecordingId = RecId };
            rec.TreeId = tree.Id;
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        /// <summary>Recover with 12.5 lab science: the finalize stamps the tip and leaves the mark.</summary>
        private static RecordingTree RecoveredWithLabMark()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0, null, 12.5f);
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test"));
            Assert.True(InFlightRecoveryRequest.LabScienceAwaitingRecovery.ContainsKey(RecId));
            return tree;
        }

        // catches: a merge clicked after the stock payout leaking the mark, because the paired
        // FundsChanged(VesselRecovery) branch never reaches TryConsumeAwaitingStockPayout.
        [Fact]
        public void CommitAfterStockPayout_PairedBranch_ClearsTheLabMark()
        {
            RecordingTree tree = RecoveredWithLabMark();
            Recording rec = tree.Recordings[RecId];
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var payout = new GameStateEvent
            {
                ut = 905.05,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 40000,
                valueAfter = 44000,
            };
            GameStateStore.AddEvent(ref payout);

            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(RecId, 10.0, 905.0, null, ref science);

            // The paired branch ran: one tagged recovery row, the payout mark untouched.
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.Recovery && a.RecordingId == RecId);
            Assert.True(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting(RecId));
            // ...and the lab mark is gone anyway.
            Assert.False(InFlightRecoveryRequest.LabScienceAwaitingRecovery.ContainsKey(RecId));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("cleared 1 lab mark(s) context=commit"));
        }

        // catches: a discard leaving the mark behind (nothing on that path consumes it).
        [Fact]
        public void DiscardOfTheRecoveredTree_ClearsTheLabMark()
        {
            RecordingTree tree = RecoveredWithLabMark();
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(InFlightRecoveryRequest.LabScienceAwaitingRecovery.ContainsKey(RecId));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("cleared 1 lab mark(s) context=discard:"));
        }

        // catches: a mark that survived on another path (a quickload abandon passes no
        // preserve, so it never reaches the discard clear) vetoing the idle discard of a
        // reload of a save taken on the pad before the Recover. The reloaded recording has
        // the same id but not the Recovered terminal the stamp wrote.
        [Fact]
        public void ReloadOfAPreRecoverSave_IgnoresALeftoverMark()
        {
            RecoveredWithLabMark();
            RecordingTree reloaded = PadLabTree();
            Assert.Equal(TerminalState.Landed, reloaded.Recordings[RecId].TerminalStateValue);
            Assert.True(InFlightRecoveryRequest.LabScienceAwaitingRecovery.ContainsKey(RecId));

            Assert.Equal(0f, InFlightRecoveryRequest.PendingRecoveryLabScience(
                reloaded, null, InFlightRecoveryRequest.LabScienceAwaitingRecovery));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(reloaded));
            Assert.DoesNotContain(logLines, l => l.Contains("recovery pending with lab science"));
        }
    }
}
