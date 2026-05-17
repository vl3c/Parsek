using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Smoke tests for the Phase B.2 stock-action-intent Harmony patches. The
    /// full end-to-end behavior is exercised by in-game tests in
    /// <c>RuntimeTests.cs</c>; these xUnit smoke tests pin that the patch types
    /// load cleanly, that <c>[HarmonyPatch]</c> is wired, and that the gate
    /// predicates fall the way the plan requires. KSP's
    /// <c>FlightGlobals.SetActiveVessel</c> / live Harmony patching itself are
    /// covered only by in-game tests because they require Unity + KSP runtime
    /// state we cannot stand up under xUnit.
    /// </summary>
    public class SwitchIntentPatchSmokeTests
    {
        [Fact]
        public void TrackingStationFlyPatch_HasHarmonyPatchAttribute()
        {
            // Fails if: KSP renames or removes SpaceTracking.FlyVessel, the
            // [HarmonyPatch] attribute on SwitchIntentTrackingStationFlyPatch is
            // dropped, or the patch class is removed.
            var attrs = typeof(SwitchIntentTrackingStationFlyPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            var spaceTrackingType = typeof(KSP.UI.Screens.SpaceTracking);
            MethodInfo flyVessel = spaceTrackingType.GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(flyVessel);
        }

        [Fact]
        public void KscVesselMarkerFlyPatch_HasHarmonyPatchAttribute()
        {
            // Fails if: KSP renames or removes KSCVesselMarkers.FlyVessel(Vessel)
            // (the non-public handler the patch arms its intent on), or the
            // [HarmonyPatch] attribute is dropped.
            var attrs = typeof(KscVesselMarkerFlyPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            var kscType = typeof(KSP.UI.Screens.KSCVesselMarkers);
            MethodInfo flyVessel = kscType.GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Vessel) },
                modifiers: null);
            Assert.NotNull(flyVessel);
        }

        [Fact]
        public void MapFocusObjectOnSelectPatch_TargetMethodResolves()
        {
            // Fails if: KSP renames or moves
            // KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject.OnSelect
            // or its containing namespace, so the patch can no longer find its
            // target via reflection.
            var attrs = typeof(MapFocusObjectOnSelectPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            // Invoke the patch's static TargetMethod() helper directly.
            MethodInfo helper = typeof(MapFocusObjectOnSelectPatch).GetMethod(
                "TargetMethod",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var resolved = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(resolved);
            Assert.Equal("OnSelect", resolved.Name);
            Assert.Equal(
                typeof(KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject),
                resolved.DeclaringType);
        }

        [Theory]
        // isOwnedVesselMode, canSwitchVesselsFar, vesselNotNull, expected
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, false, false)]
        public void MapFocusObjectOnSelectPatch_ShouldArm_GateMatrix(
            bool isOwnedVesselMode,
            bool canSwitchVesselsFar,
            bool vesselNotNull,
            bool expected)
        {
            // Fails if: ShouldArmMapSwitchTo changes its truth table and one of
            // the three gates (FocusMode / CanSwitchVesselsFar / vessel) stops
            // being load-bearing for the arm decision.
            bool actual = MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode, canSwitchVesselsFar, vesselNotNull);
            Assert.Equal(expected, actual);
        }
    }
}
