using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Manages the real→ghost vessel lifecycle: snapshot, despawn, create ghost GO,
    /// and spawn final-form vessels at chain tips.
    /// </summary>
    internal class VesselGhoster
    {
        private const string Tag = "Ghoster";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>Padding in meters added to each side of spawn AABB for collision check.</summary>
        internal const float SpawnCollisionPadding = 5f;

        private Dictionary<uint, GhostedVesselInfo> ghostedVessels =
            new Dictionary<uint, GhostedVesselInfo>();

        /// <summary>
        /// Tracks the state of a single ghosted vessel: its captured snapshot,
        /// the ghost visual GO (may be null), and identifying info.
        /// </summary>
        internal class GhostedVesselInfo
        {
            public uint vesselPid;
            public string vesselName;
            public ConfigNode snapshot;   // captured via TryBackupSnapshot before despawn
            public GameObject ghostGO;    // ghost visual (may be null if build failed/deferred)
        }

        // --- Core lifecycle ---

        /// <summary>
        /// Snapshot the real vessel, despawn it, create ghost GO.
        /// Returns true on success. On failure, vessel is left untouched.
        /// </summary>
        internal bool GhostVessel(uint vesselPid)
        {
            if (!ShouldAttemptGhosting(vesselPid, true))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic, "GhostVessel guard: pid={0} — ShouldAttemptGhosting=false", vesselPid));
                return false;
            }

            // Find vessel via PID
            Vessel vessel = FlightRecorder.FindVesselByPid(vesselPid);
            if (vessel == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "GhostVessel: pid={0} — vessel not found via FindVesselByPid", vesselPid));
                return false;
            }

            string vesselName = vessel.vesselName ?? "(unnamed)";

            // Capture snapshot before despawn
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            if (snapshot == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "GhostVessel: pid={0} name={1} — snapshot capture failed", vesselPid, vesselName));
                return false;
            }

            ParsekLog.Info(Tag,
                string.Format(ic, "Ghosting vessel: pid={0} name={1} — snapshot captured, despawning",
                    vesselPid, vesselName));

            // Despawn the real vessel
            try
            {
                vessel.Die();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "Ghost conversion FAILED: pid={0} name={1} — despawn exception: {2}. Attempting restore from snapshot.",
                        vesselPid, vesselName, ex.Message));
                try
                {
                    if (VesselSpawner.MaterializedSourceVesselExists(vesselPid))
                    {
                        ParsekLog.Info(Tag,
                            string.Format(ic,
                                "Restore from snapshot skipped after despawn failure: source vessel pid={0} still exists",
                                vesselPid));
                    }
                    else
                    {
                        VesselSpawner.RespawnVessel(snapshot, preserveIdentity: true);
                        ParsekLog.Info(Tag,
                            string.Format(ic, "Vessel restored from snapshot after despawn failure: pid={0}", vesselPid));
                    }
                }
                catch (Exception restoreEx)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Vessel restore ALSO FAILED: pid={0} — {1}. Player should reload quicksave.",
                            vesselPid, restoreEx.Message));
                }
                return false;
            }

            // Create tracking entry
            var info = new GhostedVesselInfo
            {
                vesselPid = vesselPid,
                vesselName = vesselName,
                snapshot = snapshot,
                ghostGO = null // Ghost GO creation deferred to 6b-4
            };
            ghostedVessels[vesselPid] = info;

            // Ghost GO creation placeholder — actual visual from non-Recording snapshot
            // is complex and requires ParsekFlight integration. Deferred to Task 6b-4.
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost GO creation deferred to 6b-4 for pid={0} name={1}", vesselPid, vesselName));

            ParsekLog.Info(Tag,
                string.Format(ic, "Vessel ghosted: pid={0} name={1}", vesselPid, vesselName));

            return true;
        }

        /// <summary>
        /// Is this vessel currently ghosted?
        /// </summary>
        internal bool IsGhosted(uint vesselPid)
        {
            return ghostedVessels.ContainsKey(vesselPid);
        }

        /// <summary>
        /// Get the info for a ghosted vessel (null if not ghosted).
        /// </summary>
        internal GhostedVesselInfo GetGhostedInfo(uint vesselPid)
        {
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(vesselPid, out info))
                return info;
            return null;
        }

        /// <summary>
        /// Get the ghost GO for a ghosted vessel (null if not ghosted or GO creation failed).
        /// </summary>
        internal GameObject GetGhostGO(uint vesselPid)
        {
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(vesselPid, out info))
                return info.ghostGO;
            return null;
        }

        /// <summary>
        /// Spawn the final-form vessel at chain tip, destroy ghost GO.
        /// Returns the spawned vessel's PID (0 on failure).
        /// If spawn is blocked by collision, sets chain.SpawnBlocked and returns 0.
        /// </summary>
        internal uint SpawnAtChainTip(GhostChain chain, bool allowExistingSourceDuplicate = false)
        {
            if (!CanSpawnAtChainTip(chain))
            {
                ParsekLog.Warn(Tag, "SpawnAtChainTip guard: CanSpawnAtChainTip=false");
                return 0;
            }

            // Find the tip recording from committed recordings
            string tipId = chain.TipRecordingId;
            Recording tipRecording = FindTipRecording(tipId);
            ConfigNode vesselSnapshot = tipRecording?.VesselSnapshot;
            string vesselName = tipRecording?.VesselName;
            allowExistingSourceDuplicate = allowExistingSourceDuplicate
                || VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(
                    tipRecording != null ? tipRecording.VesselPersistentId : 0u);

            uint adoptedPid = TryAdoptExistingSourceForChainTip(
                chain,
                tipRecording,
                string.Format(ic, "chain tip '{0}'", tipId),
                allowExistingSourceDuplicate);
            if (adoptedPid != 0)
                return adoptedPid;

            if (vesselSnapshot == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "SpawnAtChainTip: tip recording '{0}' has no VesselSnapshot — cannot spawn",
                        tipId));
                return 0;
            }

            // Collision check: use snapshot lat/lon/alt (where RespawnVessel actually places
            // the vessel), falling back to trajectory/terminal position (#127).
            Bounds spawnBounds = SpawnCollisionDetector.ComputeVesselBounds(vesselSnapshot);
            Vector3d spawnPos = ResolveSpawnPosition(tipRecording, vesselSnapshot);
            var (overlap, distance, blockerName, _) =
                SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(spawnPos, spawnBounds, SpawnCollisionPadding);

            if (overlap)
            {
                double currentUT = Planetarium.GetUniversalTime();
                chain.SpawnBlocked = true;
                chain.BlockedSinceUT = currentUT;
                chain.BlockedInitialDistance = distance;
                ParsekLog.Info("SpawnCollision",
                    string.Format(ic,
                        "Spawn blocked: vessel={0} overlaps with {1} at {2}m",
                        vesselName ?? "(unknown)", blockerName ?? "(unknown)",
                        distance.ToString("F1", ic)));
                return 0;
            }

            TrajectoryPoint? tipPoint = tipRecording != null && tipRecording.Points != null && tipRecording.Points.Count > 0
                ? (TrajectoryPoint?)tipRecording.Points[tipRecording.Points.Count - 1]
                : null;
            double spawnUT = Planetarium.GetUniversalTime();
            uint spawnedPid;
            if (tipPoint.HasValue)
            {
                double spawnLat, spawnLon, spawnAlt;
                VesselSpawner.ResolveSpawnPosition(
                    tipRecording,
                    -1,
                    tipPoint.Value,
                    out spawnLat,
                    out spawnLon,
                    out spawnAlt);

                CelestialBody spawnBody = VesselSpawner.ResolveSpawnRotationBody(tipRecording, tipPoint);
                bool useRecordedTerminalOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(
                    tipRecording,
                    !string.IsNullOrEmpty(tipRecording.EvaCrewName));
                Vector3d spawnVelocity;
                Orbit orbitOverride = null;
                if (useRecordedTerminalOrbit
                    && spawnBody != null
                    && VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState(
                        tipRecording,
                        spawnBody,
                        spawnUT,
                        out double orbitLat,
                        out double orbitLon,
                        out double orbitAlt,
                        out Vector3d orbitalSpawnVelocity,
                        out Orbit orbitalSpawnOrbit))
                {
                    spawnLat = orbitLat;
                    spawnLon = orbitLon;
                    spawnAlt = orbitAlt;
                    spawnVelocity = orbitalSpawnVelocity;
                    orbitOverride = orbitalSpawnOrbit;
                }
                else
                {
                    spawnVelocity = new Vector3d(
                        tipPoint.Value.velocity.x,
                        tipPoint.Value.velocity.y,
                        tipPoint.Value.velocity.z);
                }

                spawnedPid = SpawnChainTipWithResolvedState(
                    tipRecording,
                    vesselSnapshot,
                    string.Format(ic, "chain tip '{0}'", tipId),
                    spawnUT,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    spawnVelocity,
                    tipPoint,
                    spawnBody,
                    orbitOverride);
            }
            else
            {
                spawnedPid = VesselSpawner.RespawnValidatedRecording(
                    tipRecording,
                    string.Format(ic, "chain tip '{0}'", tipId),
                    preserveIdentity: true,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
            }

            if (spawnedPid == 0)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "SpawnAtChainTip: spawn path returned pid=0 for tip '{0}' — spawn failed",
                        tipId));
                return 0;
            }

            MarkChainTipRecordingSpawned(
                tipRecording,
                spawnedPid,
                string.Format(ic, "chain tip '{0}'", tipId));

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Chain tip spawn: pid={0} vessel={1} preserveIdentity=true — real vessel created",
                    spawnedPid, vesselName ?? "(unknown)"));

            // Destroy ghost GO if it exists, and remove tracking entry
            CleanupGhostedVessel(chain.OriginalVesselPid);

            return spawnedPid;
        }

        /// <summary>
        /// Called each frame for chains where SpawnBlocked == true.
        /// Propagates ghost position via GhostExtender, rechecks overlap.
        /// If clear: spawns at propagated position, clears SpawnBlocked, returns PID.
        /// If still blocked: returns 0.
        /// </summary>
        internal uint TrySpawnBlockedChain(
            GhostChain chain,
            double currentUT,
            bool allowExistingSourceDuplicate = false)
        {
            if (chain == null || !chain.SpawnBlocked)
            {
                ParsekLog.Verbose(Tag, "TrySpawnBlockedChain: chain is null or not blocked");
                return 0;
            }

            string tipId = chain.TipRecordingId;
            Recording tipRecording = FindTipRecording(tipId);
            if (tipRecording == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic, "TrySpawnBlockedChain: tip recording '{0}' not found", tipId));
                return 0;
            }
            allowExistingSourceDuplicate = allowExistingSourceDuplicate
                || VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(
                    tipRecording.VesselPersistentId);

            uint adoptedPid = TryAdoptExistingSourceForChainTip(
                chain,
                tipRecording,
                string.Format(ic, "blocked chain tip '{0}'", tipId),
                allowExistingSourceDuplicate);
            if (adoptedPid != 0)
                return adoptedPid;

            ConfigNode vesselSnapshot = tipRecording.VesselSnapshot;
            if (vesselSnapshot == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic, "TrySpawnBlockedChain: tip '{0}' has no VesselSnapshot", tipId));
                return 0;
            }

            // Propagate ghost position using GhostExtender
            Vector3d propagatedPos = ComputePropagatedPosition(tipRecording, currentUT);

            // Recheck overlap at propagated position
            Bounds spawnBounds = SpawnCollisionDetector.ComputeVesselBounds(vesselSnapshot);
            var (overlap, distance, blockerName, _) =
                SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(propagatedPos, spawnBounds, SpawnCollisionPadding);

            if (overlap)
            {
                double blockedDuration = currentUT - chain.BlockedSinceUT;

                // Trajectory walkback: if blocked for > 5s and blocker hasn't moved,
                // walk backward along the recorded trajectory to find a valid spawn position.
                // Skip walkback if already exhausted (entire trajectory was scanned with no valid spot).
                float distanceChange = Math.Abs(distance - chain.BlockedInitialDistance);
                if (!chain.WalkbackExhausted
                    && SpawnCollisionDetector.ShouldTriggerWalkback(
                        chain.BlockedSinceUT, currentUT, 5.0, distanceChange, 1.0f)
                    && tipRecording.Points != null && tipRecording.Points.Count > 1)
                {
                    uint walkbackResult = TryWalkbackSpawn(
                        chain,
                        tipRecording,
                        vesselSnapshot,
                        spawnBounds,
                        blockerName,
                        allowExistingSourceDuplicate);
                    if (walkbackResult != 0)
                        return walkbackResult;
                }

                string exhaustedSuffix = chain.WalkbackExhausted ? " [walkback exhausted]" : "";
                ParsekLog.VerboseRateLimited("SpawnCollision", "blocked-recheck-" + chain.OriginalVesselPid,
                    string.Format(ic,
                        "Spawn still blocked: vessel={0} overlaps with {1} at {2}m (blocked {3}s){4}",
                        tipRecording.VesselName ?? "(unknown)", blockerName ?? "(unknown)",
                        distance.ToString("F1", ic), blockedDuration.ToString("F1", ic),
                        exhaustedSuffix));
                return 0;
            }

            // Overlap cleared — spawn at propagated position
            double clearDuration = currentUT - chain.BlockedSinceUT;
            ParsekLog.Info("SpawnCollision",
                string.Format(ic,
                    "Spawn cleared: vessel={0} — overlap resolved after {1}s",
                    tipRecording.VesselName ?? "(unknown)", clearDuration.ToString("F1", ic)));

            TrajectoryPoint? tipPoint = tipRecording.Points != null && tipRecording.Points.Count > 0
                ? (TrajectoryPoint?)tipRecording.Points[tipRecording.Points.Count - 1]
                : null;
            uint spawnedPid;
            CelestialBody spawnBody = VesselSpawner.ResolveSpawnRotationBody(tipRecording, tipPoint);
            if (spawnBody != null)
            {
                bool useRecordedTerminalOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(
                    tipRecording,
                    !string.IsNullOrEmpty(tipRecording.EvaCrewName));
                double spawnLat = 0.0;
                double spawnLon = 0.0;
                double spawnAlt = 0.0;
                Vector3d spawnVelocity;
                Orbit orbitOverride = null;
                bool haveResolvedSpawnPosition = false;

                if (useRecordedTerminalOrbit
                    && VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState(
                        tipRecording,
                        spawnBody,
                        currentUT,
                        out double orbitLat,
                        out double orbitLon,
                        out double orbitAlt,
                        out Vector3d orbitalSpawnVelocity,
                        out Orbit orbitalSpawnOrbit))
                {
                    spawnLat = orbitLat;
                    spawnLon = orbitLon;
                    spawnAlt = orbitAlt;
                    spawnVelocity = orbitalSpawnVelocity;
                    orbitOverride = orbitalSpawnOrbit;
                    haveResolvedSpawnPosition = true;
                }
                else
                {
                    if (propagatedPos.sqrMagnitude > 0.0 && !double.IsNaN(propagatedPos.x))
                    {
                        spawnLat = spawnBody.GetLatitude(propagatedPos);
                        spawnLon = spawnBody.GetLongitude(propagatedPos);
                        spawnAlt = spawnBody.GetAltitude(propagatedPos);
                        haveResolvedSpawnPosition = true;
                    }
                    else if (tipPoint.HasValue)
                    {
                        VesselSpawner.ResolveSpawnPosition(
                            tipRecording,
                            -1,
                            tipPoint.Value,
                            out spawnLat,
                            out spawnLon,
                            out spawnAlt);
                        haveResolvedSpawnPosition = true;
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "blocked chain tip '{0}': propagated position unavailable — using endpoint spawn state",
                                tipId));
                    }

                    spawnVelocity = tipPoint.HasValue
                        ? new Vector3d(
                            tipPoint.Value.velocity.x,
                            tipPoint.Value.velocity.y,
                            tipPoint.Value.velocity.z)
                        : Vector3d.zero;
                }

                if (haveResolvedSpawnPosition)
                {
                    spawnedPid = SpawnChainTipWithResolvedState(
                        tipRecording,
                        vesselSnapshot,
                        string.Format(ic, "blocked chain tip '{0}'", tipId),
                        currentUT,
                        spawnLat,
                        spawnLon,
                        spawnAlt,
                        spawnVelocity,
                        tipPoint,
                        spawnBody,
                        orbitOverride);
                }
                else
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "blocked chain tip '{0}': propagated position and endpoint spawn state unavailable — falling back to validated snapshot respawn",
                            tipId));
                    spawnedPid = VesselSpawner.RespawnValidatedRecording(
                        tipRecording,
                        string.Format(ic, "blocked chain tip '{0}'", tipId),
                        preserveIdentity: true,
                        currentUT: currentUT,
                        allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                }
            }
            else
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "blocked chain tip '{0}': could not resolve spawn body for propagated state — falling back to validated snapshot respawn",
                        tipId));
                spawnedPid = VesselSpawner.RespawnValidatedRecording(
                    tipRecording,
                    string.Format(ic, "blocked chain tip '{0}'", tipId),
                    preserveIdentity: true,
                    currentUT: currentUT,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
            }

            if (spawnedPid == 0)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "TrySpawnBlockedChain: spawn path returned pid=0 for tip '{0}' — spawn failed",
                        tipId));
                return 0;
            }

            MarkChainTipRecordingSpawned(
                tipRecording,
                spawnedPid,
                string.Format(ic, "blocked chain tip '{0}'", tipId));

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Blocked chain tip spawn: pid={0} vessel={1} preserveIdentity=true — spawned after collision cleared",
                    spawnedPid, tipRecording.VesselName ?? "(unknown)"));

            chain.SpawnBlocked = false;
            CleanupGhostedVessel(chain.OriginalVesselPid);

            return spawnedPid;
        }

        // --- Private helpers ---

        internal static bool MarkChainTipRecordingSpawned(
            Recording tipRecording,
            uint spawnedPid,
            string logContext)
        {
            if (tipRecording == null || spawnedPid == 0)
                return false;

            uint priorPid = tipRecording.SpawnedVesselPersistentId;
            bool priorSpawned = tipRecording.VesselSpawned;
            tipRecording.SpawnedVesselPersistentId = spawnedPid;
            tipRecording.VesselSpawned = true;

            string context = string.IsNullOrEmpty(logContext)
                ? tipRecording.RecordingId ?? tipRecording.VesselName ?? "(unknown)"
                : logContext;
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "{0}: marked chain-tip recording spawned rec={1} vessel={2} pid={3} priorSpawned={4} priorPid={5}",
                    context,
                    tipRecording.RecordingId ?? "(unknown)",
                    tipRecording.VesselName ?? "(unknown)",
                    spawnedPid,
                    priorSpawned,
                    priorPid));
            return true;
        }

        private uint TryAdoptExistingSourceForChainTip(
            GhostChain chain,
            Recording tipRecording,
            string logContext,
            bool allowExistingSourceDuplicate)
        {
            if (!VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                tipRecording,
                Tag,
                logContext,
                allowExistingSourceDuplicate))
            {
                return 0;
            }

            if (chain != null)
            {
                chain.SpawnBlocked = false;
                CleanupGhostedVessel(chain.OriginalVesselPid);
            }

            uint adoptedPid = tipRecording.SpawnedVesselPersistentId;
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Chain tip adoption: pid={0} vessel={1} — existing source vessel reused",
                    adoptedPid,
                    tipRecording.VesselName ?? "(unknown)"));
            return adoptedPid;
        }

        private uint SpawnChainTipWithResolvedState(
            Recording tipRecording,
            ConfigNode vesselSnapshot,
            string logContext,
            double spawnUT,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            Vector3d spawnVelocity,
            TrajectoryPoint? rotationPoint,
            CelestialBody body,
            Orbit orbitOverride = null)
        {
            if (tipRecording == null || vesselSnapshot == null)
                return 0;

            ConfigNode spawnSnapshot = vesselSnapshot.CreateCopy();
            VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                spawnSnapshot,
                tipRecording,
                rotationPoint,
                spawnLat,
                spawnLon,
                spawnAlt,
                -1,
                logContext);

            if (TerrainCorrector.ShouldCorrectTerrain(
                    tipRecording.TerminalStateValue, tipRecording.TerrainHeightAtEnd))
            {
                ApplyTerrainCorrection(tipRecording, spawnSnapshot);
            }

            if (VesselSpawner.ShouldRouteThroughSpawnAtPosition(tipRecording))
            {
                if (body != null)
                {
                    Quaternion? surfaceRelativeRotationArg = null;
                    if (VesselSpawner.TryGetPreferredSpawnRotationFrame(
                        tipRecording, rotationPoint,
                        out _,
                        out Quaternion preferredSurfaceRelativeRotation,
                        out _))
                    {
                        surfaceRelativeRotationArg = preferredSurfaceRelativeRotation;
                    }

                    uint spawnedPid = VesselSpawner.SpawnAtPosition(
                        spawnSnapshot,
                        body,
                        spawnLat,
                        spawnLon,
                        spawnAlt,
                        spawnVelocity,
                        spawnUT,
                        preserveIdentity: true,
                        terminalState: tipRecording.TerminalStateValue,
                        surfaceRelativeRotation: surfaceRelativeRotationArg,
                        orbitOverride: orbitOverride);
                    if (spawnedPid != 0)
                        return spawnedPid;

                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "{0}: SpawnAtPosition returned pid=0 — falling back to validated snapshot respawn",
                            logContext));
                }
                else
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "{0}: spawn body unavailable — falling back to validated snapshot respawn",
                            logContext));
                }
            }

            ConfigNode validatedSpawnSnapshot = VesselSpawner.BuildValidatedRespawnSnapshot(
                spawnSnapshot,
                tipRecording,
                spawnUT,
                logContext,
                out string materializationRejectionReason);
            if (validatedSpawnSnapshot == null)
            {
                if (!string.IsNullOrEmpty(materializationRejectionReason))
                    VesselSpawner.AbandonSpawnForInvalidMaterialization(
                        tipRecording,
                        logContext,
                        materializationRejectionReason);
                return 0;
            }

            // The snapshot has already been route-specifically overridden and validated above,
            // so the fallback only needs raw materialization here rather than another wrapper.
            return VesselSpawner.RespawnVessel(validatedSpawnSnapshot, preserveIdentity: true);
        }

        /// <summary>
        /// Resolves the world-space spawn position for a chain tip spawn.
        /// Prefers snapshot lat/lon/alt (where RespawnVessel actually places the vessel),
        /// falls back to trajectory endpoint or terminal position.
        /// </summary>
        private static Vector3d ResolveSpawnPosition(Recording tipRecording, ConfigNode vesselSnapshot)
        {
            double snapLat = 0, snapLon = 0, snapAlt = 0;
            bool hasSnapPos = VesselSpawner.TryGetSnapshotDouble(vesselSnapshot, "lat", out snapLat)
                           && VesselSpawner.TryGetSnapshotDouble(vesselSnapshot, "lon", out snapLon)
                           && VesselSpawner.TryGetSnapshotDouble(vesselSnapshot, "alt", out snapAlt);
            bool hasEndpointCoords = RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                tipRecording,
                out string endpointBodyName,
                out double endpointLat,
                out double endpointLon,
                out double endpointAlt);

            if (hasSnapPos)
            {
                if (hasEndpointCoords
                    && VesselSpawner.TryGetSnapshotReferenceBodyName(vesselSnapshot, out string snapshotBodyName)
                    && string.Equals(snapshotBodyName, endpointBodyName, StringComparison.Ordinal))
                {
                    CelestialBody snapBody = FlightGlobals.GetBodyByName(endpointBodyName);
                    if (snapBody != null)
                    {
                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "ResolveSpawnPosition: using snapshot pos lat={0} lon={1} alt={2} body={3}",
                                snapLat.ToString("F4", ic), snapLon.ToString("F4", ic),
                                snapAlt.ToString("F1", ic), endpointBodyName));
                        return snapBody.GetWorldSurfacePosition(snapLat, snapLon, snapAlt);
                    }
                }

                if (!hasEndpointCoords)
                {
                    if (VesselSpawner.TryGetSnapshotReferenceBodyName(vesselSnapshot, out string snapshotFallbackBodyName))
                    {
                        CelestialBody snapBody = FlightGlobals.GetBodyByName(snapshotFallbackBodyName);
                        if (snapBody != null)
                        {
                            ParsekLog.Verbose(Tag,
                                string.Format(ic,
                                    "ResolveSpawnPosition: using snapshot pos lat={0} lon={1} alt={2} body={3}",
                                    snapLat.ToString("F4", ic), snapLon.ToString("F4", ic),
                                    snapAlt.ToString("F1", ic), snapshotFallbackBodyName));
                            return snapBody.GetWorldSurfacePosition(snapLat, snapLon, snapAlt);
                        }
                    }
                }

                ParsekLog.Verbose(Tag,
                    "ResolveSpawnPosition: snapshot coordinates lack authoritative body provenance — " +
                    "falling back to recording endpoint");
                return hasEndpointCoords
                    ? FlightGlobals.GetBodyByName(endpointBodyName)?.GetWorldSurfacePosition(endpointLat, endpointLon, endpointAlt)
                        ?? ComputeSpawnWorldPosition(tipRecording)
                    : ComputeSpawnWorldPosition(tipRecording);
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "ResolveSpawnPosition: no snapshot lat/lon/alt — falling back to ComputeSpawnWorldPosition");
                return ComputeSpawnWorldPosition(tipRecording);
            }
        }

        /// <summary>
        /// Attempts a trajectory walkback spawn: scans backward along the recorded trajectory
        /// to find a position free of collision. Returns the spawned vessel PID, or 0 if
        /// walkback found no valid position or spawn failed.
        /// </summary>
        private uint TryWalkbackSpawn(
            GhostChain chain, Recording tipRecording,
            ConfigNode vesselSnapshot,
            Bounds spawnBounds,
            string blockerName,
            bool allowExistingSourceDuplicate)
        {
            TrajectoryPoint? lastPoint = tipRecording.Points != null && tipRecording.Points.Count > 0
                ? (TrajectoryPoint?)tipRecording.Points[tipRecording.Points.Count - 1]
                : null;
            CelestialBody walkbackBody = VesselSpawner.ResolveSpawnRotationBody(tipRecording, lastPoint);
            CelestialBody walkbackSpawnBody = walkbackBody;
            TrajectoryPoint walkPt;

            if (walkbackBody != null)
            {
                var walkResult = SpawnCollisionDetector.WalkbackAlongTrajectorySubdividedDetailed(
                    tipRecording.Points,
                    walkbackBody.Radius,
                    SpawnCollisionDetector.DefaultWalkbackStepMeters,
                    (lat, lon, alt) => walkbackBody.GetWorldSurfacePosition(lat, lon, alt),
                    pos =>
                    {
                        var (ov, _, _, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                            pos, spawnBounds, SpawnCollisionPadding);
                        return ov;
                    });

                if (!walkResult.found)
                {
                    chain.WalkbackExhausted = true;
                    ParsekLog.Warn("SpawnCollision",
                        string.Format(ic,
                            "Trajectory walkback EXHAUSTED: vessel={0} — entire trajectory overlaps with {1}. " +
                            "Manual placement required. Walkback will not re-scan.",
                            tipRecording.VesselName ?? "(unknown)", blockerName ?? "(unknown)"));
                    return 0;
                }

                walkPt = walkResult.point;
                ParsekLog.Info("SpawnCollision",
                    string.Format(ic,
                        "Trajectory walkback: vessel={0} — found clear sub-step at UT={1} lat={2} lon={3} alt={4}",
                        tipRecording.VesselName ?? "(unknown)",
                        walkPt.ut.ToString("F1", ic),
                        walkPt.latitude.ToString("F6", ic),
                        walkPt.longitude.ToString("F6", ic),
                        walkPt.altitude.ToString("F1", ic)));
            }
            else
            {
                int validIdx = SpawnCollisionDetector.WalkbackAlongTrajectory(
                    tipRecording.Points,
                    spawnBounds,
                    SpawnCollisionPadding,
                    pt =>
                    {
                        string bodyName = pt.bodyName;
                        if (string.IsNullOrEmpty(bodyName)) bodyName = "Kerbin";
                        CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
                        return body != null
                            ? body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude)
                            : Vector3d.zero;
                    },
                    pos =>
                    {
                        var (ov, _, _, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                            pos, spawnBounds, SpawnCollisionPadding);
                        return ov;
                    });

                if (validIdx < 0)
                {
                    chain.WalkbackExhausted = true;
                    ParsekLog.Warn("SpawnCollision",
                        string.Format(ic,
                            "Trajectory walkback EXHAUSTED: vessel={0} — entire trajectory overlaps with {1}. " +
                            "Manual placement required. Walkback will not re-scan.",
                            tipRecording.VesselName ?? "(unknown)", blockerName ?? "(unknown)"));
                    return 0;
                }

                walkPt = tipRecording.Points[validIdx];
                ParsekLog.Warn("SpawnCollision",
                    string.Format(ic,
                        "Trajectory walkback: vessel={0} — body resolution unavailable, falling back to point walkback at UT={1}",
                        tipRecording.VesselName ?? "(unknown)",
                        walkPt.ut.ToString("F1", ic)));
            }

            Vector3d spawnVelocity = new Vector3d(
                walkPt.velocity.x,
                walkPt.velocity.y,
                walkPt.velocity.z);
            if (walkbackSpawnBody == null)
                walkbackSpawnBody = VesselSpawner.ResolveSpawnRotationBody(tipRecording, walkPt);
            uint walkbackPid = SpawnChainTipWithResolvedState(
                tipRecording,
                vesselSnapshot,
                string.Format(ic, "walkback chain tip '{0}'", tipRecording.RecordingId ?? tipRecording.VesselName),
                Planetarium.GetUniversalTime(),
                walkPt.latitude,
                walkPt.longitude,
                walkPt.altitude,
                spawnVelocity,
                walkPt,
                walkbackSpawnBody);
            if (walkbackPid != 0)
            {
                MarkChainTipRecordingSpawned(
                    tipRecording,
                    walkbackPid,
                    string.Format(ic, "walkback chain tip '{0}'",
                        tipRecording.RecordingId ?? tipRecording.VesselName));
                chain.SpawnBlocked = false;
                CleanupGhostedVessel(chain.OriginalVesselPid);
                return walkbackPid;
            }

            return 0;
        }

        /// <summary>
        /// Find the tip recording by ID from the Effective Recording Set.
        /// [Phase 3] ERS-routed: chain-tip lookups ignore NotCommitted /
        /// superseded / session-suppressed recordings, so a stale chain
        /// reference to a hidden tip returns null instead of resurrecting it.
        /// </summary>
        private static Recording FindTipRecording(string tipId)
        {
            var effective = EffectiveState.ComputeERS();
            for (int i = 0; i < effective.Count; i++)
            {
                if (effective[i].RecordingId == tipId)
                    return effective[i];
            }
            return null;
        }

        /// <summary>
        /// Compute world-space spawn position from a recording's endpoint.
        /// Uses terminal position, last trajectory point, or terminal orbit.
        /// </summary>
        private static Vector3d ComputeSpawnWorldPosition(Recording rec)
        {
            if (rec == null)
                return Vector3d.zero;

            if (RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out string bodyName, out double latitude, out double longitude, out double altitude))
            {
                CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
                if (body != null)
                {
                    Vector3d pos = body.GetWorldSurfacePosition(latitude, longitude, altitude);
                    ParsekLog.Verbose(Tag,
                        string.Format(ic, "ComputeSpawnWorldPosition: from recording endpoint " +
                            "lat={0} lon={1} alt={2}",
                            latitude.ToString("F4", ic),
                            longitude.ToString("F4", ic),
                            altitude.ToString("F1", ic)));
                    return pos;
                }
            }

            ParsekLog.Verbose(Tag, "ComputeSpawnWorldPosition: no position data — returning zero");
            return Vector3d.zero;
        }

        /// <summary>
        /// Compute propagated world position using GhostExtender strategy.
        /// For orbital recordings, propagates using Keplerian elements.
        /// For surface recordings, returns terminal surface position.
        /// Falls back to last recorded position.
        /// </summary>
        private static Vector3d ComputePropagatedPosition(Recording rec, double currentUT)
        {
            if (rec == null)
                return Vector3d.zero;

            GhostExtensionStrategy strategy = GhostExtender.ChooseStrategy(rec);

            switch (strategy)
            {
                case GhostExtensionStrategy.Orbital:
                {
                    if (!TryResolvePropagatedOrbitSeed(
                        rec,
                        out double inclination,
                        out double eccentricity,
                        out double semiMajorAxis,
                        out double lan,
                        out double argumentOfPeriapsis,
                        out double meanAnomalyAtEpoch,
                        out double epoch,
                        out string orbitBodyName))
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "ComputePropagatedPosition: no endpoint-aligned orbit seed for '{0}'",
                                rec.VesselName ?? rec.RecordingId ?? "(unknown)"));
                        return ComputeSpawnWorldPosition(rec);
                    }

                    CelestialBody body = FlightGlobals.GetBodyByName(orbitBodyName);
                    if (body == null)
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic, "ComputePropagatedPosition: body '{0}' not found",
                                orbitBodyName));
                        return ComputeSpawnWorldPosition(rec);
                    }

                    var (lat, lon, alt) = GhostExtender.PropagateOrbital(
                        inclination,
                        eccentricity,
                        semiMajorAxis,
                        lan,
                        argumentOfPeriapsis,
                        meanAnomalyAtEpoch,
                        epoch,
                        body.Radius, body.gravParameter,
                        currentUT);

                    Vector3d pos = body.GetWorldSurfacePosition(lat, lon, alt);
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "ComputePropagatedPosition: orbital propagation lat={0} lon={1} alt={2}",
                            lat.ToString("F4", ic), lon.ToString("F4", ic), alt.ToString("F0", ic)));
                    return pos;
                }

                case GhostExtensionStrategy.Surface:
                {
                    var (lat, lon, alt) = GhostExtender.PropagateSurface(rec);
                    string bodyName = null;
                    if (!RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName)
                        && rec.TerminalPosition.HasValue)
                    {
                        bodyName = rec.TerminalPosition.Value.body;
                    }
                    CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
                    if (body == null)
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic, "ComputePropagatedPosition: body '{0}' not found", bodyName));
                        return Vector3d.zero;
                    }
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "ComputePropagatedPosition: surface hold lat={0} lon={1} alt={2}",
                            lat, lon, alt));
                    return body.GetWorldSurfacePosition(lat, lon, alt);
                }

                case GhostExtensionStrategy.LastRecordedPosition:
                {
                    return ComputeSpawnWorldPosition(rec);
                }

                default:
                    return ComputeSpawnWorldPosition(rec);
            }
        }

        internal static bool TryResolvePropagatedOrbitSeed(
            Recording rec,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            return RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName);
        }

        /// <summary>
        /// Applies underground safety floor against current PQS terrain at the
        /// recording's terminal position. Only clamps UP — if the recorded
        /// altitude is below (current PQS terrain + 0.5 m), push up to the floor.
        /// Never down-clamps — the recorded altitude is the truth, including
        /// mesh-object offsets (Island Airfield, launchpad, KSC buildings) that
        /// PQS terrain alone cannot represent.
        /// </summary>
        private static void ApplyTerrainCorrection(Recording rec, ConfigNode vesselSnapshot)
        {
            if (rec.TerminalPosition == null) return;
            var tp = rec.TerminalPosition.Value;

            CelestialBody body = FlightGlobals.GetBodyByName(tp.body);
            if (body == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "ApplyTerrainCorrection: body '{0}' not found — skipping", tp.body));
                return;
            }

            double pqsTerrain = body.TerrainAltitude(tp.latitude, tp.longitude);
            // Use the shared underground safety floor (2 m) so the snapshot-alt
            // path matches what ClampAltitudeForLanded would apply downstream.
            // Review finding: the previous hard-coded 0.5 m value was consistent
            // only with ApplyLandedGhostClearance (for kinematic ghosts), not
            // with the spawn path this code serves.
            double safetyFloor = pqsTerrain + VesselSpawner.UndergroundSafetyFloorMeters;

            if (tp.altitude >= safetyFloor)
                return; // trust recorded altitude

            string altStr = vesselSnapshot.GetValue("alt");
            if (!string.IsNullOrEmpty(altStr))
            {
                vesselSnapshot.SetValue("alt", safetyFloor.ToString("R", ic));
                ParsekLog.Info("TerrainCorrect",
                    string.Format(ic,
                        "Applied underground safety floor: recorded={0} pqsTerrain={1} -> {2} (delta=+{3})",
                        tp.altitude.ToString("F1", ic),
                        pqsTerrain.ToString("F1", ic),
                        safetyFloor.ToString("F1", ic),
                        (safetyFloor - tp.altitude).ToString("F1", ic)));
            }
        }

        /// <summary>
        /// Destroy ghost GO and remove tracking entry for a ghosted vessel.
        /// </summary>
        private void CleanupGhostedVessel(uint originalPid)
        {
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(originalPid, out info))
            {
                if (info.ghostGO != null)
                {
                    UnityEngine.Object.Destroy(info.ghostGO);
                    ParsekLog.Verbose(Tag,
                        string.Format(ic, "Destroyed ghost GO for pid={0}", originalPid));
                }
                ghostedVessels.Remove(originalPid);
            }
        }

        /// <summary>
        /// Destroy all ghost GOs and clear state. Called on scene exit.
        /// </summary>
        internal void CleanupAll()
        {
            int count = ghostedVessels.Count;
            foreach (var kvp in ghostedVessels)
            {
                if (kvp.Value.ghostGO != null)
                {
                    UnityEngine.Object.Destroy(kvp.Value.ghostGO);
                }
            }
            ghostedVessels.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic, "CleanupAll: cleared {0} ghosted vessel(s)", count));
        }

        /// <summary>
        /// Number of currently ghosted vessels.
        /// </summary>
        internal int GhostedCount => ghostedVessels.Count;

        /// <summary>
        /// Reset for testing — clears all state without destroying GOs.
        /// </summary>
        internal void ResetForTesting()
        {
            ghostedVessels.Clear();
        }

        /// <summary>
        /// Add a ghosted vessel entry directly for test seeding.
        /// </summary>
        internal void AddGhostedForTesting(uint pid, string name, ConfigNode snapshot)
        {
            ghostedVessels[pid] = new GhostedVesselInfo
            {
                vesselPid = pid,
                vesselName = name,
                snapshot = snapshot
            };
        }

        // --- Pure decision methods (static, testable) ---

        /// <summary>
        /// Should we attempt to ghost this vessel?
        /// Guards: pid=0 → false, vessel missing → false.
        /// </summary>
        internal static bool ShouldAttemptGhosting(uint vesselPid, bool vesselExists)
        {
            return vesselPid != 0 && vesselExists;
        }

        /// <summary>
        /// Can we spawn at this chain's tip?
        /// Guards: null chain, terminated chain, null/empty tip recording ID → false.
        /// </summary>
        internal static bool CanSpawnAtChainTip(GhostChain chain)
        {
            return chain != null
                && !chain.IsTerminated
                && !string.IsNullOrEmpty(chain.TipRecordingId);
        }
    }
}
