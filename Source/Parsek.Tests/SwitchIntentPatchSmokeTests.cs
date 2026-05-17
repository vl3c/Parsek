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

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_ReturnsNoPriorSession()
        {
            // Fails if: the pre-switch decision helper opens a dialog when
            // no SwitchSegmentSession is armed. The regular arm-and-skip
            // flow must run unchanged in that case (rapid-switch
            // interception only triggers when there's a prior session).
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_DifferentTarget_OpensDialog()
        {
            // Fails if: a Switch-To to a different vessel while a prior
            // session is armed does NOT open the pre-switch dialog. This
            // is the rapid-switch case the dialog is for - Bug A/B from
            // logs/2026-05-17_1805_switch-fly-post-scene-discard-bug.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 100u,
                newTargetPid: 200u,
                anotherDialogOpen: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_SameTarget_SkipsDialog()
        {
            // Fails if: a duplicate Switch-To on the same vessel as the
            // active session opens a redundant dialog. The consume
            // helper's `duplicate-intent-same-target` branch already
            // handles same-target clicks; opening a dialog would be
            // confusing UX.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 200u,
                newTargetPid: 200u,
                anotherDialogOpen: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogSameTarget, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_AnotherDialogOpen_SkipsDialogReEntry()
        {
            // Fails if: a Switch-To click while another merge dialog is
            // already open spawns a second dialog on top. The re-entry
            // guard must defer to the existing dialog so the player
            // resolves it first.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 100u,
                newTargetPid: 200u,
                anotherDialogOpen: true);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogReEntry, actual);
        }
    }
}
