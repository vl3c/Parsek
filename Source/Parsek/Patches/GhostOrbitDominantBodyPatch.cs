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

        static bool Prefix(OrbitDriver __instance, ref bool __result)
        {
            Vessel v = __instance != null ? __instance.vessel : null;
            if (v == null || !ShouldSkipDominantBodyCheck(v.persistentId))
                return true; // not a ghost — run stock CheckDominantBody

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
    }
}
