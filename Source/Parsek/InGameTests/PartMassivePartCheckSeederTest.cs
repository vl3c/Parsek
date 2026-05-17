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
            Description = "Part.MassivePartCheck seeder has mutated at least one part rb.mass since process start")]
        public static void SeededPartCount_IsPositiveInFlight()
        {
            int count = PartMassivePartCheckSeederPatch.SeededPartCount;

            InGameAssert.IsTrue(count > 0,
                $"PartMassivePartCheckSeederPatch.SeededPartCount is {count}. " +
                "Expected > 0 by the time a FLIGHT-scene test runs: the active " +
                "vessel (and any in-range vessels) should have triggered " +
                "Part.Start -> UpdateAutoStrut -> MassivePartCheck on any " +
                "ForceHeaviest autostrut, and the prefix should have seeded at " +
                "least one packed rb.mass before the read. A zero counter means " +
                "the prefix is registered but its body is a no-op (wrong gate, " +
                "missing rb, or wrong-method target).");

            ParsekLog.Info("TestRunner",
                $"MassivePartCheck seeder mutation check: SeededPartCount={count}");
        }
    }
}
