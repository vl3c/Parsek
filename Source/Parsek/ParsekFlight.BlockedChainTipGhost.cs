using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    // [ERS-exempt] Reads RecordingStore.CommittedRecordings by index only to pair a
    // policy-held slot with its engine ghost (the engine's index-keyed contract). A
    // superseded or rewind-retired tip is released by the policy's own hold checks.
    public partial class ParsekFlight
    {
        /// <summary>
        /// One spawn-blocked chain tip whose playback ghost the policy is holding past its end
        /// UT (phase 6b-4, design 12.9.2 / 13.5). Resolved once when the hold is first seen, so
        /// the per-frame positioning reads cached fields and allocates nothing.
        /// </summary>
        private sealed class BlockedChainTipGhostState
        {
            public GhostChain chain;
            public uint chainPid;
            public int index;
            public string recordingId;
            public string vesselName;
            public GameObject ghost;
            public BlockedChainTipPoseSource source;
            public CelestialBody body;
            public Orbit orbit;
            public double holdLat;
            public double holdLon;
            public double holdAlt;
            public Quaternion surfaceRelativeRotation;
            public double heldSinceUT;
            public float lastPositionLogRealTime;
        }

        private const float BlockedChainTipGhostPositionLogIntervalSeconds = 5f;

        private readonly Dictionary<uint, BlockedChainTipGhostState> blockedChainTipGhosts =
            new Dictionary<uint, BlockedChainTipGhostState>();
        private readonly List<uint> blockedChainTipGhostReleaseScratch = new List<uint>();

        /// <summary>
        /// True when <paramref name="rec"/> is the tip of an active ghost chain whose spawn is
        /// collision-blocked. The policy exempts such a held ghost from the hold timeout.
        /// </summary>
        internal bool IsSpawnBlockedChainTipFromPolicy(Recording rec)
        {
            if (rec == null)
                return false;
            GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
            return chain != null && chain.SpawnBlocked;
        }

        /// <summary>In-game test seam: number of blocked chain tip ghosts being drawn.</summary>
        internal int BlockedChainTipGhostCountForInGameTest => blockedChainTipGhosts.Count;

        /// <summary>
        /// In-game test seam: positions <paramref name="ghost"/> through the production
        /// blocked-tip positioning (orbit source when <paramref name="orbit"/> is non-null, else
        /// the body-fixed hold), registering its FloatingOrigin reapply entry exactly as the
        /// per-frame pass does. Not called by any player path.
        /// </summary>
        internal void PositionBlockedChainTipGhostForInGameTest(
            GameObject ghost, CelestialBody body, Orbit orbit, Quaternion surfaceRelativeRotation,
            double holdLat, double holdLon, double holdAlt, double ut)
        {
            var s = new BlockedChainTipGhostState
            {
                recordingId = "ingame-blocked-chain-tip-probe",
                index = -1,
                ghost = ghost,
                body = body,
                orbit = orbit,
                source = ChainTipBlockedGhost.ResolvePoseSource(orbit != null, orbit != null),
                holdLat = holdLat,
                holdLon = holdLon,
                holdAlt = holdAlt,
                surfaceRelativeRotation = surfaceRelativeRotation,
                heldSinceUT = ut,
                lastPositionLogRealTime = Time.realtimeSinceStartup,
            };
            PositionBlockedChainTipGhost(s, ut);
        }

        /// <summary>
        /// Draws the held ghost of every spawn-blocked chain tip at the tip's propagated
        /// position, and lets go of it the moment the tip spawns or the hold loses its subject.
        /// Runs after the policy's held-ghost retries, so a spawn that succeeded this frame has
        /// already destroyed the ghost and is released here before anything moves it.
        /// </summary>
        private void UpdateSpawnBlockedChainTipGhosts(double currentUT)
        {
            bool anyHeld = policy != null && policy.heldGhosts.Count > 0;
            if (blockedChainTipGhosts.Count == 0 && !anyHeld)
                return;

            var committed = RecordingStore.CommittedRecordings;
            ReleaseStaleBlockedChainTipGhosts(committed, currentUT);
            if (anyHeld)
                CaptureBlockedChainTipGhosts(committed, currentUT);

            foreach (var kvp in blockedChainTipGhosts)
                PositionBlockedChainTipGhost(kvp.Value, currentUT);
        }

        private void ReleaseStaleBlockedChainTipGhosts(IReadOnlyList<Recording> committed, double currentUT)
        {
            if (blockedChainTipGhosts.Count == 0)
                return;

            blockedChainTipGhostReleaseScratch.Clear();
            foreach (var kvp in blockedChainTipGhosts)
            {
                BlockedChainTipGhostState s = kvp.Value;
                bool indexStillTip = committed != null
                    && s.index >= 0 && s.index < committed.Count
                    && committed[s.index] != null
                    && committed[s.index].RecordingId == s.recordingId;
                bool tipSpawned = indexStillTip && committed[s.index].VesselSpawned;
                GhostChain current = null;
                bool chainActive = activeGhostChains != null
                    && activeGhostChains.TryGetValue(s.chainPid, out current)
                    && object.ReferenceEquals(current, s.chain)
                    && !current.IsTerminated;
                bool heldByPolicy = policy != null && policy.heldGhosts.ContainsKey(s.index);
                GhostPlaybackState state;
                bool engineGhostIntact = engine != null
                    && engine.TryGetGhostState(s.index, out state)
                    && state != null
                    && object.ReferenceEquals(state.ghost, s.ghost)
                    && s.ghost != null;

                string reason = ChainTipBlockedGhost.DecideReleaseReason(
                    tipSpawned,
                    chainActive,
                    s.chain != null && s.chain.SpawnBlocked,
                    indexStillTip,
                    heldByPolicy,
                    engineGhostIntact);
                if (reason == null)
                    continue;

                ParsekLog.Info(ChainTipBlockedGhost.Tag,
                    ChainTipBlockedGhost.BuildReleasedMessage(
                        s.index, s.recordingId, s.vesselName, s.chainPid,
                        reason, s.heldSinceUT, currentUT));
                blockedChainTipGhostReleaseScratch.Add(kvp.Key);
            }

            for (int i = 0; i < blockedChainTipGhostReleaseScratch.Count; i++)
                blockedChainTipGhosts.Remove(blockedChainTipGhostReleaseScratch[i]);
            blockedChainTipGhostReleaseScratch.Clear();
        }

        private void CaptureBlockedChainTipGhosts(IReadOnlyList<Recording> committed, double currentUT)
        {
            if (committed == null || activeGhostChains == null || activeGhostChains.Count == 0
                || engine == null)
                return;

            foreach (var kvp in policy.heldGhosts)
            {
                int index = kvp.Key;
                if (index < 0 || index >= committed.Count)
                    continue;
                Recording rec = committed[index];
                if (rec == null)
                    continue;
                GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
                if (chain == null)
                    continue;

                GhostPlaybackState state;
                bool engineHasGhost = engine.TryGetGhostState(index, out state)
                    && state != null && state.ghost != null;
                if (!ChainTipBlockedGhost.ShouldCapture(
                        alreadyCaptured: blockedChainTipGhosts.ContainsKey(chain.OriginalVesselPid),
                        tipSpawned: rec.VesselSpawned,
                        chainActive: true,
                        chainSpawnBlocked: chain.SpawnBlocked,
                        heldByPolicy: true,
                        engineHasGhost: engineHasGhost))
                    continue;

                CaptureBlockedChainTipGhost(chain, rec, index, state.ghost, currentUT);
            }
        }

        private void CaptureBlockedChainTipGhost(
            GhostChain chain, Recording rec, int index, GameObject ghost, double currentUT)
        {
            TrajectoryPoint? lastPoint = rec.Points != null && rec.Points.Count > 0
                ? (TrajectoryPoint?)rec.Points[rec.Points.Count - 1]
                : null;
            // The same body and orbit the blocked-tip spawn resolves
            // (VesselGhoster.TrySpawnBlockedChain), so the ghost is drawn where the vessel
            // would appear if the blocker moved away now.
            CelestialBody body = VesselSpawner.ResolveSpawnRotationBody(rec, lastPoint);
            bool spawnUsesOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(
                rec, !string.IsNullOrEmpty(rec.EvaCrewName));
            Orbit orbit = null;
            bool orbitBuilt = spawnUsesOrbit && body != null
                && VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(rec, body, currentUT, out orbit)
                && orbit != null;

            var s = new BlockedChainTipGhostState
            {
                chain = chain,
                chainPid = chain.OriginalVesselPid,
                index = index,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
                ghost = ghost,
                source = ChainTipBlockedGhost.ResolvePoseSource(spawnUsesOrbit, orbitBuilt),
                body = body,
                orbit = orbitBuilt ? orbit : null,
                heldSinceUT = currentUT,
                lastPositionLogRealTime = Time.realtimeSinceStartup,
            };

            if (body != null)
            {
                Vector3d endPos = ghost.transform.position;
                s.holdLat = body.GetLatitude(endPos);
                s.holdLon = body.GetLongitude(endPos);
                s.holdAlt = body.GetAltitude(endPos);
                s.surfaceRelativeRotation =
                    Quaternion.Inverse(body.bodyTransform.rotation) * ghost.transform.rotation;
            }
            else
            {
                ParsekLog.Warn(ChainTipBlockedGhost.Tag,
                    "Blocked chain tip ghost has no spawn body: #" + index
                    + " rec=" + (rec.RecordingId ?? "(none)")
                    + " - the ghost stays at its end pose without a floating-origin reapply");
            }

            blockedChainTipGhosts[s.chainPid] = s;
            ParsekLog.Info(ChainTipBlockedGhost.Tag,
                ChainTipBlockedGhost.BuildHeldMessage(
                    index, s.recordingId, s.vesselName, s.chainPid, s.source,
                    body != null ? body.name : null, currentUT, chain.BlockedSinceUT));
        }

        private void PositionBlockedChainTipGhost(BlockedChainTipGhostState s, double currentUT)
        {
            if (s.body == null || s.ghost == null)
                return;

            double lat = s.holdLat;
            double lon = s.holdLon;
            double alt = s.holdAlt;
            Vector3d pos;
            if (s.source == BlockedChainTipPoseSource.RecordedTerminalOrbit && s.orbit != null)
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

            s.ghost.transform.position = pos;
            s.ghost.transform.rotation = s.body.bodyTransform.rotation * s.surfaceRelativeRotation;

            // Same FloatingOrigin reapply every playback ghost registers, keyed by body
            // lat/lon/alt so a shift between Update and render cannot displace the ghost.
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = s.ghost,
                mode = GhostPosMode.SinglePoint,
                recordingId = s.recordingId,
                recordingIndex = s.index,
                hasRecordingIndex = true,
                bodyBefore = s.body,
                latBefore = lat,
                lonBefore = lon,
                altBefore = alt,
                pointUT = currentUT,
                interpolatedRot = s.surfaceRelativeRotation,
            });

            float now = Time.realtimeSinceStartup;
            if (ParsekLog.IsVerboseEnabled
                && now - s.lastPositionLogRealTime >= BlockedChainTipGhostPositionLogIntervalSeconds)
            {
                s.lastPositionLogRealTime = now;
                ParsekLog.Verbose(ChainTipBlockedGhost.Tag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Blocked chain tip ghost positioned: #{0} rec={1} chainPid={2} source={3} " +
                        "body={4} lat={5:F4} lon={6:F4} alt={7:F1} UT={8:F1}",
                        s.index, s.recordingId ?? "(none)", s.chainPid,
                        ChainTipBlockedGhost.FormatPoseSource(s.source),
                        s.body.name, lat, lon, alt, currentUT));
            }
        }
    }
}
