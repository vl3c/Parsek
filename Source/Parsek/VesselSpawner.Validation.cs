using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Materialization validation: validated respawn snapshots, rejection logging and abandonment, and the degraded-fallback snapshot body-provenance repair.
    public static partial class VesselSpawner
    {
        internal static ConfigNode BuildValidatedRespawnSnapshot(Recording rec, double currentUT, string logContext)
        {
            return BuildValidatedRespawnSnapshot(
                rec,
                currentUT,
                logContext,
                out _);
        }

        internal static ConfigNode BuildValidatedRespawnSnapshot(
            Recording rec,
            double currentUT,
            string logContext,
            out string materializationRejectionReason)
        {
            string resolvedContext = DescribeSpawnValidationContext(rec, logContext);
            materializationRejectionReason = null;
            if (rec?.VesselSnapshot == null)
            {
                ParsekLog.Error("Spawner",
                    $"BuildValidatedRespawnSnapshot: missing VesselSnapshot for {resolvedContext}");
                return null;
            }

            ConfigNode snapshot = BuildValidatedRespawnSnapshot(
                rec.VesselSnapshot,
                rec,
                currentUT,
                logContext,
                out materializationRejectionReason);
            // This overload copies the stored snapshot, so it is where an end-of-recording spawn
            // copy (a chain tip's, or a route-fed leaf's) drops the route cargo a rewind refunded.
            // The prepared-snapshot overloads receive a copy their caller already adjusted.
            ChainTipRouteCargo.ApplyToSpawnCopy(snapshot, rec, resolvedContext);
            return snapshot;
        }

        internal static ConfigNode BuildValidatedRespawnSnapshot(
            ConfigNode sourceSnapshot,
            Recording rec,
            double currentUT,
            string logContext)
        {
            return BuildValidatedRespawnSnapshot(
                sourceSnapshot,
                rec,
                currentUT,
                logContext,
                out _);
        }

        internal static ConfigNode BuildValidatedRespawnSnapshot(
            ConfigNode sourceSnapshot,
            Recording rec,
            double currentUT,
            string logContext,
            out string materializationRejectionReason)
        {
            string resolvedContext = DescribeSpawnValidationContext(rec, logContext);
            materializationRejectionReason = null;
            if (sourceSnapshot == null)
            {
                ParsekLog.Error("Spawner",
                    $"BuildValidatedRespawnSnapshot: missing prepared snapshot for {resolvedContext}");
                return null;
            }

            ConfigNode snapshot = sourceSnapshot.CreateCopy();
            VesselSnapshotOps.CorrectUnsafeSnapshotSituation(snapshot, rec.TerminalStateValue);

            if (!VesselSnapshotOps.TryValidateSnapshotHasParts(snapshot, out materializationRejectionReason))
            {
                materializationRejectionReason = AddRecordingSnapshotMaterializationContext(
                    materializationRejectionReason,
                    rec);
                LogMaterializationSnapshotRejected(resolvedContext, materializationRejectionReason);
                return null;
            }

            if (!TryRepairSnapshotBodyProvenance(
                    snapshot,
                    rec,
                    currentUT,
                    resolvedContext,
                    out materializationRejectionReason))
            {
                LogMaterializationSnapshotRejected(resolvedContext, materializationRejectionReason);
                return null;
            }

            if (!VesselSnapshotOps.TryValidateFiniteMaterializationMetadata(snapshot, out materializationRejectionReason))
            {
                LogMaterializationSnapshotRejected(resolvedContext, materializationRejectionReason);
                return null;
            }

            // The stamp has served its purpose (the repair above consulted it);
            // strip it from the validated copy so it never rides a ProtoVessel
            // load into KSP save files.
            VesselSnapshotOps.StripDeliberatePositionOverrideStamp(snapshot, resolvedContext);

            return snapshot;
        }

        internal static void LogMaterializationSnapshotRejected(string logContext, string reason)
        {
            ParsekLog.Warn("Spawner",
                $"Spawn validation rejected materialization for {logContext}: {reason} - " +
                "keeping ghost-only/abandon instead of loading corrupt ProtoVessel (#620)");
        }

        internal static string AddRecordingSnapshotMaterializationContext(string reason, Recording rec)
        {
            if (rec == null)
                return reason;

            int vesselPartCount = VesselSnapshotOps.CountSnapshotParts(rec.VesselSnapshot);
            int ghostPartCount = VesselSnapshotOps.CountSnapshotParts(rec.GhostVisualSnapshot);
            string terminalState = rec.TerminalStateValue.HasValue
                ? rec.TerminalStateValue.Value.ToString()
                : "(unset)";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} (terminalState={1}, vesselSnapshotParts={2}, ghostSnapshotParts={3}, recordingId={4})",
                reason ?? "(unknown)",
                terminalState,
                vesselPartCount,
                ghostPartCount,
                string.IsNullOrEmpty(rec.RecordingId) ? "<none>" : rec.RecordingId);
        }

        internal static void AbandonSpawnForInvalidMaterialization(
            Recording rec,
            string logContext,
            string reason)
        {
            if (rec == null)
                return;

            rec.SpawnedVesselPersistentId = 0;
            rec.VesselSpawned = true;
            rec.SpawnAbandoned = true;
            SpawnWarpOutcomeWatch.NoteAbandonReason(
                rec, SpawnWarpOutcomeWatch.ReasonInvalidSnapshot);
            ParsekLog.Warn("Spawner",
                $"Spawn ABANDONED for {DescribeSpawnValidationContext(rec, logContext)}: {reason} - " +
                "recording remains ghost-only instead of retrying corrupt materialization (#620)");
        }

        /// <summary>
        /// Coordinate source chosen by the landed-like snapshot repair.
        /// Public (not internal) only because xUnit theory methods must be public
        /// and the exhaustive truth-table test takes it as a parameter.
        /// </summary>
        public enum SurfaceRepairCoordinateSource
        {
            /// <summary>No usable coordinates: reject the materialization.</summary>
            Reject = 0,
            /// <summary>Keep the snapshot's coordinates: they carry the deliberate
            /// position-override stamp (walkback / endpoint correction).</summary>
            StampedSnapshot,
            /// <summary>Rewrite the snapshot to the recording endpoint coordinates
            /// (the pre-stamp default; moves stale EVA-start snapshots to the endpoint).</summary>
            Endpoint,
            /// <summary>Keep the snapshot's coordinates: no endpoint coordinates exist
            /// and the snapshot's position/body are usable.</summary>
            Snapshot
        }

        /// <summary>
        /// Pure decision: which coordinate source the landed-like snapshot repair uses.
        /// A stamped snapshot with a finite position on a non-mismatched body keeps its
        /// deliberately-overridden coordinates (the walkback correction must survive the
        /// degraded fallback). Unstamped snapshots keep the historical endpoint-first
        /// contract EXACTLY (that path exists to move stale EVA-start snapshots to the
        /// trajectory endpoint). A stamp never blocks a genuine repair: with a missing or
        /// non-finite snapshot position the endpoint repair still applies.
        /// </summary>
        internal static SurfaceRepairCoordinateSource DecideSurfaceRepairCoordinateSource(
            bool hasDeliberateOverrideStamp,
            bool hasSnapshotPos,
            bool hasSnapshotBody,
            bool hasEndpointCoords,
            bool? bodyMismatch)
        {
            if (hasDeliberateOverrideStamp && hasSnapshotPos && bodyMismatch != true)
                return SurfaceRepairCoordinateSource.StampedSnapshot;
            if (hasEndpointCoords)
                return SurfaceRepairCoordinateSource.Endpoint;
            if (hasSnapshotPos && hasSnapshotBody && bodyMismatch != true)
                return SurfaceRepairCoordinateSource.Snapshot;
            return SurfaceRepairCoordinateSource.Reject;
        }

        internal static bool TryRepairSnapshotBodyProvenance(
            ConfigNode snapshot,
            Recording rec,
            double currentUT,
            string logContext,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (snapshot == null || rec == null)
            {
                rejectionReason = "snapshot or recording is null";
                return false;
            }

            bool hasSnapshotPos =
                VesselSnapshotOps.TryGetFiniteRequiredSnapshotDouble(snapshot, "lat", "surface", out _)
                && VesselSnapshotOps.TryGetFiniteRequiredSnapshotDouble(snapshot, "lon", "surface", out _)
                && VesselSnapshotOps.TryGetFiniteRequiredSnapshotDouble(snapshot, "alt", "surface", out _);
            bool hasSnapshotBody = LiveVesselSnapshot.TryGetSnapshotReferenceBodyName(snapshot, out string snapshotBodyName);
            bool hasEndpointCoords = RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec,
                out string endpointBodyNameFromCoords,
                out double endpointLat,
                out double endpointLon,
                out double endpointAlt);
            if (hasEndpointCoords
                && (!VesselSnapshotOps.IsFinite(endpointLat) || !VesselSnapshotOps.IsFinite(endpointLon) || !VesselSnapshotOps.IsFinite(endpointAlt)))
            {
                hasEndpointCoords = false;
            }
            string endpointBodyName = endpointBodyNameFromCoords;
            bool hasEndpointBody = hasEndpointCoords;
            if (!hasEndpointBody)
                hasEndpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out endpointBodyName);
            bool landedLike = rec.TerminalStateValue == TerminalState.Landed
                || rec.TerminalStateValue == TerminalState.Splashed
                || VesselSnapshotOps.IsLandedLikeSnapshot(snapshot);
            bool? bodyMismatch = hasEndpointBody && hasSnapshotBody
                ? (bool?)!string.Equals(snapshotBodyName, endpointBodyName, StringComparison.Ordinal)
                : null;
            bool hasFiniteOrbit = VesselSnapshotOps.TryValidateFiniteOrbitMetadata(snapshot, out _);
            bool needsSurfaceOrbitRepair = landedLike
                && !VesselSnapshotOps.HasCanonicalSurfaceOrbitSignature(snapshot);
            bool needsRepair = !hasSnapshotPos
                || !hasSnapshotBody
                || bodyMismatch == true
                || needsSurfaceOrbitRepair
                || !hasFiniteOrbit;

            if (!needsRepair)
                return true;

            string repairBodyName = hasEndpointBody ? endpointBodyName : snapshotBodyName;
            if (string.IsNullOrEmpty(repairBodyName))
            {
                rejectionReason =
                    $"snapshot provenance is malformed (hasPos={hasSnapshotPos}, hasBody={hasSnapshotBody}) and no repair body is available";
                ParsekLog.Error("Spawner",
                    $"Spawn validation failed for {logContext}: " +
                    rejectionReason);
                return false;
            }

            if (!LiveVesselSnapshot.TryResolveBodyByName(repairBodyName, out CelestialBody body))
            {
                rejectionReason =
                    $"repair body '{repairBodyName}' was resolved but is not loaded";
                ParsekLog.Error("Spawner",
                    $"Spawn validation failed for {logContext}: " +
                    rejectionReason);
                return false;
            }

            if (landedLike)
            {
                bool hasDeliberateOverrideStamp = VesselSnapshotOps.HasDeliberatePositionOverrideStamp(snapshot);
                SurfaceRepairCoordinateSource coordsSource = DecideSurfaceRepairCoordinateSource(
                    hasDeliberateOverrideStamp,
                    hasSnapshotPos,
                    hasSnapshotBody,
                    hasEndpointCoords,
                    bodyMismatch);

                if (coordsSource == SurfaceRepairCoordinateSource.Reject)
                {
                    rejectionReason =
                        "surface snapshot repair needs usable coordinates " +
                        $"(endpointCoords={hasEndpointCoords}, snapshotPos={hasSnapshotPos}, " +
                        $"snapshotBody={snapshotBodyName ?? "(none)"}, endpointBody={endpointBodyName ?? "(none)"}, " +
                        $"deliberateOverrideStamp={hasDeliberateOverrideStamp})";
                    ParsekLog.Error("Spawner",
                        $"Spawn validation failed for {logContext}: " +
                        rejectionReason);
                    return false;
                }

                if (coordsSource == SurfaceRepairCoordinateSource.Endpoint)
                {
                    VesselSnapshotOps.OverrideSnapshotPosition(
                        snapshot,
                        endpointLat,
                        endpointLon,
                        endpointAlt,
                        -1,
                        logContext ?? rec.VesselName);
                }

                LiveVesselSnapshot.ApplySurfaceOrbitToSnapshot(snapshot, body, logContext);
                string coordsSourceLabel =
                    coordsSource == SurfaceRepairCoordinateSource.StampedSnapshot ? "stamped-override"
                    : coordsSource == SurfaceRepairCoordinateSource.Endpoint ? "endpoint"
                    : "snapshot";
                ParsekLog.Warn("Spawner",
                    $"Spawn validation repaired snapshot for {logContext} " +
                    $"using {coordsSourceLabel} surface coordinates on body '{repairBodyName}' " +
                    $"(deliberateOverrideStamp={hasDeliberateOverrideStamp}, snapshotPos={hasSnapshotPos}, " +
                    $"endpointCoords={hasEndpointCoords}, " +
                    $"bodyMismatch={(bodyMismatch.HasValue ? bodyMismatch.Value.ToString() : "unknown")})");
                return true;
            }

            if (TryBuildRecordedTerminalOrbitForSpawn(rec, body, currentUT, out Orbit endpointOrbit))
            {
                LiveVesselSnapshot.ReplaceSnapshotOrbitNode(snapshot, endpointOrbit, body);

                Vector3d orbitPos = endpointOrbit.getPositionAtUT(currentUT);
                if (!hasEndpointCoords && VesselSnapshotOps.IsFinite(orbitPos))
                {
                    endpointLat = body.GetLatitude(orbitPos);
                    endpointLon = body.GetLongitude(orbitPos);
                    endpointAlt = body.GetAltitude(orbitPos);
                    hasEndpointCoords = true;
                }

                if (hasEndpointCoords)
                    VesselSnapshotOps.OverrideSnapshotPosition(snapshot, endpointLat, endpointLon, endpointAlt, -1, logContext ?? rec.VesselName);

                if (rec.TerminalStateValue == TerminalState.Orbiting
                    || rec.TerminalStateValue == TerminalState.Docked)
                {
                    VesselSnapshotOps.NormalizeOrbitalSpawnMetadata(snapshot, currentUT);
                }

                ParsekLog.Warn("Spawner",
                    $"Spawn validation repaired snapshot for {logContext} " +
                    $"using endpoint-aligned orbit data on body '{endpointBodyName}'");
                return true;
            }

            if (hasEndpointCoords)
            {
                VesselSnapshotOps.OverrideSnapshotPosition(snapshot, endpointLat, endpointLon, endpointAlt, -1, logContext ?? rec.VesselName);

                if (TryResolveEndpointStateVector(rec, endpointBodyName, out TrajectoryPoint endpointPoint))
                {
                    // The orbit is seeded at currentUT from the endpoint rebuilt in this
                    // session's axes, so the recorded vector's frame and yaw are resolved
                    // into those same axes first.
                    Vector3d velocity = ResolveRecordedSpawnVelocity(
                        rec,
                        endpointPoint,
                        body,
                        endpointLat,
                        endpointLon,
                        endpointAlt,
                        "endpoint-state-vector-repair",
                        logContext);
                    Orbit orbit = new Orbit();
                    OrbitReseed.FromLatLonAltAndRecordedVelocity(
                        orbit,
                        body,
                        endpointLat,
                        endpointLon,
                        endpointAlt,
                        velocity,
                        currentUT);
                    LiveVesselSnapshot.ReplaceSnapshotOrbitNode(snapshot, orbit, body);
                    ParsekLog.Warn("Spawner",
                        $"Spawn validation repaired snapshot for {logContext} " +
                        $"using endpoint state vectors on body '{endpointBodyName}'");
                    return true;
                }
            }

            rejectionReason =
                "snapshot provenance is malformed and endpoint data cannot reconstruct a usable orbit/body " +
                $"(endpointBody={endpointBodyName}, endpointCoords={hasEndpointCoords}, landedLike={landedLike})";
            ParsekLog.Error("Spawner",
                $"Spawn validation failed for {logContext}: " +
                rejectionReason);
            return false;
        }

        internal static string DescribeSpawnValidationContext(Recording rec, string logContext)
        {
            string vesselName = rec != null ? rec.VesselName : null;
            if (string.IsNullOrEmpty(logContext))
                return string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName;
            if (string.IsNullOrEmpty(vesselName) || string.Equals(logContext, vesselName, StringComparison.Ordinal))
                return logContext;
            return $"{logContext} ({vesselName})";
        }
    }
}
