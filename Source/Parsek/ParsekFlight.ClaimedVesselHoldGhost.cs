using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// The ghost of one held-back vessel before its first claim (design 12.5): built from
        /// the last Rewind-to-Launch's own copy of the vessel when it has one (the landing world
        /// holds the vessel in its post-claim form), else from the snapshot <c>VesselGhoster</c>
        /// took before the despawn, and posed where that copy says. Resolved once when shown.
        /// </summary>
        private sealed class ClaimedVesselHoldGhostState
        {
            public uint pid;
            public uint chainPid;
            public string vesselName;
            public ConfigNode snapshot;
            public ConfigNode visual;
            public bool fromRewindSave;
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

        // Design section 2.12 Rule 3: survivors of a Re-Fly load that a committed recording
        // flies or recovers after the rewind UT (ReFlyInvokeHold), held without a ghost chain.
        // Keyed by pid; FirstClaimUT is the first committed use, after which the hold ghost
        // yields to the committed recording.
        private readonly Dictionary<uint, ChainClaimedVessel> reFlyInvokeHolds =
            new Dictionary<uint, ChainClaimedVessel>();

        /// <summary>
        /// Second feed of the claimed-vessel hold (the first is the ghost-chain walk): ghosts
        /// each live survivor in <paramref name="entries"/> through <see cref="VesselGhoster"/>
        /// and holds it until its first committed use. A vessel the chain feed already holds is
        /// left to it. Releases the end-spawn claim a flying recording of the same launch keeps
        /// on the despawned pid, so that recording brings the vessel back at its end. Returns
        /// the entries actually held.
        /// </summary>
        internal List<ReFlyInvokeHold.Entry> HoldReFlyInvokeVessels(
            IList<ReFlyInvokeHold.Entry> entries, out int notLive, out int alreadyHeld)
        {
            notLive = 0;
            alreadyHeld = 0;
            var held = new List<ReFlyInvokeHold.Entry>();
            if (entries == null || entries.Count == 0)
                return held;
            if (vesselGhoster == null)
                vesselGhoster = new VesselGhoster();

            for (int i = 0; i < entries.Count; i++)
            {
                ReFlyInvokeHold.Entry entry = entries[i];
                if (entry == null || entry.Pid == 0u) continue;
                if (vesselGhoster.IsGhosted(entry.Pid))
                {
                    alreadyHeld++;
                    continue;
                }
                if (FlightRecorder.FindVesselByPid(entry.Pid) == null || !vesselGhoster.GhostVessel(entry.Pid))
                {
                    notLive++;
                    continue;
                }
                AddReFlyInvokeHold(entry, "refly-invoke-hold");
                held.Add(entry);
            }

            if (held.Count > 0)
                GhostPlaybackLogic.SetIsGhostedOverride(
                    pid => vesselGhoster != null && vesselGhoster.IsGhosted(pid));
            return held;
        }

        private void AddReFlyInvokeHold(ReFlyInvokeHold.Entry entry, string logContext)
        {
            reFlyInvokeHolds[entry.Pid] = new ChainClaimedVessel
            {
                Pid = entry.Pid,
                LaunchGuid = entry.LaunchGuid,
                FirstClaimUT = entry.FirstClaimUT,
            };
            for (int r = 0; r < entry.FlyingRecordingIds.Count; r++)
            {
                Recording rec = FindTipRecordingById(entry.FlyingRecordingIds[r]);
                VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(rec, entry.Pid, logContext);
            }
        }

        /// <summary>
        /// Design section 2.12 Rule 3 after a reload during a Re-Fly session (F9, a cold load
        /// of a save written mid-session, a scene reload): the invoke's holds live in memory
        /// and the held vessels were despawned before any save, so the reload left them out of
        /// the world with nothing drawing them. Re-takes every hold the session marker saved
        /// (<see cref="ReFlySessionMarker.InvokeHolds"/>): a vessel the chain feed holds is left
        /// to it, a live one is despawned as the invoke did, and a gone one is tracked with the
        /// rewind point's own copy of it as the hold ghost's shape. Skipped while a Re-Fly
        /// invoke is in flight (it takes its own holds). Logs one summary.
        /// </summary>
        private void RestoreReFlyInvokeHoldsFromMarker(double currentUT)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario)) return;
            ReFlySessionMarker marker = scenario.ActiveReFlySessionMarker;
            if (marker == null || marker.InvokeHolds == null || marker.InvokeHolds.Count == 0)
                return;
            if (RewindInvokeContext.Pending)
            {
                ParsekLog.Verbose(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke holds not restored: an invoke is in flight sess={marker.SessionId ?? "<no-id>"}");
                return;
            }
            if (vesselGhoster == null)
                vesselGhoster = new VesselGhoster();

            ConfigNode rpFlightState = null;
            bool rpFlightStateRead = false;
            int restored = 0, heldLive = 0, alreadyHeld = 0, claimPassed = 0, noSnapshot = 0, otherLaunch = 0;
            for (int i = 0; i < marker.InvokeHolds.Count; i++)
            {
                ReFlyInvokeHold.Entry entry = marker.InvokeHolds[i];
                if (entry == null || entry.Pid == 0u) continue;
                Vessel live = FlightRecorder.FindVesselByPid(entry.Pid);
                string liveGuid = live != null && live.id != Guid.Empty ? live.id.ToString("N") : null;
                ConfigNode copy = null;
                if (live == null && currentUT < entry.FirstClaimUT && !vesselGhoster.IsGhosted(entry.Pid))
                {
                    if (!rpFlightStateRead)
                    {
                        rpFlightState = ReadRewindPointFlightState(scenario, marker.RewindPointId);
                        rpFlightStateRead = true;
                    }
                    copy = ReFlyInvokeHold.FindHeldVesselNode(rpFlightState, entry.Pid, entry.LaunchGuid);
                }
                string action = ReFlyInvokeHold.DecideReloadAction(
                    entry, currentUT, vesselGhoster.IsGhosted(entry.Pid), live != null, liveGuid, copy != null);
                switch (action)
                {
                    case "claim-passed": claimPassed++; continue;
                    case "already-held": alreadyHeld++; continue;
                    case "other-launch": otherLaunch++; continue;
                    case "no-snapshot": noSnapshot++; continue;
                    case "hold-live":
                        if (!vesselGhoster.GhostVessel(entry.Pid)) { noSnapshot++; continue; }
                        heldLive++;
                        break;
                    case "restore":
                        if (!vesselGhoster.TrackHeldVesselWithoutLiveVessel(
                                entry.Pid, copy.GetValue("name"), copy.CreateCopy()))
                        {
                            alreadyHeld++;
                            continue;
                        }
                        restored++;
                        break;
                    default:
                        continue;
                }
                AddReFlyInvokeHold(entry, "refly-invoke-hold-reload");
            }

            if (restored + heldLive > 0)
                GhostPlaybackLogic.SetIsGhostedOverride(
                    pid => vesselGhoster != null && vesselGhoster.IsGhosted(pid));

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                $"Re-Fly invoke holds restored after reload: sess={marker.SessionId ?? "<no-id>"} " +
                $"saved={marker.InvokeHolds.Count.ToString(ic)} restored={restored.ToString(ic)} " +
                $"heldLive={heldLive.ToString(ic)} alreadyHeld={alreadyHeld.ToString(ic)} " +
                $"claimPassed={claimPassed.ToString(ic)} otherLaunch={otherLaunch.ToString(ic)} " +
                $"noSnapshot={noSnapshot.ToString(ic)} UT={currentUT.ToString("F1", ic)}");
        }

        /// <summary>The FLIGHTSTATE of the rewind point's quicksave, or null when it cannot be read.</summary>
        private static ConfigNode ReadRewindPointFlightState(ParsekScenario scenario, string rewindPointId)
        {
            if (scenario.RewindPoints == null || string.IsNullOrEmpty(rewindPointId)) return null;
            RewindPoint rp = null;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                if (scenario.RewindPoints[i] != null && scenario.RewindPoints[i].RewindPointId == rewindPointId)
                {
                    rp = scenario.RewindPoints[i];
                    break;
                }
            }
            string path = RewindInvoker.ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke hold reload: rewind point save unreadable rp={rewindPointId} " +
                    $"path='{path ?? "<none>"}'");
                return null;
            }
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return null;
                ConfigNode game = root.HasNode("GAME") ? root.GetNode("GAME") : root;
                return game?.GetNode("FLIGHTSTATE");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke hold reload: reading rp={rewindPointId} threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

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
                && reFlyInvokeHolds.Count == 0
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

            if (vesselGhoster != null && reFlyInvokeHolds.Count > 0)
            {
                foreach (var kv in reFlyInvokeHolds)
                {
                    ChainClaimedVessel claim = kv.Value;
                    if (claimedVesselHoldDesiredScratch.ContainsKey(claim.Pid))
                        continue;
                    var info = vesselGhoster.GetGhostedInfo(claim.Pid);
                    if (info == null || info.snapshot == null || currentUT >= claim.FirstClaimUT)
                        continue;
                    if (ers == null) ers = EffectiveState.ComputeERS();
                    if (DecideReFlyInvokeHoldHideReason(claim, currentUT, ers) != null)
                        continue;
                    claimedVesselHoldDesiredScratch[claim.Pid] =
                        new KeyValuePair<GhostChain, ChainClaimedVessel>(null, claim);
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
            ChainClaimedVessel invokeClaim;
            if (reFlyInvokeHolds.TryGetValue(pid, out invokeClaim))
                return DecideReFlyInvokeHoldHideReason(invokeClaim, currentUT, EffectiveState.ComputeERS())
                    ?? "unknown";
            return "chain-inactive";
        }

        private static string DecideReFlyInvokeHoldHideReason(
            ChainClaimedVessel claim, double currentUT, IReadOnlyList<Recording> ers)
        {
            return ClaimedVesselHoldGhost.DecideHideReason(
                hasSnapshot: true,
                chainActive: true,
                currentUT: currentUT,
                firstClaimUT: claim.FirstClaimUT,
                coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                    claim.Pid, claim.LaunchGuid, ers, currentUT));
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
                chainPid = chain != null ? chain.OriginalVesselPid : 0u,
                vesselName = info.vesselName,
                snapshot = info.snapshot,
                shownSinceUT = currentUT,
            };
            ConfigNode rewindCopy = ClaimedVesselHoldGhost.TryGetRewindSaveVessel(claim.Pid, claim.LaunchGuid);
            s.fromRewindSave = rewindCopy != null;
            s.visual = rewindCopy ?? info.snapshot;

            if (!ClaimedVesselHoldGhost.TryReadPose(s.visual, out s.pose)
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
                    s.orbit = new OrbitSnapshot(s.visual.GetNode("ORBIT")).Load();
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
                GhostVisualSnapshot = s.visual,
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
            ConfigNode[] parts = s.visual.GetNodes("PART");
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                ClaimedVesselHoldGhost.BuildShownMessage(
                    s.pid, s.vesselName, s.chainPid, s.pose.Landed, s.body.name,
                    parts != null ? parts.Length : 0, claim.FirstClaimUT, currentUT, s.fromRewindSave));
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
