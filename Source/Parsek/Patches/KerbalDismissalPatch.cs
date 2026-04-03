using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents dismissal of Parsek-managed kerbals (reserved, stand-ins, retired)
    /// from the Astronaut Complex. Uses TargetMethod to resolve the ambiguous
    /// KerbalRoster.Remove overloads (same pattern as GhostVesselSwitchPatch).
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalDismissalPatch
    {
        static MethodBase TargetMethod()
        {
            var method = typeof(KerbalRoster).GetMethod(
                nameof(KerbalRoster.Remove),
                new[] { typeof(ProtoCrewMember) });

            if (method == null)
                ParsekLog.Warn("KerbalDismissal",
                    "KerbalRoster.Remove(ProtoCrewMember) not found — patch will not apply");

            return method;
        }

        static bool Prefix(ProtoCrewMember crew)
        {
            if (crew == null) return true;

            // Allow Parsek's own cleanup calls
            if (GameStateRecorder.SuppressCrewEvents) return true;
            if (GameStateRecorder.IsReplayingActions) return true;

            if (KerbalsModule.IsManaged(crew.name))
            {
                ParsekLog.Info("KerbalDismissal",
                    $"Blocked dismissal of '{crew.name}' — managed by Parsek");
                return false;
            }
            return true;
        }
    }
}
