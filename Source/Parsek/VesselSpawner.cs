using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for spawning, recovering, and snapshotting vessels.
    /// </summary>
    public static partial class VesselSpawner
    {

        // Proximity offset removed — spawn collision detection now uses bounding box
        // overlap check via SpawnCollisionDetector (same system as chain-tip spawns).

        /// <summary>
        /// Maximum consecutive collision-blocked frames before abandoning spawn.
        /// ~2.5 seconds at 60fps. After this many frames the spawn is abandoned
        /// to prevent infinite retry loops (bug #110).
        /// </summary>
        internal const int MaxCollisionBlocks = 150;

        /// <summary>
        /// Failed attempts after which the shared end-of-recording spawn stops trying
        /// (<see cref="SpawnOrRecoverIfTooClose(Recording, int, bool, bool)"/>).
        /// </summary>
        internal const int MaxSpawnAttempts = 3;
        internal const int TerminalOrbitSpawnSafetyScanSteps = 180;
        private static Func<uint, bool> materializedSourceVesselExistsOverrideForTesting;
        // Resolves the launch Guid of the live/proto vessel with a given pid (null = unknown).
        // Paired with the existence override so the guid-aware adoption path is unit-testable
        // (R4): existence tests that leave this null get pid-only fallback (unchanged behavior).
        private static Func<uint, string> materializedSourceVesselGuidOverrideForTesting;

        internal const string ReplayBypassReasonPidOnly = "no-recording-context";
        internal const string ReplayBypassReasonDifferentLaunch = "different-launch";
        internal const string ReplayBypassReasonSameLaunch = "same-launch";

        private static bool IsHeadlessKspAccessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException || current is MethodAccessException || current is MissingMethodException)
                    return true;
            }

            return false;
        }

        internal static void ApplyResolvedSpawnStateToSnapshot(
            ConfigNode spawnSnapshot,
            Recording rec,
            TrajectoryPoint? rotationPoint,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            int index,
            string logContext,
            bool allowPreferredRotation = true,
            bool stripEvaLadder = true)
        {
            if (spawnSnapshot == null)
                return;

            string snapshotLabel = !string.IsNullOrEmpty(logContext)
                ? logContext
                : rec?.VesselName ?? "(unknown)";
            string vesselName = rec?.VesselName ?? snapshotLabel;
            if (allowPreferredRotation
                && TryResolvePreferredSpawnRotation(
                    rec,
                    rotationPoint,
                    out string rotationBodyName,
                    out Quaternion? rotationBodyRotation,
                    out Quaternion surfaceRelativeRotation,
                    out string rotationSource))
            {
                VesselSnapshotOps.OverrideSnapshotPosition(
                    spawnSnapshot,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    snapshotLabel,
                    rotationBodyName,
                    rotationBodyRotation,
                    surfaceRelativeRotation,
                    rotationSource);
            }
            else
            {
                VesselSnapshotOps.OverrideSnapshotPosition(
                    spawnSnapshot,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    snapshotLabel);
            }

            if (stripEvaLadder && !string.IsNullOrEmpty(rec?.EvaCrewName))
                VesselSnapshotOps.StripEvaLadderState(spawnSnapshot, index, vesselName);
        }

        internal static string PrepareSpawnNodeAtPosition(
            ConfigNode spawnNode,
            string bodyName,
            Quaternion? bodyRotation,
            double lat, double lon, double alt,
            double speed,
            double orbitalSpeed,
            bool overWater,
            TerminalState? terminalState,
            Quaternion? surfaceRelativeRotation)
        {
            if (spawnNode == null)
                return null;

            spawnNode.SetValue("lat", lat.ToString("R", CultureInfo.InvariantCulture), true);
            spawnNode.SetValue("lon", lon.ToString("R", CultureInfo.InvariantCulture), true);
            spawnNode.SetValue("alt", alt.ToString("R", CultureInfo.InvariantCulture), true);

            string sit = VesselSnapshotOps.DetermineSituation(alt, overWater, speed, orbitalSpeed);

            // Override FLYING situation from terminal state (#176 for Orbiting/Docked, #264
            // for Landed/Splashed). Without this, an EVA kerbal walking at alt > 0 with
            // low speed classifies as FLYING and hits the OrbitDriver updateMode=UPDATE
            // stale-orbit bug on the first physics frame after load.
            string overriddenSit = VesselSnapshotOps.OverrideSituationFromTerminalState(sit, terminalState);
            if (overriddenSit != sit)
            {
                ParsekLog.Info("Spawner",
                    $"SpawnAtPosition: overriding sit {sit} → {overriddenSit} " +
                    $"(terminal={terminalState}, speed={speed.ToString("F1", CultureInfo.InvariantCulture)}, " +
                    $"orbitalSpeed={orbitalSpeed.ToString("F1", CultureInfo.InvariantCulture)}) — prevents stale-orbit / pressure destruction (#176, #264)");
                sit = overriddenSit;
            }

            VesselSnapshotOps.ApplySituationToNode(spawnNode, sit);
            ParsekLog.Verbose("Spawner",
                $"SpawnAtPosition: determined sit={sit} (alt={alt.ToString("F0", CultureInfo.InvariantCulture)}, speed={speed.ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"orbitalSpeed={orbitalSpeed.ToString("F1", CultureInfo.InvariantCulture)}, overWater={overWater})");

            // Optional rotation override: callers hand ProtoVessel.rot a surface-relative
            // rotation. Absolute/surface trajectory points already store that frame; v6
            // RELATIVE section rotations are anchor-local and must be converted before
            // reaching this spawn path.
            if (surfaceRelativeRotation.HasValue)
            {
                VesselSnapshotOps.TryApplySpawnRotationFromSurfaceRelative(
                    spawnNode,
                    bodyName,
                    bodyRotation,
                    surfaceRelativeRotation,
                    "SpawnAtPosition");
            }

            return sit;
        }

        /// <summary>
        /// Spawn a vessel from a snapshot at a specific position and velocity,
        /// overriding the snapshot's stored orbit and location.
        /// Spawns at a specific position overriding the snapshot's stored orbit.
        /// </summary>
        public static uint SpawnAtPosition(ConfigNode vesselNode, CelestialBody body,
            double lat, double lon, double alt,
            Vector3d velocity, double ut,
            HashSet<string> excludeCrew = null, bool preserveIdentity = false,
            TerminalState? terminalState = null,
            Quaternion? surfaceRelativeRotation = null,
            Orbit orbitOverride = null,
            Recording nameSource = null)
        {
            ProtoVessel pv = null;
            CrewInventoryRestoreResult crewInventoryRestore = default(CrewInventoryRestoreResult);
            try
            {
                ConfigNode spawnNode = vesselNode.CreateCopy();
                ConverterJumpPolicy.ApplyToSpawnSnapshot(spawnNode, "SpawnAtPosition");

                // Determine situation from altitude and velocity
                double orbitalSpeed = Math.Sqrt(body.gravParameter / (body.Radius + alt));
                bool overWater = body.ocean && body.TerrainAltitude(lat, lon) < 0;
                string sit = PrepareSpawnNodeAtPosition(
                    spawnNode,
                    body?.name,
                    body != null && body.bodyTransform != null
                        ? (Quaternion?)body.bodyTransform.rotation
                        : null,
                    lat,
                    lon,
                    alt,
                    velocity.magnitude,
                    orbitalSpeed,
                    overWater,
                    terminalState,
                    surfaceRelativeRotation);

                // Rebuild ORBIT subnode from position + velocity. When no caller-supplied
                // orbit is passed (EVA / breakup / non-terminal-orbit respawn paths), the
                // velocity argument is a recorder-frame Y-up world vector (TrajectoryPoint.velocity,
                // or zero on the tipPoint-less walkback fallback, which is frame-invariant) and
                // worldPos is an absolute Y-up world position. Orbit.UpdateFromStateVectors
                // requires body-relative Zup inputs, so route through OrbitReseed rather than
                // feeding the raw vectors (the bare call produces the sub-surface / ~500 k-sma
                // garbage orbit OrbitReseed exists to correct; see OrbitReseed remarks. This
                // no-override branch is regression-guarded in-game by
                // RuntimeTests.SpawnAtPosition_NoOverrideStationAltitude_ReseedsInBand).
                Orbit orbit = orbitOverride;
                bool orbitWasReseeded = false;
                if (orbit == null)
                {
                    orbit = new Orbit();
                    Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
                    OrbitReseed.FromWorldPosAndRecordedVelocity(orbit, body, worldPos, velocity, ut);
                    orbitWasReseeded = true;
                }
                spawnNode.RemoveNode("ORBIT");
                ConfigNode orbitNode = new ConfigNode("ORBIT");
                // Deterministic landed reseed (#620 flake): a landed endpoint with an
                // EXACTLY zero recorded velocity reseeds to h=0 / ecc=1, and KSP's
                // Orbit.UpdateFromStateVectors then computes SMA = 0/0 = NaN, which the
                // finite-metadata gate below rejects (a float-residue near-zero velocity
                // yields a finite ~r/2 SMA instead, hence run-to-run flakiness). A
                // landed/splashed vessel does not need the reseeded state-vector orbit at
                // all: substitute the canonical surface orbit tuple, the same shape
                // ApplySurfaceOrbitToSnapshot writes. Non-landed situations keep the
                // reseeded orbit (rejecting a non-finite orbit is correct there).
                bool substitutedSurfaceOrbit = false;
                if (orbitWasReseeded
                    && VesselSnapshotOps.IsLandedLikeSituation(sit)
                    && OrbitReseed.HasNonFiniteOrbitElement(orbit, out string nonFiniteOrbitElements))
                {
                    if (LiveVesselSnapshot.TryResolveBodyIndex(body, out int surfaceBodyIndex))
                    {
                        VesselSnapshotOps.WriteCanonicalSurfaceOrbitValues(orbitNode, surfaceBodyIndex);
                        substitutedSurfaceOrbit = true;
                        ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                            "SpawnAtPosition: landed reseed produced non-finite orbit element(s) [{0}] " +
                            "(sit={1}, body={2}, |vel|={3:F3}): substituting canonical surface orbit " +
                            "(REF={4}); exactly-zero landed velocity reseeds to ecc=1/h=0 (NaN SMA)",
                            nonFiniteOrbitElements, sit, body.name, velocity.magnitude, surfaceBodyIndex));
                    }
                    else
                    {
                        ParsekLog.Warn("Spawner", string.Format(CultureInfo.InvariantCulture,
                            "SpawnAtPosition: landed reseed produced non-finite orbit element(s) [{0}] " +
                            "(sit={1}, |vel|={2:F3}) but body index for '{3}' could not be resolved: " +
                            "keeping reseeded orbit (finite-metadata validation will reject)",
                            nonFiniteOrbitElements, sit, velocity.magnitude, body?.name ?? "(null)"));
                    }
                }
                if (!substitutedSurfaceOrbit)
                    LiveVesselSnapshot.SaveOrbitToNode(orbit, orbitNode, body);
                spawnNode.AddNode(orbitNode);
                if (sit == "ORBITING")
                    VesselSnapshotOps.NormalizeOrbitalSpawnMetadata(spawnNode, ut);

                // Crew handling
                // (#608/#609/#687) Rescue Missing crew BEFORE the keep/remove
                // pass so RemoveDeadCrewFromSnapshot sees them as Available
                // and the snapshot is loadable by ProtoVessel.Load. Both
                // reserved+Missing and unreserved+Missing shapes are rescued.
                // #615 P1 review (fourth pass): collect rescued names; the
                // pid-scoped MarkRescuePlaced call happens AFTER pv.Load when
                // the new vessel's persistentId is known. Only RESERVED
                // rescues are collected — unreserved rescues have no stand-in
                // lifecycle for the marker to suppress.
                var rescuedNames = new List<string>();
                RescueReservedMissingCrewInSnapshot(spawnNode, rescuedNames);
                RemoveDeadCrewFromSnapshot(spawnNode);
                EnsureCrewExistInRoster(spawnNode);
                if (excludeCrew != null && excludeCrew.Count > 0)
                    VesselSnapshotOps.RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);
                CrewReservationManager.UnreserveCrewInSnapshot(spawnNode);

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                if (!preserveIdentity)
                {
                    RegenerateVesselIdentity(spawnNode, nameSource);
                }
                else
                {
                    ParsekLog.Info("Spawner", "SpawnAtPosition: preserving vessel identity (chain-tip spawn)");
                }
                VesselNameSpawn.ApplySpawnStamp(spawnNode, nameSource, "SpawnAtPosition");
                VesselSnapshotOps.EnsureSpawnReadiness(spawnNode);

                if (!VesselSnapshotOps.TryValidateSnapshotHasParts(spawnNode, out string materializationRejectionReason)
                    || !VesselSnapshotOps.TryValidateFiniteMaterializationMetadata(spawnNode, out materializationRejectionReason))
                {
                    LogMaterializationSnapshotRejected("SpawnAtPosition", materializationRejectionReason);
                    return 0;
                }

                VesselSnapshotOps.StripDeliberatePositionOverrideStamp(spawnNode, "SpawnAtPosition");

                // Give each kerbal still seated in the final spawn node the inventory he
                // carried when the snapshot was taken, replacing his current roster
                // inventory, before the ProtoVessel reads the roster (no duplicated or
                // lost cargo). No capture on the snapshot keeps the roster inventory.
                crewInventoryRestore = CrewInventorySnapshot.RestoreForSpawnLive(spawnNode, "SpawnAtPosition");

                var runningHarvesters = SpawnedHarvesterRestore.CollectActiveHarvesters(spawnNode);
                pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: SpawnAtPosition — ProtoVessel.Load() produced null vesselRef");
                    CleanupFailedSpawnedProtoVessel(pv, "Spawner", "SpawnAtPosition cleanup");
                    CrewInventorySnapshot.RollbackRestoreLive(crewInventoryRestore, "SpawnAtPosition null vesselRef");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "SpawnAtPosition vessel has no orbitDriver — may not appear in map view");

                // rb.mass seeding is handled centrally by the
                // PartMassivePartCheckSeederPatch Harmony prefix on
                // Part.MassivePartCheck. The prefix lazily seeds each
                // part's rb.mass at the exact read site during the
                // ForceHeaviest autostrut selection, so the seed is
                // correct for every load path (stock save-load, background
                // activation, this SpawnAtPosition path once it reaches
                // physics) without depending on coroutine-timing
                // assumptions.

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, sit);
                if (HighLogic.LoadedSceneIsFlight)
                    SpawnedHarvesterRestore.Arm(pv.vesselRef, runningHarvesters, "SpawnAtPosition");

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                // #615 P1 review (fourth pass): now that pv.vesselRef has a
                // persistentId, mark each rescued kerbal with the new vessel's
                // pid so the ApplyToRoster guard can scope the rescue marker
                // to the actual vessel the kerbal was placed onto.
                ulong spawnedAtPositionVesselPid = pv.vesselRef.persistentId;
                double spawnedAtPositionRescueUT = rescuedNames.Count > 0
                    ? Planetarium.GetUniversalTime() : double.NaN;
                for (int rn = 0; rn < rescuedNames.Count; rn++)
                {
                    CrewReservationManager.MarkRescuePlaced(
                        rescuedNames[rn], spawnedAtPositionVesselPid, spawnedAtPositionRescueUT);
                }
                if (rescuedNames.Count > 0)
                    ParsekLog.Info("Spawner",
                        $"Rescue marker pid-scoped for {rescuedNames.Count} kerbal(s) " +
                        $"(vesselPid={spawnedAtPositionVesselPid} " +
                        $"markUT={spawnedAtPositionRescueUT.ToString("F1", CultureInfo.InvariantCulture)}; #615 P1 fourth pass)");

                ParsekLog.Info("Spawner", $"SpawnAtPosition: vessel spawned (sit={sit}, pid={pv.vesselRef.persistentId}, " +
                    $"body={body.name}, alt={alt:F0}m) " + VesselSnapshotOps.FormatStoredPartsForLog(spawnNode));
                return pv.vesselRef.persistentId;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"SpawnAtPosition failed: {ex.Message}");
                CleanupFailedSpawnedProtoVessel(pv, "Spawner", "SpawnAtPosition cleanup");
                CrewInventorySnapshot.RollbackRestoreLive(crewInventoryRestore, "SpawnAtPosition exception");
                return 0;
            }
        }

        /// <summary>
        /// Safety floor above PQS terrain. Only used when the recorded altitude is below
        /// current PQS terrain (terrain shift since recording) — we push up by this much
        /// above raw terrain to avoid an underground spawn. Not a clearance target.
        /// </summary>
        internal const double UndergroundSafetyFloorMeters = 2.0;

        /// <summary>
        /// Trigger threshold for walkback-to-surface snapping. If a walkback trajectory
        /// candidate is more than this many metres above the raycast-detected surface at
        /// its (lat, lon), we snap it down to surface + clearance — otherwise trust the
        /// recorded altitude. Guards against spawning a diagonally-descending vessel in
        /// mid-air.
        /// </summary>
        internal const double WalkbackSurfaceSnapThresholdMeters = 5.0;

        /// <summary>
        /// Clearance above the raycast-detected surface when snapping a walkback candidate
        /// down to the ground. KSP's Vessel.CheckGroundCollision will fire on load and
        /// further adjust via getLowestPoint(), so we only need a small breathing-room.
        /// </summary>
        internal const double WalkbackSurfaceClearanceMeters = 1.0;

        /// <summary>
        /// Part-collider layer mask used by the walkback raycast. Matches KSP's
        /// Vessel.GetHeightFromSurface: <c>LayerUtil.DefaultEquivalent | 0x8000 | 0x80000</c>
        /// — default layers + terrain (layer 15) + buildings (layer 19). Hits BOTH raw PQS
        /// terrain AND placed mesh objects (Island Airfield, launchpad, KSC facilities),
        /// unlike body.TerrainAltitude() which is PQS-only.
        /// </summary>
        private static int WalkbackSurfaceLayerMask
        {
            get { return LayerUtil.DefaultEquivalent | 0x8000 | 0x80000; }
        }

        /// <summary>
        /// Starting height above expected surface for the walkback raycast origin. We
        /// cast downward from (startAltAboveSurface + this) to ensure the ray origin is
        /// above any collider at the query point.
        /// </summary>
        private const double WalkbackRaycastOriginOffsetMeters = 1000.0;

        /// <summary>
        /// Maximum raycast distance. Covers the offset + a margin for deep valleys.
        /// Combined with <see cref="WalkbackRaycastOriginOffsetMeters"/> this gives a
        /// practical altitude ceiling of ~1500 m above surface for walkback candidates
        /// (above that, the ray won't reach the surface and <see cref="TryFindSurfaceAltitudeViaRaycast"/>
        /// returns NaN, falling back to the PQS safety floor via
        /// <see cref="ClampAltitudeForLanded"/>). This is fine for the walkback's target
        /// use case — LANDED terminal trajectories where the final descent happens in
        /// the last few hundred meters. Higher-altitude sub-orbital walkback candidates
        /// degrade gracefully to PQS-based clamping.
        /// </summary>
        private const float WalkbackRaycastMaxDistanceMeters = 2500f;

        /// <summary>
        /// NaN-fallback clearance for ghost playback of Landed/Splashed recordings.
        /// Only used when <c>Recording.TerrainHeightAtEnd</c> is NaN (legacy recordings
        /// without surface data). When surface data is available,
        /// <c>ApplyLandedGhostClearance</c> trusts the recorded altitude directly and
        /// only pushes up when it falls below the current PQS underground safety floor.
        /// </summary>
        internal const double LandedGhostClearanceMeters = 4.0;

        /// <summary>
        /// Maximum spawn-then-die cycles before abandoning spawn.
        /// A vessel that spawns and immediately dies (e.g., FLYING at sea level,
        /// destroyed by KSP on-rails aero check) should not retry forever.
        /// </summary>
        internal const int MaxSpawnDeathCycles = 3;

        /// <summary>The trajectory-tail seed (built from body-fixed samples with the
        /// body's live rotation) as <c>seedSource</c>.</summary>
        internal const string TerminalOrbitSeedTrajectoryTail = "trajectory-tail";
        internal const string TerminalOrbitSeedStoredSegment = "stored-segment";

        // Tail UT must exceed the latest stored OrbitSegment endUT by at least this
        // many seconds for the tail-derived seed to win. Guards against trivial
        // boundary noise where a frame and a segment end at the same UT.
        internal const double TailDerivedOrbitFreshnessEpsilon =
            OrbitSeedResolver.TailDerivedOrbitFreshnessEpsilon;

        // Max permitted (spawnUT − tailUT) before the rotation drift between body's
        // current rotation and its rotation at candidate.ut grows large enough to
        // shift the orbit's LAN/argPe by more than the spawn safety margin. 30 s on
        // Kerbin (period 21 600 s) is ~0.5° of LAN drift; on slower bodies (Mun
        // tidally-locked, Minmus 5.5 h period) the drift is much smaller. Re-Fly
        // spawns at currentUT ≈ tailUT comfortably satisfy this.
        internal const double TailDerivedOrbitMaxRotationDriftSeconds =
            OrbitSeedResolver.TailDerivedOrbitMaxRotationDriftSeconds;

        #region Extracted helpers

        /// <summary>
        /// Pure decision: should vessel identity be regenerated?
        /// Returns true for normal spawns (new GUID), false for chain-tip spawns
        /// that must preserve the original vessel's PID for continuity.
        /// </summary>
        internal static bool ShouldRegenerateIdentity(bool preserveIdentity)
        {
            return !preserveIdentity;
        }

        /// <summary>
        /// Pure decision: should post-spawn velocity zeroing be applied? (#239)
        /// Only surface situations need stabilization — orbital/flying vessels must keep velocity.
        /// </summary>
        internal static bool ShouldZeroVelocityAfterSpawn(string situation)
        {
            if (string.IsNullOrEmpty(situation)) return false;
            return situation.Equals("LANDED", StringComparison.OrdinalIgnoreCase)
                || situation.Equals("SPLASHED", StringComparison.OrdinalIgnoreCase)
                || situation.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Zero linear and angular velocity on a freshly spawned vessel to prevent
        /// physics jitter on surface spawns. No-op for orbital/flying situations. (#239)
        /// </summary>
        internal static void ApplyPostSpawnStabilization(Vessel vessel, string situation)
        {
            if (vessel == null || !ShouldZeroVelocityAfterSpawn(situation)) return;

            vessel.SetWorldVelocity(Vector3d.zero);
            vessel.angularVelocity = Vector3.zero;
            vessel.angularMomentum = Vector3.zero;

            ParsekLog.Verbose("Spawner",
                $"Post-spawn stabilization: pid={vessel.persistentId} sit={situation} — velocity zeroed");
        }

        // FlightIntegrator only updates Part.rb.mass for UNPACKED parts
        // (Assembly-CSharp FlightIntegrator: `if (part.packed) continue;`
        // inside the per-part mass-update loop). Every ProtoVessel.Load
        // instantiates the vessel in PACKED state, so every Part.rb keeps
        // Unity's default mass=1 until the vessel is first unpacked. The
        // Part.Start chain runs UpdateAutoStrut->CycleAutoStrut->
        // SecureAutoStruts shortly after pv.Load, while the vessel is still
        // packed; if all rb.mass values are still the Unity default,
        // Part.MassivePartCheck's approximate-equality branch ties at
        // mass=1 across every part and falls into the distance tiebreaker,
        // so ForceHeaviest legs anchor to whichever sibling part happens to
        // be closest instead of the actual heaviest part (the fuel tank).
        // The wrong-anchor autostrut joints survive the packed coast at
        // breakingForce=MaxValue, and when the player Switch-To unpacks the
        // vessel they release all at once and cascade-explode the central
        // stack within ~40ms. Seeding rb.mass right after pv.Load is enough
        // to keep MassivePartCheck on the real heaviest part. Matches the
        // formula FlightIntegrator uses for unpacked parts (including the
        // GetPhysicslessChildMass roll-up so a physical parent under a
        // cluster of physicsless decorative children still ranks at the
        // mass FlightIntegrator would assign at unpack).
        //
        // Production seeding is per-part lazy at the actual rb.mass read
        // site: see Patches.PartMassivePartCheckSeederPatch. That prefix
        // calls SeedSinglePackedPart below.
        //
        // PR #885 placed inline calls right after pv.Load(flightState),
        // but ilspycmd of Assembly-CSharp.dll showed that overload never
        // populates vesselRef.parts (parts are instantiated by
        // protoVessel.LoadObjects, called later from Vessel.Load), so the
        // seeder loop iterated zero times in every production log line for
        // two months. A subsequent attempt to postfix Vessel.Load found
        // parts populated but part.rb still null (rb is created later
        // inside the Part.Start coroutine on a future Update tick). Both
        // mis-anchored fixes silently shipped no-op seeders. The
        // Part.MassivePartCheck prefix sidesteps every lifecycle question:
        // it fires AT the read site, after rb exists by virtue of having a
        // mass field to read, so the seed lands exactly when needed.
        internal static float ComputeRigidbodyMassForPackedSpawn(
            float partMass,
            float resourceMass,
            float physicslessChildMass,
            float minimumMass,
            float minimumRBMass)
        {
            float effectiveMass = Mathf.Max(
                partMass + resourceMass + physicslessChildMass,
                minimumMass);
            return Mathf.Max(effectiveMass, minimumRBMass);
        }

        // Sum mass + resourceMass of physicsless descendants under `parent`,
        // matching FlightIntegrator.GetPhysicslessChildMass: rb==null children
        // roll their mass up to the nearest physical ancestor's rb.mass.
        // Physicsless children can themselves have physicsless descendants,
        // so the walk is recursive but bounded by part-tree depth.
        // Internal (not private) so PartMassivePartCheckSeederPatch can reuse it.
        internal static float SumPhysicslessChildMass(Part parent)
        {
            if (parent == null || parent.children == null) return 0f;
            float total = 0f;
            for (int i = 0; i < parent.children.Count; i++)
            {
                Part child = parent.children[i];
                if (child == null) continue;
                if (child.rb != null) continue;
                total += child.mass + child.resourceMass;
                total += SumPhysicslessChildMass(child);
            }
            return total;
        }

        // Single-part seeder used by PartMassivePartCheckSeederPatch.
        // Returns true if a write happened (caller increments a counter for
        // diagnostics). Caller (the patch's gate) already enforces the
        // packed / rb==null / partInfo==null / vesselType / ghost-map
        // checks before reaching here; the defensive guards below are a
        // belt-and-braces backup so a refactor that opens a new caller
        // cannot NRE.
        internal static bool SeedSinglePackedPart(Part p)
        {
            if (p == null || p.rb == null) return false;
            AvailablePart partInfo = p.partInfo;
            if (partInfo == null) return false;
            float physicslessChildMass = SumPhysicslessChildMass(p);
            float seededMass = ComputeRigidbodyMassForPackedSpawn(
                p.mass,
                p.resourceMass,
                physicslessChildMass,
                partInfo.MinimumMass,
                partInfo.MinimumRBMass);
            p.rb.mass = seededMass;
            return true;
        }

        #endregion
    }
}
