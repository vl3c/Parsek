using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static methods for the selective spawn UI: determining which chain tips
    /// are available for interaction, computing warp targets, and formatting UI text.
    ///
    /// The player sees ghost vessels and explicitly chooses which to materialize by
    /// warping past the chain tip UT. The Spawn Control window lists all pending chain
    /// tips with per-vessel warp buttons and a convenience "Warp to Next Spawn" button.
    /// </summary>
    internal static class SelectiveSpawnUI
    {
        private const string Tag = "SelectiveSpawn";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Pure: count pending (non-terminated) chain tips without allocating a list.
        /// Used by the main window toggle button label.
        /// </summary>
        internal static int GetPendingChainTipCount(Dictionary<uint, GhostChain> chains)
        {
            if (chains == null || chains.Count == 0) return 0;
            int count = 0;
            foreach (var kvp in chains)
            {
                if (!kvp.Value.IsTerminated)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Pure: find all pending (non-terminated, non-blocked) chain tips, sorted by SpawnUT.
        /// These are the chains the player can choose to warp to.
        /// </summary>
        internal static List<GhostChain> GetPendingChainTips(Dictionary<uint, GhostChain> chains)
        {
            var result = new List<GhostChain>();

            if (chains == null || chains.Count == 0)
            {
                ParsekLog.Verbose(Tag, "GetPendingChainTips: empty/null chains");
                return result;
            }

            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (!chain.IsTerminated)
                {
                    result.Add(chain);
                }
            }

            result.Sort((a, b) => a.SpawnUT.CompareTo(b.SpawnUT));

            if (ParsekLog.IsVerboseEnabled)
                ParsekLog.Verbose(Tag,
                    string.Format(IC, "GetPendingChainTips: {0} pending from {1} total",
                        result.Count, chains.Count));

            return result;
        }

        /// <summary>
        /// Pure: find the earliest pending chain tip in the future.
        /// Returns the chain itself (for direct PID access), or null if none pending.
        /// Avoids floating-point equality issues by returning the chain directly
        /// rather than just its SpawnUT.
        /// </summary>
        internal static GhostChain FindNextSpawnChain(Dictionary<uint, GhostChain> chains, double currentUT)
        {
            var pending = GetPendingChainTips(chains);

            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].SpawnUT > currentUT)
                {
                    if (ParsekLog.IsVerboseEnabled)
                        ParsekLog.Verbose(Tag,
                            string.Format(IC,
                                "FindNextSpawnChain: next spawn at UT={0} vessel={1}",
                                pending[i].SpawnUT.ToString("F1", IC), pending[i].OriginalVesselPid));
                    return pending[i];
                }
            }

            ParsekLog.Verbose(Tag, "FindNextSpawnChain: no future chain tips");
            return null;
        }

        /// <summary>
        /// Pure: compute the target UT for "Warp to Next Spawn".
        /// Returns the earliest pending chain tip's SpawnUT, or 0 if none pending.
        /// </summary>
        internal static double ComputeNextSpawnUT(Dictionary<uint, GhostChain> chains, double currentUT)
        {
            var next = FindNextSpawnChain(chains, currentUT);
            return next != null ? next.SpawnUT : 0;
        }

        /// <summary>
        /// Pure: determine whether a specific chain tip can be warped to.
        /// True when the chain's SpawnUT is in the future and the chain is not terminated.
        /// </summary>
        internal static bool CanWarpToChain(GhostChain chain, double currentUT)
        {
            if (chain == null)
            {
                ParsekLog.Verbose(Tag, "CanWarpToChain: null chain");
                return false;
            }

            bool can = !chain.IsTerminated && chain.SpawnUT > currentUT;

            if (ParsekLog.IsVerboseEnabled)
                ParsekLog.Verbose(Tag,
                    string.Format(IC,
                        "CanWarpToChain: vessel={0} spawnUT={1} terminated={2} currentUT={3} -> {4}",
                        chain.OriginalVesselPid,
                        chain.SpawnUT.ToString("F1", IC),
                        chain.IsTerminated,
                        currentUT.ToString("F1", IC),
                        can));

            return can;
        }

        /// <summary>
        /// Pure: find chains that will also be spawned if the player warps to the given chain.
        /// Returns chains with SpawnUT between currentUT and the selected chain's SpawnUT (exclusive
        /// of the selected chain itself). Sorted chronologically.
        /// Chronological constraint: earlier chain tips are always spawned first.
        /// </summary>
        internal static List<GhostChain> FindAlsoSpawnedChains(
            Dictionary<uint, GhostChain> chains, GhostChain selected, double currentUT)
        {
            var result = new List<GhostChain>();

            if (chains == null || selected == null)
            {
                ParsekLog.Verbose(Tag, "FindAlsoSpawnedChains: null input");
                return result;
            }

            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (chain == selected) continue;
                if (chain.IsTerminated) continue;

                if (chain.SpawnUT > currentUT && chain.SpawnUT <= selected.SpawnUT)
                {
                    result.Add(chain);
                }
            }

            result.Sort((a, b) => a.SpawnUT.CompareTo(b.SpawnUT));

            if (ParsekLog.IsVerboseEnabled)
                ParsekLog.Verbose(Tag,
                    string.Format(IC,
                        "FindAlsoSpawnedChains: selected vessel={0} also spawns {1} chain(s)",
                        selected.OriginalVesselPid, result.Count));

            return result;
        }

        /// <summary>
        /// Pure: format the button text for a chain tip's warp button.
        /// Shows time-to-spawn in human-readable format.
        /// </summary>
        internal static string FormatWarpButtonText(GhostChain chain, double currentUT)
        {
            if (chain == null) return "Warp";

            double delta = chain.SpawnUT - currentUT;

            if (delta <= 0)
                return "Spawn Now";

            return string.Format(IC, "Warp +{0}", FormatTimeDelta(delta));
        }

        /// <summary>
        /// Pure: format the "also spawns" warning text.
        /// Returns null if no other chains are affected.
        /// </summary>
        internal static string FormatAlsoSpawnsWarning(
            List<GhostChain> alsoSpawned,
            Dictionary<uint, string> vesselNames)
        {
            if (alsoSpawned == null || alsoSpawned.Count == 0)
                return null;

            if (alsoSpawned.Count == 1)
            {
                string name = GetVesselName(alsoSpawned[0].OriginalVesselPid, vesselNames);
                return string.Format(IC, "Also spawns: {0}", name);
            }

            // Multiple chains
            var names = new List<string>();
            for (int i = 0; i < alsoSpawned.Count; i++)
            {
                names.Add(GetVesselName(alsoSpawned[i].OriginalVesselPid, vesselNames));
            }
            return string.Format(IC, "Also spawns: {0}", string.Join(", ", names));
        }

        /// <summary>
        /// Pure: format the "Warp to Next Spawn" button tooltip from a pre-found chain.
        /// Accepts the chain directly to avoid redundant GetPendingChainTips scans.
        /// </summary>
        internal static string FormatNextSpawnTooltip(
            GhostChain nextChain, double currentUT,
            Dictionary<uint, string> vesselNames)
        {
            if (nextChain == null)
                return "No pending spawns";

            string name = GetVesselName(nextChain.OriginalVesselPid, vesselNames);
            double delta = nextChain.SpawnUT - currentUT;
            return string.Format(IC,
                "Warp to {0} (spawns in {1})",
                name, FormatTimeDelta(delta));
        }

        /// <summary>
        /// Pure: format a time delta as human-readable string.
        /// Under 60s: "{s}s". Under 3600s: "{m}m {s}s". Otherwise: "{h}h {m}m".
        /// </summary>
        internal static string FormatTimeDelta(double seconds)
        {
            if (seconds < 0) seconds = 0;

            if (seconds < 60)
                return string.Format(IC, "{0}s", ((int)seconds).ToString(IC));

            if (seconds < 3600)
            {
                int m = (int)(seconds / 60);
                int s = (int)(seconds % 60);
                return string.Format(IC, "{0}m {1}s", m.ToString(IC), s.ToString(IC));
            }

            int h = (int)(seconds / 3600);
            int min = (int)((seconds % 3600) / 60);
            return string.Format(IC, "{0}h {1}m", h.ToString(IC), min.ToString(IC));
        }

        /// <summary>
        /// Pure: build a reverse lookup from TipRecordingId → GhostChain for non-terminated chains.
        /// Used by the UI to find the chain for a given recording row in O(1) instead of
        /// iterating all chains per row.
        /// </summary>
        internal static Dictionary<string, GhostChain> BuildTipRecordingToChainMap(
            Dictionary<uint, GhostChain> chains)
        {
            var map = new Dictionary<string, GhostChain>();
            if (chains == null) return map;

            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (!chain.IsTerminated && chain.TipRecordingId != null)
                    map[chain.TipRecordingId] = chain;
            }
            return map;
        }

        private static string GetVesselName(uint pid, Dictionary<uint, string> vesselNames)
        {
            if (vesselNames != null && vesselNames.TryGetValue(pid, out string name))
                return name;
            return string.Format(IC, "Vessel {0}", pid);
        }
    }
}
