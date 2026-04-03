using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Suppresses the orbit line for ghost map ProtoVessels when they are below the
    /// atmosphere. The map icon (vessel type icon) stays visible because DrawNodes()
    /// already ran before this postfix. The orbit line is only meaningful in vacuum —
    /// atmospheric drag makes the Keplerian approximation produce wild/flickering lines.
    ///
    /// Architecture: OrbitRendererBase.LateUpdate calls DrawOrbit() then DrawNodes().
    /// DrawOrbit sets OrbitLine.active = true and renders the line. DrawNodes renders
    /// the vessel icon (MapNode). This postfix runs after both, setting OrbitLine.active
    /// = false to suppress the line while the icon remains visible.
    ///
    /// Also hides Ap/Pe/AN/DN markers when the orbit line is hidden — they would float
    /// in space with no line connecting them.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "LateUpdate")]
    internal static class GhostOrbitLinePatch
    {
        static void Postfix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null)
                return;

            if (!GhostMapPresence.IsGhostMapVessel(__instance.vessel.persistentId))
                return;

            var line = __instance.OrbitLine;
            if (line == null)
                return;

            // Check if the ghost is below atmosphere — orbit line should be hidden.
            // Uses the orbit-derived altitude (OrbitDriver propagation), which is
            // approximately correct since we update the orbit every 0.5s.
            CelestialBody body = __instance.vessel.orbitDriver?.celestialBody;
            if (body == null)
                return;

            if (body.atmosphere && __instance.vessel.orbit.altitude < body.atmosphereDepth)
            {
                line.active = false;
                // Show only the vessel icon, hide Ap/Pe/AN/DN (they float without a line)
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
            }
            else
            {
                // Above atmosphere — ensure orbit line and full icons are enabled.
                // DrawOrbit already set line.active = true, but drawIcons may be
                // stale from a previous below-atmosphere frame.
                __instance.drawIcons = OrbitRendererBase.DrawIcons.ALL;
            }
        }
    }
}
