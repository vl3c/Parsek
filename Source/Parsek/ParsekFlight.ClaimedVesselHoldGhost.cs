using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// The ghost of one held-back vessel before its first claim (design 12.5): built from
        /// the snapshot <c>VesselGhoster</c> took before the despawn, so it shows the vessel as
        /// it was, and posed where that snapshot says. Resolved once when shown.
        /// </summary>
        private sealed class ClaimedVesselHoldGhostState
        {
            public uint pid;
            public uint chainPid;
            public string vesselName;
            public ConfigNode snapshot;
            public GameObject ghost;
            public bool buildFailed;
            public CelestialBody body;
            public Orbit orbit;
            public ClaimedVesselHoldPose pose;
            public Quaternion surfaceRelativeRotation;
            public double shownSinceUT;
            public bool inRange;
        }

        private readonly Dictionary<uint, ClaimedVesselHoldGhostState> claimedVesselHoldGhosts =
            new Dictionary<uint, ClaimedVesselHoldGhostState>();
        private readonly List<uint> claimedVesselHoldGhostReleaseScratch = new List<uint>();
        private readonly Dictionary<uint, KeyValuePair<GhostChain, ChainClaimedVessel>> claimedVesselHoldDesiredScratch =
            new Dictionary<uint, KeyValuePair<GhostChain, ChainClaimedVessel>>();

        /// <summary>In-game test seam: number of held-back vessels drawn from their despawn snapshot.</summary>
        internal int ClaimedVesselHoldGhostCountForInGameTest
        {
            get
            {
                int n = 0;
                foreach (var kvp in claimedVesselHoldGhosts)
                    if (kvp.Value.ghost != null) n++;
                return n;
            }
        }

        /// <summary>
        /// Draws every vessel an active ghost chain holds back while the clock is before its
        /// first claim, and lets go of it at the claim (the committed recording that carries
        /// the vessel takes over), when a recording of the same launch is already drawing it,
        /// or when the chain goes away.
        /// </summary>
        private void UpdateClaimedVesselHoldGhosts(double currentUT)
        {
            if (claimedVesselHoldGhosts.Count == 0
                && (activeGhostChains == null || activeGhostChains.Count == 0 || vesselGhoster == null))
                return;

            claimedVesselHoldDesiredScratch.Clear();
            IReadOnlyList<Recording> ers = null;
            if (activeGhostChains != null && vesselGhoster != null)
            {
                foreach (var kv in activeGhostChains)
                {
                    GhostChain chain = kv.Value;
                    if (chain == null || chain.IsTerminated) continue;
                    List<ChainClaimedVessel> held = chain.HeldBackVessels();
                    for (int h = 0; h < held.Count; h++)
                    {
                        ChainClaimedVessel claim = held[h];
                        if (claim.Pid == 0 || claimedVesselHoldDesiredScratch.ContainsKey(claim.Pid))
                            continue;
                        var info = vesselGhoster.GetGhostedInfo(claim.Pid);
                        if (info == null || info.snapshot == null || currentUT >= claim.FirstClaimUT)
                            continue;
                        if (ers == null) ers = EffectiveState.ComputeERS();
                        string reason = ClaimedVesselHoldGhost.DecideHideReason(
                            hasSnapshot: true,
                            chainActive: true,
                            currentUT: currentUT,
                            firstClaimUT: claim.FirstClaimUT,
                            coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                                claim.Pid, claim.LaunchGuid, ers, currentUT));
                        if (reason != null) continue;
                        claimedVesselHoldDesiredScratch[claim.Pid] =
                            new KeyValuePair<GhostChain, ChainClaimedVessel>(chain, claim);
                    }
                }
            }

            ReleaseClaimedVesselHoldGhosts(currentUT);

            foreach (var kv in claimedVesselHoldDesiredScratch)
            {
                ClaimedVesselHoldGhostState s;
                if (!claimedVesselHoldGhosts.TryGetValue(kv.Key, out s))
                {
                    s = CaptureClaimedVesselHoldGhost(kv.Value.Key, kv.Value.Value, currentUT);
                    claimedVesselHoldGhosts[kv.Key] = s;
                }
                PositionClaimedVesselHoldGhost(s, currentUT);
            }
            claimedVesselHoldDesiredScratch.Clear();
        }

        private void ReleaseClaimedVesselHoldGhosts(double currentUT)
        {
            if (claimedVesselHoldGhosts.Count == 0)
                return;
            claimedVesselHoldGhostReleaseScratch.Clear();
            foreach (var kvp in claimedVesselHoldGhosts)
            {
                ClaimedVesselHoldGhostState s = kvp.Value;
                var info = vesselGhoster != null ? vesselGhoster.GetGhostedInfo(s.pid) : null;
                bool sameSnapshot = info != null && ReferenceEquals(info.snapshot, s.snapshot);
                if (claimedVesselHoldDesiredScratch.ContainsKey(kvp.Key) && sameSnapshot)
                    continue;
                string reason = !sameSnapshot ? "ghosting-ended" : ResolveHoldReleaseReason(s.pid, currentUT);
                DestroyClaimedVesselHoldGhost(s, reason, currentUT);
                claimedVesselHoldGhostReleaseScratch.Add(kvp.Key);
            }
            for (int i = 0; i < claimedVesselHoldGhostReleaseScratch.Count; i++)
                claimedVesselHoldGhosts.Remove(claimedVesselHoldGhostReleaseScratch[i]);
            claimedVesselHoldGhostReleaseScratch.Clear();
        }

        /// <summary>Names why a shown hold ghost is no longer wanted (for its release line).</summary>
        private string ResolveHoldReleaseReason(uint pid, double currentUT)
        {
            if (activeGhostChains != null)
            {
                foreach (var kv in activeGhostChains)
                {
                    GhostChain chain = kv.Value;
                    if (chain == null || chain.IsTerminated) continue;
                    List<ChainClaimedVessel> held = chain.HeldBackVessels();
                    for (int h = 0; h < held.Count; h++)
                    {
                        if (held[h].Pid != pid) continue;
                        return ClaimedVesselHoldGhost.DecideHideReason(
                            hasSnapshot: true, chainActive: true, currentUT: currentUT,
                            firstClaimUT: held[h].FirstClaimUT,
                            coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                                pid, held[h].LaunchGuid, EffectiveState.ComputeERS(), currentUT))
                            ?? "unknown";
                    }
                }
            }
            return "chain-inactive";
        }

        private void DestroyClaimedVesselHoldGhost(ClaimedVesselHoldGhostState s, string reason, double currentUT)
        {
            if (s.ghost != null)
                UnityEngine.Object.Destroy(s.ghost);
            if (!s.buildFailed)
                ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                    ClaimedVesselHoldGhost.BuildReleasedMessage(
                        s.pid, s.vesselName, reason, s.shownSinceUT, currentUT));
            s.ghost = null;
        }

        /// <summary>Destroys every hold ghost (scene teardown, chain reset).</summary>
        private void ClearClaimedVesselHoldGhosts(string reason)
        {
            if (claimedVesselHoldGhosts.Count == 0)
                return;
            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); }
            catch (Exception) { }
            foreach (var kvp in claimedVesselHoldGhosts)
                DestroyClaimedVesselHoldGhost(kvp.Value, reason, ut);
            claimedVesselHoldGhosts.Clear();
        }

        private ClaimedVesselHoldGhostState CaptureClaimedVesselHoldGhost(
            GhostChain chain, ChainClaimedVessel claim, double currentUT)
        {
            var info = vesselGhoster.GetGhostedInfo(claim.Pid);
            var s = new ClaimedVesselHoldGhostState
            {
                pid = claim.Pid,
                chainPid = chain.OriginalVesselPid,
                vesselName = info.vesselName,
                snapshot = info.snapshot,
                shownSinceUT = currentUT,
            };

            if (!ClaimedVesselHoldGhost.TryReadPose(info.snapshot, out s.pose)
                || FlightGlobals.Bodies == null || s.pose.BodyIndex >= FlightGlobals.Bodies.Count)
            {
                s.buildFailed = true;
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Claimed vessel hold ghost not drawn: pid={0} name='{1}' reason=snapshot-pose-unreadable",
                    s.pid, s.vesselName ?? "(unnamed)"));
                return s;
            }
            s.body = FlightGlobals.Bodies[s.pose.BodyIndex];
            s.surfaceRelativeRotation = s.pose.HasRotation
                ? new Quaternion((float)s.pose.RotX, (float)s.pose.RotY, (float)s.pose.RotZ, (float)s.pose.RotW)
                : Quaternion.identity;
            if (!s.pose.Landed)
            {
                try
                {
                    s.orbit = new OrbitSnapshot(info.snapshot.GetNode("ORBIT")).Load();
                }
                catch (Exception ex)
                {
                    s.orbit = null;
                    ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Claimed vessel hold ghost orbit unreadable: pid={0} name='{1}': {2}",
                        s.pid, s.vesselName ?? "(unnamed)", ex.Message));
                }
                if (s.orbit == null)
                {
                    s.buildFailed = true;
                    return s;
                }
            }

            var carrier = new Recording
            {
                VesselName = s.vesselName,
                VesselPersistentId = s.pid,
                GhostVisualSnapshot = info.snapshot,
            };
            GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                carrier, "Parsek_ClaimedHold_" + s.pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (build == null || build.root == null)
            {
                s.buildFailed = true;
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Claimed vessel hold ghost not drawn: pid={0} name='{1}' reason=ghost-build-failed",
                    s.pid, s.vesselName ?? "(unnamed)"));
                return s;
            }
            s.ghost = build.root;
            ConfigNode[] parts = info.snapshot.GetNodes("PART");
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                ClaimedVesselHoldGhost.BuildShownMessage(
                    s.pid, s.vesselName, s.chainPid, s.pose.Landed, s.body.name,
                    parts != null ? parts.Length : 0, claim.FirstClaimUT, currentUT));
            return s;
        }

        private void PositionClaimedVesselHoldGhost(ClaimedVesselHoldGhostState s, double currentUT)
        {
            if (s == null || s.ghost == null || s.body == null)
                return;

            double lat = s.pose.Lat;
            double lon = s.pose.Lon;
            double alt = s.pose.Alt;
            Vector3d pos;
            if (!s.pose.Landed && s.orbit != null)
            {
                pos = s.orbit.getPositionAtUT(currentUT);
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z)
                    || double.IsInfinity(pos.x) || double.IsInfinity(pos.y) || double.IsInfinity(pos.z))
                    return;
                lat = s.body.GetLatitude(pos);
                lon = s.body.GetLongitude(pos);
                alt = s.body.GetAltitude(pos);
            }
            else
            {
                pos = s.body.GetWorldSurfacePosition(lat, lon, alt);
            }

            // Out of visual range the ghost is hidden, the same range every playback ghost uses.
            Vessel active = FlightGlobals.ActiveVessel;
            bool inRange = active == null
                || Vector3d.Distance(active.GetWorldPos3D(), pos) <= DistanceThresholds.GhostVisualRangeMeters;
            if (inRange != s.inRange || s.ghost.activeSelf != inRange)
            {
                s.ghost.SetActive(inRange);
                if (inRange != s.inRange)
                    ParsekLog.Verbose(ClaimedVesselHoldGhost.Tag, string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Claimed vessel hold ghost {0}: pid={1} UT={2:F2}",
                        inRange ? "in visual range" : "out of visual range", s.pid, currentUT));
                s.inRange = inRange;
            }
            if (!inRange)
                return;

            s.ghost.transform.position = pos;
            s.ghost.transform.rotation = s.body.bodyTransform.rotation * s.surfaceRelativeRotation;
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = s.ghost,
                mode = GhostPosMode.SinglePoint,
                recordingId = null,
                recordingIndex = -1,
                hasRecordingIndex = false,
                bodyBefore = s.body,
                latBefore = lat,
                lonBefore = lon,
                altBefore = alt,
                pointUT = currentUT,
                interpolatedRot = s.surfaceRelativeRotation,
            });
        }
    }
}
