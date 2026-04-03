using HarmonyLib;

namespace Parsek.Patches
{
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

            // Uses orbit-propagated altitude, not the ghost mesh altitude. The ghost mesh
            // lives on a separate GameObject (engine's ghostStates) that this Harmony patch
            // has no access to. Orbit altitude may diverge slightly from the ghost mesh
            // during reentry (Keplerian vs drag-affected), but for the atmosphere boundary
            // decision this is acceptable — the divergence is small near the boundary.
            if (body.atmosphere && __instance.vessel.orbit.altitude < body.atmosphereDepth)
            {
                // Below atmosphere: hide orbit line AND native icon.
                // The icon position tracks OrbitDriver propagation (Keplerian, no drag),
                // which diverges from the ghost mesh (recorded trajectory with drag).
                // Our custom DrawMapMarkers draws the correct icon at the ghost mesh pos.
                line.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
            }
            else
            {
                // Above atmosphere: show orbit line and all icons (vessel + Ap/Pe/AN/DN).
                __instance.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
            }
        }
    }
}
