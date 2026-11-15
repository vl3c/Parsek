using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The recorder's live capture entry points: backing up a live vessel into a snapshot
    /// ConfigNode (<see cref="TryBackupSnapshot"/>, with the crew-name reverse map and the
    /// terminal-state normalization) and stamping a pending recording at stop
    /// (<see cref="SnapshotVessel"/>). They live on the recorder, not on
    /// <see cref="LiveVesselSnapshot"/>, because the crew-name reverse map reads
    /// <c>KerbalsModule</c> / <c>CrewReservationManager</c> and the terminal state reads
    /// <c>RecordingTree</c>, all type-knot members: on <see cref="LiveVesselSnapshot"/> those
    /// calls put that type in the knot.
    /// </summary>
    public partial class FlightRecorder
    {
        public static ConfigNode TryBackupSnapshot(Vessel vessel)
        {
            if (vessel == null) return null;
            try
            {
                // BackupVessel() sets vessel.isBackingUp = true internally (confirmed
                // via decompilation) — PartModules see the flag during serialization. (#236)
                ProtoVessel pv = vessel.BackupVessel();
                if (pv == null) return null;
                ConfigNode node = new ConfigNode("VESSEL");
                pv.Save(node);
                NormalizeBackedUpSnapshotFromLiveVessel(node, vessel);
                // A fairing truss kept on after its stage separated is not derivable from
                // the attach rows; record what the craft actually shows.
                GhostFairingStructureStamp.StampFromLiveVessel(vessel, node);
                // Crew inventories live on the roster, not in PART nodes: capture them with
                // the snapshot so a spawn from it restores what the crew carried at this
                // moment (CrewInventorySnapshot). Keyed by live names; the reverse map below
                // renames the keys together with the PART crew values.
                CrewInventorySnapshot.CaptureFromLiveVessel(vessel, node);
                // Live PART/crew= reflects whichever kerbal is physically seated, which
                // after a SwapReservedCrewInFlight pass is a stand-in. Persisting the
                // stand-in name would cause EnsureCrewExistInRoster to fabricate a new
                // kerbal with that name at spawn time instead of restoring the original.
                KerbalsModule.ReverseMapCrewNamesInSnapshot(
                    node,
                    CrewReservationManager.CrewReplacements,
                    $"TryBackupSnapshot pid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
                return node;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to backup vessel snapshot: {ex.Message}");
                return null;
            }
        }

        private static void NormalizeBackedUpSnapshotFromLiveVessel(ConfigNode snapshot, Vessel vessel)
        {
            if (snapshot == null || vessel == null)
                return;

            LiveVesselSnapshot.ResolveLocalizedVesselNameInSnapshot(snapshot);

            TerminalState liveState = ResolveLiveSnapshotTerminalState(vessel);
            CelestialBody surfaceBody = vessel.situation == Vessel.Situations.LANDED
                || vessel.situation == Vessel.Situations.SPLASHED
                ? vessel.mainBody
                : null;
            LiveVesselSnapshot.NormalizeStableSnapshotForPersistence(
                snapshot,
                liveState,
                surfaceBody,
                $"TryBackupSnapshot pid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
        }

        private static TerminalState ResolveLiveSnapshotTerminalState(Vessel vessel)
        {
            if (vessel == null)
                return TerminalState.SubOrbital;

            return RecordingTree.DetermineTerminalState((int)vessel.situation, vessel);
        }

        public static void SnapshotVessel(
            Recording pending,
            bool vesselDestroyed,
            Vessel vesselOverride = null,
            ConfigNode destroyedFallbackSnapshot = null)
        {
            if (pending == null || pending.Points.Count == 0)
            {
                ParsekLog.Warn("Spawner", "SnapshotVessel skipped: pending recording is null or has no points");
                return;
            }

            // Compute distance from launch. A recording carrying a Relative TrackSection
            // cannot use the flat Points list: its Relative frames hold anchor-local
            // Cartesian metres in latitude/longitude/altitude, so it measures over the
            // sections' body-fixed surfaces instead (see ClassifySnapshotDistanceRoute).
            bool bodyFixedRoute =
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(pending) == LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections;
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = null;
            if (!bodyFixedRoute)
            {
                bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);
                if (bodyFirst == null)
                    ParsekLog.Warn("Spawner", $"SnapshotVessel: body '{firstPoint.bodyName}' not found — distance computation will be skipped");
            }

            if (vesselDestroyed)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = destroyedFallbackSnapshot != null
                    ? destroyedFallbackSnapshot.CreateCopy()
                    : null;
                if (bodyFixedRoute)
                {
                    // End position is the latest body-fixed sample (vessel already gone).
                    LiveVesselSnapshot.ApplySnapshotDistancesFromBodyFixedSurfaces(pending, null, "destroyed");
                }
                else
                {
                    // Use last recorded point for distance (may be a different SOI)
                    var lastPoint = pending.Points[pending.Points.Count - 1];
                    CelestialBody bodyLast = FlightGlobals.Bodies?.Find(b => b.name == lastPoint.bodyName);
                    if (bodyFirst != null && bodyLast != null)
                    {
                        Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                            firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                        Vector3d lastPos = bodyLast.GetWorldSurfacePosition(
                            lastPoint.latitude, lastPoint.longitude, lastPoint.altitude);
                        pending.DistanceFromLaunch = Vector3d.Distance(launchPos, lastPos);
                    }

                    // Compute max distance from launch across all recorded points
                    LiveVesselSnapshot.ComputeMaxDistance(pending, bodyFirst, firstPoint);
                }

                pending.VesselSituation = pending.VesselSnapshot != null ? "Destroyed (snapshot kept)" : "Destroyed";

                // End biome from the last body-fixed position (vessel already destroyed).
                // The flat tail may be a Relative frame, whose lat/lon are anchor-local metres.
                TrajectoryPoint endBiomePoint;
                if (LiveVesselSnapshot.TryGetEndBiomeSamplePoint(pending, bodyFixedRoute, out endBiomePoint))
                    pending.EndBiome = LiveVesselSnapshot.TryResolveBiome(endBiomePoint.bodyName, endBiomePoint.latitude, endBiomePoint.longitude);

                ParsekLog.Info("Spawner", $"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Snapshot kept: {pending.VesselSnapshot != null}");
                return;
            }

            Vessel vessel = vesselOverride ?? FlightGlobals.ActiveVessel;
            ParsekLog.Verbose("Spawner",
                $"SnapshotVessel: using {(vesselOverride != null ? "override" : "active")} vessel, " +
                $"vesselDestroyed={vesselDestroyed}, points={pending.Points.Count}");
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (no active vessel)";
                ParsekLog.Info("Spawner", "No active vessel at snapshot time");
                return;
            }

            // Compute distance from launch position
            if (bodyFixedRoute)
            {
                LiveVesselSnapshot.ApplySnapshotDistancesFromBodyFixedSurfaces(pending, vessel.GetWorldPos3D(), "live");
            }
            else if (bodyFirst != null)
            {
                Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                    firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                Vector3d currentPos = vessel.GetWorldPos3D();
                pending.DistanceFromLaunch = Vector3d.Distance(launchPos, currentPos);

                LiveVesselSnapshot.ComputeMaxDistance(pending, bodyFirst, firstPoint);
            }

            // Snapshot the vessel (works for regular vessels and EVA kerbals)
            if (!vessel.loaded)
                ParsekLog.Warn("Spawner", "Active vessel is unloaded at snapshot time — snapshot may be incomplete");
            ConfigNode node = TryBackupSnapshot(vessel);
            if (node == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (snapshot failed)";
                ParsekLog.Error("Spawner", "Failed to backup active vessel at snapshot time");
                return;
            }
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string (humanized, not raw enum)
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{LiveVesselSnapshot.HumanizeSituation(vessel.situation)} {vessel.mainBody.name}";

            // Capture end biome (Phase 10)
            pending.EndBiome = LiveVesselSnapshot.TryResolveBiome(vessel.mainBody?.name, vessel.latitude, vessel.longitude);

            ParsekLog.Info("Spawner", $"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}, " +
                $"EndBiome: {pending.EndBiome ?? "(null)"}");
        }
    }
}
