using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static text builders for the ghost chain-status and in-world label strings.
    /// All methods are internal static for testability, with no Unity dependencies beyond
    /// what callers provide.
    /// <para>The pre-spawn PROXIMITY WARNING pair that used to live here
    /// (<c>ShouldShowWarning</c> / <c>FormatWarningText</c>, with
    /// <c>SpawnCollisionDetector.CheckWarningProximity</c> behind them) was deleted
    /// 2026-09-11: it had no call site, and this header used to claim "OnGUI rendering is
    /// handled by ParsekFlight", which was never true. Nothing rendered it, so the
    /// "move vessel to clear" advice a player would most want never reached the game
    /// (GUI census D2). It is not wired up instead because there is nowhere to put it: the
    /// Real Spawn Control window warps, it does not confirm a spawn - the spawn itself is
    /// automatic - and no spawn-confirm dialog exists anywhere in the mod. Bringing the
    /// warning back is a feature with a surface decision, not a wiring job; the census's
    /// exposure gaps carry it.</para>
    /// </summary>
    internal static class SpawnWarningUI
    {
        private const string Tag = "SpawnWarning";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

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
