using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static methods for computing spawn warning text and ghost label text.
    /// All methods are internal static for testability. No Unity dependencies beyond
    /// what callers provide. OnGUI rendering is handled by ParsekFlight.
    /// </summary>
    internal static class SpawnWarningUI
    {
        private const string Tag = "SpawnWarning";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ────────────────────────────────────────────────────────────
        //  Spawn warning decision + formatting (Task 6d-1)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Pure decision: should a proximity warning be displayed?
        /// Returns true when distance is at or within the warning radius.
        /// </summary>
        internal static bool ShouldShowWarning(float distance, float warningRadius)
        {
            bool show = distance <= warningRadius;
            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "ShouldShowWarning: distance={0}m radius={1}m -> {2}",
                    distance.ToString("F1", IC),
                    warningRadius.ToString("F0", IC),
                    show));
            return show;
        }

        /// <summary>
        /// Pure: compute warning text for a vessel near a pending spawn point.
        /// When not blocked: "Vessel '{name}' spawning -- {distance}m from spawn point"
        /// When blocked: "Spawn BLOCKED -- {name} overlaps, move vessel to clear"
        /// </summary>
        internal static string FormatWarningText(string vesselName, float distance, double spawnUT, bool spawnBlocked)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName;
            string text;

            if (spawnBlocked)
            {
                text = string.Format(IC,
                    "Spawn BLOCKED -- {0} overlaps, move vessel to clear",
                    name);
            }
            else
            {
                text = string.Format(IC,
                    "Vessel '{0}' spawning -- {1}m from spawn point",
                    name,
                    distance.ToString("F0", IC));
            }

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "FormatWarningText: vessel={0} distance={1}m spawnUT={2} blocked={3} -> \"{4}\"",
                    name, distance.ToString("F1", IC),
                    spawnUT.ToString("F0", IC), spawnBlocked, text));

            return text;
        }

        /// <summary>
        /// Pure: compute chain status text for display in the recording list UI.
        /// Active chain: "Ghosted -- spawns at UT={SpawnUT:F0}"
        /// Terminated chain: "Ghosted -- chain terminated"
        /// Blocked chain: "Spawn blocked -- waiting for clearance"
        /// Walkback exhausted: "Spawn blocked -- walkback exhausted, manual placement required"
        /// </summary>
        internal static string FormatChainStatus(GhostChain chain, string vesselName)
        {
            if (chain == null)
            {
                ParsekLog.Verbose(Tag, "FormatChainStatus: null chain");
                return null;
            }

            string name = string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName;
            string status;

            if (chain.SpawnBlocked && chain.WalkbackExhausted)
            {
                status = "Spawn blocked -- walkback exhausted, manual placement required";
            }
            else if (chain.SpawnBlocked)
            {
                status = "Spawn blocked -- waiting for clearance";
            }
            else if (chain.IsTerminated)
            {
                status = "Ghosted -- chain terminated";
            }
            else
            {
                status = string.Format(IC,
                    "Ghosted -- spawns at UT={0}",
                    chain.SpawnUT.ToString("F0", IC));
            }

            ParsekLog.VerboseRateLimited(Tag, "chain-status",
                string.Format(IC,
                    "FormatChainStatus: vessel={0} terminated={1} blocked={2} walkbackExhausted={3} -> \"{4}\"",
                    name, chain.IsTerminated, chain.SpawnBlocked, chain.WalkbackExhausted, status));

            return status;
        }

        // ────────────────────────────────────────────────────────────
        //  Ghost label text computation (Task 6d-2)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Pure: compute the floating label text for a ghost vessel.
        /// Normal chain:         "{vesselName}\nGhost -- spawns at UT={spawnUT:F0}"
        /// Terminated:           "{vesselName}\nGhost -- chain terminated"
        /// Blocked:              "{vesselName}\nGhost -- spawn blocked"
        /// Walkback exhausted:   "{vesselName}\nGhost -- spawn abandoned"
        /// </summary>
        internal static string ComputeGhostLabelText(string vesselName, double spawnUT,
            bool isTerminated, bool isBlocked, bool isWalkbackExhausted = false)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName;
            string line2;

            if (isBlocked && isWalkbackExhausted)
            {
                line2 = "Ghost -- spawn abandoned";
            }
            else if (isBlocked)
            {
                line2 = "Ghost -- spawn blocked";
            }
            else if (isTerminated)
            {
                line2 = "Ghost -- chain terminated";
            }
            else
            {
                line2 = string.Format(IC,
                    "Ghost -- spawns at UT={0}",
                    spawnUT.ToString("F0", IC));
            }

            string label = name + "\n" + line2;

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "ComputeGhostLabelText: vessel={0} spawnUT={1} terminated={2} blocked={3} walkbackExhausted={4} -> \"{5}\"",
                    name, spawnUT.ToString("F0", IC), isTerminated, isBlocked, isWalkbackExhausted,
                    label.Replace("\n", "\\n")));

            return label;
        }
    }
}
