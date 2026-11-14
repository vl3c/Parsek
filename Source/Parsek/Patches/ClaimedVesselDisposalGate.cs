using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// GC-2: blocks the stock Recover / Delete of a real vessel that a committed future flight
    /// still holds back (the Ghost Chain Rule), outside flight.
    ///
    /// <para>A flight load despawns such a vessel (<c>ParsekFlight.FilterAndGhostChains</c>),
    /// but the Space Center and the Tracking Station keep it live in its pre-claim form until
    /// the chain tip spawns (CLAIMED-VESSEL-DESPAWN-AT-REWIND-OUTSIDE-FLIGHT). Recovering or
    /// deleting it there leaves the tip to spawn its recorded merged form anyway, so its parts
    /// exist twice and a recovery pays for them twice. The block reads exactly the flight
    /// predicate: the chain's tip spawn is still ahead, the chain is not terminated, and the
    /// pid is one the flight load would ghost (<c>ParsekFlight.SelectChainPidsToGhost</c>).</para>
    ///
    /// <para>The mark is stock's own: the Recover / Delete buttons greyed with the reason on
    /// their stock tooltip (<see cref="StockUiReasonTooltip"/>). Every greyed control ships
    /// with a click-block reading the same predicate (the stock-control annotation rule);
    /// the hooks are <c>Patches/ClaimedVesselDisposalPatches.cs</c> and the two Tracking
    /// Station confirm prefixes in <c>Patches/GhostTrackingStationPatch.cs</c>. Stock's
    /// launch-site clear (the editor's and the Space Center's "Recover and launch") is held
    /// the same way by <c>Patches/ClaimedVesselLaunchSitePatches.cs</c> (GC-9).</para>
    /// </summary>
    internal static class ClaimedVesselDisposalGate
    {
        internal const string Tag = "ClaimedVessel";

        /// <summary>Test seam: replaces the committed-tree chain walk.</summary>
        internal static Func<Dictionary<uint, GhostChain>> ChainsOverrideForTesting;

        /// <summary>Test seam: replaces the live UT read.</summary>
        internal static Func<double> UtOverrideForTesting;

        internal static void ResetForTesting()
        {
            ChainsOverrideForTesting = null;
            UtOverrideForTesting = null;
        }

        /// <summary>
        /// Pure: true when a chain in <paramref name="chains"/> holds back the vessel
        /// <paramref name="pid"/> at <paramref name="currentUT"/>, the same answer a flight load
        /// gives when it decides to ghost it. <paramref name="liveGuidOf"/> answers the live
        /// launch guid for a pid (null when unknown).
        /// </summary>
        internal static bool IsHeldByCommittedFuture(
            IReadOnlyDictionary<uint, GhostChain> chains, uint pid, double currentUT,
            Func<uint, string> liveGuidOf, out GhostChain claiming)
        {
            claiming = null;
            if (pid == 0u || chains == null) return false;
            foreach (KeyValuePair<uint, GhostChain> kvp in chains)
            {
                GhostChain chain = kvp.Value;
                if (chain == null || chain.IsTerminated) continue;
                if (!GhostChainWalker.ShouldGhostChainAtUT(chain, currentUT)) continue;
                List<uint> held = ParsekFlight.SelectChainPidsToGhost(kvp.Key, chain, liveGuidOf);
                if (!held.Contains(pid)) continue;
                claiming = chain;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Live: is the real vessel held back by a committed future flight right now? Never
        /// throws; a failure reads as "not held", the pre-existing behaviour.
        /// </summary>
        internal static bool IsHeldLive(Vessel v, string site, out GhostChain claiming)
        {
            claiming = null;
            if (v == null) return false;
            uint pid = v.persistentId;
            try
            {
                if (GhostMapPresence.IsGhostMapVessel(pid)) return false;
                Dictionary<uint, GhostChain> chains = ReadChains();
                if (chains == null || chains.Count == 0) return false;
                string liveGuid = v.id != Guid.Empty ? v.id.ToString("N") : null;
                bool held = IsHeldByCommittedFuture(chains, pid, ReadUT(),
                    p => p == pid ? liveGuid : LiveGuidOf(p), out claiming);
                return held;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "held-check-threw-" + pid.ToString(CultureInfo.InvariantCulture),
                    "Claimed-vessel check threw " + ex.GetType().Name + ": " + ex.Message
                    + " site=" + (site ?? "?") + " pid=" + pid.ToString(CultureInfo.InvariantCulture)
                    + " - treating the vessel as not held");
                claiming = null;
                return false;
            }
        }

        /// <summary>One vessel a committed future flight holds, as a launch-site clear found it.</summary>
        internal struct HeldVessel
        {
            internal uint Pid;
            internal string Name;
            internal GhostChain Claiming;
        }

        /// <summary>One vessel standing at a launch site (a stock <c>ProtoVessel</c>'s identity).</summary>
        internal struct SiteVessel
        {
            internal uint Pid;
            internal string Guid;
            internal string Name;
        }

        /// <summary>
        /// Pure: the vessels in <paramref name="candidates"/> a chain holds back at
        /// <paramref name="currentUT"/>, each by <see cref="IsHeldByCommittedFuture"/> with its
        /// own launch guid (<paramref name="otherGuidOf"/> answers any other pid; may be null).
        /// </summary>
        internal static List<HeldVessel> SelectHeld(
            IReadOnlyDictionary<uint, GhostChain> chains, double currentUT,
            IList<SiteVessel> candidates, Func<uint, string> otherGuidOf)
        {
            var held = new List<HeldVessel>();
            if (chains == null || candidates == null) return held;
            for (int i = 0; i < candidates.Count; i++)
            {
                SiteVessel c = candidates[i];
                bool isHeld = IsHeldByCommittedFuture(chains, c.Pid, currentUT,
                    p => p == c.Pid ? c.Guid : (otherGuidOf != null ? otherGuidOf(p) : null),
                    out GhostChain claiming);
                if (isHeld)
                    held.Add(new HeldVessel { Pid = c.Pid, Name = c.Name, Claiming = claiming });
            }
            return held;
        }

        /// <summary>
        /// Live: the stock <c>ProtoVessel</c>s (a launch-site clear's obstructions) a committed
        /// future flight holds right now; one chain walk for the whole list. Never throws; a
        /// failure reads as "none held", the pre-existing behaviour.
        /// </summary>
        internal static List<HeldVessel> FindHeldLive(IList<ProtoVessel> vessels, string site)
        {
            var none = new List<HeldVessel>();
            if (vessels == null || vessels.Count == 0) return none;
            try
            {
                var candidates = new List<SiteVessel>(vessels.Count);
                for (int i = 0; i < vessels.Count; i++)
                {
                    ProtoVessel pv = vessels[i];
                    if (pv == null || pv.persistentId == 0u) continue;
                    if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;
                    candidates.Add(new SiteVessel
                    {
                        Pid = pv.persistentId,
                        Guid = pv.vesselID != Guid.Empty ? pv.vesselID.ToString("N") : null,
                        Name = pv.vesselName
                    });
                }
                if (candidates.Count == 0) return none;
                Dictionary<uint, GhostChain> chains = ReadChains();
                if (chains == null || chains.Count == 0) return none;
                return SelectHeld(chains, ReadUT(), candidates, LiveGuidOf);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "held-site-check-threw-" + (site ?? "?"),
                    "Claimed-vessel launch-site check threw " + ex.GetType().Name + ": " + ex.Message
                    + " site=" + (site ?? "?") + " - treating the site's vessels as not held");
                return none;
            }
        }

        /// <summary>The hover sentence for a held vessel, in the reservation wording.</summary>
        internal static string HoldSentence(GhostChain claiming)
        {
            return ReservationExplanation.ClaimedVesselHold(
                claiming != null ? claiming.SpawnUT : double.NaN,
                ReservationExplanation.DefaultDateFormatter);
        }

        /// <summary>One Info line for a refused or greyed action.</summary>
        internal static void LogBlocked(string action, uint pid, string vesselName, GhostChain claiming)
        {
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(Tag,
                "Blocked " + (action ?? "?") + " of claimed vessel '" + (vesselName ?? "?")
                + "' pid=" + pid.ToString(ic)
                + " chainTip=" + (claiming?.TipRecordingId ?? "<none>")
                + " spawnUT=" + (claiming != null ? claiming.SpawnUT.ToString("R", ic) : "NaN")
                + " - a committed future flight holds this vessel until its chain tip spawns");
        }

        private static Dictionary<uint, GhostChain> ReadChains()
        {
            if (ChainsOverrideForTesting != null) return ChainsOverrideForTesting();
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0) return null;
            // The walker ignores its UT argument; the spawn-UT test is applied per call.
            return GhostChainWalker.ComputeAllGhostChains(trees, 0.0);
        }

        private static double ReadUT()
        {
            if (UtOverrideForTesting != null) return UtOverrideForTesting();
            return Planetarium.GetUniversalTime();
        }

        private static string LiveGuidOf(uint pid)
        {
            if (ChainsOverrideForTesting != null) return null;
            Vessel vessel = FlightRecorder.FindVesselByPid(pid);
            return vessel != null && vessel.id != Guid.Empty ? vessel.id.ToString("N") : null;
        }
    }
}
