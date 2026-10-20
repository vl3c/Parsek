using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design section 2.12 Rule 5: the row filing of a recording commit is split into a
    /// pure build step and the append step. The pin below records what
    /// <see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted"/> files for a tree that
    /// reaches every producer (events, pending science, vessel build cost with rollout
    /// adoption, a recovery awaiting the stock payout, crew end states, the kerbal-death
    /// reputation penalty), so the split leaves the filed ledger byte-identical.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerCommitRowBuildTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bill = "Bill Kerman";
        private const string TreeId = "tree-pin";
        private const string BpId = "bp-pin";
        private const string LaunchId = "pin-launch";
        private const string CapsuleId = "pin-capsule";
        private const string BoosterId = "pin-booster";

        public LedgerCommitRowBuildTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        internal static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static void AddEvent(GameStateEventType type, string key, double ut,
            string recordingId, string detail = "", double before = 0.0, double after = 0.0)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId ?? "",
            };
            GameStateStore.AddEvent(ref e);
        }

        /// <summary>
        /// Every value of every ledger row except the per-construction ActionId, in ledger
        /// order: the filed ledger as a save would carry it.
        /// </summary>
        internal static string CanonicalLedger(IEnumerable<GameAction> actions)
        {
            var sb = new StringBuilder();
            foreach (var a in actions)
            {
                var parent = new ConfigNode("ROOT");
                a.SerializeInto(parent);
                var node = parent.GetNodes()[0];
                for (int i = 0; i < node.values.Count; i++)
                {
                    if (node.values[i].name == "actionId") continue;
                    sb.Append(node.values[i].name).Append('=').Append(node.values[i].value).Append(';');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// A launch from the pad (rollout adopted, a residual build cost, a milestone and a
        /// pending science subject), a capsule child that dies with Bill (crew death plus
        /// the VesselLoss reputation hit, and a contract completed on the way down), and a
        /// recovered booster whose stock payout is still pending.
        /// </summary>
        internal static RecordingTree BuildPinTree()
        {
            LedgerOrchestrator.OnVesselRolloutSpending(99.0, 5000.0, 101u, "Pad Hopper", "LaunchPad");

            var launch = new Recording
            {
                RecordingId = LaunchId,
                VesselName = "Pad Hopper",
                TreeId = TreeId,
                StartSituation = "Prelaunch",
                LaunchSiteName = "LaunchPad",
                VesselPersistentId = 101u,
                PreLaunchFunds = 45000.0,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                VesselSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                ChildBranchPointId = BpId,
            };
            launch.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 44000.0 });
            launch.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 44000.0 });

            var capsule = new Recording
            {
                RecordingId = CapsuleId,
                VesselName = "Pad Hopper Capsule",
                TreeId = TreeId,
                ParentBranchPointId = BpId,
                GhostVisualSnapshot = CrewSnapshot(Bill),
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 300.0,
                TerminalStateValue = TerminalState.Destroyed,
            };

            var booster = new Recording
            {
                RecordingId = BoosterId,
                VesselName = "Pad Hopper Booster",
                TreeId = TreeId,
                ParentBranchPointId = BpId,
                GhostVisualSnapshot = CrewSnapshot(),
                VesselSnapshot = CrewSnapshot(),
                ExplicitStartUT = 200.0,
                ExplicitEndUT = 260.0,
                TerminalStateValue = TerminalState.Recovered,
            };
            booster.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 44000.0 });
            booster.Points.Add(new TrajectoryPoint { ut = 260.0, funds = 46000.0 });
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting(BoosterId);

            AddEvent(GameStateEventType.MilestoneAchieved, "RecordsDistance", 150.0, LaunchId,
                GameStateRecorder.BuildMilestoneDetail(4800.0, 2.0f, 0.0));
            AddEvent(GameStateEventType.ContractCompleted, "guid-pin", 250.0, CapsuleId,
                "fundsReward=3000;repReward=5;sciReward=1");
            AddEvent(GameStateEventType.ReputationChanged, "VesselLoss", 300.0, CapsuleId,
                "", 0.0, -9.5);
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "crewReport@KerbinFlyingLow",
                science = 4.5f,
                subjectMaxValue = 6f,
                captureUT = 160.0,
                reasonKey = "VesselRecovery",
                recordingId = LaunchId,
            });

            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Pad Hopper",
                RootRecordingId = LaunchId,
                ActiveRecordingId = CapsuleId,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BpId,
                UT = 200.0,
                Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { LaunchId },
                ChildRecordingIds = new List<string> { CapsuleId, BoosterId },
            });
            foreach (var rec in new[] { launch, capsule, booster })
                tree.AddOrReplaceRecording(rec);
            return tree;
        }

        internal static void CommitTree(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // Pinned BEFORE the build / append split from the unsplit filing code.
        private const string PinnedTreeCommitLedger =
            "ut=99;type=3;recordingId=pin-launch;seq=1;fundsSpent=5000;fundsSpendingSource=0;\n" +
            "ut=150;type=4;recordingId=pin-launch;seq=1;milestoneId=RecordsDistance;milestoneFundsAwarded=4800;milestoneRepAwarded=2;milestoneSciAwarded=0;\n" +
            "ut=200;type=0;recordingId=pin-launch;seq=1;subjectId=crewReport@KerbinFlyingLow;scienceAwarded=4.5;method=1;transmitScalar=0;subjectMaxValue=6;startUT=160;endUT=200;\n" +
            "ut=100;type=3;recordingId=pin-launch;fundsSpent=1000;fundsSpendingSource=0;\n" +
            "ut=100;type=11;recordingId=pin-launch;seq=1;kerbalName=Jebediah Kerman;kerbalRole=Pilot;startUT=100;endUT=200;endState=3;xpGained=0;\n" +
            "ut=0;type=22;initialReputation=0;\n" +
            "ut=250;type=6;recordingId=pin-capsule;seq=1;contractId=guid-pin;fundsReward=3000;repReward=5;scienceReward=1;\n" +
            "ut=200;type=11;recordingId=pin-capsule;seq=1;kerbalName=Bill Kerman;kerbalRole=Pilot;startUT=200;endUT=300;endState=1;xpGained=0;\n" +
            "ut=300;type=10;recordingId=pin-capsule;nominalPenalty=9.5;repPenaltySource=2;\n" +
            "ut=0;type=21;initialScience=0;\n";

        [Fact]
        public void NotifyLedgerTreeCommitted_FiledRows_ArePinned()
        {
            var tree = BuildPinTree();
            CommitTree(tree);

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            string canonical = CanonicalLedger(Ledger.Actions);
            Assert.True(PinnedTreeCommitLedger == canonical, "ledger:\n" + canonical);
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting(BoosterId));
        }
    }
}
