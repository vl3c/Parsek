using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Manages tracking station entries and map view markers for ghost vessels.
    /// Ghosts represent vessels that exist in the world pending chain tip resolution.
    /// They must be visible in map view and tracking station for mission planning.
    ///
    /// Design constraint (Section 13.2): ProtoVessel creation is acceptable ONLY for
    /// unloaded chain-tip spawns and tracking station entries (if API requires it).
    /// For loaded ghosts, use Unity GO only. For CommNet, use CommNet API directly.
    /// </summary>
    internal static class GhostMapPresence
    {
        private const string Tag = "GhostMap";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Pure: does this recording have orbital data suitable for computing an orbit line?
        /// True if terminal orbit body is set and SMA > 0 (Keplerian elements present).
        /// </summary>
        internal static bool HasOrbitData(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose(Tag, "HasOrbitData: null recording — returning false");
                return false;
            }

            bool hasOrbit = !string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalOrbitSemiMajorAxis > 0;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "HasOrbitData: rec={0} body={1} sma={2} result={3}",
                    rec.RecordingId ?? "(null)",
                    rec.TerminalOrbitBody ?? "(null)",
                    rec.TerminalOrbitSemiMajorAxis,
                    hasOrbit));

            return hasOrbit;
        }

        /// <summary>
        /// Pure: compute display info for tracking station / map view.
        /// Returns vessel name, status string, and spawn UT for the chain.
        ///
        /// Status values:
        ///   "Ghost — spawns at UT=X" for active chains
        ///   "Ghost — terminated" for terminated chains (vessel destroyed/recovered)
        ///   "Ghost — spawn blocked" for chains with blocked spawn
        /// </summary>
        internal static (string name, string status, double spawnUT)
            ComputeGhostDisplayInfo(GhostChain chain, string vesselName)
        {
            string safeName = vesselName ?? "(unnamed)";

            if (chain == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: null chain for vessel '{0}' — returning defaults",
                        safeName));
                return (safeName, "Ghost — no chain data", 0);
            }

            if (chain.IsTerminated)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: terminated chain for vessel '{0}' pid={1}",
                        safeName, chain.OriginalVesselPid));
                return (safeName, "Ghost — terminated", chain.SpawnUT);
            }

            if (chain.SpawnBlocked)
            {
                string blockedStatus = string.Format(ic,
                    "Ghost — spawn blocked (since UT={0:F1})",
                    chain.BlockedSinceUT);
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "ComputeGhostDisplayInfo: spawn blocked for vessel '{0}' pid={1} since UT={2:F1}",
                        safeName, chain.OriginalVesselPid, chain.BlockedSinceUT));
                return (safeName, blockedStatus, chain.SpawnUT);
            }

            string activeStatus = string.Format(ic,
                "Ghost — spawns at UT={0:F1}",
                chain.SpawnUT);

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeGhostDisplayInfo: active chain for vessel '{0}' pid={1} spawnUT={2:F1} tip={3}",
                    safeName, chain.OriginalVesselPid, chain.SpawnUT,
                    chain.TipRecordingId ?? "(null)"));

            return (safeName, activeStatus, chain.SpawnUT);
        }

        // NOTE: Actual KSP tracking station and map view integration requires
        // investigation of MapNode, TrackingStationWidget, and PlanetariumCamera APIs.
        // These are documented as needing research (extension plan Section 13.2).
        // For Phase 6f, implement the pure data layer and mark KSP integration points
        // with TODO comments. The actual MapNode rendering is deferred to in-game testing.

        // TODO (6f-1 in-game): Register ghost in tracking station.
        // Investigate whether KSP's tracking station requires a ProtoVessel entry
        // or if a custom MapNode can be created directly. If ProtoVessel is required,
        // create a minimal one (orbit data + vessel name only, marked non-interactable)
        // per Section 13.2 boundary rules.
        // internal static void RegisterTrackingEntry(GhostChain chain, Recording tipRecording) { }

        // TODO (6f-1 in-game): Create map view orbit line for ghost.
        // Use distinct color (e.g. semi-transparent cyan) or dashed line style to
        // differentiate from real vessel orbits. Needs PlanetariumCamera API research.
        // internal static void CreateMapOrbitLine(Recording tipRecording) { }

        // TODO (6f-1 in-game): Enable setting ghost as navigation target.
        // Player should be able to set ghost as rendezvous target for transfer planning
        // (distance, closest approach, relative velocity). Investigate Vessel.SetTarget API
        // compatibility with non-Vessel objects.
        // internal static void SetAsNavigationTarget(GhostChain chain) { }

        // TODO (6f-1 in-game): Remove tracking entry and map markers when ghost is
        // destroyed or chain tip spawns.
        // internal static void RemoveTrackingEntry(uint vesselPid) { }
    }
}
