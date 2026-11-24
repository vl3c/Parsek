using System;
using HarmonyLib;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Automation only: the input half of the <c>EvaMove</c> seam verb (contract on
    /// <see cref="TestCommandEvaMove"/>). A Postfix on the stock
    /// <c>KerbalEVA.HandleMovementInput</c> that, while a drive is armed for this kerbal's
    /// part, writes the held direction into the same fields the stock movement keys write:
    /// <c>packTgtRPos</c> for the jetpack (what <c>EVA_Pack_Forward</c> and friends add) or
    /// <c>SetWaypoint</c> for the walk (what stock's own follow uses).
    ///
    /// <para>Decompiled (KSP 1.12.5): <c>KerbalEVA.FixedUpdate</c> zeroes <c>tgtRpos</c> and
    /// <c>packTgtRPos</c>, calls <c>HandleMovementInput()</c>, then runs the FSM, so a Postfix
    /// is the one place a write survives into the frame's physics. The Postfix runs even when
    /// the original returned early for a kerbal not under control; the drive is armed only
    /// for the active EVA kerbal, so that case does not arise in a lane.</para>
    ///
    /// <para>Inert in a player build: <see cref="Prepare"/> answers false unless the seam env
    /// var is the literal <c>1</c>, so Harmony patches nothing; armed, the Postfix is one
    /// static read per kerbal per physics frame while no drive is armed.</para>
    /// </summary>
    [HarmonyPatch(typeof(KerbalEVA), "HandleMovementInput")]
    internal static class EvaMoveInputPatch
    {
        private const string Tag = "TestCommands";

        // The one armed drive. Scoped to one EvaMove step: armed by its applier, disarmed by
        // its completion, its reset and the addon's two-phase clear.
        private static uint drivePartFlightId;
        private static bool driveIsJetpack;
        private static Vector3 driveWorldDir;
        private static int framesApplied;

        internal static bool IsArmed => drivePartFlightId != 0u;

        /// <summary>Physics frames the held input was written on since the arm.</summary>
        internal static int FramesApplied => framesApplied;

        internal static void Arm(uint partFlightId, bool jetpack, Vector3 worldDir)
        {
            drivePartFlightId = partFlightId;
            driveIsJetpack = jetpack;
            driveWorldDir = worldDir;
            framesApplied = 0;
        }

        internal static void Disarm()
        {
            drivePartFlightId = 0u;
            driveIsJetpack = false;
            driveWorldDir = Vector3.zero;
        }

        static bool Prepare()
        {
            return ParsekTestCommandAddon.IsArmed(
                Environment.GetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName));
        }

        static void Postfix(KerbalEVA __instance, ref Vector3 ___packTgtRPos)
        {
            if (drivePartFlightId == 0u || __instance == null || __instance.part == null) return;
            if (__instance.part.flightID != drivePartFlightId) return;
            try
            {
                if (driveIsJetpack)
                    ___packTgtRPos = driveWorldDir;
                else
                    __instance.SetWaypoint(__instance.transform.position + driveWorldDir);
                framesApplied++;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"evamove input write threw, drive disarmed: {ex.GetType().Name}: {ex.Message}");
                Disarm();
            }
        }
    }
}
