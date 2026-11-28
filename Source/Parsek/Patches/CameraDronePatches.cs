using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// The far-watch switch token (design-far-watch.md 7.3): while the camera drone exists, every
    /// active-vessel switch Parsek did not start is refused, so no path reaches stock's unloaded
    /// branch with the drone up. Patched on the private <c>FlightGlobals.setActiveVessel(Vessel,
    /// bool, bool)</c>, which <c>SetActiveVessel</c> (both overloads) and <c>ForceSetActiveVessel</c>
    /// all call. The one allowed outside switch is to the player ship itself while the drone is
    /// dead or dying (stock <c>FlightLogger.AttemptVesselSwitch</c> after a drone death, 7.6).
    /// </summary>
    [HarmonyPatch]
    internal static class CameraDroneSwitchTokenPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(FlightGlobals), "setActiveVessel",
                new[] { typeof(Vessel), typeof(bool), typeof(bool) });
        }

        static bool Prefix(Vessel v, bool force, ref bool __result)
        {
            if (ParsekGameModeGate.CheckInert("CameraDroneSwitchTokenPatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            bool targetIsShip = v != null && FarWatchPlayerVessel.IsPlayerShip(v.persistentId, v.id);
            bool droneLive = IsDroneLiveCore();
            if (!ShouldRefuseSwitch(CameraDroneIdentity.Exists, CameraDroneIdentity.StripPending,
                    CameraDroneIdentity.SwitchTokenHeld, targetIsShip, droneLive))
                return true;
            __result = false;
            // Any other switch (the map Switch To and alarm Jump end the watch before this,
            // in their own gates) is taken as "end the watch, then switch" through the Switch-To
            // gates (design-far-watch.md 8.5, ruled): never a bare switch after the hand-back.
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight != null && flight.FarWatch != null && flight.FarWatch.IsRunning)
                flight.FarWatch.RequestEndThenSwitch(v, force ? "ForceSetActiveVessel" : "SetActiveVessel",
                    target => Patches.MapFocusObjectOnSelectPatch.ReplayOwnedVesselSwitch(
                        target, ReFlySwitchGate.PathMapSwitchTo, "Switch"));
            ParsekLog.InfoRateLimited("FarWatch",
                "refused-switch-" + (v != null ? v.persistentId.ToString(CultureInfo.InvariantCulture) : "null"),
                string.Format(CultureInfo.InvariantCulture,
                    "refused {0} target={1} source=outside-far-watch while far watch",
                    force ? "ForceSetActiveVessel" : "SetActiveVessel", v != null ? v.persistentId : 0u),
                5.0);
            return false;
        }

        /// <summary>
        /// Pure: refuse a switch while a live far watch owns the drone, unless Parsek holds the
        /// token, or the target is the player ship and the drone is no longer live.
        /// </summary>
        internal static bool ShouldRefuseSwitch(
            bool droneExists, bool stripPending, bool tokenHeld, bool targetIsShip, bool droneLive)
        {
            if (!droneExists || stripPending || tokenHeld)
                return false;
            if (targetIsShip && !droneLive)
                return false;
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsDroneLiveCore()
        {
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return false;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel d = vessels[i];
                    if (d != null && CameraDrone.Is(d))
                        return d.state != Vessel.State.DEAD;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>
    /// Discovery None does not make the drone untargetable (proof: <c>SetVesselTarget(drone,
    /// overrideInputLock: true)</c> succeeded), so the drone is refused as a target (6.2).
    /// </summary>
    [HarmonyPatch(typeof(FlightGlobals), nameof(FlightGlobals.SetVesselTarget))]
    internal static class CameraDroneTargetRefusalPatch
    {
        static bool Prefix(ITargetable tgt)
        {
            if (ParsekGameModeGate.CheckInert("CameraDroneTargetRefusalPatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists || tgt == null)
                return true;
            Vessel v = null;
            try
            {
                v = tgt.GetVessel();
            }
            catch (Exception)
            {
                return true;
            }
            if (!CameraDrone.Is(v))
                return true;
            ParsekLog.InfoRateLimited("CameraDrone", "drone-target-refused",
                "refused SetVesselTarget for the far-watch camera drone pid=" +
                v.persistentId.ToString(CultureInfo.InvariantCulture), 5.0);
            return false;
        }
    }
}
