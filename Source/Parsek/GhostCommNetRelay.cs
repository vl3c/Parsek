using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommNet;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Manages CommNet node registration for ghost vessel antennas.
    /// Ghost vessels participate in CommNet as relay nodes so that relay constellations
    /// placed by committed recordings provide coverage during the ghost window.
    ///
    /// Design constraint (Section 13.2): CommNet nodes use the CommNet API directly,
    /// NOT a full ProtoVessel. The ghost stays a raw Unity GameObject (if loaded) or
    /// has no Unity object at all (if unloaded).
    ///
    /// Instance-based: each GhostCommNetRelay tracks its own set of registered nodes.
    /// </summary>
    internal class GhostCommNetRelay
    {
        private const string Tag = "GhostCommNet";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        private Dictionary<uint, CommNode> activeGhostNodes = new Dictionary<uint, CommNode>();
        private static bool? remoteTechDetected;

        /// <summary>
        /// Pure: compute combined antenna power for RELAY-type antennas only.
        /// Uses KSP's combinability formula: strongest + sum(other * (other/strongest)^exponent).
        /// Filters to antennaType == "RELAY" or "" (empty = legacy/unknown, treated as relay for backward compat).
        /// Returns 0 for null/empty or no relay antennas.
        /// </summary>
        internal static double ComputeCombinedRelayPower(List<AntennaSpec> specs)
        {
            if (specs == null || specs.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeCombinedRelayPower: null/empty specs — returning 0");
                return 0;
            }

            // Filter to relay-type antennas only (RELAY or empty/legacy)
            var relaySpecs = new List<AntennaSpec>();
            for (int i = 0; i < specs.Count; i++)
            {
                string type = specs[i].antennaType ?? "";
                if (type == "RELAY" || type == "")
                {
                    relaySpecs.Add(specs[i]);
                }
            }

            if (relaySpecs.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeCombinedRelayPower: no RELAY-type antennas — returning 0");
                return 0;
            }

            double result = ComputeCombinedAntennaPowerFromList(relaySpecs, "ComputeCombinedRelayPower");
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeCombinedRelayPower: {0} relay antenna(s) from {1} total, combined relay power={2}",
                    relaySpecs.Count, specs.Count, result));
            return result;
        }

        /// <summary>
        /// Pure: compute combined antenna power for all antenna types (transmit power).
        /// Uses KSP's simplified combinability formula:
        ///   strongestPower + sum_others(power * (power / strongestPower)^exponent)
        /// where exponent is from the strongest antenna's antennaCombinableExponent.
        ///
        /// If no antennas are combinable, returns the strongest antenna's power.
        /// Returns 0 for null or empty list.
        /// </summary>
        internal static double ComputeCombinedAntennaPower(List<AntennaSpec> specs)
        {
            if (specs == null || specs.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeCombinedAntennaPower: null/empty specs — returning 0");
                return 0;
            }

            return ComputeCombinedAntennaPowerFromList(specs, "ComputeCombinedAntennaPower");
        }

        /// <summary>
        /// Shared implementation for antenna power combination from a pre-filtered list.
        /// </summary>
        private static double ComputeCombinedAntennaPowerFromList(List<AntennaSpec> specs, string caller)
        {
            if (specs.Count == 1)
            {
                double power = specs[0].antennaPower;
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "{0}: single antenna '{1}' power={2}",
                        caller, specs[0].partName ?? "(null)", power));
                return power;
            }

            // Find strongest antenna
            int strongestIdx = 0;
            double strongestPower = specs[0].antennaPower;
            for (int i = 1; i < specs.Count; i++)
            {
                if (specs[i].antennaPower > strongestPower)
                {
                    strongestPower = specs[i].antennaPower;
                    strongestIdx = i;
                }
            }

            if (strongestPower <= 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "{0}: all antennas have zero power — returning 0", caller));
                return 0;
            }

            // Use strongest antenna's combinability exponent
            double exponent = specs[strongestIdx].antennaCombinableExponent;
            bool anyCombinable = false;

            // Check if any antenna is combinable
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].antennaCombinable)
                {
                    anyCombinable = true;
                    break;
                }
            }

            if (!anyCombinable)
            {
                // No combinability — return strongest power only
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "{0}: no combinable antennas — strongest='{1}' power={2}",
                        caller, specs[strongestIdx].partName ?? "(null)", strongestPower));
                return strongestPower;
            }

            // KSP combinability formula:
            // total = strongestPower + sum(otherPower * (otherPower / strongestPower)^exponent)
            // Only combinable antennas participate in the sum.
            double total = strongestPower;

            for (int i = 0; i < specs.Count; i++)
            {
                if (i == strongestIdx) continue;
                if (!specs[i].antennaCombinable) continue;
                if (specs[i].antennaPower <= 0) continue;

                double ratio = specs[i].antennaPower / strongestPower;
                double contribution = specs[i].antennaPower * Math.Pow(ratio, exponent);
                total += contribution;
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "{0}: {1} antenna(s), strongest='{2}' power={3}, combined={4}",
                    caller, specs.Count, specs[strongestIdx].partName ?? "(null)", strongestPower, total));

            return total;
        }

        /// <summary>
        /// Pure: should this ghost have CommNet presence?
        /// True if it has at least one antenna spec with non-zero power.
        /// Returns false for null or empty list.
        /// </summary>
        internal static bool ShouldRegisterCommNet(List<AntennaSpec> specs)
        {
            if (specs == null || specs.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "ShouldRegisterCommNet: null/empty specs — returning false");
                return false;
            }

            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].antennaPower > 0)
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "ShouldRegisterCommNet: found antenna '{0}' with power={1} — returning true",
                            specs[i].partName ?? "(null)", specs[i].antennaPower));
                    return true;
                }
            }

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ShouldRegisterCommNet: {0} antenna(s) but all have zero power — returning false",
                    specs.Count));
            return false;
        }

        /// <summary>
        /// KSP runtime: register a ghost CommNet node.
        /// Guards: CommNet enabled, Instance non-null, RemoteTech not present.
        /// Returns true if registration succeeded.
        /// </summary>
        internal bool RegisterNode(uint vesselPid, string vesselName, double relayPower, double transmitPower, Vector3d position)
        {
            if (!HighLogic.CurrentGame.Parameters.Difficulty.EnableCommNet)
            {
                ParsekLog.Verbose(Tag, "CommNet disabled — skipping ghost node registration");
                return false;
            }
            if (IsRemoteTechPresent())
            {
                ParsekLog.Verbose(Tag, "RemoteTech detected — skipping ghost CommNet (not compatible)");
                return false;
            }
            if (CommNetNetwork.Instance?.CommNet == null)
            {
                ParsekLog.Verbose(Tag, "CommNet instance not available — skipping");
                return false;
            }

            var node = new CommNode();
            node.name = "Parsek Ghost: " + vesselName;
            node.isHome = false;
            node.isControlSource = false;
            node.isControlSourceMultiHop = false;
            node.antennaRelay.power = relayPower;
            node.antennaTransmit.power = transmitPower;
            node.precisePosition = position;

            CommNetNetwork.Instance.CommNet.Add(node);
            activeGhostNodes[vesselPid] = node;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Registered ghost CommNet node: pid={0} name={1} relayPower={2:F0} transmitPower={3:F0}",
                    vesselPid, vesselName, relayPower, transmitPower));
            return true;
        }

        /// <summary>
        /// KSP runtime: update node position each frame.
        /// For loaded ghosts: read position from ghost GO transform.
        /// For unloaded ghosts: compute position via orbital propagation.
        /// </summary>
        internal void UpdateNodePosition(uint vesselPid, Vector3d position)
        {
            CommNode node;
            if (!activeGhostNodes.TryGetValue(vesselPid, out node))
            {
                return;
            }

            node.precisePosition = position;
            ParsekLog.VerboseRateLimited(Tag, "node-pos-" + vesselPid,
                string.Format(ic,
                    "UpdateNodePosition: pid={0} pos=({1:F0},{2:F0},{3:F0})",
                    vesselPid, position.x, position.y, position.z));
        }

        /// <summary>
        /// KSP runtime: remove a ghost CommNet node.
        /// Called when ghost is destroyed or chain tip spawns as real vessel.
        /// </summary>
        internal void RemoveNode(uint vesselPid)
        {
            CommNode node;
            if (!activeGhostNodes.TryGetValue(vesselPid, out node))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "RemoveNode: pid={0} not found in active nodes — skipping", vesselPid));
                return;
            }

            CommNetNetwork.Instance?.CommNet?.Remove(node);
            activeGhostNodes.Remove(vesselPid);

            ParsekLog.Info(Tag,
                string.Format(ic, "Removed ghost CommNet node: pid={0}", vesselPid));
        }

        /// <summary>
        /// KSP runtime: remove all ghost CommNet nodes.
        /// Called during cleanup (scene change, etc).
        /// </summary>
        internal void RemoveAllNodes()
        {
            if (activeGhostNodes.Count == 0)
            {
                ParsekLog.Verbose(Tag, "RemoveAllNodes: no active ghost nodes");
                return;
            }

            int count = activeGhostNodes.Count;
            var commNet = CommNetNetwork.Instance?.CommNet;

            if (commNet != null)
            {
                foreach (var kvp in activeGhostNodes)
                {
                    commNet.Remove(kvp.Value);
                }
            }

            activeGhostNodes.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic, "Removed all {0} ghost CommNet node(s)", count));
        }

        /// <summary>
        /// KSP runtime: re-register all nodes after CommNet reinitialization.
        /// Called from OnNetworkInitialized event handler.
        /// </summary>
        internal void ReregisterAllNodes()
        {
            if (activeGhostNodes.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ReregisterAllNodes: no active ghost nodes to re-register");
                return;
            }

            var commNet = CommNetNetwork.Instance?.CommNet;
            if (commNet == null)
            {
                ParsekLog.Warn(Tag, "ReregisterAllNodes: CommNet instance not available — cannot re-register");
                return;
            }

            int count = 0;
            foreach (var kvp in activeGhostNodes)
            {
                commNet.Add(kvp.Value);
                count++;
            }

            ParsekLog.Info(Tag,
                string.Format(ic, "Re-registered {0} ghost CommNet node(s) after network init", count));
        }

        /// <summary>
        /// Pure: detect RemoteTech by scanning loaded assemblies (cached after first check).
        /// RemoteTech replaces the CommNet system entirely, so ghost CommNet nodes
        /// would be incompatible.
        /// </summary>
        internal static bool IsRemoteTechPresent()
        {
            if (remoteTechDetected.HasValue)
                return remoteTechDetected.Value;

            bool found = false;
            if (AssemblyLoader.loadedAssemblies != null)
            {
                found = AssemblyLoader.loadedAssemblies.Any(
                    a => a.name.Contains("RemoteTech"));
            }

            remoteTechDetected = found;

            ParsekLog.Info(Tag,
                string.Format(ic, "RemoteTech detection: {0}", remoteTechDetected.Value));

            return remoteTechDetected.Value;
        }

        /// <summary>
        /// Test support: get the number of tracked ghost nodes.
        /// </summary>
        internal int ActiveNodeCount => activeGhostNodes.Count;

        /// <summary>
        /// Test support: check if a specific vessel PID has a registered node.
        /// </summary>
        internal bool HasNode(uint vesselPid) => activeGhostNodes.ContainsKey(vesselPid);

        /// <summary>
        /// Test support: reset the RemoteTech detection cache.
        /// </summary>
        internal static void ResetRemoteTechCacheForTesting()
        {
            remoteTechDetected = null;
        }
    }
}
