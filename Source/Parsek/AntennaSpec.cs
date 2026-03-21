using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Stores antenna specification data extracted from a ModuleDataTransmitter module.
    /// Used for ghost CommNet relay registration without requiring a full KSP Vessel.
    /// </summary>
    internal struct AntennaSpec
    {
        public string partName;
        public double antennaPower;
        public bool antennaCombinable;
        public double antennaCombinableExponent;

        /// <summary>
        /// Antenna type from ModuleDataTransmitter: "RELAY", "DIRECT", "INTERNAL", or "" (unknown/legacy).
        /// </summary>
        public string antennaType;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "AntennaSpec part={0} power={1} combinable={2} exponent={3} type={4}",
                partName ?? "(null)", antennaPower, antennaCombinable,
                antennaCombinableExponent, antennaType ?? "");
        }
    }

    /// <summary>
    /// Pure static extraction of antenna specs from VESSEL ConfigNode snapshots.
    /// Reads MODULE subnodes looking for ModuleDataTransmitter entries and extracts
    /// antenna power, combinability, and exponent fields.
    /// </summary>
    internal static class AntennaSpecExtractor
    {
        private const string Tag = "AntennaSpec";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Extract antenna specs from a VESSEL ConfigNode snapshot.
        /// Iterates all PART nodes, finds MODULE nodes with name = ModuleDataTransmitter,
        /// and reads antennaPower, antennaCombinable, antennaCombinableExponent.
        /// Returns empty list if snapshot is null or contains no antennas.
        /// </summary>
        internal static List<AntennaSpec> ExtractFromSnapshot(ConfigNode vesselSnapshot)
        {
            var specs = new List<AntennaSpec>();

            if (vesselSnapshot == null)
            {
                ParsekLog.Verbose(Tag, "ExtractFromSnapshot: null snapshot — returning empty list");
                return specs;
            }

            ConfigNode[] partNodes = vesselSnapshot.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
            {
                ParsekLog.Verbose(Tag,
                    "ExtractFromSnapshot: no PART nodes in snapshot — returning empty list");
                return specs;
            }

            for (int p = 0; p < partNodes.Length; p++)
            {
                string partName = partNodes[p].GetValue("name") ?? "(unknown)";

                ConfigNode[] moduleNodes = partNodes[p].GetNodes("MODULE");
                if (moduleNodes == null) continue;

                for (int m = 0; m < moduleNodes.Length; m++)
                {
                    string moduleName = moduleNodes[m].GetValue("name");
                    if (moduleName != "ModuleDataTransmitter") continue;

                    var spec = new AntennaSpec();
                    spec.partName = partName;

                    // Parse antennaPower (double)
                    string powerStr = moduleNodes[m].GetValue("antennaPower");
                    if (powerStr != null)
                    {
                        double power;
                        if (double.TryParse(powerStr, NumberStyles.Float, ic, out power))
                            spec.antennaPower = power;
                    }

                    // Parse antennaCombinable (bool)
                    string combinableStr = moduleNodes[m].GetValue("antennaCombinable");
                    if (combinableStr != null)
                    {
                        bool combinable;
                        if (bool.TryParse(combinableStr, out combinable))
                            spec.antennaCombinable = combinable;
                    }

                    // Parse antennaCombinableExponent (double)
                    string exponentStr = moduleNodes[m].GetValue("antennaCombinableExponent");
                    if (exponentStr != null)
                    {
                        double exponent;
                        if (double.TryParse(exponentStr, NumberStyles.Float, ic, out exponent))
                            spec.antennaCombinableExponent = exponent;
                    }

                    // Parse antennaType (string: RELAY, DIRECT, INTERNAL, or empty)
                    spec.antennaType = moduleNodes[m].GetValue("antennaType") ?? "";

                    specs.Add(spec);

                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "ExtractFromSnapshot: found antenna on part '{0}': power={1} combinable={2} exponent={3} type={4}",
                            partName, spec.antennaPower, spec.antennaCombinable, spec.antennaCombinableExponent, spec.antennaType));
                }
            }

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "ExtractFromSnapshot: extracted {0} antenna spec(s) from {1} part(s)",
                    specs.Count, partNodes.Length));

            return specs;
        }
    }
}
