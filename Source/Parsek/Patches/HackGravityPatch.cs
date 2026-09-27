using HarmonyLib;
using KSP.UI.Screens.DebugToolbar.Screens.Cheats;

namespace Parsek.Patches
{
    /// <summary>
    /// Postfix on the stock debug-screen widget's <c>HackGravity.SetGravityFactor(double)</c>
    /// (protected virtual; every toggle, slider move and Reset of the Alt+F12 Hack Gravity
    /// cheat goes through it). Observation only: when the cheat is turned on or changed while
    /// a recording is live, the recorder writes its one gravity-hack Warn
    /// (<see cref="FlightRecorder.NoteGravityHack"/>). Stock behavior is untouched.
    /// </summary>
    [HarmonyPatch(typeof(HackGravity), "SetGravityFactor")]
    internal static class HackGravityPatch
    {
        static void Postfix(double factor)
        {
            if (ParsekGameModeGate.CheckInert("HackGravityPatch.Postfix")) return; // S9 game-mode gate
            FlightRecorder recorder = PhysicsFramePatch.ActiveRecorder;
            if (recorder == null)
            {
                if (GravityHackDetector.IsHackedFactor(factor))
                    ParsekLog.Verbose(GravityHackDetector.Tag,
                        "Gravity hack changed with no live recording: factor="
                        + factor.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            recorder.NoteGravityHack("cheat-toggled", factor);
        }
    }
}
