using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Holds a route endpoint that the Ghost Chain Rule has taken out of the world, instead
    /// of letting the resolver's surface-proximity step re-point the route at a neighbour
    /// (ROUTE-ENDPOINT-CHAIN-GHOST-PROXIMITY-REBIND).
    ///
    /// <para>A base claimed by a committed dock is despawned after a rewind and replaced by
    /// its ghost until the claiming recording's chain tip respawns it (with its identity
    /// preserved). While it is gone, the resolver's root-part and pid steps miss and the
    /// proximity step would find any craft parked within the radius and REBIND the route to it
    /// (<see cref="RouteEndpointTransfer.ApplyTransfers"/>, persisted); the respawned base then
    /// loses to the neighbour's root part at the first resolver step forever. So while the
    /// recorded endpoint is chain-ghosted the proximity step is not run: the endpoint reports
    /// <see cref="HoldReason"/> and the route waits, exactly as for any temporarily missing
    /// endpoint, until the base reappears and resolves by identity again.</para>
    ///
    /// <para>"Chain-ghosted" is read from the committed trees, so it holds in every scene the
    /// resolver runs in (the route tick and the Logistics window run outside flight too, where
    /// the despawned base is simply absent): a non-terminated chain claiming the endpoint's
    /// pid whose tip spawn UT is still ahead, or, in flight, a chain the flight still lists as
    /// waiting to spawn (a tip whose spawn a collision blocked - possibly by the very
    /// neighbour the proximity step would pick). A terminated chain (the base is destroyed or
    /// recovered in the committed future) or no chain at all is NOT a hold: a genuinely lost
    /// endpoint still transfers to a nearby craft as before.</para>
    /// </summary>
    internal static class RouteEndpointChainHold
    {
        private const string Tag = "Logistics";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The resolver reason token for a held endpoint. Hold presentation keys on
        /// it, so it is one constant.</summary>
        internal const string HoldReason = "endpoint-chain-ghosted";

        /// <summary>One ghost chain reduced to what the hold reads.</summary>
        internal struct ChainClaim
        {
            public uint VesselPid;
            public string LaunchGuid;
            public double SpawnUT;
            public bool IsTerminated;
            /// <summary>The flight still lists this chain as waiting for its tip spawn.</summary>
            public bool SpawnPending;
            public string TipRecordingId;
        }

        /// <summary>
        /// THE HOLD DECISION, pure. True when a claim names the endpoint's vessel and the
        /// vessel is still out of the world: same pid, launch guids not conclusively
        /// different (an unknown guid on either side is no evidence, the
        /// <see cref="VesselLaunchIdentity"/> rule), not terminated, and its tip spawn still
        /// ahead or still pending.
        /// </summary>
        internal static bool IsHeldByGhostChain(
            RouteEndpoint endpoint,
            IReadOnlyList<ChainClaim> claims,
            double currentUT,
            out ChainClaim matched)
        {
            matched = default(ChainClaim);
            uint pid = endpoint.VesselPersistentId;
            if (pid == 0u || claims == null) return false;
            for (int i = 0; i < claims.Count; i++)
            {
                ChainClaim claim = claims[i];
                if (claim.VesselPid != pid) continue;
                if (claim.IsTerminated) continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(endpoint.LaunchGuid, claim.LaunchGuid))
                    continue;
                bool stillOut = claim.SpawnPending || currentUT < claim.SpawnUT;
                if (!stillOut) continue;
                matched = claim;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Builds the claim list from the walker's chains (every scene) and the flight's
        /// still-pending chains (marked <see cref="ChainClaim.SpawnPending"/>). A pid present
        /// in both keeps one claim, pending if either says so.
        /// </summary>
        internal static List<ChainClaim> BuildClaims(
            IEnumerable<GhostChain> walkerChains,
            IEnumerable<GhostChain> flightPendingChains)
        {
            var claims = new List<ChainClaim>();
            var indexByPid = new Dictionary<uint, int>();
            Add(claims, indexByPid, walkerChains, pending: false);
            Add(claims, indexByPid, flightPendingChains, pending: true);
            return claims;
        }

        private static void Add(
            List<ChainClaim> claims, Dictionary<uint, int> indexByPid,
            IEnumerable<GhostChain> chains, bool pending)
        {
            if (chains == null) return;
            foreach (GhostChain chain in chains)
            {
                if (chain == null || chain.OriginalVesselPid == 0u) continue;
                if (indexByPid.TryGetValue(chain.OriginalVesselPid, out int existing))
                {
                    if (pending)
                    {
                        ChainClaim merged = claims[existing];
                        merged.SpawnPending = true;
                        claims[existing] = merged;
                    }
                    continue;
                }
                indexByPid[chain.OriginalVesselPid] = claims.Count;
                claims.Add(new ChainClaim
                {
                    VesselPid = chain.OriginalVesselPid,
                    LaunchGuid = chain.LaunchGuid,
                    SpawnUT = chain.SpawnUT,
                    IsTerminated = chain.IsTerminated,
                    SpawnPending = pending,
                    TipRecordingId = chain.TipRecordingId,
                });
            }
        }

        // ------------------------------------------------------------------
        // Live: the claims from the committed trees (memoized) plus the flight's
        // still-pending chains (read live).
        // ------------------------------------------------------------------

        /// <summary>The walker's claims for one committed-tree state, so the proximity step
        /// (which the Logistics window can reach every frame for a held route) does not
        /// re-walk every tree each frame. The UT test is applied live on every call; only
        /// the tree-derived claims are memoized.</summary>
        private struct ClaimsMemo
        {
            public bool Valid;
            public int StateVersion;
            public int TreeCount;
            public int AtFrame;
            public List<ChainClaim> Claims;
        }

        /// <summary>How long memoized claims are trusted, in frames, when the committed-tree
        /// state version and count are unchanged (a terminal-state change the version does not
        /// witness still self-heals within about two seconds).</summary>
        internal const int ClaimsMemoFrames = 120;

        private static ClaimsMemo claimsMemo;

        /// <summary>Test seam: drops the memoized claims.</summary>
        internal static void ResetForTesting()
        {
            claimsMemo = default(ClaimsMemo);
        }

        /// <summary>
        /// Live hold check for the resolver's surface-proximity step. Never throws: a failure
        /// to read the chain state is logged and reads as "not held", which is the
        /// pre-existing behaviour.
        /// </summary>
        internal static bool IsEndpointHeldLive(RouteEndpoint endpoint, double currentUT, out ChainClaim matched)
        {
            matched = default(ChainClaim);
            if (endpoint.VesselPersistentId == 0u) return false;
            try
            {
                List<ChainClaim> walkerClaims = ReadWalkerClaims();
                IEnumerable<GhostChain> flightPending = ParsekFlight.Instance != null
                    ? ParsekFlight.Instance.ActiveGhostChains?.Values
                    : null;
                List<ChainClaim> claims = flightPending != null
                    ? MergePending(walkerClaims, flightPending)
                    : walkerClaims;
                return IsHeldByGhostChain(endpoint, claims, currentUT, out matched);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag,
                    "endpoint-chain-hold-threw-" + endpoint.VesselPersistentId.ToString(IC),
                    "Endpoint chain hold check threw " + ex.GetType().Name + ": " + ex.Message
                    + " pid=" + endpoint.VesselPersistentId.ToString(IC)
                    + " - treating the endpoint as not chain-ghosted");
                return false;
            }
        }

        private static List<ChainClaim> MergePending(List<ChainClaim> walkerClaims, IEnumerable<GhostChain> flightPending)
        {
            var merged = new List<ChainClaim>(walkerClaims);
            var indexByPid = new Dictionary<uint, int>();
            for (int i = 0; i < merged.Count; i++)
                if (!indexByPid.ContainsKey(merged[i].VesselPid)) indexByPid[merged[i].VesselPid] = i;
            Add(merged, indexByPid, flightPending, pending: true);
            return merged;
        }

        private static List<ChainClaim> ReadWalkerClaims()
        {
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            int treeCount = trees != null ? trees.Count : 0;
            int version = RecordingStore.StateVersion;
            int frame = ReadFrameCount();
            if (claimsMemo.Valid
                && claimsMemo.StateVersion == version
                && claimsMemo.TreeCount == treeCount
                && frame > 0 && claimsMemo.AtFrame > 0
                && frame >= claimsMemo.AtFrame
                && frame - claimsMemo.AtFrame < ClaimsMemoFrames)
            {
                return claimsMemo.Claims;
            }

            List<ChainClaim> claims;
            if (treeCount == 0)
            {
                claims = new List<ChainClaim>();
            }
            else
            {
                // The walker ignores its UT argument; the spawn-UT test is applied per call.
                Dictionary<uint, GhostChain> chains = GhostChainWalker.ComputeAllGhostChains(trees, 0.0);
                claims = BuildClaims(chains?.Values, null);
            }
            claimsMemo = new ClaimsMemo
            {
                Valid = true,
                StateVersion = version,
                TreeCount = treeCount,
                AtFrame = frame,
                Claims = claims,
            };
            return claims;
        }

        private static int ReadFrameCount()
        {
            try
            {
                return UnityEngine.Time.frameCount;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Logs a hold (Info, rate-limited: the Logistics window re-resolves every frame and
        /// the route tick every second). The key carries the pid and the chain's spawn UT, so a
        /// different chain prints at once.
        /// </summary>
        internal static void LogHold(RouteEndpoint endpoint, ChainClaim claim, double currentUT)
        {
            ParsekLog.InfoRateLimited(Tag,
                "endpoint-chain-hold-" + endpoint.VesselPersistentId.ToString(IC)
                + "-" + claim.SpawnUT.ToString("R", IC),
                "Endpoint HELD, not rebound: pid=" + endpoint.VesselPersistentId.ToString(IC)
                + " rootPartUId=" + endpoint.RootPartUId.ToString(IC)
                + " body=" + (endpoint.BodyName ?? "<none>")
                + " reason=" + HoldReason
                + " chainTip=" + (claim.TipRecordingId ?? "<none>")
                + " spawnUT=" + claim.SpawnUT.ToString("R", IC)
                + " pending=" + (claim.SpawnPending ? "1" : "0")
                + " ut=" + currentUT.ToString("R", IC)
                + " - the recorded endpoint is a ghost until its chain tip respawns it;"
                + " the surface-proximity step is skipped so no nearby craft takes the route",
                30.0);
        }
    }
}
