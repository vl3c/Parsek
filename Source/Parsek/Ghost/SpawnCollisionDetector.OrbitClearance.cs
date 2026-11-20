using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>An asteroid or comet that blocks a spawn point.</summary>
    internal struct SpawnRockBlocker
    {
        public Vessel Vessel;
        /// <summary>The rock part of a loaded vessel (null for an unloaded rock).</summary>
        public Part Part;
        public Vector3d Centre;
        /// <summary>Radius (metres) the rock may reach: its colliders when loaded, its size class otherwise.</summary>
        public double Radius;
        public bool Loaded;
        public string Name;
    }

    // Orbit-route spawn clearance (owner ruling 2026-11-21, todo
    // SPAWN-ORBITAL-WALKBACK-NOT-WHERE-IT-SPAWNS / SPAWN-RESTING-ON-ROCK): the live overlap
    // tests behind OrbitSpawnClearance's pure searches. A recorded-terminal-orbit spawn that
    // overlaps another vessel moves along its own orbit; one that overlaps an asteroid or
    // comet (loaded, or unloaded near the spawn point) is pushed out from the rock and takes
    // its velocity. Every call runs in one synchronous pass at one UT.
    internal static partial class SpawnCollisionDetector
    {
        /// <summary>
        /// The full orbit-route clearance at <paramref name="spawnUT"/> for a vessel placed by
        /// <paramref name="spawnOrbit"/>: a rock at the spawn point pushes the spawn out from it
        /// (<see cref="ResolveRockPushOut"/>); otherwise a loaded-vessel overlap moves it along
        /// its own orbit (<see cref="SearchClearOrbitPhase"/>); otherwise the point is clear.
        /// </summary>
        internal static OrbitSpawnClearanceResult ResolveOrbitSpawnClearance(
            Orbit spawnOrbit, CelestialBody body, double spawnUT, Bounds spawnBounds, float padding,
            uint exemptVesselPid, Recording spawningRecording, string site, string logContext)
        {
            var clear = new OrbitSpawnClearanceResult
            {
                Outcome = OrbitSpawnClearanceOutcome.Clear,
                Orbit = spawnOrbit,
            };
            if (spawnOrbit == null || body == null)
                return clear;
            clear.WorldPos = spawnOrbit.getPositionAtUT(spawnUT);
            clear.VelocityZup = spawnOrbit.getOrbitalVelocityAtUT(spawnUT);

            OrbitSpawnClearanceResult rockResult;
            if (TryResolveRockAtOrbitSpawn(spawnOrbit, body, spawnUT, spawnBounds, padding,
                    exemptVesselPid, spawningRecording, site, logContext, out rockResult))
                return rockResult;

            var (overlap, _, blockerName, _) = CheckOverlapAgainstLoadedVessels(
                clear.WorldPos, spawnBounds, padding, exemptVesselPid,
                spawningRecording: spawningRecording, site: site);
            if (!overlap)
                return clear;

            return SearchClearOrbitPhase(spawnOrbit, body, spawnUT, spawnBounds, padding,
                exemptVesselPid, spawningRecording, site, logContext, blockerName);
        }

        /// <summary>
        /// The rock half of the clearance: when an asteroid or comet blocks the propagated
        /// spawn point, returns true with the push-out result
        /// (<see cref="OrbitSpawnClearanceOutcome.RockPushedOut"/> or
        /// <see cref="OrbitSpawnClearanceOutcome.Blocked"/>); false when no rock is there.
        /// </summary>
        internal static bool TryResolveRockAtOrbitSpawn(
            Orbit spawnOrbit, CelestialBody body, double spawnUT, Bounds spawnBounds, float padding,
            uint exemptVesselPid, Recording spawningRecording, string site, string logContext,
            out OrbitSpawnClearanceResult result)
        {
            result = default(OrbitSpawnClearanceResult);
            if (spawnOrbit == null || body == null)
                return false;
            Vector3d spawnPos = spawnOrbit.getPositionAtUT(spawnUT);
            if (!TryFindRockBlocker(spawnPos, spawnBounds, padding, spawnUT, body, out SpawnRockBlocker rock))
                return false;
            result = ResolveRockPushOut(rock, body, spawnUT, spawnPos, spawnBounds, padding,
                exemptVesselPid, spawningRecording, site, logContext);
            return true;
        }

        /// <summary>
        /// Moves a blocked orbit spawn a little ahead or behind along its own orbit: walks
        /// <see cref="OrbitSpawnClearance.BuildPhaseShiftScheduleMeters"/> and accepts the first
        /// phase whose bounds overlap no loaded vessel and no rock. Blocked when none within
        /// <see cref="OrbitSpawnClearance.PhaseShiftMaxMeters"/> is clear. Logged once per outcome.
        /// </summary>
        internal static OrbitSpawnClearanceResult SearchClearOrbitPhase(
            Orbit spawnOrbit, CelestialBody body, double spawnUT, Bounds spawnBounds, float padding,
            uint exemptVesselPid, Recording spawningRecording, string site, string logContext,
            string initialBlockerName)
        {
            var result = new OrbitSpawnClearanceResult
            {
                Outcome = OrbitSpawnClearanceOutcome.Blocked,
                Orbit = spawnOrbit,
                BlockerName = initialBlockerName,
            };
            if (spawnOrbit != null)
            {
                result.WorldPos = spawnOrbit.getPositionAtUT(spawnUT);
                result.VelocityZup = spawnOrbit.getOrbitalVelocityAtUT(spawnUT);
            }

            List<double> schedule = OrbitSpawnClearance.BuildPhaseShiftScheduleMeters(
                OrbitSpawnClearance.PhaseShiftFirstStepMeters,
                OrbitSpawnClearance.PhaseShiftGrowthFactor,
                OrbitSpawnClearance.PhaseShiftMaxMeters);
            Orbit candidateOrbit = null;
            Vector3d candidatePos = Vector3d.zero;
            Vector3d candidateVel = Vector3d.zero;
            double candidateSeconds = 0.0;
            int unbuildable = 0;
            int rockBlocked = 0;
            string lastBlocker = initialBlockerName;
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPhaseShift(schedule, shift =>
            {
                if (!OrbitSpawnClearance.TryBuildPhaseShiftedOrbit(
                        spawnOrbit, spawnUT, shift, out candidateOrbit, out candidatePos,
                        out candidateVel, out candidateSeconds))
                {
                    unbuildable++;
                    return false;
                }
                var (ov, _, name, _) = CheckOverlapAgainstLoadedVessels(
                    candidatePos, spawnBounds, padding, exemptVesselPid,
                    spawningRecording: spawningRecording, site: site + "-phase");
                if (ov)
                {
                    lastBlocker = name;
                    return false;
                }
                if (TryFindRockBlocker(candidatePos, spawnBounds, padding, spawnUT, body, out SpawnRockBlocker rock))
                {
                    rockBlocked++;
                    lastBlocker = rock.Name;
                    return false;
                }
                return true;
            });

            result.Attempts = search.Attempts;
            if (search.Found && candidateOrbit != null)
            {
                result.Outcome = OrbitSpawnClearanceOutcome.PhaseShifted;
                result.Orbit = candidateOrbit;
                result.WorldPos = candidatePos;
                result.VelocityZup = candidateVel;
                result.ShiftMeters = search.Value;
                result.ShiftSeconds = candidateSeconds;
            }
            else
            {
                result.BlockerName = lastBlocker;
            }

            ParsekLog.VerboseRateLimited(Tag, "orbit-phase-summary|" + (logContext ?? ""),
                string.Format(CultureInfo.InvariantCulture,
                    "Orbit phase search: site={0} {1} candidates={2} tried={3} unbuildable={4} " +
                    "rockBlocked={5} found={6} maxShift={7:F0}m",
                    site ?? "(none)", logContext ?? "(none)", schedule.Count, search.Attempts,
                    unbuildable, rockBlocked, search.Found, OrbitSpawnClearance.PhaseShiftMaxMeters));
            OrbitSpawnClearance.LogOutcome(site, logContext, result);
            return result;
        }

        /// <summary>
        /// Pushes a spawn straight out from <paramref name="rock"/>'s centre until its bounds
        /// clear the rock by <see cref="OrbitSpawnClearance.RockPushOutMarginMeters"/>, and gives it
        /// the rock's velocity, so it co-moves with the rock. A loaded rock is measured on its
        /// colliders; an unloaded one on its size-class radius. Blocked when the pushed spot
        /// overlaps another loaded vessel or the co-moving orbit cannot be built. Logged.
        /// </summary>
        internal static OrbitSpawnClearanceResult ResolveRockPushOut(
            SpawnRockBlocker rock, CelestialBody body, double spawnUT, Vector3d spawnPos,
            Bounds spawnBounds, float padding, uint exemptVesselPid, Recording spawningRecording,
            string site, string logContext)
        {
            var result = new OrbitSpawnClearanceResult
            {
                Outcome = OrbitSpawnClearanceOutcome.Blocked,
                WorldPos = spawnPos,
                BlockerName = rock.Name,
            };
            Vector3d direction = OrbitSpawnClearance.ResolvePushOutDirection(
                rock.Centre, spawnPos, spawnPos - body.position);
            double boundsRadius = OrbitSpawnClearance.ResolveBoundsRadius(spawnBounds);
            double clearance = OrbitSpawnClearance.ResolveRockClearanceDistance(
                rock.Radius, boundsRadius, OrbitSpawnClearance.RockPushOutMarginMeters);
            double start = (spawnPos - rock.Centre).magnitude;
            double max = Math.Max(clearance, start + OrbitSpawnClearance.RockPushOutStepMeters);
            OrbitSpawnClearanceSearch search = direction.sqrMagnitude > 0.0
                ? OrbitSpawnClearance.SearchPushOutDistance(start, OrbitSpawnClearance.RockPushOutStepMeters, max,
                    distance => rock.Loaded
                        ? !OverlapsRockPart(rock.Centre + direction * distance, spawnBounds,
                            OrbitSpawnClearance.RockPushOutMarginMeters, rock.Part)
                        : distance >= clearance)
                : new OrbitSpawnClearanceSearch();
            result.Attempts = search.Attempts;

            string refusal = null;
            if (!search.Found)
            {
                refusal = direction.sqrMagnitude > 0.0 ? "no-clear-distance" : "no-direction";
            }
            else
            {
                Vector3d pushedPos = rock.Centre + direction * search.Value;
                Vector3d rockVelocity = rock.Vessel.orbit.getOrbitalVelocityAtUT(spawnUT);
                if (!OrbitSpawnClearance.TryBuildCoMovingOrbit(body, pushedPos, rockVelocity, spawnUT, out Orbit coMoving))
                {
                    refusal = "co-moving-orbit-unbuildable";
                }
                else
                {
                    var (ov, _, name, _) = CheckOverlapAgainstLoadedVessels(
                        pushedPos, spawnBounds, padding, exemptVesselPid,
                        spawningRecording: spawningRecording, site: site + "-rock");
                    if (ov)
                    {
                        refusal = "pushed-spot-blocked";
                        result.BlockerName = name;
                    }
                    else
                    {
                        result.Outcome = OrbitSpawnClearanceOutcome.RockPushedOut;
                        result.Orbit = coMoving;
                        result.WorldPos = pushedPos;
                        result.VelocityZup = rockVelocity;
                        result.PushOutMeters = search.Value - start;
                    }
                }
            }

            ParsekLog.InfoRateLimited(Tag, "rock-push-out|" + (logContext ?? "") + "|" + (refusal ?? "ok"),
                string.Format(CultureInfo.InvariantCulture,
                    "Rock push-out: site={0} {1} rock='{2}' pid={3} loaded={4} rockRadius={5:F1}m " +
                    "boundsRadius={6:F1}m startDistance={7:F1}m finalDistance={8:F1}m steps={9} " +
                    "result={10}",
                    site ?? "(none)", logContext ?? "(none)", rock.Name ?? "(unnamed)",
                    rock.Vessel != null ? rock.Vessel.persistentId : 0u, rock.Loaded, rock.Radius,
                    boundsRadius, start, search.Found ? search.Value : start, search.Attempts,
                    refusal ?? "co-moving with the rock"));
            OrbitSpawnClearance.LogOutcome(site, logContext, result);
            return result;
        }

        /// <summary>
        /// The asteroid or comet whose space the spawn bounds at <paramref name="spawnPos"/>
        /// (plus <paramref name="padding"/>) reach: a loaded rock part the overlap box hits
        /// (rock parts are skipped by the ordinary overlap check, a SpaceObject vessel type), or
        /// an unloaded bare rock whose size-class sphere is that close. Rocks of another body,
        /// ghost map vessels and rocks with no orbit are left out.
        /// </summary>
        internal static bool TryFindRockBlocker(
            Vector3d spawnPos, Bounds spawnBounds, float padding, double ut, CelestialBody body,
            out SpawnRockBlocker rock)
        {
            rock = default(SpawnRockBlocker);
            if (body == null)
                return false;
            if (TryFindLoadedRockBlocker(spawnPos, spawnBounds, padding, body, out rock))
                return true;
            return TryFindUnloadedRockBlocker(spawnPos, spawnBounds, padding, ut, body, out rock);
        }

        private static bool TryFindLoadedRockBlocker(
            Vector3d spawnPos, Bounds spawnBounds, float padding, CelestialBody body,
            out SpawnRockBlocker rock)
        {
            rock = default(SpawnRockBlocker);
            ResolveSpawnOverlapBox(spawnPos, spawnBounds, padding,
                out Vector3 worldCenter, out Vector3 halfExtents, out Quaternion surfaceRot);
            Collider[] hits = Physics.OverlapBox(worldCenter, halfExtents, surfaceRot,
                PartOverlapLayerMask, QueryTriggerInteraction.Ignore);
            if (hits == null)
                return false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                Part part = FlightGlobals.GetPartUpwardsCached(hits[i].gameObject);
                Vessel v = part != null ? part.vessel : null;
                if (v == null || !v.loaded || v.orbit == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (!IsLiveRockPart(part)) continue;
                if (v.orbit.referenceBody != body) continue;
                rock = new SpawnRockBlocker
                {
                    Vessel = v,
                    Part = part,
                    Centre = part.transform.position,
                    Radius = ResolveLoadedRockRadius(part),
                    Loaded = true,
                    Name = Recording.ResolveLocalizedName(v.vesselName),
                };
                return true;
            }
            return false;
        }

        private static bool TryFindUnloadedRockBlocker(
            Vector3d spawnPos, Bounds spawnBounds, float padding, double ut, CelestialBody body,
            out SpawnRockBlocker rock)
        {
            rock = default(SpawnRockBlocker);
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return false;
            double boundsRadius = OrbitSpawnClearance.ResolveBoundsRadius(spawnBounds);
            double largestReach = RockHalfExtentForClass(VesselSnapshotOps.RockLargestSizeClass)
                + boundsRadius + padding;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.loaded || v.vesselType != VesselType.SpaceObject) continue;
                if (v.orbit == null || v.orbit.referenceBody != body) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                Vector3d centre = v.orbit.getPositionAtUT(ut);
                double distance = (spawnPos - centre).magnitude;
                if (distance >= largestReach || distance >= bestDistance) continue;
                double radius = RockHalfExtentForClass(ResolveProtoRockSizeClass(v.protoVessel));
                if (distance >= radius + boundsRadius + padding) continue;
                bestDistance = distance;
                rock = new SpawnRockBlocker
                {
                    Vessel = v,
                    Centre = centre,
                    Radius = radius,
                    Loaded = false,
                    Name = Recording.ResolveLocalizedName(v.vesselName),
                };
            }
            return rock.Vessel != null;
        }

        /// <summary>True when a live part is an asteroid or comet (by part name or module).</summary>
        internal static bool IsLiveRockPart(Part part)
        {
            if (part == null)
                return false;
            if (VesselSnapshotOps.IsRockPartName(part.partInfo != null ? part.partInfo.name : null))
                return true;
            if (part.Modules == null)
                return false;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module != null && VesselSnapshotOps.IsRockModuleName(module.moduleName))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Size class of an unloaded rock from its saved state: a rock part module's
        /// <c>prefabBaseURL</c>, else the vessel's <c>DISCOVERY size</c>, else the largest class.
        /// </summary>
        private static int ResolveProtoRockSizeClass(ProtoVessel pv)
        {
            if (pv != null && pv.protoPartSnapshots != null)
            {
                for (int p = 0; p < pv.protoPartSnapshots.Count; p++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[p];
                    if (pps == null || pps.modules == null) continue;
                    for (int m = 0; m < pps.modules.Count; m++)
                    {
                        ProtoPartModuleSnapshot module = pps.modules[m];
                        if (module == null || !VesselSnapshotOps.IsRockModuleName(module.moduleName)) continue;
                        string url = module.moduleValues != null ? module.moduleValues.GetValue("prefabBaseURL") : null;
                        if (VesselSnapshotOps.TryParseSizeClassFromPrefabUrl(url, out int fromUrl))
                            return fromUrl;
                    }
                }
            }
            string raw = pv != null && pv.discoveryInfo != null ? pv.discoveryInfo.GetValue("size") : null;
            if (!string.IsNullOrEmpty(raw)
                && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                && size >= 0 && size <= VesselSnapshotOps.RockLargestSizeClass)
                return size;
            return VesselSnapshotOps.RockLargestSizeClass;
        }

        /// <summary>
        /// Radius a loaded rock part reaches from its own position: the farthest corner of its
        /// colliders' world boxes, or its size class when it has no collider.
        /// </summary>
        private static double ResolveLoadedRockRadius(Part part)
        {
            Vector3d centre = part.transform.position;
            double radius = 0.0;
            Collider[] colliders = part.GetComponentsInChildren<Collider>();
            for (int i = 0; colliders != null && i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled) continue;
                Bounds b = c.bounds;
                double reach = ((Vector3d)b.center - centre).magnitude + ((Vector3d)b.extents).magnitude;
                if (reach > radius) radius = reach;
            }
            if (radius > 0.0)
                return radius;
            int sizeClass = VesselSnapshotOps.RockLargestSizeClass;
            for (int i = 0; part.Modules != null && i < part.Modules.Count; i++)
            {
                if (part.Modules[i] is ModuleAsteroid asteroid
                    && VesselSnapshotOps.TryParseSizeClassFromPrefabUrl(asteroid.prefabBaseURL, out int a))
                    sizeClass = a;
                else if (part.Modules[i] is ModuleComet comet
                    && VesselSnapshotOps.TryParseSizeClassFromPrefabUrl(comet.prefabBaseURL, out int c))
                    sizeClass = c;
            }
            return RockHalfExtentForClass(sizeClass);
        }

        /// <summary>True when the spawn box at <paramref name="spawnPos"/> touches a collider of <paramref name="rockPart"/>.</summary>
        private static bool OverlapsRockPart(Vector3d spawnPos, Bounds spawnBounds, float padding, Part rockPart)
        {
            if (rockPart == null)
                return false;
            ResolveSpawnOverlapBox(spawnPos, spawnBounds, padding,
                out Vector3 worldCenter, out Vector3 halfExtents, out Quaternion surfaceRot);
            Collider[] hits = Physics.OverlapBox(worldCenter, halfExtents, surfaceRot,
                PartOverlapLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; hits != null && i < hits.Length; i++)
            {
                if (hits[i] != null && FlightGlobals.GetPartUpwardsCached(hits[i].gameObject) == rockPart)
                    return true;
            }
            return false;
        }
    }
}
