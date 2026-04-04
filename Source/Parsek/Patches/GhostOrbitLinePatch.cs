using System;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents KSP from destroying ghost ProtoVessels due to on-rails atmospheric
    /// pressure. Ghost ProtoVessels orbit through the atmosphere (e.g., deorbit orbit
    /// with sub-surface periapsis). KSP's Vessel.CheckKill() destroys on-rails vessels
    /// at > 1 kPa pressure. If the PlanetariumCamera is focused on the ghost, Die()
    /// nulls the camera target → cascade of NullRefs → planet disappears.
    /// </summary>
    [HarmonyPatch(typeof(Vessel), "CheckKill")]
    internal static class GhostCheckKillPatch
    {
        static bool Prefix(Vessel __instance)
        {
            if (GhostMapPresence.IsGhostMapVessel(__instance.persistentId))
                return false; // skip CheckKill entirely for ghost vessels
            return true;
        }
    }

    /// <summary>
    /// Clamps ghost vessel OrbitDriver propagation to the visible arc of the orbit
    /// segment (#212b). Without this, the OrbitDriver propagates the vessel along
    /// the full Keplerian ellipse, positioning the icon underground during the
    /// periapsis passage. The orbit LINE is clipped by GhostOrbitArcPatch, but the
    /// vessel ICON follows the OrbitDriver position — this patch keeps the icon
    /// on the visible arc by clamping the propagated UT.
    ///
    /// When the vessel would be on the underground portion, the UT is clamped to
    /// the nearest arc endpoint (startUT or endUT), freezing the icon at the edge
    /// of the visible arc.
    ///
    /// Architecture: OrbitDriver.updateFromParameters() calls orbit.UpdateFromUT(UT)
    /// which sets the vessel's world position. This prefix intercepts the UT before
    /// propagation and clamps it to the visible arc if needed.
    /// </summary>
    [HarmonyPatch(typeof(OrbitDriver), "updateFromParameters", new Type[0])]
    internal static class GhostOrbitIconClampPatch
    {
        static void Prefix(OrbitDriver __instance)
        {
            if (__instance.vessel == null) return;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid)) return;

            // State-vector ghosts: update orbit from trajectory data every frame.
            // This runs BEFORE updateFromParameters, so the OrbitDriver uses the
            // correct position. No orbit line (suppressed by GhostOrbitArcPatch).
            if (GhostMapPresence.stateVectorGhostData.TryGetValue(pid, out var svData))
            {
                var committed = RecordingStore.CommittedRecordings;
                if (committed != null && svData.recordingIndex >= 0 && svData.recordingIndex < committed.Count)
                {
                    var rec = committed[svData.recordingIndex];
                    double ut = Planetarium.GetUniversalTime();
                    int cached = svData.cachedPointIndex;
                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(rec.Points, ut, ref cached);
                    GhostMapPresence.stateVectorGhostData[pid] = (svData.recordingIndex, cached);

                    if (pt.HasValue)
                    {
                        CelestialBody body = GhostMapPresence.FindBodyByNamePublic(pt.Value.bodyName);
                        if (body != null)
                        {
                            Vector3d worldPos = body.GetWorldSurfacePosition(
                                pt.Value.latitude, pt.Value.longitude, pt.Value.altitude);
                            Vector3d vel = new Vector3d(
                                pt.Value.velocity.x, pt.Value.velocity.y, pt.Value.velocity.z);
                            __instance.orbit.UpdateFromStateVectors(worldPos, vel, body, ut);
                        }
                    }
                }
                return; // let original updateFromParameters proceed with corrected orbit
            }

            if (!GhostMapPresence.ghostOrbitBounds.TryGetValue(pid, out var bounds)) return;

            Orbit orbit = __instance.orbit;
            if (orbit == null || orbit.eccentricity >= 1.0 || orbit.period <= 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            // Past recording bounds → clamp to endUT so icon sits at last recorded position
            if (currentUT > bounds.endUT)
            {
                orbit.UpdateFromUT(bounds.endUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }
            if (currentUT < bounds.startUT)
            {
                orbit.UpdateFromUT(bounds.startUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }

            // Within bounds — check if on the visible arc
            bool onArc = GhostOrbitArcCheck.IsOnOrbitalArc(orbit, bounds.startUT, bounds.endUT, currentUT);
            if (!onArc)
            {
                // Off the visible arc (underground) — clamp to the nearest arc endpoint
                double period = orbit.period;
                double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;
                double obtStart = ((orbit.getObtAtUT(bounds.startUT) % period) + period) % period;
                double obtEnd = ((orbit.getObtAtUT(bounds.endUT) % period) + period) % period;

                // Pick the closer endpoint in orbital-time space
                double distToStart, distToEnd;
                if (obtStart <= obtEnd)
                {
                    // No wraparound: gap is [obtEnd, obtStart+period]
                    distToStart = (obtStart - obtNow + period) % period;
                    distToEnd = (obtNow - obtEnd + period) % period;
                }
                else
                {
                    // Wraparound: gap is [obtEnd, obtStart]
                    distToStart = (obtNow <= obtStart) ? (obtStart - obtNow) : (obtStart + period - obtNow);
                    distToEnd = (obtNow >= obtEnd) ? (obtNow - obtEnd) : (obtNow + period - obtEnd);
                }

                double clampUT = (distToEnd <= distToStart) ? bounds.endUT : bounds.startUT;
                orbit.UpdateFromUT(clampUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);

                ParsekLog.VerboseRateLimited("GhostOrbitIcon", "clamp-" + pid,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Icon clamped pid={0} UT={1:F1} clampUT={2:F1} bounds=[{3:F1},{4:F1}]",
                        pid, currentUT, clampUT, bounds.startUT, bounds.endUT));
                return;
            }

            GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
        }
    }

    /// <summary>
    /// Suppresses the orbit line for ghost map ProtoVessels below atmosphere.
    /// The orbit line is meaningless during atmospheric flight (drag makes Keplerian
    /// propagation diverge from recorded trajectory).
    ///
    /// Architecture: OrbitRendererBase.LateUpdate calls DrawOrbit() then DrawNodes().
    /// This postfix runs after both, suppressing the Vectrosity line for ghost vessels.
    /// Icon clamping is handled separately by GhostOrbitIconClampPatch on OrbitDriver.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "LateUpdate")]
    internal static class GhostOrbitLinePatch
    {
        static void Postfix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null)
                return;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid))
                return;

            var line = __instance.OrbitLine;
            if (line == null)
                return;

            // Segment-based ghosts: the orbit line is clipped by GhostOrbitArcPatch.
            // When UT is past the recording bounds, hide the line entirely.
            if (GhostMapPresence.ghostOrbitBounds.TryGetValue(pid, out var timeBounds))
            {
                double currentUT = Planetarium.GetUniversalTime();
                if (currentUT > timeBounds.endUT || currentUT < timeBounds.startUT)
                {
                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    return;
                }

                // On arc — show line and vessel icon only (no Ap/Pe/AN/DN)
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                return;
            }

            // Non-segment ghosts (terminal orbits): atmosphere-based suppression only
            CelestialBody body = __instance.driver?.referenceBody;
            if (body != null && body.atmosphere && __instance.vessel.orbit != null
                && __instance.vessel.orbit.altitude < body.atmosphereDepth)
            {
                line.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
            }
            else
            {
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
            }
        }
    }

    /// <summary>
    /// Pure: is the vessel currently on the visible orbital arc between startUT and endUT?
    /// Uses orbital time (getObtAtUT) which is monotonically increasing within a period,
    /// avoiding the sign mismatch bugs in eccentric/true anomaly (#212b).
    /// </summary>
    internal static class GhostOrbitArcCheck
    {
        internal static bool IsOnOrbitalArc(Orbit orbit, double startUT, double endUT, double currentUT)
        {
            double period = orbit.period;
            if (period <= 0) return true; // degenerate orbit — show by default

            // Normalize all orbital times to [0, period)
            double obtStart = ((orbit.getObtAtUT(startUT) % period) + period) % period;
            double obtEnd = ((orbit.getObtAtUT(endUT) % period) + period) % period;
            double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;

            // If the arc spans the full period (or more), the vessel is always on arc
            if (endUT - startUT >= period) return true;

            // Standard range check with wraparound handling
            if (obtStart <= obtEnd)
                return obtNow >= obtStart && obtNow <= obtEnd;
            else
                return obtNow >= obtStart || obtNow <= obtEnd;
        }
    }

    /// <summary>
    /// Clips the ghost orbit line to only show the arc between the orbit segment's
    /// startUT and endUT, instead of the full Keplerian ellipse. Without this patch,
    /// suborbital ghosts show orbit lines passing through the planet surface.
    ///
    /// Uses the same eccentric anomaly arc-clipping logic as KSP's own
    /// PatchRendering / Trajectory.UpdateFromOrbit() — proven, battle-tested.
    ///
    /// Only applies to segment-based ghosts (those with entries in ghostOrbitBounds).
    /// Terminal-orbit ghosts (stable orbits) render the stock full ellipse.
    /// Hyperbolic orbits and full-period segments fall through to stock rendering.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "UpdateSpline")]
    internal static class GhostOrbitArcPatch
    {
        private const string Tag = "GhostOrbitArc";

        static bool Prefix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null) return true;
            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid)) return true;

            // State-vector ghosts: suppress orbit line entirely (atmospheric trajectory).
            if (GhostMapPresence.stateVectorGhostPids.Contains(pid))
            {
                var svLine = __instance.OrbitLine;
                if (svLine != null) svLine.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                return false;
            }

            if (!GhostMapPresence.ghostOrbitBounds.TryGetValue(pid, out var bounds))
            {
                ParsekLog.VerboseRateLimited(Tag, "nobounds-" + pid,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Ghost pid={0} has NO orbit bounds — stock full ellipse (scene={1})",
                        pid, HighLogic.LoadedScene));
                return true;
            }

            Orbit orbit = __instance.driver?.orbit;
            if (orbit == null) return true;
            if (orbit.eccentricity >= 1.0) return true; // let stock handle hyperbolic

            // Full orbit or more — let stock draw the complete ellipse
            if (bounds.endUT - bounds.startUT >= orbit.period) return true;

            // Convert UT bounds to eccentric anomaly (same as PatchRendering/Trajectory)
            double fromE = orbit.EccentricAnomalyAtUT(bounds.startUT);
            double toE = orbit.EccentricAnomalyAtUT(bounds.endUT);

            // NaN guard — degenerate orbits or UT outside validity
            if (double.IsNaN(fromE) || double.IsNaN(toE)) return true;

            // Handle wraparound (periapsis crossing) — same logic as Trajectory.UpdateFromOrbit.
            // GetTrueAnomaly returns [0, 2pi] for E in [0, 2pi). When fromV > toV, the arc
            // wraps through periapsis (V=0). Making fromE negative creates a monotonically
            // increasing range that crosses E=0.
            double fromV = orbit.GetTrueAnomaly(fromE);
            double toV = orbit.GetTrueAnomaly(toE);
            if (fromV > toV)
                fromE = -(Math.PI * 2.0 - fromE);

            // Sample the partial arc across all available points
            var orbitPoints = __instance.OrbitPoints;
            double semiMinorAxis = orbit.semiMinorAxis;
            int count = orbitPoints.Length; // 180 at stock sampleResolution=2.0
            double interval = (toE - fromE) / (count - 1);

            for (int i = 0; i < count; i++)
                orbitPoints[i] = orbit.getPositionFromEccAnomalyWithSemiMinorAxis(
                    fromE + interval * (double)i, semiMinorAxis);

            // Convert to scaled space and set draw range (open arc, no loop closing)
            var line = __instance.OrbitLine;
            ScaledSpace.LocalToScaledSpace(orbitPoints, line.points3);

            // Defensive: zero the stale closing point (index 180) and set draw range
            if (line.points3.Count > count)
                line.points3[count] = line.points3[count - 1];
            line.drawStart = 0;
            line.drawEnd = count - 1; // 179 — open arc, same as stock hyperbolic

            // Diagnostic: compute altitude at arc endpoints for verification
            double bodyRadius = orbit.referenceBody != null ? orbit.referenceBody.Radius : 0;
            double startR = orbit.semiMajorAxis * (1.0 - orbit.eccentricity * Math.Cos(fromE));
            double endR = orbit.semiMajorAxis * (1.0 - orbit.eccentricity * Math.Cos(toE));

            ParsekLog.VerboseRateLimited(Tag, pid.ToString(),
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Arc clip pid={0} fromE={1:F3} toE={2:F3} arc={3:F1}deg " +
                    "startAlt={4:F0} endAlt={5:F0} bodyR={6:F0} ecc={7:F4} sma={8:F0} " +
                    "drawEnd={9} pts={10} scene={11}",
                    pid, fromE, toE,
                    (toE - fromE) * (180.0 / Math.PI),
                    startR - bodyRadius, endR - bodyRadius, bodyRadius,
                    orbit.eccentricity, orbit.semiMajorAxis,
                    line.drawEnd, count,
                    HighLogic.LoadedScene));

            return false; // skip original UpdateSpline
        }
    }
}
