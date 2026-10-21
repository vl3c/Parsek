using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-4: stock's <c>[</c> / <c>]</c> vessel cycle switches with
    /// <c>FlightGlobals.ForceSetActiveVessel</c> to any LOADED vessel, and a ghost map marker
    /// inside physics range is loaded. Some Parsek patch must refuse that overload for ghosts,
    /// or a one-part marker becomes the active vessel and stock credits progress and contract
    /// parameters to a vessel that does not exist.
    /// </summary>
    [Collection("Sequential")]
    public class GhostVesselForceSwitchPatchTests : IDisposable
    {
        public GhostVesselForceSwitchPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static MethodInfo ForceSetActiveVessel()
        {
            return typeof(FlightGlobals).GetMethod(
                nameof(FlightGlobals.ForceSetActiveVessel), new[] { typeof(Vessel) });
        }

        /// <summary>Patch classes whose TargetMethod() names ForceSetActiveVessel(Vessel).</summary>
        private static Type[] PatchesTargetingForceSwitch()
        {
            MethodInfo target = ForceSetActiveVessel();
            return typeof(ParsekHarmony).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .Where(t =>
                {
                    MethodInfo tm = t.GetMethod("TargetMethod",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (tm == null || tm.GetParameters().Length != 0) return false;
                    try { return Equals(tm.Invoke(null, null), target); }
                    catch (Exception) { return false; }
                })
                .ToArray();
        }

        [Fact]
        public void SomePatch_TargetsForceSetActiveVessel()
        {
            Assert.NotNull(ForceSetActiveVessel());
            Type[] patches = PatchesTargetingForceSwitch();
            Assert.Single(patches);
            Assert.NotNull(patches[0].GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
        }

        [Fact]
        public void TheForceSwitchDecision_RefusesGhostMarkers_Only()
        {
            Type patch = PatchesTargetingForceSwitch().Single();
            MethodInfo decide = patch.GetMethod("ShouldRefuse",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(decide);

            GhostMapPresence.RegisterGhostMapVesselPidForTesting(4242u);
            Assert.True((bool)decide.Invoke(null, new object[] { 4242u }));
            Assert.False((bool)decide.Invoke(null, new object[] { 4243u }));
        }
    }
}
