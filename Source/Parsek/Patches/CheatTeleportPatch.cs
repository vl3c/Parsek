using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Postfix on the protected <c>FlightGlobals.PostOrbitSet(CelestialBody oldBody)</c>.
    /// Every stock Alt+F12 teleport ends there (decompiled KSP 1.12.5):
    /// <c>SetShipOrbit</c> (Set Orbit), <c>SetShipOrbitRendezvous</c> (Rendezvous) and
    /// <c>SetVesselPosition</c> (Set Position; the middle-click cheat only fills Set
    /// Position's latitude / longitude fields and teleports through the same button). By the
    /// time the postfix runs the active vessel carries the new orbit, and a body change has
    /// already fired <c>onVesselSOIChanged</c>. The recorder side is
    /// <see cref="FlightRecorder.OnCheatTeleport"/>.
    /// </summary>
    [HarmonyPatch(typeof(FlightGlobals), "PostOrbitSet")]
    internal static class CheatTeleportPatch
    {
        static void Postfix(CelestialBody oldBody)
        {
            if (ParsekGameModeGate.CheckInert("CheatTeleportPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null && CameraDrone.Is(v))
                {
                    // The drone mover never calls SetShipOrbit; a teleport of the drone is
                    // overwritten by the next tick (design-far-watch.md 6.3).
                    ParsekLog.Info(CheatTeleportDecision.Tag,
                        "Cheat teleport of the far-watch camera drone ignored (the mover overwrites it next tick)");
                    return;
                }
                ParsekFlight flight = ParsekFlight.Instance;
                if (flight != null && flight.HandleCheatTeleport(v, oldBody))
                    return;
                ParsekLog.Info(CheatTeleportDecision.Tag, CheatTeleportDecision.FormatLogLine(
                    CheatTeleportAction.NoLiveRecording,
                    v?.vesselName,
                    v != null ? v.persistentId : 0u,
                    oldBody?.name,
                    v?.mainBody?.name,
                    Planetarium.GetUniversalTime()));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(CheatTeleportDecision.Tag,
                    "Cheat teleport handling failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
