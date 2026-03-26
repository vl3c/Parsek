using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents dismissal of Parsek-managed kerbals (reserved, stand-ins, retired)
    /// from the Astronaut Complex. Same pattern as TechResearchPatch.
    /// </summary>
    [HarmonyPatch(typeof(KerbalRoster), nameof(KerbalRoster.Remove))]
    internal static class KerbalDismissalPatch
    {
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
