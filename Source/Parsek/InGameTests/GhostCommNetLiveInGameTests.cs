using System;
using System.Collections.Generic;
using System.Globalization;
using CommNet;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The ghost CommNet relay (design section 15.6) seen from a REAL live vessel's own CommNet
    /// node, not a test-made endpoint: the active vessel of the CN-2 lane is duna-park-probe's
    /// uncrewed DD1 probe, warped to where Duna hides every home station from it, with the
    /// <c>ghost-commnet-live</c> preset's three recordings riding its own orbit:
    /// <list type="bullet">
    ///   <item><description>a relay ghost at the limb carries the probe home (scenario 1: its
    ///       <c>ControlPath</c> hops through the ghost node and <c>IsConnectedHome</c> is true),
    ///       and with that node taken out the probe is cut off;</description></item>
    ///   <item><description>with no path home, a crewed RC-L01 ghost with no relay antenna is
    ///       the probe's control source (scenario 3), and without it there is none;</description></item>
    ///   <item><description>a relay ghost that links the probe but sits behind Duna itself does
    ///       not carry it home although home is in its range (scenario 8).</description></item>
    /// </list>
    /// Outside that preset every cell skips, naming the missing context.
    /// </summary>
    public class GhostCommNetLiveInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The ids are the contract between the injector (SyntheticRecordingTests
        // .GhostCommNetLivePreset) and these cells.
        internal const string LimbRelayRecordingId = "cn2-limb-relay";
        internal const string ShadowRelayRecordingId = "cn2-shadow-relay";
        internal const string ControlPointRecordingId = "cn2-control-point";

        private sealed class LiveContext
        {
            public CommNetwork Net;
            public Vessel Probe;
            public CommNode ProbeComm;
            public GhostCommNetManager Manager;
            public CommNode Limb;
            public CommNode Shadow;
            public CommNode Control;
            public List<CommNode> Homes;
            public CommNode StrongestHome;
        }

        [InGameTest(Category = "GhostCommNetLive", Scene = GameScenes.FLIGHT,
            Description = "A live probe cut off from home reaches it only through a ghost relay (ControlPath + IsConnectedHome)")]
        public void LiveProbeReachesHomeOnlyThroughGhostRelay_Flight()
        {
            LiveContext c = RequireLiveContext();
            string occluder = RequireOccludedFromHome(c, c.ProbeComm, "the probe");

            var path = new CommPath();
            InGameAssert.IsTrue(c.Net.FindHome(c.ProbeComm, path),
                "the probe does not reach home although the limb relay ghost is registered");
            InGameAssert.IsTrue(PathTouches(path, c.Limb), string.Format(IC,
                "the probe's path home ({0} hops) does not hop through the limb relay ghost", path.Count));
            InGameAssert.IsTrue(path.Count > 0 && (path[0].a is GhostCommNetNode || path[0].b is GhostCommNetNode),
                "the probe's first hop home is not a ghost node");

            CommNetVessel conn = c.Probe.connection;
            InGameAssert.IsTrue(conn.IsConnectedHome,
                "FindHome succeeds through the ghost relay but the probe's own IsConnectedHome is false");
            CommPath control = conn.ControlPath;
            InGameAssert.IsTrue(control != null && control.Count > 0 && PathTouches(control, c.Limb),
                "the probe's ControlPath does not go through the limb relay ghost");
            int hops = control.Count;

            bool removed = false;
            try
            {
                removed = c.Net.Remove(c.Limb);
                InGameAssert.IsTrue(removed, "could not take the limb relay ghost out for the negative control");
                c.Net.Rebuild();
                InGameAssert.IsTrue(!c.Net.FindHome(c.ProbeComm, new CommPath()),
                    "negative control: the probe still reaches home with the limb relay ghost out of the network");
                InGameAssert.IsTrue(!conn.IsConnectedHome,
                    "negative control: the probe's IsConnectedHome stayed true with the limb relay ghost out");
            }
            finally
            {
                Restore(c.Net, removed ? c.Limb : null);
            }
            InGameAssert.IsTrue(conn.IsConnectedHome,
                "the probe did not reconnect home once the limb relay ghost was back");

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet live probe relay (FLIGHT): probe=\"{0}\" homeLinked=False homeInRange=True "
                + "occludedBy={1} connectedHome=True hops={2} viaGhost={3} negativeControl=cut restored=True",
                c.Probe.vesselName, occluder, hops, LimbRelayRecordingId));
        }

        [InGameTest(Category = "GhostCommNetLive", Scene = GameScenes.FLIGHT,
            Description = "A live probe with no path home takes control from a crewed ghost control point")]
        public void LiveProbeControlledThroughGhostControlPoint_Flight()
        {
            LiveContext c = RequireLiveContext();
            RequireOccludedFromHome(c, c.ProbeComm, "the probe");
            InGameAssert.IsTrue(c.Control.isControlSource && c.Control.isControlSourceMultiHop,
                "the control point ghost (RC-L01 with a Pilot aboard) is not a multi-hop control source");
            InGameAssert.IsTrue(c.Control.antennaRelay.power == 0.0, string.Format(IC,
                "the control point ghost has relay power {0:R}; it must carry no relay antenna",
                c.Control.antennaRelay.power));

            CommNetVessel conn = c.Probe.connection;
            bool limbOut = false, controlOut = false;
            int hops;
            try
            {
                // No path home: the only relay that could carry the probe is taken out.
                limbOut = c.Net.Remove(c.Limb);
                InGameAssert.IsTrue(limbOut, "could not take the limb relay ghost out");
                c.Net.Rebuild();
                InGameAssert.IsTrue(!c.Net.FindHome(c.ProbeComm, new CommPath()),
                    "the probe still reaches home without the limb relay ghost, so the control test is not isolated");

                var cp = new CommPath();
                InGameAssert.IsTrue(c.Net.FindClosestControlSource(c.ProbeComm, cp),
                    "the probe finds no control source although the control point ghost is registered");
                InGameAssert.IsTrue(cp.Count == 1 && PathTouches(cp, c.Control), string.Format(IC,
                    "the probe's closest control source is not the control point ghost one hop away ({0} hops)", cp.Count));
                InGameAssert.IsTrue(conn.IsConnected && !conn.IsConnectedHome, string.Format(IC,
                    "the probe's connection reads connected={0} connectedHome={1}; expected connected through "
                    + "the ghost control point only", conn.IsConnected, conn.IsConnectedHome));
                CommPath control = conn.ControlPath;
                InGameAssert.IsTrue(control != null && control.Count > 0 && PathTouches(control, c.Control),
                    "the probe's ControlPath does not end at the control point ghost");
                hops = control.Count;

                // Negative control: without the control point there is no control source at all.
                controlOut = c.Net.Remove(c.Control);
                InGameAssert.IsTrue(controlOut, "could not take the control point ghost out for the negative control");
                c.Net.Rebuild();
                InGameAssert.IsTrue(!c.Net.FindClosestControlSource(c.ProbeComm, new CommPath()),
                    "negative control: the probe still finds a control source with the control point ghost out");
                InGameAssert.IsTrue(!conn.IsConnected,
                    "negative control: the probe's connection stayed connected with no relay and no control point");
            }
            finally
            {
                if (controlOut && !c.Net.Contains(c.Control))
                    c.Net.Add(c.Control);
                Restore(c.Net, limbOut ? c.Limb : null);
            }

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet live probe control point (FLIGHT): probe=\"{0}\" connectedHome=False connected=True "
                + "controlSource={1} hops={2} multiHop=True negativeControl=no-control",
                c.Probe.vesselName, ControlPointRecordingId, hops));
        }

        [InGameTest(Category = "GhostCommNetLive", Scene = GameScenes.FLIGHT,
            Description = "A ghost relay behind a body links a live probe but does not carry it home")]
        public void OccludedGhostRelayDoesNotCarryLiveProbe_Flight()
        {
            LiveContext c = RequireLiveContext();
            RequireOccludedFromHome(c, c.ProbeComm, "the probe");
            InGameAssert.IsTrue(c.ProbeComm.ContainsKey(c.Shadow),
                "the shadow relay ghost does not link the probe, so it cannot show occlusion");
            string occluder = RequireOccludedFromHome(c, c.Shadow, "the shadow relay ghost");
            bool limbHomeLinked = LinkedHomes(c.Limb, c.Homes) > 0;
            InGameAssert.IsTrue(limbHomeLinked, "the limb relay ghost links no home station, so the control is not valid");

            bool removed = false;
            try
            {
                removed = c.Net.Remove(c.Limb);
                InGameAssert.IsTrue(removed, "could not take the limb relay ghost out");
                c.Net.Rebuild();
                InGameAssert.IsTrue(c.ProbeComm.ContainsKey(c.Shadow),
                    "the shadow relay ghost stopped linking the probe with the limb relay out");
                InGameAssert.IsTrue(!c.Net.FindHome(c.ProbeComm, new CommPath()),
                    "the shadow relay ghost carried the probe home through " + occluder);
            }
            finally
            {
                Restore(c.Net, removed ? c.Limb : null);
            }

            ParsekLog.Info("TestRunner", string.Format(IC,
                "GhostCommNet occluded relay (FLIGHT): shadow={0} linkedToProbe=True homeInRange=True homeLinked=False "
                + "occludedBy={1} limb={2} limbHomeLinked=True probeHomeWithoutLimb=False",
                ShadowRelayRecordingId, occluder, LimbRelayRecordingId));
        }

        // ---------------------------------------------------------------------------------

        private static LiveContext RequireLiveContext()
        {
            bool anyPreset = false;
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; ers != null && i < ers.Count; i++)
            {
                string id = ers[i] != null ? ers[i].RecordingId : null;
                if (id == LimbRelayRecordingId || id == ShadowRelayRecordingId || id == ControlPointRecordingId)
                    anyPreset = true;
            }
            if (!anyPreset)
                InGameAssert.Skip("needs the ghost-commnet-live preset (committed recordings " + LimbRelayRecordingId
                    + ", " + ShadowRelayRecordingId + ", " + ControlPointRecordingId
                    + ") on duna-park-probe, warped to where Duna hides home from the DD1 probe");

            CommNetwork net = GhostCommNetInGameTests.RequireStockNetwork();
            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null in FLIGHT");
            GhostCommNetManager manager = flight.GhostCommNet;
            InGameAssert.IsNotNull(manager, "no ghost CommNet manager in FLIGHT");
            Vessel probe = FlightGlobals.ActiveVessel;
            InGameAssert.IsTrue(probe != null && probe.connection != null && probe.connection.Comm != null,
                "the active vessel has no CommNet connection");
            InGameAssert.IsTrue(probe.GetCrewCount() == 0, string.Format(IC,
                "the active vessel \"{0}\" carries {1} crew; the lane needs an uncrewed probe",
                probe.vesselName, probe.GetCrewCount()));

            var c = new LiveContext
            {
                Net = net,
                Probe = probe,
                ProbeComm = probe.connection.Comm,
                Manager = manager,
                Homes = new List<CommNode>(),
            };
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(LimbRelayRecordingId, out c.Limb),
                "the limb relay holds no ghost node: " + GhostCommNetInGameTests.ExclusionOf(manager, LimbRelayRecordingId));
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(ShadowRelayRecordingId, out c.Shadow),
                "the shadow relay holds no ghost node: " + GhostCommNetInGameTests.ExclusionOf(manager, ShadowRelayRecordingId));
            InGameAssert.IsTrue(manager.TryGetRegisteredNode(ControlPointRecordingId, out c.Control),
                "the control point holds no ghost node: " + GhostCommNetInGameTests.ExclusionOf(manager, ControlPointRecordingId));

            // Rebuild so every node (ghost pre-update hooks, the probe's own UpdateComm) is at
            // this frame's UT and the probe's CommNetVessel post-update has run.
            net.Rebuild();
            for (int i = 0; i < net.Count; i++)
            {
                CommNode n = net[i];
                if (n == null || !n.isHome) continue;
                if (Math.Max(n.antennaRelay.power, n.antennaTransmit.power) <= 0.0) continue;
                c.Homes.Add(n);
                if (c.StrongestHome == null || n.antennaRelay.power > c.StrongestHome.antennaRelay.power)
                    c.StrongestHome = n;
            }
            if (c.StrongestHome == null)
                InGameAssert.Skip("no CommNet home station with power in the network");
            InGameAssert.IsTrue(c.Limb.antennaRelay.power > 0.0 && c.Shadow.antennaRelay.power > 0.0,
                string.Format(IC, "a preset relay ghost is dark: limb relay={0:R} shadow relay={1:R}",
                    c.Limb.antennaRelay.power, c.Shadow.antennaRelay.power));
            return c;
        }

        /// <summary>
        /// The node links no home station while the strongest home is inside its stock antenna
        /// range, and a body stands between them: the only thing cutting it off is occlusion.
        /// Returns that body's name.
        /// </summary>
        private static string RequireOccludedFromHome(LiveContext c, CommNode node, string what)
        {
            int linked = LinkedHomes(node, c.Homes);
            InGameAssert.IsTrue(linked == 0, string.Format(IC,
                "{0} links {1} home station(s) directly at ut={2:F1}; the lane must be in the window "
                + "where Duna hides home from the probe (CN-2 WarpToUT)", what, linked, Planetarium.GetUniversalTime()));
            CommNode home = c.StrongestHome;
            double d2 = (node.precisePosition - home.precisePosition).sqrMagnitude;
            IRangeModel model = CommNetScenario.RangeModel;
            bool inRange = model.InRange(node.antennaRelay.power, home.antennaRelay.power, d2)
                || model.InRange(node.antennaRelay.power, home.antennaTransmit.power, d2)
                || model.InRange(node.antennaTransmit.power, home.antennaRelay.power, d2);
            InGameAssert.IsTrue(inRange, string.Format(IC,
                "{0} is out of antenna range of home \"{1}\" (relay={2:R} transmit={3:R} distance={4:F0} m), "
                + "so the missing link is range, not occlusion", what, home.displayName,
                node.antennaRelay.power, node.antennaTransmit.power, Math.Sqrt(d2)));
            string occluder = FindOccluder(node.precisePosition, home.precisePosition);
            InGameAssert.IsNotNull(occluder, string.Format(IC,
                "{0} is in range of home \"{1}\" and unlinked, but no body stands between them", what, home.displayName));
            return occluder;
        }

        private static int LinkedHomes(CommNode node, List<CommNode> homes)
        {
            int linked = 0;
            for (int i = 0; i < homes.Count; i++)
            {
                if (node.ContainsKey(homes[i])) linked++;
            }
            return linked;
        }

        private static bool PathTouches(CommPath path, CommNode node)
        {
            for (int i = 0; path != null && i < path.Count; i++)
            {
                if (path[i].a == node || path[i].b == node) return true;
            }
            return false;
        }

        private static void Restore(CommNetwork net, CommNode removed)
        {
            if (removed != null && !net.Contains(removed))
                net.Add(removed);
            net.Rebuild();
        }

        /// <summary>
        /// The first body whose stock CommNet occluder sphere (radius x
        /// scaledRadiusHorizonMultiplier x the atmosphere / vacuum occlusion multiplier) the
        /// segment a-b passes through, skipping a body either endpoint sits on (stock never
        /// lets a node's own occluder block it). Null when none.
        /// </summary>
        private static string FindOccluder(Vector3d a, Vector3d b)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            CommNetParams p = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Parameters.CustomParams<CommNetParams>()
                : null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody body = bodies[i];
                if (body == null) continue;
                double mult = p == null ? 1.0 : (body.atmosphere ? p.occlusionMultiplierAtm : p.occlusionMultiplierVac);
                double radius = body.Radius * body.scaledRadiusHorizonMultiplier * mult;
                Vector3d center = body.position;
                if ((a - center).magnitude < body.Radius * 1.05 || (b - center).magnitude < body.Radius * 1.05)
                    continue;
                if (SegmentPassesWithin(a, b, center, radius))
                    return body.bodyName;
            }
            return null;
        }

        /// <summary>Does the segment a-b pass within <paramref name="radius"/> of <paramref name="center"/>?</summary>
        internal static bool SegmentPassesWithin(Vector3d a, Vector3d b, Vector3d center, double radius)
        {
            Vector3d ab = b - a;
            double len2 = Vector3d.Dot(ab, ab);
            double s = len2 > 0.0 ? Vector3d.Dot(center - a, ab) / len2 : 0.0;
            if (s < 0.0) s = 0.0;
            if (s > 1.0) s = 1.0;
            return (a + ab * s - center).magnitude < radius;
        }
    }
}
