using System;
using System.Collections.Generic;
using System.Globalization;
using CommNet;

namespace Parsek.InGameTests
{
    /// <summary>
    /// A ghost relay's recorded timeline carried through a real rails warp (design section
    /// 15.6), read after the CN-3 lane's warp over the <c>ghost-commnet-timeline</c> preset:
    /// <list type="bullet">
    ///   <item><description>a deployable relay (HG-5) relays from its recorded
    ///       <c>DeployableExtended</c> event on and not before (scenario 9);</description></item>
    ///   <item><description>a relay whose recording ends Destroyed has no node once its end
    ///       has passed (scenario 10);</description></item>
    ///   <item><description>a relay whose Orbiting end fell inside the warp (its spawn deferred
    ///       by the warp, then retried and spawned in the same frame at its EndUT, so its node
    ///       is never held) has spawned, and the real vessel carries its own stock CommNet
    ///       node while the ghost node is gone (the hand-off half of scenario 1). Scenario
    ///       16's held-node position is not reachable here and stays unit-tested.</description></item>
    /// </list>
    /// The transitions themselves happen during the warp, so the lane's log contract carries
    /// their evidence; these cells check the state they leave behind. Outside that preset
    /// every cell skips, naming the missing context.
    /// </summary>
    public class GhostCommNetTimelineInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The ids are the contract between the injector (SyntheticRecordingTests
        // .GhostCommNetTimelinePreset) and these cells.
        internal const string DeployRelayRecordingId = "cn3-deploy-relay";
        internal const string DoomedRelayRecordingId = "cn3-doomed-relay";
        internal const string HeldRelayRecordingId = "cn3-held-relay";

        [InGameTest(Category = "GhostCommNetTimeline", Scene = GameScenes.FLIGHT,
            Description = "A deployable ghost relay relays from its recorded deploy event on, not before")]
        public void DeployableRelayJoinsAtItsDeployEvent_Flight()
        {
            Recording rec = RequirePresetRecording(DeployRelayRecordingId);
            double eventUT = double.NaN;
            for (int i = 0; rec.PartEvents != null && i < rec.PartEvents.Count; i++)
            {
                PartEvent e = rec.PartEvents[i];
                if (e.eventType == PartEventType.DeployableExtended)
                {
                    eventUT = e.ut;
                    break;
                }
            }
            InGameAssert.IsTrue(!double.IsNaN(eventUT), "the deploy relay recording carries no DeployableExtended event");
            double now = Planetarium.GetUniversalTime();
            if (now <= eventUT)
                InGameAssert.Skip(string.Format(IC,
                    "the deploy event at ut={0:F1} is still ahead (ut={1:F1}); the lane must warp past it", eventUT, now));

            CommNetwork net = GhostCommNetInGameTests.RequireStockNetwork();
            GhostCommNetManager manager = RequireManager();
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(DeployRelayRecordingId, out CommNode node),
                "the deploy relay holds no ghost node: " + GhostCommNetInGameTests.ExclusionOf(manager, DeployRelayRecordingId));
            InGameAssert.IsTrue(manager.TrySampleRegisteredTimeline(DeployRelayRecordingId, eventUT - 0.01,
                out GhostCommNetState before, out int states), "cannot sample the deploy relay's timeline");
            InGameAssert.IsTrue(manager.TrySampleRegisteredTimeline(DeployRelayRecordingId, eventUT,
                out GhostCommNetState after, out _), "cannot sample the deploy relay's timeline");
            InGameAssert.IsTrue(before.Powers.RelayPower == 0.0, string.Format(IC,
                "the deploy relay has relay power {0:R} before its deploy event", before.Powers.RelayPower));
            InGameAssert.IsTrue(after.Powers.RelayPower > 0.0,
                "the deploy relay has no relay power from its deploy event on");
            InGameAssert.IsTrue(states >= 2, string.Format(IC,
                "the deploy relay's timeline has {0} state(s); the deploy event made no breakpoint", states));

            net.Rebuild();
            InGameAssert.IsTrue(manager.TryDescribeRegistered(DeployRelayRecordingId, out GhostCommNetState current,
                out double rangeModifier, out _, out _, out _, out _), "cannot describe the deploy relay's node");
            double expected = current.Powers.RelayPower * rangeModifier;
            InGameAssert.IsTrue(node.antennaRelay.power > 0.0
                && Math.Abs(node.antennaRelay.power - expected) <= Math.Max(1e-6, expected * 1e-12),
                string.Format(IC, "the deploy relay's live node relays {0:R}, expected {1:R} after the deploy event",
                    node.antennaRelay.power, expected));

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet deployable relay (FLIGHT): key={0} eventUT={1:F1} relayBefore={2:R} relayAfter={3:R} "
                + "nodeRelay={4:R} timelineStates={5}",
                DeployRelayRecordingId, eventUT, before.Powers.RelayPower, after.Powers.RelayPower,
                node.antennaRelay.power, states));
        }

        [InGameTest(Category = "GhostCommNetTimeline", Scene = GameScenes.FLIGHT,
            Description = "A ghost relay whose recording ends Destroyed has no node after its end")]
        public void DestroyedRelayHasNoNodeAfterItsEnd_Flight()
        {
            Recording rec = RequirePresetRecording(DoomedRelayRecordingId);
            InGameAssert.IsTrue(rec.TerminalStateValue == TerminalState.Destroyed,
                "the doomed relay recording does not end Destroyed");
            double now = Planetarium.GetUniversalTime();
            if (now <= rec.EndUT)
                InGameAssert.Skip(string.Format(IC,
                    "the doomed relay's end at ut={0:F1} is still ahead (ut={1:F1}); the lane must warp past it",
                    rec.EndUT, now));

            CommNetwork net = GhostCommNetInGameTests.RequireStockNetwork();
            GhostCommNetManager manager = RequireManager();
            InGameAssert.IsTrue(!manager.TryGetRegisteredNode(DoomedRelayRecordingId, out _),
                "the doomed relay still holds a ghost node after its recorded destruction");
            InGameAssert.IsTrue(manager.TryGetExclusionReason(DoomedRelayRecordingId, out string reason),
                "the doomed relay was not offered to the manager as a candidate");
            InGameAssert.AreEqual(GhostCommNetMath.ReasonWindowEnded, reason, "the doomed relay's exclusion reason");
            string nodeName = GhostCommNetMath.NodeNameForRecording(DoomedRelayRecordingId);
            net.Rebuild();
            InGameAssert.IsTrue(CountNodesNamed(net, nodeName) == 0,
                "a network node named " + nodeName + " is still in the CommNet network");

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet destroyed relay (FLIGHT): key={0} endUT={1:F1} registered=False reason={2} networkNode=False",
                DoomedRelayRecordingId, rec.EndUT, reason));
        }

        [InGameTest(Category = "GhostCommNetTimeline", Scene = GameScenes.FLIGHT,
            Description = "A relay whose end fell inside a warp has spawned and carries its own stock CommNet node")]
        public void SpawnedRelayCarriesItsOwnStockNode_Flight()
        {
            Recording rec = RequirePresetRecording(HeldRelayRecordingId);
            double now = Planetarium.GetUniversalTime();
            if (now <= rec.EndUT)
                InGameAssert.Skip(string.Format(IC,
                    "the held relay's end at ut={0:F1} is still ahead (ut={1:F1}); the lane must warp past it",
                    rec.EndUT, now));
            InGameAssert.IsTrue(rec.VesselSpawned && rec.SpawnedVesselPersistentId != 0, string.Format(IC,
                "the held relay did not spawn after the warp (spawned={0} pid={1} abandoned={2} cannotSpawnSafely={3})",
                rec.VesselSpawned, rec.SpawnedVesselPersistentId, rec.SpawnAbandoned, rec.TerminalSpawnCannotSpawnSafely));

            Vessel spawned = null;
            var vessels = FlightGlobals.Vessels;
            for (int i = 0; vessels != null && i < vessels.Count; i++)
            {
                if (vessels[i] != null && vessels[i].persistentId == rec.SpawnedVesselPersistentId)
                {
                    spawned = vessels[i];
                    break;
                }
            }
            InGameAssert.IsNotNull(spawned, string.Format(IC,
                "no live vessel carries the held relay's spawned pid {0}", rec.SpawnedVesselPersistentId));

            CommNetwork net = GhostCommNetInGameTests.RequireStockNetwork();
            GhostCommNetManager manager = RequireManager();
            net.Rebuild();
            InGameAssert.IsTrue(spawned.connection != null && spawned.connection.Comm != null,
                "the spawned relay has no stock CommNet connection");
            CommNode comm = spawned.connection.Comm;
            InGameAssert.IsTrue(!(comm is GhostCommNetNode), "the spawned relay's CommNet node is a ghost node");
            InGameAssert.IsTrue(net.Contains(comm), "the spawned relay's stock CommNet node is not in the network");
            InGameAssert.IsTrue(comm.antennaRelay.power > 0.0, string.Format(IC,
                "the spawned relay's stock node has no relay power ({0:R})", comm.antennaRelay.power));

            InGameAssert.IsTrue(!manager.TryGetRegisteredNode(HeldRelayRecordingId, out _),
                "the held relay still holds a ghost node after its vessel spawned");
            InGameAssert.IsTrue(manager.TryGetExclusionReason(HeldRelayRecordingId, out string reason),
                "the held relay was not offered to the manager as a candidate");
            InGameAssert.AreEqual("vessel-spawned", reason, "the held relay's exclusion reason");
            string nodeName = GhostCommNetMath.NodeNameForRecording(HeldRelayRecordingId);
            InGameAssert.IsTrue(CountNodesNamed(net, nodeName) == 0,
                "a ghost node named " + nodeName + " is still in the CommNet network beside the real vessel's");

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet spawned relay handoff (FLIGHT): key={0} endUT={1:F1} pid={2} stockNode=True relay={3:R} "
                + "ghostNode=False reason={4}",
                HeldRelayRecordingId, rec.EndUT, rec.SpawnedVesselPersistentId, comm.antennaRelay.power, reason));
        }

        // ---------------------------------------------------------------------------------

        private static Recording RequirePresetRecording(string id)
        {
            Recording found = null;
            bool anyPreset = false;
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; ers != null && i < ers.Count; i++)
            {
                Recording r = ers[i];
                if (r == null) continue;
                if (r.RecordingId == DeployRelayRecordingId || r.RecordingId == DoomedRelayRecordingId
                    || r.RecordingId == HeldRelayRecordingId)
                    anyPreset = true;
                if (r.RecordingId == id) found = r;
            }
            if (!anyPreset)
                InGameAssert.Skip("needs the ghost-commnet-timeline preset (committed recordings " + DeployRelayRecordingId
                    + ", " + DoomedRelayRecordingId + ", " + HeldRelayRecordingId
                    + ") and a warp past their recorded transitions");
            InGameAssert.IsNotNull(found, "the ghost-commnet-timeline preset is partial: " + id + " is missing");
            return found;
        }

        private static GhostCommNetManager RequireManager()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null in FLIGHT");
            GhostCommNetManager manager = flight.GhostCommNet;
            InGameAssert.IsNotNull(manager, "no ghost CommNet manager in FLIGHT");
            return manager;
        }

        private static int CountNodesNamed(CommNetwork net, string name)
        {
            int count = 0;
            for (int i = 0; i < net.Count; i++)
            {
                if (net[i] != null && net[i].name == name) count++;
            }
            return count;
        }
    }
}
