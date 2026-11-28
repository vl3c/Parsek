using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live helpers for an in-game cell that puts a real stock vessel and its Parsek ghost
    /// side by side in orbit beside the active vessel: author the vessel's PART nodes, spawn
    /// it co-orbital, build a ghost from the live vessel's own snapshot through the engine's
    /// build-and-initialise seam, and keep that ghost out of physics. Used by
    /// <see cref="FairingGhostVisualInGameTests"/> and the part parity sweep
    /// (design-part-ghost-parity section 11). Camera, framing and placement stay with the
    /// caller; every log line goes under the caller's tag.
    /// </summary>
    internal static class GhostCoOrbitalRig
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The live vessel's snapshot, the throwaway recording and the built ghost.</summary>
        internal sealed class LiveGhost
        {
            public ConfigNode Snapshot;
            public Recording Recording;
            public GhostBuildResult Build;
            public GhostPlaybackState State;
        }

        /// <summary>
        /// A PART node for <paramref name="info"/> with every prefab module present IN PREFAB
        /// ORDER (so stock's index-based module load never falls back to its name search),
        /// each carrying only its name unless <paramref name="customize"/> adds values. A
        /// variant name is written twice, the way stock saves one: <c>moduleVariantName</c> on
        /// the part and <c>selectedVariant</c> on its <c>ModulePartVariants</c> module (before
        /// <paramref name="customize"/> runs).
        /// </summary>
        internal static ConfigNode AuthorPartNode(
            AvailablePart info, uint flightId, int parentIndex, Vector3 position,
            Dictionary<string, int> attachments, string variantName, Action<ConfigNode, string> customize)
        {
            ConfigNode p = ProtoVessel.CreatePartNode(info.name, flightId);
            p.SetValue("parent", parentIndex.ToString(IC), true);
            p.SetValue("position", KSPUtil.WriteVector(position), true);
            p.RemoveValues("attN");
            Part prefab = info.partPrefab;
            if (prefab.attachNodes != null)
            {
                for (int i = 0; i < prefab.attachNodes.Count; i++)
                {
                    string id = prefab.attachNodes[i].id;
                    int idx;
                    if (attachments == null || !attachments.TryGetValue(id, out idx))
                        idx = -1;
                    p.AddValue("attN", id + ", " + idx.ToString(IC));
                }
            }
            bool hasVariant = !string.IsNullOrEmpty(variantName);
            if (hasVariant)
                p.SetValue("moduleVariantName", variantName, true);
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m == null) continue;
                ConfigNode mn = p.AddNode("MODULE");
                mn.AddValue("name", m.moduleName);
                mn.AddValue("isEnabled", "True");
                if (hasVariant && m.moduleName == "ModulePartVariants")
                    mn.AddValue("selectedVariant", variantName);
                customize?.Invoke(mn, m.moduleName);
            }
            return p;
        }

        /// <summary><paramref name="count"/> distinct flight ids unique in the current game.</summary>
        internal static List<uint> UniqueFlightIds(int count)
        {
            var ids = new List<uint>();
            while (ids.Count < count)
            {
                uint id = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Spawns the authored vessel on a clone of the anchor's orbit with the mean anomaly
        /// advanced by the along-track offset, so it rides beside the anchor with near-zero
        /// relative drift for the test's duration (the GrappleCapture co-orbital pattern).
        /// Returns the spawned pid, 0 when the anchor orbit is degenerate or the spawn failed.
        /// </summary>
        internal static uint SpawnCoOrbital(
            ConfigNode vesselNode, Vessel anchor, double alongTrackMeters, string label, string logTag)
            => SpawnCoOrbital(vesselNode, anchor, alongTrackMeters, label, logTag, null);

        /// <summary>
        /// As above, spawned with a world rotation (design-part-ghost-parity.md E.2: an FX case's
        /// exhaust axis along the orbit normal, so no plume points at a neighbour slot). The
        /// rotation is passed on as the surface-relative one stock's ProtoVessel reads.
        /// </summary>
        internal static uint SpawnCoOrbital(
            ConfigNode vesselNode, Vessel anchor, double alongTrackMeters, string label, string logTag,
            Quaternion? worldRotation)
        {
            Orbit src = anchor.orbit;
            double ut = Planetarium.GetUniversalTime();
            if (!TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift(
                    alongTrackMeters, anchor.obt_speed, src.semiMajorAxis,
                    src.referenceBody.gravParameter, out double dM))
            {
                ParsekLog.Info(logTag, $"spawn {label}: degenerate anchor orbit, no co-orbital shift (pid=0)");
                return 0;
            }
            var orbit = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch + dM, src.epoch, src.referenceBody);
            orbit.Init();
            orbit.UpdateFromUT(ut);
            uint pid = VesselSpawner.SpawnAtPosition(
                vesselNode, src.referenceBody, anchor.latitude, anchor.longitude, anchor.altitude,
                anchor.obt_velocity, ut, terminalState: TerminalState.Orbiting,
                surfaceRelativeRotation: worldRotation.HasValue && src.referenceBody.bodyTransform != null
                    ? (Quaternion?)(Quaternion.Inverse(src.referenceBody.bodyTransform.rotation) * worldRotation.Value) : null,
                orbitOverride: orbit);
            ParsekLog.Info(logTag, $"spawn {label}: pid={pid.ToString(IC)} alongTrack={alongTrackMeters.ToString("F0", IC)}m " +
                $"dM={dM.ToString("R", IC)}rad body={src.referenceBody.name}");
            return pid;
        }

        /// <summary>
        /// Spawns the authored vessel in the air at (<paramref name="lat"/>, <paramref name="lon"/>,
        /// <paramref name="altitudeAsl"/>) over <paramref name="body"/> (the part parity air context,
        /// design-part-ghost-parity.md D.4): upright (its +Y along the local up, +Z north), moving
        /// straight down at <paramref name="descentMps"/> relative to the surface. SpawnAtPosition
        /// reads an inertial velocity, so the body's rotating-frame velocity at the spawn point is
        /// added to the surface velocity. Returns the spawned pid, 0 when the spawn failed.
        /// </summary>
        internal static uint SpawnInAir(
            ConfigNode vesselNode, CelestialBody body, double lat, double lon, double altitudeAsl,
            double descentMps, string label, string logTag)
            => SpawnInAir(vesselNode, body, lat, lon, altitudeAsl, descentMps, label, logTag, null);

        /// <summary>As above, spawned with a world rotation instead of upright (null: upright).</summary>
        internal static uint SpawnInAir(
            ConfigNode vesselNode, CelestialBody body, double lat, double lon, double altitudeAsl,
            double descentMps, string label, string logTag, Quaternion? worldRotation)
        {
            Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, altitudeAsl);
            Vector3d up = (worldPos - body.position).normalized;
            Quaternion worldRot = worldRotation ?? UprightRotation(body, lat, lon, altitudeAsl, out _);
            Quaternion srfRel = Quaternion.Inverse(body.bodyTransform.rotation) * worldRot;
            Vector3d surfaceVel = -up * descentMps;
            Vector3d orbitalVel = surfaceVel + body.getRFrmVel(worldPos);
            double ut = Planetarium.GetUniversalTime();
            uint pid = VesselSpawner.SpawnAtPosition(vesselNode, body, lat, lon, altitudeAsl, orbitalVel, ut,
                surfaceRelativeRotation: srfRel);
            ParsekLog.Info(logTag, $"spawn {label}: pid={pid.ToString(IC)} in the air lat={lat.ToString("0.00000", IC)} " +
                $"lon={lon.ToString("0.00000", IC)} alt={altitudeAsl.ToString("0", IC)}m descent={descentMps.ToString("0.0", IC)}m/s " +
                $"orbitalSpeed={orbitalVel.magnitude.ToString("0.0", IC)}m/s body={body.name}");
            return pid;
        }

        /// <summary>The upright world rotation over a point of the body (+Y along the local up,
        /// +Z north) and that up.</summary>
        internal static Quaternion UprightRotation(
            CelestialBody body, double lat, double lon, double altitudeAsl, out Vector3 up)
        {
            Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, altitudeAsl);
            Vector3d upD = (worldPos - body.position).normalized;
            Vector3d northPos = body.GetWorldSurfacePosition(lat + 0.001, lon, altitudeAsl);
            Vector3d north = Vector3d.Exclude(upD, northPos - worldPos).normalized;
            up = upD;
            return Quaternion.LookRotation((Vector3)north, (Vector3)upD);
        }

        /// <summary>
        /// Builds a ghost from <paramref name="real"/>'s own snapshot, the way playback would:
        /// a throwaway recording starting now carries the snapshot, the timeline builder makes
        /// the ghost, and the engine's build-and-initialise seam
        /// (<see cref="GhostPlaybackEngine.InitializeBuiltGhostVisuals"/>) fills the playback
        /// state. The ghost root is left unparented and in the builder's active state; the
        /// caller places, activates and neutralises it. False with a reason when the snapshot
        /// or the build fails.
        /// </summary>
        internal static bool TryBuildGhostFromLiveVessel(
            Vessel real, string recordingId, string ghostRootName, out LiveGhost ghost, out string failReason)
            => TryBuildGhostFromLiveVessel(real, recordingId, ghostRootName, null, null, out ghost, out failReason);

        /// <summary>
        /// As above, with two additions the part parity sweep needs (design D2): the
        /// throwaway recording's <c>PartEvents</c> start as <paramref name="seedEvents"/>
        /// (the live vessel's <c>PartStateSeeder</c> start seeds; replay them with
        /// <see cref="ApplySeedPrefix"/>), and <paramref name="mutateSnapshot"/> may edit the
        /// snapshot before the ghost is built from it (a control's wrong skin). Both null is
        /// exactly the overload above.
        /// </summary>
        internal static bool TryBuildGhostFromLiveVessel(
            Vessel real, string recordingId, string ghostRootName,
            List<PartEvent> seedEvents, Action<ConfigNode> mutateSnapshot,
            out LiveGhost ghost, out string failReason)
        {
            ghost = null;
            failReason = null;
            ConfigNode snapshot = FlightRecorder.TryBackupSnapshot(real);
            if (snapshot == null)
            {
                failReason = "TryBackupSnapshot returned null";
                return false;
            }
            mutateSnapshot?.Invoke(snapshot);
            return TryBuildGhostFromSnapshot(snapshot, recordingId, ghostRootName, seedEvents,
                real.vesselName, real.persistentId, double.NaN, out ghost, out failReason);
        }

        /// <summary>
        /// Builds a ghost from a SAVED snapshot (design-part-ghost-parity Appendix D.5), so a
        /// caller can build after the real vessel has moved on: the part parity sweep's event
        /// route builds from the stowed snapshot it took before the press. The throwaway
        /// recording starts at <paramref name="startUT"/> (now when NaN) and carries
        /// <paramref name="events"/> as its part events; everything else is
        /// <see cref="TryBuildGhostFromLiveVessel(Vessel, string, string, List{PartEvent}, Action{ConfigNode}, out LiveGhost, out string)"/>.
        /// </summary>
        internal static bool TryBuildGhostFromSnapshot(
            ConfigNode snapshot, string recordingId, string ghostRootName, List<PartEvent> events,
            string vesselName, uint vesselPersistentId, double startUT,
            out LiveGhost ghost, out string failReason)
        {
            ghost = null;
            failReason = null;
            if (snapshot == null)
            {
                failReason = "no snapshot";
                return false;
            }
            double t0 = double.IsNaN(startUT) ? Planetarium.GetUniversalTime() : startUT;
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = vesselName,
                VesselPersistentId = vesselPersistentId,
                ExplicitStartUT = t0,
                ExplicitEndUT = t0 + 100.0,
                GhostVisualSnapshot = snapshot.CreateCopy(),
                VesselSnapshot = snapshot.CreateCopy(),
            };
            if (events != null)
                rec.PartEvents.AddRange(events);
            GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(rec, ghostRootName);
            if (build == null || build.root == null)
            {
                failReason = "BuildTimelineGhostFromSnapshot returned no root";
                return false;
            }
            var state = new GhostPlaybackState
            {
                vesselName = rec.VesselName,
                recordingId = rec.RecordingId,
                ghost = build.root,
            };
            state.partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);
            state.SetDecoupleSnapshot(snapshot);
            state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);
            GhostPlaybackEngine.InitializeBuiltGhostVisuals(state, build, rec);
            ghost = new LiveGhost { Snapshot = snapshot, Recording = rec, Build = build, State = state };
            return true;
        }

        /// <summary>
        /// Replays the recording's part events up to <paramref name="ut"/> onto the ghost the
        /// way the engine primes a freshly loaded ghost (<c>PrimeLoadedGhostForPlaybackUT</c>),
        /// through the engine's own <see cref="GhostPlaybackEngine.PrimeHiddenPartStateAtUT"/>:
        /// cursor reset, the part events, robotics, deployables and synthesized motion under the
        /// hidden prime policy (no transient FX start), then the FX suppression. A ghost whose
        /// recording carries no part events (the FGV overload passes none) is left exactly as
        /// built. Returns the number of events applied.
        /// </summary>
        internal static int ApplySeedPrefix(LiveGhost ghost, double ut)
        {
            if (ghost?.State == null || ghost.Recording == null) return 0;
            if (ghost.Recording.PartEvents == null || ghost.Recording.PartEvents.Count == 0) return 0;
            return GhostPlaybackEngine.PrimeHiddenPartStateAtUT(-1, ghost.Recording, ghost.State, ut);
        }

        /// <summary>
        /// Destroys a rig ghost the way the engine destroys a ghost's resources
        /// (<c>GhostPlaybackEngine.DestroyGhostResources</c>, the parts reachable without an
        /// engine instance: the state's own materials, fairing shell meshes, engine / RCS
        /// particle systems stopped and cleared, fake canopies, the root). Materials the
        /// builder caches for reuse are deliberately left alone; clones nothing references
        /// go with <c>Resources.UnloadUnusedAssets</c>.
        /// </summary>
        internal static void DestroyLiveGhost(LiveGhost ghost)
        {
            if (ghost == null) return;
            GhostPlaybackState state = ghost.State;
            if (state != null)
            {
                if (state.materials != null)
                    for (int i = 0; i < state.materials.Count; i++)
                        if (state.materials[i] != null) UnityEngine.Object.Destroy(state.materials[i]);
                GhostVisualBuilder.DestroyFairingShellMeshes(state.fairingInfos);
                if (state.engineInfos != null)
                    foreach (var info in state.engineInfos.Values)
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                if (state.rcsInfos != null)
                    foreach (var info in state.rcsInfos.Values)
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                GhostPlaybackLogic.DestroyAllFakeCanopies(state);
            }
            GameObject root = ghost.Build != null ? ghost.Build.root : state?.ghost;
            if (root != null)
            {
                root.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(root);
            }
            if (ghost.Build != null) ghost.Build.root = null;
            if (state != null) state.ghost = null;
        }

        /// <summary>
        /// A ghost hung under a live physics part must not join its compound body (or fly off
        /// on its own): disables every collider and freezes every rigidbody in the hierarchy,
        /// inactive children included. Neither changes what a camera sees. Returns the counts.
        /// </summary>
        internal static void NeutralisePhysics(GameObject root, out int collidersDisabled, out int rigidbodiesFrozen)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;
            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].isKinematic = true;
                bodies[i].detectCollisions = false;
            }
            collidersDisabled = colliders.Length;
            rigidbodiesFrozen = bodies.Length;
        }
    }
}
