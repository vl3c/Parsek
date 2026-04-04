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
    /// Suppresses the orbit line AND native icon for ghost map ProtoVessels when
    /// below atmosphere. The orbit line is meaningless during atmospheric flight
    /// (drag makes Keplerian propagation diverge from recorded trajectory). The
    /// native icon is also hidden because its position comes from OrbitDriver
    /// propagation — which drifts far from the ghost mesh during reentry.
    ///
    /// When both are hidden, ParsekUI.DrawMapMarkers draws our custom vessel-type
    /// icon at the ghost mesh position (always correct). Tracked via
    /// GhostMapPresence.ghostsWithSuppressedIcon so DrawMapMarkers knows to draw.
    ///
    /// Architecture: OrbitRendererBase.LateUpdate calls DrawOrbit() then DrawNodes().
    /// This postfix runs after both, suppressing the Vectrosity line and all MapNode
    /// icons for ghost vessels below atmosphere.
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

            CelestialBody body = __instance.vessel.orbitDriver?.celestialBody;
            if (body == null)
                return;

            // Segment-based ghosts: hide orbit line AND icon when the vessel is
            // outside the visible arc. The OrbitDriver propagates the vessel along
            // the full Keplerian ellipse — including the underground periapsis passage.
            // The orbit LINE is clipped by GhostOrbitArcPatch, but the vessel ICON
            // follows OrbitDriver and goes through the planet. This check hides
            // everything when the vessel is off the visible arc (#212).
            if (GhostMapPresence.ghostOrbitBounds.TryGetValue(pid, out var timeBounds))
            {
                Orbit orbit = __instance.vessel.orbit;
                double currentUT = Planetarium.GetUniversalTime();

                // Past the recording end or before start → hide everything
                if (currentUT > timeBounds.endUT || currentUT < timeBounds.startUT)
                {
                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    return;
                }

                // Within the segment time range — but the vessel may be on the
                // underground portion of the orbit (periapsis passage). Check if
                // the vessel's current eccentric anomaly is within the visible arc.
                if (orbit != null && orbit.eccentricity < 1.0)
                {
                    double fromE = orbit.EccentricAnomalyAtUT(timeBounds.startUT);
                    double toE = orbit.EccentricAnomalyAtUT(timeBounds.endUT);
                    double curE = orbit.EccentricAnomalyAtUT(currentUT);

                    if (!double.IsNaN(fromE) && !double.IsNaN(toE) && !double.IsNaN(curE))
                    {
                        // Normalize wraparound using true anomaly (same as GhostOrbitArcPatch)
                        double fromV = orbit.GetTrueAnomaly(fromE);
                        double toV = orbit.GetTrueAnomaly(toE);
                        double curV = orbit.GetTrueAnomaly(curE);
                        if (fromV > toV)
                        {
                            fromV = -(System.Math.PI * 2.0 - fromV);
                            curV = curV > toV ? -(System.Math.PI * 2.0 - curV) : curV;
                        }

                        if (curV < fromV || curV > toV)
                        {
                            // Off the visible arc — hide line and icon
                            line.active = false;
                            __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                            GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                            return;
                        }
                    }
                }

                // On the visible arc — show line and vessel icon only (no Ap/Pe)
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                return;
            }

            // Non-segment ghosts (terminal orbits): atmosphere-based suppression only
            if (body.atmosphere && __instance.vessel.orbit.altitude < body.atmosphereDepth)
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
