using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The recovery half of the lab idle ruling (owner ruling 2026-10-17,
    /// IDLE-ON-PAD-DISCARDS-LAB-PAYOUT-FLIGHT): a flight Recover of a vessel whose lab holds
    /// science pays it at the Space Center, after every idle-on-pad check has run and
    /// untagged, so the check reads the armed recovery request (the LoadScene-prefix fast
    /// path) or the per-recording mark the finalize's Recovered stamp leaves (the Space
    /// Center OnLoad checks).
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryLabIdleVetoTests : IDisposable
    {
        private const uint Pid = 2905720181u;
        private const string LaunchGuid = "0123456789abcdef0123456789abcdef";

        private readonly List<string> logLines = new List<string>();

        public RecoveryLabIdleVetoTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree PadLabTree(string recId = "lab-rec")
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "PRELAUNCH");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", "Pad Lab");
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = "Pad Lab",
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 900.0,
                StartSituation = "Landed",
                MaxDistanceFromLaunch = 0.3,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin" });
            var tree = new RecordingTree { Id = "tree-padlab", TreeName = "Pad Lab", RootRecordingId = recId };
            rec.TreeId = tree.Id;
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        [Fact]
        public void ArmedRecoveryWithLabScience_NotIdle()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0, null, 12.5f);

            Assert.Equal(12.5f, InFlightRecoveryRequest.PendingRecoveryLabScience(
                tree, InFlightRecoveryRequest.Armed, InFlightRecoveryRequest.LabScienceAwaitingRecovery));
            Assert.True(ParsekFlight.TreeHasPendingLabRecoveryForIdle(tree, "IsActiveTreeIdleOnPad"));
            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsActiveTreeIdleOnPad: not idle - recovery pending with lab science 12.50 tree='Pad Lab'"));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("In-flight recovery request carries lab science")
                && l.Contains("labScience=12.50"));
        }

        [Fact]
        public void ArmedRecoveryWithEmptyLab_StaysIdle()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0, null, 0f);

            Assert.False(ParsekFlight.TreeHasPendingLabRecoveryForIdle(tree, "IsActiveTreeIdleOnPad"));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("recovery pending with lab science"));
        }

        [Fact]
        public void NoRecovery_StaysIdle()
        {
            RecordingTree tree = PadLabTree();

            Assert.Equal(0f, InFlightRecoveryRequest.PendingRecoveryLabScience(tree, null, null));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("recovery pending with lab science"));
        }

        [Fact]
        public void ArmedRecoveryOfAnotherLaunch_StaysIdle()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, "fedcba9876543210fedcba9876543210", "Pad Lab", 905.0, null, 12.5f);

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("recovery pending with lab science"));
        }

        // The Space Center OnLoad shape: the scene-exit finalize consumed the request and
        // stamped the tip Recovered; the lab has not paid yet.
        [Fact]
        public void StampedRecoveryWithLabScience_VetoesTheOnLoadCheckUntilCleared()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0, null, 12.5f);
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test"));
            Assert.Null(InFlightRecoveryRequest.Armed);

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - recovery pending with lab science 12.50 tree='Pad Lab'"));

            // The commit and the discard clear it (RecoveryLabMarkLifecycleTests drive both).
            Assert.Equal(1, InFlightRecoveryRequest.ClearLabRecoveryMarks(new[] { "lab-rec" }, "test"));
            Assert.Equal(0f, InFlightRecoveryRequest.PendingRecoveryLabScience(
                tree, null, InFlightRecoveryRequest.LabScienceAwaitingRecovery));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void StampedRecoveryWithEmptyLab_LeavesNoMark()
        {
            RecordingTree tree = PadLabTree();
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0);
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test"));

            Assert.Empty(InFlightRecoveryRequest.LabScienceAwaitingRecovery);
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void LabMarkMovesWithTheRecoveredTerminal()
        {
            // The commit-time optimizer moves the Recovered terminal from rec-a to rec-b.
            RecordingTree source = PadLabTree("rec-a");
            RecordingTree target = PadLabTree("rec-b");
            target.Recordings["rec-b"].TerminalStateValue = TerminalState.Recovered;
            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, "Pad Lab", 905.0, null, 4f);
            Assert.Equal(1, InFlightRecoveryRequest.ApplyAtSceneExit(source, GameScenes.SPACECENTER, "test"));

            Assert.True(InFlightRecoveryRequest.MoveAwaitingStockPayout("rec-a", "rec-b", "test"));
            Assert.Equal(4f, InFlightRecoveryRequest.PendingRecoveryLabScience(
                target, null, InFlightRecoveryRequest.LabScienceAwaitingRecovery));
            Assert.Equal(0f, InFlightRecoveryRequest.PendingRecoveryLabScience(
                source, null, InFlightRecoveryRequest.LabScienceAwaitingRecovery));
        }

        // IsActiveTreeIdleOnPad (an instance method over the live flight) and the recovery
        // request's arm site (a GameEvent handler on the scenario) cannot run headless; pin
        // that the live predicate applies both lab vetoes before its distance walk and that
        // the arm site passes the live lab science to the request.
        [Fact]
        public void LiveWiring_AppliesTheLabVetoesAndArmsWithLabScience()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string flight = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "ParsekFlight.cs")).Replace("\r\n", "\n");
            int start = flight.IndexOf("internal bool IsActiveTreeIdleOnPad()\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "IsActiveTreeIdleOnPad declaration not found");
            int walk = flight.IndexOf("foreach (var rec in activeTree.Recordings.Values)", start, StringComparison.Ordinal);
            Assert.True(walk > start, "IsActiveTreeIdleOnPad distance walk not found");
            string head = flight.Substring(start, walk - start);
            Assert.Contains("TreeHasLabPayoutsForIdle(activeTree, \"IsActiveTreeIdleOnPad\")", head);
            Assert.Contains("TreeHasPendingLabRecoveryForIdle(activeTree, \"IsActiveTreeIdleOnPad\")", head);

            string scenario = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "ParsekScenario.cs")).Replace("\r\n", "\n");
            int handler = scenario.IndexOf("private void OnVesselRecoveryRequested(Vessel v)", StringComparison.Ordinal);
            Assert.True(handler >= 0, "OnVesselRecoveryRequested not found");
            int arm = scenario.IndexOf("InFlightRecoveryRequest.Arm(", handler, StringComparison.Ordinal);
            Assert.True(arm > handler, "Arm call not found in OnVesselRecoveryRequested");
            string body = scenario.Substring(handler, arm + 300 - handler);
            Assert.Contains("ScienceLabCreditCapture.SumLiveStoredScience(v)", body);
            Assert.Contains("boundRecordingId, labStoredScience);", body);
        }

        [Fact]
        public void VetoLine_IsCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                RecordingTree tree = PadLabTree();
                tree.Recordings["lab-rec"].TerminalStateValue = TerminalState.Recovered;
                Assert.True(ParsekFlight.TreeHasPendingLabRecoveryForIdle(
                    tree, "IsTreeIdleOnPad", null,
                    new Dictionary<string, float> { { "lab-rec", 7.25f } }));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
            Assert.Contains(logLines, l =>
                l.Contains("IsTreeIdleOnPad: not idle - recovery pending with lab science 7.25 tree='Pad Lab'"));
        }
    }
}
