using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Regression guards for the <see cref="PartMassivePartCheckSeederPatch"/>.
    ///
    /// <para>Two tests, each catching a different failure mode the prior
    /// iterations of this fix exhibited:
    /// <list type="number">
    /// <item><c>Patch_IsAppliedByHarmony</c> catches a wrong-target
    /// regression: if a future KSP update renames or re-signatures
    /// <c>Part.MassivePartCheck</c>, Harmony cannot apply the prefix and
    /// every loaded vessel can cascade-explode at first Unpack.</item>
    /// <item><c>SeededPartCount_IsPositiveInFlight</c> catches a
    /// no-op-but-applied regression: if the prefix runs but its body
    /// silently skips every part (because of a wrong gate, a missing
    /// rigidbody, or a coroutine timing assumption), the
    /// <see cref="PartMassivePartCheckSeederPatch.SeededPartCount"/>
    /// counter remains zero. This is the assertion that would have caught
    /// PR #885's two-month silent no-op and the first two iterations of
    /// PR #890 (where the seeder loop iterated over an empty parts list
    /// or skipped every part on the <c>rb == null</c> guard).</item>
    /// </list>
    ///
    /// <para><b>Scene rationale.</b> Both tests run in FLIGHT: the patch
    /// applies at <c>KSPAddon.Startup.Instantly</c>, so it is live in
    /// every scene, but the second test needs FLIGHT because that is
    /// where <c>Part.MassivePartCheck</c> actually fires (every active /
    /// in-range vessel reconstruction runs Part.Start, which runs
    /// UpdateAutoStrut, which calls MassivePartCheck for any
    /// ForceHeaviest autostrut). SPACECENTER does not exercise the read
    /// site.
    /// </summary>
    public static class PartMassivePartCheckSeederTest
    {
        [InGameTest(Category = "Spawner", Scene = GameScenes.FLIGHT,
            Description = "Part.MassivePartCheck rb.mass seeder Harmony prefix is registered on the right method")]
        public static void Patch_IsAppliedByHarmony()
        {
            MethodInfo target = AccessTools.Method(typeof(Part), "MassivePartCheck");

            InGameAssert.IsNotNull(target,
                "AccessTools.Method could not find Part.MassivePartCheck. KSP may " +
                "have renamed or re-signatured the method; update " +
                "PartMassivePartCheckSeederPatch's [HarmonyPatch] target before " +
                "any loaded vessel cascade-explodes.");

            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(target);
            bool hasOurPrefix = patchInfo != null
                && patchInfo.Prefixes != null
                && patchInfo.Prefixes.Any(p =>
                    p.PatchMethod != null
                    && p.PatchMethod.DeclaringType == typeof(PartMassivePartCheckSeederPatch));

            InGameAssert.IsTrue(hasOurPrefix,
                "PartMassivePartCheckSeederPatch prefix is NOT in " +
                "Harmony.GetPatchInfo(Part.MassivePartCheck).Prefixes (patchInfo=" +
                (patchInfo == null ? "null" : "non-null") +
                "). The rb.mass seeder will not fire and ForceHeaviest autostruts " +
                "can misanchor leading to cascade-explode at first Unpack.");

            int prefixCount = patchInfo != null && patchInfo.Prefixes != null
                ? patchInfo.Prefixes.Count : 0;
            ParsekLog.Info("TestRunner",
                $"MassivePartCheck prefix applied check: method found, " +
                $"prefix count={prefixCount}, ours present={hasOurPrefix}");
        }

        [InGameTest(Category = "Spawner", Scene = GameScenes.FLIGHT,
            Description = "Part.MassivePartCheck seeder has mutated at least one part rb.mass since process start (requires an autostrut-bearing vessel in scene)")]
        public static void SeededPartCount_IsPositiveInFlight()
        {
            // Precondition: MassivePartCheck only fires for parts whose
            // autoStrutMode != Off. If no in-range vessel has any
            // autostrut-bearing parts (single-stage probes, a vanilla
            // Stayputnik launch, an EVA-only scene), the prefix never gets
            // invoked and SeededPartCount stays 0. Skip in that case rather
            // than spuriously failing on what is genuinely the absence of
            // a code-path exercise.
            bool sceneHasAutostrutCandidate = SceneHasAnyAutoStrutCandidate();
            if (!sceneHasAutostrutCandidate)
            {
                InGameAssert.Skip(
                    "No vessel in physics range has any part with autoStrutMode != Off. " +
                    "MassivePartCheck does not fire on autostrut-Off parts, so SeededPartCount " +
                    "cannot be exercised. Re-run with a multi-part vessel (e.g., stock Kerbal X) " +
                    "in physics range to exercise the prefix.");
                return;
            }

            int count = PartMassivePartCheckSeederPatch.SeededPartCount;
            InGameAssert.IsTrue(count > 0,
                $"PartMassivePartCheckSeederPatch.SeededPartCount is {count}. " +
                "An autostrut-bearing vessel is in scene so MassivePartCheck should " +
                "have fired during Part.Start's UpdateAutoStrut chain, and the prefix " +
                "should have seeded at least one packed rb.mass before the read. A zero " +
                "counter here means the prefix is registered but its body is a no-op " +
                "(wrong gate, missing rb, or wrong-method target).");

            ParsekLog.Info("TestRunner",
                $"MassivePartCheck seeder mutation check: SeededPartCount={count}, " +
                $"sceneHasAutostrutCandidate=True");
        }

        // Walks every in-range vessel's part list and returns true if any
        // part has autoStrutMode != AutoStrutMode.Off. Used by the
        // SeededPartCount test as a precondition check.
        private static bool SceneHasAnyAutoStrutCandidate()
        {
            if (FlightGlobals.VesselsLoaded == null) return false;
            for (int v = 0; v < FlightGlobals.VesselsLoaded.Count; v++)
            {
                Vessel vessel = FlightGlobals.VesselsLoaded[v];
                if (vessel == null || vessel.parts == null) continue;
                for (int p = 0; p < vessel.parts.Count; p++)
                {
                    Part part = vessel.parts[p];
                    if (part == null) continue;
                    if (part.autoStrutMode != Part.AutoStrutMode.Off) return true;
                }
            }
            return false;
        }
    }
}
