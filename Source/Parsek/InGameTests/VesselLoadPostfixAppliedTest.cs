using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Regression guard for the single-source-of-truth rb.mass seeder fix.
    ///
    /// <para>Background: this PR removed the inline
    /// <c>SeedRigidbodyMassesForPackedSpawn</c> calls from
    /// <c>VesselSpawner.SpawnAtPosition</c> and
    /// <c>VesselSpawner.RespawnVessel</c> so the Harmony postfix on
    /// <c>Vessel.Load()</c> is the only place rb.mass gets seeded before
    /// first Unpack. That made the postfix a single point of failure: if
    /// a future KSP update renames or re-signatures <c>Vessel.Load()</c>,
    /// the postfix silently stops applying and every loaded vessel
    /// cascade-explodes the way the PR #885 / PR #890 repros documented.
    ///
    /// <para>This test asserts:
    /// <list type="number">
    /// <item>The <c>Vessel.Load()</c> method the postfix targets still
    /// exists in the running KSP version (<c>AccessTools.Method</c>
    /// returns non-null), and</item>
    /// <item>The
    /// <see cref="VesselLoadRigidbodyMassSeederPatch"/> postfix is
    /// registered in <c>Harmony.GetPatchInfo</c> on that method.</item>
    /// </list>
    /// A failure here means the rb.mass seeder is no longer firing for any
    /// loaded vessel; the cascade-explode bug is back. This is the
    /// regression test that would have caught the original
    /// <c>ProtoVessel.Load</c> mis-targeting in PR #885 (every seeder log
    /// line showed <c>updated=0</c> because parts were not yet
    /// instantiated when the postfix fired).
    ///
    /// <para>Scene: SPACECENTER. Harmony patches are applied at
    /// <c>KSPAddon.Startup.Instantly</c>, so they are live in every
    /// scene; SPACECENTER is the cheapest place to run the assertion.
    /// </summary>
    public static class VesselLoadPostfixAppliedTest
    {
        [InGameTest(Category = "Spawner", Scene = GameScenes.SPACECENTER,
            Description = "Vessel.Load rb.mass seeder Harmony postfix is registered on the right method")]
        public static void VesselLoadPostfix_IsAppliedByHarmony()
        {
            MethodInfo loadMethod = AccessTools.Method(typeof(Vessel), "Load", new System.Type[0]);

            InGameAssert.IsNotNull(loadMethod,
                "AccessTools.Method could not find Vessel.Load(). KSP may have " +
                "renamed or re-signatured the method; update " +
                "VesselLoadRigidbodyMassSeederPatch.PostfixLoad's [HarmonyPatch] " +
                "target before any loaded vessel cascade-explodes.");

            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(loadMethod);

            bool hasOurPostfix = patchInfo != null
                && patchInfo.Postfixes != null
                && patchInfo.Postfixes.Any(p =>
                    p.PatchMethod != null
                    && p.PatchMethod.DeclaringType == typeof(VesselLoadRigidbodyMassSeederPatch));

            InGameAssert.IsTrue(hasOurPostfix,
                "VesselLoadRigidbodyMassSeederPatch postfix is NOT in " +
                "Harmony.GetPatchInfo(Vessel.Load).Postfixes (patchInfo=" +
                (patchInfo == null ? "null" : "non-null") +
                "). The rb.mass seeder will not fire and every save-load " +
                "reconstructed vessel will cascade-explode at first Unpack " +
                "(PR #885 / PR #890 repros).");

            int postfixCount = patchInfo != null && patchInfo.Postfixes != null
                ? patchInfo.Postfixes.Count : 0;
            ParsekLog.Info("TestRunner",
                $"VesselLoad postfix applied check: method found, " +
                $"postfix count={postfixCount}, ours present={hasOurPostfix}");
        }
    }
}
