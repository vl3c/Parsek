using System;
using System.Globalization;
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
            double currentUT = Planetarium.GetUniversalTime();
            if (!GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
                return;

            Orbit orbit = __instance.orbit;
            if (orbit == null || orbit.eccentricity >= 1.0 || orbit.period <= 0) return;

            // Past recording bounds → clamp to endUT so icon sits at last recorded position
            if (currentUT > endUT)
            {
                orbit.UpdateFromUT(endUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }
            if (currentUT < startUT)
            {
                orbit.UpdateFromUT(startUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }

            // Within bounds — check if on the visible arc
            bool onArc = GhostOrbitArcCheck.IsOnOrbitalArc(orbit, startUT, endUT, currentUT);
            if (!onArc)
            {
                // Off the visible arc (underground) — clamp to the nearest arc endpoint
                double period = orbit.period;
                double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;
                double obtStart = ((orbit.getObtAtUT(startUT) % period) + period) % period;
                double obtEnd = ((orbit.getObtAtUT(endUT) % period) + period) % period;

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

                double clampUT = (distToEnd <= distToStart) ? endUT : startUT;
                orbit.UpdateFromUT(clampUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);

                ParsekLog.VerboseRateLimited("GhostOrbitIcon", "clamp-" + pid,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Icon clamped pid={0} UT={1:F1} clampUT={2:F1} bounds=[{3:F1},{4:F1}]",
                        pid, currentUT, clampUT, startUT, endUT));
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
        private const string Tag = "GhostOrbitLine";

        internal static string BuildGhostOrbitLineDecisionStateKey(
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            string reason,
            bool hasBounds)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "active={0}|icons={1}|suppressed={2}|reason={3}|bounds={4}",
                lineActive ? 1 : 0,
                drawIcons,
                iconSuppressed ? 1 : 0,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                hasBounds ? 1 : 0);
        }

        internal static string FormatGhostOrbitLineDecision(
            uint vesselPid,
            string reason,
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            bool belowAtmosphere,
            bool hasBounds,
            double currentUT,
            double startUT,
            double endUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Orbit line decision: pid={0} reason={1} lineActive={2} drawIcons={3} iconSuppressed={4} belowAtmosphere={5} hasBounds={6} currentUT={7:F1} bounds=[{8:F1},{9:F1}] scene={10}",
                vesselPid,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                lineActive,
                drawIcons,
                iconSuppressed,
                belowAtmosphere,
                hasBounds,
                currentUT,
                hasBounds ? startUT : double.NaN,
                hasBounds ? endUT : double.NaN,
                HighLogic.LoadedScene);
        }

        private static void LogOrbitLineDecision(
            uint vesselPid,
            string reason,
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            bool belowAtmosphere,
            bool hasBounds,
            double currentUT,
            double startUT,
            double endUT)
        {
            ParsekLog.VerboseOnChange(Tag,
                "pid-" + vesselPid.ToString(CultureInfo.InvariantCulture),
                BuildGhostOrbitLineDecisionStateKey(
                    lineActive,
                    drawIcons,
                    iconSuppressed,
                    reason,
                    hasBounds),
                FormatGhostOrbitLineDecision(
                    vesselPid,
                    reason,
                    lineActive,
                    drawIcons,
                    iconSuppressed,
                    belowAtmosphere,
                    hasBounds,
                    currentUT,
                    startUT,
                    endUT));
        }

        static void Postfix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null)
                return;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid))
                return;

            var line = __instance.OrbitLine;
            if (line == null)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "missing-line-" + pid.ToString(CultureInfo.InvariantCulture),
                    string.Format(CultureInfo.InvariantCulture,
                        "Orbit line decision skipped: pid={0} reason=missing-orbit-line scene={1}",
                        pid,
                        HighLogic.LoadedScene),
                    5.0);
                return;
            }

            // Atmosphere suppression — shared by both segment-based and terminal-orbit ghosts.
            // Below the atmosphere boundary, Keplerian orbits are meaningless (drag makes them
            // flicker wildly). Suppress the orbit line and icon, letting the trajectory-interpolated
            // atmospheric marker take over.
            CelestialBody body = __instance.driver?.referenceBody;
            bool belowAtmosphere = body != null && body.atmosphere
                && __instance.vessel.orbit != null
                && __instance.vessel.orbit.altitude < body.atmosphereDepth;

            // Segment-based ghosts: the orbit line is clipped by GhostOrbitArcPatch.
            // When UT is past the recording bounds, hide the line entirely.
            double currentUT = Planetarium.GetUniversalTime();
            if (GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
            {
                if (currentUT > endUT || currentUT < startUT || belowAtmosphere)
                {
                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    if (belowAtmosphere)
                        GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    string reason = belowAtmosphere
                        ? "below-atmosphere"
                        : (currentUT > endUT ? "past-segment-end" : "before-segment-start");
                    LogOrbitLineDecision(
                        pid,
                        reason,
                        line.active,
                        __instance.drawIcons,
                        GhostMapPresence.IsIconSuppressed(pid),
                        belowAtmosphere,
                        hasBounds: true,
                        currentUT,
                        startUT,
                        endUT);
                    return;
                }

                // On arc, above atmosphere — show line and vessel icon only (no Ap/Pe/AN/DN)
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                LogOrbitLineDecision(
                    pid,
                    "visible-segment",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: true,
                    currentUT,
                    startUT,
                    endUT);
                return;
            }

            // Non-segment ghosts (terminal orbits): atmosphere-based suppression
            if (belowAtmosphere)
            {
                line.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                LogOrbitLineDecision(
                    pid,
                    "terminal-below-atmosphere",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: false,
                    currentUT,
                    double.NaN,
                    double.NaN);
            }
            else
            {
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                LogOrbitLineDecision(
                    pid,
                    "terminal-visible",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: false,
                    currentUT,
                    double.NaN,
                    double.NaN);
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
            double currentUT = Planetarium.GetUniversalTime();
            if (!GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
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
            if (endUT - startUT >= orbit.period) return true;

            // Convert UT bounds to eccentric anomaly (same as PatchRendering/Trajectory)
            double fromE = orbit.EccentricAnomalyAtUT(startUT);
            double toE = orbit.EccentricAnomalyAtUT(endUT);

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
