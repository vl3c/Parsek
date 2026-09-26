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
    ///       relays routes its control path through one;</description></item>
    ///   <item><description>every node the manager registered with relay power carries a free
    ///       endpoint home through itself and nothing else (the node taken out cuts it off),
    ///       and a control-source node is the endpoint's closest control source;</description></item>
    ///   <item><description>the ghost-commnet-relay preset's operator rulings: a crewed RC-L01
    ///       copy is a multi-hop control source, a playback-disabled copy still relays, a
    ///       looped copy whose real run is over registers nothing.</description></item>
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

        // ---------------------------------------------------------------------------------
        // The ghost-commnet-relay injected preset (SyntheticRecordingTests.GhostCommNetRelay*,
        // lanes CN-1 / CN-1T). Three committed copies of the same relay craft (RC-L01 +
        // RA-2), each on a Kerbin-synchronous equatorial orbit:
        //   A  crewed (a real Pilot from the save's roster in a Mk1 pod), playback ON, not
        //      looped, parked over the KSC, window open for the whole lane;
        //   B  uncrewed, playback OFF (hidden), 30 degrees east of A, same window;
        //   C  uncrewed, LOOPED, its real run over before the save's clock (loop copies only).
        // The ids are the contract between the injector and these cells.
        // ---------------------------------------------------------------------------------
        internal const string PresetRelayARecordingId = "cn-relay-a";
        internal const string PresetRelayBRecordingId = "cn-relay-b";
        internal const string PresetRelayCRecordingId = "cn-relay-c";

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.FLIGHT,
            Description = "Every registered ghost relay node carries a free endpoint home, and only through itself (flight)")]
        public void RegisteredGhostRelayRoutesEndpointHome_Flight()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null in FLIGHT");
            RunRegisteredRelayRouteProbe(flight.GhostCommNet, "FLIGHT");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.TRACKSTATION,
            Description = "Every registered ghost relay node carries a free endpoint home, and only through itself (tracking station)")]
        public void RegisteredGhostRelayRoutesEndpointHome_TrackingStation()
        {
            var ts = UnityEngine.Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts, "ParsekTrackingStation not found in TRACKSTATION");
            RunRegisteredRelayRouteProbe(ts.GhostCommNet, "TRACKSTATION");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.FLIGHT,
            Description = "Preset relays: crewed RC-L01 is a control source, hidden copy relays, looped copy has no node (flight)")]
        public void PresetRelayRulings_Flight()
        {
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null in FLIGHT");
            CheckPresetRelayRulings(flight.GhostCommNet, "FLIGHT");
        }

        [InGameTest(Category = "GhostCommNet", Scene = GameScenes.TRACKSTATION,
            Description = "Preset relays: crewed RC-L01 is a control source, hidden copy relays, looped copy has no node (tracking station)")]
        public void PresetRelayRulings_TrackingStation()
        {
            var ts = UnityEngine.Object.FindObjectOfType<ParsekTrackingStation>();
            InGameAssert.IsNotNull(ts, "ParsekTrackingStation not found in TRACKSTATION");
            CheckPresetRelayRulings(ts.GhostCommNet, "TRACKSTATION");
        }

        /// <summary>
        /// For every node the scene's manager registered with relay power: a free endpoint
        /// placed radially outward from the ghost node, with a transmit power small enough that
        /// the ghost reaches it at twice the separation while every home station's direct
        /// range to it is at most half its distance, must reach home through that node; with
        /// the node taken out of the network, it must not reach home at all. A node that is a
        /// control source must also be the endpoint's closest control source, one hop away.
        /// </summary>
        private static void RunRegisteredRelayRouteProbe(GhostCommNetManager manager, string scene)
        {
            CommNetwork net = RequireStockNetwork();
            if (manager == null || manager.RegisteredCount == 0)
                InGameAssert.Skip("no ghost CommNet node is registered in " + scene + "; needs a committed recording "
                    + "of a relay vessel inside its ghost window (the ghost-commnet-relay preset)");
            CelestialBody homeBody = FlightGlobals.GetHomeBody();
            if (homeBody == null)
                InGameAssert.Skip("home body not resolved");

            // Rebuild so every ghost node's pre-update hook ran at this frame's UT.
            net.Rebuild();
            var homes = new List<CommNode>();
            for (int i = 0; i < net.Count; i++)
            {
                CommNode n = net[i];
                if (n != null && n.isHome
                    && Math.Max(n.antennaRelay.power, n.antennaTransmit.power) > 0.0)
                    homes.Add(n);
            }
            if (homes.Count == 0)
                InGameAssert.Skip("no CommNet home station with power in the network");

            int probed = 0, noRelay = 0, controlAtGhost = 0;
            var probedKeys = new List<string>();
            foreach (string key in manager.RegisteredKeys())
            {
                InGameAssert.IsTrue(manager.TryGetRegisteredNode(key, out CommNode node),
                    "registered key lost: " + key);
                double relay = node.antennaRelay.power;
                if (relay <= 0.0)
                {
                    noRelay++;
                    continue;
                }

                Vector3d ghostPos = node.precisePosition;
                Vector3d up = (ghostPos - homeBody.position).normalized;
                double separation = 0.0, endpointPower = 0.0;
                Vector3d endpointPos = Vector3d.zero;
                bool separable = false;
                for (double x = 500000.0; x >= 1000.0; x *= 0.8)
                {
                    Vector3d candidate = ghostPos + up * x;
                    double power = 4.0 * x * x / relay;
                    bool ok = true;
                    for (int h = 0; h < homes.Count && ok; h++)
                    {
                        double homePower = Math.Max(homes[h].antennaRelay.power, homes[h].antennaTransmit.power);
                        double direct = Math.Sqrt(homePower * power);
                        ok = direct <= 0.5 * (candidate - homes[h].precisePosition).magnitude;
                    }
                    if (ok)
                    {
                        separation = x;
                        endpointPower = power;
                        endpointPos = candidate;
                        separable = true;
                        break;
                    }
                }
                if (!separable)
                    InGameAssert.Skip(string.Format(IC,
                        "ghost node {0} (relay {1:R}) cannot be separated from the {2} home station(s): "
                        + "every endpoint the ghost reaches is also in a home's direct range", key, relay, homes.Count));

                var endpoint = new GhostCommNetNode
                {
                    name = "ParsekTest:registered-endpoint",
                    displayName = "ParsekTest registered endpoint",
                    isHome = false,
                    isControlSource = false,
                    isControlSourceMultiHop = false,
                };
                endpoint.antennaRelay.Update(0.0, GhostCommNetManager.DefaultRangeCurve, false);
                endpoint.antennaTransmit.Update(endpointPower, GhostCommNetManager.DefaultRangeCurve, false);
                endpoint.precisePosition = endpointPos;

                bool ghostRemoved = false;
                try
                {
                    net.Add(endpoint);
                    net.Rebuild();
                    var path = new CommPath();
                    bool reached = net.FindHome(endpoint, path);
                    InGameAssert.IsTrue(reached, string.Format(IC,
                        "endpoint {0:F0} m above ghost node {1} did not reach home (relay={2:R} endpointPower={3:R})",
                        separation, key, relay, endpointPower));
                    bool viaGhost = false;
                    for (int i = 0; i < path.Count; i++)
                    {
                        if (path[i].a == node || path[i].b == node) viaGhost = true;
                    }
                    InGameAssert.IsTrue(viaGhost, string.Format(IC,
                        "the endpoint's path home ({0} hops) does not hop through ghost node {1}", path.Count, key));

                    if (node.isControlSource)
                    {
                        var controlPath = new CommPath();
                        InGameAssert.IsTrue(net.FindClosestControlSource(endpoint, controlPath),
                            "ghost node " + key + " is a control source but the endpoint finds none");
                        InGameAssert.IsTrue(controlPath.Count == 1
                                && (controlPath[0].a == node || controlPath[0].b == node),
                            string.Format(IC,
                                "ghost node {0} is a control source one hop from the endpoint, but the closest "
                                + "control source path has {1} hop(s)", key, controlPath.Count));
                        controlAtGhost++;
                    }

                    // Negative control: without the ghost node the endpoint is cut off. The node
                    // re-derives its powers in its own pre-update hook, so it is taken out of the
                    // network rather than zeroed.
                    ghostRemoved = net.Remove(node);
                    InGameAssert.IsTrue(ghostRemoved, "could not remove ghost node " + key + " for the negative control");
                    net.Rebuild();
                    InGameAssert.IsTrue(!net.FindHome(endpoint, new CommPath()), string.Format(IC,
                        "negative control: the endpoint still reached home with ghost node {0} out of the network", key));
                }
                finally
                {
                    net.Remove(endpoint);
                    if (ghostRemoved && !net.Contains(node))
                        net.Add(node);
                    net.Rebuild();
                }
                probed++;
                probedKeys.Add(key);
            }

            if (probed == 0)
                InGameAssert.Skip(string.Format(IC,
                    "none of the {0} registered ghost node(s) in {1} has relay power this frame",
                    noRelay, scene));
            probedKeys.Sort(StringComparer.Ordinal);
            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet registered relay route ({0}): probed={1} viaGhost={1} negativeControlCut={1} "
                + "controlSourceAtGhost={2} noRelay={3} homes={4} keys={5}",
                scene, probed, controlAtGhost, noRelay, homes.Count, string.Join(",", probedKeys.ToArray())));
        }

        /// <summary>
        /// The operator rulings on the preset's three copies of one relay craft: the crewed copy
        /// (a real Pilot, the RC-L01's minimumCrew = 1) is a multi-hop control source; the copy
        /// whose playback is switched off still registers and relays; the looped copy, whose
        /// real run is over, registers nothing.
        /// </summary>
        private static void CheckPresetRelayRulings(GhostCommNetManager manager, string scene)
        {
            CommNetwork net = RequireStockNetwork();
            Recording recA = null, recB = null, recC = null;
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; ers != null && i < ers.Count; i++)
            {
                Recording r = ers[i];
                if (r == null) continue;
                if (r.RecordingId == PresetRelayARecordingId) recA = r;
                else if (r.RecordingId == PresetRelayBRecordingId) recB = r;
                else if (r.RecordingId == PresetRelayCRecordingId) recC = r;
            }
            if (recA == null && recB == null && recC == null)
                InGameAssert.Skip("needs the ghost-commnet-relay preset (committed recordings " + PresetRelayARecordingId
                    + ", " + PresetRelayBRecordingId + ", " + PresetRelayCRecordingId + ")");
            InGameAssert.IsTrue(recA != null && recB != null && recC != null, string.Format(IC,
                "the ghost-commnet-relay preset is partial: A={0} B={1} C={2}", recA != null, recB != null, recC != null));
            InGameAssert.IsNotNull(manager, "no ghost CommNet manager in " + scene);

            double now = Planetarium.GetUniversalTime();
            double windowStart = Math.Max(
                GhostPlaybackEngine.ResolveGhostActivationStartUT(recA),
                GhostPlaybackEngine.ResolveGhostActivationStartUT(recB));
            if (now < windowStart)
                InGameAssert.Skip(string.Format(IC,
                    "the preset relays' window has not opened yet (ut={0:F1} < {1:F1}); the lane must let time pass first",
                    now, windowStart));

            net.Rebuild();

            // A: crewed, playback on, not looped.
            InGameAssert.IsTrue(recA.PlaybackEnabled && !recA.LoopPlayback, string.Format(IC,
                "relay A is not the plain copy: playbackEnabled={0} loop={1}", recA.PlaybackEnabled, recA.LoopPlayback));
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(PresetRelayARecordingId, out CommNode nodeA),
                "relay A holds no ghost node: " + ExclusionOf(manager, PresetRelayARecordingId));
            InGameAssert.IsTrue(nodeA.antennaRelay.power > 0.0, string.Format(IC,
                "relay A's node has no relay power ({0:R})", nodeA.antennaRelay.power));
            InGameAssert.IsTrue(nodeA.isControlSource,
                "relay A (RC-L01, minimumCrew 1, a Pilot aboard) is not a control source");
            InGameAssert.IsTrue(nodeA.isControlSourceMultiHop,
                "relay A's RC-L01 is multiHop but the node is not a multi-hop control source");

            // B: uncrewed, playback switched off: still a relay, not a control source.
            InGameAssert.IsTrue(!recB.PlaybackEnabled, "relay B is not the hidden copy (playbackEnabled=True)");
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(PresetRelayBRecordingId, out CommNode nodeB),
                "relay B (playback off) holds no ghost node: " + ExclusionOf(manager, PresetRelayBRecordingId));
            InGameAssert.IsTrue(nodeB.antennaRelay.power > 0.0, string.Format(IC,
                "relay B's node has no relay power ({0:R})", nodeB.antennaRelay.power));
            InGameAssert.IsTrue(!nodeB.isControlSource,
                "relay B carries no crew but its RC-L01 (minimumCrew 1) made it a control source");

            // C: looped, real run over: loop copies carry no signal.
            InGameAssert.IsTrue(recC.LoopPlayback,
                "relay C lost its loop flag at load (not loopable?), so it no longer tests the loop ruling");
            InGameAssert.IsTrue(!manager.TryGetRegisteredNode(PresetRelayCRecordingId, out _),
                "relay C (looped, real run over) holds a ghost node");
            InGameAssert.IsTrue(manager.TryGetExclusionReason(PresetRelayCRecordingId, out string reasonC),
                "relay C was not offered to the manager as a candidate");
            InGameAssert.AreEqual("historical-never-replayed", reasonC,
                "relay C's exclusion reason");

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet preset rulings ({0}): relayA=registered control={1} multiHop={2} relay={3:R} "
                + "relayB=registered playbackEnabled={4} control={5} relay={6:R} "
                + "relayC=unregistered loop={7} reason={8}",
                scene, nodeA.isControlSource, nodeA.isControlSourceMultiHop, nodeA.antennaRelay.power,
                recB.PlaybackEnabled, nodeB.isControlSource, nodeB.antennaRelay.power,
                recC.LoopPlayback, reasonC));
        }

        private static string ExclusionOf(GhostCommNetManager manager, string key)
        {
            return manager.TryGetExclusionReason(key, out string reason)
                ? "excluded (" + reason + ")"
                : "not a candidate on the last tick";
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
                // Held past EndUT: the node sits where the vessel about to spawn is NOW
                // (terminal orbit propagated / surface point), not at the stale EndUT point.
                InGameAssert.IsTrue(manager.TryDescribeHookSample(key, now, out GhostCommNetHookSample hook),
                    "cannot describe the hook sample of " + key);
                Vector3d expected = Vector3d.zero;
                bool resolved = hook.Lit && (hook.Held
                    ? manager.TryResolveHeldPosition(key, now, out expected)
                    : resolve(recordingId, hint, now, out expected));
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
