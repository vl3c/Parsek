using HarmonyLib;
using KSP.UI.Dialogs;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Feeds <see cref="PostFlightDialogLog"/> the display and dismissal of stock
    /// <c>FlightResultsDialog</c>, which has no GameEvent of its own. Observation only:
    /// postfixes that log and never alter the stock result.
    ///
    /// Decompiled KSP 1.12.5 Assembly-CSharp.dll (load-bearing excerpt):
    /// <c>public static FlightResultsDialog Display(string outcomeMsg)</c> closes the pause
    /// menu, calls <c>FlightDriver.SetPause(true)</c>, instantiates the singleton when absent,
    /// sets <c>display = true</c> and stores a non-empty <c>outcomeMsg</c> in the private
    /// <c>titleMsg</c>. <c>public static void Close()</c> returns early when no instance
    /// exists, else sets <c>display = false</c>, calls <c>FlightDriver.SetPause(false)</c> and
    /// destroys the GameObject. <c>private void OnDestroy()</c> nulls the static instance.
    /// Stock <c>FlightLogger</c> opens the crash screen with <c>showExitControls = true</c>
    /// and the F3 flight status screen with <c>showExitControls = false</c>; every exit
    /// button handler calls <c>Close()</c>.
    /// </summary>
    internal static class FlightResultsDialogLogPatches
    {
        [HarmonyPatch(typeof(FlightResultsDialog), nameof(FlightResultsDialog.Display))]
        internal static class DisplayPatch
        {
            static void Postfix(string outcomeMsg, FlightResultsDialog __result)
            {
                string outcome = outcomeMsg;
                if (string.IsNullOrEmpty(outcome) && __result != null)
                    outcome = Traverse.Create(__result).Field("titleMsg").GetValue<string>();

                PostFlightDialogLog.NoteFlightResultsShown(
                    Time.realtimeSinceStartup,
                    HighLogic.LoadedScene.ToString(),
                    ActiveVesselName(),
                    FlightDriver.Pause,
                    FlightResultsDialog.showExitControls,
                    outcome);
            }
        }

        [HarmonyPatch(typeof(FlightResultsDialog), nameof(FlightResultsDialog.Close))]
        internal static class ClosePatch
        {
            static void Postfix()
            {
                PostFlightDialogLog.NoteFlightResultsDismissed(
                    Time.realtimeSinceStartup, "Close", FlightDriver.Pause);
            }
        }

        // Catches a dialog that went away without Close() (scene change). After a Close()
        // the tracker is already clear and this logs nothing.
        [HarmonyPatch(typeof(FlightResultsDialog), "OnDestroy")]
        internal static class OnDestroyPatch
        {
            static void Postfix()
            {
                PostFlightDialogLog.NoteFlightResultsDismissed(
                    Time.realtimeSinceStartup, "Destroyed", FlightDriver.Pause);
            }
        }

        // The root part is already gone when the crash screen opens, so the active vessel can
        // be null or a surviving fragment; the name is whatever stock reports at display time.
        private static string ActiveVesselName()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            return v != null ? v.vesselName : null;
        }
    }
}
