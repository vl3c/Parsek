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
        /// <summary>A real vessel (or a body's driver): stock runs.</summary>
        RunStock,
        /// <summary>A ghost map vessel: skipped, Parsek owns its orbit.</summary>
        Skip,
        /// <summary>
        /// The camera drone (design-far-watch.md 6.3 step 2): skipped, because the far-watch
        /// mover owns the drone's body for as long as the drone exists. The drone is packed in
        /// UPDATE mode, so <c>VesselPrecalculate.FixedUpdate</c> -&gt; <c>OrbitDriver.UpdateOrbit</c>
        /// propagates the last written orbit and runs this check every tick, before
        /// <c>ParsekFlight.FixedUpdate</c>'s mover write; on the ghost's seam tick that orbit
        /// can be past the SOI boundary, and stock would move the drone (RecalculateOrbit,
        /// onVesselSOIChanged) ahead of the mover, which then sees no hop to clear the frame
        /// for. Refused, the drone keeps its body until the mover's cross-body write.
        /// </summary>
        SkipDrone,
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
        /// The route for one vessel: a ghost map vessel is skipped, the camera drone is skipped
        /// because the mover owns its body (design-far-watch.md 6.3 step 2, step 4), everything
        /// else runs stock. A ghost pid wins over a drone pid (they never coincide; the ghost
        /// rule is the older one).
        /// </summary>
        internal static DominantBodyCheckRoute DecideDominantBodyCheck(bool isGhostMapVessel, bool isCameraDrone)
        {
            if (isGhostMapVessel)
                return DominantBodyCheckRoute.Skip;
            return isCameraDrone ? DominantBodyCheckRoute.SkipDrone : DominantBodyCheckRoute.RunStock;
        }

        /// <summary>
        /// The drone line for a refused check: null when stock would change nothing (the mover
        /// already wrote the drone at getMainBody), else the Info text naming the body stock
        /// would have moved it to. The mover's next write makes the hop.
        /// </summary>
        internal static string DescribeRefusedDroneCheck(string referenceBody, string mainBody, uint pid)
        {
            if (string.Equals(referenceBody, mainBody, System.StringComparison.Ordinal))
                return null;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "drone dominant-body check refused pid={0} referenceBody={1} mainBody={2} (the far-watch mover makes the hop)",
                pid, referenceBody ?? "(none)", mainBody ?? "(none)");
        }

        static bool Prefix(OrbitDriver __instance, Vector3d refPos, ref bool __result)
        {
            Vessel v = __instance != null ? __instance.vessel : null;
            if (v == null)
                return true;
            DominantBodyCheckRoute route = DecideDominantBodyCheck(
                ShouldSkipDominantBodyCheck(v.persistentId), CameraDroneIdentity.IsDrone(v.persistentId));
            if (route == DominantBodyCheckRoute.RunStock)
                return true;

            // "false" tells the caller (UpdateOrbit) the dominant body did not change, so it
            // skips RecalculateOrbit / OnRailsSOITransition.
            __result = false;
            uint pid = v.persistentId;
            if (route == DominantBodyCheckRoute.SkipDrone)
            {
                // One vessel, so the main-body read is cheap; logged only when stock would act.
                CelestialBody main = FlightGlobals.getMainBody(refPos);
                CelestialBody reference = __instance.referenceBody;
                string line = DescribeRefusedDroneCheck(reference != null ? reference.bodyName : null,
                    main != null ? main.bodyName : null, pid);
                if (line != null)
                    ParsekLog.InfoRateLimited("CameraDrone", "drone-dominant-body-refused", line, 5.0);
                return false;
            }

            // Parsek-owned ghost: never let KSP transition its SOI. Parsek re-applies the
            // correct orbit + body every frame.
            // Func<string> overload: this Prefix runs every physics tick per ghost, so
            // build the message only when the rate limiter actually emits (verbose is on
            // by default, so the eager-string overload would format every tick).
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
