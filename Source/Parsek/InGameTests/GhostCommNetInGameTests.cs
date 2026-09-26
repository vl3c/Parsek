using System;
using System.Collections.Generic;
using System.Globalization;
using CommNet;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live checks of the ghost CommNet relay (design section 15.6) against the real stock
    /// CommNet network in FLIGHT and the Tracking Station:
    /// <list type="bullet">
    ///   <item><description>a deterministic three-node route (home, a ghost-node-class relay,
    ///       a free endpoint beyond the home's reach) proves stock routes through a
    ///       transform-less ghost node, with a negative control;</description></item>
    ///   <item><description>every node the scene's manager registered is in the network with
    ///       non-null range curves, at the resolver's position, with the derived powers;</description></item>
    ///   <item><description>in FLIGHT, an active vessel whose only link partners are ghost
    ///       relays routes its control path through one.</description></item>
    /// </list>
    /// </summary>
    public class GhostCommNetInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.FLIGHT,
            Description = "Stock CommNet routes a free endpoint to KSC through a ghost relay node (flight)")]
        public void GhostRelayNodeBridgesEndpointToHome_Flight()
        {
            RunRoutingProbe("FLIGHT");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.TRACKSTATION,
            Description = "Stock CommNet routes a free endpoint to KSC through a ghost relay node (tracking station)")]
        public void GhostRelayNodeBridgesEndpointToHome_TrackingStation()
        {
            RunRoutingProbe("TRACKSTATION");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.FLIGHT,
            Description = "Every registered ghost CommNet node matches its derived state (flight)")]
        public void RegisteredGhostNodesMatchDerivedState_Flight()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null in FLIGHT");
            CheckRegisteredNodes(
                flight.GhostCommNet,
                (string id, int hint, double ut, out Vector3d pos) =>
                    flight.TryResolveGhostCommNetRecordingPosition(id, hint, ut, out pos),
                "FLIGHT");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.TRACKSTATION,
            Description = "Every registered ghost CommNet node matches its derived state (tracking station)")]
        public void RegisteredGhostNodesMatchDerivedState_TrackingStation()
        {
            var ts = UnityEngine.Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts, "ParsekTrackingStation not found in TRACKSTATION");
            CheckRegisteredNodes(
                ts.GhostCommNet,
                (string id, int hint, double ut, out Vector3d pos) =>
                    ts.TryResolveGhostCommNetRecordingPosition(id, hint, ut, out pos),
                "TRACKSTATION");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.FLIGHT,
            Description = "An active vessel linked only to ghost relays routes its control path through one")]
        public void ActiveVesselControlPathUsesGhostRelay()
        {
            CommNetwork net = RequireStockNetwork();
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.connection == null || active.connection.Comm == null)
                InGameAssert.Skip("needs an active vessel with a CommNet connection");

            net.Rebuild();
            CommNode comm = active.connection.Comm;
            int ghostPartners = 0, otherPartners = 0, homePartners = 0;
            foreach (CommNode partner in comm.Keys)
            {
                if (partner == null) continue;
                if (partner.isHome) homePartners++;
                else if (partner is GhostCommNetNode) ghostPartners++;
                else otherPartners++;
            }
            if (homePartners > 0)
                InGameAssert.Skip("the active vessel links KSC directly; needs a vessel out of KSC range " +
                    "with a ghost relay (a committed relay recording in its window) in range");
            if (ghostPartners == 0)
                InGameAssert.Skip("no ghost relay node is linked to the active vessel; needs a committed " +
                    "relay recording in its ghost window within antenna range");
            if (otherPartners > 0)
                InGameAssert.Skip(string.Format(IC,
                    "the active vessel also links {0} live node(s), so a path through a ghost is not forced; " +
                    "needs the ghost relay to be the vessel's only link partner", otherPartners));

            var probe = new CommPath();
            bool routed = net.FindHome(comm, probe) || net.FindClosestControlSource(comm, probe);
            if (!routed)
                InGameAssert.Skip("the ghost relay linked to the active vessel has no route to KSC or a control point");

            CommPath controlPath = active.connection.ControlPath;
            InGameAssert.IsTrue(controlPath != null && controlPath.Count > 0,
                "a route exists through the ghost relay but the active vessel's ControlPath is empty");
            CommLink first = controlPath[0];
            bool firstHopGhost = first.a is GhostCommNetNode || first.b is GhostCommNetNode;
            InGameAssert.IsTrue(firstHopGhost, string.Format(IC,
                "ControlPath first hop does not touch a ghost node: {0} -> {1}",
                first.a != null ? first.a.name : "(null)", first.b != null ? first.b.name : "(null)"));
            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet: active vessel \"{0}\" control path hops={1} first={2}->{3} connected={4}",
                active.vesselName, controlPath.Count,
                first.a != null ? first.a.name : "(null)", first.b != null ? first.b.name : "(null)",
                active.connection.IsConnected));
        }

        private static CommNetwork RequireStockNetwork()
        {
            if (CommNetScenario.Instance == null)
                InGameAssert.Skip("CommNet is disabled in this save (difficulty setting or a CommNet-replacing mod)");
            CommNetNetwork instance = CommNetNetwork.Instance;
            CommNetwork net = instance != null ? instance.CommNet : null;
            if (net == null)
                InGameAssert.Skip("CommNet network not initialized yet");
            GhostCommNetAvailability availability = GhostCommNetMath.DecideAvailability(
                true, instance.GetType(), net.GetType(),
                CommNetScenario.RangeModel != null ? CommNetScenario.RangeModel.GetType() : null);
            if (availability != GhostCommNetAvailability.Available)
                InGameAssert.Skip("CommNet types are not stock (" + availability + "); ghost nodes are skipped by design");
            return net;
        }

        private static void RunRoutingProbe(string scene)
        {
            CommNetwork net = RequireStockNetwork();
            CommNode home = null;
            for (int i = 0; i < net.Count; i++)
            {
                CommNode n = net[i];
                if (n != null && n.isHome && n.antennaRelay.power > 0.0
                    && (home == null || n.antennaRelay.power < home.antennaRelay.power))
                    home = n;
            }
            if (home == null)
                InGameAssert.Skip("no CommNet home station with relay power in the network");

            double homePower = home.antennaRelay.power;
            const double GhostRelayPower = 1e10;
            const double EndpointPower = 1e6;
            CelestialBody homeBody = FlightGlobals.GetHomeBody();
            Vector3d homePos = home.precisePosition;
            Vector3d up = homeBody != null ? (homePos - homeBody.position).normalized : Vector3d.up;
            // Outward from the home body, half of each link's stock range (sqrt(a*b)):
            // home <-> ghost relay-relay, ghost relay <-> endpoint transmit. The endpoint is then
            // far beyond sqrt(home*endpoint), so it can only reach home through the ghost.
            double homeGhost = 0.5 * Math.Sqrt(homePower * GhostRelayPower);
            double ghostEndpoint = 0.5 * Math.Sqrt(GhostRelayPower * EndpointPower);
            double homeEndpointReach = Math.Sqrt(homePower * EndpointPower);
            InGameAssert.IsTrue(homeGhost + ghostEndpoint > 2.0 * homeEndpointReach,
                "probe geometry does not separate the endpoint from home");

            var ghost = new GhostCommNetNode
            {
                name = "ParsekTest:ghost-relay",
                displayName = "ParsekTest ghost relay",
                isHome = false,
                isControlSource = false,
                isControlSourceMultiHop = false,
            };
            ghost.antennaRelay.Update(GhostRelayPower, GhostCommNetManager.DefaultRangeCurve, false);
            ghost.antennaTransmit.Update(0.0, GhostCommNetManager.DefaultRangeCurve, false);
            ghost.precisePosition = homePos + up * homeGhost;

            var endpoint = new GhostCommNetNode
            {
                name = "ParsekTest:endpoint",
                displayName = "ParsekTest endpoint",
                isHome = false,
                isControlSource = false,
                isControlSourceMultiHop = false,
            };
            endpoint.antennaRelay.Update(0.0, GhostCommNetManager.DefaultRangeCurve, false);
            endpoint.antennaTransmit.Update(EndpointPower, GhostCommNetManager.DefaultRangeCurve, false);
            endpoint.precisePosition = homePos + up * (homeGhost + ghostEndpoint);

            try
            {
                InGameAssert.IsTrue((ghost.position - ghost.precisePosition).magnitude < 1e-6,
                    "GhostCommNetNode.position does not return precisePosition");
                net.Add(ghost);
                net.Add(endpoint);
                net.Rebuild();
                var path = new CommPath();
                bool reached = net.FindHome(endpoint, path);
                InGameAssert.IsTrue(reached, string.Format(IC,
                    "endpoint did not reach home through the ghost relay (homePower={0:R} homeGhost={1:F0} ghostEndpoint={2:F0})",
                    homePower, homeGhost, ghostEndpoint));
                bool viaGhost = false;
                for (int i = 0; i < path.Count; i++)
                {
                    if (path[i].a == ghost || path[i].b == ghost) viaGhost = true;
                }
                InGameAssert.IsTrue(viaGhost, "the endpoint's path home does not hop through the ghost node");

                // Negative control: without the ghost's power the endpoint is cut off.
                ghost.antennaRelay.power = 0.0;
                net.Rebuild();
                var none = new CommPath();
                InGameAssert.IsTrue(!net.FindHome(endpoint, none),
                    "negative control: endpoint still reached home with the ghost relay at zero power");

                ParsekLog.Info("TestRunner", string.Format(IC,
                    "GhostCommNet routing probe ({0}): home=\"{1}\" homePower={2:R} hops={3} viaGhost=True negativeControl=cut",
                    scene, home.displayName, homePower, path.Count));
            }
            finally
            {
                net.Remove(endpoint);
                net.Remove(ghost);
            }
        }

        private static void CheckRegisteredNodes(
            GhostCommNetManager manager, GhostCommNetRecordingPositionResolver resolve, string scene)
        {
            CommNetwork net = RequireStockNetwork();
            if (manager == null || manager.RegisteredCount == 0)
                InGameAssert.Skip("no ghost CommNet node is registered in " + scene + "; needs a committed recording " +
                    "of a vessel with a relay antenna (or a crewed control point) inside its ghost window");

            // Rebuild now so every node's pre-update hook ran at this frame's UT.
            net.Rebuild();
            double now = Planetarium.GetUniversalTime();
            int checkedCount = 0, dark = 0;
            foreach (string key in manager.RegisteredKeys())
            {
                InGameAssert.IsTrue(manager.TryGetRegisteredNode(key, out CommNode node), "registered key lost: " + key);
                InGameAssert.IsTrue(net.Contains(node), "registered ghost node not in the network: " + key);
                InGameAssert.IsTrue(node.antennaRelay.rangeCurve != null, "null relay range curve: " + key);
                InGameAssert.IsTrue(node.antennaTransmit.rangeCurve != null, "null transmit range curve: " + key);
                InGameAssert.IsTrue(!node.isHome, "ghost node marked home: " + key);
                InGameAssert.IsTrue(manager.TryDescribeRegistered(key, out GhostCommNetState state,
                    out double rangeModifier, out bool hold, out double endUT, out string recordingId, out int hint),
                    "cannot describe registered key " + key);
                if (recordingId == null)
                {
                    checkedCount++;
                    continue; // chain-ghosted vessel: position comes from its despawn snapshot orbit
                }
                double sampleUT = hold || now > endUT ? endUT : now;
                bool resolved = resolve(recordingId, hint, sampleUT, out Vector3d expected);
                if (!resolved)
                {
                    dark++;
                    InGameAssert.IsTrue(node.antennaRelay.power == 0.0 && node.antennaTransmit.power == 0.0,
                        "unresolved position but the node still has power: " + key);
                    continue;
                }
                double error = (node.precisePosition - expected).magnitude;
                InGameAssert.IsTrue(error < 1.0, string.Format(IC,
                    "node {0} position off the resolver by {1:F3} m", key, error));
                double relay = state.Powers.RelayPower * rangeModifier;
                double transmit = state.Powers.TransmitPower * rangeModifier;
                InGameAssert.IsTrue(Math.Abs(node.antennaRelay.power - relay) <= Math.Max(1e-6, relay * 1e-12),
                    string.Format(IC, "node {0} relay power {1:R} != derived {2:R}", key, node.antennaRelay.power, relay));
                InGameAssert.IsTrue(Math.Abs(node.antennaTransmit.power - transmit) <= Math.Max(1e-6, transmit * 1e-12),
                    string.Format(IC, "node {0} transmit power {1:R} != derived {2:R}", key, node.antennaTransmit.power, transmit));
                InGameAssert.IsTrue(node.isControlSource == state.IsControlSource,
                    "node " + key + " control flag differs from the derived state");
                checkedCount++;
            }
            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet registered nodes ({0}): checked={1} dark={2}", scene, checkedCount, dark));
        }
    }
}
