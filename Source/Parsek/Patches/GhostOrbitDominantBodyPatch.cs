using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Stops KSP's on-rails SOI transition from hijacking a Parsek-owned ghost map
    /// vessel's orbit (the heliocentric transfer-line "blink").
    ///
    /// ROOT CAUSE (read from decompiled <c>OrbitDriver</c>, not inferred): every
    /// frame, <c>OrbitDriver.UpdateOrbit</c> calls
    /// <c>CheckDominantBody(referenceBody.position + pos)</c> (UPDATE mode, which all
    /// unloaded ghosts use; also called in the TRACK_Phys / IDLE modes with
    /// <c>vessel.CoMD</c>). <c>CheckDominantBody</c> runs
    /// <c>FlightGlobals.getMainBody(refPos)</c> and, if the dominant body differs from
    /// the current <c>referenceBody</c> and <c>!FlightGlobals.overrideOrbit</c>, calls
    /// <c>RecalculateOrbit -&gt; OnRailsSOITransition</c>, re-deriving the orbit from the
    /// state vector relative to the new body. For a re-aimed interplanetary ghost the
    /// loop-shifted heliocentric orbit resolves to a position KSP reads as inside the
    /// launch body's SOI, so KSP transitions Sun -&gt; Kerbin (a degenerate ecc=1 Kerbin
    /// hyperbola ~13.6 Gm out), Parsek reseeds Sun next frame, and the cycle repeats
    /// ~once per second: the map orbit line redraws on a different conic each pass.
    /// Captured in <c>logs/2026-06-01_1414_orbit-blink-postneutralize</c> as 45
    /// stock <c>[OrbitDriver]: On-Rails SOI Transition from Sun to Kerbin</c> lines, each
    /// followed by a probe body=Kerbin event + a 13.6 Gm icon JUMP, WHILE the
    /// neutralizer (clearing <c>patchEndTransition</c> / <c>nextPatch</c> / <c>EndUT</c>)
    /// was active - proving the trigger is the live position check, not a cached patch
    /// field.
    ///
    /// Parsek fully owns every ghost map vessel's orbit: it is re-applied every frame
    /// from the recording / re-aim resolver via
    /// <c>GhostMapPresence.ApplyOrbitToVessel</c> (or the state-vector reseed), and the
    /// orbit line / icon are computed from the current Keplerian elements alone. KSP
    /// must therefore not run its own SOI transition on these vessels. This Prefix skips
    /// <c>CheckDominantBody</c> for ghost vessels only (reporting <c>__result = false</c> =
    /// "no dominant-body change", so the caller does not transition). Real vessels are
    /// untouched: the gate is <c>GhostMapPresence.ghostMapVesselPids</c>.
    /// </summary>
    /// <summary>What the dominant-body check does for one OrbitDriver's vessel.</summary>
    internal enum DominantBodyCheckRoute
    {
        /// <summary>A real vessel (or a body's driver): stock runs, unobserved.</summary>
        RunStock,
        /// <summary>A ghost map vessel: skipped, Parsek owns its orbit.</summary>
        Skip,
        /// <summary>
        /// The camera drone (design-far-watch.md 6.3 step 2): stock runs, because the drone is
        /// the active vessel and a skipped check would leave stock's frame bookkeeping on the
        /// wrong body if the mover ever disagreed with getMainBody; the mover writes the drone
        /// at <c>FlightGlobals.getMainBody(ghost position)</c>, so the check should find
        /// nothing, and a stock SOI transition of the drone is logged as a Warn.
        /// </summary>
        RunObserved,
    }

    [HarmonyPatch(typeof(OrbitDriver), nameof(OrbitDriver.CheckDominantBody))]
    internal static class GhostOrbitDominantBodyPatch
    {
        /// <summary>
        /// Pure decision: should KSP's dominant-body SOI check be skipped for the
        /// vessel with this persistentId? True only for a Parsek ghost map vessel (a
        /// known ghost PID in <see cref="GhostMapPresence.ghostMapVesselPids"/>). Real
        /// vessels (and non-vessel celestial-body drivers, filtered by the null check
        /// in the Prefix) run stock. Pure over the static ghost-pid set.
        /// </summary>
        internal static bool ShouldSkipDominantBodyCheck(uint persistentId)
        {
            return GhostMapPresence.ghostMapVesselPids.Contains(persistentId);
        }

        /// <summary>
        /// The route for one vessel: a ghost map vessel is skipped, the camera drone runs
        /// stock observed (design-far-watch.md 6.3 step 2, step 4), everything else runs stock.
        /// A ghost pid wins over a drone pid (they never coincide; the ghost rule is the older one).
        /// </summary>
        internal static DominantBodyCheckRoute DecideDominantBodyCheck(bool isGhostMapVessel, bool isCameraDrone)
        {
            if (isGhostMapVessel)
                return DominantBodyCheckRoute.Skip;
            return isCameraDrone ? DominantBodyCheckRoute.RunObserved : DominantBodyCheckRoute.RunStock;
        }

        /// <summary>
        /// The drone line after an observed check: null when stock changed nothing (the expected
        /// case: the mover already wrote the drone at getMainBody), else the Warn text. A change
        /// is a stock on-rails SOI transition (onVesselSOIChanged fires for the drone), which FW-5
        /// forbids.
        /// </summary>
        internal static string DescribeObservedDroneCheck(bool changed, string fromBody, string toBody, uint pid)
        {
            if (!changed && string.Equals(fromBody, toBody, System.StringComparison.Ordinal))
                return null;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "stock SOI transition of the drone pid={0} from={1} to={2} (the mover writes the drone at getMainBody; the next tick rewrites it)",
                pid, fromBody ?? "(none)", toBody ?? "(none)");
        }

        static bool Prefix(OrbitDriver __instance, ref bool __result, out CelestialBody __state)
        {
            __state = null;
            Vessel v = __instance != null ? __instance.vessel : null;
            if (v == null)
                return true;
            DominantBodyCheckRoute route = DecideDominantBodyCheck(
                ShouldSkipDominantBodyCheck(v.persistentId), CameraDroneIdentity.IsDrone(v.persistentId));
            if (route == DominantBodyCheckRoute.RunStock)
                return true;
            if (route == DominantBodyCheckRoute.RunObserved)
            {
                __state = __instance.referenceBody;
                return true;
            }

            // Parsek-owned ghost: never let KSP transition its SOI. "false" tells the
            // caller (UpdateOrbit) the dominant body did not change, so it skips
            // RecalculateOrbit / OnRailsSOITransition. Parsek re-applies the correct
            // orbit + body every frame.
            __result = false;
            // Func<string> overload: this Prefix runs every physics tick per ghost, so
            // build the message only when the rate limiter actually emits (verbose is on
            // by default, so the eager-string overload would format every tick).
            uint pid = v.persistentId;
            ParsekLog.VerboseRateLimited("GhostMap",
                "dominant-body-suppressed-" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Suppressed KSP on-rails dominant-body SOI transition for ghost pid={0} (Parsek owns this orbit)",
                    pid),
                5.0);
            return false;
        }

        static void Postfix(OrbitDriver __instance, bool __result, CelestialBody __state)
        {
            if (__state == null || __instance == null || __instance.vessel == null)
                return;
            CelestialBody after = __instance.referenceBody;
            string line = DescribeObservedDroneCheck(__result, __state.bodyName,
                after != null ? after.bodyName : null, __instance.vessel.persistentId);
            if (line != null)
                ParsekLog.WarnRateLimited("CameraDrone", "drone-stock-soi-transition", line, 5.0);
        }
    }
}
