using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Regression guard for the single-source-of-truth rb.mass seeder fix.
    ///
    /// <para>Background: PR #890 removed the inline
    /// <c>SeedRigidbodyMassesForPackedSpawn</c> calls from
    /// <c>VesselSpawner.SpawnAtPosition</c> and
    /// <c>VesselSpawner.RespawnVessel</c> so the Harmony postfix on
    /// <c>ProtoVessel.Load</c> is the only place rb.mass gets seeded before
    /// first Unpack. That made the postfix a single point of failure: if a
    /// future KSP update renames or re-signatures
    /// <c>ProtoVessel.Load(FlightState, Vessel)</c>, the postfix silently
    /// stops applying and every loaded vessel cascade-explodes the way the
    /// PR #885 / PR #890 repros documented.
    ///
    /// <para>This test asserts:
    /// <list type="number">
    /// <item>The exact <c>ProtoVessel.Load(FlightState, Vessel)</c> overload
    /// the postfix targets still exists in the running KSP version
    /// (<c>AccessTools.Method</c> returns non-null), and</item>
    /// <item>The
    /// <see cref="ProtoVesselLoadRigidbodyMassSeederPatch"/> postfix is
    /// registered in <c>Harmony.GetPatchInfo</c> on that method.</item>
    /// </list>
    /// A failure here means the rb.mass seeder is no longer firing for any
    /// loaded vessel; the cascade-explode bug is back.
    ///
    /// <para>Scene: SPACECENTER. Harmony patches are applied at
    /// <c>KSPAddon.Startup.Instantly</c>, so they are live in every scene;
    /// SPACECENTER is the cheapest place to run the assertion.
    /// </summary>
    public static class ProtoVesselLoadPostfixAppliedTest
    {
        [InGameTest(Category = "Spawner", Scene = GameScenes.SPACECENTER,
            Description = "ProtoVessel.Load rb.mass seeder Harmony postfix is registered on the right method")]
        public static void ProtoVesselLoadPostfix_IsAppliedByHarmony()
        {
            MethodInfo loadMethod = AccessTools.Method(
                typeof(ProtoVessel),
                "Load",
                new[] { typeof(FlightState), typeof(Vessel) });

            InGameAssert.IsNotNull(loadMethod,
                "AccessTools.Method could not find ProtoVessel.Load(FlightState, Vessel). " +
                "KSP may have renamed or re-signatured the method; update " +
                "ProtoVesselLoadRigidbodyMassSeederPatch.PostfixLoad's [HarmonyPatch] " +
                "target before any loaded vessel cascade-explodes.");

            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(loadMethod);
            InGameAssert.IsNotNull(patchInfo,
                "Harmony reports no patches on ProtoVessel.Load(FlightState, Vessel). " +
                "ProtoVesselLoadRigidbodyMassSeederPatch must have failed to apply at " +
                "ParsekHarmony startup; check [Parsek][ERROR][Harmony] in KSP.log.");

            bool hasOurPostfix = patchInfo.Postfixes.Any(p =>
                p.PatchMethod != null
                && p.PatchMethod.DeclaringType == typeof(ProtoVesselLoadRigidbodyMassSeederPatch));

            InGameAssert.IsTrue(hasOurPostfix,
                "ProtoVesselLoadRigidbodyMassSeederPatch postfix is NOT in " +
                "Harmony.GetPatchInfo(ProtoVessel.Load).Postfixes. The rb.mass seeder " +
                "will not fire and every save-load reconstructed vessel will " +
                "cascade-explode at first Unpack (PR #885 / PR #890 repros).");

            ParsekLog.Info("TestRunner",
                $"ProtoVesselLoad postfix applied check: method found, " +
                $"postfix count={patchInfo.Postfixes.Count}, " +
                $"ours present={hasOurPostfix}");
        }
    }
}
