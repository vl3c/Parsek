using System;
using System.Collections.Generic;
using System.Globalization;

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
    /// </summary>
    internal class GhostCommNetRelay
    {
        private const string Tag = "GhostCommNet";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Pure: compute total antenna power for a list of AntennaSpecs.
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

            if (specs.Count == 1)
            {
                double power = specs[0].antennaPower;
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeCombinedAntennaPower: single antenna '{0}' power={1}",
                        specs[0].partName ?? "(null)", power));
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
                    "ComputeCombinedAntennaPower: all antennas have zero power — returning 0");
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
                        "ComputeCombinedAntennaPower: no combinable antennas — strongest={0} power={1}",
                        specs[strongestIdx].partName ?? "(null)", strongestPower));
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
                    "ComputeCombinedAntennaPower: {0} antenna(s), strongest='{1}' power={2}, combined={3}",
                    specs.Count, specs[strongestIdx].partName ?? "(null)", strongestPower, total));

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

        // NOTE: Actual CommNet node registration requires investigation of
        // CommNetNetwork and CommNetVessel APIs. Extension plan Section 8.9
        // flags this as potentially requiring Harmony patches.
        // For Phase 6f, implement the pure data layer.

        // TODO (6f-2 in-game): Register CommNet node at ghost position.
        // Investigate CommNetNetwork.Instance and CommNetBody APIs to add a
        // CommNet node without a full Vessel. The node must:
        //   - Be positioned at the ghost's current world position
        //   - Have the combined antenna power from the recording's AntennaSpecs
        //   - Update position each frame (loaded: from GO; unloaded: from orbital propagation)
        //   - Be removed when ghost is destroyed or chain tip spawns
        // If CommNet API requires a Vessel, this becomes a design escalation per Section 13.2.
        // internal static void RegisterCommNetNode(uint vesselPid, double combinedPower, Vector3d position) { }

        // TODO (6f-2 in-game): Update CommNet node position each frame.
        // For loaded ghosts: read position from ghost GO transform.
        // For unloaded ghosts: compute position via GhostExtender orbital propagation.
        // internal static void UpdateCommNetNodePosition(uint vesselPid, Vector3d position) { }

        // TODO (6f-2 in-game): Remove CommNet node when ghost is destroyed or spawns as real.
        // internal static void RemoveCommNetNode(uint vesselPid) { }
    }
}
